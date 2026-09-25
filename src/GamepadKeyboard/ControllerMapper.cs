using System;
using System.Collections.Generic;
using System.Diagnostics;
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
        private readonly Stopwatch _cursorClock = Stopwatch.StartNew();
        private double _lastCursorTime;
        private bool _cursorInitialized;
        private bool _lastFreeCursor;
        private double _leftTargetX, _leftTargetY, _rightTargetX, _rightTargetY;

        public bool MouseMode { get; set; }

        /// <summary>When disabled the pad is passed through untouched (game use).</summary>
        public bool InputEnabled { get; private set; }   // starts disabled: gamepad free for games until enable combo

        public void SetInputEnabled(bool enabled)
        {
            if (InputEnabled == enabled) return;
            App.Log("input enabled -> " + enabled);
            InputEnabled = enabled;
            if (!enabled) ReleaseAllModifiers();
            InputEnabledChanged?.Invoke(enabled);
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
        public double LeftCursorX { get; private set; }
        public double LeftCursorY { get; private set; }
        public double RightCursorX { get; private set; }
        public double RightCursorY { get; private set; }
        public double LeftRayX { get; private set; }
        public double LeftRayY { get; private set; }
        public double RightRayX { get; private set; }
        public double RightRayY { get; private set; }
        public bool LeftCursorActive { get; private set; }
        public bool RightCursorActive { get; private set; }

        /// <summary>Profile-defined keyboard move/scale mode.</summary>
        public bool AdjustMoveScaleKeyboard { get; set; }
        public double MoveDX { get; private set; }
        public double MoveDY { get; private set; }
        public double ScaleDelta { get; private set; }   // per-tick, up/down = +/-


        // currently held virtual modifier keys (toggle or hold)
        private readonly HashSet<ushort> _heldModifiers = new();
        private readonly HashSet<ushort> _toggledModifiers = new();
        private readonly Dictionary<ushort, int> _heldModifierCounts = new();

        // hold-to-type state: key currently held down by the left/right commit activation
        private KeyboardLayout.KeyDef? _heldRayKeyL, _heldRayKeyR;
        private bool _leftModifierSubmit, _rightModifierSubmit;

        /// <summary>Keys currently held via hold-to-type (for UI tint; modifiers only tint).</summary>
        public System.Collections.Generic.IEnumerable<ushort> HeldRayKeyVks
        {
            get
            {
                if (_heldRayKeyL != null) yield return _heldRayKeyL.Vk;
                if (_heldRayKeyR != null) yield return _heldRayKeyR.Vk;
            }
        }

        /// <summary>Virtual modifier keys currently active (toggled on or held) — for UI tint.</summary>
        public IReadOnlyCollection<ushort> HeldModifierVks => _heldModifiers;
        public bool ShiftActive => _heldModifiers.Contains(Vk.LShift) || _heldModifiers.Contains(Vk.RShift);

        // custom combo state and explicit release-only physical modifiers
        private readonly Dictionary<string, ComboRuntimeState> _comboStates = new(StringComparer.Ordinal);
        private readonly HashSet<string> _comboConsumedButtons = new();
        private readonly HashSet<string> _modifierButtonsUsed = new(StringComparer.Ordinal);
        private readonly List<string> _staleComboKeys = new();
        private readonly List<string> _releasedComboButtons = new();
        private readonly Dictionary<ushort, int> _heldKeyCounts = new();
        private readonly Dictionary<ushort, double> _nextKeyRepeat = new();

        // previous physical state (edge detection)
        private bool _pA, _pB, _pX, _pY, _pLB, _pRB, _pLS, _pRS;
        private bool _pDUp, _pDDown, _pDLeft, _pDRight;
        private bool _pLUpAxis, _pLDownAxis, _pLLeftAxis, _pLRightAxis;
        private bool _pRUpAxis, _pRDownAxis, _pRLeftAxis, _pRRightAxis;
        /// <summary>Raised when disable/enable happens or profile changes (UI toast).</summary>
        public event Action<string>? Notification;
        public event Action? StateChanged;
        public event Action<bool>? InputEnabledChanged;

        public ControllerMapper(KeyboardLayout layout)
        {
            _layout = layout;
            ResetKeyboardCursors();
        }

        public void ResetKeyboardCursors()
        {
            var p = AppSettings.Instance.StickPointsProfile;
            double width = KeyboardWidth();
            double height = KeyboardHeight();
            LeftCursorX = _leftTargetX = 4 + p.LeftX * width;
            LeftCursorY = _leftTargetY = 4 + p.LeftY * height;
            RightCursorX = _rightTargetX = 4 + p.RightX * width;
            RightCursorY = _rightTargetY = 4 + p.RightY * height;
            LeftRayX = LeftCursorX;
            LeftRayY = LeftCursorY;
            RightRayX = RightCursorX;
            RightRayY = RightCursorY;
            LeftCursorActive = RightCursorActive = AppSettings.Instance.FreeCursorEnabled;
            LeftHit = RightHit = null;
            _cursorInitialized = true;
            _lastFreeCursor = AppSettings.Instance.FreeCursorEnabled;
            _lastCursorTime = _cursorClock.Elapsed.TotalSeconds;
        }

        public void Process(in GamepadSnapshot s)
        {
            if (!InputEnabled)
            {
                bool mappedButtonEnabled = MouseMode
                    ? TryEnableFromMappedButton(s, AppSettings.Instance.MouseProfile)
                    : TryEnableFromMappedButton(s, AppSettings.Instance.Profile);
                var enableBindings = MouseMode
                    ? AppSettings.Instance.MouseProfile.ComboBindings
                    : AppSettings.Instance.Profile.ComboBindings;
                if (mappedButtonEnabled || TryEnableFromProfile(s, enableBindings))
                {
                    SaveEdges(s); // consume the reading containing the custom enable combo
                    return;
                }

                // pass-through: app injects nothing, game sees the pad natively
                AdjustMoveScaleKeyboard = false;
                MoveDX = MoveDY = ScaleDelta = 0;
                ClearAllEdges(s);
                return;
            }

            if (MouseMode)
                ProcessMouse(s);
            else
                ProcessKeyboardMode(s);

            RepeatHeldKeys();
            SaveEdges(s);
        }

        // ── Keyboard mode ─────────────────────────────────────────────────────

        private void ProcessKeyboardMode(in GamepadSnapshot s)
        {
            var p = AppSettings.Instance.Profile;
            EvaluateCombos(s, p.ComboBindings);
            if (!InputEnabled || MouseMode) { FinishComboFrame(s); return; }

            MoveDX = MoveDY = ScaleDelta = 0;
            if (AdjustMoveScaleKeyboard)
            {
                // Left stick scales and right stick moves the keyboard.
                ApplyRadialStickCurve(s.RX, s.RY, out double curvedMoveX, out double curvedMoveY);
                MoveDX = curvedMoveX;
                MoveDY = curvedMoveY;
                ScaleDelta = ApplyStickCurve(s.LY);
            }
            else
            {
                UpdateKeyboardCursors(s);
            }

            DispatchProfileButton("LT", p.LT, s.LeftTrigger > 0.5, ref _pLTHeld, s);
            DispatchProfileButton("RT", p.RT, s.RightTrigger > 0.5, ref _pRTHeld, s);

            // Explicit modifier bindings defer their standalone action to release;
            // ordinary bindings preserve immediate down/up behavior.
            DispatchProfileButton("A", p.A, s.A, ref _pA, s);
            if (!InputEnabled || MouseMode) { FinishComboFrame(s); return; }
            DispatchProfileButton("B", p.B, s.B, ref _pB, s);
            if (!InputEnabled || MouseMode) { FinishComboFrame(s); return; }
            DispatchProfileButton("X", p.X, s.X, ref _pX, s);
            DispatchProfileButton("Y", p.Y, s.Y, ref _pY, s);

            DispatchProfileButton("LB", p.LB, s.LB, ref _pLB, s);
            DispatchProfileButton("RB", p.RB, s.RB, ref _pRB, s);
            DispatchProfileButton("LS", p.LS, s.LS, ref _pLS, s);
            DispatchProfileButton("RS", p.RS, s.RS, ref _pRS, s);

            DispatchProfileButton("View", p.View, s.View, ref _pView, s);
            if (!InputEnabled || MouseMode) { FinishComboFrame(s); return; }
            DispatchProfileButton("Menu", p.Menu, s.Menu, ref _pMenu, s);
            if (!InputEnabled || MouseMode) { FinishComboFrame(s); return; }

            DispatchProfileButton("DUp", p.DUp, s.DUp, ref _pDUp, s);
            DispatchProfileButton("DDown", p.DDown, s.DDown, ref _pDDown, s);
            DispatchProfileButton("DLeft", p.DLeft, s.DLeft, ref _pDLeft, s);
            DispatchProfileButton("DRight", p.DRight, s.DRight, ref _pDRight, s);
            FinishComboFrame(s);
        }

        // ── Mouse mode ────────────────────────────────────────────────────────

        private void ProcessMouse(in GamepadSnapshot s)
        {
            var profile = AppSettings.Instance.MouseProfile;
            var st = AppSettings.Instance;

            bool boost = IsActionHeld(profile, "SpeedBoost", s);
            double speed = st.MouseSpeed * (boost ? st.MouseSpeedBoostMultiplier : 1.0);
            ApplyRadialStickCurve(s.LX, s.LY, out double leftX, out double leftY);
            ApplyRadialStickCurve(s.RX, s.RY, out double rightX, out double rightY);

            double moveX = 0, moveY = 0, scrollX = 0, scrollY = 0;
            DispatchAnalogDirection(profile.LUp, Math.Max(0, leftY), ref _pLUpAxis,
                ref moveX, ref moveY, ref scrollX, ref scrollY);
            DispatchAnalogDirection(profile.LDown, Math.Max(0, -leftY), ref _pLDownAxis,
                ref moveX, ref moveY, ref scrollX, ref scrollY);
            DispatchAnalogDirection(profile.LLeft, Math.Max(0, -leftX), ref _pLLeftAxis,
                ref moveX, ref moveY, ref scrollX, ref scrollY);
            DispatchAnalogDirection(profile.LRight, Math.Max(0, leftX), ref _pLRightAxis,
                ref moveX, ref moveY, ref scrollX, ref scrollY);
            DispatchAnalogDirection(profile.RUp, Math.Max(0, rightY), ref _pRUpAxis,
                ref moveX, ref moveY, ref scrollX, ref scrollY);
            DispatchAnalogDirection(profile.RDown, Math.Max(0, -rightY), ref _pRDownAxis,
                ref moveX, ref moveY, ref scrollX, ref scrollY);
            DispatchAnalogDirection(profile.RLeft, Math.Max(0, -rightX), ref _pRLeftAxis,
                ref moveX, ref moveY, ref scrollX, ref scrollY);
            DispatchAnalogDirection(profile.RRight, Math.Max(0, rightX), ref _pRRightAxis,
                ref moveX, ref moveY, ref scrollX, ref scrollY);

            int dx = (int)Math.Round(moveX * speed);
            int dy = (int)Math.Round(moveY * speed);
            if (dx != 0 || dy != 0) _sender.MouseMove(dx, dy);
            int vertical = (int)Math.Round(scrollY * 120 * st.ScrollSpeed / 3.0);
            int horizontal = (int)Math.Round(scrollX * 120 * st.ScrollSpeed / 3.0);
            if (vertical != 0) SendVerticalScroll(vertical);
            if (horizontal != 0) SendHorizontalScroll(horizontal);

            EvaluateCombos(s, profile.ComboBindings);
            if (!InputEnabled || !MouseMode) { FinishComboFrame(s); return; }

            DispatchProfileButton("DUp", profile.DUp, s.DUp, ref _pDUp, s);
            DispatchProfileButton("DDown", profile.DDown, s.DDown, ref _pDDown, s);
            DispatchProfileButton("DLeft", profile.DLeft, s.DLeft, ref _pDLeft, s);
            DispatchProfileButton("DRight", profile.DRight, s.DRight, ref _pDRight, s);

            DispatchProfileButton("A", profile.A, s.A, ref _pA, s);
            if (!InputEnabled || !MouseMode) { FinishComboFrame(s); return; }
            DispatchProfileButton("B", profile.B, s.B, ref _pB, s);
            if (!InputEnabled || !MouseMode) { FinishComboFrame(s); return; }
            DispatchProfileButton("X", profile.X, s.X, ref _pX, s);
            if (!InputEnabled || !MouseMode) { FinishComboFrame(s); return; }
            DispatchProfileButton("Y", profile.Y, s.Y, ref _pY, s);
            if (!InputEnabled || !MouseMode) { FinishComboFrame(s); return; }
            DispatchProfileButton("LB", profile.LB, s.LB, ref _pLB, s);
            DispatchProfileButton("RB", profile.RB, s.RB, ref _pRB, s);
            DispatchProfileButton("LS", profile.LS, s.LS, ref _pLS, s);
            DispatchProfileButton("RS", profile.RS, s.RS, ref _pRS, s);
            DispatchProfileButton("View", profile.View, s.View, ref _pView, s);
            if (!InputEnabled || !MouseMode) { FinishComboFrame(s); return; }
            DispatchProfileButton("Menu", profile.Menu, s.Menu, ref _pMenu, s);
            if (!InputEnabled || !MouseMode) { FinishComboFrame(s); return; }
            DispatchProfileButton("LT", profile.LT, s.LeftTrigger > 0.5, ref _pLTHeld, s);
            DispatchProfileButton("RT", profile.RT, s.RightTrigger > 0.5, ref _pRTHeld, s);
            FinishComboFrame(s);
        }

        private void DispatchAnalogDirection(
            ButtonBinding binding,
            double value,
            ref bool previous,
            ref double moveX,
            ref double moveY,
            ref double scrollX,
            ref double scrollY)
        {
            switch (binding.Action)
            {
                case "MouseMoveUp": moveY -= value; previous = false; return;
                case "MouseMoveDown": moveY += value; previous = false; return;
                case "MouseMoveLeft": moveX -= value; previous = false; return;
                case "MouseMoveRight": moveX += value; previous = false; return;
                case "AnalogScrollUp": scrollY += value; previous = false; return;
                case "AnalogScrollDown": scrollY -= value; previous = false; return;
                case "AnalogScrollLeft": scrollX -= value; previous = false; return;
                case "AnalogScrollRight": scrollX += value; previous = false; return;
            }

            bool held = value >= 0.5;
            DispatchButton(binding.Action, held, ref previous);
        }

        private static bool IsActionHeld(MouseProfile profile, string action, in GamepadSnapshot s)
        {
            bool Active(ButtonBinding binding, bool held) =>
                !binding.Modifier && binding.Action == action && held;
            return Active(profile.A, s.A)
                || Active(profile.B, s.B)
                || Active(profile.X, s.X)
                || Active(profile.Y, s.Y)
                || Active(profile.LB, s.LB)
                || Active(profile.RB, s.RB)
                || Active(profile.LT, s.LeftTrigger > 0.5)
                || Active(profile.RT, s.RightTrigger > 0.5)
                || Active(profile.LS, s.LS)
                || Active(profile.RS, s.RS)
                || Active(profile.View, s.View)
                || Active(profile.Menu, s.Menu)
                || Active(profile.DUp, s.DUp)
                || Active(profile.DDown, s.DDown)
                || Active(profile.DLeft, s.DLeft)
                || Active(profile.DRight, s.DRight);
        }

        private void SendVerticalScroll(int delta)
        {
            _sender.MouseWheel(AppSettings.Instance.InvertVerticalScroll ? -delta : delta);
        }

        private void SendHorizontalScroll(int delta)
        {
            _sender.MouseHWheel(AppSettings.Instance.InvertHorizontalScroll ? -delta : delta);
        }

        // ── Custom combo bindings ─────────────────────────────────────────────

        private bool TryEnableFromMappedButton(in GamepadSnapshot s, KeyboardProfile profile) =>
            TryEnableAction("A", profile.A, s.A, _pA, s)
            || TryEnableAction("B", profile.B, s.B, _pB, s)
            || TryEnableAction("X", profile.X, s.X, _pX, s)
            || TryEnableAction("Y", profile.Y, s.Y, _pY, s)
            || TryEnableAction("LB", profile.LB, s.LB, _pLB, s)
            || TryEnableAction("RB", profile.RB, s.RB, _pRB, s)
            || TryEnableAction("LT", profile.LT, s.LeftTrigger > 0.5, _pLTHeld, s)
            || TryEnableAction("RT", profile.RT, s.RightTrigger > 0.5, _pRTHeld, s)
            || TryEnableAction("LS", profile.LS, s.LS, _pLS, s)
            || TryEnableAction("RS", profile.RS, s.RS, _pRS, s)
            || TryEnableAction("View", profile.View, s.View, _pView, s)
            || TryEnableAction("Menu", profile.Menu, s.Menu, _pMenu, s)
            || TryEnableAction("DUp", profile.DUp, s.DUp, _pDUp, s)
            || TryEnableAction("DDown", profile.DDown, s.DDown, _pDDown, s)
            || TryEnableAction("DLeft", profile.DLeft, s.DLeft, _pDLeft, s)
            || TryEnableAction("DRight", profile.DRight, s.DRight, _pDRight, s);

        private bool TryEnableFromMappedButton(in GamepadSnapshot s, MouseProfile profile) =>
            TryEnableAction("A", profile.A, s.A, _pA, s)
            || TryEnableAction("B", profile.B, s.B, _pB, s)
            || TryEnableAction("X", profile.X, s.X, _pX, s)
            || TryEnableAction("Y", profile.Y, s.Y, _pY, s)
            || TryEnableAction("LB", profile.LB, s.LB, _pLB, s)
            || TryEnableAction("RB", profile.RB, s.RB, _pRB, s)
            || TryEnableAction("LT", profile.LT, s.LeftTrigger > 0.5, _pLTHeld, s)
            || TryEnableAction("RT", profile.RT, s.RightTrigger > 0.5, _pRTHeld, s)
            || TryEnableAction("LS", profile.LS, s.LS, _pLS, s)
            || TryEnableAction("RS", profile.RS, s.RS, _pRS, s)
            || TryEnableAction("View", profile.View, s.View, _pView, s)
            || TryEnableAction("Menu", profile.Menu, s.Menu, _pMenu, s)
            || TryEnableAction("DUp", profile.DUp, s.DUp, _pDUp, s)
            || TryEnableAction("DDown", profile.DDown, s.DDown, _pDDown, s)
            || TryEnableAction("DLeft", profile.DLeft, s.DLeft, _pDLeft, s)
            || TryEnableAction("DRight", profile.DRight, s.DRight, _pDRight, s);

        private bool TryEnableAction(
            string button,
            ButtonBinding binding,
            bool held,
            bool previous,
            in GamepadSnapshot snapshot)
        {
            if (binding.Action != "EnableInput" && binding.Action != "ToggleInput") return false;
            if (binding.Modifier)
            {
                if (held && AnyOtherPhysicalButtonHeld(button, snapshot))
                    _modifierButtonsUsed.Add(button);
                if (held || !previous || _modifierButtonsUsed.Remove(button)
                    || AnyOtherPhysicalButtonHeld(button, snapshot)) return false;
            }
            else if (!held || previous)
            {
                return false;
            }
            SetInputEnabled(true);
            return true;
        }

        private bool TryEnableFromProfile(
            in GamepadSnapshot s,
            List<CustomComboBinding> combos)
        {
            foreach (var binding in combos)
            {
                if (binding.Action != "EnableInput" && binding.Action != "ToggleInput")
                    continue;

                var parts = binding.Buttons;
                if (parts.Count < 2) continue;
                var state = GetComboState(binding.Id);

                bool modifiersHeld = true;
                for (int i = 0; i < parts.Count - 1; i++)
                {
                    if (!s.Button(parts[i]))
                    {
                        modifiersHeld = false;
                        break;
                    }
                }

                string last = parts[^1];
                bool held = modifiersHeld && s.Button(last);
                bool previous = state.LastButtonHeld;
                state.LastButtonHeld = s.Button(last);
                if (held && !previous)
                {
                    foreach (var part in parts)
                    {
                        _comboConsumedButtons.Add(part);
                        _modifierButtonsUsed.Add(part);
                    }
                    App.Log("input enabled by profile binding: " + string.Join("+", parts));
                    SetInputEnabled(true);
                    return true;
                }
            }
            return false;
        }

        /// <summary>
        /// Evaluates custom combos. Earlier gamepad buttons gate the final trigger;
        /// optional keyboard modifiers remain held until that gate is released.
        /// </summary>
        private void EvaluateCombos(
            in GamepadSnapshot s,
            List<CustomComboBinding> combos)
        {
            var currentIds = new HashSet<string>(StringComparer.Ordinal);
            foreach (var binding in combos)
            {
                currentIds.Add(binding.Id);
                var parts = binding.Buttons;
                if (parts.Count < 2) continue;
                var state = GetComboState(binding.Id);

                bool prefixHeld = true;
                for (int i = 0; i < parts.Count - 1; i++)
                    if (!s.Button(parts[i])) { prefixHeld = false; break; }

                string last = parts[^1];
                bool lastHeld = s.Button(last);

                if (state.ActionHeld && !lastHeld)
                {
                    DispatchButton(state.Action, false, ref state.ActionPrevious);
                    state.ActionHeld = false;
                }

                if (state.Active && !prefixHeld)
                {
                    if (state.ActionHeld)
                    {
                        DispatchButton(state.Action, false, ref state.ActionPrevious);
                        state.ActionHeld = false;
                    }
                    ReleaseComboModifiers(state);
                    state.Active = false;
                }

                if (prefixHeld && lastHeld && !state.LastButtonHeld)
                {
                    foreach (var part in parts)
                    {
                        _comboConsumedButtons.Add(part);
                        _modifierButtonsUsed.Add(part);
                    }
                    if (!state.Active)
                    {
                        AcquireComboModifiers(binding, state);
                        state.Active = true;
                    }
                    App.Log("combo binding: " + string.Join("+", parts) + " -> " + binding.Action);
                    if (binding.HoldLast)
                    {
                        state.Action = binding.Action;
                        DispatchButton(binding.Action, true, ref state.ActionPrevious);
                        state.ActionHeld = true;
                    }
                    else
                    {
                        RunActionOnce(binding.Action);
                    }
                }
                state.LastButtonHeld = lastHeld;
            }

            _staleComboKeys.Clear();
            foreach (var key in _comboStates.Keys)
                if (!currentIds.Contains(key)) _staleComboKeys.Add(key);
            foreach (var key in _staleComboKeys)
            {
                ReleaseComboState(_comboStates[key]);
                _comboStates.Remove(key);
            }
        }

        private ComboRuntimeState GetComboState(string id)
        {
            if (_comboStates.TryGetValue(id, out var state)) return state;
            state = new ComboRuntimeState();
            _comboStates[id] = state;
            return state;
        }

        private void AcquireComboModifiers(CustomComboBinding binding, ComboRuntimeState state)
        {
            if (binding.Ctrl) AcquireComboModifier(Vk.LControl, state);
            if (binding.Shift) AcquireComboModifier(Vk.LShift, state);
            if (binding.Alt) AcquireComboModifier(Vk.LMenu, state);
        }

        private void AcquireComboModifier(ushort vk, ComboRuntimeState state)
        {
            if (state.Modifiers.Contains(vk)) return;
            state.Modifiers.Add(vk);
            int count = _heldModifierCounts.TryGetValue(vk, out int current) ? current + 1 : 1;
            _heldModifierCounts[vk] = count;
            if (_heldModifiers.Add(vk)) _sender.KeyDown(vk);
            StateChanged?.Invoke();
        }

        private void ReleaseComboModifiers(ComboRuntimeState state)
        {
            foreach (ushort vk in state.Modifiers)
            {
                int count = _heldModifierCounts.TryGetValue(vk, out int current)
                    ? Math.Max(0, current - 1)
                    : 0;
                if (count == 0) _heldModifierCounts.Remove(vk);
                else _heldModifierCounts[vk] = count;
                if (count == 0 && !_toggledModifiers.Contains(vk) && _heldModifiers.Remove(vk))
                    _sender.KeyUp(vk);
            }
            state.Modifiers.Clear();
            StateChanged?.Invoke();
        }

        private void ReleaseComboState(ComboRuntimeState state)
        {
            if (state.ActionHeld)
            {
                DispatchButton(state.Action, false, ref state.ActionPrevious);
                state.ActionHeld = false;
            }
            ReleaseComboModifiers(state);
            state.Active = false;
        }

        /// <summary>Runs a complete press/release pulse for a combo or deferred single action.</summary>
        private void RunActionOnce(string action)
        {
            bool previous = false;
            DispatchButton(action, true, ref previous);
            previous = true;
            DispatchButton(action, false, ref previous);
        }

        private void DispatchProfileButton(
            string button,
            ButtonBinding binding,
            bool held,
            ref bool previous,
            in GamepadSnapshot snapshot)
        {
            if (!binding.Modifier)
            {
                DispatchButton(binding.Action, held, ref previous);
                return;
            }

            if (held && !previous)
                _modifierButtonsUsed.Remove(button);
            if (held && AnyOtherPhysicalButtonHeld(button, snapshot))
                _modifierButtonsUsed.Add(button);
            if (!held && previous)
            {
                bool used = _modifierButtonsUsed.Remove(button)
                    || _comboConsumedButtons.Contains(button)
                    || AnyOtherPhysicalButtonHeld(button, snapshot);
                if (!used) RunActionOnce(binding.Action);
            }
            previous = held;
        }

        private static readonly string[] PhysicalButtons =
        {
            "A", "B", "X", "Y", "LB", "RB", "LT", "RT", "LS", "RS",
            "View", "Menu", "Home", "DUp", "DDown", "DLeft", "DRight"
        };

        private static bool AnyOtherPhysicalButtonHeld(string button, in GamepadSnapshot snapshot)
        {
            foreach (string candidate in PhysicalButtons)
                if (candidate != button && snapshot.Button(candidate)) return true;
            return false;
        }

        private void FinishComboFrame(in GamepadSnapshot s)
        {
            // An in parameter cannot be captured by RemoveWhere's predicate.
            // Collect released buttons first, then mutate the set separately.
            _releasedComboButtons.Clear();
            foreach (var button in _comboConsumedButtons)
                if (!s.Button(button)) _releasedComboButtons.Add(button);
            foreach (var button in _releasedComboButtons)
                _comboConsumedButtons.Remove(button);
        }

        // ── Action dispatch ───────────────────────────────────────────────────

        /// <summary>
        /// Runs a mapped action. Keyboard keys and mouse buttons preserve their
        /// down/up state; application commands use the press edge.
        /// </summary>
        private readonly Dictionary<string, int> _heldClickCounts = new();

        private void ReleaseHeldClicks()
        {
            foreach (string action in _heldClickCounts.Keys)
            {
                (uint up, uint data) = ClickRelease(action);
                if (up != 0) _sender.MouseButtonRelease(up, data);
            }
            _heldClickCounts.Clear();
        }

        private void HandleClickHold(string action, bool held, bool previous)
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

            if (held && !previous)
            {
                int count = _heldClickCounts.TryGetValue(action, out var current) ? current + 1 : 1;
                _heldClickCounts[action] = count;
                if (count == 1) _sender.MouseButtonPress(down, data);
            }
            else if (!held && previous)
            {
                int count = _heldClickCounts.TryGetValue(action, out var current)
                    ? Math.Max(0, current - 1)
                    : 0;
                if (count == 0)
                {
                    _heldClickCounts.Remove(action);
                    _sender.MouseButtonRelease(up, data);
                }
                else
                {
                    _heldClickCounts[action] = count;
                }
            }
        }

        private static (uint up, uint data) ClickRelease(string action) => action switch
        {
            "LeftClick" => (NativeMethods.MOUSEEVENTF_LEFTUP, 0u),
            "RightClick" => (NativeMethods.MOUSEEVENTF_RIGHTUP, 0u),
            "MiddleClick" => (NativeMethods.MOUSEEVENTF_MIDDLEUP, 0u),
            "XButton1" => (NativeMethods.MOUSEEVENTF_XUP, 1u),
            "XButton2" => (NativeMethods.MOUSEEVENTF_XUP, 2u),
            _ => (0u, 0u)
        };

        /// <summary>
        /// Hold-to-type: activation held = KeyDown on the ray's current key; moving the ray to
        /// another key sends KeyUp(old)+KeyDown(new); releasing activation sends KeyUp.
        /// </summary>
        private void UpdateHeldRayKey(
            ref KeyboardLayout.KeyDef? heldKey,
            ref bool modifierSubmit,
            bool activationHeld,
            bool activationWasHeld,
            KeyboardLayout.KeyDef? hit)
        {
            if (!activationHeld)
            {
                if (heldKey != null) { _sender.KeyUp(heldKey.Vk, heldKey.Extended); heldKey = null; }
                modifierSubmit = false;
                return;
            }

            // Submitting a virtual modifier latches it instead of treating it as a
            // normal hold-to-type key. It stays down until the modifier is submitted
            // again or its mapped Hold/Toggle action takes ownership of the state.
            if (!activationWasHeld && hit != null && IsModifierVk(hit.Vk))
            {
                if (heldKey != null) { _sender.KeyUp(heldKey.Vk, heldKey.Extended); heldKey = null; }
                modifierSubmit = true;
                ToggleModifier(hit.Vk);
                return;
            }
            if (modifierSubmit) return;

            if (hit == null)
            {
                if (heldKey != null) { _sender.KeyUp(heldKey.Vk, heldKey.Extended); heldKey = null; }
                return;
            }
            if (IsModifierVk(hit.Vk))
            {
                // Moving onto a modifier while submit is already held must not
                // toggle it; only a fresh submit on that key changes its latch.
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
            _leftModifierSubmit = _rightModifierSubmit = false;
        }

        private void DispatchButton(string action, bool held, ref bool prev)
        {
            bool previous = prev;
            bool edge = held && !previous;
            prev = held;
            switch (action)
            {
                case "HoldShift":
                case "HoldCtrl":
                case "HoldAlt":
                case "HoldWin":
                    ApplyHeld(action, held, previous);
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
                case "SubmitLeft":
                case "CommitLeft": // legacy saved profiles
                    UpdateHeldRayKey(ref _heldRayKeyL, ref _leftModifierSubmit, held, previous, LeftHit);
                    return;
                case "SubmitRight":
                case "CommitRight": // legacy saved profiles
                    UpdateHeldRayKey(ref _heldRayKeyR, ref _rightModifierSubmit, held, previous, RightHit);
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
                    HandleClickHold(action, held, previous);
                    return;
            }

            if (TryResolveKeyAction(action, out ushort key, out bool extended))
            {
                HandleKeyHold(key, extended, held, previous);
                return;
            }

            if (held)
            {
                switch (action)
                {
                    case "ScrollUp": SendVerticalScroll(120); return;
                    case "ScrollDown": SendVerticalScroll(-120); return;
                    case "ScrollLeft": SendHorizontalScroll(-120); return;
                    case "ScrollRight": SendHorizontalScroll(120); return;
                }
            }

            if (!edge) return;

            switch (action)
            {
                case "DisableInput":
                    App.Log("input disabled by profile action");
                    SetInputEnabled(false);
                    break;

                case "EnableInput":
                    SetInputEnabled(true);
                    break;

                case "ToggleInput":
                    SetInputEnabled(!InputEnabled);
                    break;

                case "ToggleKeyboardMouseMode":
                    ReleaseAllModifiers();
                    MouseMode = !MouseMode;
                    StateChanged?.Invoke();
                    break;
                case "KeyboardMode":
                    if (MouseMode) ReleaseAllModifiers();
                    MouseMode = false;
                    StateChanged?.Invoke();
                    break;
                case "MouseMode":
                    if (!MouseMode) ReleaseAllModifiers();
                    MouseMode = true;
                    StateChanged?.Invoke();
                    break;

                case "ToggleMoveScaleKeyboard":
                case "ToggleMoveMode": // legacy saved profile
                case "ToggleScaleMode": // legacy saved profile
                    AdjustMoveScaleKeyboard = !AdjustMoveScaleKeyboard;
                    MoveDX = MoveDY = 0;
                    ScaleDelta = 0;
                    if (AdjustMoveScaleKeyboard)
                    {
                        LeftCursorActive = RightCursorActive = false;
                        LeftHit = RightHit = null;
                        ReleaseHeldRayKeys();
                    }
                    Notification?.Invoke(AdjustMoveScaleKeyboard
                        ? "Keyboard move/scale ON — right stick moves, left stick scales"
                        : "Keyboard move/scale OFF");
                    break;

                case "SwitchKeyboardProfile":
                    SwitchProfile(+1);
                    break;
                case "SwitchStickPointsProfile":
                    SwitchStickPointsProfile(+1);
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

                case "SpeedBoost":
                case "PointerMode":
                case "MouseMoveUp":
                case "MouseMoveDown":
                case "MouseMoveLeft":
                case "MouseMoveRight":
                case "AnalogScrollUp":
                case "AnalogScrollDown":
                case "AnalogScrollLeft":
                case "AnalogScrollRight":
                    break; // handled elsewhere / no-op

                default:
                    if (action.StartsWith("Combo:", StringComparison.Ordinal))
                    {
                        SendNamedCombo(action[6..]);
                    }
                    break;
            }
        }

        private void ApplyHeld(string action, bool held, bool previous)
        {
            ushort vk = ActionToVk(action);
            if (held && !previous)
            {
                // A mapped Hold action supersedes any sticky virtual/toggle state
                // in the same modifier family. Keep this exact key down while the
                // hold source takes ownership, avoiding an artificial up/down pulse.
                ClearToggledModifierFamily(vk, vk);
                int count = _heldModifierCounts.TryGetValue(vk, out var current) ? current + 1 : 1;
                _heldModifierCounts[vk] = count;
                if (_heldModifiers.Add(vk)) _sender.KeyDown(vk);
                StateChanged?.Invoke();
            }
            else if (!held && previous)
            {
                int count = _heldModifierCounts.TryGetValue(vk, out var current) ? Math.Max(0, current - 1) : 0;
                if (count == 0) _heldModifierCounts.Remove(vk);
                else _heldModifierCounts[vk] = count;

                if (count == 0 && !_toggledModifiers.Contains(vk) && _heldModifiers.Remove(vk))
                    _sender.KeyUp(vk);
                StateChanged?.Invoke();
            }
        }

        private void HandleKeyHold(ushort vk, bool extended, bool held, bool previous)
        {
            if (held && !previous)
            {
                int count = _heldKeyCounts.TryGetValue(vk, out int current) ? current + 1 : 1;
                _heldKeyCounts[vk] = count;
                if (count == 1)
                {
                    _sender.KeyDown(vk, extended);
                    _nextKeyRepeat[vk] = _cursorClock.Elapsed.TotalSeconds + 0.5;
                }
            }
            else if (!held && previous)
            {
                int count = _heldKeyCounts.TryGetValue(vk, out int current)
                    ? Math.Max(0, current - 1)
                    : 0;
                if (count == 0)
                {
                    _heldKeyCounts.Remove(vk);
                    _nextKeyRepeat.Remove(vk);
                    _sender.KeyUp(vk, extended);
                }
                else
                {
                    _heldKeyCounts[vk] = count;
                }
            }
        }

        private void RepeatHeldKeys()
        {
            double now = _cursorClock.Elapsed.TotalSeconds;
            foreach (var pair in _heldKeyCounts)
            {
                if (!_nextKeyRepeat.TryGetValue(pair.Key, out double next) || now < next) continue;
                _sender.KeyDown(pair.Key, IsExtendedKey(pair.Key));
                _nextKeyRepeat[pair.Key] = now + 0.033;
            }
        }

        private static bool TryResolveKeyAction(string action, out ushort vk, out bool extended)
        {
            string name = (action.StartsWith("Key:", StringComparison.Ordinal) ? action[4..] : action).Trim();
            vk = NamedVk(name);
            extended = IsExtendedKey(vk);
            return vk != Vk.None;
        }

        private void ToggleModifier(ushort vk)
        {
            var activeFamily = new List<ushort>();
            foreach (var active in _toggledModifiers)
                if (SameModifierFamily(active, vk)) activeFamily.Add(active);

            if (activeFamily.Count > 0)
            {
                foreach (var active in activeFamily)
                {
                    _toggledModifiers.Remove(active);
                    if (!_heldModifierCounts.ContainsKey(active) && _heldModifiers.Remove(active))
                        _sender.KeyUp(active);
                }
            }
            else
            {
                _toggledModifiers.Add(vk);
                if (_heldModifiers.Add(vk)) _sender.KeyDown(vk);
            }
            StateChanged?.Invoke();   // refresh toggle tint immediately (both modes)
        }

        private void ClearToggledModifierFamily(ushort vk, ushort preserveDownVk)
        {
            var activeFamily = new List<ushort>();
            foreach (var active in _toggledModifiers)
                if (SameModifierFamily(active, vk)) activeFamily.Add(active);

            foreach (var active in activeFamily)
            {
                _toggledModifiers.Remove(active);
                if (active != preserveDownVk
                    && !_heldModifierCounts.ContainsKey(active)
                    && _heldModifiers.Remove(active))
                {
                    _sender.KeyUp(active);
                }
            }
        }

        private static bool SameModifierFamily(ushort left, ushort right) =>
            ModifierFamily(left) != 0 && ModifierFamily(left) == ModifierFamily(right);

        private static int ModifierFamily(ushort vk) => vk switch
        {
            Vk.LShift or Vk.RShift => 1,
            Vk.LControl or Vk.RControl => 2,
            Vk.LMenu or Vk.RMenu => 3,
            Vk.LWin or Vk.RWin => 4,
            _ => 0
        };

        private static bool IsModifierVk(ushort vk) => ModifierFamily(vk) != 0;

        private void ReleaseAllModifiers()
        {
            foreach (var pair in _heldKeyCounts)
                _sender.KeyUp(pair.Key, IsExtendedKey(pair.Key));
            _heldKeyCounts.Clear();
            _nextKeyRepeat.Clear();
            foreach (var vk in _heldModifiers)
                _sender.KeyUp(vk);
            _heldModifiers.Clear();
            _toggledModifiers.Clear();
            _heldModifierCounts.Clear();
            foreach (var state in _comboStates.Values)
            {
                state.Active = false;
                state.ActionHeld = false;
                state.ActionPrevious = false;
                state.Modifiers.Clear();
            }
            _pLUpAxis = _pLDownAxis = _pLLeftAxis = _pLRightAxis = false;
            _pRUpAxis = _pRDownAxis = _pRLeftAxis = _pRRightAxis = false;
            ReleaseHeldClicks();  // no stuck mouse buttons on disable / mode switch
            ReleaseHeldRayKeys(); // no stuck held-typed keys
        }

        private static bool IsExtendedKey(ushort vk) => vk is
            Vk.Delete or Vk.Insert or Vk.Up or Vk.Down or Vk.Left or Vk.Right
            or Vk.PageUp or Vk.PageDown or Vk.Home or Vk.End;

        private static ushort ActionToVk(string action) => action switch
        {
            "HoldShift" or "ToggleShift" => Vk.LShift,
            "HoldCtrl" or "ToggleCtrl" => Vk.LControl,
            "HoldAlt" or "ToggleAlt" => Vk.LMenu,
            "HoldWin" or "ToggleWin" => Vk.LWin,
            _ => Vk.None
        };

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
                "Insert" => Vk.Insert,
                "CapsLock" => Vk.Capital,
                "NumLock" => Vk.NumLock,
                "F1" => Vk.F1, "F2" => Vk.F2, "F3" => Vk.F3, "F4" => Vk.F4,
                "F5" => Vk.F5, "F6" => Vk.F6, "F7" => Vk.F7, "F8" => Vk.F8,
                "F9" => Vk.F9, "F10" => Vk.F10, "F11" => Vk.F11, "F12" => Vk.F12,
                "PageUp" => Vk.PageUp, "PageDown" => Vk.PageDown,
                "Home" => Vk.Home, "End" => Vk.End,
                "Up" or "ArrowUp" => Vk.Up,
                "Down" or "ArrowDown" => Vk.Down,
                "Left" or "ArrowLeft" => Vk.Left,
                "Right" or "ArrowRight" => Vk.Right,
                "VolumeUp" => Vk.VolumeUp, "VolumeDown" => Vk.VolumeDown,
                "VolumeMute" => Vk.VolumeMute, "MediaPlayPause" => Vk.MediaPlayPause,
                "MediaNext" => Vk.MediaNext, "MediaPrev" => Vk.MediaPrev,
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

        private void SwitchStickPointsProfile(int dir)
        {
            var st = AppSettings.Instance;
            int count = Math.Max(1, st.StickPointsProfiles.Count);
            st.ActiveStickPointsProfile = (st.ActiveStickPointsProfile + dir + count) % count;
            ResetKeyboardCursors();
            AppSettings.Save();
            Notification?.Invoke("Stick points profile: " + st.StickPointsProfile.Name);
            StateChanged?.Invoke();
        }

        // ── Ray and cursor geometry ───────────────────────────────────────────

        private void UpdateKeyboardCursors(in GamepadSnapshot s)
        {
            var settings = AppSettings.Instance;
            var points = settings.StickPointsProfile;
            double now = _cursorClock.Elapsed.TotalSeconds;
            double dt = Math.Clamp(now - _lastCursorTime, 0, 0.05);
            _lastCursorTime = now;

            if (!_cursorInitialized || _lastFreeCursor != settings.FreeCursorEnabled)
                ResetKeyboardCursors();
            _lastFreeCursor = settings.FreeCursorEnabled;

            double leftOriginX = 4 + points.LeftX * KeyboardWidth();
            double leftOriginY = 4 + points.LeftY * KeyboardHeight();
            double rightOriginX = 4 + points.RightX * KeyboardWidth();
            double rightOriginY = 4 + points.RightY * KeyboardHeight();

            LastLeftX = s.LX;
            LastLeftY = s.LY;
            LastRightX = s.RX;
            LastRightY = s.RY;

            if (settings.FreeCursorEnabled)
            {
                double speed = Math.Max(0, settings.FreeCursorSpeed);
                ApplyRadialStickCurve(s.LX, s.LY, out double leftX, out double leftY);
                ApplyRadialStickCurve(s.RX, s.RY, out double rightX, out double rightY);
                _leftTargetX = Math.Clamp(_leftTargetX + leftX * speed * dt, 4, 4 + KeyboardWidth());
                _leftTargetY = Math.Clamp(_leftTargetY - leftY * speed * dt, 4, 4 + KeyboardHeight());
                _rightTargetX = Math.Clamp(_rightTargetX + rightX * speed * dt, 4, 4 + KeyboardWidth());
                _rightTargetY = Math.Clamp(_rightTargetY - rightY * speed * dt, 4, 4 + KeyboardHeight());
                LeftCursorActive = RightCursorActive = true;
            }
            else
            {
                double leftMagnitude = Math.Sqrt(s.LX * s.LX + s.LY * s.LY);
                double rightMagnitude = Math.Sqrt(s.RX * s.RX + s.RY * s.RY);
                double maxRay = MaxRayLength();
                ApplyRadialStickCurve(s.LX, s.LY, out double leftX, out double leftY);
                ApplyRadialStickCurve(s.RX, s.RY, out double rightX, out double rightY);
                _leftTargetX = leftOriginX + leftX * maxRay * points.LeftRayLength;
                _leftTargetY = leftOriginY - leftY * maxRay * points.LeftRayLength;
                _rightTargetX = rightOriginX + rightX * maxRay * points.RightRayLength;
                _rightTargetY = rightOriginY - rightY * maxRay * points.RightRayLength;
                LeftCursorActive = settings.CursorLagEnabled || leftMagnitude >= 0.08;
                RightCursorActive = settings.CursorLagEnabled || rightMagnitude >= 0.08;
            }

            LeftRayX = _leftTargetX;
            LeftRayY = _leftTargetY;
            RightRayX = _rightTargetX;
            RightRayY = _rightTargetY;

            if (!settings.CursorLagEnabled || settings.CursorLagSeconds <= 0)
            {
                LeftCursorX = _leftTargetX;
                LeftCursorY = _leftTargetY;
                RightCursorX = _rightTargetX;
                RightCursorY = _rightTargetY;
            }
            else
            {
                double alpha = Math.Clamp(dt / settings.CursorLagSeconds, 0, 1);
                LeftCursorX += (_leftTargetX - LeftCursorX) * alpha;
                LeftCursorY += (_leftTargetY - LeftCursorY) * alpha;
                RightCursorX += (_rightTargetX - RightCursorX) * alpha;
                RightCursorY += (_rightTargetY - RightCursorY) * alpha;
            }

            LeftLen = Distance(leftOriginX, leftOriginY, LeftCursorX, LeftCursorY);
            RightLen = Distance(rightOriginX, rightOriginY, RightCursorX, RightCursorY);
            LeftHit = LeftCursorActive ? HitAt(LeftCursorX, LeftCursorY) : null;
            RightHit = RightCursorActive ? HitAt(RightCursorX, RightCursorY) : null;
        }

        private KeyboardLayout.KeyDef? HitAt(double x, double y)
        {
            foreach (var k in _layout.Keys)
            {
                if (KeyboardLayout.KeyRect(k, AppSettings.Instance.KeySpacing).Contains(x - 4, y - 4))
                    return k;
            }
            return null;
        }

        private static double Distance(double x1, double y1, double x2, double y2)
        {
            double dx = x2 - x1;
            double dy = y2 - y1;
            return Math.Sqrt(dx * dx + dy * dy);
        }

        private double Pitch() => 48 + AppSettings.Instance.KeySpacing;
        private double KeyboardWidth() => _layout.GridW * Pitch();
        private double KeyboardHeight() => _layout.GridH * Pitch();

        /// <summary>Max ray length = key-center distance LeftCtrl -> Backspace in pixels.</summary>
        private double MaxRayLength()
        {
            var a = _layout.FindByVk(Vk.LControl);
            var b = _layout.FindByVk(Vk.Back);
            if (a == null || b == null) return _layout.GridH * Pitch();
            double dx = b.X - a.X, dy = b.Y - a.Y;
            return Math.Sqrt(dx * dx + dy * dy) * Pitch();
        }

        private static double ApplyStickCurve(double value)
        {
            double exponent = AppSettings.Instance.AnalogStickCurveExponent;
            return Math.Sign(value) * Math.Pow(Math.Abs(value), exponent);
        }

        private static void ApplyRadialStickCurve(double x, double y, out double curvedX, out double curvedY)
        {
            double magnitude = Math.Min(1.0, Math.Sqrt(x * x + y * y));
            if (magnitude <= double.Epsilon)
            {
                curvedX = curvedY = 0;
                return;
            }

            double curvedMagnitude = Math.Pow(magnitude, AppSettings.Instance.AnalogStickCurveExponent);
            double scale = curvedMagnitude / magnitude;
            curvedX = x * scale;
            curvedY = y * scale;
        }

        // ── edge bookkeeping ──────────────────────────────────────────────────

        private void SaveEdges(in GamepadSnapshot s)
        {
            _pA = s.A; _pB = s.B; _pX = s.X; _pY = s.Y;
            _pLB = s.LB; _pRB = s.RB; _pLS = s.LS; _pRS = s.RS;
            _pDUp = s.DUp; _pDDown = s.DDown; _pDLeft = s.DLeft; _pDRight = s.DRight;
            _pView = s.View; _pMenu = s.Menu;
            _pLTHeld = s.LeftTrigger > 0.5; _pRTHeld = s.RightTrigger > 0.5;
        }

        private void ClearAllEdges(in GamepadSnapshot s)
        {
            _pA = s.A; _pB = s.B; _pX = s.X; _pY = s.Y;
            _pLB = s.LB; _pRB = s.RB; _pLS = s.LS; _pRS = s.RS;
            _pDUp = s.DUp; _pDDown = s.DDown; _pDLeft = s.DLeft; _pDRight = s.DRight;
            _pView = s.View; _pMenu = s.Menu;
            _pLTHeld = s.LeftTrigger > 0.5; _pRTHeld = s.RightTrigger > 0.5;
        }

        private sealed class ComboRuntimeState
        {
            public bool LastButtonHeld;
            public bool Active;
            public bool ActionHeld;
            public bool ActionPrevious;
            public string Action = "None";
            public List<ushort> Modifiers { get; } = new();
        }

        // edge fields used only in some modes
        private bool _pView, _pMenu, _pLTHeld, _pRTHeld;
    }
}
