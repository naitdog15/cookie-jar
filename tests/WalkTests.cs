// WalkTests.cs - the hospital walk (W held, Space jumps, stall detection) and its log: refused key events, the per-poll
// trace, the failure report text. Fake input only (FakeInputBackend, virtual time); no real input is sent. C# 5.
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Imaging;
using System.Threading;

namespace RbxMulti
{
    // Forwards everything to a FakeInputBackend, but can refuse scan-code key-downs the way SendInput does (returns false).
    internal sealed class ScanRefuser : IInputBackend
    {
        public readonly FakeInputBackend F;
        public readonly HashSet<ushort> Refuse = new HashSet<ushort>();
        public int Refused;

        public ScanRefuser(FakeInputBackend f) { F = f; }

        public bool GetCursorPos(out int x, out int y) { return F.GetCursorPos(out x, out y); }
        public bool SetCursorPos(int x, int y) { return F.SetCursorPos(x, y); }
        public uint MoveAbs(int x, int y, bool legacy, out int lastError) { return F.MoveAbs(x, y, legacy, out lastError); }
        public uint Button(bool down, bool legacy, out int lastError) { return F.Button(down, legacy, out lastError); }
        public uint Key(ushort vk, bool down, out int lastError) { return F.Key(vk, down, out lastError); }

        public bool KeyDown(ushort scanCode)
        {
            if (Refuse.Contains(scanCode))
            {
                Refused++;
                InputSim.LastScanError = 5;   // ERROR_ACCESS_DENIED, as UIPI would give
                return false;
            }
            return F.KeyDown(scanCode);
        }
        public bool KeyUp(ushort scanCode) { return F.KeyUp(scanCode); }
        public bool RightDown() { return F.RightDown(); }
        public bool RightUp() { return F.RightUp(); }
        public bool MoveRelative(int dx) { return F.MoveRelative(dx); }
        public bool Post(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam) { return F.Post(hwnd, msg, wParam, lParam); }
        public IntPtr WindowFromPoint(int x, int y) { return F.WindowFromPoint(x, y); }
        public IntPtr Foreground() { return F.Foreground(); }
        public bool Focus(IntPtr hwnd, InputTrace trace) { return F.Focus(hwnd, trace); }
        public void Sleep(int ms) { F.Sleep(ms); }
    }

    public static class WalkTests
    {
        private static Bitmap Solid(Color c)
        {
            Bitmap b = new Bitmap(32, 32, PixelFormat.Format32bppArgb);
            using (Graphics g = Graphics.FromImage(b)) g.Clear(c);
            return b;
        }

        private static WalkEnv Env(IInputBackend input, FakeInputBackend clock, IntPtr hwnd, Point anchor, CancellationToken ct,
            Func<Bitmap> frame, List<string> said)
        {
            clock.CurX = anchor.X;   // the cursor starts on the anchor, as after a turn
            clock.CurY = anchor.Y;
            WalkEnv e = new WalkEnv();
            e.Input = input;
            e.Hwnd = hwnd;
            e.Anchor = anchor;
            e.Ct = ct;
            e.Frame = frame;
            e.Seconds = delegate { return clock.Now / 1000.0; };
            Action<string> say = null;
            if (said != null) say = delegate(string s) { said.Add(s); };
            e.Say = say;
            return e;
        }

        private static int CountLines(string text, string contains)
        {
            int n = 0;
            foreach (string line in text.Split(new string[] { "\n" }, StringSplitOptions.None))
                if (line.IndexOf(contains, StringComparison.Ordinal) >= 0) n++;
            return n;
        }

        private static int CountItems(List<string> items, string contains)
        {
            int n = 0;
            foreach (string s in items) if (s.IndexOf(contains, StringComparison.Ordinal) >= 0) n++;
            return n;
        }

        // Scripted scene: frame 0 is the baseline; frame n (n >= 1) is the frame of poll n-1. A poll is "moving" when the
        // colour flips, "stalled" when it stays the same (SceneDiff 0). OnCall(n) runs when frame n is taken.
        private sealed class Scene
        {
            private readonly bool _defaultMove;
            private readonly bool[] _moves;
            private Color _color = Color.Red;
            private int _calls;
            public Action<int> OnCall;

            public Scene(bool defaultMove, params bool[] moves)
            {
                _defaultMove = defaultMove;
                _moves = moves;
            }

            public Bitmap Next()
            {
                int n = _calls++;
                if (n > 0)
                {
                    int p = n - 1;
                    bool mv = p < _moves.Length ? _moves[p] : _defaultMove;
                    if (mv) _color = _color == Color.Red ? Color.Blue : Color.Red;
                }
                if (OnCall != null) OnCall(n);
                return Solid(_color);
            }
        }

        private static HospitalResult WalkScripted(FakeInputBackend fb, IntPtr hwnd, Point anchor, Scene s)
        {
            return HospitalRoute.WalkForward(Env(fb, fb, hwnd, anchor, CancellationToken.None, s.Next, null));
        }

        // W and Space: every key-down has its key-up, and W went down exactly once.
        private static bool KeysBalanced(FakeInputBackend fb)
        {
            return fb.Count("scan 0x11 down") == fb.Count("scan 0x11 up")
                && fb.Count("scan 0x39 down") == fb.Count("scan 0x39 up");
        }

        private static void ScriptedTests(Action<string, bool, string> check, Action<string> info, IntPtr hwnd, Point anchor)
        {
            // (a) no stalls: the walk runs to the 12 s cap, no Space, W down and up once.
            {
                FakeInputBackend fb = InputTests.NewFake(hwnd);
                HospitalResult r = WalkScripted(fb, hwnd, anchor, new Scene(true));
                check("walk (a) no stalls: ok at the time cap", r.Ok && r.Message.IndexOf("for 12 s", StringComparison.Ordinal) >= 0, r.Message);
                check("walk (a) no stalls: stops at 12.0 s (virtual " + fb.Now + " ms)", fb.Now >= 12000 && fb.Now < 12500, fb.Now.ToString());
                check("walk (a) no stalls: no Space", fb.Count("scan 0x39 down") == 0, "");
                check("walk (a) no stalls: W down once, W up once, balanced", fb.Count("scan 0x11 down") == 1 && fb.Count("scan 0x11 up") == 1 && KeysBalanced(fb), "");
            }

            // (b) stall then movement: exactly one Space, then the walk runs to the cap.
            {
                FakeInputBackend fb = InputTests.NewFake(hwnd);
                HospitalResult r = WalkScripted(fb, hwnd, anchor, new Scene(true, false));
                check("walk (b) stall then movement: ok at the time cap", r.Ok, r.Message);
                check("walk (b) stall then movement: exactly one Space down", fb.Count("scan 0x39 down") == 1, fb.Count("scan 0x39 down").ToString());
                check("walk (b) stall then movement: W released, keys balanced", fb.Count("scan 0x11 up") == 1 && KeysBalanced(fb), "");
                check("walk (b) stall then movement: released at the cap (virtual " + fb.Now + " ms)", fb.Now >= 12000 && fb.Now < 12600, fb.Now.ToString());
            }

            // (c) stall, Space, stall again: blocked straight after the jump; one Space only.
            {
                FakeInputBackend fb = InputTests.NewFake(hwnd);
                HospitalResult r = WalkScripted(fb, hwnd, anchor, new Scene(false, false, false));
                check("walk (c) stall, Space, stall: blocked", !r.Ok && r.Message.StartsWith("The character is blocked", StringComparison.Ordinal), r.Message);
                check("walk (c) stall, Space, stall: exactly one Space", fb.Count("scan 0x39 down") == 1, fb.Count("scan 0x39 down").ToString());
                check("walk (c) stall, Space, stall: W released, keys balanced", fb.Count("scan 0x11 up") == 1 && KeysBalanced(fb), "");
            }

            // (d) jump cap: stall, move, stall, move, stall, move, stall. The current design is one Space per walk
            // (HospitalWalk.MaxJumps = 1), so the second stall, after movement, is blocked by the cap. Spaces = MaxJumps.
            {
                FakeInputBackend fb = InputTests.NewFake(hwnd);
                HospitalResult r = WalkScripted(fb, hwnd, anchor, new Scene(false, false, true, false, true, false, true));
                check("walk (d) jump cap: blocked once the jump budget is used", !r.Ok && r.Message.StartsWith("The character is blocked", StringComparison.Ordinal), r.Message);
                check("walk (d) jump cap: exactly MaxJumps (" + HospitalWalk.MaxJumps + ") Spaces", fb.Count("scan 0x39 down") == HospitalWalk.MaxJumps, fb.Count("scan 0x39 down").ToString());
                check("walk (d) jump cap: W released, keys balanced", fb.Count("scan 0x11 up") == 1 && KeysBalanced(fb), "");
            }

            // (e) focus lost at the first poll: release W, no Space, the focus message.
            {
                FakeInputBackend fb = InputTests.NewFake(hwnd);
                Scene s = new Scene(true);
                s.OnCall = delegate(int n) { if (n == 1) fb.Fg = new IntPtr(0x6666); };
                HospitalResult r = WalkScripted(fb, hwnd, anchor, s);
                check("walk (e) focus lost: stopped with the focus message", !r.Ok && r.Message.IndexOf("lost focus", StringComparison.Ordinal) >= 0, r.Message);
                check("walk (e) focus lost: no Space at all", fb.Count("scan 0x39") == 0, "");
                check("walk (e) focus lost: W released, keys balanced", fb.Count("scan 0x11 down") == 1 && fb.Count("scan 0x11 up") == 1 && KeysBalanced(fb), "");
            }

            // (f) drift: the cursor moves off the anchor at the first poll -> release W, no Space.
            {
                FakeInputBackend fb = InputTests.NewFake(hwnd);
                Scene s = new Scene(true);
                s.OnCall = delegate(int n) { if (n == 1) fb.CurX = anchor.X + 30; };
                HospitalResult r = WalkScripted(fb, hwnd, anchor, s);
                check("walk (f) drift: stopped with the mouse-moved message", !r.Ok && r.Message.IndexOf("mouse was moved", StringComparison.Ordinal) >= 0, r.Message);
                check("walk (f) drift: no Space at all", fb.Count("scan 0x39") == 0, "");
                check("walk (f) drift: W released, keys balanced", fb.Count("scan 0x11 down") == 1 && fb.Count("scan 0x11 up") == 1 && KeysBalanced(fb), "");
            }
            info("walk jump cap: HospitalWalk.MaxJumps = " + HospitalWalk.MaxJumps + " (one Space per walk); a stall straight after the jump releases.");
        }

        public static void Run(string dir, Action<string, bool, string> check, Action<string> info)
        {
            Console.WriteLine();
            Console.WriteLine("=== Hospital walk: key results, per-step log, report trace (fake backend) ===");

            IntPtr hwnd = new IntPtr(0x5000);
            Point anchor = new Point(500, 300);
            IInputBackend savedB = InputSim.Backend;
            try
            {
                // 1. W refused by SendInput: the walk stops with a W message (not a silent 12 s walk), W key-up still sent, no jump.
                FakeInputBackend fb = InputTests.NewFake(hwnd);
                ScanRefuser rf = new ScanRefuser(fb);
                rf.Refuse.Add(0x11);
                int calls = 0;
                Func<Bitmap> moving = delegate { calls++; return Solid(calls % 2 == 0 ? Color.Red : Color.Blue); };
                HospitalResult r1 = HospitalRoute.WalkForward(Env(rf, fb, hwnd, anchor, CancellationToken.None, moving, null));
                check("walk: W refused -> stopped (not reported as a walk)", !r1.Ok && r1.Message.IndexOf("W key was refused", StringComparison.Ordinal) >= 0, r1.Message);
                check("walk: W refused -> no walking time, no jump", fb.Count("scan 0x11 down") == 0 && fb.Count("scan 0x39 down") == 0, "");
                check("walk: W refused -> key-up still sent (balanced)", fb.Count("scan 0x11 up") == 1 && rf.Refused == 1, fb.Count("scan 0x11 up") + " up, refused " + rf.Refused);

                // 2. Space refused by SendInput while blocked: the walk stops with a jump message; W is released.
                fb = InputTests.NewFake(hwnd);
                rf = new ScanRefuser(fb);
                rf.Refuse.Add(0x39);
                HospitalResult r2 = HospitalRoute.WalkForward(Env(rf, fb, hwnd, anchor, CancellationToken.None, delegate { return Solid(Color.Gray); }, null));
                check("walk: Space refused while blocked -> stopped with the jump message", !r2.Ok && r2.Message.IndexOf("jump (Space) was refused", StringComparison.Ordinal) >= 0, r2.Message);
                check("walk: Space refused -> W balanced and Space key-up still sent", fb.Count("scan 0x11 down") == 1 && fb.Count("scan 0x11 up") == 1 && fb.Count("scan 0x39 up") == 1, "");

                // 3. Moving scene, time cap: every poll is logged through Say and the trace, with SceneDiff and the decision.
                fb = InputTests.NewFake(hwnd);
                calls = 0;
                List<string> said = new List<string>();
                moving = delegate { calls++; return Solid(calls % 2 == 0 ? Color.Red : Color.Blue); };
                WalkEnv e3 = Env(fb, fb, hwnd, anchor, CancellationToken.None, moving, said);
                HospitalResult r3 = HospitalRoute.WalkForward(e3);
                string t3 = e3.Trace.ToString();
                int polls3 = CountLines(t3, "walk poll at");
                check("walk trace: moving walk -> success", r3.Ok, r3.Message);
                check("walk trace: one line per poll (about 12 s / 0.5 s)", polls3 >= 20 && polls3 <= 25, polls3 + " poll lines");
                check("walk trace: every poll shows SceneDiff and 'moving'", CountLines(t3, "SceneDiff") == polls3 && CountLines(t3, "(moving)") == polls3, "");
                check("walk trace: says the same poll lines through the log", CountItems(said, "walk poll at") == polls3, CountItems(said, "walk poll at") + " said");
                check("walk trace: W down, W held, W up, the walk decision are all logged",
                    t3.Contains("W key-down sent (scan 0x11)") && t3.Contains("walking forward (W held)") && t3.Contains("W key-up sent")
                    && t3.Contains("walk decision at") && t3.Contains("ReleaseDone"), "");
                check("walk trace: focus before W and the start frame are logged", t3.Contains("focus before W: ok") && t3.Contains("start frame: "), "");

                // 4. Stalled scene: the stalled polls, the jump, the blocked decision and the Space tap are all in the trace.
                fb = InputTests.NewFake(hwnd);
                WalkEnv e4 = Env(fb, fb, hwnd, anchor, CancellationToken.None, delegate { return Solid(Color.Gray); }, null);
                HospitalResult r4 = HospitalRoute.WalkForward(e4);
                string t4 = e4.Trace.ToString();
                check("walk trace: stalled walk -> blocked result", !r4.Ok && r4.Message.StartsWith("The character is blocked", StringComparison.Ordinal), r4.Message);
                check("walk trace: stalled polls show SceneDiff (stalled), then the jump, then ReleaseBlocked",
                    CountLines(t4, "(stalled, below 0.020)") == 2 && t4.Contains("blocked (no scene change), jumping (1/" + HospitalWalk.MaxJumps + ")")
                    && t4.Contains("Space tap: down sent, up sent") && t4.Contains("ReleaseBlocked"), "");

                // 5. A failed capture is logged as "not a stall" and never causes a jump.
                fb = InputTests.NewFake(hwnd);
                WalkEnv e5 = Env(fb, fb, hwnd, anchor, CancellationToken.None, delegate { return null; }, null);
                HospitalResult r5 = HospitalRoute.WalkForward(e5);
                string t5 = e5.Trace.ToString();
                check("walk trace: failed captures are logged as not a stall, no jump", r5.Ok && CountLines(t5, "frame not captured (not a stall)") > 0 && !t5.Contains("jumping"), r5.Message);

                // 6. Focus lost mid-walk: the trace says so, with the stop.
                fb = InputTests.NewFake(hwnd);
                int frames6 = 0;
                Func<Bitmap> loseFocus = delegate
                {
                    frames6++;
                    if (frames6 == 3) fb.Fg = new IntPtr(0x6666);
                    return Solid(frames6 % 2 == 0 ? Color.Red : Color.Blue);
                };
                WalkEnv e6 = Env(fb, fb, hwnd, anchor, CancellationToken.None, loseFocus, null);
                HospitalResult r6 = HospitalRoute.WalkForward(e6);
                check("walk trace: focus lost -> 'focus LOST -> stop' logged", !r6.Ok && e6.Trace.ToString().Contains("focus LOST") && e6.Trace.ToString().Contains("-> stop"), r6.Message);

                // 7. Report text: the walk section is in report.txt; 'not started' when the walk never began.
                check("report: no walk -> 'Walk: not started'", HospitalRoute.WalkReportText(null).Contains("Walk: not started"), "");
                string rep = HospitalRoute.WalkReportText(e3.Trace);
                check("report: walk trace section holds the poll lines", rep.Contains("Walk trace (ms from the walk start):") && CountLines(rep, "walk poll at") == polls3, "");

                // 8. Scripted sequences (a)-(f): time cap, one jump, jump cap, stall after jump, focus loss, drift.
                ScriptedTests(check, info, hwnd, anchor);
            }
            finally
            {
                InputSim.Backend = savedB;
            }
        }
    }
}
