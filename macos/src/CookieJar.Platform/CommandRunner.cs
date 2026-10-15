using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Text;

namespace CookieJar.Platform
{
    /// <summary>Outcome of running an external command.</summary>
    public sealed class CommandResult
    {
        public CommandResult(int exitCode, string standardOutput, string standardError, bool timedOut, bool failedToStart)
        {
            ExitCode = exitCode; StandardOutput = standardOutput ?? ""; StandardError = standardError ?? "";
            TimedOut = timedOut; FailedToStart = failedToStart;
        }
        public int ExitCode { get; }
        public string StandardOutput { get; }
        public string StandardError { get; }
        public bool TimedOut { get; }
        /// <summary>True when the executable could not be started (exit code is -1).</summary>
        public bool FailedToStart { get; }
        public bool Success { get { return !TimedOut && !FailedToStart && ExitCode == 0; } }
    }

    /// <summary>Runs an executable directly (no shell). Inject a fake in tests to supply canned output. Never throws for run failures.</summary>
    public interface ICommandRunner
    {
        /// <summary>Runs the command, waiting up to timeoutMs (0 or less = no limit).</summary>
        CommandResult Run(string fileName, IReadOnlyList<string> arguments, int timeoutMs);
    }

    /// <summary>Default runner based on System.Diagnostics.Process. Kills the process on timeout.</summary>
    /// <summary>
    /// A runner that can also feed text to the child's standard input. Used when a secret must never appear on a
    /// command line (anyone on the machine can read argv with ps). Added alongside ICommandRunner so existing fakes
    /// keep compiling; a caller that needs stdin and gets a runner without it must refuse, never fall back to argv.
    /// </summary>
    public interface IStdinCommandRunner : ICommandRunner
    {
        /// <summary>Like ICommandRunner.Run, with standardInput written to the child's stdin and then closed.</summary>
        CommandResult Run(string fileName, IReadOnlyList<string> arguments, string standardInput, int timeoutMs);
    }

    public sealed class ProcessCommandRunner : IStdinCommandRunner
    {
        public CommandResult Run(string fileName, IReadOnlyList<string> arguments, int timeoutMs)
        {
            return Run(fileName, arguments, null, timeoutMs);
        }

        public CommandResult Run(string fileName, IReadOnlyList<string> arguments, string standardInput, int timeoutMs)
        {
            var psi = new ProcessStartInfo
            {
                FileName = fileName,
                UseShellExecute = false,
                RedirectStandardInput = standardInput != null,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
                Arguments = JoinArguments(arguments)
            };
            var so = new StringBuilder();
            var se = new StringBuilder();
            try
            {
                using (var p = new Process { StartInfo = psi })
                {
                    p.OutputDataReceived += (s, e) => { if (e.Data != null) lock (so) so.AppendLine(e.Data); };
                    p.ErrorDataReceived += (s, e) => { if (e.Data != null) lock (se) se.AppendLine(e.Data); };
                    p.Start();
                    p.BeginOutputReadLine();
                    p.BeginErrorReadLine();
                    if (standardInput != null)
                    {
                        // Written on another thread, after output draining has started: a child that does not read its
                        // stdin (or a big input) can no longer block this call, and the wait below still enforces the timeout.
                        string input = standardInput;
                        System.Threading.Tasks.Task.Run(() =>
                        {
                            try { p.StandardInput.Write(input); p.StandardInput.Close(); }
                            catch (Exception) { /* the child may have exited already; its exit code tells the story */ }
                        });
                    }
                    bool done;
                    if (timeoutMs <= 0) { p.WaitForExit(); done = true; }
                    else done = p.WaitForExit(timeoutMs);
                    if (!done)
                    {
                        try { p.Kill(); } catch { }
                        try { p.WaitForExit(2000); } catch { }
                        return new CommandResult(-1, Text(so), Text(se), true, false);
                    }
                    p.WaitForExit(); // flush async readers
                    return new CommandResult(p.ExitCode, Text(so), Text(se), false, false);
                }
            }
            catch (Exception ex)
            {
                return new CommandResult(-1, "", ex.Message, false, true);
            }
        }

        static string Text(StringBuilder sb) { lock (sb) return sb.ToString(); }

        /// <summary>Quotes each argument using the .NET command-line rules (same on Windows and macOS).</summary>
        public static string JoinArguments(IReadOnlyList<string> args)
        {
            if (args == null) return "";
            var sb = new StringBuilder();
            for (int i = 0; i < args.Count; i++)
            {
                if (i > 0) sb.Append(' ');
                string a = args[i] ?? "";
                if (a.Length > 0 && a.IndexOfAny(new[] { ' ', '\t', '"', '\n' }) < 0) { sb.Append(a); continue; }
                sb.Append('"');
                int bs = 0;
                foreach (char c in a)
                {
                    if (c == '\\') { bs++; continue; }
                    if (c == '"') { sb.Append('\\', bs * 2 + 1); sb.Append('"'); }
                    else { sb.Append('\\', bs); sb.Append(c); }
                    bs = 0;
                }
                sb.Append('\\', bs * 2);
                sb.Append('"');
            }
            return sb.ToString();
        }
    }
}
