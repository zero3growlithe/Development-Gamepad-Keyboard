using System;
using System.Collections.Generic;
using System.Drawing;
using System.Windows.Forms;
using GamepadKeyboard.Native;
using GamepadKeyboard.Overlay;
using GamepadKeyboard.UI;
using WFApplication = System.Windows.Forms.Application;

namespace GamepadKeyboard
{
    /// <summary>
    /// Composes everything: gamepad polling → ControllerMapper → overlay UI,
    /// tray icon with menu, and startup plumbing.
    /// </summary>
    public sealed class AppOrchestrator : IDisposable
    {
        private readonly Input.GamepadService _pad = new();
        private readonly ControllerMapper _mapper;
        private readonly KeyboardOverlay _keyboard;
        private readonly LegendOverlay _legend = new();
        private NotifyIcon? _tray;
        private ToolStripMenuItem? _overlayItem;
        private ToolStripMenuItem? _enabledItem;

        private static System.Windows.MessageBoxButton MessageBoxButton_OK() => System.Windows.MessageBoxButton.OK;
        private static System.Windows.MessageBoxImage MessageBoxImage_Information() => System.Windows.MessageBoxImage.Information;
        private bool _settingsDirty;
        private readonly System.Windows.Threading.DispatcherTimer _saveTimer =
            new() { Interval = TimeSpan.FromSeconds(2) };

        public AppOrchestrator()
        {
            _current = this;
            Keyboard.KeyboardLayout layout = new();
            layout.Build();
            _mapper = new ControllerMapper(layout);
            _keyboard = new KeyboardOverlay(layout);

            _pad.StateChanged += OnPad;
            _mapper.StateChanged += RefreshUi;
            _mapper.Notification += msg =>
                _keyboard.Dispatcher.BeginInvoke(() =>
                {
                    _keyboard.ShowStatus(msg,
                        Settings.AppSettings.Instance.ProfileToastSeconds,
                        Settings.AppSettings.Instance.ProfileToastPermanent);
                    if (_enabledItem != null)
                        _enabledItem.Checked = _mapper.InputEnabled;
                });

            // debounce settings writes while dragging with the sticks
            _saveTimer.Tick += (_, __) =>
            {
                if (_settingsDirty)
                {
                    _settingsDirty = false;
                    Settings.AppSettings.Save();
                }
            };
            _saveTimer.Start();
        }



        public void Start()
        {
            BuildTray();

            _keyboard.SetProfileName(Settings.AppSettings.Instance.Profile.Name);
            _keyboard.SetPointPositions();

            // launch state: input disabled (pad free for games) and no GUI visible;
            // the enable combo (PS+Menu+Select / L3+R3+L1+R1) starts the tool normally
            _keyboard.Hide();
            _legend.Hide();
            _mapper.MouseMode = Settings.AppSettings.Instance.StartInMouseMode;
            _enabledItem!.Checked = false;   // tray checkbox reflects the disabled start
        }

        /// <summary>Static bridge for editor windows: reload profile-dependent UI.</summary>
        public static void NotifyMappingsChanged()
        {
            var o = _current;
            if (o == null) return;
            var d = o._keyboard?.Dispatcher;
            if (d == null) return;
            d.BeginInvoke(() =>
            {
                if (o._keyboard == null) return;
                o._keyboard.RebuildAndResize();
                o._keyboard.SetPointPositions();
                o._keyboard.SetProfileName(Settings.AppSettings.Instance.Profile.Name);
                o.RefreshUiCore();
            });
        }

        private static AppOrchestrator? _current;

        private bool _keyboardShown;

        private void ShowKeyboard()
        {
            _keyboard.SetProfileName(Settings.AppSettings.Instance.Profile.Name);
            _keyboard.SetPointPositions();
            _keyboard.Show();
            _keyboardShown = true;
        }

        private void BuildTray()
        {
            _tray = new NotifyIcon
            {
                Icon = LoadIcon(),
                Text = "Development Gamepad Keyboard",
                Visible = true
            };

            var menu = new ContextMenuStrip();

            var enabledItem = new ToolStripMenuItem("Input enabled");
            enabledItem.CheckOnClick = true;
            enabledItem.Checked = _mapper.InputEnabled;   // false at launch (disabled start)
            enabledItem.Click += (_, __) => _mapper.SetInputEnabled(enabledItem.Checked);
            _enabledItem = enabledItem;

            var monitorItem = new ToolStripMenuItem("Input monitor…");
            monitorItem.Click += (_, __) =>
                UI.InputMonitorWindow.ShowSingleton(_pad, _mapper);

            var diagItem = new ToolStripMenuItem("Diagnostics…");
            diagItem.Click += (_, __) =>
            {
                var lines = new System.Collections.Generic.List<string>(_pad.Diagnostics())
                {
                    "input enabled: " + _mapper.InputEnabled,
                    "mouse mode: " + _mapper.MouseMode
                };
                System.Windows.MessageBox.Show(string.Join(Environment.NewLine, lines),
                    "Gamepad diagnostics", MessageBoxButton_OK(), MessageBoxImage_Information());
            };

            var overlayItem = new ToolStripMenuItem("Show keyboard");
            overlayItem.CheckOnClick = true;
            overlayItem.Checked = Settings.AppSettings.Instance.ShowOverlay;
            overlayItem.Click += (_, __) =>
            {
                bool show = overlayItem.Checked;
                Settings.AppSettings.Instance.ShowOverlay = show;
                Settings.AppSettings.Save();
                if (show) ShowKeyboard(); else { _keyboardShown = false; _keyboard.Hide(); }
            };
            _overlayItem = overlayItem;

            var adminItem = new ToolStripMenuItem();
            adminItem.Text = Util.LaunchUtil.IsAdmin() ? "Run as User" : "Run as Administrator";
            adminItem.Click += (_, __) =>
            {
                if (Util.LaunchUtil.IsAdmin())
                {
                    Util.LaunchUtil.RestartAsUser();
                }
                else
                {
                    Settings.AppSettings.Instance.AdminLaunch = true;
                    Settings.AppSettings.Save();
                    Util.LaunchUtil.RestartElevated();
                }
                Exit();
            };

            var startupItem = new ToolStripMenuItem("Run on startup");
            startupItem.CheckOnClick = true;
            startupItem.Checked = Util.LaunchUtil.StartupShortcutExists();
            startupItem.Click += (_, __) =>
            {
                bool on = startupItem.Checked;
                Settings.AppSettings.Instance.RunOnStartup = on;
                Settings.AppSettings.Save();
                if (on) Util.LaunchUtil.CreateStartupShortcut();
                else Util.LaunchUtil.RemoveStartupShortcut();
            };

            var settingsItem = new ToolStripMenuItem("Settings…");
            settingsItem.Font = new Font(settingsItem.Font, System.Drawing.FontStyle.Bold);
            settingsItem.Click += (_, __) => SettingsWindow.ShowSingleton();

            var aboutItem = new ToolStripMenuItem("About…");
            aboutItem.Click += (_, __) => AboutWindow.ShowSingleton();

            var exitItem = new ToolStripMenuItem("Exit");
            exitItem.Click += (_, __) => Exit();

            menu.Items.Add(enabledItem);
            menu.Items.Add(overlayItem);
            menu.Items.Add(monitorItem);
            menu.Items.Add(diagItem);
            menu.Items.Add(settingsItem);
            menu.Items.Add(aboutItem);
            menu.Items.Add(new ToolStripSeparator());
            menu.Items.Add(adminItem);
            menu.Items.Add(startupItem);
            menu.Items.Add(new ToolStripSeparator());
            menu.Items.Add(exitItem);

            _tray.ContextMenuStrip = menu;
            _tray.DoubleClick += (_, __) => SettingsWindow.ShowSingleton();
        }

        /// <summary>Tray item used by the ToggleOverlay action to sync the checkmark.</summary>
        public void SetOverlayChecked(bool isChecked)
        {
            var item = _overlayItem;
            if (item == null) return;
            _keyboard.Dispatcher.BeginInvoke(new Action(() => item.Checked = isChecked));
        }

        private static Icon? _appIcon;

        private static Icon LoadIcon()
        {
            // the exe's own Win32 icon (ApplicationIcon in csproj = gamepad-keyboard device icon)
            if (_appIcon != null) return _appIcon;
            try
            {
                string exe = System.Reflection.Assembly.GetExecutingAssembly().Location;
                if (!string.IsNullOrEmpty(exe))
                {
                    _appIcon = Icon.ExtractAssociatedIcon(exe);
                    if (_appIcon != null) return _appIcon;
                }
            }
            catch { /* fall back to procedural */ }
            using var bmp = new Bitmap(16, 16);
            using (var g = Graphics.FromImage(bmp))
            {
                g.Clear(Color.FromArgb(24, 24, 32));
                using var p1 = new Pen(Color.DeepSkyBlue, 2);
                g.DrawRectangle(p1, 2, 5, 5, 4);
                using var b = new SolidBrush(Color.Orange);
                g.FillRectangle(b, 4, 10, 8, 3);
            }
            _appIcon = Icon.FromHandle(bmp.GetHicon());
            return _appIcon;
        }

        private void OnPad(Input.GamepadSnapshot s)
        {
            _mapper.Process(s);
            if (!_mapper.MouseMode && !_mapper.InputEnabled)
                return; // nothing to draw in pass-through
            if (!_mapper.MouseMode)
            {
                // rays move every reading — refresh on the UI thread
                _keyboard.Dispatcher.BeginInvoke(RefreshUiCore);
            }
        }

        private void RefreshUi()
        {
            var dispatcher = _keyboard.Dispatcher;
            if (dispatcher.CheckAccess())
            {
                RefreshUiCore();
            }
            else
            {
                dispatcher.BeginInvoke(RefreshUiCore);
            }
        }

        private void RefreshUiCore()
        {
            _keyboard.ClearHighlights();
            _keyboard.SetToggledKeys(_mapper.HeldModifierVks);
            RefreshLegend();

            // ── stick-driven overlay adjust: L3 hold = move (left stick), R3 hold = scale (right stick) ──
            if (_mapper.AdjustMove)
            {
                var spd = Settings.AppSettings.Instance.OverlayMoveSpeed;
                _keyboard.Left = Math.Clamp(_keyboard.Left + _mapper.MoveDX * spd, -_keyboard.Width + 80, System.Windows.SystemParameters.WorkArea.Width - 40);
                _keyboard.Top = Math.Clamp(_keyboard.Top - _mapper.MoveDY * spd, 0, System.Windows.SystemParameters.WorkArea.Height - 40);
                Settings.AppSettings.Instance.OverlayLeft = _keyboard.Left;
                Settings.AppSettings.Instance.OverlayTop = _keyboard.Top;
                _settingsDirty = true;
            }
            if (_mapper.AdjustScale)
            {
                if (Math.Abs(_mapper.ScaleDelta) > 0.15)
                {
                    _keyboard.SetScale(_keyboard.Scale + Math.Sign(_mapper.ScaleDelta) * 0.02);
                    Settings.AppSettings.Instance.OverlayScale = _keyboard.Scale;
                    _settingsDirty = true;
                }
            }

            // overlay visibility follows the setting (ToggleOverlay action / tray);
            // hidden while input disabled (gamepad free for games) AND in mouse mode
            bool wantShown = Settings.AppSettings.Instance.ShowOverlay && _mapper.InputEnabled && !_mapper.MouseMode;
            if (wantShown && !_keyboardShown)
            {
                _keyboardShown = true;
                _keyboard.Show();
            }
            if (!wantShown && _keyboardShown)
            {
                _keyboardShown = false;
                _keyboard.Hide();
            }
            // keep the tray checkmark in sync (mapped ToggleOverlay flips it too)
            var items = _tray?.ContextMenuStrip?.Items;
            if (items != null)
                foreach (System.Windows.Forms.ToolStripItem it in items)
                    if (it is System.Windows.Forms.ToolStripMenuItem mi && mi.Text == "Show keyboard")
                        mi.Checked = wantShown;
            if (!_mapper.MouseMode)
            {
                if (_mapper.LeftHit != null)
                {
                    _keyboard.UpdateRay(true, _mapper.LastLeftX, _mapper.LastLeftY, _mapper.LeftLen, _mapper.LeftHit);
                    _keyboard.HighlightKey(_mapper.LeftHit, false);
                }
                if (_mapper.RightHit != null)
                {
                    _keyboard.UpdateRay(false, _mapper.LastRightX, _mapper.LastRightY, _mapper.RightLen, _mapper.RightHit);
                    _keyboard.HighlightKey(_mapper.RightHit, false);
                }
            }
            _keyboard.SetProfileName(Settings.AppSettings.Instance.Profile.Name);
        }

        private void RefreshLegend()
        {
            var st = Settings.AppSettings.Instance;
            if (st.ShowButtonLegend && _mapper.InputEnabled)
            {
                _legend.ApplySettings(st.LegendLeft, st.LegendTop, st.LegendOpacity, st.LegendFontSize);
                _legend.SetEntries(LegendEntries());
                _legend.Show();
            }
            else
            {
                _legend.Hide();
            }
        }

        private System.Collections.Generic.IReadOnlyList<(string key, string action)> LegendEntries()
        {
            if (_mapper.MouseMode)
            {
                var p = Settings.AppSettings.Instance.MouseProfile;
                return new (string, string)[]
                {
                    ("A", p.A), ("B", p.B), ("X", p.X), ("Y", p.Y),
                    ("LB", p.LB), ("RB", p.RB), ("LT", p.LT), ("RT", p.RT),
                    ("DUp", p.DUp), ("DDown", p.DDown), ("DLeft", p.DLeft), ("DRight", p.DRight)
                };
            }
            var k = Settings.AppSettings.Instance.Profile;
            return new (string, string)[]
            {
                ("A", k.A), ("B", k.B), ("X", k.X), ("Y", k.Y),
                ("LB", k.LB), ("RB", k.RB), ("LT", k.LT), ("RT", k.RT),
                ("LStick", k.LS), ("RStick", k.RS),
                ("D-pad", k.DUp + "/" + k.DDown + "/" + k.DLeft + "/" + k.DRight),
                ("Y+D-pad", k.YDUp + "/" + k.YDDown + "/" + k.YDLeft + "/" + k.YDRight),
                ("View", k.View), ("Menu", k.Menu)
            };
        }

        public void Exit()
        {
            Dispose();
            System.Windows.Application.Current?.Shutdown();
        }

        public void Dispose()
        {
            if (_current == this) _current = null;
            _pad.Dispose();
            if (_tray != null)
            {
                _tray.Visible = false;
                _tray.Dispose();
                _tray = null;
            }
        }
    }
}