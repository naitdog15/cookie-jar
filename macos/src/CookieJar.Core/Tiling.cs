using System;
using System.Collections.Generic;

namespace CookieJar.Core
{
    /// <summary>A display as seen by the tiling maths. Port of RbxMulti.MonitorInfo (work area only).</summary>
    public class TileMonitor
    {
        public int Index;
        public bool IsPrimary;
        public int X, Y, Width, Height;
        public double DpiScale;

        /// <summary>Full display rectangle (menu bar and Dock included). FullWidth 0 means unknown.</summary>
        public int FullX, FullY, FullWidth, FullHeight;

        /// <summary>
        /// Converts a platform display. Index is the position in the monitor list (0 = primary, then left to right),
        /// which the platform does not know, so the caller passes it.
        /// </summary>
        public static TileMonitor FromPlatform(CookieJar.Platform.MonitorInfo monitor, int index)
        {
            if (monitor == null) throw new ArgumentNullException(nameof(monitor));
            TileMonitor m = new TileMonitor();
            m.Index = index;
            m.IsPrimary = monitor.IsPrimary;
            m.X = monitor.WorkArea.X;
            m.Y = monitor.WorkArea.Y;
            m.Width = monitor.WorkArea.Width;
            m.Height = monitor.WorkArea.Height;
            m.DpiScale = monitor.ScaleFactor;
            m.FullX = monitor.Bounds.X;
            m.FullY = monitor.Bounds.Y;
            m.FullWidth = monitor.Bounds.Width;
            m.FullHeight = monitor.Bounds.Height;
            return m;
        }
    }

    /// <summary>A window cell in screen pixels, already inside the work area.</summary>
    public class TileCell
    {
        public int X, Y, Width, Height;
        public int MonitorIndex;
    }

    /// <summary>A window rectangle in screen pixels with the monitor it belongs to.</summary>
    public class WindowLayout
    {
        public int MonitorIndex, X, Y, Width, Height;
    }

    /// <summary>Pure tiling maths, ported from WindowManager.ComputeTiling and ComputeTilingAcrossMonitors.</summary>
    public static class Tiling
    {
        /// <summary>Primary first, then left to right, then top to bottom. Same order as the Windows monitor list.</summary>
        public static int CompareMonitors(TileMonitor a, TileMonitor b)
        {
            if (a.IsPrimary != b.IsPrimary) return a.IsPrimary ? -1 : 1;
            if (a.X != b.X) return a.X.CompareTo(b.X);
            return a.Y.CompareTo(b.Y);
        }

        /// <summary>
        /// Count is clamped to 1..32. Tries every column count and keeps the one whose cells hold the largest
        /// 16:9 window. Ties go to fewer rows. Returns exactly count cells, or none for a null or empty monitor.
        /// </summary>
        public static List<TileCell> ComputeTiling(int count, TileMonitor monitor)
        {
            List<TileCell> cells = new List<TileCell>();
            try
            {
                if (monitor == null) return cells;
                if (count < 1) count = 1;
                if (count > 32) count = 32;

                int workW = monitor.Width;
                int workH = monitor.Height;
                if (workW <= 0 || workH <= 0) return cells;

                int bestCols = 1;
                int bestRows = count;
                double bestScore = -1.0;
                const double eps = 1e-9;

                for (int cols = 1; cols <= count; cols++)
                {
                    int rows = (count + cols - 1) / cols;
                    double w = (double)workW / cols;
                    double h = (double)workH / rows;
                    double fitW = Math.Min(w, h * 16.0 / 9.0);
                    double score = fitW * (fitW * 9.0 / 16.0);
                    if (score > bestScore + eps || (Math.Abs(score - bestScore) <= eps && rows < bestRows))
                    {
                        bestScore = score;
                        bestCols = cols;
                        bestRows = rows;
                    }
                }

                if (count == 1)
                {
                    // A lone window fills the whole display, like the Windows 1.2.0 change, so one session can be fullscreen.
                    // MAC-UNVERIFIED: macOS may keep a window below the menu bar or clamp it through Accessibility.
                    bool known = monitor.FullWidth > 0 && monitor.FullHeight > 0;
                    TileCell full = new TileCell();
                    full.X = known ? monitor.FullX : monitor.X;
                    full.Y = known ? monitor.FullY : monitor.Y;
                    full.Width = known ? monitor.FullWidth : workW;
                    full.Height = known ? monitor.FullHeight : workH;
                    full.MonitorIndex = monitor.Index;
                    cells.Add(full);
                    return cells;
                }

                int colsUsed = bestCols;
                int rowsUsed = bestRows;
                for (int r = 0; r < rowsUsed; r++)
                {
                    int inRow = Math.Min(colsUsed, count - r * colsUsed);
                    int offset = (colsUsed - inRow) / 2;
                    int top = monitor.Y + (r * workH) / rowsUsed;
                    int bottom = monitor.Y + ((r + 1) * workH) / rowsUsed;

                    for (int k = 0; k < inRow; k++)
                    {
                        int left = monitor.X + ((k + offset) * workW) / colsUsed;
                        int right = monitor.X + ((k + offset + 1) * workW) / colsUsed;

                        TileCell cell = new TileCell();
                        cell.X = left + 1;
                        cell.Y = top + 1;
                        cell.Width = Math.Max(1, (right - left) - 2);
                        cell.Height = Math.Max(1, (bottom - top) - 2);
                        cell.MonitorIndex = monitor.Index;
                        cells.Add(cell);
                    }
                }
            }
            catch (Exception)
            {
            }
            return cells;
        }

        /// <summary>
        /// Count is clamped to 1..32. With one monitor, or a count of 1, tiles the primary monitor (or the first
        /// one when none is primary). Otherwise shares count out in proportion to work-area size, gives the
        /// remainder to the largest monitors, and returns cells in monitor list order.
        /// A null or empty list returns no cells (the Windows code enumerates monitors here; Core cannot).
        /// </summary>
        public static List<TileCell> ComputeTilingAcrossMonitors(int count, List<TileMonitor> monitors)
        {
            List<TileCell> result = new List<TileCell>();
            try
            {
                if (count < 1) count = 1;
                if (count > 32) count = 32;
                if (monitors == null || monitors.Count == 0) return result;

                TileMonitor primary = null;
                for (int i = 0; i < monitors.Count; i++)
                {
                    if (monitors[i].IsPrimary) { primary = monitors[i]; break; }
                }
                if (primary == null) primary = monitors[0];

                if (count <= 1 || monitors.Count == 1)
                {
                    return ComputeTiling(count, primary);
                }

                int n = monitors.Count;
                long[] areas = new long[n];
                long total = 0;
                for (int i = 0; i < n; i++)
                {
                    long a = (long)Math.Max(0, monitors[i].Width) * Math.Max(0, monitors[i].Height);
                    areas[i] = a;
                    total += a;
                }

                int[] counts = new int[n];
                int assigned = 0;
                if (total > 0)
                {
                    for (int i = 0; i < n; i++)
                    {
                        counts[i] = (int)((long)count * areas[i] / total);
                        assigned += counts[i];
                    }
                }

                // Largest first; equal areas keep list order so the remainder is handed out deterministically.
                List<int> order = new List<int>();
                for (int i = 0; i < n; i++) order.Add(i);
                order.Sort(delegate(int a, int b)
                {
                    int byArea = areas[b].CompareTo(areas[a]);
                    return byArea != 0 ? byArea : a.CompareTo(b);
                });

                int idx = 0;
                while (assigned < count)
                {
                    counts[order[idx % n]]++;
                    assigned++;
                    idx++;
                }

                for (int i = 0; i < n; i++)
                {
                    if (counts[i] <= 0) continue;
                    result.AddRange(ComputeTiling(counts[i], monitors[i]));
                }
            }
            catch (Exception)
            {
            }
            return result;
        }
    }
}
