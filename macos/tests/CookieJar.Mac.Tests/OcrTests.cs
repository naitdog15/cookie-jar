using System;
using System.Collections.Generic;
using System.Globalization;
using CookieJar.Platform;
using CookieJar.Platform.Mac;

namespace CookieJar.Tests
{
    public static class OcrTests
    {
        private const string Helper = "/opt/cookiejar/cookiejar-ocr";

        public static void Run()
        {
            AbsentHelperCases();
            ParseCases();
            HelperRunCases();
            EncodingRoundTrip();
        }

        private static void AbsentHelperCases()
        {
            var temp = new CaptureTests.FakeTemp();
            var runner = new CaptureTests.FakeRunner();
            var ocr = new MacOcrService(runner, Helper, () => false, temp);

            T.Check("absent helper: IsAvailable is false", !ocr.IsAvailable);
            IReadOnlyList<OcrLine> lines = ocr.Recognise(new RawImage(1, 1, 4, new byte[4]));
            T.Check("absent helper: Recognise returns an empty list", lines != null && lines.Count == 0);
            T.Check("absent helper: helper is never run and no temp file written",
                runner.Calls == 0 && temp.Written.Count == 0, "calls=" + runner.Calls);

            PlatformCapabilities all = PlatformCapabilities.ScreenCapture | PlatformCapabilities.Ocr;
            T.Check("absent helper: Ocr flag is cleared, other flags kept",
                MacOcrService.WithOcrFlag(all, ocr) == PlatformCapabilities.ScreenCapture);

            var present = new MacOcrService(runner, Helper, () => true, temp);
            T.Check("present helper: IsAvailable is true", present.IsAvailable);
            T.Check("present helper: Ocr flag is set, other flags kept",
                MacOcrService.WithOcrFlag(PlatformCapabilities.ScreenCapture, present) ==
                (PlatformCapabilities.ScreenCapture | PlatformCapabilities.Ocr));
            T.Check("null service: Ocr flag is cleared",
                MacOcrService.WithOcrFlag(PlatformCapabilities.Ocr, null) == PlatformCapabilities.None);
        }

        private const string Sample =
            "10\t20\t100\t30\t0.875\tFRIENDS\n" +
            "5\t6\t7\t8\t1.5\tclamped-high\n" +
            "9\t9\t9\t9\t-1\tclamped-low\n" +
            "bad line without tabs\n" +
            "1\t2\t0\t4\t0.5\tzero-width\n" +
            "1\t2\t3\tX\t0.5\tbad-height\n" +
            "1\t2\t3\t4\tnope\tbad-confidence\n" +
            "1\t2\t3\t4\t0.5\t\n" +
            "11\t12\t13\t14\t0.25\tTeleport\tOnline\n" +
            "15\t16\t17\t18\t0.5\tcrlf\r\n";

        private static void ParseCases()
        {
            IReadOnlyList<OcrLine> lines = MacOcrService.ParseLines(Sample);
            T.Check("parse keeps exactly the 5 well-formed lines", lines.Count == 5, "count=" + lines.Count);
            if (lines.Count == 5)
            {
                T.Check("parse line 1 exact values", Matches(lines[0], "FRIENDS", 10, 20, 100, 30, 0.875));
                T.Check("parse confidence above 1 clamps to 1", Matches(lines[1], "clamped-high", 5, 6, 7, 8, 1.0));
                T.Check("parse confidence below 0 clamps to 0", Matches(lines[2], "clamped-low", 9, 9, 9, 9, 0.0));
                T.Check("parse keeps tabs inside the text", Matches(lines[3], "Teleport\tOnline", 11, 12, 13, 14, 0.25));
                T.Check("parse strips CR at end of line", Matches(lines[4], "crlf", 15, 16, 17, 18, 0.5));
            }

            T.Check("parse empty text gives no lines", MacOcrService.ParseLines("").Count == 0 && MacOcrService.ParseLines(null).Count == 0);

            CultureInfo before = CultureInfo.CurrentCulture;
            List<OcrLine> german;
            try
            {
                CultureInfo.CurrentCulture = new CultureInfo("de-DE");
                german = MacOcrService.ParseLines("1\t2\t3\t4\t0.875\tculture");
            }
            finally
            {
                CultureInfo.CurrentCulture = before;
            }
            T.Check("parse uses invariant culture (0.875 under de-DE)", german.Count == 1 && Math.Abs(german[0].Confidence - 0.875) < 1e-9);
        }

        private static void HelperRunCases()
        {
            var temp = new CaptureTests.FakeTemp();
            var runner = new CaptureTests.FakeRunner { Stdout = Sample };
            var ocr = new MacOcrService(runner, Helper, () => true, temp);

            IReadOnlyList<OcrLine> lines = ocr.Recognise(new RawImage(1, 1, 4, new byte[4]));
            T.Check("recognise runs the helper with the PNG path as its only argument",
                runner.FileName == Helper && runner.Args != null && runner.Args.Count == 1 &&
                runner.Args[0] == temp.LastPath, "file=" + runner.FileName);
            T.Check("recognise returns the parsed lines", lines.Count == 5 && lines[0].Text == "FRIENDS");
            T.Check("recognise deletes the temp PNG", temp.LastPath != null && temp.Deleted.Contains(temp.LastPath));

            var failing = new CaptureTests.FakeRunner { Stdout = Sample, ExitCode = 2 };
            var failOcr = new MacOcrService(failing, Helper, () => true, new CaptureTests.FakeTemp());
            T.Check("helper exit 2 gives an empty list", failOcr.Recognise(new RawImage(1, 1, 4, new byte[4])).Count == 0);

            var none = new MacOcrService(new CaptureTests.FakeRunner(), Helper, () => true, new CaptureTests.FakeTemp());
            T.Check("null image gives an empty list", none.Recognise(null).Count == 0);
        }

        // The PNG written for the helper must decode back to the same pixels.
        private static void EncodingRoundTrip()
        {
            var pixels = new byte[3 * 2 * 4];
            for (int i = 0; i < pixels.Length; i++) pixels[i] = (byte)(i * 17 + 3);
            var image = new RawImage(3, 2, 12, pixels);

            var temp = new CaptureTests.FakeTemp();
            var ocr = new MacOcrService(new CaptureTests.FakeRunner(), Helper, () => true, temp);
            ocr.Recognise(image);

            RawImage decoded = null;
            bool ok = temp.Written.Count == 1 && PngDecoder.TryDecode(temp.Written[0], out decoded);
            string detail = "written=" + temp.Written.Count;
            if (ok)
            {
                detail += " size=" + decoded.Width + "x" + decoded.Height;
                byte[] got = new byte[pixels.Length];
                Array.Copy(decoded.Pixels, got, got.Length);
                ok = decoded.Width == 3 && decoded.Height == 2 && CaptureTests.SameBytes(got, pixels);
            }
            T.Check("written helper PNG decodes back to the same 3x2 pixels", ok, detail);
        }

        private static bool Matches(OcrLine line, string text, int x, int y, int w, int h, double confidence)
        {
            return line.Text == text &&
                   line.Bounds.X == x && line.Bounds.Y == y &&
                   line.Bounds.Width == w && line.Bounds.Height == h &&
                   Math.Abs(line.Confidence - confidence) < 1e-9;
        }
    }
}
