using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
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
        private const string BindingDragFormat = "GamepadKeyboard.ProfileBindingId";

        private readonly bool _mouse;
        private readonly ComboBox _profileBox = new() { MinWidth = 170 };
        private readonly TextBox _nameBox = new() { MinWidth = 170 };
        private readonly StackPanel _rowsPanel = new();
        private readonly System.Windows.Controls.TextBlock _stickProfileLabel = new()
        {
            Text = "",
            Foreground = System.Windows.Media.Brushes.Gray,
            VerticalAlignment = System.Windows.VerticalAlignment.Center,
            Margin = new System.Windows.Thickness(8, 0, 10, 0)
        };

        private int _suppress = 0;

        public BindingsEditorWindow(bool mouse)
        {
            _mouse = mouse;
            Title = (mouse ? "Mouse" : "Keyboard") + " mode — gamepad bindings";
            Width = 700;
            Height = 660;
            MinWidth = 650;
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
            if (!_mouse)
            {
                var pointsBtn = new Button { Content = "Stick center points…", Padding = new Thickness(10, 3, 10, 3) };
                pointsBtn.Click += (_, __) =>
                {
                    var editor = new StickPointsEditorWindow { Owner = this };
                    editor.Closed += (_, __) => UpdateStickProfileLabel();
                    editor.Show();
                };
                actionRow.Children.Add(pointsBtn);
                actionRow.Children.Add(_stickProfileLabel);
            }
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
                Text = "Bindings run from top to bottom. Double-click to edit, or drag a binding to rearrange it.",
                Foreground = Brushes.Gray,
                Margin = new Thickness(0, 0, 0, 6),
                TextWrapping = TextWrapping.Wrap
            };
            DockPanel.SetDock(hint, Dock.Top);
            root.Children.Add(hint);

            // ── scrolling rows ──
            var scroll = new ScrollViewer { VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Content = _rowsPanel };
            root.Children.Add(scroll);

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
            UpdateStickProfileLabel();

            BuildBindingList();
            AppSettings.Save();
            AppOrchestrator.NotifyMappingsChanged();
        }

        private void UpdateStickProfileLabel()
        {
            if (!_mouse)
                _stickProfileLabel.Text = "Active: " + AppSettings.Instance.StickPointsProfile.Name;
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
                    foreach (var binding in np.Bindings)
                        binding.Id = Guid.NewGuid().ToString("N");
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
                    foreach (var binding in np.Bindings)
                        binding.Id = Guid.NewGuid().ToString("N");
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

        // ── ordered bindings ─────────────────────────────────────────────────

        private List<ProfileBinding> ActiveBindings => _mouse
            ? Mo[Math.Clamp(ActiveIndex, 0, Mo.Count - 1)].Bindings
            : Kb[Math.Clamp(ActiveIndex, 0, Kb.Count - 1)].Bindings;

        private void BuildBindingList()
        {
            _rowsPanel.Children.Clear();
            var list = ActiveBindings;
            for (int i = 0; i < list.Count; i++)
                _rowsPanel.Children.Add(MakeBindingRow(list[i]));

            var add = new Button
            {
                Content = "+ Add binding",
                Padding = new Thickness(12, 4, 12, 4),
                HorizontalAlignment = HorizontalAlignment.Left,
                Margin = new Thickness(0, 8, 0, 6)
            };
            add.Click += (_, __) => ShowBindingEditor(null);
            _rowsPanel.Children.Add(add);
        }

        private UIElement MakeBindingRow(ProfileBinding binding)
        {
            string capturedId = binding.Id;
            Point? dragStart = null;
            var border = new Border
            {
                BorderBrush = new SolidColorBrush(Color.FromRgb(0xD0, 0xD0, 0xD0)),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(4),
                Padding = new Thickness(8, 5, 6, 5),
                Margin = new Thickness(0, 0, 0, 5),
                AllowDrop = true
            };
            var grid = new Grid();
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            var text = new StackPanel();
            text.Children.Add(new TextBlock
            {
                Text = FormatBinding(binding),
                FontWeight = FontWeights.SemiBold,
                TextTrimming = TextTrimming.CharacterEllipsis
            });
            string flags = FormatFlags(binding);
            if (flags.Length > 0)
                text.Children.Add(new TextBlock { Text = flags, Foreground = Brushes.DimGray, FontSize = 11 });

            var controls = new StackPanel { Orientation = Orientation.Horizontal };
            if (binding.Buttons.Count == 1)
            {
                var modifier = new CheckBox
                {
                    Content = "Modifier",
                    IsChecked = binding.Modifier,
                    VerticalAlignment = VerticalAlignment.Center,
                    Margin = new Thickness(4, 0, 4, 0),
                    ToolTip = "Run the standalone action on release only when this input was not used with another input."
                };
                modifier.Click += (_, __) =>
                {
                    var current = ActiveBindings.FirstOrDefault(x => x.Id == capturedId);
                    if (current == null) return;
                    current.Modifier = modifier.IsChecked == true;
                    Persist();
                    BuildBindingList();
                };
                controls.Children.Add(modifier);
            }
            controls.Children.Add(RowButton("↑", () => MoveBinding(capturedId, -1)));
            controls.Children.Add(RowButton("↓", () => MoveBinding(capturedId, +1)));
            controls.Children.Add(RowButton("Edit", () => ShowBindingEditor(ActiveBindings.FirstOrDefault(x => x.Id == capturedId))));
            controls.Children.Add(RowButton("Duplicate", () => DuplicateBinding(capturedId)));
            controls.Children.Add(RowButton("✕", () => DeleteBinding(capturedId)));
            Grid.SetColumn(controls, 1);
            grid.Children.Add(text);
            grid.Children.Add(controls);
            border.Child = grid;

            border.PreviewMouseLeftButtonDown += (_, e) =>
            {
                if (IsRowControl(e.OriginalSource as DependencyObject, border))
                {
                    dragStart = null;
                    return;
                }

                if (e.ClickCount == 2)
                {
                    dragStart = null;
                    ShowBindingEditor(ActiveBindings.FirstOrDefault(x => x.Id == capturedId));
                    e.Handled = true;
                    return;
                }

                dragStart = e.GetPosition(border);
            };
            border.PreviewMouseMove += (_, e) =>
            {
                if (!dragStart.HasValue || e.LeftButton != MouseButtonState.Pressed)
                {
                    dragStart = null;
                    return;
                }

                Point current = e.GetPosition(border);
                if (Math.Abs(current.X - dragStart.Value.X) < SystemParameters.MinimumHorizontalDragDistance
                    && Math.Abs(current.Y - dragStart.Value.Y) < SystemParameters.MinimumVerticalDragDistance)
                    return;

                dragStart = null;
                var data = new DataObject(BindingDragFormat, capturedId);
                DragDrop.DoDragDrop(border, data, DragDropEffects.Move);
            };
            border.DragOver += (_, e) =>
            {
                string? draggedId = e.Data.GetData(BindingDragFormat) as string;
                e.Effects = !string.IsNullOrEmpty(draggedId) && draggedId != capturedId
                    ? DragDropEffects.Move
                    : DragDropEffects.None;
                e.Handled = true;
            };
            border.Drop += (_, e) =>
            {
                string? draggedId = e.Data.GetData(BindingDragFormat) as string;
                if (string.IsNullOrEmpty(draggedId) || draggedId == capturedId) return;
                bool insertAfter = e.GetPosition(border).Y >= border.ActualHeight / 2;
                ReorderBinding(draggedId, capturedId, insertAfter);
                e.Handled = true;
            };
            return border;
        }

        private static bool IsRowControl(DependencyObject? source, Border row)
        {
            for (DependencyObject? current = source;
                 current != null && current != row;
                 current = VisualTreeHelper.GetParent(current))
            {
                if (current is ButtonBase) return true;
            }
            return false;
        }

        private static Button RowButton(string text, Action action)
        {
            var button = new Button { Content = text, Padding = new Thickness(6, 1, 6, 1), Margin = new Thickness(4, 0, 0, 0) };
            button.Click += (_, __) => action();
            return button;
        }

        private void MoveBinding(string id, int direction)
        {
            var list = ActiveBindings;
            int from = list.FindIndex(x => x.Id == id);
            int to = from + direction;
            if (from < 0 || to < 0 || to >= list.Count) return;
            (list[from], list[to]) = (list[to], list[from]);
            Persist();
            BuildBindingList();
        }

        private void ReorderBinding(string draggedId, string targetId, bool insertAfter)
        {
            var list = ActiveBindings;
            int from = list.FindIndex(x => x.Id == draggedId);
            int target = list.FindIndex(x => x.Id == targetId);
            if (from < 0 || target < 0) return;

            int insertionIndex = target + (insertAfter ? 1 : 0);
            ProfileBinding binding = list[from];
            list.RemoveAt(from);
            if (from < insertionIndex) insertionIndex--;
            insertionIndex = Math.Clamp(insertionIndex, 0, list.Count);
            if (insertionIndex == from)
            {
                list.Insert(from, binding);
                return;
            }

            list.Insert(insertionIndex, binding);
            Persist();
            BuildBindingList();
        }

        private void DuplicateBinding(string id)
        {
            var list = ActiveBindings;
            int index = list.FindIndex(x => x.Id == id);
            if (index < 0) return;
            list.Insert(index + 1, CloneBinding(list[index]));
            Persist();
            BuildBindingList();
        }

        private void DeleteBinding(string id)
        {
            ActiveBindings.RemoveAll(x => x.Id == id);
            Persist();
            BuildBindingList();
        }

        private static string ButtonLabelShort(string b) => b switch
        {
            "LT" => "LT", "RT" => "RT", "LS" => "L3", "RS" => "R3",
            "View" => "View", "Menu" => "Menu",
            "DUp" => "↑", "DDown" => "↓", "DLeft" => "←", "DRight" => "→",
            "LUp" => "Left stick ↑", "LDown" => "Left stick ↓",
            "LLeft" => "Left stick ←", "LRight" => "Left stick →",
            "RUp" => "Right stick ↑", "RDown" => "Right stick ↓",
            "RLeft" => "Right stick ←", "RRight" => "Right stick →",
            _ => b
        };

        private static string FormatActionForList(string action) =>
            action.StartsWith("Key:", StringComparison.Ordinal) ? action[4..] + " (key)" : action;

        private static string FormatBinding(ProfileBinding entry)
        {
            string text = string.Join(" + ", entry.Buttons.Select(ButtonLabelShort));
            var modifiers = new List<string>();
            if (entry.Ctrl) modifiers.Add("Ctrl");
            if (entry.Shift) modifiers.Add("Shift");
            if (entry.Alt) modifiers.Add("Alt");
            string action = (modifiers.Count > 0 ? string.Join("+", modifiers) + "+" : "")
                + FormatActionForList(entry.Action);
            return text + "  →  " + action;
        }

        private static string FormatFlags(ProfileBinding entry)
        {
            var flags = new List<string>();
            if (entry.Modifier) flags.Add("modifier / standalone action on release");
            if (entry.HoldLast) flags.Add("hold action with final input");
            return string.Join("  •  ", flags);
        }

        private static ProfileBinding CloneBinding(ProfileBinding source) => new()
        {
            Buttons = source.Buttons.ToList(),
            Action = source.Action,
            Modifier = source.Modifier,
            HoldLast = source.HoldLast,
            Ctrl = source.Ctrl,
            Shift = source.Shift,
            Alt = source.Alt
        };

        private void ShowBindingEditor(ProfileBinding? existingEntry)
        {
            const string Empty = "(none)";

            string[] existingButtons = existingEntry?.Buttons.ToArray() ?? Array.Empty<string>();
            string existingAction = existingEntry?.Action ?? "";

            var dlg = new Window
            {
                Title = existingEntry == null ? "Add binding" : "Edit binding",
                Width = 780,
                SizeToContent = SizeToContent.Height,
                ResizeMode = ResizeMode.NoResize,
                WindowStartupLocation = WindowStartupLocation.CenterScreen,
                ShowInTaskbar = false
            };
            var root = new StackPanel { Margin = new Thickness(12) };
            root.Children.Add(new TextBlock
            {
                Text = "Choose 1–5 inputs. With multiple inputs, hold the earlier ones and press the final input to trigger the action.",
                Foreground = Brushes.Gray,
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 0, 0, 8)
            });

            var panel = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Center };
            var boxes = new ComboBox[5];
            for (int i = 0; i < 5; i++)
            {
                var cb = new ComboBox { MinWidth = 138, Margin = new Thickness(3, 0, 3, 0) };
                cb.Items.Add(Empty);
                foreach (var b in BindingInputCatalog.All) cb.Items.Add(BindingInputCatalog.Display(b));
                cb.SelectedItem = i < existingButtons.Length
                    ? BindingInputCatalog.Display(existingButtons[i])
                    : Empty;
                boxes[i] = cb;
                panel.Children.Add(cb);
            }
            root.Children.Add(panel);

            root.Children.Add(new TextBlock { Text = "↓ action triggered by the last button:", Margin = new Thickness(0, 10, 0, 4) });
            var actionBox = new ComboBox { MinWidth = 300, HorizontalAlignment = HorizontalAlignment.Center };
            foreach (var a in ActionCatalog.All) actionBox.Items.Add(a);
            if (existingAction.Length > 0 && !actionBox.Items.Contains(existingAction))
                actionBox.Items.Add(existingAction);
            actionBox.Items.Add(PoolItem);
            actionBox.SelectedItem = existingAction.Length > 0 ? existingAction : ActionCatalog.All[0];
            root.Children.Add(actionBox);

            var options = new WrapPanel { Margin = new Thickness(0, 10, 0, 0) };
            var ctrl = new CheckBox { Content = "Ctrl", IsChecked = existingEntry?.Ctrl == true, Margin = new Thickness(0, 0, 14, 0) };
            var shift = new CheckBox { Content = "Shift", IsChecked = existingEntry?.Shift == true, Margin = new Thickness(0, 0, 14, 0) };
            var alt = new CheckBox { Content = "Alt", IsChecked = existingEntry?.Alt == true, Margin = new Thickness(0, 0, 14, 0) };
            var holdLast = new CheckBox
            {
                Content = "Hold action while the last gamepad button is held",
                IsChecked = existingEntry?.HoldLast == true
            };
            options.Children.Add(ctrl);
            options.Children.Add(shift);
            options.Children.Add(alt);
            options.Children.Add(holdLast);
            var modifier = new CheckBox
            {
                Content = "Modifier (single input: run on release only when unused by another binding)",
                IsChecked = existingEntry?.Modifier == true,
                Margin = new Thickness(0, 6, 0, 0)
            };
            root.Children.Add(options);
            root.Children.Add(modifier);
            root.Children.Add(new TextBlock
            {
                Text = "Selected keyboard modifiers stay down until a gamepad modifier button in this binding is released.",
                Foreground = Brushes.Gray,
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 4, 0, 0)
            });

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
                    var cap = KeyCaptureDialog.Capture("Press a keyboard key for the binding...");
                    if (cap == null) return;
                    action = cap;
                }
                if (action == "") return;

                var picked = boxes.Select(b => b.SelectedItem as string ?? Empty)
                                  .Where(x => x != Empty)
                                  .Select(BindingInputCatalog.IdFromDisplay)
                                  .Distinct(StringComparer.Ordinal)
                                  .ToList();
                if (picked.Count < 1) { MessageBox.Show(dlg, "Pick at least one gamepad input.", "Binding"); return; }

                bool combo = picked.Count > 1;

                if (existingEntry != null)
                {
                    existingEntry.Buttons = picked;
                    existingEntry.Action = action;
                    existingEntry.Ctrl = combo && ctrl.IsChecked == true;
                    existingEntry.Shift = combo && shift.IsChecked == true;
                    existingEntry.Alt = combo && alt.IsChecked == true;
                    existingEntry.HoldLast = combo && holdLast.IsChecked == true;
                    existingEntry.Modifier = !combo && modifier.IsChecked == true;
                }
                else
                {
                    ActiveBindings.Add(new ProfileBinding
                    {
                        Buttons = picked,
                        Action = action,
                        Ctrl = combo && ctrl.IsChecked == true,
                        Shift = combo && shift.IsChecked == true,
                        Alt = combo && alt.IsChecked == true,
                        HoldLast = combo && holdLast.IsChecked == true,
                        Modifier = !combo && modifier.IsChecked == true
                    });
                }
                Persist();
                BuildBindingList();
                dlg.Close();
            };

            actionBox.SelectionChanged += (_, __) =>
            {
                if (Equals(actionBox.SelectedItem, PoolItem))
                {
                    var cap = KeyCaptureDialog.Capture("Press a keyboard key for the binding...");
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
                "Reset every binding in this profile to defaults?",
                "Reset to defaults", MessageBoxButton.OKCancel, MessageBoxImage.Question);
            if (mb != MessageBoxResult.OK) return;

            if (_mouse) { string name = Mo[idx].Name; var d = new MouseProfile(); d.Name = name; Mo[idx] = d; }
            else { string name = Kb[idx].Name; var d = new KeyboardProfile(); d.Name = name; Kb[idx] = d; }
            Persist();
            BuildBindingList();
        }
    }

    /// <summary>Inputs available for ordered, removable profile bindings.</summary>
    internal static class BindingInputCatalog
    {
        public static readonly string[] All =
        {
            "A", "B", "X", "Y", "LB", "RB", "LT", "RT", "LS", "RS",
            "View", "Menu", "Home", "DUp", "DDown", "DLeft", "DRight",
            "LUp", "LDown", "LLeft", "LRight", "RUp", "RDown", "RLeft", "RRight"
        };

        public static string Display(string id) => id switch
        {
            "LS" => "L3", "RS" => "R3",
            "View" => "View / Select", "Menu" => "Menu / Options",
            "DUp" => "D-pad Up", "DDown" => "D-pad Down",
            "DLeft" => "D-pad Left", "DRight" => "D-pad Right",
            "LUp" => "Left stick Up", "LDown" => "Left stick Down",
            "LLeft" => "Left stick Left", "LRight" => "Left stick Right",
            "RUp" => "Right stick Up", "RDown" => "Right stick Down",
            "RLeft" => "Right stick Left", "RRight" => "Right stick Right",
            _ => id
        };

        public static string IdFromDisplay(string display)
        {
            foreach (string id in All)
                if (Display(id) == display) return id;
            return display;
        }
    }

    /// <summary>Full action vocabulary shown in the dropdowns.</summary>
    internal static class ActionCatalog
    {
        public static readonly string[] All = Build();

        private static string[] Build() => new[]
        {
            "None",
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
            "MouseMoveUp", "MouseMoveDown", "MouseMoveLeft", "MouseMoveRight",
            "AnalogScrollUp", "AnalogScrollDown", "AnalogScrollLeft", "AnalogScrollRight",
            // virtual keyboard control
            "SubmitLeft", "SubmitRight",
            "MoveLeftCursorUp", "MoveLeftCursorDown", "MoveLeftCursorLeft", "MoveLeftCursorRight",
            "MoveRightCursorUp", "MoveRightCursorDown", "MoveRightCursorLeft", "MoveRightCursorRight",
            // app control
            "EnableInput", "DisableInput", "ToggleInput",
            "ToggleKeyboardMouseMode", "KeyboardMode", "MouseMode",
            "ToggleMoveScaleKeyboard",
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
