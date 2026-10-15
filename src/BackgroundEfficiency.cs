using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;

namespace RbxMulti
{
    /// <summary>One instance as seen by BackgroundEfficiency.Update.</summary>
    public struct EfficiencyTarget
    {
        public int Pid;
        public bool Running;
        public DateTime StartedUtc;
    }

    /// <summary>
    /// Keeps CPU for the client you are using: Roblox clients that are not the foreground window get BELOW_NORMAL
    /// priority and Windows "efficiency mode" (EcoQoS, execution-speed power throttling); the foreground client, and
    /// every client again when the feature is turned off or the launcher exits, goes back to Normal / system managed.
    /// Clients that are not Running yet, or started less than GraceSeconds ago, are left alone so loading is not slowed.
    ///
    /// Event-light: Update is called from the launcher's existing 1 s tick and costs one GetForegroundWindow unless a
    /// client changes state, because Windows is only called when an instance's wanted state differs from what was set.
    /// UI thread only (not thread safe).
    /// </summary>
    public sealed class BackgroundEfficiency
    {
        public const int GraceSeconds = 60;

        private const uint ProcessSetInformation = 0x0200;
        private const uint ProcessQueryLimitedInformation = 0x1000;
        private const uint BelowNormalPriorityClass = 0x4000;
        private const uint NormalPriorityClass = 0x0020;
        private const int ProcessPowerThrottling = 4;
        private const uint PowerThrottlingExecutionSpeed = 0x1;

        [StructLayout(LayoutKind.Sequential)]
        private struct PowerThrottlingState
        {
            public uint Version;
            public uint ControlMask;
            public uint StateMask;
        }

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern IntPtr OpenProcess(uint access, bool inheritHandle, int processId);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool CloseHandle(IntPtr handle);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool SetPriorityClass(IntPtr process, uint priorityClass);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool SetProcessInformation(IntPtr process, int infoClass, ref PowerThrottlingState info, int size);

        [DllImport("user32.dll")]
        private static extern IntPtr GetForegroundWindow();

        [DllImport("user32.dll")]
        private static extern uint GetWindowThreadProcessId(IntPtr hwnd, out uint processId);

        /// <summary>Process id that owns the foreground window, or 0. Replaceable so tests need no real window.</summary>
        public Func<int> ForegroundPid = DefaultForegroundPid;

        /// <summary>Optional log sink (called on the UI thread, e.g. when Windows refuses to change a client).</summary>
        public Action<string> Log;

        private readonly Dictionary<int, bool> _throttled = new Dictionary<int, bool>();
        private readonly HashSet<int> _failed = new HashSet<int>();
        private const int WantThrottle = 1;
        private const int IsRunning = 2;

        // pid -> flags for the current Update. One pid can appear in several targets (a stopped card keeps its old pid,
        // and Windows may give that pid to a new client), so the entries are combined instead of fighting each other.
        private readonly Dictionary<int, int> _now = new Dictionary<int, int>();
        private readonly List<int> _drop = new List<int>();
        private bool _enabled = true;

        public bool Enabled
        {
            get { return _enabled; }
            set
            {
                if (_enabled == value) return;
                _enabled = value;
                if (!value) RestoreAll();
            }
        }

        /// <summary>True while pid is currently held at the reduced priority.</summary>
        public bool IsThrottled(int pid)
        {
            bool on;
            return _throttled.TryGetValue(pid, out on) && on;
        }

        public int ThrottledCount
        {
            get
            {
                int n = 0;
                foreach (KeyValuePair<int, bool> kv in _throttled) if (kv.Value) n++;
                return n;
            }
        }

        public static int DefaultForegroundPid()
        {
            try
            {
                IntPtr h = GetForegroundWindow();
                if (h == IntPtr.Zero) return 0;
                uint pid;
                GetWindowThreadProcessId(h, out pid);
                return (int)pid;
            }
            catch (Exception)
            {
                return 0;
            }
        }

        /// <summary>Brings every client to the wanted state for this moment. Call about once a second.</summary>
        public void Update(IList<EfficiencyTarget> targets, DateTime nowUtc)
        {
            if (!_enabled || targets == null) return;
            int fg = ForegroundPid();
            _now.Clear();
            for (int i = 0; i < targets.Count; i++)
            {
                EfficiencyTarget t = targets[i];
                if (t.Pid <= 0) continue;
                int flags;
                _now.TryGetValue(t.Pid, out flags);
                if (t.Running) flags |= IsRunning;
                if (t.Running && t.Pid != fg && (nowUtc - t.StartedUtc).TotalSeconds >= GraceSeconds) flags |= WantThrottle;
                _now[t.Pid] = flags;
            }

            foreach (KeyValuePair<int, int> kv in _now)
            {
                int pid = kv.Key;
                bool want = (kv.Value & WantThrottle) != 0;
                bool have;
                _throttled.TryGetValue(pid, out have);
                if (want == have || _failed.Contains(pid)) continue;
                if (SetState(pid, want)) _throttled[pid] = want;
                else
                {
                    _failed.Add(pid);   // do not retry (and re-log) every second
                    if (Log != null) Log("Could not lower the priority of process " + pid + " (Windows refused); it keeps running at normal priority.");
                }
            }

            // Pids that left the list are restored (the client may still be alive); pids listed as stopped are only
            // forgotten, because the process is gone and its pid may already belong to someone else.
            _drop.Clear();
            foreach (KeyValuePair<int, bool> kv in _throttled)
            {
                int flags;
                if (!_now.TryGetValue(kv.Key, out flags) || (flags & IsRunning) == 0) _drop.Add(kv.Key);
            }
            foreach (int pid in _drop)
            {
                if (_throttled[pid] && !_now.ContainsKey(pid)) SetState(pid, false);
                _throttled.Remove(pid);
            }
            _failed.RemoveWhere(delegate(int pid) { return !_now.ContainsKey(pid); });
        }

        /// <summary>Returns every throttled client to Normal priority and system-managed power. Safe to call repeatedly.</summary>
        public void RestoreAll()
        {
            foreach (KeyValuePair<int, bool> kv in _throttled)
            {
                if (kv.Value) SetState(kv.Key, false);
            }
            _throttled.Clear();
            _failed.Clear();
        }

        private static bool SetState(int pid, bool throttle)
        {
            IntPtr h = IntPtr.Zero;
            try
            {
                h = OpenProcess(ProcessSetInformation | ProcessQueryLimitedInformation, false, pid);
                if (h == IntPtr.Zero) return false;
                bool priorityOk = SetPriorityClass(h, throttle ? BelowNormalPriorityClass : NormalPriorityClass);
                // ControlMask 0 hands power throttling back to the system; EXECUTION_SPEED in both masks forces efficiency mode.
                PowerThrottlingState st = new PowerThrottlingState();
                st.Version = 1;
                st.ControlMask = throttle ? PowerThrottlingExecutionSpeed : 0;
                st.StateMask = throttle ? PowerThrottlingExecutionSpeed : 0;
                bool powerOk = SetProcessInformation(h, ProcessPowerThrottling, ref st, Marshal.SizeOf(typeof(PowerThrottlingState)));
                return priorityOk || powerOk;   // efficiency mode needs Windows 10 1709+; priority alone is still a win
            }
            catch (Exception)
            {
                return false;
            }
            finally
            {
                if (h != IntPtr.Zero) CloseHandle(h);
            }
        }
    }
}
