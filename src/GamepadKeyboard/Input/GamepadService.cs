using System;
using System.Collections.Generic;

namespace GamepadKeyboard.Input
{
    /// <summary>
    /// Gamepad polling service. Uses Windows.Gaming.Input: RawGameController for
    /// enumeration/names, Gamepad wrapper for readings. Multi-pad aware: the pad
    /// with active input wins; otherwise the first idle pad provides zeros.
    /// Every state transition and controller change is logged to crash.log.
    /// </summary>
    public sealed class GamepadService : IDisposable
    {
        private readonly System.Threading.Thread _pollThread;
        private volatile bool _stop;
        private GamepadSnapshot _last = default;
        private int _lastRawCount = -1;
        private readonly Dictionary<string, Windows.Gaming.Input.Gamepad> _padCache = new();
        private string _lastSummary = "";
        private DateTime _lastLog = DateTime.MinValue;
        private string _lastActivePad = "";

        public event Action<GamepadSnapshot>? StateChanged;

        public int PollHz { get; set; } = 250;

        public GamepadService()
        {
            // dedicated high-priority thread: never throttled when the app is
            // backgrounded (threadpool timers can be coalesced/delayed for tray apps)
            _pollThread = new System.Threading.Thread(PollLoop)
            {
                IsBackground = true,
                Priority = System.Threading.ThreadPriority.AboveNormal,
                Name = "GamepadPoll"
            };
            _pollThread.Start();
        }

        private void PollLoop()
        {
            // 1 ms system timer resolution so Sleep(4) is actually ~4 ms
            try { Native.NativeMethods.TimeBeginPeriod(1); } catch { }
            while (!_stop)
            {
                Poll(null);
                System.Threading.Thread.Sleep(4);
            }
            try { Native.NativeMethods.TimeEndPeriod(1); } catch { }
        }

        /// <summary>Optional mode probe (true = mouse mode) for the per-mode stick deadzone.</summary>
        public static Func<bool>? MouseModeProbe;

        private void Poll(object? state)
        {
            try
            {
                var app = Settings.AppSettings.Instance;
                bool mouse = MouseModeProbe?.Invoke() ?? false;
                GamepadSnapshot.Deadzone = mouse ? app.MouseStickDeadzone : app.StickDeadzone;   // live per-mode value
                var raws = Windows.Gaming.Input.RawGameController.RawGameControllers;

                if (raws.Count != _lastRawCount)
                {
                    _lastRawCount = raws.Count;
                    var names = new List<string>();
                    foreach (var r in raws)
                        names.Add("\"" + r.DisplayName + "\" (" + r.ButtonCount + " btn, " + r.AxisCount + " axes)");
                    App.Log("controllers changed: " + raws.Count + (names.Count > 0 ? " -> " + string.Join(" | ", names) : ""));
                }

                GamepadSnapshot chosen = default;
                string chosenName = "";
                bool foundIdle = false;
                bool anyInput = false;
                bool wgiAnyInput = false;

                foreach (var raw in raws)
                {
                    GamepadSnapshot snap;
                    var gp = GetPad(raw);
                    if (gp != null)
                    {
                        snap = GamepadSnapshot.From(gp.GetCurrentReading(), ReadHome(raw));
                    }
                    else
                    {
                        // no Gamepad wrapper for this device (e.g. DualSense outside
                        // DS4Windows) — read the raw controller directly, calibrated
                        var (cal, map) = GetCalibration(raw);
                        snap = GamepadSnapshot.FromRaw(raw, ReadHome(raw), cal, map);
                    }
                    if (snap.AnyInput)
                    {
                        chosen = snap; chosenName = raw.DisplayName; anyInput = true; wgiAnyInput = true;
                        break;
                    }
                    if (!foundIdle)
                    {
                        chosen = snap; chosenName = raw.DisplayName; foundIdle = true;
                    }
                }

                // ── XInput fallback: WGI enumeration works but readings stay idle when
                //    the real pad is hidden (HidHide) or consumed (DS4Windows / Steam Input
                //    virtual pads). The virtual Xbox pad is always reachable via XInput. ──
                if (!wgiAnyInput && Native.XInput.Available)
                {
                    for (int i = 0; i < 4; i++)
                    {
                        var st = new Native.XInput.XINPUT_STATE();
                        int err = Native.XInput.GetState(i, ref st);
                        if (err != 0) continue;
                        var snap = GamepadSnapshot.FromXInput(st);
                        if (snap.AnyInput)
                        {
                            chosen = snap; chosenName = "XInput slot " + i; anyInput = true;
                            if (_loggedExtras.Add("xinput-slot" + i))
                                App.Log("WGI readings idle -> using XInput slot " + i + " (hidden/virtual pad)");
                            break;
                        }
                    }
                }

                _last = chosen;

                if (anyInput)
                {
                    var now = DateTime.UtcNow;
                    var sum = chosen.Summary;
                    if (sum != _lastSummary && (now - _lastLog).TotalMilliseconds > 1500)
                    {
                        _lastLog = now;
                        _lastSummary = sum;
                        if (chosenName != _lastActivePad)
                        {
                            _lastActivePad = chosenName;
                            App.Log("active pad: \"" + chosenName + "\"");
                        }
                        App.Log("input: " + sum);
                    }
                }

                StateChanged?.Invoke(chosen);
            }
            catch (Exception ex)
            {
                App.Log("poll error: " + ex.Message);
            }
        }

        /// <summary>
        /// Best-effort PS/Xbox home-button detection: DualSense/DualShock expose the
        /// PS button (and mic mute) as extra raw buttons beyond the standard 14.
        /// Buttons with a label (paddles etc.) are ignored; unlabeled extras count as
        /// Home. Each candidate index is logged once so crash.log shows ground truth.
        /// </summary>
        private static readonly HashSet<string> _loggedExtras = new();

        private static bool ReadHome(Windows.Gaming.Input.RawGameController raw)
        {
            if (raw.ButtonCount <= 14) return false;
            var buttons = new bool[raw.ButtonCount];
            var switches = new Windows.Gaming.Input.GameControllerSwitchPosition[raw.SwitchCount];
            var axes = new double[raw.AxisCount];
            raw.GetCurrentReading(buttons, switches, axes);
            // one-time dump of extra button labels for this pad (crash.log ground truth)
            if (_loggedExtras.Add(raw.DisplayName + "#labels"))
            {
                var lbls = new List<string>();
                for (int i = 14; i < buttons.Length; i++)
                    lbls.Add(i + "=" + raw.GetButtonLabel(i));
                App.Log("extra buttons on \"" + raw.DisplayName + "\": " + string.Join(",", lbls));
            }
            bool any = false;
            for (int i = 14; i < buttons.Length; i++)
            {
                var label = raw.GetButtonLabel(i);
                if (label != Windows.Gaming.Input.GameControllerButtonLabel.None) continue;
                if (buttons[i])
                {
                    any = true;
                    string key = raw.DisplayName + "#press" + i;
                    if (_loggedExtras.Add(key))
                        App.Log("PS/Xbox (home) candidate: raw button " + i + " on \"" + raw.DisplayName + "\"");
                }
            }
            return any;
        }

        private readonly Dictionary<string, AxisCalibration> _calibrations = new();
        private readonly Dictionary<string, RawButtonMap> _buttonMaps = new();

        private (AxisCalibration cal, RawButtonMap map) GetCalibration(Windows.Gaming.Input.RawGameController raw)
        {
            if (_calibrations.TryGetValue(raw.DisplayName, out var existing))
                return (existing, _buttonMaps[raw.DisplayName]);
            var buttons = new bool[raw.ButtonCount];
            var switches = new Windows.Gaming.Input.GameControllerSwitchPosition[raw.SwitchCount];
            var axes = new double[raw.AxisCount];
            raw.GetCurrentReading(buttons, switches, axes);
            var cal = new AxisCalibration((double[])axes.Clone());
            var map = RawButtonMap.Detect(raw);
            _calibrations[raw.DisplayName] = cal;
            _buttonMaps[raw.DisplayName] = map;
            // one-time ground-truth dump: ALL button labels + axis neutrals
            var lbls = new List<string>();
            for (int i = 0; i < raw.ButtonCount; i++)
                lbls.Add(i + "=" + raw.GetButtonLabel(i));
            App.Log("button labels \"" + raw.DisplayName + "\": " + string.Join(",", lbls));
            App.Log("raw axis calibration \"" + raw.DisplayName + "\": neutral=[" +
                    string.Join(",", System.Linq.Enumerable.Select(axes, a => a.ToString("0.00"))) + "] map=" +
                    (map == RawButtonMap.Sony ? "Sony" : "Generic"));
            return (cal, map);
        }

        private Windows.Gaming.Input.Gamepad? GetPad(Windows.Gaming.Input.RawGameController raw)
        {
            if (_padCache.TryGetValue(raw.DisplayName, out var cached))
                return cached;
            var gp = Windows.Gaming.Input.Gamepad.FromGameController(raw);
            if (gp != null) _padCache[raw.DisplayName] = gp;
            else App.Log("no Gamepad wrapper for \"" + raw.DisplayName + "\" -> raw reading path");
            return gp;
        }

        /// <summary>Live per-controller readings for the input monitor window.</summary>
        public System.Collections.Generic.List<string> MonitorLines()
        {
            var lines = new System.Collections.Generic.List<string>
            {
                "input path: " + (_lastActivePad.StartsWith("XInput") ? "XINPUT (fallback)" : "WGI"),
                ""
            };
            var raws = Windows.Gaming.Input.RawGameController.RawGameControllers;
            if (raws.Count == 0)
            {
                lines.Add("no WGI controllers");
                return lines;
            }
            foreach (var raw in raws)
            {
                var buttons = new bool[raw.ButtonCount];
                var switches = new Windows.Gaming.Input.GameControllerSwitchPosition[raw.SwitchCount];
                var axes = new double[raw.AxisCount];
                raw.GetCurrentReading(buttons, switches, axes);
                int pressed = 0;
                for (int i = 0; i < buttons.Length; i++) if (buttons[i]) pressed++;
                string axesStr = "";
                for (int i = 0; i < axes.Length; i++)
                    axesStr += (i > 0 ? " " : "") + axes[i].ToString("+0.00;-0.00");
                lines.Add("\"" + raw.DisplayName + "\"");
                lines.Add("  buttons down: " + pressed + "/" + raw.ButtonCount + "   axes: " + axesStr);
            }
            lines.Add("");
            if (Native.XInput.Available)
            {
                for (int i = 0; i < 4; i++)
                {
                    var st = new Native.XInput.XINPUT_STATE();
                    int err = Native.XInput.GetState(i, ref st);
                    if (err != 0) { lines.Add("XInput " + i + ": —"); continue; }
                    lines.Add("XInput " + i + ": packet " + st.dwPacketNumber +
                              "  LX=" + (st.Game.sThumbLX / 32768.0).ToString("+0.00;-0.00") +
                              " LY=" + (st.Game.sThumbLY / 32768.0).ToString("+0.00;-0.00") +
                              " RX=" + (st.Game.sThumbRX / 32768.0).ToString("+0.00;-0.00") +
                              " RY=" + (st.Game.sThumbRY / 32768.0).ToString("+0.00;-0.00") +
                              " btn=0x" + st.Game.wButtons.ToString("X4") +
                              " LT=" + st.Game.bLeftTrigger + " RT=" + st.Game.bRightTrigger);
                }
            }
            else
            {
                lines.Add("XInput: not available");
            }
            return lines;
        }

        /// <summary>One-shot state dump for the tray diagnostics item.</summary>
        public string[] Diagnostics()
        {
            var raws = Windows.Gaming.Input.RawGameController.RawGameControllers;
            var lines = new List<string>
            {
                "RawGameControllers: " + raws.Count,
                "Gamepad wrappers: " + Windows.Gaming.Input.Gamepad.Gamepads.Count
            };
            foreach (var r in raws)
            {
                string extras = "";
                if (r.ButtonCount > 14)
                {
                    var lbls = new List<string>();
                    for (uint i = 14; i < r.ButtonCount; i++)
                        lbls.Add(i + "=" + r.GetButtonLabel((int)i));
                    extras = " extras: " + string.Join(",", lbls);
                }
                lines.Add("  - \"" + r.DisplayName + "\" [" + r.ButtonCount + "btn/" + r.AxisCount + "ax]" + extras);
            }
            lines.Add("XInput available: " + Native.XInput.Available +
                      (_lastActivePad.StartsWith("XInput") ? " (IN USE — WGI reads idle)" : ""));
            lines.Add("last snapshot: " + _last.Summary);
            return lines.ToArray();
        }

        public void Dispose()
        {
            _stop = true;
            try { _pollThread.Join(200); } catch { }
        }
    }

    /// <summary>
    /// Per-device raw axis calibration captured at first reading: neutral offset
    /// (DualSense raw axes are [0..1], neutral=0.5) plus gain to full -1..1 range.
    /// </summary>
    public sealed class AxisCalibration
    {
        private readonly double[] _neutral;

        public AxisCalibration(double[] neutralAtCapture) => _neutral = neutralAtCapture;

        public double NeutralOf(int axis) => axis < _neutral.Length ? _neutral[axis] : 0;

        public double Normalize(int axis, double value)
        {
            if (axis >= _neutral.Length) return 0;
            double n = _neutral[axis];
            // [0..1] axis (DualSense, neutral 0.5): span 0.5 → (v-0.5)*2, exactly the
            // user-measured mapping. [-1..1] axis (neutral ~0): span 1 → passthrough.
            // [0..2] axis (neutral 1): span 1 → (v-1).
            double span = Math.Max(n, 1.0 - n);
            if (span < 0.25) span = 1.0;
            return Math.Clamp((value - n) / span, -1, 1);
        }
    }

    /// <summary>
    /// True raw-button mapping for the DualSense exposed by Windows (Sony order,
    /// NOT XInput order): 0=Square 1=Cross 2=Circle 3=Triangle 4=L1 5=R1 6=L2 7=R2
    /// 8=Create 9=Options 10=L3 11=R3 12=PS 13=Touchpad 14=Mic; dpad = hat switch.
    /// Generic fallback matches the virtual Xbox pad (0=A 1=B 2=X 3=Y 4=LB 5=RB
    /// 6=View 7=Menu 8=LS 9=RS 10-13=dpad).
    /// </summary>
    internal sealed class RawButtonMap
    {
        public int A, B, X, Y, LB, RB, LS, RS, View, Menu;
        public int[] DPad = new int[4];   // Up Down Left Right (button indices)

        public static readonly RawButtonMap Generic = new()
        {
            A = 0, B = 1, X = 2, Y = 3,
            LB = 4, RB = 5,
            View = 6, Menu = 7,
            LS = 8, RS = 9,
            DPad = new[] { 10, 11, 12, 13 }
        };

        public static readonly RawButtonMap Sony = new()
        {
            A = 1, B = 2, X = 0, Y = 3,          // Cross, Circle, Square, Triangle
            LB = 4, RB = 5,
            LS = 10, RS = 11,
            View = 8, Menu = 9,                  // Create, Options
            DPad = new[] { -1, -1, -1, -1 }      // dpad = hat switch, not buttons
        };

        public static RawButtonMap Detect(Windows.Gaming.Input.RawGameController raw)
        {
            // DualSense/DualShock: raw button 6 = L2-as-button with no WGI label,
            // while Xbox-like pads have label Back/View at index 6
            if (raw.ButtonCount > 6 && raw.GetButtonLabel(6) == Windows.Gaming.Input.GameControllerButtonLabel.None)
                return Sony;
            return Generic;
        }
    }

    /// <summary>Immutable snapshot of one gamepad reading.</summary>
    public readonly struct GamepadSnapshot : IEquatable<GamepadSnapshot>
    {
        public readonly double LX, LY, RX, RY;           // -1..1
        public readonly bool A, B, X, Y;
        public readonly bool LB, RB, LS, RS;
        public readonly bool DUp, DDown, DLeft, DRight;
        public readonly bool View, Menu;
        public readonly bool Home;   // PS/Xbox center button (best-effort detection)

        public readonly double LeftTrigger;   // 0..1
        public readonly double RightTrigger;  // 0..1

        public GamepadSnapshot(
            double lx, double ly, double rx, double ry,
            bool a, bool b, bool x, bool y,
            bool lb, bool rb, bool ls, bool rs,
            bool dUp, bool dDown, bool dLeft, bool dRight,
            bool view, bool menu,
            double lt, double rt,
            bool home = false)
        {
            LX = lx; LY = ly; RX = rx; RY = ry;
            A = a; B = b; X = x; Y = y;
            LB = lb; RB = rb; LS = ls; RS = rs;
            DUp = dUp; DDown = dDown; DLeft = dLeft; DRight = dRight;
            View = view; Menu = menu;
            Home = home;
            LeftTrigger = lt; RightTrigger = rt;
        }

        public bool AnyInput =>
            Math.Abs(LX) > 0.02 || Math.Abs(LY) > 0.02 || Math.Abs(RX) > 0.02 || Math.Abs(RY) > 0.02 ||
            A || B || X || Y || LB || RB || LS || RS ||
            DUp || DDown || DLeft || DRight || View || Menu || Home ||
            LeftTrigger > 0.1 || RightTrigger > 0.1;

        /// <summary>Compact one-line trace of everything currently active (for crash.log).</summary>
        public string Summary
        {
            get
            {
                var parts = new List<string>();
                void AddIf(string n, bool v) { if (v) parts.Add(n); }
                AddIf("A", A); AddIf("B", B); AddIf("X", X); AddIf("Y", Y);
                AddIf("LB", LB); AddIf("RB", RB); AddIf("LS", LS); AddIf("RS", RS);
                AddIf("DUp", DUp); AddIf("DDown", DDown); AddIf("DLeft", DLeft); AddIf("DRight", DRight);
                AddIf("View", View); AddIf("Menu", Menu); AddIf("Home", Home);
                if (LeftTrigger > 0.1) parts.Add("LT=" + LeftTrigger.ToString("0.0"));
                if (RightTrigger > 0.1) parts.Add("RT=" + RightTrigger.ToString("0.0"));
                if (Math.Abs(LX) > 0.05) parts.Add("LX=" + LX.ToString("0.00"));
                if (Math.Abs(LY) > 0.05) parts.Add("LY=" + LY.ToString("0.00"));
                if (Math.Abs(RX) > 0.05) parts.Add("RX=" + RX.ToString("0.00"));
                if (Math.Abs(RY) > 0.05) parts.Add("RY=" + RY.ToString("0.00"));
                return parts.Count == 0 ? "(idle)" : string.Join(" ", parts);
            }
        }

        public static double Deadzone = 0.005;

        /// <summary>
        /// Raw-controller reading for devices the Gamepad wrapper cannot wrap
        /// (DualSense standalone). Layout follows the common XInput-compatible
        /// raw ordering; axis min/max are read from the controller to normalize.
        /// </summary>
        internal static GamepadSnapshot FromRaw(Windows.Gaming.Input.RawGameController raw, bool home, AxisCalibration cal, RawButtonMap map)
        {
            var buttons = new bool[raw.ButtonCount];
            var switches = new Windows.Gaming.Input.GameControllerSwitchPosition[raw.SwitchCount];
            var axes = new double[raw.AxisCount];
            raw.GetCurrentReading(buttons, switches, axes);

            double Axis(int i)
            {
                if (i >= axes.Length) return 0;
                double v = cal.Normalize(i, axes[i]);
                double dz = Deadzone;
                return Math.Abs(v) < dz ? 0 : (v - Math.Sign(v) * dz) / (1.0 - dz);
            }
            double Clamp01(double v) => v < 0 ? 0 : v;
            bool B(int i) => i >= 0 && i < buttons.Length && buttons[i];

            // dpad: buttons on generic/XInput-like pads, hat switch on Sony layout
            bool dUp, dDown, dLeft, dRight;
            if (raw.SwitchCount > 0)
            {
                var pos = switches[0];
                dUp    = pos == Windows.Gaming.Input.GameControllerSwitchPosition.Up ||
                         pos == Windows.Gaming.Input.GameControllerSwitchPosition.UpRight ||
                         pos == Windows.Gaming.Input.GameControllerSwitchPosition.UpLeft;
                dDown  = pos == Windows.Gaming.Input.GameControllerSwitchPosition.Down ||
                         pos == Windows.Gaming.Input.GameControllerSwitchPosition.DownRight ||
                         pos == Windows.Gaming.Input.GameControllerSwitchPosition.DownLeft;
                dLeft  = pos == Windows.Gaming.Input.GameControllerSwitchPosition.Left ||
                         pos == Windows.Gaming.Input.GameControllerSwitchPosition.UpLeft ||
                         pos == Windows.Gaming.Input.GameControllerSwitchPosition.DownLeft;
                dRight = pos == Windows.Gaming.Input.GameControllerSwitchPosition.Right ||
                         pos == Windows.Gaming.Input.GameControllerSwitchPosition.UpRight ||
                         pos == Windows.Gaming.Input.GameControllerSwitchPosition.DownRight;
            }
            else
            {
                dUp    = B(map.DPad[0]);
                dDown  = B(map.DPad[1]);
                dLeft  = B(map.DPad[2]);
                dRight = B(map.DPad[3]);
            }

            // triggers: pick axes whose captured neutral is near 0 (idle) — trigger
            // axes rest at 0 on DualSense ([0..1] analog), stick axes rest at 0.5
            int ltAxis = -1, rtAxis = -1;
            for (int i = 0; i < raw.AxisCount && rtAxis < 0; i++)
            {
                double n = cal.NeutralOf(i);
                if (n < 0.25)
                {
                    if (ltAxis < 0) ltAxis = i; else rtAxis = i;
                }
            }
            if (ltAxis < 0 || rtAxis < 0) { ltAxis = 3; rtAxis = 4; }   // fallback
            double lt = ltAxis >= 0 ? Clamp01(cal.Normalize(ltAxis, ltAxis < axes.Length ? axes[ltAxis] : 0)) : 0;
            double rt = rtAxis >= 0 ? Clamp01(cal.Normalize(rtAxis, rtAxis < axes.Length ? axes[rtAxis] : 0)) : 0;
            // DualSense raw axis order (user-measured): 0=LX 1=LY 2=RX 3=? 4=? 5=RY
            // (axes 3/4 rest at 0 -> triggers, auto-detected below)
            return new GamepadSnapshot(
                Axis(0), -Axis(1),
                Axis(2), -Axis(5),
                B(map.A), B(map.B), B(map.X), B(map.Y),
                B(map.LB), B(map.RB),
                B(map.LS), B(map.RS),
                dUp, dDown, dLeft, dRight,
                B(map.View), B(map.Menu),
                lt, rt,
                home);
        }

        /// <summary>XInput fallback converter (virtual Xbox pad from DS4Windows/Steam).</summary>
        public static GamepadSnapshot FromXInput(Native.XInput.XINPUT_STATE st)
        {
            double dz = Deadzone;
            double Axis(short v) => Math.Abs(v) < dz * short.MaxValue
                ? 0 : (v - Math.Sign(v) * dz * short.MaxValue) / ((1.0 - dz) * short.MaxValue);
            ushort b = st.Game.wButtons;
            return new GamepadSnapshot(
                Axis(st.Game.sThumbLX), Axis(st.Game.sThumbLY),
                Axis(st.Game.sThumbRX), Axis(st.Game.sThumbRY),
                (b & Native.XInput.BTN_A) != 0,
                (b & Native.XInput.BTN_B) != 0,
                (b & Native.XInput.BTN_X) != 0,
                (b & Native.XInput.BTN_Y) != 0,
                (b & Native.XInput.LEFT_SHOULDER) != 0,
                (b & Native.XInput.RIGHT_SHOULDER) != 0,
                (b & Native.XInput.LEFT_THUMB) != 0,
                (b & Native.XInput.RIGHT_THUMB) != 0,
                (b & Native.XInput.DPAD_UP) != 0,
                (b & Native.XInput.DPAD_DOWN) != 0,
                (b & Native.XInput.DPAD_LEFT) != 0,
                (b & Native.XInput.DPAD_RIGHT) != 0,
                (b & Native.XInput.BACK) != 0,
                (b & Native.XInput.START) != 0,
                st.Game.bLeftTrigger / 255.0,
                st.Game.bRightTrigger / 255.0,
                home: false);   // Xbox guide button is not exposed by XInput
        }

        public static GamepadSnapshot From(Windows.Gaming.Input.GamepadReading r, bool home = false)
        {
            double dz = Deadzone;
            double Axis(double v) => Math.Abs(v) < dz ? 0 : (v - Math.Sign(v) * dz) / (1.0 - dz);

            return new GamepadSnapshot(
                Axis(r.LeftThumbstickX), Axis(r.LeftThumbstickY),
                Axis(r.RightThumbstickX), Axis(r.RightThumbstickY),
                (r.Buttons & Windows.Gaming.Input.GamepadButtons.A) != 0,
                (r.Buttons & Windows.Gaming.Input.GamepadButtons.B) != 0,
                (r.Buttons & Windows.Gaming.Input.GamepadButtons.X) != 0,
                (r.Buttons & Windows.Gaming.Input.GamepadButtons.Y) != 0,
                (r.Buttons & Windows.Gaming.Input.GamepadButtons.LeftShoulder) != 0,
                (r.Buttons & Windows.Gaming.Input.GamepadButtons.RightShoulder) != 0,
                (r.Buttons & Windows.Gaming.Input.GamepadButtons.LeftThumbstick) != 0,
                (r.Buttons & Windows.Gaming.Input.GamepadButtons.RightThumbstick) != 0,
                (r.Buttons & Windows.Gaming.Input.GamepadButtons.DPadUp) != 0,
                (r.Buttons & Windows.Gaming.Input.GamepadButtons.DPadDown) != 0,
                (r.Buttons & Windows.Gaming.Input.GamepadButtons.DPadLeft) != 0,
                (r.Buttons & Windows.Gaming.Input.GamepadButtons.DPadRight) != 0,
                (r.Buttons & Windows.Gaming.Input.GamepadButtons.View) != 0,
                (r.Buttons & Windows.Gaming.Input.GamepadButtons.Menu) != 0,
                r.LeftTrigger, r.RightTrigger, home);
        }

        /// <summary>Reads a physical button by mapping-profile name (A, B, LB, RB, LT, RT, LS, RS, View, Menu, DUp…).</summary>
        public bool Button(string name) => name switch
        {
            "A" => A, "B" => B, "X" => X, "Y" => Y,
            "LB" => LB, "RB" => RB,
            "LT" => LeftTrigger > 0.5, "RT" => RightTrigger > 0.5,
            "LS" => LS, "RS" => RS,
            "View" => View, "Menu" => Menu, "Home" => Home,
            "DUp" => DUp, "DDown" => DDown, "DLeft" => DLeft, "DRight" => DRight,
            _ => false
        };

        public bool Equals(GamepadSnapshot other) =>
            LX == other.LX && LY == other.LY && RX == other.RX && RY == other.RY &&
            A == other.A && B == other.B && X == other.X && Y == other.Y &&
            LB == other.LB && RB == other.RB && LS == other.LS && RS == other.RS &&
            DUp == other.DUp && DDown == other.DDown && DLeft == other.DLeft && DRight == other.DRight &&
            View == other.View && Menu == other.Menu && Home == other.Home &&
            LeftTrigger == other.LeftTrigger && RightTrigger == other.RightTrigger;
    }
}