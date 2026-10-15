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

        /// <summary>Permission defaults to the Screen Recording preflight check (Unknown when it cannot run).</summary>
        public MacScreenCapture(ICommandRunner runner, IMacCaptureTemp temp, Func<PermissionState> permission)
        {
            if (runner == null) throw new ArgumentNullException(nameof(runner));
            if (temp == null) throw new ArgumentNullException(nameof(temp));
            _runner = runner;
            _temp = temp;
            _permission = permission ?? (() => ProbeScreenRecording());
        }

        public RawImage CaptureRegion(ScreenRect region)
        {
            if (region.Width <= 0 || region.Height <= 0) return null;
            return CaptureWith("-R" + Inv(region.X) + "," + Inv(region.Y) + "," + Inv(region.Width) + "," + Inv(region.Height));
        }

        public RawImage CaptureWindow(long windowId)
        {
            // screencapture takes a 32-bit CGWindowID.
            if (windowId <= 0 || windowId > uint.MaxValue) return null;
            return CaptureWith("-l" + windowId.ToString(CultureInfo.InvariantCulture));
        }

        public PermissionState GetPermissionState()
        {
            try { return _permission(); }
            catch (Exception) { return PermissionState.Unknown; }
        }

        private RawImage CaptureWith(string selector)
        {
            if (GetPermissionState() == PermissionState.Denied) return null;
            string path = null;
            try
            {
                path = _temp.CreatePath();
                var args = new List<string> { "-x", selector, "-t", "png", path };
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
