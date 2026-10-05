using System;
using System.Collections.Generic;
using GamepadKeyboard.Native;
using GamepadKeyboard.Settings;

namespace GamepadKeyboard.Input
{
    /// <summary>
    /// Key Maps mode: the gamepad as a chord keyboard via maps of key
    /// assignments. L2 holds Shift, L1 holds Ctrl and R1 holds Alt; holding R2
    /// (the maps key) selects the map while it is held — alone to Symbols 1,
    /// with R1 to Symbols 2, with L1 to Symbols 3, with L1+R1 to Function Keys —
    /// and releasing R2 returns to the Utility map. Every other physical slot
    /// (d-pad, face, stick deflections, stick presses, Select/Start) sends one
    /// edge-triggered tap of its mapped key, so a held stick never repeats.
    ///
    /// Modifier gating: while the maps key is held, L2/L1/R1 are map-chord
    /// buttons — their Shift/Ctrl/Alt keys are frozen (no taps, no toggles).
    /// A modifier physically held BEFORE the maps key went down stays latched
    /// for the whole hold (re-pressing that button unlocks + releases it);
    /// anything else does nothing until the maps key is released. The
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

        /// <summary>Physical chord state while the maps key is held — the
        /// overlay brightens the RIGHT strip for Symbols 2 (R2+R1), the LEFT
        /// strip for Symbols 3 (R2+L1) and the BOTTOM strip for Function Keys
        /// (R2+L1+R1).</summary>
        public bool Sym2ComboHeld { get; private set; }
        public bool Sym3ComboHeld { get; private set; }
        public bool FunctionComboHeld { get; private set; }

        /// <summary>Effective (latched OR physically held) modifier states —
        /// what the overlay's [Ctrl][Shift][Alt][Windows] chips highlight.</summary>
        public bool CtrlHeld => _ctrlHeld;
        public bool ShiftHeld => _shiftHeld;
        public bool AltHeld => _altHeld;
        public bool WindowsHeld => _windowsHeld;

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
        private bool _previousMapsKey;
        // Modifier state as of the maps-key press edge (frozen while held).
        private bool _wasShiftHeldBeforeMaps;
        private bool _wasCtrlHeldBeforeMaps;
        private bool _wasAltHeldBeforeMaps;
        private bool _previousShift;
        private bool _previousCtrl;
        private bool _previousAlt;
        private bool _shiftHeld;
        private bool _ctrlHeld;
        private bool _altHeld;
        private bool _windowsHeld;
        private bool _shiftLocked;
        private bool _ctrlLocked;
        private bool _altLocked;
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
                // The seed run IS a full tick — modifiers/windows assert here,
                // but TapSlot suppresses edges (no phantom taps from buttons
                // held across a mode switch or ReleaseAll).
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
            if (_heldXButton1)
            {
                _sender.MouseButtonRelease(NativeMethods.MOUSEEVENTF_XUP, 1u);
                _heldXButton1 = false;
            }
            if (_heldXButton2)
            {
                _sender.MouseButtonRelease(NativeMethods.MOUSEEVENTF_XUP, 2u);
                _heldXButton2 = false;
            }
            if (_shiftHeld) _sender.KeyUp(Vk.LShift);
            if (_ctrlHeld) _sender.KeyUp(Vk.LControl);
            if (_altHeld) _sender.KeyUp(Vk.LMenu);
            if (_windowsHeld && _latchedWindowsVk != Vk.None)
            {
                _sender.KeyUp(_latchedWindowsVk, _latchedWindowsExtended);
            }
            _shiftHeld = _ctrlHeld = _altHeld = _windowsHeld = false;
            _shiftLocked = _ctrlLocked = _altLocked = _windowsLocked = false;
            _latchedWindowsVk = Vk.None;
            _previousShift = _previousCtrl = _previousAlt = false;
            _wasShiftHeldBeforeMaps = _wasCtrlHeldBeforeMaps = _wasAltHeldBeforeMaps = false;
            _previousMapsKey = false;
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
            bool mapsKey = snapshot.RightTrigger >= 0.5;
            bool mapsKeyEdge = mapsKey && !_previousMapsKey;
            if (mapsKeyEdge)
            {
                _wasShiftHeldBeforeMaps = snapshot.LeftTrigger >= 0.5;
                _wasCtrlHeldBeforeMaps = snapshot.LB;
                _wasAltHeldBeforeMaps = snapshot.RB;
            }
            _previousMapsKey = mapsKey;
            int mapIndex = mapsKey
                ? snapshot.LB && snapshot.RB ? 4
                : snapshot.LB ? 3
                : snapshot.RB ? 2
                : 1
                : 0;
            MapsKeyHeld = mapsKey;
            ActiveMapIndex = mapIndex;
            Sym2ComboHeld = mapsKey && snapshot.RB;
            Sym3ComboHeld = mapsKey && snapshot.LB;
            FunctionComboHeld = mapsKey && snapshot.LB && snapshot.RB;
            LogMapChange(mapIndex);

            List<KeyMapDefinition> maps = AppSettings.Instance.KeyMaps.Maps;
            KeyMapDefinition map = maps[Math.Clamp(mapIndex, 0, maps.Count - 1)];

            // Modifier-selection buttons (LB/RB) MUST NOT be consumed as map
            // slots anywhere — they are the chord for Symbols 2/3/FunctionKeys.
            // While the maps key is held they latch instead of tapping.
            ProcessModifiers(snapshot, mapsKey, mapsKeyEdge, map);

            double threshold = Math.Clamp(AppSettings.Instance.KeyMaps.StickTapThreshold, 0.05, 1.0);
            TapSlot(ref _previousDPadUp, snapshot.DUp, map.DPadUp);
            TapSlot(ref _previousDPadDown, snapshot.DDown, map.DPadDown);
            TapSlot(ref _previousDPadLeft, snapshot.DLeft, map.DPadLeft);
            TapSlot(ref _previousDPadRight, snapshot.DRight, map.DPadRight);
            TapSlot(ref _previousFaceY, snapshot.Y, map.FaceY);
            TapSlot(ref _previousFaceA, snapshot.A, map.FaceA);
            TapSlot(ref _previousFaceX, snapshot.X, map.FaceX);
            TapSlot(ref _previousFaceB, snapshot.B, map.FaceB);
            TapSlot(ref _previousLeftStickUp, snapshot.LY >= threshold, map.LeftStickUp);
            TapSlot(ref _previousLeftStickDown, snapshot.LY <= -threshold, map.LeftStickDown);
            TapSlot(ref _previousLeftStickLeft, snapshot.LX <= -threshold, map.LeftStickLeft);
            TapSlot(ref _previousLeftStickRight, snapshot.LX >= threshold, map.LeftStickRight);
            TapSlot(ref _previousRightStickUp, snapshot.RY >= threshold, map.RightStickUp);
            TapSlot(ref _previousRightStickDown, snapshot.RY <= -threshold, map.RightStickDown);
            TapSlot(ref _previousRightStickLeft, snapshot.RX <= -threshold, map.RightStickLeft);
            TapSlot(ref _previousRightStickRight, snapshot.RX >= threshold, map.RightStickRight);
            TapSlot(ref _previousLeftStickPress, snapshot.LS, map.LeftStickPress);
            UpdateWindowsHold(snapshot.RS, mapsKey, mapsKeyEdge, map);
            TapSlot(ref _previousSelect, snapshot.View, map.Select);

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

        // ── Modifiers: physical hold + maps-key latch/lock ─────────────────────

        private void ProcessModifiers(
            in GamepadSnapshot snapshot, bool mapsKey, bool mapsKeyEdge, KeyMapDefinition map)
        {
            // physicalWasHeldFirst: the button was already down when the maps
            // key went down — only then its modifier key stays active (latched)
            // while maps is held; otherwise the control stays frozen.
            UpdateLatchedModifier(snapshot.LeftTrigger >= 0.5, mapsKey, mapsKeyEdge, _wasShiftHeldBeforeMaps,
                ref _previousShift, ref _shiftLocked, ref _shiftHeld, Vk.LShift, false);
            UpdateLatchedModifier(snapshot.LB, mapsKey, mapsKeyEdge, _wasCtrlHeldBeforeMaps,
                ref _previousCtrl, ref _ctrlLocked, ref _ctrlHeld, Vk.LControl, false);
            UpdateLatchedModifier(snapshot.RB, mapsKey, mapsKeyEdge, _wasAltHeldBeforeMaps,
                ref _previousAlt, ref _altLocked, ref _altHeld, Vk.LMenu, false);
        }

        /// <summary>
        /// One modifier's full state machine. Outside the maps key: the key
        /// simply follows the physical control (KeyDown/KeyUp on edges). On the
        /// maps-key press edge: whatever is physically held LATCHES down (real
        /// KeyDown already sent, or sent now) and stays down regardless of the
        /// physical control. While the maps key is held: a fresh press of the
        /// physical control toggles the latch (lock, or unlock+release) so the
        /// user is never stuck with a stuck modifier.
        /// </summary>
        private void UpdateLatchedModifier(
            bool physical, bool mapsKey, bool mapsKeyEdge,
            bool physicalWasHeldFirst,
            ref bool previousPhysical, ref bool locked, ref bool held,
            ushort virtualKey, bool extended)
        {
            if (mapsKey)
            {
                // While the maps key is held the control keys are frozen: the
                // button is the map-selection chord, so it must NOT tap or
                // toggle its Ctrl/Shift/Alt key. Only a modifier that was
                // already held BEFORE the maps key went down keeps its state
                // (latched); it can also be unlocked by re-pressing it.
                if (mapsKeyEdge)
                {
                    if (physicalWasHeldFirst && !held)
                    {
                        _sender.KeyDown(virtualKey, extended);
                        held = true;
                    }
                    locked = physicalWasHeldFirst;
                    previousPhysical = physical;
                    return;
                }
                if (physical && !previousPhysical && locked)
                {
                    // Fresh press of a latched modifier unlocks + releases it
                    // so the user is never stuck with a stuck modifier.
                    locked = false;
                    if (held)
                    {
                        _sender.KeyUp(virtualKey, extended);
                        held = false;
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

        /// <summary>
        /// The right-stick PRESS is a HOLD slot (default: Windows key) — key
        /// goes down with the stick press and up with its release. The mapped
        /// key participates in the maps-key latch like the modifiers do.
        /// </summary>
        private void UpdateWindowsHold(
            bool physical, bool mapsKey, bool mapsKeyEdge, KeyMapDefinition map)
        {
            string slot = map.RightStickPress;
            if (ControllerMapper.IsAppLevelAction(slot))
            {
                if (physical && !_previousRightStickPress && !_seedRun)
                {
                    ActionRequested?.Invoke(slot);
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
            bool extended = ControllerMapper.IsExtendedKey(virtualKey);
            UpdateLatchedModifierLegacy(physical, mapsKey, mapsKeyEdge,
                ref _previousRightStickPress, ref _windowsLocked, ref _windowsHeld,
                virtualKey, extended);
            _latchedWindowsVk = _windowsHeld ? virtualKey : Vk.None;
            _latchedWindowsExtended = extended;
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

        private void TapSlot(ref bool previous, bool held, string slot)
        {
            if (held && !previous && !_seedRun)
            {
                SendSlotTap(slot);
            }
            previous = held;
        }

        private void DispatchStartSlot(ref bool previous, bool held, string slot)
        {
            if (held && !previous && !_seedRun && ControllerMapper.IsAppLevelAction(slot))
            {
                ActionRequested?.Invoke(slot);
            }
            previous = held;
        }

        private void SendSlotTap(string slot)
        {
            if (string.IsNullOrWhiteSpace(slot)
                || string.Equals(slot, "None", StringComparison.OrdinalIgnoreCase))
            {
                return;
            }
            if (ControllerMapper.IsAppLevelAction(slot))
            {
                ActionRequested?.Invoke(slot);
                return;
            }
            ushort virtualKey = ControllerMapper.NamedVk(slot);
            if (virtualKey != Vk.None)
            {
                _sender.TapKey(virtualKey, ControllerMapper.IsExtendedKey(virtualKey));
                return;
            }
            if (slot.Length == 1)
            {
                // Punctuation slot (e.g. "+") on a US layout: a VK tap sends
                // "=" instead, and a held Ctrl/Alt further rewrites the glyph.
                // Typed through Unicode events for the exact character. Taps
                // are edge-triggered, so this path never runs per tick.
                _sender.TypeText(slot);
            }
            // Names that resolve to no key are ignored silently.
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