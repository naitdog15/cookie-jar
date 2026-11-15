using System;
using System.Collections.Generic;
using System.IO;
using Microsoft.Win32;

namespace RbxMulti
{
    /// <summary>
    /// Locates the Roblox player executable (RobloxPlayerBeta.exe) on this machine.
    /// Prefers the registered player/launcher installation and Roblox environment paths.
    /// If none resolves, compares players across all standard Versions folders. Results are cached briefly.
    /// Never throws; failures return null.
    /// </summary>
    public static class RobloxLocator
    {
        private const string PlayerExeName = "RobloxPlayerBeta.exe";
        private const int CacheSeconds = 10;
        private const string ProtocolCommandSubKey = "Software\\Classes\\roblox-player\\shell\\open\\command";
        private const string EnvironmentsSubKey = "Software\\ROBLOX Corporation\\Environments\\roblox-player";

        private static readonly object CacheLock = new object();
        private static string cachedExe;
        private static DateTime cachedAtUtc = DateTime.MinValue;
        private static bool cacheHasValue;

        /// <summary>
        /// Returns the registered RobloxPlayerBeta.exe, or the newest standard-install fallback.
        /// Returns null if none is found.
        /// </summary>
        public static string FindPlayerExe()
        {
            try
            {
                lock (CacheLock)
                {
                    if (cacheHasValue && DateTime.UtcNow - cachedAtUtc < TimeSpan.FromSeconds(CacheSeconds))
                    {
                        return cachedExe;
                    }
                }

                string result = null;
                try
                {
                    result = Resolve();
                }
                catch (Exception)
                {
                    result = null;
                }

                lock (CacheLock)
                {
                    cachedExe = result;
                    cachedAtUtc = DateTime.UtcNow;
                    cacheHasValue = true;
                }

                return result;
            }
            catch (Exception)
            {
                return null;
            }
        }

        /// <summary>
        /// Returns the Versions folder that contains the chosen player exe, or null.
        /// </summary>
        public static string FindVersionsFolder()
        {
            try
            {
                string exe = FindPlayerExe();
                if (exe == null)
                {
                    return null;
                }

                string versionFolder = Path.GetDirectoryName(exe);
                if (versionFolder == null)
                {
                    return null;
                }

                string versionsFolder = Path.GetDirectoryName(versionFolder);
                if (versionsFolder != null &&
                    string.Equals(Path.GetFileName(versionsFolder), "Versions", StringComparison.OrdinalIgnoreCase))
                {
                    return versionsFolder;
                }

                return null;
            }
            catch (Exception)
            {
                return null;
            }
        }

        /// <summary>
        /// True when a Roblox player executable can be found.
        /// </summary>
        public static bool IsRobloxInstalled()
        {
            try
            {
                return FindPlayerExe() != null;
            }
            catch (Exception)
            {
                return false;
            }
        }

        /// <summary>
        /// Returns %LOCALAPPDATA%\Roblox, or null if it cannot be determined.
        /// </summary>
        public static string GetLocalRobloxRoot()
        {
            try
            {
                string localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
                if (string.IsNullOrEmpty(localAppData))
                {
                    return null;
                }

                return Path.Combine(localAppData, "Roblox");
            }
            catch (Exception)
            {
                return null;
            }
        }

        /// <summary>
        /// Clears the cached lookup so the next call re-scans the machine.
        /// </summary>
        public static void InvalidateCache()
        {
            try
            {
                lock (CacheLock)
                {
                    cachedExe = null;
                    cachedAtUtc = DateTime.MinValue;
                    cacheHasValue = false;
                }
            }
            catch (Exception)
            {
                // Ignore
            }
        }

        private static string Resolve()
        {
            return ResolveCandidates(
                ReadRegisteredCommands(RegistryHive.CurrentUser), ReadRegisteredPaths(RegistryHive.CurrentUser),
                ReadRegisteredCommands(RegistryHive.LocalMachine), ReadRegisteredPaths(RegistryHive.LocalMachine),
                GetVersionsRoots());
        }

        /// <summary>
        /// Uses the same selection policy for real registry inputs and isolated filesystem fixtures.
        /// Commands and environment paths arrive in registry precedence order. Never executes them.
        /// </summary>
        internal static string ResolveCandidates(IEnumerable<string> registeredCommands,
            IEnumerable<string> registeredPaths, IEnumerable<string> versionsRoots)
        {
            return ResolveCandidates(registeredCommands, registeredPaths, null, null, versionsRoots);
        }

        /// <summary>
        /// A per-user handler can be a wrapper whose player lives in its environment path.
        /// Finish resolving that user installation before considering any machine registration.
        /// </summary>
        internal static string ResolveCandidates(IEnumerable<string> userCommands, IEnumerable<string> userPaths,
            IEnumerable<string> machineCommands, IEnumerable<string> machinePaths, IEnumerable<string> versionsRoots)
        {
            string registered = ResolveRegistration(userCommands, userPaths);
            if (registered != null) return registered;
            registered = ResolveRegistration(machineCommands, machinePaths);
            if (registered != null) return registered;

            string newest = null;
            if (versionsRoots != null)
            {
                foreach (string versionsPath in versionsRoots)
                    newest = NewerPlayer(SearchVersionsPath(versionsPath), newest);
            }
            return newest;
        }

        private static string ResolveRegistration(IEnumerable<string> registeredCommands, IEnumerable<string> registeredPaths)
        {
            if (registeredCommands != null)
            {
                foreach (string command in registeredCommands)
                {
                    string player = ExeFromPath(ParseCommandPath(command));
                    if (player != null) return player;
                }
            }

            if (registeredPaths != null)
            {
                foreach (string path in registeredPaths)
                {
                    string player = ExeFromPath(ParseCommandPath(path));
                    if (player != null) return player;
                }
            }

            return null;
        }

        private static string NewerPlayer(string candidate, string current)
        {
            try
            {
                if (candidate != null && (current == null
                    || File.GetLastWriteTimeUtc(candidate) > File.GetLastWriteTimeUtc(current)))
                    return candidate;
            }
            catch (Exception)
            {
                // One unreadable candidate must not hide the other installations.
            }
            return current;
        }

        private static List<string> GetVersionsRoots()
        {
            List<string> roots = new List<string>();
            try
            {
                AddVersionsRoot(roots, Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData));
                AddVersionsRoot(roots, Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles));
                AddVersionsRoot(roots, Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86));
            }
            catch (Exception)
            {
                // Tolerate environment lookup errors
            }

            return roots;
        }

        private static void AddVersionsRoot(List<string> roots, string baseFolder)
        {
            if (string.IsNullOrEmpty(baseFolder))
            {
                return;
            }

            string path = Path.Combine(baseFolder, "Roblox", "Versions");
            if (!roots.Contains(path))
            {
                roots.Add(path);
            }
        }

        private static string SearchVersionsPath(string versionsPath)
        {
            try
            {
                if (string.IsNullOrEmpty(versionsPath) || !Directory.Exists(versionsPath))
                {
                    return null;
                }

                string newest = null;

                DirectoryInfo versionsDir = new DirectoryInfo(versionsPath);
                DirectoryInfo[] versionDirs = versionsDir.GetDirectories();

                foreach (DirectoryInfo versionDir in versionDirs)
                {
                    string playerExe = Path.Combine(versionDir.FullName, PlayerExeName);

                    if (File.Exists(playerExe))
                    {
                        newest = NewerPlayer(playerExe, newest);
                    }
                }

                return newest;
            }
            catch (Exception)
            {
                // Tolerate access errors silently
                return null;
            }
        }

        private static List<string> ReadRegisteredCommands(RegistryHive hive)
        {
            List<string> commands = new List<string>();
            RegistryView[] views = new RegistryView[] { RegistryView.Registry64, RegistryView.Registry32 };

            // Protocol handler command lines (default value holds the launcher path)
            foreach (RegistryView view in views)
            {
                commands.Add(ReadDefaultValue(hive, view, ProtocolCommandSubKey));
            }

            return commands;
        }

        private static List<string> ReadRegisteredPaths(RegistryHive hive)
        {
            List<string> paths = new List<string>();
            RegistryView[] views = new RegistryView[] { RegistryView.Registry64, RegistryView.Registry32 };
            // Environment entries can identify an install even when a separate bootstrapper handles the protocol.
            foreach (RegistryView view in views)
            {
                ReadStringValues(hive, view, EnvironmentsSubKey, paths);
            }
            return paths;
        }

        private static RegistryKey OpenSubKey(RegistryHive hive, RegistryView view, string subKey)
        {
            try
            {
                using (RegistryKey baseKey = RegistryKey.OpenBaseKey(hive, view))
                {
                    return baseKey == null ? null : baseKey.OpenSubKey(subKey);
                }
            }
            catch (Exception)
            {
                return null;
            }
        }

        private static string ReadDefaultValue(RegistryHive hive, RegistryView view, string subKey)
        {
            try
            {
                using (RegistryKey key = OpenSubKey(hive, view, subKey))
                {
                    if (key == null)
                    {
                        return null;
                    }

                    return key.GetValue(null) as string;
                }
            }
            catch (Exception)
            {
                return null;
            }
        }

        private static void ReadStringValues(RegistryHive hive, RegistryView view, string subKey, List<string> values)
        {
            try
            {
                using (RegistryKey key = OpenSubKey(hive, view, subKey))
                {
                    if (key == null)
                    {
                        return;
                    }

                    foreach (string name in key.GetValueNames())
                    {
                        values.Add(key.GetValue(name) as string);
                    }
                }
            }
            catch (Exception)
            {
                // Tolerate registry errors
            }
        }

        /// <summary>
        /// Extracts the executable path from a shell command line such as
        /// "C:\...\RobloxPlayerLauncher.exe" "%1".
        /// </summary>
        private static string ParseCommandPath(string command)
        {
            if (string.IsNullOrEmpty(command))
            {
                return null;
            }

            string trimmed = command.Trim();
            if (trimmed.Length == 0)
            {
                return null;
            }

            if (trimmed[0] == '"')
            {
                int end = trimmed.IndexOf('"', 1);
                if (end > 1)
                {
                    return trimmed.Substring(1, end - 1);
                }

                return null;
            }

            int exeIndex = trimmed.IndexOf(".exe", StringComparison.OrdinalIgnoreCase);
            if (exeIndex >= 0)
            {
                return trimmed.Substring(0, exeIndex + 4);
            }

            return trimmed;
        }

        /// <summary>
        /// Maps a path (exe, folder containing the exe, or Versions folder) to RobloxPlayerBeta.exe.
        /// </summary>
        private static string ExeFromPath(string path)
        {
            try
            {
                if (string.IsNullOrEmpty(path))
                {
                    return null;
                }

                if (File.Exists(path) &&
                    string.Equals(Path.GetFileName(path), PlayerExeName, StringComparison.OrdinalIgnoreCase))
                {
                    return path;
                }

                if (File.Exists(path))
                {
                    string dir = Path.GetDirectoryName(path);
                    if (dir != null)
                    {
                        string sibling = Path.Combine(dir, PlayerExeName);
                        if (File.Exists(sibling))
                        {
                            return sibling;
                        }
                    }

                    return null;
                }

                if (Directory.Exists(path))
                {
                    string inFolder = Path.Combine(path, PlayerExeName);
                    if (File.Exists(inFolder))
                    {
                        return inFolder;
                    }

                    return SearchVersionsPath(path);
                }
            }
            catch (Exception)
            {
                // Tolerate invalid paths
            }

            return null;
        }
    }
}
