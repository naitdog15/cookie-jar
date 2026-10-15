using System;
using System.Collections.Generic;

namespace CookieJar.Core
{
    /// <summary>A named group of accounts that launch together into one place.</summary>
    public class LaunchProfile
    {
        public const long DefaultPlaceId = 920587237;   // Adopt Me
        public const int DefaultStagger = 8;

        public const string RamLow = "Low";
        public const string RamMedium = "Medium";
        public const string RamHigh = "High";
        public const string RamCustom = "Custom";
        public const int MinCustomRamMb = 128;
        public const int MaxCustomRamMb = 65536;

        public string Name;
        public List<string> AccountIds = new List<string>();
        public long PlaceId = DefaultPlaceId;
        public int StaggerSeconds = DefaultStagger;

        public bool AutoTile = true;
        public Dictionary<string, AccountLayout> Layouts = new Dictionary<string, AccountLayout>(StringComparer.OrdinalIgnoreCase); // accountId -> override
        public Dictionary<string, string> RamPresets = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase); // accountId -> "Low"|"Medium"|"High"|"Custom"
        public Dictionary<string, int> CustomRamMb = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

        public LaunchProfile Clone()
        {
            LaunchProfile p = new LaunchProfile();
            p.Name = Name;
            p.AccountIds = new List<string>(AccountIds);
            p.PlaceId = PlaceId;
            p.StaggerSeconds = StaggerSeconds;
            p.AutoTile = AutoTile;
            p.Layouts = CloneLayouts(Layouts);
            p.RamPresets = new Dictionary<string, string>(RamPresets, StringComparer.OrdinalIgnoreCase);
            p.CustomRamMb = new Dictionary<string, int>(CustomRamMb, StringComparer.OrdinalIgnoreCase);
            return p;
        }

        internal static Dictionary<string, AccountLayout> CloneLayouts(Dictionary<string, AccountLayout> source)
        {
            Dictionary<string, AccountLayout> copy = new Dictionary<string, AccountLayout>(StringComparer.OrdinalIgnoreCase);
            if (source == null) return copy;
            foreach (KeyValuePair<string, AccountLayout> kv in source)
            {
                if (kv.Value != null) copy[kv.Key] = kv.Value.Clone();
            }
            return copy;
        }

        public AccountLayout GetLayout(string accountId)
        {
            if (accountId == null || Layouts == null) return null;
            AccountLayout layout;
            if (Layouts.TryGetValue(accountId, out layout)) return layout;
            return null;
        }

        public void SetLayout(string accountId, AccountLayout layout)
        {
            if (accountId == null) return;
            if (Layouts == null) Layouts = new Dictionary<string, AccountLayout>(StringComparer.OrdinalIgnoreCase);
            if (layout == null) Layouts.Remove(accountId);
            else Layouts[accountId] = layout.Clone();
        }

        public void ClearLayouts()
        {
            if (Layouts == null) Layouts = new Dictionary<string, AccountLayout>(StringComparer.OrdinalIgnoreCase);
            Layouts.Clear();
        }

        public string GetRamPreset(string accountId)
        {
            string preset;
            if (accountId != null && RamPresets != null && RamPresets.TryGetValue(accountId, out preset))
            {
                return NormalizePreset(preset);
            }
            return RamMedium;
        }

        public void SetRamPreset(string accountId, string preset)
        {
            if (accountId == null) return;
            if (RamPresets == null) RamPresets = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            RamPresets[accountId] = NormalizePreset(preset);
        }

        /// <summary>True when a RAM preset was explicitly chosen for the account (otherwise: no limit).</summary>
        public bool HasRamPreset(string accountId)
        {
            return accountId != null && RamPresets != null && RamPresets.ContainsKey(accountId);
        }

        /// <summary>Removes the RAM preset and custom value of an account, which means "no limit".</summary>
        public void ClearRamPreset(string accountId)
        {
            if (accountId == null) return;
            if (RamPresets != null) RamPresets.Remove(accountId);
            if (CustomRamMb != null) CustomRamMb.Remove(accountId);
        }

        public int GetCustomRamMb(string accountId)
        {
            int mb;
            if (accountId != null && CustomRamMb != null && CustomRamMb.TryGetValue(accountId, out mb)) return mb;
            return 0;
        }

        public void SetCustomRamMb(string accountId, int mb)
        {
            if (accountId == null) return;
            if (CustomRamMb == null) CustomRamMb = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            if (mb < MinCustomRamMb) mb = MinCustomRamMb;
            if (mb > MaxCustomRamMb) mb = MaxCustomRamMb;
            CustomRamMb[accountId] = mb;
        }

        public int ResolveRamMb(string accountId)
        {
            string preset = GetRamPreset(accountId);
            if (preset == RamLow) return 1216;
            if (preset == RamHigh) return 3584;
            if (preset == RamCustom)
            {
                int custom = GetCustomRamMb(accountId);
                return custom == 0 ? 2048 : custom;
            }
            return 2048;
        }

        private static string NormalizePreset(string preset)
        {
            if (preset == null) return RamMedium;
            string p = preset.Trim();
            if (string.Equals(p, RamLow, StringComparison.OrdinalIgnoreCase)) return RamLow;
            if (string.Equals(p, RamMedium, StringComparison.OrdinalIgnoreCase)) return RamMedium;
            if (string.Equals(p, RamHigh, StringComparison.OrdinalIgnoreCase)) return RamHigh;
            if (string.Equals(p, RamCustom, StringComparison.OrdinalIgnoreCase)) return RamCustom;
            return RamMedium;
        }
    }
}
