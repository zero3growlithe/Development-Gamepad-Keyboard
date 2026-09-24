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
        private readonly System.Threading.Timer _pollTimer;
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
            // Timer on its own thread pool; cheap enough at 250 Hz.
            _pollTimer = new System.Threading.Timer(Poll, null, 0, 4);
        }

        private void Poll(object? state)
        {
            try
            {
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
                    var gp = GetPad(raw);
                    if (gp == null) continue;
                    var snap = GamepadSnapshot.From(gp.GetCurrentReading(), ReadHome(raw));
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

        private Windows.Gaming.Input.Gamepad? GetPad(Windows.Gaming.Input.RawGameController raw)
        {
            if (_padCache.TryGetValue(raw.DisplayName, out var cached))
                return cached;
            var gp = Windows.Gaming.Input.Gamepad.FromGameController(raw);
            if (gp != null) _padCache[raw.DisplayName] = gp;
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

        public void Dispose() => _pollTimer.Dispose();
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

        public static double Deadzone = 0.12;

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