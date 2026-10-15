using System;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text;
using CookieJar.Platform;

// Lets the Windows test project reach the internal task-name constructor and BuildCreateArguments.
[assembly: InternalsVisibleTo("CookieJar.Windows.Tests")]

namespace CookieJar.Platform.Windows
{
    /// <summary>
    /// "Start with Windows" as an ONLOGON Task Scheduler task (not HKCU Run), ported from src/Startup.cs.
    /// Runs "<exe>" --minibar elevated after a 20 second delay. Never throws.
    /// </summary>
    public sealed class WinStartupService : IStartupService
    {
        public const string DefaultTaskName = "RobloxMultiLauncher_MiniBar";
        private const string MiniBarFlag = "--minibar";
        private const int TimeoutMs = 15000;

        private readonly string _taskName;

        public WinStartupService() : this(DefaultTaskName)
        {
        }

        internal WinStartupService(string taskName)
        {
            _taskName = taskName;
        }

        public bool IsEnabled()
        {
            try
            {
                int code;
                string output;
                if (!RunSchtasks("/Query /TN " + Quote(_taskName), out code, out output)) return false;
                return code == 0;
            }
            catch (Exception)
            {
                return false;
            }
        }

        public bool SetEnabled(bool enabled)
        {
            try
            {
                return enabled ? Enable() : Disable();
            }
            catch (Exception)
            {
                return false;
            }
        }

        private bool Enable()
        {
            string path = CurrentExePath();
            if (string.IsNullOrEmpty(path) || !File.Exists(path)) return false;

            int code;
            string output;
            if (!RunSchtasks(BuildCreateArguments(path, _taskName), out code, out output)) return false;
            return code == 0;
        }

        private bool Disable()
        {
            int code;
            string output;
            if (!RunSchtasks("/Delete /TN " + Quote(_taskName) + " /F", out code, out output)) return false;
            if (code == 0) return true;
            // A missing task means start-with-Windows is already off.
            return !IsEnabled();
        }

        internal static string BuildCreateArguments(string exePath, string taskName)
        {
            // The /TR value must be: "<exePath>" --minibar
            // On the command line the inner quotes are escaped as \" so the
            // value stays one argument even when exePath contains spaces.
            string tr = "\\\"" + exePath + "\\\" " + MiniBarFlag;
            return "/Create /TN " + Quote(taskName)
                + " /SC ONLOGON /RL HIGHEST /F /DELAY 0000:20"
                + " /TR \"" + tr + "\"";
        }

        private static string CurrentExePath()
        {
            try
            {
                using (Process self = Process.GetCurrentProcess())
                {
                    string file = self.MainModule != null ? self.MainModule.FileName : null;
                    if (!string.IsNullOrEmpty(file)) return file;
                }
            }
            catch (Exception)
            {
                // Fall through to the entry assembly.
            }
            Assembly entry = Assembly.GetEntryAssembly();
            return entry != null ? entry.Location : null;
        }

        private static string Quote(string value)
        {
            return "\"" + (value ?? string.Empty) + "\"";
        }

        // Returns false only if the process could not be started or timed out.
        private static bool RunSchtasks(string arguments, out int exitCode, out string output)
        {
            exitCode = -1;
            output = string.Empty;

            ProcessStartInfo psi = new ProcessStartInfo();
            psi.FileName = "schtasks.exe";
            psi.Arguments = arguments;
            psi.UseShellExecute = false;
            psi.CreateNoWindow = true;
            psi.RedirectStandardOutput = true;
            psi.RedirectStandardError = true;

            StringBuilder sb = new StringBuilder();
            object sync = new object();

            using (Process p = new Process())
            {
                p.StartInfo = psi;
                p.OutputDataReceived += delegate(object s, DataReceivedEventArgs e)
                {
                    if (e.Data == null) return;
                    lock (sync) { sb.AppendLine(e.Data); }
                };
                p.ErrorDataReceived += delegate(object s, DataReceivedEventArgs e)
                {
                    if (e.Data == null) return;
                    lock (sync) { sb.AppendLine(e.Data); }
                };

                if (!p.Start()) return false;
                p.BeginOutputReadLine();
                p.BeginErrorReadLine();

                if (!p.WaitForExit(TimeoutMs))
                {
                    try { p.Kill(); } catch (Exception) { }
                    return false;
                }
                // Flush async readers after exit.
                p.WaitForExit();

                exitCode = p.ExitCode;
            }

            lock (sync) { output = sb.ToString().Trim(); }
            return true;
        }
    }
}
