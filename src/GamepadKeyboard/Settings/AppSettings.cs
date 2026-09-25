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
        private static readonly object SaveLock = new();
        private static string? _lastSavedJson;

        public int SettingsVersion { get; set; } = 4;

        public double KeySpacing { get; set; } = 6.0;
        public double StickDeadzone { get; set; } = 0.005;
        public double MouseStickDeadzone { get; set; } = 0.005;   // separate deadzone for mouse mode
        public double OverlayScale { get; set; } = 1.0;
        public double OverlayMoveSpeed { get; set; } = 6.0;
        public double AnalogStickCurveExponent { get; set; } = 1.0;
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
        public bool AlwaysShowKeyboardAtCursorPosition { get; set; } = false;
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
        public bool HidHideLegacyFallbackEnabled { get; set; } = false;
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
            MouseProfiles.Count == 0 ? new MouseProfile() : MouseProfiles[Math.Clamp(ActiveMouseProfile, 0, MouseProfiles.Count - 1)];

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
                        loaded.Normalize();
                        Instance = loaded;
                        _lastSavedJson = json;
                    }
                }
            }
            catch { /* corrupted settings -> defaults */ }
        }

        public static void Save()
        {
            lock (SaveLock)
            {
                try
                {
                    string json = JsonSerializer.Serialize(Instance, JsonOpts);
                    if (string.Equals(json, _lastSavedJson, StringComparison.Ordinal)) return;
                    File.WriteAllText(FilePath, json);
                    _lastSavedJson = json;
                }
                catch { /* non-fatal */ }
            }
        }

        private static readonly JsonSerializerOptions JsonOpts = new()
        {
            WriteIndented = true,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
        };

        private void Normalize()
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
            foreach (var profile in KeyboardProfiles)
            {
                profile.ComboBindings ??= new List<CustomComboBinding>();
                NormalizeCombos(profile.ComboBindings);
            }
            foreach (var profile in MouseProfiles)
            {
                profile.ComboBindings ??= new List<CustomComboBinding>();
                NormalizeCombos(profile.ComboBindings);
            }
            if (StickPointsProfiles.Count == 0) StickPointsProfiles.Add(new StickPointsProfile());

            ActiveProfile = Math.Clamp(ActiveProfile, 0, KeyboardProfiles.Count - 1);
            ActiveMouseProfile = Math.Clamp(ActiveMouseProfile, 0, MouseProfiles.Count - 1);
            ActiveStickPointsProfile = Math.Clamp(ActiveStickPointsProfile, 0, StickPointsProfiles.Count - 1);
            if (!double.IsFinite(AnalogStickCurveExponent) || AnalogStickCurveExponent <= 0)
                AnalogStickCurveExponent = 1.0;
            AnalogStickCurveExponent = Math.Clamp(AnalogStickCurveExponent, 0.1, 5.0);
            SettingsVersion = 4;
        }

        private static void NormalizeCombos(List<CustomComboBinding>? combos)
        {
            if (combos == null) return;
            foreach (var combo in combos)
            {
                combo.Buttons ??= new List<string>();
                if (string.IsNullOrWhiteSpace(combo.Id)) combo.Id = Guid.NewGuid().ToString("N");
                combo.Action ??= "None";
            }
        }
    }

    /// <summary>
    /// One keyboard-mode button mapping profile. Stick origins and ray behavior
    /// live in independent <see cref="StickPointsProfile"/> instances.
    /// </summary>
    public sealed class KeyboardProfile
    {
        public string Name { get; set; } = "Default";

        // ── button mappings (physical pad -> action name) ────────────────────
        public ButtonBinding A { get; set; } = new("Space");
        public ButtonBinding B { get; set; } = new("MouseMode");
        public ButtonBinding X { get; set; } = new("Tab");
        public ButtonBinding Y { get; set; } = new("None");

        /// <summary>Default submit buttons for the independently highlighted cursors.</summary>
        public ButtonBinding LB { get; set; } = new("SubmitLeft");
        public ButtonBinding RB { get; set; } = new("SubmitRight");

        /// <summary>Hold-type modifier (tap = toggle): HoldShift / HoldCtrl / HoldAlt / HoldWin, or any action.</summary>
        public ButtonBinding LT { get; set; } = new("HoldShift");
        public ButtonBinding RT { get; set; } = new("HoldCtrl");
        public ButtonBinding LS { get; set; } = new("ToggleMoveScaleKeyboard");
        public ButtonBinding RS { get; set; } = new("None");

        public ButtonBinding View { get; set; } = new("DisableInput");
        public ButtonBinding Menu { get; set; } = new("ToggleKeyboardMouseMode");

        public ButtonBinding DUp { get; set; } = new("ArrowUp");
        public ButtonBinding DDown { get; set; } = new("ArrowDown");
        public ButtonBinding DLeft { get; set; } = new("ArrowLeft");
        public ButtonBinding DRight { get; set; } = new("ArrowRight");

        /// <summary>Structured multi-button bindings; the final gamepad button triggers.</summary>
        public List<CustomComboBinding> ComboBindings { get; set; } = new()
        {
            new(new[] { "LS", "RS", "LB", "RB" }, "EnableInput")
        };
    }

    public sealed class MouseProfile
    {
        public string Name { get; set; } = "Default";

        public ButtonBinding A { get; set; } = new("LeftClick");
        public ButtonBinding B { get; set; } = new("RightClick");
        public ButtonBinding X { get; set; } = new("MiddleClick");
        public ButtonBinding Y { get; set; } = new("KeyboardMode");

        public ButtonBinding LB { get; set; } = new("LeftClick");
        public ButtonBinding RB { get; set; } = new("RightClick");
        public ButtonBinding LT { get; set; } = new("MiddleClick");
        public ButtonBinding RT { get; set; } = new("SpeedBoost");

        public ButtonBinding DUp { get; set; } = new("ScrollUp");
        public ButtonBinding DDown { get; set; } = new("ScrollDown");
        public ButtonBinding DLeft { get; set; } = new("ScrollLeft");
        public ButtonBinding DRight { get; set; } = new("ScrollRight");

        public ButtonBinding LS { get; set; } = new("None");
        public ButtonBinding RS { get; set; } = new("None");

        public ButtonBinding View { get; set; } = new("DisableInput");
        public ButtonBinding Menu { get; set; } = new("ToggleKeyboardMouseMode");

        // Analog directions can run continuous mouse/scroll actions or any
        // digital action (including held keyboard keys).
        public ButtonBinding LUp { get; set; } = new("AnalogScrollDown");
        public ButtonBinding LDown { get; set; } = new("AnalogScrollUp");
        public ButtonBinding LLeft { get; set; } = new("AnalogScrollLeft");
        public ButtonBinding LRight { get; set; } = new("AnalogScrollRight");
        public ButtonBinding RUp { get; set; } = new("MouseMoveUp");
        public ButtonBinding RDown { get; set; } = new("MouseMoveDown");
        public ButtonBinding RLeft { get; set; } = new("MouseMoveLeft");
        public ButtonBinding RRight { get; set; } = new("MouseMoveRight");

        /// <summary>Structured multi-button bindings; the final gamepad button triggers.</summary>
        public List<CustomComboBinding> ComboBindings { get; set; } = new()
        {
            new(new[] { "LS", "RS", "LB", "RB" }, "EnableInput")
        };
    }

    public sealed class ButtonBinding
    {
        public string Action { get; set; } = "None";
        public bool Modifier { get; set; }

        public ButtonBinding() { }
        public ButtonBinding(string action, bool modifier = false)
        {
            Action = action;
            Modifier = modifier;
        }
    }

    public sealed class CustomComboBinding
    {
        public string Id { get; set; } = Guid.NewGuid().ToString("N");
        public List<string> Buttons { get; set; } = new();
        public string Action { get; set; } = "None";
        public bool HoldLast { get; set; }
        public bool Ctrl { get; set; }
        public bool Shift { get; set; }
        public bool Alt { get; set; }

        public CustomComboBinding() { }
        public CustomComboBinding(IEnumerable<string> buttons, string action)
        {
            Buttons = buttons.ToList();
            Action = action;
        }
    }

    /// <summary>Independent geometry profile used by both keyboard mapping profiles.</summary>
    public sealed class StickPointsProfile
    {
        public string Name { get; set; } = "Default";
        public double LeftX { get; set; } = 0.32;
        public double LeftY { get; set; } = 0.553;
        public double RightX { get; set; } = 0.51;
        public double RightY { get; set; } = 0.53;
        public double LeftRayLength { get; set; } = 0.4;
        public double RightRayLength { get; set; } = 0.4;
    }
}
