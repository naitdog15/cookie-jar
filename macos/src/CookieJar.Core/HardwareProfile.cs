using System;
using System.Globalization;

namespace CookieJar.Core
{
    /// <summary>
    /// Plain snapshot of the machine, filled by an IHardwareProbe. The limits below are derived from it,
    /// so they can be tested with any hand-made snapshot.
    /// </summary>
    public class HardwareProfile
    {
        // RAM kept free for the OS and background apps.
        public const double OsReserveGb = 3.0;

        public int CpuCores { get; set; }
        public int LogicalProcessors { get; set; }
        public double RamGb { get; set; }
        public string CpuName { get; set; }
        public string GpuName { get; set; }
        public double VramGb { get; set; }
        public double FreeRamGb { get; set; }

        // RAM budgeted per running Roblox instance.
        public double PerInstanceRamGb
        {
            get { return 2.0; }
        }

        public int RecommendedMax
        {
            get
            {
                double ramAvailable = RamGb - OsReserveGb;
                int maxFromRam = (int)Math.Floor(ramAvailable / PerInstanceRamGb);
                int limit = Math.Min(CpuCores, maxFromRam);

                // Low-VRAM GPUs (under 4 GB) are limited to 3 instances.
                if (VramGb > 0 && VramGb < 4.0)
                {
                    limit = Math.Min(limit, 3);
                }

                if (limit < 1) limit = 1;
                if (limit > 12) limit = 12;
                return limit;
            }
        }

        public int HardMax
        {
            get
            {
                int hard = RecommendedMax + 2;
                if (hard > 14) hard = 14;
                return hard;
            }
        }

        public string Summary
        {
            get
            {
                return string.Format(CultureInfo.InvariantCulture,
                    "{0} | {1}C/{2}T | {3:0.0} GB RAM | {4} {5:0.0} GB",
                    CpuName, CpuCores, LogicalProcessors, RamGb, GpuName, VramGb);
            }
        }

        /// <summary>
        /// Values a probe starts from before it fills in what it can detect. Used when detection fails,
        /// so the rest of the app still has a usable snapshot.
        /// </summary>
        public static HardwareProfile CreateDefault()
        {
            HardwareProfile profile = new HardwareProfile();
            profile.CpuCores = 1;
            profile.LogicalProcessors = 1;
            profile.RamGb = 4.0;
            profile.CpuName = "Unknown";
            profile.GpuName = "Unknown";
            profile.VramGb = 0.5;
            profile.FreeRamGb = 1.0;
            return profile;
        }
    }

    /// <summary>Reads the machine's hardware. Windows (WMI, registry) and macOS (sysctl, IOKit) each supply one.</summary>
    public interface IHardwareProbe
    {
        HardwareProfile Detect();
    }
}
