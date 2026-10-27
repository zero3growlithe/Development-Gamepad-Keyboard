using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;

namespace GamepadKeyboard.Native
{
    /// <summary>
    /// Legacy joystick layer (winmm joyGetDevCaps/joyGetPosEx) — the same stack the
    /// Windows "Set up USB game controllers" panel (joy.cpl) and DirectInput read.
    /// It keeps seeing HID pads when Windows.Gaming.Input enumerates NOTHING:
    /// on Windows 11 24H2+ Windows.Gaming.Input is served by the GameInput service,
    /// so a stopped/disabled GameInputSvc leaves RawGameControllers empty (and kills
    /// gamepad navigation in the shell) while HID/DirectInput still work — the
    /// observed field case: WGI "controllers changed: 0", joy.cpl + Parsec fine.
    /// Used strictly as the LAST source, after Windows.Gaming.Input and XInput.
    /// </summary>
    public static class LegacyJoystick
    {
        public const int JOYERR_NOERROR = 0;
        public const int JOYERR_UNPLUGGED = 167;

        // JOYCAPS flags (mmsystem.h)
        private const uint JOYCAPS_HASZ = 0x0001;
        private const uint JOYCAPS_HASR = 0x0002;
        private const uint JOYCAPS_HASU = 0x0004;
        private const uint JOYCAPS_HASV = 0x0008;
        private const uint JOYCAPS_HASPOV = 0x0010;

        // JOYINFOEX request flags (joystickapi.h) — everything in one read
        private const uint JOY_RETURNALL = 0x000000FF;

        private const uint JOY_POVCENTERED = 0xFFFF;

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        internal struct JOYCAPSW
        {
            public ushort wMid;
            public ushort wPid;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)]
            public string szPname;
            public uint wXmin;
            public uint wXmax;
            public uint wYmin;
            public uint wYmax;
            public uint wZmin;
            public uint wZmax;
            public uint wNumButtons;
            public uint wPeriodMin;
            public uint wPeriodMax;
            public uint wRmin;
            public uint wRmax;
            public uint wUmin;
            public uint wUmax;
            public uint wVmin;
            public uint wVmax;
            public uint wCaps;
            public uint wMaxAxes;
            public uint wNumAxes;
            public uint wMaxButtons;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)]
            public string szRegKey;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)]
            public string szOEMVxD;
        }

        [StructLayout(LayoutKind.Sequential)]
        internal struct JOYINFOEX
        {
            public uint dwSize;
            public uint dwFlags;
            public uint dwXpos;
            public uint dwYpos;
            public uint dwZpos;
            public uint dwRpos;
            public uint dwUpos;
            public uint dwVpos;
            public uint dwButtons;
            public uint dwButtonNumber;
            public uint dwPOV;
            public uint dwReserved1;
            public uint dwReserved2;
        }

        // EntryPoint is spelled out on purpose: the managed names differ from the
        // real exports and DllImport binds by the managed name when it is omitted.
        [DllImport("winmm.dll", EntryPoint = "joyGetNumDevs")]
        private static extern uint joyGetNumDevs();

        [DllImport("winmm.dll", EntryPoint = "joyGetDevCapsW", CharSet = CharSet.Unicode)]
        private static extern int joyGetDevCapsW(IntPtr uJoyId, ref JOYCAPSW pjc, uint cbjc);

        [DllImport("winmm.dll", EntryPoint = "joyGetPosEx")]
        private static extern int joyGetPosEx(uint uJoyId, ref JOYINFOEX pji);

        /// <summary>One enumerated legacy joystick plus everything needed to scale readings.</summary>
        public sealed class Device
        {
            internal JOYCAPSW Caps { get; set; }

            public uint Id { get; internal set; }

            public string Name => Caps.szPname;

            public ushort VendorId => Caps.wMid;

            public ushort ProductId => Caps.wPid;

            public uint ButtonCount => Caps.wNumButtons;

            public bool HasZ => (Caps.wCaps & JOYCAPS_HASZ) != 0;

            public bool HasR => (Caps.wCaps & JOYCAPS_HASR) != 0;

            public bool HasU => (Caps.wCaps & JOYCAPS_HASU) != 0;

            public bool HasV => (Caps.wCaps & JOYCAPS_HASV) != 0;

            public bool HasPov => (Caps.wCaps & JOYCAPS_HASPOV) != 0;

            /// <summary>Sony Interactive Entertainment vendor id.</summary>
            public bool IsSony => VendorId == 0x054C;

            /// <summary>
            /// True when this device reports the stick pairs and triggers in the
            /// OPPOSITE roles from the generic HID assumption. Field-verified on a
            /// DualSense through this layer (2026-10-27): X/Y carried the RIGHT
            /// stick, Z/R the LEFT one, and U was R2 while V was L2. Requires all
            /// four stick axes so a two-axis pad still reads its left stick.
            /// </summary>
            public bool SwappedLayout => IsSony && HasZ && HasR;

            /// <summary>One-line capability dump — this is what crash.log needs to
            /// confirm the real axis/button layout of an unknown pad.</summary>
            public string Describe()
            {
                string axes = "X,Y";
                if (HasZ) { axes += ",Z"; }
                if (HasR) { axes += ",R"; }
                if (HasU) { axes += ",U"; }
                if (HasV) { axes += ",V"; }
                return "\"" + Name + "\" vid=0x" + VendorId.ToString("X4") +
                       " pid=0x" + ProductId.ToString("X4") +
                       " buttons=" + ButtonCount +
                       " layout=" + (SwappedLayout ? "sony-swapped" : "standard") +
                       " axes[" + axes + "]" + (HasPov ? "+pov" : "") +
                       " xrange=" + Caps.wXmin + ".." + Caps.wXmax +
                       " yrange=" + Caps.wYmin + ".." + Caps.wYmax;
            }
        }

        /// <summary>Enumerates the legacy joysticks Windows currently reports (empty = layer idle).
        /// Only devices whose position read succeeds are returned, which filters out
        /// ids that the driver stack reserves but no hardware occupies.</summary>
        public static List<Device> Enumerate()
        {
            List<Device> devices = new List<Device>();
            uint capacity = joyGetNumDevs();
            if (capacity > 16)
            {
                capacity = 16;
            }
            for (uint id = 0; id < capacity; id++)
            {
                JOYCAPSW caps = new JOYCAPSW();
                int capsResult = joyGetDevCapsW(new IntPtr((int)id), ref caps, (uint)Marshal.SizeOf<JOYCAPSW>());
                if (capsResult != JOYERR_NOERROR)
                {
                    continue;
                }
                JOYINFOEX probe = new JOYINFOEX();
                probe.dwSize = (uint)Marshal.SizeOf<JOYINFOEX>();
                probe.dwFlags = JOY_RETURNALL;
                int readResult = joyGetPosEx(id, ref probe);
                if (readResult != JOYERR_NOERROR)
                {
                    continue;
                }
                devices.Add(new Device { Id = id, Caps = caps });
            }
            return devices;
        }

        /// <summary>Reads one device; false when it disappeared (unplugged) since the scan.</summary>
        public static bool TryRead(Device? device, out Reading reading)
        {
            reading = default;
            if (device == null)
            {
                return false;
            }
            JOYINFOEX info = new JOYINFOEX();
            info.dwSize = (uint)Marshal.SizeOf<JOYINFOEX>();
            info.dwFlags = JOY_RETURNALL;
            int result = joyGetPosEx(device.Id, ref info);
            if (result != JOYERR_NOERROR)
            {
                return false;
            }
            reading = new Reading(device, info);
            return true;
        }

        /// <summary>One position packet with axes normalized against the device's own caps.</summary>
        public readonly struct Reading
        {
            private readonly Device _device;
            private readonly JOYINFOEX _info;

            internal Reading(Device device, JOYINFOEX info)
            {
                _device = device;
                _info = info;
            }

            public uint Id => _device.Id;

            public string Name => _device.Name;

            public uint Buttons => _info.dwButtons;

            // Raw 16-bit positions, exposed for the diagnostics probes only — the
            // converted properties below are what the app reads.
            public uint RawX => _info.dwXpos;

            public uint RawY => _info.dwYpos;

            public uint RawZ => _info.dwZpos;

            public uint RawR => _info.dwRpos;

            public uint RawU => _info.dwUpos;

            public uint RawV => _info.dwVpos;

            public uint PovHundredths => _info.dwPOV;

            public bool PovCentered => _info.dwPOV == JOY_POVCENTERED;

            public bool HasZ => _device.HasZ;

            public bool HasR => _device.HasR;

            public bool HasU => _device.HasU;

            public bool HasV => _device.HasV;

            public bool HasPov => _device.HasPov;

            /// <summary>Left stick X, -1..1, positive = right.</summary>
            public double AxisX => Centered(_info.dwXpos, _device.Caps.wXmin, _device.Caps.wXmax);

            /// <summary>Left stick Y, -1..1, positive = DOWN (HID/legacy orientation —
            /// the snapshot converter flips it, per-source converters stay one-sided).</summary>
            public double AxisY => Centered(_info.dwYpos, _device.Caps.wYmin, _device.Caps.wYmax);

            /// <summary>Right stick X on the usual Windows HID joystick layout.</summary>
            public double AxisZ => Centered(_info.dwZpos, _device.Caps.wZmin, _device.Caps.wZmax);

            /// <summary>Right stick Y, positive = DOWN, same layout note as AxisY.</summary>
            public double AxisR => Centered(_info.dwRpos, _device.Caps.wRmin, _device.Caps.wRmax);

            /// <summary>Trigger/slider U, 0..1 (rest 0).</summary>
            public double UnipolarU => Unipolar(_info.dwUpos, _device.Caps.wUmin, _device.Caps.wUmax);

            /// <summary>Trigger/slider V, 0..1 (rest 0).</summary>
            public double UnipolarV => Unipolar(_info.dwVpos, _device.Caps.wVmin, _device.Caps.wVmax);

            private static double Centered(uint value, uint min, uint max)
            {
                if (max <= min)
                {
                    return 0;
                }
                double center = min + (max - min) / 2.0;
                double half = (max - min) / 2.0;
                return Math.Clamp((value - center) / half, -1.0, 1.0);
            }

            private static double Unipolar(uint value, uint min, uint max)
            {
                if (max <= min)
                {
                    return 0;
                }
                return Math.Clamp((value - min) / (double)(max - min), 0.0, 1.0);
            }
        }
    }
}
