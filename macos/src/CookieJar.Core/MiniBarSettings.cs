using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using CookieJar.Platform;

namespace CookieJar.Core
{
    /// <summary>
    /// minibar.settings: key=value lines, UTF-8, in the per-user data directory (port of MainWindow's settings hooks).
    /// Unknown keys are ignored on load and dropped on save. Missing keys keep the defaults below.
    /// </summary>
    public sealed class MiniBarSettings
    {
        public const string FileName = "minibar.settings";

        public const string KeepAliveKey = "keepalive";
        public const string EfficiencyKey = "bgefficiency";
        public const string MiniBarKey = "minibar";
        public const string TrayTipKey = "traytip";

        public const int MinKeepAliveMinutes = 1;
        public const int MaxKeepAliveMinutes = 15;
        public const int DefaultKeepAliveMinutes = 4;
        public const int MinListWaitSeconds = 5;
        public const int MaxListWaitSeconds = 60;
        public const int DefaultListWaitSeconds = 25;

        public const string ClickMethodAuto = "Auto";
        public const string ClickMethodSendInput = "SendInput";
        public const string ClickMethodLegacy = "Legacy";

        // Defaults match the Windows fields: keep-alive off, background efficiency on, mini bar and tray tip off.
        public bool KeepAlive;
        public bool BackgroundEfficiency = true;
        public bool MiniBarOn;
        public bool TrayTipShown;

        public bool KeepSessions = true;
        public int KeepMinutes = DefaultKeepAliveMinutes;
        public bool KeepKey;
        public bool PauseHospital;
        public bool DeclineTrades = true;
        public bool DeclineInvites = true;
        /// <summary>"Auto", "SendInput" or "Legacy".</summary>
        public string ClickMethod = ClickMethodAuto;
        public int ListWaitSeconds = DefaultListWaitSeconds;

        /// <summary>Applies key=value lines on top of the current values. Lines without a key are skipped.</summary>
        public void Load(IEnumerable<string> lines)
        {
            if (lines == null) return;
            foreach (string raw in lines)
            {
                if (raw == null) continue;
                int eq = raw.IndexOf('=');
                if (eq <= 0)
                {
                    continue;
                }
                string key = raw.Substring(0, eq).Trim();
                string value = raw.Substring(eq + 1).Trim();
                bool on = string.Equals(value, "true", StringComparison.OrdinalIgnoreCase);
                if (key == KeepAliveKey)
                {
                    KeepAlive = on;
                }
                else if (key == EfficiencyKey)
                {
                    BackgroundEfficiency = on;
                }
                else if (key == TrayTipKey)
                {
                    TrayTipShown = on;
                }
                else if (key == MiniBarKey)
                {
                    MiniBarOn = on;
                }
                else
                {
                    LoadInputSetting(key, value);
                }
            }
        }

        /// <summary>The file contents in the order the Windows app writes them.</summary>
        public string[] ToLines()
        {
            return new string[]
            {
                KeepAliveKey + "=" + (KeepAlive ? "true" : "false"),
                MiniBarKey + "=" + (MiniBarOn ? "true" : "false"),
                EfficiencyKey + "=" + (BackgroundEfficiency ? "true" : "false"),
                TrayTipKey + "=" + (TrayTipShown ? "true" : "false"),
                "keepsessions=" + (KeepSessions ? "true" : "false"),
                "keepmin=" + KeepMinutes.ToString(CultureInfo.InvariantCulture),
                "keepkey=" + (KeepKey ? "true" : "false"),
                "pausehospital=" + (PauseHospital ? "true" : "false"),
                "declinetrades=" + (DeclineTrades ? "true" : "false"),
                "declineinvites=" + (DeclineInvites ? "true" : "false"),
                "clickmethod=" + ClickMethod,
                "listwait=" + ListWaitSeconds.ToString(CultureInfo.InvariantCulture)
            };
        }

        /// <summary>Reads minibar.settings. A missing or unreadable file gives the defaults. Never throws.</summary>
        public static MiniBarSettings Load(IAppPaths paths)
        {
            MiniBarSettings settings = new MiniBarSettings();
            try
            {
                string path = PathFor(paths);
                if (path == null || !File.Exists(path)) return settings;
                settings.Load(File.ReadAllLines(path, Encoding.UTF8));
            }
            catch (Exception)
            {
                // Defaults stay in force.
            }
            return settings;
        }

        /// <summary>Writes minibar.settings. Returns false on failure; the launcher carries on either way.</summary>
        public bool Save(IAppPaths paths)
        {
            try
            {
                string path = PathFor(paths);
                if (path == null) return false;
                string dir = Path.GetDirectoryName(path);
                if (!string.IsNullOrEmpty(dir))
                {
                    Directory.CreateDirectory(dir);
                }
                File.WriteAllLines(path, ToLines(), Encoding.UTF8);
                return true;
            }
            catch (Exception)
            {
                return false;
            }
        }

        private static string PathFor(IAppPaths paths)
        {
            if (paths == null) return null;
            return Path.Combine(paths.DataDirectory, FileName);
        }

        // Same rules as MainWindow.LoadInputSetting: out-of-range numbers are clamped, unparsable ones keep the default.
        private void LoadInputSetting(string key, string value)
        {
            int n;
            if (key == "keepsessions")
            {
                KeepSessions = !string.Equals(value, "false", StringComparison.OrdinalIgnoreCase);
            }
            else if (key == "keepmin")
            {
                if (int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out n))
                {
                    KeepMinutes = Math.Max(MinKeepAliveMinutes, Math.Min(MaxKeepAliveMinutes, n));
                }
            }
            else if (key == "keepkey")
            {
                KeepKey = string.Equals(value, "true", StringComparison.OrdinalIgnoreCase);
            }
            else if (key == "listwait")
            {
                if (int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out n))
                {
                    ListWaitSeconds = Math.Max(MinListWaitSeconds, Math.Min(MaxListWaitSeconds, n));
                }
            }
            else if (key == "pausehospital")
            {
                PauseHospital = string.Equals(value, "true", StringComparison.OrdinalIgnoreCase);
            }
            else if (key == "declinetrades")
            {
                DeclineTrades = ParseSwitch(value, true);
            }
            else if (key == "declineinvites")
            {
                DeclineInvites = ParseSwitch(value, true);
            }
            else if (key == "clickmethod")
            {
                if (string.Equals(value, ClickMethodSendInput, StringComparison.OrdinalIgnoreCase)) { ClickMethod = ClickMethodSendInput; }
                else if (string.Equals(value, ClickMethodLegacy, StringComparison.OrdinalIgnoreCase)) { ClickMethod = ClickMethodLegacy; }
                else { ClickMethod = ClickMethodAuto; }
            }
        }

        // Same as AntiIdleClicker.ParseSwitch: "true"/"false" (trimmed, any case), anything else gives fallback.
        private static bool ParseSwitch(string value, bool fallback)
        {
            if (value == null) return fallback;
            string t = value.Trim();
            if (string.Equals(t, "true", StringComparison.OrdinalIgnoreCase)) return true;
            if (string.Equals(t, "false", StringComparison.OrdinalIgnoreCase)) return false;
            return fallback;
        }
    }
}
