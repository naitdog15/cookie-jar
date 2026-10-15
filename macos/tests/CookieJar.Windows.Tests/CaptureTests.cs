using CookieJar.Platform;
using CookieJar.Platform.Windows;

namespace CookieJar.Tests
{
    public static class CaptureTests
    {
        public static void Run()
        {
            WinScreenCapture cap = new WinScreenCapture();

            // The primary display's origin is (0,0) in screen pixels (ScreenRect contract).
            RawImage img = cap.CaptureRegion(new ScreenRect(0, 0, 64, 64));
            T.Check("CaptureRegion 64x64 at origin returns an image", img != null);
            if (img != null)
            {
                T.Check("CaptureRegion width is 64", img.Width == 64, "Width=" + img.Width);
                T.Check("CaptureRegion height is 64", img.Height == 64, "Height=" + img.Height);
                T.Check("CaptureRegion stride is width*4", img.Stride == 256, "Stride=" + img.Stride);
                T.Check("CaptureRegion pixel buffer holds 64*64*4 bytes", img.Pixels.Length >= 64 * 64 * 4, "Length=" + img.Pixels.Length);

                bool allOpaque = true;
                for (int y = 0; y < img.Height && allOpaque; y++)
                {
                    for (int x = 0; x < img.Width; x++)
                    {
                        if (img.Pixels[img.OffsetOf(x, y) + 3] != 255) { allOpaque = false; break; }
                    }
                }
                T.Check("CaptureRegion alpha is 255 on every pixel", allOpaque);
            }

            bool repeatedOk = true;
            for (int i = 0; i < 200 && repeatedOk; i++)
            {
                repeatedOk = cap.CaptureRegion(new ScreenRect(0, 0, 64, 64)) != null;
            }
            T.Check("CaptureRegion repeated 200 times keeps succeeding (no handle exhaustion)", repeatedOk);

            T.Check("CaptureRegion width 0 returns null", cap.CaptureRegion(new ScreenRect(0, 0, 0, 64)) == null);
            T.Check("CaptureRegion height 0 returns null", cap.CaptureRegion(new ScreenRect(0, 0, 64, 0)) == null);
            T.Check("CaptureRegion negative width returns null", cap.CaptureRegion(new ScreenRect(0, 0, -5, 64)) == null);
            T.Check("CaptureRegion negative height returns null", cap.CaptureRegion(new ScreenRect(0, 0, 64, -5)) == null);

            T.Check("CaptureWindow(0) returns null", cap.CaptureWindow(0) == null);
            T.Check("GetPermissionState is Granted", cap.GetPermissionState() == PermissionState.Granted);
        }
    }
}
