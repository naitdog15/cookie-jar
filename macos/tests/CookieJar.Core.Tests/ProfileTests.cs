using System;
using System.Collections.Generic;
using System.IO;
using CookieJar.Core;
using CookieJar.Platform;

namespace CookieJar.Tests
{
    /// <summary>Per-test data directory under the system temp folder. Removed on Dispose.</summary>
    public sealed class TempAppPaths : IAppPaths, IDisposable
    {
        private readonly string _root;

        public TempAppPaths()
        {
            _root = Path.Combine(Path.GetTempPath(), "cookiejar-core-tests", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_root);
        }

        public string DataDirectory
        {
            get { return Make("data"); }
        }

        public string LogDirectory
        {
            get { return Make("logs"); }
        }

        public string DiagnosticsDirectory
        {
            get { return Make("diagnostics"); }
        }

        public void Dispose()
        {
            try { Directory.Delete(_root, true); } catch (Exception) { }
        }

        private string Make(string name)
        {
            string dir = Path.Combine(_root, name);
            Directory.CreateDirectory(dir);
            return dir;
        }
    }

    /// <summary>In-memory IProfileStorage for tests; ProfileStore clones everything it reads and writes.</summary>
    public sealed class MemoryProfileStorage : IProfileStorage
    {
        public List<LaunchProfile> Stored = new List<LaunchProfile>();

        public List<LaunchProfile> Load()
        {
            return Stored;
        }

        public bool Save(List<LaunchProfile> profiles, out string error)
        {
            Stored = profiles;
            error = null;
            return true;
        }
    }

    public static class ProfileTests
    {
        public static void Run()
        {
            NormalizeNameMessages();
            ResolveRamMbPresets();
            CustomRamClamping();
            LayoutsCloneIsDeep();
            SaveRenameAndDuplicate();
            SaveValidatesPlaceAndDelay();
            SaveLayoutsKeepsIdentity();
            DeleteRemovesProfile();
            LegacyFileCountCountsOnlyProfileFiles();
        }

        private static void NormalizeNameMessages()
        {
            string error;

            string name = ProfileStore.NormalizeName("  My Profile  ", out error);
            T.Check("NormalizeName trims and keeps a valid name", name == "My Profile" && error == null, name);

            name = ProfileStore.NormalizeName("   ", out error);
            T.Check("NormalizeName empty message", name == null && error == "Profile name is required.", error);

            name = ProfileStore.NormalizeName(null, out error);
            T.Check("NormalizeName null is treated as empty", name == null && error == "Profile name is required.", error);

            name = ProfileStore.NormalizeName(new string('a', 41), out error);
            T.Check("NormalizeName too-long message", name == null && error == "Profile name must be 40 characters or fewer.", error);

            name = ProfileStore.NormalizeName(new string('a', 40), out error);
            T.Check("NormalizeName accepts exactly 40 characters", name != null && error == null);

            name = ProfileStore.NormalizeName("bad!name", out error);
            T.Check("NormalizeName invalid-character message", name == null && error == "Profile names may use letters, digits, spaces, '-' and '_' only.", error);

            name = ProfileStore.NormalizeName("ok-name_2 x", out error);
            T.Check("NormalizeName accepts '-', '_', digits and spaces", name == "ok-name_2 x" && error == null, name);
        }

        private static void ResolveRamMbPresets()
        {
            LaunchProfile p = new LaunchProfile();
            const string id = "acc1";

            p.SetRamPreset(id, LaunchProfile.RamLow);
            T.Check("ResolveRamMb Low is 1216", p.ResolveRamMb(id) == 1216, p.ResolveRamMb(id).ToString());

            p.SetRamPreset(id, LaunchProfile.RamMedium);
            T.Check("ResolveRamMb Medium is 2048", p.ResolveRamMb(id) == 2048, p.ResolveRamMb(id).ToString());

            p.SetRamPreset(id, LaunchProfile.RamHigh);
            T.Check("ResolveRamMb High is 3584", p.ResolveRamMb(id) == 3584, p.ResolveRamMb(id).ToString());

            p.SetRamPreset(id, LaunchProfile.RamCustom);
            T.Check("ResolveRamMb Custom without a value is 2048", p.ResolveRamMb(id) == 2048, p.ResolveRamMb(id).ToString());

            p.SetCustomRamMb(id, 4096);
            T.Check("ResolveRamMb Custom uses the stored value", p.ResolveRamMb(id) == 4096, p.ResolveRamMb(id).ToString());

            T.Check("ResolveRamMb unknown account defaults to Medium 2048", new LaunchProfile().ResolveRamMb("nobody") == 2048);
            T.Check("RAM preset normalises case and whitespace", GetPreset(" low ") == LaunchProfile.RamLow);
        }

        private static string GetPreset(string raw)
        {
            LaunchProfile p = new LaunchProfile();
            p.SetRamPreset("a", raw);
            return p.GetRamPreset("a");
        }

        private static void CustomRamClamping()
        {
            LaunchProfile p = new LaunchProfile();
            const string id = "acc1";

            p.SetCustomRamMb(id, 1);
            T.Check("SetCustomRamMb clamps below 128 up to 128", p.GetCustomRamMb(id) == 128, p.GetCustomRamMb(id).ToString());

            p.SetCustomRamMb(id, 999999);
            T.Check("SetCustomRamMb clamps above 65536 down to 65536", p.GetCustomRamMb(id) == 65536, p.GetCustomRamMb(id).ToString());

            p.SetRamPreset(id, LaunchProfile.RamCustom);
            p.SetCustomRamMb(id, 999999);
            T.Check("ResolveRamMb Custom is clamped to 65536", p.ResolveRamMb(id) == 65536, p.ResolveRamMb(id).ToString());

            p.SetCustomRamMb(id, 1);
            T.Check("ResolveRamMb Custom is clamped to 128", p.ResolveRamMb(id) == 128, p.ResolveRamMb(id).ToString());

            p.ClearRamPreset(id);
            T.Check("ClearRamPreset means no limit", !p.HasRamPreset(id) && p.GetCustomRamMb(id) == 0);
        }

        private static void LayoutsCloneIsDeep()
        {
            LaunchProfile p = new LaunchProfile();
            p.Name = "Deep";
            p.AccountIds.Add("a1");
            p.SetLayout("a1", new AccountLayout { MonitorIndex = 1, X = 10, Y = 20, Width = 300, Height = 400 });
            p.SetRamPreset("a1", LaunchProfile.RamHigh);

            LaunchProfile c = p.Clone();
            p.Layouts["a1"].X = 999;
            p.AccountIds.Add("a2");
            p.RamPresets["a1"] = LaunchProfile.RamLow;
            p.CustomRamMb["a1"] = 512;

            T.Check("Clone copies layout values", c.GetLayout("a1") != null && c.GetLayout("a1").X == 10 && c.GetLayout("a1").MonitorIndex == 1);
            T.Check("Clone layout is a separate object", !ReferenceEquals(c.Layouts["a1"], p.Layouts["a1"]));
            T.Check("Clone is unaffected by layout edits", c.Layouts["a1"].X == 10);
            T.Check("Clone account list is independent", c.AccountIds.Count == 1);
            T.Check("Clone RAM preset is independent", c.GetRamPreset("a1") == LaunchProfile.RamHigh);
            T.Check("Clone custom RAM is independent", c.GetCustomRamMb("a1") == 0);

            AccountLayout stored = new AccountLayout { X = 5 };
            p.SetLayout("a2", stored);
            stored.X = 77;
            T.Check("SetLayout stores a copy", p.GetLayout("a2").X == 5);

            p.SetLayout("a2", null);
            T.Check("SetLayout(null) removes the override", p.GetLayout("a2") == null);
        }

        private static void SaveRenameAndDuplicate()
        {
            MemoryProfileStorage storage = new MemoryProfileStorage();
            ProfileStore.Configure(null, storage);
            string error;

            LaunchProfile alpha = new LaunchProfile { Name = "Alpha", AccountIds = new List<string> { "a1" } };
            T.Check("Save creates a profile", ProfileStore.Save(null, alpha, out error) && error == null, error);

            LaunchProfile beta = new LaunchProfile { Name = "Beta" };
            T.Check("Save creates a second profile", ProfileStore.Save(null, beta, out error), error);

            LaunchProfile renamed = new LaunchProfile { Name = "gamma", AccountIds = new List<string> { "a1" } };
            T.Check("Save rename to a free name succeeds", ProfileStore.Save("Alpha", renamed, out error), error);

            List<LaunchProfile> list = ProfileStore.List();
            T.Check("Rename leaves no profile under the old name", ProfileStore.Find("Alpha") == null);
            T.Check("Rename stores the new name (sorted list)", list.Count == 2 && list[0].Name == "Beta" && list[1].Name == "gamma", list.Count.ToString());
            T.Check("Rename keeps the account list", ProfileStore.Find("gamma").AccountIds.Count == 1);

            bool dup = ProfileStore.Save("gamma", new LaunchProfile { Name = "BETA" }, out error);
            T.Check("Rename to an existing name is rejected", !dup && error == "A profile named \"Beta\" already exists.", error);
            T.Check("Rejected rename changes nothing", ProfileStore.Find("gamma") != null && ProfileStore.List().Count == 2);

            T.Check("Find is case-insensitive", ProfileStore.Find("BETA") != null);
        }

        private static void SaveValidatesPlaceAndDelay()
        {
            ProfileStore.Configure(null, new MemoryProfileStorage());
            string error;

            LaunchProfile badPlace = new LaunchProfile { Name = "P", PlaceId = 0 };
            T.Check("Save rejects a non-positive place id", !ProfileStore.Save(null, badPlace, out error) && error == "Place id must be a positive number.", error);

            LaunchProfile badDelay = new LaunchProfile { Name = "P", StaggerSeconds = 301 };
            T.Check("Save rejects a delay above 300", !ProfileStore.Save(null, badDelay, out error) && error == "Delay must be between 0 and 300 seconds.", error);

            LaunchProfile ok = new LaunchProfile { Name = "P", StaggerSeconds = 300 };
            T.Check("Save accepts a delay of 300", ProfileStore.Save(null, ok, out error), error);
        }

        private static void SaveLayoutsKeepsIdentity()
        {
            ProfileStore.Configure(null, new MemoryProfileStorage());
            string error;
            LaunchProfile stored = new LaunchProfile { Name = "Team", PlaceId = 555, StaggerSeconds = 12, AccountIds = new List<string> { "a1", "a2" } };
            ProfileStore.Save(null, stored, out error);

            LaunchProfile source = new LaunchProfile { Name = "ignored", PlaceId = 1, StaggerSeconds = 0, AutoTile = false, AccountIds = new List<string>() };
            source.SetRamPreset("a1", LaunchProfile.RamHigh);
            source.SetLayout("a2", new AccountLayout { X = 3 });

            T.Check("SaveLayouts succeeds", ProfileStore.SaveLayouts("Team", source, out error), error);
            LaunchProfile after = ProfileStore.Find("Team");
            T.Check("SaveLayouts keeps place, delay and accounts", after.PlaceId == 555 && after.StaggerSeconds == 12 && after.AccountIds.Count == 2);
            T.Check("SaveLayouts copies tiling and RAM", !after.AutoTile && after.GetRamPreset("a1") == LaunchProfile.RamHigh && after.GetLayout("a2").X == 3);
            T.Check("SaveLayouts rejects an unknown profile", !ProfileStore.SaveLayouts("Nope", source, out error) && error == "Profile \"Nope\" was not found.", error);
            T.Check("SaveLayouts rejects null source", !ProfileStore.SaveLayouts("Team", null, out error) && error == "No layout settings supplied.", error);
        }

        private static void DeleteRemovesProfile()
        {
            ProfileStore.Configure(null, new MemoryProfileStorage());
            string error;
            ProfileStore.Save(null, new LaunchProfile { Name = "Gone" }, out error);
            T.Check("Delete removes the profile", ProfileStore.Delete("gone", out error) && ProfileStore.Find("Gone") == null, error);
        }

        private static void LegacyFileCountCountsOnlyProfileFiles()
        {
            using (TempAppPaths paths = new TempAppPaths())
            {
                ProfileStore.Configure(paths, new MemoryProfileStorage());
                T.Check("LegacyFileCount is zero without a profiles folder", ProfileStore.LegacyFileCount() == 0);

                string dir = Path.Combine(paths.DataDirectory, "profiles");
                Directory.CreateDirectory(dir);
                File.WriteAllText(Path.Combine(dir, "one.profile"), "x");
                File.WriteAllText(Path.Combine(dir, "two.profile"), "x");
                File.WriteAllText(Path.Combine(dir, "keep.txt"), "x");
                T.Check("LegacyFileCount counts *.profile only", ProfileStore.LegacyFileCount() == 2, ProfileStore.LegacyFileCount().ToString());
                T.Check("LegacyFileCount leaves files untouched", File.Exists(Path.Combine(dir, "one.profile")));
            }
            ProfileStore.Configure(null, null);
        }
    }
}
