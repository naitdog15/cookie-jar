using System.Collections.Generic;

namespace CookieJar.Platform
{
    /// <summary>
    /// Process queries and control. Thread-safe. Unsupported operations return false/empty and the
    /// matching PlatformCapabilities flag is absent; no member throws for "unsupported", access denied,
    /// or a missing pid.
    /// </summary>
    public interface IProcessService
    {
        /// <summary>True if the pid is alive (not a zombie/exited).</summary>
        bool IsRunning(int pid);

        /// <summary>Resident memory in bytes, or -1 if unknown or the process is gone.</summary>
        long GetWorkingSetBytes(int pid);

        /// <summary>Pids whose executable name equals <paramref name="processName"/> (case-insensitive, no extension, e.g. "RobloxPlayerBeta").</summary>
        IReadOnlyList<int> FindByName(string processName);

        /// <summary>All descendants of pid (children, grandchildren...), leaves first. Empty if none.</summary>
        IReadOnlyList<int> GetDescendants(int pid);

        /// <summary>Kills pid and all descendants, leaves first. Returns the number of processes killed (0 on failure).</summary>
        int KillTree(int pid);

        /// <summary>Puts pid in a low-priority/efficiency mode. Needs BackgroundPriority. False if unsupported or failed.</summary>
        bool SetBackgroundPriority(int pid);

        /// <summary>Restores normal priority. Needs BackgroundPriority. False if unsupported or failed.</summary>
        bool ClearBackgroundPriority(int pid);

        /// <summary>Caps resident memory to <paramref name="maxBytes"/> (soft). Needs MemoryCap. False if unsupported or failed.</summary>
        bool TryCapWorkingSet(int pid, long maxBytes);

        /// <summary>Removes a cap set by TryCapWorkingSet. Needs MemoryCap. False if unsupported or failed.</summary>
        bool ClearWorkingSetCap(int pid);

        /// <summary>True when the app runs with elevated rights (Windows admin / macOS root).</summary>
        bool IsElevated();
    }
}
