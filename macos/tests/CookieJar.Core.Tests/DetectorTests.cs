using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using CookieJar.Core.Imaging;
using CookieJar.Platform;
using CookieJar.Platform.Mac;

namespace CookieJar.Tests
{
    public static class DetectorTests
    {
        private sealed class Ocr : IOcrService
        {
            public string Text;
            public bool Throw;
            public int Calls;
            public IReadOnlyList<OcrLine> Recognise(RawImage image)
            {
                Calls++;
                if (Throw) throw new InvalidOperationException("OCR unavailable");
                return new[] { new OcrLine(Text, new ScreenRect(0, 0, image.Width, image.Height), 1) };
            }
        }

        private static RawImage Load(string name)
        {
            RawImage image;
            if (!PngDecoder.TryDecode(File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "Fixtures", name)), out image))
                throw new InvalidDataException("Could not decode fixture " + name);
            return image;
        }

        private static RawImage Padded(RawImage image)
        {
            int stride = image.Width * 4 + 28;
            byte[] bytes = Enumerable.Repeat((byte)237, stride * image.Height).ToArray();
            for (int y = 0; y < image.Height; y++)
                Buffer.BlockCopy(image.Pixels, y * image.Stride, bytes, y * stride, image.Width * 4);
            return new RawImage(image.Width, image.Height, stride, bytes);
        }

        private static RawImage Solid(int width, int height, byte r, byte g, byte b)
        {
            byte[] bytes = new byte[width * height * 4];
            for (int i = 0; i < bytes.Length; i += 4)
            { bytes[i] = b; bytes[i + 1] = g; bytes[i + 2] = r; bytes[i + 3] = 255; }
            return new RawImage(width, height, width * 4, bytes);
        }

        private static void At(string file, Func<RawImage, ImageRect?> find, double x, double y)
        {
            RawImage source = Load(file);
            foreach (double scale in new[] { 0.6, 0.8, 1.0, 1.25 })
            {
                RawImage image = scale == 1 ? Padded(source) : ImagePixels.Resize(source,
                    (int)Math.Round(source.Width * scale), (int)Math.Round(source.Height * scale));
                var found = find(image);
                T.Check(file + " x" + scale + ": detected near expected centre", found.HasValue
                    && Math.Abs((found.Value.X + found.Value.Width / 2.0) / image.Width - x) < 0.035
                    && Math.Abs((found.Value.Y + found.Value.Height / 2.0) / image.Height - y) < 0.035,
                    found?.ToString() ?? "none");
            }
        }

        public static void Run()
        {
            PixelOperations();
            CashAndInvites();
            Hospital();
            Teleport();
        }

        private static void PixelOperations()
        {
            var original = Solid(3, 2, 50, 100, 200);
            var padded = Padded(original);
            var before = (byte[])padded.Pixels.Clone();
            T.Check("pixel packing removes row padding", ImagePixels.Packed(padded).SequenceEqual(original.Pixels));
            var crop = ImagePixels.Crop(padded, new ImageRect(-1, 0, 3, 5));
            T.Check("crop clamps to image boundaries", crop.Width == 2 && crop.Height == 2 && crop.Stride == 8);
            var enlarged = ImagePixels.Resize(padded, 13, 7);
            T.Check("resize preserves BGRA channels and solid colour", enlarged.Pixels.Where((v, i) => i % 4 == 0).All(v => v == 200)
                && enlarged.Pixels.Where((v, i) => i % 4 == 1).All(v => v == 100)
                && enlarged.Pixels.Where((v, i) => i % 4 == 2).All(v => v == 50));
            foreach (int mode in new[] { 0, 1, 2 })
            {
                var prepared = ImagePixels.PrepareNameCrop(padded, 2, mode);
                T.Check("OCR mode " + mode + " adds white margin", prepared.Width == 30 && prepared.Height == 28
                    && prepared.Pixels[0] == 255 && prepared.Pixels[3] == 255);
            }
            T.Check("pixel operations leave source and padding unchanged", padded.Pixels.SequenceEqual(before));
            bool rejected = false;
            try { ImagePixels.Crop(padded, new ImageRect(30, 30, 1, 1)); } catch (ArgumentException) { rejected = true; }
            T.Check("crop outside the image is rejected", rejected);
            var rect = new ImageRect(1, 2, 3, 4);
            T.Check("image rectangles use exclusive right/bottom", rect.Contains(1, 2) && !rect.Contains(4, 2) && !rect.Contains(1, 6));
        }

        private static void CashAndInvites()
        {
            RawImage cash = Load("cashout_ref.png");
            var found = CashOutDetector.Find(cash);
            T.Check("cash screenshot: cash-out found", found.HasValue, found?.ToString() ?? "none");
            T.Check("cash detector supports padded capture rows", Nullable.Equals(found, CashOutDetector.Find(Padded(cash))));
            foreach (double scale in new[] { 0.4, 0.6, 1.25 })
                T.Check("cash screenshot x" + scale, CashOutDetector.Find(ImagePixels.Resize(cash,
                    (int)(cash.Width * scale), (int)(cash.Height * scale))).HasValue);
            T.Check("cash null and tiny captures are ignored", CashOutDetector.Find(null) == null && CashOutDetector.Find(Solid(10, 10, 0, 255, 0)) == null);
            T.Check("label measurement clips external rectangles", InviteDetector.LabelWidthFraction(cash, new ImageRect(-300, -300, 5, 5)) == 0);
            foreach (string name in new[] { "invite-window_blue.png", "invite-window_pink.png", "invite-whole-window_blue.png", "invite-whole-window_pink.png" })
            {
                var image = Load(name);
                foreach (double scale in new[] { 0.6, 1.0, 1.25 })
                {
                    var frame = scale == 1 ? Padded(image) : ImagePixels.Resize(image, (int)(image.Width * scale), (int)(image.Height * scale));
                    var red = InviteDetector.Find(frame);
                    T.Check(name + " x" + scale + ": dismiss found", red.HasValue);
                    T.Check(name + " x" + scale + ": accept never mistaken for cash-out", CashOutDetector.Find(frame) == null);
                    if (red.HasValue)
                    {
                        var point = InviteDetector.ClickPoint(red.Value);
                        T.Check(name + ": click stays inside red button", red.Value.Contains(point));
                        T.Check(name + ": label width is usable", InviteDetector.LabelWidthFraction(frame, red.Value) >= InviteDetector.MinLabelWidth);
                    }
                }
                var trade = new Ocr { Text = "would like to trade with you" };
                var invite = new Ocr { Text = "You are invited to a party" };
                T.Check(name + ": trade switch respected", InviteDetector.Find(image, true, false, trade).HasValue
                    && !InviteDetector.Find(image, false, true, trade).HasValue);
                T.Check(name + ": invite switch respected", InviteDetector.Find(image, false, true, invite).HasValue
                    && !InviteDetector.Find(image, true, false, invite).HasValue);
                T.Check(name + ": both switches off skips dialogs", !InviteDetector.Find(image, false, false).HasValue);
            }
            foreach (string name in new[] { "cashout_ref.png", "tp_full_panel.png", "tp_pink_open.png", "tp_sidebar.png", "tp_panel_crop.png", "invite-decline-button.png", "invite-close-button.png" })
                T.Check(name + ": no invite false positive", InviteDetector.Find(Load(name)) == null);
        }

        private static void Hospital()
        {
            At("third_person_view_gift.png", HospitalDetector.FindBackpackButton, 0.4995, 0.907);
            At("backpack_ui_window-whole_game_window.png", HospitalDetector.FindBackpackButton, 572.5 / 1145, 625.5 / 693);
            At("backpack_ui_window_gifts_highlighted.png", HospitalDetector.FindPlusTile, 0.371, 0.320);
            At("backpack_ui_window_gifts_highlighted.png", HospitalDetector.FindGiftsTabHighlighted, 0.2096, 0.7303);
            At("achieving_to_the_gifts_display_look_pos.png", HospitalDetector.FindHospitalMarker, 0.5445, 0.1092);
            var pane = Load("backpack_ui_window_gifts_highlighted.png");
            var selected = HospitalDetector.FindGiftsTabHighlighted(pane);
            T.Check("selected Gifts tile is not also unselected", HospitalDetector.FindGiftsTabUnselected(pane) == null);
            if (selected.HasValue)
            {
                var bytes = (byte[])pane.Pixels.Clone();
                var tile = selected.Value;
                for (int y = tile.Top; y < tile.Bottom; y++)
                    for (int x = tile.Left; x < tile.Right; x++)
                    {
                        int offset = pane.OffsetOf(x, y);
                        if (bytes[offset + 2] > bytes[offset + 1] + 30 && bytes[offset] < bytes[offset + 1])
                        { bytes[offset] = 69; bytes[offset + 1] = 194; bytes[offset + 2] = 80; }
                    }
                var unselected = new RawImage(pane.Width, pane.Height, pane.Stride, bytes);
                T.Check("unselected green Gifts tile keeps icon recognition", HospitalDetector.FindGiftsTabUnselected(unselected).HasValue);
            }
            foreach (string file in new[] { "teleport_to_the_gifts_display_request_whole_window.png", "teleport_to_the_gifts_display_request_window.png" })
            {
                var image = Padded(Load(file));
                var ocr = new Ocr { Text = "Teleport to The Gifts Display?" };
                var yes = HospitalDetector.FindTeleportYes(image, ocr);
                T.Check(file + ": confirmed Gifts Display returns Yes", yes.HasValue);
                T.Check(file + ": invite detector leaves Gifts Display alone", InviteDetector.Find(image, ocr) == null);
                T.Check(file + ": missing OCR refuses affirmative button", HospitalDetector.FindTeleportYes(image) == null);
                T.Check(file + ": unrelated OCR refuses affirmative button", HospitalDetector.FindTeleportYes(image, new Ocr { Text = "Buy this item?" }) == null);
                T.Check(file + ": failing OCR refuses affirmative button", HospitalDetector.FindTeleportYes(image, new Ocr { Throw = true }) == null);
                T.Check(file + ": prompt OCR diagnostic is retained", HospitalDetector.LastPromptText == "");
            }
            var empty = Solid(600, 400, 0, 0, 0);
            T.Check("blank capture has no hospital elements", HospitalDetector.FindBackpackButton(empty) == null
                && HospitalDetector.FindPlusTile(empty) == null && HospitalDetector.FindHospitalMarker(empty) == null);
        }

        private static void Teleport()
        {
            var full = Load("tp_full_panel.png");
            var panel = TeleportDetector.FindPanel(Padded(full));
            T.Check("friends panel: five rows, four online", panel != null && panel.Rows.Count == 5 && panel.Rows.Count(r => r.Online) == 4);
            T.Check("friends panel: close button found", panel != null && panel.HasCloseButton);
            string body;
            T.Check("panel with rows is Loaded without OCR", TeleportDetector.ClassifyPanel(full, panel, out body) == TeleportDetector.PanelState.Loaded);
            T.Check("missing panel is Closed", TeleportDetector.ClassifyPanel(full, null, out body) == TeleportDetector.PanelState.Closed);
            var emptyPanel = new FriendsPanelInfo { Bounds = new ImageRect(0, 0, 600, 400) };
            var blank = Solid(600, 400, 179, 239, 255);
            T.Check("blank panel body is Empty without OCR", TeleportDetector.ClassifyPanel(blank, emptyPanel, out body) == TeleportDetector.PanelState.Empty);
            T.Check("OCR Loading keeps waiting", TeleportDetector.ClassifyPanel(blank, emptyPanel, out body,
                new Ocr { Text = "Loading..." }) == TeleportDetector.PanelState.Loading);
            T.Check("OCR empty-list message is Empty", TeleportDetector.ClassifyPanel(blank, emptyPanel, out body,
                new Ocr { Text = "No friends nearby" }) == TeleportDetector.PanelState.Empty);
            T.Check("cropped friends panel: five rows", TeleportDetector.FindPanel(Load("tp_panel_crop.png"))?.Rows.Count == 5);
            foreach (string file in new[] { "cashout_ref.png", "tp_sidebar.png", "tp_friends_btn.png" })
                T.Check(file + ": friends button found", TeleportDetector.FindFriendsButton(Padded(Load(file))) != null);
            foreach (string file in new[] { "cashout_ref.png", "tp_sidebar.png", "tp_fail_tall_closed.png", "tp_fail_tall_closed2.png" })
                T.Check(file + ": closed panel not detected", TeleportDetector.FindPanel(Load(file)) == null);
            foreach (double scale in new[] { 0.6, 0.8, 1.25 })
                T.Check("friends panel x" + scale + ": rows preserved", TeleportDetector.FindPanel(ImagePixels.Resize(full,
                    (int)(full.Width * scale), (int)(full.Height * scale)))?.Rows.Count == 5);
            var names = new FriendsPanelInfo();
            names.Rows.Add(new PanelRow { NameRect = new ImageRect(0, 0, 100, 25), Online = true });
            var ocr = new Ocr { Text = "RubyPlexo" };
            TeleportDetector.ReadNames(full, names, "RubyPlexo", ocr);
            T.Check("name OCR uses injected service and keeps candidates", ocr.Calls > 0 && names.Rows[0].Name == "RubyPlexo"
                && names.Rows[0].Candidates.Contains("RubyPlexo"));
            T.Check("name matching is case insensitive", TeleportDetector.MatchTarget(names, "rubyplexo").Status == MatchStatus.Found);
            names.Rows.Add(new PanelRow { Name = "RubyPlexo", Online = true });
            T.Check("duplicate OCR names are ambiguous", TeleportDetector.MatchTarget(names, "RubyPlexo").Status == MatchStatus.Ambiguous);
            TeleportDetector.ReadNames(full, names);
            T.Check("missing OCR clears old names and candidates", names.Rows.All(r => r.Name == "" && r.RawName == "" && r.Candidates.Count == 0));
            TeleportDetector.ReadNames(full, names, new Ocr { Throw = true });
            T.Check("OCR failure leaves names unreadable", names.Rows.All(r => r.Name == ""));
            var unreadable = new FriendsPanelInfo();
            unreadable.Rows.Add(new PanelRow());
            T.Check("missing OCR is distinguished from no matching name", TeleportDetector.MatchTarget(unreadable, "RubyPlexo", false).Status == MatchStatus.NoOcr);
            var black = Solid(160, 90, 0, 0, 0);
            var white = Solid(160, 90, 255, 255, 255);
            T.Check("scene diff: identical padded pixels unchanged", TeleportDetector.SceneDiff(black, Padded(black), ImageRect.Empty, ImageRect.Empty) == 0);
            T.Check("scene diff: full change detected", TeleportDetector.SceneDiff(black, white, ImageRect.Empty, ImageRect.Empty) == 1);
            T.Check("scene diff: excluded region ignored", TeleportDetector.SceneDiff(black, white, new ImageRect(0, 0, 160, 90), ImageRect.Empty) == 0);
            var stripes = Solid(640, 360, 0, 0, 0);
            for (int y = 0; y < stripes.Height; y++)
                for (int x = 0; x < stripes.Width; x++)
                    if (x % 10 < 3)
                    {
                        int offset = stripes.OffsetOf(x, y);
                        stripes.Pixels[offset] = stripes.Pixels[offset + 1] = stripes.Pixels[offset + 2] = 255;
                    }
            T.Check("scene diff averages thin changes between grid centres", TeleportDetector.SceneDiff(Solid(640, 360, 0, 0, 0), stripes,
                ImageRect.Empty, ImageRect.Empty) == 1);
            var zero = new RawImage(0, 0, 0, new byte[0]);
            T.Check("scene diff accepts empty capture", TeleportDetector.SceneDiff(zero, black, ImageRect.Empty, ImageRect.Empty) == 0);
            using (var cancellation = new CancellationTokenSource())
            {
                cancellation.Cancel();
                int captures = 0;
                var result = TeleportDetector.WaitForList(() => { captures++; return full; }, 1000, 10, 500, 100,
                    null, null, cancellation.Token);
                T.Check("cancelled panel wait exits before capture", result.Cancelled && captures == 0);
            }
        }
    }
}
