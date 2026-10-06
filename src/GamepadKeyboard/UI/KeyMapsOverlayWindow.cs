using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Runtime.InteropServices;
using GamepadKeyboard.Native;
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
        private List<KeyMapDefinition> _maps = new();
        private int _lastRenderedMapIndex;
        private bool _bindingsDirty = true;
        private bool _shown;
        private bool _followCursor;
        private double _lastLayoutFingerprint = double.NaN;

        /// <summary>Mirrors the keyboard window's "always show at cursor
        /// position" behavior (set by AppOrchestrator from AppSettings).</summary>
        public bool FollowCursor { get; set; }

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

            bool wantVisible = mapper != null
                && mapper.InputEnabled
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
                if (FollowCursor)
                {
                    PositionAtCursor();
                }
                else
                {
                    RestorePersistedPosition();
                }
                Visibility = Visibility.Visible;
            }

            Input.KeyMapsMapper keyMaps = mapper.KeyMaps!;
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
            bool shiftHeld = keyMaps.ShiftHeld;
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
            _followCursor = true;
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
            BuildModifierRow(boardScale);
            BuildCenterColumn(activeIndex, boardScale);
            BuildAtoms(boardScale);
            ApplyMapName(activeIndex);
            _lastRenderedMapIndex = activeIndex;
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
                   layout.SelectStartOffsetX * 4.7 + layout.SelectStartOffsetY * 4.8 +
                   layout.SelectStartScale * 4.9 + layout.SelectStartSpreadX * 5.0 +
                   (layout.StickUniformSpread ? 1.0 : 0.0) * 5.1 +
                   layout.LeftStickCenterOffsetY * 5.2 + layout.LeftStickBottomOffsetY * 5.3 +
                   layout.RightStickCenterOffsetY * 5.4 + layout.RightStickBottomOffsetY * 5.5;
            return fingerprint;
        }

        private void BuildModifierRow(double boardScale)
        {
            for (int index = 0; index < ModifierNames.Length; index++)
            {
                Border chipBorder = MakeChip(ModifierNames[index], boardScale);
                Canvas.SetLeft(chipBorder, ChipColumnX(index, boardScale));
                Canvas.SetTop(chipBorder, 14 * boardScale);
                _root.Children.Add(chipBorder);
                _chips.Add(new ChipView { Border = chipBorder, ModIndex = index });
            }
            KeyMapsLayoutSettings layout = AppSettings.Instance.KeyMaps.Layout;
            _mapNameLabel.Width = BoardWidth;
            _mapNameLabel.TextAlignment = TextAlignment.Center;
            _mapNameLabel.FontSize = 17 * boardScale * layout.FontScale;
            Canvas.SetLeft(_mapNameLabel, 0);
            Canvas.SetTop(_mapNameLabel, (14 + ChipHeight + 6) * boardScale);
            _root.Children.Add(_mapNameLabel);
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
            _comboChips[0] = MakeComboChip("L1", 12, chipY, boardScale);
            _comboChips[1] = MakeComboChip("R1", BoardWidth - 12 - 46 * boardScale, chipY, boardScale);
            _comboChips[2] = MakeComboChip("L1 + R1", (BoardWidth - 74 * boardScale) / 2.0, bottomY, boardScale);
            _lastRenderedComboHeld = false;
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

        private void BuildCluster(
            int activeIndex, double clusterX, double clusterY, ClusterGeometry cluster,
            double offsetPixels, double offsetY, double spread, double boardScale)
        {
            double pitchX = (TileWidth + TileGap) * spread;
            double pitchY = (TileHeight + TileGap) * spread;
            double clusterSpanX = cluster.Columns * pitchX - TileGap;
            double clusterSpanY = cluster.Rows * pitchY - TileGap;
            double centerX = BoardWidth / 2.0;
            double centerY = BoardHeight / 2.0 + 20.0;
            double breadthX = 96.0;
            double breadthY = 132.0;
            double baseX = centerX + clusterX * (clusterSpanX / 2.0 + breadthX / 2.0)
                + offsetPixels * boardScale - clusterSpanX / 2.0;
            double baseY = centerY + clusterY * (clusterSpanY / 2.0 + breadthY / 2.0)
                + offsetY * boardScale - clusterSpanY / 2.0;
            for (int index = 0; index < cluster.Placements.Count; index++)
            {
                SlotPlacement placement = cluster.Placements[index];
                AddTile(placement.SlotName, _maps[activeIndex],
                    baseX + placement.Column * pitchX * boardScale,
                    baseY + placement.Row * pitchY * boardScale,
                    boardScale);
            }
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
        private bool _lastRenderedComboHeld;
        private bool _lastRenderedShiftHeld;

        private Border MakeComboChip(string label, double x, double y, double boardScale)
        {
            Border chip = new()
            {
                Width = label.Length > 2 ? 74 * boardScale : 46 * boardScale,
                Height = ChipHeight * boardScale,
                CornerRadius = new CornerRadius(8),
                BorderThickness = new Thickness(1),
                BorderBrush = new SolidColorBrush(Color.FromArgb(0x55, 0xFF, 0xFF, 0xFF)),
                Background = new SolidColorBrush(Color.FromArgb(0x30, 0x20, 0x20, 0x20)),
                Opacity = 0.55,
                Child = new TextBlock
                {
                    Text = label,
                    Foreground = Brushes.White,
                    FontSize = 12 * boardScale * AppSettings.Instance.KeyMaps.Layout.FontScale,
                    FontWeight = FontWeights.Medium,
                    TextAlignment = TextAlignment.Center,
                    HorizontalAlignment = HorizontalAlignment.Center,
                    VerticalAlignment = VerticalAlignment.Center,
                },
            };
            Canvas.SetLeft(chip, x);
            Canvas.SetTop(chip, y);
            _root.Children.Add(chip);
            return chip;
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
            public int MapIndex;
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
            _followCursor = false;
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
            _followCursor = false;
        }
    }
}
