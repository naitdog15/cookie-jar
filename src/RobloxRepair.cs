using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Management;
using System.Text;
using Microsoft.Win32;

namespace RbxMulti
{
    /// <summary>
    /// OS boundary for the repair. HKCU only, by construction: it has no HKLM or Program Files method.
    /// Tests replace every member.
    /// </summary>
    internal class RobloxRepairEnvironment
    {
        internal virtual string LocalVersionsRoot
        {
            get
            {
                string r = RobloxLocator.GetLocalRobloxRoot();
                return r == null ? null : Path.Combine(r, "Versions");
            }
        }

        internal virtual string BackupRoot
        {
            get { return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "RobloxMultiLauncher", "backups"); }
        }

        internal virtual DateTime Now { get { return DateTime.Now; } }

        internal virtual bool HkcuKeyExists(string subKey)
        {
            try
            {
                using (RegistryKey b = RegistryKey.OpenBaseKey(RegistryHive.CurrentUser, RegistryView.Registry64))
                using (RegistryKey k = b.OpenSubKey(subKey))
                {
                    return k != null;
                }
            }
            catch (Exception) { return false; }
        }

        internal virtual string ReadHkcuValue(string subKey, string name)
        {
            try
            {
                using (RegistryKey b = RegistryKey.OpenBaseKey(RegistryHive.CurrentUser, RegistryView.Registry64))
                using (RegistryKey k = b.OpenSubKey(subKey))
                {
                    return k == null ? null : k.GetValue(name) as string;
                }
            }
            catch (Exception) { return null; }
        }

        internal virtual bool ExportHkcuKey(string subKey, string regFile, out string error)
        {
            error = null;
            try
            {
                ProcessStartInfo psi = new ProcessStartInfo(Path.Combine(Environment.SystemDirectory, "reg.exe"),
                    "export \"HKCU\\" + subKey + "\" \"" + regFile + "\" /y");
                psi.UseShellExecute = false;
                psi.CreateNoWindow = true;
                psi.RedirectStandardError = true;
                psi.RedirectStandardOutput = true;
                using (Process p = Process.Start(psi))
                {
                    if (p == null) { error = "reg.exe did not start"; return false; }
                    // Drain output so a full pipe cannot block reg.exe.
                    p.StandardOutput.ReadToEnd();
                    string stderr = p.StandardError.ReadToEnd();
                    if (!p.WaitForExit(15000))
                    {
                        error = "reg.exe timed out";
                        return false;
                    }
                    if (p.ExitCode != 0 || !File.Exists(regFile))
                    {
                        error = "reg.exe exit code " + p.ExitCode + (string.IsNullOrEmpty(stderr) ? "" : ": " + stderr.Trim());
                        return false;
                    }
                    return true;
                }
            }
            catch (Exception ex)
            {
                error = ex.Message;
                return false;
            }
        }

        internal virtual bool DeleteHkcuKeyTree(string subKey, out string error)
        {
            error = null;
            try
            {
                using (RegistryKey b = RegistryKey.OpenBaseKey(RegistryHive.CurrentUser, RegistryView.Registry64))
                {
                    b.DeleteSubKeyTree(subKey, false);
                }
                return true;
            }
            catch (Exception ex)
            {
                error = ex.Message;
                return false;
            }
        }

        /// <summary>
        /// Image paths of running RobloxPlayerBeta.exe and RobloxPlayerInstaller.exe. A null entry means a
        /// process whose path could not be read.
        /// </summary>
        internal virtual List<string> RunningRobloxImagePaths()
        {
            List<string> paths = new List<string>();
            try
            {
                using (ManagementObjectSearcher s = new ManagementObjectSearcher(
                    "SELECT ProcessId, ExecutablePath FROM Win32_Process WHERE Name='RobloxPlayerBeta.exe' OR Name='RobloxPlayerInstaller.exe'"))
                using (ManagementObjectCollection rows = s.Get())
                {
                    foreach (ManagementBaseObject row in rows)
                    {
                        paths.Add(row["ExecutablePath"] as string);
                        row.Dispose();
                    }
                }
            }
            catch (Exception)
            {
                paths.Clear();
                paths.Add(null);
            }
            return paths;
        }

        internal virtual bool DirectoryExists(string p) { return Directory.Exists(p); }
        internal virtual bool IsReparsePoint(string p) { return (new DirectoryInfo(p).Attributes & FileAttributes.ReparsePoint) != 0; }
        internal virtual void DeleteDirectory(string p) { Directory.Delete(p, true); }
        internal virtual void CreateDirectory(string p) { Directory.CreateDirectory(p); }
    }

    public sealed class StaleRobloxCopy
    {
        public string Folder;            // e.g. %LOCALAPPDATA%\Roblox\Versions\version-f0ea5da9412f4e35
        public string PlayerExe;
        public Version StaleVersion;
        public string NewestExe;
        public Version NewestVersion;
        public List<string> HkcuKeys = new List<string>();
    }

    public sealed class RobloxCopyAudit
    {
        public List<string> Warnings = new List<string>();   // one line per outdated registration (HKCU or HKLM)
        public StaleRobloxCopy Repairable;                    // null unless a per-user copy can be removed safely
    }

    public enum RepairOutcome { Removed, PartiallyRemoved, Refused, Failed }

    internal static class RobloxRepair
    {
        internal static readonly string[] CandidateKeys = new string[] {
            "Software\\Classes\\roblox-player", "Software\\Classes\\roblox", "Software\\ROBLOX Corporation\\Environments\\roblox-player" };

        private const string VersionPrefix = "version-";

        internal static RobloxCopyAudit Audit(RobloxRepairEnvironment env, List<PlayerCandidate> candidates)
        {
            RobloxCopyAudit audit = new RobloxCopyAudit();
            if (candidates == null) return audit;

            PlayerCandidate newest = null;
            foreach (PlayerCandidate c in candidates)
                if (c != null && c.Version != null && (newest == null || c.Version > newest.Version)) newest = c;
            if (newest == null) return audit;

            foreach (PlayerCandidate c in candidates)
            {
                if (c == null || c.Version == null || c.Version >= newest.Version) continue;
                if (c.Source != "HKCU" && c.Source != "HKLM") continue;
                audit.Warnings.Add("Outdated Roblox copy: " + c.ExePath + " (" + c.Source + ") is " + c.VersionText
                    + "; newest installed is " + newest.ExePath + " (" + newest.VersionText + ")");

                if (audit.Repairable != null || c.Source != "HKCU") continue;
                try
                {
                    string folder = Path.GetDirectoryName(c.ExePath);
                    string newestFolder = Path.GetDirectoryName(newest.ExePath);
                    if (string.IsNullOrEmpty(folder)) continue;
                    if (!IsDirectChildOfRoot(env, folder)) continue;
                    if (!Path.GetFileName(folder).StartsWith(VersionPrefix, StringComparison.OrdinalIgnoreCase)) continue;
                    if (string.Equals(folder, newestFolder, StringComparison.OrdinalIgnoreCase)) continue;
                    if (!env.DirectoryExists(folder) || env.IsReparsePoint(folder)) continue;

                    StaleRobloxCopy plan = new StaleRobloxCopy();
                    plan.Folder = folder;
                    plan.PlayerExe = c.ExePath;
                    plan.StaleVersion = c.Version;
                    plan.NewestExe = newest.ExePath;
                    plan.NewestVersion = newest.Version;
                    foreach (string key in CandidateKeys)
                        if (KeyPointsInto(env, key, folder)) plan.HkcuKeys.Add(key);
                    if (plan.HkcuKeys.Count > 0) audit.Repairable = plan;
                }
                catch (Exception)
                {
                    // An unreadable candidate is simply not repairable.
                }
            }
            return audit;
        }

        internal static bool KeyPointsInto(RobloxRepairEnvironment env, string key, string folder)
        {
            try
            {
                if (!env.HkcuKeyExists(key)) return false;
                List<string> values = new List<string>();
                if (key.StartsWith("Software\\Classes\\", StringComparison.OrdinalIgnoreCase))
                {
                    values.Add(RobloxLocator.ParseCommandPath(env.ReadHkcuValue(key + "\\shell\\open\\command", null)));
                }
                else
                {
                    values.Add(RobloxLocator.ParseCommandPath(env.ReadHkcuValue(key, "clientExe")));
                    values.Add(RobloxLocator.ParseCommandPath(env.ReadHkcuValue(key, null)));
                }
                string prefix = Path.GetFullPath(folder).TrimEnd('\\') + "\\";
                foreach (string v in values)
                {
                    if (string.IsNullOrEmpty(v)) continue;
                    if (Path.GetFullPath(v).StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) return true;
                }
                return false;
            }
            catch (Exception)
            {
                return false;
            }
        }

        internal static bool IsDirectChildOfRoot(RobloxRepairEnvironment env, string folder)
        {
            try
            {
                string rootText = env.LocalVersionsRoot;
                if (rootText == null || string.IsNullOrEmpty(folder)) return false;
                string full = Path.GetFullPath(folder).TrimEnd('\\');
                string parent = Path.GetFullPath(Path.GetDirectoryName(full)).TrimEnd('\\');
                string root = Path.GetFullPath(rootText).TrimEnd('\\');
                return string.Equals(parent, root, StringComparison.OrdinalIgnoreCase);
            }
            catch (Exception)
            {
                return false;
            }
        }

        private static RepairOutcome Refuse(string reason, out string report, out string backupFolder)
        {
            report = "Refused: " + reason;
            backupFolder = null;
            return RepairOutcome.Refused;
        }

        internal static RepairOutcome Execute(RobloxRepairEnvironment env, StaleRobloxCopy plan, out string report, out string backupFolder)
        {
            report = null;
            backupFolder = null;
            try
            {
                // A. Re-validate the plan.
                if (env == null || plan == null || plan.Folder == null || plan.HkcuKeys == null)
                    return Refuse("the repair plan is incomplete", out report, out backupFolder);
                string folder = Path.GetFullPath(plan.Folder).TrimEnd('\\');
                if (!IsDirectChildOfRoot(env, folder))
                    return Refuse(folder + " is not directly inside the per-user Roblox Versions folder", out report, out backupFolder);
                if (!Path.GetFileName(folder).StartsWith(VersionPrefix, StringComparison.OrdinalIgnoreCase))
                    return Refuse(folder + " is not a version-* folder", out report, out backupFolder);
                if (!env.DirectoryExists(folder))
                    return Refuse(folder + " does not exist", out report, out backupFolder);
                if (env.IsReparsePoint(folder))
                    return Refuse(folder + " is a link, not a normal folder", out report, out backupFolder);
                foreach (string key in plan.HkcuKeys)
                {
                    bool allowed = false;
                    foreach (string c in CandidateKeys) if (string.Equals(c, key, StringComparison.OrdinalIgnoreCase)) allowed = true;
                    if (!allowed) return Refuse("registry key " + key + " is not one Cookie Jar may remove", out report, out backupFolder);
                }

                // B. Nothing may be running from that folder.
                string prefix = folder + "\\";
                foreach (string p in env.RunningRobloxImagePaths())
                {
                    if (p == null)
                        return Refuse("a Roblox process is running whose location could not be checked; close Roblox and the Roblox installer first", out report, out backupFolder);
                    if (Path.GetFullPath(p).StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                        return Refuse(p + " is running from that folder", out report, out backupFolder);
                }

                // C. Back up every key before deleting anything.
                string backup = Path.Combine(env.BackupRoot, env.Now.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture));
                env.CreateDirectory(backup);
                backupFolder = backup;
                for (int i = 0; i < plan.HkcuKeys.Count; i++)
                {
                    string key = plan.HkcuKeys[i];
                    string file = Path.Combine(backup, (i + 1).ToString(CultureInfo.InvariantCulture) + "-" + key.Replace('\\', '_').Replace(' ', '_') + ".reg");
                    if (!env.HkcuKeyExists(key)) continue;
                    string err;
                    if (!env.ExportHkcuKey(key, file, out err))
                    {
                        report = "Backup of HKEY_CURRENT_USER\\" + key + " failed (" + err + "); nothing was deleted";
                        return RepairOutcome.Failed;
                    }
                }

                // D and E. Delete the keys, then the folder.
                StringBuilder sb = new StringBuilder();
                int worked = 0, failed = 0;
                foreach (string key in plan.HkcuKeys)
                {
                    string err;
                    if (env.DeleteHkcuKeyTree(key, out err)) { worked++; sb.AppendLine("HKEY_CURRENT_USER\\" + key + ": removed"); }
                    else { failed++; sb.AppendLine("HKEY_CURRENT_USER\\" + key + ": failed: " + err); }
                }
                try
                {
                    env.DeleteDirectory(folder);
                    worked++;
                    sb.AppendLine(folder + ": removed");
                }
                catch (Exception ex)
                {
                    failed++;
                    sb.AppendLine(folder + ": failed: " + ex.Message);
                }
                sb.Append("Backup: " + backup);
                report = sb.ToString();
                if (failed == 0) return RepairOutcome.Removed;
                return worked > 0 ? RepairOutcome.PartiallyRemoved : RepairOutcome.Failed;
            }
            catch (Exception ex)
            {
                report = "Failed: " + ex.Message;
                return RepairOutcome.Failed;
            }
        }
    }
}
