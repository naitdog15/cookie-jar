using System;
using System.Collections.Generic;
using CookieJar.Platform;
using CookieJar.Platform.Mac;

namespace CookieJar.Tests
{
    public static class InputTests
    {
        public static void Run()
        {
            KeyMapValues();
            PermissionStates();
            DeniedPermissionMakesNoNativeCalls();
            ClickSendsDownThenUpAtCursor();
            ClickInWindowOrder();
            ClickInWindowRestoresMinimisedWindow();
            TapKeyInWindowOrder();
            NoWindowServiceMeansNoWindowClick();
        }

        private static void KeyMapValues()
        {
            ushort code;
            T.Check("'A' (0x41) maps to key code 0", MacKeyMap.TryMap(0x41, out code) && code == 0, code.ToString());
            T.Check("'Z' (0x5A) maps to key code 6", MacKeyMap.TryMap(0x5A, out code) && code == 6, code.ToString());
            T.Check("'1' (0x31) maps to key code 18", MacKeyMap.TryMap(0x31, out code) && code == 18, code.ToString());
            T.Check("Space (0x20) maps to key code 49", MacKeyMap.TryMap(0x20, out code) && code == 49, code.ToString());
            T.Check("Enter (0x0D) maps to key code 36", MacKeyMap.TryMap(0x0D, out code) && code == 36, code.ToString());
            T.Check("Escape (0x1B) maps to key code 53", MacKeyMap.TryMap(0x1B, out code) && code == 53, code.ToString());
            T.Check("Tab (0x09) maps to key code 48", MacKeyMap.TryMap(0x09, out code) && code == 48, code.ToString());
            T.Check("Left arrow (0x25) maps to key code 123", MacKeyMap.TryMap(0x25, out code) && code == 123, code.ToString());
            T.Check("Down arrow (0x28) maps to key code 125", MacKeyMap.TryMap(0x28, out code) && code == 125, code.ToString());
            T.Check("Shift (0x10) maps to key code 56", MacKeyMap.TryMap(0x10, out code) && code == 56, code.ToString());
            T.Check("Right Ctrl (0xA3) maps to key code 62", MacKeyMap.TryMap(0xA3, out code) && code == 62, code.ToString());
            T.Check("unknown virtual key 0x99 is not mapped", !MacKeyMap.TryMap(0x99, out code));
        }

        private static void PermissionStates()
        {
            var log = new List<string>();
            var native = new InputFakeNative(log) { Trusted = true };
            var svc = new MacInputService(native);
            T.Check("trusted process reports Granted", svc.GetPermissionState() == PermissionState.Granted);

            native.Trusted = false;
            T.Check("untrusted process reports Denied", svc.GetPermissionState() == PermissionState.Denied);
            T.Check("request while untrusted reports Denied", svc.RequestPermission() == PermissionState.Denied);

            var throwing = new InputFakeNative(log) { ThrowOnTrust = true };
            T.Check("native failure reports Unknown", new MacInputService(throwing).GetPermissionState() == PermissionState.Unknown);
        }

        private static void DeniedPermissionMakesNoNativeCalls()
        {
            var log = new List<string>();
            var native = new InputFakeNative(log) { Trusted = false, CursorX = 5, CursorY = 6 };
            var windows = new InputFakeWindows(log) { Foreground = 42 };
            windows.Existing.Add(42);
            var svc = new MacInputService(native, windows);
            long id = 5003L;

            int x, y;
            var results = new List<bool>
            {
                svc.TryGetCursorPosition(out x, out y),
                svc.MoveCursor(10, 10),
                svc.Click(MouseButton.Left, 30),
                svc.ButtonDown(MouseButton.Left),
                svc.ButtonUp(MouseButton.Left),
                svc.Wheel(3),
                svc.KeyDown(0x41),
                svc.KeyUp(0x41),
                svc.ClickInWindow(id, 10, 20, MouseButton.Left, 30, 500),
                svc.TapKeyInWindow(id, 0x41, 30, 500)
            };

            bool allFalse = true;
            foreach (bool r in results) allFalse &= !r;
            T.Check("every input method returns false when permission is Denied", allFalse);
            T.Check("no native post call recorded when permission is Denied", native.PostCount == 0, native.PostCount.ToString());
            T.Check("no window call recorded when permission is Denied", log.Count == 0, string.Join(";", log));
        }

        private static void ClickSendsDownThenUpAtCursor()
        {
            var log = new List<string>();
            var native = new InputFakeNative(log) { Trusted = true, CursorX = 50, CursorY = 60 };
            var svc = new MacInputService(native);

            bool ok = svc.Click(MouseButton.Left, 30);
            T.Check("click reports success", ok);
            T.Check("click sends down then up at the current cursor",
                log.Count == 2 && log[0] == "down:0@50,60" && log[1] == "up:0@50,60", string.Join(";", log));
            T.Check("click holds for holdMs between down and up", native.Sleeps.Count == 1 && native.Sleeps[0] == 30,
                string.Join(",", native.Sleeps));

            log.Clear();
            svc.Click(MouseButton.Right, 0);
            T.Check("right click uses the right button codes and no hold",
                log.Count == 2 && log[0] == "down:1@50,60" && log[1] == "up:1@50,60", string.Join(";", log));
        }

        private static void ClickInWindowOrder()
        {
            var log = new List<string>();
            var native = new InputFakeNative(log) { Trusted = true, CursorX = 5, CursorY = 6 };
            var windows = new InputFakeWindows(log) { Foreground = 42 };
            windows.Existing.Add(42);
            var svc = new MacInputService(native, windows);
            long id = 5003L;

            bool ok = svc.ClickInWindow(id, 10, 20, MouseButton.Left, 40, 500);
            string expected = "front:" + id + ";move:110,220;down:0@110,220;up:0@110,220;move:5,6;front:42";
            T.Check("ClickInWindow reports success", ok);
            T.Check("ClickInWindow order is front, move, down, up, restore cursor, restore previous foreground",
                string.Join(";", log) == expected, string.Join(";", log));
            T.Check("ClickInWindow leaves the previous window in front", windows.Foreground == 42);
        }

        private static void ClickInWindowRestoresMinimisedWindow()
        {
            var log = new List<string>();
            var native = new InputFakeNative(log) { Trusted = true, CursorX = 5, CursorY = 6 };
            var windows = new InputFakeWindows(log) { Foreground = 42, Minimised = true };
            windows.Existing.Add(42);
            var svc = new MacInputService(native, windows);
            long id = 5003L;

            bool ok = svc.ClickInWindow(id, 10, 20, MouseButton.Left, 40, 500);
            string expected = "restore:" + id + ";front:" + id + ";move:110,220;down:0@110,220;up:0@110,220;move:5,6;min:" + id + ";front:42";
            T.Check("ClickInWindow restores, clicks and minimises a window that was minimised",
                ok && string.Join(";", log) == expected, string.Join(";", log));
        }

        private static void TapKeyInWindowOrder()
        {
            var log = new List<string>();
            var native = new InputFakeNative(log) { Trusted = true, CursorX = 5, CursorY = 6 };
            var windows = new InputFakeWindows(log) { Foreground = 42 };
            windows.Existing.Add(42);
            var svc = new MacInputService(native, windows);
            long id = 5003L;

            bool ok = svc.TapKeyInWindow(id, 0x41, 60, 500);
            string expected = "front:" + id + ";key:down:0;key:up:0;front:42";
            T.Check("TapKeyInWindow order is front, key down, key up, restore previous foreground",
                ok && string.Join(";", log) == expected, string.Join(";", log));

            log.Clear();
            T.Check("TapKeyInWindow with an unmapped key does nothing", !svc.TapKeyInWindow(id, 0x99, 60, 500) && log.Count == 0);
        }

        private static void NoWindowServiceMeansNoWindowClick()
        {
            var log = new List<string>();
            var native = new InputFakeNative(log) { Trusted = true };
            var svc = new MacInputService(native, null);
            T.Check("ClickInWindow without a window service is false and posts nothing",
                !svc.ClickInWindow(5001L, 1, 1, MouseButton.Left, 10, 10) && native.PostCount == 0);
        }

        /// <summary>Records every call in a shared log so window and input order can be asserted together.</summary>
        public sealed class InputFakeNative : IMacInputNative
        {
            private readonly List<string> _log;
            public bool Trusted;
            public bool ThrowOnTrust;
            public int CursorX;
            public int CursorY;
            public int PostCount;
            public readonly List<int> Sleeps = new List<int>();

            public InputFakeNative(List<string> log) { _log = log; }

            public bool IsTrusted(bool prompt)
            {
                if (ThrowOnTrust) throw new InvalidOperationException("no framework");
                return Trusted;
            }

            public bool GetCursor(out int x, out int y)
            {
                x = CursorX;
                y = CursorY;
                return true;
            }

            public bool PostMouseMove(int x, int y)
            {
                PostCount++;
                _log.Add("move:" + x + "," + y);
                CursorX = x;
                CursorY = y;
                return true;
            }

            public bool PostMouseButton(MouseButton button, bool down, int x, int y)
            {
                PostCount++;
                _log.Add((down ? "down:" : "up:") + (int)button + "@" + x + "," + y);
                return true;
            }

            public bool PostScroll(int notches)
            {
                PostCount++;
                _log.Add("scroll:" + notches);
                return true;
            }

            public bool PostKey(ushort keyCode, bool down)
            {
                PostCount++;
                _log.Add("key:" + (down ? "down:" : "up:") + keyCode);
                return true;
            }

            public void Sleep(int ms)
            {
                Sleeps.Add(ms);
            }
        }

        /// <summary>Window service that logs focus and minimise changes into the shared log.</summary>
        public sealed class InputFakeWindows : IWindowService
        {
            private readonly List<string> _log;
            public long Foreground;
            public bool Minimised;
            public readonly HashSet<long> Existing = new HashSet<long>();
            public ScreenRect Bounds = new ScreenRect(100, 200, 800, 600);

            public InputFakeWindows(List<string> log) { _log = log; }

            public IReadOnlyList<long> GetWindowsOfProcess(int pid) { return new long[0]; }
            public long GetMainWindow(int pid) { return 0; }
            public bool Exists(long windowId) { return Existing.Contains(windowId); }
            public bool IsVisible(long windowId) { return true; }
            public bool IsMinimised(long windowId) { return Minimised; }

            public bool Restore(long windowId)
            {
                _log.Add("restore:" + windowId);
                Minimised = false;
                return true;
            }

            public bool Minimise(long windowId)
            {
                _log.Add("min:" + windowId);
                Minimised = true;
                return true;
            }

            public bool BringToFront(long windowId)
            {
                _log.Add("front:" + windowId);
                Foreground = windowId;
                return true;
            }

            public long GetForegroundWindow() { return Foreground; }

            public bool TryGetBounds(long windowId, out ScreenRect bounds)
            {
                bounds = Bounds;
                return true;
            }

            public bool TryGetContentBounds(long windowId, out ScreenRect bounds)
            {
                bounds = Bounds;
                return true;
            }

            public bool SetBounds(long windowId, ScreenRect bounds) { return false; }
            public IReadOnlyList<MonitorInfo> GetMonitors() { return new MonitorInfo[0]; }
        }
    }
}
