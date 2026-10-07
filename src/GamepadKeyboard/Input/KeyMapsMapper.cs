using System;
using System.Collections.Generic;
using System.Linq;
using GamepadKeyboard.Native;
using GamepadKeyboard.Settings;
using GamepadKeyboard.UI;

namespace GamepadKeyboard.Input
{
    /// <summary>
    /// Key Maps mode: the gamepad as a chord keyboard via maps of key
    /// assignments. L2/L1/R1 run their configured actions (hold/toggle a
    /// modifier, tap a key, fire an app action); holding R2 (the maps key)
    /// selects the map while it is held — the map whose OpenWith combination
    /// (subset of L2/L1/R1/L3/R3) best matches the currently held buttons —
    /// and releasing the maps key returns to the Utility map. Every other physical slot
    /// (d-pad, face, stick deflections, stick presses, Select/Start) sends one
    /// edge-triggered tap of its mapped key, so a held stick never repeats.
    ///
    /// Modifier gating: while the maps key is held, L2/L1/R1 are map-chord
    /// buttons — their Shift/Ctrl/Alt keys are frozen (no taps, no toggles).
    /// A modifier physically held BEFORE the maps key went down stays latched
    /// for the whole hold; RE-PRESSING a chord button while the maps key is
    /// held unlocks AND FREES its modifier — from that moment the key tracks
    /// the physical control exactly as if the maps key were not held (useful
    /// for Shift/L2: press L2 again under R2 → Shift works normally). The
    /// Windows key (right-stick press, not a chord button) keeps the legacy
    /// latch: it can be locked and toggled while the maps key is held.
    ///
    /// Modifier state is driven by real KeyDown/KeyUp pairs, so a held modifier
    /// naturally combines with every tapped map key. Punctuation slots are
    /// typed through KEYEVENTF_UNICODE so the exact character lands even with
    /// Ctrl or Alt held: a VK tap of "+" emits "=" on a US layout, and real
    /// modifiers would further rewrite it.
    /// </summary>
    public sealed class KeyMapsMapper
    {
        // ── Overlay-facing state (poll thread writes, UI thread reads) ────────

        /// <summary>True while the maps key (R2) is held — drives the overlay's
        /// shadow-key display.</summary>
        public bool MapsKeyHeld { get; private set; }

        /// <summary>Active map: 0 = Utility, 1 = Symbols 1, 2 = Symbols 2,
        /// 3 = Symbols 3, 4 = Function Keys (indexes into KeyMapsSettings.Maps
        /// and the overlay's layout array).</summary>
        public int ActiveMapIndex { get; private set; }

        /// <summary>Physical chord state while the maps key is held — true
        /// while the held buttons are exactly a candidate map's OpenWith set
        /// (the overlay brightens the matching quark); the ACTIVE map may
        /// differ when two combinations match simultaneously.</summary>
        public bool Sym2ComboHeld { get; private set; }
        public bool Sym3ComboHeld { get; private set; }
        public bool FunctionComboHeld { get; private set; }

        /// <summary>Effective modifier states — what the overlay's
        /// [Ctrl][Shift][Alt][Windows] chips highlight. Derived from every
        /// configurable system button's current down virtual key (hold or
        /// sticky toggle) plus the right-stick-press hold slot.</summary>
        public bool CtrlHeld => IsModifierFamilyDown(Vk.LControl, Vk.RControl);
        public bool ShiftHeld => IsModifierFamilyDown(Vk.LShift, Vk.RShift);
        public bool AltHeld => IsModifierFamilyDown(Vk.LMenu, Vk.RMenu);
        public bool WindowsHeld => IsModifierFamilyDown(Vk.LWin, Vk.RWin) || _windowsHeld;

        /// <summary>Which gamepad control (L2/L1/R1/R2/R3, "" = none) currently
        /// drives a modifier family — resolved from the configured system-button
        /// actions plus the Utility map's right-stick-press slot. The keyboard
        /// overlay uses this to draw the pad-button badge on the matching key
        /// (Shift/Ctrl/Alt/Windows).</summary>
        public static string ModifierDriverButton(ushort primaryVk, ushort alternateVk)
        {
            KeyMapsSettings settings = AppSettings.Instance.KeyMaps;
            string result = DriverIfAction(settings.RightTriggerAction, primaryVk, alternateVk, "R2")
                ?? DriverIfAction(settings.LeftTriggerAction, primaryVk, alternateVk, "L2")
                ?? DriverIfAction(settings.LeftBumperAction, primaryVk, alternateVk, "L1")
                ?? DriverIfAction(settings.RightBumperAction, primaryVk, alternateVk, "R1");
            if (result != null)
            {
                return result;
            }
            System.Collections.Generic.IReadOnlyList<KeyMapDefinition> maps = settings.Maps;
            if (maps.Count > 0 && maps[0].RightStickPress == "Windows"
                && (primaryVk == Vk.LWin || alternateVk == Vk.RWin))
            {
                return "R3";
            }
            return "";
        }

        /// <summary>The pad-button name when the action targets the modifier
        /// family (hold or toggle); null when it does not.</summary>
        private static string? DriverIfAction(string action, ushort primaryVk, ushort alternateVk, string button)
        {
            ushort actionVk = action switch
            {
                "HoldShift" or "ToggleShift" => Vk.LShift,
                "HoldCtrl" or "ToggleCtrl" => Vk.LControl,
                "HoldAlt" or "ToggleAlt" => Vk.LMenu,
                "HoldWin" or "ToggleWin" => Vk.LWin,
                _ => Vk.None,
            };
            if (actionVk == Vk.None)
            {
                return null;
            }
            return actionVk == primaryVk || actionVk == alternateVk ? button : null;
        }

        private bool IsModifierFamilyDown(ushort primaryVk, ushort alternateVk)
        {
            if (_heldModifierVks.Contains(primaryVk) || _heldModifierVks.Contains(alternateVk)
                || _toggledModifierVks.Contains(primaryVk) || _toggledModifierVks.Contains(alternateVk))
            {
                return true;
            }
            return ButtonDownVk(_leftTrigger, primaryVk, alternateVk)
                || ButtonDownVk(_leftBumper, primaryVk, alternateVk)
                || ButtonDownVk(_rightBumper, primaryVk, alternateVk)
                || (_windowsHeld && (_latchedWindowsVk == primaryVk || _latchedWindowsVk == alternateVk));
        }

        private static bool ButtonDownVk(SystemButtonState state, ushort primaryVk, ushort alternateVk)
        {
            return state.Held && (state.VirtualKey == primaryVk || state.VirtualKey == alternateVk);
        }

    /// <summary>Preview Maps latched by TogglePreviewMaps — the projected
    /// overlay shows prompts from ALL maps while true (poll-thread write).</summary>
    public bool PreviewMapsOn { get; set; }

    /// <summary>LEVEL input for the "Maps modifier hold" binding action
    /// (poll-thread write each tick; DispatchButton relays hold state). The
    /// maps key = this HOLD, or the sticky TOGGLE below, or the physical
    /// right trigger for backward compatibility.</summary>
    public bool MapsModifierRequested { get; set; }

    /// <summary>EDGE request for the "Maps modifier toggle" binding action;
    /// consumed (reset) by the next poll tick.</summary>
    public bool MapsModifierToggleRequested { get; set; }


        /// <summary>App-level action requested by a map slot's tap edge
        /// (e.g. Start = MouseMode on the Utility map). Invoked by
        /// ControllerMapper after Process returns; only the last one counts,
        /// which is safe — one switch per tap edge, and two same-tick edges
        /// would also double-fire in the other modes.</summary>
        public Action<string>? ActionRequested;

        private readonly InputSender _sender;

        // ── Edge state: previous-tick heldness per physical input ─────────────
        // All in the mapper, no per-tick allocations; the stick deflections use
        // the SAME thresholded booleans the tap logic sees.
        private bool _seedRun;
        private bool _seeded;
        private bool _mapsKeyToggledOn;
        private bool _previousMapsKey;
        // ── Locks logic: per-button state of the three configurable system
        // buttons (L2/L1/R1). While the maps key is held a chord member is
        // frozen (a modifier held BEFORE the maps key went down latches down
        // for the whole hold); a button in NO combination keeps running its
        // configured action, with the friendlier free semantics: pre-held
        // latches, a fresh press frees the modifier so it tracks the physical
        // control exactly as if the maps key were not held.
        private readonly SystemButtonState _leftTrigger = new();
        private readonly SystemButtonState _leftBumper = new();
        private readonly SystemButtonState _rightBumper = new();
        private readonly SystemButtonState _rightTrigger = new();
        private bool _previousSystemToggleL2;
        private bool _previousSystemToggleL1;
        private bool _previousSystemToggleR1;
        private bool _previousSystemToggleR2;
        private bool _windowsHeld;
        private bool _windowsLocked;
        private ushort _latchedWindowsVk;
        private bool _latchedWindowsExtended;
        private bool _previousDPadUp;
        private bool _previousDPadDown;
        private bool _previousDPadLeft;
        private bool _previousDPadRight;
        private bool _previousFaceY;
        private bool _previousFaceA;
        private bool _previousFaceX;
        private bool _previousFaceB;
        private bool _previousLeftStickUp;
        private bool _previousLeftStickDown;
        private bool _previousLeftStickLeft;
        private bool _previousLeftStickRight;
        private bool _previousRightStickUp;
        private bool _previousRightStickDown;
        private bool _previousRightStickLeft;
        private bool _previousRightStickRight;
        private bool _previousLeftStickPress;
        private bool _previousRightStickPress;
        private bool _previousSelect;
        private bool _previousStart;
        /// <summary>Chord-button mask of the CURRENT tick (L2/L1/R1/L3/R3);
        /// recomputed each tick before selection, read by matching helpers.</summary>
        private string CurrentChordMask = "";
        // Sticky (toggle) modifier VKs and hold-down VKs across ALL system
        // buttons — the Ctrl/Shift/Alt/Windows family probes read these.
        private readonly HashSet<ushort> _toggledModifierVks = new();
        private readonly HashSet<ushort> _heldModifierVks = new();
        private bool _heldXButton1;
        private bool _heldXButton2;

        private int _lastLoggedMapIndex;
        private DateTime _lastMapLogTime = DateTime.MinValue;
        private DateTime _lastErrorLogTime = DateTime.MinValue;

        public KeyMapsMapper(InputSender sender)
        {
            _sender = sender;
        }

        // ── Poll tick: exception isolation + seed gate ─────────────────────────

        public void Process(in GamepadSnapshot snapshot)
        {
            try
            {
                _seedRun = !_seeded;
                _seeded = true;
                // The seed run IS a full tick — the seed gate in HoldSlot
                // suppresses edges, so buttons held across a mode switch or
                // ReleaseAll never fire phantom events on the first tick.
                ProcessTick(snapshot);
            }
            catch (Exception exception)
            {
                // Nothing escapes into the poll loop: swallow + ONE line, 1/s.
                DateTime now = DateTime.UtcNow;
                if ((now - _lastErrorLogTime).TotalSeconds >= 1.0)
                {
                    _lastErrorLogTime = now;
                    App.Log("key maps mode error (rate-limited): " + exception.Message);
                }
            }
        }

        /// <summary>
        /// Drops everything this mode can hold: sends X-up for any held side
        /// mouse button, KeyUp for Shift/Ctrl/Alt/Windows, then re-seeds edges.
        /// CapsLock is a toggle key the OS remembers by design — the spec's
        /// toggle behavior.
        /// </summary>
        public void ReleaseAll()
        {
            _seeded = false;
            MapsModifierToggleRequested = false;
            MapsModifierRequested = false;
            ReleaseSystemButton(_leftTrigger);
            ReleaseSystemButton(_leftBumper);
            ReleaseSystemButton(_rightBumper);
            ReleaseSystemButton(_rightTrigger);
            if (_windowsHeld && _latchedWindowsVk != Vk.None)
            {
                _sender.KeyUp(_latchedWindowsVk, _latchedWindowsExtended);
            }
            _windowsHeld = false;
            _windowsLocked = false;
            ControllerMapper.HoldShadowMapsActive = false;
            ControllerMapper.HoldPreviewMapsActive = false;
            _latchedWindowsVk = Vk.None;
            _toggledModifierVks.Clear();
            _heldModifierVks.Clear();
            _previousMapsKey = false;
            _mapsKeyToggledOn = false;
            _previousSystemToggleL2 = false;
            _previousSystemToggleL1 = false;
            _previousSystemToggleR1 = false;
            _previousSystemToggleR2 = false;
            _previousRightStickPress = false;
            _previousStart = false;
            MapsKeyHeld = false;
            Sym2ComboHeld = Sym3ComboHeld = FunctionComboHeld = false;
            ActiveMapIndex = 0;
            _lastLoggedMapIndex = 0;
        }

        // ── One poll tick ──────────────────────────────────────────────────────

        private void ProcessTick(in GamepadSnapshot snapshot)
        {
            // Map selection (physical chord) FIRST: the modifier latch needs to
            // know whether the maps key just went down on this very tick.
            // Maps key source, in priority order: the sticky TOGGLE request
            // (MapsModifierToggle binding flips it; an edge also unlatches),
            // the HOLD request (MapsModifierHold binding), the physical right
            // trigger (backward compatibility / default binding).
            bool toggleEdge = MapsModifierToggleRequested;
            MapsModifierToggleRequested = false;
            if (toggleEdge)
            {
                _mapsKeyToggledOn = !_mapsKeyToggledOn;
            }
            KeyMapsSettings mapsSettings = AppSettings.Instance.KeyMaps;
            // A button whose action IS a maps-modifier action is the maps key
            // itself — it never counts toward a chord (its combos are stripped).
            bool mapsKeyIsL2 = mapsSettings.LeftTriggerAction is "MapsModifierHold" or "MapsModifierToggle";
            bool mapsKeyIsL1 = mapsSettings.LeftBumperAction is "MapsModifierHold" or "MapsModifierToggle";
            bool mapsKeyIsR1 = mapsSettings.RightBumperAction is "MapsModifierHold" or "MapsModifierToggle";
            bool mapsKeyIsR2 = mapsSettings.RightTriggerAction is "MapsModifierHold" or "MapsModifierToggle";
            // System buttons mapped to a maps-modifier TOGGLE flip the latch on
            // their press edge (not level-follow); HOLD buttons follow level.
            bool l2Toggle = mapsKeyIsL2 && mapsSettings.LeftTriggerAction == "MapsModifierToggle";
            bool l1Toggle = mapsKeyIsL1 && mapsSettings.LeftBumperAction == "MapsModifierToggle";
            bool r1Toggle = mapsKeyIsR1 && mapsSettings.RightBumperAction == "MapsModifierToggle";
            bool r2Toggle = mapsKeyIsR2 && mapsSettings.RightTriggerAction == "MapsModifierToggle";
            bool l2Held = mapsKeyIsL2 && snapshot.LeftTrigger >= 0.5 && mapsSettings.LeftTriggerAction == "MapsModifierHold";
            bool l1Held = mapsKeyIsL1 && snapshot.LB && mapsSettings.LeftBumperAction == "MapsModifierHold";
            bool r1Held = mapsKeyIsR1 && snapshot.RB && mapsSettings.RightBumperAction == "MapsModifierHold";
            bool r2Held = mapsKeyIsR2 && snapshot.RightTrigger >= 0.5 && mapsSettings.RightTriggerAction == "MapsModifierHold";
            bool systemToggleEdge = (l2Toggle && snapshot.LeftTrigger >= 0.5 && !_previousSystemToggleL2)
                || (l1Toggle && snapshot.LB && !_previousSystemToggleL1)
                || (r1Toggle && snapshot.RB && !_previousSystemToggleR1)
                || (r2Toggle && snapshot.RightTrigger >= 0.5 && !_previousSystemToggleR2);
            _previousSystemToggleL2 = l2Toggle && snapshot.LeftTrigger >= 0.5;
            _previousSystemToggleL1 = l1Toggle && snapshot.LB;
            _previousSystemToggleR1 = r1Toggle && snapshot.RB;
            _previousSystemToggleR2 = r2Toggle && snapshot.RightTrigger >= 0.5;
            if (systemToggleEdge)
            {
                _mapsKeyToggledOn = !_mapsKeyToggledOn;
            }
            bool heldFromBinding = MapsModifierRequested
                // A system button mapped to a maps-modifier HOLD acts as the
                // maps key too — any trigger/bumper can open the maps.
                || l2Held || l1Held || r1Held || r2Held;
            bool mapsKey = _mapsKeyToggledOn || heldFromBinding;
            CurrentChordMask = ChordMask(
                snapshot.LeftTrigger >= 0.5 && !mapsKeyIsL2, snapshot.LB && !mapsKeyIsL1,
                snapshot.RB && !mapsKeyIsR1, snapshot.LS, snapshot.RS)
                + (mapsKeyIsR2 ? "" : snapshot.RightTrigger >= 0.5 ? "+R2" : "");
            if (CurrentChordMask.StartsWith('+'))
            {
                CurrentChordMask = CurrentChordMask[1..];
            }
            bool mapsKeyEdge = mapsKey && !_previousMapsKey;
            _previousMapsKey = mapsKey;
            int mapIndex = SelectMapForChord(mapsSettings, mapsKey);
            MapsKeyHeld = mapsKey;
            ActiveMapIndex = mapIndex;
            UpdateComboHighlights(mapsSettings, mapsKey);
            LogMapChange(mapIndex);

            KeyMapDefinition map = mapsSettings.Maps[Math.Clamp(mapIndex, 0, mapsSettings.Maps.Count - 1)];

            // System buttons (L2/L1/R1) run their configured actions through
            // the locks logic: while the maps key is held a chord member is
            // frozen (a pre-held modifier latches down, a re-press frees it),
            // a button in NO combination keeps working. R3/L3 chord membership
            // freezes the respective stick-press slots.
            UpdateSystemButton(_leftTrigger, snapshot.LeftTrigger >= 0.5,
                mapsSettings.LeftTriggerAction, mapsKey && !mapsKeyIsL2, mapsKeyEdge && !mapsKeyIsL2,
                ChordUses(mapsSettings, "L2"), mapsSettings.LeftTriggerMitigateLock);
            UpdateSystemButton(_leftBumper, snapshot.LB,
                mapsSettings.LeftBumperAction, mapsKey && !mapsKeyIsL1, mapsKeyEdge && !mapsKeyIsL1,
                ChordUses(mapsSettings, "L1"), mapsSettings.LeftBumperMitigateLock);
            UpdateSystemButton(_rightBumper, snapshot.RB,
                mapsSettings.RightBumperAction, mapsKey && !mapsKeyIsR1, mapsKeyEdge && !mapsKeyIsR1,
                ChordUses(mapsSettings, "R1"), mapsSettings.RightBumperMitigateLock);
            UpdateSystemButton(_rightTrigger, snapshot.RightTrigger >= 0.5,
                mapsSettings.RightTriggerAction, mapsKey && !mapsKeyIsR2, mapsKeyEdge && !mapsKeyIsR2,
                ChordUses(mapsSettings, "R2"), mapsSettings.RightTriggerMitigateLock);
            UpdateWindowsHold(snapshot.RS, mapsKey, mapsKeyEdge, map,
                ChordUses(mapsSettings, "R3"));

            double threshold = Math.Clamp(AppSettings.Instance.KeyMaps.StickTapThreshold, 0.05, 1.0);
            HoldSlot(ref _previousDPadUp, snapshot.DUp, map.DPadUp);
            HoldSlot(ref _previousDPadDown, snapshot.DDown, map.DPadDown);
            HoldSlot(ref _previousDPadLeft, snapshot.DLeft, map.DPadLeft);
            HoldSlot(ref _previousDPadRight, snapshot.DRight, map.DPadRight);
            HoldSlot(ref _previousFaceY, snapshot.Y, map.FaceY);
            HoldSlot(ref _previousFaceA, snapshot.A, map.FaceA);
            HoldSlot(ref _previousFaceX, snapshot.X, map.FaceX);
            HoldSlot(ref _previousFaceB, snapshot.B, map.FaceB);
            HoldSlot(ref _previousLeftStickUp, snapshot.LY >= threshold, map.LeftStickUp);
            HoldSlot(ref _previousLeftStickDown, snapshot.LY <= -threshold, map.LeftStickDown);
            HoldSlot(ref _previousLeftStickLeft, snapshot.LX <= -threshold, map.LeftStickLeft);
            HoldSlot(ref _previousLeftStickRight, snapshot.LX >= threshold, map.LeftStickRight);
            HoldSlot(ref _previousRightStickUp, snapshot.RY >= threshold, map.RightStickUp);
            HoldSlot(ref _previousRightStickDown, snapshot.RY <= -threshold, map.RightStickDown);
            HoldSlot(ref _previousRightStickLeft, snapshot.RX <= -threshold, map.RightStickLeft);
            HoldSlot(ref _previousRightStickRight, snapshot.RX >= threshold, map.RightStickRight);
            HoldSlot(ref _previousLeftStickPress, snapshot.LS, map.LeftStickPress,
                mapsKey && ChordUses(mapsSettings, "L3"));
            HoldSlot(ref _previousSelect, snapshot.View, map.Select);

            // Start LAST: on the Utility map it requests the MouseMode switch,
            // which ends this mode for the rest of the tick.
            if (_seedRun)
            {
                _previousStart = snapshot.Menu;
            }
            else
            {
                DispatchStartSlot(ref _previousStart, snapshot.Menu, map.Start);
            }
        }

        // ── System buttons: configurable actions + maps-key locks logic ────────

        private sealed class SystemButtonState
        {
            public bool Held;
            public bool Locked;
            public bool Freed;
            public bool PreviousPhysical;
            public ushort VirtualKey;
            public bool Extended;
            public string Action = "";
        }

        /// <summary>One configurable system button's full state machine.
        /// Outside the maps key the configured action runs exactly as the
        /// bindings editor would (hold/toggle modifier, key hold, mouse hold,
        /// app action on the press edge). While the maps key is held and the
        /// button IS a chord member (takes part in any OpenWith combination)
        /// the action is frozen: a modifier held BEFORE the maps key went down
        /// LATCHES down for the whole hold; anything else goes silent so the
        /// button purely selects maps. While the maps key is held and the
        /// button is in NO combination, the friendlier free semantics apply:
        /// pre-held latches down, a fresh press releases the freeze so the
        /// modifier tracks the physical control (the maps key never hijacks an
        /// unneeded button — this keeps Hold Ctrl/Shift/Alt/Windows working
        /// through the locks logic).</summary>
        /// <summary>One configurable system button's full state machine.
        /// Outside the maps key the configured action runs on the physical
        /// edges (hold/toggle modifier, key hold, mouse hold, app action).
        /// While the maps key is held:
        /// • chord member (in an OpenWith combination) → frozen so the button
        ///   purely selects maps, EXCEPT a modifier: held BEFORE the maps key
        ///   edge → latched down for the whole hold; a re-press while held →
        ///   unlocked AND freed so it tracks the physical control (the user is
        ///   never stuck with a stuck modifier — Shift/L2 typing use case);
        /// • NOT a chord member → keeps working: pre-held latches, a fresh
        ///   press frees the modifier so Hold Ctrl/Shift/Alt/Windows actions
        ///   work through the locks logic exactly as without the maps key.
        /// </summary>
                private void UpdateSystemButton(
            SystemButtonState state,
            bool physical,
            string action,
            bool mapsKey,
            bool mapsKeyEdge,
            bool isChordMember,
            bool mitigateLock)
        {
            if (!mapsKey)
            {
                state.Locked = false;
                state.Freed = false;
                RunAction(state, physical, action);
                state.PreviousPhysical = physical;
                return;
            }
            // The maps key is held. How this button's action behaves:
            // • Pre-held (physically down at the maps-key edge): the key LATCHES
            //   in its current state ("sticks") — Locked — regardless of
            //   membership or mitigation.
            // • Chord members (part of some map-open combination) stay locked
            //   until the maps key is released; a re-press frees them ONLY when
            //   the button's action is a hold modifier AND mitigation is on.
            // • Non-members with mitigation ON behave like the classic Shift
            //   exception: pre-held sticks, re-press unlocks + frees.
            // • Non-members with mitigation OFF (default): fresh presses run
            //   normally (the modifier was not part of the chord), but a
            //   LATCHED pre-held state never unlocks by re-pressing — it stays
            //   until the maps key is released.
            if (mapsKeyEdge)
            {
                state.Locked = physical && state.Held;
                state.Freed = false;
            }
            bool canRepressUnlock = mitigateLock && IsHoldModifierAction(action);
            if (state.Locked && !state.Freed)
            {
                // Latched: keep the virtual key down; a re-press only frees the
                // modifier when mitigation allows it.
                if (canRepressUnlock && physical && !state.PreviousPhysical)
                {
                    state.Locked = false;
                    state.Freed = true;
                }
                state.PreviousPhysical = physical;
                return;
            }
            if (isChordMember && !canRepressUnlock)
            {
                // Chord member without mitigation: frozen for this maps hold.
                state.PreviousPhysical = physical;
                return;
            }
            RunAction(state, physical, action);
            state.PreviousPhysical = physical;
        }

private static bool IsHoldModifierAction(string action) => action is
            "HoldShift" or "HoldCtrl" or "HoldAlt" or "HoldWin"
            or "ToggleShift" or "ToggleCtrl" or "ToggleAlt" or "ToggleWin"
            or "MapsModifierHold" or "MapsModifierToggle";

        /// <summary>Runs the configured action for one system button on its
        /// physical edges.</summary>
        private void RunAction(SystemButtonState state, bool physical, string action)
        {
            if (!string.Equals(state.Action, action, StringComparison.Ordinal) && state.Held
                && state.VirtualKey != Vk.None)
            {
                // The configured action changed while a hold was down: release
                // the OLD key so the new action does not leave it stuck.
                _sender.KeyUp(state.VirtualKey, state.Extended);
                _heldModifierVks.Remove(state.VirtualKey);
                state.Held = false;
            }
            state.Action = action;
            if (string.IsNullOrWhiteSpace(action)
                || string.Equals(action, "None", StringComparison.OrdinalIgnoreCase))
            {
                state.VirtualKey = Vk.None;
                state.Held = false;
                return;
            }
            switch (action)
            {
                case "MapsModifierHold":
                case "MapsModifierToggle":
                    // Handled by the maps-key source in ProcessTick (the button
                    // IS the maps key); RunAction stays a no-op so the pass in
                    // which mapsKey reads false never double-fires.
                    state.VirtualKey = Vk.None;
                    state.Held = false;
                    return;
                case "HoldShift":
                case "HoldCtrl":
                case "HoldAlt":
                case "HoldWin":
                    ushort modifierVk = ActionVkFor(action);
                    state.VirtualKey = modifierVk;
                    state.Extended = false;
                    if (physical && !state.Held)
                    {
                        _sender.KeyDown(modifierVk);
                        state.Held = true;
                        // A hold supersedes a sticky toggle in the same family.
                        _toggledModifierVks.Remove(modifierVk);
                        _heldModifierVks.Add(modifierVk);
                    }
                    else if (!physical && state.Held)
                    {
                        _sender.KeyUp(modifierVk);
                        state.Held = false;
                        _heldModifierVks.Remove(modifierVk);
                    }
                    return;
                case "ToggleShift":
                case "ToggleCtrl":
                case "ToggleAlt":
                case "ToggleWin":
                    state.VirtualKey = Vk.None;
                    if (physical && !state.PreviousPhysical)
                    {
                        ToggleSystemModifier(ActionVkFor(action));
                    }
                    return;
                default:
                    // Plain key / mouse / app action: hold semantics down on
                    // press, up on release; app actions fire on the press edge.
                    // The seed run stays silent (legacy: no phantom key/app
                    // fires from buttons held across a mode switch).
                    state.VirtualKey = Vk.None;
                    if (_seedRun)
                    {
                        return;
                    }
                    if (physical && !state.PreviousPhysical)
                    {
                        SendSlotDown(ResolveSlotKeyName(action), ControllerMapper.KeyMapsMoveModeActive
                            && !string.Equals(action, "ToggleKeyMapsMoveMode", StringComparison.Ordinal));
                    }
                    else if (!physical && state.PreviousPhysical)
                    {
                        SendSlotUp(ResolveSlotKeyName(action), ControllerMapper.KeyMapsMoveModeActive
                            && !string.Equals(action, "ToggleKeyMapsMoveMode", StringComparison.Ordinal));
                    }
                    return;
            }
        }

        private static ushort ActionVkFor(string action) => action switch
        {
            "HoldShift" or "ToggleShift" => Vk.LShift,
            "HoldCtrl" or "ToggleCtrl" => Vk.LControl,
            "HoldAlt" or "ToggleAlt" => Vk.LMenu,
            "HoldWin" or "ToggleWin" => Vk.LWin,
            _ => Vk.None,
        };

        private void ToggleSystemModifier(ushort virtualKey)
        {
            if (_toggledModifierVks.Remove(virtualKey))
            {
                if (!_heldModifierVks.Contains(virtualKey))
                {
                    _sender.KeyUp(virtualKey);
                }
                return;
            }
            _toggledModifierVks.Add(virtualKey);
            if (_heldModifierVks.Add(virtualKey))
            {
                _sender.KeyDown(virtualKey);
            }
        }

        /// <summary>Releases everything a system button can hold.</summary>
        private void ReleaseSystemButton(SystemButtonState state)
        {
            if (state.Held && state.VirtualKey != Vk.None)
            {
                _sender.KeyUp(state.VirtualKey, state.Extended);
                _heldModifierVks.Remove(state.VirtualKey);
            }
            state.Held = false;
            state.Locked = false;
            state.Freed = false;
            state.VirtualKey = Vk.None;
            state.Action = "";
        }

        // ── Chord masks: canonical OpenWith parsing + best-subset selection ────

        /// <summary>Held-button mask, canonical order L2, L1, R1, L3, R3
        /// ("", "L1", "L1+R3", ...). Returns canonical constants for the
        /// common shapes so mask comparison with configured combos is
        /// allocation-free in the hot path.</summary>
        private static string ChordMask(bool l2, bool l1, bool r1, bool l3, bool r3)
        {
            if (!l2 && !l1 && !r1 && !l3 && !r3) return "";
            if (!l2 && l1 && !r1 && !l3 && !r3) return "L1";
            if (!l2 && !l1 && r1 && !l3 && !r3) return "R1";
            if (!l2 && l1 && r1 && !l3 && !r3) return "L1+R1";
            if (!l2 && !l1 && !r1 && l3 && !r3) return "L3";
            if (!l2 && !l1 && !r1 && !l3 && r3) return "R3";
            if (!l2 && l1 && !r1 && l3 && !r3) return "L1+L3";
            if (!l2 && l1 && !r1 && !l3 && r3) return "L1+R3";
            if (!l2 && !l1 && r1 && !l3 && r3) return "R1+R3";
            if (!l2 && l1 && r1 && l3 && !r3) return "L1+R1+L3";
            if (!l2 && l1 && r1 && !l3 && r3) return "L1+R1+R3";
            if (!l2 && !l1 && r1 && l3 && r3) return "R1+L3+R3";
            if (l2 && !l1 && !r1 && !l3 && !r3) return "L2";
            System.Text.StringBuilder builder = new(14);
            if (l2) builder.Append("L2+");
            if (l1) builder.Append("L1+");
            if (r1) builder.Append("R1+");
            if (l3) builder.Append("L3+");
            if (r3) builder.Append("R3");
            return builder[^1] == '+' ? builder.ToString(0, builder.Length - 1) : builder.ToString();
        }

        private static bool IsCanonicalChord(string combo)
        {
            if (combo.Length == 0)
            {
                return true;
            }
            foreach (string part in combo.Split('+'))
            {
                if (part is not ("L2" or "L1" or "R1" or "R2" or "L3" or "R3"))
                {
                    return false;
                }
            }
            return true;
        }

        private static int CountChordButtons(string combo)
        {
            int count = 1;
            foreach (char c in combo)
            {
                if (c == '+') count++;
            }
            return count;
        }

        private static bool ChordSubset(string candidate, string held)
        {
            if (candidate.Length == 0)
            {
                return true;
            }
            foreach (string part in candidate.Split('+'))
            {
                if (!HeldChordContains(held, part))
                {
                    return false;
                }
            }
            return true;
        }

        private static bool HeldChordContains(string held, string button)
        {
            return held.Split('+').Contains(button, StringComparer.Ordinal);
        }

        private static bool ChordUses(KeyMapsSettings settings, string button)
        {
            for (int index = 1; index < settings.Maps.Count; index++)
            {
                string combo = settings.Maps[index].OpenWith ?? string.Empty;
                if (!IsCanonicalChord(combo))
                {
                    continue;
                }
                foreach (string part in combo.Split('+'))
                {
                    if (part == button)
                    {
                        return true;
                    }
                }
            }
            return false;
        }

        /// <summary>Best-subset map selection: the candidate map whose OpenWith
        /// combination is a subset of the held chord buttons with the LARGEST
        /// button count wins (ties → first match); none → Utility (0). The
        /// maps key alone (empty combination) matches candidates with
        /// OpenWith = "".</summary>
        private int SelectMapForChord(KeyMapsSettings settings, bool mapsKey)
        {
            if (!mapsKey)
            {
                return 0;
            }
            string held = CurrentChordMask;
            int best = 0;
            int bestSize = -1;
            for (int candidate = 1; candidate < settings.Maps.Count; candidate++)
            {
                string combo = settings.Maps[candidate].OpenWith ?? string.Empty;
                if (!IsCanonicalChord(combo) || !ChordSubset(combo, held))
                {
                    continue;
                }
                int size = combo.Length == 0 ? 0 : CountChordButtons(combo);
                if (size > bestSize)
                {
                    bestSize = size;
                    best = candidate;
                }
            }
            return best;
        }

        /// <summary>Quark/chip highlight state — INDEX-based: the highlight
        /// marks the map actually selected by the current chord (right quark =
        /// Symbols 2, left = Symbols 3, bottom = Function Keys), so it stays
        /// correct with ANY configured open combinations.</summary>
        private void UpdateComboHighlights(KeyMapsSettings settings, bool mapsKey)
        {
            if (!mapsKey)
            {
                Sym2ComboHeld = Sym3ComboHeld = FunctionComboHeld = false;
                return;
            }
            int selected = ActiveMapIndex;
            Sym2ComboHeld = selected == 2;
            Sym3ComboHeld = selected == 3;
            FunctionComboHeld = selected >= 4;
        }

        /// <summary>
        /// One modifier's full state machine. Outside the maps key: the key
        /// simply follows the physical control (KeyDown/KeyUp on edges). On the
        /// maps-key press edge: whatever is physically held LATCHES down (real
        /// KeyDown already sent, or sent now) and stays down regardless of the
        /// physical control. While the maps key is held: the FIRST fresh press
        /// of the physical control unlocks AND FREES the modifier (whether or
        /// not it was held before the maps key) so it follows the
        /// physical control as if the maps key were not held (the user is
        /// never stuck with a stuck modifier).
        /// </summary>
        /// <summary>One modifier's full state machine kept as the legacy
        /// fallback for the Windows hold slot (see UpdateLatchedModifierLegacy).
        /// The L2/L1/R1 buttons run through UpdateSystemButton instead, which
        /// supports the configurable Hold/Toggle/key/mouse/app actions.</summary>
        /// <summary>
        /// The right-stick PRESS is a HOLD slot (default: Windows key) — key
        /// goes down with the stick press and up with its release. The mapped
        /// key participates in the maps-key latch like the modifiers do.
        /// </summary>
        private void UpdateWindowsHold(
            bool physical, bool mapsKey, bool mapsKeyEdge, KeyMapDefinition map,
            bool isChordMember)
        {
            // R3 in a combination → chord member under the maps key: frozen
            // (the stick press is consumed by map selection). A windows key
            // latched BEFORE the maps-key edge stays down for the whole hold;
            // a RE-PRESS of the stick while the maps key is held unlocks the
            // latched key so it follows the physical control again.
            if (mapsKey && isChordMember)
            {
                if (mapsKeyEdge)
                {
                    _windowsLocked = physical && _windowsHeld;
                }
                else if (physical && !_previousRightStickPress && _windowsLocked && _windowsHeld)
                {
                    // Re-press: unlock + release the latched key.
                    _windowsLocked = false;
                    _sender.KeyUp(_latchedWindowsVk == Vk.None ? Vk.LWin : _latchedWindowsVk,
                        _latchedWindowsExtended);
                    _windowsHeld = false;
                }
                _previousRightStickPress = physical;
                _latchedWindowsVk = _windowsHeld ? _latchedWindowsVk : Vk.None;
                return;
            }
            string slot = map.RightStickPress;
            if (ControllerMapper.IsAppLevelAction(slot))
            {
                bool boardOwnsInput = ControllerMapper.KeyMapsMoveModeActive
                    && !string.Equals(slot, "ToggleKeyMapsMoveMode", StringComparison.Ordinal);
                if (physical && !_previousRightStickPress && !_seedRun && !boardOwnsInput)
                {
                    ActionRequested?.Invoke(slot);
                }
                _previousRightStickPress = physical;
                _latchedWindowsVk = Vk.None;
                return;
            }
            if (string.Equals(slot, "XButton1", StringComparison.OrdinalIgnoreCase)
                || string.Equals(slot, "XButton2", StringComparison.OrdinalIgnoreCase))
            {
                // Mouse side buttons: true hold (down on press, up on release);
                // while the board owns the input the mapping stays silent.
                bool boardOwnsInput = ControllerMapper.KeyMapsMoveModeActive
                    && !string.Equals(slot, "ToggleKeyMapsMoveMode", StringComparison.Ordinal);
                if (!boardOwnsInput)
                {
                    if (physical && !_previousRightStickPress && !_seedRun)
                    {
                        _ = HandleMouseSlotDown(slot);
                    }
                    else if (!physical && _previousRightStickPress && !_seedRun)
                    {
                        _ = HandleMouseSlotUp(slot);
                    }
                }
                _previousRightStickPress = physical;
                _latchedWindowsVk = Vk.None;
                return;
            }
            ushort virtualKey = ControllerMapper.NamedVk(slot);
            if (virtualKey == Vk.None)
            {
                _previousRightStickPress = physical;
                _latchedWindowsVk = Vk.None;
                return;
            }
            if (ControllerMapper.KeyMapsMoveModeActive
                && !string.Equals(slot, "ToggleKeyMapsMoveMode", StringComparison.Ordinal))
            {
                // Board owns the input: hold the key-down state frozen (no
                // edges) so the Windows latch cannot fire under the sticks.
                _previousRightStickPress = physical;
                return;
            }
            bool extended = ControllerMapper.IsExtendedKey(virtualKey);
            UpdateLatchedModifierLegacy(physical, mapsKey, mapsKeyEdge,
                ref _previousRightStickPress, ref _windowsLocked, ref _windowsHeld,
                virtualKey, extended);
            _latchedWindowsVk = _windowsHeld ? virtualKey : Vk.None;
            _latchedWindowsExtended = extended;
        }

        /// <summary>
        /// HOLD slot (non-modifier buttons): the mapped key goes down on the
        /// button-down edge and up on the release, so held buttons stay held.
        /// Modifier chord buttons (shoulders/triggers) never reach this — the
        /// modifier and map-selection paths consume them first. Slots that
        /// cannot hold (app actions, mouse-button slots, Unicode-typed
        /// punctuation) keep tap/edge behavior.
        /// While the move/scale-board mode is active the gamepad drives the
        /// board, so EVERY slot mapping stays silent — plain keys, app-level
        /// actions, clicks — except the move-mode toggle itself (so the same
        /// button switches the mode back off).
        /// </summary>
        private void HoldSlot(ref bool previous, bool held, string slot, bool chordFrozen = false)
        {
            // A chord-member button (stick press in an OpenWith combination)
            // is consumed by map selection while the maps key is held — its
            // slot mapping stays silent (and remembers no edges).
            if (chordFrozen)
            {
                previous = held;
                return;
            }
            if (!_seedRun)
            {
                if (held && !previous)
                {
                    bool boardOwnsInput = ControllerMapper.KeyMapsMoveModeActive
                        && !string.Equals(slot, "ToggleKeyMapsMoveMode", StringComparison.Ordinal);
                    SendSlotDown(slot, boardOwnsInput);
                }
                else if (!held && previous)
                {
                    SendSlotUp(slot, ControllerMapper.KeyMapsMoveModeActive
                        && !string.Equals(slot, "ToggleKeyMapsMoveMode", StringComparison.Ordinal));
                }
            }
            previous = held;
        }

        private void SendSlotDown(string slot, bool suppressSlot = false)
        {
            if (string.IsNullOrWhiteSpace(slot)
                || string.Equals(slot, "None", StringComparison.OrdinalIgnoreCase))
            {
                return;
            }
            if (suppressSlot)
            {
                // Stick deflection while the move/scale-board mode is active:
                // the stick drives the board, its mapping stays silent.
                return;
            }
            if (string.Equals(slot, "HoldShadowMaps", StringComparison.Ordinal))
            {
                ControllerMapper.HoldShadowMapsActive = true;
                return;
            }
            if (string.Equals(slot, "HoldPreviewMaps", StringComparison.Ordinal))
            {
                ControllerMapper.HoldPreviewMapsActive = true;
                return;
            }
            if (slot is "ToggleShadowMaps" or "TogglePreviewMaps")
            {
                // Tap-edge toggles once on press; release does nothing.
                ActionRequested?.Invoke(slot);
                return;
            }
            if (ControllerMapper.IsAppLevelAction(slot))
            {
                ActionRequested?.Invoke(slot);
                return;
            }
            if (HandleMouseSlotDown(slot))
            {
                return;
            }
            ushort virtualKey = ControllerMapper.NamedVk(ResolveSlotKeyName(slot));
            if (virtualKey != Vk.None)
            {
                _sender.KeyDown(virtualKey, ControllerMapper.IsExtendedKey(virtualKey));
                return;
            }
            if (slot.Length == 1)
            {
                // Punctuation has no held form — tap the exact glyph (the
                // shift-aware path rewrites it when Shift is held).
                SendShiftAwareText(slot);
            }
        }

        private void SendSlotUp(string slot, bool suppressSlot = false)
        {
            if (string.IsNullOrWhiteSpace(slot)
                || string.Equals(slot, "None", StringComparison.OrdinalIgnoreCase))
            {
                return;
            }
            if (suppressSlot
                && slot is not ("ToggleShadowMaps" or "TogglePreviewMaps" or "ToggleKeyMapsMoveMode"))
            {
                return;
            }
            if (string.Equals(slot, "HoldShadowMaps", StringComparison.Ordinal))
            {
                ControllerMapper.HoldShadowMapsActive = false;
                return;
            }
            if (string.Equals(slot, "HoldPreviewMaps", StringComparison.Ordinal))
            {
                ControllerMapper.HoldPreviewMapsActive = false;
                return;
            }
            if (slot is "ToggleShadowMaps" or "TogglePreviewMaps")
            {
                return;
            }
            if (ControllerMapper.IsAppLevelAction(slot))
            {
                return;
            }
            if (HandleMouseSlotUp(slot))
            {
                return;
            }
            ushort virtualKey = ControllerMapper.NamedVk(ResolveSlotKeyName(slot));
            if (virtualKey != Vk.None)
            {
                _sender.KeyUp(virtualKey, ControllerMapper.IsExtendedKey(virtualKey));
            }
        }

        private bool HandleMouseSlotDown(string slot)
        {
            if (string.Equals(slot, "XButton1", StringComparison.OrdinalIgnoreCase))
            {
                if (!_heldXButton1)
                {
                    _sender.MouseButtonPress(NativeMethods.MOUSEEVENTF_XDOWN, 1u);
                    _heldXButton1 = true;
                }
                return true;
            }
            if (string.Equals(slot, "XButton2", StringComparison.OrdinalIgnoreCase))
            {
                if (!_heldXButton2)
                {
                    _sender.MouseButtonPress(NativeMethods.MOUSEEVENTF_XDOWN, 2u);
                    _heldXButton2 = true;
                }
                return true;
            }
            return false;
        }

        private bool HandleMouseSlotUp(string slot)
        {
            if (string.Equals(slot, "XButton1", StringComparison.OrdinalIgnoreCase))
            {
                if (_heldXButton1)
                {
                    _sender.MouseButtonRelease(NativeMethods.MOUSEEVENTF_XUP, 1u);
                    _heldXButton1 = false;
                }
                return true;
            }
            if (string.Equals(slot, "XButton2", StringComparison.OrdinalIgnoreCase))
            {
                if (_heldXButton2)
                {
                    _sender.MouseButtonRelease(NativeMethods.MOUSEEVENTF_XUP, 2u);
                    _heldXButton2 = false;
                }
                return true;
            }
            return false;
        }


        /// <summary>Single-character punctuation under a held Shift sends its
        /// shifted glyph (Unicode typing ignores the physical modifier).</summary>
        private void SendShiftAwareText(string slot)
        {
            string sent = KeyMapsShift.SentValue(slot);
            _sender.TypeText(sent);
        }


        /// <summary>Old latch semantics (edge-latch + fresh-press toggle) kept
        /// for the Windows hold slot, whose button is not a map chord.</summary>
        private void UpdateLatchedModifierLegacy(
            bool physical, bool mapsKey, bool mapsKeyEdge,
            ref bool previousPhysical, ref bool locked, ref bool held,
            ushort virtualKey, bool extended)
        {
            if (mapsKeyEdge)
            {
                if (physical && !held)
                {
                    _sender.KeyDown(virtualKey, extended);
                    held = true;
                }
                locked = physical;
                previousPhysical = physical;
                return;
            }
            if (mapsKey)
            {
                if (physical && !previousPhysical)
                {
                    if (locked)
                    {
                        locked = false;
                        if (held)
                        {
                            _sender.KeyUp(virtualKey, extended);
                            held = false;
                        }
                    }
                    else
                    {
                        locked = true;
                        if (!held)
                        {
                            _sender.KeyDown(virtualKey, extended);
                            held = true;
                        }
                    }
                }
                previousPhysical = physical;
                return;
            }
            locked = false;
            if (held != physical)
            {
                if (physical)
                {
                    _sender.KeyDown(virtualKey, extended);
                }
                else
                {
                    _sender.KeyUp(virtualKey, extended);
                }
                held = physical;
            }
            previousPhysical = physical;
        }


        /// <summary>Pool-captured slot values arrive as "Key:<Name>"; strip
        /// the prefix before VK resolution.</summary>
        private static string ResolveSlotKeyName(string slot)
        {
            return slot.StartsWith("Key:", StringComparison.Ordinal) ? slot[4..] : slot;
        }

        private void DispatchStartSlot(ref bool previous, bool held, string slot)
        {
            if (held && !previous && !_seedRun && ControllerMapper.IsAppLevelAction(slot))
            {
                bool boardOwnsInput = ControllerMapper.KeyMapsMoveModeActive
                    && !string.Equals(slot, "ToggleKeyMapsMoveMode", StringComparison.Ordinal);
                if (!boardOwnsInput)
                {
                    ActionRequested?.Invoke(slot);
                }
            }
            previous = held;
        }

        private void LogMapChange(int mapIndex)
        {
            if (mapIndex == _lastLoggedMapIndex)
            {
                return;
            }
            _lastLoggedMapIndex = mapIndex;
            DateTime now = DateTime.UtcNow;
            if ((now - _lastMapLogTime).TotalSeconds < 1.0)
            {
                // Rate limit: bursts of R2-toggling (e.g. wobble on the trigger)
                // never flood the log; the NEXT change still logs its new state.
                return;
            }
            _lastMapLogTime = now;
            List<KeyMapDefinition> maps = AppSettings.Instance.KeyMaps.Maps;
            string mapName = mapIndex >= 0 && mapIndex < maps.Count ? maps[mapIndex].Name : mapIndex.ToString();
            App.Log("key maps mode: map -> " + mapName);
        }
    }
}