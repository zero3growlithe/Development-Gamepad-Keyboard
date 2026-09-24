using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Shapes;
using GamepadKeyboard.Keyboard;
using GamepadKeyboard.Native;
using GamepadKeyboard.Settings;

namespace GamepadKeyboard.Overlay
{
    /// <summary>
    /// Transparent topmost overlay that renders the virtual keyboard, the two
    /// analog origin points, selection rays and highlights. Click-through when
    /// passive; interactive while the user drags the origin points or the window.
    /// </summary>
    public sealed class KeyboardOverlay : Window
    {
        private readonly Canvas _canvas = new();
        private readonly Dictionary<KeyboardLayout.KeyDef, Border> _keyBorders = new();
        private readonly Ellipse _leftPoint = MakePoint(Brushes.Orange);
        private readonly Ellipse _rightPoint = MakePoint(Brushes.DeepSkyBlue);
        private readonly Line _leftRay = MakeRay();
        private readonly Line _rightRay = MakeRay();
        private readonly Ellipse _leftHit = MakeHit();
        private readonly Ellipse _rightHit = MakeHit();
        private readonly TextBlock _profileLabel = MakeLabel();

        public KeyboardLayout Layout { get; } = new();

        public event Action<KeyboardLayout.KeyDef>? KeyClicked;

        public KeyboardOverlay(KeyboardLayout layout)
        {
            Layout = layout;
            WindowStyle = WindowStyle.None;
            AllowsTransparency = true;
            Background = Brushes.Transparent;
            ResizeMode = ResizeMode.NoResize;
            ShowInTaskbar = false;
            Topmost = true;
            ShowActivated = false;

            var grid = new Grid();
            _canvas.Children.Add(grid);
            Content = grid;

            grid.Children.Add(_profileLabel);
            Canvas.SetZIndex(_profileLabel, 100);

            RebuildKeys();
            SizeToContent();
        }

        private void RebuildKeys()
        {
            foreach (var b in _keyBorders.Values) _canvas.Children.Remove(b);
            _keyBorders.Clear();

            foreach (var k in Layout.Keys)
            {
                var r = KeyboardLayout.KeyRect(k, AppSettings.Instance.KeySpacing);
                var border = new Border
                {
                    Width = r.Width,
                    Height = r.Height,
                    BorderBrush = (Brush)FindResource("KeyBorderBrush"),
                    BorderThickness = new Thickness(1),
                    Background = (Brush)FindResource("KeyBrush"),
                    CornerRadius = new CornerRadius(3),
                    Child = new TextBlock
                    {
                        Text = k.Label,
                        Foreground = (Brush)FindResource("TextBrush"),
                        FontSize = 12,
                        HorizontalAlignment = HorizontalAlignment.Center,
                        VerticalAlignment = VerticalAlignment.Center
                    }
                };
                Canvas.SetLeft(border, r.X);
                Canvas.SetTop(border, r.Y);
                _canvas.Children.Add(border);
                _keyBorders[k] = border;

                if (k.Vk != 0)
                {
                    border.MouseDown += (_, __) => KeyClicked?.Invoke(k);
                }
            }

            // add the dynamic elements on top
            _canvas.Children.Add(_leftRay);
            _canvas.Children.Add(_rightRay);
            _canvas.Children.Add(_leftPoint);
            _canvas.Children.Add(_rightPoint);
            _canvas.Children.Add(_leftHit);
            _canvas.Children.Add(_rightHit);
        }

        private static Ellipse MakePoint(Brush fill) => new()
        {
            Width = 14, Height = 14, Fill = fill,
            Stroke = Brushes.Black, StrokeThickness = 1.5,
            IsHitTestVisible = true
        };

        private static Ellipse MakeHit() => new()
        {
            Width = 26, Height = 26, Fill = Brushes.Transparent,
            Stroke = null, IsHitTestVisible = true
        };

        private static Line MakeRay() => new()
        {
            StrokeThickness = 2,
            Stroke = new SolidColorBrush(Color.FromArgb(160, 60, 200, 255)),
            IsHitTestVisible = false
        };

        private static TextBlock MakeLabel() => new()
        {
            Foreground = new SolidColorBrush(Color.FromArgb(220, 240, 240, 245)),
            FontSize = 14,
            FontWeight = FontWeights.SemiBold,
            Background = new SolidColorBrush(Color.FromArgb(160, 20, 20, 24)),
            Padding = new Thickness(6, 2, 6, 2)
        };

        public void SetProfileName(string name) => _profileLabel.Text = $"Profile: {name}";

        public void LayoutProfileLabel(double width)
        {
            Canvas.SetLeft(_profileLabel, 4);
            Canvas.SetTop(_profileLabel, ActualHeight - 30);
        }

        public new void SizeToContent()
        {
            var pitch = 48 + AppSettings.Instance.KeySpacing;
            Width = Layout.GridW * pitch + 8;
            Height = Layout.GridH * pitch + 44;
        }

        public void RebuildAndResize()
        {
            RebuildKeys();
            SizeToContent();
            // reposition points per profile after a resize
            SetPointPositions();
        }

        public void SetPointPositions()
        {
            var p = AppSettings.Instance.Profile;
            SetPoint(_leftPoint, _leftHit, p.LeftX, p.LeftY);
            SetPoint(_rightPoint, _rightHit, p.RightX, p.RightY);
        }

        private void SetPoint(Ellipse dot, Ellipse hit, double nx, double ny)
        {
            double x = nx * (Width - 8) + 4;
            double y = ny * (Height - 44) + 4;
            Canvas.SetLeft(hit, x - 13);
            Canvas.SetTop(hit, y - 13);
            Canvas.SetLeft(dot, x - 7);
            Canvas.SetTop(dot, y - 7);
        }

        /// <summary>
        /// Update rays + highlight for a stick. Origin = the point for that stick.
        /// </summary>
        public void UpdateRay(bool left, double dx, double dy, double len, KeyboardLayout.KeyDef? hit)
        {
            var point = left ? _leftPoint : _rightPoint;
            var ray = left ? _leftRay : _rightRay;
            var hitE = left ? _leftHit : _rightHit;

            double ox = Canvas.GetLeft(point) + 7;
            double oy = Canvas.GetTop(point) + 7;

            if (hit == null || (dx == 0 && dy == 0))
            {
                ray.Visibility = Visibility.Collapsed;
                hitE.Visibility = Visibility.Collapsed;
                return;
            }

            ray.Visibility = Visibility.Visible;
            ray.X1 = ox; ray.Y1 = oy;
            ray.X2 = ox + dx * len;
            ray.Y2 = oy - dy * len; // screen Y is inverted

            var r = KeyboardLayout.KeyRect(hit, AppSettings.Instance.KeySpacing);
            Canvas.SetLeft(hitE, r.X + r.Width / 2 - 13);
            Canvas.SetTop(hitE, r.Y + r.Height / 2 - 13);
            hitE.Visibility = Visibility.Visible;
        }

        public void HighlightKey(KeyboardLayout.KeyDef? k, bool active)
        {
            if (k != null && _keyBorders.TryGetValue(k, out var b))
            {
                b.BorderBrush = active ? (Brush)FindResource("ActiveBrush") : (Brush)FindResource("HighlightBrush");
                b.BorderThickness = new Thickness(active ? 2.5 : 1.8);
            }
        }

        public void ClearHighlights()
        {
            foreach (var kv in _keyBorders)
            {
                kv.Value.BorderBrush = (Brush)FindResource("KeyBorderBrush");
                kv.Value.BorderThickness = new Thickness(1);
            }
        }

        protected override void OnSourceInitialized(EventArgs e)
        {
            base.OnSourceInitialized(e);
            var hwnd = new WindowInteropHelper(this).Handle;
            int ex = NativeMethods.GetWindowLong(hwnd, NativeMethods.GWL_EXSTYLE);
            NativeMethods.SetWindowLong(hwnd, NativeMethods.GWL_EXSTYLE,
                ex | NativeMethods.WS_EX_LAYERED | NativeMethods.WS_EX_NOACTIVATE | NativeMethods.WS_EX_TOOLWINDOW);
            // start click-through; the controller turns WS_EX_TRANSPARENT off when editing points
            NativeMethods.SetWindowLong(hwnd, NativeMethods.GWL_EXSTYLE, ex | NativeMethods.WS_EX_TRANSPARENT);
        }

        public void SetClickThrough(bool clickThrough)
        {
            var hwnd = new WindowInteropHelper(this).Handle;
            if (hwnd == IntPtr.Zero) return;
            int ex = NativeMethods.GetWindowLong(hwnd, NativeMethods.GWL_EXSTYLE);
            if (clickThrough)
                NativeMethods.SetWindowLong(hwnd, NativeMethods.GWL_EXSTYLE, ex | NativeMethods.WS_EX_TRANSPARENT);
            else
                NativeMethods.SetWindowLong(hwnd, NativeMethods.GWL_EXSTYLE, ex & ~NativeMethods.WS_EX_TRANSPARENT);
        }
    }
}