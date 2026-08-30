using System;
using System.Collections.Generic;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;

namespace FunkyMakerPlayer.Managers;

/// <summary>
/// Settings Manager with tabbed interface (Codename Engine style).
/// Tabs: GAMEPLAY, CONTROLS, GRAPHICS, AUDIO, ADVANCED
/// </summary>
public class SettingsManager
{
    // ── State ────────────────────────────────────────────────────────────────
    public bool IsActive { get; set; } = false;
    public int Selection { get; set; } = 0;
    public int CurrentTab { get; set; } = 0;  // 0=Gameplay, 1=Controls, 2=Graphics, 3=Audio, 4=Advanced
    public float ScrollOffset { get; set; } = 0f;  // For scrolling when content is too tall
    
    private static readonly string[] TabNames = { "GAMEPLAY", "CONTROLS", "GRAPHICS", "AUDIO", "ADVANCED" };
    
    // Available countdown packs (cached)
    private List<string> _availableCountdowns = new List<string> { "default" };
    
    // ── Description system for better UX ──────────────────────────────────────
    private static readonly Dictionary<string, string> Descriptions = new()
    {
        // GAMEPLAY
        ["Autoplay"] = "Automatically hits all notes. Great for watching charts or testing levels.",
        ["Downscroll"] = "Notes scroll from top to bottom instead of bottom to top.",
        ["Midlescroll"] = "Centers your strumline and hides opponent notes. Classic FNF style.",
        ["Scroll Speed"] = "How fast notes travel. Higher = faster. Adjust to your comfort.",
        ["Ghost Tapping"] = "Prevents misses when tapping keys with no notes. Easier gameplay.",
        ["Input Buffer"] = "Time window to buffer inputs before a note arrives. Helps with fast sections.",
        ["Safe Frames"] = "Grace period after hitting a note before another input counts. Prevents double hits.",
        ["Countdown Style"] = "Visual countdown style before the song starts (Ready, Set, Go!).",
        ["Font Style"] = "Choose the font used throughout the game. Add custom fonts to mods/fonts/.",
        ["Show Combo"] = "Display combo counter and accuracy rating during gameplay.",
        
        // CONTROLS
        ["Rebind Keys"] = "Customize keyboard controls for all 4 lanes.",
        
        // GRAPHICS
        ["VSync"] = "Synchronizes frame rate with monitor refresh rate. Prevents screen tearing.",
        ["Fullscreen"] = "Toggle fullscreen mode. Press Alt+Enter anytime to toggle quickly.",
        ["Bilinear Filter"] = "Smooths textures when scaling. Turn OFF for crispy pixel art.",
        ["Note Style"] = "Visual style for notes. Place custom styles in mods/notestyles/.",
        ["Note Splash"] = "Hit effect that plays when you hit a note perfectly.",
        ["Song Timer"] = "Progress bar showing song time. Legacy follows original FMM style.",
        ["Timer Color"] = "Color scheme for the timer bar.",
        ["  Preset"] = "Timer position: Legacy (original), Custom HUD (editor), or V-Slice styles.",
        ["    Text Mode"] = "What text to display on the timer bar.",
        ["    Force Top"] = "Always place timer at top, ignoring downscroll setting.",
        ["Camera Zoom"] = "Adjust camera zoom level. Locked if the chart uses camera events.",
        ["Hide Extra Strumlines"] = "Hide strumlines beyond the first two (player and opponent).",
        ["Hide Opponent Strumline"] = "Hide only the opponent's strumline. Good for focus.",
        ["Icon Mode"] = "FNF Base: icons bob with health. Fixed: icons stay still.",
        ["Color Grading"] = "Adjust colors, saturation, and hue for visual customization.",
        ["  Hue"] = "Shift all colors on the color wheel. 0.0 = original colors.",
        ["  Saturation"] = "Color intensity. 1.0 = normal, 0.0 = grayscale, 2.0 = super vibrant.",
        
        // AUDIO
        ["Music Volume"] = "Master volume for music and sound effects.",
        
        // ADVANCED
        ["Debug Info"] = "Show technical info like BPM, beat count, and event status.",
        ["FPS Counter"] = "Display frames per second in the corner.",
        ["Timeskip"] = "Hold T during gameplay to fast-forward through sections.",
        ["Timeskip Seconds"] = "How many seconds to skip forward when timeskipping.",
        ["Arrow Effects"] = "Visual effects on receptors (strumline arrows) synced to music.",
        ["  Effect Intensity"] = "How strong the arrow effects are. 100% = full effect.",
        ["  Effect Type"] = "Which effect to apply: Bounce, Wave, Rotate, Scale, or Random.",
        ["  Effect Chance"] = "Probability of effect triggering per beat. 100% = every beat.",
        ["Brightness"] = "Overall screen brightness. 1.0 = normal, higher = brighter.",
        
        // BACK
        ["Back"] = "Return to the previous menu.",
    };

    // ── Initialization ───────────────────────────────────────────────────────

    public SettingsManager()
    {
        Console.WriteLine("[SettingsManager] Initialized with tabs");
        ScanAvailableCountdowns();
    }

    public void Reset()
    {
        IsActive = false;
        Selection = 0;
        CurrentTab = 0;
        ScrollOffset = 0f;
        
        Console.WriteLine("[SettingsManager] Reset");
    }
    
    /// <summary>
    /// Scans mods/countdowns/ folder for available countdown packs.
    /// </summary>
    private void ScanAvailableCountdowns()
    {
        _availableCountdowns.Clear();
        _availableCountdowns.Add("default");  // Always available (built-in Assets)
        
        try
        {
            string countdownsDir = System.IO.Path.Combine(AppContext.BaseDirectory, "mods", "countdowns");
            if (System.IO.Directory.Exists(countdownsDir))
            {
                var dirs = System.IO.Directory.GetDirectories(countdownsDir);
                foreach (var dir in dirs)
                {
                    string name = System.IO.Path.GetFileName(dir);
                    if (!string.IsNullOrEmpty(name) && name != "default")
                    {
                        // Check if it has the required files
                        string readyPath = System.IO.Path.Combine(dir, "ready.png");
                        string setPath = System.IO.Path.Combine(dir, "set.png");
                        string goPath = System.IO.Path.Combine(dir, "go.png");
                        
                        if (System.IO.File.Exists(readyPath) && System.IO.File.Exists(setPath) && System.IO.File.Exists(goPath))
                        {
                            _availableCountdowns.Add(name);
                        }
                    }
                }
            }
            
            Console.WriteLine($"[SettingsManager] Found {_availableCountdowns.Count} countdown packs: {string.Join(", ", _availableCountdowns)}");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[SettingsManager] Error scanning countdowns: {ex.Message}");
        }
    }

    /// <summary>
    /// Returns the number of selectable settings items in the current tab.
    /// </summary>
    public int GetItemCount()
    {
        return CurrentTab switch
        {
            0 => 11,  // GAMEPLAY: 11 items (added Font Style)
            1 => 2,   // CONTROLS: 2 items (Rebind Keys, Back)
            2 => GetGraphicsItemCount(),  // GRAPHICS: 13-19 items (dynamic based on ShowTimer and Color Grading)
            3 => 2,   // AUDIO: 2 items (Volume, Back)
            4 => ArrowEffectManager.Enabled ? 10 : 7,  // ADVANCED: 7-10 items (dynamic based on Arrow Effects)
            _ => 1
        };
    }
    
    private int GetGraphicsItemCount()
    {
        int count = 13;  // Base items
        
        if (Game1.ShowTimer)
        {
            count++; // Timer Preset
            if (Game1.TimerPreset != "Legacy" && Game1.TimerPreset != "Custom HUD")
            {
                count += 2;  // Text Mode, Force Top
            }
        }
        
        if (Game1.ColorGradeEnabled) count += 2;  // Color Grading suboptions: Hue, Saturation
        return count;
    }

    // ── Tab Navigation ───────────────────────────────────────────────────────
    
    public void NextTab()
    {
        CurrentTab = (CurrentTab + 1) % TabNames.Length;
        Selection = 0;  // Reset selection when changing tabs
        ScrollOffset = 0f;  // Reset scroll
    }
    
    public void PrevTab()
    {
        CurrentTab = (CurrentTab - 1 + TabNames.Length) % TabNames.Length;
        Selection = 0;
        ScrollOffset = 0f;
    }

    // ── Action Handling ──────────────────────────────────────────────────────

    /// <summary>
    /// Handles the Enter/Space action for the currently selected setting.
    /// </summary>
    public SettingsActionResult HandleSelectAction(Game1 game)
    {
        var result = new SettingsActionResult();

        switch (CurrentTab)
        {
            case 0: // GAMEPLAY tab
                result = HandleGameplayAction(game);
                break;
            case 1: // CONTROLS tab
                result = HandleControlsAction(game);
                break;
            case 2: // GRAPHICS tab
                result = HandleGraphicsAction(game);
                break;
            case 3: // AUDIO tab
                result = HandleAudioAction(game);
                break;
            case 4: // ADVANCED tab
                result = HandleAdvancedAction(game);
                break;
        }

        return result;
    }

    private SettingsActionResult HandleGameplayAction(Game1 game)
    {
        var result = new SettingsActionResult { NeedsSave = true };
        
        switch (Selection)
        {
            case 0: // Autoplay
                if (!game._isMultiplayerMatch)
                    NoteRenderer.Autoplay = !NoteRenderer.Autoplay;
                break;
            case 1: // Downscroll
                NoteRenderer.Downscroll = !NoteRenderer.Downscroll;
                break;
            case 2: // Midlescroll
                NoteRenderer.Midlescroll = !NoteRenderer.Midlescroll;
                result.NeedsStrumlineReposition = true;
                break;
            case 3: // Scroll Speed (use left/right)
                break;
            case 4: // Ghost Tapping
                NoteRenderer.GhostTapping = !NoteRenderer.GhostTapping;
                break;
            case 5: // Input Buffer (use left/right)
                break;
            case 6: // Safe Frames (use left/right)
                break;
            case 7: // Countdown Style - Open menu
                result.OpenCountdownMenu = true;
                IsActive = false;
                result.NeedsSave = false;
                break;
            case 8: // Font Style
                result.OpenFontMenu = true;
                IsActive = false;
                result.NeedsSave = false;
                break;
            case 9: // Show Combo
                Game1.ShowCombo = !Game1.ShowCombo;
                break;
            case 10: // Back
                result.ExitMenu = true;
                IsActive = false;
                result.NeedsSave = false;
                break;
        }
        
        return result;
    }

    private SettingsActionResult HandleControlsAction(Game1 game)
    {
        var result = new SettingsActionResult { NeedsSave = false };
        
        switch (Selection)
        {
            case 0: // Rebind Keys
                result.OpenKeyBind = true;
                IsActive = false;
                break;
            case 1: // Back
                result.ExitMenu = true;
                IsActive = false;
                break;
        }
        
        return result;
    }

    private SettingsActionResult HandleGraphicsAction(Game1 game)
    {
        var result = new SettingsActionResult { NeedsSave = true };
        bool canOverrideZoom = game._events == null || !game._events.HasCameraEvents;
        
        // Build index mapping based on visible items
        int idx = 0;
        int vSync = idx++;
        int fullscreen = idx++;
        int bilinear = idx++;
        int noteStyle = idx++;
        int noteSplash = idx++;
        int songTimer = idx++;
        int timerColor = idx++;
        int timerPreset = Game1.ShowTimer ? idx++ : -1;
        int timerTextMode = (Game1.ShowTimer && Game1.TimerPreset != "Legacy" && Game1.TimerPreset != "Custom HUD") ? idx++ : -1;
        int timerForceTop = (Game1.ShowTimer && Game1.TimerPreset != "Legacy" && Game1.TimerPreset != "Custom HUD") ? idx++ : -1;
        int cameraZoom = idx++;
        int hideExtra = idx++;
        int hideOpponent = idx++;
        int iconMode = idx++;
        int colorGrading = idx++;
        int hue = Game1.ColorGradeEnabled ? idx++ : -1;
        int saturation = Game1.ColorGradeEnabled ? idx++ : -1;
        int back = idx++;
        
        if (Selection == vSync)
            result.ToggleVSync = true;
        else if (Selection == fullscreen)
            result.ToggleFullscreen = true;
        else if (Selection == bilinear)
            Game1.BilinearFiltering = !Game1.BilinearFiltering;
        else if (Selection == noteStyle)
        {
            result.OpenArrowsMenu = true;
            IsActive = false;
            result.NeedsSave = false;
        }
        else if (Selection == noteSplash)
        {
            result.OpenSplashMenu = true;
            IsActive = false;
            result.NeedsSave = false;
        }
        else if (Selection == songTimer)
            Game1.ShowTimer = !Game1.ShowTimer;
        else if (Selection == timerColor)
        {
            // Use left/right
        }
        else if (Selection == timerPreset)
        {
            // Use left/right
        }
        else if (Selection == timerTextMode)
        {
            // Use left/right
        }
        else if (Selection == timerForceTop && timerForceTop != -1)
            Game1.TimerForceTop = !Game1.TimerForceTop;
        else if (Selection == cameraZoom)
        {
            if (canOverrideZoom)
                result.NeedsCameraReset = true;
            result.NeedsSave = false;
        }
        else if (Selection == hideExtra)
        {
            Game1.HideExtraStrumlines = !Game1.HideExtraStrumlines;
            result.NeedsStrumlineAlphaUpdate = true;
        }
        else if (Selection == hideOpponent)
        {
            Game1.HideOpponentStrumline = !Game1.HideOpponentStrumline;
            result.NeedsOpponentAlphaUpdate = true;
        }
        else if (Selection == iconMode)
            Game1.IconMode = (Game1.IconMode + 1) % 2;
        else if (Selection == colorGrading)
            Game1.ColorGradeEnabled = !Game1.ColorGradeEnabled;
        else if (Selection == hue)
        {
            // Use left/right
        }
        else if (Selection == saturation)
        {
            // Use left/right
        }
        else if (Selection == back)
        {
            result.ExitMenu = true;
            IsActive = false;
            result.NeedsSave = false;
        }
        
        return result;
    }

    private SettingsActionResult HandleAudioAction(Game1 game)
    {
        var result = new SettingsActionResult { NeedsSave = true };
        
        switch (Selection)
        {
            case 0: // Volume (use left/right)
                break;
            case 1: // Back
                result.ExitMenu = true;
                IsActive = false;
                result.NeedsSave = false;
                break;
        }
        
        return result;
    }

    private SettingsActionResult HandleAdvancedAction(Game1 game)
    {
        var result = new SettingsActionResult { NeedsSave = true };
        
        // Build index mapping based on visible items
        int idx = 0;
        int debugInfo = idx++;
        int fpsCounter = idx++;
        int timeskip = idx++;
        int timeskipSeconds = idx++;
        int arrowEffects = idx++;
        int effectIntensity = ArrowEffectManager.Enabled ? idx++ : -1;
        int effectType = ArrowEffectManager.Enabled ? idx++ : -1;
        int effectChance = ArrowEffectManager.Enabled ? idx++ : -1;
        int brightness = idx++;
        int back = idx++;
        
        if (Selection == debugInfo)
            Game1.ShowDebugInfo = !Game1.ShowDebugInfo;
        else if (Selection == fpsCounter)
            Game1.ShowFpsCounter = !Game1.ShowFpsCounter;
        else if (Selection == timeskip)
            Game1.TimeskipEnabled = !Game1.TimeskipEnabled;
        else if (Selection == timeskipSeconds)
        {
            // Use left/right
        }
        else if (Selection == arrowEffects)
            ArrowEffectManager.Enabled = !ArrowEffectManager.Enabled;
        else if (Selection == effectIntensity && effectIntensity != -1)
        {
            // Use left/right
        }
        else if (Selection == effectType && effectType != -1)
        {
            // Use left/right
        }
        else if (Selection == effectChance && effectChance != -1)
        {
            // Use left/right
        }
        else if (Selection == brightness)
        {
            // Use left/right
        }
        else if (Selection == back)
        {
            result.ExitMenu = true;
            IsActive = false;
            result.NeedsSave = false;
        }
        
        return result;
    }

    /// <summary>
    /// Handles Left/Right adjustments for settings that use them.
    /// </summary>
    public SettingsActionResult HandleAdjustment(Game1 game, bool isLeft)
    {
        var result = new SettingsActionResult { NeedsSave = true };
        float delta = isLeft ? -1f : 1f;

        switch (CurrentTab)
        {
            case 0: // GAMEPLAY
                result = HandleGameplayAdjustment(game, isLeft, delta);
                break;
            case 2: // GRAPHICS
                result = HandleGraphicsAdjustment(game, isLeft, delta);
                break;
            case 3: // AUDIO
                result = HandleAudioAdjustment(game, isLeft, delta);
                break;
            case 4: // ADVANCED
                result = HandleAdvancedAdjustment(game, isLeft, delta);
                break;
        }

        return result;
    }

    private SettingsActionResult HandleGameplayAdjustment(Game1 game, bool isLeft, float delta)
    {
        var result = new SettingsActionResult { NeedsSave = true };
        
        switch (Selection)
        {
            case 3: // Scroll Speed
                NoteRenderer.ScrollMultiplier = MathF.Round(
                    Math.Clamp(NoteRenderer.ScrollMultiplier + delta * 0.25f, 0.25f, 5.0f), 2);
                break;
            case 5: // Input Buffer
                NoteRenderer.InputBuffer = MathF.Round(
                    Math.Clamp(NoteRenderer.InputBuffer + delta * 0.005f, 0f, 0.050f), 3);
                break;
            case 6: // Safe Frames
                NoteRenderer.SafeFramesTime = MathF.Round(
                    Math.Clamp(NoteRenderer.SafeFramesTime + delta * 0.017f, 0f, 0.500f), 3);
                break;
            case 7: // Countdown Style
                int currentIndex = _availableCountdowns.IndexOf(Game1.SelectedCountdown);
                if (currentIndex == -1) currentIndex = 0;
                int newIndex = (currentIndex + (isLeft ? -1 : 1) + _availableCountdowns.Count) % _availableCountdowns.Count;
                Game1.SelectedCountdown = _availableCountdowns[newIndex];
                break;
            default:
                result.NeedsSave = false;
                break;
        }
        
        return result;
    }

    private SettingsActionResult HandleGraphicsAdjustment(Game1 game, bool isLeft, float delta)
    {
        var result = new SettingsActionResult { NeedsSave = true };
        bool canOverrideZoom = game._events == null || !game._events.HasCameraEvents;
        
        // Build index mapping
        int idx = 0;
        idx++; // vSync
        idx++; // fullscreen
        idx++; // bilinear
        idx++; // noteStyle
        idx++; // noteSplash
        idx++; // songTimer
        int timerColor = idx++;
        int timerPreset = Game1.ShowTimer ? idx++ : -1;
        int timerTextMode = (Game1.ShowTimer && Game1.TimerPreset != "Legacy" && Game1.TimerPreset != "Custom HUD") ? idx++ : -1;
        int timerForceTop = (Game1.ShowTimer && Game1.TimerPreset != "Legacy" && Game1.TimerPreset != "Custom HUD") ? idx++ : -1;
        int cameraZoom = idx++;
        idx++; // hideExtra
        idx++; // hideOpponent
        idx++; // iconMode
        idx++; // colorGrading
        int hue = Game1.ColorGradeEnabled ? idx++ : -1;
        int saturation = Game1.ColorGradeEnabled ? idx++ : -1;
        
        if (Selection == timerColor)
        {
            const int MAX_PRESET = 6;
            Game1.TimerColorPreset = isLeft 
                ? (Game1.TimerColorPreset - 1 + MAX_PRESET + 1) % (MAX_PRESET + 1)
                : (Game1.TimerColorPreset + 1) % (MAX_PRESET + 1);
        }
        else if (Selection == timerPreset && timerPreset != -1)
        {
            string[] presets = { "Legacy", "Custom HUD", "Top", "Left", "Middle", "Right", "Strumline" };
            int currentIndex = Array.IndexOf(presets, Game1.TimerPreset);
            if (currentIndex == -1) currentIndex = 1; // Default to Custom HUD
            currentIndex = isLeft ? (currentIndex - 1 + presets.Length) % presets.Length : (currentIndex + 1) % presets.Length;
            Game1.TimerPreset = presets[currentIndex];
        }
        else if (Selection == timerTextMode && timerTextMode != -1)
        {
            string[] modes = { "Time Left", "Time Elapsed", "Song Name", "None" };
            int currentIndex = Array.IndexOf(modes, Game1.TimerTextMode);
            if (currentIndex == -1) currentIndex = 0;
            currentIndex = isLeft ? (currentIndex - 1 + modes.Length) % modes.Length : (currentIndex + 1) % modes.Length;
            Game1.TimerTextMode = modes[currentIndex];
        }
        else if (Selection == cameraZoom)
        {
            if (canOverrideZoom)
            {
                result.AdjustCameraZoom = isLeft ? -1 : 1;
                result.NeedsSave = false;
            }
        }
        else if (Selection == hue && hue != -1)
        {
            Game1.ColorGradeHue = MathF.Round(
                Math.Clamp(Game1.ColorGradeHue + delta * 0.01f, -1f, 1f), 2);
        }
        else if (Selection == saturation && saturation != -1)
        {
            Game1.ColorGradeSaturation = MathF.Round(
                Math.Clamp(Game1.ColorGradeSaturation + delta * 0.05f, 0f, 2f), 2);
        }
        else
        {
            result.NeedsSave = false;
        }
        
        return result;
    }

    private SettingsActionResult HandleAudioAdjustment(Game1 game, bool isLeft, float delta)
    {
        var result = new SettingsActionResult { NeedsSave = true };
        
        switch (Selection)
        {
            case 0: // Volume
                result.AdjustVolume = delta;
                break;
            default:
                result.NeedsSave = false;
                break;
        }
        
        return result;
    }

    private SettingsActionResult HandleAdvancedAdjustment(Game1 game, bool isLeft, float delta)
    {
        var result = new SettingsActionResult { NeedsSave = true };
        
        // Build index mapping
        int idx = 0;
        idx++; // debugInfo
        idx++; // fpsCounter
        int timeskip = idx++;
        int timeskipSeconds = idx++;
        idx++; // arrowEffects
        int effectIntensity = ArrowEffectManager.Enabled ? idx++ : -1;
        int effectType = ArrowEffectManager.Enabled ? idx++ : -1;
        int effectChance = ArrowEffectManager.Enabled ? idx++ : -1;
        int brightness = idx++;
        
        if (Selection == timeskip && Game1.TimeskipEnabled)
        {
            result.SeekSong = delta * Game1.TimeskipSeconds;
            result.NeedsSave = false;
        }
        else if (Selection == timeskipSeconds)
        {
            Game1.TimeskipSeconds = MathF.Round(
                Math.Clamp(Game1.TimeskipSeconds + delta * 5f, 5f, 60f), 0);
        }
        else if (Selection == effectIntensity && effectIntensity != -1)
        {
            ArrowEffectManager.EffectIntensity = MathF.Round(
                Math.Clamp(ArrowEffectManager.EffectIntensity + delta * 0.1f, 0f, 1f), 2);
        }
        else if (Selection == effectType && effectType != -1)
        {
            int newEffect = ArrowEffectManager.SelectedEffect + (isLeft ? -1 : 1);
            ArrowEffectManager.SelectedEffect = (newEffect + 6) % 6;
        }
        else if (Selection == effectChance && effectChance != -1)
        {
            ArrowEffectManager.EffectChance = MathF.Round(
                Math.Clamp(ArrowEffectManager.EffectChance + delta * 0.05f, 0f, 1f), 2);
        }
        else if (Selection == brightness)
        {
            Game1.ColorGradeBrightness = MathF.Round(
                Math.Clamp(Game1.ColorGradeBrightness + delta * 0.05f, -1f, 1f), 2);
        }
        else
        {
            result.NeedsSave = false;
        }
        
        return result;
    }

    // ── Drawing ──────────────────────────────────────────────────────────────

    /// <summary>
    /// Draws the settings menu UI with tabs.
    /// </summary>
    public void Draw(Game1 game, SpriteBatch spriteBatch, SpriteFont font, Texture2D pixel, 
                     int screenWidth, int screenHeight, FontRenderer? fontRenderer = null)
    {
        if (font == null) return;

        // Scale factor based on 720p reference (so UI looks consistent across resolutions)
        const float referenceHeight = 720f;
        float scaleFactor = screenHeight / referenceHeight;

        // ── Background ────────────────────────────────────────────────────────
        for (int y = 0; y < screenHeight; y += 4)
        {
            float factor = (float)y / screenHeight;
            Color bgColor = Color.Lerp(new Color(8, 8, 15), new Color(12, 12, 22), factor) * 0.95f;
            DrawRect(spriteBatch, pixel, 0, y, screenWidth, 4, bgColor);
        }

        // ── Header with tabs ──────────────────────────────────────────────────
        int headerH = (int)(80 * scaleFactor);
        int tabH = (int)(40 * scaleFactor);
        int tabY = headerH - tabH;
        
        // Header gradient
        for (int y = 0; y < headerH; y++)
        {
            float factor = (float)y / headerH;
            Color barColor = Color.Lerp(new Color(20, 12, 30), new Color(12, 12, 20), factor);
            DrawRect(spriteBatch, pixel, 0, y, screenWidth, 1, barColor);
        }
        
        // Title with shadow
        if (fontRenderer != null)
        {
            int titleSize = (int)(48 * scaleFactor);
            float titleY = 1 * scaleFactor;
            // Shadow
            fontRenderer.DrawString(spriteBatch, "SETTINGS",
                new Vector2(24 * scaleFactor, titleY + 2),
                Color.Black * 0.5f, titleSize);
            // Main text
            fontRenderer.DrawString(spriteBatch, "SETTINGS",
                new Vector2(22 * scaleFactor, titleY),
                new Color(0xFF, 0x44, 0x88), titleSize);
        }
        else
        {
            // Shadow
            spriteBatch.DrawString(font, "SETTINGS",
                new Vector2(24, 15),
                Color.Black * 0.5f, 0f, Vector2.Zero, 1.1f, SpriteEffects.None, 0f);
            // Main text
            spriteBatch.DrawString(font, "SETTINGS",
                new Vector2(22, 13),
                new Color(0xFF, 0x44, 0x88), 0f, Vector2.Zero, 1.1f, SpriteEffects.None, 0f);
        }

        // ── Draw Tabs ─────────────────────────────────────────────────────────
        int tabCount = TabNames.Length;
        float tabW = screenWidth / (float)tabCount;
        
        for (int i = 0; i < tabCount; i++)
        {
            bool isSelected = i == CurrentTab;
            float tx = i * tabW;
            
            // Tab background with gradient
            Color tabBg = isSelected 
                ? new Color(0xFF, 0x44, 0x88) * 0.35f 
                : new Color(15, 15, 25) * 0.7f;
            
            for (int ty = 0; ty < tabH; ty++)
            {
                float gradFactor = (float)ty / tabH;
                Color lineColor = isSelected
                    ? Color.Lerp(tabBg, tabBg * 0.8f, gradFactor)
                    : Color.Lerp(tabBg, tabBg * 1.2f, gradFactor);
                DrawRect(spriteBatch, pixel, (int)tx, tabY + ty, (int)tabW, 1, lineColor);
            }
            
            // Tab top border
            if (isSelected)
            {
                DrawRect(spriteBatch, pixel, (int)tx, tabY, (int)tabW, 3, new Color(0xFF, 0x44, 0x88));
            }
            
            // Tab separator (right edge)
            if (i < tabCount - 1)
            {
                DrawRect(spriteBatch, pixel, (int)(tx + tabW) - 1, tabY, 1, tabH, 
                    Color.Black * 0.3f);
            }
            
            // Tab text
            string tabName = TabNames[i];
            Color tabColor = isSelected ? Color.White : Color.White * 0.65f;
            
            if (fontRenderer != null)
            {
                int tabTextSize = (int)(24 * scaleFactor);
                Vector2 measure = fontRenderer.MeasureString(tabName, tabTextSize);
                
                // Text shadow
                if (isSelected)
                {
                    fontRenderer.DrawString(spriteBatch, tabName,
                        new Vector2(tx + tabW / 2f - measure.X / 2f + 1, tabY + tabH / 2f - measure.Y / 2f + 1),
                        Color.Black * 0.4f, tabTextSize);
                }
                
                // Main text
                fontRenderer.DrawString(spriteBatch, tabName,
                    new Vector2(tx + tabW / 2f - measure.X / 2f, tabY + tabH / 2f - measure.Y / 2f),
                    tabColor, tabTextSize);
            }
            else
            {
                float textScale = 0.65f;
                Vector2 measure = font.MeasureString(tabName) * textScale;
                
                // Text shadow
                if (isSelected)
                {
                    spriteBatch.DrawString(font, tabName,
                        new Vector2(tx + tabW / 2f - measure.X / 2f + 1, tabY + tabH / 2f - measure.Y / 2f + 1),
                        Color.Black * 0.4f, 0f, Vector2.Zero, textScale, SpriteEffects.None, 0f);
                }
                
                // Main text
                spriteBatch.DrawString(font, tabName,
                    new Vector2(tx + tabW / 2f - measure.X / 2f, tabY + tabH / 2f - measure.Y / 2f),
                    tabColor, 0f, Vector2.Zero, textScale, SpriteEffects.None, 0f);
            }
        }

        // Accent line below tabs
        DrawRect(spriteBatch, pixel, 0, headerH - 3, screenWidth, 3, new Color(0xFF, 0x44, 0x88) * 0.7f);

        // ── Content area ──────────────────────────────────────────────────────
        float panelY = headerH + 10;
        float panelH = screenHeight - panelY - 20;
        
        // Draw content based on current tab
        switch (CurrentTab)
        {
            case 0: DrawGameplayTab(spriteBatch, font, pixel, screenWidth, screenHeight, panelY, panelH, fontRenderer, scaleFactor); break;
            case 1: DrawControlsTab(spriteBatch, font, pixel, screenWidth, screenHeight, panelY, panelH, fontRenderer, scaleFactor); break;
            case 2: DrawGraphicsTab(game, spriteBatch, font, pixel, screenWidth, screenHeight, panelY, panelH, fontRenderer, scaleFactor); break;
            case 3: DrawAudioTab(spriteBatch, font, pixel, screenWidth, screenHeight, panelY, panelH, fontRenderer, scaleFactor); break;
            case 4: DrawAdvancedTab(spriteBatch, font, pixel, screenWidth, screenHeight, panelY, panelH, fontRenderer, scaleFactor); break;
        }

        // ── Description box (centered at bottom, above hint bar) ──────────────
        int hintBarH = 36;  // Declare hint bar height first
        string description = GetCurrentDescription();
        if (!string.IsNullOrEmpty(description))
        {
            int descBoxH = 80;
            int descBoxY = screenHeight - hintBarH - descBoxH - 10;  // 10px gap above hint bar
            int descBoxW = Math.Min(900, screenWidth - 100);
            int descBoxX = screenWidth / 2 - descBoxW / 2;
            
            // Background with gradient
            for (int y = 0; y < descBoxH; y++)
            {
                float factor = (float)y / descBoxH;
                Color descBg = Color.Lerp(new Color(0, 0, 0, 200), new Color(0, 0, 0, 180), factor);
                DrawRect(spriteBatch, pixel, descBoxX, descBoxY + y, descBoxW, 1, descBg);
            }
            
            // Border (accent color)
            DrawRect(spriteBatch, pixel, descBoxX, descBoxY, descBoxW, 2, new Color(0xFF, 0x44, 0x88) * 0.6f); // Top
            DrawRect(spriteBatch, pixel, descBoxX, descBoxY + descBoxH - 2, descBoxW, 2, new Color(0xFF, 0x44, 0x88) * 0.6f); // Bottom
            DrawRect(spriteBatch, pixel, descBoxX, descBoxY, 2, descBoxH, new Color(0xFF, 0x44, 0x88) * 0.4f); // Left
            DrawRect(spriteBatch, pixel, descBoxX + descBoxW - 2, descBoxY, 2, descBoxH, new Color(0xFF, 0x44, 0x88) * 0.4f); // Right
            
            // Text (word-wrapped)
            Color descColor = Color.White * 0.85f;
            int padding = 15;
            
            if (fontRenderer != null)
            {
                int descSize = (int)(20 * scaleFactor);
                // Manual word wrap for FontRenderer
                string[] words = description.Split(' ');
                float lineX = descBoxX + padding;
                float lineY = descBoxY + padding;
                float maxWidth = descBoxW - padding * 2;
                string currentLine = "";
                
                foreach (string word in words)
                {
                    string testLine = string.IsNullOrEmpty(currentLine) ? word : currentLine + " " + word;
                    Vector2 testSize = fontRenderer.MeasureString(testLine, descSize);
                    
                    if (testSize.X > maxWidth && !string.IsNullOrEmpty(currentLine))
                    {
                        // Draw current line
                        fontRenderer.DrawString(spriteBatch, currentLine, new Vector2(lineX, lineY),
                            descColor, descSize);
                        lineY += descSize + 4;
                        currentLine = word;
                    }
                    else
                    {
                        currentLine = testLine;
                    }
                }
                
                // Draw last line
                if (!string.IsNullOrEmpty(currentLine))
                {
                    fontRenderer.DrawString(spriteBatch, currentLine, new Vector2(lineX, lineY),
                        descColor, descSize);
                }
            }
            else
            {
                float descScale = 0.55f;
                // Simple word wrap
                string[] words = description.Split(' ');
                float lineX = descBoxX + padding;
                float lineY = descBoxY + padding;
                float maxWidth = descBoxW - padding * 2;
                string currentLine = "";
                
                foreach (string word in words)
                {
                    string testLine = string.IsNullOrEmpty(currentLine) ? word : currentLine + " " + word;
                    Vector2 testSize = font.MeasureString(testLine) * descScale;
                    
                    if (testSize.X > maxWidth && !string.IsNullOrEmpty(currentLine))
                    {
                        // Draw current line
                        spriteBatch.DrawString(font, currentLine, new Vector2(lineX, lineY),
                            descColor, 0f, Vector2.Zero, descScale, SpriteEffects.None, 0f);
                        lineY += 22;
                        currentLine = word;
                    }
                    else
                    {
                        currentLine = testLine;
                    }
                }
                
                // Draw last line
                if (!string.IsNullOrEmpty(currentLine))
                {
                    spriteBatch.DrawString(font, currentLine, new Vector2(lineX, lineY),
                        descColor, 0f, Vector2.Zero, descScale, SpriteEffects.None, 0f);
                }
            }
        }
        
        // ── Navigation hint ───────────────────────────────────────────────────
        string hint = "Q/E: Switch Tab  |  ↑↓: Navigate  |  ←→: Adjust  |  ENTER: Select  |  ESC: Back";
        
        // Dark bar for hint background
        // hintBarH already declared above
        for (int y = 0; y < hintBarH; y++)
        {
            float factor = (float)y / hintBarH;
            Color hintBg = Color.Lerp(new Color(10, 10, 18), new Color(8, 8, 12), factor) * 0.9f;
            DrawRect(spriteBatch, pixel, 0, screenHeight - hintBarH + y, screenWidth, 1, hintBg);
        }
        
        // Top border for hint bar
        DrawRect(spriteBatch, pixel, 0, screenHeight - hintBarH, screenWidth, 2, new Color(0xFF, 0x44, 0x88) * 0.4f);
        
        Color hintColor = Color.White * 0.6f;
        
        if (fontRenderer != null)
        {
            int hintSize = (int)(18 * scaleFactor);
            Vector2 hintMeasure = fontRenderer.MeasureString(hint, hintSize);
            fontRenderer.DrawString(spriteBatch, hint,
                new Vector2(screenWidth / 2f - hintMeasure.X / 2f, screenHeight - hintBarH + (hintBarH - hintMeasure.Y) / 2f),
                hintColor, hintSize);
        }
        else
        {
            float hintScale = 0.52f;
            Vector2 hintMeasure = font.MeasureString(hint) * hintScale;
            spriteBatch.DrawString(font, hint,
                new Vector2(screenWidth / 2f - hintMeasure.X / 2f, screenHeight - hintBarH + (hintBarH - hintMeasure.Y) / 2f),
                hintColor, 0f, Vector2.Zero, hintScale, SpriteEffects.None, 0f);
        }
    }

    // ── Tab Drawing Methods (to be implemented) ──────────────────────────────
    
    private void DrawGameplayTab(SpriteBatch sb, SpriteFont font, Texture2D pixel, int sw, int sh, float panelY, float panelH, FontRenderer? fr, float scaleFactor)
    {
        var entries = new List<(string label, string value)>
        {
            ("Autoplay", NoteRenderer.Autoplay ? "ON" : "OFF"),
            ("Downscroll", NoteRenderer.Downscroll ? "ON" : "OFF"),
            ("Midlescroll", NoteRenderer.Midlescroll ? "ON" : "OFF"),
            ("Scroll Speed", $"{NoteRenderer.ScrollMultiplier:F2}x"),
            ("Ghost Tapping", NoteRenderer.GhostTapping ? "ON" : "OFF"),
            ("Input Buffer", $"{(int)(NoteRenderer.InputBuffer * 1000)}ms"),
            ("Safe Frames", $"{(int)(NoteRenderer.SafeFramesTime * 1000)}ms"),
            ("Countdown Style", Game1.SelectedCountdown),
            ("Font Style", "→"),
            ("Show Combo", Game1.ShowCombo ? "ON" : "OFF"),
            ("Back", "")
        };
        
        DrawSimpleList(sb, font, pixel, sw, panelY, panelH, entries, fr, scaleFactor);
    }

    private void DrawControlsTab(SpriteBatch sb, SpriteFont font, Texture2D pixel, int sw, int sh, float panelY, float panelH, FontRenderer? fr, float scaleFactor)
    {
        var entries = new List<(string label, string value)>
        {
            ("Rebind Keys", ""),
            ("Back", "")
        };
        
        DrawSimpleList(sb, font, pixel, sw, panelY, panelH, entries, fr, scaleFactor);
    }

    private void DrawGraphicsTab(Game1 game, SpriteBatch sb, SpriteFont font, Texture2D pixel, int sw, int sh, float panelY, float panelH, FontRenderer? fr, float scaleFactor)
    {
        bool canOverrideZoom = game._events == null || !game._events.HasCameraEvents;
        float currentZoom = game._cameraManager?.GetEffectiveScale(game._events?.Camera) ?? 1f;
        
        var entries = new List<(string label, string value)>
        {
            ("VSync", game._graphics.SynchronizeWithVerticalRetrace ? "ON" : "OFF"),
            ("Fullscreen", game._isFullscreen ? "ON" : "OFF"),
            ("Bilinear Filter", Game1.BilinearFiltering ? "ON" : "OFF"),
            ("Note Style", Game1.NoteStyleName),
            ("Note Splash", Game1.NoteSplashStyleName),
            ("Song Timer", Game1.ShowTimer ? "ON" : "OFF"),
            ("Timer Color", Game1.TimerColorPreset switch { 1=>"Cyan", 2=>"Pink", 3=>"Yellow", 4=>"Green", 5=>"Player", 6=>"Opponent", _=>"White" }),
        };
        
        // Timer suboptions (only if ShowTimer is ON)
        if (Game1.ShowTimer)
        {
            entries.Add(("  Preset", Game1.TimerPreset));
            
            // Text Mode and Force Top only available for non-Legacy and non-Custom HUD presets
            if (Game1.TimerPreset != "Legacy" && Game1.TimerPreset != "Custom HUD")
            {
                entries.Add(("    Text Mode", Game1.TimerTextMode));
                entries.Add(("    Force Top", Game1.TimerForceTop ? "ON" : "OFF"));
            }
        }
        
        entries.AddRange(new[]
        {
            ("Camera Zoom", canOverrideZoom ? $"{currentZoom:F2}" : $"{currentZoom:F2} (locked)"),
            ("Hide Extra Strumlines", Game1.HideExtraStrumlines ? "ON" : "OFF"),
            ("Hide Opponent Strumline", Game1.HideOpponentStrumline ? "ON" : "OFF"),
            ("Icon Mode", Game1.IconMode == 0 ? "FNF Base" : "Fixed"),
            ("Color Grading", Game1.ColorGradeEnabled ? "ON" : "OFF"),
        });
        
        // Only show suboptions if Color Grading is enabled
        if (Game1.ColorGradeEnabled)
        {
            entries.Add(("  Hue", $"{Game1.ColorGradeHue:F2}"));
            entries.Add(("  Saturation", $"{Game1.ColorGradeSaturation:F2}"));
        }
        
        entries.Add(("Back", ""));
        
        DrawSimpleList(sb, font, pixel, sw, panelY, panelH, entries, fr, scaleFactor);
    }

    private void DrawAudioTab(SpriteBatch sb, SpriteFont font, Texture2D pixel, int sw, int sh, float panelY, float panelH, FontRenderer? fr, float scaleFactor)
    {
        var entries = new List<(string label, string value)>
        {
            ("Music Volume", $"{(int)(Game1.MusicVolume * 100)}%"),
            ("Back", "")
        };
        
        DrawSimpleList(sb, font, pixel, sw, panelY, panelH, entries, fr, scaleFactor);
    }

    private void DrawAdvancedTab(SpriteBatch sb, SpriteFont font, Texture2D pixel, int sw, int sh, float panelY, float panelH, FontRenderer? fr, float scaleFactor)
    {
        var entries = new List<(string label, string value)>
        {
            ("Debug Info", Game1.ShowDebugInfo ? "ON" : "OFF"),
            ("FPS Counter", Game1.ShowFpsCounter ? "ON" : "OFF"),
            ("Timeskip", Game1.TimeskipEnabled ? "ON" : "OFF"),
            ("Timeskip Seconds", $"{Game1.TimeskipSeconds:F0}s"),
            ("Arrow Effects", ArrowEffectManager.Enabled ? "ON" : "OFF"),
        };
        
        // Only show suboptions if Arrow Effects is enabled
        if (ArrowEffectManager.Enabled)
        {
            entries.Add(("  Effect Intensity", $"{(int)(ArrowEffectManager.EffectIntensity * 100)}%"));
            entries.Add(("  Effect Type", ArrowEffectManager.GetSelectedEffectName()));
            entries.Add(("  Effect Chance", $"{(int)(ArrowEffectManager.EffectChance * 100)}%"));
        }
        
        entries.Add(("Brightness", $"{Game1.ColorGradeBrightness:F2}"));
        entries.Add(("Back", ""));
        
        DrawSimpleList(sb, font, pixel, sw, panelY, panelH, entries, fr, scaleFactor);
    }

    private void DrawSimpleList(SpriteBatch sb, SpriteFont font, Texture2D pixel, int sw, float panelY, float panelH, 
                                 List<(string label, string value)> entries, FontRenderer? fr, float scaleFactor)
    {
        float itemH = 56f * scaleFactor;
        float startY = panelY + 40 * scaleFactor;
        float panelW = Math.Min(900f * scaleFactor, sw * 0.7f);
        float panelX = (sw - panelW) / 2f;
        
        // Calculate total height needed
        float totalHeight = entries.Count * itemH;
        // Reserve space for description box at bottom (80px box + 10px gap)
        float descBoxReservedSpace = 90f;
        float availableHeight = panelH - 80 - descBoxReservedSpace;
        
        // Update scroll to keep selected item visible (centered when possible)
        float selectedY = Selection * itemH;
        float viewportTop = ScrollOffset;
        float viewportBottom = ScrollOffset + availableHeight;
        
        // Try to center the selected item in the viewport
        float targetScroll = selectedY - (availableHeight / 2f) + (itemH / 2f);
        targetScroll = Math.Clamp(targetScroll, 0f, Math.Max(0f, totalHeight - availableHeight));
        
        // Smooth scroll towards target
        float scrollDiff = targetScroll - ScrollOffset;
        if (Math.Abs(scrollDiff) > 0.5f)
        {
            ScrollOffset += scrollDiff * 0.3f; // Smooth interpolation
        }
        else
        {
            ScrollOffset = targetScroll;
        }
        
        // Clamp scroll
        ScrollOffset = Math.Clamp(ScrollOffset, 0f, Math.Max(0f, totalHeight - availableHeight));
        
        // If content fits, center it
        if (totalHeight < availableHeight)
        {
            startY = panelY + (availableHeight - totalHeight) / 2f + 20;
            ScrollOffset = 0f;
        }
        
        // Begin scissor test for clipping
        var oldScissor = sb.GraphicsDevice.ScissorRectangle;
        var oldRasterizer = sb.GraphicsDevice.RasterizerState;
        var scissorRect = new Rectangle((int)panelX - 20, (int)panelY, (int)panelW + 40, (int)(panelH - 40));
        
        sb.End();
        sb.Begin(SpriteSortMode.Deferred, BlendState.AlphaBlend, SamplerState.PointClamp, null, 
                 new RasterizerState { ScissorTestEnable = true });
        sb.GraphicsDevice.ScissorRectangle = scissorRect;
        
        for (int i = 0; i < entries.Count; i++)
        {
            bool sel = i == Selection;
            float y = startY + i * itemH - ScrollOffset;
            
            // Skip if outside viewport
            if (y + itemH < panelY || y > panelY + panelH)
                continue;
            
            // Selection highlight with rounded effect
            if (sel)
            {
                // Main highlight bar
                DrawRect(sb, pixel, (int)panelX - 12, (int)y - 6, (int)panelW + 24, (int)itemH - 8, 
                    new Color(0xFF, 0x44, 0x88) * 0.25f);
                
                // Left accent bar
                DrawRect(sb, pixel, (int)panelX - 12, (int)y - 6, 5, (int)itemH - 8, 
                    new Color(0xFF, 0x44, 0x88));
                
                // Top and bottom borders
                DrawRect(sb, pixel, (int)panelX - 12, (int)y - 6, (int)panelW + 24, 2, 
                    new Color(0xFF, 0x44, 0x88) * 0.5f);
                DrawRect(sb, pixel, (int)panelX - 12, (int)y + (int)itemH - 14, (int)panelW + 24, 2, 
                    new Color(0xFF, 0x44, 0x88) * 0.5f);
            }
            
            Color textColor = sel ? Color.White : Color.White * 0.75f;
            Color valueColor = sel ? new Color(0xFF, 0xAA, 0xDD) : new Color(0xAA, 0xAA, 0xBB);
            
            // Label
            if (fr != null)
            {
                int textSize = (int)(28 * scaleFactor);
                fr.DrawString(sb, entries[i].label, new Vector2(panelX + 10, y + 4), textColor, textSize);
                
                // Value (right-aligned)
                if (!string.IsNullOrEmpty(entries[i].value))
                {
                    Vector2 valueMeasure = fr.MeasureString(entries[i].value, textSize);
                    fr.DrawString(sb, entries[i].value, 
                        new Vector2(panelX + panelW - valueMeasure.X - 10, y + 4), 
                        valueColor, textSize);
                }
            }
            else
            {
                float scale = 0.8f;
                sb.DrawString(font, entries[i].label, new Vector2(panelX + 10, y + 4), textColor, 
                    0f, Vector2.Zero, scale, SpriteEffects.None, 0f);
                
                if (!string.IsNullOrEmpty(entries[i].value))
                {
                    Vector2 valueMeasure = font.MeasureString(entries[i].value) * scale;
                    sb.DrawString(font, entries[i].value, 
                        new Vector2(panelX + panelW - valueMeasure.X - 10, y + 4),
                        valueColor,
                        0f, Vector2.Zero, scale, SpriteEffects.None, 0f);
                }
            }
        }
        
        // Restore scissor test
        sb.End();
        sb.Begin();
        sb.GraphicsDevice.ScissorRectangle = oldScissor;
        
        // Draw scroll indicator if content is scrollable
        if (totalHeight > availableHeight)
        {
            float scrollBarH = Math.Max(40f, availableHeight * (availableHeight / totalHeight));
            float scrollBarY = panelY + 20 + (availableHeight - scrollBarH - 40) * (ScrollOffset / (totalHeight - availableHeight));
            
            // Scroll track
            DrawRect(sb, pixel, sw - 20, (int)panelY + 20, 8, (int)(availableHeight - 40), Color.Black * 0.3f);
            // Scroll thumb
            DrawRect(sb, pixel, sw - 20, (int)scrollBarY, 8, (int)scrollBarH, new Color(0xFF, 0x44, 0x88) * 0.7f);
        }
    }

    private void DrawRect(SpriteBatch sb, Texture2D pixel, int x, int y, int w, int h, Color color)
    {
        sb.Draw(pixel, new Rectangle(x, y, w, h), color);
    }
    
    private string GetCurrentDescription()
    {
        // Get entries from the current tab (without drawing)
        var entries = new List<(string label, string value)>();
        
        switch (CurrentTab)
        {
            case 0: // GAMEPLAY
                entries.Add(("Autoplay", ""));
                entries.Add(("Downscroll", ""));
                entries.Add(("Midlescroll", ""));
                entries.Add(("Scroll Speed", ""));
                entries.Add(("Ghost Tapping", ""));
                entries.Add(("Input Buffer", ""));
                entries.Add(("Safe Frames", ""));
                entries.Add(("Countdown Style", ""));
                entries.Add(("Font Style", ""));
                entries.Add(("Show Combo", ""));
                entries.Add(("Back", ""));
                break;
                
            case 1: // CONTROLS
                entries.Add(("Rebind Keys", ""));
                entries.Add(("Back", ""));
                break;
                
            case 2: // GRAPHICS
                entries.Add(("VSync", ""));
                entries.Add(("Fullscreen", ""));
                entries.Add(("Bilinear Filter", ""));
                entries.Add(("Note Style", ""));
                entries.Add(("Note Splash", ""));
                entries.Add(("Song Timer", ""));
                entries.Add(("Timer Color", ""));
                if (Game1.ShowTimer)
                {
                    entries.Add(("  Preset", ""));
                    if (Game1.TimerPreset != "Legacy" && Game1.TimerPreset != "Custom HUD")
                    {
                        entries.Add(("    Text Mode", ""));
                        entries.Add(("    Force Top", ""));
                    }
                }
                entries.Add(("Camera Zoom", ""));
                entries.Add(("Hide Extra Strumlines", ""));
                entries.Add(("Hide Opponent Strumline", ""));
                entries.Add(("Icon Mode", ""));
                entries.Add(("Color Grading", ""));
                if (Game1.ColorGradeEnabled)
                {
                    entries.Add(("  Hue", ""));
                    entries.Add(("  Saturation", ""));
                }
                entries.Add(("Back", ""));
                break;
                
            case 3: // AUDIO
                entries.Add(("Music Volume", ""));
                entries.Add(("Back", ""));
                break;
                
            case 4: // ADVANCED
                entries.Add(("Debug Info", ""));
                entries.Add(("FPS Counter", ""));
                entries.Add(("Timeskip", ""));
                entries.Add(("Timeskip Seconds", ""));
                entries.Add(("Arrow Effects", ""));
                if (ArrowEffectManager.Enabled)
                {
                    entries.Add(("  Effect Intensity", ""));
                    entries.Add(("  Effect Type", ""));
                    entries.Add(("  Effect Chance", ""));
                }
                entries.Add(("Brightness", ""));
                entries.Add(("Back", ""));
                break;
        }
        
        if (Selection < 0 || Selection >= entries.Count) return "";
        
        string key = entries[Selection].label;
        return Descriptions.TryGetValue(key, out string? desc) ? desc : "";
    }
}

// ── Action Result ────────────────────────────────────────────────────────────

public class SettingsActionResult
{
    public bool NeedsSave = false;
    public bool NeedsStrumlineReposition = false;
    public bool NeedsCameraReset = false;
    public bool NeedsStrumlineAlphaUpdate = false;
    public bool NeedsOpponentAlphaUpdate = false;
    public bool OpenKeyBind = false;
    public bool OpenArrowsMenu = false;
    public bool OpenSplashMenu = false;
    public bool OpenCountdownMenu = false;
    public bool OpenFontMenu = false;
    public bool ExitMenu = false;
    public bool ToggleVSync = false;
    public bool ToggleFullscreen = false;
    public int AdjustCameraZoom = 0;
    public float AdjustVolume = 0f;
    public float SeekSong = 0f;
}
