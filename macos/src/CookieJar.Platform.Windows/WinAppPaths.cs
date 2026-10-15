using System;
using System.IO;
using CookieJar.Platform;

namespace CookieJar.Platform.Windows
{
    /// <summary>
    /// Per-user folders under %LocalAppData%\RobloxMultiLauncher, matching src/. Getters only build paths:
    /// the folders are created by whoever writes into them (as src does), so reading a path has no side effects.
    /// </summary>
    public sealed class WinAppPaths : IAppPaths
    {
        private const string AppFolder = "RobloxMultiLauncher";

        private static string Root
        {
            get { return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), AppFolder); }
        }

        public string DataDirectory
        {
            get { return Root; }
        }

        // src/ has no app log folder; "Logs" is the fallback name from the task.
        public string LogDirectory
        {
            get { return Path.Combine(Root, "Logs"); }
        }

        // src/InputDiagnostics.cs writes to the "diag" folder.
        public string DiagnosticsDirectory
        {
            get { return Path.Combine(Root, "diag"); }
        }
    }
}
