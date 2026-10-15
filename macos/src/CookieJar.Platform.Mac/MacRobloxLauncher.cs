using System;
using System.Collections.Generic;
using System.Threading;

namespace CookieJar.Platform.Mac
{
    /// <summary>
    /// Starts Roblox on macOS with `open`. The new player pid is the one that was not running before `open` ran.
    /// Never throws; failures return Started = false with an Error.
    /// </summary>
    public sealed class MacRobloxLauncher : IRobloxLauncher
    {
        // MAC-UNVERIFIED: /usr/bin/open is the stock LaunchServices front end on macOS.
        private const string OpenPath = "/usr/bin/open";
        private const int OpenTimeoutMs = 10000;
        private const int DefaultPollMs = 500;

        private readonly ICommandRunner runner;
        private readonly IProcessService procs;
        private readonly MacRobloxLocator locator;
        private readonly Action<int> sleep;
        private readonly int pollMs;

        public MacRobloxLauncher(ICommandRunner runner, IProcessService procs, MacRobloxLocator locator)
            : this(runner, procs, locator, Thread.Sleep, DefaultPollMs) { }

        /// <summary>Full constructor. Tests pass a no-op sleep.</summary>
        public MacRobloxLauncher(ICommandRunner runner, IProcessService procs, MacRobloxLocator locator, Action<int> sleep, int pollMs)
        {
            this.runner = runner ?? throw new ArgumentNullException(nameof(runner));
            this.procs = procs ?? throw new ArgumentNullException(nameof(procs));
            this.locator = locator ?? throw new ArgumentNullException(nameof(locator));
            this.sleep = sleep ?? throw new ArgumentNullException(nameof(sleep));
            this.pollMs = Math.Max(0, pollMs);
        }

        /// <summary>Runs `open -n -a &lt;Roblox.app&gt; --args &lt;arguments&gt;`.</summary>
        public LaunchResult Launch(IReadOnlyList<string> arguments, int timeoutMs)
        {
            try
            {
                string app = locator.FindInstallRoot();
                if (app == null) return Fail("Roblox is not installed.");

                var args = new List<string> { "-n", "-a", app, "--args" };
                if (arguments != null)
                {
                    foreach (string argument in arguments) args.Add(argument ?? "");
                }
                return StartAndWait(args, timeoutMs);
            }
            catch (Exception ex)
            {
                return Fail("Launch failed: " + ex.Message);
            }
        }

        /// <summary>Runs `open -n &lt;url&gt;` for a roblox:// or roblox-player: URL.</summary>
        public LaunchResult LaunchUrl(string robloxUrl, int timeoutMs)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(robloxUrl)) return Fail("No URL given.");

                string url = robloxUrl.Trim();
                if (!IsRobloxScheme(url)) return Fail("Not a roblox:// URL: " + url);

                return StartAndWait(new List<string> { "-n", url }, timeoutMs);
            }
            catch (Exception ex)
            {
                return Fail("Launch failed: " + ex.Message);
            }
        }

        /// <summary>
        /// MAC-UNVERIFIED: `open -n` starts a fresh instance without closing any handle, so there is nothing to
        /// release. The proof of concept on a real Mac still has to confirm several clients really run side by side.
        /// </summary>
        public bool PrepareForAnotherInstance()
        {
            return true;
        }

        /// <summary>MAC-UNVERIFIED: no single-instance lock is held when `open -n` is used.</summary>
        public int ReleaseSingleInstanceLock(int pid)
        {
            return 0;
        }

        /// <summary>MAC-UNVERIFIED: no single-instance lock is held when `open -n` is used.</summary>
        public int CountSingleInstanceLocks(int pid)
        {
            return 0;
        }

        private static bool IsRobloxScheme(string url)
        {
            return url.StartsWith("roblox://", StringComparison.OrdinalIgnoreCase) ||
                   url.StartsWith("roblox-player:", StringComparison.OrdinalIgnoreCase);
        }

        private LaunchResult StartAndWait(IReadOnlyList<string> openArguments, int timeoutMs)
        {
            string name = locator.PlayerProcessName;
            var before = new HashSet<int>(procs.FindByName(name) ?? new List<int>());

            CommandResult open = runner.Run(OpenPath, openArguments, OpenTimeoutMs);
            if (open == null) return Fail("open did not run.");
            if (open.TimedOut) return Fail("open timed out.");
            if (open.FailedToStart) return Fail("open could not start: " + Describe(open));
            if (open.ExitCode != 0) return Fail("open failed (exit " + open.ExitCode + "): " + Describe(open));

            // Poll once per step; a zero step still gets one attempt per timeout unit.
            int step = Math.Max(1, pollMs);
            int attempts = Math.Max(1, timeoutMs / step);
            for (int i = 0; i < attempts; i++)
            {
                foreach (int pid in procs.FindByName(name) ?? new List<int>())
                {
                    if (!before.Contains(pid)) return new LaunchResult(true, pid, null);
                }
                if (i < attempts - 1) sleep(pollMs);
            }

            return Fail("No new " + name + " process appeared within " + timeoutMs + " ms.");
        }

        private static string Describe(CommandResult result)
        {
            string text = (result.StandardError ?? "").Trim();
            if (text.Length == 0) text = (result.StandardOutput ?? "").Trim();
            return text.Length == 0 ? "no output" : text;
        }

        private static LaunchResult Fail(string error)
        {
            return new LaunchResult(false, 0, error);
        }
    }
}
