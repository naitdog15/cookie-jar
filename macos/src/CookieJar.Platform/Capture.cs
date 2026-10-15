using System;
using System.Collections.Generic;

namespace CookieJar.Platform
{
    /// <summary>Pixel buffer, BGRA 8-bit per channel, top row first. Replaces System.Drawing.Bitmap.</summary>
    public sealed class RawImage
    {
        public RawImage(int width, int height, int stride, byte[] pixels)
        {
            if (width < 0) throw new ArgumentOutOfRangeException(nameof(width));
            if (height < 0) throw new ArgumentOutOfRangeException(nameof(height));
            if (pixels == null) throw new ArgumentNullException(nameof(pixels));
            if (stride < width * 4) throw new ArgumentException("stride too small", nameof(stride));
            if ((long)stride * height > pixels.Length) throw new ArgumentException("pixels too short", nameof(pixels));
            Width = width; Height = height; Stride = stride; Pixels = pixels;
        }
        public int Width { get; }
        public int Height { get; }
        /// <summary>Bytes per row (at least Width*4).</summary>
        public int Stride { get; }
        /// <summary>BGRA32 data, length at least Stride*Height.</summary>
        public byte[] Pixels { get; }

        /// <summary>Offset of pixel (x, y); channels are B, G, R, A.</summary>
        public int OffsetOf(int x, int y) { return y * Stride + x * 4; }
    }

    /// <summary>Screen/window capture. Without ScreenCapture, methods return null. May need a permission (macOS Screen Recording).</summary>
    public interface IScreenCapture
    {
        /// <summary>Captures a screen rectangle in screen pixels, or null on failure.</summary>
        RawImage CaptureRegion(ScreenRect region);
        /// <summary>Captures the content of a window (even if partly covered where the OS allows), or null.</summary>
        RawImage CaptureWindow(long windowId);
        /// <summary>Permission state for capture.</summary>
        PermissionState GetPermissionState();
    }

    /// <summary>One recognised line of text; Bounds are in the image's pixel coordinates.</summary>
    public sealed class OcrLine
    {
        public OcrLine(string text, ScreenRect bounds, double confidence)
        {
            Text = text ?? ""; Bounds = bounds; Confidence = confidence;
        }
        public string Text { get; }
        public ScreenRect Bounds { get; }
        /// <summary>0..1, or -1 when the engine gives none.</summary>
        public double Confidence { get; }
    }

    /// <summary>Text recognition. Without the Ocr capability, Recognise returns an empty list. May be slow; call off the UI thread.</summary>
    public interface IOcrService
    {
        IReadOnlyList<OcrLine> Recognise(RawImage image);
    }
}
