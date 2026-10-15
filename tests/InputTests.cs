// InputTests.cs - tests for the shared input layer (InputSim), the keep-alive and the failure evidence (C# 5).
// Everything here runs against FakeInputBackend (InputSim.Backend is replaced), so NO real mouse or keyboard input is
// sent and nothing outside the throw-away test windows can be touched. The fake mimics a game that ignores instant
// clicks: a click only registers when real mouse-move events (hover) precede it and the button is held >= 50 ms.
// Optional real-input check (SendInput on the throw-away window) runs only when the session can really move the cursor.

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Windows.Forms;

namespace RbxMulti
{
    internal sealed class FakeInputBackend : IInputBackend
    {
        public int CurX = 5, CurY = 5;
        public long Now;
        public IntPtr Fg = IntPtr.Zero;
        public IntPtr Target;                 // the game window
        public IntPtr Under = IntPtr.Zero;    // what WindowFromPoint returns (Zero = Target)
        public bool FocusWorks = true, RequireHover = true, IgnoreSendInput, IgnoreLegacy, AcceptPost = true, CursorLocked, SendInputBlocked, RealSleep;
        public readonly List<string> Events = new List<string>();
        public int Registered;

        private struct Mv { public long T; public int X, Y; public bool Legacy; }
        private readonly List<Mv> _moves = new List<Mv>();
        private long _downAt = -1;
        private bool _downLegacy;

        [DllImport("user32.dll")] private static extern bool PostMessage(IntPtr h, uint msg, IntPtr w, IntPtr l);
        [DllImport("user32.dll")] private static extern bool ScreenToClient(IntPtr h, ref Point p);

        private void Ev(string s) { lock (Events) { Events.Add("T" + Thread.CurrentThread.ManagedThreadId + ":" + s); } }
        public string[] Snapshot() { lock (Events) { return Events.ToArray(); } }
        public int Count(string contains) { int n = 0; foreach (string e in Snapshot()) if (e.IndexOf(contains, StringComparison.Ordinal) >= 0) n++; return n; }
        public long HoldMs;                   // measured LEFTDOWN -> LEFTUP of the last click (virtual ms)

        public bool GetCursorPos(out int x, out int y) { x = CurX; y = CurY; return true; }
        public bool SetCursorPos(int x, int y)
        {
            Ev("SetCursorPos " + x + "," + y);
            if (CursorLocked) return false;
            CurX = x; CurY = y; return true;
        }
        public uint MoveAbs(int x, int y, bool legacy, out int le)
        {
            le = 0;
            Ev((legacy ? "mouse_event" : "SendInput") + " move " + x + "," + y);
            if (SendInputBlocked && !legacy) { le = 5; return 0; }
            if (!CursorLocked) { CurX = x; CurY = y; }
            Mv m; m.T = Now; m.X = x; m.Y = y; m.Legacy = legacy; _moves.Add(m);
            return 1;
        }
        public uint Button(bool down, bool legacy, out int le)
        {
            le = 0;
            Ev((legacy ? "mouse_event" : "SendInput") + (down ? " LEFTDOWN" : " LEFTUP") + " t=" + Now);
            if (SendInputBlocked && !legacy) { le = 5; return 0; }
            if (down) { _downAt = Now; _downLegacy = legacy; return 1; }
            if (_downAt < 0) return 1;
            HoldMs = Now - _downAt;
            bool apiOk = _downLegacy ? !IgnoreLegacy : !IgnoreSendInput;
            bool hover = false;
            if (RequireHover)
            {
                int distinct = 0; int lx = int.MinValue, ly = int.MinValue;
                foreach (Mv m in _moves)
                {
                    if (m.Legacy != _downLegacy || m.T > _downAt || _downAt - m.T > 700) continue;
                    if (m.X != lx || m.Y != ly) distinct++;
                    lx = m.X; ly = m.Y;
                }
                hover = distinct >= 2 && Math.Abs(lx - CurX) <= 3 && Math.Abs(ly - CurY) <= 3;
            }
            else hover = true;
            bool held = !RequireHover || HoldMs >= 50;
            bool focused = Fg == Target;
            _downAt = -1;
            if (apiOk && hover && held && focused)
            {
                Registered++;
                Ev("GAME REGISTERED CLICK at " + CurX + "," + CurY);
                Point p = new Point(CurX, CurY); ScreenToClient(Target, ref p);
                IntPtr lp = new IntPtr((p.Y << 16) | (p.X & 0xFFFF));
                PostMessage(Target, 0x201, new IntPtr(1), lp); Thread.Sleep(20);
                PostMessage(Target, 0x202, IntPtr.Zero, lp);
            }
            else Ev("game ignored the click (api " + apiOk + ", hover " + hover + ", held " + held + ", focused " + focused + ")");
            return 1;
        }
        public uint Key(ushort vk, bool down, out int le) { le = 0; Ev("key 0x" + vk.ToString("X") + (down ? " down" : " up")); return 1; }
        public bool KeyDown(ushort scan) { Ev("key scan 0x" + scan.ToString("X") + " down"); return true; }
        public bool KeyUp(ushort scan) { Ev("key scan 0x" + scan.ToString("X") + " up"); return true; }
        public bool RightDown() { Ev("right down"); return true; }
        public bool RightUp() { Ev("right up"); return true; }
        public bool MoveRelative(int dx) { Ev("rel move " + dx); return true; }
        public bool Post(IntPtr hwnd, uint msg, IntPtr w, IntPtr l)
        {
            Ev("Post 0x" + msg.ToString("X"));
            if (AcceptPost && (msg == 0x201 || msg == 0x202)) PostMessage(hwnd, msg, w, l);
            if (AcceptPost && msg == 0x201) Registered++;
            return true;
        }
        public IntPtr WindowFromPoint(int x, int y) { return Under != IntPtr.Zero ? Under : Target; }
        public IntPtr Foreground() { return Fg; }
        public bool Focus(IntPtr hwnd, InputTrace t)
        {
            Ev("Focus");
            if (FocusWorks) { Fg = hwnd; return true; }
            t.Add("fake: focus refused");
            return false;
        }
        public void Sleep(int ms) { Now += ms; if (RealSleep) Thread.Sleep(2); }
    }

    public static class InputTests
    {
        private sealed class ClickForm : Form
        {
            public int Clicks;
            public ClickForm()
            {
                Text = "InputTestForm_SECRET_TITLE";
                TopMost = true;
                StartPosition = FormStartPosition.Manual;
                Location = new Point(120, 120);
                ClientSize = new Size(640, 400);
            }
            protected override void OnMouseDown(MouseEventArgs e) { Clicks++; base.OnMouseDown(e); }
        }

        [DllImport("user32.dll")] private static extern bool GetCursorPos(out Point p);
        [DllImport("user32.dll")] private static extern bool SetCursorPos(int x, int y);

        public static void Run(Action<string, bool, string> check, Action<string> info)
        {
            Console.WriteLine();
            Console.WriteLine("=== Part 3: InputSim / keep-alive / evidence (fake input backend, no real input) ===");
            ClickForm form = null;
            ManualResetEvent ready = new ManualResetEvent(false);
            Thread ui = new Thread(delegate()
            {
                Application.EnableVisualStyles();
                form = new ClickForm();
                form.Shown += delegate { ready.Set(); };
                Application.Run(form);
            });
            ui.SetApartmentState(ApartmentState.STA); ui.IsBackground = true; ui.Start();
            ready.WaitOne(10000); Thread.Sleep(300);
            IntPtr hwnd = form.Handle;

            IInputBackend saved = InputSim.Backend;
            ClickMethod savedMethod = InputSim.Method;
            string diagRoot = Path.Combine(Path.GetTempPath(), "rbx_diag_test_" + Guid.NewGuid().ToString("N"));
            string savedRootOverride = InputDiagnostics.RootOverride;
            InputDiagnostics.RootOverride = diagRoot;
            try
            {
                // --- pure maths
                int nx, ny;
                InputSim.NormalizeAbs(0, 0, 0, 0, 1920, 1080, out nx, out ny);
                check("normalise: top-left -> 0,0", nx == 0 && ny == 0, nx + "," + ny);
                InputSim.NormalizeAbs(1919, 1079, 0, 0, 1920, 1080, out nx, out ny);
                check("normalise: bottom-right -> 65535,65535", nx == 65535 && ny == 65535, nx + "," + ny);
                InputSim.NormalizeAbs(-1920, 0, -1920, 0, 3840, 1080, out nx, out ny);
                check("normalise: virtual desktop with a monitor left of the primary (origin -1920) -> 0", nx == 0, nx.ToString());
                InputSim.NormalizeAbs(0, 540, -1920, 0, 3840, 1080, out nx, out ny);
                check("normalise: primary origin on a 3840 wide desktop starting at -1920 -> about half", Math.Abs(nx - 32768) <= 20 && Math.Abs(ny - 32800) <= 80, nx + "," + ny);
                InputSim.NormalizeAbs(5000, 5000, 0, 0, 1920, 1080, out nx, out ny);
                check("normalise: clamps outside values", nx == 65535 && ny == 65535, nx + "," + ny);

                FakeInputBackend fb = NewFake(hwnd);
                InputSim.Backend = fb;
                InputSim.Method = ClickMethod.Auto;

                // --- sanity of the fake: the OLD behaviour (instant click, no movement) is ignored by the strict game
                int e0;
                fb.SetCursorPos(400, 300);
                fb.Button(true, false, out e0); fb.Button(false, false, out e0);
                check("fake game ignores an instant click without movement/hold (the old behaviour)", fb.Registered == 0, "registered " + fb.Registered);

                // --- 1. human-like click registers, with movement, hover, hold, separate calls, cursor restored
                fb = NewFake(hwnd); InputSim.Backend = fb;
                int clicksBefore = form.Clicks;
                Rectangle wr = ScreenRect(hwnd);
                ClickReport r = InputSim.ClickClient(hwnd, 320, 200, new ClickOptions { Verify = delegate { return fb.Registered > 0; } });
                Thread.Sleep(200);
                check("human click: delivered, confirmed, SendInput", r.Ok && r.Confirmed && r.TechUsed == ClickTech.SendInput, r.Error + " | " + r.AttemptsText());
                check("human click: the game form received exactly one WM_LBUTTONDOWN", form.Clicks - clicksBefore == 1, (form.Clicks - clicksBefore).ToString());
                check("human click: >= 3 real move events before the press", fb.Count("SendInput move") >= 3, fb.Count("SendInput move") + " moves");
                check("human click: LEFTDOWN and LEFTUP are separate calls held " + fb.HoldMs + " ms (>= 60)", fb.Count("LEFTDOWN") == 1 && fb.Count("LEFTUP") == 1 && fb.HoldMs >= 60 && fb.HoldMs <= 120, fb.HoldMs.ToString());
                check("human click: cursor restored to where it was (5,5)", fb.CurX == 5 && fb.CurY == 5, fb.CurX + "," + fb.CurY);
                check("human click: screen point = client origin + (320,200)", r.ScreenX == ClientOrigin(hwnd).X + 320 && r.ScreenY == ClientOrigin(hwnd).Y + 200, r.ScreenX + "," + r.ScreenY);
                check("human click: trace has cursor readbacks and WindowFromPoint", r.Trace.ToString().Contains("cursor readback") && r.Trace.ToString().Contains("WindowFromPoint"), "");

                // --- 2. Auto falls back: game ignores SendInput but accepts mouse_event
                fb = NewFake(hwnd); fb.IgnoreSendInput = true; InputSim.Backend = fb; InputSim.Method = ClickMethod.Auto;
                r = InputSim.ClickClient(hwnd, 320, 200, new ClickOptions { Verify = delegate { return fb.Registered > 0; } });
                check("Auto: SendInput sent but no change, then mouse_event works", r.Confirmed && r.TechUsed == ClickTech.MouseEvent && r.Attempts.Count == 2 && r.Attempts[0].Delivered && !r.Attempts[0].Changed, r.AttemptsText());
                int regBefore = fb.Registered;
                ClickReport r2 = InputSim.ClickClient(hwnd, 320, 200, new ClickOptions { Verify = delegate { return fb.Registered > regBefore; } });
                check("Auto: remembers the method that worked (mouse_event first next time)", r2.Attempts.Count == 1 && r2.TechUsed == ClickTech.MouseEvent, r2.AttemptsText());
                // fixed SendInput: reports 'did not register'
                InputSim.Method = ClickMethod.SendInput;
                int regB2 = fb.Registered;
                r = InputSim.ClickClient(hwnd, 320, 200, new ClickOptions { Verify = delegate { return fb.Registered > regB2; } });
                check("SendInput only: 'Click did not register' with the method listed", !r.Ok && r.ErrorCode == "no-change" && r.Error.Contains("did not register") && r.Attempts.Count == 1, r.Error);
                // Legacy order: mouse_event then PostMessage
                InputSim.Method = ClickMethod.Legacy;
                fb.IgnoreLegacy = true;
                int regB3 = fb.Registered;
                r = InputSim.ClickClient(hwnd, 320, 200, new ClickOptions { Verify = delegate { return fb.Registered > regB3; } });
                check("Legacy: mouse_event ignored -> PostMessage works", r.Confirmed && r.TechUsed == ClickTech.PostMessage && r.Attempts.Count == 2, r.AttemptsText());

                // --- 3. Auto reaches PostMessage when both mouse APIs are ignored
                fb = NewFake(hwnd); fb.IgnoreSendInput = true; fb.IgnoreLegacy = true; InputSim.Backend = fb; InputSim.Method = ClickMethod.Auto;
                r = InputSim.ClickClient(hwnd, 320, 200, new ClickOptions { Verify = delegate { return fb.Registered > 0; } });
                check("Auto: third fallback PostMessage works", r.Confirmed && r.TechUsed == ClickTech.PostMessage && r.Attempts.Count == 3, r.AttemptsText());

                // --- 4. covered window: no input at all
                fb = NewFake(hwnd); fb.Under = new IntPtr(0x7FFF0); InputSim.Backend = fb; InputSim.Method = ClickMethod.Auto;
                r = InputSim.ClickClient(hwnd, 320, 200, new ClickOptions { Verify = delegate { return fb.Registered > 0; } });
                check("covered window: error 'covered', no LEFTDOWN/Post sent", !r.Delivered && r.ErrorCode == "covered" && fb.Count("LEFTDOWN") == 0 && fb.Count("Post 0x201") == 0, r.Error);

                // --- 5. focus cannot be obtained
                fb = NewFake(hwnd); fb.FocusWorks = false; InputSim.Backend = fb; InputSim.Method = ClickMethod.SendInput;
                r = InputSim.ClickClient(hwnd, 320, 200, new ClickOptions { Verify = delegate { return fb.Registered > 0; } });
                check("focus refused: error 'focus', no input sent", !r.Delivered && r.ErrorCode == "focus" && fb.Count("LEFTDOWN") == 0 && fb.Count("SendInput move") == 0, r.Error);

                // --- 6. cursor cannot be moved (locked screen / other program)
                fb = NewFake(hwnd); fb.CursorLocked = true; InputSim.Backend = fb; InputSim.Method = ClickMethod.SendInput;
                r = InputSim.ClickClient(hwnd, 320, 200, null);
                check("cursor locked: error 'cursor', no click", !r.Delivered && r.ErrorCode == "cursor" && fb.Count("LEFTDOWN") == 0, r.Error);

                // --- 7. SendInput refused (UIPI): return value and GetLastError reported
                fb = NewFake(hwnd); fb.SendInputBlocked = true; InputSim.Backend = fb; InputSim.Method = ClickMethod.SendInput;
                r = InputSim.ClickClient(hwnd, 320, 200, null);
                check("SendInput returns 0: error 'sendinput' mentions GetLastError 5", !r.Delivered && r.ErrorCode == "sendinput" && r.Trace.ToString().Contains("GetLastError 5"), r.Error);

                // --- 8. outside the window
                fb = NewFake(hwnd); InputSim.Backend = fb; InputSim.Method = ClickMethod.SendInput;
                r = InputSim.ClickClient(hwnd, 5000, 5000, null);
                check("point outside the window: refused", !r.Delivered && r.ErrorCode == "outside", r.Error);

                // --- 9. user became active: abort before any input
                r = InputSim.ClickClient(hwnd, 320, 200, new ClickOptions { AbortIf = delegate { return true; } });
                check("user active: aborted with no input", r.ErrorCode == "user-active" && fb.Count("LEFTDOWN") == 0, r.Error);

                // --- 10. minimised window is restored first, clicked, and minimised again
                fb = NewFake(hwnd); InputSim.Backend = fb; InputSim.Method = ClickMethod.SendInput;
                form.Invoke(new Action(delegate { form.WindowState = FormWindowState.Minimized; }));
                Thread.Sleep(300);
                bool wasMin = InputSim.NativeWin.IsIconic(hwnd);
                r = InputSim.ClickClient(hwnd, 320, 200, new ClickOptions { Verify = delegate { return fb.Registered > 0; } });
                Thread.Sleep(300);
                bool minAgain = InputSim.NativeWin.IsIconic(hwnd);
                check("minimised window: restored before clicking, click registered, minimised again afterwards", wasMin && r.Confirmed && minAgain, "wasMin " + wasMin + " confirmed " + r.Confirmed + " minAgain " + minAgain);
                form.Invoke(new Action(delegate { form.WindowState = FormWindowState.Normal; }));
                Thread.Sleep(300);

                // --- 11. Wiggle (keep-alive input)
                fb = NewFake(hwnd); InputSim.Backend = fb; InputSim.Method = ClickMethod.Auto;
                r = InputSim.Wiggle(hwnd, false, null, null);
                check("wiggle: real moves over the window, no button press, no key, cursor restored", r.Delivered && fb.Count("SendInput move") >= 4 && fb.Count("LEFTDOWN") == 0 && fb.Count("key ") == 0 && fb.CurX == 5 && fb.CurY == 5, r.Error + " moves " + fb.Count("SendInput move"));
                { InputSim.NativeWin.RECT wcr; InputSim.NativeWin.GetClientRect(hwnd, out wcr); check("keep-alive targeting: the wiggle point is the client-area centre (a harmless spot, never a UI button) and no PostMessage click was posted", r.ClientX == (wcr.Right - wcr.Left) / 2 && r.ClientY == (wcr.Bottom - wcr.Top) / 2 && fb.Count("Post 0x201") == 0, r.ClientX + "," + r.ClientY); }
                check("wiggle: records confirmed input for the process", InputSim.LastConfirmedUtc(InputSim.NativeWin.PidOf(hwnd)).HasValue, "");
                fb = NewFake(hwnd); InputSim.Backend = fb;
                r = InputSim.Wiggle(hwnd, true, null, null);
                check("wiggle with key tap: Right Ctrl down then up", fb.Count("key 0xA3 down") == 1 && fb.Count("key 0xA3 up") == 1, "");
                fb = NewFake(hwnd); fb.Under = new IntPtr(0x7FFF0); InputSim.Backend = fb;
                r = InputSim.Wiggle(hwnd, false, null, null);
                check("wiggle: window covered -> nothing sent (SendInput, mouse_event; PostMessage skipped too)", !r.Delivered && r.ErrorCode == "covered" && fb.Count("move") <= 1, r.Error);

                // --- 12. sequential: two parallel keep-alives never interleave their input
                fb = NewFake(hwnd); fb.RealSleep = true; InputSim.Backend = fb;
                Thread ta = new Thread(delegate() { InputSim.Wiggle(hwnd, true, null, null); });
                Thread tb = new Thread(delegate() { InputSim.Wiggle(hwnd, true, null, null); });
                ta.Start(); tb.Start(); ta.Join(); tb.Join();
                string[] ev = fb.Snapshot();
                int switches = 0; string prev = null;
                foreach (string e in ev) { string tid = e.Substring(0, e.IndexOf(':')); if (prev != null && tid != prev) switches++; prev = tid; }
                check("two simultaneous keep-alives are serialised by the global ClickLock (1 thread switch only)", switches == 1, "switches " + switches + " of " + ev.Length + " events");

                // --- 13. KeepAliveService
                KeepAliveTests(check, info, hwnd);

                // --- 14. failure evidence
                EvidenceTests(check, info, form, hwnd);

                // --- 15. optional: REAL SendInput on the throw-away window
                InputSim.Backend = saved;
                InputSim.Method = ClickMethod.SendInput;
                bool real = ProbeInput();
                if (real)
                {
                    int b = form.Clicks;
                    ClickReport rr = InputSim.ClickClient(hwnd, 320, 200, new ClickOptions { Verify = delegate { Thread.Sleep(100); return form.Clicks > b; } });
                    check("REAL SendInput on the throw-away window: click registered via human-like sequence", rr.Confirmed, rr.Error + " | " + rr.Trace);
                }
                else info("SKIPPED: real SendInput check - this session cannot move the cursor (SetCursorPos fails). Real-input behaviour is NOT verified here.");
            }
            finally
            {
                InputSim.Backend = saved;
                InputSim.Method = savedMethod;
                InputDiagnostics.RootOverride = savedRootOverride;
                try { Directory.Delete(diagRoot, true); } catch (Exception) { }
                try { form.Invoke(new Action(delegate { form.Close(); })); } catch (Exception) { }
            }
        }

        private static bool ProbeInput()
        {
            Point p0; if (!GetCursorPos(out p0)) return false;
            bool ok = SetCursorPos(p0.X + 1, p0.Y);
            Point p1; GetCursorPos(out p1);
            SetCursorPos(p0.X, p0.Y);
            return ok && p1.X == p0.X + 1;
        }

        internal static FakeInputBackend NewFake(IntPtr hwnd)
        {
            FakeInputBackend f = new FakeInputBackend();
            f.Target = hwnd;
            InputSim.ResetLastGood();
            return f;
        }

        private static Rectangle ScreenRect(IntPtr h)
        {
            InputSim.NativeWin.RECT r; InputSim.NativeWin.GetWindowRect(h, out r);
            return new Rectangle(r.Left, r.Top, r.Right - r.Left, r.Bottom - r.Top);
        }

        private static Point ClientOrigin(IntPtr h)
        {
            InputSim.NativeWin.POINT p = new InputSim.NativeWin.POINT();
            InputSim.NativeWin.ClientToScreen(h, ref p);
            return new Point(p.X, p.Y);
        }

        private static void KeepAliveTests(Action<string, bool, string> check, Action<string> info, IntPtr hwnd)
        {
            KeepAliveService ka = new KeepAliveService();
            ka.IntervalMinutes = 0; check("keep-alive interval clamps up to 1 minute", ka.IntervalMinutes == 1, ka.IntervalMinutes.ToString());
            ka.IntervalMinutes = 99; check("keep-alive interval clamps down to 15 minutes", ka.IntervalMinutes == 15, ka.IntervalMinutes.ToString());
            check("keep-alive default interval is 4 minutes", new KeepAliveService().IntervalMinutes == 4, "");
            check("keep-alive default key tap is off", !new KeepAliveService().TapKey, "");

            List<string> log = new List<string>();
            ka.Log += delegate(string s) { log.Add(s); };
            ka.IntervalMinutes = 4;
            int pid = Process.GetCurrentProcess().Id;
            KeepAliveTarget t = new KeepAliveTarget(); t.Id = 2; t.Pid = pid; t.Label = "alice";
            ka.SetTargets(new KeepAliveTarget[] { t });
            int calls = 0; bool lastKey = true;
            ka.WiggleHook = delegate(IntPtr h, bool key) { calls++; lastKey = key; ClickReport cr = new ClickReport(); cr.Delivered = true; cr.TechUsed = ClickTech.SendInput; return cr; };

            ka.IdleHook = delegate { return 500; };
            string res = ka.RunOnce(t);
            check("keep-alive skipped while the user is active, nothing sent", res.StartsWith("skipped: user") && calls == 0, res);
            ka.IdleHook = delegate { return 60000; };
            res = ka.RunOnce(t);
            check("keep-alive sent when idle; log says 'Keep-alive sent to alice'", res == "sent" && calls == 1 && log.Exists(delegate(string s) { return s.StartsWith("Keep-alive sent to alice"); }), res + " | " + string.Join(";", log.ToArray()));
            KeepAliveStats st = ka.GetStats(pid);
            check("keep-alive stats: last keep-alive time recorded ('just now')", st.LastKeepAliveUtc.HasValue && KeepAliveService.AgoText(st.LastKeepAliveUtc) == "just now", KeepAliveService.AgoText(st.LastKeepAliveUtc));
            ka.TapKey = true; ka.RunOnce(t);
            check("keep-alive passes the key-tap setting through", lastKey, "");
            ka.WiggleHook = delegate(IntPtr h, bool key) { ClickReport cr = new ClickReport(); cr.Delivered = false; cr.ErrorCode = "covered"; cr.Error = "covered by x"; cr.Trace = new InputTrace(); return cr; };
            res = ka.RunOnce(t);
            check("keep-alive failure is reported with the reason", res.StartsWith("failed:") && log.Exists(delegate(string s) { return s.StartsWith("Keep-alive FAILED"); }), res);

            DateTime now = DateTime.UtcNow;
            check("AgoText: 2m ago", KeepAliveService.AgoText(now.AddSeconds(-125)) == "2m ago", KeepAliveService.AgoText(now.AddSeconds(-125)));
            check("AgoText: never", KeepAliveService.AgoText(null) == "never", "");
            KeepAliveStats old = new KeepAliveStats(); old.FirstSeenUtc = now.AddMinutes(-13);
            check("warning threshold: 13 min without confirmed input is >= 12", KeepAliveService.MinutesSinceInput(999999, old) >= KeepAliveService.WarnAfterMinutes, KeepAliveService.MinutesSinceInput(999999, old).ToString());
            KeepAliveStats fresh = new KeepAliveStats(); fresh.FirstSeenUtc = now.AddMinutes(-5);
            check("warning threshold: 5 min is below 12", KeepAliveService.MinutesSinceInput(999998, fresh) < KeepAliveService.WarnAfterMinutes, "");
            InputSim.RecordConfirmed(999997);
            check("confirmed input resets the no-input timer", KeepAliveService.MinutesSinceInput(999997, old) == 0, KeepAliveService.MinutesSinceInput(999997, old).ToString());

            // scheduler: first keep-alive is due soon after a target appears, and a disabled service sends nothing
            KeepAliveService sched = new KeepAliveService();
            int sent = 0;
            sched.WiggleHook = delegate(IntPtr h, bool key) { sent++; ClickReport cr = new ClickReport(); cr.Delivered = true; return cr; };
            sched.IdleHook = delegate { return 60000; };
            sched.Enabled = false;
            sched.SetTargets(new KeepAliveTarget[] { t });
            sched.Start();
            Thread.Sleep(1500);
            check("scheduler: disabled service sends nothing", sent == 0, sent.ToString());
            sched.Stop(); sched.Dispose();
        }

        private static void EvidenceTests(Action<string, bool, string> check, Action<string> info, ClickForm form, IntPtr hwnd)
        {
            try
            {
                FakeInputBackend fb = NewFake(hwnd); fb.Under = new IntPtr(0x7FFF0); InputSim.Backend = fb; InputSim.Method = ClickMethod.SendInput;
                ClickReport rep = InputSim.ClickClient(hwnd, 320, 200, null);
                string folder = InputDiagnostics.WriteFailureEvidence("unittest", "click failed (test)", hwnd, rep, new Rectangle(300, 180, 40, 40));
                check("evidence: folder created with report.txt and failure.png", folder != null && File.Exists(Path.Combine(folder, "report.txt")) && File.Exists(Path.Combine(folder, "failure.png")), folder);
                string txt = folder != null ? File.ReadAllText(Path.Combine(folder, "report.txt")) : "";
                check("evidence: report has click coordinates, trace, error code and the WindowFromPoint process", txt.Contains("Click: client (320,200)") && txt.Contains("WindowFromPoint") && txt.Contains("covered") && txt.Contains("Monitor:"), "");
                check("evidence: report contains no window title", !txt.Contains("SECRET_TITLE"), "");
                string again = InputDiagnostics.WriteFailureEvidence("unittest", "again", hwnd, rep, null);
                check("evidence: rate limited (second call within 20 s writes no new folder, returns the earlier one so messages can still point at it)", again == folder, again);
                string other = InputDiagnostics.WriteFailureEvidence("unittest2", "other kind", hwnd, null, null);
                check("evidence: another kind is written, with a null click report", other != null && File.Exists(Path.Combine(other, "report.txt")), other);
            }
            finally
            {
            }
        }
    }
}

