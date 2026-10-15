using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;

namespace RbxMulti
{
    /// <summary>A Roblox account whose .ROBLOSECURITY token is kept (DPAPI encrypted on disk).</summary>
    public class Account
    {
        public string Id;
        public string Label;
        public string Username;
        public long UserId;
        public string Token;
        /// <summary>Runtime only, never saved: "Not checked", "Valid", "Expired" or "Error".</summary>
        public string Status = "Not checked";

        public string DisplayName
        {
            get
            {
                if (!string.IsNullOrEmpty(Label)) return Label;
                if (!string.IsNullOrEmpty(Username)) return Username;
                return "Account";
            }
        }

        public Account Clone()
        {
            Account a = new Account();
            a.Id = Id; a.Label = Label; a.Username = Username; a.UserId = UserId; a.Token = Token; a.Status = Status;
            return a;
        }

        // Never include the token in text output.
        public override string ToString()
        {
            return DisplayName;
        }
    }

    /// <summary>Parses what a user pastes into the token box. Pure, no I/O.</summary>
    public static class TokenParser
    {
        public const string Prefix =
            "_|WARNING:-DO-NOT-SHARE-THIS.--Sharing-this-will-allow-someone-to-log-in-as-you-and-to-steal-your-ROBUX-and-items.|_";

        private static readonly Regex BodyPattern = new Regex(@"^[A-Za-z0-9_\-+/=.]{40,4000}$");

        /// <summary>
        /// Returns the normalized cookie value (always with the standard warning prefix), or null with error set.
        /// Accepts the value with or without the prefix, with or without ".ROBLOSECURITY=", quotes or a trailing ';'.
        /// Anything containing whitespace or control characters is rejected (prevents header injection).
        /// </summary>
        public static string Normalize(string raw, out string error)
        {
            error = null;
            string s = raw == null ? "" : raw.Trim();
            if (s.Length == 0)
            {
                error = "Paste the .ROBLOSECURITY value first.";
                return null;
            }

            if (s.StartsWith(".ROBLOSECURITY=", StringComparison.OrdinalIgnoreCase))
            {
                s = s.Substring(".ROBLOSECURITY=".Length).Trim();
            }
            s = s.Trim().TrimEnd(';').Trim().Trim('"', '\'').Trim();

            string body = s;
            if (s.StartsWith("_|WARNING", StringComparison.Ordinal))
            {
                int end = s.LastIndexOf("|_", StringComparison.Ordinal);
                if (end < 0 || end + 2 >= s.Length)
                {
                    error = "That does not look like a full .ROBLOSECURITY value (the long code after the warning text is missing).";
                    return null;
                }
                string head = s.Substring(0, end + 2);
                if (head.IndexOfAny(new char[] { ' ', '\t', '\r', '\n', ';', ',' }) >= 0)
                {
                    error = "That value contains characters a Roblox token never has. Copy it again.";
                    return null;
                }
                body = s.Substring(end + 2);
            }

            if (!BodyPattern.IsMatch(body))
            {
                error = "That does not look like a .ROBLOSECURITY value. Copy the whole value again, without spaces or line breaks.";
                return null;
            }
            return Prefix + body;
        }
    }

    /// <summary>
    /// Accounts and launch profiles in ONE file, encrypted with DPAPI (current user, app entropy):
    /// %LocalAppData%\RobloxMultiLauncher\accounts.dat. Tokens never reach logs or exception messages.
    /// </summary>
    public static class AccountStore
    {
        private const string Magic = "RML2";
        private const int FormatVersion = 3;
        private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("RobloxMultiLauncher/accounts/v2/b41c");

        internal static readonly object Sync = new object();
        internal static List<Account> AccountsRaw = new List<Account>();
        internal static List<LaunchProfile> ProfilesRaw = new List<LaunchProfile>();
        private static bool _loaded;
        private static string _loadError;

        /// <summary>Tests may point the store at a throwaway file.</summary>
        public static string PathOverride;

        public static string FilePath
        {
            get
            {
                if (!string.IsNullOrEmpty(PathOverride)) return PathOverride;
                return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "RobloxMultiLauncher", "accounts.dat");
            }
        }

        /// <summary>Non-null when the file exists but could not be read; saving is then refused so it is never overwritten.</summary>
        public static string LoadError
        {
            get { lock (Sync) { EnsureLoaded(); return _loadError; } }
        }

        public static void Reload()
        {
            lock (Sync)
            {
                _loaded = false;
                EnsureLoaded();
            }
        }

        internal static void Reload_IfNeeded()
        {
            EnsureLoaded();
        }

        private static void EnsureLoaded()
        {
            if (_loaded) return;
            _loaded = true;
            AccountsRaw = new List<Account>();
            ProfilesRaw = new List<LaunchProfile>();
            _loadError = null;
            string path = FilePath;
            if (!File.Exists(path)) return;
            try
            {
                byte[] plain = Dpapi.Unprotect(File.ReadAllBytes(path), Entropy);
                List<Account> accounts;
                List<LaunchProfile> profiles;
                Deserialize(plain, out accounts, out profiles);
                AccountsRaw = accounts;
                ProfilesRaw = profiles;
            }
            catch (Exception)
            {
                _loadError = "The saved accounts file could not be read by this Windows user. It was left untouched and nothing new can be saved until that is resolved.";
            }
        }

        // ---------------- accounts ----------------

        /// <summary>Copies of all accounts, sorted by name. Status values are runtime state.</summary>
        public static List<Account> GetAccounts()
        {
            lock (Sync)
            {
                EnsureLoaded();
                List<Account> list = new List<Account>();
                foreach (Account a in AccountsRaw) list.Add(a.Clone());
                list.Sort(delegate(Account x, Account y) { return string.Compare(x.DisplayName, y.DisplayName, StringComparison.OrdinalIgnoreCase); });
                return list;
            }
        }

        public static Account Find(string id)
        {
            lock (Sync)
            {
                EnsureLoaded();
                foreach (Account a in AccountsRaw)
                {
                    if (a.Id == id) return a.Clone();
                }
                return null;
            }
        }

        /// <summary>Adds an account, or replaces the token of the account with the same Roblox user id.</summary>
        public static bool AddOrUpdate(Account account, out bool updated, out string error)
        {
            updated = false;
            lock (Sync)
            {
                EnsureLoaded();
                List<Account> next = new List<Account>();
                foreach (Account a in AccountsRaw) next.Add(a.Clone());
                Account existing = null;
                foreach (Account a in next)
                {
                    if (a.UserId != 0 && a.UserId == account.UserId) existing = a;
                }
                if (existing != null)
                {
                    existing.Token = account.Token;
                    existing.Username = account.Username;
                    if (!string.IsNullOrEmpty(account.Label)) existing.Label = account.Label;
                    existing.Status = "Valid";
                    updated = true;
                }
                else
                {
                    Account added = account.Clone();
                    if (string.IsNullOrEmpty(added.Id)) added.Id = Guid.NewGuid().ToString("N");
                    next.Add(added);
                }
                if (!Persist(next, ProfilesRaw, out error)) return false;
                AccountsRaw = next;
                return true;
            }
        }

        public static bool Remove(string id, out string error)
        {
            lock (Sync)
            {
                EnsureLoaded();
                List<Account> next = new List<Account>();
                foreach (Account a in AccountsRaw) { if (a.Id != id) next.Add(a); }
                List<LaunchProfile> profiles = new List<LaunchProfile>();
                foreach (LaunchProfile p in ProfilesRaw)
                {
                    LaunchProfile c = p.Clone();
                    c.AccountIds.RemoveAll(delegate(string x) { return x == id; });
                    // Drop stale per-account layout and RAM entries so they cannot be saved forever.
                    if (c.Layouts != null) c.Layouts.Remove(id);
                    if (c.RamPresets != null) c.RamPresets.Remove(id);
                    if (c.CustomRamMb != null) c.CustomRamMb.Remove(id);
                    profiles.Add(c);
                }
                if (!Persist(next, profiles, out error)) return false;
                AccountsRaw = next;
                ProfilesRaw = profiles;
                return true;
            }
        }

        /// <summary>Updates the runtime status (and the username, when given) without touching disk unless the username changed.</summary>
        public static void SetStatus(string id, string status, string username)
        {
            lock (Sync)
            {
                EnsureLoaded();
                foreach (Account a in AccountsRaw)
                {
                    if (a.Id != id) continue;
                    a.Status = status;
                    if (!string.IsNullOrEmpty(username) && username != a.Username)
                    {
                        a.Username = username;
                        string ignored;
                        Persist(AccountsRaw, ProfilesRaw, out ignored);
                    }
                }
            }
        }

        // ---------------- persistence ----------------

        internal static bool PersistProfiles(List<LaunchProfile> next, out string error)
        {
            lock (Sync)
            {
                EnsureLoaded();
                if (!Persist(AccountsRaw, next, out error)) return false;
                ProfilesRaw = next;
                return true;
            }
        }

        private static bool Persist(List<Account> accounts, List<LaunchProfile> profiles, out string error)
        {
            error = null;
            if (_loadError != null)
            {
                error = _loadError;
                return false;
            }
            try
            {
                string path = FilePath;
                string dir = Path.GetDirectoryName(path);
                EnsureFolder(dir);
                byte[] sealedBytes = Dpapi.Protect(Serialize(accounts, profiles), Entropy);
                string tmp = path + ".tmp";
                File.WriteAllBytes(tmp, sealedBytes);
                if (File.Exists(path)) File.Replace(tmp, path, null);
                else File.Move(tmp, path);
                return true;
            }
            catch (Exception)
            {
                // Deliberately not echoing exception text: keeps secrets out of any message.
                error = "Could not save the accounts file (is the disk full or the folder read-only?).";
                return false;
            }
        }

        private static void EnsureFolder(string dir)
        {
            bool existed = Directory.Exists(dir);
            Directory.CreateDirectory(dir);
            if (existed || !string.IsNullOrEmpty(PathOverride)) return;
            try
            {
                System.Security.AccessControl.DirectorySecurity sec = new System.Security.AccessControl.DirectorySecurity();
                sec.SetAccessRuleProtection(true, false);
                sec.AddAccessRule(new System.Security.AccessControl.FileSystemAccessRule(
                    System.Security.Principal.WindowsIdentity.GetCurrent().User,
                    System.Security.AccessControl.FileSystemRights.FullControl,
                    System.Security.AccessControl.InheritanceFlags.ContainerInherit | System.Security.AccessControl.InheritanceFlags.ObjectInherit,
                    System.Security.AccessControl.PropagationFlags.None,
                    System.Security.AccessControl.AccessControlType.Allow));
                new DirectoryInfo(dir).SetAccessControl(sec);
            }
            catch (Exception)
            {
                // Best effort; the content is DPAPI encrypted regardless.
            }
        }

        internal static byte[] Serialize(List<Account> accounts, List<LaunchProfile> profiles)
        {
            using (MemoryStream ms = new MemoryStream())
            {
                using (BinaryWriter w = new BinaryWriter(ms, Encoding.UTF8))
                {
                    w.Write(Magic);
                    w.Write(FormatVersion);
                    w.Write(accounts.Count);
                    foreach (Account a in accounts)
                    {
                        w.Write(a.Id ?? "");
                        w.Write(a.Label ?? "");
                        w.Write(a.Username ?? "");
                        w.Write(a.UserId);
                        w.Write(a.Token ?? "");
                    }
                    w.Write(profiles.Count);
                    foreach (LaunchProfile p in profiles)
                    {
                        w.Write(p.Name ?? "");
                        w.Write(p.PlaceId);
                        w.Write(p.StaggerSeconds);
                        w.Write(p.AccountIds.Count);
                        foreach (string id in p.AccountIds) w.Write(id);

                        // Version 3 extras: auto-tile flag, manual layouts, RAM presets and custom RAM.
                        w.Write(p.AutoTile);

                        int layoutCount = 0;
                        if (p.Layouts != null)
                        {
                            foreach (KeyValuePair<string, AccountLayout> kv in p.Layouts) { if (kv.Value != null) layoutCount++; }
                        }
                        w.Write(layoutCount);
                        if (p.Layouts != null)
                        {
                            foreach (KeyValuePair<string, AccountLayout> kv in p.Layouts)
                            {
                                if (kv.Value == null) continue;
                                w.Write(kv.Key ?? "");
                                w.Write(kv.Value.MonitorIndex);
                                w.Write(kv.Value.X);
                                w.Write(kv.Value.Y);
                                w.Write(kv.Value.Width);
                                w.Write(kv.Value.Height);
                            }
                        }

                        // Merge the keys of RamPresets and CustomRamMb into one list of (id, preset, customMb).
                        Dictionary<string, bool> ramKeys = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
                        if (p.RamPresets != null) { foreach (string k in p.RamPresets.Keys) ramKeys[k ?? ""] = true; }
                        if (p.CustomRamMb != null) { foreach (string k in p.CustomRamMb.Keys) ramKeys[k ?? ""] = true; }
                        w.Write(ramKeys.Count);
                        foreach (string key in ramKeys.Keys)
                        {
                            string preset = "Medium";
                            string storedPreset;
                            if (p.RamPresets != null && p.RamPresets.TryGetValue(key, out storedPreset) && !string.IsNullOrEmpty(storedPreset))
                            {
                                preset = storedPreset;
                            }
                            int customMb = 0;
                            int storedMb;
                            if (p.CustomRamMb != null && p.CustomRamMb.TryGetValue(key, out storedMb)) customMb = storedMb;
                            w.Write(key);
                            w.Write(preset);
                            w.Write(customMb);
                        }
                    }
                }
                return ms.ToArray();
            }
        }

        internal static void Deserialize(byte[] data, out List<Account> accounts, out List<LaunchProfile> profiles)
        {
            accounts = new List<Account>();
            profiles = new List<LaunchProfile>();
            using (BinaryReader r = new BinaryReader(new MemoryStream(data), Encoding.UTF8))
            {
                if (r.ReadString() != Magic) throw new InvalidDataException("bad file");
                int version = r.ReadInt32();
                if (version != 2 && version != 3) throw new InvalidDataException("unsupported version");
                int n = r.ReadInt32();
                for (int i = 0; i < n; i++)
                {
                    Account a = new Account();
                    a.Id = r.ReadString();
                    a.Label = r.ReadString();
                    a.Username = r.ReadString();
                    a.UserId = r.ReadInt64();
                    a.Token = r.ReadString();
                    accounts.Add(a);
                }
                int pc = r.ReadInt32();
                for (int i = 0; i < pc; i++)
                {
                    LaunchProfile p = new LaunchProfile();
                    p.Name = r.ReadString();
                    p.PlaceId = r.ReadInt64();
                    p.StaggerSeconds = r.ReadInt32();
                    int ic = r.ReadInt32();
                    for (int j = 0; j < ic; j++) p.AccountIds.Add(r.ReadString());

                    // Version 2 files have none of the extras: the LaunchProfile defaults stay (AutoTile true, empty maps).
                    if (version >= 3)
                    {
                        p.AutoTile = r.ReadBoolean();

                        int lc = r.ReadInt32();
                        for (int j = 0; j < lc; j++)
                        {
                            string layoutId = r.ReadString();
                            AccountLayout layout = new AccountLayout();
                            layout.MonitorIndex = r.ReadInt32();
                            layout.X = r.ReadInt32();
                            layout.Y = r.ReadInt32();
                            layout.Width = r.ReadInt32();
                            layout.Height = r.ReadInt32();
                            p.Layouts[layoutId] = layout;
                        }

                        int rc = r.ReadInt32();
                        for (int j = 0; j < rc; j++)
                        {
                            string ramId = r.ReadString();
                            string preset = r.ReadString();
                            int customMb = r.ReadInt32();
                            p.RamPresets[ramId] = string.IsNullOrEmpty(preset) ? "Medium" : preset;
                            if (customMb > 0) p.CustomRamMb[ramId] = customMb;
                        }
                    }
                    profiles.Add(p);
                }
            }
        }
    }
}
