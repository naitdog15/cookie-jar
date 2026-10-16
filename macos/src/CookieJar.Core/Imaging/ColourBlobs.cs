using System;
using CookieJar.Platform;
using Rectangle = CookieJar.Core.Imaging.ImageRect;
using Point = CookieJar.Core.Imaging.ImagePoint;
using System.Collections.Generic;

namespace CookieJar.Core.Imaging
{
    internal sealed class Blob
    {
        public int Id;
        public int MinX, MinY, MaxX, MaxY;
        public int Count;

        public int W { get { return MaxX - MinX + 1; } }
        public int H { get { return MaxY - MinY + 1; } }
    }

    internal sealed class BlobMap
    {
        public int[] Map;                       // 0 = not in the colour, otherwise the blob id
        public List<Blob> Blobs = new List<Blob>();
    }

    internal delegate bool PixelTest(byte r, byte g, byte b);

    internal static class ColourBlobs
    {
        public static BlobMap Label(byte[] px, int w, int h, PixelTest pred)
        {
            BlobMap res = new BlobMap();
            res.Map = new int[w * h];
            Stack<int> stack = new Stack<int>();

            for (int y0 = 0; y0 < h; y0++)
            {
                for (int x0 = 0; x0 < w; x0++)
                {
                    int start = y0 * w + x0;
                    if (res.Map[start] != 0) continue;
                    if (!Test(px, start, pred)) continue;

                    Blob c = new Blob();
                    c.Id = res.Blobs.Count + 1;
                    c.MinX = x0; c.MaxX = x0; c.MinY = y0; c.MaxY = y0;
                    res.Blobs.Add(c);

                    res.Map[start] = c.Id;
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
                                if (res.Map[nidx] != 0) continue;
                                if (!Test(px, nidx, pred)) continue;
                                res.Map[nidx] = c.Id;
                                stack.Push(nidx);
                            }
                        }
                    }
                }
            }
            return res;
        }

        private static bool Test(byte[] px, int idx, PixelTest pred)
        {
            int o = idx * 4;
            return pred(px[o + 2], px[o + 1], px[o]);
        }

        public static void Hsv(byte r, byte g, byte b, out float h, out float s, out float v)
        {
            int max = Math.Max(r, Math.Max(g, b));
            int min = Math.Min(r, Math.Min(g, b));
            v = max / 255f;
            s = max == 0 ? 0f : (max - min) / (float)max;
            int d = max - min;
            if (d == 0)
            {
                h = 0f;
                return;
            }
            if (max == r) h = 60f * ((g - b) / (float)d);
            else if (max == g) h = 60f * (2f + (b - r) / (float)d);
            else h = 60f * (4f + (r - g) / (float)d);
            if (h < 0f) h += 360f;
        }

        public static bool IsWhite(byte r, byte g, byte b)
        {
            return r > 225 && g > 225 && b > 225;
        }
    }
}
