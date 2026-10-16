using System;

namespace RbxMulti
{
    /// <summary>Equal working-set budgets; pure arithmetic, no process changes.</summary>
    public static class AutoRamPolicy
    {
        public static int PerSessionMb(double totalRamGb, int sessionCount)
        {
            if (sessionCount <= 0 || double.IsNaN(totalRamGb) || double.IsInfinity(totalRamGb)) return 0;
            double budgetMb = Math.Max(0, totalRamGb - HardwareProfile.OsReserveGb) * 1024;
            double share = Math.Min(RamScale.MaxMb, budgetMb / sessionCount);
            // Round down so rounding never overcommits the total budget.
            int mb = (int)(Math.Floor(share / RamScale.StepMb) * RamScale.StepMb);
            // Do not manufacture a minimum allocation when the budget is exhausted.
            return mb >= RamScale.MinMb ? mb : 0;
        }
    }
}
