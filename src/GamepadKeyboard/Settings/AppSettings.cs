using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace GamepadKeyboard.Settings
{
    public sealed class AppSettings
    {
        public static AppSettings Instance { get; private set; } = new();

        public int SettingsVersion { get; set; } = 2;

        public double KeySpacing { get; set; } = 6.0;
        public double StickDeadzone { get; set; } = 0.005;
        public double MouseStickDeadzone { get; set; } = 0.005;   // separate deadzone for mouse mode
        public double OverlayScale { get; set; } = 1.0;
        public double OverlayMoveSpeed { get; set; } = 6.0;
        public double OverlayLeft { get; set; } = 100;
        public double OverlayTop { get; set; } = 100;
        public double MouseSpeed { get; set; } = 12.0;
        public double MouseSpeedBoostMultiplier { get; set; } = 2.5;
        public double ScrollSpeed { get; set; } = 3.0;
        public bool InvertVerticalScroll { get; set; } = false;
        public bool InvertHorizontalScroll { get; set; } = false;
        public bool CursorLagEnabled { get; set; } = false;
        public double CursorLagSeconds { get; set; } = 0.15;
        public bool HideRaysInCursorLag { get; set; } = false;
        public bool FreeCursorEnabled { get; set; } = false;
        public double FreeCursorSpeed { get; set; } = 420.0;
        public bool HideCenterPointsAndRaysInFreeCursor { get; set; } = false;
        public double PointEditStickSpeed { get; set; } = 0.8;
        public bool ShowOverlay { get; set; } = true;
        public bool RunAsAdminOnLaunch { get; set; } = false;
        public bool RunOnStartup { get; set; } = false;
        public bool ShowButtonLegend { get; set; } = true;
        public double LegendOpacity { get; set; } = 0.85;
        public int LegendFontSize { get; set; } = 13;
        public double LegendLeft { get; set; } = 40;
        public double LegendTop { get; set; } = 40;
        public int ProfileToastSeconds { get; set; } = 3;
        public bool ProfileToastPermanent { get; set; } = false;
        public bool StartInMouseMode { get; set; } = true;
        public bool HidHideSessionEnabled { get; set; } = false;
        public List<string> HidHideDeviceInstancePaths { get; set; } = new();
        public int ActiveProfile { get; set; } = 0;
        public int ActiveMouseProfile { get; set; } = 0;
        public int ActiveStickPointsProfile { get; set; } = 0;
        public bool AdminLaunch { get; set; } = false;

        public List<KeyboardProfile> KeyboardProfiles { get; set; } = new()
        {
            new KeyboardProfile()
        };

        public List<MouseProfile> MouseProfiles { get; set; } = new()
        {
            new MouseProfile()
        };

        public List<StickPointsProfile> StickPointsProfiles { get; set; } = new()
        {
            new StickPointsProfile()
        };

        [JsonIgnore]
        public KeyboardProfile Profile =>
            KeyboardProfiles.Count == 0 ? new KeyboardProfile() : KeyboardProfiles[Math.Clamp(ActiveProfile, 0, KeyboardProfiles.Count - 1)];

        [JsonIgnore]
        public MouseProfile MouseProfile =>
            MouseProfiles.Count == 0 ? new MouseProfile() : MouseProfiles[Math.Min(ActiveMouseProfile, MouseProfiles.Count - 1)];

        [JsonIgnore]
        public StickPointsProfile StickPointsProfile =>
            StickPointsProfiles.Count == 0 ? new StickPointsProfile() : StickPointsProfiles[Math.Clamp(ActiveStickPointsProfile, 0, StickPointsProfiles.Count - 1)];

        public static string FilePath
        {
            get
            {
                string dir = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                    "DevelopmentGamepadKeyboard");
                Directory.CreateDirectory(dir);
                return Path.Combine(dir, "settings.json");
            }
        }

        public static void Load()
        {
            try
            {
                if (File.Exists(FilePath))
                {
                    string json = File.ReadAllText(FilePath);
                    var loaded = JsonSerializer.Deserialize<AppSettings>(json, JsonOpts);
                    if (loaded != null)
                    {
                        using var document = JsonDocument.Parse(json);
                        var root = document.RootElement;
                        bool hasStickProfiles = root.TryGetProperty(nameof(StickPointsProfiles), out _);
                        int settingsVersion = root.TryGetProperty(nameof(SettingsVersion), out var versionElement)
                            && versionElement.TryGetInt32(out var version) ? version : 0;
                        loaded.Normalize(root, hasStickProfiles, settingsVersion);
                        Instance = loaded;
                    }
                }
            }
            catch { /* corrupted settings -> defaults */ }
        }

        public static void Save()
        {
            try
            {
                File.WriteAllText(FilePath, JsonSerializer.Serialize(Instance, JsonOpts));
            }
            catch { /* non-fatal */ }
        }

        private static readonly JsonSerializerOptions JsonOpts = new()
        {
            WriteIndented = true,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
        };

        private void Normalize(JsonElement root, bool hasStickProfiles, int settingsVersion)
        {
            KeyboardProfiles ??= new List<KeyboardProfile>();
            MouseProfiles ??= new List<MouseProfile>();
            StickPointsProfiles ??= new List<StickPointsProfile>();
            HidHideDeviceInstancePaths ??= new List<string>();
            HidHideDeviceInstancePaths = HidHideDeviceInstancePaths
                .Where(path => !string.IsNullOrWhiteSpace(path))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
            if (KeyboardProfiles.Count == 0) KeyboardProfiles.Add(new KeyboardProfile());
            if (MouseProfiles.Count == 0) MouseProfiles.Add(new MouseProfile());
            foreach (var profile in KeyboardProfiles) profile.ComboBindings ??= new List<string>();
            foreach (var profile in MouseProfiles) profile.ComboBindings ??= new List<string>();

            if (!hasStickProfiles)
            {
                StickPointsProfiles.Clear();
                if (root.TryGetProperty(nameof(KeyboardProfiles), out var profiles) && profiles.ValueKind == JsonValueKind.Array)
                {
                    foreach (var old in profiles.EnumerateArray())
                    {
                        var points = new StickPointsProfile
                        {
                            Name = ReadString(old, nameof(StickPointsProfile.Name), "Default"),
                            LeftX = ReadDouble(old, nameof(StickPointsProfile.LeftX), 0.32),
                            LeftY = ReadDouble(old, nameof(StickPointsProfile.LeftY), 0.553),
                            RightX = ReadDouble(old, nameof(StickPointsProfile.RightX), 0.51),
                            RightY = ReadDouble(old, nameof(StickPointsProfile.RightY), 0.53),
                            CurveExponent = ReadDouble(old, nameof(StickPointsProfile.CurveExponent), 1.0),
                            LeftRayLength = ReadDouble(old, nameof(StickPointsProfile.LeftRayLength), 0.4),
                            RightRayLength = ReadDouble(old, nameof(StickPointsProfile.RightRayLength), 0.4)
                        };
                        StickPointsProfiles.Add(points);
                    }
                }
            }
            if (StickPointsProfiles.Count == 0) StickPointsProfiles.Add(new StickPointsProfile());

            ActiveProfile = Math.Clamp(ActiveProfile, 0, KeyboardProfiles.Count - 1);
            ActiveMouseProfile = Math.Clamp(ActiveMouseProfile, 0, MouseProfiles.Count - 1);
            ActiveStickPointsProfile = hasStickProfiles
                ? Math.Clamp(ActiveStickPointsProfile, 0, StickPointsProfiles.Count - 1)
                : Math.Clamp(ActiveProfile, 0, StickPointsProfiles.Count - 1);

            if (settingsVersion < 1)
                MigrateLegacyBindings();
            if (settingsVersion < 2)
                MigrateMoveScaleAction();
            SettingsVersion = 2;
        }

        private void MigrateLegacyBindings()
        {
            foreach (var profile in KeyboardProfiles)
            {
                if (profile.B == "Backspace") profile.B = "MouseMode";
                if (profile.LB == "LeftClick") profile.LB = "SubmitLeft";
                if (profile.RB == "RightClick") profile.RB = "SubmitRight";
                if (profile.LS == "ToggleAlt") profile.LS = "ToggleMoveMode";
                if (profile.RS == "None") profile.RS = "ToggleScaleMode";
                AddEnableBinding(profile.ComboBindings);
            }
            foreach (var profile in MouseProfiles)
            {
                if (profile.Y == "ToggleKeyboard") profile.Y = "KeyboardMode";
                AddEnableBinding(profile.ComboBindings);
            }
        }

        private static void AddEnableBinding(List<string> bindings)
        {
            const string enableBinding = "LS+RS+LB+RB=EnableInput";
            if (!bindings.Contains(enableBinding)) bindings.Add(enableBinding);
        }

        private void MigrateMoveScaleAction()
        {
            foreach (var profile in KeyboardProfiles)
            {
                bool defaultMove = profile.LS == "ToggleMoveMode";
                bool defaultScale = profile.RS == "ToggleScaleMode";
                if (defaultMove) profile.LS = "ToggleMoveScaleKeyboard";
                if (defaultScale) profile.RS = defaultMove ? "None" : "ToggleMoveScaleKeyboard";
                ReplaceComboAction(profile.ComboBindings, "ToggleMoveMode", "ToggleMoveScaleKeyboard");
                ReplaceComboAction(profile.ComboBindings, "ToggleScaleMode", "ToggleMoveScaleKeyboard");
            }
            foreach (var profile in MouseProfiles)
            {
                ReplaceComboAction(profile.ComboBindings, "ToggleMoveMode", "ToggleMoveScaleKeyboard");
                ReplaceComboAction(profile.ComboBindings, "ToggleScaleMode", "ToggleMoveScaleKeyboard");
            }
        }

        private static void ReplaceComboAction(List<string> bindings, string oldAction, string newAction)
        {
            for (int i = 0; i < bindings.Count; i++)
            {
                int equals = bindings[i].IndexOf('=');
                if (equals > 0 && bindings[i][(equals + 1)..].Trim() == oldAction)
                    bindings[i] = bindings[i][..(equals + 1)] + newAction;
            }
        }

        private static double ReadDouble(JsonElement element, string name, double fallback) =>
            element.TryGetProperty(name, out var value) && value.TryGetDouble(out var number) ? number : fallback;

        private static string ReadString(JsonElement element, string name, string fallback) =>
            element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
                ? value.GetString() ?? fallback
                : fallback;
    }

    /// <summary>
    /// One keyboard-mode button mapping profile. Stick origins and ray behavior
    /// live in independent <see cref="StickPointsProfile"/> instances.
    /// </summary>
    public sealed class KeyboardProfile
    {
        public string Name { get; set; } = "Default";

        // ── button mappings (physical pad -> action name) ────────────────────
        public string A { get; set; } = "Space";
        public string B { get; set; } = "MouseMode";
        public string X { get; set; } = "Tab";
        public string Y { get; set; } = "None";

        /// <summary>Default submit buttons for the independently highlighted cursors.</summary>
        public string LB { get; set; } = "SubmitLeft";
        public string RB { get; set; } = "SubmitRight";

        /// <summary>Hold-type modifier (tap = toggle): HoldShift / HoldCtrl / HoldAlt / HoldWin, or any action.</summary>
        public string LT { get; set; } = "HoldShift";
        public string RT { get; set; } = "HoldCtrl";
        public string LS { get; set; } = "ToggleMoveScaleKeyboard";
        public string RS { get; set; } = "None";

        public string View { get; set; } = "DisableInput";
        public string Menu { get; set; } = "ToggleKeyboardMouseMode";

        public string DUp { get; set; } = "ArrowUp";
        public string DDown { get; set; } = "ArrowDown";
        public string DLeft { get; set; } = "ArrowLeft";
        public string DRight { get; set; } = "ArrowRight";

        /// <summary>D-pad layer while Y is held (navigation).</summary>
        public string YDUp { get; set; } = "None";
        public string YDDown { get; set; } = "None";
        public string YDLeft { get; set; } = "None";
        public string YDRight { get; set; } = "None";

        /// <summary>
        /// Custom combo bindings: "A+B+X=Action" — all buttons held, the LAST one's
        /// press edge triggers the action (earlier buttons are modifiers).
        /// </summary>
        public List<string> ComboBindings { get; set; } = new()
        {
            "LS+RS+LB+RB=EnableInput"
        };
    }

    public sealed class MouseProfile
    {
        public string Name { get; set; } = "Default";

        public string A { get; set; } = "LeftClick";
        public string B { get; set; } = "RightClick";
        public string X { get; set; } = "MiddleClick";
        public string Y { get; set; } = "KeyboardMode";

        public string LB { get; set; } = "LeftClick";
        public string RB { get; set; } = "RightClick";
        public string LT { get; set; } = "MiddleClick";
        public string RT { get; set; } = "SpeedBoost";

        public string DUp { get; set; } = "ScrollUp";
        public string DDown { get; set; } = "ScrollDown";
        public string DLeft { get; set; } = "ScrollLeft";
        public string DRight { get; set; } = "ScrollRight";

        public string LS { get; set; } = "None";
        public string RS { get; set; } = "None";

        public string View { get; set; } = "DisableInput";
        public string Menu { get; set; } = "ToggleKeyboardMouseMode";

        /// <summary>Custom combo bindings: "A+B=Action" (mods held, last press triggers).</summary>
        public List<string> ComboBindings { get; set; } = new()
        {
            "LS+RS+LB+RB=EnableInput"
        };
    }

    /// <summary>Independent geometry profile used by both keyboard mapping profiles.</summary>
    public sealed class StickPointsProfile
    {
        public string Name { get; set; } = "Default";
        public double LeftX { get; set; } = 0.32;
        public double LeftY { get; set; } = 0.553;
        public double RightX { get; set; } = 0.51;
        public double RightY { get; set; } = 0.53;
        public double CurveExponent { get; set; } = 1.0;
        public double LeftRayLength { get; set; } = 0.4;
        public double RightRayLength { get; set; } = 0.4;
    }
}
