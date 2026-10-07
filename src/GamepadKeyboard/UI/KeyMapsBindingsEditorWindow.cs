using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Text;
using System.Windows.Controls;
using System.Windows.Media;
using GamepadKeyboard.Settings;
using GamepadKeyboard.UI;

namespace GamepadKeyboard.UI
{
    /// <summary>
    /// Key Maps mode binding editor: the fixed physical-slot list divided by
    /// input circle (D-Pad, Face Buttons, Left Analog Stick, Right Analog
    /// Stick, Center = Select/Start), one tab per key map (Utility .. Function
    /// Keys). The user can only change each fixed slot's action value — the
    /// same ActionCatalog the keyboard/mouse bindings editor offers, with the
    /// same "Pool for keyboard key…" capture.
    /// </summary>
    public sealed class KeyMapsBindingsEditorWindow : Window
    {
        private const string NoneItem = "None";
        private const string PoolItem = "Pool for keyboard key...";
        private const string EmptyItem = "(unbound)";

        private static readonly (string Slot, string Label)[] DPadSlots =
        {
            ("DPadUp", "D-pad Up"), ("DPadDown", "D-pad Down"),
            ("DPadLeft", "D-pad Left"), ("DPadRight", "D-pad Right"),
        };
        private static readonly (string Slot, string Label)[] FaceSlots =
        {
            ("FaceY", "Y (north)"), ("FaceX", "X (west)"),
            ("FaceB", "B (east)"), ("FaceA", "A (south)"),
        };
        private static readonly (string Slot, string Label)[] LeftStickSlots =
        {
            ("LeftStickUp", "Left stick Up"), ("LeftStickDown", "Left stick Down"),
            ("LeftStickLeft", "Left stick Left"), ("LeftStickRight", "Left stick Right"),
            ("LeftStickPress", "Left stick Press (L3)"),
        };
        private static readonly (string Slot, string Label)[] RightStickSlots =
        {
            ("RightStickUp", "Right stick Up"), ("RightStickDown", "Right stick Down"),
            ("RightStickLeft", "Right stick Left"), ("RightStickRight", "Right stick Right"),
            ("RightStickPress", "Right stick Press (R3)"),
        };
        private static readonly (string Slot, string Label)[] CenterSlots =
        {
            ("Select", "View / Select"), ("Start", "Menu / Options"),
        };
        private static readonly (string Title, (string, string)[] Slots)[] Circles =
        {
            ("── D-Pad ──", DPadSlots),
            ("── Face Buttons ──", FaceSlots),
            ("── Left Analog Stick ──", LeftStickSlots),
            ("── Right Analog Stick ──", RightStickSlots),
            ("── Center (select/start) ──", CenterSlots),
        };

        private readonly KeyMapsSettings _mapsSettings;
        private bool _suppress;

        public KeyMapsBindingsEditorWindow()
        {
            Title = "Key Maps mode — gamepad bindings";
            Width = 620;
            Height = 620;
            MinWidth = 560;
            MinHeight = 480;
            WindowStartupLocation = WindowStartupLocation.CenterScreen;
            ShowInTaskbar = false;

            _mapsSettings = AppSettings.Instance.KeyMaps;
            _mapsSettings.Normalize();

            var root = new DockPanel { Margin = new Thickness(10) };
            FrameworkElement buttonBar = BuildButtonBar();
            DockPanel.SetDock(buttonBar, Dock.Bottom);
            root.Children.Add(buttonBar);
            var tabs = new TabControl { Margin = new Thickness(0, 8, 0, 0) };
            ScrollViewer systemScroll = new() { VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
            systemScroll.Content = BuildSystemTab();
            tabs.Items.Add(new TabItem { Header = "System & open combos", Content = systemScroll });
            for (int mapIndex = 0; mapIndex < _mapsSettings.Maps.Count; mapIndex++)
            {
                KeyMapDefinition map = _mapsSettings.Maps[mapIndex];
                var scroll = new ScrollViewer { VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
                scroll.Content = BuildMapTab(map, mapIndex);
                tabs.Items.Add(new TabItem { Header = string.IsNullOrWhiteSpace(map.Name) ? ("Map " + (mapIndex + 1)) : map.Name, Content = scroll });
            }
            root.Children.Add(tabs);
            Content = root;
        }

        /// <summary>The system tab: what the triggers/bumpers run (actions
        /// from the shared catalog) and the button combination that opens each
        /// map while the maps key is held (subset of L2, L1, R1, R2, L3, R3;
        /// empty = the maps key alone).</summary>
        private FrameworkElement BuildSystemTab()
        {
            Grid grid = new() { Margin = new Thickness(10) };
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            int row = 0;

            AddHeader(grid, ref row, "Trigger / bumper actions");
            AddSystemActionRow(grid, ref row, "Left trigger (L2) action", _mapsSettings.LeftTriggerAction, value => _mapsSettings.LeftTriggerAction = value);
            AddSystemActionRow(grid, ref row, "Left bumper (L1) action", _mapsSettings.LeftBumperAction, value => _mapsSettings.LeftBumperAction = value);
            AddSystemActionRow(grid, ref row, "Right bumper (R1) action", _mapsSettings.RightBumperAction, value => _mapsSettings.RightBumperAction = value);
            AddSystemActionRow(grid, ref row, "Right trigger (R2) action", _mapsSettings.RightTriggerAction, value => _mapsSettings.RightTriggerAction = value);

            AddHeader(grid, ref row, "Mitigate lock (use the action while the maps key is held)");
            AddMitigateLockRow(grid, ref row, "Left trigger (L2) mitigates lock",
                () => _mapsSettings.LeftTriggerMitigateLock, value => _mapsSettings.LeftTriggerMitigateLock = value,
                () => _mapsSettings.LeftTriggerAction);
            AddMitigateLockRow(grid, ref row, "Left bumper (L1) mitigates lock",
                () => _mapsSettings.LeftBumperMitigateLock, value => _mapsSettings.LeftBumperMitigateLock = value,
                () => _mapsSettings.LeftBumperAction);
            AddMitigateLockRow(grid, ref row, "Right bumper (R1) mitigates lock",
                () => _mapsSettings.RightBumperMitigateLock, value => _mapsSettings.RightBumperMitigateLock = value,
                () => _mapsSettings.RightBumperAction);
            AddMitigateLockRow(grid, ref row, "Right trigger (R2) mitigates lock",
                () => _mapsSettings.RightTriggerMitigateLock, value => _mapsSettings.RightTriggerMitigateLock = value,
                () => _mapsSettings.RightTriggerAction);

            AddHeader(grid, ref row, "Map open combinations (held with the maps key)");
            TextBlock hint = new()
            {
                Text = "Buttons held together with the maps key (R2 by default). " +
                       "Available: L2, L1, R1, R2, L3, R3. Leave empty for the maps key alone. " +
                       "The best-matching map wins; releasing the maps key returns to Utility.",
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 0, 0, 6),
                Foreground = System.Windows.Media.Brushes.Gray,
            };
            Grid.SetRow(hint, row);
            Grid.SetColumnSpan(hint, 2);
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            grid.Children.Add(hint);
            row++;

            for (int mapIndex = 0; mapIndex < _mapsSettings.Maps.Count; mapIndex++)
            {
                KeyMapDefinition map = _mapsSettings.Maps[mapIndex];
                string mapName = string.IsNullOrWhiteSpace(map.Name) ? ("Map " + (mapIndex + 1)) : map.Name;
                if (mapIndex == 0)
                {
                    // Utility is the fallback map — opened by releasing the maps key; not configurable.
                    TextBlock fixedLabel = new()
                    {
                        Text = mapName + " — opens when the maps key is released (fixed)",
                        VerticalAlignment = VerticalAlignment.Center,
                        Margin = new Thickness(0, 2, 10, 2),
                        Foreground = System.Windows.Media.Brushes.Gray,
                    };
                    Grid.SetRow(fixedLabel, row);
                    grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
                    grid.Children.Add(fixedLabel);
                    row++;
                    continue;
                }
                AddOpenComboRow(grid, ref row, map, mapName);
            }
            return grid;
        }

        private void AddSystemActionRow(Grid grid, ref int row, string label, string current, Action<string> write)
        {
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            TextBlock name = new()
            {
                Text = label,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, 2, 10, 2),
            };
            Grid.SetRow(name, row);
            Grid.SetColumn(name, 0);
            grid.Children.Add(name);

            ComboBox combo = new() { MinWidth = 260 };
            foreach (string action in ActionCatalog.All)
            {
                combo.Items.Add(ActionCatalog.DisplayLabel(action));
            }
            combo.SelectedItem = ActionCatalog.DisplayLabel(current);
            combo.SelectionChanged += (_, __) =>
            {
                if (_suppress)
                {
                    return;
                }
                string chosen = ActionCatalog.ValueOf(combo.SelectedItem as string ?? "");
                if (!string.IsNullOrEmpty(chosen))
                {
                    write(chosen);
                    // A button becoming the maps key loses combo membership:
                    // strip it from every map's OpenWith so chords stay valid.
                    if (chosen is "MapsModifierHold" or "MapsModifierToggle")
                    {
                        string mapsModifierButton = MapsModifierButton();
                        foreach (KeyMapDefinition comboMap in _mapsSettings.Maps)
                        {
                            HashSet<string> parsed = ParseOpenCombo(comboMap.OpenWith);
                            if (parsed.Remove(mapsModifierButton))
                            {
                                comboMap.OpenWith = string.Join("+",
                                    parsed.OrderBy(ChordOrder));
                            }
                        }
                    }
                    Persist();
                    RefreshMitigateLockRows();
                }
            };
            Grid.SetRow(combo, row);
            Grid.SetColumn(combo, 1);
            grid.Children.Add(combo);
            row++;
        }

        /// <summary>"Mitigate lock" checkbox row: allows the button's action
        /// to be used while the maps key is held (see the settings property
        /// doc). DISABLED + unchecked when the button's action is a maps-modifier
        /// action — the maps key has no locked state to mitigate.</summary>
        private void AddMitigateLockRow(
            Grid grid, ref int row, string label,
            Func<bool> read, Action<bool> write, Func<string> readAction)
        {
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            CheckBox check = new()
            {
                Content = label,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, 2, 10, 2),
                HorizontalAlignment = HorizontalAlignment.Left,
            };
            Grid.SetColumnSpan(check, 2);
            Grid.SetRow(check, row);
            grid.Children.Add(check);
            row++;

            void Refresh()
            {
                bool isMapsModifier = readAction() is "MapsModifierHold" or "MapsModifierToggle";
                check.IsEnabled = !isMapsModifier;
                check.IsChecked = isMapsModifier ? false : read();
                check.ToolTip = isMapsModifier
                    ? "Unavailable: this button currently carries a maps-modifier action."
                    : null;
            }
            Refresh();
            _mitigateRefreshers.Add(Refresh);
            check.Click += (_, __) =>
            {
                if (_suppress)
                {
                    return;
                }
                write(check.IsChecked == true);
                Persist();
            };
        }

        /// <summary>Re-runs the gray-out evaluation of every mitigate-lock
        /// checkbox (called after any action dropdown changes).</summary>
        private void RefreshMitigateLockRows()
        {
            foreach (Action refresh in _mitigateRefreshers)
            {
                refresh();
            }
        }

        private readonly List<Action> _mitigateRefreshers = new();

        /// <summary>One "open with" row: six checkboxes (L2, L1, R1, R2, L3, R3)
        /// reflecting the map's OpenWith combination; each change rewrites the
        /// canonical "L2+L1+R1"-style value and persists.</summary>
        private void AddOpenComboRow(Grid grid, ref int row, KeyMapDefinition map, string mapName)
        {
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            TextBlock name = new()
            {
                Text = mapName + " — open with",
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, 2, 10, 2),
            };
            Grid.SetRow(name, row);
            Grid.SetColumn(name, 0);
            grid.Children.Add(name);

            HashSet<string> selected = ParseOpenCombo(map.OpenWith);
            List<CheckBox> rowChecks = new(ChordButtonChoices.Length);
            StackPanel picker = new() { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
            foreach ((string button, string label) in ChordButtonChoices)
            {
                CheckBox check = new() { Content = label, Margin = new Thickness(0, 0, 10, 0), VerticalAlignment = VerticalAlignment.Center, Tag = button };
                check.IsChecked = selected.Contains(button);
                // A button that IS the maps key (maps-modifier action) cannot
                // be a combo member: gray it out and drop it from this combo.
                if (MapsModifierButton() == button)
                {
                    check.IsEnabled = false;
                    check.IsChecked = false;
                    check.ToolTip = "This button is the maps key (its action is a maps-modifier)";
                }
                rowChecks.Add(check);
                picker.Children.Add(check);
            }
            KeyMapDefinition capturedMap = map;
            List<CheckBox> capturedChecks = rowChecks;
            foreach (CheckBox check in rowChecks)
            {
                check.Click += (_, __) =>
                {
                    if (_suppress)
                    {
                        return;
                    }
                    // Rewrite the canonical combination from ALL the row's
                    // boxes (the clicked one already carries its new state).
                    WriteOpenCombo(capturedMap, capturedChecks);
                };
            }
            Grid.SetRow(picker, row);
            Grid.SetColumn(picker, 1);
            grid.Children.Add(picker);
            row++;
        }

        private static readonly (string Button, string Label)[] ChordButtonChoices =
        {
            ("L2", "L2"), ("L1", "L1"), ("R1", "R1"), ("R2", "R2"), ("L3", "L3"), ("R3", "R3"),
        };

        /// <summary>Parses "L2+R1" into a set; unknown tokens are dropped.</summary>
        private static HashSet<string> ParseOpenCombo(string combo)
        {
            HashSet<string> parsed = new(StringComparer.Ordinal);
            if (string.IsNullOrWhiteSpace(combo))
            {
                return parsed;
            }
            foreach (string part in combo.Split('+'))
            {
                foreach ((string button, _) in ChordButtonChoices)
                {
                    if (part == button)
                    {
                        parsed.Add(button);
                        break;
                    }
                }
            }
            return parsed;
        }

        private static int ChordOrder(string button) => button switch
        {
            "L2" => 0, "L1" => 1, "R1" => 2, "R2" => 3, "L3" => 4, "R3" => 5, _ => 6,
        };

        /// <summary>The button currently acting as the maps key (its action is
        /// a maps-modifier action): "L2", "L1", "R1", "R2" — or "" (none;
        /// no button carries the maps modifier).</summary>
        private string MapsModifierButton()
        {
            if (_mapsSettings.LeftTriggerAction is "MapsModifierHold" or "MapsModifierToggle") return "L2";
            if (_mapsSettings.LeftBumperAction is "MapsModifierHold" or "MapsModifierToggle") return "L1";
            if (_mapsSettings.RightBumperAction is "MapsModifierHold" or "MapsModifierToggle") return "R1";
            if (_mapsSettings.RightTriggerAction is "MapsModifierHold" or "MapsModifierToggle") return "R2";
            return "";
        }

        /// <summary>Rebuilds the map's OpenWith value from the row's checked
        /// boxes in canonical L2, L1, R1, L3, R3 order and persists.</summary>
        private void WriteOpenCombo(KeyMapDefinition map, List<CheckBox> checks)
        {
            StringBuilder builder = new(20);
            foreach ((string button, _) in ChordButtonChoices)
            {
                foreach (CheckBox check in checks)
                {
                    if (check.IsChecked == true && string.Equals((string)check.Tag, button, StringComparison.Ordinal))
                    {
                        if (builder.Length > 0)
                        {
                            builder.Append('+');
                        }
                        builder.Append(button);
                        break;
                    }
                }
            }
            map.OpenWith = builder.ToString();
            Persist();
        }

        private FrameworkElement BuildMapTab(KeyMapDefinition map, int mapIndex)
        {
            var grid = new Grid { Margin = new Thickness(10) };
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            int row = 0;
            foreach ((string circleTitle, (string Slot, string Label)[] slots) in Circles)
            {
                AddHeader(grid, ref row, circleTitle);
                foreach ((string slot, string label) in slots)
                {
                    AddSlotRow(grid, ref row, map, mapIndex, slot, label);
                }
            }
            return grid;
        }

        private static void AddHeader(Grid grid, ref int row, string title)
        {
            TextBlock header = new()
            {
                Text = title,
                FontWeight = FontWeights.Bold,
                Margin = new Thickness(0, 10, 0, 4),
            };
            if (row > 0)
            {
                header.Margin = new Thickness(0, 16, 0, 4);
            }
            Grid.SetRow(header, row);
            Grid.SetColumn(header, 0);
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            grid.Children.Add(header);
            row++;
        }

        private void AddSlotRow(Grid grid, ref int row, KeyMapDefinition map, int mapIndex, string slot, string label)
        {
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

            TextBlock name = new()
            {
                Text = label,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, 2, 10, 2),
            };
            Grid.SetRow(name, row);
            Grid.SetColumn(name, 0);
            grid.Children.Add(name);

            ComboBox combo = new() { MinWidth = 260 };
            foreach (string action in ActionCatalog.All)
            {
                combo.Items.Add(ActionCatalog.DisplayLabel(action));
            }
            combo.Items.Add(PoolItem);
            string current = ReadSlot(map, slot);
            string canonicalCurrent = current.Length == 0
                ? current
                : ControllerMapper.NormalizeAction(current);
            if (current.Length == 0)
            {
                combo.SelectedItem = NoneItem;
            }
            else if (combo.Items.Contains(ActionCatalog.DisplayLabel(canonicalCurrent)))
            {
                combo.SelectedItem = ActionCatalog.DisplayLabel(canonicalCurrent);
            }
            else
            {
                combo.Items.Add(canonicalCurrent);
                combo.SelectedItem = canonicalCurrent;
            }
            string capturedSlot = slot;
            int capturedIndex = mapIndex;
            combo.SelectionChanged += (_, __) =>
            {
                if (_suppress)
                {
                    return;
                }
                string chosen = combo.SelectedItem as string ?? NoneItem;
                if (chosen == PoolItem)
                {
                    string? captured = KeyCaptureDialog.Capture("Press a keyboard key for the binding...");
                    if (captured == null)
                    {
                        _suppress = true;
                        combo.SelectedItem = ActionCatalog.DisplayLabel(ControllerMapper.NormalizeAction(ReadSlot(map, capturedSlot)));
                        _suppress = false;
                        return;
                    }
                    // Captured "Key:<name>": select the matching catalog action when
                    // the key exists as one; unknown keys fall back to None.
                    string actionName = captured.StartsWith("Key:", StringComparison.Ordinal) ? captured[4..] : captured;
                    if (ActionCatalog.All.Contains(actionName, StringComparer.Ordinal))
                    {
                        _suppress = true;
                        combo.SelectedItem = ActionCatalog.DisplayLabel(actionName);
                        _suppress = false;
                        WriteSlot(map, capturedSlot, actionName);
                    }
                    else
                    {
                        // Unknown key: nothing dispatchable → fall back to None.
                        _suppress = true;
                        combo.SelectedItem = NoneItem;
                        _suppress = false;
                        WriteSlot(map, capturedSlot, "");
                    }
                }
                else if (chosen == NoneItem || chosen == EmptyItem)
                {
                    WriteSlot(map, capturedSlot, "");
                }
                else
                {
                    WriteSlot(map, capturedSlot, ActionCatalog.ValueOf(chosen));
                }
            };
            Grid.SetRow(combo, row);
            Grid.SetColumn(combo, 1);
            grid.Children.Add(combo);
            row++;
        }

        private FrameworkElement BuildButtonBar()
        {
            var bar = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 8, 0, 0) };
            var defaultsButton = new Button { Content = "Restore map defaults", Padding = new Thickness(12, 3, 12, 3), Margin = new Thickness(0, 0, 8, 0) };
            defaultsButton.Click += (_, __) => RestoreDefaults();
            var okayButton = new Button { Content = "OK", Padding = new Thickness(16, 3, 16, 3), Margin = new Thickness(0, 0, 8, 0) };
            okayButton.Click += (_, __) => Close();
            bar.Children.Add(defaultsButton);
            bar.Children.Add(okayButton);
            return bar;
        }

        private void RestoreDefaults()
        {
            _suppress = true;
            foreach (KeyMapDefinition map in _mapsSettings.Maps)
            {
                foreach ((string circleTitle, (string Slot, string Label)[] slots) in Circles)
                {
                    foreach ((string slot, string label) in slots)
                    {
                        KeyMapDefinition fresh = new KeyMapDefinition(string.IsNullOrWhiteSpace(map.Name) ? "Utility" : map.Name);
                        WriteSlot(map, slot, ReadSlot(fresh, slot));
                    }
                }
            }
            _suppress = false;
            Persist();
        }

        private static string ReadSlot(KeyMapDefinition map, string slot)
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
                _ => "",
            };
        }

        private void WriteSlot(KeyMapDefinition map, string slot, string value)
        {
            switch (slot)
            {
                case "Select": map.Select = value; break;
                case "Start": map.Start = value; break;
                case "DPadUp": map.DPadUp = value; break;
                case "DPadDown": map.DPadDown = value; break;
                case "DPadLeft": map.DPadLeft = value; break;
                case "DPadRight": map.DPadRight = value; break;
                case "FaceY": map.FaceY = value; break;
                case "FaceA": map.FaceA = value; break;
                case "FaceX": map.FaceX = value; break;
                case "FaceB": map.FaceB = value; break;
                case "LeftStickUp": map.LeftStickUp = value; break;
                case "LeftStickDown": map.LeftStickDown = value; break;
                case "LeftStickLeft": map.LeftStickLeft = value; break;
                case "LeftStickRight": map.LeftStickRight = value; break;
                case "LeftStickPress": map.LeftStickPress = value; break;
                case "RightStickUp": map.RightStickUp = value; break;
                case "RightStickDown": map.RightStickDown = value; break;
                case "RightStickLeft": map.RightStickLeft = value; break;
                case "RightStickRight": map.RightStickRight = value; break;
                case "RightStickPress": map.RightStickPress = value; break;
            }
            Persist();
        }

        private void Persist()
        {
            AppSettings.Save();
            AppOrchestrator.NotifyMappingsChanged();
            AppOrchestrator.NotifyKeyMapsLayoutChanged();
        }
    }
}
