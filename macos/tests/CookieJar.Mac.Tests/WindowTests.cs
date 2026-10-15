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
            WindowIdValidity();
            ParsesWindowRowsAndSkipsBadLines();
            CocoaToTopLeftConversion();
            ParsesScreensIntoMonitors();
            ContentBoundsRemoveTitleBarExceptFullScreen();
            ScriptsAreBuiltFromNumbersOnly();
            MainWindowIsFrontmostOnScreenAppWindow();
            MainWindowFallsBackWhenNothingIsOnScreen();
            WindowsOfProcessIncludesExtraPidsAndSkipsOverlays();
            ExistsAndBoundsFromListing();
            IdentityDoesNotChangeWhenOtherWindowsReorder();
            UnknownIdIsFoundByScanningEveryProcess();
            ForegroundWindowIsMainWindowOfFrontmostProcess();
            MinimiseRestoreAndRaiseTargetTheWindowByBounds();
            SetBoundsSetsPositionAndSize();
            FailedOsascriptReturnsFalseOrEmpty();
            NonZeroExitMakesRestoreFalse();
            MonitorsFromInjectedScreenSource();
            ContentBoundsOfFullScreenWindowUseMonitors();
        }

        private static CommandResult Ok(string output)
        {
            return new CommandResult(0, output, "", false, false);
        }

        // id|pid|layer|x|y|w|h|onscreen. 101 is minimised (off screen), 102 is on screen, 103 is a layer-25 overlay.
        private const string ThreeWindows =
            "101|1234|0|0|0|800|600|0\r\n\r\nbogus line\r\n102|1234|0|10|10|800|600|1\r\n103|1234|25|0|0|800|30|1\r\n";

        private static void WindowIdValidity()
        {
            T.Check("window id 1 is valid", MacWindowParsing.IsValidWindowId(1));
            T.Check("window id 4294967295 (largest CGWindowID) is valid", MacWindowParsing.IsValidWindowId(4294967295L));
            T.Check("window id 4294967296 does not fit 32 bits", !MacWindowParsing.IsValidWindowId(4294967296L));
            T.Check("window id 0 and negative ids are invalid", !MacWindowParsing.IsValidWindowId(0) && !MacWindowParsing.IsValidWindowId(-5));
        }

        private static void ParsesWindowRowsAndSkipsBadLines()
        {
            var rows = MacWindowParsing.ParseWindowRows(ThreeWindows);
            T.Check("three good window rows parsed", rows.Count == 3, rows.Count.ToString());
            if (rows.Count != 3) return;

            T.Check("row 101 fields parsed", rows[0].Id == 101 && rows[0].Pid == 1234 && rows[0].Layer == 0
                && rows[0].Bounds.X == 0 && rows[0].Bounds.Width == 800 && !rows[0].OnScreen);
            T.Check("row 102 is on screen at (10,10)", rows[1].Id == 102 && rows[1].OnScreen && rows[1].Bounds.X == 10 && rows[1].Bounds.Y == 10);
            T.Check("normal windows are app windows, the layer-25 overlay is not", rows[0].IsAppWindow && rows[1].IsAppWindow && !rows[2].IsAppWindow);

            T.Check("window id 0 row skipped", MacWindowParsing.ParseWindowRows("0|1|0|1|2|3|4|1").Count == 0);
            T.Check("window id above 32 bits skipped", MacWindowParsing.ParseWindowRows("4294967296|1|0|1|2|3|4|1").Count == 0);
            T.Check("pid 0 row skipped", MacWindowParsing.ParseWindowRows("7|0|0|1|2|3|4|1").Count == 0);
            T.Check("non-numeric row skipped", MacWindowParsing.ParseWindowRows("3|1|0|x|1|2|3|1").Count == 0);
            T.Check("short row skipped", MacWindowParsing.ParseWindowRows("4|1|0|3").Count == 0);
            T.Check("old 7-field index format skipped", MacWindowParsing.ParseWindowRows("1|0|0|800|600|true|true").Count == 0);
            T.Check("empty text gives no rows", MacWindowParsing.ParseWindowRows("").Count == 0);

            var odd = MacWindowParsing.ParseWindowRows("9|55|-2147483623|0|0|10|10|0");
            T.Check("a negative window layer (desktop level) parses and is not an app window", odd.Count == 1 && odd[0].Layer == -2147483623 && !odd[0].IsAppWindow);
            var tiny = MacWindowParsing.ParseWindowRows("10|55|0|5|5|20|20|1");
            T.Check("a layer-0 window smaller than 32 points is not an app window", tiny.Count == 1 && !tiny[0].IsAppWindow);
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

        private static void ContentBoundsRemoveTitleBarExceptFullScreen()
        {
            ScreenRect content;
            bool ok = MacWindowParsing.ToContentBounds(new ScreenRect(100, 200, 800, 600), null, out content);
            T.Check("a titled window loses the 28 pt title bar", ok && content.X == 100 && content.Y == 228
                && content.Width == 800 && content.Height == 572, content.Y + "/" + content.Height);
            T.Check("frame shorter than the title bar has no content area",
                !MacWindowParsing.ToContentBounds(new ScreenRect(0, 0, 100, 20), null, out content));
            T.Check("an empty frame has no content area", !MacWindowParsing.ToContentBounds(new ScreenRect(0, 0, 0, 0), null, out content));

            var monitors = new List<MonitorInfo> { new MonitorInfo(0, new ScreenRect(0, 0, 1440, 900), new ScreenRect(0, 25, 1440, 875), 2.0, true) };
            ok = MacWindowParsing.ToContentBounds(new ScreenRect(0, 0, 1440, 900), monitors, out content);
            T.Check("a window covering a whole display (full screen / borderless) keeps its full frame",
                ok && content.Y == 0 && content.Height == 900, content.Y + "/" + content.Height);
            ok = MacWindowParsing.ToContentBounds(new ScreenRect(1, 1, 1439, 899), monitors, out content);
            T.Check("within 2 points of a display still counts as covering it", ok && content.Y == 1 && content.Height == 899, content.Y + "/" + content.Height);
            ok = MacWindowParsing.ToContentBounds(new ScreenRect(0, 0, 1440, 700), monitors, out content);
            T.Check("a window that is not display sized still loses the title bar", ok && content.Y == 28 && content.Height == 672, content.Y + "/" + content.Height);
        }

        private static void ScriptsAreBuiltFromNumbersOnly()
        {
            string list = MacWindowParsing.WindowsScript(1234);
            T.Check("window list script targets pid 1234 through CGWindowList", list.Contains("var wanted = 1234;") && list.Contains("CGWindowListCopyWindowInfo"));
            T.Check("pid 0 lists every process, a negative pid is treated as 0",
                MacWindowParsing.WindowsScript(0).Contains("var wanted = 0;") && MacWindowParsing.WindowsScript(-7).Contains("var wanted = 0;"));
            T.Check("window list script has no unfilled placeholder", !list.Contains("@"));

            ScreenRect now = new ScreenRect(10, 20, 800, 600);
            string raise = MacWindowParsing.RaiseScript(4321, now);
            T.Check("raise script names the pid and matches the window by its current bounds",
                raise.Contains("unix id is 4321") && raise.Contains("(10 - 3)") && raise.Contains("(20 + 3)") && raise.Contains("(800 + 3)") && raise.Contains("(600 - 3)") && raise.Contains("AXRaise"), raise);
            T.Check("raise script has no unfilled placeholder and no window index", !raise.Contains("@") && !raise.Contains("window 1 of") && !raise.Contains("window @"));
            T.Check("minimise script sets AXMinimized to true, restore to false",
                MacWindowParsing.SetMinimisedScript(1, now, true).Contains("AXMinimized\" of target to true")
                && MacWindowParsing.SetMinimisedScript(1, now, false).Contains("AXMinimized\" of target to false"));
        }

        private static void MainWindowIsFrontmostOnScreenAppWindow()
        {
            // CGWindowList is front to back: 102 (on screen) comes after the minimised 101 in the text but is the one on screen.
            var runner = new WindowFakeRunner { WindowsText = ThreeWindows };
            var svc = new MacWindowService(runner);
            T.Check("main window is the first on-screen app window (102), not the minimised 101 and not the overlay 103",
                svc.GetMainWindow(1234) == 102, svc.GetMainWindow(1234).ToString());

            var two = new WindowFakeRunner { WindowsText = "201|1234|0|0|0|800|600|1\n202|1234|0|10|10|800|600|1" };
            T.Check("with several on-screen windows the frontmost (first listed) wins", new MacWindowService(two).GetMainWindow(1234) == 201);
        }

        private static void MainWindowFallsBackWhenNothingIsOnScreen()
        {
            var runner = new WindowFakeRunner { WindowsText = "301|1234|0|0|0|800|600|0\n302|1234|0|10|10|800|600|0" };
            var svc = new MacWindowService(runner);
            T.Check("main window falls back to the first app window when none is on screen", svc.GetMainWindow(1234) == 301);

            var noWindows = new MacWindowService(new WindowFakeRunner { WindowsText = "" });
            T.Check("main window of a process with no windows is 0", noWindows.GetMainWindow(1234) == 0);
            var onlyOverlay = new MacWindowService(new WindowFakeRunner { WindowsText = "401|1234|25|0|0|800|30|1" });
            T.Check("a process with only overlay windows has no main window", onlyOverlay.GetMainWindow(1234) == 0);
        }

        private static void WindowsOfProcessIncludesExtraPidsAndSkipsOverlays()
        {
            var runner = new WindowFakeRunner { WindowsText = ThreeWindows + "501|5678|0|0|0|300|300|1\n" };
            var svc = new MacWindowService(runner, pid => new List<int> { 5678 });
            IReadOnlyList<long> ids = svc.GetWindowsOfProcess(1234);

            T.Check("windows of pid include the one from the extra pid", ids.Contains(101) && ids.Contains(102) && ids.Contains(501), string.Join(",", ids));
            T.Check("overlay windows are not listed", !ids.Contains(103));
            T.Check("a pid of 0 lists nothing", svc.GetWindowsOfProcess(0).Count == 0);
        }

        private static void ExistsAndBoundsFromListing()
        {
            var runner = new WindowFakeRunner { WindowsText = ThreeWindows };
            var svc = new MacWindowService(runner);

            T.Check("window 102 exists", svc.Exists(102));
            T.Check("window 999 does not exist", !svc.Exists(999));
            T.Check("window 101 reports minimised (not on screen)", svc.IsMinimised(101) && !svc.IsVisible(101));
            T.Check("window 102 is visible and not minimised", svc.IsVisible(102) && !svc.IsMinimised(102));

            ScreenRect bounds;
            T.Check("bounds of window 102 read from listing", svc.TryGetBounds(102, out bounds)
                && bounds.X == 10 && bounds.Y == 10 && bounds.Width == 800 && bounds.Height == 600);
            T.Check("bounds of a missing window fail", !svc.TryGetBounds(999, out bounds));
        }

        private static void IdentityDoesNotChangeWhenOtherWindowsReorder()
        {
            var runner = new WindowFakeRunner { WindowsText = "601|1234|0|0|0|800|600|1\n602|1234|0|50|60|700|500|1" };
            var svc = new MacWindowService(runner);
            long id = svc.GetMainWindow(1234);          // 601, remembered with its owner
            runner.WindowsText = "602|1234|0|50|60|700|500|1\n";   // window 601 closed; 602 is now first in the list
            ScreenRect bounds;
            T.Check("window 602 keeps its id and bounds after another window closed (index would have shifted)",
                svc.TryGetBounds(602, out bounds) && bounds.X == 50 && bounds.Y == 60 && bounds.Width == 700);
            T.Check("the closed window 601 no longer exists and is not silently replaced by 602", id == 601 && !svc.Exists(601));
        }

        private static void UnknownIdIsFoundByScanningEveryProcess()
        {
            var runner = new WindowFakeRunner { WindowsText = "701|9999|0|0|0|640|480|1" };
            var svc = new MacWindowService(runner);
            ScreenRect bounds;
            T.Check("a window id never seen before is found by scanning all processes", svc.TryGetBounds(701, out bounds) && bounds.Width == 640);
            T.Check("that scan listed every process (wanted 0)", runner.Scripts.Any(s => s.Contains("var wanted = 0;")));
            runner.Scripts.Clear();
            svc.Exists(701);
            T.Check("the next lookup asks only the owning process (wanted 9999)", runner.Scripts.Count == 1 && runner.Scripts[0].Contains("var wanted = 9999;"),
                string.Join(" | ", runner.Scripts.Select(s => s.Substring(s.IndexOf("var wanted"), 20))));
        }

        private static void ForegroundWindowIsMainWindowOfFrontmostProcess()
        {
            var runner = new WindowFakeRunner { FrontmostText = "4321\n", WindowsText = "801|4321|0|0|0|800|600|1\n802|1234|0|0|0|800|600|1" };
            var svc = new MacWindowService(runner);
            T.Check("foreground window is the main window of the frontmost pid", svc.GetForegroundWindow() == 801, svc.GetForegroundWindow().ToString());
            T.Check("foreground process id is the frontmost pid", svc.GetForegroundProcessId() == 4321);

            var none = new MacWindowService(new WindowFakeRunner { FrontmostText = "" });
            T.Check("foreground window is 0 when no process is frontmost", none.GetForegroundWindow() == 0 && none.GetForegroundProcessId() == 0);
        }

        private static void MinimiseRestoreAndRaiseTargetTheWindowByBounds()
        {
            var runner = new WindowFakeRunner { WindowsText = ThreeWindows };
            var svc = new MacWindowService(runner);

            T.Check("restore succeeds on exit 0", svc.Restore(102));
            T.Check("restore sets AXMinimized to false on the window at 10,10 800x600 of pid 1234",
                runner.LastScript.Contains("AXMinimized") && runner.LastScript.Contains("to false") && runner.LastScript.Contains("unix id is 1234")
                && runner.LastScript.Contains("(10 - 3)") && runner.LastScript.Contains("(800 + 3)"), runner.LastScript);

            T.Check("minimise succeeds on exit 0", svc.Minimise(102));
            T.Check("minimise sets AXMinimized to true", runner.LastScript.Contains("AXMinimized") && runner.LastScript.Contains("to true"));

            T.Check("bring to front raises through AXRaise", svc.BringToFront(102) && runner.LastScript.Contains("AXRaise") && runner.LastScript.Contains("set frontmost of p to true"));
            T.Check("no action script carries a window index or title", !runner.LastScript.Contains("window 2") && !runner.LastScript.Contains("name of"));

            int before = runner.Scripts.Count;
            T.Check("an action on a window that does not exist fails without running any action script",
                !svc.Restore(999) && runner.Scripts.Skip(before).All(s => s.Contains("CGWindowListCopyWindowInfo")));
        }

        private static void SetBoundsSetsPositionAndSize()
        {
            var runner = new WindowFakeRunner { WindowsText = ThreeWindows };
            var svc = new MacWindowService(runner);
            bool ok = svc.SetBounds(102, new ScreenRect(100, 200, 800, 600));
            T.Check("set bounds succeeds", ok);
            T.Check("set bounds script carries the new position and size and matches the current bounds",
                runner.LastScript.Contains("{100, 200}") && runner.LastScript.Contains("{800, 600}") && runner.LastScript.Contains("(10 - 3)"), runner.LastScript);
        }

        private static void FailedOsascriptReturnsFalseOrEmpty()
        {
            var runner = new WindowFakeRunner { Failure = new CommandResult(-1, "", "not started", false, true) };
            var svc = new MacWindowService(runner);

            T.Check("failed to start: no windows", svc.GetWindowsOfProcess(1234).Count == 0);
            T.Check("failed to start: window does not exist", !svc.Exists(102));
            T.Check("failed to start: restore is false", !svc.Restore(102));
            T.Check("failed to start: bring to front is false", !svc.BringToFront(102));
            T.Check("failed to start: foreground is 0", svc.GetForegroundWindow() == 0);
            T.Check("failed to start: no monitors", svc.GetMonitors().Count == 0);
        }

        private static void NonZeroExitMakesRestoreFalse()
        {
            var runner = new WindowFakeRunner { WindowsText = ThreeWindows };
            var svc = new MacWindowService(runner);
            runner.ActionFailure = new CommandResult(1, "", "Not authorized to send Apple events", false, false);
            T.Check("exit 1 (automation denied) makes restore false", !svc.Restore(102));
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

        private static void ContentBoundsOfFullScreenWindowUseMonitors()
        {
            var screens = new WindowFakeScreens { Text = "0|0|0|1440|900|0|25|1440|875|2|1\n" };
            var full = new MacWindowService(new WindowFakeRunner { WindowsText = "901|1234|0|0|0|1440|900|1" }, null, screens);
            ScreenRect content;
            T.Check("a full-screen window reports its whole frame as content", full.TryGetContentBounds(901, out content) && content.Y == 0 && content.Height == 900);

            var windowed = new MacWindowService(new WindowFakeRunner { WindowsText = "902|1234|0|100|100|800|600|1" }, null, screens);
            T.Check("a windowed window reports the frame minus the title bar", windowed.TryGetContentBounds(902, out content) && content.Y == 128 && content.Height == 572);
        }

        /// <summary>
        /// Canned osascript output. The window-list script is answered from WindowsText, filtered by `var wanted = N;`
        /// the way the real script filters by pid; the frontmost-process script from FrontmostText; any other script is
        /// an action (raise, minimise, move) that returns ActionFailure or success.
        /// </summary>
        public sealed class WindowFakeRunner : ICommandRunner
        {
            public string WindowsText = "";
            public string FrontmostText = "";
            public CommandResult Failure;
            public CommandResult ActionFailure;
            public string LastScript = "";
            public readonly List<string> Scripts = new List<string>();

            public CommandResult Run(string fileName, IReadOnlyList<string> arguments, int timeoutMs)
            {
                string script = arguments[arguments.Count - 1];
                LastScript = script;
                Scripts.Add(script);
                if (Failure != null) return Failure;
                if (script.Contains("CGWindowListCopyWindowInfo"))
                {
                    int wanted = Wanted(script);
                    var lines = (WindowsText ?? "").Split('\n').Where(l =>
                    {
                        if (wanted == 0) return true;
                        string[] f = l.Trim().Split('|');
                        return f.Length > 1 && f[1] == wanted.ToString();
                    });
                    return Ok(string.Join("\n", lines));
                }
                if (script.Contains("frontmost is true")) return Ok(FrontmostText);
                if (ActionFailure != null) return ActionFailure;
                return Ok("");
            }

            private static int Wanted(string script)
            {
                int i = script.IndexOf("var wanted = ", StringComparison.Ordinal);
                if (i < 0) return 0;
                i += "var wanted = ".Length;
                int j = script.IndexOf(';', i);
                int value;
                return int.TryParse(script.Substring(i, j - i), out value) ? value : 0;
            }
        }

        public sealed class WindowFakeScreens : IMacScreenSource
        {
            public string Text;
            public string ReadScreens() { return Text; }
        }
    }
}
