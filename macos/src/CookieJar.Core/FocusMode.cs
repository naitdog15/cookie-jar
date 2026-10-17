using System;
using System.Collections.Generic;
using CookieJar.Platform;

namespace CookieJar.Core
{
    /// <summary>
    /// Focus mode: one client fills its display and every other running client is held at background priority, so the
    /// focused one gets the CPU. Port of the Windows 1.2.0 Focus button, limited to what the platform contracts offer:
    /// fullscreen is the display bounds through IWindowService, and "more CPU" is slowing the others through
    /// BackgroundEfficiency.FocusPid (macOS has no processor affinity and cannot raise a process above normal).
    /// Stop puts the window back where it was and restores priorities. UI thread only (not thread safe).
    /// </summary>
    public sealed class FocusMode
    {
        private readonly IWindowService _windows;
        private readonly BackgroundEfficiency _efficiency;
        private int _instanceId;
        private long _windowId;
        private ScreenRect _before;
        private bool _hasBefore;

        /// <summary>Optional log sink (called on the UI thread).</summary>
        public Action<string> Log;

        /// <summary>Raised after Focus starts, moves to another client, or ends.</summary>
        public event Action Changed;

        public FocusMode(IWindowService windows, BackgroundEfficiency efficiency)
        {
            if (windows == null) throw new ArgumentNullException(nameof(windows));
            if (efficiency == null) throw new ArgumentNullException(nameof(efficiency));
            _windows = windows;
            _efficiency = efficiency;
        }

        public bool Active { get { return _instanceId != 0; } }

        public bool IsFocused(int instanceId)
        {
            return _instanceId != 0 && _instanceId == instanceId;
        }

        /// <summary>
        /// Starts Focus on target (or moves it from the previous client). False when target has no process. A window that
        /// cannot be found or moved is logged, but the CPU side still applies.
        /// </summary>
        public bool Start(RobloxInstance target, IList<RobloxInstance> all)
        {
            if (target == null || target.Pid <= 0 || target.StatusKind == "Stopped") return false;
            if (_instanceId == target.Id) return true;
            RestoreWindow();

            _instanceId = target.Id;
            _efficiency.Log = Log;
            _efficiency.FocusPid = target.Pid;
            _efficiency.Update(Targets(all), DateTime.UtcNow);

            _windowId = 0;
            _hasBefore = false;
            try
            {
                long id = _windows.GetMainWindow(target.Pid);
                ScreenRect now;
                if (id != 0 && _windows.TryGetBounds(id, out now))
                {
                    MonitorInfo monitor = MonitorOf(now);
                    if (monitor != null && _windows.SetBounds(id, monitor.Bounds))
                    {
                        _windowId = id;
                        _before = now;
                        _hasBefore = true;
                        _windows.BringToFront(id);
                    }
                    else if (Log != null) Log("Focus could not make " + target.Name + " fullscreen (the OS refused to move the window).");
                }
                else if (Log != null) Log("Focus found no window for " + target.Name + " yet.");
            }
            catch (Exception ex)
            {
                if (Log != null) Log("Focus fullscreen failed: " + ex.Message);
            }
            RaiseChanged();
            return true;
        }

        /// <summary>Ends Focus: the window returns to where it was and the other clients are restored. Safe when inactive.</summary>
        public void Stop(IList<RobloxInstance> all)
        {
            if (_instanceId == 0) return;
            RestoreWindow();
            _instanceId = 0;
            _efficiency.FocusPid = 0;
            _efficiency.Update(Targets(all), DateTime.UtcNow);
            RaiseChanged();
        }

        private void RestoreWindow()
        {
            try
            {
                if (_hasBefore && _windowId != 0 && _windows.Exists(_windowId)) _windows.SetBounds(_windowId, _before);
            }
            catch (Exception)
            {
            }
            _hasBefore = false;
            _windowId = 0;
        }

        // The display holding the centre of the window; the first display when none does.
        private MonitorInfo MonitorOf(ScreenRect r)
        {
            IReadOnlyList<MonitorInfo> monitors = _windows.GetMonitors();
            if (monitors == null || monitors.Count == 0) return null;
            int cx = r.X + r.Width / 2;
            int cy = r.Y + r.Height / 2;
            for (int i = 0; i < monitors.Count; i++)
            {
                ScreenRect b = monitors[i].Bounds;
                if (cx >= b.X && cx < b.Right && cy >= b.Y && cy < b.Bottom) return monitors[i];
            }
            return monitors[0];
        }

        private static List<EfficiencyTarget> Targets(IList<RobloxInstance> all)
        {
            List<EfficiencyTarget> list = new List<EfficiencyTarget>();
            if (all == null) return list;
            for (int i = 0; i < all.Count; i++)
            {
                EfficiencyTarget t = new EfficiencyTarget();
                t.Pid = all[i].Pid;
                t.Running = all[i].StatusKind == "Running";
                t.StartedUtc = all[i].StartedUtc;
                list.Add(t);
            }
            return list;
        }

        private void RaiseChanged()
        {
            Action h = Changed;
            if (h != null) h();
        }
    }
}
