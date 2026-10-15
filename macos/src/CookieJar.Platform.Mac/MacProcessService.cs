using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Threading;

namespace CookieJar.Platform.Mac
{
    /// <summary>Capabilities this macOS process service really provides.</summary>
    public static class MacCapabilities
    {
        public static readonly PlatformCapabilities Process = PlatformCapabilities.BackgroundPriority | PlatformCapabilities.MultiInstance;
    }

    /// <summary>The libc calls the process service needs. Replaced by a fake in tests.</summary>
    public interface IMacProcessNative
    {
        /// <summary>kill(2). True on success.</summary>
        bool Kill(int pid, int signal);

        /// <summary>setpriority(2) with PRIO_PROCESS. True when the nice value is applied.</summary>
        bool SetPriority(int pid, int nice);

        /// <summary>True if the pid exists. EPERM also means it exists.</summary>
        bool IsAlive(int pid);

        /// <summary>Effective uid from geteuid. Returns -1 when unknown.</summary>
        int GetUid();
    }

    /// <summary>Default implementation over libc. Every call is guarded and never throws.</summary>
    public sealed class MacLibcProcessNative : IMacProcessNative
    {
        private const int SigZero = 0;
        private const int EPerm = 1;
        private const int PrioProcess = 0;

        // MAC-UNVERIFIED: assumes the "libc" name resolves on macOS (libc.dylib / libSystem) under .NET and Mono.
        [DllImport("libc", EntryPoint = "kill", SetLastError = true)]
        private static extern int NativeKill(int pid, int sig);

        // MAC-UNVERIFIED: setpriority(2) and getpriority(2) take (which, who, prio) as ints on macOS.
        [DllImport("libc", EntryPoint = "setpriority", SetLastError = true)]
        private static extern int NativeSetPriority(int which, int who, int prio);

        [DllImport("libc", EntryPoint = "getpriority", SetLastError = true)]
        private static extern int NativeGetPriority(int which, int who);

        // MAC-UNVERIFIED: geteuid(2) returns uid_t, a 32-bit unsigned value on macOS.
        [DllImport("libc", EntryPoint = "geteuid")]
        private static extern uint NativeGetEuid();

        public bool Kill(int pid, int signal)
        {
            try
            {
                // MAC-UNVERIFIED: kill(2) signals a pid owned by this user; other users' pids fail with EPERM.
                return NativeKill(pid, signal) == 0;
            }
            catch (Exception)
            {
                return false;
            }
        }

        public bool SetPriority(int pid, int nice)
        {
            try
            {
                // MAC-UNVERIFIED: raising nice works for the process owner; lowering it back needs root on macOS.
                if (NativeSetPriority(PrioProcess, pid, nice) != 0) return false;
                return NativeGetPriority(PrioProcess, pid) == nice;
            }
            catch (Exception)
            {
                return false;
            }
        }

        public bool IsAlive(int pid)
        {
            try
            {
                // MAC-UNVERIFIED: kill(pid, 0) probes existence on macOS; a zombie still answers as alive.
                if (NativeKill(pid, SigZero) == 0) return true;
                return Marshal.GetLastWin32Error() == EPerm;
            }
            catch (Exception)
            {
                return false;
            }
        }

        public int GetUid()
        {
            try
            {
                return unchecked((int)NativeGetEuid());
            }
            catch (Exception)
            {
                return -1;
            }
        }
    }

    /// <summary>
    /// Process queries and control on macOS, built on `ps` (through ICommandRunner) and libc (through IMacProcessNative).
    /// Never throws; failures return false, 0, -1 or an empty list.
    /// </summary>
    public sealed class MacProcessService : IProcessService
    {
        private const int SigTerm = 15;
        private const int SigKill = 9;
        private const int BackgroundNice = 10;
        private const int NormalNice = 0;
        private const int PsTimeoutMs = 5000;
        private const int DefaultKillWaitMs = 300;

        // MAC-UNVERIFIED: ps lives at /bin/ps on macOS and accepts -axo with these column names.
        private const string PsPath = "/bin/ps";
        private const string PsColumns = "pid=,ppid=,rss=,comm=";

        private readonly ICommandRunner runner;
        private readonly IMacProcessNative native;
        private readonly Action<int> sleep;
        private readonly int killWaitMs;

        public MacProcessService(ICommandRunner runner) : this(runner, new MacLibcProcessNative()) { }

        public MacProcessService(ICommandRunner runner, IMacProcessNative native)
            : this(runner, native, Thread.Sleep, DefaultKillWaitMs) { }

        /// <summary>Full constructor. Tests pass a no-op sleep and a zero wait.</summary>
        public MacProcessService(ICommandRunner runner, IMacProcessNative native, Action<int> sleep, int killWaitMs)
        {
            this.runner = runner ?? throw new ArgumentNullException(nameof(runner));
            this.native = native ?? throw new ArgumentNullException(nameof(native));
            this.sleep = sleep ?? throw new ArgumentNullException(nameof(sleep));
            this.killWaitMs = Math.Max(0, killWaitMs);
        }

        public bool IsRunning(int pid)
        {
            try
            {
                return pid > 0 && native.IsAlive(pid);
            }
            catch (Exception)
            {
                return false;
            }
        }

        public long GetWorkingSetBytes(int pid)
        {
            try
            {
                if (pid <= 0) return -1;
                List<MacProcessRow> rows = Snapshot();
                if (rows == null) return -1;
                foreach (MacProcessRow row in rows)
                {
                    if (row.Pid == pid) return row.RssBytes;
                }
                return -1;
            }
            catch (Exception)
            {
                return -1;
            }
        }

        public IReadOnlyList<int> FindByName(string processName)
        {
            var pids = new List<int>();
            try
            {
                if (string.IsNullOrWhiteSpace(processName)) return pids;

                // A ".app" suffix on the query is dropped, as Windows lookups drop ".exe".
                string wanted = processName.Trim();
                if (wanted.EndsWith(".app", StringComparison.OrdinalIgnoreCase))
                {
                    wanted = wanted.Substring(0, wanted.Length - 4);
                }

                List<MacProcessRow> rows = Snapshot();
                if (rows == null) return pids;
                foreach (MacProcessRow row in rows)
                {
                    if (string.Equals(row.Name, wanted, StringComparison.OrdinalIgnoreCase)) pids.Add(row.Pid);
                }
            }
            catch (Exception)
            {
                pids.Clear();
            }
            return pids;
        }

        public IReadOnlyList<int> GetDescendants(int pid)
        {
            try
            {
                List<MacProcessRow> rows = Snapshot();
                return rows == null ? new List<int>() : MacProcessParsing.Descendants(rows, pid);
            }
            catch (Exception)
            {
                return new List<int>();
            }
        }

        /// <summary>
        /// SIGTERM to the descendants (children first), then the root. Survivors get SIGKILL.
        /// Returns how many of the processes alive at the start are gone after the wait.
        /// </summary>
        public int KillTree(int pid)
        {
            try
            {
                // pid 0 and 1 (launchd) are never touched, and neither is this process.
                if (pid <= 1 || pid == OwnPid()) return 0;

                List<MacProcessRow> rows = Snapshot();
                List<int> targets = rows == null ? new List<int>() : MacProcessParsing.Descendants(rows, pid);
                targets.Add(pid);

                var alive = new List<int>();
                foreach (int target in targets)
                {
                    if (native.IsAlive(target)) alive.Add(target);
                }
                if (alive.Count == 0) return 0;

                foreach (int target in alive)
                {
                    native.Kill(target, SigTerm);
                }
                sleep(killWaitMs);

                var leftovers = new List<int>();
                foreach (int target in alive)
                {
                    if (native.IsAlive(target)) leftovers.Add(target);
                }
                if (leftovers.Count > 0)
                {
                    foreach (int target in leftovers)
                    {
                        native.Kill(target, SigKill);
                    }
                    sleep(killWaitMs);
                }

                int killed = 0;
                foreach (int target in alive)
                {
                    if (!native.IsAlive(target)) killed++;
                }
                return killed;
            }
            catch (Exception)
            {
                return 0;
            }
        }

        public bool SetBackgroundPriority(int pid)
        {
            return SetNice(pid, BackgroundNice);
        }

        public bool ClearBackgroundPriority(int pid)
        {
            return SetNice(pid, NormalNice);
        }

        /// <summary>macOS has no per-process resident memory cap, so this always reports unsupported.</summary>
        public bool TryCapWorkingSet(int pid, long maxBytes)
        {
            return false;
        }

        /// <summary>See TryCapWorkingSet: there is no cap to clear on macOS.</summary>
        public bool ClearWorkingSetCap(int pid)
        {
            return false;
        }

        public bool IsElevated()
        {
            try
            {
                return native.GetUid() == 0;
            }
            catch (Exception)
            {
                return false;
            }
        }

        private bool SetNice(int pid, int nice)
        {
            try
            {
                if (pid <= 1) return false;
                return native.SetPriority(pid, nice);
            }
            catch (Exception)
            {
                return false;
            }
        }

        /// <summary>Runs ps once and parses it. Null when ps fails.</summary>
        private List<MacProcessRow> Snapshot()
        {
            try
            {
                CommandResult result = runner.Run(PsPath, new[] { "-axo", PsColumns }, PsTimeoutMs);
                if (result == null || !result.Success) return null;
                return MacProcessParsing.ParsePs(result.StandardOutput);
            }
            catch (Exception)
            {
                return null;
            }
        }

        private static int OwnPid()
        {
            try
            {
                return System.Diagnostics.Process.GetCurrentProcess().Id;
            }
            catch (Exception)
            {
                return -1;
            }
        }
    }
}
