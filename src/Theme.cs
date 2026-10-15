using System;
using System.Windows;
using System.Windows.Media;
using System.Collections.Generic;
using Microsoft.Win32;
using System.Runtime.InteropServices;
using System.Windows.Interop;

namespace RbxMulti
{
    // Central colour, font and radius source for the launcher UI.
    //
    // PUBLIC API (for UI wiring):
    //   Theme.Apply(bool dark)           switch palette; updates brushes, bumps Version, raises Changed.
    //   Theme.IsDark                     current mode.
    //   Theme.SystemPrefersDark()        reads the Windows "apps use light theme" setting.
    //   Theme.ApplyTitleBar(Window w)    best-effort DWM dark title bar / Mica / rounded corners.
    //   Theme.Changed                    event (Action) raised after every Apply(); each subscriber
    //                                    runs in its own try/catch so one failure cannot break a toggle.
    //   Theme.Version                    int, incremented on every Apply().
    //   Theme.ShadowColor                card shadow colour for the current mode.
    //   Theme.RadiusCard / RadiusControl, Theme.UiFont / MonoFont.
    //   Theme.Bg, Surface, SurfaceAlt, Border, Text, TextMuted, Accent, AccentHover, AccentSoft,
    //   OnAccent, Success, SuccessSoft, Warning, WarningSoft, Danger, DangerSoft, DangerHover,
    //   ScrollThumb                      shared SolidColorBrush instances (see IMPORTANT below).
    //   Theme.Clone(SolidColorBrush b)   returns a new unfrozen copy that is never replaced by Apply().
    //
    // IMPORTANT: brushes may be REPLACED (not just mutated) on Apply(). WPF freezes shared brushes
    // used in styles, templates and resources, so when a frozen brush is encountered a new instance
    // is created. Consumers must re-read Theme.X inside a Theme.Changed handler, or use
    // DynamicResource keys, and must never cache a brush reference long-term. Use Theme.Clone(...)
    // if a private, stable, mutable copy is needed.
    public static class Theme
    {
        public const double RadiusCard = 14;
        public const double RadiusControl = 9;

        // Type scale (px). Use these instead of literal sizes so sections stay consistent.
        public const double TextDisplay = 22;   // big numbers in the stat strip, window title
        public const double TextTitle = 18;     // dialog titles
        public const double TextSection = 15;   // card / section headings, instance names
        public const double TextBody = 13;      // normal text, buttons
        public const double TextMeta = 12;      // secondary lines, hints, footers
        public const double TextCaption = 11;   // small caps captions above values

        // Spacing (px).
        public const double Gap = 8;            // between controls in a row
        public const double GapSection = 12;    // between cards / sections
        public static Thickness CardPadding { get { return new Thickness(18, 14, 18, 14); } }

        public static readonly FontFamily UiFont = new FontFamily("Segoe UI Variable Text, Segoe UI");
        public static readonly FontFamily MonoFont = new FontFamily("Cascadia Mono, Consolas");

        public static bool IsDark { get; private set; }

        // Incremented on every Apply() so consumers can detect a palette change cheaply.
        public static int Version { get; private set; }

        // Raised after every Apply() so code-behind can refresh anything not bound.
        public static event Action Changed;

        // Backing fields for the shared brushes. Replaced only when WPF has frozen/sealed them.
        private static SolidColorBrush _bg;
        private static SolidColorBrush _surface;
        private static SolidColorBrush _surfaceAlt;
        private static SolidColorBrush _border;
        private static SolidColorBrush _text;
        private static SolidColorBrush _textMuted;
        private static SolidColorBrush _accent;
        private static SolidColorBrush _accentHover;
        private static SolidColorBrush _accentSoft;
        private static SolidColorBrush _onAccent;
        private static SolidColorBrush _success;
        private static SolidColorBrush _successSoft;
        private static SolidColorBrush _warning;
        private static SolidColorBrush _warningSoft;
        private static SolidColorBrush _danger;
        private static SolidColorBrush _dangerSoft;
        private static SolidColorBrush _dangerHover;
        private static SolidColorBrush _scrollThumb;

        // Shared brush instances. Read them via the property each time; do not cache.
        public static SolidColorBrush Bg { get { return _bg; } private set { _bg = value; } }
        public static SolidColorBrush Surface { get { return _surface; } private set { _surface = value; } }
        public static SolidColorBrush SurfaceAlt { get { return _surfaceAlt; } private set { _surfaceAlt = value; } }
        public static SolidColorBrush Border { get { return _border; } private set { _border = value; } }
        public static SolidColorBrush Text { get { return _text; } private set { _text = value; } }
        public static SolidColorBrush TextMuted { get { return _textMuted; } private set { _textMuted = value; } }
        public static SolidColorBrush Accent { get { return _accent; } private set { _accent = value; } }
        public static SolidColorBrush AccentHover { get { return _accentHover; } private set { _accentHover = value; } }
        public static SolidColorBrush AccentSoft { get { return _accentSoft; } private set { _accentSoft = value; } }
        public static SolidColorBrush OnAccent { get { return _onAccent; } private set { _onAccent = value; } }
        public static SolidColorBrush Success { get { return _success; } private set { _success = value; } }
        public static SolidColorBrush SuccessSoft { get { return _successSoft; } private set { _successSoft = value; } }
        public static SolidColorBrush Warning { get { return _warning; } private set { _warning = value; } }
        public static SolidColorBrush WarningSoft { get { return _warningSoft; } private set { _warningSoft = value; } }
        public static SolidColorBrush Danger { get { return _danger; } private set { _danger = value; } }
        public static SolidColorBrush DangerSoft { get { return _dangerSoft; } private set { _dangerSoft = value; } }
        public static SolidColorBrush DangerHover { get { return _dangerHover; } private set { _dangerHover = value; } }
        public static SolidColorBrush ScrollThumb { get { return _scrollThumb; } private set { _scrollThumb = value; } }

        // Shadow colour for cards; alpha differs by mode.
        public static Color ShadowColor
        {
            get { return IsDark ? Color.FromArgb(0x40, 0, 0, 0) : Color.FromArgb(0x14, 0x35, 0x26, 0x1E); }
        }

        static Theme()
        {
            // Create every brush up front so the properties are never null, then apply the light palette.
            _bg = new SolidColorBrush();
            _surface = new SolidColorBrush();
            _surfaceAlt = new SolidColorBrush();
            _border = new SolidColorBrush();
            _text = new SolidColorBrush();
            _textMuted = new SolidColorBrush();
            _accent = new SolidColorBrush();
            _accentHover = new SolidColorBrush();
            _accentSoft = new SolidColorBrush();
            _onAccent = new SolidColorBrush();
            _success = new SolidColorBrush();
            _successSoft = new SolidColorBrush();
            _warning = new SolidColorBrush();
            _warningSoft = new SolidColorBrush();
            _danger = new SolidColorBrush();
            _dangerSoft = new SolidColorBrush();
            _dangerHover = new SolidColorBrush();
            _scrollThumb = new SolidColorBrush();

            Apply(false);
        }

        // Returns a new, unfrozen, private copy of a brush. Apply() never touches the copy.
        public static SolidColorBrush Clone(SolidColorBrush b)
        {
            if (b == null) return new SolidColorBrush();
            return new SolidColorBrush(b.Color);
        }

        private static Color Hex(string rgb)
        {
            return (Color)ColorConverter.ConvertFromString(rgb);
        }

        // Sets a brush colour without ever throwing. If the brush is null, frozen, sealed,
        // or rejects the assignment, the field is replaced with a fresh brush of the same colour.
        private static void SetBrush(ref SolidColorBrush brush, string rgb)
        {
            Color colour;
            try
            {
                colour = Hex(rgb);
            }
            catch (Exception)
            {
                return; // Invalid literal: keep the previous colour.
            }

            try
            {
                if (brush == null || brush.IsFrozen || brush.IsSealed)
                {
                    brush = new SolidColorBrush(colour);
                    return;
                }
                brush.Color = colour;
            }
            catch (InvalidOperationException)
            {
                try
                {
                    brush = new SolidColorBrush(colour);
                }
                catch (Exception)
                {
                    // Give up silently; never throw from Apply.
                }
            }
            catch (Exception)
            {
                // Never throw from Apply.
            }
        }

        // Switches palette, updates the shared brushes and raises Changed.
        public static void Apply(bool dark)
        {
            try
            {
                IsDark = dark;

                if (dark)
                {
                    SetBrush(ref _bg, "#211A16");
                    SetBrush(ref _surface, "#2B231E");
                    SetBrush(ref _surfaceAlt, "#352A23");
                    SetBrush(ref _border, "#514137");
                    SetBrush(ref _text, "#FFF7ED");
                    SetBrush(ref _textMuted, "#C2B3A8");
                    SetBrush(ref _accent, "#E7A96B");
                    SetBrush(ref _accentHover, "#F2BA7E");
                    SetBrush(ref _accentSoft, "#493424");
                    SetBrush(ref _onAccent, "#2B1608");
                    SetBrush(ref _success, "#8BC49A");
                    SetBrush(ref _successSoft, "#22342A");
                    SetBrush(ref _warning, "#F2C66D");
                    SetBrush(ref _warningSoft, "#3D321A");
                    SetBrush(ref _danger, "#F08E88");
                    SetBrush(ref _dangerSoft, "#4C2928");
                    SetBrush(ref _dangerHover, "#653634");
                    SetBrush(ref _scrollThumb, "#796354");
                }
                else
                {
                    SetBrush(ref _bg, "#FFF8ED");
                    SetBrush(ref _surface, "#FFFFFF");
                    SetBrush(ref _surfaceAlt, "#F8EEDD");
                    SetBrush(ref _border, "#E5D3BC");
                    SetBrush(ref _text, "#35261E");
                    SetBrush(ref _textMuted, "#716158");
                    SetBrush(ref _accent, "#95572D");
                    SetBrush(ref _accentHover, "#7D4523");
                    SetBrush(ref _accentSoft, "#F4E0C8");
                    SetBrush(ref _onAccent, "#FFFFFF");
                    SetBrush(ref _success, "#3F6F4C");
                    SetBrush(ref _successSoft, "#E4F0E6");
                    SetBrush(ref _warning, "#855700");
                    SetBrush(ref _warningSoft, "#F7E8C6");
                    SetBrush(ref _danger, "#A63F3F");
                    SetBrush(ref _dangerSoft, "#F8DEDA");
                    SetBrush(ref _dangerHover, "#F1C4BE");
                    SetBrush(ref _scrollThumb, "#B99F89");
                }

                Version = Version + 1;
            }
            catch (Exception)
            {
                // Apply must never throw to the caller.
            }

            RaiseChanged();
        }

        // Invokes each subscriber on its own so one failing handler cannot abort the toggle.
        private static void RaiseChanged()
        {
            Action handler = Changed;
            if (handler == null) return;

            Delegate[] subscribers;
            try
            {
                subscribers = handler.GetInvocationList();
            }
            catch (Exception)
            {
                return;
            }

            foreach (Delegate d in subscribers)
            {
                try
                {
                    Action action = d as Action;
                    if (action != null) action();
                }
                catch (Exception)
                {
                    // Ignore a failing subscriber; the others still run.
                }
            }
        }

        // Reads the Windows "apps use light theme" setting.
        // Returns false (light) when the value is missing or unreadable.
        public static bool SystemPrefersDark()
        {
            try
            {
                object v = Registry.GetValue(
                    @"HKEY_CURRENT_USER\Software\Microsoft\Windows\CurrentVersion\Themes\Personalize",
                    "AppsUseLightTheme", null);
                if (v is int)
                {
                    return ((int)v) == 0;
                }
            }
            catch (Exception)
            {
                // Fall through to light.
            }
            return false;
        }

        // Best-effort DWM tweaks: dark title bar, Mica backdrop, rounded corners.
        // Any failure (older Windows, missing dwmapi entry point) is ignored.
        public static void ApplyTitleBar(Window w)
        {
            if (w == null) return;
            try
            {
                IntPtr hwnd = new WindowInteropHelper(w).EnsureHandle();
                if (hwnd == IntPtr.Zero) return;

                int dark = IsDark ? 1 : 0;
                SetAttr(hwnd, 20, dark);   // DWMWA_USE_IMMERSIVE_DARK_MODE
                SetAttr(hwnd, 38, 2);      // DWMWA_SYSTEMBACKDROP_TYPE: 2 = Mica
                SetAttr(hwnd, 33, 2);      // DWMWA_WINDOW_CORNER_PREFERENCE: 2 = round
            }
            catch (Exception)
            {
                // Best effort only.
            }
        }

        private static void SetAttr(IntPtr hwnd, int attr, int value)
        {
            try
            {
                DwmSetWindowAttribute(hwnd, attr, ref value, sizeof(int));
            }
            catch (Exception)
            {
                // Attribute unsupported on this Windows build.
            }
        }

        [DllImport("dwmapi.dll")]
        private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int attrValue, int attrSize);
    }
}
