using System;

namespace CookieJar.Core
{
    /// <summary>Cumulative CPU time counters in 100 ns units. Kernel time includes idle time, as on Windows.</summary>
    public sealed class CpuTimeCounters
    {
        public CpuTimeCounters(ulong idle, ulong kernel, ulong user)
        {
            Idle = idle;
            Kernel = kernel;
            User = user;
        }

        public ulong Idle { get; }
        public ulong Kernel { get; }
        public ulong User { get; }
    }

    /// <summary>Physical memory totals in bytes.</summary>
    public sealed class MemoryCounters
    {
        public MemoryCounters(ulong totalPhysBytes, ulong availPhysBytes)
        {
            TotalPhysBytes = totalPhysBytes;
            AvailPhysBytes = availPhysBytes;
        }

        public ulong TotalPhysBytes { get; }
        public ulong AvailPhysBytes { get; }
    }

    /// <summary>
    /// Raw system counters. The Windows implementation wraps GetSystemTimes and GlobalMemoryStatusEx.
    /// Returns false when a read fails; the sampler then keeps its last values.
    /// </summary>
    public interface ISystemSampler
    {
        bool TryReadCpuTimes(out CpuTimeCounters counters);
        bool TryReadMemory(out MemoryCounters counters);
    }

    /// <summary>One sample of system-wide CPU and RAM usage.</summary>
    public sealed class ResourceSample
    {
        public ResourceSample(double cpuPercent, double freeRamGb, double totalRamGb, double usedRamPercent)
        {
            CpuPercent = cpuPercent;
            FreeRamGb = freeRamGb;
            TotalRamGb = totalRamGb;
            UsedRamPercent = usedRamPercent;
        }

        /// <summary>System-wide CPU busy percentage, 0 to 100.</summary>
        public double CpuPercent { get; }
        /// <summary>Free physical RAM in GiB.</summary>
        public double FreeRamGb { get; }
        /// <summary>Total physical RAM in GiB.</summary>
        public double TotalRamGb { get; }
        /// <summary>Used physical RAM as a percentage, 0 to 100.</summary>
        public double UsedRamPercent { get; }
    }

    /// <summary>
    /// Turns raw counters into CPU busy percent and RAM totals. CPU busy percent comes from the change
    /// between two samples, so the first poll reports 0. Thread-safe.
    /// </summary>
    public sealed class ResourceSampler
    {
        private const double BytesPerGb = 1073741824.0;

        private readonly object _sync = new object();
        private readonly ISystemSampler _sampler;

        // CPU sampling state.
        private bool _hasPrevCpu;
        private ulong _prevIdle;
        private ulong _prevTotal;

        // Published values.
        private double _cpuPercent;
        private double _freeRamGb;
        private double _totalRamGb;
        private double _usedRamPercent;

        public ResourceSampler(ISystemSampler sampler)
        {
            if (sampler == null) throw new ArgumentNullException(nameof(sampler));
            _sampler = sampler;
        }

        /// <summary>The values from the last poll (all zero before the first one).</summary>
        public ResourceSample Current
        {
            get
            {
                lock (_sync)
                {
                    return new ResourceSample(_cpuPercent, _freeRamGb, _totalRamGb, _usedRamPercent);
                }
            }
        }

        /// <summary>Takes one sample. A failed read keeps the last value for that part.</summary>
        public ResourceSample PollOnce()
        {
            lock (_sync)
            {
                CpuTimeCounters cpu = null;
                bool cpuOk;
                try
                {
                    cpuOk = _sampler.TryReadCpuTimes(out cpu);
                }
                catch (Exception)
                {
                    cpuOk = false;
                }
                if (cpuOk && cpu != null) UpdateCpu(cpu);

                MemoryCounters mem = null;
                bool memOk;
                try
                {
                    memOk = _sampler.TryReadMemory(out mem);
                }
                catch (Exception)
                {
                    memOk = false;
                }
                if (memOk && mem != null) UpdateMemory(mem);
            }

            return Current;
        }

        // Busy = (total - idle) / total over the interval. Kernel time includes idle, so total = kernel + user.
        private void UpdateCpu(CpuTimeCounters c)
        {
            ulong idle = c.Idle;
            ulong total = c.Kernel + c.User;

            // First sample, or counters went backwards: prime and report 0.
            if (!_hasPrevCpu || total < _prevTotal || idle < _prevIdle)
            {
                _hasPrevCpu = true;
                _prevIdle = idle;
                _prevTotal = total;
                _cpuPercent = 0.0;
                return;
            }

            ulong dTotal = total - _prevTotal;
            ulong dIdle = idle - _prevIdle;
            _prevTotal = total;
            _prevIdle = idle;

            // No new time since the last sample: keep the previous value.
            if (dTotal == 0) return;

            double busy = 0.0;
            if (dIdle < dTotal)
            {
                busy = (double)(dTotal - dIdle) / (double)dTotal * 100.0;
            }
            _cpuPercent = Clamp(busy, 0.0, 100.0);
        }

        private void UpdateMemory(MemoryCounters m)
        {
            double totalGb = m.TotalPhysBytes / BytesPerGb;
            double freeGb = m.AvailPhysBytes / BytesPerGb;

            double usedPercent = 0.0;
            if (m.TotalPhysBytes > 0 && m.TotalPhysBytes > m.AvailPhysBytes)
            {
                ulong usedBytes = m.TotalPhysBytes - m.AvailPhysBytes;
                usedPercent = (double)usedBytes / (double)m.TotalPhysBytes * 100.0;
            }

            _totalRamGb = totalGb;
            _freeRamGb = freeGb;
            _usedRamPercent = Clamp(usedPercent, 0.0, 100.0);
        }

        private static double Clamp(double value, double min, double max)
        {
            if (value < min) return min;
            if (value > max) return max;
            return value;
        }
    }
}
