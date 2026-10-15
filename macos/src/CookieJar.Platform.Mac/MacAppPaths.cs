using System;
using System.IO;

namespace CookieJar.Platform.Mac
{
    /// <summary>
    /// macOS per-user folders. Getters create the folder on demand and never throw; a folder that
    /// cannot be created is still returned so callers get the same path every time.
    /// </summary>
    public sealed class MacAppPaths : IAppPaths
    {
        private readonly string _home;

        public MacAppPaths(string homeDir)
        {
            _home = string.IsNullOrEmpty(homeDir) ? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile) : homeDir;
        }

        public string DataDirectory
        {
            get { return Ensure(_home + "/Library/Application Support/CookieJar"); }
        }

        public string LogDirectory
        {
            get { return Ensure(_home + "/Library/Logs/CookieJar"); }
        }

        public string DiagnosticsDirectory
        {
            get { return Ensure(DataDirectory + "/diagnostics"); }
        }

        private static string Ensure(string path)
        {
            try { Directory.CreateDirectory(path); }
            catch (Exception) { }
            return path;
        }
    }
}
