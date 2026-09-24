using System;
using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
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
        private readonly TextBox _deadzone = new() { Text = "" };
        private readonly CheckBox _legend = new() { Content = "Show button legend overlay" };
        private readonly CheckBox _toastPermanent = new() { Content = "Keep profile toast permanently visible" };
        private readonly CheckBox _runAdmin = new() { Content = "Run as Administrator every launch (UAC on startup)" };
        private readonly CheckBox _startup = new() { Content = "Run on Windows startup" };
        private readonly CheckBox _startMouse = new() { Content = "Start in mouse mode" };

        public SettingsWindow()
        {
            Title = "Development Gamepad Keyboard — Settings";
            Width = 460;
            SizeToContent = SizeToContent.Height;
            ResizeMode = ResizeMode.NoResize;
            WindowStartupLocation = WindowStartupLocation.CenterScreen;

            var s = Settings.AppSettings.Instance;
            _spacing.Text = s.KeySpacing.ToString("0.#");
            _mouseSpeed.Text = s.MouseSpeed.ToString("0.#");
            _boost.Text = s.MouseSpeedBoostMultiplier.ToString("0.##");
            _scroll.Text = s.ScrollSpeed.ToString("0.#");
            _deadzone.Text = s.StickDeadzone.ToString("0.###");
            _legend.IsChecked = s.ShowButtonLegend;
            _toastPermanent.IsChecked = s.ProfileToastPermanent;
            _runAdmin.IsChecked = s.AdminLaunch;
            _startup.IsChecked = s.RunOnStartup;
            _startMouse.IsChecked = s.StartInMouseMode;

            var grid = new Grid { Margin = new Thickness(12) };
            for (int i = 0; i < 7; i++) grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(220) });
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
            AddRow(2, "Mouse speed boost multiplier (R2):", _boost);
            AddRow(3, "Scroll speed:", _scroll);
            AddRow(4, "Stick deadzone (0.000–0.5):", _deadzone);
            AddRow(5, "", _legend);
            AddRow(6, "", _toastPermanent);

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
            var ptsBtn = new Button { Content = "Stick center points…", Padding = new Thickness(10, 3, 10, 3), Margin = new Thickness(8, 0, 0, 0) };
            ptsBtn.Click += (_, __) => new StickPointsEditorWindow().Show();
            editors.Children.Add(kbBtn);
            editors.Children.Add(moBtn);
            editors.Children.Add(ptsBtn);

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
            outer.Children.Add(editors);
            outer.Children.Add(buttons);
            buttons.Margin = new Thickness(0, 12, 12, 12);

            Content = outer;
        }

        private void Save()
        {
            var s = Settings.AppSettings.Instance;
            if (double.TryParse(_spacing.Text, out var spacing) && spacing >= 0) s.KeySpacing = spacing;
            if (double.TryParse(_mouseSpeed.Text, out var ms) && ms > 0) s.MouseSpeed = ms;
            if (double.TryParse(_boost.Text, out var boost) && boost >= 1) s.MouseSpeedBoostMultiplier = boost;
            if (double.TryParse(_scroll.Text, out var scroll) && scroll > 0) s.ScrollSpeed = scroll;
            if (double.TryParse(_deadzone.Text, System.Globalization.CultureInfo.InvariantCulture, out var dz) && dz >= 0 && dz <= 0.5) s.StickDeadzone = dz;
            s.ShowButtonLegend = _legend.IsChecked == true;
            s.ProfileToastPermanent = _toastPermanent.IsChecked == true;
            s.StartInMouseMode = _startMouse.IsChecked == true;

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
        }

        protected override void OnClosed(EventArgs e)
        {
            base.OnClosed(e);
        }
    }
}