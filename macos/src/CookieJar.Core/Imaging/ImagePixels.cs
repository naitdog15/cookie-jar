using System;
using System.Text;
using CookieJar.Platform;

namespace CookieJar.Core.Imaging
{
    /// <summary>Managed BGRA operations. Inputs are never mutated, including padded capture rows.</summary>
    public static class ImagePixels
    {
        public static byte[] Packed(RawImage image)
        {
            if (image == null) throw new ArgumentNullException(nameof(image));
            int row = checked(image.Width * 4);
            byte[] pixels = new byte[checked(row * image.Height)];
            for (int y = 0; y < image.Height; y++)
                Buffer.BlockCopy(image.Pixels, y * image.Stride, pixels, y * row, row);
            return pixels;
        }

        public static RawImage Crop(RawImage image, ImageRect bounds)
        {
            if (image == null) throw new ArgumentNullException(nameof(image));
            bounds.Intersect(new ImageRect(0, 0, image.Width, image.Height));
            if (bounds.IsEmpty) throw new ArgumentException("Crop must overlap the image.", nameof(bounds));
            int stride = checked(bounds.Width * 4);
            byte[] pixels = new byte[checked(stride * bounds.Height)];
            for (int y = 0; y < bounds.Height; y++)
                Buffer.BlockCopy(image.Pixels, image.OffsetOf(bounds.X, bounds.Y + y), pixels, y * stride, stride);
            return new RawImage(bounds.Width, bounds.Height, stride, pixels);
        }

        /// <summary>Bilinear resampling with clamped edges; output is packed BGRA.</summary>
        public static RawImage Resize(RawImage image, int width, int height)
        {
            if (image == null) throw new ArgumentNullException(nameof(image));
            if (image.Width <= 0 || image.Height <= 0 || width <= 0 || height <= 0)
                throw new ArgumentException("Resize needs nonempty images.");
            int stride = checked(width * 4);
            byte[] pixels = new byte[checked(stride * height)];
            for (int y = 0; y < height; y++)
            {
                double sy = Math.Max(0, (y + 0.5) * image.Height / height - 0.5);
                int y0 = Math.Min(image.Height - 1, (int)sy), y1 = Math.Min(image.Height - 1, y0 + 1);
                double fy = sy - y0;
                for (int x = 0; x < width; x++)
                {
                    double sx = Math.Max(0, (x + 0.5) * image.Width / width - 0.5);
                    int x0 = Math.Min(image.Width - 1, (int)sx), x1 = Math.Min(image.Width - 1, x0 + 1);
                    double fx = sx - x0;
                    int a = image.OffsetOf(x0, y0), b = image.OffsetOf(x1, y0);
                    int c = image.OffsetOf(x0, y1), d = image.OffsetOf(x1, y1);
                    int dest = y * stride + x * 4;
                    for (int channel = 0; channel < 4; channel++)
                    {
                        double top = image.Pixels[a + channel] * (1 - fx) + image.Pixels[b + channel] * fx;
                        double bottom = image.Pixels[c + channel] * (1 - fx) + image.Pixels[d + channel] * fx;
                        pixels[dest + channel] = (byte)Math.Round(top * (1 - fy) + bottom * fy);
                    }
                }
            }
            return new RawImage(width, height, stride, pixels);
        }

        internal static RawImage Upscale(RawImage image, int targetWidth)
        {
            double scale = Math.Min(6, Math.Max(1, targetWidth / (double)Math.Max(1, image.Width)));
            return Resize(image, (int)Math.Round(image.Width * scale), (int)Math.Round(image.Height * scale));
        }

        // Average every source pixel covered by each grid cell; point sampling can miss thin scene changes.
        internal static byte[] SceneGrid(RawImage image, int width, int height)
        {
            byte[] output = new byte[checked(width * height * 4)];
            for (int gy = 0; gy < height; gy++)
                for (int gx = 0; gx < width; gx++)
                {
                    double left = gx * (double)image.Width / width, right = (gx + 1) * (double)image.Width / width;
                    double top = gy * (double)image.Height / height, bottom = (gy + 1) * (double)image.Height / height;
                    double blue = 0, green = 0, red = 0, total = 0;
                    for (int y = (int)top; y < Math.Min(image.Height, (int)Math.Ceiling(bottom)); y++)
                        for (int x = (int)left; x < Math.Min(image.Width, (int)Math.Ceiling(right)); x++)
                        {
                            double weight = (Math.Min(right, x + 1) - Math.Max(left, x))
                                * (Math.Min(bottom, y + 1) - Math.Max(top, y));
                            int offset = image.OffsetOf(x, y);
                            blue += image.Pixels[offset] * weight;
                            green += image.Pixels[offset + 1] * weight;
                            red += image.Pixels[offset + 2] * weight;
                            total += weight;
                        }
                    int dest = (gy * width + gx) * 4;
                    output[dest] = (byte)Math.Round(blue / total);
                    output[dest + 1] = (byte)Math.Round(green / total);
                    output[dest + 2] = (byte)Math.Round(red / total);
                    output[dest + 3] = 255;
                }
            return output;
        }

        /// <summary>Scales, stretches text contrast, and adds a white margin for the platform OCR engine.</summary>
        public static RawImage PrepareNameCrop(RawImage image, int scale, int mode = 0)
        {
            scale = Math.Max(1, Math.Min(8, scale));
            RawImage big = Resize(image, checked(image.Width * scale), checked(image.Height * scale));
            byte[] pixels = big.Pixels; // Resize owns a new buffer.
            int n = checked(big.Width * big.Height);
            int[] hist = new int[256];
            byte[] lum = new byte[n];
            for (int i = 0; i < n; i++)
            {
                int l = (pixels[i * 4 + 2] * 77 + pixels[i * 4 + 1] * 151 + pixels[i * 4] * 28) >> 8;
                lum[i] = (byte)l; hist[l]++;
            }
            int bg = 255, best = -1;
            for (int v = 0; v < 256; v++)
            {
                int sum = 0;
                for (int k = Math.Max(0, v - 3); k <= Math.Min(255, v + 3); k++) sum += hist[k];
                if (sum > best) { best = sum; bg = v; }
            }
            int acc = 0, lo = 0, target = (int)(n * 0.02);
            for (int v = 0; v < 256; v++) { acc += hist[v]; if (acc >= target) { lo = v; break; } }
            if (lo > bg - 30) lo = Math.Max(0, bg - 30);
            int span = Math.Max(1, bg - lo);
            for (int i = 0; i < n; i++)
            {
                int value = Math.Max(0, Math.Min(255, (lum[i] - lo) * 255 / span));
                if (mode == 1) value = value > 150 ? 255 : 0;
                else if (mode == 0 && value > 200) value = 255;
                pixels[i * 4] = pixels[i * 4 + 1] = pixels[i * 4 + 2] = (byte)value;
                pixels[i * 4 + 3] = 255;
            }
            int pad = 6 * scale, width = checked(big.Width + 2 * pad), height = checked(big.Height + 2 * pad);
            int stride = checked(width * 4);
            byte[] padded = new byte[checked(stride * height)];
            for (int i = 0; i < padded.Length; i++) padded[i] = 255;
            for (int y = 0; y < big.Height; y++)
                Buffer.BlockCopy(pixels, y * big.Stride, padded, (y + pad) * stride + pad * 4, big.Stride);
            return new RawImage(width, height, stride, padded);
        }

        internal static string ReadLine(IOcrService ocr, RawImage image)
        {
            if (ocr == null || image == null) return "";
            try
            {
                var lines = ocr.Recognise(image);
                if (lines == null) return "";
                var text = new StringBuilder();
                foreach (var line in lines)
                {
                    if (line == null || string.IsNullOrWhiteSpace(line.Text)) continue;
                    if (text.Length > 0) text.Append(' ');
                    text.Append(line.Text);
                }
                return text.ToString().Trim();
            }
            catch (Exception) { return ""; }
        }
    }
}
