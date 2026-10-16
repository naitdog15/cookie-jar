// MiniBar.cs
// Floating, always-on-top status strip docked to the taskbar, just left of the tray.
//
// PUBLIC API (for the UI team)
// ----------------------------
// public interface IMiniBarHost
//     IList<RobloxInstance> Instances { get; }  instances to show as chips. If the list also implements
//                                               INotifyCollectionChanged the bar refreshes on changes.
//     void SwitchTo(RobloxInstance i)           bring that instance's window to the front.
//     void Stop(RobloxInstance i)               ends one instance (tree kill) after the host confirms.
//     void Play()                               launch the selected profile / selected game (Games.Current).
//     void StopAll()                            ends every instance (tree kill) after one summary confirm.
//     void ShowMainWindow()                     bring the main launcher window forward.
//     bool CanPlay { get; }                     MiniBar disables the Play button when false.
//
// public class MiniBar : Window
//     MiniBar(IMiniBarHost host)                builds the UI; does not show it.
//     void RefreshNow()                         rebuilds the chips from host.Instances (UI thread).
//     void Reposition()                         recomputes the dock rectangle now and applies it.
//     void ShowDocked()                         Show(), Reposition(), topmost, starts the 2 s timer.
//     bool IsDocked { get; }                    true when the bar sits over a horizontal taskbar.
//     event EventHandler OpenMainRequested      raised by the open-launcher button and the context menu
//                                               in addition to host.ShowMainWindow().
//
// Behaviour summary
//   * Window: no chrome, transparent, rounded 12 px Surface/Border card. ShowInTaskbar=false, Topmost,
//     never activates (WS_EX_NOACTIVATE | WS_EX_TOOLWINDOW set after SourceInitialized, so it is hidden
//     from Alt-Tab and never takes focus).
//   * Placement (physical px): taskbar = FindWindow("Shell_TrayWnd"). Height = taskbar height (fallback
//     48 DIP). Vertically aligned with the taskbar. Right edge = left of "TrayNotifyWnd" minus 8 px gap
//     (or taskbar right minus 300 DIP if the tray is not found). Left bound = 60% across the taskbar, so
//     the bar stays in the right part, clear of the Widgets area. Width = clamp(space, 420, 560) DIP.
//     Vertical taskbars: floats in the bottom-right of the monitor work area (IsDocked = false).
//     No taskbar: bottom-right of the primary work area.
//   * DPI scale = GetDpiForWindow(taskbar) / 96 (fallback 96). WPF Width/Height/Left/Top are set in DIP and
//     SetWindowPos is applied in physical px.
//   * Re-positions on DisplaySettingsChanged, WM_DISPLAYCHANGE, WM_SETTINGCHANGE and WM_DPICHANGED.
//     A 2 s DispatcherTimer re-asserts HWND_TOPMOST each tick and re-applies placement when it changed.
//   * Layout: [game ComboBox (Games.All / Games.Current)] [Play] [Stop all] [chips] [open launcher].
//     Chips: 0-4 instances = full chip (dot, name, RAM, switch/stop/kill icon buttons);
//     5-8 = compact chip (dot + 6-char name + "..." menu); more than 8 = first 8 plus "+N".
//     Right-click on the bar: "Hide mini bar" / "Open launcher". Call ShowDocked() to show it again.
//   * Theme: uses Theme brushes directly (live) and refreshes on Theme.Changed.
//   * Every host call is wrapped in try/catch. Stop confirmation is shown by the host.

using System;
using System.Collections.Generic;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Shapes;
using System.Windows.Threading;
using Microsoft.Win32;

namespace RbxMulti
{
    public interface IMiniBarHost : ITeleportHost
    {
        IList<RobloxInstance> Instances { get; }
        void SwitchTo(RobloxInstance i);
        void Stop(RobloxInstance i);
        void Play();
        void StopAll();
        void ShowMainWindow();
        bool CanPlay { get; }
        /// <summary>Live numbers for the usage bars and the "N / recommended" badge. Called on the UI thread.</summary>
        MiniBarStats GetStats();
        /// <summary>Quit the launcher completely (used by the bar's context menu when the main window is hidden).</summary>
        void ExitLauncher();
    }

    /// <summary>Snapshot shown by the mini bar. Recommended and HardMax are 0 while hardware is still being detected.</summary>
    public class MiniBarStats
    {
        public int Running;
        public int Recommended;
        public int HardMax;
        public bool InstanceLimitOverride;
        public double CpuPercent;
        public double UsedRamPercent;
        public double FreeRamGb;
        public double TotalRamGb;
    }

    /// <summary>Traffic-light state of a mini bar meter.</summary>
    public enum MiniLevel
    {
        Calm,
        Warn,
        Critical
    }

    /// <summary>Pure threshold maths for the mini bar meters (same limits as Restrictor).</summary>
    public static class MiniLevels
    {
        public static MiniLevel Ram(double freeRamGb)
        {
            if (freeRamGb < Restrictor.BlockFreeRamGb) return MiniLevel.Critical;
            if (freeRamGb < Restrictor.WarnFreeRamGb) return MiniLevel.Warn;
            return MiniLevel.Calm;
        }

        public static MiniLevel Cpu(double cpuPercent)
        {
            if (cpuPercent >= Restrictor.CriticalCpuPercent) return MiniLevel.Critical;
            if (cpuPercent > Restrictor.WarnCpuPercent) return MiniLevel.Warn;
            return MiniLevel.Calm;
        }

        public static MiniLevel Instances(int running, int recommended, int hardMax)
        {
            if (recommended <= 0) return MiniLevel.Calm;
            if (hardMax > 0 && running >= hardMax) return MiniLevel.Critical;
            if (running >= recommended) return MiniLevel.Warn;
            return MiniLevel.Calm;
        }
    }

    public class MiniBar : Window
    {
        private const int GwlExStyle = -20;
        private const long WsExToolWindow = 0x00000080L;
        private const long WsExNoActivate = 0x08000000L;
        private const uint SwpNoActivate = 0x0010;
        private const uint MonitorDefaultToNearest = 2;
        private const int WmDisplayChange = 0x007E;
        private const int WmSettingChange = 0x001A;
        private const int WmDpiChanged = 0x02E0;

        private static readonly IntPtr HwndTopmost = new IntPtr(-1);

        private static ControlTemplate _flatTemplate;
        private static FontFamily _iconFont;
        private static bool _iconChecked;

        private readonly IMiniBarHost _host;
        private readonly Border _root;
        private readonly ComboBox _gameBox;
        private readonly Button _playBtn;
        private readonly Button _stopAllBtn;
        private readonly Button _openBtn;
        private readonly StackPanel _chipPanel;
        private readonly DispatcherTimer _timer;
        private readonly List<ChipView> _chips = new List<ChipView>();
        private readonly ContextMenu _rootMenu;
        private const double MeterWidth = 46;
        private Border _ramFill;
        private Border _cpuFill;
        private Border _ramTrack;
        private Border _cpuTrack;
        private TextBlock _ramLabel;
        private TextBlock _cpuLabel;
        private Border _badge;
        private TextBlock _badgeText;
        private StackPanel _statsPanel;
        private Border _chipHost;

        private IntPtr _hwnd = IntPtr.Zero;
        private bool _docked;
        private bool _syncingGame;
        private bool _hasLast;
        private bool _systemSubscribed;
        private Placement _last;
        private INotifyCollectionChanged _collection;

        public event EventHandler OpenMainRequested;

        public bool IsDocked
        {
            get { return _docked; }
        }

        public MiniBar(IMiniBarHost host)
        {
            if (host == null) throw new ArgumentNullException("host");
            _host = host;

            WindowStyle = WindowStyle.None;
            AllowsTransparency = true;
            Background = Brushes.Transparent;
            ResizeMode = ResizeMode.NoResize;
            ShowInTaskbar = false;
            Topmost = true;
            ShowActivated = false;
            Focusable = false;
            WindowStartupLocation = WindowStartupLocation.Manual;
            Width = 640;
            Height = 48;
            Resources = new ResourceDictionary();
            ThemedPopups.Install(Resources);

            _root = new Border
            {
                CornerRadius = new CornerRadius(12),
                Background = Theme.Surface,
                BorderBrush = Theme.Border,
                BorderThickness = new Thickness(1),
                Padding = new Thickness(6, 2, 6, 2),
                ClipToBounds = true
            };

            var grid = new Grid();
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            // Left: game + play + stop all.
            var left = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
            _gameBox = new ComboBox
            {
                Width = 112,
                Height = 28,
                Margin = new Thickness(0, 0, 6, 0),
                VerticalContentAlignment = VerticalAlignment.Center,
                Background = Theme.SurfaceAlt,
                Foreground = Theme.Text,
                BorderBrush = Theme.Border,
                FontFamily = Theme.UiFont,
                FontSize = 12,
                ToolTip = "Game"
            };
            ThemedCombo.Apply(_gameBox);
            _gameBox.SelectionChanged += OnGameSelectionChanged;
            left.Children.Add(_gameBox);

            _playBtn = FlatButton("Play", "Launch the selected profile and game",
                Theme.Accent, Theme.AccentHover, Theme.OnAccent, 48, 28);
            _playBtn.FontWeight = FontWeights.SemiBold;
            _playBtn.Click += OnPlayClick;
            left.Children.Add(_playBtn);

            _stopAllBtn = FlatButton("Stop all", "Ends every Roblox client and its child processes immediately (asks first)",
                null, Theme.DangerSoft, Theme.Danger, 58, 28);
            _stopAllBtn.Click += delegate { Safe(delegate { _host.StopAll(); }); };
            left.Children.Add(_stopAllBtn);
            Grid.SetColumn(left, 0);
            grid.Children.Add(left);

            // Usage: RAM and CPU meters plus the "running / recommended" badge.
            _statsPanel = BuildStats();
            Grid.SetColumn(_statsPanel, 1);
            grid.Children.Add(_statsPanel);

            // Middle: chips, clipped to the available width.
            _chipPanel = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
            _chipHost = new Border
            {
                ClipToBounds = true,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(4, 0, 4, 0),
                Child = _chipPanel
            };
            Grid.SetColumn(_chipHost, 2);
            grid.Children.Add(_chipHost);

            // Right: open launcher.
            _openBtn = FlatButton(GlyphBlock(0xE8A7, "â†—"), "Open launcher",
                null, Theme.AccentSoft, Theme.Text, 28, 28);
            _openBtn.Click += delegate { OpenMain(); };
            Grid.SetColumn(_openBtn, 3);
            grid.Children.Add(_openBtn);

            _root.Child = grid;

            _rootMenu = new ContextMenu();
            StyleMenu(_rootMenu);
            _rootMenu.Items.Add(MakeItem("Hide mini bar", delegate { Hide(); }));
            _rootMenu.Items.Add(MakeItem("Open launcher", delegate { OpenMain(); }));
            _rootMenu.Items.Add(MakeItem("Exit launcher", delegate { _host.ExitLauncher(); }));
            _root.ContextMenu = _rootMenu;

            Content = _root;

            _timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
            _timer.Tick += OnTick;

            Theme.Changed += OnThemeChanged;
            Games.CurrentChanged += OnCurrentGameChanged;

            try
            {
                _collection = host.Instances as INotifyCollectionChanged;
                if (_collection != null) _collection.CollectionChanged += OnCollectionChanged;
            }
            catch (Exception)
            {
                _collection = null;
            }

            SyncGame();
            UpdatePlayState();
            RefreshNow();
            UpdateStats();
        }

        // ----- usage meters ----------------------------------------------------------

        private StackPanel BuildStats()
        {
            var panel = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(4, 0, 4, 0) };

            var meters = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
            meters.Children.Add(MeterRow("RAM", out _ramLabel, out _ramTrack, out _ramFill));
            meters.Children.Add(MeterRow("CPU", out _cpuLabel, out _cpuTrack, out _cpuFill));
            panel.Children.Add(meters);

            _badgeText = new TextBlock
            {
                FontFamily = Theme.UiFont,
                FontSize = 13,
                FontWeight = FontWeights.SemiBold,
                VerticalAlignment = VerticalAlignment.Center,
                Text = "0 / ?"
            };
            _badge = new Border
            {
                CornerRadius = new CornerRadius(9),
                Padding = new Thickness(8, 2, 8, 2),
                Margin = new Thickness(8, 0, 0, 0),
                VerticalAlignment = VerticalAlignment.Center,
                BorderThickness = new Thickness(1),
                Child = _badgeText
            };
            panel.Children.Add(_badge);
            return panel;
        }

        private static StackPanel MeterRow(string name, out TextBlock label, out Border track, out Border fill)
        {
            label = new TextBlock
            {
                Text = name,
                FontFamily = Theme.UiFont,
                FontSize = 10,
                Width = 26,
                VerticalAlignment = VerticalAlignment.Center
            };
            fill = new Border
            {
                CornerRadius = new CornerRadius(3),
                HorizontalAlignment = HorizontalAlignment.Left,
                Width = 0
            };
            track = new Border
            {
                Width = MeterWidth,
                Height = 6,
                CornerRadius = new CornerRadius(3),
                BorderThickness = new Thickness(1),
                VerticalAlignment = VerticalAlignment.Center,
                Child = fill
            };
            var row = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 1, 0, 1) };
            row.Children.Add(label);
            row.Children.Add(track);
            return row;
        }

        public static Brush LevelBrush(MiniLevel level)
        {
            switch (level)
            {
                case MiniLevel.Critical: return Theme.Danger;
                case MiniLevel.Warn: return Theme.Warning;
                default: return Theme.Success;
            }
        }

        private static Brush LevelSoft(MiniLevel level)
        {
            switch (level)
            {
                case MiniLevel.Critical: return Theme.DangerSoft;
                case MiniLevel.Warn: return Theme.WarningSoft;
                default: return Theme.SuccessSoft;
            }
        }

        // Reads the host numbers and redraws meters, badge and tooltips. Safe to call at any time.
        public void UpdateStats()
        {
            MiniBarStats s = null;
            try { s = _host.GetStats(); }
            catch (Exception) { s = null; }
            if (s == null) s = new MiniBarStats();

            MiniLevel ramLevel = MiniLevels.Ram(s.FreeRamGb);
            MiniLevel cpuLevel = MiniLevels.Cpu(s.CpuPercent);
            MiniLevel instLevel = MiniLevels.Instances(s.Running, s.Recommended, s.HardMax);

            _ramLabel.Foreground = Theme.TextMuted;
            _cpuLabel.Foreground = Theme.TextMuted;
            _ramTrack.Background = Theme.SurfaceAlt;
            _ramTrack.BorderBrush = Theme.Border;
            _cpuTrack.Background = Theme.SurfaceAlt;
            _cpuTrack.BorderBrush = Theme.Border;

            double inner = MeterWidth - 2;
            _ramFill.Width = Math.Max(0, Math.Min(100, s.UsedRamPercent)) / 100.0 * inner;
            _cpuFill.Width = Math.Max(0, Math.Min(100, s.CpuPercent)) / 100.0 * inner;
            _ramFill.Background = LevelBrush(ramLevel);
            _cpuFill.Background = LevelBrush(cpuLevel);

            string rec = s.Recommended > 0 ? s.Recommended.ToString(System.Globalization.CultureInfo.InvariantCulture) : "?";
            _badgeText.Text = s.Running.ToString(System.Globalization.CultureInfo.InvariantCulture) + " / " + rec;
            _badgeText.Foreground = LevelBrush(instLevel);
            _badge.Background = LevelSoft(instLevel);
            _badge.BorderBrush = LevelBrush(instLevel);

            double usedGb = s.TotalRamGb - s.FreeRamGb;
            string tip = "RAM: " + usedGb.ToString("0.0", System.Globalization.CultureInfo.InvariantCulture) + " GB used of "
                + s.TotalRamGb.ToString("0.0", System.Globalization.CultureInfo.InvariantCulture) + " GB ("
                + s.UsedRamPercent.ToString("0", System.Globalization.CultureInfo.InvariantCulture) + "%)"
                + "\nFree RAM: " + s.FreeRamGb.ToString("0.0", System.Globalization.CultureInfo.InvariantCulture) + " GB"
                + "\nCPU: " + s.CpuPercent.ToString("0", System.Globalization.CultureInfo.InvariantCulture) + "%"
                + "\nInstances: " + s.Running.ToString(System.Globalization.CultureInfo.InvariantCulture) + " running, "
                + (s.Recommended > 0 ? s.Recommended.ToString(System.Globalization.CultureInfo.InvariantCulture) + " recommended, hardware limit "
                    + s.HardMax.ToString(System.Globalization.CultureInfo.InvariantCulture) : "recommended count still being detected")
                + (s.InstanceLimitOverride ? "\nHardware instance-limit override ON; extra launches require confirmation." : "");
            _statsPanel.ToolTip = tip;
            _badge.ToolTip = tip;
        }

        // Rebuilds chips from host.Instances. Must run on the UI thread.
        public void RefreshNow()
        {
            ClearChips();

            var list = new List<RobloxInstance>();
            try
            {
                IList<RobloxInstance> src = _host.Instances;
                if (src != null)
                {
                    foreach (RobloxInstance r in src)
                    {
                        if (r != null) list.Add(r);
                    }
                }
            }
            catch (Exception)
            {
                // Keep whatever was collected.
            }

            int n = list.Count;
            if (n == 0)
            {
                _chipPanel.Children.Add(new TextBlock
                {
                    Text = "All instances tucked away",
                    Foreground = Theme.TextMuted,
                    FontSize = 11,
                    VerticalAlignment = VerticalAlignment.Center,
                    FontFamily = Theme.UiFont
                });
            }
            else
            {
                bool compact = n > 1;
                int shown = n > 8 ? 8 : n;
                for (int i = 0; i < shown; i++)
                {
                    ChipView v = BuildChip(list[i], compact);
                    _chips.Add(v);
                    _chipPanel.Children.Add(v.Root);
                }
                if (n > 8)
                {
                    _chipPanel.Children.Add(new TextBlock
                    {
                        Text = "+" + (n - 8).ToString(),
                        Foreground = Theme.TextMuted,
                        FontSize = 12,
                        FontWeight = FontWeights.SemiBold,
                        VerticalAlignment = VerticalAlignment.Center,
                        Margin = new Thickness(2, 0, 0, 0)
                    });
                }
            }

            UpdatePlayState();
            SyncGame();
        }

        // Recomputes the dock rectangle now and applies it.
        public void Reposition()
        {
            Place(true);
        }

        // Shows the bar, positions it, keeps it topmost, and starts the 2 s maintenance timer.
        public void ShowDocked()
        {
            try
            {
                Show();
                Place(true);
                Topmost = true;
                if (!_timer.IsEnabled) _timer.Start();
            }
            catch (Exception)
            {
                // Never crash the launcher because of the mini bar.
            }
        }

        protected override void OnSourceInitialized(EventArgs e)
        {
            base.OnSourceInitialized(e);
            try
            {
                _hwnd = new WindowInteropHelper(this).Handle;
                if (_hwnd != IntPtr.Zero)
                {
                    long ex = MiniBarNative.GetWindowLongPtr(_hwnd, GwlExStyle).ToInt64();
                    ex |= WsExToolWindow | WsExNoActivate;
                    MiniBarNative.SetWindowLongPtr(_hwnd, GwlExStyle, new IntPtr(ex));

                    HwndSource src = HwndSource.FromHwnd(_hwnd);
                    if (src != null) src.AddHook(WndProc);
                }
            }
            catch (Exception)
            {
                // Style tweaks are best effort.
            }

            try
            {
                SystemEvents.DisplaySettingsChanged += OnDisplaySettingsChanged;
                _systemSubscribed = true;
            }
            catch (Exception)
            {
                _systemSubscribed = false;
            }
        }

        protected override void OnClosed(EventArgs e)
        {
            try
            {
                _timer.Stop();
                Theme.Changed -= OnThemeChanged;
                Games.CurrentChanged -= OnCurrentGameChanged;
                ThemeResources.Unregister(Resources);
                if (_systemSubscribed)
                {
                    SystemEvents.DisplaySettingsChanged -= OnDisplaySettingsChanged;
                    _systemSubscribed = false;
                }
                if (_collection != null)
                {
                    _collection.CollectionChanged -= OnCollectionChanged;
                    _collection = null;
                }
                ClearChips();
            }
            catch (Exception)
            {
                // Ignore during shutdown.
            }
            base.OnClosed(e);
        }

        // ----- placement -------------------------------------------------------------

        private struct Placement
        {
            public int X;
            public int Y;
            public int W;
            public int H;
            public int Dpi;
            public bool Docked;
        }

        private static bool SamePlacement(Placement a, Placement b)
        {
            return a.X == b.X && a.Y == b.Y && a.W == b.W && a.H == b.H && a.Dpi == b.Dpi && a.Docked == b.Docked;
        }

        private void Place(bool force)
        {
            try
            {
                if (_hwnd == IntPtr.Zero)
                {
                    _hwnd = new WindowInteropHelper(this).Handle;
                    if (_hwnd == IntPtr.Zero) return;
                }

                Placement p = Compute();
                _docked = p.Docked;
                double scale = p.Dpi / 96.0;

                if (force || !_hasLast || !SamePlacement(p, _last))
                {
                    Width = p.W / scale;
                    Height = p.H / scale;
                    Left = p.X / scale;
                    Top = p.Y / scale;
                }

                // Re-asserts topmost every call and moves the window if needed.
                MiniBarNative.SetWindowPos(_hwnd, HwndTopmost, p.X, p.Y, p.W, p.H, SwpNoActivate);
                _last = p;
                _hasLast = true;
            }
            catch (Exception)
            {
                // Placement is best effort.
            }
        }

        private Placement Compute()
        {
            Placement p = new Placement();
            IntPtr tb = MiniBarNative.FindWindow("Shell_TrayWnd", null);

            int dpi = 96;
            if (tb != IntPtr.Zero) dpi = DpiOf(tb);
            else if (_hwnd != IntPtr.Zero) dpi = DpiOf(_hwnd);
            p.Dpi = dpi;

            double scale = dpi / 96.0;
            int minW = (int)Math.Round(500 * scale);
            int maxW = (int)Math.Round(660 * scale);
            int gap = (int)Math.Round(8 * scale);
            int defaultH = (int)Math.Round(48 * scale);

            MiniBarNative.MbRect tr;
            if (tb != IntPtr.Zero && MiniBarNative.GetWindowRect(tb, out tr))
            {
                MiniBarNative.MbMonitorInfo mi = new MiniBarNative.MbMonitorInfo();
                mi.cbSize = Marshal.SizeOf(typeof(MiniBarNative.MbMonitorInfo));
                IntPtr mon = MiniBarNative.MonitorFromWindow(tb, MonitorDefaultToNearest);
                MiniBarNative.GetMonitorInfo(mon, ref mi);

                int tbW = tr.Right - tr.Left;
                int tbH = tr.Bottom - tr.Top;
                if (tbW >= tbH)
                {
                    // Horizontal taskbar (bottom or top): sit over it, left of the tray.
                    p.Docked = true;
                    p.H = tbH > 0 ? tbH : defaultH;
                    p.Y = tr.Top;

                    int rightEdge;
                    MiniBarNative.MbRect trr;
                    IntPtr tray = MiniBarNative.FindWindowEx(tb, IntPtr.Zero, "TrayNotifyWnd", null);
                    if (tray != IntPtr.Zero && MiniBarNative.GetWindowRect(tray, out trr))
                    {
                        rightEdge = trr.Left - gap;
                    }
                    else
                    {
                        rightEdge = tr.Right - (int)Math.Round(300 * scale);
                    }

                    int leftBound = tr.Left + (int)(tbW * 0.5);
                    int w = rightEdge - leftBound;
                    if (w < minW) w = minW;
                    if (w > maxW) w = maxW;
                    p.W = w;
                    p.X = rightEdge - w;
                }
                else
                {
                    // Vertical taskbar: float in the bottom-right of the work area.
                    p.Docked = false;
                    p.W = maxW;
                    p.H = defaultH;
                    p.X = mi.rcWork.Right - p.W;
                    p.Y = mi.rcWork.Bottom - p.H;
                }
            }
            else
            {
                // No taskbar found: bottom-right of the primary work area.
                p.Docked = false;
                Rect wa = SystemParameters.WorkArea;
                p.W = maxW;
                p.H = defaultH;
                p.X = (int)Math.Round(wa.Right * scale) - p.W - (int)Math.Round(300 * scale);
                p.Y = (int)Math.Round(wa.Bottom * scale) - p.H;
            }

            return p;
        }

        private static int DpiOf(IntPtr hwnd)
        {
            try
            {
                uint d = MiniBarNative.GetDpiForWindow(hwnd);
                if (d >= 48 && d <= 1000) return (int)d;
            }
            catch (Exception)
            {
                // Older Windows: entry point missing.
            }
            return 96;
        }

        private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
        {
            if (msg == WmDisplayChange || msg == WmSettingChange || msg == WmDpiChanged)
            {
                Dispatcher.BeginInvoke(DispatcherPriority.Normal, new Action(Reposition));
            }
            return IntPtr.Zero;
        }

        private void OnDisplaySettingsChanged(object sender, EventArgs e)
        {
            Ui(Reposition);
        }

        private void OnTick(object sender, EventArgs e)
        {
            if (!IsVisible) return;
            UpdatePlayState();
            UpdateStats();
            RefreshTeleport();
            Place(false);
        }

        // ----- host-facing actions ---------------------------------------------------

        private void OpenMain()
        {
            try
            {
                EventHandler h = OpenMainRequested;
                if (h != null) h(this, EventArgs.Empty);
            }
            catch (Exception)
            {
            }
            Safe(delegate { _host.ShowMainWindow(); });
        }

        private void OnPlayClick(object sender, RoutedEventArgs e)
        {
            if (!CanPlay()) return;
            Safe(delegate { _host.Play(); });
        }

        private bool CanPlay()
        {
            try
            {
                return _host.CanPlay;
            }
            catch (Exception)
            {
                return false;
            }
        }

        private void UpdatePlayState()
        {
            bool can = CanPlay();
            _playBtn.IsEnabled = can;
            _playBtn.Opacity = can ? 1.0 : 0.45;
        }

        private static void Safe(Action a)
        {
            try
            {
                a();
            }
            catch (Exception)
            {
                // Host failures must never crash the bar.
            }
        }

        // ----- games -----------------------------------------------------------------

        private void SyncGame()
        {
            _syncingGame = true;
            try
            {
                IList<GameInfo> all = Games.All;
                if (!ReferenceEquals(_gameBox.ItemsSource, all)) _gameBox.ItemsSource = all;
                _gameBox.SelectedItem = Games.Current;
            }
            catch (Exception)
            {
            }
            finally
            {
                _syncingGame = false;
            }
        }

        private void OnGameSelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_syncingGame) return;
            GameInfo g = _gameBox.SelectedItem as GameInfo;
            if (g == null) return;
            if (ReferenceEquals(Games.Current, g)) return;
            Safe(delegate { Games.Select(g); });
        }

        private void OnCurrentGameChanged()
        {
            Ui(SyncGame);
        }

        // ----- theme / collection / marshalling --------------------------------------

        private void OnThemeChanged()
        {
            Ui(delegate
            {
                try
                {
                    _root.Background = Theme.Surface;
                    _root.BorderBrush = Theme.Border;
                    _gameBox.Background = Theme.SurfaceAlt;
                    _gameBox.Foreground = Theme.Text;
                    _gameBox.BorderBrush = Theme.Border;
                    StyleMenu(_rootMenu);
                    foreach (object o in _rootMenu.Items)
                    {
                        MenuItem mi = o as MenuItem;
                        if (mi != null) { mi.Foreground = Theme.Text; mi.Background = Theme.Surface; }
                    }
                    RefreshNow();
                    UpdateStats();
                }
                catch (Exception)
                {
                    // A theme change must never surface as an error.
                }
            });
        }

        private void OnCollectionChanged(object sender, NotifyCollectionChangedEventArgs e)
        {
            Ui(RefreshNow);
        }

        private void Ui(Action a)
        {
            if (Dispatcher.CheckAccess()) a();
            else Dispatcher.BeginInvoke(DispatcherPriority.Normal, a);
        }

        // ----- chips -----------------------------------------------------------------

        private sealed class ChipView
        {
            public RobloxInstance Inst;
            public Border Root;
            public Ellipse Dot;
            public TextBlock NameText;
            public TextBlock RamText;
            public TextBlock TpText;      // teleport state glyph: ... / ok / ! / x
            public int NameLen;
            public PropertyChangedEventHandler Handler;
        }

        private void ClearChips()
        {
            foreach (ChipView v in _chips)
            {
                try
                {
                    if (v.Handler != null) v.Inst.PropertyChanged -= v.Handler;
                }
                catch (Exception)
                {
                }
            }
            _chips.Clear();
            _chipPanel.Children.Clear();
        }

        private ChipView BuildChip(RobloxInstance inst, bool compact)
        {
            var v = new ChipView { Inst = inst, NameLen = compact ? 6 : 10 };

            var sp = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };

            v.Dot = new Ellipse
            {
                Width = 8,
                Height = 8,
                Margin = new Thickness(0, 0, 5, 0),
                VerticalAlignment = VerticalAlignment.Center
            };
            sp.Children.Add(v.Dot);

            v.NameText = new TextBlock
            {
                FontSize = 12,
                FontWeight = FontWeights.SemiBold,
                Foreground = Theme.Text,
                FontFamily = Theme.UiFont,
                VerticalAlignment = VerticalAlignment.Center
            };
            sp.Children.Add(v.NameText);

            v.TpText = new TextBlock
            {
                FontSize = 11,
                FontWeight = FontWeights.Bold,
                FontFamily = Theme.UiFont,
                Margin = new Thickness(4, 0, 0, 0),
                VerticalAlignment = VerticalAlignment.Center,
                Visibility = Visibility.Collapsed
            };
            sp.Children.Add(v.TpText);

            if (!compact)
            {
                v.RamText = new TextBlock
                {
                    FontSize = 11,
                    Foreground = Theme.TextMuted,
                    FontFamily = Theme.UiFont,
                    Margin = new Thickness(6, 0, 4, 0),
                    VerticalAlignment = VerticalAlignment.Center
                };
                sp.Children.Add(v.RamText);

                sp.Children.Add(FlatButton(GlyphBlock(0xE7B3, ">"), "Switch to this instance",
                    null, Theme.AccentSoft, Theme.Text, 22, 22,
                    delegate { Safe(delegate { _host.SwitchTo(inst); }); }));
                sp.Children.Add(FlatButton(GlyphBlock(0xE71A, "â– "), "Ends this Roblox client and all its child processes immediately, like Process Explorer > Kill Process Tree",
                    null, Theme.DangerSoft, Theme.Danger, 22, 22,
                    delegate { Safe(delegate { _host.Stop(inst); }); }));
            }
            else
            {
                // Compact chip: click anywhere on it for Switch to / Stop / Kill.
            }

            v.Root = new Border
            {
                CornerRadius = new CornerRadius(8),
                Background = Theme.SurfaceAlt,
                BorderBrush = Theme.Border,
                BorderThickness = new Thickness(1),
                Padding = compact ? new Thickness(7, 3, 8, 3) : new Thickness(6, 1, 2, 1),
                Margin = new Thickness(0, 0, 4, 0),
                VerticalAlignment = VerticalAlignment.Center,
                Child = sp
            };
            if (compact)
            {
                ContextMenu menu = BuildInstanceMenu(inst);
                v.Root.Cursor = Cursors.Hand;
                v.Root.MouseLeftButtonUp += delegate
                {
                    FillTeleportItems(menu, inst);
                    menu.PlacementTarget = v.Root;
                    menu.IsOpen = true;
                };
            }

            v.Handler = delegate { Ui(delegate { UpdateChip(v); }); };
            inst.PropertyChanged += v.Handler;
            UpdateChip(v);
            return v;
        }

        private void UpdateChip(ChipView v)
        {
            RobloxInstance r = v.Inst;
            string label = Label(r);
            v.NameText.Text = Shorten(label, v.NameLen);
            if (v.RamText != null) v.RamText.Text = r.MemoryText;
            v.Dot.Fill = KindBrush(r.StatusKind);

            string status = string.IsNullOrEmpty(r.Status) ? "Stopped" : r.Status;
            string tip = label + "\nPID " + r.Pid.ToString() + "\nStatus: " + status + "\nRAM: " + r.MemoryText;

            // Teleport state: "..." while this instance teleports, then a coloured mark for ChipSeconds.
            Brush border = Theme.Border;
            string mark = null;
            Brush markBrush = Theme.TextMuted;
            try
            {
                if (_host.TeleportBusy && ReferenceEquals(_host.TeleportSource, r))
                {
                    mark = "…"; markBrush = Theme.Accent; border = Theme.Accent;
                    tip += "\nTeleporting... (Cancel is in the chip menu)";
                }
                else
                {
                    TeleportResult res = _host.LastTeleportResult;
                    double age = (DateTime.UtcNow - _host.LastTeleportUtc).TotalSeconds;
                    if (res != null && ReferenceEquals(_host.LastTeleportSource, r) && age >= 0 && age < TeleportCardRow.ChipSeconds)
                    {
                        tip += "\nTeleport: " + TeleportText.Chip(res) + (res.Status == TeleportStatus.Failed ? "" : "\n" + res.Message);
                        switch (res.Status)
                        {
                            case TeleportStatus.Success: mark = "✓"; markBrush = Theme.Success; border = Theme.Success; break;
                            case TeleportStatus.Likely: mark = "!"; markBrush = Theme.Warning; border = Theme.Warning; break;
                            case TeleportStatus.Cancelled: mark = "–"; markBrush = Theme.TextMuted; break;
                            default: mark = "✕"; markBrush = Theme.Danger; border = Theme.Danger; break;
                        }
                    }
                }
            }
            catch (Exception)
            {
                mark = null;
            }
            if (v.TpText != null)
            {
                v.TpText.Text = mark ?? "";
                v.TpText.Foreground = markBrush;
                v.TpText.Visibility = mark == null ? Visibility.Collapsed : Visibility.Visible;
            }
            v.Root.BorderBrush = border;
            v.Root.ToolTip = tip;
        }

        /// <summary>Re-reads the teleport state of every chip (busy ring, result mark). UI thread.</summary>
        public void RefreshTeleport()
        {
            foreach (ChipView v in _chips)
            {
                UpdateChip(v);
            }
        }

        // Adds (or refreshes) the "Teleport to <user>" items of a chip menu. Items are flat (a submenu would not follow the dark theme).
        private void FillTeleportItems(ContextMenu m, RobloxInstance inst)
        {
            try
            {
                for (int i = m.Items.Count - 1; i >= 0; i--)
                {
                    MenuItem old = m.Items[i] as MenuItem;
                    if (old != null && "tp".Equals(old.Tag)) m.Items.RemoveAt(i);
                }
                StyleMenu(m);
                bool busyHere = _host.TeleportBusy && ReferenceEquals(_host.TeleportSource, inst);
                if (busyHere)
                {
                    MenuItem c = MakeItem("Cancel teleport", delegate { _host.CancelTeleport(); });
                    c.Tag = "tp";
                    m.Items.Add(c);
                    return;
                }
                IList<RobloxInstance> targets = _host.TeleportTargets(inst);
                if (targets.Count == 0)
                {
                    string why = _host.TeleportBlockReason(inst, null);
                    MenuItem none = MakeItem("Teleport: no other running instance", delegate { });
                    none.IsEnabled = false;
                    none.Tag = "tp";
                    none.ToolTip = (why ?? "") + "\n" + TeleportText.Notes;
                    m.Items.Add(none);
                    return;
                }
                foreach (RobloxInstance t in targets)
                {
                    RobloxInstance target = t;
                    MenuItem it = MakeItem("Teleport to " + TeleportText.UserOf(target), delegate { _host.StartTeleport(inst, target); });
                    string reason = _host.TeleportBlockReason(inst, target);
                    it.IsEnabled = reason == null;
                    it.ToolTip = reason == null ? TeleportText.Notes : reason + "\n" + TeleportText.Notes;
                    it.Tag = "tp";
                    m.Items.Add(it);
                }
            }
            catch (Exception)
            {
                // Menu building must never throw.
            }
        }

        private ContextMenu BuildInstanceMenu(RobloxInstance inst)
        {
            var m = new ContextMenu();
            StyleMenu(m);
            m.Items.Add(MakeItem("Switch to", delegate { Safe(delegate { _host.SwitchTo(inst); }); }));
            m.Items.Add(MakeItem("Stop", delegate { Safe(delegate { _host.Stop(inst); }); }));
            return m;
        }

        private static string Label(RobloxInstance r)
        {
            string a = r.AccountName;
            if (string.IsNullOrEmpty(a)) a = r.Name;
            return a ?? string.Empty;
        }

        private static string Shorten(string s, int max)
        {
            if (string.IsNullOrEmpty(s)) return string.Empty;
            if (s.Length <= max) return s;
            return s.Substring(0, max) + "\u2026";
        }

        private static Brush KindBrush(string kind)
        {
            switch (kind)
            {
                case "Running": return Theme.Success;
                case "Starting": return Theme.Warning;
                case "Error": return Theme.Danger;
                default: return Theme.TextMuted;
            }
        }

        // ----- buttons and menus -----------------------------------------------------

        private static Button FlatButton(object content, string tip, Brush normal, Brush hover, Brush fg, double w, double h)
        {
            return FlatButton(content, tip, normal, hover, fg, w, h, null);
        }

        private static Button FlatButton(object content, string tip, Brush normal, Brush hover, Brush fg, double w, double h, RoutedEventHandler onClick)
        {
            var b = new Button();
            b.Template = FlatTemplate();
            b.Content = content;
            if (!string.IsNullOrEmpty(tip)) b.ToolTip = tip;
            b.Width = w;
            b.Height = h;
            b.Padding = new Thickness(6, 0, 6, 0);
            b.Margin = new Thickness(1, 0, 1, 0);
            b.BorderThickness = new Thickness(0);
            b.FontFamily = Theme.UiFont;
            b.FontSize = 12;
            b.Foreground = fg;
            b.Cursor = Cursors.Hand;
            b.Focusable = false;

            Brush rest = normal;
            if (rest == null) rest = Brushes.Transparent;
            b.Background = rest;

            b.MouseEnter += delegate
            {
                if (b.IsEnabled && hover != null) b.Background = hover;
            };
            b.MouseLeave += delegate { b.Background = rest; };

            if (onClick != null) b.Click += onClick;
            return b;
        }

        private static ControlTemplate FlatTemplate()
        {
            if (_flatTemplate != null) return _flatTemplate;

            var t = new ControlTemplate(typeof(Button));
            var bd = new FrameworkElementFactory(typeof(Border), "bd");
            bd.SetValue(Border.CornerRadiusProperty, new CornerRadius(8));
            bd.SetBinding(Border.BackgroundProperty, new Binding("Background") { RelativeSource = RelativeSource.TemplatedParent });

            var cp = new FrameworkElementFactory(typeof(ContentPresenter));
            cp.SetValue(ContentPresenter.HorizontalAlignmentProperty, HorizontalAlignment.Center);
            cp.SetValue(ContentPresenter.VerticalAlignmentProperty, VerticalAlignment.Center);
            cp.SetBinding(ContentPresenter.ContentProperty, new Binding("Content") { RelativeSource = RelativeSource.TemplatedParent });
            cp.SetBinding(TextElement.ForegroundProperty, new Binding("Foreground") { RelativeSource = RelativeSource.TemplatedParent });

            bd.AppendChild(cp);
            t.VisualTree = bd;
            _flatTemplate = t;
            return t;
        }

        private static TextBlock GlyphBlock(int codePoint, string fallback)
        {
            EnsureIconFont();
            var tb = new TextBlock
            {
                FontSize = 12,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center
            };
            if (_iconFont != null)
            {
                tb.FontFamily = _iconFont;
                tb.Text = ((char)codePoint).ToString();
            }
            else
            {
                tb.FontFamily = Theme.UiFont;
                tb.Text = fallback;
            }
            return tb;
        }

        private static void EnsureIconFont()
        {
            if (_iconChecked) return;
            _iconChecked = true;
            try
            {
                foreach (FontFamily f in Fonts.SystemFontFamilies)
                {
                    string n = f.Source;
                    if (n == "Segoe Fluent Icons" || n == "Segoe MDL2 Assets")
                    {
                        _iconFont = f;
                        break;
                    }
                }
            }
            catch (Exception)
            {
                _iconFont = null;
            }
        }

        private static MenuItem MakeItem(string header, Action action)
        {
            var mi = new MenuItem { Header = header, Foreground = Theme.Text, Background = Theme.Surface };
            mi.Click += delegate { Safe(action); };
            return mi;
        }

        private static void StyleMenu(ContextMenu m)
        {
            m.Background = Theme.Surface;
            m.Foreground = Theme.Text;
            m.BorderBrush = Theme.Border;
            m.FontFamily = Theme.UiFont;
        }

        // ----- native interop --------------------------------------------------------

        private static class MiniBarNative
        {
            [StructLayout(LayoutKind.Sequential)]
            internal struct MbRect
            {
                public int Left;
                public int Top;
                public int Right;
                public int Bottom;
            }

            [StructLayout(LayoutKind.Sequential)]
            internal struct MbMonitorInfo
            {
                public int cbSize;
                public MbRect rcMonitor;
                public MbRect rcWork;
                public uint dwFlags;
            }

            [DllImport("user32.dll", CharSet = CharSet.Unicode)]
            internal static extern IntPtr FindWindow(string lpClassName, string lpWindowName);

            [DllImport("user32.dll", CharSet = CharSet.Unicode)]
            internal static extern IntPtr FindWindowEx(IntPtr parent, IntPtr childAfter, string lpszClass, string lpszWindow);

            [DllImport("user32.dll")]
            internal static extern bool GetWindowRect(IntPtr hWnd, out MbRect rect);

            [DllImport("user32.dll")]
            internal static extern IntPtr MonitorFromWindow(IntPtr hwnd, uint flags);

            [DllImport("user32.dll")]
            internal static extern bool GetMonitorInfo(IntPtr hMonitor, ref MbMonitorInfo info);

            [DllImport("user32.dll")]
            internal static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter, int x, int y, int cx, int cy, uint flags);

            [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")]
            internal static extern IntPtr GetWindowLongPtr(IntPtr hWnd, int index);

            [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW")]
            internal static extern IntPtr SetWindowLongPtr(IntPtr hWnd, int index, IntPtr newValue);

            [DllImport("user32.dll", EntryPoint = "GetDpiForWindow")]
            internal static extern uint GetDpiForWindow(IntPtr hwnd);
        }
    }
}
