// TeleportDetector.cs
//
// Screen-image analysis for the Adopt Me FRIENDS panel. No input, no process memory: it only looks at a
// Bitmap (a window capture, see CashOutDetector.CaptureWindow). C# 5. Detection is scale-invariant:
// every size threshold is a fraction of the image height, the panel width or the panel height.
//
// PUBLIC API (namespace RbxMulti, static class TeleportDetector)
//   FriendsButtonInfo FindFriendsButton(Bitmap bmp)
//       The pink FRIENDS button of the bottom-right sidebar (pink ribbon with white text + glasses-people
//       icon above it). Null when not found. Searches the right 45% of the image (the whole image when it is
//       narrow, < 0.6 x height, or tiny, < 300 px wide, so cropped fixtures work).
//   FriendsPanelInfo FindPanel(Bitmap bmp)
//       The open light-blue FRIENDS panel: bounds, red close X, and one PanelRow per Teleport/Offline button
//       found (Online = green Teleport, false = grey Offline). Names are NOT read yet. Null when no panel.
//   void ReadNames(Bitmap bmp, FriendsPanelInfo panel)
//       OCRs each row's name (TeleportOcr) and fills PanelRow.Name / RawName.
//   NameMatch MatchTarget(FriendsPanelInfo panel, string username)
//       Case-insensitive, OCR-confusion tolerant (I/l/1, O/0, rn/m ...) match. Requires a clear best match; two
//       similar rows give Status Ambiguous (refuse instead of guessing).
//   double SceneDiff(Bitmap a, Bitmap b, Rectangle exclude1, Rectangle exclude2)
//       Fraction (0..1) of a 64x36 grid whose cells changed clearly between two captures, ignoring two rectangles.
//   MatchStatus / NameMatch / PanelRow / FriendsPanelInfo / FriendsButtonInfo: result types.
//   static string Normalize(string), static double Similarity(string a, string b): exposed for tests.

using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using System.Text;

namespace RbxMulti
{
    public sealed class FriendsButtonInfo
    {
        public Rectangle Label;       // the pink ribbon with the white FRIENDS text (bounding box, the sticker is tilted)
        public Rectangle Icon;        // the white paper with the two pink people above the ribbon
        public Rectangle Bounds;      // icon + ribbon, with a small margin for the tilt/wobble
        public Point Click;           // primary click point: on the pink icon (= Ladder[0]), bitmap pixels
        public Point[] Ladder;        // 3 distinct points on the icon to try in turn (primary, centre, upper-right)
        public double Score;
    }

    public sealed class PanelRow
    {
        public Rectangle RowRect;     // full row (avatar, name, gear, button)
        public Rectangle NameRect;    // where the username text is
        public Rectangle ButtonRect;  // Teleport or Offline button
        public bool Online;           // green Teleport button
        public string Name;           // OCR text (after ReadNames), null before
        public string RawName;
        public List<string> Candidates = new List<string>();   // every distinct OCR reading (different scales / thresholds)
        public int Index;             // 0-based position in the panel
    }

    public sealed class FriendsPanelInfo
    {
        public Rectangle Bounds;
        public Rectangle CloseButton; // red X; Rectangle.Empty when not found
        public List<PanelRow> Rows = new List<PanelRow>();
        public bool HasCloseButton { get { return !CloseButton.IsEmpty; } }
    }

    public enum MatchStatus
    {
        Found,
        NotFound,
        Ambiguous,
        NoOcr
    }

    public sealed class NameMatch
    {
        public MatchStatus Status;
        public PanelRow Row;
        public double Score;
        public double SecondScore;
        public string Message;
    }

    public static class TeleportDetector
    {
        // ------------------------------------------------------------------------------------
        // Pixel access
        // ------------------------------------------------------------------------------------

        private sealed class Img
        {
            public byte[] Px;   // BGRA
            public int W;
            public int H;
        }

        private static Img Load(Bitmap bmp)
        {
            Bitmap work = bmp;
            bool dispose = false;
            if (bmp.PixelFormat != PixelFormat.Format32bppArgb)
            {
                work = new Bitmap(bmp.Width, bmp.Height, PixelFormat.Format32bppArgb);
                using (Graphics g = Graphics.FromImage(work)) { g.DrawImage(bmp, 0, 0, bmp.Width, bmp.Height); }
                dispose = true;
            }
            try
            {
                int w = work.Width, h = work.Height;
                BitmapData d = work.LockBits(new Rectangle(0, 0, w, h), ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
                try
                {
                    byte[] buf = new byte[w * h * 4];
                    int rowBytes = w * 4;
                    for (int y = 0; y < h; y++)
                    {
                        Marshal.Copy(IntPtr.Add(d.Scan0, y * d.Stride), buf, y * rowBytes, rowBytes);
                    }
                    return new Img { Px = buf, W = w, H = h };
                }
                finally { work.UnlockBits(d); }
            }
            finally
            {
                if (dispose) work.Dispose();
            }
        }

        private static void Hsv(byte r, byte g, byte b, out float hue, out float sat, out float val)
        {
            int max = Math.Max(r, Math.Max(g, b));
            int min = Math.Min(r, Math.Min(g, b));
            int d = max - min;
            val = max / 255f;
            sat = max == 0 ? 0f : d / (float)max;
            if (d == 0) { hue = 0f; return; }
            float h;
            if (max == r) h = 60f * (((g - b) / (float)d) % 6f);
            else if (max == g) h = 60f * (2f + (b - r) / (float)d);
            else h = 60f * (4f + (r - g) / (float)d);
            if (h < 0) h += 360f;
            hue = h;
        }

        // Strong pink of the FRIENDS ribbon, about (237,44,121) / (240,81,145).
        private static bool IsPink(byte r, byte g, byte b)
        {
            float h, s, v; Hsv(r, g, b, out h, out s, out v);
            return v >= 0.80f && s >= 0.55f && h >= 322f && h <= 354f;
        }

        // Softer pink / magenta used in the icon (faces, hair).
        private static bool IsPinkish(byte r, byte g, byte b)
        {
            float h, s, v; Hsv(r, g, b, out h, out s, out v);
            return v >= 0.55f && s >= 0.22f && h >= 312f && h <= 358f;
        }

        private static bool IsWhite(byte r, byte g, byte b)
        {
            return r > 225 && g > 225 && b > 225;
        }

        // Panel background, about (179,239,255) with slightly darker row lines (171,235,253).
        private static bool IsPanelBlue(byte r, byte g, byte b)
        {
            return b >= 238 && g >= 220 && g <= 250 && r >= 140 && r <= 205 && g - r >= 30 && b >= g;
        }

        // Pale tinted body of any theme (blue, pink, purple, green...): bright, slightly saturated.
        private static bool IsPaleBody(byte r, byte g, byte b)
        {
            if (IsPanelBlue(r, g, b)) return true;
            float h, s, v; Hsv(r, g, b, out h, out s, out v);
            return v >= 0.90f && s >= 0.06f && s <= 0.45f;
        }

        // Dark pill of the Nearby tab (a little darker than the Offline button).
        private static bool IsDarkTab(byte r, byte g, byte b)
        {
            int max = Math.Max(r, Math.Max(g, b));
            int min = Math.Min(r, Math.Min(g, b));
            return max <= 100 && max - min <= 24;
        }

        // Red close button, about (216,42,63).
        private static bool IsRed(byte r, byte g, byte b)
        {
            float h, s, v; Hsv(r, g, b, out h, out s, out v);
            return v >= 0.70f && s >= 0.65f && (h >= 345f || h <= 10f);
        }

        // Teleport button green, about (74,198,85).
        private static bool IsGreen(byte r, byte g, byte b)
        {
            float h, s, v; Hsv(r, g, b, out h, out s, out v);
            return v >= 0.50f && s >= 0.40f && h >= 95f && h <= 160f;
        }

        // Offline button, dark neutral grey about (68,69,71).
        private static bool IsDarkGrey(byte r, byte g, byte b)
        {
            int max = Math.Max(r, Math.Max(g, b));
            int min = Math.Min(r, Math.Min(g, b));
            return max <= 105 && min >= 35 && max - min <= 16;
        }

        private sealed class Comp
        {
            public int MinX, MinY, MaxX, MaxY, Count;
            public int W { get { return MaxX - MinX + 1; } }
            public int H { get { return MaxY - MinY + 1; } }
            public Rectangle Rect { get { return new Rectangle(MinX, MinY, W, H); } }
        }

        private delegate bool PixelTest(byte r, byte g, byte b);

        // 8-connected components of pixels inside 'region' that pass 'test'.
        private static List<Comp> Components(Img im, Rectangle region, PixelTest test, int minCount)
        {
            int w = im.W, h = im.H;
            int x0 = Math.Max(0, region.Left), y0 = Math.Max(0, region.Top);
            int x1 = Math.Min(w, region.Right), y1 = Math.Min(h, region.Bottom);
            List<Comp> list = new List<Comp>();
            if (x1 <= x0 || y1 <= y0) return list;
            bool[] seen = new bool[w * h];
            Stack<int> st = new Stack<int>();
            byte[] px = im.Px;
            for (int y = y0; y < y1; y++)
            {
                for (int x = x0; x < x1; x++)
                {
                    int s = y * w + x;
                    if (seen[s]) continue;
                    int o = s * 4;
                    if (!test(px[o + 2], px[o + 1], px[o])) { continue; }
                    Comp c = new Comp { MinX = x, MaxX = x, MinY = y, MaxY = y };
                    seen[s] = true;
                    st.Push(s);
                    while (st.Count > 0)
                    {
                        int idx = st.Pop();
                        int cx = idx % w, cy = idx / w;
                        c.Count++;
                        if (cx < c.MinX) c.MinX = cx;
                        if (cx > c.MaxX) c.MaxX = cx;
                        if (cy < c.MinY) c.MinY = cy;
                        if (cy > c.MaxY) c.MaxY = cy;
                        for (int dy = -1; dy <= 1; dy++)
                        {
                            int ny = cy + dy;
                            if (ny < y0 || ny >= y1) continue;
                            for (int dx = -1; dx <= 1; dx++)
                            {
                                int nx = cx + dx;
                                if (nx < x0 || nx >= x1) continue;
                                int ni = ny * w + nx;
                                if (seen[ni]) continue;
                                int no = ni * 4;
                                if (!test(px[no + 2], px[no + 1], px[no])) continue;
                                seen[ni] = true;
                                st.Push(ni);
                            }
                        }
                    }
                    if (c.Count >= minCount) list.Add(c);
                }
            }
            return list;
        }

        private static double Fraction(Img im, Rectangle r, PixelTest test)
        {
            int x0 = Math.Max(0, r.Left), y0 = Math.Max(0, r.Top), x1 = Math.Min(im.W, r.Right), y1 = Math.Min(im.H, r.Bottom);
            if (x1 <= x0 || y1 <= y0) return 0;
            int n = 0, hit = 0;
            for (int y = y0; y < y1; y++)
            {
                for (int x = x0; x < x1; x++)
                {
                    int o = (y * im.W + x) * 4;
                    n++;
                    if (test(im.Px[o + 2], im.Px[o + 1], im.Px[o])) hit++;
                }
            }
            return n == 0 ? 0 : hit / (double)n;
        }

        // ------------------------------------------------------------------------------------
        // FRIENDS button
        // ------------------------------------------------------------------------------------

        public static FriendsButtonInfo FindFriendsButton(Bitmap bmp)
        {
            if (bmp == null || bmp.Width < 60 || bmp.Height < 60) return null;
            Img im = Load(bmp);
            int w = im.W, h = im.H;

            // Crops (tiny or narrow images) are searched whole and without the window-relative size limits.
            bool isCrop = w < 300 || w < 0.6 * h;
            Rectangle region;
            if (isCrop) region = new Rectangle(0, 0, w, h);
            else region = new Rectangle((int)(w * 0.55), (int)(h * 0.20), w - (int)(w * 0.55), h - (int)(h * 0.20));

            List<Comp> comps = Components(im, region, IsPink, 25);
            FriendsButtonInfo best = null;
            foreach (Comp c in comps)
            {
                int bw = c.W, bh = c.H;
                double aspect = bw / (double)bh;
                // Ribbon: wide, 2.4..5 : 1. Size relative to the image height (reference: 88 x 24 at 692 high).
                if (aspect < 1.25 || aspect > 6.0) continue;   // wide: a tilted ribbon, or a window stretched non-uniformly
                if (!isCrop)
                {
                    if (bh < 0.020 * h || bh > 0.090 * h) continue;
                    if (bw < 0.035 * h || bw > 0.250 * h) continue;
                }
                double boxFill = c.Count / (double)(bw * bh);
                if (boxFill < 0.30) continue;   // a tilted ribbon fills only ~40% of its bounding box

                // White text inside the ribbon.
                double white = Fraction(im, c.Rect, IsWhite);
                if (white < 0.08 || white > 0.50) continue;

                // Icon above the ribbon: white paper with pinkish people.
                int iconH = (int)Math.Max(bw * 0.95, bh * 3.0);   // the paper is ~0.9 ribbon widths wide and ~2 ribbon heights high
                Rectangle icon = new Rectangle(c.MinX - bw / 12, c.MinY - iconH, bw + bw / 6, iconH - 1);
                icon.Intersect(new Rectangle(0, 0, w, h));
                if (icon.Width < 8 || icon.Height < 8) continue;
                double pinkish = Fraction(im, icon, IsPinkish);
                double iconWhite = Fraction(im, icon, IsWhite);
                if (pinkish < 0.10) continue;
                if (iconWhite < 0.08) continue;

                double score = 0.4 + Math.Min(0.3, pinkish) + (1.0 - Math.Abs(aspect - 3.7) / 3.7) * 0.2 + Math.Min(0.1, white / 3.0);
                if (best == null || score > best.Score)
                {
                    best = new FriendsButtonInfo();
                    best.Label = c.Rect;
                    best.Bounds = Rectangle.Union(c.Rect, icon);
                    best.Click = new Point(c.MinX + bw / 2, c.MinY + bh / 2);
                    best.Score = score;
                }
            }
            if (best != null) BuildIcon(im, best);
            return best;
        }

        // True when most of the 5x5 neighbourhood of p is pink (the people on the FRIENDS icon). For tests and checks.
        public static bool PinkIconAt(Bitmap bmp, Point p)
        {
            if (bmp == null) return false;
            return PinkShare(Load(bmp), p.X, p.Y, 2) >= 0.8;
        }

        private static double PinkShare(Img im, int cx, int cy, int rad)
        {
            int n = 0, hit = 0;
            for (int y = cy - rad; y <= cy + rad; y++)
            {
                for (int x = cx - rad; x <= cx + rad; x++)
                {
                    n++;
                    if (x < 0 || y < 0 || x >= im.W || y >= im.H) continue;
                    int o = (y * im.W + x) * 4;
                    if (IsPinkish(im.Px[o + 2], im.Px[o + 1], im.Px[o])) hit++;
                }
            }
            return n == 0 ? 0 : hit / (double)n;
        }

        // Nearest point to 'want' (within 'radius') that sits inside a solid pink area of the icon; 'want' itself when none.
        private static Point SnapPink(Img im, Point want, int radius)
        {
            for (int r = 0; r <= radius; r++)
            {
                for (int dy = -r; dy <= r; dy++)
                {
                    for (int dx = -r; dx <= r; dx++)
                    {
                        if (Math.Max(Math.Abs(dx), Math.Abs(dy)) != r) continue;
                        if (PinkShare(im, want.X + dx, want.Y + dy, 2) >= 0.9) return new Point(want.X + dx, want.Y + dy);
                    }
                }
            }
            return want;
        }

        private static double PtDist(Point a, Point b)
        {
            return Math.Sqrt((a.X - b.X) * (double)(a.X - b.X) + (a.Y - b.Y) * (double)(a.Y - b.Y));
        }

        // Finds the icon (white paper with the pink people) above the ribbon and derives the click ladder from it.
        // The sticker is tilted and wobbles, so the ribbon's bounding box alone says little about where the icon is.
        private static void BuildIcon(Img im, FriendsButtonInfo info)
        {
            Rectangle rib = info.Label;
            int bw = rib.Width;
            Rectangle img = new Rectangle(0, 0, im.W, im.H);
            // Geometric estimate: the paper is ~0.9 ribbon-widths wide and ~0.66 high, directly above the ribbon.
            int bh = rib.Height;
            int estH = (int)Math.Max(0.68 * bw, 2.0 * bh);
            Rectangle est = new Rectangle(rib.Left + (int)(0.05 * bw), rib.Top - estH, (int)(0.90 * bw), estH);
            est.Intersect(img);
            Rectangle search = Rectangle.FromLTRB(rib.Left - (int)(0.10 * bw), rib.Top - (int)Math.Max(0.95 * bw, 3.0 * bh), rib.Right + (int)(0.10 * bw), rib.Top);
            search.Intersect(img);
            Rectangle icon = est;
            PixelTest paper = delegate(byte r, byte g, byte b) { return IsWhite(r, g, b) || IsPinkish(r, g, b); };
            Comp pick = null;
            foreach (Comp c in Components(im, search, paper, 30))
            {
                Rectangle cr = c.Rect;
                if (!cr.IntersectsWith(est)) continue;
                if (cr.Width < 0.45 * bw || cr.Width > 1.25 * bw || cr.Height < 0.9 * bh || cr.Height > Math.Max(1.0 * bw, 3.2 * bh)) continue;
                if (pick == null || c.Count > pick.Count) pick = c;
            }
            if (pick != null) icon = pick.Rect;
            if (icon.Width < 8 || icon.Height < 8) icon = est;
            info.Icon = icon;

            int iw = icon.Width, ih = icon.Height;
            Point p0 = SnapPink(im, new Point(icon.Left + iw / 2, icon.Top + (int)(0.42 * ih)), Math.Max(4, (int)(0.30 * iw)));
            Point p1 = new Point(icon.Left + iw / 2, icon.Top + ih / 2);
            if (PtDist(p0, p1) < 10) p1 = SnapPink(im, new Point(icon.Left + (int)(0.28 * iw), icon.Top + (int)(0.50 * ih)), Math.Max(3, (int)(0.2 * iw)));
            Point p2 = SnapPink(im, new Point(icon.Left + (int)(0.74 * iw), icon.Top + (int)(0.30 * ih)), Math.Max(3, (int)(0.18 * iw)));
            if (PtDist(p2, p0) < 10 || PtDist(p2, p1) < 10) p2 = new Point(icon.Left + (int)(0.80 * iw), icon.Top + (int)(0.25 * ih));
            info.Ladder = new Point[] { p0, p1, p2 };
            info.Click = p0;

            int m = Math.Max(3, (int)(0.04 * bw));
            Rectangle bounds = Rectangle.Union(rib, icon);
            bounds.Inflate(m, m);
            bounds.Intersect(img);
            info.Bounds = bounds;
        }

        // ------------------------------------------------------------------------------------
        // FRIENDS panel
        // ------------------------------------------------------------------------------------

        public static FriendsPanelInfo FindPanel(Bitmap bmp)
        {
            return FindPanel(bmp, false);
        }

        /// <summary>
        /// Layout independent: the panel is found by colour (light-blue body) and shape, confirmed by the red X at its
        /// top-right corner; rows are the horizontal bands that hold a green/grey rounded button on the right. Every
        /// size test is relative to the DETECTED panel (or to the image area), never to the 1144x692 reference, and the
        /// aspect ratio gates are wide so a stretched or tall window still works. allowNoRows=true also returns a panel
        /// without any row (open but empty / not loaded yet), which the service reports as its own failure kind.
        /// </summary>
        public static FriendsPanelInfo FindPanel(Bitmap bmp, bool allowNoRows)
        {
            if (bmp == null || bmp.Width < 200 || bmp.Height < 120) return null;
            Img im = Load(bmp);
            int w = im.W, h = im.H;

            // Candidate bodies: the classic light-blue theme, plus any other pale tint (Adopt Me recolours the UI per
            // player: pink, purple, green, orange ...). Non-blue candidates must also show the Friends/Nearby tab pair.
            List<Comp> blues = Components(im, new Rectangle(0, 0, w, h), IsPanelBlue, (int)(0.012 * w * h));
            List<bool> themed = new List<bool>();
            for (int i = 0; i < blues.Count; i++) themed.Add(false);
            int[] bucket = new int[24];
            for (int i = 0; i < im.Px.Length; i += 4)
            {
                float hh, ss, vv; Hsv(im.Px[i + 2], im.Px[i + 1], im.Px[i], out hh, out ss, out vv);
                if (vv >= 0.90f && ss >= 0.06f && ss <= 0.45f) bucket[((int)(hh / 15f)) % 24]++;
            }
            for (int bk = 0; bk < 24; bk++)
            {
                if (bk >= 12 && bk <= 14) continue;   // 180-225 deg: the blue theme above
                if (bucket[bk] + bucket[(bk + 1) % 24] + bucket[(bk + 23) % 24] < 0.012 * w * h) continue;
                float centre = bk * 15f + 7.5f;
                PixelTest tint = delegate(byte r, byte g, byte b)
                {
                    float h2, s2, v2; Hsv(r, g, b, out h2, out s2, out v2);
                    if (v2 < 0.90f || s2 < 0.06f || s2 > 0.45f) return false;
                    float dh = Math.Abs(h2 - centre); if (dh > 180f) dh = 360f - dh;
                    return dh <= 24f;
                };
                foreach (Comp tc in Components(im, new Rectangle(0, 0, w, h), tint, (int)(0.012 * w * h))) { blues.Add(tc); themed.Add(true); }
            }
            FriendsPanelInfo best = null;
            double bestScore = 0;
            for (int ci = 0; ci < blues.Count; ci++)
            {
                Comp c = blues[ci];
                int bw = c.W, bh = c.H;
                if (bh < 0.18 * h || bh > 0.97 * h) continue;
                if (bw < 0.15 * w || bw > 0.98 * w) continue;
                double aspect = bw / (double)bh;
                if (aspect < 0.5 || aspect > 5.0) continue;
                double fill = c.Count / (double)(bw * bh);
                if (fill < 0.35) continue;

                Rectangle pr = c.Rect;
                // Red X at the top-right corner (it overlaps the corner, so look slightly outside too).
                Rectangle search = Rectangle.FromLTRB(pr.Right - (int)(0.16 * bw), pr.Top - (int)(0.10 * bh),
                    Math.Min(w, pr.Right + (int)(0.05 * bw)), pr.Top + (int)(0.25 * bh));
                Comp x = null;
                foreach (Comp r in Components(im, search, IsRed, 12))
                {
                    double ra = r.W / (double)r.H;
                    if (ra < 0.35 || ra > 2.8) continue;
                    if (r.W < 0.025 * bw || r.W > 0.16 * bw) continue;
                    if (r.H < 0.04 * bh || r.H > 0.25 * bh) continue;
                    if (x == null || r.Count > x.Count) x = r;
                }
                if (x == null) continue;
                if (themed[ci] && FindTabsIn(im, pr) == null) continue;

                FriendsPanelInfo info = new FriendsPanelInfo();
                info.Bounds = pr;
                info.CloseButton = x.Rect;
                FindRows(im, info);
                if (info.Rows.Count == 0 && !allowNoRows) continue;
                double score = bw * (double)bh * (info.Rows.Count > 0 ? 1.0 : 0.3);
                if (best == null || score > bestScore) { best = info; bestScore = score; }
            }
            return best;
        }

        private static void FindRows(Img im, FriendsPanelInfo info)
        {
            Rectangle pr = info.Bounds;
            int bw = pr.Width, bh = pr.Height;
            int top = pr.Top + (int)(0.17 * bh);
            if (info.HasCloseButton) top = Math.Max(top, info.CloseButton.Bottom);   // rows start below the header (title + red X)
            Rectangle region = Rectangle.FromLTRB(pr.Left + (int)(0.45 * bw), top, pr.Right - (int)(0.01 * bw), pr.Bottom);
            List<PanelRow> rows = new List<PanelRow>();
            AddButtons(im, region, IsGreen, true, pr, rows);
            AddButtons(im, region, IsDarkGrey, false, pr, rows);
            rows.Sort(delegate(PanelRow a, PanelRow b) { return a.ButtonRect.Y.CompareTo(b.ButtonRect.Y); });
            // Drop duplicates on the same row (keep the first).
            List<PanelRow> clean = new List<PanelRow>();
            foreach (PanelRow r in rows)
            {
                if (clean.Count > 0 && Math.Abs(clean[clean.Count - 1].ButtonRect.Y - r.ButtonRect.Y) < 0.05 * bh) continue;
                clean.Add(r);
            }
            // Row pitch from the detected bands (median distance); a lone row falls back to the reference proportion.
            double pitch = 0.166 * bh;
            if (clean.Count >= 2)
            {
                List<int> d = new List<int>();
                for (int i = 1; i < clean.Count; i++) d.Add(clean[i].ButtonRect.Y + clean[i].ButtonRect.Height / 2 - (clean[i - 1].ButtonRect.Y + clean[i - 1].ButtonRect.Height / 2));
                d.Sort();
                pitch = d[d.Count / 2];
            }
            pitch = Math.Max(0.08 * bh, Math.Min(0.30 * bh, pitch));
            for (int i = 0; i < clean.Count; i++)
            {
                PanelRow r = clean[i];
                r.Index = i;
                int cy = r.ButtonRect.Y + r.ButtonRect.Height / 2;
                int rowH = (int)pitch;
                int nameH = Math.Max(r.ButtonRect.Height, (int)(0.9 * pitch));
                r.RowRect = new Rectangle(pr.Left, cy - rowH / 2, pr.Width, rowH);
                int nx0 = AvatarEnd(im, pr, cy, (int)pitch, r.ButtonRect.Left);
                int nx1 = r.ButtonRect.Left - (int)(0.085 * bw);
                r.NameRect = Rectangle.FromLTRB(nx0, cy - nameH / 2, Math.Max(nx0 + 4, nx1), cy + nameH / 2);
            }
            info.Rows = clean;
        }

        // Left edge of the name text: the right edge of the avatar thumbnail (first wide run of non-panel columns in the
        // row band), so the crop does not depend on how the UI scales the avatar. Falls back to 11.5% of the panel width.
        private static int AvatarEnd(Img im, Rectangle pr, int cy, int pitch, int buttonLeft)
        {
            int fallback = pr.Left + (int)(0.115 * pr.Width);
            try
            {
                int y0 = Math.Max(0, cy - (int)(0.3 * pitch)), y1 = Math.Min(im.H, cy + (int)(0.3 * pitch));
                int xa = pr.Left + (int)(0.02 * pr.Width), xb = Math.Min(im.W, Math.Min(buttonLeft - (int)(0.1 * pr.Width), pr.Left + (int)(0.30 * pr.Width)));
                if (y1 - y0 < 4 || xb - xa < 10) return fallback;
                int runStart = -1, lastActive = -1;
                for (int x = xa; x < xb; x++)
                {
                    int non = 0;
                    for (int y = y0; y < y1; y++)
                    {
                        int o = (y * im.W + x) * 4;
                        if (!IsPaleBody(im.Px[o + 2], im.Px[o + 1], im.Px[o])) non++;
                    }
                    bool active = non >= 0.12 * (y1 - y0);
                    if (active)
                    {
                        if (runStart < 0) runStart = x;
                        lastActive = x;
                    }
                    else if (runStart >= 0 && x - lastActive > 2)
                    {
                        if (lastActive - runStart + 1 >= 0.55 * pitch)
                        {
                            int end = lastActive + Math.Max(2, (int)(0.012 * pr.Width));
                            if (end >= pr.Left + (int)(0.06 * pr.Width) && end <= pr.Left + (int)(0.22 * pr.Width)) return end;
                            return fallback;
                        }
                        runStart = -1;
                    }
                }
            }
            catch (Exception) { }
            return fallback;
        }

        private static void AddButtons(Img im, Rectangle region, PixelTest test, bool online, Rectangle pr, List<PanelRow> rows)
        {
            int bw = pr.Width, bh = pr.Height;
            foreach (Comp c in Components(im, region, test, 30))
            {
                double aspect = c.W / (double)c.H;
                if (aspect < 1.3 || aspect > 8.0) continue;
                if (c.W < 0.10 * bw || c.W > 0.40 * bw) continue;
                if (c.H < 0.05 * bh || c.H > 0.24 * bh) continue;
                double fill = c.Count / (double)(c.W * c.H);
                if (fill < 0.45) continue;

                PanelRow r = new PanelRow();
                r.Online = online;
                r.ButtonRect = c.Rect;
                int cy = c.MinY + c.H / 2;
                int half = (int)(0.075 * bh);
                int nx0 = pr.Left + (int)(0.115 * bw);
                int nx1 = c.MinX - (int)(0.085 * bw);
                r.NameRect = Rectangle.FromLTRB(nx0, cy - half, Math.Max(nx0 + 4, nx1), cy + half);
                int rowHalf = (int)(0.078 * bh);
                r.RowRect = Rectangle.FromLTRB(pr.Left, cy - rowHalf, pr.Right, cy + rowHalf);
                rows.Add(r);
            }
        }

        // ------------------------------------------------------------------------------------
        // Names
        // ------------------------------------------------------------------------------------

        public static void ReadNames(Bitmap bmp, FriendsPanelInfo panel)
        {
            ReadNames(bmp, panel, null);
        }

        // ------------------------------------------------------------------------------------
        // Tabs and loading state (theme independent: the green Friends / dark Nearby pills keep their colours)
        // ------------------------------------------------------------------------------------

        public sealed class PanelTabs
        {
            public Rectangle Friends;   // green pill
            public Rectangle Nearby;    // dark pill
            public Point FriendsClick { get { return new Point(Friends.X + Friends.Width / 2, Friends.Y + Friends.Height / 2); } }
            public Point NearbyClick { get { return new Point(Nearby.X + Nearby.Width / 2, Nearby.Y + Nearby.Height / 2); } }
        }

        private static PanelTabs FindTabsIn(Img im, Rectangle pr)
        {
            int bw = pr.Width, bh = pr.Height;
            Rectangle band = Rectangle.FromLTRB(pr.Left + (int)(0.30 * bw), pr.Top - (int)(0.03 * bh), pr.Right - (int)(0.12 * bw), pr.Top + (int)(0.22 * bh));
            // The dark Nearby pill is found first (the header behind the tabs can be saturated green in a green theme, which
            // would merge with the green Friends pill); the Friends pill is the green block directly left of it.
            Comp d = null;
            foreach (Comp c in Components(im, band, IsDarkTab, 60))
            {
                double a = c.W / (double)c.H;
                if (a < 1.2 || a > 4.5) continue;
                if (c.W < 0.08 * bw || c.W > 0.30 * bw || c.H < 0.04 * bh || c.H > 0.16 * bh) continue;
                if (c.Count < 0.5 * c.W * c.H) continue;
                if (d == null || c.Count > d.Count) d = c;
            }
            if (d == null) return null;
            Rectangle gr = new Rectangle(d.MinX - d.W - 1, d.MinY, d.W, d.H);
            if (gr.Left < 0) return null;
            int green = 0, tot = 0;
            for (int y = gr.Top; y < gr.Bottom && y < im.H; y++)
                for (int x = gr.Left; x < gr.Right && x < im.W; x++)
                {
                    int o = (y * im.W + x) * 4; tot++;
                    if (IsGreen(im.Px[o + 2], im.Px[o + 1], im.Px[o])) green++;
                }
            if (tot == 0 || green < 0.30 * tot) return null;
            Comp g = new Comp { MinX = gr.Left, MinY = gr.Top, MaxX = gr.Right - 1, MaxY = gr.Bottom - 1 };            PanelTabs t = new PanelTabs();
            t.Friends = g.Rect;
            t.Nearby = d.Rect;
            return t;
        }

        /// <summary>The Friends / Nearby tab pills of a detected panel, or null.</summary>
        public static PanelTabs FindTabs(Bitmap bmp, FriendsPanelInfo panel)
        {
            if (bmp == null || panel == null) return null;
            return FindTabsIn(Load(bmp), panel.Bounds);
        }

        public enum PanelState { Closed, Loading, Empty, Loaded }

        /// <summary>
        /// Loaded = at least one row. Without rows: Loading when the body shows 'Loading...' (OCR) or some unreadable content,
        /// Empty when the body is blank or shows other text (an empty friends list).
        /// </summary>
        public static PanelState ClassifyPanel(Bitmap bmp, FriendsPanelInfo p, out string bodyText)
        {
            bodyText = "";
            if (bmp == null || p == null) return PanelState.Closed;
            if (p.Rows.Count > 0) return PanelState.Loaded;
            int bh = p.Bounds.Height, bw = p.Bounds.Width;
            int top = p.Bounds.Top + (int)(0.17 * bh);
            if (p.HasCloseButton) top = Math.Max(top, p.CloseButton.Bottom);
            Rectangle r = Rectangle.FromLTRB(p.Bounds.Left + (int)(0.04 * bw), top, p.Bounds.Right - (int)(0.10 * bw), top + (int)(0.45 * (p.Bounds.Bottom - top)));
            r.Intersect(new Rectangle(0, 0, bmp.Width, bmp.Height));
            if (r.Width < 20 || r.Height < 12) return PanelState.Loading;
            double ink;
            using (Bitmap crop = bmp.Clone(r, PixelFormat.Format32bppArgb))
            {
                Img im = Load(crop);
                int n = 0, tot = im.W * im.H;
                for (int i = 0; i < im.Px.Length; i += 4) if (!IsPaleBody(im.Px[i + 2], im.Px[i + 1], im.Px[i])) n++;
                ink = tot == 0 ? 0 : n / (double)tot;
                if (TeleportOcr.IsAvailable)
                {
                    int scale = Math.Max(1, Math.Min(4, (int)Math.Round(120.0 / crop.Height)));
                    for (int mode = 0; mode < 2 && bodyText.Length == 0; mode++)
                    {
                        using (Bitmap prep = TeleportOcr.PrepareNameCrop(crop, scale, mode == 0 ? 0 : 2)) bodyText = TeleportOcr.ReadLine(prep);
                    }
                }
            }
            string low = (bodyText ?? "").ToLowerInvariant();
            if (low.IndexOf("load", StringComparison.Ordinal) >= 0) return PanelState.Loading;
            foreach (string tok in low.Split(new char[] { ' ', '.', ',' }, StringSplitOptions.RemoveEmptyEntries))
                if (tok.Length >= 4 && Similarity("loading", tok) >= 0.6) return PanelState.Loading;
            if (low.Trim().Length > 0) return PanelState.Empty;
            return ink >= 0.02 ? PanelState.Loading : PanelState.Empty;   // unreadable content = still loading; blank = empty
        }

        public sealed class ListWaitResult
        {
            public PanelState State;
            public FriendsPanelInfo Panel;
            public Bitmap Frame;          // the last capture (caller disposes)
            public int ElapsedMs;
            public int Polls;
            public bool Cancelled;
            public bool Recovered;        // the Nearby/Friends refresh was tried
            public string BodyText;
        }

        /// <summary>
        /// Polls the open FRIENDS panel until it is Loaded. Loading keeps waiting up to timeoutMs; a blank (Empty) body for
        /// emptyGraceMs, or a vanished panel (3 polls), ends early. After recoverAfterMs of Loading, recover() runs once
        /// (the Nearby/Friends tab refresh) and the wait continues. progress(elapsedMs, state) is called every poll.
        /// </summary>
        public static ListWaitResult WaitForList(Func<Bitmap> capture, int timeoutMs, int pollMs, int recoverAfterMs, int emptyGraceMs,
            Func<FriendsPanelInfo, bool> recover, Action<int, PanelState> progress, System.Threading.CancellationToken ct)
        {
            ListWaitResult res = new ListWaitResult();
            res.BodyText = "";
            System.Diagnostics.Stopwatch sw = System.Diagnostics.Stopwatch.StartNew();
            int misses = 0;
            long emptySince = -1;
            while (true)
            {
                Bitmap f = capture();
                PanelState st = PanelState.Closed;
                if (f != null)
                {
                    FriendsPanelInfo p = FindPanel(f, true);
                    string bt;
                    st = ClassifyPanel(f, p, out bt);
                    if (res.Frame != null) res.Frame.Dispose();
                    res.Frame = f;
                    res.Panel = p;
                    res.BodyText = bt;
                }
                res.Polls++;
                res.State = st;
                res.ElapsedMs = (int)sw.ElapsedMilliseconds;
                if (progress != null) { try { progress(res.ElapsedMs, st); } catch (Exception) { } }
                if (st == PanelState.Loaded) return res;
                if (st == PanelState.Closed) { if (++misses >= 3) return res; } else misses = 0;
                if (st == PanelState.Empty)
                {
                    if (emptySince < 0) emptySince = sw.ElapsedMilliseconds;
                    if (sw.ElapsedMilliseconds - emptySince >= emptyGraceMs) return res;
                }
                else emptySince = -1;
                if (st == PanelState.Loading && !res.Recovered && recover != null && sw.ElapsedMilliseconds >= recoverAfterMs)
                {
                    res.Recovered = true;
                    try { recover(res.Panel); } catch (OperationCanceledException) { res.Cancelled = true; return res; }
                }
                res.ElapsedMs = (int)sw.ElapsedMilliseconds;
                if (sw.ElapsedMilliseconds >= timeoutMs) return res;
                if (ct.WaitHandle.WaitOne(pollMs)) { res.Cancelled = true; res.ElapsedMs = (int)sw.ElapsedMilliseconds; return res; }
            }
        }

        /// <summary>
        /// OCRs every row. The crop follows the detected row height; several scales and binarisation modes are tried
        /// when a reading is empty or (with a target username) does not match it yet. Every distinct reading is kept in
        /// PanelRow.Candidates so MatchTarget can use them all.
        /// </summary>
        public static void ReadNames(Bitmap bmp, FriendsPanelInfo panel, string target)
        {
            if (bmp == null || panel == null) return;
            foreach (PanelRow row in panel.Rows)
            {
                Rectangle r = row.NameRect;
                r.Intersect(new Rectangle(0, 0, bmp.Width, bmp.Height));
                row.Candidates.Clear();
                if (r.Width < 4 || r.Height < 4) { row.Name = ""; row.RawName = ""; continue; }
                using (Bitmap crop = bmp.Clone(r, PixelFormat.Format32bppArgb))
                {
                    // Upscale so the text is about 70 px tall, which the OCR engine likes.
                    int baseScale = Math.Max(1, Math.Min(6, (int)Math.Round(70.0 / r.Height)));
                    int[] scales = new int[] { baseScale, baseScale + 1, Math.Max(1, baseScale - 1), baseScale + 2 };
                    int[] modes = new int[] { 0, 1, 2 };
                    string best = "";
                    double bestScore = -1;
                    bool done = false;
                    for (int m = 0; m < modes.Length && !done; m++)
                    {
                        for (int t = 0; t < scales.Length && !done; t++)
                        {
                            if (m > 0 && t > 2) continue;   // fewer scale tries for the alternative thresholds
                            string text;
                            using (Bitmap prepared = TeleportOcr.PrepareNameCrop(crop, scales[t], modes[m])) text = TeleportOcr.ReadLine(prepared);
                            string clean = CleanName(text);
                            if (clean.Length == 0) continue;
                            if (!row.Candidates.Contains(text)) row.Candidates.Add(text);
                            double sc = string.IsNullOrEmpty(target) ? clean.Length : RowScore(text, target);
                            if (sc > bestScore) { bestScore = sc; best = text; }
                            // Without a target: the first reading of 3+ characters is enough. With one: stop at a clear match.
                            if (string.IsNullOrEmpty(target)) { if (clean.Length >= 3) done = true; }
                            else if (sc >= 0.95) done = true;
                        }
                    }
                    row.RawName = best;
                    row.Name = CleanName(best);
                }
            }
        }

        /// <summary>"RubyPlexo (Teleport), kugixoxo (Teleport), (unreadable) (Offline)" - what the OCR saw, for messages.</summary>
        public static string DescribeNames(FriendsPanelInfo panel)
        {
            if (panel == null || panel.Rows.Count == 0) return "no rows";
            StringBuilder sb = new StringBuilder();
            foreach (PanelRow r in panel.Rows)
            {
                if (sb.Length > 0) sb.Append(", ");
                string n = string.IsNullOrEmpty(r.Name) ? "(unreadable)" : r.Name;
                sb.Append(n).Append(r.Online ? " [online]" : " [offline]");
            }
            return sb.ToString();
        }

        private static string CleanName(string text)
        {
            if (text == null) return "";
            StringBuilder sb = new StringBuilder();
            foreach (char ch in text)
            {
                if (char.IsLetterOrDigit(ch) || ch == '_' || ch == '…' || ch == '.') sb.Append(ch);
            }
            return sb.ToString();
        }

        // ------------------------------------------------------------------------------------
        // Matching
        // ------------------------------------------------------------------------------------

        /// <summary>Lower-case, OCR-confusable characters folded together, other characters dropped.</summary>
        public static string Normalize(string s)
        {
            if (string.IsNullOrEmpty(s)) return "";
            string t = s.ToLowerInvariant().Replace("…", "").Replace("...", "");
            t = t.Replace("rn", "m").Replace("vv", "w");
            StringBuilder sb = new StringBuilder();
            foreach (char ch in t)
            {
                char c = ch;
                if (c == 'i' || c == '1' || c == '|' || c == '!' || c == 'l') c = 'l';
                else if (c == '0' || c == 'o') c = 'o';
                else if (c == '5') c = 's';
                else if (c == '2') c = 'z';
                else if (c == '8') c = 'b';
                if (char.IsLetterOrDigit(c) || c == '_') sb.Append(c);
            }
            return sb.ToString();
        }

        private static int Lev(string a, string b)
        {
            int[] prev = new int[b.Length + 1];
            int[] cur = new int[b.Length + 1];
            for (int j = 0; j <= b.Length; j++) prev[j] = j;
            for (int i = 1; i <= a.Length; i++)
            {
                cur[0] = i;
                for (int j = 1; j <= b.Length; j++)
                {
                    int cost = a[i - 1] == b[j - 1] ? 0 : 1;
                    cur[j] = Math.Min(Math.Min(cur[j - 1] + 1, prev[j] + 1), prev[j - 1] + cost);
                }
                int[] tmp = prev; prev = cur; cur = tmp;
            }
            return prev[b.Length];
        }

        /// <summary>1.0 identical (after Normalize) down to 0.0.</summary>
        public static double Similarity(string a, string b)
        {
            string x = Normalize(a), y = Normalize(b);
            if (x.Length == 0 || y.Length == 0) return 0;
            if (x == y) return 1.0;
            int max = Math.Max(x.Length, y.Length);
            return 1.0 - Lev(x, y) / (double)max;
        }

        // Row text can be truncated with an ellipsis; then only the visible prefix is compared.
        private static double RowScore(string rawRowText, string username)
        {
            if (string.IsNullOrEmpty(rawRowText)) return 0;
            bool truncated = rawRowText.EndsWith("…") || rawRowText.EndsWith("...");
            string row = Normalize(rawRowText);
            string target = Normalize(username);
            if (row.Length == 0 || target.Length == 0) return 0;
            if (truncated && row.Length >= 4 && row.Length < target.Length)
            {
                string prefix = target.Substring(0, row.Length);
                double s = row == prefix ? 0.97 : Similarity(row, prefix) * 0.97;
                return s;
            }
            return Similarity(row, target);
        }

        public static NameMatch MatchTarget(FriendsPanelInfo panel, string username)
        {
            NameMatch m = new NameMatch();
            if (panel == null || string.IsNullOrEmpty(username))
            {
                m.Status = MatchStatus.NotFound;
                m.Message = "No username to look for";
                return m;
            }
            string target = Normalize(username);
            double best = 0, second = 0;
            PanelRow bestRow = null;
            int exactCount = 0;
            bool anyName = false;
            foreach (PanelRow row in panel.Rows)
            {
                if (!string.IsNullOrEmpty(row.Name)) anyName = true;
                double s = RowScore(row.Name, username);
                foreach (string cand in row.Candidates)
                {
                    double sc = RowScore(cand, username);
                    if (sc > s) s = sc;
                }
                if (s >= 0.999) exactCount++;
                if (s > best) { second = best; best = s; bestRow = row; }
                else if (s > second) { second = s; }
            }
            m.Score = best;
            m.SecondScore = second;
            if (!anyName)
            {
                m.Status = TeleportOcr.IsAvailable ? MatchStatus.NotFound : MatchStatus.NoOcr;
                m.Message = TeleportOcr.IsAvailable ? "Panel opened with " + panel.Rows.Count + " row(s) but no name could be read (OCR returned nothing)" : "Windows text recognition (OCR) is not available";
                return m;
            }
            // Short names must match exactly: one wrong letter would be another player.
            double need = target.Length < 5 ? 0.999 : 0.80;
            if (bestRow == null || best < need)
            {
                m.Status = MatchStatus.NotFound;
                m.Message = "Panel opened, read: " + DescribeNames(panel) + "; target '" + username + "' is not among them (best match " + best.ToString("0.00") + "). "
                    + "Roblox may show a display name instead of the username, or the friend is not in this list.";
                return m;
            }
            if (exactCount > 1)
            {
                m.Status = MatchStatus.Ambiguous;
                m.Message = "More than one row matches exactly";
                return m;
            }
            if (best < 0.999 && second >= 0.60 && best - second < 0.15)
            {
                m.Status = MatchStatus.Ambiguous;
                m.Message = "Two rows look similar (" + best.ToString("0.00") + " vs " + second.ToString("0.00") + "), not guessing";
                return m;
            }
            m.Status = MatchStatus.Found;
            m.Row = bestRow;
            m.Message = "Matched with score " + best.ToString("0.00");
            return m;
        }

        // ------------------------------------------------------------------------------------
        // Scene change
        // ------------------------------------------------------------------------------------

        private const int GridW = 64;
        private const int GridH = 36;

        private static byte[] Small(Bitmap bmp)
        {
            using (Bitmap s = new Bitmap(GridW, GridH, PixelFormat.Format32bppArgb))
            {
                using (Graphics g = Graphics.FromImage(s))
                {
                    g.InterpolationMode = InterpolationMode.HighQualityBilinear;
                    g.PixelOffsetMode = PixelOffsetMode.HighQuality;
                    g.DrawImage(bmp, new Rectangle(0, 0, GridW, GridH), new Rectangle(0, 0, bmp.Width, bmp.Height), GraphicsUnit.Pixel);
                }
                BitmapData d = s.LockBits(new Rectangle(0, 0, GridW, GridH), ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
                try
                {
                    byte[] buf = new byte[GridW * GridH * 4];
                    for (int y = 0; y < GridH; y++) Marshal.Copy(IntPtr.Add(d.Scan0, y * d.Stride), buf, y * GridW * 4, GridW * 4);
                    return buf;
                }
                finally { s.UnlockBits(d); }
            }
        }

        /// <summary>Fraction of grid cells (outside the two rectangles, bitmap coordinates of 'a') that changed clearly.</summary>
        public static double SceneDiff(Bitmap a, Bitmap b, Rectangle exclude1, Rectangle exclude2)
        {
            if (a == null || b == null) return 0;
            byte[] pa = Small(a), pb = Small(b);
            double sx = GridW / (double)a.Width, sy = GridH / (double)a.Height;
            int considered = 0, changed = 0;
            for (int gy = 0; gy < GridH; gy++)
            {
                for (int gx = 0; gx < GridW; gx++)
                {
                    Rectangle cell = new Rectangle((int)(gx / sx), (int)(gy / sy), (int)(1 / sx) + 1, (int)(1 / sy) + 1);
                    if (!exclude1.IsEmpty && cell.IntersectsWith(exclude1)) continue;
                    if (!exclude2.IsEmpty && cell.IntersectsWith(exclude2)) continue;
                    int o = (gy * GridW + gx) * 4;
                    int d = Math.Abs(pa[o] - pb[o]) + Math.Abs(pa[o + 1] - pb[o + 1]) + Math.Abs(pa[o + 2] - pb[o + 2]);
                    considered++;
                    if (d > 60) changed++;
                }
            }
            return considered == 0 ? 0 : changed / (double)considered;
        }
    }
}
