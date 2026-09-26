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
        private readonly List<string> _staleComboKeys = new();
        private readonly List<string> _releasedComboButtons = new();
        private readonly Dictionary<ushort, int> _heldKeyCounts = new();
        private readonly Dictionary<ushort, double> _nextKeyRepeat = new();

        private readonly Dictionary<string, BindingRuntimeState> _bindingStates = new(StringComparer.Ordinal);
        private readonly List<string> _staleBindingKeys = new();
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
                var bindings = MouseMode
                    ? AppSettings.Instance.MouseProfile.Bindings
                    : AppSettings.Instance.Profile.Bindings;
                if (TryEnableFromProfile(s, bindings))
                {
                    CaptureBindingEdges(s, bindings);
                    return;
                }

                // pass-through: app injects nothing, game sees the pad natively
                AdjustMoveScaleKeyboard = false;
                MoveDX = MoveDY = ScaleDelta = 0;
                CaptureBindingEdges(s, bindings);
                CleanupRuntimeBindings(bindings);
                FinishComboFrame(s);
                return;
            }

            if (MouseMode)
                ProcessMouse(s);
            else
                ProcessKeyboardMode(s);

            RepeatHeldKeys();
            CleanupRuntimeBindings(MouseMode
                ? AppSettings.Instance.MouseProfile.Bindings
                : AppSettings.Instance.Profile.Bindings);
        }

        // ── Keyboard mode ─────────────────────────────────────────────────────

        private void ProcessKeyboardMode(in GamepadSnapshot s)
        {
            var p = AppSettings.Instance.Profile;
            EvaluateCombos(s, p.Bindings);
            if (!InputEnabled || MouseMode) { FinishComboFrame(s); return; }

            MoveDX = MoveDY = ScaleDelta = 0;
            double leftX = 0, leftY = 0, rightX = 0, rightY = 0;
            AccumulateContinuousActions(p.Bindings, s,
                ref leftX, ref leftY, ref rightX, ref rightY,
                out _, out _, out _, out _);
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
                UpdateKeyboardCursors(s, leftX, leftY, rightX, rightY);
            }

            DispatchSingleBindings(p.Bindings, s, keyboardMode: true);
            FinishComboFrame(s);
        }

        // ── Mouse mode ────────────────────────────────────────────────────────

        private void ProcessMouse(in GamepadSnapshot s)
        {
            var profile = AppSettings.Instance.MouseProfile;
            var st = AppSettings.Instance;

            bool boost = IsActionHeld(profile.Bindings, "SpeedBoost", s);
            double speed = st.MouseSpeed * (boost ? st.MouseSpeedBoostMultiplier : 1.0);
            double leftX = 0, leftY = 0, rightX = 0, rightY = 0;
            AccumulateContinuousActions(profile.Bindings, s,
                ref leftX, ref leftY, ref rightX, ref rightY,
                out double moveX, out double moveY, out double scrollX, out double scrollY);

            int dx = (int)Math.Round(moveX * speed);
            int dy = (int)Math.Round(moveY * speed);
            if (dx != 0 || dy != 0) _sender.MouseMove(dx, dy);
            int vertical = (int)Math.Round(scrollY * 120 * st.ScrollSpeed / 3.0);
            int horizontal = (int)Math.Round(scrollX * 120 * st.ScrollSpeed / 3.0);
            if (vertical != 0) SendVerticalScroll(vertical);
            if (horizontal != 0) SendHorizontalScroll(horizontal);

            EvaluateCombos(s, profile.Bindings);
            if (!InputEnabled || !MouseMode) { FinishComboFrame(s); return; }
            DispatchSingleBindings(profile.Bindings, s, keyboardMode: false);
            FinishComboFrame(s);
        }

        private static bool IsActionHeld(List<ProfileBinding> bindings, string action, in GamepadSnapshot s)
        {
            foreach (var binding in bindings)
            {
                if (binding.Buttons.Count == 1 && !binding.Modifier
                    && binding.Action == action && InputValue(s, binding.Buttons[0]) >= 0.5)
                    return true;
            }
            return false;
        }

        private void AccumulateContinuousActions(
            List<ProfileBinding> bindings,
            in GamepadSnapshot s,
            ref double leftX,
            ref double leftY,
            ref double rightX,
            ref double rightY,
            out double moveX,
            out double moveY,
            out double scrollX,
            out double scrollY)
        {
            moveX = moveY = scrollX = scrollY = 0;
            foreach (var binding in bindings)
            {
                if (binding.Buttons.Count != 1) continue;
                double value = InputValue(s, binding.Buttons[0]);
                if (value <= 0) continue;
                switch (binding.Action)
                {
                    case "MoveLeftCursorUp": leftY += value; break;
                    case "MoveLeftCursorDown": leftY -= value; break;
                    case "MoveLeftCursorLeft": leftX -= value; break;
                    case "MoveLeftCursorRight": leftX += value; break;
                    case "MoveRightCursorUp": rightY += value; break;
                    case "MoveRightCursorDown": rightY -= value; break;
                    case "MoveRightCursorLeft": rightX -= value; break;
                    case "MoveRightCursorRight": rightX += value; break;
                    case "MouseMoveUp": moveY -= value; break;
                    case "MouseMoveDown": moveY += value; break;
                    case "MouseMoveLeft": moveX -= value; break;
                    case "MouseMoveRight": moveX += value; break;
                    case "AnalogScrollUp": scrollY += value; break;
                    case "AnalogScrollDown": scrollY -= value; break;
                    case "AnalogScrollLeft": scrollX -= value; break;
                    case "AnalogScrollRight": scrollX += value; break;
                }
            }
            NormalizeVector(ref leftX, ref leftY);
            NormalizeVector(ref rightX, ref rightY);
            NormalizeVector(ref moveX, ref moveY);
        }

        private static void NormalizeVector(ref double x, ref double y)
        {
            double magnitude = Math.Sqrt(x * x + y * y);
            if (magnitude <= 1) return;
            x /= magnitude;
            y /= magnitude;
        }

        private void SendVerticalScroll(int delta) => _sender.MouseWheel(delta);

        private void SendHorizontalScroll(int delta) => _sender.MouseHWheel(delta);

        // ── Ordered profile bindings ──────────────────────────────────────────

        private bool TryEnableFromProfile(
            in GamepadSnapshot s,
            List<ProfileBinding> bindings)
        {
            foreach (var binding in bindings)
            {
                if (binding.Action != "EnableInput" && binding.Action != "ToggleInput")
                    continue;

                var parts = binding.Buttons;
                if (parts.Count == 1)
                {
                    string button = parts[0];
                    bool held = InputValue(s, button) >= 0.5;
                    var singleState = GetBindingState(binding.Id);
                    bool previous = singleState.Previous;
                    if (binding.Modifier)
                    {
                        if (held && AnyOtherPhysicalButtonHeld(button, s))
                            singleState.UsedAsModifier = true;
                        bool used = singleState.UsedAsModifier;
                        if (!held) singleState.UsedAsModifier = false;
                        if (held || !previous || used
                            || AnyOtherPhysicalButtonHeld(button, s)) continue;
                    }
                    else if (!held || previous)
                    {
                        continue;
                    }
                    SetInputEnabled(true);
                    return true;
                }
                if (parts.Count < 2) continue;
                var state = GetComboState(binding.Id);

                bool modifiersHeld = true;
                for (int i = 0; i < parts.Count - 1; i++)
                {
                    if (InputValue(s, parts[i]) < 0.5)
                    {
                        modifiersHeld = false;
                        break;
                    }
                }

                string last = parts[^1];
                bool lastHeld = InputValue(s, last) >= 0.5;
                bool held = modifiersHeld && lastHeld;
                bool previous = state.LastButtonHeld;
                state.LastButtonHeld = lastHeld;
                if (held && !previous)
                {
                    foreach (var part in parts)
                    {
                        _comboConsumedButtons.Add(part);
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
            List<ProfileBinding> bindings)
        {
            var currentIds = new HashSet<string>(StringComparer.Ordinal);
            foreach (var binding in bindings)
            {
                var parts = binding.Buttons;
                if (parts.Count < 2) continue;
                currentIds.Add(binding.Id);
                var state = GetComboState(binding.Id);

                bool prefixHeld = true;
                for (int i = 0; i < parts.Count - 1; i++)
                    if (InputValue(s, parts[i]) < 0.5) { prefixHeld = false; break; }

                string last = parts[^1];
                bool lastHeld = InputValue(s, last) >= 0.5;

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

        private void AcquireComboModifiers(ProfileBinding binding, ComboRuntimeState state)
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

        private void DispatchSingleBindings(
            List<ProfileBinding> bindings,
            in GamepadSnapshot snapshot,
            bool keyboardMode)
        {
            foreach (var binding in bindings)
            {
                if (binding.Buttons.Count != 1) continue;
                var state = GetBindingState(binding.Id);
                bool continuous = IsContinuousAction(binding.Action);
                if (!string.Equals(state.Action, binding.Action, StringComparison.Ordinal)
                    || state.Modifier != binding.Modifier
                    || state.Continuous != continuous)
                {
                    ReleaseBindingState(state);
                    state.Action = binding.Action;
                }

                if (continuous)
                {
                    state.Previous = false;
                    state.Modifier = false;
                    state.Continuous = true;
                    continue;
                }

                state.Continuous = false;
                bool held = InputValue(snapshot, binding.Buttons[0]) >= 0.5;
                DispatchProfileBinding(binding, held, state, snapshot);
                bool modeChanged = keyboardMode ? MouseMode : !MouseMode;
                if (!InputEnabled || modeChanged) break;
            }
        }

        private void DispatchProfileBinding(
            ProfileBinding binding,
            bool held,
            BindingRuntimeState state,
            in GamepadSnapshot snapshot)
        {
            string button = binding.Buttons[0];
            bool previous = state.Previous;
            state.Modifier = binding.Modifier;
            state.Action = binding.Action;
            if (!binding.Modifier)
            {
                DispatchButton(binding.Action, held, ref state.Previous);
                return;
            }

            if (held && !previous)
                state.UsedAsModifier = false;
            if (held && AnyOtherPhysicalButtonHeld(button, snapshot))
                state.UsedAsModifier = true;
            if (!held && previous)
            {
                bool used = state.UsedAsModifier
                    || _comboConsumedButtons.Contains(button)
                    || AnyOtherPhysicalButtonHeld(button, snapshot);
                state.UsedAsModifier = false;
                if (!used) RunActionOnce(binding.Action);
            }
            state.Previous = held;
        }

        private static readonly string[] PhysicalButtons =
        {
            "A", "B", "X", "Y", "LB", "RB", "LT", "RT", "LS", "RS",
            "View", "Menu", "Home", "DUp", "DDown", "DLeft", "DRight",
            "LUp", "LDown", "LLeft", "LRight", "RUp", "RDown", "RLeft", "RRight"
        };

        private static bool AnyOtherPhysicalButtonHeld(string button, in GamepadSnapshot snapshot)
        {
            foreach (string candidate in PhysicalButtons)
                if (candidate != button && InputValue(snapshot, candidate) >= 0.5) return true;
            return false;
        }

        private static double InputValue(in GamepadSnapshot s, string button)
        {
            return button switch
            {
                "A" => s.A ? 1 : 0, "B" => s.B ? 1 : 0,
                "X" => s.X ? 1 : 0, "Y" => s.Y ? 1 : 0,
                "LB" => s.LB ? 1 : 0, "RB" => s.RB ? 1 : 0,
                "LT" => s.LeftTrigger, "RT" => s.RightTrigger,
                "LS" => s.LS ? 1 : 0, "RS" => s.RS ? 1 : 0,
                "View" => s.View ? 1 : 0, "Menu" => s.Menu ? 1 : 0,
                "Home" => s.Home ? 1 : 0,
                "DUp" => s.DUp ? 1 : 0, "DDown" => s.DDown ? 1 : 0,
                "DLeft" => s.DLeft ? 1 : 0, "DRight" => s.DRight ? 1 : 0,
                "LUp" => Math.Max(0, ApplyStickCurve(s.LY)),
                "LDown" => Math.Max(0, -ApplyStickCurve(s.LY)),
                "LLeft" => Math.Max(0, -ApplyStickCurve(s.LX)),
                "LRight" => Math.Max(0, ApplyStickCurve(s.LX)),
                "RUp" => Math.Max(0, ApplyStickCurve(s.RY)),
                "RDown" => Math.Max(0, -ApplyStickCurve(s.RY)),
                "RLeft" => Math.Max(0, -ApplyStickCurve(s.RX)),
                "RRight" => Math.Max(0, ApplyStickCurve(s.RX)),
                _ => 0
            };
        }

        private static bool IsContinuousAction(string action) => action is
            "MoveLeftCursorUp" or "MoveLeftCursorDown" or "MoveLeftCursorLeft" or "MoveLeftCursorRight"
            or "MoveRightCursorUp" or "MoveRightCursorDown" or "MoveRightCursorLeft" or "MoveRightCursorRight"
            or "MouseMoveUp" or "MouseMoveDown" or "MouseMoveLeft" or "MouseMoveRight"
            or "AnalogScrollUp" or "AnalogScrollDown" or "AnalogScrollLeft" or "AnalogScrollRight";

        private BindingRuntimeState GetBindingState(string id)
        {
            if (_bindingStates.TryGetValue(id, out var state)) return state;
            state = new BindingRuntimeState();
            _bindingStates[id] = state;
            return state;
        }

        private void ReleaseBindingState(BindingRuntimeState state)
        {
            if (state.Previous && !state.Modifier && !state.Continuous)
                DispatchButton(state.Action, false, ref state.Previous);
            state.Previous = false;
            state.Modifier = false;
            state.Continuous = false;
            state.UsedAsModifier = false;
        }

        private void CaptureBindingEdges(in GamepadSnapshot snapshot, List<ProfileBinding> bindings)
        {
            foreach (var binding in bindings)
            {
                if (binding.Buttons.Count == 1)
                {
                    var state = GetBindingState(binding.Id);
                    state.Action = binding.Action;
                    state.Modifier = binding.Modifier;
                    state.Continuous = IsContinuousAction(binding.Action);
                    state.Previous = InputValue(snapshot, binding.Buttons[0]) >= 0.5;
                    if (!state.Previous) state.UsedAsModifier = false;
                    else if (binding.Modifier && AnyOtherPhysicalButtonHeld(binding.Buttons[0], snapshot))
                        state.UsedAsModifier = true;
                }
                else if (binding.Buttons.Count > 1)
                {
                    GetComboState(binding.Id).LastButtonHeld =
                        InputValue(snapshot, binding.Buttons[^1]) >= 0.5;
                }
            }
        }

        private void CleanupRuntimeBindings(List<ProfileBinding> bindings)
        {
            var activeIds = new HashSet<string>(StringComparer.Ordinal);
            foreach (var binding in bindings)
                if (binding.Buttons.Count == 1) activeIds.Add(binding.Id);
            _staleBindingKeys.Clear();
            foreach (string id in _bindingStates.Keys)
                if (!activeIds.Contains(id)) _staleBindingKeys.Add(id);
            foreach (string id in _staleBindingKeys)
            {
                ReleaseBindingState(_bindingStates[id]);
                _bindingStates.Remove(id);
            }
        }

        private void FinishComboFrame(in GamepadSnapshot s)
        {
            // An in parameter cannot be captured by RemoveWhere's predicate.
            // Collect released buttons first, then mutate the set separately.
            _releasedComboButtons.Clear();
            foreach (var button in _comboConsumedButtons)
                if (InputValue(s, button) < 0.5) _releasedComboButtons.Add(button);
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
                case "MoveLeftCursorUp":
                case "MoveLeftCursorDown":
                case "MoveLeftCursorLeft":
                case "MoveLeftCursorRight":
                case "MoveRightCursorUp":
                case "MoveRightCursorDown":
                case "MoveRightCursorLeft":
                case "MoveRightCursorRight":
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
            foreach (var state in _bindingStates.Values)
            {
                state.Previous = false;
                state.Modifier = false;
                state.UsedAsModifier = false;
            }
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

        private void UpdateKeyboardCursors(
            in GamepadSnapshot s,
            double mappedLeftX,
            double mappedLeftY,
            double mappedRightX,
            double mappedRightY)
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
                _leftTargetX = Math.Clamp(_leftTargetX + mappedLeftX * speed * dt, 4, 4 + KeyboardWidth());
                _leftTargetY = Math.Clamp(_leftTargetY - mappedLeftY * speed * dt, 4, 4 + KeyboardHeight());
                _rightTargetX = Math.Clamp(_rightTargetX + mappedRightX * speed * dt, 4, 4 + KeyboardWidth());
                _rightTargetY = Math.Clamp(_rightTargetY - mappedRightY * speed * dt, 4, 4 + KeyboardHeight());
                LeftCursorActive = RightCursorActive = true;
            }
            else
            {
                double leftMagnitude = Math.Sqrt(mappedLeftX * mappedLeftX + mappedLeftY * mappedLeftY);
                double rightMagnitude = Math.Sqrt(mappedRightX * mappedRightX + mappedRightY * mappedRightY);
                double maxRay = MaxRayLength();
                _leftTargetX = leftOriginX + mappedLeftX * maxRay * points.LeftRayLength;
                _leftTargetY = leftOriginY - mappedLeftY * maxRay * points.LeftRayLength;
                _rightTargetX = rightOriginX + mappedRightX * maxRay * points.RightRayLength;
                _rightTargetY = rightOriginY - mappedRightY * maxRay * points.RightRayLength;
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

        private sealed class BindingRuntimeState
        {
            public bool Previous;
            public bool Modifier;
            public bool Continuous;
            public bool UsedAsModifier;
            public string Action = "None";
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

    }
}
