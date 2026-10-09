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
using GamepadKeyboard.UI;

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

        private bool _capsLockActive;
        private double _lastLeftPointX = double.NaN, _lastLeftPointY = double.NaN;
        private double _lastRightPointX = double.NaN, _lastRightPointY = double.NaN;
        private bool? _lastCentersVisible;
        private readonly List<UIElement> _bindingPromptElements = new();
        private readonly List<KeyboardLayout.KeyDef> _promptExtraDefs = new();   // off-layout VK keys (volume/media) drawn right of the grid
        private double _promptListHeight;
        private double _lastPromptFingerprint = double.NaN;

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
                    Opacity = Math.Clamp(AppSettings.Instance.KeyboardKeyOpacity, 0.2, 1.0),
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

            RenderBindingPrompts();
        }

        /// <summary>"Show button prompts" (Keyboard tab): pins pad-button
        /// icons onto the virtual keyboard the same way the Key Maps board
        /// pins them — right-upper corner, scaled/offset by the shared
        /// 'Buttons icons scale'/'Prompt offset' sliders. Actions whose keys
        /// are not on the layout (volume, media) get extra virtual keys right
        /// of the grid; app-level commands render as a [icon label] list
        /// under the grid, wrapping inside the keyboard width.</summary>
        private double PromptFingerprintNow()
        {
            string text = AppSettings.Instance.ShowKeyboardButtonPrompts.ToString()
                + "|" + AppSettings.Instance.Profile.Name
                + "|" + AppSettings.Instance.ActiveProfile
                + "|" + AppSettings.Instance.KeySpacing
                + "|" + AppSettings.Instance.KeyboardKeyOpacity
                + "|" + AppSettings.Instance.KeyboardPromptIconScale
                + "|" + AppSettings.Instance.KeyboardPromptOffsetX
                + "|" + AppSettings.Instance.KeyboardPromptOffsetY;
            foreach (ProfileBinding binding in AppSettings.Instance.Profile.Bindings)
            {
                text += "|" + string.Join(",", binding.Buttons) + "=" + binding.Action
                    + (binding.Modifier ? "+M" : string.Empty) + (binding.HoldLast ? "+H" : string.Empty);
            }
            return text.GetHashCode();
        }

        /// <summary>Called from RefreshUiCore (mapper state transitions —
        /// profile switches, binding edits): rebuilds the prompt block only
        /// when a cheap fingerprint of the relevant settings changed.</summary>
        public void RefreshBindingPromptsIfDirty()
        {
            if (PromptFingerprintNow() == _lastPromptFingerprint)
            {
                return;
            }
            RenderBindingPrompts();
        }

        private void RenderBindingPrompts()
        {
            foreach (UIElement element in _bindingPromptElements)
            {
                _canvas.Children.Remove(element);
            }
            _bindingPromptElements.Clear();
            _promptExtraDefs.Clear();
            _promptListHeight = 0;
            _lastPromptFingerprint = PromptFingerprintNow();

            if (!AppSettings.Instance.ShowKeyboardButtonPrompts)
            {
                SizeToContent();
                return;
            }

            Dictionary<ushort, List<string>> keyPrompts = new();
            List<ushort> extraKeyVks = new();
            List<(string IconSlot, string Label)> appPrompts = new();
            ControllerMapper.CollectKeyboardPrompts(
                AppSettings.Instance.Profile.Bindings,
                vk => Layout.FindByVk(vk) != null,
                keyPrompts, extraKeyVks, appPrompts);

            double pitch = 48 + AppSettings.Instance.KeySpacing;
            double promptScale = KeyMapsOverlayWindow.PromptIconScale * Math.Max(0.05,
                AppSettings.Instance.KeyboardPromptIconScale);
            double iconSpan = KeyMapsAtom.IconSpan * promptScale;

            // ── 1. badges on keys present on the layout ──
            foreach (KeyValuePair<ushort, List<string>> pair in keyPrompts)
            {
                KeyboardLayout.KeyDef? key = Layout.FindByVk(pair.Key);
                if (key == null)
                {
                    continue;
                }
                Rect keyRect = KeyboardLayout.KeyRect(key, AppSettings.Instance.KeySpacing);
                AttachPromptIcons(pair.Value, keyRect, promptScale, iconSpan);
            }

            // ── 2. extra virtual keys for off-layout VKs (Maps-Mode style) ──
            if (extraKeyVks.Count > 0)
            {
                const int Rows = 6;
                double baseX = Layout.GridW + 0.25;
                System.Windows.Media.Brush borderBrush = (System.Windows.Media.Brush)FindResource("KeyBorderBrush");
                System.Windows.Media.Brush fillBrush = (System.Windows.Media.Brush)FindResource("KeyBrush");
                for (int index = 0; index < extraKeyVks.Count; index++)
                {
                    ushort vk = extraKeyVks[index];
                    int row = index % Rows;
                    int column = index / Rows;
                    KeyboardLayout.KeyDef extra = new KeyboardLayout.KeyDef(VkLabel(vk), vk, 1, ControllerMapper.IsExtendedKey(vk));
                    extra.X = baseX + column;
                    extra.Y = row;
                    _promptExtraDefs.Add(extra);
                    Rect r = KeyboardLayout.KeyRect(extra, AppSettings.Instance.KeySpacing);
                    Border tile = new Border
                    {
                        Width = r.Width,
                        Height = r.Height,
                        BorderBrush = borderBrush,
                        BorderThickness = new Thickness(1),
                        Background = fillBrush,
                        Opacity = Math.Clamp(AppSettings.Instance.KeyboardKeyOpacity, 0.2, 1.0),
                        CornerRadius = new CornerRadius(3),
                        Child = new TextBlock
                        {
                            Text = VkLabel(vk),
                            Foreground = (System.Windows.Media.Brush)FindResource("TextBrush"),
                            FontSize = 12,
                            HorizontalAlignment = HorizontalAlignment.Center,
                            VerticalAlignment = VerticalAlignment.Center
                        },
                        IsHitTestVisible = false,
                    };
                    Canvas.SetLeft(tile, r.X);
                    Canvas.SetTop(tile, r.Y);
                    _canvas.Children.Add(tile);
                    _bindingPromptElements.Add(tile);
                    if (keyPrompts.TryGetValue(vk, out List<string>? slots))
                    {
                        AttachPromptIcons(slots, r, promptScale, iconSpan);
                    }
                }
            }

            // ── 3. app-level action list under the grid, wraps at keyboard width ──
            double listY = Layout.GridH * pitch + 6;
            double listX = 4;
            double listWidth = Layout.GridW * pitch;
            System.Windows.Media.Brush textBrush = (System.Windows.Media.Brush)FindResource("TextBrush");
            foreach ((string IconSlot, string Label) entry in appPrompts)
            {
                Canvas icon = KeyMapsAtom.MakeIcon(entry.IconSlot);
                icon.RenderTransform = new ScaleTransform(promptScale, promptScale);
                double entryWidth = iconSpan + 6 + 8 + MeasureTextWidth(entry.Label, 12) + 14;
                if (listX > 4 && listX + entryWidth > listWidth)
                {
                    listX = 4;
                    listY += iconSpan + 8;
                }
                Canvas.SetLeft(icon, listX);
                Canvas.SetTop(icon, listY + (iconSpan * 0.0));
                icon.IsHitTestVisible = false;
                _canvas.Children.Add(icon);
                _bindingPromptElements.Add(icon);
                TextBlock label = new TextBlock
                {
                    Text = entry.Label,
                    Foreground = textBrush,
                    FontSize = 12,
                    VerticalAlignment = VerticalAlignment.Center,
                    IsHitTestVisible = false,
                };
                Canvas.SetLeft(label, listX + iconSpan + 6);
                Canvas.SetTop(label, listY + iconSpan / 2 - 8);
                _canvas.Children.Add(label);
                _bindingPromptElements.Add(label);
                listX += entryWidth;
            }
            if (appPrompts.Count > 0)
            {
                _promptListHeight = listY + iconSpan + 8 - Layout.GridH * pitch;
            }

            SizeToContent();
        }

        /// <summary>Pins a row of pad-button icons to a key's right-upper
        /// corner (same anchor as the modifier badge), chaining leftward so
        /// multiple buttons do not overlap; zero allocations per tick — only
        /// runs on rebuilds.</summary>
        private void AttachPromptIcons(List<string> buttonIds, Rect keyRect, double promptScale, double iconSpan)
        {
            int placed = 0;
            foreach (string buttonId in buttonIds)
            {
                string slot = PromptIconSlot(buttonId);
                if (slot.Length == 0)
                {
                    continue;
                }
                Canvas icon = KeyMapsAtom.MakeIcon(slot);
                icon.RenderTransform = new ScaleTransform(promptScale, promptScale);
                double left = Math.Max(2.0, keyRect.X + keyRect.Width - iconSpan / 2.0
                    + AppSettings.Instance.KeyboardPromptOffsetX - placed * iconSpan * 0.72);
                double top = Math.Max(2.0, keyRect.Y - iconSpan / 2.0
                    + AppSettings.Instance.KeyboardPromptOffsetY);
                Canvas.SetLeft(icon, left);
                Canvas.SetTop(icon, top);
                icon.IsHitTestVisible = false;
                _canvas.Children.Add(icon);
                _bindingPromptElements.Add(icon);
                placed++;
                if (placed >= 3)
                {
                    break;
                }
            }
        }

        /// <summary>Profile button id -> Key Maps icon slot ("" = no icon).</summary>
        private static string PromptIconSlot(string buttonId) => buttonId switch
        {
            "A" => "FaceA",
            "B" => "FaceB",
            "X" => "FaceX",
            "Y" => "FaceY",
            "LB" => "L1",
            "RB" => "R1",
            "LT" => "L2",
            "RT" => "R2",
            "LS" => "LeftStickPress",
            "RS" => "RightStickPress",
            "View" => "Select",
            "Menu" => "Start",
            "Home" => "Start",
            "DUp" => "DPadUp",
            "DDown" => "DPadDown",
            "DLeft" => "DPadLeft",
            "DRight" => "DPadRight",
            "LUp" => "LeftStickUp",
            "LDown" => "LeftStickDown",
            "LLeft" => "LeftStickLeft",
            "LRight" => "LeftStickRight",
            "RUp" => "RightStickUp",
            "RDown" => "RightStickDown",
            "RLeft" => "RightStickLeft",
            "RRight" => "RightStickRight",
            _ => ""
        };

        private static string VkLabel(ushort vk) => vk switch
        {
            Vk.VolumeUp => "Vol+",
            Vk.VolumeDown => "Vol−",
            Vk.VolumeMute => "Mut",
            Vk.MediaPlayPause => "▷∥",
            Vk.MediaNext => "▷▷",
            Vk.MediaPrev => "◁◁",
            Vk.MediaStop => "■",
            Vk.PageUp => "PgUp",
            Vk.PageDown => "PgDn",
            Vk.Insert => "Ins",
            Vk.Delete => "Del",
            Vk.Home => "Home",
            Vk.End => "End",
            Vk.Print => "PrtSc",
            Vk.Scroll => "ScrLk",
            Vk.Pause => "PasBr",
            Vk.Up => "▲",
            Vk.Down => "▼",
            Vk.Left => "◀",
            Vk.Right => "▶",
            _ => ((int)vk).ToString()
        };

        private double MeasureTextWidth(string text, double fontSize)
        {
            FormattedText formatted = new FormattedText(
                text,
                System.Globalization.CultureInfo.CurrentCulture,
                System.Windows.FlowDirection.LeftToRight,
                new Typeface("Segoe UI"),
                fontSize,
                (System.Windows.Media.Brush)FindResource("TextBrush"),
                VisualTreeHelper.GetDpi(this).PixelsPerDip);
            return formatted.Width;
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
            double extraW = 0;
            foreach (KeyboardLayout.KeyDef extra in _promptExtraDefs)
            {
                extraW = Math.Max(extraW, extra.X + extra.W - Layout.GridW);
            }
            _baseW = (Layout.GridW + extraW) * pitch + 8;
            _baseH = Layout.GridH * pitch + 44 + _promptListHeight;
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
            // modifier VKs only — never tint regular keys. CapsLock lights from
            // the OS toggle state (a mapped CapsLock tap flips that state, so the
            // overlay follows both physical and mapped activation).
            HashSet<ushort> next = new HashSet<ushort>(vks.Where(IsModifierVk));
            if (NativeMethods.CapsLockActive)
            {
                next.Add((ushort)0x14);
            }
            if (NativeMethods.ScrollLockActive)
            {
                next.Add((ushort)0x91);
            }

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
            bool capsLockActive = NativeMethods.CapsLockActive;
            if (_shiftActive == active && _capsLockActive == capsLockActive) return;
            _shiftActive = active;
            _capsLockActive = capsLockActive;
            foreach (var pair in _keyBorders)
            {
                if (pair.Value.Child is TextBlock label)
                    label.Text = VisibleKeyLabel(pair.Key, active, capsLockActive);
            }
        }

        internal static string VisibleKeyLabel(KeyboardLayout.KeyDef key, bool shiftActive)
        {
            return VisibleKeyLabel(key, shiftActive, NativeMethods.CapsLockActive);
        }

        internal static string VisibleKeyLabel(KeyboardLayout.KeyDef key, bool shiftActive, bool capsLockActive)
        {
            // Windows semantics: CapsLock uppercases letters; Shift while CapsLock
            // gives lowercase. Symbols follow Shift only.
            bool lettersUppercase = shiftActive ^ capsLockActive;
            if (key.Vk >= 'A' && key.Vk <= 'Z')
                return lettersUppercase ? key.Label.ToUpperInvariant() : key.Label.ToLowerInvariant();
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
            (ushort)0x5B or (ushort)0x5C or   // LWin / RWin
            (ushort)0x14 or (ushort)0x91;     // CapsLock / ScrollLock (OS toggles)

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

        // ── Pad-button badges now live ONLY in binding prompts (RenderBindingPrompts):
        // the old KeyMaps-trigger badges showed Maps-mode leftovers in Keyboard mode.

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

    }
}
