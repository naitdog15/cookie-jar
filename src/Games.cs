// Games.cs
// Game catalogue and persisted game selection for RbxMulti.
//
// PUBLIC API (for the UI team)
// ----------------------------
// public sealed class GameInfo
//     string Name            { get; }   display name, e.g. "Adopt Me!"
//     long   PlaceId         { get; }   Roblox place id
//     string Url             { get; }   store/join url
//     GameInfo(string name, long placeId, string url)
//     string ToString()                 returns Name (handy for ComboBox display)
//
// public static class Games
//     IList<GameInfo> All                read-only list of known games (currently only Adopt Me!)
//     GameInfo        Current            currently selected game; never null (falls back to All[0])
//     event Action    CurrentChanged     raised only when the selection actually changes
//     void            Select(GameInfo g) null or unknown game is ignored; a real change is persisted via Save()
//     void            SelectByPlaceId(long placeId)  same as Select, looked up by place id
//     GameInfo        FindByPlaceId(long placeId)    null when no game has that id
//     string          SettingsPath       %LocalAppData%\RobloxMultiLauncher\settings.json
//     void            Load()             reads selectedPlaceId from SettingsPath; missing or corrupt file
//                                        falls back to the default. Never throws. Runs automatically
//                                        from the static constructor.
//     void            Save()             writes selectedPlaceId into SettingsPath. Creates the directory,
//                                        writes UTF-8 without BOM, preserves any other top-level keys
//                                        already in the file, writes via a temp file and File.Replace/Move.
//                                        IOException and UnauthorizedAccessException are swallowed.
//
// Thread safety: all state is guarded by a private lock object. CurrentChanged is raised outside the lock.
// No secrets are stored in the settings file.

using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace RbxMulti
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

        private static readonly object _lock = new object();
        private static readonly ReadOnlyCollection<GameInfo> _all;
        private static GameInfo _current;

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

        public static string SettingsPath
        {
            get
            {
                string local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
                return Path.Combine(Path.Combine(local, "RobloxMultiLauncher"), "settings.json");
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

                if (File.Exists(path))
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
                string dir = Path.GetDirectoryName(path);
                if (!Directory.Exists(dir)) Directory.CreateDirectory(dir);

                string existing = File.Exists(path) ? File.ReadAllText(path, Encoding.UTF8) : null;
                string output = BuildJson(existing, id);

                temp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
                File.WriteAllText(temp, output, new UTF8Encoding(false));

                if (File.Exists(path))
                {
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
