// CookieJarLogo.cs
// The Cookie Jar logo drawn as WPF vector paths (64 x 64 grid), plus the startup splash that animates it.
// Visual only: nothing here touches launcher logic. C# 5, code-only UI.
using System;
using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using System.Windows.Threading;
using Drw = System.Drawing;

namespace RbxMulti
{
    /// <summary>The layers of the logo, kept separate so the splash can animate the cookie and the lid.</summary>
    internal sealed class LogoParts
    {
        public Canvas Root;
        public Canvas Cookie;
        public Canvas Lid;
        public TranslateTransform CookieMove = new TranslateTransform();
        public TranslateTransform LidMove = new TranslateTransform();
    }

    internal static class LogoArt
    {
        private const string JarOuter = "M18,20 L46,20 L46,25 C46,28 54,29 54,37 L54,47 Q54,57 44,57 L20,57 Q10,57 10,47 L10,37 C10,29 18,28 18,25 Z";
        private const string JarInner = "M21,23 L43,23 L43,26 C43,30 51,31 51,37 L51,47 Q51,54 44,54 L20,54 Q13,54 13,47 L13,37 C13,31 21,30 21,26 Z";
        private const string CookieOutline = "M43,40 A11,11 0 1 1 21,40 A11,11 0 1 1 43,40 Z";
        private const string CookieFill = "M41,40 A9,9 0 1 1 23,40 A9,9 0 1 1 41,40 Z";
        private const string Chip1 = "M30,36 A2,2 0 1 1 26,36 A2,2 0 1 1 30,36 Z";
        private const string Chip2 = "M38,39 A2,2 0 1 1 34,39 A2,2 0 1 1 38,39 Z";
        private const string Chip3 = "M32,45 A2,2 0 1 1 28,45 A2,2 0 1 1 32,45 Z";
        private const string Handle = "M26,14 L26,11 Q26,8 29,8 L35,8 Q38,8 38,11 L38,14 Z";
        private const string LidBody = "M17,13 L47,13 Q51,13 51,17 L51,19 Q51,23 47,23 L17,23 Q13,23 13,19 L13,17 Q13,13 17,13 Z";
        private const string LidInset = "M19,16 L45,16 Q46,16 46,17 Q46,18 45,18 L19,18 Q18,18 18,17 Q18,16 19,16 Z";

        private static Path P(string data, Color fill)
        {
            Path p = new Path();
            p.Data = Geometry.Parse(data);
            p.Fill = new SolidColorBrush(fill);
            return p;
        }

        private static Color Rgb(int r, int g, int b)
        {
            return Color.FromRgb((byte)r, (byte)g, (byte)b);
        }

        /// <summary>Builds the 64 x 64 logo. Dark mode swaps the jar and lid to the light caramel.</summary>
        public static LogoParts Build(bool dark)
        {
            Color line = dark ? Rgb(0xE7, 0xA9, 0x6B) : Rgb(0x95, 0x57, 0x2D);
            Color inner = dark ? Rgb(0x2B, 0x23, 0x1E) : Rgb(0xF4, 0xE0, 0xC8);
            Color cookie = Rgb(0xE7, 0xA9, 0x6B);
            Color chip = Rgb(0x35, 0x26, 0x1E);

            LogoParts parts = new LogoParts();
            parts.Root = new Canvas();
            parts.Root.Width = 64;
            parts.Root.Height = 64;

            parts.Root.Children.Add(P(JarOuter, line));
            parts.Root.Children.Add(P(JarInner, inner));

            parts.Cookie = new Canvas();
            parts.Cookie.Width = 64;
            parts.Cookie.Height = 64;
            parts.Cookie.RenderTransform = parts.CookieMove;
            parts.Cookie.Children.Add(P(CookieOutline, line));
            parts.Cookie.Children.Add(P(CookieFill, cookie));
            parts.Cookie.Children.Add(P(Chip1, chip));
            parts.Cookie.Children.Add(P(Chip2, chip));
            parts.Cookie.Children.Add(P(Chip3, chip));
            parts.Root.Children.Add(parts.Cookie);

            parts.Lid = new Canvas();
            parts.Lid.Width = 64;
            parts.Lid.Height = 64;
            parts.Lid.RenderTransform = parts.LidMove;
            parts.Lid.Children.Add(P(Handle, line));
            parts.Lid.Children.Add(P(LidBody, line));
            parts.Lid.Children.Add(P(LidInset, inner));
            parts.Root.Children.Add(parts.Lid);
            return parts;
        }

        /// <summary>Renders the light-mode logo to a GDI+ bitmap (used for the tray icon). Call on the UI thread.</summary>
        public static Drw.Bitmap RenderBitmap(int px)
        {
            LogoParts parts = Build(false);
            Viewbox box = new Viewbox();
            box.Stretch = Stretch.Uniform;
            box.Width = px;
            box.Height = px;
            box.Child = parts.Root;
            box.Measure(new Size(px, px));
            box.Arrange(new Rect(0, 0, px, px));
            box.UpdateLayout();

            RenderTargetBitmap rtb = new RenderTargetBitmap(px, px, 96, 96, PixelFormats.Pbgra32);
            rtb.Render(box);
            PngBitmapEncoder enc = new PngBitmapEncoder();
            enc.Frames.Add(BitmapFrame.Create(rtb));
            using (System.IO.MemoryStream ms = new System.IO.MemoryStream())
            {
                enc.Save(ms);
                ms.Position = 0;
                using (Drw.Bitmap tmp = new Drw.Bitmap(ms))
                {
                    return new Drw.Bitmap(tmp);
                }
            }
        }
    }

    /// <summary>The logo as a control. Follows light and dark mode live.</summary>
    internal sealed class CookieJarLogo : Viewbox
    {
        private readonly Action _onTheme;

        public CookieJarLogo(double size)
        {
            Width = size;
            Height = size;
            Stretch = Stretch.Uniform;
            SnapsToDevicePixels = false;
            _onTheme = delegate { Dispatcher.BeginInvoke(new Action(Rebuild)); };
            Rebuild();
            Loaded += delegate { Theme.Changed += _onTheme; Rebuild(); };
            Unloaded += delegate { Theme.Changed -= _onTheme; };
        }

        private void Rebuild()
        {
            Child = LogoArt.Build(Theme.IsDark).Root;
        }
    }

    /// <summary>
    /// Startup splash: the cookie drops into the jar, the lid bounces on, then three crumb dots pulse.
    /// It owns its palette (from the Windows light/dark setting) so it never touches Theme state.
    /// </summary>
    internal sealed class SplashWindow : Window
    {
        public static SplashWindow TryShow()
        {
            try
            {
                SplashWindow w = new SplashWindow();
                w.Show();
                return w;
            }
            catch (Exception)
            {
                return null;
            }
        }

        private SplashWindow()
        {
            bool dark = Theme.SystemPrefersDark();
            Color bg = dark ? Color.FromRgb(0x21, 0x1A, 0x16) : Color.FromRgb(0xFF, 0xF8, 0xED);
            Color border = dark ? Color.FromRgb(0x51, 0x41, 0x37) : Color.FromRgb(0xE5, 0xD3, 0xBC);
            Color text = dark ? Color.FromRgb(0xFF, 0xF7, 0xED) : Color.FromRgb(0x35, 0x26, 0x1E);
            Color accent = dark ? Color.FromRgb(0xE7, 0xA9, 0x6B) : Color.FromRgb(0x95, 0x57, 0x2D);

            WindowStyle = WindowStyle.None;
            AllowsTransparency = true;
            Background = Brushes.Transparent;
            ResizeMode = ResizeMode.NoResize;
            ShowInTaskbar = false;
            Topmost = true;
            Width = 300;
            Height = 320;
            WindowStartupLocation = WindowStartupLocation.CenterScreen;
            Title = "Cookie Jar";

            // Stage 96 x 112: the logo sits at (16,32) so the cookie has room to drop in from above.
            Canvas stage = new Canvas();
            stage.Width = 96;
            stage.Height = 112;
            LogoParts parts = LogoArt.Build(dark);
            Canvas.SetLeft(parts.Root, 16);
            Canvas.SetTop(parts.Root, 32);
            stage.Children.Add(parts.Root);

            Ellipse[] dots = new Ellipse[3];
            double[] cx = new double[] { 40, 48, 56 };
            for (int i = 0; i < 3; i++)
            {
                Ellipse d = new Ellipse();
                d.Width = 3;
                d.Height = 3;
                d.Fill = new SolidColorBrush(accent);
                d.Opacity = 0.30;
                Canvas.SetLeft(d, cx[i] - 1.5);
                Canvas.SetTop(d, 99 - 1.5);
                stage.Children.Add(d);
                dots[i] = d;
            }
            stage.LayoutTransform = new ScaleTransform(1.6, 1.6);

            TextBlock caption = new TextBlock();
            caption.Text = "Getting things ready…";
            caption.FontFamily = Theme.UiFont;
            caption.FontSize = 14;
            caption.Foreground = new SolidColorBrush(text);
            caption.HorizontalAlignment = HorizontalAlignment.Center;
            caption.Margin = new Thickness(0, 8, 0, 0);

            StackPanel stack = new StackPanel();
            stack.VerticalAlignment = VerticalAlignment.Center;
            stack.HorizontalAlignment = HorizontalAlignment.Center;
            stack.Children.Add(stage);
            stack.Children.Add(caption);

            Border card = new Border();
            card.Margin = new Thickness(8);
            card.CornerRadius = new CornerRadius(18);
            card.Background = new SolidColorBrush(bg);
            card.BorderBrush = new SolidColorBrush(border);
            card.BorderThickness = new Thickness(1);
            card.Child = stack;
            Content = card;

            if (SystemParameters.ClientAreaAnimation)
            {
                Animate(parts, dots);
            }
            else
            {
                foreach (Ellipse d in dots) d.Opacity = 0.60;
            }
        }

        private static DoubleAnimation Anim(double from, double to, double begin, double seconds, IEasingFunction ease)
        {
            DoubleAnimation a = new DoubleAnimation(from, to, new Duration(TimeSpan.FromSeconds(seconds)));
            a.BeginTime = TimeSpan.FromSeconds(begin);
            a.EasingFunction = ease;
            a.FillBehavior = FillBehavior.HoldEnd;
            return a;
        }

        private void Animate(LogoParts parts, Ellipse[] dots)
        {
            // Start state: cookie high and invisible, lid lifted aside.
            parts.Cookie.Opacity = 0;
            parts.CookieMove.Y = -42;
            parts.LidMove.X = 18;
            parts.LidMove.Y = -16;

            parts.Cookie.BeginAnimation(UIElement.OpacityProperty, Anim(0, 1, 0.00, 0.12, null));
            parts.CookieMove.BeginAnimation(TranslateTransform.YProperty,
                Anim(-42, 0, 0.12, 0.48, new CubicEase { EasingMode = EasingMode.EaseIn }));
            parts.LidMove.BeginAnimation(TranslateTransform.XProperty,
                Anim(18, 0, 0.62, 0.22, new QuadraticEase { EasingMode = EasingMode.EaseInOut }));
            parts.LidMove.BeginAnimation(TranslateTransform.YProperty,
                Anim(-16, 0, 0.62, 0.50, new BounceEase { EasingMode = EasingMode.EaseOut, Bounces = 2, Bounciness = 3 }));

            // Crumb dots: a 1.2 s loop that starts once the lid has settled.
            Storyboard loop = new Storyboard();
            loop.BeginTime = TimeSpan.FromSeconds(1.2);
            loop.Duration = new Duration(TimeSpan.FromSeconds(1.2));
            loop.RepeatBehavior = RepeatBehavior.Forever;
            for (int i = 0; i < dots.Length; i++)
            {
                DoubleAnimation pulse = new DoubleAnimation(0.30, 1.0, new Duration(TimeSpan.FromSeconds(0.30)));
                pulse.AutoReverse = true;
                pulse.BeginTime = TimeSpan.FromSeconds(0.15 * i);
                pulse.EasingFunction = new SineEase { EasingMode = EasingMode.EaseInOut };
                pulse.FillBehavior = FillBehavior.Stop;
                Storyboard.SetTarget(pulse, dots[i]);
                Storyboard.SetTargetProperty(pulse, new PropertyPath(UIElement.OpacityProperty));
                loop.Children.Add(pulse);
            }
            loop.Begin(this);
        }

        /// <summary>Keeps the splash up until at least <paramref name="minimum"/> has passed since <paramref name="clock"/> started, then closes it.</summary>
        public void CloseAfter(Stopwatch clock, TimeSpan minimum)
        {
            try
            {
                TimeSpan left = minimum - clock.Elapsed;
                if (left > TimeSpan.Zero)
                {
                    DispatcherFrame frame = new DispatcherFrame();
                    DispatcherTimer timer = new DispatcherTimer();
                    timer.Interval = left;
                    timer.Tick += delegate { timer.Stop(); frame.Continue = false; };
                    timer.Start();
                    Dispatcher.PushFrame(frame);
                }
            }
            catch (Exception)
            {
                // Never let the splash delay or block startup.
            }
            try { Close(); } catch (Exception) { }
        }
    }
}
