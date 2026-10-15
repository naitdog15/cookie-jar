using CookieJar.AppModel;
using CookieJar.Platform;

namespace CookieJar.Tests
{
    internal sealed class CapsPlatform : IPlatform
    {
        public CapsPlatform(PlatformCapabilities caps) { Capabilities = caps; }
        public string Name { get { return "CapsPlatform"; } }
        public PlatformCapabilities Capabilities { get; }
        public IProcessService Processes { get { return null; } }
        public IRobloxLocator RobloxLocator { get { return null; } }
        public IRobloxLauncher RobloxLauncher { get { return null; } }
        public ISecretStore Secrets { get { return null; } }
        public IWindowService Windows { get { return null; } }
        public IInputService Input { get { return null; } }
        public IScreenCapture ScreenCapture { get { return null; } }
        public IOcrService Ocr { get { return null; } }
        public IStartupService Startup { get { return null; } }
        public IAppPaths Paths { get { return null; } }
        public IShellService Shell { get { return null; } }
    }

    public static class FeatureStateTests
    {
        public static void Run()
        {
            CapsPlatform all = new CapsPlatform(PlatformCapabilities.MemoryCap | PlatformCapabilities.BackgroundPriority
                | PlatformCapabilities.WindowPlacement | PlatformCapabilities.InputInjection | PlatformCapabilities.StartAtLogin);
            CapsPlatform none = new CapsPlatform(PlatformCapabilities.None);
            CapsPlatform only = new CapsPlatform(PlatformCapabilities.InputInjection);

            FeatureState ok = FeatureState.From(all, PlatformCapabilities.MemoryCap, "Memory cap");
            T.Check("supported: visible", ok.Visible);
            T.Check("supported: enabled", ok.Enabled);
            T.Check("supported: no reason", ok.Reason == "", ok.Reason);

            FeatureState bad = FeatureState.From(none, PlatformCapabilities.MemoryCap, "Memory cap");
            T.Check("unsupported: still visible", bad.Visible);
            T.Check("unsupported: disabled", !bad.Enabled);
            T.Check("unsupported: reason names label and platform",
                bad.Reason == "Memory cap is not supported on CapsPlatform.", bad.Reason);

            T.Check("null reason becomes empty", new FeatureState(true, true, null).Reason == "");
            T.Check("ctor keeps flags", !new FeatureState(false, true, "x").Visible && new FeatureState(false, true, "x").Enabled);

            bool threw = false;
            try { FeatureState.From(null, PlatformCapabilities.MemoryCap, "x"); }
            catch (System.ArgumentNullException) { threw = true; }
            T.Check("null platform throws", threw);

            MainViewModel vm = new MainViewModel(only);
            T.Check("only InputInjection: input enabled", vm.InputInjection.Enabled);
            T.Check("only InputInjection: memory cap disabled", !vm.MemoryCap.Enabled);
            T.Check("only InputInjection: background priority disabled", !vm.BackgroundPriority.Enabled);
            T.Check("only InputInjection: placement disabled", !vm.WindowPlacement.Enabled);
            T.Check("only InputInjection: start at login disabled", !vm.StartAtLogin.Enabled);
            T.Check("start at login reason", vm.StartAtLogin.Reason == "Start at login is not supported on CapsPlatform.", vm.StartAtLogin.Reason);

            MainViewModel full = new MainViewModel(all);
            T.Check("full platform enables every gate",
                full.MemoryCap.Enabled && full.BackgroundPriority.Enabled && full.WindowPlacement.Enabled
                && full.InputInjection.Enabled && full.StartAtLogin.Enabled);

            AccountRowViewModel row = new AccountRowViewModel("id1", "Alt", none);
            T.Check("account row placement disabled without capability", !row.Placement.Enabled && row.Placement.Reason.Length > 0);
        }
    }
}
