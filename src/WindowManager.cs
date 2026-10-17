// WindowManager.cs
//
// Win32 window placement, tiling, focus and fullscreen helpers for Roblox client windows.
// Namespace: RbxMulti. C# 5 / .NET Framework. Physical pixels throughout (app is PerMonitorV2 DPI aware).
// Only Win32 window APIs are used. No memory access to Roblox, no injection.
//
// ===================== PUBLIC API (for the UI team) =====================
//
// public class MonitorInfo
//   int  Index            position in the GetMonitors() list (0 = primary, then left-to-right)
//   bool IsPrimary
//   int  X, Y, Width, Height       work area (rcWork), absolute virtual-screen physical px
//   int  FullX, FullY, FullWidth, FullHeight   full monitor rect (rcMonitor), physical px
//   double DpiScale       1.0 = 96 dpi (effective DPI / 96)
//   IntPtr Handle         HMONITOR
//   string DeviceName     szDevice, e.g. "\\\\.\\DISPLAY1"
//
// public class TileCell
//   int X, Y, Width, Height   absolute virtual-screen physical px (already inside the work area)
//   int MonitorIndex          MonitorInfo.Index the cell belongs to
//
// public class WindowLayout
//   int MonitorIndex, X, Y, Width, Height   physical px, absolute virtual-screen coords
//
// public static class WindowManager
//
//   IntPtr FindWindowForPid(int pid, int timeoutMs)
//       Polls EnumWindows every ~250 ms until timeoutMs elapses (at least one scan).
//       Candidate: GetWindowThreadProcessId == pid, IsWindowVisible, no owner (GW_OWNER == 0),
//       size >= 200x150. Preference: class "WINDOWSCLIENT" (+2), title contains "Roblox" (+1),
//       then larger area. Returns IntPtr.Zero when nothing matches. Never throws.
//
//   List<MonitorInfo> GetMonitors()
//       EnumDisplayMonitors + GetMonitorInfo. DPI via shcore GetDpiForMonitor(MDT_EFFECTIVE_DPI),
//       falling back to user32 GetDpiForWindow(desktop) and then 96. Sorted primary first, then
//       left-to-right. Index = list position. Never returns an empty list (falls back to
//       GetSystemMetrics primary). Never throws.
//
//   List<TileCell> ComputeTiling(int count, MonitorInfo monitor)
//       count is clamped to 1..32. Tries every cols in 1..count with rows = ceil(count/cols) and
//       scores each by the largest cell area after fitting to 16:9. Highest score wins; ties go
//       to fewer rows. Cells are row-major, fill the work area evenly with a 1 px inset per side
//       (2 px gap between neighbours), and an incomplete last row is centred. Returns exactly
//       count cells (empty list only if monitor is null or has no area).
//
//   List<TileCell> ComputeTilingAcrossMonitors(int count, List<MonitorInfo> monitors)
//       count clamped to 1..32. If count <= 1 or only one monitor: tiles the primary monitor
//       (or monitors[0] when no primary). Otherwise distributes count across monitors
//       proportionally to work-area size (remainder goes to the largest monitors), calls
//       ComputeTiling per monitor, and returns cells in monitor list order. Null or empty
//       monitors list makes it call GetMonitors().
//
//   bool Place(IntPtr hwnd, WindowLayout layout)
//       Restores the window if minimised or maximised. Compensates the invisible DWM frame
//       (DWMWA_EXTENDED_FRAME_BOUNDS vs GetWindowRect) so the VISIBLE bounds equal the layout
//       rect. SetWindowPos(HWND_TOP, SWP_NOACTIVATE | SWP_SHOWWINDOW). Returns success.
//
//   bool Place(IntPtr hwnd, TileCell cell)
//       Convenience overload: converts the cell to a WindowLayout and calls Place.
//
//   bool BringToFront(IntPtr hwnd)
//       Restores if minimised, then: ALT key trick, AttachThreadInput(foreground thread, target
//       thread) and (current thread, foreground thread), SetWindowPos TOPMOST then NOTOPMOST,
//       BringWindowToTop, SetForegroundWindow, detach. Returns GetForegroundWindow() == hwnd.
//       Windows may still refuse focus; the return value reports the outcome.
//
//   void BringAllToFront(IList<IntPtr> hwnds)
//       Calls BringToFront in list order with an 80 ms sleep between windows. The LAST window in
//       the list ends up on top (and focused). Pass windows in the order you want stacked, so
//       the one to end on top goes last.
//
//   bool IsFullscreen(IntPtr hwnd)
//       True when the window rect covers the whole monitor rect (MonitorFromWindow, nearest)
//       and the window has no WS_CAPTION style.
//
//   bool IsWindowAlive(IntPtr hwnd)
//       IsWindow.
//
// All methods catch their own exceptions and never throw to the caller.
// ========================================================================

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;

namespace RbxMulti
{
    public class MonitorInfo
    {
        public int Index;
        public bool IsPrimary;
        public int X, Y, Width, Height;
        public int FullX, FullY, FullWidth, FullHeight;
        public double DpiScale;
        public IntPtr Handle;
        public string DeviceName;
    }

    public class TileCell
    {
        public int X, Y, Width, Height;
        public int MonitorIndex;
    }

    public class WindowLayout
    {
        public int MonitorIndex, X, Y, Width, Height;
    }

    public static class WindowManager
    {
        private const int SW_RESTORE = 9;
        private const uint GW_OWNER = 4;
        private const int GWL_STYLE = -16;
        private const int WS_CAPTION = 0x00C00000;
        private const uint MONITOR_DEFAULTTONEAREST = 2;
        private const uint MDT_EFFECTIVE_DPI = 0;
        private const int SM_CXSCREEN = 0;
        private const int SM_CYSCREEN = 1;
        private const uint DWMWA_EXTENDED_FRAME_BOUNDS = 9;
        private const uint SWP_NOSIZE = 0x0001;
        private const uint SWP_NOMOVE = 0x0002;
        private const uint SWP_NOACTIVATE = 0x0010;
        private const uint SWP_SHOWWINDOW = 0x0040;
        private const byte VK_MENU = 0x12;
        private const uint KEYEVENTF_KEYUP = 0x0002;

        private static readonly IntPtr HWND_TOP = new IntPtr(0);
        private static readonly IntPtr HWND_TOPMOST = new IntPtr(-1);
        private static readonly IntPtr HWND_NOTOPMOST = new IntPtr(-2);

        // ------------------------------------------------------------------
        // Window discovery
        // ------------------------------------------------------------------

        public static IntPtr FindWindowForPid(int pid, int timeoutMs)
        {
            try
            {
                if (timeoutMs < 0) timeoutMs = 0;
                Stopwatch sw = Stopwatch.StartNew();
                while (true)
                {
                    IntPtr found = FindWindowOnce(pid);
                    if (found != IntPtr.Zero) return found;
                    if (sw.ElapsedMilliseconds >= timeoutMs) break;
                    Thread.Sleep(250);
                }
            }
            catch (Exception)
            {
            }
            return IntPtr.Zero;
        }

        private static IntPtr FindWindowOnce(int pid)
        {
            IntPtr best = IntPtr.Zero;
            int bestScore = -1;
            long bestArea = 0;

            NativeMethods.EnumWindowsProc proc = delegate(IntPtr hwnd, IntPtr lp)
            {
                try
                {
                    uint wpid;
                    NativeMethods.GetWindowThreadProcessId(hwnd, out wpid);
                    if (wpid != (uint)pid) return true;
                    if (!NativeMethods.IsWindowVisible(hwnd)) return true;
                    if (NativeMethods.GetWindow(hwnd, GW_OWNER) != IntPtr.Zero) return true;

                    NativeMethods.RECT r;
                    if (!NativeMethods.GetWindowRect(hwnd, out r)) return true;
                    int w = r.Right - r.Left;
                    int h = r.Bottom - r.Top;
                    if (w < 200 || h < 150) return true;

                    string cls = ReadClass(hwnd);
                    string title = ReadTitle(hwnd);
                    int score = 0;
                    if (string.Equals(cls, "WINDOWSCLIENT", StringComparison.OrdinalIgnoreCase)) score += 2;
                    if (title.IndexOf("Roblox", StringComparison.OrdinalIgnoreCase) >= 0) score += 1;

                    long area = (long)w * h;
                    if (score > bestScore || (score == bestScore && area > bestArea))
                    {
                        best = hwnd;
                        bestScore = score;
                        bestArea = area;
                    }
                }
                catch (Exception)
                {
                }
                return true;
            };

            try
            {
                NativeMethods.EnumWindows(proc, IntPtr.Zero);
            }
            catch (Exception)
            {
            }
            GC.KeepAlive(proc);
            return best;
        }

        // ------------------------------------------------------------------
        // Monitors
        // ------------------------------------------------------------------

        public static List<MonitorInfo> GetMonitors()
        {
            List<MonitorInfo> list = new List<MonitorInfo>();
            try
            {
                NativeMethods.MonitorEnumProc proc = delegate(IntPtr hMon, IntPtr hdc, IntPtr rect, IntPtr data)
                {
                    try
                    {
                        MonitorInfo mi = ReadMonitor(hMon);
                        if (mi != null) list.Add(mi);
                    }
                    catch (Exception)
                    {
                    }
                    return true;
                };
                NativeMethods.EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero, proc, IntPtr.Zero);
                GC.KeepAlive(proc);
            }
            catch (Exception)
            {
            }

            if (list.Count == 0)
            {
                list.Add(FallbackPrimary());
            }

            list.Sort(CompareMonitors);
            for (int i = 0; i < list.Count; i++)
            {
                list[i].Index = i;
            }
            return list;
        }

        private static int CompareMonitors(MonitorInfo a, MonitorInfo b)
        {
            if (a.IsPrimary != b.IsPrimary) return a.IsPrimary ? -1 : 1;
            if (a.X != b.X) return a.X.CompareTo(b.X);
            return a.Y.CompareTo(b.Y);
        }

        private static MonitorInfo ReadMonitor(IntPtr hMon)
        {
            NativeMethods.MONITORINFOEX info = new NativeMethods.MONITORINFOEX();
            info.cbSize = Marshal.SizeOf(typeof(NativeMethods.MONITORINFOEX));
            if (!NativeMethods.GetMonitorInfo(hMon, ref info)) return null;

            MonitorInfo mi = new MonitorInfo();
            mi.Handle = hMon;
            mi.DeviceName = info.szDevice != null ? info.szDevice : "";
            mi.IsPrimary = (info.dwFlags & 1) != 0;
            mi.FullX = info.rcMonitor.Left;
            mi.FullY = info.rcMonitor.Top;
            mi.FullWidth = info.rcMonitor.Right - info.rcMonitor.Left;
            mi.FullHeight = info.rcMonitor.Bottom - info.rcMonitor.Top;
            mi.X = info.rcWork.Left;
            mi.Y = info.rcWork.Top;
            mi.Width = info.rcWork.Right - info.rcWork.Left;
            mi.Height = info.rcWork.Bottom - info.rcWork.Top;
            mi.DpiScale = GetDpiScale(hMon);
            return mi;
        }

        private static MonitorInfo FallbackPrimary()
        {
            int w = 0, h = 0;
            try
            {
                w = NativeMethods.GetSystemMetrics(SM_CXSCREEN);
                h = NativeMethods.GetSystemMetrics(SM_CYSCREEN);
            }
            catch (Exception)
            {
            }
            if (w <= 0) w = 1920;
            if (h <= 0) h = 1080;

            MonitorInfo mi = new MonitorInfo();
            mi.Index = 0;
            mi.IsPrimary = true;
            mi.X = 0;
            mi.Y = 0;
            mi.Width = w;
            mi.Height = h;
            mi.FullX = 0;
            mi.FullY = 0;
            mi.FullWidth = w;
            mi.FullHeight = h;
            mi.DpiScale = GetDpiScale(IntPtr.Zero);
            mi.Handle = IntPtr.Zero;
            mi.DeviceName = "PRIMARY";
            return mi;
        }

        private static double GetDpiScale(IntPtr hMon)
        {
            try
            {
                uint dpiX, dpiY;
                int hr = NativeMethods.GetDpiForMonitor(hMon, MDT_EFFECTIVE_DPI, out dpiX, out dpiY);
                if (hr == 0 && dpiX > 0) return dpiX / 96.0;
            }
            catch (Exception)
            {
            }
            try
            {
                uint d = NativeMethods.GetDpiForWindow(NativeMethods.GetDesktopWindow());
                if (d > 0) return d / 96.0;
            }
            catch (Exception)
            {
            }
            return 1.0;
        }

        // ------------------------------------------------------------------
        // Tiling
        // ------------------------------------------------------------------

        public static List<TileCell> ComputeTiling(int count, MonitorInfo monitor)
        {
            List<TileCell> cells = new List<TileCell>();
            try
            {
                if (monitor == null) return cells;
                if (count < 1) count = 1;
                if (count > 32) count = 32;

                int workW = monitor.Width;
                int workH = monitor.Height;
                if (workW <= 0 || workH <= 0) return cells;

                int bestCols = 1;
                int bestRows = count;
                double bestScore = -1.0;
                const double eps = 1e-9;

                for (int cols = 1; cols <= count; cols++)
                {
                    int rows = (count + cols - 1) / cols;
                    double w = (double)workW / cols;
                    double h = (double)workH / rows;
                    double fitW = Math.Min(w, h * 16.0 / 9.0);
                    double score = fitW * (fitW * 9.0 / 16.0);   // area of the 16:9 window that fits the cell
                    if (score > bestScore + eps || (Math.Abs(score - bestScore) <= eps && rows < bestRows))
                    {
                        bestScore = score;
                        bestCols = cols;
                        bestRows = rows;
                    }
                }

                if (count == 1)
                {
                    // A lone window fills the whole monitor (taskbar included), like a borderless fullscreen.
                    TileCell full = new TileCell();
                    full.X = monitor.FullWidth > 0 ? monitor.FullX : monitor.X;
                    full.Y = monitor.FullWidth > 0 ? monitor.FullY : monitor.Y;
                    full.Width = monitor.FullWidth > 0 ? monitor.FullWidth : workW;
                    full.Height = monitor.FullWidth > 0 ? monitor.FullHeight : workH;
                    full.MonitorIndex = monitor.Index;
                    cells.Add(full);
                    return cells;
                }

                int colsUsed = bestCols;
                int rowsUsed = bestRows;
                for (int r = 0; r < rowsUsed; r++)
                {
                    int inRow = Math.Min(colsUsed, count - r * colsUsed);
                    int offset = (colsUsed - inRow) / 2;
                    int top = monitor.Y + (r * workH) / rowsUsed;
                    int bottom = monitor.Y + ((r + 1) * workH) / rowsUsed;

                    for (int k = 0; k < inRow; k++)
                    {
                        int left = monitor.X + ((k + offset) * workW) / colsUsed;
                        int right = monitor.X + ((k + offset + 1) * workW) / colsUsed;

                        TileCell cell = new TileCell();
                        cell.X = left + 1;
                        cell.Y = top + 1;
                        cell.Width = Math.Max(1, (right - left) - 2);
                        cell.Height = Math.Max(1, (bottom - top) - 2);
                        cell.MonitorIndex = monitor.Index;
                        cells.Add(cell);
                    }
                }
            }
            catch (Exception)
            {
            }
            return cells;
        }

        public static List<TileCell> ComputeTilingAcrossMonitors(int count, List<MonitorInfo> monitors)
        {
            List<TileCell> result = new List<TileCell>();
            try
            {
                if (count < 1) count = 1;
                if (count > 32) count = 32;
                if (monitors == null || monitors.Count == 0) monitors = GetMonitors();
                if (monitors.Count == 0) return result;

                MonitorInfo primary = null;
                for (int i = 0; i < monitors.Count; i++)
                {
                    if (monitors[i].IsPrimary) { primary = monitors[i]; break; }
                }
                if (primary == null) primary = monitors[0];

                if (count <= 1 || monitors.Count == 1)
                {
                    return ComputeTiling(count, primary);
                }

                int n = monitors.Count;
                long[] areas = new long[n];
                long total = 0;
                for (int i = 0; i < n; i++)
                {
                    long a = (long)Math.Max(0, monitors[i].Width) * Math.Max(0, monitors[i].Height);
                    areas[i] = a;
                    total += a;
                }

                int[] counts = new int[n];
                int assigned = 0;
                if (total > 0)
                {
                    for (int i = 0; i < n; i++)
                    {
                        counts[i] = (int)((long)count * areas[i] / total);
                        assigned += counts[i];
                    }
                }

                List<int> order = new List<int>();
                for (int i = 0; i < n; i++) order.Add(i);
                order.Sort(delegate(int a, int b) { return areas[b].CompareTo(areas[a]); });

                int idx = 0;
                while (assigned < count)
                {
                    counts[order[idx % n]]++;
                    assigned++;
                    idx++;
                }

                for (int i = 0; i < n; i++)
                {
                    if (counts[i] <= 0) continue;
                    result.AddRange(ComputeTiling(counts[i], monitors[i]));
                }
            }
            catch (Exception)
            {
            }
            return result;
        }

        // ------------------------------------------------------------------
        // Placement
        // ------------------------------------------------------------------

        public static bool Place(IntPtr hwnd, TileCell cell)
        {
            try
            {
                if (cell == null) return false;
                WindowLayout layout = new WindowLayout();
                layout.MonitorIndex = cell.MonitorIndex;
                layout.X = cell.X;
                layout.Y = cell.Y;
                layout.Width = cell.Width;
                layout.Height = cell.Height;
                return Place(hwnd, layout);
            }
            catch (Exception)
            {
                return false;
            }
        }

        public static bool Place(IntPtr hwnd, WindowLayout layout)
        {
            try
            {
                if (hwnd == IntPtr.Zero || layout == null) return false;
                if (!NativeMethods.IsWindow(hwnd)) return false;
                if (layout.Width <= 0 || layout.Height <= 0) return false;

                if (NativeMethods.IsIconic(hwnd) || NativeMethods.IsZoomed(hwnd))
                {
                    NativeMethods.ShowWindow(hwnd, SW_RESTORE);
                }

                NativeMethods.RECT outer;
                if (!NativeMethods.GetWindowRect(hwnd, out outer)) return false;

                int left = layout.X;
                int top = layout.Y;
                int right = layout.X + layout.Width;
                int bottom = layout.Y + layout.Height;

                NativeMethods.RECT frame;
                if (GetFrameBounds(hwnd, out frame))
                {
                    left -= (frame.Left - outer.Left);
                    top -= (frame.Top - outer.Top);
                    right += (outer.Right - frame.Right);
                    bottom += (outer.Bottom - frame.Bottom);
                }

                return NativeMethods.SetWindowPos(hwnd, HWND_TOP, left, top, right - left, bottom - top,
                    SWP_NOACTIVATE | SWP_SHOWWINDOW);
            }
            catch (Exception)
            {
                return false;
            }
        }

        /// <summary>
        /// Covers the whole monitor the window is mostly on (taskbar included) and raises it. Returns the cell used,
        /// or null when the window or monitor could not be found.
        /// </summary>
        public static TileCell PlaceFullscreen(IntPtr hwnd)
        {
            try
            {
                if (hwnd == IntPtr.Zero || !NativeMethods.IsWindow(hwnd)) return null;
                IntPtr hMon = NativeMethods.MonitorFromWindow(hwnd, MONITOR_DEFAULTTONEAREST);
                MonitorInfo target = null;
                List<MonitorInfo> all = GetMonitors();
                for (int i = 0; i < all.Count; i++)
                {
                    if (all[i].Handle == hMon) { target = all[i]; break; }
                }
                if (target == null) target = all[0];

                TileCell cell = new TileCell();
                cell.X = target.FullX;
                cell.Y = target.FullY;
                cell.Width = target.FullWidth;
                cell.Height = target.FullHeight;
                cell.MonitorIndex = target.Index;
                return Place(hwnd, cell) ? cell : null;
            }
            catch (Exception)
            {
                return null;
            }
        }

        private static bool GetFrameBounds(IntPtr hwnd, out NativeMethods.RECT frame)
        {
            frame = new NativeMethods.RECT();
            try
            {
                int hr = NativeMethods.DwmGetWindowAttribute(hwnd, DWMWA_EXTENDED_FRAME_BOUNDS, out frame,
                    (uint)Marshal.SizeOf(typeof(NativeMethods.RECT)));
                return hr == 0 && frame.Right > frame.Left && frame.Bottom > frame.Top;
            }
            catch (Exception)
            {
                return false;
            }
        }

        // ------------------------------------------------------------------
        // Focus
        // ------------------------------------------------------------------

        public static bool BringToFront(IntPtr hwnd)
        {
            try
            {
                if (hwnd == IntPtr.Zero || !NativeMethods.IsWindow(hwnd)) return false;
                if (NativeMethods.IsIconic(hwnd)) NativeMethods.ShowWindow(hwnd, SW_RESTORE);

                IntPtr fg = NativeMethods.GetForegroundWindow();
                if (fg == hwnd) return true;

                uint dummy;
                uint fgThread = fg == IntPtr.Zero ? 0 : NativeMethods.GetWindowThreadProcessId(fg, out dummy);
                uint targetThread = NativeMethods.GetWindowThreadProcessId(hwnd, out dummy);
                uint curThread = NativeMethods.GetCurrentThreadId();

                bool attachedFgTarget = false;
                bool attachedCurFg = false;
                try
                {
                    if (fgThread != 0 && targetThread != 0 && fgThread != targetThread)
                    {
                        attachedFgTarget = NativeMethods.AttachThreadInput(fgThread, targetThread, true);
                    }
                    if (fgThread != 0 && fgThread != curThread)
                    {
                        attachedCurFg = NativeMethods.AttachThreadInput(curThread, fgThread, true);
                    }

                    // ALT tap so Windows lets this process take the foreground.
                    NativeMethods.keybd_event(VK_MENU, 0, 0, UIntPtr.Zero);
                    NativeMethods.keybd_event(VK_MENU, 0, KEYEVENTF_KEYUP, UIntPtr.Zero);

                    NativeMethods.SetWindowPos(hwnd, HWND_TOPMOST, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE);
                    NativeMethods.SetWindowPos(hwnd, HWND_NOTOPMOST, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE);
                    NativeMethods.BringWindowToTop(hwnd);
                    NativeMethods.SetForegroundWindow(hwnd);
                }
                finally
                {
                    if (attachedCurFg) NativeMethods.AttachThreadInput(curThread, fgThread, false);
                    if (attachedFgTarget) NativeMethods.AttachThreadInput(fgThread, targetThread, false);
                }

                return NativeMethods.GetForegroundWindow() == hwnd;
            }
            catch (Exception)
            {
                return false;
            }
        }

        // Calls BringToFront in list order with 80 ms between windows.
        // The LAST window in the list ends up on top (and focused).
        public static void BringAllToFront(IList<IntPtr> hwnds)
        {
            try
            {
                if (hwnds == null) return;
                for (int i = 0; i < hwnds.Count; i++)
                {
                    BringToFront(hwnds[i]);
                    if (i < hwnds.Count - 1) Thread.Sleep(80);
                }
            }
            catch (Exception)
            {
            }
        }

        // ------------------------------------------------------------------
        // State queries
        // ------------------------------------------------------------------

        public static bool IsFullscreen(IntPtr hwnd)
        {
            try
            {
                if (hwnd == IntPtr.Zero || !NativeMethods.IsWindow(hwnd)) return false;

                NativeMethods.RECT r;
                if (!NativeMethods.GetWindowRect(hwnd, out r)) return false;

                IntPtr mon = NativeMethods.MonitorFromWindow(hwnd, MONITOR_DEFAULTTONEAREST);
                if (mon == IntPtr.Zero) return false;

                NativeMethods.MONITORINFOEX mi = new NativeMethods.MONITORINFOEX();
                mi.cbSize = Marshal.SizeOf(typeof(NativeMethods.MONITORINFOEX));
                if (!NativeMethods.GetMonitorInfo(mon, ref mi)) return false;

                bool covers = r.Left <= mi.rcMonitor.Left && r.Top <= mi.rcMonitor.Top
                    && r.Right >= mi.rcMonitor.Right && r.Bottom >= mi.rcMonitor.Bottom;
                if (!covers) return false;

                int style = NativeMethods.GetWindowLong(hwnd, GWL_STYLE);
                if ((style & WS_CAPTION) == WS_CAPTION) return false;

                return true;
            }
            catch (Exception)
            {
                return false;
            }
        }

        public static bool IsWindowAlive(IntPtr hwnd)
        {
            try
            {
                return hwnd != IntPtr.Zero && NativeMethods.IsWindow(hwnd);
            }
            catch (Exception)
            {
                return false;
            }
        }

        // ------------------------------------------------------------------
        // Helpers
        // ------------------------------------------------------------------

        private static string ReadClass(IntPtr hwnd)
        {
            try
            {
                StringBuilder sb = new StringBuilder(256);
                NativeMethods.GetClassName(hwnd, sb, sb.Capacity);
                return sb.ToString();
            }
            catch (Exception)
            {
                return "";
            }
        }

        private static string ReadTitle(IntPtr hwnd)
        {
            try
            {
                int len = NativeMethods.GetWindowTextLength(hwnd);
                StringBuilder sb = new StringBuilder(len + 1);
                NativeMethods.GetWindowText(hwnd, sb, sb.Capacity);
                return sb.ToString();
            }
            catch (Exception)
            {
                return "";
            }
        }

        // ------------------------------------------------------------------
        // Native P/Invoke declarations
        // ------------------------------------------------------------------

        private static class NativeMethods
        {
            [StructLayout(LayoutKind.Sequential)]
            public struct RECT
            {
                public int Left;
                public int Top;
                public int Right;
                public int Bottom;
            }

            [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
            public struct MONITORINFOEX
            {
                public int cbSize;
                public RECT rcMonitor;
                public RECT rcWork;
                public uint dwFlags;
                [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)]
                public string szDevice;
            }

            public delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);
            public delegate bool MonitorEnumProc(IntPtr hMonitor, IntPtr hdcMonitor, IntPtr lprcMonitor, IntPtr dwData);

            [DllImport("user32.dll")]
            public static extern bool EnumWindows(EnumWindowsProc lpEnumFunc, IntPtr lParam);

            [DllImport("user32.dll")]
            public static extern bool IsWindowVisible(IntPtr hWnd);

            [DllImport("user32.dll")]
            public static extern bool IsWindow(IntPtr hWnd);

            [DllImport("user32.dll")]
            public static extern bool IsIconic(IntPtr hWnd);

            [DllImport("user32.dll")]
            public static extern bool IsZoomed(IntPtr hWnd);

            [DllImport("user32.dll")]
            public static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);

            [DllImport("user32.dll")]
            public static extern IntPtr GetWindow(IntPtr hWnd, uint uCmd);

            [DllImport("user32.dll")]
            public static extern bool GetWindowRect(IntPtr hWnd, out RECT lpRect);

            [DllImport("user32.dll", CharSet = CharSet.Unicode)]
            public static extern int GetClassName(IntPtr hWnd, StringBuilder lpClassName, int nMaxCount);

            [DllImport("user32.dll", CharSet = CharSet.Unicode)]
            public static extern int GetWindowText(IntPtr hWnd, StringBuilder lpString, int nMaxCount);

            [DllImport("user32.dll", CharSet = CharSet.Unicode)]
            public static extern int GetWindowTextLength(IntPtr hWnd);

            [DllImport("user32.dll")]
            public static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

            [DllImport("user32.dll")]
            public static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter, int X, int Y, int cx, int cy, uint uFlags);

            [DllImport("user32.dll")]
            public static extern bool BringWindowToTop(IntPtr hWnd);

            [DllImport("user32.dll")]
            public static extern bool SetForegroundWindow(IntPtr hWnd);

            [DllImport("user32.dll")]
            public static extern IntPtr GetForegroundWindow();

            [DllImport("user32.dll")]
            public static extern bool AttachThreadInput(uint idAttach, uint idAttachTo, bool fAttach);

            [DllImport("kernel32.dll")]
            public static extern uint GetCurrentThreadId();

            [DllImport("user32.dll")]
            public static extern void keybd_event(byte bVk, byte bScan, uint dwFlags, UIntPtr dwExtraInfo);

            [DllImport("user32.dll")]
            public static extern int GetSystemMetrics(int nIndex);

            [DllImport("user32.dll")]
            public static extern int GetWindowLong(IntPtr hWnd, int nIndex);

            [DllImport("user32.dll")]
            public static extern IntPtr GetDesktopWindow();

            [DllImport("user32.dll")]
            public static extern uint GetDpiForWindow(IntPtr hwnd);

            [DllImport("user32.dll")]
            public static extern IntPtr MonitorFromWindow(IntPtr hwnd, uint dwFlags);

            [DllImport("user32.dll")]
            public static extern bool EnumDisplayMonitors(IntPtr hdc, IntPtr lprcClip, MonitorEnumProc lpfnEnum, IntPtr dwData);

            [DllImport("user32.dll", CharSet = CharSet.Unicode)]
            public static extern bool GetMonitorInfo(IntPtr hMonitor, ref MONITORINFOEX lpmi);

            [DllImport("shcore.dll")]
            public static extern int GetDpiForMonitor(IntPtr hmonitor, uint dpiType, out uint dpiX, out uint dpiY);

            [DllImport("dwmapi.dll")]
            public static extern int DwmGetWindowAttribute(IntPtr hwnd, uint dwAttribute, out RECT pvAttribute, uint cbAttribute);
        }
    }
}
