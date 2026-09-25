using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace GamepadKeyboard.Native
{
    /// <summary>
    /// Uses HidHide's process-owned, in-memory blacklist. These claims disappear
    /// automatically when this process exits, even after a crash. No persistent
    /// HidHide device or application configuration is changed here.
    /// </summary>
    public sealed class HidHideSession : IDisposable
    {
        private const string ControlDevice = @"\\.\HidHide";
        private const uint GenericRead = 0x80000000;
        private const uint OpenExisting = 3;
        private const int ErrorInsufficientBuffer = 122;
        private const int ErrorMoreData = 234;
        private const int MaxConfigurationBytes = 1024 * 1024;

        private static readonly uint IoctlGetWhitelist = CtlCode(2048);
        private static readonly uint IoctlGetActive = CtlCode(2052);
        private static readonly uint IoctlGetInverse = CtlCode(2054);
        private static readonly uint IoctlAddSessionBlacklist = CtlCode(2056);
        private static readonly uint IoctlClearSessionBlacklist = CtlCode(2057);

        private bool _claimed;

        public bool IsClaimed => _claimed;

        public HidHideResult Claim(IEnumerable<string> deviceInstancePaths)
        {
            var paths = deviceInstancePaths
                .Where(path => !string.IsNullOrWhiteSpace(path))
                .Select(path => path.Trim())
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();

            if (paths.Length == 0)
                return HidHideResult.Failure("HidHide is enabled, but no controller is selected.");

            if (_claimed)
            {
                var released = Release();
                if (!released.Success) return released;
            }

            using var handle = OpenControlDevice(out var openError);
            if (handle == null)
                return HidHideResult.Failure(openError);

            var sessionApi = ProbeSessionApi(handle);
            if (!sessionApi.Success) return sessionApi;

            var active = ReadBoolean(handle, IoctlGetActive, "read HidHide's active state");
            if (!active.Success) return active;
            if (!active.Value)
                return HidHideResult.Failure("HidHide device hiding is not active. Enable it in HidHide Configuration Client.");

            var inverse = ReadBoolean(handle, IoctlGetInverse, "read HidHide's inverse-cloak state");
            if (!inverse.Success) return inverse;
            if (inverse.Value)
                return HidHideResult.Failure("HidHide inverse cloak is enabled. Turn it off before using session-only controller hiding.");

            var whitelist = ReadMultiString(handle, IoctlGetWhitelist, "read HidHide's application list");
            if (!whitelist.Success) return whitelist;

            string? processPath = Environment.ProcessPath;
            string? ntProcessPath = processPath == null ? null : ToNtPath(processPath);
            bool applicationAllowed = whitelist.Values.Any(path =>
                string.Equals(path, processPath, StringComparison.OrdinalIgnoreCase) ||
                (ntProcessPath != null && string.Equals(path, ntProcessPath, StringComparison.OrdinalIgnoreCase)));
            if (!applicationAllowed)
                return HidHideResult.Failure("Add this app to HidHide's Applications list before enabling controller reservation.");

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

            _claimed = true;
            return HidHideResult.Ok;
        }

        public HidHideResult Release()
        {
            if (!_claimed) return HidHideResult.Ok;

            using var handle = OpenControlDevice(out var openError);
            if (handle == null)
                return HidHideResult.Failure(openError + " Exit the app to release its session claim.");

            if (!DeviceIoControl(handle, IoctlClearSessionBlacklist,
                    null, 0, null, 0, out _, IntPtr.Zero))
            {
                int error = Marshal.GetLastWin32Error();
                return HidHideResult.Failure(SessionApiError("release the controller", error) +
                                             " Exit the app to release its session claim.");
            }

            _claimed = false;
            return HidHideResult.Ok;
        }

        public void Dispose()
        {
            try { Release(); }
            catch { /* process-owned entries are also removed by the driver on exit */ }
        }

        private static SafeFileHandle? OpenControlDevice(out string error)
        {
            var handle = CreateFile(ControlDevice, GenericRead,
                0, IntPtr.Zero, OpenExisting, 0, IntPtr.Zero);
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
                return HidHideBooleanResult.Failure(SessionApiError(operation, error));
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
            return HidHideResult.Failure(SessionApiError("use safe session claims", error));
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
                    return HidHideStringsResult.Failure(SessionApiError(operation, error));
                size *= 2;
            }

            return HidHideStringsResult.Failure("HidHide's application list is unexpectedly large.");
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
            const int ErrorInvalidFunction = 1;
            const int ErrorNotSupported = 50;
            const int ErrorInvalidParameter = 87;
            if (error is ErrorInvalidFunction or ErrorNotSupported or ErrorInvalidParameter)
                return $"The installed HidHide driver does not support safe session claims, so it could not {operation}. Update HidHide; no persistent fallback was used.";
            return $"HidHide could not {operation} ({new Win32Exception(error).Message}).";
        }

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
    }

    public readonly record struct HidHideResult(bool Success, string? Error)
    {
        public static HidHideResult Ok => new(true, null);
        public static HidHideResult Failure(string error) => new(false, error);
    }

    internal readonly record struct HidHideBooleanResult(bool Success, bool Value, string? Error)
    {
        public static HidHideBooleanResult FromValue(bool value) => new(true, value, null);
        public static HidHideBooleanResult Failure(string error) => new(false, false, error);
        public static implicit operator HidHideResult(HidHideBooleanResult result) =>
            new(result.Success, result.Error);
    }

    internal readonly record struct HidHideStringsResult(bool Success, IReadOnlyList<string> Values, string? Error)
    {
        public static HidHideStringsResult FromValues(IReadOnlyList<string> values) => new(true, values, null);
        public static HidHideStringsResult Failure(string error) => new(false, Array.Empty<string>(), error);
        public static implicit operator HidHideResult(HidHideStringsResult result) =>
            new(result.Success, result.Error);
    }
}
