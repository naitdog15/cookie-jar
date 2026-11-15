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
            // 1.2.2 behaviour: free singleton handles still held by running clients, then close
            // the new client's own handles right after it starts (CloseSingletons below).
            // Holding the names ourselves (the 1.3.x MultiInstanceGuard) made each new client take
            // Roblox's single-instance path and end the older games, so it is no longer used.
            error = null;
            closed = HandleCloser.CloseSingletonsInAllRoblox();
            return true;
        }
        internal virtual int CloseSingletons(int pid) { return HandleCloser.CloseSingletons(pid); }
        internal virtual string FindPlayerExe() { return RobloxLocator.FindPlayerExe(); }
        internal virtual List<int> GetInstallerPids() { return RobloxProcesses.GetInstallerPids(); }
        /// <summary>Players from the last locator run whose version is higher than exe's. Empty when none or unknown.</summary>
        internal virtual List<string> NewerPlayersThan(string exe)
        {
            List<string> newer = new List<string>();
            PlayerCandidate chosen = null;
            List<PlayerCandidate> all = RobloxLocator.GetLastCandidates();
            foreach (PlayerCandidate c in all) if (string.Equals(c.ExePath, exe, StringComparison.OrdinalIgnoreCase)) chosen = c;
            foreach (PlayerCandidate c in all)
                if (c.Version != null && (chosen == null || chosen.Version == null || c.Version > chosen.Version) && c != chosen)
                    newer.Add(c.ExePath + " (" + c.VersionText + ")");
            return newer;
        }
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
