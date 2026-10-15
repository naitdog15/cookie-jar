using System;
using System.IO;
using System.Threading;
using CookieJar.Platform.Windows;

namespace CookieJar.Tests
{
    public static class StartupTests
    {
        public static void Run()
        {
            BuildArguments();
            IsEnabledMissingTask();
            Paths();
            InstanceLock();
        }

        private static void BuildArguments()
        {
            // Literal derived by hand from src/Startup.cs BuildCreateArguments:
            // "/Create /TN \"<task>\" /SC ONLOGON /RL HIGHEST /F /DELAY 0000:20 /TR \"\\\"<exe>\\\" --minibar\""
            const string expected = "/Create /TN \"RobloxMultiLauncher_MiniBar\" /SC ONLOGON /RL HIGHEST /F /DELAY 0000:20"
                + " /TR \"\\\"C:/Program Files/Cookie Jar/CookieJar.exe\\\" --minibar\"";
            string actual = WinStartupService.BuildCreateArguments("C:/Program Files/Cookie Jar/CookieJar.exe", "RobloxMultiLauncher_MiniBar");
            T.Check("startup: BuildCreateArguments matches src/Startup.cs exactly", actual == expected, actual);
            T.Check("startup: default task name is RobloxMultiLauncher_MiniBar", WinStartupService.DefaultTaskName == "RobloxMultiLauncher_MiniBar");
        }

        private static void IsEnabledMissingTask()
        {
            // Read-only query against a task name that cannot exist. The real startup task is never touched.
            string name = "RobloxMultiLauncher_Test_Missing_" + Guid.NewGuid().ToString("N");
            WinStartupService service = new WinStartupService(name);
            T.Check("startup: IsEnabled is false for a task that does not exist", !service.IsEnabled());
        }

        private static void Paths()
        {
            // Only path construction is checked; nothing is created on disk.
            WinAppPaths paths = new WinAppPaths();
            string local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            string expectedData = Path.Combine(local, "RobloxMultiLauncher");
            T.Check("paths: DataDirectory is %LocalAppData%\\RobloxMultiLauncher", paths.DataDirectory == expectedData, paths.DataDirectory);
            T.Check("paths: DataDirectory ends with RobloxMultiLauncher", Path.GetFileName(paths.DataDirectory) == "RobloxMultiLauncher");
            T.Check("paths: LogDirectory is inside DataDirectory", Path.GetDirectoryName(paths.LogDirectory) == paths.DataDirectory, paths.LogDirectory);
            T.Check("paths: DiagnosticsDirectory is the src diag folder", paths.DiagnosticsDirectory == Path.Combine(expectedData, "diag"), paths.DiagnosticsDirectory);
        }

        private static void InstanceLock()
        {
            // Unique name so a running Cookie Jar is never affected.
            string name = "CookieJar.Test.Lock." + Guid.NewGuid().ToString("N");
            WinShellService owner = new WinShellService();
            try
            {
                T.Check("lock: first acquire succeeds", owner.TryAcquireAppInstanceLock(name));

                // A named mutex is re-entrant on one thread, so the competing copy is checked from another thread.
                bool? other = null;
                Thread t = new Thread(() => { other = new WinShellService().TryAcquireAppInstanceLock(name); });
                t.Start();
                t.Join();
                T.Check("lock: second copy is refused while held", other == false);

                owner.ReleaseAppInstanceLock(name);
                bool? after = null;
                Thread t2 = new Thread(() =>
                {
                    WinShellService again = new WinShellService();
                    after = again.TryAcquireAppInstanceLock(name);
                    again.ReleaseAppInstanceLock(name);
                });
                t2.Start();
                t2.Join();
                T.Check("lock: acquire succeeds again after release", after == true);
            }
            finally
            {
                owner.ReleaseAppInstanceLock(name);
            }
        }
    }
}
