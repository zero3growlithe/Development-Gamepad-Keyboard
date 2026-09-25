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

        // custom combo bindings ("A+B+X=Action"): per-combo last-button edge tracking
        private readonly Dictionary<string, bool> _comboPrev = new();
        private readonly HashSet<string> _comboConsumedButtons = new();
        private readonly ComboBindingCache _keyboardComboCache = new();
        private readonly ComboBindingCache _mouseComboCache = new();
        private readonly List<string> _staleComboKeys = new();
        private readonly List<string> _releasedComboButtons = new();

        // previous physical state (edge detection)
        private bool _pA, _pB, _pX, _pY, _pLB, _pRB, _pLS, _pRS;
        private bool _pDUp, _pDDown, _pDLeft, _pDRight;
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
                var enableCache = MouseMode ? _mouseComboCache : _keyboardComboCache;
                if (mappedButtonEnabled || TryEnableFromProfile(s, enableBindings, enableCache))
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

            SaveEdges(s);
        }

        // ── Keyboard mode ─────────────────────────────────────────────────────

        private void ProcessKeyboardMode(in GamepadSnapshot s)
        {
            var p = AppSettings.Instance.Profile;
            var comboParticipants = EvaluateCombos(s, p.ComboBindings, _keyboardComboCache);
            if (!InputEnabled || MouseMode) { FinishComboFrame(s); return; }

            MoveDX = MoveDY = ScaleDelta = 0;
            if (AdjustMoveScaleKeyboard)
            {
                // Default: left stick scales, right stick moves. The global swap
                // option reverses these roles here and in mouse mode.
                bool swap = AppSettings.Instance.SwapAnalogSticks;
                double moveX = swap ? s.LX : s.RX;
                double moveY = swap ? s.LY : s.RY;
                ApplyRadialStickCurve(moveX, moveY, out double curvedMoveX, out double curvedMoveY);
                MoveDX = curvedMoveX;
                MoveDY = curvedMoveY;
                ScaleDelta = ApplyStickCurve(swap ? s.RY : s.LY);
            }
            else
            {
                UpdateKeyboardCursors(s);
            }

            DispatchProfileButton("LT", p.LT, s.LeftTrigger > 0.5, ref _pLTHeld, comboParticipants);
            DispatchProfileButton("RT", p.RT, s.RightTrigger > 0.5, ref _pRTHeld, comboParticipants);

            // Combo participants defer standalone actions to release; a fired combo
            // consumes those releases. Continuous modifier holds remain immediate.
            DispatchProfileButton("A", p.A, s.A, ref _pA, comboParticipants);
            DispatchProfileButton("B", p.B, s.B, ref _pB, comboParticipants);
            if (!InputEnabled || MouseMode) { FinishComboFrame(s); return; }
            DispatchProfileButton("X", p.X, s.X, ref _pX, comboParticipants);
            DispatchProfileButton("Y", p.Y, s.Y, ref _pY, comboParticipants);

            DispatchProfileButton("LB", p.LB, s.LB, ref _pLB, comboParticipants);
            DispatchProfileButton("RB", p.RB, s.RB, ref _pRB, comboParticipants);
            DispatchProfileButton("LS", p.LS, s.LS, ref _pLS, comboParticipants);
            DispatchProfileButton("RS", p.RS, s.RS, ref _pRS, comboParticipants);

            DispatchProfileButton("View", p.View, s.View, ref _pView, comboParticipants);
            DispatchProfileButton("Menu", p.Menu, s.Menu, ref _pMenu, comboParticipants);

            // dpad layer: Y-held layer or plain layer
            if (s.Y)
            {
                DispatchProfileButton("DUp", p.YDUp, s.DUp, ref _pDUp, comboParticipants);
                DispatchProfileButton("DDown", p.YDDown, s.DDown, ref _pDDown, comboParticipants);
                DispatchProfileButton("DLeft", p.YDLeft, s.DLeft, ref _pDLeft, comboParticipants);
                DispatchProfileButton("DRight", p.YDRight, s.DRight, ref _pDRight, comboParticipants);
            }
            else
            {
                DispatchProfileButton("DUp", p.DUp, s.DUp, ref _pDUp, comboParticipants);
                DispatchProfileButton("DDown", p.DDown, s.DDown, ref _pDDown, comboParticipants);
                DispatchProfileButton("DLeft", p.DLeft, s.DLeft, ref _pDLeft, comboParticipants);
                DispatchProfileButton("DRight", p.DRight, s.DRight, ref _pDRight, comboParticipants);
            }
            FinishComboFrame(s);
        }

        // ── Mouse mode ────────────────────────────────────────────────────────

        private void ProcessMouse(in GamepadSnapshot s)
        {
            var profile = AppSettings.Instance.MouseProfile;
            var st = AppSettings.Instance;

            bool boost = IsActionHeld(profile, "SpeedBoost", s);
            double speed = st.MouseSpeed * (boost ? st.MouseSpeedBoostMultiplier : 1.0);

            // Default: right stick moves the cursor, left stick scrolls. The
            // global swap option reverses the complete stick roles.
            bool swap = st.SwapAnalogSticks;
            double cursorX = swap ? s.LX : s.RX;
            double cursorY = swap ? s.LY : s.RY;
            double scrollX = swap ? s.RX : s.LX;
            double scrollY = swap ? s.RY : s.LY;
            ApplyRadialStickCurve(cursorX, cursorY, out double curvedCursorX, out double curvedCursorY);
            ApplyRadialStickCurve(scrollX, scrollY, out double curvedScrollX, out double curvedScrollY);
            _sender.MouseMove(
                (int)Math.Round(curvedCursorX * speed),
                (int)Math.Round(-curvedCursorY * speed));

            double sc = st.ScrollSpeed;
            if (Math.Abs(scrollY) > 0.05)
                SendVerticalScroll(-(int)Math.Round(curvedScrollY * 120 * sc / 3.0));
            if (Math.Abs(scrollX) > 0.05)
                SendHorizontalScroll((int)Math.Round(curvedScrollX * 120 * sc / 3.0));

            var comboParticipants = EvaluateCombos(s, profile.ComboBindings, _mouseComboCache);
            if (!InputEnabled || !MouseMode) { FinishComboFrame(s); return; }

            // dpad scroll (unless remapped to something else)
            if (comboParticipants.Contains("DUp"))
                DispatchProfileButton("DUp", profile.DUp, s.DUp, ref _pDUp, comboParticipants);
            else
            {
                if (profile.DUp == "ScrollUp") { if (s.DUp) SendVerticalScroll(120); }
                else DispatchButton(profile.DUp, s.DUp, ref _pDUp);
            }
            if (comboParticipants.Contains("DDown"))
                DispatchProfileButton("DDown", profile.DDown, s.DDown, ref _pDDown, comboParticipants);
            else
            {
                if (profile.DDown == "ScrollDown") { if (s.DDown) SendVerticalScroll(-120); }
                else DispatchButton(profile.DDown, s.DDown, ref _pDDown);
            }
            if (comboParticipants.Contains("DLeft"))
                DispatchProfileButton("DLeft", profile.DLeft, s.DLeft, ref _pDLeft, comboParticipants);
            else
            {
                if (profile.DLeft == "ScrollLeft") { if (s.DLeft) SendHorizontalScroll(-120); }
                else DispatchButton(profile.DLeft, s.DLeft, ref _pDLeft);
            }
            if (comboParticipants.Contains("DRight"))
                DispatchProfileButton("DRight", profile.DRight, s.DRight, ref _pDRight, comboParticipants);
            else
            {
                if (profile.DRight == "ScrollRight") { if (s.DRight) SendHorizontalScroll(120); }
                else DispatchButton(profile.DRight, s.DRight, ref _pDRight);
            }

            // buttons (combo participants use release-only standalone actions)
            DispatchProfileButton("A", profile.A, s.A, ref _pA, comboParticipants);
            DispatchProfileButton("B", profile.B, s.B, ref _pB, comboParticipants);
            DispatchProfileButton("X", profile.X, s.X, ref _pX, comboParticipants);
            DispatchProfileButton("Y", profile.Y, s.Y, ref _pY, comboParticipants);
            if (!InputEnabled || !MouseMode) { FinishComboFrame(s); return; }
            DispatchProfileButton("LB", profile.LB, s.LB, ref _pLB, comboParticipants);
            DispatchProfileButton("RB", profile.RB, s.RB, ref _pRB, comboParticipants);
            DispatchProfileButton("LS", profile.LS, s.LS, ref _pLS, comboParticipants);
            DispatchProfileButton("RS", profile.RS, s.RS, ref _pRS, comboParticipants);
            DispatchProfileButton("View", profile.View, s.View, ref _pView, comboParticipants);
            DispatchProfileButton("Menu", profile.Menu, s.Menu, ref _pMenu, comboParticipants);
            DispatchProfileButton("LT", profile.LT, s.LeftTrigger > 0.5, ref _pLTHeld, comboParticipants);
            DispatchProfileButton("RT", profile.RT, s.RightTrigger > 0.5, ref _pRTHeld, comboParticipants);
            FinishComboFrame(s);
        }

        private static bool IsActionHeld(MouseProfile profile, string action, in GamepadSnapshot s)
        {
            return profile.A == action && s.A
                || profile.B == action && s.B
                || profile.X == action && s.X
                || profile.Y == action && s.Y
                || profile.LB == action && s.LB
                || profile.RB == action && s.RB
                || profile.LT == action && s.LeftTrigger > 0.5
                || profile.RT == action && s.RightTrigger > 0.5
                || profile.LS == action && s.LS
                || profile.RS == action && s.RS
                || profile.View == action && s.View
                || profile.Menu == action && s.Menu
                || profile.DUp == action && s.DUp
                || profile.DDown == action && s.DDown
                || profile.DLeft == action && s.DLeft
                || profile.DRight == action && s.DRight;
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
            TryEnableAction(profile.A, s.A, _pA)
            || TryEnableAction(profile.B, s.B, _pB)
            || TryEnableAction(profile.X, s.X, _pX)
            || TryEnableAction(profile.Y, s.Y, _pY)
            || TryEnableAction(profile.LB, s.LB, _pLB)
            || TryEnableAction(profile.RB, s.RB, _pRB)
            || TryEnableAction(profile.LT, s.LeftTrigger > 0.5, _pLTHeld)
            || TryEnableAction(profile.RT, s.RightTrigger > 0.5, _pRTHeld)
            || TryEnableAction(profile.LS, s.LS, _pLS)
            || TryEnableAction(profile.RS, s.RS, _pRS)
            || TryEnableAction(profile.View, s.View, _pView)
            || TryEnableAction(profile.Menu, s.Menu, _pMenu)
            || TryEnableAction(profile.DUp, s.DUp, _pDUp)
            || TryEnableAction(profile.DDown, s.DDown, _pDDown)
            || TryEnableAction(profile.DLeft, s.DLeft, _pDLeft)
            || TryEnableAction(profile.DRight, s.DRight, _pDRight);

        private bool TryEnableFromMappedButton(in GamepadSnapshot s, MouseProfile profile) =>
            TryEnableAction(profile.A, s.A, _pA)
            || TryEnableAction(profile.B, s.B, _pB)
            || TryEnableAction(profile.X, s.X, _pX)
            || TryEnableAction(profile.Y, s.Y, _pY)
            || TryEnableAction(profile.LB, s.LB, _pLB)
            || TryEnableAction(profile.RB, s.RB, _pRB)
            || TryEnableAction(profile.LT, s.LeftTrigger > 0.5, _pLTHeld)
            || TryEnableAction(profile.RT, s.RightTrigger > 0.5, _pRTHeld)
            || TryEnableAction(profile.LS, s.LS, _pLS)
            || TryEnableAction(profile.RS, s.RS, _pRS)
            || TryEnableAction(profile.View, s.View, _pView)
            || TryEnableAction(profile.Menu, s.Menu, _pMenu)
            || TryEnableAction(profile.DUp, s.DUp, _pDUp)
            || TryEnableAction(profile.DDown, s.DDown, _pDDown)
            || TryEnableAction(profile.DLeft, s.DLeft, _pDLeft)
            || TryEnableAction(profile.DRight, s.DRight, _pDRight);

        private bool TryEnableAction(string action, bool held, bool previous)
        {
            if (action != "EnableInput" || !held || previous) return false;
            SetInputEnabled(true);
            return true;
        }

        private bool TryEnableFromProfile(
            in GamepadSnapshot s,
            List<string> combos,
            ComboBindingCache cache)
        {
            foreach (var binding in cache.Get(combos))
            {
                if (!string.Equals(binding.Action, "EnableInput", StringComparison.Ordinal))
                    continue;

                var parts = binding.Parts;
                if (parts.Length < 2) continue;

                bool modifiersHeld = true;
                for (int i = 0; i < parts.Length - 1; i++)
                {
                    if (!s.Button(parts[i]))
                    {
                        modifiersHeld = false;
                        break;
                    }
                }

                string last = parts[^1];
                bool held = modifiersHeld && s.Button(last);
                bool previous = _comboPrev.TryGetValue(binding.Raw, out var wasHeld) && wasHeld;
                _comboPrev[binding.Raw] = held;
                if (held && !previous)
                {
                    foreach (var part in parts)
                        _comboConsumedButtons.Add(part);
                    App.Log("input enabled by profile binding: " + binding.Key);
                    SetInputEnabled(true);
                    return true;
                }
            }
            return false;
        }

        /// <summary>
        /// Evaluates the profile's custom combos. Returns every physical button that
        /// participates in a combo so its standalone action can be deferred to release.
        /// "A+B+X=Act": A+B held = modifiers; X edge = trigger.
        /// </summary>
        private HashSet<string> EvaluateCombos(
            in GamepadSnapshot s,
            List<string> combos,
            ComboBindingCache cache)
        {
            var bindings = cache.Get(combos);
            var participants = cache.Participants;
            if (bindings.Count == 0)
            {
                _comboPrev.Clear();
                _comboConsumedButtons.Clear();
                return participants;
            }

            foreach (var binding in bindings)
            {
                var parts = binding.Parts;
                bool allHeld = true;
                for (int i = 0; i < parts.Length - 1; i++)
                    if (!s.Button(parts[i])) { allHeld = false; break; }
                if (!allHeld) { _comboPrev[binding.Raw] = false; continue; }

                string last = parts[^1];
                bool held = s.Button(last);
                bool prev = _comboPrev.TryGetValue(binding.Raw, out var p) && p;
                if (held && !prev)
                {
                    foreach (var part in parts)
                        _comboConsumedButtons.Add(part);
                    App.Log("combo binding: " + binding.Key + " -> " + binding.Action);
                    RunActionOnce(binding.Action);
                }
                _comboPrev[binding.Raw] = held;
            }

            // Drop stale entries for removed or mode-specific combos without
            // allocating a new work list on every input sample.
            _staleComboKeys.Clear();
            foreach (var k in _comboPrev.Keys)
                if (!cache.RawKeys.Contains(k)) _staleComboKeys.Add(k);
            foreach (var k in _staleComboKeys) _comboPrev.Remove(k);
            return participants;
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
            string action,
            bool held,
            ref bool previous,
            HashSet<string> comboParticipants)
        {
            if (!comboParticipants.Contains(button) || IsContinuousModifier(action))
            {
                DispatchButton(action, held, ref previous);
                return;
            }

            if (!held && previous && !_comboConsumedButtons.Contains(button))
                RunActionOnce(action);

            // Deferred buttons still need their physical edge recorded; otherwise
            // a release can be missed when this method is used outside Process().
            previous = held;
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

        private static bool IsContinuousModifier(string action) => action is
            "HoldShift" or "HoldCtrl" or "HoldAlt" or "HoldWin";

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
            bool edge = held && !prev;
            switch (action)
            {
                case "HoldShift":
                case "HoldCtrl":
                case "HoldAlt":
                case "HoldWin":
                    ApplyHeld(action, held, prev);
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
                    UpdateHeldRayKey(ref _heldRayKeyL, ref _leftModifierSubmit, held, prev, LeftHit);
                    return;
                case "SubmitRight":
                case "CommitRight": // legacy saved profiles
                    UpdateHeldRayKey(ref _heldRayKeyR, ref _rightModifierSubmit, held, prev, RightHit);
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
                    App.Log("input disabled by profile action");
                    SetInputEnabled(false);
                    break;

                case "EnableInput":
                    SetInputEnabled(true);
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
                    bool swapped = AppSettings.Instance.SwapAnalogSticks;
                    Notification?.Invoke(AdjustMoveScaleKeyboard
                        ? "Keyboard move/scale ON — " +
                          (swapped ? "left stick moves, right stick scales" : "right stick moves, left stick scales")
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

                // mouse actions (also valid in keyboard-mode mappings if wanted)
                case "ScrollUp": SendVerticalScroll(120); break;
                case "ScrollDown": SendVerticalScroll(-120); break;
                case "ScrollLeft": SendHorizontalScroll(-120); break;
                case "ScrollRight": SendHorizontalScroll(120); break;

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
            foreach (var vk in _heldModifiers)
                _sender.KeyUp(vk);
            _heldModifiers.Clear();
            _toggledModifiers.Clear();
            _heldModifierCounts.Clear();
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

        private sealed class ComboBindingCache
        {
            private readonly List<string> _source = new();
            private readonly List<ParsedComboBinding> _bindings = new();

            public HashSet<string> Participants { get; } = new(StringComparer.Ordinal);
            public HashSet<string> RawKeys { get; } = new(StringComparer.Ordinal);

            public IReadOnlyList<ParsedComboBinding> Get(List<string> source)
            {
                bool unchanged = _source.Count == source.Count;
                if (unchanged)
                {
                    for (int i = 0; i < source.Count; i++)
                    {
                        if (string.Equals(_source[i], source[i], StringComparison.Ordinal)) continue;
                        unchanged = false;
                        break;
                    }
                }
                if (unchanged) return _bindings;

                _source.Clear();
                _source.AddRange(source);
                _bindings.Clear();
                Participants.Clear();
                RawKeys.Clear();

                foreach (string raw in source)
                {
                    int equals = raw.IndexOf('=');
                    if (equals <= 0) continue;
                    string key = raw[..equals].Trim();
                    string action = raw[(equals + 1)..].Trim();
                    if (key.Length == 0 || action.Length == 0) continue;

                    string[] parts = key.Split('+', StringSplitOptions.TrimEntries);
                    _bindings.Add(new ParsedComboBinding(raw, key, action, parts));
                    RawKeys.Add(raw);
                    foreach (string part in parts)
                        Participants.Add(part);
                }

                return _bindings;
            }
        }

        private sealed class ParsedComboBinding
        {
            public string Raw { get; }
            public string Key { get; }
            public string Action { get; }
            public string[] Parts { get; }

            public ParsedComboBinding(string raw, string key, string action, string[] parts)
            {
                Raw = raw;
                Key = key;
                Action = action;
                Parts = parts;
            }
        }

        // edge fields used only in some modes
        private bool _pView, _pMenu, _pLTHeld, _pRTHeld;
    }
}
