using System;
using CookieJar.Platform;
using Rectangle = CookieJar.Core.Imaging.ImageRect;
using Point = CookieJar.Core.Imaging.ImagePoint;
using System.Collections.Generic;

namespace CookieJar.Core.Imaging
{
    public static class CashOutDetector
    {
        private const int MinImageW = 200;
        private const int MinImageH = 120;

        // ----------------------------------------------------------------
        // Public: Find
        // ----------------------------------------------------------------
        public static Rectangle? Find(RawImage bmp)
        {
            if (bmp == null || bmp.Width < MinImageW || bmp.Height < MinImageH) return null;
            return FindInPixels(ReadPixels(bmp), bmp.Width, bmp.Height);
        }

        // Copies a 32bppArgb bitmap into a BGRA byte array (stride removed).
        internal static byte[] ReadPixels(RawImage bmp)
        {
            return ImagePixels.Packed(bmp);
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

    }
}
