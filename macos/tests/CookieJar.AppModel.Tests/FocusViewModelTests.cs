// FocusViewModelTests.cs - the Focus button's view-model: feature gating, toggling, one focused card at a time, and
// Focus ending when the focused client stops. Fakes only; no window or process is touched.
using System;
using System.Collections.Generic;
using CookieJar.AppModel;
using CookieJar.Core;
using CookieJar.Platform;

namespace CookieJar.Tests
{
    public static class FocusViewModelTests
    {
        private sealed class Procs : IProcessService
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

        private sealed class Wins : IWindowService
        {
            private readonly ScreenRect _bounds = new ScreenRect(10, 10, 640, 480);
            private readonly MonitorInfo[] _monitors = { new MonitorInfo(1, new ScreenRect(0, 0, 1920, 1080), new ScreenRect(0, 25, 1920, 1055), 1.0, true) };
            public IReadOnlyList<long> GetWindowsOfProcess(int pid) { return new long[] { pid }; }
            public long GetMainWindow(int pid) { return pid; }
            public bool Exists(long id) { return true; }
            public bool IsVisible(long id) { return true; }
            public bool IsMinimised(long id) { return false; }
            public bool Restore(long id) { return true; }
            public bool Minimise(long id) { return true; }
            public bool BringToFront(long id) { return true; }
            public long GetForegroundWindow() { return 0; }
            public bool TryGetBounds(long id, out ScreenRect b) { b = _bounds; return true; }
            public bool TryGetContentBounds(long id, out ScreenRect b) { b = _bounds; return true; }
            public bool SetBounds(long id, ScreenRect b) { return true; }
            public IReadOnlyList<MonitorInfo> GetMonitors() { return _monitors; }
        }

        private sealed class Plat : IPlatform
        {
            public Plat(PlatformCapabilities caps, IProcessService p, IWindowService w) { Capabilities = caps; Processes = p; Windows = w; }
            public string Name { get { return "FocusPlatform"; } }
            public PlatformCapabilities Capabilities { get; }
            public IProcessService Processes { get; }
            public IWindowService Windows { get; }
            public IRobloxLocator RobloxLocator { get { return null; } }
            public IRobloxLauncher RobloxLauncher { get { return null; } }
            public ISecretStore Secrets { get { return null; } }
            public IInputService Input { get { return null; } }
            public IScreenCapture ScreenCapture { get { return null; } }
            public IOcrService Ocr { get { return null; } }
            public IStartupService Startup { get { return null; } }
            public IAppPaths Paths { get { return null; } }
            public IShellService Shell { get { return null; } }
        }

        private static RobloxInstance Inst(int id, int pid)
        {
            RobloxInstance i = new RobloxInstance();
            i.Id = id; i.Pid = pid; i.Status = "Running"; i.StartedUtc = DateTime.UtcNow;
            return i;
        }

        public static void Run()
        {
            PlatformCapabilities both = PlatformCapabilities.WindowPlacement | PlatformCapabilities.BackgroundPriority;
            Procs procs = new Procs();
            MainViewModel vm = new MainViewModel(new Plat(both, procs, new Wins()));
            RobloxInstance a = Inst(1, 100), b = Inst(2, 200);
            InstanceCardViewModel ca = new InstanceCardViewModel(a, new Plat(both, procs, new Wins()));
            InstanceCardViewModel cb = new InstanceCardViewModel(b, new Plat(both, procs, new Wins()));
            vm.Instances.Add(ca);
            vm.Instances.Add(cb);

            T.Check("focus vm: enabled when window placement and background priority exist", ca.FocusFeature.Enabled && ca.FocusCommand.CanExecute(null), ca.FocusFeature.Reason);
            T.Check("focus vm: label starts as Focus", ca.FocusLabel == "Focus" && !ca.IsFocused, "");

            List<string> ev = new List<string>();
            ca.PropertyChanged += (s, e) => ev.Add(e.PropertyName);
            ca.FocusCommand.Execute(null);
            T.Check("focus vm: command focuses this card", ca.IsFocused && ca.FocusLabel == "Unfocus" && !cb.IsFocused, "");
            T.Check("focus vm: IsFocused and FocusLabel raised", ev.Contains("IsFocused") && ev.Contains("FocusLabel"), string.Join(",", ev));
            T.Check("focus vm: the other client is slowed", procs.Lowered.Contains(200) && !procs.Lowered.Contains(100), string.Join(",", procs.Lowered));

            cb.FocusCommand.Execute(null);
            T.Check("focus vm: focusing another card moves Focus", cb.IsFocused && !ca.IsFocused && procs.Lowered.Contains(100) && !procs.Lowered.Contains(200), string.Join(",", procs.Lowered));

            cb.FocusCommand.Execute(null);
            T.Check("focus vm: command again unfocuses and restores", !cb.IsFocused && procs.Lowered.Count == 0, string.Join(",", procs.Lowered));

            ca.FocusCommand.Execute(null);
            b.Status = "Stopped";
            T.Check("focus vm: a stopped client's card does not end Focus on another", ca.IsFocused, "");
            a.Status = "Stopped";
            T.Check("focus vm: the focused client stopping ends Focus and restores the others", !ca.IsFocused && procs.Lowered.Count == 0, string.Join(",", procs.Lowered));

            Plat noWin = new Plat(PlatformCapabilities.BackgroundPriority, procs, new Wins());
            InstanceCardViewModel c1 = new InstanceCardViewModel(Inst(3, 300), noWin);
            T.Check("focus vm: no window placement disables Focus with a reason", !c1.FocusFeature.Enabled && c1.FocusFeature.Reason.Contains("window placement"), c1.FocusFeature.Reason);
            Plat noPrio = new Plat(PlatformCapabilities.WindowPlacement, procs, new Wins());
            InstanceCardViewModel c2 = new InstanceCardViewModel(Inst(4, 400), noPrio);
            T.Check("focus vm: no background priority disables Focus with a reason", !c2.FocusFeature.Enabled && c2.FocusFeature.Reason.Contains("background priority"), c2.FocusFeature.Reason);
            T.Check("focus vm: a card with no Focus attached cannot run the command", !c2.FocusCommand.CanExecute(null), "");
            MainViewModel bare = new MainViewModel(new Plat(PlatformCapabilities.None, null, null));
            T.Check("focus vm: no services means no Focus mode (and no crash)", bare.Focus == null && bare.Efficiency == null, "");
        }
    }
}
