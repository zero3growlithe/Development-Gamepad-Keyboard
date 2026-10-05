using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace GamepadKeyboard.UI
{
    /// <summary>
    /// Simple settings window (non-modal, singleton). Covers the core knobs:
    /// key spacing, ray scales, mouse speed, legend, startup behavior.
    /// </summary>
    public sealed class SettingsWindow : Window
    {
        private static SettingsWindow? _instance;
        public static void ShowSingleton()
        {
            if (_instance == null)
            {
                _instance = new SettingsWindow();
                _instance.Closed += (_, __) => _instance = null;
                _instance.Show();
            }
            else
            {
                _instance.Activate();
            }
        }

        private readonly TextBox _spacing = new() { Text = "" };
        private readonly TextBox _keyboardMoveSpeed = new() { Text = "" };
        private readonly TextBox _mouseSpeed = new() { Text = "" };
        private readonly TextBox _boost = new() { Text = "" };
        private readonly TextBox _scroll = new() { Text = "" };
        private readonly TextBox _deadzone = new() { Text = "" };
        private readonly TextBox _mouseDeadzone = new() { Text = "" };
        private readonly TextBox _curveExponent = new()
        {
            Text = "",
            ToolTip = "Below 1 responds quickly near the center; 1 is linear; above 1 starts gently and rises toward the edge."
        };
        private readonly CheckBox _cursorLag = new() { Content = "Enable cursor lag" };
        private readonly TextBox _cursorLagSeconds = new() { Text = "" };
        private readonly CheckBox _hideLagRays = new() { Content = "Hide rays in cursor lag mode" };
        private readonly CheckBox _freeCursor = new() { Content = "Enable free cursor" };
        private readonly TextBox _freeCursorSpeed = new() { Text = "" };
        private readonly CheckBox _hideCentersAndRays = new() { Content = "Hide center points and rays in free cursor mode" };
        private readonly CheckBox _legend = new() { Content = "Show button legend overlay" };
        private readonly CheckBox _showAtCursor = new() { Content = "Always show keyboard at cursor position" };
        private readonly CheckBox _toastPermanent = new() { Content = "Keep profile toast permanently visible" };
        private readonly CheckBox _runAdmin = new() { Content = "Run as Administrator every launch (restarts with UAC when needed)" };
        private readonly CheckBox _startup = new() { Content = "Run on Windows startup" };
        private readonly CheckBox _startMouse = new() { Content = "Start in mouse mode" };
        private readonly CheckBox _hidHideSession = new() { Content = "Reserve selected controllers while input is enabled" };
        private readonly CheckBox _hidHideLegacy = new() { Content = "Allow legacy persistent-list fallback when session claims are unsupported" };
        private readonly TextBlock _hidHideSelection = new() { VerticalAlignment = VerticalAlignment.Center };
        private List<string> _hidHidePaths = new();
        private readonly List<Action> _numericValidators = new();
        private readonly Dictionary<string, Slider> _keyMapsSliders = new();      // layout property → slider
        private readonly Dictionary<string, TextBlock> _keyMapsValueLabels = new();
        private readonly Dictionary<string, object> _keyMapsLayoutBackup = new();
        private bool _keyMapsSettingsSavedExplicitly;
        private bool _suppressKeyMapsLiveApply;   // while programmatically initializing sliders

        public SettingsWindow()
        {
            Title = "Development Gamepad Keyboard — Settings";
            Width = 680;
            Height = 650;
            MinWidth = 620;
            MinHeight = 520;
            ResizeMode = ResizeMode.CanResize;
            WindowStartupLocation = WindowStartupLocation.CenterScreen;

            var s = Settings.AppSettings.Instance;
            _spacing.Text = s.KeySpacing.ToString("0.#");
            _keyboardMoveSpeed.Text = s.OverlayMoveSpeed.ToString("0.#");
            _mouseSpeed.Text = s.MouseSpeed.ToString("0.#");
            _boost.Text = s.MouseSpeedBoostMultiplier.ToString("0.##");
            _scroll.Text = s.ScrollSpeed.ToString("0.#");
            _deadzone.Text = s.StickDeadzone.ToString("0.###");
            _mouseDeadzone.Text = s.MouseStickDeadzone.ToString("0.###");
            _curveExponent.Text = s.AnalogStickCurveExponent.ToString("0.##");
            _cursorLag.IsChecked = s.CursorLagEnabled;
            _cursorLagSeconds.Text = s.CursorLagSeconds.ToString("0.###");
            _hideLagRays.IsChecked = s.HideRaysInCursorLag;
            _freeCursor.IsChecked = s.FreeCursorEnabled;
            _freeCursorSpeed.Text = s.FreeCursorSpeed.ToString("0.#");
            _hideCentersAndRays.IsChecked = s.HideCenterPointsAndRaysInFreeCursor;
            _legend.IsChecked = s.ShowButtonLegend;
            _showAtCursor.IsChecked = s.AlwaysShowKeyboardAtCursorPosition;
            _toastPermanent.IsChecked = s.ProfileToastPermanent;
            _runAdmin.IsChecked = s.AdminLaunch;
            _startup.IsChecked = s.RunOnStartup;
            _startMouse.IsChecked = s.StartInMouseMode;
            _hidHideSession.IsChecked = s.HidHideSessionEnabled;
            _hidHideLegacy.IsChecked = s.HidHideLegacyFallbackEnabled;
            _hidHidePaths = s.HidHideDeviceInstancePaths.ToList();
            UpdateHidHideSelectionText();
            BackupKeyMapsLayout(s.KeyMaps.Layout);

            ConfigureNumericValidation(_spacing, value => value >= 0, "0.#");
            ConfigureNumericValidation(_keyboardMoveSpeed, value => value > 0, "0.#");
            ConfigureNumericValidation(_mouseSpeed, value => value > 0, "0.#");
            ConfigureNumericValidation(_boost, value => value >= 1, "0.##");
            ConfigureNumericValidation(_scroll, value => value > 0, "0.#");
            ConfigureNumericValidation(_deadzone, value => value is >= 0 and <= 0.5, "0.###");
            ConfigureNumericValidation(_mouseDeadzone, value => value is >= 0 and <= 0.5, "0.###");
            ConfigureNumericValidation(_curveExponent, value => value is >= 0.1 and <= 5, "0.##");
            ConfigureNumericValidation(_cursorLagSeconds, value => value >= 0, "0.###");
            ConfigureNumericValidation(_freeCursorSpeed, value => value > 0, "0.#");

            // Binding editors stay outside the tabs so they are always visible.
            var editors = new WrapPanel { Margin = new Thickness(12, 10, 12, 8) };
            var kbBtn = new Button { Content = "Gamepad bindings (keyboard mode)…", Padding = new Thickness(10, 3, 10, 3), Margin = new Thickness(0, 0, 8, 0) };
            kbBtn.Click += (_, __) => new BindingsEditorWindow(mouse: false).Show();
            var moBtn = new Button { Content = "Gamepad bindings (mouse mode)…", Padding = new Thickness(10, 3, 10, 3) };
            moBtn.Click += (_, __) => new BindingsEditorWindow(mouse: true).Show();
            editors.Children.Add(kbBtn);
            editors.Children.Add(moBtn);

            var tabs = new TabControl { Margin = new Thickness(12, 0, 12, 0) };
            tabs.Items.Add(new TabItem
            {
                Header = "Keyboard",
                Content = new ScrollViewer
                {
                    Content = MakeSettingsForm(BuildKeyboardTabRows()),
                    VerticalScrollBarVisibility = ScrollBarVisibility.Auto
                }
            });
            InitializeKeyMapsLayoutEditors(s);
            tabs.Items.Add(new TabItem
            {
                Header = "Mouse",
                Content = MakeSettingsForm(
                    ("Mouse speed (px per stick unit):", _mouseSpeed),
                    ("Mouse speed boost multiplier:", _boost),
                    ("Scroll speed:", _scroll),
                    ("Stick deadzone (0.000–0.5):", _mouseDeadzone),
                    ("Analog sensitivity curve (0.1–5; 1 = linear):", _curveExponent))
            });
            tabs.Items.Add(new TabItem
            {
                Header = "Keyboard cursor",
                Content = MakeSettingsForm(
                    ("", _cursorLag),
                    ("Cursor lag (seconds; 0 = instant):", _cursorLagSeconds),
                    ("", _hideLagRays),
                    ("", _freeCursor),
                    ("Free cursor speed (px/second):", _freeCursorSpeed),
                    ("", _hideCentersAndRays))
            });
            tabs.Items.Add(new TabItem
            {
                Header = "Interface & startup",
                Content = MakeSettingsForm(
                    ("", _showAtCursor),
                    ("", _legend),
                    ("", _toastPermanent),
                    ("", _startMouse),
                    ("", _runAdmin),
                    ("", _startup))
            });
            tabs.Items.Add(new TabItem
            {
                Header = "HidHide",
                Content = new ScrollViewer
                {
                    Content = BuildHidHideSection(),
                    VerticalScrollBarVisibility = ScrollBarVisibility.Auto
                }
            });

            var buttons = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                HorizontalAlignment = HorizontalAlignment.Right,
                Margin = new Thickness(0, 12, 0, 0)
            };
            var ok = new Button { Content = "OK", Padding = new Thickness(16, 4, 16, 4), Margin = new Thickness(0, 0, 8, 0) };
            ok.Click += (_, __) => { Save(); _keyMapsSettingsSavedExplicitly = true; Close(); };
            var cancel = new Button { Content = "Cancel", Padding = new Thickness(16, 4, 16, 4) };
            cancel.Click += (_, __) => Close();   // OnClosed restores the pre-open layout
            buttons.Children.Add(ok);
            buttons.Children.Add(cancel);

            buttons.Margin = new Thickness(12);
            var outer = new DockPanel();
            DockPanel.SetDock(editors, Dock.Top);
            DockPanel.SetDock(buttons, Dock.Bottom);
            outer.Children.Add(editors);
            outer.Children.Add(buttons);
            outer.Children.Add(tabs);
            Content = outer;
        }

        private static FrameworkElement MakeSettingsForm(params (string label, FrameworkElement editor)[] rows)
        {
            var grid = new Grid { Margin = new Thickness(12) };
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(300) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            for (int i = 0; i < rows.Length; i++)
            {
                grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
                var label = new TextBlock
                {
                    Text = rows[i].label,
                    VerticalAlignment = VerticalAlignment.Center,
                    Margin = new Thickness(0, 5, 10, 5)
                };
                if (rows[i].editor == null)
                {
                    label.FontWeight = FontWeights.SemiBold;
                    label.Margin = new Thickness(0, 12, 10, 5);
                    label.TextAlignment = TextAlignment.Center;
                    Grid.SetRow(label, i);
                    Grid.SetColumnSpan(label, 2);
                    grid.Children.Add(label);
                    continue;
                }
                rows[i].editor.Margin = new Thickness(0, 5, 0, 5);
                Grid.SetRow(label, i);
                Grid.SetColumn(rows[i].editor, 1);
                Grid.SetRow(rows[i].editor, i);
                grid.Children.Add(label);
                grid.Children.Add(rows[i].editor);
            }
            return new ScrollViewer
            {
                Content = grid,
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled
            };
        }

        /// <summary>Keyboard tab rows: the classic keyboard settings plus the
        /// slider-only Key Maps layout section.</summary>
        private (string, FrameworkElement)[] BuildKeyboardTabRows()
        {
            List<(string, FrameworkElement)> rows = new()
            {
                ("Key spacing (px gap between keys):", _spacing),
                ("Keyboard move speed:", _keyboardMoveSpeed),
                ("Stick deadzone (0.000–0.5):", _deadzone),
                ("── Key Maps layout ──", null!),
            };
            rows.AddRange(BuildKeyMapsLayoutRows());
            return rows.ToArray();
        }

        /// <summary>Key Maps layout editors — every option a slider (offsets
        /// relative to the board position, spreads, hub positions, hub sizes
        /// and the global key size), all live-applied while dragging.</summary>
        private (string, FrameworkElement)[] BuildKeyMapsLayoutRows()
        {
            (string key, string label)[] wheels =
            {
                ("DPad", "D-Pad"),
                ("Face", "Face buttons"),
                ("LeftStick", "Left analog stick"),
                ("RightStick", "Right analog stick"),
            };
            List<(string, FrameworkElement)> rows = new()
            {
                MakeKeyMapsSliderRow("Key size", "KeySize", 0.6, 2.0, 0.05),
            };
            foreach ((string key, string label) in wheels)
            {
                rows.Add(MakeKeyMapsSliderRow(label + " center offset X", key + "OffsetX", -400, 400, 5));
                rows.Add(MakeKeyMapsSliderRow(label + " center offset Y", key + "OffsetY", -300, 300, 5));
                rows.Add(MakeKeyMapsSliderRow(label + " spread", key + "Spread", 0.5, 2.0, 0.05));
            }

            // ── Atoms: one per key slot (center prompt + quarks + button icon) ──
            (string header, (string prop, string label)[] items)[] atomGroups =
            {
                ("── D-Pad atoms ──", new[]
                {
                    ("DPadUpAtomX", "D-Pad Up atom X"), ("DPadUpAtomY", "D-Pad Up atom Y"),
                    ("DPadLeftAtomX", "D-Pad Left atom X"), ("DPadLeftAtomY", "D-Pad Left atom Y"),
                    ("DPadRightAtomX", "D-Pad Right atom X"), ("DPadRightAtomY", "D-Pad Right atom Y"),
                    ("DPadDownAtomX", "D-Pad Down atom X"), ("DPadDownAtomY", "D-Pad Down atom Y"),
                }),
                ("── Face-button atoms ──", new[]
                {
                    ("FaceUpAtomX", "Face Y atom X"), ("FaceUpAtomY", "Face Y atom Y"),
                    ("FaceLeftAtomX", "Face X atom X"), ("FaceLeftAtomY", "Face X atom Y"),
                    ("FaceRightAtomX", "Face B atom X"), ("FaceRightAtomY", "Face B atom Y"),
                    ("FaceDownAtomX", "Face A atom X"), ("FaceDownAtomY", "Face A atom Y"),
                }),
                ("── Left-stick atoms ──", new[]
                {
                    ("LeftStickUpAtomX", "L-Stick Up atom X"), ("LeftStickUpAtomY", "L-Stick Up atom Y"),
                    ("LeftStickLeftAtomX", "L-Stick Left atom X"), ("LeftStickLeftAtomY", "L-Stick Left atom Y"),
                    ("LeftStickRightAtomX", "L-Stick Right atom X"), ("LeftStickRightAtomY", "L-Stick Right atom Y"),
                    ("LeftStickPressAtomX", "L-Stick Press atom X"), ("LeftStickPressAtomY", "L-Stick Press atom Y"),
                    ("LeftStickDownAtomX", "L-Stick Down atom X"), ("LeftStickDownAtomY", "L-Stick Down atom Y"),
                }),
                ("── Right-stick atoms ──", new[]
                {
                    ("RightStickUpAtomX", "R-Stick Up atom X"), ("RightStickUpAtomY", "R-Stick Up atom Y"),
                    ("RightStickLeftAtomX", "R-Stick Left atom X"), ("RightStickLeftAtomY", "R-Stick Left atom Y"),
                    ("RightStickRightAtomX", "R-Stick Right atom X"), ("RightStickRightAtomY", "R-Stick Right atom Y"),
                    ("RightStickPressAtomX", "R-Stick Press atom X"), ("RightStickPressAtomY", "R-Stick Press atom Y"),
                    ("RightStickDownAtomX", "R-Stick Down atom X"), ("RightStickDownAtomY", "R-Stick Down atom Y"),
                }),
                ("── Atom look (all atoms) ──", new[]
                {
                    ("AtomSize", "Atom (big prompt) size"),
                    ("QuarkSize", "Quark (small prompt) size"),
                    ("QuarkDistance", "Quark distance from atom"),
                    ("IconOffsetX", "Button-icon offset X"),
                    ("IconOffsetY", "Button-icon offset Y (-1 = auto)"),
                }),
            };
            foreach ((string header, (string prop, string label)[] items) in atomGroups)
            {
                rows.Add((header, null!));
                foreach ((string prop, string label) in items)
                {
                    if (prop == "AtomSize" || prop == "QuarkSize" || prop == "QuarkDistance")
                    {
                        rows.Add(MakeKeyMapsSliderRow(label, prop, 0.5, 2.0, 0.05));
                    }
                    else if (prop == "IconOffsetX" || prop == "IconOffsetY")
                    {
                        rows.Add(MakeKeyMapsSliderRow(label, prop, -200, 200, 5));
                    }
                    else
                    {
                        rows.Add(MakeKeyMapsSliderRow(label, prop, -500, 500, 5));
                    }
                }
            }
            return rows.ToArray();
        }

        /// <summary>Builds one labeled slider row for a Key Maps layout property
        /// (live-applied on drag; a read-only value caption shows the current
        /// number and updates as the thumb moves).</summary>
        private (string, FrameworkElement) MakeKeyMapsSliderRow(string label, string propertyName, double minimum, double maximum, double step)
        {
            Slider slider = new()
            {
                Minimum = minimum,
                Maximum = maximum,
                TickFrequency = step,
                IsSnapToTickEnabled = false,
                Width = 240,
                VerticalAlignment = VerticalAlignment.Center,
            };
            TextBlock valueLabel = new()
            {
                MinWidth = 58,
                VerticalAlignment = VerticalAlignment.Center,
                HorizontalAlignment = HorizontalAlignment.Left,
                Margin = new Thickness(10, 0, 0, 0),
                Text = slider.Value.ToString("0.#"),
            };
            StackPanel host = new() { Orientation = Orientation.Horizontal };
            host.Children.Add(slider);
            host.Children.Add(valueLabel);
            _keyMapsSliders[propertyName] = slider;
            _keyMapsValueLabels[propertyName] = valueLabel;
            string valueFormat = step < 1.0 ? "0.##" : "0.#";
            slider.ValueChanged += (_, e) =>
            {
                valueLabel.Text = e.NewValue.ToString(valueFormat, CultureInfo.CurrentCulture);
                ApplyKeyMapsSliderChange(propertyName, e.NewValue);
            };
            return (label + ":", host);
        }

        private void InitializeKeyMapsLayoutEditors(Settings.AppSettings s)
        {
            Settings.KeyMapsLayoutSettings layout = s.KeyMaps.Layout;
            _suppressKeyMapsLiveApply = true;
            foreach (KeyValuePair<string, Slider> pair in _keyMapsSliders)
            {
                double value = typeof(Settings.KeyMapsLayoutSettings).GetProperty(pair.Key)!.GetValue(layout) as double? ?? 0.0;
                pair.Value.Value = value;
                if (_keyMapsValueLabels.TryGetValue(pair.Key, out TextBlock? label))
                {
                    label.Text = value.ToString(pair.Value.TickFrequency < 1.0 ? "0.##" : "0.#", CultureInfo.CurrentCulture);
                }
            }
            _suppressKeyMapsLiveApply = false;
        }

        /// <summary>Writes one slider value into the layout settings live
        /// (drag → board rebuild in the same tick); skipped while the
        /// initializers are seeding values.</summary>
        private void ApplyKeyMapsSliderChange(string propertyName, double value)
        {
            if (_suppressKeyMapsLiveApply)
            {
                return;
            }
            Settings.KeyMapsLayoutSettings layout = Settings.AppSettings.Instance.KeyMaps.Layout;
            typeof(Settings.KeyMapsLayoutSettings).GetProperty(propertyName)!.SetValue(layout, value);
            layout.Normalize();
            Settings.AppSettings.Save();
            AppOrchestrator.NotifyKeyMapsLayoutChanged();
        }

        private void BackupKeyMapsLayout(Settings.KeyMapsLayoutSettings layout)
        {
            _keyMapsLayoutBackup.Clear();
            foreach (System.Reflection.PropertyInfo property in typeof(Settings.KeyMapsLayoutSettings).GetProperties())
            {
                _keyMapsLayoutBackup[property.Name] = property.GetValue(layout);
            }
        }

        /// <summary>Restores the pre-opening Key Maps layout after Cancel.</summary>
        private void RestoreKeyMapsLayout()
        {
            if (_keyMapsLayoutBackup.Count == 0)
            {
                return;
            }
            Settings.KeyMapsLayoutSettings layout = Settings.AppSettings.Instance.KeyMaps.Layout;
            _suppressKeyMapsLiveApply = true;
            foreach (KeyValuePair<string, object> pair in _keyMapsLayoutBackup)
            {
                double value = Convert.ToDouble(pair.Value);
                typeof(Settings.KeyMapsLayoutSettings).GetProperty(pair.Key)!.SetValue(layout, value);
                if (_keyMapsSliders.TryGetValue(pair.Key, out Slider? slider))
                {
                    slider.Value = value;
                }
            }
            _suppressKeyMapsLiveApply = false;
            Settings.AppSettings.Save();
            AppOrchestrator.NotifyKeyMapsLayoutChanged();
        }

        private void Save()
        {
            ValidateNumericFields();
            var s = Settings.AppSettings.Instance;
            var selectedHidHidePaths = _hidHidePaths
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
            bool hidHideChanged = s.HidHideSessionEnabled != (_hidHideSession.IsChecked == true)
                || s.HidHideLegacyFallbackEnabled != (_hidHideLegacy.IsChecked == true)
                || !new HashSet<string>(s.HidHideDeviceInstancePaths, StringComparer.OrdinalIgnoreCase)
                    .SetEquals(selectedHidHidePaths);

            s.KeySpacing = ReadValidatedNumber(_spacing);
            s.OverlayMoveSpeed = ReadValidatedNumber(_keyboardMoveSpeed);
            s.MouseSpeed = ReadValidatedNumber(_mouseSpeed);
            s.MouseSpeedBoostMultiplier = ReadValidatedNumber(_boost);
            s.ScrollSpeed = ReadValidatedNumber(_scroll);
            s.StickDeadzone = ReadValidatedNumber(_deadzone);
            s.MouseStickDeadzone = ReadValidatedNumber(_mouseDeadzone);
            s.AnalogStickCurveExponent = ReadValidatedNumber(_curveExponent);
            s.CursorLagEnabled = _cursorLag.IsChecked == true;
            s.CursorLagSeconds = ReadValidatedNumber(_cursorLagSeconds);
            s.HideRaysInCursorLag = _hideLagRays.IsChecked == true;
            s.FreeCursorEnabled = _freeCursor.IsChecked == true;
            s.FreeCursorSpeed = ReadValidatedNumber(_freeCursorSpeed);
            s.HideCenterPointsAndRaysInFreeCursor = _hideCentersAndRays.IsChecked == true;
            SaveKeyMapsLayout(s);
            s.ShowButtonLegend = _legend.IsChecked == true;
            s.AlwaysShowKeyboardAtCursorPosition = _showAtCursor.IsChecked == true;
            s.ProfileToastPermanent = _toastPermanent.IsChecked == true;
            s.StartInMouseMode = _startMouse.IsChecked == true;
            s.HidHideSessionEnabled = _hidHideSession.IsChecked == true;
            s.HidHideLegacyFallbackEnabled = _hidHideLegacy.IsChecked == true;
            s.HidHideDeviceInstancePaths = selectedHidHidePaths;

            bool wantAdmin = _runAdmin.IsChecked == true;
            bool wantStartup = _startup.IsChecked == true;

            s.AdminLaunch = wantAdmin;
            s.RunOnStartup = wantStartup;
            bool canManageAdminTask = Util.LaunchUtil.IsAdmin();
            if (canManageAdminTask)
                Util.LaunchUtil.SetAdminStartup(wantStartup && wantAdmin);
            if (wantStartup && (!wantAdmin || !canManageAdminTask))
                Util.LaunchUtil.CreateStartupShortcut();
            else
                Util.LaunchUtil.RemoveStartupShortcut();

            Settings.AppSettings.Save();
            AppOrchestrator.NotifyStickPointsChanged();
            if (hidHideChanged)
                AppOrchestrator.NotifyHidHideSettingsChanged();
        }

        /// <summary>Pushes slider values back into the Key Maps layout (the
        /// sliders already live-apply; this is the authoritative write on OK).</summary>
        private void SaveKeyMapsLayout(Settings.AppSettings s)
        {
            Settings.KeyMapsLayoutSettings layout = s.KeyMaps.Layout;
            foreach (KeyValuePair<string, Slider> pair in _keyMapsSliders)
            {
                typeof(Settings.KeyMapsLayoutSettings).GetProperty(pair.Key)!.SetValue(
                    layout, pair.Value.Value);
            }
            layout.Normalize();
        }

        private FrameworkElement BuildHidHideSection()
        {
            var select = new Button
            {
                Content = "Select controllers…",
                Padding = new Thickness(10, 3, 10, 3),
                Margin = new Thickness(0, 0, 8, 0)
            };
            select.Click += (_, __) =>
            {
                var dialog = new HidHideDevicesWindow(_hidHidePaths) { Owner = this };
                if (dialog.ShowDialog() == true)
                {
                    _hidHidePaths = dialog.SelectedPaths.ToList();
                    UpdateHidHideSelectionText();
                }
            };

            var configure = new Button
            {
                Content = "Open HidHide configuration…",
                Padding = new Thickness(10, 3, 10, 3)
            };
            configure.Click += (_, __) =>
            {
                if (!Input.HidHideDeviceCatalog.TryOpenConfiguration(out string error))
                    MessageBox.Show(this, error, "HidHide", MessageBoxButton.OK, MessageBoxImage.Warning);
            };

            var controls = new WrapPanel { Margin = new Thickness(0, 6, 0, 4) };
            controls.Children.Add(select);
            controls.Children.Add(configure);
            controls.Children.Add(_hidHideSelection);
            _hidHideSelection.Margin = new Thickness(10, 4, 0, 0);

            var instructions = new TextBlock
            {
                Text = "In HidHide, add this app to Applications, enable device hiding, and leave inverse cloak off. " +
                       "Legacy fallback temporarily edits HidHide's persistent device list. It restores only entries added by this app " +
                       "on DisableInput/exit and retries cleanup on the next app launch after a crash. Run as Administrator so Windows " +
                       "can restart the controller when the list changes; otherwise reconnect it manually.",
                TextWrapping = TextWrapping.Wrap,
                Foreground = Brushes.DimGray,
                Margin = new Thickness(0, 2, 0, 0)
            };

            var panel = new StackPanel { Margin = new Thickness(12, 12, 12, 4) };
            panel.Children.Add(_hidHideSession);
            _hidHideLegacy.Margin = new Thickness(18, 6, 0, 0);
            panel.Children.Add(_hidHideLegacy);
            panel.Children.Add(controls);
            panel.Children.Add(instructions);
            return new GroupBox { Header = "HidHide controller reservation", Content = panel, Margin = new Thickness(12, 10, 12, 0) };
        }

        private void UpdateHidHideSelectionText()
        {
            _hidHideSelection.Text = _hidHidePaths.Count == 0
                ? "No controller selected"
                : $"{_hidHidePaths.Count} device path{(_hidHidePaths.Count == 1 ? "" : "s")} selected";
        }

        private void ConfigureNumericValidation(TextBox input, Func<double, bool> inRange, string format)
        {
            string previousValue = input.Text;

            void Validate()
            {
                if (double.TryParse(input.Text, NumberStyles.Float, CultureInfo.CurrentCulture, out var value)
                    && double.IsFinite(value)
                    && inRange(value))
                {
                    previousValue = value.ToString(format, CultureInfo.CurrentCulture);
                }
                input.Text = previousValue;
            }

            input.LostKeyboardFocus += (_, __) => Validate();
            input.KeyDown += (_, e) =>
            {
                if (e.Key != Key.Enter) return;
                Validate();
                e.Handled = true;
            };
            _numericValidators.Add(Validate);
        }

        private void ValidateNumericFields()
        {
            foreach (var validate in _numericValidators)
                validate();
        }

        private static double ReadValidatedNumber(TextBox input) =>
            double.Parse(input.Text, NumberStyles.Float, CultureInfo.CurrentCulture);

        protected override void OnClosed(EventArgs e)
        {
            if (!_keyMapsSettingsSavedExplicitly)
            {
                RestoreKeyMapsLayout();
            }
            base.OnClosed(e);
        }
    }
}
