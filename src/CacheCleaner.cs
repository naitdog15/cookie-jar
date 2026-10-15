using System;
using System.Collections.Generic;
using System.IO;
using Microsoft.Win32;

namespace RbxMulti
{
    /// <summary>
    /// Roblox cache and login-state cleanup.
    /// ClearCache runs on every launch. ClearLoginState removes the cookie file and
    /// login registry values; InstanceManager calls it only for a fresh (no profile)
    /// launch while no other instance is running. Settings, installed versions,
    /// plugins and Studio data are never touched.
    /// </summary>
    public static class CacheCleaner
    {
        // Folders under %LOCALAPPDATA%\Roblox that are safe to delete (whitelist).
        private static readonly string[] FoldersToDelete = new string[]
        {
            "logs",
            "cache",
            "HttpCache",
            "http-cache",
            "Downloads"
        };

        /// <summary>File under %LOCALAPPDATA%\Roblox that holds login state.</summary>
        internal const string LoginFileName = "RobloxCookies.dat";

        /// <summary>Registry keys (relative to HKCU) that may hold login state.</summary>
        internal static readonly string[] RegistryKeys = new string[]
        {
            @"Software\Roblox Corporation\Environments\roblox-player",
            @"Software\ROBLOX Corporation\Roblox"
        };

        // Substrings (case-insensitive) identifying login-related registry value names.
        private static readonly string[] RegistryValueMarkers = new string[]
        {
            "Cookie",
            "Auth",
            "Token",
            "Login"
        };

        /// <summary>Clears cache, login files and login registry values. Never throws.</summary>
        public static string[] Clear()
        {
            return Clear(true);
        }

        /// <summary>
        /// Clears cache and login state. Never throws. Returns deleted paths and
        /// registry values (formatted as HKCU\...\valuename).
        /// </summary>
        /// <param name="includeRegistry">When true, login-related registry values are also removed.</param>
        public static string[] Clear(bool includeRegistry)
        {
            List<string> deleted = new List<string>();

            try { ClearCacheFolders(deleted); } catch { }
            try { ClearLoginFile(deleted); } catch { }
            if (includeRegistry)
            {
                try { ClearRegistry(deleted); } catch { }
            }

            return deleted.ToArray();
        }

        /// <summary>Deletes only the cache folders and Roblox temp files. Never throws.</summary>
        public static string[] ClearCache()
        {
            List<string> deleted = new List<string>();
            try { ClearCacheFolders(deleted); } catch { }
            return deleted.ToArray();
        }

        /// <summary>
        /// Deletes only login state: the cookie file and login-related registry values.
        /// Callers must make sure no Roblox instance is running. Never throws.
        /// </summary>
        public static string[] ClearLoginState()
        {
            List<string> deleted = new List<string>();
            try { ClearLoginFile(deleted); } catch { }
            try { ClearRegistry(deleted); } catch { }
            return deleted.ToArray();
        }

        /// <summary>
        /// Returns true if the Roblox login cookie file (RobloxCookies.dat) exists.
        /// Never throws.
        /// </summary>
        public static bool IsLoginStatePresent()
        {
            try
            {
                return File.Exists(LoginFilePath()) || File.Exists(LegacyLoginFilePath());
            }
            catch
            {
                return false;
            }
        }

        /// <summary>Full path of %LOCALAPPDATA%\Roblox.</summary>
        internal static string GetRobloxPath()
        {
            return Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "Roblox");
        }

        /// <summary>Full path of the login cookie file.</summary>
        internal static string LoginFilePath()
        {
            return Path.Combine(GetRobloxPath(), "LocalStorage", LoginFileName);
        }

        /// <summary>Older location, checked as a fallback.</summary>
        internal static string LegacyLoginFilePath()
        {
            return Path.Combine(GetRobloxPath(), LoginFileName);
        }

        /// <summary>True when a registry value name looks like login state.</summary>
        internal static bool IsLoginValueName(string valueName)
        {
            if (string.IsNullOrEmpty(valueName))
            {
                return false;
            }

            foreach (string marker in RegistryValueMarkers)
            {
                if (valueName.IndexOf(marker, StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    return true;
                }
            }

            return false;
        }

        private static void ClearCacheFolders(List<string> deleted)
        {
            string robloxPath = GetRobloxPath();

            if (Directory.Exists(robloxPath))
            {
                // Never touch: Versions, Plugins, Studio, rbx-storage, ClientSettings,
                // GlobalSettings*.xml, GlobalBasicSettings*.xml.
                foreach (string folder in FoldersToDelete)
                {
                    string folderPath = Path.Combine(robloxPath, folder);
                    if (!Directory.Exists(folderPath))
                    {
                        continue;
                    }

                    try
                    {
                        DeleteDirectory(folderPath);
                        deleted.Add(folderPath);
                    }
                    catch
                    {
                        // Tolerant of locked files; continue with next folder
                    }
                }
            }

            // Temporary Roblox files.
            try
            {
                string tempPath = Path.Combine(Path.GetTempPath(), "Roblox");
                if (Directory.Exists(tempPath))
                {
                    DeleteDirectory(tempPath);
                    deleted.Add(tempPath);
                }
            }
            catch
            {
                // Tolerant of locked files
            }
        }

        private static void ClearLoginFile(List<string> deleted)
        {
            foreach (string filePath in new string[] { LoginFilePath(), LegacyLoginFilePath() })
            {
                if (!File.Exists(filePath)) continue;
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

        private static void ClearRegistry(List<string> deleted)
        {
            foreach (string keyPath in RegistryKeys)
            {
                try
                {
                    using (RegistryKey key = Registry.CurrentUser.OpenSubKey(keyPath, true))
                    {
                        if (key == null)
                        {
                            continue;
                        }

                        // Snapshot names first so we do not modify while enumerating.
                        string[] valueNames = key.GetValueNames();
                        foreach (string valueName in valueNames)
                        {
                            if (!IsLoginValueName(valueName))
                            {
                                continue;
                            }

                            try
                            {
                                key.DeleteValue(valueName, false);
                                deleted.Add("HKCU\\" + keyPath + "\\" + valueName);
                            }
                            catch
                            {
                                // Tolerant of permission or access errors
                            }
                        }
                    }
                }
                catch
                {
                    // Tolerant of access errors on this key; continue with next
                }
            }
        }

        private static void DeleteDirectory(string path)
        {
            if (!Directory.Exists(path))
            {
                return;
            }

            // Delete all files first
            try
            {
                string[] files = Directory.GetFiles(path);
                foreach (string file in files)
                {
                    try
                    {
                        File.Delete(file);
                    }
                    catch
                    {
                        // Tolerant of locked files
                    }
                }
            }
            catch
            {
                // Ignore errors enumerating files
            }

            // Recursively delete subdirectories
            try
            {
                string[] directories = Directory.GetDirectories(path);
                foreach (string dir in directories)
                {
                    DeleteDirectory(dir);
                }
            }
            catch
            {
                // Ignore errors enumerating subdirectories
            }

            // Delete empty directory
            try
            {
                Directory.Delete(path);
            }
            catch
            {
                // Tolerant of locked files
            }
        }
    }
}

