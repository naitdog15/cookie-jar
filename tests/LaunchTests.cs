using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Threading;

namespace RbxMulti
{
    // No Roblox, account store, network, windows, mouse or keyboard. All named objects are unique.
    internal static class LaunchTests
    {
        private static int _passed;

        private static void Check(string name, bool ok)
        {
            if (!ok) throw new Exception("FAIL: " + name);
            _passed++;
            Console.WriteLine("PASS: " + name);
        }

        private static string Prefix()
        {
            return "CookieJar.LaunchTests." + Guid.NewGuid().ToString("N");
        }

        [STAThread]
        private static int Main(string[] args)
        {
            if (args.Length > 0 && args[0] == "--launch-pipeline-client")
                return LaunchPipelineTests.RunChild(args);
            if (args.Length > 0 && args[0].StartsWith("roblox-player:", StringComparison.Ordinal))
                return FakeClient(args);
            try
            {
                SixClients();
                EventConflict();
                OwnerConflict();
                AbandonedOwner();
                ConcurrentPreparation();
                CancelBeforeLaunch();
                GuardFailureTests.Run(Check);
                LocatorTests.Run(Check);
                LaunchPipelineTests.Run(Check);
                RobloxRepairTests.Run(Check);
                Console.WriteLine(_passed + " launch checks passed; no real clients or input used.");
                return 0;
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine(ex.Message);
                return 1;
            }
        }

        private static void SixClients()
        {
            string prefix = Prefix();
            List<Process> clients = new List<Process>();
            string error;
            using (EventWaitHandle stop = new EventWaitHandle(false, EventResetMode.ManualReset, prefix + ".stop"))
            using (MultiInstanceGuard guard = new MultiInstanceGuard(prefix + ".mutex", prefix + ".event"))
            {
                try
                {
                    Check("guard ready before first account", guard.EnsureReady(out error));
                    for (int i = 0; i < 6; i++)
                    {
                        // Different tickets/trackers must arrive intact as a single argument, including
                        // the encoded place URL. This also exercises a player path containing spaces.
                        string uri = TicketLauncher.BuildLaunchUri("FAKE_TICKET_" + i, 920587237, 100000000000L + i, 1234567890);
                        ProcessStartInfo info = TicketLauncher.BuildPlayerStartInfo(Assembly.GetExecutingAssembly().Location, uri);
                        info.EnvironmentVariables["COOKIEJAR_TEST_PREFIX"] = prefix;
                        info.EnvironmentVariables["COOKIEJAR_TEST_URI"] = uri;
                        info.EnvironmentVariables["COOKIEJAR_TEST_INDEX"] = i.ToString();
                        info.CreateNoWindow = true;
                        Check("account " + (i + 1) + " bypasses shell routing", !info.UseShellExecute
                            && info.FileName.EndsWith("LaunchTests.exe") && info.Arguments == "\"" + uri + "\"");
                        using (EventWaitHandle ready = new EventWaitHandle(false, EventResetMode.ManualReset, prefix + ".ready" + i))
                        {
                            clients.Add(Process.Start(info));
                            Check("account " + (i + 1) + " cannot acquire singleton or routing event", ready.WaitOne(5000));
                        }
                        Check("guard remains ready between accounts", guard.EnsureReady(out error));
                    }
                    Check("all six simulated clients remain alive together", clients.TrueForAll(delegate(Process p) { return !p.HasExited; }));
                }
                finally
                {
                    stop.Set();
                    foreach (Process client in clients)
                    {
                        try
                        {
                            if (!client.WaitForExit(5000)) client.Kill();
                            Check("simulated client exits normally", client.WaitForExit(1000) && client.ExitCode == 0);
                        }
                        finally { client.Dispose(); }
                    }
                }
            }
            using (EventWaitHandle after = new EventWaitHandle(false, EventResetMode.AutoReset, prefix + ".event"))
                Check("shutdown releases routing name", after != null);
            using (Mutex after = new Mutex(false, prefix + ".mutex"))
            {
                bool acquired = after.WaitOne(0);
                Check("shutdown releases singleton ownership", acquired);
                if (acquired) after.ReleaseMutex();
            }
        }

        private static int FakeClient(string[] args)
        {
            string prefix = Environment.GetEnvironmentVariable("COOKIEJAR_TEST_PREFIX");
            if (string.IsNullOrEmpty(prefix) || !prefix.StartsWith("CookieJar.LaunchTests.", StringComparison.Ordinal)) return 20;
            if (args.Length != 1 || args[0] != Environment.GetEnvironmentVariable("COOKIEJAR_TEST_URI")) return 21;
            using (Mutex singleton = new Mutex(false, prefix + ".mutex"))
            {
                bool acquired;
                try { acquired = singleton.WaitOne(0); }
                catch (AbandonedMutexException) { acquired = true; }
                if (acquired) { singleton.ReleaseMutex(); return 22; }
                try
                {
                    using (EventWaitHandle routing = new EventWaitHandle(false, EventResetMode.AutoReset, prefix + ".event")) { }
                    return 23;
                }
                catch (WaitHandleCannotBeOpenedException) { }
                using (EventWaitHandle ready = EventWaitHandle.OpenExisting(prefix + ".ready" + Environment.GetEnvironmentVariable("COOKIEJAR_TEST_INDEX")))
                using (EventWaitHandle stop = EventWaitHandle.OpenExisting(prefix + ".stop"))
                {
                    ready.Set();
                    return stop.WaitOne(30000) ? 0 : 24;
                }
            }
        }

        private static void EventConflict()
        {
            string prefix = Prefix();
            string error;
            using (MultiInstanceGuard guard = new MultiInstanceGuard(prefix + ".mutex", prefix + ".event"))
            {
                using (EventWaitHandle existing = new EventWaitHandle(false, EventResetMode.AutoReset, prefix + ".event"))
                {
                    Check("existing routing event blocks unsafe launch", !guard.EnsureReady(out error) && !string.IsNullOrEmpty(error));
                    existing.Set();
                    Check("failure leaves existing routing event intact", existing.WaitOne(0));
                }
                Check("retry succeeds after event conflict is removed", guard.EnsureReady(out error));
            }
        }

        private static void OwnerConflict()
        {
            string prefix = Prefix();
            string error;
            using (Mutex existing = new Mutex(true, prefix + ".mutex"))
            using (MultiInstanceGuard guard = new MultiInstanceGuard(prefix + ".mutex", prefix + ".event"))
            {
                Check("another owner blocks unsafe launch", !guard.EnsureReady(out error) && !string.IsNullOrEmpty(error));
                existing.ReleaseMutex();
                Check("retry succeeds after other owner releases", guard.EnsureReady(out error));
            }
        }

        private static void AbandonedOwner()
        {
            string prefix = Prefix();
            Mutex abandoned = null;
            Thread owner = new Thread(delegate() { abandoned = new Mutex(true, prefix + ".mutex"); });
            owner.Start();
            owner.Join();
            using (abandoned)
            using (MultiInstanceGuard guard = new MultiInstanceGuard(prefix + ".mutex", prefix + ".event"))
            {
                string error;
                Check("recovers abandoned mutex ownership", guard.EnsureReady(out error));
            }
        }

        private static void ConcurrentPreparation()
        {
            string prefix = Prefix();
            using (MultiInstanceGuard guard = new MultiInstanceGuard(prefix + ".mutex", prefix + ".event"))
            {
                Thread[] callers = new Thread[6];
                bool[] ready = new bool[6];
                for (int i = 0; i < callers.Length; i++)
                {
                    int index = i;
                    callers[i] = new Thread(delegate() { string error; ready[index] = guard.EnsureReady(out error); });
                    callers[i].Start();
                }
                foreach (Thread caller in callers) caller.Join();
                Check("concurrent preparations share one persistent owner", Array.TrueForAll(ready, delegate(bool value) { return value; }));
            }
        }

        private static void CancelBeforeLaunch()
        {
            using (CancellationTokenSource cancel = new CancellationTokenSource())
            {
                cancel.Cancel();
                AccountLaunchResult result = new InstanceManager().LaunchAccount(null, null, 1, 1, cancel.Token);
                Check("pre-cancelled launch does not inspect accounts or touch Roblox", result.Outcome == LaunchOutcome.Cancelled);
            }
        }
    }
}
