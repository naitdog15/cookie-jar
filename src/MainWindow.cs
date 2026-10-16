// MainWindow.cs
//
// Main window: hardware summary, capacity warnings, profile picker, instance cards, Advanced and
// Settings panels, layout tools and activity log. The whole UI is built in code (no XAML/BAML).
// C# 5 only (no ?., $"", nameof, out var, expression-bodied members).
//
// PUBLIC / INTERNAL API (for the UI team and MainWindow.Accounts.cs)
// -------------------------------------------------------------------
// public partial class MainWindow : Window, IMiniBarHost
//     MainWindow()                          normal start.
//     MainWindow(bool miniBarOnly)          start hidden with the mini bar shown (AutoStart.MiniBarFlag).
//
// internal static bool StartMiniBarOnly    set from the constructor argument; Program.cs may read it.
//
// internal AntiIdleClicker Clicker         created and started in the constructor, disposed on close.
// internal readonly Dictionary<int,string> InstAccountId
//                                          RobloxInstance.Id -> account id. Filled by Accounts.cs after launch
//                                          or adoption. Removed automatically when the card is removed.
// internal Action PlayHook                 set by Accounts.cs. The mini bar Play button calls it through
//                                          PlayFromMiniBar(). CanPlay is true only when it is set and idle.
// internal LaunchProfile ActiveProfile     set by Accounts.cs when the profile selection changes (may be null).
//                                          The Advanced rows rebuild themselves on the next tick when it changes.
// internal void ApplyRamAndPlacement(RobloxInstance inst)
//                                          applies the account RAM setting, then places the window(s):
//                                          AutoTile on -> all running instances are tiled with distinct cells;
//                                          AutoTile off -> only the account layout override (if any) is applied.
//                                          Window lookup runs on a worker thread (20 s timeout); placement is UI.
// internal void TileAll()                  re-tile every running instance now (same rules).
// internal void BringAllToFront(IList<RobloxInstance> ordered)
//                                          WindowManager.BringAllToFront on their windows in order (last ends on top).
// public void SwitchTo(RobloxInstance inst)
//                                          bring one instance to the front (public because IMiniBarHost requires it).
// internal void PlayFromMiniBar()          invokes PlayHook; used by the mini bar Play button.
// internal void TrackDialog(Window dialog) registers an open dialog so its title bar follows the theme.
//
// Theme: every brush used here is re-read from Theme on Theme.Changed (text, log, bars, cards, Advanced rows).
// OnThemeChanged is wrapped in try/catch so toggling never shows an error dialog.
// Persisted settings: minibar.settings next to Games.SettingsPath (key=value, UTF-8):
//     keepalive=true|false   minibar=true|false
// -------------------------------------------------------------------

using System;
using System.Collections.Generic;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Text;
using System.Threading;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;

namespace RbxMulti
{
    /// <summary>
    /// Main window: hardware summary, capacity warnings, profile picker, instance cards, Advanced and
    /// Settings panels and activity log. The whole UI is built in code (no XAML/BAML). Compiles as C# 5.
    /// </summary>
    public partial class MainWindow : Window, IMiniBarHost
    {
        private const int MaxLogLines = 500;
        private const int TickMilliseconds = 1000;
        private const int ErrorBannerHoldSeconds = 8;
        private const string LaunchLabel = "_Launch new instance (website login)";

        // Segoe MDL2 Assets glyphs for the theme switch icon.
        private const string SunGlyph = "";
        private const string MoonGlyph = "";

        // Auto-click and RAM defaults.
        private const int DefaultIntervalSeconds = 240;
        private const int MinIntervalSeconds = 30;
        private const int MaxIntervalSeconds = 1800;
        private const int NudgeMinutes = 5;
        private const int DefaultCustomRamMb = 2048;
        private const int MinCustomRamMb = RamScale.MinMb;
        private const int MaxCustomRamMb = 65536;
        private const int MinLayoutWidth = 200;
        private const int MinLayoutHeight = 150;
        private const string SettingsFileName = "minibar.settings";
        private const string KeepAliveKey = "keepalive";
        private const string EfficiencyKey = "bgefficiency";
        private const string MiniBarKey = "minibar";

        /// <summary>Set from the MainWindow(bool) constructor. Program.cs may read it.</summary>
        internal static bool StartMiniBarOnly;

        /// <summary>Test hook for scratch hosts only: when true the constructor does not adopt running Roblox clients. Never set by the app.</summary>
        internal static bool SkipAdoption;

        // Back end.
        private readonly InstanceManager _manager;
        private readonly ResourceMonitor _monitor;
        private readonly DispatcherTimer _timer;
        private readonly Dictionary<RobloxInstance, InstanceCard> _cards = new Dictionary<RobloxInstance, InstanceCard>();

        // Contract members used by MainWindow.Accounts.cs.
        internal AntiIdleClicker Clicker;
        internal readonly Dictionary<int, string> InstAccountId = new Dictionary<int, string>();
        internal Action PlayHook;
        internal LaunchProfile ActiveProfile;

        // State. Restrictor and HardwareProfile stay null until detection finishes.
        private HardwareProfile _profile;
        private Restrictor _restrictor;
        private int _workCount;              // global busy count: launch, save profile, delete profile
        private bool _syncingTheme;
        private int _logLineCount;
        private DateTime _bannerHoldUntilUtc = DateTime.MinValue;

        // Per-instance RAM settings made in this window (instance Id -> setting).
        private readonly Dictionary<int, RamSetting> _ram = new Dictionary<int, RamSetting>();
        private readonly List<Window> _openDialogs = new List<Window>();
        private readonly List<TextBlock> _primaryTexts = new List<TextBlock>();
        private readonly List<TextBlock> _mutedTexts = new List<TextBlock>();
        private MonitorInfo[] _monitors = new MonitorInfo[0];
        private string _advancedSignature = "";
        private int _intervalSeconds = DefaultIntervalSeconds;
        private bool _nudge;
        private bool _startHidden;
        private bool _keepAlive;
        private bool _bgEfficiency = true;
        private CheckBox _efficiencyCheck;
        private bool _autoRam = true;
        private bool _overrideInstanceLimit;
        private CheckBox _overrideLimitCheck;
        private CheckBox _autoRamCheck;
        private TextBlock _autoRamStatus;
        private readonly Dictionary<int, int> _autoRamCaps = new Dictionary<int, int>();
        private readonly List<EfficiencyTarget> _effTargets = new List<EfficiencyTarget>();

        /// <summary>Lowers CPU priority and turns on Windows efficiency mode for clients that are not in front.</summary>
        internal readonly BackgroundEfficiency Efficiency = new BackgroundEfficiency();
        private bool _exiting;
        private bool _settingsSync;
        private MiniBar _miniBar;

        // UI references.
        private Button _exitButton;
        private ToggleSwitch _themeSwitch;
        private TextBlock _themeIcon;
        private TextBlock _cpuText;
        private TextBlock _gpuText;
        private TextBlock _coresText;
        private TextBlock _ramText;
        private TextBlock _recommendText;
        private TextBlock _usageText;
        private TextBlock _capacityText;
        private MeterBar _capacityBar;
        private MeterBar _cpuBar;
        private MeterBar _ramBar;
        private TextBlock _ramSubText;
        private TextBlock _vramText;
        private Grid _statGrid;
        private int _statCols;
        private readonly List<FrameworkElement> _statTiles = new List<FrameworkElement>();
        private readonly List<Border> _statDividers = new List<Border>();
        private InfoBanner _banner;
        private StackPanel _instanceList;
        private UIElement _emptyState;
        private TextBox _logBox;

        // Advanced panel.
        private Expander _advancedExpander;
        private CheckBox _autoMaster;
        private CheckBox _nudgeCheck;
        private TextBox _intervalBox;
        private TextBlock _ramSummary;
        private StackPanel _ramWarnings;
        private string _ramWarningsKey;
        private StackPanel _ramRows;
        private TextBlock _monitorsText;

        // Settings panel.
        private Expander _settingsExpander;
        private CheckBox _miniBarCheck;
        private CheckBox _autoStartCheck;
        private CheckBox _keepAliveCheck;

        /// <summary>Normal start.</summary>
        public MainWindow() : this(false)
        {
        }

        /// <summary>When miniBarOnly is true the window starts hidden and the mini bar is shown instead.</summary>
        public MainWindow(bool miniBarOnly)
        {
            _startHidden = miniBarOnly;
            StartMiniBarOnly = miniBarOnly;

            // Theme first, so every brush the UI uses is already in the right colours.
            Theme.Apply(Theme.SystemPrefersDark());

            Title = "Cookie Jar";
            Width = 1000;
            Height = 920;
            MinWidth = 800;
            MinHeight = 700;
            WindowStartupLocation = WindowStartupLocation.CenterScreen;
            FontFamily = Theme.UiFont;
            Background = Theme.Bg;
            Foreground = Theme.Text;

            // Scrollbars: theme brushes are reached through these keys, so they update with the theme.
            ThemedPopups.Install(Resources);
            Resources.Add(typeof(ScrollBar), ThemedScroll.Create());
            SmoothScroll.Attach(this);

            // Constructed on the UI thread: InstanceManager captures this thread's dispatcher.
            _manager = new InstanceManager();
            _monitor = new ResourceMonitor();

            LoadMiniBarSettings();
            Efficiency.Enabled = _bgEfficiency;

            Content = BuildRoot();
            Efficiency.Log = AppendLog;

            _manager.Log += AppendLogSafe;
            _manager.Instances.CollectionChanged += OnInstancesChanged;
            Theme.Changed += OnThemeChanged;
            Closing += OnClosing;
            Closed += OnClosed;
            StateChanged += OnStateChanged;
            _tray = new TrayIcon(
                delegate { ShowMainWindow(); },
                ToggleMiniBarFromTray,
                delegate { return _miniBar != null && _miniBar.IsVisible; },
                BringAllFromTray,
                RequestExit,
                StopAllWithConfirm);
            if (Application.Current != null)
            {
                Application.Current.SessionEnding += delegate { OnSessionEnding(); };
                // Whatever ends the app, never leave a ghost tray icon behind.
                Application.Current.Exit += delegate
                {
                    if (_tray != null) { _tray.Dispose(); _tray = null; }
                };
            }
            Loaded += OnLoadedWindow;

            Clicker = new AntiIdleClicker();
            Clicker.Log += AppendLogSafe;
            Clicker.Start();
            InitInput();

            Teleporter = new TeleportService();
            Teleporter.Log += AppendLogSafe;
            Teleporter.Status += OnTeleportStatus;

            MemoryGovernor.SetInstanceCountHint(_manager.Instances.Count);

            _timer = new DispatcherTimer();
            _timer.Interval = TimeSpan.FromMilliseconds(TickMilliseconds);
            _timer.Tick += OnTimerTick;
            IsVisibleChanged += OnVisibleChanged;

            UpdateEmptyState();
            RefreshAccountList();
            RefreshProfileList(null);
            if (AccountStore.LoadError != null)
            {
                AppendLog("Warning: " + AccountStore.LoadError);
                ShowTemporaryError(AccountStore.LoadError);
            }
            else if (_accountCount == 0)
            {
                _accountsExpander.IsExpanded = true;
            }
            int legacy = ProfileStore.LegacyFileCount();
            if (legacy > 0)
            {
                AppendLog(legacy + " old single-login profile file(s) were found. They are left untouched and are no longer used or shown.");
            }
            SampleMonitor();
            RefreshButtons();
            RefreshLiveInfo();
            RefreshAdvancedIfChanged();
            _timer.Start();
            DetectHardwareInBackground();
            LoadAutoStartState();
            ThreadPool.QueueUserWorkItem(delegate(object state)
            {
                try { if (!SkipAdoption) { _manager.AdoptRunning(); } } catch { }
            });
        }

        /// <summary>Applies the title bar colour once the native window handle exists.</summary>
        protected override void OnSourceInitialized(EventArgs e)
        {
            base.OnSourceInitialized(e);
            Theme.ApplyTitleBar(this);
        }

        /// <summary>Ctrl+N starts a new launch when the launch button is available.</summary>
        protected override void OnPreviewKeyDown(KeyEventArgs e)
        {
            if (e.Key == Key.N && Keyboard.Modifiers == ModifierKeys.Control)
            {
                if (_launchButton != null && _launchButton.IsEnabled)
                {
                    OnLaunchClick(this, new RoutedEventArgs());
                }
                e.Handled = true;
                return;
            }
            base.OnPreviewKeyDown(e);
        }

        // ------------------------------------------------------------------
        // Layout
        // ------------------------------------------------------------------

        /// <summary>Title and subtitle on the left, dark mode switch on the right.</summary>
        private UIElement BuildHeader()
        {
            Grid header = new Grid();
            header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            StackPanel titles = new StackPanel();
            StackPanel brand = new StackPanel();
            brand.Orientation = Orientation.Horizontal;
            CookieJarLogo logo = new CookieJarLogo(40);
            logo.Margin = new Thickness(0, 0, 10, 0);
            logo.VerticalAlignment = VerticalAlignment.Center;
            brand.Children.Add(logo);
            TextBlock wordmark = UiUtil.Label("Cookie Jar", Theme.TextDisplay, true, Theme.Text);
            wordmark.VerticalAlignment = VerticalAlignment.Center;
            brand.Children.Add(wordmark);
            titles.Children.Add(brand);

            TextBlock subtitle = UiUtil.Label(
                "Run several Roblox clients side by side. Launch a whole profile of accounts with one click, or start one from the website; the cache is cleared before each launch.",
                13, false, Theme.TextMuted);
            subtitle.TextWrapping = TextWrapping.Wrap;
            subtitle.Margin = new Thickness(0, 4, 0, 0);
            titles.Children.Add(subtitle);
            Grid.SetColumn(titles, 0);
            header.Children.Add(titles);

            StackPanel themeBox = new StackPanel();
            themeBox.Orientation = Orientation.Horizontal;
            themeBox.VerticalAlignment = VerticalAlignment.Center;

            _themeIcon = new TextBlock();
            _themeIcon.FontFamily = new FontFamily("Segoe MDL2 Assets");
            _themeIcon.FontSize = 16;
            _themeIcon.Foreground = Theme.Text;
            _themeIcon.VerticalAlignment = VerticalAlignment.Center;
            _themeIcon.Margin = new Thickness(0, 0, 10, 0);

            _themeSwitch = new ToggleSwitch();
            _themeSwitch.VerticalAlignment = VerticalAlignment.Center;
            _themeSwitch.IsChecked = Theme.IsDark;
            _themeSwitch.ToolTip = "Switch between light and dark mode";
            AutomationProperties.SetName(_themeSwitch, "Dark mode");

            TextBlock label = UiUtil.Label("Dark mode", 13, false, Theme.Text);
            label.VerticalAlignment = VerticalAlignment.Center;
            label.Margin = new Thickness(10, 0, 0, 0);

            themeBox.Children.Add(_themeIcon);
            themeBox.Children.Add(_themeSwitch);
            themeBox.Children.Add(label);

            _exitButton = PillButton.Create("E_xit", PillStyle.Danger);
            _exitButton.MinWidth = 64;
            _exitButton.Padding = new Thickness(16, 8, 16, 8);
            _exitButton.MinHeight = 36;
            _exitButton.Margin = new Thickness(18, 0, 0, 0);
            _exitButton.VerticalAlignment = VerticalAlignment.Center;
            _exitButton.ToolTip = "Quit the launcher. Closing this window only hides it in the tray; this button really exits (asks first if instances are running).";
            AutomationProperties.SetName(_exitButton, "Exit launcher");
            _exitButton.Click += delegate { RequestExit(); };
            themeBox.Children.Add(_exitButton);
            Grid.SetColumn(themeBox, 1);
            header.Children.Add(themeBox);

            // Attach after the initial state is set so the first sync does not re-apply the theme.
            _themeSwitch.Checked += OnThemeSwitchChanged;
            _themeSwitch.Unchecked += OnThemeSwitchChanged;
            UpdateThemeIcon();
            return header;
        }

        /// <summary>Hardware summary, live usage and the capacity bar.</summary>
        /// <summary>
        /// Stat strip: Instances (running / recommended), CPU, Memory and Graphics tiles, each a caption, one big value,
        /// a meter where it applies and a short detail. Four columns, two by two on narrow windows.
        /// </summary>
        private UIElement BuildHardwareCard()
        {
            _capacityText = Track(UiUtil.Label("0 / 0", Theme.TextDisplay, true, Theme.Text), false);
            _recommendText = Track(UiUtil.Label(" ", Theme.TextMeta, false, Theme.TextMuted), true);
            _capacityBar = new MeterBar();
            _capacityBar.ToolTip = "How many instances are running compared with the recommended number for this PC.";
            AutomationProperties.SetName(_capacityBar, "Running instances against recommended limit");

            _usageText = Track(UiUtil.Label("0%", Theme.TextDisplay, true, Theme.Text), false);
            _cpuText = Track(UiUtil.Label("Detecting hardware...", Theme.TextMeta, false, Theme.TextMuted), true);
            _coresText = Track(UiUtil.Label(" ", Theme.TextMeta, false, Theme.TextMuted), true);
            _cpuBar = new MeterBar();

            _ramText = Track(UiUtil.Label("0%", Theme.TextDisplay, true, Theme.Text), false);
            _ramSubText = Track(UiUtil.Label(" ", Theme.TextMeta, false, Theme.TextMuted), true);
            _ramBar = new MeterBar();

            _gpuText = Track(UiUtil.Label(" ", Theme.TextSection, true, Theme.Text), false);
            _gpuText.Margin = new Thickness(0, 4, 0, 0);
            _vramText = Track(UiUtil.Label(" ", Theme.TextMeta, false, Theme.TextMuted), true);

            _statTiles.Clear();
            _statTiles.Add(StatTile("INSTANCES", _capacityText, _capacityBar, _recommendText, null));
            _statTiles.Add(StatTile("CPU", _usageText, _cpuBar, _cpuText, _coresText));
            _statTiles.Add(StatTile("MEMORY", _ramText, _ramBar, _ramSubText, null));
            _statTiles.Add(StatTile("GRAPHICS", _gpuText, null, _vramText, null));

            _statGrid = new Grid();
            LayoutStatTiles(4);
            _statGrid.SizeChanged += delegate(object sender, SizeChangedEventArgs e)
            {
                int cols = e.NewSize.Width < 720 ? 2 : 4;
                if (cols != _statCols) LayoutStatTiles(cols);
            };

            Card card = new Card();
            card.Padding = Theme.CardPadding;
            card.Child = _statGrid;
            return card;
        }

        private FrameworkElement StatTile(string caption, TextBlock value, MeterBar bar, TextBlock sub1, TextBlock sub2)
        {
            StackPanel tile = new StackPanel();
            TextBlock cap = Track(UiUtil.Label(caption, Theme.TextCaption, true, Theme.TextMuted), true);
            tile.Children.Add(cap);
            value.Margin = new Thickness(0, value.Margin.Top + 2, 0, 0);
            tile.Children.Add(value);
            if (bar != null)
            {
                bar.Margin = new Thickness(0, 6, 0, 0);
                tile.Children.Add(bar);
            }
            sub1.Margin = new Thickness(0, 6, 0, 0);
            tile.Children.Add(sub1);
            if (sub2 != null)
            {
                sub2.Margin = new Thickness(0, 2, 0, 0);
                tile.Children.Add(sub2);
            }
            return tile;
        }

        /// <summary>Places the tiles in cols columns (4 or 2) with thin dividers between neighbours in a row.</summary>
        private void LayoutStatTiles(int cols)
        {
            _statCols = cols;
            _statGrid.Children.Clear();
            _statGrid.ColumnDefinitions.Clear();
            _statGrid.RowDefinitions.Clear();
            _statDividers.Clear();
            for (int c = 0; c < cols; c++)
            {
                if (c > 0) _statGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(33) });
                _statGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            }
            int rows = (_statTiles.Count + cols - 1) / cols;
            for (int r = 0; r < rows; r++)
            {
                _statGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            }
            for (int i = 0; i < _statTiles.Count; i++)
            {
                int r = i / cols, c = i % cols;
                FrameworkElement t = _statTiles[i];
                t.Margin = new Thickness(0, r > 0 ? 14 : 0, 0, 0);
                Grid.SetRow(t, r);
                Grid.SetColumn(t, c * 2);
                _statGrid.Children.Add(t);
                if (c > 0)
                {
                    Border div = new Border();
                    div.Width = 1;
                    div.Background = Theme.Border;
                    div.HorizontalAlignment = HorizontalAlignment.Center;
                    div.Margin = new Thickness(0, r > 0 ? 14 : 0, 0, 0);
                    Grid.SetRow(div, r);
                    Grid.SetColumn(div, c * 2 - 1);
                    _statGrid.Children.Add(div);
                    _statDividers.Add(div);
                }
            }
        }

        /// <summary>Restriction banner. Its message is refreshed on each tick.</summary>
        private UIElement BuildBanner()
        {
            _banner = new InfoBanner();
            _banner.Show("Info", "Detecting hardware...");
            return _banner;
        }

        private static TextBlock CenteredMargin(TextBlock text)
        {
            text.Margin = new Thickness(0, 0, 8, 0);
            return text;
        }

        /// <summary>
        /// Scrollable instance list with an empty-state message on top, and the Advanced and Settings
        /// panels below it.
        /// </summary>
        private UIElement BuildInstanceArea()
        {
            _instanceList = new StackPanel();
            _instanceList.Margin = new Thickness(0, 0, 4, 0);

            ScrollViewer scroll = new ScrollViewer();
            scroll.VerticalScrollBarVisibility = ScrollBarVisibility.Auto;
            scroll.HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled;
            scroll.Content = _instanceList;

            _emptyState = BuildEmptyState();

            Grid listGrid = new Grid();
            listGrid.Children.Add(scroll);
            listGrid.Children.Add(_emptyState);

            StackPanel panels = new StackPanel();
            panels.Margin = new Thickness(0, 0, 4, 0);
            panels.Children.Add(BuildAdvancedExpander());
            panels.Children.Add(BuildSettingsExpander());

            ScrollViewer panelScroll = new ScrollViewer();
            panelScroll.VerticalScrollBarVisibility = ScrollBarVisibility.Auto;
            panelScroll.HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled;
            panelScroll.MaxHeight = 360;
            panelScroll.Content = panels;

            Grid area = new Grid();
            area.RowDefinitions.Add(Row(new GridLength(1, GridUnitType.Star)));
            area.RowDefinitions.Add(Row(GridLength.Auto));
            Grid.SetRow(listGrid, 0);
            Grid.SetRow(panelScroll, 1);
            area.Children.Add(listGrid);
            area.Children.Add(panelScroll);
            return area;
        }

        /// <summary>Friendly centred message shown when no instances exist.</summary>
        private UIElement BuildEmptyState()
        {
            StackPanel stack = new StackPanel();
            stack.HorizontalAlignment = HorizontalAlignment.Center;
            stack.VerticalAlignment = VerticalAlignment.Center;

            TextBlock title = Track(UiUtil.Label("Ready for another batch", 18, true, Theme.Text), false);
            title.TextAlignment = TextAlignment.Center;

            TextBlock hint = Track(UiUtil.Label(
                "Press Launch profile to open a group of accounts, or Launch new instance (Ctrl+N) to open the Adopt Me page in your browser and log in there yourself. "
                + "New Roblox windows appear here automatically. Clients that are already running can be added with Adopt running clients. "
                + "Your Roblox login is never wiped unless you tick Force fresh login.",
                13, false, Theme.TextMuted), true);
            hint.TextAlignment = TextAlignment.Center;
            hint.TextWrapping = TextWrapping.Wrap;
            hint.MaxWidth = 640;
            hint.Margin = new Thickness(0, 8, 0, 0);

            stack.Children.Add(title);
            stack.Children.Add(hint);
            return stack;
        }

        /// <summary>Collapsible activity log, read-only, auto-scrolled and capped.</summary>
        private UIElement BuildLogPanel()
        {
            _logBox = new TextBox();
            _logBox.IsReadOnly = true;
            _logBox.AcceptsReturn = true;
            _logBox.TextWrapping = TextWrapping.NoWrap;
            _logBox.VerticalScrollBarVisibility = ScrollBarVisibility.Auto;
            _logBox.HorizontalScrollBarVisibility = ScrollBarVisibility.Auto;
            _logBox.FontFamily = Theme.MonoFont;
            _logBox.FontSize = 12;
            _logBox.Height = 140;
            _logBox.Padding = new Thickness(12);
            _logBox.Background = Theme.SurfaceAlt;
            _logBox.Foreground = Theme.Text;
            _logBox.BorderThickness = new Thickness(0);
            AutomationProperties.SetName(_logBox, "Activity log");

            Button clear = PillButton.Create("_Clear", PillStyle.Secondary);
            clear.Click += delegate { ClearLog(); };
            clear.HorizontalAlignment = HorizontalAlignment.Right;
            clear.Margin = new Thickness(0, 0, 0, 8);
            clear.ToolTip = "Clear the activity log. Nothing is undone.";

            StackPanel content = new StackPanel();
            content.Children.Add(clear);
            content.Children.Add(_logBox);

            Expander expander = new Expander();
            expander.Header = SectionHeader("Activity log");
            expander.IsExpanded = true;
            expander.Foreground = Theme.Text;
            expander.Content = content;
            return expander;
        }

        // ------------------------------------------------------------------
        // Advanced panel: auto-click master switch, interval, RAM summary, per-instance RAM and layout
        // ------------------------------------------------------------------

        private UIElement BuildAdvancedExpander()
        {
            StackPanel body = new StackPanel();

            _autoMaster = MakeCheck("Enable auto-click for all instances");
            _autoMaster.IsChecked = false;
            _autoMaster.Checked += OnMasterChanged;
            _autoMaster.Unchecked += OnMasterChanged;
            _autoMaster.ToolTip = "Off by default. When off, every card's CASH OUT switch is greyed out and nothing is clicked.";
            body.Children.Add(_autoMaster);

            StackPanel intervalRow = new StackPanel();
            intervalRow.Orientation = Orientation.Horizontal;
            intervalRow.Margin = new Thickness(0, 8, 0, 0);
            TextBlock intervalLabel = Track(UiUtil.Label("Check every (seconds, " + MinIntervalSeconds + "-" + MaxIntervalSeconds + ")", 13, false, Theme.Text), false);
            intervalLabel.VerticalAlignment = VerticalAlignment.Center;
            _intervalBox = MakeField(DefaultIntervalSeconds.ToString(CultureInfo.InvariantCulture), 80, "Auto-click interval in seconds");
            _intervalBox.LostFocus += delegate { CommitInterval(); };
            _intervalBox.KeyDown += delegate(object s, KeyEventArgs ke) { if (ke.Key == Key.Enter) { CommitInterval(); } };
            _intervalBox.Margin = new Thickness(10, 0, 0, 0);
            intervalRow.Children.Add(intervalLabel);
            intervalRow.Children.Add(_intervalBox);
            body.Children.Add(intervalRow);

            _nudgeCheck = MakeCheck("Nudge the cursor every " + NudgeMinutes + " minutes too (off by default)");
            _nudgeCheck.IsChecked = false;
            _nudgeCheck.Checked += delegate { _nudge = true; ReapplyAllAuto(); };
            _nudgeCheck.Unchecked += delegate { _nudge = false; ReapplyAllAuto(); };
            body.Children.Add(_nudgeCheck);

            body.Children.Add(BuildDeclineSettings());
            body.Children.Add(BuildInputSettings());

            body.Children.Add(Spaced(Track(UiUtil.Label("Memory allocation", 14, true, Theme.Text), false), 14));
            _ramSummary = Track(UiUtil.Label(" ", 13, false, Theme.TextMuted), true);
            _ramSummary.TextWrapping = TextWrapping.Wrap;
            _ramSummary.Margin = new Thickness(0, 4, 0, 0);
            body.Children.Add(_ramSummary);
            _ramWarnings = new StackPanel();
            body.Children.Add(_ramWarnings);

            body.Children.Add(Spaced(Track(UiUtil.Label("Layout", 14, true, Theme.Text), false), 14));
            _monitorsText = Track(UiUtil.Label(" ", 13, false, Theme.TextMuted), true);
            _monitorsText.TextWrapping = TextWrapping.Wrap;
            _monitorsText.Margin = new Thickness(0, 4, 0, 0);
            body.Children.Add(_monitorsText);

            Button tile = PillButton.Create("_Tile now", PillStyle.Secondary);
            tile.HorizontalAlignment = HorizontalAlignment.Left;
            tile.Margin = new Thickness(0, 8, 0, 0);
            tile.ToolTip = "Re-arrange every running instance into its own cell on the monitors.";
            AutomationProperties.SetName(tile, "Tile now");
            tile.Click += delegate { TileAll(); };
            body.Children.Add(tile);

            _ramRows = new StackPanel();
            _ramRows.Margin = new Thickness(0, 8, 0, 0);
            body.Children.Add(_ramRows);

            _advancedExpander = new Expander();
            _advancedExpander.Header = SectionHeader("Advanced");
            _advancedExpander.IsExpanded = false;
            _advancedExpander.Foreground = Theme.Text;
            _advancedExpander.Margin = new Thickness(0, 0, 0, 8);
            _advancedExpander.Content = body;
            return _advancedExpander;
        }

        // ------------------------------------------------------------------
        // Settings panel: mini bar, start with Windows, keep-alive
        // ------------------------------------------------------------------

        private UIElement BuildSettingsExpander()
        {
            StackPanel body = new StackPanel();

            _miniBarCheck = MakeCheck("Show mini bar");
            _miniBarCheck.Checked += OnMiniBarToggled;
            _miniBarCheck.Unchecked += OnMiniBarToggled;
            body.Children.Add(_miniBarCheck);

            _autoStartCheck = MakeCheck("Start with Windows (mini bar)");
            _autoStartCheck.Checked += OnAutoStartToggled;
            _autoStartCheck.Unchecked += OnAutoStartToggled;
            body.Children.Add(_autoStartCheck);

            _efficiencyCheck = MakeCheck("Save CPU on background clients");
            _efficiencyCheck.IsChecked = _bgEfficiency;
            _efficiencyCheck.ToolTip = "Clients that are not the window in front run at lower CPU priority with Windows efficiency mode, "
                + "so the game you are playing (or the rest of the PC) gets the CPU first. The client in front, and any client "
                + "still starting up (first minute), always run at full speed. Everything returns to normal when you turn this off or exit.";
            _efficiencyCheck.Checked += OnEfficiencyToggled;
            _efficiencyCheck.Unchecked += OnEfficiencyToggled;
            body.Children.Add(_efficiencyCheck);

            _autoRamCheck = MakeCheck("Auto RAM allocation across sessions");
            _autoRamCheck.IsChecked = _autoRam;
            _autoRamCheck.ToolTip = "Shares RAM equally across active and starting sessions, leaving 4 GB for Windows and apps. "
                + "Up to 3 GB per session. Overrides manual limits while on; switching off restores them. These are working-set caps, not reserved RAM.";
            _autoRamCheck.Checked += OnAutoRamToggled;
            _autoRamCheck.Unchecked += OnAutoRamToggled;
            body.Children.Add(_autoRamCheck);
            _autoRamStatus = Track(UiUtil.Label("Waiting for hardware detection", 12, false, Theme.TextMuted), true);
            _autoRamStatus.TextWrapping = TextWrapping.Wrap;
            body.Children.Add(_autoRamStatus);

            _overrideLimitCheck = MakeCheck("Override hardware instance limit (this app session only)");
            _overrideLimitCheck.ToolTip = "Allows additional sessions beyond the hardware limit after an explicit warning for each launch. Low-memory protection remains active. Resets when Cookie Jar exits.";
            _overrideLimitCheck.Checked += OnOverrideLimitToggled;
            _overrideLimitCheck.Unchecked += OnOverrideLimitToggled;
            body.Children.Add(_overrideLimitCheck);

            body.Children.Add(Track(UiUtil.Label("Closing or minimising this window hides it in the tray. Use Exit (top right) or the tray menu to quit.", 12, false, Theme.TextMuted), true));

            _settingsExpander = new Expander();
            _settingsExpander.Header = SectionHeader("Settings");
            _settingsExpander.IsExpanded = false;
            _settingsExpander.Foreground = Theme.Text;
            _settingsExpander.Margin = new Thickness(0, 0, 0, 8);
            _settingsExpander.Content = body;
            return _settingsExpander;
        }

        // ------------------------------------------------------------------
        // Hardware detection and periodic refresh
        // ------------------------------------------------------------------

        /// <summary>WMI detection runs on a thread-pool thread; the result is marshalled back to the UI.</summary>
        private void DetectHardwareInBackground()
        {
            ThreadPool.QueueUserWorkItem(delegate(object state)
            {
                HardwareProfile profile = null;
                try
                {
                    profile = HardwareProfile.Detect();
                }
                catch
                {
                    profile = null;
                }

                Dispatcher.BeginInvoke(new Action(delegate { OnHardwareDetected(profile); }));
            });
        }

        private void OnHardwareDetected(HardwareProfile profile)
        {
            _profile = profile;
            MemoryGovernor.Initialize(profile);
            UpdateAutoRam();
            // Restrictor accepts a null profile and falls back to conservative limits.
            _restrictor = new Restrictor(profile);

            if (profile != null)
            {
                _cpuText.Text = Text(profile.CpuName, "Unknown CPU");
                _cpuText.ToolTip = _cpuText.Text;
                _gpuText.Text = Text(profile.GpuName, "Unknown GPU");
                _gpuText.ToolTip = _gpuText.Text;
                _vramText.Text = Fmt(profile.VramGb, "0.0") + " GB VRAM";
                _coresText.Text = profile.CpuCores.ToString(CultureInfo.InvariantCulture) + " cores  ·  "
                    + profile.LogicalProcessors.ToString(CultureInfo.InvariantCulture) + " threads";
            }
            else
            {
                _cpuText.Text = "Hardware detection failed";
                _gpuText.Text = "Unknown";
                _vramText.Text = "Using conservative limits";
                _coresText.Text = " ";
            }

            RefreshLiveInfo();
            RefreshButtons();
        }

        private void OnTimerTick(object sender, EventArgs e)
        {
            try
            {
                _manager.Tick();
            }
            catch
            {
                // Refresh errors must never stop the timer.
            }

            UpdateEfficiency();
            UpdateAutoRam();
            SampleMonitor();
            if (_tray != null) { _tray.SetRunning(_manager.RunningCount); }
            RefreshButtons();   // also keeps ActiveProfile current for the mini bar and instance mapping
            TickInput();
            // Text, cards and Advanced rows are only drawn while the window is shown (OnVisibleChanged catches up).
            if (IsVisible) { RefreshVisuals(); }
        }

        private void RefreshVisuals()
        {
            RefreshLiveInfo();
            RefreshCardStats();
            RefreshAdvancedIfChanged();
            UpdateRamSummary();
        }

        private void OnVisibleChanged(object sender, DependencyPropertyChangedEventArgs e)
        {
            if (!IsVisible) return;
            try { RefreshVisuals(); } catch (Exception) { }
        }

        /// <summary>One foreground check per tick; Windows is only called when a client's wanted state changes.</summary>
        private void UpdateEfficiency()
        {
            if (!Efficiency.Enabled) return;
            try
            {
                _effTargets.Clear();
                foreach (RobloxInstance inst in _manager.Instances)
                {
                    EfficiencyTarget t = new EfficiencyTarget();
                    t.Pid = inst.Pid;
                    t.Running = inst.StatusKind == "Running";
                    t.StartedUtc = inst.StartedUtc;
                    _effTargets.Add(t);
                }
                Efficiency.Update(_effTargets, DateTime.UtcNow);
            }
            catch (Exception)
            {
                // Efficiency is an extra; it must never stop the refresh.
            }
        }

        private void SampleMonitor()
        {
            try
            {
                _monitor.PollOnce();
            }
            catch
            {
                // Keep the last good values.
            }
        }

        /// <summary>Usage line, RAM line, capacity bar and restriction banner.</summary>
        private void RefreshLiveInfo()
        {
            int running = _manager.RunningCount;
            RestrictResult result = EvaluateRestriction();

            double cpu = _monitor.CpuPercent;
            _usageText.Text = Fmt(cpu, "0") + "%";
            _cpuBar.SetFraction(cpu / 100.0);
            _cpuBar.Fill = cpu >= 90 ? Theme.Danger : cpu >= 75 ? Theme.Warning : Theme.Accent;

            double usedRam = _monitor.UsedRamPercent;
            double totalRam = _profile != null ? _profile.RamGb : 0;
            _ramText.Text = Fmt(usedRam, "0") + "%";
            _ramSubText.Text = Fmt(_monitor.FreeRamGb, "0.0") + " GB free of " + Fmt(totalRam, "0.0") + " GB";
            _ramBar.SetFraction(usedRam / 100.0);
            _ramBar.Fill = usedRam >= 90 ? Theme.Danger : usedRam >= 80 ? Theme.Warning : Theme.Accent;

            int recommended = RecommendedCount();
            _recommendText.Text = "Recommended " + recommended.ToString(CultureInfo.InvariantCulture)
                + "  ·  hardware limit " + HardMaxCount().ToString(CultureInfo.InvariantCulture)
                + (_overrideInstanceLimit ? " (override ON)" : "");
            _capacityBar.SetFraction((double)running / Math.Max(1, recommended));
            _capacityText.Text = running.ToString(CultureInfo.InvariantCulture)
                + " / " + recommended.ToString(CultureInfo.InvariantCulture);
            _capacityBar.Fill = BrushFor(result);

            UpdateInstanceHeading();

            if (!IsWorking())
            {
                string waiting = _manager.WaitingText;
                _workText.Text = waiting ?? "";
                _workText.Foreground = waiting != null ? Theme.Accent : Theme.TextMuted;
            }

            if (DateTime.UtcNow >= _bannerHoldUntilUtc)
            {
                ShowRestriction(result);
            }
        }

        // ------------------------------------------------------------------
        // Restriction
        // ------------------------------------------------------------------

        /// <summary>Adapter over Restrictor.Evaluate; the one place to change if its signature moves.</summary>
        private RestrictResult EvaluateRestriction()
        {
            if (_restrictor == null)
            {
                return null;
            }
            return _restrictor.Evaluate(_manager.RunningCount, _monitor.FreeRamGb, _monitor.CpuPercent, _overrideInstanceLimit);
        }

        private int RecommendedCount()
        {
            if (_restrictor != null)
            {
                return _restrictor.Recommended;
            }
            return 0;
        }

        private int HardMaxCount()
        {
            if (_restrictor != null)
            {
                return _restrictor.MaxAllowed;
            }
            return 0;
        }

        /// <summary>
        /// Plain-language restriction message. Warn can be overridden by the user; Block cannot.
        /// </summary>
        private void ShowRestriction(RestrictResult result)
        {
            if (result == null)
            {
                _banner.Show("Info", "Detecting hardware...");
                return;
            }

            switch (result.Level)
            {
                case RestrictLevel.Allow:
                    _banner.Show("None", "Everything looks good. " + result.Message);
                    break;
                case RestrictLevel.Warn:
                    _banner.Show("Warning", result.Message + " You can still launch if you choose to.");
                    break;
                default:
                    _banner.Show("Blocked", result.Message + " Launching is paused until you close an instance.");
                    break;
            }
        }

        /// <summary>Shows an error on the banner for a few seconds, then the live restriction message returns.</summary>
        private void ShowTemporaryError(string message)
        {
            _banner.Show("Critical", message);
            _bannerHoldUntilUtc = DateTime.UtcNow.AddSeconds(ErrorBannerHoldSeconds);
        }

        // ------------------------------------------------------------------
        // Launch and placement
        // ------------------------------------------------------------------

        private void OnAdoptClick(object sender, RoutedEventArgs e)
        {
            _adoptButton.IsEnabled = false;
            ThreadPool.QueueUserWorkItem(delegate(object state)
            {
                int n = 0;
                try { n = _manager.AdoptRunning(); }
                catch (Exception ex) { AppendLogFromAnyThread("Adopt failed: " + ex.Message); }
                if (n == 0) { AppendLogFromAnyThread("No untracked Roblox clients are running."); }
                Dispatcher.BeginInvoke(new Action(delegate { RefreshButtons(); }));
            });
        }

        private void OnLaunchFinished(bool ok, string error)
        {
            _launchButton.Content = LaunchLabel;
            EndWork();

            if (!ok)
            {
                string message = string.IsNullOrEmpty(error) ? "Launch failed." : error;
                AppendLog("Error: " + message);
                ShowTemporaryError(message);
            }

            RefreshLiveInfo();
        }

        /// <summary>
        /// Called by the launch code after an instance starts. Applies its RAM setting and places its window.
        /// The window lookup waits on a worker thread (20 s); the move happens on the UI thread.
        /// </summary>
        internal void ApplyRamAndPlacement(RobloxInstance inst)
        {
            if (inst == null)
            {
                return;
            }

            try
            {
                RamSetting setting = GetRam(inst);
                if (setting != null)
                {
                    ApplyRamToInstance(inst, setting);
                }
            }
            catch (Exception ex)
            {
                AppendLog("RAM setting failed for " + inst.Name + ": " + ex.Message);
            }

            _placedIds.Add(inst.Id);
            bool autoTile = ActiveProfile == null || ActiveProfile.AutoTile;
            List<RobloxInstance> targets = autoTile ? RunningInstances() : new List<RobloxInstance>();
            if (!targets.Contains(inst))
            {
                targets.Add(inst);
            }
            PlaceInstances(targets, 20000);
        }

        /// <summary>Re-tiles every running instance now, using the same rules as launch placement.</summary>
        internal void TileAll()
        {
            PlaceInstances(RunningInstances(), 3000);
        }

        /// <summary>Brings the windows of the given instances to the front in order. The last one ends on top.</summary>
        internal void BringAllToFront(IList<RobloxInstance> ordered)
        {
            if (ordered == null || ordered.Count == 0)
            {
                return;
            }

            List<RobloxInstance> copy = new List<RobloxInstance>(ordered);
            ThreadPool.QueueUserWorkItem(delegate(object state)
            {
                List<IntPtr> handles = new List<IntPtr>();
                foreach (RobloxInstance inst in copy)
                {
                    IntPtr hwnd = FindWindowFor(inst, 0);
                    if (hwnd != IntPtr.Zero)
                    {
                        handles.Add(hwnd);
                    }
                }
                try
                {
                    WindowManager.BringAllToFront(handles);
                }
                catch (Exception ex)
                {
                    AppendLogFromAnyThread("Bring to front failed: " + ex.Message);
                }
            });
        }

        /// <summary>Brings one instance's window to the front. Lookup runs off the UI thread.</summary>
        public void SwitchTo(RobloxInstance inst)
        {
            if (inst == null)
            {
                return;
            }

            ThreadPool.QueueUserWorkItem(delegate(object state)
            {
                bool ok = false;
                try
                {
                    IntPtr hwnd = FindWindowFor(inst, 0);
                    if (hwnd != IntPtr.Zero)
                    {
                        ok = WindowManager.BringToFront(hwnd);
                    }
                    else
                    {
                        AppendLogFromAnyThread("No window found for " + inst.Name + " yet.");
                        return;
                    }
                }
                catch (Exception ex)
                {
                    AppendLogFromAnyThread("Switch to failed: " + ex.Message);
                    return;
                }
                if (!ok)
                {
                    AppendLogFromAnyThread("Windows did not allow " + inst.Name + " to come forward. Click its taskbar icon.");
                }
            });
        }

        /// <summary>Finds the instance window: main-window helper first, then the plain PID search.</summary>
        private static IntPtr FindWindowFor(RobloxInstance inst, int timeoutMs)
        {
            IntPtr hwnd = AntiIdleClicker.FindMainWindow(inst.Pid);
            if (hwnd == IntPtr.Zero)
            {
                hwnd = WindowManager.FindWindowForPid(inst.Pid, timeoutMs);
            }
            return hwnd;
        }

        private List<RobloxInstance> RunningInstances()
        {
            List<RobloxInstance> list = new List<RobloxInstance>();
            foreach (RobloxInstance inst in _manager.Instances)
            {
                if (inst.Pid > 0 && inst.StatusKind != "Stopped")
                {
                    list.Add(inst);
                }
            }
            return list;
        }

        /// <summary>Key used to give each instance its own tile: the account id when known, otherwise the instance id.</summary>
        private string TileKeyFor(RobloxInstance inst)
        {
            string accountId;
            if (InstAccountId.TryGetValue(inst.Id, out accountId) && !string.IsNullOrEmpty(accountId))
            {
                return accountId;
            }
            return "#" + inst.Id.ToString(CultureInfo.InvariantCulture);
        }

        private sealed class PlaceJob
        {
            public int Pid;
            public int InstId;
            public IntPtr Hwnd;
            public TileCell Cell;
            public WindowLayout Layout;
        }

        /// <summary>
        /// Works out one placement per instance on the UI thread (each instance gets a distinct tile index),
        /// finds the windows on a worker thread and moves them back on the UI thread.
        /// </summary>
        private void PlaceInstances(List<RobloxInstance> targets, int timeoutMs)
        {
            if (targets == null || targets.Count == 0)
            {
                return;
            }

            List<MonitorInfo> monitors = WindowManager.GetMonitors();
            bool autoTile = ActiveProfile == null || ActiveProfile.AutoTile;
            List<PlaceJob> jobs = new List<PlaceJob>();

            List<TileCell> cells = null;
            if (autoTile)
            {
                if (ActiveProfile != null)
                {
                    List<string> keys = new List<string>();
                    HashSet<string> seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                    foreach (RobloxInstance inst in targets)
                    {
                        string key = TileKeyFor(inst);
                        // Same account running twice: only the first uses the account's saved layout.
                        if (!seen.Add(key)) { key = key + "#" + inst.Id.ToString(CultureInfo.InvariantCulture); }
                        keys.Add(key);
                    }
                    cells = ActiveProfile.ApplyTiling(keys, monitors);
                }
                else
                {
                    cells = WindowManager.ComputeTilingAcrossMonitors(targets.Count, monitors);
                }
            }

            for (int i = 0; i < targets.Count; i++)
            {
                RobloxInstance inst = targets[i];
                PlaceJob job = new PlaceJob();
                job.Pid = inst.Pid;
                job.InstId = inst.Id;

                if (autoTile)
                {
                    if (cells == null || i >= cells.Count || cells[i] == null)
                    {
                        continue;
                    }
                    job.Cell = cells[i];
                }
                else
                {
                    string accountId;
                    AccountLayout layout = null;
                    if (ActiveProfile != null && InstAccountId.TryGetValue(inst.Id, out accountId) && accountId != null)
                    {
                        layout = ActiveProfile.GetLayout(accountId);
                    }
                    if (layout == null)
                    {
                        continue;
                    }
                    job.Layout = ToWindowLayout(layout);
                }
                jobs.Add(job);
            }

            if (jobs.Count == 0)
            {
                return;
            }

            ThreadPool.QueueUserWorkItem(delegate(object state)
            {
                // One shared deadline: poll every window until all exist or time runs out.
                DateTime deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
                while (true)
                {
                    bool missing = false;
                    foreach (PlaceJob job in jobs)
                    {
                        if (job.Hwnd == IntPtr.Zero)
                        {
                            job.Hwnd = WindowManager.FindWindowForPid(job.Pid, 0);
                            if (job.Hwnd == IntPtr.Zero) { missing = true; }
                        }
                    }
                    if (!missing || DateTime.UtcNow >= deadline)
                    {
                        break;
                    }
                    Thread.Sleep(300);
                }
                Dispatcher.BeginInvoke(new Action(delegate
                {
                    PlaceJobs(jobs, true);
                    // Roblox sometimes resizes itself while loading: place again a few seconds later.
                    DispatcherTimer again = new DispatcherTimer();
                    again.Interval = TimeSpan.FromSeconds(4);
                    again.Tick += delegate { again.Stop(); PlaceJobs(jobs, false); };
                    again.Start();
                }));
            });
        }

        private readonly HashSet<int> _placedIds = new HashSet<int>();
        private readonly Dictionary<int, int> _placeTries = new Dictionary<int, int>();

        /// <summary>UI-thread half of placement.</summary>
        private void PlaceJobs(List<PlaceJob> jobs, bool first)
        {
            int placed = 0;
            foreach (PlaceJob job in jobs)
            {
                if (job.Hwnd == IntPtr.Zero)
                {
                    if (first)
                    {
                        // No window yet: let the next sync tick try again (a few times).
                        int tries;
                        _placeTries.TryGetValue(job.InstId, out tries);
                        _placeTries[job.InstId] = tries + 1;
                        if (tries < 4) { _placedIds.Remove(job.InstId); }
                    }
                    continue;
                }
                try
                {
                    bool ok = job.Cell != null
                        ? WindowManager.Place(job.Hwnd, job.Cell)
                        : WindowManager.Place(job.Hwnd, job.Layout);
                    if (ok)
                    {
                        placed++;
                    }
                }
                catch (Exception ex)
                {
                    AppendLog("Placement failed: " + ex.Message);
                }
            }
            if (placed > 0 && first)
            {
                AppendLog("Placed " + placed.ToString(CultureInfo.InvariantCulture) + " window(s).");
            }
        }

        private static WindowLayout ToWindowLayout(AccountLayout layout)
        {
            WindowLayout w = new WindowLayout();
            w.MonitorIndex = layout.MonitorIndex;
            w.X = layout.X;
            w.Y = layout.Y;
            w.Width = layout.Width;
            w.Height = layout.Height;
            return w;
        }

        // ------------------------------------------------------------------
        // RAM settings
        // ------------------------------------------------------------------

        private sealed class RamSetting
        {
            public MemoryPreset Preset;
            public int CustomMb;
        }

        private const string RamNone = "No limit";

        private static MemoryPreset PresetFromLabel(string label)
        {
            switch (label)
            {
                case RamNone: return MemoryPreset.Unlimited;
                case LaunchProfile.RamLow: return MemoryPreset.Low;
                case LaunchProfile.RamHigh: return MemoryPreset.High;
                case LaunchProfile.RamCustom: return MemoryPreset.Custom;
                default: return MemoryPreset.Medium;
            }
        }

        private static string LabelFromPreset(MemoryPreset preset)
        {
            switch (preset)
            {
                case MemoryPreset.Unlimited: return RamNone;
                case MemoryPreset.Low: return LaunchProfile.RamLow;
                case MemoryPreset.High: return LaunchProfile.RamHigh;
                case MemoryPreset.Custom: return LaunchProfile.RamCustom;
                default: return LaunchProfile.RamMedium;
            }
        }

        /// <summary>The RAM setting in force for an instance: made here, else from its account in the active profile, else null.</summary>
        private void OnOverrideLimitToggled(object sender, RoutedEventArgs e)
        {
            if (_settingsSync) return;
            bool enable = _overrideLimitCheck.IsChecked == true;
            if (enable && MessageBox.Show(this,
                "This removes the hardware instance-count block for this app session. Additional clients can cause severe lag, disk paging, crashes or make Windows unresponsive. "
                + "Auto RAM does not create more memory. You will need to confirm each launch beyond the hardware limit. Low-memory protection stays on.\n\nEnable the override?",
                "Override hardware instance limit", MessageBoxButton.YesNo, MessageBoxImage.Warning,
                MessageBoxResult.No) != MessageBoxResult.Yes)
            {
                SetSettingCheck(_overrideLimitCheck, false);
                return;
            }
            _overrideInstanceLimit = enable;
            AppendLog(enable ? "Hardware instance-limit override ON for this app session; every extra launch requires confirmation."
                : "Hardware instance-limit override OFF.");
            RefreshButtons();
            RefreshLiveInfo();
        }

        private bool ConfirmResourceWarning(RestrictResult warning, bool profileRun)
        {
            if (warning.RequiresConfirmation)
            {
                bool go = MessageBox.Show(this, warning.Message, warning.Title, MessageBoxButton.YesNo,
                    MessageBoxImage.Warning, MessageBoxResult.No) == MessageBoxResult.Yes;
                AppendLog(go ? "Hardware limit override explicitly confirmed: " + warning.Message
                    : "Launch stopped: hardware limit override declined.");
                return go;
            }
            return TimedWarnDialog.Confirm(this, warning.Message + (profileRun
                ? "\n\nKeep launching the rest of this profile anyway? Launching continues automatically when the countdown ends."
                : "\n\nLaunch another instance anyway? Launching continues automatically when the countdown ends."),
                profileRun ? "Keep launching" : "Launch anyway", profileRun ? "Stop" : "Cancel", TimedWarnDialog.DefaultSeconds);
        }

        private void OnAutoRamToggled(object sender, RoutedEventArgs e)
        {
            if (_settingsSync) return;
            _autoRam = _autoRamCheck.IsChecked == true;
            UpdateAutoRam();
            SaveMiniBarSettings();
            RefreshCardStats();
            _advancedSignature = "";
            RefreshAdvancedIfChanged();
            UpdateRamSummary();
            RefreshAccountList();
            AppendLog(_autoRam ? "Auto RAM allocation enabled." : "Auto RAM allocation off; manual session limits restored.");
        }

        private void UpdateAutoRam()
        {
            List<RobloxInstance> running = RunningInstances();
            int count = _manager.RunningCount; // includes sessions still starting, even before their PID exists
            MemoryGovernor.SetInstanceCountHint(count);
            int mb = _profile == null ? 0 : AutoRamPolicy.PerSessionMb(_profile.RamGb, count);
            HashSet<int> live = new HashSet<int>();
            foreach (RobloxInstance inst in running) live.Add(inst.Pid);
            foreach (int pid in new List<int>(_autoRamCaps.Keys))
            {
                if (!live.Contains(pid))
                {
                    MemoryGovernor.Clear(pid);
                    _autoRamCaps.Remove(pid);
                }
            }
            int failures = 0;
            foreach (RobloxInstance inst in running)
            {
                if (_autoRam && mb > 0)
                {
                    int previous;
                    if (_autoRamCaps.TryGetValue(inst.Pid, out previous) && previous == mb) continue;
                    bool ok = MemoryGovernor.Apply(inst.Pid, MemoryPreset.Custom, mb);
                    _autoRamCaps[inst.Pid] = ok ? mb : -mb; // failed applications retry on the next tick
                    if (!ok) failures++;
                }
                else if (_autoRamCaps.ContainsKey(inst.Pid))
                {
                    RamSetting manual = GetManualRam(inst);
                    bool ok = MemoryGovernor.Apply(inst.Pid, manual == null ? MemoryPreset.Unlimited : manual.Preset,
                        manual == null ? 0 : manual.CustomMb);
                    if (ok) _autoRamCaps.Remove(inst.Pid);
                    else failures++;
                }
            }
            if (_autoRamStatus != null)
            {
                _autoRamStatus.Text = !_autoRam ? "Manual session RAM limits are in use."
                    : _profile == null ? "Waiting for hardware detection"
                    : count == 0 ? "Auto RAM is ready; no active sessions."
                    : mb == 0 ? "RAM budget too small for automatic allocation; close sessions or use manual limits."
                    : string.Format(CultureInfo.InvariantCulture, "Auto RAM: {0} MB per session across {1} sessions (up to 3 GB each; 4 GB reserved).", mb, count);
                if (failures > 0) _autoRamStatus.Text += " Retrying RAM limits for " + failures + " session(s).";
            }
        }

        private RamSetting GetRam(RobloxInstance inst)
        {
            if (_autoRam && _profile != null && inst.IsActive)
            {
                int mb = AutoRamPolicy.PerSessionMb(_profile.RamGb, _manager.RunningCount);
                if (mb > 0) return new RamSetting { Preset = MemoryPreset.Custom, CustomMb = mb };
            }
            return GetManualRam(inst);
        }

        private RamSetting GetManualRam(RobloxInstance inst)
        {
            RamSetting setting;
            if (_ram.TryGetValue(inst.Id, out setting))
            {
                return setting;
            }

            string accountId;
            if (ActiveProfile != null && InstAccountId.TryGetValue(inst.Id, out accountId) && accountId != null
                && ActiveProfile.HasRamPreset(accountId))
            {
                setting = new RamSetting();
                setting.Preset = PresetFromLabel(ActiveProfile.GetRamPreset(accountId));
                setting.CustomMb = ActiveProfile.GetCustomRamMb(accountId);
                if (setting.CustomMb <= 0)
                {
                    setting.CustomMb = DefaultCustomRamMb;
                }
                _ram[inst.Id] = setting;
                return setting;
            }
            return null;
        }

        private void ApplyRamToInstance(RobloxInstance inst, RamSetting setting)
        {
            if (_autoRam && _profile != null)
            {
                UpdateAutoRam();
                return;
            }
            if (inst.Pid <= 0)
            {
                return;
            }
            bool ok = MemoryGovernor.Apply(inst.Pid, setting.Preset, setting.CustomMb);
            if (!ok)
            {
                AppendLog("Could not apply the RAM limit to " + inst.Name + ".");
            }
        }

        /// <summary>Sets an instance's RAM preset: applies it now and stores it to the active profile when the account is known.</summary>
        private void SetInstanceRam(RobloxInstance inst, MemoryPreset preset, int customMb)
        {
            if (_autoRam)
            {
                ShowTemporaryError("Switch off Auto RAM allocation in Settings to set manual session limits.");
                return;
            }
            RamSetting setting = new RamSetting();
            setting.Preset = preset;
            setting.CustomMb = customMb;
            if (preset == MemoryPreset.Custom)
            {
                if (setting.CustomMb <= 0)
                {
                    setting.CustomMb = DefaultCustomRamMb;
                }
                setting.CustomMb = Math.Max(MinCustomRamMb, Math.Min(MaxCustomRamMb, setting.CustomMb));
            }
            _ram[inst.Id] = setting;

            ApplyRamToInstance(inst, setting);

            string accountId;
            bool saved = false;
            if (ActiveProfile != null && InstAccountId.TryGetValue(inst.Id, out accountId) && accountId != null)
            {
                if (preset == MemoryPreset.Unlimited)
                {
                    ActiveProfile.ClearRamPreset(accountId);
                }
                else
                {
                    ActiveProfile.SetRamPreset(accountId, LabelFromPreset(preset));
                    if (preset == MemoryPreset.Custom)
                    {
                        ActiveProfile.SetCustomRamMb(accountId, setting.CustomMb);
                    }
                }
                string error;
                try
                {
                    if (!ProfileStore.SaveLayouts(ActiveProfile.Name, ActiveProfile, out error))
                    {
                        AppendLog("Could not save the RAM setting: " + error);
                    }
                    else
                    {
                        saved = true;
                    }
                }
                catch (Exception ex)
                {
                    AppendLog("Could not save the RAM setting: " + ex.Message);
                }
            }

            AppendLog("RAM for " + inst.Name + " set to " + LabelFromPreset(preset)
                + (preset == MemoryPreset.Unlimited ? "" : " (" + MemoryGovernor.ResolveMb(preset, setting.CustomMb) + " MB)")
                + (saved ? ", saved to the profile." : ", for this session only (not linked to a profile account)."));
            UpdateRamSummary();
        }

        /// <summary>Allocated and free RAM line plus MemoryGovernor warnings.</summary>
        private void UpdateRamSummary()
        {
            if (_ramSummary == null)
            {
                return;
            }

            List<MemoryAllocation> items = new List<MemoryAllocation>();
            int allocated = 0;
            foreach (RobloxInstance inst in RunningInstances())
            {
                RamSetting setting = GetRam(inst);
                if (setting == null)
                {
                    continue;
                }
                MemoryAllocation item = new MemoryAllocation();
                item.Pid = inst.Pid;
                item.Preset = setting.Preset;
                item.CustomMb = setting.CustomMb;
                item.Label = inst.Name;
                items.Add(item);
                allocated += MemoryGovernor.ResolveMb(setting.Preset, setting.CustomMb);
            }

            int freeMb = (int)Math.Round(_monitor.FreeRamGb * 1024.0);
            _ramSummary.Text = "Allocated " + allocated.ToString(CultureInfo.InvariantCulture) + " MB of "
                + freeMb.ToString(CultureInfo.InvariantCulture) + " MB free";
            _ramSummary.Foreground = Theme.TextMuted;
            if (_autoRam) _ramSummary.Text = "Auto RAM caps: " + allocated.ToString(CultureInfo.InvariantCulture)
                + " MB total across active sessions; " + freeMb.ToString(CultureInfo.InvariantCulture) + " MB currently free";

            List<string> warnings = MemoryGovernor.ValidateAllocation(items);
            string warnKey = Theme.Warning + "|" + string.Join("|", warnings.ToArray());
            if (warnKey == _ramWarningsKey) return;   // unchanged: keep the existing lines
            _ramWarningsKey = warnKey;
            _ramWarnings.Children.Clear();
            foreach (string warning in warnings)
            {
                TextBlock line = UiUtil.Label(warning, 12, false, Theme.Warning);
                line.TextWrapping = TextWrapping.Wrap;
                line.Margin = new Thickness(0, 4, 0, 0);
                _ramWarnings.Children.Add(line);
            }
        }

        /// <summary>Custom MB, Apply and layout controls for each instance. Rebuilt when the instance list or profile changes.</summary>
        private void RefreshAdvancedIfChanged()
        {
            if (_ramRows == null)
            {
                return;
            }

            StringBuilder sig = new StringBuilder();
            sig.Append(ActiveProfile != null ? ActiveProfile.Name + "|" + ActiveProfile.AutoTile : "none");
            foreach (RobloxInstance inst in _manager.Instances)
            {
                sig.Append('|').Append(inst.Id.ToString(CultureInfo.InvariantCulture)).Append(':').Append(inst.Name);
            }
            string signature = sig.ToString();
            if (signature == _advancedSignature)
            {
                return;
            }
            _advancedSignature = signature;

            _monitors = WindowManager.GetMonitors().ToArray();
            _monitorsText.Text = BuildMonitorText();

            // The old rows' labels were registered for theme updates; drop them first, or the window-lifetime lists keep
            // every rebuilt row (and through its handlers, every removed instance) alive - ~2 MB per instance change.
            _mutedTexts.RemoveAll(IsInRamRows);
            _primaryTexts.RemoveAll(IsInRamRows);
            _ramRows.Children.Clear();
            foreach (RobloxInstance inst in _manager.Instances)
            {
                _ramRows.Children.Add(BuildInstanceRow(inst));
            }
        }

        private string BuildMonitorText()
        {
            if (_monitors.Length == 0)
            {
                return "No monitors detected.";
            }
            StringBuilder text = new StringBuilder();
            foreach (MonitorInfo m in _monitors)
            {
                if (text.Length > 0)
                {
                    text.Append(Environment.NewLine);
                }
                text.Append(MonitorLabel(m));
            }
            return text.ToString();
        }

        private static string MonitorLabel(MonitorInfo m)
        {
            return "Monitor " + (m.Index + 1).ToString(CultureInfo.InvariantCulture) + ": "
                + m.FullWidth.ToString(CultureInfo.InvariantCulture) + "x"
                + m.FullHeight.ToString(CultureInfo.InvariantCulture) + ", scale "
                + Math.Round(m.DpiScale * 100.0).ToString(CultureInfo.InvariantCulture) + "%"
                + (m.IsPrimary ? " (primary)" : "");
        }

        private UIElement BuildInstanceRow(RobloxInstance inst)
        {
            StackPanel row = new StackPanel();
            row.Margin = new Thickness(0, 10, 0, 0);
            string accountId = null;
            if (InstAccountId.ContainsKey(inst.Id))
            {
                accountId = InstAccountId[inst.Id];
            }

            row.Children.Add(Track(UiUtil.Label(inst.Name + "  ·  PID " + inst.Pid.ToString(CultureInfo.InvariantCulture), 13, true, Theme.Text), false));

            // RAM line.
            WrapPanel ramLine = new WrapPanel();
            ramLine.Margin = new Thickness(0, 4, 0, 0);
            RamSetting current = GetRam(inst);
            TextBox customBox = MakeField(
                (current != null && current.CustomMb > 0 ? current.CustomMb : DefaultCustomRamMb).ToString(CultureInfo.InvariantCulture),
                80, "Custom memory limit in MB for " + inst.Name);
            Button applyCustom = PillButton.Create("Apply custom MB", PillStyle.Secondary);
            customBox.IsEnabled = !_autoRam;
            applyCustom.IsEnabled = !_autoRam;
            applyCustom.ToolTip = "Switch off Auto RAM allocation in Settings to use manual session limits.";
            AutomationProperties.SetName(applyCustom, "Apply custom memory limit to " + inst.Name);
            applyCustom.Margin = new Thickness(8, 0, 0, 0);
            applyCustom.Click += delegate
            {
                int mb;
                if (!int.TryParse(customBox.Text.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out mb)
                    || mb < MinCustomRamMb || mb > MaxCustomRamMb)
                {
                    ShowTemporaryError("Enter a custom limit from " + MinCustomRamMb + " to " + MaxCustomRamMb + " MB.");
                    return;
                }
                SetInstanceRam(inst, MemoryPreset.Custom, mb);
            };
            ramLine.Children.Add(Track(UiUtil.Label("Custom MB", 12, false, Theme.TextMuted), true));
            ramLine.Children.Add(customBox);
            ramLine.Children.Add(applyCustom);
            row.Children.Add(ramLine);

            // Layout line.
            WrapPanel layoutLine = new WrapPanel();
            layoutLine.Margin = new Thickness(0, 6, 0, 0);

            ComboBox monitorBox = new ComboBox();
            monitorBox.Width = 290;
            monitorBox.Height = 28;
            monitorBox.VerticalContentAlignment = VerticalAlignment.Center;
            monitorBox.Margin = new Thickness(0, 0, 8, 0);
            AutomationProperties.SetName(monitorBox, "Monitor for " + inst.Name);
            foreach (MonitorInfo m in _monitors)
            {
                monitorBox.Items.Add(MonitorLabel(m));
            }
            ThemedCombo.Apply(monitorBox);

            AccountLayout saved = null;
            if (accountId != null && ActiveProfile != null)
            {
                saved = ActiveProfile.GetLayout(accountId);
            }

            int monitorIndex = saved != null ? saved.MonitorIndex : 0;
            if (monitorIndex < 0 || monitorIndex >= monitorBox.Items.Count)
            {
                monitorIndex = 0;
            }
            if (monitorBox.Items.Count > 0)
            {
                monitorBox.SelectedIndex = monitorIndex;
            }

            TextBox xBox = MakeField(saved != null ? saved.X.ToString(CultureInfo.InvariantCulture) : "", 64, "X position for " + inst.Name);
            TextBox yBox = MakeField(saved != null ? saved.Y.ToString(CultureInfo.InvariantCulture) : "", 64, "Y position for " + inst.Name);
            TextBox wBox = MakeField(saved != null ? saved.Width.ToString(CultureInfo.InvariantCulture) : "", 64, "Width for " + inst.Name);
            TextBox hBox = MakeField(saved != null ? saved.Height.ToString(CultureInfo.InvariantCulture) : "", 64, "Height for " + inst.Name);

            layoutLine.Children.Add(Track(UiUtil.Label("Monitor", 12, false, Theme.TextMuted), true));
            layoutLine.Children.Add(monitorBox);
            layoutLine.Children.Add(Track(UiUtil.Label("X", 12, false, Theme.TextMuted), true));
            layoutLine.Children.Add(xBox);
            layoutLine.Children.Add(Track(UiUtil.Label("Y", 12, false, Theme.TextMuted), true));
            layoutLine.Children.Add(yBox);
            layoutLine.Children.Add(Track(UiUtil.Label("W", 12, false, Theme.TextMuted), true));
            layoutLine.Children.Add(wBox);
            layoutLine.Children.Add(Track(UiUtil.Label("H", 12, false, Theme.TextMuted), true));
            layoutLine.Children.Add(hBox);
            row.Children.Add(layoutLine);

            WrapPanel buttons = new WrapPanel();
            buttons.Margin = new Thickness(0, 6, 0, 0);

            Button apply = PillButton.Create("Apply", PillStyle.Secondary);
            AutomationProperties.SetName(apply, "Apply layout to " + inst.Name);
            apply.Margin = new Thickness(0, 0, 8, 0);
            apply.Click += delegate { ApplyLayoutFromRow(inst, accountId, monitorBox, xBox, yBox, wBox, hBox, false); };

            Button save = PillButton.Create("Save layout to profile", PillStyle.Secondary);
            AutomationProperties.SetName(save, "Save layout to profile for " + inst.Name);
            save.Margin = new Thickness(0, 0, 8, 0);
            save.Click += delegate { ApplyLayoutFromRow(inst, accountId, monitorBox, xBox, yBox, wBox, hBox, true); };

            Button reset = PillButton.Create("Reset", PillStyle.Secondary);
            AutomationProperties.SetName(reset, "Reset layout for " + inst.Name);
            reset.Click += delegate { ResetLayoutForRow(accountId); };

            buttons.Children.Add(apply);
            buttons.Children.Add(save);
            buttons.Children.Add(reset);
            row.Children.Add(buttons);
            return row;
        }

        private void ApplyLayoutFromRow(RobloxInstance inst, string accountId, ComboBox monitorBox,
            TextBox xBox, TextBox yBox, TextBox wBox, TextBox hBox, bool save)
        {
            int x, y, w, h;
            if (!ParseInt(xBox.Text, out x) || !ParseInt(yBox.Text, out y)
                || !ParseInt(wBox.Text, out w) || !ParseInt(hBox.Text, out h))
            {
                ShowTemporaryError("Layout X, Y, W and H must be whole numbers.");
                return;
            }
            if (w < MinLayoutWidth || h < MinLayoutHeight)
            {
                ShowTemporaryError("Layout width must be at least " + MinLayoutWidth + " and height at least " + MinLayoutHeight + " pixels.");
                return;
            }
            int monitorIndex = monitorBox.SelectedIndex;
            if (monitorIndex < 0 || monitorIndex >= _monitors.Length)
            {
                ShowTemporaryError("Choose a monitor for this layout.");
                return;
            }
            MonitorInfo m = _monitors[monitorIndex];
            if (x < m.FullX || y < m.FullY || x + w > m.FullX + m.FullWidth || y + h > m.FullY + m.FullHeight)
            {
                ShowTemporaryError("That layout is outside monitor " + (monitorIndex + 1).ToString(CultureInfo.InvariantCulture) + ".");
                return;
            }

            AccountLayout layout = new AccountLayout();
            layout.MonitorIndex = monitorIndex;
            layout.X = x;
            layout.Y = y;
            layout.Width = w;
            layout.Height = h;

            if (save)
            {
                if (ActiveProfile == null || accountId == null)
                {
                    ShowTemporaryError("Layouts are saved per account. Launch this instance from a profile first.");
                    return;
                }
                ActiveProfile.SetLayout(accountId, layout);
                string error;
                if (!ProfileStore.SaveLayouts(ActiveProfile.Name, ActiveProfile, out error))
                {
                    ShowTemporaryError("Could not save the layout: " + error);
                    return;
                }
                AppendLog("Layout saved to profile " + ActiveProfile.Name + ".");
            }

            WindowLayout placement = ToWindowLayout(layout);
            int pid = inst.Pid;
            ThreadPool.QueueUserWorkItem(delegate(object state)
            {
                IntPtr hwnd = WindowManager.FindWindowForPid(pid, 3000);
                Dispatcher.BeginInvoke(new Action(delegate
                {
                    if (hwnd == IntPtr.Zero)
                    {
                        AppendLog("No window found for " + inst.Name + ".");
                        return;
                    }
                    try
                    {
                        WindowManager.Place(hwnd, placement);
                    }
                    catch (Exception ex)
                    {
                        AppendLog("Placement failed: " + ex.Message);
                    }
                }));
            });
        }

        private void ResetLayoutForRow(string accountId)
        {
            if (accountId != null && ActiveProfile != null)
            {
                ActiveProfile.Layouts.Remove(accountId);
                string error;
                if (!ProfileStore.SaveLayouts(ActiveProfile.Name, ActiveProfile, out error))
                {
                    ShowTemporaryError("Could not save the layout reset: " + error);
                    return;
                }
            }
            TileAll();
            _advancedSignature = "";
        }

        private static bool ParseInt(string text, out int value)
        {
            return int.TryParse((text ?? "").Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out value);
        }

        // ------------------------------------------------------------------
        // Advanced: auto-click settings
        // ------------------------------------------------------------------

        private bool MasterOn
        {
            get { return _autoMaster != null && _autoMaster.IsChecked == true; }
        }

        private void OnMasterChanged(object sender, RoutedEventArgs e)
        {
            ApplyKeepAliveConfig();
            ReapplyAllAuto();
            foreach (KeyValuePair<RobloxInstance, InstanceCard> pair in _cards)
            {
                pair.Value.SetAutoAvailable(MasterOn);
            }
        }

        private void CommitInterval()
        {
            int value;
            if (!ParseInt(_intervalBox.Text, out value))
            {
                value = DefaultIntervalSeconds;
            }
            value = Math.Max(MinIntervalSeconds, Math.Min(MaxIntervalSeconds, value));
            _intervalBox.Text = value.ToString(CultureInfo.InvariantCulture);
            _intervalSeconds = value;
            ReapplyAllAuto();
        }

        private void ReapplyAllAuto()
        {
            foreach (KeyValuePair<RobloxInstance, InstanceCard> pair in _cards)
            {
                pair.Value.ReapplyAuto();
            }
        }

        /// <summary>Pushes one instance's auto-click configuration to the clicker. Never throws.</summary>
        private void ApplyClickerConfig(RobloxInstance inst, bool enabled)
        {
            if (Clicker == null || inst == null)
            {
                return;
            }
            try
            {
                Clicker.Configure(inst.Id, inst.Pid, enabled, _intervalSeconds, _nudge, NudgeMinutes);
            }
            catch (Exception ex)
            {
                AppendLog("Auto-click setting failed for " + inst.Name + ": " + ex.Message);
            }
        }

        private void RefreshCardStats()
        {
            InTeleportPass(delegate
            {
                foreach (KeyValuePair<RobloxInstance, InstanceCard> pair in _cards)
                {
                    pair.Value.RefreshStats();
                    pair.Value.RefreshTeleport();
                }
            });
        }

        // ------------------------------------------------------------------
        // Settings: mini bar, start with Windows, keep-alive
        // ------------------------------------------------------------------

        /// <summary>Creates the mini bar when needed and shows it docked to the taskbar.</summary>
        private void EnsureMiniBar()
        {
            if (_miniBar == null)
            {
                _miniBar = new MiniBar(this);
                _miniBar.Closed += OnMiniBarClosed;
            }
            try
            {
                _miniBar.ShowDocked();
            }
            catch (Exception ex)
            {
                AppendLog("Mini bar could not be shown: " + ex.Message);
            }
            SetSettingCheck(_miniBarCheck, true);
        }

        private void CloseMiniBar()
        {
            MiniBar bar = _miniBar;
            _miniBar = null;
            if (bar == null)
            {
                return;
            }
            try
            {
                bar.Closed -= OnMiniBarClosed;
                bar.Close();
            }
            catch
            {
                // Closing the bar must never block the launcher from exiting.
            }
        }

        private void OnMiniBarClosed(object sender, EventArgs e)
        {
            if (_exiting)
            {
                return;
            }
            _miniBar = null;
            SetSettingCheck(_miniBarCheck, false);
            SaveMiniBarSettings();
        }

        private void OnMiniBarToggled(object sender, RoutedEventArgs e)
        {
            if (_settingsSync)
            {
                return;
            }
            if (_miniBarCheck.IsChecked == true)
            {
                EnsureMiniBar();
            }
            else
            {
                CloseMiniBar();
            }
            SaveMiniBarSettings();
        }

        private void OnAutoStartToggled(object sender, RoutedEventArgs e)
        {
            if (_settingsSync)
            {
                return;
            }

            // schtasks can take a few seconds, so it runs off the UI thread; the box is locked meanwhile.
            bool enable = _autoStartCheck.IsChecked == true;
            string exe = EntryExePath();
            _autoStartCheck.IsEnabled = false;
            ThreadPool.QueueUserWorkItem(delegate(object state)
            {
                bool ok = false;
                string message;
                bool enabled = false;
                try
                {
                    message = enable ? AutoStart.Enable(exe, out ok) : AutoStart.Disable(out ok);
                }
                catch (Exception ex)
                {
                    message = ex.Message;
                    ok = false;
                }
                try { enabled = AutoStart.IsEnabled(); } catch { }

                Dispatcher.BeginInvoke(new Action(delegate
                {
                    _banner.Show(ok ? "Info" : "Warning", message ?? (ok ? "Done." : "Could not change the Windows startup setting."));
                    _bannerHoldUntilUtc = DateTime.UtcNow.AddSeconds(ErrorBannerHoldSeconds);
                    AppendLog("Start with Windows: " + message);
                    // Show what Windows actually has, without firing this handler again.
                    SetSettingCheck(_autoStartCheck, enabled);
                    _autoStartCheck.IsEnabled = true;
                }));
            });
        }

        /// <summary>Reads the real scheduled-task state once at start so the checkbox is truthful.</summary>
        private void LoadAutoStartState()
        {
            ThreadPool.QueueUserWorkItem(delegate(object state)
            {
                bool enabled = false;
                try { enabled = AutoStart.IsEnabled(); } catch { }
                Dispatcher.BeginInvoke(new Action(delegate { SetSettingCheck(_autoStartCheck, enabled); }));
            });
        }

        private void OnEfficiencyToggled(object sender, RoutedEventArgs e)
        {
            if (_settingsSync)
            {
                return;
            }
            _bgEfficiency = _efficiencyCheck.IsChecked == true;
            Efficiency.Enabled = _bgEfficiency;   // turning it off restores every client at once
            SaveMiniBarSettings();
            AppendLog(_bgEfficiency
                ? "Background clients now run at lower CPU priority (the client in front stays at full speed)."
                : "Background CPU saving is off; all clients are back at normal priority.");
        }

        private void OnKeepAliveToggled(object sender, RoutedEventArgs e)
        {
            if (_settingsSync)
            {
                return;
            }
            _keepAlive = _keepAliveCheck.IsChecked == true;
            SaveMiniBarSettings();
        }

        private static string EntryExePath()
        {
            Assembly entry = Assembly.GetEntryAssembly();
            return entry != null ? entry.Location : "";
        }

        /// <summary>Sets a settings checkbox without firing its handler.</summary>
        private void SetSettingCheck(CheckBox box, bool value)
        {
            if (box == null)
            {
                return;
            }
            bool previous = _settingsSync;
            _settingsSync = true;
            try
            {
                box.IsChecked = value;
            }
            finally
            {
                _settingsSync = previous;
            }
        }

        /// <summary>Shows the mini bar and reveals the main window again.</summary>
        public void ShowMainWindow()
        {
            _startHidden = false;
            if (!IsVisible)
            {
                Show();
            }
            if (WindowState == WindowState.Minimized)
            {
                WindowState = WindowState.Normal;
            }
            Activate();
            // Windows refuses a plain Activate() while a game window has focus, so the launcher stayed behind it.
            // Wait briefly for any running click sequence (teleport, cash out, keep-alive) to finish its click,
            // so raising the launcher cannot make that click fail its "game window is in front" check.
            bool gotLock = false;
            try
            {
                gotLock = System.Threading.Monitor.TryEnter(AntiIdleClicker.ClickLock, 1500);
                IntPtr self = new System.Windows.Interop.WindowInteropHelper(this).Handle;
                if (self != IntPtr.Zero) WindowManager.BringToFront(self);
            }
            catch (Exception)
            {
            }
            finally
            {
                if (gotLock) System.Threading.Monitor.Exit(AntiIdleClicker.ClickLock);
            }
        }

        /// <summary>Invoked by the mini bar Play button. Accounts.cs sets PlayHook.</summary>
        internal void PlayFromMiniBar()
        {
            Action hook = PlayHook;
            if (hook == null)
            {
                return;
            }
            try
            {
                hook();
            }
            catch (Exception ex)
            {
                AppendLog("Play failed: " + ex.Message);
            }
        }

        /// <summary>Expands the Advanced panel (custom RAM, allocation warnings, per-instance layout) and scrolls it into view.</summary>
        internal void OpenAdvanced()
        {
            if (_advancedExpander == null)
            {
                return;
            }
            _advancedExpander.IsExpanded = true;
            _advancedExpander.BringIntoView();
        }

        /// <summary>Registers an open dialog so its title bar follows the theme. Removed when it closes.</summary>
        internal void TrackDialog(Window dialog)
        {
            if (dialog == null)
            {
                return;
            }
            _openDialogs.Add(dialog);
            dialog.Closed += delegate { _openDialogs.Remove(dialog); };
        }

        // Mini bar host (IMiniBarHost).

        public IList<RobloxInstance> Instances
        {
            get { return _manager.Instances; }
        }

        public void Stop(RobloxInstance instance)
        {
            StopInstance(instance);
        }

        public void Play()
        {
            PlayFromMiniBar();
        }

        public void StopAll()
        {
            StopAllWithConfirm();
        }

        public bool CanPlay
        {
            get { return PlayHook != null && !IsWorking(); }
        }

        public MiniBarStats GetStats()
        {
            MiniBarStats s = new MiniBarStats();
            s.Running = _manager.RunningCount;
            s.Recommended = RecommendedCount();
            s.HardMax = HardMaxCount();
            s.InstanceLimitOverride = _overrideInstanceLimit;
            s.CpuPercent = _monitor.CpuPercent;
            s.UsedRamPercent = _monitor.UsedRamPercent;
            s.FreeRamGb = _monitor.FreeRamGb;
            s.TotalRamGb = _monitor.TotalRamGb;
            return s;
        }

        /// <summary>Quits the launcher for real, even when keep-alive is on or the main window is hidden.</summary>
        public void ExitLauncher()
        {
            RequestExit();
        }

        /// <summary>
        /// Start-up path for --minibar: the main window is never shown, only the mini bar.
        /// Running clients are adopted by the constructor already.
        /// </summary>
        public void StartMiniBarMode()
        {
            _startHidden = true;
            EnsureMiniBar();
        }

        // ------------------------------------------------------------------
        // Busy state
        // ------------------------------------------------------------------

        private bool IsWorking()
        {
            return _workCount > 0;
        }

        private void ShowInfo(string message)
        {
            MessageBox.Show(this, message, "Cookie Jar", MessageBoxButton.OK, MessageBoxImage.Information);
        }

        /// <summary>Marks a long action as running: disables the toolbar and shows the reason.</summary>
        private void BeginWork(string text)
        {
            _workCount++;
            _workText.Text = text;
            RefreshButtons();
        }

        private void EndWork()
        {
            if (_workCount > 0)
            {
                _workCount--;
            }
            if (_workCount == 0)
            {
                _workText.Text = "";
            }
            RefreshButtons();
        }

        // ------------------------------------------------------------------
        // Stop and kill
        // ------------------------------------------------------------------

        private void OnStopAllClick(object sender, RoutedEventArgs e)
        {
            StopAllWithConfirm();
        }

        /// <summary>One summary confirmation, then every running instance is ended with a process-tree kill.</summary>
        internal void StopAllWithConfirm()
        {
            List<RobloxInstance> running = RunningInstances();
            if (running.Count == 0)
            {
                return;
            }
            if (!StopConfirmDialog.Confirm(IsVisible ? this : null, running))
            {
                AppendLog("Stop all cancelled.");
                return;
            }
            AppendLog("Stop all: ending " + running.Count + " instance(s) now...");
            RunStop(running);
        }

        /// <summary>Ends one instance (tree kill) after confirmation. Used by the card, tray and mini bar.</summary>
        internal void StopInstance(RobloxInstance instance)
        {
            if (instance == null)
            {
                return;
            }
            List<RobloxInstance> one = new List<RobloxInstance>();
            one.Add(instance);
            if (!StopConfirmDialog.Confirm(IsVisible ? this : null, one))
            {
                return;
            }
            AppendLog("Stop requested for " + instance.Name + " (PID " + instance.Pid + ")...");
            RunStop(one);
        }

        private void RunStop(List<RobloxInstance> targets)
        {
            _stopAllButton.IsEnabled = false;
            foreach (RobloxInstance instance in targets)
            {
                InstanceCard card;
                if (_cards.TryGetValue(instance, out card))
                {
                    card.SetBusy(true);
                }
            }
            ThreadPool.QueueUserWorkItem(delegate(object state)
            {
                foreach (RobloxInstance instance in targets)
                {
                    try
                    {
                        _manager.Stop(instance);
                    }
                    catch (Exception ex)
                    {
                        AppendLogFromAnyThread("Stop failed: " + ex.Message);
                    }
                }

                Dispatcher.BeginInvoke(new Action(delegate
                {
                    foreach (RobloxInstance instance in targets)
                    {
                        CleanupStopped(instance);
                        InstanceCard card;
                        if (_cards.TryGetValue(instance, out card))
                        {
                            card.SetBusy(false);
                        }
                    }
                    RefreshButtons();
                }));
            });
        }

        /// <summary>A stopped instance keeps its card (marked Stopped, Remove available) but no live timers or placement state.</summary>
        private void CleanupStopped(RobloxInstance instance)
        {
            if (Clicker != null)
            {
                try { Clicker.Remove(instance.Id); } catch { }
            }
            _placedIds.Remove(instance.Id);
            _placeTries.Remove(instance.Id);
        }

        /// <summary>Removes a stopped instance from the collection.</summary>
        private void RemoveInstance(RobloxInstance instance)
        {
            _manager.Instances.Remove(instance);
        }

        /// <summary>Stopped card "Relaunch": starts that account again and replaces the stopped card with the new one.</summary>
        internal void RelaunchInstance(RobloxInstance instance)
        {
            Account a = FindAccountFor(instance);
            if (a == null)
            {
                ShowInfo("This instance is not linked to a saved account, so it cannot be relaunched here.");
                return;
            }
            if (!CanLaunchSingle(a))
            {
                LaunchSingleAccount(a);   // shows why (already running) or does nothing while a launch is busy
                return;
            }
            AppendLog("Relaunching " + a.DisplayName + "...");
            RemoveInstance(instance);
            LaunchSingleAccount(a);
        }
        // ------------------------------------------------------------------
        // Instance cards
        // ------------------------------------------------------------------

        /// <summary>Keeps one card per instance in step with the collection.</summary>
        private void OnInstancesChanged(object sender, NotifyCollectionChangedEventArgs e)
        {
            if (e.Action == NotifyCollectionChangedAction.Reset)
            {
                foreach (InstanceCard card in _cards.Values)
                {
                    card.Dispose();
                }
                _cards.Clear();
                _instanceList.Children.Clear();
            }
            else
            {
                if (e.OldItems != null)
                {
                    foreach (RobloxInstance instance in e.OldItems)
                    {
                        RemoveCard(instance);
                    }
                }
                if (e.NewItems != null)
                {
                    foreach (RobloxInstance instance in e.NewItems)
                    {
                        AddCard(instance);
                    }
                }
            }

            MemoryGovernor.SetInstanceCountHint(_manager.Instances.Count);
            UpdateAutoRam();
            UpdateEmptyState();
            RefreshButtons();
            RefreshAdvancedIfChanged();
            UpdateRamSummary();
            TickInput();
        }

        private void AddCard(RobloxInstance instance)
        {
            if (_cards.ContainsKey(instance))
            {
                return;
            }

            InstanceCard card = new InstanceCard(this, instance);
            _cards[instance] = card;
            _instanceList.Children.Add(card.Root);
        }

        private void RemoveCard(RobloxInstance instance)
        {
            InstanceCard card;
            if (_cards.TryGetValue(instance, out card))
            {
                card.Dispose();
                _instanceList.Children.Remove(card.Root);
                _cards.Remove(instance);
            }
            if (Clicker != null)
            {
                try { Clicker.Remove(instance.Id); } catch { }
            }
            _ram.Remove(instance.Id);
            _placedIds.Remove(instance.Id);
            _placeTries.Remove(instance.Id);
            InstAccountId.Remove(instance.Id);
        }

        private void UpdateEmptyState()
        {
            bool empty = _manager.Instances.Count == 0;
            _emptyState.Visibility = empty ? Visibility.Visible : Visibility.Collapsed;
            UpdateInstanceHeading();
        }

        /// <summary>"2 running  ·  1 stopped" next to the Instances heading. Text is only reassigned when it changes.</summary>
        private void UpdateInstanceHeading()
        {
            if (_instancesCount == null) return;
            int running = 0, stopped = 0, other = 0;
            foreach (RobloxInstance inst in _manager.Instances)
            {
                string k = inst.StatusKind;
                if (k == "Running") running++;
                else if (k == "Stopped") stopped++;
                else other++;
            }
            string text = _manager.Instances.Count == 0 ? "none yet"
                : running.ToString(CultureInfo.InvariantCulture) + " running"
                  + (other > 0 ? "  ·  " + other.ToString(CultureInfo.InvariantCulture) + " starting" : "")
                  + (stopped > 0 ? "  ·  " + stopped.ToString(CultureInfo.InvariantCulture) + " stopped" : "");
            if (_instancesCount.Text != text) _instancesCount.Text = text;
        }

        /// <summary>
        /// One instance card in two rows: name, PID, profile, status and actions; then RAM, auto-click and caption.
        /// Subscribes to the instance's PropertyChanged and always unsubscribes on Dispose.
        /// </summary>
        private sealed class InstanceCard
        {
            private const string AutoTip = "Auto-click CASH OUT for this instance (needs the master switch in Advanced). "
                + "Briefly brings this window forward and clicks CASH OUT only when you are not active, to avoid idle timeouts.";

            private readonly MainWindow _owner;
            private readonly RobloxInstance _instance;
            private readonly Dispatcher _dispatcher;
            private readonly TextBlock _name;
            private readonly TextBlock _details;
            private readonly TextBlock _error;
            private readonly TextBlock _ramLabel;
            private readonly TextBlock _autoLabel;
            private readonly TextBlock _clickStats;
            private readonly TextBlock _caption;
            private readonly StatusBadge _badge;
            private readonly Button _switchButton;
            private readonly Button _stopButton;
            private readonly Button _removeButton;
            private readonly Button _relaunchButton;
            private readonly Button _layoutButton;
            private readonly RamSlider _ramSlider;
            private readonly ToggleSwitch _auto;
            private readonly TeleportCardRow _teleportRow;
            private readonly CashOutCardRow _cashRow;
            private readonly Button _hospitalButton;
            private readonly TextBlock _keepText;
            private readonly Border _warnChip;
            private readonly TextBlock _warnText;
            private readonly WrapPanel _controls;
            private readonly WrapPanel _footer;
            private readonly Border _divider;
            private bool _busy;
            private bool _shownThrottled;
            private bool _suppress;
            private int _lastPid = -1;

            public readonly Card Root;

            public InstanceCard(MainWindow owner, RobloxInstance instance)
            {
                _owner = owner;
                _instance = instance;
                _dispatcher = Dispatcher.CurrentDispatcher;

                _name = UiUtil.Label(" ", 15, true, Theme.Text);
                _details = UiUtil.Label(" ", 12, false, Theme.TextMuted);
                _details.TextWrapping = TextWrapping.Wrap;
                _details.Margin = new Thickness(0, 2, 0, 0);

                _error = UiUtil.Label(" ", 12, false, Theme.Warning);
                _error.TextWrapping = TextWrapping.Wrap;
                _error.Margin = new Thickness(0, 4, 0, 0);
                _error.Visibility = Visibility.Collapsed;

                StackPanel info = new StackPanel();
                info.Children.Add(_name);
                info.Children.Add(_details);
                info.Children.Add(_error);

                _badge = new StatusBadge();
                _badge.VerticalAlignment = VerticalAlignment.Center;
                _badge.Margin = new Thickness(8, 0, 0, 0);

                Grid header = new Grid();
                header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                Grid.SetColumn(info, 0);
                Grid.SetColumn(_badge, 1);
                header.Children.Add(info);
                header.Children.Add(_badge);

                _switchButton = PillButton.Create("Switch _to", PillStyle.Secondary);
                _switchButton.Click += delegate { _owner.SwitchTo(_instance); };
                _switchButton.Margin = new Thickness(0, 0, 8, 0);
                _switchButton.ToolTip = "Bring this instance's window to the front.";
                AutomationProperties.SetName(_switchButton, "Switch to " + instance.Name);

                _stopButton = PillButton.Create("_Stop", PillStyle.Danger);
                _stopButton.Click += delegate { _owner.StopInstance(_instance); };
                _stopButton.Margin = new Thickness(0, 0, 8, 0);
                _stopButton.ToolTip = "Ends this Roblox client and all its child processes immediately, like Process Explorer > Kill Process Tree";

                _removeButton = PillButton.Create("_Remove", PillStyle.Secondary);
                _removeButton.Click += delegate { _owner.RemoveInstance(_instance); };
                _removeButton.ToolTip = "Remove this stopped instance from the list.";
                _removeButton.Margin = new Thickness(0, 0, 8, 0);

                _relaunchButton = PillButton.Create("Re_launch", PillStyle.Primary);
                _relaunchButton.Click += delegate { _owner.RelaunchInstance(_instance); };
                _relaunchButton.ToolTip = "Start this account again (only this one; other instances keep running).";
                _relaunchButton.Margin = new Thickness(0, 0, 8, 0);
                AutomationProperties.SetName(_relaunchButton, "Relaunch " + instance.Name);

                _layoutButton = PillButton.Create("_Layout and RAM...", PillStyle.Secondary);
                _layoutButton.Click += delegate { _owner.OpenAdvanced(); };
                _layoutButton.ToolTip = "Open Advanced: custom RAM in MB, allocation warnings and this instance's window position (X, Y, W, H, monitor).";
                AutomationProperties.SetName(_layoutButton, "Open layout and RAM settings for " + instance.Name);

                WrapPanel actions = new WrapPanel();
                actions.Margin = new Thickness(0, 10, 0, 0);
                _cashRow = new CashOutCardRow(_owner, instance);
                _hospitalButton = PillButton.Create("_Hospital", PillStyle.Secondary);
                _hospitalButton.Margin = new Thickness(0, 0, 8, 0);
                _hospitalButton.ToolTip = HospitalButtonTip;
                AutomationProperties.SetName(_hospitalButton, "Go to the hospital in " + TeleportText.UserOf(instance));
                _hospitalButton.Click += delegate { _owner.HospitalButtonClicked(_instance); };
                actions.Children.Add(_switchButton);
                actions.Children.Add(_cashRow.Button);
                actions.Children.Add(_hospitalButton);
                actions.Children.Add(_stopButton);
                actions.Children.Add(_relaunchButton);
                actions.Children.Add(_removeButton);
                actions.Children.Add(_layoutButton);

                _ramLabel = UiUtil.Label("RAM limit", 12, false, Theme.TextMuted);
                _ramLabel.VerticalAlignment = VerticalAlignment.Center;
                _ramSlider = new RamSlider("RAM limit for " + instance.Name);
                _ramSlider.Margin = new Thickness(8, 0, 0, 0);
                _ramSlider.Committed += OnRamCommitted;

                StackPanel ramRow = new StackPanel();
                ramRow.Orientation = Orientation.Horizontal;
                ramRow.Margin = new Thickness(0, 8, 20, 0);
                ramRow.VerticalAlignment = VerticalAlignment.Top;
                ramRow.MinHeight = 34;   // line up with the Teleport row, not its result chip
                ramRow.Children.Add(_ramLabel);
                ramRow.Children.Add(_ramSlider);

                _auto = new ToggleSwitch();
                _auto.IsChecked = false;
                _auto.VerticalAlignment = VerticalAlignment.Center;
                _auto.ToolTip = AutoTip;
                AutomationProperties.SetName(_auto, "Auto-click CASH OUT for " + instance.Name);
                _auto.Checked += OnAutoChanged;
                _auto.Unchecked += OnAutoChanged;

                _autoLabel = UiUtil.Label("Auto-click CASH OUT", 12, false, Theme.Text);
                _autoLabel.VerticalAlignment = VerticalAlignment.Center;
                _autoLabel.Margin = new Thickness(8, 0, 0, 0);
                _autoLabel.ToolTip = AutoTip;

                _clickStats = UiUtil.Label("Clicks: 0, last: never", 12, false, Theme.TextMuted);
                _clickStats.VerticalAlignment = VerticalAlignment.Center;
                _clickStats.Margin = new Thickness(0, 0, 14, 0);

                StackPanel autoRow = new StackPanel();
                autoRow.Orientation = Orientation.Horizontal;
                autoRow.Margin = new Thickness(0, 8, 0, 0);
                autoRow.VerticalAlignment = VerticalAlignment.Top;
                autoRow.MinHeight = 34;
                autoRow.Children.Add(_auto);
                autoRow.Children.Add(_autoLabel);

                _caption = _clickStats;   // the long auto-click description now lives in the switch tooltip (AutoTip)

                _teleportRow = new TeleportCardRow(_owner, instance);
                _teleportRow.HideCaption();
                _teleportRow.Margin = new Thickness(0, 8, 20, 0);

                _keepText = UiUtil.Label("Last keep-alive: never", 12, false, Theme.TextMuted);
                _keepText.VerticalAlignment = VerticalAlignment.Center;
                _keepText.Margin = new Thickness(0, 0, 12, 0);
                _warnText = UiUtil.Label(" ", 12, true, Theme.Warning);
                _warnChip = new Border();
                _warnChip.CornerRadius = new CornerRadius(10);
                _warnChip.Padding = new Thickness(10, 3, 10, 3);
                _warnChip.Background = Theme.WarningSoft;
                _warnChip.Child = _warnText;
                _warnChip.Visibility = Visibility.Collapsed;
                // Controls row: Teleport, RAM limit and auto-click side by side (wraps on narrow windows).
                _controls = new WrapPanel();
                _controls.Margin = new Thickness(0, 2, 0, 0);
                _controls.Children.Add(_teleportRow);
                _controls.Children.Add(ramRow);
                _controls.Children.Add(autoRow);

                // Footer: one muted status line (clicks, keep-alive, warning chip).
                _footer = new WrapPanel();
                _footer.Margin = new Thickness(0, 10, 0, 0);
                _footer.Children.Add(_clickStats);
                _footer.Children.Add(_keepText);
                _footer.Children.Add(_warnChip);

                _divider = new Border();
                _divider.Height = 1;
                _divider.Margin = new Thickness(0, 12, 0, 2);
                _divider.Background = Theme.Border;

                StackPanel body = new StackPanel();
                body.Children.Add(header);
                body.Children.Add(actions);
                body.Children.Add(_cashRow);
                body.Children.Add(_divider);
                body.Children.Add(_controls);
                body.Children.Add(_footer);

                Root = new Card();
                Root.Padding = new Thickness(18, 14, 18, 14);
                Root.Margin = new Thickness(0, 0, 0, 10);
                Root.Child = body;

                _instance.PropertyChanged += OnInstancePropertyChanged;
                Refresh();
                SetAutoAvailable(_owner.MasterOn);
            }

            public void SetBusy(bool busy)
            {
                _busy = busy;
                Refresh();
            }

            /// <summary>Re-reads the Teleport row (targets, enabled state, spinner, result chip).</summary>
            public void RefreshTeleport()
            {
                _teleportRow.Refresh();
                _cashRow.Refresh();
                RefreshHospital();
            }

            /// <summary>Enables the Hospital button only when the route may start; the tooltip shows the last result.</summary>
            private void RefreshHospital()
            {
                string last = _owner.LastHospitalResult(_instance);
                if (_owner.HospitalRunningFor(_instance))
                {
                    // While running this button cancels the route.
                    _hospitalButton.IsEnabled = true;
                    _hospitalButton.ToolTip = "Hospital route running. Click to cancel.";
                    return;
                }
                string reason = _owner.HospitalBlockReason(_instance, false);
                _hospitalButton.IsEnabled = reason == null;
                string tip = reason == null ? HospitalButtonTip : reason + "\n" + HospitalButtonTip;
                _hospitalButton.ToolTip = last == null ? tip : tip + "\nLast: " + last;
            }

            /// <summary>Greys the auto-click switch when the master switch is off.</summary>
            public void SetAutoAvailable(bool masterOn)
            {
                _auto.IsEnabled = masterOn;
                ReapplyAuto();
            }

            /// <summary>Pushes this card's switch state (ANDed with the master switch) to the clicker.</summary>
            public void ReapplyAuto()
            {
                bool on = _auto.IsChecked == true && _owner.MasterOn;
                _owner.ApplyClickerConfig(_instance, on);
            }

            public void RefreshStats()
            {
                SyncRam();
                // The detail line normally follows memory/uptime changes; redraw it at once when the background state flips.
                if (_owner.Efficiency.IsThrottled(_instance.Pid) != _shownThrottled) RefreshDetails();
                if (_owner.Clicker == null)
                {
                    return;
                }
                AntiIdleStats stats = _owner.Clicker.GetStats(_instance.Id);
                string last = stats.LastClickUtc.HasValue
                    ? stats.LastClickUtc.Value.ToLocalTime().ToString("HH:mm:ss", CultureInfo.InvariantCulture)
                    : "never";
                string dismissed = stats.LastDismissUtc.HasValue
                    ? stats.Dismissals.ToString(CultureInfo.InvariantCulture) + " (last " + stats.LastDismissUtc.Value.ToLocalTime().ToString("HH:mm:ss", CultureInfo.InvariantCulture) + ")"
                    : stats.Dismissals.ToString(CultureInfo.InvariantCulture);
                _clickStats.Text = "Clicks: " + stats.Clicks.ToString(CultureInfo.InvariantCulture) + ", last: " + last
                    + ", invites/requests declined: " + dismissed
                    + (string.IsNullOrEmpty(stats.LastMethod) ? "" : " via " + stats.LastMethod)
                    + (string.IsNullOrEmpty(stats.LastError) ? "" : "  (last problem: " + stats.LastError + ")");
                RefreshKeepAlive();
                _cashRow.Refresh();
                RefreshHospital();
            }

            /// <summary>"Last keep-alive: 2m ago" and the warning chip when no input was confirmed for 12+ minutes.</summary>
            public void RefreshKeepAlive()
            {
                KeepAliveService ka = _owner.KeepAlive;
                if (ka == null)
                {
                    return;
                }
                KeepAliveStats ks = ka.GetStats(_instance.Pid);
                bool running = _instance.StatusKind == "Running";
                string text;
                if (ks.LastKeepAliveUtc.HasValue)
                {
                    text = "Last keep-alive: " + KeepAliveService.AgoText(ks.LastKeepAliveUtc);
                }
                else
                {
                    text = _owner.KeepAliveActive ? "Last keep-alive: not yet" : "Keep-alive: off";
                }
                if (!string.IsNullOrEmpty(ks.LastResult) && ks.LastResult.StartsWith("failed", StringComparison.Ordinal))
                {
                    text += "  (last attempt failed)";
                }
                _keepText.Text = text;

                int mins = KeepAliveService.MinutesSinceInput(_instance.Pid, ks);
                if (running && mins >= KeepAliveService.WarnAfterMinutes)
                {
                    _warnText.Text = "No confirmed input for " + mins.ToString(CultureInfo.InvariantCulture) + " min - may time out at 20";
                    _warnChip.Visibility = Visibility.Visible;
                }
                else
                {
                    _warnChip.Visibility = Visibility.Collapsed;
                }
            }

            /// <summary>Re-reads brushes after a theme change.</summary>
            public void ApplyTheme()
            {
                _name.Foreground = Theme.Text;
                _autoLabel.Foreground = Theme.Text;
                _details.Foreground = Theme.TextMuted;
                _caption.Foreground = Theme.TextMuted;
                _clickStats.Foreground = Theme.TextMuted;
                _keepText.Foreground = Theme.TextMuted;
                _warnText.Foreground = Theme.Warning;
                _warnChip.Background = Theme.WarningSoft;
                _ramLabel.Foreground = Theme.TextMuted;
                _error.Foreground = Theme.Warning;
                _divider.Background = Theme.Border;
                _ramSlider.ApplyTheme();
                _teleportRow.ApplyTheme();
                _cashRow.ApplyTheme();
            }

            public void Dispose()
            {
                _instance.PropertyChanged -= OnInstancePropertyChanged;
            }

            /// <summary>The slider settled on a value: 0 = Auto (no cap), otherwise a cap in MB.</summary>
            private void OnRamCommitted(int mb)
            {
                if (mb <= 0)
                {
                    _owner.SetInstanceRam(_instance, MemoryPreset.Unlimited, 0);
                }
                else
                {
                    _owner.SetInstanceRam(_instance, MemoryPreset.Custom, mb);
                }
            }

            /// <summary>Keeps the slider showing what is really in force (the setting can change from the profile rows or the Advanced panel).</summary>
            public void SyncRam()
            {
                RamSetting setting = _owner.GetRam(_instance);
                int mb = 0;
                if (setting != null && setting.Preset != MemoryPreset.Unlimited)
                {
                    mb = MemoryGovernor.ResolveMb(setting.Preset, setting.CustomMb);
                }
                _ramSlider.SetMb(mb);
                _ramSlider.IsEnabled = !_owner._autoRam;
                if (_owner._autoRam) _ramSlider.ToolTip = "Managed by Auto RAM allocation in Settings. Switch it off to use manual session limits.";
            }

            private void OnAutoChanged(object sender, RoutedEventArgs e)
            {
                if (_suppress)
                {
                    return;
                }
                ReapplyAuto();
                _owner.AppendLog((_auto.IsChecked == true ? "Auto-click on" : "Auto-click off") + " for " + _instance.Name + ".");
            }

            private void OnInstancePropertyChanged(object sender, PropertyChangedEventArgs e)
            {
                // Memory and uptime change every second and only the detail line depends on them, so they skip the full
                // refresh (buttons, RAM combo, relaunch lookup). MemoryText duplicates MemoryMb; ChildCount is not shown.
                string prop = e.PropertyName;
                if (prop == "MemoryText" || prop == "ChildCount") return;
                Action work = (prop == "MemoryMb" || prop == "Uptime") ? new Action(RefreshDetails) : new Action(Refresh);
                // Stop runs on a worker thread, so property changes may arrive off the UI thread.
                if (!_dispatcher.CheckAccess())
                {
                    _dispatcher.BeginInvoke(work);
                    return;
                }
                work();
            }

            private void RefreshDetails()
            {
                _shownThrottled = _owner.Efficiency.IsThrottled(_instance.Pid);
                string login = string.IsNullOrEmpty(_instance.AccountName)
                    ? "Website login"
                    : "Stored account" + (string.IsNullOrEmpty(_instance.ProfileName) ? "" : "  ·  Profile " + _instance.ProfileName);
                bool isStopped = _instance.StatusKind == "Stopped";
                string details = (string.IsNullOrEmpty(_instance.AccountName) ? "" : _instance.AccountName + "  ·  ")
                    + login
                    + (_instance.IsAdopted ? "  ·  Adopted" : "")
                    + (isStopped ? "" : "  ·  " + UiUtil.FormatMb(_instance.MemoryMb) + "  ·  Up " + Text(_instance.Uptime, "0s"))
                    + (!isStopped && _owner.Efficiency.IsThrottled(_instance.Pid) ? "  ·  Background (lower CPU priority)" : "")
                    + "  ·  PID " + _instance.Pid.ToString(CultureInfo.InvariantCulture);
                if (details != _details.Text) _details.Text = details;
            }

            private void Refresh()
            {
                string name = string.IsNullOrEmpty(_instance.Name) ? "Instance" : _instance.Name;
                _name.Text = name;

                RefreshDetails();
                bool isStopped = _instance.StatusKind == "Stopped";

                if (string.IsNullOrEmpty(_instance.LastError))
                {
                    _error.Visibility = Visibility.Collapsed;
                }
                else
                {
                    _error.Text = "Note: " + _instance.LastError;
                    _error.Visibility = Visibility.Visible;
                }

                _badge.SetStatus(string.IsNullOrEmpty(_instance.Status) ? "Stopped" : _instance.Status);

                bool stopped = isStopped;
                _switchButton.IsEnabled = !stopped && !_busy;
                _stopButton.IsEnabled = !stopped && !_busy;
                // A stopped card shows only what still applies: Relaunch, Remove, Layout.
                Visibility live = stopped ? Visibility.Collapsed : Visibility.Visible;
                _switchButton.Visibility = live;
                _stopButton.Visibility = live;
                _cashRow.Button.Visibility = live;
                _hospitalButton.Visibility = live;
                _controls.Visibility = live;
                _footer.Visibility = live;
                _divider.Visibility = live;
                _removeButton.Visibility = stopped ? Visibility.Visible : Visibility.Collapsed;
                _removeButton.IsEnabled = !_busy;
                Account relaunchAccount = stopped ? _owner.FindAccountFor(_instance) : null;
                _relaunchButton.Visibility = relaunchAccount != null ? Visibility.Visible : Visibility.Collapsed;
                _relaunchButton.IsEnabled = !_busy && _owner.CanLaunchSingle(relaunchAccount);

                // Keep the RAM slider in step with what is in force for this instance.
                SyncRam();

                // A relaunch changes the PID; the clicker needs the new one.
                if (_instance.Pid != _lastPid)
                {
                    _lastPid = _instance.Pid;
                    if (_auto.IsChecked == true)
                    {
                        ReapplyAuto();
                    }
                }
            }
        }

        // ------------------------------------------------------------------
        // Activity log
        // ------------------------------------------------------------------

        /// <summary>Log handler that is safe to call from any thread.</summary>
        private void AppendLogSafe(string message)
        {
            if (!Dispatcher.CheckAccess())
            {
                Dispatcher.BeginInvoke(new Action(delegate { AppendLog(message); }));
                return;
            }
            AppendLog(message);
        }

        private void AppendLogFromAnyThread(string message)
        {
            AppendLogSafe(message);
        }

        /// <summary>Appends a timestamped line on the UI thread, trimming to the newest 500 lines.</summary>
        private void AppendLog(string message)
        {
            if (_logBox == null)
            {
                return;
            }

            _logBox.AppendText(DateTime.Now.ToString("HH:mm:ss", CultureInfo.InvariantCulture) + "  " + message + Environment.NewLine);
            _logLineCount++;

            // Trim back to MaxLogLines in one batch when the log passes MaxLogLines + 100: replacing the whole Text re-lays out the box,
            // so doing it for every new line (the old behaviour) cost a full re-layout per log line.
            if (_logLineCount > MaxLogLines + 100)
            {
                string text = _logBox.Text;
                int cut = 0;
                int drop = _logLineCount - MaxLogLines;
                for (int i = 0; i < drop; i++)
                {
                    int newline = text.IndexOf('\n', cut);
                    if (newline < 0) break;
                    cut = newline + 1;
                    _logLineCount--;
                }
                if (cut > 0) _logBox.Text = text.Substring(cut);
            }

            _logBox.ScrollToEnd();
        }

        private void ClearLog()
        {
            _logBox.Clear();
            _logLineCount = 0;
        }

        // ------------------------------------------------------------------
        // Theme and window lifecycle
        // ------------------------------------------------------------------

        private void OnThemeSwitchChanged(object sender, RoutedEventArgs e)
        {
            if (_syncingTheme)
            {
                return;
            }

            bool dark = _themeSwitch.IsChecked == true;
            Theme.Apply(dark);
            Theme.ApplyTitleBar(this);
            UpdateThemeIcon();
        }

        /// <summary>
        /// Re-reads every Theme brush this window holds, so a replaced brush never leaves stale colours.
        /// Wrapped in try/catch: a theme change must never show an error dialog.
        /// </summary>
        private void OnThemeChanged()
        {
            try
            {
                _syncingTheme = true;
                try
                {
                    _themeSwitch.IsChecked = Theme.IsDark;
                }
                finally
                {
                    _syncingTheme = false;
                }

                UpdateThemeIcon();
                Background = Theme.Bg;
                Foreground = Theme.Text;
                ApplyThemeBrushes();
                foreach (KeyValuePair<RobloxInstance, InstanceCard> pair in _cards)
                {
                    pair.Value.ApplyTheme();
                }
                if (_advancedExpander != null) { _advancedExpander.Foreground = Theme.Text; }
                if (_settingsExpander != null) { _settingsExpander.Foreground = Theme.Text; }
                if (_autoMaster != null) { _autoMaster.Foreground = Theme.Text; }
                if (_nudgeCheck != null) { _nudgeCheck.Foreground = Theme.Text; }
                ApplyInputTheme();
                if (_miniBarCheck != null) { _miniBarCheck.Foreground = Theme.Text; }
                if (_autoStartCheck != null) { _autoStartCheck.Foreground = Theme.Text; }
                if (_keepAliveCheck != null) { _keepAliveCheck.Foreground = Theme.Text; }
                if (_efficiencyCheck != null) { _efficiencyCheck.Foreground = Theme.Text; }
                if (_autoRamCheck != null) { _autoRamCheck.Foreground = Theme.Text; }
                if (_overrideLimitCheck != null) { _overrideLimitCheck.Foreground = Theme.Warning; }
                if (_intervalBox != null) { _intervalBox.Foreground = Theme.Text; _intervalBox.Background = Theme.SurfaceAlt; _intervalBox.BorderBrush = Theme.Border; }
                if (_logBox != null) { _logBox.Background = Theme.SurfaceAlt; _logBox.Foreground = Theme.Text; }
                if (_capacityBar != null) { _capacityBar.ApplyTheme(); _cpuBar.ApplyTheme(); _ramBar.ApplyTheme(); }
                foreach (Border div in _statDividers) { div.Background = Theme.Border; }
                if (_profileDivider != null) { _profileDivider.Background = Theme.Border; }
                if (_instancesHeading != null) { _instancesHeading.Foreground = Theme.Text; _instancesCount.Foreground = Theme.TextMuted; }
                _advancedSignature = ""; // per-instance Advanced rows are rebuilt with the new brushes on the next tick
                Theme.ApplyTitleBar(this);
                foreach (Window dialog in _openDialogs.ToArray())
                {
                    try { Theme.ApplyTitleBar(dialog); } catch { }
                }
                if (_tray != null) { _tray.ApplyTheme(); }
                if (_exitButton != null) { _exitButton.Foreground = Theme.Danger; _exitButton.BorderBrush = Theme.Danger; _exitButton.Background = Theme.DangerSoft; }
                if (_miniBar != null)
                {
                    try { _miniBar.RefreshNow(); } catch { }
                }
            }
            catch
            {
                // A failed re-theme keeps the previous colours; it must not surface as an error.
            }
        }

        /// <summary>Re-assigns the brushes of the text and bars this window owns.</summary>
        private void ApplyThemeBrushes()
        {
            foreach (TextBlock t in _primaryTexts)
            {
                t.Foreground = Theme.Text;
            }
            foreach (TextBlock t in _mutedTexts)
            {
                t.Foreground = Theme.TextMuted;
            }
        }

        private void UpdateThemeIcon()
        {
            _themeIcon.Text = Theme.IsDark ? MoonGlyph : SunGlyph;
        }

        /// <summary>Warns before closing with instances still running. With keep-alive on, the window hides instead.</summary>
        private void OnClosing(object sender, CancelEventArgs e)
        {
            if (_exiting)
            {
                return;
            }
            // The X button never quits: it hides the window into the tray. Exit is explicit (button, tray, mini bar).
            e.Cancel = true;
            HideToTray();
        }

        private bool _trayTipShown;
        private TrayIcon _tray;

        /// <summary>Hides the main window (no taskbar button) and shows the one-time balloon.</summary>
        internal void HideToTray()
        {
            Hide();
            if (!_trayTipShown && _tray != null)
            {
                _trayTipShown = true;
                SaveMiniBarSettings();
                _tray.ShowBalloon();
            }
        }

        private void OnStateChanged(object sender, EventArgs e)
        {
            if (WindowState == WindowState.Minimized && !_exiting)
            {
                HideToTray();
                WindowState = WindowState.Normal;   // so Show() later restores a normal window
            }
        }

        private void ToggleMiniBarFromTray()
        {
            if (_miniBar != null && _miniBar.IsVisible) { CloseMiniBar(); SetSettingCheck(_miniBarCheck, false); }
            else { EnsureMiniBar(); }
            SaveMiniBarSettings();
        }

        private void BringAllFromTray()
        {
            List<RobloxInstance> running = RunningInstances();
            if (running.Count > 0) { BringAllToFront(running); }
        }

        /// <summary>Asks for confirmation when something is running, then exits for real.</summary>
        internal void RequestExit()
        {
            List<string> lines = new List<string>();
            List<RobloxInstance> running = RunningInstances();
            if (running.Count > 0)
            {
                StringBuilder names = new StringBuilder();
                foreach (RobloxInstance inst in running)
                {
                    if (names.Length > 0) { names.Append(", "); }
                    names.Append(string.IsNullOrEmpty(inst.Name) ? "Instance" : inst.Name);
                }
                lines.Add(running.Count.ToString(CultureInfo.InvariantCulture) + " Roblox instance(s) running: " + names);
            }
            if (_profileRunning) { lines.Add("A profile launch is in progress."); }
            if (MasterOn) { lines.Add("Auto-click is switched on."); }
            if (TeleportBusy) { lines.Add("A teleport is running (it will be cancelled)."); }

            bool stop = false;
            if (lines.Count > 0)
            {
                ExitChoice choice = ExitDialog.Ask(string.Join("\n", lines.ToArray()));
                if (choice == ExitChoice.GoBack) { return; }
                stop = choice == ExitChoice.ExitStop;
            }
            DoExit(stop);
        }

        private void DoExit(bool stopInstances)
        {
            _exiting = true;
            CancelTeleport();
            CancellationTokenSource runCts = _runCts;
            if (runCts != null)
            {
                try { runCts.Cancel(); } catch (ObjectDisposedException) { }
            }
            if (stopInstances)
            {
                try { _manager.StopAll(); } catch { }
            }
            try { Close(); } catch { }
            if (Application.Current != null)
            {
                Application.Current.Shutdown();
            }
        }

        /// <summary>Windows logoff or shutdown: close without asking or blocking.</summary>
        internal void OnSessionEnding()
        {
            _exiting = true;
            try { Close(); } catch { }
        }

        /// <summary>Starts hidden when launched with --minibar, and restores the saved mini bar state.</summary>
        private void OnLoadedWindow(object sender, RoutedEventArgs e)
        {
            Loaded -= OnLoadedWindow;
            try
            {
                bool savedMiniBar = LoadedMiniBarFlag;
                if (savedMiniBar)
                {
                    EnsureMiniBar();
                }
            }
            catch (Exception ex)
            {
                AppendLog("Startup mini bar error: " + ex.Message);
            }
        }

        private bool _savedMiniBarOn;
        private bool LoadedMiniBarFlag
        {
            get { return _savedMiniBarOn; }
        }

        private void OnClosed(object sender, EventArgs e)
        {
            _exiting = true;
            _timer.Stop();
            _timer.Tick -= OnTimerTick;
            CancellationTokenSource runCts = _runCts;
            if (runCts != null)
            {
                try { runCts.Cancel(); } catch (ObjectDisposedException) { }
            }

            CloseMiniBar();
            if (_tray != null) { _tray.Dispose(); _tray = null; }
            try { MemoryGovernor.ClearAll(); } catch { }
            try { Efficiency.RestoreAll(); } catch { }

            _manager.Log -= AppendLogSafe;
            _manager.Instances.CollectionChanged -= OnInstancesChanged;
            Theme.Changed -= OnThemeChanged;

            CancelTeleport();
            if (Teleporter != null)
            {
                Teleporter.Log -= AppendLogSafe;
                Teleporter.Status -= OnTeleportStatus;
            }

            DisposeInput();
            if (Clicker != null)
            {
                Clicker.Log -= AppendLogSafe;
                try { Clicker.Dispose(); } catch { }
                Clicker = null;
            }

            foreach (InstanceCard card in _cards.Values)
            {
                card.Dispose();
            }
            _cards.Clear();

            _monitor.Dispose();
        }

        // ------------------------------------------------------------------
        // minibar.settings (key=value, UTF-8, next to Games.SettingsPath)
        // ------------------------------------------------------------------

        private static string SettingsFilePath()
        {
            string dir = null;
            try
            {
                string gamesPath = Games.SettingsPath;
                if (!string.IsNullOrEmpty(gamesPath))
                {
                    dir = Path.GetDirectoryName(gamesPath);
                }
            }
            catch
            {
                dir = null;
            }

            if (string.IsNullOrEmpty(dir))
            {
                dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "RobloxMultiLauncher");
            }
            return Path.Combine(dir, SettingsFileName);
        }

        /// <summary>Reads keep-alive and mini bar state. Missing or unreadable files mean the defaults (both off).</summary>
        private void LoadMiniBarSettings()
        {
            try
            {
                string path = SettingsFilePath();
                if (!File.Exists(path))
                {
                    return;
                }
                foreach (string raw in File.ReadAllLines(path, Encoding.UTF8))
                {
                    int eq = raw.IndexOf('=');
                    if (eq <= 0)
                    {
                        continue;
                    }
                    string key = raw.Substring(0, eq).Trim();
                    string value = raw.Substring(eq + 1).Trim();
                    bool on = string.Equals(value, "true", StringComparison.OrdinalIgnoreCase);
                    if (key == KeepAliveKey)
                    {
                        _keepAlive = on;
                    }
                    else if (key == EfficiencyKey)
                    {
                        _bgEfficiency = on;
                    }
                    else if (key == "autoRam")
                    {
                        _autoRam = on;
                    }
                    else if (key == "traytip")
                    {
                        _trayTipShown = on;
                    }
                    else if (key == MiniBarKey)
                    {
                        _savedMiniBarOn = on;
                    }
                    else
                    {
                        LoadInputSetting(key, value);
                    }
                }
            }
            catch
            {
                // Defaults stay in force.
            }
        }

        private void SaveMiniBarSettings()
        {
            try
            {
                string path = SettingsFilePath();
                string dir = Path.GetDirectoryName(path);
                if (!string.IsNullOrEmpty(dir))
                {
                    Directory.CreateDirectory(dir);
                }
                List<string> lineList = new List<string>();
                lineList.Add(KeepAliveKey + "=" + (_keepAlive ? "true" : "false"));
                lineList.Add(MiniBarKey + "=" + (_miniBar != null ? "true" : "false"));
                lineList.Add(EfficiencyKey + "=" + (_bgEfficiency ? "true" : "false"));
                lineList.Add("autoRam=" + (_autoRam ? "true" : "false"));
                lineList.Add("traytip=" + (_trayTipShown ? "true" : "false"));
                lineList.AddRange(InputSettingLines());
                string[] lines = lineList.ToArray();
                File.WriteAllLines(path, lines, Encoding.UTF8);
            }
            catch
            {
                // Settings are a convenience; failing to write them must not stop the launcher.
            }
        }

        // ------------------------------------------------------------------
        // Small helpers
        // ------------------------------------------------------------------

        /// <summary>Registers a text block so its brush is refreshed on theme changes.</summary>
        private bool IsInRamRows(TextBlock t)
        {
            for (DependencyObject d = t; d != null; d = LogicalTreeHelper.GetParent(d))
            {
                if (ReferenceEquals(d, _ramRows)) return true;
            }
            return false;
        }

        /// <summary>Heading for a collapsible section (same size and weight as the card headings).</summary>
        private TextBlock SectionHeader(string text)
        {
            TextBlock t = Track(UiUtil.Label(text, Theme.TextSection, true, Theme.Text), false);
            t.Margin = new Thickness(6, 0, 0, 0);
            return t;
        }

        private TextBlock Track(TextBlock text, bool muted)
        {
            if (muted)
            {
                _mutedTexts.Add(text);
            }
            else
            {
                _primaryTexts.Add(text);
            }
            return text;
        }

        private static CheckBox MakeCheck(string text)
        {
            CheckBox box = new CheckBox();
            box.Content = text;
            box.Foreground = Theme.Text;
            box.Margin = new Thickness(0, 4, 0, 0);
            AutomationProperties.SetName(box, text);
            return box;
        }

        private static TextBox MakeField(string value, double width, string accessibleName)
        {
            TextBox box = new TextBox();
            box.Text = value;
            box.Width = width;
            box.Margin = new Thickness(0, 0, 8, 0);
            box.VerticalAlignment = VerticalAlignment.Center;
            box.Foreground = Theme.Text;
            box.Background = Theme.SurfaceAlt;
            box.BorderBrush = Theme.Border;
            box.BorderThickness = new Thickness(1);
            box.Padding = new Thickness(6, 3, 6, 3);
            box.Height = 28;
            box.VerticalContentAlignment = VerticalAlignment.Center;
            AutomationProperties.SetName(box, accessibleName);
            return box;
        }

        private static RowDefinition Row(GridLength height)
        {
            RowDefinition row = new RowDefinition();
            row.Height = height;
            return row;
        }

        private static void AddToRow(Grid grid, UIElement element, int row, Thickness margin)
        {
            Grid.SetRow(element, row);
            FrameworkElement fe = element as FrameworkElement;
            if (fe != null)
            {
                fe.Margin = margin;
            }
            grid.Children.Add(element);
        }

        private static TextBlock Spaced(TextBlock text, double top)
        {
            text.Margin = new Thickness(0, top, 0, 0);
            return text;
        }

        private static string Text(string value, string fallback)
        {
            return string.IsNullOrEmpty(value) ? fallback : value.Trim();
        }

        private static string Fmt(double value, string format)
        {
            return value.ToString(format, CultureInfo.InvariantCulture);
        }

        private static SolidColorBrush BrushFor(RestrictResult result)
        {
            if (result == null)
            {
                return Theme.Accent;
            }
            switch (result.Level)
            {
                case RestrictLevel.Allow:
                    return Theme.Success;
                case RestrictLevel.Warn:
                    return Theme.Warning;
                default:
                    return Theme.Danger;
            }
        }
    }

}


