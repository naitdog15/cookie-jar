using System;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using CookieJar.Platform;

namespace CookieJar.Platform.Windows
{
    /// <summary>
    /// One DPAPI-sealed file per key. Its entropy differs from accounts.dat so the two stores cannot read each other.
    /// File names are a SHA-256 of the key, so any key text gives a safe, unique, deterministic name.
    /// </summary>
    public sealed class WinSecretStore : ISecretStore
    {
        private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("RobloxMultiLauncher/secrets/v1");
        private const string FileSuffix = ".secret";

        private readonly string _directory;
        private readonly object _sync = new object();

        public WinSecretStore() : this(new WinAppPaths().DataDirectory)
        {
        }

        public WinSecretStore(string directory)
        {
            if (string.IsNullOrEmpty(directory)) throw new ArgumentException("directory is required", nameof(directory));
            _directory = directory;
        }

        public bool Set(string key, string value)
        {
            if (string.IsNullOrEmpty(key) || value == null) return false;
            string tmp = null;
            try
            {
                byte[] sealedBytes = Dpapi.Protect(Encoding.UTF8.GetBytes(value), Entropy);
                lock (_sync)
                {
                    Directory.CreateDirectory(_directory);
                    string path = PathFor(key);
                    tmp = path + ".tmp";
                    File.WriteAllBytes(tmp, sealedBytes);
                    if (File.Exists(path)) File.Replace(tmp, path, null);
                    else File.Move(tmp, path);
                }
                return true;
            }
            catch (Exception)
            {
                return false;
            }
            finally
            {
                // Only leftovers of a failed write remain here; a successful move leaves nothing behind.
                try { if (tmp != null && File.Exists(tmp)) File.Delete(tmp); } catch (Exception) { }
            }
        }

        public string Get(string key)
        {
            if (string.IsNullOrEmpty(key)) return null;
            try
            {
                byte[] sealedBytes;
                lock (_sync)
                {
                    string path = PathFor(key);
                    if (!File.Exists(path)) return null;
                    sealedBytes = File.ReadAllBytes(path);
                }
                // Wrong entropy or a damaged file makes DPAPI throw, which is reported as "no value".
                return Encoding.UTF8.GetString(Dpapi.Unprotect(sealedBytes, Entropy));
            }
            catch (Exception)
            {
                return null;
            }
        }

        public bool Delete(string key)
        {
            if (string.IsNullOrEmpty(key)) return false;
            try
            {
                lock (_sync)
                {
                    string path = PathFor(key);
                    if (!File.Exists(path)) return false;
                    File.Delete(path);
                    return true;
                }
            }
            catch (Exception)
            {
                return false;
            }
        }

        private string PathFor(string key)
        {
            byte[] hash;
            using (SHA256 sha = SHA256.Create())
            {
                hash = sha.ComputeHash(Encoding.UTF8.GetBytes(key));
            }
            return Path.Combine(_directory, BitConverter.ToString(hash).Replace("-", "").ToLowerInvariant() + FileSuffix);
        }
    }
}
