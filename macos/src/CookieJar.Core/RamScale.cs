using System;
using System.Globalization;

namespace CookieJar.Core
{
    /// <summary>
    /// Scale behind the RAM slider: position 0 is Auto (no cap, the OS decides), positions 1..Steps map to
    /// MinMb..MaxMb in StepMb steps. Pure logic, no UI, so it can be tested without a window.
    /// </summary>
    public static class RamScale
    {
        public const int MinMb = 128;
        public const int MaxMb = 3072;
        public const int StepMb = 64;

        /// <summary>Number of capped positions (128, 192 ... 3072 MB); the slider runs 0..Steps.</summary>
        public const int Steps = (MaxMb - MinMb) / StepMb + 1;

        /// <summary>MB for a slider position; 0 (Auto) for position 0 or below.</summary>
        public static int MbFromIndex(int index)
        {
            if (index <= 0) return 0;
            if (index > Steps) index = Steps;
            return MinMb + (index - 1) * StepMb;
        }

        /// <summary>Nearest slider position for a cap in MB; 0 (Auto) for 0 or below.</summary>
        public static int IndexFromMb(int mb)
        {
            if (mb <= 0) return 0;
            if (mb < MinMb) mb = MinMb;
            if (mb > MaxMb) mb = MaxMb;
            return 1 + (int)Math.Round((mb - MinMb) / (double)StepMb);
        }

        /// <summary>"Auto", "640 MB" or "1.5 GB".</summary>
        public static string Label(int mb)
        {
            if (mb <= 0) return "Auto";
            if (mb < 1024) return mb.ToString(CultureInfo.InvariantCulture) + " MB";
            return (mb / 1024.0).ToString("0.##", CultureInfo.InvariantCulture) + " GB";
        }
    }
}
