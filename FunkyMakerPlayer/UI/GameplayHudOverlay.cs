using System;
using System.Collections.Generic;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using FunkyMakerPlayer.Managers;
using FunkyMakerPlayer.Multiplayer;

namespace FunkyMakerPlayer.UI;

/// <summary>
/// Aggregated gameplay rating and accuracy statistics computed from a <see cref="NoteRenderer"/>.
/// </summary>
public readonly record struct GameplayStats(
    int Score,
    int Combo,
    int Misses,
    int Sicks,
    int Goods,
    int Bads,
    int Shits,
    float Accuracy);

/// <summary>
/// Renders the in-game HUD (health bars, singleplayer/multiplayer stats, timer bar,
/// song info, debug telemetry, FPS counter, controls status, and screen flashes).
/// </summary>
public static class GameplayHudOverlay
{
    /// <summary>
    /// Extracts player statistics and computes weighted FNF accuracy (defaults to 100% when no notes processed).
    /// </summary>
    public static GameplayStats CalculateStats(NoteRenderer? renderer)
    {
        if (renderer == null)
            return new GameplayStats(0, 0, 0, 0, 0, 0, 0, 100f);

        int sicks = renderer.Sicks;
        int goods = renderer.Goods;
        int bads = renderer.Bads;
        int shits = renderer.Shits;
        int misses = renderer.Misses;

        int totalNotes = sicks + goods + bads + shits + misses;
        float accuracy = 100f;
        if (totalNotes > 0)
        {
            float weightedHits = (sicks * 1.0f) + (goods * 0.75f) + (bads * 0.5f) + (shits * 0.25f);
            accuracy = (weightedHits / totalNotes) * 100f;
        }

        return new GameplayStats(renderer.Score, renderer.Combo, misses, sicks, goods, bads, shits, accuracy);
    }

    /// <summary>
    /// Renders all visible gameplay HUD elements onto the active SpriteBatch.
    /// </summary>
    public static void DrawHUD(
        SpriteBatch spriteBatch,
        SpriteFont? font,
        Texture2D? pixel,
        int sw,
        int sh,
        int rtWidth,
        int rtHeight,
        FnmmLevel? level,
        EventProcessor? events,
        CameraManager? cameraManager,
        HealthBar? healthBar,
        TimerBar? timerBar,
        KeyBindingOverlay keyBindOverlay,
        NoteStyle? noteStyle,
        List<NoteRenderer> noteRenderers,
        bool isMultiplayerMatch,
        string? opponentName,
        int opponentScore,
        float opponentHealth,
        float songTime,
        float songEndTime,
        float songBeats,
        bool started,
        bool paused,
        double lastDt,
        bool vsyncEnabled)
    {
        // GUI event can hide the entire engine HUD (health bar, score, timer, etc.)
        if (events?.HudVisible == false) return;

        var hudSettings = NoteRenderer.Downscroll
            ? HudSettings.Instance.GetDownscrollAdjusted(sh)
            : HudSettings.Instance;

        // Master HUD toggle: if disabled by the user, hide the entire gameplay HUD at once
        if (!hudSettings.MasterHudVisible)
        {
            if (font != null && pixel != null)
                keyBindOverlay.Draw(spriteBatch, font, pixel, sw, sh, noteStyle);
            return;
        }

        bool levelHasOwnBar = level != null &&
            level.Scene.Objects.Exists(o => o.Spr == "barra" && o.Fixed);

        if (!levelHasOwnBar && healthBar != null)
            healthBar.Draw(sh, NoteRenderer.Downscroll);

        if (font == null || noteRenderers.Count == 0 || level == null) return;

        var playerRenderer = noteRenderers[0];
        if (playerRenderer == null) return;

        var playerStats = CalculateStats(playerRenderer);

        if (!levelHasOwnBar)
        {
            if (isMultiplayerMatch && pixel != null && noteRenderers.Count >= 2)
            {
                DrawMultiplayerStats(
                    spriteBatch, font, pixel, sw, sh,
                    healthBar, playerStats, opponentName, opponentScore, opponentHealth);
            }
            else
            {
                DrawSingleplayerStats(spriteBatch, font, hudSettings, playerStats);
            }
        }

        if (timerBar != null)
        {
            timerBar.ShowTimer = Game1.ShowTimer;
            timerBar.Preset = Game1.TimerPreset;
            timerBar.TextMode = Game1.TimerTextMode;
            timerBar.ForceTop = Game1.TimerForceTop;
            timerBar.ColorPreset = Game1.TimerColorPreset;
            timerBar.Position = new Point(hudSettings.TimerBarPosition.X, hudSettings.TimerBarPosition.Y);
            timerBar.Size = new Point(hudSettings.TimerBarSize.X, hudSettings.TimerBarSize.Y);

            timerBar.Draw(sw, sh, songTime, songEndTime, NoteRenderer.Downscroll,
                level.LevelName, healthBar?.PlayerBarColor, healthBar?.OpponentBarColor, started);
        }

        Vector2 songInfoPos = new Vector2(hudSettings.SongInfoPosition.X, hudSettings.SongInfoPosition.Y);
        if (hudSettings.SongInfoVisible)
            DrawShadowedText(spriteBatch, font, ErrorOverlay.Safe(level.LevelName), songInfoPos, Color.White, 1.0f);

        if (Game1.ShowDebugInfo)
            DrawDebugInfo(spriteBatch, font, rtWidth, rtHeight, level, events, cameraManager, songBeats);

        if (Game1.ShowFpsCounter)
            DrawFpsCounter(spriteBatch, font, pixel, sw, songInfoPos, hudSettings.SongInfoVisible, lastDt, vsyncEnabled);

        if (hudSettings.StatusInfoVisible)
        {
            string autoStr = NoteRenderer.Autoplay ? "[BOTPLAY]" : "[MANUAL]";
            string scrollStr = NoteRenderer.Downscroll ? "[DOWN]" : "[UP]";
            string pauseStr = paused ? "[PAUSED]" : "";
            string speedStr = ErrorOverlay.Safe($"Speed:x{NoteRenderer.ScrollMultiplier:F2}");
            Vector2 statusPos = new Vector2(hudSettings.StatusInfoPosition.X, hudSettings.StatusInfoPosition.Y);
            spriteBatch.DrawString(font,
                ErrorOverlay.Safe($"{autoStr} {scrollStr} {speedStr} {pauseStr}"),
                statusPos,
                NoteRenderer.Autoplay ? Color.Yellow * 0.8f : Color.White * 0.4f);
        }

        if (pixel != null)
            keyBindOverlay.Draw(spriteBatch, font, pixel, sw, sh, noteStyle);
    }

    /// <summary>
    /// Draws active camera/screen flash rectangles over the scene.
    /// </summary>
    public static void DrawScreenFlashes(SpriteBatch spriteBatch, Texture2D? pixel, EventProcessor? events, int sw, int sh)
    {
        if (events == null || pixel == null) return;
        foreach (var flash in events.ActiveFlashes)
        {
            Color c = flash.Color * flash.Alpha;
            spriteBatch.Draw(pixel, new Rectangle(0, 0, sw, sh), c);
        }
    }

    private static void DrawSingleplayerStats(
        SpriteBatch spriteBatch,
        SpriteFont font,
        HudSettings hudSettings,
        GameplayStats stats)
    {
        const float statsScale = 0.85f;

        if (hudSettings.ScoreVisible)
        {
            DrawShadowedText(
                spriteBatch, font,
                ErrorOverlay.Safe($"Score: {stats.Score}"),
                new Vector2(hudSettings.ScorePosition.X, hudSettings.ScorePosition.Y),
                Color.White, statsScale);
        }

        if (hudSettings.MissesVisible)
        {
            Color missesColor = stats.Misses > 0 ? new Color(255, 100, 100) : Color.White;
            DrawShadowedText(
                spriteBatch, font,
                ErrorOverlay.Safe($"Misses: {stats.Misses}"),
                new Vector2(hudSettings.MissesPosition.X, hudSettings.MissesPosition.Y),
                missesColor, statsScale);
        }

        if (hudSettings.AccuracyVisible)
        {
            Color accColor = stats.Accuracy >= 95f ? new Color(100, 255, 100) :
                             stats.Accuracy >= 80f ? Color.Yellow :
                             stats.Accuracy >= 60f ? new Color(255, 200, 100) :
                                                     new Color(255, 100, 100);
            DrawShadowedText(
                spriteBatch, font,
                ErrorOverlay.Safe($"Accuracy: {stats.Accuracy:F1}%"),
                new Vector2(hudSettings.AccuracyPosition.X, hudSettings.AccuracyPosition.Y),
                accColor, statsScale);
        }
    }

    private static void DrawMultiplayerStats(
        SpriteBatch spriteBatch,
        SpriteFont font,
        Texture2D pixel,
        int sw,
        int sh,
        HealthBar? healthBar,
        GameplayStats playerStats,
        string? opponentName,
        int opponentScore,
        float opponentHealth)
    {
        var mp = Multiplayer.MultiplayerSettings.Instance;
        float hudAlpha = Math.Clamp(mp.HudOpacity / 100f, 0.1f, 1f);
        string playerName = string.IsNullOrWhiteSpace(mp.Username) ? "Player" : mp.Username.Trim();
        string oppName = string.IsNullOrWhiteSpace(opponentName) ? "Opponent" : opponentName;
        Color playerNameColor = Color.Lerp(mp.GetPlayerColor(), Color.White, 0.25f);
        Color oppColor = new Color(255, 110, 110);

        const float statsScale = 0.75f;
        const int barWidth = 300;
        const int barHeight = 20;
        const int iconSize = 80;
        int barY = sh - 100;

        // Left side: Opponent
        int opponentBarX = sw / 4 - barWidth / 2;
        DrawSplitHealthBar(
            spriteBatch, pixel, opponentBarX, barY, barWidth, barHeight,
            opponentHealth, healthBar?.OpponentBarColor ?? new Color(0xFF, 0x00, 0x00), hudAlpha);
        DrawSplitIcon(
            spriteBatch, healthBar?.OpponentIcon,
            opponentBarX + barWidth / 2 - iconSize / 2, barY - iconSize - 10, iconSize, hudAlpha);

        string opponentStatsText = mp.ShowOpponentScore
            ? ErrorOverlay.Safe($"{oppName}\nScore: {opponentScore}\nHealth: {(opponentHealth * 100):F0}%")
            : ErrorOverlay.Safe($"{oppName}\nHealth: {(opponentHealth * 100):F0}%");
        Vector2 opponentStatsSize = font.MeasureString(opponentStatsText) * statsScale;
        DrawShadowedText(
            spriteBatch, font, opponentStatsText,
            new Vector2(opponentBarX + barWidth / 2 - opponentStatsSize.X / 2, barY + barHeight + 8),
            oppColor * hudAlpha, statsScale, 2f, hudAlpha);

        // Right side: Local Player
        int playerBarX = (sw * 3) / 4 - barWidth / 2;
        float playerHealth = healthBar?.Health ?? 0.5f;
        DrawSplitHealthBar(
            spriteBatch, pixel, playerBarX, barY, barWidth, barHeight,
            playerHealth, healthBar?.PlayerBarColor ?? new Color(0x66, 0xFF, 0x66), hudAlpha);
        DrawSplitIcon(
            spriteBatch, healthBar?.PlayerIcon,
            playerBarX + barWidth / 2 - iconSize / 2, barY - iconSize - 10, iconSize, hudAlpha);

        string playerStatsText = ErrorOverlay.Safe(
            $"{playerName}\nScore: {playerStats.Score}\nAccuracy: {playerStats.Accuracy:F0}%\nCombo: x{playerStats.Combo}");
        Vector2 playerStatsSize = font.MeasureString(playerStatsText) * statsScale;
        DrawShadowedText(
            spriteBatch, font, playerStatsText,
            new Vector2(playerBarX + barWidth / 2 - playerStatsSize.X / 2, barY + barHeight + 8),
            playerNameColor * hudAlpha, statsScale, 2f, hudAlpha);

        // Center comparison
        const float centerScale = 0.9f;
        int centerY = sh - 180;

        string accuracyText = $"Accuracy: {playerStats.Accuracy:F1}%";
        Vector2 accuracySize = font.MeasureString(accuracyText) * centerScale;
        DrawShadowedText(
            spriteBatch, font, accuracyText,
            new Vector2(sw / 2 - accuracySize.X / 2, centerY),
            Color.White * hudAlpha, centerScale, 2f, hudAlpha);

        if (mp.ShowOpponentScore)
        {
            int scoreDiff = playerStats.Score - opponentScore;
            string scoreText = scoreDiff >= 0 ? $"+{scoreDiff}" : $"{scoreDiff}";
            Color scoreColor = scoreDiff >= 0 ? new Color(100, 255, 100) : new Color(255, 100, 100);
            Vector2 scoreSize = font.MeasureString(scoreText) * centerScale;
            DrawShadowedText(
                spriteBatch, font, scoreText,
                new Vector2(sw / 2 - scoreSize.X / 2, centerY + 30),
                scoreColor * hudAlpha, centerScale, 2f, hudAlpha);
        }

        if (mp.ShowConnectionQuality)
        {
            int ping = NetworkManager.Instance.Ping;
            var quality = NetworkManager.Instance.Quality;
            Color pingColor = quality switch
            {
                ConnectionQuality.Excellent => new Color(100, 255, 100),
                ConnectionQuality.Good => new Color(150, 255, 150),
                ConnectionQuality.Fair => Color.Yellow,
                ConnectionQuality.Poor => new Color(255, 150, 0),
                ConnectionQuality.Critical => new Color(255, 50, 50),
                _ => Color.Gray
            };

            string pingText = mp.InterfaceMode == Multiplayer.MultiplayerSettings.UIMode.Full
                ? (ping > 0 ? $"Ping: {ping}ms ({quality})" : $"Ping: ... ({quality})")
                : (ping > 0 ? $"{ping}ms" : "...");
            const float pingScale = 0.7f;
            Vector2 pingSize = font.MeasureString(pingText) * pingScale;
            const int margin = 14;
            int boxW = (int)pingSize.X + 16;
            int boxH = (int)pingSize.Y + 8;
            int boxX = mp.ConnectionIndicatorPosition switch
            {
                Multiplayer.MultiplayerSettings.IndicatorPosition.TopLeft or
                Multiplayer.MultiplayerSettings.IndicatorPosition.BottomLeft => margin,
                _ => sw - boxW - margin
            };
            int boxY = mp.ConnectionIndicatorPosition switch
            {
                Multiplayer.MultiplayerSettings.IndicatorPosition.BottomLeft or
                Multiplayer.MultiplayerSettings.IndicatorPosition.BottomRight => sh - boxH - margin,
                _ => margin + 28
            };

            spriteBatch.Draw(pixel, new Rectangle(boxX, boxY, boxW, boxH), Color.Black * (0.65f * hudAlpha));
            DrawShadowedText(spriteBatch, font, pingText,
                new Vector2(boxX + 8, boxY + 4), pingColor * hudAlpha, pingScale, 1f, hudAlpha);
        }
    }

    private static void DrawSplitHealthBar(
        SpriteBatch spriteBatch,
        Texture2D pixel,
        int barX,
        int barY,
        int barWidth,
        int barHeight,
        float healthRatio,
        Color baseColor,
        float alpha = 1f)
    {
        spriteBatch.Draw(pixel, new Rectangle(barX - 3, barY - 3, barWidth + 6, barHeight + 6), Color.Black * (0.6f * alpha));
        spriteBatch.Draw(pixel, new Rectangle(barX - 2, barY - 2, barWidth + 4, barHeight + 4), Color.Black * alpha);
        spriteBatch.Draw(pixel, new Rectangle(barX, barY, barWidth, barHeight), new Color(30, 30, 30) * alpha);

        int fillW = (int)(Math.Clamp(healthRatio, 0f, 1f) * barWidth);
        for (int i = 0; i < fillW; i++)
        {
            float t = (float)i / barWidth;
            Color c = Color.Lerp(baseColor * 0.7f, baseColor * 1.1f, t) * alpha;
            spriteBatch.Draw(pixel, new Rectangle(barX + i, barY, 1, barHeight), c);
        }
    }

    private static void DrawSplitIcon(SpriteBatch spriteBatch, SpriteAnimator? icon, int boxX, int boxY, int iconSize, float alpha = 1f)
    {
        if (icon == null) return;
        var (tex, src) = icon.GetCurrentFrameTexture();
        if (tex == null || src.Width <= 0 || src.Height <= 0) return;

        float scale = (float)iconSize / Math.Max(src.Width, src.Height);
        int drawW = (int)(src.Width * scale);
        int drawH = (int)(src.Height * scale);
        int drawX = boxX + (iconSize - drawW) / 2;
        int drawY = boxY + (iconSize - drawH) / 2;
        spriteBatch.Draw(tex, new Rectangle(drawX, drawY, drawW, drawH), src, Color.White * alpha);
    }

    private static void DrawDebugInfo(
        SpriteBatch spriteBatch,
        SpriteFont font,
        int rtWidth,
        int rtHeight,
        FnmmLevel level,
        EventProcessor? events,
        CameraManager? cameraManager,
        float songBeats)
    {
        spriteBatch.DrawString(font,
            ErrorOverlay.Safe($"BPM: {level.Data.EffectiveBpm}  Beat: {songBeats:F1}"),
            new Vector2(20, 48), Color.White * 0.5f);

        if (events == null) return;

        float worldW = level.Scene.Landscape ? 2160f : 1920f;
        float worldH = level.Scene.Landscape ? 1920f : 2160f;
        float dbgScale = cameraManager?.GetEffectiveScale(events.Camera) ?? 1f;
        float dbgPx = Math.Min((float)rtWidth / (worldW * dbgScale), (float)rtHeight / (worldH * dbgScale));

        spriteBatch.DrawString(font,
            ErrorOverlay.Safe($"Cam: ({events.Camera.X:F0},{events.Camera.Y:F0}) zoom={events.Camera.Scale:F3} base={events.Camera.BaseScale:F3} px={dbgPx:F3}" +
                (cameraManager?.HasManualOverride == true ? $" [manual:{cameraManager.ScaleOverride:F2}]" : "") +
                (events.HasCameraEvents ? " [EVENT]" : $" [AUTO->({events.AutoCamTargetX:F0},{events.AutoCamTargetY:F0})]")),
            new Vector2(20, 58), Color.Cyan * 0.7f);

        var unknown = events.UnknownEventTypes;
        if (unknown.Count > 0)
        {
            string unknownStr = "UNKNOWN EVENTS: " + string.Join(", ", unknown);
            spriteBatch.DrawString(font, ErrorOverlay.Safe(unknownStr),
                new Vector2(20, 76), new Color(255, 80, 80) * 0.9f);
        }
    }

    private static void DrawFpsCounter(
        SpriteBatch spriteBatch,
        SpriteFont font,
        Texture2D? pixel,
        int sw,
        Vector2 songInfoPos,
        bool songInfoVisible,
        double lastDt,
        bool vsyncEnabled)
    {
        float fps = (float)(1.0 / Math.Max(0.0001, lastDt));

#if ANDROID
        string fpsText = ErrorOverlay.Safe($"FPS: {fps:F0}");
        Vector2 textSize = font.MeasureString(fpsText);
        Vector2 fpsPos = new Vector2(sw - textSize.X - 20, 20);

        if (pixel != null)
        {
            Rectangle bgBox = new Rectangle(
                (int)(fpsPos.X - 8),
                (int)(fpsPos.Y - 4),
                (int)(textSize.X + 16),
                (int)(textSize.Y + 8));
            spriteBatch.Draw(pixel, bgBox, Color.Black * 0.7f);
        }

        Color fpsColor = fps >= 55 ? Color.LimeGreen : (fps >= 30 ? Color.Yellow : Color.Red);
        spriteBatch.DrawString(font, fpsText, fpsPos + new Vector2(2, 2), Color.Black * 0.8f);
        spriteBatch.DrawString(font, fpsText, fpsPos, fpsColor);
#else
        Vector2 fpsPos = new Vector2(songInfoPos.X, songInfoVisible ? songInfoPos.Y + 28 : songInfoPos.Y);
        string fpsText = ErrorOverlay.Safe($"FPS: {fps:F0}  {(vsyncEnabled ? "VSYNC" : "UNCAPPED")}");
        spriteBatch.DrawString(font, fpsText, fpsPos + new Vector2(2, 2), Color.Black * 0.6f);
        spriteBatch.DrawString(font, fpsText, fpsPos, Color.White * 0.7f);
#endif
    }

    private static void DrawShadowedText(
        SpriteBatch spriteBatch,
        SpriteFont font,
        string text,
        Vector2 pos,
        Color color,
        float scale,
        float shadowOffset = 2f,
        float shadowAlpha = 1f)
    {
        spriteBatch.DrawString(font, text,
            pos + new Vector2(shadowOffset, shadowOffset), Color.Black * (0.7f * shadowAlpha),
            0f, Vector2.Zero, scale, SpriteEffects.None, 0f);
        spriteBatch.DrawString(font, text,
            pos, color,
            0f, Vector2.Zero, scale, SpriteEffects.None, 0f);
    }
}
