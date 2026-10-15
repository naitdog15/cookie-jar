using System;
using System.Collections.Generic;
using System.Globalization;

namespace CookieJar.Core
{
    public enum MemoryPreset
    {
        Unlimited = 0,
        Low = 1,
        Medium = 2,
        High = 3,
        Custom = 4
    }

    public class MemoryAllocation
    {
        public int Pid { get; set; }
        public MemoryPreset Preset { get; set; }
        public int CustomMb { get; set; }
        // Optional display name for warnings.
        public string Label { get; set; }
    }

    /// <summary>
    /// RAM cap maths for Roblox instances. Pure: the hardware snapshot, instance count and free RAM are
    /// passed in, so nothing here touches the OS.
    /// </summary>
    public static class MemoryPolicy
    {
        public const int MinSafeMb = 800;
        public const int CustomMinMb = 128;
        public const int CustomMaxMb = 65536;
        public const int ScaledFloorMb = 600;
        public const int RoundStepMb = 64;
        public const int BudgetFloorMb = 2048;
        public const int CountMin = 1;
        public const int CountMax = 16;
        public const int FallbackRamGb = 4;

        // Suggested cap in MB for a preset, scaled down when the instance count exceeds the budget.
        // Unlimited and Custom have no suggestion (0).
        public static int SuggestedMb(MemoryPreset preset, int instanceCount, HardwareProfile hardware)
        {
            int baseMb = BaseMbFor(preset);
            if (baseMb <= 0) return 0;

            double budget = BudgetMb(hardware);
            int count = ClampInt(instanceCount, CountMin, CountMax);

            double value = baseMb;
            bool scaled = false;
            if (count * (double)baseMb > budget)
            {
                value = baseMb * (budget / (count * (double)baseMb));
                scaled = true;
            }
            if (value < ScaledFloorMb) value = ScaledFloorMb;

            int rounded = RoundToStep(value);
            // Rounding a scaled value down can dip under the floor; bump to the next step.
            if (scaled && rounded < ScaledFloorMb) rounded = ScaledFloorMb + RoundStepMb - (ScaledFloorMb % RoundStepMb);
            return rounded;
        }

        // Cap in MB for a preset or custom value. 0 means no cap.
        public static int ResolveMb(MemoryPreset preset, int customMb, int instanceCount, HardwareProfile hardware)
        {
            if (preset == MemoryPreset.Custom)
            {
                return ClampInt(customMb, CustomMinMb, CustomMaxMb);
            }
            return SuggestedMb(preset, instanceCount, hardware);
        }

        // Safe budget in MB: installed RAM minus the OS reserve, floored at BudgetFloorMb.
        // A null snapshot is treated as the fallback RAM size.
        public static double BudgetMb(HardwareProfile hardware)
        {
            double ramGb = hardware != null ? hardware.RamGb : FallbackRamGb;
            double budget = (ramGb - HardwareProfile.OsReserveGb) * 1024.0;
            if (double.IsNaN(budget) || budget < BudgetFloorMb) return BudgetFloorMb;
            return budget;
        }

        // Human-readable warnings for a set of allocations. Empty list means no problems found.
        // freeRamGb is the free physical RAM at the time of the check.
        public static List<string> ValidateAllocation(IList<MemoryAllocation> items, HardwareProfile hardware, int instanceCount, double freeRamGb)
        {
            List<string> warnings = new List<string>();
            if (items == null || items.Count == 0) return warnings;

            int count = ClampInt(instanceCount, CountMin, CountMax);
            double freeMb = freeRamGb * 1024.0;
            double budgetMb = BudgetMb(hardware);
            double reserveGb = HardwareProfile.OsReserveGb;
            int highMb = SuggestedMb(MemoryPreset.High, count, hardware);

            long totalMb = 0;
            foreach (MemoryAllocation item in items)
            {
                if (item == null) continue;

                int resolved = item.Preset == MemoryPreset.Unlimited
                    ? highMb
                    : ResolveMb(item.Preset, item.CustomMb, count, hardware);
                totalMb += resolved;

                if (resolved > 0 && resolved < MinSafeMb)
                {
                    warnings.Add(string.Format(CultureInfo.InvariantCulture,
                        "{0} is capped at {1} MB, below the {2} MB safe minimum. It will likely stutter.",
                        LabelFor(item), resolved, MinSafeMb));
                }
            }

            if (totalMb > freeMb)
            {
                warnings.Add(string.Format(CultureInfo.InvariantCulture,
                    "Combined caps of {0} MB exceed free RAM of {1:0.0} GB ({2:0} MB). Windows will page memory to disk and the games may stutter.",
                    totalMb, freeMb / 1024.0, freeMb));
            }

            if (totalMb > budgetMb)
            {
                warnings.Add(string.Format(CultureInfo.InvariantCulture,
                    "Combined caps of {0} MB exceed the safe budget of {1:0} MB (installed RAM minus {2:0.0} GB reserved for Windows).",
                    totalMb, budgetMb, reserveGb));
            }

            return warnings;
        }

        private static int BaseMbFor(MemoryPreset preset)
        {
            switch (preset)
            {
                case MemoryPreset.High:
                    return 3584;
                case MemoryPreset.Medium:
                    return 2048;
                case MemoryPreset.Low:
                    return 1200;
                default:
                    return 0;
            }
        }

        private static int RoundToStep(double value)
        {
            return (int)(Math.Round(value / RoundStepMb, MidpointRounding.AwayFromZero) * RoundStepMb);
        }

        private static int ClampInt(int value, int min, int max)
        {
            if (value < min) return min;
            if (value > max) return max;
            return value;
        }

        private static string LabelFor(MemoryAllocation item)
        {
            if (!string.IsNullOrEmpty(item.Label)) return item.Label;
            return string.Format(CultureInfo.InvariantCulture, "PID {0}", item.Pid);
        }
    }
}
