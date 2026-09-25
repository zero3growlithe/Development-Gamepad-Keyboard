using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace GamepadKeyboard.UI
{
    /// <summary>
    /// Small About dialog: app name, version, shortcut cheat sheet.
    /// </summary>
    public sealed class AboutWindow : Window
    {
        private static AboutWindow? _instance;
        public static void ShowSingleton()
        {
            if (_instance == null)
            {
                _instance = new AboutWindow();
                _instance.Closed += (_, __) => _instance = null;
                _instance.Show();
            }
            else
            {
                _instance.Activate();
            }
        }

        public AboutWindow()
        {
            Title = "About — Development Gamepad Keyboard";
            Width = 480;
            SizeToContent = SizeToContent.Height;
            ResizeMode = ResizeMode.NoResize;
            WindowStartupLocation = WindowStartupLocation.CenterScreen;

            var text = new TextBlock
            {
                Margin = new Thickness(16),
                TextWrapping = TextWrapping.Wrap,
                Foreground = Brushes.Black
            };

            string version = typeof(App).Assembly.GetName().Version?.ToString(3) ?? "0.1";
            text.Inlines.Add(new System.Windows.Documents.Run("Development Gamepad Keyboard v" + version)
            {
                FontWeight = FontWeights.Bold,
                FontSize = 16
            });
            text.Inlines.Add(new System.Windows.Documents.Run("\n\nType and control the mouse with dual analog sticks on Xbox / PlayStation controllers.\n\n"));
            text.Inlines.Add(new System.Windows.Documents.Run("Keyboard mode — default bindings:\n")
            {
                FontWeight = FontWeights.Bold
            });
            text.Inlines.Add(new System.Windows.Documents.Run(
                "• Left/Right stick: aim a cursor; L1 / R1 submit its highlighted key\n" +
                "• Hold L2 = Shift, hold R2 = Ctrl\n" +
                "• B = mouse mode, L3 toggles keyboard move/scale mode\n" +
                "• D-pad = arrows\n" +
                "• All buttons and shortcuts are remappable per profile\n\n"));
            text.Inlines.Add(new System.Windows.Documents.Run("Mouse mode:\n") { FontWeight = FontWeights.Bold });
            text.Inlines.Add(new System.Windows.Documents.Run(
                "• Right stick = cursor, left stick = scroll, D-pad = scroll\n" +
                "• L1 = left click, R1 = right click, L2 = middle click, R2 = speed boost\n" +
                "• Y = keyboard mode\n" +
                "• All buttons are remappable per mouse profile in settings"));

            var ok = new Button
            {
                Content = "Close",
                Padding = new Thickness(16, 4, 16, 4),
                HorizontalAlignment = HorizontalAlignment.Right,
                Margin = new Thickness(0, 0, 16, 12)
            };
            ok.Click += (_, __) => Close();

            var stack = new StackPanel();
            stack.Children.Add(text);
            stack.Children.Add(ok);
            Content = stack;
        }
    }
}
