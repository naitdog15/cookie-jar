using System;
using System.Collections.Generic;
using System.Linq;
using CookieJar.Platform;
using CookieJar.Platform.Mac;

namespace CookieJar.Tests
{
    public static class WindowTests
    {
        public static void Run()
        {
            WindowIdRoundTrip();
            ParsesWindowRowsAndSkipsBadLines();
            CocoaToTopLeftConversion();
            ParsesScreensIntoMonitors();
            ContentBoundsRemoveTitleBar();
            MainWindowPrefersFirstWhenNotMinimised();
            MainWindowFallsBackWhenFirstMinimised();
            WindowsOfProcessIncludesExtraPids();
            ExistsAndBoundsFromListing();
            ForegroundWindowIsWindowOneOfFrontmostProcess();
            MinimiseAndRestoreUseAxMinimised();
            SetBoundsSetsPositionAndSize();
            FailedOsascriptReturnsFalseOrEmpty();
            NonZeroExitMakesRestoreFalse();
            MonitorsFromInjectedScreenSource();
        }

        private static CommandResult Ok(string output)
        {
            return new CommandResult(0, output, "", false, false);
        }

        private const string TwoWindows = "1|0|0|800|600|true|true\r\n\r\nbogus line\r\n2|10|10|800|600|false|true\r\n";

        private static void WindowIdRoundTrip()
        {
            long id = MacWindowParsing.EncodeWindowId(1234, 3);
            T.Check("window id of pid 1234 index 3 is 1293942787", id == 1293942787L, id.ToString());

            int pid, index;
            bool ok = MacWindowParsing.DecodeWindowId(id, out pid, out index);
            T.Check("window id decodes to pid 1234 index 3", ok && pid == 1234 && index == 3, pid + "/" + index);

            T.Check("decode of 0 is invalid", !MacWindowParsing.DecodeWindowId(0, out pid, out index));
            T.Check("decode of -5 is invalid", !MacWindowParsing.DecodeWindowId(-5, out pid, out index));
            T.Check("encode with index 0 is 0", MacWindowParsing.EncodeWindowId(1234, 0) == 0);
            T.Check("encode with pid 0 is 0", MacWindowParsing.EncodeWindowId(0, 1) == 0);
        }

        private static void ParsesWindowRowsAndSkipsBadLines()
        {
            var rows = MacWindowParsing.ParseWindowRows(TwoWindows);
            T.Check("two good window rows parsed", rows.Count == 2, rows.Count.ToString());
            if (rows.Count != 2) return;

            T.Check("row 1 bounds parsed", rows[0].Index == 1 && rows[0].Bounds.X == 0 && rows[0].Bounds.Width == 800,
                rows[0].Index + " " + rows[0].Bounds.Width);
            T.Check("row 1 minimised and visible flags", rows[0].Minimised && rows[0].Visible);
            T.Check("row 2 bounds and flags parsed", rows[1].Index == 2 && rows[1].Bounds.X == 10 && rows[1].Bounds.Y == 10
                && !rows[1].Minimised && rows[1].Visible);

            T.Check("index 0 row skipped", MacWindowParsing.ParseWindowRows("0|1|2|3|4|false|true").Count == 0);
            T.Check("non-numeric row skipped", MacWindowParsing.ParseWindowRows("3|x|1|2|3|false|true").Count == 0);
            T.Check("short row skipped", MacWindowParsing.ParseWindowRows("4|1|2|3").Count == 0);
            T.Check("empty text gives no rows", MacWindowParsing.ParseWindowRows("").Count == 0);
        }

        private static void CocoaToTopLeftConversion()
        {
            T.Check("cocoa y=0 h=100 on primary H=900 gives y=800", MacWindowParsing.TopLeftY(900, 0, 100) == 800,
                MacWindowParsing.TopLeftY(900, 0, 100).ToString());

            ScreenRect r = MacWindowParsing.ToTopLeft(900, 10, 20, 300, 100);
            T.Check("cocoa rect (10,20,300,100) on H=900 gives top-left (10,780,300,100)",
                r.X == 10 && r.Y == 780 && r.Width == 300 && r.Height == 100,
                r.X + "," + r.Y + "," + r.Width + "," + r.Height);
        }

        private static void ParsesScreensIntoMonitors()
        {
            const string screens = "0|0|0|1440|900|0|25|1440|875|2|1\n" +
                                   "1|1440|-200|1920|1080|1440|-200|1920|1080|1|0\n";
            var rows = MacWindowParsing.ParseScreenRows(screens);
            T.Check("two screen rows parsed", rows.Count == 2, rows.Count.ToString());

            var monitors = MacWindowParsing.ToMonitors(rows);
            T.Check("two monitors produced", monitors.Count == 2, monitors.Count.ToString());
            if (monitors.Count != 2) return;

            MonitorInfo primary = monitors[0];
            T.Check("primary monitor bounds are the full top-left rect",
                primary.IsPrimary && primary.Bounds.X == 0 && primary.Bounds.Y == 0 && primary.Bounds.Width == 1440 && primary.Bounds.Height == 900);
            T.Check("primary work area drops the menu bar and dock strip",
                primary.WorkArea.Y == 0 && primary.WorkArea.Height == 875, primary.WorkArea.Y + "/" + primary.WorkArea.Height);
            T.Check("retina scale carried through", primary.ScaleFactor == 2.0);

            MonitorInfo second = monitors[1];
            T.Check("secondary monitor above the primary converts to top-left y=20",
                !second.IsPrimary && second.Bounds.X == 1440 && second.Bounds.Y == 20 && second.Bounds.Height == 1080,
                second.Bounds.X + "," + second.Bounds.Y + "," + second.Bounds.Height);
        }

        private static void ContentBoundsRemoveTitleBar()
        {
            ScreenRect content;
            bool ok = MacWindowParsing.ToContentBounds(new ScreenRect(100, 200, 800, 600), out content);
            T.Check("content bounds drop the 28 px title bar", ok && content.X == 100 && content.Y == 228
                && content.Width == 800 && content.Height == 572, content.Y + "/" + content.Height);
            T.Check("frame shorter than the title bar has no content area",
                !MacWindowParsing.ToContentBounds(new ScreenRect(0, 0, 100, 20), out content));
        }

        private static void MainWindowPrefersFirstWhenNotMinimised()
        {
            var runner = new WindowFakeRunner { WindowsText = "1|0|0|800|600|false|true\n2|10|10|800|600|false|true" };
            var svc = new MacWindowService(runner);
            T.Check("main window is window 1 when it is not minimised",
                svc.GetMainWindow(1234) == MacWindowParsing.EncodeWindowId(1234, 1));
        }

        private static void MainWindowFallsBackWhenFirstMinimised()
        {
            var runner = new WindowFakeRunner { WindowsText = TwoWindows };
            var svc = new MacWindowService(runner);
            T.Check("main window falls back to the first window when window 1 is minimised",
                svc.GetMainWindow(1234) == MacWindowParsing.EncodeWindowId(1234, 1));

            var noWindows = new MacWindowService(new WindowFakeRunner { WindowsText = "" });
            T.Check("main window of a process with no windows is 0", noWindows.GetMainWindow(1234) == 0);
        }

        private static void WindowsOfProcessIncludesExtraPids()
        {
            var runner = new WindowFakeRunner { WindowsText = "1|0|0|800|600|false|true", ExtraPidText = "1|0|0|300|300|false|true" };
            var svc = new MacWindowService(runner, pid => new List<int> { 5678 });
            IReadOnlyList<long> ids = svc.GetWindowsOfProcess(1234);

            T.Check("windows of pid include the one from the extra pid",
                ids.Contains(MacWindowParsing.EncodeWindowId(1234, 1)) && ids.Contains(MacWindowParsing.EncodeWindowId(5678, 1)),
                ids.Count.ToString());
        }

        private static void ExistsAndBoundsFromListing()
        {
            var runner = new WindowFakeRunner { WindowsText = TwoWindows };
            var svc = new MacWindowService(runner);
            long second = MacWindowParsing.EncodeWindowId(1234, 2);
            long third = MacWindowParsing.EncodeWindowId(1234, 3);

            T.Check("window 2 exists", svc.Exists(second));
            T.Check("window 3 does not exist", !svc.Exists(third));
            T.Check("window 1 reports minimised", svc.IsMinimised(MacWindowParsing.EncodeWindowId(1234, 1)));
            T.Check("window 2 is visible and not minimised", svc.IsVisible(second) && !svc.IsMinimised(second));

            ScreenRect bounds;
            T.Check("bounds of window 2 read from listing", svc.TryGetBounds(second, out bounds)
                && bounds.X == 10 && bounds.Y == 10 && bounds.Width == 800 && bounds.Height == 600);
            T.Check("bounds of a missing window fail", !svc.TryGetBounds(third, out bounds));
        }

        private static void ForegroundWindowIsWindowOneOfFrontmostProcess()
        {
            var runner = new WindowFakeRunner { FrontmostText = "4321\n" };
            var svc = new MacWindowService(runner);
            T.Check("foreground window is window 1 of the frontmost pid",
                svc.GetForegroundWindow() == MacWindowParsing.EncodeWindowId(4321, 1));

            var none = new MacWindowService(new WindowFakeRunner { FrontmostText = "" });
            T.Check("foreground window is 0 when no process is frontmost", none.GetForegroundWindow() == 0);
        }

        private static void MinimiseAndRestoreUseAxMinimised()
        {
            var runner = new WindowFakeRunner { WindowsText = TwoWindows };
            var svc = new MacWindowService(runner);
            long second = MacWindowParsing.EncodeWindowId(1234, 2);

            T.Check("restore succeeds on exit 0", svc.Restore(second));
            string restoreScript = runner.LastScript;
            T.Check("restore sets AXMinimized to false on window 2",
                restoreScript.Contains("AXMinimized") && restoreScript.Contains("window 2") && restoreScript.Contains("to false"));

            T.Check("minimise succeeds on exit 0", svc.Minimise(second));
            T.Check("minimise sets AXMinimized to true on window 2",
                runner.LastScript.Contains("AXMinimized") && runner.LastScript.Contains("to true"));

            T.Check("bring to front raises window 2 through AXRaise", svc.BringToFront(second)
                && runner.LastScript.Contains("AXRaise") && runner.LastScript.Contains("window 2"));
        }

        private static void SetBoundsSetsPositionAndSize()
        {
            var runner = new WindowFakeRunner();
            var svc = new MacWindowService(runner);
            bool ok = svc.SetBounds(MacWindowParsing.EncodeWindowId(1234, 1), new ScreenRect(100, 200, 800, 600));
            T.Check("set bounds succeeds", ok);
            T.Check("set bounds script carries position and size",
                runner.LastScript.Contains("{100, 200}") && runner.LastScript.Contains("{800, 600}"), runner.LastScript);
        }

        private static void FailedOsascriptReturnsFalseOrEmpty()
        {
            var runner = new WindowFakeRunner { Failure = new CommandResult(-1, "", "not started", false, true) };
            var svc = new MacWindowService(runner);
            long id = MacWindowParsing.EncodeWindowId(1234, 1);

            T.Check("failed to start: no windows", svc.GetWindowsOfProcess(1234).Count == 0);
            T.Check("failed to start: window does not exist", !svc.Exists(id));
            T.Check("failed to start: restore is false", !svc.Restore(id));
            T.Check("failed to start: bring to front is false", !svc.BringToFront(id));
            T.Check("failed to start: foreground is 0", svc.GetForegroundWindow() == 0);
            T.Check("failed to start: no monitors", svc.GetMonitors().Count == 0);
        }

        private static void NonZeroExitMakesRestoreFalse()
        {
            var runner = new WindowFakeRunner { Failure = new CommandResult(1, "", "Not authorized to send Apple events", false, false) };
            var svc = new MacWindowService(runner);
            T.Check("exit 1 (automation denied) makes restore false",
                !svc.Restore(MacWindowParsing.EncodeWindowId(1234, 1)));
        }

        private static void MonitorsFromInjectedScreenSource()
        {
            var svc = new MacWindowService(new WindowFakeRunner(),
                null, new WindowFakeScreens { Text = "0|0|0|1440|900|0|25|1440|875|2|1\n" });
            IReadOnlyList<MonitorInfo> monitors = svc.GetMonitors();
            T.Check("monitors come from the injected screen source", monitors.Count == 1 && monitors[0].Bounds.Height == 900);

            var missing = new MacWindowService(new WindowFakeRunner(), null, new WindowFakeScreens { Text = null });
            T.Check("no screen text gives no monitors", missing.GetMonitors().Count == 0);
        }

        /// <summary>Canned osascript output. Chooses the answer from which script was sent.</summary>
        public sealed class WindowFakeRunner : ICommandRunner
        {
            public string WindowsText = "";
            public string ExtraPidText = null;
            public string FrontmostText = "";
            public CommandResult Failure;
            public string LastScript = "";

            public CommandResult Run(string fileName, IReadOnlyList<string> arguments, int timeoutMs)
            {
                string script = arguments[arguments.Count - 1];
                LastScript = script;
                if (Failure != null) return Failure;
                if (script.Contains("count of windows"))
                {
                    if (ExtraPidText != null && script.Contains("unix id is 5678")) return Ok(ExtraPidText);
                    return Ok(WindowsText);
                }
                if (script.Contains("frontmost is true")) return Ok(FrontmostText);
                return Ok("");
            }
        }

        public sealed class WindowFakeScreens : IMacScreenSource
        {
            public string Text;
            public string ReadScreens() { return Text; }
        }
    }
}
