// InputDiagnostics.cs
//
// Evidence for "the click does not register in Roblox".
//   InputDiagnostics.Run(label, hwnd, progress)          the "Diagnose input" button: records the environment, tries ONE
//       click on the FRIENDS button with each click method in turn (harmless sidebar toggle, undone afterwards),
//       captures before/after PNGs with the click point drawn on them and writes report.txt.
//   InputDiagnostics.WriteFailureEvidence(kind, ...)     the same kind of evidence (report + failure screenshot) for every
//       teleport / CASH OUT click failure. Rate limited, old folders are pruned.
// Output: %LocalAppData%\RobloxMultiLauncher\diag\<timestamp>\ (report.txt + PNGs). No tokens, cookies or window titles
// are written (only process names, window classes, sizes and coordinates). C# 5.

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;

namespace RbxMulti
{
    public sealed class DiagResult
    {
        public string Folder;
        public string Summary;
        public bool ClickWorked;
        public string WorkingMethod;
    }

    public static class InputDiagnostics
    {
        private static readonly object _evLock = new object();
        private static readonly Dictionary<string, DateTime> _lastEvidence = new Dictionary<string, DateTime>();
        private static readonly Dictionary<string, string> _lastFolder = new Dictionary<string, string>();
        private const int EvidenceMinGapSeconds = 20;
        private const int KeepFailureFolders = 20;
        private const int KeepRunFolders = 10;

        /// <summary>Test hook: overrides the output root.</summary>
        internal static string RootOverride;

        public static string Root()
        {
            if (!string.IsNullOrEmpty(RootOverride)) return RootOverride;
            return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "RobloxMultiLauncher", "diag");
        }

        // ------------------------------------------------------------------------------------
        // Environment description
        // ------------------------------------------------------------------------------------

        [DllImport("kernel32.dll")] private static extern IntPtr OpenProcess(uint access, bool inherit, int pid);
        [DllImport("kernel32.dll")] private static extern bool CloseHandle(IntPtr h);
        [DllImport("advapi32.dll", SetLastError = true)] private static extern bool OpenProcessToken(IntPtr proc, uint access, out IntPtr token);
        [DllImport("advapi32.dll", SetLastError = true)] private static extern bool GetTokenInformation(IntPtr token, int cls, out int info, int len, out int ret);
        [DllImport("user32.dll")] private static extern uint GetDpiForWindow(IntPtr h);
        [DllImport("user32.dll")] private static extern uint GetDpiForSystem();
        [DllImport("user32.dll")] private static extern IntPtr MonitorFromWindow(IntPtr h, uint flags);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern bool GetMonitorInfo(IntPtr mon, ref MONITORINFOEX info);
        [DllImport("shcore.dll")] private static extern int GetDpiForMonitor(IntPtr mon, int type, out uint x, out uint y);
        [DllImport("user32.dll")] private static extern int GetWindowLong(IntPtr h, int index);

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct MONITORINFOEX
        {
            public int cbSize;
            public InputSim.NativeWin.RECT rcMonitor;
            public InputSim.NativeWin.RECT rcWork;
            public uint dwFlags;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)]
            public string szDevice;
        }

        /// <summary>"elevated", "not elevated" or "unknown (...)" for a process.</summary>
        public static string ElevationOf(int pid)
        {
            IntPtr proc = IntPtr.Zero, tok = IntPtr.Zero;
            try
            {
                proc = OpenProcess(0x1000, false, pid);   // PROCESS_QUERY_LIMITED_INFORMATION
                if (proc == IntPtr.Zero) return "unknown (OpenProcess error " + Marshal.GetLastWin32Error() + ")";
                if (!OpenProcessToken(proc, 0x0008, out tok)) return "unknown (OpenProcessToken error " + Marshal.GetLastWin32Error() + ")";
                int elevated, ret;
                if (!GetTokenInformation(tok, 20, out elevated, 4, out ret)) return "unknown (GetTokenInformation error " + Marshal.GetLastWin32Error() + ")";
                return elevated != 0 ? "elevated (administrator)" : "not elevated";
            }
            catch (Exception ex) { return "unknown (" + ex.GetType().Name + ")"; }
            finally
            {
                if (tok != IntPtr.Zero) CloseHandle(tok);
                if (proc != IntPtr.Zero) CloseHandle(proc);
            }
        }

        public static string DescribeEnvironment(IntPtr hwnd)
        {
            StringBuilder sb = new StringBuilder();
            int me = Process.GetCurrentProcess().Id;
            sb.AppendLine("Launcher: pid " + me + ", " + (IntPtr.Size == 8 ? "64-bit" : "32-bit") + ", " + ElevationOf(me));
            sb.AppendLine("OS: " + Environment.OSVersion.VersionString);
            try { sb.AppendLine("System DPI: " + GetDpiForSystem() + " (" + (GetDpiForSystem() * 100 / 96) + "%)"); } catch (Exception) { sb.AppendLine("System DPI: unavailable"); }
            int vx = InputSim.NativeWin.GetSystemMetrics(76), vy = InputSim.NativeWin.GetSystemMetrics(77);
            int vw = InputSim.NativeWin.GetSystemMetrics(78), vh = InputSim.NativeWin.GetSystemMetrics(79);
            sb.AppendLine("Virtual screen (physical px): origin " + vx + "," + vy + " size " + vw + "x" + vh);
            sb.AppendLine("Monitors: " + InputSim.NativeWin.GetSystemMetrics(80));
            int cx, cy;
            InputSim.Backend.GetCursorPos(out cx, out cy);
            sb.AppendLine("Cursor: " + cx + "," + cy);
            sb.AppendLine("Foreground window: " + InputSim.NativeWin.DescribeWindow(InputSim.NativeWin.GetForegroundWindow()));
            sb.AppendLine("Click method setting: " + InputSim.MethodLabel(InputSim.Method));
            if (hwnd == IntPtr.Zero || !InputSim.NativeWin.IsWindow(hwnd))
            {
                sb.AppendLine("Game window: none");
                return sb.ToString();
            }
            int pid = InputSim.NativeWin.PidOf(hwnd);
            sb.AppendLine("Game window: " + InputSim.NativeWin.DescribeWindow(hwnd));
            string elev = ElevationOf(pid);
            sb.AppendLine("Game process: " + elev + (elev.StartsWith("elevated") || elev.StartsWith("not") ? "" : ""));
            bool meElev = ElevationOf(me).StartsWith("elevated");
            if (elev.StartsWith("elevated") && !meElev) sb.AppendLine("  WARNING: the game is elevated but the launcher is not. Windows blocks input into it (UIPI).");
            InputSim.NativeWin.RECT wr, cr;
            InputSim.NativeWin.GetWindowRect(hwnd, out wr);
            InputSim.NativeWin.GetClientRect(hwnd, out cr);
            InputSim.NativeWin.POINT o = new InputSim.NativeWin.POINT();
            InputSim.NativeWin.ClientToScreen(hwnd, ref o);
            sb.AppendLine("Window rect (screen px): " + wr.Left + "," + wr.Top + " - " + wr.Right + "," + wr.Bottom + "  size " + (wr.Right - wr.Left) + "x" + (wr.Bottom - wr.Top));
            sb.AppendLine("Client rect: size " + (cr.Right - cr.Left) + "x" + (cr.Bottom - cr.Top) + ", client origin on screen " + o.X + "," + o.Y);
            sb.AppendLine("Visible: " + InputSim.NativeWin.IsWindowVisible(hwnd) + ", minimised: " + InputSim.NativeWin.IsIconic(hwnd));
            try { uint d = GetDpiForWindow(hwnd); sb.AppendLine("Window DPI: " + d + " (scale " + (d * 100 / 96) + "%)"); } catch (Exception) { sb.AppendLine("Window DPI: unavailable"); }
            try
            {
                int ex = GetWindowLong(hwnd, -20);
                sb.AppendLine("Extended style: 0x" + ex.ToString("X8") + (((ex & 0x8) != 0) ? " TOPMOST" : "") + (((ex & 0x80000) != 0) ? " LAYERED" : "") + (((ex & 0x20) != 0) ? " TRANSPARENT" : "") + (((ex & 0x08000000) != 0) ? " NOACTIVATE" : ""));
            }
            catch (Exception) { }
            try
            {
                IntPtr mon = MonitorFromWindow(hwnd, 2);
                MONITORINFOEX mi = new MONITORINFOEX();
                mi.cbSize = Marshal.SizeOf(typeof(MONITORINFOEX));
                if (GetMonitorInfo(mon, ref mi))
                {
                    uint dx = 0, dy = 0;
                    try { GetDpiForMonitor(mon, 0, out dx, out dy); } catch (Exception) { }
                    sb.AppendLine("Monitor: " + mi.szDevice + (((mi.dwFlags & 1) != 0) ? " (primary)" : "") + " rect " + mi.rcMonitor.Left + "," + mi.rcMonitor.Top + " - " + mi.rcMonitor.Right + "," + mi.rcMonitor.Bottom
                        + ", work area " + mi.rcWork.Left + "," + mi.rcWork.Top + " - " + mi.rcWork.Right + "," + mi.rcWork.Bottom + ", DPI " + dx + " (scale " + (dx * 100 / 96) + "%)");
                }
            }
            catch (Exception) { sb.AppendLine("Monitor: unavailable"); }
            return sb.ToString();
        }

        // ------------------------------------------------------------------------------------
        // Drawing / saving
        // ------------------------------------------------------------------------------------

        public static Bitmap Annotate(Bitmap src, Point? clientPoint, Rectangle? rect, string caption)
        {
            Bitmap b = new Bitmap(src.Width, src.Height, PixelFormat.Format32bppArgb);
            using (Graphics g = Graphics.FromImage(b))
            {
                g.DrawImage(src, 0, 0, src.Width, src.Height);
                if (rect.HasValue)
                {
                    using (Pen p = new Pen(Color.Yellow, 2)) g.DrawRectangle(p, rect.Value);
                }
                if (clientPoint.HasValue)
                {
                    Point c = clientPoint.Value;
                    using (Pen p = new Pen(Color.Red, 3))
                    {
                        g.DrawEllipse(p, c.X - 14, c.Y - 14, 28, 28);
                        g.DrawLine(p, c.X - 24, c.Y, c.X + 24, c.Y);
                        g.DrawLine(p, c.X, c.Y - 24, c.X, c.Y + 24);
                    }
                }
                if (!string.IsNullOrEmpty(caption))
                {
                    using (Font f = new Font("Segoe UI", 11, FontStyle.Bold))
                    {
                        SizeF sz = g.MeasureString(caption, f);
                        g.FillRectangle(Brushes.Black, 4, 4, sz.Width + 8, sz.Height + 4);
                        g.DrawString(caption, f, Brushes.White, 8, 6);
                    }
                }
            }
            return b;
        }

        internal static void SavePng(Bitmap bmp, string path)
        {
            try { bmp.Save(path, ImageFormat.Png); } catch (Exception) { }
        }

        // ------------------------------------------------------------------------------------
        // Failure evidence
        // ------------------------------------------------------------------------------------

        /// <summary>
        /// Saves report.txt + failure.png for a failed click/teleport. Returns the folder or null (rate limited or error).
        /// rep may be null; target is the detected button rectangle in client pixels, if any.
        /// </summary>
        public static string WriteFailureEvidence(string kind, string message, IntPtr hwnd, ClickReport rep, Rectangle? target)
        {
            try
            {
                lock (_evLock)
                {
                    DateTime last;
                    if (_lastEvidence.TryGetValue(kind, out last) && (DateTime.UtcNow - last).TotalSeconds < EvidenceMinGapSeconds)
                    {
                        string again; _lastFolder.TryGetValue(kind, out again);   // rate limited: point at the folder written a moment ago
                        return again;
                    }
                    _lastEvidence[kind] = DateTime.UtcNow;
                }
                string folder = Path.Combine(Root(), "fail_" + DateTime.Now.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture) + "_" + kind);
                Directory.CreateDirectory(folder);
                StringBuilder sb = new StringBuilder();
                sb.AppendLine("Failure evidence: " + kind);
                sb.AppendLine("Time: " + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture));
                sb.AppendLine("Message: " + message);
                sb.AppendLine();
                sb.Append(DescribeEnvironment(hwnd));
                if (target.HasValue) sb.AppendLine("Detected target rectangle (client px): " + target.Value.X + "," + target.Value.Y + " " + target.Value.Width + "x" + target.Value.Height);
                if (rep != null)
                {
                    sb.AppendLine();
                    sb.AppendLine("Click: client (" + rep.ClientX + "," + rep.ClientY + ") -> screen (" + rep.ScreenX + "," + rep.ScreenY + ")");
                    sb.AppendLine("Delivered: " + rep.Delivered + ", confirmed by screen change: " + rep.Confirmed + ", error code: " + rep.ErrorCode);
                    sb.AppendLine("Attempts: " + rep.AttemptsText());
                    sb.AppendLine("Error: " + rep.Error);
                    sb.AppendLine();
                    sb.AppendLine("Trace:");
                    sb.AppendLine(rep.Trace.ToString());
                }
                File.WriteAllText(Path.Combine(folder, "report.txt"), sb.ToString(), Encoding.UTF8);

                string method;
                Bitmap frame = CashOutDetector.CaptureWindow(hwnd, out method);
                if (frame != null)
                {
                    Point? pt = null;
                    if (rep != null && (rep.ClientX != 0 || rep.ClientY != 0)) pt = new Point(rep.ClientX, rep.ClientY);
                    SavePng(frame, Path.Combine(folder, "capture.png"));   // raw frame: usable as a detector test fixture
                    using (frame)
                    using (Bitmap ann = Annotate(frame, pt, target, kind + ": " + message))
                    {
                        SavePng(ann, Path.Combine(folder, "failure.png"));
                    }
                }
                Prune("fail_", KeepFailureFolders);
                lock (_evLock) { _lastFolder[kind] = folder; }
                return folder;
            }
            catch (Exception)
            {
                return null;
            }
        }

        // ------------------------------------------------------------------------------------
        // Panel evidence (every teleport failure kind)
        // ------------------------------------------------------------------------------------

        /// <summary>Annotated copy of a capture: panel rect, close X, every row (rect, button, name crop), the OCR text and the click point.</summary>
        public static Bitmap AnnotatePanel(Bitmap src, FriendsPanelInfo panel, Point? clientPoint, string caption)
        {
            Bitmap b = new Bitmap(src.Width, src.Height, PixelFormat.Format32bppArgb);
            using (Graphics g = Graphics.FromImage(b))
            {
                g.DrawImage(src, 0, 0, src.Width, src.Height);
                if (panel != null)
                {
                    using (Pen p = new Pen(Color.Lime, 3)) g.DrawRectangle(p, panel.Bounds);
                    if (panel.HasCloseButton) using (Pen p = new Pen(Color.Red, 3)) g.DrawRectangle(p, panel.CloseButton);
                    using (Font f = new Font("Segoe UI", 10, FontStyle.Bold))
                    {
                        foreach (PanelRow r in panel.Rows)
                        {
                            using (Pen p = new Pen(Color.Orange, 1)) g.DrawRectangle(p, r.RowRect);
                            using (Pen p = new Pen(Color.Yellow, 2)) g.DrawRectangle(p, r.NameRect);
                            using (Pen p = new Pen(r.Online ? Color.Cyan : Color.Magenta, 2)) g.DrawRectangle(p, r.ButtonRect);
                            string t = "#" + (r.Index + 1) + " '" + (string.IsNullOrEmpty(r.Name) ? "?" : r.Name) + "' " + (r.Online ? "Teleport" : "Offline");
                            SizeF sz = g.MeasureString(t, f);
                            float ty = Math.Max(0, r.NameRect.Top - sz.Height + 2);
                            g.FillRectangle(Brushes.Black, r.NameRect.Left, ty, sz.Width + 4, sz.Height);
                            g.DrawString(t, f, Brushes.White, r.NameRect.Left + 2, ty);
                        }
                    }
                }
                if (clientPoint.HasValue)
                {
                    Point c = clientPoint.Value;
                    using (Pen p = new Pen(Color.Red, 3))
                    {
                        g.DrawEllipse(p, c.X - 14, c.Y - 14, 28, 28);
                        g.DrawLine(p, c.X - 24, c.Y, c.X + 24, c.Y);
                        g.DrawLine(p, c.X, c.Y - 24, c.X, c.Y + 24);
                    }
                }
                if (!string.IsNullOrEmpty(caption))
                {
                    using (Font f = new Font("Segoe UI", 11, FontStyle.Bold))
                    {
                        SizeF sz = g.MeasureString(caption, f, Math.Max(100, src.Width - 16));
                        g.FillRectangle(Brushes.Black, 4, 4, sz.Width + 8, sz.Height + 4);
                        g.DrawString(caption, f, Brushes.White, new RectangleF(8, 6, Math.Max(100, src.Width - 16), sz.Height + 4));
                    }
                }
            }
            return b;
        }

        /// <summary>Plain-text description of a detected panel and its rows (for report.txt).</summary>
        public static string DescribePanel(FriendsPanelInfo panel)
        {
            StringBuilder sb = new StringBuilder();
            if (panel == null) { sb.AppendLine("Panel: not detected"); return sb.ToString(); }
            sb.AppendLine("Panel rect (client px): " + panel.Bounds.X + "," + panel.Bounds.Y + " " + panel.Bounds.Width + "x" + panel.Bounds.Height
                + ", close X: " + (panel.HasCloseButton ? panel.CloseButton.X + "," + panel.CloseButton.Y + " " + panel.CloseButton.Width + "x" + panel.CloseButton.Height : "not found"));
            sb.AppendLine("Rows detected: " + panel.Rows.Count);
            foreach (PanelRow r in panel.Rows)
            {
                sb.AppendLine("  row " + (r.Index + 1) + ": " + (r.Online ? "green Teleport button" : "grey Offline button")
                    + ", button " + r.ButtonRect.X + "," + r.ButtonRect.Y + " " + r.ButtonRect.Width + "x" + r.ButtonRect.Height
                    + ", name area " + r.NameRect.X + "," + r.NameRect.Y + " " + r.NameRect.Width + "x" + r.NameRect.Height
                    + ", OCR '" + (r.RawName ?? "") + "' -> '" + (r.Name ?? "") + "'"
                    + (r.Candidates.Count > 1 ? ", other readings: " + string.Join(" | ", r.Candidates.ToArray()) : ""));
            }
            return sb.ToString();
        }

        /// <summary>
        /// Evidence for a teleport failure where the FRIENDS panel was visible: report.txt (summary, what was read, per-row
        /// details, environment, optional click trace), capture.png (raw frame, usable as a test fixture) and failure.png
        /// (annotated). Returns the folder or null (rate limited / error). frame is NOT disposed.
        /// </summary>
        public static string WriteTeleportEvidence(string kind, string message, IntPtr hwnd, Bitmap frame, FriendsPanelInfo panel,
            string username, Point? click, ClickReport rep)
        {
            try
            {
                lock (_evLock)
                {
                    DateTime last;
                    if (_lastEvidence.TryGetValue(kind, out last) && (DateTime.UtcNow - last).TotalSeconds < EvidenceMinGapSeconds)
                    {
                        string again; _lastFolder.TryGetValue(kind, out again);   // rate limited: point at the folder written a moment ago
                        return again;
                    }
                    _lastEvidence[kind] = DateTime.UtcNow;
                }
                string folder = Path.Combine(Root(), "fail_" + DateTime.Now.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture) + "_" + kind);
                Directory.CreateDirectory(folder);
                StringBuilder sb = new StringBuilder();
                sb.AppendLine("Failure evidence: " + kind);
                sb.AppendLine("Time: " + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture));
                sb.AppendLine("Message: " + message);
                sb.AppendLine("Target username: " + (username ?? "?"));
                if (frame != null) sb.AppendLine("Capture size: " + frame.Width + "x" + frame.Height);
                if (click.HasValue) sb.AppendLine("Click point (client px): " + click.Value.X + "," + click.Value.Y);
                sb.AppendLine("OCR: " + (TeleportOcr.IsAvailable ? "available (" + TeleportOcr.Language + ")" : "NOT available (" + TeleportOcr.LastError + ")"));
                sb.AppendLine("Names read: " + TeleportDetector.DescribeNames(panel));
                sb.AppendLine();
                sb.Append(DescribePanel(panel));
                sb.AppendLine();
                sb.Append(DescribeEnvironment(hwnd));
                if (rep != null)
                {
                    sb.AppendLine();
                    sb.AppendLine("Click: client (" + rep.ClientX + "," + rep.ClientY + ") -> screen (" + rep.ScreenX + "," + rep.ScreenY + ")");
                    sb.AppendLine("Delivered: " + rep.Delivered + ", confirmed: " + rep.Confirmed + ", error code: " + rep.ErrorCode);
                    sb.AppendLine("Attempts: " + rep.AttemptsText());
                    sb.AppendLine();
                    sb.AppendLine("Trace:");
                    sb.AppendLine(rep.Trace == null ? "" : rep.Trace.ToString());
                }
                File.WriteAllText(Path.Combine(folder, "report.txt"), sb.ToString(), Encoding.UTF8);
                if (frame != null)
                {
                    SavePng(frame, Path.Combine(folder, "capture.png"));
                    using (Bitmap ann = AnnotatePanel(frame, panel, click, kind + ": " + message))
                    {
                        SavePng(ann, Path.Combine(folder, "failure.png"));
                    }
                }
                Prune("fail_", KeepFailureFolders);
                lock (_evLock) { _lastFolder[kind] = folder; }
                return folder;
            }
            catch (Exception)
            {
                return null;
            }
        }

        internal static void Prune(string prefix, int keep)
        {
            try
            {
                string[] dirs = Directory.GetDirectories(Root(), prefix + "*");
                Array.Sort(dirs, StringComparer.Ordinal);
                for (int i = 0; i < dirs.Length - keep; i++)
                {
                    try { Directory.Delete(dirs[i], true); } catch (Exception) { }
                }
            }
            catch (Exception) { }
        }

        // ------------------------------------------------------------------------------------
        // Diagnose input
        // ------------------------------------------------------------------------------------

        private static string Pct(int v, int total)
        {
            return total <= 0 ? "?" : (v * 100 / total).ToString(CultureInfo.InvariantCulture) + "%";
        }

        /// <summary>
        /// Runs the diagnostics against one game window. Blocking (about 30-60 s); call from a worker thread.
        /// Sends real input to that window only: mouse moves and ONE FRIENDS-button click per method (undone afterwards).
        /// </summary>
        public static DiagResult Run(string label, IntPtr hwnd, Action<string> progress)
        {
            return Run(label, hwnd, progress, null);
        }

        /// <summary>
        /// Creates diag\&lt;timestamp&gt;\ with a first report.txt right away. The UI calls this the moment the button is pressed
        /// (even for refusals), so every Diagnose press leaves a folder behind. Returns the folder or null when it cannot be written.
        /// </summary>
        public static string Begin(string label, string note)
        {
            try
            {
                string stamp = DateTime.Now.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture);
                string folder = Path.Combine(Root(), stamp);
                int n = 1;
                while (Directory.Exists(folder)) { folder = Path.Combine(Root(), stamp + "_" + (++n)); }
                Directory.CreateDirectory(folder);
                File.WriteAllText(Path.Combine(folder, "report.txt"),
                    "Cookie Jar - input diagnostics (started)" + Environment.NewLine
                    + "Time: " + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture) + Environment.NewLine
                    + "Instance: " + label + Environment.NewLine
                    + (string.IsNullOrEmpty(note) ? "Status: running (this file is replaced by the full report when the run ends)" : "Status: " + note) + Environment.NewLine, Encoding.UTF8);
                return folder;
            }
            catch (Exception) { return null; }
        }

        /// <summary>Overwrites the stub report of a Diagnose press that did not run (refusal or error).</summary>
        public static void WriteStatus(string folder, string label, string status)
        {
            if (string.IsNullOrEmpty(folder)) return;
            try
            {
                File.WriteAllText(Path.Combine(folder, "report.txt"),
                    "Cookie Jar - input diagnostics" + Environment.NewLine
                    + "Time: " + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture) + Environment.NewLine
                    + "Instance: " + label + Environment.NewLine
                    + "RESULT: " + status + Environment.NewLine, Encoding.UTF8);
            }
            catch (Exception) { }
        }

        // Detects and reads the FRIENDS panel in 'frame', saves an annotated PNG and lists rows/names/button states in the report.
        // Never clicks anything.
        private static void AnalysePanel(Bitmap frame, string folder, string fileName, string title, StringBuilder R, List<string> summary, Point? click)
        {
            FriendsPanelInfo p = TeleportDetector.FindPanel(frame, true);
            R.AppendLine("=== PANEL ANALYSIS (" + title + ") ===");
            if (p == null)
            {
                R.AppendLine("Panel not detected in this capture.");
                summary.Add("Panel analysis (" + title + "): no panel detected.");
            }
            else
            {
                if (p.Rows.Count > 0) TeleportDetector.ReadNames(frame, p);
                R.Append(DescribePanel(p));
                R.AppendLine("Names read: " + TeleportDetector.DescribeNames(p));
                summary.Add("Panel analysis (" + title + "): " + p.Rows.Count + " row(s): " + TeleportDetector.DescribeNames(p) + ".");
            }
            using (Bitmap ann = AnnotatePanel(frame, p, click, "panel analysis: " + title))
            {
                SavePng(ann, Path.Combine(folder, fileName));
            }
            R.AppendLine("(annotated: " + fileName + "; nothing was clicked on the Teleport buttons)");
            R.AppendLine();
        }

        /// <summary>Max wait for the friends list in Diagnose input.</summary>
        public static int ListWaitMs = 25000;

        // Polls the open panel until rows show up (or ListWaitMs). Returns the best frame (caller disposes it unless it is
        // the same object as firstFrame). Records "list loaded after N s" / "still loading after N s" in the report.
        private static Bitmap WaitForLoadedList(IntPtr hwnd, Bitmap firstFrame, StringBuilder R, List<string> summary, Action<string> say)
        {
            R.AppendLine("=== FRIENDS LIST LOADING ===");
            string bt0;
            FriendsPanelInfo p0 = TeleportDetector.FindPanel(firstFrame, true);
            TeleportDetector.PanelState s0 = TeleportDetector.ClassifyPanel(firstFrame, p0, out bt0);
            R.AppendLine("First look after the click: " + s0 + (string.IsNullOrEmpty(bt0) ? "" : " (body text: " + bt0 + ")"));
            if (s0 == TeleportDetector.PanelState.Loaded)
            {
                R.AppendLine("list loaded after 0 s (already loaded when first seen)");
                summary.Add("Friends list: loaded immediately.");
                R.AppendLine();
                return firstFrame;
            }
            TeleportDetector.ListWaitResult lw = TeleportDetector.WaitForList(
                delegate { string m; return CashOutDetector.CaptureWindow(hwnd, out m); },
                ListWaitMs, 700, int.MaxValue, 4000, null,
                delegate(int ms, TeleportDetector.PanelState st) { say("Waiting for the friends list to load... " + (ms / 1000) + " s"); },
                System.Threading.CancellationToken.None);
            string secs = (lw.ElapsedMs / 1000.0).ToString("0.0", CultureInfo.InvariantCulture);
            if (lw.State == TeleportDetector.PanelState.Loaded)
            {
                R.AppendLine("list loaded after " + secs + " s (" + lw.Polls + " polls)");
                summary.Add("Friends list: loaded after " + secs + " s.");
            }
            else
            {
                R.AppendLine("still loading after " + secs + " s (final state " + lw.State + (string.IsNullOrEmpty(lw.BodyText) ? "" : ", body text: " + lw.BodyText) + ", " + lw.Polls + " polls)");
                summary.Add("Friends list: " + (lw.State == TeleportDetector.PanelState.Loading ? "still loading after " + secs + " s (Roblox busy?)" : "state " + lw.State + " after " + secs + " s") + ".");
            }
            R.AppendLine();
            if (lw.Frame == null) return firstFrame;
            return lw.Frame;
        }

        public static DiagResult Run(string label, IntPtr hwnd, Action<string> progress, string existingFolder)
        {
            DiagResult res = new DiagResult();
            string folder = existingFolder;
            if (string.IsNullOrEmpty(folder)) folder = Begin(label, null);
            if (string.IsNullOrEmpty(folder))
            {
                folder = Path.Combine(Path.GetTempPath(), "CookieJar_diag_" + DateTime.Now.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture));
                Directory.CreateDirectory(folder);
            }
            res.Folder = folder;
            bool panelAnalysed = false;
            StringBuilder R = new StringBuilder();
            List<string> summary = new List<string>();
            Action<string> say = delegate(string s) { if (progress != null) { try { progress(s); } catch (Exception) { } } };
            List<string> timings = new List<string>();
            Stopwatch lapSw = Stopwatch.StartNew();
            Action<string> lap = delegate(string n) { timings.Add(n + ": " + lapSw.ElapsedMilliseconds + " ms"); lapSw.Restart(); };

            R.AppendLine("Cookie Jar - input diagnostics");
            R.AppendLine("Time: " + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture));
            R.AppendLine("Instance: " + label);
            R.AppendLine();
            R.AppendLine("=== ENVIRONMENT ===");
            R.Append(DescribeEnvironment(hwnd));
            R.AppendLine();

            if (hwnd == IntPtr.Zero || !InputSim.NativeWin.IsWindow(hwnd))
            {
                summary.Add("FAILED: no game window found for this instance.");
                return Finish(res, R, summary, folder);
            }

            lock (AntiIdleClicker.ClickLock)
            {
                int cx0, cy0;
                InputSim.Backend.GetCursorPos(out cx0, out cy0);
                IntPtr fgStart = InputSim.NativeWin.GetForegroundWindow();
                bool wasIconic = InputSim.NativeWin.IsIconic(hwnd);
                try
                {
                    // --- Focus
                    say("Bringing the game window to the front...");
                    R.AppendLine("=== FOCUS ===");
                    R.AppendLine("Foreground BEFORE: " + InputSim.NativeWin.DescribeWindow(fgStart));
                    InputTrace ft = new InputTrace();
                    if (wasIconic)
                    {
                        InputSim.NativeWin.ShowWindow(hwnd, InputSim.NativeWin.SW_RESTORE);
                        Thread.Sleep(600);
                        R.AppendLine("Window was minimised; restored (still minimised: " + InputSim.NativeWin.IsIconic(hwnd) + ")");
                    }
                    bool focusOk = InputSim.EnsureForeground(hwnd, ft);
                    IntPtr fgAfter = InputSim.NativeWin.GetForegroundWindow();
                    R.AppendLine("Foreground AFTER:  " + InputSim.NativeWin.DescribeWindow(fgAfter));
                    R.AppendLine("Game window is foreground: " + focusOk);
                    R.AppendLine(ft.ToString());
                    R.AppendLine();
                    summary.Add("Focus: " + (focusOk ? "OK, game window came to the front." : "FAILED, foreground stayed " + InputSim.NativeWin.ProcessNameOf(fgAfter) + ". SendInput/mouse_event cannot work like this."));
                    lap("focus");
                    string env = DescribeEnvironment(hwnd);
                    if (env.IndexOf("WARNING", StringComparison.Ordinal) >= 0) summary.Add("Privilege mismatch: the game is elevated and the launcher is not (see report).");

                    // --- Capture and detection
                    say("Capturing the window...");
                    string cm;
                    Bitmap frame = CashOutDetector.CaptureWindow(hwnd, out cm);
                    R.AppendLine("=== CAPTURE AND DETECTION ===");
                    if (frame == null)
                    {
                        R.AppendLine("Capture failed (minimised, hidden or too small).");
                        summary.Add("Capture FAILED, cannot continue.");
                        return Finish(res, R, summary, folder);
                    }
                    InputSim.NativeWin.RECT cr;
                    InputSim.NativeWin.GetClientRect(hwnd, out cr);
                    int cw = cr.Right - cr.Left, ch = cr.Bottom - cr.Top;
                    R.AppendLine("Capture method: " + cm + ", bitmap " + frame.Width + "x" + frame.Height + ", client rect " + cw + "x" + ch + (frame.Width == cw && frame.Height == ch ? " (same size: coordinates map 1:1)" : "  SIZE MISMATCH: coordinates will be off"));
                    summary.Add("Capture: " + cm + " " + frame.Width + "x" + frame.Height + (frame.Width == cw && frame.Height == ch ? ", matches the client area." : ", does NOT match the client area " + cw + "x" + ch + " (DPI scaling problem)."));

                    FriendsPanelInfo panel0 = TeleportDetector.FindPanel(frame, true);
                    FriendsButtonInfo btn = TeleportDetector.FindFriendsButton(frame);
                    Rectangle? cash = CashOutDetector.Find(frame);
                    R.AppendLine("FRIENDS panel already open: " + (panel0 != null));
                    R.AppendLine("CASH OUT button: " + (cash.HasValue ? cash.Value.X + "," + cash.Value.Y + " " + cash.Value.Width + "x" + cash.Value.Height : "not visible"));
                    using (Bitmap a0 = Annotate(frame, btn != null ? (Point?)btn.Click : null, btn != null ? (Rectangle?)btn.Bounds : null, "initial capture (" + cm + ")"))
                    {
                        SavePng(a0, Path.Combine(folder, "01_initial.png"));
                    }
                    if (panel0 != null)
                    {
                        AnalysePanel(frame, folder, "01b_panel_analysis.png", "panel was already open", R, summary, null);
                        panelAnalysed = true;
                    }
                    if (btn == null)
                    {
                        R.AppendLine("FRIENDS button: NOT FOUND (see 01_initial.png). Is the game loaded and the sidebar visible?");
                        summary.Add("FRIENDS button not found in the capture, so no click test was done. Open the game, show the sidebar and run again.");
                        frame.Dispose();
                        return Finish(res, R, summary, folder);
                    }
                    InputSim.NativeWin.POINT sp = new InputSim.NativeWin.POINT();
                    sp.X = btn.Click.X; sp.Y = btn.Click.Y;
                    InputSim.NativeWin.ClientToScreen(hwnd, ref sp);
                    InputSim.NativeWin.RECT wr;
                    InputSim.NativeWin.GetWindowRect(hwnd, out wr);
                    R.AppendLine("FRIENDS button: bounds client " + btn.Bounds.X + "," + btn.Bounds.Y + " " + btn.Bounds.Width + "x" + btn.Bounds.Height + ", score " + btn.Score.ToString("0.00", CultureInfo.InvariantCulture));
                    R.AppendLine("Sticker: icon " + btn.Icon.X + "," + btn.Icon.Y + " " + btn.Icon.Width + "x" + btn.Icon.Height + ", ribbon " + btn.Label.X + "," + btn.Label.Y + " " + btn.Label.Width + "x" + btn.Label.Height
                        + "; click ladder (on the pink icon): " + (btn.Ladder == null ? "none" : string.Join(" ", Array.ConvertAll<Point, string>(btn.Ladder, delegate(Point lp) { return "(" + lp.X + "," + lp.Y + ")"; }))));
                    R.AppendLine("Click point: client (" + btn.Click.X + "," + btn.Click.Y + ") -> screen (" + sp.X + "," + sp.Y + ")");
                    R.AppendLine("  inside the window at " + Pct(btn.Click.X, cw) + " across, " + Pct(btn.Click.Y, ch) + " down; window rect " + wr.Left + "," + wr.Top + " - " + wr.Right + "," + wr.Bottom);
                    summary.Add("FRIENDS button found at client (" + btn.Click.X + "," + btn.Click.Y + ") = screen (" + sp.X + "," + sp.Y + "), " + Pct(btn.Click.X, cw) + " across / " + Pct(btn.Click.Y, ch) + " down.");
                    R.AppendLine();
                    lap("capture and detection");
                    Point click = btn.Click;
                    Rectangle btnBounds = btn.Bounds;
                    frame.Dispose();

                    // --- Move-only test (the keep-alive path)
                    say("Testing mouse movement (keep-alive path)...");
                    InputTrace wt = new InputTrace();
                    ClickReport wig = InputSim.Wiggle(hwnd, false, null, wt);
                    R.AppendLine("=== MOUSE MOVE TEST (keep-alive: small move over the window and back, no click) ===");
                    R.AppendLine("Delivered: " + wig.Delivered + (wig.Delivered ? " via " + InputSim.TechLabel(wig.TechUsed) : "") + ", error: " + (wig.Error ?? "none"));
                    R.AppendLine(wt.ToString());
                    R.AppendLine();
                    lap("mouse move test");
                    summary.Add("Mouse move test: " + (wig.Delivered ? "delivered via " + InputSim.TechLabel(wig.TechUsed) + "." : "FAILED: " + wig.Error));

                    // --- One FRIENDS click per method
                    ClickTech[] techs = new ClickTech[] { ClickTech.SendInput, ClickTech.MouseEvent, ClickTech.PostMessage };
                    int n = 1;
                    List<string> worked = new List<string>();
                    int delivered = 0;
                    foreach (ClickTech tech in techs)
                    {
                        n++;
                        string tn = InputSim.TechLabel(tech);
                        say("Clicking FRIENDS with " + tn + "...");
                        string prefix = n.ToString("00", CultureInfo.InvariantCulture) + "_" + tn;
                        R.AppendLine("=== CLICK TEST: " + tn + " ===");

                        string m2;
                        Bitmap before = CashOutDetector.CaptureWindow(hwnd, out m2);
                        bool state0 = before != null && TeleportDetector.FindPanel(before, true) != null;
                        if (before != null)
                        {
                            using (Bitmap ab = Annotate(before, click, btnBounds, tn + " BEFORE click (panel " + (state0 ? "open" : "closed") + ")"))
                                SavePng(ab, Path.Combine(folder, prefix + "_before.png"));
                            before.Dispose();
                        }

                        List<string> seq = new List<string>();
                        Bitmap firstAfter = null, lastAfter = null;
                        Stopwatch sw = Stopwatch.StartNew();
                        ClickOptions opt = new ClickOptions();
                        opt.RestoreFocus = false;
                        InputTrace tt = new InputTrace();
                        opt.Trace = tt;
                        opt.Verify = delegate
                        {
                            bool changed = false;
                            long deadline = sw.ElapsedMilliseconds + 2500;
                            do
                            {
                                string m3;
                                Bitmap f = CashOutDetector.CaptureWindow(hwnd, out m3);
                                if (f != null)
                                {
                                    bool open = TeleportDetector.FindPanel(f, true) != null;
                                    seq.Add("t=" + sw.ElapsedMilliseconds + " ms after click start: panel " + (open ? "OPEN" : "closed"));
                                    if (firstAfter == null) { firstAfter = (Bitmap)f.Clone(); }
                                    if (lastAfter != null) lastAfter.Dispose();
                                    lastAfter = f;
                                    if (open != state0) { changed = true; break; }
                                }
                                Thread.Sleep(350);
                            } while (sw.ElapsedMilliseconds < deadline);
                            return changed;
                        };
                        ClickReport rep = InputSim.ClickClient(hwnd, click.X, click.Y, new ClickTech[] { tech }, opt);
                        lap("click test " + tn + " (click + verify)");

                        R.AppendLine("Delivered: " + rep.Delivered + ", panel state changed after click: " + rep.Confirmed + ", error: " + (rep.Error ?? "none"));
                        R.AppendLine("Screen point used: (" + rep.ScreenX + "," + rep.ScreenY + ")");
                        R.AppendLine("After-click capture sequence (panel was " + (state0 ? "open" : "closed") + " before):");
                        foreach (string s in seq) R.AppendLine("  " + s);
                        R.AppendLine("Trace:");
                        R.AppendLine(tt.ToString());
                        if (firstAfter != null)
                        {
                            using (Bitmap a1 = Annotate(firstAfter, click, null, tn + " first capture after click"))
                                SavePng(a1, Path.Combine(folder, prefix + "_after_first.png"));
                            firstAfter.Dispose();
                        }
                        if (lastAfter != null)
                        {
                            using (Bitmap a2 = Annotate(lastAfter, click, null, tn + " last capture after click (" + (rep.Confirmed ? "panel toggled" : "no change") + ")"))
                                SavePng(a2, Path.Combine(folder, prefix + "_after_last.png"));
                            // The panel opened: detect it, read the names and list rows/buttons (no Teleport click), then it is closed below via its red X.
                            if (rep.Confirmed && !panelAnalysed && !state0)
                            {
                                // The list loads asynchronously (Loading...): wait for rows before analysing (never clicks).
                                say("Waiting for the friends list to load...");
                                Bitmap loaded = WaitForLoadedList(hwnd, lastAfter, R, summary, say);
                                lap("friends list wait");
                                say("Reading the FRIENDS panel...");
                                AnalysePanel(loaded, folder, prefix + "_panel_analysis.png", "opened by " + tn, R, summary, null);
                                if (!object.ReferenceEquals(loaded, lastAfter)) loaded.Dispose();
                                panelAnalysed = true;
                                lap("panel analysis (OCR)");
                            }
                            lastAfter.Dispose();
                        }
                        if (rep.Delivered) delivered++;
                        if (rep.Confirmed) worked.Add(tn);
                        summary.Add(tn + ": " + (rep.Confirmed ? "WORKS - the FRIENDS panel toggled." : rep.Delivered ? "input accepted, but the game did NOT react (no panel change)." : "could not send: " + rep.Error));

                        // Put the panel back where it was so the next method starts from the same state.
                        if (rep.Confirmed)
                        {
                            say("Undoing the FRIENDS toggle...");
                            Thread.Sleep(400);
                            ClickOptions back = new ClickOptions();
                            back.RestoreFocus = false;
                            back.Trace = new InputTrace();
                            back.Verify = delegate
                            {
                                string m4;
                                Bitmap f = CashOutDetector.CaptureWindow(hwnd, out m4);
                                if (f == null) return false;
                                try { return (TeleportDetector.FindPanel(f, true) != null) == state0; }
                                finally { f.Dispose(); }
                            };
                            // Close with the panel's red X when it has one (like Teleport does); otherwise toggle the FRIENDS button again.
                            int ux = click.X, uy = click.Y;
                            string m5;
                            Bitmap uf = CashOutDetector.CaptureWindow(hwnd, out m5);
                            if (uf != null)
                            {
                                FriendsPanelInfo up = TeleportDetector.FindPanel(uf, true);
                                if (up != null && up.HasCloseButton)
                                {
                                    ux = up.CloseButton.X + up.CloseButton.Width / 2;
                                    uy = up.CloseButton.Y + up.CloseButton.Height / 2;
                                }
                                uf.Dispose();
                            }
                            ClickReport br = InputSim.ClickClient(hwnd, ux, uy, new ClickTech[] { tech }, back);
                            R.AppendLine("Panel closed via " + (ux != click.X || uy != click.Y ? "its red X at client (" + ux + "," + uy + ")" : "the FRIENDS button (no red X detected)") + ": delivered " + br.Delivered + ", panel back to original: " + br.Confirmed);
                            if (!br.Confirmed) R.AppendLine("  WARNING: the FRIENDS panel may still be toggled; please check the game.");
                        }
                        R.AppendLine();
                        Thread.Sleep(400);
                        lap("close panel / settle after " + tn);
                    }

                    if (worked.Count > 0)
                    {
                        res.ClickWorked = true;
                        res.WorkingMethod = worked[0];
                        summary.Add("RESULT: working click method(s): " + string.Join(", ", worked.ToArray()) + ".");
                        summary.Add(worked.Contains("SendInput") ? "Keep the Click method on Auto or SendInput." : "SendInput did not work but " + worked[0] + " did: Auto will use it; Legacy is the right setting.");
                    }
                    else if (delivered > 0)
                    {
                        summary.Add("RESULT: every method was accepted by Windows but the game never reacted. Roblox seems to ignore these inputs (check the screenshots: is the cursor over the button in the 'before' image? is the game window in front?). Send this folder.");
                    }
                    else
                    {
                        summary.Add("RESULT: input could not be delivered at all (see the errors above and in report.txt).");
                    }
                }
                catch (Exception ex)
                {
                    R.AppendLine("DIAGNOSTICS ERROR: " + ex);
                    summary.Add("Diagnostics stopped by an error: " + ex.GetType().Name + " " + ex.Message);
                }
                finally
                {
                    R.AppendLine("=== TIMINGS (per phase) ===");
                    foreach (string tl in timings) R.AppendLine(tl);
                    R.AppendLine();
                    try
                    {
                        InputSim.Backend.SetCursorPos(cx0, cy0);
                        if (wasIconic && InputSim.NativeWin.IsWindow(hwnd)) InputSim.NativeWin.ShowWindow(hwnd, InputSim.NativeWin.SW_SHOWMINNOACTIVE);
                        if (fgStart != IntPtr.Zero && InputSim.NativeWin.IsWindow(fgStart) && !InputSim.NativeWin.SameApp(fgStart, hwnd))
                            InputSim.Backend.Focus(fgStart, new InputTrace());
                    }
                    catch (Exception) { }
                }
            }
            return Finish(res, R, summary, folder);
        }

        private static DiagResult Finish(DiagResult res, StringBuilder R, List<string> summary, string folder)
        {
            StringBuilder s = new StringBuilder();
            foreach (string line in summary) s.AppendLine("- " + line);
            res.Summary = s.ToString();
            string head = "SUMMARY" + Environment.NewLine + res.Summary + Environment.NewLine + "Folder: " + folder + Environment.NewLine + Environment.NewLine;
            try { File.WriteAllText(Path.Combine(folder, "report.txt"), head + R.ToString(), Encoding.UTF8); } catch (Exception) { }
            Prune("2", KeepRunFolders);
            return res;
        }
    }
}
