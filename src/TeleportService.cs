// TeleportService.cs
//
// Teleports instance A's player to instance B's player in Adopt Me by driving the in-game UI of A's window:
// click the pink FRIENDS button, wait for the FRIENDS panel, find B's row by USERNAME (OCR), click its green
// Teleport button, then judge from screen captures whether it worked. Screen capture, image analysis and
// SendInput only (like the CASH OUT clicker). No injection, no process memory access. C# 5.
//
// PUBLIC API (namespace RbxMulti)
//   enum TeleportStatus { Success, Likely, Failed, Cancelled }
//   class TeleportResult { TeleportStatus Status; string Message; }
//   class TeleportJob { void Cancel(); bool IsDone; TeleportResult Result; }
//   class TeleportService
//       event Action<string> Log                 any thread; ids only ("#2 -> #3"), never tokens or titles.
//       Func<RobloxInstance, IntPtr> ResolveWindow   default: AntiIdleClicker.FindMainWindow(pid). Tests override.
//       bool RetryOnTransientFailure             default true; one retry when the panel/button was not found or the click did nothing.
//       int PanelTimeoutMs / ObserveMs           defaults 4500 / 7500.
//       bool IsBusy                              true while a teleport runs (one at a time, process wide).
//       string CanTeleport(source, target)       null when allowed, else the specific refusal text (UI uses it for tooltips).
//       TeleportResult Teleport(source, target, CancellationToken ct)   blocking; call from a worker thread.
//       TeleportJob Start(source, target, Action<TeleportResult> done)  runs Teleport on a background thread.
//
// Safety: takes AntiIdleClicker.ClickLock for the whole flow (the CASH OUT clicker waits), always closes the
// FRIENDS panel it opened (red X), restores the cursor after each click and the previous foreground window at the end.

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;

namespace RbxMulti
{
    public enum TeleportStatus
    {
        Success,
        Likely,
        Failed,
        Cancelled
    }

    public sealed class TeleportResult
    {
        public TeleportStatus Status;
        public string Message;

        public TeleportResult(TeleportStatus status, string message)
        {
            Status = status;
            Message = message;
        }

        public override string ToString()
        {
            return Status + ": " + Message;
        }
    }

    public sealed class TeleportJob
    {
        internal CancellationTokenSource Cts = new CancellationTokenSource();
        private volatile bool _done;
        public TeleportResult Result;
        public bool IsDone { get { return _done; } internal set { _done = value; } }
        public void Cancel()
        {
            try { Cts.Cancel(); } catch (Exception) { }
        }
    }

    public sealed class TeleportService
    {
        public const string FriendsHint = "Both accounts must be friends in Adopt Me. Uses screen automation: the game window briefly takes focus and the cursor moves, only when you press the button. Detection can fail if the game UI changes or the window is covered or too small.";

        private static int _busy;
        private static int _rememberedPoint = -1;   // index in the FRIENDS click ladder that opened the panel last time

        /// <summary>Tests: forget the remembered FRIENDS click point.</summary>
        internal static void ResetMemory() { _rememberedPoint = -1; }
        internal static int RememberedPoint { get { return _rememberedPoint; } }

        public event Action<string> Log;
        public Func<RobloxInstance, IntPtr> ResolveWindow;
        public bool RetryOnTransientFailure = true;
        public int PanelTimeoutMs = 4500;
        public int ObserveMs = 7500;
        public int SettleMs = 350;
        public int PanelAfterClickMs = 2500;          // how long to look for the panel after the FRIENDS click
        public bool RetryFriendsWithAlternative = true;

        // Friends list loading (Roblox loads it asynchronously and is slow with many clients running).
        public const int DefaultListWaitSeconds = 25;
        public const int MinListWaitSeconds = 5;
        public const int MaxListWaitSeconds = 60;
        public int ListWaitMs = DefaultListWaitSeconds * 1000;   // total wait for the list after the panel opened
        public int ListPollMs = 700;
        public int ListRecoverAfterMs = 10000;                   // still 'Loading...' after this: click Nearby then Friends once
        public int EmptyGraceMs = 4000;                          // a blank body (no 'Loading...') this long = empty list
        public int MaxScrolls = 6;                               // wheel scroll steps when the target is not among the visible rows

        /// <summary>Progress text for the busy chip ("Waiting for the friends list to load... 8 s"). Any thread.</summary>
        public event Action<string> Status;

        /// <summary>Tests: replaces the mouse wheel over the list (hwnd, panel).</summary>
        public Action<IntPtr, FriendsPanelInfo> ScrollHook;

        public static int ClampListWaitSeconds(int s) { return Math.Max(MinListWaitSeconds, Math.Min(MaxListWaitSeconds, s)); }

        /// <summary>Tests only: when set, clicks go through this (hwnd, clientX, clientY) -> delivered instead of SendInput.</summary>
        public Func<IntPtr, int, int, bool> ClickHook;

        private sealed class AttemptResult
        {
            public TeleportResult Result;
            public bool Retryable;
        }

        public TeleportService()
        {
            ResolveWindow = delegate(RobloxInstance i) { return AntiIdleClicker.FindMainWindow(i == null ? 0 : i.Pid); };
        }

        public bool IsBusy { get { return Thread.VolatileRead(ref _busy) != 0; } }

        /// <summary>The manual CASH OUT test shares the one-at-a-time guard with Teleport.</summary>
        internal static bool TryAcquireExclusive() { return Interlocked.CompareExchange(ref _busy, 1, 0) == 0; }
        internal static void ReleaseExclusive() { ClearBusy(); }

        // Every release bumps the generation, so a caller that waited for the mouse can tell a test ran in between.
        private static long _guardGen;
        private static void ClearBusy()
        {
            Interlocked.Exchange(ref _busy, 0);
            Interlocked.Increment(ref _guardGen);
        }
        internal static long ExclusiveGeneration { get { return Interlocked.Read(ref _guardGen); } }
        internal static bool ExclusiveBusy { get { return Thread.VolatileRead(ref _busy) != 0; } }

        // ------------------------------------------------------------------------------------
        // Preconditions
        // ------------------------------------------------------------------------------------

        public string CanTeleport(RobloxInstance source, RobloxInstance target)
        {
            return CanTeleport(source, target, true);
        }

        /// <summary>checkWindows=false skips the (slower) window lookups; the UI uses it every tick for tooltips.</summary>
        public string CanTeleport(RobloxInstance source, RobloxInstance target, bool checkWindows)
        {
            if (source == null) return "No source instance.";
            if (target == null) return "Pick the instance to teleport to.";
            if (ReferenceEquals(source, target) || source.Id == target.Id) return "Pick a different instance as the target (source and target are the same).";
            if (source.StatusKind != "Running") return "This instance is not running yet (status: " + StatusText(source) + "). Wait until it shows the green Running status.";
            if (target.StatusKind != "Running") return "The target instance is not running yet (status: " + StatusText(target) + "). Wait until it shows the green Running status.";
            if (string.IsNullOrEmpty(target.AccountName)) return "The target has no known username (it was started with the website login). Teleport finds the friend by username, so launch it from a stored account.";
            if (!checkWindows) return null;
            IntPtr hs = SafeResolve(source);
            if (hs == IntPtr.Zero || !TpNative.IsWindowVisible(hs)) return "This instance has no visible window yet.";
            IntPtr ht = SafeResolve(target);
            if (ht == IntPtr.Zero || !TpNative.IsWindowVisible(ht)) return "The target instance has no visible window yet (not loaded in).";
            return null;
        }

        private static string StatusText(RobloxInstance i)
        {
            return string.IsNullOrEmpty(i.Status) ? "unknown" : i.Status;
        }

        private IntPtr SafeResolve(RobloxInstance i)
        {
            try { return ResolveWindow(i); }
            catch (Exception) { return IntPtr.Zero; }
        }

        // ------------------------------------------------------------------------------------
        // Entry points
        // ------------------------------------------------------------------------------------

        public TeleportJob Start(RobloxInstance source, RobloxInstance target, Action<TeleportResult> done)
        {
            TeleportJob job = new TeleportJob();
            Thread t = new Thread(delegate()
            {
                TeleportResult r;
                try { r = Teleport(source, target, job.Cts.Token); }
                catch (Exception ex) { r = new TeleportResult(TeleportStatus.Failed, "Unexpected error: " + ex.GetType().Name); }
                job.Result = r;
                job.IsDone = true;
                if (done != null)
                {
                    try { done(r); } catch (Exception) { }
                }
            });
            t.IsBackground = true;
            t.Name = "TeleportService";
            t.Start();
            return job;
        }

        public TeleportResult Teleport(RobloxInstance source, RobloxInstance target, CancellationToken ct)
        {
            string refusal = CanTeleport(source, target);
            if (refusal != null)
            {
                Raise("refused: " + refusal);
                return new TeleportResult(TeleportStatus.Failed, refusal);
            }
            if (Interlocked.CompareExchange(ref _busy, 1, 0) != 0)
            {
                return new TeleportResult(TeleportStatus.Failed, "Another teleport is already running. Wait for it or cancel it.");
            }
            string tag = "#" + source.Id.ToString(CultureInfo.InvariantCulture) + " -> #" + target.Id.ToString(CultureInfo.InvariantCulture);
            try
            {
                IntPtr hwnd = SafeResolve(source);
                string username = target.AccountName;
                Raise(tag + " start");

                // Wait for the global click lock (the CASH OUT clicker may hold it for a moment).
                bool locked = false;
                Stopwatch wait = Stopwatch.StartNew();
                while (!locked)
                {
                    if (ct.IsCancellationRequested) return Cancelled(tag, "before it started");
                    locked = Monitor.TryEnter(AntiIdleClicker.ClickLock, 100);
                    if (!locked && wait.ElapsedMilliseconds > 20000)
                    {
                        return new TeleportResult(TeleportStatus.Failed, "Could not get the mouse: the auto-clicker was busy for 20 s.");
                    }
                }
                try
                {
                    return RunLocked(tag, hwnd, username, ct);
                }
                finally
                {
                    Monitor.Exit(AntiIdleClicker.ClickLock);
                }
            }
            finally
            {
                ClearBusy();
            }
        }

        private TeleportResult Cancelled(string tag, string when)
        {
            Raise(tag + " cancelled " + when);
            return new TeleportResult(TeleportStatus.Cancelled, "Cancelled " + when + ".");
        }

        // ------------------------------------------------------------------------------------
        // Locked section: save/restore foreground and cursor, attempts
        // ------------------------------------------------------------------------------------

        private TeleportResult RunLocked(string tag, IntPtr hwnd, string username, CancellationToken ct)
        {
            IntPtr prevFg = TpNative.GetForegroundWindow();
            TeleportResult result = null;
            try
            {
                int attempts = RetryOnTransientFailure ? 2 : 1;
                for (int attempt = 1; attempt <= attempts; attempt++)
                {
                    AttemptResult ar = RunAttempt(tag, attempt, hwnd, username, ct);
                    result = ar.Result;
                    if (result.Status != TeleportStatus.Failed || !ar.Retryable || attempt == attempts) break;
                    Raise(tag + " attempt " + attempt + " failed (" + result.Message + "), retrying once");
                    Sleep(1500, ct);
                }
            }
            catch (OperationCanceledException)
            {
                result = new TeleportResult(TeleportStatus.Cancelled, "Cancelled. If the Teleport button had already been clicked, the teleport may still happen.");
                Raise(tag + " cancelled");
            }
            catch (Exception ex)
            {
                result = new TeleportResult(TeleportStatus.Failed, "Unexpected error: " + ex.GetType().Name);
                Raise(tag + " error " + ex.GetType().Name);
            }
            finally
            {
                if (prevFg != IntPtr.Zero && prevFg != hwnd && TpNative.IsWindow(prevFg))
                {
                    try { AntiIdleClicker.BringToFront(prevFg); } catch (Exception) { }
                }
            }
            Raise(tag + " result " + result.Status + ": " + result.Message);
            return result;
        }

        private void Sleep(int ms, CancellationToken ct)
        {
            if (ct.WaitHandle.WaitOne(ms)) throw new OperationCanceledException();
        }

        private AttemptResult RunAttempt(string tag, int attempt, IntPtr hwnd, string username, CancellationToken ct)
        {
            string at = tag + " attempt " + attempt + ": ";
            bool panelOpenedByUs = false;
            Bitmap panelFrame = null;
            try
            {
                // 1) Bring the source window forward.
                if (!TpNative.IsWindow(hwnd)) return Fail("The instance window is gone.", false);
                if (TpNative.IsIconic(hwnd)) { TpNative.ShowWindow(hwnd, 9); Sleep(400, ct); }
                AntiIdleClicker.BringToFront(hwnd);
                Sleep(SettleMs, ct);
                if (TpNative.GetForegroundWindow() != hwnd)
                {
                    AntiIdleClicker.BringToFront(hwnd);
                    Sleep(SettleMs, ct);
                }

                // 2) Capture and look for the panel (already open) or the FRIENDS button.
                string method;
                Bitmap frame = Capture(hwnd, out method);
                if (frame == null) return Fail("Could not capture the game window (minimised or hidden).", true);
                FriendsPanelInfo panel = null;
                try
                {
                    if (frame.Height < 270 || frame.Width < 480)
                    {
                        return Fail("The game window is too small (" + frame.Width + "x" + frame.Height + "). Make it at least 640x360.", false);
                    }
                    Raise(at + "captured " + frame.Width + "x" + frame.Height + " via " + method);
                    panel = TeleportDetector.FindPanel(frame, true);
                    if (panel != null)
                    {
                        Raise(at + "FRIENDS panel was already open");
                        panelOpenedByUs = true;   // we close it when done
                    }
                    else
                    {
                        FriendsButtonInfo btn = null;
                        for (int i = 0; i < 4; i++)
                        {
                            btn = TeleportDetector.FindFriendsButton(frame);
                            if (btn != null) break;
                            frame.Dispose(); frame = null;
                            Sleep(700, ct);
                            frame = Capture(hwnd, out method);
                            if (frame == null) return Fail("Could not capture the game window.", true);
                        }
                        if (btn == null) return Fail("Game not loaded yet or the sidebar is hidden (no FRIENDS button found).", true);
                        Raise(at + "FRIENDS button found at " + btn.Click.X + "," + btn.Click.Y + " (score " + btn.Score.ToString("0.00", CultureInfo.InvariantCulture) + ")");

                        // Click it and wait up to PanelAfterClickMs for the panel, re-capturing. Auto: next click method when nothing opens.
                        Func<bool> panelVerify = delegate
                        {
                            Stopwatch pw = Stopwatch.StartNew();
                            while (!ct.IsCancellationRequested)
                            {
                                string pm;
                                Bitmap f = Capture(hwnd, out pm);
                                if (f != null)
                                {
                                    FriendsPanelInfo p = TeleportDetector.FindPanel(f, true);
                                    if (p != null)
                                    {
                                        if (panelFrame != null) panelFrame.Dispose();
                                        panelFrame = f;
                                        panel = p;
                                        return true;
                                    }
                                    f.Dispose();
                                }
                                if (pw.ElapsedMilliseconds >= PanelAfterClickMs) break;
                                Thread.Sleep(350);
                            }
                            return false;
                        };
                        // Click ladder: the pink ICON of the sticker (the label sits on a tilted ribbon and is not reliably hit).
                        // Points are tried in turn with the preferred click method; the point that worked last time goes first.
                        Point[] ladder = btn.Ladder != null && btn.Ladder.Length > 0 ? btn.Ladder : new Point[] { btn.Click };
                        List<int> order = new List<int>();
                        int mem = _rememberedPoint;
                        if (mem >= 0 && mem < ladder.Length) order.Add(mem);
                        for (int li = 0; li < ladder.Length; li++) if (!order.Contains(li)) order.Add(li);
                        ClickTech[] preferred = InputSim.OrderFor(InputSim.Method);
                        ClickTech[] single = new ClickTech[] { preferred[0] };
                        ClickReport fr = null;
                        StringBuilder triedPts = new StringBuilder();
                        // One-shot "is the panel open right now?" (any theme). A FRIENDS click TOGGLES the panel, so never click
                        // while it is open: re-check before the first click and before every retry.
                        Func<bool> quickCheck = delegate
                        {
                            string qm;
                            Bitmap qf = Capture(hwnd, out qm);
                            if (qf == null) return false;
                            FriendsPanelInfo qp = TeleportDetector.FindPanel(qf, true);
                            if (qp == null) { qf.Dispose(); return false; }
                            if (panelFrame != null) panelFrame.Dispose();
                            panelFrame = qf; panel = qp;
                            return true;
                        };
                        for (int oi = 0; oi < order.Count && panel == null; oi++)
                        {
                            if (quickCheck())
                            {
                                Raise(at + "FRIENDS panel is already open (re-checked before clicking), not clicking FRIENDS");
                                break;
                            }
                            Point lp = ladder[order[oi]];
                            fr = DoClick(hwnd, lp.X, lp.Y, panelVerify, single);
                            ct.ThrowIfCancellationRequested();
                            triedPts.Append(" (" + lp.X + "," + lp.Y + ")" + (panel != null ? " opened" : fr.Delivered ? " no change" : " not delivered"));
                            if (panel != null)
                            {
                                _rememberedPoint = order[oi];
                                Raise(at + "FRIENDS panel opened with click point " + (order[oi] + 1) + "/" + ladder.Length + " at " + lp.X + "," + lp.Y + " (remembered for next time)");
                            }
                            else if (!fr.Delivered) break;   // covered / focus / cursor problems: another point will not help
                            else Raise(at + "FRIENDS click at " + lp.X + "," + lp.Y + " did not open the panel" + (oi + 1 < order.Count ? ", trying the next point on the icon" : ""));
                        }
                        string tried = "points:" + triedPts + "; " + (fr == null ? "" : fr.AttemptsText());
                        Point lastPoint = fr == null ? btn.Click : new Point(fr.ClientX, fr.ClientY);
                        if (panel == null && fr != null && fr.Delivered && quickCheck())
                        {
                            Raise(at + "FRIENDS panel turned out to be open after all (late open), not clicking again");
                            _rememberedPoint = order[0];
                        }
                        if (panel == null && fr != null && fr.Delivered)
                        {
                            // All points failed with the preferred method: the full method ladder at the primary point.
                            fr = DoClick(hwnd, ladder[order[0]].X, ladder[order[0]].Y, panelVerify, null);
                            ct.ThrowIfCancellationRequested();
                            tried += " | all methods at the primary point: " + fr.AttemptsText();
                            lastPoint = new Point(fr.ClientX, fr.ClientY);
                            if (panel == null && fr.Delivered && quickCheck()) { Raise(at + "FRIENDS panel is open (re-checked), no retry click"); }
                            if (panel == null && fr.Delivered && RetryFriendsWithAlternative)
                            {
                                // Retry once with the other family of click methods.
                                ClickTech[] alt = InputSim.Method == ClickMethod.SendInput
                                    ? InputSim.OrderFor(ClickMethod.Legacy)
                                    : (InputSim.Method == ClickMethod.Legacy ? InputSim.OrderFor(ClickMethod.SendInput) : null);
                                if (alt != null)
                                {
                                    Raise(at + "FRIENDS click did not open the panel, retrying once with the alternative click method");
                                    fr = DoClick(hwnd, ladder[order[0]].X, ladder[order[0]].Y, panelVerify, alt);
                                    ct.ThrowIfCancellationRequested();
                                    tried += " | retry: " + fr.AttemptsText();
                                }
                            }
                            if (panel != null) _rememberedPoint = order[0];
                        }
                        if (panel == null && quickCheck()) { Raise(at + "FRIENDS panel is open (final re-check)"); }
                        if (panel == null)
                        {
                            string text;
                            if (fr == null) text = "The FRIENDS button could not be clicked.";
                            else if (!fr.Delivered) text = DeliveryFailText(fr, "the FRIENDS button");
                            else text = "Click did not register: the FRIENDS button was clicked (" + tried + ") but the panel never opened within " + (PanelAfterClickMs / 1000.0).ToString("0.#", CultureInfo.InvariantCulture) + " s.";
                            string folder = InputDiagnostics.WriteFailureEvidence("teleport-friends", text, hwnd, fr, btn.Bounds);
                            if (folder != null) { Raise(at + "evidence saved to " + folder); text += " Evidence saved (see the log for the folder)."; }
                            return Fail(text, true);
                        }
                        if (fr != null) Raise(at + "FRIENDS panel opened via " + InputSim.TechLabel(fr.TechUsed));
                        panelOpenedByUs = true;
                    }
                }
                finally
                {
                    if (frame != null) frame.Dispose();
                }

                // 3) The panel is open: take the frame it was seen in.
                Bitmap cur = panelFrame;
                panelFrame = null;
                try
                {
                    if (panel == null) { panelOpenedByUs = false; return Fail("The FRIENDS panel is not visible.", true); }
                    Raise(at + "panel open, " + panel.Rows.Count + " row(s)");
                    if (cur == null) cur = Capture(hwnd, out method);
                    if (cur == null) return Fail("Could not capture the game window.", true);

                    // Roblox loads the list asynchronously ('Loading...' in the body): poll until at least one row shows up.
                    if (panel.Rows.Count == 0)
                    {
                        Stopwatch lsw = Stopwatch.StartNew();
                        int lastLogged = -1;
                        TeleportDetector.ListWaitResult lw = TeleportDetector.WaitForList(
                            delegate { string wm; return Capture(hwnd, out wm); },
                            ListWaitMs, ListPollMs, ListRecoverAfterMs, EmptyGraceMs,
                            delegate(FriendsPanelInfo pp) { return RecoverList(at, hwnd, ct); },
                            delegate(int ms, TeleportDetector.PanelState st)
                            {
                                int sec = ms / 1000;
                                if (st == TeleportDetector.PanelState.Loading || st == TeleportDetector.PanelState.Empty)
                                {
                                    RaiseStatus("Waiting for the friends list to load... " + sec + " s");
                                    if (sec / 5 != lastLogged) { lastLogged = sec / 5; Raise(at + "friends list " + st + " after " + sec + " s"); }
                                }
                            }, ct);
                        if (lw.Cancelled)
                        {
                            if (lw.Frame != null) lw.Frame.Dispose();
                            throw new OperationCanceledException();
                        }
                        string waitInfo = "list " + lw.State + " after " + (lw.ElapsedMs / 1000.0).ToString("0.0", CultureInfo.InvariantCulture) + " s (" + lw.Polls + " polls" + (lw.Recovered ? ", Nearby/Friends refresh tried" : "") + ")";
                        Raise(at + waitInfo);
                        if (lw.Frame != null) { cur.Dispose(); cur = lw.Frame; if (lw.Panel != null) panel = lw.Panel; }
                        if (lw.State == TeleportDetector.PanelState.Closed)
                        {
                            panelOpenedByUs = false;
                            return Fail("The FRIENDS panel closed unexpectedly while waiting for the friends list.", true);
                        }
                        if (lw.State == TeleportDetector.PanelState.Loading)
                        {
                            return FailEvidence(at, "loading", "Friends list never finished loading (Roblox is busy - try with fewer instances running or again in a moment). Waited " + (lw.ElapsedMs / 1000) + " s.",
                                false, hwnd, cur, panel, username, null, null);
                        }
                        if (lw.State == TeleportDetector.PanelState.Empty)
                        {
                            return FailEvidence(at, "teleport-norows", "The FRIENDS panel opened (" + panel.Bounds.Width + "x" + panel.Bounds.Height + " in a " + cur.Width + "x" + cur.Height + " window) but no friend rows were detected: the list is empty, still loading, or the layout was not recognised.",
                                true, hwnd, cur, panel, username, null, null);
                        }
                        RaiseStatus("Friends list loaded after " + (lw.ElapsedMs / 1000) + " s");
                    }

                    // 4) Find the target's row (scroll the list if everyone visible is online).
                    NameMatch match = null;
                    string lastFirst = null;
                    for (int page = 0; page <= MaxScrolls; page++)
                    {
                        TeleportDetector.ReadNames(cur, panel, username);
                        string names = JoinNames(panel);
                        Raise(at + "rows read: " + panel.Rows.Count + " (" + CountOnline(panel) + " online)");
                        match = TeleportDetector.MatchTarget(panel, username);
                        if (match.Status == MatchStatus.NoOcr)
                        {
                            return Fail("Windows text recognition (OCR) is not available on this PC, so the friend cannot be found by name.", false, panel, hwnd, ct);
                        }
                        if (match.Status != MatchStatus.NotFound) break;
                        if (page >= MaxScrolls || names == lastFirst) break;   // end of the list (nothing new after a scroll) or scroll budget used
                        lastFirst = names;
                        Raise(at + "target not among the " + panel.Rows.Count + " visible row(s), scrolling the list (" + (page + 1) + "/" + MaxScrolls + ")");
                        ScrollList(hwnd, panel, ct);
                        Sleep(500, ct);
                        cur.Dispose(); cur = Capture(hwnd, out method);
                        if (cur == null) return Fail("Could not capture the game window.", true, panel, hwnd, ct);
                        FriendsPanelInfo again = TeleportDetector.FindPanel(cur, true);
                        if (again == null) return Fail("The FRIENDS panel closed unexpectedly.", true);
                        panel = again;
                    }

                    if (match.Status == MatchStatus.Ambiguous)
                    {
                        return FailEvidence(at, "teleport-ambiguous", "Two friends have similar names; not guessing. " + match.Message + ". Panel read: " + TeleportDetector.DescribeNames(panel) + ".", false, hwnd, cur, panel, username, null, null);
                    }
                    if (match.Status != MatchStatus.Found)
                    {
                        return FailEvidence(at, "teleport-name", match.Message + " Both accounts must be friends in Adopt Me and the target must be online.", false, hwnd, cur, panel, username, null, null);
                    }
                    if (!match.Row.Online)
                    {
                        return FailEvidence(at, "teleport-nobutton", "Teleport button not found for the target: its row ('" + match.Row.Name + "') shows a grey Offline button, not a green Teleport button (offline or not in the game yet).", false, hwnd, cur, panel, username, null, null);
                    }
                    Raise(at + "target row found (score " + match.Score.ToString("0.00", CultureInfo.InvariantCulture) + ", Teleport button)");

                    // 5) Click Teleport.
                    Rectangle br = match.Row.ButtonRect;
                    Bitmap pre = (Bitmap)cur.Clone();
                    cur.Dispose(); cur = null;
                    Rectangle panelExcl = panel.Bounds;
                    panelExcl.Inflate((int)(panelExcl.Width * 0.04), (int)(panelExcl.Height * 0.06));
                    try
                    {
                        ClickReport tr = DoClick(hwnd, br.X + br.Width / 2, br.Y + br.Height / 2, null, null);
                        ct.ThrowIfCancellationRequested();
                        if (!tr.Delivered)
                        {
                            string ttext = DeliveryFailText(tr, "the Teleport button");
                            return FailEvidence(at, "teleport-button", ttext, true, hwnd, pre, panel, username, new Point(br.X + br.Width / 2, br.Y + br.Height / 2), tr);
                        }
                        Raise(at + "clicked Teleport via " + InputSim.TechLabel(tr.TechUsed));
                        return Observe(at, hwnd, pre, panel, panelExcl, ct, ref panelOpenedByUs, username, new Point(br.X + br.Width / 2, br.Y + br.Height / 2));
                    }
                    finally
                    {
                        pre.Dispose();
                    }
                }
                finally
                {
                    if (cur != null) cur.Dispose();
                }
            }
            catch (OperationCanceledException)
            {
                if (panelOpenedByUs) { try { ClosePanel(hwnd, null); } catch (Exception) { } }
                throw;
            }
        }

        private AttemptResult Fail(string message, bool retryable)
        {
            AttemptResult r = new AttemptResult();
            r.Result = new TeleportResult(TeleportStatus.Failed, message);
            r.Retryable = retryable;
            return r;
        }

        // Fail and make sure the panel is closed afterwards.
        private AttemptResult Fail(string message, bool retryable, FriendsPanelInfo panel, IntPtr hwnd, CancellationToken ct)
        {
            ClosePanel(hwnd, panel);
            return Fail(message, retryable);
        }

        private static string JoinNames(FriendsPanelInfo p)
        {
            System.Text.StringBuilder sb = new System.Text.StringBuilder();
            foreach (PanelRow r in p.Rows) { sb.Append(r.Name).Append('|'); }
            return sb.ToString();
        }

        private static int CountOnline(FriendsPanelInfo p)
        {
            int n = 0;
            foreach (PanelRow r in p.Rows) if (r.Online) n++;
            return n;
        }

        // ------------------------------------------------------------------------------------
        // Success check
        // ------------------------------------------------------------------------------------

        // Evidence (annotated capture + report) for a failure that happened with the panel open, then close the panel and fail.
        private AttemptResult FailEvidence(string at, string kind, string message, bool retryable, IntPtr hwnd, Bitmap frame,
            FriendsPanelInfo panel, string username, Point? click, ClickReport rep)
        {
            string folder = InputDiagnostics.WriteTeleportEvidence(kind, message, hwnd, frame, panel, username, click, rep);
            string text = message;
            if (folder != null) { Raise(at + "evidence saved to " + folder); text += " Evidence saved to " + folder + "."; }
            ClosePanel(hwnd, panel);
            return Fail(text, retryable);
        }

        private AttemptResult Observe(string at, IntPtr hwnd, Bitmap pre, FriendsPanelInfo panel, Rectangle exclude,
            CancellationToken ct, ref bool panelOpenedByUs, string username, Point clickPoint)
        {
            Bitmap lastFrame = null;
            Stopwatch sw = Stopwatch.StartNew();
            double maxDiff = 0;
            bool panelOpen = true;
            int strongFrames = 0;
            string method;
            List<string> trace = new List<string>();
            while (sw.ElapsedMilliseconds < ObserveMs)
            {
                if (ct.WaitHandle.WaitOne(500))
                {
                    if (panelOpen) ClosePanel(hwnd, null);
                    panelOpenedByUs = false;
                    throw new OperationCanceledException();
                }
                Bitmap f = Capture(hwnd, out method);
                if (f == null) continue;
                try
                {
                    double d = TeleportDetector.SceneDiff(pre, f, exclude, Rectangle.Empty);
                    if (d > maxDiff) maxDiff = d;
                    panelOpen = TeleportDetector.FindPanel(f, true) != null;
                    if (lastFrame != null) lastFrame.Dispose();
                    lastFrame = (Bitmap)f.Clone();
                    trace.Add(((int)(d * 100)).ToString(CultureInfo.InvariantCulture) + (panelOpen ? "P" : "-"));
                    if (d >= 0.50) strongFrames++;
                    // A clear transition: stop early after a couple of strong frames.
                    if (strongFrames >= 2) break;
                }
                finally { f.Dispose(); }
            }
            Raise(at + "observed " + string.Join(" ", trace.ToArray()) + " (max change " + maxDiff.ToString("0.00", CultureInfo.InvariantCulture) + ", panel " + (panelOpen ? "open" : "closed") + ")");

            // Close what is left of the panel (never leave the game with an open panel).
            if (panelOpen)
            {
                Sleep(600, ct);
                ClosePanel(hwnd, null);
            }
            panelOpenedByUs = false;

            AttemptResult ar = new AttemptResult();
            if (maxDiff >= 0.50 || (!panelOpen && maxDiff >= 0.25))
            {
                ar.Result = new TeleportResult(TeleportStatus.Success, "Teleported (the scene changed clearly).");
            }
            else if (!panelOpen)
            {
                ar.Result = new TeleportResult(TeleportStatus.Likely, "Clicked Teleport and the friends panel closed, but the scene did not change clearly; could not confirm.");
            }
            else if (maxDiff >= 0.20)
            {
                ar.Result = new TeleportResult(TeleportStatus.Likely, "Clicked Teleport; the scene changed somewhat but the panel stayed open; could not confirm.");
            }
            else if (maxDiff < 0.08)
            {
                string msg = "Teleport didn't happen: the Teleport button was clicked but the friends panel stayed open and the scene did not change.";
                if (lastFrame != null)
                {
                    FriendsPanelInfo lp = TeleportDetector.FindPanel(lastFrame, true);
                    string folder = InputDiagnostics.WriteTeleportEvidence("teleport-noeffect", msg, hwnd, lastFrame, lp ?? panel, username, clickPoint, LastClick);
                    if (folder != null) { Raise(at + "evidence saved to " + folder); msg += " Evidence saved to " + folder + "."; }
                }
                ar.Result = new TeleportResult(TeleportStatus.Failed, msg);
                ar.Retryable = true;
            }
            else
            {
                ar.Result = new TeleportResult(TeleportStatus.Likely, "Clicked Teleport; could not confirm.");
            }
            if (lastFrame != null) lastFrame.Dispose();
            return ar;
        }

        // Forces Roblox to refresh a stuck list: click the Nearby tab, then the Friends tab (found by colour in the header band).
        private bool RecoverList(string at, IntPtr hwnd, CancellationToken ct)
        {
            RaiseStatus("Friends list still loading, refreshing it (Nearby, then Friends)...");
            string m;
            Bitmap f = Capture(hwnd, out m);
            if (f == null) return false;
            TeleportDetector.PanelTabs tabs;
            try { FriendsPanelInfo p = TeleportDetector.FindPanel(f, true); tabs = p == null ? null : TeleportDetector.FindTabs(f, p); }
            finally { f.Dispose(); }
            if (tabs == null) { Raise(at + "recovery: Friends/Nearby tabs not found, cannot refresh"); return false; }
            Raise(at + "recovery: still loading, clicking the Nearby tab, then the Friends tab");
            Point np = tabs.NearbyClick;
            DoClick(hwnd, np.X, np.Y, null, null);
            Sleep(900, ct);
            Bitmap f2 = Capture(hwnd, out m);
            if (f2 != null)
            {
                try
                {
                    FriendsPanelInfo p2 = TeleportDetector.FindPanel(f2, true);
                    TeleportDetector.PanelTabs t2 = p2 == null ? null : TeleportDetector.FindTabs(f2, p2);
                    if (t2 != null) tabs = t2;
                }
                finally { f2.Dispose(); }
            }
            Point fp = tabs.FriendsClick;
            DoClick(hwnd, fp.X, fp.Y, null, null);
            Sleep(600, ct);
            return true;
        }

        // Closes the FRIENDS panel with its red X if it is open. Safe to call at any time.
        private void ClosePanel(IntPtr hwnd, FriendsPanelInfo known)
        {
            for (int i = 0; i < 2; i++)
            {
                string method;
                Bitmap f = Capture(hwnd, out method);
                if (f == null) return;
                FriendsPanelInfo p;
                try { p = TeleportDetector.FindPanel(f, true); }
                finally { f.Dispose(); }
                if (p == null) return;
                if (!p.HasCloseButton) return;
                Rectangle x = p.CloseButton;
                DoClick(hwnd, x.X + x.Width / 2, x.Y + x.Height / 2, null, null);
                Thread.Sleep(450);
            }
        }

        // ------------------------------------------------------------------------------------
        // Input
        // ------------------------------------------------------------------------------------

        private static Bitmap Capture(IntPtr hwnd, out string method)
        {
            return CashOutDetector.CaptureWindow(hwnd, out method);
        }

        /// <summary>Evidence of the most recent click (tests and diagnostics read it).</summary>
        public ClickReport LastClick;

        // One click through InputSim (verified focus, real mouse movement, hover, held press, covered-window check).
        // 'order' null = the configured click method. Under ClickHook (tests) the hook delivers the click instead.
        private ClickReport DoClick(IntPtr hwnd, int cx, int cy, Func<bool> verify, ClickTech[] order)
        {
            ClickReport rep;
            if (ClickHook != null)
            {
                rep = new ClickReport();
                rep.Trace = new InputTrace();
                rep.ClientX = cx; rep.ClientY = cy;
                rep.VerifyRequested = verify != null;
                rep.Delivered = ClickHook(hwnd, cx, cy);
                if (!rep.Delivered) { rep.ErrorCode = "covered"; rep.Error = "The click was not delivered."; }
                else if (verify != null)
                {
                    rep.Confirmed = verify();
                    if (!rep.Confirmed) { rep.ErrorCode = "no-change"; rep.Error = "Click did not register."; }
                }
            }
            else
            {
                ClickOptions o = new ClickOptions();
                o.Verify = verify;
                o.RestoreFocus = false;   // the whole flow keeps the game in front; RunLocked restores the previous window at the end
                o.Trace = new InputTrace();
                rep = InputSim.ClickClient(hwnd, cx, cy, order ?? InputSim.OrderFor(InputSim.Method), o);
                // The cursor did not follow: usually the user moved the mouse at that moment. Nothing was clicked, so retry (twice).
                for (int cr = 0; cr < 2 && !rep.Delivered && rep.ErrorCode == "cursor"; cr++)
                {
                    Raise("cursor did not follow (mouse moved?), retrying the click");
                    Thread.Sleep(700);
                    o = new ClickOptions();
                    o.Verify = verify;
                    o.RestoreFocus = false;
                    o.Trace = new InputTrace();
                    rep = InputSim.ClickClient(hwnd, cx, cy, order ?? InputSim.OrderFor(InputSim.Method), o);
                }
            }
            LastClick = rep;
            return rep;
        }

        // Specific wording for a click that could not be delivered at all.
        private static string DeliveryFailText(ClickReport rep, string what)
        {
            switch (rep.ErrorCode)
            {
                case "covered": return "The game window is covered by another window, so " + what + " was not clicked. " + rep.Error;
                case "focus": return "Windows would not bring the game window to the front, so " + what + " was not clicked. " + rep.Error;
                case "cursor": return "The mouse cursor could not be moved onto " + what + " (locked screen, or another program controls the mouse).";
                case "minimised": return "The game window is minimised and could not be restored, so " + what + " was not clicked.";
                case "sendinput": return "Windows rejected the mouse input for " + what + ". " + rep.Error;
                case "outside": return "The click point for " + what + " is outside the game window.";
                default: return "Could not click " + what + ": " + rep.Error;
            }
        }
        // Mouse wheel over the list (3 notches down).
        private void ScrollList(IntPtr hwnd, FriendsPanelInfo panel, CancellationToken ct)
        {
            if (ScrollHook != null) { ScrollHook(hwnd, panel); return; }
            if (ClickHook != null) return;
            TpNative.POINT saved;
            TpNative.GetCursorPos(out saved);
            TpNative.POINT p = new TpNative.POINT();
            p.X = panel.Bounds.X + panel.Bounds.Width / 3;
            p.Y = panel.Bounds.Y + panel.Bounds.Height * 3 / 5;
            TpNative.ClientToScreen(hwnd, ref p);
            try
            {
                TpNative.SetCursorPos(p.X, p.Y);
                Thread.Sleep(90);
                IntPtr under = TpNative.WindowFromPoint(p);
                if (under == IntPtr.Zero || TpNative.GetAncestor(under, 2) != TpNative.GetAncestor(hwnd, 2)) return;
                TpNative.SendWheel(-120 * 3);
                Thread.Sleep(80);
            }
            finally
            {
                TpNative.SetCursorPos(saved.X, saved.Y);
            }
        }

        private void RaiseStatus(string text)
        {
            Action<string> h = Status;
            if (h == null) return;
            try { h(text); } catch (Exception) { }
        }

        private void Raise(string message)
        {
            Action<string> h = Log;
            if (h == null) return;
            try { h("[Teleport] " + message); }
            catch (Exception) { }
        }

        private static class TpNative
        {
            [StructLayout(LayoutKind.Sequential)]
            public struct POINT { public int X; public int Y; }

            [StructLayout(LayoutKind.Sequential)]
            private struct MOUSEINPUT
            {
                public int dx;
                public int dy;
                public uint mouseData;
                public uint dwFlags;
                public uint time;
                public IntPtr dwExtraInfo;
            }

            [StructLayout(LayoutKind.Sequential)]
            private struct INPUT
            {
                public uint type;
                public MOUSEINPUT mi;
            }

            [DllImport("user32.dll")] public static extern bool IsWindow(IntPtr h);
            [DllImport("user32.dll")] public static extern bool IsWindowVisible(IntPtr h);
            [DllImport("user32.dll")] public static extern bool IsIconic(IntPtr h);
            [DllImport("user32.dll")] public static extern bool ShowWindow(IntPtr h, int cmd);
            [DllImport("user32.dll")] public static extern IntPtr GetForegroundWindow();
            [DllImport("user32.dll")] public static extern bool GetCursorPos(out POINT p);
            [DllImport("user32.dll")] public static extern bool SetCursorPos(int x, int y);
            [DllImport("user32.dll")] public static extern bool ClientToScreen(IntPtr h, ref POINT p);
            [DllImport("user32.dll")] public static extern IntPtr WindowFromPoint(POINT p);
            [DllImport("user32.dll")] public static extern IntPtr GetAncestor(IntPtr h, uint flags);
            [DllImport("user32.dll", SetLastError = true)] private static extern uint SendInput(uint n, INPUT[] inputs, int size);

            public static void SendWheel(int delta)
            {
                INPUT[] i = new INPUT[1];
                i[0].type = 0;
                i[0].mi.dwFlags = 0x0800;   // MOUSEEVENTF_WHEEL
                i[0].mi.mouseData = unchecked((uint)delta);
                SendInput(1, i, Marshal.SizeOf(typeof(INPUT)));
            }
        }
    }
}
