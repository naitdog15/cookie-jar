using System;
using System.Collections.Generic;
using CookieJar.Core;

namespace CookieJar.Tests
{
    public static class MemoryPolicyTests
    {
        private static HardwareProfile Ram(double ramGb)
        {
            HardwareProfile p = HardwareProfile.CreateDefault();
            p.RamGb = ramGb;
            return p;
        }

        private static MemoryAllocation Alloc(int pid, MemoryPreset preset, int customMb, string label)
        {
            return new MemoryAllocation { Pid = pid, Preset = preset, CustomMb = customMb, Label = label };
        }

        public static void Run()
        {
            // Budget = max(2048, (RamGb - 3) * 1024).
            T.Check("budget floor at 4 GB", MemoryPolicy.BudgetMb(Ram(4)) == 2048, "got " + MemoryPolicy.BudgetMb(Ram(4)));
            T.Check("budget floor at 2 GB", MemoryPolicy.BudgetMb(Ram(2)) == 2048);
            T.Check("budget floor at 0 GB", MemoryPolicy.BudgetMb(Ram(0)) == 2048);
            T.Check("budget floor at negative RAM", MemoryPolicy.BudgetMb(Ram(-5)) == 2048);
            T.Check("budget floor for null profile", MemoryPolicy.BudgetMb(null) == 2048, "got " + MemoryPolicy.BudgetMb(null));
            T.Check("budget at 8 GB is 5120", MemoryPolicy.BudgetMb(Ram(8)) == 5120, "got " + MemoryPolicy.BudgetMb(Ram(8)));
            T.Check("budget at 16 GB is 13312", MemoryPolicy.BudgetMb(Ram(16)) == 13312, "got " + MemoryPolicy.BudgetMb(Ram(16)));

            // Unscaled suggestions at 16 GB (budget 13312): base values after 64 MB rounding.
            HardwareProfile big = Ram(16);
            T.Check("high unscaled", MemoryPolicy.SuggestedMb(MemoryPreset.High, 1, big) == 3584);
            T.Check("medium unscaled", MemoryPolicy.SuggestedMb(MemoryPreset.Medium, 1, big) == 2048);
            T.Check("low unscaled rounds 1200 up to 1216", MemoryPolicy.SuggestedMb(MemoryPreset.Low, 1, big) == 1216,
                "got " + MemoryPolicy.SuggestedMb(MemoryPreset.Low, 1, big));
            T.Check("unlimited suggests 0", MemoryPolicy.SuggestedMb(MemoryPreset.Unlimited, 1, big) == 0);
            T.Check("custom suggests 0", MemoryPolicy.SuggestedMb(MemoryPreset.Custom, 1, big) == 0);
            T.Check("two high fit at 16 GB", MemoryPolicy.SuggestedMb(MemoryPreset.High, 2, big) == 3584);

            // Scaling when count * base > budget.
            // 16 GB, 4 High: 14336 > 13312, so 3584 * 13312 / 14336 = 3328.
            T.Check("4 high scaled at 16 GB", MemoryPolicy.SuggestedMb(MemoryPreset.High, 4, big) == 3328,
                "got " + MemoryPolicy.SuggestedMb(MemoryPreset.High, 4, big));

            // 8 GB (budget 5120): 2 High = 7168 > 5120, 3584 * 5120 / 7168 = 2560 exactly.
            HardwareProfile eight = Ram(8);
            T.Check("2 high scaled at 8 GB", MemoryPolicy.SuggestedMb(MemoryPreset.High, 2, eight) == 2560,
                "got " + MemoryPolicy.SuggestedMb(MemoryPreset.High, 2, eight));
            T.Check("3 high scaled at 8 GB", MemoryPolicy.SuggestedMb(MemoryPreset.High, 3, eight) == 1728,
                "got " + MemoryPolicy.SuggestedMb(MemoryPreset.High, 3, eight));
            T.Check("2 medium fit at 8 GB", MemoryPolicy.SuggestedMb(MemoryPreset.Medium, 2, eight) == 2048);
            T.Check("3 medium scaled at 8 GB", MemoryPolicy.SuggestedMb(MemoryPreset.Medium, 3, eight) == 1728,
                "got " + MemoryPolicy.SuggestedMb(MemoryPreset.Medium, 3, eight));

            // Floor: 16 instances on 8 GB gives 320 MB, raised to 600, rounds to 576, then bumped to 640.
            T.Check("16 high on 8 GB hits scaled floor", MemoryPolicy.SuggestedMb(MemoryPreset.High, 16, eight) == 640,
                "got " + MemoryPolicy.SuggestedMb(MemoryPreset.High, 16, eight));
            T.Check("16 low on 8 GB hits scaled floor", MemoryPolicy.SuggestedMb(MemoryPreset.Low, 16, eight) == 640,
                "got " + MemoryPolicy.SuggestedMb(MemoryPreset.Low, 16, eight));

            // Count is clamped to 1..16.
            T.Check("count 99 clamps to 16", MemoryPolicy.SuggestedMb(MemoryPreset.High, 99, eight) == 640,
                "got " + MemoryPolicy.SuggestedMb(MemoryPreset.High, 99, eight));
            T.Check("count 0 clamps to 1", MemoryPolicy.SuggestedMb(MemoryPreset.High, 0, eight) == 3584,
                "got " + MemoryPolicy.SuggestedMb(MemoryPreset.High, 0, eight));
            T.Check("negative count clamps to 1", MemoryPolicy.SuggestedMb(MemoryPreset.High, -3, eight) == 3584);

            // Tiny RAM: 3584 is capped by the 2048 budget.
            T.Check("high on 0.5 GB is budget 2048", MemoryPolicy.SuggestedMb(MemoryPreset.High, 1, Ram(0.5)) == 2048,
                "got " + MemoryPolicy.SuggestedMb(MemoryPreset.High, 1, Ram(0.5)));
            T.Check("high on null profile is budget 2048", MemoryPolicy.SuggestedMb(MemoryPreset.High, 1, null) == 2048);

            // ResolveMb: Custom clamps to [128, 65536] with no rounding. Presets use SuggestedMb.
            T.Check("custom 50 clamps to 128", MemoryPolicy.ResolveMb(MemoryPreset.Custom, 50, 1, eight) == 128);
            T.Check("custom 0 clamps to 128", MemoryPolicy.ResolveMb(MemoryPreset.Custom, 0, 1, eight) == 128);
            T.Check("custom -1 clamps to 128", MemoryPolicy.ResolveMb(MemoryPreset.Custom, -1, 1, eight) == 128);
            T.Check("custom 129 is kept", MemoryPolicy.ResolveMb(MemoryPreset.Custom, 129, 1, eight) == 129);
            T.Check("custom 1024 is kept", MemoryPolicy.ResolveMb(MemoryPreset.Custom, 1024, 1, eight) == 1024);
            T.Check("custom 65536 is kept", MemoryPolicy.ResolveMb(MemoryPreset.Custom, 65536, 1, eight) == 65536);
            T.Check("custom 70000 clamps to 65536", MemoryPolicy.ResolveMb(MemoryPreset.Custom, 70000, 1, eight) == 65536);
            T.Check("preset ignores custom value", MemoryPolicy.ResolveMb(MemoryPreset.High, 9999, 1, big) == 3584);
            T.Check("unlimited resolves to 0", MemoryPolicy.ResolveMb(MemoryPreset.Unlimited, 500, 1, big) == 0);
            T.Check("resolve for preset scales with count", MemoryPolicy.ResolveMb(MemoryPreset.High, 0, 3, eight) == 1728);

            // ValidateAllocation: messages copied from MemoryGovernor.ValidateAllocation.
            T.Check("null items give no warnings", MemoryPolicy.ValidateAllocation(null, big, 1, 16).Count == 0);
            T.Check("empty items give no warnings", MemoryPolicy.ValidateAllocation(new List<MemoryAllocation>(), big, 1, 16).Count == 0);

            List<string> low = MemoryPolicy.ValidateAllocation(
                new List<MemoryAllocation> { Alloc(1, MemoryPreset.Custom, 500, "Acct A") }, big, 1, 16);
            T.Check("one low-cap warning", low.Count == 1, "count " + low.Count);
            if (low.Count == 1)
            {
                T.Check("low-cap message text", low[0] == "Acct A is capped at 500 MB, below the 800 MB safe minimum. It will likely stutter.", low[0]);
            }

            List<string> pidLabel = MemoryPolicy.ValidateAllocation(
                new List<MemoryAllocation> { Alloc(7, MemoryPreset.Custom, 600, null) }, big, 1, 16);
            T.Check("low-cap uses PID when no label", pidLabel.Count == 1 && pidLabel[0] == "PID 7 is capped at 600 MB, below the 800 MB safe minimum. It will likely stutter.",
                pidLabel.Count == 1 ? pidLabel[0] : "count " + pidLabel.Count);

            List<string> negative = MemoryPolicy.ValidateAllocation(
                new List<MemoryAllocation> { Alloc(3, MemoryPreset.Custom, -5, null) }, big, 1, 16);
            T.Check("negative custom resolves to 128 and warns", negative.Count == 1 && negative[0].StartsWith("PID 3 is capped at 128 MB,"),
                negative.Count == 1 ? negative[0] : "count " + negative.Count);

            List<string> atMin = MemoryPolicy.ValidateAllocation(
                new List<MemoryAllocation> { Alloc(1, MemoryPreset.Custom, 800, "X") }, big, 1, 16);
            T.Check("800 MB is not below the minimum", atMin.Count == 0, "count " + atMin.Count);

            List<string> unlimited = MemoryPolicy.ValidateAllocation(
                new List<MemoryAllocation> { Alloc(1, MemoryPreset.Unlimited, 0, "U") }, big, 1, 16);
            T.Check("unlimited with one instance is fine", unlimited.Count == 0, "count " + unlimited.Count);

            List<string> nullEntry = MemoryPolicy.ValidateAllocation(
                new List<MemoryAllocation> { null, Alloc(2, MemoryPreset.Custom, 500, "Y") }, big, 1, 16);
            T.Check("null entries are skipped", nullEntry.Count == 1, "count " + nullEntry.Count);

            List<string> freeRam = MemoryPolicy.ValidateAllocation(
                new List<MemoryAllocation> { Alloc(1, MemoryPreset.Medium, 0, "M") }, big, 1, 1.0);
            T.Check("free RAM warning count", freeRam.Count == 1, "count " + freeRam.Count);
            if (freeRam.Count == 1)
            {
                T.Check("free RAM message text", freeRam[0] == "Combined caps of 2048 MB exceed free RAM of 1.0 GB (1024 MB). Windows will page memory to disk and the games may stutter.",
                    freeRam[0]);
            }

            // Budget: 4 GB gives a 2048 MB budget. Two 1500 MB caps total 3000.
            List<string> budget = MemoryPolicy.ValidateAllocation(
                new List<MemoryAllocation> { Alloc(1, MemoryPreset.Custom, 1500, "A"), Alloc(2, MemoryPreset.Custom, 1500, "B") },
                Ram(4), 1, 16);
            T.Check("budget warning count", budget.Count == 1, "count " + budget.Count);
            if (budget.Count == 1)
            {
                T.Check("budget message text", budget[0] == "Combined caps of 3000 MB exceed the safe budget of 2048 MB (installed RAM minus 3.0 GB reserved for Windows).",
                    budget[0]);
            }

            // Both free RAM and budget warnings, free RAM first.
            List<string> both = MemoryPolicy.ValidateAllocation(
                new List<MemoryAllocation> { Alloc(1, MemoryPreset.Custom, 1500, "A"), Alloc(2, MemoryPreset.Custom, 1500, "B") },
                Ram(4), 1, 1.0);
            T.Check("free and budget warnings together", both.Count == 2, "count " + both.Count);
            if (both.Count == 2)
            {
                T.Check("free RAM warning comes first", both[0].StartsWith("Combined caps of 3000 MB exceed free RAM"), both[0]);
                T.Check("budget warning comes second", both[1].StartsWith("Combined caps of 3000 MB exceed the safe budget"), both[1]);
            }

            // Unlimited counts as High with the instance count applied: five Unlimited on 8 GB exceed 5120.
            List<MemoryAllocation> five = new List<MemoryAllocation>();
            for (int i = 0; i < 5; i++) five.Add(Alloc(i + 1, MemoryPreset.Unlimited, 0, null));
            List<string> fiveWarn = MemoryPolicy.ValidateAllocation(five, eight, 1, 32);
            T.Check("five unlimited on 8 GB warns about budget", fiveWarn.Count == 1 &&
                fiveWarn[0] == "Combined caps of 17920 MB exceed the safe budget of 5120 MB (installed RAM minus 3.0 GB reserved for Windows).",
                fiveWarn.Count == 1 ? fiveWarn[0] : "count " + fiveWarn.Count);
        }
    }

    public static class RamScaleTests
    {
        public static void Run()
        {
            T.Check("steps is 47", RamScale.Steps == 47, "got " + RamScale.Steps);
            T.Check("index 0 is Auto (0)", RamScale.MbFromIndex(0) == 0, "got " + RamScale.MbFromIndex(0));
            T.Check("index -4 is Auto (0)", RamScale.MbFromIndex(-4) == 0);
            T.Check("index 1 is 128", RamScale.MbFromIndex(1) == 128, "got " + RamScale.MbFromIndex(1));
            T.Check("index 2 is 192", RamScale.MbFromIndex(2) == 192);
            T.Check("index Steps is 3072", RamScale.MbFromIndex(RamScale.Steps) == 3072, "got " + RamScale.MbFromIndex(RamScale.Steps));
            T.Check("index past Steps clamps to 3072", RamScale.MbFromIndex(RamScale.Steps + 9) == 3072);

            T.Check("0 MB is index 0", RamScale.IndexFromMb(0) == 0);
            T.Check("negative MB is index 0", RamScale.IndexFromMb(-1) == 0);
            T.Check("below min clamps to index 1", RamScale.IndexFromMb(1) == 1, "got " + RamScale.IndexFromMb(1));
            T.Check("128 is index 1", RamScale.IndexFromMb(128) == 1);
            T.Check("150 rounds to index 1", RamScale.IndexFromMb(150) == 1, "got " + RamScale.IndexFromMb(150));
            T.Check("170 rounds to index 2", RamScale.IndexFromMb(170) == 2, "got " + RamScale.IndexFromMb(170));
            T.Check("3072 is index Steps", RamScale.IndexFromMb(3072) == RamScale.Steps, "got " + RamScale.IndexFromMb(3072));
            T.Check("above max clamps to Steps", RamScale.IndexFromMb(100000) == RamScale.Steps, "got " + RamScale.IndexFromMb(100000));

            bool roundTrip = true;
            string firstBad = "";
            for (int i = 1; i <= RamScale.Steps; i++)
            {
                int back = RamScale.IndexFromMb(RamScale.MbFromIndex(i));
                if (back != i)
                {
                    roundTrip = false;
                    firstBad = "index " + i + " came back as " + back;
                    break;
                }
            }
            T.Check("every slider index round trips", roundTrip, firstBad);

            T.Check("label Auto for 0", RamScale.Label(0) == "Auto");
            T.Check("label Auto for negative", RamScale.Label(-1) == "Auto");
            T.Check("label 128 MB", RamScale.Label(128) == "128 MB", RamScale.Label(128));
            T.Check("label 640 MB", RamScale.Label(640) == "640 MB", RamScale.Label(640));
            T.Check("label 1023 MB", RamScale.Label(1023) == "1023 MB", RamScale.Label(1023));
            T.Check("label 1 GB", RamScale.Label(1024) == "1 GB", RamScale.Label(1024));
            T.Check("label 1.5 GB", RamScale.Label(1536) == "1.5 GB", RamScale.Label(1536));
            T.Check("label 3 GB", RamScale.Label(3072) == "3 GB", RamScale.Label(3072));
        }
    }
}
