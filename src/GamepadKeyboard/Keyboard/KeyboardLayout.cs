using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

using GamepadKeyboard.Native;

namespace GamepadKeyboard.Keyboard
{
    /// <summary>
    /// Builds the full US key layout and lays it out on a Canvas.
    /// Layout is generated from a compact definition table; geometry depends only on
    /// KeySpacing (gap in key-width units) so the ray distance scales with it.
    /// </summary>
    public sealed class KeyboardLayout
    {
        public sealed class KeyDef
        {
            public string Label;
            public ushort Vk;
            public bool Extended;
            public double X, Y;   // cell coords (columns / rows), assigned at build time
            public double W = 1;  // width in key units
            public double H = 1;

            public KeyDef(string label, ushort vk, double w = 1, bool extended = false)
            {
                Label = label;
                Vk = vk;
                W = w;
            }

            /// <summary>Char keys: letters/digits map to their VK; punctuation maps to the proper VK_OEM_* code.</summary>
            public KeyDef(string label, char c, double w = 1)
            {
                Label = label;
                Vk = CharVk(c);
                W = w;
            }

            internal static ushort CharVk(char c)
            {
                if (c >= 'A' && c <= 'Z') return (ushort)c;   // VK_A..VK_Z match ASCII
                if (c >= '0' && c <= '9') return (ushort)c;   // VK_0..VK_9 match ASCII
                return c switch
                {
                    '`' => 0xC0, '-' => 0xBD, '=' => 0xBB,          // OEM_3, OEM_MINUS, OEM_PLUS
                    '[' => 0xDB, ']' => 0xDD, '\\' => 0xDC,        // OEM_4, OEM_6, OEM_5
                    ';' => 0xBA, '\'' => 0xDE,                      // OEM_1, OEM_7
                    ',' => 0xBC, '.' => 0xBE, '/' => 0xBF,          // OEM_COMMA, OEM_PERIOD, OEM_2
                    _ => (ushort)c
                };
            }
        }

        public IReadOnlyList<KeyDef> Keys => _keys;
        private readonly List<KeyDef> _keys = new();

        /// <summary>Total grid width/height in key units (incl. gaps).</summary>
        public double GridW { get; private set; }
        public double GridH { get; private set; }

        public KeyDef? FindByVk(ushort vk)
        {
            foreach (var k in _keys)
                if (k.Vk == vk && vk != 0) return k;
            return null;
        }

        private void Row(int y, params KeyDef[] defs)
        {
            double x = 0;
            foreach (var d in defs)
            {
                d.X = x;
                d.Y = y;
                _keys.Add(d);
                x += d.W;
            }
        }

        public void Build()
        {
            // Row 0: Esc + F1..F12
            Row(0,
                new KeyDef("Esc", Vk.Escape),
                new KeyDef("F1", Vk.F1), new KeyDef("F2", Vk.F2), new KeyDef("F3", Vk.F3), new KeyDef("F4", Vk.F4),
                new KeyDef("F5", Vk.F5), new KeyDef("F6", Vk.F6), new KeyDef("F7", Vk.F7), new KeyDef("F8", Vk.F8),
                new KeyDef("F9", Vk.F9), new KeyDef("F10", Vk.F10), new KeyDef("F11", Vk.F11), new KeyDef("F12", Vk.F12));

            // Row 1: ` 1..0 - = Backspace
            Row(1,
                new KeyDef("`", '`'), new KeyDef("1", '1'), new KeyDef("2", '2'), new KeyDef("3", '3'), new KeyDef("4", '4'),
                new KeyDef("5", '5'), new KeyDef("6", '6'), new KeyDef("7", '7'), new KeyDef("8", '8'), new KeyDef("9", '9'),
                new KeyDef("0", '0'), new KeyDef("-", '-'), new KeyDef("=", '='),
                new KeyDef("Bksp", Vk.Back, 2));

            // Row 2: Tab Q..]
            Row(2,
                new KeyDef("Tab", Vk.Tab, 1.5),
                new KeyDef("Q", 'Q'), new KeyDef("W", 'W'), new KeyDef("E", 'E'), new KeyDef("R", 'R'),
                new KeyDef("T", 'T'), new KeyDef("Y", 'Y'), new KeyDef("U", 'U'), new KeyDef("I", 'I'),
                new KeyDef("O", 'O'), new KeyDef("P", 'P'), new KeyDef("[", '['), new KeyDef("]", ']'),
                new KeyDef("\\", 0xDC, 1.5));

            // Row 3: Caps A..'
            Row(3,
                new KeyDef("Caps", Vk.Capital, 1.75),
                new KeyDef("A", 'A'), new KeyDef("S", 'S'), new KeyDef("D", 'D'), new KeyDef("F", 'F'),
                new KeyDef("G", 'G'), new KeyDef("H", 'H'), new KeyDef("J", 'J'), new KeyDef("K", 'K'),
                new KeyDef("L", 'L'), new KeyDef(";", ';'), new KeyDef("'", '\''),
                new KeyDef("Enter", Vk.Return, 2.25));

            // Row 4: Shift Z../
            Row(4,
                new KeyDef("Shift", Vk.LShift, 2.25),
                new KeyDef("Z", 'Z'), new KeyDef("X", 'X'), new KeyDef("C", 'C'), new KeyDef("V", 'V'),
                new KeyDef("B", 'B'), new KeyDef("N", 'N'), new KeyDef("M", 'M'), new KeyDef(",", ','), new KeyDef(".", '.'),
                new KeyDef("/", '/'),
                new KeyDef("RShift", Vk.RShift, 2.75));

            // Row 5: Ctrl Win Alt Space Alt Ctrl
            Row(5,
                new KeyDef("LCtrl", Vk.LControl, 1.25),
                new KeyDef("Win", Vk.LWin, 1.25),
                new KeyDef("LAlt", Vk.LMenu, 1.25),
                new KeyDef("Space", Vk.Space, 6.25),
                new KeyDef("RAlt", Vk.RMenu, 1.25),
                new KeyDef("RCtrl", Vk.RControl, 1.25));

            GridW = 16;   // widest row: 13 function keys
            GridH = 6;    // rows 0..5
        }

        private void Add(KeyDef k, double x, double y, double w = 1, double h = 1)
        {
            k.X = x; k.Y = y; k.W = w; k.H = h;
            _keys.Add(k);
        }

        /// <summary>Grid coord (key units) -> pixels with the given spacing.</summary>
        public static Rect KeyRect(KeyDef k, double spacing)
        {
            // cell pitch = base 48 px + spacing px; key occupies (pitch - spacing)
            double pitch = 48 + spacing;
            double x = k.X * pitch + spacing / 2;
            double y = k.Y * pitch + spacing / 2;
            double w = k.W * pitch - spacing;
            double h = k.H * pitch - spacing;
            return new Rect(x, y, w, h);
        }
    }
}