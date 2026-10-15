// TeleportUi.cs
//
// UI pieces for the Teleport feature (C# 5, built in code like the rest of the launcher).
//
// PUBLIC API (namespace RbxMulti)
//   interface ITeleportHost         what the card row and the mini bar need from the window (MainWindow implements it,
//                                   IMiniBarHost extends it). Everything is called on the UI thread.
//   class TeleportChoice            combo item: one other Running instance, shown by its username.
//   class TeleportCardRow : StackPanel
//       TeleportCardRow(ITeleportHost host, RobloxInstance instance)
//       void Refresh()              re-reads host state (targets, enabled state, spinner, result chip). Call every tick.
//       void ApplyTheme()           re-reads Theme brushes (call from the owner's theme handler).
//       Rows: [Teleport to] [dropdown of other Running instances by username] [Teleport] [Cancel + spinner while busy],
//             a coloured result chip (green Teleported / amber Clicked - unconfirmed / red message, fades after
//             TeleportCardRow.ChipSeconds), and a muted caption with the friends/automation notes.
//   static class TeleportText       shared strings (chip text, tooltips) so the card and the mini bar agree.

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Shapes;

namespace RbxMulti
{
    public interface ITeleportHost
    {
        /// <summary>True while any teleport runs (only one at a time).</summary>
        bool TeleportBusy { get; }
        /// <summary>Source instance of the running teleport, or null.</summary>
        RobloxInstance TeleportSource { get; }
        /// <summary>Last finished result (null until one finished) with its source and UTC time.</summary>
        TeleportResult LastTeleportResult { get; }
        RobloxInstance LastTeleportSource { get; }
        DateTime LastTeleportUtc { get; }
        /// <summary>Progress text while a teleport runs ("Waiting for the friends list to load... 8 s"), or "".</summary>
        string TeleportStatusText { get; }
        /// <summary>Other instances that are Running and have a known username, in list order.</summary>
        IList<RobloxInstance> TeleportTargets(RobloxInstance source);
        /// <summary>Changes whenever the set of possible targets (or a username) changes; cheap to read every tick.</summary>
        int TeleportTargetsStamp { get; }
        /// <summary>Null when Teleport source -> target may be started now, otherwise the reason (for tooltips). Cheap, no window lookups.</summary>
        string TeleportBlockReason(RobloxInstance source, RobloxInstance target);
        void StartTeleport(RobloxInstance source, RobloxInstance target);
        void CancelTeleport();
    }

    public sealed class TeleportChoice
    {
        public RobloxInstance Instance;
        public string Text;

        public TeleportChoice(RobloxInstance inst)
        {
            Instance = inst;
            Text = TeleportText.UserOf(inst);
        }

        public override string ToString() { return Text; }
    }

    public static class TeleportText
    {
        public const string Notes = "Both accounts must be friends in Adopt Me. Teleport uses screen automation: the game window briefly takes focus and the mouse moves, only when you press the button. It can fail if the game UI changes or the window is covered or too small.";

        public static string UserOf(RobloxInstance i)
        {
            if (i == null) return "";
            if (!string.IsNullOrEmpty(i.AccountName)) return i.AccountName;
            return string.IsNullOrEmpty(i.Name) ? "Instance" : i.Name;
        }

        /// <summary>Short chip text: Teleported / Clicked - unconfirmed / the failure message.</summary>
        public static string Chip(TeleportResult r)
        {
            if (r == null) return "";
            switch (r.Status)
            {
                case TeleportStatus.Success: return "Teleported";
                case TeleportStatus.Likely: return "Clicked - unconfirmed";
                case TeleportStatus.Cancelled: return "Teleport cancelled";
                default: return string.IsNullOrEmpty(r.Message) ? "Teleport failed" : r.Message;
            }
        }
    }

    public sealed class TeleportCardRow : StackPanel
    {
        /// <summary>Seconds a finished result stays visible on the card before it fades out.</summary>
        public const double ChipSeconds = 30;

        private readonly ITeleportHost _host;
        private readonly RobloxInstance _inst;
        private readonly TextBlock _label;
        private readonly ComboBox _combo;
        private readonly Button _go;
        private readonly Button _cancel;
        private readonly Path _spinner;
        private readonly RotateTransform _rotate;
        private readonly TextBlock _busyText;
        private readonly Border _chip;
        private readonly TextBlock _chipIcon;
        private readonly TextBlock _chipText;
        private readonly TextBlock _caption;
        private int _stamp = -1;
        private string _goTip, _comboTip;
        private bool _spinning;
        private bool _fading;
        private DateTime _shownResultUtc = DateTime.MinValue;
        private bool _syncing;

        public TeleportCardRow(ITeleportHost host, RobloxInstance instance)
        {
            _host = host;
            _inst = instance;

            _label = UiUtil.Label("Teleport to", 12, false, Theme.TextMuted);
            _label.VerticalAlignment = VerticalAlignment.Center;

            _combo = new ComboBox();
            _combo.Width = 170;
            _combo.Height = 30;
            _combo.VerticalContentAlignment = VerticalAlignment.Center;
            _combo.Margin = new Thickness(8, 0, 8, 0);
            ThemedCombo.Apply(_combo);
            AutomationProperties.SetName(_combo, "Teleport target for " + TeleportText.UserOf(instance));
            _combo.SelectionChanged += delegate { if (!_syncing) Refresh(); };

            _go = PillButton.Create("_Teleport", PillStyle.Primary);
            _go.Click += OnGoClick;
            _go.Margin = new Thickness(0, 0, 8, 0);
            AutomationProperties.SetName(_go, "Teleport " + TeleportText.UserOf(instance) + " to the selected instance");

            _cancel = PillButton.Create("_Cancel", PillStyle.Danger);
            _cancel.Click += delegate { _host.CancelTeleport(); };
            _cancel.Margin = new Thickness(0, 0, 8, 0);
            _cancel.Visibility = Visibility.Collapsed;
            _cancel.ToolTip = "Stop this teleport. The game panel is closed again.";
            AutomationProperties.SetName(_cancel, "Cancel teleport");

            _rotate = new RotateTransform(0);
            _spinner = new Path();
            _spinner.Data = Geometry.Parse("M 9,2 A 7,7 0 1 1 2,9");
            _spinner.StrokeThickness = 2.2;
            _spinner.StrokeStartLineCap = PenLineCap.Round;
            _spinner.StrokeEndLineCap = PenLineCap.Round;
            _spinner.Width = 18;
            _spinner.Height = 18;
            _spinner.RenderTransform = _rotate;
            _spinner.RenderTransformOrigin = new Point(0.5, 0.5);
            _spinner.Margin = new Thickness(0, 0, 8, 0);
            _spinner.VerticalAlignment = VerticalAlignment.Center;
            _spinner.Visibility = Visibility.Collapsed;
            _spinner.Stroke = Theme.Accent;

            _busyText = UiUtil.Label("Teleporting...", 12, false, Theme.TextMuted);
            _busyText.Visibility = Visibility.Collapsed;

            WrapPanel row = new WrapPanel();
            row.Children.Add(_label);
            row.Children.Add(_combo);
            row.Children.Add(_go);
            row.Children.Add(_cancel);
            row.Children.Add(_spinner);
            row.Children.Add(_busyText);

            _chipIcon = new TextBlock();
            _chipIcon.FontFamily = new FontFamily("Segoe Fluent Icons, Segoe MDL2 Assets");
            _chipIcon.FontSize = 13;
            _chipIcon.VerticalAlignment = VerticalAlignment.Top;
            _chipIcon.Margin = new Thickness(0, 1, 7, 0);
            _chipText = new TextBlock();
            _chipText.FontFamily = Theme.UiFont;
            _chipText.FontSize = 12;
            _chipText.FontWeight = FontWeights.SemiBold;
            _chipText.TextWrapping = TextWrapping.Wrap;
            _chipText.MaxWidth = 560;
            StackPanel chipBody = new StackPanel();
            chipBody.Orientation = Orientation.Horizontal;
            chipBody.Children.Add(_chipIcon);
            chipBody.Children.Add(_chipText);
            _chip = new Border();
            _chip.CornerRadius = new CornerRadius(10);
            _chip.Padding = new Thickness(10, 4, 12, 4);
            _chip.HorizontalAlignment = HorizontalAlignment.Left;
            _chip.Margin = new Thickness(0, 8, 0, 0);
            _chip.Child = chipBody;
            _chip.Visibility = Visibility.Collapsed;
            AutomationProperties.SetLiveSetting(_chip, AutomationLiveSetting.Polite);

            _caption = UiUtil.Label(TeleportText.Notes, 12, false, Theme.TextMuted);
            _caption.TextWrapping = TextWrapping.Wrap;
            _caption.Margin = new Thickness(0, 6, 0, 0);

            Margin = new Thickness(0, 10, 0, 0);
            Children.Add(row);
            Children.Add(_chip);
            Children.Add(_caption);
            _go.SetValue(ToolTipService.ShowOnDisabledProperty, true);
            _combo.SetValue(ToolTipService.ShowOnDisabledProperty, true);

            Refresh();
        }

        /// <summary>Compact layout (instance cards): the friends/automation note stays in the Teleport tooltip only.</summary>
        public void HideCaption()
        {
            _caption.Visibility = Visibility.Collapsed;
        }

        private void OnGoClick(object sender, RoutedEventArgs e)
        {
            TeleportChoice c = _combo.SelectedItem as TeleportChoice;
            if (c == null) return;
            _host.StartTeleport(_inst, c.Instance);
        }

        /// <summary>Re-reads host state. Cheap; called every tick and after every teleport event.</summary>
        public void Refresh()
        {
            bool busy = _host.TeleportBusy;
            bool mine = busy && ReferenceEquals(_host.TeleportSource, _inst);

            // Targets (rebuilt only when the set changes and the dropdown is closed).
            int stamp = _host.TeleportTargetsStamp;
            if (stamp != _stamp && !_combo.IsDropDownOpen)
            {
                _stamp = stamp;
                IList<RobloxInstance> targets = _host.TeleportTargets(_inst);
                RobloxInstance keep = null;
                TeleportChoice cur = _combo.SelectedItem as TeleportChoice;
                if (cur != null) keep = cur.Instance;
                _syncing = true;
                try
                {
                    _combo.Items.Clear();
                    TeleportChoice select = null;
                    foreach (RobloxInstance t in targets)
                    {
                        TeleportChoice c = new TeleportChoice(t);
                        _combo.Items.Add(c);
                        if (select == null) select = c;
                        if (ReferenceEquals(t, keep)) select = c;
                    }
                    if (select != null) _combo.SelectedItem = select;
                }
                finally { _syncing = false; }
            }

            TeleportChoice sel = _combo.SelectedItem as TeleportChoice;
            RobloxInstance target = sel == null ? null : sel.Instance;
            string reason = _host.TeleportBlockReason(_inst, target);

            _combo.IsEnabled = !busy && _inst.StatusKind == "Running" && _combo.Items.Count > 0;
            _go.IsEnabled = reason == null;
            string who = target == null ? "the selected instance" : TeleportText.UserOf(target);
            string goTip = reason == null
                ? "Teleport " + TeleportText.UserOf(_inst) + " to " + who + " by clicking FRIENDS > Teleport in this instance's game window.\n" + TeleportText.Notes
                : reason + "\n" + TeleportText.Notes;
            if (goTip != _goTip) { _goTip = goTip; _go.ToolTip = goTip; }
            string comboTip = _combo.Items.Count > 0
                ? "Teleport target: another instance that is Running (green). Listed by username."
                : "No target yet. Needs another instance that is Running (green) and was started from a stored account.";
            if (!ReferenceEquals(comboTip, _comboTip)) { _comboTip = comboTip; _combo.ToolTip = comboTip; }

            // Busy state: spinner and Cancel only on the card that is teleporting.
            _cancel.Visibility = mine ? Visibility.Visible : Visibility.Collapsed;
            _busyText.Visibility = mine ? Visibility.Visible : Visibility.Collapsed;
            string progress = mine ? _host.TeleportStatusText : "";
            _busyText.Text = mine ? (string.IsNullOrEmpty(progress) ? "Teleporting... (takes about 10 s; the game window comes to the front briefly)" : progress) : "";
            SetSpinning(mine);

            UpdateChip(mine);
        }

        private void SetSpinning(bool on)
        {
            if (on == _spinning) return;
            _spinning = on;
            if (on)
            {
                _spinner.Visibility = Visibility.Visible;
                DoubleAnimation a = new DoubleAnimation(0, 360, new Duration(TimeSpan.FromMilliseconds(900)));
                a.RepeatBehavior = RepeatBehavior.Forever;
                _rotate.BeginAnimation(RotateTransform.AngleProperty, a);
            }
            else
            {
                _rotate.BeginAnimation(RotateTransform.AngleProperty, null);
                _spinner.Visibility = Visibility.Collapsed;
            }
        }

        private void UpdateChip(bool mine)
        {
            TeleportResult r = _host.LastTeleportResult;
            bool show = false;
            if (!mine && r != null && ReferenceEquals(_host.LastTeleportSource, _inst))
            {
                double age = (DateTime.UtcNow - _host.LastTeleportUtc).TotalSeconds;
                if (age >= 0 && age < ChipSeconds) show = true;
                if (show && _host.LastTeleportUtc != _shownResultUtc)
                {
                    _shownResultUtc = _host.LastTeleportUtc;
                    _fading = false;
                    _chip.BeginAnimation(UIElement.OpacityProperty, null);
                    _chip.Opacity = 1;
                }
                if (show && !_fading && age > ChipSeconds - 1.2)
                {
                    _fading = true;
                    DoubleAnimation fade = new DoubleAnimation(1, 0, new Duration(TimeSpan.FromMilliseconds(1100)));
                    _chip.BeginAnimation(UIElement.OpacityProperty, fade);
                }
            }
            if (!show)
            {
                _chip.Visibility = Visibility.Collapsed;
                return;
            }
            _chip.Visibility = Visibility.Visible;
            ApplyChipBrushes(r);
            _chipText.Text = TeleportText.Chip(r);
            _chip.ToolTip = r.Message;
            AutomationProperties.SetName(_chip, "Teleport result: " + TeleportText.Chip(r));
        }

        private void ApplyChipBrushes(TeleportResult r)
        {
            SolidColorBrush fg, bg;
            string glyph;
            switch (r.Status)
            {
                case TeleportStatus.Success: fg = Theme.Success; bg = Theme.SuccessSoft; glyph = "E73E"; break;
                case TeleportStatus.Likely: fg = Theme.Warning; bg = Theme.WarningSoft; glyph = "E7BA"; break;
                case TeleportStatus.Cancelled: fg = Theme.TextMuted; bg = Theme.SurfaceAlt; glyph = "E711"; break;
                default: fg = Theme.Danger; bg = Theme.DangerSoft; glyph = "EA39"; break;
            }
            _chip.Background = bg;
            _chipText.Foreground = fg;
            _chipIcon.Foreground = fg;
            _chipIcon.Text = UiUtil.GlyphIcon(glyph);
        }

        public void ApplyTheme()
        {
            _label.Foreground = Theme.TextMuted;
            _caption.Foreground = Theme.TextMuted;
            _busyText.Foreground = Theme.TextMuted;
            _spinner.Stroke = Theme.Accent;
            ThemedCombo.Apply(_combo);
            TeleportResult r = _host.LastTeleportResult;
            if (_chip.Visibility == Visibility.Visible && r != null) ApplyChipBrushes(r);
        }
    }
}
