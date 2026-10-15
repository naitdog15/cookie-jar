using System;
using System.IO;
using System.Text;
using CookieJar.Core;

namespace CookieJar.Tests
{
    public static class GamesTests
    {
        private const long AdoptMe = 920587237L;

        public static void Run()
        {
            CatalogueMatchesWindows();
            FindByPlaceIdLookup();
            DefaultSelectionWithoutFile();
            LoadReadsSavedSelection();
            LoadFallsBackOnUnknownOrCorruptFile();
            SaveKeepsOtherKeysAndWritesWithoutBom();
            SaveCreatesFileWhenMissing();
            SelectIgnoresUnknownGames();
            SelectByPlaceIdIgnoresUnknownIds();
            Games.Configure(null);
        }

        private static void CatalogueMatchesWindows()
        {
            T.Check("Games.All has one game", Games.All.Count == 1, Games.All.Count.ToString());
            GameInfo g = Games.All[0];
            T.Check("Games.All[0] is Adopt Me!", g.Name == "Adopt Me!" && g.PlaceId == AdoptMe, g.Name + " " + g.PlaceId);
            T.Check("Games.All[0] url matches Windows", g.Url == "https://www.roblox.com/games/920587237/Adopt-Me", g.Url);
            T.Check("GameInfo.ToString returns the name", g.ToString() == "Adopt Me!");
        }

        private static void FindByPlaceIdLookup()
        {
            GameInfo found = Games.FindByPlaceId(AdoptMe);
            T.Check("FindByPlaceId finds Adopt Me", found != null && found.Name == "Adopt Me!");
            T.Check("FindByPlaceId returns null for an unknown id", Games.FindByPlaceId(1) == null);
            T.Check("FindByPlaceId returns null for zero", Games.FindByPlaceId(0) == null);
        }

        private static void DefaultSelectionWithoutFile()
        {
            using (TempAppPaths paths = new TempAppPaths())
            {
                Games.Configure(paths);
                T.Check("Default selection is All[0] when no settings file exists", Games.Current.PlaceId == AdoptMe);
                T.Check("Default selection is never null", Games.Current != null);
                T.Check("SettingsPath lives in DataDirectory", Games.SettingsPath == Path.Combine(paths.DataDirectory, "settings.json"), Games.SettingsPath);
            }
        }

        private static void LoadReadsSavedSelection()
        {
            using (TempAppPaths paths = new TempAppPaths())
            {
                File.WriteAllText(Path.Combine(paths.DataDirectory, "settings.json"), "{\"selectedPlaceId\":920587237}", Encoding.UTF8);
                Games.Configure(paths);
                T.Check("Load reads selectedPlaceId", Games.Current.PlaceId == AdoptMe);

                File.WriteAllText(Path.Combine(paths.DataDirectory, "settings.json"), "{\"selectedPlaceId\": \"920587237\"}", Encoding.UTF8);
                Games.Load();
                T.Check("Load accepts a quoted id as written by older builds", Games.Current.PlaceId == AdoptMe);
            }
        }

        private static void LoadFallsBackOnUnknownOrCorruptFile()
        {
            using (TempAppPaths paths = new TempAppPaths())
            {
                string file = Path.Combine(paths.DataDirectory, "settings.json");

                File.WriteAllText(file, "{\"selectedPlaceId\":5}", Encoding.UTF8);
                Games.Configure(paths);
                T.Check("Unknown saved id falls back to All[0]", Games.Current.PlaceId == AdoptMe);

                File.WriteAllText(file, "not json at all {", Encoding.UTF8);
                Games.Configure(paths);
                T.Check("Corrupt file falls back to All[0]", Games.Current.PlaceId == AdoptMe);
            }
        }

        private static void SaveKeepsOtherKeysAndWritesWithoutBom()
        {
            using (TempAppPaths paths = new TempAppPaths())
            {
                string file = Path.Combine(paths.DataDirectory, "settings.json");
                File.WriteAllText(file, "{\"theme\":\"dark\",\"selectedPlaceId\":1}", Encoding.UTF8);
                Games.Configure(paths);

                Games.Save();
                string text = File.ReadAllText(file, Encoding.UTF8);
                T.Check("Save replaces selectedPlaceId in place", text == "{\"theme\":\"dark\",\"selectedPlaceId\":920587237}", text);

                byte[] bytes = File.ReadAllBytes(file);
                bool bom = bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF;
                T.Check("Save writes UTF-8 without a BOM", !bom);
                T.Check("Save leaves no temp files behind", Directory.GetFiles(paths.DataDirectory).Length == 1);
            }
        }

        private static void SaveCreatesFileWhenMissing()
        {
            using (TempAppPaths paths = new TempAppPaths())
            {
                Games.Configure(paths);
                Games.Save();
                string file = Path.Combine(paths.DataDirectory, "settings.json");
                T.Check("Save creates settings.json", File.Exists(file));
                T.Check("Save writes the selected place id", File.ReadAllText(file, Encoding.UTF8) == "{\"selectedPlaceId\":920587237}");
            }
        }

        private static void SelectIgnoresUnknownGames()
        {
            using (TempAppPaths paths = new TempAppPaths())
            {
                Games.Configure(paths);
                int raised = 0;
                Action handler = delegate { raised++; };
                Games.CurrentChanged += handler;
                try
                {
                    Games.Select(null);
                    Games.Select(new GameInfo("Other", 42L, "u"));
                    T.Check("Select ignores null and unknown games", Games.Current.PlaceId == AdoptMe && raised == 0, raised.ToString());
                    Games.Select(Games.All[0]);
                    T.Check("Select of the current game raises nothing", raised == 0, raised.ToString());
                }
                finally
                {
                    Games.CurrentChanged -= handler;
                }
            }
        }

        private static void SelectByPlaceIdIgnoresUnknownIds()
        {
            using (TempAppPaths paths = new TempAppPaths())
            {
                Games.Configure(paths);
                Games.SelectByPlaceId(7L);
                T.Check("SelectByPlaceId ignores unknown ids", Games.Current.PlaceId == AdoptMe);
                Games.SelectByPlaceId(AdoptMe);
                T.Check("SelectByPlaceId keeps a known selection", Games.Current.PlaceId == AdoptMe);
            }
        }
    }
}
