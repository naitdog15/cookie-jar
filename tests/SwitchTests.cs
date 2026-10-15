// SwitchTests.cs - AntiIdleClicker switch parsing / serialising and InviteDetector trade vs party-invite selection.
// ParseSwitch must read "true"/"false" (any case) and fall back on garbage or null. SwitchSetting must round-trip
// through ParseSwitch. The trade toggle (decline trades) must match only the trade frame, the invites toggle only the
// party invite frame. C# 5.
using System;
using System.Drawing;
using System.IO;

namespace RbxMulti
{
    public static class SwitchTests
    {
        private static Bitmap Load(string dir, string file)
        {
            return new Bitmap(Path.Combine(dir, file));
        }

        private static void CheckParse(Action<string, bool, string> check, string value, bool fallback, bool expected)
        {
            bool got = AntiIdleClicker.ParseSwitch(value, fallback);
            check("ParseSwitch(" + (value == null ? "null" : "\"" + value + "\"") + ", fallback " + fallback + ") = " + expected,
                got == expected, "got " + got);
        }

        private static void CheckRoundTrip(Action<string, bool, string> check, bool value)
        {
            string text = AntiIdleClicker.SwitchSetting(value);
            check("SwitchSetting(" + value + ") is \"" + (value ? "true" : "false") + "\"",
                text == (value ? "true" : "false"), "got \"" + text + "\"");
            // Round trip must hold whichever fallback is used, since the stored text is never garbage.
            check("SwitchSetting(" + value + ") round-trips through ParseSwitch (fallback true)",
                AntiIdleClicker.ParseSwitch(text, true) == value, "got " + AntiIdleClicker.ParseSwitch(text, true));
            check("SwitchSetting(" + value + ") round-trips through ParseSwitch (fallback false)",
                AntiIdleClicker.ParseSwitch(text, false) == value, "got " + AntiIdleClicker.ParseSwitch(text, false));
        }

        private static void CheckFrame(Action<string, bool, string> check, string name, Bitmap frame,
            bool declineTrades, bool declineInvites, bool expectMatch)
        {
            Rectangle? found = InviteDetector.Find(frame, declineTrades, declineInvites);
            string state = "trades=" + declineTrades + ", invites=" + declineInvites;
            if (expectMatch)
                check(name + " (" + state + "): dismiss button found", found.HasValue, found.HasValue ? found.Value.ToString() : "none");
            else
                check(name + " (" + state + "): no dismiss button", !found.HasValue, found.HasValue ? found.Value.ToString() : "");
        }

        public static void Run(string dir, Action<string, bool, string> check, Action<string> info)
        {
            Console.WriteLine();
            Console.WriteLine("=== Switches: ParseSwitch / SwitchSetting and trade vs invite detection ===");

            // (a) ParseSwitch truth table.
            CheckParse(check, "true", false, true);
            CheckParse(check, "true", true, true);
            CheckParse(check, "false", true, false);
            CheckParse(check, "false", false, false);
            CheckParse(check, "TRUE", false, true);
            CheckParse(check, "FALSE", true, false);
            CheckParse(check, "garbage", true, true);
            CheckParse(check, "garbage", false, false);
            CheckParse(check, null, true, true);
            CheckParse(check, null, false, false);

            // (b) SwitchSetting round trip.
            CheckRoundTrip(check, true);
            CheckRoundTrip(check, false);

            // (c) and (d) InviteDetector switches against the reference frames.
            string blue = "invite-window_blue.png";
            string pink = "invite-window_pink.png";
            if (!File.Exists(Path.Combine(dir, blue)))
                info("skipped trade frame checks: " + blue + " missing");
            else
            {
                using (Bitmap f = Load(dir, blue))
                {
                    // Trade request: only the decline-trades switch may match it.
                    CheckFrame(check, blue, f, true, false, true);
                    CheckFrame(check, blue, f, false, true, false);
                    CheckFrame(check, blue, f, true, true, true);
                }
            }

            if (!File.Exists(Path.Combine(dir, pink)))
                info("skipped party invite frame checks: " + pink + " missing");
            else
            {
                using (Bitmap f = Load(dir, pink))
                {
                    // Party invite: only the decline-invites switch may match it.
                    CheckFrame(check, pink, f, false, true, true);
                    CheckFrame(check, pink, f, true, false, false);
                    CheckFrame(check, pink, f, false, false, false);
                }
            }
        }
    }
}
