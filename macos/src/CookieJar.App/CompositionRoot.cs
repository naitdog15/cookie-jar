// Compiles with the .NET 8 SDK; not yet run on a Mac. The only place that creates the platform; the App layer holds no logic.

using CookieJar.AppModel;
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
            // MacPlatform composes every Mac service once (processes, Roblox, secrets, windows, input, capture, OCR,
            // start at login, shell) and sets the capability flags the view-model gates features on.
            // MAC-UNVERIFIED: nothing here has run on a Mac yet.
            MacPlatform platform = MacPlatform.CreateDefault();
            return new CompositionRoot(new MainViewModel(platform));
        }
    }
}
