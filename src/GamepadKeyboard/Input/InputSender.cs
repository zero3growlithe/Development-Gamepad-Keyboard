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
            Span<NativeMethods.INPUT> inputs = stackalloc NativeMethods.INPUT[1];
            inputs[0] = KeyInput(vk, true, extended);
            Dispatch(inputs);
        }

        public void TapKey(ushort vk, ushort modifier)
        {
            Span<NativeMethods.INPUT> inputs = stackalloc NativeMethods.INPUT[3];
            inputs[0] = KeyInput(modifier, true);
            inputs[1] = KeyInput(vk, true);
            inputs[2] = KeyInput(vk, false);
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
            Span<NativeMethods.INPUT> inputs = stackalloc NativeMethods.INPUT[1];
            inputs[0] = new NativeMethods.INPUT
            {
                type = NativeMethods.INPUT_MOUSE,
                U = new NativeMethods.InputUnion
                {
                    mi = new NativeMethods.MOUSEINPUT { dx = dx, dy = dy, dwFlags = NativeMethods.MOUSEEVENTF_MOVE }
                }
            };
            Dispatch(inputs);
        }

        public void MouseButton(uint downFlag, uint upFlag, uint mouseData = 0)
        {
            var inputs = new NativeMethods.INPUT[2];
            inputs[0] = MouseInput(downFlag, mouseData);
            inputs[1] = MouseInput(upFlag, mouseData);
            Dispatch(inputs);
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

        private static void Dispatch(Span<NativeMethods.INPUT> inputs)
        {
            var arr = inputs.ToArray();
            uint sent = NativeMethods.SendInput((uint)arr.Length, arr, NativeMethods.INPUT.Size);
            if (sent == arr.Length)
            {
                _lastFailed = false;
            }
            else if (!_lastFailed)
            {
                _lastFailed = true;
                App.Log("SendInput failed: sent " + sent + "/" + arr.Length +
                        " err=" + System.Runtime.InteropServices.Marshal.GetLastWin32Error());
            }
        }
    }
}