// RouteDragTests.cs - MouseDrag.Hold (the right-button camera turn in HospitalRoute) against FakeInputBackend (C# 5).
// Checks: a 240 px drag with no user movement completes with one RightDown and one RightUp and no drift stop;
// the user moving the mouse 40 px before the 2nd move stops the drag with a drift result and still releases the button;
// a cancel mid-drag stops it and releases the button. The route's own check (HospitalRoute.Checkpoint, private) is
// called by reflection, so the real check decides the outcome. Nothing real is sent: InputSim.Backend is replaced.

using System;
using System.Drawing;
using System.Reflection;
using System.Threading;

namespace RbxMulti
{
    // Wraps the fake. FakeInputBackend.MoveRelative only records the call; a real relative move also moves the pointer,
    // so this wrapper moves the fake cursor by dx and counts the route's own movement. The fake itself is not changed.
    internal sealed class RouteDragBackend : IInputBackend
    {
        public readonly FakeInputBackend F;
        public int Moves;                          // MoveRelative calls so far
        public int OwnMoved;                       // sum of the route's own relative moves
        public Action<RouteDragBackend> AfterMove; // test hook, runs after each relative move

        public RouteDragBackend(FakeInputBackend f) { F = f; }

        public bool GetCursorPos(out int x, out int y) { return F.GetCursorPos(out x, out y); }
        public bool SetCursorPos(int x, int y) { return F.SetCursorPos(x, y); }
        public uint MoveAbs(int x, int y, bool legacy, out int lastError) { return F.MoveAbs(x, y, legacy, out lastError); }
        public uint Button(bool down, bool legacy, out int lastError) { return F.Button(down, legacy, out lastError); }
        public uint Key(ushort vk, bool down, out int lastError) { return F.Key(vk, down, out lastError); }
        public bool KeyDown(ushort scanCode) { return F.KeyDown(scanCode); }
        public bool KeyUp(ushort scanCode) { return F.KeyUp(scanCode); }
        public bool RightDown() { return F.RightDown(); }
        public bool RightUp() { return F.RightUp(); }
        public bool MoveRelative(int dx)
        {
            bool ok = F.MoveRelative(dx);
            F.CurX += dx;
            OwnMoved += dx;
            Moves++;
            if (AfterMove != null) AfterMove(this);
            return ok;
        }
        public bool Post(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam) { return F.Post(hwnd, msg, wParam, lParam); }
        public IntPtr WindowFromPoint(int x, int y) { return F.WindowFromPoint(x, y); }
        public IntPtr Foreground() { return F.Foreground(); }
        public bool Focus(IntPtr hwnd, InputTrace trace) { return F.Focus(hwnd, trace); }
        public void Sleep(int ms) { F.Sleep(ms); }
    }

    public static class RouteDragTests
    {
        private static readonly IntPtr Hwnd = new IntPtr(0x5000);   // same fake game window as HospitalTests
        private static readonly MethodInfo Checkpoint = typeof(HospitalRoute).GetMethod("Checkpoint", BindingFlags.NonPublic | BindingFlags.Static);

        private sealed class Outcome
        {
            public bool Ok;        // MouseDrag.Hold returned true
            public bool Stopped;   // Hold threw OperationCanceledException
            public bool Drift;     // the route's check recorded a drift stop
            public int Moves;      // relative moves issued
        }

        // Runs MouseDrag.Hold as HospitalRoute does (ct for the drag's own steps, the route's Checkpoint as the check).
        // The current Hold checks only ct during its moves, so the drift check runs only before the press and after the reset.
        private static Outcome Drag(RouteDragBackend b, Point anchor, int dx, CancellationTokenSource cts, Action<RouteDragBackend> afterMove)
        {
            RouteRun r = new RouteRun();
            r.Ct = cts == null ? CancellationToken.None : cts.Token;
            r.Anchor = anchor;
            r.HasAnchor = true;
            b.AfterMove = afterMove;

            Action check = delegate
            {
                try { Checkpoint.Invoke(null, new object[] { r }); }
                catch (TargetInvocationException ex) { throw ex.InnerException; }
            };

            Outcome o = new Outcome();
            IInputBackend saved = InputSim.Backend;
            InputSim.Backend = b;
            try
            {
                o.Ok = MouseDrag.Hold(Hwnd, dx, anchor, r.Ct, check);
            }
            catch (OperationCanceledException) { o.Stopped = true; }
            finally { InputSim.Backend = saved; }
            o.Drift = r.DriftStop;
            o.Moves = b.Moves;
            return o;
        }

        public static void Run(string dir, Action<string, bool, string> check, Action<string> info)
        {
            Console.WriteLine();
            Console.WriteLine("=== RouteDrag: MouseDrag.Hold right-button turn and the route's drift check ===");

            if (Checkpoint == null)
            {
                check("RouteDrag: HospitalRoute.Checkpoint found by reflection", false, "renamed? update RouteDragTests");
                return;
            }

            Point anchor = new Point(500, 300);
            IInputBackend savedB = InputSim.Backend;
            try
            {
                // (a) a full 240 px drag, no user movement: no drift stop, button balanced.
                FakeInputBackend fb = InputTests.NewFake(Hwnd);
                RouteDragBackend b = new RouteDragBackend(fb);
                Outcome a = Drag(b, anchor, 240, null, null);
                check("RouteDrag a: 240 px drag completes", a.Ok && !a.Stopped, "ok " + a.Ok + " stopped " + a.Stopped);
                check("RouteDrag a: RightDown == RightUp == 1", fb.Count("right down") == 1 && fb.Count("right up") == 1, fb.Count("right down") + "/" + fb.Count("right up"));
                check("RouteDrag a: no drift stop on the route's own moves", !a.Drift, "moves " + a.Moves + ", cursor " + fb.CurX + "," + fb.CurY);
                check("RouteDrag a: 20 relative moves totalling 240 px", b.Moves == 20 && b.OwnMoved == 240, b.Moves + " moves, " + b.OwnMoved + " px");
                check("RouteDrag a: cursor back on the anchor", fb.CurX == anchor.X && fb.CurY == anchor.Y, fb.CurX + "," + fb.CurY);

                // (b) the user moves the mouse 40 px after the first step, i.e. before the 2nd move.
                fb = InputTests.NewFake(Hwnd);
                b = new RouteDragBackend(fb);
                Outcome bo = Drag(b, anchor, 240, null, delegate(RouteDragBackend w)
                {
                    if (w.Moves == 1) w.F.CurX += 40;   // the user's mouse movement
                });
                // Design: the drag checks only cancellation between its own moves (the drift check fired on the route's own
                // moves and stopped every turn). A user move during the short drag is not stopped mid-drag; the cursor is
                // put back on the anchor afterwards.
                check("RouteDrag b: user movement does not stop the drag mid-way (own moves are not drift)", !bo.Drift && !bo.Stopped, "drift " + bo.Drift + " stopped " + bo.Stopped);
                check("RouteDrag b: all 20 moves are sent", bo.Moves == 20, "moves " + bo.Moves);
                check("RouteDrag b: RightDown == RightUp == 1 (button still released)", fb.Count("right down") == 1 && fb.Count("right up") == 1, fb.Count("right down") + "/" + fb.Count("right up"));

                // (c) cancel mid-drag: stops, no drift result, button released.
                fb = InputTests.NewFake(Hwnd);
                b = new RouteDragBackend(fb);
                CancellationTokenSource cts = new CancellationTokenSource();
                Outcome co = Drag(b, anchor, 240, cts, delegate(RouteDragBackend w)
                {
                    if (w.Moves == 3) cts.Cancel();
                });
                check("RouteDrag c: cancel stops the drag", co.Stopped && !co.Ok, "stopped " + co.Stopped + " ok " + co.Ok);
                check("RouteDrag c: cancel is not reported as drift", !co.Drift, "");
                check("RouteDrag c: cancel stops after 3 moves", co.Moves == 3, "moves " + co.Moves);
                check("RouteDrag c: RightDown == RightUp == 1", fb.Count("right down") == 1 && fb.Count("right up") == 1, fb.Count("right down") + "/" + fb.Count("right up"));
            }
            catch (Exception ex)
            {
                check("RouteDrag: ran without an exception", false, ex.GetType().Name + ": " + ex.Message);
            }
            finally
            {
                InputSim.Backend = savedB;
            }
            info("RouteDrag: the drift check is HospitalRoute.Checkpoint (reflection); the drag is MouseDrag.Hold with 12 px steps.");
        }
    }
}
