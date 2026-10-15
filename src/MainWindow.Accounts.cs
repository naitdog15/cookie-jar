using System;
using System.Collections.Generic;
using System.Globalization;
using System.Threading;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;

namespace RbxMulti
{
    // ---------------------------------------------------------------------------------------------
    // MainWindow.Accounts.cs - profile card, game selection, profile section controls and the
    // account list (partial class MainWindow). Public surface for the UI team:
    //
    //   Profile card controls (created in BuildProfileCard):
    //     _gameBox        ComboBox bound to Games.All, mirrors Games.Current, writes via Games.Select
    //     _gameText       "Launches: <game> (place <id>)" line, refreshed on Games.CurrentChanged
    //     _bringFrontButton "Bring all to front" -> BringAllToFront(running instances of ActiveProfile)
    //     _tileNowButton  "Tile now" -> TileAll()
    //     _autoTileSwitch "Auto-tile windows" -> ActiveProfile.AutoTile, saved via ProfileStore.Save
    //
    //   Account rows: a Low/Medium/High RAM ComboBox per account of ActiveProfile, saved via
    //   ActiveProfile.SetRamPreset + ProfileStore.Save, then ApplyRamAndPlacement on mapped instances.
    //
    //   Instance mapping: every 2 s on the UI thread (DispatcherTimer) unmapped RobloxInstances whose
    //   AccountName matches a member of ActiveProfile are added to InstAccountId and passed to
    //   ApplyRamAndPlacement. Stale ids are removed from InstAccountId. Covers profile launches and
    //   adoption without any index-based matching.
    //
    //   PlayHook is set to a method that runs the profile launch on the UI thread.
    //   ActiveProfile is refreshed on every RefreshButtons (selection change and list refresh).
    //
    //   Theme: ApplyProfileTheme() re-assigns brushes; it runs on Theme.Changed and is unsubscribed on
    //   window Closed, together with the Games.CurrentChanged subscription and the sync timer.
    // ---------------------------------------------------------------------------------------------

    /// <summary>Profiles, stored accounts and the manual (website login) launch controls.</summary>
    public partial class MainWindow
    {
        private static readonly string[] RamPresetChoices = new string[] { LaunchProfile.RamLow, LaunchProfile.RamMedium, LaunchProfile.RamHigh };

        private ComboBox _profileBox;
        private TextBlock _profileHint;
        private TextBlock _workText;
        private TextBlock _progressText;
        private Button _launchButton;            // website login launch
        private Button _launchProfileButton;
        private Button _cancelRunButton;
        private Button _newProfileButton;
        private Button _editProfileButton;
        private Button _deleteProfileButton;
        private Button _addAccountButton;
        private Expander _accountsExpander;
        private StackPanel _accountList;
        private TextBlock _accountsEmpty;
        private Button _stopAllButton;
        private Button _adoptButton;
        private CheckBox _forceFresh;
        private TextBlock _instancesHeading;
        private TextBlock _accountsHeader;
        private TextBlock _instancesCount;
        private Border _profileDivider;
        private CancellationTokenSource _runCts;
        private bool _profileRunning;
        private int _accountCount;

        private ComboBox _gameBox;
        private TextBlock _gameText;
        private Button _bringFrontButton;
        private Button _tileNowButton;
        private ToggleSwitch _autoTileSwitch;
        private TextBlock _autoTileText;
        private bool _suppressGameEvents;
        private bool _suppressProfileEvents;
        private DispatcherTimer _syncTimer;
        private string _lastActiveName;
        private TextBlock _cardTitle, _cardSub, _securityText;

        // ------------------------------------------------------------------
        // Layout
        // ------------------------------------------------------------------

        private UIElement BuildRoot()
        {
            Grid root = new Grid();
            root.Margin = new Thickness(24, 18, 24, 18);
            root.RowDefinitions.Add(Row(GridLength.Auto));                          // 0 header
            root.RowDefinitions.Add(Row(GridLength.Auto));                          // 1 hardware card
            root.RowDefinitions.Add(Row(GridLength.Auto));                          // 2 restriction banner
            root.RowDefinitions.Add(Row(GridLength.Auto));                          // 3 profiles
            root.RowDefinitions.Add(Row(GridLength.Auto));                          // 4 manual toolbar (heads the instance list)
            RowDefinition listRow = Row(new GridLength(1, GridUnitType.Star));      // 5 instance list
            listRow.MinHeight = 110;
            root.RowDefinitions.Add(listRow);
            root.RowDefinitions.Add(Row(GridLength.Auto));                          // 6 accounts (setup, below the live cards)
            root.RowDefinitions.Add(Row(GridLength.Auto));                          // 7 activity log

            AddToRow(root, BuildHeader(), 0, new Thickness(0, 0, 0, 12));
            AddToRow(root, BuildHardwareCard(), 1, new Thickness(0, 0, 0, 12));
            AddToRow(root, BuildBanner(), 2, new Thickness(0, 0, 0, 12));
            AddToRow(root, BuildProfileCard(), 3, new Thickness(0, 0, 0, 12));
            AddToRow(root, BuildToolbar(), 4, new Thickness(0, 0, 0, 12));
            AddToRow(root, BuildInstanceArea(), 5, new Thickness(0, 0, 0, 12));
            AddToRow(root, BuildAccountsSection(), 6, new Thickness(0, 0, 0, 12));
            AddToRow(root, BuildLogPanel(), 7, new Thickness(0));

            // The whole page scrolls when the window is short, so nothing is squeezed out of view.
            ScrollViewer page = new ScrollViewer();
            page.VerticalScrollBarVisibility = ScrollBarVisibility.Auto;
            page.HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled;
            page.Content = root;
            return page;
        }

        /// <summary>Profiles: pick a profile, pick the game, manage it, and launch all its accounts with one click.</summary>
        private UIElement BuildProfileCard()
        {
            TextBlock title = UiUtil.Label("Profiles", Theme.TextSection, true, Theme.Text);
            TextBlock sub = UiUtil.Label("A profile is a named group of accounts. One click opens them all, one after another, straight into the game.",
                12, false, Theme.TextMuted);
            sub.TextWrapping = TextWrapping.Wrap;
            sub.Margin = new Thickness(0, 2, 0, 10);
            _cardTitle = title;
            _cardSub = sub;

            _profileBox = new ComboBox();
            _profileBox.MinWidth = 240;
            _profileBox.Height = 34;
            _profileBox.VerticalContentAlignment = VerticalAlignment.Center;
            _profileBox.Margin = new Thickness(0, 0, 12, 0);
            _profileBox.ToolTip = "Choose which group of accounts to launch.";
            _profileBox.TabIndex = 1;
            AutomationProperties.SetName(_profileBox, "Profile");
            ThemedCombo.Register(Resources);
            ThemedCombo.Apply(_profileBox);

            _newProfileButton = PillButton.Create("_New", PillStyle.Secondary);
            _newProfileButton.Click += OnNewProfileClick;
            _newProfileButton.Margin = new Thickness(0, 0, 8, 0);
            _newProfileButton.ToolTip = "Create a profile from your accounts.";
            _newProfileButton.TabIndex = 2;

            _editProfileButton = PillButton.Create("_Edit", PillStyle.Secondary);
            _editProfileButton.Click += OnEditProfileClick;
            _editProfileButton.Margin = new Thickness(0, 0, 8, 0);
            _editProfileButton.ToolTip = "Rename the selected profile or change its accounts, game and delay.";
            _editProfileButton.TabIndex = 3;

            _deleteProfileButton = PillButton.Create("_Delete", PillStyle.Secondary);
            _deleteProfileButton.Click += OnDeleteProfileClick;
            _deleteProfileButton.ToolTip = "Delete the selected profile. Its accounts are kept.";
            _deleteProfileButton.TabIndex = 4;

            WrapPanel pick = new WrapPanel();
            pick.Children.Add(_profileBox);
            pick.Children.Add(_newProfileButton);
            pick.Children.Add(_editProfileButton);
            pick.Children.Add(_deleteProfileButton);

            _profileHint = UiUtil.Label(" ", 12, false, Theme.TextMuted);
            _profileHint.TextWrapping = TextWrapping.Wrap;
            _profileHint.Margin = new Thickness(0, 8, 0, 0);
            _profileHint.Visibility = Visibility.Collapsed;

            // Game picker (only Adopt Me! for now). Mirrors Games.Current.
            _gameBox = new ComboBox();
            _gameBox.MinWidth = 170;
            _gameBox.Height = 44;
            _gameBox.VerticalContentAlignment = VerticalAlignment.Center;
            _gameBox.Margin = new Thickness(0, 0, 10, 0);
            _gameBox.ToolTip = "Game every launch from this window opens. Only Adopt Me! is available for now.";
            _gameBox.TabIndex = 5;
            AutomationProperties.SetName(_gameBox, "Game");
            _gameBox.ItemsSource = Games.All;
            ThemedCombo.Apply(_gameBox);
            _suppressGameEvents = true;
            try
            {
                _gameBox.SelectedItem = Games.Current;
            }
            finally
            {
                _suppressGameEvents = false;
            }
            _gameBox.SelectionChanged += OnGameBoxChanged;

            _launchProfileButton = PillButton.Create("Launch pro_file", PillStyle.Primary);
            _launchProfileButton.FontSize = 15;
            _launchProfileButton.MinWidth = 260;
            _launchProfileButton.MinHeight = 44;
            _launchProfileButton.Click += OnLaunchProfileClick;
            _launchProfileButton.IsEnabled = false;
            _launchProfileButton.Margin = new Thickness(0, 0, 10, 0);
            _launchProfileButton.TabIndex = 6;
            AutomationProperties.SetName(_launchProfileButton, "Launch profile");

            _cancelRunButton = PillButton.Create("Cancel", PillStyle.Danger);
            _cancelRunButton.MinHeight = 44;
            _cancelRunButton.Click += OnCancelRunClick;
            _cancelRunButton.Visibility = Visibility.Collapsed;
            _cancelRunButton.Margin = new Thickness(0, 0, 12, 0);
            _cancelRunButton.ToolTip = "Stop launching the remaining accounts. Clients that already opened keep running.";
            _cancelRunButton.TabIndex = 7;

            _progressText = UiUtil.Label("", 13, false, Theme.Accent);
            _progressText.VerticalAlignment = VerticalAlignment.Center;
            _progressText.TextWrapping = TextWrapping.Wrap;
            AutomationProperties.SetName(_progressText, "Launch progress");

            WrapPanel go = new WrapPanel();
            go.Margin = new Thickness(0, 10, 0, 0);
            go.Children.Add(_gameBox);
            go.Children.Add(_launchProfileButton);
            go.Children.Add(_cancelRunButton);
            go.Children.Add(_progressText);

            _gameText = UiUtil.Label("", 12, false, Theme.TextMuted);
            _gameText.TextWrapping = TextWrapping.Wrap;
            _gameText.Margin = new Thickness(0, 6, 0, 0);
            AutomationProperties.SetName(_gameText, "Selected game");
            UpdateGameText();

            // Section controls: bring to front, tile, auto-tile.
            _bringFrontButton = PillButton.Create("Bring all to front", PillStyle.Secondary);
            _bringFrontButton.MinWidth = 0;
            _bringFrontButton.Margin = new Thickness(0, 0, 8, 0);
            _bringFrontButton.Click += OnBringFrontClick;
            _bringFrontButton.ToolTip = "Bring the running windows of this profile to the front, in profile order.";
            _bringFrontButton.IsEnabled = false;
            _bringFrontButton.TabIndex = 20;
            AutomationProperties.SetName(_bringFrontButton, "Bring all to front");

            _tileNowButton = PillButton.Create("Tile now", PillStyle.Secondary);
            _tileNowButton.MinWidth = 0;
            _tileNowButton.Margin = new Thickness(0, 0, 16, 0);
            _tileNowButton.Click += OnTileNowClick;
            _tileNowButton.ToolTip = "Arrange the running windows of this profile across your monitors now.";
            _tileNowButton.IsEnabled = false;
            _tileNowButton.TabIndex = 21;
            AutomationProperties.SetName(_tileNowButton, "Tile now");

            _autoTileSwitch = new ToggleSwitch();
            _autoTileSwitch.VerticalAlignment = VerticalAlignment.Center;
            _autoTileSwitch.Margin = new Thickness(0, 0, 8, 0);
            _autoTileSwitch.ToolTip = "Arrange windows automatically when this profile launches or an instance is adopted.";
            _autoTileSwitch.TabIndex = 22;
            _autoTileSwitch.IsEnabled = false;
            AutomationProperties.SetName(_autoTileSwitch, "Auto-tile windows");
            _autoTileSwitch.Checked += OnAutoTileChanged;
            _autoTileSwitch.Unchecked += OnAutoTileChanged;

            _autoTileText = UiUtil.Label("Auto-tile windows", 13, false, Theme.Text);
            _autoTileText.VerticalAlignment = VerticalAlignment.Center;

            WrapPanel sections = new WrapPanel();
            sections.Margin = new Thickness(0, 0, 0, 0);
            sections.Children.Add(_bringFrontButton);
            sections.Children.Add(_tileNowButton);
            sections.Children.Add(_autoTileSwitch);
            sections.Children.Add(_autoTileText);

            StackPanel body = new StackPanel();
            body.Children.Add(title);
            body.Children.Add(sub);
            body.Children.Add(pick);
            body.Children.Add(_profileHint);
            body.Children.Add(go);
            body.Children.Add(_gameText);
            // Window tools (front / tile / auto-tile) are separate from launching: a thin divider sets them apart.
            _profileDivider = new Border();
            _profileDivider.Height = 1;
            _profileDivider.Margin = new Thickness(0, 12, 0, 10);
            _profileDivider.Background = Theme.Border;
            body.Children.Add(_profileDivider);
            body.Children.Add(sections);

            Card card = new Card();
            card.Padding = Theme.CardPadding;
            card.Child = body;

            _profileBox.SelectionChanged += delegate { RefreshButtons(); };

            PlayHook = OnPlayHook;
            ApplyProfileTheme();
            Theme.Changed += OnProfileThemeChanged;
            Games.CurrentChanged += OnGameCurrentChanged;

            _syncTimer = new DispatcherTimer();
            _syncTimer.Interval = TimeSpan.FromSeconds(2);
            _syncTimer.Tick += OnSyncTick;
            _syncTimer.Start();

            Closed += OnProfileCardClosed;
            return card;
        }

        /// <summary>Collapsible accounts list with add / re-validate / remove and a security note.</summary>
        private UIElement BuildAccountsSection()
        {
            _addAccountButton = PillButton.Create("Add acc_ount", PillStyle.Secondary);
            _addAccountButton.Click += OnAddAccountClick;
            _addAccountButton.HorizontalAlignment = HorizontalAlignment.Left;
            _addAccountButton.ToolTip = "Add a Roblox account by pasting its .ROBLOSECURITY value. It is checked, then stored encrypted.";

            TextBlock security = UiUtil.Label(
                "Tokens are encrypted for this Windows user only. Windows encryption (DPAPI) cannot stop malware that runs as you, so use this only on a PC you trust.",
                12, false, Theme.TextMuted);
            security.TextWrapping = TextWrapping.Wrap;
            _securityText = security;
            security.Margin = new Thickness(0, 8, 0, 0);

            _accountsEmpty = UiUtil.Label("No accounts yet. Press Add account and follow the steps to paste the account's login key.",
                13, false, Theme.TextMuted);
            _accountsEmpty.TextWrapping = TextWrapping.Wrap;
            _accountsEmpty.Margin = new Thickness(0, 10, 0, 4);

            _accountList = new StackPanel();
            _accountList.Margin = new Thickness(0, 10, 4, 0);
            // No fixed height: the page scrolls, so rows are never cut off.
            ScrollViewer scroll = new ScrollViewer();
            scroll.VerticalScrollBarVisibility = ScrollBarVisibility.Auto;
            scroll.HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled;
            scroll.Content = _accountList;

            StackPanel body = new StackPanel();
            body.Margin = new Thickness(0, 6, 0, 0);
            body.Children.Add(_addAccountButton);
            body.Children.Add(security);
            body.Children.Add(_accountsEmpty);
            body.Children.Add(scroll);

            _accountsExpander = new Expander();
            _accountsHeader = SectionHeader("Accounts");
            _accountsExpander.Header = _accountsHeader;
            _accountsExpander.Foreground = Theme.Text;
            _accountsExpander.IsExpanded = false;
            _accountsExpander.Content = body;
            return _accountsExpander;
        }

        /// <summary>Manual launch (website login), stop all and adopt. Tab order follows the order added.</summary>
        private UIElement BuildToolbar()
        {
            _launchButton = PillButton.Create(LaunchLabel, PillStyle.Secondary);
            _launchButton.Click += OnLaunchClick;
            _launchButton.IsEnabled = false;
            _launchButton.Margin = new Thickness(0, 0, 8, 0);
            _launchButton.ToolTip = "Open the Adopt Me page in your default browser (Ctrl+N). Log in there and press Play; the cache is cleared first, your login is kept.";

            _stopAllButton = PillButton.Create("Sto_p all", PillStyle.Secondary);
            _stopAllButton.Click += OnStopAllClick;
            _stopAllButton.IsEnabled = false;
            _stopAllButton.Margin = new Thickness(0, 0, 8, 0);

            _adoptButton = PillButton.Create("_Adopt running clients", PillStyle.Secondary);
            _adoptButton.Click += OnAdoptClick;
            _adoptButton.Margin = new Thickness(0, 0, 8, 0);
            _adoptButton.ToolTip = "Add Roblox clients that are already running (started from the website) as cards, so you can Stop or Kill them here.";

            _forceFresh = new CheckBox();
            _forceFresh.Content = "Force fresh login (wipes saved Roblox login)";
            _forceFresh.IsChecked = false;
            _forceFresh.Foreground = Theme.Text;
            _forceFresh.VerticalAlignment = VerticalAlignment.Center;
            _forceFresh.Margin = new Thickness(8, 0, 0, 0);
            _forceFresh.ToolTip = "Off by default. Deletes the saved Roblox login before a website launch, only if no Roblox client is running at all.";
            AutomationProperties.SetName(_forceFresh, "Force fresh login");

            // Section heading for the instance list, with a live count (UpdateInstanceHeading).
            _instancesHeading = UiUtil.Label("Instances", Theme.TextSection, true, Theme.Text);
            _instancesCount = UiUtil.Label("", Theme.TextMeta, false, Theme.TextMuted);
            _instancesCount.Margin = new Thickness(10, 2, 0, 0);
            StackPanel heading = new StackPanel();
            heading.Orientation = Orientation.Horizontal;
            heading.Margin = new Thickness(0, 4, 0, 8);
            heading.Children.Add(_instancesHeading);
            heading.Children.Add(_instancesCount);

            WrapPanel row = new WrapPanel();
            row.Children.Add(_launchButton);
            row.Children.Add(_stopAllButton);
            row.Children.Add(_adoptButton);
            row.Children.Add(_forceFresh);

            _workText = UiUtil.Label("", 12, false, Theme.TextMuted);
            _workText.Margin = new Thickness(0, 4, 0, 0);

            StackPanel bar = new StackPanel();
            bar.Children.Add(heading);
            bar.Children.Add(row);
            bar.Children.Add(_workText);
            return bar;
        }

        // ------------------------------------------------------------------
        // Theme, game selection and window lifetime
        // ------------------------------------------------------------------

        /// <summary>Re-assigns the profile card brushes. Runs at build time and on every Theme.Changed.</summary>
        private void ApplyProfileTheme()
        {
            try
            {
                if (_profileBox != null)
                {
                    _profileBox.Background = Theme.Surface;
                    _profileBox.Foreground = Theme.Text;
                    _profileBox.BorderBrush = Theme.Border;
                }
                if (_gameBox != null)
                {
                    _gameBox.Background = Theme.Surface;
                    _gameBox.Foreground = Theme.Text;
                    _gameBox.BorderBrush = Theme.Border;
                }
                if (_gameText != null) { _gameText.Foreground = Theme.TextMuted; }
                if (_autoTileText != null) { _autoTileText.Foreground = Theme.Text; }
                if (_accountsExpander != null) { _accountsExpander.Foreground = Theme.Text; }
                if (_cardTitle != null) { _cardTitle.Foreground = Theme.Text; }
                if (_cardSub != null) { _cardSub.Foreground = Theme.TextMuted; }
                if (_securityText != null) { _securityText.Foreground = Theme.TextMuted; }
                if (_profileHint != null) { _profileHint.Foreground = Theme.TextMuted; }
                if (_accountsEmpty != null) { _accountsEmpty.Foreground = Theme.TextMuted; }
                if (_workText != null) { _workText.Foreground = Theme.TextMuted; }
                if (_forceFresh != null) { _forceFresh.Foreground = Theme.Text; }
                if (_progressText != null) { _progressText.Foreground = _profileRunning ? (Brush)Theme.Accent : Theme.TextMuted; }
            }
            catch (Exception)
            {
                // Theme switching must never throw.
            }
        }

        private void OnProfileThemeChanged()
        {
            try
            {
                ApplyProfileTheme();
                RefreshAccountList();
            }
            catch (Exception)
            {
                // Theme switching must never throw.
            }
        }

        private void OnProfileCardClosed(object sender, EventArgs e)
        {
            try { Games.CurrentChanged -= OnGameCurrentChanged; } catch (Exception) { }
            try { Theme.Changed -= OnProfileThemeChanged; } catch (Exception) { }
            if (_syncTimer != null)
            {
                _syncTimer.Stop();
            }
        }

        private void UpdateGameText()
        {
            if (_gameText == null) return;
            GameInfo g = Games.Current;
            _gameText.Text = "Launches: " + g.Name + " (place " + g.PlaceId.ToString(CultureInfo.InvariantCulture) + ")";
        }

        private void OnGameBoxChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_suppressGameEvents || _gameBox == null) return;
            GameInfo picked = _gameBox.SelectedItem as GameInfo;
            if (picked == null || picked.PlaceId == Games.Current.PlaceId) return;
            Games.Select(picked);
            AppendLog("Game set to " + picked.Name);
        }

        private void OnGameCurrentChanged()
        {
            Dispatcher.BeginInvoke(new Action(delegate
            {
                try
                {
                    _suppressGameEvents = true;
                    try
                    {
                        if (_gameBox != null && _gameBox.SelectedItem != Games.Current)
                        {
                            _gameBox.SelectedItem = Games.Current;
                        }
                    }
                    finally
                    {
                        _suppressGameEvents = false;
                    }
                    UpdateGameText();
                }
                catch (Exception)
                {
                }
            }));
        }

        /// <summary>Hook used by the mini bar Play button: same code path as the profile launch button.</summary>
        private void OnPlayHook()
        {
            try
            {
                Dispatcher.BeginInvoke(new Action(delegate
                {
                    try
                    {
                        OnLaunchProfileClick(this, new RoutedEventArgs());
                    }
                    catch (Exception)
                    {
                    }
                }));
            }
            catch (Exception)
            {
            }
        }

        // ------------------------------------------------------------------
        // Website login launch
        // ------------------------------------------------------------------

        private void OnLaunchClick(object sender, RoutedEventArgs e)
        {
            if (IsWorking() || _profileRunning || _restrictor == null || !_launchButton.IsEnabled)
            {
                return;
            }

            RestrictResult result = EvaluateRestriction();
            if (result == null)
            {
                return;
            }

            if (!result.CanLaunch)
            {
                AppendLog("Launch not allowed: " + result.Message);
                ShowTemporaryError(result.Message);
                return;
            }

            bool anyReal = RobloxProcesses.AnyRunning();
            bool wipe = false;
            if (_forceFresh.IsChecked == true)
            {
                if (anyReal)
                {
                    AppendLog("Force fresh login ignored: a Roblox client is running, its login is kept.");
                }
                else if (MessageBox.Show(this,
                    "This deletes your saved Roblox login on this PC (you will have to log in again on the website). Continue?",
                    "Force fresh login", MessageBoxButton.YesNo, MessageBoxImage.Warning) == MessageBoxResult.Yes)
                {
                    wipe = true;
                }
                else
                {
                    return;
                }
            }

            if (result.Level == RestrictLevel.Warn)
            {
                if (!TimedWarnDialog.Confirm(this, result.Message + "\n\nLaunch another instance anyway? Launching continues automatically when the countdown ends.",
                    "Launch anyway", "Cancel", TimedWarnDialog.DefaultSeconds))
                {
                    return;
                }
            }

            BeginWork("Preparing launch...");
            _launchButton.Content = "Launching this session...";
            AppendLog("Launching new instance (website login, " + Games.Current.Name + ")...");

            ThreadPool.QueueUserWorkItem(delegate(object state)
            {
                string error = null;
                bool ok = false;
                try
                {
                    ok = _manager.Launch(wipe, out error);
                }
                catch (Exception ex)
                {
                    error = ex.Message;
                    ok = false;
                }

                Dispatcher.BeginInvoke(new Action(delegate { OnLaunchFinished(ok, error); }));
            });
        }

        // ------------------------------------------------------------------
        // Profiles
        // ------------------------------------------------------------------

        /// <summary>Name of the selected profile, or null (placeholder row).</summary>
        private string SelectedProfileName()
        {
            if (_profileBox == null)
            {
                return null;
            }
            ComboBoxItem item = _profileBox.SelectedItem as ComboBoxItem;
            return item == null ? null : item.Tag as string;
        }

        private LaunchProfile SelectedProfile()
        {
            string name = SelectedProfileName();
            return name == null ? null : ProfileStore.Find(name);
        }

        /// <summary>Reloads the profile dropdown and selects <paramref name="select"/> when it exists.</summary>
        private void RefreshProfileList(string select)
        {
            string current = select ?? SelectedProfileName();
            List<LaunchProfile> profiles = ProfileStore.List();
            _profileBox.Items.Clear();

            int selectIndex = 0;
            foreach (LaunchProfile p in profiles)
            {
                ComboBoxItem item = new ComboBoxItem();
                int n = p.AccountIds.Count;
                item.Content = p.Name + "  (" + n.ToString(CultureInfo.InvariantCulture) + (n == 1 ? " account)" : " accounts)");
                item.Tag = p.Name;
                _profileBox.Items.Add(item);
                if (current != null && string.Equals(current, p.Name, StringComparison.OrdinalIgnoreCase))
                {
                    selectIndex = _profileBox.Items.Count - 1;
                }
            }

            if (profiles.Count == 0)
            {
                ComboBoxItem none = new ComboBoxItem();
                none.Content = "(no profiles yet)";
                none.Tag = null;
                _profileBox.Items.Add(none);
            }

            _profileBox.SelectedIndex = selectIndex;
            RefreshButtons();
        }

        private void OnNewProfileClick(object sender, RoutedEventArgs e)
        {
            EditProfile(null);
        }

        private void OnEditProfileClick(object sender, RoutedEventArgs e)
        {
            LaunchProfile p = SelectedProfile();
            if (p != null)
            {
                EditProfile(p);
            }
        }

        private void EditProfile(LaunchProfile existing)
        {
            if (_profileRunning)
            {
                return;
            }
            List<Account> accounts = AccountStore.GetAccounts();
            if (accounts.Count == 0)
            {
                _accountsExpander.IsExpanded = true;
                ShowInfo("Add at least one account first (Accounts section, Add account), then create a profile.");
                return;
            }

            ProfileDialog dialog = new ProfileDialog(this, existing, accounts);
            if (dialog.ShowDialog() != true || dialog.Result == null)
            {
                return;
            }

            string error;
            if (!ProfileStore.Save(existing == null ? null : existing.Name, dialog.Result, out error))
            {
                AppendLog("Error: " + error);
                ShowInfo(error);
                return;
            }
            AppendLog((existing == null ? "Created" : "Saved") + " profile \"" + dialog.Result.Name + "\"");
            RefreshProfileList(dialog.Result.Name);
        }

        private void OnDeleteProfileClick(object sender, RoutedEventArgs e)
        {
            string name = SelectedProfileName();
            if (_profileRunning || name == null)
            {
                return;
            }

            if (MessageBox.Show(this,
                    "Delete the profile \"" + name + "\"? Its accounts stay saved. Running instances are not affected.",
                    "Cookie Jar", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes)
            {
                return;
            }

            string error;
            if (ProfileStore.Delete(name, out error))
            {
                AppendLog("Deleted profile \"" + name + "\"");
                RefreshProfileList(null);
            }
            else
            {
                AppendLog("Error: " + error);
                ShowTemporaryError(error);
            }
        }

        // ---- profile section controls ----

        /// <summary>Running instances that belong to the profile, in the profile's account order. Missing ones are skipped.</summary>
        private List<RobloxInstance> OrderedInstancesFor(LaunchProfile profile)
        {
            List<RobloxInstance> result = new List<RobloxInstance>();
            if (profile == null || _manager == null || _manager.Instances == null || InstAccountId == null)
            {
                return result;
            }

            List<RobloxInstance> snapshot = new List<RobloxInstance>(_manager.Instances);
            foreach (string accountId in profile.AccountIds)
            {
                foreach (RobloxInstance inst in snapshot)
                {
                    string mapped;
                    if (InstAccountId.TryGetValue(inst.Id, out mapped)
                        && string.Equals(mapped, accountId, StringComparison.OrdinalIgnoreCase)
                        && !result.Contains(inst))
                    {
                        result.Add(inst);
                        break;
                    }
                }
            }
            return result;
        }

        private void OnBringFrontClick(object sender, RoutedEventArgs e)
        {
            try
            {
                List<RobloxInstance> ordered = OrderedInstancesFor(ActiveProfile);
                if (ordered.Count == 0)
                {
                    AppendLog("No running instances from this profile to bring forward.");
                    return;
                }
                BringAllToFront(ordered);
            }
            catch (Exception ex)
            {
                AppendLog("Bring to front failed: " + ex.Message);
            }
        }

        private void OnTileNowClick(object sender, RoutedEventArgs e)
        {
            try
            {
                TileAll();
            }
            catch (Exception ex)
            {
                AppendLog("Tiling failed: " + ex.Message);
            }
        }

        private void OnAutoTileChanged(object sender, RoutedEventArgs e)
        {
            if (_suppressProfileEvents || _profileRunning || _autoTileSwitch == null) return;
            LaunchProfile p = ActiveProfile;
            if (p == null) return;

            bool on = _autoTileSwitch.IsChecked == true;
            if (p.AutoTile == on) return;

            p.AutoTile = on;
            string error;
            if (!ProfileStore.Save(p.Name, p, out error))
            {
                p.AutoTile = !on;
                AppendLog("Error: " + error);
                ShowInfo(error);
                SyncProfileControls(p);
                return;
            }

            AppendLog("Auto-tile " + (on ? "on" : "off") + " for profile \"" + p.Name + "\"");
            if (on)
            {
                OnTileNowClick(this, null);
            }
        }

        /// <summary>Reflects the active profile in the section controls without firing their handlers.</summary>
        private void SyncProfileControls(LaunchProfile profile)
        {
            if (_autoTileSwitch == null) return;
            _suppressProfileEvents = true;
            try
            {
                _autoTileSwitch.IsChecked = profile != null && profile.AutoTile;
            }
            finally
            {
                _suppressProfileEvents = false;
            }
        }

        // ---- launching a profile ----

        private void OnLaunchProfileClick(object sender, RoutedEventArgs e)
        {
            if (_profileRunning || IsWorking() || _restrictor == null || !_launchProfileButton.IsEnabled)
            {
                return;
            }
            LaunchProfile profile = SelectedProfile();
            if (profile == null)
            {
                return;
            }

            List<Account> all = AccountStore.GetAccounts();
            List<Account> members = new List<Account>();
            foreach (string id in profile.AccountIds)
            {
                foreach (Account a in all)
                {
                    if (a.Id == id)
                    {
                        members.Add(a);
                    }
                }
            }
            if (members.Count == 0)
            {
                ShowInfo("This profile has no accounts. Use Edit to add some.");
                return;
            }

            StartAccountsRun(profile, members, "profile \"" + profile.Name + "\" (" + members.Count + " account(s))");
        }

        /// <summary>Account (if any) a tracked instance belongs to: the profile mapping first, then its account name.</summary>
        internal Account FindAccountFor(RobloxInstance inst)
        {
            if (inst == null) return null;
            string id;
            if (InstAccountId.TryGetValue(inst.Id, out id) && !string.IsNullOrEmpty(id))
            {
                Account byId = AccountStore.Find(id);
                if (byId != null) return byId;
            }
            if (string.IsNullOrEmpty(inst.AccountName)) return null;
            foreach (Account a in AccountStore.GetAccounts())
            {
                if (string.Equals(a.DisplayName, inst.AccountName, StringComparison.OrdinalIgnoreCase)
                    || string.Equals(a.Username, inst.AccountName, StringComparison.OrdinalIgnoreCase)) return a;
            }
            return null;
        }

        internal bool CanLaunchSingle(Account a)
        {
            return a != null && !_profileRunning && !IsWorking() && _restrictor != null && !_manager.IsAccountRunning(a.Id);
        }

        /// <summary>
        /// Launches one saved account (account list "Launch", stopped card "Relaunch") through the same runner as a
        /// profile launch: restriction check, timed warning, progress, Cancel, RAM and tiling. The active profile is used
        /// for layout/RAM when it contains the account; other running instances are left alone.
        /// </summary>
        internal void LaunchSingleAccount(Account a)
        {
            if (!CanLaunchSingle(a))
            {
                if (a != null && _manager.IsAccountRunning(a.Id)) ShowInfo(a.DisplayName + " is already running.");
                return;
            }
            LaunchProfile ap = ActiveProfile;
            LaunchProfile profile;
            if (ap != null && ap.AccountIds.Contains(a.Id)) profile = ap;
            else
            {
                profile = new LaunchProfile();
                profile.Name = ap != null ? ap.Name : "Single launch";
            }
            List<Account> members = new List<Account>();
            members.Add(a);
            StartAccountsRun(profile, members, "account " + a.DisplayName);
        }

        private void StartAccountsRun(LaunchProfile profile, List<Account> members, string what)
        {
            RestrictResult first = EvaluateRestriction();
            if (first != null && !first.CanLaunch)
            {
                AppendLog("Launch not allowed: " + first.Message);
                ShowTemporaryError(first.Message);
                return;
            }

            // The selected game always wins over the profile's stored place id.
            GameInfo game = Games.Current;
            LaunchProfile runProfile = profile.Clone();
            if (runProfile.PlaceId != game.PlaceId)
            {
                AppendLog("Using selected game: " + game.Name);
            }
            runProfile.PlaceId = game.PlaceId;

            _runCts = new CancellationTokenSource();
            CancellationTokenSource cts = _runCts;
            _profileRunning = true;
            _progressText.Foreground = Theme.Accent;
            _progressText.Text = "Starting...";
            AppendLog("Launching " + what + "...");
            RefreshAccountList();
            RefreshButtons();

            ProfileRunner runner = new ProfileRunner(
                _manager,
                delegate { return (RestrictResult)Dispatcher.Invoke(new Func<RestrictResult>(EvaluateRestriction)); },
                delegate(string message)
                {
                    return (bool)Dispatcher.Invoke(new Func<bool>(delegate
                    {
                        bool go = TimedWarnDialog.Confirm(this,
                            message + "\n\nKeep launching the rest of this profile anyway? Launching continues automatically when the countdown ends.",
                            "Keep launching", "Stop", TimedWarnDialog.DefaultSeconds);
                        AppendLog(go ? "Resource warning accepted (or timed out): " + message : "Resource warning: profile launch stopped by you.");
                        return go;
                    }));
                },
                delegate(string text)
                {
                    Dispatcher.BeginInvoke(new Action(delegate { _progressText.Text = text; }));
                });

            Thread worker = new Thread(delegate()
            {
                string summary = "Launch ended.";
                string problem = null;
                try
                {
                    summary = runner.Run(runProfile, members, cts.Token, out problem);
                }
                catch (Exception)
                {
                    summary = "Launch stopped unexpectedly.";
                }

                try
                {
                    Dispatcher.BeginInvoke(new Action(delegate { OnProfileRunFinished(summary, problem); }));
                }
                catch (Exception)
                {
                }
            });
            worker.IsBackground = true;
            worker.Name = "ProfileLaunch";
            worker.Start();
        }

        private void OnProfileRunFinished(string summary, string problem)
        {
            _profileRunning = false;
            if (_runCts != null)
            {
                _runCts.Dispose();
                _runCts = null;
            }
            _progressText.Foreground = Theme.TextMuted;
            _progressText.Text = summary;
            if (problem != null)
            {
                ShowTemporaryError(problem);
            }
            RefreshAccountList();
            RefreshButtons();
        }

        private void OnCancelRunClick(object sender, RoutedEventArgs e)
        {
            CancellationTokenSource cts = _runCts;
            if (cts != null)
            {
                try { cts.Cancel(); } catch (ObjectDisposedException) { }
                _cancelRunButton.IsEnabled = false;
                _progressText.Text = "Cancelling...";
            }
        }

        // ---- instance <-> account mapping, RAM and placement ----

        /// <summary>
        /// Runs on the UI thread every 2 s. Maps newly launched or adopted instances to members of the
        /// active profile by account name, drops ids of closed instances, and applies RAM and placement.
        /// </summary>
        private void SyncInstanceAccounts()
        {
            try
            {
                if (_manager == null || _manager.Instances == null || InstAccountId == null) return;
                List<RobloxInstance> snapshot = new List<RobloxInstance>(_manager.Instances);

                HashSet<int> live = new HashSet<int>();
                foreach (RobloxInstance inst in snapshot) { live.Add(inst.Id); }
                List<int> dead = new List<int>();
                foreach (int key in InstAccountId.Keys)
                {
                    if (!live.Contains(key)) dead.Add(key);
                }
                foreach (int key in dead) { InstAccountId.Remove(key); }

                LaunchProfile profile = ActiveProfile;
                if (profile == null || profile.AccountIds.Count == 0) return;

                List<Account> accounts = null;
                foreach (RobloxInstance inst in snapshot)
                {
                    if (InstAccountId.ContainsKey(inst.Id) || string.IsNullOrEmpty(inst.AccountName)) continue;
                    if (accounts == null) accounts = AccountStore.GetAccounts();

                    Account match = null;
                    foreach (Account a in accounts)
                    {
                        if (!profile.AccountIds.Contains(a.Id) || IsAccountMapped(a.Id)) continue;
                        if (string.Equals(a.DisplayName, inst.AccountName, StringComparison.OrdinalIgnoreCase)
                            || string.Equals(a.Username, inst.AccountName, StringComparison.OrdinalIgnoreCase))
                        {
                            match = a;
                            break;
                        }
                    }
                    if (match == null) continue;

                    InstAccountId[inst.Id] = match.Id;
                    ApplyRamAndPlacementSafe(inst);
                }
            }
            catch (Exception)
            {
                // Mapping is best effort; the next tick retries.
            }
        }

        private void OnSyncTick(object sender, EventArgs e)
        {
            SyncInstanceAccounts();
            PlacePending();
        }

        /// <summary>Places running instances that were never placed (website launches, adopted clients, windows that did not exist yet).</summary>
        private void PlacePending()
        {
            try
            {
                foreach (RobloxInstance inst in new List<RobloxInstance>(_manager.Instances))
                {
                    if (inst.Pid <= 0 || inst.StatusKind == "Stopped" || _placedIds.Contains(inst.Id)) continue;
                    bool mapped = InstAccountId.ContainsKey(inst.Id);
                    bool waitingForMap = !mapped && !string.IsNullOrEmpty(inst.AccountName) && ActiveProfile != null
                        && (DateTime.UtcNow - inst.StartedUtc).TotalSeconds < 10;
                    if (waitingForMap) continue;
                    ApplyRamAndPlacementSafe(inst);
                }
            }
            catch (Exception)
            {
            }
        }

        private bool IsAccountMapped(string accountId)
        {
            foreach (KeyValuePair<int, string> kv in InstAccountId)
            {
                if (string.Equals(kv.Value, accountId, StringComparison.OrdinalIgnoreCase)) return true;
            }
            return false;
        }

        private void ApplyRamAndPlacementSafe(RobloxInstance inst)
        {
            try
            {
                ApplyRamAndPlacement(inst);
            }
            catch (Exception ex)
            {
                AppendLog("Could not apply RAM or window placement: " + ex.Message);
            }
        }

        private void ApplyRamToAccount(string accountId)
        {
            if (_manager == null || _manager.Instances == null || InstAccountId == null) return;
            List<RobloxInstance> snapshot = new List<RobloxInstance>(_manager.Instances);
            foreach (RobloxInstance inst in snapshot)
            {
                string mapped;
                if (InstAccountId.TryGetValue(inst.Id, out mapped)
                    && string.Equals(mapped, accountId, StringComparison.OrdinalIgnoreCase))
                {
                    _ram.Remove(inst.Id);   // drop the cached setting so the profile value is re-read
                    RamSetting s = GetRam(inst);
                    ApplyRamToInstance(inst, s != null ? s : new RamSetting { Preset = MemoryPreset.Unlimited });
                    UpdateRamSummary();
                }
            }
        }

        private void OnRamPresetChanged(Account account, ComboBox box)
        {
            if (_profileRunning || box == null) return;
            string picked = box.SelectedItem as string;
            LaunchProfile p = ActiveProfile;
            if (picked == null || p == null) return;
            string now = p.HasRamPreset(account.Id) ? p.GetRamPreset(account.Id) : RamNone;
            if (string.Equals(now, picked, StringComparison.OrdinalIgnoreCase)) return;

            if (picked == RamNone) p.ClearRamPreset(account.Id);
            else p.SetRamPreset(account.Id, picked);
            string error;
            if (!ProfileStore.SaveLayouts(p.Name, p, out error))
            {
                AppendLog("Error: " + error);
                ShowInfo(error);
                RefreshAccountList();
                return;
            }

            AppendLog("RAM for " + account.DisplayName + " in \"" + p.Name + "\": " + picked);
            ApplyRamToAccount(account.Id);
        }

        // ------------------------------------------------------------------
        // Accounts
        // ------------------------------------------------------------------

        /// <summary>Rebuilds the account rows from the store (tokens are never shown).</summary>
        private void RefreshAccountList()
        {
            List<Account> accounts = AccountStore.GetAccounts();
            _accountCount = accounts.Count;
            _accountsHeader.Text = "Accounts (" + accounts.Count.ToString(CultureInfo.InvariantCulture) + ")";
            _accountsEmpty.Visibility = accounts.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
            _accountList.Children.Clear();

            foreach (Account account in accounts)
            {
                Account a = account;
                TextBlock name = UiUtil.Label(a.DisplayName, 14, true, Theme.Text);
                string detail = (string.IsNullOrEmpty(a.Label) || a.Label == a.Username) ? "" : a.Username + "  Â·  ";
                Brush statusBrush = Theme.TextMuted;
                string statusText = a.Status;
                if (a.Status == "Valid") { statusBrush = Theme.Success; }
                else if (a.Status == "Expired") { statusBrush = Theme.Danger; statusText = "Expired - press Re-validate or add the account again"; }
                else if (a.Status == "Error") { statusBrush = Theme.Warning; statusText = "Could not check (network?)"; }
                TextBlock status = UiUtil.Label(detail + statusText, 12, false, statusBrush);
                status.TextWrapping = TextWrapping.Wrap;

                StackPanel info = new StackPanel();
                info.VerticalAlignment = VerticalAlignment.Center;
                info.Children.Add(name);
                info.Children.Add(status);

                WrapPanel buttons = new WrapPanel();
                buttons.MaxWidth = 360;
                buttons.HorizontalAlignment = HorizontalAlignment.Right;
                buttons.VerticalAlignment = VerticalAlignment.Center;

                LaunchProfile ap = ActiveProfile;
                if (ap != null && ap.AccountIds.Contains(a.Id))
                {
                    string current = ap.HasRamPreset(a.Id) ? ap.GetRamPreset(a.Id) : RamNone;
                    List<string> choices = new List<string>();
                    choices.Add(RamNone);
                    choices.AddRange(RamPresetChoices);
                    if (!choices.Contains(current)) choices.Add(current);

                    ComboBox ram = new ComboBox();
                    ram.ItemsSource = choices;
                    ram.MinWidth = 110;
                    ram.Height = 30;
                    ram.VerticalContentAlignment = VerticalAlignment.Center;
                    ram.VerticalAlignment = VerticalAlignment.Center;
                    ram.Margin = new Thickness(0, 0, 8, 6);
                    ram.TabIndex = 30;
                    ram.IsEnabled = !_profileRunning;
                    ram.ToolTip = "RAM limit for this account in the active profile (working-set trim). Low about 1.2 GB, Medium 2 GB, High 3.5 GB, scaled down when many instances run.";
                    AutomationProperties.SetName(ram, "RAM preset for " + a.DisplayName);
                    ThemedCombo.Apply(ram);
                    ram.SelectedItem = current;
                    ram.SelectionChanged += delegate { OnRamPresetChanged(a, ram); };
                    buttons.Children.Add(ram);
                }

                Button check = PillButton.Create("Re-validate", PillStyle.Secondary);
                check.MinWidth = 0;
                check.Padding = new Thickness(12, 5, 12, 5);
                check.Margin = new Thickness(0, 0, 8, 6);
                check.ToolTip = "Ask Roblox whether this saved login still works.";
                check.IsEnabled = !_profileRunning && a.Status != "Checking...";
                check.Click += delegate { ValidateAccount(a.Id); };
                AutomationProperties.SetName(check, "Re-validate " + a.DisplayName);

                Button remove = PillButton.Create("Remove", PillStyle.Secondary);
                remove.MinWidth = 0;
                remove.Padding = new Thickness(12, 5, 12, 5);
                remove.Margin = new Thickness(0, 0, 0, 6);
                remove.ToolTip = "Delete this account's saved login from this PC.";
                remove.IsEnabled = !_profileRunning;
                remove.Click += delegate { RemoveAccount(a); };
                AutomationProperties.SetName(remove, "Remove " + a.DisplayName);

                Button launch = PillButton.Create("Launch", PillStyle.Primary);
                launch.MinWidth = 0;
                launch.Padding = new Thickness(12, 5, 12, 5);
                launch.Margin = new Thickness(0, 0, 8, 6);
                bool running = _manager.IsAccountRunning(a.Id);
                launch.ToolTip = running ? "This account is already running." : "Start just this account (other instances keep running).";
                launch.IsEnabled = !running && !_profileRunning;
                launch.Click += delegate { LaunchSingleAccount(a); };
                AutomationProperties.SetName(launch, "Launch " + a.DisplayName);

                buttons.Children.Add(launch);
                buttons.Children.Add(check);
                buttons.Children.Add(remove);

                Grid g = new Grid();
                g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                Grid.SetColumn(info, 0);
                Grid.SetColumn(buttons, 1);
                g.Children.Add(info);
                g.Children.Add(buttons);

                Border row = new Border();
                row.Background = Theme.SurfaceAlt;
                row.CornerRadius = new CornerRadius(9);
                row.Padding = new Thickness(12, 8, 12, 8);
                row.Margin = new Thickness(0, 0, 4, 8);
                row.Child = g;
                Fx.HoverRow(row);
                _accountList.Children.Add(row);
            }
        }

        private void OnAddAccountClick(object sender, RoutedEventArgs e)
        {
            if (_profileRunning)
            {
                return;
            }
            AddAccountDialog dialog = new AddAccountDialog(this);
            if (dialog.ShowDialog() != true || dialog.Result == null)
            {
                return;
            }

            bool updated;
            string error;
            Account added = dialog.Result;
            string name = added.Username;
            bool ok = AccountStore.AddOrUpdate(added, out updated, out error);
            added.Token = null;
            if (!ok)
            {
                AppendLog("Error: " + error);
                ShowInfo(error);
                return;
            }
            AppendLog((updated ? "Updated the saved login of account " : "Added account ") + name);
            RefreshAccountList();
            RefreshProfileList(null);
        }

        private void RemoveAccount(Account account)
        {
            if (_profileRunning)
            {
                return;
            }
            if (MessageBox.Show(this,
                    "Remove the account \"" + account.DisplayName + "\"? Its saved login is deleted from this PC and it is taken out of every profile. "
                    + "Running instances are not affected.",
                    "Cookie Jar", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes)
            {
                return;
            }
            string error;
            if (AccountStore.Remove(account.Id, out error))
            {
                AppendLog("Removed account " + account.DisplayName);
                RefreshAccountList();
                RefreshProfileList(null);
            }
            else
            {
                AppendLog("Error: " + error);
                ShowInfo(error);
            }
        }

        private void ValidateAccount(string id)
        {
            Account stored = AccountStore.Find(id);
            if (stored == null)
            {
                return;
            }
            string token = stored.Token;
            string display = stored.DisplayName;
            AccountStore.SetStatus(id, "Checking...", null);
            RefreshAccountList();

            ThreadPool.QueueUserWorkItem(delegate(object state)
            {
                string status, name, err;
                long userId;
                TicketLauncher.Validate(token, out status, out userId, out name, out err);
                Dispatcher.BeginInvoke(new Action(delegate
                {
                    AccountStore.SetStatus(id, status, status == "Valid" ? name : null);
                    AppendLog("Account " + display + ": " + (status == "Valid" ? "valid"
                        : (status == "Expired" ? "expired - the login no longer works" : "could not check (" + err + ")")));
                    RefreshAccountList();
                }));
            });
        }

        // ------------------------------------------------------------------
        // Button state
        // ------------------------------------------------------------------

        /// <summary>Enables or disables buttons and sets hints. Tooltips explain why a control is unavailable.</summary>
        private void RefreshButtons()
        {
            if (_launchButton == null || _profileBox == null || _launchProfileButton == null || _newProfileButton == null)
            {
                return;
            }

            RestrictResult result = EvaluateRestriction();
            bool allowed = result != null && result.CanLaunch;
            int running = _manager.RunningCount;
            bool working = IsWorking();
            bool busy = working || _profileRunning;
            LaunchProfile profile = SelectedProfile();
            int members = profile == null ? 0 : profile.AccountIds.Count;
            bool hasProfiles = profile != null;

            // The active profile drives the section controls, RAM presets and instance mapping.
            ActiveProfile = profile;
            SyncProfileControls(profile);
            string activeName = profile == null ? null : profile.Name;
            if (!string.Equals(activeName, _lastActiveName, StringComparison.Ordinal))
            {
                _lastActiveName = activeName;
                if (_accountList != null && _accountsExpander != null && _accountsEmpty != null) { RefreshAccountList(); }
            }

            _launchButton.IsEnabled = allowed && !busy;
            if (result == null)
            {
                _launchButton.ToolTip = "Hardware is still being detected.";
            }
            else if (!allowed)
            {
                _launchButton.ToolTip = result.Message;
            }
            else
            {
                _launchButton.ToolTip = "Open the Adopt Me page in your default browser (Ctrl+N). Log in there and press Play; the cache is cleared first.";
            }

            _launchProfileButton.IsEnabled = allowed && !busy && hasProfiles && members > 0;
            if (_profileRunning)
            {
                _launchProfileButton.Content = "Launching this session...";
                _launchProfileButton.ToolTip = "A profile is launching. Use Cancel to stop.";
            }
            else
            {
                _launchProfileButton.Content = !hasProfiles
                    ? "Launch pro_file"
                    : "Launch pro_file (" + members.ToString(CultureInfo.InvariantCulture) + (members == 1 ? " account)" : " accounts)");
                if (!hasProfiles) { _launchProfileButton.ToolTip = "Create a profile first."; }
                else if (members == 0) { _launchProfileButton.ToolTip = "This profile has no accounts. Use Edit."; }
                else if (result == null) { _launchProfileButton.ToolTip = "Hardware is still being detected."; }
                else if (!allowed) { _launchProfileButton.ToolTip = result.Message; }
                else { _launchProfileButton.ToolTip = "Open every account in this profile, one after another, straight into the game."; }
            }

            _cancelRunButton.Visibility = _profileRunning ? Visibility.Visible : Visibility.Collapsed;
            if (_profileRunning && _progressText.Text != "Cancelling...")
            {
                _cancelRunButton.IsEnabled = true;
            }

            _stopAllButton.IsEnabled = running > 0 && !working;
            _adoptButton.IsEnabled = !busy;
            _newProfileButton.IsEnabled = !busy;
            _editProfileButton.IsEnabled = !busy && hasProfiles;
            _deleteProfileButton.IsEnabled = !busy && hasProfiles;
            _profileBox.IsEnabled = !busy;
            _addAccountButton.IsEnabled = !_profileRunning;

            if (_gameBox != null) { _gameBox.IsEnabled = !busy; }
            if (_bringFrontButton != null) { _bringFrontButton.IsEnabled = !busy && hasProfiles; }
            if (_tileNowButton != null) { _tileNowButton.IsEnabled = !busy && hasProfiles; }
            if (_autoTileSwitch != null) { _autoTileSwitch.IsEnabled = !busy && hasProfiles; }

            if (!hasProfiles)
            {
                _profileHint.Text = _accountCount == 0
                    ? "Add an account to get started. Open Accounts below and press Add account, then press New to group accounts into a profile."
                    : "No profiles yet. Press New to group your accounts into a profile.";
                _profileHint.Visibility = Visibility.Visible;
            }
            else
            {
                _profileHint.Visibility = Visibility.Collapsed;
            }

            _stopAllButton.ToolTip = running > 0
                ? "Ends every running Roblox client and all child processes immediately, like Process Explorer > Kill Process Tree. Asks once first."
                : "No instances are running.";
        }
    }
}
