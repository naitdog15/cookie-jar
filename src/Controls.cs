// Controls.cs - reusable, theme-aware WPF controls built entirely in code (C# 5, .NET 4.x).
// All colours come from the static Theme class. Brush instances are never replaced, so
// controls that assign Theme brushes directly update live when the theme changes.

using System;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Markup;
using System.Windows.Media.Effects;
using System.Windows.Shapes;

namespace RbxMulti
{
    /// <summary>Visual intent of a <see cref="PillButton"/>.</summary>
    public enum PillStyle
    {
        Primary,
        Secondary,
        Danger
    }

    /// <summary>
    /// Windows 11 style pill toggle (40x20 track, 14px knob). The knob slides
    /// over 150ms when IsChecked changes. Checked/Unchecked are inherited from ToggleButton.
    /// </summary>
    public class ToggleSwitch : ToggleButton
    {
        private const double TrackWidth = 40;
        private const double TrackHeight = 20;
        private const double KnobSize = 14;
        private const double KnobInset = 2;   // gap between knob and inner track edge
        private const double TrackBorder = 1;
        // Distance the knob travels from off to on position.
        private const double KnobTravel = TrackWidth - 2 * TrackBorder - 2 * KnobInset - KnobSize;

        private TranslateTransform _knobTransform;
        private Border _track, _focus;
        private Ellipse _knob;

        private void UpdateVisuals()
        {
            bool on = IsChecked == true;
            if (_track != null)
            {
                _track.Background = on ? Theme.Accent : Theme.SurfaceAlt;
                _track.BorderBrush = on ? Theme.Accent : Theme.Border;
            }
            if (_knob != null) _knob.Fill = on ? Theme.OnAccent : Theme.Text;
            if (_focus != null) _focus.Visibility = IsKeyboardFocused ? Visibility.Visible : Visibility.Collapsed;
        }

        protected override void OnGotKeyboardFocus(KeyboardFocusChangedEventArgs e) { base.OnGotKeyboardFocus(e); UpdateVisuals(); }
        protected override void OnLostKeyboardFocus(KeyboardFocusChangedEventArgs e) { base.OnLostKeyboardFocus(e); UpdateVisuals(); }

        public ToggleSwitch()
        {
            Width = TrackWidth;
            Height = TrackHeight;
            Cursor = Cursors.Hand;
            Focusable = true;
            Background = Brushes.Transparent;
            BorderThickness = new Thickness(0);
            Padding = new Thickness(0);
            Template = BuildTemplate();
        }

        private static ControlTemplate BuildTemplate()
        {
            var root = new FrameworkElementFactory(typeof(Grid));

            // Track: filled with SurfaceAlt when off, Accent when on.
            var track = new FrameworkElementFactory(typeof(Border));
            track.Name = "PART_Track";
            track.SetValue(FrameworkElement.WidthProperty, TrackWidth);
            track.SetValue(FrameworkElement.HeightProperty, TrackHeight);
            track.SetValue(Border.CornerRadiusProperty, new CornerRadius(TrackHeight / 2));
            track.SetValue(Border.BorderThicknessProperty, new Thickness(TrackBorder));

            // Knob: positioned at the left; animated with a TranslateTransform in code.
            var knob = new FrameworkElementFactory(typeof(Ellipse));
            knob.Name = "PART_Knob";
            knob.SetValue(FrameworkElement.WidthProperty, KnobSize);
            knob.SetValue(FrameworkElement.HeightProperty, KnobSize);
            knob.SetValue(FrameworkElement.HorizontalAlignmentProperty, HorizontalAlignment.Left);
            knob.SetValue(FrameworkElement.VerticalAlignmentProperty, VerticalAlignment.Center);
            knob.SetValue(FrameworkElement.MarginProperty, new Thickness(KnobInset, 0, 0, 0));
            track.AppendChild(knob);
            root.AppendChild(track);

            // Keyboard focus ring, hidden unless the control has keyboard focus.
            var focus = new FrameworkElementFactory(typeof(Border));
            focus.Name = "PART_Focus";
            focus.SetValue(FrameworkElement.WidthProperty, TrackWidth + 4);
            focus.SetValue(FrameworkElement.HeightProperty, TrackHeight + 4);
            focus.SetValue(Border.CornerRadiusProperty, new CornerRadius((TrackHeight + 4) / 2));
            focus.SetValue(Border.BorderThicknessProperty, new Thickness(1));
            focus.SetValue(UIElement.VisibilityProperty, Visibility.Collapsed);
            root.AppendChild(focus);

            var ct = new ControlTemplate(typeof(ToggleButton)) { VisualTree = root };

            var disabledTrigger = new Trigger { Property = UIElement.IsEnabledProperty, Value = false };
            disabledTrigger.Setters.Add(new Setter(UIElement.OpacityProperty, 0.45));
            ct.Triggers.Add(disabledTrigger);

            return ct;
        }

        public override void OnApplyTemplate()
        {
            base.OnApplyTemplate();
            // Brushes are applied here, not in the template: templates freeze Freezables,
            // which would freeze the shared Theme brushes.
            _track = GetTemplateChild("PART_Track") as Border;
            _focus = GetTemplateChild("PART_Focus") as Border;
            var knob = GetTemplateChild("PART_Knob") as Ellipse;
            _knob = knob;
            if (_focus != null) _focus.BorderBrush = Theme.Accent;
            UpdateVisuals();
            if (knob == null) return;

            // Fresh transform each time the template is applied; start at the correct position.
            _knobTransform = new TranslateTransform(IsChecked == true ? KnobTravel : 0, 0);
            knob.RenderTransform = _knobTransform;
        }

        // True only while a click or key press toggles the switch, so loading saved settings stays silent.
        private bool _userToggle;

        protected override void OnToggle()
        {
            _userToggle = true;
            try { base.OnToggle(); }
            finally { _userToggle = false; }
        }

        protected override void OnChecked(RoutedEventArgs e)
        {
            base.OnChecked(e);
            UpdateVisuals();
            AnimateKnob(KnobTravel);
            if (_userToggle && IsLoaded)
            {
                // One pixel sparkle just above the knob once it has arrived.
                double knobCentreX = TrackBorder + KnobInset + KnobTravel + KnobSize / 2;
                double knobTop = (TrackHeight - KnobSize) / 2;
                Fx.Sparkle(this, new Point(knobCentreX, knobTop - 4), 1, true, 140);
            }
        }

        protected override void OnUnchecked(RoutedEventArgs e)
        {
            base.OnUnchecked(e);
            UpdateVisuals();
            AnimateKnob(0);
        }

        private void AnimateKnob(double to)
        {
            if (_knobTransform == null) return;
            var anim = new DoubleAnimation(to, new Duration(TimeSpan.FromMilliseconds(150)))
            {
                EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
            };
            _knobTransform.BeginAnimation(TranslateTransform.XProperty, anim);
        }
    }

    /// <summary>
    /// Rounded (9px) button with Primary, Secondary and Danger looks. Hover and pressed
    /// states are driven in code by swapping between Theme brush instances.
    /// </summary>
    public class PillButton : Button
    {
        private readonly SolidColorBrush _normal;
        private readonly SolidColorBrush _hover;
        private bool _pressed;

        // Interaction effects (see Fx.cs): lift on hover, squash on press, pixel sparkle and crumb burst.
        private readonly bool _primary;
        private readonly ScaleTransform _scale = new ScaleTransform(1, 1);
        private readonly TranslateTransform _lift = new TranslateTransform(0, 0);
        private System.Windows.Threading.DispatcherTimer _sparkleTimer;
        private Point _lastPos;
        private bool _hasPos;
        private int _lastBurst;

        private PillButton(PillStyle style)
        {
            _primary = style == PillStyle.Primary;
            FontFamily = Theme.UiFont;
            FontSize = 13;
            FontWeight = FontWeights.SemiBold;
            Cursor = Cursors.Hand;
            MinWidth = 80;
            MinHeight = 36;
            Padding = new Thickness(16, 8, 16, 8);
            BorderThickness = new Thickness(1);
            // Keep tooltips visible on disabled buttons so the user learns why they are off.
            ToolTipService.SetShowOnDisabled(this, true);

            switch (style)
            {
                case PillStyle.Primary:
                    _normal = Theme.Accent;
                    MinHeight = 40;
                    _hover = Theme.AccentHover;
                    Foreground = Theme.OnAccent;
                    BorderBrush = Theme.Accent;
                    break;
                case PillStyle.Danger:
                    _normal = Theme.DangerSoft;
                    _hover = Theme.DangerHover;
                    Foreground = Theme.Danger;
                    BorderBrush = Theme.Danger;
                    break;
                default:
                    _normal = Theme.Surface;
                    _hover = Theme.SurfaceAlt;
                    Foreground = Theme.Text;
                    BorderBrush = Theme.Border;
                    break;
            }

            Background = _normal;
            Template = BuildTemplate();

            RenderTransformOrigin = new Point(0.5, 0.5);
            TransformGroup fx = new TransformGroup();
            fx.Children.Add(_scale);
            fx.Children.Add(_lift);
            RenderTransform = fx;
            Unloaded += (s, e) => CancelSparkle();
            IsEnabledChanged += (s, e) =>
            {
                Refresh();
                if (!IsEnabled)
                {
                    CancelSparkle();
                    SettleTransforms();
                }
            };
        }

        // Returns the squash and lift to their resting values (lifted 1px while the pointer is over an enabled button).
        private void SettleTransforms()
        {
            if (!Fx.Enabled) return;
            bool hot = IsEnabled && IsMouseOver;
            Fx.To(_scale, ScaleTransform.ScaleXProperty, 1.0, 100, false);
            Fx.To(_scale, ScaleTransform.ScaleYProperty, 1.0, 100, false);
            Fx.To(_lift, TranslateTransform.YProperty, hot ? -1.0 : 0.0, hot ? 100 : 120, false);
        }

        private void CancelSparkle()
        {
            if (_sparkleTimer != null) _sparkleTimer.Stop();
        }

        // Primary buttons show one small pixel sparkle after the pointer has rested on them for 180 ms.
        private void StartSparkle()
        {
            if (!_primary || !Fx.Enabled) return;
            if (_sparkleTimer == null)
            {
                _sparkleTimer = new System.Windows.Threading.DispatcherTimer();
                _sparkleTimer.Interval = TimeSpan.FromMilliseconds(180);
                _sparkleTimer.Tick += (s, e) =>
                {
                    _sparkleTimer.Stop();
                    if (IsMouseOver && IsEnabled && !_pressed)
                    {
                        Fx.Sparkle(this, new Point(ActualWidth - 7, 7), 2, false, 0);
                    }
                };
            }
            _sparkleTimer.Stop();
            _sparkleTimer.Start();
        }

        protected override void OnClick()
        {
            // Burst first so the effect still plays if a Click handler throws or tears this button down.
            try
            {
                int now = Environment.TickCount;
                if (IsEnabled && Fx.Enabled && unchecked(now - _lastBurst) > 150)
                {
                    _lastBurst = now;
                    Point at = _hasPos ? _lastPos : new Point(ActualWidth / 2, ActualHeight / 2);
                    Fx.Burst(this, at, _primary);
                }
            }
            catch (Exception)
            {
            }
            _hasPos = false;
            base.OnClick();
        }

        /// <summary>Creates a pill button with the given text and style.</summary>
        public static PillButton Create(string text, PillStyle style)
        {
            var b = new PillButton(style);
            b.Content = text;
            return b;
        }

        private static ControlTemplate BuildTemplate()
        {
            var border = new FrameworkElementFactory(typeof(Border));
            border.Name = "PART_Bg";
            border.SetValue(Border.CornerRadiusProperty, new CornerRadius(9));
            border.SetValue(Border.BackgroundProperty, new TemplateBindingExtension(Control.BackgroundProperty));
            border.SetValue(Border.BorderBrushProperty, new TemplateBindingExtension(Control.BorderBrushProperty));
            border.SetValue(Border.BorderThicknessProperty, new TemplateBindingExtension(Control.BorderThicknessProperty));
            border.SetValue(Border.PaddingProperty, new TemplateBindingExtension(Control.PaddingProperty));

            var content = new FrameworkElementFactory(typeof(ContentPresenter));
            content.SetValue(FrameworkElement.HorizontalAlignmentProperty, HorizontalAlignment.Center);
            content.SetValue(FrameworkElement.VerticalAlignmentProperty, VerticalAlignment.Center);
            content.SetValue(ContentPresenter.RecognizesAccessKeyProperty, true);
            border.AppendChild(content);

            return new ControlTemplate(typeof(Button)) { VisualTree = border };
        }

        // Applies the current interaction state to Background and Opacity.
        private void Refresh()
        {
            if (!IsEnabled)
            {
                Background = _normal;   // no hover while disabled
                Opacity = 0.45;
                return;
            }

            Opacity = _pressed ? 0.94 : 1.0;
            Background = (_pressed || IsMouseOver) ? _hover : _normal;
        }

        protected override void OnMouseEnter(MouseEventArgs e)
        {
            base.OnMouseEnter(e);
            Refresh();
            if (IsEnabled && Fx.Enabled)
            {
                Fx.To(_lift, TranslateTransform.YProperty, -1.0, 100, false);
                StartSparkle();
            }
        }

        protected override void OnMouseLeave(MouseEventArgs e)
        {
            base.OnMouseLeave(e);
            _pressed = false;
            Refresh();
            CancelSparkle();
            SettleTransforms();
        }

        protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
        {
            base.OnMouseLeftButtonDown(e);
            _pressed = true;
            Refresh();
            if (IsEnabled && Fx.Enabled)
            {
                CancelSparkle();
                Fx.To(_scale, ScaleTransform.ScaleXProperty, 0.98, 60, true);
                Fx.To(_scale, ScaleTransform.ScaleYProperty, 0.98, 60, true);
                Fx.To(_lift, TranslateTransform.YProperty, 0.0, 60, true);
            }
        }

        protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e)
        {
            // Remember where the click landed; base raises Click, and OnClick uses this for the crumb burst.
            _lastPos = e.GetPosition(this);
            _hasPos = true;
            base.OnMouseLeftButtonUp(e);
            _hasPos = false;
            _pressed = false;
            Refresh();
            SettleTransforms();
        }

        protected override void OnLostMouseCapture(MouseEventArgs e)
        {
            base.OnLostMouseCapture(e);
            _pressed = false;
            Refresh();
            SettleTransforms();
        }
    }

    /// <summary>
    /// Theme-aware scrollbar style. The template is parsed from XAML at runtime (no XAML compilation),
    /// and its colours are DynamicResource keys that MainWindow registers with the shared Theme brushes,
    /// so the scrollbars follow dark and light mode live.
    /// </summary>
    public static class ThemedScroll
    {
        private const string TemplateXaml =
            "<ControlTemplate xmlns=\"http://schemas.microsoft.com/winfx/2006/xaml/presentation\" " +
            "xmlns:x=\"http://schemas.microsoft.com/winfx/2006/xaml\" TargetType=\"ScrollBar\">" +
            "<Grid Background=\"{DynamicResource ThScrollTrack}\">" +
            "<Track Name=\"PART_Track\" Orientation=\"{TemplateBinding Orientation}\">" +
            "<Track.DecreaseRepeatButton><RepeatButton Command=\"{x:Static ScrollBar.PageUpCommand}\" Opacity=\"0\" Focusable=\"False\"/></Track.DecreaseRepeatButton>" +
            "<Track.Thumb><Thumb><Thumb.Template><ControlTemplate TargetType=\"Thumb\">" +
            "<Border x:Name=\"th\" Margin=\"4\" CornerRadius=\"3\" Opacity=\"0.7\" Background=\"{DynamicResource ThScrollThumb}\"/>" +
            "<ControlTemplate.Triggers>" +
            "<Trigger Property=\"IsMouseOver\" Value=\"True\"><Setter TargetName=\"th\" Property=\"Opacity\" Value=\"0.9\"/><Setter TargetName=\"th\" Property=\"Background\" Value=\"{DynamicResource ThCbAccent}\"/></Trigger>" +
            "<Trigger Property=\"IsDragging\" Value=\"True\"><Setter TargetName=\"th\" Property=\"Opacity\" Value=\"1\"/><Setter TargetName=\"th\" Property=\"Background\" Value=\"{DynamicResource ThCbAccent}\"/></Trigger>" +
            "</ControlTemplate.Triggers>" +
            "</ControlTemplate></Thumb.Template></Thumb></Track.Thumb>" +
            "<Track.IncreaseRepeatButton><RepeatButton Command=\"{x:Static ScrollBar.PageDownCommand}\" Opacity=\"0\" Focusable=\"False\"/></Track.IncreaseRepeatButton>" +
            "</Track></Grid></ControlTemplate>";

        /// <summary>Returns a ScrollBar style to register as an implicit style on a window.</summary>
        public static Style Create()
        {
            Style style = new Style(typeof(ScrollBar));
            style.Setters.Add(new Setter(Control.TemplateProperty, XamlReader.Parse(TemplateXaml)));
            // A replaced template loses the built-in thickness, so a vertical bar would measure 0 wide.
            style.Setters.Add(new Setter(FrameworkElement.WidthProperty, 14.0));
            Trigger horizontal = new Trigger();
            horizontal.Property = ScrollBar.OrientationProperty;
            horizontal.Value = Orientation.Horizontal;
            horizontal.Setters.Add(new Setter(FrameworkElement.WidthProperty, double.NaN));
            horizontal.Setters.Add(new Setter(FrameworkElement.HeightProperty, 14.0));
            style.Triggers.Add(horizontal);
            return style;
        }
    }

    /// <summary>
    /// Theme-aware ToolTip and CheckBox styles. Register them as implicit styles in a window's Resources
    /// (key typeof(ToolTip) / typeof(CheckBox)); colours come from the ThTip*/ThChk*/ThCb* DynamicResource keys.
    /// </summary>
    public static class ThemedPopups
    {
        private const string Ns = "xmlns=\"http://schemas.microsoft.com/winfx/2006/xaml/presentation\" " +
            "xmlns:x=\"http://schemas.microsoft.com/winfx/2006/xaml\" ";

        private const string TipXaml =
            "<ControlTemplate " + Ns + "TargetType=\"ToolTip\">" +
            "<Border Background=\"{DynamicResource ThTipBg}\" BorderBrush=\"{DynamicResource ThCbBorder}\" BorderThickness=\"1\" CornerRadius=\"8\" Padding=\"8,6\">" +
            "<ContentPresenter TextElement.Foreground=\"{DynamicResource ThCbText}\"/></Border></ControlTemplate>";

        private const string CheckXaml =
            "<ControlTemplate " + Ns + "TargetType=\"CheckBox\">" +
            "<StackPanel Orientation=\"Horizontal\" Background=\"Transparent\">" +
            "<Border x:Name=\"bx\" Width=\"18\" Height=\"18\" CornerRadius=\"4\" BorderThickness=\"1\" VerticalAlignment=\"Center\" " +
            "Background=\"{DynamicResource ThChkBox}\" BorderBrush=\"{DynamicResource ThCbBorder}\">" +
            "<Path x:Name=\"ck\" Visibility=\"Collapsed\" HorizontalAlignment=\"Center\" VerticalAlignment=\"Center\" Data=\"M 0 4 L 3.5 7.5 L 10 0\" " +
            "Stroke=\"{DynamicResource ThCbAccent}\" StrokeThickness=\"2\" StrokeStartLineCap=\"Round\" StrokeEndLineCap=\"Round\" StrokeLineJoin=\"Round\"/>" +
            "</Border><ContentPresenter Margin=\"8,0,0,0\" VerticalAlignment=\"Center\" RecognizesAccessKey=\"True\"/></StackPanel>" +
            "<ControlTemplate.Triggers>" +
            "<Trigger Property=\"IsChecked\" Value=\"True\"><Setter TargetName=\"ck\" Property=\"Visibility\" Value=\"Visible\"/>" +
            "<Setter TargetName=\"bx\" Property=\"BorderBrush\" Value=\"{DynamicResource ThCbAccent}\"/></Trigger>" +
            "<Trigger Property=\"IsMouseOver\" Value=\"True\"><Setter TargetName=\"bx\" Property=\"BorderBrush\" Value=\"{DynamicResource ThCbAccent}\"/></Trigger>" +
            "<Trigger Property=\"IsEnabled\" Value=\"False\"><Setter Property=\"Opacity\" Value=\"0.5\"/></Trigger>" +
            "</ControlTemplate.Triggers></ControlTemplate>";

        public static Style ToolTipStyle()
        {
            Style s = new Style(typeof(ToolTip));
            s.Setters.Add(new Setter(Control.TemplateProperty, XamlReader.Parse(TipXaml)));
            s.Setters.Add(new Setter(FrameworkElement.MaxWidthProperty, 380.0));
            s.Setters.Add(new Setter(Control.FontFamilyProperty, Theme.UiFont));
            s.Setters.Add(new Setter(Control.FontSizeProperty, 12.0));
            s.Setters.Add(new Setter(Control.BackgroundProperty, Brushes.Transparent));
            s.Setters.Add(new Setter(Control.BorderThicknessProperty, new Thickness(0)));
            return s;
        }

        public static Style CheckBoxStyle()
        {
            Style s = new Style(typeof(CheckBox));
            s.Setters.Add(new Setter(Control.TemplateProperty, XamlReader.Parse(CheckXaml)));
            s.Setters.Add(new Setter(Control.FocusVisualStyleProperty, null));
            return s;
        }

        /// <summary>Adds the ThCb* keys and the implicit ToolTip and CheckBox styles to a window's resources.</summary>
        public static void Install(ResourceDictionary resources)
        {
            ThemeResources.Register(resources);
            resources[typeof(ToolTip)] = ToolTipStyle();
            resources[typeof(CheckBox)] = CheckBoxStyle();
        }
    }

    /// <summary>
    /// Windows 11 style rounded ComboBox. Colours are DynamicResource keys (ThCb*) that the owner
    /// registers in its Resources with the shared Theme brushes, so both modes update live.
    /// </summary>
    public static class ThemedCombo
    {
        private const string Ns = "xmlns=\"http://schemas.microsoft.com/winfx/2006/xaml/presentation\" " +
            "xmlns:x=\"http://schemas.microsoft.com/winfx/2006/xaml\" ";

        private const string ComboXaml =
            "<ControlTemplate " + Ns + "TargetType=\"ComboBox\"><Grid>" +
            "<ToggleButton Focusable=\"False\" ClickMode=\"Press\" " +
            "IsChecked=\"{Binding IsDropDownOpen, Mode=TwoWay, RelativeSource={RelativeSource TemplatedParent}}\">" +
            "<ToggleButton.Template><ControlTemplate TargetType=\"ToggleButton\">" +
            "<Border x:Name=\"bd\" CornerRadius=\"9\" MinHeight=\"36\" BorderThickness=\"1\" Background=\"{DynamicResource ThCbBg}\" BorderBrush=\"{DynamicResource ThCbBorder}\">" +
            "<Path HorizontalAlignment=\"Right\" VerticalAlignment=\"Center\" Margin=\"0,0,12,0\" Data=\"M 0 0 L 4 4 L 8 0\" " +
            "Stroke=\"{DynamicResource ThCbText}\" StrokeThickness=\"1.5\" StrokeStartLineCap=\"Round\" StrokeEndLineCap=\"Round\" StrokeLineJoin=\"Round\"/>" +
            "</Border><ControlTemplate.Triggers>" +
            "<Trigger Property=\"IsMouseOver\" Value=\"True\"><Setter TargetName=\"bd\" Property=\"BorderBrush\" Value=\"{DynamicResource ThCbAccent}\"/></Trigger>" +
            "<Trigger Property=\"IsChecked\" Value=\"True\"><Setter TargetName=\"bd\" Property=\"BorderBrush\" Value=\"{DynamicResource ThCbAccent}\"/></Trigger>" +
            "</ControlTemplate.Triggers></ControlTemplate></ToggleButton.Template></ToggleButton>" +
            "<ContentPresenter IsHitTestVisible=\"False\" Margin=\"12,0,32,0\" VerticalAlignment=\"Center\" HorizontalAlignment=\"Left\" " +
            "TextElement.Foreground=\"{DynamicResource ThCbText}\" " +
            "Content=\"{TemplateBinding SelectionBoxItem}\" ContentTemplate=\"{TemplateBinding SelectionBoxItemTemplate}\"/>" +
            "<Popup x:Name=\"PART_Popup\" IsOpen=\"{TemplateBinding IsDropDownOpen}\" Placement=\"Bottom\" AllowsTransparency=\"True\" Focusable=\"False\" PopupAnimation=\"Fade\">" +
            "<Grid MinWidth=\"{Binding ActualWidth, RelativeSource={RelativeSource TemplatedParent}}\" MaxHeight=\"300\">" +
            "<Border Margin=\"0,4,8,10\" Padding=\"4\" CornerRadius=\"8\" BorderThickness=\"1\" Background=\"{DynamicResource ThCbBg}\" BorderBrush=\"{DynamicResource ThCbBorder}\">" +
            "<Border.Effect><DropShadowEffect BlurRadius=\"12\" ShadowDepth=\"3\" Opacity=\"0.35\"/></Border.Effect>" +
            "<ScrollViewer><StackPanel IsItemsHost=\"True\" KeyboardNavigation.DirectionalNavigation=\"Contained\"/></ScrollViewer>" +
            "</Border></Grid></Popup></Grid></ControlTemplate>";

        private const string ItemXaml =
            "<ControlTemplate " + Ns + "TargetType=\"ComboBoxItem\">" +
            "<Border x:Name=\"bd\" CornerRadius=\"6\" Margin=\"0,1\" Padding=\"12,8\" Background=\"Transparent\">" +
            "<ContentPresenter TextElement.Foreground=\"{DynamicResource ThCbText}\"/></Border>" +
            "<ControlTemplate.Triggers>" +
            "<Trigger Property=\"IsHighlighted\" Value=\"True\"><Setter TargetName=\"bd\" Property=\"Background\" Value=\"{DynamicResource ThCbHover}\"/></Trigger>" +
            "<Trigger Property=\"IsSelected\" Value=\"True\"><Setter TargetName=\"bd\" Property=\"Background\" Value=\"{DynamicResource ThCbHover}\"/></Trigger>" +
            "</ControlTemplate.Triggers></ControlTemplate>";

        /// <summary>Registers the brush keys the templates use; call once on the owning window's Resources.</summary>
        public static void Register(ResourceDictionary resources)
        {
            ThemeResources.Register(resources);
        }
        /// <summary>Applies the themed template and item style to a ComboBox.</summary>
        // Parsed once and shared: the template and item style only use DynamicResource brushes, so one sealed
        // instance serves every combo. Parsing per combo (and again on each theme change) cost a XAML parse per card.
        private static ControlTemplate _comboTemplate;
        private static Style _itemStyle;

        public static void Apply(ComboBox box)
        {
            if (_comboTemplate == null)
            {
                _comboTemplate = (ControlTemplate)XamlReader.Parse(ComboXaml);
                _comboTemplate.Seal();
                Style item = new Style(typeof(ComboBoxItem));
                item.Setters.Add(new Setter(Control.TemplateProperty, XamlReader.Parse(ItemXaml)));
                item.Setters.Add(new Setter(Control.ForegroundProperty, new DynamicResourceExtension("ThCbText")));
                item.Seal();
                _itemStyle = item;
            }
            if (!ReferenceEquals(box.Template, _comboTemplate)) box.Template = _comboTemplate;
            if (!ReferenceEquals(box.ItemContainerStyle, _itemStyle)) box.ItemContainerStyle = _itemStyle;
        }
    }

    /// <summary>
    /// Brushes reached through DynamicResource (templates, styles). WPF freezes Freezables that live in
    /// templates/styles, so these keys never hold the shared mutable Theme brushes: each key holds a private
    /// frozen copy that is replaced (not mutated) on every theme change, and DynamicResource re-resolves it.
    /// </summary>
    public static class ThemeResources
    {
        private static readonly System.Collections.Generic.Dictionary<ResourceDictionary, Action> Subs =
            new System.Collections.Generic.Dictionary<ResourceDictionary, Action>();

        /// <summary>Fills the dictionary and keeps it in step with the theme. Calling it twice for one dictionary is harmless.</summary>
        public static void Register(ResourceDictionary resources)
        {
            if (resources == null) return;
            lock (Subs)
            {
                if (Subs.ContainsKey(resources)) { Refresh(resources); return; }
                Action handler = delegate { Refresh(resources); };
                Subs[resources] = handler;
                Theme.Changed += handler;
            }
            Refresh(resources);
        }

        /// <summary>Stops updating a dictionary (call when its window closes so it can be collected).</summary>
        public static void Unregister(ResourceDictionary resources)
        {
            if (resources == null) return;
            lock (Subs)
            {
                Action handler;
                if (Subs.TryGetValue(resources, out handler))
                {
                    Theme.Changed -= handler;
                    Subs.Remove(resources);
                }
            }
        }

        private static void Refresh(ResourceDictionary r)
        {
            Put(r, "ThTipBg", Theme.Surface);
            Put(r, "ThChkBox", Theme.SurfaceAlt);
            Put(r, "ThCbBg", Theme.Surface);
            Put(r, "ThCbBorder", Theme.Border);
            Put(r, "ThCbText", Theme.Text);
            Put(r, "ThCbAccent", Theme.Accent);
            Put(r, "ThCbHover", Theme.AccentSoft);
            Put(r, "ThScrollTrack", Theme.SurfaceAlt);
            Put(r, "ThScrollThumb", Theme.ScrollThumb);
            Put(r, "ThSlTrack", Theme.Border);
            Put(r, "ThSlFill", Theme.Accent);
            Put(r, "ThSlThumb", Theme.Surface);
        }

        private static void Put(ResourceDictionary r, string key, SolidColorBrush source)
        {
            SolidColorBrush copy = new SolidColorBrush(source.Color);
            copy.Freeze();
            r[key] = copy;
        }
    }

    /// <summary>
    /// RAM limit slider: position 0 is Auto (no cap), then 128 MB to 3 GB in 64 MB steps (see RamScale). Shows the
    /// value next to the track (amber below the "likely to stutter" line). Committed fires about 350 ms after the
    /// last change, so dragging does not apply a cap or save the profile on every step.
    /// </summary>
    public sealed class RamSlider : StackPanel
    {
        private const string SliderXaml =
            "<ControlTemplate xmlns='http://schemas.microsoft.com/winfx/2006/xaml/presentation' " +
            "xmlns:x='http://schemas.microsoft.com/winfx/2006/xaml' TargetType='Slider'>" +
            "<Grid x:Name='root' Height='26' Background='Transparent' SnapsToDevicePixels='True'>" +
            "<Border x:Name='focusRing' CornerRadius='7' BorderThickness='1' BorderBrush='{DynamicResource ThSlFill}' Visibility='Collapsed' IsHitTestVisible='False'/>" +
            "<Border Height='4' Margin='8,0' CornerRadius='2' Background='{DynamicResource ThSlTrack}' VerticalAlignment='Center'/>" +
            "<Track x:Name='PART_Track'>" +
            "<Track.DecreaseRepeatButton><RepeatButton Command='Slider.DecreaseLarge' Focusable='False' IsTabStop='False'>" +
            "<RepeatButton.Template><ControlTemplate TargetType='RepeatButton'><Border Height='4' Margin='8,0,0,0' CornerRadius='2,0,0,2' Background='{DynamicResource ThSlFill}' VerticalAlignment='Center'/></ControlTemplate></RepeatButton.Template>" +
            "</RepeatButton></Track.DecreaseRepeatButton>" +
            "<Track.IncreaseRepeatButton><RepeatButton Command='Slider.IncreaseLarge' Focusable='False' IsTabStop='False'>" +
            "<RepeatButton.Template><ControlTemplate TargetType='RepeatButton'><Border Background='Transparent' Height='26'/></ControlTemplate></RepeatButton.Template>" +
            "</RepeatButton></Track.IncreaseRepeatButton>" +
            "<Track.Thumb><Thumb Width='16' Height='16' Focusable='False'>" +
            "<Thumb.Template><ControlTemplate TargetType='Thumb'>" +
            "<Border x:Name='knob' Width='16' Height='16' CornerRadius='8' Background='{DynamicResource ThSlThumb}' BorderBrush='{DynamicResource ThSlFill}' BorderThickness='2'/>" +
            "<ControlTemplate.Triggers>" +
            "<Trigger Property='IsMouseOver' Value='True'><Setter TargetName='knob' Property='BorderThickness' Value='4'/></Trigger>" +
            "<Trigger Property='IsDragging' Value='True'><Setter TargetName='knob' Property='BorderThickness' Value='5'/></Trigger>" +
            "</ControlTemplate.Triggers></ControlTemplate></Thumb.Template></Thumb></Track.Thumb>" +
            "</Track></Grid>" +
            "<ControlTemplate.Triggers>" +
            "<Trigger Property='IsKeyboardFocused' Value='True'><Setter TargetName='focusRing' Property='Visibility' Value='Visible'/></Trigger>" +
            "<Trigger Property='IsEnabled' Value='False'><Setter TargetName='root' Property='Opacity' Value='0.45'/></Trigger>" +
            "</ControlTemplate.Triggers></ControlTemplate>";

        // Parsed once and shared: the template only uses DynamicResource brushes.
        private static ControlTemplate _template;

        private readonly Slider _slider = new Slider();
        private readonly TextBlock _label;
        private readonly System.Windows.Threading.DispatcherTimer _commit;
        private bool _quiet;

        /// <summary>Raised after the user settles on a value: MB, or 0 for Auto.</summary>
        public event Action<int> Committed;

        public RamSlider(string automationName)
        {
            if (_template == null)
            {
                _template = (ControlTemplate)System.Windows.Markup.XamlReader.Parse(SliderXaml);
                _template.Seal();
            }
            Orientation = Orientation.Horizontal;
            VerticalAlignment = VerticalAlignment.Center;

            _slider.Minimum = 0;
            _slider.Maximum = RamScale.Steps;
            _slider.SmallChange = 1;
            _slider.LargeChange = 4;
            _slider.TickFrequency = 1;
            _slider.IsSnapToTickEnabled = true;
            _slider.Width = 150;
            _slider.VerticalAlignment = VerticalAlignment.Center;
            _slider.FocusVisualStyle = null;
            _slider.Template = _template;
            System.Windows.Automation.AutomationProperties.SetName(_slider, automationName);

            _label = UiUtil.Label("Auto", 12, true, Theme.Text);
            _label.MinWidth = 62;
            _label.Margin = new Thickness(8, 0, 0, 0);

            Children.Add(_slider);
            Children.Add(_label);
            ToolTip = "Memory cap for this client: Auto (no cap) or 128 MB to 3 GB. It trims the RAM Windows keeps for the game " +
                "(a working-set limit, not a hard memory limit), so a low value frees RAM but can make the game stutter.";
            ToolTipService.SetShowOnDisabled(this, true);

            _commit = new System.Windows.Threading.DispatcherTimer();
            _commit.Interval = TimeSpan.FromMilliseconds(350);
            _commit.Tick += delegate
            {
                _commit.Stop();
                Action<int> h = Committed;
                if (h != null) h(Mb);
            };
            _slider.ValueChanged += delegate
            {
                UpdateLabel();
                if (_quiet) return;
                _commit.Stop();
                _commit.Start();
            };
            UpdateLabel();
        }

        /// <summary>Selected cap in MB; 0 means Auto.</summary>
        public int Mb
        {
            get { return RamScale.MbFromIndex((int)Math.Round(_slider.Value)); }
        }

        /// <summary>True while the user is dragging or a change is waiting to be committed; outside updates should wait.</summary>
        public bool IsBusy
        {
            get { return _slider.IsMouseCaptureWithin || _commit.IsEnabled; }
        }

        /// <summary>Shows mb (0 = Auto) without raising Committed. Ignored while the user is changing it.</summary>
        public void SetMb(int mb)
        {
            if (IsBusy) return;
            int index = RamScale.IndexFromMb(mb);
            if ((int)Math.Round(_slider.Value) == index) return;
            _quiet = true;
            try { _slider.Value = index; }
            finally { _quiet = false; }
            UpdateLabel();
        }

        /// <summary>Re-reads the label colour after a theme change (the track uses DynamicResource brushes).</summary>
        public void ApplyTheme()
        {
            UpdateLabel();
        }

        private void UpdateLabel()
        {
            int mb = Mb;
            string text = RamScale.Label(mb);
            if (_label.Text != text) _label.Text = text;
            Brush want = (mb > 0 && mb < MemoryGovernor.MinSafeMb) ? (Brush)Theme.Warning : Theme.Text;
            if (!ReferenceEquals(_label.Foreground, want)) _label.Foreground = want;
        }
    }

    /// <summary>Rounded surface with 1px outline and a soft drop shadow.</summary>
    public class Card : Border
    {
        // No DropShadowEffect: a blur shader per card is re-run whenever anything inside the card changes (stats tick
        // every second), which adds up with many instances. A 2 px bottom border gives the same lifted look for free.
        public Card()
        {
            Background = Theme.Surface;
            BorderBrush = Theme.Border;
            BorderThickness = new Thickness(1, 1, 1, 2);
            CornerRadius = new CornerRadius(14);
            Padding = new Thickness(16);
            SnapsToDevicePixels = true;
        }
    }

    /// <summary>
    /// Thin static meter (track + fill) for the stat strip. Two star columns, no template and no animation, unlike
    /// ProgressBar whose system template can animate; re-lays out only when the value moves by 0.5 % or more.
    /// </summary>
    public class MeterBar : Grid
    {
        private readonly Border _fill = new Border();
        private readonly ColumnDefinition _on = new ColumnDefinition();
        private readonly ColumnDefinition _off = new ColumnDefinition();
        private double _shown = -1;

        public MeterBar()
        {
            Height = 6;
            ColumnDefinitions.Add(_on);
            ColumnDefinitions.Add(_off);
            Border track = new Border();
            track.CornerRadius = new CornerRadius(3);
            track.Background = Theme.SurfaceAlt;
            SetColumnSpan(track, 2);
            _fill.CornerRadius = new CornerRadius(3);
            _fill.Background = Theme.Success;
            Children.Add(track);
            Children.Add(_fill);
            SetFraction(0);
        }

        /// <summary>0..1.</summary>
        public void SetFraction(double f)
        {
            if (double.IsNaN(f) || f < 0) f = 0;
            if (f > 1) f = 1;
            if (_shown >= 0 && Math.Abs(f - _shown) < 0.005) return;
            _shown = f;
            _on.Width = new GridLength(f, GridUnitType.Star);
            _off.Width = new GridLength(1 - f, GridUnitType.Star);
            _fill.Visibility = f <= 0 ? Visibility.Collapsed : Visibility.Visible;
        }

        public Brush Fill
        {
            get { return _fill.Background; }
            set { if (!ReferenceEquals(_fill.Background, value)) _fill.Background = value; }
        }

        public void ApplyTheme()
        {
            ((Border)Children[0]).Background = Theme.SurfaceAlt;
        }
    }

    /// <summary>Pill-shaped status indicator with a coloured dot.</summary>
    public class StatusBadge : Border
    {
        private readonly Ellipse _dot = new Ellipse
        {
            Width = 6,
            Height = 6,
            Margin = new Thickness(0, 0, 6, 0),
            VerticalAlignment = VerticalAlignment.Center
        };

        private readonly TextBlock _text = new TextBlock
        {
            FontFamily = Theme.UiFont,
            FontSize = 12,
            FontWeight = FontWeights.Bold,
            VerticalAlignment = VerticalAlignment.Center
        };

        private string _status = "";
        private bool _subscribed;

        public StatusBadge()
        {
            CornerRadius = new CornerRadius(10);
            Padding = new Thickness(10, 3, 10, 3);
            HorizontalAlignment = HorizontalAlignment.Left;

            var panel = new StackPanel { Orientation = Orientation.Horizontal };
            panel.Children.Add(_dot);
            panel.Children.Add(_text);
            Child = panel;

            Loaded += OnLoaded;
            Unloaded += OnUnloaded;
            Apply();
        }

        /// <summary>Sets the status text and picks colours from it.</summary>
        public void SetStatus(string status)
        {
            _status = status ?? "";
            Apply();
        }

        private void Apply()
        {
            string key = _status.Trim().ToLowerInvariant();
            SolidColorBrush fg;
            SolidColorBrush soft;

            if (key == "running")
            {
                fg = Theme.Success;
                soft = Theme.SuccessSoft;
            }
            else if (key == "awaiting login" || key == "starting")
            {
                fg = Theme.Warning;
                soft = Theme.WarningSoft;
            }
            else if (key == "stopped")
            {
                fg = Theme.TextMuted;
                soft = Theme.SurfaceAlt;
            }
            else
            {
                fg = Theme.Accent;
                soft = Theme.AccentSoft;
            }

            Background = soft;
            _dot.Fill = fg;
            _text.Foreground = fg;
            _text.Text = _status;
        }

        private void OnLoaded(object sender, RoutedEventArgs e)
        {
            if (_subscribed) return;
            Theme.Changed += OnThemeChanged;
            _subscribed = true;
            Apply();
        }

        private void OnUnloaded(object sender, RoutedEventArgs e)
        {
            if (!_subscribed) return;
            Theme.Changed -= OnThemeChanged;
            _subscribed = false;
        }

        private void OnThemeChanged()
        {
            Apply();
        }
    }

    /// <summary>Full-width message strip with a level icon and tinted background.</summary>
    public class InfoBanner : Border
    {
        private readonly TextBlock _icon = new TextBlock
        {
            FontFamily = new FontFamily("Segoe Fluent Icons, Segoe MDL2 Assets"),
            FontSize = 16,
            VerticalAlignment = VerticalAlignment.Top,
            Margin = new Thickness(0, 1, 10, 0)
        };

        private readonly TextBlock _message = new TextBlock
        {
            FontFamily = Theme.UiFont,
            FontSize = 13,
            TextWrapping = TextWrapping.Wrap,
            VerticalAlignment = VerticalAlignment.Center,
            Foreground = Theme.Text
        };

        public InfoBanner()
        {
            CornerRadius = new CornerRadius(9);
            Padding = new Thickness(16, 12, 16, 12);
            Visibility = Visibility.Collapsed;

            var grid = new Grid();
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            grid.Children.Add(_icon);
            Grid.SetColumn(_message, 1);
            grid.Children.Add(_message);
            Child = grid;
        }

        /// <summary>
        /// Shows the banner. Level is "None", "Info", "Warning" or "Critical"/"Blocked"
        /// (case-insensitive; unknown values fall back to Info). Empty message collapses it.
        /// </summary>
        public void Show(string level, string message)
        {
            if (string.IsNullOrWhiteSpace(message))
            {
                Visibility = Visibility.Collapsed;
                return;
            }

            string key = (level ?? "").Trim().ToLowerInvariant();
            string glyph;
            SolidColorBrush bg;
            SolidColorBrush fg;

            switch (key)
            {
                case "none":
                    glyph = "E73E";   // checkmark
                    bg = Theme.SuccessSoft;
                    fg = Theme.Success;
                    break;
                case "warning":
                    glyph = "E7BA";
                    bg = Theme.WarningSoft;
                    fg = Theme.Warning;
                    break;
                case "critical":
                case "blocked":
                    glyph = "EA39";
                    bg = Theme.DangerSoft;
                    fg = Theme.Danger;
                    break;
                default:
                    glyph = "E946";   // info
                    bg = Theme.AccentSoft;
                    fg = Theme.Accent;
                    break;
            }

            Background = bg;
            _icon.Text = UiUtil.GlyphIcon(glyph);
            _icon.Foreground = fg;
            _message.Text = message.Trim();
            Visibility = Visibility.Visible;
        }
    }

    /// <summary>Shared helpers for building UI in code.</summary>
    public static class UiUtil
    {
        /// <summary>Single-line label using the UI font, trimmed with an ellipsis.</summary>
        public static TextBlock Label(string text, double size, bool bold, Brush fg)
        {
            return new TextBlock
            {
                Text = text ?? "",
                FontFamily = Theme.UiFont,
                FontSize = size,
                FontWeight = bold ? FontWeights.SemiBold : FontWeights.Normal,
                Foreground = fg,
                TextWrapping = TextWrapping.NoWrap,
                TextTrimming = TextTrimming.CharacterEllipsis,
                VerticalAlignment = VerticalAlignment.Center
            };
        }

        /// <summary>"512 MB" below 1024 MB, otherwise "1.2 GB".</summary>
        public static string FormatMb(double mb)
        {
            if (mb < 1024)
            {
                return Math.Round(mb).ToString("0", CultureInfo.InvariantCulture) + " MB";
            }
            return (mb / 1024.0).ToString("0.0", CultureInfo.InvariantCulture) + " GB";
        }

        /// <summary>Converts a hex code point (e.g. "E946") to the Segoe icon glyph string.</summary>
        public static string GlyphIcon(string hex)
        {
            return ((char)Convert.ToInt32(hex, 16)).ToString();
        }
    }
}


