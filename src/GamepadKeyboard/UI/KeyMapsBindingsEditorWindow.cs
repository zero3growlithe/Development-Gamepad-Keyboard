using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
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
                combo.Items.Add(action);
            }
            combo.Items.Add(PoolItem);
            string current = ReadSlot(map, slot);
            if (current.Length == 0)
            {
                combo.SelectedItem = NoneItem;
            }
            else
            {
                combo.Items.Add(current);
                combo.SelectedItem = current;
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
                        combo.SelectedItem = ReadSlot(map, capturedSlot).Length == 0 ? NoneItem : ReadSlot(map, capturedSlot);
                        _suppress = false;
                        return;
                    }
                    if (!combo.Items.Contains(captured))
                    {
                        combo.Items.Add(captured);
                    }
                    WriteSlot(map, capturedSlot, captured);
                }
                else if (chosen == NoneItem || chosen == EmptyItem)
                {
                    WriteSlot(map, capturedSlot, "");
                }
                else
                {
                    WriteSlot(map, capturedSlot, chosen);
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
