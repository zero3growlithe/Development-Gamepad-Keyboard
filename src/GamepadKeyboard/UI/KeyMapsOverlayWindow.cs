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
        private const double ShadowWidth = 48.0;
        private const double ShadowHeight = 28.0;
        private const double ChipWidth = 64.0;
        private const double ChipHeight = 26.0;
        private const double ChipGap = 10.0;
        private const double LabelFontSize = 11.5;
        private const double ShadowFontSize = 9.0;
        private const double CenterColumnOffset = 14.0;
        private const double IdleShadowOpacity = 0.42;
        private const double ActiveOpacity = 1.0;

        private static readonly string[] ModifierNames = { "Ctrl", "Shift", "Alt", "Win" };

        // ── Durable UI state ────────────────────────────────────────────────────

        private readonly Canvas _root;
        private readonly TextBlock _mapNameLabel;
        private readonly List<ChipView> _chips = new();
        private readonly Dictionary<string, TileView> _tiles = new();
        private readonly Dictionary<string, List<TileView>> _shadows = new();
        private List<KeyMapDefinition> _maps = new();
        private int _lastRenderedMapIndex;
        private bool _wasMapsKeyHeld;
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
                    ClearShadows();
                    Visibility = Visibility.Hidden;
                }
                return;
            }

            if (!_shown)
            {
                _shown = true;
                _bindingsDirty = true;
                _wasMapsKeyHeld = false;
                _lastRenderedMapIndex = -1;
                _lastLayoutFingerprint = double.NaN;
                if (FollowCursor)
                {
                    PositionAtCursor();
                }
                else
                {
                    PositionBottomCenter();
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

            bool mapsKeyHeld = keyMaps.MapsKeyHeld;
            if (mapsKeyHeld && !_wasMapsKeyHeld)
            {
                BuildShadows();
            }
            else if (!mapsKeyHeld && _wasMapsKeyHeld)
            {
                ClearShadows();
            }
            _wasMapsKeyHeld = mapsKeyHeld;

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
            ClearShadows();
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
            BuildCluster(activeIndex, -1, -1, ClusterGeometry.DPad, layout.DPadOffsetX, layout.DPadOffsetY, layout.DPadSpread, boardScale);
            BuildCluster(activeIndex, +1, -1, ClusterGeometry.Face, layout.FaceOffsetX, layout.FaceOffsetY, layout.FaceSpread, boardScale);
            BuildCluster(activeIndex, -1, +1, ClusterGeometry.LeftStick, layout.LeftStickOffsetX, layout.LeftStickOffsetY, layout.LeftStickSpread, boardScale);
            BuildCluster(activeIndex, +1, +1, ClusterGeometry.RightStick, layout.RightStickOffsetX, layout.RightStickOffsetY, layout.RightStickSpread, boardScale);
            ApplyMapName(activeIndex);
            _lastRenderedMapIndex = activeIndex;
        }

        /// <summary>Fingerprint of every layout-relevant setting; a change
        /// forces a full rebuild so live edits show immediately.</summary>
        private static double LayoutFingerprint()
        {
            KeyMapsLayoutSettings layout = AppSettings.Instance.KeyMaps.Layout;
            return layout.DPadOffsetX + layout.DPadOffsetY * 1.001 +
                   layout.FaceOffsetX * 1.002 + layout.FaceOffsetY * 1.003 +
                   layout.LeftStickOffsetX * 1.004 + layout.LeftStickOffsetY * 1.005 +
                   layout.RightStickOffsetX * 1.006 + layout.RightStickOffsetY * 1.007 +
                   layout.DPadSpread * 2.0 + layout.FaceSpread * 2.1 +
                   layout.LeftStickSpread * 2.2 + layout.RightStickSpread * 2.3 +
                   layout.KeySize * 3.0;
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
            double comboRowY = 14 * boardScale;
            double comboX = ChipColumnX(3, boardScale) + ChipWidth * boardScale + 18 * boardScale;
            _comboChips[0] = MakeComboChip("L1", comboX, comboRowY, boardScale);
            _comboChips[1] = MakeComboChip("R1", comboX + 56 * boardScale, comboRowY, boardScale);
            _comboChips[2] = MakeComboChip("L+R", comboX + 112 * boardScale, comboRowY, boardScale);
            double mapNameLeft = ChipColumnX(3, boardScale) + ChipWidth * boardScale + 150 * boardScale;
            Canvas.SetLeft(_mapNameLabel, mapNameLeft);
            Canvas.SetTop(_mapNameLabel, 16 * boardScale);
            _root.Children.Add(_mapNameLabel);
        }

        private static double ChipColumnX(int index, double boardScale)
        {
            double totalWidth = ModifierNames.Length * ChipWidth + (ModifierNames.Length - 1) * ChipGap;
            return (BoardWidth - totalWidth) / 2.0 - 120.0 + index * (ChipWidth + ChipGap) * boardScale;
        }

        private void BuildCenterColumn(int activeIndex, double boardScale)
        {
            double centerX = BoardWidth / 2.0;
            double centerY = BoardHeight / 2.0 + 20.0;
            AddTile("Select", _maps[activeIndex],
                centerX - TileWidth / 2.0 - CenterColumnOffset, centerY - TileHeight - 2.0, boardScale);
            AddTile("Start", _maps[activeIndex],
                centerX - TileWidth / 2.0 + CenterColumnOffset, centerY + 2.0, boardScale);
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
            TextBlock labelBlock = MakeLabel(SplitLabel(label), LabelFontSize * boardScale);
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

        // ── Shadow pills (maps key held) ────────────────────────────────────────

        private void BuildShadows()
        {
            ClearShadows();
            foreach (KeyValuePair<string, TileView> pair in _tiles)
            {
                TileView origin = pair.Value;
                AddShadowPill(origin, 3);   // Symbols 3 (L1 combo) — up/left lane
                AddShadowPill(origin, 2);   // Symbols 2 (R1 combo) — right/below lane
                AddShadowPill(origin, 4);   // Function Keys (L1+R1) — below lane
            }
        }

        private void AddShadowPill(TileView origin, int mapIndex)
        {
            if (mapIndex >= _maps.Count)
            {
                return;
            }
            string label = LabelFor(_maps[mapIndex], origin.Slot);
            if (string.IsNullOrWhiteSpace(label))
            {
                return;   // slot unbound on that map — no shadow pill
            }
            (double dx, double dy) = ShadowOffsetOf(origin.Slot, mapIndex);
            double tileWidth = origin.Width <= 0 ? TileWidth : origin.Width;
            // Negative dx: pill hugs the tile's LEFT edge (grows leftwards);
            // positive dx: pill grows rightwards from the tile's left edge.
            double resolvedLeft = dx < 0
                ? origin.Left + dx * (tileWidth / TileWidth) + (tileWidth - ShadowWidth)
                : origin.Left + dx * (tileWidth / TileWidth);
            TextBlock labelBlock = MakeLabel(SplitLabel(label), ShadowFontSize);
            Border border = MakeTileBorder(ShadowWidth, ShadowHeight);
            border.Child = labelBlock;
            border.Opacity = IdleShadowOpacity;
            Canvas.SetLeft(border, resolvedLeft);
            Canvas.SetTop(border, origin.Top + dy);
            Canvas.SetZIndex(border, 9);
            _root.Children.Add(border);
            if (!_shadows.TryGetValue(origin.Slot, out List<TileView>? list))
            {
                list = new List<TileView>();
                _shadows[origin.Slot] = list;
            }
            list.Add(new TileView
            {
                Border = border,
                Label = labelBlock,
                Slot = origin.Slot,
                Left = resolvedLeft,
                Top = origin.Top + dy,
                MapIndex = mapIndex,
            });
        }

        private void ClearShadows()
        {
            foreach (List<TileView> list in _shadows.Values)
            {
                for (int index = 0; index < list.Count; index++)
                {
                    _root.Children.Remove(list[index].Border);
                }
            }
            _shadows.Clear();
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
                tile.Border.Opacity = boundHere ? ActiveOpacity : IdleShadowOpacity;
                Canvas.SetZIndex(tile.Border, boundHere ? 10 : 5);
            }
            foreach (List<TileView> list in _shadows.Values)
            {
                for (int index = 0; index < list.Count; index++)
                {
                    TileView pill = list[index];
                    pill.Border.Opacity = pill.MapIndex == activeIndex && _wasMapsKeyHeld
                        ? ActiveOpacity
                        : IdleShadowOpacity;
                }
            }
        }

        // ── Modifier chips ──────────────────────────────────────────────────────

        /// <summary>Combo chips: [L1] [R1] [L+R] in the modifier row always
        /// visible (dim by default), brightening to combo-active opacity.</summary>
        private readonly Border?[] _comboChips = new Border?[3];

        private Border MakeComboChip(string label, double x, double y, double boardScale)
        {
            Border chip = new()
            {
                Width = 46 * boardScale,
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
                    FontSize = 12 * boardScale,
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
                chip.Opacity = active ? 1.0 : 0.55;
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
                    FontSize = 13 * boardScale,
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

        /// <summary>Fixed shadow pill offset per slot + combo map. dx negative
        /// hugs the tile's left edge (pill sits to its left), dx positive
        /// offsets rightwards from the tile's left edge; lanes are tuned per
        /// slot so pills land in the free corners and below-lane of the plus
        /// shapes and the three combo lanes never collide. Positions NEVER
        /// depend on the currently active map — muscle memory stays stable.</summary>
        private static (double Dx, double Dy) ShadowOffsetOf(string slot, int mapIndex)
        {
            return (slot, mapIndex) switch
            {
                // Up slots: diagonal corners above the cluster are free.
                ("DPadUp", 3) => (-52, -32),
                ("DPadUp", 2) => (+52, -32),
                ("DPadUp", 4) => (0, +58),
                ("FaceY", 3) => (+52, -32),
                ("FaceY", 2) => (-52, -32),
                ("FaceY", 4) => (0, +58),
                ("LeftStickUp", 3) => (-52, -32),
                ("LeftStickUp", 2) => (+52, -32),
                ("LeftStickUp", 4) => (0, +58),
                ("RightStickUp", 3) => (+52, -32),
                ("RightStickUp", 2) => (-52, -32),
                ("RightStickUp", 4) => (0, +58),

                // Left-column slots: outside-left is free.
                ("DPadLeft", 3) => (-56, -36),
                ("DPadLeft", 2) => (-56, +8),
                ("DPadLeft", 4) => (-56, +52),
                ("FaceX", 3) => (+52, -36),
                ("FaceX", 2) => (+52, +8),
                ("FaceX", 4) => (+52, +52),
                ("LeftStickLeft", 3) => (-56, -36),
                ("LeftStickLeft", 2) => (-56, +8),
                ("LeftStickLeft", 4) => (-56, +52),
                ("RightStickLeft", 3) => (+52, -36),
                ("RightStickLeft", 2) => (+52, +8),
                ("RightStickLeft", 4) => (+52, +52),

                // Right-column slots: outside-right is free.
                ("DPadRight", 3) => (+56, -36),
                ("DPadRight", 2) => (+56, +8),
                ("DPadRight", 4) => (+56, +52),
                ("FaceB", 3) => (-56, -36),
                ("FaceB", 2) => (-56, +8),
                ("FaceB", 4) => (-56, +52),
                ("LeftStickRight", 3) => (+56, -36),
                ("LeftStickRight", 2) => (+56, +8),
                ("LeftStickRight", 4) => (+56, +52),
                ("RightStickRight", 3) => (-56, -36),
                ("RightStickRight", 2) => (-56, +8),
                ("RightStickRight", 4) => (-56, +52),

                // Down slots: below is free.
                ("DPadDown", 3) => (-52, +30),
                ("DPadDown", 2) => (+52, +30),
                ("DPadDown", 4) => (0, +58),
                ("FaceA", 3) => (+52, +30),
                ("FaceA", 2) => (-52, +30),
                ("FaceA", 4) => (0, +58),
                ("LeftStickDown", 3) => (-52, +26),
                ("LeftStickDown", 2) => (+52, +26),
                ("LeftStickDown", 4) => (0, +58),
                ("RightStickDown", 3) => (+52, +26),
                ("RightStickDown", 2) => (-52, +26),
                ("RightStickDown", 4) => (0, +58),

                // Stick presses: side lanes at press-row height.
                ("LeftStickPress", 3) => (-56, +8),
                ("LeftStickPress", 2) => (+56, +8),
                ("LeftStickPress", 4) => (0, +58),
                ("RightStickPress", 3) => (+56, +8),
                ("RightStickPress", 2) => (-56, +8),
                ("RightStickPress", 4) => (0, +58),

                // Center column: Select shadows above, Start shadows below.
                ("Select", 3) => (-56, -34),
                ("Select", 2) => (+56, -34),
                ("Select", 4) => (-20, +50),
                ("Start", 3) => (-56, -34),
                ("Start", 2) => (+56, -34),
                ("Start", 4) => (+20, +50),

                _ => (0, +58),
            };
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
    }
}