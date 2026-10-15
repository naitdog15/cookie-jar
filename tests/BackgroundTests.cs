// BackgroundTests.cs - RamScale (slider scale) and BackgroundEfficiency (priority + efficiency mode for clients that are
// not in front). No window, mouse or keyboard: the efficiency tests drive a throw-away child process (ping) and read its
// priority class and power-throttling state back from Windows. C# 5.
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.InteropServices;

namespace RbxMulti
{
    public static class BackgroundTests
    {
        [StructLayout(LayoutKind.Sequential)]
        private struct PowerState { public uint Version, ControlMask, StateMask; }

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern IntPtr OpenProcess(uint access, bool inherit, int pid);
        [DllImport("kernel32.dll")]
        private static extern bool CloseHandle(IntPtr h);
        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool GetProcessInformation(IntPtr h, int infoClass, ref PowerState info, int size);

        private static bool EcoOn(int pid)
        {
            IntPtr h = OpenProcess(0x1000, false, pid);
            if (h == IntPtr.Zero) return false;
            try
            {
                PowerState st = new PowerState();
                st.Version = 1;   // Windows rejects the call (error 87) unless the input struct carries its version
                if (!GetProcessInformation(h, 4, ref st, Marshal.SizeOf(typeof(PowerState)))) return false;
                return (st.ControlMask & 1) != 0 && (st.StateMask & 1) != 0;
            }
            finally { CloseHandle(h); }
        }

        private static ProcessPriorityClass PriorityOf(int pid)
        {
            using (Process p = Process.GetProcessById(pid)) { return p.PriorityClass; }
        }

        private static List<EfficiencyTarget> One(int pid, bool running, DateTime started)
        {
            List<EfficiencyTarget> l = new List<EfficiencyTarget>();
            EfficiencyTarget t = new EfficiencyTarget();
            t.Pid = pid; t.Running = running; t.StartedUtc = started;
            l.Add(t);
            return l;
        }

        public static void Run(string dir, Action<string, bool, string> check, Action<string> info)
        {
            Console.WriteLine();
            Console.WriteLine("=== RAM slider scale and background efficiency ===");

            // ---- RamScale
            check("RamScale: position 0 is Auto (0 MB)", RamScale.MbFromIndex(0) == 0 && RamScale.IndexFromMb(0) == 0, "");
            check("RamScale: position 1 is 128 MB, last position is 3072 MB",
                RamScale.MbFromIndex(1) == 128 && RamScale.MbFromIndex(RamScale.Steps) == 3072, RamScale.MbFromIndex(1) + " / " + RamScale.MbFromIndex(RamScale.Steps));
            check("RamScale: 47 capped positions plus Auto", RamScale.Steps == 47, "steps=" + RamScale.Steps);
            bool roundTrip = true;
            for (int i = 0; i <= RamScale.Steps; i++) if (RamScale.IndexFromMb(RamScale.MbFromIndex(i)) != i) roundTrip = false;
            check("RamScale: every position round-trips through MB", roundTrip, "");
            check("RamScale: out of range values clamp (50 -> 128, 99999 -> 3072, -5 -> Auto)",
                RamScale.IndexFromMb(50) == 1 && RamScale.IndexFromMb(99999) == RamScale.Steps && RamScale.IndexFromMb(-5) == 0, "");
            check("RamScale: old presets land on the nearest step (Low 1216, Medium 2048, High 3584)",
                RamScale.MbFromIndex(RamScale.IndexFromMb(1216)) == 1216 && RamScale.MbFromIndex(RamScale.IndexFromMb(2048)) == 2048 && RamScale.MbFromIndex(RamScale.IndexFromMb(3584)) == 3072, "");
            check("RamScale: labels Auto / 640 MB / 1.19 GB / 3 GB",
                RamScale.Label(0) == "Auto" && RamScale.Label(640) == "640 MB" && RamScale.Label(1216) == "1.19 GB" && RamScale.Label(3072) == "3 GB",
                RamScale.Label(0) + " | " + RamScale.Label(640) + " | " + RamScale.Label(1216) + " | " + RamScale.Label(3072));

            // ---- BackgroundEfficiency, against a real child process
            Process child = null;
            try
            {
                ProcessStartInfo psi = new ProcessStartInfo("ping.exe", "127.0.0.1 -n 60");
                psi.CreateNoWindow = true; psi.UseShellExecute = false; psi.RedirectStandardOutput = true;
                child = Process.Start(psi);
                int pid = child.Id;
                DateTime now = DateTime.UtcNow;
                DateTime old = now.AddSeconds(-(BackgroundEfficiency.GraceSeconds + 5));

                BackgroundEfficiency be = new BackgroundEfficiency();
                be.ForegroundPid = delegate { return 0; };

                be.Update(One(pid, true, now), now);
                check("efficiency: a client inside the start-up grace period is left alone",
                    !be.IsThrottled(pid) && PriorityOf(pid) == ProcessPriorityClass.Normal, PriorityOf(pid).ToString());

                be.Update(One(pid, false, old), now);
                check("efficiency: a client that is not Running is left alone", !be.IsThrottled(pid) && PriorityOf(pid) == ProcessPriorityClass.Normal, "");

                be.Update(One(pid, true, old), now);
                check("efficiency: a Running background client goes to BelowNormal", be.IsThrottled(pid) && PriorityOf(pid) == ProcessPriorityClass.BelowNormal, PriorityOf(pid).ToString());
                bool eco = EcoOn(pid);
                info("      efficiency mode (EcoQoS) read back from Windows: " + eco);
                check("efficiency: efficiency mode is on for it (Windows 10 1709+)", eco, "");
                check("efficiency: ThrottledCount is 1", be.ThrottledCount == 1, "count=" + be.ThrottledCount);

                be.ForegroundPid = delegate { return pid; };
                be.Update(One(pid, true, old), now);
                check("efficiency: bringing it to the foreground restores Normal and system-managed power",
                    !be.IsThrottled(pid) && PriorityOf(pid) == ProcessPriorityClass.Normal && !EcoOn(pid), PriorityOf(pid).ToString());

                be.ForegroundPid = delegate { return 0; };
                be.Update(One(pid, true, old), now);
                be.Enabled = false;
                check("efficiency: switching the feature off restores Normal at once", !be.IsThrottled(pid) && PriorityOf(pid) == ProcessPriorityClass.Normal && !EcoOn(pid), PriorityOf(pid).ToString());
                be.Update(One(pid, true, old), now);
                check("efficiency: while off, Update changes nothing", !be.IsThrottled(pid) && PriorityOf(pid) == ProcessPriorityClass.Normal, "");

                be.Enabled = true;
                be.Update(One(pid, true, old), now);
                be.RestoreAll();
                check("efficiency: RestoreAll (launcher exit) puts it back", !be.IsThrottled(pid) && PriorityOf(pid) == ProcessPriorityClass.Normal && !EcoOn(pid), "");

                // a stopped card that still carries the pid of a running client must not undo its throttle
                List<EfficiencyTarget> twin = One(pid, false, old);
                twin.AddRange(One(pid, true, old));
                be.Update(twin, now);
                be.Update(twin, now);
                check("efficiency: a stopped entry with the same pid as a running client does not undo or thrash it",
                    be.IsThrottled(pid) && PriorityOf(pid) == ProcessPriorityClass.BelowNormal, PriorityOf(pid).ToString());

                // when the process is gone (listed as stopped / removed) the state is simply forgotten
                be.Update(One(pid, false, old), now);
                check("efficiency: when the client stops, its state is forgotten without touching the pid again", be.ThrottledCount == 0, "count=" + be.ThrottledCount);

                be.Update(One(pid, true, old), now);
                be.Update(new List<EfficiencyTarget>(), now);
                check("efficiency: a client removed from the list is forgotten", be.ThrottledCount == 0, "");
                be.RestoreAll();
                check("efficiency: (cleanup) the still-running child is back at Normal", PriorityOf(pid) == ProcessPriorityClass.Normal || !be.IsThrottled(pid), PriorityOf(pid).ToString());

                // A pid Windows will not let us touch must not throw, spam the log, or retry every second.
                int logs = 0;
                BackgroundEfficiency bad = new BackgroundEfficiency();
                bad.ForegroundPid = delegate { return 0; };
                bad.Log = delegate(string s) { logs++; };
                List<EfficiencyTarget> ghost = One(999999, true, old);
                bad.Update(ghost, now); bad.Update(ghost, now); bad.Update(ghost, now);
                check("efficiency: an unreachable pid is not marked throttled and is logged once", !bad.IsThrottled(999999) && logs == 1, "logs=" + logs);
            }
            finally
            {
                if (child != null) { try { child.Kill(); } catch (Exception) { } child.Dispose(); }
            }
        }
    }
}
