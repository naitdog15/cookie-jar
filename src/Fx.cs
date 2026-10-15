// Fx.cs
// Small interaction effects for the Cookie Jar theme: pixel-art crumb bursts and sparkles, button lift and press,
// list-row hover. Visual only. Designed from Astra's spec (sprite grids, timings and caps are its numbers).
//
//   Fx.Enabled                              false when Windows animations are off (reduced motion): every effect is skipped.
//   Fx.Burst(src, at, primary)              pixel crumb burst from a click point (src coordinates).
//   Fx.Sparkle(src, centre, cellPx, ...)    one 5x5 pixel sparkle.
//   Fx.To(transform, property, to, ms, q)   short eased animation of a transform value.
//   Fx.HoverRow(border)                     hover border and fill for a list row.
//
// Particles are drawn as aliased integer-pixel rectangles by ONE adorner per window, capped at 24 live sprites.
// The adorner only listens to CompositionTarget.Rendering while particles exist.
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using System.Windows.Media.Animation;

namespace RbxMulti
{
    internal static class Fx
    {
        private const int MaxParticles = 24;

        // '.' is transparent. O outline, c fill, S sparkle arm, H sparkle centre.
        internal static readonly string[] Crumb = new string[]
        {
            ".......",
            "..OO...",
            ".OccO..",
            ".OcccO.",
            "..OccO.",
            "...OO..",
            "......."
        };

        internal static readonly string[] Spark = new string[]
        {
            "..S..",
            "..S..",
            "SSHSS",
            "..S..",
            "..S.."
        };

        private static readonly Dictionary<string, Brush> BrushCache = new Dictionary<string, Brush>();
        private static readonly ConditionalWeakTable<UIElement, FxOverlay> Overlays = new ConditionalWeakTable<UIElement, FxOverlay>();

        public static bool Enabled
        {
            get { return SystemParameters.ClientAreaAnimation; }
        }

        private static Brush Solid(string hex)
        {
            Brush b;
            if (!BrushCache.TryGetValue(hex, out b))
            {
                SolidColorBrush s = new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex));
                s.Freeze();
                BrushCache[hex] = s;
                b = s;
            }
            return b;
        }

        internal static Brush CellBrush(char ch, bool dark)
        {
            switch (ch)
            {
                case 'O': return Solid("#95572D");
                case 'c': return Solid("#E7A96B");
                case 'X': return Solid("#35261E");
                case 'S': return Solid(dark ? "#E7A96B" : "#95572D");
                case 'H': return Solid(dark ? "#F4E0C8" : "#E7A96B");
                default: return null;
            }
        }

        /// <summary>Short eased animation of a transform value. quad: quadratic-out, otherwise cubic-out.</summary>
        public static void To(Animatable target, DependencyProperty property, double to, double ms, bool quad)
        {
            if (target == null) return;
            DoubleAnimation a = new DoubleAnimation(to, new Duration(TimeSpan.FromMilliseconds(ms)));
            if (quad) a.EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseOut };
            else a.EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut };
            target.BeginAnimation(property, a);
        }

        private static FxOverlay OverlayFor(FrameworkElement src, out UIElement root)
        {
            root = null;
            try
            {
                Window w = Window.GetWindow(src);
                if (w == null) return null;
                root = w.Content as UIElement;
                if (root == null) return null;
                FxOverlay existing;
                if (Overlays.TryGetValue(root, out existing)) return existing;
                AdornerLayer layer = AdornerLayer.GetAdornerLayer(root);
                if (layer == null) return null;
                FxOverlay created = new FxOverlay(root);
                layer.Add(created);
                Overlays.Add(root, created);
                return created;
            }
            catch (Exception)
            {
                return null;
            }
        }

        private static bool Place(FrameworkElement src, Point at, out FxOverlay overlay, out Point mapped)
        {
            mapped = at;
            UIElement root;
            overlay = OverlayFor(src, out root);
            if (overlay == null) return false;
            try
            {
                mapped = src.TransformToAncestor(root).Transform(at);
                return true;
            }
            catch (Exception)
            {
                return false;
            }
        }

        /// <summary>Pixel crumb burst from <paramref name="at"/> (in src coordinates), upward and outward.</summary>
        public static void Burst(FrameworkElement src, Point at, bool primary)
        {
            if (!Enabled || src == null) return;
            double x = Math.Max(4, Math.Min(src.ActualWidth - 4, at.X));
            double y = Math.Max(4, Math.Min(src.ActualHeight - 4, at.Y));
            FxOverlay overlay;
            Point origin;
            if (!Place(src, new Point(x, y), out overlay, out origin)) return;

            double[] angles = primary ? new double[] { -155, -120, -90, -60, -25 } : new double[] { -115, -65 };
            double[] dists = primary ? new double[] { 18, 26, 30, 26, 18 } : new double[] { 10, 12 };
            double duration = primary ? 320 : 200;
            double peak = primary ? 0.90 : 0.55;
            double fadeStart = primary ? 100 : 40;
            double drop = primary ? 18 : 7;

            for (int i = 0; i < angles.Length; i++)
            {
                double rad = angles[i] * Math.PI / 180.0;
                Particle p = new Particle();
                p.Grid = Crumb;
                p.CellPx = 1;
                p.Ox = origin.X;
                p.Oy = origin.Y;
                p.Dx = Math.Cos(rad) * dists[i];
                p.Dy = Math.Sin(rad) * dists[i];
                p.Drop = drop;
                p.Duration = duration;
                p.FadeIn = 0;
                p.HoldUntil = fadeStart;
                p.Peak = peak;
                overlay.Add(p);
            }
        }

        /// <summary>One 5x5 pixel sparkle centred on <paramref name="centre"/>. toggle: rises 4 DIP, otherwise it stays put.</summary>
        public static void Sparkle(FrameworkElement src, Point centre, int cellPx, bool toggle, double delayMs)
        {
            if (!Enabled || src == null) return;
            FxOverlay overlay;
            Point origin;
            if (!Place(src, centre, out overlay, out origin)) return;

            Particle p = new Particle();
            p.Grid = Spark;
            p.CellPx = cellPx;
            p.Ox = origin.X;
            p.Oy = origin.Y;
            p.Dx = 0;
            p.Dy = toggle ? -4 : 0;
            p.Drop = 0;
            p.Delay = delayMs;
            p.Duration = toggle ? 180 : 300;
            p.FadeIn = toggle ? 30 : 60;
            p.HoldUntil = toggle ? 30 : 160;
            p.Peak = toggle ? 0.85 : 1.0;
            overlay.Add(p);
        }

        /// <summary>List-row hover: soft fill and caramel border. A 1 DIP border is reserved at rest so nothing moves.</summary>
        public static void HoverRow(Border row)
        {
            if (row == null) return;
            Thickness p = row.Padding;
            row.Padding = new Thickness(Math.Max(0, p.Left - 1), Math.Max(0, p.Top - 1), Math.Max(0, p.Right - 1), Math.Max(0, p.Bottom - 1));
            row.BorderThickness = new Thickness(1);
            row.BorderBrush = Theme.Border;
            row.MouseEnter += delegate
            {
                row.Background = Theme.AccentSoft;
                row.BorderBrush = Theme.Accent;
            };
            row.MouseLeave += delegate
            {
                row.Background = Theme.SurfaceAlt;
                row.BorderBrush = Theme.Border;
            };
        }
    }

    internal sealed class Particle
    {
        public string[] Grid;
        public int CellPx;
        public double Ox, Oy, Dx, Dy, Drop;
        public double Delay;        // ms before it appears
        public double Duration;     // ms of life, after the delay
        public double FadeIn;       // ms to reach Peak
        public double HoldUntil;    // ms at which the fade-out starts
        public double Peak;
        public double StartMs;
    }

    /// <summary>One overlay per window that draws every live sprite as aliased pixel rectangles.</summary>
    internal sealed class FxOverlay : Adorner
    {
        private readonly List<Particle> _live = new List<Particle>();
        private readonly Stopwatch _clock = Stopwatch.StartNew();
        private bool _hooked;

        public FxOverlay(UIElement adorned) : base(adorned)
        {
            IsHitTestVisible = false;
            RenderOptions.SetEdgeMode(this, EdgeMode.Aliased);
        }

        public void Add(Particle p)
        {
            if (_live.Count >= 24) return;
            p.StartMs = _clock.Elapsed.TotalMilliseconds + p.Delay;
            _live.Add(p);
            if (!_hooked)
            {
                CompositionTarget.Rendering += OnFrame;
                _hooked = true;
            }
            InvalidateVisual();
        }

        private void OnFrame(object sender, EventArgs e)
        {
            double now = _clock.Elapsed.TotalMilliseconds;
            _live.RemoveAll(delegate(Particle p) { return now - p.StartMs >= p.Duration; });
            InvalidateVisual();
            if (_live.Count == 0 && _hooked)
            {
                CompositionTarget.Rendering -= OnFrame;
                _hooked = false;
            }
        }

        private double DeviceScale()
        {
            try
            {
                PresentationSource ps = PresentationSource.FromVisual(this);
                if (ps != null && ps.CompositionTarget != null) return ps.CompositionTarget.TransformToDevice.M11;
            }
            catch (Exception)
            {
            }
            return 1.0;
        }

        protected override void OnRender(DrawingContext dc)
        {
            if (_live.Count == 0) return;
            double now = _clock.Elapsed.TotalMilliseconds;
            double scale = DeviceScale();
            bool dark = Theme.IsDark;

            foreach (Particle p in _live)
            {
                double t = now - p.StartMs;
                if (t < 0 || t >= p.Duration) continue;
                double u = t / p.Duration;
                double e = 1.0 - Math.Pow(1.0 - u, 3.0);
                double x = p.Ox + p.Dx * e;
                double y = p.Oy + p.Dy * e + p.Drop * u * u;

                double opacity;
                if (t < p.FadeIn) opacity = p.Peak * (t / p.FadeIn);
                else if (t < p.HoldUntil) opacity = p.Peak;
                else opacity = p.Peak * (1.0 - (t - p.HoldUntil) / (p.Duration - p.HoldUntil));
                if (opacity <= 0) continue;

                double cell = p.CellPx / scale;
                int rows = p.Grid.Length;
                int cols = p.Grid[0].Length;
                double left = Math.Round((x - cols * cell / 2.0) * scale) / scale;
                double top = Math.Round((y - rows * cell / 2.0) * scale) / scale;

                dc.PushOpacity(opacity);
                for (int r = 0; r < rows; r++)
                {
                    string row = p.Grid[r];
                    for (int c = 0; c < cols; c++)
                    {
                        Brush b = Fx.CellBrush(row[c], dark);
                        if (b == null) continue;
                        dc.DrawRectangle(b, null, new Rect(left + c * cell, top + r * cell, cell, cell));
                    }
                }
                dc.Pop();
            }
        }
    }
}
