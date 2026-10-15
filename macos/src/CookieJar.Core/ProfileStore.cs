using System;
using System.Collections.Generic;
using System.IO;
using CookieJar.Platform;

namespace CookieJar.Core
{
    /// <summary>
    /// Where the profile list is kept. On Windows it lives inside the DPAPI-encrypted accounts file, which is a
    /// platform concern, so Core only sees this seam. Implementations must never throw.
    /// </summary>
    public interface IProfileStorage
    {
        /// <summary>The stored profiles, or an empty list when there are none.</summary>
        List<LaunchProfile> Load();
        /// <summary>Replaces the stored profiles. Returns false with an error message on failure.</summary>
        bool Save(List<LaunchProfile> profiles, out string error);
    }

    /// <summary>
    /// Profiles v2: named groups of accounts. The list itself is persisted through <see cref="IProfileStorage"/>;
    /// the legacy single-login profile files are only counted, never read or deleted.
    /// Configure must be called once at startup (and again in tests).
    /// </summary>
    public static class ProfileStore
    {
        private const int MaxNameLength = 40;

        private static readonly object _sync = new object();
        private static IAppPaths _paths;
        private static IProfileStorage _storage;

        public static void Configure(IAppPaths paths, IProfileStorage storage)
        {
            lock (_sync)
            {
                _paths = paths;
                _storage = storage;
            }
        }

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
            lock (_sync)
            {
                List<LaunchProfile> list = new List<LaunchProfile>();
                foreach (LaunchProfile p in LoadNoLock()) list.Add(p.Clone());
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

            lock (_sync)
            {
                List<LaunchProfile> next = new List<LaunchProfile>();
                foreach (LaunchProfile p in LoadNoLock())
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
                return PersistNoLock(next, out error);
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
            lock (_sync)
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
            lock (_sync)
            {
                List<LaunchProfile> next = new List<LaunchProfile>();
                foreach (LaunchProfile p in LoadNoLock())
                {
                    if (!string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase)) next.Add(p.Clone());
                }
                return PersistNoLock(next, out error);
            }
        }

        /// <summary>How many old single-login profile files exist. They are left untouched.</summary>
        public static int LegacyFileCount()
        {
            try
            {
                IAppPaths paths;
                lock (_sync) { paths = _paths; }
                if (paths == null) return 0;

                string dir = Path.Combine(paths.DataDirectory, "profiles");
                if (Directory.Exists(dir)) return Directory.GetFiles(dir, "*.profile").Length;
            }
            catch (Exception)
            {
            }
            return 0;
        }

        private static List<LaunchProfile> LoadNoLock()
        {
            if (_storage == null) return new List<LaunchProfile>();
            List<LaunchProfile> loaded = _storage.Load();
            return loaded ?? new List<LaunchProfile>();
        }

        private static bool PersistNoLock(List<LaunchProfile> next, out string error)
        {
            if (_storage == null)
            {
                error = "Profile storage is not configured.";
                return false;
            }
            return _storage.Save(next, out error);
        }
    }
}
