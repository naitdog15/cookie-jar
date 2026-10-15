// AimSimTests.cs - closed-loop simulation of HospitalAim (DragFor / UpdateSlope) as driven by HospitalRoute.Aim.
// Model: the controller measures the marker error (screen px), asks DragFor for a mouse drag, the simulated camera
// moves the marker by gain * mouse px, and UpdateSlope learns from the measured change. The drag is executed in
// 12 px chunks (as MouseDrag.Hold does). Each (gain, start error) pair must reach |error| <= HospitalAim.Tolerance
// within HospitalRoute.MaxAimSteps measurements. C# 5.
using System;

namespace RbxMulti
{
    public static class AimSimTests
    {
        private const int FrameWidth = 1145;
        private const int ChunkPx = 12;

        private static readonly double[] Gains = { -0.2, -0.5, -1.0, -2.0, 3.0 };
        private static readonly double[] StartErrors = { 400, -250, 90 };

        private struct SimResult
        {
            public bool Converged;
            public int Steps;
            public double MaxAbsErr;
            public double FinalErr;
        }

        // Mirrors the loop in HospitalRoute.Aim (without the screen capture and the mouse).
        private static SimResult Simulate(double gain, double startErr)
        {
            SimResult res = new SimResult();
            double tol = HospitalAim.Tolerance(FrameWidth);
            double err = startErr;
            double slope = HospitalAim.InitialSlope;
            bool havePrev = false;
            double prevErr = 0;
            int prevDrag = 0;
            res.MaxAbsErr = Math.Abs(err);

            for (int i = 1; i <= HospitalRoute.MaxAimSteps; i++)
            {
                // Measurement (the real loop measures once per iteration).
                res.Steps = i;
                res.FinalErr = err;
                if (Math.Abs(err) > res.MaxAbsErr) res.MaxAbsErr = Math.Abs(err);
                if (Math.Abs(err) <= tol)
                {
                    res.Converged = true;
                    return res;
                }

                if (havePrev) slope = HospitalAim.UpdateSlope(slope, prevErr, err, prevDrag);
                int drag = HospitalAim.DragFor(err, slope);

                // Execute the drag in 12 px chunks; the camera moves the marker by gain per mouse px.
                int left = drag;
                int sign = drag < 0 ? -1 : 1;
                while (left != 0)
                {
                    int step = sign * Math.Min(Math.Abs(left), ChunkPx);
                    err += gain * step;
                    left -= step;
                }

                havePrev = true;
                prevErr = res.FinalErr;
                prevDrag = drag;
            }
            res.FinalErr = err;
            if (Math.Abs(err) > res.MaxAbsErr) res.MaxAbsErr = Math.Abs(err);
            return res;
        }

        // Smallest drag whose effect on the error reaches the 1 px noise floor that UpdateSlope ignores.
        private static int MinUsefulDrag(double gain)
        {
            return (int)Math.Ceiling(1.0 / Math.Abs(gain));
        }

        public static void Run(string dir, Action<string, bool, string> check, Action<string> info)
        {
            Console.WriteLine();
            Console.WriteLine("=== AimSim: HospitalAim closed-loop convergence ===");

            double tol = HospitalAim.Tolerance(FrameWidth);
            double halfWidth = FrameWidth / 2.0;
            info("tolerance " + tol.ToString("0.0") + " px, initial slope " + HospitalAim.InitialSlope + ", max steps " + HospitalRoute.MaxAimSteps + ", chunk " + ChunkPx + " px");

            foreach (double gain in Gains)
            {
                foreach (double e0 in StartErrors)
                {
                    SimResult r = Simulate(gain, e0);
                    string name = "aim sim gain " + gain.ToString("0.0") + " start " + e0.ToString("+0;-0;0") + " px: converges";
                    string detail = "steps " + r.Steps + ", final err " + r.FinalErr.ToString("0.0")
                        + ", max |err| " + r.MaxAbsErr.ToString("0")
                        + (r.MaxAbsErr > halfWidth ? " (marker leaves the frame)" : "");
                    check(name, r.Converged, detail);
                }
            }

            foreach (double gain in Gains)
            {
                info("gain " + gain.ToString("0.0") + ": min useful drag " + MinUsefulDrag(gain) + " px (effect >= 1 px; DragFor floor is "
                    + HospitalAim.MinDragPx + " px)");
            }
        }
    }
}
