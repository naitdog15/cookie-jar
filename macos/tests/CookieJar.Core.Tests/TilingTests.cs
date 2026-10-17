using System;
using System.Collections.Generic;
using System.Text;
using CookieJar.Core;
using CookieJar.Platform;

namespace CookieJar.Tests
{
    // Reference strings are "monitorIndex:x,y,width,height" per cell, in the order the Windows code returns them.
    // They were produced by running the unchanged WindowManager.ComputeTiling / ComputeTilingAcrossMonitors
    // code from src/WindowManager.cs on the monitor layouts used in each test.
    public static class TilingTests
    {
        // Primary 1920x1080 at 0,0 (index 0).
        private static readonly TileMonitor A = Mon(0, true, 0, 0, 1920, 1080, 1.0);
        // Second 2560x1440 at 1920,0 (index 1), not primary.
        private static readonly TileMonitor B = Mon(1, false, 1920, 0, 2560, 1440, 1.0);
        // Third 1280x1024 at -1280,0 (index 2), left of the primary.
        private static readonly TileMonitor C = Mon(2, false, -1280, 0, 1280, 1024, 1.0);

        private static TileMonitor Mon(int index, bool primary, int x, int y, int w, int h, double scale)
        {
            TileMonitor m = new TileMonitor();
            m.Index = index; m.IsPrimary = primary; m.X = x; m.Y = y; m.Width = w; m.Height = h; m.DpiScale = scale;
            return m;
        }

        private static string Describe(List<TileCell> cells)
        {
            StringBuilder sb = new StringBuilder();
            for (int i = 0; i < cells.Count; i++)
            {
                if (i > 0) sb.Append(' ');
                TileCell c = cells[i];
                sb.Append(c.MonitorIndex + ":" + c.X + "," + c.Y + "," + c.Width + "," + c.Height);
            }
            return sb.ToString();
        }

        private static void CheckTable(string label, string[] expected, Func<int, List<TileCell>> tile)
        {
            for (int n = 1; n <= expected.Length; n++)
            {
                string got = Describe(tile(n));
                T.Check(label + " count " + n + " matches Windows", got == expected[n - 1], "want " + expected[n - 1] + " got " + got);
            }
        }

        private static bool Inside(TileCell c, TileMonitor m)
        {
            return c.X >= m.X && c.Y >= m.Y && c.X + c.Width <= m.X + m.Width && c.Y + c.Height <= m.Y + m.Height;
        }

        private static bool Overlaps(TileCell a, TileCell b)
        {
            return a.X < b.X + b.Width && b.X < a.X + a.Width && a.Y < b.Y + b.Height && b.Y < a.Y + a.Height;
        }

        public static void Run()
        {
            CheckTable("1920x1080", SINGLE_A, n => Tiling.ComputeTiling(n, A));
            CheckTable("2560x1440 second monitor", SINGLE_B, n => Tiling.ComputeTiling(n, B));
            CheckTable("across 1920x1080 + 2560x1440", ACROSS_AB, n => Tiling.ComputeTilingAcrossMonitors(n, new List<TileMonitor> { A, B }));
            CheckTable("across list order 2560x1440 first", ACROSS_BA_order, n => Tiling.ComputeTilingAcrossMonitors(n, new List<TileMonitor> { B, A }));
            CheckTable("across three monitors", ACROSS_ABC, n => Tiling.ComputeTilingAcrossMonitors(n, new List<TileMonitor> { A, B, C }));
            CheckTable("portrait 1080x1920", PORTRAIT, n => Tiling.ComputeTiling(n, Mon(0, true, 0, 0, 1080, 1920, 1.0)));
            CheckTable("1x1 monitor", TINY, n => Tiling.ComputeTiling(n, Mon(0, true, 0, 0, 1, 1, 1.0)));

            List<TileMonitor> mixed = new List<TileMonitor> { Mon(0, true, 0, 0, 1920, 1080, 1.5), Mon(1, false, 1920, 0, 1920, 1080, 1.0) };
            string mixedGot = Describe(Tiling.ComputeTilingAcrossMonitors(4, mixed));
            T.Check("mixed scales: DPI does not change the split", mixedGot == "0:1,1,958,1078 0:961,1,958,1078 1:1921,1,958,1078 1:2881,1,958,1078", mixedGot);

            T.Check("zero-size monitor gives no cells", Tiling.ComputeTiling(3, Mon(0, true, 0, 0, 0, 0, 1.0)).Count == 0, "");
            T.Check("all monitors zero-size gives no cells across", Tiling.ComputeTilingAcrossMonitors(3, new List<TileMonitor> { Mon(0, true, 0, 0, 0, 0, 1.0), Mon(1, false, 0, 0, 0, 0, 1.0) }).Count == 0, "");
            T.Check("null monitor gives no cells", Tiling.ComputeTiling(3, null).Count == 0, "");
            T.Check("null monitor list gives no cells", Tiling.ComputeTilingAcrossMonitors(3, null).Count == 0, "");
            T.Check("empty monitor list gives no cells", Tiling.ComputeTilingAcrossMonitors(3, new List<TileMonitor>()).Count == 0, "");

            T.Check("count 0 is clamped to 1", Describe(Tiling.ComputeTiling(0, A)) == "0:0,0,1920,1080", Describe(Tiling.ComputeTiling(0, A)));
            T.Check("count 100 is clamped to 32", Tiling.ComputeTiling(100, A).Count == 32, "count=" + Tiling.ComputeTiling(100, A).Count);
            T.Check("across count 40 with three monitors is clamped to 32", Tiling.ComputeTilingAcrossMonitors(40, new List<TileMonitor> { A, B, C }).Count == 32, "");
            T.Check("across count 0 tiles the primary only", Describe(Tiling.ComputeTilingAcrossMonitors(0, new List<TileMonitor> { B, A })) == "0:0,0,1920,1080", "");
            T.Check("across with no primary tiles the first monitor", Describe(Tiling.ComputeTilingAcrossMonitors(1, new List<TileMonitor> { Mon(5, false, 0, 0, 800, 600, 1.0), Mon(6, false, 800, 0, 1920, 1080, 1.0) })) == "5:0,0,800,600", "");

            // Every count from 1 to 32 must give exactly that many cells, all inside their monitor and none overlapping.
            List<List<TileMonitor>> layouts = new List<List<TileMonitor>>
            {
                new List<TileMonitor> { A },
                new List<TileMonitor> { Mon(0, true, 0, 0, 1080, 1920, 1.0) },
                new List<TileMonitor> { Mon(0, true, -200, 40, 1366, 729, 1.25) },
                new List<TileMonitor> { A, B },
                new List<TileMonitor> { A, B, C },
            };
            foreach (List<TileMonitor> layout in layouts)
            {
                bool countsOk = true, insideOk = true, overlapOk = true;
                for (int n = 1; n <= 32; n++)
                {
                    List<TileCell> cells = Tiling.ComputeTilingAcrossMonitors(n, layout);
                    if (cells.Count != n) countsOk = false;
                    for (int i = 0; i < cells.Count; i++)
                    {
                        TileMonitor m = null;
                        foreach (TileMonitor candidate in layout) if (candidate.Index == cells[i].MonitorIndex) m = candidate;
                        if (m == null || !Inside(cells[i], m)) insideOk = false;
                        for (int j = i + 1; j < cells.Count; j++)
                            if (cells[i].MonitorIndex == cells[j].MonitorIndex && Overlaps(cells[i], cells[j])) overlapOk = false;
                    }
                }
                string name = layout.Count + " monitor(s), first " + layout[0].Width + "x" + layout[0].Height;
                T.Check("counts 1..32 give exactly that many cells (" + name + ")", countsOk, "");
                T.Check("every cell is inside its work area (" + name + ")", insideOk, "");
                T.Check("cells do not overlap (" + name + ")", overlapOk, "");
            }

            // Monitor ordering used by the platform list: primary first, then left to right, then top to bottom.
            List<TileMonitor> unordered = new List<TileMonitor> { C, B, A };
            unordered.Sort(Tiling.CompareMonitors);
            T.Check("order: primary first, then left to right", unordered[0] == A && unordered[1] == C && unordered[2] == B, string.Join(",", Indexes(unordered)));
            TileMonitor upper = Mon(7, false, 1920, 0, 1000, 1000, 1.0);
            TileMonitor lower = Mon(8, false, 1920, 500, 1000, 1000, 1.0);
            T.Check("order: same x, smaller y first", Tiling.CompareMonitors(upper, lower) < 0 && Tiling.CompareMonitors(lower, upper) > 0 && Tiling.CompareMonitors(upper, upper) == 0, "");

            // Platform monitor conversion: the work area (not the full bounds) is what gets tiled.
            MonitorInfo platform = new MonitorInfo(7, new ScreenRect(0, 0, 3024, 1964), new ScreenRect(0, 25, 3024, 1939), 2.0, true);
            TileMonitor converted = TileMonitor.FromPlatform(platform, 0);
            T.Check("FromPlatform copies the work area, scale and primary flag", converted.X == 0 && converted.Y == 25 && converted.Width == 3024 && converted.Height == 1939 && converted.DpiScale == 2.0 && converted.IsPrimary && converted.Index == 0, "");
            T.Check("FromPlatform result: a lone window fills the full display", Describe(Tiling.ComputeTiling(1, converted)) == "0:0,0,3024,1964", Describe(Tiling.ComputeTiling(1, converted)));
            bool threw = false;
            try { TileMonitor.FromPlatform(null, 0); } catch (ArgumentNullException) { threw = true; }
            T.Check("FromPlatform rejects null", threw, "");
        }

        private static List<int> Indexes(List<TileMonitor> list)
        {
            List<int> r = new List<int>();
            foreach (TileMonitor m in list) r.Add(m.Index);
            return r;
        }
        private static readonly string[] SINGLE_A = {
            "0:0,0,1920,1080",
            "0:1,1,958,1078 0:961,1,958,1078",
            "0:1,1,958,538 0:961,1,958,538 0:1,541,958,538",
            "0:1,1,958,538 0:961,1,958,538 0:1,541,958,538 0:961,541,958,538",
            "0:1,1,638,538 0:641,1,638,538 0:1281,1,638,538 0:1,541,638,538 0:641,541,638,538",
            "0:1,1,638,538 0:641,1,638,538 0:1281,1,638,538 0:1,541,638,538 0:641,541,638,538 0:1281,541,638,538",
            "0:1,1,638,358 0:641,1,638,358 0:1281,1,638,358 0:1,361,638,358 0:641,361,638,358 0:1281,361,638,358 0:641,721,638,358",
            "0:1,1,638,358 0:641,1,638,358 0:1281,1,638,358 0:1,361,638,358 0:641,361,638,358 0:1281,361,638,358 0:1,721,638,358 0:641,721,638,358",
        };
        private static readonly string[] SINGLE_B = {
            "1:1920,0,2560,1440",
            "1:1921,1,1278,1438 1:3201,1,1278,1438",
            "1:1921,1,1278,718 1:3201,1,1278,718 1:1921,721,1278,718",
            "1:1921,1,1278,718 1:3201,1,1278,718 1:1921,721,1278,718 1:3201,721,1278,718",
            "1:1921,1,851,718 1:2774,1,851,718 1:3627,1,852,718 1:1921,721,851,718 1:2774,721,851,718",
            "1:1921,1,851,718 1:2774,1,851,718 1:3627,1,852,718 1:1921,721,851,718 1:2774,721,851,718 1:3627,721,852,718",
            "1:1921,1,851,478 1:2774,1,851,478 1:3627,1,852,478 1:1921,481,851,478 1:2774,481,851,478 1:3627,481,852,478 1:2774,961,851,478",
            "1:1921,1,851,478 1:2774,1,851,478 1:3627,1,852,478 1:1921,481,851,478 1:2774,481,851,478 1:3627,481,852,478 1:1921,961,851,478 1:2774,961,851,478",
        };
        private static readonly string[] ACROSS_AB = {
            "0:0,0,1920,1080",
            "1:1921,1,1278,1438 1:3201,1,1278,1438",
            "0:0,0,1920,1080 1:1921,1,1278,1438 1:3201,1,1278,1438",
            "0:0,0,1920,1080 1:1921,1,1278,718 1:3201,1,1278,718 1:1921,721,1278,718",
            "0:0,0,1920,1080 1:1921,1,1278,718 1:3201,1,1278,718 1:1921,721,1278,718 1:3201,721,1278,718",
            "0:1,1,958,1078 0:961,1,958,1078 1:1921,1,1278,718 1:3201,1,1278,718 1:1921,721,1278,718 1:3201,721,1278,718",
            "0:1,1,958,1078 0:961,1,958,1078 1:1921,1,851,718 1:2774,1,851,718 1:3627,1,852,718 1:1921,721,851,718 1:2774,721,851,718",
            "0:1,1,958,1078 0:961,1,958,1078 1:1921,1,851,718 1:2774,1,851,718 1:3627,1,852,718 1:1921,721,851,718 1:2774,721,851,718 1:3627,721,852,718",
            "0:1,1,958,538 0:961,1,958,538 0:1,541,958,538 1:1921,1,851,718 1:2774,1,851,718 1:3627,1,852,718 1:1921,721,851,718 1:2774,721,851,718 1:3627,721,852,718",
            "0:1,1,958,538 0:961,1,958,538 0:1,541,958,538 1:1921,1,851,478 1:2774,1,851,478 1:3627,1,852,478 1:1921,481,851,478 1:2774,481,851,478 1:3627,481,852,478 1:2774,961,851,478",
        };
        private static readonly string[] ACROSS_BA_order = {
            "0:0,0,1920,1080",
            "1:1921,1,1278,1438 1:3201,1,1278,1438",
            "1:1921,1,1278,1438 1:3201,1,1278,1438 0:0,0,1920,1080",
            "1:1921,1,1278,718 1:3201,1,1278,718 1:1921,721,1278,718 0:0,0,1920,1080",
            "1:1921,1,1278,718 1:3201,1,1278,718 1:1921,721,1278,718 1:3201,721,1278,718 0:0,0,1920,1080",
            "1:1921,1,1278,718 1:3201,1,1278,718 1:1921,721,1278,718 1:3201,721,1278,718 0:1,1,958,1078 0:961,1,958,1078",
            "1:1921,1,851,718 1:2774,1,851,718 1:3627,1,852,718 1:1921,721,851,718 1:2774,721,851,718 0:1,1,958,1078 0:961,1,958,1078",
            "1:1921,1,851,718 1:2774,1,851,718 1:3627,1,852,718 1:1921,721,851,718 1:2774,721,851,718 1:3627,721,852,718 0:1,1,958,1078 0:961,1,958,1078",
            "1:1921,1,851,718 1:2774,1,851,718 1:3627,1,852,718 1:1921,721,851,718 1:2774,721,851,718 1:3627,721,852,718 0:1,1,958,538 0:961,1,958,538 0:1,541,958,538",
            "1:1921,1,851,478 1:2774,1,851,478 1:3627,1,852,478 1:1921,481,851,478 1:2774,481,851,478 1:3627,481,852,478 1:2774,961,851,478 0:1,1,958,538 0:961,1,958,538 0:1,541,958,538",
        };
        private static readonly string[] ACROSS_ABC = {
            "0:0,0,1920,1080",
            "1:1921,1,1278,1438 1:3201,1,1278,1438",
            "0:0,0,1920,1080 1:1921,1,1278,1438 1:3201,1,1278,1438",
            "0:0,0,1920,1080 1:1921,1,1278,718 1:3201,1,1278,718 1:1921,721,1278,718",
            "0:1,1,958,1078 0:961,1,958,1078 1:1921,1,1278,718 1:3201,1,1278,718 1:1921,721,1278,718",
            "0:0,0,1920,1080 1:1921,1,1278,718 1:3201,1,1278,718 1:1921,721,1278,718 1:3201,721,1278,718 2:-1280,0,1280,1024",
            "0:1,1,958,1078 0:961,1,958,1078 1:1921,1,1278,718 1:3201,1,1278,718 1:1921,721,1278,718 1:3201,721,1278,718 2:-1280,0,1280,1024",
            "0:1,1,958,1078 0:961,1,958,1078 1:1921,1,851,718 1:2774,1,851,718 1:3627,1,852,718 1:1921,721,851,718 1:2774,721,851,718 2:-1280,0,1280,1024",
            "0:1,1,958,538 0:961,1,958,538 0:1,541,958,538 1:1921,1,851,718 1:2774,1,851,718 1:3627,1,852,718 1:1921,721,851,718 1:2774,721,851,718 2:-1280,0,1280,1024",
            "0:1,1,958,538 0:961,1,958,538 0:1,541,958,538 1:1921,1,851,718 1:2774,1,851,718 1:3627,1,852,718 1:1921,721,851,718 1:2774,721,851,718 1:3627,721,852,718 2:-1280,0,1280,1024",
        };
        private static readonly string[] PORTRAIT = {
            "0:0,0,1080,1920",
            "0:1,1,1078,958 0:1,961,1078,958",
            "0:1,1,1078,638 0:1,641,1078,638 0:1,1281,1078,638",
            "0:1,1,1078,478 0:1,481,1078,478 0:1,961,1078,478 0:1,1441,1078,478",
        };
        private static readonly string[] TINY = {
            "0:0,0,1,1",
            "0:1,1,1,1 0:1,1,1,1",
            "0:1,1,1,1 0:1,1,1,1 0:1,1,1,1",
        };

        // Reference tables (see the note at the top of this class).
    }
}
