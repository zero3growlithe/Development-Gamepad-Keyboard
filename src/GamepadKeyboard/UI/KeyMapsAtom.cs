using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

using GamepadKeyboard.Settings;

namespace GamepadKeyboard.UI
{
    /// <summary>
    /// One "atom": the visualization of a single Key Maps slot (e.g. the A
    /// button). Structure: the physical gamepad-button graphic above the
    /// center, the big key prompt in the middle, and three smaller prompts
    /// (quarks) to the left / bottom / right.
    ///
    /// Content per map (never moves, only labels change):
    ///   center  = the slot's key on the currently active map
    ///   left    = Symbols 3 (R2+L1)
    ///   right   = Symbols 2 (R2+R1)
    ///   bottom  = Function Keys (R2+L1+R1)
    ///
    /// Visibility/highlight (driven per tick):
    ///   R2 up:    center visible, quarks hidden; pressing the physical button
    ///             highlights the center
    ///   R2 down:  everything visible; center more opaque than quarks;
    ///             pressing the physical button highlights the center
    ///   R2+L1:    left quark most opaque
    ///   R2+R1:    right quark most opaque
    ///   R2+L1+R1: bottom quark most opaque
    /// </summary>
    public sealed class KeyMapsAtom
    {
        // ── Geometry constants (scaled by the atom/quark size settings) ───────

        public const double CenterWidth = 74.0;
        public const double CenterHeight = 46.0;
        public const double QuarkWidth = 52.0;
        public const double QuarkHeight = 30.0;
        public const double IconSpan = 26.0;
        private const double IconGap = 5.0;
        private const double QuarkPitch = 44.0;
        private const double QuarkBottomGap = 16.0;

        // ── Frozen brushes (no per-tick allocation) ─────────────────────────────

        private static readonly Brush CenterFill = Frozen(new SolidColorBrush(Color.FromArgb(0xD8, 0x1B, 0x1B, 0x24)));
        private static readonly Brush CenterFillPressed = Frozen(new SolidColorBrush(Color.FromArgb(0xE6, 0x2E, 0x8B, 0x57)));
        private static readonly Brush CenterBorderIdle = Frozen(new SolidColorBrush(Color.FromArgb(0x88, 0xFF, 0xFF, 0xFF)));
        private static readonly Brush CenterBorderPressed = Frozen(new SolidColorBrush(Color.FromRgb(0x7C, 0xFC, 0x9A)));
        private static readonly Brush QuarkFill = Frozen(new SolidColorBrush(Color.FromArgb(0x90, 0x22, 0x22, 0x2E)));
        private static readonly Brush QuarkFillActive = Frozen(new SolidColorBrush(Color.FromArgb(0xE6, 0x2E, 0x8B, 0x57)));
        private static readonly Brush QuarkBorderIdle = Frozen(new SolidColorBrush(Color.FromArgb(0x55, 0xFF, 0xFF, 0xFF)));
        private static readonly Brush QuarkBorderActive = Frozen(new SolidColorBrush(Color.FromRgb(0x7C, 0xFC, 0x9A)));
        private static readonly Brush IconStroke = Frozen(new SolidColorBrush(Color.FromArgb(0xC8, 0xE8, 0xE8, 0xF4)));

        private static Brush Frozen(Brush brush)
        {
            brush.Freeze();
            return brush;
        }

        /// <summary>Slot whose key feeds the atom's center prompt and whose
        /// physical button drives the highlight (e.g. "FaceA").</summary>
        public readonly string Slot;

        private readonly Canvas _container = new();
        private readonly UIElement _icon;
        private readonly Border _center;
        private readonly TextBlock _centerLabel;
        private readonly Border _quarkLeft;
        private readonly Border _quarkRight;
        private readonly Border _quarkBottom;
        private readonly Dictionary<string, TextBlock> _quarkLabels;
        private readonly Func<KeyMapDefinition, string, string> _labelFor;

        public KeyMapsAtom(string slot, Func<KeyMapDefinition, string, string> labelFor)
        {
            Slot = slot;
            _labelFor = labelFor;
            _icon = MakeIcon(slot);
            _centerLabel = new TextBlock
            {
                Foreground = System.Windows.Media.Brushes.White,
                FontSize = 13,
                FontWeight = FontWeights.SemiBold,
                TextWrapping = TextWrapping.Wrap,
                TextAlignment = TextAlignment.Center,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
            };
            _center = new Border
            {
                Width = CenterWidth,
                Height = CenterHeight,
                CornerRadius = new CornerRadius(9),
                BorderThickness = new Thickness(1.5),
                BorderBrush = CenterBorderIdle,
                Background = CenterFill,
                Child = _centerLabel,
            };
            _quarkLabels = new Dictionary<string, TextBlock>();
            _quarkLeft = MakeQuark("QuarkLeft");
            _quarkRight = MakeQuark("QuarkRight");
            _quarkBottom = MakeQuark("QuarkBottom");

            _container.Children.Add(_icon);
            _container.Children.Add(_center);
            _container.Children.Add(_quarkLeft);
            _container.Children.Add(_quarkRight);
            _container.Children.Add(_quarkBottom);
        }

        /// <summary>The element to add to the board canvas.</summary>
        public UIElement Root => _container;

        private Border MakeQuark(string key)
        {
            TextBlock label = new()
            {
                Foreground = System.Windows.Media.Brushes.White,
                FontSize = 9.5,
                TextWrapping = TextWrapping.Wrap,
                TextAlignment = TextAlignment.Center,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
            };
            _quarkLabels[key] = label;
            return new Border
            {
                Width = QuarkWidth,
                Height = QuarkHeight,
                CornerRadius = new CornerRadius(7),
                BorderThickness = new Thickness(1),
                BorderBrush = QuarkBorderIdle,
                Background = QuarkFill,
                Child = label,
            };
        }

        // ── Layout (position + sizes + quark distance) ───────────────────────

        /// <summary>Applies sizes and the quark distance; called per rebuild.</summary>
        public void LayoutChildren(double atomScale, double quarkScale, double quarkDistanceScale, double iconOffsetX, double iconOffsetY)
        {
            double centerW = CenterWidth * atomScale;
            double centerH = CenterHeight * atomScale;
            double quarkW = QuarkWidth * quarkScale;
            double quarkH = QuarkHeight * quarkScale;

            double iconSize = IconSpan * Math.Max(atomScale, quarkScale);
            Canvas.SetLeft(_icon, iconOffsetX - iconSize / 2.0);
            Canvas.SetTop(_icon, iconOffsetY - iconSize / 2.0);

            Canvas.SetLeft(_center, -centerW / 2.0);
            Canvas.SetTop(_center, -centerH / 2.0);
            _center.Width = centerW;
            _center.Height = centerH;
            _centerLabel.FontSize = Math.Max(9.0, 13.0 * atomScale);

            double sideGap = QuarkPitch * quarkDistanceScale;   // gap between center edge and quark edge
            double bottomY = centerH / 2.0 + quarkH / 2.0 + QuarkBottomGap * quarkDistanceScale;
            Canvas.SetLeft(_quarkLeft, -centerW / 2.0 - sideGap - quarkW);
            Canvas.SetTop(_quarkLeft, -quarkH / 2.0);
            Canvas.SetLeft(_quarkRight, centerW / 2.0 + sideGap);
            Canvas.SetTop(_quarkRight, -quarkH / 2.0);
            Canvas.SetLeft(_quarkBottom, -quarkW / 2.0);
            Canvas.SetTop(_quarkBottom, bottomY);
            _quarkLeft.Width = _quarkRight.Width = _quarkBottom.Width = quarkW;
            _quarkLeft.Height = _quarkRight.Height = _quarkBottom.Height = quarkH;
            foreach (TextBlock label in _quarkLabels.Values)
            {
                label.FontSize = Math.Max(7.5, 9.5 * quarkScale);
            }
        }

        // ── Per-tick state ───────────────────────────────────────────────────

        /// <summary>Re-renders labels + opacity/highlight from live state.</summary>
        public void Update(
            IReadOnlyList<KeyMapDefinition> maps,
            int activeMapIndex,
            bool mapsKeyHeld,
            bool sym2ComboHeld,
            bool sym3ComboHeld,
            bool functionComboHeld,
            bool physicalPressed)
        {
            // Center prompt: the active map's key for this slot.
            _centerLabel.Text = SplitLabel(_labelFor(maps[Math.Clamp(activeMapIndex, 0, maps.Count - 1)], Slot));

            // Quark prompts: the same slot from the combo maps (never move).
            if (maps.Count >= 5)
            {
                _quarkLabels["QuarkLeft"].Text = SplitLabel(_labelFor(maps[3], Slot));
                _quarkLabels["QuarkRight"].Text = SplitLabel(_labelFor(maps[2], Slot));
                _quarkLabels["QuarkBottom"].Text = SplitLabel(_labelFor(maps[4], Slot));
            }

            // Visibility: quarks only while the maps key is held.
            Visibility quarkVisibility = mapsKeyHeld ? Visibility.Visible : Visibility.Collapsed;
            _quarkLeft.Visibility = quarkVisibility;
            _quarkRight.Visibility = quarkVisibility;
            _quarkBottom.Visibility = quarkVisibility;

            // Highlight: pressed button → the element matching the held combo.
            bool centerActive = physicalPressed
                ? !sym2ComboHeld && !sym3ComboHeld && !functionComboHeld
                : mapsKeyHeld && !sym2ComboHeld && !sym3ComboHeld && !functionComboHeld;
            _center.Background = centerActive ? CenterFillPressed : CenterFill;
            _center.BorderBrush = centerActive ? CenterBorderPressed : CenterBorderIdle;

            // Center more opaque than quarks while R2 is held; quark opacity
            // rises for the one matching the held combo (never swaps content).
            double idleQuarkOpacity = mapsKeyHeld ? 0.45 : 0.0;
            _quarkLeft.Opacity = sym3ComboHeld ? 1.0 : idleQuarkOpacity;
            _quarkRight.Opacity = sym2ComboHeld ? 1.0 : idleQuarkOpacity;
            _quarkBottom.Opacity = functionComboHeld ? 1.0 : idleQuarkOpacity;
            _quarkLeft.Background = sym3ComboHeld ? QuarkFillActive : QuarkFill;
            _quarkLeft.BorderBrush = sym3ComboHeld ? QuarkBorderActive : QuarkBorderIdle;
            _quarkRight.Background = sym2ComboHeld ? QuarkFillActive : QuarkFill;
            _quarkRight.BorderBrush = sym2ComboHeld ? QuarkBorderActive : QuarkBorderIdle;
            _quarkBottom.Background = functionComboHeld ? QuarkFillActive : QuarkFill;
            _quarkBottom.BorderBrush = functionComboHeld ? QuarkBorderActive : QuarkBorderIdle;

            // When a combo is held the center dims below the active quark so
            // the "more opaque" rule reads clearly (content stays in place).
            _center.Opacity = sym2ComboHeld || sym3ComboHeld || functionComboHeld ? 0.55 : 1.0;
        }

        // ── Button-icon factory ──────────────────────────────────────────────

        /// <summary>Vector gamepad-button icon keyed by the atom's slot:
        /// face letter, d-pad arrow, stick ring (LS/RS), pill (Select/Start).</summary>
        private static UIElement MakeIcon(string slot)
        {
            Canvas canvas = new()
            {
                Width = IconSpan,
                Height = IconSpan,
            };
            double mid = IconSpan / 2.0;

            switch (slot)
            {
                case "FaceY":
                    AddShape(canvas, MakeGlyph("Y", 0x4C, 0xC8, 0x7E), 3, 3);
                    break;
                case "FaceA":
                    AddShape(canvas, MakeGlyph("A", 0x5C, 0x68, 0xE8), 3, 3);
                    break;
                case "FaceX":
                    AddShape(canvas, MakeGlyph("X", 0x38, 0x68, 0xD8), 3, 3);
                    break;
                case "FaceB":
                    AddShape(canvas, MakeGlyph("B", 0xD8, 0x48, 0x54), 3, 3);
                    break;
                case "DPadUp":
                    AddArrow(canvas, mid, 0.0);
                    break;
                case "DPadDown":
                    AddArrow(canvas, mid, 180.0);
                    break;
                case "DPadLeft":
                    AddArrow(canvas, mid, 270.0);
                    break;
                case "DPadRight":
                    AddArrow(canvas, mid, 90.0);
                    break;
                case "LeftStickUp":
                case "LeftStickDown":
                case "LeftStickLeft":
                case "LeftStickRight":
                case "LeftStickPress":
                    AddStick(canvas, mid, 0x3C, 0x9C, 0xF4);
                    break;
                case "RightStickUp":
                case "RightStickDown":
                case "RightStickLeft":
                case "RightStickRight":
                case "RightStickPress":
                    AddStick(canvas, mid, 0xE0, 0x64, 0xA8);
                    break;
                default:
                    AddShape(canvas, new System.Windows.Shapes.Rectangle
                    {
                        Width = 12,
                        Height = 5,
                        RadiusX = 2.5,
                        RadiusY = 2.5,
                        Fill = IconStroke,
                    }, 5, mid - 2.5);
                    AddShape(canvas, new System.Windows.Shapes.Rectangle
                    {
                        Width = 12,
                        Height = 5,
                        RadiusX = 2.5,
                        RadiusY = 2.5,
                        Fill = IconStroke,
                    }, IconSpan - 17, mid - 2.5);
                    break;
            }

            return canvas;
        }

        private static System.Windows.Controls.Grid MakeGlyph(string letter, byte r, byte g, byte b)
        {
            System.Windows.Shapes.Ellipse ring = new()
            {
                Width = IconSpan - 6,
                Height = IconSpan - 6,
                Stroke = new SolidColorBrush(Color.FromRgb(r, g, b)),
                StrokeThickness = 2.2,
            };
            System.Windows.Controls.TextBlock text = new()
            {
                Text = letter,
                Foreground = new SolidColorBrush(Color.FromRgb(r, g, b)),
                FontSize = 11,
                FontWeight = FontWeights.Bold,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
            };
            System.Windows.Controls.Grid grid = new();
            grid.Children.Add(ring);
            grid.Children.Add(text);
            return grid;
        }

        private static void AddArrow(Canvas canvas, double mid, double degrees)
        {
            System.Windows.Shapes.Polygon arrow = new()
            {
                Points = new PointCollection
                {
                    new Point(mid, 2),
                    new Point(mid + 7, 12),
                    new Point(mid - 7, 12),
                },
                Fill = IconStroke,
                RenderTransform = new RotateTransform(degrees, mid, mid),
            };
            canvas.Children.Add(arrow);
            System.Windows.Shapes.Rectangle stem = new()
            {
                Width = 8,
                Height = 12,
                RadiusX = 2,
                RadiusY = 2,
                Fill = IconStroke,
            };
            AddShape(canvas, stem, mid - 4, 12);
        }

        private static void AddStick(Canvas canvas, double mid, byte r, byte g, byte b)
        {
            System.Windows.Shapes.Ellipse ring = new()
            {
                Width = IconSpan - 6,
                Height = IconSpan - 6,
                Stroke = new SolidColorBrush(Color.FromRgb(r, g, b)),
                StrokeThickness = 2.4,
            };
            Canvas.SetLeft(ring, 3);
            Canvas.SetTop(ring, 3);
            canvas.Children.Add(ring);
        }

        private static void AddShape(Canvas canvas, UIElement element, double x, double y)
        {
            Canvas.SetLeft(element, x);
            Canvas.SetTop(element, y);
            canvas.Children.Add(element);
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