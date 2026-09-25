using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using GamepadKeyboard.Keyboard;
using GamepadKeyboard.Settings;

namespace GamepadKeyboard.UI
{
    /// <summary>
    /// Edytor punktów początkowych promieni analogów na wirtualnej klawiaturze.
    /// Suwaki X/Y (0..1) dla lewego i prawego drążka; punkty odświeżają się
    /// w czasie rzeczywistym na overlay klawiatury.
    /// </summary>
    public sealed class StickPointsEditorWindow : Window
    {
        private readonly ComboBox _profileBox = new() { MinWidth = 170 };
        private readonly TextBox _nameBox = new() { MinWidth = 170 };
        private readonly Slider _lx = MakeSlider();
        private readonly Slider _ly = MakeSlider();
        private readonly Slider _rx = MakeSlider();
        private readonly Slider _ry = MakeSlider();
        private readonly Slider _ll = MakeRaySlider();
        private readonly Slider _rl = MakeRaySlider();
        private readonly TextBlock _lxv = MakeValue();
        private readonly TextBlock _lyv = MakeValue();
        private readonly TextBlock _rxv = MakeValue();
        private readonly TextBlock _ryv = MakeValue();
        private readonly TextBlock _llv = MakeValue();
        private readonly TextBlock _rlv = MakeValue();
        private int _suppress = 0;

        public StickPointsEditorWindow()
        {
            Title = "Keyboard mode — analog stick center points";
            Width = 540;
            SizeToContent = SizeToContent.Height;
            ResizeMode = ResizeMode.NoResize;
            WindowStartupLocation = WindowStartupLocation.CenterScreen;
            ShowInTaskbar = false;

            var root = new StackPanel { Margin = new Thickness(12) };

            // ── profiles ──
            var bar = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 4) };
            bar.Children.Add(new TextBlock { Text = "Profile:", VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 6, 0) });
            bar.Children.Add(_profileBox);
            bar.Children.Add(MakeBtn("Add", () => AddProfile(false)));
            bar.Children.Add(MakeBtn("Duplicate", () => AddProfile(true)));
            bar.Children.Add(MakeBtn("Delete", DeleteProfile));
            root.Children.Add(bar);

            var nameBar = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 8) };
            nameBar.Children.Add(new TextBlock { Text = "Name:", VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 6, 0) });
            nameBar.Children.Add(_nameBox);
            _nameBox.LostFocus += (_, __) => CommitName();
            root.Children.Add(nameBar);

            // ── sliders ──
            root.Children.Add(SliderRow("Left stick — X (left ↔ right):", _lx, _lxv));
            root.Children.Add(SliderRow("Left stick — Y (bottom ↔ top):", _ly, _lyv));
            root.Children.Add(Separator());
            root.Children.Add(SliderRow("Right stick — X (left ↔ right):", _rx, _rxv));
            root.Children.Add(SliderRow("Right stick — Y (bottom ↔ top):", _ry, _ryv));

            root.Children.Add(Separator());
            root.Children.Add(new TextBlock
            {
                Text = "Ray length (1.0 = LeftCtrl → Backspace distance):",
                FontWeight = FontWeights.SemiBold,
                Margin = new Thickness(0, 2, 0, 4)
            });
            root.Children.Add(SliderRow("Left stick ray length:", _ll, _llv));
            root.Children.Add(SliderRow("Right stick ray length:", _rl, _rlv));

            var hint = new TextBlock
            {
                Text = "Points update live on the virtual keyboard overlay.",
                Foreground = Brushes.Gray,
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 10, 0, 0)
            };
            root.Children.Add(hint);

            var close = new Button { Content = "Close", Padding = new Thickness(16, 4, 16, 4), HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 10, 0, 0) };
            close.Click += (_, __) => Close();
            root.Children.Add(close);

            Content = root;

            WireSliders();
            _profileBox.SelectionChanged += (_, __) => LoadSelected();
            ReloadProfileList();
        }

        private static Slider MakeRaySlider() => new()
        {
            Minimum = 0.05,
            Maximum = 1.0,
            TickFrequency = 0.05,
            IsSnapToTickEnabled = false,
            AutoToolTipPlacement = System.Windows.Controls.Primitives.AutoToolTipPlacement.TopLeft,
            AutoToolTipPrecision = 2,
            VerticalAlignment = VerticalAlignment.Center
        };

        private static Slider MakeSlider() => new()
        {
            Minimum = 0.02,
            Maximum = 0.98,
            TickFrequency = 0.05,
            IsSnapToTickEnabled = false,
            AutoToolTipPlacement = System.Windows.Controls.Primitives.AutoToolTipPlacement.TopLeft,
            AutoToolTipPrecision = 3,
            VerticalAlignment = VerticalAlignment.Center
        };

        private static TextBlock MakeValue() => new()
        {
            Width = 52,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(8, 0, 0, 0)
        };

        private static UIElement SliderRow(string label, Slider slider, TextBlock value)
        {
            var grid = new Grid { Margin = new Thickness(0, 6, 0, 6) };
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(240) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            var l = new TextBlock { Text = label, VerticalAlignment = VerticalAlignment.Center };
            Grid.SetColumn(l, 0);
            Grid.SetColumn(slider, 1);
            Grid.SetColumn(value, 2);
            grid.Children.Add(l);
            grid.Children.Add(slider);
            grid.Children.Add(value);
            return grid;
        }

        private static UIElement Separator() => new Separator { Margin = new Thickness(0, 6, 0, 6) };

        private static Button MakeBtn(string label, Action onClick)
        {
            var b = new Button { Content = label, Padding = new Thickness(8, 2, 8, 2), Margin = new Thickness(6, 0, 0, 0) };
            b.Click += (_, __) => onClick();
            return b;
        }

        private List<StickPointsProfile> Profiles => AppSettings.Instance.StickPointsProfiles;

        private void ReloadProfileList()
        {
            _suppress++;
            _profileBox.Items.Clear();
            for (int i = 0; i < Profiles.Count; i++)
                _profileBox.Items.Add(Profiles[i].Name + (i == AppSettings.Instance.ActiveStickPointsProfile ? "  (active)" : ""));
            int idx = Math.Clamp(AppSettings.Instance.ActiveStickPointsProfile, 0, Profiles.Count - 1);
            _profileBox.SelectedIndex = idx;
            _suppress--;
            LoadSelected();
        }

        private void LoadSelected()
        {
            if (_suppress > 0) return;
            int idx = _profileBox.SelectedIndex;
            if (idx < 0) return;
            AppSettings.Instance.ActiveStickPointsProfile = idx;
            _nameBox.Text = Profiles[idx].Name;

            var p = Profiles[idx];
            _suppress++;
            _lx.Value = p.LeftX; _ly.Value = p.LeftY;
            _rx.Value = p.RightX; _ry.Value = p.RightY;
            _ll.Value = p.LeftRayLength; _rl.Value = p.RightRayLength;
            _suppress--;

            UpdateValues();
            AppSettings.Save();
            AppOrchestrator.NotifyStickPointsChanged();
        }

        private void WireSliders()
        {
            _lx.ValueChanged += (_, __) => SliderChanged();
            _ly.ValueChanged += (_, __) => SliderChanged();
            _rx.ValueChanged += (_, __) => SliderChanged();
            _ry.ValueChanged += (_, __) => SliderChanged();
            _ll.ValueChanged += (_, __) => SliderChanged();
            _rl.ValueChanged += (_, __) => SliderChanged();
        }

        private void SliderChanged()
        {
            if (_suppress > 0) return;
            int idx = _profileBox.SelectedIndex;
            if (idx < 0) return;
            var p = Profiles[idx];
            p.LeftX = _lx.Value; p.LeftY = _ly.Value;
            p.RightX = _rx.Value; p.RightY = _ry.Value;
            p.LeftRayLength = _ll.Value; p.RightRayLength = _rl.Value;
            _lxv.Text = p.LeftX.ToString("0.000");
            _lyv.Text = p.LeftY.ToString("0.000");
            _llv.Text = p.LeftRayLength.ToString("0.00");
            _rlv.Text = p.RightRayLength.ToString("0.00");
            _rxv.Text = p.RightX.ToString("0.000");
            _ryv.Text = p.RightY.ToString("0.000");

            AppSettings.Save();
            AppOrchestrator.NotifyStickPointsChanged();   // refreshes points on the keyboard overlay live
        }

        private void UpdateValues()
        {
            _lxv.Text = _lx.Value.ToString("0.000");
            _lyv.Text = _ly.Value.ToString("0.000");
            _rxv.Text = _rx.Value.ToString("0.000");
            _ryv.Text = _ry.Value.ToString("0.000");
            _llv.Text = _ll.Value.ToString("0.00");
            _rlv.Text = _rl.Value.ToString("0.00");
        }

        private void AddProfile(bool dup)
        {
            StickPointsProfile np;
            int src = _profileBox.SelectedIndex;
            if (dup && src >= 0 && src < Profiles.Count)
            {
                var json = JsonSerializer.Serialize(Profiles[src]);
                np = JsonSerializer.Deserialize<StickPointsProfile>(json) ?? new StickPointsProfile();
                np.Name = np.Name + " copy";
            }
            else np = new StickPointsProfile { Name = "Stick points profile " + (Profiles.Count + 1) };
            int insert = Math.Min(Math.Max(src, 0) + 1, Profiles.Count);
            Profiles.Insert(insert, np);
            AppSettings.Instance.ActiveStickPointsProfile = insert;
            AppSettings.Save();
            AppOrchestrator.NotifyStickPointsChanged();
            ReloadProfileList();
        }

        private void DeleteProfile()
        {
            if (Profiles.Count <= 1) return;
            int idx = _profileBox.SelectedIndex;
            if (idx < 0) return;
            Profiles.RemoveAt(idx);
            AppSettings.Instance.ActiveStickPointsProfile = Math.Clamp(idx - 1, 0, Profiles.Count - 1);
            AppSettings.Save();
            AppOrchestrator.NotifyStickPointsChanged();
            ReloadProfileList();
        }

        private void CommitName()
        {
            int idx = _profileBox.SelectedIndex;
            string name = _nameBox.Text.Trim();
            if (idx < 0 || string.IsNullOrEmpty(name) || Profiles[idx].Name == name) return;
            Profiles[idx].Name = name;
            _suppress++;
            _profileBox.Items[idx] = name + (idx == AppSettings.Instance.ActiveStickPointsProfile ? "  (active)" : "");
            _suppress--;
            AppSettings.Save();
            AppOrchestrator.NotifyStickPointsChanged();
        }
    }
}
