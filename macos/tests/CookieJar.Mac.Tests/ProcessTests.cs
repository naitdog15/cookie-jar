using System;
using System.Collections.Generic;
using System.Linq;
using CookieJar.Platform;
using CookieJar.Platform.Mac;

namespace CookieJar.Tests
{
    /// <summary>Command runner that returns one canned result and records every call.</summary>
    internal sealed class FakeCommandRunner : ICommandRunner
    {
        public CommandResult Next = new CommandResult(0, "", "", false, false);
        public readonly List<string> Calls = new List<string>();
        public readonly List<IReadOnlyList<string>> Arguments = new List<IReadOnlyList<string>>();

        public CommandResult Run(string fileName, IReadOnlyList<string> arguments, int timeoutMs)
        {
            Calls.Add(fileName);
            Arguments.Add(arguments);
            return Next;
        }
    }

    /// <summary>libc stand-in: alive set, signal log, nice values and a uid.</summary>
    internal sealed class FakeMacNative : IMacProcessNative
    {
        public readonly HashSet<int> Alive = new HashSet<int>();
        public readonly List<string> Signals = new List<string>();
        public readonly Dictionary<int, int> Nice = new Dictionary<int, int>();
        public bool PriorityWorks = true;
        public int Uid = 501;

        public bool Kill(int pid, int signal)
        {
            Signals.Add(pid + ":" + signal);
            if (signal == 15) Alive.Remove(pid);
            return true;
        }

        public bool SetPriority(int pid, int nice)
        {
            if (!PriorityWorks) return false;
            Nice[pid] = nice;
            return true;
        }

        public bool IsAlive(int pid) { return Alive.Contains(pid); }

        public int GetUid() { return Uid; }
    }

    public static class ProcessTests
    {
        // Canned ps output: pid, ppid, rss (KB), comm. Lines 4 and 6 are malformed.
        private const string PsSample =
            "    1     0   12048 /sbin/launchd\n" +
            "  120     1    5120 /Applications/Roblox.app/Contents/MacOS/RobloxPlayer\n" +
            "  121   120  204800 /Applications/Some Tool.app/Contents/MacOS/Some Tool\n" +
            "this line is malformed\n" +
            "  130   120    1024 /usr/bin/tail\n" +
            "  131   130       x /bin/broken\n";

        // Tree 1 -> 2 -> (3, 4), 4 -> 5
        private const string TreeSample =
            "1 0 100 /sbin/launchd\n" +
            "2 1 100 /usr/bin/parent\n" +
            "3 2 100 /usr/bin/a\n" +
            "4 2 100 /usr/bin/b\n" +
            "5 4 100 /usr/bin/c\n";

        private static string Join(IEnumerable<int> values)
        {
            return string.Join(",", values.Select(v => v.ToString()));
        }

        private static string JoinStrings(IEnumerable<string> values)
        {
            return string.Join(",", values);
        }

        private static MacProcessService Service(FakeCommandRunner runner, FakeMacNative native)
        {
            return new MacProcessService(runner, native, _ => { }, 0);
        }

        private static FakeCommandRunner PsRunner(string text)
        {
            return new FakeCommandRunner { Next = new CommandResult(0, text, "", false, false) };
        }

        public static void Run()
        {
            ParseSampleTests();
            DescendantTests();
            ServiceQueryTests();
            KillTreeTests();
            PriorityAndCapTests();
        }

        private static void ParseSampleTests()
        {
            List<MacProcessRow> rows = MacProcessParsing.ParsePs(PsSample);
            string pids = Join(rows.Select(r => r.Pid));
            T.Check("ps parse keeps the 4 well-formed rows", rows.Count == 4, "count=" + rows.Count);
            T.Check("ps parse pids are 1,120,121,130", pids == "1,120,121,130", pids);

            MacProcessRow player = rows.First(r => r.Pid == 120);
            T.Check("rss is KB converted to bytes (5120 KB)", player.RssBytes == 5242880L, player.RssBytes.ToString());
            T.Check("basename of comm path", player.Name == "RobloxPlayer", player.Name);

            MacProcessRow tool = rows.First(r => r.Pid == 121);
            T.Check("comm with spaces keeps full name", tool.Name == "Some Tool", tool.Name);
            T.Check("comm with spaces rss converted", tool.RssBytes == 209715200L, tool.RssBytes.ToString());

            T.Check("malformed and non-numeric rows are skipped", rows.All(r => r.Pid != 131), "pid 131 had rss x");
            T.Check("ppid parsed", rows.First(r => r.Pid == 130).ParentPid == 120);
        }

        private static void DescendantTests()
        {
            List<MacProcessRow> rows = MacProcessParsing.ParsePs(TreeSample);

            string desc = Join(MacProcessParsing.Descendants(rows, 2));
            T.Check("descendants of 2 are post-order [3,5,4], root excluded", desc == "3,5,4", desc);

            string withRoot = Join(MacProcessParsing.TreeLeavesFirst(rows, 2));
            T.Check("tree leaves first from 2 is [3,5,4,2] (root last)", withRoot == "3,5,4,2", withRoot);

            string none = Join(MacProcessParsing.Descendants(rows, 5));
            T.Check("leaf has no descendants", none == "", none);

            List<MacProcessRow> cycle = MacProcessParsing.ParsePs("10 11 100 /a\n11 10 100 /b\n");
            string cyc = Join(MacProcessParsing.Descendants(cycle, 10));
            T.Check("cycle in ppid links terminates with [11]", cyc == "11", cyc);
        }

        private static void ServiceQueryTests()
        {
            var native = new FakeMacNative();
            native.Alive.Add(120);
            MacProcessService svc = Service(PsRunner(PsSample), native);

            T.Check("IsRunning true for a live pid", svc.IsRunning(120));
            T.Check("IsRunning false for an absent pid", !svc.IsRunning(999));
            T.Check("IsRunning false for pid 0", !svc.IsRunning(0));

            // A zombie (exited, not yet reaped) still answers kill(pid, 0) but the contract says it is not running.
            var zombieNative = new FakeMacNative();
            zombieNative.Alive.Add(120);
            FakeCommandRunner zombiePs = new FakeCommandRunner { Next = new CommandResult(0, "Z+\n", "", false, false) };
            T.Check("IsRunning false for a zombie (ps state Z)", !Service(zombiePs, zombieNative).IsRunning(120));
            T.Check("IsRunning asks ps for the state of that pid only",
                zombiePs.Arguments.Count == 1 && string.Join(" ", zombiePs.Arguments[0]) == "-o stat= -p 120", zombiePs.Arguments.Count == 0 ? "no call" : string.Join(" ", zombiePs.Arguments[0]));
            FakeCommandRunner sleepingPs = new FakeCommandRunner { Next = new CommandResult(0, "S\n", "", false, false) };
            T.Check("IsRunning true for a sleeping process (ps state S)", Service(sleepingPs, zombieNative).IsRunning(120));
            FakeCommandRunner brokenPs = new FakeCommandRunner { Next = new CommandResult(-1, "", "no ps", false, true) };
            T.Check("IsRunning still true when ps itself fails (kill(pid,0) is the fallback)", Service(brokenPs, zombieNative).IsRunning(120));

            T.Check("working set is rss in bytes", svc.GetWorkingSetBytes(120) == 5242880L, svc.GetWorkingSetBytes(120).ToString());
            T.Check("working set is -1 for an absent pid (contract)", svc.GetWorkingSetBytes(999) == -1L);

            T.Check("FindByName is case-insensitive", Join(svc.FindByName("robloxplayer")) == "120");
            T.Check("FindByName drops a trailing .app", Join(svc.FindByName("RobloxPlayer.app")) == "120");
            T.Check("FindByName matches a name with spaces", Join(svc.FindByName("Some Tool")) == "121");
            T.Check("FindByName is exact, not a prefix", svc.FindByName("Roblox").Count == 0);

            string underPlayer = Join(svc.GetDescendants(120));
            T.Check("GetDescendants of 120 is [121,130] (root excluded)", underPlayer == "121,130", underPlayer);
            string underLaunchd = Join(svc.GetDescendants(1));
            T.Check("GetDescendants of 1 is post-order [121,130,120]", underLaunchd == "121,130,120", underLaunchd);

            var failing = Service(new FakeCommandRunner { Next = new CommandResult(1, "", "bad", false, false) }, native);
            T.Check("ps failure gives -1 working set", failing.GetWorkingSetBytes(120) == -1L);
            T.Check("ps failure gives no names", failing.FindByName("RobloxPlayer").Count == 0);
            T.Check("ps failure gives no descendants", failing.GetDescendants(1).Count == 0);
        }

        private static void KillTreeTests()
        {
            var native = new FakeMacNative();
            native.Alive.UnionWith(new[] { 2, 3, 4, 5 });
            var runner = PsRunner(TreeSample);
            MacProcessService svc = Service(runner, native);

            int killed = svc.KillTree(2);
            string signals = JoinStrings(native.Signals);
            T.Check("KillTree(2) signals children first and root last, SIGTERM only",
                signals == "3:15,5:15,4:15,2:15", signals);
            T.Check("KillTree(2) counts the four processes gone", killed == 4, killed.ToString());
            T.Check("KillTree leaves nothing alive", !native.Alive.Any(), Join(native.Alive));

            var native1 = new FakeMacNative();
            native1.Alive.UnionWith(new[] { 1, 2, 3 });
            var svc1 = Service(PsRunner(TreeSample), native1);
            T.Check("KillTree(1) returns 0", svc1.KillTree(1) == 0);
            T.Check("KillTree(1) sends no signal", native1.Signals.Count == 0);

            var native0 = new FakeMacNative();
            var svc0 = Service(PsRunner(TreeSample), native0);
            T.Check("KillTree(0) returns 0 and sends no signal", svc0.KillTree(0) == 0 && native0.Signals.Count == 0);

            int own = System.Diagnostics.Process.GetCurrentProcess().Id;
            var nativeOwn = new FakeMacNative();
            nativeOwn.Alive.Add(own);
            var svcOwn = Service(PsRunner(TreeSample), nativeOwn);
            T.Check("KillTree(own pid) returns 0", svcOwn.KillTree(own) == 0);
            T.Check("KillTree(own pid) sends no signal", nativeOwn.Signals.Count == 0);

            var stubborn = new FakeMacNative();
            stubborn.Alive.UnionWith(new[] { 2, 3 });
            var svcStubborn = new MacProcessService(PsRunner(TreeSample), new StubbornNative(stubborn), _ => { }, 0);
            int stubbornKilled = svcStubborn.KillTree(2);
            T.Check("survivors of SIGTERM get SIGKILL", stubborn.Signals.Contains("2:9") && stubborn.Signals.Contains("3:9"),
                JoinStrings(stubborn.Signals));
            T.Check("SIGKILL survivors are counted", stubbornKilled == 2, stubbornKilled.ToString());

            var rootOnly = new FakeMacNative();
            rootOnly.Alive.Add(2);
            var svcRoot = Service(new FakeCommandRunner { Next = new CommandResult(1, "", "", false, false) }, rootOnly);
            T.Check("KillTree with ps failing still kills the root", svcRoot.KillTree(2) == 1 && JoinStrings(rootOnly.Signals) == "2:15");
        }

        private static void PriorityAndCapTests()
        {
            var native = new FakeMacNative();
            native.Alive.Add(120);
            MacProcessService svc = Service(PsRunner(PsSample), native);

            T.Check("SetBackgroundPriority returns true", svc.SetBackgroundPriority(120));
            T.Check("SetBackgroundPriority sets nice 10", native.Nice.ContainsKey(120) && native.Nice[120] == 10,
                native.Nice.ContainsKey(120) ? native.Nice[120].ToString() : "none");
            T.Check("ClearBackgroundPriority returns true", svc.ClearBackgroundPriority(120));
            T.Check("ClearBackgroundPriority sets nice 0", native.Nice[120] == 0, native.Nice[120].ToString());
            T.Check("priority refuses pid 1", !svc.SetBackgroundPriority(1) && !native.Nice.ContainsKey(1));

            native.PriorityWorks = false;
            T.Check("SetBackgroundPriority false when native fails", !svc.SetBackgroundPriority(120));
            T.Check("ClearBackgroundPriority false when native fails", !svc.ClearBackgroundPriority(120));
            native.PriorityWorks = true;

            T.Check("TryCapWorkingSet is false on macOS", !svc.TryCapWorkingSet(120, 1024L * 1024L));
            T.Check("ClearWorkingSetCap is false on macOS", !svc.ClearWorkingSetCap(120));

            T.Check("IsElevated is false for uid 501", !svc.IsElevated());
            native.Uid = 0;
            T.Check("IsElevated is true for uid 0", svc.IsElevated());

            T.Check("Process capabilities are BackgroundPriority|MultiInstance",
                MacCapabilities.Process == (PlatformCapabilities.BackgroundPriority | PlatformCapabilities.MultiInstance));
            T.Check("Process capabilities omit MemoryCap", (MacCapabilities.Process & PlatformCapabilities.MemoryCap) == 0);
        }

        /// <summary>Native that never lets a process die on SIGTERM; only SIGKILL works.</summary>
        private sealed class StubbornNative : IMacProcessNative
        {
            private readonly FakeMacNative inner;
            public StubbornNative(FakeMacNative inner) { this.inner = inner; }

            public bool Kill(int pid, int signal)
            {
                inner.Signals.Add(pid + ":" + signal);
                if (signal == 9) inner.Alive.Remove(pid);
                return true;
            }

            public bool SetPriority(int pid, int nice) { return inner.SetPriority(pid, nice); }
            public bool IsAlive(int pid) { return inner.IsAlive(pid); }
            public int GetUid() { return inner.GetUid(); }
        }
    }
}
