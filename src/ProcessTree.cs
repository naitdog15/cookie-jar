using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Security.Principal;

namespace RbxMulti
{
    /// <summary>
    /// Result of a process-tree kill. Counts and PID lists describe what happened to each process.
    /// </summary>
    public sealed class KillResult
    {
        /// <summary>Number of processes confirmed terminated (exit observed within the wait window).</summary>
        public int Killed;

        /// <summary>Number of processes that were already gone (exited, or PID no longer valid).</summary>
        public int AlreadyGone;

        /// <summary>PIDs that could not be terminated for reasons other than access denial.</summary>
        public List<int> Failed;

        /// <summary>PIDs that could not be opened or terminated due to access denial (Win32 error 5).</summary>
        public List<int> AccessDenied;

        /// <summary>Creates an empty result.</summary>
        public KillResult()
        {
            Failed = new List<int>();
            AccessDenied = new List<int>();
        }
    }

    /// <summary>
    /// Process-tree utilities modelled on Sysinternals Process Explorer's "Kill Process Tree".
    /// Parent/child relationships come from a Toolhelp snapshot, with PID-reuse protection based on
    /// process creation times.
    /// </summary>
    public static class ProcessTree
    {
        // ---- Native declarations -------------------------------------------------------------

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern IntPtr CreateToolhelp32Snapshot(uint dwFlags, uint th32ProcessID);

        [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode, EntryPoint = "Process32FirstW")]
        private static extern bool Process32FirstW(IntPtr hSnapshot, ref PROCESSENTRY32W lppe);

        [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode, EntryPoint = "Process32NextW")]
        private static extern bool Process32NextW(IntPtr hSnapshot, ref PROCESSENTRY32W lppe);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool CloseHandle(IntPtr hObject);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern IntPtr OpenProcess(uint dwDesiredAccess, bool bInheritHandle, uint dwProcessId);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool TerminateProcess(IntPtr hProcess, uint uExitCode);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern uint WaitForSingleObject(IntPtr hHandle, uint dwMilliseconds);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool GetProcessTimes(IntPtr hProcess, out long lpCreationTime, out long lpExitTime, out long lpKernelTime, out long lpUserTime);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern IntPtr GetCurrentProcess();

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern uint GetCurrentProcessId();

        [DllImport("advapi32.dll", SetLastError = true)]
        private static extern bool OpenProcessToken(IntPtr ProcessHandle, uint DesiredAccess, out IntPtr TokenHandle);

        [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode, EntryPoint = "LookupPrivilegeValueW")]
        private static extern bool LookupPrivilegeValueW(string lpSystemName, string lpName, out LUID lpLuid);

        [DllImport("advapi32.dll", SetLastError = true)]
        private static extern bool AdjustTokenPrivileges(IntPtr TokenHandle, bool DisableAllPrivileges, ref TOKEN_PRIVILEGES NewState, uint BufferLength, IntPtr PreviousState, IntPtr ReturnLength);

        [StructLayout(LayoutKind.Sequential)]
        private struct LUID
        {
            public uint LowPart;
            public int HighPart;
        }

        // 4 + 8 + 4 = 16 bytes, matching the native TOKEN_PRIVILEGES with one entry.
        [StructLayout(LayoutKind.Sequential)]
        private struct TOKEN_PRIVILEGES
        {
            public uint PrivilegeCount;
            public LUID Luid;
            public uint Attributes;
        }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct PROCESSENTRY32W
        {
            public uint dwSize;
            public uint cntUsage;
            public uint th32ProcessID;
            public IntPtr th32DefaultHeapID;
            public uint th32ModuleID;
            public uint cntThreads;
            public uint th32ParentProcessID;
            public int pcPriClassBase;
            public uint dwFlags;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)]
            public string szExeFile;
        }

        // ---- Constants -----------------------------------------------------------------------

        private const uint TH32CS_SNAPPROCESS = 0x00000002;
        private const uint PROCESS_TERMINATE = 0x0001;
        private const uint PROCESS_QUERY_LIMITED_INFORMATION = 0x1000;
        private const uint SYNCHRONIZE = 0x00100000;
        private const uint TOKEN_ADJUST_PRIVILEGES = 0x0020;
        private const uint TOKEN_QUERY = 0x0008;
        private const uint SE_PRIVILEGE_ENABLED = 0x00000002;
        private const uint WAIT_OBJECT_0 = 0x00000000;
        private const uint WAIT_TIMEOUT = 0x00000102;
        private const int ERROR_ACCESS_DENIED = 5;
        private const int ERROR_INVALID_PARAMETER = 87;
        private const uint TERMINATE_WAIT_MS = 2000;
        private const int MaxKillPasses = 5;
        private const int MaxTreeDepth = 64;

        private static readonly IntPtr InvalidSnapshot = new IntPtr(-1);

        // ---- Snapshot model ------------------------------------------------------------------

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
            var pids = new List<int>();
            var ppids = new List<int>();

            IntPtr hSnap = CreateToolhelp32Snapshot(TH32CS_SNAPPROCESS, 0);
            if (hSnap == InvalidSnapshot || hSnap == IntPtr.Zero)
            {
                return null;
            }

            try
            {
                PROCESSENTRY32W pe = new PROCESSENTRY32W();
                pe.dwSize = (uint)Marshal.SizeOf(typeof(PROCESSENTRY32W));

                if (Process32FirstW(hSnap, ref pe))
                {
                    do
                    {
                        pids.Add((int)pe.th32ProcessID);
                        ppids.Add((int)pe.th32ParentProcessID);
                    }
                    while (Process32NextW(hSnap, ref pe));
                }
            }
            finally
            {
                CloseHandle(hSnap);
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

        /// <summary>Returns the creation time of a process (FILETIME as long), or false if unavailable.</summary>
        private static bool TryGetStartTime(int pid, out long creationTime)
        {
            creationTime = 0;
            IntPtr h = OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, false, (uint)pid);
            if (h == IntPtr.Zero)
            {
                return false;
            }

            try
            {
                long exit;
                long kernel;
                long user;
                return GetProcessTimes(h, out creationTime, out exit, out kernel, out user);
            }
            finally
            {
                CloseHandle(h);
            }
        }

        /// <summary>
        /// Post-order (children before parents) list of descendants of root. Cycle-safe and depth-limited.
        /// </summary>
        private static List<int> PostOrder(Snapshot snap, int root, bool includeRoot)
        {
            var output = new List<int>();
            var visited = new HashSet<int>();
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

        // ---- Public API ----------------------------------------------------------------------

        /// <summary>
        /// Returns all descendants of a process, leaves first (children before their parents).
        /// The root PID itself is excluded. Returns an empty list on failure.
        /// </summary>
        /// <param name="pid">Root process ID.</param>
        public static List<int> GetDescendants(int pid)
        {
            try
            {
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

        /// <summary>
        /// Returns the process tree leaves first, with the root PID as the last element.
        /// Returns an empty list on failure.
        /// </summary>
        /// <param name="pid">Root process ID.</param>
        public static List<int> GetTreeLeavesFirst(int pid)
        {
            try
            {
                Snapshot snap = TakeSnapshot();
                if (snap == null)
                {
                    return new List<int>();
                }
                return PostOrder(snap, pid, true);
            }
            catch
            {
                return new List<int>();
            }
        }

        /// <summary>
        /// Kills a process and its entire tree (Process Explorer "Kill Process Tree" semantics).
        /// Returns the number of processes confirmed terminated.
        /// </summary>
        /// <param name="pid">Root process ID.</param>
        public static int KillTree(int pid)
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

        /// <summary>
        /// Kills a process tree and reports per-process outcomes. Descendants are killed leaves-first over up
        /// to five passes (re-snapshotting each time to catch newly spawned children); the root is killed last.
        /// PID 0, PID 4 and this process are never touched.
        /// </summary>
        /// <param name="pid">Root process ID.</param>
        public static KillResult KillTreeDetailed(int pid)
        {
            KillResult result = new KillResult();
            try
            {
                if (pid <= 4)
                {
                    return result;
                }

                int self = (int)GetCurrentProcessId();
                if (pid == self)
                {
                    return result;
                }

                var handled = new HashSet<int>();
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

        /// <summary>
        /// Returns true if the current process is running with an elevated (administrator) token.
        /// </summary>
        public static bool IsElevated()
        {
            try
            {
                WindowsIdentity identity = WindowsIdentity.GetCurrent();
                WindowsPrincipal principal = new WindowsPrincipal(identity);
                return principal.IsInRole(WindowsBuiltInRole.Administrator);
            }
            catch
            {
                return false;
            }
        }

        /// <summary>
        /// Returns true if the process exists and has not yet exited. Access-denied counts as alive.
        /// </summary>
        /// <param name="pid">Process ID to test.</param>
        public static bool IsAlive(int pid)
        {
            try
            {
                if (pid <= 0)
                {
                    return false;
                }

                IntPtr h = OpenProcess(SYNCHRONIZE, false, (uint)pid);
                if (h == IntPtr.Zero)
                {
                    return Marshal.GetLastWin32Error() == ERROR_ACCESS_DENIED;
                }

                try
                {
                    return WaitForSingleObject(h, 0) == WAIT_TIMEOUT;
                }
                finally
                {
                    CloseHandle(h);
                }
            }
            catch
            {
                return false;
            }
        }

        // ---- Internals -----------------------------------------------------------------------

        /// <summary>Terminates one process and records the outcome in result.</summary>
        private static void KillOne(int pid, KillResult result, ref bool debugTried)
        {
            const uint access = PROCESS_TERMINATE | SYNCHRONIZE | PROCESS_QUERY_LIMITED_INFORMATION;

            IntPtr h = OpenProcess(access, false, (uint)pid);
            if (h == IntPtr.Zero)
            {
                int err = Marshal.GetLastWin32Error();
                if (err == ERROR_INVALID_PARAMETER)
                {
                    result.AlreadyGone++;
                    return;
                }
                if (err != ERROR_ACCESS_DENIED)
                {
                    result.Failed.Add(pid);
                    return;
                }

                if (!debugTried)
                {
                    debugTried = true;
                    EnableDebugPrivilege();
                }

                h = OpenProcess(access, false, (uint)pid);
                if (h == IntPtr.Zero)
                {
                    err = Marshal.GetLastWin32Error();
                    if (err == ERROR_INVALID_PARAMETER)
                    {
                        result.AlreadyGone++;
                    }
                    else if (err == ERROR_ACCESS_DENIED)
                    {
                        result.AccessDenied.Add(pid);
                    }
                    else
                    {
                        result.Failed.Add(pid);
                    }
                    return;
                }
            }

            try
            {
                if (WaitForSingleObject(h, 0) == WAIT_OBJECT_0)
                {
                    // Already exited; the handle stays signaled.
                    result.AlreadyGone++;
                    return;
                }

                if (!TerminateProcess(h, 1))
                {
                    int err = Marshal.GetLastWin32Error();
                    if (err == ERROR_INVALID_PARAMETER)
                    {
                        result.AlreadyGone++;
                    }
                    else if (err == ERROR_ACCESS_DENIED)
                    {
                        result.AccessDenied.Add(pid);
                    }
                    else
                    {
                        result.Failed.Add(pid);
                    }
                    return;
                }

                if (WaitForSingleObject(h, TERMINATE_WAIT_MS) == WAIT_OBJECT_0)
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
                CloseHandle(h);
            }
        }

        /// <summary>Enables SeDebugPrivilege on the current token. Returns true only if it was granted.</summary>
        private static bool EnableDebugPrivilege()
        {
            try
            {
                IntPtr token;
                if (!OpenProcessToken(GetCurrentProcess(), TOKEN_ADJUST_PRIVILEGES | TOKEN_QUERY, out token))
                {
                    return false;
                }

                try
                {
                    LUID luid;
                    if (!LookupPrivilegeValueW(null, "SeDebugPrivilege", out luid))
                    {
                        return false;
                    }

                    TOKEN_PRIVILEGES tp = new TOKEN_PRIVILEGES();
                    tp.PrivilegeCount = 1;
                    tp.Luid = luid;
                    tp.Attributes = SE_PRIVILEGE_ENABLED;

                    bool ok = AdjustTokenPrivileges(token, false, ref tp, 0, IntPtr.Zero, IntPtr.Zero);
                    // AdjustTokenPrivileges returns TRUE even when the privilege is not held; check last error.
                    return ok && Marshal.GetLastWin32Error() == 0;
                }
                finally
                {
                    CloseHandle(token);
                }
            }
            catch
            {
                return false;
            }
        }
    }
}
