using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.Json;

namespace GamepadKeyboard.Input
{
    public sealed record HidHideGamingDevice(string Name, IReadOnlyList<string> InstancePaths);

    /// <summary>Read-only discovery through HidHide's installed command-line client.</summary>
    public static class HidHideDeviceCatalog
    {
        public static IReadOnlyList<HidHideGamingDevice> Load()
        {
            string cli = FindInstalledFile("HidHideCLI.exe") ??
                throw new InvalidOperationException("HidHideCLI.exe was not found. Install a current version of HidHide first.");

            using var process = new Process
            {
                StartInfo = new ProcessStartInfo
                {
                    FileName = cli,
                    Arguments = "--dev-gaming",
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true
                }
            };

            process.Start();
            string output = process.StandardOutput.ReadToEnd();
            string error = process.StandardError.ReadToEnd();
            process.WaitForExit();
            if (process.ExitCode != 0)
                throw new InvalidOperationException(string.IsNullOrWhiteSpace(error)
                    ? $"HidHideCLI exited with code {process.ExitCode}."
                    : error.Trim());

            int start = output.IndexOf('[');
            int end = output.LastIndexOf(']');
            if (start < 0 || end < start)
                throw new InvalidOperationException("HidHideCLI returned an unexpected device list.");

            using var document = JsonDocument.Parse(output[start..(end + 1)]);
            var result = new List<HidHideGamingDevice>();
            foreach (var group in document.RootElement.EnumerateArray())
            {
                string name = ReadString(group, "friendlyName") ?? "Gaming device";
                var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                if (group.TryGetProperty("devices", out var devices) && devices.ValueKind == JsonValueKind.Array)
                {
                    foreach (var device in devices.EnumerateArray())
                    {
                        if (ReadBool(device, "present") == false || ReadBool(device, "gamingDevice") == false)
                            continue;
                        AddPath(paths, ReadString(device, "deviceInstancePath"));
                        AddPath(paths, ReadString(device, "xusbDeviceInstancePath"));
                    }
                }
                if (paths.Count > 0)
                    result.Add(new HidHideGamingDevice(name, paths.OrderBy(path => path).ToArray()));
            }
            return result;
        }

        public static bool TryOpenConfiguration(out string error)
        {
            string? client = FindInstalledFile("HidHideClient.exe");
            if (client == null)
            {
                error = "HidHide Configuration Client was not found. Install a current version of HidHide first.";
                return false;
            }
            try
            {
                Process.Start(new ProcessStartInfo(client) { UseShellExecute = true });
                error = string.Empty;
                return true;
            }
            catch (Exception ex)
            {
                error = "Could not open HidHide Configuration Client: " + ex.Message;
                return false;
            }
        }

        private static string? FindInstalledFile(string fileName)
        {
            string programFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
            string[] candidates =
            {
                Path.Combine(programFiles, "Nefarius Software Solutions", "HidHide", fileName),
                Path.Combine(programFiles, "Nefarius Software Solutions", "HidHide", "x64", fileName),
                Path.Combine(programFiles, "Nefarius Software Solutions e.U", "HidHide", fileName),
                Path.Combine(programFiles, "Nefarius Software Solutions e.U", "HidHide", "x64", fileName),
                Path.Combine(programFiles, "Nefarius Software Solutions e.U", "HidHideClient", fileName),
                Path.Combine(programFiles, "Nefarius Software Solutions e.U", "HidHideCLI", fileName)
            };
            return candidates.FirstOrDefault(File.Exists);
        }

        private static void AddPath(HashSet<string> paths, string? path)
        {
            if (!string.IsNullOrWhiteSpace(path)) paths.Add(path.Trim());
        }

        private static string? ReadString(JsonElement element, string property) =>
            element.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String
                ? value.GetString()
                : null;

        private static bool ReadBool(JsonElement element, string property) =>
            element.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.True;
    }
}
