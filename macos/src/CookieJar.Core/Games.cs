// Games.cs
// Game catalogue and persisted game selection (port of src/Games.cs).
//
// Configure(IAppPaths) must be called once at startup; until then the default game is selected and nothing is
// read or written. The selection is stored in <DataDirectory>/settings.json as "selectedPlaceId".
// Thread safety: state is guarded by a private lock; CurrentChanged is raised outside the lock.
// No secrets are stored in the settings file.

using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using CookieJar.Platform;

namespace CookieJar.Core
{
    public sealed class GameInfo
    {
        public string Name { get; private set; }
        public long PlaceId { get; private set; }
        public string Url { get; private set; }

        public GameInfo(string name, long placeId, string url)
        {
            Name = name;
            PlaceId = placeId;
            Url = url;
        }

        public override string ToString()
        {
            return Name;
        }
    }

    public static class Games
    {
        private const long DefaultPlaceId = 920587237L;
        private const string SettingKey = "selectedPlaceId";
        private const string SettingsFileName = "settings.json";

        private static readonly object _lock = new object();
        private static readonly ReadOnlyCollection<GameInfo> _all;
        private static GameInfo _current;
        private static IAppPaths _paths;

        private static readonly Regex _valueRegex =
            new Regex("\"selectedPlaceId\"\\s*:\\s*\"?(-?\\d+)\"?", RegexOptions.CultureInvariant);

        private static readonly Regex _valueReplaceRegex =
            new Regex("\"selectedPlaceId\"\\s*:\\s*\"?-?\\d+\"?", RegexOptions.CultureInvariant);

        public static event Action CurrentChanged;

        static Games()
        {
            _all = new ReadOnlyCollection<GameInfo>(new List<GameInfo>(new GameInfo[]
            {
                new GameInfo("Adopt Me!", 920587237L, "https://www.roblox.com/games/920587237/Adopt-Me")
            }));
            _current = _all[0];
            Load();
        }

        /// <summary>Sets where settings.json lives and reloads the saved selection from it.</summary>
        public static void Configure(IAppPaths paths)
        {
            lock (_lock)
            {
                _paths = paths;
            }
            Load();
        }

        public static IList<GameInfo> All
        {
            get { return _all; }
        }

        public static GameInfo Current
        {
            get
            {
                lock (_lock)
                {
                    return _current ?? _all[0];
                }
            }
        }

        /// <summary>Full path of settings.json, or null before Configure.</summary>
        public static string SettingsPath
        {
            get
            {
                IAppPaths paths;
                lock (_lock) { paths = _paths; }
                if (paths == null) return null;
                return Path.Combine(paths.DataDirectory, SettingsFileName);
            }
        }

        public static void Select(GameInfo g)
        {
            if (g == null) return;

            bool changed = false;
            lock (_lock)
            {
                GameInfo match = FindByPlaceIdNoLock(g.PlaceId);
                if (match != null && (_current == null || _current.PlaceId != match.PlaceId))
                {
                    _current = match;
                    changed = true;
                }
            }

            if (!changed) return;

            Save();

            Action handler = CurrentChanged;
            if (handler != null) handler();
        }

        public static void SelectByPlaceId(long placeId)
        {
            GameInfo match = FindByPlaceId(placeId);
            if (match != null) Select(match);
        }

        public static GameInfo FindByPlaceId(long placeId)
        {
            lock (_lock)
            {
                return FindByPlaceIdNoLock(placeId);
            }
        }

        private static GameInfo FindByPlaceIdNoLock(long placeId)
        {
            for (int i = 0; i < _all.Count; i++)
            {
                if (_all[i].PlaceId == placeId) return _all[i];
            }
            return null;
        }

        public static void Load()
        {
            try
            {
                long id = DefaultPlaceId;
                string path = SettingsPath;

                if (path != null && File.Exists(path))
                {
                    string text = File.ReadAllText(path, Encoding.UTF8);
                    Match m = _valueRegex.Match(text ?? string.Empty);
                    long parsed;
                    if (m.Success && long.TryParse(m.Groups[1].Value, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out parsed))
                    {
                        id = parsed;
                    }
                }

                lock (_lock)
                {
                    GameInfo match = FindByPlaceIdNoLock(id);
                    _current = match ?? _all[0];
                }
            }
            catch (Exception)
            {
                lock (_lock)
                {
                    _current = _all[0];
                }
            }
        }

        public static void Save()
        {
            long id;
            lock (_lock)
            {
                id = Current.PlaceId;
            }

            string path = null;
            string temp = null;
            try
            {
                path = SettingsPath;
                if (path == null) return;

                string dir = Path.GetDirectoryName(path);
                if (!Directory.Exists(dir)) Directory.CreateDirectory(dir);

                string existing = File.Exists(path) ? File.ReadAllText(path, Encoding.UTF8) : null;
                string output = BuildJson(existing, id);

                temp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
                File.WriteAllText(temp, output, new UTF8Encoding(false));

                if (File.Exists(path))
                {
                    // MAC-UNVERIFIED: File.Replace is assumed to swap the file atomically on Unix as it does on Windows.
                    File.Replace(temp, path, null);
                }
                else
                {
                    File.Move(temp, path);
                }
                temp = null;
            }
            catch (IOException)
            {
            }
            catch (UnauthorizedAccessException)
            {
            }
            finally
            {
                if (temp != null)
                {
                    try
                    {
                        if (File.Exists(temp)) File.Delete(temp);
                    }
                    catch (IOException)
                    {
                    }
                    catch (UnauthorizedAccessException)
                    {
                    }
                }
            }
        }

        // Produces the new file contents. Keeps every other top-level key in the existing text.
        private static string BuildJson(string existing, long id)
        {
            string setting = "\"" + SettingKey + "\":" + id.ToString(CultureInfo.InvariantCulture);

            if (string.IsNullOrEmpty(existing) || existing.IndexOf('{') < 0 || existing.LastIndexOf('}') < 0)
            {
                return "{" + setting + "}";
            }

            if (_valueReplaceRegex.IsMatch(existing))
            {
                return _valueReplaceRegex.Replace(existing, setting, 1);
            }

            int close = existing.LastIndexOf('}');
            string before = existing.Substring(0, close).TrimEnd();
            string after = existing.Substring(close);
            bool emptyObject = before.EndsWith("{", StringComparison.Ordinal);

            return before + (emptyObject ? "" : ",") + setting + after;
        }
    }
}
