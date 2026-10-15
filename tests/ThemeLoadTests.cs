// ThemeLoadTests.cs - detector tests for other UI themes (pink etc.) and the LOADING state. C# 5.
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Threading;

namespace RbxMulti
{
    public static class ThemeLoadTests
    {
        public static readonly Rectangle BluePanel = new Rectangle(329, 224, 487, 273);   // in tp_full_panel.png (incl. title bar)

        public static void HsvToRgb(float h, float s, float v, out int r, out int g, out int b)
        {
            h = ((h % 360f) + 360f) % 360f;
            float c = v * s, x = c * (1 - Math.Abs((h / 60f) % 2 - 1)), m = v - c;
            float rr, gg, bb;
            if (h < 60) { rr = c; gg = x; bb = 0; }
            else if (h < 120) { rr = x; gg = c; bb = 0; }
            else if (h < 180) { rr = 0; gg = c; bb = x; }
            else if (h < 240) { rr = 0; gg = x; bb = c; }
            else if (h < 300) { rr = x; gg = 0; bb = c; }
            else { rr = c; gg = 0; bb = x; }
            r = (int)Math.Round((rr + m) * 255); g = (int)Math.Round((gg + m) * 255); b = (int)Math.Round((bb + m) * 255);
        }

        public static void RgbToHsv(int r, int g, int b, out float h, out float s, out float v)
        {
            int max = Math.Max(r, Math.Max(g, b)), min = Math.Min(r, Math.Min(g, b)), d = max - min;
            v = max / 255f; s = max == 0 ? 0 : d / (float)max;
            if (d == 0) { h = 0; return; }
            if (max == r) h = 60f * (((g - b) / (float)d) % 6f);
            else if (max == g) h = 60f * (2f + (b - r) / (float)d);
            else h = 60f * (4f + (r - g) / (float)d);
            if (h < 0) h += 360f;
        }

        /// <summary>Recolours the bluish (hue 170-250) tinted pixels inside rect to targetHue (panel body + header only).</summary>
        public static Bitmap HueShift(Bitmap src, Rectangle rect, float targetHue)
        {
            Bitmap b = new Bitmap(src.Width, src.Height, PixelFormat.Format32bppArgb);
            using (Graphics g = Graphics.FromImage(b)) g.DrawImage(src, 0, 0, src.Width, src.Height);
            for (int y = rect.Top; y < rect.Bottom && y < b.Height; y++)
                for (int x = rect.Left; x < rect.Right && x < b.Width; x++)
                {
                    Color c = b.GetPixel(x, y);
                    float h, s, v; RgbToHsv(c.R, c.G, c.B, out h, out s, out v);
                    if (s < 0.12f || h < 170f || h > 250f) continue;
                    int r2, g2, b2; HsvToRgb(targetHue, s, v, out r2, out g2, out b2);
                    b.SetPixel(x, y, Color.FromArgb(r2, g2, b2));
                }
            return b;
        }

        public static void Run(string dir, Action<string, bool, string> check, Action<string> info)
        {
            Console.WriteLine();
            Console.WriteLine("=== Part 1b: other themes (pink, hue-shifted) and the LOADING state ===");
            using (Bitmap pink = new Bitmap(Path.Combine(dir, "tp_pink_open.png")))
            {
                FriendsPanelInfo p = TeleportDetector.FindPanel(pink);
                check("pink theme: panel found (theme independent)", p != null, p == null ? "" : p.Bounds.ToString());
                if (p != null)
                {
                    TeleportDetector.PanelTabs tabs = TeleportDetector.FindTabs(pink, p);
                    check("pink theme: red X found", p.HasCloseButton, p.CloseButton.ToString());
                    check("pink theme: green Friends + dark Nearby tabs found (Friends left of Nearby)", tabs != null && tabs.Friends.Right <= tabs.Nearby.Left + 6, tabs == null ? "none" : tabs.Friends + " " + tabs.Nearby);
                    check("pink theme: 5 rows, first 4 online (Teleport), 5th offline", p.Rows.Count == 5 && p.Rows[0].Online && p.Rows[1].Online && p.Rows[2].Online && p.Rows[3].Online && !p.Rows[4].Online, "rows=" + p.Rows.Count);
                    TeleportDetector.ReadNames(pink, p);
                    info("pink names: " + TeleportDetector.DescribeNames(p));
                    string[] want = { "kugixoxo", "cutiewutie87x", "naittxoxo", "naitdog15" };
                    for (int i = 0; i < want.Length; i++)
                    {
                        NameMatch m = TeleportDetector.MatchTarget(p, want[i]);
                        check("pink theme: OCR finds '" + want[i] + "' as a Teleport row", m.Status == MatchStatus.Found && m.Row != null && m.Row.Online && m.Row.Index == i, m.Status + " " + m.Message);
                    }
                    NameMatch mo = TeleportDetector.MatchTarget(p, "teagan_1537");
                    check("pink theme: 'teagan_1537' is found but Offline (never clickable)", mo.Status == MatchStatus.Found && mo.Row != null && !mo.Row.Online, mo.Status + " " + mo.Message);
                    string bt;
                    check("pink theme: classified Loaded", TeleportDetector.ClassifyPanel(pink, p, out bt) == TeleportDetector.PanelState.Loaded, "");
                }
                check("pink theme: FRIENDS sticker found (it is pink in every theme)", TeleportDetector.FindFriendsButton(pink) != null, "");
            }

            using (Bitmap load = new Bitmap(Path.Combine(dir, "tp_loading_panel.png")))
            {
                FriendsPanelInfo p = TeleportDetector.FindPanel(load, true);
                check("LOADING fixture: panel detected, no rows", p != null && p.Rows.Count == 0, p == null ? "no panel" : p.Rows.Count + " rows");
                if (p != null)
                {
                    string bt;
                    TeleportDetector.PanelState st = TeleportDetector.ClassifyPanel(load, p, out bt);
                    info("loading body OCR text: '" + bt + "' (OCR " + (TeleportOcr.IsAvailable ? "available" : "NOT available") + ")");
                    check("LOADING fixture: classified Loading (not Empty / no rows)", st == TeleportDetector.PanelState.Loading, st + " text='" + bt + "'");
                    TeleportDetector.PanelTabs tabs = TeleportDetector.FindTabs(load, p);
                    check("LOADING fixture: Friends/Nearby tabs found", tabs != null, tabs == null ? "none" : tabs.Friends + " " + tabs.Nearby);
                }
                check("LOADING fixture: FindPanel without allowNoRows returns nothing (rows needed)", TeleportDetector.FindPanel(load, false) == null, "");
            }

            using (Bitmap full = new Bitmap(Path.Combine(dir, "tp_full_panel.png")))
            {
                string bt = "";
                FriendsPanelInfo pf = TeleportDetector.FindPanel(full);
                check("blue fixture still Loaded", pf != null && TeleportDetector.ClassifyPanel(full, pf, out bt) == TeleportDetector.PanelState.Loaded, "");
                using (Bitmap blank = BlankBody(full))
                {
                    FriendsPanelInfo pb = TeleportDetector.FindPanel(blank, true);
                    TeleportDetector.PanelState sb = pb == null ? TeleportDetector.PanelState.Closed : TeleportDetector.ClassifyPanel(blank, pb, out bt);
                    info("blank body: panel " + (pb == null ? "none" : pb.Bounds.ToString()) + " text='" + bt + "'"); check("blank body (empty list) is Empty, not Loading", sb == TeleportDetector.PanelState.Empty, sb.ToString());
                }
                float[] hues = { 330f, 285f, 130f, 30f };
                string[] nm = { "pink", "purple", "green", "orange" };
                for (int i = 0; i < hues.Length; i++)
                {
                    using (Bitmap shifted = HueShift(full, new Rectangle(BluePanel.Left - 4, BluePanel.Top - 32, BluePanel.Width + 8, BluePanel.Height + 36), hues[i]))
                    {
                        FriendsPanelInfo ps = TeleportDetector.FindPanel(shifted); if (ps == null) { FriendsPanelInfo p2 = TeleportDetector.FindPanel(shifted, true); info(nm[i] + " allowNoRows: " + (p2 == null ? "none" : p2.Bounds + " rows " + p2.Rows.Count)); Color pc = shifted.GetPixel(600, 400); info("body pixel " + pc); }
                        check("hue-shifted blue -> " + nm[i] + ": panel found with 5 rows, X and tabs", ps != null && ps.Rows.Count == 5 && ps.HasCloseButton && TeleportDetector.FindTabs(shifted, ps) != null, ps == null ? "no panel" : ps.Rows.Count + " rows");
                    }
                }
            }

            using (Bitmap solid = new Bitmap(1000, 560))
            {
                using (Graphics g = Graphics.FromImage(solid)) g.Clear(Color.FromArgb(255, 225, 240));
                check("negative: solid pale pink image has no panel", TeleportDetector.FindPanel(solid, true) == null, "");
                using (Graphics g = Graphics.FromImage(solid)) g.Clear(Color.FromArgb(235, 255, 235));
                check("negative: solid pale green image has no panel", TeleportDetector.FindPanel(solid, true) == null, "");
            }

            // WaitForList with a scripted capture: 3 loading frames, then loaded.
            using (Bitmap load = new Bitmap(Path.Combine(dir, "tp_loading_panel.png")))
            using (Bitmap pink = new Bitmap(Path.Combine(dir, "tp_pink_open.png")))
            {
                int n = 0;
                List<string> prog = new List<string>();
                Func<Bitmap> cap = delegate { n++; return (Bitmap)(n <= 3 ? load : pink).Clone(); };
                TeleportDetector.ListWaitResult r = TeleportDetector.WaitForList(cap, 10000, 30, 100000, 1000, null, delegate(int ms, TeleportDetector.PanelState s) { prog.Add(s.ToString()); }, CancellationToken.None);
                check("WaitForList: 3 loading polls then Loaded, frame returned", r.State == TeleportDetector.PanelState.Loaded && r.Polls == 4 && r.Frame != null && prog.Count == 4 && prog[0] == "Loading", r.State + " polls=" + r.Polls + " " + string.Join(",", prog.ToArray()));
                if (r.Frame != null) r.Frame.Dispose();
                n = 0;
                r = TeleportDetector.WaitForList(delegate { return (Bitmap)load.Clone(); }, 600, 40, 100000, 1000, null, null, CancellationToken.None);
                check("WaitForList: stays Loading until the timeout (no early 'no rows')", r.State == TeleportDetector.PanelState.Loading && r.ElapsedMs >= 550, r.State + " " + r.ElapsedMs + " ms");
                if (r.Frame != null) r.Frame.Dispose();
                int rec = 0;
                r = TeleportDetector.WaitForList(delegate { return (Bitmap)load.Clone(); }, 900, 40, 300, 1000, delegate(FriendsPanelInfo pp) { rec++; return true; }, null, CancellationToken.None);
                check("WaitForList: recovery callback runs exactly once", rec == 1 && r.Recovered, "calls=" + rec);
                if (r.Frame != null) r.Frame.Dispose();
                CancellationTokenSource cts = new CancellationTokenSource();
                ThreadPool.QueueUserWorkItem(delegate { Thread.Sleep(250); cts.Cancel(); });
                System.Diagnostics.Stopwatch sw = System.Diagnostics.Stopwatch.StartNew();
                r = TeleportDetector.WaitForList(delegate { return (Bitmap)load.Clone(); }, 20000, 5000, 100000, 1000, null, null, cts.Token);
                check("WaitForList: Cancel aborts a long poll promptly", r.Cancelled && sw.ElapsedMilliseconds < 3000, sw.ElapsedMilliseconds + " ms");
                if (r.Frame != null) r.Frame.Dispose();
            }
        }

        private static Bitmap BlankBody(Bitmap full)
        {
            Bitmap b = new Bitmap(full);
            using (Graphics g = Graphics.FromImage(b))
            using (Brush br = new SolidBrush(Color.FromArgb(179, 239, 255)))
            {
                g.FillRectangle(br, BluePanel.Left + 20, BluePanel.Top + 52, BluePanel.Width - 40, BluePanel.Height - 62);
            }
            return b;
        }
    }
}
