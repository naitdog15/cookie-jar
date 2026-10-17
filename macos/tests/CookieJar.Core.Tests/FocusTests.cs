// FocusTests.cs - FocusMode (one client fullscreen, the others held at background priority) and the
// BackgroundEfficiency.FocusPid override it uses. Fakes record calls; nothing touches a real window or process.
using System;
using System.Collections.Generic;
using CookieJar.Core;
using CookieJar.Platform;

namespace CookieJar.Tests
{
    public static class FocusTests
    {
        private sealed class FakeProcesses : IProcessService
        {
            public readonly HashSet<int> Lowered = new HashSet<int>();
            public bool SetBackgroundPriority(int pid) { Lowered.Add(pid); return true; }
            public bool ClearBackgroundPriority(int pid) { Lowered.Remove(pid); return true; }
            public bool IsRunning(int pid) { return true; }
            public long GetWorkingSetBytes(int pid) { return -1; }
            public IReadOnlyList<int> FindByName(string processName) { return new int[0]; }
            public IReadOnlyList<int> GetDescendants(int pid) { return new int[0]; }
            public int KillTree(int pid) { return 0; }
            public bool TryCapWorkingSet(int pid, long maxBytes) { return false; }
            public bool ClearWorkingSetCap(int pid) { return false; }
            public bool IsElevated() { return false; }
        }

        /// <summary>Window id = pid. Two displays: 0..1919 and 1920..4479.</summary>
        private sealed class FakeWindows : IWindowService
        {
            public readonly Dictionary<long, ScreenRect> Bounds = new Dictionary<long, ScreenRect>();
            public readonly List<long> Raised = new List<long>();
            public bool Refuse;
            private readonly MonitorInfo[] _monitors =
            {
                new MonitorInfo(1, new ScreenRect(0, 0, 1920, 1080), new ScreenRect(0, 25, 1920, 1055), 1.0, true),
                new MonitorInfo(2, new ScreenRect(1920, 0, 2560, 1440), new ScreenRect(1920, 25, 2560, 1415), 1.0, false)
            };
            public IReadOnlyList<long> GetWindowsOfProcess(int pid) { return Bounds.ContainsKey(pid) ? new long[] { pid } : new long[0]; }
            public long GetMainWindow(int pid) { return Bounds.ContainsKey(pid) ? pid : 0; }
            public bool Exists(long id) { return Bounds.ContainsKey(id); }
            public bool IsVisible(long id) { return true; }
            public bool IsMinimised(long id) { return false; }
            public bool Restore(long id) { return true; }
            public bool Minimise(long id) { return true; }
            public bool BringToFront(long id) { Raised.Add(id); return true; }
            public long GetForegroundWindow() { return 0; }
            public bool TryGetBounds(long id, out ScreenRect b) { return Bounds.TryGetValue(id, out b); }
            public bool TryGetContentBounds(long id, out ScreenRect b) { return Bounds.TryGetValue(id, out b); }
            public bool SetBounds(long id, ScreenRect b) { if (Refuse || !Bounds.ContainsKey(id)) return false; Bounds[id] = b; return true; }
            public IReadOnlyList<MonitorInfo> GetMonitors() { return _monitors; }
        }

        private static RobloxInstance Inst(int id, int pid, string status)
        {
            RobloxInstance i = new RobloxInstance();
            i.Id = id; i.Pid = pid; i.Status = status; i.StartedUtc = DateTime.UtcNow;
            return i;
        }

        public static void Run()
        {
            FakeProcesses procs = new FakeProcesses();
            FakeWindows win = new FakeWindows();
            win.Bounds[100] = new ScreenRect(2000, 100, 800, 600);   // on the second display
            win.Bounds[200] = new ScreenRect(100, 100, 800, 600);
            BackgroundEfficiency eff = new BackgroundEfficiency(procs);
            eff.ForegroundPid = delegate { return -1; };
            FocusMode focus = new FocusMode(win, eff);
            RobloxInstance a = Inst(1, 100, "Running"), b = Inst(2, 200, "Running"), c = Inst(3, 300, "Running");
            List<RobloxInstance> all = new List<RobloxInstance> { a, b, c };
            int changed = 0;
            focus.Changed += delegate { changed++; };

            T.Check("focus: starts inactive", !focus.Active && !focus.IsFocused(1), "");
            T.Check("focus: a stopped client cannot be focused", !focus.Start(Inst(9, 900, "Stopped"), all) && !focus.Active, "");
            T.Check("focus: Start succeeds", focus.Start(a, all) && focus.IsFocused(1) && !focus.IsFocused(2), "");
            T.Check("focus: the others are slowed at once, no grace period", procs.Lowered.Contains(200) && procs.Lowered.Contains(300) && !procs.Lowered.Contains(100), string.Join(",", procs.Lowered));
            T.Check("focus: the window covers the display it was on", win.Bounds[100].X == 1920 && win.Bounds[100].Width == 2560 && win.Bounds[100].Height == 1440, win.Bounds[100].X + "," + win.Bounds[100].Width);
            T.Check("focus: the window is raised", win.Raised.Contains(100), "");
            T.Check("focus: Changed raised once", changed == 1, "changed=" + changed);

            T.Check("focus: moving to another client restores the first window", focus.Start(b, all) && win.Bounds[100].Width == 800 && win.Bounds[100].X == 2000, win.Bounds[100].Width + "");
            T.Check("focus: the new client is fullscreen on its own display", win.Bounds[200].Width == 1920 && win.Bounds[200].X == 0, "");
            T.Check("focus: the previous client is slowed, the new one is not", procs.Lowered.Contains(100) && !procs.Lowered.Contains(200), string.Join(",", procs.Lowered));

            focus.Stop(all);
            T.Check("focus: Stop ends focus", !focus.Active, "");
            T.Check("focus: Stop restores the window", win.Bounds[200].Width == 800 && win.Bounds[200].X == 100, "");
            T.Check("focus: Stop restores every priority", procs.Lowered.Count == 0, string.Join(",", procs.Lowered));
            int before = changed;
            focus.Stop(all);
            T.Check("focus: Stop when inactive does nothing", changed == before, "");

            // The OS refusing to move a window must not stop the CPU side.
            win.Refuse = true;
            List<string> logs = new List<string>();
            focus.Log = delegate(string s) { logs.Add(s); };
            T.Check("focus: refused move still focuses and slows the others", focus.Start(a, all) && procs.Lowered.Contains(200), "");
            T.Check("focus: refused move is logged", logs.Count >= 1, "logs=" + logs.Count);
            focus.Stop(all);

            // FocusPid works while the background feature is off, and ending it still restores.
            eff.Enabled = false;
            focus.Start(c, all);
            T.Check("focus: applies even with background saving off", procs.Lowered.Contains(100) && procs.Lowered.Contains(200) && !procs.Lowered.Contains(300), string.Join(",", procs.Lowered));
            focus.Stop(all);
            T.Check("focus: restores even with background saving off", procs.Lowered.Count == 0, string.Join(",", procs.Lowered));
        }
    }
}
