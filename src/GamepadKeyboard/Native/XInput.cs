using System;
using System.Runtime.InteropServices;

namespace GamepadKeyboard.Native
{
    /// <summary>
    /// XInput polling (xinput1_4.dll with xinput9_1_0.dll fallback). Fallback input
    /// path when Windows.Gaming.Input cannot read the pad (HidHide hiding the real
    /// device, DS4Windows / Steam Input virtual Xbox pads, etc.).
    /// </summary>
    public static class XInput
    {
        [StructLayout(LayoutKind.Sequential)]
        public struct XINPUT_STATE
        {
            public uint dwPacketNumber;
            public XINPUT_GAMEPAD Game;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct XINPUT_GAMEPAD
        {
            public ushort wButtons;
            public byte bLeftTrigger;
            public byte bRightTrigger;
            public short sThumbLX;
            public short sThumbLY;
            public short sThumbRX;
            public short sThumbRY;
        }

        public const ushort DPAD_UP = 0x0001;
        public const ushort DPAD_DOWN = 0x0002;
        public const ushort DPAD_LEFT = 0x0004;
        public const ushort DPAD_RIGHT = 0x0008;
        public const ushort START = 0x0010;          // Menu
        public const ushort BACK = 0x0020;           // View / Select / Share
        public const ushort LEFT_THUMB = 0x0040;     // L3
        public const ushort RIGHT_THUMB = 0x0080;    // R3
        public const ushort LEFT_SHOULDER = 0x0100;  // L1
        public const ushort RIGHT_SHOULDER = 0x0200; // R1
        public const ushort BTN_A = 0x1000;
        public const ushort BTN_B = 0x2000;
        public const ushort BTN_X = 0x4000;
        public const ushort BTN_Y = 0x8000;

        private static bool _tried14, _ok14, _tried910, _ok910;

        [DllImport("xinput1_4.dll")]
        private static extern int XInputGetState14(int dwUserIndex, ref XINPUT_STATE pState);

        [DllImport("xinput9_1_0.dll")]
        private static extern int XInputGetState910(int dwUserIndex, ref XINPUT_STATE pState);

        private static void Probe()
        {
            if (!_tried14)
            {
                _tried14 = true;
                try { var s = new XINPUT_STATE(); XInputGetState14(0, ref s); _ok14 = true; }
                catch (DllNotFoundException) { }
                catch (EntryPointNotFoundException) { }
                catch (BadImageFormatException) { }
            }
            if (!_ok14 && !_tried910)
            {
                _tried910 = true;
                try { var s = new XINPUT_STATE(); XInputGetState910(0, ref s); _ok910 = true; }
                catch { }
            }
        }

        public static bool Available
        {
            get
            {
                Probe();
                return _ok14 || _ok910;
            }
        }

        /// <summary>0 = OK, nonzero = error / device not connected.</summary>
        public static int GetState(int index, ref XINPUT_STATE state)
        {
            Probe();
            if (_ok14) return XInputGetState14(index, ref state);
            if (_ok910) return XInputGetState910(index, ref state);
            return unchecked((int)0x8007048F); // device not connected
        }
    }
}