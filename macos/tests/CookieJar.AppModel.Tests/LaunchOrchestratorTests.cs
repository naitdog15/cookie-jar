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
            public readonly List<LaunchRequest> Requests = new List<LaunchRequest>();
            public readonly List<string> Expired = new List<string>();
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
                Orch.Events.AccountTokenExpired = id => Expired.Add(id);
            }

            /// <summary>A launchOne that records the request and starts the client.</summary>
            public System.Func<LaunchRequest, AccountLaunchResult> Ok()
            {
                return req => { Launched.Add(req.Account.Id); Requests.Add(req); return AccountLaunchResult.Ok(100); };
            }

            /// <summary>A launchOne that records the request and answers with the given result.</summary>
            public System.Func<LaunchRequest, AccountLaunchResult> Answers(System.Func<LaunchRequest, AccountLaunchResult> answer)
            {
                return req => { Launched.Add(req.Account.Id); Requests.Add(req); return answer(req); };
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
            LaunchReport rs = blockedSingle.Orch.LaunchSingle(new LaunchAccount("a1", "Alice"), null, blockedSingle.Ok());
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
            LaunchReport rr = lowRam.Orch.LaunchSingle(new LaunchAccount("a1", "Alice"), null, lowRam.Ok());
            T.Check("low free RAM blocks without launching", lowRam.Launched.Count == 0 && rr.Accounts[0].Outcome == AccountOutcome.Blocked, "");

            // Cancel before the second account: the first one is launched, the run stops and the summary says so.
            Harness cancel = new Harness();
            CancellationTokenSource cts = new CancellationTokenSource();
            LaunchReport rc = cancel.Orch.LaunchProfile(Profile(8), Two(), req =>
            {
                cancel.Launched.Add(req.Account.Id);
                cts.Cancel();
                return AccountLaunchResult.Ok(100);
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
            LaunchReport rf = fail.Orch.LaunchProfile(Profile(8), Two(), req =>
            {
                fail.Launched.Add(req.Account.Id);
                return req.Account.Id == "a1" ? AccountLaunchResult.Failed("boom") : AccountLaunchResult.Ok(1);
            });
            T.Check("failed start is counted and the run goes on", rf.Summary == LaunchPlanner.Summary(false, 1, 1, 0), rf.Summary);
            T.Check("no stagger after a failed start", fail.Delays.Count == 0, "delays=" + fail.Delays.Count);
            T.Check("failed account keeps its message", rf.Accounts[0].Outcome == AccountOutcome.Failed && rf.Accounts[0].Message.EndsWith("could not start Roblox: boom"), "");

            // Single launch: one call, Done summary.
            Harness single = new Harness();
            LaunchReport rs1 = single.Orch.LaunchSingle(new LaunchAccount("a1", "Alice"), null, single.Ok());
            T.Check("single launch calls launchOne once", single.Launched.Count == 1, "");
            T.Check("single launch summary is Done", rs1.Summary == LaunchPlanner.Summary(false, 1, 0, 0), rs1.Summary);

            // Single launch with an accepted warning uses the single-launch suffix.
            Harness singleWarn = new Harness();
            singleWarn.RunningCount = 2;
            LaunchReport rsw = singleWarn.Orch.LaunchSingle(new LaunchAccount("a1", "Alice"), null, singleWarn.Ok());
            T.Check("accepted single warning launches", singleWarn.Launched.Count == 1 && rsw.Accounts[0].Outcome == AccountOutcome.Launched, "");
            T.Check("single warning uses the single suffix", singleWarn.Asked.Count == 1 && singleWarn.Asked[0].EndsWith(SingleSuffix), singleWarn.Asked.Count > 0 ? singleWarn.Asked[0] : "none");

            // Declined single warning does not launch.
            Harness singleNo = new Harness();
            singleNo.RunningCount = 2;
            singleNo.Accept = false;
            LaunchReport rsn = singleNo.Orch.LaunchSingle(new LaunchAccount("a1", "Alice"), null, singleNo.Ok());
            T.Check("declined single warning never launches", singleNo.Launched.Count == 0 && rsn.Accounts[0].Outcome == AccountOutcome.Declined, "");

            // Relaunch: same checks, one call.
            Harness relaunch = new Harness();
            LaunchReport rre = relaunch.Orch.Relaunch(new LaunchAccount("a1", "Alice"), null, relaunch.Ok());
            T.Check("relaunch calls launchOne once after the check", relaunch.Launched.Count == 1 && rre.Accounts.Count == 1, "");

            // A single launch with the token already cancelled does nothing.
            Harness singleCancel = new Harness();
            CancellationTokenSource sc = new CancellationTokenSource();
            sc.Cancel();
            LaunchReport rsc = singleCancel.Orch.LaunchSingle(new LaunchAccount("a1", "Alice"), null, singleCancel.Ok(), sc.Token);
            T.Check("cancelled single launch does nothing", singleCancel.Launched.Count == 0 && rsc.Stopped, "");

            // Another instance is allowed only when the platform says so.
            Harness multi = new Harness();
            multi.Platform.Capabilities = PlatformCapabilities.None;
            T.Check("no MultiInstance capability: another instance is refused", !multi.Orch.PrepareForAnotherInstance(), "");
            multi.Platform.Capabilities = PlatformCapabilities.MultiInstance;
            T.Check("MultiInstance capability asks the launcher", multi.Orch.PrepareForAnotherInstance(), "");
            multi.Platform.Launcher.Ready = false;
            T.Check("launcher refusal is passed on", !multi.Orch.PrepareForAnotherInstance(), "");

            RequestTests();
            OutcomeTests();
            CancellationAndWarningTests();
        }

        // What launchOne receives: the same inputs the Windows runner passes to LaunchAccount.
        private static void RequestTests()
        {
            LaunchProfile prof = Profile(0);
            prof.Name = "Farm";
            prof.PlaceId = 920587237L;

            Harness h = new Harness();
            CancellationTokenSource cts = new CancellationTokenSource();
            h.Orch.LaunchProfile(prof, Two(), h.Ok(), cts.Token);
            T.Check("each request carries the profile name, place id and the 60 s timeout",
                h.Requests.Count == 2 && h.Requests[0].ProfileName == "Farm" && h.Requests[0].PlaceId == 920587237L
                && h.Requests[0].TimeoutSeconds == 60 && h.Requests[1].ProfileName == "Farm", "n=" + h.Requests.Count);
            T.Check("each request carries the run's cancellation token", h.Requests.Count == 2 && h.Requests[0].Cancellation == cts.Token);
            T.Check("only the first launch of a run may clear the cache", h.Requests.Count == 2 && h.Requests[0].ClearCache && !h.Requests[1].ClearCache);

            // A skipped account does not use up the cache flag; a failed first launch does (like cacheHandled in the Windows runner).
            Harness skip = new Harness();
            skip.Orch.IsAccountRunning = id => id == "a1";
            skip.Orch.LaunchProfile(Profile(0), Two(), skip.Ok());
            T.Check("a skipped account does not consume the cache flag", skip.Requests.Count == 1 && skip.Requests[0].ClearCache);

            Harness failFirst = new Harness();
            failFirst.Orch.LaunchProfile(Profile(0), Two(), failFirst.Answers(r => r.Account.Id == "a1" ? AccountLaunchResult.Failed("x") : AccountLaunchResult.Ok(2)));
            T.Check("a failed first launch still counts as the cache launch", failFirst.Requests.Count == 2 && failFirst.Requests[0].ClearCache && !failFirst.Requests[1].ClearCache);

            Harness single = new Harness();
            single.Orch.LaunchSingle(new LaunchAccount("a1", "Alice"), null, single.Ok());
            T.Check("a single launch without a profile uses the 'Single launch' name and the default game",
                single.Requests.Count == 1 && single.Requests[0].ProfileName == "Single launch" && single.Requests[0].PlaceId == LaunchProfile.DefaultPlaceId && single.Requests[0].ClearCache);

            Harness withProfile = new Harness();
            withProfile.Orch.LaunchSingle(new LaunchAccount("a1", "Alice"), prof, withProfile.Ok());
            T.Check("a single launch with a profile carries its name and place id",
                withProfile.Requests.Count == 1 && withProfile.Requests[0].ProfileName == "Farm" && withProfile.Requests[0].PlaceId == 920587237L);

            AccountLaunchResult started = AccountLaunchResult.FromPlatform(new LaunchResult(true, 321, null));
            AccountLaunchResult notStarted = AccountLaunchResult.FromPlatform(new LaunchResult(false, 0, "open failed"));
            AccountLaunchResult nothing = AccountLaunchResult.FromPlatform(null);
            T.Check("platform LaunchResult adapter: started -> Ok(pid)", started.Kind == AccountLaunchKind.Ok && started.Pid == 321);
            T.Check("platform LaunchResult adapter: not started -> Failed with the error", notStarted.Kind == AccountLaunchKind.Failed && notStarted.Message == "open failed");
            T.Check("platform LaunchResult adapter: null -> Failed 'no answer'", nothing.Kind == AccountLaunchKind.Failed && nothing.Message == "no answer from the launcher");
        }

        // The Windows runner told ticket failure, timeout, cancellation and other failures apart.
        private static void OutcomeTests()
        {
            Harness ticket = new Harness();
            LaunchReport rt = ticket.Orch.LaunchProfile(Profile(8), Two(), ticket.Answers(r =>
                r.Account.Id == "a1" ? AccountLaunchResult.TicketFailed("401 Unauthorized", true) : AccountLaunchResult.Ok(2)));
            T.Check("ticket failure is counted as failed and the run goes on", rt.Summary == LaunchPlanner.Summary(false, 1, 1, 0) && !rt.Stopped, rt.Summary);
            T.Check("ticket failure message is 'name: ticket failed: <reason>'", rt.Accounts[0].Message == "Alice: ticket failed: 401 Unauthorized", rt.Accounts[0].Message);
            T.Check("an expired token raises AccountTokenExpired with the account id", ticket.Expired.Count == 1 && ticket.Expired[0] == "a1", string.Join(",", ticket.Expired));
            T.Check("no stagger after a failed ticket", ticket.Delays.Count == 0);

            Harness ticketOk = new Harness();
            ticketOk.Orch.LaunchSingle(new LaunchAccount("a1", "Alice"), null, ticketOk.Answers(r => AccountLaunchResult.TicketFailed("network down", false)));
            T.Check("a ticket failure that is not an expired token does not raise AccountTokenExpired", ticketOk.Expired.Count == 0);

            Harness timeout = new Harness();
            LaunchReport rto = timeout.Orch.LaunchSingle(new LaunchAccount("a1", "Alice"), null, timeout.Answers(r => AccountLaunchResult.TimedOut()));
            T.Check("timeout message names the 60 s window and the Adopt hint",
                rto.Accounts[0].Outcome == AccountOutcome.Failed
                && rto.Accounts[0].Message == "Alice: timed out - no new Roblox window within 60 seconds (if it appears later, use Adopt running clients)", rto.Accounts[0].Message);

            Harness cancelled = new Harness();
            LaunchReport rcn = cancelled.Orch.LaunchProfile(Profile(8), Two(), cancelled.Answers(r => AccountLaunchResult.Cancelled()));
            T.Check("a cancelled launch stops the run without starting the next account", cancelled.Launched.Count == 1 && rcn.Stopped, "calls=" + cancelled.Launched.Count);
            T.Check("a cancelled launch is reported as Cancelled and is not counted as failed",
                rcn.Accounts[0].Outcome == AccountOutcome.Cancelled && rcn.Summary == LaunchPlanner.Summary(true, 0, 0, 0), rcn.Summary);
            T.Check("no stagger after a cancelled launch", cancelled.Delays.Count == 0);

            Harness thrown = new Harness();
            LaunchReport rth = thrown.Orch.LaunchSingle(new LaunchAccount("a1", "Alice"), null, thrown.Answers(r => { throw new System.InvalidOperationException("disk full"); }));
            T.Check("an exception from launchOne becomes a failed account with its message", rth.Accounts[0].Outcome == AccountOutcome.Failed && rth.Accounts[0].Message.EndsWith("disk full"), rth.Accounts[0].Message);

            Harness oce = new Harness();
            LaunchReport roc = oce.Orch.LaunchSingle(new LaunchAccount("a1", "Alice"), null, oce.Answers(r => { throw new System.OperationCanceledException(); }));
            T.Check("an OperationCanceledException from launchOne counts as cancelled", roc.Accounts[0].Outcome == AccountOutcome.Cancelled && roc.Stopped);

            Harness nothing = new Harness();
            LaunchReport rn = nothing.Orch.LaunchSingle(new LaunchAccount("a1", "Alice"), null, nothing.Answers(r => null));
            T.Check("a null answer from launchOne is a failed account", rn.Accounts[0].Outcome == AccountOutcome.Failed && rn.Accounts[0].Message.EndsWith("no answer from the launcher"));
        }

        private static void CancellationAndWarningTests()
        {
            // The warning question can take seconds; cancelling meanwhile must stop before any client starts.
            Harness cancelWhileAsking = new Harness();
            cancelWhileAsking.RunningCount = 2;
            CancellationTokenSource cts = new CancellationTokenSource();
            cancelWhileAsking.Orch.Events.ConfirmWarning = m => { cts.Cancel(); return true; };
            LaunchReport rc = cancelWhileAsking.Orch.LaunchProfile(Profile(8), Two(), cancelWhileAsking.Ok(), cts.Token);
            T.Check("cancel pressed while the warning is shown starts nothing", cancelWhileAsking.Launched.Count == 0 && rc.Stopped, "calls=" + cancelWhileAsking.Launched.Count);
            T.Check("that account is reported as Cancelled", rc.Accounts.Count == 1 && rc.Accounts[0].Outcome == AccountOutcome.Cancelled);

            Harness singleCancel = new Harness();
            singleCancel.RunningCount = 2;
            CancellationTokenSource sc = new CancellationTokenSource();
            singleCancel.Orch.Events.ConfirmWarning = m => { sc.Cancel(); return true; };
            LaunchReport rsc = singleCancel.Orch.LaunchSingle(new LaunchAccount("a1", "Alice"), null, singleCancel.Ok(), sc.Token);
            T.Check("cancel pressed while a single-launch warning is shown starts nothing", singleCancel.Launched.Count == 0 && rsc.Stopped);

            // Nobody to ask: do not launch past a warning (the Windows runner always has a confirmation callback).
            Harness noHandler = new Harness();
            noHandler.RunningCount = 2;
            noHandler.Orch.Events.ConfirmWarning = null;
            LaunchReport rnh = noHandler.Orch.LaunchProfile(Profile(8), Two(), noHandler.Ok());
            T.Check("a warning with no confirmation handler is declined, not accepted",
                noHandler.Launched.Count == 0 && rnh.Stopped && rnh.Accounts[0].Outcome == AccountOutcome.Declined, "calls=" + noHandler.Launched.Count);

            Harness noHandlerFine = new Harness();
            noHandlerFine.Orch.Events.ConfirmWarning = null;
            noHandlerFine.Orch.LaunchProfile(Profile(0), Two(), noHandlerFine.Ok());
            T.Check("without a warning the missing handler does not matter", noHandlerFine.Launched.Count == 2);

            // The token is passed through so a long start can be interrupted.
            Harness live = new Harness();
            CancellationTokenSource lc = new CancellationTokenSource();
            bool sawToken = false;
            live.Orch.LaunchSingle(new LaunchAccount("a1", "Alice"), null, live.Answers(r => { sawToken = r.Cancellation == lc.Token; return AccountLaunchResult.Ok(1); }), lc.Token);
            T.Check("a single launch passes its token to launchOne", sawToken);
        }
    }
}
