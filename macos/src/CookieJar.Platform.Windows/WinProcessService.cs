using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.InteropServices;

namespace CookieJar.Platform.Windows
{
    /// <summary>
    /// Windows process queries and control. Ported from src/ProcessTree.cs, src/BackgroundEfficiency.cs and
    /// src/MemoryGovernor.cs (QueryWorkingSet, cap/clear). Never throws: failures return false, -1 or empty.
    /// </summary>
    public sealed class WinProcessService : IProcessService
    {
        // ---- Constants (same values as src) --------------------------------------------------

        private const int ErrorInvalidParameter = Kernel32.ERROR_INVALID_PARAMETER;
        private const int ErrorAccessDenied = Kernel32.ERROR_ACCESS_DENIED;
        private const uint StillActive = 259;
        private const uint TerminateWaitMs = 2000;
        private const int MaxKillPasses = 5;
        private const int MaxTreeDepth = 64;
        private const long MinWorkingSetMb = 50;
        private const uint PowerThrottlingVersion = 1;
        private const int TokenElevationClass = 20;   // TOKEN_INFORMATION_CLASS.TokenElevation

        private static readonly IntPtr InvalidSnapshot = new IntPtr(-1);

        private enum ProcState { Exited, Unknown, Alive }

        // ---- IProcessService -----------------------------------------------------------------

        public bool IsRunning(int pid)
        {
            return IsAlive(pid);
        }

        public long GetWorkingSetBytes(int pid)
        {
            try
            {
                long bytes;
                return QueryWorkingSet(pid, out bytes) == ProcState.Alive ? bytes : -1;
            }
            catch
            {
                return -1;
            }
        }

        public IReadOnlyList<int> FindByName(string processName)
        {
            List<int> pids = new List<int>();
            try
            {
                if (string.IsNullOrEmpty(processName))
                {
                    return pids;
                }

                string name = processName;
                if (name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
                {
                    name = name.Substring(0, name.Length - 4);
                }

                foreach (Process p in Process.GetProcesses())
                {
                    try
                    {
                        if (string.Equals(p.ProcessName, name, StringComparison.OrdinalIgnoreCase))
                        {
                            pids.Add(p.Id);
                        }
                    }
                    catch
                    {
                        // Process exited while being inspected
                    }
                    finally
                    {
                        p.Dispose();
                    }
                }
            }
            catch
            {
                // Return what was gathered
            }
            return pids;
        }

        public IReadOnlyList<int> GetDescendants(int pid)
        {
            try
            {
                if (pid <= 0)
                {
                    return new List<int>();
                }

                Snapshot snap = TakeSnapshot();
                if (snap == null)
                {
                    return new List<int>();
                }
                return PostOrder(snap, pid, false);
            }
            catch
            {
                return new List<int>();
            }
        }

        public int KillTree(int pid)
        {
            try
            {
                return KillTreeDetailed(pid).Killed;
            }
            catch
            {
                return 0;
            }
        }

        public bool SetBackgroundPriority(int pid)
        {
            return SetPowerState(pid, true);
        }

        public bool ClearBackgroundPriority(int pid)
        {
            return SetPowerState(pid, false);
        }

        public bool TryCapWorkingSet(int pid, long maxBytes)
        {
            IntPtr handle = IntPtr.Zero;
            try
            {
                if (pid <= 0 || maxBytes <= 0)
                {
                    return false;
                }

                handle = Kernel32.OpenProcess(Kernel32.PROCESS_SET_QUOTA | Kernel32.PROCESS_QUERY_LIMITED_INFORMATION, false, (uint)pid);
                if (handle == IntPtr.Zero)
                {
                    return false;
                }

                // Same clamp as MemoryGovernor.ApplyNativeCap: UIntPtr is 64-bit on x64 and 32-bit on x86.
                ulong maxValue = (ulong)maxBytes;
                ulong nativeMax = UIntPtr.Size == 4 ? (ulong)uint.MaxValue : ulong.MaxValue;
                if (maxValue > nativeMax)
                {
                    maxValue = nativeMax;
                }

                UIntPtr minimum = (UIntPtr)((ulong)MinWorkingSetMb * 1024UL * 1024UL);
                return Kernel32.SetProcessWorkingSetSizeEx(handle, minimum, (UIntPtr)maxValue,
                    Kernel32.QUOTA_LIMITS_HARDWS_MAX_ENABLE | Kernel32.QUOTA_LIMITS_HARDWS_MIN_DISABLE);
            }
            catch
            {
                return false;
            }
            finally
            {
                if (handle != IntPtr.Zero) Kernel32.CloseHandle(handle);
            }
        }

        public bool ClearWorkingSetCap(int pid)
        {
            IntPtr handle = IntPtr.Zero;
            try
            {
                if (pid <= 0)
                {
                    return false;
                }

                handle = Kernel32.OpenProcess(Kernel32.PROCESS_SET_QUOTA | Kernel32.PROCESS_QUERY_LIMITED_INFORMATION, false, (uint)pid);
                if (handle == IntPtr.Zero)
                {
                    return false;
                }

                // All-ones values are ignored by the kernel when MAX_DISABLE is set (see MemoryGovernor.ClearNativeCap).
                UIntPtr allOnes = UIntPtr.Size == 4 ? new UIntPtr(uint.MaxValue) : new UIntPtr(ulong.MaxValue);
                return Kernel32.SetProcessWorkingSetSizeEx(handle, allOnes, allOnes, Kernel32.QUOTA_LIMITS_HARDWS_MAX_DISABLE);
            }
            catch
            {
                return false;
            }
            finally
            {
                if (handle != IntPtr.Zero) Kernel32.CloseHandle(handle);
            }
        }

        public bool IsElevated()
        {
            IntPtr token = IntPtr.Zero;
            try
            {
                // Same answer as src's IsInRole(Administrator): a UAC-filtered token is not elevated.
                if (!Kernel32.OpenProcessToken(Kernel32.GetCurrentProcess(), Kernel32.TOKEN_QUERY, out token))
                {
                    return false;
                }

                uint elevated;
                uint returned;
                if (!Kernel32.GetTokenInformation(token, TokenElevationClass, out elevated, sizeof(uint), out returned))
                {
                    return false;
                }
                return elevated != 0;
            }
            catch
            {
                return false;
            }
            finally
            {
                if (token != IntPtr.Zero) Kernel32.CloseHandle(token);
            }
        }


        // ---- Liveness ------------------------------------------------------------------------

        /// <summary>True if the process exists and has not exited. Access-denied counts as alive (ProcessTree.IsAlive).</summary>
        internal static bool IsAlive(int pid)
        {
            try
            {
                if (pid <= 0)
                {
                    return false;
                }

                IntPtr h = Kernel32.OpenProcess(Kernel32.SYNCHRONIZE, false, (uint)pid);
                if (h == IntPtr.Zero)
                {
                    return Marshal.GetLastWin32Error() == ErrorAccessDenied;
                }

                try
                {
                    return Kernel32.WaitForSingleObject(h, 0) == Kernel32.WAIT_TIMEOUT;
                }
                finally
                {
                    Kernel32.CloseHandle(h);
                }
            }
            catch
            {
                return false;
            }
        }

        // ---- Working set (MemoryGovernor.QueryWorkingSet) -----------------------------------

        private static ProcState QueryWorkingSet(int pid, out long bytes)
        {
            bytes = 0;
            if (pid <= 0) return ProcState.Exited;
            IntPtr handle = Kernel32.OpenProcess(Kernel32.PROCESS_QUERY_LIMITED_INFORMATION, false, (uint)pid);
            if (handle == IntPtr.Zero)
            {
                // ERROR_INVALID_PARAMETER means no such pid; anything else (access denied) is unknown.
                return Marshal.GetLastWin32Error() == ErrorInvalidParameter ? ProcState.Exited : ProcState.Unknown;
            }
            try
            {
                uint code;
                if (Kernel32.GetExitCodeProcess(handle, out code) && code != StillActive) return ProcState.Exited;
                Kernel32.ProcessMemoryCounters c = new Kernel32.ProcessMemoryCounters();
                c.cb = (uint)Marshal.SizeOf(typeof(Kernel32.ProcessMemoryCounters));
                if (!Kernel32.K32GetProcessMemoryInfo(handle, ref c, c.cb)) return ProcState.Unknown;
                bytes = (long)c.WorkingSetSize.ToUInt64();
                return ProcState.Alive;
            }
            finally
            {
                Kernel32.CloseHandle(handle);
            }
        }

        // ---- Efficiency mode (BackgroundEfficiency.SetState) ---------------------------------

        private static bool SetPowerState(int pid, bool throttle)
        {
            IntPtr h = IntPtr.Zero;
            try
            {
                if (pid <= 0) return false;
                h = Kernel32.OpenProcess(Kernel32.PROCESS_SET_INFORMATION | Kernel32.PROCESS_QUERY_LIMITED_INFORMATION, false, (uint)pid);
                if (h == IntPtr.Zero) return false;
                bool priorityOk = Kernel32.SetPriorityClass(h, throttle ? Kernel32.BelowNormalPriorityClass : Kernel32.NormalPriorityClass);
                // ControlMask 0 hands power throttling back to the system; EXECUTION_SPEED in both masks forces efficiency mode.
                Kernel32.PowerThrottlingState st = new Kernel32.PowerThrottlingState();
                st.Version = PowerThrottlingVersion;
                st.ControlMask = throttle ? Kernel32.PowerThrottlingExecutionSpeed : 0;
                st.StateMask = throttle ? Kernel32.PowerThrottlingExecutionSpeed : 0;
                bool powerOk = Kernel32.SetProcessInformation(h, Kernel32.ProcessPowerThrottling, ref st,
                    (uint)Marshal.SizeOf(typeof(Kernel32.PowerThrottlingState)));
                return priorityOk || powerOk;   // efficiency mode needs Windows 10 1709+; priority alone is still a win
            }
            catch
            {
                return false;
            }
            finally
            {
                if (h != IntPtr.Zero) Kernel32.CloseHandle(h);
            }
        }

        // ---- Snapshot and tree (ProcessTree) -------------------------------------------------

        private sealed class KillResult
        {
            public int Killed;
            public List<int> Failed = new List<int>();
            public List<int> AccessDenied = new List<int>();
        }

        /// <summary>A point-in-time view of the process table with validated parent/child links.</summary>
        private sealed class Snapshot
        {
            public readonly HashSet<int> Pids = new HashSet<int>();
            public readonly Dictionary<int, long> Start = new Dictionary<int, long>();
            public readonly Dictionary<int, List<int>> Children = new Dictionary<int, List<int>>();
        }

        /// <summary>Reads the process table and builds validated child lists. Returns null on failure.</summary>
        private static Snapshot TakeSnapshot()
        {
            List<int> pids = new List<int>();
            List<int> ppids = new List<int>();

            IntPtr hSnap = Kernel32.CreateToolhelp32Snapshot(Kernel32.TH32CS_SNAPPROCESS, 0);
            if (hSnap == InvalidSnapshot || hSnap == IntPtr.Zero)
            {
                return null;
            }

            try
            {
                Kernel32.PROCESSENTRY32W pe = new Kernel32.PROCESSENTRY32W();
                pe.dwSize = (uint)Marshal.SizeOf(typeof(Kernel32.PROCESSENTRY32W));

                if (Kernel32.Process32FirstW(hSnap, ref pe))
                {
                    do
                    {
                        pids.Add((int)pe.th32ProcessID);
                        ppids.Add((int)pe.th32ParentProcessID);
                    }
                    while (Kernel32.Process32NextW(hSnap, ref pe));
                }
            }
            finally
            {
                Kernel32.CloseHandle(hSnap);
            }

            Snapshot snap = new Snapshot();
            for (int i = 0; i < pids.Count; i++)
            {
                snap.Pids.Add(pids[i]);
                long start;
                if (TryGetStartTime(pids[i], out start))
                {
                    snap.Start[pids[i]] = start;
                }
            }

            for (int i = 0; i < pids.Count; i++)
            {
                int child = pids[i];
                int parent = ppids[i];

                if (child == parent || !snap.Pids.Contains(parent))
                {
                    continue;
                }

                // PID-reuse guard: a child cannot have been created before its parent.
                long childStart;
                long parentStart;
                if (snap.Start.TryGetValue(child, out childStart) &&
                    snap.Start.TryGetValue(parent, out parentStart) &&
                    childStart < parentStart)
                {
                    continue;
                }

                List<int> kids;
                if (!snap.Children.TryGetValue(parent, out kids))
                {
                    kids = new List<int>();
                    snap.Children[parent] = kids;
                }
                kids.Add(child);
            }

            return snap;
        }

        /// <summary>Creation time of a process (FILETIME as long), or false if unavailable.</summary>
        private static bool TryGetStartTime(int pid, out long creationTime)
        {
            creationTime = 0;
            IntPtr h = Kernel32.OpenProcess(Kernel32.PROCESS_QUERY_LIMITED_INFORMATION, false, (uint)pid);
            if (h == IntPtr.Zero)
            {
                return false;
            }

            try
            {
                long exit;
                long kernel;
                long user;
                return Kernel32.GetProcessTimes(h, out creationTime, out exit, out kernel, out user);
            }
            finally
            {
                Kernel32.CloseHandle(h);
            }
        }

        /// <summary>Post-order (children before parents) list of descendants of root. Cycle-safe and depth-limited.</summary>
        private static List<int> PostOrder(Snapshot snap, int root, bool includeRoot)
        {
            List<int> output = new List<int>();
            HashSet<int> visited = new HashSet<int>();
            visited.Add(root);
            Walk(snap, root, 0, visited, output);
            if (includeRoot)
            {
                output.Add(root);
            }
            return output;
        }

        private static void Walk(Snapshot snap, int parent, int depth, HashSet<int> visited, List<int> output)
        {
            if (depth >= MaxTreeDepth)
            {
                return;
            }

            List<int> kids;
            if (!snap.Children.TryGetValue(parent, out kids))
            {
                return;
            }

            foreach (int kid in kids)
            {
                if (!visited.Add(kid))
                {
                    continue;
                }
                Walk(snap, kid, depth + 1, visited, output);
                output.Add(kid);
            }
        }

        /// <summary>
        /// Kills a process tree (Process Explorer "Kill Process Tree" semantics). Descendants are killed leaves-first over
        /// up to five passes (re-snapshotting to catch newly spawned children); the root is killed last.
        /// PID 0, PID 4 and this process are never touched.
        /// </summary>
        private static KillResult KillTreeDetailed(int pid)
        {
            KillResult result = new KillResult();
            try
            {
                if (pid <= 4)
                {
                    return result;
                }

                int self = (int)Kernel32.GetCurrentProcessId();
                if (pid == self)
                {
                    return result;
                }

                HashSet<int> handled = new HashSet<int>();
                bool debugTried = false;

                for (int pass = 0; pass < MaxKillPasses; pass++)
                {
                    Snapshot snap = TakeSnapshot();
                    if (snap == null)
                    {
                        break;
                    }

                    List<int> targets = PostOrder(snap, pid, false);
                    int newTargets = 0;
                    foreach (int target in targets)
                    {
                        if (target <= 4 || target == self || handled.Contains(target))
                        {
                            continue;
                        }
                        handled.Add(target);
                        newTargets++;
                        KillOne(target, result, ref debugTried);
                    }

                    if (newTargets == 0)
                    {
                        break;
                    }
                }

                if (!handled.Contains(pid))
                {
                    KillOne(pid, result, ref debugTried);
                }
            }
            catch
            {
                // Never throw from a public method; return whatever was gathered.
            }
            return result;
        }

        /// <summary>Terminates one process and records the outcome in result.</summary>
        private static void KillOne(int pid, KillResult result, ref bool debugTried)
        {
            const uint access = Kernel32.PROCESS_TERMINATE | Kernel32.SYNCHRONIZE | Kernel32.PROCESS_QUERY_LIMITED_INFORMATION;

            IntPtr h = Kernel32.OpenProcess(access, false, (uint)pid);
            if (h == IntPtr.Zero)
            {
                int err = Marshal.GetLastWin32Error();
                if (err == ErrorInvalidParameter)
                {
                    // No such process: already gone.
                    return;
                }
                if (err != ErrorAccessDenied)
                {
                    result.Failed.Add(pid);
                    return;
                }

                if (!debugTried)
                {
                    debugTried = true;
                    EnableDebugPrivilege();
                }

                h = Kernel32.OpenProcess(access, false, (uint)pid);
                if (h == IntPtr.Zero)
                {
                    err = Marshal.GetLastWin32Error();
                    if (err == ErrorAccessDenied)
                    {
                        result.AccessDenied.Add(pid);
                    }
                    else if (err != ErrorInvalidParameter)
                    {
                        result.Failed.Add(pid);
                    }
                    return;
                }
            }

            try
            {
                if (Kernel32.WaitForSingleObject(h, 0) == Kernel32.WAIT_OBJECT_0)
                {
                    // Already exited; the handle stays signaled.
                    return;
                }

                if (!Kernel32.TerminateProcess(h, 1))
                {
                    int err = Marshal.GetLastWin32Error();
                    if (err == ErrorAccessDenied)
                    {
                        result.AccessDenied.Add(pid);
                    }
                    else if (err != ErrorInvalidParameter)
                    {
                        result.Failed.Add(pid);
                    }
                    return;
                }

                if (Kernel32.WaitForSingleObject(h, TerminateWaitMs) == Kernel32.WAIT_OBJECT_0)
                {
                    result.Killed++;
                }
                else
                {
                    result.Failed.Add(pid);
                }
            }
            finally
            {
                Kernel32.CloseHandle(h);
            }
        }

        /// <summary>Enables SeDebugPrivilege on the current token. Returns true only if it was granted.</summary>
        private static bool EnableDebugPrivilege()
        {
            try
            {
                IntPtr token;
                if (!Kernel32.OpenProcessToken(Kernel32.GetCurrentProcess(), Kernel32.TOKEN_ADJUST_PRIVILEGES | Kernel32.TOKEN_QUERY, out token))
                {
                    return false;
                }

                try
                {
                    Kernel32.LUID luid;
                    if (!Kernel32.LookupPrivilegeValueW(null, "SeDebugPrivilege", out luid))
                    {
                        return false;
                    }

                    Kernel32.TOKEN_PRIVILEGES tp = new Kernel32.TOKEN_PRIVILEGES();
                    tp.PrivilegeCount = 1;
                    tp.Luid = luid;
                    tp.Attributes = Kernel32.SE_PRIVILEGE_ENABLED;

                    bool ok = Kernel32.AdjustTokenPrivileges(token, false, ref tp, 0, IntPtr.Zero, IntPtr.Zero);
                    // AdjustTokenPrivileges returns TRUE even when the privilege is not held; check last error.
                    return ok && Marshal.GetLastWin32Error() == 0;
                }
                finally
                {
                    Kernel32.CloseHandle(token);
                }
            }
            catch
            {
                return false;
            }
        }
    }
}
