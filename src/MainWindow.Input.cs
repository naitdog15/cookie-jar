// MainWindow.Input.cs
//
// Advanced-panel UI and glue for the shared input layer (partial class MainWindow, C# 5):
//   * Click method: Auto / SendInput / Legacy (InputSim.Method), persisted in minibar.settings (clickmethod=).
//   * Keep sessions from timing out (KeepAliveService): checkbox (default ON, only active while the auto-click
//     master switch is on), interval 1-15 minutes (default 4), optional Right Ctrl tap. Persisted (keepsessions=, keepmin=, keepkey=).
//   * Diagnostics: instance dropdown + "Diagnose input" button -> InputDiagnostics.Run on a worker thread, result dialog.

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Threading;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;

namespace RbxMulti
{
    public partial class MainWindow
    {
        internal KeepAliveService KeepAlive;

        private bool _ksOn = true;
        private int _ksMinutes = KeepAliveService.DefaultIntervalMinutes;
        private bool _ksKey;
        private CheckBox _ksCheck;
        private CheckBox _ksKeyCheck;
        private CheckBox _pauseHospitalCheck;
        private bool _pauseHospital;
        private CheckBox _declineTradesCheck;
        private CheckBox _declineInvitesCheck;
        private TextBox _ksMinBox;
        private ComboBox _clickCombo;
        private ComboBox _diagCombo;
        private Button _diagButton;
        private TextBlock _diagStatus;
        private readonly List<RobloxInstance> _diagItems = new List<RobloxInstance>();
        private string _diagSignature = "";
        private bool _diagRunning;
        private bool _inputSync;
        private int _listWaitSec = TeleportService.DefaultListWaitSeconds;
        private TextBox _listWaitBox;

        /// <summary>True when keep-alive inputs are being sent: master auto-click switch on and the keep-alive option on.</summary>
        internal bool KeepAliveActive
        {
            get { return MasterOn && _ksOn; }
        }

        private void InitInput()
        {
            KeepAlive = new KeepAliveService();
            KeepAlive.Log += AppendLogSafe;
            ApplyKeepAliveConfig();
            KeepAlive.Start();
        }

        private void DisposeInput()
        {
            if (KeepAlive != null)
            {
                KeepAlive.Log -= AppendLogSafe;
                try { KeepAlive.Dispose(); } catch { }
                KeepAlive = null;
            }
        }

        // ------------------------------------------------------------------
        // Settings persistence hooks (called from Load/SaveMiniBarSettings)
        // ------------------------------------------------------------------

        private void LoadInputSetting(string key, string value)
        {
            int n;
            if (key == "keepsessions")
            {
                _ksOn = !string.Equals(value, "false", StringComparison.OrdinalIgnoreCase);
            }
            else if (key == "keepmin")
            {
                if (int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out n))
                {
                    _ksMinutes = Math.Max(KeepAliveService.MinIntervalMinutes, Math.Min(KeepAliveService.MaxIntervalMinutes, n));
                }
            }
            else if (key == "keepkey")
            {
                _ksKey = string.Equals(value, "true", StringComparison.OrdinalIgnoreCase);
            }
            else if (key == "listwait")
            {
                if (int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out n))
                {
                    _listWaitSec = TeleportService.ClampListWaitSeconds(n);
                }
            }
            else if (key == "pausehospital")
            {
                _pauseHospital = AntiIdleClicker.ParsePauseWhileHospital(value);
                AntiIdleClicker.PauseWhileHospital = _pauseHospital;
            }
            else if (key == "declinetrades")
            {
                AntiIdleClicker.DeclineTrades = AntiIdleClicker.ParseSwitch(value, true);
                SyncDeclineChecks();
            }
            else if (key == "declineinvites")
            {
                AntiIdleClicker.DeclineInvites = AntiIdleClicker.ParseSwitch(value, true);
                SyncDeclineChecks();
            }
            else if (key == "clickmethod")
            {
                if (string.Equals(value, "SendInput", StringComparison.OrdinalIgnoreCase)) { InputSim.Method = ClickMethod.SendInput; }
                else if (string.Equals(value, "Legacy", StringComparison.OrdinalIgnoreCase)) { InputSim.Method = ClickMethod.Legacy; }
                else { InputSim.Method = ClickMethod.Auto; }
            }
        }

        private string[] InputSettingLines()
        {
            return new string[]
            {
                "keepsessions=" + (_ksOn ? "true" : "false"),
                "keepmin=" + _ksMinutes.ToString(CultureInfo.InvariantCulture),
                "keepkey=" + (_ksKey ? "true" : "false"),
                "pausehospital=" + AntiIdleClicker.PauseWhileHospitalSetting(_pauseHospital),
                "declinetrades=" + AntiIdleClicker.SwitchSetting(AntiIdleClicker.DeclineTrades),
                "declineinvites=" + AntiIdleClicker.SwitchSetting(AntiIdleClicker.DeclineInvites),
                "clickmethod="+ InputSim.MethodLabel(InputSim.Method),
                "listwait=" + _listWaitSec.ToString(CultureInfo.InvariantCulture)
            };
        }

        // ------------------------------------------------------------------
        // UI
        // ------------------------------------------------------------------

        private UIElement BuildInputSettings()
        {
            StackPanel body = new StackPanel();

            body.Children.Add(Spaced(Track(UiUtil.Label("Click method", 14, true, Theme.Text), false), 14));
            StackPanel methodRow = new StackPanel();
            methodRow.Orientation = Orientation.Horizontal;
            methodRow.Margin = new Thickness(0, 6, 0, 0);
            TextBlock methodLabel = Track(UiUtil.Label("How clicks are sent", 13, false, Theme.Text), false);
            methodLabel.VerticalAlignment = VerticalAlignment.Center;
            _clickCombo = new ComboBox();
            _clickCombo.Items.Add("Auto");
            _clickCombo.Items.Add("SendInput");
            _clickCombo.Items.Add("Legacy");
            _clickCombo.SelectedItem = InputSim.MethodLabel(InputSim.Method);
            _clickCombo.Width = 150;
            _clickCombo.Height = 30;
            _clickCombo.Margin = new Thickness(10, 0, 0, 0);
            _clickCombo.VerticalContentAlignment = VerticalAlignment.Center;
            _clickCombo.ToolTip = "Auto: SendInput first, then the legacy mouse_event, then window messages when a click shows no effect. SendInput: only SendInput. Legacy: mouse_event, then window messages.";
            AutomationProperties.SetName(_clickCombo, "Click method");
            ThemedCombo.Apply(_clickCombo);
            _clickCombo.SelectionChanged += OnClickMethodChanged;
            methodRow.Children.Add(methodLabel);
            methodRow.Children.Add(_clickCombo);
            body.Children.Add(methodRow);
            TextBlock methodHint = Track(UiUtil.Label("Every click uses real mouse movement, a hover pause and a held press. Auto switches to the next method when the screen shows no change. Use Legacy if SendInput does nothing.", 12, false, Theme.TextMuted), true);
            methodHint.TextWrapping = TextWrapping.Wrap;
            methodHint.Margin = new Thickness(0, 4, 0, 0);
            body.Children.Add(methodHint);

            StackPanel waitRow = new StackPanel();
            waitRow.Orientation = Orientation.Horizontal;
            waitRow.Margin = new Thickness(0, 10, 0, 0);
            TextBlock waitLabel = Track(UiUtil.Label("Friends list wait (s, " + TeleportService.MinListWaitSeconds + "-" + TeleportService.MaxListWaitSeconds + ")", 13, false, Theme.Text), false);
            waitLabel.VerticalAlignment = VerticalAlignment.Center;
            _listWaitBox = MakeField(_listWaitSec.ToString(CultureInfo.InvariantCulture), 80, "Friends list wait in seconds");
            _listWaitBox.Margin = new Thickness(10, 0, 0, 0);
            _listWaitBox.ToolTip = "Teleport: how long to wait for the FRIENDS list to finish loading (Roblox loads it slowly when many clients run). Default 25 s.";
            _listWaitBox.LostFocus += delegate { CommitListWait(); };
            _listWaitBox.KeyDown += delegate(object s, System.Windows.Input.KeyEventArgs ke) { if (ke.Key == System.Windows.Input.Key.Enter) { CommitListWait(); } };
            waitRow.Children.Add(waitLabel);
            waitRow.Children.Add(_listWaitBox);
            body.Children.Add(waitRow);

            _pauseHospitalCheck = MakeCheck("Pause auto-click while the hospital route runs (off by default)");
            _pauseHospitalCheck.IsChecked = _pauseHospital;
            _pauseHospitalCheck.Margin = new Thickness(0, 10, 0, 0);
            _pauseHospitalCheck.Checked += OnPauseHospitalChanged;
            _pauseHospitalCheck.Unchecked += OnPauseHospitalChanged;
            _pauseHospitalCheck.ToolTip = "Off by default. When on, the CASH OUT auto-click waits while the hospital route runs and carries on afterwards.";
            body.Children.Add(_pauseHospitalCheck);

            body.Children.Add(Spaced(Track(UiUtil.Label("Keep sessions from timing out", 14, true, Theme.Text), false), 14));
            _ksCheck = MakeCheck("Keep sessions from timing out (needs the auto-click master switch)");
            _ksCheck.IsChecked = _ksOn;
            _ksCheck.Checked += OnKeepSessionsChanged;
            _ksCheck.Unchecked += OnKeepSessionsChanged;
            _ksCheck.ToolTip = "Roblox disconnects a client after 20 minutes without input. This briefly focuses each instance in turn and sends a small real mouse move.";
            body.Children.Add(_ksCheck);

            StackPanel minRow = new StackPanel();
            minRow.Orientation = Orientation.Horizontal;
            minRow.Margin = new Thickness(0, 8, 0, 0);
            TextBlock minLabel = Track(UiUtil.Label("Every (minutes, " + KeepAliveService.MinIntervalMinutes + "-" + KeepAliveService.MaxIntervalMinutes + ")", 13, false, Theme.Text), false);
            minLabel.VerticalAlignment = VerticalAlignment.Center;
            _ksMinBox = MakeField(_ksMinutes.ToString(CultureInfo.InvariantCulture), 80, "Keep-alive interval in minutes");
            _ksMinBox.Margin = new Thickness(10, 0, 0, 0);
            _ksMinBox.LostFocus += delegate { CommitKeepMinutes(); };
            _ksMinBox.KeyDown += delegate(object s, System.Windows.Input.KeyEventArgs ke) { if (ke.Key == System.Windows.Input.Key.Enter) { CommitKeepMinutes(); } };
            minRow.Children.Add(minLabel);
            minRow.Children.Add(_ksMinBox);
            body.Children.Add(minRow);

            _ksKeyCheck = MakeCheck("Also tap the Right Ctrl key (off by default)");
            _ksKeyCheck.IsChecked = _ksKey;
            _ksKeyCheck.Checked += OnKeepSessionsChanged;
            _ksKeyCheck.Unchecked += OnKeepSessionsChanged;
            _ksKeyCheck.ToolTip = "A harmless key press as extra input. Shift is not used because Roblox turns it into Shift Lock.";
            body.Children.Add(_ksKeyCheck);

            TextBlock ksHint = Track(UiUtil.Label("Works independently of the CASH OUT switch of each card. Instances are handled one at a time; nothing is sent while you are using the PC, and the cursor and the window you were in are restored.", 12, false, Theme.TextMuted), true);
            ksHint.TextWrapping = TextWrapping.Wrap;
            ksHint.Margin = new Thickness(0, 4, 0, 0);
            body.Children.Add(ksHint);

            body.Children.Add(Spaced(Track(UiUtil.Label("Diagnostics", 14, true, Theme.Text), false), 14));
            TextBlock diagHint = Track(UiUtil.Label("If clicks do nothing in Roblox, run this on a running instance and send the folder it opens. It tries one click on the FRIENDS button with each method, reads the friends panel when it opens (names and buttons, Teleport is NOT clicked), closes it again and saves a report and screenshots in a new folder.", 12, false, Theme.TextMuted), true);
            diagHint.TextWrapping = TextWrapping.Wrap;
            diagHint.Margin = new Thickness(0, 4, 0, 0);
            body.Children.Add(diagHint);

            StackPanel diagRow = new StackPanel();
            diagRow.Orientation = Orientation.Horizontal;
            diagRow.Margin = new Thickness(0, 8, 0, 0);
            _diagCombo = new ComboBox();
            _diagCombo.Width = 230;
            _diagCombo.Height = 30;
            _diagCombo.VerticalContentAlignment = VerticalAlignment.Center;
            _diagCombo.ToolTip = "Instance to test (running instances only).";
            AutomationProperties.SetName(_diagCombo, "Instance to diagnose");
            ThemedCombo.Apply(_diagCombo);
            _diagButton = PillButton.Create("_Diagnose input", PillStyle.Secondary);
            _diagButton.Margin = new Thickness(10, 0, 0, 0);
            _diagButton.ToolTip = "Moves the mouse and clicks the FRIENDS button of the chosen instance once per click method. Do not touch the mouse while it runs (about 30-60 s). A folder with report.txt is created every time you press it.";
            AutomationProperties.SetName(_diagButton, "Diagnose input");
            _diagButton.Click += delegate { StartDiagnostics(); };
            diagRow.Children.Add(_diagCombo);
            diagRow.Children.Add(_diagButton);
            body.Children.Add(diagRow);

            _diagStatus = Track(UiUtil.Label(" ", 12, false, Theme.TextMuted), true);
            _diagStatus.TextWrapping = TextWrapping.Wrap;
            _diagStatus.Margin = new Thickness(0, 6, 0, 0);
            body.Children.Add(_diagStatus);

            RefreshDiagItems();
            return body;
        }

        // Advanced panel, directly under the auto-click switches: "Decline invites" and "Decline trade requests" (both default on).
        private UIElement BuildDeclineSettings()
        {
            StackPanel panel = new StackPanel();

            _declineInvitesCheck = MakeCheck("Decline invites");
            _declineInvitesCheck.IsChecked = AntiIdleClicker.DeclineInvites;
            _declineInvitesCheck.Margin = new Thickness(0, 6, 0, 0);
            _declineInvitesCheck.Checked += OnDeclineChanged;
            _declineInvitesCheck.Unchecked += OnDeclineChanged;
            _declineInvitesCheck.ToolTip = "On by default. The auto-click dismisses incoming Roblox friend and game invites so they do not sit on screen.";
            panel.Children.Add(_declineInvitesCheck);

            _declineTradesCheck = MakeCheck("Decline trade requests");
            _declineTradesCheck.IsChecked = AntiIdleClicker.DeclineTrades;
            _declineTradesCheck.Margin = new Thickness(0, 4, 0, 0);
            _declineTradesCheck.Checked += OnDeclineChanged;
            _declineTradesCheck.Unchecked += OnDeclineChanged;
            _declineTradesCheck.ToolTip = "On by default. The auto-click declines incoming Roblox trade requests.";
            panel.Children.Add(_declineTradesCheck);

            return panel;
        }

        private void SyncDeclineChecks()
        {
            if (_declineTradesCheck == null || _declineInvitesCheck == null) { return; }
            bool prev = _inputSync;
            _inputSync = true;
            try
            {
                _declineTradesCheck.IsChecked = AntiIdleClicker.DeclineTrades;
                _declineInvitesCheck.IsChecked = AntiIdleClicker.DeclineInvites;
            }
            finally { _inputSync = prev; }
        }

        private void OnDeclineChanged(object sender, RoutedEventArgs e)
        {
            if (_inputSync || _declineTradesCheck == null || _declineInvitesCheck == null) { return; }
            AntiIdleClicker.DeclineTrades = _declineTradesCheck.IsChecked == true;
            AntiIdleClicker.DeclineInvites = _declineInvitesCheck.IsChecked == true;
            AppendLog("Decline invites: " + (AntiIdleClicker.DeclineInvites ? "on" : "off") + ". Decline trade requests: " + (AntiIdleClicker.DeclineTrades ? "on" : "off") + ".");
            SaveMiniBarSettings();
        }

        private void ApplyInputTheme()
        {
            if (_declineTradesCheck != null) { _declineTradesCheck.Foreground = Theme.Text; }
            if (_declineInvitesCheck != null) { _declineInvitesCheck.Foreground = Theme.Text; }
            if (_ksCheck != null) { _ksCheck.Foreground = Theme.Text; }
            if (_ksKeyCheck != null) { _ksKeyCheck.Foreground = Theme.Text; }
            if (_pauseHospitalCheck != null) { _pauseHospitalCheck.Foreground = Theme.Text; }
            if (_listWaitBox != null) { _listWaitBox.Foreground = Theme.Text; _listWaitBox.Background = Theme.SurfaceAlt; _listWaitBox.BorderBrush = Theme.Border; }
            if (_ksMinBox != null) { _ksMinBox.Foreground = Theme.Text; _ksMinBox.Background = Theme.SurfaceAlt; _ksMinBox.BorderBrush = Theme.Border; }
            if (_clickCombo != null) { ThemedCombo.Apply(_clickCombo); }
            if (_diagCombo != null) { ThemedCombo.Apply(_diagCombo); }
        }

        // ------------------------------------------------------------------
        // Handlers
        // ------------------------------------------------------------------

        private void OnClickMethodChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_inputSync || _clickCombo == null || _clickCombo.SelectedItem == null) { return; }
            string s = _clickCombo.SelectedItem.ToString();
            ClickMethod m = ClickMethod.Auto;
            if (s == "SendInput") { m = ClickMethod.SendInput; }
            else if (s == "Legacy") { m = ClickMethod.Legacy; }
            InputSim.Method = m;
            AppendLog("Click method: " + s + ".");
            SaveMiniBarSettings();
        }

        private void OnPauseHospitalChanged(object sender, RoutedEventArgs e)
        {
            if (_inputSync || _pauseHospitalCheck == null) { return; }
            _pauseHospital = _pauseHospitalCheck.IsChecked == true;
            AntiIdleClicker.PauseWhileHospital = _pauseHospital;
            AppendLog("Pause auto-click while the hospital route runs: " + (_pauseHospital ? "on." : "off."));
            SaveMiniBarSettings();
        }

        private void OnKeepSessionsChanged(object sender, RoutedEventArgs e)
        {
            if (_inputSync) { return; }
            _ksOn = _ksCheck.IsChecked == true;
            _ksKey = _ksKeyCheck.IsChecked == true;
            ApplyKeepAliveConfig();
            SaveMiniBarSettings();
        }

        private void CommitListWait()
        {
            int v;
            if (!ParseInt(_listWaitBox.Text, out v)) { v = TeleportService.DefaultListWaitSeconds; }
            v = TeleportService.ClampListWaitSeconds(v);
            _listWaitBox.Text = v.ToString(CultureInfo.InvariantCulture);
            _listWaitSec = v;
            if (Teleporter != null) { Teleporter.ListWaitMs = v * 1000; }
            SaveMiniBarSettings();
        }

        private void CommitKeepMinutes()
        {
            int v;
            if (!ParseInt(_ksMinBox.Text, out v)) { v = KeepAliveService.DefaultIntervalMinutes; }
            v = Math.Max(KeepAliveService.MinIntervalMinutes, Math.Min(KeepAliveService.MaxIntervalMinutes, v));
            _ksMinBox.Text = v.ToString(CultureInfo.InvariantCulture);
            _ksMinutes = v;
            ApplyKeepAliveConfig();
            SaveMiniBarSettings();
        }

        /// <summary>Pushes the keep-alive switches to the service (cheap; also called every tick so the master switch is followed).</summary>
        private void ApplyKeepAliveConfig()
        {
            if (Teleporter != null) { Teleporter.ListWaitMs = _listWaitSec * 1000; }
            if (KeepAlive == null) { return; }
            KeepAlive.Enabled = KeepAliveActive;
            KeepAlive.IntervalMinutes = _ksMinutes;
            KeepAlive.TapKey = _ksKey;
        }

        /// <summary>Tick: sync switches and the list of running instances to the keep-alive service, refresh the diagnostics dropdown.</summary>
        private void TickInput()
        {
            if (KeepAlive == null) { return; }
            ApplyKeepAliveConfig();
            List<KeepAliveTarget> targets = new List<KeepAliveTarget>();
            foreach (RobloxInstance inst in _manager.Instances)
            {
                if (inst.Pid <= 0 || inst.StatusKind != "Running") { continue; }
                KeepAliveTarget t = new KeepAliveTarget();
                t.Id = inst.Id;
                t.Pid = inst.Pid;
                t.Label = string.IsNullOrEmpty(inst.AccountName) ? inst.Name : inst.AccountName;
                targets.Add(t);
            }
            KeepAlive.SetTargets(targets);
            RefreshDiagItems();
        }

        // ------------------------------------------------------------------
        // Diagnostics
        // ------------------------------------------------------------------

        private static string DiagLabel(RobloxInstance inst)
        {
            string who = string.IsNullOrEmpty(inst.AccountName) ? inst.Name : inst.AccountName;
            return "#" + inst.Id.ToString(CultureInfo.InvariantCulture) + "  " + who + "  (PID " + inst.Pid.ToString(CultureInfo.InvariantCulture) + ")";
        }

        private void RefreshDiagItems()
        {
            if (_diagCombo == null || _diagCombo.IsDropDownOpen) { return; }
            List<RobloxInstance> running = new List<RobloxInstance>();
            StringBuilder sig = new StringBuilder();
            foreach (RobloxInstance inst in _manager.Instances)
            {
                if (inst.Pid > 0 && inst.StatusKind == "Running")
                {
                    running.Add(inst);
                    sig.Append(DiagLabel(inst)).Append('|');
                }
            }
            if (sig.ToString() == _diagSignature) { return; }
            _diagSignature = sig.ToString();
            RobloxInstance keep = _diagCombo.SelectedIndex >= 0 && _diagCombo.SelectedIndex < _diagItems.Count ? _diagItems[_diagCombo.SelectedIndex] : null;
            _diagItems.Clear();
            _diagCombo.Items.Clear();
            foreach (RobloxInstance inst in running)
            {
                _diagItems.Add(inst);
                _diagCombo.Items.Add(DiagLabel(inst));
            }
            if (_diagItems.Count > 0)
            {
                int idx = keep != null ? _diagItems.IndexOf(keep) : -1;
                _diagCombo.SelectedIndex = idx >= 0 ? idx : 0;
            }
            if (_diagButton != null) { _diagButton.IsEnabled = _diagItems.Count > 0 && !_diagRunning; }
        }

        // Every press of "Diagnose input" leaves a diag\<timestamp>\ folder with a report.txt behind, also when the run is
        // refused (no instance, teleport running) or crashes, and always ends in a dialog that shows the folder path.
        private void ShowDiagRefusal(string label, string folder, string reason)
        {
            InputDiagnostics.WriteStatus(folder, label, "NOT RUN: " + reason);
            _diagStatus.Text = reason + (folder != null ? " Report: " + folder : "");
            AppendLog("Diagnose input: not run (" + reason + ")" + (folder != null ? " Folder: " + folder : ""));
            DiagResult r = new DiagResult();
            r.Folder = folder ?? "(the diagnostics folder could not be created)";
            r.Summary = "- Not run: " + reason + Environment.NewLine;
            DiagResultDialog d = new DiagResultDialog(this, r);
            TrackDialog(d);
            d.ShowDialog();
        }

        private void StartDiagnostics()
        {
            string label = "?";
            string folder = null;
            try
            {
                if (_diagRunning) { return; }
                folder = InputDiagnostics.Begin(label, null);
                AppendLog("Diagnose input: button pressed." + (folder != null ? " Folder: " + folder : ""));
                int idx = _diagCombo.SelectedIndex;
                if (idx < 0 || idx >= _diagItems.Count)
                {
                    ShowDiagRefusal(label, folder, "No running instance is selected. Start an instance, wait for the green Running status and pick it in the list.");
                    return;
                }
                if (Teleporter != null && Teleporter.IsBusy)
                {
                    ShowDiagRefusal(label, folder, "A teleport is running. Wait for it to finish.");
                    return;
                }
                RobloxInstance inst = _diagItems[idx];
                label = "#" + inst.Id.ToString(CultureInfo.InvariantCulture) + " (PID " + inst.Pid.ToString(CultureInfo.InvariantCulture) + ")";
                _diagRunning = true;
                _diagButton.IsEnabled = false;
                _diagStatus.Text = "Running diagnostics on " + label + ". Do not touch the mouse for the next 30-60 seconds... Folder: " + folder;
                AppendLog("Diagnose input: started for " + label + ".");
                string runFolder = folder;
                string runLabel = label;

                Thread worker = new Thread(delegate()
                {
                    DiagResult result = null;
                    string error = null;
                    try
                    {
                        IntPtr hwnd = FindWindowFor(inst, 2000);
                        result = InputDiagnostics.Run(runLabel, hwnd, delegate(string msg)
                        {
                            Dispatcher.BeginInvoke(new Action(delegate { _diagStatus.Text = msg + " (do not touch the mouse)"; }));
                        }, runFolder);
                    }
                    catch (Exception ex)
                    {
                        error = ex.GetType().Name + ": " + ex.Message;
                        InputDiagnostics.WriteStatus(runFolder, runLabel, "CRASHED: " + ex);
                    }
                    Dispatcher.BeginInvoke(new Action(delegate { OnDiagnosticsDone(runLabel, runFolder, result, error); }));
                });
                worker.IsBackground = true;
                worker.Name = "InputDiagnostics";
                worker.Start();
            }
            catch (Exception ex)
            {
                _diagRunning = false;
                try { if (_diagButton != null) { _diagButton.IsEnabled = _diagItems.Count > 0; } } catch (Exception) { }
                InputDiagnostics.WriteStatus(folder, label, "ERROR starting the diagnostics: " + ex);
                try { ShowDiagRefusal(label, folder, "Could not start: " + ex.GetType().Name + ": " + ex.Message); } catch (Exception) { }
            }
        }

        private void OnDiagnosticsDone(string label, string folder, DiagResult result, string error)
        {
            _diagRunning = false;
            _diagButton.IsEnabled = _diagItems.Count > 0;
            if (result == null)
            {
                ShowDiagRefusal(label, folder, "Diagnostics failed: " + error);
                return;
            }
            _diagStatus.Text = "Done. Report saved to " + result.Folder;
            AppendLog("Diagnose input finished for " + label + ". Folder: " + result.Folder);
            try
            {
                DiagResultDialog d = new DiagResultDialog(this, result);
                TrackDialog(d);
                d.ShowDialog();
            }
            catch (Exception ex)
            {
                AppendLog("Diagnose input: could not show the result dialog (" + ex.GetType().Name + "). Report: " + result.Folder);
            }
        }
    }
    /// <summary>Shows the diagnostics summary with a button that opens the folder.</summary>
    internal sealed class DiagResultDialog : Window
    {
        public DiagResultDialog(Window owner, DiagResult result)
        {
            DialogUtil.Setup(this, "Input diagnostics", owner, 620);
            ThemedPopups.Install(Resources);

            StackPanel body = new StackPanel();
            body.Margin = new Thickness(22);
            body.Children.Add(UiUtil.Label("Diagnostics finished", 18, true, Theme.Text));
            body.Children.Add(DialogUtil.Para("Send the folder below (report.txt and the PNG screenshots) to whoever is helping you. It contains no passwords or tokens.", 13, Theme.TextMuted, 6));

            TextBox box = new TextBox();
            box.Text = result.Summary + Environment.NewLine + result.Folder;
            box.IsReadOnly = true;
            box.TextWrapping = TextWrapping.Wrap;
            box.VerticalScrollBarVisibility = ScrollBarVisibility.Auto;
            box.MaxHeight = 300;
            box.MinHeight = 120;
            box.Margin = new Thickness(0, 12, 0, 14);
            box.Padding = new Thickness(8);
            box.Background = Theme.SurfaceAlt;
            box.Foreground = Theme.Text;
            box.BorderBrush = Theme.Border;
            box.FontSize = 12;
            AutomationProperties.SetName(box, "Diagnostics summary");
            body.Children.Add(box);

            StackPanel buttons = new StackPanel();
            buttons.Orientation = Orientation.Horizontal;
            buttons.HorizontalAlignment = HorizontalAlignment.Right;
            string folder = result.Folder;
            Button copy = PillButton.Create("_Copy path", PillStyle.Secondary);
            copy.Click += delegate { try { Clipboard.SetText(folder); } catch (Exception) { } };
            Button open = PillButton.Create("_Open diagnostics folder", PillStyle.Primary);
            open.Margin = new Thickness(10, 0, 0, 0);
            open.IsDefault = true;
            open.Click += delegate
            {
                try { Process.Start("explorer.exe", "\"" + folder + "\""); } catch (Exception) { }
            };
            Button close = PillButton.Create("Close", PillStyle.Secondary);
            close.Margin = new Thickness(10, 0, 0, 0);
            close.IsCancel = true;
            close.Click += delegate { Close(); };
            buttons.Children.Add(copy);
            buttons.Children.Add(open);
            buttons.Children.Add(close);
            body.Children.Add(buttons);
            Content = body;
        }
    }
}
