using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using GamepadKeyboard.Native;

namespace GamepadKeyboard.Overlay
{
    /// <summary>Standalone toast so notifications remain visible while the keyboard is hidden.</summary>
    public sealed class StatusToastOverlay : Window
    {
        private readonly TextBlock _text = new()
        {
            Foreground = new SolidColorBrush(Color.FromRgb(240, 240, 245)),
            FontSize = 14,
            FontWeight = FontWeights.SemiBold,
            TextWrapping = TextWrapping.Wrap,
            MaxWidth = 640
        };

        private readonly System.Windows.Threading.DispatcherTimer _hideTimer =
            new() { Interval = TimeSpan.FromSeconds(3) };

        public StatusToastOverlay()
        {
            WindowStyle = WindowStyle.None;
            AllowsTransparency = true;
            Background = Brushes.Transparent;
            ResizeMode = ResizeMode.NoResize;
            SizeToContent = SizeToContent.WidthAndHeight;
            ShowInTaskbar = false;
            ShowActivated = false;
            Topmost = true;

            Content = new Border
            {
                Background = new SolidColorBrush(Color.FromArgb(225, 20, 20, 24)),
                BorderBrush = new SolidColorBrush(Color.FromArgb(220, 80, 80, 92)),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(5),
                Padding = new Thickness(10, 6, 10, 6),
                Child = _text
            };

            _hideTimer.Tick += (_, __) =>
            {
                _hideTimer.Stop();
                Hide();
            };
        }

        public void ShowStatus(string message, int seconds, bool permanent)
        {
            _text.Text = message;
            if (!IsVisible) Show();
            PositionAtTopCenter();

            _hideTimer.Stop();
            if (!permanent)
            {
                _hideTimer.Interval = TimeSpan.FromSeconds(Math.Max(1, seconds));
                _hideTimer.Start();
            }
        }

        private void PositionAtTopCenter()
        {
            UpdateLayout();
            var area = SystemParameters.WorkArea;
            Left = area.Left + Math.Max(0, (area.Width - ActualWidth) / 2);
            Top = area.Top + 32;
        }

        protected override void OnSourceInitialized(EventArgs e)
        {
            base.OnSourceInitialized(e);
            var hwnd = new WindowInteropHelper(this).Handle;
            int ex = NativeMethods.GetWindowLong(hwnd, NativeMethods.GWL_EXSTYLE);
            NativeMethods.SetWindowLong(hwnd, NativeMethods.GWL_EXSTYLE,
                ex | NativeMethods.WS_EX_LAYERED | NativeMethods.WS_EX_NOACTIVATE |
                NativeMethods.WS_EX_TOOLWINDOW | NativeMethods.WS_EX_TRANSPARENT);
        }
    }
}
