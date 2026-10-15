// TeleportTests.cs - console test runner for the Teleport feature (C# 5).
// Part 1: detector/OCR/matching on the real reference screenshots (tests\tp_*.png, tests\cashout_ref.png),
//         scaled copies and negative images.
// Part 2: the real TeleportService driving a throw-away WinForms "fake game" window that renders the reference
//         images and reacts to real SendInput clicks (FRIENDS -> panel -> Teleport on the right row).
// Build: tests\build_tests.ps1   Run: tests\out\TeleportTests.exe [detector|service|all] [--no-ocr-warm]
// Never touches Roblox or the launcher: all clicks go to the fake window created here.

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Windows.Forms;

namespace RbxMulti
{
    public static class TeleportTests
    {
        private static int _pass, _fail;
        private static string _dir;

        [DllImport("user32.dll")] private static extern bool SetProcessDpiAwarenessContext(IntPtr ctx);
        [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
        [DllImport("user32.dll")] private static extern bool GetCursorPos(out Point p);
        [DllImport("user32.dll")] private static extern bool SetCursorPos(int x, int y);
        [DllImport("user32.dll")] private static extern bool SetWindowPos(IntPtr h, IntPtr after, int x, int y, int cx, int cy, uint flags);

        [DllImport("user32.dll")] private static extern bool PostMessage(IntPtr h, uint msg, IntPtr w, IntPtr l);
        private static bool _real;
        private static string _evRoot;
        private static TeleportResult _lastResult;
        private static string[] EvDirs(string kind) { try { return Directory.GetDirectories(_evRoot, "fail_*_" + kind); } catch (Exception) { return new string[0]; } }
        private static string ReadReport(string dir) { try { return File.ReadAllText(Path.Combine(dir, "report.txt")); } catch (Exception) { return ""; } }

        // True when this process can really move the mouse (it cannot while the screen is locked, etc.).
        private static bool ProbeInput()
        {
            Point p0; if (!GetCursorPos(out p0)) return false;
            bool ok = SetCursorPos(p0.X + 1, p0.Y);
            Point p1; GetCursorPos(out p1);
            SetCursorPos(p0.X, p0.Y);
            return ok && p1.X == p0.X + 1;
        }

        private static bool PostClick(IntPtr h, int x, int y)
        {
            IntPtr lp = new IntPtr((y << 16) | (x & 0xFFFF));
            PostMessage(h, 0x201, new IntPtr(1), lp); Thread.Sleep(30);
            PostMessage(h, 0x202, IntPtr.Zero, lp); Thread.Sleep(60);
            return true;
        }

        private static void Check(string name, bool ok, string detail)
        {
            if (ok) _pass++; else _fail++;
            Console.WriteLine((ok ? "PASS  " : "FAIL  ") + name + (string.IsNullOrEmpty(detail) ? "" : "  [" + detail + "]"));
        }

        private static void Info(string s) { Console.WriteLine("      " + s); }

        private static string P(string f) { return Path.Combine(_dir, f); }

        public static int Main(string[] args)
        {
            try { SetProcessDpiAwarenessContext(new IntPtr(-4)); } catch (Exception) { }
            _dir = Path.GetDirectoryName(Path.GetDirectoryName(Path.GetDirectoryName(System.Reflection.Assembly.GetExecutingAssembly().Location)));
            _dir = Path.Combine(_dir, "tests");
            if (!File.Exists(P("tp_full_panel.png"))) _dir = Path.Combine(Directory.GetCurrentDirectory(), "tests");
            string which = args.Length > 0 ? args[0] : "all";
            Console.WriteLine("Fixtures: " + _dir);
            Console.WriteLine("OCR available: " + TeleportOcr.IsAvailable + " language=" + TeleportOcr.Language + " err=" + TeleportOcr.LastError);
            string evRoot = Path.Combine(Path.GetTempPath(), "rbx_tp_evidence_" + Guid.NewGuid().ToString("N"));
            _evRoot = evRoot;
            InputDiagnostics.RootOverride = evRoot;   // failure evidence of the tests never goes to the real diag folder
            if (which == "all" || which == "detector") DetectorTests();
            if (which == "all" || which == "detector" || which == "theme") ThemeLoadTests.Run(_dir, Check, Info);
            if (which == "all" || which == "input") InputTests.Run(Check, Info);
            InputDiagnostics.RootOverride = evRoot;   // never the real diag folder, whatever an earlier part restored
            if (which == "all" || which == "service") ServiceTests();
            if (which == "all" || which == "cashout") CashOutTests.Run(_dir, Check, Info);
            if (which == "all" || which == "invite") InviteTests.Run(_dir, Check, Info);
            if (which == "all" || which == "hospital") HospitalTests.Run(_dir, Check, Info);
            if (which == "all" || which == "walk") WalkTests.Run(_dir, Check, Info);
            if (which == "all" || which == "clicker") ClickerOptionTests.Run(_dir, Check, Info);
            if (which == "all" || which == "order") RequestOrderTests.Run(_dir, Check, Info);
            if (which == "all" || which == "aimsim") AimSimTests.Run(_dir, Check, Info);
            if (which == "all" || which == "routedrag") RouteDragTests.Run(_dir, Check, Info);
            if (which == "all" || which == "switch") SwitchTests.Run(_dir, Check, Info);
            Console.WriteLine();
            try { Directory.Delete(evRoot, true); } catch (Exception) { }
            Console.WriteLine("RESULT: " + _pass + " passed, " + _fail + " failed");
            return _fail == 0 ? 0 : 1;
        }

        // =====================================================================================
        // Part 1: detector
        // =====================================================================================

        private static Bitmap Scale(Bitmap src, int w, int h)
        {
            Bitmap b = new Bitmap(w, h, PixelFormat.Format32bppArgb);
            using (Graphics g = Graphics.FromImage(b))
            {
                g.InterpolationMode = InterpolationMode.HighQualityBicubic;
                g.PixelOffsetMode = PixelOffsetMode.HighQuality;
                g.DrawImage(src, new Rectangle(0, 0, w, h), new Rectangle(0, 0, src.Width, src.Height), GraphicsUnit.Pixel);
            }
            return b;
        }

        private static Bitmap Fill(Bitmap src, Rectangle r, Color c)
        {
            Bitmap b = new Bitmap(src);
            using (Graphics g = Graphics.FromImage(b)) { using (Brush br = new SolidBrush(c)) g.FillRectangle(br, r); }
            return b;
        }

        // Truth (hand measured on tp_full_panel.png, 1144x692 including the 30px title bar).
        private static readonly Point TruthFriends = new Point(1094, 519);          // centre of the ribbon (label)
        private static readonly Point TruthIcon = new Point(1094, 473);             // centre of the white paper with the pink people
        private static readonly Rectangle TruthIconBox = new Rectangle(1053, 444, 82, 58);
        private static readonly Rectangle TruthPanel = new Rectangle(329, 224, 487, 273);
        private static readonly string[] Names = { "RubyPlexo", "kugixoxo", "naittxoxo", "cutiewutie87x", "Kugixo" };
        private static readonly int[] TruthRowY = { 290, 336, 381, 426, 472 };

        private static void DetectorTests()
        {
            Console.WriteLine();
            Console.WriteLine("=== Part 1: detector on real screenshots ===");

            Stopwatch sw = Stopwatch.StartNew();
            using (Bitmap warm = new Bitmap(P("tp_panel_crop.png")))
            {
                FriendsPanelInfo wp = TeleportDetector.FindPanel(warm);
                if (wp != null) TeleportDetector.ReadNames(warm, wp);
            }
            Info("OCR warm-up (first call): " + sw.ElapsedMilliseconds + " ms");

            using (Bitmap full = new Bitmap(P("tp_full_panel.png")))
            using (Bitmap side = new Bitmap(P("tp_sidebar.png")))
            using (Bitmap btn = new Bitmap(P("tp_friends_btn.png")))
            using (Bitmap crop = new Bitmap(P("tp_panel_crop.png")))
            using (Bitmap tele = new Bitmap(P("tp_teleport_btn.png")))
            using (Bitmap cash = new Bitmap(P("cashout_ref.png")))
            {
                // --- FRIENDS button
                sw.Restart();
                FriendsButtonInfo fb = TeleportDetector.FindFriendsButton(full);
                long ms = sw.ElapsedMilliseconds;
                Check("full window: FRIENDS button found", fb != null, ms + " ms");
                if (fb != null)
                {
                    double dist = Dist(fb.Click, TruthIcon);
                    Check("full window: click point is on the ICON (within 14 px of the icon centre, above the ribbon), not on the label", dist <= 14 && fb.Click.Y < fb.Label.Y && TeleportDetector.PinkIconAt(full, fb.Click), "click=" + fb.Click + " truthIcon=" + TruthIcon + " dist=" + dist.ToString("0.0") + " label=" + fb.Label + " icon=" + fb.Icon + " score=" + fb.Score.ToString("0.00"));
                    Check("full window: icon rect close to the measured paper (IoU > 0.8)", Overlap(fb.Icon, TruthIconBox) > 0.8, "icon=" + fb.Icon + " truth=" + TruthIconBox + " iou=" + Overlap(fb.Icon, TruthIconBox).ToString("0.00"));
                    Check("full window: ladder has 3 distinct points inside Bounds, all above the ribbon", fb.Ladder != null && fb.Ladder.Length == 3 && Dist(fb.Ladder[0], fb.Ladder[1]) >= 8 && Dist(fb.Ladder[0], fb.Ladder[2]) >= 8 && Dist(fb.Ladder[1], fb.Ladder[2]) >= 8 && fb.Bounds.Contains(fb.Ladder[0]) && fb.Bounds.Contains(fb.Ladder[1]) && fb.Bounds.Contains(fb.Ladder[2]) && fb.Ladder[0].Y < fb.Label.Y && fb.Ladder[1].Y < fb.Label.Y && fb.Ladder[2].Y < fb.Label.Y, fb.Ladder == null ? "null" : string.Join(" ", Array.ConvertAll<Point, string>(fb.Ladder, delegate(Point q) { return q.ToString(); })));
                }
                FriendsButtonInfo fs = TeleportDetector.FindFriendsButton(side);
                Check("sidebar strip: FRIENDS button found", fs != null, fs == null ? "" : "click=" + fs.Click + " score=" + fs.Score.ToString("0.00"));
                if (fs != null && fb != null)
                {
                    // The sidebar crop starts at x = 992 of the full window.
                    Check("sidebar strip: agrees with full window (x offset 992)", Math.Abs(fs.Click.X + 992 - fb.Click.X) <= 4 && Math.Abs(fs.Click.Y - fb.Click.Y) <= 3, "sidebar=" + fs.Click + " full=" + fb.Click);
                }
                FriendsButtonInfo fbtn = TeleportDetector.FindFriendsButton(btn);
                Check("button crop (100x103): FRIENDS found", fbtn != null, fbtn == null ? "" : "click=" + fbtn.Click);
                FriendsButtonInfo fc = TeleportDetector.FindFriendsButton(cash);
                Check("cashout_ref.png (also an Adopt Me frame with the sidebar): FRIENDS found", fc != null, fc == null ? "" : "label=" + fc.Label + " click=" + fc.Click);
                if (fc != null) Check("cashout_ref.png: FRIENDS click lands in the bottom-right column", fc.Click.X > cash.Width * 0.88 && fc.Click.Y > cash.Height * 0.65 && fc.Click.Y < cash.Height * 0.85, "click=" + fc.Click + " of " + cash.Width + "x" + cash.Height);

                // --- panel
                sw.Restart();
                FriendsPanelInfo panel = TeleportDetector.FindPanel(full);
                ms = sw.ElapsedMilliseconds;
                Check("full window: panel found", panel != null, ms + " ms");
                if (panel != null)
                {
                    Check("full window: panel bounds close to truth", Overlap(panel.Bounds, TruthPanel) > 0.93, "bounds=" + panel.Bounds + " iou=" + Overlap(panel.Bounds, TruthPanel).ToString("0.00"));
                    Check("full window: red X found at top-right", panel.HasCloseButton && panel.CloseButton.X > panel.Bounds.Right - 70 && panel.CloseButton.Y < panel.Bounds.Y + 60, "X=" + panel.CloseButton);
                    Check("full window: 5 rows (4 Teleport + 1 Offline)", panel.Rows.Count == 5 && CountOn(panel) == 4, "rows=" + panel.Rows.Count + " online=" + CountOn(panel));
                    for (int i = 0; i < panel.Rows.Count && i < 5; i++)
                    {
                        Rectangle b = panel.Rows[i].ButtonRect;
                        Check("row " + i + ": button centre y within 6 px of truth and state " + (i < 4 ? "Online" : "Offline"),
                            Math.Abs(b.Y + b.Height / 2 - TruthRowY[i]) <= 6 && panel.Rows[i].Online == (i < 4), "centreY=" + (b.Y + b.Height / 2) + " truth=" + TruthRowY[i] + " online=" + panel.Rows[i].Online);
                    }
                    sw.Restart();
                    TeleportDetector.ReadNames(full, panel);
                    ms = sw.ElapsedMilliseconds;
                    Info("OCR of 5 rows: " + ms + " ms");
                    for (int i = 0; i < panel.Rows.Count && i < 5; i++)
                    {
                        Info("row " + i + " OCR raw='" + panel.Rows[i].RawName + "' expected='" + Names[i] + "' similarity=" + TeleportDetector.Similarity(panel.Rows[i].Name, Names[i]).ToString("0.00"));
                    }
                    foreach (string n in Names)
                    {
                        NameMatch m = TeleportDetector.MatchTarget(panel, n);
                        int idx = m.Row == null ? -1 : panel.Rows.IndexOf(m.Row);
                        bool expectOnline = n != "Kugixo";
                        Check("match '" + n + "' -> Found, correct row, " + (expectOnline ? "Online" : "Offline"),
                            m.Status == MatchStatus.Found && idx == Array.IndexOf(Names, n) && m.Row.Online == expectOnline,
                            m.Status + " row=" + idx + " score=" + m.Score.ToString("0.00") + " second=" + m.SecondScore.ToString("0.00"));
                    }
                    NameMatch mu = TeleportDetector.MatchTarget(panel, "KUGIXOXO");
                    Check("match is case-insensitive (KUGIXOXO)", mu.Status == MatchStatus.Found && panel.Rows.IndexOf(mu.Row) == 1, mu.Status.ToString());
                    NameMatch mn = TeleportDetector.MatchTarget(panel, "zzzzunknown");
                    Check("unknown name -> NotFound", mn.Status == MatchStatus.NotFound, mn.Status.ToString());
                    NameMatch mt = TeleportDetector.MatchTarget(panel, "kugixoxx");
                    Check("one-letter typo of kugixoxo is never matched to the wrong (Offline) row", mt.Status != MatchStatus.Found || panel.Rows.IndexOf(mt.Row) == 1, mt.Status + " score=" + mt.Score.ToString("0.00") + " second=" + mt.SecondScore.ToString("0.00"));
                    NameMatch ms2 = TeleportDetector.MatchTarget(panel, "Rub");
                    Check("very short / partial name 'Rub' is not accepted", ms2.Status != MatchStatus.Found, ms2.Status.ToString());
                }

                FriendsPanelInfo pc = TeleportDetector.FindPanel(crop);
                Check("panel crop (500x307): panel found with 5 rows", pc != null && pc.Rows.Count == 5, pc == null ? "" : "rows=" + pc.Rows.Count + " bounds=" + pc.Bounds);
                if (pc != null)
                {
                    TeleportDetector.ReadNames(crop, pc);
                    int ok = 0;
                    for (int i = 0; i < 5; i++) if (TeleportDetector.Similarity(pc.Rows[i].Name, Names[i]) >= 0.85) ok++;
                    Check("panel crop: names read (>= 0.85 similarity) for 5/5 rows", ok == 5, ok + "/5: " + string.Join(" | ", Raw(pc)));
                }

                // --- negatives
                Check("negative: Teleport button crop has no panel", TeleportDetector.FindPanel(tele) == null, "");
                Check("negative: Teleport button crop has no FRIENDS button", TeleportDetector.FindFriendsButton(tele) == null, "");
                Check("negative: panel crop has no FRIENDS button", TeleportDetector.FindFriendsButton(crop) == null, "");
                Check("negative: sidebar strip has no panel", TeleportDetector.FindPanel(side) == null, "");
                Check("negative: cashout_ref.png has no panel (panel closed)", TeleportDetector.FindPanel(cash) == null, "");

                using (Bitmap closed = MakeClosed(full))
                {
                    Check("panel closed (synthetic: panel painted over): no panel", TeleportDetector.FindPanel(closed) == null, "");
                    FriendsButtonInfo f2 = TeleportDetector.FindFriendsButton(closed);
                    Check("panel closed: FRIENDS button still found", f2 != null, "");
                }
                using (Bitmap nobtn = Fill(full, new Rectangle(1030, 440, 114, 110), Color.FromArgb(150, 110, 80)))
                {
                    Check("sidebar button painted over: no FRIENDS button", TeleportDetector.FindFriendsButton(nobtn) == null, "");
                }
                using (Bitmap nobtn2 = Fill(cash, new Rectangle(1000, 370, 103, 110), Color.FromArgb(200, 140, 110)))
                {
                    Check("cashout_ref with the button painted over: no FRIENDS button", TeleportDetector.FindFriendsButton(nobtn2) == null, "");
                }
                using (Bitmap solid = new Bitmap(1000, 560)) { using (Graphics g = Graphics.FromImage(solid)) g.Clear(Color.FromArgb(179, 239, 255)); Check("negative: solid light-blue image has no panel", TeleportDetector.FindPanel(solid) == null, ""); }
                using (Bitmap pink = new Bitmap(1000, 560)) { using (Graphics g = Graphics.FromImage(pink)) g.Clear(Color.FromArgb(237, 44, 121)); Check("negative: solid pink image has no FRIENDS button", TeleportDetector.FindFriendsButton(pink) == null, ""); }
                Random rnd = new Random(7);
                using (Bitmap noise = new Bitmap(800, 450))
                {
                    for (int y = 0; y < noise.Height; y++) for (int x = 0; x < noise.Width; x++) noise.SetPixel(x, y, Color.FromArgb(rnd.Next(256), rnd.Next(256), rnd.Next(256)));
                    Check("negative: random noise: no panel and no FRIENDS button", TeleportDetector.FindPanel(noise) == null && TeleportDetector.FindFriendsButton(noise) == null, "");
                }

                LayoutTests(full, cash);
                // --- scaled copies
                int[][] sizes = new int[][] { new int[] { 640, 387 }, new int[] { 800, 484 }, new int[] { 960, 581 }, new int[] { 1280, 774 }, new int[] { 1600, 968 }, new int[] { 1920, 1161 }, new int[] { 1280, 720 }, new int[] { 1920, 1080 }, new int[] { 2560, 1548 } };
                foreach (int[] sz in sizes)
                {
                    using (Bitmap sc = Scale(full, sz[0], sz[1]))
                    {
                        double kx = sz[0] / (double)full.Width, ky = sz[1] / (double)full.Height;
                        string tag = "scaled " + sz[0] + "x" + sz[1] + ": ";
                        sw.Restart();
                        FriendsButtonInfo f = TeleportDetector.FindFriendsButton(sc);
                        long t1 = sw.ElapsedMilliseconds; sw.Restart();
                        FriendsPanelInfo p = TeleportDetector.FindPanel(sc);
                        long t2 = sw.ElapsedMilliseconds;
                        Point exp = new Point((int)(TruthIcon.X * kx), (int)(TruthIcon.Y * ky));
                        double tol = Math.Max(8, 14 * Math.Max(kx, ky));
                        Check(tag + "FRIENDS click point on the icon near truth", f != null && Dist(f.Click, exp) <= tol, f == null ? "null" : "click=" + f.Click + " exp=" + exp + " (" + t1 + " ms)");
                        Check(tag + "panel + 5 rows (4 online)", p != null && p.Rows.Count == 5 && CountOn(p) == 4, p == null ? "null" : "rows=" + p.Rows.Count + " (" + t2 + " ms)");
                        if (p != null && p.Rows.Count == 5)
                        {
                            TeleportDetector.ReadNames(sc, p);
                            int found = 0;
                            foreach (string n in Names)
                            {
                                NameMatch m = TeleportDetector.MatchTarget(p, n);
                                if (m.Status == MatchStatus.Found && p.Rows.IndexOf(m.Row) == Array.IndexOf(Names, n)) found++;
                            }
                            Check(tag + "all 5 names matched to the right row (OCR)", found == 5, found + "/5: " + string.Join(" | ", Raw(p)));
                        }
                    }
                }
            }
        }

        // =====================================================================================
        // Tall / odd aspect windows: failure fixtures from the user PC + stretched copies of the reference
        // =====================================================================================

        private static void LayoutTests(Bitmap full, Bitmap cash)
        {
            Console.WriteLine();
            Console.WriteLine("--- tall window failure fixtures (1716x1358, user PC) ---");
            // Hand-measured on tp_fail_tall_closed.png: white paper with the people, and the tilted FRIENDS ribbon below it.
            // Fixture 1 is tilted ~12 degrees (axis-aligned box of the paper), fixture 2 is the upright pose.
            foreach (string fn in new string[] { "tp_fail_tall_closed.png", "tp_fail_tall_closed2.png" })
            {
                Rectangle paper = fn == "tp_fail_tall_closed.png" ? new Rectangle(1620, 756, 86, 70) : new Rectangle(1628, 763, 78, 55);
                using (Bitmap tall = new Bitmap(P(fn)))
                {
                    FriendsButtonInfo f = TeleportDetector.FindFriendsButton(tall);
                    Check(fn + ": FRIENDS found in the " + tall.Width + "x" + tall.Height + " capture", f != null, f == null ? "null" : "label=" + f.Label + " icon=" + f.Icon + " bounds=" + f.Bounds);
                    if (f != null)
                    {
                        Info("click=" + f.Click + " ladder=" + string.Join(" ", Array.ConvertAll<Point, string>(f.Ladder, delegate(Point q) { return q.ToString(); })) + " icon=" + f.Icon + " bounds=" + f.Bounds + " label=" + f.Label);
                        Check(fn + ": click is NOT on the ribbon label (the old click was (1667,837))", Dist(f.Click, new Point(1667, 837)) > 30 && f.Click.Y < f.Label.Y, "click=" + f.Click + " label=" + f.Label);
                        Check(fn + ": click point lies on pink icon pixels", TeleportDetector.PinkIconAt(tall, f.Click), "click=" + f.Click);
                        Check(fn + ": click inside the paper area", Rectangle.Inflate(paper, 6, 6).Contains(f.Click), "click=" + f.Click + " paper=" + paper);
                        Check(fn + ": ladder points 1 and 3 also on pink pixels, all inside the sticker", TeleportDetector.PinkIconAt(tall, f.Ladder[0]) && TeleportDetector.PinkIconAt(tall, f.Ladder[2]) && f.Bounds.Contains(f.Ladder[1]), "");
                        Check(fn + ": Bounds cover the whole sticker (paper + tilted ribbon, with margin)", f.Bounds.Contains(paper) && f.Bounds.Left <= 1622 && f.Bounds.Bottom >= 852 && f.Bounds.Right >= 1712, "bounds=" + f.Bounds);
                        // Draw the measurement for the record.
                        using (Bitmap ann = InputDiagnostics.Annotate(tall, f.Click, f.Bounds, "detector: bounds + click ladder P0"))
                        using (Graphics g = Graphics.FromImage(ann))
                        {
                            foreach (Point q in f.Ladder) g.DrawEllipse(Pens.Lime, q.X - 5, q.Y - 5, 10, 10);
                            g.DrawRectangle(Pens.Cyan, f.Icon);
                            try { Directory.CreateDirectory(Path.Combine(_dir, "shots_teleport")); ann.Save(Path.Combine(_dir, "shots_teleport", "layout_" + fn), ImageFormat.Png); } catch (Exception) { }
                        }
                    }
                    Check(fn + ": closed panel -> no panel", TeleportDetector.FindPanel(tall) == null && TeleportDetector.FindPanel(tall, true) == null, "");
                }
            }

            Console.WriteLine("--- reference stretched to many aspect ratios (non-uniform stretch included) ---");
            int[][] dims = new int[][] { new int[] { 1716, 1358 }, new int[] { 1200, 1400 }, new int[] { 2000, 700 }, new int[] { 800, 600 }, new int[] { 1000, 1000 }, new int[] { 3440, 1440 }, new int[] { 1100, 1400 }, new int[] { 640, 720 } };   // (a 2.7x non-uniform squash such as 900x1500 is beyond what any Roblox layout does and is not supported; 2.1x as in 1100x1400 is)
            Random rnd = new Random(11);
            int totalNames = 0, okNames = 0;
            foreach (int[] d in dims)
            {
                double kx = d[0] / (double)full.Width, ky = d[1] / (double)full.Height;
                string tag = "stretched " + d[0] + "x" + d[1] + " (kx " + kx.ToString("0.00") + ", ky " + ky.ToString("0.00") + "): ";
                using (Bitmap sc = Scale(full, d[0], d[1]))
                {
                    FriendsButtonInfo f = TeleportDetector.FindFriendsButton(sc);
                    double ex = Math.Abs(f == null ? 999 : (f.Click.X - TruthIcon.X * kx) / kx), ey = Math.Abs(f == null ? 999 : (f.Click.Y - TruthIcon.Y * ky) / ky);
                    Check(tag + "FRIENDS click on the icon (error in reference px " + ex.ToString("0") + "," + ey.ToString("0") + ")", f != null && ex <= 16 && ey <= 16 && TeleportDetector.PinkIconAt(sc, f.Click), f == null ? "null" : "click=" + f.Click);
                    FriendsPanelInfo p = TeleportDetector.FindPanel(sc);
                    Rectangle truthP = new Rectangle((int)(TruthPanel.X * kx), (int)(TruthPanel.Y * ky), (int)(TruthPanel.Width * kx), (int)(TruthPanel.Height * ky));
                    Check(tag + "panel found, 5 rows (4 online), bounds IoU > 0.85, red X", p != null && p.Rows.Count == 5 && CountOn(p) == 4 && Overlap(p.Bounds, truthP) > 0.85 && p.HasCloseButton, p == null ? "null" : "rows=" + p.Rows.Count + " iou=" + Overlap(p.Bounds, truthP).ToString("0.00") + " bounds=" + p.Bounds);
                    if (p != null && p.Rows.Count == 5)
                    {
                        bool geo = true; string why = "";
                        for (int i = 0; i < 5; i++)
                        {
                            PanelRow r = p.Rows[i];
                            double cy = r.ButtonRect.Y + r.ButtonRect.Height / 2.0;
                            if (Math.Abs(cy - TruthRowY[i] * ky) > 7 * ky + 3) { geo = false; why += " row" + i + " y=" + cy.ToString("0") + " exp " + (TruthRowY[i] * ky).ToString("0"); }
                            if (!(r.NameRect.Right <= r.ButtonRect.Left && r.NameRect.Left > p.Bounds.Left && r.NameRect.Height >= r.ButtonRect.Height && r.NameRect.Width > p.Bounds.Width * 0.15)) { geo = false; why += " row" + i + " namerect " + r.NameRect; }
                        }
                        Check(tag + "row bands at the scaled positions, name rect left of the button and within the panel", geo, why);
                        TeleportDetector.ReadNames(sc, p);
                        int ok = 0;
                        for (int i = 0; i < 5; i++) if (TeleportDetector.Similarity(p.Rows[i].Name, Names[i]) >= 0.85) ok++;
                        totalNames += 5; okNames += ok;
                        Info(tag + "OCR names read " + ok + "/5: " + string.Join(" | ", Raw(p)) + "  name rect h=" + p.Rows[0].NameRect.Height);
                        double need = (Math.Abs(kx / ky - 1) < 0.25) ? 5 : 3;   // glyphs stretched 2x sideways are hard for any OCR
                        Check(tag + "OCR names (>= " + need + "/5 correct)", ok >= need, ok + "/5");
                    }
                    // negatives at this aspect ratio
                    using (Bitmap noise = new Bitmap(d[0] / 2, d[1] / 2))
                    {
                        for (int y = 0; y < noise.Height; y++) for (int x = 0; x < noise.Width; x++) noise.SetPixel(x, y, Color.FromArgb(rnd.Next(256), rnd.Next(256), rnd.Next(256)));
                        Check(tag + "noise: no panel (strict + loose) and no FRIENDS button", TeleportDetector.FindPanel(noise) == null && TeleportDetector.FindPanel(noise, true) == null && TeleportDetector.FindFriendsButton(noise) == null, "");
                    }
                    using (Bitmap closed = MakeClosed(full))
                    using (Bitmap sc2 = Scale(closed, d[0], d[1]))
                    {
                        Check(tag + "closed panel: no panel, FRIENDS still found", TeleportDetector.FindPanel(sc2, true) == null && TeleportDetector.FindFriendsButton(sc2) != null, "");
                    }
                    using (Bitmap norows = NoRows(full))
                    using (Bitmap sc3 = Scale(norows, d[0], d[1]))
                    {
                        FriendsPanelInfo strict = TeleportDetector.FindPanel(sc3);
                        FriendsPanelInfo loose = TeleportDetector.FindPanel(sc3, true);
                        Check(tag + "panel without rows: strict null, loose finds it with 0 rows", strict == null && loose != null && loose.Rows.Count == 0 && loose.HasCloseButton, loose == null ? "loose null" : "rows=" + loose.Rows.Count);
                    }
                }
            }
            Info("OCR over all stretched sizes: " + okNames + "/" + totalNames + " names read correctly");

            Console.WriteLine("--- name-not-found message, avatar/name geometry, CASH OUT target ---");
            FriendsPanelInfo pr = TeleportDetector.FindPanel(full);
            if (pr != null)
            {
                TeleportDetector.ReadNames(full, pr);
                NameMatch nm = TeleportDetector.MatchTarget(pr, "DisplayNameOfSomeoneElse");
                Info("message: " + nm.Message);
                Check("name not found: clear message lists what was read and the target", nm.Status == MatchStatus.NotFound && nm.Message.StartsWith("Panel opened, read:") && nm.Message.Contains("kugixoxo") && nm.Message.Contains("RubyPlexo") && nm.Message.Contains("not among them") && nm.Message.Contains("DisplayNameOfSomeoneElse"), nm.Message);
                int minLeft = int.MaxValue; foreach (PanelRow r in pr.Rows) minLeft = Math.Min(minLeft, r.NameRect.Left);
                double frac = (minLeft - pr.Bounds.Left) / (double)pr.Bounds.Width;
                Check("reference: detected name column starts at 8-16% of the panel width (avatar detection agrees with the old 11.5%)", frac >= 0.08 && frac <= 0.16, "left=" + frac.ToString("0.000"));
            }
            Rectangle? co = CashOutDetector.Find(cash);
            Check("CASH OUT: button found in cashout_ref.png", co.HasValue, "");
            if (co.HasValue)
            {
                Point cp = AntiIdleClicker.CashOutClickPoint(co.Value);
                // The centre may fall on a white letter of "CASH OUT": judge the 21x21 neighbourhood and the button as a whole.
                int green = 0, tot = 0;
                for (int yy = cp.Y - 10; yy <= cp.Y + 10; yy++) for (int xx = cp.X - 10; xx <= cp.X + 10; xx++) { Color q = cash.GetPixel(xx, yy); tot++; if (q.GetHue() >= 80 && q.GetHue() <= 170 && q.GetSaturation() >= 0.3) green++; }
                Check("CASH OUT: click point is the centre of the detected button and the area around it is green", cp.X == co.Value.X + co.Value.Width / 2 && cp.Y == co.Value.Y + co.Value.Height / 2 && green >= tot * 0.5, "point=" + cp + " green " + green + "/" + tot + " rect=" + co.Value);
            }
        }

        // Reference with all row buttons and names painted over by the panel colour (panel open, list empty).
        private static Bitmap NoRows(Bitmap full)
        {
            Bitmap b = new Bitmap(full);
            using (Graphics g = Graphics.FromImage(b))
            using (Brush br = new SolidBrush(Color.FromArgb(179, 239, 255)))
            {
                g.FillRectangle(br, TruthPanel.Left + 20, TruthPanel.Top + 52, TruthPanel.Width - 40, TruthPanel.Height - 62);
            }
            return b;
        }

        // The annotated failure.png must differ from the raw capture.png where the panel border / rows are drawn.
        private static void CheckAnnotated(string dir)
        {
            try
            {
                using (Bitmap raw = new Bitmap(Path.Combine(dir, "capture.png")))
                using (Bitmap ann = new Bitmap(Path.Combine(dir, "failure.png")))
                {
                    int lime = 0;
                    for (int y = 40; y < ann.Height; y += 1) for (int x = 0; x < ann.Width; x += 1) { Color c = ann.GetPixel(x, y); if (c.R < 30 && c.G > 220 && c.B < 30) lime++; }
                    Check("evidence PNG " + Path.GetFileName(dir) + ": annotated copy has the panel rectangle drawn (lime) and the same size as the raw capture", raw.Size == ann.Size && lime > 100, "lime px " + lime);
                }
            }
            catch (Exception ex) { Check("evidence PNG readable " + dir, false, ex.Message); }
        }

        private static string[] Raw(FriendsPanelInfo p)
        {
            List<string> l = new List<string>();
            foreach (PanelRow r in p.Rows) l.Add(r.RawName);
            return l.ToArray();
        }

        private static int CountOn(FriendsPanelInfo p) { int n = 0; foreach (PanelRow r in p.Rows) if (r.Online) n++; return n; }
        private static double Dist(Point a, Point b) { return Math.Sqrt((a.X - b.X) * (a.X - b.X) + (a.Y - b.Y) * (a.Y - b.Y)); }
        private static double Overlap(Rectangle a, Rectangle b)
        {
            Rectangle i = Rectangle.Intersect(a, b);
            if (i.IsEmpty) return 0;
            double inter = i.Width * (double)i.Height;
            return inter / (a.Width * (double)a.Height + b.Width * (double)b.Height - inter);
        }

        // The reference image with the panel (and its X) painted over by wall-like stripes.
        private static Bitmap MakeClosed(Bitmap full)
        {
            Bitmap b = new Bitmap(full);
            Rectangle r = Rectangle.FromLTRB(TruthPanel.Left - 22, TruthPanel.Top - 10, TruthPanel.Right + 6, TruthPanel.Bottom + 10);
            using (Graphics g = Graphics.FromImage(b))
            {
                int x = r.Left;
                bool a = true;
                while (x < r.Right)
                {
                    using (Brush br = new SolidBrush(a ? Color.FromArgb(120, 200, 225) : Color.FromArgb(235, 245, 248)))
                        g.FillRectangle(br, x, r.Top, Math.Min(34, r.Right - x), r.Height);
                    x += 34; a = !a;
                }
            }
            return b;
        }

        // =====================================================================================
        // Part 2: service against a fake game window
        // =====================================================================================

        private enum FakeMode { SuccessScene, PanelCloseOnly, NoEffect, NoSidebar, ChangeKeepPanel, EmptyPanel, LoadingThenRows, LoadingForever, LoadingNeedsRecovery, ScrollNeeded }

        private sealed class FakeGame : Form
        {
            private readonly Bitmap _open, _closed, _scene2, _dark, _noSidebar, _openEmpty, _openLoading, _openScroll;
            public volatile int LoadMs = 2000;
            public Rectangle TabFriends, TabNearby;   // image coordinates (title bar included), set by the test from the detector
            private DateTime _openedAt = DateTime.UtcNow;
            private volatile bool _nearbyClicked, _recovered, _scrolled;
            public volatile int IgnoreFriendsClicks;   // the first N clicks on the FRIENDS sticker do nothing (to exercise the click ladder)
            public volatile string State = "closed";
            public FakeMode Mode = FakeMode.SuccessScene;
            public new readonly List<string> Events = new List<string>();
            public volatile bool KeepOnTop = true;
            private readonly object _lock = new object();
            private DateTime _transitionAt;
            private System.Windows.Forms.Timer _timer;

            public FakeGame(Bitmap full, int clientW)
            {
                // Remove the 31 px title bar of the screenshot so the client area is the game view only.
                Rectangle view = new Rectangle(0, 31, full.Width, full.Height - 31);
                _open = full.Clone(view, PixelFormat.Format32bppArgb);
                using (Bitmap c = MakeClosed(full)) _closed = c.Clone(view, PixelFormat.Format32bppArgb);
                using (Bitmap nr = NoRows(full)) _openEmpty = nr.Clone(view, PixelFormat.Format32bppArgb);
                using (Bitmap ld = NoRows(full))
                {
                    using (Graphics lg = Graphics.FromImage(ld))
                    using (Font lf = new Font("Arial", 20, FontStyle.Bold, GraphicsUnit.Pixel))
                    using (Brush lb = new SolidBrush(Color.FromArgb(40, 170, 230)))
                        lg.DrawString("Loading...", lf, lb, TruthPanel.Left + 175, TruthPanel.Top + 62);
                    _openLoading = ld.Clone(view, PixelFormat.Format32bppArgb);
                }
                using (Bitmap sc = Fill(full, new Rectangle(TruthPanel.Left + 4, TruthRowY[2] - 22, TruthPanel.Width - 8, 44), Color.FromArgb(179, 239, 255)))
                    _openScroll = sc.Clone(view, PixelFormat.Format32bppArgb);
                _scene2 = (Bitmap)_closed.Clone();
                _scene2.RotateFlip(RotateFlipType.RotateNoneFlipX);
                _dark = Fill(_closed, new Rectangle(0, 0, _closed.Width, _closed.Height), Color.FromArgb(20, 20, 30));
                using (Bitmap nb = Fill(full, new Rectangle(1030, 440, 114, 110), Color.FromArgb(150, 110, 80)))
                using (Bitmap nbc = MakeClosed(nb)) _noSidebar = nbc.Clone(view, PixelFormat.Format32bppArgb);

                Text = "Fake Roblox test window";
                StartPosition = FormStartPosition.Manual;
                Location = new Point(30, 20);
                ClientSize = new Size(clientW, (int)Math.Round(clientW * (double)_open.Height / _open.Width));
                TopMost = true;
                DoubleBuffered = true;
                _timer = new System.Windows.Forms.Timer();
                _timer.Interval = 40;
                _timer.Tick += delegate
                {
                    // Keep the fake game above every other topmost window (the real desktop may have some).
                    if (KeepOnTop && IsHandleCreated) SetWindowPos(Handle, new IntPtr(-1), 0, 0, 0, 0, 0x0001 | 0x0002 | 0x0010 | 0x0040);
                    if (State == "transition" && (DateTime.UtcNow - _transitionAt).TotalMilliseconds > 900) { State = "scene2"; Invalidate(); }
                    if (State == "open") Invalidate();
                };
                _timer.Start();
            }

            public Size ImageSize { get { return _open.Size; } }

            public void Log(string e) { lock (_lock) { Events.Add(e); } }
            public string[] Snapshot() { lock (_lock) { return Events.ToArray(); } }
            public void ResetEvents() { lock (_lock) { Events.Clear(); } }

            private Bitmap Current()
            {
                switch (State)
                {
                    case "open":
                        switch (Mode)
                        {
                            case FakeMode.EmptyPanel: return _openEmpty;
                            case FakeMode.LoadingThenRows: return (DateTime.UtcNow - _openedAt).TotalMilliseconds < LoadMs ? _openLoading : _open;
                            case FakeMode.LoadingForever: return _openLoading;
                            case FakeMode.LoadingNeedsRecovery: return _recovered ? _open : _openLoading;
                            case FakeMode.ScrollNeeded: return _scrolled ? _open : _openScroll;
                            default: return _open;
                        }
                    case "scene2": return _scene2;
                    case "transition": return _dark;
                    default: return Mode == FakeMode.NoSidebar ? _noSidebar : _closed;
                }
            }

            protected override void OnPaint(PaintEventArgs e)
            {
                e.Graphics.InterpolationMode = InterpolationMode.HighQualityBicubic;
                e.Graphics.DrawImage(Current(), new Rectangle(0, 0, ClientSize.Width, ClientSize.Height), 0, 0, _open.Width, _open.Height, GraphicsUnit.Pixel);
            }

            public void SimScroll() { Log("wheel:sim"); if (State == "open") { _scrolled = true; Invalidate(); } }

            protected override void OnMouseWheel(MouseEventArgs e)
            {
                Log("wheel:" + e.Delta);
                if (State == "open") { _scrolled = true; Invalidate(); }
                base.OnMouseWheel(e);
            }

            protected override void OnMouseDown(MouseEventArgs e)
            {
                double sx = _open.Width / (double)ClientSize.Width;
                double sy = _open.Height / (double)ClientSize.Height;
                int ix = (int)(e.X * sx), iy = (int)(e.Y * sy) + 31;   // truth coordinates include the 31 px title bar
                Log("click@" + ix + "," + iy + " state=" + State);
                if (State == "closed" || State == "scene2")
                {
                    if (Mode != FakeMode.NoSidebar && ix >= 1045 && ix <= 1142 && iy >= 450 && iy <= 537)
                    {
                        if (IgnoreFriendsClicks > 0) { IgnoreFriendsClicks--; Log("FRIENDS_IGNORED"); return; }
                        Log("FRIENDS");
                        if (State == "closed") { _openedAt = DateTime.UtcNow; _nearbyClicked = false; _recovered = false; _scrolled = false; State = "open"; Invalidate(); }
                    }
                }
                else if (State == "open")
                {
                    if (ix >= 779 && ix <= 814 && iy >= 227 && iy <= 262) { Log("CLOSE"); State = "closed"; Invalidate(); return; }
                    if (!TabNearby.IsEmpty && TabNearby.Contains(ix, iy)) { Log("NEARBY"); _nearbyClicked = true; return; }
                    if (!TabFriends.IsEmpty && TabFriends.Contains(ix, iy)) { Log("FRIENDSTAB"); if (_nearbyClicked) { _recovered = true; Invalidate(); } return; }
                    if (Mode == FakeMode.ScrollNeeded && !_scrolled && Math.Abs(iy - TruthRowY[2]) <= 15 && ix >= 664 && ix <= 780) { Log("TELEPORT_ON_BLANK"); return; }
                    if (Mode != FakeMode.SuccessScene && (Mode == FakeMode.LoadingForever || (Mode == FakeMode.LoadingThenRows && (DateTime.UtcNow - _openedAt).TotalMilliseconds < LoadMs) || (Mode == FakeMode.LoadingNeedsRecovery && !_recovered))) return;
                    if (ix >= 664 && ix <= 780)
                    {
                        for (int r = 0; r < 4; r++)
                        {
                            if (Math.Abs(iy - TruthRowY[r]) <= 15)
                            {
                                Log("TELEPORT:" + r + ":" + Names[r]);
                                if (Mode == FakeMode.NoEffect) return;
                                if (Mode == FakeMode.PanelCloseOnly) { State = "closed"; Invalidate(); return; }
                                if (Mode == FakeMode.ChangeKeepPanel) { return; }
                                _transitionAt = DateTime.UtcNow; State = "transition"; Invalidate();
                                return;
                            }
                        }
                        if (Math.Abs(iy - TruthRowY[4]) <= 15) Log("OFFLINE_CLICK");
                    }
                }
            }
        }

        private static FakeGame _fake;
        private static Thread _uiThread;

        private static FakeGame StartFake(Bitmap full, int clientW)
        {
            ManualResetEvent ready = new ManualResetEvent(false);
            _uiThread = new Thread(delegate()
            {
                Application.EnableVisualStyles();
                _fake = new FakeGame(full, clientW);
                _fake.Shown += delegate { ready.Set(); };
                Application.Run(_fake);
            });
            _uiThread.SetApartmentState(ApartmentState.STA);
            _uiThread.IsBackground = true;
            _uiThread.Start();
            ready.WaitOne(10000);
            Thread.Sleep(400);
            return _fake;
        }

        private static RobloxInstance Inst(int id, string name, string status, string account)
        {
            RobloxInstance r = new RobloxInstance();
            r.Id = id; r.Name = name; r.Status = status; r.AccountName = account;
            r.Pid = Process.GetCurrentProcess().Id;
            return r;
        }

        private static int Count(string[] ev, string prefix) { int n = 0; foreach (string e in ev) if (e.StartsWith(prefix)) n++; return n; }

        private static void ServiceTests()
        {
            Console.WriteLine();
            Console.WriteLine("=== Part 2: TeleportService against a fake game window (real SendInput clicks) ===");
            using (Bitmap full = new Bitmap(P("tp_full_panel.png")))
            {
                int clientW = Math.Min(1000, Screen.PrimaryScreen.Bounds.Width - 100);
                FakeGame fake = StartFake(full, clientW);
                Info("fake window handle=" + fake.Handle + " client=" + fake.ClientSize);
                IntPtr hwnd = fake.Handle;

                RobloxInstance a = Inst(1, "Instance A", "Running", "alice");
                RobloxInstance b = Inst(2, "Instance B", "Running", "kugixoxo");
                TeleportService svc = new TeleportService();
                svc.ObserveMs = 3500;
                svc.SettleMs = 150;
                svc.ListPollMs = 150;
                svc.EmptyGraceMs = 1200;
                {
                    FriendsPanelInfo tp = TeleportDetector.FindPanel(full);
                    TeleportDetector.PanelTabs tt = tp == null ? null : TeleportDetector.FindTabs(full, tp);
                    if (tt != null) { fake.TabFriends = tt.Friends; fake.TabNearby = tt.Nearby; }
                    Info("fake tabs: friends=" + (tt == null ? "?" : tt.Friends.ToString()) + " nearby=" + (tt == null ? "?" : tt.Nearby.ToString()));
                }
                List<string> statusLog = new List<string>();
                svc.Status += delegate(string s) { lock (statusLog) { statusLog.Add(s); } };
                List<string> log = new List<string>();
                svc.Log += delegate(string s) { lock (log) { log.Add(s); } };
                // Both instances resolve to the fake window for the source; the target only needs a visible window.
                svc.ResolveWindow = delegate(RobloxInstance i) { return hwnd; };

                _real = ProbeInput();
                Console.WriteLine("INPUT MODE: " + (_real ? "REAL SendInput/SetCursorPos on the fake window" : "SIMULATED (PostMessage mouse events to the fake window). Real SendInput is blocked in this session (SetCursorPos fails, e.g. locked/blank screen or input desktop not accessible), so the cursor/foreground/covered-window checks are SKIPPED."));
                if (!_real) svc.ClickHook = PostClick;

                // --- preconditions
                Check("refuse: source == target", svc.CanTeleport(a, a) != null, svc.CanTeleport(a, a));
                RobloxInstance stopped = Inst(3, "C", "Stopped", "carol");
                Check("refuse: source not running", svc.CanTeleport(stopped, b) != null, svc.CanTeleport(stopped, b));
                Check("refuse: target not running", svc.CanTeleport(a, stopped) != null, svc.CanTeleport(a, stopped));
                RobloxInstance starting = Inst(4, "D", "Starting", "dave");
                Check("refuse: target still starting", svc.CanTeleport(a, starting) != null, svc.CanTeleport(a, starting));
                RobloxInstance nouser = Inst(5, "E", "Running", null);
                Check("refuse: target has no username", svc.CanTeleport(a, nouser) != null, svc.CanTeleport(a, nouser));
                Check("allow: two running instances with a window", svc.CanTeleport(a, b) == null, svc.CanTeleport(a, b));
                svc.ResolveWindow = delegate(RobloxInstance i) { return i == b ? IntPtr.Zero : hwnd; };
                Check("refuse: target has no window", svc.CanTeleport(a, b) != null, svc.CanTeleport(a, b));
                svc.ResolveWindow = delegate(RobloxInstance i) { return hwnd; };
                TeleportResult rr = svc.Teleport(a, a, CancellationToken.None);
                Check("Teleport(a, a) returns Failed with a message and clicks nothing", rr.Status == TeleportStatus.Failed && fake.Snapshot().Length == 0, rr.Message);

                // --- scenario helper
                Point cur0; GetCursorPos(out cur0);

                // S1 success
                RunScenario(svc, fake, a, b, "kugixoxo", FakeMode.SuccessScene, TeleportStatus.Success, new string[] { "FRIENDS", "TELEPORT:1:kugixoxo" }, "closed|scene2", log);
                // S2 panel closes, scene identical
                RunScenario(svc, fake, a, Inst(6, "F", "Running", "cutiewutie87x"), "cutiewutie87x", FakeMode.PanelCloseOnly, TeleportStatus.Likely, new string[] { "FRIENDS", "TELEPORT:3:cutiewutie87x" }, "closed", log);
                // S3 click ignored: Failed, panel closed again by the service, one retry
                svc.ObserveMs = 2500;
                RunScenario(svc, fake, a, Inst(7, "G", "Running", "naittxoxo"), "naittxoxo", FakeMode.NoEffect, TeleportStatus.Failed, new string[] { "FRIENDS", "TELEPORT:2:naittxoxo", "CLOSE" }, "closed", log);
                int tele = Count(fake.Snapshot(), "TELEPORT:");
                Check("S3: retry happened once (2 Teleport clicks) and panel was closed", tele == 2 && Count(fake.Snapshot(), "CLOSE") >= 1, "teleport clicks=" + tele);
                {
                    string[] ne = EvDirs("teleport-noeffect");
                    Check("S3 evidence: teleport-noeffect folder with report, capture.png and annotated failure.png (rows + OCR + click point)", ne.Length == 1 && File.Exists(Path.Combine(ne[0], "failure.png")) && File.Exists(Path.Combine(ne[0], "capture.png")) && ReadReport(ne[0]).Contains("Names read:") && ReadReport(ne[0]).Contains("Click point (client px):") && _lastResult.Message.Contains("Evidence saved"), ne.Length == 0 ? "no folder" : ne[0]);
                    if (ne.Length == 1) CheckAnnotated(ne[0]);
                }
                // S3b: retry disabled
                svc.RetryOnTransientFailure = false;
                RunScenario(svc, fake, a, Inst(7, "G", "Running", "naittxoxo"), "naittxoxo", FakeMode.NoEffect, TeleportStatus.Failed, new string[] { "FRIENDS", "TELEPORT:2:naittxoxo", "CLOSE" }, "closed", log);
                Check("S3b: retry disabled -> exactly 1 Teleport click", Count(fake.Snapshot(), "TELEPORT:") == 1, "clicks=" + Count(fake.Snapshot(), "TELEPORT:"));
                svc.RetryOnTransientFailure = true;
                // S4 offline target
                RunScenario(svc, fake, a, Inst(8, "H", "Running", "Kugixo"), "Kugixo", FakeMode.SuccessScene, TeleportStatus.Failed, new string[] { "FRIENDS", "CLOSE" }, "closed", log);
                Check("S4: Offline row was never clicked and no Teleport click happened", Count(fake.Snapshot(), "OFFLINE_CLICK") == 0 && Count(fake.Snapshot(), "TELEPORT:") == 0, "");
                {
                    string[] nb = EvDirs("teleport-nobutton");
                    Check("S4 evidence: teleport-nobutton folder; message says the Teleport button was not found (grey Offline)", nb.Length == 1 && ReadReport(nb[0]).Contains("Teleport button not found") && _lastResult.Message.Contains("Teleport button not found"), nb.Length == 0 ? "no folder" : _lastResult.Message);
                }
                // S5 unknown user
                RunScenario(svc, fake, a, Inst(9, "I", "Running", "nobodyhere99"), "nobodyhere99", FakeMode.SuccessScene, TeleportStatus.Failed, new string[] { "FRIENDS", "CLOSE" }, "closed", log);
                Check("S5: no Teleport click for an unknown user", Count(fake.Snapshot(), "TELEPORT:") == 0, "");
                {
                    Info("S5 message: " + _lastResult.Message);
                    string[] nn = EvDirs("teleport-name");
                    string rp = nn.Length == 1 ? ReadReport(nn[0]) : "";
                    Check("S5: message lists the names that WERE read and says the target is not among them", _lastResult.Message.StartsWith("Panel opened, read:") && _lastResult.Message.Contains("kugixoxo") && _lastResult.Message.Contains("RubyPlexo") && _lastResult.Message.Contains("nobodyhere99") && _lastResult.Message.Contains("not among them"), _lastResult.Message);
                    Check("S5 evidence: teleport-name folder, report lists target + names read + per-row OCR, annotated failure.png", nn.Length == 1 && rp.Contains("Target username: nobodyhere99") && rp.Contains("Names read: RubyPlexo") && rp.Contains("row 2:") && File.Exists(Path.Combine(nn[0], "failure.png")), nn.Length == 0 ? "no folder" : rp.Substring(0, Math.Min(300, rp.Length)));
                    if (nn.Length == 1) CheckAnnotated(nn[0]);
                }
                // S12 panel opens but no rows are detected
                RunScenario(svc, fake, a, b, "kugixoxo", FakeMode.EmptyPanel, TeleportStatus.Failed, new string[] { "FRIENDS", "CLOSE" }, "closed", log);
                {
                    string[] nr = EvDirs("teleport-norows");
                    Check("S12: panel opened but no rows -> evidence folder teleport-norows with a clear message, nothing clicked on Teleport", nr.Length == 1 && _lastResult.Message.Contains("no friend rows were detected") && Count(fake.Snapshot(), "TELEPORT:") == 0 && ReadReport(nr[0]).Contains("Rows detected: 0"), _lastResult.Message);
                    if (nr.Length == 1) CheckAnnotated(nr[0]);
                }
                // ---- Friends list LOADING scenarios
                svc.ListRecoverAfterMs = 100000;
                fake.LoadMs = 2200;
                lock (statusLog) { statusLog.Clear(); }
                RunScenario(svc, fake, a, b, "kugixoxo", FakeMode.LoadingThenRows, TeleportStatus.Success, new string[] { "FRIENDS", "TELEPORT:1:kugixoxo" }, "closed|scene2", log);
                {
                    string sl; lock (statusLog) { sl = string.Join("|", statusLog.ToArray()); }
                    string lj; lock (log) { lj = string.Join("|", log.GetRange(Math.Max(0, log.Count - 30), Math.Min(30, log.Count)).ToArray()); }
                    Check("L1 LOADING for 2.2 s then rows: the service waits (no no-rows failure), clicks Teleport once, Success", _lastResult.Status == TeleportStatus.Success && Count(fake.Snapshot(), "TELEPORT:") == 1 && Count(fake.Snapshot(), "NEARBY") == 0, _lastResult.ToString());
                    Check("L1 progress text 'Waiting for the friends list to load... N s' was reported and the load time is logged", sl.Contains("Waiting for the friends list to load...") && lj.Contains("list Loaded after"), sl);
                    Check("L1 no teleport-norows / loading evidence for a slow-but-successful load", EvDirs("loading").Length == 0, "");
                }
                // L2 never loads: recovery click (Nearby then Friends) is tried once, then close the panel and fail with the specific text + evidence kind loading
                svc.ListWaitMs = 3600; svc.ListRecoverAfterMs = 1200;
                RunScenario(svc, fake, a, b, "kugixoxo", FakeMode.LoadingForever, TeleportStatus.Failed, new string[] { "FRIENDS", "NEARBY", "FRIENDSTAB", "CLOSE" }, "closed", log);
                {
                    string[] ld = EvDirs("loading");
                    Check("L2 never loads: message 'Friends list never finished loading (Roblox is busy ...)', no Teleport click, panel closed", _lastResult.Message.StartsWith("Friends list never finished loading (Roblox is busy - try with fewer instances running or again in a moment)") && Count(fake.Snapshot(), "TELEPORT:") == 0 && fake.State == "closed", _lastResult.Message);
                    Check("L2 recovery clicked Nearby then Friends exactly once", Count(fake.Snapshot(), "NEARBY") == 1 && Count(fake.Snapshot(), "FRIENDSTAB") == 1, string.Join(",", fake.Snapshot()));
                    Check("L2 evidence: kind loading with report + last capture + annotated failure.png", ld.Length == 1 && File.Exists(Path.Combine(ld[0], "capture.png")) && File.Exists(Path.Combine(ld[0], "failure.png")) && ReadReport(ld[0]).Contains("never finished loading"), ld.Length == 0 ? "no folder" : ld[0]);
                }
                // L3 the list only loads after the Nearby -> Friends refresh
                svc.ListWaitMs = 8000; svc.ListRecoverAfterMs = 1200;
                RunScenario(svc, fake, a, b, "kugixoxo", FakeMode.LoadingNeedsRecovery, TeleportStatus.Success, new string[] { "FRIENDS", "NEARBY", "FRIENDSTAB", "TELEPORT:1:kugixoxo" }, "closed|scene2", log);
                // L4 only some rows are visible, target not among them: the list is scrolled with the wheel, then found
                if (!_real) svc.ScrollHook = delegate(IntPtr h, FriendsPanelInfo pi) { PostMessage(h, 0x20A, new IntPtr(unchecked((int)((uint)(-360 << 16)))), IntPtr.Zero); fake.BeginInvoke(new Action(delegate { fake.SimScroll(); })); };
                svc.ListWaitMs = 25000;
                RunScenario(svc, fake, a, Inst(10, "J", "Running", "naittxoxo"), "naittxoxo", FakeMode.ScrollNeeded, TeleportStatus.Success, new string[] { "FRIENDS", "TELEPORT:2:naittxoxo" }, "closed|scene2", log);
                Check("L4 scroll path: target not among the visible rows -> list scrolled, then Teleport clicked on the right row (not on the blank spot)", Count(fake.Snapshot(), "TELEPORT_ON_BLANK") == 0 && Count(fake.Snapshot(), "TELEPORT:2") == 1, string.Join(",", fake.Snapshot()));
                svc.ScrollHook = null;
                // L5 Cancel aborts the wait promptly, closes the panel and releases the ClickLock
                svc.ListWaitMs = 40000; svc.ListRecoverAfterMs = 100000;
                fake.Mode = FakeMode.LoadingForever; fake.State = "closed"; fake.Invalidate(); Thread.Sleep(250); fake.ResetEvents();
                {
                    TeleportJob jl = svc.Start(a, b, null);
                    int wl = 0; while (Count(fake.Snapshot(), "FRIENDS") == 0 && wl++ < 100) Thread.Sleep(100);
                    Thread.Sleep(1500);
                    Stopwatch cs = Stopwatch.StartNew();
                    jl.Cancel();
                    int wl2 = 0; while (!jl.IsDone && wl2++ < 100) Thread.Sleep(100);
                    Check("L5 Cancel during the list wait -> Cancelled within 2 s", jl.IsDone && jl.Result != null && jl.Result.Status == TeleportStatus.Cancelled && cs.ElapsedMilliseconds < 2500, jl.Result == null ? "no result" : jl.Result + " " + cs.ElapsedMilliseconds + " ms");
                    Thread.Sleep(400);
                    Check("L5 panel closed after cancel, service not busy", fake.State == "closed" && !svc.IsBusy, "state=" + fake.State);
                    bool free = false; Thread lt = new Thread(delegate() { free = Monitor.TryEnter(AntiIdleClicker.ClickLock, 500); if (free) Monitor.Exit(AntiIdleClicker.ClickLock); }); lt.Start(); lt.Join();
                    Check("L5 ClickLock released on the cancel path", free, "");
                }
                svc.ListWaitMs = 25000;

                // S13 click ladder: the first two clicks on the FRIENDS sticker are ignored by the fake game
                TeleportService.ResetMemory();
                fake.Mode = FakeMode.SuccessScene; fake.State = "closed"; fake.Invalidate(); Thread.Sleep(250); fake.ResetEvents();
                fake.IgnoreFriendsClicks = 2;
                int logMark = log.Count;
                TeleportResult r13 = svc.Teleport(a, b, CancellationToken.None);
                string[] ev13 = fake.Snapshot();
                List<Point> cl = new List<Point>();
                foreach (string e in ev13) if (e.StartsWith("click@") && e.EndsWith("state=closed")) { string[] xy = e.Substring(6, e.IndexOf(" ") - 6).Split(new char[] { (char)44 }); cl.Add(new Point(int.Parse(xy[0]), int.Parse(xy[1]))); }
                int ribbonTop = 504;   // truth: label top of the reference sticker
                bool distinct = cl.Count >= 3 && Dist(cl[0], cl[1]) >= 6 && Dist(cl[0], cl[2]) >= 6 && Dist(cl[1], cl[2]) >= 6;
                bool onIcon = cl.Count >= 3 && cl[0].Y < ribbonTop && cl[1].Y < ribbonTop && cl[2].Y < ribbonTop;
                string joined; lock (log) { joined = string.Join("|", log.GetRange(logMark, log.Count - logMark).ToArray()); }
                Check("S13 ladder: 2 ignored clicks -> 3rd point opened the panel, Success", r13.Status == TeleportStatus.Success && Count(ev13, "FRIENDS_IGNORED") == 2 && Count(ev13, "FRIENDS") == 3, r13 + " events=" + string.Join(",", ev13));
                Check("S13 ladder: the 3 clicks were at 3 distinct points, all on the icon (above the ribbon at y " + ribbonTop + ")", distinct && onIcon, string.Join(" ", cl.ConvertAll(delegate(Point q) { return q.ToString(); }).ToArray()));
                Check("S13 ladder: log says which point worked (3/3) and it is remembered", joined.Contains("opened with click point 3/3") && TeleportService.RememberedPoint == 2, "remembered=" + TeleportService.RememberedPoint);
                fake.State = "closed"; fake.Invalidate(); Thread.Sleep(300); fake.ResetEvents();
                TeleportResult r13b = svc.Teleport(a, b, CancellationToken.None);
                List<Point> cl2 = new List<Point>();
                foreach (string e in fake.Snapshot()) if (e.StartsWith("click@") && e.EndsWith("state=closed")) { string[] xy = e.Substring(6, e.IndexOf(" ") - 6).Split(new char[] { (char)44 }); cl2.Add(new Point(int.Parse(xy[0]), int.Parse(xy[1]))); }
                Check("S13b: next teleport starts with the remembered point and needs only one FRIENDS click", r13b.Status == TeleportStatus.Success && cl2.Count == 1 && cl.Count >= 3 && Dist(cl2[0], cl[2]) <= 2, r13b + " first=" + (cl2.Count > 0 ? cl2[0].ToString() : "none") + " remembered=" + (cl.Count >= 3 ? cl[2].ToString() : "?"));
                TeleportService.ResetMemory();
                fake.State = "closed"; fake.Invalidate(); Thread.Sleep(300); fake.ResetEvents();
                // S6 sidebar hidden
                RunScenario(svc, fake, a, b, "kugixoxo", FakeMode.NoSidebar, TeleportStatus.Failed, new string[0], "closed", log);
                Check("S6: no click at all when the FRIENDS button is not found", fake.Snapshot().Length == 0, string.Join(",", fake.Snapshot()));
                // S6b a panel left open beforehand is reused and closed
                fake.Mode = FakeMode.SuccessScene; fake.State = "open"; fake.Invalidate(); Thread.Sleep(200); fake.ResetEvents();
                TeleportResult r6b = svc.Teleport(a, b, CancellationToken.None);
                Info("S6b result " + r6b);
                Check("S6b: panel already open: no FRIENDS click, Teleport clicked, Success", r6b.Status == TeleportStatus.Success && Count(fake.Snapshot(), "FRIENDS") == 0 && Count(fake.Snapshot(), "TELEPORT:1") == 1, string.Join(",", fake.Snapshot()));

                // S7 cancel during observation
                fake.Mode = FakeMode.NoEffect; fake.State = "closed"; fake.Invalidate(); Thread.Sleep(200); fake.ResetEvents();
                svc.ObserveMs = 6000;
                TeleportJob job = svc.Start(a, b, null);
                int w = 0; while (Count(fake.Snapshot(), "TELEPORT:") == 0 && w++ < 100) Thread.Sleep(100);
                Thread.Sleep(600);
                job.Cancel();
                int w2 = 0; while (!job.IsDone && w2++ < 100) Thread.Sleep(100);
                Check("S7: Cancel during the success check -> Cancelled", job.IsDone && job.Result != null && job.Result.Status == TeleportStatus.Cancelled, job.Result == null ? "no result" : job.Result.ToString());
                Thread.Sleep(300);
                Check("S7: panel closed after cancel", fake.State == "closed", "state=" + fake.State);
                svc.ObserveMs = 3500;

                // S8 covered window (needs real input)
                if (_real) {
                fake.Mode = FakeMode.SuccessScene; fake.State = "closed"; fake.Invalidate(); Thread.Sleep(200); fake.ResetEvents();
                fake.KeepOnTop = false;
                Form cover = null;
                Thread ct = new Thread(delegate() { cover = new Form(); cover.Text = "cover"; cover.TopMost = true; cover.StartPosition = FormStartPosition.Manual; cover.Location = new Point(0, 0); cover.Size = new Size(Screen.PrimaryScreen.Bounds.Width, Screen.PrimaryScreen.Bounds.Height); cover.BackColor = Color.Black; Application.Run(cover); });
                ct.SetApartmentState(ApartmentState.STA); ct.IsBackground = true; ct.Start();
                Thread.Sleep(900);
                TeleportResult r8 = svc.Teleport(a, b, CancellationToken.None);
                Info("S8 result " + r8);
                // The new focus logic raises the game above a topmost cover (BringWindowToTop/ALT trick), so Success is fine; what must never happen is a click landing on the cover or a half-click.
                Check("S8: topmost cover: either the game was raised and clicked correctly, or nothing was clicked at all", (r8.Status == TeleportStatus.Success && Count(fake.Snapshot(), "FRIENDS") == 1) || (r8.Status == TeleportStatus.Failed && fake.Snapshot().Length == 0), r8.Message + " events=" + string.Join(",", fake.Snapshot()));
                try { cover.Invoke(new Action(delegate { cover.Close(); })); } catch (Exception) { }
                fake.KeepOnTop = true;
                } else Info("SKIPPED S8 (covered window) - needs real input");
                Thread.Sleep(500);

                // S9 busy (second call while one runs) + S10 click lock
                fake.Mode = FakeMode.NoEffect; fake.State = "closed"; fake.Invalidate(); Thread.Sleep(200); fake.ResetEvents();
                svc.ObserveMs = 3000;
                TeleportJob j1 = svc.Start(a, b, null);
                Thread.Sleep(500);
                Check("S9: IsBusy while running", svc.IsBusy, "");
                TeleportResult second = svc.Teleport(a, b, CancellationToken.None);
                Check("S9: second teleport refused while one runs", second.Status == TeleportStatus.Failed && second.Message.IndexOf("already", StringComparison.OrdinalIgnoreCase) >= 0, second.Message);
                j1.Cancel(); int w3 = 0; while (!j1.IsDone && w3++ < 100) Thread.Sleep(100);
                Thread.Sleep(300);
                Check("S9: not busy afterwards", !svc.IsBusy, "");

                fake.Mode = FakeMode.SuccessScene; fake.State = "closed"; fake.Invalidate(); Thread.Sleep(200); fake.ResetEvents();
                Stopwatch lockSw = Stopwatch.StartNew();
                ManualResetEvent held = new ManualResetEvent(false);
                Thread holder = new Thread(delegate() { lock (AntiIdleClicker.ClickLock) { held.Set(); Thread.Sleep(1500); } });
                holder.IsBackground = true; holder.Start(); held.WaitOne();
                TeleportResult r10 = svc.Teleport(a, b, CancellationToken.None);
                Check("S10: waits for the CASH OUT clicker's global ClickLock, then succeeds", lockSw.ElapsedMilliseconds >= 1400 && r10.Status == TeleportStatus.Success, lockSw.ElapsedMilliseconds + " ms " + r10.Status);

                // S11 Diagnose input against the fake game with the fake input backend (no real input)
                {
                    IInputBackend savedB = InputSim.Backend;
                    string savedRoot = InputDiagnostics.RootOverride;
                    string drRoot = Path.Combine(Path.GetTempPath(), "rbx_diag_run_" + Guid.NewGuid().ToString("N"));
                    InputDiagnostics.RootOverride = drRoot;
                    try
                    {
                        FakeInputBackend fb2 = InputTests.NewFake(hwnd);
                        InputSim.Backend = fb2;
                        InputSim.Method = ClickMethod.Auto;
                        fake.Mode = FakeMode.SuccessScene; fake.State = "closed"; fake.Invalidate(); Thread.Sleep(250); fake.ResetEvents();
                        List<string> prog = new List<string>();
                        DiagResult dr = InputDiagnostics.Run("#1 test", hwnd, delegate(string s) { prog.Add(s); });
                        string rpt = File.Exists(Path.Combine(dr.Folder, "report.txt")) ? File.ReadAllText(Path.Combine(dr.Folder, "report.txt")) : "";
                        Info("diag summary: " + dr.Summary.Replace("\r\n", " | "));
                        Check("S11 Diagnose input: report.txt + before/after PNGs per method + initial capture written", File.Exists(Path.Combine(dr.Folder, "01_initial.png")) && File.Exists(Path.Combine(dr.Folder, "02_SendInput_before.png")) && File.Exists(Path.Combine(dr.Folder, "02_SendInput_after_last.png")) && File.Exists(Path.Combine(dr.Folder, "03_mouse_event_before.png")) && File.Exists(Path.Combine(dr.Folder, "04_PostMessage_before.png")), dr.Folder);
                        Check("S11 Diagnose input: SendInput worked, report names click point, screen point, readbacks, WindowFromPoint", dr.ClickWorked && dr.WorkingMethod == "SendInput" && rpt.Contains("Click point: client (") && rpt.Contains("-> screen (") && rpt.Contains("cursor readback") && rpt.Contains("WindowFromPoint") && rpt.Contains("returned 1, GetLastError 0") && rpt.Contains("Window DPI"), "worked=" + dr.ClickWorked + " method=" + dr.WorkingMethod);
                        Check("S11 Diagnose input: all three methods tested in turn and the panel left closed", rpt.Contains("CLICK TEST: SendInput") && rpt.Contains("CLICK TEST: mouse_event") && rpt.Contains("CLICK TEST: PostMessage") && fake.State == "closed", "state " + fake.State);
                        Check("S11 Diagnose input: report has environment (elevation, foreground before/after, monitor)", rpt.Contains("Launcher: pid") && rpt.Contains("Foreground BEFORE") && rpt.Contains("Foreground AFTER") && rpt.Contains("Monitor:"), "");
                        Check("S11 Diagnose input: no window title in report", !rpt.Contains("Fake Roblox test window"), "");
                        Check("S11 Diagnose input: continues past the FRIENDS click: panel analysis lists rows, names and button states, annotated PNG saved, Teleport NOT clicked, closed via red X", rpt.Contains("PANEL ANALYSIS") && rpt.Contains("Rows detected: 5") && rpt.Contains("RubyPlexo") && rpt.Contains("green Teleport button") && rpt.Contains("grey Offline button") && rpt.Contains("Panel closed via its red X") && Directory.GetFiles(dr.Folder, "*_panel_analysis.png").Length == 1 && Count(fake.Snapshot(), "TELEPORT:") == 0 && dr.Summary.Contains("Panel analysis"), "files=" + string.Join(",", Directory.GetFiles(dr.Folder, "*.png")));
                        Check("S11 Diagnose input: report has per-phase TIMINGS and the friends-list load time", rpt.Contains("=== TIMINGS (per phase) ===") && rpt.Contains("focus:") && rpt.Contains("friends list wait:") && rpt.Contains("list loaded after"), "");
                        // D2: the list shows Loading... for 2.5 s: the diagnostics wait for the rows and report the load time
                        fake.Mode = FakeMode.LoadingThenRows; fake.LoadMs = 2500; fake.State = "closed"; fake.Invalidate(); Thread.Sleep(250); fake.ResetEvents();
                        fb2 = InputTests.NewFake(hwnd); InputSim.Backend = fb2;
                        DiagResult dr2 = InputDiagnostics.Run("#1 test", hwnd, delegate(string s) { });
                        string rpt2 = File.ReadAllText(Path.Combine(dr2.Folder, "report.txt"));
                        Check("D2 Diagnose with a slow list: waits, records 'list loaded after N s', then analyses 5 rows, never clicks Teleport, panel closed", rpt2.Contains("list loaded after") && !rpt2.Contains("list loaded after 0.0 s") && rpt2.Contains("Rows detected: 5") && Count(fake.Snapshot(), "TELEPORT:") == 0 && fake.State == "closed", rpt2.Contains("FRIENDS LIST LOADING") ? rpt2.Substring(rpt2.IndexOf("FRIENDS LIST LOADING"), 200) : "no section");
                        // D3: never loads: 'still loading after N s'
                        int savedWait = InputDiagnostics.ListWaitMs; InputDiagnostics.ListWaitMs = 2500;
                        fake.Mode = FakeMode.LoadingForever; fake.State = "closed"; fake.Invalidate(); Thread.Sleep(250); fake.ResetEvents();
                        fb2 = InputTests.NewFake(hwnd); InputSim.Backend = fb2;
                        DiagResult dr3 = InputDiagnostics.Run("#1 test", hwnd, delegate(string s) { });
                        string rpt3 = File.ReadAllText(Path.Combine(dr3.Folder, "report.txt"));
                        InputDiagnostics.ListWaitMs = savedWait;
                        Check("D3 Diagnose with a list that never loads: report says 'still loading after N s' and the panel is closed", rpt3.Contains("still loading after") && Count(fake.Snapshot(), "TELEPORT:") == 0 && fake.State == "closed", "");
                        fake.Mode = FakeMode.SuccessScene; fake.State = "closed"; fake.Invalidate(); Thread.Sleep(250);
                        string stub = InputDiagnostics.Begin("#9 test", null);
                        Check("Diagnose: Begin() creates the timestamp folder with report.txt immediately", stub != null && Directory.Exists(stub) && File.ReadAllText(Path.Combine(stub, "report.txt")).Contains("started"), stub);
                        InputDiagnostics.WriteStatus(stub, "#9 test", "NOT RUN: no running instance");
                        Check("Diagnose: a refused run is written to its folder report.txt (NOT RUN + reason)", File.ReadAllText(Path.Combine(stub, "report.txt")).Contains("NOT RUN: no running instance"), "");
                        string stub2 = InputDiagnostics.Begin("#9 test", null);
                        Check("Diagnose: two presses in the same second get two different folders", stub2 != null && stub2 != stub && Directory.Exists(stub2), stub2);
                    }
                    finally
                    {
                        InputSim.Backend = savedB;
                        InputDiagnostics.RootOverride = savedRoot;
                        try { Directory.Delete(drRoot, true); } catch (Exception) { }
                    }
                }

                // Cursor restored
                Point cur1; GetCursorPos(out cur1);
                if (_real) Check("cursor restored to where it was before the scenarios (within 3 px)", Dist(cur0, cur1) <= 3, cur0 + " -> " + cur1);
                else Info("SKIPPED: cursor restore checks (needs real input)");

                Info("---- service log of the last runs ----");
                lock (log) { int start = Math.Max(0, log.Count - 40); for (int i = start; i < log.Count; i++) Info(log[i]); }
                try { fake.Invoke(new Action(delegate { fake.Close(); })); } catch (Exception) { }
            }
        }

        private static void RunScenario(TeleportService svc, FakeGame fake, RobloxInstance src, RobloxInstance dst, string user, FakeMode mode,
            TeleportStatus expect, string[] mustContain, string finalStates, List<string> log)
        {
            fake.Mode = mode; fake.State = "closed"; fake.Invalidate(); Thread.Sleep(250); fake.ResetEvents();
            Point before; GetCursorPos(out before);
            IntPtr fgBefore = GetForegroundWindow();
            Stopwatch sw = Stopwatch.StartNew();
            TeleportResult r = svc.Teleport(src, dst, CancellationToken.None);
            _lastResult = r;
            long ms = sw.ElapsedMilliseconds;
            string[] ev = fake.Snapshot();
            Thread.Sleep(300);
            bool all = true;
            foreach (string m in mustContain)
            {
                bool hit = false; foreach (string e in ev) if (e.StartsWith(m, StringComparison.OrdinalIgnoreCase)) hit = true;
                if (!hit) all = false;
            }
            Point after; GetCursorPos(out after);
            Check("[" + mode + " / " + user + "] status " + expect + ", expected clicks seen, panel left closed",
                r.Status == expect && all && Array.IndexOf(finalStates.Split('|'), fake.State) >= 0,
                "got " + r.Status + " in " + ms + " ms: " + r.Message + " | events=" + string.Join(",", ev) + " | final state=" + fake.State);
            if (_real) Check("[" + mode + " / " + user + "] cursor restored", Dist(before, after) <= 3, before + " -> " + after);
        }
    }
}
