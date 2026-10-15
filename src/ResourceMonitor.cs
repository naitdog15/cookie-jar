using System;
using System.Runtime.InteropServices;
using System.Threading;

namespace RbxMulti
{
    /// <summary>
    /// Polls system-wide CPU and RAM usage on a background timer.
    /// Does not depend on the WPF dispatcher. Thread-safe.
    /// </summary>
    public class ResourceMonitor : IDisposable
    {
        private const double BytesPerGb = 1073741824.0;

        // Guards the published values, the running/disposed flags and the timer.
        private readonly object _sync = new object();

        // Serialises sampling so PollOnce is safe from any thread.
        private readonly object _pollLock = new object();

        private readonly int _intervalMs;
        private readonly Timer _timer;

        // Non-zero while a timer-driven poll is in progress (re-entrancy guard).
        private int _pollingFlag;

        private bool _running;
        private bool _disposed;

        // CPU sampling state (guarded by _pollLock).
        private bool _hasPrevCpu;
        private ulong _prevIdle;
        private ulong _prevTotal;

        // Published values (guarded by _sync).
        private double _cpuPercent;
        private double _freeRamGb;
        private double _totalRamGb;
        private double _usedRamPercent;

        public ResourceMonitor() : this(1000)
        {
        }

        public ResourceMonitor(int intervalMs)
        {
            if (intervalMs <= 0)
            {
                throw new ArgumentOutOfRangeException("intervalMs", "Interval must be positive.");
            }

            _intervalMs = intervalMs;
            _timer = new Timer(OnTimerTick, null, Timeout.Infinite, Timeout.Infinite);
        }

        /// <summary>System-wide CPU busy percentage, 0 to 100.</summary>
        public double CpuPercent
        {
            get { lock (_sync) { return _cpuPercent; } }
        }

        /// <summary>Free physical RAM in GiB.</summary>
        public double FreeRamGb
        {
            get { lock (_sync) { return _freeRamGb; } }
        }

        /// <summary>Total physical RAM in GiB.</summary>
        public double TotalRamGb
        {
            get { lock (_sync) { return _totalRamGb; } }
        }

        /// <summary>Used physical RAM as a percentage, 0 to 100.</summary>
        public double UsedRamPercent
        {
            get { lock (_sync) { return _usedRamPercent; } }
        }

        /// <summary>
        /// Raised on the timer thread after each timer-driven poll.
        /// Exceptions thrown by handlers are swallowed.
        /// </summary>
        public event Action Updated;

        /// <summary>Starts periodic polling. Calling Start while running is a no-op.</summary>
        public void Start()
        {
            lock (_sync)
            {
                if (_disposed)
                {
                    throw new ObjectDisposedException("ResourceMonitor");
                }

                if (_running)
                {
                    return;
                }

                _running = true;
                _timer.Change(0, _intervalMs);
            }
        }

        /// <summary>Stops periodic polling. A poll already in progress may finish.</summary>
        public void Stop()
        {
            lock (_sync)
            {
                if (_disposed || !_running)
                {
                    return;
                }

                _running = false;
                _timer.Change(Timeout.Infinite, Timeout.Infinite);
            }
        }

        /// <summary>
        /// Takes one synchronous sample. Usable without Start. Does not raise Updated.
        /// </summary>
        public void PollOnce()
        {
            lock (_pollLock)
            {
                try
                {
                    SampleCpu();
                }
                catch (Exception)
                {
                    // Keep the last known CPU value.
                }

                try
                {
                    SampleRam();
                }
                catch (Exception)
                {
                    // Keep the last known RAM values.
                }
            }
        }

        public void Dispose()
        {
            lock (_sync)
            {
                if (_disposed)
                {
                    return;
                }

                _disposed = true;
                _running = false;
                _timer.Dispose();
            }
        }

        private void OnTimerTick(object state)
        {
            // Skip this tick if the previous poll is still running.
            if (Interlocked.CompareExchange(ref _pollingFlag, 1, 0) != 0)
            {
                return;
            }

            try
            {
                bool active;
                lock (_sync)
                {
                    active = _running && !_disposed;
                }

                if (!active)
                {
                    return;
                }

                PollOnce();
                RaiseUpdated();
            }
            catch (Exception)
            {
                // Never let an exception escape the timer callback.
            }
            finally
            {
                Interlocked.Exchange(ref _pollingFlag, 0);
            }
        }

        private void RaiseUpdated()
        {
            Action handler = Updated;
            if (handler == null)
            {
                return;
            }

            foreach (Delegate d in handler.GetInvocationList())
            {
                try
                {
                    ((Action)d)();
                }
                catch (Exception)
                {
                    // Swallow handler exceptions.
                }
            }
        }

        private void SampleCpu()
        {
            NativeMethods.FILETIME idleFt;
            NativeMethods.FILETIME kernelFt;
            NativeMethods.FILETIME userFt;

            if (!NativeMethods.GetSystemTimes(out idleFt, out kernelFt, out userFt))
            {
                return;
            }

            // Kernel time includes idle time, so busy = (kernel + user - idle) / (kernel + user).
            ulong idle = idleFt.ToUInt64();
            ulong total = kernelFt.ToUInt64() + userFt.ToUInt64();

            if (!_hasPrevCpu || total < _prevTotal || idle < _prevIdle)
            {
                // First sample (or counters went backwards): prime and report 0.
                _hasPrevCpu = true;
                _prevIdle = idle;
                _prevTotal = total;
                StoreCpu(0.0);
                return;
            }

            ulong dTotal = total - _prevTotal;
            ulong dIdle = idle - _prevIdle;
            _prevTotal = total;
            _prevIdle = idle;

            if (dTotal == 0)
            {
                // No new information since the last sample; keep the previous value.
                return;
            }

            double busy = 0.0;
            if (dIdle < dTotal)
            {
                busy = (double)(dTotal - dIdle) / (double)dTotal * 100.0;
            }

            StoreCpu(Clamp(busy, 0.0, 100.0));
        }

        private void SampleRam()
        {
            NativeMethods.MEMORYSTATUSEX status = new NativeMethods.MEMORYSTATUSEX();
            status.dwLength = (uint)Marshal.SizeOf(typeof(NativeMethods.MEMORYSTATUSEX));

            if (!NativeMethods.GlobalMemoryStatusEx(ref status))
            {
                return;
            }

            double totalGb = status.ullTotalPhys / BytesPerGb;
            double freeGb = status.ullAvailPhys / BytesPerGb;

            double usedPercent = 0.0;
            if (status.ullTotalPhys > 0 && status.ullTotalPhys > status.ullAvailPhys)
            {
                ulong usedBytes = status.ullTotalPhys - status.ullAvailPhys;
                usedPercent = (double)usedBytes / (double)status.ullTotalPhys * 100.0;
            }

            lock (_sync)
            {
                _totalRamGb = totalGb;
                _freeRamGb = freeGb;
                _usedRamPercent = Clamp(usedPercent, 0.0, 100.0);
            }
        }

        private void StoreCpu(double value)
        {
            lock (_sync)
            {
                _cpuPercent = value;
            }
        }

        private static double Clamp(double value, double min, double max)
        {
            if (value < min)
            {
                return min;
            }

            if (value > max)
            {
                return max;
            }

            return value;
        }

        private static class NativeMethods
        {
            [StructLayout(LayoutKind.Sequential)]
            internal struct FILETIME
            {
                public uint dwLowDateTime;
                public uint dwHighDateTime;

                public ulong ToUInt64()
                {
                    return ((ulong)dwHighDateTime << 32) | (ulong)dwLowDateTime;
                }
            }

            [StructLayout(LayoutKind.Sequential)]
            internal struct MEMORYSTATUSEX
            {
                public uint dwLength;
                public uint dwMemoryLoad;
                public ulong ullTotalPhys;
                public ulong ullAvailPhys;
                public ulong ullTotalPageFile;
                public ulong ullAvailPageFile;
                public ulong ullTotalVirtual;
                public ulong ullAvailVirtual;
                public ulong ullAvailExtendedVirtual;
            }

            [DllImport("kernel32.dll", SetLastError = true)]
            [return: MarshalAs(UnmanagedType.Bool)]
            internal static extern bool GetSystemTimes(
                out FILETIME lpIdleTime,
                out FILETIME lpKernelTime,
                out FILETIME lpUserTime);

            [DllImport("kernel32.dll", SetLastError = true)]
            [return: MarshalAs(UnmanagedType.Bool)]
            internal static extern bool GlobalMemoryStatusEx(ref MEMORYSTATUSEX lpBuffer);
        }
    }
}
