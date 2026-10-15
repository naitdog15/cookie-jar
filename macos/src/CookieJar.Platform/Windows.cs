using System.Collections.Generic;

namespace CookieJar.Platform
{
    /// <summary>Integer rectangle in screen pixels (origin top-left of the primary display).</summary>
    public struct ScreenRect
    {
        public ScreenRect(int x, int y, int width, int height) { X = x; Y = y; Width = width; Height = height; }
        public int X { get; }
        public int Y { get; }
        public int Width { get; }
        public int Height { get; }
        public int Right { get { return X + Width; } }
        public int Bottom { get { return Y + Height; } }
    }

    /// <summary>A display. Bounds and WorkArea are in the same pixel space used by IWindowService and IInputService.</summary>
    public sealed class MonitorInfo
    {
        public MonitorInfo(int id, ScreenRect bounds, ScreenRect workArea, double scaleFactor, bool isPrimary)
        {
            Id = id; Bounds = bounds; WorkArea = workArea; ScaleFactor = scaleFactor; IsPrimary = isPrimary;
        }
        /// <summary>Opaque monitor id, stable while the layout is unchanged.</summary>
        public int Id { get; }
        public ScreenRect Bounds { get; }
        /// <summary>Bounds minus taskbar/menu bar/dock.</summary>
        public ScreenRect WorkArea { get; }
        /// <summary>Backing pixels per unit coordinate (1.0 = 100%, 2.0 = Retina).</summary>
        public double ScaleFactor { get; }
        public bool IsPrimary { get; }
    }

    /// <summary>
    /// Window control. Window ids are opaque numbers (long), valid only while the window exists; never an OS handle type.
    /// Thread-safe. Without WindowPlacement all mutators return false and queries return empty/false.
    /// </summary>
    public interface IWindowService
    {
        /// <summary>Top-level window ids owned by pid (visible or not).</summary>
        IReadOnlyList<long> GetWindowsOfProcess(int pid);
        /// <summary>The main window id of pid, or 0 if none.</summary>
        long GetMainWindow(int pid);
        /// <summary>True if the window still exists.</summary>
        bool Exists(long windowId);
        bool IsVisible(long windowId);
        bool IsMinimised(long windowId);
        /// <summary>Un-minimises the window (without necessarily focusing). False on failure.</summary>
        bool Restore(long windowId);
        bool Minimise(long windowId);
        /// <summary>Raises and focuses the window. False on failure or if the OS refused.</summary>
        bool BringToFront(long windowId);
        /// <summary>Window id with keyboard focus, or 0.</summary>
        long GetForegroundWindow();
        /// <summary>Outer frame in screen pixels. False if unknown.</summary>
        bool TryGetBounds(long windowId, out ScreenRect bounds);
        /// <summary>Content area (without title bar/borders) in screen pixels. False if unknown.</summary>
        bool TryGetContentBounds(long windowId, out ScreenRect bounds);
        /// <summary>Moves and resizes the outer frame in screen pixels. False on failure.</summary>
        bool SetBounds(long windowId, ScreenRect bounds);
        /// <summary>All displays. Empty if unsupported.</summary>
        IReadOnlyList<MonitorInfo> GetMonitors();
    }
}
