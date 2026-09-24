using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;

namespace GamepadKeyboard.Overlay
{
    /// <summary>
    /// Transient toast that shows the active profile / mode name.
    /// Can linger permanently if configured in settings.
    /// </summary>
    public sealed class ToastOverlay : Window
    {
        private readonly TextBlock _text = new()
        {
            Foreground = Brushes.White,
            FontSize = 18,
            FontWeight = FontWeights.Bold
        };

        private readonly System.Windows.Threading.DispatcherTimer _hideTimer;

        public ToastOverlay()
        {
            WindowStyle = WindowStyle.None;
            AllowsTransparency = true;
            Background = Brushes.Transparent;
            ResizeMode = ResizeMode.NoResize;
            ShowInTaskbar = false;
            Topmost = true;
            ShowActivated = false;

            var border = new Border
            {
                Background = new SolidColorBrush(Color.FromArgb(210, 24, 24, 32)),
                CornerRadius = new CornerRadius(8),
                Padding = new Thickness(16, 8, 16, 8),
                Child = _text
            };
            Content = border;

            _hideTimer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromSeconds(3) };
            _hideTimer.Tick += (_, __) => Hide();
        }

        public void Show(string message, int seconds, bool permanent)
        {
            _text.Text = message;
            Show();
            if (!permanent)
            {
                _hideTimer.Interval = TimeSpan.FromSeconds(Math.Max(1, seconds));
                _hideTimer.Stop();
                _hideTimer.Start();
            }
            else
            {
                _hideTimer.Stop();
            }
        }
    }
}