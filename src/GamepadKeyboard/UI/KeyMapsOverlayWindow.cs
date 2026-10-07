using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Runtime.InteropServices;
using GamepadKeyboard.Native;
using GamepadKeyboard.Keyboard;
using GamepadKeyboard.Overlay;
using GamepadKeyboard.Settings;

namespace GamepadKeyboard.UI
{
    /// <summary>
    /// Key Maps mode overlay: a gamepad-shaped board of key tiles. Four clusters
    /// in a 2×2 arrangement (left stick under d-pad, right stick under face
    /// buttons) with Select/Start between them. While the maps key (R2) is held
    /// every slot shows compact shadow pills for the other maps at FIXED
    /// per-slot offsets — Symbols 3 up/left lane, Symbols 2 right/below lane,
    /// Function Keys below lane — placed into the free corners of each
    /// cluster's plus-shape so nothing ever overlaps. When a combo map becomes
    /// active its pills turn opaque in place while the previous map's tiles
    /// fade to shadows — positions never move. Labels wrap and split on camel
    /// case ("PageDown" → "Page Down"); modifier chips highlight while their
    /// key is held (including latches locked by the maps key).
    /// </summary>
    public sealed class KeyMapsOverlayWindow : Window
    {
        // ── Window / board constants ────────────────────────────────────────────

        private const double BoardWidth = 1000.0;
        private const double BoardHeight = 640.0;
        private const double TileWidth = 70.0;
        private const double TileHeight = 42.0;
        private const double TileGap = 16.0;
        private const double TilePitchX = TileWidth + TileGap;    // 86
        private const double TilePitchY = TileHeight + TileGap;   // 58
        private const double ChipWidth = 64.0;
        private const double ChipHeight = 26.0;
        private const double ChipGap = 10.0;
        private const double LabelFontSize = 11.5;
        private const double CenterColumnOffset = 14.0;
        private const double IdleOpacity = 0.42;
private const double AtomSpreadPitchX = 104.0;   // px between atom columns at Spread = 1
private const double AtomSpreadPitchY = 120.0;   // px between atom rows at Spread = 1
        private const double ActiveOpacity = 1.0;

        private static readonly string[] ModifierNames = { "Ctrl", "Shift", "Alt", "Win" };

        // ── Durable UI state ────────────────────────────────────────────────────

        private readonly Canvas _root;
        private readonly TextBlock _mapNameLabel;
        private readonly List<ChipView> _chips = new();
        private readonly Dictionary<string, TileView> _tiles = new();
        private readonly Dictionary<string, KeyMapsAtom> _atoms = new();   // slot → atom (18 slots)

        // ── Projected-keyboard view state ───────────────────────────────────────

        private readonly HashSet<KeyboardLayout.KeyDef> _projectedCovered = new();
        private readonly Dictionary<KeyboardLayout.KeyDef, Border> _projectedKeys = new();
        private readonly HashSet<Border> _projectedExtraBorders = new();
        private readonly Dictionary<KeyboardLayout.KeyDef, bool> _projectedPressed = new();
        private readonly List<UIElement> _projectedPrompts = new();
        private readonly List<Canvas> _projectedModifierBadges = new();
        private readonly List<Canvas> _projectedExtraModifierBadges = new();
        private readonly Dictionary<string, (KeyboardLayout.KeyDef Key, string Label)> _promptTargets = new();
        private KeyboardLayout? _projectedLayout;
        private double _lastProjectedFingerprint = double.NaN;
        private bool _lastProjectedShift;

        private bool _lastProjectedCapsLock;

        private List<KeyMapDefinition> _maps = new();
        private int _lastRenderedMapIndex;
        private bool _bindingsDirty = true;
        private bool _shown;
        private double _lastLayoutFingerprint = double.NaN;

        /// <summary>Mirrors the keyboard window's "always show at cursor
        /// position" behavior (set by AppOrchestrator from AppSettings).</summary>

        public KeyMapsOverlayWindow()
        {
            Title = "Key Maps";
            Width = BoardWidth;
            Height = BoardHeight;
            WindowStyle = WindowStyle.None;
            ResizeMode = ResizeMode.NoResize;
            ShowInTaskbar = false;
            ShowActivated = false;
            AllowsTransparency = true;
            Background = Brushes.Transparent;
            Topmost = true;
            Visibility = Visibility.Hidden;
            SourceInitialized += (_, _) => ApplyClickThroughExStyles();

            _mapNameLabel = new TextBlock
            {
                Foreground = Brushes.White,
                FontSize = 17,
                FontWeight = FontWeights.SemiBold,
            };
            _root = new Canvas();
            Content = _root;
        }

        // ── Public API (called by AppOrchestrator, poll-thread safe) ────────────

        /// <summary>Refreshes the board from the mapper's live state. Marshals
        /// to the UI thread when called from the poll thread.</summary>
        public void Update(ControllerMapper mapper)
        {
            if (!Dispatcher.CheckAccess())
            {
                _ = Dispatcher.BeginInvoke(() => Update(mapper));
                return;
            }

            bool wantVisible = mapper.InputEnabled
                && mapper.Mode == ControllerMapper.MapperMode.DirectInput
                && AppSettings.Instance.KeyMaps.ShowOverlay;
            if (!wantVisible)
            {
                if (_shown)
                {
                    _shown = false;
                    Visibility = Visibility.Hidden;
                }
                return;
            }

            if (!_shown)
            {
                _shown = true;
                _bindingsDirty = true;
                _lastRenderedMapIndex = -1;
                _lastLayoutFingerprint = double.NaN;
                if (AppSettings.Instance.AlwaysShowKeyboardAtCursorPosition)
                {
                    PositionAtCursor();
                }
                else
                {
                    RestorePersistedPosition();
                }
                Visibility = Visibility.Visible;
            }

            // The Key Maps overlay only runs while the Key Maps mapper exists;
            // the guard keeps the compiler happy without null-chasing. Hoisted
            // through a nullable local so the flow analysis is explicit.
            Input.KeyMapsMapper? keyMapsMaybe = mapper.KeyMaps;
            if (keyMapsMaybe is null)
            {
                throw new InvalidOperationException("Key Maps overlay opened without a Key Maps mapper");
            }
            Input.KeyMapsMapper keyMaps = keyMapsMaybe;
            double fingerprint = LayoutFingerprint();
            if (_bindingsDirty || !ReferenceEquals(_maps, AppSettings.Instance.KeyMaps.Maps)
                || fingerprint != _lastLayoutFingerprint)
            {
                _lastLayoutFingerprint = fingerprint;
                _maps = AppSettings.Instance.KeyMaps.Maps;
                RebuildAll(Math.Clamp(keyMaps.ActiveMapIndex, 0, Math.Max(_maps.Count - 1, 0)));
            }

            bool sym2ComboHeld = keyMaps.Sym2ComboHeld;
            bool sym3ComboHeld = keyMaps.Sym3ComboHeld;
            bool functionComboHeld = keyMaps.FunctionComboHeld;
            IReadOnlyList<KeyMapDefinition> maps = AppSettings.Instance.KeyMaps.Maps;
            IdleQuarkAlphaSetting.Value = AppSettings.Instance.KeyMaps.Layout.IdleQuarkAlpha;
            KeyMapsShadowMapsRuntime.Show = AppSettings.Instance.KeyMaps.Layout.ShowShadowMaps
                || ControllerMapper.HoldShadowMapsActive;
            bool shiftHeld = keyMaps.ShiftHeld;
            if (_atoms.Count > 0)
            {
                foreach (KeyValuePair<string, KeyMapsAtom> pair in _atoms)
                {
                    pair.Value.Update(maps, keyMaps.ActiveMapIndex, keyMaps.MapsKeyHeld,
                        sym2ComboHeld, sym3ComboHeld, functionComboHeld,
                        IsSlotPressed(mapper, pair.Key), shiftHeld);
                }

                // Shift toggling rewrites the Select/Start tile labels too.
                if (shiftHeld != _lastRenderedShiftHeld)
                {
                    _lastRenderedShiftHeld = shiftHeld;
                    foreach (KeyValuePair<string, TileView> tilePair in _tiles)
                    {
                        string label = LabelFor(_maps[Math.Clamp(keyMaps.ActiveMapIndex, 0, _maps.Count - 1)], tilePair.Value.Slot);
                        tilePair.Value.Label.Text = SplitLabel(KeyMapsShift.Label(label));
                    }
                }

                if (keyMaps.ActiveMapIndex != _lastRenderedMapIndex)
                {
                    int activeIndex = Math.Clamp(keyMaps.ActiveMapIndex, 0, _maps.Count - 1);
                    _lastRenderedMapIndex = activeIndex;
                    SwapActiveMap(activeIndex);
                }
                ApplyChipStates(keyMaps, true);
            }
            else if (_projectedLayout != null)
            {
                UpdateProjectedKeyboard(mapper, keyMaps, maps, shiftHeld);
            }
        }

        /// <summary>Drops every tile and shadow so the next Update rebuilds the
        /// board from current settings (called after settings edits).</summary>
        public void ResetView()
        {
            _bindingsDirty = true;
            _lastLayoutFingerprint = double.NaN;
        }

        /// <summary>Moves the board to the current cursor position (clamped to
        /// the cursor's monitor working area at that monitor's DPI) — same
        /// behavior as the keyboard window's at-cursor mode.</summary>
        public void PositionAtCursor()
        {
            System.Drawing.Point cursor = System.Windows.Forms.Cursor.Position;
            System.Drawing.Rectangle screen = System.Windows.Forms.Screen.FromPoint(cursor).WorkingArea;
            (double dpiX, double dpiY) = NativeMethods.EffectiveMonitorDpi(cursor.X, cursor.Y);
            double scaleX = 96.0 / dpiX;
            double scaleY = 96.0 / dpiY;
            double workLeft = screen.Left * scaleX;
            double workTop = screen.Top * scaleY;
            double workRight = screen.Right * scaleX;
            double workBottom = screen.Bottom * scaleY;
            double maxLeft = Math.Max(workLeft, workRight - BoardWidth);
            double maxTop = Math.Max(workTop, workBottom - BoardHeight);
            Left = Math.Clamp(cursor.X * scaleX, workLeft, maxLeft);
            Top = Math.Clamp(cursor.Y * scaleY, workTop, maxTop);
        }

        // ── Build ───────────────────────────────────────────────────────────────

        private void RebuildAll(int activeIndex)
        {
            _bindingsDirty = false;
            _root.Children.Clear();
            _chips.Clear();
            _tiles.Clear();
            _atoms.Clear();
            if (_maps.Count < 5)
            {
                return;
            }
            KeyMapsLayoutSettings layout = AppSettings.Instance.KeyMaps.Layout;
            if (layout.KeySize != _windowKeySize)
            {
                _windowKeySize = layout.KeySize;
                Width = BoardWidth * layout.KeySize;
                Height = BoardHeight * layout.KeySize;
            }
            double boardScale = Width / BoardWidth;
            if (layout.ProjectKeyboard)
            {
                BuildProjectedKeyboard(activeIndex, boardScale);
            }
            else
            {
                BuildModifierRow(boardScale);
                BuildCenterColumn(activeIndex, boardScale);
                BuildAtoms(boardScale);
            }
            ApplyMapName(activeIndex);
            _lastRenderedMapIndex = activeIndex;
            RefreshComboChipLabels();
        }

        /// <summary>Builds one atom per Key Maps slot from the wheel geometry
        /// tables (d-pad plus, face plus, both sticks with the press atom
        /// between left and right); Select/Start remain plain tiles. Positions
        /// come from per-atom X/Y settings; per-atom quark distance/icon
        /// offset/sizes from the global sliders.</summary>
        private void BuildAtoms(double boardScale)
        {
            KeyMapsLayoutSettings layout = AppSettings.Instance.KeyMaps.Layout;
            double centerX = BoardWidth / 2.0;
            double centerY = BoardHeight / 2.0 + 20.0;

            // Direction offsets in local circle space: this atom's position from
            // the circle's center at Spread = 1 (X factors are ±1; Y factors set
            // the exact default row layout per circle — the stick press atom
            // shares the middle row with left/right, per the 5-atom cross).
            (string slot, double circleAnchorX, double circleAnchorY, double dirX, double dirY, string circleKey)[] atoms =
            {
                ("DPadUp",           -1, -1,  0.0, -1.0, "DPad"),
                ("DPadLeft",         -1, -1, -1.0,  0.0, "DPad"),
                ("DPadRight",        -1, -1, +1.0,  0.0, "DPad"),
                ("DPadDown",         -1, -1,  0.0, +1.0, "DPad"),
                ("FaceY",            +1, -1,  0.0, -1.0, "Face"),
                ("FaceX",            +1, -1, -1.0,  0.0, "Face"),
                ("FaceB",            +1, -1, +1.0,  0.0, "Face"),
                ("FaceA",            +1, -1,  0.0, +1.0, "Face"),
                ("LeftStickUp",      -1, +1,  0.0, -1.25, "LeftStick"),
                ("LeftStickLeft",    -1, +1, -1.0, -0.25, "LeftStick"),
                ("LeftStickRight",   -1, +1, +1.0, -0.25, "LeftStick"),
                ("LeftStickPress",   -1, +1,  0.0, -0.25, "LeftStick"),
                ("LeftStickDown",    -1, +1,  0.0, +1.25, "LeftStick"),
                ("RightStickUp",     +1, +1,  0.0, -1.25, "RightStick"),
                ("RightStickLeft",   +1, +1, -1.0, -0.25, "RightStick"),
                ("RightStickRight",  +1, +1, +1.0, -0.25, "RightStick"),
                ("RightStickPress",  +1, +1,  0.0, -0.25, "RightStick"),
                ("RightStickDown",   +1, +1,  0.0, +1.25, "RightStick"),
            };

            foreach ((string slot, double circleAnchorX, double circleAnchorY, double dirX, double dirY, string circleKey) in atoms)
            {
                KeyMapsAtom atom = new(slot, LabelFor);
                atom.LayoutChildren(
                    layout.AtomSize, layout.QuarkSize,
                    layout.QuarkDistanceX, layout.QuarkDistanceY,
                    layout.IconOffsetX, layout.IconOffsetY, layout.IconScale, layout.FontScale);
                double circleCenterX = centerX + circleAnchorX * 190.0 + CircleOffsetX(layout, circleKey) * boardScale;
                double circleCenterY = centerY + circleAnchorY * 110.0 + CircleOffsetY(layout, circleKey) * boardScale;
                double atomX = dirX * AtomSpreadPitchX * CircleSpreadX(layout, circleKey);
                double atomY = dirY * AtomSpreadPitchY * CircleSpreadY(layout, circleKey);
                if (!layout.StickUniformSpread && circleKey.EndsWith("Stick", StringComparison.Ordinal))
                {
                    bool isLeft = circleKey == "LeftStick";
                    if (slot.EndsWith("Press", StringComparison.Ordinal))
                    {
                        atomY = (isLeft ? layout.LeftStickCenterOffsetY : layout.RightStickCenterOffsetY);
                    }
                    else if (slot.EndsWith("Down", StringComparison.Ordinal))
                    {
                        atomY = (isLeft ? layout.LeftStickBottomOffsetY : layout.RightStickBottomOffsetY);
                    }
                }
                Canvas atomHost = (Canvas)atom.Root;
                Canvas.SetLeft(atomHost, circleCenterX + atomX * boardScale);
                Canvas.SetTop(atomHost, circleCenterY + atomY * boardScale);
                Canvas.SetZIndex(atomHost, 12);
                _root.Children.Add(atomHost);
                _atoms[slot] = atom;
            }
        }

        private static double CircleOffsetX(KeyMapsLayoutSettings layout, string circleKey)
        {
            return circleKey switch
            {
                "DPad" => layout.DPadOffsetX,
                "Face" => layout.FaceOffsetX,
                "LeftStick" => layout.LeftStickOffsetX,
                _ => layout.RightStickOffsetX,
            };
        }

        private static double CircleOffsetY(KeyMapsLayoutSettings layout, string circleKey)
        {
            return circleKey switch
            {
                "DPad" => layout.DPadOffsetY,
                "Face" => layout.FaceOffsetY,
                "LeftStick" => layout.LeftStickOffsetY,
                _ => layout.RightStickOffsetY,
            };
        }

        private static double CircleSpreadX(KeyMapsLayoutSettings layout, string circleKey)
        {
            return circleKey switch
            {
                "DPad" => layout.DPadSpreadX,
                "Face" => layout.FaceSpreadX,
                "LeftStick" => layout.LeftStickSpreadX,
                _ => layout.RightStickSpreadX,
            };
        }

        private static double CircleSpreadY(KeyMapsLayoutSettings layout, string circleKey)
        {
            return circleKey switch
            {
                "DPad" => layout.DPadSpreadY,
                "Face" => layout.FaceSpreadY,
                "LeftStick" => layout.LeftStickSpreadY,
                _ => layout.RightStickSpreadY,
            };
        }

        /// <summary>Fingerprint of every layout-relevant setting; a change
        /// forces a full rebuild so live edits show immediately.</summary>
        private static double LayoutFingerprint()
        {
            KeyMapsLayoutSettings layout = AppSettings.Instance.KeyMaps.Layout;
            double fingerprint = layout.DPadOffsetX + layout.DPadOffsetY * 1.001 +
                   layout.FaceOffsetX * 1.002 + layout.FaceOffsetY * 1.003 +
                   layout.LeftStickOffsetX * 1.004 + layout.LeftStickOffsetY * 1.005 +
                   layout.RightStickOffsetX * 1.006 + layout.RightStickOffsetY * 1.007 +
                   layout.DPadSpreadX * 2.0 + layout.DPadSpreadY * 2.01 +
                   layout.FaceSpreadX * 2.02 + layout.FaceSpreadY * 2.03 +
                   layout.LeftStickSpreadX * 2.04 + layout.LeftStickSpreadY * 2.05 +
                   layout.RightStickSpreadX * 2.06 + layout.RightStickSpreadY * 2.07 +
                   layout.KeySize * 3.0 +
                   layout.AtomSize * 4.0 + layout.QuarkSize * 4.1 +
                   layout.QuarkDistanceX * 4.2 + layout.QuarkDistanceY * 4.21 +
                   layout.IconOffsetX * 4.3 + layout.IconOffsetY * 4.4 +
                   layout.IconScale * 4.5 + layout.FontScale * 4.6 +
                   (layout.ProjectKeyboard ? 9.0 : 0.0) +
                   layout.SelectStartOffsetX * 4.7 + layout.SelectStartOffsetY * 4.8 +
                   layout.SelectStartScale * 4.9 + layout.SelectStartSpreadX * 5.0 +
                   (layout.ShowShadowMaps ? 1.0 : 0.0) * 5.08 +
                   (layout.ShowMapNameLabel ? 1.0 : 0.0) * 5.09 +
                   (layout.StickUniformSpread ? 1.0 : 0.0) * 5.1 +
                   layout.LeftStickCenterOffsetY * 5.2 + layout.LeftStickBottomOffsetY * 5.3 +
                   layout.RightStickCenterOffsetY * 5.4 + layout.RightStickBottomOffsetY * 5.5 +
                   AppSettings.Instance.KeySpacing * 47.0;
            return fingerprint;
        }

        private void BuildModifierRow(double boardScale)
        {
            KeyMapsLayoutSettings layout = AppSettings.Instance.KeyMaps.Layout;
            for (int index = 0; index < ModifierNames.Length; index++)
            {
                Border chipBorder = MakeChip(ModifierNames[index], boardScale);
                Canvas.SetLeft(chipBorder, ChipColumnX(index, boardScale));
                Canvas.SetTop(chipBorder, 14 * boardScale);
                _root.Children.Add(chipBorder);
                _chips.Add(new ChipView { Border = chipBorder, ModIndex = index });
                Canvas? badgeIcon = AttachChipPadBadge(chipBorder, ModifierChipVks(index), boardScale);
                if (badgeIcon != null)
                {
                    double iconSpan = ChipPadBadgeIconSpan(boardScale);
                    Canvas.SetLeft(badgeIcon, ChipColumnX(index, boardScale)
                        + ChipWidth * boardScale / 2.0 - iconSpan / 2.0
                        + layout.PromptOffsetX * boardScale);
                    Canvas.SetTop(badgeIcon, 14 * boardScale - iconSpan / 2.0
                        + layout.PromptOffsetY * boardScale);
                    _root.Children.Add(badgeIcon);
                }
            }
            _mapNameLabel.Width = BoardWidth;
            _mapNameLabel.TextAlignment = TextAlignment.Center;
            _mapNameLabel.FontSize = 17 * boardScale * layout.FontScale;
            Canvas.SetLeft(_mapNameLabel, 0);
            Canvas.SetTop(_mapNameLabel, (14 + ChipHeight + 6) * boardScale);
            _mapNameLabel.Visibility = layout.ShowMapNameLabel
                ? Visibility.Visible : Visibility.Collapsed;
            if (!_root.Children.Contains(_mapNameLabel))
            {
                _root.Children.Add(_mapNameLabel);
            }
            BuildComboChips(boardScale);
        }

        /// <summary>Combo markers on the board edges, per spec: [L1] beside the
        /// left clusters, [R1] beside the right ones, [L1 + R1] below them —
        /// visible (semi-transparent) only while the maps key is held, turning
        /// opaque/green when their combo is actually held.</summary>
        private void BuildComboChips(double boardScale)
        {
            // Vertical anchor: middle of the upper cluster rows (default layout).
            double upperRowY = (BoardHeight / 2.0 + 20.0) - (3 * TilePitchY - TileGap) / 2.0 - 132.0 / 2.0 + (TilePitchY - TileGap) / 2.0;
            double chipY = upperRowY - ChipHeight * boardScale / 2.0;
            double bottomY = BoardHeight - 44.0;
            _comboChips[0] = MakeComboChip(ComboChipLabel(0, "L1"), 12, chipY, boardScale, 0);
            _comboChips[1] = MakeComboChip(ComboChipLabel(1, "R1"), BoardWidth - 12 - 46 * boardScale, chipY, boardScale, 1);
            _comboChips[2] = MakeComboChip(ComboChipLabel(2, "L1 + R1"), (BoardWidth - 74 * boardScale) / 2.0, bottomY, boardScale, 2);
            foreach (Border? chip in _comboChips)
            {
                if (chip != null)
                {
                    chip.Opacity = 0.0;   // hidden until the maps key is held
                }
            }
        }

        private static double ChipColumnX(int index, double boardScale)
        {
            double totalWidth = ModifierNames.Length * ChipWidth * boardScale + (ModifierNames.Length - 1) * ChipGap * boardScale;
            return (BoardWidth - totalWidth) / 2.0 + index * (ChipWidth + ChipGap) * boardScale;
        }

        private void BuildCenterColumn(int activeIndex, double boardScale)
        {
            KeyMapsLayoutSettings layout = AppSettings.Instance.KeyMaps.Layout;
            double centerX = BoardWidth / 2.0 + layout.SelectStartOffsetX * boardScale;
            double centerY = BoardHeight / 2.0 + 20.0 + layout.SelectStartOffsetY * boardScale;
            double tileScale = layout.SelectStartScale * boardScale;
            double width = TileWidth * tileScale;
            double height = TileHeight * tileScale;
            double spread = (CenterColumnOffset * 2.0 + TileWidth) * layout.SelectStartSpreadX * boardScale;
            AddTileSized("Select", _maps[activeIndex], centerX - width / 2.0 - spread / 2.0,
                centerY - height / 2.0, width, height, boardScale);
            AddTileSized("Start", _maps[activeIndex], centerX - width / 2.0 + spread / 2.0,
                centerY - height / 2.0, width, height, boardScale);
        }

        /// <summary>AddTile with an explicit tile size (Select/Start sliders).</summary>
        private void AddTileSized(
            string slot, KeyMapDefinition map, double left, double top, double width, double height, double boardScale)
        {
            string label = LabelFor(map, slot);
            TextBlock labelBlock = MakeLabel(
                SplitLabel(label), LabelFontSize * boardScale * AppSettings.Instance.KeyMaps.Layout.FontScale);
            Border border = MakeTileBorder(width, height);
            border.Child = labelBlock;
            Canvas.SetLeft(border, left - width / 2.0);
            Canvas.SetTop(border, top);
            Canvas.SetZIndex(border, 10);
            _root.Children.Add(border);
            _tiles[slot] = new TileView
            {
                Border = border,
                Label = labelBlock,
                Slot = slot,
                Left = left - width / 2.0,
                Top = top,
                Width = width,
            };
        }

        private void AddTile(string slot, KeyMapDefinition map, double left, double top, double boardScale)
        {
            string label = LabelFor(map, slot);
            TextBlock labelBlock = MakeLabel(
                SplitLabel(label), LabelFontSize * boardScale * AppSettings.Instance.KeyMaps.Layout.FontScale);
            Border border = MakeTileBorder(TileWidth * boardScale, TileHeight * boardScale);
            border.Child = labelBlock;
            Canvas.SetLeft(border, left);
            Canvas.SetTop(border, top);
            Canvas.SetZIndex(border, 10);
            _root.Children.Add(border);
            _tiles[slot] = new TileView
            {
                Border = border,
                Label = labelBlock,
                Slot = slot,
                Left = left,
                Top = top,
                Width = TileWidth * boardScale,
            };
        }

        private void SwapActiveMap(int activeIndex)
        {
            ApplyMapName(activeIndex);
            foreach (KeyValuePair<string, TileView> pair in _tiles)
            {
                TileView tile = pair.Value;
                string label = LabelFor(_maps[activeIndex], tile.Slot);
                tile.Label.Text = SplitLabel(label);
                bool boundHere = !string.IsNullOrWhiteSpace(label);
                tile.Border.Opacity = boundHere ? ActiveOpacity : IdleOpacity;
                Canvas.SetZIndex(tile.Border, boundHere ? 10 : 5);
            }
        }

        // ── Modifier chips ──────────────────────────────────────────────────────

        /// <summary>Combo chips: [L1] [R1] [L1+R1] on the board edges — built
        /// once per rebuild, opacity-driven per tick.</summary>
        private readonly Border?[] _comboChips = new Border?[3];
        private readonly TextBlock?[] _comboChipTexts = new TextBlock?[3];
        private bool _lastRenderedShiftHeld;

        private Border MakeComboChip(string label, double x, double y, double boardScale, int chipIndex)
        {
            TextBlock chipText = new()
            {
                Text = label,
                Foreground = Brushes.White,
                FontSize = 12 * boardScale * AppSettings.Instance.KeyMaps.Layout.FontScale,
                FontWeight = FontWeights.Medium,
                TextAlignment = TextAlignment.Center,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
            };
            Border chip = new()
            {
                Width = label.Length > 2 ? 74 * boardScale : 46 * boardScale,
                Height = ChipHeight * boardScale,
                CornerRadius = new CornerRadius(8),
                BorderThickness = new Thickness(1),
                BorderBrush = new SolidColorBrush(Color.FromArgb(0x55, 0xFF, 0xFF, 0xFF)),
                Background = new SolidColorBrush(Color.FromArgb(0x30, 0x20, 0x20, 0x20)),
                Opacity = 0.55,
                Child = chipText,
            };
            _comboChipTexts[chipIndex] = chipText;
            Canvas.SetLeft(chip, x);
            Canvas.SetTop(chip, y);
            _root.Children.Add(chip);
            return chip;
        }

        /// <summary>Combo chip labels for the CURRENT configuration: the
        /// shape of the map's OpenWith combination (map 2 → right chip,
        /// map 3 → left chip, map 4+ → bottom chip). Falls back to the
        /// historical defaults when combinations are unconfigured.</summary>
        private static string ComboChipLabel(int chipIndex, string fallback)
        {
            System.Collections.Generic.List<KeyMapDefinition> maps = AppSettings.Instance.KeyMaps.Maps;
            int mapIndex = chipIndex switch { 0 => 3, 1 => 2, _ => 4 };
            if (mapIndex < maps.Count)
            {
                string combo = maps[mapIndex].OpenWith ?? string.Empty;
                if (combo.Length > 0)
                {
                    return combo.Replace("+", " + ");
                }
            }
            return fallback;
        }

        /// <summary>Live-apply new chip labels after the OpenWith editor
        /// changed combinations (called on rebuild; cheap text writes).</summary>
        private void RefreshComboChipLabels()
        {
            string[] fallbacks = { "L1", "R1", "L1 + R1" };
            for (int index = 0; index < _comboChipTexts.Length; index++)
            {
                TextBlock? text = _comboChipTexts[index];
                Border? chip = _comboChips[index];
                if (text == null || chip == null)
                {
                    continue;
                }
                string label = ComboChipLabel(index, fallbacks[index]);
                double boardScale = chip.Height / ChipHeight;
                text.Text = label;
                chip.Width = (label.Length > 2 ? 74 : 46) * boardScale;
            }
        }

        private void ApplyComboChipStates(Input.KeyMapsMapper keyMaps)
        {
            bool mapsKeyHeld = keyMaps.MapsKeyHeld;
            // Chip order matches: [0] L1 chip (left) → Symbols 3 combo,
            // [1] R1 chip (right) → Symbols 2 combo, [2] L1+R1 → Function Keys.
            bool[] comboActive = 
            {
                keyMaps.Sym3ComboHeld,
                keyMaps.Sym2ComboHeld,
                keyMaps.FunctionComboHeld,
            };
            for (int index = 0; index < _comboChips.Length; index++)
            {
                Border? chip = _comboChips[index];
                if (chip == null)
                {
                    continue;
                }
                bool active = comboActive[index];
                // Visible (semi-transparent) while the maps key is held —
                // hidden entirely otherwise; brightens + greens while the
                // combo itself is held.
                chip.Opacity = active ? 1.0 : (mapsKeyHeld ? 0.55 : 0.0);
                chip.Background = active
                    ? new SolidColorBrush(Color.FromArgb(0xE6, 0x2E, 0x8B, 0x57))
                    : new SolidColorBrush(Color.FromArgb(0x30, 0x20, 0x20, 0x20));
                chip.BorderBrush = active
                    ? new SolidColorBrush(Color.FromRgb(0x7C, 0xFC, 0x9A))
                    : new SolidColorBrush(Color.FromArgb(0x55, 0xFF, 0xFF, 0xFF));
            }
        }

        // ── Pad-button badges on the modifier chips ───────────────────────────
        // Same graphics as the keyboard overlay's key badges: which gamepad
        // control drives [Ctrl]/[Shift]/[Alt]/[Win] in the current config
        // (resolved from the system-button actions + Utility RS-press).

        private const string badgeNone = "";

        /// <summary>Modifier chip index → (primary, alternate) VKs.</summary>
        private static (ushort Primary, ushort Alternate) ModifierChipVks(int index) => index switch
        {
            0 => (Vk.LControl, Vk.RControl),
            1 => (Vk.LShift, Vk.RShift),
            2 => (Vk.LMenu, Vk.RMenu),
            3 => (Vk.LWin, Vk.RWin),
            _ => (Vk.None, Vk.None),
        };

        private Canvas? AttachChipPadBadge(Border chip, (ushort Primary, ushort Alternate) vks, double boardScale)
        {
            string driver = Input.KeyMapsMapper.ModifierDriverButton(vks.Primary, vks.Alternate);
            if (string.IsNullOrEmpty(driver))
            {
                return null;
            }

            // Same prompt-icon system as the projected-key button hints: scaled by
            // "Buttons icons scale" (IconScale), nudged by "Prompt offset". The icon
            // is returned so the caller positions it on the board canvas (Canvas
            // attached properties do not position children inside a Grid).
            KeyMapsLayoutSettings layout = AppSettings.Instance.KeyMaps.Layout;
            double promptScale = PromptIconScale * Math.Max(0.05, layout.IconScale) * boardScale;
            double iconSpan = KeyMapsAtom.IconSpan * promptScale;
            Canvas icon = KeyMapsAtom.MakeIcon(driver);
            icon.RenderTransform = new ScaleTransform(promptScale, promptScale);
            icon.IsHitTestVisible = false;
            return icon;
        }

        private double ChipPadBadgeIconSpan(double boardScale)
        {
            KeyMapsLayoutSettings layout = AppSettings.Instance.KeyMaps.Layout;
            return KeyMapsAtom.IconSpan * PromptIconScale * Math.Max(0.05, layout.IconScale) * boardScale;
        }

        private void ApplyChipStates(Input.KeyMapsMapper keyMaps, bool includeComboChips)
        {
            ApplyChipVisual(_chips[0].Border, keyMaps.CtrlHeld);
            ApplyChipVisual(_chips[1].Border, keyMaps.ShiftHeld);
            ApplyChipVisual(_chips[2].Border, keyMaps.AltHeld);
            ApplyChipVisual(_chips[3].Border, keyMaps.WindowsHeld);
            if (includeComboChips)
            {
                ApplyComboChipStates(keyMaps);
            }
        }

        private static void ApplyChipVisual(Border border, bool held)
        {
            border.Background = held
                ? new SolidColorBrush(Color.FromArgb(0xE6, 0x2E, 0x8B, 0x57))
                : new SolidColorBrush(Color.FromArgb(0x66, 0x20, 0x20, 0x20));
            border.BorderBrush = held
                ? new SolidColorBrush(Color.FromRgb(0x7C, 0xFC, 0x9A))
                : new SolidColorBrush(Color.FromArgb(0x55, 0xFF, 0xFF, 0xFF));
        }

        private void ApplyMapName(int mapIndex)
        {
            _mapNameLabel.Text = _maps[mapIndex].Name;
        }

        /// <summary>Whether the atom's physical gamepad control is currently
        /// down (drives the "pressing the button highlights" rule).</summary>
        private static bool IsSlotPressed(ControllerMapper mapper, string slot)
        {
            // While the move/scale adjust mode owns the pad, slot keys cannot
            // activate — the press highlight must not imply otherwise.
            if (ControllerMapper.KeyMapsMoveModeActive
                && !string.Equals(slot, "ToggleMoveScale", StringComparison.Ordinal))
            {
                return false;
            }
            Input.GamepadSnapshot snapshot = mapper.LatestSnapshot;
            double threshold = Math.Clamp(AppSettings.Instance.KeyMaps.StickTapThreshold, 0.05, 1.0);
            return slot switch
            {
                "DPadUp" => snapshot.DUp,
                "DPadDown" => snapshot.DDown,
                "DPadLeft" => snapshot.DLeft,
                "DPadRight" => snapshot.DRight,
                "FaceY" => snapshot.Y,
                "FaceA" => snapshot.A,
                "FaceX" => snapshot.X,
                "FaceB" => snapshot.B,
                "LeftStickUp" => snapshot.LY >= threshold,
                "LeftStickDown" => snapshot.LY <= -threshold,
                "LeftStickLeft" => snapshot.LX <= -threshold,
                "LeftStickRight" => snapshot.LX >= threshold,
                "LeftStickPress" => snapshot.LS,
                "RightStickUp" => snapshot.RY >= threshold,
                "RightStickDown" => snapshot.RY <= -threshold,
                "RightStickLeft" => snapshot.RX <= -threshold,
                "RightStickRight" => snapshot.RX >= threshold,
                "RightStickPress" => snapshot.RS,
                _ => false,
            };
        }

        // ── Factories ───────────────────────────────────────────────────────────

        private static Border MakeChip(string label, double boardScale)
        {
            return new Border
            {
                Width = ChipWidth * boardScale,
                Height = ChipHeight * boardScale,
                CornerRadius = new CornerRadius(8),
                BorderThickness = new Thickness(1),
                Background = new SolidColorBrush(Color.FromArgb(0x66, 0x20, 0x20, 0x20)),
                BorderBrush = new SolidColorBrush(Color.FromArgb(0x55, 0xFF, 0xFF, 0xFF)),
                Child = new TextBlock
                {
                    Text = label,
                    Foreground = Brushes.White,
                    FontSize = 13 * boardScale * AppSettings.Instance.KeyMaps.Layout.FontScale,
                    FontWeight = FontWeights.Medium,
                    TextAlignment = TextAlignment.Center,
                    HorizontalAlignment = HorizontalAlignment.Center,
                    VerticalAlignment = VerticalAlignment.Center,
                },
            };
        }

        private static Border MakeTileBorder(double width, double height)
        {
            return new Border
            {
                Width = width,
                Height = height,
                CornerRadius = new CornerRadius(8),
                BorderThickness = new Thickness(1),
                BorderBrush = new SolidColorBrush(Color.FromArgb(0x66, 0xFF, 0xFF, 0xFF)),
                Background = new SolidColorBrush(Color.FromArgb(0xC8, 0x18, 0x18, 0x18)),
            };
        }

        private static TextBlock MakeLabel(string labelText, double fontSize)
        {
            return new TextBlock
            {
                Text = labelText,
                Foreground = Brushes.White,
                FontSize = fontSize,
                TextWrapping = TextWrapping.Wrap,
                TextAlignment = TextAlignment.Center,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
            };
        }

        // ── Label shaping ───────────────────────────────────────────────────────

        /// <summary>"PageDown" → "Page Down": space at every lower→upper
        /// boundary so word wrapping can break lines naturally. Known
        /// abbreviations humanized first.</summary>
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

        // ── Slot metadata ───────────────────────────────────────────────────────

        private static string? SlotValue(KeyMapDefinition map, string slot)
        {
            return slot switch
            {
                "Select" => map.Select,
                "Start" => map.Start,
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
                _ => null,
            };
        }

        private static string LabelFor(KeyMapDefinition map, string slot)
        {
            string? value = SlotValue(map, slot);
            return string.IsNullOrWhiteSpace(value) || value == "None" ? "" : value;
        }



        // ── Projected-keyboard view ──────────────────────────────────────────────

        /// <summary>Keys that exist only off the standard US layout (nav
        /// cluster, media, volume, PrintScreen) get a fixed vertical column
        /// left of the keyboard so their prompts have an anchor.</summary>
        private static readonly (string Label, ushort Vk)[] ExtraKeyCatalog =
        {
            ("PrtSc", Vk.Print),
            ("ScrLk", Vk.Scroll),
            ("Pause", Vk.Pause),
            ("Ins", Vk.Insert),
            ("Del", Vk.Delete),
            ("Home", Vk.Home),
            ("End", Vk.End),
            ("PgUp", Vk.PageUp),
            ("PgDn", Vk.PageDown),
            ("▼", Vk.Down),
            ("▲", Vk.Up),
            ("◀", Vk.Left),
            ("▶", Vk.Right),
            ("Vol+", Vk.VolumeUp),
            ("Vol−", Vk.VolumeDown),
            ("Mut", Vk.VolumeMute),
            ("▷∥", Vk.MediaPlayPause),
            ("▷▷", Vk.MediaNext),
            ("◁◁", Vk.MediaPrev),
            ("■", Vk.MediaStop),
        };

        /// <summary>Prompt-icon scale shared with the classic keyboard overlay's
        /// pad-button badges (both follow the "Buttons icons scale" system).</summary>
        internal const double PromptIconScale = 1.55;
        private const double ProjectedPromptIdleOpacity = 0.8;

        private static readonly string[] ProjectedSlots =
        {
            "DPadUp", "DPadDown", "DPadLeft", "DPadRight",
            "FaceY", "FaceA", "FaceX", "FaceB",
            "LeftStickUp", "LeftStickDown", "LeftStickLeft", "LeftStickRight", "LeftStickPress",
            "RightStickUp", "RightStickDown", "RightStickLeft", "RightStickRight", "RightStickPress",
            "Select", "Start",
        };

        /// <summary>Builds the projected view: a full US keyboard (same layout
        /// as Keyboard Mode), a left column for keys absent from the board, and
        /// one button prompt per active-map slot floating over the key the slot
        /// sends. No sticks, no rays, no center points.</summary>
        private void BuildProjectedKeyboard(int activeIndex, double boardScale)
        {
            BuildProjectedKeyboardBase(boardScale);
            BuildProjectedPrompts(activeIndex);
        }

        /// <summary>Keys + extra column + map name (no prompts). Runs on
        /// settings rebuilds only — never on map switches, so the keyboard
        /// itself never flashes.</summary>
        private void BuildProjectedKeyboardBase(double boardScale)
        {
            _projectedKeys.Clear();
            _projectedExtraBorders.Clear();
            _projectedPressed.Clear();
            _projectedCovered.Clear();
            foreach (UIElement prompt in _projectedPrompts)
            {
                _root.Children.Remove(prompt);
            }
            _projectedPrompts.Clear();
            foreach (UIElement badge in _projectedModifierBadges)
            {
                _root.Children.Remove(badge);
            }
            _projectedModifierBadges.Clear();
            _promptTargets.Clear();
            _lastProjectedShift = false;
            _lastPromptOpacity = double.NaN;

            if (_projectedLayout == null)
            {
                _projectedLayout = new KeyboardLayout();
                _projectedLayout.Build();
            }

            KeyMapsLayoutSettings layout = AppSettings.Instance.KeyMaps.Layout;
            double pitch = (46.0 + Math.Max(0.0, AppSettings.Instance.KeySpacing)) * boardScale;
            double keySpanX = _projectedLayout.GridW * pitch + pitch * 1.15;
            double keySpanY = _projectedLayout.GridH * pitch;
            double originX = (BoardWidth * boardScale - keySpanX) / 2.0 + pitch * 1.15;
            // Sit the keyboard just under the map-name label (label top 6*scale,
            // ~28*scale tall) — the old center-aligned Y left a ~180 px void
            // between label and first key row.
            double originY = 44.0 * boardScale;

            foreach (KeyboardLayout.KeyDef key in _projectedLayout.Keys)
            {
                AddProjectedKey(key, originX, originY, pitch, boardScale);
            }

            // Map-name label (same styled element as the atom board).
            if (!_root.Children.Contains(_mapNameLabel))
            {
                _mapNameLabel.Width = BoardWidth * boardScale;
                _mapNameLabel.TextAlignment = TextAlignment.Center;
                _mapNameLabel.FontSize = 16 * boardScale * AppSettings.Instance.KeyMaps.Layout.FontScale;
                Canvas.SetLeft(_mapNameLabel, 0);
                Canvas.SetTop(_mapNameLabel, 6 * boardScale);
                _root.Children.Add(_mapNameLabel);
            }
            _mapNameLabel.Visibility = AppSettings.Instance.KeyMaps.Layout.ShowMapNameLabel
                ? Visibility.Visible : Visibility.Collapsed;
        }

        /// <summary>(Re)creates ONLY the prompt icons + covered outlines for
        /// the current map set — runs on map switches / preview toggles so
        /// the keyboard keys themselves never flash.</summary>
        private void BuildProjectedPrompts(int activeIndex)
        {
            foreach (UIElement prompt in _projectedPrompts)
            {
                _root.Children.Remove(prompt);
            }
            _projectedPrompts.Clear();
            foreach (UIElement badge in _projectedExtraModifierBadges)
            {
                _root.Children.Remove(badge);
            }
            _projectedExtraModifierBadges.Clear();
            _promptTargets.Clear();
            _lastPromptOpacity = double.NaN;
            foreach (Border extraBorder in _projectedExtraBorders)
            {
                _root.Children.Remove(extraBorder);
            }
            _projectedExtraBorders.Clear();
            foreach (KeyValuePair<KeyboardLayout.KeyDef, Border> pair in _projectedKeys)
            {
                pair.Value.Background = projectedKeyFillIdle;
                pair.Value.BorderBrush = projectedKeyBorderIdle;
                pair.Value.BorderThickness = new Thickness(1);
                _projectedPressed[pair.Key] = false;
            }
            KeyMapsLayoutSettings layout = AppSettings.Instance.KeyMaps.Layout;
            double boardScale = Width / BoardWidth;
            double pitch = (46.0 + Math.Max(0.0, AppSettings.Instance.KeySpacing)) * boardScale;
            // Same geometry as BuildProjectedKeyboardBase — keys, prompts and
            // the extra grid must share one origin or they drift apart.
            double keySpanX = _projectedLayout!.GridW * pitch + pitch * 1.15;
            double originX = (BoardWidth * boardScale - keySpanX) / 2.0 + pitch * 1.15;
            double originY = 44.0 * boardScale;

            IReadOnlyList<KeyMapDefinition> maps = AppSettings.Instance.KeyMaps.Maps;

            // Extra column: entries used by the map we show — catalog order
            // keeps it stable across map switches.
            List<ushort> usedVks = new();
            for (int mapIndex = 0; mapIndex < maps.Count; mapIndex++)
            {
                if (mapIndex != Math.Clamp(activeIndex, 0, maps.Count - 1))
                {
                    continue;
                }
                foreach (string slot in ProjectedSlots)
                {
                    string? value = SlotValue(maps[mapIndex], slot);
                    if (string.IsNullOrWhiteSpace(value) || value == "None")
                    {
                        continue;
                    }
                    ushort vk = ResolveProjectedVk(value);
                    if (vk != Vk.None && _projectedLayout.FindByVk(vk) == null && !usedVks.Contains(vk))
                    {
                        usedVks.Add(vk);
                    }
                }
            }
            // Multi-column block to the RIGHT of the keyboard: 6 rows,
            // as many columns as the currently shown maps need. Catalog order
            // keeps positions stable across map switches.
            ushort[] orderedExtra = UsedCatalogVks(usedVks);
            const int ExtraRows = 6;
            double rowPitch = 1.15 * Math.Max(0.4, layout.ExtraKeySpacing);
            for (int extraIndex = 0; extraIndex < orderedExtra.Length; extraIndex++)
            {
                ushort vk = orderedExtra[extraIndex];
                string label = Array.Find(ExtraKeyCatalog, pair => pair.Vk == vk).Label;
                int column = extraIndex / ExtraRows;
                int row = extraIndex % ExtraRows;
                KeyboardLayout.KeyDef extra = new(label, vk)
                {
                    X = 15.55 + column * 1.05,
                    Y = 1.0 + row * rowPitch,
                    W = 0.9,
                };
                AddProjectedKey(extra, originX, originY, pitch, boardScale,
                    isExtraKey: true, badgeSink: _projectedExtraModifierBadges);
            }

            // Prompts: one vector gamepad-button icon per bound slot, over its
            // key. In preview mode all maps contribute; redundant bindings
            // (same slot+value or same slot+key) are dropped and several maps
            // hitting the SAME key stack vertically beside the prompt.
            HashSet<(string Slot, string Value)> seenBindings = new();
            Dictionary<KeyboardLayout.KeyDef, int> keyStackDepth = new();
            {
                int sourceIndex = Math.Clamp(activeIndex, 0, maps.Count - 1);
                KeyMapDefinition sourceMap = maps[sourceIndex];
                foreach (string slot in ProjectedSlots)
                {
                    string? value = SlotValue(sourceMap, slot);
                    if (string.IsNullOrWhiteSpace(value) || value == "None")
                    {
                        continue;
                    }
                    if (!seenBindings.Add((slot, value)))
                    {
                        continue;   // redundant cross-map binding
                    }
                    if (ControllerMapper.IsAppLevelAction(value))
                    {
                        continue;   // app commands (MouseMode…) have no key to project onto
                    }
                    ushort vk = ResolveProjectedVk(value);
                    if (vk == Vk.None)
                    {
                        continue;
                    }
                    KeyboardLayout.KeyDef? target = _projectedLayout.FindByVk(vk);
                    if (target == null)
                    {
                        foreach (KeyboardLayout.KeyDef key in _projectedKeys.Keys)
                        {
                            if (key.Vk == vk)
                            {
                                target = key;
                                break;
                            }
                        }
                    }
                    if (target == null)
                    {
                        continue;
                    }
                    bool isActiveMap = sourceIndex == Math.Clamp(activeIndex, 0, maps.Count - 1);
                    int stackIndex = keyStackDepth.TryGetValue(target, out int depth) ? depth : 0;
                    keyStackDepth[target] = stackIndex + 1;
                    double promptScale = PromptIconScale * Math.Max(0.05, layout.IconScale);
                    double iconSpan = KeyMapsAtom.IconSpan * promptScale * boardScale;
                    Canvas icon = KeyMapsAtom.MakeIcon(slot);
                    icon.Opacity = ProjectedPromptIdleOpacity;
                    icon.RenderTransform = new ScaleTransform(
                        promptScale * boardScale, promptScale * boardScale);
                    double px = originX + target.X * pitch + (target.W * pitch) / 2.0
                        - iconSpan / 2.0 + layout.PromptOffsetX * boardScale
                        + (isActiveMap ? 0.0 : PreviewStackOffsetY);
                    double py = originY + target.Y * pitch - iconSpan - 4.0 * boardScale
                        + layout.PromptOffsetY * boardScale
                        + stackIndex * PreviewStackOffsetY;
                    Canvas.SetLeft(icon, px);
                    Canvas.SetTop(icon, py);
                    Canvas.SetZIndex(icon, isActiveMap ? 21 : 19);
                    _root.Children.Add(icon);
                    _projectedPrompts.Add(icon);
                    _projectedCoveredPending.Add(target);
                    if (isActiveMap)
                    {
                        _promptTargets[slot] = (target, value);
                    }
                }
            }

            // Orange outline marks every key covered by the shown maps.
            _projectedCovered.Clear();
            foreach (KeyboardLayout.KeyDef key in _projectedCoveredPending)
            {
                _projectedCovered.Add(key);
            }
            _projectedCoveredPending.Clear();
            foreach (KeyValuePair<KeyboardLayout.KeyDef, Border> pair in _projectedKeys)
            {
                if (_projectedCovered.Contains(pair.Key))
                {
                    pair.Value.BorderBrush = projectedBorderCovered;
                    pair.Value.BorderThickness = new Thickness(1.8);
                }
            }

            _lastProjectedFingerprint = PromptFingerprint();
            _lastProjectedShift = false;
            ApplyProjectedKeyLabels(false);
        }

        private ushort[] UsedCatalogVks(List<ushort> used)
        {
            List<ushort> ordered = new();
            foreach ((string label, ushort vk) in ExtraKeyCatalog)
            {
                if (used.Contains(vk))
                {
                    ordered.Add(vk);
                }
            }
            return ordered.ToArray();
        }

        private static ushort ResolveProjectedVk(string value)
        {
            string probe = value;
            if (probe.StartsWith("Key:", StringComparison.Ordinal))
            {
                probe = probe.Substring(4);
            }
            ushort vk = ControllerMapper.NamedVk(probe);
            if (vk == Vk.None && probe.Length == 1)
            {
                vk = KeyboardLayout.KeyDef.CharVk(probe[0]);
            }
            return vk;
        }

        /// <summary>One keyboard-styled key (same look as Keyboard Mode) added
        /// to the board canvas at grid coords.</summary>
        private void AddProjectedKey(
            KeyboardLayout.KeyDef key, double originX, double originY, double pitch, double boardScale,
            bool isExtraKey = false,
            List<Canvas>? badgeSink = null)
        {
            KeyMapsLayoutSettings layout = AppSettings.Instance.KeyMaps.Layout;
            double gap = 6.0 * boardScale;
            Rect rect = new(key.X * pitch, key.Y * pitch, key.W * pitch - gap, pitch - gap);
            double keyOpacity = Math.Clamp(AppSettings.Instance.KeyboardKeyOpacity, 0.2, 1.0);
            Border border = new()
            {
                Width = rect.Width,
                Height = rect.Height,
                CornerRadius = new CornerRadius(3),
                BorderThickness = new Thickness(1),
                BorderBrush = projectedKeyBorderIdle,
                Background = projectedKeyFillIdle,
                Opacity = keyOpacity,
            };
            TextBlock label = new()
            {
                Text = Overlay.KeyboardOverlay.VisibleKeyLabel(key, _lastProjectedShift),
                Foreground = projectedKeyText,
                FontSize = 11.5 * boardScale,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
            };
            string badgeDriver = key.Vk is (ushort)0xA0 or (ushort)0xA1   // Shift
                ? Input.KeyMapsMapper.ModifierDriverButton(Vk.LShift, Vk.RShift)
                : key.Vk is (ushort)0xA2 or (ushort)0xA3   // Ctrl
                    ? Input.KeyMapsMapper.ModifierDriverButton(Vk.LControl, Vk.RControl)
                : key.Vk is (ushort)0xA4 or (ushort)0xA5   // Alt
                    ? Input.KeyMapsMapper.ModifierDriverButton(Vk.LMenu, Vk.RMenu)
                : key.Vk is (ushort)0x5B or (ushort)0x5C   // Windows
                    ? Input.KeyMapsMapper.ModifierDriverButton(Vk.LWin, Vk.RWin)
                : "";
            border.Child = label;
            Canvas.SetLeft(border, originX + rect.X);
            Canvas.SetTop(border, originY + rect.Y);
            _root.Children.Add(border);
            _projectedKeys[key] = border;
            if (!string.IsNullOrEmpty(badgeDriver))
            {
                // Same prompt-icon system as the button hints above keys — sibling
                // element on the board canvas so Prompt offset shifts it exactly
                // like the hints.
                double promptScale = PromptIconScale * Math.Max(0.05, layout.IconScale) * boardScale;
                double iconSpan = KeyMapsAtom.IconSpan * promptScale;
                Canvas icon = KeyMapsAtom.MakeIcon(badgeDriver);
                icon.RenderTransform = new ScaleTransform(promptScale, promptScale);
                Canvas.SetLeft(icon, originX + rect.X + rect.Width / 2.0 - iconSpan / 2.0
                    + layout.PromptOffsetX * boardScale);
                Canvas.SetTop(icon, originY + rect.Y - iconSpan / 2.0
                    + layout.PromptOffsetY * boardScale);
                icon.IsHitTestVisible = false;
                Canvas.SetZIndex(icon, 22);
                _root.Children.Add(icon);
                (badgeSink ?? _projectedModifierBadges).Add(icon);
            }
            _projectedPressed[key] = false;
            if (isExtraKey)
            {
                _projectedExtraBorders.Add(border);
            }
        }

        /// <summary>Per-tick projected update: press highlights (the pressed
        /// button's target key glows green like a cursor highlight), modifier
        /// hold styling (same rules as the atom view), Shift label swaps and
        /// rebuilds on map/offset changes.</summary>
        private void UpdateProjectedKeyboard(
            ControllerMapper mapper, Input.KeyMapsMapper keyMaps,
            IReadOnlyList<KeyMapDefinition> maps, bool shiftHeld)
        {
            int activeIndex = Math.Clamp(keyMaps.ActiveMapIndex, 0, maps.Count - 1);
            double fingerprint = PromptFingerprint();
            if (activeIndex != _lastRenderedMapIndex || fingerprint != _lastProjectedFingerprint)
            {
                _lastRenderedMapIndex = activeIndex;
                _lastProjectedFingerprint = fingerprint;
                RebuildProjectedPrompts(activeIndex);
                ApplyMapName(activeIndex);
                return;
            }
            UpdateProjectedPromptOpacity(keyMaps);
            UpdateProjectedPresses(mapper);
            UpdateProjectedModifiers(keyMaps);

            bool capsLockActive = Native.NativeMethods.CapsLockActive;
            if (shiftHeld != _lastProjectedShift || capsLockActive != _lastProjectedCapsLock)
            {
                _lastProjectedShift = shiftHeld;
                _lastProjectedCapsLock = capsLockActive;
                ApplyProjectedKeyLabels(shiftHeld, capsLockActive);
            }
        }

        /// <summary>Map switch / preview toggle: refresh prompts + outlines
        /// without touching the keyboard keys (no flash).</summary>
        private void RebuildProjectedPrompts(int activeIndex)
        {
            BuildProjectedPrompts(activeIndex);
        }

        /// <summary>Prompts are this view's core info — always visible; they
        /// brighten to full while the maps key is held. NOT gated by
        /// "Show shadow maps" (that toggle governs the atom view's quarks).</summary>
        private void UpdateProjectedPromptOpacity(Input.KeyMapsMapper keyMaps)
        {
            double promptOpacity = keyMaps.MapsKeyHeld ? ActiveOpacity : ProjectedPromptIdleOpacity;
            if (promptOpacity != _lastPromptOpacity)
            {
                _lastPromptOpacity = promptOpacity;
                foreach (UIElement prompt in _projectedPrompts)
                {
                    prompt.Opacity = promptOpacity;
                }
            }
        }

        private void UpdateProjectedPresses(ControllerMapper mapper)
        {
            foreach (KeyValuePair<string, (KeyboardLayout.KeyDef Key, string Label)> pair in _promptTargets)
            {
                bool pressed = IsSlotPressedProjected(mapper, pair.Key);
                if (_projectedPressed.TryGetValue(pair.Value.Key, out bool wasPressed) && wasPressed == pressed)
                {
                    continue;
                }
                _projectedPressed[pair.Value.Key] = pressed;
                if (_projectedKeys.TryGetValue(pair.Value.Key, out Border? border))
                {
                    border.Background = pressed ? projectedKeyFillPressed : projectedKeyFillIdle;
                    border.BorderBrush = pressed ? projectedBorderPressed : ProjectedBorderFor(pair.Value.Key);
                    border.BorderThickness = new Thickness(
                        pressed ? 2.5 : _projectedCovered.Contains(pair.Value.Key) ? 1.8 : 1);
                }
            }
        }

        /// <summary>Modifier keys: same highlight rules as the atom view's chips.</summary>
        private void UpdateProjectedModifiers(Input.KeyMapsMapper keyMaps)
        {
            ApplyProjectedModifier(Vk.LControl, keyMaps.CtrlHeld);
            ApplyProjectedModifier(Vk.RControl, keyMaps.CtrlHeld);
            ApplyProjectedModifier(Vk.LShift, keyMaps.ShiftHeld);
            ApplyProjectedModifier(Vk.RShift, keyMaps.ShiftHeld);
            ApplyProjectedModifier(Vk.LMenu, keyMaps.AltHeld);
            ApplyProjectedModifier(Vk.RMenu, keyMaps.AltHeld);
            ApplyProjectedModifier(Vk.LWin, keyMaps.WindowsHeld);

            // CapsLock lights from the OS toggle state (lights like Shift while ON).
            ApplyProjectedModifier(Vk.Capital, Native.NativeMethods.CapsLockActive);
        }

        private double _lastPromptOpacity = double.NaN;
        private readonly HashSet<KeyboardLayout.KeyDef> _projectedCoveredPending = new();

        /// <summary>Preview-prompt offset stacking: each slot's prompt shifts
        /// up by this many px extra per additional map that targets the same
        /// key, so multi-map prompts don't overlap into one blob.</summary>
        private const double PreviewStackOffsetY = 6.0;

        /// <summary>Border for a projected key at rest: orange while the
        /// active map covers it, neutral gray otherwise.</summary>
        private Brush ProjectedBorderFor(KeyboardLayout.KeyDef key)
        {
            return _projectedCovered.Contains(key) ? projectedBorderCovered : projectedKeyBorderIdle;
        }

        private void ApplyProjectedModifier(ushort vk, bool held)
        {
            KeyboardLayout.KeyDef? key = _projectedLayout?.FindByVk(vk);
            if (key == null || !_projectedKeys.TryGetValue(key, out Border? border))
            {
                return;
            }
            border.BorderBrush = held ? projectedBorderPressed : ProjectedBorderFor(key);
            border.BorderThickness = new Thickness(
                held ? 2.5 : _projectedCovered.Contains(key) ? 1.8 : 1);
        }

        private void ApplyProjectedKeyLabels(bool shiftActive)
        {
            ApplyProjectedKeyLabels(shiftActive, Native.NativeMethods.CapsLockActive);
        }

        private void ApplyProjectedKeyLabels(bool shiftActive, bool capsLockActive)
        {
            if (_projectedLayout == null)
            {
                return;
            }
            foreach (KeyValuePair<KeyboardLayout.KeyDef, Border> pair in _projectedKeys)
            {
                if (pair.Value.Child is TextBlock text && pair.Key.X >= 0)
                {
                    text.Text = Overlay.KeyboardOverlay.VisibleKeyLabel(pair.Key, shiftActive, capsLockActive);
                }
            }
        }

        private double PromptFingerprint()
        {
            KeyMapsLayoutSettings layout = AppSettings.Instance.KeyMaps.Layout;
            return layout.PromptOffsetX * 31.0 + layout.PromptOffsetY * 17.0
                + layout.ExtraKeySpacing * 13.0
                + layout.IconScale * 43.0
                + AppSettings.Instance.KeySpacing * 47.0
                + (layout.ProjectKeyboard ? 3.0 : 0.0);
        }

        /// <summary>Projected-view press source: same physical controls as the
        /// atom view (Select/Start map to the pad's menu buttons).</summary>
        private static bool IsSlotPressedProjected(ControllerMapper mapper, string slot)
        {
            // Move/scale mode owns the pad: Select/Start highlight nothing either.
            if (ControllerMapper.KeyMapsMoveModeActive)
            {
                return false;
            }
            if (slot == "Select") return mapper.LatestSnapshot.View;
            if (slot == "Start") return mapper.LatestSnapshot.Menu;
            return IsSlotPressed(mapper, slot);
        }

        // ── Frozen projected-key brushes ─────────────────────────────────────────
        // Opaque bases: all translucency comes ONLY from the per-key Opacity
        // (KeyboardKeyOpacity slider) — a semi-alpha brush multiplied by element
        // opacity made keys look translucent even at slider = 1.
        private static readonly Brush projectedKeyFillIdle =
            new SolidColorBrush(Color.FromRgb(0x14, 0x14, 0x1A));
        private static readonly Brush projectedKeyFillPressed =
            new SolidColorBrush(Color.FromRgb(0x2E, 0x8B, 0x57));
        private static readonly Brush projectedKeyBorderIdle =
            new SolidColorBrush(Color.FromRgb(0xC8, 0xC8, 0xD0));
        private static readonly Brush projectedBorderPressed =
            new SolidColorBrush(Color.FromRgb(0x7C, 0xFC, 0x9A));
        private static readonly Brush projectedBorderCovered =
            new SolidColorBrush(Color.FromRgb(0xFF, 0x8C, 0x00));
        private static readonly Brush projectedKeyText =
            new SolidColorBrush(Color.FromArgb(0xE6, 0xE8, 0xE8, 0xF0));

        // ── Geometry tables ─────────────────────────────────────────────────────

        private sealed class SlotPlacement
        {
            public SlotPlacement(string slotName, double column, double row)
            {
                SlotName = slotName;
                Column = column;
                Row = row;
            }

            public string SlotName;
            public double Column;
            public double Row;
        }

        private sealed class ClusterGeometry
        {
            /// <summary>D-pad: plus shape (corners free for shadow lanes).</summary>
            public static readonly ClusterGeometry DPad = new(new List<SlotPlacement>
            {
                new("DPadUp", 1, 0),
                new("DPadLeft", 0, 1),
                new("DPadRight", 2, 1),
                new("DPadDown", 1, 2),
            }, 3, 3);

            /// <summary>Face buttons: same plus shape.</summary>
            public static readonly ClusterGeometry Face = new(new List<SlotPlacement>
            {
                new("FaceY", 1, 0),
                new("FaceX", 0, 1),
                new("FaceB", 2, 1),
                new("FaceA", 1, 2),
            }, 3, 3);

            /// <summary>Left stick: cross with the PRESS tile centered between
            /// the left and right keys (user request), down below it.</summary>
            public static readonly ClusterGeometry LeftStick = new(new List<SlotPlacement>
            {
                new("LeftStickUp", 1, 0),
                new("LeftStickLeft", 0, 1),
                new("LeftStickRight", 2, 1),
                new("LeftStickPress", 1, 1.7),
                new("LeftStickDown", 1, 2.5),
            }, 3, 4);

            /// <summary>Right stick: mirrored geometry.</summary>
            public static readonly ClusterGeometry RightStick = new(new List<SlotPlacement>
            {
                new("RightStickUp", 1, 0),
                new("RightStickLeft", 0, 1),
                new("RightStickRight", 2, 1),
                new("RightStickPress", 1, 1.7),
                new("RightStickDown", 1, 2.5),
            }, 3, 4);

            public ClusterGeometry(List<SlotPlacement> placements, int columns, int rows)
            {
                Placements = placements;
                Columns = columns;
                Rows = rows;
            }

            public List<SlotPlacement> Placements;
            public int Columns;
            public int Rows;
        }

        private sealed class ChipView
        {
            public Border Border = null!;
            public int ModIndex;
        }

        private sealed class TileView
        {
            public Border Border = null!;
            public TextBlock Label = null!;
            public string Slot = "";
            public double Left;
            public double Top;
            public double Width;
        }

        // ── Click-through / non-activating window ───────────────────────────────

        private const int GWLExStyle = -20;
        private const int WSExLayered = 0x00080000;
        private const int WSExTransparent = 0x00000020;
        private const int WSExToolWindow = 0x00000080;
        private const int WSExNoActivate = 0x08000000;

        [DllImport("user32.dll", EntryPoint = "GetWindowLong")]
        private static extern int GetWindowLong(IntPtr hWnd, int nIndex);

        [DllImport("user32.dll", EntryPoint = "SetWindowLong")]
        private static extern int SetWindowLong(IntPtr hWnd, int nIndex, int dwNewLong);

        private void ApplyClickThroughExStyles()
        {
            IntPtr handle = new System.Windows.Interop.WindowInteropHelper(this).Handle;
            int extendedStyle = GetWindowLong(handle, GWLExStyle);
            _ = SetWindowLong(handle, GWLExStyle,
                extendedStyle | WSExLayered | WSExTransparent | WSExToolWindow | WSExNoActivate);
        }

        private double _windowKeySize = 1.0;

        private void PositionBottomCenter()
        {
            double screenWidth = SystemParameters.WorkArea.Width;
            double screenHeight = SystemParameters.WorkArea.Height;
            Left = (screenWidth - Width) / 2.0;
            Top = screenHeight - Height - 24.0;
        }

        /// <summary>Reuses the last gamepad-moved position when available
        /// (-1 = never moved → falls back to bottom-center).</summary>
        private void RestorePersistedPosition()
        {
            double storedLeft = AppSettings.Instance.KeyMapsOverlayLeft;
            double storedTop = AppSettings.Instance.KeyMapsOverlayTop;
            if (storedLeft < 0 || storedTop < 0)
            {
                PositionBottomCenter();
                return;
            }

            double screenWidth = SystemParameters.WorkArea.Width;
            double screenHeight = SystemParameters.WorkArea.Height;
            Left = Math.Clamp(storedLeft, -Width + 80.0, Math.Max(screenWidth - 40.0, 80.0 - Width));
            Top = Math.Clamp(storedTop, 0.0, Math.Max(screenHeight - 40.0, 0.0));
        }
    }
}
