using System;
using System.IO;

namespace RbxMulti
{
    // Exercises production selection with fake files only. No real registry reads/writes or player launches.
    internal static class LocatorTests
    {
        internal static void Run(Action<string, bool> check)
        {
            string root = Path.Combine(AppDomain.CurrentDomain.BaseDirectory,
                "locator fixtures-" + Guid.NewGuid().ToString("N"));
            string localVersions = Path.Combine(root, "Local AppData", "Roblox", "Versions");
            string programVersions = Path.Combine(root, "Program Files", "Roblox", "Versions");
            string x86Versions = Path.Combine(root, "Program Files (x86)", "Roblox", "Versions");
            string registered = Player(Path.Combine(root, "Registered Install", "version-active"), 2020);
            string local = Player(Path.Combine(localVersions, "version-stale"), 2021);
            string program = Player(Path.Combine(programVersions, "version-current"), 2023);
            string x86 = Player(Path.Combine(x86Versions, "version-newest"), 2024);
            string[] roots = new string[] { localVersions, programVersions, x86Versions };
            string[] empty = new string[0];
            string command = "\"" + registered + "\" \"%1\"";

            check("locator: registered player wins over all newer scanned remnants",
                RobloxLocator.ResolveCandidates(new string[] { command }, empty, roots) == registered);

            string launcher = Path.Combine(Path.GetDirectoryName(registered), "RobloxPlayerLauncher.exe");
            File.WriteAllText(launcher, "fake launcher; never executed");
            check("locator: quoted registered launcher resolves its player sibling",
                RobloxLocator.ResolveCandidates(new string[] { "\"" + launcher + "\" \"%1\"" }, empty, roots) == registered);

            check("locator: registration precedence is preserved",
                RobloxLocator.ResolveCandidates(new string[] { command, "\"" + program + "\" %1" }, empty, roots) == registered);
            check("locator: missing protocol entry does not hide a later valid registration",
                RobloxLocator.ResolveCandidates(new string[] { null, "\"" + Path.Combine(root, "missing.exe") + "\" %1", command }, empty, roots) == registered);
            check("locator: malformed command falls through to a registered environment executable",
                RobloxLocator.ResolveCandidates(new string[] { "\"unterminated" }, new string[] { registered }, roots) == registered);
            check("locator: registered environment folder wins over scanned remnants",
                RobloxLocator.ResolveCandidates(empty, new string[] { Path.GetDirectoryName(registered) }, roots) == registered);
            check("locator: registered environment Versions folder resolves a player",
                RobloxLocator.ResolveCandidates(empty, new string[] { programVersions }, roots) == program);

            string bootstrapper = Path.Combine(root, "Bootstrapper Only", "Bloxstrap.exe");
            Directory.CreateDirectory(Path.GetDirectoryName(bootstrapper));
            File.WriteAllText(bootstrapper, "fake bootstrapper; never executed");
            check("locator: bootstrapper without a sibling uses registered environment installation",
                RobloxLocator.ResolveCandidates(new string[] { "\"" + bootstrapper + "\" %1" }, new string[] { registered }, roots) == registered);
            check("locator: bootstrapper is never returned as a player",
                RobloxLocator.ResolveCandidates(new string[] { "\"" + bootstrapper + "\" %1" }, empty, empty) == null);

            check("locator: current-user wrapper environment wins over a shadowed machine player",
                RobloxLocator.ResolveCandidates(new string[] { "\"" + bootstrapper + "\" %1" },
                    new string[] { registered }, new string[] { "\"" + program + "\" %1" }, empty, roots) == registered);
            check("locator: machine registration is used when no user installation resolves",
                RobloxLocator.ResolveCandidates(new string[] { "\"" + bootstrapper + "\" %1" }, empty,
                    new string[] { "\"" + program + "\" %1" }, empty, roots) == program);
            check("locator: machine wrapper environment is used before scanned installations",
                RobloxLocator.ResolveCandidates(empty, empty, new string[] { "\"" + bootstrapper + "\" %1" },
                    new string[] { registered }, roots) == registered);

            check("locator: fallback compares all three standard roots",
                RobloxLocator.ResolveCandidates(empty, empty, roots) == x86);
            check("locator: fallback does not depend on root enumeration order",
                RobloxLocator.ResolveCandidates(empty, empty, new string[] { x86Versions, localVersions, programVersions }) == x86);
            check("locator: missing registrations fall back to all standard roots",
                RobloxLocator.ResolveCandidates(new string[] { "missing.exe %1" }, new string[] { Path.Combine(root, "missing") }, roots) == x86);
            check("locator: missing roots do not hide a later installed player",
                RobloxLocator.ResolveCandidates(empty, empty, new string[] { null, Path.Combine(root, "absent"), localVersions }) == local);
            check("locator: no candidates returns null",
                RobloxLocator.ResolveCandidates(null, null, null) == null);
        }

        private static string Player(string directory, int year)
        {
            Directory.CreateDirectory(directory);
            string path = Path.Combine(directory, "RobloxPlayerBeta.exe");
            File.WriteAllText(path, "fake player; never executed");
            File.SetLastWriteTimeUtc(path, new DateTime(year, 1, 1, 0, 0, 0, DateTimeKind.Utc));
            return path;
        }
    }
}
