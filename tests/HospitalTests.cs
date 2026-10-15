// HospitalTests.cs - HospitalDetector against tonight's reference frames (tests\*.png from the Gifts Display route) and
// negatives from the other fixtures. Every element must be found at several window sizes, at the expected position
// (as a fraction of the frame), and must not be found on frames where it is not on screen. C# 5.
using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;
using System.Threading;

namespace RbxMulti
{
    public static class HospitalTests
    {
        private static Bitmap Load(string dir, string file)
        {
            return new Bitmap(Path.Combine(dir, file));
        }

        private static Bitmap Scale(Bitmap src, double factor)
        {
            int w = Math.Max(1, (int)Math.Round(src.Width * factor));
            int h = Math.Max(1, (int)Math.Round(src.Height * factor));
            Bitmap b = new Bitmap(w, h, PixelFormat.Format32bppArgb);
            using (Graphics g = Graphics.FromImage(b))
            {
                g.InterpolationMode = InterpolationMode.HighQualityBicubic;
                g.PixelOffsetMode = PixelOffsetMode.HighQuality;
                g.DrawImage(src, new Rectangle(0, 0, w, h), new Rectangle(0, 0, src.Width, src.Height), GraphicsUnit.Pixel);
            }
            return b;
        }

        // Expected centre as a fraction of the frame; tolerance 4% of the frame size.
        private static void CheckAt(Action<string, bool, string> check, string name, Rectangle? found, Bitmap frame, double fx, double fy)
        {
            check(name + ": found", found.HasValue, found.HasValue ? found.Value.ToString() : "none");
            if (!found.HasValue) return;
            double cx = (found.Value.X + found.Value.Width / 2.0) / frame.Width;
            double cy = (found.Value.Y + found.Value.Height / 2.0) / frame.Height;
            bool near = Math.Abs(cx - fx) <= 0.04 && Math.Abs(cy - fy) <= 0.04;
            check(name + ": at the expected place", near, "centre " + cx.ToString("0.000") + "," + cy.ToString("0.000") + " expected " + fx.ToString("0.000") + "," + fy.ToString("0.000"));
        }

        private static Bitmap Canvas(int w, int h)
        {
            Bitmap b = new Bitmap(w, h, PixelFormat.Format32bppArgb);
            using (Graphics g = Graphics.FromImage(b)) g.Clear(Color.FromArgb(40, 40, 48));
            return b;
        }

        private static void Paste(Bitmap canvas, Bitmap src, int x, int y)
        {
            using (Graphics g = Graphics.FromImage(canvas)) g.DrawImage(src, x, y, src.Width, src.Height);
        }

        // A 1000 x 700 canvas with one disc of the given colour centred at (cx, cy); optionally a white square glyph inside it
        // (about 0.20 of the disc area, like the backpack icon).
        private static Bitmap PaintDisc(Color disc, bool glyph, int cx, int cy, int d)
        {
            Bitmap b = Canvas(1000, 700);
            using (Graphics g = Graphics.FromImage(b))
            {
                using (Brush br = new SolidBrush(disc)) g.FillEllipse(br, cx - d / 2, cy - d / 2, d, d);
                if (glyph)
                {
                    using (Brush wb = new SolidBrush(Color.White)) g.FillRectangle(wb, cx - d / 5, cy - d / 5, 2 * d / 5, 2 * d / 5);
                }
            }
            return b;
        }

        // The backpack button of a reference frame: found, and centred at (cx, cy) in pixels of that frame.
        private static void DiscAt(Action<string, bool, string> check, string dir, string file, double cx, double cy)
        {
            using (Bitmap b = Load(dir, file))
            {
                CheckAt(check, "backpack button " + file, HospitalDetector.FindBackpackButton(b), b, cx / b.Width, cy / b.Height);
            }
        }

        private delegate Rectangle? Finder(Bitmap bmp);

        // A positive element at several sizes (the tiled windows are not all the same size).
        private static void Positive(Action<string, bool, string> check, string dir, string file, string what, Finder find, double fx, double fy, double[] factors)
        {
            foreach (double k in factors)
            {
                using (Bitmap src = Load(dir, file))
                using (Bitmap frame = k == 1.0 ? (Bitmap)src.Clone() : Scale(src, k))
                {
                    CheckAt(check, what + " " + file + " x" + k, find(frame), frame, fx, fy);
                }
            }
        }

        private static void Negative(Action<string, bool, string> check, string dir, string file, string what, Finder find)
        {
            using (Bitmap b = Load(dir, file))
            {
                Rectangle? r = find(b);
                check("negative: " + what + " not on " + file, !r.HasValue, r.HasValue ? r.Value.ToString() : "");
            }
        }

        public static void Run(string dir, Action<string, bool, string> check, Action<string> info)
        {
            Console.WriteLine();
            Console.WriteLine("=== Hospital route: aim controller on a simulated camera ===");
            // The marker's error changes by k screen px per mouse px of drag. The real gain is unknown, so try both signs
            // and several sizes. The controller must bring the marker inside the tolerance within 12 turns.
            double[] gains = { -6.0, -2.5, 3.0, 9.0 };
            double[] starts = { 400.0, -250.0, 90.0 };
            foreach (double k in gains)
            {
                foreach (double start in starts)
                {
                    double err = start;
                    double tol = HospitalAim.Tolerance(1145);
                    double slope = HospitalAim.InitialSlope;
                    double? prev = null;
                    int prevDrag = 0;
                    bool ok = false;
                    for (int i = 0; i < 12 && !ok; i++)
                    {
                        if (Math.Abs(err) <= tol) { ok = true; break; }
                        if (prev.HasValue) slope = HospitalAim.UpdateSlope(slope, prev.Value, err, prevDrag);
                        int drag = HospitalAim.DragFor(err, slope);
                        prev = err;
                        prevDrag = drag;
                        err += k * drag;
                    }
                    if (!ok && Math.Abs(err) <= tol) ok = true;
                    check("aim converges: gain " + k + ", start " + start, ok, "final error " + err.ToString("0.0") + " px");
                }
            }

            // Search: the marker is off screen at arrival. One-direction sweep, periodic camera, 70 degree visible window,
            // markers at every 5 degrees, sensitivities 0.1 to 0.6 degrees per mouse px. Every marker must be found within the budget.
            double[] sens = { 0.1, 0.2, 0.35, 0.6 };
            foreach (double s in sens)
            {
                int misses = 0;
                for (int theta = 0; theta < 360; theta += 5)
                {
                    double phi = 0;
                    bool found = false;
                    for (int sweep = 0; sweep <= HospitalAim.MaxSweeps; sweep++)
                    {
                        double d = ((theta - phi) % 360 + 540) % 360 - 180;
                        if (Math.Abs(d) <= 35) { found = true; break; }
                        phi += s * HospitalAim.SweepPx;
                    }
                    if (!found) misses++;
                }
                check("search finds the marker at every heading: " + s + " deg/px", misses == 0, misses + " headings missed");
            }

            // The rectangles that exclude the prompt and the pane from the arrival check cover them (reference: whole window).
            using (Bitmap whole = Load(dir, "teleport_to_the_gifts_display_request_whole_window.png"))
            {
                Rectangle? yes = HospitalDetector.FindTeleportYes(whole);
                if (yes.HasValue)
                {
                    Rectangle dlg = HospitalDetector.DialogRect(yes.Value);
                    bool covers = dlg.Contains(340, 290) && dlg.Contains(810, 530);
                    check("dialog exclusion covers the prompt box", covers, dlg.ToString());
                }
            }
            using (Bitmap world = Load(dir, "third_person_view_gift.png"))
            using (Bitmap pane = Load(dir, "backpack_ui_window_gifts_highlighted.png"))
            {
                // Paste at (120,110): the "+" is at (120+147, 110+76) in the frame, so the pane's top-left is (120,110).
                Rectangle plus = new Rectangle(120 + 147, 110 + 76, 78, 77);
                Rectangle pr = HospitalDetector.PaneRect(plus);
                bool covers = pr.Contains(120, 110) && pr.Contains(120 + pane.Width - 1, 110 + pane.Height - 1);
                check("pane exclusion covers the pasted pane", covers, pr.ToString());
            }

            Console.WriteLine();
            Console.WriteLine("=== Hospital route: element detectors (Gifts Display -> hospital) ===");
            double[] sizes = { 0.6, 0.8, 1.0, 1.25 };

            // Backpack button: bottom centre of the world view (third-person frame).
            Positive(check, dir, "third_person_view_gift.png", "backpack button", HospitalDetector.FindBackpackButton, 0.4995, 0.907, sizes);
            // Pink theme: the same button in pink (about 255,93,211), centre measured at (572.5, 625.5) of 1145 x 693.
            Positive(check, dir, "backpack_ui_window-whole_game_window.png", "backpack button (pink theme)", HospitalDetector.FindBackpackButton, 572.5 / 1145, 625.5 / 693, sizes);

            // The "+" tile and the highlighted Gifts tile: the backpack window with Gifts open.
            Positive(check, dir, "backpack_ui_window_gifts_highlighted.png", "backpack + tile", HospitalDetector.FindPlusTile, 0.371, 0.320, sizes);
            Positive(check, dir, "backpack_ui_window_gifts_highlighted.png", "Gifts tab (highlighted)", HospitalDetector.FindGiftsTabHighlighted, 0.2096, 0.7303, sizes);
            // Pink theme: the "+" tile of the pets pane (centre 190, 116.5 of 504 x 363) and the "+" of the whole window (508, 319.5 of 1145 x 693).
            Positive(check, dir, "backpack_ui_window_pets_opened.png", "backpack + tile (pink pets pane)", HospitalDetector.FindPlusTile, 190.0 / 504, 116.5 / 363, sizes);
            Positive(check, dir, "backpack_ui_window-whole_game_window.png", "backpack + tile (pink whole window)", HospitalDetector.FindPlusTile, 508.0 / 1145, 319.5 / 693, sizes);

            // Synthetic discs (no reference frame needed): the pink disc at the bottom centre is found, with or without the same
            // white glyph; a red disc (the map pins and invite buttons, hue about 352) is not, nor is a pink disc above the bottom.
            using (Bitmap pinkDisc = PaintDisc(Color.FromArgb(255, 93, 211), true, 500, 620, 80))
            {
                CheckAt(check, "backpack button: synthetic pink disc with white glyph", HospitalDetector.FindBackpackButton(pinkDisc), pinkDisc, 500.0 / 1000, 620.0 / 700);
            }
            using (Bitmap redDisc = PaintDisc(Color.FromArgb(216, 42, 63), true, 500, 620, 80))
            {
                Rectangle? r = HospitalDetector.FindBackpackButton(redDisc);
                check("negative: red disc with white glyph is not a backpack button", !r.HasValue, r.HasValue ? r.Value.ToString() : "");
            }
            using (Bitmap plain = PaintDisc(Color.FromArgb(255, 93, 211), false, 500, 620, 80))
            {
                Rectangle? r = HospitalDetector.FindBackpackButton(plain);
                check("negative: pink disc without the white glyph is not a backpack button", !r.HasValue, r.HasValue ? r.Value.ToString() : "");
            }
            using (Bitmap high = PaintDisc(Color.FromArgb(255, 93, 211), true, 500, 300, 80))
            {
                Rectangle? r = HospitalDetector.FindBackpackButton(high);
                check("negative: pink disc above the bottom of the window is not a backpack button", !r.HasValue, r.HasValue ? r.Value.ToString() : "");
            }
            // Whole windows where other coloured UI shares the frame (pink and blue invite dialogs, the red map pin, the pink
            // prompt, the shifted blue button of the friends panel). The disc must be the bottom-centre button, not a bigger blob.
            // Centres in pixels of each frame (measured): the button is 85 px across.
            DiscAt(check, dir, "invite-whole-window_pink.png", 572.5, 628.5);
            DiscAt(check, dir, "invite-whole-window_blue.png", 571.5, 628.5);
            DiscAt(check, dir, "achieving_to_the_gifts_display_look_pos.png", 573.5, 625.5);
            DiscAt(check, dir, "tp_pink_open.png", 571.5, 595.5);
            DiscAt(check, dir, "tp_full_panel.png", 514.5, 624.5);

            // Hospital marker: above the top bar once the character is in the Gifts Display area.
            Positive(check, dir, "achieving_to_the_gifts_display_look_pos.png", "hospital marker", HospitalDetector.FindHospitalMarker, 0.5445, 0.1092, sizes);

            // Yes on "Teleport to The Gifts Display?" (whole window and the window crop).
            using (Bitmap whole = Load(dir, "teleport_to_the_gifts_display_request_whole_window.png"))
            {
                Rectangle? yes = HospitalDetector.FindTeleportYes(whole);
                check("Gifts Display prompt: Yes found (whole window)", yes.HasValue, yes.HasValue ? yes.Value.ToString() : "none");
                if (yes.HasValue)
                {
                    check("Gifts Display prompt: Yes is a green button of invite size", Math.Abs(yes.Value.Width - 174) <= 20 && yes.Value.Y > 430 && yes.Value.Y < 470, yes.Value.ToString());
                }
            }
            using (Bitmap crop = Load(dir, "teleport_to_the_gifts_display_request_window.png"))
            {
                Rectangle? yes = HospitalDetector.FindTeleportYes(crop);
                check("Gifts Display prompt: Yes found (window crop)", yes.HasValue, yes.HasValue ? yes.Value.ToString() : "none");
            }

            // The backpack pane pasted 1:1 (and smaller / larger) into a world frame, as it appears on a real window.
            // The pane's own size is unknown relative to the window, so these are the realistic positives.
            using (Bitmap world = Load(dir, "third_person_view_gift.png"))
            using (Bitmap pane = Load(dir, "backpack_ui_window_gifts_highlighted.png"))
            {
                int px0 = 120, py0 = 110;
                foreach (double k in new double[] { 0.7, 1.0, 1.25 })
                {
                    using (Bitmap pk = Scale(pane, k))
                    using (Bitmap comp = (Bitmap)world.Clone())
                    {
                        using (Graphics g = Graphics.FromImage(comp)) g.DrawImage(pk, px0, py0, pk.Width, pk.Height);
                        string tag = "pane pasted x" + k;
                        Rectangle? plus = HospitalDetector.FindPlusTile(comp);
                        check(tag + ": + tile found", plus.HasValue, plus.HasValue ? plus.Value.ToString() : "none");
                        if (plus.HasValue)
                        {
                            Point c = HospitalDetector.Centre(plus.Value);
                            double ex = px0 + 185.5 * k, ey = py0 + 114 * k;
                            check(tag + ": + tile at the pasted place", Math.Abs(c.X - ex) <= 6 && Math.Abs(c.Y - ey) <= 6, c + " expected " + ex.ToString("0") + "," + ey.ToString("0"));
                        }
                        Rectangle? gifts = HospitalDetector.FindGiftsTabHighlighted(comp);
                        check(tag + ": highlighted Gifts tile found", gifts.HasValue, gifts.HasValue ? gifts.Value.ToString() : "none");
                    }
                }
                // The same world without the pane: nothing.
                Rectangle? none = HospitalDetector.FindPlusTile(world);
                check("negative: + tile not on the world frame", !none.HasValue, none.HasValue ? none.Value.ToString() : "");
            }

            // Prompt at smaller window sizes: the title is read after an upscale, so the Yes must still be found.
            foreach (double k in new double[] { 0.5, 0.6, 0.8 })
            {
                using (Bitmap src = Load(dir, "teleport_to_the_gifts_display_request_whole_window.png"))
                using (Bitmap small = Scale(src, k))
                {
                    Rectangle? yes = HospitalDetector.FindTeleportYes(small);
                    check("Gifts Display prompt: Yes found at x" + k, yes.HasValue, yes.HasValue ? yes.Value.ToString() : "none");
                }
            }

            // Gifts tab NOT selected: the same tile is green (like the others) with the same white gift icon. The reference has
            // only the orange (selected) tile, so the unselected case is made by recolouring that tile to the neighbouring green.
            using (Bitmap pane = Load(dir, "backpack_ui_window_gifts_highlighted.png"))
            {
                Color green = pane.GetPixel(30, 110);   // a plain green tile in the same pane (the star tile)
                Rectangle tile = new Rectangle(76, 238, 59, 42);
                using (Bitmap unselected = (Bitmap)pane.Clone())
                {
                    for (int y = tile.Top; y < tile.Bottom; y++)
                    {
                        for (int x = tile.Left; x < tile.Right; x++)
                        {
                            Color c = unselected.GetPixel(x, y);
                            if (c.R > c.G + 30 && c.B < c.G) unselected.SetPixel(x, y, green);
                        }
                    }
                    Rectangle? found = HospitalDetector.FindGiftsTabUnselected(unselected);
                    CheckAt(check, "Gifts tab unselected (green, same icon) in the pane", found, unselected, (tile.X + tile.Width / 2.0) / unselected.Width, (tile.Y + tile.Height / 2.0) / unselected.Height);

                    foreach (double k in new double[] { 0.6, 1.25 })
                    {
                        using (Bitmap sc = Scale(unselected, k))
                        {
                            Rectangle? f2 = HospitalDetector.FindGiftsTabUnselected(sc);
                            CheckAt(check, "Gifts tab unselected in the pane x" + k, f2, sc, (tile.X + tile.Width / 2.0) / unselected.Width, (tile.Y + tile.Height / 2.0) / unselected.Height);
                        }
                    }
                    using (Bitmap canvas = Canvas(1920, 1080))
                    {
                        Paste(canvas, unselected, 60, 120);
                        Rectangle? f3 = HospitalDetector.FindGiftsTabUnselected(canvas);
                        check("Gifts tab unselected on 1920x1080 canvas", f3.HasValue && Math.Abs(f3.Value.X - (60 + tile.X)) <= 3 && Math.Abs(f3.Value.Y - (120 + tile.Y)) <= 3, f3.HasValue ? f3.Value.ToString() : "none");
                    }
                }
                // Negative: the selected pane has no green Gifts tile; the other green tiles must not match the gift icon.
                Rectangle? other = HospitalDetector.FindGiftsTabUnselected(pane);
                check("negative: no unselected Gifts tile in the selected pane", !other.HasValue, other.HasValue ? other.Value.ToString() : "");
            }
            Negative(check, dir, "third_person_view_gift.png", "unselected Gifts tile", HospitalDetector.FindGiftsTabUnselected);
            Negative(check, dir, "achieving_to_the_gifts_display_look_pos.png", "unselected Gifts tile", HospitalDetector.FindGiftsTabUnselected);
            Negative(check, dir, "cashout_ref.png", "unselected Gifts tile", HospitalDetector.FindGiftsTabUnselected);
            Negative(check, dir, "teleport_to_the_gifts_display_request_whole_window.png", "unselected Gifts tile", HospitalDetector.FindGiftsTabUnselected);

            // Large windows: Roblox UI keeps its pixel size, so the same elements, pasted 1:1 on 1920x1080 and 2560x1440
            // canvases, must still be found (their share of the width is smaller there).
            int[][] canvasSizes = { new int[] { 1920, 1080 }, new int[] { 2560, 1440 } };
            foreach (int[] cs in canvasSizes)
            {
                string tag = cs[0] + "x" + cs[1];
                using (Bitmap world = Load(dir, "third_person_view_gift.png"))
                using (Bitmap canvas = Canvas(cs[0], cs[1]))
                {
                    int ox = cs[0] / 2 - world.Width / 2, oy = cs[1] - world.Height;
                    Paste(canvas, world, ox, oy);
                    Rectangle? disc = HospitalDetector.FindBackpackButton(canvas);
                    check("backpack button on " + tag + " canvas", disc.HasValue && Math.Abs(disc.Value.X + disc.Value.Width / 2 - (ox + 570)) <= 6, disc.HasValue ? disc.Value.ToString() : "none");
                }
                using (Bitmap pane = Load(dir, "backpack_ui_window_gifts_highlighted.png"))
                using (Bitmap canvas = Canvas(cs[0], cs[1]))
                {
                    Paste(canvas, pane, 60, 120);
                    Rectangle? plus = HospitalDetector.FindPlusTile(canvas);
                    check("+ tile on " + tag + " canvas", plus.HasValue, plus.HasValue ? plus.Value.ToString() : "none");
                    Rectangle? gifts = HospitalDetector.FindGiftsTabHighlighted(canvas);
                    check("Gifts tab on " + tag + " canvas", gifts.HasValue, gifts.HasValue ? gifts.Value.ToString() : "none");
                }
                using (Bitmap marker = Load(dir, "achieving_to_the_gifts_display_look_pos.png"))
                using (Bitmap canvas = Canvas(cs[0], cs[1]))
                {
                    int ox = cs[0] / 2 - marker.Width / 2;
                    Paste(canvas, marker, ox, 0);
                    Rectangle? m = HospitalDetector.FindHospitalMarker(canvas);
                    check("hospital marker on " + tag + " canvas", m.HasValue && Math.Abs(m.Value.X + m.Value.Width / 2 - (ox + 623)) <= 6, m.HasValue ? m.Value.ToString() : "none");
                }
                using (Bitmap empty = Canvas(cs[0], cs[1]))
                {
                    check("negative: empty " + tag + " canvas has no element", !HospitalDetector.FindBackpackButton(empty).HasValue && !HospitalDetector.FindPlusTile(empty).HasValue && !HospitalDetector.FindHospitalMarker(empty).HasValue, "");
                }
            }

            // Shared guard: a click the auto-clicker prepared before a manual / hospital test must not be sent after it.
            // Uses the guard only (no input is sent): a busy guard and a generation bump both give "superseded".
            long gen0 = TeleportService.ExclusiveGeneration;
            ClickOptions noOptions = new ClickOptions();
            ClickReport busy;
            bool took = TeleportService.TryAcquireExclusive();
            try
            {
                busy = AntiIdleClicker.GuardedClick(IntPtr.Zero, 0, 0, noOptions, TeleportService.ExclusiveGeneration);
            }
            finally
            {
                if (took) TeleportService.ReleaseExclusive();
            }
            check("guard: no click while another test holds the guard", busy.ErrorCode == "superseded", busy.ErrorCode ?? "no code");
            ClickReport stale = AntiIdleClicker.GuardedClick(IntPtr.Zero, 0, 0, noOptions, gen0 - 1);
            check("guard: no click when a test ran since the pass began", stale.ErrorCode == "superseded", stale.ErrorCode ?? "no code");

            // Negatives: each element is absent from the frames where it is not on screen.
            Negative(check, dir, "teleported_to_the_gifts_display_entry_look_pos.png", "hospital marker", HospitalDetector.FindHospitalMarker);
            Negative(check, dir, "third_person_view_gift.png", "hospital marker", HospitalDetector.FindHospitalMarker);
            Negative(check, dir, "backpack_ui_window_gifts_highlighted.png", "hospital marker", HospitalDetector.FindHospitalMarker);
            Negative(check, dir, "backpack_ui_window_gifts_highlighted.png", "backpack button", HospitalDetector.FindBackpackButton);
            // Pink invite and prompt crops (blue and pink theme): the pink dialog buttons and text are not a backpack disc.
            // (The whole-window invite frames do show the backpack button at the bottom centre, so they are not negatives.)
            Negative(check, dir, "invite-window_pink.png", "backpack button", HospitalDetector.FindBackpackButton);
            Negative(check, dir, "invite-window_blue.png", "backpack button", HospitalDetector.FindBackpackButton);
            Negative(check, dir, "teleport_to_the_gifts_display_request_window.png", "backpack button", HospitalDetector.FindBackpackButton);
            Negative(check, dir, "achieving_to_the_gifts_display_look_pos.png", "backpack + tile", HospitalDetector.FindPlusTile);
            Negative(check, dir, "third_person_view_gift.png", "backpack + tile", HospitalDetector.FindPlusTile);
            Negative(check, dir, "achieving_to_the_gifts_display_look_pos.png", "Gifts tab (highlighted)", HospitalDetector.FindGiftsTabHighlighted);
            Negative(check, dir, "teleport_to_the_gifts_display_request_whole_window.png", "Gifts tab (highlighted)", HospitalDetector.FindGiftsTabHighlighted);
            // The backpack button is on every Adopt Me frame at the bottom centre (the CASH OUT frame shows it too).
            using (Bitmap b = Load(dir, "cashout_ref.png"))
            {
                Rectangle? r = HospitalDetector.FindBackpackButton(b);
                check("backpack button also on cashout_ref.png (same Adopt Me frame)", r.HasValue, r.HasValue ? r.Value.ToString() : "none");
            }
            Negative(check, dir, "cashout_ref.png", "hospital marker", HospitalDetector.FindHospitalMarker);
            Negative(check, dir, "tp_full_panel.png", "hospital marker", HospitalDetector.FindHospitalMarker);
            Negative(check, dir, "invite-whole-window_blue.png", "hospital marker", HospitalDetector.FindHospitalMarker);
            Negative(check, dir, "invite-whole-window_pink.png", "hospital marker", HospitalDetector.FindHospitalMarker);

            // Yes must never be confirmed without the Gifts Display title: trade and party invites have green buttons too.
            Negative(check, dir, "invite-whole-window_blue.png", "Gifts Display Yes", HospitalDetector.FindTeleportYes);
            Negative(check, dir, "invite-whole-window_pink.png", "Gifts Display Yes", HospitalDetector.FindTeleportYes);
            Negative(check, dir, "invite-window_blue.png", "Gifts Display Yes", HospitalDetector.FindTeleportYes);
            Negative(check, dir, "cashout_ref.png", "Gifts Display Yes", HospitalDetector.FindTeleportYes);
            Negative(check, dir, "achieving_to_the_gifts_display_look_pos.png", "Gifts Display Yes", HospitalDetector.FindTeleportYes);

            RouteTests(check);
        }

        // ----------------------------------------------------------------
        // Walk decisions, cursor drift and input balance. Fake backend, virtual time, no real input.
        // ----------------------------------------------------------------

        // Drives HospitalWalk with a stall script, one poll per 0.5 s, until a release (or 200 polls).
        private static WalkStep DriveWalk(HospitalWalk w, bool[] stalls)
        {
            double t = 0;
            WalkStep s = WalkStep.Hold;
            for (int i = 0; i < 200; i++)
            {
                bool st = i < stalls.Length && stalls[i];
                t += 0.5;
                s = w.Next(st, true, t, false);
                if (s != WalkStep.Hold && s != WalkStep.Jump) break;
            }
            return s;
        }

        private static Bitmap Solid(Color c)
        {
            Bitmap b = new Bitmap(32, 32, PixelFormat.Format32bppArgb);
            using (Graphics g = Graphics.FromImage(b)) g.Clear(c);
            return b;
        }

        private static WalkEnv MakeWalkEnv(FakeInputBackend fb, IntPtr hwnd, Point anchor, CancellationToken ct, Func<Bitmap> frame)
        {
            fb.CurX = anchor.X;
            fb.CurY = anchor.Y;
            WalkEnv e = new WalkEnv();
            e.Input = fb;
            e.Hwnd = hwnd;
            e.Anchor = anchor;
            e.Ct = ct;
            e.Frame = frame;
            e.Seconds = delegate { return fb.Now / 1000.0; };
            e.Say = null;
            return e;
        }

        private static void RouteTests(Action<string, bool, string> check)
        {
            Console.WriteLine();
            Console.WriteLine("=== Hospital route: walk decisions, cursor drift, input balance (fake backend) ===");

            // --- pure walk decisions (scripted sequences)
            HospitalWalk w1 = new HospitalWalk();
            WalkStep s1 = DriveWalk(w1, new bool[0]);
            check("walk 1: no stalls reaches the time cap -> ReleaseDone, no jump", s1 == WalkStep.ReleaseDone && w1.Jumps == 0, s1 + " jumps " + w1.Jumps);

            HospitalWalk w2 = new HospitalWalk();
            WalkStep s2 = DriveWalk(w2, new bool[] { true, false });
            check("walk 2: one stall then movement -> one Jump, then ReleaseDone", s2 == WalkStep.ReleaseDone && w2.Jumps == 1, s2 + " jumps " + w2.Jumps);

            HospitalWalk w3 = new HospitalWalk();
            WalkStep s3 = DriveWalk(w3, new bool[] { true, true });
            check("walk 3: stall, jump, stall again -> ReleaseBlocked", s3 == WalkStep.ReleaseBlocked && w3.Jumps == 1, s3 + " jumps " + w3.Jumps);

            bool[] alt = new bool[40];
            for (int i = 0; i < alt.Length; i++) alt[i] = i % 2 == 0;   // stall, move, stall, move, ...
            HospitalWalk w4 = new HospitalWalk();
            WalkStep s4 = DriveWalk(w4, alt);
            check("walk 4: one Space per stall (MaxJumps 1), then blocked, never a second", s4 == WalkStep.ReleaseBlocked && w4.Jumps == HospitalWalk.MaxJumps && HospitalWalk.MaxJumps == 1, s4 + " jumps " + w4.Jumps);

            HospitalWalk w5 = new HospitalWalk();
            WalkStep s5 = w5.Next(false, false, 1.0, false);
            check("walk 5: focus lost on the first poll -> ReleaseFocus", s5 == WalkStep.ReleaseFocus, s5.ToString());

            HospitalWalk w6 = new HospitalWalk();
            WalkStep s6 = w6.Next(false, true, 1.0, true);
            check("walk 6: cursor drifted -> ReleaseDrift", s6 == WalkStep.ReleaseDrift, s6.ToString());

            // --- cursor drift (pure)
            Point anchor = new Point(500, 300);
            check("drift: 5 px is within 6 px", !HospitalRoute.CursorDrifted(anchor, new Point(504, 303), 6), "");
            check("drift: exactly 6 px is within 6 px", !HospitalRoute.CursorDrifted(anchor, new Point(506, 300), 6), "");
            check("drift: 7 px is beyond 6 px", HospitalRoute.CursorDrifted(anchor, new Point(500, 293), 6), "");
            check("drift: the default tolerance is DriftTolerancePx", HospitalRoute.DriftTolerancePx == 6, "");

            IntPtr hwnd = new IntPtr(0x5000);
            IInputBackend savedB = InputSim.Backend;
            try
            {
                // --- walk on a moving scene, time cap reached: W balanced
                FakeInputBackend fb = InputTests.NewFake(hwnd);
                InputSim.Backend = fb;
                int calls = 0;
                Func<Bitmap> moving = delegate { calls++; return Solid(calls % 2 == 0 ? Color.Red : Color.Blue); };
                HospitalResult rw = HospitalRoute.WalkForward(MakeWalkEnv(fb, hwnd, anchor, CancellationToken.None, moving));
                check("walk: moving scene reaches the time cap -> success", rw.Ok, rw.Message);
                check("walk: W key-down == key-up == 1 (moving)", fb.Count("scan 0x11 down") == 1 && fb.Count("scan 0x11 up") == 1, fb.Count("scan 0x11 down") + "/" + fb.Count("scan 0x11 up"));
                check("walk: no jump on a moving scene", fb.Count("scan 0x39 down") == 0, "");
                check("walk: cursor is on the anchor at the end", fb.CurX == anchor.X && fb.CurY == anchor.Y, fb.CurX + "," + fb.CurY);

                // --- a null frame (capture failed) is never a stall
                fb = InputTests.NewFake(hwnd); InputSim.Backend = fb;
                Func<Bitmap> nulls = delegate { return null; };
                rw = HospitalRoute.WalkForward(MakeWalkEnv(fb, hwnd, anchor, CancellationToken.None, nulls));
                check("walk: failed captures do not count as a stall (no jump, success)", rw.Ok && fb.Count("scan 0x39 down") == 0, rw.Message);

                // --- stall, jump, then the scene moves again: one jump, then success
                fb = InputTests.NewFake(hwnd); InputSim.Backend = fb;
                calls = 0;
                Func<Bitmap> stallThenMove = delegate
                {
                    calls++;
                    if (calls <= 2) return Solid(Color.Gray);
                    return Solid(calls % 2 == 0 ? Color.Red : Color.Blue);
                };
                rw = HospitalRoute.WalkForward(MakeWalkEnv(fb, hwnd, anchor, CancellationToken.None, stallThenMove));
                check("walk: stall -> one jump -> moving again -> success", rw.Ok && fb.Count("scan 0x39 down") == 1 && fb.Count("scan 0x39 up") == 1, rw.Message + " space " + fb.Count("scan 0x39 down"));
                check("walk: W still balanced after a jump", fb.Count("scan 0x11 down") == 1 && fb.Count("scan 0x11 up") == 1, "");

                // --- stall forever: one jump, then blocked; W and Space balanced
                fb = InputTests.NewFake(hwnd); InputSim.Backend = fb;
                Func<Bitmap> still = delegate { return Solid(Color.Gray); };
                rw = HospitalRoute.WalkForward(MakeWalkEnv(fb, hwnd, anchor, CancellationToken.None, still));
                check("walk: stalled twice after a jump -> blocked", !rw.Ok && rw.Message.StartsWith("The character is blocked"), rw.Message);
                check("walk: blocked walk releases W and Space (balanced)", fb.Count("scan 0x11 down") == 1 && fb.Count("scan 0x11 up") == 1
                    && fb.Count("scan 0x39 down") == 1 && fb.Count("scan 0x39 up") == 1, "");

                // --- balance 1: cancelled mid-walk
                fb = InputTests.NewFake(hwnd); InputSim.Backend = fb;
                CancellationTokenSource cts = new CancellationTokenSource();
                int frames = 0;
                Func<Bitmap> cancelAt = delegate
                {
                    frames++;
                    if (frames == 4) cts.Cancel();
                    return Solid(frames % 2 == 0 ? Color.Red : Color.Blue);
                };
                bool threw = false;
                try { HospitalRoute.WalkForward(MakeWalkEnv(fb, hwnd, anchor, cts.Token, cancelAt)); }
                catch (OperationCanceledException) { threw = true; }
                check("balance: cancelled mid-walk stops with OperationCanceledException", threw, "");
                check("balance: cancelled mid-walk -> W key-down == key-up == 1", fb.Count("scan 0x11 down") == 1 && fb.Count("scan 0x11 up") == 1, fb.Count("scan 0x11 down") + "/" + fb.Count("scan 0x11 up"));

                // --- balance 2: focus lost during the walk
                fb = InputTests.NewFake(hwnd); InputSim.Backend = fb;
                frames = 0;
                Func<Bitmap> loseFocus = delegate
                {
                    frames++;
                    if (frames == 3) fb.Fg = new IntPtr(0x6666);   // another window takes the foreground
                    return Solid(frames % 2 == 0 ? Color.Red : Color.Blue);
                };
                rw = HospitalRoute.WalkForward(MakeWalkEnv(fb, hwnd, anchor, CancellationToken.None, loseFocus));
                check("balance: focus lost during the walk -> the focus message", !rw.Ok && rw.Message == "The game window lost focus; the walk was stopped.", rw.Message);
                check("balance: focus lost -> W key-down == key-up == 1", fb.Count("scan 0x11 down") == 1 && fb.Count("scan 0x11 up") == 1, "");

                // --- balance 3: cursor moved (user) during the walk
                fb = InputTests.NewFake(hwnd); InputSim.Backend = fb;
                frames = 0;
                Func<Bitmap> moveMouse = delegate
                {
                    frames++;
                    if (frames == 3) fb.CurX += 40;
                    return Solid(frames % 2 == 0 ? Color.Red : Color.Blue);
                };
                rw = HospitalRoute.WalkForward(MakeWalkEnv(fb, hwnd, anchor, CancellationToken.None, moveMouse));
                check("balance: cursor moved during the walk -> stopped, W balanced", !rw.Ok && fb.Count("scan 0x11 down") == 1 && fb.Count("scan 0x11 up") == 1, rw.Message);

                // --- balance 4: cancelled mid-turn (right button)
                fb = InputTests.NewFake(hwnd); InputSim.Backend = fb;
                int steps = 0;
                bool turnThrew = false;
                try
                {
                    using (var dragCts = new CancellationTokenSource())
                    {
                        MouseDrag.Hold(hwnd, 120, anchor, dragCts.Token, delegate
                        {
                            steps++;
                            if (steps >= 3) throw new OperationCanceledException();
                        });
                    }
                }
                catch (OperationCanceledException) { turnThrew = true; }
                check("balance: cancelled mid-turn -> right-down == right-up == 1", fb.Count("right down") == 1 && fb.Count("right up") == 1, fb.Count("right down") + "/" + fb.Count("right up"));

                // --- balance 5: cursor drift during the drag
                fb = InputTests.NewFake(hwnd); InputSim.Backend = fb;
                steps = 0;
                turnThrew = false;
                try
                {
                    MouseDrag.Hold(hwnd, 120, anchor, CancellationToken.None, delegate
                    {
                        steps++;
                        if (steps == 2) fb.CurX += 40;   // the user moves the mouse
                        if (HospitalRoute.CursorDrifted(anchor, new Point(fb.CurX, fb.CurY), HospitalRoute.DriftTolerancePx)) throw new OperationCanceledException();
                    });
                }
                catch (OperationCanceledException) { turnThrew = true; }
                check("balance: drift during the drag stops the turn", turnThrew, "");
                check("balance: drift during the drag -> right-down == right-up == 1", fb.Count("right down") == 1 && fb.Count("right up") == 1, "");

                // --- a full turn: balanced, cursor back on the anchor
                fb = InputTests.NewFake(hwnd); InputSim.Backend = fb;
                bool okTurn = MouseDrag.Hold(hwnd, -90, anchor, CancellationToken.None, null);
                check("turn: completes, right button balanced, cursor on the anchor", okTurn && fb.Count("right down") == 1 && fb.Count("right up") == 1 && fb.CurX == anchor.X && fb.CurY == anchor.Y, "");
            }
            finally
            {
                InputSim.Backend = savedB;
            }
        }
    }
}
