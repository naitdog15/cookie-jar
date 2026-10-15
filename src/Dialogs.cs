using System;
using System.Collections.Generic;
using System.Globalization;
using System.Threading;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;

namespace RbxMulti
{
    internal static class DialogUtil
    {
        public static void Setup(Window w, string title, Window owner, double width)
        {
            w.Title = title;
            SmoothScroll.Attach(w);
            if (owner != null && owner.IsVisible)
            {
                w.Owner = owner;
                w.WindowStartupLocation = WindowStartupLocation.CenterOwner;
            }
            else
            {
                w.WindowStartupLocation = WindowStartupLocation.CenterScreen;
            }
            w.ResizeMode = ResizeMode.NoResize;
            w.ShowInTaskbar = false;
            w.SizeToContent = SizeToContent.Height;
            w.Width = width;
            w.Background = Theme.Bg;
            w.Foreground = Theme.Text;
            w.FontFamily = Theme.UiFont;
            w.SourceInitialized += delegate { Theme.ApplyTitleBar(w); };
        }

        public static TextBox Box(string name, double width)
        {
            TextBox box = new TextBox();
            box.Padding = new Thickness(6, 4, 6, 4);
            box.Background = Theme.Surface;
            box.Foreground = Theme.Text;
            box.BorderBrush = Theme.Border;
            box.CaretBrush = Theme.Text;
            if (width > 0) box.Width = width;
            AutomationProperties.SetName(box, name);
            return box;
        }

        public static TextBlock Para(string text, double size, Brush fg, double top)
        {
            TextBlock t = UiUtil.Label(text, size, false, fg);
            t.TextWrapping = TextWrapping.Wrap;
            t.Margin = new Thickness(0, top, 0, 0);
            return t;
        }

        public static TextBlock Heading(string text, double top)
        {
            TextBlock t = UiUtil.Label(text, 13, true, Theme.Text);
            t.Margin = new Thickness(0, top, 0, 4);
            return t;
        }
    }

    /// <summary>Adds an account: masked token paste, validated against Roblox before anything is stored.</summary>
    public class AddAccountDialog : Window
    {
        private readonly PasswordBox _token;
        private readonly TextBox _label;
        private readonly TextBlock _status;
        private readonly PillButton _save;
        private readonly PillButton _cancel;
        private bool _busy;
        private bool _closed;

        /// <summary>Set when the dialog returns true. Holds the validated token; the caller stores it.</summary>
        public Account Result;

        public AddAccountDialog(Window owner)
        {
            DialogUtil.Setup(this, "Add Roblox account", owner, 520);

            StackPanel panel = new StackPanel();
            panel.Margin = new Thickness(22);

            panel.Children.Add(DialogUtil.Heading("How to get the .ROBLOSECURITY value", 0));
            panel.Children.Add(DialogUtil.Para(
                "1. Log in to roblox.com in your browser (a private window is best).\n"
                + "2. Press F12, open the Application tab (Chrome/Edge) or Storage tab (Firefox), then Cookies > https://www.roblox.com.\n"
                + "3. Find .ROBLOSECURITY, double-click its Value and copy it. It is one very long line starting with _|WARNING.\n"
                + "4. Paste it below. Don't use Log out on that browser afterwards - that makes the value stop working.",
                12, Theme.TextMuted, 0));

            TextBlock warn = DialogUtil.Para(
                "This value is the key to the account: anyone who has it can use the account without a password. "
                + "Never share it or paste it anywhere else. It is kept only on this PC, encrypted for your Windows user.",
                12, Theme.Warning, 10);
            panel.Children.Add(warn);

            panel.Children.Add(DialogUtil.Heading(".ROBLOSECURITY value (hidden as you paste)", 14));
            _token = new PasswordBox();
            _token.Padding = new Thickness(6, 4, 6, 4);
            _token.Background = Theme.Surface;
            _token.Foreground = Theme.Text;
            _token.BorderBrush = Theme.Border;
            _token.CaretBrush = Theme.Text;
            _token.ToolTip = "Paste the cookie value here. It is masked and checked with Roblox before it is saved.";
            AutomationProperties.SetName(_token, "ROBLOSECURITY value");
            panel.Children.Add(_token);

            panel.Children.Add(DialogUtil.Heading("Label (optional)", 12));
            _label = DialogUtil.Box("Account label", 0);
            _label.MaxLength = 30;
            _label.ToolTip = "A nickname shown instead of the username, for example \"Main\" or \"Alt 2\".";
            panel.Children.Add(_label);

            _status = DialogUtil.Para(" ", 12, Theme.TextMuted, 12);
            panel.Children.Add(_status);

            StackPanel buttons = new StackPanel();
            buttons.Orientation = Orientation.Horizontal;
            buttons.HorizontalAlignment = HorizontalAlignment.Right;
            buttons.Margin = new Thickness(0, 16, 0, 0);

            _cancel = PillButton.Create("_Cancel", PillStyle.Secondary);
            _cancel.IsCancel = true;
            _cancel.Click += delegate { if (!_busy) { _token.Clear(); DialogResult = false; } };

            _save = PillButton.Create("Check and _add", PillStyle.Primary);
            _save.IsDefault = true;
            _save.Margin = new Thickness(12, 0, 0, 0);
            _save.ToolTip = "Checks the value with Roblox, then saves the account encrypted.";
            _save.Click += OnSave;

            buttons.Children.Add(_cancel);
            buttons.Children.Add(_save);
            panel.Children.Add(buttons);

            Content = panel;
            Loaded += delegate { _token.Focus(); };
            Closed += delegate { _closed = true; _token.Clear(); };
            Closing += delegate(object s, System.ComponentModel.CancelEventArgs e) { if (_busy) e.Cancel = true; };
        }

        private void SetBusy(bool busy, string text, Brush color)
        {
            _busy = busy;
            _save.IsEnabled = !busy;
            _token.IsEnabled = !busy;
            _label.IsEnabled = !busy;
            _status.Text = text;
            _status.Foreground = color;
        }

        private void OnSave(object sender, RoutedEventArgs e)
        {
            if (_busy) return;
            string error;
            string token = TokenParser.Normalize(_token.Password, out error);
            if (token == null)
            {
                SetBusy(false, error, Theme.Danger);
                return;
            }
            string label = _label.Text.Trim();
            SetBusy(true, "Checking with Roblox...", Theme.Accent);

            ThreadPool.QueueUserWorkItem(delegate(object state)
            {
                string status, name, err;
                long userId;
                bool ok = TicketLauncher.Validate(token, out status, out userId, out name, out err);
                Dispatcher.BeginInvoke(new Action(delegate
                {
                    if (_closed) return;
                    if (!ok)
                    {
                        SetBusy(false, err, Theme.Danger);
                        return;
                    }
                    Account a = new Account();
                    a.Label = label;
                    a.Username = name;
                    a.UserId = userId;
                    a.Token = token;
                    a.Status = "Valid";
                    Result = a;
                    _token.Clear();
                    _busy = false;
                    DialogResult = true;
                }));
            });
        }
    }

    /// <summary>Create or edit a profile: name, member accounts (checkbox list), place id and delay.</summary>
    public class ProfileDialog : Window
    {
        private readonly TextBox _name;
        private readonly TextBox _place;
        private readonly TextBox _stagger;
        private readonly TextBlock _error;
        private readonly List<KeyValuePair<CheckBox, Account>> _boxes = new List<KeyValuePair<CheckBox, Account>>();

        public LaunchProfile Result;

        public ProfileDialog(Window owner, LaunchProfile existing, List<Account> accounts)
        {
            bool editing = existing != null;
            DialogUtil.Setup(this, editing ? "Edit profile" : "New profile", owner, 460);

            StackPanel panel = new StackPanel();
            panel.Margin = new Thickness(22);

            panel.Children.Add(DialogUtil.Heading("Profile name", 0));
            _name = DialogUtil.Box("Profile name", 0);
            _name.MaxLength = 40;
            _name.Text = editing ? existing.Name : "";
            _name.ToolTip = "Letters, digits, spaces, '-' and '_' (up to 40 characters).";
            panel.Children.Add(_name);

            panel.Children.Add(DialogUtil.Heading("Accounts in this profile", 12));
            StackPanel list = new StackPanel();
            if (accounts.Count == 0)
            {
                list.Children.Add(DialogUtil.Para("No accounts yet. Close this window and use Add account first.", 12, Theme.TextMuted, 0));
            }
            foreach (Account a in accounts)
            {
                CheckBox cb = new CheckBox();
                cb.Content = a.DisplayName + (string.IsNullOrEmpty(a.Label) || a.Label == a.Username ? "" : "  (" + a.Username + ")");
                cb.Foreground = Theme.Text;
                cb.Margin = new Thickness(2, 3, 2, 3);
                cb.IsChecked = editing && existing.AccountIds.Contains(a.Id);
                AutomationProperties.SetName(cb, a.DisplayName);
                list.Children.Add(cb);
                _boxes.Add(new KeyValuePair<CheckBox, Account>(cb, a));
            }
            ScrollViewer scroll = new ScrollViewer();
            scroll.MaxHeight = 170;
            scroll.VerticalScrollBarVisibility = ScrollBarVisibility.Auto;
            scroll.Content = list;
            Border frame = new Border();
            frame.BorderBrush = Theme.Border;
            frame.BorderThickness = new Thickness(1);
            frame.Background = Theme.Surface;
            frame.Padding = new Thickness(8);
            frame.Child = scroll;
            panel.Children.Add(frame);

            Grid row = new Grid();
            row.Margin = new Thickness(0, 12, 0, 0);
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(16) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

            StackPanel placeBox = new StackPanel();
            placeBox.Children.Add(DialogUtil.Heading("Place id (default: Adopt Me)", 0));
            _place = DialogUtil.Box("Place id", 0);
            _place.Text = (editing ? existing.PlaceId : LaunchProfile.DefaultPlaceId).ToString(CultureInfo.InvariantCulture);
            _place.ToolTip = "The number in the game's roblox.com address. 920587237 is Adopt Me.";
            placeBox.Children.Add(_place);
            Grid.SetColumn(placeBox, 0);
            row.Children.Add(placeBox);

            StackPanel staggerBox = new StackPanel();
            staggerBox.Children.Add(DialogUtil.Heading("Seconds between accounts", 0));
            _stagger = DialogUtil.Box("Seconds between accounts", 0);
            _stagger.Text = (editing ? existing.StaggerSeconds : LaunchProfile.DefaultStagger).ToString(CultureInfo.InvariantCulture);
            _stagger.ToolTip = "Extra pause after each client has opened, before the next account starts (0 to 300).";
            staggerBox.Children.Add(_stagger);
            Grid.SetColumn(staggerBox, 2);
            row.Children.Add(staggerBox);
            panel.Children.Add(row);

            _error = DialogUtil.Para(" ", 12, Theme.Danger, 10);
            panel.Children.Add(_error);

            StackPanel buttons = new StackPanel();
            buttons.Orientation = Orientation.Horizontal;
            buttons.HorizontalAlignment = HorizontalAlignment.Right;
            buttons.Margin = new Thickness(0, 12, 0, 0);

            PillButton cancel = PillButton.Create("_Cancel", PillStyle.Secondary);
            cancel.IsCancel = true;
            cancel.Click += delegate { DialogResult = false; };
            PillButton save = PillButton.Create("_Save", PillStyle.Primary);
            save.IsDefault = true;
            save.Margin = new Thickness(12, 0, 0, 0);
            save.Click += OnSave;
            buttons.Children.Add(cancel);
            buttons.Children.Add(save);
            panel.Children.Add(buttons);

            Content = panel;
            Loaded += delegate { _name.Focus(); _name.SelectAll(); };
        }

        private void OnSave(object sender, RoutedEventArgs e)
        {
            string err;
            string name = ProfileStore.NormalizeName(_name.Text, out err);
            if (name == null) { _error.Text = err; return; }

            long place;
            if (!long.TryParse(_place.Text.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out place) || place <= 0)
            {
                _error.Text = "Place id must be a positive whole number.";
                return;
            }
            int stagger;
            if (!int.TryParse(_stagger.Text.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out stagger) || stagger > 300)
            {
                _error.Text = "Seconds between accounts must be a whole number from 0 to 300.";
                return;
            }

            LaunchProfile p = new LaunchProfile();
            p.Name = name;
            p.PlaceId = place;
            p.StaggerSeconds = stagger;
            foreach (KeyValuePair<CheckBox, Account> pair in _boxes)
            {
                if (pair.Key.IsChecked == true) p.AccountIds.Add(pair.Value.Id);
            }
            if (p.AccountIds.Count == 0)
            {
                _error.Text = "Tick at least one account.";
                return;
            }
            Result = p;
            DialogResult = true;
        }
    }

    /// <summary>
    /// Themed confirmation before ending instances (tree kill, no in-game Leave prompt). Go back is the default.
    /// Topmost and on screen centre so it is visible over the mini bar.
    /// </summary>
    internal sealed class StopConfirmDialog : Window
    {
        /// <summary>When set (checkbox, single-instance only), single stops skip the prompt for the rest of this session.</summary>
        public static bool SkipSingleThisSession;

        private bool _confirmed;
        private readonly CheckBox _dontAsk;

        public static string Describe(RobloxInstance r)
        {
            string a = r.AccountName;
            if (string.IsNullOrEmpty(a)) a = r.Name;
            if (string.IsNullOrEmpty(a)) a = "Instance";
            return a + " (PID " + r.Pid.ToString(CultureInfo.InvariantCulture) + ")";
        }

        /// <summary>Returns true when the user chose End instance. Asks nothing when single-stop prompts are skipped.</summary>
        public static bool Confirm(Window owner, IList<RobloxInstance> targets)
        {
            if (targets == null || targets.Count == 0) return false;
            if (targets.Count == 1 && SkipSingleThisSession) return true;
            StopConfirmDialog d = new StopConfirmDialog(owner, targets);
            d.ShowDialog();
            if (d._confirmed && targets.Count == 1 && d._dontAsk.IsChecked == true) SkipSingleThisSession = true;
            return d._confirmed;
        }

        private StopConfirmDialog(Window owner, IList<RobloxInstance> targets)
        {
            bool many = targets.Count > 1;
            Title = many ? "End all instances" : "End instance";
            Width = 480;
            SizeToContent = SizeToContent.Height;
            ResizeMode = ResizeMode.NoResize;
            WindowStartupLocation = WindowStartupLocation.CenterScreen;
            ShowInTaskbar = true;
            Topmost = true;
            FontFamily = Theme.UiFont;
            Background = Theme.Bg;
            Foreground = Theme.Text;
            ThemedPopups.Install(Resources);

            StackPanel body = new StackPanel();
            body.Margin = new Thickness(22);
            body.Children.Add(UiUtil.Label(many ? "End all " + targets.Count.ToString(CultureInfo.InvariantCulture) + " instances?" : "End this instance?", 18, true, Theme.Text));

            string msg;
            if (many)
            {
                System.Text.StringBuilder sb = new System.Text.StringBuilder();
                sb.Append("These instances and every process they started will be force-closed immediately, without the in-game Leave game prompt:\n");
                foreach (RobloxInstance r in targets) sb.Append("\n  - ").Append(Describe(r));
                sb.Append("\n\nUnsaved progress in their windows will be lost. Unsaved in-game progress may be lost, and any trade or action in progress will be cut off. Continue?");
                msg = sb.ToString();
            }
            else
            {
                msg = Describe(targets[0]) + " and every process it started will be force-closed immediately, without the in-game Leave game prompt. "
                    + "Unsaved progress in its windows will be lost. Unsaved in-game progress may be lost, and any trade or action in progress will be cut off. Continue?";
            }
            TextBlock text = UiUtil.Label(msg, 13, false, Theme.TextMuted);
            text.TextWrapping = TextWrapping.Wrap;
            text.Margin = new Thickness(0, 8, 0, 14);
            body.Children.Add(text);

            _dontAsk = new CheckBox();
            _dontAsk.Content = "Don't ask again this session";
            _dontAsk.Foreground = Theme.Text;
            _dontAsk.IsChecked = false;
            _dontAsk.Margin = new Thickness(0, 0, 0, 14);
            _dontAsk.Visibility = many ? Visibility.Collapsed : Visibility.Visible;
            body.Children.Add(_dontAsk);

            StackPanel buttons = new StackPanel();
            buttons.Orientation = Orientation.Horizontal;
            buttons.HorizontalAlignment = HorizontalAlignment.Right;
            Button back = PillButton.Create("Go back", PillStyle.Primary);
            back.IsDefault = true;
            back.IsCancel = true;
            back.Click += delegate { _confirmed = false; Close(); };
            Button end = PillButton.Create(many ? "End instances" : "End instance", PillStyle.Danger);
            end.Margin = new Thickness(12, 0, 0, 0);
            end.ToolTip = "Ends the Roblox client and all its child processes immediately.";
            end.Click += delegate { _confirmed = true; Close(); };
            buttons.Children.Add(back);
            buttons.Children.Add(end);
            body.Children.Add(buttons);

            Content = body;
            Loaded += delegate { Theme.ApplyTitleBar(this); back.Focus(); };
        }
    }

    /// <summary>
    /// RAM/CPU launch warning that answers Yes by itself after a countdown, so a profile launch keeps its
    /// stagger instead of waiting on a prompt. Topmost, opened without taking focus (the user may be typing elsewhere).
    /// </summary>
    internal sealed class TimedWarnDialog : Window
    {
        public const int DefaultSeconds = 6;

        private bool _confirmed = true;   // timing out counts as Yes

        /// <summary>Returns false only when the user picked the No button; Yes or the timeout return true.</summary>
        public static bool Confirm(Window owner, string message, string yesText, string noText, int seconds)
        {
            TimedWarnDialog d = new TimedWarnDialog(owner, message, yesText, noText, Math.Max(1, seconds));
            d.ShowDialog();
            return d._confirmed;
        }

        private TimedWarnDialog(Window owner, string message, string yesText, string noText, int seconds)
        {
            Title = "Cookie Jar";
            Width = 460;
            SizeToContent = SizeToContent.Height;
            ResizeMode = ResizeMode.NoResize;
            if (owner != null && owner.IsVisible) { Owner = owner; WindowStartupLocation = WindowStartupLocation.CenterOwner; }
            else WindowStartupLocation = WindowStartupLocation.CenterScreen;
            ShowInTaskbar = false;
            ShowActivated = false;
            Topmost = true;
            FontFamily = Theme.UiFont;
            Background = Theme.Bg;
            Foreground = Theme.Text;
            ThemedPopups.Install(Resources);

            StackPanel body = new StackPanel();
            body.Margin = new Thickness(22);
            body.Children.Add(UiUtil.Label("Resource warning", 18, true, Theme.Warning));
            TextBlock text = UiUtil.Label(message, 13, false, Theme.TextMuted);
            text.TextWrapping = TextWrapping.Wrap;
            text.Margin = new Thickness(0, 8, 0, 14);
            body.Children.Add(text);

            StackPanel buttons = new StackPanel();
            buttons.Orientation = Orientation.Horizontal;
            buttons.HorizontalAlignment = HorizontalAlignment.Right;
            Button no = PillButton.Create(noText, PillStyle.Danger);
            no.IsCancel = true;
            no.Click += delegate { _confirmed = false; Close(); };
            Button yes = PillButton.Create(yesText + " (" + seconds.ToString(CultureInfo.InvariantCulture) + ")", PillStyle.Primary);
            yes.IsDefault = true;
            yes.Margin = new Thickness(12, 0, 0, 0);
            yes.Click += delegate { _confirmed = true; Close(); };
            buttons.Children.Add(no);
            buttons.Children.Add(yes);
            body.Children.Add(buttons);
            Content = body;

            int left = seconds;
            System.Windows.Threading.DispatcherTimer timer = new System.Windows.Threading.DispatcherTimer();
            timer.Interval = TimeSpan.FromSeconds(1);
            timer.Tick += delegate
            {
                left--;
                if (left <= 0) { timer.Stop(); _confirmed = true; Close(); return; }
                yes.Content = yesText + " (" + left.ToString(CultureInfo.InvariantCulture) + ")";
            };
            Loaded += delegate { Theme.ApplyTitleBar(this); timer.Start(); };
            Closed += delegate { timer.Stop(); };
        }
    }
}