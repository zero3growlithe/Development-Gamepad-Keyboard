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

        /// <summary>Opacity of keyboard key tiles — Keyboard Mode overlay and
        /// the Maps Mode projected keyboard (0.2–1; clamped in Normalize()).
        /// </summary>
        public double KeyboardKeyOpacity { get; set; } = 1.0;
        public double StickDeadzone { get; set; } = 0.1;
        public double MouseStickDeadzone { get; set; } = 0.1;   // separate deadzone for mouse mode
        public double OverlayScale { get; set; } = 1.0;
        public double OverlayMoveSpeed { get; set; } = 6.0;
        public double AnalogStickCurveExponent { get; set; } = 1.0;
        public double OverlayLeft { get; set; } = 100;
        public double OverlayTop { get; set; } = 100;
        public double KeyMapsOverlayLeft { get; set; } = -1;   // -1 = not moved yet → default bottom-center
        public double KeyMapsOverlayTop { get; set; } = -1;
        public double MouseSpeed { get; set; } = 8.0;
        public double MouseSpeedBoostMultiplier { get; set; } = 2;
        public double ScrollSpeed { get; set; } = 1.0;
        public bool CursorLagEnabled { get; set; } = false;
        public double CursorLagSeconds { get; set; } = 0.2;
        public bool HideRaysInCursorLag { get; set; } = false;
        public bool FreeCursorEnabled { get; set; } = true;
        public double FreeCursorSpeed { get; set; } = 600.0;
        public bool HideCenterPointsAndRaysInFreeCursor { get; set; } = false;
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
            KeyboardKeyOpacity = Math.Clamp(KeyboardKeyOpacity, 0.2, 1.0);
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

        /// <summary>Action the LEFT TRIGGER (L2) runs outside map chords —
        /// hold/toggle a modifier, tap a key or fire an app action. While the
        /// maps key is held the button is frozen only when it takes part in a
        /// map-open combination; otherwise the action keeps working.</summary>
        public string LeftTriggerAction { get; set; } = "HoldShift";

        /// <summary>Action the LEFT BUMPER (L1) runs outside map chords.</summary>
        public string LeftBumperAction { get; set; } = "HoldCtrl";

        /// <summary>Action the RIGHT BUMPER (R1) runs outside map chords.</summary>
        public string RightBumperAction { get; set; } = "HoldAlt";

        /// <summary>"Mitigate lock" toggles: allow the button's action to be
        /// USED while the maps key is held, even when the button is part of an
        /// open combination. With mitigation ON the action behaves like the
        /// pre-held Shift case: whatever modifier state the button had when the
        /// maps key went down STAYS in that state ("sticks") until the button
        /// is pressed again (which frees it to track the physical control,
        /// exactly as the Shift exception always did). With mitigation OFF
        /// (default) a chord member is simply frozen while the maps key is
        /// held — its state at the maps-key edge is held until release.</summary>
        /// <summary>Action the RIGHT TRIGGER (R2) runs — the maps key itself.
        /// "MapsModifierHold"/"MapsModifierToggle" pick HOW R2 engages the maps
        /// layer (hold = maps key down while held; toggle = one press flips it);
        /// any other action demotes R2 to a normal system button and the maps
        /// key falls back to the hold-through-binding path.</summary>
        public string RightTriggerAction { get; set; } = "MapsModifierHold";

        public bool LeftTriggerMitigateLock { get; set; }
        public bool LeftBumperMitigateLock { get; set; }
        public bool RightBumperMitigateLock { get; set; }
        public bool RightTriggerMitigateLock { get; set; }

        public KeyMapsLayoutSettings Layout { get; set; } = new();
        public List<KeyMapDefinition> Maps { get; set; } = new()
        {
            new KeyMapDefinition("Utility"),
            new KeyMapDefinition("Symbols 1"),
            new KeyMapDefinition("Symbols 2"),
            new KeyMapDefinition("Symbols 3"),
            new KeyMapDefinition("Function Keys")
        };

        /// <summary>One-shot marker: pre-combination settings files lack the
        /// per-map OpenWith* flags, so their first Normalize() seeds the
        /// historical chords (Utility/Symbols 1 = none, Symbols 2 = R1,
        /// Symbols 3 = L1, Function Keys = L1+R1) exactly once.</summary>
        public bool OpenCombosConfigured { get; set; }

        /// <summary>Clamp to exactly the spec's five maps (repair drifted files).</summary>
        public void Normalize()
        {
            Layout ??= new KeyMapsLayoutSettings();
            Layout.Normalize();
            if (Maps.Count == 5)
            {
                foreach (KeyMapDefinition map in Maps)
                {
                    map.Normalize();
                }
                if (!OpenCombosConfigured)
                {
                    foreach (KeyMapDefinition map in Maps)
                    {
                        map.ApplyDefaultOpenCombo();
                    }
                    OpenCombosConfigured = true;
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
            OpenCombosConfigured = true;
        }
    }

    /// <summary>Overlay layout tuning for Key Maps mode: per-key-circle center
    /// offsets relative to the board's current position, the per-circle atom
    /// spread, the global atom/quark size and quark-distance multipliers, the
    /// button-icon offset and a global key size multiplier. All values clamped
    /// in Normalize().</summary>
    public sealed class KeyMapsLayoutSettings
    {
        /// <summary>Center offset of each key wheel relative to the overlay
        /// window's default cluster anchor, in px (positive X = right,
        /// positive Y = down).</summary>
        public double DPadOffsetX { get; set; } = -276.0;
        public double DPadOffsetY { get; set; } = 0.0;
        public double FaceOffsetX { get; set; } = 104.0;
        public double FaceOffsetY { get; set; } = 0.0;
        public double LeftStickOffsetX { get; set; } = -276.0;
        public double LeftStickOffsetY { get; set; } = 10.0;
        public double RightStickOffsetX { get; set; } = 104.0;
        public double RightStickOffsetY { get; set; } = 10.0;

        /// <summary>Global atom (big center prompt) size multiplier.</summary>
        public double AtomSize { get; set; } = 1.0;

        /// <summary>Global quark (small prompt) size multiplier.</summary>
        public double QuarkSize { get; set; } = 1.0;

        /// <summary>Quark distance from their atom center, separate per axis;
        /// 0 = quarks hug the center edge, 1 = default gap, −2..2 = full
        /// pull-in/push-out range (negative pulls quarks into the atom).</summary>
        public double QuarkDistanceX { get; set; } = 1.0;
        public double QuarkDistanceY { get; set; } = 1.0;

        /// <summary>Button-image offset relative to the atom center, in px
        /// (applied to every atom's icon; manual, negative values allowed).</summary>
        public double IconOffsetX { get; set; } = 0.0;
        public double IconOffsetY { get; set; } = -55.0;

        /// <summary>Spread of the circle's atoms around the circle center,
        /// separate per axis (0 = collapsed, 1 = default spacing, up to 3 =
        /// wide).</summary>
        public double DPadSpreadX { get; set; } = 1.0;
        public double DPadSpreadY { get; set; } = 1.0;
        public double FaceSpreadX { get; set; } = 1.0;
        public double FaceSpreadY { get; set; } = 1.0;
        public double LeftStickSpreadX { get; set; } = 1.0;
        public double LeftStickSpreadY { get; set; } = 1.0;
        public double RightStickSpreadX { get; set; } = 1.0;
        public double RightStickSpreadY { get; set; } = 1.0;

        /// <summary>Opacity of inactive (not-combo-highlighted) quarks while
        /// the maps key is held (0–1).</summary>
        public double IdleQuarkAlpha { get; set; } = 0.45;

        /// <summary>Button-image scale multiplier for every atom's icon
        /// (0–2; 1 = natural size).</summary>
        public double IconScale { get; set; } = 1.0;

        /// <summary>Font-size multiplier for every prompt label on the Key
        /// Maps board (0.2–3; 1 = default).</summary>
        public double FontScale { get; set; } = 1.0;

        /// <summary>Select/Start pair placement: base position, tile scale and
        /// the X spread between the two tiles.</summary>
        public double SelectStartOffsetX { get; set; } = 0.0;
        public double SelectStartOffsetY { get; set; } = 0.0;
        public double SelectStartScale { get; set; } = 1.0;
        public double SelectStartSpreadX { get; set; } = 1.0;



        /// <summary>True = stick atoms spread uniformly from the circle center
        /// (dir factors −1.25/−0.25/+1.25 like the other circles). False =
        /// the center and bottom atoms get manual Y offsets below.</summary>
        /// <summary>Show shadow maps: when R2 is held the atom centers show
        /// the ACTIVE map's binding and the quarks (Symbols 2/3/Function
        /// variants) appear beside it. Off = no quarks, no amber center
        /// highlight — center just reads the active map's binding.</summary>
        public bool ShowShadowMaps { get; set; } = true;

        /// <summary>Projected-keyboard view: instead of the atom wheels, Maps
        /// Mode draws a full US keyboard (no sticks/rays/points) and floats the
        /// active map's button prompts over the key each slot sends.</summary>
        public bool ProjectKeyboard { get; set; } = false;

        /// <summary>Button-prompt offset relative to a key's top edge in the
        /// projected view, in px (manual, clamped in Normalize()).</summary>
        public double PromptOffsetX { get; set; } = 0.0;
        public double PromptOffsetY { get; set; } = 0.0;

        /// <summary>Row spacing multiplier for the extra off-keyboard key
        /// column in the projected view (0.5–2, 1 = default).</summary>
        public double ExtraKeySpacing { get; set; } = 1.0;

        public bool StickUniformSpread { get; set; } = true;
        public double LeftStickCenterOffsetY { get; set; } = 0.0;
        public double LeftStickBottomOffsetY { get; set; } = 0.0;
        public double RightStickCenterOffsetY { get; set; } = 0.0;
        public double RightStickBottomOffsetY { get; set; } = 0.0;

        /// <summary>Global tile size multiplier (labels wrap inside tiles).</summary>
        public double KeySize { get; set; } = 1.0;

        public void Normalize()
        {
            DPadOffsetX = Math.Clamp(DPadOffsetX, -400.0, 400.0);
            DPadOffsetY = Math.Clamp(DPadOffsetY, -300.0, 300.0);
            FaceOffsetX = Math.Clamp(FaceOffsetX, -400.0, 400.0);
            FaceOffsetY = Math.Clamp(FaceOffsetY, -300.0, 300.0);
            LeftStickOffsetX = Math.Clamp(LeftStickOffsetX, -400.0, 400.0);
            LeftStickOffsetY = Math.Clamp(LeftStickOffsetY, -300.0, 300.0);
            RightStickOffsetX = Math.Clamp(RightStickOffsetX, -400.0, 400.0);
            RightStickOffsetY = Math.Clamp(RightStickOffsetY, -300.0, 300.0);
            DPadSpreadX = Math.Clamp(DPadSpreadX, 0.0, 3.0);
            DPadSpreadY = Math.Clamp(DPadSpreadY, 0.0, 3.0);
            FaceSpreadX = Math.Clamp(FaceSpreadX, 0.0, 3.0);
            FaceSpreadY = Math.Clamp(FaceSpreadY, 0.0, 3.0);
            LeftStickSpreadX = Math.Clamp(LeftStickSpreadX, 0.0, 3.0);
            LeftStickSpreadY = Math.Clamp(LeftStickSpreadY, 0.0, 3.0);
            RightStickSpreadX = Math.Clamp(RightStickSpreadX, 0.0, 3.0);
            RightStickSpreadY = Math.Clamp(RightStickSpreadY, 0.0, 3.0);
            LeftStickCenterOffsetY = Math.Clamp(LeftStickCenterOffsetY, -200.0, 200.0);
            LeftStickBottomOffsetY = Math.Clamp(LeftStickBottomOffsetY, -200.0, 200.0);
            RightStickCenterOffsetY = Math.Clamp(RightStickCenterOffsetY, -200.0, 200.0);
            RightStickBottomOffsetY = Math.Clamp(RightStickBottomOffsetY, -200.0, 200.0);
            KeySize = Math.Clamp(KeySize, 0.6, 2.0);
            AtomSize = Math.Clamp(AtomSize, 0.5, 2.0);
            QuarkSize = Math.Clamp(QuarkSize, 0.5, 2.0);
            QuarkDistanceX = Math.Clamp(QuarkDistanceX, -2.0, 2.0);
            QuarkDistanceY = Math.Clamp(QuarkDistanceY, -2.0, 2.0);
            IdleQuarkAlpha = Math.Clamp(IdleQuarkAlpha, 0.0, 1.0);
            IconScale = Math.Clamp(IconScale, 0.0, 2.0);
            FontScale = Math.Clamp(FontScale, 0.2, 3.0);
            PromptOffsetX = Math.Clamp(PromptOffsetX, -200.0, 200.0);
            PromptOffsetY = Math.Clamp(PromptOffsetY, -200.0, 200.0);
            ExtraKeySpacing = Math.Clamp(ExtraKeySpacing, 0.2, 2.0);
            SelectStartOffsetX = Math.Clamp(SelectStartOffsetX, -400.0, 400.0);
            SelectStartOffsetY = Math.Clamp(SelectStartOffsetY, -300.0, 300.0);
            SelectStartScale = Math.Clamp(SelectStartScale, 0.5, 2.0);
            SelectStartSpreadX = Math.Clamp(SelectStartSpreadX, 0.5, 3.0);
            IconOffsetX = Math.Clamp(IconOffsetX, -200.0, 200.0);
            IconOffsetY = Math.Clamp(IconOffsetY, -200.0, 200.0);
        }
    }

    /// <summary>One map's physical-slot → key-name table ("" = unbound).</summary>
    public sealed class KeyMapDefinition
    {
        public string Name { get; set; } = "";

        public string Select { get; set; } = "";
        public string Start { get; set; } = "";
        public string DPadUp { get; set; } = "";
        public string DPadDown { get; set; } = "";
        public string DPadLeft { get; set; } = "";
        public string DPadRight { get; set; } = "";
        public string FaceY { get; set; } = "";
        public string FaceA { get; set; } = "";
        public string FaceX { get; set; } = "";
        public string FaceB { get; set; } = "";
        public string LeftStickUp { get; set; } = "";
        public string LeftStickDown { get; set; } = "";
        public string LeftStickLeft { get; set; } = "";
        public string LeftStickRight { get; set; } = "";
        public string LeftStickPress { get; set; } = "";
        public string RightStickUp { get; set; } = "";
        public string RightStickDown { get; set; } = "";
        public string RightStickLeft { get; set; } = "";
        public string RightStickRight { get; set; } = "";
        public string RightStickPress { get; set; } = "";

        /// <summary>Gamepad buttons (subset of L2, L1, R1, L3, R3) that must be
        /// held together with the maps key to open this map while it is held.
        /// Stored as canonical short names "L2", "L1", "R1", "L3", "R3"
        /// separated by '+' (empty = maps key alone). "" on fresh defaults so
        /// ApplyDefaultOpenCombo() can seed the historical chords exactly once
        /// for files created before this option existed.</summary>
        public string OpenWith { get; set; } = "";

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

        /// <summary>Seeds the historical open-combinations exactly once (the
        /// first Normalize of a settings file without OpenWith data): Utility
        /// and Symbols 1 open with the maps key alone, Symbols 2 with R1,
        /// Symbols 3 with L1, Function Keys with L1+R1. Kept unchanged when the
        /// file already carries combinations.</summary>
        public void ApplyDefaultOpenCombo()
        {
            if (!string.IsNullOrEmpty(OpenWith))
            {
                return;
            }
            OpenWith = Name switch
            {
                "Symbols 2" => "R1",
                "Symbols 3" => "L1",
                "Function Keys" => "L1+R1",
                _ => "",
            };
        }
    }
}
