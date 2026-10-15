namespace CookieJar.Platform
{
    /// <summary>Start at login. Without StartAtLogin: IsEnabled false, SetEnabled false.</summary>
    public interface IStartupService
    {
        /// <summary>True if the app is registered to start at login.</summary>
        bool IsEnabled();
        /// <summary>Registers or removes login start. Returns true on success.</summary>
        bool SetEnabled(bool enabled);
    }

    /// <summary>Per-user locations. Directories are created on demand by the getter and are absolute.</summary>
    public interface IAppPaths
    {
        /// <summary>Profiles, accounts and settings.</summary>
        string DataDirectory { get; }
        /// <summary>Log files.</summary>
        string LogDirectory { get; }
        /// <summary>Diagnostic bundles and traces.</summary>
        string DiagnosticsDirectory { get; }
    }
}

namespace CookieJar.Platform
{
    /// <summary>Shell integration the Windows app does with mutexes and explorer.exe. Never throws.</summary>
    public interface IShellService
    {
        /// <summary>
        /// Claims the per-user "only one Cookie Jar" lock under <paramref name="name"/>. Returns false when another copy holds it.
        /// The lock is held until the process exits or <see cref="ReleaseAppInstanceLock"/>.
        /// </summary>
        bool TryAcquireAppInstanceLock(string name);
        /// <summary>Releases a lock taken by TryAcquireAppInstanceLock.</summary>
        void ReleaseAppInstanceLock(string name);
        /// <summary>Shows a folder in the file manager (Explorer / Finder). False on failure.</summary>
        bool OpenFolder(string path);
    }
}
