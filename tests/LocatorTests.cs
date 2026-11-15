using System;
using System.Collections.Generic;
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

            check("locator: with unknown versions, a registered player wins over newer scanned remnants",
                RobloxLocator.ResolveCandidates(new string[] { command }, empty, roots) == registered);

            string launcher = Path.Combine(Path.GetDirectoryName(registered), "RobloxPlayerLauncher.exe");
            File.WriteAllText(launcher, "fake launcher; never executed");
            check("locator: quoted registered launcher resolves its player sibling",
                RobloxLocator.ResolveCandidates(new string[] { "\"" + launcher + "\" \"%1\"" }, empty, roots) == registered);

            check("locator: unknown-version registrations in one hive fall back to the newer file",
                RobloxLocator.ResolveCandidates(new string[] { command, "\"" + program + "\" %1" }, empty, roots) == program);
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

            check("locator: equal/unknown versions prefer HKCU over HKLM",
                RobloxLocator.ResolveCandidates(new string[] { "\"" + bootstrapper + "\" %1" },
                    new string[] { registered }, new string[] { "\"" + program + "\" %1" }, empty, roots) == registered);
            Dictionary<string, Version> wrapperVersions = new Dictionary<string, Version>(StringComparer.OrdinalIgnoreCase);
            wrapperVersions[Path.GetFullPath(registered)] = new Version(0, 746, 0, 7461267);
            wrapperVersions[Path.GetFullPath(program)] = new Version(0, 747, 0, 7471481);
            check("locator: newer machine player beats an older current-user player",
                RobloxLocator.ResolveCandidates(new string[] { "\"" + bootstrapper + "\" %1" },
                    new string[] { registered }, new string[] { "\"" + program + "\" %1" }, empty, roots,
                    VersionMap(wrapperVersions), new List<string>()) == Path.GetFullPath(program));
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
            try { Directory.Delete(root, true); } catch (Exception) { }
            VersionCases(check);
        }

        private static Func<string, Version> VersionMap(Dictionary<string, Version> versions)
        {
            return delegate(string p) { Version v; return versions.TryGetValue(Path.GetFullPath(p), out v) ? v : null; };
        }

        private static string Cmd(string exe) { return "\"" + exe + "\" %1"; }

        private static int CountLines(List<string> log, string prefix)
        {
            int n = 0;
            foreach (string s in log) if (s.StartsWith(prefix, StringComparison.Ordinal)) n++;
            return n;
        }

        private static void VersionCases(Action<string, bool> check)
        {
            string root = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "locator version fixtures-" + Guid.NewGuid().ToString("N"));
            try
            {
                string local = Path.Combine(root, "Local AppData", "Roblox", "Versions");
                string pf = Path.Combine(root, "Program Files", "Roblox", "Versions");
                string x86 = Path.Combine(root, "Program Files (x86)", "Roblox", "Versions");
                string[] roots = new string[] { local, pf, x86 };
                string[] empty = new string[0];
                Version v746 = new Version(0, 746, 0, 7461267), v747 = new Version(0, 747, 0, 7471481);

                string stale = Player(Path.Combine(local, "version-f0ea5da9412f4e35"), 2026);
                string current = Player(Path.Combine(pf, "version-4ea8e85481e34f46"), 2026);
                string old1 = Player(Path.Combine(pf, "version-old1"), 2025);
                string old2 = Player(Path.Combine(pf, "version-old2"), 2025);
                string x86New = Player(Path.Combine(x86, "version-new748"), 2026);
                string unknownScan = Player(Path.Combine(x86, "version-unknown"), 2020);

                Dictionary<string, Version> versions = new Dictionary<string, Version>(StringComparer.OrdinalIgnoreCase);
                Func<string, Version> fake = VersionMap(versions);
                versions[Path.GetFullPath(stale)] = v746;
                versions[Path.GetFullPath(current)] = v747;
                versions[Path.GetFullPath(old1)] = new Version(0, 700, 0, 1);
                versions[Path.GetFullPath(old2)] = new Version(0, 745, 0, 1);
                versions[Path.GetFullPath(x86New)] = new Version(0, 748, 0, 1);

                List<string> log = new List<string>();
                check("locator: HKCU 0.746 + HKLM 0.747 picks HKLM",
                    RobloxLocator.ResolveCandidates(new string[] { Cmd(stale) }, empty, new string[] { Cmd(current) }, empty,
                        new string[] { local, pf }, fake, log) == Path.GetFullPath(current));

                log = new List<string>();
                check("locator: user layout picks 0.747",
                    RobloxLocator.ResolveCandidates(new string[] { Cmd(stale) }, new string[] { stale },
                        new string[] { Cmd(current) }, new string[] { current },
                        new string[] { local, pf, Path.Combine(root, "no x86") }, fake, log) == Path.GetFullPath(current));

                check("locator: scan-only higher version beats a lower registered one",
                    RobloxLocator.ResolveCandidates(new string[] { Cmd(stale) }, empty, empty, empty, new string[] { x86 }, fake, null)
                        == Path.GetFullPath(x86New));

                versions[Path.GetFullPath(old1)] = v747; // equal to current
                check("locator: equal versions: HKCU over HKLM",
                    RobloxLocator.ResolveCandidates(new string[] { Cmd(old1) }, empty, new string[] { Cmd(current) }, empty, empty, fake, null)
                        == Path.GetFullPath(old1));
                check("locator: equal versions: registered over scan",
                    RobloxLocator.ResolveCandidates(new string[] { Cmd(old1) }, empty, empty, empty, new string[] { pf }, fake, null)
                        == Path.GetFullPath(old1));
                versions[Path.GetFullPath(old1)] = new Version(0, 700, 0, 1);

                versions[Path.GetFullPath(unknownScan)] = null;
                Dictionary<string, Version> low = new Dictionary<string, Version>(StringComparer.OrdinalIgnoreCase);
                low[Path.GetFullPath(old2)] = new Version(0, 1, 0, 0);
                check("locator: unknown version loses to any known version",
                    RobloxLocator.ResolveCandidates(new string[] { Cmd(unknownScan) }, empty, empty, empty, new string[] { pf }, VersionMap(low), null)
                        == Path.GetFullPath(old2));

                string[] scanOnly = new string[] { x86 };
                string newerUnknown = Player(Path.Combine(x86, "version-unknown2"), 2024);
                check("locator: all unknown: newest write time wins among scanned players",
                    RobloxLocator.ResolveCandidates(empty, empty, empty, empty, scanOnly, VersionMap(new Dictionary<string, Version>()), null)
                        == Path.GetFullPath(x86New) && File.GetLastWriteTimeUtc(x86New) > File.GetLastWriteTimeUtc(newerUnknown)
                    && File.GetLastWriteTimeUtc(newerUnknown) > File.GetLastWriteTimeUtc(unknownScan));
                check("locator: all unknown: registered first",
                    RobloxLocator.ResolveCandidates(new string[] { Cmd(unknownScan) }, empty, empty, empty, scanOnly,
                        VersionMap(new Dictionary<string, Version>()), null) == Path.GetFullPath(unknownScan));

                log = new List<string>();
                string dotted = Path.Combine(Path.GetDirectoryName(current), ".", "RobloxPlayerBeta.exe");
                string upper = current.ToUpperInvariant();
                string chosen = RobloxLocator.ResolveCandidates(new string[] { Cmd(current) }, empty, new string[] { Cmd(upper), Cmd(dotted) }, empty,
                    new string[] { pf }, fake, log);
                int distinct = CountLines(log, "candidate ");
                check("locator: duplicates are de-duplicated", chosen == Path.GetFullPath(current) && distinct == 3
                    && log.Exists(delegate(string s) { return s.StartsWith("candidate " + Path.GetFullPath(current) + " ", StringComparison.OrdinalIgnoreCase) && s.EndsWith("source=HKCU"); }));

                log = new List<string>();
                chosen = RobloxLocator.ResolveCandidates(new string[] { Cmd(stale) }, empty, new string[] { Cmd(current) }, empty,
                    new string[] { x86 }, fake, log);
                string fullCurrent = Path.GetFullPath(current);
                check("locator: log lists every candidate and chosen",
                    log.Contains("candidate " + fullCurrent + " version=0.747.0.7471481 source=HKLM")
                    && log.Exists(delegate(string s) { return s.StartsWith("candidate " + Path.GetFullPath(unknownScan) + " version=unknown source=scan"); })
                    && log[log.Count - 1] == "chosen " + chosen && chosen == Path.GetFullPath(x86New));

                check("locator: null inputs return null",
                    RobloxLocator.ResolveCandidates(null, null, null, null, null, null, null) == null
                    && RobloxLocator.ResolveCandidates(null, null, null, null, null, fake, null) == null);

                check("locator: version text with commas parses", object.Equals(RobloxLocator.ParseVersionText("0, 747, 0, 7471481"), v747));
                check("locator: version text with trailing words parses",
                    object.Equals(RobloxLocator.ParseVersionText("4.8.9037.0 built by: NET481REL1"), new Version(4, 8, 9037, 0)));
                check("locator: dotted version text parses", object.Equals(RobloxLocator.ParseVersionText("0.747.0.7471481"), v747));
                check("locator: zero, empty and junk version text is unknown",
                    RobloxLocator.ParseVersionText("0.0.0.0") == null && RobloxLocator.ParseVersionText("") == null
                    && RobloxLocator.ParseVersionText("abc") == null);
                check("locator: truncated 16-bit build numbers would sort wrongly", new Version(0, 747, 0, 377) < v747);

                check("locator: real reader reads a system file version",
                    RobloxLocator.ReadFileVersion(Path.Combine(Environment.SystemDirectory, "kernel32.dll")) != null
                    && RobloxLocator.ReadFileVersion(typeof(object).Assembly.Location) != null);
                check("locator: real reader returns null for missing and unversioned files",
                    RobloxLocator.ReadFileVersion(Path.Combine(root, "missing.exe")) == null
                    && RobloxLocator.ReadFileVersion(current) == null);
            }
            finally
            {
                try { Directory.Delete(root, true); } catch (Exception) { }
            }
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
