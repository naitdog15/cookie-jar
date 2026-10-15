using System;
using System.Collections.Generic;
using System.Diagnostics;
using CookieJar.Platform;
using CookieJar.Platform.Windows;

namespace CookieJar.Tests
{
    // Read-only window checks. Mutators (BringToFront, SetBounds, Minimise, Restore) are only called with id 0,
    // which must return false before any native call. No real window is moved, raised or minimised.
    public static class WindowTests
    {
        public static void Run()
        {
            WinWindowService ws = new WinWindowService();

            IReadOnlyList<MonitorInfo> monitors = ws.GetMonitors();
            T.Check("monitors non-empty", monitors != null && monitors.Count > 0, monitors == null ? "null" : monitors.Count + " monitors");

            int primaries = 0;
            bool sizesOk = true;
            bool scaleOk = true;
            bool workInside = true;
            if (monitors != null)
            {
                foreach (MonitorInfo m in monitors)
                {
                    if (m.IsPrimary) primaries++;
                    if (m.Bounds.Width <= 0 || m.Bounds.Height <= 0) sizesOk = false;
                    if (!(m.ScaleFactor > 0)) scaleOk = false;
                    ScreenRect b = m.Bounds;
                    ScreenRect w = m.WorkArea;
                    if (w.X < b.X || w.Y < b.Y || w.Right > b.Right || w.Bottom > b.Bottom) workInside = false;
                }
            }
            T.Check("exactly one primary monitor", primaries == 1, "primary count " + primaries);
            T.Check("every monitor bounds has width and height > 0", sizesOk);
            T.Check("every monitor scale factor > 0", scaleOk);
            T.Check("every monitor WorkArea inside Bounds", workInside);

            long fg = ws.GetForegroundWindow();
            T.Check("GetForegroundWindow callable", fg >= 0, "id " + fg);

            T.Check("Exists(0) is false", !ws.Exists(0));
            T.Check("IsVisible(0) is false", !ws.IsVisible(0));
            T.Check("IsMinimised(0) is false", !ws.IsMinimised(0));

            ScreenRect bounds;
            T.Check("TryGetBounds(0) is false", !ws.TryGetBounds(0, out bounds));
            T.Check("TryGetContentBounds(0) is false", !ws.TryGetContentBounds(0, out bounds));

            // Dead-id mutators: validation returns false before any native window call.
            T.Check("Restore(0) is false", !ws.Restore(0));
            T.Check("Minimise(0) is false", !ws.Minimise(0));
            T.Check("BringToFront(0) is false", !ws.BringToFront(0));
            T.Check("SetBounds(0, valid rect) is false", !ws.SetBounds(0, new ScreenRect(0, 0, 100, 100)));

            int ownPid = Process.GetCurrentProcess().Id;
            IReadOnlyList<long> own = null;
            bool ownThrew = false;
            try { own = ws.GetWindowsOfProcess(ownPid); }
            catch (Exception) { ownThrew = true; }
            T.Check("GetWindowsOfProcess(own pid) does not throw", !ownThrew && own != null, "count " + (own == null ? -1 : own.Count));

            IReadOnlyList<long> none = ws.GetWindowsOfProcess(0);
            T.Check("GetWindowsOfProcess(0) is empty", none != null && none.Count == 0);

            long main = ws.GetMainWindow(ownPid);
            T.Check("GetMainWindow(own pid) callable", main >= 0, "id " + main);
            T.Check("GetMainWindow(0) is 0", ws.GetMainWindow(0) == 0);
        }
    }
}
