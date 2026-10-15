using System;
using System.Collections.Generic;
using System.IO;

namespace RbxMulti
{
    // ---------------------------------------------------------------------------------------------
    // ProfileStore.cs - public API (for the UI team)
    //
    // LaunchProfile (per-profile settings, persisted inside AccountStore):
    //   bool AutoTile                                   - auto-tile accounts across monitors (default true)
    //   Dictionary<string, AccountLayout> Layouts       - accountId -> manual layout override
    //   Dictionary<string, string> RamPresets           - accountId -> "Low" | "Medium" | "High" | "Custom"
    //   Dictionary<string, int> CustomRamMb             - accountId -> custom RAM in MB (256..65536)
    //   AccountLayout GetLayout(string accountId)               - null when no override
    //   void SetLayout(string accountId, AccountLayout layout)  - null layout removes the override
    //   void ClearLayouts()
    //   string GetRamPreset(string accountId)           - default "Medium", normalised
    //   void SetRamPreset(string accountId, string preset)
    //   int GetCustomRamMb(string accountId)            - 0 means unset
    //   void SetCustomRamMb(string accountId, int mb)   - clamped to 256..65536
    //   int ResolveRamMb(string accountId)              - Low 1216, Medium 2048, High 3584 (unscaled, as MemoryGovernor), Custom (or 2048 if unset)
    //   bool HasRamPreset(string accountId) / void ClearRamPreset(string accountId)  - absent entry means "No limit"
    //   List<TileCell> ApplyTiling(List<string> accountIds, List<MonitorInfo> monitors)
    //       AutoTile true : auto cells from WindowManager.ComputeTilingAcrossMonitors, overrides replace them.
    //       AutoTile false: only override cells are returned (in accountIds order).
    //
    // ProfileStore:
    //   SaveLayouts(profileName, source, out error) - persists source's AutoTile/Layouts/RamPresets/
    //       CustomRamMb onto the named profile; all other fields are kept.
    //
    // Depends on TileCell, MonitorInfo and WindowManager.ComputeTilingAcrossMonitors (WindowManager.cs).
    // ---------------------------------------------------------------------------------------------

    /// <summary>A window rectangle on a specific monitor, used as a manual tiling override.</summary>
    public class AccountLayout
    {
        public int MonitorIndex;
        public int X;
        public int Y;
        public int Width;
        public int Height;

        public AccountLayout Clone()
        {
            AccountLayout l = new AccountLayout();
            l.MonitorIndex = MonitorIndex;
            l.X = X;
            l.Y = Y;
            l.Width = Width;
            l.Height = Height;
            return l;
        }
    }

    /// <summary>A named group of accounts that launch together into one place.</summary>
    public class LaunchProfile
    {
        public const long DefaultPlaceId = 920587237;   // Adopt Me
        public const int DefaultStagger = 8;

        public const string RamLow = "Low";
        public const string RamMedium = "Medium";
        public const string RamHigh = "High";
        public const string RamCustom = "Custom";
        public const int MinCustomRamMb = 256;
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

        /// <summary>
        /// Returns one TileCell per account in accountIds order (AutoTile on), or only the override cells (AutoTile off).
        /// A saved layout override always wins over the auto cell for that account.
        /// </summary>
        public List<TileCell> ApplyTiling(List<string> accountIds, List<MonitorInfo> monitors)
        {
            List<TileCell> result = new List<TileCell>();
            if (accountIds == null || accountIds.Count == 0) return result;

            List<TileCell> auto = null;
            if (AutoTile)
            {
                auto = WindowManager.ComputeTilingAcrossMonitors(accountIds.Count, monitors);
            }

            for (int i = 0; i < accountIds.Count; i++)
            {
                string id = accountIds[i];
                AccountLayout layout = GetLayout(id);
                if (layout != null)
                {
                    TileCell cell = new TileCell();
                    cell.MonitorIndex = layout.MonitorIndex;
                    cell.X = layout.X;
                    cell.Y = layout.Y;
                    cell.Width = layout.Width;
                    cell.Height = layout.Height;
                    result.Add(cell);
                }
                else if (AutoTile && auto != null && i < auto.Count)
                {
                    result.Add(auto[i]);
                }
            }
            return result;
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

    /// <summary>
    /// Profiles v2: named groups of accounts, stored inside the encrypted AccountStore file.
    /// Old single-login profile files (profiles\*.profile) are neither read, shown nor deleted.
    /// </summary>
    public static class ProfileStore
    {
        private const int MaxNameLength = 40;

        public static string NormalizeName(string raw, out string error)
        {
            error = null;
            string name = raw == null ? string.Empty : raw.Trim();
            if (name.Length == 0) { error = "Profile name is required."; return null; }
            if (name.Length > MaxNameLength) { error = "Profile name must be " + MaxNameLength + " characters or fewer."; return null; }
            foreach (char c in name)
            {
                if (!(char.IsLetterOrDigit(c) || c == ' ' || c == '-' || c == '_'))
                {
                    error = "Profile names may use letters, digits, spaces, '-' and '_' only.";
                    return null;
                }
            }
            return name;
        }

        public static List<LaunchProfile> List()
        {
            lock (AccountStore.Sync)
            {
                AccountStore.Reload_IfNeeded();
                List<LaunchProfile> list = new List<LaunchProfile>();
                foreach (LaunchProfile p in AccountStore.ProfilesRaw) list.Add(p.Clone());
                list.Sort(delegate(LaunchProfile a, LaunchProfile b) { return string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase); });
                return list;
            }
        }

        public static LaunchProfile Find(string name)
        {
            foreach (LaunchProfile p in List())
            {
                if (string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase)) return p;
            }
            return null;
        }

        /// <summary>Creates a profile, or replaces <paramref name="oldName"/> (rename / edit) when given.</summary>
        public static bool Save(string oldName, LaunchProfile profile, out string error)
        {
            string name = NormalizeName(profile.Name, out error);
            if (name == null) return false;
            if (profile.PlaceId <= 0) { error = "Place id must be a positive number."; return false; }
            if (profile.StaggerSeconds < 0 || profile.StaggerSeconds > 300) { error = "Delay must be between 0 and 300 seconds."; return false; }

            lock (AccountStore.Sync)
            {
                AccountStore.Reload_IfNeeded();
                List<LaunchProfile> next = new List<LaunchProfile>();
                foreach (LaunchProfile p in AccountStore.ProfilesRaw)
                {
                    bool isOld = oldName != null && string.Equals(p.Name, oldName, StringComparison.OrdinalIgnoreCase);
                    if (isOld) continue;
                    if (string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase))
                    {
                        error = "A profile named \"" + p.Name + "\" already exists.";
                        return false;
                    }
                    next.Add(p.Clone());
                }
                LaunchProfile saved = profile.Clone();
                saved.Name = name;
                next.Add(saved);
                return AccountStore.PersistProfiles(next, out error);
            }
        }

        /// <summary>
        /// Persists tiling and RAM settings from <paramref name="source"/> onto the named profile.
        /// AccountIds, PlaceId, StaggerSeconds and Name are kept from the stored profile.
        /// </summary>
        public static bool SaveLayouts(string profileName, LaunchProfile source, out string error)
        {
            error = null;
            if (source == null) { error = "No layout settings supplied."; return false; }

            // One lock for the whole read-modify-write, so two saves cannot interleave and lose an update.
            // (Monitor locks are re-entrant, so Find and Save may take the same lock again.)
            lock (AccountStore.Sync)
            {
                LaunchProfile target = Find(profileName);
                if (target == null) { error = "Profile \"" + profileName + "\" was not found."; return false; }

                target.AutoTile = source.AutoTile;
                target.Layouts = LaunchProfile.CloneLayouts(source.Layouts);
                target.RamPresets = source.RamPresets == null
                    ? new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                    : new Dictionary<string, string>(source.RamPresets, StringComparer.OrdinalIgnoreCase);
                target.CustomRamMb = source.CustomRamMb == null
                    ? new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase)
                    : new Dictionary<string, int>(source.CustomRamMb, StringComparer.OrdinalIgnoreCase);

                return Save(profileName, target, out error);
            }
        }

        public static bool Delete(string name, out string error)
        {
            lock (AccountStore.Sync)
            {
                AccountStore.Reload_IfNeeded();
                List<LaunchProfile> next = new List<LaunchProfile>();
                foreach (LaunchProfile p in AccountStore.ProfilesRaw)
                {
                    if (!string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase)) next.Add(p.Clone());
                }
                return AccountStore.PersistProfiles(next, out error);
            }
        }

        /// <summary>How many old single-login profile files exist. They are left untouched.</summary>
        public static int LegacyFileCount()
        {
            try
            {
                string dir = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "RobloxMultiLauncher", "profiles");
                if (Directory.Exists(dir)) return Directory.GetFiles(dir, "*.profile").Length;
            }
            catch (Exception)
            {
            }
            return 0;
        }
    }
}
