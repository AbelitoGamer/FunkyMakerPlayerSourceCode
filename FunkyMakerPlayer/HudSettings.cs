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

    // ── Health Bar ───────────────────────────────────────────────────────────
    public HudPosition HealthBarPosition { get; set; } = new HudPosition(340, 650);
    public HudPosition HealthBarSize { get; set; } = new HudPosition(600, 20);

    // ── Score, Misses, Accuracy (below health bar) ──────────────────────────
    public HudPosition ScorePosition { get; set; } = new HudPosition(440, 680);
    public HudPosition MissesPosition { get; set; } = new HudPosition(600, 680);
    public HudPosition AccuracyPosition { get; set; } = new HudPosition(720, 680);

    // ── Timer Bar ────────────────────────────────────────────────────────────
    public HudPosition TimerBarPosition { get; set; } = new HudPosition(340, 4);
    public HudPosition TimerBarSize { get; set; } = new HudPosition(600, 32);

    // ── Song Info (top-left) ─────────────────────────────────────────────────
    public HudPosition SongInfoPosition { get; set; } = new HudPosition(17, 7);

    // ── Combo Display (center screen, above player strumline) ───────────────
    // Position is top-left corner of text (no automatic centering)
    // Centered at X=640 for 1280px width (middle of player strumline)
    public HudPosition ComboPosition { get; set; } = new HudPosition(610, 360);

    // ── Judgement Text (above combo) ─────────────────────────────────────────
    // Position is top-left corner of text (no automatic centering)
    // Centered at X=640 for 1280px width (middle of player strumline)
    public HudPosition JudgementPosition { get; set; } = new HudPosition(600, 300);

    // ── Health Icons (for "Fixed at Ends" IconMode) ──────────────────────────
    // These positions are only used when IconMode = 1 (Fixed at Ends)
    public HudPosition OpponentIconPosition { get; set; } = new HudPosition(290, 610);
    public HudPosition PlayerIconPosition { get; set; } = new HudPosition(890, 610);

    // ── File Path ────────────────────────────────────────────────────────────
    private static string SettingsPath
    {
        get
        {
            // Save in Assets/data folder for better organization
            string assetsPath = Path.Combine(AppContext.BaseDirectory, "Assets", "data");
            
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
    /// Reset all positions to default values.
    /// </summary>
    public void ResetToDefaults()
    {
        HealthBarPosition = new HudPosition(340, 650);
        HealthBarSize = new HudPosition(600, 20);
        ScorePosition = new HudPosition(440, 680);
        MissesPosition = new HudPosition(600, 680);
        AccuracyPosition = new HudPosition(720, 680);
        TimerBarPosition = new HudPosition(340, 4);
        TimerBarSize = new HudPosition(600, 32);
        SongInfoPosition = new HudPosition(17, 7);
        ComboPosition = new HudPosition(610, 360);
        JudgementPosition = new HudPosition(600, 300);
        OpponentIconPosition = new HudPosition(290, 610);
        PlayerIconPosition = new HudPosition(890, 610);
        
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
            // Flip health bar to top
            HealthBarPosition = new HudPosition(340, 50),
            HealthBarSize = new HudPosition(600, 20),
            
            // Flip stats to top (below health bar)
            ScorePosition = new HudPosition(440, 80),
            MissesPosition = new HudPosition(600, 80),
            AccuracyPosition = new HudPosition(720, 80),
            
            // Flip timer to bottom
            TimerBarPosition = new HudPosition(340, screenHeight - 40),
            TimerBarSize = TimerBarSize,
            
            // Keep song info at top-left (unchanged)
            SongInfoPosition = SongInfoPosition,
            
            // Keep combo and judgement positions from base settings (user can customize in HUD editor)
            ComboPosition = ComboPosition,
            JudgementPosition = JudgementPosition,
            
            // Flip icon positions to top (for Fixed at Ends mode)
            OpponentIconPosition = new HudPosition(290, 10),
            PlayerIconPosition = new HudPosition(890, 10)
        };
        
        return adjusted;
    }
}
