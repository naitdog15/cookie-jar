// UNVERIFIED: never compiled. Avalonia entry point; the Windows Program.cs (WPF) is not used here.

using System;
using Avalonia;

namespace CookieJar.App
{
    internal static class Program
    {
        [STAThread]
        private static void Main(string[] args)
        {
            BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
        }

        // MAC-UNVERIFIED: UsePlatformDetect is assumed to select the macOS backend (Avalonia.Native) on a Mac.
        public static AppBuilder BuildAvaloniaApp()
        {
            return AppBuilder.Configure<App>().UsePlatformDetect();
        }
    }
}
