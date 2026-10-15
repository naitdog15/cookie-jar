using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Threading;
using CookieJar.Platform;

namespace CookieJar.Platform.Windows
{
    /// <summary>
    /// Named-mutex instance lock (src/Program.cs uses the plain name "RbxMulti.Launcher.SingleInstance",
    /// which is per-session by default) and explorer.exe for folders. Never throws.
    /// </summary>
    public sealed class WinShellService : IShellService
    {
        private readonly object _sync = new object();
        private readonly Dictionary<string, Mutex> _held = new Dictionary<string, Mutex>(StringComparer.Ordinal);

        public bool TryAcquireAppInstanceLock(string name)
        {
            if (string.IsNullOrEmpty(name)) return false;
            try
            {
                lock (_sync)
                {
                    if (_held.ContainsKey(name)) return true;

                    bool createdNew;
                    Mutex mutex = new Mutex(true, name, out createdNew);
                    if (createdNew)
                    {
                        _held[name] = mutex;
                        return true;
                    }

                    // The mutex already exists. Take it if its owner died (abandoned), otherwise another copy holds it.
                    try
                    {
                        if (mutex.WaitOne(0))
                        {
                            _held[name] = mutex;
                            return true;
                        }
                    }
                    catch (AbandonedMutexException)
                    {
                        _held[name] = mutex;
                        return true;
                    }
                    mutex.Dispose();
                    return false;
                }
            }
            catch (Exception)
            {
                return false;
            }
        }

        public void ReleaseAppInstanceLock(string name)
        {
            if (string.IsNullOrEmpty(name)) return;
            try
            {
                lock (_sync)
                {
                    Mutex mutex;
                    if (!_held.TryGetValue(name, out mutex)) return;
                    _held.Remove(name);
                    try { mutex.ReleaseMutex(); } catch (Exception) { }
                    mutex.Dispose();
                }
            }
            catch (Exception)
            {
            }
        }

        public bool OpenFolder(string path)
        {
            if (string.IsNullOrEmpty(path) || !Directory.Exists(path)) return false;
            try
            {
                // UseShellExecute=false keeps this a plain child process; the path is quoted as one argument.
                var psi = new ProcessStartInfo
                {
                    FileName = "explorer.exe",
                    Arguments = "\"" + path + "\"",
                    UseShellExecute = false
                };
                using (Process p = Process.Start(psi))
                {
                    return p != null;
                }
            }
            catch (Exception)
            {
                return false;
            }
        }
    }
}
