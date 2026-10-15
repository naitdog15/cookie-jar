// InputSim.cs
//
// Shared "human-like" input layer used by the CASH OUT clicker, Teleport, the keep-alive and the diagnostics.
// Win32 window APIs and input simulation only. No injection, no process memory access. C# 5, x64.
//
// Why: Roblox ignores a click that arrives with no preceding mouse movement and no hold time (SetCursorPos then
// LEFTDOWN+LEFTUP in one SendInput call). A click here is therefore:
//   restore if minimised -> bring to front (verified, several fall-backs) -> SetCursorPos a few px beside the
//   target -> real MOVE|ABSOLUTE|VIRTUALDESK events in small steps (15-30 ms apart) -> hover pause -> LEFTDOWN ->
//   hold -> LEFTUP (separate calls) -> cursor read back and WindowFromPoint checked at every stage.
//
// Click methods (setting "Click method": Auto / SendInput / Legacy)
//   SendInput   : the sequence above through SendInput.
//   MouseEvent  : same sequence through the legacy mouse_event API.
//   PostMessage : WM_MOUSEMOVE / WM_LBUTTONDOWN / WM_LBUTTONUP posted straight to the window (no cursor movement).
//   Auto   = SendInput, then MouseEvent, then PostMessage, moving to the next one when the caller's verify
//            callback reports that nothing changed on screen. The method that last worked is tried first.
//   Legacy = MouseEvent, then PostMessage.
//
// Testing: every Win32 input call goes through IInputBackend (InputSim.Backend). Tests plug in a fake; the real
// backend is Win32Backend. All timings are in InputSim.Timing and can be changed.

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;

namespace RbxMulti
{
    public enum ClickMethod
    {
        Auto,
        SendInput,
        Legacy
    }

    public enum ClickTech
    {
        SendInput,
        MouseEvent,
        PostMessage
    }

    /// <summary>Low level input and focus operations. The real implementation is Win32Backend.</summary>
    internal interface IInputBackend
    {
        bool GetCursorPos(out int x, out int y);
        bool SetCursorPos(int x, int y);
        /// <summary>Absolute mouse move in screen pixels. Returns the number of events accepted (SendInput) or 1 (mouse_event).</summary>
        uint MoveAbs(int x, int y, bool legacy, out int lastError);
        uint Button(bool down, bool legacy, out int lastError);
        uint Key(ushort vk, bool down, out int lastError);
        /// <summary>Hardware scan-code key down / up (KEYEVENTF_SCANCODE). Roblox ignores plain virtual-key events.</summary>
        bool KeyDown(ushort scanCode);
        bool KeyUp(ushort scanCode);
        /// <summary>Right mouse button down / up (MOUSEEVENTF_RIGHTDOWN / RIGHTUP).</summary>
        bool RightDown();
        bool RightUp();
        /// <summary>Relative mouse move (MOUSEEVENTF_MOVE) by dx px, dy 0.</summary>
        bool MoveRelative(int dx);
        bool Post(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam);
        IntPtr WindowFromPoint(int x, int y);
        IntPtr Foreground();
        bool Focus(IntPtr hwnd, InputTrace trace);
        void Sleep(int ms);
    }

    /// <summary>Timestamped list of what happened, for logs and diagnostics reports.</summary>
    public sealed class InputTrace
    {
        private readonly List<string> _lines = new List<string>();
        private readonly Stopwatch _sw = Stopwatch.StartNew();

        public void Add(string line)
        {
            lock (_lines)
            {
                _lines.Add("+" + _sw.ElapsedMilliseconds.ToString(CultureInfo.InvariantCulture).PadLeft(5) + " ms  " + line);
            }
        }

        public string[] Lines()
        {
            lock (_lines) { return _lines.ToArray(); }
        }

        public override string ToString()
        {
            return string.Join(Environment.NewLine, Lines());
        }
    }

    public sealed class ClickAttempt
    {
        public ClickTech Tech;
        public bool Delivered;
        public bool Changed;
        public string Error;
        public string ErrorCode;
    }

    public sealed class ClickOptions
    {
        /// <summary>Called after a delivered click; true when the screen shows the expected change. Null = cannot verify.</summary>
        public Func<bool> Verify;
        /// <summary>Called before any input is sent; true aborts with "user became active".</summary>
        public Func<bool> AbortIf;
        public bool RestoreFocus = true;
        public bool RestoreCursor = true;
        public InputTrace Trace;
    }

    public sealed class ClickReport
    {
        public bool Delivered;        // input was accepted and the cursor/hit-test checks passed
        public bool Confirmed;        // Verify said the screen changed
        public bool VerifyRequested;
        public ClickTech TechUsed;
        public string Error;          // null when fine
        public string ErrorCode;      // window-gone, minimised, focus, covered, cursor, sendinput, user-active, outside, no-change
        public int ClientX, ClientY, ScreenX, ScreenY;
        public InputTrace Trace;
        public readonly List<ClickAttempt> Attempts = new List<ClickAttempt>();

        /// <summary>True when the click was delivered and (if a verify was supplied) it visibly did something.</summary>
        public bool Ok { get { return Delivered && (!VerifyRequested || Confirmed); } }

        public string AttemptsText()
        {
            StringBuilder sb = new StringBuilder();
            foreach (ClickAttempt a in Attempts)
            {
                if (sb.Length > 0) sb.Append("; ");
                sb.Append(a.Tech);
                sb.Append(a.Delivered ? (VerifyRequested ? (a.Changed ? " ok" : " sent, no change") : " sent") : " failed (" + a.Error + ")");
            }
            return sb.ToString();
        }
    }

    public static class InputSim
    {
        /// <summary>All timings in milliseconds / pixels. Static fields so tests and tuning can change them.</summary>
        public static class Timing
        {
            public static int ApproachDx = 14;       // SetCursorPos lands this far left of the target ...
            public static int ApproachDy = 6;        // ... and this far below it
            public static int MoveSteps = 4;         // interpolated real mouse-move events (3-6)
            public static int MoveStepMs = 20;       // between steps (15-30)
            public static int HoverMs = 120;         // hover pause before pressing
            public static int HoldMs = 80;           // LEFTDOWN to LEFTUP (60-100)
            public static int PostUpMs = 60;
            public static int FocusRetries = 3;
            public static int FocusSettleMs = 140;
            public static int RestoreWaitMs = 800;   // wait for a minimised window to come back
            public static int VerifySettleMs = 500;  // wait before the first verify call
            public static int CursorTolerancePx = 2;
            public static int WiggleDx = 9;          // keep-alive mouse move size
            public static int KeyTapMs = 60;
        }

        internal static IInputBackend Backend = new Win32Backend();

        /// <summary>GetLastError after the last refused scan-code key event (0 after a sent one). Read by the walk log.</summary>
        internal static int LastScanError;

        /// <summary>Global setting: Auto / SendInput / Legacy.</summary>
        public static ClickMethod Method = ClickMethod.Auto;

        private static ClickTech _lastGood = ClickTech.SendInput;
        private static readonly Dictionary<int, DateTime> _confirmed = new Dictionary<int, DateTime>();

        public const ushort VkRControl = 0xA3;

        // ------------------------------------------------------------------------------------
        // Ledger of confirmed input per process id
        // ------------------------------------------------------------------------------------

        public static void RecordConfirmed(int pid)
        {
            if (pid <= 0) return;
            lock (_confirmed) { _confirmed[pid] = DateTime.UtcNow; }
        }

        public static DateTime? LastConfirmedUtc(int pid)
        {
            lock (_confirmed)
            {
                DateTime d;
                if (_confirmed.TryGetValue(pid, out d)) return d;
            }
            return null;
        }

        public static string MethodLabel(ClickMethod m)
        {
            switch (m)
            {
                case ClickMethod.SendInput: return "SendInput";
                case ClickMethod.Legacy: return "Legacy";
                default: return "Auto";
            }
        }

        public static string TechLabel(ClickTech t)
        {
            switch (t)
            {
                case ClickTech.MouseEvent: return "mouse_event";
                case ClickTech.PostMessage: return "PostMessage";
                default: return "SendInput";
            }
        }

        /// <summary>Forgets which technique worked last (Auto starts with SendInput again). Tests and the Click method combo use it.</summary>
        public static void ResetLastGood() { _lastGood = ClickTech.SendInput; }

        public static ClickTech[] OrderFor(ClickMethod m)
        {
            switch (m)
            {
                case ClickMethod.SendInput:
                    return new ClickTech[] { ClickTech.SendInput };
                case ClickMethod.Legacy:
                    return new ClickTech[] { ClickTech.MouseEvent, ClickTech.PostMessage };
                default:
                    List<ClickTech> list = new List<ClickTech>();
                    list.Add(_lastGood);
                    foreach (ClickTech t in new ClickTech[] { ClickTech.SendInput, ClickTech.MouseEvent, ClickTech.PostMessage })
                    {
                        if (!list.Contains(t)) list.Add(t);
                    }
                    return list.ToArray();
            }
        }

        // ------------------------------------------------------------------------------------
        // Public click API
        // ------------------------------------------------------------------------------------

        /// <summary>Click a client-area point using the configured InputSim.Method.</summary>
        public static ClickReport ClickClient(IntPtr hwnd, int cx, int cy, ClickOptions opt)
        {
            return ClickClient(hwnd, cx, cy, OrderFor(Method), opt);
        }

        /// <summary>Click a client-area point trying exactly the given techniques in order.</summary>
        public static ClickReport ClickClient(IntPtr hwnd, int cx, int cy, ClickTech[] order, ClickOptions opt)
        {
            if (opt == null) opt = new ClickOptions();
            ClickReport rep = new ClickReport();
            rep.Trace = opt.Trace ?? new InputTrace();
            rep.VerifyRequested = opt.Verify != null;
            rep.ClientX = cx;
            rep.ClientY = cy;
            IInputBackend B = Backend;
            InputTrace t = rep.Trace;

            lock (AntiIdleClicker.ClickLock)
            {
                int sx0, sy0;
                B.GetCursorPos(out sx0, out sy0);
                IntPtr prevFg = B.Foreground();
                bool wasIconic = false;
                try
                {
                    if (!NativeWin.IsWindow(hwnd))
                    {
                        Fail(rep, "window-gone", "The game window no longer exists.");
                        return rep;
                    }

                    string focusErr;
                    bool focusOk = PrepareWindow(B, hwnd, t, out wasIconic, out focusErr);
                    if (NativeWin.IsIconic(hwnd))
                    {
                        Fail(rep, "minimised", "The game window is minimised and could not be restored.");
                        return rep;
                    }

                    NativeWin.POINT p = new NativeWin.POINT();
                    p.X = cx; p.Y = cy;
                    NativeWin.ClientToScreen(hwnd, ref p);
                    rep.ScreenX = p.X; rep.ScreenY = p.Y;
                    NativeWin.RECT wr;
                    if (NativeWin.GetWindowRect(hwnd, out wr) && (p.X < wr.Left || p.X >= wr.Right || p.Y < wr.Top || p.Y >= wr.Bottom))
                    {
                        Fail(rep, "outside", "The click point (" + p.X + "," + p.Y + ") is outside the game window.");
                        return rep;
                    }
                    t.Add("click target client(" + cx + "," + cy + ") = screen(" + p.X + "," + p.Y + "), order " + OrderText(order));

                    if (opt.AbortIf != null && opt.AbortIf())
                    {
                        Fail(rep, "user-active", "skipped: user became active");
                        return rep;
                    }

                    foreach (ClickTech tech in order)
                    {
                        ClickAttempt at = new ClickAttempt();
                        at.Tech = tech;
                        rep.Attempts.Add(at);
                        if (tech != ClickTech.PostMessage && !focusOk)
                        {
                            at.ErrorCode = "focus";
                            at.Error = "game window is not in the foreground (" + focusErr + ")";
                            t.Add(TechLabel(tech) + " skipped: " + at.Error);
                            continue;
                        }

                        string code, err;
                        bool ok;
                        if (tech == ClickTech.PostMessage) ok = RunPost(B, hwnd, cx, cy, p.X, p.Y, t, out code, out err);
                        else ok = RunMouse(B, hwnd, tech == ClickTech.MouseEvent, p.X, p.Y, opt.AbortIf, t, out code, out err);
                        at.Delivered = ok;
                        at.Error = err;
                        at.ErrorCode = code;
                        if (!ok)
                        {
                            t.Add(TechLabel(tech) + " failed: " + err);
                            if (code == "user-active") break;
                            continue;
                        }
                        rep.Delivered = true;
                        rep.TechUsed = tech;
                        if (opt.Verify == null) break;

                        B.Sleep(Timing.VerifySettleMs);
                        bool changed = false;
                        try { changed = opt.Verify(); } catch (Exception) { changed = false; }
                        at.Changed = changed;
                        t.Add(TechLabel(tech) + (changed ? ": screen changed (click registered)" : ": no visible change after the click"));
                        if (changed)
                        {
                            rep.Confirmed = true;
                            _lastGood = tech;
                            break;
                        }
                    }

                    if (rep.Error == null)
                    {
                        if (!rep.Delivered)
                        {
                            ClickAttempt last = rep.Attempts.Count > 0 ? rep.Attempts[rep.Attempts.Count - 1] : null;
                            rep.ErrorCode = last != null && last.ErrorCode != null ? last.ErrorCode : "sendinput";
                            rep.Error = "Click could not be sent: " + rep.AttemptsText();
                        }
                        else if (rep.VerifyRequested && !rep.Confirmed)
                        {
                            rep.ErrorCode = "no-change";
                            rep.Error = "Click did not register (" + rep.AttemptsText() + ").";
                        }
                    }
                    if (rep.Delivered && (rep.Confirmed || (!rep.VerifyRequested && rep.TechUsed != ClickTech.PostMessage)))
                    {
                        RecordConfirmed(NativeWin.PidOf(hwnd));
                    }
                    return rep;
                }
                catch (Exception ex)
                {
                    Fail(rep, "error", "Unexpected error while clicking: " + ex.GetType().Name + " " + ex.Message);
                    return rep;
                }
                finally
                {
                    Restore(B, hwnd, sx0, sy0, prevFg, wasIconic, opt.RestoreCursor, opt.RestoreFocus, t);
                }
            }
        }

        /// <summary>
        /// Keep-alive: brief focus, a small real mouse move over the window and back, optionally a harmless key tap,
        /// then cursor and focus are restored. Uses the configured Method order (key tap only through SendInput).
        /// </summary>
        public static ClickReport Wiggle(IntPtr hwnd, bool tapKey, Func<bool> abortIf, InputTrace trace)
        {
            ClickReport rep = new ClickReport();
            rep.Trace = trace ?? new InputTrace();
            InputTrace t = rep.Trace;
            IInputBackend B = Backend;
            lock (AntiIdleClicker.ClickLock)
            {
                int sx0, sy0;
                B.GetCursorPos(out sx0, out sy0);
                IntPtr prevFg = B.Foreground();
                bool wasIconic = false;
                try
                {
                    if (!NativeWin.IsWindow(hwnd)) { Fail(rep, "window-gone", "The game window no longer exists."); return rep; }
                    string focusErr;
                    bool focusOk = PrepareWindow(B, hwnd, t, out wasIconic, out focusErr);
                    if (NativeWin.IsIconic(hwnd)) { Fail(rep, "minimised", "The game window is minimised and could not be restored."); return rep; }

                    NativeWin.POINT c = new NativeWin.POINT();
                    NativeWin.RECT cr;
                    NativeWin.GetClientRect(hwnd, out cr);
                    c.X = (cr.Right - cr.Left) / 2;
                    c.Y = (cr.Bottom - cr.Top) / 2;
                    rep.ClientX = c.X; rep.ClientY = c.Y;
                    NativeWin.ClientToScreen(hwnd, ref c);
                    rep.ScreenX = c.X; rep.ScreenY = c.Y;

                    if (abortIf != null && abortIf()) { Fail(rep, "user-active", "skipped: user became active"); return rep; }

                    foreach (ClickTech tech in OrderFor(Method))
                    {
                        ClickAttempt at = new ClickAttempt();
                        at.Tech = tech;
                        rep.Attempts.Add(at);
                        string code, err;
                        bool ok;
                        if (tech == ClickTech.PostMessage)
                        {
                            ok = RunPostMoves(B, hwnd, rep.ClientX, rep.ClientY, c.X, c.Y, t, out code, out err);
                        }
                        else if (!focusOk)
                        {
                            ok = false; code = "focus"; err = "game window is not in the foreground (" + focusErr + ")";
                        }
                        else
                        {
                            ok = RunWiggleMouse(B, hwnd, tech == ClickTech.MouseEvent, c.X, c.Y, abortIf, t, out code, out err);
                        }
                        at.Delivered = ok; at.Error = err; at.ErrorCode = code;
                        if (!ok)
                        {
                            t.Add(TechLabel(tech) + " failed: " + err);
                            if (code == "user-active") break;
                            continue;
                        }
                        rep.Delivered = true;
                        rep.TechUsed = tech;
                        if (tapKey && tech != ClickTech.PostMessage)
                        {
                            int e1, e2;
                            uint n1 = B.Key(VkRControl, true, out e1);
                            B.Sleep(Timing.KeyTapMs);
                            uint n2 = B.Key(VkRControl, false, out e2);
                            t.Add("key tap RControl down=" + n1 + " (err " + e1 + ") up=" + n2 + " (err " + e2 + ")");
                        }
                        break;
                    }
                    if (!rep.Delivered && rep.Error == null)
                    {
                        ClickAttempt last = rep.Attempts.Count > 0 ? rep.Attempts[rep.Attempts.Count - 1] : null;
                        rep.ErrorCode = last != null && last.ErrorCode != null ? last.ErrorCode : "sendinput";
                        rep.Error = "Keep-alive input could not be sent: " + rep.AttemptsText();
                    }
                    rep.Confirmed = rep.Delivered && rep.TechUsed != ClickTech.PostMessage;
                    if (rep.Confirmed) RecordConfirmed(NativeWin.PidOf(hwnd));
                    return rep;
                }
                catch (Exception ex)
                {
                    Fail(rep, "error", "Unexpected error: " + ex.GetType().Name + " " + ex.Message);
                    return rep;
                }
                finally
                {
                    Restore(B, hwnd, sx0, sy0, prevFg, wasIconic, true, true, t);
                }
            }
        }

        // ------------------------------------------------------------------------------------
        // Focus
        // ------------------------------------------------------------------------------------

        /// <summary>Brings the window forward with retries and fall-backs. Returns true when it is verified to be in the foreground.</summary>
        internal static bool EnsureForeground(IntPtr hwnd, InputTrace trace)
        {
            return Backend.Focus(hwnd, trace ?? new InputTrace());
        }

        private static bool PrepareWindow(IInputBackend B, IntPtr hwnd, InputTrace t, out bool wasIconic, out string err)
        {
            err = null;
            wasIconic = NativeWin.IsIconic(hwnd);
            if (wasIconic)
            {
                NativeWin.ShowWindow(hwnd, NativeWin.SW_RESTORE);
                int waited = 0;
                while (NativeWin.IsIconic(hwnd) && waited < Timing.RestoreWaitMs)
                {
                    B.Sleep(50);
                    waited += 50;
                }
                t.Add("window was minimised, restored (still minimised: " + NativeWin.IsIconic(hwnd) + ")");
                if (NativeWin.IsIconic(hwnd)) { err = "could not restore from minimised"; return false; }
                B.Sleep(Timing.FocusSettleMs);
            }
            bool ok = B.Focus(hwnd, t);
            IntPtr fg = B.Foreground();
            t.Add("foreground after focus: " + NativeWin.DescribeWindow(fg) + (ok ? "  (game window is foreground)" : "  (NOT the game window)"));
            if (!ok) err = "foreground is " + NativeWin.ProcessNameOf(fg);
            return ok;
        }

        private static void Restore(IInputBackend B, IntPtr hwnd, int sx0, int sy0, IntPtr prevFg, bool wasIconic, bool cursor, bool focus, InputTrace t)
        {
            try
            {
                if (cursor) B.SetCursorPos(sx0, sy0);
                if (focus)
                {
                    if (wasIconic && NativeWin.IsWindow(hwnd))
                    {
                        NativeWin.ShowWindow(hwnd, NativeWin.SW_SHOWMINNOACTIVE);
                    }
                    if (prevFg != IntPtr.Zero && NativeWin.IsWindow(prevFg) && !NativeWin.SameApp(prevFg, hwnd))
                    {
                        B.Focus(prevFg, new InputTrace());
                    }
                }
            }
            catch (Exception ex)
            {
                t.Add("restore failed: " + ex.GetType().Name);
            }
        }

        // ------------------------------------------------------------------------------------
        // Techniques
        // ------------------------------------------------------------------------------------

        private static void Fail(ClickReport rep, string code, string message)
        {
            rep.ErrorCode = code;
            rep.Error = message;
            rep.Trace.Add("FAILED [" + code + "] " + message);
        }

        private static string OrderText(ClickTech[] order)
        {
            StringBuilder sb = new StringBuilder();
            foreach (ClickTech t in order) { if (sb.Length > 0) sb.Append(" > "); sb.Append(TechLabel(t)); }
            return sb.ToString();
        }

        private static bool Near(int ax, int ay, int bx, int by, int tol)
        {
            return Math.Abs(ax - bx) <= tol && Math.Abs(ay - by) <= tol;
        }

        // Cursor readback. Logs it and returns whether it is within tolerance of the expected point.
        private static bool Readback(IInputBackend B, InputTrace t, string what, int ex, int ey, int tol)
        {
            int x, y;
            bool got = B.GetCursorPos(out x, out y);
            bool near = got && Near(x, y, ex, ey, tol);
            t.Add("cursor readback after " + what + ": (" + x + "," + y + ") expected (" + ex + "," + ey + ")" + (near ? "" : "  MISMATCH"));
            return near;
        }

        private static bool CheckUnder(IInputBackend B, IntPtr hwnd, int sx, int sy, InputTrace t, out string code, out string err)
        {
            code = null; err = null;
            IntPtr under = B.WindowFromPoint(sx, sy);
            t.Add("WindowFromPoint(" + sx + "," + sy + ") = " + NativeWin.DescribeWindow(under));
            if (under == IntPtr.Zero)
            {
                code = "covered"; err = "Nothing was found under the click point.";
                return false;
            }
            if (NativeWin.PidOf(under) != NativeWin.PidOf(hwnd) && NativeWin.RootOf(under) != NativeWin.RootOf(hwnd))
            {
                code = "covered";
                err = "Another window (" + NativeWin.ProcessNameOf(under) + ") is on top of the click point, so the game would not receive it.";
                return false;
            }
            return true;
        }

        // Moves the cursor from the current approach point to (sx,sy) with real move events. Returns false with code/err on failure.
        private static bool MoveSteps(IInputBackend B, bool legacy, int fx, int fy, int sx, int sy, Func<bool> abortIf, InputTrace t, out string code, out string err)
        {
            code = null; err = null;
            string name = legacy ? "mouse_event" : "SendInput";
            int steps = Math.Max(1, Timing.MoveSteps);
            for (int i = 1; i <= steps; i++)
            {
                int x = fx + (int)Math.Round((sx - fx) * (double)i / steps);
                int y = fy + (int)Math.Round((sy - fy) * (double)i / steps);
                int le;
                uint n = B.MoveAbs(x, y, legacy, out le);
                t.Add(name + " MOVE|ABSOLUTE|VIRTUALDESK (" + x + "," + y + ") returned " + n + ", GetLastError " + le);
                if (n == 0)
                {
                    code = "sendinput";
                    err = name + " refused the mouse move (returned 0, GetLastError " + le + "). Input is blocked: UIPI (the game runs at a higher privilege level), a secure desktop or a locked screen.";
                    return false;
                }
                B.Sleep(Timing.MoveStepMs);
                if (!Readback(B, t, "move step " + i, x, y, Timing.CursorTolerancePx + 2))
                {
                    // One more try with SetCursorPos, in case something nudged the mouse.
                    B.SetCursorPos(x, y);
                    B.Sleep(15);
                    if (!Readback(B, t, "SetCursorPos fix-up", x, y, Timing.CursorTolerancePx + 2))
                    {
                        code = abortIf != null && abortIf() ? "user-active" : "cursor";
                        err = code == "user-active" ? "skipped: user became active"
                            : "The mouse cursor does not follow (locked screen, another program controls the mouse, or the game captured it).";
                        return false;
                    }
                }
            }
            return true;
        }

        private static bool RunMouse(IInputBackend B, IntPtr hwnd, bool legacy, int sx, int sy, Func<bool> abortIf, InputTrace t, out string code, out string err)
        {
            string name = legacy ? "mouse_event" : "SendInput";
            t.Add("--- " + name + " click at screen (" + sx + "," + sy + ") ---");
            NativeWin.RECT wr;
            int ax = sx - Timing.ApproachDx, ay = sy + Timing.ApproachDy;
            if (NativeWin.GetWindowRect(hwnd, out wr))
            {
                if (ax < wr.Left + 2) ax = sx + Timing.ApproachDx;
                if (ay > wr.Bottom - 2) ay = sy - Timing.ApproachDy;
            }
            B.SetCursorPos(ax, ay);
            B.Sleep(25);
            Readback(B, t, "SetCursorPos(approach)", ax, ay, Timing.CursorTolerancePx);

            if (!MoveSteps(B, legacy, ax, ay, sx, sy, abortIf, t, out code, out err)) return false;

            if (!Readback(B, t, "final move", sx, sy, Timing.CursorTolerancePx))
            {
                B.SetCursorPos(sx, sy);
                B.Sleep(20);
                if (!Readback(B, t, "SetCursorPos(target)", sx, sy, Timing.CursorTolerancePx))
                {
                    code = "cursor";
                    err = "The mouse cursor could not be placed on the click point (locked screen, or another program controls the mouse).";
                    return false;
                }
            }
            if (!CheckUnder(B, hwnd, sx, sy, t, out code, out err)) return false;

            B.Sleep(Timing.HoverMs);
            if (!CheckUnder(B, hwnd, sx, sy, t, out code, out err)) return false;

            int e1, e2;
            uint down = B.Button(true, legacy, out e1);
            t.Add(name + " LEFTDOWN returned " + down + ", GetLastError " + e1);
            if (down == 0)
            {
                code = "sendinput";
                err = name + " refused the button press (returned 0, GetLastError " + e1 + ").";
                return false;
            }
            B.Sleep(Timing.HoldMs);
            uint up = B.Button(false, legacy, out e2);
            t.Add(name + " LEFTUP returned " + up + ", GetLastError " + e2 + " (held " + Timing.HoldMs + " ms)");
            if (up == 0)
            {
                B.Button(false, legacy, out e2);   // never leave the button stuck down
                code = "sendinput";
                err = name + " refused the button release (returned 0, GetLastError " + e2 + ").";
                return false;
            }
            B.Sleep(Timing.PostUpMs);
            return true;
        }

        private static IntPtr TargetWindowAt(IntPtr hwnd, int sx, int sy, out int lx, out int ly)
        {
            NativeWin.POINT p = new NativeWin.POINT();
            p.X = sx; p.Y = sy;
            NativeWin.ScreenToClient(hwnd, ref p);
            IntPtr target = hwnd;
            try
            {
                IntPtr child = NativeWin.ChildWindowFromPointEx(hwnd, p, 1 | 4);
                if (child != IntPtr.Zero && child != hwnd)
                {
                    target = child;
                    NativeWin.POINT q = new NativeWin.POINT();
                    q.X = sx; q.Y = sy;
                    NativeWin.ScreenToClient(child, ref q);
                    p = q;
                }
            }
            catch (Exception) { }
            lx = p.X; ly = p.Y;
            return target;
        }

        private static IntPtr MakeLParam(int x, int y)
        {
            return new IntPtr(((y & 0xFFFF) << 16) | (x & 0xFFFF));
        }

        private static bool RunPost(IInputBackend B, IntPtr hwnd, int cx, int cy, int sx, int sy, InputTrace t, out string code, out string err)
        {
            t.Add("--- PostMessage click at client (" + cx + "," + cy + ") ---");
            if (!CheckUnder(B, hwnd, sx, sy, t, out code, out err)) return false;
            int lx, ly;
            IntPtr target = TargetWindowAt(hwnd, sx, sy, out lx, out ly);
            t.Add("posting to " + NativeWin.DescribeWindow(target) + " at (" + lx + "," + ly + ")");
            int steps = Math.Max(1, Timing.MoveSteps);
            for (int i = 1; i <= steps; i++)
            {
                int x = lx - Timing.ApproachDx + (int)Math.Round(Timing.ApproachDx * (double)i / steps);
                int y = ly + Timing.ApproachDy - (int)Math.Round(Timing.ApproachDy * (double)i / steps);
                if (!B.Post(target, 0x0200, IntPtr.Zero, MakeLParam(x, y))) { code = "sendinput"; err = "PostMessage WM_MOUSEMOVE failed."; return false; }
                B.Sleep(Timing.MoveStepMs);
            }
            B.Sleep(Timing.HoverMs);
            bool d = B.Post(target, 0x0201, new IntPtr(1), MakeLParam(lx, ly));
            B.Sleep(Timing.HoldMs);
            bool u = B.Post(target, 0x0202, IntPtr.Zero, MakeLParam(lx, ly));
            t.Add("PostMessage WM_LBUTTONDOWN=" + d + " WM_LBUTTONUP=" + u);
            if (!d || !u) { code = "sendinput"; err = "PostMessage was rejected by the window."; return false; }
            B.Sleep(Timing.PostUpMs);
            return true;
        }

        private static bool RunPostMoves(IInputBackend B, IntPtr hwnd, int cx, int cy, int sx, int sy, InputTrace t, out string code, out string err)
        {
            t.Add("--- PostMessage mouse move at client (" + cx + "," + cy + ") ---");
            if (!CheckUnder(B, hwnd, sx, sy, t, out code, out err)) return false;
            int lx, ly;
            IntPtr target = TargetWindowAt(hwnd, sx, sy, out lx, out ly);
            int[] dx = new int[] { 0, Timing.WiggleDx, 0 };
            foreach (int d in dx)
            {
                if (!B.Post(target, 0x0200, IntPtr.Zero, MakeLParam(lx + d, ly))) { code = "sendinput"; err = "PostMessage WM_MOUSEMOVE failed."; return false; }
                B.Sleep(Timing.MoveStepMs * 2);
            }
            return true;
        }

        private static bool RunWiggleMouse(IInputBackend B, IntPtr hwnd, bool legacy, int sx, int sy, Func<bool> abortIf, InputTrace t, out string code, out string err)
        {
            string name = legacy ? "mouse_event" : "SendInput";
            t.Add("--- " + name + " mouse wiggle around screen (" + sx + "," + sy + ") ---");
            int ax = sx - Timing.WiggleDx, ay = sy;
            B.SetCursorPos(ax, ay);
            B.Sleep(25);
            Readback(B, t, "SetCursorPos(start)", ax, ay, Timing.CursorTolerancePx);
            if (!CheckUnder(B, hwnd, sx, sy, t, out code, out err)) return false;
            if (!MoveSteps(B, legacy, ax, ay, sx + Timing.WiggleDx, sy, abortIf, t, out code, out err)) return false;
            B.Sleep(Timing.HoverMs / 2);
            if (!MoveSteps(B, legacy, sx + Timing.WiggleDx, sy, sx, sy, abortIf, t, out code, out err)) return false;
            return true;
        }

        /// <summary>
        /// Screen pixel -> SendInput absolute coordinates (0..65535 over the VIRTUAL screen, MOUSEEVENTF_VIRTUALDESK).
        /// vx,vy,vw,vh = SM_X/YVIRTUALSCREEN and SM_CX/CYVIRTUALSCREEN in physical pixels (the app is PerMonitorV2 aware).
        /// </summary>
        public static void NormalizeAbs(int x, int y, int vx, int vy, int vw, int vh, out int nx, out int ny)
        {
            if (vw < 2) vw = 2;
            if (vh < 2) vh = 2;
            nx = (int)Math.Round((x - vx) * 65535.0 / (vw - 1));
            ny = (int)Math.Round((y - vy) * 65535.0 / (vh - 1));
            if (nx < 0) nx = 0;
            if (nx > 65535) nx = 65535;
            if (ny < 0) ny = 0;
            if (ny > 65535) ny = 65535;
        }

        // ------------------------------------------------------------------------------------
        // Win32 backend
        // ------------------------------------------------------------------------------------

        internal sealed class Win32Backend : IInputBackend
        {
            public bool GetCursorPos(out int x, out int y)
            {
                NativeWin.POINT p;
                bool ok = NativeWin.GetCursorPos(out p);
                x = p.X; y = p.Y;
                return ok;
            }

            public bool SetCursorPos(int x, int y)
            {
                return NativeWin.SetCursorPos(x, y);
            }

            private static void Normalize(int x, int y, out int nx, out int ny)
            {
                NormalizeAbs(x, y, NativeWin.GetSystemMetrics(76), NativeWin.GetSystemMetrics(77),
                    NativeWin.GetSystemMetrics(78), NativeWin.GetSystemMetrics(79), out nx, out ny);
            }

            public uint MoveAbs(int x, int y, bool legacy, out int lastError)
            {
                int nx, ny;
                Normalize(x, y, out nx, out ny);
                const uint flags = NativeWin.MOUSEEVENTF_MOVE | NativeWin.MOUSEEVENTF_ABSOLUTE | NativeWin.MOUSEEVENTF_VIRTUALDESK;
                if (legacy)
                {
                    NativeWin.mouse_event(flags, nx, ny, 0, IntPtr.Zero);
                    lastError = 0;
                    return 1;
                }
                NativeWin.INPUT[] i = new NativeWin.INPUT[1];
                i[0].type = 0;
                i[0].mi.dx = nx;
                i[0].mi.dy = ny;
                i[0].mi.dwFlags = flags;
                uint n = NativeWin.SendInput(1, i, NativeWin.InputSize);
                lastError = n == 0 ? Marshal.GetLastWin32Error() : 0;
                return n;
            }

            public uint Button(bool down, bool legacy, out int lastError)
            {
                uint flag = down ? NativeWin.MOUSEEVENTF_LEFTDOWN : NativeWin.MOUSEEVENTF_LEFTUP;
                if (legacy)
                {
                    NativeWin.mouse_event(flag, 0, 0, 0, IntPtr.Zero);
                    lastError = 0;
                    return 1;
                }
                NativeWin.INPUT[] i = new NativeWin.INPUT[1];
                i[0].type = 0;
                i[0].mi.dwFlags = flag;
                uint n = NativeWin.SendInput(1, i, NativeWin.InputSize);
                lastError = n == 0 ? Marshal.GetLastWin32Error() : 0;
                return n;
            }

            public uint Key(ushort vk, bool down, out int lastError)
            {
                NativeWin.INPUT[] i = new NativeWin.INPUT[1];
                i[0].type = 1;
                i[0].ki.wVk = vk;
                i[0].ki.wScan = (ushort)NativeWin.MapVirtualKey(vk, 0);
                i[0].ki.dwFlags = down ? 0u : 2u;   // KEYEVENTF_KEYUP
                uint n = NativeWin.SendInput(1, i, NativeWin.InputSize);
                lastError = n == 0 ? Marshal.GetLastWin32Error() : 0;
                return n;
            }

            public bool KeyDown(ushort scanCode) { return SendScan(scanCode, false); }
            public bool KeyUp(ushort scanCode) { return SendScan(scanCode, true); }

            private static bool SendScan(ushort scanCode, bool up)
            {
                NativeWin.INPUT[] i = new NativeWin.INPUT[1];
                i[0].type = 1;                       // INPUT_KEYBOARD
                i[0].ki.wVk = 0;                     // scan code only
                i[0].ki.wScan = scanCode;
                i[0].ki.dwFlags = NativeWin.KEYEVENTF_SCANCODE | (up ? NativeWin.KEYEVENTF_KEYUP : 0u);
                uint n = NativeWin.SendInput(1, i, NativeWin.InputSize);
                LastScanError = n == 1 ? 0 : Marshal.GetLastWin32Error();
                return n == 1;
            }

            public bool RightDown() { return SendMouse(NativeWin.MOUSEEVENTF_RIGHTDOWN, 0); }
            public bool RightUp() { return SendMouse(NativeWin.MOUSEEVENTF_RIGHTUP, 0); }
            public bool MoveRelative(int dx) { return SendMouse(NativeWin.MOUSEEVENTF_MOVE, dx); }

            private static bool SendMouse(uint flags, int dx)
            {
                NativeWin.INPUT[] i = new NativeWin.INPUT[1];
                i[0].type = 0;                       // INPUT_MOUSE
                i[0].mi.dx = dx;
                i[0].mi.dy = 0;
                i[0].mi.dwFlags = flags;
                return NativeWin.SendInput(1, i, NativeWin.InputSize) == 1;
            }

            public bool Post(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam)
            {
                return NativeWin.PostMessage(hwnd, msg, wParam, lParam);
            }

            public IntPtr WindowFromPoint(int x, int y)
            {
                NativeWin.POINT p = new NativeWin.POINT();
                p.X = x; p.Y = y;
                return NativeWin.WindowFromPoint(p);
            }

            public IntPtr Foreground()
            {
                return NativeWin.GetForegroundWindow();
            }

            public void Sleep(int ms)
            {
                if (ms > 0) Thread.Sleep(ms);
            }

            public bool Focus(IntPtr hwnd, InputTrace t)
            {
                for (int attempt = 1; attempt <= Math.Max(1, Timing.FocusRetries); attempt++)
                {
                    if (NativeWin.SameApp(NativeWin.GetForegroundWindow(), hwnd))
                    {
                        if (attempt > 1) t.Add("focus ok after attempt " + (attempt - 1));
                        return true;
                    }
                    if (!NativeWin.IsWindow(hwnd)) return false;
                    if (NativeWin.IsIconic(hwnd)) { NativeWin.ShowWindow(hwnd, NativeWin.SW_RESTORE); Thread.Sleep(150); }

                    if (attempt == 1)
                    {
                        t.Add("focus attempt 1: AttachThreadInput + SetForegroundWindow");
                        AttachAndSet(hwnd);
                    }
                    else if (attempt == 2)
                    {
                        t.Add("focus attempt 2: ALT key trick + SwitchToThisWindow");
                        NativeWin.keybd_event(0x12, 0, 0, IntPtr.Zero);
                        NativeWin.keybd_event(0x12, 0, 2, IntPtr.Zero);
                        AttachAndSet(hwnd);
                        try { NativeWin.SwitchToThisWindow(hwnd, true); } catch (Exception) { }
                    }
                    else
                    {
                        t.Add("focus attempt " + attempt + ": minimise and restore");
                        NativeWin.ShowWindow(hwnd, NativeWin.SW_MINIMIZE);
                        Thread.Sleep(180);
                        NativeWin.ShowWindow(hwnd, NativeWin.SW_RESTORE);
                        Thread.Sleep(220);
                        AttachAndSet(hwnd);
                    }
                    Thread.Sleep(Timing.FocusSettleMs);
                }
                bool ok = NativeWin.SameApp(NativeWin.GetForegroundWindow(), hwnd);
                t.Add(ok ? "focus ok" : "focus FAILED after " + Timing.FocusRetries + " attempts");
                return ok;
            }

            private static void AttachAndSet(IntPtr hwnd)
            {
                IntPtr fg = NativeWin.GetForegroundWindow();
                uint fgThread = 0;
                if (fg != IntPtr.Zero)
                {
                    uint ignored;
                    fgThread = NativeWin.GetWindowThreadProcessId(fg, out ignored);
                }
                uint cur = NativeWin.GetCurrentThreadId();
                bool attached = false;
                if (fgThread != 0 && fgThread != cur)
                {
                    attached = NativeWin.AttachThreadInput(cur, fgThread, true);
                }
                try
                {
                    NativeWin.AllowSetForegroundWindow(-1);
                    NativeWin.BringWindowToTop(hwnd);
                    NativeWin.SetForegroundWindow(hwnd);
                }
                finally
                {
                    if (attached) NativeWin.AttachThreadInput(cur, fgThread, false);
                }
            }
        }

        // ------------------------------------------------------------------------------------
        // Native helpers (also used by the diagnostics)
        // ------------------------------------------------------------------------------------

        internal static class NativeWin
        {
            public const uint MOUSEEVENTF_MOVE = 0x0001;
            public const uint MOUSEEVENTF_LEFTDOWN = 0x0002;
            public const uint MOUSEEVENTF_LEFTUP = 0x0004;
            public const uint MOUSEEVENTF_RIGHTDOWN = 0x0008;
            public const uint MOUSEEVENTF_RIGHTUP = 0x0010;
            public const uint MOUSEEVENTF_VIRTUALDESK = 0x4000;
            public const uint KEYEVENTF_KEYUP = 0x0002;
            public const uint KEYEVENTF_SCANCODE = 0x0008;
            public const uint MOUSEEVENTF_ABSOLUTE = 0x8000;
            public const int SW_MINIMIZE = 6;
            public const int SW_SHOWMINNOACTIVE = 7;
            public const int SW_RESTORE = 9;

            [StructLayout(LayoutKind.Sequential)]
            public struct POINT { public int X; public int Y; }

            [StructLayout(LayoutKind.Sequential)]
            public struct RECT { public int Left; public int Top; public int Right; public int Bottom; }

            [StructLayout(LayoutKind.Sequential)]
            public struct MOUSEINPUT
            {
                public int dx;
                public int dy;
                public uint mouseData;
                public uint dwFlags;
                public uint time;
                public IntPtr dwExtraInfo;
            }

            [StructLayout(LayoutKind.Sequential)]
            public struct KEYBDINPUT
            {
                public ushort wVk;
                public ushort wScan;
                public uint dwFlags;
                public uint time;
                public IntPtr dwExtraInfo;
            }

            // x64 layout: type at 0, union at 8, total 40 bytes (the build targets x64 only).
            [StructLayout(LayoutKind.Explicit, Size = 40)]
            public struct INPUT
            {
                [FieldOffset(0)] public uint type;
                [FieldOffset(8)] public MOUSEINPUT mi;
                [FieldOffset(8)] public KEYBDINPUT ki;
            }

            public static int InputSize { get { return Marshal.SizeOf(typeof(INPUT)); } }

            [DllImport("user32.dll")] public static extern bool IsWindow(IntPtr h);
            [DllImport("user32.dll")] public static extern bool IsWindowVisible(IntPtr h);
            [DllImport("user32.dll")] public static extern bool IsIconic(IntPtr h);
            [DllImport("user32.dll")] public static extern bool ShowWindow(IntPtr h, int cmd);
            [DllImport("user32.dll")] public static extern IntPtr GetForegroundWindow();
            [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr h);
            [DllImport("user32.dll")] public static extern bool BringWindowToTop(IntPtr h);
            [DllImport("user32.dll")] public static extern void SwitchToThisWindow(IntPtr h, bool altTab);
            [DllImport("user32.dll")] public static extern bool AllowSetForegroundWindow(int pid);
            [DllImport("user32.dll")] public static extern bool AttachThreadInput(uint a, uint b, bool attach);
            [DllImport("kernel32.dll")] public static extern uint GetCurrentThreadId();
            [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
            [DllImport("user32.dll")] public static extern bool GetCursorPos(out POINT p);
            [DllImport("user32.dll")] public static extern bool SetCursorPos(int x, int y);
            [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr h, out RECT r);
            [DllImport("user32.dll")] public static extern bool GetClientRect(IntPtr h, out RECT r);
            [DllImport("user32.dll")] public static extern bool ClientToScreen(IntPtr h, ref POINT p);
            [DllImport("user32.dll")] public static extern bool ScreenToClient(IntPtr h, ref POINT p);
            [DllImport("user32.dll")] public static extern IntPtr WindowFromPoint(POINT p);
            [DllImport("user32.dll")] public static extern IntPtr ChildWindowFromPointEx(IntPtr h, POINT p, uint flags);
            [DllImport("user32.dll")] public static extern IntPtr GetAncestor(IntPtr h, uint flags);
            [DllImport("user32.dll")] public static extern int GetSystemMetrics(int index);
            [DllImport("user32.dll")] public static extern bool PostMessage(IntPtr h, uint msg, IntPtr w, IntPtr l);
            [DllImport("user32.dll")] public static extern void mouse_event(uint flags, int dx, int dy, uint data, IntPtr extra);
            [DllImport("user32.dll")] public static extern void keybd_event(byte vk, byte scan, uint flags, IntPtr extra);
            [DllImport("user32.dll")] public static extern uint MapVirtualKey(uint code, uint mapType);
            [DllImport("user32.dll", SetLastError = true)] public static extern uint SendInput(uint n, INPUT[] inputs, int size);
            [DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern int GetClassName(IntPtr h, StringBuilder sb, int max);

            public static IntPtr RootOf(IntPtr h)
            {
                if (h == IntPtr.Zero) return IntPtr.Zero;
                IntPtr r = GetAncestor(h, 2);
                return r == IntPtr.Zero ? h : r;
            }

            public static int PidOf(IntPtr h)
            {
                if (h == IntPtr.Zero) return 0;
                uint pid;
                GetWindowThreadProcessId(h, out pid);
                return (int)pid;
            }

            /// <summary>True when both windows are the same top-level window or belong to the same process.</summary>
            public static bool SameApp(IntPtr a, IntPtr b)
            {
                if (a == IntPtr.Zero || b == IntPtr.Zero) return false;
                if (a == b || RootOf(a) == RootOf(b)) return true;
                int pa = PidOf(a);
                return pa != 0 && pa == PidOf(b);
            }

            public static string ProcessNameOf(IntPtr h)
            {
                int pid = PidOf(h);
                if (pid <= 0) return "unknown";
                try
                {
                    using (Process p = Process.GetProcessById(pid)) { return p.ProcessName; }
                }
                catch (Exception) { return "pid " + pid; }
            }

            public static string ClassOf(IntPtr h)
            {
                StringBuilder sb = new StringBuilder(256);
                if (h != IntPtr.Zero) GetClassName(h, sb, sb.Capacity);
                return sb.ToString();
            }

            /// <summary>hwnd, class, process name and pid. Never the window title (can contain personal data).</summary>
            public static string DescribeWindow(IntPtr h)
            {
                if (h == IntPtr.Zero) return "none (0)";
                return "0x" + h.ToInt64().ToString("X") + " class=" + ClassOf(h) + " process=" + ProcessNameOf(h) + " pid=" + PidOf(h);
            }
        }
    }
}
