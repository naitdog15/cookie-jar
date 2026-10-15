using CookieJar.Core;

namespace CookieJar.Tests
{
    public static class HardwareTests
    {
        private static HardwareProfile Make(int cores, double ramGb, double vramGb)
        {
            HardwareProfile p = HardwareProfile.CreateDefault();
            p.CpuCores = cores;
            p.LogicalProcessors = cores * 2;
            p.RamGb = ramGb;
            p.VramGb = vramGb;
            p.CpuName = "Test CPU";
            return p;
        }

        private static void CheckLimits(string name, HardwareProfile p, int expectRecommended, int expectHard)
        {
            T.Check(name + " recommended", p.RecommendedMax == expectRecommended,
                "expected " + expectRecommended + " got " + p.RecommendedMax);
            T.Check(name + " hard max", p.HardMax == expectHard,
                "expected " + expectHard + " got " + p.HardMax);
        }

        public static void Run()
        {
            // Expected values follow HardwareProfile.RecommendedMax in src/HardwareProfile.cs:
            // min(cores, floor((RAM - 3) / 2)), 3 when 0 < VRAM < 4, clamped to 1..12; HardMax = min(14, recommended + 2).
            CheckLimits("8 cores / 16 GB / 8 GB VRAM", Make(8, 16, 8), 6, 8);
            CheckLimits("16 cores / 64 GB / 24 GB VRAM (clamped to 12)", Make(16, 64, 24), 12, 14);
            CheckLimits("4 cores / 8 GB / 2 GB VRAM (low VRAM)", Make(4, 8, 2), 2, 4);
            CheckLimits("12 cores / 32 GB / 0.5 GB VRAM (heuristic, capped at 3)", Make(12, 32, 0.5), 3, 5);
            CheckLimits("8 cores / 8 GB / unknown VRAM", Make(8, 8, 0), 2, 4);
            CheckLimits("8 cores / 2 GB (tiny RAM, negative budget)", Make(8, 2, 8), 1, 3);
            CheckLimits("0 cores / 16 GB", Make(0, 16, 8), 1, 3);
            CheckLimits("1 core / 64 GB", Make(1, 64, 0), 1, 3);
            CheckLimits("negative RAM", Make(8, -10, 8), 1, 3);
            CheckLimits("RAM 6.9 GB (floor of 1.95)", Make(8, 6.9, 8), 1, 3);
            CheckLimits("RAM 7 GB (exactly 2 instances)", Make(8, 7, 8), 2, 4);
            CheckLimits("VRAM exactly 4 GB is not capped", Make(12, 32, 4.0), 12, 14);
            CheckLimits("VRAM 3.99 GB is capped at 3", Make(12, 32, 3.99), 3, 5);

            // Null profile values are handled by Restrictor, and CreateDefault gives the probe fallback values.
            HardwareProfile d = HardwareProfile.CreateDefault();
            T.Check("default CPU cores", d.CpuCores == 1, "got " + d.CpuCores);
            T.Check("default RAM", d.RamGb == 4.0, "got " + d.RamGb);
            T.Check("default VRAM", d.VramGb == 0.5, "got " + d.VramGb);
            T.Check("default name", d.CpuName == "Unknown", "got " + d.CpuName);
            T.Check("default recommended is 1", d.RecommendedMax == 1, "got " + d.RecommendedMax);

            T.Check("per-instance RAM is 2 GB", Make(8, 16, 8).PerInstanceRamGb == 2.0);
            T.Check("OS reserve is 3 GB", HardwareProfile.OsReserveGb == 3.0);

            // Format from HardwareProfile.Summary: "{0} | {1}C/{2}T | {3:0.0} GB RAM | {4} {5:0.0} GB".
            HardwareProfile s = Make(8, 16, 8);
            s.LogicalProcessors = 16;
            s.GpuName = "RTX 3070";
            s.VramGb = 8.0;
            s.CpuName = "Ryzen 7";
            string summary = s.Summary;
            T.Check("summary text", summary == "Ryzen 7 | 8C/16T | 16.0 GB RAM | RTX 3070 8.0 GB", "got " + summary);
        }
    }
}
