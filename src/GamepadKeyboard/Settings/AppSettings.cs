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

        public double KeySpacing { get; set; } = 6.0;
        public double LeftRayScale { get; set; } = 1.0;    // multiplies distance to Esc
        public double RightRayScale { get; set; } = 1.0;   // multiplies distance to F12
        public double MouseSpeed { get; set; } = 12.0;
        public double MouseSpeedBoostMultiplier { get; set; } = 2.5;
        public double ScrollSpeed { get; set; } = 3.0;
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
        public bool StartInMouseMode { get; set; } = false;
        public int ActiveProfile { get; set; } = 0;
        public int ActiveMouseProfile { get; set; } = 0;
        public bool AdminLaunch { get; set; } = false;
        public string DefaultCursorSize { get; set; } = "M";

        public List<CenterPointProfile> KeyboardProfiles { get; set; } = new()
        {
            new CenterPointProfile()
        };

        public List<MouseProfile> MouseProfiles { get; set; } = new()
        {
            new MouseProfile()
        };

        [JsonIgnore]
        public CenterPointProfile Profile =>
            KeyboardProfiles.Count == 0 ? new CenterPointProfile() : KeyboardProfiles[Math.Clamp(ActiveProfile, 0, KeyboardProfiles.Count - 1)];

        [JsonIgnore]
        public MouseProfile MouseProfile =>
            MouseProfiles.Count == 0 ? new MouseProfile() : MouseProfiles[Math.Min(ActiveMouseProfile, MouseProfiles.Count - 1)];

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
                    var loaded = JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(FilePath), JsonOpts);
                    if (loaded != null) Instance = loaded;
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
    }

    public sealed class CenterPointProfile
    {
        public string Name { get; set; } = "Default";

        /// <summary>Left point position, normalized 0..1 within the keyboard layout bounds.</summary>
        public double LeftX { get; set; } = 0.28;
        public double LeftY { get; set; } = 0.55;

        /// <summary>Right point position, normalized 0..1 within the keyboard layout bounds.</summary>
        public double RightX { get; set; } = 0.72;
        public double RightY { get; set; } = 0.55;

        /// <summary>Response curve exponent. 1.0 = linear; &gt;1 = slower near center (fine control), &lt;1 = faster.</summary>
        public double CurveExponent { get; set; } = 1.0;

        /// <summary>Ray length scale relative to the key-grid-derived maximum.</summary>
        public double RayScale { get; set; } = 1.0;
    }

    public sealed class MouseProfile
    {
        public string Name { get; set; } = "Default";

        public string A { get; set; } = "LeftClick";
        public string B { get; set; } = "KeyboardMode";
        public string X { get; set; } = "MiddleClick";
        public string Y { get; set; } = "ToggleLegend";

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
        public string View { get; set; } = "None";
        public string Menu { get; set; } = "None";
    }
}