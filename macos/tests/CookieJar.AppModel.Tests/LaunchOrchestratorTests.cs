using System.Collections.Generic;
using System.Threading;
using CookieJar.AppModel;
using CookieJar.Core;
using CookieJar.Platform;

namespace CookieJar.Tests
{
    public static class LaunchOrchestratorTests
    {
        // Warning suffixes copied from the Windows app (MainWindow.Accounts.cs and StartAccountsRun).
        private const string SingleSuffix = "\n\nLaunch another instance anyway? Launching continues automatically when the countdown ends.";
        private const string ProfileSuffix = "\n\nKeep launching the rest of this profile anyway? Launching continues automatically when the countdown ends.";

        private sealed class FakeLauncher : IRobloxLauncher
        {
            public bool Ready = true;

            public LaunchResult Launch(IReadOnlyList<string> arguments, int timeoutMs) { return null; }
            public LaunchResult LaunchUrl(string robloxUrl, int timeoutMs) { return null; }
            public bool PrepareForAnotherInstance() { return Ready; }
            public int ReleaseSingleInstanceLock(int pid) { return 0; }
            public int CountSingleInstanceLocks(int pid) { return 0; }
        }

        private sealed class FakePlatform : IPlatform
        {
            public PlatformCapabilities Capabilities { get; set; }
            public string Name { get { return "Fake"; } }
            public IProcessService Processes { get { return null; } }
            public IRobloxLocator RobloxLocator { get { return null; } }
            public IRobloxLauncher RobloxLauncher { get { return Launcher; } }
            public ISecretStore Secrets { get { return null; } }
            public IWindowService Windows { get { return null; } }
            public IInputService Input { get { return null; } }
            public IScreenCapture ScreenCapture { get { return null; } }
            public IOcrService Ocr { get { return null; } }
            public IStartupService Startup { get { return null; } }
            public IAppPaths Paths { get { return null; } }
            public IShellService Shell { get { return null; } }
            public readonly FakeLauncher Launcher = new FakeLauncher();
        }

        /// <summary>Fixed machine state with the fallback limits (recommended 2, hard max 4) and recorded side effects.</summary>
        private sealed class Harness
        {
            public int RunningCount;
            public double FreeRamGb = 16.0;
            public double CpuPercent = 10.0;
            public bool Accept = true;
            public readonly List<int> Delays = new List<int>();
            public readonly List<string> Progress = new List<string>();
            public readonly List<string> Logs = new List<string>();
            public readonly List<string> Asked = new List<string>();
            public readonly List<string> Launched = new List<string>();
            public readonly FakePlatform Platform = new FakePlatform();
            public readonly LaunchOrchestrator Orch;

            public Harness()
            {
                Orch = new LaunchOrchestrator(Platform, new Restrictor(null),
                    () => RunningCount, () => FreeRamGb, () => CpuPercent,
                    (seconds, ct) => { Delays.Add(seconds); return true; });
                Orch.Events.ConfirmWarning = m => { Asked.Add(m); return Accept; };
                Orch.Events.Progress = p => Progress.Add(p);
                Orch.Events.Log = l => Logs.Add(l);
            }

            /// <summary>A launchOne that records the id and starts the client.</summary>
            public System.Func<string, LaunchResult> Ok()
            {
                return id => { Launched.Add(id); return new LaunchResult(true, 100, null); };
            }
        }

        private static LaunchProfile Profile(int stagger)
        {
            LaunchProfile p = new LaunchProfile();
            p.Name = "Test";
            p.StaggerSeconds = stagger;
            return p;
        }

        private static IList<LaunchAccount> Two()
        {
            return new List<LaunchAccount> { new LaunchAccount("a1", "Alice"), new LaunchAccount("a2", "Bob") };
        }

        public static void Run()
        {
            // Blocked restriction, single launch: launchOne never runs.
            Harness blockedSingle = new Harness();
            blockedSingle.RunningCount = 4;
            LaunchReport rs = blockedSingle.Orch.LaunchSingle(new LaunchAccount("a1", "Alice"), blockedSingle.Ok());
            T.Check("blocked restriction never invokes launchOne (single)", blockedSingle.Launched.Count == 0, "calls=" + blockedSingle.Launched.Count);
            T.Check("blocked single is reported as Blocked", rs.Accounts.Count == 1 && rs.Accounts[0].Outcome == AccountOutcome.Blocked, "");
            T.Check("blocked message comes from LaunchPlanner", rs.Accounts[0].Message == LaunchPlanner.BlockedMessage(blockedSingle.Orch.LastRestriction, "Alice"), rs.Accounts[0].Message);
            T.Check("blocked message ends with the stop sentence", rs.Accounts[0].Message.EndsWith(" Stopped before launching Alice."), rs.Accounts[0].Message);

            // Blocked restriction, profile: no launch, run stopped, summary from LaunchPlanner.
            Harness blockedProfile = new Harness();
            blockedProfile.RunningCount = 4;
            LaunchReport rp = blockedProfile.Orch.LaunchProfile(Profile(8), Two(), blockedProfile.Ok());
            T.Check("blocked restriction never invokes launchOne (profile)", blockedProfile.Launched.Count == 0, "calls=" + blockedProfile.Launched.Count);
            T.Check("blocked profile is stopped", rp.Stopped, "");
            T.Check("blocked profile summary comes from LaunchPlanner", rp.Summary == LaunchPlanner.Summary(true, 0, 0, 0), rp.Summary);

            // Not enough free RAM is also a block.
            Harness lowRam = new Harness();
            lowRam.FreeRamGb = 1.0;
            LaunchReport rr = lowRam.Orch.LaunchSingle(new LaunchAccount("a1", "Alice"), lowRam.Ok());
            T.Check("low free RAM blocks without launching", lowRam.Launched.Count == 0 && rr.Accounts[0].Outcome == AccountOutcome.Blocked, "");

            // Cancel before the second account: the first one is launched, the run stops and the summary says so.
            Harness cancel = new Harness();
            CancellationTokenSource cts = new CancellationTokenSource();
            LaunchReport rc = cancel.Orch.LaunchProfile(Profile(8), Two(), id =>
            {
                cancel.Launched.Add(id);
                cts.Cancel();
                return new LaunchResult(true, 100, null);
            }, cts.Token);
            T.Check("cancel stops after the first account", cancel.Launched.Count == 1 && cancel.Launched[0] == "a1", "calls=" + cancel.Launched.Count);
            T.Check("cancel during stagger skips the wait", cancel.Delays.Count == 0, "delays=" + cancel.Delays.Count);
            T.Check("cancelled run is reported as stopped", rc.Stopped, "");
            T.Check("cancelled summary counts the first launch", rc.Summary == LaunchPlanner.Summary(true, 1, 0, 0), rc.Summary);

            // Cancel requested before anything starts.
            Harness preCancel = new Harness();
            CancellationTokenSource pre = new CancellationTokenSource();
            pre.Cancel();
            LaunchReport rpc = preCancel.Orch.LaunchProfile(Profile(8), Two(), preCancel.Ok(), pre.Token);
            T.Check("pre-cancelled profile launches nothing", preCancel.Launched.Count == 0 && rpc.Stopped, "");

            // Declined warning stops the rest of the profile.
            Harness declined = new Harness();
            declined.RunningCount = 2;
            declined.Accept = false;
            LaunchReport rd = declined.Orch.LaunchProfile(Profile(8), Two(), declined.Ok());
            T.Check("declined warning launches nothing more", declined.Launched.Count == 0, "calls=" + declined.Launched.Count);
            T.Check("declined warning stops the profile", rd.Stopped && rd.Summary == LaunchPlanner.Summary(true, 0, 0, 0), rd.Summary);
            T.Check("warning message is the restriction text plus the profile suffix",
                declined.Asked.Count == 1 && declined.Asked[0].StartsWith("You are beyond the recommended 2 instances")
                && declined.Asked[0].EndsWith(ProfileSuffix), declined.Asked.Count > 0 ? declined.Asked[0] : "none");

            // Accepted warning is asked once for the whole profile.
            Harness accepted = new Harness();
            accepted.RunningCount = 2;
            LaunchReport ra = accepted.Orch.LaunchProfile(Profile(8), Two(), accepted.Ok());
            T.Check("accepted warning launches every account", accepted.Launched.Count == 2, "calls=" + accepted.Launched.Count);
            T.Check("warning is asked only once per profile", accepted.Asked.Count == 1, "asked=" + accepted.Asked.Count);
            T.Check("accepted profile is Done", !ra.Stopped && ra.Summary == LaunchPlanner.Summary(false, 2, 0, 0), ra.Summary);

            // Stagger uses the profile's own seconds, and only after a successful start that has a next account.
            Harness stagger = new Harness();
            LaunchReport rg = stagger.Orch.LaunchProfile(Profile(3), Two(), stagger.Ok());
            T.Check("stagger waits profile.StaggerSeconds once, between the two accounts", stagger.Delays.Count == 3, "delays=" + stagger.Delays.Count);
            bool allOneSecond = true;
            foreach (int d in stagger.Delays) if (d != 1) allOneSecond = false;
            T.Check("each stagger wait is one second", allOneSecond, "");
            T.Check("countdown text comes from LaunchPlanner", stagger.Progress.Contains(LaunchPlanner.StaggerCountdownMessage(1, 2, 3))
                && stagger.Progress.Contains(LaunchPlanner.StaggerCountdownMessage(1, 2, 2))
                && stagger.Progress.Contains(LaunchPlanner.StaggerCountdownMessage(1, 2, 1)), "");
            T.Check("no stagger after the last account", !stagger.Progress.Contains(LaunchPlanner.StaggerCountdownMessage(2, 2, 3)), "");
            T.Check("first progress line names the first account", stagger.Progress.Contains(LaunchPlanner.LaunchingMessage(0, 2, "Alice")), "");
            T.Check("stagger profile is Done", rg.Summary == LaunchPlanner.Summary(false, 2, 0, 0), rg.Summary);

            // Accounts already running are skipped, with no stagger.
            Harness skip = new Harness();
            skip.Orch.IsAccountRunning = id => id == "a1";
            LaunchReport rk = skip.Orch.LaunchProfile(Profile(8), Two(), skip.Ok());
            T.Check("already-running account is not launched", skip.Launched.Count == 1 && skip.Launched[0] == "a2", "");
            T.Check("skip is counted", rk.Summary == LaunchPlanner.Summary(false, 1, 0, 1), rk.Summary);
            T.Check("no stagger after a skip", skip.Delays.Count == 0, "delays=" + skip.Delays.Count);

            // A failed start counts as failed, and no stagger follows it.
            Harness fail = new Harness();
            LaunchReport rf = fail.Orch.LaunchProfile(Profile(8), Two(), id =>
            {
                fail.Launched.Add(id);
                return id == "a1" ? new LaunchResult(false, 0, "boom") : new LaunchResult(true, 1, null);
            });
            T.Check("failed start is counted and the run goes on", rf.Summary == LaunchPlanner.Summary(false, 1, 1, 0), rf.Summary);
            T.Check("no stagger after a failed start", fail.Delays.Count == 0, "delays=" + fail.Delays.Count);
            T.Check("failed account keeps its message", rf.Accounts[0].Outcome == AccountOutcome.Failed && rf.Accounts[0].Message.EndsWith("could not start Roblox: boom"), "");

            // Single launch: one call, Done summary.
            Harness single = new Harness();
            LaunchReport rs1 = single.Orch.LaunchSingle(new LaunchAccount("a1", "Alice"), single.Ok());
            T.Check("single launch calls launchOne once", single.Launched.Count == 1, "");
            T.Check("single launch summary is Done", rs1.Summary == LaunchPlanner.Summary(false, 1, 0, 0), rs1.Summary);

            // Single launch with an accepted warning uses the single-launch suffix.
            Harness singleWarn = new Harness();
            singleWarn.RunningCount = 2;
            LaunchReport rsw = singleWarn.Orch.LaunchSingle(new LaunchAccount("a1", "Alice"), singleWarn.Ok());
            T.Check("accepted single warning launches", singleWarn.Launched.Count == 1 && rsw.Accounts[0].Outcome == AccountOutcome.Launched, "");
            T.Check("single warning uses the single suffix", singleWarn.Asked.Count == 1 && singleWarn.Asked[0].EndsWith(SingleSuffix), singleWarn.Asked.Count > 0 ? singleWarn.Asked[0] : "none");

            // Declined single warning does not launch.
            Harness singleNo = new Harness();
            singleNo.RunningCount = 2;
            singleNo.Accept = false;
            LaunchReport rsn = singleNo.Orch.LaunchSingle(new LaunchAccount("a1", "Alice"), singleNo.Ok());
            T.Check("declined single warning never launches", singleNo.Launched.Count == 0 && rsn.Accounts[0].Outcome == AccountOutcome.Declined, "");

            // Relaunch: same checks, one call.
            Harness relaunch = new Harness();
            LaunchReport rre = relaunch.Orch.Relaunch(new LaunchAccount("a1", "Alice"), relaunch.Ok());
            T.Check("relaunch calls launchOne once after the check", relaunch.Launched.Count == 1 && rre.Accounts.Count == 1, "");

            // A single launch with the token already cancelled does nothing.
            Harness singleCancel = new Harness();
            CancellationTokenSource sc = new CancellationTokenSource();
            sc.Cancel();
            LaunchReport rsc = singleCancel.Orch.LaunchSingle(new LaunchAccount("a1", "Alice"), singleCancel.Ok(), sc.Token);
            T.Check("cancelled single launch does nothing", singleCancel.Launched.Count == 0 && rsc.Stopped, "");

            // Another instance is allowed only when the platform says so.
            Harness multi = new Harness();
            multi.Platform.Capabilities = PlatformCapabilities.None;
            T.Check("no MultiInstance capability: another instance is refused", !multi.Orch.PrepareForAnotherInstance(), "");
            multi.Platform.Capabilities = PlatformCapabilities.MultiInstance;
            T.Check("MultiInstance capability asks the launcher", multi.Orch.PrepareForAnotherInstance(), "");
            multi.Platform.Launcher.Ready = false;
            T.Check("launcher refusal is passed on", !multi.Orch.PrepareForAnotherInstance(), "");
        }
    }
}
