using System;
using System.Threading;
using System.Windows;
using System.Windows.Controls;

namespace RbxMulti
{
    public partial class MainWindow
    {
        private Button _checkUpdatesButton;
        private TextBlock _updateStatus;
        private bool _updateBusy;

        private UIElement BuildUpdateControls()
        {
            StackPanel panel = new StackPanel();
            panel.Margin = new Thickness(0, 12, 0, 8);
            _checkUpdatesButton = PillButton.Create("Check for updates", PillStyle.Secondary);
            _checkUpdatesButton.HorizontalAlignment = HorizontalAlignment.Left;
            _checkUpdatesButton.Click += OnCheckUpdates;
            panel.Children.Add(_checkUpdatesButton);
            _updateStatus = Track(UiUtil.Label("Installed version: " + AppVersion.Display + ". Private repository updates use your GitHub CLI sign-in (gh auth login).", 12, false, Theme.TextMuted), true);
            _updateStatus.TextWrapping = TextWrapping.Wrap;
            panel.Children.Add(_updateStatus);
            return panel;
        }

        private void OnCheckUpdates(object sender, RoutedEventArgs e)
        {
            if (_updateBusy || IsWorking() || _profileRunning) { ShowInfo("Wait for the current launch or action to finish before checking for updates."); return; }
            _updateBusy = true;
            _checkUpdatesButton.IsEnabled = false;
            _updateStatus.Text = "Checking GitHub for updates...";
            ThreadPool.QueueUserWorkItem(delegate
            {
                UpdateInfo update = null;
                string error = null;
                try { update = UpdateService.Check(); } catch (Exception ex) { error = ex.Message; }
                Dispatcher.BeginInvoke(new Action(delegate
                {
                    if (_exiting) return;
                    _updateBusy = false;
                    _checkUpdatesButton.IsEnabled = true;
                    if (error != null) { _updateStatus.Text = "Update check failed: " + error; return; }
                    if (UpdateService.CompareVersions(update.Version, AppVersion.Display) <= 0)
                    {
                        _updateStatus.Text = "You're up to date (" + AppVersion.Display + "). Latest on main: " + update.Version + ".";
                        return;
                    }
                    _updateStatus.Text = "Update available: " + update.Version;
                    if (IsWorking() || _profileRunning) { _updateStatus.Text += ". Finish the current action, then check again to install."; return; }
                    if (!ShowUpdateOffer(update)) return;
                    BeginSourceUpdate(update);
                }));
            });
        }

        private bool ShowUpdateOffer(UpdateInfo update)
        {
            Window dialog = new Window { Title = "Cookie Jar update", Width = 600, Height = 560,
                Owner = this, WindowStartupLocation = WindowStartupLocation.CenterOwner,
                Background = Theme.Bg, Foreground = Theme.Text, FontFamily = Theme.UiFont };
            DockPanel content = new DockPanel { Margin = new Thickness(20) };
            TextBlock heading = UiUtil.Label("Update " + AppVersion.Display + " → " + update.Version, 18, true, Theme.Text);
            DockPanel.SetDock(heading, Dock.Top); content.Children.Add(heading);
            TextBlock detail = UiUtil.Label("Downloads and builds Windows source from GitHub main. Your account data and Git checkout stay in place. "
                + "After a successful build you can restart Cookie Jar, leaving Roblox running.\nSource: " + update.Revision, 13, false, Theme.TextMuted);
            detail.TextWrapping = TextWrapping.Wrap;
            detail.Margin = new Thickness(0, 8, 0, 12);
            DockPanel.SetDock(detail, Dock.Top); content.Children.Add(detail);
            StackPanel buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 12, 0, 0) };
            Button cancel = PillButton.Create("Later", PillStyle.Secondary); cancel.IsCancel = true; cancel.IsDefault = true;
            cancel.Click += delegate { dialog.DialogResult = false; };
            Button install = PillButton.Create("Download and build update", PillStyle.Primary); install.Margin = new Thickness(12, 0, 0, 0);
            install.Click += delegate { dialog.DialogResult = true; };
            buttons.Children.Add(cancel); buttons.Children.Add(install);
            DockPanel.SetDock(buttons, Dock.Bottom); content.Children.Add(buttons);
            TextBox notes = new TextBox { Text = update.Notes, IsReadOnly = true, TextWrapping = TextWrapping.Wrap,
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Background = Theme.SurfaceAlt, Foreground = Theme.Text,
                BorderBrush = Theme.Border, Padding = new Thickness(10) };
            content.Children.Add(notes); dialog.Content = content;
            dialog.Loaded += delegate { Theme.ApplyTitleBar(dialog); };
            return dialog.ShowDialog() == true;
        }

        private void BeginSourceUpdate(UpdateInfo update)
        {
            _updateBusy = true;
            _checkUpdatesButton.IsEnabled = false;
            _updateStatus.Text = "Downloading and building " + update.Version + "...";
            BeginWork("Preparing update; Cookie Jar will keep running until you choose to restart.");
            bool miniBar = _startHidden;
            ThreadPool.QueueUserWorkItem(delegate
            {
                PreparedUpdate prepared = null;
                string error = null;
                try { prepared = UpdateService.Prepare(update, miniBar); } catch (Exception ex) { error = ex.Message; }
                Dispatcher.BeginInvoke(new Action(delegate
                {
                    if (_exiting) return;
                    if (error != null)
                    {
                        EndWork(); _updateBusy = false; _checkUpdatesButton.IsEnabled = true;
                        _updateStatus.Text = "Update preparation failed: " + error; AppendLog(_updateStatus.Text); return;
                    }
                    _updateStatus.Text = "Version " + update.Version + " is ready to install.";
                    if (MessageBox.Show(this, "The update has been built and verified. Restart Cookie Jar to install " + update.Version
                        + "? Roblox sessions will stay running. Launcher automation pauses during restart; the hardware override resets. A backup of the old executable will be kept.",
                        "Install Cookie Jar update", MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.No) != MessageBoxResult.Yes)
                    { EndWork(); _updateBusy = false; _checkUpdatesButton.IsEnabled = true; _updateStatus.Text += " Check again when ready to restart."; return; }
                    try { UpdateService.StartInstall(prepared); DoExit(false); }
                    catch (Exception ex) { EndWork(); _updateBusy = false; _checkUpdatesButton.IsEnabled = true; _updateStatus.Text = "Could not start installation: " + ex.Message; }
                }));
            });
        }
    }
}
