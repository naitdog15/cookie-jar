using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace CookieJar.Platform.Mac
{
    /// <summary>
    /// Single-instance locks and folder reveal for macOS. Never throws.
    /// A lock is an exclusively opened file under the lock directory, held until released or the process exits.
    /// </summary>
    public sealed class MacShellService : IShellService
    {
        // MAC-UNVERIFIED: `open` should return quickly; the timeout only guards against a hung Finder launch.
        private const int OpenTimeoutMs = 10000;

        private readonly ICommandRunner _runner;
        private readonly string _lockDirectory;
        private readonly Dictionary<string, FileStream> _locks = new Dictionary<string, FileStream>(StringComparer.Ordinal);
        private readonly object _sync = new object();

        public MacShellService(ICommandRunner runner, string lockDirectory)
        {
            if (runner == null) throw new ArgumentNullException("runner");
            _runner = runner;
            _lockDirectory = lockDirectory;
        }

        public bool TryAcquireAppInstanceLock(string name)
        {
            string safe = SanitizeName(name);
            if (safe.Length == 0 || string.IsNullOrEmpty(_lockDirectory)) return false;

            lock (_sync)
            {
                if (_locks.ContainsKey(safe)) return false;
                try
                {
                    Directory.CreateDirectory(_lockDirectory);
                    // MAC-UNVERIFIED: .NET on macOS implements FileShare.None with an advisory flock, so a second
                    // process fails to open the file the same way it does on Windows. Only checked on Windows so far.
                    var stream = new FileStream(Path.Combine(_lockDirectory, safe + ".lock"),
                        FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
                    _locks[safe] = stream;
                    return true;
                }
                catch (Exception)
                {
                    // Usually an IOException: another copy holds the lock.
                    return false;
                }
            }
        }

        public void ReleaseAppInstanceLock(string name)
        {
            string safe = SanitizeName(name);
            lock (_sync)
            {
                FileStream stream;
                if (!_locks.TryGetValue(safe, out stream)) return;
                _locks.Remove(safe);
                try { stream.Dispose(); }
                catch (Exception) { }
                try { File.Delete(Path.Combine(_lockDirectory, safe + ".lock")); }
                catch (Exception) { }
            }
        }

        public bool OpenFolder(string path)
        {
            if (string.IsNullOrEmpty(path)) return false;
            try
            {
                return _runner.Run("open", new List<string> { path }, OpenTimeoutMs).Success;
            }
            catch (Exception)
            {
                return false;
            }
        }

        /// <summary>Keeps only [A-Za-z0-9._-]; every other character becomes '_'. Null gives "".</summary>
        public static string SanitizeName(string name)
        {
            if (name == null) return "";
            var sb = new StringBuilder(name.Length);
            foreach (char c in name)
            {
                bool ok = (c >= 'A' && c <= 'Z') || (c >= 'a' && c <= 'z') || (c >= '0' && c <= '9') || c == '.' || c == '_' || c == '-';
                sb.Append(ok ? c : '_');
            }
            return sb.ToString();
        }
    }
}
