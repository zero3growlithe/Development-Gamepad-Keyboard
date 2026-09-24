using System;
using System.Collections.Generic;
using GamepadKeyboard.Input;
using GamepadKeyboard.Keyboard;
using GamepadKeyboard.Native;
using GamepadKeyboard.Settings;

namespace GamepadKeyboard
{
    /// <summary>
    /// Translates gamepad snapshots into keyboard / mouse actions.
    /// All pad buttons go through an action table resolved from the active
    /// KeyboardProfile or MouseProfile, so every button is remappable.
    /// Owns enable/disable state (mappable) for using the pad inside games.
    /// </summary>
    public sealed class ControllerMapper
    {
        private readonly InputSender _sender = new();
        private readonly KeyboardLayout _layout;

        public bool MouseMode { get; set; }

        /// <summary>When disabled the pad is passed through untouched (game use).</summary>
        public bool InputEnabled { get; private set; } = true;

        public void SetInputEnabled(bool enabled)
        {
            if (InputEnabled == enabled) return;
            App.Log("input enabled -> " + enabled);
            InputEnabled = enabled;
            if (!enabled) ReleaseAllModifiers();
            Notification?.Invoke(enabled ? "Input ENABLED — gamepad controls the PC"
                                         : "Input DISABLED — gamepad free for games");
            StateChanged?.Invoke();
        }

        public KeyboardLayout.KeyDef? LeftHit { get; private set; }
        public KeyboardLayout.KeyDef? RightHit { get; private set; }
        public double LeftLen { get; private set; }
        public double RightLen { get; private set; }
        public double LastLeftX { get; private set; }
        public double LastLeftY { get; private set; }
        public double LastRightX { get; private set; }
        public double LastRightY { get; private set; }

        /// <summary>L3/R3 press toggles (sticky until toggled off or input disabled).</summary>
        public bool AdjustMove { get; set; }
        public bool AdjustScale { get; set; }
        public double MoveDX { get; private set; }
        public double MoveDY { get; private set; }
        public double ScaleDelta { get; private set; }   // per-tick, up/down = +/-


        // currently held virtual modifier keys (toggle or hold)
        private readonly HashSet<ushort> _heldModifiers = new();

        // previous physical state (edge detection)
        private bool _pA, _pB, _pX, _pY, _pLB, _pRB, _pLS, _pRS;
        private bool _pDUp, _pDDown, _pDLeft, _pDRight;
        private double _pLT, _pRT;
        private bool _pCombo;

        /// <summary>Raised when disable/enable happens or profile changes (UI toast).</summary>
        public event Action<string>? Notification;
        public event Action? StateChanged;

        public ControllerMapper(KeyboardLayout layout)
        {
            _layout = layout;
        }

        public void Process(in GamepadSnapshot s)
        {
            // ── enable combo (works even when disabled) ──────────────────────
            // primary: PS/Xbox (Home) + Menu + Select; fallback: L3 + R3 + L1 + R1
            bool comboPrimary = s.Home && s.Menu && s.View;
            bool comboFallback = s.LS && s.RS && s.LB && s.RB;
            bool combo = comboPrimary || comboFallback;
            if (combo && !_pCombo)
            {
                App.Log("input enabled -> true (enable combo: " +
                        (comboPrimary ? "Home+Menu+Select" : "L3+R3+L1+R1") + ")");
                InputEnabled = true;
                Notification?.Invoke("Input ENABLED — gamepad controls the PC");
                StateChanged?.Invoke();
                _pCombo = combo;
                SaveEdges(s);   // consume the enabling reading: still-held combo buttons
                return;         // (View/Menu) must not fire their mapped actions
            }
            _pCombo = combo;

            if (!InputEnabled)
            {
                // pass-through: app injects nothing, game sees the pad natively
                AdjustMove = AdjustScale = false;
                MoveDX = MoveDY = ScaleDelta = 0;
                ClearAllEdges(s);
                return;
            }

            if (MouseMode)
                ProcessMouse(s);
            else
                ProcessKeyboardMode(s);

            SaveEdges(s);
        }

        // ── Keyboard mode ─────────────────────────────────────────────────────

        private void ProcessKeyboardMode(in GamepadSnapshot s)
        {
            var p = AppSettings.Instance.Profile;

            // origin-point edit mode is intentionally NOT here; profile switch first
            if (HandleProfileSwitch(s))
                return;

            // ── overlay adjust TOGGLES: press L3 once = move mode, press again = off ──
            if (s.LS && !_pLS)
            {
                AdjustMove = !AdjustMove;
                MoveDX = MoveDY = 0;
                Notification?.Invoke(AdjustMove ? "Move mode ON — left stick moves the keyboard"
                                                : "Move mode OFF");
            }
            if (s.RS && !_pRS)
            {
                AdjustScale = !AdjustScale;
                ScaleDelta = 0;
                Notification?.Invoke(AdjustScale ? "Scale mode ON — right stick up/down scales"
                                                 : "Scale mode OFF");
            }
            if (AdjustMove)
            {
                MoveDX = s.LX;
                MoveDY = s.LY;
            }
            if (AdjustScale)
            {
                ScaleDelta = s.RY;   // up (+RY) = bigger, down = smaller
            }

            // dispatch mapped actions for every button (edge or hold semantics)
            DispatchButton(p.A, s.A, ref _pA);
            DispatchButton(p.B, s.B, ref _pB);
            DispatchButton(p.X, s.X, ref _pX);
            DispatchButton(p.Y, s.Y, ref _pY);

            DispatchButton(p.LB, s.LB, ref _pLB);
            DispatchButton(p.RB, s.RB, ref _pRB);

            // L3/R3 are dedicated to overlay move/scale toggles — not mappable here

            DispatchButton(p.View, s.View, ref _pView);
            DispatchButton(p.Menu, s.Menu, ref _pMenu);

            // dpad layer: Y-held layer or plain layer
            if (s.Y)
            {
                DispatchButton(p.YDUp, s.DUp, ref _pDUp);
                DispatchButton(p.YDDown, s.DDown, ref _pDDown);
                DispatchButton(p.YDLeft, s.DLeft, ref _pDLeft);
                DispatchButton(p.YDRight, s.DRight, ref _pDRight);
            }
            else
            {
                DispatchButton(p.DUp, s.DUp, ref _pDUp);
                DispatchButton(p.DDown, s.DDown, ref _pDDown);
                DispatchButton(p.DLeft, s.DLeft, ref _pDLeft);
                DispatchButton(p.DRight, s.DRight, ref _pDRight);
            }

            // hold modifiers from triggers (LT/RT mapped as HoldShift/HoldCtrl)
            ApplyTriggerModifier(p.LT, s.LeftTrigger);
            ApplyTriggerModifier(p.RT, s.RightTrigger);

            // stick rays (max length = origin point -> Esc / F12, not layout corner)
            LastLeftX = s.LX; LastLeftY = s.LY;
            double maxL = RayLengthFor(Vk.Escape, p.LeftX * GridW(), p.LeftY * GridH())
                          * AppSettings.Instance.LeftRayScale * p.RayScale;
            (LeftHit, LeftLen) = RayHit(s.LX, s.LY, maxL, left: true);

            LastRightX = s.RX; LastRightY = s.RY;
            double maxR = RayLengthFor(Vk.F12, p.RightX * GridW(), p.RightY * GridH())
                          * AppSettings.Instance.RightRayScale * p.RayScale;
            (RightHit, RightLen) = RayHit(s.RX, s.RY, maxR, left: false);
        }

        private bool HandleProfileSwitch(in GamepadSnapshot s)
        {
            // L2+R2+dpad left/right switches keyboard profile (fixed combo, documented)
            if (s.LeftTrigger > 0.5 && s.RightTrigger > 0.5)
            {
                bool next = s.DRight && !_pDRight;
                bool prev = s.DLeft && !_pDLeft;
                if (next || prev)
                {
                    var st = AppSettings.Instance;
                    int count = Math.Max(1, st.KeyboardProfiles.Count);
                    st.ActiveProfile = (st.ActiveProfile + (next ? 1 : count - 1)) % count;
                    AppSettings.Save();
                    Notification?.Invoke("Keyboard profile: " + st.Profile.Name);
                    StateChanged?.Invoke();
                }
                return true;
            }
            return false;
        }

        // ── Mouse mode ────────────────────────────────────────────────────────

        private void ProcessMouse(in GamepadSnapshot s)
        {
            var profile = AppSettings.Instance.MouseProfile;
            var st = AppSettings.Instance;

            bool boost = s.Button(profile.RT) || s.RightTrigger > 0.5;
            double speed = st.MouseSpeed * (boost ? st.MouseSpeedBoostMultiplier : 1.0);

            // right stick: cursor
            double rx = ApplyCurve(s.RX);
            double ry = ApplyCurve(s.RY);
            _sender.MouseMove((int)Math.Round(rx * speed), (int)Math.Round(-ry * speed));

            // left stick: scroll (vertical + horizontal)
            double sc = st.ScrollSpeed;
            if (Math.Abs(s.LY) > 0.05)
                _sender.MouseWheel((int)Math.Sign(s.LY) * -(int)Math.Round(ApplyCurve(Math.Abs(s.LY)) * 120 * sc / 3.0));
            if (Math.Abs(s.LX) > 0.05)
                _sender.MouseHWheel((int)Math.Sign(s.LX) * (int)Math.Round(ApplyCurve(Math.Abs(s.LX)) * 120 * sc / 3.0));

            // dpad scroll (unless remapped to something else)
            if (profile.DUp == "ScrollUp") { if (s.DUp) _sender.MouseWheel(120); }
            else DispatchButton(profile.DUp, s.DUp, ref _pDUp);

            if (profile.DDown == "ScrollDown") { if (s.DDown) _sender.MouseWheel(-120); }
            else DispatchButton(profile.DDown, s.DDown, ref _pDDown);

            if (profile.DLeft == "ScrollLeft") { if (s.DLeft) _sender.MouseHWheel(-120); }
            else DispatchButton(profile.DLeft, s.DLeft, ref _pDLeft);

            if (profile.DRight == "ScrollRight") { if (s.DRight) _sender.MouseHWheel(120); }
            else DispatchButton(profile.DRight, s.DRight, ref _pDRight);

            // buttons
            DispatchButton(profile.A, s.A, ref _pA);
            DispatchButton(profile.B, s.B, ref _pB);
            DispatchButton(profile.X, s.X, ref _pX);
            DispatchButton(profile.Y, s.Y, ref _pY);
            DispatchButton(profile.LB, s.LB, ref _pLB);
            DispatchButton(profile.RB, s.RB, ref _pRB);
            DispatchButton(profile.LS, s.LS, ref _pLS);
            DispatchButton(profile.RS, s.RS, ref _pRS);
            DispatchButton(profile.View, s.View, ref _pView);
            DispatchButton(profile.Menu, s.Menu, ref _pMenu);
            DispatchButton(profile.LT, s.LeftTrigger > 0.5, ref _pLTHeld);
            DispatchButton(profile.RT, s.RightTrigger > 0.5, ref _pRTHeld);
        }

        // ── Action dispatch ───────────────────────────────────────────────────

        /// <summary>
        /// Runs a mapped action on press edge. Hold-type actions (modifiers,
        /// DisableInput) use the held flag directly; everything else is edge-only.
        /// </summary>
        private void DispatchButton(string action, bool held, ref bool prev)
        {
            bool edge = held && !prev;
            switch (action)
            {
                case "HoldShift":
                case "HoldCtrl":
                case "HoldAlt":
                case "HoldWin":
                    ApplyHeld(action, held);
                    return;
                case "ToggleShift":
                case "ToggleCtrl":
                case "ToggleAlt":
                case "ToggleWin":
                    if (edge) ToggleModifier(ActionToVk(action));
                    return;
                case "None":
                    return;
            }

            if (!edge) return;

            switch (action)
            {
                case "CommitLeft":
                    if (LeftHit != null) CommitKey(LeftHit);
                    break;
                case "CommitRight":
                    if (RightHit != null) CommitKey(RightHit);
                    break;
                case "Backspace": _sender.TapKey(Vk.Back); break;
                case "Space": _sender.TapKey(Vk.Space); break;
                case "Tab": _sender.TapKey(Vk.Tab); break;
                case "Enter": _sender.TapKey(Vk.Return); break;
                case "Escape": _sender.TapKey(Vk.Escape); break;
                case "Delete": _sender.TapKey(Vk.Delete, extended: true); break;
                case "ArrowUp": _sender.TapKey(Vk.Up, extended: true); break;
                case "ArrowDown": _sender.TapKey(Vk.Down, extended: true); break;
                case "ArrowLeft": _sender.TapKey(Vk.Left, extended: true); break;
                case "ArrowRight": _sender.TapKey(Vk.Right, extended: true); break;
                case "PageUp": _sender.TapKey(Vk.PageUp, extended: true); break;
                case "PageDown": _sender.TapKey(Vk.PageDown, extended: true); break;
                case "Home": _sender.TapKey(Vk.Home, extended: true); break;
                case "End": _sender.TapKey(Vk.End, extended: true); break;
                case "CapsLock": _sender.TapKey(Vk.Capital); break;
                case "NumLock": _sender.TapKey(Vk.NumLock); break;
                case "VolumeUp": _sender.TapKey(Vk.VolumeUp); break;
                case "VolumeDown": _sender.TapKey(Vk.VolumeDown); break;
                case "VolumeMute": _sender.TapKey(Vk.VolumeMute); break;
                case "MediaPlayPause": _sender.TapKey(Vk.MediaPlayPause); break;
                case "MediaNext": _sender.TapKey(Vk.MediaNext); break;
                case "MediaPrev": _sender.TapKey(Vk.MediaPrev); break;

                case "DisableInput":
                    App.Log("input enabled -> false (View button)");
                    InputEnabled = false;
                    ReleaseAllModifiers();
                    Notification?.Invoke("Input DISABLED — gamepad free for games");
                    StateChanged?.Invoke();
                    break;

                case "ToggleKeyboardMouseMode":
                    MouseMode = !MouseMode;
                    StateChanged?.Invoke();
                    break;
                case "KeyboardMode": MouseMode = false; StateChanged?.Invoke(); break;
                case "MouseMode": MouseMode = true; StateChanged?.Invoke(); break;

                case "SwitchKeyboardProfile":
                    SwitchProfile(+1);
                    break;
                case "SwitchMouseProfile":
                    SwitchMouseProfile(+1);
                    break;

                case "ToggleLegend":
                    AppSettings.Instance.ShowButtonLegend = !AppSettings.Instance.ShowButtonLegend;
                    AppSettings.Save();
                    StateChanged?.Invoke();
                    break;

                case "ToggleOverlay":
                    var ovs = AppSettings.Instance;
                    ovs.ShowOverlay = !ovs.ShowOverlay;
                    AppSettings.Save();
                    StateChanged?.Invoke();
                    break;

                // mouse actions (also valid in keyboard-mode mappings if wanted)
                case "LeftClick": _sender.MouseButton(NativeMethods.MOUSEEVENTF_LEFTDOWN, NativeMethods.MOUSEEVENTF_LEFTUP); break;
                case "RightClick": _sender.MouseButton(NativeMethods.MOUSEEVENTF_RIGHTDOWN, NativeMethods.MOUSEEVENTF_RIGHTUP); break;
                case "MiddleClick": _sender.MouseButton(NativeMethods.MOUSEEVENTF_MIDDLEDOWN, NativeMethods.MOUSEEVENTF_MIDDLEUP); break;
                case "XButton1": _sender.MouseButton(NativeMethods.MOUSEEVENTF_XDOWN, NativeMethods.MOUSEEVENTF_XUP, 1); break;
                case "XButton2": _sender.MouseButton(NativeMethods.MOUSEEVENTF_XDOWN, NativeMethods.MOUSEEVENTF_XUP, 2); break;
                case "ScrollUp": _sender.MouseWheel(120); break;
                case "ScrollDown": _sender.MouseWheel(-120); break;
                case "ScrollLeft": _sender.MouseHWheel(-120); break;
                case "ScrollRight": _sender.MouseHWheel(120); break;

                case "SpeedBoost":
                case "PointerMode":
                    break; // handled elsewhere / no-op

                default:
                    // "Key:A", "Key:F5", "Combo:Ctrl+S" style custom mappings
                    if (action.StartsWith("Key:", StringComparison.Ordinal))
                    {
                        SendNamedKey(action[4..]);
                    }
                    else if (action.StartsWith("Combo:", StringComparison.Ordinal))
                    {
                        SendNamedCombo(action[6..]);
                    }
                    break;
            }
        }

        private void ApplyHeld(string action, bool held)
        {
            ushort vk = ActionToVk(action);
            if (held) { if (_heldModifiers.Add(vk)) _sender.KeyDown(vk); }
            else { if (_heldModifiers.Remove(vk)) _sender.KeyUp(vk); }
        }

        private void ApplyTriggerModifier(string action, double trigger)
        {
            if (action is "HoldShift" or "HoldCtrl" or "HoldAlt" or "HoldWin")
            {
                ApplyHeld(action, trigger > 0.5);
            }
            else if (action != "None")
            {
                // treat like a button edge on trigger crossing
                bool held = trigger > 0.5;
                bool dummy = false;
                DispatchButton(action, held, ref dummy);
            }
        }

                private void ToggleModifier(ushort vk)
        {
            if (_heldModifiers.Contains(vk))
            {
                _heldModifiers.Remove(vk);
                _sender.KeyUp(vk);
            }
            else
            {
                _heldModifiers.Add(vk);
                _sender.KeyDown(vk);
            }
        }

        private void ReleaseAllModifiers()
        {
            foreach (var vk in _heldModifiers)
                _sender.KeyUp(vk);
            _heldModifiers.Clear();
        }

        private static ushort ActionToVk(string action) => action switch
        {
            "HoldShift" or "ToggleShift" => Vk.LShift,
            "HoldCtrl" or "ToggleCtrl" => Vk.LControl,
            "HoldAlt" or "ToggleAlt" => Vk.LMenu,
            "HoldWin" or "ToggleWin" => Vk.LWin,
            _ => Vk.None
        };

        /// <summary>Named-key resolution for "Key:" mappings (e.g. Key:F5, Key:A, Key:NumPad4).</summary>
        private void SendNamedKey(string name)
        {
            name = name.Trim();
            if (name.Length == 1)
            {
                char c = char.ToUpperInvariant(name[0]);
                if (c >= 'A' && c <= 'Z') { _sender.TapKey((ushort)c); return; }
                if (c >= '0' && c <= '9') { _sender.TapKey((ushort)c); return; }
            }
            switch (name)
            {
                case "F1": _sender.TapKey(Vk.F1); break;
                case "F2": _sender.TapKey(Vk.F2); break;
                case "F3": _sender.TapKey(Vk.F3); break;
                case "F4": _sender.TapKey(Vk.F4); break;
                case "F5": _sender.TapKey(Vk.F5); break;
                case "F6": _sender.TapKey(Vk.F6); break;
                case "F7": _sender.TapKey(Vk.F7); break;
                case "F8": _sender.TapKey(Vk.F8); break;
                case "F9": _sender.TapKey(Vk.F9); break;
                case "F10": _sender.TapKey(Vk.F10); break;
                case "F11": _sender.TapKey(Vk.F11); break;
                case "F12": _sender.TapKey(Vk.F12); break;
                case "Space": _sender.TapKey(Vk.Space); break;
                case "Enter": _sender.TapKey(Vk.Return); break;
                case "Backspace": _sender.TapKey(Vk.Back); break;
            }
        }

        private void SendNamedCombo(string combo)
        {
            // "Combo:Ctrl+Shift+T"
            var parts = combo.Split('+');
            var mods = new List<ushort>();
            ushort? main = null;
            foreach (var raw in parts)
            {
                var part = raw.Trim();
                ushort m = part.ToLowerInvariant() switch
                {
                    "ctrl" => Vk.LControl,
                    "shift" => Vk.LShift,
                    "alt" => Vk.LMenu,
                    "win" => Vk.LWin,
                    _ => Vk.None
                };
                if (m != Vk.None) { mods.Add(m); continue; }
                main = NamedVk(part);
            }
            foreach (var m in mods) _sender.KeyDown(m);
            if (main != null) _sender.TapKey(main.Value);
            for (int i = mods.Count - 1; i >= 0; i--) _sender.KeyUp(mods[i]);
        }

        private static ushort NamedVk(string name)
        {
            if (name.Length == 1)
            {
                char c = char.ToUpperInvariant(name[0]);
                if (c >= 'A' && c <= 'Z') return (ushort)c;
                if (c >= '0' && c <= '9') return (ushort)c;
            }
            return name switch
            {
                "Space" => Vk.Space,
                "Enter" => Vk.Return,
                "Backspace" => Vk.Back,
                "Tab" => Vk.Tab,
                "Escape" => Vk.Escape,
                "Delete" => Vk.Delete,
                "F1" => Vk.F1, "F2" => Vk.F2, "F3" => Vk.F3, "F4" => Vk.F4,
                "F5" => Vk.F5, "F6" => Vk.F6, "F7" => Vk.F7, "F8" => Vk.F8,
                "F9" => Vk.F9, "F10" => Vk.F10, "F11" => Vk.F11, "F12" => Vk.F12,
                "PageUp" => Vk.PageUp, "PageDown" => Vk.PageDown,
                "Home" => Vk.Home, "End" => Vk.End,
                "Up" => Vk.Up, "Down" => Vk.Down, "Left" => Vk.Left, "Right" => Vk.Right,
                _ => Vk.None
            };
        }

        private void SwitchProfile(int dir)
        {
            var st = AppSettings.Instance;
            int count = Math.Max(1, st.KeyboardProfiles.Count);
            st.ActiveProfile = (st.ActiveProfile + dir + count) % count;
            AppSettings.Save();
            Notification?.Invoke("Keyboard profile: " + st.Profile.Name);
            StateChanged?.Invoke();
        }

        private void SwitchMouseProfile(int dir)
        {
            var st = AppSettings.Instance;
            int count = Math.Max(1, st.MouseProfiles.Count);
            st.ActiveMouseProfile = (st.ActiveMouseProfile + dir + count) % count;
            AppSettings.Save();
            Notification?.Invoke("Mouse profile: " + st.MouseProfile.Name);
            StateChanged?.Invoke();
        }

        // ── Key commit with modifiers ────────────────────────────────────────

        private void CommitKey(KeyboardLayout.KeyDef k)
        {
            // held modifiers are already pressed via SendInput — just tap the key
            _sender.TapKey(k.Vk, k.Extended);
        }

        // ── Ray geometry ──────────────────────────────────────────────────────

        private (KeyboardLayout.KeyDef?, double) RayHit(double sx, double sy, double maxLen, bool left)
        {
            double mag = Math.Sqrt(sx * sx + sy * sy);
            if (mag < 0.08) return (null, 0);

            var p = AppSettings.Instance.Profile;
            double len = maxLen * Math.Pow(mag, p.CurveExponent);

            // origin in grid units
            double gx = (left ? p.LeftX : p.RightX) * GridW();
            double gy = (left ? p.LeftY : p.RightY) * GridH();
            double ex = gx + sx * len / Pitch();
            double ey = gy - sy * len / Pitch();   // grid Y grows downward; stick up = smaller Y

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

        private double GridW() => _layout.GridW;
        private double GridH() => _layout.GridH;
        private double Pitch() => 48 + AppSettings.Instance.KeySpacing;

        private double RayLengthFor(ushort vk, double gx, double gy)
        {
            var k = _layout.FindByVk(vk);
            if (k == null) return _layout.GridH * Pitch();
            double dx = k.X - gx, dy = k.Y - gy;
            return Math.Sqrt(dx * dx + dy * dy) * Pitch();
        }

        private double ApplyCurve(double v)
        {
            double exp = AppSettings.Instance.Profile.CurveExponent;
            double sign = Math.Sign(v);
            return sign * Math.Pow(Math.Abs(v), exp);
        }

        // ── edge bookkeeping ──────────────────────────────────────────────────

        private void SaveEdges(in GamepadSnapshot s)
        {
            _pA = s.A; _pB = s.B; _pX = s.X; _pY = s.Y;
            _pLB = s.LB; _pRB = s.RB; _pLS = s.LS; _pRS = s.RS;
            _pDUp = s.DUp; _pDDown = s.DDown; _pDLeft = s.DLeft; _pDRight = s.DRight;
            _pLT = s.LeftTrigger; _pRT = s.RightTrigger;
            _pView = s.View; _pMenu = s.Menu;
            _pLTHeld = s.LeftTrigger > 0.5; _pRTHeld = s.RightTrigger > 0.5;
        }

        private void ClearAllEdges(in GamepadSnapshot s)
        {
            _pA = s.A; _pB = s.B; _pX = s.X; _pY = s.Y;
            _pLB = s.LB; _pRB = s.RB; _pLS = s.LS; _pRS = s.RS;
            _pDUp = s.DUp; _pDDown = s.DDown; _pDLeft = s.DLeft; _pDRight = s.DRight;
            _pLT = s.LeftTrigger; _pRT = s.RightTrigger;
            _pView = s.View; _pMenu = s.Menu;
            _pLTHeld = s.LeftTrigger > 0.5; _pRTHeld = s.RightTrigger > 0.5;
        }

        // edge fields used only in some modes
        private bool _pView, _pMenu, _pLTHeld, _pRTHeld;
    }
}