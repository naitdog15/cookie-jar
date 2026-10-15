// HospitalRoute.cs
//
// "Go to the hospital" for one instance: backpack -> Gifts -> "+" -> Yes on "Teleport to The Gifts Display?" ->
// wait for arrival -> turn the character until the hospital marker is straight ahead -> walk forward (W) for a fixed
// time, jumping (Space) at most MaxJumps times when the scene stops changing. UI-free, C# 5.
//
// PUBLIC API (namespace RbxMulti)
//   class HospitalResult      Ok, Message, Millis
//   static HospitalRoute
//       string BlockReason(inst, resolve)           null when the route may start, else the reason text.
//       HospitalResult Run(inst, resolve, ct, log)  blocking; call from a worker thread. One at a time with Teleport and CASH OUT.
//       bool CursorDrifted(anchor, now, tolerancePx) pure drift test used by the route.
//   static HospitalAim    pure aiming controller (drag size and gain update), exposed for tests.
//   class HospitalWalk    pure walk decision (Hold / Jump / release), exposed for tests.
//
// Input: clicks go through InputSim.ClickClient. Every key and right-button event goes through InputSim.Backend
// (scan codes, not virtual keys: Roblox ignores plain virtual-key events). The turn is a right-button drag (the default
// Roblox camera rotate); MouseDrag.Hold always releases the button in a finally.
// Cursor anchor: the centre of the game client area. After every mouse action the cursor is put back on it. If the
// cursor moves away from it (the user touched the mouse), the route stops. The user's cursor position is restored at
// the very end of Run.
// Holds ClickLock for the whole route, so the auto-clicker and the keep-alive wiggle wait.
//
// Not tested live. The image side and the input/walk logic are covered by tests\HospitalTests.cs (fake input backend).

using System;
using System.Diagnostics;
using System.Drawing;
using System.Globalization;
using System.Threading;

namespace RbxMulti
{
    public sealed class HospitalResult
    {
        public bool Ok;
        public string Message;
        public long Millis;

        public HospitalResult(bool ok, string message)
        {
            Ok = ok;
            Message = message;
        }
    }

    /// <summary>
    /// Closed-loop aim: the marker's horizontal error (screen px, positive = right of centre) is driven to zero by mouse drags.
    /// slope = change of the error (screen px) per mouse px of drag. Starts at a guess and is refined from each measured step.
    /// </summary>
    public static class HospitalAim
    {
        public const double InitialSlope = -4.0;   // dragging right usually moves the scene left
        public const double MinSlope = 0.5;
        public const double MaxSlope = 20.0;
        public const int MaxDragPx = 300;
        public const int MinDragPx = 3;

        // Mouse px to drag so that the error goes to zero, given the current slope.
        public static int DragFor(double err, double slope)
        {
            if (Math.Abs(slope) < 1e-6) slope = InitialSlope;
            double dx = -err / slope;
            if (dx > MaxDragPx) dx = MaxDragPx;
            if (dx < -MaxDragPx) dx = -MaxDragPx;
            int d = (int)Math.Round(dx);
            if (d != 0 && Math.Abs(d) < MinDragPx) d = d > 0 ? MinDragPx : -MinDragPx;
            return d;
        }

        // Updates the slope from the last drag: error change over the drag. Keeps the old slope when nothing moved.
        public static double UpdateSlope(double slope, double prevErr, double err, int prevDrag)
        {
            if (prevDrag == 0) return slope;
            double change = err - prevErr;
            if (Math.Abs(change) < 1.0) return slope;
            double s = change / prevDrag;
            double mag = Math.Abs(s);
            if (mag < MinSlope) mag = MinSlope;
            if (mag > MaxSlope) mag = MaxSlope;
            return s < 0 ? -mag : mag;
        }

        public static double Tolerance(int frameWidth)
        {
            return Math.Max(6.0, 0.02 * frameWidth);
        }

        // Search while the marker is off screen: always the same direction (the camera turns a full circle, so one
        // direction reaches every heading). MaxSweeps x SweepPx covers a full turn at 0.1 degrees per mouse px.
        public const int SweepPx = 60;
        public const int MaxSweeps = 80;
    }

    internal enum WalkStep
    {
        Hold,
        Jump,
        ReleaseDone,
        ReleaseBlocked,
        ReleaseFocus,
        ReleaseDrift
    }

    /// <summary>
    /// Pure walk decision. The route calls Next on every frame poll (every WalkPollMs) and, between frames, when focus
    /// or the cursor anchor is lost. The route only executes the returned step.
    /// </summary>
    internal sealed class HospitalWalk
    {
        // One Space tap per walk. A second stall after that tap (moving or not) means blocked: W is released and the walk stops.
        public const int MaxJumps = 1;

        public int Jumps;
        public int StallCount;                 // consecutive stalled polls
        private bool _jumpedAwaitingMove;      // a jump was sent and the scene has not moved since

        public WalkStep Next(bool stalled, bool focusOk, double elapsedSeconds, bool drifted)
        {
            if (!focusOk) return WalkStep.ReleaseFocus;
            if (drifted) return WalkStep.ReleaseDrift;
            if (elapsedSeconds >= HospitalRoute.WalkSeconds) return WalkStep.ReleaseDone;
            if (!stalled)
            {
                StallCount = 0;
                _jumpedAwaitingMove = false;
                return WalkStep.Hold;
            }
            StallCount++;
            if (_jumpedAwaitingMove) return WalkStep.ReleaseBlocked;   // stalled again straight after a jump
            if (Jumps >= MaxJumps) return WalkStep.ReleaseBlocked;     // jump budget used up
            Jumps++;
            _jumpedAwaitingMove = true;
            return WalkStep.Jump;
        }
    }

    /// <summary>Everything the walk touches, so the walk can be driven by a fake backend and a scripted frame source.</summary>
    internal sealed class WalkEnv
    {
        public IInputBackend Input;
        public IntPtr Hwnd;
        public Point Anchor;
        public CancellationToken Ct;
        public Func<Bitmap> Frame;         // may return null (capture failed)
        public Func<double> Seconds;       // elapsed walk time
        public Action<string> Say;         // may be null
        public InputTrace Trace;           // every walk step and input result; created by WalkForward when null
    }

    /// <summary>Set by the route when the cursor drifted from its anchor, so the run reports it.</summary>
    internal sealed class RouteRun
    {
        public IntPtr Hwnd;
        public CancellationToken Ct;
        public Action<string> Say;
        public Point Anchor;
        public bool HasAnchor;
        public bool DriftStop;
    }

    public static class HospitalRoute
    {
        public const int MaxAimSteps = 40;
        public const int ArriveTimeoutMs = 40000;
        public const int SettleMs = 1500;
        public const double ArriveChange = 0.20;   // scene change that means "teleported"
        public const double SettledChange = 0.02;  // scene change between two frames that means "stopped moving"

        // The cursor anchor may drift by this many px before the route stops. The value is not verified at every DPI scale.
        public const int DriftTolerancePx = 6;

        // Walk forward for this long once the marker is straight ahead. PLACEHOLDER: the user has not supplied the real
        // time from the Gifts Display to the hospital yet; replace 12 with the measured value.
        public const int WalkSeconds = 12;
        public const int WalkPollMs = 500;         // one frame compare per poll
        public const int FocusPollMs = 250;        // focus and cursor checks while W is held

        private const ushort ScanW = 0x11;         // W (scan code, Roblox ignores virtual-key events)
        private const ushort ScanSpace = 0x39;     // Space (jump)

        // checkWindow = false on the card refresh (runs every tick, so no window enumeration); true before a start.
        // Evidence of the current run, kept per thread: the last captured frame and the last step text.
        [ThreadStatic] private static Bitmap _evidence;
        [ThreadStatic] private static string _lastStep;
        [ThreadStatic] private static InputTrace _walkTrace;   // the walk trace of this run (null when the walk never started)

        /// <summary>True when the cursor is further than tolerancePx from the anchor (Euclidean distance).</summary>
        public static bool CursorDrifted(Point anchor, Point now, int tolerancePx)
        {
            long dx = now.X - anchor.X;
            long dy = now.Y - anchor.Y;
            return dx * dx + dy * dy > (long)tolerancePx * tolerancePx;
        }

        /// <summary>Stops the route when cancel was asked for, or when the cursor left its anchor (the user touched the mouse).</summary>
        private static void Checkpoint(RouteRun r)
        {
            if (r.Ct.IsCancellationRequested) throw new OperationCanceledException();
            if (r.HasAnchor && CursorMoved(InputSim.Backend, r.Anchor))
            {
                r.DriftStop = true;
                throw new OperationCanceledException();
            }
        }

        private static bool CursorMoved(IInputBackend b, Point anchor)
        {
            int x, y;
            if (!b.GetCursorPos(out x, out y)) return false;
            return CursorDrifted(anchor, new Point(x, y), DriftTolerancePx);
        }

        public static string BlockReason(RobloxInstance inst, Func<RobloxInstance, IntPtr> resolve, bool checkWindow)
        {
            if (inst == null) return "No instance.";
            if (inst.StatusKind != "Running")
                return "Start this instance and wait for the green Running status first (status: " + (string.IsNullOrEmpty(inst.Status) ? "unknown" : inst.Status) + ").";
            if (checkWindow)
            {
                IntPtr h = IntPtr.Zero;
                try { h = resolve == null ? AntiIdleClicker.FindMainWindow(inst.Pid) : resolve(inst); } catch (Exception) { }
                if (h == IntPtr.Zero) return "This instance has no visible game window yet.";
            }
            return null;
        }

        public static HospitalResult Run(RobloxInstance inst, Func<RobloxInstance, IntPtr> resolve, CancellationToken ct, Action<string> log)
        {
            Stopwatch total = Stopwatch.StartNew();
            string tag = "[Hospital] " + (inst == null ? "?" : "#" + inst.Id.ToString(CultureInfo.InvariantCulture));
            _lastStep = "";
            _walkTrace = null;
            Action<string> say = delegate(string s)
            {
                _lastStep = s;
                if (log != null) { try { log(tag + " " + s); } catch (Exception) { } }
            };

            // The user's cursor position, restored at the very end of the run.
            IInputBackend B = InputSim.Backend;
            int origX = 0, origY = 0;
            bool haveOrig = false;
            try { haveOrig = B.GetCursorPos(out origX, out origY); } catch (Exception) { }

            RouteRun r = new RouteRun();
            r.Ct = ct;
            r.Say = say;
            HospitalResult res;
            try
            {
                try { res = RunCore(inst, resolve, r); }
                catch (OperationCanceledException)
                {
                    res = new HospitalResult(false, r.DriftStop ? "The mouse was moved; the route was stopped." : "Cancelled.");
                }
                catch (Exception ex) { res = new HospitalResult(false, "Unexpected error: " + ex.GetType().Name); }
            }
            finally
            {
                if (haveOrig) { try { B.SetCursorPos(origX, origY); } catch (Exception) { } }
            }
            res.Millis = total.ElapsedMilliseconds;
            say((res.Ok ? "done: " : "stopped: ") + res.Message + " in " + res.Millis + " ms");
            if (!res.Ok) SaveEvidence(inst, res);
            if (_evidence != null) { _evidence.Dispose(); _evidence = null; }
            return res;
        }

        // A failed run leaves the last captured frame and the step text in diag\hospital_<timestamp>_<instance>\ (newest 20 kept).
        // Same folder and pruning as the other diagnostics. No account names, tickets or window titles.
        private static void SaveEvidence(RobloxInstance inst, HospitalResult res)
        {
            try
            {
                string dir = System.IO.Path.Combine(InputDiagnostics.Root(),
                    "hospital_" + DateTime.Now.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture) + "_" + (inst == null ? "x" : inst.Id.ToString(CultureInfo.InvariantCulture)));
                System.IO.Directory.CreateDirectory(dir);
                string report = "Cookie Jar - hospital route stopped" + Environment.NewLine
                    + "Time: " + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture) + Environment.NewLine
                    + "Instance: " + (inst == null ? "?" : inst.Id.ToString(CultureInfo.InvariantCulture)) + Environment.NewLine
                    + "Result: " + res.Message + Environment.NewLine
                    + "Last step: " + (_lastStep ?? "") + Environment.NewLine
                    + "Time taken: " + res.Millis.ToString(CultureInfo.InvariantCulture) + " ms" + Environment.NewLine
                    + "Frame: " + (_evidence == null ? "none captured" : "last.png") + Environment.NewLine
                    + WalkReportText(_walkTrace);
                System.IO.File.WriteAllText(System.IO.Path.Combine(dir, "report.txt"), report);
                if (_evidence != null) _evidence.Save(System.IO.Path.Combine(dir, "last.png"), System.Drawing.Imaging.ImageFormat.Png);
                InputDiagnostics.Prune("hospital_", 20);
            }
            catch (Exception) { }
        }

        private static HospitalResult RunCore(RobloxInstance inst, Func<RobloxInstance, IntPtr> resolve, RouteRun r)
        {
            string block = BlockReason(inst, resolve, true);
            if (block != null) return new HospitalResult(false, block);
            if (!TeleportService.TryAcquireExclusive())
                return new HospitalResult(false, "A teleport or another test is already running (one at a time). Wait for it to finish.");
            try
            {
                // The flag is set only once this run owns the exclusive guard, so a refused second run cannot clear it.
                AntiIdleClicker.HospitalRouteActive = true;
                try
                {
                    IntPtr hwnd = resolve == null ? AntiIdleClicker.FindMainWindow(inst.Pid) : resolve(inst);
                    if (hwnd == IntPtr.Zero || !InputSim.NativeWin.IsWindow(hwnd))
                        return new HospitalResult(false, "No visible game window for this instance.");
                    r.Hwnd = hwnd;

                    bool locked = false;
                    Stopwatch wait = Stopwatch.StartNew();
                    while (!locked)
                    {
                        if (r.Ct.IsCancellationRequested) throw new OperationCanceledException();
                        locked = Monitor.TryEnter(AntiIdleClicker.ClickLock, 100);
                        if (!locked && wait.ElapsedMilliseconds > 20000)
                            return new HospitalResult(false, "Could not get the mouse: the auto-clicker was busy for 20 s.");
                    }
                    try
                    {
                        if (InputSim.NativeWin.IsIconic(hwnd))
                        {
                            InputSim.NativeWin.ShowWindow(hwnd, InputSim.NativeWin.SW_RESTORE);
                            Thread.Sleep(500);
                        }
                        // The anchor is read after the restore (an iconic window has an empty client rect).
                        Point anchor;
                        if (!ClientCentre(hwnd, out anchor)) return new HospitalResult(false, "Could not read the game window size.");
                        r.Anchor = anchor;
                        r.HasAnchor = true;
                        return Attempt(r);
                    }
                    finally
                    {
                        Monitor.Exit(AntiIdleClicker.ClickLock);
                    }
                }
                finally
                {
                    AntiIdleClicker.HospitalRouteActive = false;
                }
            }
            finally
            {
                TeleportService.ReleaseExclusive();
            }
        }

        private static HospitalResult Attempt(RouteRun r)
        {
            IntPtr hwnd = r.Hwnd;
            Action<string> say = r.Say;

            // 1. Backpack open (the button toggles, so look first).
            Bitmap bmp = Capture(hwnd);
            if (bmp == null) return Fail("Could not capture the game window (minimised or hidden).");
            if (!PanelOpen(bmp))
            {
                Rectangle? bp = HospitalDetector.FindBackpackButton(bmp);
                bmp.Dispose();
                if (!bp.HasValue) return Fail("The backpack button was not found. Is the Adopt Me world on screen?");
                say("opening the backpack");
                if (!ClickToggle(r, bp.Value, delegate { return PanelOpenNow(hwnd); }, 3000))
                    return Fail("The backpack did not open.");
                bmp = Capture(hwnd);
                if (bmp == null) return Fail("Could not capture the game window.");
            }

            // 2. Gifts must be the selected tab (the orange tile). An unselected Gifts tile is green with the same white gift
            // icon: find it by the icon and click it, then check that it turned orange.
            if (HospitalDetector.FindGiftsTabHighlighted(bmp) == null)
            {
                Rectangle? unselected = HospitalDetector.FindGiftsTabUnselected(bmp);
                bmp.Dispose();
                if (!unselected.HasValue) return Fail("The Gifts tab was not found in the backpack.");
                say("selecting the Gifts tab");
                if (!ClickCentre(r, unselected.Value, delegate { return GiftsSelectedNow(hwnd); }))
                    return Fail("The Gifts tab did not become selected.");
                bmp = Capture(hwnd);
                if (bmp == null) return Fail("Could not capture the game window.");
            }

            // 3. The "+" opens the teleport prompt.
            Rectangle? plus = HospitalDetector.FindPlusTile(bmp);
            bmp.Dispose();
            if (!plus.HasValue) return Fail("The + tile was not found in the Gifts pane.");
            say("asking for the Gifts Display teleport");
            if (!ClickCentre(r, plus.Value, delegate { return PromptNow(hwnd); }))
                return Fail("The teleport prompt did not appear.");

            // 4. Yes, only on the Gifts Display prompt (title read with OCR). Keep the frame for the arrival check.
            bmp = Capture(hwnd);
            if (bmp == null) return Fail("Could not capture the game window.");
            Rectangle? yes = HospitalDetector.FindTeleportYes(bmp);
            if (!yes.HasValue)
            {
                bmp.Dispose();
                string read = HospitalDetector.LastPromptText;
                return Fail("The \"Teleport to The Gifts Display?\" prompt was not confirmed, so nothing was clicked. OCR read: \""
                    + (read.Length == 0 ? "(no prompt text)" : read) + "\".");
            }
            // The baseline is the frame before Yes; the prompt and the backpack pane are excluded from the change measure,
            // so their closing is not mistaken for the teleport. Taking the baseline before the click means an instant
            // teleport is still seen as a change.
            Bitmap before = (Bitmap)bmp.Clone();
            Rectangle dialog = HospitalDetector.DialogRect(yes.Value);
            Rectangle pane = HospitalDetector.PaneRect(plus.Value);
            bmp.Dispose();
            say("confirming the teleport");
            if (!ClickCentre(r, yes.Value, delegate { return !PromptNow(hwnd); }))
            {
                before.Dispose();
                return Fail("The teleport prompt did not close after Yes.");
            }

            // 5. Arrival (soft): the scene changes, then settles. If no change is seen the marker search still decides.
            bool arrived = WaitForArrival(r, before, dialog, pane);
            before.Dispose();
            if (!arrived) say("no clear teleport seen within " + (ArriveTimeoutMs / 1000) + " s; looking for the marker anyway");

            // 6. Turn until the hospital marker is straight ahead, then walk.
            HospitalResult aim = Aim(r);
            if (!aim.Ok) return aim;
            say("aim finished: " + aim.Message);
            return Walk(r);
        }

        private static bool WaitForArrival(RouteRun r, Bitmap before, Rectangle dialog, Rectangle pane)
        {
            IntPtr hwnd = r.Hwnd;
            Action<string> say = r.Say;
            Stopwatch sw = Stopwatch.StartNew();
            bool moved = false;
            while (sw.ElapsedMilliseconds < ArriveTimeoutMs)
            {
                Checkpoint(r);
                Thread.Sleep(1000);
                Bitmap f = Capture(hwnd);
                if (f == null) continue;
                if (!moved)
                {
                    if (TeleportDetector.SceneDiff(before, f, dialog, pane) >= ArriveChange)
                    {
                        moved = true;
                        say("teleport started, waiting for the scene to settle");
                        f.Dispose();
                        continue;
                    }
                    // No change: the character may already be at the Gifts Display. Stop waiting once the marker is visible
                    // or after 8 s, provided the scene is still (two frames SettleMs apart match).
                    if (sw.ElapsedMilliseconds >= 8000 || HospitalDetector.FindHospitalMarker(f).HasValue)
                    {
                        Thread.Sleep(SettleMs);
                        Bitmap still = Capture(hwnd);
                        bool isStill = still != null && TeleportDetector.SceneDiff(f, still, Rectangle.Empty, Rectangle.Empty) < SettledChange;
                        if (still != null) still.Dispose();
                        if (isStill)
                        {
                            say("no teleport change seen, the scene is still: going on");
                            f.Dispose();
                            return true;
                        }
                    }
                    f.Dispose();
                    continue;
                }
                Thread.Sleep(SettleMs);
                Bitmap g = Capture(hwnd);
                if (g == null) { f.Dispose(); continue; }
                double change = TeleportDetector.SceneDiff(f, g, Rectangle.Empty, Rectangle.Empty);
                f.Dispose();
                g.Dispose();
                if (change < SettledChange) return true;
            }
            return false;
        }

        // Two budgets: searching (marker off screen, one direction, HospitalAim.MaxSweeps) and aiming (marker visible,
        // closed loop, MaxAimSteps). Losing the marker during the aim drops back to the search.
        private static HospitalResult Aim(RouteRun r)
        {
            IntPtr hwnd = r.Hwnd;
            Action<string> say = r.Say;
            double slope = HospitalAim.InitialSlope;
            double? prevErr = null;
            int prevDrag = 0;
            int aimSteps = 0;
            int sweeps = 0;

            while (aimSteps < MaxAimSteps && sweeps < HospitalAim.MaxSweeps)
            {
                Checkpoint(r);
                Bitmap bmp = Capture(hwnd);
                if (bmp == null) return Fail("Could not capture the game window while turning.");
                Rectangle? m = HospitalDetector.FindHospitalMarker(bmp);
                if (!m.HasValue)
                {
                    bmp.Dispose();
                    prevErr = null;
                    prevDrag = 0;
                    sweeps++;
                    say("hospital marker not on screen, searching (" + sweeps + "/" + HospitalAim.MaxSweeps + ")");
                    if (!MouseDrag.Hold(hwnd, HospitalAim.SweepPx, r.Anchor, r.Ct, delegate { Checkpoint(r); })) return Fail("Could not focus the game window to turn.");
                    continue;
                }
                aimSteps++;

                double width = bmp.Width;
                double err = (m.Value.X + m.Value.Width / 2.0) - width / 2.0;
                bmp.Dispose();
                if (Math.Abs(err) <= HospitalAim.Tolerance((int)width))
                {
                    return new HospitalResult(true, "facing the hospital marker (off by " + err.ToString("0", CultureInfo.InvariantCulture) + " px).");
                }

                if (prevErr.HasValue) slope = HospitalAim.UpdateSlope(slope, prevErr.Value, err, prevDrag);
                int drag = HospitalAim.DragFor(err, slope);
                say("turning " + drag + " px, marker off by " + err.ToString("0", CultureInfo.InvariantCulture) + " px");
                if (!MouseDrag.Hold(hwnd, drag, r.Anchor, r.Ct, delegate { Checkpoint(r); })) return Fail("Could not focus the game window to turn.");
                prevErr = err;
                prevDrag = drag;
            }
            if (sweeps >= HospitalAim.MaxSweeps)
                return Fail("The hospital marker was not found after a full turn.");
            return Fail("Could not face the hospital marker within " + MaxAimSteps + " turns.");
        }

        // Walk forward with W held for WalkSeconds (see WalkForward).
        private static HospitalResult Walk(RouteRun r)
        {
            IntPtr hwnd = r.Hwnd;
            Stopwatch sw = Stopwatch.StartNew();
            WalkEnv e = new WalkEnv();
            e.Input = InputSim.Backend;
            e.Hwnd = hwnd;
            e.Anchor = r.Anchor;
            e.Ct = r.Ct;
            e.Say = r.Say;
            e.Frame = delegate { return Capture(hwnd); };
            e.Seconds = delegate { return sw.Elapsed.TotalSeconds; };
            e.Trace = new InputTrace();
            _walkTrace = e.Trace;
            if (r.Say != null) r.Say("walking toward the hospital marker (" + WalkSeconds + " s)");
            return WalkForward(e);
        }

        // The walk trace as it goes into report.txt (the timestamps are ms from the start of the walk).
        internal static string WalkReportText(InputTrace t)
        {
            if (t == null) return "Walk: not started" + Environment.NewLine;
            return "Walk trace (ms from the walk start):" + Environment.NewLine + t.ToString() + Environment.NewLine;
        }

        /// <summary>
        /// Holds W for WalkSeconds. W is always released in the finally. Every WalkPollMs a frame is compared with the
        /// previous one: a stall means the character is blocked, so Space is tapped (at most MaxJumps times). Focus and
        /// the cursor anchor are checked every FocusPollMs; the focus check is a plain read (no re-focusing) while W is held.
        /// </summary>
        internal static HospitalResult WalkForward(WalkEnv e)
        {
            IInputBackend B = e.Input;
            if (e.Trace == null) e.Trace = new InputTrace();
            HospitalWalk walk = new HospitalWalk();
            uint layoutW = InputSim.NativeWin.MapVirtualKey(0x57, 0);
            uint layoutSpace = InputSim.NativeWin.MapVirtualKey(0x20, 0);
            e.Trace.Add("walk start: foreground " + InputSim.NativeWin.DescribeWindow(B.Foreground())
                + "; game window " + InputSim.NativeWin.DescribeWindow(e.Hwnd)
                + "; layout check: W is scan 0x" + layoutW.ToString("X") + (layoutW == ScanW ? " (MATCH)" : " (MISMATCH, sent 0x" + ScanW.ToString("X") + ")")
                + ", Space is scan 0x" + layoutSpace.ToString("X") + (layoutSpace == ScanSpace ? " (MATCH)" : " (MISMATCH, sent 0x" + ScanSpace.ToString("X") + ")"));
            // Before the first key-down: the one place that may bring the game forward.
            bool focus0 = B.Focus(e.Hwnd, e.Trace);
            Step(e, "focus before W: " + (focus0 ? "ok" : "FAILED"));
            if (!focus0) return Fail(WalkFocusText);

            bool held = false;
            Bitmap prev = null;
            int ticks = 0;
            int framesPerPoll = Math.Max(1, WalkPollMs / FocusPollMs);
            try
            {
                // The baseline frame is taken before W, so the first poll compares the walk with the standing character.
                prev = e.Frame();
                Step(e, prev == null ? "start frame not captured (a failed capture is never a stall)" : "start frame: " + DescribePanel(prev));

                InputSim.LastScanError = 0;
                held = true;          // set before the call: the key-up is always sent
                if (!B.KeyDown(ScanW))
                {
                    int err = InputSim.LastScanError;
                    Step(e, "W key-down REFUSED by SendInput (scan 0x" + ScanW.ToString("X") + ", GetLastError " + err + ")");
                    return Fail("The W key was refused by SendInput, so the character was not walked (GetLastError " + err
                        + "). Input may be blocked (UIPI, locked screen).");
                }
                Step(e, "W key-down sent (scan 0x" + ScanW.ToString("X") + ")");
                Step(e, "walking forward (W held)");

                while (true)
                {
                    if (e.Ct.IsCancellationRequested) throw new OperationCanceledException();
                    B.Sleep(FocusPollMs);
                    ticks++;

                    bool focusOk = HasFocus(B, e.Hwnd);
                    bool drifted = CursorMoved(B, e.Anchor);
                    if (!focusOk || drifted)
                    {
                        Step(e, "walk check at " + Secs(e.Seconds()) + " s: focus " + (focusOk ? "ok" : "LOST")
                            + ", cursor " + (drifted ? "MOVED off the anchor" : "on the anchor") + " -> stop");
                        return WalkOutcome(walk.Next(false, focusOk, e.Seconds(), drifted));
                    }
                    if (ticks % framesPerPoll != 0) continue;

                    double now = e.Seconds();
                    Bitmap cur = e.Frame();
                    // A failed capture (null) is never a stall: it must not trigger a jump.
                    bool haveDiff = prev != null && cur != null;
                    double diff = haveDiff ? TeleportDetector.SceneDiff(prev, cur, Rectangle.Empty, Rectangle.Empty) : -1;
                    bool stalled = haveDiff && diff < SettledChange;
                    if (prev != null) prev.Dispose();
                    prev = cur;
                    Step(e, "walk poll at " + Secs(now) + " s: "
                        + (haveDiff
                            ? "SceneDiff " + diff.ToString("0.000", CultureInfo.InvariantCulture)
                              + (stalled ? " (stalled, below " + SettledChange.ToString("0.000", CultureInfo.InvariantCulture) + ")" : " (moving)")
                            : "frame not captured (not a stall)")
                        + "; focus ok, cursor on anchor");

                    WalkStep step = walk.Next(stalled, true, now, false);
                    if (step == WalkStep.Jump)
                    {
                        Step(e, "blocked (no scene change), jumping (" + walk.Jumps + "/" + HospitalWalk.MaxJumps + ")");
                        string jumpErr = Jump(e);
                        if (jumpErr != null) return Fail(jumpErr);
                        continue;
                    }
                    if (step == WalkStep.Hold) continue;
                    Step(e, "walk decision at " + Secs(now) + " s: " + step);
                    return WalkOutcome(step);
                }
            }
            finally
            {
                if (held)
                {
                    bool up = B.KeyUp(ScanW);
                    Step(e, "W key-up " + (up ? "sent" : "REFUSED by SendInput"));
                }
                if (prev != null) prev.Dispose();
            }
        }

        private const string WalkFocusText = "The game window lost focus; the walk was stopped.";

        // One Space tap: down and up are always paired, even when the key-down was refused. Returns null when the tap was
        // sent, otherwise the reason the walk must stop.
        private static string Jump(WalkEnv e)
        {
            if (!e.Input.Focus(e.Hwnd, e.Trace)) return WalkFocusText;
            InputSim.LastScanError = 0;
            bool down = false, up = false;
            int downErr = 0;
            try
            {
                down = e.Input.KeyDown(ScanSpace);
                downErr = InputSim.LastScanError;
                if (down) e.Input.Sleep(InputSim.Timing.KeyTapMs);
            }
            finally
            {
                up = e.Input.KeyUp(ScanSpace);
            }
            Step(e, "Space tap: down " + (down ? "sent" : "REFUSED (GetLastError " + downErr + ")") + ", up " + (up ? "sent" : "REFUSED"));
            return down ? null : "The jump (Space) was refused by SendInput (GetLastError " + downErr + "), so the walk was stopped.";
        }

        private static void Step(WalkEnv e, string s)
        {
            if (e.Trace != null) e.Trace.Add(s);
            Say(e, s);
        }

        private static string Secs(double s)
        {
            return s.ToString("0.0", CultureInfo.InvariantCulture);
        }

        // Diagnostic only: whether the backpack pane is still on screen when the walk starts (it should be closed).
        private static string DescribePanel(Bitmap b)
        {
            try { return PanelOpen(b) ? "backpack pane still open" : "no backpack pane on screen"; }
            catch (Exception) { return "backpack pane check failed"; }
        }

        private static HospitalResult WalkOutcome(WalkStep step)
        {
            switch (step)
            {
                case WalkStep.ReleaseDone:
                    return new HospitalResult(true, "walked toward the hospital marker for " + WalkSeconds + " s.");
                case WalkStep.ReleaseBlocked:
                    return Fail("The character is blocked: it stalled again after the one jump. W was released and the walk stopped.");
                case WalkStep.ReleaseFocus:
                    return Fail(WalkFocusText);
                case WalkStep.ReleaseDrift:
                    return Fail("The mouse was moved; the walk was stopped.");
                default:
                    return Fail("The walk ended unexpectedly.");
            }
        }

        private static bool HasFocus(IInputBackend b, IntPtr hwnd)
        {
            return InputSim.NativeWin.SameApp(b.Foreground(), hwnd);
        }

        private static void Say(WalkEnv e, string s)
        {
            if (e.Say != null) e.Say(s);
        }

        // ----------------------------------------------------------------
        // Helpers
        // ----------------------------------------------------------------

        private static HospitalResult Fail(string message)
        {
            return new HospitalResult(false, message);
        }

        // Centre of the client area in screen coordinates (the cursor anchor).
        private static bool ClientCentre(IntPtr hwnd, out Point centre)
        {
            centre = Point.Empty;
            InputSim.NativeWin.RECT rc;
            if (!InputSim.NativeWin.GetClientRect(hwnd, out rc)) return false;
            InputSim.NativeWin.POINT p = new InputSim.NativeWin.POINT();
            p.X = (rc.Right - rc.Left) / 2;
            p.Y = (rc.Bottom - rc.Top) / 2;
            if (!InputSim.NativeWin.ClientToScreen(hwnd, ref p)) return false;
            centre = new Point(p.X, p.Y);
            return true;
        }

        private static Bitmap Capture(IntPtr hwnd)
        {
            string method;
            Bitmap b = CashOutDetector.CaptureWindow(hwnd, out method);
            if (b != null)
            {
                if (_evidence != null) _evidence.Dispose();
                _evidence = (Bitmap)b.Clone();
            }
            return b;
        }

        private static bool PanelOpenNow(IntPtr hwnd)
        {
            Bitmap b = Capture(hwnd);
            if (b == null) return false;
            try { return PanelOpen(b); } finally { b.Dispose(); }
        }

        private static bool PromptNow(IntPtr hwnd)
        {
            Bitmap b = Capture(hwnd);
            if (b == null) return false;
            try { return HospitalDetector.FindTeleportYes(b).HasValue; } finally { b.Dispose(); }
        }

        private static bool GiftsSelectedNow(IntPtr hwnd)
        {
            Bitmap b = Capture(hwnd);
            if (b == null) return false;
            try { return HospitalDetector.FindGiftsTabHighlighted(b).HasValue; } finally { b.Dispose(); }
        }

        // The backpack pane is open when its Gifts tile (selected or not) or its "+" tile is on screen.
        private static bool PanelOpen(Bitmap bmp)
        {
            return HospitalDetector.FindGiftsTabHighlighted(bmp).HasValue
                || HospitalDetector.FindPlusTile(bmp).HasValue
                || HospitalDetector.FindGiftsTabUnselected(bmp).HasValue;
        }

        // One click through InputSim at the centre of the element; verify must return true for success.
        // Afterwards the cursor is put back on the anchor (ClickClient restores the pre-click position).
        // A toggle button (the backpack): one SendInput click only, then poll until the pane is open. The default click
        // ladder retries other methods when a check fails, and each retry would toggle the pane again.
        private static bool ClickToggle(RouteRun r, Rectangle rect, Func<bool> opened, int waitMs)
        {
            Point cp = HospitalDetector.Centre(rect);
            ClickOptions opt = new ClickOptions();
            ClickReport rep = InputSim.ClickClient(r.Hwnd, cp.X, cp.Y, new ClickTech[] { ClickTech.SendInput }, opt);
            if (r.HasAnchor) InputSim.Backend.SetCursorPos(r.Anchor.X, r.Anchor.Y);
            if (!rep.Delivered) return false;
            Stopwatch sw = Stopwatch.StartNew();
            while (sw.ElapsedMilliseconds < waitMs)
            {
                Thread.Sleep(250);
                if (opened()) return true;
            }
            return false;
        }

        private static bool ClickCentre(RouteRun r, Rectangle rect, Func<bool> verify)
        {
            Point cp = HospitalDetector.Centre(rect);
            ClickOptions opt = new ClickOptions();
            opt.Verify = verify;
            ClickReport rep = InputSim.ClickClient(r.Hwnd, cp.X, cp.Y, opt);
            if (r.HasAnchor) InputSim.Backend.SetCursorPos(r.Anchor.X, r.Anchor.Y);
            Thread.Sleep(200);   // let the game react before the next capture
            return rep.Ok;
        }
    }

    /// <summary>
    /// Turns the camera with a right-button drag inside the game window. Every event goes through InputSim.Backend.
    /// The right button is always released in a finally (the flag is set before the press, so a press is never left
    /// without its release). The cursor is put back on the anchor afterwards. check runs before the press and once
    /// more after the drag and the reset; it may throw to stop the route. During the drag's own moves only ct is
    /// checked, because the route's relative moves are what move the cursor and would otherwise look like drift.
    /// </summary>
    internal static class MouseDrag
    {
        private const int StepPx = 12;

        public static bool Hold(IntPtr hwnd, int dx, Point anchor, CancellationToken ct, Action check)
        {
            IInputBackend B = InputSim.Backend;
            if (!InputSim.EnsureForeground(hwnd, null)) return false;

            B.SetCursorPos(anchor.X, anchor.Y);
            B.Sleep(60);

            bool pressed = false;
            try
            {
                if (check != null) check();
                pressed = true;
                B.RightDown();
                B.Sleep(60);

                int left = dx;
                int sign = dx < 0 ? -1 : 1;
                while (left != 0)
                {
                    ct.ThrowIfCancellationRequested();
                    int step = sign * Math.Min(Math.Abs(left), StepPx);
                    B.MoveRelative(step);
                    left -= step;
                    B.Sleep(8);
                }
                B.Sleep(60);
            }
            finally
            {
                if (pressed) B.RightUp();
                B.SetCursorPos(anchor.X, anchor.Y);
            }
            if (check != null) check();
            B.Sleep(250);
            return true;
        }
    }
}
