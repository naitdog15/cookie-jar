using System;
using System.Collections.Generic;
using System.ComponentModel;
using CookieJar.AppModel;
using CookieJar.Core;
using CookieJar.Platform;

namespace CookieJar.Tests
{
    internal sealed class FakePlatform : IPlatform
    {
        public FakePlatform(PlatformCapabilities caps) { Capabilities = caps; }
        public string Name { get { return "FakePlatform"; } }
        public PlatformCapabilities Capabilities { get; }
        public IProcessService Processes { get { return null; } }
        public IRobloxLocator RobloxLocator { get { return null; } }
        public IRobloxLauncher RobloxLauncher { get { return null; } }
        public ISecretStore Secrets { get { return null; } }
        public IWindowService Windows { get { return null; } }
        public IInputService Input { get { return null; } }
        public IScreenCapture ScreenCapture { get { return null; } }
        public IOcrService Ocr { get { return null; } }
        public IStartupService Startup { get { return null; } }
        public IAppPaths Paths { get { return null; } }
        public IShellService Shell { get { return null; } }
    }

    public static class AppModelTests
    {
        private static List<string> Record(INotifyPropertyChanged o)
        {
            List<string> names = new List<string>();
            o.PropertyChanged += (s, e) => names.Add(e.PropertyName);
            return names;
        }

        private static RobloxInstance Inst(string status)
        {
            RobloxInstance i = new RobloxInstance();
            i.Status = status;
            return i;
        }

        public static void Run()
        {
            FakePlatform none = new FakePlatform(PlatformCapabilities.None);
            FakePlatform mem = new FakePlatform(PlatformCapabilities.MemoryCap | PlatformCapabilities.WindowPlacement);

            // RAM slider: index drives MB and label, and a change is reported for all three.
            MainViewModel vm = new MainViewModel(none);
            List<string> ev = Record(vm);
            vm.RamIndex = 5;
            T.Check("RamIndex raises RamIndex", ev.Contains("RamIndex"));
            T.Check("RamIndex raises RamMb", ev.Contains("RamMb"));
            T.Check("RamIndex raises RamLabel", ev.Contains("RamLabel"));
            T.Check("RamMb equals MbFromIndex(5)", vm.RamMb == RamScale.MbFromIndex(5) && vm.RamMb == 384, vm.RamMb.ToString());
            T.Check("RamLabel is 384 MB", vm.RamLabel == "384 MB", vm.RamLabel);

            ev.Clear();
            vm.RamIndex = 5;
            T.Check("same RamIndex raises nothing", ev.Count == 0, ev.Count.ToString());

            vm.RamIndex = 0;
            T.Check("index 0 is Auto", vm.RamMb == 0 && vm.RamLabel == "Auto", vm.RamLabel);

            ev.Clear();
            vm.RamMb = 384;
            T.Check("setting RamMb sets index 5", vm.RamIndex == 5, vm.RamIndex.ToString());
            T.Check("setting RamMb raises RamIndex and RamLabel", ev.Contains("RamIndex") && ev.Contains("RamLabel"));

            // Feature gates.
            T.Check("unsupported MemoryCap is disabled but visible", !vm.MemoryCap.Enabled && vm.MemoryCap.Visible);
            T.Check("unsupported MemoryCap has reason",
                vm.MemoryCap.Reason == "Memory cap is not supported on FakePlatform.", vm.MemoryCap.Reason);
            MainViewModel vmMem = new MainViewModel(mem);
            T.Check("supported MemoryCap is enabled", vmMem.MemoryCap.Enabled && vmMem.MemoryCap.Reason == "");
            T.Check("supported WindowPlacement is enabled", vmMem.WindowPlacement.Enabled);
            T.Check("unsupported InputInjection is disabled", !vmMem.InputInjection.Enabled);

            // Instances and active count.
            RobloxInstance a = Inst("Running");
            RobloxInstance b = Inst("Stopped");
            RobloxInstance c = Inst("Starting");
            RobloxInstance d = Inst(null);
            MainViewModel list = new MainViewModel(none);
            list.Instances.Add(new InstanceCardViewModel(a, none));
            list.Instances.Add(new InstanceCardViewModel(b, none));
            list.Instances.Add(new InstanceCardViewModel(c, none));
            list.Instances.Add(new InstanceCardViewModel(d, none));
            T.Check("ActiveCount equals InstanceStatus.CountActive",
                list.ActiveCount == InstanceStatus.CountActive(new[] { a, b, c, d }) && list.ActiveCount == 2,
                list.ActiveCount.ToString());

            List<string> lev = Record(list);
            b.Status = "Running";
            T.Check("card status change raises ActiveCount", lev.Contains("ActiveCount"));
            T.Check("ActiveCount updates after status change", list.ActiveCount == 3, list.ActiveCount.ToString());

            list.SetRestrictor(new Restrictor(null));
            T.Check("StatusLine uses Restrictor.StatusLine",
                list.StatusLine == "3 / 2 recommended (hard max 4)", list.StatusLine);
            T.Check("no restrictor gives empty StatusLine", new MainViewModel(none).StatusLine == "");

            // Card forwarding and disposal.
            InstanceCardViewModel card = new InstanceCardViewModel(a, mem);
            List<string> cev = Record(card);
            a.MemoryMb = 512;
            T.Check("card forwards MemoryText", cev.Contains("MemoryText") && card.MemoryText == "512 MB", card.MemoryText);
            T.Check("card CanCapMemory is enabled on MemoryCap platform", card.CanCapMemory.Enabled);
            T.Check("card CanThrottle is disabled without BackgroundPriority", !card.CanThrottle.Enabled);
            card.Dispose();
            cev.Clear();
            a.Status = "Stopped";
            T.Check("disposed card stops forwarding", cev.Count == 0, cev.Count.ToString());
        }
    }
}
