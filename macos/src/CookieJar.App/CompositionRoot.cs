// Compiles with the .NET 8 SDK; not yet run on a Mac. The only place that constructs platform services; the App layer holds no logic.

using System;
using System.IO;
using CookieJar.AppModel;
using CookieJar.Platform;
using CookieJar.Platform.Mac;

namespace CookieJar.App
{
    internal sealed class CompositionRoot
    {
        private CompositionRoot(MainViewModel main)
        {
            Main = main;
        }

        public MainViewModel Main { get; }

        public static CompositionRoot Create()
        {
            // MAC-UNVERIFIED: the user profile is assumed to be the macOS home folder used by the Mac services.
            string home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            ICommandRunner runner = new ProcessCommandRunner();
            MacPlatform platform = new MacPlatform(runner, home);
            return new CompositionRoot(new MainViewModel(platform));
        }

        // Only the services built here are wired. The rest stay null until their constructors are
        // composed, and the view-model reads just Name and Capabilities from the platform.
        private sealed class MacPlatform : IPlatform
        {
            public MacPlatform(ICommandRunner runner, string home)
            {
                // MAC-UNVERIFIED: flags are the ones MacProcessService really provides; others are added as their services are wired.
                Capabilities = MacCapabilities.Process;
                Processes = new MacProcessService(runner);
                RobloxLocator = new MacRobloxLocator(Directory.Exists, File.Exists, home, runner);
                RobloxLauncher = new MacRobloxLauncher(runner, Processes, (MacRobloxLocator)RobloxLocator);
                Secrets = new MacSecretStore(runner);
                Windows = new MacWindowService(runner);
                Paths = new MacAppPaths(home);
            }

            public string Name { get { return "macOS"; } }
            public PlatformCapabilities Capabilities { get; }
            public IProcessService Processes { get; }
            public IRobloxLocator RobloxLocator { get; }
            public IRobloxLauncher RobloxLauncher { get; }
            public ISecretStore Secrets { get; }
            public IWindowService Windows { get; }
            public IInputService Input { get { return null; } }
            public IScreenCapture ScreenCapture { get { return null; } }
            public IOcrService Ocr { get { return null; } }
            public IStartupService Startup { get { return null; } }
            public IAppPaths Paths { get; }
            public IShellService Shell { get { return null; } }
        }
    }
}
