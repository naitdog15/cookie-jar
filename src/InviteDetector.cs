// InviteDetector.cs
//
// PUBLIC API (namespace RbxMulti, static class InviteDetector)
// ----------------------------------------------------------------
//   public static Rectangle? Find(Bitmap bmp);
//       Locates the red dismiss button of a trade request ("Decline") or a party
//       invitation ("Close") in a screenshot of a Roblox (Adopt Me) client area.
//       The red button must have a green button of similar size directly to its
//       right (Accept / View), and a white label (Decline, Close, Reject, or "No").
//       Only the red button is ever reported; the green one is never a click target. Returns the red button's bounding rectangle in
//       bitmap pixel coordinates, or null. The bitmap is not modified and not disposed.
//
//   public static Rectangle? Find(Bitmap bmp, bool declineTrades, bool declineInvites);
//       As Find(bmp), with each kind switched off by its flag. A trade request ("trade" in the body
//       text above the red button) is TRADE; anything else (including unreadable text) is INVITE.
//
//   public static Point ClickPoint(Rectangle button);
//       Centre of the red button (bitmap pixels).
//
//   public static double LabelWidthFraction(Bitmap bmp, Rectangle button);
//       Width of the white label inside a button, as a fraction of the button width.
//
//   internal static List<RedGreenPair> RedGreenPairs(byte[] px, int w, int h);
//       Every red button with a similar-sized green button to its right, with no label test.
//       Shared with HospitalDetector (the teleport prompt's Yes / No pair).
//
// This file performs image analysis only. It never sends input and never clicks.

using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Imaging;

namespace RbxMulti
{
    internal sealed class RedGreenPair
    {
        public Rectangle Red;      // the left, red button
        public Rectangle Green;    // the right, green button
    }

    public static class InviteDetector
    {
        private const int MinImageW = 200;
        private const int MinImageH = 120;

        // Label width (see LabelWidthFraction) a dismiss button must have. Measured: Decline/Close 0.40-0.57, short "No" about 0.20.
        // Lowered from 0.30 so the short "No" label (Gifts Display prompt) is accepted as a decline target. The red-left /
        // green-right pair shape, white label and sibling rules still apply, and the green button is never returned.
        public const double MinLabelWidth = 0.12;

        // Labels narrower than this are the two-letter "No"; only those are checked against the Gifts Display title.
        public const double GiftsPromptNoMaxWidth = 0.25;

        // ----------------------------------------------------------------
        // Public: Find
        // ----------------------------------------------------------------
        public static Rectangle? Find(Bitmap bmp)
        {
            return Find(bmp, true, true);
        }

        /// <summary>
        /// As Find(bmp), but each kind of dialog can be switched off: declineTrades skips trade requests,
        /// declineInvites skips party invitations (and any dialog whose body text is unreadable).
        /// </summary>
        public static Rectangle? Find(Bitmap bmp, bool declineTrades, bool declineInvites)
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

                byte[] px = CashOutDetector.ReadPixels(work);
                Rectangle? best = null;
                int bestArea = 0;
                foreach (RedGreenPair p in RedGreenPairs(px, work.Width, work.Height))
                {
                    // Any white label: Decline, Close, Reject, and the short "No" (Gifts Display prompt). Only the red is returned.
                    if (LabelWidth(px, work.Width, p.Red) < MinLabelWidth) continue;
                    // A short "No" on the "Teleport to The Gifts Display?" prompt is ignored (the user's rule): read its title.
                    if (LabelWidth(px, work.Width, p.Red) < GiftsPromptNoMaxWidth && HospitalDetector.DialogTextIsGiftsDisplay(work, p.Red)) continue;
                    // Trade request or party invitation: skip the kind whose switch is off.
                    bool trade = IsTradeDialog(work, p.Red);
                    if (trade && !declineTrades) continue;
                    if (!trade && !declineInvites) continue;
                    int area = p.Red.Width * p.Red.Height;
                    if (area > bestArea)
                    {
                        bestArea = area;
                        best = p.Red;
                    }
                }
                return best;
            }
            finally
            {
                if (disposeWork) work.Dispose();
            }
        }

        public static Point ClickPoint(Rectangle button)
        {
            return new Point(button.X + button.Width / 2, button.Y + button.Height / 2);
        }

        /// <summary>
        /// Width of the white label inside a red button, as a fraction of the button width (0 when there is no white text).
        /// "Decline" and "Close" are long words; the Gifts Display prompt's "No" is two letters.
        /// </summary>
        public static double LabelWidthFraction(Bitmap bmp, Rectangle button)
        {
            if (bmp == null || button.Width <= 0) return 0;
            byte[] px = CashOutDetector.ReadPixels(bmp);
            return LabelWidth(px, bmp.Width, button);
        }

        // ----------------------------------------------------------------
        // Dialog kind
        // ----------------------------------------------------------------

        /// <summary>
        /// True when the body text above the red button contains "trade" (case-insensitive): a trade request.
        /// Anything else, including text that cannot be read, is treated as a party invitation (false).
        /// </summary>
        internal static bool IsTradeDialog(Bitmap bmp, Rectangle red)
        {
            int x0 = Math.Max(0, red.Left - 2 * red.Width);
            int x1 = Math.Min(bmp.Width, red.Right + 2 * red.Width);
            int y0 = Math.Max(0, red.Top - 4 * red.Height);
            int y1 = Math.Max(0, red.Top - red.Height / 2);
            if (x1 <= x0 || y1 <= y0) return false;
            using (Bitmap crop = bmp.Clone(new Rectangle(x0, y0, x1 - x0, y1 - y0), PixelFormat.Format32bppArgb))
            {
                foreach (int targetWidth in new int[] { 480, 800 })
                {
                    string text;
                    using (Bitmap big = Upscale(crop, targetWidth))
                    {
                        try { text = TeleportOcr.ReadLine(big) ?? ""; }
                        catch (Exception) { text = ""; }
                    }
                    if (text.IndexOf("trade", StringComparison.OrdinalIgnoreCase) >= 0) return true;
                }
                return false;
            }
        }

        // Upscales a crop to about targetWidth px (never shrinks, at most 6x).
        private static Bitmap Upscale(Bitmap crop, int targetWidth)
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

        // ----------------------------------------------------------------
        // Shape rules
        // ----------------------------------------------------------------

        // Red button, about (216,42,63), with a darker bottom edge about (175,23,56): hue wraps around 0, so 345..10 deg.
        internal static bool IsRed(byte r, byte g, byte b)
        {
            float h, s, v;
            ColourBlobs.Hsv(r, g, b, out h, out s, out v);
            return v >= 0.55f && s >= 0.65f && (h >= 345f || h <= 10f);
        }

        // Accept / View button green, about (74,198,85).
        internal static bool IsGreen(byte r, byte g, byte b)
        {
            float h, s, v;
            ColourBlobs.Hsv(r, g, b, out h, out s, out v);
            return v >= 0.50f && s >= 0.40f && h >= 95f && h <= 160f;
        }

        internal static List<RedGreenPair> RedGreenPairs(byte[] px, int w, int h)
        {
            List<RedGreenPair> pairs = new List<RedGreenPair>();
            BlobMap reds = ColourBlobs.Label(px, w, h, IsRed);
            BlobMap greens = ColourBlobs.Label(px, w, h, IsGreen);

            foreach (Blob r in reds.Blobs)
            {
                int bw = r.W;
                int bh = r.H;

                // Button size and shape: wide, about 2-5 : 1 (reference 3.4 : 1).
                if (bh < 0.03 * h || bh > 0.25 * h) continue;
                if (bw < 0.06 * w || bw > 0.50 * w) continue;
                float aspect = bw / (float)bh;
                if (aspect < 2.0f || aspect > 5.0f) continue;

                // Solid red with white text inside the box.
                int area = bw * bh;
                int filled = 0;
                int white = 0;
                for (int y = r.MinY; y <= r.MaxY; y++)
                {
                    for (int x = r.MinX; x <= r.MaxX; x++)
                    {
                        int idx = y * w + x;
                        if (reds.Map[idx] == r.Id)
                        {
                            filled++;
                            continue;
                        }
                        int o = idx * 4;
                        if (ColourBlobs.IsWhite(px[o + 2], px[o + 1], px[o])) white++;
                    }
                }
                // Anti-aliased text edges are neither red nor white, so the solid share stays below 100%.
                if ((filled + white) / (float)area < 0.70f) continue;
                // Floor 1%: the two-letter "No" at a small window size has very little white text.
                float whiteFrac = white / (float)area;
                if (whiteFrac < 0.01f || whiteFrac > 0.45f) continue;

                // Green button of similar size directly to the right, on the same row.
                Blob g = FindGreenSiblingOnRight(greens, r, bw, bh);
                if (g == null) continue;

                RedGreenPair p = new RedGreenPair();
                p.Red = new Rectangle(r.MinX, r.MinY, bw, bh);
                p.Green = new Rectangle(g.MinX, g.MinY, g.W, g.H);
                pairs.Add(p);
            }
            return pairs;
        }

        private static Blob FindGreenSiblingOnRight(BlobMap greens, Blob red, int bw, int bh)
        {
            double redCy = red.MinY + bh / 2.0;
            foreach (Blob g in greens.Blobs)
            {
                int gw = g.W;
                int gh = g.H;
                if (gh < 0.6 * bh || gh > 1.4 * bh) continue;
                if (gw < 0.6 * bw || gw > 1.4 * bw) continue;

                double greenCy = g.MinY + gh / 2.0;
                if (Math.Abs(greenCy - redCy) > 0.35 * bh) continue;

                int gap = g.MinX - red.MaxX;
                if (gap < 0 || gap > 0.25 * bw) continue;
                return g;
            }
            return null;
        }

        // Inner box only: the rounded corners show the white paper behind the button.
        internal static double LabelWidth(byte[] px, int w, Rectangle button)
        {
            int x0 = button.Left + (int)(0.12 * button.Width);
            int x1 = button.Right - (int)(0.12 * button.Width);
            int y0 = button.Top + (int)(0.20 * button.Height);
            int y1 = button.Bottom - (int)(0.20 * button.Height);
            int minX = int.MaxValue, maxX = int.MinValue;
            for (int y = y0; y < y1; y++)
            {
                for (int x = x0; x < x1; x++)
                {
                    int o = (y * w + x) * 4;
                    if (!ColourBlobs.IsWhite(px[o + 2], px[o + 1], px[o])) continue;
                    if (x < minX) minX = x;
                    if (x > maxX) maxX = x;
                }
            }
            if (maxX < minX) return 0;
            return (maxX - minX + 1) / (double)button.Width;
        }
    }
}
