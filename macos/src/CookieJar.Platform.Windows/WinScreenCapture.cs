using System;
using System.Runtime.InteropServices;
using CookieJar.Platform.Windows.Native;

namespace CookieJar.Platform.Windows
{
    /// <summary>GDI screen and window capture. Windows needs no capture permission.</summary>
    public sealed class WinScreenCapture : IScreenCapture
    {
        // PW_CLIENTONLY | PW_RENDERFULLCONTENT, the flags the shipping app uses for DirectX windows.
        private const uint PrintWindowFlags = 3u;

        public RawImage CaptureRegion(ScreenRect region)
        {
            if (region.Width <= 0 || region.Height <= 0) return null;
            IntPtr screenDc = IntPtr.Zero, memDc = IntPtr.Zero, dib = IntPtr.Zero, old = IntPtr.Zero;
            try
            {
                screenDc = CaptureUser32.GetDC(IntPtr.Zero);
                if (screenDc == IntPtr.Zero) return null;
                memDc = Gdi32.CreateCompatibleDC(screenDc);
                if (memDc == IntPtr.Zero) return null;
                IntPtr bits;
                dib = CreateDib(memDc, region.Width, region.Height, out bits);
                if (dib == IntPtr.Zero) return null;
                old = Gdi32.SelectObject(memDc, dib);
                if (old == IntPtr.Zero) return null;
                if (!Gdi32.BitBlt(memDc, 0, 0, region.Width, region.Height, screenDc, region.X, region.Y, Gdi32.SRCCOPY | Gdi32.CAPTUREBLT)) return null;
                return CopyPixels(bits, region.Width, region.Height);
            }
            finally
            {
                ReleaseHandles(screenDc, memDc, old, dib);
            }
        }

        public RawImage CaptureWindow(long windowId)
        {
            if (windowId == 0) return null;
            IntPtr hwnd = new IntPtr(windowId);
            if (!CaptureUser32.IsWindow(hwnd) || CaptureUser32.IsIconic(hwnd)) return null;
            CaptureRect rc;
            if (!CaptureUser32.GetClientRect(hwnd, out rc)) return null;
            int width = rc.Right - rc.Left;
            int height = rc.Bottom - rc.Top;
            if (width <= 0 || height <= 0) return null;

            IntPtr memDc = IntPtr.Zero, dib = IntPtr.Zero, old = IntPtr.Zero;
            try
            {
                memDc = Gdi32.CreateCompatibleDC(IntPtr.Zero);
                if (memDc == IntPtr.Zero) return null;
                IntPtr bits;
                dib = CreateDib(memDc, width, height, out bits);
                if (dib == IntPtr.Zero) return null;
                old = Gdi32.SelectObject(memDc, dib);
                if (old == IntPtr.Zero) return null;
                if (!CaptureUser32.PrintWindow(hwnd, memDc, PrintWindowFlags)) return null;
                return CopyPixels(bits, width, height);
            }
            finally
            {
                ReleaseHandles(IntPtr.Zero, memDc, old, dib);
            }
        }

        public PermissionState GetPermissionState()
        {
            return PermissionState.Granted;
        }

        // Top-down 32bpp section (negative height), so rows come out in image order with stride = width*4.
        private static IntPtr CreateDib(IntPtr dc, int width, int height, out IntPtr bits)
        {
            CaptureBitmapInfo bmi = new CaptureBitmapInfo();
            bmi.Size = Marshal.SizeOf(typeof(CaptureBitmapInfo));
            bmi.Width = width;
            bmi.Height = -height;
            bmi.Planes = 1;
            bmi.BitCount = 32;
            bmi.Compression = 0;
            return Gdi32.CreateDIBSection(dc, ref bmi, Gdi32.DIB_RGB_COLORS, out bits, IntPtr.Zero, 0);
        }

        // Copies the section while it is still selected and alive; the caller releases the handles afterwards.
        private static RawImage CopyPixels(IntPtr bits, int width, int height)
        {
            int stride = width * 4;
            byte[] pixels = new byte[stride * height];
            Marshal.Copy(bits, pixels, 0, pixels.Length);
            // BitBlt and PrintWindow leave alpha undefined; the contract wants opaque pixels.
            for (int i = 3; i < pixels.Length; i += 4) pixels[i] = 255;
            return new RawImage(width, height, stride, pixels);
        }

        // Deselects the bitmap before deleting it, then releases each handle that was obtained.
        private static void ReleaseHandles(IntPtr screenDc, IntPtr memDc, IntPtr old, IntPtr dib)
        {
            if (memDc != IntPtr.Zero && old != IntPtr.Zero) Gdi32.SelectObject(memDc, old);
            if (dib != IntPtr.Zero) Gdi32.DeleteObject(dib);
            if (memDc != IntPtr.Zero) Gdi32.DeleteDC(memDc);
            if (screenDc != IntPtr.Zero) CaptureUser32.ReleaseDC(IntPtr.Zero, screenDc);
        }
    }
}
