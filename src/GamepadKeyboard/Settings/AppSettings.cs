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

        public int SettingsVersion { get; set; } = 5;

        public double KeySpacing { get; set; } = 1.0;
        public double StickDeadzone { get; set; } = 0.1;
        public double MouseStickDeadzone { get; set; } = 0.1;   // separate deadzone for mouse mode
        public double OverlayScale { get; set; } = 1.0;
        public double OverlayMoveSpeed { get; set; } = 6.0;
        public double AnalogStickCurveExponent { get; set; } = 1.0;
        public double OverlayLeft { get; set; } = 100;
        public double OverlayTop { get; set; } = 100;
        public double MouseSpeed { get; set; } = 8.0;
        public double MouseSpeedBoostMultiplier { get; set; } = 2;
        public double ScrollSpeed { get; set; } = 1.0;
        public bool CursorLagEnabled { get; set; } = false;
        public double CursorLagSeconds { get; set; } = 0.2;
        public bool HideRaysInCursorLag { get; set; } = false;
        public bool FreeCursorEnabled { get; set; } = true;
        public double FreeCursorSpeed { get; set; } = 600.0;
        public bool HideCenterPointsAndRaysInFreeCursor { get; set; } = false;
        public double PointEditStickSpeed { get; set; } = 0.8;
        public bool ShowOverlay { get; set; } = true;
        public bool AlwaysShowKeyboardAtCursorPosition { get; set; } = true;
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

        public KeyMapsSettings KeyMaps { get; set; } = new();

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
                        if (!_warnedEmptyBindings && EmptyBothBindingLists(loaded))
                        {
                            // Schema drift or an emptied file can leave BOTH profiles
                            // without a single binding row — dispatch then has nothing
                            // to do and the app looks completely dead. One-time
                            // in-memory fallback to factory defaults; the user's
                            // settings.json is never rewritten or deleted by this.
                            _warnedEmptyBindings = true;
                            App.Log("settings: empty bindings after load -> factory defaults");
                            loaded = new AppSettings();
                        }
                        Instance = loaded;
                        _lastSavedJson = json;
                    }
                }
            }
            catch { /* corrupted settings -> defaults */ }
        }

        private static bool _warnedEmptyBindings;

        /// <summary>True when neither profile carries a single binding row.</summary>
        private static bool EmptyBothBindingLists(AppSettings s) =>
            !s.KeyboardProfiles.Any(p => p.Bindings.Count > 0) &&
            !s.MouseProfiles.Any(p => p.Bindings.Count > 0);

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
            KeyMaps ??= new KeyMapsSettings();
            KeyMaps.Normalize();
            HidHideDeviceInstancePaths = HidHideDeviceInstancePaths
                .Where(path => !string.IsNullOrWhiteSpace(path))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
            if (KeyboardProfiles.Count == 0) KeyboardProfiles.Add(new KeyboardProfile());
            if (MouseProfiles.Count == 0) MouseProfiles.Add(new MouseProfile());
            foreach (var profile in KeyboardProfiles) NormalizeBindings(profile);
            foreach (var profile in MouseProfiles) NormalizeBindings(profile);
            if (StickPointsProfiles.Count == 0) StickPointsProfiles.Add(new StickPointsProfile());

            ActiveProfile = Math.Clamp(ActiveProfile, 0, KeyboardProfiles.Count - 1);
            ActiveMouseProfile = Math.Clamp(ActiveMouseProfile, 0, MouseProfiles.Count - 1);
            ActiveStickPointsProfile = Math.Clamp(ActiveStickPointsProfile, 0, StickPointsProfiles.Count - 1);
            if (!double.IsFinite(AnalogStickCurveExponent) || AnalogStickCurveExponent <= 0)
                AnalogStickCurveExponent = 1.0;
            AnalogStickCurveExponent = Math.Clamp(AnalogStickCurveExponent, 0.1, 5.0);
            SettingsVersion = 5;
        }

        private static void NormalizeBindings(BindingProfile profile)
        {
            profile.Bindings ??= new List<ProfileBinding>();
            var ids = new HashSet<string>(StringComparer.Ordinal);
            foreach (var binding in profile.Bindings)
            {
                binding.Buttons ??= new List<string>();
                binding.Buttons = binding.Buttons
                    .Where(button => !string.IsNullOrWhiteSpace(button))
                    .Distinct(StringComparer.Ordinal)
                    .ToList();
                if (string.IsNullOrWhiteSpace(binding.Id) || !ids.Add(binding.Id))
                {
                    binding.Id = Guid.NewGuid().ToString("N");
                    ids.Add(binding.Id);
                }
                binding.Action ??= "None";
            }
        }
    }

    /// <summary>
    /// Shared ordered binding collection for keyboard and mouse profiles.
    /// </summary>
    public abstract class BindingProfile
    {
        public string Name { get; set; } = "Default";
        public List<ProfileBinding> Bindings { get; set; } = new();
    }

    public sealed class KeyboardProfile : BindingProfile
    {
        public KeyboardProfile()
        {
            Bindings = DefaultBindings.Keyboard();
        }
    }

    public sealed class MouseProfile : BindingProfile
    {
        public MouseProfile()
        {
            Bindings = DefaultBindings.Mouse();
        }
    }

    public sealed class ProfileBinding
    {
        public string Id { get; set; } = Guid.NewGuid().ToString("N");
        public List<string> Buttons { get; set; } = new();
        public string Action { get; set; } = "None";
        public bool Modifier { get; set; }
        public bool HoldLast { get; set; }
        public bool Ctrl { get; set; }
        public bool Shift { get; set; }
        public bool Alt { get; set; }

        public ProfileBinding() { }
        public ProfileBinding(string button, string action, bool modifier = false)
        {
            Buttons.Add(button);
            Action = action;
            Modifier = modifier;
        }
        public ProfileBinding(IEnumerable<string> buttons, string action)
        {
            Buttons = buttons.ToList();
            Action = action;
        }
    }

    internal static class DefaultBindings
    {
        private static ProfileBinding B(string button, string action) => new(button, action);
        private static List<ProfileBinding> CommonDigital(
            string a, string b, string x, string y,
            string lb, string rb, string lt, string rt,
            string ls = "ToggleMoveScaleKeyboard", string rs = "None",
            string dUp = "ArrowUp", string dDown = "ArrowDown",
            string dLeft = "ArrowLeft", string dRight = "ArrowRight") => new()
        {
            B("A", a),
            B("B", b),
            B("X", x),
            B("Y", y),
            B("LB", lb),
            B("RB", rb),
            B("LT", lt),
            B("RT", rt),
            B("LS", ls),
            B("RS", rs),
            B("View", "DisableInput"),
            B("Menu", "ToggleKeyboardMouseMode"),
            B("Home", "None"),
            B("DUp", dUp),
            B("DDown", dDown),
            B("DLeft", dLeft),
            B("DRight", dRight)
        };

        public static List<ProfileBinding> Keyboard()
        {
            var bindings = CommonDigital(
                "Space",
                "MouseMode",
                "Tab",
                "None",
                "SubmitLeft",
                "SubmitRight",
                "HoldShift",
                "HoldCtrl"
            );
            bindings.AddRange(new[]
            {
                B("LUp", "MoveLeftCursorUp"),
                B("LDown", "MoveLeftCursorDown"),
                B("LLeft", "MoveLeftCursorLeft"),
                B("LRight", "MoveLeftCursorRight"),
                B("RUp", "MoveRightCursorUp"),
                B("RDown", "MoveRightCursorDown"),
                B("RLeft", "MoveRightCursorLeft"),
                B("RRight", "MoveRightCursorRight"),
                new ProfileBinding(new[] { "LS", "RS", "LB", "RB" }, "EnableInput")
            });
            return bindings;
        }

        public static List<ProfileBinding> Mouse()
        {
            var bindings = CommonDigital(
                "LeftClick",
                "RightClick",
                "MiddleClick",
                "KeyboardMode",
                "LeftClick",
                "RightClick",
                "MiddleClick",
                "SpeedBoost",
                ls: "None",
                dUp: "ScrollUp",
                dDown: "ScrollDown",
                dLeft: "ScrollLeft",
                dRight: "ScrollRight"
            );
            bindings.AddRange(new[]
            {
                B("LUp", "AnalogScrollDown"),
                B("LDown", "AnalogScrollUp"),
                B("LLeft", "AnalogScrollLeft"),
                B("LRight", "AnalogScrollRight"),
                B("RUp", "MouseMoveUp"),
                B("RDown", "MouseMoveDown"),
                B("RLeft", "MouseMoveLeft"),
                B("RRight", "MouseMoveRight"),
                new ProfileBinding(new[] { "LS", "RS", "LB", "RB" }, "EnableInput")
            });
            return bindings;
        }
    }

    /// <summary>Independent geometry profile used by both keyboard mapping profiles.</summary>
    public sealed class StickPointsProfile
    {
        public string Name { get; set; } = "Default";
        public double LeftX { get; set; } = 0.32;
        public double LeftY { get; set; } = 0.53;
        public double RightX { get; set; } = 0.51;
        public double RightY { get; set; } = 0.53;
        public double LeftRayLength { get; set; } = 0.4;
        public double RightRayLength { get; set; } = 0.4;
    }

    /// <summary>
    /// Key Maps mode (DirectInput) configuration: the five fixed maps with their
    /// per-slot key names (existing input-sender key vocabulary; a single
    /// punctuation character taps its exact glyph via a Unicode event) and the
    /// tap thresholds. Persisted like every other settings object; the settings
    /// UI for editing the slots is a later task.
    /// </summary>
    public sealed class KeyMapsSettings
    {
        public double StickTapThreshold { get; set; } = 0.55;
        public bool ShowOverlay { get; set; } = true;
        public List<KeyMapDefinition> Maps { get; set; } = new()
        {
            new KeyMapDefinition("Utility"),
            new KeyMapDefinition("Symbols 1"),
            new KeyMapDefinition("Symbols 2"),
            new KeyMapDefinition("Symbols 3"),
            new KeyMapDefinition("Function Keys")
        };

        /// <summary>Clamp to exactly the spec's five maps (repair drifted files).</summary>
        public void Normalize()
        {
            if (Maps.Count == 5)
            {
                foreach (KeyMapDefinition map in Maps)
                {
                    map.Normalize();
                }
                return;
            }
            Maps = new List<KeyMapDefinition>
            {
                new KeyMapDefinition("Utility"),
                new KeyMapDefinition("Symbols 1"),
                new KeyMapDefinition("Symbols 2"),
                new KeyMapDefinition("Symbols 3"),
                new KeyMapDefinition("Function Keys")
            };
        }
    }

    /// <summary>One map's physical-slot → key-name table ("" = unbound).</summary>
    public sealed class KeyMapDefinition
    {
        public string Name { get; set; } = "";

        public string Select { get; set; }
        public string Start { get; set; }
        public string DPadUp { get; set; }
        public string DPadDown { get; set; }
        public string DPadLeft { get; set; }
        public string DPadRight { get; set; }
        public string FaceY { get; set; }
        public string FaceA { get; set; }
        public string FaceX { get; set; }
        public string FaceB { get; set; }
        public string LeftStickUp { get; set; }
        public string LeftStickDown { get; set; }
        public string LeftStickLeft { get; set; }
        public string LeftStickRight { get; set; }
        public string LeftStickPress { get; set; }
        public string RightStickUp { get; set; }
        public string RightStickDown { get; set; }
        public string RightStickLeft { get; set; }
        public string RightStickRight { get; set; }
        public string RightStickPress { get; set; }

        public KeyMapDefinition(string name)
        {
            Name = name;
            Select = "";
            Start = "";
            ApplyDefaults(name);
        }

        /// <summary>Spec defaults; empty string = unbound (kept "" by repair).</summary>
        private void ApplyDefaults(string name)
        {
            DPadUp = "ArrowUp";
            DPadDown = "ArrowDown";
            DPadLeft = "ArrowLeft";
            DPadRight = "ArrowRight";
            FaceY = "Delete";
            FaceA = "Enter";
            FaceX = "Space";
            FaceB = "Backspace";
            LeftStickUp = "PageUp";
            LeftStickDown = "PageDown";
            LeftStickLeft = "Home";
            LeftStickRight = "End";
            LeftStickPress = "CapsLock";
            RightStickUp = "Insert";
            RightStickDown = "PrintScreen";
            RightStickLeft = "Escape";
            RightStickRight = "Tab";
            RightStickPress = "Windows";
            Select = "";
            Start = "MouseMode";
            if (name == "Symbols 1")
            {
                DPadUp = "W"; DPadDown = "S"; DPadLeft = "A"; DPadRight = "D";
                FaceY = "I"; FaceA = "K"; FaceX = "J"; FaceB = "L";
                LeftStickUp = "E"; LeftStickDown = "Z"; LeftStickLeft = "Q"; LeftStickRight = "C";
                RightStickUp = "U"; RightStickDown = "M"; RightStickLeft = "O"; RightStickRight = "P";
            }
            else if (name == "Symbols 2")
            {
                DPadUp = "R"; DPadDown = "C"; DPadLeft = "F"; DPadRight = "T";
                FaceY = "Y"; FaceA = "B"; FaceX = "G"; FaceB = "H";
                LeftStickUp = "+"; LeftStickDown = "-"; LeftStickLeft = "["; LeftStickRight = "]";
                RightStickUp = "'"; RightStickDown = "`"; RightStickLeft = "V"; RightStickRight = "N";
                LeftStickPress = ".";
                RightStickPress = ",";
            }
            else if (name == "Symbols 3")
            {
                DPadUp = "1"; DPadDown = "2"; DPadLeft = "3"; DPadRight = "4";
                FaceY = "5"; FaceA = "6"; FaceX = "7"; FaceB = "8";
                LeftStickUp = "0"; LeftStickDown = ","; LeftStickLeft = "'"; LeftStickRight = ";";
                RightStickUp = "9"; RightStickDown = "."; RightStickLeft = "-"; RightStickRight = "=";
                LeftStickPress = "/";
                RightStickPress = "\\";
            }
            else if (name == "Function Keys")
            {
                DPadUp = "F1"; DPadDown = "F2"; DPadLeft = "F3"; DPadRight = "F4";
                FaceY = "F5"; FaceA = "F6"; FaceX = "F7"; FaceB = "F8";
                LeftStickUp = "F9"; LeftStickDown = "F10"; LeftStickLeft = "F11"; LeftStickRight = "F12";
                LeftStickPress = "XButton1";
                RightStickUp = "VolumeUp";
                RightStickDown = "VolumeDown";
                RightStickLeft = "BrowserStop";
                RightStickRight = "MediaPlayPause";
                RightStickPress = "XButton2";
                Select = "ScrollLock";
                Start = "PauseBreak";
            }
        }

        /// <summary>
        /// JSON repair: System.Text.Json runs the (name) constructor first, which
        /// lays in the spec defaults for the map's name, then overwrites every
        /// property present in the file — so a drifted file already falls back
        /// to defaults slot-by-slot. Only explicit nulls from hand edits remain;
        /// refill those from a probe built on the map's name.
        /// </summary>
        public void Normalize()
        {
            KeyMapDefinition defaults = new KeyMapDefinition(string.IsNullOrWhiteSpace(Name) ? "Utility" : Name);
            Select = Select ?? defaults.Select;
            Start = Start ?? defaults.Start;
            DPadUp = DPadUp ?? defaults.DPadUp;
            DPadDown = DPadDown ?? defaults.DPadDown;
            DPadLeft = DPadLeft ?? defaults.DPadLeft;
            DPadRight = DPadRight ?? defaults.DPadRight;
            FaceY = FaceY ?? defaults.FaceY;
            FaceA = FaceA ?? defaults.FaceA;
            FaceX = FaceX ?? defaults.FaceX;
            FaceB = FaceB ?? defaults.FaceB;
            LeftStickUp = LeftStickUp ?? defaults.LeftStickUp;
            LeftStickDown = LeftStickDown ?? defaults.LeftStickDown;
            LeftStickLeft = LeftStickLeft ?? defaults.LeftStickLeft;
            LeftStickRight = LeftStickRight ?? defaults.LeftStickRight;
            LeftStickPress = LeftStickPress ?? defaults.LeftStickPress;
            RightStickUp = RightStickUp ?? defaults.RightStickUp;
            RightStickDown = RightStickDown ?? defaults.RightStickDown;
            RightStickLeft = RightStickLeft ?? defaults.RightStickLeft;
            RightStickRight = RightStickRight ?? defaults.RightStickRight;
            RightStickPress = RightStickPress ?? defaults.RightStickPress;
        }
    }
}
