using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;

namespace GamepadKeyboard.Overlay
{
    /// <summary>
    /// Small always-on-top HUD that lists current gamepad button mappings
    /// (keyboard mode and mouse mode). Position/size adjustable in settings.
    /// </summary>
    public sealed class LegendOverlay : Window
    {
        private readonly StackPanel _panel = new() { Orientation = Orientation.Vertical };

        public LegendOverlay()
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
                Background = new SolidColorBrush(Color.FromArgb(200, 18, 18, 24)),
                CornerRadius = new CornerRadius(6),
                Padding = new Thickness(8),
                Child = _panel
            };
            Content = border;

            Left = 40;
            Top = 40;
            Width = 240;
        }

        public void SetEntries(IReadOnlyList<(string key, string action)> entries)
        {
            _panel.Children.Clear();
            foreach (var (key, action) in entries)
            {
                var row = new Grid { Margin = new Thickness(0, 1, 0, 1) };
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(70) });
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

                var k = new TextBlock
                {
                    Text = key,
                    FontWeight = FontWeights.SemiBold,
                    Foreground = Brushes.DeepSkyBlue,
                    FontSize = 12
                };
                Grid.SetColumn(k, 0);
                var a = new TextBlock
                {
                    Text = action,
                    Foreground = Brushes.White,
                    FontSize = 12
                };
                Grid.SetColumn(a, 1);
                row.Children.Add(k);
                row.Children.Add(a);
                _panel.Children.Add(row);
            }
        }

        public void ApplySettings(double left, double top, double opacity, int fontSize)
        {
            Left = left;
            Top = top;
            Opacity = opacity;
            foreach (var child in _panel.Children)
                if (child is Grid g)
                    foreach (var c in g.Children)
                        if (c is System.Windows.Controls.TextBlock tb)
                            tb.FontSize = fontSize;
        }
    }
}