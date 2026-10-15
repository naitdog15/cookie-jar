using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using CookieJar.Platform;

namespace CookieJar.Platform.Mac
{
    /// <summary>
    /// OCR through an external helper executable, `cookiejar-ocr &lt;png-path&gt;`. The helper is built separately
    /// for the Mac and is not part of this repo yet. Contract:
    ///   - argument: path to an 8-bit RGBA PNG written by Recognise
    ///   - stdout: one line per recognised text run:
    ///       x&lt;TAB&gt;y&lt;TAB&gt;w&lt;TAB&gt;h&lt;TAB&gt;confidence&lt;TAB&gt;text
    ///     x, y, w, h are integer pixels of the image, top-left origin. confidence is a decimal number written with
    ///     a dot (invariant culture); it is clamped to 0..1 on read. text is everything after the fifth tab (it may
    ///     contain tabs) and is trimmed.
    ///   - lines that do not parse, have width or height at most 0, or have empty text are skipped.
    ///   - exit code 0 is success. Any other exit code gives an empty result.
    /// Helper absent: IsAvailable is false and Recognise returns an empty list without running anything.
    /// Recognise never throws. MAC-UNVERIFIED: helper not run on a Mac yet.
    /// </summary>
    public sealed class MacOcrService : IOcrService
    {
        // MAC-UNVERIFIED: a short text crop should take well under this; the timeout guards a hung helper.
        private const int HelperTimeoutMs = 30000;

        private readonly ICommandRunner _runner;
        private readonly string _helperPath;
        private readonly Func<bool> _helperExists;
        private readonly IMacCaptureTemp _temp;

        public MacOcrService(ICommandRunner runner, string helperPath, Func<bool> helperExists, IMacCaptureTemp temp)
        {
            if (runner == null) throw new ArgumentNullException(nameof(runner));
            if (temp == null) throw new ArgumentNullException(nameof(temp));
            string path = helperPath ?? "";
            _runner = runner;
            _helperPath = path;
            _helperExists = helperExists ?? (() => File.Exists(path));
            _temp = temp;
        }

        /// <summary>True when the helper executable is present.</summary>
        public bool IsAvailable
        {
            get
            {
                try { return _helperExists(); }
                catch (Exception) { return false; }
            }
        }

        /// <summary>Returns flags with Ocr set only when <paramref name="service"/> is available (a null service clears it).</summary>
        public static PlatformCapabilities WithOcrFlag(PlatformCapabilities flags, MacOcrService service)
        {
            bool on = service != null && service.IsAvailable;
            return on ? (flags | PlatformCapabilities.Ocr) : (flags & ~PlatformCapabilities.Ocr);
        }

        public IReadOnlyList<OcrLine> Recognise(RawImage image)
        {
            var empty = new List<OcrLine>();
            if (image == null || image.Width <= 0 || image.Height <= 0 || !IsAvailable) return empty;

            string png = null;
            try
            {
                png = _temp.CreatePath();
                if (!_temp.TryWrite(png, PngEncoder.Encode(image))) return empty;
                CommandResult result = _runner.Run(_helperPath, new List<string> { png }, HelperTimeoutMs);
                return result.Success ? ParseLines(result.StandardOutput) : empty;
            }
            catch (Exception)
            {
                return empty;
            }
            finally
            {
                if (png != null)
                {
                    try { _temp.Delete(png); }
                    catch (Exception) { }
                }
            }
        }

        /// <summary>Parses helper stdout per the class contract. Malformed lines are skipped; never throws.</summary>
        public static List<OcrLine> ParseLines(string text)
        {
            var lines = new List<OcrLine>();
            if (string.IsNullOrEmpty(text)) return lines;
            foreach (string raw in text.Split('\n'))
            {
                OcrLine line;
                if (TryParseLine(raw, out line)) lines.Add(line);
            }
            return lines;
        }

        private static bool TryParseLine(string raw, out OcrLine line)
        {
            line = null;
            if (raw == null) return false;
            string[] f = raw.TrimEnd('\r').Split('\t');
            if (f.Length < 6) return false;

            int x, y, w, h;
            double confidence;
            if (!int.TryParse(f[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out x)) return false;
            if (!int.TryParse(f[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out y)) return false;
            if (!int.TryParse(f[2], NumberStyles.Integer, CultureInfo.InvariantCulture, out w)) return false;
            if (!int.TryParse(f[3], NumberStyles.Integer, CultureInfo.InvariantCulture, out h)) return false;
            if (w <= 0 || h <= 0) return false;
            if (!double.TryParse(f[4], NumberStyles.Float, CultureInfo.InvariantCulture, out confidence)) return false;
            if (double.IsNaN(confidence)) return false;
            if (confidence < 0) confidence = 0;
            if (confidence > 1) confidence = 1;

            string text = string.Join("\t", f, 5, f.Length - 5).Trim();
            if (text.Length == 0) return false;

            line = new OcrLine(text, new ScreenRect(x, y, w, h), confidence);
            return true;
        }

        /// <summary>Minimal PNG writer: 8-bit RGBA, no filters, stored (uncompressed) deflate blocks.</summary>
        private static class PngEncoder
        {
            private static readonly uint[] CrcTable = BuildCrcTable();

            public static byte[] Encode(RawImage img)
            {
                int w = img.Width, h = img.Height;
                int rowLen = w * 4;
                byte[] raw = new byte[h * (rowLen + 1)];
                for (int y = 0; y < h; y++)
                {
                    int dst = y * (rowLen + 1);      // filter byte 0 (None) stays zero
                    for (int x = 0; x < w; x++)
                    {
                        int s = img.OffsetOf(x, y);
                        int d = dst + 1 + x * 4;
                        raw[d] = img.Pixels[s + 2];
                        raw[d + 1] = img.Pixels[s + 1];
                        raw[d + 2] = img.Pixels[s];
                        raw[d + 3] = img.Pixels[s + 3];
                    }
                }

                var ms = new MemoryStream();
                Put(ms, new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 });
                var ihdr = new MemoryStream();
                Put(ihdr, BigEndian(w));
                Put(ihdr, BigEndian(h));
                ihdr.WriteByte(8);   // bit depth
                ihdr.WriteByte(6);   // colour type RGBA
                ihdr.WriteByte(0);   // compression
                ihdr.WriteByte(0);   // filter method
                ihdr.WriteByte(0);   // no interlace
                WriteChunk(ms, "IHDR", ihdr.ToArray());
                WriteChunk(ms, "IDAT", StoredZlib(raw));
                WriteChunk(ms, "IEND", new byte[0]);
                return ms.ToArray();
            }

            private static byte[] StoredZlib(byte[] data)
            {
                var ms = new MemoryStream();
                ms.WriteByte(0x78);   // CMF: deflate, 32K window
                ms.WriteByte(0x01);   // FLG: no dictionary, fastest, check bits
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

                uint a = 1, b = 0;
                foreach (byte v in data)
                {
                    a = (a + v) % 65521;
                    b = (b + a) % 65521;
                }
                Put(ms, BigEndian((int)((b << 16) | a)));
                return ms.ToArray();
            }

            private static void WriteChunk(MemoryStream ms, string type, byte[] data)
            {
                byte[] typeBytes = System.Text.Encoding.ASCII.GetBytes(type);
                Put(ms, BigEndian(data.Length));
                Put(ms, typeBytes);
                Put(ms, data);
                uint crc = 0xFFFFFFFF;
                crc = CrcUpdate(crc, typeBytes);
                crc = CrcUpdate(crc, data) ^ 0xFFFFFFFF;
                Put(ms, BigEndian((int)crc));
            }

            private static uint CrcUpdate(uint crc, byte[] bytes)
            {
                foreach (byte v in bytes)
                {
                    crc = CrcTable[(crc ^ v) & 0xFF] ^ (crc >> 8);
                }
                return crc;
            }

            private static uint[] BuildCrcTable()
            {
                var table = new uint[256];
                for (uint n = 0; n < 256; n++)
                {
                    uint c = n;
                    for (int k = 0; k < 8; k++)
                    {
                        c = (c & 1) != 0 ? 0xEDB88320 ^ (c >> 1) : c >> 1;
                    }
                    table[n] = c;
                }
                return table;
            }

            private static byte[] BigEndian(int v)
            {
                return new byte[] { (byte)(v >> 24), (byte)(v >> 16), (byte)(v >> 8), (byte)v };
            }

            private static void Put(MemoryStream ms, byte[] bytes)
            {
                ms.Write(bytes, 0, bytes.Length);
            }
        }
    }
}
