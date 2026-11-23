using System;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Xna.Framework;

namespace FunkyMakerPlayer;

/// <summary>
/// Serializable position structure for JSON
/// </summary>
public class HudPosition
{
    public int X { get; set; }
    public int Y { get; set; }
    
    public HudPosition() { }
    public HudPosition(int x, int y) { X = x; Y = y; }
    
    public Point ToPoint() => new Point(X, Y);
    public static HudPosition FromPoint(Point p) => new HudPosition(p.X, p.Y);
}

/// <summary>
/// Persistent HUD element positions and sizes.
/// Used by both the HUD editor and the gameplay renderer.
/// </summary>
public class HudSettings
{
    private static HudSettings? _instance;
    
    // Force reload from disk - useful when entering HUD editor
    public static void ForceReload()
    {
        _instance = Load();
    }
    
    public static HudSettings Instance => _instance ??= Load();

    // ── Master HUD Toggle ────────────────────────────────────────────────────
    public bool MasterHudVisible { get; set; } = true;

    // ── Health Bar ───────────────────────────────────────────────────────────
    public HudPosition HealthBarPosition { get; set; } = new HudPosition(340, 650);
    public HudPosition HealthBarSize { get; set; } = new HudPosition(600, 20);
    public bool HealthBarVisible { get; set; } = true;

    // ── Score, Misses, Accuracy (below health bar) ──────────────────────────
    public HudPosition ScorePosition { get; set; } = new HudPosition(440, 680);
    public bool ScoreVisible { get; set; } = true;
    public HudPosition MissesPosition { get; set; } = new HudPosition(600, 680);
    public bool MissesVisible { get; set; } = true;
    public HudPosition AccuracyPosition { get; set; } = new HudPosition(720, 680);
    public bool AccuracyVisible { get; set; } = true;

    // ── Timer Bar ────────────────────────────────────────────────────────────
    public HudPosition TimerBarPosition { get; set; } = new HudPosition(340, 4);
    public HudPosition TimerBarSize { get; set; } = new HudPosition(600, 32);
    [JsonIgnore]
    public bool TimerBarVisible
    {
        get => Game1.ShowTimer;
        set => Game1.ShowTimer = value;
    }

    // ── Song Info (top-left) ─────────────────────────────────────────────────
    public HudPosition SongInfoPosition { get; set; } = new HudPosition(17, 7);
    public bool SongInfoVisible { get; set; } = true;

    // ── Status Info (bottom-left: Botplay / Scroll / Speed) ─────────────────
    public HudPosition StatusInfoPosition { get; set; } = new HudPosition(20, 692);
    public bool StatusInfoVisible { get; set; } = true;

    // ── Combo Display (center screen, above player strumline) ───────────────
    // Position is top-left corner of text (no automatic centering)
    // Centered at X=640 for 1280px width (middle of player strumline)
    public HudPosition ComboPosition { get; set; } = new HudPosition(610, 360);
    public bool ComboVisible { get; set; } = true;

    // ── Judgement Text (above combo) ─────────────────────────────────────────
    // Position is top-left corner of text (no automatic centering)
    // Centered at X=640 for 1280px width (middle of player strumline)
    public HudPosition JudgementPosition { get; set; } = new HudPosition(600, 300);
    public bool JudgementVisible { get; set; } = true;

    // ── Health Icons (for "Fixed at Ends" IconMode) ──────────────────────────
    // These positions are only used when IconMode = 1 (Fixed at Ends)
    public HudPosition OpponentIconPosition { get; set; } = new HudPosition(290, 610);
    public bool OpponentIconVisible { get; set; } = true;
    public HudPosition PlayerIconPosition { get; set; } = new HudPosition(890, 610);

    // ── Strumline offsets (HUD editor) ───────────────────────────────────────
    // Strumline positions come from the level (and can move with events), so the HUD editor
    // stores an offset in 1280×720 pixels that is added on top of wherever the strumline is.
    public HudPosition PlayerStrumlineOffset { get; set; } = new HudPosition(0, 0);
    public HudPosition OpponentStrumlineOffset { get; set; } = new HudPosition(0, 0);
    public bool PlayerIconVisible { get; set; } = true;

    // ── Element scales (fork: HUD editor resize handles; 1 = default size) ───
    // Health bar / timer bar are resized through their Size instead.
    public float ScoreScale { get; set; } = 1f;
    public float MissesScale { get; set; } = 1f;
    public float AccuracyScale { get; set; } = 1f;
    public float SongInfoScale { get; set; } = 1f;
    public float StatusInfoScale { get; set; } = 1f;
    public float ComboScale { get; set; } = 1f;
    public float JudgementScale { get; set; } = 1f;
    public float OpponentIconScale { get; set; } = 1f;
    public float PlayerIconScale { get; set; } = 1f;
    public float PlayerStrumlineScale { get; set; } = 1f;
    public float OpponentStrumlineScale { get; set; } = 1f;

    /// <summary>Copies every element scale from <paramref name="o"/>.</summary>
    public void CopyScalesFrom(HudSettings o)
    {
        ScoreScale = o.ScoreScale; MissesScale = o.MissesScale; AccuracyScale = o.AccuracyScale;
        SongInfoScale = o.SongInfoScale; StatusInfoScale = o.StatusInfoScale;
        ComboScale = o.ComboScale; JudgementScale = o.JudgementScale;
        OpponentIconScale = o.OpponentIconScale; PlayerIconScale = o.PlayerIconScale;
        PlayerStrumlineScale = o.PlayerStrumlineScale; OpponentStrumlineScale = o.OpponentStrumlineScale;
    }

    // ── Strumlines (unified with NoteRenderer / Game1 static state) ──────────
    [JsonIgnore]
    public bool StrumlinesVisible
    {
        get => NoteRenderer.ShowStrumlines;
        set => NoteRenderer.ShowStrumlines = value;
    }

    [JsonIgnore]
    public bool PlayerStrumlineVisible
    {
        get => !NoteRenderer.HidePlayerStrumline;
        set => NoteRenderer.HidePlayerStrumline = !value;
    }

    [JsonIgnore]
    public bool OpponentStrumlineVisible
    {
        get => !Game1.HideOpponentStrumline;
        set => Game1.HideOpponentStrumline = !value;
    }

    [JsonIgnore]
    public bool ExtraStrumlinesVisible
    {
        get => !Game1.HideExtraStrumlines;
        set => Game1.HideExtraStrumlines = !value;
    }

    // ── File Path ────────────────────────────────────────────────────────────
    private static string SettingsPath
    {
        get
        {
            // Save in Assets/data folder for better organization.
            // Desktop: use the real executable folder. In single-file builds
            // AppContext.BaseDirectory is a temp extraction folder, so the layout
            // would not persist next to the exe.
#if ANDROID
            string baseDir = AppContext.BaseDirectory;
#else
            string baseDir = PlatformPaths.GetAppBaseDirectory();
#endif
            string assetsPath = Path.Combine(baseDir, "Assets", "data");
            
            // Create directory if it doesn't exist
            if (!Directory.Exists(assetsPath))
            {
                Directory.CreateDirectory(assetsPath);
            }
            
            return Path.Combine(assetsPath, "hud_settings.json");
        }
    }

    /// <summary>
    /// Save current HUD settings to disk.
    /// </summary>
    public void Save()
    {
        try
        {
            var options = new JsonSerializerOptions
            {
                WriteIndented = true,
                DefaultIgnoreCondition = JsonIgnoreCondition.Never
            };

            string json = JsonSerializer.Serialize(this, options);
            File.WriteAllText(SettingsPath, json);
            Console.WriteLine($"[HudSettings] Saved to {SettingsPath}");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[HudSettings] Save failed: {ex.Message}");
        }
    }

    /// <summary>
    /// Load HUD settings from disk. Returns default settings if file doesn't exist.
    /// </summary>
    public static HudSettings Load()
    {
        try
        {
            if (!File.Exists(SettingsPath))
            {
                Console.WriteLine($"[HudSettings] No settings file found at {SettingsPath}");
                Console.WriteLine($"[HudSettings] Creating new settings file with defaults");
                
                // Create new settings with defaults
                var newSettings = new HudSettings();
                
                // Save it immediately so the file exists
                newSettings.Save();
                
                return newSettings;
            }

            string json = File.ReadAllText(SettingsPath);
            var settings = JsonSerializer.Deserialize<HudSettings>(json);
            
            if (settings == null)
            {
                Console.WriteLine($"[HudSettings] Failed to deserialize, creating new defaults");
                var newSettings = new HudSettings();
                newSettings.Save();
                return newSettings;
            }

            Console.WriteLine($"[HudSettings] Loaded from {SettingsPath}");
            return settings;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[HudSettings] Load failed: {ex.Message}, creating new defaults");
            var newSettings = new HudSettings();
            
            // Try to save the defaults
            try
            {
                newSettings.Save();
            }
            catch (Exception saveEx)
            {
                Console.WriteLine($"[HudSettings] Failed to save defaults: {saveEx.Message}");
            }
            
            return newSettings;
        }
    }

    /// <summary>
    /// Reset all positions and visibility to default values.
    /// </summary>
    public void ResetToDefaults()
    {
        MasterHudVisible = true;
        HealthBarPosition = new HudPosition(340, 650);
        HealthBarSize = new HudPosition(600, 20);
        HealthBarVisible = true;
        ScorePosition = new HudPosition(440, 680);
        ScoreVisible = true;
        MissesPosition = new HudPosition(600, 680);
        MissesVisible = true;
        AccuracyPosition = new HudPosition(720, 680);
        AccuracyVisible = true;
        TimerBarPosition = new HudPosition(340, 4);
        TimerBarSize = new HudPosition(600, 32);
        TimerBarVisible = true;
        SongInfoPosition = new HudPosition(17, 7);
        SongInfoVisible = true;
        StatusInfoPosition = new HudPosition(20, 692);
        StatusInfoVisible = true;
        ComboPosition = new HudPosition(610, 360);
        ComboVisible = true;
        JudgementPosition = new HudPosition(600, 300);
        JudgementVisible = true;
        OpponentIconPosition = new HudPosition(290, 610);
        OpponentIconVisible = true;
        PlayerIconPosition = new HudPosition(890, 610);
        PlayerIconVisible = true;
        PlayerStrumlineOffset = new HudPosition(0, 0);
        OpponentStrumlineOffset = new HudPosition(0, 0);
        StrumlinesVisible = true;
        PlayerStrumlineVisible = true;
        OpponentStrumlineVisible = true;
        ExtraStrumlinesVisible = true;
        CopyScalesFrom(new HudSettings());

        Console.WriteLine("[HudSettings] Reset to default values");
    }

    /// <summary>
    /// Get adjusted positions for Downscroll mode.
    /// In Downscroll, health bar and stats move to the top, timer moves to bottom.
    /// </summary>
    public HudSettings GetDownscrollAdjusted(int screenHeight)
    {
        var adjusted = new HudSettings
        {
            MasterHudVisible = MasterHudVisible,

            // Flip health bar to top
            HealthBarPosition = new HudPosition(HealthBarPosition.X, 50),
            HealthBarSize = HealthBarSize,
            HealthBarVisible = HealthBarVisible,
            
            // Flip stats to top (below health bar)
            ScorePosition = new HudPosition(ScorePosition.X, 80),
            ScoreVisible = ScoreVisible,
            MissesPosition = new HudPosition(MissesPosition.X, 80),
            MissesVisible = MissesVisible,
            AccuracyPosition = new HudPosition(AccuracyPosition.X, 80),
            AccuracyVisible = AccuracyVisible,
            
            // Flip timer to bottom
            TimerBarPosition = new HudPosition(TimerBarPosition.X, screenHeight - 40),
            TimerBarSize = TimerBarSize,
            TimerBarVisible = TimerBarVisible,
            
            // Keep song info and status info positions from base settings
            SongInfoPosition = SongInfoPosition,
            SongInfoVisible = SongInfoVisible,
            StatusInfoPosition = StatusInfoPosition,
            StatusInfoVisible = StatusInfoVisible,
            
            // Keep combo and judgement positions from base settings (user can customize in HUD editor)
            ComboPosition = ComboPosition,
            ComboVisible = ComboVisible,
            JudgementPosition = JudgementPosition,
            JudgementVisible = JudgementVisible,
            
            // Flip icon positions to top (for Fixed at Ends mode)
            OpponentIconPosition = new HudPosition(OpponentIconPosition.X, 10),
            OpponentIconVisible = OpponentIconVisible,
            PlayerIconPosition = new HudPosition(PlayerIconPosition.X, 10),
            PlayerIconVisible = PlayerIconVisible,

            PlayerStrumlineOffset = PlayerStrumlineOffset,
            OpponentStrumlineOffset = OpponentStrumlineOffset
        };
        adjusted.CopyScalesFrom(this);

        return adjusted;
    }
}
