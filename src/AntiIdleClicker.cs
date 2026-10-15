// AntiIdleClicker.cs
//
// Keeps Roblox sessions from timing out by clicking the green "CASH OUT" button
// in each tracked instance when it is visible, and optionally nudging the cursor.
// When no CASH OUT button is visible but a trade request or party invitation is,
// its red "Decline" / "Close" button (left of the green Accept / View) is clicked
// to dismiss it. The green Accept / View button is never clicked.
//
// PUBLIC API (namespace RbxMulti) - for the UI team
// ---------------------------------------------------
// public sealed class AntiIdleStats
//     int Clicks;               // successful CASH OUT clicks
//     DateTime? LastClickUtc;   // UTC time of last successful click
//     string LastResult;        // "clicked", "button not found", "nudged",
//                               // "skipped: user active", "skipped: not running",
//                               // "skipped: minimised", "skipped: capture failed",
//                               // "skipped: user became active", "skipped: hospital route running",
//                               // "dismissed invite", "dismissed invite and clicked CASH OUT",
//                               // "error: <ExceptionType>"
//     DateTime? LastCheckUtc;   // UTC time of last detection capture
//
// public sealed class AntiIdleClicker : IDisposable
//     AntiIdleClicker()
//         Creates an idle clicker. Nothing runs until Start().
//     event Action<string> Log
//         Raised from any thread. Messages contain only the instance id,
//         e.g. "[AntiIdle] #2 clicked CASH OUT". Never account names/tickets/titles.
//     bool Enabled(int instanceId)
//         True when the instance has an enabled record.
//     void Configure(int instanceId, int pid, bool enabled, int intervalSeconds,
//                    bool nudge, int nudgeMinutes)
//         Creates or updates the per-instance record (thread-safe). A changed pid
//         (relaunch) is accepted. intervalSeconds is clamped to 15..3600 (<=0 -> 45).
//         nudgeMinutes is clamped to 1..120 (<=0 -> 5).
//         Defaults for a new record: enabled=false, interval 45 s, nudge=false, 5 min.
//     void Remove(int instanceId)
//         Forgets the instance record and its stats.
//     AntiIdleStats GetStats(int instanceId)
//         Returns a snapshot copy (never null). Zeroed stats for unknown ids.
//     static IntPtr FindMainWindow(int pid)
//         Largest visible top-level window of the pid, preferring windows whose
//         title or class contains "Roblox". Returns IntPtr.Zero when none.
//         A minimised window is returned as-is (the caller reports "skipped: minimised").
//     bool ClickNow(int instanceId, out string result)
//         One immediate detect+click pass for the instance. Ignores the user-idle
//         skip, respects the global click lock. For a UI "Test" button.
//         Returns true when clicked; result holds the outcome text.
//     void Start()
//         Starts the single background loop (IsBackground, ticks every 1 s). Idempotent.
//     void Stop()
//         Signals the loop and joins for up to 3 s. Idempotent.
//     void Dispose()
//         Calls Stop(). Safe to call more than once.
//
// Behaviour summary
// - An enabled instance is checked when its due time passes. If the user has
//   provided input in the last 3 s, the check is skipped and retried in 3 s.
// - Otherwise: process must be alive with a visible window (else "skipped: not running"),
//   minimised windows are skipped and never restored, the window client area is
//   captured via CashOutDetector.CaptureWindow and searched via CashOutDetector.Find.
// - A found button is clicked at its centre with InputSim (see InputSim.cs): verified
//   foreground, real mouse-move events, hover, held click, then the cursor and previous
//   foreground window are restored. The click is verified by re-detecting the button;
//   in "Auto" click method the next technique is tried when nothing changed. Failures
//   write evidence (report + screenshot) via InputDiagnostics.WriteFailureEvidence.
//   The user-idle check is repeated right before the click and aborts when the user became active.
// - All clicks and nudges share one global lock, with a 400 ms gap before release.
// - Nudge (optional): when nothing was found for nudgeMinutes, the cursor is moved
//   1-2 px inside the window client centre and back. No click, no focus change.
// - Due times are staggered per instance id (id modulo 20, times 1500 ms).
// - Only Win32 window APIs, input simulation and screen capture are used. No process
//   memory access and no process termination.

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;

namespace RbxMulti
{
    /// <summary>Snapshot of per-instance anti-idle statistics.</summary>
    public sealed class AntiIdleStats
    {
        public int Clicks;
        public DateTime? LastClickUtc;
        public string LastResult;
        public DateTime? LastCheckUtc;
        public string LastMethod;   // click technique that last worked: SendInput, mouse_event or PostMessage
        public string LastError;    // specific reason of the last failed click
        public int Dismissals;      // invites and teleport requests dismissed (red Decline / Close), counted beside Clicks
        public DateTime? LastDismissUtc;
    }

    public sealed class AntiIdleClicker : IDisposable
    {
        private const int DefaultIntervalSeconds = 45;
        private const int MinIntervalSeconds = 15;
        private const int MaxIntervalSeconds = 3600;
        private const int DefaultNudgeMinutes = 5;
        private const int MinNudgeMinutes = 1;
        private const int MaxNudgeMinutes = 120;

        private const uint UserActiveSkipMs = 3000;
        private const uint UserActiveAbortMs = 1500;
        private const int ActiveRetryMs = 3000;
        private const int ClickSettleMs = 40;
        private const int ClickGapMs = 400;
        private const int NudgeSettleMs = 30;
        private const int StaggerStepMs = 1500;
        private const int StaggerSlots = 20;
        private const int TickMs = 1000;
        private const int JoinTimeoutMs = 3000;

        // One global lock shared by all instances: only one click or nudge at a time.
        internal static readonly object ClickLock = new object();   // also taken by TeleportService

        // Set by HospitalRoute while it runs (its own flag: ExclusiveBusy is shared with Teleport and CASH OUT).
        internal static volatile bool HospitalRouteActive;

        // Advanced option "Pause auto-click while the hospital route runs" (default off). Off: auto-click keeps running.
        public static volatile bool PauseWhileHospital;

        /// <summary>True when a pass must wait because the hospital route is running and the option is on.</summary>
        public static bool ShouldPauseForHospital(bool optionOn, bool routeActive)
        {
            return optionOn && routeActive;
        }

        /// <summary>Settings value written for the option ("true" / "false") and read back by ParsePauseWhileHospital.</summary>
        public static string PauseWhileHospitalSetting(bool on)
        {
            return on ? "true" : "false";
        }

        /// <summary>Reads the option from its settings value. Missing or anything but "true" means off.</summary>
        public static bool ParsePauseWhileHospital(string value)
        {
            return string.Equals(value, "true", StringComparison.OrdinalIgnoreCase);
        }

        // Dismiss switches (default on): red Decline of trade requests / red Close of party invitations.
        public static volatile bool DeclineTrades = true;
        public static volatile bool DeclineInvites = true;

        /// <summary>Reads a settings switch ("true" / "false", case-insensitive, trimmed). Anything else, including null, gives fallback.</summary>
        public static bool ParseSwitch(string value, bool fallback)
        {
            if (value == null)
            {
                return fallback;
            }
            string t = value.Trim();
            if (string.Equals(t, "true", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
            if (string.Equals(t, "false", StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }
            return fallback;
        }

        /// <summary>Settings value for a switch: "true" or "false".</summary>
        public static string SwitchSetting(bool on)
        {
            return on ? "true" : "false";
        }

        /// <summary>True when a pass result means "look again in ActiveRetryMs" rather than at the normal interval.</summary>
        internal static bool RetrySoon(string result)
        {
            return result == "dismissed invite"
                || result == "dismissed invite and clicked CASH OUT"
                || result == "skipped: another test ran"
                || result == "skipped: hospital route running";
        }

        private readonly object _stateLock = new object();
        private readonly object _threadLock = new object();
        private readonly Dictionary<int, InstanceState> _states = new Dictionary<int, InstanceState>();
        private ManualResetEvent _stop;
        private Thread _thread;
        private bool _disposed;

        /// <summary>Writes a line to the log (any thread).</summary>
        public event Action<string> Log;

        public AntiIdleClicker()
        {
        }

        private sealed class InstanceState
        {
            public int Id;
            public int Pid;
            public bool Enabled;
            public int IntervalSeconds;
            public bool Nudge;
            public int NudgeMinutes;
            public DateTime NextDueUtc;
            public DateTime LastFoundUtc;
            public readonly AntiIdleStats Stats = new AntiIdleStats();
        }

        private sealed class WindowCandidate
        {
            public IntPtr Hwnd;
            public int Area;
            public bool Named;
        }

        public bool Enabled(int instanceId)
        {
            lock (_stateLock)
            {
                InstanceState st;
                return _states.TryGetValue(instanceId, out st) && st.Enabled;
            }
        }

        public void Configure(int instanceId, int pid, bool enabled, int intervalSeconds, bool nudge, int nudgeMinutes)
        {
            int interval = intervalSeconds <= 0 ? DefaultIntervalSeconds : Clamp(intervalSeconds, MinIntervalSeconds, MaxIntervalSeconds);
            int nudgeMin = nudgeMinutes <= 0 ? DefaultNudgeMinutes : Clamp(nudgeMinutes, MinNudgeMinutes, MaxNudgeMinutes);
            DateTime now = DateTime.UtcNow;

            lock (_stateLock)
            {
                InstanceState st;
                if (!_states.TryGetValue(instanceId, out st))
                {
                    st = new InstanceState();
                    st.Id = instanceId;
                    st.LastFoundUtc = now;
                    st.NextDueUtc = now.AddMilliseconds(StaggerMs(instanceId));
                    _states[instanceId] = st;
                }
                if (st.Pid != pid)
                {
                    st.Pid = pid;
                    st.LastFoundUtc = now;
                }
                st.Enabled = enabled;
                st.IntervalSeconds = interval;
                st.Nudge = nudge;
                st.NudgeMinutes = nudgeMin;
            }
        }

        public void Remove(int instanceId)
        {
            lock (_stateLock)
            {
                _states.Remove(instanceId);
            }
        }

        /// <summary>A manual CASH OUT click (card button): counts like an auto click in the card statistics.</summary>
        public void RecordManualClick(int instanceId, string method, bool ok, string error)
        {
            lock (_stateLock)
            {
                InstanceState st;
                if (!_states.TryGetValue(instanceId, out st)) return;
                if (ok)
                {
                    st.Stats.Clicks++;
                    st.Stats.LastClickUtc = DateTime.UtcNow;
                    st.Stats.LastMethod = method;
                    st.LastFoundUtc = DateTime.UtcNow;
                    st.Stats.LastError = null;
                }
                else if (!string.IsNullOrEmpty(error))
                {
                    st.Stats.LastError = error;
                }
            }
        }

        public AntiIdleStats GetStats(int instanceId)
        {
            AntiIdleStats copy = new AntiIdleStats();
            lock (_stateLock)
            {
                InstanceState st;
                if (_states.TryGetValue(instanceId, out st))
                {
                    copy.Clicks = st.Stats.Clicks;
                    copy.LastClickUtc = st.Stats.LastClickUtc;
                    copy.LastResult = st.Stats.LastResult;
                    copy.LastCheckUtc = st.Stats.LastCheckUtc;
                    copy.LastMethod = st.Stats.LastMethod;
                    copy.LastError = st.Stats.LastError;
                    copy.Dismissals = st.Stats.Dismissals;
                    copy.LastDismissUtc = st.Stats.LastDismissUtc;
                }
            }
            return copy;
        }

        /// <summary>
        /// Largest visible top-level window of the pid, preferring windows whose title or class
        /// mentions Roblox. Returns IntPtr.Zero when there is none.
        /// </summary>
        public static IntPtr FindMainWindow(int pid)
        {
            if (pid <= 0)
            {
                return IntPtr.Zero;
            }

            List<WindowCandidate> found = new List<WindowCandidate>();
            AntiIdleNative.EnumWindowsProc callback = delegate(IntPtr hwnd, IntPtr lParam)
            {
                try
                {
                    uint wpid;
                    AntiIdleNative.GetWindowThreadProcessId(hwnd, out wpid);
                    if (wpid != (uint)pid || !AntiIdleNative.IsWindowVisible(hwnd))
                    {
                        return true;
                    }
                    AntiIdleNative.NativeRect r;
                    if (!AntiIdleNative.GetWindowRect(hwnd, out r))
                    {
                        return true;
                    }
                    int area = (r.Right - r.Left) * (r.Bottom - r.Top);
                    if (area <= 0)
                    {
                        return true;
                    }
                    bool named = Mentions(GetWindowTitle(hwnd)) || Mentions(GetWindowClass(hwnd));
                    found.Add(new WindowCandidate { Hwnd = hwnd, Area = area, Named = named });
                }
                catch (Exception)
                {
                }
                return true;
            };
            AntiIdleNative.EnumWindows(callback, IntPtr.Zero);
            GC.KeepAlive(callback);

            IntPtr best = IntPtr.Zero;
            int bestArea = 0;
            bool bestNamed = false;
            foreach (WindowCandidate c in found)
            {
                bool take;
                if (best == IntPtr.Zero)
                {
                    take = true;
                }
                else if (c.Named != bestNamed)
                {
                    take = c.Named;
                }
                else
                {
                    take = c.Area > bestArea;
                }
                if (take)
                {
                    best = c.Hwnd;
                    bestArea = c.Area;
                    bestNamed = c.Named;
                }
            }
            return best;
        }

        public bool ClickNow(int instanceId, out string result)
        {
            InstanceState st = null;
            lock (_stateLock)
            {
                _states.TryGetValue(instanceId, out st);
            }
            if (st == null)
            {
                result = "not configured";
                return false;
            }

            try
            {
                result = RunPass(st, false, false, DateTime.UtcNow);
            }
            catch (Exception ex)
            {
                result = "error: " + ex.GetType().Name;
            }
            SetResult(st, result);
            return result == "clicked";
        }

        public void Start()
        {
            lock (_threadLock)
            {
                if (_disposed || _thread != null)
                {
                    return;
                }
                ManualResetEvent stop = new ManualResetEvent(false);
                _stop = stop;
                Thread t = new Thread(delegate() { RunLoop(stop); });
                t.IsBackground = true;
                t.Name = "AntiIdleClicker";
                _thread = t;
                t.Start();
            }
        }

        public void Stop()
        {
            Thread t;
            ManualResetEvent stop;
            lock (_threadLock)
            {
                t = _thread;
                stop = _stop;
                _thread = null;
                _stop = null;
            }
            if (t == null)
            {
                return;
            }
            try
            {
                stop.Set();
            }
            catch (Exception)
            {
            }
            if (t != Thread.CurrentThread)
            {
                t.Join(JoinTimeoutMs);
            }
        }

        public void Dispose()
        {
            lock (_threadLock)
            {
                if (_disposed)
                {
                    return;
                }
                _disposed = true;
            }
            Stop();
        }

        // ---------------------------------------------------------------------
        // Loop
        // ---------------------------------------------------------------------

        private void RunLoop(ManualResetEvent stop)
        {
            while (true)
            {
                bool signalled;
                try
                {
                    signalled = stop.WaitOne(TickMs);
                }
                catch (Exception)
                {
                    return;
                }
                if (signalled)
                {
                    return;
                }
                try
                {
                    TickAll();
                }
                catch (Exception)
                {
                }
            }
        }

        private void TickAll()
        {
            List<InstanceState> due = new List<InstanceState>();
            DateTime now = DateTime.UtcNow;
            lock (_stateLock)
            {
                foreach (InstanceState st in _states.Values)
                {
                    if (st.Enabled && st.NextDueUtc <= now)
                    {
                        due.Add(st);
                    }
                }
            }
            foreach (InstanceState st in due)
            {
                TickInstance(st);
            }
        }

        private void TickInstance(InstanceState st)
        {
            try
            {
                if (IdleMs() < UserActiveSkipMs)
                {
                    SetResult(st, "skipped: user active");
                    SetDue(st, DateTime.UtcNow.AddMilliseconds(ActiveRetryMs));
                    return;
                }

                string result = RunPass(st, true, true, DateTime.UtcNow);
                SetResult(st, result);
                // A dismissed invite may be followed by another one queued behind it: look again soon.
                // A pass that waited for another test or the hospital route is also looked at again soon.
                SetDue(st, RetrySoon(result) ? DateTime.UtcNow.AddMilliseconds(ActiveRetryMs) : NextDue(st));
            }
            catch (Exception ex)
            {
                SetResult(st, "error: " + ex.GetType().Name);
                SetDue(st, NextDue(st));
            }
        }

        /// <summary>One detect (and click or nudge) pass. Returns the result text.</summary>
        private string RunPass(InstanceState st, bool checkIdle, bool allowNudge, DateTime now)
        {
            // A manual or hospital test may start (and finish) while this pass waits for the mouse: the screen is then stale.
            long guard = TeleportService.ExclusiveGeneration;
            int pid;
            bool nudge;
            int nudgeMinutes;
            DateTime lastFound;
            lock (_stateLock)
            {
                pid = st.Pid;
                nudge = st.Nudge;
                nudgeMinutes = st.NudgeMinutes;
                lastFound = st.LastFoundUtc;
            }

            // Advanced option: no click, nudge or capture while the hospital route runs (checked again later).
            if (ShouldPauseForHospital(PauseWhileHospital, HospitalRouteActive))
            {
                return "skipped: hospital route running";
            }

            if (!IsProcessAlive(pid))
            {
                return "skipped: not running";
            }

            IntPtr hwnd = FindMainWindow(pid);
            if (hwnd == IntPtr.Zero)
            {
                return "skipped: not running";
            }

            if (AntiIdleNative.IsIconic(hwnd))
            {
                return "skipped: minimised";
            }

            string method;
            Bitmap bmp = CashOutDetector.CaptureWindow(hwnd, out method);
            if (bmp == null)
            {
                return "skipped: capture failed";
            }

            Rectangle? found = null;
            Rectangle? invite = null;
            try
            {
                found = CashOutDetector.Find(bmp);
                invite = InviteDetector.Find(bmp, DeclineTrades, DeclineInvites);
            }
            finally
            {
                bmp.Dispose();
            }

            lock (_stateLock)
            {
                st.Stats.LastCheckUtc = now;
            }

            string[] steps = PlanPass(invite.HasValue, found.HasValue);
            if (steps.Length > 0)
            {
                return RunSteps(st, hwnd, steps, found, invite, checkIdle, guard);
            }

            if (allowNudge && nudge && (now - lastFound).TotalMinutes >= nudgeMinutes)
            {
                string nudgeResult;
                if (Nudge(hwnd, checkIdle, out nudgeResult))
                {
                    lock (_stateLock)
                    {
                        st.LastFoundUtc = DateTime.UtcNow;
                    }
                    return "nudged";
                }
                return nudgeResult;
            }

            return "button not found";
        }

        /// <summary>Order of the steps for one pass: "decline" first (when a request is on screen), "cashout" last.</summary>
        internal static string[] PlanPass(bool requestOnScreen, bool cashOutOnScreen)
        {
            List<string> steps = new List<string>();
            if (requestOnScreen)
            {
                steps.Add("decline");
            }
            if (cashOutOnScreen)
            {
                steps.Add("cashout");
            }
            return steps.ToArray();
        }

        /// <summary>
        /// Runs the planned steps in order. After a dismiss the window is captured again and CASH OUT is clicked only when
        /// it is still there. Returns the result text.
        /// </summary>
        private string RunSteps(InstanceState st, IntPtr hwnd, string[] steps, Rectangle? found, Rectangle? invite, bool checkIdle, long guard)
        {
            bool declined = false;
            foreach (string step in steps)
            {
                if (step == "decline")
                {
                    string dismiss = DismissInvite(st, hwnd, invite.Value, checkIdle, guard);
                    if (dismiss != "dismissed invite")
                    {
                        return dismiss;
                    }
                    declined = true;
                }
                else if (step == "cashout")
                {
                    Rectangle? button = found;
                    if (declined)
                    {
                        // The dismiss changed the screen: look for CASH OUT again before clicking.
                        button = FindCashOut(hwnd);
                        if (!button.HasValue)
                        {
                            return "dismissed invite";
                        }
                    }
                    string cash = CashOutStep(st, hwnd, button.Value, checkIdle, guard);
                    if (declined && cash == "clicked")
                    {
                        return "dismissed invite and clicked CASH OUT";
                    }
                    return cash;
                }
            }
            return "dismissed invite";
        }

        /// <summary>Captures the window again and returns the CASH OUT button, or null when it is not there (or no capture).</summary>
        private static Rectangle? FindCashOut(IntPtr hwnd)
        {
            string method;
            Bitmap bmp = CashOutDetector.CaptureWindow(hwnd, out method);
            if (bmp == null)
            {
                return null;
            }
            try
            {
                return CashOutDetector.Find(bmp);
            }
            finally
            {
                bmp.Dispose();
            }
        }

        /// <summary>Clicks the CASH OUT button and records the outcome. Returns "clicked" or the failure text.</summary>
        private string CashOutStep(InstanceState st, IntPtr hwnd, Rectangle r, bool checkIdle, long guard)
        {
            ClickReport rep = ClickAt(hwnd, r, checkIdle, guard);
            if (rep.ErrorCode == "superseded")
            {
                return "skipped: another test ran";
            }
            if (rep.Ok)
            {
                lock (_stateLock)
                {
                    st.Stats.Clicks++;
                    st.Stats.LastClickUtc = DateTime.UtcNow;
                    st.Stats.LastMethod = InputSim.TechLabel(rep.TechUsed);
                    st.LastFoundUtc = DateTime.UtcNow;
                }
                CashOutHistory.Add(st.Id, new CashOutEvent { Time = DateTime.Now, Kind = CashOutKind.Success, Method = InputSim.TechLabel(rep.TechUsed), ClickPoint = new Point(rep.ClientX, rep.ClientY), Manual = false });
                Raise("[AntiIdle] #" + st.Id + " clicked CASH OUT via " + InputSim.TechLabel(rep.TechUsed) + " (" + rep.AttemptsText() + ")");
                return "clicked";
            }
            if (rep.ErrorCode == "user-active")
            {
                return "skipped: user became active";
            }
            string why = rep.ErrorCode == "no-change" ? "click did not register" : "click failed";
            CashOutHistory.Add(st.Id, new CashOutEvent { Time = DateTime.Now, Kind = CashOutKind.Failed, Message = why, ClickPoint = new Point(rep.ClientX, rep.ClientY), Manual = false });
            Raise("[AntiIdle] #" + st.Id + " " + why + ": " + rep.Error);
            string folder = InputDiagnostics.WriteFailureEvidence("cashout", "CASH OUT " + why, hwnd, rep, r);
            if (folder != null)
            {
                Raise("[AntiIdle] #" + st.Id + " evidence saved to " + folder);
            }
            lock (_stateLock)
            {
                st.Stats.LastError = rep.Error;
            }
            return why;
        }

        /// <summary>
        /// Dismisses a trade request or party invitation by clicking its red button on the left ("Decline" / "Close").
        /// The green Accept / View button on the right is never a click target. Verified by the red button being gone.
        /// </summary>
        private string DismissInvite(InstanceState st, IntPtr hwnd, Rectangle button, bool checkIdle, long guard)
        {
            Point cp = InviteDetector.ClickPoint(button);
            ClickOptions opt = new ClickOptions();
            opt.Verify = delegate { return InviteGone(hwnd, button); };
            if (checkIdle)
            {
                opt.AbortIf = delegate { return IdleMs() < UserActiveAbortMs; };
            }
            ClickReport rep = GuardedClick(hwnd, cp.X, cp.Y, opt, guard);
            if (rep.ErrorCode == "superseded")
            {
                return "skipped: another test ran";
            }
            lock (ClickLock)
            {
                Thread.Sleep(ClickGapMs);
            }

            if (rep.Ok)
            {
                lock (_stateLock)
                {
                    st.Stats.Dismissals++;
                    st.Stats.LastDismissUtc = DateTime.UtcNow;
                    st.Stats.LastError = null;
                }
                Raise("[AntiIdle] #" + st.Id + " dismissed an invite");
                return "dismissed invite";
            }
            if (rep.ErrorCode == "user-active")
            {
                return "skipped: user became active";
            }
            Raise("[AntiIdle] #" + st.Id + " invite dismiss did not register: " + rep.Error);
            string folder = InputDiagnostics.WriteFailureEvidence("invite", "invite dismiss did not register", hwnd, rep, button);
            if (folder != null)
            {
                Raise("[AntiIdle] #" + st.Id + " evidence saved to " + folder);
            }
            lock (_stateLock)
            {
                st.Stats.LastError = rep.Error;
            }
            return "invite dismiss failed";
        }

        // True when the red button is no longer at the clicked spot. A failed capture proves nothing, so it counts as not gone.
        private static bool InviteGone(IntPtr hwnd, Rectangle button)
        {
            string method;
            Bitmap bmp = CashOutDetector.CaptureWindow(hwnd, out method);
            if (bmp == null)
            {
                return false;
            }
            try
            {
                Rectangle? again = InviteDetector.Find(bmp);
                if (!again.HasValue)
                {
                    return true;
                }
                Rectangle a = again.Value;
                int dx = Math.Abs((a.X + a.Width / 2) - (button.X + button.Width / 2));
                int dy = Math.Abs((a.Y + a.Height / 2) - (button.Y + button.Height / 2));
                return dx > button.Width / 2 || dy > button.Height / 2;
            }
            finally
            {
                bmp.Dispose();
            }
        }

        /// <summary>
        /// Clicks the client point with InputSim (verified focus, real mouse movement, hover, held click) and checks that the
        /// CASH OUT button at that spot is really gone or moved afterwards. Falls back to the next click method (Auto).
        /// </summary>
        /// <summary>Where CASH OUT is clicked: the centre of the detected green button (client pixels). Tests check it lies on green.</summary>
        public static Point CashOutClickPoint(Rectangle button)
        {
            return new Point(button.X + button.Width / 2, button.Y + button.Height / 2);
        }

        private static ClickReport ClickAt(IntPtr hwnd, Rectangle button, bool checkIdle, long guard)
        {
            Point cp = CashOutClickPoint(button);
            int cx = cp.X;
            int cy = cp.Y;
            ClickOptions opt = new ClickOptions();
            opt.Verify = delegate { return CashOutGone(hwnd, button); };
            if (checkIdle)
            {
                opt.AbortIf = delegate { return IdleMs() < UserActiveAbortMs; };
            }
            ClickReport rep = GuardedClick(hwnd, cx, cy, opt, guard);
            lock (ClickLock)
            {
                Thread.Sleep(ClickGapMs);
            }
            return rep;
        }

        /// <summary>
        /// Clicks under ClickLock (held across the check, so a route cannot start between them), unless a manual or hospital
        /// test ran or is running since the pass began: then nothing is clicked and the report says "superseded".
        /// </summary>
        internal static ClickReport GuardedClick(IntPtr hwnd, int cx, int cy, ClickOptions opt, long guard)
        {
            lock (ClickLock)
            {
                if (TeleportService.ExclusiveBusy || TeleportService.ExclusiveGeneration != guard)
                {
                    ClickReport skipped = new ClickReport();
                    skipped.ErrorCode = "superseded";
                    skipped.Error = "another test ran while waiting for the mouse";
                    return skipped;
                }
                return InputSim.ClickClient(hwnd, cx, cy, opt);
            }
        }

        // True when the green button is no longer at the clicked spot (or the window cannot be captured, which proves nothing).
        private static bool CashOutGone(IntPtr hwnd, Rectangle button)
        {
            bool unknown;
            return CashOutGone(hwnd, button, out unknown);
        }

        // unknown = true when the window could not be captured (the "gone" answer then proves nothing).
        internal static bool CashOutGone(IntPtr hwnd, Rectangle button, out bool unknown)
        {
            unknown = false;
            string method;
            Bitmap bmp = CashOutDetector.CaptureWindow(hwnd, out method);
            if (bmp == null)
            {
                unknown = true;
                return true;
            }
            try
            {
                Rectangle? again = CashOutDetector.Find(bmp);
                if (!again.HasValue)
                {
                    return true;
                }
                Rectangle a = again.Value;
                int dx = Math.Abs((a.X + a.Width / 2) - (button.X + button.Width / 2));
                int dy = Math.Abs((a.Y + a.Height / 2) - (button.Y + button.Height / 2));
                return dx > button.Width / 2 || dy > button.Height / 2;
            }
            finally
            {
                bmp.Dispose();
            }
        }
        /// <summary>Moves the cursor 1-2 px inside the client centre and back. No click, no focus change.</summary>
        private static bool Nudge(IntPtr hwnd, bool checkIdle, out string result)
        {
            result = null;
            lock (ClickLock)
            {
                AntiIdleNative.POINT saved = new AntiIdleNative.POINT();
                bool moved = false;
                bool ok = false;
                try
                {
                    if (checkIdle && IdleMs() < UserActiveAbortMs)
                    {
                        result = "skipped: user became active";
                    }
                    else
                    {
                        AntiIdleNative.NativeRect cr;
                        AntiIdleNative.GetClientRect(hwnd, out cr);
                        AntiIdleNative.POINT c = new AntiIdleNative.POINT();
                        c.X = (cr.Right - cr.Left) / 2;
                        c.Y = (cr.Bottom - cr.Top) / 2;
                        AntiIdleNative.ClientToScreen(hwnd, ref c);

                        AntiIdleNative.GetCursorPos(out saved);
                        moved = true;
                        AntiIdleNative.SetCursorPos(c.X + 2, c.Y + 1);
                        Thread.Sleep(NudgeSettleMs);
                        ok = true;
                    }
                }
                catch (Exception ex)
                {
                    result = "error: " + ex.GetType().Name;
                }
                finally
                {
                    if (moved)
                    {
                        AntiIdleNative.SetCursorPos(saved.X, saved.Y);
                    }
                }

                if (!ok)
                {
                    return false;
                }
                Thread.Sleep(ClickGapMs);
                result = "nudged";
                return true;
            }
        }

        /// <summary>Brings the window to the foreground (verified, with fall-backs). Returns true when it is in front.</summary>
        internal static bool BringToFront(IntPtr hwnd)
        {
            return InputSim.EnsureForeground(hwnd, null);
        }
        /// <summary>Milliseconds since the last user input. Returns 0 when unknown (treated as active).</summary>
        internal static uint IdleMs()
        {
            AntiIdleNative.LASTINPUTINFO info = new AntiIdleNative.LASTINPUTINFO();
            info.cbSize = (uint)Marshal.SizeOf(typeof(AntiIdleNative.LASTINPUTINFO));
            if (!AntiIdleNative.GetLastInputInfo(ref info))
            {
                return 0;
            }
            uint now = AntiIdleNative.GetTickCount();
            return unchecked(now - info.dwTime);
        }

        private static bool IsProcessAlive(int pid)
        {
            if (pid <= 0)
            {
                return false;
            }
            try
            {
                using (Process p = Process.GetProcessById(pid))
                {
                    return !p.HasExited;
                }
            }
            catch (Exception)
            {
                return false;
            }
        }

        private static string GetWindowTitle(IntPtr hwnd)
        {
            int len = AntiIdleNative.GetWindowTextLength(hwnd);
            if (len <= 0)
            {
                return string.Empty;
            }
            StringBuilder sb = new StringBuilder(len + 1);
            AntiIdleNative.GetWindowText(hwnd, sb, sb.Capacity);
            return sb.ToString();
        }

        private static string GetWindowClass(IntPtr hwnd)
        {
            StringBuilder sb = new StringBuilder(256);
            AntiIdleNative.GetClassName(hwnd, sb, sb.Capacity);
            return sb.ToString();
        }

        private static bool Mentions(string s)
        {
            return s != null && s.IndexOf("roblox", StringComparison.OrdinalIgnoreCase) >= 0;
        }

        private static int StaggerMs(int id)
        {
            int slot = id % StaggerSlots;
            if (slot < 0)
            {
                slot = -slot;
            }
            return slot * StaggerStepMs;
        }

        private static int Clamp(int value, int min, int max)
        {
            if (value < min)
            {
                return min;
            }
            if (value > max)
            {
                return max;
            }
            return value;
        }

        private DateTime NextDue(InstanceState st)
        {
            int interval;
            lock (_stateLock)
            {
                interval = st.IntervalSeconds;
            }
            return DateTime.UtcNow.AddSeconds(interval).AddMilliseconds(StaggerMs(st.Id));
        }

        private void SetDue(InstanceState st, DateTime due)
        {
            lock (_stateLock)
            {
                st.NextDueUtc = due;
            }
        }

        private void SetResult(InstanceState st, string result)
        {
            lock (_stateLock)
            {
                st.Stats.LastResult = result;
            }
        }

        private void Raise(string message)
        {
            Action<string> handler = Log;
            if (handler == null)
            {
                return;
            }
            try
            {
                handler(message);
            }
            catch (Exception)
            {
            }
        }

        private static class AntiIdleNative
        {
            public const uint INPUT_MOUSE = 0;
            public const uint MOUSEEVENTF_LEFTDOWN = 0x0002;
            public const uint MOUSEEVENTF_LEFTUP = 0x0004;
            public const int SW_RESTORE = 9;

            public delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

            [StructLayout(LayoutKind.Sequential)]
            public struct POINT
            {
                public int X;
                public int Y;
            }

            [StructLayout(LayoutKind.Sequential)]
            public struct NativeRect
            {
                public int Left;
                public int Top;
                public int Right;
                public int Bottom;
            }

            [StructLayout(LayoutKind.Sequential)]
            public struct LASTINPUTINFO
            {
                public uint cbSize;
                public uint dwTime;
            }

            // Sequential layout: MOUSEINPUT contains an IntPtr, so the CLR aligns it to 8 on x64
            // (offset 8) and to 4 on x86, which matches the Win32 INPUT union layout.
            [StructLayout(LayoutKind.Sequential)]
            public struct MOUSEINPUT
            {
                public int dx;
                public int dy;
                public uint mouseData;
                public uint dwFlags;
                public uint time;
                public IntPtr dwExtraInfo;
            }

            [StructLayout(LayoutKind.Sequential)]
            public struct INPUT
            {
                public uint type;
                public MOUSEINPUT mi;
            }

            [DllImport("user32.dll")]
            public static extern bool GetLastInputInfo(ref LASTINPUTINFO plii);

            [DllImport("kernel32.dll")]
            public static extern uint GetTickCount();

            [DllImport("user32.dll")]
            public static extern bool EnumWindows(EnumWindowsProc lpEnumFunc, IntPtr lParam);

            [DllImport("user32.dll")]
            public static extern bool IsWindowVisible(IntPtr hWnd);

            [DllImport("user32.dll")]
            public static extern bool IsWindow(IntPtr hWnd);

            [DllImport("user32.dll")]
            public static extern bool IsIconic(IntPtr hWnd);

            [DllImport("user32.dll")]
            public static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);

            [DllImport("user32.dll", CharSet = CharSet.Unicode)]
            public static extern int GetWindowTextLength(IntPtr hWnd);

            [DllImport("user32.dll", CharSet = CharSet.Unicode)]
            public static extern int GetWindowText(IntPtr hWnd, StringBuilder lpString, int nMaxCount);

            [DllImport("user32.dll", CharSet = CharSet.Unicode)]
            public static extern int GetClassName(IntPtr hWnd, StringBuilder lpClassName, int nMaxCount);

            [DllImport("user32.dll")]
            public static extern bool GetWindowRect(IntPtr hWnd, out NativeRect lpRect);

            [DllImport("user32.dll")]
            public static extern bool GetClientRect(IntPtr hWnd, out NativeRect lpRect);

            [DllImport("user32.dll")]
            public static extern bool ClientToScreen(IntPtr hWnd, ref POINT lpPoint);

            [DllImport("user32.dll")]
            public static extern bool GetCursorPos(out POINT lpPoint);

            [DllImport("user32.dll")]
            public static extern bool SetCursorPos(int x, int y);

            [DllImport("user32.dll")]
            public static extern IntPtr GetForegroundWindow();

            [DllImport("user32.dll")]
            public static extern bool SetForegroundWindow(IntPtr hWnd);

            [DllImport("user32.dll")]
            public static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

            [DllImport("user32.dll")]
            public static extern bool AttachThreadInput(uint idAttach, uint idAttachTo, bool fAttach);

            [DllImport("kernel32.dll")]
            public static extern uint GetCurrentThreadId();

            [DllImport("user32.dll", SetLastError = true)]
            public static extern uint SendInput(uint nInputs, INPUT[] pInputs, int cbSize);
        }
    }
}
