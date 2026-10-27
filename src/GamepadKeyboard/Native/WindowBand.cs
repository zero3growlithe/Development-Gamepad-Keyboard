using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace GamepadKeyboard.Native
{
    /// <summary>
    /// Puts our overlay windows into the ZBID_UIACCESS band — the band that sits above the
    /// Start menu, Task Manager and every immersive shell surface.
    /// <para>
    /// Two conditions have to hold and both are easy to miss:
    /// </para>
    /// <list type="number">
    /// <item>the process must hold the UIAccess flag (signed exe, in Program Files, launched
    /// non-elevated — an elevated process never gets it), and</item>
    /// <item>the window has to be promoted with an explicit SetWindowPos(HWND_TOPMOST) call.
    /// WS_EX_TOPMOST applied at creation time (which is all WPF's Topmost = true does) leaves
    /// the window in ZBID_DESKTOP, i.e. still under the Start menu, even though the process
    /// token carries UIAccess.</item>
    /// </list>
    /// <para>
    /// Band ids are readable without any privilege (GetWindowBand, exported by user32 since
    /// Windows 8 but undocumented), which lets the log state what the OS actually ended up
    /// with instead of what we asked for.
    /// </para>
    /// </summary>
    internal static class WindowBand
    {
        // ── Win32 ────────────────────────────────────────────────────────────────

        private const int HwndTopmost = -1;

        private const uint SwpNoSize = 0x0001;
        private const uint SwpNoMove = 0x0002;
        private const uint SwpNoActivate = 0x0010;
        private const uint SwpNoOwnerZOrder = 0x0200;

        // ZBID values as defined by the enum; priority order is NOT numeric order
        // (ZBID_UIACCESS has the lowest id of the interesting bands but the highest priority).
        private const uint ZbidDesktop = 1;
        private const uint ZbidUiAccess = 2;

        [DllImport("user32.dll", EntryPoint = "SetWindowPos", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool SetWindowPos(IntPtr windowHandle, IntPtr insertAfter, int x, int y,
            int width, int height, uint flags);

        [DllImport("kernel32.dll", EntryPoint = "GetModuleHandleW", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern IntPtr GetModuleHandleW(string moduleName);

        [DllImport("kernel32.dll", EntryPoint = "GetProcAddress", CharSet = CharSet.Ansi, SetLastError = true)]
        private static extern IntPtr GetProcAddress(IntPtr module, string procedureName);

        [UnmanagedFunctionPointer(CallingConvention.Winapi)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private delegate bool GetWindowBandDelegate(IntPtr windowHandle, out uint band);

        // ── State ────────────────────────────────────────────────────────────────

        private static GetWindowBandDelegate? _getWindowBand;
        private static bool _getWindowBandProbed;
        private static readonly Dictionary<string, uint> LastLoggedBand = new();

        // ── Promotion ────────────────────────────────────────────────────────────

        /// <summary>
        /// Re-asserts topmost on the window (which is what actually moves it into the
        /// UIAccess band when the process holds the flag) and logs the resulting band id
        /// once per change, so a repeating call is cheap and quiet.
        /// </summary>
        public static void Promote(Window? window, string label)
        {
            if (window == null)
                return;

            IntPtr handle;
            try
            {
                handle = new WindowInteropHelper(window).EnsureHandle();
            }
            catch (InvalidOperationException)
            {
                return;   // no presentation source yet — the next Promote call will do it
            }

            if (handle == IntPtr.Zero)
                return;

            bool promoted = SetWindowPos(handle, new IntPtr(HwndTopmost), 0, 0, 0, 0,
                SwpNoMove | SwpNoSize | SwpNoActivate | SwpNoOwnerZOrder);
            int promoteError = promoted ? 0 : Marshal.GetLastWin32Error();

            if (!TryGetBand(handle, out uint band))
            {
                if (!promoted && LastLoggedBand.TryAdd(label, uint.MaxValue))
                    App.Log("overlay band: " + label + " setwindowpos=failed err=" + promoteError + " band=unknown");
                return;
            }

            if (LastLoggedBand.TryGetValue(label, out uint previous) && previous == band && promoted)
                return;

            LastLoggedBand[label] = band;
            string verdict = band == ZbidUiAccess
                ? " (above the Start menu)"
                : " (NOT uiaccess - check the run mode line: elevation and a missing signature both cost the band)";
            App.Log("overlay band: " + label + " setwindowpos=" + (promoted ? "ok" : "failed err=" + promoteError) +
                    " band=" + BandName(band) + verdict);
        }

        private static bool TryGetBand(IntPtr handle, out uint band)
        {
            band = 0;
            if (!_getWindowBandProbed)
            {
                _getWindowBandProbed = true;
                IntPtr user32 = GetModuleHandleW("user32.dll");
                if (user32 != IntPtr.Zero)
                {
                    IntPtr address = GetProcAddress(user32, "GetWindowBand");
                    if (address != IntPtr.Zero)
                        _getWindowBand = Marshal.GetDelegateForFunctionPointer<GetWindowBandDelegate>(address);
                }
            }

            return _getWindowBand != null && _getWindowBand(handle, out band);
        }

        private static string BandName(uint band)
        {
            switch (band)
            {
                case ZbidDesktop:
                    return ZbidDesktop + " (desktop)";
                case ZbidUiAccess:
                    return ZbidUiAccess + " (uiaccess)";
                default:
                    return band.ToString();
            }
        }
    }
}
