// CashOutUi.cs
//
// Card UI of the manual "Cash out" test (C# 5, WPF). The button itself is placed by the card in its action row;
// this row holds the busy text, the result chip (green Cashed out / amber Clicked - unconfirmed / red failure) and the
// expandable "Click history" list (last 50 events of this instance, auto and manual).
//
//   interface ICashOutHost   what the row needs from the window (MainWindow implements it).
//   class CashOutCardRow     StackPanel; Button = the "Cash out" button; Refresh() is cheap and called every tick.

using System;
using System.Globalization;
using System.Text;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;

namespace RbxMulti
{
    public interface ICashOutHost
    {
        bool CashOutBusy { get; }
        RobloxInstance CashOutSource { get; }
        /// <summary>Null when a manual CASH OUT may start now, else the reason (tooltip). Cheap, no window lookups.</summary>
        string CashOutBlockReason(RobloxInstance inst);
        void StartManualCashOut(RobloxInstance inst);
        CashOutResult LastCashOut(RobloxInstance inst, out DateTime utc);
    }

    public sealed class CashOutCardRow : StackPanel
    {
        public const double ChipSeconds = 30;
        public const string ButtonTip = "Look for the green CASH OUT button in this instance and click it once now (manual test of the auto-click)";

        public readonly Button Button;
        private readonly ICashOutHost _host;
        private readonly RobloxInstance _inst;
        private readonly TextBlock _busyText;
        private readonly Border _chip;
        private readonly TextBlock _chipIcon;
        private readonly TextBlock _chipText;
        private readonly TextBlock _historyToggle;
        private readonly TextBlock _historyBody;
        private readonly Border _historyBox;
        private bool _historyOpen;
        private string _historySig = "";
        private DateTime _shownUtc = DateTime.MinValue;

        public CashOutCardRow(ICashOutHost host, RobloxInstance instance)
        {
            _host = host;
            _inst = instance;

            Button = PillButton.Create("Cash _out", PillStyle.Secondary);
            Button.Margin = new Thickness(0, 0, 8, 0);
            Button.ToolTip = ButtonTip;
            AutomationProperties.SetName(Button, "Cash out now in " + TeleportText.UserOf(instance));
            Button.Click += delegate { _host.StartManualCashOut(_inst); };

            _busyText = UiUtil.Label("Looking for CASH OUT and clicking it...", 12, false, Theme.TextMuted);
            _busyText.Margin = new Thickness(0, 8, 0, 0);
            _busyText.Visibility = Visibility.Collapsed;

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

            _historyToggle = UiUtil.Label("Click history (0)  ▸", 12, false, Theme.TextMuted);
            _historyToggle.Cursor = System.Windows.Input.Cursors.Hand;
            _historyToggle.Margin = new Thickness(0, 8, 0, 0);
            _historyToggle.ToolTip = "The last 50 CASH OUT clicks of this instance (automatic and manual): time, result, click method, point.";
            _historyToggle.MouseLeftButtonUp += delegate { _historyOpen = !_historyOpen; _historySig = ""; Refresh(); };
            AutomationProperties.SetName(_historyToggle, "Click history");

            _historyBody = new TextBlock();
            _historyBody.FontFamily = new FontFamily("Consolas");
            _historyBody.FontSize = 11;
            _historyBody.TextWrapping = TextWrapping.NoWrap;
            _historyBody.Foreground = Theme.TextMuted;
            ScrollViewer sv = new ScrollViewer();
            sv.MaxHeight = 150;
            sv.VerticalScrollBarVisibility = ScrollBarVisibility.Auto;
            sv.HorizontalScrollBarVisibility = ScrollBarVisibility.Auto;
            sv.Content = _historyBody;
            _historyBox = new Border();
            _historyBox.CornerRadius = new CornerRadius(8);
            _historyBox.Padding = new Thickness(8, 6, 8, 6);
            _historyBox.Margin = new Thickness(0, 4, 0, 0);
            _historyBox.Background = Theme.SurfaceAlt;
            _historyBox.Child = sv;
            _historyBox.Visibility = Visibility.Collapsed;

            Children.Add(_busyText);
            Children.Add(_chip);
            Children.Add(_historyToggle);
            Children.Add(_historyBox);
            Refresh();
        }

        public void Refresh()
        {
            bool mine = _host.CashOutBusy && ReferenceEquals(_host.CashOutSource, _inst);
            string reason = _host.CashOutBlockReason(_inst);
            Button.IsEnabled = reason == null;
            Button.ToolTip = reason == null ? ButtonTip : reason + "\n" + ButtonTip;
            _busyText.Visibility = mine ? Visibility.Visible : Visibility.Collapsed;

            DateTime utc;
            CashOutResult r = _host.LastCashOut(_inst, out utc);
            bool show = false;
            if (!mine && r != null)
            {
                double age = (DateTime.UtcNow - utc).TotalSeconds;
                show = age >= 0 && age < ChipSeconds;
            }
            if (!show) _chip.Visibility = Visibility.Collapsed;
            else
            {
                _chip.Visibility = Visibility.Visible;
                _shownUtc = utc;
                ApplyChipBrushes(r);
                _chipText.Text = r.Chip;
                _chip.ToolTip = r.Message;
                AutomationProperties.SetName(_chip, "Cash out result: " + r.Chip);
            }

            System.Collections.Generic.List<CashOutEvent> hist = CashOutHistory.Get(_inst.Id);
            string sig = hist.Count + "|" + (hist.Count > 0 ? hist[0].Time.Ticks.ToString(CultureInfo.InvariantCulture) : "") + "|" + _historyOpen;
            if (sig != _historySig)
            {
                _historySig = sig;
                _historyToggle.Text = "Click history (" + hist.Count + ")  " + (_historyOpen ? "▾" : "▸");
                _historyBox.Visibility = _historyOpen ? Visibility.Visible : Visibility.Collapsed;
                if (_historyOpen)
                {
                    StringBuilder sb = new StringBuilder();
                    if (hist.Count == 0) sb.Append("No CASH OUT clicks recorded yet.");
                    for (int i = 0; i < hist.Count; i++) { if (i > 0) sb.Append('\n'); sb.Append(hist[i].Line()); }
                    _historyBody.Text = sb.ToString();
                }
            }
        }

        private void ApplyChipBrushes(CashOutResult r)
        {
            SolidColorBrush fg, bg;
            string glyph;
            switch (r.Kind)
            {
                case CashOutKind.Success: fg = Theme.Success; bg = Theme.SuccessSoft; glyph = "E73E"; break;
                case CashOutKind.Unconfirmed: fg = Theme.Warning; bg = Theme.WarningSoft; glyph = "E7BA"; break;
                default: fg = Theme.Danger; bg = Theme.DangerSoft; glyph = "EA39"; break;
            }
            _chip.Background = bg;
            _chipText.Foreground = fg;
            _chipIcon.Foreground = fg;
            _chipIcon.Text = UiUtil.GlyphIcon(glyph);
        }

        public void ApplyTheme()
        {
            _busyText.Foreground = Theme.TextMuted;
            _historyToggle.Foreground = Theme.TextMuted;
            _historyBody.Foreground = Theme.TextMuted;
            _historyBox.Background = Theme.SurfaceAlt;
            DateTime utc;
            CashOutResult r = _host.LastCashOut(_inst, out utc);
            if (_chip.Visibility == Visibility.Visible && r != null) ApplyChipBrushes(r);
        }
    }
}
