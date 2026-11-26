using System;
using System.IO;
using System.Text.Json;
using Microsoft.Xna.Framework;

namespace FunkyMakerPlayer.Multiplayer
{
    /// <summary>
    /// Stores all multiplayer settings with categories.
    /// Automatically saves/loads from JSON file.
    /// </summary>
    public class MultiplayerSettings
    {
        // ══════════════════════════════════════════════════════════════════════
        // Singleton Pattern
        // ══════════════════════════════════════════════════════════════════════
        private static MultiplayerSettings? _instance;
        public static MultiplayerSettings Instance
        {
            get
            {
                if (_instance == null)
                {
                    _instance = Load();
                }
                return _instance;
            }
        }

        private const string SETTINGS_FILE = "multiplayer_settings.json";
        
        /// <summary>
        /// Get the directory used to persist multiplayer data (settings, leaderboards, match history).
        /// </summary>
        public static string GetStorageDirectory()
        {
#if ANDROID
            string appFolder = Environment.GetFolderPath(Environment.SpecialFolder.Personal);
#else
            string appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
            string appFolder = Path.Combine(appData, "FunkyMakerPlayer");
#endif
            if (!Directory.Exists(appFolder))
                Directory.CreateDirectory(appFolder);

            return appFolder;
        }

        /// <summary>
        /// Get the full path for the multiplayer settings file.
        /// </summary>
        private static string GetSettingsPath()
        {
            // FUNKYMAKER_MP_PROFILE=<name> (fork, for testing two game windows on one PC): a separate
            // settings file, so each window gets its own PlayerId / username / color.
            string? profile = Environment.GetEnvironmentVariable(ProfileEnvironmentVariable);
            if (!string.IsNullOrWhiteSpace(profile))
            {
                string safe = string.Concat(profile.Trim().Split(Path.GetInvalidFileNameChars()));
                return Path.Combine(GetStorageDirectory(), $"multiplayer_settings.{safe}.json");
            }
            return Path.Combine(GetStorageDirectory(), SETTINGS_FILE);
        }

        public const string ProfileEnvironmentVariable = "FUNKYMAKER_MP_PROFILE";

        // ══════════════════════════════════════════════════════════════════════
        // Category 2: User Configuration
        // ══════════════════════════════════════════════════════════════════════
        public string PlayerId { get; set; } = "";
        public string Username { get; set; } = "Player";
        public int AvatarIndex { get; set; } = 0; // Kept for wire/JSON backwards compatibility (hidden in UI)
        public int PlayerColorHue { get; set; } = 180; // 0-360 for HSV hue
        /// <summary>
        /// Optional custom character slug from <c>mods/characters/&lt;slug&gt;/</c> equipped locally on your side
        /// during online FNF matches (empty = use the level's default character). Client-side only.
        /// </summary>
        public string EquippedCharacter { get; set; } = "";

        // ══════════════════════════════════════════════════════════════════════
        // Category 3: Gameplay Configuration
        // ══════════════════════════════════════════════════════════════════════
        /// <summary>
        /// Same setting as Settings → GAMEPLAY... → Opponent Strumline (fork: merged). Stored in
        /// settings.json, not in the multiplayer file.
        /// </summary>
        [System.Text.Json.Serialization.JsonIgnore]
        public bool ShowOpponentNotes
        {
            get => !Game1.HideOpponentStrumline;
            set => Game1.HideOpponentStrumline = !value;
        }
        public bool ShowOpponentScore { get; set; } = true;
        public bool ShowConnectionQuality { get; set; } = true;
        /// <summary>Opacity (0-100%) of the opponent's notes in online matches ("Opponent Notes Opacity").</summary>
        public int GhostTransparency { get; set; } = 50;
        /// <summary>
        /// Fork: osu! songs received from other players come without their background video (smaller
        /// downloads). Told to the server in Hello (resent when changed).
        /// </summary>
        public bool OsuSkipVideo { get; set; } = false;
        /// <summary>
        /// Online FNF Chart-Only mode: downloads and loads only charts + music (no sprites/stage)
        /// in multiplayer matches so low-RAM / 32-bit devices can play heavy levels smoothly.
        /// Told to the server in Hello so the server sends lightweight chart-only packages.
        /// </summary>
        public bool ChartOnlyMode { get; set; } = false;

        // ══════════════════════════════════════════════════════════════════════
        // Category 4: Performance Configuration
        // ══════════════════════════════════════════════════════════════════════
        public enum NetworkUpdateRate
        {
            Low = 30,       // 30 updates/second
            Medium = 60,    // 60 updates/second
            High = 120      // 120 updates/second
        }
        public NetworkUpdateRate UpdateRate { get; set; } = NetworkUpdateRate.Medium;
        public bool EnableInterpolation { get; set; } = true;

        // ══════════════════════════════════════════════════════════════════════
        // Category 5: Visual Configuration
        // ══════════════════════════════════════════════════════════════════════
        public int HudOpacity { get; set; } = 100; // 0-100%
        
        public enum IndicatorPosition
        {
            TopLeft,
            TopRight,
            BottomLeft,
            BottomRight
        }
        public IndicatorPosition ConnectionIndicatorPosition { get; set; } = IndicatorPosition.TopLeft;
        public int SettingsSchemaVersion { get; set; } = 0;
        
        public enum UIMode
        {
            Compact,
            Full
        }
        public UIMode InterfaceMode { get; set; } = UIMode.Full;

        /// <summary>
        /// Online server (rooms, matches, level transfers, leaderboard). Empty =
        /// <see cref="DefaultServerUrl"/>. The environment variable FUNKYMAKER_SERVER_URL overrides it.
        /// Only editable in the JSON file for now.
        /// </summary>
        public string ServerUrl { get; set; } = "";

        /// <summary>Old name of <see cref="ServerUrl"/> (separate transfer server); used when ServerUrl is empty.</summary>
        public string TransferServerUrl { get; set; } = "";

        /// <summary>The fork's online server (room system). The old game server only speaks the 1v1 protocol.</summary>
        public const string DefaultServerUrl = "https://funkymaker-transfer.onrender.com";
        public const string ServerUrlEnvironmentVariable = "FUNKYMAKER_SERVER_URL";

        /// <summary>Server to connect to: env var, then settings, then the default.</summary>
        [System.Text.Json.Serialization.JsonIgnore]
        public string EffectiveServerUrl
        {
            get
            {
                string? env = Environment.GetEnvironmentVariable(ServerUrlEnvironmentVariable);
                string url = !string.IsNullOrWhiteSpace(env) ? env
                    : !string.IsNullOrWhiteSpace(ServerUrl) ? ServerUrl
                    : !string.IsNullOrWhiteSpace(TransferServerUrl) ? TransferServerUrl
                    : DefaultServerUrl;
                return url.Trim().TrimEnd('/');
            }
        }

        /// <summary>What the strumline of the other side shows in Teams matches.</summary>
        public enum RivalStrumlineMode
        {
            /// <summary>The first player of the other team (their real presses).</summary>
            RealPlayer,
            /// <summary>Played by the CPU, like the extra strumlines.</summary>
            Cpu,
        }
        public RivalStrumlineMode TeamsRivalStrumline { get; set; } = RivalStrumlineMode.RealPlayer;

        /// <summary>Where the in-match list of players and scores is drawn.</summary>
        public enum ScoreListSide { Left, Right, Hidden }
        public ScoreListSide ScoreListPosition { get; set; } = ScoreListSide.Left;

        // ══════════════════════════════════════════════════════════════════════
        // Save / Load Methods
        // ══════════════════════════════════════════════════════════════════════

        /// <summary>
        /// Save settings to JSON file.
        /// </summary>
        public void Save()
        {
            try
            {
                string json = JsonSerializer.Serialize(this, new JsonSerializerOptions
                {
                    WriteIndented = true
                });

                string path = GetSettingsPath();
                File.WriteAllText(path, json);

                Console.WriteLine($"[Multiplayer Settings] Saved to {path}");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[Multiplayer Settings] Failed to save: {ex.Message}");
            }
        }

        /// <summary>
        /// Load settings from JSON file, or create default if not found.
        /// </summary>
        public static MultiplayerSettings Load()
        {
            try
            {
                string path = GetSettingsPath();

                if (!File.Exists(path))
                {
                    Console.WriteLine("[Multiplayer Settings] No settings file found, using defaults");
                    var defaultSettings = new MultiplayerSettings
                    {
                        PlayerId = Guid.NewGuid().ToString("N"),
                        SettingsSchemaVersion = 1,
                        ConnectionIndicatorPosition = IndicatorPosition.TopLeft
                    };
                    defaultSettings.Save(); // Create default file
                    return defaultSettings;
                }

                string json = File.ReadAllText(path);
                var settings = JsonSerializer.Deserialize<MultiplayerSettings>(json);

                if (settings == null)
                {
                    Console.WriteLine("[Multiplayer Settings] Failed to parse settings, using defaults");
                    return new MultiplayerSettings
                    {
                        PlayerId = Guid.NewGuid().ToString("N"),
                        SettingsSchemaVersion = 1,
                        ConnectionIndicatorPosition = IndicatorPosition.TopLeft
                    };
                }

                bool dirty = false;
                if (string.IsNullOrWhiteSpace(settings.PlayerId))
                {
                    settings.PlayerId = Guid.NewGuid().ToString("N");
                    dirty = true;
                }

                if (settings.SettingsSchemaVersion < 1)
                {
                    settings.ConnectionIndicatorPosition = IndicatorPosition.TopLeft;
                    settings.SettingsSchemaVersion = 1;
                    dirty = true;
                }

                if (dirty)
                    settings.Save();

                Console.WriteLine($"[Multiplayer Settings] Loaded from {path}");
                return settings;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[Multiplayer Settings] Failed to load: {ex.Message}, using defaults");
                return new MultiplayerSettings { PlayerId = Guid.NewGuid().ToString("N") };
            }
        }

        /// <summary>
        /// Get player color from HSV hue value.
        /// </summary>
        public Color GetPlayerColor()
        {
            return ColorFromHue(PlayerColorHue);
        }

        /// <summary>
        /// Convert a 0-360 hue degree value to an RGB Color.
        /// </summary>
        public static Color ColorFromHue(int hueDegrees)
        {
            int wrapped = ((hueDegrees % 360) + 360) % 360;
            return ColorFromHSV(wrapped / 360f, 0.8f, 0.9f);
        }

        /// <summary>
        /// Get state sync interval in seconds corresponding to the configured <see cref="UpdateRate"/>.
        /// </summary>
        public float GetStateSyncIntervalSeconds()
        {
            return UpdateRate switch
            {
                NetworkUpdateRate.Low => 0.20f,
                NetworkUpdateRate.Medium => 0.10f,
                NetworkUpdateRate.High => 0.05f,
                _ => 0.10f
            };
        }

        /// <summary>
        /// Convert HSV to RGB color.
        /// </summary>
        private static Color ColorFromHSV(float h, float s, float v)
        {
            int i = (int)(h * 6);
            float f = h * 6 - i;
            float p = v * (1 - s);
            float q = v * (1 - f * s);
            float t = v * (1 - (1 - f) * s);

            float r, g, b;
            switch (i % 6)
            {
                case 0: r = v; g = t; b = p; break;
                case 1: r = q; g = v; b = p; break;
                case 2: r = p; g = v; b = t; break;
                case 3: r = p; g = q; b = v; break;
                case 4: r = t; g = p; b = v; break;
                default: r = v; g = p; b = q; break;
            }

            return new Color((int)(r * 255), (int)(g * 255), (int)(b * 255));
        }

        /// <summary>
        /// Get avatar icon character based on index.
        /// </summary>
        public string GetAvatarIcon()
        {
            // Using ASCII-safe characters that all fonts support
            string[] avatars = { "@", "#", "$", "%", "&", "*", "+", "=", "~", "^" };
            return avatars[AvatarIndex % avatars.Length];
        }

        /// <summary>
        /// Reset all settings to default values.
        /// </summary>
        public void ResetToDefaults()
        {
            Username = "Player";
            AvatarIndex = 0;
            PlayerColorHue = 180;
            EquippedCharacter = "";
            
            ShowOpponentNotes = true;   // also saved in settings.json (shared with Opponent Strumline)
            ShowOpponentScore = true;
            ShowConnectionQuality = true;
            GhostTransparency = 50;
            
            UpdateRate = NetworkUpdateRate.Medium;
            EnableInterpolation = true;
            
            HudOpacity = 100;
            TeamsRivalStrumline = RivalStrumlineMode.RealPlayer;
            ScoreListPosition = ScoreListSide.Left;
            ConnectionIndicatorPosition = IndicatorPosition.TopLeft;
            InterfaceMode = UIMode.Full;
            SettingsSchemaVersion = 1;
            
            Save();
            Game1.SaveSettings();
            Console.WriteLine("[Multiplayer Settings] Reset to defaults");
        }
    }
}
