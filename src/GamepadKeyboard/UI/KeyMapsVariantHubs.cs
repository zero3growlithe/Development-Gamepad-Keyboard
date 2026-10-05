using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;

using GamepadKeyboard.Settings;

namespace GamepadKeyboard.UI
{
    /// <summary>
    /// One modular per-wheel "variant hub": a gamepad-element vector icon above a
    /// big center button, with three small combo-variant buttons pinned around it
    /// (Symbols 3 = R2+L1 upper-LEFT, Symbols 2 = R2+R1 upper-RIGHT, Function
    /// Keys = R2+L1+R1 below). Hub center shows the Utility key while the maps
    /// key is up and Character set 1 (Symbols 1) while it is held; satellites
    /// list this group's keys from Symbols 2 / Symbols 3 / Function Keys and
    /// only render while the maps key is held (combos without R2 mean nothing).
    /// Highlighting (never swapping content): only R2 → center; R2+L1 → left;
    /// R2+R1 → right; R2+L1+R1 → bottom.
    /// Position: wheel-anchor + per-wheel X/Y offset (settings sliders); sizes:
    /// one global center-size and one global variant-size multiplier.
    /// </summary>
    public sealed class KeyMapsVariantHub
    {
        public const double CenterWidth = 96.0;
        public const double CenterHeight = 58.0;
        public const double VariantWidth = 56.0;
        public const double VariantHeight = 34.0;
        public const double IconSpan = 34.0;

        private const double VariantPitchX = 60.0;
        private const double VariantPitchY = 38.0;
        private const double VariantTopY = -66.0;
        private const double VariantBottomY = 66.0;
        private const double IconLift = 24.0;

        private static readonly Brush ActiveFill = Frozen(new SolidColorBrush(Color.FromArgb(0xE6, 0x2E, 0x8B, 0x57)));
        private static readonly Brush ActiveBorder = Frozen(new SolidColorBrush(Color.FromRgb(0x7C, 0xFC, 0x9A)));
        private static readonly Brush IdleVariantFill = Frozen(new SolidColorBrush(Color.FromArgb(0xB0, 0x18, 0x18, 0x22)));
        private static readonly Brush IdleVariantBorder = Frozen(new SolidColorBrush(Color.FromArgb(0x66, 0xFF, 0xFF, 0xFF)));
        private static readonly Brush IdleCenterFill = Frozen(new SolidColorBrush(Color.FromArgb(0xD8, 0x1B, 0x1B, 0x24)));
        private static readonly Brush IdleCenterBorder = Frozen(new SolidColorBrush(Color.FromArgb(0x88, 0xFF, 0xFF, 0xFF)));

        private static Brush Frozen(Brush brush)
        {
            brush.Freeze();
            return brush;
        }

        /// <summary>Slot whose Utility label feeds the center button.</summary>
        public readonly string CenterSlot;

        private readonly Canvas _container = new();
        private readonly Border _centerBorder;
        private readonly TextBlock _centerLabel;
        private readonly Border _variantR1;
        private readonly Border _variantL1;
        private readonly Border _variantFn;
        private readonly UIElement _icon;

        public KeyMapsVariantHub(string centerSlot, Func<KeyMapDefinition, string, string> labelFor)
        {
            CenterSlot = centerSlot;

            // ── Icon (vector, drawn from shapes; sits above the center button) ──
            _icon = MakeIcon(centerSlot);

            // ── Center button ──
            _centerLabel = new TextBlock
            {
                Foreground = Brushes.White,
                FontSize = 14,
                FontWeight = FontWeights.SemiBold,
                TextWrapping = TextWrapping.Wrap,
                TextAlignment = TextAlignment.Center,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
            };
            _centerBorder = new Border
            {
                Width = CenterWidth,
                Height = CenterHeight,
                CornerRadius = new CornerRadius(10),
                BorderThickness = new Thickness(1.5),
                BorderBrush = IdleCenterBorder,
                Background = IdleCenterFill,
                Child = _centerLabel,
            };

            // ── Variant satellites ──
            _variantR1 = MakeVariant();
            _variantL1 = MakeVariant();
            _variantFn = MakeVariant();

            _container.Children.Add(_icon);
            _container.Children.Add(_centerBorder);
            _container.Children.Add(_variantR1);
            _container.Children.Add(_variantL1);
            _container.Children.Add(_variantFn);
            LayoutChildren(1.0, 1.0);

            // initial labels
            _centerLabel.Text = SplitLabel(labelFor(AppSettings.Instance.KeyMaps.Maps[0], centerSlot));
        }

        /// <summary>The element to add to the board canvas.</summary>
        public UIElement Root => _container;

        /// <summary>Repositions children after a size-multiplier change; also
        /// re-applies them on every rebuild (cheap, no allocation).</summary>
        public void LayoutChildren(double centerScale, double variantScale)
        {
            double centerW = CenterWidth * centerScale;
            double centerH = CenterHeight * centerScale;
            double variantW = VariantWidth * variantScale;
            double variantH = VariantHeight * variantScale;
            double pitchX = VariantPitchX * variantScale;
            double pitchDown = VariantBottomY * variantScale;

            // center under the icon
            double centerX = -centerW / 2.0;
            double centerY = -centerH / 2.0 + IconLift;
            Canvas.SetLeft(_centerBorder, centerX);
            Canvas.SetTop(_centerBorder, centerY);

            // icon sits above the center button, centered
            double iconSize = Math.Max(IconSpan, 22.0) * Math.Max(centerScale, variantScale);
            Canvas.SetLeft(_icon, -iconSize / 2.0);
            Canvas.SetTop(_icon, centerY - iconSize - 6.0);

            // satellites: L1 (Sym 3) upper-LEFT, R1 (Sym 2) upper-RIGHT, L1+R1 below
            Canvas.SetLeft(_variantL1, centerX - variantW - pitchX * 0.35);
            Canvas.SetTop(_variantL1, VariantTopY * variantScale);
            Canvas.SetLeft(_variantR1, centerX + centerW + pitchX * 0.35);
            Canvas.SetTop(_variantR1, VariantTopY * variantScale);
            Canvas.SetLeft(_variantFn, centerX + centerW / 2.0 - variantW / 2.0);
            Canvas.SetTop(_variantFn, centerY + centerH + pitchDown * 0.28);

            _centerBorder.Width = centerW;
            _centerBorder.Height = centerH;
            _centerLabel.FontSize = Math.Max(9.0, 14.0 * centerScale);
            _variantR1.Width = _variantL1.Width = _variantFn.Width = variantW;
            _variantR1.Height = _variantL1.Height = _variantFn.Height = variantH;
        }

        /// <summary>Per-tick label/visibility/highlight update from live mapper
        /// state. Content never moves: center = Symbols 1, left = Symbols 3,
        /// right = Symbols 2, bottom = Function Keys — highlighting marks which
        /// combo is currently held.</summary>
        public void Update(
            IReadOnlyList<KeyMapDefinition> maps,
            bool mapsKeyHeld,
            bool sym2ComboHeld,
            bool sym3ComboHeld,
            bool functionComboHeld,
            Func<KeyMapDefinition, string, string> labelFor)
        {
            int utilityIndex = 0;
            int centerMapIndex = mapsKeyHeld ? Math.Min(1, maps.Count - 1) : utilityIndex;
            string centerLabel = labelFor(maps[centerMapIndex], CenterSlot);
            _centerLabel.Text = SplitLabel(centerLabel);

            bool centerHighlighted = mapsKeyHeld && !sym2ComboHeld && !sym3ComboHeld && !functionComboHeld;
            _centerBorder.Opacity = mapsKeyHeld ? 1.0 : 0.88;
            _centerBorder.BorderBrush = centerHighlighted ? ActiveBorder : IdleCenterBorder;
            _centerBorder.Background = centerHighlighted ? ActiveFill : IdleCenterFill;

            // Satellites exist only while the maps key is held (combos without
            // R2 mean nothing). Symbols 3 = R2+L1 (left), Symbols 2 = R2+R1
            // (right), Function Keys = R2+L1+R1 (bottom) — maps indices 3 / 2 / 4.
            if (!mapsKeyHeld || maps.Count < 5)
            {
                _variantR1.Visibility = Visibility.Collapsed;
                _variantL1.Visibility = Visibility.Collapsed;
                _variantFn.Visibility = Visibility.Collapsed;
                return;
            }

            SetVariant(_variantR1, labelFor(maps[2], CenterSlot));
            SetVariant(_variantL1, labelFor(maps[3], CenterSlot));
            SetVariant(_variantFn, labelFor(maps[4], CenterSlot));
            HighlightVariant(_variantR1, sym2ComboHeld);
            HighlightVariant(_variantL1, sym3ComboHeld);
            HighlightVariant(_variantFn, functionComboHeld);
            _variantR1.Visibility = Visibility.Visible;
            _variantL1.Visibility = Visibility.Visible;
            _variantFn.Visibility = Visibility.Visible;
        }

        private static void HighlightVariant(Border border, bool active)
        {
            border.Background = active ? ActiveFill : IdleVariantFill;
            border.BorderBrush = active ? ActiveBorder : IdleVariantBorder;
        }

        private void SetVariant(Border border, string label)
        {
            if (border.Child is TextBlock block)
            {
                block.Text = SplitLabel(label);
            }
        }

        private static Border MakeVariant()
        {
            return new Border
            {
                Width = VariantWidth,
                Height = VariantHeight,
                CornerRadius = new CornerRadius(7),
                BorderThickness = new Thickness(1),
                BorderBrush = IdleVariantBorder,
                Background = IdleVariantFill,
                Child = new TextBlock
                {
                    Foreground = Brushes.White,
                    FontSize = 10,
                    TextWrapping = TextWrapping.Wrap,
                    TextAlignment = TextAlignment.Center,
                    HorizontalAlignment = HorizontalAlignment.Center,
                    VerticalAlignment = VerticalAlignment.Center,
                },
            };
        }

        /// <summary>Vector gamepad-element icon keyed by the hub's center slot:
        /// cross for D-Pad, four dots for face, one stick circle with a dot for
        /// sticks, two small pills for the Select/Start pair.</summary>
        private static UIElement MakeIcon(string centerSlot)
        {
            Canvas canvas = new()
            {
                Width = IconSpan,
                Height = IconSpan,
            };
            Brush stroke = new SolidColorBrush(Color.FromArgb(0xC0, 0xE8, 0xE8, 0xF4));
            double mid = IconSpan / 2.0;

            if (centerSlot == "DPadUp" || centerSlot == "DPadDown")
            {
                double arm = 6.0;
                AddShape(canvas, new Rectangle { Width = IconSpan, Height = arm, RadiusX = 2, RadiusY = 2, Fill = stroke }, 0, mid - arm / 2.0);
                AddShape(canvas, new Rectangle { Width = arm, Height = IconSpan, RadiusX = 2, RadiusY = 2, Fill = stroke }, mid - arm / 2.0, 0);
            }
            else if (centerSlot == "FaceY" || centerSlot == "FaceA")
            {
                AddDot(canvas, mid, mid - 11, 5.0, stroke);
                AddDot(canvas, mid + 11, mid, 5.0, stroke);
                AddDot(canvas, mid, mid + 11, 5.0, stroke);
                AddDot(canvas, mid - 11, mid, 5.0, stroke);
            }
            else if (centerSlot == "LeftStickPress" || centerSlot == "RightStickPress")
            {
                Ellipse ring = new()
                {
                    Width = IconSpan - 6,
                    Height = IconSpan - 6,
                    Stroke = stroke,
                    StrokeThickness = 2.4,
                    StrokeDashArray = new DoubleCollection { 2.4, 1.8 },
                };
                AddShape(canvas, ring, 3, 3);
                AddDot(canvas, mid, mid, 4.2, stroke);
            }
            else
            {
                // Select/Start pair (center hub fallback)
                AddShape(canvas, new Rectangle { Width = 12, Height = 5, RadiusX = 2.5, RadiusY = 2.5, Fill = stroke }, 5, mid - 2.5);
                AddShape(canvas, new Rectangle { Width = 12, Height = 5, RadiusX = 2.5, RadiusY = 2.5, Fill = stroke }, IconSpan - 17, mid - 2.5);
            }

            return canvas;
        }

        private static void AddShape(Canvas canvas, UIElement element, double x, double y)
        {
            Canvas.SetLeft(element, x);
            Canvas.SetTop(element, y);
            canvas.Children.Add(element);
        }

        private static void AddDot(Canvas canvas, double cx, double cy, double radius, Brush fill)
        {
            AddShape(canvas, new Ellipse { Width = radius * 2, Height = radius * 2, Fill = fill }, cx - radius, cy - radius);
        }

        /// <summary>Same humanization as the board's tile labels.</summary>
        private static string SplitLabel(string name)
        {
            if (string.IsNullOrEmpty(name))
            {
                return "";
            }
            string display = name switch
            {
                "PrntScrn" => "Print Screen",
                "ScrlLock" => "Scroll Lock",
                "PauseBreak" => "Pause Break",
                "Xmouse1" => "Mouse 1",
                "Xmouse2" => "Mouse 2",
                _ => name,
            };
            System.Text.StringBuilder spaced = new();
            for (int index = 0; index < display.Length; index++)
            {
                char current = display[index];
                bool previousIsLower = index > 0 && char.IsLower(display[index - 1]);
                if (index > 0 && char.IsUpper(current) && previousIsLower)
                {
                    spaced.Append(' ');
                }
                spaced.Append(current);
            }
            return spaced.ToString();
        }
    }
}