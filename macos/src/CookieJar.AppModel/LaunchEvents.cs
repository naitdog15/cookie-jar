using System;

namespace CookieJar.AppModel
{
    /// <summary>
    /// Callbacks the UI hands to LaunchOrchestrator. The orchestrator never blocks on UI itself:
    /// ConfirmWarning is expected to run a TimedWarning countdown and return its Result.
    /// </summary>
    public sealed class LaunchEvents
    {
        /// <summary>Asks about a resource warning. True continues. Null means no UI: continue, like a timeout.</summary>
        public Func<string, bool> ConfirmWarning { get; set; }

        /// <summary>One-line progress text, for example "Launching 2/3: Name...".</summary>
        public Action<string> Progress { get; set; }

        /// <summary>One line per account decision, for the log.</summary>
        public Action<string> Log { get; set; }

        /// <summary>
        /// Raised with the account id when a ticket failed because the saved login expired, so the app can mark the
        /// account Expired (the Windows runner did AccountStore.SetStatus(id, "Expired")). Optional.
        /// </summary>
        public Action<string> AccountTokenExpired { get; set; }
    }
}
