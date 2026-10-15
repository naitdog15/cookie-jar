using System;
using System.Collections.Generic;
using CookieJar.Platform;
using CookieJar.Platform.Windows;

namespace CookieJar.Tests
{
    // Read-only input checks. No cursor move, click, key or wheel is sent. Invalid-argument calls return false
    // before any SendInput or SetCursorPos call, so nothing reaches the desktop.
    public static class InputTests
    {
        public static void Run()
        {
            WinInputService input = new WinInputService();

            T.Check("GetPermissionState is Granted", input.GetPermissionState() == PermissionState.Granted);
            T.Check("RequestPermission is Granted", input.RequestPermission() == PermissionState.Granted);

            int cx, cy;
            bool got = input.TryGetCursorPosition(out cx, out cy);
            T.Check("TryGetCursorPosition returns true", got);

            // Virtual screen = union of all monitor Bounds.
            IReadOnlyList<MonitorInfo> monitors = new WinWindowService().GetMonitors();
            int minX = int.MaxValue, minY = int.MaxValue, maxX = int.MinValue, maxY = int.MinValue;
            foreach (MonitorInfo m in monitors)
            {
                minX = Math.Min(minX, m.Bounds.X);
                minY = Math.Min(minY, m.Bounds.Y);
                maxX = Math.Max(maxX, m.Bounds.Right);
                maxY = Math.Max(maxY, m.Bounds.Bottom);
            }
            bool inside = got && cx >= minX && cx < maxX && cy >= minY && cy < maxY;
            T.Check("cursor position inside virtual screen", inside, "(" + cx + "," + cy + ") in " + minX + "," + minY + " .. " + maxX + "," + maxY);

            CheckNormalize("NormalizeAbs origin of 1920x1080", 0, 0, 0, 0, 1920, 1080, 0, 0);
            CheckNormalize("NormalizeAbs bottom-right of 1920x1080", 1919, 1079, 0, 0, 1920, 1080, 65535, 65535);
            CheckNormalize("NormalizeAbs centre of 1920x1080", 960, 540, 0, 0, 1920, 1080, 32785, 32798);
            CheckNormalize("NormalizeAbs left monitor origin (vx=-1920)", -1920, 0, -1920, 0, 3840, 1080, 0, 0);
            CheckNormalize("NormalizeAbs x=0 on virtual -1920..1920", 0, 0, -1920, 0, 3840, 1080, 32776, 0);
            CheckNormalize("NormalizeAbs clamps below range to 0", -50, 0, 0, 0, 1920, 1080, 0, 0);
            CheckNormalize("NormalizeAbs clamps above range to 65535", 5000, 0, 0, 0, 1920, 1080, 65535, 0);
            CheckNormalize("NormalizeAbs size below 2 treated as 2", 1, 1, 0, 0, 1, 1, 65535, 65535);

            // Argument validation: every call below must return false without sending input.
            T.Check("ClickInWindow(0, ...) is false", !input.ClickInWindow(0, 10, 10, MouseButton.Left, 0, 0));
            T.Check("ClickInWindow with invalid button is false", !input.ClickInWindow(0, 10, 10, (MouseButton)99, 0, 0));
            T.Check("TapKeyInWindow(0, ...) is false", !input.TapKeyInWindow(0, 0x20, 0, 0));
            T.Check("Click with invalid button is false", !input.Click((MouseButton)99, 0));
            T.Check("ButtonDown with invalid button is false", !input.ButtonDown((MouseButton)99));
            T.Check("ButtonUp with invalid button is false", !input.ButtonUp((MouseButton)99));
            T.Check("Wheel(0) is false", !input.Wheel(0));
            T.Check("KeyDown(0) is false", !input.KeyDown(0));
            T.Check("KeyUp(0) is false", !input.KeyUp(0));
            T.Check("KeyUp(-1) is false", !input.KeyUp(-1));
        }

        private static void CheckNormalize(string name, int x, int y, int vx, int vy, int vw, int vh, int ex, int ey)
        {
            int nx, ny;
            WinInputService.NormalizeAbs(x, y, vx, vy, vw, vh, out nx, out ny);
            T.Check(name, nx == ex && ny == ey, "got " + nx + "," + ny + " expected " + ex + "," + ey);
        }
    }
}
