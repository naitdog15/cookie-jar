// Compiles with the .NET 8 SDK; not yet run on a Mac. Wires the shell only; all state comes from CompositionRoot.

using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;

namespace CookieJar.App
{
    public partial class App : Application
    {
        public override void Initialize()
        {
            AvaloniaXamlLoader.Load(this);
        }

        public override void OnFrameworkInitializationCompleted()
        {
            // MAC-UNVERIFIED: classic desktop lifetime is assumed to give a normal macOS app menu and Dock behaviour.
            if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
            {
                CompositionRoot root = CompositionRoot.Create();
                desktop.MainWindow = new MainWindow { DataContext = root.Main };
            }
            base.OnFrameworkInitializationCompleted();
        }
    }
}
