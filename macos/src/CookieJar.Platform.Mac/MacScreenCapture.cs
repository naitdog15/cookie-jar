using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;

namespace CookieJar.Platform.Mac
{
    /// <summary>Temp files used by the macOS capture and OCR paths. Injectable so tests run without the disk.</summary>
    public interface IMacCaptureTemp
    {
        /// <summary>A new unique file path. The file need not exist yet.</summary>
        string CreatePath();
        bool TryWrite(string path, byte[] bytes);
        bool TryRead(string path, out byte[] bytes);
        /// <summary>Deletes the file if it exists. Never throws.</summary>
        void Delete(string path);
    }

    /// <summary>Default temp store: files in the system temp folder.</summary>
    public sealed class MacCaptureTemp : IMacCaptureTemp
    {
        public string CreatePath()
        {
            return Path.Combine(Path.GetTempPath(), "cookiejar-capture-" + Guid.NewGuid().ToString("N") + ".png");
        }

        public bool TryWrite(string path, byte[] bytes)
        {
            try { File.WriteAllBytes(path, bytes); return true; }
            catch (Exception) { return false; }
        }

        public bool TryRead(string path, out byte[] bytes)
        {
            try { bytes = File.ReadAllBytes(path); return true; }
            catch (Exception) { bytes = null; return false; }
        }

        public void Delete(string path)
        {
            try { if (File.Exists(path)) File.Delete(path); }
            catch (Exception) { }
        }
    }

    /// <summary>
    /// Screen and window capture through the `screencapture` tool. Output is PNG, decoded with PngDecoder.
    /// Window ids: CaptureWindow takes a CGWindowID (the number `screencapture -l` expects). This is NOT the
    /// IWindowService id encoding on macOS, so callers must not pass IWindowService ids here without converting them.
    /// Retina: the PNG is at backing-store scale (usually 2x the point size), so the returned image is larger than
    /// the ScreenRect. The image is returned as decoded and is not rescaled. MAC-UNVERIFIED.
    /// </summary>
    public sealed class MacScreenCapture : IScreenCapture
    {
        // MAC-UNVERIFIED: a full-screen grab takes well under a second; the timeout only guards a hung tool.
        private const int CaptureTimeoutMs = 15000;
        private const string Tool = "screencapture";

        private readonly ICommandRunner _runner;
        private readonly IMacCaptureTemp _temp;
        private readonly Func<PermissionState> _permission;
        private readonly IWindowService _windows;

        /// <summary>
        /// Permission defaults to the Screen Recording preflight check (Unknown when it cannot run). With a window
        /// service, CaptureWindow crops the title bar so the result is the window content, as the contract says;
        /// without one it returns the whole window (title bar included).
        /// </summary>
        public MacScreenCapture(ICommandRunner runner, IMacCaptureTemp temp, Func<PermissionState> permission, IWindowService windows = null)
        {
            if (runner == null) throw new ArgumentNullException(nameof(runner));
            if (temp == null) throw new ArgumentNullException(nameof(temp));
            _runner = runner;
            _temp = temp;
            _permission = permission ?? (() => ProbeScreenRecording());
            _windows = windows;
        }

        public RawImage CaptureRegion(ScreenRect region)
        {
            if (region.Width <= 0 || region.Height <= 0) return null;
            return CaptureWith("-R" + Inv(region.X) + "," + Inv(region.Y) + "," + Inv(region.Width) + "," + Inv(region.Height));
        }

        public RawImage CaptureWindow(long windowId)
        {
            // screencapture takes a 32-bit CGWindowID, which is exactly what MacWindowService uses as window id.
            if (windowId <= 0 || windowId > uint.MaxValue) return null;
            RawImage whole = CaptureWith("-l" + windowId.ToString(CultureInfo.InvariantCulture), true);
            if (whole == null || _windows == null) return whole;

            // The image is in pixels, the window bounds in points (2 px per point on Retina); crop to the content area.
            // MAC-UNVERIFIED: with -o (no shadow) the image width is frame.Width * backing scale.
            ScreenRect frame, content;
            if (!_windows.TryGetBounds(windowId, out frame) || !_windows.TryGetContentBounds(windowId, out content) || frame.Width <= 0) return whole;
            double scale = (double)whole.Width / frame.Width;
            RawImage cropped = Crop(whole,
                (int)Math.Round((content.X - frame.X) * scale), (int)Math.Round((content.Y - frame.Y) * scale),
                (int)Math.Round(content.Width * scale), (int)Math.Round(content.Height * scale));
            return cropped ?? whole;
        }

        /// <summary>A copy of the given pixel rectangle, clamped to the image. Null when nothing is left.</summary>
        public static RawImage Crop(RawImage source, int x, int y, int width, int height)
        {
            if (source == null) return null;
            if (x < 0) { width += x; x = 0; }
            if (y < 0) { height += y; y = 0; }
            if (x >= source.Width || y >= source.Height) return null;
            if (width > source.Width - x) width = source.Width - x;
            if (height > source.Height - y) height = source.Height - y;
            if (width <= 0 || height <= 0) return null;
            if (x == 0 && y == 0 && width == source.Width && height == source.Height) return source;

            long rowBytes = (long)width * 4;
            if (rowBytes > int.MaxValue || rowBytes * height > int.MaxValue) return null;
            var pixels = new byte[rowBytes * height];
            for (int row = 0; row < height; row++)
            {
                Buffer.BlockCopy(source.Pixels, source.OffsetOf(x, y + row), pixels, (int)(rowBytes * row), (int)rowBytes);
            }
            return new RawImage(width, height, (int)rowBytes, pixels);
        }

        public PermissionState GetPermissionState()
        {
            try { return _permission(); }
            catch (Exception) { return PermissionState.Unknown; }
        }

        private RawImage CaptureWith(string selector, bool withoutShadow = false)
        {
            if (GetPermissionState() == PermissionState.Denied) return null;
            string path = null;
            try
            {
                path = _temp.CreatePath();
                // -o leaves the window shadow out, so the image is exactly the window (needed for the crop maths).
                var args = withoutShadow
                    ? new List<string> { "-x", "-o", selector, "-t", "png", path }
                    : new List<string> { "-x", selector, "-t", "png", path };
                CommandResult result = _runner.Run(Tool, args, CaptureTimeoutMs);
                if (!result.Success) return null;
                byte[] png;
                if (!_temp.TryRead(path, out png)) return null;
                RawImage image;
                return PngDecoder.TryDecode(png, out image) ? image : null;
            }
            catch (Exception)
            {
                return null;
            }
            finally
            {
                if (path != null)
                {
                    try { _temp.Delete(path); }
                    catch (Exception) { }
                }
            }
        }

        private static string Inv(int value)
        {
            return value.ToString(CultureInfo.InvariantCulture);
        }

        // MAC-UNVERIFIED: CGPreflightScreenCaptureAccess (macOS 10.15+) returns false when Screen Recording is not
        // granted. It cannot tell "never asked" from "denied", so false is reported as Denied. Where the framework is
        // missing the call throws, which is caught and reported as Unknown.
        [DllImport("/System/Library/Frameworks/CoreGraphics.framework/CoreGraphics")]
        [return: MarshalAs(UnmanagedType.U1)]
        private static extern bool CGPreflightScreenCaptureAccess();

        private static PermissionState ProbeScreenRecording()
        {
            try
            {
                return CGPreflightScreenCaptureAccess() ? PermissionState.Granted : PermissionState.Denied;
            }
            catch (Exception)
            {
                return PermissionState.Unknown;
            }
        }
    }
}
