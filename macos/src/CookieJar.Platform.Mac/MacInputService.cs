using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading;

namespace CookieJar.Platform.Mac
{
    /// <summary>Raw macOS input calls. Implementations never throw; a missing framework reads as false.</summary>
    public interface IMacInputNative
    {
        /// <summary>AXIsProcessTrusted. With prompt, also opens the Accessibility settings pane when not trusted.</summary>
        bool IsTrusted(bool prompt);
        bool GetCursor(out int x, out int y);
        bool PostMouseMove(int x, int y);
        bool PostMouseButton(MouseButton button, bool down, int x, int y);
        /// <summary>Line-based wheel notches; positive scrolls up.</summary>
        bool PostScroll(int notches);
        bool PostKey(ushort keyCode, bool down);
        void Sleep(int ms);
    }

    /// <summary>
    /// Synthetic input on macOS (CGEvent) with focus-and-restore clicks. Every action needs Accessibility permission;
    /// without Granted, no native post call is made. Thread-safe by an internal lock.
    /// </summary>
    public sealed class MacInputService : IInputService
    {
        // Same settle as InputSim.Timing.FocusSettleMs: Roblox ignores input that lands right after a focus change.
        private const int FocusSettleMs = 140;
        private const int MinimiseStepMs = 50;

        private readonly IMacInputNative _native;
        private readonly IWindowService _windows;
        private readonly object _sync = new object();

        /// <param name="native">Null uses the CoreGraphics/ApplicationServices implementation.</param>
        /// <param name="windows">Needed for ClickInWindow and TapKeyInWindow; without it those return false.</param>
        public MacInputService(IMacInputNative native, IWindowService windows = null)
        {
            _native = native ?? new MacInputNative();
            _windows = windows;
        }

        public PermissionState GetPermissionState()
        {
            try
            {
                return _native.IsTrusted(false) ? PermissionState.Granted : PermissionState.Denied;
            }
            catch (Exception)
            {
                return PermissionState.Unknown;
            }
        }

        public PermissionState RequestPermission()
        {
            try
            {
                return _native.IsTrusted(true) ? PermissionState.Granted : PermissionState.Denied;
            }
            catch (Exception)
            {
                return PermissionState.Unknown;
            }
        }

        public bool TryGetCursorPosition(out int x, out int y)
        {
            x = 0;
            y = 0;
            lock (_sync)
            {
                if (!Allowed()) return false;
                try { return _native.GetCursor(out x, out y); }
                catch (Exception) { x = 0; y = 0; return false; }
            }
        }

        public bool MoveCursor(int x, int y)
        {
            lock (_sync)
            {
                if (!Allowed()) return false;
                try { return _native.PostMouseMove(x, y); }
                catch (Exception) { return false; }
            }
        }

        public bool Click(MouseButton button, int holdMs)
        {
            lock (_sync)
            {
                if (!Allowed()) return false;
                try { return ClickAtCursor(button, holdMs); }
                catch (Exception) { return false; }
            }
        }

        public bool ButtonDown(MouseButton button)
        {
            lock (_sync)
            {
                if (!Allowed()) return false;
                try
                {
                    int x, y;
                    return _native.GetCursor(out x, out y) && _native.PostMouseButton(button, true, x, y);
                }
                catch (Exception) { return false; }
            }
        }

        public bool ButtonUp(MouseButton button)
        {
            lock (_sync)
            {
                if (!Allowed()) return false;
                try
                {
                    int x, y;
                    return _native.GetCursor(out x, out y) && _native.PostMouseButton(button, false, x, y);
                }
                catch (Exception) { return false; }
            }
        }

        public bool Wheel(int notches)
        {
            lock (_sync)
            {
                if (!Allowed()) return false;
                try { return _native.PostScroll(notches); }
                catch (Exception) { return false; }
            }
        }

        public bool KeyDown(int virtualKey)
        {
            lock (_sync)
            {
                if (!Allowed()) return false;
                try
                {
                    ushort code;
                    return MacKeyMap.TryMap(virtualKey, out code) && _native.PostKey(code, true);
                }
                catch (Exception) { return false; }
            }
        }

        public bool KeyUp(int virtualKey)
        {
            lock (_sync)
            {
                if (!Allowed()) return false;
                try
                {
                    ushort code;
                    return MacKeyMap.TryMap(virtualKey, out code) && _native.PostKey(code, false);
                }
                catch (Exception) { return false; }
            }
        }

        /// <summary>
        /// Order: remember cursor and foreground window, focus the window (restoring it if minimised), move to its origin
        /// plus (x, y), click, wait restoreWaitMs, then put the cursor, minimised state and previous foreground window back.
        /// </summary>
        public bool ClickInWindow(long windowId, int x, int y, MouseButton button, int holdMs, int restoreWaitMs)
        {
            lock (_sync)
            {
                if (_windows == null || !Allowed()) return false;
                try
                {
                    int cursorX, cursorY;
                    bool hadCursor = _native.GetCursor(out cursorX, out cursorY);
                    long previousForeground = _windows.GetForegroundWindow();
                    bool wasMinimised = _windows.IsMinimised(windowId);

                    bool ok = false;
                    if (Focus(windowId, restoreWaitMs))
                    {
                        ScreenRect bounds;
                        ok = _windows.TryGetBounds(windowId, out bounds)
                            && _native.PostMouseMove(bounds.X + x, bounds.Y + y)
                            && ClickAtCursor(button, holdMs);
                    }

                    _native.Sleep(restoreWaitMs);
                    RestoreState(windowId, wasMinimised, hadCursor, cursorX, cursorY, previousForeground);
                    return ok;
                }
                catch (Exception)
                {
                    return false;
                }
            }
        }

        /// <summary>Same focus-and-restore order as ClickInWindow, with a key down/up in place of the click.</summary>
        public bool TapKeyInWindow(long windowId, int virtualKey, int holdMs, int restoreWaitMs)
        {
            lock (_sync)
            {
                if (_windows == null || !Allowed()) return false;
                try
                {
                    ushort code;
                    if (!MacKeyMap.TryMap(virtualKey, out code)) return false;

                    long previousForeground = _windows.GetForegroundWindow();
                    bool wasMinimised = _windows.IsMinimised(windowId);

                    bool ok = false;
                    if (Focus(windowId, restoreWaitMs))
                    {
                        ok = _native.PostKey(code, true);
                        if (ok)
                        {
                            if (holdMs > 0) _native.Sleep(holdMs);
                            ok = _native.PostKey(code, false);
                        }
                    }

                    _native.Sleep(restoreWaitMs);
                    RestoreState(windowId, wasMinimised, false, 0, 0, previousForeground);
                    return ok;
                }
                catch (Exception)
                {
                    return false;
                }
            }
        }

        private bool Allowed()
        {
            return GetPermissionState() == PermissionState.Granted;
        }

        /// <summary>Restores the window if minimised (waiting up to restoreWaitMs), then brings it to the front and settles.</summary>
        private bool Focus(long windowId, int restoreWaitMs)
        {
            if (_windows.IsMinimised(windowId))
            {
                _windows.Restore(windowId);
                int waited = 0;
                while (_windows.IsMinimised(windowId) && waited < restoreWaitMs)
                {
                    _native.Sleep(MinimiseStepMs);
                    waited += MinimiseStepMs;
                }
                if (_windows.IsMinimised(windowId)) return false;
            }
            if (!_windows.BringToFront(windowId)) return false;
            _native.Sleep(FocusSettleMs);
            return true;
        }

        /// <summary>Cursor first, then minimised state, then the previous foreground window (same order as InputSim.Restore).</summary>
        private void RestoreState(long windowId, bool wasMinimised, bool hadCursor, int cursorX, int cursorY, long previousForeground)
        {
            if (hadCursor) _native.PostMouseMove(cursorX, cursorY);
            if (wasMinimised) _windows.Minimise(windowId);
            if (previousForeground != 0 && previousForeground != windowId && _windows.Exists(previousForeground))
            {
                _windows.BringToFront(previousForeground);
            }
        }

        /// <summary>Down at the current cursor, hold, up. Does not send up when down failed.</summary>
        private bool ClickAtCursor(MouseButton button, int holdMs)
        {
            int x, y;
            if (!_native.GetCursor(out x, out y)) return false;
            if (!_native.PostMouseButton(button, true, x, y)) return false;
            if (holdMs > 0) _native.Sleep(holdMs);
            return _native.PostMouseButton(button, false, x, y);
        }
    }

    /// <summary>
    /// CoreGraphics and ApplicationServices calls. Each import is guarded: a missing library or symbol returns false.
    /// </summary>
    public sealed class MacInputNative : IMacInputNative
    {
        // MAC-UNVERIFIED: these framework paths are the umbrella frameworks; AXIsProcessTrustedWithOptions is re-exported by ApplicationServices.
        private const string AppServices = "/System/Library/Frameworks/ApplicationServices.framework/ApplicationServices";
        private const string CoreGraphics = "/System/Library/Frameworks/CoreGraphics.framework/CoreGraphics";
        private const string CoreFoundation = "/System/Library/Frameworks/CoreFoundation.framework/CoreFoundation";

        // MAC-UNVERIFIED: CGEventType values and button numbers from CGEventTypes.h.
        private const uint MouseMoved = 5;
        private const uint HidEventTap = 0;   // kCGHIDEventTap
        private const uint ScrollUnitLine = 1; // kCGScrollEventUnitLine

        [StructLayout(LayoutKind.Sequential)]
        private struct CGPointNative
        {
            public double X;
            public double Y;
        }

        [DllImport(AppServices)]
        [return: MarshalAs(UnmanagedType.I1)]
        private static extern bool AXIsProcessTrustedWithOptions(IntPtr options);

        [DllImport(CoreGraphics)]
        private static extern IntPtr CGEventCreate(IntPtr source);

        [DllImport(CoreGraphics)]
        private static extern CGPointNative CGEventGetLocation(IntPtr evt);

        [DllImport(CoreGraphics)]
        private static extern IntPtr CGEventCreateMouseEvent(IntPtr source, uint mouseType, CGPointNative position, uint button);

        [DllImport(CoreGraphics)]
        private static extern void CGEventPost(uint tap, IntPtr evt);

        [DllImport(CoreGraphics)]
        private static extern IntPtr CGEventCreateKeyboardEvent(IntPtr source, ushort keyCode, [MarshalAs(UnmanagedType.I1)] bool keyDown);

        // MAC-UNVERIFIED: variadic in the C header; passing one wheel value works on x86-64 but not guaranteed on arm64.
        [DllImport(CoreGraphics)]
        private static extern IntPtr CGEventCreateScrollWheelEvent(IntPtr source, uint units, uint wheelCount, int wheel1);

        [DllImport(CoreFoundation)]
        private static extern void CFRelease(IntPtr cf);

        public bool IsTrusted(bool prompt)
        {
            bool trusted = Guard(() => AXIsProcessTrustedWithOptions(IntPtr.Zero));
            if (!trusted && prompt) OpenAccessibilitySettings();
            return trusted;
        }

        public bool GetCursor(out int x, out int y)
        {
            x = 0;
            y = 0;
            try
            {
                IntPtr ev = CGEventCreate(IntPtr.Zero);
                if (ev == IntPtr.Zero) return false;
                try
                {
                    CGPointNative p = CGEventGetLocation(ev);
                    x = (int)Math.Round(p.X);
                    y = (int)Math.Round(p.Y);
                    return true;
                }
                finally
                {
                    CFRelease(ev);
                }
            }
            catch (DllNotFoundException) { return false; }
            catch (EntryPointNotFoundException) { return false; }
        }

        public bool PostMouseMove(int x, int y)
        {
            return Guard(() => PostEvent(CGEventCreateMouseEvent(IntPtr.Zero, MouseMoved, Point(x, y), 0)));
        }

        public bool PostMouseButton(MouseButton button, bool down, int x, int y)
        {
            uint downType, upType, buttonNumber;
            if (!ButtonCodes(button, out downType, out upType, out buttonNumber)) return false;
            return Guard(() => PostEvent(CGEventCreateMouseEvent(IntPtr.Zero, down ? downType : upType, Point(x, y), buttonNumber)));
        }

        public bool PostScroll(int notches)
        {
            return Guard(() => PostEvent(CGEventCreateScrollWheelEvent(IntPtr.Zero, ScrollUnitLine, 1, notches)));
        }

        public bool PostKey(ushort keyCode, bool down)
        {
            return Guard(() => PostEvent(CGEventCreateKeyboardEvent(IntPtr.Zero, keyCode, down)));
        }

        public void Sleep(int ms)
        {
            if (ms > 0) Thread.Sleep(ms);
        }

        private static bool Guard(Func<bool> body)
        {
            try { return body(); }
            catch (DllNotFoundException) { return false; }
            catch (EntryPointNotFoundException) { return false; }
        }

        private static bool PostEvent(IntPtr ev)
        {
            if (ev == IntPtr.Zero) return false;
            try
            {
                CGEventPost(HidEventTap, ev);
                return true;
            }
            finally
            {
                CFRelease(ev);
            }
        }

        private static CGPointNative Point(int x, int y)
        {
            return new CGPointNative { X = x, Y = y };
        }

        // MAC-UNVERIFIED: kCGEventLeftMouseDown/Up = 1/2, RightMouseDown/Up = 3/4, OtherMouseDown/Up = 25/26; button numbers 0/1/2.
        private static bool ButtonCodes(MouseButton button, out uint downType, out uint upType, out uint buttonNumber)
        {
            switch (button)
            {
                case MouseButton.Left: downType = 1; upType = 2; buttonNumber = 0; return true;
                case MouseButton.Right: downType = 3; upType = 4; buttonNumber = 1; return true;
                case MouseButton.Middle: downType = 25; upType = 26; buttonNumber = 2; return true;
                default: downType = 0; upType = 0; buttonNumber = 0; return false;
            }
        }

        private static void OpenAccessibilitySettings()
        {
            try
            {
                // MAC-UNVERIFIED: this deep link opens Privacy > Accessibility on recent macOS releases.
                Process.Start("open", "x-apple.systempreferences:com.apple.preference.security?Privacy_Accessibility");
            }
            catch (Exception) { }
        }
    }
}
