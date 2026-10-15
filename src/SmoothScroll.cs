using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;

namespace RbxMulti
{
    /// <summary>
    /// Window-level wheel and keyboard scrolling. Nested ScrollViewers (page, panels, log, lists) normally
    /// swallow the wheel even when they cannot scroll; here the wheel goes to the nearest ancestor that can
    /// actually move in that direction, in steps that honour the Windows wheel setting.
    /// </summary>
    public static class SmoothScroll
    {
        private const double DipPerLine = 16.0;   // 3 lines (the Windows default) = 48 DIP per notch

        public static void Attach(Window w)
        {
            w.PreviewMouseWheel += OnWheel;
            w.PreviewKeyDown += OnKey;
        }

        private static DependencyObject ParentOf(DependencyObject d)
        {
            if (d == null) return null;
            if (d is Visual || d is System.Windows.Media.Media3D.Visual3D) return VisualTreeHelper.GetParent(d);
            return LogicalTreeHelper.GetParent(d);
        }

        /// <summary>True when the viewer can move in the given direction (up when delta &gt; 0).</summary>
        public static bool CanMove(ScrollViewer sv, bool up)
        {
            if (sv == null || sv.ScrollableHeight <= 0.5) return false;
            return up ? sv.VerticalOffset > 0.5 : sv.VerticalOffset < sv.ScrollableHeight - 0.5;
        }

        public static ScrollViewer FindScrollable(DependencyObject start, bool up)
        {
            for (DependencyObject d = start; d != null; d = ParentOf(d))
            {
                ScrollViewer sv = d as ScrollViewer;
                if (sv != null && CanMove(sv, up)) return sv;
            }
            return null;
        }

        /// <summary>Scrolls the nearest capable ancestor by wheel delta. Returns true if something moved.</summary>
        public static bool Scroll(DependencyObject source, int delta)
        {
            if (delta == 0) return false;
            bool up = delta > 0;
            ScrollViewer sv = FindScrollable(source, up);
            if (sv == null) return false;
            int lines = SystemParameters.WheelScrollLines;
            double step = lines < 0 ? sv.ViewportHeight : lines * DipPerLine;
            double amount = (delta / 120.0) * step;
            double target = Math.Max(0, Math.Min(sv.ScrollableHeight, sv.VerticalOffset - amount));
            sv.ScrollToVerticalOffset(target);
            return true;
        }

        private static void OnWheel(object sender, MouseWheelEventArgs e)
        {
            if (e.Handled) return;
            if (Keyboard.Modifiers != ModifierKeys.None) return;
            DependencyObject src = e.OriginalSource as DependencyObject;
            if (Scroll(src, e.Delta)) e.Handled = true;
        }

        private static ScrollViewer KeyTarget(Window w, bool up)
        {
            ScrollViewer sv = FindScrollable(Keyboard.FocusedElement as DependencyObject, up);
            if (sv != null) return sv;
            return FindDown(w.Content as DependencyObject, up);
        }

        private static ScrollViewer FindDown(DependencyObject d, bool up)
        {
            if (d == null) return null;
            ScrollViewer sv = d as ScrollViewer;
            if (sv != null && CanMove(sv, up)) return sv;
            if (sv != null) return null;
            int n = d is Visual ? VisualTreeHelper.GetChildrenCount(d) : 0;
            for (int i = 0; i < n; i++)
            {
                ScrollViewer r = FindDown(VisualTreeHelper.GetChild(d, i), up);
                if (r != null) return r;
            }
            return null;
        }

        private static void OnKey(object sender, KeyEventArgs e)
        {
            if (e.Handled || Keyboard.Modifiers != ModifierKeys.None) return;
            Key k = e.Key;
            if (k != Key.PageUp && k != Key.PageDown && k != Key.Home && k != Key.End) return;
            DependencyObject f = Keyboard.FocusedElement as DependencyObject;
            if (f is TextBoxBase || f is ComboBox || f is Selector) return;
            for (DependencyObject d = f; d != null; d = ParentOf(d))
                if (d is ComboBox || d is TextBoxBase) return;
            Window w = sender as Window;
            bool up = k == Key.PageUp || k == Key.Home;
            ScrollViewer sv = KeyTarget(w, up);
            if (sv == null) return;
            if (k == Key.PageUp) sv.ScrollToVerticalOffset(Math.Max(0, sv.VerticalOffset - sv.ViewportHeight * 0.9));
            else if (k == Key.PageDown) sv.ScrollToVerticalOffset(Math.Min(sv.ScrollableHeight, sv.VerticalOffset + sv.ViewportHeight * 0.9));
            else if (k == Key.Home) sv.ScrollToTop();
            else sv.ScrollToBottom();
            e.Handled = true;
        }
    }
}

