using System;
using System.Collections.Generic;

namespace CookieJar.Core
{
    /// <summary>Window placement for a profile's accounts. Port of LaunchProfile.ApplyTiling.</summary>
    public static class ProfileTiling
    {
        /// <summary>
        /// Returns one TileCell per account in accountIds order (AutoTile on), or only the override cells (AutoTile off).
        /// A saved layout override always wins over the auto cell for that account.
        /// </summary>
        public static List<TileCell> ApplyTiling(LaunchProfile profile, List<string> accountIds, List<TileMonitor> monitors)
        {
            if (profile == null) throw new ArgumentNullException(nameof(profile));
            List<TileCell> result = new List<TileCell>();
            if (accountIds == null || accountIds.Count == 0) return result;

            List<TileCell> auto = null;
            if (profile.AutoTile)
            {
                auto = Tiling.ComputeTilingAcrossMonitors(accountIds.Count, monitors);
            }

            for (int i = 0; i < accountIds.Count; i++)
            {
                string id = accountIds[i];
                AccountLayout layout = null;
                if (id != null && profile.Layouts != null) profile.Layouts.TryGetValue(id, out layout);
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
                else if (profile.AutoTile && auto != null && i < auto.Count)
                {
                    result.Add(auto[i]);
                }
            }
            return result;
        }
    }
}
