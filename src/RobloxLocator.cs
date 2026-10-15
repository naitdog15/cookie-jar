using System;
using System.Collections.Generic;
using System.IO;
using Microsoft.Win32;

namespace RbxMulti
{
    /// <summary>
    /// Locates the Roblox player executable (RobloxPlayerBeta.exe) on this machine.
    /// Search order: LOCALAPPDATA, Program Files, Program Files (x86) Versions folders,
    /// then the roblox-player protocol registry entries. Results are cached briefly.
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
        /// Returns the full path to the newest RobloxPlayerBeta.exe, or null if none is found.
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
            foreach (string versionsPath in GetVersionsRoots())
            {
                string found = SearchVersionsPath(versionsPath);
                if (found != null)
                {
                    return found;
                }
            }

            return SearchRegistry();
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
                DateTime newestTime = DateTime.MinValue;

                DirectoryInfo versionsDir = new DirectoryInfo(versionsPath);
                DirectoryInfo[] versionDirs = versionsDir.GetDirectories();

                foreach (DirectoryInfo versionDir in versionDirs)
                {
                    string playerExe = Path.Combine(versionDir.FullName, PlayerExeName);

                    if (File.Exists(playerExe))
                    {
                        FileInfo fi = new FileInfo(playerExe);
                        if (fi.LastWriteTimeUtc > newestTime)
                        {
                            newestTime = fi.LastWriteTimeUtc;
                            newest = playerExe;
                        }
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

        private static string SearchRegistry()
        {
            RegistryView[] views = new RegistryView[] { RegistryView.Registry64, RegistryView.Registry32 };
            RegistryHive[] hives = new RegistryHive[] { RegistryHive.CurrentUser, RegistryHive.LocalMachine };

            // Protocol handler command lines (default value holds the launcher path)
            foreach (RegistryHive hive in hives)
            {
                foreach (RegistryView view in views)
                {
                    string command = ReadDefaultValue(hive, view, ProtocolCommandSubKey);
                    string commandPath = ParseCommandPath(command);
                    string exe = ExeFromPath(commandPath);
                    if (exe != null)
                    {
                        return exe;
                    }
                }
            }

            // ROBLOX Corporation environment entries (string values may hold install paths)
            foreach (RegistryView view in views)
            {
                string exe = ExeFromStringValues(RegistryHive.CurrentUser, view, EnvironmentsSubKey);
                if (exe != null)
                {
                    return exe;
                }
            }

            return null;
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

        private static string ExeFromStringValues(RegistryHive hive, RegistryView view, string subKey)
        {
            try
            {
                using (RegistryKey key = OpenSubKey(hive, view, subKey))
                {
                    if (key == null)
                    {
                        return null;
                    }

                    foreach (string name in key.GetValueNames())
                    {
                        string value = key.GetValue(name) as string;
                        string exe = ExeFromPath(ParseCommandPath(value));
                        if (exe != null)
                        {
                            return exe;
                        }
                    }
                }
            }
            catch (Exception)
            {
                // Tolerate registry errors
            }

            return null;
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
