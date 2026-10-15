using System;
using System.Collections.Generic;
using System.Threading;
using CookieJar.Core;
using CookieJar.Platform;

namespace CookieJar.AppModel
{
    /// <summary>One account to start: the id given to launchOne and the name shown in messages.</summary>
    public sealed class LaunchAccount
    {
        public LaunchAccount(string id, string displayName)
        {
            Id = id;
            DisplayName = displayName ?? id;
        }

        public string Id { get; }
        public string DisplayName { get; }
    }

    public enum AccountOutcome
    {
        Launched,
        Failed,
        Skipped,
        /// <summary>The restriction forbids another client. Stops the run.</summary>
        Blocked,
        /// <summary>The user declined a resource warning. Stops the run.</summary>
        Declined
    }

    public sealed class AccountReport
    {
        public AccountReport(LaunchAccount account, AccountOutcome outcome, string message)
        {
            Account = account;
            Outcome = outcome;
            Message = message;
        }

        public LaunchAccount Account { get; }
        public AccountOutcome Outcome { get; }

        /// <summary>The blocked or failure text, or null.</summary>
        public string Message { get; }
    }

    /// <summary>Result of LaunchProfile, LaunchSingle or Relaunch.</summary>
    public sealed class LaunchReport
    {
        public LaunchReport(IList<AccountReport> accounts, bool stopped, string summary)
        {
            Accounts = accounts;
            Stopped = stopped;
            Summary = summary;
        }

        /// <summary>One entry per account that was attempted, in order.</summary>
        public IList<AccountReport> Accounts { get; }
        public bool Stopped { get; }

        /// <summary>The one-line result from LaunchPlanner.Summary.</summary>
        public string Summary { get; }
    }

    /// <summary>
    /// Launch decisions for the app: restriction check, warning confirmation, stagger between accounts and
    /// cancellation. Starting a client is delegated to launchOne, so this type needs no process or window code.
    /// </summary>
    public sealed class LaunchOrchestrator
    {
        private const string SingleWarnSuffix = "\n\nLaunch another instance anyway? Launching continues automatically when the countdown ends.";
        private const string ProfileWarnSuffix = "\n\nKeep launching the rest of this profile anyway? Launching continues automatically when the countdown ends.";

        private readonly IPlatform _platform;
        private readonly Restrictor _restrictor;
        private readonly Func<int> _runningCount;
        private readonly Func<double> _freeRamGb;
        private readonly Func<double> _cpuPercent;
        private readonly Func<int, CancellationToken, bool> _delay;

        /// <param name="delay">Waits the given seconds. Returns false when cancelled. Tests pass a fake; null means real time.</param>
        public LaunchOrchestrator(IPlatform platform, Restrictor restrictor, Func<int> runningCount,
            Func<double> freeRamGb, Func<double> cpuPercent, Func<int, CancellationToken, bool> delay = null)
        {
            if (platform == null) throw new ArgumentNullException(nameof(platform));
            if (restrictor == null) throw new ArgumentNullException(nameof(restrictor));
            if (runningCount == null) throw new ArgumentNullException(nameof(runningCount));
            if (freeRamGb == null) throw new ArgumentNullException(nameof(freeRamGb));
            if (cpuPercent == null) throw new ArgumentNullException(nameof(cpuPercent));
            _platform = platform;
            _restrictor = restrictor;
            _runningCount = runningCount;
            _freeRamGb = freeRamGb;
            _cpuPercent = cpuPercent;
            _delay = delay ?? new Func<int, CancellationToken, bool>(WaitRealSeconds);
            Events = new LaunchEvents();
        }

        public LaunchEvents Events { get; }

        /// <summary>True when the account already has a running client. Null means nothing is running.</summary>
        public Func<string, bool> IsAccountRunning { get; set; }

        /// <summary>The restriction result from the last check, or null before the first check.</summary>
        public RestrictResult LastRestriction { get; private set; }

        /// <summary>
        /// Evaluates the restriction and returns what to do. Block: never launch. Warn: asks the user unless
        /// warnConfirmed is set. Returns Launch when allowed or accepted, and ConfirmWarn when the user declined.
        /// </summary>
        public LaunchAction CheckRestriction(string warningSuffix, bool warnConfirmed)
        {
            RestrictResult result = _restrictor.Evaluate(_runningCount(), _freeRamGb(), _cpuPercent());
            LastRestriction = result;
            LaunchAction action = LaunchPlanner.Decide(false, result, warnConfirmed);
            if (action != LaunchAction.ConfirmWarn) return action;

            bool accepted = Ask(result.Message + (warningSuffix ?? ""));
            return accepted ? LaunchAction.Launch : LaunchAction.ConfirmWarn;
        }

        /// <summary>Starts one account after the restriction check. Already-running accounts are started anyway.</summary>
        public LaunchReport LaunchSingle(LaunchAccount account, Func<string, LaunchResult> launchOne,
            CancellationToken ct = default(CancellationToken))
        {
            CheckArgs(account, launchOne);
            List<AccountReport> reports = new List<AccountReport>();
            if (ct.IsCancellationRequested) return Finish(reports, true);

            bool warnAccepted;
            AccountReport report = LaunchAccountOnce(account, SingleWarnSuffix, false, launchOne, out warnAccepted);
            reports.Add(report);
            if (report.Outcome == AccountOutcome.Blocked) Log("Launch not allowed: " + report.Message);
            bool stopped = report.Outcome == AccountOutcome.Blocked || report.Outcome == AccountOutcome.Declined;
            return Finish(reports, stopped);
        }

        /// <summary>Starts one account again. Same checks as LaunchSingle.</summary>
        public LaunchReport Relaunch(LaunchAccount account, Func<string, LaunchResult> launchOne,
            CancellationToken ct = default(CancellationToken))
        {
            return LaunchSingle(account, launchOne, ct);
        }

        /// <summary>
        /// Starts the accounts one after another. Accounts already running are skipped. A blocked restriction or
        /// a declined warning stops the run. Cancel is honoured before each account and during the stagger.
        /// </summary>
        public LaunchReport LaunchProfile(LaunchProfile profile, IList<LaunchAccount> accounts,
            Func<string, LaunchResult> launchOne, CancellationToken ct = default(CancellationToken))
        {
            if (profile == null) throw new ArgumentNullException(nameof(profile));
            if (accounts == null) throw new ArgumentNullException(nameof(accounts));
            if (launchOne == null) throw new ArgumentNullException(nameof(launchOne));

            int total = accounts.Count;
            List<AccountReport> reports = new List<AccountReport>();
            bool stopped = false;
            bool warnConfirmed = false;

            for (int i = 0; i < total && !stopped; i++)
            {
                if (ct.IsCancellationRequested)
                {
                    stopped = true;
                    break;
                }

                LaunchAccount account = accounts[i];
                string who = account.DisplayName;
                Emit(Events.Progress, LaunchPlanner.LaunchingMessage(i, total, who));

                if (IsRunning(account.Id))
                {
                    reports.Add(new AccountReport(account, AccountOutcome.Skipped, null));
                    Log(who + ": skipped, already running");
                    continue;
                }

                bool warnAccepted;
                AccountReport report = LaunchAccountOnce(account, ProfileWarnSuffix, warnConfirmed, launchOne, out warnAccepted);
                reports.Add(report);
                warnConfirmed = warnConfirmed || warnAccepted;

                if (report.Outcome == AccountOutcome.Blocked)
                {
                    Log("Profile launch stopped: " + report.Message);
                    stopped = true;
                    break;
                }
                if (report.Outcome == AccountOutcome.Declined)
                {
                    Log("Profile launch stopped at your request (warning declined)");
                    stopped = true;
                    break;
                }

                bool more = i < total - 1;
                int stagger = LaunchPlanner.StaggerSecondsAfter(report.Outcome == AccountOutcome.Launched, more, profile);
                if (!Countdown(i + 1, total, stagger, ct)) stopped = true;
            }

            LaunchReport result = Finish(reports, stopped);
            Log("Profile \"" + profile.Name + "\": " + result.Summary);
            return result;
        }

        /// <summary>
        /// Asks the platform to allow another client while others run. False when the platform lacks MultiInstance.
        /// Not called by the launch paths above; the UI calls it once before a multi-client launch.
        /// </summary>
        public bool PrepareForAnotherInstance()
        {
            if (!_platform.Supports(PlatformCapabilities.MultiInstance)) return false;
            IRobloxLauncher launcher = _platform.RobloxLauncher;
            // MAC-UNVERIFIED: assumes the macOS launcher can allow a second client; the proof of concept has not run on a Mac yet.
            return launcher != null && launcher.PrepareForAnotherInstance();
        }

        private AccountReport LaunchAccountOnce(LaunchAccount account, string warningSuffix, bool warnConfirmed,
            Func<string, LaunchResult> launchOne, out bool warnAccepted)
        {
            warnAccepted = false;
            string who = account.DisplayName;

            LaunchAction action = CheckRestriction(warningSuffix, warnConfirmed);
            if (action == LaunchAction.Block)
            {
                return new AccountReport(account, AccountOutcome.Blocked, LaunchPlanner.BlockedMessage(LastRestriction, who));
            }
            if (action == LaunchAction.ConfirmWarn)
            {
                return new AccountReport(account, AccountOutcome.Declined, null);
            }
            warnAccepted = LastRestriction.Level == RestrictLevel.Warn;

            // MAC-UNVERIFIED: launchOne is assumed to start the client through IRobloxLauncher on macOS; not run on a Mac yet.
            LaunchResult result = launchOne(account.Id);
            if (result != null && result.Started)
            {
                Log(who + ": ok");
                return new AccountReport(account, AccountOutcome.Launched, null);
            }

            string error = result == null || result.Error == null ? "no answer from the launcher" : result.Error;
            string text = who + ": could not start Roblox: " + error;
            Log(text);
            return new AccountReport(account, AccountOutcome.Failed, text);
        }

        private bool Countdown(int launched, int total, int seconds, CancellationToken ct)
        {
            for (int s = seconds; s > 0; s--)
            {
                if (ct.IsCancellationRequested) return false;
                Emit(Events.Progress, LaunchPlanner.StaggerCountdownMessage(launched, total, s));
                if (!_delay(1, ct)) return false;
            }
            return true;
        }

        private LaunchReport Finish(List<AccountReport> reports, bool stopped)
        {
            int ok = 0, failed = 0, skipped = 0;
            foreach (AccountReport r in reports)
            {
                if (r.Outcome == AccountOutcome.Launched) ok++;
                else if (r.Outcome == AccountOutcome.Failed) failed++;
                else if (r.Outcome == AccountOutcome.Skipped) skipped++;
            }
            return new LaunchReport(reports, stopped, LaunchPlanner.Summary(stopped, ok, failed, skipped));
        }

        private bool IsRunning(string accountId)
        {
            return IsAccountRunning != null && IsAccountRunning(accountId);
        }

        private bool Ask(string message)
        {
            Func<string, bool> confirm = Events.ConfirmWarning;
            return confirm == null || confirm(message);
        }

        private void Log(string text)
        {
            Emit(Events.Log, text);
        }

        private static void Emit(Action<string> sink, string text)
        {
            if (sink != null) sink(text);
        }

        private static void CheckArgs(LaunchAccount account, Func<string, LaunchResult> launchOne)
        {
            if (account == null) throw new ArgumentNullException(nameof(account));
            if (launchOne == null) throw new ArgumentNullException(nameof(launchOne));
        }

        private static bool WaitRealSeconds(int seconds, CancellationToken ct)
        {
            return !ct.WaitHandle.WaitOne(seconds * 1000);
        }
    }
}
