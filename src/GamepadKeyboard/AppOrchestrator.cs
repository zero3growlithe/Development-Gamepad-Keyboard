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
        private readonly ToastOverlay _toast = new();
        private NotifyIcon? _tray;
        private ToolStripMenuItem? _overlayItem;

        public AppOrchestrator()
        {
            Keyboard.KeyboardLayout layout = new();
            layout.Build();
            _mapper = new ControllerMapper(layout);
            _keyboard = new KeyboardOverlay(layout);

            _pad.StateChanged += OnPad;
            _mapper.StateChanged += RefreshUi;
            _mapper.Notification += msg =>
                _keyboard.Dispatcher.BeginInvoke(() =>
                    _toast.Show(msg,
                        Settings.AppSettings.Instance.ProfileToastSeconds,
                        Settings.AppSettings.Instance.ProfileToastPermanent));
            _mapper.Notification += msg =>
                _keyboard.Dispatcher.BeginInvoke(() =>
                    _toast.Show(msg,
                        Settings.AppSettings.Instance.ProfileToastSeconds,
                        Settings.AppSettings.Instance.ProfileToastPermanent));
        }

        public void Start()
        {
            BuildTray();

            _keyboard.SetProfileName(Settings.AppSettings.Instance.Profile.Name);
            _keyboard.SetPointPositions();

            if (Settings.AppSettings.Instance.ShowOverlay)
                ShowKeyboard();
            else
                _keyboard.Hide();

            RefreshLegend();

            _toast.Show(
                Settings.AppSettings.Instance.StartInMouseMode ? "Mouse mode" : "Keyboard mode",
                Settings.AppSettings.Instance.ProfileToastSeconds,
                Settings.AppSettings.Instance.ProfileToastPermanent);
            _mapper.MouseMode = Settings.AppSettings.Instance.StartInMouseMode;
        }

        private void ShowKeyboard()
        {
            _keyboard.SetProfileName(Settings.AppSettings.Instance.Profile.Name);
            _keyboard.SetPointPositions();
            _keyboard.Show();
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

            var overlayItem = new ToolStripMenuItem("Show keyboard");
            overlayItem.CheckOnClick = true;
            overlayItem.Checked = Settings.AppSettings.Instance.ShowOverlay;
            overlayItem.Click += (_, __) =>
            {
                bool show = overlayItem.Checked;
                Settings.AppSettings.Instance.ShowOverlay = show;
                Settings.AppSettings.Save();
                if (show) ShowKeyboard(); else _keyboard.Hide();
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

            menu.Items.Add(overlayItem);
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

        private static Icon LoadIcon()
        {
            // tiny embedded 16x16 icon drawn procedurally (no external asset needed)
            using var bmp = new Bitmap(16, 16);
            using (var g = Graphics.FromImage(bmp))
            {
                g.Clear(Color.FromArgb(24, 24, 32));
                using var p1 = new Pen(Color.DeepSkyBlue, 2);
                g.DrawRectangle(p1, 2, 5, 5, 4);
                g.DrawRectangle(p1, 9, 5, 5, 4);
                using var b = new SolidBrush(Color.Orange);
                g.FillRectangle(b, 4, 10, 8, 3);
            }
            return Icon.FromHandle(bmp.GetHicon());
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
            RefreshLegend();

            // overlay visibility follows the setting (ToggleOverlay action / tray)
            bool wantShown = Settings.AppSettings.Instance.ShowOverlay;
            if (wantShown && !_keyboard.IsVisible) _keyboard.Show();
            if (!wantShown && _keyboard.IsVisible) _keyboard.Hide();
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
            if (st.ShowButtonLegend)
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