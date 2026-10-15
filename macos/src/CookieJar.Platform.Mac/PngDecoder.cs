using System;
using System.IO;
using System.IO.Compression;
using System.Text;

namespace CookieJar.Platform.Mac
{
    /// <summary>
    /// Minimal PNG decoder for capture output, with no packages. Supports 8-bit grey (0), RGB (2),
    /// palette (3, with PLTE and tRNS) and RGBA (6), all five scanline filters.
    /// Limitations: interlaced (Adam7) images give false; other bit depths give false; chunk CRCs and the
    /// zlib Adler-32 are not checked; tRNS is only used for palette images. Never throws.
    /// </summary>
    public static class PngDecoder
    {
        // Guards against a bogus IHDR asking for a huge buffer (50 million pixels is about 200 MB of BGRA).
        private const long MaxPixels = 50000000;
        private static readonly byte[] Signature = { 137, 80, 78, 71, 13, 10, 26, 10 };

        /// <summary>Decodes a PNG to BGRA32 (stride = width * 4, alpha 255 when the image has none). False for anything else.</summary>
        public static bool TryDecode(byte[] png, out RawImage image)
        {
            image = null;
            try
            {
                return Decode(png, out image);
            }
            catch (Exception)
            {
                image = null;
                return false;
            }
        }

        private static bool Decode(byte[] png, out RawImage image)
        {
            image = null;
            if (png == null || png.Length < 8 + 12) return false;
            for (int i = 0; i < Signature.Length; i++)
            {
                if (png[i] != Signature[i]) return false;
            }

            int width = 0, height = 0, colourType = -1;
            byte[] palette = null, trns = null;
            bool sawHeader = false;
            var idat = new MemoryStream();

            int pos = 8;
            while (pos + 8 <= png.Length)
            {
                long len = ReadBE32(png, pos);
                string type = Encoding.ASCII.GetString(png, pos + 4, 4);
                int body = pos + 8;
                // Data plus the CRC that follows it must fit. The CRC itself is not compared.
                if (len > png.Length || body + len + 4 > png.Length) return false;
                int n = (int)len;

                if (type == "IEND") break;
                if (type == "IHDR")
                {
                    if (sawHeader || n != 13) return false;
                    long w = ReadBE32(png, body);
                    long h = ReadBE32(png, body + 4);
                    if (w <= 0 || h <= 0 || w * h > MaxPixels) return false;
                    width = (int)w;
                    height = (int)h;
                    if (png[body + 8] != 8) return false;           // bit depth
                    colourType = png[body + 9];
                    if (colourType != 0 && colourType != 2 && colourType != 3 && colourType != 6) return false;
                    if (png[body + 10] != 0 || png[body + 11] != 0) return false;   // compression, filter method
                    if (png[body + 12] != 0) return false;          // interlaced: not supported
                    sawHeader = true;
                }
                else if (type == "PLTE")
                {
                    if (!sawHeader || n == 0 || n % 3 != 0 || n / 3 > 256) return false;
                    palette = Slice(png, body, n);
                }
                else if (type == "tRNS")
                {
                    if (sawHeader && colourType == 3) trns = Slice(png, body, n);
                }
                else if (type == "IDAT")
                {
                    if (!sawHeader) return false;
                    idat.Write(png, body, n);
                }
                pos = body + n + 4;
            }

            if (!sawHeader || idat.Length == 0) return false;
            if (colourType == 3 && palette == null) return false;

            int bpp = colourType == 0 || colourType == 3 ? 1 : colourType == 2 ? 3 : 4;
            int rowBytes = width * bpp;
            byte[] raw = Inflate(idat.ToArray(), (long)height * (rowBytes + 1));
            if (raw == null) return false;

            byte[] recon = new byte[height * rowBytes];
            if (!Unfilter(raw, recon, height, rowBytes, bpp)) return false;

            byte[] pixels = new byte[width * height * 4];
            for (int y = 0; y < height; y++)
            {
                for (int x = 0; x < width; x++)
                {
                    int s = y * rowBytes + x * bpp;
                    int d = (y * width + x) * 4;
                    switch (colourType)
                    {
                        case 0:
                            pixels[d] = recon[s]; pixels[d + 1] = recon[s]; pixels[d + 2] = recon[s]; pixels[d + 3] = 255;
                            break;
                        case 2:
                            pixels[d] = recon[s + 2]; pixels[d + 1] = recon[s + 1]; pixels[d + 2] = recon[s]; pixels[d + 3] = 255;
                            break;
                        case 6:
                            pixels[d] = recon[s + 2]; pixels[d + 1] = recon[s + 1]; pixels[d + 2] = recon[s]; pixels[d + 3] = recon[s + 3];
                            break;
                        default:
                            int index = recon[s];
                            if (index >= palette.Length / 3) return false;
                            pixels[d] = palette[index * 3 + 2];
                            pixels[d + 1] = palette[index * 3 + 1];
                            pixels[d + 2] = palette[index * 3];
                            pixels[d + 3] = trns != null && index < trns.Length ? trns[index] : (byte)255;
                            break;
                    }
                }
            }

            image = new RawImage(width, height, width * 4, pixels);
            return true;
        }

        // Undoes the scanline filters. raw holds one filter byte then rowBytes per row. False on an unknown filter type.
        private static bool Unfilter(byte[] raw, byte[] recon, int height, int rowBytes, int bpp)
        {
            for (int y = 0; y < height; y++)
            {
                int filter = raw[y * (rowBytes + 1)];
                int src = y * (rowBytes + 1) + 1;
                int dst = y * rowBytes;
                int prior = dst - rowBytes;
                for (int i = 0; i < rowBytes; i++)
                {
                    int a = i >= bpp ? recon[dst + i - bpp] : 0;             // left
                    int b = y > 0 ? recon[prior + i] : 0;                    // up
                    int c = y > 0 && i >= bpp ? recon[prior + i - bpp] : 0;  // up-left
                    int x = raw[src + i];
                    int v;
                    switch (filter)
                    {
                        case 0: v = x; break;
                        case 1: v = x + a; break;
                        case 2: v = x + b; break;
                        case 3: v = x + ((a + b) >> 1); break;
                        case 4: v = x + Paeth(a, b, c); break;
                        default: return false;
                    }
                    recon[dst + i] = (byte)v;
                }
            }
            return true;
        }

        private static int Paeth(int a, int b, int c)
        {
            int p = a + b - c;
            int pa = Math.Abs(p - a), pb = Math.Abs(p - b), pc = Math.Abs(p - c);
            if (pa <= pb && pa <= pc) return a;
            if (pb <= pc) return b;
            return c;
        }

        // IDAT payload is a zlib stream: skip the 2-byte header, inflate raw deflate, ignore the Adler-32 trailer.
        private static byte[] Inflate(byte[] data, long expected)
        {
            if (data.Length < 2 || expected <= 0 || expected > int.MaxValue) return null;
            byte[] buf = new byte[(int)expected];
            using (var ds = new DeflateStream(new MemoryStream(data, 2, data.Length - 2), CompressionMode.Decompress))
            {
                int got = 0;
                while (got < buf.Length)
                {
                    int r = ds.Read(buf, got, buf.Length - got);
                    if (r <= 0) break;
                    got += r;
                }
                if (got < buf.Length) return null;
            }
            return buf;
        }

        private static long ReadBE32(byte[] b, int p)
        {
            return ((long)b[p] << 24) | ((long)b[p + 1] << 16) | ((long)b[p + 2] << 8) | b[p + 3];
        }

        private static byte[] Slice(byte[] src, int offset, int count)
        {
            byte[] r = new byte[count];
            Buffer.BlockCopy(src, offset, r, 0, count);
            return r;
        }
    }
}
