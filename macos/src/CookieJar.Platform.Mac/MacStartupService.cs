using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text;

namespace CookieJar.Platform.Mac
{
    /// <summary>
    /// Start at login through a per-user LaunchAgent plist. The plist file is the source of truth:
    /// launchctl calls are best effort, so a failed bootstrap still counts as enabled (it loads at next login).
    /// </summary>
    public sealed class MacStartupService : IStartupService
    {
        /// <summary>LaunchAgent label, also the plist file name.</summary>
        public const string Label = "com.cookiejar.app";
        // Same flag the Windows Start-with-Windows task passes (mini bar only at logon).
        private const string MiniBarFlag = "--minibar";
        private const int TimeoutMs = 15000;
        private const string LaunchctlTool = "launchctl";

        private readonly ICommandRunner _runner;
        private readonly string _plistPath;
        private readonly string _executablePath;
        private readonly Func<string, bool> _fileExists;
        private readonly Action<string, string> _writeFile;
        private readonly Action<string> _deleteFile;
        private readonly Func<int> _getUid;

        public MacStartupService(ICommandRunner runner, string homeDir, string executablePath,
            Func<string, bool> fileExists, Action<string, string> writeFile, Action<string> deleteFile,
            Func<int> getUid = null)
        {
            if (runner == null) throw new ArgumentNullException("runner");
            if (fileExists == null) throw new ArgumentNullException("fileExists");
            if (writeFile == null) throw new ArgumentNullException("writeFile");
            if (deleteFile == null) throw new ArgumentNullException("deleteFile");
            string home = string.IsNullOrEmpty(homeDir) ? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile) : homeDir;
            _runner = runner;
            _plistPath = home + "/Library/LaunchAgents/" + Label + ".plist";
            _executablePath = executablePath;
            _fileExists = fileExists;
            _writeFile = writeFile;
            _deleteFile = deleteFile;
            _getUid = getUid ?? new Func<int>(DefaultUid);
        }

        /// <summary>Plist path this service manages (for tests and diagnostics).</summary>
        public string PlistPath { get { return _plistPath; } }

        public bool IsEnabled()
        {
            try { return _fileExists(_plistPath); }
            catch (Exception) { return false; }
        }

        public bool SetEnabled(bool enabled)
        {
            try
            {
                if (enabled)
                {
                    if (string.IsNullOrEmpty(_executablePath)) return false;
                    _writeFile(_plistPath, BuildPlist(Label, _executablePath));
                    RunLaunchctl(new List<string> { "bootstrap", "gui/" + CurrentUid(), _plistPath });
                    return true;
                }

                RunLaunchctl(new List<string> { "bootout", "gui/" + CurrentUid() + "/" + Label });
                if (_fileExists(_plistPath)) _deleteFile(_plistPath);
                return true;
            }
            catch (Exception)
            {
                return false;
            }
        }

        /// <summary>Builds the LaunchAgent plist. Values are XML-escaped, so paths with &amp; or spaces stay valid.</summary>
        public static string BuildPlist(string label, string executablePath)
        {
            var sb = new StringBuilder();
            sb.Append("<?xml version=\"1.0\" encoding=\"UTF-8\"?>\n");
            sb.Append("<!DOCTYPE plist PUBLIC \"-//Apple//DTD PLIST 1.0//EN\" \"http://www.apple.com/DTDs/PropertyList-1.0.dtd\">\n");
            sb.Append("<plist version=\"1.0\">\n");
            sb.Append("<dict>\n");
            sb.Append("  <key>Label</key>\n");
            sb.Append("  <string>").Append(XmlEscape(label)).Append("</string>\n");
            sb.Append("  <key>ProgramArguments</key>\n");
            sb.Append("  <array>\n");
            sb.Append("    <string>").Append(XmlEscape(executablePath)).Append("</string>\n");
            sb.Append("    <string>").Append(MiniBarFlag).Append("</string>\n");
            sb.Append("  </array>\n");
            sb.Append("  <key>RunAtLoad</key>\n");
            sb.Append("  <true/>\n");
            sb.Append("  <key>ProcessType</key>\n");
            sb.Append("  <string>Interactive</string>\n");
            sb.Append("</dict>\n");
            sb.Append("</plist>\n");
            return sb.ToString();
        }

        private static string XmlEscape(string value)
        {
            if (value == null) return "";
            return value.Replace("&", "&amp;")
                .Replace("<", "&lt;")
                .Replace(">", "&gt;")
                .Replace("\"", "&quot;")
                .Replace("'", "&apos;");
        }

        private void RunLaunchctl(List<string> arguments)
        {
            // Best effort: the result is ignored, and a runner that throws must not fail SetEnabled.
            try { _runner.Run(LaunchctlTool, arguments, TimeoutMs); }
            catch (Exception) { }
        }

        private string CurrentUid()
        {
            try { return _getUid().ToString(); }
            catch (Exception) { return "0"; }
        }

        // MAC-UNVERIFIED: assumes the "libc" name resolves to libSystem on macOS. Throws where it does not
        // (Windows, tests), which CurrentUid turns into uid 0; tests inject the uid instead.
        [DllImport("libc")]
        private static extern uint getuid();

        private static int DefaultUid()
        {
            return (int)getuid();
        }
    }
}
