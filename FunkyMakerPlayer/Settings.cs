using System;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
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
        public bool  Botplay           { get; set; } = false;
        /// <summary>Legacy name of <see cref="Botplay"/> (read from old files only).</summary>
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public bool? Autoplay          { get; set; }
        public bool  Downscroll        { get; set; } = false;
        public bool  VSync             { get; set; } = true;
        public float ScrollMultiplier  { get; set; } = 2.5f;
        public bool  ShowTimer         { get; set; } = true;
        /// <summary>Seconds per Timeskip step; 0 = Timeskip off.</summary>
        public float TimeskipSeconds   { get; set; } = 0f;
        /// <summary>Legacy on/off switch (read from old files only): false meant Timeskip off.</summary>
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public bool? TimeskipEnabled   { get; set; }
        public int   TimerColorPreset  { get; set; } = 1;
        
        // ── Timer Bar Options (V-Slice style with Legacy support) ─────────────
        public string TimerPreset      { get; set; } = "HUD Editor";  // Legacy, HUD Editor ("Custom HUD" in old files), Top, Left, Middle, Right, Strumline
        public string TimerTextMode    { get; set; } = "Time Left";   // Time Left, Time Elapsed, Song Name, Song Name + Difficulty, None
        public bool   TimerForceTop    { get; set; } = false;         // Ignore downscroll (only for Top/Left/Middle/Right/Strumline presets)
        public bool  ShowDebugInfo     { get; set; } = false;
        public bool  ShowFpsCounter    { get; set; } = false;
        /// <summary>(fork) Render gameplay at the window's resolution (true) or the classic fixed 720p (false).</summary>
        public bool  NativeRenderResolution { get; set; } = true;
        public bool  Midlescroll      { get; set; } = false;
        /// <summary>Master volume: scales every sound.</summary>
        public float? MasterVolume     { get; set; }
        /// <summary>Legacy name of <see cref="MasterVolume"/> (read from old files only).</summary>
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public float? MusicVolume      { get; set; }
        public float SongVolume        { get; set; } = 1.0f;   // song music, scaled by master
        public float SfxVolume         { get; set; } = 1.0f;   // UI sound effects (UiSfx)
        public float MenuMusicVolume   { get; set; } = 1.0f;   // menu background music (OnlineMenuMusic)
        /// <summary>Fork: settings for osu! songs (Osu/OsuSettings.cs).</summary>
        public Osu.OsuSettings.Data? Osu { get; set; }
        public string NoteStyleName    { get; set; } = DefaultMods.NoteStyleName;
        public string NoteSplashStyleName { get; set; } = DefaultMods.SplashName;
        /// <summary>Fork: UI font file in mods/fonts chosen in Settings → Font Style (null = default).</summary>
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public string? UiFontFile { get; set; }
        public bool  ShowStrumlines       { get; set; } = true;
        public bool  HidePlayerStrumline  { get; set; } = false;
        public bool  HideExtraStrumlines { get; set; } = true;
        public bool  HideOpponentStrumline { get; set; } = false;
        public bool  ShowCombo         { get; set; } = true;
        
        // ── Gameplay Quality of Life Settings ────────────────────────────────
        /// <summary>
        /// Ghost Tapping: When true, pressing keys without notes has no penalty.
        /// When false, ghost taps break combo and count as miss (competitive mode).
        /// </summary>
        public bool  GhostTapping      { get; set; } = false;
        public bool  ShowKeyLabels     { get; set; } = true;
        public float LaneUnderlay      { get; set; } = 0f;
        
        // Input buffer / safe frames are no longer settings (fork): NoteRenderer's defaults always apply,
        // and values saved by older versions are ignored.

        /// <summary>Fork: global song offset in ms (notes later when positive).</summary>
        public int GlobalOffsetMs { get; set; } = 0;
        /// <summary>Fork: play the miss sound.</summary>
        public bool MissSounds { get; set; } = true;
        /// <summary>Fork: Skip Breaks (Space skips long stretches without arrows; Game1.BreakSkip.cs).</summary>
        public bool SkipBreaks { get; set; } = false;
        
        /// <summary>
        /// Health icons: 0 = move with the health bar divider (FNF style), 1 = pinned to the bar ends.
        /// </summary>
        public int IconMode { get; set; } = 0;
        
        // ── Arrow Effects Settings ────────────────────────────────────────────
        /// <summary>
        /// Arrow Effects Enabled: Master toggle for visual effects on receptor arrows.
        /// Default: false (disabled by default)
        /// </summary>
        public bool  ArrowEffectsEnabled     { get; set; } = false;
        
        // ── Android Touch Controls Settings ───────────────────────────────────
        /// <summary>
        /// Hide Touch Hitbox Colors: When true, touch hitboxes are completely invisible (android only).
        /// Default: false (hitboxes show subtle colors when pressed)
        /// </summary>
        public bool  HideTouchHitboxColors   { get; set; } = false;

        /// <summary>(fork) Opacity of a touch hitbox while it is pressed, 0-1 (android only). Default 30 %.</summary>
        public float TouchHitboxOpacity      { get; set; } = 0.3f;

        /// <summary>(fork) On-screen menu buttons (android only): 0 = Auto, 1 = On, 2 = Off.</summary>
        public int   MenuButtonsMode         { get; set; } = 0;
        
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
        /// Default: A, S, K, L
        /// </summary>
        public string[] PlayerKeyBindings { get; set; } = { "A", "S", "K", "L" };

        /// <summary>Fork: rebindable hotkeys (Settings → CONTROLS), action → [primary, alternate].</summary>
        public System.Collections.Generic.Dictionary<string, string[]>? Hotkeys { get; set; }

        /// <summary>Fork: frame rate cap in FPS (0 = unlimited).</summary>
        public int FpsCap { get; set; } = 0;
    }

    public static void Load(Game1 game)
    {
        try
        {
            if (!File.Exists(_path)) return;
            var d = JsonSerializer.Deserialize<Data>(File.ReadAllText(_path));
            if (d == null) return;

            NoteRenderer.Botplay           = d.Autoplay ?? d.Botplay;
            NoteRenderer.Downscroll        = d.Downscroll;
            NoteRenderer.ScrollMultiplier  = d.ScrollMultiplier <= 0f ? 1.0f : Math.Clamp(d.ScrollMultiplier, 0.25f, 5.0f);
            NoteRenderer.Midlescroll       = d.Midlescroll;
            NoteRenderer.GhostTapping      = d.GhostTapping;
            NoteRenderer.ShowKeyLabels     = d.ShowKeyLabels;
            NoteRenderer.LaneUnderlay      = Math.Clamp(d.LaneUnderlay, 0f, NoteRenderer.LaneUnderlayMax);
            NoteRenderer.MissSounds        = d.MissSounds;
            Game1.SkipBreaks               = d.SkipBreaks;
            Game1.GlobalOffsetMs           = Math.Clamp(d.GlobalOffsetMs, -Game1.MaxOffsetMs, Game1.MaxOffsetMs);
            Game1.ShowTimer                = d.ShowTimer;
            Game1.TimeskipSeconds          = d.TimeskipEnabled == false || d.TimeskipSeconds <= 0f
                                                 ? 0f
                                                 : Math.Clamp(d.TimeskipSeconds, 5f, 60f);
            Game1.TimerColorPreset         = d.TimerColorPreset;
            Game1.TimerPreset              = d.TimerPreset == "Custom HUD" ? "HUD Editor" : d.TimerPreset;
            Game1.TimerTextMode            = d.TimerTextMode;
            Game1.TimerForceTop            = d.TimerForceTop;
            Game1.ShowDebugInfo            = d.ShowDebugInfo;
            Game1.ShowFpsCounter           = d.ShowFpsCounter;
            Game1.NativeRenderResolution   = d.NativeRenderResolution;
            Game1.MasterVolume             = Math.Clamp(d.MasterVolume ?? d.MusicVolume ?? 0.5f, 0f, 1f);
            Game1.SongVolume               = Math.Clamp(d.SongVolume, 0f, 1f);
            UI.UiSfx.Volume                = Math.Clamp(d.SfxVolume, 0f, 1f);
            Managers.OnlineMenuMusic.Volume = Math.Clamp(d.MenuMusicVolume, 0f, 1f);
            FunkyMakerPlayer.Osu.OsuSettings.Import(d.Osu);
            // Old saves say "Default"; show the current default's name instead
            Game1.NoteStyleName            = DefaultMods.IsDefaultNoteStyle(d.NoteStyleName)
                                                 ? DefaultMods.NoteStyleName
                                                 : d.NoteStyleName;
            // Old saves say "Default" (the code-drawn splash); that now means the default splash
            Game1.NoteSplashStyleName      = DefaultMods.IsDefaultSplash(d.NoteSplashStyleName)
                                                 ? DefaultMods.SplashName
                                                 : d.NoteSplashStyleName;
            Game1.UiFontFile               = d.UiFontFile;
            NoteRenderer.ShowStrumlines    = d.ShowStrumlines;
            NoteRenderer.HidePlayerStrumline = d.HidePlayerStrumline;
            Game1.HideExtraStrumlines      = d.HideExtraStrumlines;
            Game1.HideOpponentStrumline    = d.HideOpponentStrumline;
            Game1.ShowCombo                = d.ShowCombo;
            Game1.IconMode                 = d.IconMode;
            ArrowEffectManager.Enabled         = d.ArrowEffectsEnabled;
            ArrowEffectManager.SelectedEffect  = d.ArrowSelectedEffect;
            Game1.SelectedCountdown            = d.SelectedCountdown;
            Game1.HideTouchHitboxColors        = d.HideTouchHitboxColors;
            Game1.TouchHitboxOpacity           = Math.Clamp(d.TouchHitboxOpacity, 0f, 1f);
            Game1.MenuButtonsMode              = Math.Clamp(d.MenuButtonsMode, 0, 2);
            
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
            
            Controls.Import(d.Hotkeys);
            Game1.FpsCap = Array.IndexOf(Game1.FpsCapOptions, d.FpsCap) >= 0 ? d.FpsCap : 0;

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
                Botplay          = NoteRenderer.Botplay,
                Downscroll       = NoteRenderer.Downscroll,
                VSync            = vsync,
                ScrollMultiplier = NoteRenderer.ScrollMultiplier,
                Midlescroll      = NoteRenderer.Midlescroll,
                GhostTapping     = NoteRenderer.GhostTapping,
                ShowKeyLabels    = NoteRenderer.ShowKeyLabels,
                LaneUnderlay     = NoteRenderer.LaneUnderlay,
                GlobalOffsetMs   = Game1.GlobalOffsetMs,
                MissSounds       = NoteRenderer.MissSounds,
                SkipBreaks       = Game1.SkipBreaks,
                ShowTimer        = Game1.ShowTimer,
                TimeskipSeconds  = Game1.TimeskipSeconds,
                TimerColorPreset = Game1.TimerColorPreset,
                TimerPreset      = Game1.TimerPreset,
                TimerTextMode    = Game1.TimerTextMode,
                TimerForceTop    = Game1.TimerForceTop,
                ShowDebugInfo    = Game1.ShowDebugInfo,
                ShowFpsCounter   = Game1.ShowFpsCounter,
                NativeRenderResolution = Game1.NativeRenderResolution,
                MasterVolume     = Game1.MasterVolume,
                SongVolume       = Game1.SongVolume,
                SfxVolume        = UI.UiSfx.Volume,
                MenuMusicVolume  = Managers.OnlineMenuMusic.Volume,
                Osu              = FunkyMakerPlayer.Osu.OsuSettings.Export(),
                NoteStyleName     = Game1.NoteStyleName,
                NoteSplashStyleName = Game1.NoteSplashStyleName,
                UiFontFile          = Game1.UiFontFile,
                ShowStrumlines      = NoteRenderer.ShowStrumlines,
                HidePlayerStrumline = NoteRenderer.HidePlayerStrumline,
                HideExtraStrumlines = Game1.HideExtraStrumlines,
                HideOpponentStrumline = Game1.HideOpponentStrumline,
                ShowCombo = Game1.ShowCombo,
                IconMode = Game1.IconMode,
                ArrowEffectsEnabled = ArrowEffectManager.Enabled,
                ArrowSelectedEffect = ArrowEffectManager.SelectedEffect,
                SelectedCountdown = Game1.SelectedCountdown,
                HideTouchHitboxColors = Game1.HideTouchHitboxColors,
                TouchHitboxOpacity = Game1.TouchHitboxOpacity,
                MenuButtonsMode = Game1.MenuButtonsMode,
                PlayerKeyBindings = new string[]
                {
                    NoteRenderer.PlayerKeys[0].ToString(),
                    NoteRenderer.PlayerKeys[1].ToString(),
                    NoteRenderer.PlayerKeys[2].ToString(),
                    NoteRenderer.PlayerKeys[3].ToString()
                },
                Hotkeys = Controls.Export(),
                FpsCap = Game1.FpsCap,
            };
            string tmp = _path + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(d,
                new JsonSerializerOptions { WriteIndented = true }));
            File.Move(tmp, _path, overwrite: true);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[Settings] Save failed: {ex.Message}");
        }
    }
}
