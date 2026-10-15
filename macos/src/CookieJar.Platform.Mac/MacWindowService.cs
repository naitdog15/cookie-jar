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
    /// Window control and display layout on macOS. Window ids are CGWindowIDs (see MacWindowParsing): stable while the
    /// window exists and the same number `screencapture -l` takes. State is read with CGWindowListCopyWindowInfo through
    /// `osascript -l JavaScript`; raise, minimise and move go through System Events, which finds the window by process
    /// and current position. Never throws: failures return false, empty or 0.
    /// Needs Automation permission for System Events (actions only) and Accessibility for the host app.
    /// </summary>
    public sealed class MacWindowService : IWindowService, IForegroundProcessSource
    {
        // MAC-UNVERIFIED: System Events can block on an Automation consent prompt; the timeout only stops a hang.
        internal const int ScriptTimeoutMs = 10000;

        private readonly ICommandRunner _runner;
        private readonly Func<int, IReadOnlyList<int>> _pidsProvider;
        private readonly IMacScreenSource _screens;
        private readonly object _sync = new object();
        private readonly Dictionary<long, int> _pidOf = new Dictionary<long, int>();   // window id -> owner pid seen last

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
                        if (!row.IsAppWindow) continue;
                        Remember(row);
                        ids.Add(row.Id);
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
                // CGWindowList is ordered front to back, so the first on-screen window is the frontmost one.
                MacWindowRow first = null, pick = null;
                foreach (MacWindowRow row in ReadWindows(pid))
                {
                    if (!row.IsAppWindow) continue;
                    if (first == null) first = row;
                    if (pick == null && row.OnScreen) pick = row;
                }
                if (pick == null) pick = first;
                if (pick == null) return 0;
                Remember(pick);
                return pick.Id;
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
            return row != null && row.OnScreen;
        }

        // MAC-UNVERIFIED: CGWindowList cannot tell "minimised" from "on another Space"; both read as not on screen.
        public bool IsMinimised(long windowId)
        {
            MacWindowRow row = FindRow(windowId);
            return row != null && !row.OnScreen;
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
                MacWindowRow row = FindRow(windowId);
                if (row == null) return false;
                return RunAppleScript(MacWindowParsing.RaiseScript(row.Pid, row.Bounds)).Success;
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
                int pid = GetForegroundProcessId();
                return pid == 0 ? 0 : GetMainWindow(pid);
            }
            catch (Exception)
            {
                return 0;
            }
        }

        /// <summary>Process id of the frontmost application, or 0 when unknown.</summary>
        public int GetForegroundProcessId()
        {
            try
            {
                CommandResult r = RunAppleScript(MacWindowParsing.FrontmostProcessScript());
                return r.Success ? MacWindowParsing.ParsePid(r.StandardOutput) : 0;
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
            if (!TryGetBounds(windowId, out frame)) return false;
            return MacWindowParsing.ToContentBounds(frame, GetMonitors(), out bounds);
        }

        public bool SetBounds(long windowId, ScreenRect bounds)
        {
            try
            {
                MacWindowRow row = FindRow(windowId);
                if (row == null) return false;
                return RunAppleScript(MacWindowParsing.SetBoundsScript(row.Pid, row.Bounds, bounds)).Success;
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
                MacWindowRow row = FindRow(windowId);
                if (row == null) return false;
                return RunAppleScript(MacWindowParsing.SetMinimisedScript(row.Pid, row.Bounds, minimised)).Success;
            }
            catch (Exception)
            {
                return false;
            }
        }

        /// <summary>The current row of a window, or null when it no longer exists. Looks in the owner's windows first.</summary>
        private MacWindowRow FindRow(long windowId)
        {
            try
            {
                if (!MacWindowParsing.IsValidWindowId(windowId)) return null;
                int pid;
                lock (_sync) { if (!_pidOf.TryGetValue(windowId, out pid)) pid = 0; }
                if (pid > 0)
                {
                    MacWindowRow known = Pick(ReadWindows(pid), windowId);
                    if (known != null) return known;
                }
                MacWindowRow any = Pick(ReadWindows(0), windowId);   // not seen yet (or moved owner): scan every process
                if (any == null) Forget(windowId);
                return any;
            }
            catch (Exception)
            {
                return null;
            }
        }

        private MacWindowRow Pick(List<MacWindowRow> rows, long windowId)
        {
            foreach (MacWindowRow row in rows)
            {
                if (row.Id == windowId) { Remember(row); return row; }
            }
            return null;
        }

        private void Remember(MacWindowRow row)
        {
            lock (_sync) { _pidOf[row.Id] = row.Pid; }
        }

        private void Forget(long windowId)
        {
            lock (_sync) { _pidOf.Remove(windowId); }
        }

        /// <summary>Windows of one process; pid 0 lists every process; a negative pid lists nothing.</summary>
        private List<MacWindowRow> ReadWindows(int pid)
        {
            var rows = new List<MacWindowRow>();
            if (pid < 0) return rows;
            CommandResult r = _runner.Run("osascript",
                new List<string> { "-l", "JavaScript", "-e", MacWindowParsing.WindowsScript(pid) }, ScriptTimeoutMs);
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
