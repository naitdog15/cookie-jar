using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using Microsoft.Win32;

namespace RbxMulti
{
    /// <summary>One RobloxPlayerBeta.exe found by the locator.</summary>
    public sealed class PlayerCandidate
    {
        public string ExePath;      // full path (Path.GetFullPath)
        public string Source;       // "HKCU", "HKLM" or "scan"
        public Version Version;     // null = unknown
        public DateTime WriteUtc;   // DateTime.MinValue when unreadable
        internal int SourceRank { get { return Source == "HKCU" ? 0 : (Source == "HKLM" ? 1 : 2); } }
        public string VersionText { get { return Version == null ? "unknown" : Version.ToString(); } }
    }

    /// <summary>
    /// Locates the Roblox player executable (RobloxPlayerBeta.exe) on this machine.
    /// Chooses the highest FileVersion among registered and scanned players; ties prefer registered over scanned, HKCU over HKLM, then the newer file.
    /// Results are cached briefly. Never throws; failures return null.
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
        private static string[] lastLog = new string[0];
        private static List<PlayerCandidate> lastCandidates = new List<PlayerCandidate>();

        public static string[] GetLastResolutionLog() { lock (CacheLock) { return (string[])lastLog.Clone(); } }
        public static List<PlayerCandidate> GetLastCandidates() { lock (CacheLock) { return new List<PlayerCandidate>(lastCandidates); } }

        /// <summary>Forces a re-scan and returns the fresh candidate list (used by the repair audit).</summary>
        public static List<PlayerCandidate> GetCandidatesFresh() { InvalidateCache(); FindPlayerExe(); return GetLastCandidates(); }

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
            List<string> log = new List<string>();
            List<PlayerCandidate> candidates;
            string chosen = ResolveCore(ReadRegisteredCommands(RegistryHive.CurrentUser), ReadRegisteredPaths(RegistryHive.CurrentUser),
                ReadRegisteredCommands(RegistryHive.LocalMachine), ReadRegisteredPaths(RegistryHive.LocalMachine),
                GetVersionsRoots(), ReadFileVersion, log, out candidates);
            lock (CacheLock) { lastLog = log.ToArray(); lastCandidates = candidates; }
            return chosen;
        }

        internal static string ResolveCandidates(IEnumerable<string> registeredCommands,
            IEnumerable<string> registeredPaths, IEnumerable<string> versionsRoots)
        {
            return ResolveCandidates(registeredCommands, registeredPaths, null, null, versionsRoots);
        }

        internal static string ResolveCandidates(IEnumerable<string> userCommands, IEnumerable<string> userPaths,
            IEnumerable<string> machineCommands, IEnumerable<string> machinePaths, IEnumerable<string> versionsRoots)
        {
            return ResolveCandidates(userCommands, userPaths, machineCommands, machinePaths, versionsRoots, ReadFileVersion, null);
        }

        internal static string ResolveCandidates(IEnumerable<string> userCommands, IEnumerable<string> userPaths,
            IEnumerable<string> machineCommands, IEnumerable<string> machinePaths, IEnumerable<string> versionsRoots,
            Func<string, Version> versionOf, List<string> log)
        {
            List<PlayerCandidate> ignored;
            return ResolveCore(userCommands, userPaths, machineCommands, machinePaths, versionsRoots, versionOf, log, out ignored);
        }

        internal static string ResolveCore(IEnumerable<string> userCommands, IEnumerable<string> userPaths,
            IEnumerable<string> machineCommands, IEnumerable<string> machinePaths, IEnumerable<string> versionsRoots,
            Func<string, Version> versionOf, List<string> log, out List<PlayerCandidate> candidates)
        {
            candidates = new List<PlayerCandidate>();
            Dictionary<string, PlayerCandidate> seen = new Dictionary<string, PlayerCandidate>(StringComparer.OrdinalIgnoreCase);
            AddRegistered(candidates, seen, userCommands, "HKCU", versionOf);
            AddRegistered(candidates, seen, userPaths, "HKCU", versionOf);
            AddRegistered(candidates, seen, machineCommands, "HKLM", versionOf);
            AddRegistered(candidates, seen, machinePaths, "HKLM", versionOf);
            if (versionsRoots != null)
                foreach (string root in versionsRoots)
                    foreach (string exe in ListPlayers(root)) AddCandidate(candidates, seen, exe, "scan", versionOf);
            PlayerCandidate best = null;
            foreach (PlayerCandidate c in candidates) if (best == null || CompareCandidates(c, best) > 0) best = c;
            if (log != null)
            {
                foreach (PlayerCandidate c in candidates) log.Add("candidate " + c.ExePath + " version=" + c.VersionText + " source=" + c.Source);
                if (best != null) log.Add("chosen " + best.ExePath);
            }
            return best == null ? null : best.ExePath;
        }

        private static void AddRegistered(List<PlayerCandidate> list, Dictionary<string, PlayerCandidate> seen,
            IEnumerable<string> entries, string source, Func<string, Version> versionOf)
        {
            if (entries == null) return;
            foreach (string e in entries)
            {
                string exe = ExeFromPath(ParseCommandPath(e));
                if (exe != null) AddCandidate(list, seen, exe, source, versionOf);
            }
        }

        private static void AddCandidate(List<PlayerCandidate> list, Dictionary<string, PlayerCandidate> seen,
            string exe, string source, Func<string, Version> versionOf)
        {
            string full;
            try { full = Path.GetFullPath(exe); } catch (Exception) { return; }
            if (seen.ContainsKey(full)) return; // the first source seen wins; input order is HKCU, HKLM, scan
            PlayerCandidate c = new PlayerCandidate();
            c.ExePath = full;
            c.Source = source;
            try { c.Version = versionOf == null ? null : versionOf(full); } catch (Exception) { c.Version = null; }
            try { c.WriteUtc = File.GetLastWriteTimeUtc(full); } catch (Exception) { c.WriteUtc = DateTime.MinValue; }
            seen[full] = c;
            list.Add(c);
        }

        /// <summary>Greater than 0 when a is better than b.</summary>
        internal static int CompareCandidates(PlayerCandidate a, PlayerCandidate b)
        {
            if (a.Version != null || b.Version != null)
            {
                if (a.Version == null) return -1;
                if (b.Version == null) return 1;
                int v = a.Version.CompareTo(b.Version);
                if (v != 0) return v;
            }
            if (a.SourceRank != b.SourceRank) return b.SourceRank - a.SourceRank; // registered beats scan, then HKCU beats HKLM
            return a.WriteUtc.CompareTo(b.WriteUtc);
        }

        internal static Version ReadFileVersion(string path)
        {
            try
            {
                FileVersionInfo info = FileVersionInfo.GetVersionInfo(path);
                Version parsed = ParseVersionText(info.FileVersion);
                if (parsed != null) return parsed;
                if (info.FileMajorPart == 0 && info.FileMinorPart == 0 && info.FileBuildPart == 0 && info.FilePrivatePart == 0) return null;
                return new Version(info.FileMajorPart, info.FileMinorPart, info.FileBuildPart, info.FilePrivatePart);
            }
            catch (Exception) { return null; }
        }

        /// <summary>Accepts "0, 747, 0, 7471481", "0.747.0.7471481" and "4.8.9037.0 built by: X". Null for empty, unparsable or all-zero text.</summary>
        internal static Version ParseVersionText(string text)
        {
            if (string.IsNullOrEmpty(text)) return null;
            int cut = 0;
            while (cut < text.Length && "0123456789., ".IndexOf(text[cut]) >= 0) cut++;
            string[] parts = text.Substring(0, cut).Split(new char[] { ',', '.' }, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length < 2 || parts.Length > 4) return null;
            int[] n = new int[4];
            for (int i = 0; i < parts.Length; i++)
                if (!int.TryParse(parts[i].Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out n[i])) return null;
            if (n[0] == 0 && n[1] == 0 && n[2] == 0 && n[3] == 0) return null;
            return new Version(n[0], n[1], n[2], n[3]);
        }

        private static string NewestByWriteTime(List<string> players)
        {
            string newest = null;
            foreach (string p in players) newest = NewerPlayer(p, newest);
            return newest;
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

        /// <summary>Every version-*\RobloxPlayerBeta.exe under a Versions folder. Never throws.</summary>
        internal static List<string> ListPlayers(string versionsPath)
        {
            List<string> players = new List<string>();
            try
            {
                if (string.IsNullOrEmpty(versionsPath) || !Directory.Exists(versionsPath))
                {
                    return players;
                }

                foreach (DirectoryInfo versionDir in new DirectoryInfo(versionsPath).GetDirectories())
                {
                    string playerExe = Path.Combine(versionDir.FullName, PlayerExeName);
                    if (File.Exists(playerExe))
                    {
                        players.Add(playerExe);
                    }
                }
            }
            catch (Exception)
            {
                // Tolerate access errors silently
            }
            return players;
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
        internal static string ParseCommandPath(string command)
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

                    return NewestByWriteTime(ListPlayers(path));
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
