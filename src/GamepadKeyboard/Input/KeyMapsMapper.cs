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
    /// edge-triggered tap of its mapped key, so a held stick never repeats:
    /// typing is discrete by construction, and the physical chord
    /// (LB/RB/LT/R2) is what makes it usable at 250 Hz.
    ///
    /// Modifier state is driven by real KeyDown/KeyUp pairs, so a held modifier
    /// naturally combines with every tapped map key (same semantics as
    /// keyboard-mode Hold*). Punctuation slots are typed through
    /// KEYEVENTF_UNICODE so the exact character lands even with Ctrl or Alt
    /// held: a VK tap of "+" emits "=" on a US layout, and real modifiers would
    /// further rewrite it.
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
        public bool CtrlHeld { get; private set; }
        public bool ShiftHeld { get; private set; }
        public bool AltHeld { get; private set; }

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
        private bool _seeded;
        private bool _previousShift;
        private bool _previousCtrl;
        private bool _previousAlt;
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
                if (!_seeded)
                {
                    // First tick after entering the mode or after ReleaseAll:
                    // latch current physical heldness so buttons held across the
                    // transition cannot fire phantom taps; still-held modifiers
                    // re-assert their KeyDown below (ReleaseAll leaves their
                    // previous state false, so an edge asserts again).
                    _seeded = true;
                    ProcessModifiers(snapshot);
                    SeedSlotEdges(snapshot);
                    return;
                }
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
        /// mouse button, KeyUp for Shift/Ctrl/Alt, then re-seeds edges. Windows
        /// key is a TAP (no held state); CapsLock is a toggle key the OS
        /// remembers by design — the spec's toggle behavior.
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
            if (ShiftHeld) _sender.KeyUp(Vk.LShift);
            if (CtrlHeld) _sender.KeyUp(Vk.LControl);
            if (AltHeld) _sender.KeyUp(Vk.LMenu);
            ShiftHeld = CtrlHeld = AltHeld = false;
            _previousShift = false;
            _previousCtrl = false;
            _previousAlt = false;
            MapsKeyHeld = false;
            ActiveMapIndex = 0;
            _lastLoggedMapIndex = 0;
        }

        // ── One poll tick ──────────────────────────────────────────────────────

        private void ProcessTick(in GamepadSnapshot snapshot)
        {
            ProcessModifiers(snapshot);

            bool mapsKey = snapshot.RightTrigger >= 0.5;
            int mapIndex = mapsKey
                ? snapshot.LB && snapshot.RB ? 4
                : snapshot.LB ? 3
                : snapshot.RB ? 2
                : 1
                : 0;
            MapsKeyHeld = mapsKey;
            ActiveMapIndex = mapIndex;
            LogMapChange(mapIndex);

            List<KeyMapDefinition> maps = AppSettings.Instance.KeyMaps.Maps;
            KeyMapDefinition map = maps[Math.Clamp(mapIndex, 0, maps.Count - 1)];
            double threshold = Math.Clamp(AppSettings.Instance.KeyMaps.StickTapThreshold, 0.05, 1.0);

            // Modifier-selection buttons (LB/RB) MUST NOT be consumed as map
            // slots anywhere — they are the chord for Symbols 2/3/FunctionKeys.
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
            TapSlot(ref _previousRightStickPress, snapshot.RS, map.RightStickPress);
            TapSlot(ref _previousSelect, snapshot.View, map.Select);

            // Start LAST: on the Utility map it requests the MouseMode switch,
            // which ends this mode for the rest of the tick.
            DispatchStartSlot(ref _previousStart, snapshot.Menu, map.Start);
        }

        private void ProcessModifiers(in GamepadSnapshot snapshot)
        {
            bool shift = snapshot.LeftTrigger >= 0.5;
            if (shift != _previousShift)
            {
                if (shift) _sender.KeyDown(Vk.LShift); else _sender.KeyUp(Vk.LShift);
                ShiftHeld = shift;
                _previousShift = shift;
            }

            bool ctrl = snapshot.LB;
            if (ctrl != _previousCtrl)
            {
                if (ctrl) _sender.KeyDown(Vk.LControl); else _sender.KeyUp(Vk.LControl);
                CtrlHeld = ctrl;
                _previousCtrl = ctrl;
            }

            bool alt = snapshot.RB;
            if (alt != _previousAlt)
            {
                if (alt) _sender.KeyDown(Vk.LMenu); else _sender.KeyUp(Vk.LMenu);
                AltHeld = alt;
                _previousAlt = alt;
            }
        }

        private void TapSlot(ref bool previous, bool held, string slot)
        {
            if (held && !previous)
            {
                SendSlotTap(slot);
            }
            previous = held;
        }

        private void DispatchStartSlot(ref bool previous, bool held, string slot)
        {
            if (held && !previous && ControllerMapper.IsAppLevelAction(slot))
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

        private void SeedSlotEdges(in GamepadSnapshot snapshot)
        {
            double threshold = Math.Clamp(AppSettings.Instance.KeyMaps.StickTapThreshold, 0.05, 1.0);
            _previousDPadUp = snapshot.DUp;
            _previousDPadDown = snapshot.DDown;
            _previousDPadLeft = snapshot.DLeft;
            _previousDPadRight = snapshot.DRight;
            _previousFaceY = snapshot.Y;
            _previousFaceA = snapshot.A;
            _previousFaceX = snapshot.X;
            _previousFaceB = snapshot.B;
            _previousLeftStickUp = snapshot.LY >= threshold;
            _previousLeftStickDown = snapshot.LY <= -threshold;
            _previousLeftStickLeft = snapshot.LX <= -threshold;
            _previousLeftStickRight = snapshot.LX >= threshold;
            _previousRightStickUp = snapshot.RY >= threshold;
            _previousRightStickDown = snapshot.RY <= -threshold;
            _previousRightStickLeft = snapshot.RX <= -threshold;
            _previousRightStickRight = snapshot.RX >= threshold;
            _previousLeftStickPress = snapshot.LS;
            _previousRightStickPress = snapshot.RS;
            _previousSelect = snapshot.View;
            _previousStart = snapshot.Menu;
        }
    }
}