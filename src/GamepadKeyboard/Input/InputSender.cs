using System;
using System.Collections.Generic;
using GamepadKeyboard.Native;
using GamepadKeyboard.Settings;

namespace GamepadKeyboard.Input
{
    /// <summary>
    /// Sends global keyboard and mouse input via user32 SendInput.
    /// All methods are safe to call from any thread; SendInput is atomic per call.
    /// </summary>
    public sealed class InputSender
    {
        public void TapKey(ushort vk, bool extended = false)
        {
            Span<NativeMethods.INPUT> inputs = stackalloc NativeMethods.INPUT[2];
            inputs[0] = KeyInput(vk, true, extended);
            inputs[1] = KeyInput(vk, false, extended);
            Dispatch(inputs);
        }

        public void KeyDown(ushort vk, bool extended = false)
        {
            Span<NativeMethods.INPUT> inputs = stackalloc NativeMethods.INPUT[1];
            inputs[0] = KeyInput(vk, true, extended);
            Dispatch(inputs);
            if (keyRepeatEnabled && vk != Vk.None && IsRepeatableKey(vk))
            {
                _repeatHeldEntries[vk] = _repeatClock.Elapsed.TotalSeconds
                    + KeyboardRepeatDelaySeconds;
                _repeatExtendedStates[vk] = extended;
            }
        }

        public void KeyUp(ushort vk, bool extended = false)
        {
            Span<NativeMethods.INPUT> inputs = stackalloc NativeMethods.INPUT[1];
            inputs[0] = KeyInput(vk, false, extended);
            Dispatch(inputs);
            _repeatHeldEntries.Remove(vk);
            _repeatExtendedStates.Remove(vk);
        }

        public void TypeText(string text)
        {
            foreach (char c in text)
            {
                NativeMethods.INPUT[] inputs = new NativeMethods.INPUT[2];
                inputs[0] = UnicodeInput(c, true);
                inputs[1] = UnicodeInput(c, false);
                Dispatch(inputs);
            }
        }

        public void MouseMove(int dx, int dy)
        {
            // Default: plain relative move — the classic behavior (pointer
            // acceleration applies, deltas accumulate normally).
            if (!Settings.AppSettings.Instance.UseAbsoluteMouse)
            {
                Span<NativeMethods.INPUT> relativeInputs = stackalloc NativeMethods.INPUT[1];
                relativeInputs[0] = new NativeMethods.INPUT
                {
                    type = NativeMethods.INPUT_MOUSE,
                    U = new NativeMethods.InputUnion
                    {
                        mi = new NativeMethods.MOUSEINPUT { dx = dx, dy = dy, dwFlags = NativeMethods.MOUSEEVENTF_MOVE }
                    }
                };
                Dispatch(relativeInputs);
                return;
            }

            // "Use absolute mouse" toggle (remote desktop fix): remote-capture
            // tools (Parsec) track the cursor position reliably from
            // MOUSEEVENTF_ABSOLUTE|VIRTUALDESK events, while plain relative moves
            // can leave the streamed cursor stuck at its last drawn spot. The
            // RELATIVE move carries the per-tick motion exactly like a physical
            // mouse (clean deltas — avoids the teleport smoothing some remote
            // clients apply to absolute-only streams), and the absolute event
            // re-anchors the exact intended position.
            if (NativeMethods.TryGetCursorPos(out NativeMethods.POINT point))
            {
                (int virtualX, int virtualY, int virtualWidth, int virtualHeight) =
                    NativeMethods.VirtualDesktopBounds();
                if (virtualWidth > 0 && virtualHeight > 0)
                {
                    long absoluteX = ((long)(point.X + dx - virtualX) << 16) / virtualWidth;
                    long absoluteY = ((long)(point.Y + dy - virtualY) << 16) / virtualHeight;
                    absoluteX = Math.Clamp(absoluteX, 0, 65535);
                    absoluteY = Math.Clamp(absoluteY, 0, 65535);
                    Span<NativeMethods.INPUT> inputs = stackalloc NativeMethods.INPUT[2];
                    inputs[0] = new NativeMethods.INPUT
                    {
                        type = NativeMethods.INPUT_MOUSE,
                        U = new NativeMethods.InputUnion
                        {
                            mi = new NativeMethods.MOUSEINPUT
                            {
                                dx = dx,
                                dy = dy,
                                dwFlags = NativeMethods.MOUSEEVENTF_MOVE,
                            }
                        }
                    };
                    inputs[1] = new NativeMethods.INPUT
                    {
                        type = NativeMethods.INPUT_MOUSE,
                        U = new NativeMethods.InputUnion
                        {
                            mi = new NativeMethods.MOUSEINPUT
                            {
                                dx = (int)absoluteX,
                                dy = (int)absoluteY,
                                dwFlags = NativeMethods.MOUSEEVENTF_MOVE
                                    | NativeMethods.MOUSEEVENTF_ABSOLUTE
                                    | NativeMethods.MOUSEEVENTF_VIRTUALDESK,
                            }
                        }
                    };
                    Dispatch(inputs);
                    return;
                }
            }
            // No cursor position (GetCursorPos failed / empty virtual desktop):
            // fall back to the plain relative move.
            Span<NativeMethods.INPUT> fallbackInputs = stackalloc NativeMethods.INPUT[1];
            fallbackInputs[0] = new NativeMethods.INPUT
            {
                type = NativeMethods.INPUT_MOUSE,
                U = new NativeMethods.InputUnion
                {
                    mi = new NativeMethods.MOUSEINPUT { dx = dx, dy = dy, dwFlags = NativeMethods.MOUSEEVENTF_MOVE }
                }
            };
            Dispatch(fallbackInputs);
        }

        public void MouseButton(uint downFlag, uint upFlag, uint mouseData = 0)
        {
            NativeMethods.INPUT[] inputs = new NativeMethods.INPUT[2];
            inputs[0] = MouseInput(downFlag, mouseData);
            inputs[1] = MouseInput(upFlag, mouseData);
            Dispatch(inputs);
        }

        public void MouseButtonPress(uint downFlag, uint mouseData = 0)
        {
            Dispatch(new[] { MouseInput(downFlag, mouseData) });
        }

        public void MouseButtonRelease(uint upFlag, uint mouseData = 0)
        {
            Dispatch(new[] { MouseInput(upFlag, mouseData) });
        }

        private static NativeMethods.INPUT MouseInput(uint flags, uint mouseData) => new()
        {
            type = NativeMethods.INPUT_MOUSE,
            U = new NativeMethods.InputUnion
            {
                mi = new NativeMethods.MOUSEINPUT { mouseData = mouseData, dwFlags = flags }
            }
        };

        public void MouseWheel(int delta) => Wheel(delta, NativeMethods.MOUSEEVENTF_WHEEL);

        public void MouseHWheel(int delta) => Wheel(delta, NativeMethods.MOUSEEVENTF_HWHEEL);

        private void Wheel(int delta, uint flag)
        {
            Span<NativeMethods.INPUT> inputs = stackalloc NativeMethods.INPUT[1];
            inputs[0] = new NativeMethods.INPUT
            {
                type = NativeMethods.INPUT_MOUSE,
                U = new NativeMethods.InputUnion
                {
                    mi = new NativeMethods.MOUSEINPUT { mouseData = unchecked((uint)delta), dwFlags = flag }
                }
            };
            Dispatch(inputs);
        }

        // ── Simulated key repeat ("Simulate key repeat" Keyboard-tab toggle) ────
        //
        // While the toggle is on, every held repeatable key sends fresh
        // KeyDown events on the OS repeat schedule (initial delay then
        // period, both read once from the Control Panel keyboard settings) —
        // the behavior of a physically held keyboard key. Modifier and toggle
        // keys (Shift/Ctrl/Alt/Win/Caps/Num/Scroll) never repeat, exactly like
        // real hardware. The pump is called from the keyboard and Key Maps UI
        // ticks; mode switches clear the registry so nothing repeats stray.

        private static readonly System.Diagnostics.Stopwatch _repeatClock =
            System.Diagnostics.Stopwatch.StartNew();
        private static readonly Dictionary<ushort, double> _repeatHeldEntries = new();
        private static readonly Dictionary<ushort, bool> _repeatExtendedStates = new();
        private static bool _repeatTimingResolved;
        private static double _repeatDelaySeconds = 0.5;
        private static double _repeatPeriodSeconds = 0.033;

        private static double KeyboardRepeatDelaySeconds
        {
            get
            {
                if (!_repeatTimingResolved)
                {
                    (int delayMs, int periodMs) = NativeMethods.KeyboardRepeatTiming();
                    _repeatDelaySeconds = delayMs / 1000.0;
                    _repeatPeriodSeconds = periodMs / 1000.0;
                    _repeatTimingResolved = true;
                }
                return _repeatDelaySeconds;
            }
        }

        private static bool keyRepeatEnabled => Settings.AppSettings.Instance.SimulateKeyRepeat;

        private static bool IsRepeatableKey(ushort vk) => vk is not
            (Vk.LShift or Vk.RShift or Vk.LControl or Vk.RControl
            or Vk.LMenu or Vk.RMenu or Vk.LWin or Vk.RWin
            or Vk.Capital or Vk.NumLock or Vk.Scroll);

        /// <summary>Sends repeat key-downs for every held repeatable key whose
        /// timer expired; called once per UI tick while the toggle is on.</summary>
        public void PumpKeyRepeats()
        {
            if (!keyRepeatEnabled || _repeatHeldEntries.Count == 0)
            {
                return;
            }
            double now = _repeatClock.Elapsed.TotalSeconds;
            double periodSeconds = 0.033;
            if (!_repeatTimingResolved)
            {
                (int delayMs, int periodMs) = NativeMethods.KeyboardRepeatTiming();
                _repeatDelaySeconds = delayMs / 1000.0;
                _repeatPeriodSeconds = periodMs / 1000.0;
                _repeatTimingResolved = true;
            }
            periodSeconds = _repeatPeriodSeconds;
            List<ushort> dueKeys = null;
            foreach (KeyValuePair<ushort, double> entry in _repeatHeldEntries)
            {
                if (entry.Value <= now)
                {
                    (dueKeys ??= new List<ushort>()).Add(entry.Key);
                }
            }
            if (dueKeys == null)
            {
                return;
            }
            foreach (ushort vk in dueKeys)
            {
                _repeatHeldEntries[vk] = now + periodSeconds;
                Span<NativeMethods.INPUT> inputs = stackalloc NativeMethods.INPUT[1];
                inputs[0] = KeyInput(vk, true, _repeatExtendedStates.TryGetValue(vk, out bool extended) && extended);
                Dispatch(inputs);
            }
        }

        /// <summary>Forgets every held key (mode switches / releases); nothing
        /// repeats after the switch.</summary>
        public void ClearKeyRepeats()
        {
            _repeatHeldEntries.Clear();
            _repeatExtendedStates.Clear();
        }

        private static NativeMethods.INPUT KeyInput(ushort vk, bool down, bool extended = false)
        {
            uint flags = down ? 0u : NativeMethods.KEYEVENTF_KEYUP;
            if (extended) flags |= NativeMethods.KEYEVENTF_EXTENDEDKEY;
            return new NativeMethods.INPUT
            {
                type = NativeMethods.INPUT_KEYBOARD,
                U = new NativeMethods.InputUnion
                {
                    ki = new NativeMethods.KEYBDINPUT { wVk = vk, dwFlags = flags }
                }
            };
        }

        private static NativeMethods.INPUT UnicodeInput(char c, bool down)
        {
            uint flags = NativeMethods.KEYEVENTF_UNICODE;
            if (!down) flags |= NativeMethods.KEYEVENTF_KEYUP;
            return new NativeMethods.INPUT
            {
                type = NativeMethods.INPUT_KEYBOARD,
                U = new NativeMethods.InputUnion
                {
                    ki = new NativeMethods.KEYBDINPUT { wScan = c, dwFlags = flags }
                }
            };
        }

        private static bool _lastFailed;

        private static unsafe void Dispatch(Span<NativeMethods.INPUT> inputs)
        {
            // SendInput wants a pointer; fixed passes the stack buffer straight
            // through — the previous ToArray() allocated on every key/mouse
            // event at up to poll frequency.
            fixed (NativeMethods.INPUT* p = inputs)
            {
                uint sent = NativeMethods.SendInputUnsafe((uint)inputs.Length, p, NativeMethods.INPUT.Size);
                if (sent == inputs.Length)
                {
                    _lastFailed = false;
                }
                else if (!_lastFailed)
                {
                    _lastFailed = true;
                    App.Log("SendInput failed: sent " + sent + "/" + inputs.Length +
                            " err=" + System.Runtime.InteropServices.Marshal.GetLastWin32Error());
                }
            }
        }
    }
}
