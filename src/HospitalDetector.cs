// HospitalDetector.cs
//
// Finds the screen elements of the "go to the hospital" route in a Roblox (Adopt Me) client-area bitmap.
// Every element is recognised by its colour structure, never by a plain template crop, so tiling sizes and the
// scene behind the button do not matter. Scale-invariant: thresholds are fractions of the image size or colour ratios.
//
// PUBLIC API (namespace RbxMulti, static class HospitalDetector). All functions return the element's bounding rectangle
// in bitmap pixels, or null. The bitmap is not modified and not disposed.
//   FindBackpackButton(bmp)      the round backpack button at the bottom centre of the window: light blue (blue theme) or pink (pink theme).
//   FindPlusTile(bmp)            the green "+" tile (Gifts pane) with its white plus.
//   FindGiftsTabHighlighted(bmp) the orange, selected Gifts category tile on the left of the backpack window.
//   FindHospitalMarker(bmp)      the red map pin with a red heart in a white disc, above the top bar.
//   FindTeleportYes(bmp)         the green "Yes" of the "Teleport to The Gifts Display?" prompt. Only returned when the
//                                prompt's red button is the two-letter "No" AND the dialog text reads "Gifts Display"
//                                (OCR). Anything else returns null, so an unrelated prompt is never confirmed.
//
// Pure image analysis: no capture, no input.

using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Imaging;

namespace RbxMulti
{
    public static class HospitalDetector
    {
        private const int MinImageW = 200;
        private const int MinImageH = 120;

        // A red button whose label is narrower than this is a "No" (see FindTeleportYes).
        private const double MaxNoLabelWidth = 0.25;

        // ----------------------------------------------------------------
        // Public
        // ----------------------------------------------------------------

        public static Point Centre(Rectangle r)
        {
            return new Point(r.X + r.Width / 2, r.Y + r.Height / 2);
        }

        /// <summary>
        /// Where the backpack pane is, from its "+" tile (reference: pane 501 x 356, its top-left 147 x 76 px left of and above the
        /// "+", the "+" 78 px wide). Scaled with the tile. Used to exclude the pane from the teleport arrival check.
        /// </summary>
        public static Rectangle PaneRect(Rectangle plus)
        {
            double s = plus.Width / 78.0;
            return new Rectangle((int)(plus.X - 147 * s), (int)(plus.Y - 76 * s), (int)(501 * s), (int)(356 * s));
        }

        /// <summary>
        /// Where the "Teleport to The Gifts Display?" box is, from its Yes button (reference: whole window). The box is about
        /// 1.6 button widths left of Yes, 0.6 right, 4 heights above and 1 below. Used to exclude the prompt from the arrival check.
        /// </summary>
        public static Rectangle DialogRect(Rectangle yes)
        {
            int left = (int)(yes.Left - 1.6 * yes.Width);
            int right = (int)(yes.Right + 0.6 * yes.Width);
            int top = (int)(yes.Top - 4.0 * yes.Height);
            int bottom = (int)(yes.Bottom + 1.0 * yes.Height);
            return Rectangle.FromLTRB(left, top, right, bottom);
        }

        // The backpack button comes in two themes: light blue (blue UI) and pink (pink UI). Each colour is labelled on its own,
        // so a disc never merges with a neighbouring button of the other colour; the largest accepted disc wins.
        public static Rectangle? FindBackpackButton(Bitmap bmp)
        {
            return WithPixels(bmp, delegate(byte[] px, int w, int h)
            {
                Rectangle? best = BestBackpackDisc(px, w, h, IsLightBlue);
                Rectangle? pink = BestBackpackDisc(px, w, h, IsPink);
                if (pink.HasValue && (!best.HasValue || pink.Value.Width * pink.Value.Height > best.Value.Width * best.Value.Height))
                    best = pink;
                return best;
            });
        }

        private static Rectangle? BestBackpackDisc(byte[] px, int w, int h, PixelTest discColour)
        {
            Rectangle? best = null;
            int bestArea = 0;
            foreach (Blob b in ColourBlobs.Label(px, w, h, discColour).Blobs)
            {
                int bw = b.W, bh = b.H;
                if (!RoughlySquare(bw, bh, 0.80f, 1.25f)) continue;
                if (bw < 0.02 * w || bw > 0.16 * w) continue;
                // Bottom centre of the window.
                double cx = b.MinX + bw / 2.0, cy = b.MinY + bh / 2.0;
                if (cx < 0.40 * w || cx > 0.60 * w) continue;
                if (cy < 0.72 * h) continue;
                // Solid disc with a white backpack glyph inside.
                double fill = b.Count / (double)(bw * bh);
                if (fill < 0.45 || fill > 0.95) continue;
                double white = WhiteFraction(px, w, b.MinX, b.MinY, bw, bh);
                if (white < 0.05 || white > 0.40) continue;
                if (bw * bh > bestArea) { bestArea = bw * bh; best = new Rectangle(b.MinX, b.MinY, bw, bh); }
            }
            return best;
        }

        public static Rectangle? FindPlusTile(Bitmap bmp)
        {
            return WithPixels(bmp, delegate(byte[] px, int w, int h)
            {
                Rectangle? best = null;
                int bestArea = 0;
                foreach (Blob b in ColourBlobs.Label(px, w, h, InviteDetector.IsGreen).Blobs)
                {
                    int bw = b.W, bh = b.H;
                    if (!RoughlySquare(bw, bh, 0.85f, 1.20f)) continue;
                    // Measured: 78 px in a 1145 px window (0.07). Roblox UI keeps its pixel size, so the floor is low.
                    if (bw < 0.02 * w || bw > 0.25 * w) continue;
                    double fill = b.Count / (double)(bw * bh);
                    if (fill < 0.50) continue;
                    // A white plus: a white bar through the middle row and one through the middle column.
                    if (!HasPlus(px, w, b.MinX, b.MinY, bw, bh)) continue;
                    if (bw * bh > bestArea) { bestArea = bw * bh; best = new Rectangle(b.MinX, b.MinY, bw, bh); }
                }
                return best;
            });
        }

        // The highlighted Gifts tile is measured against the "+" tile, so the window size does not matter:
        // it is about 0.6-0.95 of the "+" width, left of it and below its top. (The orange sort button is above the "+".)
        public static Rectangle? FindGiftsTabHighlighted(Bitmap bmp)
        {
            Rectangle? plus = FindPlusTile(bmp);
            if (!plus.HasValue) return null;
            Rectangle p = plus.Value;

            return WithPixels(bmp, delegate(byte[] px, int w, int h)
            {
                Rectangle? best = null;
                int bestArea = 0;
                foreach (Blob b in ColourBlobs.Label(px, w, h, IsOrange).Blobs)
                {
                    int bw = b.W, bh = b.H;
                    float aspect = bw / (float)bh;
                    if (aspect < 1.20f || aspect > 1.60f) continue;
                    float ratio = bw / (float)p.Width;
                    if (ratio < 0.60f || ratio > 0.95f) continue;
                    double cx = b.MinX + bw / 2.0, cy = b.MinY + bh / 2.0;
                    if (cx >= p.X) continue;
                    if (cy <= p.Y) continue;
                    double fill = b.Count / (double)(bw * bh);
                    if (fill < 0.60) continue;
                    if (bw * bh > bestArea) { bestArea = bw * bh; best = new Rectangle(b.MinX, b.MinY, bw, bh); }
                }
                return best;
            });
        }

        // Gift icon of the Gifts category tile on a 20 x 14 grid (1 = white), from gifts_icon.png. Only the interior is compared:
        // the rounded corners show the background.
        private static readonly string[] GiftGlyphRows =
        {
            "11000000000000000011",
            "10000000000000000001",
            "00000000111000000000",
            "00000001111100000000",
            "00000000111000000000",
            "00000011001101000000",
            "00000011011100000000",
            "00000011011100000000",
            "00000011011100000000",
            "00000011011100000000",
            "00000000011100000000",
            "00000000000000000000",
            "00000000000000000000",
            "10000000000000000001",
        };

        /// <summary>
        /// The Gifts tile when it is NOT selected: a green tile whose white gift icon matches the reference. The tile colour
        /// is not used (every unselected tile is green). Only a clear best match counts; with the "+" tile on screen, only
        /// tiles in the left column (left of the "+") are considered.
        /// </summary>
        public static Rectangle? FindGiftsTabUnselected(Bitmap bmp)
        {
            Rectangle? plus = FindPlusTile(bmp);
            return WithPixels(bmp, delegate(byte[] px, int w, int h)
            {
                // Measured on the synthetic unselected tile: target 0.80, runner-up 0.63. The best non-Gifts tile in the
                // selected pane scores 0.63, so the floor sits at 0.70 and a clear margin is required.
                const double MinScore = 0.70;
                const double Margin = 0.10;
                Rectangle? best = null;
                double bestScore = 0, second = 0;
                foreach (Blob b in ColourBlobs.Label(px, w, h, InviteDetector.IsGreen).Blobs)
                {
                    int bw = b.W, bh = b.H;
                    float aspect = bw / (float)bh;
                    if (aspect < 1.20f || aspect > 1.60f) continue;
                    if (bw < 0.02 * w || bw > 0.20 * w) continue;
                    if (plus.HasValue && b.MinX + bw / 2.0 >= plus.Value.X) continue;
                    double score = GlyphScore(px, w, b.MinX, b.MinY, bw, bh);
                    if (score > bestScore)
                    {
                        second = bestScore;
                        bestScore = score;
                        best = new Rectangle(b.MinX, b.MinY, bw, bh);
                    }
                    else if (score > second)
                    {
                        second = score;
                    }
                }
                if (!best.HasValue || bestScore < MinScore || bestScore - second < Margin) return null;
                return best;
            });
        }

        // Pearson correlation between the candidate's white fraction per interior cell and GiftGlyphRows (1 / 0).
        // Continuous on the candidate side, so a tile at a small size (cells of 2-3 px) still correlates.
        private static double GlyphScore(byte[] px, int w, int x0, int y0, int bw, int bh)
        {
            const int N = 12 * 18;
            double[] cand = new double[N];
            double[] tmpl = new double[N];
            int k = 0;
            for (int j = 1; j < 13; j++)
            {
                for (int i = 1; i < 19; i++)
                {
                    int cx0 = x0 + i * bw / 20, cx1 = Math.Max(cx0 + 1, x0 + (i + 1) * bw / 20);
                    int cy0 = y0 + j * bh / 14, cy1 = Math.Max(cy0 + 1, y0 + (j + 1) * bh / 14);
                    int white = 0, n = 0;
                    for (int y = cy0; y < cy1; y++)
                    {
                        for (int x = cx0; x < cx1; x++)
                        {
                            int o = (y * w + x) * 4;
                            n++;
                            if (ColourBlobs.IsWhite(px[o + 2], px[o + 1], px[o])) white++;
                        }
                    }
                    cand[k] = n > 0 ? white / (double)n : 0;
                    tmpl[k] = GiftGlyphRows[j][i] == '1' ? 1 : 0;
                    k++;
                }
            }
            return Correlation(cand, tmpl);
        }

        private static double Correlation(double[] a, double[] b)
        {
            int n = a.Length;
            double ma = 0, mb = 0;
            for (int i = 0; i < n; i++) { ma += a[i]; mb += b[i]; }
            ma /= n; mb /= n;
            double cov = 0, va = 0, vb = 0;
            for (int i = 0; i < n; i++)
            {
                double da = a[i] - ma, db = b[i] - mb;
                cov += da * db; va += da * da; vb += db * db;
            }
            if (va <= 1e-9 || vb <= 1e-9) return 0;
            return cov / Math.Sqrt(va * vb);
        }

        public static Rectangle? FindHospitalMarker(Bitmap bmp)
        {
            return WithPixels(bmp, delegate(byte[] px, int w, int h)
            {
                BlobMap reds = ColourBlobs.Label(px, w, h, InviteDetector.IsRed);
                Rectangle? best = null;
                int bestArea = 0;
                foreach (Blob pin in reds.Blobs)
                {
                    int pw = pin.W, ph = pin.H;
                    if (!RoughlySquare(pw, ph, 0.80f, 1.25f)) continue;
                    if (pw < 0.015 * w || pw > 0.15 * w) continue;
                    if (!HasRedHeartInside(px, w, reds, pin)) continue;
                    // The white disc around the heart.
                    if (WhiteFraction(px, w, pin.MinX, pin.MinY, pw, ph) < 0.20) continue;
                    if (pw * ph > bestArea) { bestArea = pw * ph; best = new Rectangle(pin.MinX, pin.MinY, pw, ph); }
                }
                return best;
            });
        }

        public static Rectangle? FindTeleportYes(Bitmap bmp)
        {
            _lastPromptText = "";
            if (bmp == null || bmp.Width < MinImageW || bmp.Height < MinImageH) return null;
            Bitmap work = bmp;
            bool disposeWork = false;
            try
            {
                if (bmp.PixelFormat != PixelFormat.Format32bppArgb)
                {
                    work = new Bitmap(bmp.Width, bmp.Height, PixelFormat.Format32bppArgb);
                    using (Graphics g = Graphics.FromImage(work)) g.DrawImage(bmp, 0, 0, bmp.Width, bmp.Height);
                    disposeWork = true;
                }
                byte[] px = CashOutDetector.ReadPixels(work);
                foreach (RedGreenPair p in InviteDetector.RedGreenPairs(px, work.Width, work.Height))
                {
                    // "No" is short (measured 0.20); the invite buttons are long words (0.40-0.57). The prompt must be the Gifts Display one.
                    if (InviteDetector.LabelWidth(px, work.Width, p.Red) >= MaxNoLabelWidth) continue;
                    if (!DialogTextIsGiftsDisplay(work, p.Red)) continue;
                    return p.Green;
                }
                return null;
            }
            finally
            {
                if (disposeWork) work.Dispose();
            }
        }

        // ----------------------------------------------------------------
        // Colour tests
        // ----------------------------------------------------------------

        // Light blue backpack disc (about 40,170,230).
        internal static bool IsLightBlue(byte r, byte g, byte b)
        {
            float h, s, v;
            ColourBlobs.Hsv(r, g, b, out h, out s, out v);
            return h >= 185f && h <= 225f && s >= 0.40f && v >= 0.55f;
        }

        // Pink backpack disc of the pink UI theme (measured about 255,93,211: hue 316, saturation 0.64, value 1.0). The hue
        // band stops at 345, short of the red of the map pin and invite buttons (IsRed).
        internal static bool IsPink(byte r, byte g, byte b)
        {
            float h, s, v;
            ColourBlobs.Hsv(r, g, b, out h, out s, out v);
            return h >= 300f && h <= 345f && s >= 0.35f && v >= 0.70f;
        }

        // Orange selected category tile.
        internal static bool IsOrange(byte r, byte g, byte b)
        {
            float h, s, v;
            ColourBlobs.Hsv(r, g, b, out h, out s, out v);
            return h >= 12f && h <= 42f && s >= 0.50f && v >= 0.70f;
        }

        // ----------------------------------------------------------------
        // Shape helpers
        // ----------------------------------------------------------------

        private static bool RoughlySquare(int bw, int bh, float min, float max)
        {
            if (bh <= 0) return false;
            float aspect = bw / (float)bh;
            return aspect >= min && aspect <= max;
        }

        private static double WhiteFraction(byte[] px, int w, int x0, int y0, int bw, int bh)
        {
            int white = 0;
            for (int y = y0; y < y0 + bh; y++)
            {
                for (int x = x0; x < x0 + bw; x++)
                {
                    int o = (y * w + x) * 4;
                    if (ColourBlobs.IsWhite(px[o + 2], px[o + 1], px[o])) white++;
                }
            }
            return white / (double)(bw * bh);
        }

        // White bar through the middle row and the middle column of the box.
        private static bool HasPlus(byte[] px, int w, int x0, int y0, int bw, int bh)
        {
            int midY = y0 + bh / 2, midX = x0 + bw / 2;
            int rowWhite = 0, rowN = 0, colWhite = 0, colN = 0;
            for (int x = x0 + bw / 5; x < x0 + bw - bw / 5; x++)
            {
                rowN++;
                int o = (midY * w + x) * 4;
                if (ColourBlobs.IsWhite(px[o + 2], px[o + 1], px[o])) rowWhite++;
            }
            for (int y = y0 + bh / 5; y < y0 + bh - bh / 5; y++)
            {
                colN++;
                int o = (y * w + midX) * 4;
                if (ColourBlobs.IsWhite(px[o + 2], px[o + 1], px[o])) colWhite++;
            }
            return rowN > 0 && colN > 0 && rowWhite >= 0.6 * rowN && colWhite >= 0.6 * colN;
        }

        // The heart: a smaller red blob inside the pin's red ring, about centred, at 20-45% of the ring's width, whose
        // red shape matches the heart of the reference (HeartRows). Other map pins (for example the ticket-style icon) do not.
        private static bool HasRedHeartInside(byte[] px, int w, BlobMap reds, Blob pin)
        {
            double cx = pin.MinX + pin.W / 2.0, cy = pin.MinY + pin.H / 2.0;
            foreach (Blob c in reds.Blobs)
            {
                if (c.Id == pin.Id) continue;
                if (c.MinX < pin.MinX || c.MaxX > pin.MaxX || c.MinY < pin.MinY || c.MaxY > pin.MaxY) continue;
                if (c.W < 0.20 * pin.W || c.W > 0.45 * pin.W) continue;
                if (c.H < 0.20 * pin.H || c.H > 0.45 * pin.H) continue;
                double ccx = c.MinX + c.W / 2.0, ccy = c.MinY + c.H / 2.0;
                if (Math.Abs(ccx - cx) > 0.15 * pin.W || Math.Abs(ccy - cy) > 0.15 * pin.H) continue;
                if (HeartIoU(reds, c, w) < 0.70) continue;
                return true;
            }
            return false;
        }

        // Red part of the heart in gift_hospital_destination_icon.png (14 x 14, 1 = red, top row first).
        private static readonly string[] HeartRows =
        {
            "00000001100000",
            "00000011110000",
            "00000011110000",
            "00000011110000",
            "00000111110000",
            "11111111110000",
            "11111111111110",
            "11111111111111",
            "00111111111111",
            "00001111111110",
            "00001111100000",
            "00001111000000",
            "00001111000000",
            "00001111000000",
        };

        // Intersection over union of the candidate's red pixels (resampled to 14 x 14) with HeartRows.
        private static double HeartIoU(BlobMap reds, Blob c, int w)
        {
            int inter = 0, union = 0;
            for (int i = 0; i < 14; i++)
            {
                for (int j = 0; j < 14; j++)
                {
                    int sx = c.MinX + (int)((j + 0.5) * c.W / 14.0);
                    int sy = c.MinY + (int)((i + 0.5) * c.H / 14.0);
                    bool cand = reds.Map[sy * w + sx] == c.Id;
                    bool tmpl = HeartRows[i][j] == '1';
                    if (cand && tmpl) inter++;
                    if (cand || tmpl) union++;
                }
            }
            return union == 0 ? 0 : inter / (double)union;
        }

        // The last text OCR read for the prompt title, kept per thread so a refusal can say why (shown in the failure message).
        [ThreadStatic] private static string _lastPromptText;
        public static string LastPromptText { get { return _lastPromptText ?? ""; } }

        // Reads the title; tries a second upscale when the first read has no match (small text in tiled windows).
        internal static bool DialogTextIsGiftsDisplay(Bitmap bmp, Rectangle red)
        {
            int x0 = Math.Max(0, red.Left - (int)(0.6 * red.Width));
            int x1 = Math.Min(bmp.Width, red.Right + (int)(0.6 * red.Width));
            int y0 = Math.Max(0, red.Top - 4 * red.Height);
            int y1 = Math.Max(0, red.Top - red.Height / 2);
            if (x1 <= x0 || y1 <= y0) return false;
            using (Bitmap crop = bmp.Clone(new Rectangle(x0, y0, x1 - x0, y1 - y0), PixelFormat.Format32bppArgb))
            {
                foreach (int targetWidth in new int[] { 480, 800 })
                {
                    string text;
                    using (Bitmap big = UpscaleForOcr(crop, targetWidth))
                    {
                        try { text = TeleportOcr.ReadLine(big) ?? ""; }
                        catch (Exception) { text = ""; }
                    }
                    _lastPromptText = text;
                    // OCR reads the title as "Gifts Displa" at this size, so match the stem of each word.
                    string t = text.ToLowerInvariant();
                    if (t.Contains("gift") && t.Contains("displ")) return true;
                }
                return false;
            }
        }

        // Upscales the title crop to about targetWidth px (never shrinks, at most 6x).
        private static Bitmap UpscaleForOcr(Bitmap crop, int targetWidth)
        {
            double k = Math.Min(6.0, Math.Max(1.0, targetWidth / (double)Math.Max(1, crop.Width)));
            int w = (int)Math.Round(crop.Width * k), h = (int)Math.Round(crop.Height * k);
            Bitmap b = new Bitmap(w, h, PixelFormat.Format32bppArgb);
            using (Graphics g = Graphics.FromImage(b))
            {
                g.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.HighQualityBicubic;
                g.DrawImage(crop, new Rectangle(0, 0, w, h), new Rectangle(0, 0, crop.Width, crop.Height), GraphicsUnit.Pixel);
            }
            return b;
        }

        private static Rectangle? WithPixels(Bitmap bmp, Func<byte[], int, int, Rectangle?> find)
        {
            if (bmp == null || bmp.Width < MinImageW || bmp.Height < MinImageH) return null;
            Bitmap work = bmp;
            bool disposeWork = false;
            try
            {
                if (bmp.PixelFormat != PixelFormat.Format32bppArgb)
                {
                    work = new Bitmap(bmp.Width, bmp.Height, PixelFormat.Format32bppArgb);
                    using (Graphics g = Graphics.FromImage(work)) g.DrawImage(bmp, 0, 0, bmp.Width, bmp.Height);
                    disposeWork = true;
                }
                return find(CashOutDetector.ReadPixels(work), work.Width, work.Height);
            }
            finally
            {
                if (disposeWork) work.Dispose();
            }
        }
    }
}
