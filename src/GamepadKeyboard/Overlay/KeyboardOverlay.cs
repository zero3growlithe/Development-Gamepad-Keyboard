using System;
using System.Collections.Generic;
using System.Linq;
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
        private readonly HashSet<KeyboardLayout.KeyDef> _highlighted = new();
        private readonly Ellipse _leftPoint = MakePoint(LeftBrush);
        private readonly Ellipse _rightPoint = MakePoint(RightBrush);
        private readonly Line _leftRay = MakeRay(new SolidColorBrush(Color.FromArgb(170, 0xFF, 0xA5, 0x00)));
        private readonly Line _rightRay = MakeRay(new SolidColorBrush(Color.FromArgb(170, 0x00, 0xBF, 0xFF)));
        private readonly Ellipse _leftHit = MakeCursor(LeftBrush);
        private readonly Ellipse _rightHit = MakeCursor(RightBrush);
        private readonly TextBlock _profileLabel = MakeLabel();
        private readonly TextBlock _statusLabel = MakeLabel();   // mode/notifications, right of profile
        private HashSet<ushort> _toggledVks = new();             // modifier keys tinted while toggled on
        private readonly ScaleTransform _scaleT = new(1, 1);
        private double _baseW, _baseH;   // unscaled canvas size
        private double _scale = 1.0;
        private string _profileText = "";
        private bool _shiftActive;
        private double _lastLeftPointX = double.NaN, _lastLeftPointY = double.NaN;
        private double _lastRightPointX = double.NaN, _lastRightPointY = double.NaN;
        private bool? _lastCentersVisible;

        public double Scale => _scale;

        public KeyboardLayout Layout { get; }

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

            // the canvas IS the content — keys, points, rays and label all live here
            _canvas.Children.Add(_profileLabel);
            Canvas.SetZIndex(_profileLabel, 100);
            _canvas.Children.Add(_statusLabel);
            Canvas.SetZIndex(_statusLabel, 100);
            _statusLabel.Visibility = Visibility.Collapsed;
            _canvas.LayoutTransform = _scaleT;   // stick-driven scaling
            Content = _canvas;

            _statusHide.Tick += StatusHideTick;
            Left = Settings.AppSettings.Instance.OverlayLeft;
            Top = Settings.AppSettings.Instance.OverlayTop;
            RebuildKeys();
            SizeToContent();
            LayoutProfileLabel(_baseW);
            SetScale(Settings.AppSettings.Instance.OverlayScale);
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
                        Text = VisibleKeyLabel(k, _shiftActive),
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

                if (k.Vk != 0 && _toggledVks.Contains(k.Vk))
                    border.Background = ToggledBrush;

                if (k.Vk != 0)
                {
                    border.MouseDown += (_, __) => KeyClicked?.Invoke(k);
                }
            }

            // dynamic elements on top (remove first so a rebuild never re-parents)
            _canvas.Children.Remove(_leftRay);
            _canvas.Children.Remove(_rightRay);
            _canvas.Children.Remove(_leftPoint);
            _canvas.Children.Remove(_rightPoint);
            _canvas.Children.Remove(_leftHit);
            _canvas.Children.Remove(_rightHit);
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

        private static Ellipse MakeCursor(Brush stroke) => new()
        {
            Width = 26, Height = 26, Fill = Brushes.Transparent,
            Stroke = stroke, StrokeThickness = 2.5, IsHitTestVisible = false
        };

        private static Line MakeRay(Brush stroke) => new()
        {
            StrokeThickness = 2,
            Stroke = stroke,
            IsHitTestVisible = false
        };

        static readonly Brush LeftBrush = new SolidColorBrush(Color.FromRgb(0xFF, 0xA5, 0x00));      // Orange
        static readonly Brush RightBrush = new SolidColorBrush(Color.FromRgb(0x00, 0xBF, 0xFF));     // DeepSkyBlue

        private static TextBlock MakeLabel() => new()
        {
            Foreground = new SolidColorBrush(Color.FromArgb(220, 240, 240, 245)),
            FontSize = 14,
            FontWeight = FontWeights.SemiBold,
            Background = new SolidColorBrush(Color.FromArgb(160, 20, 20, 24)),
            Padding = new Thickness(6, 2, 6, 2)
        };

        public void SetProfileName(string name)
        {
            string text = $"Profile: {name}";
            if (_profileText == text) return;
            _profileText = text;
            _profileLabel.Text = text;
            PlaceStatusAfterProfile();
        }

        public void LayoutProfileLabel(double width)
        {
            Canvas.SetLeft(_profileLabel, 4);
            Canvas.SetTop(_profileLabel, _baseH - 40);
            PlaceStatusAfterProfile();
        }

        /// <summary>Notification/status text: same look as profile label, to its right.</summary>
        public void ShowStatus(string message, int seconds, bool permanent)
        {
            _statusLabel.Text = message;
            _statusLabel.Visibility = Visibility.Visible;
            PlaceStatusAfterProfile();
            _statusHide.Stop();
            if (!permanent)
            {
                _statusHide.Interval = TimeSpan.FromSeconds(Math.Max(1, seconds));
                _statusHide.Start();
            }
        }

        private readonly System.Windows.Threading.DispatcherTimer _statusHide =
            new() { Interval = TimeSpan.FromSeconds(3) };

        private void StatusHideTick(object? sender, EventArgs e)
        {
            _statusHide.Stop();
            _statusLabel.Visibility = Visibility.Collapsed;
        }

        private void PlaceStatusAfterProfile()
        {
            // profile label is at (4, _baseH-40); place status right after its rendered width
            _profileLabel.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
            double pw = _profileLabel.DesiredSize.Width;
            Canvas.SetLeft(_statusLabel, 4 + pw + 8);
            Canvas.SetTop(_statusLabel, _baseH - 40);
        }

        public new void SizeToContent()
        {
            var pitch = 48 + AppSettings.Instance.KeySpacing;
            _baseW = Layout.GridW * pitch + 8;
            _baseH = Layout.GridH * pitch + 44;
            ApplyScale();
        }

        public void SetScale(double scale)
        {
            double clamped = Math.Clamp(scale, 0.5, 2.5);
            if (Math.Abs(_scale - clamped) < 0.0001) return;
            _scale = clamped;
            ApplyScale();
        }

        private void ApplyScale()
        {
            Width = _baseW * _scale;
            Height = _baseH * _scale;
            _scaleT.ScaleX = _scaleT.ScaleY = _scale;
        }

        public void RebuildAndResize()
        {
            RebuildKeys();
            SizeToContent();
            LayoutProfileLabel(Width);
            // reposition points per profile after a resize
            _lastLeftPointX = _lastLeftPointY = double.NaN;
            _lastRightPointX = _lastRightPointY = double.NaN;
            SetPointPositions();
        }

        public void SetPointPositions()
        {
            var p = AppSettings.Instance.StickPointsProfile;
            if (p.LeftX != _lastLeftPointX || p.LeftY != _lastLeftPointY)
            {
                SetPoint(_leftPoint, p.LeftX, p.LeftY);
                _lastLeftPointX = p.LeftX;
                _lastLeftPointY = p.LeftY;
            }
            if (p.RightX != _lastRightPointX || p.RightY != _lastRightPointY)
            {
                SetPoint(_rightPoint, p.RightX, p.RightY);
                _lastRightPointX = p.RightX;
                _lastRightPointY = p.RightY;
            }
            bool showCenters = !(AppSettings.Instance.FreeCursorEnabled
                && AppSettings.Instance.HideCenterPointsAndRaysInFreeCursor);
            if (_lastCentersVisible != showCenters)
            {
                _leftPoint.Visibility = _rightPoint.Visibility = showCenters ? Visibility.Visible : Visibility.Collapsed;
                _lastCentersVisible = showCenters;
            }
        }

        private void SetPoint(Ellipse dot, double nx, double ny)
        {
            double x = nx * (_baseW - 8) + 4;
            double y = ny * (_baseH - 44) + 4;
            Canvas.SetLeft(dot, x - 7);
            Canvas.SetTop(dot, y - 7);
        }

        /// <summary>
        /// Update a stick's ray and independent unfilled cursor circle.
        /// </summary>
        public void UpdateCursor(bool left, double rayX, double rayY, double cursorX, double cursorY, bool active)
        {
            var point = left ? _leftPoint : _rightPoint;
            var ray = left ? _leftRay : _rightRay;
            var cursor = left ? _leftHit : _rightHit;

            double ox = Canvas.GetLeft(point) + 7;
            double oy = Canvas.GetTop(point) + 7;

            if (!active)
            {
                ray.Visibility = Visibility.Collapsed;
                cursor.Visibility = Visibility.Collapsed;
                return;
            }

            bool showRay = !(AppSettings.Instance.FreeCursorEnabled
                && AppSettings.Instance.HideCenterPointsAndRaysInFreeCursor)
                && !(AppSettings.Instance.CursorLagEnabled
                    && AppSettings.Instance.HideRaysInCursorLag);
            ray.Visibility = showRay ? Visibility.Visible : Visibility.Collapsed;
            ray.X1 = ox; ray.Y1 = oy;
            ray.X2 = rayX;
            ray.Y2 = rayY;

            Canvas.SetLeft(cursor, cursorX - 13);
            Canvas.SetTop(cursor, cursorY - 13);
            cursor.Visibility = Visibility.Visible;
        }

        /// <summary>Tints the background of keys whose virtual modifiers are toggled on.</summary>
        public void SetToggledKeys(System.Collections.Generic.IEnumerable<ushort> vks)
        {
            // modifier VKs only — never tint regular keys
            var next = new HashSet<ushort>(vks.Where(IsModifierVk));
            if (_toggledVks.SetEquals(next)) return;
            _toggledVks = next;
            foreach (var pair in _keyBorders)
            {
                bool on = pair.Key.Vk != 0 && _toggledVks.Contains(pair.Key.Vk);
                ApplyToggleTint(pair.Value, on);
            }
        }

        public void SetShiftActive(bool active)
        {
            if (_shiftActive == active) return;
            _shiftActive = active;
            foreach (var pair in _keyBorders)
            {
                if (pair.Value.Child is TextBlock label)
                    label.Text = VisibleKeyLabel(pair.Key, active);
            }
        }

        private static string VisibleKeyLabel(KeyboardLayout.KeyDef key, bool shiftActive)
        {
            if (!shiftActive) return key.Label;
            return key.Vk switch
            {
                0xC0 => "~",                         // `
                (ushort)'1' => "!",
                (ushort)'2' => "@",
                (ushort)'3' => "#",
                (ushort)'4' => "$",
                (ushort)'5' => "%",
                (ushort)'6' => "^",
                (ushort)'7' => "&",
                (ushort)'8' => "*",
                (ushort)'9' => "(",
                (ushort)'0' => ")",
                0xBD => "_",                         // -
                0xBB => "+",                         // =
                0xDB => "{",                         // [
                0xDD => "}",                         // ]
                0xDC => "|",                         // \
                0xBA => ":",                         // ;
                0xDE => "\"",                        // '
                0xBC => "<",                         // ,
                0xBE => ">",                         // .
                0xBF => "?",                         // /
                _ => key.Label
            };
        }

        private static bool IsModifierVk(ushort vk) => vk is
            (ushort)0xA0 or (ushort)0xA1 or   // LShift / RShift
            (ushort)0xA2 or (ushort)0xA3 or   // LControl / RControl
            (ushort)0xA4 or (ushort)0xA5 or   // LMenu / RMenu (Alt)
            (ushort)0x5B or (ushort)0x5C;     // LWin / RWin

        private void ApplyToggleTint(Border border, bool on)
        {
            if (on) border.Background = ToggledBrush;
            else if (ReferenceEquals(border.Background, ToggledBrush))
                border.Background = (Brush)FindResource("KeyBrush");
        }

        static readonly Brush ToggledBrush = new SolidColorBrush(Color.FromArgb(200, 40, 190, 90)); // green

        public void HighlightKey(KeyboardLayout.KeyDef? k, bool active, bool left)
        {
            if (k != null && _keyBorders.TryGetValue(k, out var b))
            {
                // border color matches that stick's dot color
                b.BorderBrush = left ? LeftBrush : RightBrush;
                b.BorderThickness = new Thickness(active ? 2.5 : 1.8);
                _highlighted.Add(k);
            }
        }

        public void ClearHighlights()
        {
            if (_highlighted.Count == 0) return;
            foreach (var key in _highlighted)
            {
                if (!_keyBorders.TryGetValue(key, out var border)) continue;
                border.BorderBrush = (Brush)FindResource("KeyBorderBrush");
                border.BorderThickness = new Thickness(1);
            }
            _highlighted.Clear();
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
