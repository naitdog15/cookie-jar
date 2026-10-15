using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text.RegularExpressions;

namespace CookieJar.Core
{
    /// <summary>What ProfileRunner does with one account before it starts it.</summary>
    public enum LaunchAction
    {
        /// <summary>Already running: leave it alone and move on.</summary>
        Skip,
        /// <summary>Hardware has not been measured yet: stop the whole profile.</summary>
        StopHardwareUnknown,
        /// <summary>The restriction forbids another client: stop the whole profile.</summary>
        Block,
        /// <summary>A warning applies and was not confirmed yet: ask the user first.</summary>
        ConfirmWarn,
        /// <summary>Start the account.</summary>
        Launch
    }

    /// <summary>
    /// Pure launch decisions of ProfileRunner and TicketLauncher: validation, URI building, stagger timing and the
    /// messages the launcher prints. No HTTP, no process starts. Inputs are plain values so Core needs no
    /// restriction or profile types.
    /// </summary>
    public static class LaunchPlanner
    {
        /// <summary>Default pause between accounts, in seconds.</summary>
        public const int DefaultStagger = LaunchProfile.DefaultStagger;

        /// <summary>Largest allowed pause between accounts, in seconds.</summary>
        public const int MaxStagger = 300;

        /// <summary>How long a new client may take to appear before the launch counts as timed out.</summary>
        public const int LaunchTimeoutSeconds = 60;

        public const string HardwareUnknownMessage = "Hardware is still being detected. Try again in a moment.";

        private static readonly Regex TicketPattern = new Regex(@"^[A-Za-z0-9_\-\.=]{8,4000}$");
        private static readonly Regex UserIdPattern = new Regex("\"id\"\\s*:\\s*(\\d+)");
        private static readonly Regex UserNamePattern = new Regex("\"name\"\\s*:\\s*\"([A-Za-z0-9_]{1,40})\"");

        // ---------------- tickets and URIs ----------------

        /// <summary>True when the ticket has the shape Roblox issues (8 to 4000 letters, digits, _ - . =).</summary>
        public static bool IsValidTicket(string ticket)
        {
            return ticket != null && TicketPattern.IsMatch(ticket);
        }

        /// <summary>Maps a random value in [0, 1) to a 12-digit browser tracker id, like the real web client.</summary>
        public static long BrowserTrackerIdFromUnit(double unit)
        {
            return 100000000000L + (long)(unit * 899999999999.0);
        }

        /// <summary>Milliseconds since the Unix epoch.</summary>
        public static long UnixMs(DateTime utcNow)
        {
            return (long)(utcNow - new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc)).TotalMilliseconds;
        }

        /// <summary>Builds the roblox-player: URI. The ticket must already satisfy IsValidTicket.</summary>
        public static string BuildLaunchUri(string ticket, long placeId, long browserTrackerId, long launchTimeMs)
        {
            string id = browserTrackerId.ToString(CultureInfo.InvariantCulture);
            string place = placeId.ToString(CultureInfo.InvariantCulture);
            string placeLauncher = "https://assetgame.roblox.com/game/PlaceLauncher.ashx?request=RequestGame"
                + "&browserTrackerId=" + id + "&placeId=" + place + "&isPlayTogetherGame=false";
            return "roblox-player:1+launchmode:play+gameinfo:" + ticket
                + "+launchtime:" + launchTimeMs.ToString(CultureInfo.InvariantCulture)
                + "+placelauncherurl:" + Uri.EscapeDataString(placeLauncher)
                + "+browsertrackerid:" + id
                + "+robloxLocale:en_us+gameLocale:en_us+channel:+LaunchExp:InApp";
        }

        /// <summary>Extracts id and name from {"id":1,"name":"x",...}. Returns false when either is missing.</summary>
        public static bool ParseUser(string json, out long userId, out string username)
        {
            userId = 0;
            username = null;
            if (string.IsNullOrEmpty(json)) return false;
            Match mi = UserIdPattern.Match(json);
            Match mn = UserNamePattern.Match(json);
            if (!mi.Success || !mn.Success) return false;
            if (!long.TryParse(mi.Groups[1].Value, NumberStyles.None, CultureInfo.InvariantCulture, out userId)) return false;
            username = mn.Groups[1].Value;
            return userId > 0;
        }

        // ---------------- stagger ----------------

        /// <summary>True when a stagger value typed by the user is allowed (0 to MaxStagger seconds).</summary>
        public static bool IsValidStagger(int seconds)
        {
            return seconds >= 0 && seconds <= MaxStagger;
        }

        /// <summary>Seconds to wait after an account, or 0. Only after a successful start, and never after the last account.</summary>
        public static int StaggerSecondsAfter(bool launchedOk, bool moreAccounts, int staggerSeconds)
        {
            return launchedOk && moreAccounts && staggerSeconds > 0 ? staggerSeconds : 0;
        }

        /// <summary>Same as StaggerSecondsAfter, using the profile's own stagger.</summary>
        public static int StaggerSecondsAfter(bool launchedOk, bool moreAccounts, LaunchProfile profile)
        {
            return StaggerSecondsAfter(launchedOk, moreAccounts, profile == null ? 0 : profile.StaggerSeconds);
        }

        public static string StaggerCountdownMessage(int launched, int total, int secondsLeft)
        {
            return "Launched " + Inv(launched) + "/" + Inv(total) + ". Next account in " + Inv(secondsLeft) + "s...";
        }

        // ---------------- order and per-account decisions ----------------

        /// <summary>Indices of the accounts to start, in profile order, skipping the ones already running.</summary>
        public static IList<int> PendingIndices(IList<bool> alreadyRunning)
        {
            List<int> pending = new List<int>();
            for (int i = 0; i < alreadyRunning.Count; i++)
            {
                if (!alreadyRunning[i]) pending.Add(i);
            }
            return pending;
        }

        public static string LaunchingMessage(int index, int total, string who)
        {
            return "Launching " + Inv(index + 1) + "/" + Inv(total) + ": " + who + "...";
        }

        /// <summary>
        /// Decides what to do with one account. restrictionKnown is false while hardware is still measured;
        /// canLaunch and isWarn come from the restriction check; warnConfirmed is true once the user has
        /// accepted a warning during this profile run.
        /// </summary>
        public static LaunchAction Decide(bool alreadyRunning, bool restrictionKnown, bool canLaunch, bool isWarn, bool warnConfirmed)
        {
            if (alreadyRunning) return LaunchAction.Skip;
            if (!restrictionKnown) return LaunchAction.StopHardwareUnknown;
            if (!canLaunch) return LaunchAction.Block;
            if (isWarn && !warnConfirmed) return LaunchAction.ConfirmWarn;
            return LaunchAction.Launch;
        }

        /// <summary>Same as the primitive Decide, with a restriction check result. A null result means hardware is not measured yet.</summary>
        public static LaunchAction Decide(bool alreadyRunning, RestrictResult restriction, bool warnConfirmed)
        {
            if (restriction == null) return Decide(alreadyRunning, false, false, false, warnConfirmed);
            return Decide(alreadyRunning, true, restriction.CanLaunch, restriction.Level == RestrictLevel.Warn, warnConfirmed);
        }

        public static string BlockedMessage(string restrictionMessage, string who)
        {
            return restrictionMessage + " Stopped before launching " + who + ".";
        }

        public static string BlockedMessage(RestrictResult restriction, string who)
        {
            return BlockedMessage(restriction == null ? null : restriction.Message, who);
        }

        public static string TimedOutMessage(string who)
        {
            return who + ": timed out - no new Roblox window within " + Inv(LaunchTimeoutSeconds) + " seconds "
                + "(if it appears later, use Adopt running clients)";
        }

        /// <summary>The one-line result of a profile run. Per-account lines go to the log.</summary>
        public static string Summary(bool stopped, int ok, int failed, int skipped)
        {
            return (stopped ? "Stopped. " : "Done. ") + Inv(ok) + " launched, " + Inv(failed) + " failed, " + Inv(skipped) + " skipped.";
        }

        private static string Inv(int value)
        {
            return value.ToString(CultureInfo.InvariantCulture);
        }
    }
}
