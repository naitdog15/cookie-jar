using System;
using System.Collections.Generic;
using System.IO;

namespace CookieJar.Platform.Mac
{
    /// <summary>
    /// Finds Roblox.app and its cache and login-state files on macOS. Filesystem access is injected so tests can
    /// use a fake layout. Results are memoised until InvalidateCache. Never throws.
    /// </summary>
    public sealed class MacRobloxLocator : IRobloxLocator
    {
        // MAC-UNVERIFIED: Roblox installs as /Applications/Roblox.app with the player at Contents/MacOS/RobloxPlayer.
        private const string SystemAppPath = "/Applications/Roblox.app";
        private const string PlayerRelative = "/Contents/MacOS/RobloxPlayer";
        private const string PlayerName = "RobloxPlayer";

        // MAC-UNVERIFIED: login state is kept in these per-user folders; the names are the ones the app is expected to use.
        private const string StoragePrefix = "com.roblox.RobloxPlayer";
        private const string CookieFileName = "com.roblox.RobloxPlayer.binarycookies";

        private readonly Func<string, bool> dirExists;
        private readonly Func<string, bool> fileExists;
        private readonly Func<string, IReadOnlyList<string>> listEntries;
        private readonly Func<string, bool> deletePath;
        private readonly string home;

        private readonly object cacheLock = new object();
        private bool rootCached;
        private string cachedRoot;
        private bool playerCached;
        private string cachedPlayer;

        /// <summary>The runner is reserved for command-based checks; the file checks here do not need it.</summary>
        public MacRobloxLocator(Func<string, bool> dirExists, Func<string, bool> fileExists, string homeDir, ICommandRunner runner)
            : this(dirExists, fileExists, homeDir, runner, null, null) { }

        /// <summary>Full constructor. Null listEntries or deletePath use the real file system.</summary>
        public MacRobloxLocator(Func<string, bool> dirExists, Func<string, bool> fileExists, string homeDir, ICommandRunner runner,
            Func<string, IReadOnlyList<string>> listEntries, Func<string, bool> deletePath)
        {
            this.dirExists = dirExists ?? (p => false);
            this.fileExists = fileExists ?? (p => false);
            this.home = (homeDir ?? "").TrimEnd('/');
            this.listEntries = listEntries ?? DefaultListEntries;
            this.deletePath = deletePath ?? DefaultDelete;
        }

        public string PlayerProcessName { get { return PlayerName; } }

        /// <summary>The app bundle: /Applications/Roblox.app, else ~/Applications/Roblox.app, else null.</summary>
        public string FindInstallRoot()
        {
            lock (cacheLock)
            {
                if (rootCached) return cachedRoot;
            }

            string root = null;
            try
            {
                if (dirExists(SystemAppPath))
                {
                    root = SystemAppPath;
                }
                else
                {
                    string userApp = home + "/Applications/Roblox.app";
                    if (dirExists(userApp)) root = userApp;
                }
            }
            catch (Exception)
            {
                root = null;
            }

            lock (cacheLock)
            {
                cachedRoot = root;
                rootCached = true;
            }
            return root;
        }

        /// <summary>&lt;root&gt;/Contents/MacOS/RobloxPlayer when that file exists, else null.</summary>
        public string FindPlayerPath()
        {
            lock (cacheLock)
            {
                if (playerCached) return cachedPlayer;
            }

            string player = null;
            try
            {
                string root = FindInstallRoot();
                if (root != null)
                {
                    string candidate = root + PlayerRelative;
                    if (fileExists(candidate)) player = candidate;
                }
            }
            catch (Exception)
            {
                player = null;
            }

            lock (cacheLock)
            {
                cachedPlayer = player;
                playerCached = true;
            }
            return player;
        }

        public bool IsInstalled()
        {
            return FindPlayerPath() != null;
        }

        /// <summary>Roblox cache, log and HTTP cache folders that exist. Empty if none.</summary>
        public IReadOnlyList<string> GetCacheDirectories()
        {
            var dirs = new List<string>();
            try
            {
                string[] candidates =
                {
                    home + "/Library/Roblox",
                    home + "/Library/Logs/Roblox",
                    home + "/Library/Caches/com.roblox.RobloxPlayer"
                };
                foreach (string candidate in candidates)
                {
                    if (dirExists(candidate)) dirs.Add(candidate);
                }
            }
            catch (Exception)
            {
                dirs.Clear();
            }
            return dirs;
        }

        /// <summary>Deletes the coarse login state (HTTPStorages entries and the binary cookie file). Returns what was removed.</summary>
        public IReadOnlyList<string> ClearLoginState()
        {
            var removed = new List<string>();
            try
            {
                foreach (string path in LoginStatePaths())
                {
                    try
                    {
                        if (deletePath(path)) removed.Add("Removed " + path);
                    }
                    catch (Exception)
                    {
                        // Keep going; one locked file must not stop the rest.
                    }
                }
            }
            catch (Exception)
            {
                // Never throw from a cleanup call.
            }
            return removed;
        }

        public bool HasLoginState()
        {
            try
            {
                return LoginStatePaths().Count > 0;
            }
            catch (Exception)
            {
                return false;
            }
        }

        public void InvalidateCache()
        {
            lock (cacheLock)
            {
                rootCached = false;
                cachedRoot = null;
                playerCached = false;
                cachedPlayer = null;
            }
        }

        // MAC-UNVERIFIED: login state is spread across these two locations; a full logout may also need the
        // defaults domain, which this coarse version does not touch.
        private List<string> LoginStatePaths()
        {
            var paths = new List<string>();
            try
            {
                foreach (string entry in listEntries(home + "/Library/HTTPStorages"))
                {
                    if (LeafName(entry).StartsWith(StoragePrefix, StringComparison.Ordinal)) paths.Add(entry);
                }
            }
            catch (Exception)
            {
                // Missing folder means no HTTP storage to clear.
            }

            string cookies = home + "/Library/Cookies/" + CookieFileName;
            if (fileExists(cookies)) paths.Add(cookies);
            return paths;
        }

        private static string LeafName(string path)
        {
            if (string.IsNullOrEmpty(path)) return "";
            int slash = path.LastIndexOf('/');
            return slash < 0 ? path : path.Substring(slash + 1);
        }

        private static IReadOnlyList<string> DefaultListEntries(string dir)
        {
            try
            {
                return Directory.GetFileSystemEntries(dir);
            }
            catch (Exception)
            {
                return new string[0];
            }
        }

        private static bool DefaultDelete(string path)
        {
            try
            {
                if (Directory.Exists(path))
                {
                    Directory.Delete(path, true);
                    return true;
                }
                if (File.Exists(path))
                {
                    File.Delete(path);
                    return true;
                }
            }
            catch (Exception)
            {
                // Reported as not removed.
            }
            return false;
        }
    }
}
