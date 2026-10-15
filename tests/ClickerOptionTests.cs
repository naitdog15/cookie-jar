// ClickerOptionTests.cs - the Advanced option "Pause auto-click while the hospital route runs" (default off):
// the pause truth table, the settings value round trip (the same helpers MainWindow.Input.cs uses to save and load),
// and which pass results are looked at again soon (ActiveRetryMs) instead of at the normal interval.
// Pure functions only: no window, no input, no UI. C# 5.
using System;

namespace RbxMulti
{
    public static class ClickerOptionTests
    {
        public static void Run(string dir, Action<string, bool, string> check, Action<string> info)
        {
            info("clicker option: hospital pause");

            // (a) Truth table: only "option on AND route running" pauses.
            check("pause: option off, route off -> no pause", !AntiIdleClicker.ShouldPauseForHospital(false, false), "");
            check("pause: option on, route off -> no pause", !AntiIdleClicker.ShouldPauseForHospital(true, false), "");
            check("pause: option off, route on -> no pause", !AntiIdleClicker.ShouldPauseForHospital(false, true), "");
            check("pause: option on, route on -> pause", AntiIdleClicker.ShouldPauseForHospital(true, true), "");

            // The option is off unless the user turned it on.
            check("option: default is off", !AntiIdleClicker.PauseWhileHospital, "");

            // (b) Settings value round trip, as written to and read from minibar.settings (key pausehospital).
            bool on = AntiIdleClicker.ParsePauseWhileHospital(AntiIdleClicker.PauseWhileHospitalSetting(true));
            check("persist: on survives save and load", on, "");
            bool off = AntiIdleClicker.ParsePauseWhileHospital(AntiIdleClicker.PauseWhileHospitalSetting(false));
            check("persist: off survives save and load", !off, "");
            check("persist: written text is true / false", AntiIdleClicker.PauseWhileHospitalSetting(true) == "true" && AntiIdleClicker.PauseWhileHospitalSetting(false) == "false", "");
            check("persist: missing value loads as off", !AntiIdleClicker.ParsePauseWhileHospital(null), "");
            check("persist: unknown text loads as off", !AntiIdleClicker.ParsePauseWhileHospital("yes please"), "");
            check("persist: TRUE (any case) loads as on", AntiIdleClicker.ParsePauseWhileHospital("TRUE"), "");

            // (c) Results that re-check in ActiveRetryMs. The rest keep the normal interval.
            check("retry: 'skipped: another test ran' re-checks soon", AntiIdleClicker.RetrySoon("skipped: another test ran"), "");
            check("retry: 'skipped: hospital route running' re-checks soon", AntiIdleClicker.RetrySoon("skipped: hospital route running"), "");
            check("retry: 'dismissed invite' still re-checks soon", AntiIdleClicker.RetrySoon("dismissed invite"), "");
            check("retry: 'clicked' keeps the normal interval", !AntiIdleClicker.RetrySoon("clicked"), "");
            check("retry: 'button not found' keeps the normal interval", !AntiIdleClicker.RetrySoon("button not found"), "");
            check("retry: 'skipped: user active' keeps its own 3 s path (not this list)", !AntiIdleClicker.RetrySoon("skipped: user active"), "");
        }
    }
}
