using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using CookieJar.Platform;
using CookieJar.Platform.Windows;

namespace CookieJar.Tests
{
    // No ClearLoginState on the real account, no Launch of a real client, no single-instance handle closing.
    // Registry checks run only on a throw-away key under HKCU\Software.
    public static class RobloxTests
    {
        private static readonly IntPtr HkeyCurrentUser = new IntPtr(unchecked((int)0x80000001));
        private const uint KeyQueryValue = 0x0001;
        private const uint KeyAllAccess = 0xF003F;
        private const uint RegSz = 1;
        public static void Run()
        {
            IRobloxLocator locator = new WinRobloxLocator();

            T.Check("PlayerProcessName is the Windows value", locator.PlayerProcessName == "RobloxPlayerBeta", locator.PlayerProcessName);

            string exe = locator.FindPlayerPath();
            T.Check("FindPlayerPath is null or an existing file", exe == null || File.Exists(exe), exe ?? "null");
            T.Check("IsInstalled agrees with FindPlayerPath", locator.IsInstalled() == (exe != null));

            string installRoot = locator.FindInstallRoot();
            T.Check("FindInstallRoot is null or an existing Versions folder",
                installRoot == null || (Directory.Exists(installRoot) && Path.GetFileName(installRoot) == "Versions"), installRoot ?? "null");

            IReadOnlyList<string> cacheDirs = locator.GetCacheDirectories();
            T.Check("GetCacheDirectories lists only existing folders", cacheDirs != null && cacheDirs.All(Directory.Exists));

            string localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            string robloxRoot = Path.Combine(localAppData, "Roblox");
            bool expectedLogin = File.Exists(Path.Combine(robloxRoot, "LocalStorage", "RobloxCookies.dat"))
                || File.Exists(Path.Combine(robloxRoot, "RobloxCookies.dat"));
            T.Check("HasLoginState matches the cookie file check", locator.HasLoginState() == expectedLogin);

            // A non-Roblox link must be refused without starting anything.
            LaunchResult bad = new WinRobloxLauncher(locator).LaunchUrl("https://example.com", 100);
            T.Check("LaunchUrl refuses a non-Roblox link", !bad.Started && bad.Pid == 0 && !string.IsNullOrEmpty(bad.Error));

            LaunchResult none = new WinRobloxLauncher(locator).LaunchUrl(null, 100);
            T.Check("LaunchUrl(null) returns a failed result", !none.Started && !string.IsNullOrEmpty(none.Error));

            T.Check("JoinArguments quotes spaces, quotes and trailing backslashes",
                WinRobloxLauncher.JoinArguments(new string[] { "a b", "c\"d", "e\\" }) == "\"a b\" \"c\\\"d\" e\\");
            T.Check("JoinArguments quotes an empty argument",
                WinRobloxLauncher.JoinArguments(new string[] { "" }) == "\"\"");

            IRobloxLauncher launcher = new WinRobloxLauncher(locator);
            T.Check("CountSingleInstanceLocks(0) is 0", launcher.CountSingleInstanceLocks(0) == 0);
            T.Check("CountSingleInstanceLocks(-1) is 0", launcher.CountSingleInstanceLocks(-1) == 0);
            T.Check("ReleaseSingleInstanceLock(0) is 0", launcher.ReleaseSingleInstanceLock(0) == 0);

            RegistryChecks();
        }

        // Creates HKCU\Software\CookieJarTest_<guid> with two login-like values and one unrelated value.
        // Only the two login-like values may be reported and deleted; the unrelated one must survive.
        private static void RegistryChecks()
        {
            string keyPath = "Software\\CookieJarTest_" + Guid.NewGuid().ToString("N");
            string prefix = "HKCU\\" + keyPath + "\\";
            try
            {
                IntPtr key;
                uint disposition;
                int rc = Kernel32.RegCreateKeyExW(HkeyCurrentUser, keyPath, 0, null, 0, KeyAllAccess, IntPtr.Zero, out key, out disposition);
                T.Check("throw-away registry key is created", rc == 0 && key != IntPtr.Zero, "rc " + rc);
                if (rc != 0)
                {
                    return;
                }

                try
                {
                    SetString(key, "RobloxCookie", "a");
                    SetString(key, "AuthToken", "b");
                    SetString(key, "Keep", "c");
                }
                finally
                {
                    Kernel32.RegCloseKey(key);
                }

                List<string> deleted = WinRobloxLocator.ClearLoginValuesAt(keyPath);
                T.Check("login-like values are reported as deleted",
                    deleted.Count == 2 && deleted.Contains(prefix + "RobloxCookie") && deleted.Contains(prefix + "AuthToken"),
                    string.Join(", ", deleted));

                T.Check("RobloxCookie is gone", !ValueExists(keyPath, "RobloxCookie"));
                T.Check("AuthToken is gone", !ValueExists(keyPath, "AuthToken"));
                T.Check("unrelated value Keep survives", ValueExists(keyPath, "Keep"));

                T.Check("a missing key yields no deletions",
                    WinRobloxLocator.ClearLoginValuesAt("Software\\CookieJarTest_missing_" + Guid.NewGuid().ToString("N")).Count == 0);
            }
            finally
            {
                try
                {
                    IntPtr key;
                    if (Kernel32.RegOpenKeyExW(HkeyCurrentUser, keyPath, 0, KeyAllAccess, out key) == 0)
                    {
                        Kernel32.RegDeleteValueW(key, "Keep");
                        Kernel32.RegDeleteValueW(key, "RobloxCookie");
                        Kernel32.RegDeleteValueW(key, "AuthToken");
                        Kernel32.RegCloseKey(key);
                    }
                    Kernel32.RegDeleteKeyW(HkeyCurrentUser, keyPath);
                }
                catch
                {
                    // Best effort cleanup of the throw-away key
                }
            }
        }

        private static void SetString(IntPtr key, string name, string value)
        {
            byte[] data = Encoding.Unicode.GetBytes(value + "\0");
            int rc = Kernel32.RegSetValueExW(key, name, 0, RegSz, data, (uint)data.Length);
            T.Check("registry value " + name + " is written", rc == 0, "rc " + rc);
        }

        private static bool ValueExists(string keyPath, string name)
        {
            IntPtr key;
            if (Kernel32.RegOpenKeyExW(HkeyCurrentUser, keyPath, 0, KeyQueryValue, out key) != 0)
            {
                return false;
            }

            try
            {
                uint type;
                uint size = 0;
                int rc = Kernel32.RegQueryValueExW(key, name, IntPtr.Zero, out type, null, ref size);
                // ERROR_SUCCESS or ERROR_MORE_DATA means the value exists; ERROR_FILE_NOT_FOUND (2) means it does not.
                return rc == 0 || rc == 234;
            }
            finally
            {
                Kernel32.RegCloseKey(key);
            }
        }
    }
}
