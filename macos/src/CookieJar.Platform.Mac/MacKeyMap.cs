using System.Collections.Generic;

namespace CookieJar.Platform.Mac
{
    /// <summary>
    /// Windows virtual-key codes used by the app to macOS CGKeyCode values.
    /// Letters and digits follow the ANSI layout. // MAC-UNVERIFIED: other keyboard layouts map letters to different key codes.
    /// </summary>
    public static class MacKeyMap
    {
        // Index 0 = 'A' (0x41) ... 25 = 'Z' (0x5A).
        private static readonly ushort[] Letters =
        {
            0, 11, 8, 2, 14, 3, 5, 4, 34, 38, 40, 37, 46,
            45, 31, 35, 12, 15, 1, 17, 32, 9, 13, 7, 16, 6
        };

        // Index 0 = '0' (0x30) ... 9 = '9' (0x39).
        private static readonly ushort[] Digits = { 29, 18, 19, 20, 21, 23, 22, 26, 28, 25 };

        private static readonly Dictionary<int, ushort> Others = new Dictionary<int, ushort>
        {
            { 0x08, 51 },    // Backspace
            { 0x09, 48 },    // Tab
            { 0x0D, 36 },    // Enter (Return)
            { 0x10, 56 },    // Shift
            { 0x11, 59 },    // Ctrl
            { 0x12, 58 },    // Alt (Option)
            { 0x1B, 53 },    // Escape
            { 0x20, 49 },    // Space
            { 0x25, 123 },   // Left
            { 0x26, 126 },   // Up
            { 0x27, 124 },   // Right
            { 0x28, 125 },   // Down
            { 0xA0, 56 },    // Left Shift
            { 0xA1, 60 },    // Right Shift
            { 0xA2, 59 },    // Left Ctrl
            { 0xA3, 62 },    // Right Ctrl
            { 0xA4, 58 },    // Left Alt
            { 0xA5, 61 }     // Right Alt
        };

        /// <summary>False for a virtual key with no mapping.</summary>
        public static bool TryMap(int virtualKey, out ushort keyCode)
        {
            keyCode = 0;
            if (virtualKey >= 0x41 && virtualKey <= 0x5A)
            {
                keyCode = Letters[virtualKey - 0x41];
                return true;
            }
            if (virtualKey >= 0x30 && virtualKey <= 0x39)
            {
                keyCode = Digits[virtualKey - 0x30];
                return true;
            }
            return Others.TryGetValue(virtualKey, out keyCode);
        }
    }
}
