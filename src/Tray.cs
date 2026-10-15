// Tray.cs
// Notification-area icon (TrayIcon) and the themed exit confirmation dialog (ExitDialog).
//
// TrayIcon(open, toggleMiniBar, isMiniBarShown, bringAll, exit)
//     Left or double click = open. Right click menu: Open, Show/Hide mini bar, Bring all to front, Exit.
//     SetRunning(n)  tooltip "Cookie Jar - N running".
//     ShowBalloon()  one-time "still running in the tray" hint.
//     ApplyTheme()   re-colours the menu for the current Theme.
//     Dispose()      removes the icon from the tray (no ghost icon) and frees the generated icon.
// ExitDialog.Ask(text)  returns GoBack (default), ExitLeave or ExitStop.

using System;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Drw = System.Drawing;
using Wf = System.Windows.Forms;

namespace RbxMulti
{
    internal sealed class TrayIcon : IDisposable
    {
        private readonly Wf.NotifyIcon _ni;
        private readonly Wf.ContextMenuStrip _menu;
        private readonly Wf.ToolStripMenuItem _mini;
        private readonly Func<bool> _isMiniShown;
        private Drw.Icon _icon;
        private IntPtr _hicon;
        private bool _disposed;

        [DllImport("user32.dll")]
        private static extern bool DestroyIcon(IntPtr handle);

        public TrayIcon(Action open, Action toggleMini, Func<bool> isMiniShown, Action bringAll, Action exit, Action stopAll)
        {
            _isMiniShown = isMiniShown;
            _menu = new Wf.ContextMenuStrip();
            Wf.ToolStripMenuItem openItem = new Wf.ToolStripMenuItem("Open");
            openItem.Click += delegate { Safe(open); };
            _mini = new Wf.ToolStripMenuItem("Show mini bar");
            _mini.Click += delegate { Safe(toggleMini); };
            Wf.ToolStripMenuItem bring = new Wf.ToolStripMenuItem("Bring all to front");
            bring.Click += delegate { Safe(bringAll); };
            Wf.ToolStripMenuItem exitItem = new Wf.ToolStripMenuItem("Exit");
            exitItem.Click += delegate { Safe(exit); };
            _menu.Items.Add(openItem);
            _menu.Items.Add(_mini);
            _menu.Items.Add(bring);
            Wf.ToolStripMenuItem stopItem = new Wf.ToolStripMenuItem("Stop all instances");
            stopItem.ToolTipText = "Ends every Roblox client and its child processes immediately (asks first)";
            stopItem.Click += delegate { Safe(stopAll); };
            _menu.Items.Add(stopItem);
            _menu.Items.Add(new Wf.ToolStripSeparator());
            _menu.Items.Add(exitItem);
            _menu.Opening += delegate
            {
                bool shown = false;
                try { shown = _isMiniShown(); } catch (Exception) { }
                _mini.Text = shown ? "Hide mini bar" : "Show mini bar";
            };
            ApplyTheme();

            _icon = BuildIcon(out _hicon);
            _ni = new Wf.NotifyIcon();
            _ni.Icon = _icon;
            _ni.Text = "Cookie Jar";
            _ni.ContextMenuStrip = _menu;
            _ni.MouseClick += delegate(object s, Wf.MouseEventArgs e)
            {
                if (e.Button == Wf.MouseButtons.Left) Safe(open);
            };
            _ni.DoubleClick += delegate { Safe(open); };
            _ni.Visible = true;
        }

        private static void Safe(Action a)
        {
            try { a(); } catch (Exception) { }
        }

        public void SetRunning(int n)
        {
            if (_disposed) return;
            try { _ni.Text = "Cookie Jar - " + n.ToString(System.Globalization.CultureInfo.InvariantCulture) + " running"; }
            catch (Exception) { }
        }

        public void ShowBalloon()
        {
            if (_disposed) return;
            try
            {
                _ni.ShowBalloonTip(6000, "Cookie Jar",
                    "Cookie Jar is still running in the notification area - right-click the icon and choose Exit to quit", Wf.ToolTipIcon.Info);
            }
            catch (Exception) { }
        }

        private static Drw.Color C(SolidColorBrush b)
        {
            Color c = b.Color;
            return Drw.Color.FromArgb(c.R, c.G, c.B);
        }

        private sealed class MenuColors : Wf.ProfessionalColorTable
        {
            public override Drw.Color MenuItemSelected { get { return C(Theme.AccentSoft); } }
            public override Drw.Color MenuItemBorder { get { return C(Theme.Border); } }
            public override Drw.Color MenuBorder { get { return C(Theme.Border); } }
            public override Drw.Color ToolStripDropDownBackground { get { return C(Theme.Surface); } }
            public override Drw.Color ImageMarginGradientBegin { get { return C(Theme.Surface); } }
            public override Drw.Color ImageMarginGradientMiddle { get { return C(Theme.Surface); } }
            public override Drw.Color ImageMarginGradientEnd { get { return C(Theme.Surface); } }
            public override Drw.Color SeparatorDark { get { return C(Theme.Border); } }
            public override Drw.Color SeparatorLight { get { return C(Theme.Border); } }
        }

        public void ApplyTheme()
        {
            try
            {
                _menu.Renderer = new Wf.ToolStripProfessionalRenderer(new MenuColors());
                _menu.BackColor = C(Theme.Surface);
                _menu.ForeColor = C(Theme.Text);
                foreach (Wf.ToolStripItem it in _menu.Items)
                {
                    it.BackColor = C(Theme.Surface);
                    it.ForeColor = C(Theme.Text);
                }
            }
            catch (Exception) { }
        }

        private static Drw.Icon BuildIcon(out IntPtr hicon)
        {
            hicon = IntPtr.Zero;
            try
            {
                using (Drw.Bitmap bmp = new Drw.Bitmap(32, 32))
                using (Drw.Graphics g = Drw.Graphics.FromImage(bmp))
                {
                    g.SmoothingMode = Drw.Drawing2D.SmoothingMode.AntiAlias;
                    g.TextRenderingHint = Drw.Text.TextRenderingHint.AntiAliasGridFit;
                    g.InterpolationMode = Drw.Drawing2D.InterpolationMode.HighQualityBicubic;
                    using (Drw.Bitmap logo = LogoArt.RenderBitmap(32))
                        g.DrawImage(logo, 0, 0, 32, 32);
                    hicon = bmp.GetHicon();
                }
                return Drw.Icon.FromHandle(hicon);
            }
            catch (Exception)
            {
                return Drw.SystemIcons.Application;
            }
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            try { _ni.Visible = false; _ni.Dispose(); } catch (Exception) { }
            try { _menu.Dispose(); } catch (Exception) { }
            try { if (_hicon != IntPtr.Zero) DestroyIcon(_hicon); } catch (Exception) { }
        }
    }

    internal enum ExitChoice
    {
        GoBack,
        ExitLeave,
        ExitStop
    }

    /// <summary>Themed confirmation shown when something is still running. Go back is the default.</summary>
    internal sealed class ExitDialog : Window
    {
        private ExitChoice _choice = ExitChoice.GoBack;

        public static ExitChoice Ask(string details)
        {
            ExitDialog d = new ExitDialog(details);
            d.ShowDialog();
            return d._choice;
        }

        private ExitDialog(string details)
        {
            Title = "Exit Cookie Jar";
            Width = 460;
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
            body.Margin = new Thickness(22, 20, 22, 20);
            TextBlock head = UiUtil.Label("Exit the launcher?", 18, true, Theme.Text);
            body.Children.Add(head);
            TextBlock text = UiUtil.Label(details + "\n\n" + "Exit and stop all instances ends every Roblox client and its child processes immediately (no in-game Leave prompt). Unsaved in-game progress may be lost and any trade or action in progress will be cut off.", 13, false, Theme.TextMuted);
            text.TextWrapping = TextWrapping.Wrap;
            text.Margin = new Thickness(0, 8, 0, 16);
            body.Children.Add(text);

            Button stop = PillButton.Create("Exit and stop all instances", PillStyle.Danger);
            stop.Click += delegate { _choice = ExitChoice.ExitStop; Close(); };
            Button leave = PillButton.Create("Exit and leave Roblox running", PillStyle.Secondary);
            leave.Click += delegate { _choice = ExitChoice.ExitLeave; Close(); };
            Button back = PillButton.Create("Go back", PillStyle.Primary);
            back.IsDefault = true;
            back.IsCancel = true;
            back.Click += delegate { _choice = ExitChoice.GoBack; Close(); };
            foreach (Button b in new Button[] { stop, leave, back })
            {
                b.Margin = new Thickness(0, 0, 0, 8);
                b.HorizontalAlignment = HorizontalAlignment.Stretch;
                body.Children.Add(b);
            }
            Content = body;
            Loaded += delegate { Theme.ApplyTitleBar(this); back.Focus(); };
        }
    }
}
