using System;
using System.Globalization;
using System.IO;
using System.Text;

namespace RbxMulti
{
    /// <summary>
    /// Keeps a copy of the activity log in %LocalAppData%\RobloxMultiLauncher\launcher.log so a failed launch
    /// can be reported after the window is closed. Lines never carry tokens or tickets (same rule as the
    /// on-screen log). Every method swallows exceptions and never throws.
    /// </summary>
    public static class LauncherLog
    {
        private const long MaxBytes = 512L * 1024L;
        private static readonly object Gate = new object();

        public static string FilePath
        {
            get
            {
                string root = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
                return Path.Combine(root, "RobloxMultiLauncher", "launcher.log");
            }
        }

        public static void Write(string message)
        {
            try
            {
                lock (Gate)
                {
                    string path = FilePath;
                    Directory.CreateDirectory(Path.GetDirectoryName(path));
                    FileInfo info = new FileInfo(path);
                    if (info.Exists && info.Length > MaxBytes)
                    {
                        string old = path + ".old";
                        if (File.Exists(old)) File.Delete(old);
                        File.Move(path, old);
                    }
                    string line = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture) + "  " + message + Environment.NewLine;
                    File.AppendAllText(path, line, Encoding.UTF8);
                }
            }
            catch
            {
            }
        }

        /// <summary>One line describing the setup that matters for multi-launch problems.</summary>
        public static void WriteStartup()
        {
            try
            {
                string exe = RobloxLocator.FindPlayerExe();
                string roblox = string.IsNullOrEmpty(exe) ? "not found" : exe;
                Write("Start-up: Cookie Jar " + AppVersion.Display
                    + ", admin=" + HandleCloser.IsElevated()
                    + ", " + Environment.OSVersion.VersionString
                    + ", Roblox player " + roblox);
            }
            catch
            {
            }
        }
    }
}
