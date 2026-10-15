using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text;
using CookieJar.Platform;

namespace CookieJar.Platform.Windows
{
    /// <summary>
    /// Win32 window control, ported from src/WindowManager.cs. Window id = HWND as a long.
    /// Dead or zero ids return false/empty and never throw.
    /// </summary>
    public sealed class WinWindowService : IWindowService, IForegroundProcessSource
    {
        internal const int SW_MINIMIZE = 6;
        internal const int SW_SHOWMINNOACTIVE = 7;
        internal const int SW_RESTORE = 9;

        private const uint GW_OWNER = 4;
        private const uint GA_ROOT = 2;
        private const uint SWP_NOSIZE = 0x0001;
        private const uint SWP_NOMOVE = 0x0002;
        private const uint SWP_NOACTIVATE = 0x0010;
        private const uint SWP_SHOWWINDOW = 0x0040;
        private const uint DWMWA_EXTENDED_FRAME_BOUNDS = 9;
        private const int MDT_EFFECTIVE_DPI = 0;
        private const uint MONITORINFOF_PRIMARY = 1;
        private const int SM_CXSCREEN = 0;
        private const int SM_CYSCREEN = 1;
        private const byte VK_MENU = 0x12;

        private static readonly IntPtr HWND_TOP = new IntPtr(0);
        private static readonly IntPtr HWND_TOPMOST = new IntPtr(-1);
        private static readonly IntPtr HWND_NOTOPMOST = new IntPtr(-2);

        public IReadOnlyList<long> GetWindowsOfProcess(int pid)
        {
            List<long> visible = new List<long>();
            List<long> hidden = new List<long>();
            if (pid <= 0) return visible;
            try
            {
                User32.EnumWindowsProc proc = delegate(IntPtr hwnd, IntPtr lp)
                {
                    try
                    {
                        uint wpid;
                        User32.GetWindowThreadProcessId(hwnd, out wpid);
                        if (wpid != (uint)pid) return true;
                        if (User32.IsWindowVisible(hwnd)) visible.Add(ToId(hwnd));
                        else hidden.Add(ToId(hwnd));
                    }
                    catch (Exception)
                    {
                    }
                    return true;
                };
                User32.EnumWindows(proc, IntPtr.Zero);
                GC.KeepAlive(proc);
            }
            catch (Exception)
            {
            }
            visible.AddRange(hidden);
            return visible;
        }

        public long GetMainWindow(int pid)
        {
            if (pid <= 0) return 0;
            try
            {
                IntPtr best = IntPtr.Zero;
                int bestScore = -1;
                long bestArea = 0;

                User32.EnumWindowsProc proc = delegate(IntPtr hwnd, IntPtr lp)
                {
                    try
                    {
                        uint wpid;
                        User32.GetWindowThreadProcessId(hwnd, out wpid);
                        if (wpid != (uint)pid) return true;
                        if (!User32.IsWindowVisible(hwnd)) return true;
                        if (User32.GetWindow(hwnd, GW_OWNER) != IntPtr.Zero) return true;

                        User32.RECT r;
                        if (!User32.GetWindowRect(hwnd, out r)) return true;
                        int w = r.Right - r.Left;
                        int h = r.Bottom - r.Top;
                        if (w < 200 || h < 150) return true;

                        int score = 0;
                        if (string.Equals(ReadClass(hwnd), "WINDOWSCLIENT", StringComparison.OrdinalIgnoreCase)) score += 2;
                        if (ReadTitle(hwnd).IndexOf("Roblox", StringComparison.OrdinalIgnoreCase) >= 0) score += 1;

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
                User32.EnumWindows(proc, IntPtr.Zero);
                GC.KeepAlive(proc);
                return ToId(best);
            }
            catch (Exception)
            {
                return 0;
            }
        }

        public bool Exists(long windowId)
        {
            IntPtr h = ToHandle(windowId);
            return h != IntPtr.Zero && SafeIsWindow(h);
        }

        public bool IsVisible(long windowId)
        {
            IntPtr h = ToHandle(windowId);
            return h != IntPtr.Zero && SafeIsWindow(h) && User32.IsWindowVisible(h);
        }

        public bool IsMinimised(long windowId)
        {
            IntPtr h = ToHandle(windowId);
            return h != IntPtr.Zero && SafeIsWindow(h) && User32.IsIconic(h);
        }

        public bool Restore(long windowId)
        {
            IntPtr h = ToHandle(windowId);
            if (h == IntPtr.Zero || !SafeIsWindow(h)) return false;
            try
            {
                User32.ShowWindow(h, SW_RESTORE);
                return !User32.IsIconic(h);
            }
            catch (Exception)
            {
                return false;
            }
        }

        public bool Minimise(long windowId)
        {
            IntPtr h = ToHandle(windowId);
            if (h == IntPtr.Zero || !SafeIsWindow(h)) return false;
            try
            {
                User32.ShowWindow(h, SW_MINIMIZE);
                return User32.IsIconic(h);
            }
            catch (Exception)
            {
                return false;
            }
        }

        public bool BringToFront(long windowId)
        {
            return BringToFrontHandle(ToHandle(windowId));
        }

        public long GetForegroundWindow()
        {
            try
            {
                return ToId(User32.GetForegroundWindow());
            }
            catch (Exception)
            {
                return 0;
            }
        }

        public int GetForegroundProcessId()
        {
            try
            {
                IntPtr fg = User32.GetForegroundWindow();
                if (fg == IntPtr.Zero) return 0;
                uint pid;
                User32.GetWindowThreadProcessId(fg, out pid);
                return (int)pid;
            }
            catch (Exception)
            {
                return 0;
            }
        }

        public bool TryGetBounds(long windowId, out ScreenRect bounds)
        {
            bounds = new ScreenRect(0, 0, 0, 0);
            IntPtr h = ToHandle(windowId);
            if (h == IntPtr.Zero || !SafeIsWindow(h)) return false;
            try
            {
                User32.RECT r;
                if (!User32.GetWindowRect(h, out r)) return false;
                bounds = ToRect(r);
                return true;
            }
            catch (Exception)
            {
                return false;
            }
        }

        // Uses the DWM extended frame, so invisible resize borders are not counted (as src/WindowManager.cs does).
        public bool TryGetContentBounds(long windowId, out ScreenRect bounds)
        {
            bounds = new ScreenRect(0, 0, 0, 0);
            IntPtr h = ToHandle(windowId);
            if (h == IntPtr.Zero || !SafeIsWindow(h)) return false;
            try
            {
                User32.RECT frame;
                if (GetFrameBounds(h, out frame))
                {
                    bounds = ToRect(frame);
                    return true;
                }
                return TryGetBounds(windowId, out bounds);
            }
            catch (Exception)
            {
                return false;
            }
        }

        public bool SetBounds(long windowId, ScreenRect bounds)
        {
            IntPtr h = ToHandle(windowId);
            if (h == IntPtr.Zero || !SafeIsWindow(h)) return false;
            if (bounds.Width <= 0 || bounds.Height <= 0) return false;
            try
            {
                if (User32.IsIconic(h) || User32.IsZoomed(h))
                {
                    User32.ShowWindow(h, SW_RESTORE);
                }

                User32.RECT outer;
                if (!User32.GetWindowRect(h, out outer)) return false;

                // Compensate for invisible borders so the visible frame lands on the requested bounds.
                int left = bounds.X;
                int top = bounds.Y;
                int right = bounds.X + bounds.Width;
                int bottom = bounds.Y + bounds.Height;

                User32.RECT frame;
                if (GetFrameBounds(h, out frame))
                {
                    left -= (frame.Left - outer.Left);
                    top -= (frame.Top - outer.Top);
                    right += (outer.Right - frame.Right);
                    bottom += (outer.Bottom - frame.Bottom);
                }

                return User32.SetWindowPos(h, HWND_TOP, left, top, right - left, bottom - top,
                    SWP_NOACTIVATE | SWP_SHOWWINDOW);
            }
            catch (Exception)
            {
                return false;
            }
        }

        public IReadOnlyList<MonitorInfo> GetMonitors()
        {
            List<RawMonitor> found = new List<RawMonitor>();
            try
            {
                User32.MonitorEnumProc proc = delegate(IntPtr hMon, IntPtr hdc, IntPtr rect, IntPtr data)
                {
                    try
                    {
                        User32.MONITORINFOEX info = new User32.MONITORINFOEX();
                        info.cbSize = Marshal.SizeOf(typeof(User32.MONITORINFOEX));
                        if (User32.GetMonitorInfo(hMon, ref info))
                        {
                            RawMonitor m = new RawMonitor();
                            m.Bounds = ToRect(info.rcMonitor);
                            m.Work = ToRect(info.rcWork);
                            m.IsPrimary = (info.dwFlags & MONITORINFOF_PRIMARY) != 0;
                            m.Scale = GetDpiScale(hMon);
                            found.Add(m);
                        }
                    }
                    catch (Exception)
                    {
                    }
                    return true;
                };
                User32.EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero, proc, IntPtr.Zero);
                GC.KeepAlive(proc);
            }
            catch (Exception)
            {
            }

            if (found.Count == 0)
            {
                found.Add(FallbackPrimary());
            }

            // Primary first, then left-to-right, top-to-bottom (same order as src/WindowManager.cs).
            found.Sort(CompareMonitors);

            List<MonitorInfo> list = new List<MonitorInfo>();
            for (int i = 0; i < found.Count; i++)
            {
                RawMonitor m = found[i];
                list.Add(new MonitorInfo(i, m.Bounds, m.Work, m.Scale, m.IsPrimary));
            }
            return list;
        }

        // ------------------------------------------------------------------
        // Helpers shared with WinInputService (internal)
        // ------------------------------------------------------------------

        internal static IntPtr ToHandle(long id)
        {
            if (id == 0) return IntPtr.Zero;
            try
            {
                return new IntPtr(id);
            }
            catch (OverflowException)
            {
                return IntPtr.Zero;
            }
        }

        internal static long ToId(IntPtr h)
        {
            return h == IntPtr.Zero ? 0 : h.ToInt64();
        }

        internal static bool SafeIsWindow(IntPtr h)
        {
            try
            {
                return h != IntPtr.Zero && User32.IsWindow(h);
            }
            catch (Exception)
            {
                return false;
            }
        }

        internal static IntPtr RootOf(IntPtr h)
        {
            if (h == IntPtr.Zero) return IntPtr.Zero;
            IntPtr r = User32.GetAncestor(h, GA_ROOT);
            return r == IntPtr.Zero ? h : r;
        }

        internal static int PidOf(IntPtr h)
        {
            if (h == IntPtr.Zero) return 0;
            uint pid;
            User32.GetWindowThreadProcessId(h, out pid);
            return (int)pid;
        }

        /// <summary>True when both handles are the same window, the same top-level window, or the same process.</summary>
        internal static bool SameApp(IntPtr a, IntPtr b)
        {
            if (a == IntPtr.Zero || b == IntPtr.Zero) return false;
            if (a == b || RootOf(a) == RootOf(b)) return true;
            int pa = PidOf(a);
            return pa != 0 && pa == PidOf(b);
        }

        /// <summary>Raises and focuses the window. Port of WindowManager.BringToFront, unchanged.</summary>
        internal static bool BringToFrontHandle(IntPtr hwnd)
        {
            try
            {
                if (hwnd == IntPtr.Zero || !User32.IsWindow(hwnd)) return false;
                if (User32.IsIconic(hwnd)) User32.ShowWindow(hwnd, SW_RESTORE);

                IntPtr fg = User32.GetForegroundWindow();
                if (fg == hwnd) return true;

                uint dummy;
                uint fgThread = fg == IntPtr.Zero ? 0 : User32.GetWindowThreadProcessId(fg, out dummy);
                uint targetThread = User32.GetWindowThreadProcessId(hwnd, out dummy);
                uint curThread = User32.GetCurrentThreadId();

                bool attachedFgTarget = false;
                bool attachedCurFg = false;
                try
                {
                    if (fgThread != 0 && targetThread != 0 && fgThread != targetThread)
                    {
                        attachedFgTarget = User32.AttachThreadInput(fgThread, targetThread, true);
                    }
                    if (fgThread != 0 && fgThread != curThread)
                    {
                        attachedCurFg = User32.AttachThreadInput(curThread, fgThread, true);
                    }

                    // ALT tap so Windows lets this process take the foreground.
                    User32.keybd_event(VK_MENU, 0, 0, UIntPtr.Zero);
                    User32.keybd_event(VK_MENU, 0, InputNative.KEYEVENTF_KEYUP, UIntPtr.Zero);

                    User32.SetWindowPos(hwnd, HWND_TOPMOST, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE);
                    User32.SetWindowPos(hwnd, HWND_NOTOPMOST, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE);
                    User32.BringWindowToTop(hwnd);
                    User32.SetForegroundWindow(hwnd);
                }
                finally
                {
                    if (attachedCurFg) User32.AttachThreadInput(curThread, fgThread, false);
                    if (attachedFgTarget) User32.AttachThreadInput(fgThread, targetThread, false);
                }

                return User32.GetForegroundWindow() == hwnd;
            }
            catch (Exception)
            {
                return false;
            }
        }

        // ------------------------------------------------------------------
        // Private helpers
        // ------------------------------------------------------------------

        private sealed class RawMonitor
        {
            public ScreenRect Bounds;
            public ScreenRect Work;
            public double Scale;
            public bool IsPrimary;
        }

        private static int CompareMonitors(RawMonitor a, RawMonitor b)
        {
            if (a.IsPrimary != b.IsPrimary) return a.IsPrimary ? -1 : 1;
            if (a.Bounds.X != b.Bounds.X) return a.Bounds.X.CompareTo(b.Bounds.X);
            return a.Bounds.Y.CompareTo(b.Bounds.Y);
        }

        private static RawMonitor FallbackPrimary()
        {
            int w = 0, h = 0;
            try
            {
                w = User32.GetSystemMetrics(SM_CXSCREEN);
                h = User32.GetSystemMetrics(SM_CYSCREEN);
            }
            catch (Exception)
            {
            }
            if (w <= 0) w = 1920;
            if (h <= 0) h = 1080;

            RawMonitor m = new RawMonitor();
            m.Bounds = new ScreenRect(0, 0, w, h);
            m.Work = m.Bounds;
            m.Scale = 1.0;
            m.IsPrimary = true;
            return m;
        }

        private static double GetDpiScale(IntPtr hMon)
        {
            try
            {
                uint dpiX, dpiY;
                int hr = Shcore.GetDpiForMonitor(hMon, MDT_EFFECTIVE_DPI, out dpiX, out dpiY);
                if (hr == 0 && dpiX > 0) return dpiX / 96.0;
            }
            catch (Exception)
            {
            }
            return 1.0;
        }

        private static bool GetFrameBounds(IntPtr hwnd, out User32.RECT frame)
        {
            frame = new User32.RECT();
            try
            {
                int hr = Dwmapi.DwmGetWindowAttribute(hwnd, DWMWA_EXTENDED_FRAME_BOUNDS, out frame,
                    (uint)Marshal.SizeOf(typeof(User32.RECT)));
                return hr == 0 && frame.Right > frame.Left && frame.Bottom > frame.Top;
            }
            catch (Exception)
            {
                return false;
            }
        }

        private static ScreenRect ToRect(User32.RECT r)
        {
            return new ScreenRect(r.Left, r.Top, r.Right - r.Left, r.Bottom - r.Top);
        }

        private static string ReadClass(IntPtr hwnd)
        {
            try
            {
                StringBuilder sb = new StringBuilder(256);
                User32.GetClassName(hwnd, sb, sb.Capacity);
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
                int len = User32.GetWindowTextLength(hwnd);
                StringBuilder sb = new StringBuilder(len + 1);
                User32.GetWindowText(hwnd, sb, sb.Capacity);
                return sb.ToString();
            }
            catch (Exception)
            {
                return "";
            }
        }
    }
}
