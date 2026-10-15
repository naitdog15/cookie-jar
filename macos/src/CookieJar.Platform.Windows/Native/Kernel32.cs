using System;
using System.Runtime.InteropServices;
using System.Text;

namespace CookieJar.Platform.Windows
{
    /// <summary>
    /// Win32 declarations used by the Windows platform classes. kernel32 and advapi32 are used by the process,
    /// working-set and Roblox code; ntdll is used only by the single-instance handle closer (the same calls
    /// the shipping app makes in src/HandleCloser.cs).
    /// </summary>
    internal static class Kernel32
    {
        // ---- Constants -----------------------------------------------------------------------

        public const uint TH32CS_SNAPPROCESS = 0x00000002;
        public const uint PROCESS_TERMINATE = 0x0001;
        public const uint PROCESS_SET_INFORMATION = 0x0200;
        public const uint PROCESS_SET_QUOTA = 0x0100;
        public const uint PROCESS_DUP_HANDLE = 0x0040;
        public const uint PROCESS_QUERY_LIMITED_INFORMATION = 0x1000;
        public const uint SYNCHRONIZE = 0x00100000;
        public const uint TOKEN_ADJUST_PRIVILEGES = 0x0020;
        public const uint TOKEN_QUERY = 0x0008;
        public const uint SE_PRIVILEGE_ENABLED = 0x00000002;
        public const uint WAIT_OBJECT_0 = 0x00000000;
        public const uint WAIT_TIMEOUT = 0x00000102;
        public const int ERROR_ACCESS_DENIED = 5;
        public const int ERROR_INVALID_PARAMETER = 87;
        public const uint DUPLICATE_SAME_ACCESS = 0x2;
        public const uint DUPLICATE_CLOSE_SOURCE = 0x1;
        public const uint QUOTA_LIMITS_HARDWS_MIN_DISABLE = 0x2;
        public const uint QUOTA_LIMITS_HARDWS_MAX_ENABLE = 0x4;
        public const uint QUOTA_LIMITS_HARDWS_MAX_DISABLE = 0x8;
        public const uint BelowNormalPriorityClass = 0x4000;
        public const uint NormalPriorityClass = 0x0020;
        public const int ProcessPowerThrottling = 4;
        public const uint PowerThrottlingExecutionSpeed = 0x1;
        public const uint SYSTEM_EXTENDED_HANDLE_INFORMATION = 64;
        public const uint OBJECT_NAME_INFORMATION_CLASS = 1;
        public const uint OBJECT_TYPE_INFORMATION_CLASS = 2;
        public const uint STATUS_SUCCESS = 0;
        public const uint STATUS_INFO_LENGTH_MISMATCH = 0xC0000004;
        public const uint STATUS_BUFFER_OVERFLOW = 0x80000005;
        public const uint STATUS_BUFFER_TOO_SMALL = 0xC0000023;

        // ---- Structures ----------------------------------------------------------------------

        [StructLayout(LayoutKind.Sequential)]
        public struct LUID
        {
            public uint LowPart;
            public int HighPart;
        }

        // 4 + 8 + 4 = 16 bytes, matching the native TOKEN_PRIVILEGES with one entry.
        [StructLayout(LayoutKind.Sequential)]
        public struct TOKEN_PRIVILEGES
        {
            public uint PrivilegeCount;
            public LUID Luid;
            public uint Attributes;
        }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        public struct PROCESSENTRY32W
        {
            public uint dwSize;
            public uint cntUsage;
            public uint th32ProcessID;
            public IntPtr th32DefaultHeapID;
            public uint th32ModuleID;
            public uint cntThreads;
            public uint th32ParentProcessID;
            public int pcPriClassBase;
            public uint dwFlags;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)]
            public string szExeFile;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct PowerThrottlingState
        {
            public uint Version;
            public uint ControlMask;
            public uint StateMask;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct ProcessMemoryCounters
        {
            public uint cb;
            public uint PageFaultCount;
            public UIntPtr PeakWorkingSetSize;
            public UIntPtr WorkingSetSize;
            public UIntPtr QuotaPeakPagedPoolUsage;
            public UIntPtr QuotaPagedPoolUsage;
            public UIntPtr QuotaPeakNonPagedPoolUsage;
            public UIntPtr QuotaNonPagedPoolUsage;
            public UIntPtr PagefileUsage;
            public UIntPtr PeakPagefileUsage;
        }

        // ---- kernel32 ------------------------------------------------------------------------

        [DllImport("kernel32.dll", SetLastError = true)]
        public static extern IntPtr CreateToolhelp32Snapshot(uint dwFlags, uint th32ProcessID);

        [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode, EntryPoint = "Process32FirstW")]
        public static extern bool Process32FirstW(IntPtr hSnapshot, ref PROCESSENTRY32W lppe);

        [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode, EntryPoint = "Process32NextW")]
        public static extern bool Process32NextW(IntPtr hSnapshot, ref PROCESSENTRY32W lppe);

        [DllImport("kernel32.dll", SetLastError = true)]
        public static extern bool CloseHandle(IntPtr hObject);

        [DllImport("kernel32.dll", SetLastError = true)]
        public static extern IntPtr OpenProcess(uint dwDesiredAccess, bool bInheritHandle, uint dwProcessId);

        [DllImport("kernel32.dll", SetLastError = true)]
        public static extern bool TerminateProcess(IntPtr hProcess, uint uExitCode);

        [DllImport("kernel32.dll", SetLastError = true)]
        public static extern uint WaitForSingleObject(IntPtr hHandle, uint dwMilliseconds);

        [DllImport("kernel32.dll", SetLastError = true)]
        public static extern bool GetProcessTimes(IntPtr hProcess, out long lpCreationTime, out long lpExitTime, out long lpKernelTime, out long lpUserTime);

        [DllImport("kernel32.dll", SetLastError = true)]
        public static extern IntPtr GetCurrentProcess();

        [DllImport("kernel32.dll", SetLastError = true)]
        public static extern uint GetCurrentProcessId();

        [DllImport("kernel32.dll", SetLastError = true)]
        public static extern bool GetExitCodeProcess(IntPtr hProcess, out uint lpExitCode);

        [DllImport("kernel32.dll", SetLastError = true)]
        public static extern bool K32GetProcessMemoryInfo(IntPtr hProcess, ref ProcessMemoryCounters counters, uint size);

        [DllImport("kernel32.dll", SetLastError = true)]
        public static extern bool SetProcessWorkingSetSizeEx(IntPtr hProcess, UIntPtr minimum, UIntPtr maximum, uint flags);

        [DllImport("kernel32.dll", SetLastError = true)]
        public static extern bool SetPriorityClass(IntPtr hProcess, uint dwPriorityClass);

        [DllImport("kernel32.dll", SetLastError = true)]
        public static extern bool SetProcessInformation(IntPtr hProcess, int ProcessInformationClass, ref PowerThrottlingState ProcessInformation, uint ProcessInformationSize);

        [DllImport("kernel32.dll", SetLastError = false)]
        public static extern bool DuplicateHandle(IntPtr hSourceProcessHandle, IntPtr hSourceHandle, IntPtr hTargetProcessHandle, out IntPtr lpTargetHandle, uint dwDesiredAccess, bool bInheritHandle, uint dwOptions);

        // ---- advapi32 ------------------------------------------------------------------------

        [DllImport("advapi32.dll", SetLastError = true)]
        public static extern bool OpenProcessToken(IntPtr ProcessHandle, uint DesiredAccess, out IntPtr TokenHandle);

        [DllImport("advapi32.dll", SetLastError = true)]
        public static extern bool GetTokenInformation(IntPtr TokenHandle, int TokenInformationClass, out uint TokenInformation, uint TokenInformationLength, out uint ReturnLength);

        [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode, EntryPoint = "LookupPrivilegeValueW")]
        public static extern bool LookupPrivilegeValueW(string lpSystemName, string lpName, out LUID lpLuid);

        [DllImport("advapi32.dll", SetLastError = true)]
        public static extern bool AdjustTokenPrivileges(IntPtr TokenHandle, bool DisableAllPrivileges, ref TOKEN_PRIVILEGES NewState, uint BufferLength, IntPtr PreviousState, IntPtr ReturnLength);

        // ---- advapi32 registry (netstandard2.0 has no Microsoft.Win32.Registry without a package) ----

        [DllImport("advapi32.dll", CharSet = CharSet.Unicode)]
        public static extern int RegOpenKeyExW(IntPtr hKey, string lpSubKey, uint ulOptions, uint samDesired, out IntPtr phkResult);

        [DllImport("advapi32.dll")]
        public static extern int RegCloseKey(IntPtr hKey);

        [DllImport("advapi32.dll", CharSet = CharSet.Unicode)]
        public static extern int RegQueryValueExW(IntPtr hKey, string lpValueName, IntPtr lpReserved, out uint lpType, byte[] lpData, ref uint lpcbData);

        [DllImport("advapi32.dll", CharSet = CharSet.Unicode)]
        public static extern int RegEnumValueW(IntPtr hKey, uint dwIndex, StringBuilder lpValueName, ref uint lpcchValueName, IntPtr lpReserved, IntPtr lpType, IntPtr lpData, IntPtr lpcbData);

        [DllImport("advapi32.dll", CharSet = CharSet.Unicode)]
        public static extern int RegDeleteValueW(IntPtr hKey, string lpValueName);

        [DllImport("advapi32.dll", CharSet = CharSet.Unicode)]
        public static extern int RegCreateKeyExW(IntPtr hKey, string lpSubKey, uint Reserved, string lpClass, uint dwOptions, uint samDesired, IntPtr lpSecurityAttributes, out IntPtr phkResult, out uint lpdwDisposition);

        [DllImport("advapi32.dll", CharSet = CharSet.Unicode)]
        public static extern int RegSetValueExW(IntPtr hKey, string lpValueName, uint Reserved, uint dwType, byte[] lpData, uint cbData);

        [DllImport("advapi32.dll", CharSet = CharSet.Unicode)]
        public static extern int RegDeleteKeyW(IntPtr hKey, string lpSubKey);

        // ---- ntdll (handle table, single-instance closer only) ------------------------------

        [DllImport("ntdll.dll", SetLastError = false)]
        public static extern uint NtQuerySystemInformation(uint SystemInformationClass, IntPtr SystemInformation, uint SystemInformationLength, out uint ReturnLength);

        [DllImport("ntdll.dll", SetLastError = false)]
        public static extern uint NtQueryObject(IntPtr Handle, uint ObjectInformationClass, IntPtr ObjectInformation, uint ObjectInformationLength, out uint ReturnLength);
    }
}
