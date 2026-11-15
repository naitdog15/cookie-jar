using System;
using System.Collections.Generic;
using System.IO;

namespace RbxMulti
{
    // Fake registry and a temp folder under the test exe only. Never touches the real registry or Roblox folders.
    internal static class RobloxRepairTests
    {
        private static readonly Version Stale = new Version(0, 746, 0, 7461267);
        private static readonly Version Newest = new Version(0, 747, 0, 7471481);

        private sealed class FakeRepairEnvironment : RobloxRepairEnvironment
        {
            internal string Root;
            internal readonly Dictionary<string, Dictionary<string, string>> Registry =
                new Dictionary<string, Dictionary<string, string>>(StringComparer.OrdinalIgnoreCase);
            internal readonly List<string> Deleted = new List<string>();
            internal readonly HashSet<string> FailDelete = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            internal readonly HashSet<string> FailExport = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            internal List<string> Running = new List<string>();

            internal override string LocalVersionsRoot { get { return Path.Combine(Root, "Local", "Roblox", "Versions"); } }
            internal override string BackupRoot { get { return Path.Combine(Root, "backups"); } }
            internal override DateTime Now { get { return new DateTime(2026, 11, 15, 15, 0, 0); } }

            internal override bool HkcuKeyExists(string subKey) { return Registry.ContainsKey(subKey); }
            internal override string ReadHkcuValue(string subKey, string name)
            {
                Dictionary<string, string> values;
                string v;
                if (!Registry.TryGetValue(subKey, out values)) return null;
                return values.TryGetValue(name ?? "", out v) ? v : null;
            }
            internal override bool ExportHkcuKey(string subKey, string regFile, out string error)
            {
                error = null;
                if (FailExport.Contains(subKey)) { error = "simulated export failure"; return false; }
                File.WriteAllText(regFile, "Windows Registry Editor Version 5.00\r\n\r\n[HKEY_CURRENT_USER\\" + subKey + "]\r\n");
                return true;
            }
            internal override bool DeleteHkcuKeyTree(string subKey, out string error)
            {
                error = null;
                if (FailDelete.Contains(subKey)) { error = "simulated delete failure"; return false; }
                List<string> gone = new List<string>();
                foreach (string k in Registry.Keys)
                    if (string.Equals(k, subKey, StringComparison.OrdinalIgnoreCase)
                        || k.StartsWith(subKey + "\\", StringComparison.OrdinalIgnoreCase)) gone.Add(k);
                foreach (string k in gone) Registry.Remove(k);
                Deleted.Add(subKey);
                return true;
            }
            internal override List<string> RunningRobloxImagePaths() { return new List<string>(Running); }

            internal string StaleDir { get { return Path.Combine(LocalVersionsRoot, "version-f0ea5da9412f4e35"); } }
            internal string StaleExe { get { return Path.Combine(StaleDir, "RobloxPlayerBeta.exe"); } }
            internal string StaleInstaller { get { return Path.Combine(StaleDir, "RobloxPlayerInstaller.exe"); } }
            internal string NewestDir { get { return Path.Combine(Root, "PF", "Roblox", "Versions", "version-4ea8e85481e34f46"); } }
            internal string NewestExe { get { return Path.Combine(NewestDir, "RobloxPlayerBeta.exe"); } }
            internal string LogFile { get { return Path.Combine(Root, "Local", "Roblox", "logs", "keep.log"); } }
            internal string SettingsFile { get { return Path.Combine(Root, "Local", "Roblox", "GlobalBasicSettings_13.xml"); } }

            internal void Key(string key, string name, string value)
            {
                Dictionary<string, string> values;
                if (!Registry.TryGetValue(key, out values)) { values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase); Registry[key] = values; }
                values[name] = value;
            }

            internal void Build()
            {
                Directory.CreateDirectory(StaleDir);
                File.WriteAllText(StaleExe, "fake player");
                File.WriteAllText(StaleInstaller, "fake installer");
                Directory.CreateDirectory(NewestDir);
                File.WriteAllText(NewestExe, "fake player");
                Directory.CreateDirectory(Path.GetDirectoryName(LogFile));
                File.WriteAllText(LogFile, "log");
                File.WriteAllText(SettingsFile, "<settings/>");
                string command = "\"" + StaleExe + "\" %1";
                Key("Software\\Classes\\roblox-player\\shell\\open\\command", "", command);
                Key("Software\\Classes\\roblox\\shell\\open\\command", "", command);
                Key("Software\\Classes\\roblox-player", "", "URL: Roblox Protocol");
                Key("Software\\Classes\\roblox", "", "URL: Roblox Protocol");
                Key("Software\\ROBLOX Corporation\\Environments\\roblox-player", "clientExe", StaleExe);
                Key("Software\\ROBLOX Corporation\\Environments\\roblox-player", "", StaleInstaller);
                Key("Software\\ROBLOX Corporation\\Environments\\roblox-player\\Capabilities", "", "caps");
                Key("Software\\ROBLOX Corporation\\Environments\\roblox-studio", "clientExe", Path.Combine(StaleDir, "RobloxStudioBeta.exe"));
            }
        }

        private static PlayerCandidate Cand(string exe, string source, Version v)
        {
            PlayerCandidate c = new PlayerCandidate();
            c.ExePath = exe;
            c.Source = source;
            c.Version = v;
            return c;
        }

        private static FakeRepairEnvironment NewEnv()
        {
            FakeRepairEnvironment env = new FakeRepairEnvironment();
            env.Root = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "repair fixtures-" + Guid.NewGuid().ToString("N"));
            env.Build();
            return env;
        }

        private static List<PlayerCandidate> Standard(FakeRepairEnvironment env)
        {
            return new List<PlayerCandidate> { Cand(env.StaleExe, "HKCU", Stale), Cand(env.NewestExe, "HKLM", Newest) };
        }

        private static void Cleanup(FakeRepairEnvironment env)
        {
            try { Directory.Delete(env.Root, true); } catch (Exception) { }
        }

        internal static void Run(Action<string, bool> check)
        {
            string key1 = RobloxRepair.CandidateKeys[1];

            // R1
            FakeRepairEnvironment env = NewEnv();
            try
            {
                RobloxCopyAudit audit = RobloxRepair.Audit(env, Standard(env));
                check("repair: audit finds the per-user copy with its three keys", audit.Repairable != null
                    && audit.Repairable.Folder == env.StaleDir && audit.Repairable.HkcuKeys.Count == 3);
                check("repair: warnings mention both versions", audit.Warnings.Count == 1
                    && audit.Warnings[0].Contains("0.746.0.7461267") && audit.Warnings[0].Contains("0.747.0.7471481"));
            }
            finally { Cleanup(env); }

            // R2
            env = NewEnv();
            try
            {
                List<PlayerCandidate> equal = new List<PlayerCandidate> { Cand(env.StaleExe, "HKCU", Newest), Cand(env.NewestExe, "HKLM", Newest) };
                RobloxCopyAudit a = RobloxRepair.Audit(env, equal);
                List<PlayerCandidate> newer = new List<PlayerCandidate> { Cand(env.StaleExe, "HKCU", new Version(0, 748, 0, 1)), Cand(env.NewestExe, "HKLM", new Version(0, 748, 0, 1)) };
                RobloxCopyAudit b = RobloxRepair.Audit(env, newer);
                check("repair: equal or newer HKCU copy produces no warning", a.Warnings.Count == 0 && a.Repairable == null
                    && b.Warnings.Count == 0 && b.Repairable == null);
            }
            finally { Cleanup(env); }

            // R3
            env = NewEnv();
            try
            {
                string outside = Path.Combine(env.Root, "PF", "Roblox", "Versions", "version-old", "RobloxPlayerBeta.exe");
                Directory.CreateDirectory(Path.GetDirectoryName(outside));
                File.WriteAllText(outside, "fake");
                RobloxCopyAudit a = RobloxRepair.Audit(env, new List<PlayerCandidate> { Cand(outside, "HKCU", Stale), Cand(env.NewestExe, "HKLM", Newest) });
                RobloxCopyAudit b = RobloxRepair.Audit(env, new List<PlayerCandidate> { Cand(outside, "HKLM", Stale), Cand(env.NewestExe, "HKLM", Newest) });
                check("repair: a copy outside the per-user Versions folder warns but is not repairable",
                    a.Warnings.Count == 1 && a.Repairable == null && b.Warnings.Count == 1 && b.Repairable == null);
            }
            finally { Cleanup(env); }

            // R4
            env = NewEnv();
            try
            {
                env.Key(key1 + "\\shell\\open\\command", "", "\"" + env.NewestExe + "\" %1");
                env.Registry.Remove(RobloxRepair.CandidateKeys[0] + "\\shell\\open\\command");
                RobloxCopyAudit a = RobloxRepair.Audit(env, Standard(env));
                check("repair: keys pointing elsewhere or missing are excluded", a.Repairable != null
                    && a.Repairable.HkcuKeys.Count == 1 && a.Repairable.HkcuKeys[0] == RobloxRepair.CandidateKeys[2]);
                env.Registry.Remove(RobloxRepair.CandidateKeys[2]);
                RobloxCopyAudit b = RobloxRepair.Audit(env, Standard(env));
                check("repair: no qualifying key means nothing to repair", b.Warnings.Count == 1 && b.Repairable == null);
            }
            finally { Cleanup(env); }

            // R5, R6, R7
            string[] runningNames = new string[] { "stale beta", "stale installer", "unreadable path" };
            for (int i = 0; i < 3; i++)
            {
                env = NewEnv();
                try
                {
                    RobloxCopyAudit audit = RobloxRepair.Audit(env, Standard(env));
                    env.Running.Add(i == 0 ? env.StaleExe : (i == 1 ? env.StaleInstaller : null));
                    string report, backup;
                    RepairOutcome outcome = RobloxRepair.Execute(env, audit.Repairable, out report, out backup);
                    check("repair: refused while " + runningNames[i] + " is running", outcome == RepairOutcome.Refused
                        && Directory.Exists(env.StaleDir) && env.Registry.Count == 7 && env.Deleted.Count == 0
                        && !Directory.Exists(env.BackupRoot));
                }
                finally { Cleanup(env); }
            }

            // R8
            env = NewEnv();
            try
            {
                RobloxCopyAudit audit = RobloxRepair.Audit(env, Standard(env));
                env.Running.Add(env.NewestExe);
                string report, backup;
                check("repair: a running newest copy does not block the removal",
                    RobloxRepair.Execute(env, audit.Repairable, out report, out backup) == RepairOutcome.Removed);
            }
            finally { Cleanup(env); }

            // R9
            env = NewEnv();
            try
            {
                RobloxCopyAudit audit = RobloxRepair.Audit(env, Standard(env));
                string report, backup;
                RepairOutcome outcome = RobloxRepair.Execute(env, audit.Repairable, out report, out backup);
                check("repair: success removes the keys and the folder", outcome == RepairOutcome.Removed
                    && !Directory.Exists(env.StaleDir) && env.Deleted.Count == 3
                    && !env.Registry.ContainsKey(RobloxRepair.CandidateKeys[2] + "\\Capabilities")
                    && !env.Registry.ContainsKey(RobloxRepair.CandidateKeys[0] + "\\shell\\open\\command")
                    && report.Contains("Backup: "));
                check("repair: success backs up three .reg files", backup == Path.Combine(env.BackupRoot, "20261115-150000")
                    && Directory.GetFiles(backup, "*.reg").Length == 3);
                check("repair: success leaves Studio, the newest copy, logs and settings alone",
                    env.Registry.ContainsKey("Software\\ROBLOX Corporation\\Environments\\roblox-studio")
                    && File.Exists(env.NewestExe) && File.Exists(env.LogFile) && File.Exists(env.SettingsFile));
            }
            finally { Cleanup(env); }

            // R10
            env = NewEnv();
            try
            {
                RobloxCopyAudit audit = RobloxRepair.Audit(env, Standard(env));
                env.FailExport.Add(key1);
                string report, backup;
                RepairOutcome outcome = RobloxRepair.Execute(env, audit.Repairable, out report, out backup);
                check("repair: a failed backup deletes nothing", outcome == RepairOutcome.Failed && env.Deleted.Count == 0
                    && Directory.Exists(env.StaleDir) && report.Contains("nothing was deleted"));
            }
            finally { Cleanup(env); }

            // R11
            env = NewEnv();
            try
            {
                StaleRobloxCopy good = RobloxRepair.Audit(env, Standard(env)).Repairable;
                StaleRobloxCopy[] bad = new StaleRobloxCopy[3];
                for (int i = 0; i < 3; i++)
                {
                    bad[i] = new StaleRobloxCopy();
                    bad[i].Folder = good.Folder;
                    bad[i].HkcuKeys = new List<string>(good.HkcuKeys);
                }
                bad[0].Folder = env.NewestDir;
                bad[1].Folder = Path.Combine(env.LocalVersionsRoot, "..", "..", "x");
                bad[2].HkcuKeys = new List<string> { "Software\\ROBLOX Corporation\\Environments\\roblox-studio" };
                bool allRefused = true;
                for (int i = 0; i < 3; i++)
                {
                    string report, backup;
                    if (RobloxRepair.Execute(env, bad[i], out report, out backup) != RepairOutcome.Refused) allRefused = false;
                }
                check("repair: plans outside the per-user Versions folder or with foreign keys are refused", allRefused
                    && env.Deleted.Count == 0 && Directory.Exists(env.NewestDir) && Directory.Exists(env.StaleDir));
            }
            finally { Cleanup(env); }

            // R12
            env = NewEnv();
            try
            {
                RobloxCopyAudit audit = RobloxRepair.Audit(env, Standard(env));
                string report, backup;
                RepairOutcome outcome;
                using (new FileStream(env.StaleInstaller, FileMode.Open, FileAccess.Read, FileShare.None))
                {
                    outcome = RobloxRepair.Execute(env, audit.Repairable, out report, out backup);
                }
                check("repair: a locked file gives a partial removal with a failure report", outcome == RepairOutcome.PartiallyRemoved
                    && env.Deleted.Count == 3 && report.Contains("failed"));
            }
            finally { Cleanup(env); }

            // R13
            env = NewEnv();
            try
            {
                RobloxCopyAudit a = RobloxRepair.Audit(env, null);
                RobloxCopyAudit b = RobloxRepair.Audit(env, new List<PlayerCandidate>());
                check("repair: audit with no candidates is empty", a.Warnings.Count == 0 && a.Repairable == null
                    && b.Warnings.Count == 0 && b.Repairable == null);
            }
            finally { Cleanup(env); }
        }
    }
}
