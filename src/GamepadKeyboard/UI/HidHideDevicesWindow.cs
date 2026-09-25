using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using GamepadKeyboard.Input;

namespace GamepadKeyboard.UI
{
    public sealed class HidHideDevicesWindow : Window
    {
        private readonly StackPanel _devices = new();
        private readonly HashSet<string> _initialSelection;
        private readonly List<(CheckBox checkBox, HidHideGamingDevice device)> _choices = new();

        public IReadOnlyList<string> SelectedPaths { get; private set; }

        public HidHideDevicesWindow(IEnumerable<string> selectedPaths)
        {
            _initialSelection = new HashSet<string>(selectedPaths, StringComparer.OrdinalIgnoreCase);
            SelectedPaths = _initialSelection.ToArray();
            Title = "Select controllers for HidHide";
            Width = 620;
            Height = 430;
            WindowStartupLocation = WindowStartupLocation.CenterOwner;

            var explanation = new TextBlock
            {
                Text = "Selected controllers are hidden from other apps only while this app's input is enabled. " +
                       "The claim is kept in memory and disappears when this app exits.",
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(12, 12, 12, 8)
            };

            var scroll = new ScrollViewer
            {
                Content = _devices,
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                Margin = new Thickness(12, 0, 12, 0)
            };

            var buttons = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                HorizontalAlignment = HorizontalAlignment.Right,
                Margin = new Thickness(12)
            };
            var refresh = new Button { Content = "Refresh", Padding = new Thickness(12, 4, 12, 4), Margin = new Thickness(0, 0, 8, 0) };
            refresh.Click += (_, __) => LoadDevices();
            var ok = new Button { Content = "OK", Padding = new Thickness(16, 4, 16, 4), Margin = new Thickness(0, 0, 8, 0) };
            ok.Click += (_, __) =>
            {
                SelectedPaths = _choices.Where(choice => choice.checkBox.IsChecked == true)
                    .SelectMany(choice => choice.device.InstancePaths)
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToArray();
                DialogResult = true;
            };
            var cancel = new Button { Content = "Cancel", Padding = new Thickness(16, 4, 16, 4) };
            cancel.Click += (_, __) => Close();
            buttons.Children.Add(refresh);
            buttons.Children.Add(ok);
            buttons.Children.Add(cancel);

            var root = new DockPanel();
            DockPanel.SetDock(explanation, Dock.Top);
            DockPanel.SetDock(buttons, Dock.Bottom);
            root.Children.Add(explanation);
            root.Children.Add(buttons);
            root.Children.Add(scroll);
            Content = root;

            Loaded += (_, __) => LoadDevices();
        }

        private void LoadDevices()
        {
            var currentSelection = new HashSet<string>(
                _choices.Where(choice => choice.checkBox.IsChecked == true)
                    .SelectMany(choice => choice.device.InstancePaths),
                StringComparer.OrdinalIgnoreCase);
            if (_choices.Count == 0) currentSelection.UnionWith(_initialSelection);

            _devices.Children.Clear();
            _choices.Clear();
            try
            {
                var devices = HidHideDeviceCatalog.Load();
                if (devices.Count == 0)
                {
                    _devices.Children.Add(new TextBlock { Text = "No connected gaming controllers were found." });
                    return;
                }

                foreach (var device in devices)
                {
                    var check = new CheckBox
                    {
                        Content = device.Name,
                        ToolTip = string.Join(Environment.NewLine, device.InstancePaths),
                        IsChecked = device.InstancePaths.Any(currentSelection.Contains),
                        Margin = new Thickness(0, 4, 0, 4)
                    };
                    _choices.Add((check, device));
                    _devices.Children.Add(check);
                }
            }
            catch (Exception ex)
            {
                _devices.Children.Add(new TextBlock
                {
                    Text = ex.Message,
                    TextWrapping = TextWrapping.Wrap
                });
            }
        }
    }
}
