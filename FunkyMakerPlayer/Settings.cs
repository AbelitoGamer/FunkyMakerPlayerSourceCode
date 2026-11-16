using System;
using System.IO;
using System.Text.Json;
using Microsoft.Xna.Framework;

namespace FunkyMakerPlayer;

/// <summary>
/// Persists all user settings to %AppData%/FunkyMakerPlayer/settings.json.
/// On Android: uses app-specific storage.
/// Call Load() at startup and Save() whenever a setting changes.
/// </summary>
public static class Settings
{
    private static readonly string _path = GetSettingsPath();
    
    private static string GetSettingsPath()
    {
        #if ANDROID
        // Android: use app-specific internal storage
        string appData = Environment.GetFolderPath(Environment.SpecialFolder.Personal);
        return Path.Combine(appData, "settings.json");
        #else
        // PC: use AppData/Roaming
        string appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        return Path.Combine(appData, "FunkyMakerPlayer", "settings.json");
        #endif
    }

    private class Data
    {
        public bool  Autoplay          { get; set; } = false;
        public bool  Downscroll        { get; set; } = false;
        public bool  VSync             { get; set; } = true;
        public float ScrollMultiplier  { get; set; } = 1.0f;
        public bool  ShowTimer         { get; set; } = true;
        public bool  TimeskipEnabled   { get; set; } = true;
        public float TimeskipSeconds   { get; set; } = 10f;
        public int   TimerColorPreset  { get; set; } = 0;
        
        // ── Timer Bar Options (V-Slice style with Legacy support) ─────────────
        public string TimerPreset      { get; set; } = "Custom HUD";  // Legacy, Custom HUD, Top, Left, Middle, Right, Strumline
        public string TimerTextMode    { get; set; } = "Time Left";   // Time Left, Time Elapsed, Song Name, Song Name + Difficulty, None
        public bool   TimerForceTop    { get; set; } = false;         // Ignore downscroll (only for Top/Left/Middle/Right/Strumline presets)
        public bool  ShowDebugInfo     { get; set; } = false;
        public bool  ShowFpsCounter    { get; set; } = false;
        public bool  Midlescroll       { get; set; } = false;
        public float MusicVolume       { get; set; } = 0.85f;
        public float SfxVolume         { get; set; } = 0.8f;   // UI sound effects (UiSfx)
        public float MenuMusicVolume   { get; set; } = 0.7f;   // menu background music (OnlineMenuMusic)
        public bool  BilinearFiltering { get; set; } = true;
        public string NoteStyleName    { get; set; } = "Default";
        public string NoteSplashStyleName { get; set; } = "Default";
        public bool  ShowStrumlines       { get; set; } = true;
        public bool  HidePlayerStrumline  { get; set; } = false;
        public bool  HideExtraStrumlines { get; set; } = false;
        public bool  HideOpponentStrumline { get; set; } = false;
        public bool  ShowCombo         { get; set; } = true;
        
        // ── Color Grading Shader (FNF Erect-Style - Subtle) ──────────────────
        public bool  ColorGradeEnabled        { get; set; } = false;
        public float ColorGradeHue            { get; set; } = -0.05f;
        public float ColorGradeSaturation     { get; set; } = 0.80f;
        public float ColorGradeBrightness     { get; set; } = 0.00f;
        public float ColorGradeContrast       { get; set; } = 1.00f;
        public float ColorGradeShadowAmount   { get; set; } = 0.35f;
        public float ColorGradeShadowTintR    { get; set; } = 0.65f;
        public float ColorGradeShadowTintG    { get; set; } = 0.55f;
        public float ColorGradeShadowTintB    { get; set; } = 0.90f;
        
        // ── Gameplay Quality of Life Settings ────────────────────────────────
        /// <summary>
        /// Ghost Tapping: When true, pressing keys without notes has no penalty.
        /// When false, ghost taps break combo and count as miss (competitive mode).
        /// </summary>
        public bool  GhostTapping      { get; set; } = true;
        
        /// <summary>
        /// Input buffer in seconds. Allows lowering pre-hit anticipation below default.
        /// Default / Max: 0.010 (10ms). Range: 0.000 to 0.010 (0-10ms)
        /// </summary>
        public float InputBuffer       { get; set; } = 0.010f;
        
        /// <summary>
        /// Safe frames at song start in seconds. Misses don't count during this time.
        /// Default / Max: 0.083 (~5 frames at 60fps). Range: 0.000 to 0.083 (0-83ms)
        /// </summary>
        public float SafeFramesTime    { get; set; } = 0.083f;
        
        /// <summary>
        /// Camera Follow Player: When enabled, camera smoothly follows the singing character.
        /// Automatically switches between player and opponent based on who has upcoming notes.
        /// Uses actual object positions from movement events.
        /// Default: false (disabled)
        /// </summary>
        public bool CameraFollowPlayer { get; set; } = false;
        
        /// <summary>
        /// Icon positioning mode: Fixed (FNF base - icons move with health %) or Dynamic (icons stay at ends)
        /// Default: 0 (Fixed/FNF Base style)
        /// </summary>
        public int IconMode { get; set; } = 0; // 0 = Fixed (FNF base), 1 = Dynamic
        
        // ── Arrow Effects Settings ────────────────────────────────────────────
        /// <summary>
        /// Arrow Effects Enabled: Master toggle for visual effects on receptor arrows.
        /// Default: false (disabled by default)
        /// </summary>
        public bool  ArrowEffectsEnabled     { get; set; } = false;
        
        /// <summary>
        /// Arrow Effect Intensity: Multiplier for effect strength (0.0 to 1.0).
        /// Default: 1.0 (full intensity)
        /// </summary>
        public float ArrowEffectIntensity    { get; set; } = 1.0f;
        
        /// <summary>
        /// Arrow Effect Chance: Probability per beat to trigger an effect (0.0 to 1.0).
        /// Default: 0.25 (25% chance)
        /// </summary>
        public float ArrowEffectChance       { get; set; } = 0.25f;
        
        // ── Android Touch Controls Settings ───────────────────────────────────
        /// <summary>
        /// Hide Touch Hitbox Colors: When true, touch hitboxes are completely invisible (android only).
        /// Default: false (hitboxes show subtle colors when pressed)
        /// </summary>
        public bool  HideTouchHitboxColors   { get; set; } = false;
        
        /// <summary>
        /// Selected Arrow Effect: 0=Random, 1=WhiteGlow, 2=BounceVertical, 3=Spin360, 4=BounceHorizontal, 5=Dizzy
        /// Default: 0 (Random)
        /// </summary>
        public int   ArrowSelectedEffect     { get; set; } = 0;
        
        // ── Countdown Customization ──────────────────────────────────────────
        /// <summary>
        /// Selected countdown pack name from mods/countdowns/[name]/
        /// Default: "default"
        /// </summary>
        public string SelectedCountdown { get; set; } = "default";
        
        // ── Key Bindings ──────────────────────────────────────────────────────
        /// <summary>
        /// Custom key bindings for gameplay (4 lanes: Left, Down, Up, Right).
        /// Stored as key names (e.g., "D", "F", "J", "K").
        /// Default: D, F, J, K
        /// </summary>
        public string[] PlayerKeyBindings { get; set; } = { "D", "F", "J", "K" };
    }

    public static void Load(Game1 game)
    {
        try
        {
            if (!File.Exists(_path)) return;
            var d = JsonSerializer.Deserialize<Data>(File.ReadAllText(_path));
            if (d == null) return;

            NoteRenderer.Autoplay          = d.Autoplay;
            NoteRenderer.Downscroll        = d.Downscroll;
            NoteRenderer.ScrollMultiplier  = d.ScrollMultiplier <= 0f ? 1.0f : Math.Clamp(d.ScrollMultiplier, 0.25f, 5.0f);
            NoteRenderer.Midlescroll       = d.Midlescroll;
            NoteRenderer.GhostTapping      = d.GhostTapping;
            NoteRenderer.InputBuffer       = Math.Clamp(d.InputBuffer, 0f, 0.010f);
            NoteRenderer.SafeFramesTime    = Math.Clamp(d.SafeFramesTime, 0f, 0.083f);
            NoteRenderer.CameraFollowPlayer = d.CameraFollowPlayer;
            Game1.ShowTimer                = d.ShowTimer;
            Game1.TimeskipEnabled          = d.TimeskipEnabled;
            Game1.TimeskipSeconds          = Math.Clamp(d.TimeskipSeconds, 5f, 60f);
            Game1.TimerColorPreset         = d.TimerColorPreset;
            Game1.TimerPreset              = d.TimerPreset;
            Game1.TimerTextMode            = d.TimerTextMode;
            Game1.TimerForceTop            = d.TimerForceTop;
            Game1.ShowDebugInfo            = d.ShowDebugInfo;
            Game1.ShowFpsCounter           = d.ShowFpsCounter;
            Game1.MusicVolume              = Math.Clamp(d.MusicVolume, 0f, 1f);
            UI.UiSfx.Volume                = Math.Clamp(d.SfxVolume, 0f, 1f);
            Managers.OnlineMenuMusic.Volume = Math.Clamp(d.MenuMusicVolume, 0f, 1f);
            Game1.BilinearFiltering        = d.BilinearFiltering;
            Game1.NoteStyleName            = d.NoteStyleName;
            Game1.NoteSplashStyleName      = d.NoteSplashStyleName;
            NoteRenderer.ShowStrumlines    = d.ShowStrumlines;
            NoteRenderer.HidePlayerStrumline = d.HidePlayerStrumline;
            Game1.HideExtraStrumlines      = d.HideExtraStrumlines;
            Game1.HideOpponentStrumline    = d.HideOpponentStrumline;
            Game1.ShowCombo                = d.ShowCombo;
            Game1.ColorGradeEnabled        = d.ColorGradeEnabled;
            Game1.ColorGradeHue            = d.ColorGradeHue;
            Game1.ColorGradeSaturation     = d.ColorGradeSaturation;
            Game1.ColorGradeBrightness     = d.ColorGradeBrightness;
            Game1.ColorGradeContrast       = d.ColorGradeContrast;
            Game1.ColorGradeShadowAmount   = d.ColorGradeShadowAmount;
            Game1.ColorGradeShadowTintR    = d.ColorGradeShadowTintR;
            Game1.ColorGradeShadowTintG    = d.ColorGradeShadowTintG;
            Game1.ColorGradeShadowTintB    = d.ColorGradeShadowTintB;
            Game1.IconMode                 = d.IconMode;
            ArrowEffectManager.Enabled         = d.ArrowEffectsEnabled;
            ArrowEffectManager.EffectIntensity = d.ArrowEffectIntensity;
            ArrowEffectManager.EffectChance    = d.ArrowEffectChance;
            ArrowEffectManager.SelectedEffect  = d.ArrowSelectedEffect;
            Game1.SelectedCountdown            = d.SelectedCountdown;
            Game1.HideTouchHitboxColors        = d.HideTouchHitboxColors;
            
            // Load key bindings
            if (d.PlayerKeyBindings != null && d.PlayerKeyBindings.Length == 4)
            {
                for (int i = 0; i < 4; i++)
                {
                    if (Enum.TryParse<Microsoft.Xna.Framework.Input.Keys>(d.PlayerKeyBindings[i], out var key))
                    {
                        NoteRenderer.PlayerKeys[i] = key;
                    }
                }
            }
            
            // VSync is applied via GraphicsDeviceManager — handled in Game1.ApplySettings
            game.ApplyLoadedSettings(d.VSync);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[Settings] Load failed: {ex.Message}");
        }
    }

    public static void Save(bool vsync)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
            var d = new Data
            {
                Autoplay         = NoteRenderer.Autoplay,
                Downscroll       = NoteRenderer.Downscroll,
                VSync            = vsync,
                ScrollMultiplier = NoteRenderer.ScrollMultiplier,
                Midlescroll      = NoteRenderer.Midlescroll,
                GhostTapping     = NoteRenderer.GhostTapping,
                InputBuffer      = NoteRenderer.InputBuffer,
                SafeFramesTime   = NoteRenderer.SafeFramesTime,
                CameraFollowPlayer = NoteRenderer.CameraFollowPlayer,
                ShowTimer        = Game1.ShowTimer,
                TimeskipEnabled  = Game1.TimeskipEnabled,
                TimeskipSeconds  = Game1.TimeskipSeconds,
                TimerColorPreset = Game1.TimerColorPreset,
                TimerPreset      = Game1.TimerPreset,
                TimerTextMode    = Game1.TimerTextMode,
                TimerForceTop    = Game1.TimerForceTop,
                ShowDebugInfo    = Game1.ShowDebugInfo,
                ShowFpsCounter   = Game1.ShowFpsCounter,
                MusicVolume      = Game1.MusicVolume,
                SfxVolume        = UI.UiSfx.Volume,
                MenuMusicVolume  = Managers.OnlineMenuMusic.Volume,
                BilinearFiltering = Game1.BilinearFiltering,
                NoteStyleName     = Game1.NoteStyleName,
                NoteSplashStyleName = Game1.NoteSplashStyleName,
                ShowStrumlines      = NoteRenderer.ShowStrumlines,
                HidePlayerStrumline = NoteRenderer.HidePlayerStrumline,
                HideExtraStrumlines = Game1.HideExtraStrumlines,
                HideOpponentStrumline = Game1.HideOpponentStrumline,
                ShowCombo = Game1.ShowCombo,
                ColorGradeEnabled = Game1.ColorGradeEnabled,
                ColorGradeHue = Game1.ColorGradeHue,
                ColorGradeSaturation = Game1.ColorGradeSaturation,
                ColorGradeBrightness = Game1.ColorGradeBrightness,
                ColorGradeContrast = Game1.ColorGradeContrast,
                ColorGradeShadowAmount = Game1.ColorGradeShadowAmount,
                ColorGradeShadowTintR = Game1.ColorGradeShadowTintR,
                ColorGradeShadowTintG = Game1.ColorGradeShadowTintG,
                ColorGradeShadowTintB = Game1.ColorGradeShadowTintB,
                IconMode = Game1.IconMode,
                ArrowEffectsEnabled = ArrowEffectManager.Enabled,
                ArrowEffectIntensity = ArrowEffectManager.EffectIntensity,
                ArrowEffectChance = ArrowEffectManager.EffectChance,
                ArrowSelectedEffect = ArrowEffectManager.SelectedEffect,
                SelectedCountdown = Game1.SelectedCountdown,
                HideTouchHitboxColors = Game1.HideTouchHitboxColors,
                PlayerKeyBindings = new string[]
                {
                    NoteRenderer.PlayerKeys[0].ToString(),
                    NoteRenderer.PlayerKeys[1].ToString(),
                    NoteRenderer.PlayerKeys[2].ToString(),
                    NoteRenderer.PlayerKeys[3].ToString()
                }
            };
            File.WriteAllText(_path, JsonSerializer.Serialize(d,
                new JsonSerializerOptions { WriteIndented = true }));
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[Settings] Save failed: {ex.Message}");
        }
    }
}
