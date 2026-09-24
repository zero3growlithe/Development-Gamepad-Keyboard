using System;
using System.Drawing;
using System.IO;
using System.Windows;
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

        public AppOrchestrator()
        {
            Keyboard.KeyboardLayout layout = _mapper_Layout();
            _mapper = new ControllerMapper(layout);
            _keyboard = new KeyboardOverlay(layout);

            _pad.StateChanged += OnPad;
            _mapper.StateChanged += RefreshUi;
            _mapper.Notification += msg =>
                _keyboard.Dispatcher.BeginInvoke(() =>
                    _toast.Show(msg,
                        Settings.AppSettings.Instance.ProfileToastSeconds,
                        Settings.AppSettings.Instance.ProfileToastPermanent));
        }

        private static Keyboard.KeyboardLayout _mapper_Layout()
        {
            var l = new Keyboard.KeyboardLayout();
            l.Build();
            return l;
        }

        public void Start()
        {
            BuildTray();

            _keyboard.SetProfileName(Settings.AppSettings.Instance.Profile.Name);
            _keyboard.SetPointPositions();
            _keyboard.KeyClicked += _ => { /* future: keyboard-test clicks */ };

            if (Settings.AppSettings.Instance.ShowOverlay)
                _keyboard.Show();

            if (Settings.AppSettings.Instance.ShowButtonLegend)
            {
                _legend.ApplySettings(
                    Settings.AppSettings.Instance.LegendLeft,
                    Settings.AppSettings.Instance.LegendTop,
                    Settings.AppSettings.Instance.LegendOpacity,
                    Settings.AppSettings.Instance.LegendFontSize);
                _legend.SetEntries(LegendEntries());
                _legend.Show();
            }

            _toast.Show(
                Settings.AppSettings.Instance.StartInMouseMode ? "Mouse mode" : "Keyboard mode",
                Settings.AppSettings.Instance.ProfileToastSeconds,
                Settings.AppSettings.Instance.ProfileToastPermanent);
            _mapper.MouseMode = Settings.AppSettings.Instance.StartInMouseMode;
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
            if (_mapper.MouseMode || true)  // legend reflects active mode + profile
            {
                _legend.SetEntries(LegendEntries());
            }
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