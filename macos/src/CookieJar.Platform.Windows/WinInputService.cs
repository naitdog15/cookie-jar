using System;
using System.Threading;
using CookieJar.Platform;

namespace CookieJar.Platform.Windows
{
    /// <summary>
    /// Synthetic mouse and keyboard input through SendInput, ported from src/InputSim.cs.
    /// Virtual keys are passed straight through (the platform-neutral codes are the Windows VK values).
    /// Every public method validates its arguments before any native input call.
    /// </summary>
    public sealed class WinInputService : IInputService
    {
        // Timings from InputSim.Timing.
        private const int ApproachDx = 14;
        private const int ApproachDy = 6;
        private const int MoveSteps = 4;
        private const int MoveStepMs = 20;
        private const int HoverMs = 120;
        private const int PostUpMs = 60;
        private const int FocusSettleMs = 140;
        private const int CursorTolerancePx = 2;

        private static readonly object _lock = new object();

        public PermissionState GetPermissionState()
        {
            return PermissionState.Granted;
        }

        public PermissionState RequestPermission()
        {
            return PermissionState.Granted;
        }

        public bool TryGetCursorPosition(out int x, out int y)
        {
            x = 0;
            y = 0;
            try
            {
                User32.POINT p;
                if (!User32.GetCursorPos(out p)) return false;
                x = p.X;
                y = p.Y;
                return true;
            }
            catch (Exception)
            {
                return false;
            }
        }

        public bool MoveCursor(int x, int y)
        {
            lock (_lock)
            {
                try
                {
                    return User32.SetCursorPos(x, y);
                }
                catch (Exception)
                {
                    return false;
                }
            }
        }

        public bool Click(MouseButton button, int holdMs)
        {
            uint down, up;
            if (!TryButtonFlags(button, out down, out up)) return false;
            if (holdMs < 0) holdMs = 0;
            lock (_lock)
            {
                if (!SendMouse(down)) return false;
                Sleep(holdMs);
                if (!SendMouse(up))
                {
                    SendMouse(up);   // never leave the button stuck down
                    return false;
                }
            }
            return true;
        }

        public bool ButtonDown(MouseButton button)
        {
            uint down, up;
            if (!TryButtonFlags(button, out down, out up)) return false;
            lock (_lock)
            {
                return SendMouse(down);
            }
        }

        public bool ButtonUp(MouseButton button)
        {
            uint down, up;
            if (!TryButtonFlags(button, out down, out up)) return false;
            lock (_lock)
            {
                return SendMouse(up);
            }
        }

        public bool Wheel(int notches)
        {
            if (notches == 0) return false;
            lock (_lock)
            {
                InputNative.INPUT[] i = new InputNative.INPUT[1];
                i[0].type = InputNative.INPUT_MOUSE;
                i[0].mi.mouseData = unchecked((uint)(notches * InputNative.WHEEL_DELTA));
                i[0].mi.dwFlags = InputNative.MOUSEEVENTF_WHEEL;
                return SendOne(i) == 1;
            }
        }

        public bool KeyDown(int virtualKey)
        {
            if (!ValidVirtualKey(virtualKey)) return false;
            lock (_lock)
            {
                return SendKey(virtualKey, false);
            }
        }

        public bool KeyUp(int virtualKey)
        {
            if (!ValidVirtualKey(virtualKey)) return false;
            lock (_lock)
            {
                return SendKey(virtualKey, true);
            }
        }

        // Simplified port: one SendInput focus path (restore, BringToFront, real mouse moves). The source's
        // SendInput -> mouse_event -> PostMessage fallback chain and its focus retry ladder are not ported.
        public bool ClickInWindow(long windowId, int x, int y, MouseButton button, int holdMs, int restoreWaitMs)
        {
            uint down, up;
            if (!TryButtonFlags(button, out down, out up)) return false;
            IntPtr h = WinWindowService.ToHandle(windowId);
            if (h == IntPtr.Zero || !WinWindowService.SafeIsWindow(h)) return false;
            if (holdMs < 0) holdMs = 0;
            if (restoreWaitMs < 0) restoreWaitMs = 0;

            lock (_lock)
            {
                int sx0, sy0;
                if (!GetCursor(out sx0, out sy0)) return false;
                IntPtr prevFg = User32.GetForegroundWindow();
                bool wasIconic = false;
                bool ok = false;
                try
                {
                    if (!Focus(h, restoreWaitMs, out wasIconic)) return false;
                    if (!MoveAndCheck(h, x, y)) return false;
                    if (!SendMouse(down)) return false;
                    Sleep(holdMs);
                    if (!SendMouse(up))
                    {
                        SendMouse(up);   // never leave the button stuck down
                        return false;
                    }
                    Sleep(PostUpMs);
                    ok = true;
                }
                catch (Exception)
                {
                    ok = false;
                }
                finally
                {
                    RestoreState(h, sx0, sy0, prevFg, wasIconic);
                }
                return ok;
            }
        }

        public bool TapKeyInWindow(long windowId, int virtualKey, int holdMs, int restoreWaitMs)
        {
            if (!ValidVirtualKey(virtualKey)) return false;
            IntPtr h = WinWindowService.ToHandle(windowId);
            if (h == IntPtr.Zero || !WinWindowService.SafeIsWindow(h)) return false;
            if (holdMs < 0) holdMs = 0;
            if (restoreWaitMs < 0) restoreWaitMs = 0;

            lock (_lock)
            {
                int sx0, sy0;
                if (!GetCursor(out sx0, out sy0)) return false;
                IntPtr prevFg = User32.GetForegroundWindow();
                bool wasIconic = false;
                bool ok = false;
                try
                {
                    if (!Focus(h, restoreWaitMs, out wasIconic)) return false;
                    if (!SendKey(virtualKey, false)) return false;
                    Sleep(holdMs);
                    if (!SendKey(virtualKey, true))
                    {
                        SendKey(virtualKey, true);   // never leave the key stuck down
                        return false;
                    }
                    ok = true;
                }
                catch (Exception)
                {
                    ok = false;
                }
                finally
                {
                    RestoreState(h, sx0, sy0, prevFg, wasIconic);
                }
                return ok;
            }
        }

        /// <summary>
        /// Screen pixel to SendInput absolute coordinates (0..65535 over the VIRTUAL screen, MOUSEEVENTF_VIRTUALDESK).
        /// Copy of InputSim.NormalizeAbs, kept identical so the maths can be tested. vx,vy,vw,vh = virtual screen rect.
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

        // ------------------------------------------------------------------
        // Private helpers (called with _lock held, or before it is taken)
        // ------------------------------------------------------------------

        private static bool TryButtonFlags(MouseButton button, out uint down, out uint up)
        {
            down = 0;
            up = 0;
            switch (button)
            {
                case MouseButton.Left:
                    down = InputNative.MOUSEEVENTF_LEFTDOWN;
                    up = InputNative.MOUSEEVENTF_LEFTUP;
                    return true;
                case MouseButton.Right:
                    down = InputNative.MOUSEEVENTF_RIGHTDOWN;
                    up = InputNative.MOUSEEVENTF_RIGHTUP;
                    return true;
                case MouseButton.Middle:
                    down = InputNative.MOUSEEVENTF_MIDDLEDOWN;
                    up = InputNative.MOUSEEVENTF_MIDDLEUP;
                    return true;
                default:
                    return false;
            }
        }

        // 0 is undefined and 0xFF is reserved in the Win32 virtual-key table.
        private static bool ValidVirtualKey(int virtualKey)
        {
            return virtualKey >= 1 && virtualKey <= 254;
        }

        private static bool GetCursor(out int x, out int y)
        {
            x = 0;
            y = 0;
            User32.POINT p;
            if (!User32.GetCursorPos(out p)) return false;
            x = p.X;
            y = p.Y;
            return true;
        }

        private static bool SendMouse(uint flags)
        {
            InputNative.INPUT[] i = new InputNative.INPUT[1];
            i[0].type = InputNative.INPUT_MOUSE;
            i[0].mi.dwFlags = flags;
            return SendOne(i) == 1;
        }

        private static bool SendKey(int virtualKey, bool up)
        {
            InputNative.INPUT[] i = new InputNative.INPUT[1];
            i[0].type = InputNative.INPUT_KEYBOARD;
            i[0].ki.wVk = (ushort)virtualKey;
            i[0].ki.wScan = (ushort)User32.MapVirtualKey((uint)virtualKey, 0);
            i[0].ki.dwFlags = up ? InputNative.KEYEVENTF_KEYUP : 0u;
            return SendOne(i) == 1;
        }

        private static uint SendOne(InputNative.INPUT[] inputs)
        {
            return User32.SendInput(1, inputs, InputNative.InputSize);
        }

        private static uint MoveAbsolute(int x, int y)
        {
            int vx = User32.GetSystemMetrics(InputNative.SM_XVIRTUALSCREEN);
            int vy = User32.GetSystemMetrics(InputNative.SM_YVIRTUALSCREEN);
            int vw = User32.GetSystemMetrics(InputNative.SM_CXVIRTUALSCREEN);
            int vh = User32.GetSystemMetrics(InputNative.SM_CYVIRTUALSCREEN);
            int nx, ny;
            NormalizeAbs(x, y, vx, vy, vw, vh, out nx, out ny);

            InputNative.INPUT[] i = new InputNative.INPUT[1];
            i[0].type = InputNative.INPUT_MOUSE;
            i[0].mi.dx = nx;
            i[0].mi.dy = ny;
            i[0].mi.dwFlags = InputNative.MOUSEEVENTF_MOVE | InputNative.MOUSEEVENTF_ABSOLUTE | InputNative.MOUSEEVENTF_VIRTUALDESK;
            return SendOne(i);
        }

        private static void Sleep(int ms)
        {
            if (ms > 0) Thread.Sleep(ms);
        }

        private static bool Near(int ax, int ay, int bx, int by, int tol)
        {
            return Math.Abs(ax - bx) <= tol && Math.Abs(ay - by) <= tol;
        }

        private static bool CursorAt(int x, int y, int tol)
        {
            int cx, cy;
            return GetCursor(out cx, out cy) && Near(cx, cy, x, y, tol);
        }

        // Raises the window, restoring it first when minimised. True when it is in the foreground.
        private static bool Focus(IntPtr h, int restoreWaitMs, out bool wasIconic)
        {
            wasIconic = User32.IsIconic(h);
            if (wasIconic)
            {
                User32.ShowWindow(h, WinWindowService.SW_RESTORE);
                int waited = 0;
                while (User32.IsIconic(h) && waited < restoreWaitMs)
                {
                    Sleep(50);
                    waited += 50;
                }
                if (User32.IsIconic(h)) return false;
                Sleep(FocusSettleMs);
            }
            return WinWindowService.BringToFrontHandle(h);
        }

        // Real move events from a point beside the target, then a check that the window is under the point.
        private static bool MoveAndCheck(IntPtr h, int sx, int sy)
        {
            int ax = sx - ApproachDx;
            int ay = sy + ApproachDy;
            User32.RECT wr;
            if (User32.GetWindowRect(h, out wr))
            {
                if (ax < wr.Left + 2) ax = sx + ApproachDx;
                if (ay > wr.Bottom - 2) ay = sy - ApproachDy;
            }
            User32.SetCursorPos(ax, ay);
            Sleep(25);

            int steps = Math.Max(1, MoveSteps);
            for (int i = 1; i <= steps; i++)
            {
                int x = ax + (int)Math.Round((sx - ax) * (double)i / steps);
                int y = ay + (int)Math.Round((sy - ay) * (double)i / steps);
                if (MoveAbsolute(x, y) == 0) return false;   // refused: UIPI, secure desktop or locked screen
                Sleep(MoveStepMs);
                if (!CursorAt(x, y, CursorTolerancePx + 2))
                {
                    User32.SetCursorPos(x, y);
                    Sleep(15);
                    if (!CursorAt(x, y, CursorTolerancePx + 2)) return false;
                }
            }

            if (!CursorAt(sx, sy, CursorTolerancePx))
            {
                User32.SetCursorPos(sx, sy);
                Sleep(20);
                if (!CursorAt(sx, sy, CursorTolerancePx)) return false;
            }
            if (!WindowUnderPoint(h, sx, sy)) return false;
            Sleep(HoverMs);
            return WindowUnderPoint(h, sx, sy);
        }

        // True when the point is over the window or one of its process's windows, so the game receives the input.
        private static bool WindowUnderPoint(IntPtr h, int sx, int sy)
        {
            User32.POINT p = new User32.POINT();
            p.X = sx;
            p.Y = sy;
            IntPtr under = User32.WindowFromPoint(p);
            if (under == IntPtr.Zero) return false;
            return WinWindowService.PidOf(under) == WinWindowService.PidOf(h)
                || WinWindowService.RootOf(under) == WinWindowService.RootOf(h);
        }

        private static void RestoreState(IntPtr h, int sx0, int sy0, IntPtr prevFg, bool wasIconic)
        {
            try
            {
                User32.SetCursorPos(sx0, sy0);
                if (wasIconic && WinWindowService.SafeIsWindow(h))
                {
                    User32.ShowWindow(h, WinWindowService.SW_SHOWMINNOACTIVE);
                }
                if (prevFg != IntPtr.Zero && WinWindowService.SafeIsWindow(prevFg) && !WinWindowService.SameApp(prevFg, h))
                {
                    WinWindowService.BringToFrontHandle(prevFg);
                }
            }
            catch (Exception)
            {
            }
        }
    }
}
