using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;

namespace CookieJar.Platform.Windows
{
    /// <summary>
    /// Starts Roblox clients and handles the single-instance workaround. Launch/LaunchUrl and the
    /// single-instance calls are ported from src/InstanceManager.cs, src/HandleCloser.cs and src/TicketLauncher.cs.
    /// Never throws: failures come back as LaunchResult.Error, false or 0.
    /// </summary>
    public sealed class WinRobloxLauncher : IRobloxLauncher
    {
        private const string PlayerProcessName = "RobloxPlayerBeta";
        private const int PollMs = 250;

        private readonly IRobloxLocator _locator;
        private readonly WinProcessService _processes = new WinProcessService();

        public WinRobloxLauncher() : this(new WinRobloxLocator())
        {
        }

        public WinRobloxLauncher(IRobloxLocator locator)
        {
            _locator = locator ?? new WinRobloxLocator();
        }

        public LaunchResult Launch(IReadOnlyList<string> arguments, int timeoutMs)
        {
            try
            {
                string exe = _locator.FindPlayerPath();
                if (string.IsNullOrEmpty(exe))
                {
                    return Fail("Roblox is not installed.");
                }

                HashSet<int> known = new HashSet<int>(_processes.FindByName(PlayerProcessName));
                ProcessStartInfo psi = new ProcessStartInfo(exe, JoinArguments(arguments));
                psi.UseShellExecute = false;
                psi.WorkingDirectory = Path.GetDirectoryName(exe);

                int startedPid = 0;
                using (Process started = Process.Start(psi))
                {
                    if (started != null)
                    {
                        startedPid = started.Id;
                    }
                }

                int pid = WaitForPlayerPid(known, startedPid, timeoutMs);
                if (pid <= 0)
                {
                    return Fail("Roblox did not start within " + SecondsText(timeoutMs) + " seconds.");
                }
                return new LaunchResult(true, pid, null);
            }
            catch (Exception ex)
            {
                return Fail("Could not start Roblox: " + ex.Message);
            }
        }

        public LaunchResult LaunchUrl(string robloxUrl, int timeoutMs)
        {
            try
            {
                if (!IsRobloxLink(robloxUrl))
                {
                    return Fail("Not a Roblox launch link.");
                }

                HashSet<int> known = new HashSet<int>(_processes.FindByName(PlayerProcessName));

                // Protocol links go through the shell so Windows picks the roblox-player handler (src/InstanceManager.cs).
                ProcessStartInfo psi = new ProcessStartInfo(robloxUrl);
                psi.UseShellExecute = true;
                using (Process starter = Process.Start(psi))
                {
                    // starter may be null when Windows reuses an already running handler; nothing to dispose then.
                }

                int pid = WaitForPlayerPid(known, 0, timeoutMs);
                if (pid <= 0)
                {
                    return Fail("Roblox did not start within " + SecondsText(timeoutMs) + " seconds.");
                }
                return new LaunchResult(true, pid, null);
            }
            catch (Exception)
            {
                return Fail("Windows could not open the roblox-player link. Is Roblox installed?");
            }
        }

        public bool PrepareForAnotherInstance()
        {
            try
            {
                SingleInstanceHandles.CloseSingletonsInAllRoblox();
                return true;
            }
            catch
            {
                return false;
            }
        }

        public int ReleaseSingleInstanceLock(int pid)
        {
            try
            {
                return SingleInstanceHandles.CloseSingletons(pid);
            }
            catch
            {
                return 0;
            }
        }

        public int CountSingleInstanceLocks(int pid)
        {
            try
            {
                return SingleInstanceHandles.CountSingletons(pid);
            }
            catch
            {
                return 0;
            }
        }

        // ---- Helpers -------------------------------------------------------------------------

        private static LaunchResult Fail(string message)
        {
            return new LaunchResult(false, 0, message);
        }

        private static string SecondsText(int timeoutMs)
        {
            return (Math.Max(0, timeoutMs) / 1000).ToString();
        }

        private static bool IsRobloxLink(string url)
        {
            if (string.IsNullOrEmpty(url))
            {
                return false;
            }
            return url.StartsWith("roblox://", StringComparison.OrdinalIgnoreCase)
                || url.StartsWith("roblox-player:", StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>
        /// Polls until the player pid appears: the started pid if it is alive, else a player process that was not
        /// running before the launch. Returns 0 on timeout.
        /// </summary>
        private int WaitForPlayerPid(HashSet<int> known, int preferredPid, int timeoutMs)
        {
            DateTime deadline = DateTime.UtcNow.AddMilliseconds(Math.Max(0, timeoutMs));
            while (true)
            {
                if (preferredPid > 0 && WinProcessService.IsAlive(preferredPid))
                {
                    return preferredPid;
                }

                foreach (int pid in _processes.FindByName(PlayerProcessName))
                {
                    if (!known.Contains(pid))
                    {
                        return pid;
                    }
                }

                if (DateTime.UtcNow >= deadline)
                {
                    return 0;
                }
                Thread.Sleep(PollMs);
            }
        }

        /// <summary>
        /// Windows command-line quoting (MSVCRT rules): quote arguments with spaces, tabs or quotes, and double the
        /// backslashes that precede a quote.
        /// </summary>
        public static string JoinArguments(IReadOnlyList<string> arguments)
        {
            if (arguments == null)
            {
                return string.Empty;
            }

            StringBuilder sb = new StringBuilder();
            foreach (string arg in arguments)
            {
                if (sb.Length > 0)
                {
                    sb.Append(' ');
                }
                sb.Append(QuoteArgument(arg ?? string.Empty));
            }
            return sb.ToString();
        }

        private static string QuoteArgument(string arg)
        {
            if (arg.Length > 0 && arg.IndexOfAny(new char[] { ' ', '\t', '"' }) < 0)
            {
                return arg;
            }

            StringBuilder sb = new StringBuilder();
            sb.Append('"');
            int backslashes = 0;
            foreach (char c in arg)
            {
                if (c == '\\')
                {
                    backslashes++;
                    continue;
                }

                if (c == '"')
                {
                    sb.Append('\\', backslashes * 2 + 1);
                    sb.Append('"');
                }
                else
                {
                    sb.Append('\\', backslashes);
                    sb.Append(c);
                }
                backslashes = 0;
            }
            sb.Append('\\', backslashes * 2);
            sb.Append('"');
            return sb.ToString();
        }
    }

    /// <summary>
    /// Closes the ROBLOX singleton handles (ROBLOX_singletonEvent / ROBLOX_singletonMutex) inside RobloxPlayerBeta
    /// processes. Ported from src/HandleCloser.cs: handles are found through the system extended handle table,
    /// verified on duplicates, and closed in the target with DUPLICATE_CLOSE_SOURCE. Nothing is injected or written.
    /// </summary>
    internal static class SingleInstanceHandles
    {
        // Buffer sizing limits (same as src)
        private const int MaxBufferAttempts = 64;
        private const long MaxSystemInfoBytes = 512L * 1024L * 1024L;
        private const long InitialSystemInfoBytes = 0x100000L;
        private const long InitialObjectInfoBytes = 4096L;
        private const long MaxObjectInfoBytes = 64L * 1024L;
        private const int NameQueryTimeoutMs = 500;

        // Layout of SYSTEM_EXTENDED_HANDLE_TABLE_ENTRY_INFO on x64 (40 bytes):
        // Object @0, UniqueProcessId @8, HandleValue @16, GrantedAccess @24, ...
        private const int HandleEntrySize = 40;

        private const string SingletonEventName = "ROBLOX_singletonEvent";
        private const string SingletonMutexName = "ROBLOX_singletonMutex";
        private const string PlayerProcessName = "RobloxPlayerBeta";

        public static int CloseSingletons(int pid)
        {
            return ProcessSingletons(pid, true);
        }

        public static int CountSingletons(int pid)
        {
            return ProcessSingletons(pid, false);
        }

        /// <summary>Closes singleton handles in all RobloxPlayerBeta processes. Returns the total closed. Never throws.</summary>
        public static int CloseSingletonsInAllRoblox()
        {
            int total = 0;
            Process[] processes;
            try
            {
                processes = Process.GetProcessesByName(PlayerProcessName);
            }
            catch
            {
                return 0;
            }

            foreach (Process proc in processes)
            {
                try
                {
                    total += CloseSingletons(proc.Id);
                }
                catch
                {
                    // Continue with the next process
                }
                finally
                {
                    try
                    {
                        proc.Dispose();
                    }
                    catch
                    {
                        // Ignore dispose failures
                    }
                }
            }
            return total;
        }

        private static int ProcessSingletons(int pid, bool close)
        {
            if (IntPtr.Size != 8 || pid <= 0)
            {
                return 0;
            }

            int result = 0;
            IntPtr sourceProcess = IntPtr.Zero;
            try
            {
                sourceProcess = OpenTargetProcess(pid);
                if (sourceProcess == IntPtr.Zero)
                {
                    return 0;
                }

                List<IntPtr> candidates = ReadHandleValuesForProcess(pid);
                List<IntPtr> matches = new List<IntPtr>();
                foreach (IntPtr candidate in candidates)
                {
                    if (IsSingletonHandle(sourceProcess, candidate))
                    {
                        matches.Add(candidate);
                    }
                }

                if (!close)
                {
                    return matches.Count;
                }

                foreach (IntPtr match in matches)
                {
                    if (CloseSourceHandle(sourceProcess, match))
                    {
                        result++;
                    }
                }
            }
            catch
            {
                // Never let an exception escape
            }
            finally
            {
                if (sourceProcess != IntPtr.Zero)
                {
                    try
                    {
                        Kernel32.CloseHandle(sourceProcess);
                    }
                    catch
                    {
                        // Ignore
                    }
                }
            }
            return result;
        }

        /// <summary>Opens the target with PROCESS_DUP_HANDLE. On failure enables SeDebugPrivilege (non-fatal) and retries once.</summary>
        private static IntPtr OpenTargetProcess(int pid)
        {
            IntPtr handle = Kernel32.OpenProcess(Kernel32.PROCESS_DUP_HANDLE, false, (uint)pid);
            if (handle != IntPtr.Zero)
            {
                return handle;
            }

            try
            {
                EnableSeDebugPrivilege();
            }
            catch
            {
                // Non-fatal
            }

            return Kernel32.OpenProcess(Kernel32.PROCESS_DUP_HANDLE, false, (uint)pid);
        }

        /// <summary>Enables SeDebugPrivilege for the current process. Failures are ignored by callers.</summary>
        private static void EnableSeDebugPrivilege()
        {
            IntPtr hToken;
            if (!Kernel32.OpenProcessToken(Kernel32.GetCurrentProcess(), Kernel32.TOKEN_ADJUST_PRIVILEGES | Kernel32.TOKEN_QUERY, out hToken))
            {
                return;
            }

            try
            {
                Kernel32.LUID luid;
                if (!Kernel32.LookupPrivilegeValueW(null, "SeDebugPrivilege", out luid))
                {
                    return;
                }

                Kernel32.TOKEN_PRIVILEGES tp = new Kernel32.TOKEN_PRIVILEGES();
                tp.PrivilegeCount = 1;
                tp.Luid = luid;
                tp.Attributes = Kernel32.SE_PRIVILEGE_ENABLED;

                Kernel32.AdjustTokenPrivileges(hToken, false, ref tp, (uint)Marshal.SizeOf(typeof(Kernel32.TOKEN_PRIVILEGES)), IntPtr.Zero, IntPtr.Zero);
            }
            finally
            {
                Kernel32.CloseHandle(hToken);
            }
        }

        /// <summary>
        /// Reads the system extended handle table and returns the handle values (in the target's numbering) that
        /// belong to pid. Grows the buffer on size-related statuses, bounded by MaxBufferAttempts and MaxSystemInfoBytes.
        /// </summary>
        private static List<IntPtr> ReadHandleValuesForProcess(int pid)
        {
            List<IntPtr> result = new List<IntPtr>();
            long size = InitialSystemInfoBytes;
            IntPtr buffer = IntPtr.Zero;

            try
            {
                bool ok = false;
                for (int attempt = 0; attempt < MaxBufferAttempts; attempt++)
                {
                    buffer = Marshal.AllocHGlobal(new IntPtr(size));

                    uint returnLength;
                    uint status = Kernel32.NtQuerySystemInformation(Kernel32.SYSTEM_EXTENDED_HANDLE_INFORMATION, buffer, (uint)size, out returnLength);
                    if (status == Kernel32.STATUS_SUCCESS)
                    {
                        ok = true;
                        break;
                    }

                    Marshal.FreeHGlobal(buffer);
                    buffer = IntPtr.Zero;

                    if (!IsBufferSizeStatus(status))
                    {
                        return result;
                    }

                    long next = Math.Max((long)returnLength + 0x10000L, size * 2L);
                    if (next > MaxSystemInfoBytes)
                    {
                        return result;
                    }
                    size = next;
                }

                if (!ok)
                {
                    return result;
                }

                ParseHandleTable(buffer, size, pid, result);
            }
            finally
            {
                if (buffer != IntPtr.Zero)
                {
                    Marshal.FreeHGlobal(buffer);
                }
            }
            return result;
        }

        /// <summary>SYSTEM_EXTENDED_HANDLE_INFORMATION: header { NumberOfHandles, Reserved } then 40-byte x64 entries.</summary>
        private static void ParseHandleTable(IntPtr buffer, long bufferSize, int pid, List<IntPtr> result)
        {
            int headerSize = 2 * IntPtr.Size;
            long count = Marshal.ReadIntPtr(buffer, 0).ToInt64();
            long maxEntries = (bufferSize - headerSize) / HandleEntrySize;

            if (count < 0)
            {
                count = 0;
            }
            if (count > maxEntries)
            {
                count = maxEntries;
            }

            int pidOffset = IntPtr.Size;          // UniqueProcessId
            int handleOffset = 2 * IntPtr.Size;   // HandleValue

            for (long i = 0; i < count; i++)
            {
                int entryBase = headerSize + (int)(i * HandleEntrySize);

                long owner = Marshal.ReadIntPtr(buffer, entryBase + pidOffset).ToInt64();
                if (owner != pid)
                {
                    continue;
                }

                IntPtr handleValue = Marshal.ReadIntPtr(buffer, entryBase + handleOffset);
                if (handleValue != IntPtr.Zero)
                {
                    result.Add(handleValue);
                }
            }
        }

        /// <summary>
        /// Duplicates a source handle into this process, checks the object type (only Event, Mutant and Semaphore are
        /// name-queried) and the name. The duplicate is closed unless a name query timed out, in which case it is
        /// leaked on purpose because the worker thread may still be using it.
        /// </summary>
        private static bool IsSingletonHandle(IntPtr sourceProcess, IntPtr sourceHandle)
        {
            IntPtr dup = IntPtr.Zero;
            bool leak = false;
            bool match = false;

            try
            {
                if (!Kernel32.DuplicateHandle(sourceProcess, sourceHandle, Kernel32.GetCurrentProcess(), out dup, 0, false, Kernel32.DUPLICATE_SAME_ACCESS))
                {
                    dup = IntPtr.Zero;
                    return false;
                }

                string typeName;
                if (TryQueryUnicodeString(dup, Kernel32.OBJECT_TYPE_INFORMATION_CLASS, out typeName) && IsNameQueryableType(typeName))
                {
                    bool timedOut;
                    string objectName = QueryNameWithTimeout(dup, out timedOut);
                    if (timedOut)
                    {
                        leak = true;
                    }
                    else if (objectName != null)
                    {
                        match = IsSingletonName(objectName);
                    }
                }
            }
            catch
            {
                // Treat as non-matching
            }
            finally
            {
                if (dup != IntPtr.Zero && !leak)
                {
                    try
                    {
                        Kernel32.CloseHandle(dup);
                    }
                    catch
                    {
                        // Ignore
                    }
                }
            }
            return match;
        }

        /// <summary>Closes the handle inside the source process via DUPLICATE_CLOSE_SOURCE; the returned duplicate is closed at once.</summary>
        private static bool CloseSourceHandle(IntPtr sourceProcess, IntPtr sourceHandle)
        {
            IntPtr dup2 = IntPtr.Zero;
            bool ok;
            try
            {
                ok = Kernel32.DuplicateHandle(sourceProcess, sourceHandle, Kernel32.GetCurrentProcess(), out dup2, 0, false, Kernel32.DUPLICATE_CLOSE_SOURCE);
            }
            catch
            {
                ok = false;
            }

            if (dup2 != IntPtr.Zero)
            {
                try
                {
                    Kernel32.CloseHandle(dup2);
                }
                catch
                {
                    // Ignore
                }
            }
            return ok;
        }

        private static bool IsBufferSizeStatus(uint status)
        {
            return status == Kernel32.STATUS_INFO_LENGTH_MISMATCH
                || status == Kernel32.STATUS_BUFFER_OVERFLOW
                || status == Kernel32.STATUS_BUFFER_TOO_SMALL;
        }

        private static bool IsNameQueryableType(string typeName)
        {
            return string.Equals(typeName, "Event", StringComparison.OrdinalIgnoreCase)
                || string.Equals(typeName, "Mutant", StringComparison.OrdinalIgnoreCase)
                || string.Equals(typeName, "Semaphore", StringComparison.OrdinalIgnoreCase);
        }

        private static bool IsSingletonName(string objectName)
        {
            return objectName.EndsWith(SingletonEventName, StringComparison.OrdinalIgnoreCase)
                || objectName.EndsWith(SingletonMutexName, StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>Queries a UNICODE_STRING-prefixed info class, growing the buffer on size-related statuses.</summary>
        private static bool TryQueryUnicodeString(IntPtr handle, uint infoClass, out string value)
        {
            value = null;
            long size = InitialObjectInfoBytes;

            for (int attempt = 0; attempt < MaxBufferAttempts; attempt++)
            {
                IntPtr buffer = Marshal.AllocHGlobal(new IntPtr(size));
                try
                {
                    uint returnLength;
                    uint status = Kernel32.NtQueryObject(handle, infoClass, buffer, (uint)size, out returnLength);
                    if (status == Kernel32.STATUS_SUCCESS)
                    {
                        value = ReadUnicodeStringAt(buffer, size);
                        return value != null;
                    }

                    if (!IsBufferSizeStatus(status))
                    {
                        return false;
                    }

                    long next = Math.Max((long)returnLength, size * 2L);
                    if (next > MaxObjectInfoBytes)
                    {
                        return false;
                    }
                    size = next;
                }
                finally
                {
                    Marshal.FreeHGlobal(buffer);
                }
            }
            return false;
        }

        /// <summary>Reads the UNICODE_STRING at the start of the buffer. Returns null if empty or out of range.</summary>
        private static string ReadUnicodeStringAt(IntPtr buffer, long bufferSize)
        {
            ushort length = (ushort)Marshal.ReadInt16(buffer, 0);
            if (length < 2)
            {
                return null;
            }

            IntPtr stringPtr = Marshal.ReadIntPtr(buffer, IntPtr.Size);
            if (stringPtr == IntPtr.Zero)
            {
                return null;
            }

            int evenLength = length & ~1;
            if (evenLength > bufferSize)
            {
                return null;
            }

            byte[] bytes = new byte[evenLength];
            Marshal.Copy(stringPtr, bytes, 0, evenLength);
            return Encoding.Unicode.GetString(bytes);
        }

        /// <summary>Holds the state for one object-name query run on a background thread.</summary>
        private sealed class NameQuery
        {
            private readonly IntPtr _handle;
            public readonly ManualResetEvent Done = new ManualResetEvent(false);
            public string Name;

            public NameQuery(IntPtr handle)
            {
                _handle = handle;
            }

            public void Run()
            {
                try
                {
                    string name;
                    if (TryQueryUnicodeString(_handle, Kernel32.OBJECT_NAME_INFORMATION_CLASS, out name))
                    {
                        Name = name;
                    }
                }
                catch
                {
                    // Ignore
                }
                finally
                {
                    Done.Set();
                }
            }
        }

        /// <summary>
        /// Queries the object name on a worker thread with a timeout. On timeout timedOut is true and the worker is
        /// abandoned (background thread); the caller must not close the handle.
        /// </summary>
        private static string QueryNameWithTimeout(IntPtr handle, out bool timedOut)
        {
            timedOut = false;
            NameQuery query = new NameQuery(handle);

            Thread worker = new Thread(new ThreadStart(query.Run));
            worker.IsBackground = true;

            try
            {
                worker.Start();
            }
            catch
            {
                query.Done.Dispose();
                return null;
            }

            if (query.Done.WaitOne(NameQueryTimeoutMs))
            {
                query.Done.Dispose();
                return query.Name;
            }

            timedOut = true;
            return null;
        }
    }
}
