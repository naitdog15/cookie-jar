using System;
using System.IO;

namespace CookieJar.Platform.Mac
{
    /// <summary>
    /// The macOS platform: every Mac service, composed once. Create it with CreateDefault at startup and hand it to the
    /// app; nothing else needs to know which concrete class sits behind which interface.
    /// Capabilities are fixed at creation. A permission the user may still grant (Accessibility, Screen Recording,
    /// Automation) is not a capability: the services report it through their permission state.
    /// </summary>
    public sealed class MacPlatform : IPlatform
    {
        public MacPlatform(PlatformCapabilities capabilities, IProcessService processes, IRobloxLocator robloxLocator,
            IRobloxLauncher robloxLauncher, ISecretStore secrets, IWindowService windows, IInputService input,
            IScreenCapture screenCapture, IOcrService ocr, IStartupService startup, IAppPaths paths, IShellService shell)
        {
            Capabilities = capabilities;
            Processes = processes;
            RobloxLocator = robloxLocator;
            RobloxLauncher = robloxLauncher;
            Secrets = secrets;
            Windows = windows;
            Input = input;
            ScreenCapture = screenCapture;
            Ocr = ocr;
            Startup = startup;
            Paths = paths;
            Shell = shell;
        }

        public string Name { get { return "macOS"; } }
        public PlatformCapabilities Capabilities { get; }
        public IProcessService Processes { get; }
        public IRobloxLocator RobloxLocator { get; }
        public IRobloxLauncher RobloxLauncher { get; }
        public ISecretStore Secrets { get; }
        public IWindowService Windows { get; }
        public IInputService Input { get; }
        public IScreenCapture ScreenCapture { get; }
        public IOcrService Ocr { get; }
        public IStartupService Startup { get; }
        public IAppPaths Paths { get; }
        public IShellService Shell { get; }

        /// <summary>
        /// Composes the real macOS services.
        /// </summary>
        /// <param name="runner">Runs `ps`, `open`, `security`, `osascript` and friends. Default: ProcessCommandRunner. It must also
        /// implement IStdinCommandRunner or secrets cannot be stored (they are never put on a command line).</param>
        /// <param name="homeDirectory">The user's home folder. Default: the current user profile.</param>
        /// <param name="executablePath">The app's own executable, used for "start at login". Default: this process's main module.</param>
        /// <param name="ocrHelperPath">The Vision OCR helper executable. Default: cookiejar-ocr next to the app. OCR is reported
        /// as supported only when the file exists.</param>
        public static MacPlatform CreateDefault(ICommandRunner runner = null, string homeDirectory = null,
            string executablePath = null, string ocrHelperPath = null)
        {
            runner = runner ?? new ProcessCommandRunner();
            string home = string.IsNullOrEmpty(homeDirectory)
                ? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile) : homeDirectory;

            var processes = new MacProcessService(runner);
            var locator = new MacRobloxLocator(Directory.Exists, File.Exists, home, runner);
            var launcher = new MacRobloxLauncher(runner, processes, locator);
            var secrets = new MacSecretStore(runner);
            var windows = new MacWindowService(runner, pid => processes.GetDescendants(pid));
            var input = new MacInputService(new MacInputNative(), windows);
            var capture = new MacScreenCapture(runner, new MacCaptureTemp(), null, windows);

            string helper = string.IsNullOrEmpty(ocrHelperPath)
                ? Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "cookiejar-ocr") : ocrHelperPath;
            var ocr = new MacOcrService(runner, helper, null, new MacCaptureTemp());

            string exe = string.IsNullOrEmpty(executablePath) ? CurrentExecutable() : executablePath;
            var startup = new MacStartupService(runner, home, exe, File.Exists,
                delegate (string path, string text)
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(path));
                    File.WriteAllText(path, text);
                },
                delegate (string path) { File.Delete(path); });

            var paths = new MacAppPaths(home);
            var shell = new MacShellService(runner, Path.Combine(paths.DataDirectory, "locks"));

            // MAC-UNVERIFIED: these flags say the services exist and are wired; none of them has run on a Mac yet.
            PlatformCapabilities caps = MacCapabilities.Process
                | PlatformCapabilities.WindowPlacement
                | PlatformCapabilities.InputInjection
                | PlatformCapabilities.ScreenCapture
                | PlatformCapabilities.StartAtLogin
                | PlatformCapabilities.SecureStorage;
            if (ocr.IsAvailable) caps |= PlatformCapabilities.Ocr;

            return new MacPlatform(caps, processes, locator, launcher, secrets, windows, input, capture, ocr, startup, paths, shell);
        }

        private static string CurrentExecutable()
        {
            try
            {
                using (var p = System.Diagnostics.Process.GetCurrentProcess())
                {
                    return p.MainModule == null ? "" : p.MainModule.FileName;
                }
            }
            catch (Exception)
            {
                return "";
            }
        }
    }
}
