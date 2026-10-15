using System;
using System.Collections.Generic;
using System.Linq;
using CookieJar.Platform;
using CookieJar.Platform.Mac;

namespace CookieJar.Tests
{
    /// <summary>Process service stand-in for the launcher: FindByName returns the scripted lists in order.</summary>
    internal sealed class FakeScriptedProcesses : IProcessService
    {
        public readonly List<IReadOnlyList<int>> Scripted = new List<IReadOnlyList<int>>();
        private int calls;

        public int FindCalls { get { return calls; } }

        public IReadOnlyList<int> FindByName(string processName)
        {
            if (Scripted.Count == 0) return new List<int>();
            int index = Math.Min(calls, Scripted.Count - 1);
            calls++;
            return Scripted[index];
        }

        public bool IsRunning(int pid) { return false; }
        public long GetWorkingSetBytes(int pid) { return -1; }
        public IReadOnlyList<int> GetDescendants(int pid) { return new List<int>(); }
        public int KillTree(int pid) { return 0; }
        public bool SetBackgroundPriority(int pid) { return false; }
        public bool ClearBackgroundPriority(int pid) { return false; }
        public bool TryCapWorkingSet(int pid, long maxBytes) { return false; }
        public bool ClearWorkingSetCap(int pid) { return false; }
        public bool IsElevated() { return false; }
    }

    public static class RobloxTests
    {
        private static string Join(IEnumerable<string> values)
        {
            return string.Join("|", values);
        }

        private static MacRobloxLocator Locator(string[] dirs, string[] files, string home,
            Dictionary<string, string[]> listings = null, List<string> deleted = null)
        {
            var dirSet = new HashSet<string>(dirs);
            var fileSet = new HashSet<string>(files);
            var listed = listings ?? new Dictionary<string, string[]>();
            return new MacRobloxLocator(
                p => dirSet.Contains(p),
                p => fileSet.Contains(p),
                home,
                null,
                p => listed.ContainsKey(p) ? listed[p] : new string[0],
                p =>
                {
                    if (deleted != null) deleted.Add(p);
                    return true;
                });
        }

        public static void Run()
        {
            LocatorTests();
            LoginStateTests();
            LauncherTests();
        }

        private static void LocatorTests()
        {
            const string player = "/Applications/Roblox.app/Contents/MacOS/RobloxPlayer";
            MacRobloxLocator system = Locator(
                new[] { "/Applications/Roblox.app" }, new[] { player }, "/Users/tester");
            T.Check("system install root is /Applications/Roblox.app", system.FindInstallRoot() == "/Applications/Roblox.app", system.FindInstallRoot() ?? "null");
            T.Check("player path is Contents/MacOS/RobloxPlayer", system.FindPlayerPath() == player, system.FindPlayerPath() ?? "null");
            T.Check("IsInstalled true with the player present", system.IsInstalled());
            T.Check("player process name is RobloxPlayer", system.PlayerProcessName == "RobloxPlayer");

            string userApp = "/Users/tester/Applications/Roblox.app";
            MacRobloxLocator user = Locator(new[] { userApp }, new[] { userApp + "/Contents/MacOS/RobloxPlayer" }, "/Users/tester");
            T.Check("falls back to ~/Applications/Roblox.app", user.FindInstallRoot() == userApp, user.FindInstallRoot() ?? "null");

            MacRobloxLocator both = Locator(new[] { "/Applications/Roblox.app", userApp }, new[] { player }, "/Users/tester");
            T.Check("system install wins over the user install", both.FindInstallRoot() == "/Applications/Roblox.app");

            MacRobloxLocator none = Locator(new string[0], new string[0], "/Users/tester");
            T.Check("not installed gives null root", none.FindInstallRoot() == null);
            T.Check("not installed gives null player path", none.FindPlayerPath() == null);
            T.Check("not installed means IsInstalled false", !none.IsInstalled());

            MacRobloxLocator noPlayer = Locator(new[] { "/Applications/Roblox.app" }, new string[0], "/Users/tester");
            T.Check("app folder without the player is not installed", noPlayer.FindPlayerPath() == null && !noPlayer.IsInstalled());

            var sharedDirs = new List<string>();
            var sharedFiles = new HashSet<string>();
            MacRobloxLocator cached = new MacRobloxLocator(p => sharedDirs.Contains(p), p => sharedFiles.Contains(p), "/Users/tester", null);
            T.Check("first lookup finds nothing", cached.FindInstallRoot() == null);
            sharedDirs.Add("/Applications/Roblox.app");
            sharedFiles.Add(player);
            T.Check("lookup stays memoised until InvalidateCache", cached.FindInstallRoot() == null);
            cached.InvalidateCache();
            T.Check("InvalidateCache makes the next lookup rescan", cached.FindInstallRoot() == "/Applications/Roblox.app");

            string[] caches = { "/Users/tester/Library/Roblox", "/Users/tester/Library/Caches/com.roblox.RobloxPlayer" };
            MacRobloxLocator withCaches = Locator(caches, new string[0], "/Users/tester");
            string dirs = Join(withCaches.GetCacheDirectories());
            T.Check("cache directories list only existing folders",
                dirs == Join(caches), dirs);
            T.Check("no cache directories when none exist", Locator(new string[0], new string[0], "/Users/tester").GetCacheDirectories().Count == 0);
        }

        private static void LoginStateTests()
        {
            const string cookies = "/Users/tester/Library/Cookies/com.roblox.RobloxPlayer.binarycookies";
            const string storageA = "/Users/tester/Library/HTTPStorages/com.roblox.RobloxPlayer.abc";
            var listings = new Dictionary<string, string[]>
            {
                ["/Users/tester/Library/HTTPStorages"] = new[]
                {
                    storageA,
                    "/Users/tester/Library/HTTPStorages/com.apple.Safari"
                }
            };

            var deleted = new List<string>();
            MacRobloxLocator locator = Locator(new string[0], new[] { cookies }, "/Users/tester", listings, deleted);
            T.Check("login state detected", locator.HasLoginState());

            IReadOnlyList<string> removed = locator.ClearLoginState();
            T.Check("clear removes only Roblox storage and the cookie file",
                Join(deleted) == storageA + "|" + cookies, Join(deleted));
            T.Check("clear reports two removals", removed.Count == 2, removed.Count.ToString());
            T.Check("clear descriptions name the path", removed.Count > 0 && removed[0].Contains(storageA));

            MacRobloxLocator clean = Locator(new string[0], new string[0], "/Users/tester",
                new Dictionary<string, string[]>(), new List<string>());
            T.Check("no login state when nothing exists", !clean.HasLoginState());
            T.Check("clear with nothing to remove returns empty", clean.ClearLoginState().Count == 0);
        }

        private static CommandResult Ok(string stdout = "")
        {
            return new CommandResult(0, stdout, "", false, false);
        }

        private static void LauncherTests()
        {
            const string app = "/Applications/Roblox.app";
            string player = app + "/Contents/MacOS/RobloxPlayer";
            const string url = "roblox://experiences/start?placeId=1";

            // Opens once; the new pid 200 was not running before open.
            var runner = new FakeCommandRunner { Next = Ok() };
            var procs = new FakeScriptedProcesses();
            procs.Scripted.Add(new List<int> { 100 });
            procs.Scripted.Add(new List<int> { 100, 200 });
            var launcher = new MacRobloxLauncher(runner, procs, Locator(new[] { app }, new[] { player }, "/Users/tester"), _ => { }, 0);

            LaunchResult ok = launcher.LaunchUrl(url, 1000);
            T.Check("LaunchUrl reports the new pid", ok.Started && ok.Pid == 200, "started=" + ok.Started + " pid=" + ok.Pid);
            T.Check("LaunchUrl has no error on success", ok.Error == null);
            T.Check("LaunchUrl runs open -n <url>",
                runner.Calls.Count == 1 && runner.Calls[0] == "/usr/bin/open" &&
                Join(runner.Arguments[0]) == "-n|" + url, runner.Calls.Count > 0 ? Join(runner.Arguments[0]) : "no call");

            var launch = new FakeCommandRunner { Next = Ok() };
            var launchProcs = new FakeScriptedProcesses();
            launchProcs.Scripted.Add(new List<int>());
            launchProcs.Scripted.Add(new List<int> { 300 });
            var launchNew = new MacRobloxLauncher(launch, launchProcs, Locator(new[] { app }, new[] { player }, "/Users/tester"), _ => { }, 0);
            LaunchResult viaLaunch = launchNew.Launch(new[] { "--flag", "value" }, 1000);
            T.Check("Launch reports the new pid", viaLaunch.Started && viaLaunch.Pid == 300, "pid=" + viaLaunch.Pid);
            T.Check("Launch runs open -n -a <app> --args <arguments>",
                Join(launch.Arguments[0]) == "-n|-a|" + app + "|--args|--flag|value", Join(launch.Arguments[0]));

            var failRunner = new FakeCommandRunner { Next = new CommandResult(1, "", "LaunchServices refused", false, false) };
            var failProcs = new FakeScriptedProcesses();
            failProcs.Scripted.Add(new List<int>());
            var failLauncher = new MacRobloxLauncher(failRunner, failProcs, Locator(new[] { app }, new[] { player }, "/Users/tester"), _ => { }, 0);
            LaunchResult failed = failLauncher.LaunchUrl(url, 1000);
            T.Check("open failure gives Started=false", !failed.Started && failed.Pid == 0);
            T.Check("open failure has an error that names the cause",
                !string.IsNullOrEmpty(failed.Error) && failed.Error.Contains("LaunchServices refused"), failed.Error ?? "null");
            T.Check("open failure only takes the before-snapshot, no polling", failProcs.FindCalls == 1, failProcs.FindCalls.ToString());

            var timeoutRunner = new FakeCommandRunner { Next = new CommandResult(-1, "", "", true, false) };
            var timeoutLauncher = new MacRobloxLauncher(timeoutRunner, new FakeScriptedProcesses(), Locator(new[] { app }, new[] { player }, "/Users/tester"), _ => { }, 0);
            LaunchResult timedOut = timeoutLauncher.LaunchUrl(url, 1000);
            T.Check("open timeout gives Started=false with an error", !timedOut.Started && !string.IsNullOrEmpty(timedOut.Error), timedOut.Error ?? "null");

            var stillRunning = new FakeScriptedProcesses();
            stillRunning.Scripted.Add(new List<int> { 100 });
            var noNew = new MacRobloxLauncher(new FakeCommandRunner { Next = Ok() }, stillRunning, Locator(new[] { app }, new[] { player }, "/Users/tester"), _ => { }, 10);
            LaunchResult none = noNew.LaunchUrl(url, 100);
            T.Check("no new pid within the timeout gives Started=false", !none.Started && none.Pid == 0 && !string.IsNullOrEmpty(none.Error), none.Error ?? "null");
            T.Check("no-new-pid polls the timeout / poll-step times", stillRunning.FindCalls == 11, stillRunning.FindCalls.ToString());

            var reuse = new FakeScriptedProcesses();
            reuse.Scripted.Add(new List<int> { 100 });
            reuse.Scripted.Add(new List<int> { 100 });
            var reuseLauncher = new MacRobloxLauncher(new FakeCommandRunner { Next = Ok() }, reuse, Locator(new[] { app }, new[] { player }, "/Users/tester"), _ => { }, 0);
            T.Check("an already running pid is not reported as new", !reuseLauncher.LaunchUrl(url, 0).Started);

            var badRunner = new FakeCommandRunner { Next = Ok() };
            var badLauncher = new MacRobloxLauncher(badRunner, new FakeScriptedProcesses(), Locator(new[] { app }, new[] { player }, "/Users/tester"), _ => { }, 0);
            LaunchResult https = badLauncher.LaunchUrl("https://www.roblox.com/games/1", 1000);
            T.Check("non-roblox scheme gives Started=false with an error", !https.Started && !string.IsNullOrEmpty(https.Error), https.Error ?? "null");
            T.Check("non-roblox scheme never runs open", badRunner.Calls.Count == 0);
            LaunchResult empty = badLauncher.LaunchUrl("", 1000);
            T.Check("empty URL gives Started=false with an error", !empty.Started && !string.IsNullOrEmpty(empty.Error), empty.Error ?? "null");
            LaunchResult nullUrl = badLauncher.LaunchUrl(null, 1000);
            T.Check("null URL gives Started=false with an error", !nullUrl.Started && !string.IsNullOrEmpty(nullUrl.Error));
            T.Check("empty and null URLs never run open", badRunner.Calls.Count == 0);

            var legacyRunner = new FakeCommandRunner { Next = Ok() };
            var legacyProcs = new FakeScriptedProcesses();
            legacyProcs.Scripted.Add(new List<int>());
            legacyProcs.Scripted.Add(new List<int> { 400 });
            var legacy = new MacRobloxLauncher(legacyRunner, legacyProcs, Locator(new[] { app }, new[] { player }, "/Users/tester"), _ => { }, 0);
            LaunchResult legacyResult = legacy.LaunchUrl("roblox-player:1+launchmode:play", 1000);
            T.Check("roblox-player: scheme is accepted and reports the pid", legacyResult.Started && legacyResult.Pid == 400 && legacyRunner.Calls.Count == 1);

            var noInstall = new MacRobloxLauncher(new FakeCommandRunner { Next = Ok() }, new FakeScriptedProcesses(),
                Locator(new string[0], new string[0], "/Users/tester"), _ => { }, 0);
            LaunchResult missing = noInstall.Launch(new[] { "x" }, 1000);
            T.Check("Launch with Roblox not installed gives Started=false", !missing.Started && !string.IsNullOrEmpty(missing.Error));

            var multi = new MacRobloxLauncher(new FakeCommandRunner { Next = Ok() }, new FakeScriptedProcesses(),
                Locator(new[] { app }, new[] { player }, "/Users/tester"), _ => { }, 0);
            T.Check("PrepareForAnotherInstance is true", multi.PrepareForAnotherInstance());
            T.Check("ReleaseSingleInstanceLock returns 0", multi.ReleaseSingleInstanceLock(200) == 0);
            T.Check("CountSingleInstanceLocks returns 0", multi.CountSingleInstanceLocks(200) == 0);
        }
    }
}
