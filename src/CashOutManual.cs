// CashOutManual.cs
//
// The "Cash out" button of an instance card: ONE manual CASH OUT attempt through the same code path as the auto-clicker
// (CashOutDetector.Find + AntiIdleClicker.CashOutClickPoint + InputSim.ClickClient with its method ladder, ClickLock, focus
// and cursor restore). C# 5, screen capture and SendInput only.
//
// PUBLIC API (namespace RbxMulti)
//   enum CashOutKind { Success, Unconfirmed, Failed }
//   class CashOutResult { Kind, Message, Method, ClickPoint, Folder, Millis }
//   class CashOutEvent  one history row (time, kind, message, method, point, manual)
//   static CashOutHistory   last 50 events per instance id (memory only): Add / Get / Clear
//   static ManualCashOut
//       string BlockReason(RobloxInstance, resolve)   null when allowed, else the tooltip text.
//       CashOutResult Run(inst, clicker, resolve, ct, log)   blocking; call from a worker thread. One at a time with Teleport.
//       CashOutResult Classify(ClickReport, bool verifyUnknown)   pure mapping, exposed for tests.
//
// On success a small evidence record is written: diag\cashout_<timestamp>_<user>\ report.txt + before.png + after.png
// (click point drawn); only the newest 20 such folders are kept.

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Globalization;
using System.IO;
using System.Text;
using System.Threading;

namespace RbxMulti
{
    public enum CashOutKind
    {
        Success,
        Unconfirmed,
        Failed
    }

    public sealed class CashOutResult
    {
        public CashOutKind Kind;
        public string Message;
        public string Method;
        public Point? ClickPoint;
        public string Folder;
        public long Millis;

        public CashOutResult(CashOutKind kind, string message)
        {
            Kind = kind;
            Message = message;
        }

        public string Chip
        {
            get
            {
                if (Kind == CashOutKind.Success) return "Cashed out";
                if (Kind == CashOutKind.Unconfirmed) return "Clicked - unconfirmed";
                return string.IsNullOrEmpty(Message) ? "Cash out failed" : Message;
            }
        }

        public override string ToString() { return Kind + ": " + Message; }
    }

    public sealed class CashOutEvent
    {
        public DateTime Time;
        public CashOutKind Kind;
        public string Message;
        public string Method;
        public Point? ClickPoint;
        public bool Manual;

        public string Line()
        {
            string res = Kind == CashOutKind.Success ? "cashed out" : Kind == CashOutKind.Unconfirmed ? "unconfirmed" : "failed";
            return Time.ToString("HH:mm:ss", CultureInfo.InvariantCulture) + "  " + (Manual ? "manual" : "auto") + "  " + res
                + (string.IsNullOrEmpty(Method) ? "" : "  via " + Method)
                + (ClickPoint.HasValue ? "  at " + ClickPoint.Value.X + "," + ClickPoint.Value.Y : "")
                + (Kind == CashOutKind.Success || string.IsNullOrEmpty(Message) ? "" : "  - " + Message);
        }
    }

    public static class CashOutHistory
    {
        public const int Max = 50;
        private static readonly object Gate = new object();
        private static readonly Dictionary<int, List<CashOutEvent>> Map = new Dictionary<int, List<CashOutEvent>>();

        public static void Add(int instanceId, CashOutEvent e)
        {
            lock (Gate)
            {
                List<CashOutEvent> l;
                if (!Map.TryGetValue(instanceId, out l)) { l = new List<CashOutEvent>(); Map[instanceId] = l; }
                l.Add(e);
                while (l.Count > Max) l.RemoveAt(0);
            }
        }

        /// <summary>Newest first.</summary>
        public static List<CashOutEvent> Get(int instanceId)
        {
            lock (Gate)
            {
                List<CashOutEvent> l;
                List<CashOutEvent> copy = new List<CashOutEvent>();
                if (Map.TryGetValue(instanceId, out l)) { for (int i = l.Count - 1; i >= 0; i--) copy.Add(l[i]); }
                return copy;
            }
        }

        public static void Clear(int instanceId)
        {
            lock (Gate) { Map.Remove(instanceId); }
        }
    }

    public static class ManualCashOut
    {
        public const int KeepEvidenceFolders = 20;

        public static string BlockReason(RobloxInstance inst, Func<RobloxInstance, IntPtr> resolve, bool checkWindow)
        {
            if (inst == null) return "No instance.";
            if (inst.StatusKind != "Running")
                return "Start this instance and wait for the green Running status first (status: " + (string.IsNullOrEmpty(inst.Status) ? "unknown" : inst.Status) + ").";
            if (checkWindow)
            {
                IntPtr h = IntPtr.Zero;
                try { h = resolve == null ? AntiIdleClicker.FindMainWindow(inst.Pid) : resolve(inst); } catch (Exception) { }
                if (h == IntPtr.Zero) return "This instance has no visible game window yet.";
            }
            return null;
        }

        /// <summary>Pure mapping of a click report to the user-facing result. verifyUnknown = the re-capture failed.</summary>
        public static CashOutResult Classify(ClickReport rep, bool verifyUnknown)
        {
            if (rep == null) return new CashOutResult(CashOutKind.Failed, "Could not click CASH OUT.");
            string tech = InputSim.TechLabel(rep.TechUsed);
            if (rep.Delivered && rep.Confirmed)
            {
                if (verifyUnknown)
                {
                    CashOutResult u = new CashOutResult(CashOutKind.Unconfirmed, "Clicked, but the window could not be captured afterwards, so the result is unconfirmed.");
                    u.Method = tech;
                    return u;
                }
                CashOutResult ok = new CashOutResult(CashOutKind.Success, "CASH OUT clicked and the button is gone.");
                ok.Method = tech;
                return ok;
            }
            string msg;
            if (rep.Delivered)
            {
                msg = "Click did not register: the CASH OUT button is still there after the click (" + rep.AttemptsText() + ").";
                return new CashOutResult(CashOutKind.Failed, msg);
            }
            switch (rep.ErrorCode)
            {
                case "covered": msg = "The game window is covered by another window, so CASH OUT was not clicked. " + rep.Error; break;
                case "focus": msg = "Windows would not bring the game window to the front, so CASH OUT was not clicked. " + rep.Error; break;
                case "cursor": msg = "The mouse cursor could not be moved onto CASH OUT (locked screen, or the mouse was moving)."; break;
                case "minimised": msg = "The game window is minimised and could not be restored."; break;
                case "sendinput": msg = "Windows rejected the mouse input. " + rep.Error; break;
                case "outside": msg = "The CASH OUT click point is outside the game window."; break;
                case "window-gone": msg = "The game window is gone."; break;
                default: msg = "Could not click CASH OUT: " + rep.Error; break;
            }
            return new CashOutResult(CashOutKind.Failed, msg);
        }

        public static CashOutResult Run(RobloxInstance inst, AntiIdleClicker clicker, Func<RobloxInstance, IntPtr> resolve,
            CancellationToken ct, Action<string> log)
        {
            Stopwatch total = Stopwatch.StartNew();
            string tag = "[CashOut] " + (inst == null ? "?" : "#" + inst.Id.ToString(CultureInfo.InvariantCulture) + " " + (string.IsNullOrEmpty(inst.AccountName) ? inst.Name : inst.AccountName));
            Action<string> say = delegate(string s) { if (log != null) { try { log(tag + " " + s); } catch (Exception) { } } };
            CashOutResult res;
            try { res = RunCore(inst, clicker, resolve, ct, say); }
            catch (OperationCanceledException) { res = new CashOutResult(CashOutKind.Failed, "Cancelled."); }
            catch (Exception ex) { res = new CashOutResult(CashOutKind.Failed, "Unexpected error: " + ex.GetType().Name); }
            res.Millis = total.ElapsedMilliseconds;
            if (inst != null)
            {
                CashOutEvent e = new CashOutEvent();
                e.Time = DateTime.Now; e.Kind = res.Kind; e.Message = res.Message; e.Method = res.Method; e.ClickPoint = res.ClickPoint; e.Manual = true;
                CashOutHistory.Add(inst.Id, e);
                if (clicker != null)
                {
                    try { clicker.RecordManualClick(inst.Id, res.Method, res.Kind == CashOutKind.Success, res.Kind == CashOutKind.Failed ? res.Message : null); } catch (Exception) { }
                }
            }
            say("manual attempt: " + res.Kind + " - " + res.Message + (string.IsNullOrEmpty(res.Method) ? "" : " (via " + res.Method + ")")
                + (res.ClickPoint.HasValue ? " at " + res.ClickPoint.Value.X + "," + res.ClickPoint.Value.Y : "") + " in " + res.Millis + " ms"
                + (string.IsNullOrEmpty(res.Folder) ? "" : ", evidence " + res.Folder));
            return res;
        }

        private static CashOutResult RunCore(RobloxInstance inst, AntiIdleClicker clicker, Func<RobloxInstance, IntPtr> resolve,
            CancellationToken ct, Action<string> say)
        {
            string block = BlockReason(inst, resolve, false);
            if (block != null) return new CashOutResult(CashOutKind.Failed, block);
            if (!TeleportService.TryAcquireExclusive())
                return new CashOutResult(CashOutKind.Failed, "A teleport or another CASH OUT test is already running (one at a time). Wait for it to finish.");
            try
            {
                IntPtr hwnd = resolve == null ? AntiIdleClicker.FindMainWindow(inst.Pid) : resolve(inst);
                if (hwnd == IntPtr.Zero || !InputSim.NativeWin.IsWindow(hwnd))
                    return new CashOutResult(CashOutKind.Failed, "No visible game window for this instance.");

                bool locked = false;
                Stopwatch wait = Stopwatch.StartNew();
                while (!locked)
                {
                    if (ct.IsCancellationRequested) throw new OperationCanceledException();
                    locked = Monitor.TryEnter(AntiIdleClicker.ClickLock, 100);
                    if (!locked && wait.ElapsedMilliseconds > 20000)
                        return new CashOutResult(CashOutKind.Failed, "Could not get the mouse: the auto-clicker was busy for 20 s.");
                }
                try
                {
                    return Attempt(inst, clicker, hwnd, say);
                }
                finally
                {
                    Monitor.Exit(AntiIdleClicker.ClickLock);
                }
            }
            finally
            {
                TeleportService.ReleaseExclusive();
            }
        }

        private static CashOutResult Attempt(RobloxInstance inst, AntiIdleClicker clicker, IntPtr hwnd, Action<string> say)
        {
            Stopwatch sw = Stopwatch.StartNew();
            if (InputSim.NativeWin.IsIconic(hwnd))
            {
                InputSim.NativeWin.ShowWindow(hwnd, InputSim.NativeWin.SW_RESTORE);
                Thread.Sleep(500);
            }
            string cm;
            Bitmap before = CashOutDetector.CaptureWindow(hwnd, out cm);
            if (before == null) return new CashOutResult(CashOutKind.Failed, "Could not capture the game window (minimised or hidden).");
            long tCapture = sw.ElapsedMilliseconds;
            Rectangle? found;
            try { found = CashOutDetector.Find(before); }
            catch (Exception) { before.Dispose(); throw; }
            long tDetect = sw.ElapsedMilliseconds;
            if (!found.HasValue)
            {
                before.Dispose();
                say("CASH OUT button not visible (capture " + before_size(cm) + ")");
                return new CashOutResult(CashOutKind.Failed, "CASH OUT button not visible in this window.");
            }
            Rectangle btn = found.Value;
            Point cp = AntiIdleClicker.CashOutClickPoint(btn);
            say("button found at " + btn.X + "," + btn.Y + " " + btn.Width + "x" + btn.Height + ", clicking its centre " + cp.X + "," + cp.Y);

            bool verifyUnknown = false;
            ClickOptions opt = new ClickOptions();
            opt.Verify = delegate
            {
                bool unk;
                bool gone = AntiIdleClicker.CashOutGone(hwnd, btn, out unk);
                if (unk) verifyUnknown = true;
                return gone;
            };
            ClickReport rep = InputSim.ClickClient(hwnd, cp.X, cp.Y, opt);
            long tClick = sw.ElapsedMilliseconds;
            string cm2;
            Bitmap after = CashOutDetector.CaptureWindow(hwnd, out cm2);
            CashOutResult res = Classify(rep, verifyUnknown);
            res.ClickPoint = new Point(rep.ClientX != 0 || rep.ClientY != 0 ? rep.ClientX : cp.X, rep.ClientX != 0 || rep.ClientY != 0 ? rep.ClientY : cp.Y);
            if (string.IsNullOrEmpty(res.Method) && rep.Delivered) res.Method = InputSim.TechLabel(rep.TechUsed);
            try
            {
                if (res.Kind == CashOutKind.Success)
                {
                    res.Folder = WriteEvidence(inst, res, rep, before, after, btn, cm, tCapture, tDetect, tClick, sw.ElapsedMilliseconds);
                }
                else if (res.Kind == CashOutKind.Failed && rep.Delivered)
                {
                    string f = InputDiagnostics.WriteFailureEvidence("cashout", "manual CASH OUT: " + res.Message, hwnd, rep, btn);
                    if (f != null) res.Folder = f;
                }
            }
            finally
            {
                before.Dispose();
                if (after != null) after.Dispose();
            }
            return res;
        }

        private static string before_size(string cm) { return cm; }

        private static string WriteEvidence(RobloxInstance inst, CashOutResult res, ClickReport rep, Bitmap before, Bitmap after, Rectangle btn,
            string captureMethod, long tCapture, long tDetect, long tClick, long tTotal)
        {
            try
            {
                string user = Safe(string.IsNullOrEmpty(inst.AccountName) ? inst.Name : inst.AccountName);
                string folder = Path.Combine(InputDiagnostics.Root(), "cashout_" + DateTime.Now.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture) + "_" + user);
                int n = 1;
                string baseFolder = folder;
                while (Directory.Exists(folder)) folder = baseFolder + "_" + (++n);
                Directory.CreateDirectory(folder);
                StringBuilder sb = new StringBuilder();
                sb.AppendLine("Cookie Jar - manual CASH OUT (card button)");
                sb.AppendLine("Time: " + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture));
                sb.AppendLine("Instance: #" + inst.Id + " " + user + " (PID " + inst.Pid + ")");
                sb.AppendLine("Verdict: " + res.Kind + " - " + res.Message);
                sb.AppendLine("Click method used: " + res.Method + " (setting: " + InputSim.MethodLabel(InputSim.Method) + ")");
                sb.AppendLine("Attempts: " + rep.AttemptsText());
                sb.AppendLine("Button rect (client px): " + btn.X + "," + btn.Y + " " + btn.Width + "x" + btn.Height);
                sb.AppendLine("Click point (client px): " + res.ClickPoint.Value.X + "," + res.ClickPoint.Value.Y + "  screen: " + rep.ScreenX + "," + rep.ScreenY);
                sb.AppendLine("Capture method: " + captureMethod + ", before " + before.Width + "x" + before.Height);
                sb.AppendLine("Timings: capture " + tCapture + " ms, detect +" + (tDetect - tCapture) + " ms, click+verify +" + (tClick - tDetect) + " ms, total " + tTotal + " ms");
                sb.AppendLine("Before/after verdict: button " + (res.Kind == CashOutKind.Success ? "visible before, gone or moved after the click" : "see after.png"));
                File.WriteAllText(Path.Combine(folder, "report.txt"), sb.ToString(), Encoding.UTF8);
                using (Bitmap a = InputDiagnostics.Annotate(before, res.ClickPoint, btn, "before: CASH OUT found, click point"))
                    InputDiagnostics.SavePng(a, Path.Combine(folder, "before.png"));
                if (after != null)
                {
                    using (Bitmap a2 = InputDiagnostics.Annotate(after, res.ClickPoint, null, "after: click point (" + res.Kind + ")"))
                        InputDiagnostics.SavePng(a2, Path.Combine(folder, "after.png"));
                }
                InputDiagnostics.Prune("cashout_", KeepEvidenceFolders);
                return folder;
            }
            catch (Exception)
            {
                return null;
            }
        }

        private static string Safe(string s)
        {
            StringBuilder sb = new StringBuilder();
            foreach (char c in s ?? "") { if (char.IsLetterOrDigit(c) || c == '_' || c == '-') sb.Append(c); }
            return sb.Length == 0 ? "inst" : sb.ToString();
        }
    }
}
