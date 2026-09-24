using System;
using System.Runtime.InteropServices;

namespace GamepadKeyboard.Input
{
    /// <summary>
    /// Gamepad polling service. Uses Windows.Gaming.Input.Gamepad, which covers
    /// Xbox 360/One/Series and DualShock 4 / DualSense pads on Windows 10/11.
    /// A dedicated thread polls at a fixed rate and raises a snapshot event.
    /// </summary>
    public sealed class GamepadService : IDisposable
    {
        private readonly System.Threading.Timer _pollTimer;
        private GamepadSnapshot _last = default;
        private bool _hasLast;

        public event Action<GamepadSnapshot>? StateChanged;

        public int PollHz { get; set; } = 250;

        public GamepadService()
        {
            // Timer on its own thread pool; cheap enough at 250 Hz.
            _pollTimer = new System.Threading.Timer(Poll, null, 0, 4);
        }

        private void Poll(object? state)
        {
            var pad = Windows.Gaming.Input.Gamepad.Gamepads;
            if (pad.Count == 0)
            {
                if (_hasLast && _last.AnyInput)
                {
                    // pad unplugged mid-use — release everything
                    var empty = default(GamepadSnapshot);
                    _hasLast = false;
                    StateChanged?.Invoke(empty);
                }
                return;
            }

            var reading = pad[0].GetCurrentReading();
            var snap = GamepadSnapshot.From(reading);

            _pollTimer.Change(0, 1000 / Math.Max(60, PollHz));

            if (_hasLast && snap.Equals(_last))
                return;

            _last = snap;
            _hasLast = true;
            StateChanged?.Invoke(snap);
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

        public readonly double LeftTrigger;   // 0..1
        public readonly double RightTrigger;  // 0..1

        public GamepadSnapshot(
            double lx, double ly, double rx, double ry,
            bool a, bool b, bool x, bool y,
            bool lb, bool rb, bool ls, bool rs,
            bool dUp, bool dDown, bool dLeft, bool dRight,
            bool view, bool menu,
            double lt, double rt)
        {
            LX = lx; LY = ly; RX = rx; RY = ry;
            A = a; B = b; X = x; Y = y;
            LB = lb; RB = rb; LS = ls; RS = rs;
            DUp = dUp; DDown = dDown; DLeft = dLeft; DRight = dRight;
            View = view; Menu = menu;
            LeftTrigger = lt; RightTrigger = rt;
        }

        public bool AnyInput =>
            Math.Abs(LX) > 0.15 || Math.Abs(LY) > 0.15 || Math.Abs(RX) > 0.15 || Math.Abs(RY) > 0.15 ||
            A || B || X || Y || LB || RB || LS || RS ||
            DUp || DDown || DLeft || DRight || View || Menu ||
            LeftTrigger > 0.5 || RightTrigger > 0.5;

        public static GamepadSnapshot From(Windows.Gaming.Input.GamepadReading r)
        {
            static double Axis(double v) => Math.Abs(v) < 0.12 ? 0 : (v - Math.Sign(v) * 0.12) / (1.0 - 0.12);

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
                r.LeftTrigger, r.RightTrigger);
        }


        /// <summary>Reads a physical button by mapping-profile name (A, B, LB, RB, LT, RT, LS, RS, View, Menu, DUp…).</summary>
        public bool Button(string name) => name switch
        {
            "A" => A, "B" => B, "X" => X, "Y" => Y,
            "LB" => LB, "RB" => RB,
            "LT" => LeftTrigger > 0.5, "RT" => RightTrigger > 0.5,
            "LS" => LS, "RS" => RS,
            "View" => View, "Menu" => Menu,
            "DUp" => DUp, "DDown" => DDown, "DLeft" => DLeft, "DRight" => DRight,
            _ => false
        };

        public bool Equals(GamepadSnapshot other) =>
            LX == other.LX && LY == other.LY && RX == other.RX && RY == other.RY &&
            A == other.A && B == other.B && X == other.X && Y == other.Y &&
            LB == other.LB && RB == other.RB && LS == other.LS && RS == other.RS &&
            DUp == other.DUp && DDown == other.DDown && DLeft == other.DLeft && DRight == other.DRight &&
            View == other.View && Menu == other.Menu &&
            LeftTrigger == other.LeftTrigger && RightTrigger == other.RightTrigger;
    }
}