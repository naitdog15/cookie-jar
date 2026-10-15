using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using CookieJar.Platform;
using CookieJar.Platform.Windows;

namespace CookieJar.Tests
{
    public static class ProcessTests
    {
        // int.MaxValue is far above the pids Windows hands out (multiples of 4 well below 2^20 in practice).
        private const int UnusedPid = int.MaxValue;
        private const long EightHundredMb = 800L * 1024L * 1024L;

        public static void Run()
        {
            IProcessService svc = new WinProcessService();
            int self = Process.GetCurrentProcess().Id;

            T.Check("own pid is running", svc.IsRunning(self));

            long ownWs = svc.GetWorkingSetBytes(self);
            T.Check("own working set is positive", ownWs > 0, ownWs.ToString());

            T.Check("unused pid is not running", !svc.IsRunning(UnusedPid));
            T.Check("pid 0 is not running", !svc.IsRunning(0));
            T.Check("pid -1 is not running", !svc.IsRunning(-1));

            foreach (int pid in new int[] { 0, -1, UnusedPid })
            {
                BadPidChecks(svc, pid);
            }

            bool elevatedThrew = false;
            bool elevated = false;
            try
            {
                elevated = svc.IsElevated();
            }
            catch (Exception)
            {
                elevatedThrew = true;
            }
            T.Check("IsElevated does not throw", !elevatedThrew, "elevated " + elevated);

            T.Check("FindByName(null) is empty", svc.FindByName(null).Count == 0);
            T.Check("FindByName(empty) is empty", svc.FindByName("").Count == 0);

            ChildChecks(svc, self);
        }

        private static void BadPidChecks(IProcessService svc, int pid)
        {
            string tag = "pid " + pid;
            T.Check("GetWorkingSetBytes is -1 for " + tag, svc.GetWorkingSetBytes(pid) == -1);
            T.Check("GetDescendants is empty for " + tag, svc.GetDescendants(pid).Count == 0);
            T.Check("KillTree kills nothing for " + tag, svc.KillTree(pid) == 0);
            T.Check("SetBackgroundPriority is false for " + tag, !svc.SetBackgroundPriority(pid));
            T.Check("ClearBackgroundPriority is false for " + tag, !svc.ClearBackgroundPriority(pid));
            T.Check("TryCapWorkingSet is false for " + tag, !svc.TryCapWorkingSet(pid, EightHundredMb));
            T.Check("ClearWorkingSetCap is false for " + tag, !svc.ClearWorkingSetCap(pid));
        }

        private static void ChildChecks(IProcessService svc, int self)
        {
            // Throw-away child that only this test starts and kills.
            Process child = Process.Start(new ProcessStartInfo("ping.exe", "-n 30 127.0.0.1")
            {
                UseShellExecute = false,
                CreateNoWindow = true
            });

            if (child == null)
            {
                T.Check("ping.exe started", false);
                return;
            }

            try
            {
                int childPid = child.Id;

                T.Check("ping child is running", svc.IsRunning(childPid));
                T.Check("FindByName(ping) contains the child", svc.FindByName("ping").Contains(childPid));
                T.Check("FindByName(PING.exe) contains the child", svc.FindByName("PING.exe").Contains(childPid));
                T.Check("child is a descendant of the test process", svc.GetDescendants(self).Contains(childPid));

                // Read before the cap calls: ClearWorkingSetCap passes the all-ones values that Windows documents as
                // emptying the working set, so the size is not meaningful afterwards.
                T.Check("child working set is positive", svc.GetWorkingSetBytes(childPid) > 0);

                T.Check("SetBackgroundPriority(child) is true", svc.SetBackgroundPriority(childPid));
                T.Check("ClearBackgroundPriority(child) is true", svc.ClearBackgroundPriority(childPid));

                T.Check("TryCapWorkingSet(child) is true", svc.TryCapWorkingSet(childPid, EightHundredMb));
                T.Check("ClearWorkingSetCap(child) is true", svc.ClearWorkingSetCap(childPid));

                T.Check("single-instance locks in the child are 0",
                    new WinRobloxLauncher().CountSingleInstanceLocks(childPid) == 0);

                int killed = svc.KillTree(childPid);
                T.Check("KillTree(child) killed at least 1", killed >= 1, "killed " + killed);
                T.Check("child is no longer running after KillTree", !svc.IsRunning(childPid));
            }
            finally
            {
                try
                {
                    if (!child.HasExited)
                    {
                        child.Kill();
                    }
                }
                catch
                {
                    // Already gone
                }
                child.Dispose();
            }
        }
    }
}
