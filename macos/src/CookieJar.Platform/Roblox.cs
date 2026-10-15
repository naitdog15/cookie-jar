using System.Collections.Generic;

namespace CookieJar.Platform
{
    /// <summary>Finds the installed Roblox player. Thread-safe; may cache.</summary>
    public interface IRobloxLocator
    {
        /// <summary>Full path of the player executable (or .app bundle on macOS), or null if not installed.</summary>
        string FindPlayerPath();

        /// <summary>Folder holding the installed version(s), or null.</summary>
        string FindInstallRoot();

        /// <summary>The process name of a running player (e.g. "RobloxPlayerBeta").</summary>
        string PlayerProcessName { get; }

        /// <summary>True if a player is installed.</summary>
        bool IsInstalled();

        /// <summary>Folders holding Roblox cache and temp files that are safe to delete. Empty if none.</summary>
        IReadOnlyList<string> GetCacheDirectories();

        /// <summary>
        /// Removes Roblox login state kept by the OS (Windows: RobloxCookies.dat and login registry values; macOS: cookie files and defaults).
        /// Returns human readable descriptions of what was removed. Never throws.
        /// </summary>
        IReadOnlyList<string> ClearLoginState();

        /// <summary>True if Roblox login state is present.</summary>
        bool HasLoginState();

        /// <summary>Drops cached locations so the next call re-scans.</summary>
        void InvalidateCache();
    }

    /// <summary>Result of starting a client.</summary>
    public sealed class LaunchResult
    {
        public LaunchResult(bool started, int pid, string error)
        {
            Started = started; Pid = pid; Error = error;
        }
        /// <summary>True when a client process was found or started.</summary>
        public bool Started { get; }
        /// <summary>Pid of the player process, or 0 if unknown.</summary>
        public int Pid { get; }
        /// <summary>Human readable reason when Started is false, else null.</summary>
        public string Error { get; }
    }

    /// <summary>Starts Roblox clients. Methods block up to the timeout and never throw for launch failures.</summary>
    public interface IRobloxLauncher
    {
        /// <summary>Starts the player with raw launch arguments. Waits up to <paramref name="timeoutMs"/> for the player pid.</summary>
        LaunchResult Launch(IReadOnlyList<string> arguments, int timeoutMs);

        /// <summary>Starts a client from a roblox:// (or roblox-player:) URL, waiting up to <paramref name="timeoutMs"/> for the pid.</summary>
        LaunchResult LaunchUrl(string robloxUrl, int timeoutMs);

        /// <summary>
        /// Makes the OS allow another client while others run (Windows: closes the singleton mutex/event handles;
        /// macOS: whatever the proof of concept shows). Needs MultiInstance; returns false when unsupported.
        /// </summary>
        bool PrepareForAnotherInstance();

        /// <summary>Applies the single-instance workaround to one running client. Returns the number of locks released.</summary>
        int ReleaseSingleInstanceLock(int pid);

        /// <summary>Number of single-instance locks still held by pid (0 when none or unsupported).</summary>
        int CountSingleInstanceLocks(int pid);
    }
}
