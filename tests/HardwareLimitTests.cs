using System;
namespace RbxMulti
{
    // Pure policy checks; no game launches or input.
    public static class HardwareLimitTests
    {
        private static int failed;
        private static void Check(string name, bool ok)
        {
            Console.WriteLine((ok ? "PASS " : "FAIL ") + name);
            if (!ok) failed++;
        }
        private static HardwareProfile P(int cores, int threads, double ram, double vram)
        {
            return new HardwareProfile { CpuCores = cores, LogicalProcessors = threads, RamGb = ram, VramGb = vram };
        }
        public static int Main()
        {
            HardwareProfile pc = P(6, 12, 25700368384.0 / (1024 * 1024 * 1024), 8);
            Check("this PC recommends 9", pc.RecommendedMax == 9);
            Check("this PC permits 12", pc.HardMax == 12);
            Restrictor r = new Restrictor(pc);
            Check("ninth launch allowed", r.Evaluate(8, 8, 30).Level == RestrictLevel.Allow);
            Check("tenth launch warns", r.Evaluate(9, 8, 30).Level == RestrictLevel.Warn);
            Check("twelfth launch warns", r.Evaluate(11, 8, 30).CanLaunch);
            Check("thirteenth launch blocked", !r.Evaluate(12, 8, 30).CanLaunch);
            RestrictResult extra = r.Evaluate(12, 8, 30, true);
            Check("override permits thirteenth with explicit warning", extra.CanLaunch && extra.Level == RestrictLevel.Warn && extra.RequiresConfirmation);
            Check("every extra launch requires confirmation", r.Evaluate(13, 8, 30, true).RequiresConfirmation);
            Check("override off restores count block", !r.Evaluate(13, 8, 30, false).CanLaunch);
            Check("override retains low-memory block", !r.Evaluate(12, 1.49, 30, true).CanLaunch);
            Check("ordinary warning stays ordinary", !r.Evaluate(9, 8, 30, true).RequiresConfirmation);
            Check("override warning includes live resources", extra.Message.Contains("8.0 GB") && extra.Message.Contains("30%"));
            Check("low RAM still blocks", !r.Evaluate(0, 1.49, 0).CanLaunch);
            Check("busy CPU still warns", r.Evaluate(0, 8, 95).Level == RestrictLevel.Warn);
            HardwareProfile low = P(6, 12, 8, 8);
            Check("8 GB remains RAM limited", low.RecommendedMax == 2 && low.HardMax == 2);
            HardwareProfile gpu = P(6, 12, 24, 2);
            Check("low VRAM stays conservative", gpu.RecommendedMax == 3 && gpu.HardMax == 5);
            HardwareProfile noSmt = P(6, 6, 24, 8);
            Check("no SMT gains no CPU capacity", noSmt.RecommendedMax == 6 && noSmt.HardMax == 6);
            HardwareProfile large = P(16, 32, 64, 16);
            Check("larger hardware scales", large.RecommendedMax == 24 && large.HardMax == 32);
            HardwareProfile unknown = P(0, 0, 0, 0);
            Check("failed detection keeps minimum", unknown.RecommendedMax == 1 && unknown.HardMax == 1);
            Console.WriteLine("Failures: " + failed);
            double ram = pc.RamGb;
            Check("auto no sessions", AutoRamPolicy.PerSessionMb(ram, 0) == 0);
            Check("auto one session capped at 3 GB", AutoRamPolicy.PerSessionMb(ram, 1) == 3072);
            Check("auto six sessions capped at 3 GB", AutoRamPolicy.PerSessionMb(ram, 6) == 3072);
            Check("auto nine sessions", AutoRamPolicy.PerSessionMb(ram, 9) == 2240);
            Check("auto twelve sessions", AutoRamPolicy.PerSessionMb(ram, 12) == 1664);
            Check("auto session exit restores larger share", AutoRamPolicy.PerSessionMb(ram, 11) > AutoRamPolicy.PerSessionMb(ram, 12));
            Check("auto insufficient budget", AutoRamPolicy.PerSessionMb(4, 12) == 0);
            Check("auto invalid RAM", AutoRamPolicy.PerSessionMb(double.NaN, 12) == 0);
            for (int count = 1; count <= 64; count++)
            {
                int share = AutoRamPolicy.PerSessionMb(ram, count);
                Check("auto budget respected at " + count, share * (double)count <= (ram - HardwareProfile.OsReserveGb) * 1024);
            }
            Console.WriteLine("Total failures: " + failed);
            return failed == 0 ? 0 : 1;
        }
    }
}
