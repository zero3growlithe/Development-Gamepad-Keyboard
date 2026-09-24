using System;
using System.Collections.Generic;
using System.Linq;
using GamepadKeyboard.Input;
using GamepadKeyboard.Keyboard;
using GamepadKeyboard.Native;
using GamepadKeyboard.Overlay;

namespace GamepadKeyboard
{
    /// <summary>
    /// Translates gamepad snapshots into keyboard / mouse actions.
    /// Owns the two interaction modes (virtual keyboard, mouse) and all
    /// button semantics; raises UI update requests for the overlay.
    /// </summary>
    public sealed class ControllerMapper
    {
        private readonly InputSender _sender = new();
        private readonly KeyboardLayout _layout = new();

        public bool MouseMode { get; set; }
        public KeyboardLayout.KeyDef? LeftHit { get; private set; }
        public KeyboardLayout.KeyDef? RightHit { get; private set; }
        public double LeftLen { get; private set; }
        public double RightLen { get; private set; }
        public double LastLeftX { get; private set; }
        public double LastLeftY { get; private set; }
        public double LastRightX { get; private set; }
        public double LastRightY { get; private set; }

        // held modifier state from toggle support
        private readonly HashSet<ushort> _toggledKeys = new();

        // edge detection
        private bool _lastA, _lastB, _lastY, _lastL1, _lastR1, _lastDLeft, _lastDRight, _lastY_Hold;

        public event Action? StateChanged;

        public ControllerMapper(KeyboardLayout layout)
        {
            _layout = layout;
            _layout.Build();
        }

        public void Process(in GamepadSnapshot s)
        {
            // Mode switch: Y press (edge)
            if (s.Y && !_lastY)
            {
                MouseMode = !MouseMode;
                StateChanged?.Invoke();
            }

            if (MouseMode)
            {
                ProcessMouse(s);
            }
            else
            {
                ProcessKeyboard(s);
            }

            _lastY = s.Y;
        }

        // ── Keyboard mode ─────────────────────────────────────────────────────

        private void ProcessKeyboard(in GamepadSnapshot s)
        {
            // L2/R2 combo + dpad switches profile
            if (s.LeftTrigger > 0.5 && s.RightTrigger > 0.5)
            {
                bool next = s.DRight && !_lastDRight;
                bool prev = s.DLeft && !_lastDLeft;
                if (next || prev)
                {
                    var settings = Settings.AppSettings.Instance;
                    int count = Math.Max(1, settings.KeyboardProfiles.Count);
                    settings.ActiveProfile = (settings.ActiveProfile + (next ? 1 : count - 1)) % count;
                    Settings.AppSettings.Save();
                    StateChanged?.Invoke();
                }
                _lastDLeft = s.DLeft; _lastDRight = s.DRight;
                return;
            }

            _lastDLeft = s.DLeft; _lastDRight = s.DRight;

            // dpad -> arrows / PageUp-Home etc when Y held
            if (s.Y)
            {
                HandleDpadWithY(s);
            }
            else
            {
                HandleDpadPlain(s);
            }

            // B = backspace (edge)
            if (s.B && !_lastB) _sender.TapKey(Vk.Back);

            LastLeftX = s.LX; LastLeftY = s.LY;
            // left stick ray
            double maxL = RayLengthFor(Vk.Escape) * Settings.AppSettings.Instance.LeftRayScale;
            (LeftHit, LeftLen) = RayHit(s.LX, s.LY, maxL);

            LastRightX = s.RX; LastRightY = s.RY;
            // right stick ray
            double maxR = RayLengthFor(Vk.F12) * Settings.AppSettings.Instance.RightRayScale;
            (RightHit, RightLen) = RayHit(s.RX, s.RY, maxR);

            // L1 / R1 commit
            bool l1 = s.LB, r1 = s.RB;
            if (l1 && !_lastL1 && LeftHit != null) CommitKey(LeftHit, s);
            if (r1 && !_lastR1 && RightHit != null) CommitKey(RightHit, s);

            // hold modifiers via triggers/stick-press
            ApplyHeldModifier(s.LeftTrigger > 0.5, Vk.LShift);
            ApplyHeldModifier(s.RightTrigger > 0.5, Vk.LControl);

            _lastL1 = l1; _lastR1 = r1;
            StateChanged?.Invoke();
        }

        private void HandleDpadPlain(in GamepadSnapshot s)
        {
            if (s.DUp && !_lastDUp) _sender.TapKey(Vk.Up, extended: true);
            if (s.DDown && !_lastDDown) _sender.TapKey(Vk.Down, extended: true);
            if (s.DLeft && !_lastDLeft2) _sender.TapKey(Vk.Left, extended: true);
            if (s.DRight && !_lastDRight2) _sender.TapKey(Vk.Right, extended: true);
        }

        private void HandleDpadWithY(in GamepadSnapshot s)
        {
            if (s.DUp && !_lastDUp) _sender.TapKey(Vk.PageUp, extended: true);
            if (s.DDown && !_lastDDown) _sender.TapKey(Vk.PageDown, extended: true);
            if (s.DLeft && !_lastDLeft2) _sender.TapKey(Vk.Home, extended: true);
            if (s.DRight && !_lastDRight2) _sender.TapKey(Vk.End, extended: true);
        }

        private void CommitKey(KeyboardLayout.KeyDef k, in GamepadSnapshot s)
        {
            bool shift = s.LeftTrigger > 0.5 || _toggledKeys.Contains(Vk.Shift);
            bool ctrl = s.RightTrigger > 0.5 || _toggledKeys.Contains(Vk.Control);
            bool alt = Math.Abs(s.LX) > 0.9 || _toggledKeys.Contains(Vk.Menu);

            if (shift) _sender.KeyDown(Vk.Shift);
            if (ctrl) _sender.KeyDown(Vk.Control);
            if (alt) _sender.KeyDown(Vk.Menu);

            _sender.TapKey(k.Vk, k.Extended);

            if (alt) _sender.KeyUp(Vk.Menu);
            if (ctrl) _sender.KeyUp(Vk.Control);
            if (shift) _sender.KeyUp(Vk.Shift);
        }

        private void ApplyHeldModifier(bool held, ushort vk)
        {
            if (held) _sender.KeyDown(vk);
            else _sender.KeyUp(vk);
        }

        // ── Mouse mode ────────────────────────────────────────────────────────

        private void ProcessMouse(in GamepadSnapshot s)
        {
            var profile = Settings.AppSettings.Instance.MouseProfile;

            // right stick: cursor
            double rx = ApplyCurve(s.RX);
            double ry = ApplyCurve(s.RY);
            double speed = Settings.AppSettings.Instance.MouseSpeed;
            if (s.RightTrigger > 0.5) speed *= Settings.AppSettings.Instance.MouseSpeedBoostMultiplier;

            _sender.MouseMove((int)Math.Round(rx * speed), (int)Math.Round(-ry * speed));

            // left stick: scroll (vertical + horizontal simultaneously)
            double scroll = Settings.AppSettings.Instance.ScrollSpeed;
            if (Math.Abs(s.LY) > 0.05) _sender.MouseWheel((int)Math.Sign(s.LY) * -(int)Math.Round(ApplyCurve(Math.Abs(s.LY)) * 120 * scroll / 3.0));
            if (Math.Abs(s.LX) > 0.05) _sender.MouseHWheel((int)Math.Sign(s.LX) * (int)Math.Round(ApplyCurve(Math.Abs(s.LX)) * 120 * scroll / 3.0));

            // dpad scroll
            if (s.DUp) _sender.MouseWheel(120);
            if (s.DDown) _sender.MouseWheel(-120);
            if (s.DLeft) _sender.MouseHWheel(-120);
            if (s.DRight) _sender.MouseHWheel(120);

            // buttons (edge + repeat-free; simple tap semantics)
            HandleMouseAction(profile.A, s.A && !_lastA);
            HandleMouseAction(profile.B, s.B && !_lastB);
            HandleMouseAction(profile.X, s.X && !_lastX);
            HandleMouseAction(profile.Y, s.Y && !_lastY);
            HandleMouseAction(profile.LB, s.LB && !_lastLB);
            HandleMouseAction(profile.RB, s.RB && !_lastRB);
            HandleMouseAction(profile.LT, s.LeftTrigger > 0.5 && _lastLT <= 0.5);
            HandleMouseAction(profile.RT, s.RightTrigger > 0.5 && _lastRT <= 0.5);
            HandleMouseAction(profile.DUp, s.DUp && !_lastDUp);
            HandleMouseAction(profile.DDown, s.DDown && !_lastDDown);
            HandleMouseAction(profile.DLeft, s.DLeft && !_lastDLeft2);
            HandleMouseAction(profile.DRight, s.DRight && !_lastDRight2);
            HandleMouseAction(profile.LS, s.LS && !_lastLS);
            HandleMouseAction(profile.RS, s.RS && !_lastRS);

            _lastA = s.A; _lastB = s.B; _lastX = s.X; _lastLB = s.LB; _lastRB = s.RB; _lastLS = s.LS; _lastRS = s.RS;
            _lastLT = s.LeftTrigger; _lastRT = s.RightTrigger;
        }

        private void HandleMouseAction(string action, bool pressed)
        {
            if (!pressed || action == "None") return;
            switch (action)
            {
                case "LeftClick": _sender.MouseButton(NativeMethods.MOUSEEVENTF_LEFTDOWN, NativeMethods.MOUSEEVENTF_LEFTUP); break;
                case "RightClick": _sender.MouseButton(NativeMethods.MOUSEEVENTF_RIGHTDOWN, NativeMethods.MOUSEEVENTF_RIGHTUP); break;
                case "MiddleClick": _sender.MouseButton(NativeMethods.MOUSEEVENTF_MIDDLEDOWN, NativeMethods.MOUSEEVENTF_MIDDLEUP); break;
                case "XButton1": _sender.MouseButton(NativeMethods.MOUSEEVENTF_XDOWN, NativeMethods.MOUSEEVENTF_XUP); break;
                case "ScrollUp": _sender.MouseWheel(120); break;
                case "ScrollDown": _sender.MouseWheel(-120); break;
                case "ScrollLeft": _sender.MouseHWheel(-120); break;
                case "ScrollRight": _sender.MouseHWheel(120); break;
                case "SpeedBoost": break; // held behavior handled in stick handler
                case "KeyboardMode": MouseMode = false; StateChanged?.Invoke(); break;
                case "ToggleLegend":
                    Settings.AppSettings.Instance.ShowButtonLegend = !Settings.AppSettings.Instance.ShowButtonLegend;
                    Settings.AppSettings.Save();
                    StateChanged?.Invoke();
                    break;
            }
        }

        private double ApplyCurve(double v)
        {
            double exp = Settings.AppSettings.Instance.Profile.CurveExponent;
            double sign = Math.Sign(v);
            return sign * Math.Pow(Math.Abs(v), exp);
        }

        // ── Ray geometry ──────────────────────────────────────────────────────

        /// <summary>
        /// Compute the key hit by a ray from the given origin in grid space.
        /// Uses the profile origin + curve + scale; returns null when the stick is idle.
        /// </summary>
        private (KeyboardLayout.KeyDef?, double) RayHit(double sx, double sy, double maxLen)
        {
            double mag = Math.Sqrt(sx * sx + sy * sy);
            if (mag < 0.08) return (null, 0);

            var p = Settings.AppSettings.Instance.Profile;
            double len = maxLen * Math.Pow(mag, p.CurveExponent);

            // find the key whose rect center is closest to the ray endpoint
            double ex = p.LeftX * 20 + sx * len / 40.0; // normalized endpoint in grid units
            double ey = p.LeftY * 8 + sy * len / 40.0;

            KeyboardLayout.KeyDef? best = null;
            double bestD = double.MaxValue;
            foreach (var k in _layout.Keys)
            {
                double cx = k.X + k.W / 2;
                double cy = k.Y + k.H / 2;
                double d = (cx - ex) * (cx - ex) + (cy - ey) * (cy - ey);
                if (d < bestD) { bestD = d; best = k; }
            }
            return (best, len);
        }

        private double RayLengthFor(ushort vk)
        {
            var k = _layout.FindByVk(vk);
            if (k == null) return 8 * 54; // fallback: full grid height
            // distance from grid origin in key units * pitch
            double pitch = 48 + Settings.AppSettings.Instance.KeySpacing;
            return Math.Sqrt(k.X * k.X + k.Y * k.Y) * pitch;
        }

        private bool _lastDUp, _lastDDown, _lastDLeft2, _lastDRight2, _lastX, _lastLB, _lastRB, _lastLS, _lastRS;
        private double _lastLT, _lastRT;
    }
}