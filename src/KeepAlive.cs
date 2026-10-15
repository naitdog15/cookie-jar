// KeepAlive.cs
//
// "Keep sessions from timing out": Roblox disconnects a client after 20 minutes without input. Independent of the
// CASH OUT button, every Interval minutes this briefly focuses each running instance in turn, sends a harmless real
// input (a small mouse move over the window and back, optionally a tap of the Right Ctrl key) through InputSim and
// restores the cursor and the previously focused window. Sequential, under the global AntiIdleClicker.ClickLock,
// skipped while the user is active. Log line per instance: "Keep-alive sent to <user>".
//
// PUBLIC API (namespace RbxMulti)
//   class KeepAliveTarget { int Id; int Pid; string Label; }
//   class KeepAliveStats  { DateTime? LastKeepAliveUtc; string LastResult; DateTime FirstSeenUtc; }
//   class KeepAliveService : IDisposable
//       event Action<string> Log
//       bool Enabled; int IntervalMinutes (1..15, default 4); bool TapKey (default false)
//       void SetTargets(IList<KeepAliveTarget>)       call regularly (UI tick); new pids are scheduled, gone pids dropped
//       KeepAliveStats GetStats(int pid)
//       static int MinutesSinceInput(int pid, KeepAliveStats stats)    minutes without confirmed input (see InputSim ledger)
//       string RunOnce(KeepAliveTarget t)             one immediate keep-alive (tests, "send now")
//       Start / Stop / Dispose

using System;
using System.Collections.Generic;
using System.Threading;

namespace RbxMulti
{
    public sealed class KeepAliveTarget
    {
        public int Id;
        public int Pid;
        public string Label;
    }

    public sealed class KeepAliveStats
    {
        public DateTime? LastKeepAliveUtc;
        public string LastResult;
        public DateTime FirstSeenUtc;
    }

    public sealed class KeepAliveService : IDisposable
    {
        public const int DefaultIntervalMinutes = 4;
        public const int MinIntervalMinutes = 1;
        public const int MaxIntervalMinutes = 15;
        public const int WarnAfterMinutes = 12;

        private const uint UserActiveSkipMs = 3000;
        private const int ActiveRetryMs = 5000;
        private const int FirstDelayMs = 20000;
        private const int StaggerMs = 7000;
        private const int TickMs = 1000;

        private readonly object _lock = new object();
        private readonly Dictionary<int, KeepAliveTarget> _targets = new Dictionary<int, KeepAliveTarget>();
        private readonly Dictionary<int, KeepAliveStats> _stats = new Dictionary<int, KeepAliveStats>();
        private readonly Dictionary<int, DateTime> _due = new Dictionary<int, DateTime>();
        private ManualResetEvent _stop;
        private Thread _thread;
        private bool _disposed;
        private int _interval = DefaultIntervalMinutes;

        public event Action<string> Log;

        /// <summary>Master on/off (the UI sets it to: anti-idle master switch AND "Keep sessions from timing out").</summary>
        public volatile bool Enabled;
        public volatile bool TapKey;

        /// <summary>Tests only: replaces the real input (hwnd, tapKey) so no mouse is touched.</summary>
        internal Func<IntPtr, bool, ClickReport> WiggleHook;
        /// <summary>Tests only: replaces the idle check (milliseconds since last user input).</summary>
        internal Func<uint> IdleHook;

        public int IntervalMinutes
        {
            get { lock (_lock) { return _interval; } }
            set { lock (_lock) { _interval = Math.Max(MinIntervalMinutes, Math.Min(MaxIntervalMinutes, value)); } }
        }

        public void SetTargets(IList<KeepAliveTarget> targets)
        {
            DateTime now = DateTime.UtcNow;
            lock (_lock)
            {
                Dictionary<int, KeepAliveTarget> next = new Dictionary<int, KeepAliveTarget>();
                int slot = 0;
                if (targets != null)
                {
                    foreach (KeepAliveTarget t in targets)
                    {
                        if (t == null || t.Pid <= 0) continue;
                        next[t.Pid] = t;
                        if (!_stats.ContainsKey(t.Pid))
                        {
                            KeepAliveStats st = new KeepAliveStats();
                            st.FirstSeenUtc = now;
                            _stats[t.Pid] = st;
                            _due[t.Pid] = now.AddMilliseconds(FirstDelayMs + slot * StaggerMs);
                        }
                        slot++;
                    }
                }
                List<int> gone = new List<int>();
                foreach (int pid in _stats.Keys) if (!next.ContainsKey(pid)) gone.Add(pid);
                foreach (int pid in gone) { _stats.Remove(pid); _due.Remove(pid); }
                _targets.Clear();
                foreach (KeyValuePair<int, KeepAliveTarget> kv in next) _targets[kv.Key] = kv.Value;
            }
        }

        /// <summary>Tests only (scratch host screenshots): sets the last keep-alive and first-seen times of a target.</summary>
        internal void SetStatsForTest(int pid, DateTime? lastKeepAliveUtc, DateTime firstSeenUtc)
        {
            lock (_lock)
            {
                KeepAliveStats st;
                if (!_stats.TryGetValue(pid, out st)) { st = new KeepAliveStats(); _stats[pid] = st; }
                st.LastKeepAliveUtc = lastKeepAliveUtc;
                st.FirstSeenUtc = firstSeenUtc;
            }
        }

        public KeepAliveStats GetStats(int pid)
        {
            KeepAliveStats copy = new KeepAliveStats();
            lock (_lock)
            {
                KeepAliveStats st;
                if (_stats.TryGetValue(pid, out st))
                {
                    copy.LastKeepAliveUtc = st.LastKeepAliveUtc;
                    copy.LastResult = st.LastResult;
                    copy.FirstSeenUtc = st.FirstSeenUtc;
                }
                else
                {
                    copy.FirstSeenUtc = DateTime.UtcNow;
                }
            }
            return copy;
        }

        /// <summary>Minutes since the last confirmed input (keep-alive, click, or the user working in that window); since first seen when none yet.</summary>
        public static int MinutesSinceInput(int pid, KeepAliveStats stats)
        {
            DateTime ref0 = stats != null ? stats.FirstSeenUtc : DateTime.UtcNow;
            DateTime? c = InputSim.LastConfirmedUtc(pid);
            if (c.HasValue && c.Value > ref0) ref0 = c.Value;
            double m = (DateTime.UtcNow - ref0).TotalMinutes;
            return m < 0 ? 0 : (int)m;
        }

        /// <summary>"just now", "45s ago", "2m ago", "never".</summary>
        public static string AgoText(DateTime? utc)
        {
            if (!utc.HasValue) return "never";
            double s = (DateTime.UtcNow - utc.Value).TotalSeconds;
            if (s < 5) return "just now";
            if (s < 90) return ((int)s) + "s ago";
            if (s < 5400) return ((int)(s / 60)) + "m ago";
            return ((int)(s / 3600)) + "h ago";
        }

        public void Start()
        {
            lock (_lock)
            {
                if (_disposed || _thread != null) return;
                ManualResetEvent stop = new ManualResetEvent(false);
                _stop = stop;
                Thread t = new Thread(delegate() { RunLoop(stop); });
                t.IsBackground = true;
                t.Name = "KeepAlive";
                _thread = t;
                t.Start();
            }
        }

        public void Stop()
        {
            Thread t;
            ManualResetEvent stop;
            lock (_lock)
            {
                t = _thread; stop = _stop; _thread = null; _stop = null;
            }
            if (t == null) return;
            try { stop.Set(); } catch (Exception) { }
            if (t != Thread.CurrentThread) t.Join(3000);
        }

        public void Dispose()
        {
            lock (_lock) { if (_disposed) return; _disposed = true; }
            Stop();
        }

        private void RunLoop(ManualResetEvent stop)
        {
            while (true)
            {
                bool sig;
                try { sig = stop.WaitOne(TickMs); } catch (Exception) { return; }
                if (sig) return;
                try { Tick(stop); } catch (Exception) { }
            }
        }

        private void Tick(ManualResetEvent stop)
        {
            if (!Enabled) return;
            List<KeepAliveTarget> due = new List<KeepAliveTarget>();
            DateTime now = DateTime.UtcNow;
            lock (_lock)
            {
                foreach (KeyValuePair<int, KeepAliveTarget> kv in _targets)
                {
                    DateTime d;
                    if (_due.TryGetValue(kv.Key, out d) && d <= now) due.Add(kv.Value);
                }
            }
            foreach (KeepAliveTarget t in due)
            {
                if (!Enabled || stop.WaitOne(0)) return;
                string r = RunOnce(t);
                if (r.StartsWith("skipped: user", StringComparison.Ordinal)) return;   // retry soon, do not queue more
            }
        }

        private uint Idle()
        {
            Func<uint> h = IdleHook;
            return h != null ? h() : AntiIdleClicker.IdleMs();
        }

        /// <summary>One keep-alive pass for a target. Returns the result text (also stored in the stats).</summary>
        public string RunOnce(KeepAliveTarget t)
        {
            string result;
            try
            {
                result = Run(t);
            }
            catch (Exception ex)
            {
                result = "error: " + ex.GetType().Name;
            }
            int interval = IntervalMinutes;
            lock (_lock)
            {
                KeepAliveStats st;
                if (_stats.TryGetValue(t.Pid, out st))
                {
                    st.LastResult = result;
                    if (result == "sent") st.LastKeepAliveUtc = DateTime.UtcNow;
                }
                _due[t.Pid] = result.StartsWith("skipped: user", StringComparison.Ordinal)
                    ? DateTime.UtcNow.AddMilliseconds(ActiveRetryMs)
                    : DateTime.UtcNow.AddMinutes(interval).AddMilliseconds(StaggerMs * (t.Id % 5));
            }
            return result;
        }

        private string Run(KeepAliveTarget t)
        {
            IntPtr hwnd = AntiIdleClicker.FindMainWindow(t.Pid);
            if (hwnd == IntPtr.Zero && WiggleHook == null) return "skipped: not running";

            // The user working in this very window already counts as input.
            if (WiggleHook == null && InputSim.NativeWin.PidOf(InputSim.NativeWin.GetForegroundWindow()) == t.Pid && Idle() < 60000)
            {
                InputSim.RecordConfirmed(t.Pid);
                return "skipped: you are using this window";
            }
            if (Idle() < UserActiveSkipMs) return "skipped: user active";

            Func<bool> abort = delegate { return false; };   // our own input resets the idle timer, so only checked up front
            ClickReport rep;
            Func<IntPtr, bool, ClickReport> hook = WiggleHook;
            if (hook != null) rep = hook(hwnd, TapKey);
            else rep = InputSim.Wiggle(hwnd, TapKey, abort, null);

            if (rep.Delivered)
            {
                if (hook != null) InputSim.RecordConfirmed(t.Pid);
                Raise("Keep-alive sent to " + (string.IsNullOrEmpty(t.Label) ? "#" + t.Id : t.Label) + " via " + InputSim.TechLabel(rep.TechUsed));
                return "sent";
            }
            if (rep.ErrorCode == "user-active") return "skipped: user active";
            Raise("Keep-alive FAILED for " + (string.IsNullOrEmpty(t.Label) ? "#" + t.Id : t.Label) + ": " + rep.Error);
            InputDiagnostics.WriteFailureEvidence("keepalive", "Keep-alive failed", hwnd, rep, null);
            return "failed: " + rep.Error;
        }

        private void Raise(string message)
        {
            Action<string> h = Log;
            if (h == null) return;
            try { h(message); } catch (Exception) { }
        }
    }
}
