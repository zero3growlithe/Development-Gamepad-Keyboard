using System;
using System.IO;
using System.Runtime.InteropServices;

namespace GamepadKeyboard.Native
{
    /// <summary>
    /// Optional keyboard-injection path through the Interception filter driver
    /// (https://github.com/oblitum/Interception). SendInput-generated keys carry
    /// the LLKHF_INJECTED flag, and the Parsec host's low-level keyboard hook
    /// swallows exactly that class of keys while a session is connected (mouse
    /// injection is unaffected — the absolute-mouse fix keeps working), so
    /// gamepad-derived keystrokes silently vanish when the game machine is a
    /// Parsec host. Keys written through interception enter the OS as genuine
    /// keyboard-class device input below the injected-flag layer and arrive
    /// looking like physical keystrokes.
    ///
    /// interception.dll and the driver are NOT bundled (own license, install
    /// once: command-line installer + reboot). Everything here is optional:
    /// until the Keyboard-tab toggle is on, no library is probed at all, and
    /// every per-key failure (library missing, driver not running, unknown
    /// scancode) falls back to the classic SendInput path.
    /// </summary>
    internal static class InterceptionKeyboard
    {
        // interception.h (1.0.1): key stroke = { ushort code; ushort state;
        // uint information; } — 8 bytes per stroke; keyboard slots = 1..10.
        // State bits come straight from ntddkbd KEYBOARD_INPUT_DATA.Flags:
        // make = 0, break = 1, E0 prefix = 2, E1 prefix = 4. The library
        // indexes multi-stroke buffers with sizeof(InterceptionKeyStroke) = 8,
        // so a tap is two consecutive 8-byte strokes in one send call.
        private const int KeyboardSlotFirst = 1;
        private const int KeyboardSlotLast = 10;
        private const ushort KeyStateUp = 0x0001;
        private const ushort KeyStateE0 = 0x0002;

        private const string DllFileName = "interception.dll";

        private static readonly object _sync = new object();
        private static readonly byte[] _stroke = new byte[16];   // two key strokes, stride 8
        private static readonly byte[] _hardwareId = new byte[256];
        private static IntPtr _library;
        private static IntPtr _context;
        private static int _device;              // chosen keyboard slot (1..10)
        private static DateTime _lastFailedInit = DateTime.MinValue;
        private static volatile string _status = "Interception: not probed yet";

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        private delegate IntPtr CreateContextDelegate();
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        private delegate void DestroyContextDelegate(IntPtr context);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        private delegate int SendDelegate(IntPtr context, int device, byte[] stroke, uint nstroke);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        private delegate uint HardwareIdDelegate(IntPtr context, int device, byte[] buffer, uint size);

        private static CreateContextDelegate _createContext;
        private static DestroyContextDelegate _destroyContext;
        private static SendDelegate _send;
        private static HardwareIdDelegate _hardwareIdGetter;

        /// <summary>True when interception.dll and the Interception driver are
        /// loaded and a keyboard slot was chosen. The Keyboard-tab toggle still
        /// decides per key whether this path is used.</summary>
        public static bool Ready { get; private set; }

        /// <summary>One-line human-readable state for the Settings window.</summary>
        public static string Status => _status ?? "";

        /// <summary>Ensures the library/driver are usable; cheap no-op when
        /// ready, rate-limited retries after a failure. Never throws.</summary>
        public static bool Probe()
        {
            try
            {
                lock (_sync)
                {
                    if (Ready)
                    {
                        return true;
                    }
                    if ((DateTime.UtcNow - _lastFailedInit).TotalSeconds < 10)
                    {
                        return false;
                    }
                    InitializeLocked();
                    return Ready;
                }
            }
            catch (Exception ex)
            {
                LogOnce("probe fault", ex);
                return false;
            }
        }

        /// <summary>Sends one key event as physical keyboard input.
        /// Returns false when the setting is off, the path is unavailable, or
        /// this virtual key cannot be represented (caller falls back to
        /// SendInput). Steady state allocates nothing.</summary>
        public static bool TrySendKey(ushort virtualKey, bool down, bool extended)
        {
            if (virtualKey == 0 || !Settings.AppSettings.Instance.InterceptionKeyboard)
            {
                return false;
            }
            lock (_sync)
            {
                if (!Ready && !TryInitializeLocked())
                {
                    return false;
                }
                if (!TranslateVirtualKey(virtualKey, extended, out int code, out bool e0))
                {
                    return false;
                }
                WriteStroke(0, code, (ushort)((down ? 0 : KeyStateUp) | (e0 ? KeyStateE0 : 0)));
                int written = _send(_context, _device, _stroke, 1);
                if (written != 1)
                {
                    InvalidateLocked("send failed — driver/library mismatch, falling back to SendInput");
                    return false;
                }
                return true;
            }
        }

        /// <summary>Sends a full tap (down + up) in one driver write, matching
        /// the atomicity of the two-event SendInput call it replaces.</summary>
        public static bool TrySendTap(ushort virtualKey, bool extended)
        {
            if (virtualKey == 0 || !Settings.AppSettings.Instance.InterceptionKeyboard)
            {
                return false;
            }
            lock (_sync)
            {
                if (!Ready && !TryInitializeLocked())
                {
                    return false;
                }
                if (!TranslateVirtualKey(virtualKey, extended, out int code, out bool e0))
                {
                    return false;
                }
                WriteStroke(0, code, e0 ? KeyStateE0 : (ushort)0);
                WriteStroke(8, code, (ushort)(KeyStateUp | (e0 ? KeyStateE0 : (ushort)0)));
                int written = _send(_context, _device, _stroke, 2);
                if (written != 2)
                {
                    InvalidateLocked("tap send failed — driver/library mismatch, falling back to SendInput");
                    return false;
                }
                return true;
            }
        }

        // ── Init / teardown ─────────────────────────────────────────────────

        private static bool TryInitializeLocked()
        {
            if (Ready)
            {
                return true;
            }
            if ((DateTime.UtcNow - _lastFailedInit).TotalSeconds < 10)
            {
                return false;
            }
            InitializeLocked();
            return Ready;
        }

        private static void InitializeLocked()
        {
            _lastFailedInit = DateTime.UtcNow;
            if (_context != IntPtr.Zero)
            {
                return;   // context exists but a previous send failed; reuse it
            }
            if (!LoadLibraryLocked())
            {
                _status = "Interception: interception.dll not found (exe folder, C:\\Interception\\library\\x64, PATH or the configured path) — using SendInput";
                LogOnce("library not found (searched: " + _lastLibrarySearch + ")", null);
                return;
            }
            try
            {
                _context = _createContext();
            }
            catch (Exception ex)
            {
                LogOnce("create_context fault", ex);
                _context = IntPtr.Zero;
            }
            if (_context == IntPtr.Zero)
            {
                _context = IntPtr.Zero;
                _status = "Interception: driver not ready — install the Interception driver (command-line installer) and reboot; using SendInput";
                LogOnce("driver not ready (create_context returned 0)", null);
                return;
            }
            _device = ChooseKeyboardSlot();
            Ready = true;
            HookProcessExitOnce();
            _status = "Interception: ready — keys leave as keyboard slot " + _device + " (physical device input)";
            App.Log("interception: keyboard mode ready, slot " + _device);
        }

        private static bool _exitHooked;

        /// <summary>Frees the driver context on process exit (best effort;
        /// the OS reclaims it either way — the driver tolerates unclosed clients).</summary>
        private static void HookProcessExitOnce()
        {
            if (_exitHooked) return;
            _exitHooked = true;
            AppDomain.CurrentDomain.ProcessExit += static (_, __) => Shutdown();
        }

        /// <summary>Loads interception.dll from, in order: the configured
        /// path, the app directory, the standard install location, PATH.</summary>
        private static bool LoadLibraryLocked()
        {
            if (_library != IntPtr.Zero && _createContext != null)
            {
                return true;
            }
            string configured = Settings.AppSettings.Instance.InterceptionDllPath;
            string baseDirectory = AppContext.BaseDirectory;
            string[] candidates =
            {
                File.Exists(configured) ? Path.GetFullPath(configured) : "",
                System.IO.Path.Combine(baseDirectory, DllFileName),
                System.IO.Path.Combine(@"C:\Interception\library\x64", DllFileName),
                DllFileName,   // default loader search incl. PATH
            };
            _lastLibrarySearch = "";
            foreach (string candidate in candidates)
            {
                if (candidate.Length == 0)
                {
                    continue;
                }
                _lastLibrarySearch += ( _lastLibrarySearch.Length > 0 ? "; " : "" ) + candidate;
                if (candidate != DllFileName && !File.Exists(candidate))
                {
                    continue;
                }
                if (NativeLibrary.TryLoad(candidate, out _library) && BindExports())
                {
                    return true;
                }
            }
            _library = IntPtr.Zero;
            return false;
        }

        /// <summary>Binds the interception_ functions the send path uses.
        /// The whole C API ships in every 1.0.1+ interception.dll.</summary>
        private static bool BindExports()
        {
            try
            {
                _createContext = Marshal.GetDelegateForFunctionPointer<CreateContextDelegate>(
                    NativeLibrary.GetExport(_library, "interception_create_context"));
                _destroyContext = Marshal.GetDelegateForFunctionPointer<DestroyContextDelegate>(
                    NativeLibrary.GetExport(_library, "interception_destroy_context"));
                _send = Marshal.GetDelegateForFunctionPointer<SendDelegate>(
                    NativeLibrary.GetExport(_library, "interception_send"));
                _hardwareIdGetter = Marshal.GetDelegateForFunctionPointer<HardwareIdDelegate>(
                    NativeLibrary.GetExport(_library, "interception_get_hardware_id"));
                return true;
            }
            catch (Exception ex)
            {
                LogOnce("export binding failed (interception.dll too old? need 1.0.1+)", ex);
                NativeLibrary.Free(_library);
                _library = IntPtr.Zero;
                return false;
            }
        }

        /// <summary>Picks the first keyboard slot the driver actually has a
        /// device behind (hardware id readable); slot 1 as the safe default.</summary>
        private static int ChooseKeyboardSlot()
        {
            for (int slot = KeyboardSlotFirst; slot <= KeyboardSlotLast; slot++)
            {
                uint size = 0;
                try
                {
                    size = _hardwareIdGetter(_context, slot, _hardwareId, (uint)_hardwareId.Length);
                }
                catch { }
                if (size > 0)
                {
                    string name = DecodeId(size);
                    App.Log("interception: keyboard slot " + slot + " = " + name);
                    return slot;
                }
            }
            App.Log("interception: no hardware id reported for keyboard slots 1..10, using slot 1");
            return KeyboardSlotFirst;
        }

        private static string DecodeId(uint size)
        {
            int length = Math.Min((int)size, _hardwareId.Length);
            int end = length;
            for (int i = 0; i < length; i++)
            {
                if (_hardwareId[i] == 0)
                {
                    end = i;
                    break;
                }
            }
            string text = System.Text.Encoding.ASCII.GetString(_hardwareId, 0, end).Trim();
            return text.Length <= 90 ? text : text[..90] + "…";
        }

        // ── Key encoding ────────────────────────────────────────────────────

        /// <summary>Resolves the scan code + E0 prefix for a virtual key with
        /// the active layout (MAPVK_VK_TO_VSC_EX returns the prefix in the
        /// high byte: VK_RCONTROL → 0xE01D while VK_LCONTROL → 0x001D).
        /// E1-prefixed keys (Pause) are not representable → caller falls back
        /// to SendInput for them.</summary>
        private static bool TranslateVirtualKey(ushort virtualKey, bool extended, out int code, out bool e0)
        {
            code = 0;
            e0 = false;
            uint full;
            try
            {
                full = (uint)NativeMethods.MapVirtualKeyEx(virtualKey, NativeMethods.MapVkToVscExtended, NativeMethods.GetKeyboardLayout(0));
            }
            catch
            {
                return false;
            }
            if (full == 0)
            {
                return false;
            }
            uint prefix = full >> 8;
            if (prefix == 0xE1)
            {
                return false;
            }
            code = (int)(full & 0xFF);
            e0 = prefix == 0xE0 || extended;
            return true;
        }

        /// <summary>Writes one 8-byte key stroke: { code: ushort, state: ushort,
        /// information: uint } (down state bit = 0 like ntddkbd KEY_MAKE).</summary>
        private static void WriteStroke(int offset, int code, ushort state)
        {
            _stroke[offset] = (byte)code;
            _stroke[offset + 1] = (byte)(code >> 8);
            _stroke[offset + 2] = (byte)state;
            _stroke[offset + 3] = (byte)(state >> 8);
            _stroke[offset + 4] = 0;
            _stroke[offset + 5] = 0;
            _stroke[offset + 6] = 0;
            _stroke[offset + 7] = 0;
        }

        private static void InvalidateLocked(string reason)
        {
            Ready = false;
            _lastFailedInit = DateTime.UtcNow;
            _status = "Interception: " + reason;
            App.Log("interception: " + reason);
            ShutdownLocked();
        }

        public static void Shutdown()
        {
            try
            {
                lock (_sync)
                {
                    ShutdownLocked();
                }
            }
            catch { }
        }

        private static void ShutdownLocked()
        {
            if (_context != IntPtr.Zero && _destroyContext != null)
            {
                try { _destroyContext(_context); } catch { }
                _context = IntPtr.Zero;
            }
            Ready = false;
        }

        // ── One-shot diagnostics ────────────────────────────────────────────
        private static string _lastLoggedReason = "";
        private static DateTime _lastLoggedAt = DateTime.MinValue;

        /// <summary>One line per new reason per minute — the crash.log rhythm
        /// used across this app for repeated input-path conditions.</summary>
        private static void LogOnce(string reason, Exception? ex)
        {
            string message = reason + (ex == null ? "" : " — " + ex.GetType().Name + ": " + ex.Message);
            DateTime now = DateTime.UtcNow;
            if (message == _lastLoggedReason && (now - _lastLoggedAt).TotalMinutes < 1)
            {
                return;
            }
            _lastLoggedReason = message;
            _lastLoggedAt = now;
            App.Log("interception: " + message);
        }
    }
}