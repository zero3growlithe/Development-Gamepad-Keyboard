using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using GamepadKeyboard.Input;
using GamepadKeyboard.Native;
using GamepadKeyboard.Settings;

namespace GamepadKeyboard.UI
{
    /// <summary>
    /// Key Maps overlay: a passive always-on-top HUD shown only while input is
    /// enabled and the mapper is in DirectInput (Key Maps) mode. The modifier
    /// row sits at the top ([Ctrl][Shift][Alt][Windows] + current map name).
    /// Below it, the center pad shows the ACTIVE map opaque; while R2 is held
    /// the combo maps appear as half-transparent shadow strips at fixed side
    /// positions — Symbols 3 (R2+L1) LEFT, Symbols 2 (R2+R1) RIGHT, Function
    /// Keys (R2+L1+R1) BELOW — brightening to full opacity while their combo
    /// is actually held. When a combo map becomes active the center swaps
    /// opacities (the old keys become shadows at the same positions), so every
    /// physical slot always reads at the same spot: positions are FIXED for
    /// stable muscle memory.
    /// </summary>
    public sealed class KeyMapsOverlayWindow : Window
    {
        // ── Layout: center pad grid (1 unit = Pitch px) ────────────────────────
        //
        //        [Ctrl] [Shift] [Alt] [Win]  CurrentMapName     ← separate row
        //  row 0:    DPad↑      LS↑        RS↑        FaceY
        //  row 1:  DPad←  DPad→ LS←  LS→   RS←  RS→   FaceX  FaceB
        //  row 2:    DPad↓      LS↓        RS↓        FaceA
        //  row 3:           [LStickPress]  [RStickPress]
        //  row 5:              [Select]    [Start]
        //
        //  left strip = R2+L1 map, right strip = R2+R1 map, bottom strip =
        //  R2+L1+R1 map — all three rendered only while R2 is held.

        private const double Pitch = 50;
        private const double PadOffsetX = 165;   // center pad drawn inside the shared canvas
        private const int MapUtility = 0;
        private const int MapSymbols1 = 1;
        private const int MapSymbols2 = 2;
        private const int MapSymbols3 = 3;
        private const int MapFunctionKeys = 4;
        private const double ShadowOpacity = 0.42;
        private const double UnboundOpacity = 0.15;
        private const double FallbackOpacity = 0.6;

        private readonly Canvas _rootCanvas;
        private readonly Canvas _padCanvas;
        private readonly Dictionary<string, Border> _slotBorders = new(StringComparer.Ordinal);
        private readonly List<Border> _orderedSlotBorders = new();
        private readonly List<MapStrip> _strips = new();
        private readonly Border _ctrlChip;
        private readonly Border _shiftChip;
        private readonly Border _altChip;
        private readonly Border _windowsChip;
        private readonly TextBlock _mapNameLabel;
        private readonly Brush _modifierTintBrush = new SolidColorBrush(Color.FromArgb(0xFF, 0x28, 0xBE, 0x5A));
        private readonly Brush _activeBorderBrush = new SolidColorBrush(Color.FromArgb(0xFF, 0xFF, 0xB0, 0x3A));
        private readonly Brush _keyBrush;
        private readonly Brush _keyBorderBrush;
        private readonly Brush _textBrush;
        private readonly Brush _dimTextBrush;
        private bool _shown;
        private bool _lastReady;

        /// <summary>One combo side strip: a bordered panel listing its map's keys.</summary>
        private sealed class MapStrip
        {
            public int MapIndex;
            public string Header = "";
            public StackPanel Panel = new() { Orientation = Orientation.Vertical };
            public Border Frame = new();
            public string Body = "";
        }

        public KeyMapsOverlayWindow()
        {
            // App-level resources (App.xaml) are available at construction time.
            _keyBrush = (Brush)FindResource("KeyBrush");
            _keyBorderBrush = (Brush)FindResource("KeyBorderBrush");
            _textBrush = (Brush)FindResource("TextBrush");
            _dimTextBrush = (Brush)FindResource("DimTextBrush");

            WindowStyle = WindowStyle.None;
            AllowsTransparency = true;
            Background = Brushes.Transparent;
            ResizeMode = ResizeMode.NoResize;
            ShowInTaskbar = false;
            Topmost = true;
            ShowActivated = false;
            Width = 770;
            Height = 590;
            Left = 60;
            Top = 60;

            _ctrlChip = BuildModifierChip("Ctrl");
            _shiftChip = BuildModifierChip("Shift");
            _altChip = BuildModifierChip("Alt");
            _windowsChip = BuildModifierChip("Win");
            _mapNameLabel = new TextBlock
            {
                Text = "",
                Foreground = _textBrush,
                FontSize = 14,
                FontWeight = FontWeights.SemiBold,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(10, 0, 0, 0)
            };
            StackPanel modifierRow = new() { Orientation = Orientation.Horizontal };
            modifierRow.HorizontalAlignment = HorizontalAlignment.Center;
            modifierRow.Margin = new Thickness(0, 6, 0, 6);
            modifierRow.Children.Add(_ctrlChip);
            modifierRow.Children.Add(BuildChipSpacer());
            modifierRow.Children.Add(_shiftChip);
            modifierRow.Children.Add(BuildChipSpacer());
            modifierRow.Children.Add(_altChip);
            modifierRow.Children.Add(BuildChipSpacer());
            modifierRow.Children.Add(_windowsChip);
            modifierRow.Children.Add(_mapNameLabel);

            _rootCanvas = new Canvas
            {
                Width = 738,
                Height = 490,
                HorizontalAlignment = HorizontalAlignment.Center
            };
            _padCanvas = BuildPadCanvas();
            Canvas.SetLeft(_padCanvas, PadOffsetX);
            Canvas.SetTop(_padCanvas, 0);
            _rootCanvas.Children.Add(_padCanvas);

            // Fixed side positions per spec: L1-combo LEFT, R1-combo RIGHT,
            // L1+R1-combo BELOW. Each strip lists its own map's keys.
            _strips.Add(BuildStrip(MapSymbols3, "R2 + L1", 0, 8));
            _strips.Add(BuildStrip(MapSymbols2, "R2 + R1", 588, 8));
            _strips.Add(BuildStrip(MapFunctionKeys, "R2 + L1 + R1", PadOffsetX, 400));

            StackPanel centeringPanel = new() { Orientation = Orientation.Vertical };
            centeringPanel.Children.Add(modifierRow);
            centeringPanel.Children.Add(_rootCanvas);
            Border rootBorder = new()
            {
                Background = (Brush)FindResource("PanelBrush"),
                CornerRadius = new CornerRadius(8),
                Padding = new Thickness(10),
                Child = centeringPanel
            };
            Content = rootBorder;
        }

        protected override void OnSourceInitialized(EventArgs e)
        {
            base.OnSourceInitialized(e);
            // Passive overlay: never activates, never steals focus, clicks pass
            // through (same treatment as the legend / keyboard overlays).
            IntPtr hwnd = new WindowInteropHelper(this).Handle;
            int extendedStyle = NativeMethods.GetWindowLong(hwnd, NativeMethods.GWL_EXSTYLE);
            NativeMethods.SetWindowLong(hwnd, NativeMethods.GWL_EXSTYLE,
                extendedStyle | NativeMethods.WS_EX_LAYERED | NativeMethods.WS_EX_TRANSPARENT |
                NativeMethods.WS_EX_NOACTIVATE | NativeMethods.WS_EX_TOOLWINDOW);
        }

        // ── Build: center pad grid, positions FIXED across all five maps ──────

        private Canvas BuildPadCanvas()
        {
            Canvas canvas = new()
            {
                Width = 7.4 * Pitch + 8,
                Height = 6.2 * Pitch + 8
            };

            canvas.Children.Add(BuildSlot("DPadUp", 0.85, 0));
            canvas.Children.Add(BuildSlot("DPadLeft", 0, 1));
            canvas.Children.Add(BuildSlot("DPadRight", 1.7, 1));
            canvas.Children.Add(BuildSlot("DPadDown", 0.85, 2));

            canvas.Children.Add(BuildSlot("LeftStickUp", 2.8, 0));
            canvas.Children.Add(BuildSlot("LeftStickLeft", 2.4, 1));
            canvas.Children.Add(BuildSlot("LeftStickRight", 3.2, 1));
            canvas.Children.Add(BuildSlot("LeftStickDown", 2.8, 2));
            canvas.Children.Add(BuildSlot("LeftStickPress", 2.5, 3, 1.6, 0.7));

            canvas.Children.Add(BuildSlot("RightStickUp", 4.3, 0));
            canvas.Children.Add(BuildSlot("RightStickLeft", 3.9, 1));
            canvas.Children.Add(BuildSlot("RightStickRight", 4.7, 1));
            canvas.Children.Add(BuildSlot("RightStickDown", 4.3, 2));
            canvas.Children.Add(BuildSlot("RightStickPress", 4.0, 3, 1.6, 0.7));

            canvas.Children.Add(BuildSlot("FaceY", 5.65, 0));
            canvas.Children.Add(BuildSlot("FaceX", 5.15, 1));
            canvas.Children.Add(BuildSlot("FaceB", 6.15, 1));
            canvas.Children.Add(BuildSlot("FaceA", 5.65, 2));

            // Select/Start: bound on the Utility and Function Keys maps only.
            canvas.Children.Add(BuildSlot("Select", 2.3, 5.2, 1.3, 0.8));
            canvas.Children.Add(BuildSlot("Start", 3.6, 5.2, 1.5, 0.8));
            return canvas;
        }

        private Border BuildSlot(string slot, double column, double row, double widthUnits = 1, double heightUnits = 1)
        {
            Border border = new()
            {
                Width = widthUnits * Pitch - 8,
                Height = heightUnits * Pitch - 8,
                BorderBrush = _keyBorderBrush,
                BorderThickness = new Thickness(1),
                Background = _keyBrush,
                CornerRadius = new CornerRadius(4),
                Opacity = UnboundOpacity,
                Child = new TextBlock
                {
                    Text = "",
                    Foreground = _textBrush,
                    FontSize = 13,
                    HorizontalAlignment = HorizontalAlignment.Center,
                    VerticalAlignment = VerticalAlignment.Center
                }
            };
            Canvas.SetLeft(border, column * Pitch + 4);
            Canvas.SetTop(border, row * Pitch + 4);
            _slotBorders[slot] = border;
            _orderedSlotBorders.Add(border);
            return border;
        }

        private Border BuildModifierChip(string label)
        {
            return new Border
            {
                Background = _keyBrush,
                BorderBrush = _keyBorderBrush,
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(4),
                Padding = new Thickness(9, 3, 9, 3),
                Opacity = 0.35,
                Child = new TextBlock
                {
                    Text = label,
                    Foreground = _textBrush,
                    FontSize = 12,
                    FontWeight = FontWeights.SemiBold
                }
            };
        }

        private static FrameworkElement BuildChipSpacer()
        {
            return new Border { Width = 7, Background = Brushes.Transparent };
        }

        private MapStrip BuildStrip(int mapIndex, string header, double x, double y)
        {
            MapStrip strip = new()
            {
                MapIndex = mapIndex,
                Header = header,
                Panel = { Opacity = 0 }
            };
            Border frame = new()
            {
                BorderBrush = _keyBorderBrush,
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(4),
                Padding = new Thickness(6, 4, 6, 4),
                Child = strip.Panel
            };
            strip.Frame = frame;
            Canvas.SetLeft(frame, x);
            Canvas.SetTop(frame, y);
            _rootCanvas.Children.Add(frame);
            return strip;
        }

        // ── Per-refresh update (brush/label swaps only, no tree changes) ───────

        /// <summary>
        /// Shows/hides the overlay and applies the mapper state. Call on the UI
        /// thread (AppOrchestrator already guarantees that).
        /// </summary>
        public void Update(ControllerMapper mapper)
        {
            bool ready = mapper.InputEnabled
                         && mapper.Mode == ControllerMapper.MapperMode.DirectInput
                         && AppSettings.Instance.KeyMaps.ShowOverlay;
            if (ready != _lastReady)
            {
                _lastReady = ready;
                if (ready)
                {
                    RefreshState(mapper.KeyMaps);
                    if (!_shown)
                    {
                        Show();
                        _shown = true;
                    }
                }
                else
                {
                    if (_shown)
                    {
                        Hide();
                        _shown = false;
                    }
                }
                return;
            }

            if (!ready)
            {
                return;
            }
            RefreshState(mapper.KeyMaps);
        }

        private void RefreshState(KeyMapsMapper? state)
        {
            if (state == null)
            {
                return;
            }
            List<KeyMapDefinition> maps = AppSettings.Instance.KeyMaps.Maps;
            if (maps.Count < 5)
            {
                return;
            }
            bool mapsKeyHeld = state.MapsKeyHeld;
            int active = Math.Clamp(state.ActiveMapIndex, MapUtility, MapFunctionKeys);
            KeyMapDefinition utility = maps[MapUtility];
            KeyMapDefinition current = maps[active];

            string wantedLabel = current.Name;
            if (_mapNameLabel.Text != wantedLabel)
            {
                _mapNameLabel.Text = wantedLabel;
            }

            // Select/Start: bound on the Utility and Function Keys maps only —
            // on the three symbols maps the slots are unbound (dim dash).
            bool activeBindsSelectStart = active == MapUtility || active == MapFunctionKeys;
            KeyMapDefinition selectStartMap = active == MapFunctionKeys ? maps[MapFunctionKeys] : utility;
            ApplySlot("Select",
                activeBindsSelectStart ? selectStartMap.Select : "",
                activeBindsSelectStart && !IsUnbound(selectStartMap.Select) ? 1 : UnboundOpacity,
                active == MapFunctionKeys);
            ApplySlot("Start",
                activeBindsSelectStart ? UtilityStartLabel(selectStartMap.Start) : "",
                activeBindsSelectStart && !IsUnbound(selectStartMap.Start) ? 1 : UnboundOpacity,
                active == MapFunctionKeys);

            // Face/d-pad/stick slots: the ACTIVE map's keys render full; while
            // R2 is held the Utility keys stay visible as shadows at the SAME
            // positions (they are where release lands, so their targets must
            // not move). Unbound slots show a dim dash.
            bool comboMapActive = mapsKeyHeld && active != MapUtility;
            foreach (string slot in SlotKeyNames)
            {
                string activeBinding = SlotValue(current, slot);
                string utilityBinding = SlotValue(utility, slot);
                if (comboMapActive)
                {
                    if (!IsUnbound(activeBinding))
                    {
                        ApplySlot(slot, activeBinding, 1, true);
                    }
                    else if (!IsUnbound(utilityBinding))
                    {
                        ApplySlot(slot, utilityBinding, FallbackOpacity, false);
                    }
                    else
                    {
                        ApplySlot(slot, "", UnboundOpacity, false);
                    }
                }
                else
                {
                    ApplySlot(slot, utilityBinding,
                        IsUnbound(utilityBinding) ? UnboundOpacity : 1, false);
                }
            }

            // Side strips: visible only while R2 is held; a strip brightens to
            // full opacity while ITS combo is exactly held.
            foreach (MapStrip strip in _strips)
            {
                UpdateStrip(strip, maps, mapsKeyHeld, active);
            }

            ApplyChip(_ctrlChip, state.CtrlHeld);
            ApplyChip(_shiftChip, state.ShiftHeld);
            ApplyChip(_altChip, state.AltHeld);
            // Windows is a mappable key, not a modifier — reminder chip only.
            if (_windowsChip.Opacity != 1)
            {
                _windowsChip.Opacity = 1;
            }
        }

        private void UpdateStrip(MapStrip strip, List<KeyMapDefinition> maps, bool mapsKeyHeld, int active)
        {
            KeyMapDefinition map = maps[Math.Clamp(strip.MapIndex, 0, maps.Count - 1)];
            string body = StripBody(map);
            if (!string.Equals(strip.Body, body, StringComparison.Ordinal))
            {
                strip.Body = body;
                RebuildStripLines(strip, body);
            }
            bool bright = mapsKeyHeld && active == strip.MapIndex;
            // Spec: shadows at ~0.4 while R2 is held; the strip of the combo
            // actually held brightens to full. When that combo map is active
            // the center pad shows the same map — the duplicated readout is
            // per spec ("all keys visible", positions fixed).
            double opacity = mapsKeyHeld ? (bright ? 1.0 : ShadowOpacity) : 0;
            if (strip.Frame.Opacity != opacity)
            {
                strip.Frame.Opacity = opacity;
            }
        }

        private void RebuildStripLines(MapStrip strip, string body)
        {
            strip.Panel.Children.Clear();
            strip.Panel.Children.Add(new TextBlock
            {
                Text = strip.Header,
                Foreground = _dimTextBrush,
                FontSize = 11,
                FontWeight = FontKeys(),
                Margin = new Thickness(2, 0, 0, 2)
            });
            string[] lines = body.Split('\n');
            foreach (string line in lines)
            {
                strip.Panel.Children.Add(new TextBlock
                {
                    Text = line,
                    Foreground = _textBrush,
                    FontSize = 11
                });
            }
        }

        private static System.Windows.FontWeight FontKeys()
        {
            return FontWeights.SemiBold;
        }

        private static string UtilityStartLabel(string startSlot)
        {
            // The overlay shows what Start DOES, not the raw action name.
            return string.Equals(startSlot, "MouseMode", StringComparison.OrdinalIgnoreCase)
                ? "Mouse"
                : startSlot;
        }

        /// <summary>Multi-line body listing the map's own slot keys.</summary>
        private static string StripBody(KeyMapDefinition map)
        {
            return "D-pad  " + Short(map.DPadUp) + " " + Short(map.DPadDown) + " "
                   + Short(map.DPadLeft) + " " + Short(map.DPadRight) + "\n"
                   + "Face   " + Short(map.FaceY) + " " + Short(map.FaceA) + " "
                   + Short(map.FaceX) + " " + Short(map.FaceB) + "\n"
                   + "L-st   " + Short(map.LeftStickUp) + " " + Short(map.LeftStickDown) + " "
                   + Short(map.LeftStickLeft) + " " + Short(map.LeftStickRight)
                   + (IsUnbound(map.LeftStickPress) ? "" : "  L3:" + Short(map.LeftStickPress)) + "\n"
                   + "R-st   " + Short(map.RightStickUp) + " " + Short(map.RightStickDown) + " "
                   + Short(map.RightStickLeft) + " " + Short(map.RightStickRight)
                   + (IsUnbound(map.RightStickPress) ? "" : "  R3:" + Short(map.RightStickPress)) + "\n"
                   + (IsUnbound(map.Select) && IsUnbound(map.Start)
                       ? ""
                       : "Sel    " + Short(map.Select) + "   Start " + Short(map.Start) + "\n");
        }

        private static string Short(string? slot)
        {
            return IsUnbound(slot) ? "—" : slot;
        }

        private void ApplySlot(string slot, string label, double opacity, bool active)
        {
            if (!_slotBorders.TryGetValue(slot, out Border? border) || border == null)
            {
                return;
            }
            if (border.Opacity != opacity)
            {
                border.Opacity = opacity;
            }
            Brush wantedBorder = active ? _activeBorderBrush : _keyBorderBrush;
            if (!ReferenceEquals(border.BorderBrush, wantedBorder))
            {
                border.BorderBrush = wantedBorder;
            }
            if (border.Child is TextBlock textBlock && textBlock.Text != label)
            {
                textBlock.Text = label;
            }
        }

        private void ApplyChip(Border chip, bool held)
        {
            double wantedOpacity = held ? 1 : 0.35;
            if (chip.Opacity != wantedOpacity)
            {
                chip.Opacity = wantedOpacity;
            }
            Brush wantedBrush = held ? _modifierTintBrush : _keyBrush;
            if (!ReferenceEquals(chip.Background, wantedBrush))
            {
                chip.Background = wantedBrush;
            }
        }

        // ── Static slot tables ─────────────────────────────────────────────────

        private static readonly string[] SlotKeyNames =
        {
            "DPadUp", "DPadDown", "DPadLeft", "DPadRight",
            "FaceY", "FaceA", "FaceX", "FaceB",
            "LeftStickUp", "LeftStickDown", "LeftStickLeft", "LeftStickRight", "LeftStickPress",
            "RightStickUp", "RightStickDown", "RightStickLeft", "RightStickRight", "RightStickPress"
        };

        private static bool IsUnbound(string? slot)
        {
            return string.IsNullOrWhiteSpace(slot)
                   || string.Equals(slot, "None", StringComparison.OrdinalIgnoreCase);
        }

        private static string SlotValue(KeyMapDefinition map, string slot)
        {
            return slot switch
            {
                "DPadUp" => map.DPadUp,
                "DPadDown" => map.DPadDown,
                "DPadLeft" => map.DPadLeft,
                "DPadRight" => map.DPadRight,
                "FaceY" => map.FaceY,
                "FaceA" => map.FaceA,
                "FaceX" => map.FaceX,
                "FaceB" => map.FaceB,
                "LeftStickUp" => map.LeftStickUp,
                "LeftStickDown" => map.LeftStickDown,
                "LeftStickLeft" => map.LeftStickLeft,
                "LeftStickRight" => map.LeftStickRight,
                "LeftStickPress" => map.LeftStickPress,
                "RightStickUp" => map.RightStickUp,
                "RightStickDown" => map.RightStickDown,
                "RightStickLeft" => map.RightStickLeft,
                "RightStickRight" => map.RightStickRight,
                "RightStickPress" => map.RightStickPress,
                _ => ""
            };
        }
    }
}