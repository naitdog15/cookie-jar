using System;

namespace CookieJar.Platform
{
    /// <summary>Features an OS implementation can provide. Features gate on these flags, never on OS names.</summary>
    [Flags]
    public enum PlatformCapabilities
    {
        /// <summary>Nothing is supported.</summary>
        None = 0,
        /// <summary>IProcessService.TryCapWorkingSet really limits resident memory.</summary>
        MemoryCap = 1 << 0,
        /// <summary>IProcessService.SetBackgroundPriority lowers CPU/efficiency class.</summary>
        BackgroundPriority = 1 << 1,
        /// <summary>IWindowService can move/resize/raise windows.</summary>
        WindowPlacement = 1 << 2,
        /// <summary>IInputService can synthesize mouse/keyboard events.</summary>
        InputInjection = 1 << 3,
        /// <summary>IScreenCapture can capture screens or windows.</summary>
        ScreenCapture = 1 << 4,
        /// <summary>IOcrService can recognise text.</summary>
        Ocr = 1 << 5,
        /// <summary>IStartupService can register start at login.</summary>
        StartAtLogin = 1 << 6,
        /// <summary>ISecretStore uses OS-protected storage (DPAPI / Keychain).</summary>
        SecureStorage = 1 << 7,
        /// <summary>Several Roblox clients may run at once (IRobloxLauncher.PrepareForAnotherInstance works).</summary>
        MultiInstance = 1 << 8
    }

    /// <summary>Entry point to the OS services. Implementations are created once at startup.</summary>
    public interface IPlatform
    {
        /// <summary>Short OS name, e.g. "Windows" or "macOS".</summary>
        string Name { get; }
        /// <summary>Union of features this platform supports. Constant for the process lifetime.</summary>
        PlatformCapabilities Capabilities { get; }
        IProcessService Processes { get; }
        IRobloxLocator RobloxLocator { get; }
        IRobloxLauncher RobloxLauncher { get; }
        ISecretStore Secrets { get; }
        IWindowService Windows { get; }
        IInputService Input { get; }
        IScreenCapture ScreenCapture { get; }
        IOcrService Ocr { get; }
        IStartupService Startup { get; }
        IAppPaths Paths { get; }
        IShellService Shell { get; }
    }

    /// <summary>Helpers for capability checks.</summary>
    public static class PlatformExtensions
    {
        /// <summary>True when every flag in <paramref name="required"/> is supported.</summary>
        public static bool Supports(this IPlatform platform, PlatformCapabilities required)
        {
            if (platform == null) throw new ArgumentNullException(nameof(platform));
            return (platform.Capabilities & required) == required;
        }
    }
}
