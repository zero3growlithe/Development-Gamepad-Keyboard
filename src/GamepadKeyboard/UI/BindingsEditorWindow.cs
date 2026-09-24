using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using GamepadKeyboard.Settings;

namespace GamepadKeyboard.UI
{
    /// <summary>
    /// Edytor przypisań przycisków pada do akcji (tryb klawiatury i myszy).
    /// Lewa kolumna: nazwa przycisku pada, prawa: dropdown z akcją.
    /// Pierwsza pozycja dropdownu "Pool for keyboard key..." łapie wciśnięty klawisz.
    /// </summary>
    public sealed class BindingsEditorWindow : Window
    {
        private const string PoolItem = "Pool for keyboard key...";

        private readonly bool _mouse;
        private readonly ComboBox _profileBox = new() { MinWidth = 170 };
        private readonly TextBox _nameBox = new() { MinWidth = 170 };
        private readonly StackPanel _rowsPanel = new();
        private readonly StackPanel _comboPanel = new();
        private readonly System.Windows.Controls.TextBlock _stickProfileLabel = new()
        {
            Text = "",
            Foreground = System.Windows.Media.Brushes.Gray,
            VerticalAlignment = System.Windows.VerticalAlignment.Center,
            Margin = new System.Windows.Thickness(10, 0, 0, 0)
        };

        private sealed class Row
        {
            public string Label = "";
            public Func<string> Get = () => "None";
            public Action<string> Set = _ => { };
            public ComboBox Box = new() { MinWidth = 240 };
            public string Last = "None";
        }

        private readonly List<Row> _rows = new();
        private int _suppress = 0;

        public BindingsEditorWindow(bool mouse)
        {
            _mouse = mouse;
            Title = (mouse ? "Mouse" : "Keyboard") + " mode — gamepad bindings";
            Width = 560;
            Height = 660;
            MinWidth = 560;
            MinHeight = 500;
            WindowStartupLocation = WindowStartupLocation.CenterScreen;
            ShowInTaskbar = false;

            var root = new DockPanel { Margin = new Thickness(10) };

            // ── top: profile selector + actions ──
            var profileBar = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 4) };
            profileBar.Children.Add(new TextBlock { Text = "Profile:", VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 6, 0) });
            profileBar.Children.Add(_profileBox);
            profileBar.Children.Add(MakeBarButton("Add", () => AddProfile(dup: false)));
            profileBar.Children.Add(MakeBarButton("Duplicate", () => AddProfile(dup: true)));
            profileBar.Children.Add(MakeBarButton("Delete", DeleteProfile));
            DockPanel.SetDock(profileBar, Dock.Top);
            root.Children.Add(profileBar);

            var nameBar = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 8) };
            nameBar.Children.Add(new TextBlock { Text = "Name:", VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 6, 0) });
            nameBar.Children.Add(_nameBox);
            _nameBox.LostFocus += (_, __) => CommitName();

            DockPanel.SetDock(nameBar, Dock.Top);
            root.Children.Add(nameBar);

            // Reset + Close row directly under the name field — a bottom bar didn't fit
            var actionRow = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 0, 0, 6) };
            var resetBtn = new Button { Content = "Reset to defaults", Padding = new Thickness(10, 3, 10, 3) };
            resetBtn.Click += (_, __) => ResetToDefaults();
            var closeBtn = new Button { Content = "Close", Padding = new Thickness(14, 3, 14, 3), Margin = new Thickness(8, 0, 0, 0) };
            closeBtn.Click += (_, __) => Close();
            actionRow.Children.Add(resetBtn);
            actionRow.Children.Add(closeBtn);
            DockPanel.SetDock(actionRow, Dock.Top);
            root.Children.Add(actionRow);

            // ── hint ──
            var hint = new TextBlock
            {
                Text = "Pick an action per button, or \"Pool for keyboard key...\" and press any key.",
                Foreground = Brushes.Gray,
                Margin = new Thickness(0, 0, 0, 6),
                TextWrapping = TextWrapping.Wrap
            };
            DockPanel.SetDock(hint, Dock.Top);
            root.Children.Add(hint);

            // ── scrolling rows ──
            var scroll = new ScrollViewer { VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Content = _rowsPanel };
            root.Children.Add(scroll);

            if (!_mouse)
            {
                // keyboard-mode only: stick center points editor shortcut
                var ptsBar = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 8, 0, 0) };
                var ptsBtn = new Button { Content = "Stick center points…", Padding = new Thickness(10, 3, 10, 3) };
                ptsBtn.Click += (_, __) => new StickPointsEditorWindow { Owner = this }.Show();
                ptsBar.Children.Add(ptsBtn);
                DockPanel.SetDock(ptsBar, Dock.Top);   // docked after scroll -> bottom strip
                root.Children.Add(ptsBar);
            }

            Content = root;

            _profileBox.SelectionChanged += (_, __) => LoadSelectedProfile();
            ReloadProfileList();
        }

        private static Button MakeBarButton(string label, Action onClick)
        {
            var b = new Button { Content = label, Padding = new Thickness(8, 2, 8, 2), Margin = new Thickness(6, 0, 0, 0) };
            b.Click += (_, __) => onClick();
            return b;
        }

        // ── profiles ─────────────────────────────────────────────────────────

        private List<KeyboardProfile> Kb => AppSettings.Instance.KeyboardProfiles;
        private List<MouseProfile> Mo => AppSettings.Instance.MouseProfiles;

        private int ActiveIndex
        {
            get => _mouse ? AppSettings.Instance.ActiveMouseProfile : AppSettings.Instance.ActiveProfile;
            set
            {
                if (_mouse) AppSettings.Instance.ActiveMouseProfile = value;
                else AppSettings.Instance.ActiveProfile = value;
            }
        }

        private string ProfileNameOf(int i) => _mouse ? Mo[i].Name : Kb[i].Name;

        private void ReloadProfileList()
        {
            _suppress++;
            _profileBox.Items.Clear();
            int count = _mouse ? Mo.Count : Kb.Count;
            for (int i = 0; i < count; i++)
                _profileBox.Items.Add(ProfileNameOf(i) + (i == ActiveIndex ? "  (active)" : ""));
            int idx = Math.Clamp(ActiveIndex, 0, Math.Max(0, count - 1));
            if (count > 0) _profileBox.SelectedIndex = idx;
            _suppress--;
            LoadSelectedProfile();
        }

        private void LoadSelectedProfile()
        {
            if (_suppress > 0) return;
            int idx = _profileBox.SelectedIndex;
            if (idx < 0) return;
            ActiveIndex = idx;
            _nameBox.Text = ProfileNameOf(idx);
            if (!_mouse) _stickProfileLabel.Text = "(stick points: " + ProfileNameOf(idx) + ")";

            BuildRows();
            AppSettings.Save();
            AppOrchestrator.NotifyMappingsChanged();
        }

        private void AddProfile(bool dup)
        {
            if (_mouse)
            {
                MouseProfile np;
                if (dup && ActiveIndex < Mo.Count)
                {
                    var json = JsonSerializer.Serialize(Mo[ActiveIndex]);
                    np = JsonSerializer.Deserialize<MouseProfile>(json) ?? new MouseProfile();
                    np.Name = np.Name + " copy";
                }
                else np = new MouseProfile { Name = "Mouse profile " + (Mo.Count + 1) };
                int insert = Math.Min(ActiveIndex + 1, Mo.Count);
                Mo.Insert(insert, np);
                ActiveIndex = insert;
            }
            else
            {
                KeyboardProfile np;
                if (dup && ActiveIndex < Kb.Count)
                {
                    var json = JsonSerializer.Serialize(Kb[ActiveIndex]);
                    np = JsonSerializer.Deserialize<KeyboardProfile>(json) ?? new KeyboardProfile();
                    np.Name = np.Name + " copy";
                }
                else np = new KeyboardProfile { Name = "Keyboard profile " + (Kb.Count + 1) };
                int insert = Math.Min(ActiveIndex + 1, Kb.Count);
                Kb.Insert(insert, np);
                ActiveIndex = insert;
            }
            AppSettings.Save();
            AppOrchestrator.NotifyMappingsChanged();
            ReloadProfileList();
        }

        private void DeleteProfile()
        {
            if ((_mouse ? Mo.Count : Kb.Count) <= 1) return;   // keep at least one
            int idx = ActiveIndex;
            if (_mouse) Mo.RemoveAt(idx); else Kb.RemoveAt(idx);
            ActiveIndex = Math.Clamp(idx - 1, 0, (_mouse ? Mo.Count : Kb.Count) - 1);
            AppSettings.Save();
            AppOrchestrator.NotifyMappingsChanged();
            ReloadProfileList();
        }

        private void CommitName()
        {
            int idx = ActiveIndex;
            string name = _nameBox.Text.Trim();
            if (string.IsNullOrEmpty(name) || idx < 0) return;
            if (_mouse) { if (Mo[idx].Name != name) { Mo[idx].Name = name; Persist(); } }
            else { if (Kb[idx].Name != name) { Kb[idx].Name = name; Persist(); } }
        }

        private void Persist()
        {
            AppSettings.Save();
            AppOrchestrator.NotifyMappingsChanged();
            _suppress++;
            int i = _profileBox.SelectedIndex;
            if (i >= 0)
            {
                _profileBox.Items[i] = ProfileNameOf(i) + (i == ActiveIndex ? "  (active)" : "");
                _profileBox.SelectedIndex = i;
            }
            _suppress--;
        }

        // ── rows ─────────────────────────────────────────────────────────────

        private void BuildRows()
        {
            _rows.Clear();
            _rowsPanel.Children.Clear();
            _comboPanel.Children.Clear();

            void Add(string label, Func<string> get, Action<string> set)
            {
                var row = new Row { Label = label, Get = get, Set = set, Last = get() };
                _rows.Add(row);
                _rowsPanel.Children.Add(MakeRow(row));
            }

            if (_mouse)
            {
                var p = Mo[Math.Clamp(ActiveIndex, 0, Mo.Count - 1)];
                Add("A", () => p.A, v => p.A = v);
                Add("B", () => p.B, v => p.B = v);
                Add("X", () => p.X, v => p.X = v);
                Add("Y", () => p.Y, v => p.Y = v);
                Add("LB", () => p.LB, v => p.LB = v);
                Add("RB", () => p.RB, v => p.RB = v);
                Add("LT (trigger)", () => p.LT, v => p.LT = v);
                Add("RT (trigger)", () => p.RT, v => p.RT = v);
                Add("LS (L3)", () => p.LS, v => p.LS = v);
                Add("RS (R3)", () => p.RS, v => p.RS = v);
                Add("View (Select)", () => p.View, v => p.View = v);
                Add("Menu (Options)", () => p.Menu, v => p.Menu = v);
                Add("D-pad Up", () => p.DUp, v => p.DUp = v);
                Add("D-pad Down", () => p.DDown, v => p.DDown = v);
                Add("D-pad Left", () => p.DLeft, v => p.DLeft = v);
                Add("D-pad Right", () => p.DRight, v => p.DRight = v);
                BuildComboSection(() => p.ComboBindings, v => p.ComboBindings = v);
            }
            else
            {
                var p = Kb[Math.Clamp(ActiveIndex, 0, Kb.Count - 1)];
                Add("A", () => p.A, v => p.A = v);
                Add("B", () => p.B, v => p.B = v);
                Add("X", () => p.X, v => p.X = v);
                Add("Y", () => p.Y, v => p.Y = v);
                Add("LB", () => p.LB, v => p.LB = v);
                Add("RB", () => p.RB, v => p.RB = v);
                Add("LT (trigger)", () => p.LT, v => p.LT = v);
                Add("RT (trigger)", () => p.RT, v => p.RT = v);
                Add("LS (L3)", () => p.LS, v => p.LS = v);
                Add("RS (R3)", () => p.RS, v => p.RS = v);
                Add("View (Select)", () => p.View, v => p.View = v);
                Add("Menu (Options)", () => p.Menu, v => p.Menu = v);
                Add("D-pad Up", () => p.DUp, v => p.DUp = v);
                Add("D-pad Down", () => p.DDown, v => p.DDown = v);
                Add("D-pad Left", () => p.DLeft, v => p.DLeft = v);
                Add("D-pad Right", () => p.DRight, v => p.DRight = v);
                BuildComboSection(() => p.ComboBindings, v => p.ComboBindings = v);
            }
        }

        private UIElement MakeRow(Row row)
        {
            var grid = new Grid { Margin = new Thickness(0, 2, 0, 2) };
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(150) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

            var label = new TextBlock { Text = row.Label, VerticalAlignment = VerticalAlignment.Center };
            Grid.SetColumn(label, 0);

            FillChoices(row.Box, row.Last);
            row.Box.SelectedIndex = IndexOfAction(row.Box, row.Last);
            row.Box.SelectionChanged += (_, __) => OnRowChanged(row);

            Grid.SetColumn(row.Box, 1);
            grid.Children.Add(label);
            grid.Children.Add(row.Box);
            return grid;
        }

        private static int IndexOfAction(ComboBox box, string action)
        {
            for (int i = 0; i < box.Items.Count; i++)
                if (Equals(box.Items[i], action)) return i;
            return -1;
        }

        private void FillChoices(ComboBox box, string currentValue)
        {
            box.Items.Clear();
            box.Items.Add("None");
            box.Items.Add(PoolItem);
            if (!string.IsNullOrEmpty(currentValue) && currentValue != "None")
                box.Items.Add(currentValue);   // custom value (e.g. Key:X) stays visible
            foreach (var a in ActionCatalog.All) box.Items.Add(a);
        }

        private void OnRowChanged(Row row)
        {
            if (_suppress > 0) return;
            var sel = row.Box.SelectedItem as string;
            if (sel == null) { row.Box.SelectedIndex = IndexOfAction(row.Box, row.Last); return; }

            if (sel == PoolItem)
            {
                _suppress++;
                var captured = KeyCaptureDialog.Capture("Press a keyboard key for [" + row.Label + "]...");
                if (captured != null)
                {
                    row.Set(captured);
                    row.Last = captured;
                    FillChoices(row.Box, captured);
                    row.Box.SelectedIndex = IndexOfAction(row.Box, captured);
                }
                else
                {
                    row.Box.SelectedIndex = IndexOfAction(row.Box, row.Last);
                }
                _suppress--;
                Persist();
                return;
            }

            row.Set(sel);
            row.Last = sel;
            if (IndexOfAction(row.Box, sel) < 0)
            {
                FillChoices(row.Box, sel);
                row.Box.SelectedIndex = IndexOfAction(row.Box, sel);
            }
            Persist();
        }

        // ── custom combos ────────────────────────────────────────────────────

        private void BuildComboSection(Func<List<string>> get, Action<List<string>> set)
        {
            var list = get();
            _comboPanel.Children.Add(new Separator { Margin = new Thickness(0, 10, 0, 6) });
            _comboPanel.Children.Add(new TextBlock
            {
                Text = "Custom combos — hold modifiers, last button press triggers:",
                FontWeight = FontWeights.SemiBold,
                Margin = new Thickness(0, 0, 0, 4)
            });

            for (int i = 0; i < list.Count; i++)
            {
                string entry = list[i];
                var row = new Grid { Margin = new Thickness(0, 2, 0, 2) };
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

                var label = new TextBlock { Text = FormatCombo(entry), VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis };
                Grid.SetColumn(label, 0);

                var del = new Button { Content = "✕", Padding = new Thickness(6, 0, 6, 0), Margin = new Thickness(6, 0, 0, 0) };
                string captured = entry;
                del.Click += (_, __) =>
                {
                    var l = get();
                    l.Remove(captured);
                    set(l);
                    Persist();
                    BuildRows();
                };
                Grid.SetColumn(del, 1);

                row.Children.Add(label);
                row.Children.Add(del);
                _comboPanel.Children.Add(row);
            }

            var addBtn = new Button
            {
                Content = "+ Add new custom binding",
                Padding = new Thickness(10, 3, 10, 3),
                HorizontalAlignment = HorizontalAlignment.Left,
                Margin = new Thickness(0, 4, 0, 0)
            };
            addBtn.Click += (_, __) => ShowComboBuilder(get, set);
            _comboPanel.Children.Add(addBtn);

            _rowsPanel.Children.Add(_comboPanel);
        }

        private static string ButtonLabel(string b) => b switch
        {
            "LT" => "LT (trigger)", "RT" => "RT (trigger)",
            "LS" => "L3", "RS" => "R3",
            "View" => "View/Select", "Menu" => "Menu/Options",
            "DUp" => "D-pad Up", "DDown" => "D-pad Down",
            "DLeft" => "D-pad Left", "DRight" => "D-pad Right",
            _ => b
        };

        private static string ButtonLabelShort(string b) => b switch
        {
            "LT" => "LT", "RT" => "RT", "LS" => "L3", "RS" => "R3",
            "View" => "View", "Menu" => "Menu",
            "DUp" => "↑", "DDown" => "↓", "DLeft" => "←", "DRight" => "→",
            _ => b
        };

        private static string FormatActionForList(string action) =>
            action.StartsWith("Key:", StringComparison.Ordinal) ? action[4..] + " (key)" : action;

        private static string FormatCombo(string entry)
        {
            int eq = entry.IndexOf('=');
            if (eq <= 0) return entry;
            string buttons = entry[..eq];
            string action = entry[(eq + 1)..];
            var parts = buttons.Split('+');
            string text = string.Join(" + ", parts.Select(ButtonLabelShort));
            return text + "  →  " + FormatActionForList(action);
        }

        private void ShowComboBuilder(Func<List<string>> get, Action<List<string>> set)
        {
            const string Empty = "(none)";

            var dlg = new Window
            {
                Title = "Add custom binding",
                Width = 640,
                SizeToContent = SizeToContent.Height,
                ResizeMode = ResizeMode.NoResize,
                WindowStartupLocation = WindowStartupLocation.CenterScreen,
                ShowInTaskbar = false
            };
            var root = new StackPanel { Margin = new Thickness(12) };
            root.Children.Add(new TextBlock
            {
                Text = "Fill 2–5 buttons: the earlier ones are held as modifiers, the LAST press triggers the action.",
                Foreground = Brushes.Gray,
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 0, 0, 8)
            });

            var panel = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Center };
            var boxes = new ComboBox[5];
            for (int i = 0; i < 5; i++)
            {
                var cb = new ComboBox { MinWidth = 86, Margin = new Thickness(3, 0, 3, 0) };
                cb.Items.Add(Empty);
                foreach (var b in ComboButtonCatalog.All) cb.Items.Add(b);
                cb.SelectedIndex = 0;
                boxes[i] = cb;
                panel.Children.Add(cb);
            }
            root.Children.Add(panel);

            root.Children.Add(new TextBlock { Text = "↓ action triggered by the last button:", Margin = new Thickness(0, 10, 0, 4) });
            var actionBox = new ComboBox { MinWidth = 300, HorizontalAlignment = HorizontalAlignment.Center };
            foreach (var a in ActionCatalog.All) actionBox.Items.Add(a);
            actionBox.Items.Add(PoolItem);
            actionBox.SelectedIndex = 0;
            root.Children.Add(actionBox);

            var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 12, 0, 0) };
            var cancel = new Button { Content = "Cancel", Padding = new Thickness(14, 3, 14, 3), Margin = new Thickness(0, 0, 8, 0) };
            var confirm = new Button { Content = "Confirm", Padding = new Thickness(14, 3, 14, 3) };
            buttons.Children.Add(cancel);
            buttons.Children.Add(confirm);
            root.Children.Add(buttons);
            dlg.Content = root;

            cancel.Click += (_, __) => dlg.Close();

            confirm.Click += (_, __) =>
            {
                string action = actionBox.SelectedItem as string ?? "";
                if (action == PoolItem)
                {
                    var cap = KeyCaptureDialog.Capture("Press a keyboard key for the combo...");
                    if (cap == null) return;
                    action = cap;
                }
                if (action == "") return;

                var picked = boxes.Select(b => b.SelectedItem as string ?? Empty)
                                  .Where(x => x != Empty)
                                  .ToList();
                if (picked.Count < 2) { MessageBox.Show(dlg, "Pick at least 2 buttons (modifier + trigger).", "Custom binding"); return; }

                string entry = string.Join("+", picked) + "=" + action;
                var l = get();
                if (!l.Contains(entry)) l.Add(entry);
                set(l);
                Persist();
                BuildRows();
                dlg.Close();
            };

            actionBox.SelectionChanged += (_, __) =>
            {
                if (Equals(actionBox.SelectedItem, PoolItem))
                {
                    var cap = KeyCaptureDialog.Capture("Press a keyboard key for the combo...");
                    _suppress++;
                    if (cap != null) { actionBox.Items[^1] = cap; actionBox.SelectedItem = cap; }
                    else actionBox.SelectedIndex = 0;
                    _suppress--;
                }
            };

            dlg.ShowDialog();
        }

        private void ResetToDefaults()
        {
            int idx = ActiveIndex;
            if (idx < 0) return;
            var mb = MessageBox.Show(this,
                "Reset ALL button bindings and custom combos of this profile to defaults?",
                "Reset to defaults", MessageBoxButton.OKCancel, MessageBoxImage.Question);
            if (mb != MessageBoxResult.OK) return;

            if (_mouse) { string name = Mo[idx].Name; var d = new MouseProfile(); d.Name = name; Mo[idx] = d; }
            else { string name = Kb[idx].Name; var d = new KeyboardProfile(); d.Name = name; Kb[idx] = d; }
            Persist();
            BuildRows();
        }
    }

    /// <summary>Buttons available for custom combos (Home excluded — enable combo).</summary>
    internal static class ComboButtonCatalog
    {
        public static readonly string[] All = { "A", "B", "X", "Y", "LB", "RB", "LT", "RT", "LS", "RS", "View", "Menu", "DUp", "DDown", "DLeft", "DRight" };
    }

    /// <summary>Full action vocabulary shown in the dropdowns.</summary>
    internal static class ActionCatalog
    {
        public static readonly string[] All = Build();

        private static string[] Build() => new[]
        {
            // modifiers (hold / toggle)
            "HoldShift", "HoldCtrl", "HoldAlt", "HoldWin",
            "ToggleShift", "ToggleCtrl", "ToggleAlt", "ToggleWin",
            // keyboard keys
            "Space", "Enter", "Backspace", "Tab", "Escape", "Delete", "Insert",
            "CapsLock", "NumLock",
            "ArrowUp", "ArrowDown", "ArrowLeft", "ArrowRight",
            "PageUp", "PageDown", "Home", "End",
            "F1", "F2", "F3", "F4", "F5", "F6", "F7", "F8", "F9", "F10", "F11", "F12",
            // mouse
            "LeftClick", "RightClick", "MiddleClick", "XButton1", "XButton2",
            "ScrollUp", "ScrollDown", "ScrollLeft", "ScrollRight", "SpeedBoost",
            // virtual keyboard control
            "CommitLeft", "CommitRight",
            // app control
            "DisableInput", "ToggleKeyboardMouseMode", "KeyboardMode", "MouseMode",
            "ToggleKeyboard", "ToggleLegend",
            "SwitchKeyboardProfile", "SwitchMouseProfile", "SwitchStickPointsProfile",
            // media
            "VolumeUp", "VolumeDown", "VolumeMute",
            "MediaPlayPause", "MediaNext", "MediaPrev",
        };
    }

    /// <summary>Modal "press any key" capture; returns "Key:<Name>" or null on Esc.</summary>
    internal static class KeyCaptureDialog
    {
        public static string? Capture(string prompt)
        {
            string? result = null;
            var win = new Window
            {
                Title = "Pool for keyboard key",
                WindowStyle = WindowStyle.None,
                ResizeMode = ResizeMode.NoResize,
                ShowInTaskbar = false,
                Topmost = true,
                SizeToContent = SizeToContent.WidthAndHeight,
                WindowStartupLocation = WindowStartupLocation.CenterScreen,
                Background = new SolidColorBrush(Color.FromRgb(0x22, 0x22, 0x26)),
                Content = new TextBlock
                {
                    Text = prompt + "\nEsc = cancel",
                    Foreground = Brushes.White,
                    FontSize = 15,
                    Margin = new Thickness(24, 18, 24, 18),
                    TextAlignment = TextAlignment.Center
                }
            };

            win.PreviewKeyDown += (_, e) =>
            {
                var key = e.Key == Key.System ? e.SystemKey : e.Key;
                if (key is Key.LeftCtrl or Key.RightCtrl or Key.LeftShift or Key.RightShift
                    or Key.LeftAlt or Key.RightAlt or Key.LWin or Key.RWin)
                {
                    e.Handled = true;
                    return;
                }
                if (key == Key.Escape) { win.DialogResult = false; e.Handled = true; return; }

                ushort vk = (ushort)KeyInterop.VirtualKeyFromKey(key);
                string? name = VkName(vk);
                if (name == null) { e.Handled = true; return; }   // unsupported: keep waiting
                result = "Key:" + name;
                win.DialogResult = true;
                e.Handled = true;
            };

            win.ShowDialog();
            return result;
        }

        private static string? VkName(ushort vk)
        {
            if (vk >= 0x41 && vk <= 0x5A) return ((char)vk).ToString();
            if (vk >= 0x30 && vk <= 0x39) return ((char)vk).ToString();
            if (vk >= 0x70 && vk <= 0x7B) return "F" + (vk - 0x70 + 1);
            if (vk >= 0x60 && vk <= 0x69) return "NumPad" + (vk - 0x60);
            return vk switch
            {
                0xBA => ";", 0xBB => "=", 0xBC => ",", 0xBD => "-", 0xBE => ".", 0xBF => "/",
                0xC0 => "`", 0xDB => "[", 0xDC => "\\", 0xDD => "]", 0xDE => "'",
                0x08 => "Backspace",
                0x09 => "Tab",
                0x0D => "Enter",
                0x14 => "CapsLock",
                0x1B => "Escape",
                0x20 => "Space",
                0x21 => "PageUp",
                0x22 => "PageDown",
                0x23 => "End",
                0x24 => "Home",
                0x25 => "ArrowLeft",
                0x26 => "ArrowUp",
                0x27 => "ArrowRight",
                0x28 => "ArrowDown",
                0x2D => "Insert",
                0x2E => "Delete",
                _ => null
            };
        }
    }
}