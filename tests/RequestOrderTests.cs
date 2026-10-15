// RequestOrderTests.cs - AntiIdleClicker.PlanPass: the order of the pass steps. A pending invite (decline) is always
// handled before the CASH OUT button, and each step is planned only when its window is on screen. C# 5.
using System;

namespace RbxMulti
{
    public static class RequestOrderTests
    {
        private static bool SameSteps(string[] got, string[] want)
        {
            if (got == null || got.Length != want.Length) return false;
            for (int i = 0; i < want.Length; i++)
            {
                if (got[i] != want[i]) return false;
            }
            return true;
        }

        private static string Show(string[] steps)
        {
            if (steps == null) return "null";
            if (steps.Length == 0) return "[]";
            return "[" + string.Join(", ", steps) + "]";
        }

        private static void CheckPlan(Action<string, bool, string> check, string name, bool request, bool cashOut, string[] want)
        {
            string[] got = AntiIdleClicker.PlanPass(request, cashOut);
            check("PlanPass " + name + " -> " + Show(want), SameSteps(got, want), "got " + Show(got));
        }

        public static void Run(string dir, Action<string, bool, string> check, Action<string> info)
        {
            Console.WriteLine();
            Console.WriteLine("=== Request order: PlanPass decline / cash-out sequence ===");

            CheckPlan(check, "(request on, cash-out on)", true, true, new string[] { "decline", "cashout" });
            CheckPlan(check, "(request on, cash-out off)", true, false, new string[] { "decline" });
            CheckPlan(check, "(request off, cash-out on)", false, true, new string[] { "cashout" });
            CheckPlan(check, "(request off, cash-out off)", false, false, new string[0]);

            // Explicit ordering guard, independent of the exact truth table above.
            string[] both = AntiIdleClicker.PlanPass(true, true);
            int declineAt = both == null ? -1 : Array.IndexOf(both, "decline");
            int cashAt = both == null ? -1 : Array.IndexOf(both, "cashout");
            check("decline comes before cashout when both are present", declineAt >= 0 && cashAt > declineAt,
                "decline=" + declineAt + " cashout=" + cashAt);

            // No step may appear twice in a pass.
            check("no duplicate steps when both are present", both != null && both.Length == 2 && declineAt != cashAt,
                Show(both));

            info("plan (true,true) = " + Show(both));
        }
    }
}
