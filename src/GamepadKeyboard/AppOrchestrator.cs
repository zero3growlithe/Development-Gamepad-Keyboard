using System;
using System.Collections.Generic;
using System.Linq;
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
        private readonly HidHideSession _hidHide = new();
        private readonly KeyboardOverlay _keyboard;
        private readonly StatusToastOverlay _toast = new();
        private readonly LegendOverlay _legend = new();
        private NotifyIcon? _tray;
        private ToolStripMenuItem? _enabledItem;

        private static System.Windows.MessageBoxButton MessageBoxButton_OK() => System.Windows.MessageBoxButton.OK;
        private static System.Windows.MessageBoxImage MessageBoxImage_Information() => System.Windows.MessageBoxImage.Information;
        private bool _settingsDirty;
        private long _lastUiRefreshTimestamp;
        private int _uiRefreshQueued;
        private static readonly long UiRefreshInterval = Math.Max(1, System.Diagnostics.Stopwatch.Frequency / 60);
        private readonly System.Windows.Threading.DispatcherTimer _saveTimer =
            new() { Interval = TimeSpan.FromSeconds(2) };

        public AppOrchestrator()
        {
            _current = this;
            Keyboard.KeyboardLayout layout = new();
            layout.Build();
            _mapper = new ControllerMapper(layout);
            Input.GamepadService.MouseModeProbe = () => _mapper.MouseMode;   // per-mode deadzone
            Input.GamepadService.InputEnabledProbe = () => _mapper.InputEnabled;
            _keyboard = new KeyboardOverlay(layout);

            _pad.StateChanged += OnPad;
            _mapper.StateChanged += RefreshUi;
            _mapper.InputEnabledChanged += OnInputEnabledChanged;
            _mapper.Notification += msg =>
                _keyboard.Dispatcher.BeginInvoke(() =>
                {
                    _toast.ShowStatus(msg,
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

            var recovery = _hidHide.RecoverLegacyClaim();
            if (!recovery.Success)
                ShowHidHideError(recovery.Error ?? "legacy cleanup failed.");
            else if (!string.IsNullOrWhiteSpace(recovery.Warning))
                ShowHidHideWarning(recovery.Warning);
            _pad.ResetDeviceCaches();

            _keyboard.SetProfileName(Settings.AppSettings.Instance.Profile.Name);
            _keyboard.SetPointPositions();

            // launch state: input disabled (pad free for games) and no GUI visible;
            // a profile-defined EnableInput binding starts the tool normally
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

        public static void NotifyStickPointsChanged()
        {
            _current?._mapper.ResetKeyboardCursors();
            NotifyMappingsChanged();
        }

        public static void NotifyHidHideSettingsChanged()
        {
            var o = _current;
            if (o == null) return;
            o.ApplyHidHideState(o._mapper.InputEnabled, refresh: true);
        }

        private static AppOrchestrator? _current;

        private bool _keyboardShown;

        private void ShowKeyboard()
        {
            _mapper.ResetKeyboardCursors();
            _keyboard.SetProfileName(Settings.AppSettings.Instance.Profile.Name);
            _keyboard.SetPointPositions();
            if (Settings.AppSettings.Instance.AlwaysShowKeyboardAtCursorPosition)
                PositionKeyboardAtCursor();
            _keyboard.Show();
            _keyboardShown = true;
        }

        private void PositionKeyboardAtCursor()
        {
            var cursor = Cursor.Position;
            var screen = Screen.FromPoint(cursor);
            var dpi = NativeMethods.EffectiveMonitorDpi(cursor.X, cursor.Y);
            double scaleX = 96.0 / dpi.x;
            double scaleY = 96.0 / dpi.y;
            double workLeft = screen.WorkingArea.Left * scaleX;
            double workTop = screen.WorkingArea.Top * scaleY;
            double workRight = screen.WorkingArea.Right * scaleX;
            double workBottom = screen.WorkingArea.Bottom * scaleY;
            double maxLeft = Math.Max(workLeft, workRight - _keyboard.Width);
            double maxTop = Math.Max(workTop, workBottom - _keyboard.Height);
            _keyboard.Left = Math.Clamp(cursor.X * scaleX, workLeft, maxLeft);
            _keyboard.Top = Math.Clamp(cursor.Y * scaleY, workTop, maxTop);
        }

        private void ResetKeyboardPosition()
        {
            const double defaultLeft = 100;
            const double defaultTop = 100;
            _keyboard.Left = defaultLeft;
            _keyboard.Top = defaultTop;
            Settings.AppSettings.Instance.OverlayLeft = defaultLeft;
            Settings.AppSettings.Instance.OverlayTop = defaultTop;
            Settings.AppSettings.Save();
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
                    "mouse mode: " + _mapper.MouseMode,
                    "HidHide reservation: " +
                        (Settings.AppSettings.Instance.HidHideSessionEnabled
                            ? (_hidHide.IsClaimed ? _hidHide.ModeDescription : "enabled, not claimed")
                            : "disabled")
                };
                System.Windows.MessageBox.Show(string.Join(Environment.NewLine, lines),
                    "Gamepad diagnostics", MessageBoxButton_OK(), MessageBoxImage_Information());
            };

            var resetKeyboardPositionItem = new ToolStripMenuItem("Reset keyboard position");
            resetKeyboardPositionItem.Click += (_, __) => ResetKeyboardPosition();

            var adminItem = new ToolStripMenuItem();
            adminItem.Text = Util.LaunchUtil.IsAdmin() ? "Run as User" : "Run as Administrator";
            adminItem.Click += (_, __) =>
            {
                if (Util.LaunchUtil.IsAdmin())
                {
                    Settings.AppSettings.Instance.AdminLaunch = false;
                    Settings.AppSettings.Save();
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

            var startupItem = new ToolStripMenuItem("Run on Windows startup");
            startupItem.CheckOnClick = true;
            startupItem.Checked = Util.LaunchUtil.StartupShortcutExists()
                || Util.LaunchUtil.AdminStartupExists();
            startupItem.Click += (_, __) =>
            {
                bool on = startupItem.Checked;
                Settings.AppSettings.Instance.RunOnStartup = on;
                Settings.AppSettings.Save();
                bool elevatedStartup = on
                    && Settings.AppSettings.Instance.AdminLaunch
                    && Util.LaunchUtil.IsAdmin();
                if (Util.LaunchUtil.IsAdmin())
                    Util.LaunchUtil.SetAdminStartup(elevatedStartup);
                if (on && !elevatedStartup) Util.LaunchUtil.CreateStartupShortcut();
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
            menu.Items.Add(resetKeyboardPositionItem);
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
                // Input is sampled faster than the display can render. Coalesce UI
                // work to 60 Hz instead of queueing a WPF pass for every poll.
                long now = System.Diagnostics.Stopwatch.GetTimestamp();
                long previous = System.Threading.Interlocked.Read(ref _lastUiRefreshTimestamp);
                if (now - previous < UiRefreshInterval) return;
                if (System.Threading.Interlocked.CompareExchange(ref _uiRefreshQueued, 1, 0) != 0) return;
                System.Threading.Interlocked.Exchange(ref _lastUiRefreshTimestamp, now);
                _keyboard.Dispatcher.BeginInvoke(new Action(() =>
                {
                    System.Threading.Interlocked.Exchange(ref _uiRefreshQueued, 0);
                    RefreshUiCore(refreshStatic: false);
                }));
            }
        }

        private void OnInputEnabledChanged(bool enabled)
        {
            ApplyHidHideState(enabled, refresh: false);
        }

        private void ApplyHidHideState(bool inputEnabled, bool refresh)
        {
            HidHideResult result;
            bool wasLegacy = _hidHide.IsLegacyClaimed;
            try
            {
                result = HidHideResult.Ok;
                if (refresh && _hidHide.IsClaimed)
                    result = _hidHide.Release();

                if (result.Success)
                {
                    var settings = Settings.AppSettings.Instance;
                    result = inputEnabled && settings.HidHideSessionEnabled
                        ? _hidHide.Claim(settings.HidHideDeviceInstancePaths,
                            settings.HidHideLegacyFallbackEnabled)
                        : _hidHide.Release();
                }
            }
            catch (Exception ex)
            {
                result = HidHideResult.Failure("controller reservation failed unexpectedly (" + ex.Message + ").");
            }

            if (result.Success)
            {
                if (wasLegacy || _hidHide.IsLegacyClaimed)
                    _pad.ResetDeviceCaches();
                App.Log("HidHide reservation: " + (_hidHide.IsClaimed ? _hidHide.ModeDescription : "released"));
                if (!string.IsNullOrWhiteSpace(result.Warning))
                {
                    ShowHidHideWarning(result.Warning);
                }
                else if (_hidHide.IsLegacyClaimed)
                {
                    _keyboard.Dispatcher.BeginInvoke(
                        System.Windows.Threading.DispatcherPriority.Background,
                        new Action(() => _toast.ShowStatus(
                            "HidHide: using legacy persistent fallback; DisableInput or exit restores it.",
                            Settings.AppSettings.Instance.ProfileToastSeconds,
                            permanent: false)));
                }
                return;
            }

            ShowHidHideError(result.Error ?? "controller reservation failed.");
        }

        private void ShowHidHideWarning(string warning)
        {
            string message = "HidHide: " + warning;
            App.Log(message);
            _keyboard.Dispatcher.BeginInvoke(
                System.Windows.Threading.DispatcherPriority.Background,
                new Action(() => _toast.ShowStatus(message,
                    Math.Max(8, Settings.AppSettings.Instance.ProfileToastSeconds),
                    permanent: false)));
        }

        private void ShowHidHideError(string error)
        {
            string message = "HidHide: " + error;
            App.Log(message);
            _keyboard.Dispatcher.BeginInvoke(
                System.Windows.Threading.DispatcherPriority.Background,
                new Action(() => _toast.ShowStatus(message,
                    Settings.AppSettings.Instance.ProfileToastSeconds,
                    permanent: false)));
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
                dispatcher.BeginInvoke(new Action(() => RefreshUiCore()));
            }
        }

        private void RefreshUiCore(bool refreshStatic = true)
        {
            _keyboard.ClearHighlights();
            if (refreshStatic)
            {
                _keyboard.SetToggledKeys(_mapper.HeldModifierVks.Concat(_mapper.HeldRayKeyVks));
                _keyboard.SetShiftActive(_mapper.ShiftActive);
                _keyboard.SetPointPositions();
                RefreshLegend();
            }

            // ── stick-driven overlay adjust (buttons are profile-defined actions) ──
            if (_mapper.AdjustMoveScaleKeyboard)
            {
                var spd = Settings.AppSettings.Instance.OverlayMoveSpeed;
                if (Math.Abs(_mapper.MoveDX) > 0.01 || Math.Abs(_mapper.MoveDY) > 0.01)
                {
                    _keyboard.Left = Math.Clamp(_keyboard.Left + _mapper.MoveDX * spd, -_keyboard.Width + 80, System.Windows.SystemParameters.WorkArea.Width - 40);
                    _keyboard.Top = Math.Clamp(_keyboard.Top - _mapper.MoveDY * spd, 0, System.Windows.SystemParameters.WorkArea.Height - 40);
                    Settings.AppSettings.Instance.OverlayLeft = _keyboard.Left;
                    Settings.AppSettings.Instance.OverlayTop = _keyboard.Top;
                    _settingsDirty = true;
                }
                if (Math.Abs(_mapper.ScaleDelta) > 0.01)
                {
                    _keyboard.SetScale(_keyboard.Scale + _mapper.ScaleDelta * 0.02);
                    Settings.AppSettings.Instance.OverlayScale = _keyboard.Scale;
                    _settingsDirty = true;
                }
            }

            // overlay visibility follows the setting (ToggleOverlay action / tray);
            // hidden while input disabled (gamepad free for games) AND in mouse mode
            if (refreshStatic)
            {
                bool wantShown = Settings.AppSettings.Instance.ShowOverlay && _mapper.InputEnabled && !_mapper.MouseMode;
                if (wantShown && !_keyboardShown)
                {
                    ShowKeyboard();
                }
                if (!wantShown && _keyboardShown)
                {
                    _keyboardShown = false;
                    _keyboard.Hide();
                }
                _keyboard.SetProfileName(Settings.AppSettings.Instance.Profile.Name);
            }
            if (!_mapper.MouseMode)
            {
                _keyboard.UpdateCursor(true, _mapper.LeftRayX, _mapper.LeftRayY,
                    _mapper.LeftCursorX, _mapper.LeftCursorY, _mapper.LeftCursorActive);
                _keyboard.UpdateCursor(false, _mapper.RightRayX, _mapper.RightRayY,
                    _mapper.RightCursorX, _mapper.RightCursorY, _mapper.RightCursorActive);
                if (_mapper.LeftHit != null)
                {
                    _keyboard.HighlightKey(_mapper.LeftHit, false, left: true);
                }
                if (_mapper.RightHit != null)
                {
                    _keyboard.HighlightKey(_mapper.RightHit, false, left: false);
                }
            }
        }

        private void RefreshLegend()
        {
            var st = Settings.AppSettings.Instance;
            if (st.ShowButtonLegend && _mapper.InputEnabled)
            {
                _legend.ApplySettings(st.LegendLeft, st.LegendTop, st.LegendOpacity, st.LegendFontSize);
                _legend.SetEntries(LegendEntries());
                if (!_legend.IsVisible) _legend.Show();
            }
            else
            {
                if (_legend.IsVisible) _legend.Hide();
            }
        }

        private System.Collections.Generic.IReadOnlyList<(string key, string action)> LegendEntries()
        {
            var bindings = _mapper.MouseMode
                ? Settings.AppSettings.Instance.MouseProfile.Bindings
                : Settings.AppSettings.Instance.Profile.Bindings;
            return bindings
                .Where(binding => binding.Buttons.Count > 0 && binding.Action != "None")
                .Select(binding => (
                    string.Join("+", binding.Buttons.Select(LegendInputName)),
                    binding.Action))
                .ToArray();
        }

        private static string LegendInputName(string input) => input switch
        {
            "LS" => "L3", "RS" => "R3",
            "DUp" => "D↑", "DDown" => "D↓", "DLeft" => "D←", "DRight" => "D→",
            "LUp" => "L↑", "LDown" => "L↓", "LLeft" => "L←", "LRight" => "L→",
            "RUp" => "R↑", "RDown" => "R↓", "RLeft" => "R←", "RRight" => "R→",
            _ => input
        };

        public void Exit()
        {
            Dispose();
            System.Windows.Application.Current?.Shutdown();
        }

        public void Dispose()
        {
            if (_current == this) _current = null;
            _mapper.InputEnabledChanged -= OnInputEnabledChanged;
            var release = _hidHide.Release();
            if (!release.Success) App.Log("HidHide shutdown release: " + release.Error);
            _hidHide.Dispose();
            _pad.Dispose();
            _toast.Close();
            if (_tray != null)
            {
                _tray.Visible = false;
                _tray.Dispose();
                _tray = null;
            }
        }
    }
}
