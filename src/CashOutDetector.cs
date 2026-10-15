// CashOutDetector.cs
//
// PUBLIC API (namespace RbxMulti, static class CashOutDetector)
// ----------------------------------------------------------------
//   public static Rectangle? Find(Bitmap bmp);
//       Locates the green "CASH OUT" button in a screenshot of a Roblox (Adopt Me)
//       client area. Returns the button's bounding rectangle in bitmap pixel
//       coordinates, or null when no button is found (or the bitmap is null or
//       smaller than 200x120). Any pixel format is accepted (it is converted to
//       32bppArgb internally). Detection is scale-invariant: every threshold is a
//       fraction of image width/height or a colour/HSV ratio, never a pixel size.
//       The bitmap is not modified and not disposed.
//
//   public static Bitmap CaptureWindow(IntPtr hwnd, out string method);
//       Captures the CLIENT area of a top-level window as a 32bppArgb Bitmap in
//       physical pixels (the app is PerMonitorV2 DPI aware). The caller owns the
//       returned bitmap and must Dispose it. "method" is set to:
//         "PrintWindow"    - PrintWindow(PW_CLIENTONLY | PW_RENDERFULLCONTENT) worked
//                            and the result was not uniform (black/blank).
//         "CopyFromScreen" - PrintWindow failed or returned a uniform image, so the
//                            client rect was copied from the screen instead.
//         "failed"         - returns null: invalid handle, minimised window
//                            (IsIconic), client area under 100px, or an error.
//
// CAPTURE METHOD FOR ROBLOX
//   Roblox renders with DirectX. PrintWindow with PW_RENDERFULLCONTENT (flag 2)
//   normally returns the real frame on Windows 10/11 and works even when the
//   window is covered by other windows. If it returns a black/blank image the
//   code falls back to CopyFromScreen.
//   CAVEAT for CopyFromScreen: it copies whatever is ON SCREEN at that rectangle,
//   so the Roblox window must be visible and not occluded by other windows at the
//   moment of capture. Minimised windows are rejected up front.
//
// This file performs capture and image analysis only. It never sends input,
// never clicks, never reads or writes Roblox process memory.

using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;

namespace RbxMulti
{
    public static class CashOutDetector
    {
        private const int MinImageW = 200;
        private const int MinImageH = 120;

        // ----------------------------------------------------------------
        // Public: Find
        // ----------------------------------------------------------------
        public static Rectangle? Find(Bitmap bmp)
        {
            if (bmp == null) return null;
            if (bmp.Width < MinImageW || bmp.Height < MinImageH) return null;

            Bitmap work = bmp;
            bool disposeWork = false;
            try
            {
                if (bmp.PixelFormat != PixelFormat.Format32bppArgb)
                {
                    work = new Bitmap(bmp.Width, bmp.Height, PixelFormat.Format32bppArgb);
                    using (Graphics g = Graphics.FromImage(work))
                    {
                        g.DrawImage(bmp, 0, 0, bmp.Width, bmp.Height);
                    }
                    disposeWork = true;
                }

                int w = work.Width;
                int h = work.Height;
                byte[] px = ReadPixels(work);
                return FindInPixels(px, w, h);
            }
            finally
            {
                if (disposeWork) work.Dispose();
            }
        }

        // Copies a 32bppArgb bitmap into a BGRA byte array (stride removed).
        internal static byte[] ReadPixels(Bitmap bmp)
        {
            int w = bmp.Width;
            int h = bmp.Height;
            BitmapData d = bmp.LockBits(new Rectangle(0, 0, w, h),
                ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
            try
            {
                byte[] buf = new byte[w * h * 4];
                int rowBytes = w * 4;
                if (d.Stride == rowBytes)
                {
                    Marshal.Copy(d.Scan0, buf, 0, buf.Length);
                }
                else
                {
                    for (int y = 0; y < h; y++)
                    {
                        Marshal.Copy(IntPtr.Add(d.Scan0, y * d.Stride), buf, y * rowBytes, rowBytes);
                    }
                }
                return buf;
            }
            finally
            {
                bmp.UnlockBits(d);
            }
        }

        // Green test in HSV terms: value >= ~0.5, saturation >= 0.45, hue ~95..160 deg.
        private static bool IsButtonGreen(byte r, byte g, byte b)
        {
            int max = Math.Max(r, Math.Max(g, b));
            int min = Math.Min(r, Math.Min(g, b));
            if (max < 128) return false;                       // V < 0.5
            if (g != max) return false;                        // green must dominate
            float s = (max - min) / (float)max;
            if (s < 0.45f) return false;
            int d = max - min;
            if (d == 0) return false;
            // Hue for max == G: 60 * (2 + (B - R) / d)
            float hue = 60f * (2f + (b - r) / (float)d);
            return hue >= 95f && hue <= 160f;
        }

        // White text test: all channels above 225.
        private static bool IsWhite(byte r, byte g, byte b)
        {
            return r > 225 && g > 225 && b > 225;
        }

        private sealed class Component
        {
            public int Id;
            public int MinX, MinY, MaxX, MaxY;
            public int Count;
        }

        private static Rectangle? FindInPixels(byte[] px, int w, int h)
        {
            int total = w * h;
            int[] label = new int[total];      // 0 = not green, otherwise component id
            List<Component> comps = new List<Component>();
            Stack<int> stack = new Stack<int>();

            // Pass 1: label green pixels with 8-connected flood fill (iterative).
            for (int y0 = 0; y0 < h; y0++)
            {
                for (int x0 = 0; x0 < w; x0++)
                {
                    int start = y0 * w + x0;
                    if (label[start] != 0) continue;
                    int o = start * 4;
                    if (!IsButtonGreen(px[o + 2], px[o + 1], px[o])) continue;

                    Component c = new Component();
                    c.Id = comps.Count + 1;
                    c.MinX = x0; c.MaxX = x0; c.MinY = y0; c.MaxY = y0;
                    c.Count = 0;
                    comps.Add(c);

                    label[start] = c.Id;
                    stack.Push(start);
                    while (stack.Count > 0)
                    {
                        int idx = stack.Pop();
                        int x = idx % w;
                        int y = idx / w;
                        c.Count++;
                        if (x < c.MinX) c.MinX = x;
                        if (x > c.MaxX) c.MaxX = x;
                        if (y < c.MinY) c.MinY = y;
                        if (y > c.MaxY) c.MaxY = y;

                        for (int dy = -1; dy <= 1; dy++)
                        {
                            int ny = y + dy;
                            if (ny < 0 || ny >= h) continue;
                            for (int dx = -1; dx <= 1; dx++)
                            {
                                int nx = x + dx;
                                if (nx < 0 || nx >= w) continue;
                                int nidx = ny * w + nx;
                                if (label[nidx] != 0) continue;
                                int no = nidx * 4;
                                if (!IsButtonGreen(px[no + 2], px[no + 1], px[no])) continue;
                                label[nidx] = c.Id;
                                stack.Push(nidx);
                            }
                        }
                    }
                }
            }

            // Pass 2: validate each candidate component.
            Rectangle? best = null;
            double bestScore = double.MinValue;

            foreach (Component c in comps)
            {
                int bw = c.MaxX - c.MinX + 1;
                int bh = c.MaxY - c.MinY + 1;

                // Cheap geometric filters.
                if (bh < 0.025 * h) continue;
                if (bw < 0.12 * w || bw > 0.60 * w) continue;
                float aspect = bw / (float)bh;
                if (aspect < 4.5f || aspect > 10f) continue;

                int area = bw * bh;
                double stripTop = c.MaxY - 0.15 * bh;     // bottom ~15% of the box
                double midLo = c.MinX + 0.15 * bw;        // middle 70% horizontally
                double midHi = c.MinX + 0.85 * bw;

                int filled = 0;
                int white = 0;
                int whiteMid = 0;
                double fillV = 0; int fillN = 0;
                double stripV = 0; int stripN = 0;

                for (int y = c.MinY; y <= c.MaxY; y++)
                {
                    for (int x = c.MinX; x <= c.MaxX; x++)
                    {
                        int idx = y * w + x;
                        int o = idx * 4;
                        byte r = px[o + 2], g = px[o + 1], b = px[o];
                        if (label[idx] == c.Id)
                        {
                            filled++;
                            double v = Math.Max(r, Math.Max(g, b)) / 255.0;
                            fillV += v; fillN++;
                            if (y >= stripTop) { stripV += v; stripN++; }
                        }
                        else if (IsWhite(r, g, b))
                        {
                            filled++;
                            white++;
                            if (x >= midLo && x <= midHi) whiteMid++;
                        }
                    }
                }

                if (fillN == 0 || stripN == 0) continue;
                float fill = filled / (float)area;
                if (fill < 0.55f) continue;

                float whiteFrac = white / (float)area;
                if (whiteFrac < 0.05f || whiteFrac > 0.40f) continue;
                if (whiteMid < 0.9 * white) continue;

                double fillMean = fillV / fillN;
                double stripMean = stripV / stripN;
                if (stripMean > fillMean * 0.92) continue;   // darker bottom edge required

                // Score: closeness to the reference aspect (7.4) plus text presence.
                double aspectScore = 1.0 - Math.Abs(aspect - 7.4) / 7.4;
                if (aspectScore < 0) aspectScore = 0;
                double textScore = Math.Min(1.0, whiteFrac / 0.15);
                double score = 0.7 * aspectScore + 0.3 * textScore;

                if (score > bestScore)
                {
                    bestScore = score;
                    best = new Rectangle(c.MinX, c.MinY, bw, bh);
                }
            }

            return best;
        }

        // ----------------------------------------------------------------
        // Public: CaptureWindow
        // ----------------------------------------------------------------
        public static Bitmap CaptureWindow(IntPtr hwnd, out string method)
        {
            method = "failed";
            try
            {
                if (hwnd == IntPtr.Zero) return null;
                if (!CashOutNative.IsWindow(hwnd)) return null;
                if (CashOutNative.IsIconic(hwnd)) return null;

                CashOutNative.RECT rc;
                if (!CashOutNative.GetClientRect(hwnd, out rc)) return null;
                int w = rc.Right - rc.Left;
                int h = rc.Bottom - rc.Top;
                if (w < 100 || h < 100) return null;

                // 1) PrintWindow of the client area (PW_CLIENTONLY | PW_RENDERFULLCONTENT).
                Bitmap printed = new Bitmap(w, h, PixelFormat.Format32bppArgb);
                bool ok;
                using (Graphics g = Graphics.FromImage(printed))
                {
                    IntPtr hdc = g.GetHdc();
                    try
                    {
                        ok = CashOutNative.PrintWindow(hwnd, hdc, 3u);
                    }
                    finally
                    {
                        g.ReleaseHdc(hdc);
                    }
                }

                if (ok && !IsUniform(printed))
                {
                    method = "PrintWindow";
                    return printed;
                }
                printed.Dispose();

                // 2) Fallback: copy the client rect from the screen.
                CashOutNative.POINT pt = new CashOutNative.POINT();
                pt.X = 0;
                pt.Y = 0;
                if (!CashOutNative.ClientToScreen(hwnd, ref pt)) return null;

                Bitmap screen = new Bitmap(w, h, PixelFormat.Format32bppArgb);
                using (Graphics g = Graphics.FromImage(screen))
                {
                    g.CopyFromScreen(pt.X, pt.Y, 0, 0, new Size(w, h), CopyPixelOperation.SourceCopy);
                }
                method = "CopyFromScreen";
                return screen;
            }
            catch (Exception)
            {
                method = "failed";
                return null;
            }
        }

        // Samples about 200 pixels on a grid; true when all are (nearly) identical.
        private static bool IsUniform(Bitmap bmp)
        {
            int w = bmp.Width;
            int h = bmp.Height;
            BitmapData d = bmp.LockBits(new Rectangle(0, 0, w, h),
                ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
            try
            {
                int step = (int)Math.Max(1, Math.Sqrt((double)(w * h) / 200.0));
                int first = -1;
                bool uniform = true;
                for (int y = 0; y < h && uniform; y += step)
                {
                    for (int x = 0; x < w; x += step)
                    {
                        int v = Marshal.ReadInt32(d.Scan0, y * d.Stride + x * 4);
                        if (first == -1) first = v;
                        else if ((v & 0x00FFFFFF) != (first & 0x00FFFFFF)) { uniform = false; break; }
                    }
                }
                return uniform;
            }
            finally
            {
                bmp.UnlockBits(d);
            }
        }

        private static class CashOutNative
        {
            [StructLayout(LayoutKind.Sequential)]
            public struct RECT
            {
                public int Left;
                public int Top;
                public int Right;
                public int Bottom;
            }

            [StructLayout(LayoutKind.Sequential)]
            public struct POINT
            {
                public int X;
                public int Y;
            }

            [DllImport("user32.dll")]
            public static extern bool IsWindow(IntPtr hWnd);

            [DllImport("user32.dll")]
            public static extern bool IsIconic(IntPtr hWnd);

            [DllImport("user32.dll")]
            public static extern bool GetClientRect(IntPtr hWnd, out RECT lpRect);

            [DllImport("user32.dll")]
            public static extern bool GetWindowRect(IntPtr hWnd, out RECT lpRect);

            [DllImport("user32.dll")]
            public static extern bool ClientToScreen(IntPtr hWnd, ref POINT lpPoint);

            [DllImport("user32.dll")]
            public static extern bool PrintWindow(IntPtr hWnd, IntPtr hdc, uint nFlags);
        }
    }
}
