using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using Microsoft.Win32.SafeHandles;

namespace GamepadKeyboard.Native
{
    /// <summary>
    /// Prefers HidHide's process-owned, in-memory blacklist. An explicitly enabled
    /// legacy fallback temporarily merges paths into the persistent blacklist and
    /// journals its additions so they can be removed after an abnormal exit.
    /// </summary>
    public sealed class HidHideSession : IDisposable
    {
        private const string ControlDevice = @"\\.\HidHide";
        private const uint GenericRead = 0x80000000;
        private const uint FileShareRead = 0x00000001;
        private const uint FileShareWrite = 0x00000002;
        private const uint OpenExisting = 3;
        private const int ErrorInsufficientBuffer = 122;
        private const int ErrorMoreData = 234;
        private const int MaxConfigurationBytes = 1024 * 1024;

        private static readonly uint IoctlGetWhitelist = CtlCode(2048);
        private static readonly uint IoctlGetBlacklist = CtlCode(2050);
        private static readonly uint IoctlSetBlacklist = CtlCode(2051);
        private static readonly uint IoctlGetActive = CtlCode(2052);
        private static readonly uint IoctlGetInverse = CtlCode(2054);
        private static readonly uint IoctlAddSessionBlacklist = CtlCode(2056);
        private static readonly uint IoctlClearSessionBlacklist = CtlCode(2057);

        private ReservationMode _mode;
        private string[] _legacyAddedPaths = Array.Empty<string>();

        public bool IsClaimed => _mode != ReservationMode.None;
        public bool IsLegacyClaimed => _mode == ReservationMode.Legacy;
        public string ModeDescription => _mode switch
        {
            ReservationMode.Session => "claimed (session)",
            ReservationMode.Legacy => "claimed (legacy persistent fallback)",
            _ => "not claimed"
        };

        public HidHideResult Claim(IEnumerable<string> deviceInstancePaths, bool allowLegacyFallback)
        {
            var paths = deviceInstancePaths
                .Where(path => !string.IsNullOrWhiteSpace(path))
                .Select(path => path.Trim())
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();

            if (paths.Length == 0)
                return HidHideResult.Failure("HidHide is enabled, but no controller is selected.");

            if (IsClaimed)
            {
                var released = Release();
                if (!released.Success) return released;
            }

            // Never overwrite evidence from an earlier interrupted legacy claim.
            if (File.Exists(LegacyJournalPath))
            {
                var recovery = RecoverLegacyClaim();
                if (!recovery.Success) return recovery;
            }

            using var handle = OpenControlDevice(out var openError);
            if (handle == null)
                return HidHideResult.Failure(openError);

            var active = ReadBoolean(handle, IoctlGetActive, "read HidHide's active state");
            if (!active.Success) return active;
            if (!active.Value)
                return HidHideResult.Failure("HidHide device hiding is not active. Enable it in HidHide Configuration Client.");

            var inverse = ReadBoolean(handle, IoctlGetInverse, "read HidHide's inverse-cloak state");
            if (!inverse.Success) return inverse;
            if (inverse.Value)
                return HidHideResult.Failure("HidHide inverse cloak is enabled. Turn it off before using controller reservation.");

            var whitelist = ReadMultiString(handle, IoctlGetWhitelist, "read HidHide's application list");
            if (!whitelist.Success) return whitelist;

            string? processPath = Environment.ProcessPath;
            string? ntProcessPath = processPath == null ? null : ToNtPath(processPath);
            bool applicationAllowed = whitelist.Values.Any(path =>
                string.Equals(path, processPath, StringComparison.OrdinalIgnoreCase) ||
                (ntProcessPath != null && string.Equals(path, ntProcessPath, StringComparison.OrdinalIgnoreCase)));
            if (!applicationAllowed)
                return HidHideResult.Failure("Add this app to HidHide's Applications list before enabling controller reservation.");

            var sessionApi = ProbeSessionApi(handle);
            if (!sessionApi.Success)
            {
                if (allowLegacyFallback && sessionApi.SessionApiUnsupported)
                    return ClaimLegacy(handle, paths);
                return sessionApi;
            }

            byte[] input = EncodeMultiString(paths);
            if (!DeviceIoControl(handle, IoctlAddSessionBlacklist,
                    input, input.Length, null, 0, out _, IntPtr.Zero))
            {
                int error = Marshal.GetLastWin32Error();
                string message = SessionApiError("reserve the selected controller", error);
                if (!DeviceIoControl(handle, IoctlClearSessionBlacklist,
                        null, 0, null, 0, out _, IntPtr.Zero))
                    message += " Any partial session claim will still be removed automatically when the app exits.";
                return HidHideResult.Failure(message);
            }

            _mode = ReservationMode.Session;
            return HidHideResult.Ok;
        }

        public HidHideResult Release()
        {
            if (_mode == ReservationMode.None) return HidHideResult.Ok;

            if (_mode == ReservationMode.Legacy && _legacyAddedPaths.Length == 0)
            {
                _mode = ReservationMode.None;
                return HidHideResult.Ok;
            }

            using var handle = OpenControlDevice(out var openError);
            if (handle == null)
                return HidHideResult.Failure(openError + LegacyReleaseGuidance());

            if (_mode == ReservationMode.Legacy)
            {
                var cleanup = CleanupLegacyPaths(handle, _legacyAddedPaths);
                if (!cleanup.Success) return cleanup;
                _legacyAddedPaths = Array.Empty<string>();
                _mode = ReservationMode.None;
                return HidHideResult.Ok;
            }

            if (!DeviceIoControl(handle, IoctlClearSessionBlacklist,
                    null, 0, null, 0, out _, IntPtr.Zero))
            {
                int error = Marshal.GetLastWin32Error();
                return HidHideResult.Failure(SessionApiError("release the controller", error) +
                                             " Exit the app to release its session claim.");
            }

            _mode = ReservationMode.None;
            return HidHideResult.Ok;
        }

        public HidHideResult RecoverLegacyClaim()
        {
            if (!File.Exists(LegacyJournalPath)) return HidHideResult.Ok;

            string[] paths;
            try
            {
                var journalPaths = JsonSerializer.Deserialize<string[]>(
                    File.ReadAllText(LegacyJournalPath));
                paths = journalPaths?
                    .Where(path => !string.IsNullOrWhiteSpace(path))
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToArray() ?? Array.Empty<string>();
            }
            catch (Exception ex)
            {
                return HidHideResult.Failure("could not read the legacy recovery journal (" + ex.Message + ").");
            }

            if (paths.Length == 0)
                return ClearLegacyJournal();

            using var handle = OpenControlDevice(out var openError);
            if (handle == null)
                return HidHideResult.Failure(openError + " Legacy blacklist cleanup remains pending.");
            return CleanupLegacyPaths(handle, paths);
        }

        private HidHideResult ClaimLegacy(SafeFileHandle handle, IReadOnlyList<string> paths)
        {
            var currentResult = ReadMultiString(handle, IoctlGetBlacklist,
                "read HidHide's persistent device list");
            if (!currentResult.Success) return currentResult;

            var current = new HashSet<string>(currentResult.Values, StringComparer.OrdinalIgnoreCase);
            string[] added = paths.Where(path => !current.Contains(path)).ToArray();
            if (added.Length == 0)
            {
                _legacyAddedPaths = Array.Empty<string>();
                _mode = ReservationMode.Legacy;
                return HidHideResult.Ok;
            }

            var journal = SaveLegacyJournal(added);
            if (!journal.Success) return journal;

            var merged = currentResult.Values.Concat(added)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();
            var write = WriteMultiString(handle, IoctlSetBlacklist, merged,
                "update HidHide's persistent device list");
            if (!write.Success)
            {
                var rollback = CleanupLegacyPaths(handle, added);
                return rollback.Success
                    ? write
                    : HidHideResult.Failure(write.Error + " Rollback also failed: " + rollback.Error);
            }

            _legacyAddedPaths = added;
            _mode = ReservationMode.Legacy;
            return HidHideResult.Ok;
        }

        private static HidHideResult CleanupLegacyPaths(SafeFileHandle handle, IReadOnlyCollection<string> addedPaths)
        {
            var currentResult = ReadMultiString(handle, IoctlGetBlacklist,
                "read HidHide's persistent device list for cleanup");
            if (!currentResult.Success) return currentResult;

            var remove = new HashSet<string>(addedPaths, StringComparer.OrdinalIgnoreCase);
            string[] cleaned = currentResult.Values.Where(path => !remove.Contains(path)).ToArray();
            if (cleaned.Length != currentResult.Values.Count)
            {
                var write = WriteMultiString(handle, IoctlSetBlacklist, cleaned,
                    "restore HidHide's persistent device list");
                if (!write.Success)
                    return HidHideResult.Failure(write.Error + " Cleanup remains journaled for the next app launch.");
            }

            return ClearLegacyJournal();
        }

        private static HidHideResult SaveLegacyJournal(IReadOnlyList<string> paths)
        {
            try
            {
                WriteLegacyJournal(paths);
                return HidHideResult.Ok;
            }
            catch (Exception ex)
            {
                return HidHideResult.Failure("could not create the legacy recovery journal (" + ex.Message + "). No persistent changes were made.");
            }
        }

        private static HidHideResult ClearLegacyJournal()
        {
            try
            {
                File.Delete(LegacyJournalPath);
                return HidHideResult.Ok;
            }
            catch (Exception deleteError)
            {
                try
                {
                    // An empty journal is safe if antivirus/file locking prevents deletion.
                    WriteLegacyJournal(Array.Empty<string>());
                    return HidHideResult.Ok;
                }
                catch (Exception writeError)
                {
                    return HidHideResult.Failure("the legacy device list was restored, but its recovery journal could not be cleared (" +
                        deleteError.Message + "; " + writeError.Message + ").");
                }
            }
        }

        private static void WriteLegacyJournal(IReadOnlyList<string> paths)
        {
            string directory = Path.GetDirectoryName(LegacyJournalPath)!;
            Directory.CreateDirectory(directory);
            string temporaryPath = LegacyJournalPath + ".tmp";
            try
            {
                File.WriteAllText(temporaryPath, JsonSerializer.Serialize(paths.ToArray()));
                File.Move(temporaryPath, LegacyJournalPath, overwrite: true);
            }
            finally
            {
                try { File.Delete(temporaryPath); }
                catch { /* the real journal is authoritative */ }
            }
        }

        private static string LegacyJournalPath => Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "DevelopmentGamepadKeyboard", "hidhide-legacy-recovery.json");

        private string LegacyReleaseGuidance() => _mode == ReservationMode.Legacy
            ? " Legacy cleanup remains journaled and will be retried on the next app launch."
            : " Exit the app to release its session claim.";

        public void Dispose()
        {
            try { Release(); }
            catch { /* session entries auto-expire; legacy entries remain journaled for next launch */ }
        }

        private static SafeFileHandle? OpenControlDevice(out string error)
        {
            var handle = CreateFile(ControlDevice, GenericRead,
                FileShareRead | FileShareWrite, IntPtr.Zero, OpenExisting, 0, IntPtr.Zero);
            if (!handle.IsInvalid)
            {
                error = string.Empty;
                return handle;
            }

            int code = Marshal.GetLastWin32Error();
            handle.Dispose();
            error = $"HidHide is unavailable ({new Win32Exception(code).Message}). Install or start HidHide, then try again.";
            return null;
        }

        private static HidHideBooleanResult ReadBoolean(SafeFileHandle handle, uint ioctl, string operation)
        {
            byte[] output = new byte[1];
            if (!DeviceIoControl(handle, ioctl, null, 0, output, output.Length, out int returned, IntPtr.Zero))
            {
                int error = Marshal.GetLastWin32Error();
                return HidHideBooleanResult.Failure(DriverError(operation, error));
            }
            if (returned < 1)
                return HidHideBooleanResult.Failure($"HidHide did not return enough data to {operation}.");
            return HidHideBooleanResult.FromValue(output[0] != 0);
        }

        private static HidHideResult ProbeSessionApi(SafeFileHandle handle)
        {
            if (DeviceIoControl(handle, IoctlClearSessionBlacklist,
                    null, 0, null, 0, out _, IntPtr.Zero))
                return HidHideResult.Ok;
            int error = Marshal.GetLastWin32Error();
            return HidHideResult.Failure(
                SessionApiError("use safe session claims", error),
                sessionApiUnsupported: IsUnsupportedSessionError(error));
        }

        private static HidHideStringsResult ReadMultiString(SafeFileHandle handle, uint ioctl, string operation)
        {
            int size = 4096;
            while (size <= MaxConfigurationBytes)
            {
                byte[] output = new byte[size];
                if (DeviceIoControl(handle, ioctl, null, 0, output, output.Length, out int returned, IntPtr.Zero))
                {
                    if (returned == 0) return HidHideStringsResult.FromValues(Array.Empty<string>());
                    int byteCount = returned - (returned % 2);
                    string text = Encoding.Unicode.GetString(output, 0, byteCount);
                    return HidHideStringsResult.FromValues(text.Split('\0', StringSplitOptions.RemoveEmptyEntries));
                }

                int error = Marshal.GetLastWin32Error();
                if (error is not ErrorInsufficientBuffer and not ErrorMoreData)
                    return HidHideStringsResult.Failure(DriverError(operation, error));
                size *= 2;
            }

            return HidHideStringsResult.Failure("HidHide returned an unexpectedly large list.");
        }

        private static HidHideResult WriteMultiString(
            SafeFileHandle handle, uint ioctl, IEnumerable<string> values, string operation)
        {
            byte[] input = EncodeMultiString(values);
            if (DeviceIoControl(handle, ioctl, input, input.Length, null, 0, out _, IntPtr.Zero))
                return HidHideResult.Ok;
            return HidHideResult.Failure(DriverError(operation, Marshal.GetLastWin32Error()));
        }

        private static byte[] EncodeMultiString(IEnumerable<string> values) =>
            Encoding.Unicode.GetBytes(string.Join("\0", values) + "\0\0");

        private static string? ToNtPath(string path)
        {
            string? root = Path.GetPathRoot(path);
            if (string.IsNullOrEmpty(root) || root.Length < 2 || root[1] != ':') return null;

            var target = new StringBuilder(1024);
            if (QueryDosDevice(root[..2], target, target.Capacity) == 0) return null;
            int separator = target.ToString().IndexOf('\0');
            string device = separator < 0 ? target.ToString() : target.ToString(0, separator);
            return device + path[root.Length..].Insert(0, "\\");
        }

        private static string SessionApiError(string operation, int error)
        {
            if (IsUnsupportedSessionError(error))
                return $"The installed HidHide driver does not support safe session claims, so it could not {operation}. Enable the legacy fallback in Settings to use its persistent device list instead.";
            return $"HidHide could not {operation} ({new Win32Exception(error).Message}).";
        }

        private static bool IsUnsupportedSessionError(int error)
        {
            const int ErrorInvalidFunction = 1;
            const int ErrorNotSupported = 50;
            const int ErrorInvalidParameter = 87;
            return error is ErrorInvalidFunction or ErrorNotSupported or ErrorInvalidParameter;
        }

        private static string DriverError(string operation, int error) =>
            $"HidHide could not {operation} ({new Win32Exception(error).Message}).";

        private static uint CtlCode(uint function) =>
            (0x8001u << 16) | (1u << 14) | (function << 2);

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern SafeFileHandle CreateFile(
            string fileName, uint desiredAccess, uint shareMode, IntPtr securityAttributes,
            uint creationDisposition, uint flagsAndAttributes, IntPtr templateFile);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool DeviceIoControl(
            SafeFileHandle device, uint ioControlCode,
            byte[]? inBuffer, int inBufferSize,
            byte[]? outBuffer, int outBufferSize,
            out int bytesReturned, IntPtr overlapped);

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern uint QueryDosDevice(string deviceName, StringBuilder targetPath, int maxLength);

        private enum ReservationMode
        {
            None,
            Session,
            Legacy
        }

    }

    public readonly record struct HidHideResult(bool Success, string? Error, bool SessionApiUnsupported)
    {
        public static HidHideResult Ok => new(true, null, false);
        public static HidHideResult Failure(string error, bool sessionApiUnsupported = false) =>
            new(false, error, sessionApiUnsupported);
    }

    internal readonly record struct HidHideBooleanResult(bool Success, bool Value, string? Error)
    {
        public static HidHideBooleanResult FromValue(bool value) => new(true, value, null);
        public static HidHideBooleanResult Failure(string error) => new(false, false, error);
        public static implicit operator HidHideResult(HidHideBooleanResult result) =>
            new(result.Success, result.Error, false);
    }

    internal readonly record struct HidHideStringsResult(bool Success, IReadOnlyList<string> Values, string? Error)
    {
        public static HidHideStringsResult FromValues(IReadOnlyList<string> values) => new(true, values, null);
        public static HidHideStringsResult Failure(string error) => new(false, Array.Empty<string>(), error);
        public static implicit operator HidHideResult(HidHideStringsResult result) =>
            new(result.Success, result.Error, false);
    }
}
