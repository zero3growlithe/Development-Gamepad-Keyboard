using System;
using System.Runtime.InteropServices;

namespace GamepadKeyboard.Native
{
    /// <summary>
    /// Self-diagnostics for the two run-mode facts that decide whether the overlay can
    /// ever sit above the Start menu. Windows grants the UIAccess flag ONLY to a signed
    /// executable in a secure location (Program Files) launched WITHOUT elevation — an
    /// elevated copy can never hold it, and without it every window lands in a normal
    /// band, i.e. below the Start menu. Both flags therefore belong in crash.log: a
    /// field report of "the overlay is still under the Start menu" is answered by
    /// reading one line instead of guessing.
    /// </summary>
    internal static class ProcessToken
    {
        private const int TokenElevationInformation = 20;
        private const int TokenUiAccessInformation = 26;
        private const uint TokenQueryAccess = 0x0008;

        [DllImport("advapi32.dll", EntryPoint = "OpenProcessToken", SetLastError = true)]
        private static extern bool OpenProcessToken(IntPtr processHandle, uint desiredAccess, out IntPtr tokenHandle);

        [DllImport("advapi32.dll", EntryPoint = "GetTokenInformation", SetLastError = true)]
        private static extern bool GetTokenInformation(
            IntPtr tokenHandle,
            int informationClass,
            out int information,
            int informationLength,
            out int returnLength);

        [DllImport("kernel32.dll", EntryPoint = "GetCurrentProcess")]
        private static extern IntPtr GetCurrentProcess();

        [DllImport("kernel32.dll", EntryPoint = "CloseHandle", SetLastError = true)]
        private static extern bool CloseHandle(IntPtr handle);

        /// <summary>True when this process's own token carries the UIAccess flag
        /// (the only thing that puts our windows above the Start menu).</summary>
        public static bool HasUiAccess()
        {
            return ReadBooleanFlag(TokenUiAccessInformation);
        }

        /// <summary>True when this process runs elevated (high integrity).
        /// Elevation and UIAccess are mutually exclusive for our purposes.</summary>
        public static bool IsElevated()
        {
            return ReadBooleanFlag(TokenElevationInformation);
        }

        /// <summary>True when the executable lives under Program Files — the "secure
        /// location" half of the UIAccess requirement.</summary>
        public static bool IsInSecureLocation()
        {
            string? path = Environment.ProcessPath;
            if (string.IsNullOrEmpty(path))
            {
                return false;
            }
            string programFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
            string programFilesX86 = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86);
            return path.StartsWith(programFiles + "\\", StringComparison.OrdinalIgnoreCase)
                || path.StartsWith(programFilesX86 + "\\", StringComparison.OrdinalIgnoreCase);
        }

        private static bool ReadBooleanFlag(int informationClass)
        {
            IntPtr tokenHandle = IntPtr.Zero;
            if (!OpenProcessToken(GetCurrentProcess(), TokenQueryAccess, out tokenHandle))
            {
                return false;
            }
            try
            {
                int value;
                int returnedLength;
                // TokenElevation and TokenUIAccess both return a single DWORD.
                if (!GetTokenInformation(tokenHandle, informationClass, out value, sizeof(int), out returnedLength))
                {
                    return false;
                }
                return value != 0;
            }
            finally
            {
                CloseHandle(tokenHandle);
            }
        }
    }
}
