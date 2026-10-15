using System;
using CookieJar.Core;

namespace CookieJar.Tests
{
    public static class ResourceSamplingTests
    {
        // Fake sampler: set NextCpu/NextMemory to the next values to return, or null to fail the read.
        private sealed class FakeSampler : ISystemSampler
        {
            public CpuTimeCounters NextCpu;
            public MemoryCounters NextMemory;
            public bool Throw;

            public bool TryReadCpuTimes(out CpuTimeCounters counters)
            {
                if (Throw) throw new InvalidOperationException("boom");
                counters = NextCpu;
                return NextCpu != null;
            }

            public bool TryReadMemory(out MemoryCounters counters)
            {
                if (Throw) throw new InvalidOperationException("boom");
                counters = NextMemory;
                return NextMemory != null;
            }
        }

        private static bool Near(double a, double b)
        {
            return Math.Abs(a - b) < 1e-9;
        }

        private const ulong Gib = 1073741824UL;

        public static void Run()
        {
            // CPU busy percent comes from the change between two samples.
            FakeSampler s = new FakeSampler();
            ResourceSampler sampler = new ResourceSampler(s);

            T.Check("fresh sampler reads zero", sampler.Current.CpuPercent == 0.0 && sampler.Current.TotalRamGb == 0.0);

            s.NextCpu = new CpuTimeCounters(1000, 2000, 3000);
            s.NextMemory = null;
            T.Check("first CPU sample reports 0", sampler.PollOnce().CpuPercent == 0.0);

            // Total goes 5000 -> 5400 (+400), idle 1000 -> 1100 (+100): busy 300 / 400 = 75%.
            s.NextCpu = new CpuTimeCounters(1100, 2200, 3200);
            double busy = sampler.PollOnce().CpuPercent;
            T.Check("CPU busy from deltas is 75", Near(busy, 75.0), "got " + busy);

            // No new time since the last sample keeps the previous value.
            s.NextCpu = new CpuTimeCounters(1100, 2200, 3200);
            double same = sampler.PollOnce().CpuPercent;
            T.Check("no new time keeps 75", Near(same, 75.0), "got " + same);

            // Idle equals the whole interval: 0%. Kernel +400 with idle +400.
            s.NextCpu = new CpuTimeCounters(1500, 2600, 3200);
            double idle = sampler.PollOnce().CpuPercent;
            T.Check("fully idle interval is 0", Near(idle, 0.0), "got " + idle);

            // Counters going backwards primes at 0 instead of reusing the old delta.
            s.NextCpu = new CpuTimeCounters(10, 20, 30);
            double back = sampler.PollOnce().CpuPercent;
            T.Check("backwards counters prime at 0", Near(back, 0.0), "got " + back);

            // Interval with no idle time: 100%.
            s.NextCpu = new CpuTimeCounters(10, 120, 30);
            double full = sampler.PollOnce().CpuPercent;
            T.Check("no idle in interval is 100", Near(full, 100.0), "got " + full);

            // Failed CPU read keeps the last value.
            s.NextCpu = null;
            double kept = sampler.PollOnce().CpuPercent;
            T.Check("failed CPU read keeps last value", Near(kept, 100.0), "got " + kept);

            // Memory: 16 GiB total, 4 GiB available.
            FakeSampler m = new FakeSampler();
            ResourceSampler memSampler = new ResourceSampler(m);
            m.NextCpu = null;
            m.NextMemory = new MemoryCounters(16 * Gib, 4 * Gib);
            ResourceSample mem = memSampler.PollOnce();
            T.Check("total RAM 16 GB", Near(mem.TotalRamGb, 16.0), "got " + mem.TotalRamGb);
            T.Check("free RAM 4 GB", Near(mem.FreeRamGb, 4.0), "got " + mem.FreeRamGb);
            T.Check("used RAM 75 percent", Near(mem.UsedRamPercent, 75.0), "got " + mem.UsedRamPercent);
            T.Check("no CPU read leaves CPU at 0", mem.CpuPercent == 0.0);

            // Zero total memory gives zero used percent.
            m.NextMemory = new MemoryCounters(0, 0);
            ResourceSample zero = memSampler.PollOnce();
            T.Check("zero total gives 0 used", Near(zero.UsedRamPercent, 0.0), "got " + zero.UsedRamPercent);
            T.Check("zero total gives 0 free", Near(zero.FreeRamGb, 0.0));

            // Available above total gives 0 used percent, not a negative value.
            m.NextMemory = new MemoryCounters(8 * Gib, 9 * Gib);
            ResourceSample over = memSampler.PollOnce();
            T.Check("available above total gives 0 used", Near(over.UsedRamPercent, 0.0), "got " + over.UsedRamPercent);
            T.Check("available above total is still reported as free", Near(over.FreeRamGb, 9.0), "got " + over.FreeRamGb);

            // Failed memory read keeps the last values.
            m.NextMemory = null;
            ResourceSample keptMem = memSampler.PollOnce();
            T.Check("failed memory read keeps free RAM", Near(keptMem.FreeRamGb, 9.0), "got " + keptMem.FreeRamGb);

            // A sampler that throws does not escape PollOnce.
            FakeSampler bad = new FakeSampler();
            bad.Throw = true;
            bool threw = false;
            try
            {
                new ResourceSampler(bad).PollOnce();
            }
            catch (Exception)
            {
                threw = true;
            }
            T.Check("sampler exceptions are swallowed", !threw);

            // A null sampler is rejected.
            bool nullRejected = false;
            try
            {
                new ResourceSampler(null);
            }
            catch (ArgumentNullException)
            {
                nullRejected = true;
            }
            T.Check("null sampler is rejected", nullRejected);
        }
    }
}
