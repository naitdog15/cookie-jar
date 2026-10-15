using System;
using System.Collections.Generic;
using CookieJar.Core;

namespace CookieJar.Tests
{
    public static class LaunchPlannerTests
    {
        public static void Run()
        {
            // ---- tickets
            T.Check("IsValidTicket: 7 characters is rejected", !LaunchPlanner.IsValidTicket(new string('a', 7)), "");
            T.Check("IsValidTicket: 8 characters is accepted", LaunchPlanner.IsValidTicket(new string('a', 8)), "");
            T.Check("IsValidTicket: 4000 characters is accepted", LaunchPlanner.IsValidTicket(new string('a', 4000)), "");
            T.Check("IsValidTicket: 4001 characters is rejected", !LaunchPlanner.IsValidTicket(new string('a', 4001)), "");
            T.Check("IsValidTicket: null is rejected", !LaunchPlanner.IsValidTicket(null), "");
            T.Check("IsValidTicket: letters, digits, _ - . = are accepted", LaunchPlanner.IsValidTicket("Ab_9-.=x"), "");
            T.Check("IsValidTicket: a space is rejected", !LaunchPlanner.IsValidTicket("abc def1"), "");
            T.Check("IsValidTicket: ! is rejected", !LaunchPlanner.IsValidTicket("abcdefg!"), "");
            T.Check("IsValidTicket: / is rejected", !LaunchPlanner.IsValidTicket("abcdefg/"), "");
            T.Check("IsValidTicket: a non-ASCII letter is rejected", !LaunchPlanner.IsValidTicket("abcdefég"), "");

            // ---- launch URI, exact string (src/TicketLauncher.cs BuildLaunchUri)
            string uri = LaunchPlanner.BuildLaunchUri("abcdefgh12", 920587237L, 123456789012L, 1700000000000L);
            string expected = "roblox-player:1+launchmode:play+gameinfo:abcdefgh12+launchtime:1700000000000"
                + "+placelauncherurl:https%3A%2F%2Fassetgame.roblox.com%2Fgame%2FPlaceLauncher.ashx%3Frequest%3DRequestGame"
                + "%26browserTrackerId%3D123456789012%26placeId%3D920587237%26isPlayTogetherGame%3Dfalse"
                + "+browsertrackerid:123456789012+robloxLocale:en_us+gameLocale:en_us+channel:+LaunchExp:InApp";
            T.Check("BuildLaunchUri: exact string", uri == expected, uri);

            // ---- time and tracker id
            T.Check("UnixMs: the epoch is 0", LaunchPlanner.UnixMs(new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc)) == 0, "");
            T.Check("UnixMs: 2000-01-01 is 946684800000", LaunchPlanner.UnixMs(new DateTime(2000, 1, 1, 0, 0, 0, DateTimeKind.Utc)) == 946684800000L, "");
            T.Check("BrowserTrackerIdFromUnit: 0 gives the lowest 12-digit id", LaunchPlanner.BrowserTrackerIdFromUnit(0) == 100000000000L, "");
            long high = LaunchPlanner.BrowserTrackerIdFromUnit(0.999999999);
            T.Check("BrowserTrackerIdFromUnit: values stay 12 digits", high >= 100000000000L && high < 1000000000000L, high.ToString());

            // ---- ParseUser
            long uid;
            string uname;
            T.Check("ParseUser: reads id and name", LaunchPlanner.ParseUser("{\"id\":12345,\"name\":\"Good_User\",\"displayName\":\"x\"}", out uid, out uname)
                && uid == 12345 && uname == "Good_User", uid + " " + uname);
            T.Check("ParseUser: missing name is rejected", !LaunchPlanner.ParseUser("{\"id\":12345}", out uid, out uname), "");
            T.Check("ParseUser: id 0 is rejected", !LaunchPlanner.ParseUser("{\"id\":0,\"name\":\"x_y\"}", out uid, out uname), "");
            T.Check("ParseUser: a name with a dash is rejected", !LaunchPlanner.ParseUser("{\"id\":5,\"name\":\"bad-name\"}", out uid, out uname), "");
            T.Check("ParseUser: null or empty is rejected", !LaunchPlanner.ParseUser(null, out uid, out uname) && !LaunchPlanner.ParseUser("", out uid, out uname), "");

            // ---- stagger
            T.Check("Stagger: default is 8 seconds", LaunchPlanner.DefaultStagger == 8, "");
            T.Check("Stagger: 0 and 300 are valid, 301 and -1 are not",
                LaunchPlanner.IsValidStagger(0) && LaunchPlanner.IsValidStagger(300) && !LaunchPlanner.IsValidStagger(301) && !LaunchPlanner.IsValidStagger(-1), "");
            T.Check("Stagger: waits after a successful start with more accounts to come", LaunchPlanner.StaggerSecondsAfter(true, true, 8) == 8, "");
            T.Check("Stagger: no wait after a failed start", LaunchPlanner.StaggerSecondsAfter(false, true, 8) == 0, "");
            T.Check("Stagger: no wait after the last account", LaunchPlanner.StaggerSecondsAfter(true, false, 8) == 0, "");
            T.Check("Stagger: no wait when the stagger is 0", LaunchPlanner.StaggerSecondsAfter(true, true, 0) == 0, "");
            T.Check("Stagger: countdown text", LaunchPlanner.StaggerCountdownMessage(1, 3, 5) == "Launched 1/3. Next account in 5s...",
                LaunchPlanner.StaggerCountdownMessage(1, 3, 5));

            // ---- order and per-account decisions
            IList<int> pending = LaunchPlanner.PendingIndices(new List<bool> { true, false, false });
            T.Check("Order: running accounts are skipped, profile order kept", pending.Count == 2 && pending[0] == 1 && pending[1] == 2, "count=" + pending.Count);
            T.Check("Order: launching message", LaunchPlanner.LaunchingMessage(0, 3, "A") == "Launching 1/3: A...", LaunchPlanner.LaunchingMessage(0, 3, "A"));

            T.Check("Decide: a running account is skipped even when the restriction is unknown",
                LaunchPlanner.Decide(true, false, false, true, false) == LaunchAction.Skip, "");
            T.Check("Decide: unknown hardware stops the profile",
                LaunchPlanner.Decide(false, false, true, false, false) == LaunchAction.StopHardwareUnknown, "");
            T.Check("Decide: a restriction that cannot launch blocks",
                LaunchPlanner.Decide(false, true, false, false, false) == LaunchAction.Block, "");
            T.Check("Decide: a warning asks the user the first time",
                LaunchPlanner.Decide(false, true, true, true, false) == LaunchAction.ConfirmWarn, "");
            T.Check("Decide: a confirmed warning launches without asking again",
                LaunchPlanner.Decide(false, true, true, true, true) == LaunchAction.Launch, "");
            T.Check("Decide: no restriction launches", LaunchPlanner.Decide(false, true, true, false, false) == LaunchAction.Launch, "");

            // ---- RestrictResult and LaunchProfile overloads (src/Restrictor.cs, src/LaunchProfile.cs)
            T.Check("Decide(result): a running account is skipped", LaunchPlanner.Decide(true, RestrictResult.Create(RestrictLevel.Allow, "Ready", "ok"), false) == LaunchAction.Skip, "");
            T.Check("Decide(result): null result means hardware unknown",
                LaunchPlanner.Decide(false, (RestrictResult)null, false) == LaunchAction.StopHardwareUnknown, "");
            T.Check("Decide(result): Block level stops the account",
                LaunchPlanner.Decide(false, RestrictResult.Create(RestrictLevel.Block, "Hard limit reached", "x"), false) == LaunchAction.Block, "");
            T.Check("Decide(result): Warn level asks the user first",
                LaunchPlanner.Decide(false, RestrictResult.Create(RestrictLevel.Warn, "Low memory", "x"), false) == LaunchAction.ConfirmWarn, "");
            T.Check("Decide(result): confirmed Warn level launches",
                LaunchPlanner.Decide(false, RestrictResult.Create(RestrictLevel.Warn, "Low memory", "x"), true) == LaunchAction.Launch, "");
            T.Check("Decide(result): Allow level launches",
                LaunchPlanner.Decide(false, RestrictResult.Create(RestrictLevel.Allow, "Ready", "x"), false) == LaunchAction.Launch, "");
            T.Check("BlockedMessage(result): uses the restriction message",
                LaunchPlanner.BlockedMessage(RestrictResult.Create(RestrictLevel.Block, "t", "Not enough memory."), "Account A")
                == "Not enough memory. Stopped before launching Account A.", "");

            T.Check("DefaultStagger equals LaunchProfile.DefaultStagger", LaunchPlanner.DefaultStagger == LaunchProfile.DefaultStagger, "");
            LaunchProfile profile = new LaunchProfile();
            profile.StaggerSeconds = 12;
            T.Check("StaggerSecondsAfter(profile): returns the profile stagger after a successful start",
                LaunchPlanner.StaggerSecondsAfter(true, true, profile) == profile.StaggerSeconds, "");
            T.Check("StaggerSecondsAfter(profile): no wait after the last account",
                LaunchPlanner.StaggerSecondsAfter(true, false, profile) == 0, "");

            T.Check("Messages: blocked text", LaunchPlanner.BlockedMessage("Not enough RAM.", "Account A") == "Not enough RAM. Stopped before launching Account A.",
                LaunchPlanner.BlockedMessage("Not enough RAM.", "Account A"));
            T.Check("Messages: hardware text", LaunchPlanner.HardwareUnknownMessage == "Hardware is still being detected. Try again in a moment.", "");
            T.Check("Messages: timeout is 60 seconds", LaunchPlanner.LaunchTimeoutSeconds == 60
                && LaunchPlanner.TimedOutMessage("A") == "A: timed out - no new Roblox window within 60 seconds (if it appears later, use Adopt running clients)",
                LaunchPlanner.TimedOutMessage("A"));
            T.Check("Summary: done line", LaunchPlanner.Summary(false, 2, 1, 0) == "Done. 2 launched, 1 failed, 0 skipped.", LaunchPlanner.Summary(false, 2, 1, 0));
            T.Check("Summary: stopped line", LaunchPlanner.Summary(true, 0, 0, 3) == "Stopped. 0 launched, 0 failed, 3 skipped.", LaunchPlanner.Summary(true, 0, 0, 3));
        }
    }
}
