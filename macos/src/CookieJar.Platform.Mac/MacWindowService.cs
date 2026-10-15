using System;
using System.Collections.Generic;

namespace CookieJar.Platform.Mac
{
    /// <summary>Source of raw display text (see MacWindowParsing.ScreensScript). Returns null when unavailable.</summary>
    public interface IMacScreenSource
    {
        string ReadScreens();
    }

    /// <summary>Default display source: runs the JXA script through osascript.</summary>
    public sealed class MacOsascriptScreenSource : IMacScreenSource
    {
        private readonly ICommandRunner _runner;

        public MacOsascriptScreenSource(ICommandRunner runner)
        {
            if (runner == null) throw new ArgumentNullException("runner");
            _runner = runner;
        }

        public string ReadScreens()
        {
            // MAC-UNVERIFIED: `osascript -l JavaScript` with ObjC.import needs no Automation consent, unlike System Events.
            CommandResult r = _runner.Run("osascript",
                new List<string> { "-l", "JavaScript", "-e", MacWindowParsing.ScreensScript }, MacWindowService.ScriptTimeoutMs);
            return r.Success ? r.StandardOutput : null;
        }
    }

    /// <summary>
    /// Window control and display layout on macOS, through System Events and NSScreen via osascript.
    /// Window ids come from MacWindowParsing.EncodeWindowId. Never throws: failures return false, empty or 0.
    /// Needs Automation permission for System Events and Accessibility for the host app.
    /// </summary>
    public sealed class MacWindowService : IWindowService
    {
        // MAC-UNVERIFIED: System Events can block on an Automation consent prompt; the timeout only stops a hang.
        internal const int ScriptTimeoutMs = 10000;

        private readonly ICommandRunner _runner;
        private readonly Func<int, IReadOnlyList<int>> _pidsProvider;
        private readonly IMacScreenSource _screens;

        /// <param name="pidsProvider">Optional: extra pids whose windows count as belonging to a pid (e.g. helper processes).</param>
        /// <param name="screens">Optional display source; defaults to osascript.</param>
        public MacWindowService(ICommandRunner runner, Func<int, IReadOnlyList<int>> pidsProvider = null, IMacScreenSource screens = null)
        {
            if (runner == null) throw new ArgumentNullException("runner");
            _runner = runner;
            _pidsProvider = pidsProvider;
            _screens = screens ?? new MacOsascriptScreenSource(runner);
        }

        public IReadOnlyList<long> GetWindowsOfProcess(int pid)
        {
            var ids = new List<long>();
            try
            {
                foreach (int candidate in CandidatePids(pid))
                {
                    foreach (MacWindowRow row in ReadWindows(candidate))
                    {
                        long id = MacWindowParsing.EncodeWindowId(candidate, row.Index);
                        if (id != 0) ids.Add(id);
                    }
                }
            }
            catch (Exception) { }
            return ids;
        }

        public long GetMainWindow(int pid)
        {
            try
            {
                List<MacWindowRow> rows = ReadWindows(pid);
                MacWindowRow pick = null;
                foreach (MacWindowRow row in rows)
                {
                    if (row.Index == 1 && !row.Minimised) { pick = row; break; }
                }
                if (pick == null && rows.Count > 0) pick = rows[0];
                return pick == null ? 0 : MacWindowParsing.EncodeWindowId(pid, pick.Index);
            }
            catch (Exception)
            {
                return 0;
            }
        }

        public bool Exists(long windowId)
        {
            return FindRow(windowId) != null;
        }

        public bool IsVisible(long windowId)
        {
            MacWindowRow row = FindRow(windowId);
            return row != null && row.Visible;
        }

        public bool IsMinimised(long windowId)
        {
            MacWindowRow row = FindRow(windowId);
            return row != null && row.Minimised;
        }

        public bool Restore(long windowId)
        {
            return SetMinimised(windowId, false);
        }

        public bool Minimise(long windowId)
        {
            return SetMinimised(windowId, true);
        }

        public bool BringToFront(long windowId)
        {
            try
            {
                int pid, index;
                if (!MacWindowParsing.DecodeWindowId(windowId, out pid, out index)) return false;
                return RunAppleScript(MacWindowParsing.RaiseScript(pid, index)).Success;
            }
            catch (Exception)
            {
                return false;
            }
        }

        public long GetForegroundWindow()
        {
            try
            {
                CommandResult r = RunAppleScript(MacWindowParsing.FrontmostProcessScript());
                if (!r.Success) return 0;
                int pid = MacWindowParsing.ParsePid(r.StandardOutput);
                return pid == 0 ? 0 : MacWindowParsing.EncodeWindowId(pid, 1);
            }
            catch (Exception)
            {
                return 0;
            }
        }

        public bool TryGetBounds(long windowId, out ScreenRect bounds)
        {
            bounds = default(ScreenRect);
            try
            {
                MacWindowRow row = FindRow(windowId);
                if (row == null) return false;
                bounds = row.Bounds;
                return true;
            }
            catch (Exception)
            {
                return false;
            }
        }

        public bool TryGetContentBounds(long windowId, out ScreenRect bounds)
        {
            bounds = default(ScreenRect);
            ScreenRect frame;
            return TryGetBounds(windowId, out frame) && MacWindowParsing.ToContentBounds(frame, out bounds);
        }

        public bool SetBounds(long windowId, ScreenRect bounds)
        {
            try
            {
                int pid, index;
                if (!MacWindowParsing.DecodeWindowId(windowId, out pid, out index)) return false;
                return RunAppleScript(MacWindowParsing.SetBoundsScript(pid, index, bounds)).Success;
            }
            catch (Exception)
            {
                return false;
            }
        }

        public IReadOnlyList<MonitorInfo> GetMonitors()
        {
            try
            {
                string text = _screens.ReadScreens();
                if (text == null) return new MonitorInfo[0];
                return MacWindowParsing.ToMonitors(MacWindowParsing.ParseScreenRows(text));
            }
            catch (Exception)
            {
                return new MonitorInfo[0];
            }
        }

        private bool SetMinimised(long windowId, bool minimised)
        {
            try
            {
                int pid, index;
                if (!MacWindowParsing.DecodeWindowId(windowId, out pid, out index)) return false;
                return RunAppleScript(MacWindowParsing.SetMinimisedScript(pid, index, minimised)).Success;
            }
            catch (Exception)
            {
                return false;
            }
        }

        private MacWindowRow FindRow(long windowId)
        {
            try
            {
                int pid, index;
                if (!MacWindowParsing.DecodeWindowId(windowId, out pid, out index)) return null;
                foreach (MacWindowRow row in ReadWindows(pid))
                {
                    if (row.Index == index) return row;
                }
                return null;
            }
            catch (Exception)
            {
                return null;
            }
        }

        private List<MacWindowRow> ReadWindows(int pid)
        {
            var rows = new List<MacWindowRow>();
            if (pid <= 0) return rows;
            CommandResult r = RunAppleScript(MacWindowParsing.WindowsScript(pid));
            if (!r.Success) return rows;
            return MacWindowParsing.ParseWindowRows(r.StandardOutput);
        }

        private List<int> CandidatePids(int pid)
        {
            var pids = new List<int>();
            if (pid <= 0) return pids;
            pids.Add(pid);
            if (_pidsProvider == null) return pids;
            IReadOnlyList<int> extra = _pidsProvider(pid);
            if (extra == null) return pids;
            foreach (int p in extra)
            {
                if (p > 0 && !pids.Contains(p)) pids.Add(p);
            }
            return pids;
        }

        private CommandResult RunAppleScript(string script)
        {
            return _runner.Run("osascript", new List<string> { "-e", script }, ScriptTimeoutMs);
        }
    }
}
