using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace CookieJar.Platform.Windows
{
    /// <summary>
    /// Finds the Roblox player on Windows. Ported from src/RobloxLocator.cs (search order and 10 s cache) and
    /// src/CacheCleaner.cs (cache folders, login cookie file and login registry values). Never throws.
    /// </summary>
    public sealed class WinRobloxLocator : IRobloxLocator
    {
        private const string PlayerExeName = "RobloxPlayerBeta.exe";
        private const int CacheSeconds = 10;
        private const string ProtocolCommandSubKey = "Software\\Classes\\roblox-player\\shell\\open\\command";
        private const string EnvironmentsSubKey = "Software\\ROBLOX Corporation\\Environments\\roblox-player";
        private const string LoginFileName = "RobloxCookies.dat";

        // Predefined root keys, sign-extended as Windows defines them (HKEY_CURRENT_USER = 0x80000001).
        private static readonly IntPtr HkeyCurrentUser = new IntPtr(unchecked((int)0x80000001));
        private static readonly IntPtr HkeyLocalMachine = new IntPtr(unchecked((int)0x80000002));
        private const uint KeyRead = 0x20019;
        // KEY_READ plus KEY_SET_VALUE. Reading is needed too: RegEnumValueW fails without KEY_QUERY_VALUE.
        private const uint KeyReadWrite = 0x20019 | 0x0002;
        private const uint KeyWow64_64 = 0x0100;
        private const uint KeyWow64_32 = 0x0200;
        private const uint RegSz = 1;
        private const uint RegExpandSz = 2;
        private const int ErrorMoreData = 234;
        private const int MaxValueNameChars = 16384;

        // Folders under %LOCALAPPDATA%\Roblox that are safe to delete (CacheCleaner.FoldersToDelete).
        private static readonly string[] CacheFolders = new string[] { "logs", "cache", "HttpCache", "http-cache", "Downloads" };

        // Keys (relative to HKCU) that may hold login state (CacheCleaner.RegistryKeys).
        private static readonly string[] LoginRegistryKeys = new string[]
        {
            "Software\\Roblox Corporation\\Environments\\roblox-player",
            "Software\\ROBLOX Corporation\\Roblox"
        };

        // Substrings (case-insensitive) identifying login-related registry value names (CacheCleaner.RegistryValueMarkers).
        private static readonly string[] LoginValueMarkers = new string[] { "Cookie", "Auth", "Token", "Login" };

        private readonly object _cacheLock = new object();
        private string _cachedExe;
        private DateTime _cachedAtUtc = DateTime.MinValue;
        private bool _cacheHasValue;

        public string PlayerProcessName
        {
            get { return "RobloxPlayerBeta"; }
        }

        public string FindPlayerPath()
        {
            try
            {
                lock (_cacheLock)
                {
                    if (_cacheHasValue && DateTime.UtcNow - _cachedAtUtc < TimeSpan.FromSeconds(CacheSeconds))
                    {
                        return _cachedExe;
                    }
                }

                string result;
                try
                {
                    result = Resolve();
                }
                catch (Exception)
                {
                    result = null;
                }

                lock (_cacheLock)
                {
                    _cachedExe = result;
                    _cachedAtUtc = DateTime.UtcNow;
                    _cacheHasValue = true;
                }
                return result;
            }
            catch (Exception)
            {
                return null;
            }
        }

        public string FindInstallRoot()
        {
            try
            {
                string exe = FindPlayerPath();
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

        public bool IsInstalled()
        {
            try
            {
                return FindPlayerPath() != null;
            }
            catch (Exception)
            {
                return false;
            }
        }

        public IReadOnlyList<string> GetCacheDirectories()
        {
            List<string> dirs = new List<string>();
            try
            {
                string robloxPath = GetRobloxPath();
                if (robloxPath != null && Directory.Exists(robloxPath))
                {
                    foreach (string folder in CacheFolders)
                    {
                        string path = Path.Combine(robloxPath, folder);
                        if (Directory.Exists(path))
                        {
                            dirs.Add(path);
                        }
                    }
                }

                string tempPath = Path.Combine(Path.GetTempPath(), "Roblox");
                if (Directory.Exists(tempPath))
                {
                    dirs.Add(tempPath);
                }
            }
            catch (Exception)
            {
                // Return what was gathered
            }
            return dirs;
        }

        /// <summary>
        /// Deletes the login cookie file(s) and login-related registry values (CacheCleaner.ClearLoginState).
        /// Callers must make sure no Roblox client is running; this method does not check.
        /// </summary>
        public IReadOnlyList<string> ClearLoginState()
        {
            List<string> deleted = new List<string>();
            try
            {
                foreach (string filePath in new string[] { LoginFilePath(), LegacyLoginFilePath() })
                {
                    if (filePath == null || !File.Exists(filePath)) continue;
                    try
                    {
                        File.Delete(filePath);
                        deleted.Add(filePath);
                    }
                    catch
                    {
                        // Tolerant of locked files
                    }
                }
            }
            catch
            {
                // Never throw
            }

            try
            {
                ClearLoginRegistry(deleted);
            }
            catch
            {
                // Never throw
            }
            return deleted;
        }

        /// <summary>True if the Roblox login cookie file (RobloxCookies.dat) exists. Same check as CacheCleaner.IsLoginStatePresent.</summary>
        public bool HasLoginState()
        {
            try
            {
                string primary = LoginFilePath();
                string legacy = LegacyLoginFilePath();
                return (primary != null && File.Exists(primary)) || (legacy != null && File.Exists(legacy));
            }
            catch
            {
                return false;
            }
        }

        public void InvalidateCache()
        {
            try
            {
                lock (_cacheLock)
                {
                    _cachedExe = null;
                    _cachedAtUtc = DateTime.MinValue;
                    _cacheHasValue = false;
                }
            }
            catch
            {
                // Ignore
            }
        }

        // ---- Paths ---------------------------------------------------------------------------

        /// <summary>%LOCALAPPDATA%\Roblox, or null.</summary>
        private static string GetRobloxPath()
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
            catch
            {
                return null;
            }
        }

        /// <summary>%LOCALAPPDATA%\Roblox\LocalStorage\RobloxCookies.dat</summary>
        private static string LoginFilePath()
        {
            string root = GetRobloxPath();
            return root == null ? null : Path.Combine(root, "LocalStorage", LoginFileName);
        }

        /// <summary>Older location, checked as a fallback: %LOCALAPPDATA%\Roblox\RobloxCookies.dat</summary>
        private static string LegacyLoginFilePath()
        {
            string root = GetRobloxPath();
            return root == null ? null : Path.Combine(root, LoginFileName);
        }

        // ---- Player search (RobloxLocator.Resolve) -------------------------------------------

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

        /// <summary>Newest RobloxPlayerBeta.exe (by last write time) inside the version folders of versionsPath.</summary>
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
                foreach (DirectoryInfo versionDir in versionsDir.GetDirectories())
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
                return null;
            }
        }

        private static string SearchRegistry()
        {
            uint[] views = new uint[] { KeyWow64_64, KeyWow64_32 };
            IntPtr[] hives = new IntPtr[] { HkeyCurrentUser, HkeyLocalMachine };

            // Protocol handler command lines (default value holds the launcher path)
            foreach (IntPtr hive in hives)
            {
                foreach (uint view in views)
                {
                    string command = ReadDefaultValue(hive, view, ProtocolCommandSubKey);
                    string exe = ExeFromPath(ParseCommandPath(command));
                    if (exe != null)
                    {
                        return exe;
                    }
                }
            }

            // ROBLOX Corporation environment entries (string values may hold install paths)
            foreach (uint view in views)
            {
                string exe = ExeFromStringValues(HkeyCurrentUser, view, EnvironmentsSubKey);
                if (exe != null)
                {
                    return exe;
                }
            }
            return null;
        }

        private static string ReadDefaultValue(IntPtr hive, uint view, string subKey)
        {
            try
            {
                IntPtr key = OpenKey(hive, subKey, view, false);
                if (key == IntPtr.Zero)
                {
                    return null;
                }

                try
                {
                    return ReadStringValue(key, string.Empty);
                }
                finally
                {
                    Kernel32.RegCloseKey(key);
                }
            }
            catch (Exception)
            {
                return null;
            }
        }

        private static string ExeFromStringValues(IntPtr hive, uint view, string subKey)
        {
            try
            {
                IntPtr key = OpenKey(hive, subKey, view, false);
                if (key == IntPtr.Zero)
                {
                    return null;
                }

                try
                {
                    foreach (string name in EnumValueNames(key))
                    {
                        string value = ReadStringValue(key, name);
                        string exe = ExeFromPath(ParseCommandPath(value));
                        if (exe != null)
                        {
                            return exe;
                        }
                    }
                }
                finally
                {
                    Kernel32.RegCloseKey(key);
                }
            }
            catch (Exception)
            {
                // Tolerate registry errors
            }
            return null;
        }

        /// <summary>Extracts the executable path from a command line such as "C:\...\RobloxPlayerLauncher.exe" "%1".</summary>
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

        /// <summary>Maps a path (exe, folder holding the exe, or Versions folder) to RobloxPlayerBeta.exe.</summary>
        private static string ExeFromPath(string path)
        {
            try
            {
                if (string.IsNullOrEmpty(path))
                {
                    return null;
                }

                if (File.Exists(path) && string.Equals(Path.GetFileName(path), PlayerExeName, StringComparison.OrdinalIgnoreCase))
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

        // ---- Login registry values (CacheCleaner.ClearRegistry) -----------------------------

        private static void ClearLoginRegistry(List<string> deleted)
        {
            foreach (string keyPath in LoginRegistryKeys)
            {
                try
                {
                    deleted.AddRange(ClearLoginValuesAt(keyPath));
                }
                catch
                {
                    // Tolerant of access errors on this key; continue with next
                }
            }
        }

        /// <summary>
        /// Deletes the login-marked values under HKCU\keyPath and returns "HKCU\keyPath\value" for each one removed.
        /// Internal so the tests can run it on a throw-away key.
        /// </summary>
        internal static List<string> ClearLoginValuesAt(string keyPath)
        {
            List<string> deleted = new List<string>();
            IntPtr key = OpenKey(HkeyCurrentUser, keyPath, 0, true);
            if (key == IntPtr.Zero)
            {
                return deleted;
            }

            try
            {
                // Snapshot names first so we do not modify while enumerating.
                foreach (string valueName in EnumValueNames(key))
                {
                    if (!IsLoginValueName(valueName))
                    {
                        continue;
                    }

                    try
                    {
                        if (Kernel32.RegDeleteValueW(key, valueName) == 0)
                        {
                            deleted.Add("HKCU\\" + keyPath + "\\" + valueName);
                        }
                    }
                    catch
                    {
                        // Tolerant of permission or access errors
                    }
                }
            }
            finally
            {
                Kernel32.RegCloseKey(key);
            }
            return deleted;
        }

        // ---- Registry access through advapi32 (same keys and value rules as CacheCleaner) ----

        /// <summary>Opens subKey under root. viewFlag is KEY_WOW64_64KEY/32KEY or 0. Returns IntPtr.Zero on failure.</summary>
        private static IntPtr OpenKey(IntPtr root, string subKey, uint viewFlag, bool write)
        {
            IntPtr key;
            uint access = (write ? KeyReadWrite : KeyRead) | viewFlag;
            return Kernel32.RegOpenKeyExW(root, subKey, 0, access, out key) == 0 ? key : IntPtr.Zero;
        }

        /// <summary>Reads a REG_SZ or REG_EXPAND_SZ value ("" = default value). Returns null for other types or failures.</summary>
        private static string ReadStringValue(IntPtr key, string name)
        {
            uint type;
            uint size = 0;
            int rc = Kernel32.RegQueryValueExW(key, name, IntPtr.Zero, out type, null, ref size);
            if (rc != 0 && rc != ErrorMoreData)
            {
                return null;
            }
            if (size == 0)
            {
                return string.Empty;
            }

            byte[] data = new byte[size];
            rc = Kernel32.RegQueryValueExW(key, name, IntPtr.Zero, out type, data, ref size);
            if (rc != 0 || (type != RegSz && type != RegExpandSz))
            {
                return null;
            }

            string value = Encoding.Unicode.GetString(data, 0, (int)Math.Min(size, (uint)data.Length)).TrimEnd('\0');
            return type == RegExpandSz ? Environment.ExpandEnvironmentVariables(value) : value;
        }

        private static List<string> EnumValueNames(IntPtr key)
        {
            List<string> names = new List<string>();
            for (uint i = 0; ; i++)
            {
                StringBuilder name = new StringBuilder(MaxValueNameChars);
                uint length = (uint)name.Capacity;
                if (Kernel32.RegEnumValueW(key, i, name, ref length, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero) != 0)
                {
                    break;
                }
                names.Add(name.ToString(0, (int)length));
            }
            return names;
        }

        private static bool IsLoginValueName(string valueName)
        {
            if (string.IsNullOrEmpty(valueName))
            {
                return false;
            }

            foreach (string marker in LoginValueMarkers)
            {
                if (valueName.IndexOf(marker, StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    return true;
                }
            }
            return false;
        }
    }
}
