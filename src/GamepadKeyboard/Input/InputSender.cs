using System;
using System.Collections.Generic;
using GamepadKeyboard.Native;

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
        }

        public void KeyUp(ushort vk, bool extended = false)
        {
            Span<NativeMethods.INPUT> inputs = stackalloc NativeMethods.INPUT[1];
            inputs[0] = KeyInput(vk, false, extended);
            Dispatch(inputs);
        }

        public void TypeText(string text)
        {
            foreach (char c in text)
            {
                var inputs = new NativeMethods.INPUT[2];
                inputs[0] = UnicodeInput(c, true);
                inputs[1] = UnicodeInput(c, false);
                Dispatch(inputs);
            }
        }

        public void MouseMove(int dx, int dy)
        {
            // Absolute injection: remote-capture tools (Parsec) track the cursor
            // position reliably from MOUSEEVENTF_ABSOLUTE|VIRTUALDESK events, while
            // plain relative moves can leave the streamed cursor stuck at its last
            // drawn spot (the local cursor shape still changes, so apps move fine —
            // only the capture misses it). Resolved per call from the real cursor
            // position; GetCursorPos failure falls back to a relative move.
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
                    Span<NativeMethods.INPUT> inputs = stackalloc NativeMethods.INPUT[1];
                    inputs[0] = new NativeMethods.INPUT
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
        }

        public void MouseButton(uint downFlag, uint upFlag, uint mouseData = 0)
        {
            var inputs = new NativeMethods.INPUT[2];
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
