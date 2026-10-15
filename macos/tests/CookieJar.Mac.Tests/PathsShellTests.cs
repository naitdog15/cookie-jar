using System;
using System.IO;
using System.Linq;
using CookieJar.Platform;
using CookieJar.Platform.Mac;

namespace CookieJar.Tests
{
    public static class PathsShellTests
    {
        public static void Run()
        {
            PathTests();
            ShellOpenTests();
            LockTests();
        }

        private static void PathTests()
        {
            string home = Path.Combine(Path.GetTempPath(), "cookiejar-paths-" + Guid.NewGuid().ToString("N"));
            try
            {
                MacAppPaths paths = new MacAppPaths(home);
                T.Check("paths DataDirectory is <home>/Library/Application Support/CookieJar",
                    paths.DataDirectory == home + "/Library/Application Support/CookieJar");
                T.Check("paths LogDirectory is <home>/Library/Logs/CookieJar",
                    paths.LogDirectory == home + "/Library/Logs/CookieJar");
                T.Check("paths DiagnosticsDirectory is <DataDirectory>/diagnostics",
                    paths.DiagnosticsDirectory == paths.DataDirectory + "/diagnostics");
                T.Check("paths getters create the folders", Directory.Exists(paths.DataDirectory) &&
                    Directory.Exists(paths.LogDirectory) && Directory.Exists(paths.DiagnosticsDirectory));
            }
            finally
            {
                try { if (Directory.Exists(home)) Directory.Delete(home, true); } catch (Exception) { }
            }

        }

        private static void ShellOpenTests()
        {
            RecordingRunner runner = new RecordingRunner();
            MacShellService shell = new MacShellService(runner, Path.GetTempPath());
            T.Check("OpenFolder returns true on success", shell.OpenFolder("/Users/tester/Library/Logs/CookieJar"));
            T.Check("OpenFolder runs open with exact argv",
                runner.Calls.Count == 1 && runner.Calls[0].FileName == "open" &&
                runner.Calls[0].Arguments.SequenceEqual(new[] { "/Users/tester/Library/Logs/CookieJar" }));

            int before = runner.Calls.Count;
            T.Check("OpenFolder rejects an empty path", !shell.OpenFolder("") && !shell.OpenFolder(null));
            T.Check("OpenFolder does not run anything for an empty path", runner.Calls.Count == before);

            RecordingRunner broken = new RecordingRunner { FailToStart = true };
            T.Check("OpenFolder returns false when open cannot start", !new MacShellService(broken, Path.GetTempPath()).OpenFolder("/tmp/x"));

            T.Check("lock name sanitises to [A-Za-z0-9._-]", MacShellService.SanitizeName("Cookie Jar/Main!") == "Cookie_Jar_Main_");
        }

        private static void LockTests()
        {
            string dir = Path.Combine(Path.GetTempPath(), "cookiejar-lock-" + Guid.NewGuid().ToString("N"));
            RecordingRunner runner = new RecordingRunner();
            MacShellService shell = new MacShellService(runner, dir);
            try
            {
                T.Check("first lock acquire returns true", shell.TryAcquireAppInstanceLock("CookieJar Main"));
                T.Check("lock file uses the sanitised name", File.Exists(Path.Combine(dir, "CookieJar_Main.lock")));
                T.Check("second acquire of the same lock returns false", !shell.TryAcquireAppInstanceLock("CookieJar Main"));

                MacShellService other = new MacShellService(runner, dir);
                T.Check("another service instance cannot take a held lock", !other.TryAcquireAppInstanceLock("CookieJar Main"));

                shell.ReleaseAppInstanceLock("CookieJar Main");
                // The file stays on purpose: deleting it after the release lets a second process lock the old file while a
                // third creates and locks a new one (two owners). Only the lock is released.
                T.Check("lock file is kept on release (no delete race), but the lock is free",
                    File.Exists(Path.Combine(dir, "CookieJar_Main.lock")) && other.TryAcquireAppInstanceLock("CookieJar Main"));
                other.ReleaseAppInstanceLock("CookieJar Main");
                T.Check("acquire after release returns true", shell.TryAcquireAppInstanceLock("CookieJar Main"));
                shell.ReleaseAppInstanceLock("CookieJar Main");

                T.Check("empty lock name is rejected", !shell.TryAcquireAppInstanceLock("") && !shell.TryAcquireAppInstanceLock(null));
            }
            finally
            {
                try { shell.ReleaseAppInstanceLock("CookieJar Main"); } catch (Exception) { }
                try { if (Directory.Exists(dir)) Directory.Delete(dir, true); } catch (Exception) { }
            }
        }
    }
}
