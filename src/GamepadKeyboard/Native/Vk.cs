using System;
using System.Runtime.InteropServices;

namespace GamepadKeyboard.Native
{
    /// <summary>
    /// Virtual key codes used by the app (subset of Win32 VK_* constants).
    /// </summary>
    internal static class Vk
    {
        public const ushort None = 0;
        public const ushort Cancel = 0x03;
        public const ushort Back = 0x08;       // Backspace
        public const ushort Tab = 0x09;
        public const ushort Return = 0x0D;     // Enter
        public const ushort Shift = 0x10;
        public const ushort Control = 0x11;
        public const ushort Menu = 0x12;       // Alt
        public const ushort Pause = 0x13;
        public const ushort Capital = 0x14;    // Caps Lock
        public const ushort Escape = 0x1B;
        public const ushort Space = 0x20;
        public const ushort PageUp = 0x21;
        public const ushort PageDown = 0x22;
        public const ushort End = 0x23;
        public const ushort Home = 0x24;
        public const ushort Left = 0x25;
        public const ushort Up = 0x26;
        public const ushort Right = 0x27;
        public const ushort Down = 0x28;
        public const ushort Print = 0x2A;
        public const ushort Insert = 0x2D;
        public const ushort Delete = 0x2E;
        public const ushort LWin = 0x5B;
        public const ushort RWin = 0x5C;
        public const ushort Apps = 0x5D;
        public const ushort NumPad0 = 0x60;
        public const ushort NumPad9 = 0x69;
        public const ushort Multiply = 0x6A;
        public const ushort Add = 0x6B;
        public const ushort Separator = 0x6C;
        public const ushort Subtract = 0x6D;
        public const ushort Decimal = 0x6E;
        public const ushort Divide = 0x6F;
        public const ushort F1 = 0x70;
        public const ushort F2 = 0x71;
        public const ushort F3 = 0x72;
        public const ushort F4 = 0x73;
        public const ushort F5 = 0x74;
        public const ushort F6 = 0x75;
        public const ushort F7 = 0x76;
        public const ushort F8 = 0x77;
        public const ushort F9 = 0x78;
        public const ushort F10 = 0x79;
        public const ushort F11 = 0x7A;
        public const ushort F12 = 0x7B;
        public const ushort F13 = 0x7C;
        public const ushort F24 = 0x87;
        public const ushort NumLock = 0x90;
        public const ushort Scroll = 0x91;

        // extended (nav cluster + arrows share these with padding)
        public const ushort LShift = 0xA0;
        public const ushort RShift = 0xA1;
        public const ushort LControl = 0xA2;
        public const ushort RControl = 0xA3;
        public const ushort LMenu = 0xA4;
        public const ushort RMenu = 0xA5;

        public const ushort VolumeMute = 0xAD;
        public const ushort VolumeDown = 0xAE;
        public const ushort VolumeUp = 0xAF;
        public const ushort MediaNext = 0xB0;
        public const ushort MediaPrev = 0xB1;
        public const ushort MediaStop = 0xB2;
        public const ushort MediaPlayPause = 0xB3;
    }
}