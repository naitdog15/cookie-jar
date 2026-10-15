using System;
using System.Collections.Generic;
using System.Linq;
using CookieJar.Platform;
using CookieJar.Platform.Mac;

namespace CookieJar.Tests
{
    public static class MacPlatformTests
    {
        public static void Run()
        {
            RecordingRunner runner = new RecordingRunner();
            MacPlatform p = MacPlatform.CreateDefault(runner, "/Users/tester", "/Applications/Cookie Jar.app/Contents/MacOS/CookieJar", "/nonexistent/cookiejar-ocr");

            T.Check("MacPlatform is named macOS", p.Name == "macOS");
            T.Check("every service is wired (none is null)",
                p.Processes != null && p.RobloxLocator != null && p.RobloxLauncher != null && p.Secrets != null && p.Windows != null
                && p.Input != null && p.ScreenCapture != null && p.Ocr != null && p.Startup != null && p.Paths != null && p.Shell != null);
            T.Check("services have the expected macOS types",
                p.Processes is MacProcessService && p.Secrets is MacSecretStore && p.Windows is MacWindowService
                && p.Input is MacInputService && p.ScreenCapture is MacScreenCapture && p.Startup is MacStartupService && p.Shell is MacShellService);
            T.Check("the window service can say which process is in front (needed for background CPU saving)", p.Windows is IForegroundProcessSource);

            PlatformCapabilities caps = p.Capabilities;
            T.Check("capabilities cover process priority, multi-instance, windows, input, capture, start at login and secrets",
                p.Supports(PlatformCapabilities.BackgroundPriority | PlatformCapabilities.MultiInstance | PlatformCapabilities.WindowPlacement
                    | PlatformCapabilities.InputInjection | PlatformCapabilities.ScreenCapture | PlatformCapabilities.StartAtLogin | PlatformCapabilities.SecureStorage), caps.ToString());
            T.Check("macOS has no per-process RAM cap, so MemoryCap is not claimed", !p.Supports(PlatformCapabilities.MemoryCap));
            T.Check("OCR is not claimed when the helper executable is missing", !p.Supports(PlatformCapabilities.Ocr) && !((MacOcrService)p.Ocr).IsAvailable);

            T.Check("data directory follows the home folder you gave", p.Paths.DataDirectory.StartsWith("/Users/tester/Library/Application Support"), p.Paths.DataDirectory);

            // The services share the one command runner: storing a secret reaches `security -i` with the secret on stdin only.
            bool stored = p.Secrets.Set("token", "s3cret-value");
            RecordedCall call = runner.Calls.LastOrDefault();
            T.Check("a secret stored through the platform goes to security -i on stdin and never onto argv",
                stored && call != null && call.FileName == "security" && call.Arguments.Count == 1 && call.Arguments[0] == "-i"
                && call.Stdin != null && call.Stdin.Contains("s3cret-value") && !runner.Calls.Any(c => c.Arguments.Any(a => a.Contains("s3cret-value"))));

            MacPlatform withOcr = MacPlatform.CreateDefault(runner, "/Users/tester", "/x", System.Reflection.Assembly.GetExecutingAssembly().Location);
            T.Check("OCR is claimed when the helper file exists", withOcr.Supports(PlatformCapabilities.Ocr) && ((MacOcrService)withOcr.Ocr).IsAvailable);

            RawImageCases();
        }

        private static void RawImageCases()
        {
            bool threw = false;
            try { new RawImage(0x40000001, 1, 4, new byte[4]); }   // width * 4 wraps to 4 in 32-bit arithmetic
            catch (ArgumentException) { threw = true; }
            T.Check("RawImage rejects a width whose row size overflows 32 bits (it used to wrap and pass)", threw);

            threw = false;
            try { new RawImage(2, 1, 7, new byte[16]); }
            catch (ArgumentException) { threw = true; }
            T.Check("RawImage rejects a stride smaller than one row", threw);

            threw = false;
            try { new RawImage(2, 2, 8, new byte[15]); }
            catch (ArgumentException) { threw = true; }
            T.Check("RawImage rejects a pixel buffer that is too short", threw);

            RawImage ok = new RawImage(2, 2, 8, new byte[16]);
            T.Check("a valid RawImage keeps its size and offsets", ok.Width == 2 && ok.Height == 2 && ok.OffsetOf(1, 1) == 12);
        }
    }
}
