using System;
using System.Threading;

namespace RbxMulti
{
    internal static class GuardFailureTests
    {
        internal static void Run(Action<string, bool> check)
        {
            FailedStartCanBeDisposed(check);
            RetryAfterFailedStart(check);
        }

        private static string Prefix()
        {
            return "CookieJar.GuardFailureTests." + Guid.NewGuid().ToString("N");
        }

        private static void FailedStartCanBeDisposed(Action<string, bool> check)
        {
            string prefix = Prefix();
            int attempts = 0;
            using (MultiInstanceGuard guard = new MultiInstanceGuard(prefix + ".mutex", prefix + ".event",
                delegate(Thread thread)
                {
                    attempts++;
                    throw new OutOfMemoryException("Simulated thread-start failure");
                }))
            {
                string error;
                check("guard startup failure returns a useful error", !guard.EnsureReady(out error)
                    && !string.IsNullOrEmpty(error));
                check("failed startup does not poison the next attempt", !guard.EnsureReady(out error)
                    && attempts == 2);
                guard.Dispose();
                guard.Dispose();
                check("unstarted guard disposal is safe and repeatable", true);
            }
        }

        private static void RetryAfterFailedStart(Action<string, bool> check)
        {
            string prefix = Prefix();
            int attempts = 0;
            using (MultiInstanceGuard guard = new MultiInstanceGuard(prefix + ".mutex", prefix + ".event",
                delegate(Thread thread)
                {
                    if (++attempts == 1) throw new OutOfMemoryException("Simulated thread-start failure");
                    thread.Start();
                }))
            {
                string error;
                check("first transient guard startup failure is reported", !guard.EnsureReady(out error));
                check("guard retry succeeds after startup resources recover", guard.EnsureReady(out error)
                    && attempts == 2 && error == null);
                using (Mutex singleton = new Mutex(false, prefix + ".mutex"))
                {
                    bool acquired = singleton.WaitOne(0);
                    if (acquired) singleton.ReleaseMutex();
                    check("retried guard owns the singleton on its worker", !acquired);
                }
            }
            using (EventWaitHandle routing = new EventWaitHandle(false, EventResetMode.AutoReset, prefix + ".event"))
                check("retried guard releases its routing name on disposal", routing != null);
        }
    }
}
