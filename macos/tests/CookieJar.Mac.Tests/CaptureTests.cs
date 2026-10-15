using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using CookieJar.Platform;
using CookieJar.Platform.Mac;

namespace CookieJar.Tests
{
    public static class CaptureTests
    {
        public static void Run()
        {
            PngDecodeCases();
            CaptureCases();
        }

        // ---------------------------------------------------------------- PNG decoding

        private static void PngDecodeCases()
        {
            // 2x2 RGBA: row 0 filter None, row 1 filter Up. Filtered row 1 = raw - row 0 (mod 256).
            byte[] rgba = BuildPng(2, 2, 6, false, null, null, new byte[] {
                0, 255, 0, 0, 255, 0, 255, 0, 128,
                2, 1, 0, 255, 0, 10, 21, 30, 168 });
            CheckPixels("png RGBA 2x2 (None + Up) decodes to exact BGRA, alpha kept", rgba, 2, 2,
                new byte[] { 0, 0, 255, 255, 0, 255, 0, 128, 255, 0, 0, 255, 30, 20, 10, 40 });

            // 2x5 RGB, one row per filter (colour type 2, bpp 3). Expected values are hand-computed:
            //   raw row0 = 10,20,30,40,50,60            filter 0 None
            //   raw row1 = 11,22,33,44,55,66            filter 1 Sub   -> 11,22,33,33,33,33
            //   raw row2 = 12,24,36,48,60,72            filter 2 Up    -> 1,2,3,4,5,6 (against row1)
            //   raw row3 = 20,40,60,90,35,130           filter 3 Avg   -> 14,28,42,56,241,64 (against row2)
            //   raw row4 = 100,45,65,120,50,70          filter 4 Paeth -> 80,5,5,20,10,196 (against row3)
            //     Paeth branches used: b (bytes 0-2, 5), a (byte 3), c (byte 4)
            byte[] rgb = BuildPng(2, 5, 2, false, null, null, new byte[] {
                0, 10, 20, 30, 40, 50, 60,
                1, 11, 22, 33, 33, 33, 33,
                2, 1, 2, 3, 4, 5, 6,
                3, 14, 28, 42, 56, 241, 64,
                4, 80, 5, 5, 20, 10, 196 });
            CheckPixels("png RGB 2x5 decodes all five filters (None, Sub, Up, Average, Paeth)", rgb, 2, 5,
                new byte[] {
                    30, 20, 10, 255,  60, 50, 40, 255,
                    33, 22, 11, 255,  66, 55, 44, 255,
                    36, 24, 12, 255,  72, 60, 48, 255,
                    60, 40, 20, 255, 130, 35, 90, 255,
                    65, 45, 100, 255, 70, 50, 120, 255 });

            // Palette 2x1 with tRNS giving entry 0 alpha 0; entry 1 has no tRNS value so alpha is 255.
            byte[] pal = BuildPng(2, 1, 3, false, new byte[] { 255, 0, 0, 0, 0, 255 }, new byte[] { 0 }, new byte[] { 0, 1, 0 });
            CheckPixels("png palette 2x1 uses PLTE and tRNS", pal, 2, 1,
                new byte[] { 255, 0, 0, 255, 0, 0, 255, 0 });

            // Grey 2x1 (colour type 0).
            byte[] grey = BuildPng(2, 1, 0, false, null, null, new byte[] { 0, 200, 7 });
            CheckPixels("png grey 2x1 expands to BGRA with alpha 255", grey, 2, 1,
                new byte[] { 200, 200, 200, 255, 7, 7, 7, 255 });

            RawImage junk;
            T.Check("png garbage bytes give false", !PngDecoder.TryDecode(new byte[] { 1, 2, 3, 4, 5 }, out junk) && junk == null);
            T.Check("png null input gives false", !PngDecoder.TryDecode(null, out junk) && junk == null);
            byte[] text = Encoding.ASCII.GetBytes("this is not a png file, not even close at all");
            T.Check("png text input gives false", !PngDecoder.TryDecode(text, out junk) && junk == null);
            byte[] truncated = new byte[30];
            Array.Copy(rgba, truncated, truncated.Length);
            T.Check("png truncated file gives false", !PngDecoder.TryDecode(truncated, out junk) && junk == null);
            T.Check("png wrong signature gives false", !PngDecoder.TryDecode(Replace(rgba, 0, 0x88), out junk) && junk == null);

            byte[] interlaced = BuildPng(2, 2, 6, true, null, null, new byte[] {
                0, 255, 0, 0, 255, 0, 255, 0, 128,
                2, 1, 0, 255, 0, 10, 21, 30, 168 });
            T.Check("png interlaced image gives false", !PngDecoder.TryDecode(interlaced, out junk) && junk == null);

            byte[] badFilter = BuildPng(2, 1, 0, false, null, null, new byte[] { 5, 1, 2 });
            T.Check("png unknown filter type gives false", !PngDecoder.TryDecode(badFilter, out junk) && junk == null);
        }

        private static void CheckPixels(string name, byte[] png, int w, int h, byte[] expected)
        {
            RawImage img;
            bool ok = PngDecoder.TryDecode(png, out img);
            string detail = "decode=" + ok;
            if (ok)
            {
                detail += " size=" + img.Width + "x" + img.Height + " stride=" + img.Stride;
                ok = img.Width == w && img.Height == h && img.Stride == w * 4;
                if (ok)
                {
                    byte[] got = new byte[w * h * 4];
                    Array.Copy(img.Pixels, got, got.Length);
                    ok = SameBytes(got, expected);
                    detail += " pixels=" + BitConverter.ToString(got);
                }
            }
            T.Check(name, ok, detail);
        }

        // ---------------------------------------------------------------- capture commands

        private static void CaptureCases()
        {
            byte[] small = BuildPng(2, 2, 6, false, null, null, new byte[] {
                0, 255, 0, 0, 255, 0, 255, 0, 128,
                0, 0, 0, 255, 255, 255, 255, 255, 255 });

            var temp = new FakeTemp();
            var runner = new FakeRunner { Temp = temp, OutputFile = small };
            var capture = new MacScreenCapture(runner, temp, () => PermissionState.Granted);

            RawImage region = capture.CaptureRegion(new ScreenRect(10, 20, 300, 200));
            T.Check("region argv is exactly: -x -R10,20,300,200 -t png <tmp>",
                runner.FileName == "screencapture" &&
                ArgsEqual(runner.Args, "-x", "-R10,20,300,200", "-t", "png", temp.LastPath), Describe(runner));
            T.Check("region runs the command once", runner.Calls == 1, "calls=" + runner.Calls);
            T.Check("region returns the decoded PNG", region != null && region.Width == 2 && region.Height == 2 &&
                region.Pixels[0] == 0 && region.Pixels[2] == 255, region == null ? "null" : "size=" + region.Width + "x" + region.Height);
            T.Check("region temp file is deleted after success",
                temp.LastPath != null && temp.Deleted.Contains(temp.LastPath) && temp.Files.Count == 0);

            temp = new FakeTemp();
            runner = new FakeRunner { Temp = temp, OutputFile = small };
            capture = new MacScreenCapture(runner, temp, () => PermissionState.Granted);
            RawImage window = capture.CaptureWindow(12345);
            T.Check("window argv is exactly: -x -l12345 -t png <tmp>",
                runner.FileName == "screencapture" &&
                ArgsEqual(runner.Args, "-x", "-l12345", "-t", "png", temp.LastPath), Describe(runner));
            T.Check("window returns the decoded PNG", window != null && window.Width == 2);

            temp = new FakeTemp();
            runner = new FakeRunner { Temp = temp, OutputFile = small };
            capture = new MacScreenCapture(runner, temp, () => PermissionState.Denied);
            T.Check("permission Denied: region returns null", capture.CaptureRegion(new ScreenRect(0, 0, 10, 10)) == null);
            T.Check("permission Denied: window returns null", capture.CaptureWindow(7) == null);
            T.Check("permission Denied: no command run", runner.Calls == 0, "calls=" + runner.Calls);

            temp = new FakeTemp();
            runner = new FakeRunner { Temp = temp, OutputFile = small };
            capture = new MacScreenCapture(runner, temp, () => PermissionState.Unknown);
            capture.CaptureRegion(new ScreenRect(0, 0, 10, 10));
            T.Check("permission Unknown still runs the command", runner.Calls == 1);

            temp = new FakeTemp();
            runner = new FakeRunner { Temp = temp, ExitCode = 1, OutputFile = small };
            capture = new MacScreenCapture(runner, temp, () => PermissionState.Granted);
            T.Check("command exit 1 gives null", capture.CaptureRegion(new ScreenRect(0, 0, 10, 10)) == null);
            T.Check("command exit 1 still deletes the temp file",
                temp.LastPath != null && temp.Deleted.Contains(temp.LastPath));

            temp = new FakeTemp();
            runner = new FakeRunner { Temp = temp, OutputFile = new byte[] { 1, 2, 3 } };
            capture = new MacScreenCapture(runner, temp, () => PermissionState.Granted);
            T.Check("garbage output gives null", capture.CaptureWindow(9) == null);
            T.Check("garbage output still deletes the temp file",
                temp.LastPath != null && temp.Deleted.Contains(temp.LastPath) && temp.Files.Count == 0);

            temp = new FakeTemp();
            runner = new FakeRunner { Temp = temp, OutputFile = small };
            capture = new MacScreenCapture(runner, temp, () => PermissionState.Granted);
            T.Check("zero-width region gives null without running", capture.CaptureRegion(new ScreenRect(0, 0, 0, 10)) == null && runner.Calls == 0);
            T.Check("window id 0 gives null without running", capture.CaptureWindow(0) == null && runner.Calls == 0);
            T.Check("window id above 32 bits gives null without running", capture.CaptureWindow(4294967296L) == null && runner.Calls == 0);

            T.Check("GetPermissionState returns the injected value",
                new MacScreenCapture(runner, temp, () => PermissionState.Denied).GetPermissionState() == PermissionState.Denied);
        }

        // ---------------------------------------------------------------- helpers and test doubles

        private static bool ArgsEqual(List<string> actual, params string[] expected)
        {
            if (actual == null || actual.Count != expected.Length) return false;
            for (int i = 0; i < expected.Length; i++)
            {
                if (actual[i] != expected[i]) return false;
            }
            return true;
        }

        private static string Describe(FakeRunner runner)
        {
            return (runner.FileName ?? "(none)") + " " + string.Join(" | ", runner.Args ?? new List<string>());
        }

        public static bool SameBytes(byte[] a, byte[] b)
        {
            if (a == null || b == null || a.Length != b.Length) return false;
            for (int i = 0; i < a.Length; i++)
            {
                if (a[i] != b[i]) return false;
            }
            return true;
        }

        private static byte[] Replace(byte[] src, int index, byte value)
        {
            byte[] copy = (byte[])src.Clone();
            copy[index] = value;
            return copy;
        }

        // Builds a PNG with correct structure. CRCs are zero because the decoder does not check them.
        public static byte[] BuildPng(int w, int h, byte colourType, bool interlaced, byte[] plte, byte[] trns, byte[] filtered)
        {
            var ms = new MemoryStream();
            Put(ms, new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 });
            var ihdr = new MemoryStream();
            Put(ihdr, Be(w));
            Put(ihdr, Be(h));
            ihdr.WriteByte(8);
            ihdr.WriteByte(colourType);
            ihdr.WriteByte(0);
            ihdr.WriteByte(0);
            ihdr.WriteByte((byte)(interlaced ? 1 : 0));
            Chunk(ms, "IHDR", ihdr.ToArray());
            if (plte != null) Chunk(ms, "PLTE", plte);
            if (trns != null) Chunk(ms, "tRNS", trns);
            Chunk(ms, "IDAT", StoredZlib(filtered));
            Chunk(ms, "IEND", new byte[0]);
            return ms.ToArray();
        }

        // zlib stream made of stored (uncompressed) deflate blocks. The Adler-32 trailer is zero (not checked).
        private static byte[] StoredZlib(byte[] data)
        {
            var ms = new MemoryStream();
            ms.WriteByte(0x78);
            ms.WriteByte(0x01);
            int pos = 0;
            do
            {
                int n = Math.Min(65535, data.Length - pos);
                bool last = pos + n >= data.Length;
                ms.WriteByte((byte)(last ? 1 : 0));
                ms.WriteByte((byte)(n & 0xFF));
                ms.WriteByte((byte)((n >> 8) & 0xFF));
                ms.WriteByte((byte)(~n & 0xFF));
                ms.WriteByte((byte)((~n >> 8) & 0xFF));
                ms.Write(data, pos, n);
                pos += n;
            } while (pos < data.Length);
            Put(ms, new byte[4]);
            return ms.ToArray();
        }

        private static void Chunk(MemoryStream ms, string type, byte[] data)
        {
            Put(ms, Be(data.Length));
            Put(ms, Encoding.ASCII.GetBytes(type));
            Put(ms, data);
            Put(ms, new byte[4]);
        }

        private static byte[] Be(int v)
        {
            return new byte[] { (byte)(v >> 24), (byte)(v >> 16), (byte)(v >> 8), (byte)v };
        }

        private static void Put(MemoryStream ms, byte[] bytes)
        {
            ms.Write(bytes, 0, bytes.Length);
        }

        public sealed class FakeTemp : IMacCaptureTemp
        {
            public readonly Dictionary<string, byte[]> Files = new Dictionary<string, byte[]>();
            public readonly List<string> Deleted = new List<string>();
            public readonly List<byte[]> Written = new List<byte[]>();
            public string LastPath;
            private int _count;

            public string CreatePath()
            {
                _count++;
                LastPath = "/tmp/cj-test-" + _count + ".png";
                return LastPath;
            }

            public bool TryWrite(string path, byte[] bytes)
            {
                Files[path] = bytes;
                Written.Add(bytes);
                return true;
            }

            public bool TryRead(string path, out byte[] bytes)
            {
                return Files.TryGetValue(path, out bytes);
            }

            public void Delete(string path)
            {
                Deleted.Add(path);
                Files.Remove(path);
            }
        }

        // Records each call. When Temp and OutputFile are set, the "screencapture" output file is written at the last argument.
        public sealed class FakeRunner : ICommandRunner
        {
            public string FileName;
            public List<string> Args;
            public int Calls;
            public int ExitCode;
            public string Stdout = "";
            public FakeTemp Temp;
            public byte[] OutputFile;

            public CommandResult Run(string fileName, IReadOnlyList<string> arguments, int timeoutMs)
            {
                Calls++;
                FileName = fileName;
                Args = new List<string>(arguments);
                if (Temp != null && OutputFile != null && arguments.Count > 0)
                {
                    Temp.Files[arguments[arguments.Count - 1]] = OutputFile;
                }
                return new CommandResult(ExitCode, Stdout, "", false, false);
            }
        }
    }
}
