using CookieJar.Core;

namespace CookieJar.Tests
{
    public static class AccountLayoutTests
    {
        private static AccountLayout Make(int monitor, int x, int y, int w, int h)
        {
            AccountLayout l = new AccountLayout();
            l.MonitorIndex = monitor; l.X = x; l.Y = y; l.Width = w; l.Height = h;
            return l;
        }

        public static void Run()
        {
            AccountLayout original = Make(1, 3201, 1, 1278, 1438);
            AccountLayout copy = original.Clone();

            T.Check("Clone returns a new instance", !ReferenceEquals(original, copy), "");
            T.Check("Clone copies every field", copy.MonitorIndex == 1 && copy.X == 3201 && copy.Y == 1 && copy.Width == 1278 && copy.Height == 1438, "");

            copy.MonitorIndex = 0; copy.X = 1; copy.Y = 2; copy.Width = 3; copy.Height = 4;
            T.Check("changing the clone leaves the original alone", original.MonitorIndex == 1 && original.X == 3201 && original.Y == 1 && original.Width == 1278 && original.Height == 1438, "");

            AccountLayout source = Make(0, 1, 1, 958, 1078);
            AccountLayout cloneOfSource = source.Clone();
            source.Width = 500;
            T.Check("changing the original leaves the clone alone", cloneOfSource.Width == 958, "width=" + cloneOfSource.Width);

            AccountLayout twice = cloneOfSource.Clone().Clone();
            T.Check("clone of a clone keeps the values and is independent", twice.X == 1 && twice.Width == 958 && !ReferenceEquals(twice, cloneOfSource), "");

            RunProfileTiling();
        }

        private static string Describe(System.Collections.Generic.List<TileCell> cells)
        {
            string s = "";
            foreach (TileCell c in cells) s += c.MonitorIndex + ":" + c.X + "," + c.Y + "," + c.Width + "," + c.Height + " ";
            return s.TrimEnd();
        }

        private static void RunProfileTiling()
        {
            TileMonitor primary = new TileMonitor();
            primary.Index = 0; primary.IsPrimary = true; primary.X = 0; primary.Y = 0; primary.Width = 1920; primary.Height = 1080; primary.DpiScale = 1.0;
            System.Collections.Generic.List<TileMonitor> monitors = new System.Collections.Generic.List<TileMonitor> { primary };
            System.Collections.Generic.List<string> ids = new System.Collections.Generic.List<string> { "a", "b" };

            LaunchProfile auto = new LaunchProfile();
            auto.Layouts["b"] = Make(0, 5, 5, 100, 100);
            // Two accounts on one monitor use the 2-cell auto grid (same as SINGLE_A count 2); "b" is replaced by its override.
            T.Check("ApplyTiling: override replaces the auto cell for that account only",
                Describe(ProfileTiling.ApplyTiling(auto, ids, monitors)) == "0:1,1,958,1078 0:5,5,100,100",
                Describe(ProfileTiling.ApplyTiling(auto, ids, monitors)));

            LaunchProfile manual = new LaunchProfile();
            manual.AutoTile = false;
            manual.Layouts["b"] = Make(0, 5, 5, 100, 100);
            T.Check("ApplyTiling: AutoTile off returns only the override cells",
                Describe(ProfileTiling.ApplyTiling(manual, ids, monitors)) == "0:5,5,100,100",
                Describe(ProfileTiling.ApplyTiling(manual, ids, monitors)));

            T.Check("ApplyTiling: no accounts gives no cells", ProfileTiling.ApplyTiling(auto, new System.Collections.Generic.List<string>(), monitors).Count == 0, "");
        }
    }
}
