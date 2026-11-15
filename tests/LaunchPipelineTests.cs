using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Reflection;
using System.Threading;

namespace RbxMulti
{
    internal static class LaunchPipelineTests
    {
        internal static int RunChild(string[] args)
        {
            if (args.Length != 2 || !args[1].StartsWith("CookieJar.PipelineTests.", StringComparison.Ordinal)) return 30;
            using (EventWaitHandle ready = EventWaitHandle.OpenExisting(args[1] + ".ready"))
            using (EventWaitHandle stop = EventWaitHandle.OpenExisting(args[1] + ".stop"))
            {
                ready.Set();
                return stop.WaitOne(30000) ? 0 : 31;
            }
        }

        internal static void Run(Action<string, bool> check)
        {
            CancelAfterStart(check, false);
            CancelAfterStart(check, true);
            TimeoutRetainsTracking(check);
            PreviousClientExit(check);
            AdoptionIsIdempotent(check);
            TrayAdoption(check);
            TrayPolicy(check);
            SixAccountProfile(check);
            GuardFailureStopsProfile(check);
            CacheCheckIsFresh(check);
            SingletonsClosedInNewClient(check);
            InstallerClearsThenLaunches(check);
            InstallerTimeout(check);
            StarterExitStartsInstaller(check);
            ProfileStopsOnUpdate(check);
            HandOffWithoutInstaller(check);
            UpdateClosedExistingClient(check);
        }

        private static bool AnyLog(List<string> logs, string part)
        {
            return logs.Exists(delegate(string s) { return s.Contains(part); });
        }

        private static void InstallerClearsThenLaunches(Action<string, bool> check)
        {
            using (FakeEnvironment environment = new FakeEnvironment())
            {
                List<string> logs = new List<string>();
                InstanceManager manager = new InstanceManager(environment);
                manager.Log += delegate(string s) { logs.Add(s); };
                environment.Installers.Add(4242);
                int ticks = 0;
                environment.OnWait = delegate { if (++ticks == 4) environment.Installers.Clear(); };
                AccountLaunchResult result = manager.LaunchAccount(TestAccount(21), "test", 1, 10, CancellationToken.None);
                check("launch waits for a running Roblox installer and then continues", result.Outcome == LaunchOutcome.Ok
                    && environment.Starts == 1 && environment.GuardCalls == 1 && ticks >= 4);
                check("installer wait is logged", AnyLog(logs, "Roblox installer running (PID 4242); waiting"));
            }
        }

        private static void InstallerTimeout(Action<string, bool> check)
        {
            using (FakeEnvironment environment = new FakeEnvironment())
            {
                environment.Installers.Add(4242);
                AccountLaunchResult result = new InstanceManager(environment).LaunchAccount(TestAccount(22), "test", 1, 10, CancellationToken.None);
                check("installer that never finishes reports Roblox updating before any launch work", result.Outcome == LaunchOutcome.RobloxUpdating
                    && environment.Starts == 0 && environment.GuardCalls == 0 && environment.Tokens.Count == 0);
                check("installer timeout names the PID and Task Manager", result.Message != null
                    && result.Message.Contains("4242") && result.Message.Contains("Task Manager"));
                check("installer wait lasts the full 120 seconds", environment.Waits >= 120 * 4);
            }
            using (FakeEnvironment environment = new FakeEnvironment())
            using (CancellationTokenSource cancel = new CancellationTokenSource())
            {
                environment.Installers.Add(4242);
                environment.OnWait = delegate { cancel.Cancel(); };
                AccountLaunchResult result = new InstanceManager(environment).LaunchAccount(TestAccount(23), "test", 1, 10, cancel.Token);
                check("cancelling during the installer wait is reported as cancelled", result.Outcome == LaunchOutcome.Cancelled
                    && environment.Starts == 0);
            }
        }

        private static void StarterExitStartsInstaller(Action<string, bool> check)
        {
            using (FakeEnvironment environment = new FakeEnvironment())
            {
                List<string> logs = new List<string>();
                InstanceManager manager = new InstanceManager(environment);
                manager.Log += delegate(string s) { logs.Add(s); };
                environment.Newer.Add("C:\\Fake PF\\RobloxPlayerBeta.exe (0.747.0.7471481)");
                environment.OnStart = delegate { environment.Pids.Remove(environment.LastPid); environment.Installers.Add(5150); };
                AccountLaunchResult result = manager.LaunchAccount(TestAccount(24), "test", 1, 10, CancellationToken.None);
                check("starter that exits into an installer is reported as an update", result.Outcome == LaunchOutcome.RobloxUpdating
                    && result.Message.Contains(Assembly.GetExecutingAssembly().Location) && result.Message.Contains("Newer installs found")
                    && !result.Message.Contains("exited before its account window"));
                check("installer start is logged", AnyLog(logs, "started an update (RobloxPlayerInstaller PID 5150)"));
            }
            using (FakeEnvironment environment = new FakeEnvironment())
            {
                environment.ShowWindows = false;
                environment.OnStart = delegate { environment.Installers.Add(5151); };
                AccountLaunchResult result = new InstanceManager(environment).LaunchAccount(TestAccount(25), "test", 1, 10, CancellationToken.None);
                check("an installer started by a still-running starter is reported as an update", result.Outcome == LaunchOutcome.RobloxUpdating);
            }
            using (FakeEnvironment environment = new FakeEnvironment())
            {
                environment.OnPrepare = delegate { if (!environment.Installers.Contains(1)) environment.Installers.Add(1); };
                AccountLaunchResult result = new InstanceManager(environment).LaunchAccount(TestAccount(26), "test", 1, 10, CancellationToken.None);
                check("an installer already running at start is not counted as new", result.Outcome == LaunchOutcome.Ok);
            }
        }

        private static void ProfileStopsOnUpdate(Action<string, bool> check)
        {
            using (FakeEnvironment environment = new FakeEnvironment())
            {
                environment.OnStart = delegate
                {
                    if (environment.Starts == 2) { environment.Pids.Remove(environment.LastPid); environment.Installers.Add(6000); }
                };
                string problem;
                string summary = RunProfile(new InstanceManager(environment),
                    new List<Account> { TestAccount(31), TestAccount(32), TestAccount(33) }, out problem);
                check("profile stops when a launch starts a Roblox update", environment.Starts == 2 && summary.StartsWith("Stopped.")
                    && summary.Contains("1 launched, 1 failed") && problem != null && problem.Contains("out of date"));
            }
            using (FakeEnvironment environment = new FakeEnvironment())
            {
                string problem;
                string summary = RunProfile(new InstanceManager(environment),
                    new List<Account> { TestAccount(34), TestAccount(35), TestAccount(36) }, out problem,
                    delegate(string t) { if (t.StartsWith("Launching 3/3")) environment.Installers.Add(7000); });
                check("profile does not start an account while an installer runs", environment.Starts == 2
                    && summary.Contains("2 launched") && problem != null && problem.Contains("7000"));
            }
        }

        private static void HandOffWithoutInstaller(Action<string, bool> check)
        {
            using (FakeEnvironment environment = new FakeEnvironment())
            {
                List<string> logs = new List<string>();
                InstanceManager manager = new InstanceManager(environment);
                manager.Log += delegate(string s) { logs.Add(s); };
                int self = Process.GetCurrentProcess().Id;
                environment.OnStart = delegate { environment.Pids.Remove(environment.LastPid); environment.Pids.Add(self); environment.Visible.Add(self); };
                Account account = TestAccount(41);
                AccountLaunchResult result = manager.LaunchAccount(account, "test", 1, 10, CancellationToken.None);
                check("hand-off to one new player without an installer still succeeds", result.Outcome == LaunchOutcome.Ok
                    && AnyLog(logs, "handed off to PID " + self) && manager.IsAccountRunning(account.Id));
            }
        }

        private static void UpdateClosedExistingClient(Action<string, bool> check)
        {
            using (FakeEnvironment environment = new FakeEnvironment())
            {
                int previous = Process.GetCurrentProcess().Id;
                environment.Pids.Add(previous);
                environment.Visible.Add(previous);
                InstanceManager manager = new InstanceManager(environment);
                manager.AdoptRunning();
                environment.OnStart = delegate { environment.Pids.Remove(previous); environment.Installers.Add(8000); };
                AccountLaunchResult result = manager.LaunchAccount(TestAccount(51), "test", 1, 10, CancellationToken.None);
                check("an update that closes an existing client is reported as such", result.Outcome == LaunchOutcome.RobloxUpdating
                    && result.Message.Contains("closed the existing client"));
            }
        }

        private static void SingletonsClosedInNewClient(Action<string, bool> check)
        {
            using (FakeEnvironment environment = new FakeEnvironment())
            {
                AccountLaunchResult result = new InstanceManager(environment).LaunchAccount(TestAccount(11),
                    "test", 1, 10, CancellationToken.None);
                check("new client's singleton handles are closed after start", result.Outcome == LaunchOutcome.Ok
                    && environment.SingletonCloses.Count == 1 && environment.SingletonCloses[0] == environment.LastPid);
            }
        }

        private static Account TestAccount(int index)
        {
            return new Account { Id = "fake-account-" + index, Username = "FakeUser" + index, Token = "FAKE_TOKEN_" + index };
        }

        private static string RunProfile(InstanceManager manager, List<Account> accounts, out string problem)
        {
            return RunProfile(manager, accounts, out problem, delegate { });
        }

        private static string RunProfile(InstanceManager manager, List<Account> accounts, out string problem, Action<string> progress)
        {
            ProfileRunner runner = new ProfileRunner(manager,
                delegate { return RestrictResult.Create(RestrictLevel.Allow, "", ""); },
                delegate { return true; }, progress);
            return runner.Run(new LaunchProfile { Name = "Isolated tests", StaggerSeconds = 0 }, accounts,
                CancellationToken.None, out problem);
        }

        private static void CancelAfterStart(Action<string, bool> check, bool duringReadiness)
        {
            using (FakeEnvironment environment = new FakeEnvironment())
            using (CancellationTokenSource cancel = new CancellationTokenSource())
            {
                InstanceManager manager = new InstanceManager(environment);
                Account account = TestAccount(1);
                if (duringReadiness) environment.OnWait = delegate { cancel.Cancel(); };
                else environment.OnStart = delegate { cancel.Cancel(); };
                AccountLaunchResult result = manager.LaunchAccount(account, "test", 1, 10, cancel.Token);
                string when = duringReadiness ? "during window settling" : "immediately after process start";
                check("cancel " + when + " is reported", result.Outcome == LaunchOutcome.Cancelled);
                check("cancel " + when + " keeps exact account PID tracked", manager.Instances.Count == 1
                    && manager.Instances[0].Pid == environment.LastPid && manager.IsAccountRunning(account.Id));
                check("cancel " + when + " leaves the started game alive", environment.LastClientAlive);
                string problem;
                string summary = RunProfile(manager, new List<Account> { account }, out problem);
                check("profile retry skips account cancelled " + when, environment.Starts == 1
                    && summary.Contains("1 skipped") && problem == null);
            }
        }

        private static void TimeoutRetainsTracking(Action<string, bool> check)
        {
            using (FakeEnvironment environment = new FakeEnvironment())
            {
                environment.ShowWindows = false;
                InstanceManager manager = new InstanceManager(environment);
                Account account = TestAccount(2);
                AccountLaunchResult result = manager.LaunchAccount(account, "test", 1, 0, CancellationToken.None);
                check("zero readiness timeout is reported", result.Outcome == LaunchOutcome.TimedOut);
                check("readiness timeout retains live account mapping", manager.Instances.Count == 1
                    && manager.IsAccountRunning(account.Id) && environment.LastClientAlive);
                string problem;
                string summary = RunProfile(manager, new List<Account> { account }, out problem);
                check("profile retry does not duplicate a timed-out live account", environment.Starts == 1 && summary.Contains("1 skipped"));
            }
        }

        private static void PreviousClientExit(Action<string, bool> check)
        {
            using (FakeEnvironment environment = new FakeEnvironment())
            {
                // The current test process supplies a real PID for adoption. Its disappearance is
                // simulated only in GetPids; no process is terminated or otherwise manipulated.
                int previous = Process.GetCurrentProcess().Id;
                environment.Pids.Add(previous);
                environment.Visible.Add(previous);
                InstanceManager manager = new InstanceManager(environment);
                check("prior game is adopted for preservation test", manager.AdoptRunning() == 1);
                environment.OnStart = delegate { environment.Pids.Remove(previous); };
                Account account = TestAccount(3);
                AccountLaunchResult result = manager.LaunchAccount(account, "test", 1, 10, CancellationToken.None);
                check("prior-client disappearance stops the launch", result.Outcome == LaunchOutcome.MultiInstanceFailed);
                check("prior-client failure still retains the new live account", manager.IsAccountRunning(account.Id)
                    && manager.Instances.Count == 2 && environment.LastClientAlive);
            }
        }

        private static void AdoptionIsIdempotent(Action<string, bool> check)
        {
            using (FakeEnvironment environment = new FakeEnvironment())
            {
                InstanceManager manager = new InstanceManager(environment);
                environment.OnStart = delegate { manager.AdoptRunning(); };
                Account account = TestAccount(4);
                AccountLaunchResult result = manager.LaunchAccount(account, "profile", 1, 10, CancellationToken.None);
                check("pre-adopted started player still becomes ready", result.Outcome == LaunchOutcome.Ok);
                check("racing adoption creates one card with account and profile", manager.Instances.Count == 1
                    && manager.Instances[0].AccountName == account.Username && manager.Instances[0].ProfileName == "profile"
                    && manager.IsAccountRunning(account.Id));
                check("subsequent adoption cannot duplicate the card", manager.AdoptRunning() == 0 && manager.Instances.Count == 1);
            }
        }

        private static void TrayAdoption(Action<string, bool> check)
        {
            using (FakeEnvironment environment = new FakeEnvironment())
            {
                int pid = Process.GetCurrentProcess().Id;
                environment.Pids.Add(pid);
                InstanceManager manager = new InstanceManager(environment);
                check("startup does not adopt a hidden tray process", manager.AdoptRunning() == 0 && manager.Instances.Count == 0);
                environment.Visible.Add(pid); // IsWindowVisible also includes minimised windows.
                check("tray becoming a visible game is adoptable", manager.AdoptRunning() == 1);
                environment.Visible.Clear();
                AccountLaunchResult result = manager.LaunchAccount(TestAccount(5), "test", 1, 0, CancellationToken.None);
                check("already-adopted game stays protected when hidden", environment.Protected.Contains(pid)
                    && result.Outcome == LaunchOutcome.TimedOut);
            }
            using (FakeEnvironment environment = new FakeEnvironment())
            {
                int tray = Process.GetCurrentProcess().Id;
                environment.Pids.Add(tray);
                InstanceManager manager = new InstanceManager(environment);
                manager.AdoptRunning();
                environment.TrayPid = tray;
                AccountLaunchResult result = manager.LaunchAccount(TestAccount(6), "test", 1, 10, CancellationToken.None);
                check("startup tray is eligible for cleanup and not protected as a game", !environment.Protected.Contains(tray)
                    && !environment.Pids.Contains(tray) && result.Outcome == LaunchOutcome.Ok);
            }
        }

        private static void TrayPolicy(Action<string, bool> check)
        {
            string tray = "\"C:\\Fake Roblox\\RobloxPlayerBeta.exe\" --launch-to-tray";
            check("explicit hidden tray is cleanup eligible", RobloxProcesses.IsIdleTrayClient(false, false, tray));
            check("visible tray/game is never cleanup eligible", !RobloxProcesses.IsIdleTrayClient(true, false, tray));
            check("tracked hidden game is never cleanup eligible", !RobloxProcesses.IsIdleTrayClient(false, true, tray));
            check("unknown hidden process is not presumed idle by age", !RobloxProcesses.IsIdleTrayClient(false, false, null));
            check("ticket argument containing tray text is not a tray flag", !RobloxProcesses.IsIdleTrayClient(false, false,
                "RobloxPlayerBeta.exe roblox-player:1+gameinfo:FAKE--launch-to-tray"));
        }

        private static void SixAccountProfile(Action<string, bool> check)
        {
            using (FakeEnvironment environment = new FakeEnvironment())
            {
                InstanceManager manager = new InstanceManager(environment);
                List<Account> accounts = new List<Account>();
                for (int i = 0; i < 6; i++) accounts.Add(TestAccount(i));
                string problem;
                string summary = RunProfile(manager, accounts, out problem);
                check("real profile runner launches six isolated simulated players", environment.Starts == 6
                    && manager.Instances.Count == 6 && summary.Contains("6 launched") && problem == null);
                check("each production launch requests its own account ticket", environment.Tokens.Count == 6
                    && new HashSet<string>(environment.Tokens).Count == 6);
                check("all six production launches retain account identity", accounts.TrueForAll(delegate(Account a) { return manager.IsAccountRunning(a.Id); }));
                check("production readiness waits two seconds per player", environment.Waits >= 6 * 8);
                summary = RunProfile(manager, accounts, out problem);
                check("rerunning six-account profile skips all existing players", environment.Starts == 6 && summary.Contains("6 skipped"));
            }
        }

        private static void GuardFailureStopsProfile(Action<string, bool> check)
        {
            using (FakeEnvironment environment = new FakeEnvironment())
            {
                environment.GuardReady = false;
                string problem;
                string summary = RunProfile(new InstanceManager(environment),
                    new List<Account> { TestAccount(1), TestAccount(2) }, out problem);
                check("guard failure stops profile before another account", environment.GuardCalls == 1
                    && environment.Starts == 0 && environment.Tokens.Count == 0 && problem != null && summary.StartsWith("Stopped."));
            }
        }

        private static void CacheCheckIsFresh(Action<string, bool> check)
        {
            using (FakeEnvironment environment = new FakeEnvironment())
            {
                environment.OnPrepare = delegate { environment.Pids.Add(Process.GetCurrentProcess().Id); };
                AccountLaunchResult result = new InstanceManager(environment).LaunchAccount(TestAccount(9),
                    "test", 1, 0, CancellationToken.None);
                check("client starting during preparation prevents cache clearing", environment.CacheClears == 0
                    && result.Outcome == LaunchOutcome.TimedOut);
            }
        }

        private sealed class FakeEnvironment : LaunchEnvironment, IDisposable
        {
            internal readonly HashSet<int> Pids = new HashSet<int>();
            internal readonly HashSet<int> Visible = new HashSet<int>();
            internal readonly List<int> Installers = new List<int>();
            internal readonly List<string> Newer = new List<string>();
            internal HashSet<int> Protected = new HashSet<int>();
            internal readonly List<string> Tokens = new List<string>();
            private readonly List<Process> _clients = new List<Process>();
            private readonly string _prefix = "CookieJar.PipelineTests." + Guid.NewGuid().ToString("N");
            private readonly EventWaitHandle _stop;
            private readonly EventWaitHandle _ready;
            private DateTime _now = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
            internal int Starts, Waits, GuardCalls, LastPid, TrayPid, CacheClears;
            internal bool ShowWindows = true, GuardReady = true;
            internal Action OnStart, OnWait, OnPrepare;
            internal bool LastClientAlive { get { return _clients.Count > 0 && !_clients[_clients.Count - 1].HasExited; } }

            internal FakeEnvironment()
            {
                _stop = new EventWaitHandle(false, EventResetMode.ManualReset, _prefix + ".stop");
                _ready = new EventWaitHandle(false, EventResetMode.ManualReset, _prefix + ".ready");
            }
            internal override List<int> GetPids() { return new List<int>(Pids); }
            internal override List<int> GetInstallerPids() { return new List<int>(Installers); }
            internal override List<string> NewerPlayersThan(string exe) { return new List<string>(Newer); }
            internal override HashSet<int> VisiblePids() { return new HashSet<int>(Visible); }
            internal override int EndIdleTrayClients(IEnumerable<int> protectedPids)
            {
                Protected = new HashSet<int>(protectedPids);
                if (TrayPid != 0 && !Protected.Contains(TrayPid)) { Pids.Remove(TrayPid); return 1; }
                return 0;
            }
            internal override bool PrepareMultiInstance(out string error, out int closed)
            {
                GuardCalls++;
                if (OnPrepare != null) OnPrepare();
                closed = 0;
                error = GuardReady ? null : "Simulated guard conflict";
                return GuardReady;
            }
            internal readonly List<int> SingletonCloses = new List<int>();
            internal override int CloseSingletons(int pid) { SingletonCloses.Add(pid); return 1; }
            internal override string FindPlayerExe() { return Assembly.GetExecutingAssembly().Location; }
            internal override void ClearCache() { CacheClears++; }
            internal override bool GetTicket(string token, out string ticket, out bool expired, out string error)
            {
                Tokens.Add(token);
                ticket = "FAKE_TICKET_" + Tokens.Count;
                expired = false;
                error = null;
                return true;
            }
            internal override Process StartPlayer(ProcessStartInfo info)
            {
                _ready.Reset();
                ProcessStartInfo child = new ProcessStartInfo(info.FileName, "--launch-pipeline-client " + _prefix);
                child.UseShellExecute = false;
                child.CreateNoWindow = true;
                Process process = Process.Start(child);
                _clients.Add(Process.GetProcessById(process.Id));
                if (!_ready.WaitOne(5000)) throw new Exception("Simulated player did not start");
                Starts++;
                LastPid = process.Id;
                Pids.Add(LastPid);
                if (ShowWindows) Visible.Add(LastPid);
                if (OnStart != null) OnStart();
                return process;
            }
            internal override bool HasWindow(int pid) { return ShowWindows && Visible.Contains(pid); }
            internal override DateTime UtcNow { get { return _now; } }
            internal override void Wait(CancellationToken ct)
            {
                Waits++;
                _now = _now.AddMilliseconds(250);
                if (OnWait != null) OnWait();
            }
            public void Dispose()
            {
                _stop.Set();
                foreach (Process client in _clients)
                {
                    if (!client.WaitForExit(5000)) { client.Kill(); client.WaitForExit(1000); }
                    client.Dispose();
                }
                _ready.Close();
                _stop.Close();
            }
        }
    }
}
