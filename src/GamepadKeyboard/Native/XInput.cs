using System;
using System.Runtime.InteropServices;

namespace GamepadKeyboard.Native
{
    /// <summary>
    /// XInput polling across the Windows 8+, legacy redistributable, and system
    /// compatibility runtimes. Some virtual-pad stacks expose different state
    /// through these runtimes as foreground ownership changes, so a disconnected
    /// response from one runtime must not suppress the others. This is the
    /// fallback when Windows.Gaming.Input cannot read a hidden or virtual pad.
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

        private static volatile bool _probed;
        private static bool _ok14, _ok13, _ok910;
        private static readonly object ProbeLock = new();

        [DllImport("xinput1_4.dll")]
        private static extern int XInputGetState14(int dwUserIndex, ref XINPUT_STATE pState);

        [DllImport("xinput1_3.dll")]
        private static extern int XInputGetState13(int dwUserIndex, ref XINPUT_STATE pState);

        [DllImport("xinput9_1_0.dll")]
        private static extern int XInputGetState910(int dwUserIndex, ref XINPUT_STATE pState);

        private static readonly string[] ProbeErrors = new string[3];

        private static void Probe()
        {
            if (_probed) return;
            lock (ProbeLock)
            {
                if (_probed) return;
                _ok14 = RuntimeAvailable("xinput1_4.dll", XInputGetState14, 0);
                _ok13 = RuntimeAvailable("xinput1_3.dll", XInputGetState13, 1);
                _ok910 = RuntimeAvailable("xinput9_1_0.dll", XInputGetState910, 2);
                _probed = true;
            }
        }

        private delegate int GetStateDelegate(int index, ref XINPUT_STATE state);

        private static bool RuntimeAvailable(string dll, GetStateDelegate getState, int slot)
        {
            try
            {
                var state = new XINPUT_STATE();
                getState(0, ref state);
                return true;
            }
            catch (Exception ex)
            {
                // record WHY the DLL could not be used — "file missing" vs "load
                // blocked" are different problems with different fixes
                ProbeErrors[slot] = dll + ": " + ex.GetType().Name + " (" + ex.Message + ")";
                return false;
            }
        }

        /// <summary>One-time per-DLL probe outcomes for diagnostics (null until probed).</summary>
        public static string?[] DescribeProbe()
        {
            Probe();
            var lines = new string?[4];
            lines[0] = _ok14 ? "xinput1_4.dll: ok" : ProbeErrors[0] ?? "xinput1_4.dll: failed (no detail)";
            lines[1] = _ok13 ? "xinput1_3.dll: ok" : ProbeErrors[1] ?? "xinput1_3.dll: failed (no detail)";
            lines[2] = _ok910 ? "xinput9_1_0.dll: ok" : ProbeErrors[2] ?? "xinput9_1_0.dll: failed (no detail)";
            lines[3] = "sys32 files present: xinput1_4=" +
                System.IO.File.Exists(System.IO.Path.Combine(System.Environment.SystemDirectory, "xinput1_4.dll")) +
                " xinput1_3=" +
                System.IO.File.Exists(System.IO.Path.Combine(System.Environment.SystemDirectory, "xinput1_3.dll")) +
                " xinput9_1_0=" +
                System.IO.File.Exists(System.IO.Path.Combine(System.Environment.SystemDirectory, "xinput9_1_0.dll"));
            return lines;
        }

        public static bool Available
        {
            get
            {
                Probe();
                return _ok14 || _ok13 || _ok910;
            }
        }

        /// <summary>0 = OK, nonzero = error / device not connected.</summary>
        public static int GetState(int index, ref XINPUT_STATE state)
        {
            Probe();
            int error = unchecked((int)0x8007048F); // device not connected
            if (_ok14)
            {
                error = XInputGetState14(index, ref state);
                if (error == 0) return 0;
            }
            if (_ok13)
            {
                error = XInputGetState13(index, ref state);
                if (error == 0) return 0;
            }
            if (_ok910)
            {
                error = XInputGetState910(index, ref state);
                if (error == 0) return 0;
            }
            return error;
        }
    }
}
