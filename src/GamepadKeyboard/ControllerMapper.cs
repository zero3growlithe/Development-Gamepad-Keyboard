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
        /// <summary>Keyboard was entered from mouse mode via Y — B returns to mouse.</summary>
        public bool KeyboardFromMouse { get; private set; }

        /// <summary>When disabled the pad is passed through untouched (game use).</summary>
        public bool InputEnabled { get; private set; }   // starts disabled: gamepad free for games until enable combo

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

        // hold-to-type state: key currently held down by the left/right commit activation
        private KeyboardLayout.KeyDef? _heldRayKeyL, _heldRayKeyR;

        /// <summary>Virtual modifier keys currently active (toggled on or held) — for UI tint.</summary>
        public IReadOnlyCollection<ushort> HeldModifierVks => _heldModifiers;

        // custom combo bindings ("A+B+X=Action"): per-combo last-button edge tracking
        private readonly Dictionary<string, bool> _comboPrev = new();

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
            var comboSup = EvaluateCombos(s, p.ComboBindings);
            bool Sup(string b) => comboSup.Contains(b);

            // fixed mode-switch: B returns to mouse mode if keyboard was opened via Y in mouse mode
            if (KeyboardFromMouse && !Sup("B") && s.B && !_pB)
            {
                KeyboardFromMouse = false;
                MouseMode = true;
                App.Log("mode: keyboard -> mouse (B)");
                Notification?.Invoke("Mouse mode");
                StateChanged?.Invoke();
                return;
            }

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

            // hold modifiers from triggers (LT/RT mapped as HoldShift/HoldCtrl)
            if (!Sup("LT")) ApplyTriggerModifier(p.LT, s.LeftTrigger);
            if (!Sup("RT")) ApplyTriggerModifier(p.RT, s.RightTrigger);

            // stick rays (max length = LeftCtrl -> Backspace distance, per-stick slider)
            LastLeftX = s.LX; LastLeftY = s.LY;
            double maxL = MaxRayLength() * p.LeftRayLength;
            (LeftHit, LeftLen) = RayHit(s.LX, s.LY, maxL, left: true);

            LastRightX = s.RX; LastRightY = s.RY;
            double maxR = MaxRayLength() * p.RightRayLength;
            (RightHit, RightLen) = RayHit(s.RX, s.RY, maxR, left: false);

            // dispatch mapped actions for every button (edge or hold semantics);
            // buttons used as combo LAST button are suppressed while combo modifiers held
            if (!Sup("A")) DispatchButton(p.A, s.A, ref _pA);
            if (!Sup("B")) DispatchButton(p.B, s.B, ref _pB);
            if (!Sup("X")) DispatchButton(p.X, s.X, ref _pX);
            if (!Sup("Y")) DispatchButton(p.Y, s.Y, ref _pY);

            if (!Sup("LB")) DispatchButton(p.LB, s.LB, ref _pLB);
            if (!Sup("RB")) DispatchButton(p.RB, s.RB, ref _pRB);

            // L3/R3 are dedicated to overlay move/scale toggles — not mappable here

            if (!Sup("View")) DispatchButton(p.View, s.View, ref _pView);
            if (!Sup("Menu")) DispatchButton(p.Menu, s.Menu, ref _pMenu);

            // dpad layer: Y-held layer or plain layer
            if (s.Y)
            {
                if (!Sup("DUp")) DispatchButton(p.YDUp, s.DUp, ref _pDUp);
                if (!Sup("DDown")) DispatchButton(p.YDDown, s.DDown, ref _pDDown);
                if (!Sup("DLeft")) DispatchButton(p.YDLeft, s.DLeft, ref _pDLeft);
                if (!Sup("DRight")) DispatchButton(p.YDRight, s.DRight, ref _pDRight);
            }
            else
            {
                if (!Sup("DUp")) DispatchButton(p.DUp, s.DUp, ref _pDUp);
                if (!Sup("DDown")) DispatchButton(p.DDown, s.DDown, ref _pDDown);
                if (!Sup("DLeft")) DispatchButton(p.DLeft, s.DLeft, ref _pDLeft);
                if (!Sup("DRight")) DispatchButton(p.DRight, s.DRight, ref _pDRight);
            }


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

            var comboSup = EvaluateCombos(s, profile.ComboBindings);
            bool Sup(string b) => comboSup.Contains(b);

            // dpad scroll (unless remapped to something else)
            if (!Sup("DUp"))
            {
                if (profile.DUp == "ScrollUp") { if (s.DUp) _sender.MouseWheel(120); }
                else DispatchButton(profile.DUp, s.DUp, ref _pDUp);
            }
            if (!Sup("DDown"))
            {
                if (profile.DDown == "ScrollDown") { if (s.DDown) _sender.MouseWheel(-120); }
                else DispatchButton(profile.DDown, s.DDown, ref _pDDown);
            }
            if (!Sup("DLeft"))
            {
                if (profile.DLeft == "ScrollLeft") { if (s.DLeft) _sender.MouseHWheel(-120); }
                else DispatchButton(profile.DLeft, s.DLeft, ref _pDLeft);
            }
            if (!Sup("DRight"))
            {
                if (profile.DRight == "ScrollRight") { if (s.DRight) _sender.MouseHWheel(120); }
                else DispatchButton(profile.DRight, s.DRight, ref _pDRight);
            }

            // fixed mode-switch: Y press in mouse mode enables the keyboard overlay
            // (suppressed while Y is a combo last-button with modifiers held)
            if (!Sup("Y") && s.Y && !_pY && MouseMode)
            {
                MouseMode = false;
                KeyboardFromMouse = true;
                App.Log("mode: mouse -> keyboard (Y)");
                Notification?.Invoke("Keyboard mode");
                StateChanged?.Invoke();
            }

            // buttons (combo last-button suppressed while its modifiers held)
            if (!Sup("A")) DispatchButton(profile.A, s.A, ref _pA);
            if (!Sup("B")) DispatchButton(profile.B, s.B, ref _pB);
            if (!Sup("X")) DispatchButton(profile.X, s.X, ref _pX);
            if (!MouseMode) return;   // switched to keyboard this tick: skip mouse Y dispatch
            if (!Sup("Y")) DispatchButton(profile.Y, s.Y, ref _pY);
            if (!Sup("LB")) DispatchButton(profile.LB, s.LB, ref _pLB);
            if (!Sup("RB")) DispatchButton(profile.RB, s.RB, ref _pRB);
            if (!Sup("LS")) DispatchButton(profile.LS, s.LS, ref _pLS);
            if (!Sup("RS")) DispatchButton(profile.RS, s.RS, ref _pRS);
            if (!Sup("View")) DispatchButton(profile.View, s.View, ref _pView);
            if (!Sup("Menu")) DispatchButton(profile.Menu, s.Menu, ref _pMenu);
            if (!Sup("LT")) DispatchButton(profile.LT, s.LeftTrigger > 0.5, ref _pLTHeld);
            if (!Sup("RT")) DispatchButton(profile.RT, s.RightTrigger > 0.5, ref _pRTHeld);
        }

        // ── Custom combo bindings ─────────────────────────────────────────────

        /// <summary>
        /// Evaluates the profile's custom combos. Returns the set of last buttons whose
        /// mapped dispatch must be suppressed this tick (combo modifiers are held).
        /// "A+B+X=Act": A+B held = modifiers; X edge = trigger.
        /// </summary>
        private HashSet<string> EvaluateCombos(in GamepadSnapshot s, List<string> combos)
        {
            var suppress = new HashSet<string>();
            if (combos.Count == 0) { _comboPrev.Clear(); return suppress; }

            var seen = new HashSet<string>();
            foreach (var raw in combos)
            {
                int eq = raw.IndexOf('=');
                if (eq <= 0) continue;
                string action = raw[(eq + 1)..].Trim();
                string key = raw[..eq].Trim();
                if (key.Length == 0 || action.Length == 0) continue;
                seen.Add(raw);

                var parts = key.Split('+');
                bool allHeld = true;
                for (int i = 0; i < parts.Length - 1; i++)
                    if (!s.Button(parts[i].Trim())) { allHeld = false; break; }
                if (!allHeld) { _comboPrev[raw] = false; continue; }

                string last = parts[^1].Trim();
                suppress.Add(last);          // mods held: last button belongs to the combo
                bool held = s.Button(last);
                bool prev = _comboPrev.TryGetValue(raw, out var p) && p;
                if (held && !prev)
                {
                    App.Log("combo binding: " + key + " -> " + action);
                    RunActionOnce(action);
                }
                _comboPrev[raw] = held;
            }

            // drop stale entries for removed combos
            var stale = new List<string>();
            foreach (var k in _comboPrev.Keys)
                if (!seen.Contains(k)) stale.Add(k);
            foreach (var k in stale) _comboPrev.Remove(k);
            return suppress;
        }

        /// <summary>Runs an action once for a combo trigger (hold semantics preserved).</summary>
        private void RunActionOnce(string action)
        {
            bool dummy = false;   // prev=false -> DispatchButton sees a press edge
            DispatchButton(action, true, ref dummy);
        }

        // ── Action dispatch ───────────────────────────────────────────────────

        /// <summary>
        /// Runs a mapped action on press edge. Hold-type actions (modifiers,
        /// DisableInput) use the held flag directly; everything else is edge-only.
        /// </summary>
        private string? _heldClickAction;

        private void ReleaseHeldClick()
        {
            if (_heldClickAction == null) return;
            (uint up, uint data) = _heldClickAction switch
            {
                "LeftClick" => (NativeMethods.MOUSEEVENTF_LEFTUP, 0u),
                "RightClick" => (NativeMethods.MOUSEEVENTF_RIGHTUP, 0u),
                "MiddleClick" => (NativeMethods.MOUSEEVENTF_MIDDLEUP, 0u),
                "XButton1" => (NativeMethods.MOUSEEVENTF_XUP, 1u),
                "XButton2" => (NativeMethods.MOUSEEVENTF_XUP, 2u),
                _ => (0u, 0u)
            };
            if (up != 0) _sender.MouseButtonRelease(up, data);
            _heldClickAction = null;
        }

        private void HandleClickHold(string action, bool held, ref bool prev)
        {
            (uint down, uint up, uint data) = action switch
            {
                "LeftClick" => (NativeMethods.MOUSEEVENTF_LEFTDOWN, NativeMethods.MOUSEEVENTF_LEFTUP, 0u),
                "RightClick" => (NativeMethods.MOUSEEVENTF_RIGHTDOWN, NativeMethods.MOUSEEVENTF_RIGHTUP, 0u),
                "MiddleClick" => (NativeMethods.MOUSEEVENTF_MIDDLEDOWN, NativeMethods.MOUSEEVENTF_MIDDLEUP, 0u),
                "XButton1" => (NativeMethods.MOUSEEVENTF_XDOWN, NativeMethods.MOUSEEVENTF_XUP, 1u),
                "XButton2" => (NativeMethods.MOUSEEVENTF_XDOWN, NativeMethods.MOUSEEVENTF_XUP, 2u),
                _ => (0u, 0u, 0u)
            };
            if (down == 0) return;

            if (held && !prev)
            {
                _sender.MouseButtonPress(down, data);
                _heldClickAction = action;
            }
            else if (!held && prev)
            {
                _sender.MouseButtonRelease(up, data);
                _heldClickAction = null;
            }
        }

        /// <summary>
        /// Hold-to-type: activation held = KeyDown on the ray's current key; moving the ray to
        /// another key sends KeyUp(old)+KeyDown(new); releasing activation sends KeyUp.
        /// </summary>
        private void UpdateHeldRayKey(ref KeyboardLayout.KeyDef? heldKey, bool activationHeld, KeyboardLayout.KeyDef? hit)
        {
            if (!activationHeld)
            {
                if (heldKey != null) { _sender.KeyUp(heldKey.Vk, heldKey.Extended); heldKey = null; }
                return;
            }
            if (hit == null)
            {
                if (heldKey != null) { _sender.KeyUp(heldKey.Vk, heldKey.Extended); heldKey = null; }
                return;
            }
            if (!ReferenceEquals(heldKey, hit))
            {
                if (heldKey != null) _sender.KeyUp(heldKey.Vk, heldKey.Extended);
                _sender.KeyDown(hit.Vk, hit.Extended);
                heldKey = hit;
            }
        }

        private void ReleaseHeldRayKeys()
        {
            if (_heldRayKeyL != null) { _sender.KeyUp(_heldRayKeyL.Vk, _heldRayKeyL.Extended); _heldRayKeyL = null; }
            if (_heldRayKeyR != null) { _sender.KeyUp(_heldRayKeyR.Vk, _heldRayKeyR.Extended); _heldRayKeyR = null; }
        }

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

            // hold-to-type: commit buttons hold the ray-highlighted key down (LB/R1 style)
            switch (action)
            {
                case "CommitLeft":
                    UpdateHeldRayKey(ref _heldRayKeyL, held, LeftHit);
                    return;
                case "CommitRight":
                    UpdateHeldRayKey(ref _heldRayKeyR, held, RightHit);
                    return;
            }

            // mouse buttons support HOLD (drag & drop): down on press, up on release
            switch (action)
            {
                case "LeftClick":
                case "RightClick":
                case "MiddleClick":
                case "XButton1":
                case "XButton2":
                    HandleClickHold(action, held, ref prev);
                    return;
            }

            if (!edge) return;

            switch (action)
            {
                case "Backspace": _sender.TapKey(Vk.Back); break;
                case "Space": _sender.TapKey(Vk.Space); break;
                case "Tab": _sender.TapKey(Vk.Tab); break;
                case "Enter": _sender.TapKey(Vk.Return); break;
                case "Escape": _sender.TapKey(Vk.Escape); break;
                case "Delete": _sender.TapKey(Vk.Delete, extended: true); break;
                case "Insert": _sender.TapKey(Vk.Insert, extended: true); break;
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
                    KeyboardFromMouse = false;
                    StateChanged?.Invoke();
                    break;
                case "KeyboardMode": MouseMode = false; KeyboardFromMouse = false; StateChanged?.Invoke(); break;
                case "MouseMode": MouseMode = true; KeyboardFromMouse = false; StateChanged?.Invoke(); break;

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
                case "ToggleKeyboard":
                    var ovs = AppSettings.Instance;
                    ovs.ShowOverlay = !ovs.ShowOverlay;
                    AppSettings.Save();
                    StateChanged?.Invoke();
                    break;

                // mouse actions (also valid in keyboard-mode mappings if wanted)
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
            if (held) { if (_heldModifiers.Add(vk)) { _sender.KeyDown(vk); StateChanged?.Invoke(); } }
            else { if (_heldModifiers.Remove(vk)) { _sender.KeyUp(vk); StateChanged?.Invoke(); } }
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
            StateChanged?.Invoke();   // refresh toggle tint immediately (both modes)
        }

        private void ReleaseAllModifiers()
        {
            foreach (var vk in _heldModifiers)
                _sender.KeyUp(vk);
            _heldModifiers.Clear();
            ReleaseHeldClick();   // no stuck mouse buttons on disable / mode switch
            ReleaseHeldRayKeys(); // no stuck held-typed keys
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
                ushort oem = Keyboard.KeyboardLayout.KeyDef.CharVk(c);
                if (oem != c) { _sender.TapKey(oem); return; }   // punctuation: proper VK_OEM_*
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
                ushort oem = Keyboard.KeyboardLayout.KeyDef.CharVk(c);
                if (oem != c) return oem;   // punctuation: proper VK_OEM_*
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
                "NumPad0" => Vk.NumPad0, "NumPad1" => 0x61, "NumPad2" => 0x62, "NumPad3" => 0x63,
                "NumPad4" => 0x64, "NumPad5" => 0x65, "NumPad6" => 0x66, "NumPad7" => 0x67,
                "NumPad8" => 0x68, "NumPad9" => Vk.NumPad9,
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

        /// <summary>Max ray length = key-center distance LeftCtrl -> Backspace in pixels.</summary>
        private double MaxRayLength()
        {
            var a = _layout.FindByVk(Vk.LControl);
            var b = _layout.FindByVk(Vk.Back);
            if (a == null || b == null) return _layout.GridH * Pitch();
            double dx = b.X - a.X, dy = b.Y - a.Y;
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