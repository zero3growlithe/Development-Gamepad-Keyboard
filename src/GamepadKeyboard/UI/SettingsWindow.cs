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
        private readonly TextBox _mouseSpeed = new() { Text = "" };
        private readonly TextBox _boost = new() { Text = "" };
        private readonly TextBox _scroll = new() { Text = "" };
        private readonly CheckBox _invertScroll = new() { Content = "Invert vertical mouse scroll direction" };
        private readonly CheckBox _invertHorizontalScroll = new() { Content = "Invert horizontal mouse scroll direction" };
        private readonly TextBox _deadzone = new() { Text = "" };
        private readonly TextBox _mouseDeadzone = new() { Text = "" };
        private readonly CheckBox _cursorLag = new() { Content = "Enable cursor lag" };
        private readonly TextBox _cursorLagSeconds = new() { Text = "" };
        private readonly CheckBox _hideLagRays = new() { Content = "Hide rays in cursor lag mode" };
        private readonly CheckBox _freeCursor = new() { Content = "Enable free cursor" };
        private readonly TextBox _freeCursorSpeed = new() { Text = "" };
        private readonly CheckBox _hideCentersAndRays = new() { Content = "Hide center points and rays in free cursor mode" };
        private readonly CheckBox _legend = new() { Content = "Show button legend overlay" };
        private readonly CheckBox _toastPermanent = new() { Content = "Keep profile toast permanently visible" };
        private readonly CheckBox _runAdmin = new() { Content = "Run as Administrator every launch (UAC on startup)" };
        private readonly CheckBox _startup = new() { Content = "Run on Windows startup" };
        private readonly CheckBox _startMouse = new() { Content = "Start in mouse mode" };
        private readonly CheckBox _hidHideSession = new() { Content = "Reserve selected controllers while input is enabled" };
        private readonly TextBlock _hidHideSelection = new() { VerticalAlignment = VerticalAlignment.Center };
        private List<string> _hidHidePaths = new();
        private readonly List<Action> _numericValidators = new();

        public SettingsWindow()
        {
            Title = "Development Gamepad Keyboard — Settings";
            Width = 560;
            SizeToContent = SizeToContent.Height;
            ResizeMode = ResizeMode.NoResize;
            WindowStartupLocation = WindowStartupLocation.CenterScreen;

            var s = Settings.AppSettings.Instance;
            _spacing.Text = s.KeySpacing.ToString("0.#");
            _mouseSpeed.Text = s.MouseSpeed.ToString("0.#");
            _boost.Text = s.MouseSpeedBoostMultiplier.ToString("0.##");
            _scroll.Text = s.ScrollSpeed.ToString("0.#");
            _invertScroll.IsChecked = s.InvertVerticalScroll;
            _invertHorizontalScroll.IsChecked = s.InvertHorizontalScroll;
            _deadzone.Text = s.StickDeadzone.ToString("0.###");
            _mouseDeadzone.Text = s.MouseStickDeadzone.ToString("0.###");
            _cursorLag.IsChecked = s.CursorLagEnabled;
            _cursorLagSeconds.Text = s.CursorLagSeconds.ToString("0.###");
            _hideLagRays.IsChecked = s.HideRaysInCursorLag;
            _freeCursor.IsChecked = s.FreeCursorEnabled;
            _freeCursorSpeed.Text = s.FreeCursorSpeed.ToString("0.#");
            _hideCentersAndRays.IsChecked = s.HideCenterPointsAndRaysInFreeCursor;
            _legend.IsChecked = s.ShowButtonLegend;
            _toastPermanent.IsChecked = s.ProfileToastPermanent;
            _runAdmin.IsChecked = s.AdminLaunch;
            _startup.IsChecked = s.RunOnStartup;
            _startMouse.IsChecked = s.StartInMouseMode;
            _hidHideSession.IsChecked = s.HidHideSessionEnabled;
            _hidHidePaths = s.HidHideDeviceInstancePaths.ToList();
            UpdateHidHideSelectionText();

            var grid = new Grid { Margin = new Thickness(12) };
            ConfigureNumericValidation(_spacing, value => value >= 0, "0.#");
            ConfigureNumericValidation(_mouseSpeed, value => value > 0, "0.#");
            ConfigureNumericValidation(_boost, value => value >= 1, "0.##");
            ConfigureNumericValidation(_scroll, value => value > 0, "0.#");
            ConfigureNumericValidation(_deadzone, value => value is >= 0 and <= 0.5, "0.###");
            ConfigureNumericValidation(_mouseDeadzone, value => value is >= 0 and <= 0.5, "0.###");
            ConfigureNumericValidation(_cursorLagSeconds, value => value >= 0, "0.###");
            ConfigureNumericValidation(_freeCursorSpeed, value => value > 0, "0.#");

            for (int i = 0; i < 16; i++) grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(285) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

            void AddRow(int r, string label, FrameworkElement editor)
            {
                var l = new TextBlock { Text = label, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 4, 8, 4) };
                Grid.SetRow(l, r); Grid.SetColumn(l, 0);
                editor.Margin = new Thickness(0, 4, 0, 4);
                Grid.SetRow(editor, r); Grid.SetColumn(editor, 1);
                grid.Children.Add(l);
                grid.Children.Add(editor);
            }

            AddRow(0, "Key spacing (px gap between keys):", _spacing);
            AddRow(1, "Mouse speed (px per stick unit):", _mouseSpeed);
            AddRow(2, "Mouse speed boost multiplier:", _boost);
            AddRow(3, "Scroll speed:", _scroll);
            AddRow(4, "", _invertScroll);
            AddRow(5, "", _invertHorizontalScroll);
            AddRow(6, "Stick deadzone keyboard mode (0.000–0.5):", _deadzone);
            AddRow(7, "Stick deadzone mouse mode (0.000–0.5):", _mouseDeadzone);
            AddRow(8, "", _cursorLag);
            AddRow(9, "Cursor lag (seconds; 0 = instant):", _cursorLagSeconds);
            AddRow(10, "", _hideLagRays);
            AddRow(11, "", _freeCursor);
            AddRow(12, "Free cursor speed (px/second):", _freeCursorSpeed);
            AddRow(13, "", _hideCentersAndRays);
            AddRow(14, "", _legend);
            AddRow(15, "", _toastPermanent);

            // WrapPanel: three wide buttons wrap to the next line instead of
            // being clipped off the 460 px window edge
            var editors = new WrapPanel
            {
                Margin = new Thickness(0, 10, 0, 0)
            };
            var kbBtn = new Button { Content = "Gamepad bindings (keyboard mode)…", Padding = new Thickness(10, 3, 10, 3), Margin = new Thickness(0, 0, 8, 0) };
            kbBtn.Click += (_, __) => new BindingsEditorWindow(mouse: false).Show();
            var moBtn = new Button { Content = "Gamepad bindings (mouse mode)…", Padding = new Thickness(10, 3, 10, 3) };
            moBtn.Click += (_, __) => new BindingsEditorWindow(mouse: true).Show();
            editors.Children.Add(kbBtn);
            editors.Children.Add(moBtn);

            var buttons = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                HorizontalAlignment = HorizontalAlignment.Right,
                Margin = new Thickness(0, 12, 0, 0)
            };
            var ok = new Button { Content = "OK", Padding = new Thickness(16, 4, 16, 4), Margin = new Thickness(0, 0, 8, 0) };
            ok.Click += (_, __) => { Save(); Close(); };
            var cancel = new Button { Content = "Cancel", Padding = new Thickness(16, 4, 16, 4) };
            cancel.Click += (_, __) => Close();
            buttons.Children.Add(ok);
            buttons.Children.Add(cancel);

            var outer = new StackPanel();
            outer.Children.Add(grid);
            outer.Children.Add(_runAdmin);
            outer.Children.Add(_startup);
            outer.Children.Add(_startMouse);
            _runAdmin.Margin = new Thickness(12, 8, 12, 0);
            _startup.Margin = new Thickness(12, 4, 12, 0);
            _startMouse.Margin = new Thickness(12, 4, 12, 0);
            outer.Children.Add(BuildHidHideSection());
            outer.Children.Add(editors);
            outer.Children.Add(buttons);
            buttons.Margin = new Thickness(0, 12, 12, 12);

            Content = outer;
        }

        private void Save()
        {
            ValidateNumericFields();
            var s = Settings.AppSettings.Instance;
            s.KeySpacing = ReadValidatedNumber(_spacing);
            s.MouseSpeed = ReadValidatedNumber(_mouseSpeed);
            s.MouseSpeedBoostMultiplier = ReadValidatedNumber(_boost);
            s.ScrollSpeed = ReadValidatedNumber(_scroll);
            s.InvertVerticalScroll = _invertScroll.IsChecked == true;
            s.InvertHorizontalScroll = _invertHorizontalScroll.IsChecked == true;
            s.StickDeadzone = ReadValidatedNumber(_deadzone);
            s.MouseStickDeadzone = ReadValidatedNumber(_mouseDeadzone);
            s.CursorLagEnabled = _cursorLag.IsChecked == true;
            s.CursorLagSeconds = ReadValidatedNumber(_cursorLagSeconds);
            s.HideRaysInCursorLag = _hideLagRays.IsChecked == true;
            s.FreeCursorEnabled = _freeCursor.IsChecked == true;
            s.FreeCursorSpeed = ReadValidatedNumber(_freeCursorSpeed);
            s.HideCenterPointsAndRaysInFreeCursor = _hideCentersAndRays.IsChecked == true;
            s.ShowButtonLegend = _legend.IsChecked == true;
            s.ProfileToastPermanent = _toastPermanent.IsChecked == true;
            s.StartInMouseMode = _startMouse.IsChecked == true;
            s.HidHideSessionEnabled = _hidHideSession.IsChecked == true;
            s.HidHideDeviceInstancePaths = _hidHidePaths
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();

            bool wantAdmin = _runAdmin.IsChecked == true;
            bool wantStartup = _startup.IsChecked == true;

            if (wantAdmin != s.AdminLaunch)
            {
                s.AdminLaunch = wantAdmin;
                if (Util.LaunchUtil.IsAdmin())
                    Util.LaunchUtil.SetAdminStartup(wantAdmin);
            }

            if (wantStartup != s.RunOnStartup)
            {
                s.RunOnStartup = wantStartup;
                if (wantStartup) Util.LaunchUtil.CreateStartupShortcut();
                else Util.LaunchUtil.RemoveStartupShortcut();
            }

            Settings.AppSettings.Save();
            AppOrchestrator.NotifyHidHideSettingsChanged();
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
                Text = "Session-only safety mode. In HidHide, add this app to Applications, enable device hiding, " +
                       "and leave inverse cloak off. An unsupported driver fails open; persistent hiding is never used.",
                TextWrapping = TextWrapping.Wrap,
                Foreground = Brushes.DimGray,
                Margin = new Thickness(0, 2, 0, 0)
            };

            var panel = new StackPanel { Margin = new Thickness(12, 12, 12, 4) };
            panel.Children.Add(_hidHideSession);
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
            base.OnClosed(e);
        }
    }
}
