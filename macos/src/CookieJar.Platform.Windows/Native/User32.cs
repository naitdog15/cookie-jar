using System;
using System.Runtime.InteropServices;
using System.Text;

namespace CookieJar.Platform.Windows
{
    // P/Invoke declarations for window, monitor, cursor and input calls. Only Platform.Windows may use these.
    internal static class User32
    {
        internal delegate bool EnumWindowsProc(IntPtr hwnd, IntPtr lParam);
        internal delegate bool MonitorEnumProc(IntPtr hMonitor, IntPtr hdc, IntPtr rect, IntPtr data);

        [StructLayout(LayoutKind.Sequential)]
        internal struct RECT
        {
            public int Left;
            public int Top;
            public int Right;
            public int Bottom;
        }

        [StructLayout(LayoutKind.Sequential)]
        internal struct POINT
        {
            public int X;
            public int Y;
        }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        internal struct MONITORINFOEX
        {
            public int cbSize;
            public RECT rcMonitor;
            public RECT rcWork;
            public uint dwFlags;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)]
            public string szDevice;
        }

        [DllImport("kernel32.dll")] internal static extern uint GetCurrentThreadId();
        [DllImport("user32.dll")] internal static extern bool EnumWindows(EnumWindowsProc lpEnumFunc, IntPtr lParam);
        [DllImport("user32.dll")] internal static extern bool IsWindow(IntPtr hWnd);
        [DllImport("user32.dll")] internal static extern bool IsWindowVisible(IntPtr hWnd);
        [DllImport("user32.dll")] internal static extern bool IsIconic(IntPtr hWnd);
        [DllImport("user32.dll")] internal static extern bool IsZoomed(IntPtr hWnd);
        [DllImport("user32.dll")] internal static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);
        [DllImport("user32.dll")] internal static extern IntPtr GetWindow(IntPtr hWnd, uint uCmd);
        [DllImport("user32.dll")] internal static extern IntPtr GetAncestor(IntPtr hwnd, uint gaFlags);
        [DllImport("user32.dll")] internal static extern bool GetWindowRect(IntPtr hWnd, out RECT lpRect);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] internal static extern int GetClassName(IntPtr hWnd, StringBuilder lpClassName, int nMaxCount);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] internal static extern int GetWindowText(IntPtr hWnd, StringBuilder lpString, int nMaxCount);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] internal static extern int GetWindowTextLength(IntPtr hWnd);
        [DllImport("user32.dll")] internal static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);
        [DllImport("user32.dll")] internal static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter, int x, int y, int cx, int cy, uint uFlags);
        [DllImport("user32.dll")] internal static extern bool BringWindowToTop(IntPtr hWnd);
        [DllImport("user32.dll")] internal static extern bool SetForegroundWindow(IntPtr hWnd);
        [DllImport("user32.dll")] internal static extern IntPtr GetForegroundWindow();
        [DllImport("user32.dll")] internal static extern bool AttachThreadInput(uint idAttach, uint idAttachTo, bool fAttach);
        [DllImport("user32.dll")] internal static extern void keybd_event(byte bVk, byte bScan, uint dwFlags, UIntPtr dwExtraInfo);
        [DllImport("user32.dll")] internal static extern int GetSystemMetrics(int nIndex);
        [DllImport("user32.dll")] internal static extern bool EnumDisplayMonitors(IntPtr hdc, IntPtr lprcClip, MonitorEnumProc lpfnEnum, IntPtr dwData);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] internal static extern bool GetMonitorInfo(IntPtr hMonitor, ref MONITORINFOEX lpmi);
        [DllImport("user32.dll")] internal static extern bool GetCursorPos(out POINT lpPoint);
        [DllImport("user32.dll")] internal static extern bool SetCursorPos(int x, int y);
        [DllImport("user32.dll")] internal static extern IntPtr WindowFromPoint(POINT point);
        [DllImport("user32.dll")] internal static extern uint MapVirtualKey(uint uCode, uint uMapType);
        [DllImport("user32.dll", SetLastError = true)] internal static extern uint SendInput(uint nInputs, InputNative.INPUT[] pInputs, int cbSize);
    }

    internal static class Dwmapi
    {
        [DllImport("dwmapi.dll")] internal static extern int DwmGetWindowAttribute(IntPtr hwnd, uint dwAttribute, out User32.RECT pvAttribute, uint cbAttribute);
    }

    internal static class Shcore
    {
        [DllImport("shcore.dll")] internal static extern int GetDpiForMonitor(IntPtr hmonitor, int dpiType, out uint dpiX, out uint dpiY);
    }
}
