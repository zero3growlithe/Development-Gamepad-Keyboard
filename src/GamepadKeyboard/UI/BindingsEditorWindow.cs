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

            // ── bottom: close ──
            var bottom = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 8, 0, 0) };
            var close = new Button { Content = "Close", Padding = new Thickness(16, 4, 16, 4) };
            close.Click += (_, __) => Close();
            bottom.Children.Add(close);
            DockPanel.SetDock(bottom, Dock.Top);   // added last, dock order matters
            root.Children.Add(bottom);

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
                Add("Y+D-pad Up", () => p.YDUp, v => p.YDUp = v);
                Add("Y+D-pad Down", () => p.YDDown, v => p.YDDown = v);
                Add("Y+D-pad Left", () => p.YDLeft, v => p.YDLeft = v);
                Add("Y+D-pad Right", () => p.YDRight, v => p.YDRight = v);
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
            for (int i = 1; i < box.Items.Count; i++)
                if (Equals(box.Items[i], action)) return i;
            return -1;
        }

        private void FillChoices(ComboBox box, string currentValue)
        {
            box.Items.Clear();
            box.Items.Add(PoolItem);
            if (!string.IsNullOrEmpty(currentValue))
                box.Items.Add(currentValue);   // custom value (e.g. Key:X) stays visible at index 1
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
            "SwitchKeyboardProfile", "SwitchMouseProfile",
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
            return vk switch
            {
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