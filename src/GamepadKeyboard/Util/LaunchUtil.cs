using System;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;

namespace GamepadKeyboard.Util
{
    /// <summary>
    /// Autostart ("Run on Windows startup") and elevation helpers.
    /// Autostart uses the Startup shell folder (no registry), elevation uses
    /// ShellExecute "runas" verb. Admin-launch persistence uses Task Scheduler
    /// (schtasks) so no UAC prompt appears on every login.
    /// </summary>
    public static class LaunchUtil
    {
        private const string TaskName = "DevelopmentGamepadKeyboard";

        public static bool IsAdmin()
        {
            using var identity = System.Security.Principal.WindowsIdentity.GetCurrent();
            var principal = new System.Security.Principal.WindowsPrincipal(identity);
            return principal.IsInRole(System.Security.Principal.WindowsBuiltInRole.Administrator);
        }

        public static bool RestartElevated()
        {
            var exe = Environment.ProcessPath;
            if (string.IsNullOrEmpty(exe)) return false;
            try
            {
                return Process.Start(new ProcessStartInfo
                {
                    FileName = exe,
                    Arguments = "--elevated-restart",
                    UseShellExecute = true,
                    Verb = "runas"
                }) != null;
            }
            catch { return false; /* user cancelled UAC */ }
        }

        public static void RestartAsUser()
        {
            var exe = Environment.ProcessPath;
            if (string.IsNullOrEmpty(exe)) return;
            try
            {
                // relaunch non-elevated via explorer to shed the admin token
                Process.Start(new ProcessStartInfo
                {
                    FileName = "explorer.exe",
                    Arguments = $"\"{exe}\"",
                    UseShellExecute = true
                });
            }
            catch { }
        }

        public static bool StartupShortcutExists()
        {
            string link = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.Startup),
                "Development Gamepad Keyboard.lnk");
            return File.Exists(link);
        }

        public static void CreateStartupShortcut()
        {
            string link = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.Startup),
                "Development Gamepad Keyboard.lnk");
            string exe = Environment.ProcessPath ?? "";
            try
            {
                // PowerShell creates a .lnk without needing IWshRuntimeLibrary COM interop
                var psi = new ProcessStartInfo
                {
                    FileName = "powershell.exe",
                    Arguments = $"-NoProfile -WindowStyle Hidden -Command \"$ws = New-Object -ComObject WScript.Shell; $s = $ws.CreateShortcut('{link}'); $s.TargetPath = '{exe}'; $s.Save()\"",
                    UseShellExecute = true,
                    CreateNoWindow = true
                };
                Process.Start(psi)?.WaitForExit(5000);
            }
            catch { }
        }

        public static void RemoveStartupShortcut()
        {
            string link = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.Startup),
                "Development Gamepad Keyboard.lnk");
            try { File.Delete(link); } catch { }
        }

        /// <summary>
        /// Creates or deletes a highest-privileges scheduled task that launches
        /// the app at logon without a UAC prompt (only meaningful when the app
        /// should start elevated).
        /// </summary>
        public static void SetAdminStartup(bool enabled)
        {
            string exe = Environment.ProcessPath ?? "";
            try
            {
                if (enabled)
                {
                    Run($"schtasks /Create /F /RL HIGHEST /SC ONLOGON /TN \"{TaskName}\" /TR \"\\\"{exe}\\\"\"");
                }
                else
                {
                    Run($"schtasks /Delete /F /TN \"{TaskName}\"");
                }
            }
            catch { }
        }

        public static bool AdminStartupExists()
        {
            var (output, code) = Run($"schtasks /Query /TN \"{TaskName}\"");
            return code == 0;
        }

        private static (string output, int exitCode) Run(string arguments)
        {
            var psi = new ProcessStartInfo
            {
                FileName = "cmd.exe",
                Arguments = $"/c {arguments}",
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };
            using var p = Process.Start(psi);
            if (p == null) return ("", -1);
            string output = p.StandardOutput.ReadToEnd();
            p.WaitForExit(10000);
            return (output, p.ExitCode);
        }
    }
}
