using System;
using System.Runtime.InteropServices;

namespace CookieJar.Platform.Windows.Native
{
    /// <summary>GDI calls for screen and window capture. Each handle returned here must be released by the caller.</summary>
    internal static class Gdi32
    {
        public const uint SRCCOPY = 0x00CC0020;
        public const uint CAPTUREBLT = 0x40000000;
        public const uint DIB_RGB_COLORS = 0;

        [DllImport("gdi32.dll")]
        public static extern IntPtr CreateCompatibleDC(IntPtr hdc);

        [DllImport("gdi32.dll")]
        public static extern bool DeleteDC(IntPtr hdc);

        [DllImport("gdi32.dll")]
        public static extern IntPtr SelectObject(IntPtr hdc, IntPtr obj);

        [DllImport("gdi32.dll")]
        public static extern bool DeleteObject(IntPtr obj);

        [DllImport("gdi32.dll")]
        public static extern bool BitBlt(IntPtr hdcDest, int x, int y, int cx, int cy, IntPtr hdcSrc, int x1, int y1, uint rop);

        // A BITMAPINFO with no colour table is valid for 32bpp BI_RGB, so the header alone is passed.
        [DllImport("gdi32.dll")]
        public static extern IntPtr CreateDIBSection(IntPtr hdc, ref CaptureBitmapInfo bmi, uint usage, out IntPtr bits, IntPtr section, uint offset);
    }

    /// <summary>user32 calls needed by capture. Kept here so all capture P/Invokes live in one file.</summary>
    internal static class CaptureUser32
    {
        [DllImport("user32.dll")]
        public static extern IntPtr GetDC(IntPtr hwnd);

        [DllImport("user32.dll")]
        public static extern int ReleaseDC(IntPtr hwnd, IntPtr hdc);

        [DllImport("user32.dll")]
        public static extern bool PrintWindow(IntPtr hwnd, IntPtr hdc, uint flags);

        [DllImport("user32.dll")]
        public static extern bool IsWindow(IntPtr hwnd);

        [DllImport("user32.dll")]
        public static extern bool IsIconic(IntPtr hwnd);

        [DllImport("user32.dll")]
        public static extern bool GetClientRect(IntPtr hwnd, out CaptureRect rect);
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct CaptureBitmapInfo
    {
        public int Size;
        public int Width;
        public int Height;
        public ushort Planes;
        public ushort BitCount;
        public uint Compression;
        public uint SizeImage;
        public int XPelsPerMeter;
        public int YPelsPerMeter;
        public uint ClrUsed;
        public uint ClrImportant;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct CaptureRect
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }
}
