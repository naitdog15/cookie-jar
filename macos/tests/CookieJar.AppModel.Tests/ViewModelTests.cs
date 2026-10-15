using System.Collections.Generic;
using System.ComponentModel;
using CookieJar.AppModel;
using CookieJar.Core;
using CookieJar.Platform;

namespace CookieJar.Tests
{
    public static class ViewModelTests
    {
        private static List<string> Record(INotifyPropertyChanged o)
        {
            List<string> names = new List<string>();
            o.PropertyChanged += (s, e) => names.Add(e.PropertyName);
            return names;
        }

        private static int Count(List<string> l, string name)
        {
            return l.FindAll(n => n == name).Count;
        }

        public static void Run()
        {
            CapsPlatform none = new CapsPlatform(PlatformCapabilities.None);
            MainViewModel vm = new MainViewModel(none);

            // RAM slider round trip: 0 is Auto, 1 is MinMb, Steps is MaxMb (RamScale in Core).
            vm.RamIndex = 1;
            T.Check("index 1 is 128 MB", vm.RamMb == 128 && vm.RamMb == RamScale.MinMb, vm.RamMb.ToString());
            T.Check("index 1 round-trips through IndexFromMb", RamScale.IndexFromMb(vm.RamMb) == 1);
            vm.RamIndex = RamScale.Steps;
            T.Check("max index is 3072 MB", vm.RamMb == 3072 && vm.RamMb == RamScale.MaxMb, vm.RamMb.ToString());
            T.Check("max index label comes from RamScale", vm.RamLabel == RamScale.Label(3072), vm.RamLabel);
            vm.RamIndex = 0;
            T.Check("index 0 is Auto (0 MB)", vm.RamMb == 0 && vm.RamLabel == "Auto");
            vm.RamIndex = RamScale.Steps + 50;
            T.Check("index above max clamps", vm.RamIndex == RamScale.Steps, vm.RamIndex.ToString());
            vm.RamIndex = -4;
            T.Check("negative index clamps to 0", vm.RamIndex == 0, vm.RamIndex.ToString());
            vm.RamMb = 192;
            T.Check("RamMb 192 gives index 2", vm.RamIndex == 2 && vm.RamMb == 192, vm.RamIndex.ToString());

            // PropertyChanged fires once per real change.
            MainViewModel v2 = new MainViewModel(none);
            List<string> ev = Record(v2);
            v2.RamIndex = 3;
            T.Check("RamIndex raised exactly once", Count(ev, "RamIndex") == 1);
            T.Check("RamMb raised exactly once", Count(ev, "RamMb") == 1);
            T.Check("RamLabel raised exactly once", Count(ev, "RamLabel") == 1);
            ev.Clear();
            v2.RamIndex = 3;
            T.Check("same index raises nothing", ev.Count == 0, ev.Count.ToString());
            v2.RamIndex = 9999;
            ev.Clear();
            v2.RamIndex = 8888;
            T.Check("two clamped values to the same index raise nothing", ev.Count == 0, ev.Count.ToString());

            AccountRowViewModel row = new AccountRowViewModel("a1", "Main", none);
            List<string> rev = Record(row);
            row.IsSelected = true;
            row.IsSelected = true;
            T.Check("IsSelected raised once for one change", Count(rev, "IsSelected") == 1);
            row.IsRunning = true;
            T.Check("IsRunning raised", rev.Contains("IsRunning"));
            T.Check("row keeps id and name", row.AccountId == "a1" && row.DisplayName == "Main");

            // ActiveCount
            RobloxInstance run = new RobloxInstance(); run.Status = "Running";
            RobloxInstance stop = new RobloxInstance(); stop.Status = "Stopped";
            MainViewModel list = new MainViewModel(none);
            T.Check("empty list has ActiveCount 0", list.ActiveCount == 0);
            list.Instances.Add(new InstanceCardViewModel(run, none));
            list.Instances.Add(new InstanceCardViewModel(stop, none));
            T.Check("ActiveCount counts only non-stopped",
                list.ActiveCount == InstanceStatus.CountActive(new[] { run, stop }) && list.ActiveCount == 1,
                list.ActiveCount.ToString());
            list.Instances.RemoveAt(0);
            T.Check("ActiveCount drops after removal", list.ActiveCount == 0, list.ActiveCount.ToString());

            Restrictor r = new Restrictor(null);
            list.SetRestrictor(r);
            T.Check("StatusLine equals Restrictor.StatusLine for current count",
                list.StatusLine == r.StatusLine(list.ActiveCount) && list.StatLine == list.StatusLine, list.StatusLine);

            // Card change notifications.
            RobloxInstance inst = new RobloxInstance();
            InstanceCardViewModel card = new InstanceCardViewModel(inst, none);
            List<string> cev = Record(card);
            inst.Status = "Running";
            T.Check("card raises Status once", Count(cev, "Status") == 1);
            cev.Clear();
            inst.Status = "Running";
            T.Check("card silent on same status", cev.Count == 0);
            T.Check("card exposes wrapped instance", card.Instance == inst);

            // Restrictor thresholds (Core) that the UI warn/block decision relies on.
            T.Check("free RAM below 1.5 GB blocks", r.Evaluate(0, Restrictor.BlockFreeRamGb - 0.1, 0).Level == RestrictLevel.Block);
            T.Check("free RAM below 3.0 GB warns", r.Evaluate(0, Restrictor.WarnFreeRamGb - 0.1, 0).Level == RestrictLevel.Warn);
            T.Check("free RAM at 8 GB allows", r.Evaluate(0, 8.0, 0).Level == RestrictLevel.Allow);
        }
    }
}
