using System.Threading;
using CookieJar.Platform;

namespace CookieJar.AppModel
{
    /// <summary>How one account's launch ended. Mirrors the outcomes of the Windows app's LaunchAccount.</summary>
    public enum AccountLaunchKind
    {
        /// <summary>A new client started.</summary>
        Ok,
        /// <summary>The login ticket could not be obtained (see TokenExpired).</summary>
        TicketFailed,
        /// <summary>No new Roblox window appeared within the request's timeout.</summary>
        TimedOut,
        /// <summary>Cancelled while starting. Stops the whole run.</summary>
        Cancelled,
        /// <summary>The client could not be started for another reason.</summary>
        Failed
    }

    /// <summary>What launchOne reports back for one account.</summary>
    public sealed class AccountLaunchResult
    {
        private AccountLaunchResult(AccountLaunchKind kind, int pid, string message, bool tokenExpired)
        {
            Kind = kind;
            Pid = pid;
            Message = message;
            TokenExpired = tokenExpired;
        }

        public AccountLaunchKind Kind { get; }
        /// <summary>Process id of the new client for Ok, otherwise 0.</summary>
        public int Pid { get; }
        /// <summary>The failure text for TicketFailed and Failed; may be null.</summary>
        public string Message { get; }
        /// <summary>True when the ticket failed because the saved login expired (the app marks the account Expired).</summary>
        public bool TokenExpired { get; }

        public static AccountLaunchResult Ok(int pid) { return new AccountLaunchResult(AccountLaunchKind.Ok, pid, null, false); }
        public static AccountLaunchResult TicketFailed(string message, bool tokenExpired) { return new AccountLaunchResult(AccountLaunchKind.TicketFailed, 0, message, tokenExpired); }
        public static AccountLaunchResult TimedOut() { return new AccountLaunchResult(AccountLaunchKind.TimedOut, 0, null, false); }
        public static AccountLaunchResult Cancelled() { return new AccountLaunchResult(AccountLaunchKind.Cancelled, 0, null, false); }
        public static AccountLaunchResult Failed(string message) { return new AccountLaunchResult(AccountLaunchKind.Failed, 0, message, false); }

        /// <summary>Adapter for a launcher that only knows the platform's LaunchResult (started or not).</summary>
        public static AccountLaunchResult FromPlatform(LaunchResult result)
        {
            if (result != null && result.Started) return Ok(result.Pid);
            return Failed(result == null || result.Error == null ? "no answer from the launcher" : result.Error);
        }
    }

    /// <summary>
    /// Everything launchOne needs to start one account: the same inputs the Windows runner passes to LaunchAccount
    /// (profile name, place id, timeout, cancellation, and whether this is the first launch of the run).
    /// </summary>
    public sealed class LaunchRequest
    {
        /// <summary>Seconds to wait for the new client's window (the Windows app uses 60).</summary>
        public const int DefaultTimeoutSeconds = 60;

        public LaunchRequest(LaunchAccount account, string profileName, long placeId, int timeoutSeconds,
            CancellationToken cancellation, bool clearCache)
        {
            Account = account;
            ProfileName = profileName;
            PlaceId = placeId;
            TimeoutSeconds = timeoutSeconds;
            Cancellation = cancellation;
            ClearCache = clearCache;
        }

        public LaunchAccount Account { get; }
        public string ProfileName { get; }
        public long PlaceId { get; }
        public int TimeoutSeconds { get; }
        /// <summary>Cancelled by the user; launchOne must stop waiting and return Cancelled.</summary>
        public CancellationToken Cancellation { get; }
        /// <summary>True for the first launch of a run: the Roblox cache may be cleared (only if no client is running).</summary>
        public bool ClearCache { get; }
    }
}
