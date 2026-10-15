using CookieJar.Core;

namespace CookieJar.Tests
{
    public static class RestrictorTests
    {
        // 8 cores, 16 GB, 8 GB VRAM: RecommendedMax 6, HardMax 8.
        private static Restrictor Make()
        {
            HardwareProfile p = HardwareProfile.CreateDefault();
            p.CpuCores = 8;
            p.RamGb = 16;
            p.VramGb = 8;
            p.CpuName = "  Ryzen Test  ";
            return new Restrictor(p);
        }

        private static void Expect(string name, RestrictResult r, RestrictLevel level, string title, string message)
        {
            T.Check(name + " level", r.Level == level, "expected " + level + " got " + r.Level);
            T.Check(name + " title", r.Title == title, "expected '" + title + "' got '" + r.Title + "'");
            T.Check(name + " message", r.Message == message, "got '" + r.Message + "'");
        }

        public static void Run()
        {
            Restrictor r = Make();
            T.Check("recommended from profile", r.Recommended == 6, "got " + r.Recommended);
            T.Check("max from profile", r.MaxAllowed == 8, "got " + r.MaxAllowed);

            // Rule 1: hard limit (wins over everything).
            Expect("hard limit", r.Evaluate(8, 10, 0), RestrictLevel.Block, "Hard limit reached",
                "Hard limit reached: this PC (Ryzen Test, 8 cores, 16 GB RAM) can run at most 8 instances. Close one to continue.");
            Expect("hard limit beats low RAM", r.Evaluate(9, 0.5, 99), RestrictLevel.Block, "Hard limit reached",
                "Hard limit reached: this PC (Ryzen Test, 8 cores, 16 GB RAM) can run at most 8 instances. Close one to continue.");

            // Rule 2: free RAM below 1.5 GB blocks.
            Expect("not enough memory", r.Evaluate(0, 1.2, 0), RestrictLevel.Block, "Not enough memory",
                "Only 1.2 GB of RAM free; launching another instance could freeze Windows. Close an instance or other apps first.");
            Expect("negative free RAM blocks", r.Evaluate(0, -1.0, 0), RestrictLevel.Block, "Not enough memory",
                "Only -1.0 GB of RAM free; launching another instance could freeze Windows. Close an instance or other apps first.");
            Expect("free exactly 1.5 is not blocked", r.Evaluate(0, 1.5, 0), RestrictLevel.Warn, "Low memory",
                "Low free RAM (1.5 GB). Close apps you do not need before launching.");
            Expect("memory beats above-recommended", r.Evaluate(6, 1.0, 0), RestrictLevel.Block, "Not enough memory",
                "Only 1.0 GB of RAM free; launching another instance could freeze Windows. Close an instance or other apps first.");

            // Rule 3: at or beyond recommended warns.
            Expect("above recommended", r.Evaluate(6, 10, 0), RestrictLevel.Warn, "Above recommended",
                "You are beyond the recommended 6 instances for this PC; expect stuttering or crashes.");
            Expect("above recommended beats low memory", r.Evaluate(6, 2.0, 0), RestrictLevel.Warn, "Above recommended",
                "You are beyond the recommended 6 instances for this PC; expect stuttering or crashes.");
            Expect("five instances is not above recommended", r.Evaluate(5, 10, 0), RestrictLevel.Allow, "Ready",
                "Ready: 5 of 6 recommended instances in use.");

            // Rule 4: free RAM below 3 GB warns.
            Expect("low memory", r.Evaluate(0, 2.5, 0), RestrictLevel.Warn, "Low memory",
                "Low free RAM (2.5 GB). Close apps you do not need before launching.");
            Expect("low memory beats CPU busy", r.Evaluate(0, 2.0, 99), RestrictLevel.Warn, "Low memory",
                "Low free RAM (2.0 GB). Close apps you do not need before launching.");
            Expect("free exactly 3.0 is not low", r.Evaluate(0, 3.0, 0), RestrictLevel.Allow, "Ready",
                "Ready: 0 of 6 recommended instances in use.");

            // Rule 5: CPU above 90% warns.
            Expect("CPU busy", r.Evaluate(0, 10, 95), RestrictLevel.Warn, "CPU busy",
                "CPU is at 95%. A new instance may slow everything down.");
            Expect("CPU exactly 90 is not busy", r.Evaluate(0, 10, 90), RestrictLevel.Allow, "Ready",
                "Ready: 0 of 6 recommended instances in use.");

            // Rule 6: all clear.
            Expect("ready", r.Evaluate(2, 10, 10), RestrictLevel.Allow, "Ready",
                "Ready: 2 of 6 recommended instances in use.");

            // CanLaunch is false only for Block.
            T.Check("block cannot launch", !new RestrictResult { Level = RestrictLevel.Block }.CanLaunch);
            T.Check("warn can launch", new RestrictResult { Level = RestrictLevel.Warn }.CanLaunch);
            T.Check("allow can launch", new RestrictResult { Level = RestrictLevel.Allow }.CanLaunch);

            // Status line and LevelFor.
            T.Check("status line", r.StatusLine(2) == "2 / 6 recommended (hard max 8)", "got " + r.StatusLine(2));
            T.Check("level 0 allow", r.LevelFor(0) == RestrictLevel.Allow);
            T.Check("level 5 allow", r.LevelFor(5) == RestrictLevel.Allow);
            T.Check("level 6 warn", r.LevelFor(6) == RestrictLevel.Warn, "got " + r.LevelFor(6));
            T.Check("level 7 warn", r.LevelFor(7) == RestrictLevel.Warn, "got " + r.LevelFor(7));
            T.Check("level 8 block", r.LevelFor(8) == RestrictLevel.Block, "got " + r.LevelFor(8));
            T.Check("level 9 block", r.LevelFor(9) == RestrictLevel.Block, "got " + r.LevelFor(9));

            // Unknown CPU name falls back to "this PC".
            HardwareProfile unknown = HardwareProfile.CreateDefault();
            unknown.CpuCores = 8;
            unknown.RamGb = 16;
            unknown.VramGb = 8;
            Expect("unknown CPU name", new Restrictor(unknown).Evaluate(8, 10, 0), RestrictLevel.Block, "Hard limit reached",
                "Hard limit reached: this PC (this PC, 8 cores, 16 GB RAM) can run at most 8 instances. Close one to continue.");

            // Null profile uses the fallback limits 2 and 4, with "?" for unknown specs.
            Restrictor none = new Restrictor(null);
            T.Check("null profile recommended", none.Recommended == 2, "got " + none.Recommended);
            T.Check("null profile max", none.MaxAllowed == 4, "got " + none.MaxAllowed);
            Expect("null profile hard limit", none.Evaluate(4, 10, 0), RestrictLevel.Block, "Hard limit reached",
                "Hard limit reached: this PC (this PC, ? cores, ? GB RAM) can run at most 4 instances. Close one to continue.");
            Expect("null profile above recommended", none.Evaluate(2, 10, 0), RestrictLevel.Warn, "Above recommended",
                "You are beyond the recommended 2 instances for this PC; expect stuttering or crashes.");

            // Clamped limits: 16 cores / 64 GB gives recommended 12 and hard max 14.
            HardwareProfile big = HardwareProfile.CreateDefault();
            big.CpuCores = 16;
            big.RamGb = 64;
            big.VramGb = 24;
            Restrictor bigR = new Restrictor(big);
            T.Check("clamped recommended", bigR.Recommended == 12, "got " + bigR.Recommended);
            T.Check("clamped hard max", bigR.MaxAllowed == 14, "got " + bigR.MaxAllowed);
        }
    }
}
