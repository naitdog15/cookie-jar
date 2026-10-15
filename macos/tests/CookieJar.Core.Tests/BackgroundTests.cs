// BackgroundTests.cs - BackgroundEfficiency (background priority for clients that are not in front), ported from
// tests/BackgroundTests.cs. The Windows version drove a real ping.exe child and read its priority back from Windows;
// here a fake IProcessService records the same calls, so the expected values are the Windows ones.
using System;
using System.Collections.Generic;
using CookieJar.Core;
using CookieJar.Platform;

namespace CookieJar.Tests
{
    public static class BackgroundTests
    {
        /// <summary>Records SetBackgroundPriority / ClearBackgroundPriority per pid. Pids in refuse are not accepted.</summary>
        private sealed class FakeProcessService : IProcessService
        {
            private readonly HashSet<int> _lowered = new HashSet<int>();
            private readonly HashSet<int> _refuse = new HashSet<int>();
            public int SetCalls;

            public void Refuse(int pid) { _refuse.Add(pid); }
            public bool IsLowered(int pid) { return _lowered.Contains(pid); }

            public bool SetBackgroundPriority(int pid)
            {
                SetCalls++;
                if (_refuse.Contains(pid)) return false;
                _lowered.Add(pid);
                return true;
            }

            public bool ClearBackgroundPriority(int pid)
            {
                if (_refuse.Contains(pid)) return false;
                _lowered.Remove(pid);
                return true;
            }

            public bool IsRunning(int pid) { return !_refuse.Contains(pid); }
            public long GetWorkingSetBytes(int pid) { return -1; }
            public IReadOnlyList<int> FindByName(string processName) { return new int[0]; }
            public IReadOnlyList<int> GetDescendants(int pid) { return new int[0]; }
            public int KillTree(int pid) { return 0; }
            public bool TryCapWorkingSet(int pid, long maxBytes) { return false; }
            public bool ClearWorkingSetCap(int pid) { return false; }
            public bool IsElevated() { return false; }
        }

        private static List<EfficiencyTarget> One(int pid, bool running, DateTime started)
        {
            List<EfficiencyTarget> l = new List<EfficiencyTarget>();
            EfficiencyTarget t = new EfficiencyTarget();
            t.Pid = pid; t.Running = running; t.StartedUtc = started;
            l.Add(t);
            return l;
        }

        public static void Run()
        {
            const int pid = 4242;
            DateTime now = new DateTime(2026, 10, 15, 12, 0, 0, DateTimeKind.Utc);
            DateTime old = now.AddSeconds(-(BackgroundEfficiency.GraceSeconds + 5));

            FakeProcessService fake = new FakeProcessService();
            BackgroundEfficiency be = new BackgroundEfficiency(fake);
            be.ForegroundPid = delegate { return 0; };

            be.Update(One(pid, true, now), now);
            T.Check("efficiency: a client inside the start-up grace period is left alone",
                !be.IsThrottled(pid) && !fake.IsLowered(pid), "");

            be.Update(One(pid, false, old), now);
            T.Check("efficiency: a client that is not Running is left alone", !be.IsThrottled(pid) && !fake.IsLowered(pid), "");

            be.Update(One(pid, true, old), now);
            T.Check("efficiency: a Running background client goes to background priority",
                be.IsThrottled(pid) && fake.IsLowered(pid), "");
            T.Check("efficiency: ThrottledCount is 1", be.ThrottledCount == 1, "count=" + be.ThrottledCount);

            be.ForegroundPid = delegate { return pid; };
            be.Update(One(pid, true, old), now);
            T.Check("efficiency: bringing it to the foreground restores normal priority",
                !be.IsThrottled(pid) && !fake.IsLowered(pid), "");

            be.ForegroundPid = delegate { return 0; };
            be.Update(One(pid, true, old), now);
            be.Enabled = false;
            T.Check("efficiency: switching the feature off restores normal priority at once",
                !be.IsThrottled(pid) && !fake.IsLowered(pid), "");
            be.Update(One(pid, true, old), now);
            T.Check("efficiency: while off, Update changes nothing", !be.IsThrottled(pid) && !fake.IsLowered(pid), "");

            be.Enabled = true;
            be.Update(One(pid, true, old), now);
            be.RestoreAll();
            T.Check("efficiency: RestoreAll (launcher exit) puts it back", !be.IsThrottled(pid) && !fake.IsLowered(pid), "");

            // a stopped card that still carries the pid of a running client must not undo its throttle
            List<EfficiencyTarget> twin = One(pid, false, old);
            twin.AddRange(One(pid, true, old));
            int callsBefore = fake.SetCalls;
            be.Update(twin, now);
            be.Update(twin, now);
            T.Check("efficiency: a stopped entry with the same pid as a running client does not undo or thrash it",
                be.IsThrottled(pid) && fake.IsLowered(pid) && fake.SetCalls - callsBefore == 1,
                "calls=" + (fake.SetCalls - callsBefore));

            // when the process is gone (listed as stopped / removed) the state is simply forgotten
            be.Update(One(pid, false, old), now);
            T.Check("efficiency: when the client stops, its state is forgotten without touching the pid again",
                be.ThrottledCount == 0, "count=" + be.ThrottledCount);

            be.Update(One(pid, true, old), now);
            be.Update(new List<EfficiencyTarget>(), now);
            T.Check("efficiency: a client removed from the list is forgotten", be.ThrottledCount == 0, "");
            be.RestoreAll();
            T.Check("efficiency: (cleanup) the client is back at normal priority", !be.IsThrottled(pid) && !fake.IsLowered(pid), "");

            // A pid the OS will not let us touch must not throw, spam the log, or retry every second.
            FakeProcessService refusing = new FakeProcessService();
            refusing.Refuse(999999);
            int logs = 0;
            BackgroundEfficiency bad = new BackgroundEfficiency(refusing);
            bad.ForegroundPid = delegate { return 0; };
            bad.Log = delegate(string s) { logs++; };
            List<EfficiencyTarget> ghost = One(999999, true, old);
            bad.Update(ghost, now); bad.Update(ghost, now); bad.Update(ghost, now);
            T.Check("efficiency: an unreachable pid is not marked throttled and is logged once", !bad.IsThrottled(999999) && logs == 1, "logs=" + logs);
            T.Check("efficiency: an unreachable pid is tried once, not every second", refusing.SetCalls == 1, "calls=" + refusing.SetCalls);
        }
    }
}
