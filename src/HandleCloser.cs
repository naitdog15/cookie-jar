using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Text;
using System.Threading;

namespace RbxMulti
{
    /// <summary>
    /// Closes the ROBLOX singleton handles (ROBLOX_singletonEvent / ROBLOX_singletonMutex) inside
    /// RobloxPlayerBeta processes so that another Roblox instance can be started. Handles are
    /// located through the system extended handle table, verified on duplicates, and closed in the
    /// target process via DuplicateHandle(DUPLICATE_CLOSE_SOURCE). Nothing is injected or written.
    /// Every public method swallows exceptions and never throws.
    /// </summary>
    public static class HandleCloser
    {
        // NTSTATUS codes
        private const uint STATUS_SUCCESS = 0;
        private const uint STATUS_INFO_LENGTH_MISMATCH = 0xC0000004;
        private const uint STATUS_BUFFER_OVERFLOW = 0x80000005;
        private const uint STATUS_BUFFER_TOO_SMALL = 0xC0000023;

        // NtQuerySystemInformation / NtQueryObject classes
        private const uint SYSTEM_EXTENDED_HANDLE_INFORMATION = 64;
        private const uint OBJECT_NAME_INFORMATION_CLASS = 1;
        private const uint OBJECT_TYPE_INFORMATION_CLASS = 2;

        // Process / handle rights
        private const uint PROCESS_DUP_HANDLE = 0x0040;
        private const uint DUPLICATE_SAME_ACCESS = 0x2;
        private const uint DUPLICATE_CLOSE_SOURCE = 0x1;

        // Token rights
        private const uint TOKEN_ADJUST_PRIVILEGES = 0x0020;
        private const uint TOKEN_QUERY = 0x0008;
        private const uint SE_PRIVILEGE_ENABLED = 0x2;

        // Layout of SYSTEM_EXTENDED_HANDLE_TABLE_ENTRY_INFO on x64 (40 bytes):
        // Object @0, UniqueProcessId @8, HandleValue @16, GrantedAccess @24,
        // CreatorBackTraceIndex @28, ObjectTypeIndex @30, HandleAttributes @32, Reserved @36.
        private const int HandleEntrySize = 40;

        // Buffer sizing limits
        private const int MaxBufferAttempts = 64;
        private const long MaxSystemInfoBytes = 512L * 1024L * 1024L;
        private const long InitialSystemInfoBytes = 0x100000L;
        private const long InitialObjectInfoBytes = 4096L;
        private const long MaxObjectInfoBytes = 64L * 1024L;

        private const int NameQueryTimeoutMs = 500;

        private const string SingletonEventName = "ROBLOX_singletonEvent";
        private const string SingletonMutexName = "ROBLOX_singletonMutex";

        // Token privileges. Pack = 4 gives 4 (count) + 8 (LUID) + 4 (attributes) = 16 bytes,
        // matching the native layout (one LUID_AND_ATTRIBUTES entry is 12 bytes, 4-byte aligned).
        [StructLayout(LayoutKind.Sequential, Pack = 4)]
        private struct TOKEN_PRIVILEGES
        {
            public uint PrivilegeCount;
            public long Luid;
            public uint Attributes;
        }

        // P/Invoke declarations. None depend on GetLastError.
        [DllImport("ntdll.dll", SetLastError = false)]
        private static extern uint NtQuerySystemInformation(
            uint SystemInformationClass,
            IntPtr SystemInformation,
            uint SystemInformationLength,
            out uint ReturnLength);

        [DllImport("ntdll.dll", SetLastError = false)]
        private static extern uint NtQueryObject(
            IntPtr Handle,
            uint ObjectInformationClass,
            IntPtr ObjectInformation,
            uint ObjectInformationLength,
            out uint ReturnLength);

        [DllImport("kernel32.dll", SetLastError = false)]
        private static extern bool DuplicateHandle(
            IntPtr hSourceProcessHandle,
            IntPtr hSourceHandle,
            IntPtr hTargetProcessHandle,
            out IntPtr lpTargetHandle,
            uint dwDesiredAccess,
            bool bInheritHandle,
            uint dwOptions);

        [DllImport("kernel32.dll", SetLastError = false)]
        private static extern IntPtr OpenProcess(
            uint dwDesiredAccess,
            bool bInheritHandle,
            int dwProcessId);

        [DllImport("kernel32.dll", SetLastError = false)]
        private static extern bool CloseHandle(IntPtr hObject);

        [DllImport("kernel32.dll", SetLastError = false)]
        private static extern IntPtr GetCurrentProcess();

        [DllImport("advapi32.dll", SetLastError = false)]
        private static extern bool OpenProcessToken(
            IntPtr ProcessHandle,
            uint DesiredAccess,
            out IntPtr TokenHandle);

        [DllImport("advapi32.dll", SetLastError = false, CharSet = CharSet.Unicode)]
        private static extern bool LookupPrivilegeValue(
            string lpSystemName,
            string lpName,
            out long lpLuid);

        [DllImport("advapi32.dll", SetLastError = false)]
        private static extern bool AdjustTokenPrivileges(
            IntPtr TokenHandle,
            bool DisableAllPrivileges,
            ref TOKEN_PRIVILEGES NewState,
            uint BufferLength,
            IntPtr PreviousState,
            out uint ReturnLength);

        /// <summary>
        /// Closes ROBLOX singleton handles in the specified process.
        /// Returns the number of handles closed, or 0 on any failure.
        /// </summary>
        public static int CloseSingletons(int pid)
        {
            return ProcessSingletons(pid, true);
        }

        /// <summary>
        /// Counts ROBLOX singleton handles in the specified process without closing them.
        /// Returns 0 on any failure.
        /// </summary>
        public static int CountSingletons(int pid)
        {
            return ProcessSingletons(pid, false);
        }

        /// <summary>
        /// Closes singleton handles in all RobloxPlayerBeta processes.
        /// Returns the total number of handles closed. Never throws.
        /// </summary>
        public static int CloseSingletonsInAllRoblox()
        {
            int totalClosed = 0;

            Process[] robloxProcesses;
            try
            {
                robloxProcesses = Process.GetProcessesByName("RobloxPlayerBeta");
            }
            catch
            {
                return 0;
            }

            foreach (Process proc in robloxProcesses)
            {
                try
                {
                    totalClosed += CloseSingletons(proc.Id);
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

            return totalClosed;
        }

        /// <summary>
        /// Returns true if the current process is running with administrator rights.
        /// Returns false on any error.
        /// </summary>
        public static bool IsElevated()
        {
            try
            {
                using (WindowsIdentity identity = WindowsIdentity.GetCurrent())
                {
                    WindowsPrincipal principal = new WindowsPrincipal(identity);
                    return principal.IsInRole(WindowsBuiltInRole.Administrator);
                }
            }
            catch
            {
                return false;
            }
        }

        /// <summary>
        /// Shared implementation for CloseSingletons (close = true) and CountSingletons (close = false).
        /// </summary>
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
                        CloseHandle(sourceProcess);
                    }
                    catch
                    {
                        // Ignore
                    }
                }
            }

            return result;
        }

        /// <summary>
        /// Opens the target process with PROCESS_DUP_HANDLE. On failure, enables SeDebugPrivilege
        /// (non-fatal) and retries once.
        /// </summary>
        private static IntPtr OpenTargetProcess(int pid)
        {
            IntPtr handle = OpenProcess(PROCESS_DUP_HANDLE, false, pid);
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

            return OpenProcess(PROCESS_DUP_HANDLE, false, pid);
        }

        /// <summary>
        /// Enables SeDebugPrivilege for the current process. Failures are ignored by callers.
        /// </summary>
        private static void EnableSeDebugPrivilege()
        {
            IntPtr hToken;
            if (!OpenProcessToken(GetCurrentProcess(), TOKEN_ADJUST_PRIVILEGES | TOKEN_QUERY, out hToken))
            {
                return;
            }

            try
            {
                long luid;
                if (!LookupPrivilegeValue(null, "SeDebugPrivilege", out luid))
                {
                    return;
                }

                TOKEN_PRIVILEGES tp = new TOKEN_PRIVILEGES();
                tp.PrivilegeCount = 1;
                tp.Luid = luid;
                tp.Attributes = SE_PRIVILEGE_ENABLED;

                uint returnLength;
                AdjustTokenPrivileges(hToken, false, ref tp, (uint)Marshal.SizeOf(typeof(TOKEN_PRIVILEGES)), IntPtr.Zero, out returnLength);
            }
            finally
            {
                CloseHandle(hToken);
            }
        }

        /// <summary>
        /// Reads the system extended handle table and returns the handle values (in the target
        /// process's own numbering) that belong to the given pid. Grows the buffer on size-related
        /// statuses, bounded by MaxBufferAttempts and MaxSystemInfoBytes.
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
                    uint status = NtQuerySystemInformation(SYSTEM_EXTENDED_HANDLE_INFORMATION, buffer, (uint)size, out returnLength);
                    if (status == STATUS_SUCCESS)
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

        /// <summary>
        /// Parses SYSTEM_EXTENDED_HANDLE_INFORMATION: header = { UIntPtr NumberOfHandles; UIntPtr Reserved; }
        /// followed by 40-byte entries (x64). Uses explicit offsets.
        /// </summary>
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
        /// Duplicates a source-process handle into this process, checks its object type (only
        /// Event, Mutant and Semaphore are name-queried), and checks the name. The duplicate is
        /// always closed unless a name query timed out, in which case it is leaked on purpose
        /// because the worker thread may still be using it.
        /// </summary>
        private static bool IsSingletonHandle(IntPtr sourceProcess, IntPtr sourceHandle)
        {
            IntPtr dup = IntPtr.Zero;
            bool leak = false;
            bool match = false;

            try
            {
                if (!DuplicateHandle(sourceProcess, sourceHandle, GetCurrentProcess(), out dup, 0, false, DUPLICATE_SAME_ACCESS))
                {
                    dup = IntPtr.Zero;
                    return false;
                }

                string typeName;
                if (TryQueryUnicodeString(dup, OBJECT_TYPE_INFORMATION_CLASS, out typeName) && IsNameQueryableType(typeName))
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
                        CloseHandle(dup);
                    }
                    catch
                    {
                        // Ignore
                    }
                }
            }

            return match;
        }

        /// <summary>
        /// Closes the handle inside the source process using DUPLICATE_CLOSE_SOURCE. The returned
        /// duplicate is closed immediately.
        /// </summary>
        private static bool CloseSourceHandle(IntPtr sourceProcess, IntPtr sourceHandle)
        {
            IntPtr dup2 = IntPtr.Zero;
            bool ok = false;
            try
            {
                ok = DuplicateHandle(sourceProcess, sourceHandle, GetCurrentProcess(), out dup2, 0, false, DUPLICATE_CLOSE_SOURCE);
            }
            catch
            {
                ok = false;
            }

            if (dup2 != IntPtr.Zero)
            {
                try
                {
                    CloseHandle(dup2);
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
            return status == STATUS_INFO_LENGTH_MISMATCH
                || status == STATUS_BUFFER_OVERFLOW
                || status == STATUS_BUFFER_TOO_SMALL;
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

        /// <summary>
        /// Queries a UNICODE_STRING-prefixed info class (object name or type) on a handle, growing
        /// the buffer on size-related statuses. The string is copied to managed memory before the
        /// buffer is freed.
        /// </summary>
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
                    uint status = NtQueryObject(handle, infoClass, buffer, (uint)size, out returnLength);
                    if (status == STATUS_SUCCESS)
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

        /// <summary>
        /// Reads a UNICODE_STRING located at the start of the buffer (Length ushort, MaximumLength
        /// ushort, Buffer pointer at IntPtr.Size). Returns null if empty or out of range.
        /// </summary>
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

        /// <summary>
        /// Holds the state for one object-name query run on a background thread.
        /// </summary>
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
                    if (TryQueryUnicodeString(_handle, OBJECT_NAME_INFORMATION_CLASS, out name))
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
        /// Queries the object name on a worker thread with a timeout. On timeout, timedOut is true
        /// and the worker is abandoned (background thread); the caller must not close the handle.
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
