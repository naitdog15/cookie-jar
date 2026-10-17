using System;
using System.Collections.Generic;
using CookieJar.Platform;

namespace CookieJar.Core
{
    /// <summary>One instance as seen by BackgroundEfficiency.Update.</summary>
    public struct EfficiencyTarget
    {
        public int Pid;
        public bool Running;
        public DateTime StartedUtc;
    }

    /// <summary>
    /// Keeps CPU for the client you are using: Roblox clients that are not the foreground window are put into the
    /// background priority class through IProcessService; the foreground client, and every client again when the
    /// feature is turned off or the launcher exits, is restored. Clients that are not Running yet, or started less
    /// than GraceSeconds ago, are left alone so loading is not slowed.
    ///
    /// Event-light: Update is called from the launcher's existing 1 s tick and only calls the process service when an
    /// instance's wanted state differs from what was set. UI thread only (not thread safe).
    /// </summary>
    public sealed class BackgroundEfficiency
    {
        public const int GraceSeconds = 60;

        private const int WantThrottle = 1;
        private const int IsRunning = 2;

        private readonly IProcessService _processes;

        /// <summary>
        /// Pid that owns the frontmost window: 0 when there is none, negative when it cannot be told. While it is
        /// unknown nothing is slowed down (a client you are using must never be throttled by mistake).
        /// Wire it with ForegroundFrom(platform); replaceable so tests need no window server.
        /// </summary>
        public Func<int> ForegroundPid = DefaultForegroundPid;

        /// <summary>Optional log sink (called on the UI thread, e.g. when the OS refuses to change a client).</summary>
        public Action<string> Log;

        private readonly Dictionary<int, bool> _throttled = new Dictionary<int, bool>();
        private readonly HashSet<int> _failed = new HashSet<int>();

        // pid -> flags for the current Update. One pid can appear in several targets (a stopped card keeps its old pid,
        // and the OS may give that pid to a new client), so the entries are combined instead of fighting each other.
        // StartedUtc is used only for the grace period; pid reuse is handled by this combining and by forgetting
        // stopped pids in Update, not by a start-time comparison.
        private readonly Dictionary<int, int> _now = new Dictionary<int, int>();
        private readonly List<int> _drop = new List<int>();
        private bool _enabled = true;

        public BackgroundEfficiency(IProcessService processes)
        {
            if (processes == null) throw new ArgumentNullException(nameof(processes));
            _processes = processes;
        }

        /// <summary>
        /// Focus mode: when above 0, every other Running client is held at the background priority at once (no
        /// start-up grace, whatever is in front), and this pid is left alone. It applies even while Enabled is off.
        /// Set it back to 0 and call Update to restore the others. MAC-UNVERIFIED: macOS lowers priority with nice, and
        /// an unprivileged process usually cannot raise it back, so Unfocus may leave clients slowed until they restart.
        /// </summary>
        public int FocusPid;

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

        /// <summary>The default source: unknown (-1), so no client is slowed until a real source is wired.</summary>
        public static int DefaultForegroundPid()
        {
            return -1;
        }

        /// <summary>
        /// A foreground source from the platform's window service (it must implement IForegroundProcessSource);
        /// unknown (-1) when it does not.
        /// </summary>
        public static Func<int> ForegroundFrom(IPlatform platform)
        {
            return delegate
            {
                IForegroundProcessSource source = platform == null ? null : platform.Windows as IForegroundProcessSource;
                if (source == null) return -1;
                int pid = source.GetForegroundProcessId();
                return pid < 0 ? 0 : pid;   // a source answers 0 for "no window"; never negative
            };
        }

        /// <summary>Brings every client to the wanted state for this moment. Call about once a second.</summary>
        public void Update(IList<EfficiencyTarget> targets, DateTime nowUtc)
        {
            bool focus = FocusPid > 0;
            if (targets == null) return;
            if (!_enabled && !focus)
            {
                if (_throttled.Count > 0) RestoreAll();   // Focus ended while the feature is off: put everything back
                return;
            }
            int fg = ForegroundPid();
            _now.Clear();
            for (int i = 0; i < targets.Count; i++)
            {
                EfficiencyTarget t = targets[i];
                if (t.Pid <= 0) continue;
                int flags;
                _now.TryGetValue(t.Pid, out flags);
                if (t.Running) flags |= IsRunning;
                // fg < 0: the foreground process is unknown, so nothing may be slowed (and anything slowed is restored).
                if (focus)
                {
                    if (t.Running && t.Pid != FocusPid) flags |= WantThrottle;
                }
                else if (_enabled && t.Running && fg >= 0 && t.Pid != fg && (nowUtc - t.StartedUtc).TotalSeconds >= GraceSeconds) flags |= WantThrottle;
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
                    if (Log != null) Log("Could not lower the priority of process " + pid + " (the OS refused); it keeps running at normal priority.");
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

        /// <summary>Returns every throttled client to normal priority. Safe to call repeatedly.</summary>
        public void RestoreAll()
        {
            foreach (KeyValuePair<int, bool> kv in _throttled)
            {
                if (kv.Value) SetState(kv.Key, false);
            }
            _throttled.Clear();
            _failed.Clear();
        }

        private bool SetState(int pid, bool throttle)
        {
            try
            {
                return throttle ? _processes.SetBackgroundPriority(pid) : _processes.ClearBackgroundPriority(pid);
            }
            catch (Exception)
            {
                return false;
            }
        }
    }
}
