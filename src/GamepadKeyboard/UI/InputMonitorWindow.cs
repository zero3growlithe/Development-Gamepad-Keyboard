using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using GamepadKeyboard.Native;

namespace GamepadKeyboard.UI
{
    /// <summary>
    /// Live gamepad input monitor: per-controller raw WGI axis/button values,
    /// XInput slot states, chosen input path and app state — refreshed ~8×/s.
    /// Answers "is any input path receiving data at all" without log round-trips.
    /// </summary>
    public sealed class InputMonitorWindow : Window
    {
        private static InputMonitorWindow? _instance;

        private readonly Input.GamepadService _pad;
        private readonly ControllerMapper _mapper;
        private readonly TextBlock _text = new();
        private readonly DispatcherTimer _timer;

        public static void ShowSingleton(Input.GamepadService pad, ControllerMapper mapper)
        {
            if (_instance != null)
            {
                _instance.Activate();
                return;
            }
            _instance = new InputMonitorWindow(pad, mapper);
            _instance.Show();
        }

        private InputMonitorWindow(Input.GamepadService pad, ControllerMapper mapper)
        {
            _pad = pad;
            _mapper = mapper;

            Title = "Gamepad input monitor";
            Width = 470;
            Height = 360;
            WindowStyle = WindowStyle.None;
            ResizeMode = ResizeMode.NoResize;
            ShowActivated = false;
            ShowInTaskbar = false;
            Topmost = true;
            Background = new SolidColorBrush(Color.FromRgb(14, 14, 20));
            Left = System.Windows.SystemParameters.WorkArea.Width - Width - 16;
            Top = 16;

            _text.Margin = new Thickness(12, 34, 12, 12);
            _text.FontFamily = new FontFamily("Consolas");
            _text.FontSize = 12;
            _text.Foreground = Brushes.LightGray;
            _text.TextWrapping = TextWrapping.Wrap;

            var close = new Button
            {
                Content = "×",
                Width = 28,
                Height = 24,
                Margin = new Thickness(0, 4, 6, 0),
                HorizontalAlignment = HorizontalAlignment.Right,
                VerticalAlignment = VerticalAlignment.Top
            };
            close.Click += (_, __) => Close();

            var grid = new Grid();
            grid.Children.Add(_text);
            grid.Children.Add(close);
            Content = grid;

            MouseLeftButtonDown += (_, __) => { try { DragMove(); } catch { /* no-op */ } };

            _timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(120) };
            _timer.Tick += (_, __) => Update();
            _timer.Start();

            Closed += (_, __) => { _timer.Stop(); _instance = null; };
        }

        protected override void OnSourceInitialized(EventArgs e)
        {
            base.OnSourceInitialized(e);
            var hwnd = new WindowInteropHelper(this).Handle;
            int ex = NativeMethods.GetWindowLong(hwnd, NativeMethods.GWL_EXSTYLE);
            // NOACTIVATE: never takes focus — SendInput targets keep receiving keys
            NativeMethods.SetWindowLong(hwnd, NativeMethods.GWL_EXSTYLE,
                ex | NativeMethods.WS_EX_NOACTIVATE | NativeMethods.WS_EX_TOOLWINDOW);
        }

        private void Update()
        {
            var lines = _pad.MonitorLines();
            lines.Add("input enabled: " + _mapper.InputEnabled + "   mouse mode: " + _mapper.MouseMode);
            lines.Add("");
            lines.Add("drag anywhere to move — × to close");
            _text.Text = string.Join(Environment.NewLine, lines);
        }
    }
}