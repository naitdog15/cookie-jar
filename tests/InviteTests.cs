// InviteTests.cs - InviteDetector against the reference screenshots (tests\invite-*.png) and negative images.
// The red dismiss button must be found in every invite frame (trade request and party invitation, blue and pink
// themes, several sizes) and must never be found in frames without an invite. CashOutDetector must not match
// the green Accept / View button. C# 5.
using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;

namespace RbxMulti
{
    public static class InviteTests
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

        // The click point must sit on the red button, not on the green one.
        private static bool IsRedPixel(Color c)
        {
            return c.R > 170 && c.G < 90 && c.B < 110;
        }

        private static bool IsGreenPixel(Color c)
        {
            return c.G > 150 && c.G > c.R + 40 && c.G > c.B + 40;
        }

        private static void CheckFrame(Action<string, bool, string> check, string name, Bitmap frame)
        {
            Rectangle? found = InviteDetector.Find(frame);
            check(name + ": red dismiss button found", found.HasValue, found.HasValue ? found.Value.ToString() : "none");
            if (!found.HasValue) return;

            Rectangle r = found.Value;
            Point cp = InviteDetector.ClickPoint(r);
            // The click point is the centre, where the white label may sit: most of the box must be red, the label excepted.
            int red = 0, total = 0;
            for (int y = r.Top; y < r.Bottom; y++)
            {
                for (int x = r.Left; x < r.Right; x++)
                {
                    total++;
                    if (IsRedPixel(frame.GetPixel(x, y))) red++;
                }
            }
            check(name + ": box is mostly the red button", red >= 0.45 * total, red + "/" + total + " red, click=" + cp);
            // "Decline" / "Close" are long words; the Gifts Display prompt's "No" is short (see MinLabelWidth).
            double label = InviteDetector.LabelWidthFraction(frame, r);
            check(name + ": label is a long word (Decline / Close)", label >= InviteDetector.MinLabelWidth, "width " + label.ToString("0.00"));

            // The green Accept / View button sits just right of the red one, at the same height.
            bool greenRight = false;
            for (int x = r.Right + 1; x < Math.Min(frame.Width, r.Right + r.Width / 2); x++)
            {
                if (IsGreenPixel(frame.GetPixel(x, cp.Y))) { greenRight = true; break; }
            }
            check(name + ": green Accept/View button is right of the red one", greenRight, "");

            // Accept / View must never be read as CASH OUT.
            Rectangle? cash = CashOutDetector.Find(frame);
            check(name + ": CashOutDetector does not match the green button", !cash.HasValue, cash.HasValue ? cash.Value.ToString() : "");
        }

        private static void CheckNone(Action<string, bool, string> check, string name, Bitmap frame)
        {
            Rectangle? found = InviteDetector.Find(frame);
            check("negative: " + name + " has no dismiss button", !found.HasValue, found.HasValue ? found.Value.ToString() : "");
        }

        private static void InfoLabel(Action<string> info, string name, Bitmap frame)
        {
            Rectangle? found = InviteDetector.Find(frame);
            if (found.HasValue) info("negative-candidate " + name + ": label width " + InviteDetector.LabelWidthFraction(frame, found.Value).ToString("0.00"));
        }

        public static void Run(string dir, Action<string, bool, string> check, Action<string> info)
        {
            Console.WriteLine();
            Console.WriteLine("=== Invite: red Decline / Close button detector ===");

            string[] frames = { "invite-window_blue.png", "invite-window_pink.png", "invite-whole-window_blue.png", "invite-whole-window_pink.png" };
            foreach (string f in frames)
            {
                using (Bitmap b = Load(dir, f))
                {
                    CheckFrame(check, f, b);
                }
            }

            // Sizes as the tiled windows appear (small, normal and enlarged), both themes.
            string[] whole = { "invite-whole-window_blue.png", "invite-whole-window_pink.png" };
            double[] factors = { 0.4, 0.6, 0.75, 1.25, 1.5 };
            foreach (string f in whole)
            {
                foreach (double k in factors)
                {
                    using (Bitmap src = Load(dir, f))
                    using (Bitmap scaled = Scale(src, k))
                    {
                        CheckFrame(check, f + " x" + k, scaled);
                    }
                }
            }

            // The Gifts Display prompt (red No, green Yes) at other window sizes: its red No is IGNORED (the user's rule),
            // so Find must return null at every size. The title is read with OCR to tell it apart from other "No" prompts.
            string[] prompts = { "teleport_to_the_gifts_display_request_whole_window.png", "teleport_to_the_gifts_display_request_window.png" };
            double[] promptSizes = { 0.4, 0.5, 0.6, 0.8, 1.0, 1.25, 1.5 };
            foreach (string f in prompts)
            {
                foreach (double k in promptSizes)
                {
                    using (Bitmap src = Load(dir, f))
                    using (Bitmap scaled = Scale(src, k))
                    {
                        Rectangle? found = InviteDetector.Find(scaled);
                        check("Gifts Display prompt x" + k + " (" + f + "): red No ignored", !found.HasValue, found.HasValue ? found.Value.ToString() : "");
                    }
                }
            }

            // Negatives: a lone red button (no green sibling), and Adopt Me frames with no invite on screen.
            using (Bitmap b = Load(dir, "invite-decline-button.png")) CheckNone(check, "lone Decline button crop", b);
            using (Bitmap b = Load(dir, "invite-close-button.png")) CheckNone(check, "lone Close button crop", b);
            // Teleport frames (cashout_ref and tp_*) have no red-left / green-right dismiss pair: must return null.
            // (The Gifts Display prompt is handled above: its red No is ignored.)
            string[] noInvite = { "cashout_ref.png", "tp_full_panel.png", "tp_pink_open.png", "tp_sidebar.png", "tp_panel_crop.png" };
            foreach (string f in noInvite)
            {
                using (Bitmap b = Load(dir, f))
                {
                    CheckNone(check, f, b);
                    InfoLabel(info, f, b);
                }
            }
            info("frames tested: " + (frames.Length + whole.Length * factors.Length));
        }
    }
}
