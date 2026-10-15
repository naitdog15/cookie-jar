// Startup.cs
//
// Public API (namespace RbxMulti, static class AutoStart)
// -------------------------------------------------------
// Optional "Start with Windows" support for the mini bar. Implemented with a
// Windows Task Scheduler task (NOT HKCU\...\Run). Opt-in only: nothing runs at
// class load time.
//
// const string TaskName = "RobloxMultiLauncher_MiniBar";
// const string MiniBarFlag = "--minibar";
//
// bool HasMiniBarFlag(string[] args)
//     True if any argument equals "--minibar" or "/minibar" (case-insensitive).
//     Null-safe: null args or null elements return false.
//
// bool IsEnabled()
// bool IsEnabled(string taskName)
//     Runs "schtasks /Query /TN <taskName>". Exit code 0 => true.
//     Any failure (missing schtasks, timeout, exception) => false.
//
// string Enable(string exePath, out bool ok)
// string Enable(string exePath, string taskName, out bool ok)
//     Creates an ONLOGON task (RL HIGHEST, 20 second delay) that runs
//     "<exePath>" --minibar. exePath null/empty uses the entry assembly location.
//     Fails with a message if the exe does not exist. Returns a human-readable
//     message; ok reports success. "Access is denied" yields a message asking
//     the user to run the launcher as administrator.
//
// string Disable(out bool ok)
// string Disable(string taskName, out bool ok)
//     Deletes the task ("schtasks /Delete /TN <name> /F"). If the task does not
//     exist, returns ok=true with "Start with Windows is already off."
//
// string BuildCreateArguments(string exePath, string taskName)
//     Pure helper. Returns the full schtasks argument string used by Enable.
//
// Process handling: schtasks.exe is run with UseShellExecute=false,
// CreateNoWindow=true, stdout/stderr redirected. Waits up to 15 seconds; the
// schtasks child process is killed only on timeout.

using System;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Text;

namespace RbxMulti
{
    public static class AutoStart
    {
        public const string TaskName = "RobloxMultiLauncher_MiniBar";
        public const string MiniBarFlag = "--minibar";

        private const int TimeoutMs = 15000;

        public static bool HasMiniBarFlag(string[] args)
        {
            if (args == null) return false;
            for (int i = 0; i < args.Length; i++)
            {
                string a = args[i];
                if (a == null) continue;
                if (string.Equals(a, MiniBarFlag, StringComparison.OrdinalIgnoreCase)) return true;
                if (string.Equals(a, "/minibar", StringComparison.OrdinalIgnoreCase)) return true;
            }
            return false;
        }

        public static bool IsEnabled()
        {
            return IsEnabled(TaskName);
        }

        public static bool IsEnabled(string taskName)
        {
            try
            {
                int code;
                string output;
                if (!RunSchtasks("/Query /TN " + Quote(taskName), out code, out output)) return false;
                return code == 0;
            }
            catch (Exception)
            {
                return false;
            }
        }

        public static string Enable(string exePath, out bool ok)
        {
            return Enable(exePath, TaskName, out ok);
        }

        public static string Enable(string exePath, string taskName, out bool ok)
        {
            ok = false;
            try
            {
                string path = exePath;
                if (string.IsNullOrEmpty(path))
                {
                    Assembly entry = Assembly.GetEntryAssembly();
                    path = entry != null ? entry.Location : null;
                }
                if (string.IsNullOrEmpty(path) || !File.Exists(path))
                {
                    return "Could not enable: executable not found" + (string.IsNullOrEmpty(path) ? "." : ": " + path);
                }

                int code;
                string output;
                if (!RunSchtasks(BuildCreateArguments(path, taskName), out code, out output))
                {
                    return "Could not enable: schtasks did not complete.";
                }
                if (code == 0)
                {
                    ok = true;
                    return "Start with Windows enabled (runs elevated at logon, mini bar only).";
                }
                if (output.IndexOf("Access is denied", StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    return "Run the launcher as administrator to change this.";
                }
                return "Could not enable: " + output;
            }
            catch (Exception ex)
            {
                return "Could not enable: " + ex.Message;
            }
        }

        public static string Disable(out bool ok)
        {
            return Disable(TaskName, out ok);
        }

        public static string Disable(string taskName, out bool ok)
        {
            ok = false;
            try
            {
                int code;
                string output;
                if (!RunSchtasks("/Delete /TN " + Quote(taskName) + " /F", out code, out output))
                {
                    return "Could not disable: schtasks did not complete.";
                }
                if (code == 0)
                {
                    ok = true;
                    return "Start with Windows disabled.";
                }

                // Task missing => already off.
                if (!IsEnabled(taskName))
                {
                    ok = true;
                    return "Start with Windows is already off.";
                }

                if (output.IndexOf("Access is denied", StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    return "Run the launcher as administrator to change this.";
                }
                return "Could not disable: " + output;
            }
            catch (Exception ex)
            {
                return "Could not disable: " + ex.Message;
            }
        }

        public static string BuildCreateArguments(string exePath, string taskName)
        {
            // The /TR value must be: "<exePath>" --minibar
            // On the command line the inner quotes are escaped as \" so the
            // value stays one argument even when exePath contains spaces.
            string tr = "\\\"" + exePath + "\\\" " + MiniBarFlag;
            return "/Create /TN " + Quote(taskName)
                + " /SC ONLOGON /RL HIGHEST /F /DELAY 0000:20"
                + " /TR \"" + tr + "\"";
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
