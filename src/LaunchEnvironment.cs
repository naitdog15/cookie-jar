using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;

namespace RbxMulti
{
    /// <summary>
    /// OS/network boundaries for account launching. Tests replace these boundaries while
    /// exercising the real launch, adoption and profile state transitions.
    /// </summary>
    internal class LaunchEnvironment
    {
        internal virtual List<int> GetPids() { return RobloxProcesses.GetPids(); }
        internal virtual HashSet<int> VisiblePids() { return RobloxProcesses.PidsWithVisibleWindow(); }
        internal virtual int EndIdleTrayClients(IEnumerable<int> protectedPids)
        {
            return RobloxProcesses.EndIdleTrayClients(protectedPids);
        }
        internal virtual bool PrepareMultiInstance(out string error, out int closed)
        {
            closed = HandleCloser.CloseSingletonsInAllRoblox();
            return MultiInstanceGuard.Shared.EnsureReady(out error);
        }
        internal virtual string FindPlayerExe() { return RobloxLocator.FindPlayerExe(); }
        internal virtual void ClearCache() { CacheCleaner.ClearCache(); }
        internal virtual bool GetTicket(string token, out string ticket, out bool expired, out string error)
        {
            return TicketLauncher.GetTicket(token, out ticket, out expired, out error);
        }
        internal virtual Process StartPlayer(ProcessStartInfo info) { return Process.Start(info); }
        internal virtual bool HasWindow(int pid) { return WindowManager.FindWindowForPid(pid, 0) != IntPtr.Zero; }
        internal virtual DateTime UtcNow { get { return DateTime.UtcNow; } }
        internal virtual void Wait(CancellationToken ct) { ct.WaitHandle.WaitOne(250); }
    }
}
