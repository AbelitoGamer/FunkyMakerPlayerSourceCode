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
        bool vsyncEnabled,
        int opponentMisses = 0,
        float opponentAccuracy = 100f,
        int opponentColorHue = 180)
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
        bool useReworkedMpHud = isMultiplayerMatch && !levelHasOwnBar && pixel != null && noteRenderers.Count >= 2;

        // Solo health bar + center icons only draw outside the reworked multiplayer VS HUD
        if (!levelHasOwnBar && healthBar != null && !useReworkedMpHud)
            healthBar.Draw(sh, NoteRenderer.Downscroll);

        if (font == null || noteRenderers.Count == 0 || level == null) return;

        var playerRenderer = noteRenderers[0];
        if (playerRenderer == null) return;

        var playerStats = CalculateStats(playerRenderer);

        if (useReworkedMpHud)
        {
            DrawMultiplayerHUD(
                spriteBatch, font, pixel!, sw, sh, level.LevelName,
                hudSettings, healthBar, timerBar, playerStats,
                opponentName, opponentScore, opponentHealth,
                opponentMisses, opponentAccuracy, opponentColorHue,
                songTime, songEndTime, songBeats, started, paused, lastDt, vsyncEnabled);
        }
        else
        {
            if (!levelHasOwnBar)
                DrawSingleplayerStats(spriteBatch, font, hudSettings, playerStats);

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
        }

        if (Game1.ShowDebugInfo)
            DrawDebugInfo(spriteBatch, font, rtWidth, rtHeight, level, events, cameraManager, songBeats);

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

    private static void DrawMultiplayerHUD(
        SpriteBatch spriteBatch,
        SpriteFont font,
        Texture2D pixel,
        int sw,
        int sh,
        string levelDisplayName,
        HudSettings hudSettings,
        HealthBar? healthBar,
        TimerBar? timerBar,
        GameplayStats playerStats,
        string? opponentName,
        int opponentScore,
        float opponentHealth,
        int opponentMisses,
        float opponentAccuracy,
        int opponentColorHue,
        float songTime,
        float songEndTime,
        float songBeats,
        bool started,
        bool paused,
        double lastDt,
        bool vsyncEnabled)
    {
        var mp = Multiplayer.MultiplayerSettings.Instance;
        float hudAlpha = Math.Clamp(mp.HudOpacity / 100f, 0.1f, 1f);
        bool compact = mp.InterfaceMode == Multiplayer.MultiplayerSettings.UIMode.Compact;
        bool downscroll = NoteRenderer.Downscroll;

        float uiScale = Math.Clamp(Math.Min(sw / 1280f, sh / 720f), 0.72f, 1.35f);
        int marginX = Math.Max(10, (int)MathF.Round(12 * uiScale));
        int headerY = Math.Max(6, (int)MathF.Round(8 * uiScale));
        int headerH = Math.Max(22, (int)MathF.Round(26 * uiScale));

        // Subtle beat pulse matching FNF health icon bounce
        float beatFrac = songBeats - MathF.Floor(songBeats);
        float iconBounce = started
            ? 1f + 0.13f * MathF.Pow(Math.Max(0f, 1f - beatFrac * 2.6f), 2f)
            : 1f;

        string cleanSongName = Game1.SanitizeLevelDisplayName(levelDisplayName);
        string playerName = string.IsNullOrWhiteSpace(mp.Username) ? "Player" : mp.Username.Trim();
        string oppName = string.IsNullOrWhiteSpace(opponentName) ? "Opponent" : opponentName.Trim();

        Color playerBaseColor = healthBar?.PlayerBarColor ?? mp.GetPlayerColor();
        Color playerAccent = Color.Lerp(playerBaseColor, mp.GetPlayerColor(), 0.25f);
        Color oppFallbackColor = Multiplayer.MultiplayerSettings.ColorFromHue(opponentColorHue);
        Color oppBaseColor = healthBar?.OpponentBarColor ?? oppFallbackColor;
        Color oppAccent = Color.Lerp(oppBaseColor, oppFallbackColor, 0.20f);

        float playerHealth = Math.Clamp(healthBar?.Health ?? 0.5f, 0f, 1f);
        float oppHealthClamped = Math.Clamp(opponentHealth, 0f, 1f);

        // ── Top header bar (timer centered, ping + song info placed without touching receptors) ──
        int timerW = Math.Clamp((int)MathF.Round(420 * uiScale), 200, Math.Max(200, sw - 260));
        int timerX = (sw - timerW) / 2;
        if (timerBar != null)
        {
            timerBar.ShowTimer = Game1.ShowTimer;
            timerBar.Preset = "Custom HUD";
            timerBar.TextMode = Game1.TimerTextMode;
            timerBar.ForceTop = true;
            timerBar.ColorPreset = Game1.TimerColorPreset;
            timerBar.Position = new Point(timerX, headerY);
            timerBar.Size = new Point(timerW, headerH);

            timerBar.Draw(sw, sh, songTime, songEndTime, downscroll: false,
                cleanSongName, playerBaseColor, oppBaseColor, started);
        }

        int pingPillW = 0;
        if (mp.ShowConnectionQuality &&
            (mp.ConnectionIndicatorPosition == Multiplayer.MultiplayerSettings.IndicatorPosition.TopLeft ||
             mp.ConnectionIndicatorPosition == Multiplayer.MultiplayerSettings.IndicatorPosition.TopRight))
        {
            bool pingOnLeft = mp.ConnectionIndicatorPosition == Multiplayer.MultiplayerSettings.IndicatorPosition.TopLeft;
            pingPillW = DrawMultiplayerPingBadge(
                spriteBatch, font, pixel,
                pingOnLeft ? marginX : sw - marginX, headerY, headerH,
                alignRight: !pingOnLeft, compact, uiScale, hudAlpha);
        }

        // Song title pill on TopLeft cleanly bounded before TimerBar
        int songStartX = marginX;
        if (mp.ShowConnectionQuality &&
            mp.ConnectionIndicatorPosition == Multiplayer.MultiplayerSettings.IndicatorPosition.TopLeft &&
            pingPillW > 0)
        {
            songStartX += pingPillW + (int)MathF.Round(8 * uiScale);
        }

        int maxSongPillW = Math.Max(0, timerX - (int)MathF.Round(10 * uiScale) - songStartX);
        if (hudSettings.SongInfoVisible && maxSongPillW >= (int)MathF.Round(90 * uiScale))
        {
            float songTextScale = 0.72f * uiScale;
            int textPadX = Math.Max(8, (int)MathF.Round(10 * uiScale));
            string fittedSong = TruncateToWidth(font, ErrorOverlay.Safe(cleanSongName), maxSongPillW - textPadX * 2, songTextScale);
            Vector2 songSz = font.MeasureString(fittedSong) * songTextScale;
            int pillW = Math.Min(maxSongPillW, (int)MathF.Ceiling(songSz.X) + textPadX * 2);
            DrawBorderedCard(spriteBatch, pixel, new Rectangle(songStartX, headerY, pillW, headerH),
                new Color(14, 16, 26) * (0.76f * hudAlpha),
                Color.White * (0.20f * hudAlpha));
            DrawShadowedText(spriteBatch, font, fittedSong,
                new Vector2(songStartX + textPadX, headerY + (headerH - songSz.Y) * 0.5f),
                Color.White * hudAlpha, songTextScale, 1.5f, hudAlpha);
        }

        // TopRight utility tags (FPS + concise scroll/speed/pause) clear of TopRight Ping if present
        int rightCursorX = sw - marginX;
        if (mp.ShowConnectionQuality &&
            mp.ConnectionIndicatorPosition == Multiplayer.MultiplayerSettings.IndicatorPosition.TopRight &&
            pingPillW > 0)
        {
            rightCursorX -= pingPillW + (int)MathF.Round(8 * uiScale);
        }

        int maxRightSlotW = Math.Max(0, rightCursorX - (timerX + timerW + (int)MathF.Round(10 * uiScale)));
        if (maxRightSlotW >= (int)MathF.Round(80 * uiScale))
        {
            var metaParts = new List<string>();
            if (paused) metaParts.Add("PAUSED");
            if (hudSettings.StatusInfoVisible)
                metaParts.Add($"{(downscroll ? "DOWN" : "UP")} x{NoteRenderer.ScrollMultiplier:F2}");
            if (Game1.ShowFpsCounter && lastDt > 0)
            {
                int fps = Math.Clamp((int)Math.Round(1.0 / lastDt), 1, 999);
                metaParts.Add(vsyncEnabled ? $"{fps} FPS VSYNC" : $"{fps} FPS");
            }

            if (metaParts.Count > 0)
            {
                string metaStr = ErrorOverlay.Safe(string.Join("  |  ", metaParts));
                float metaScale = 0.64f * uiScale;
                int padX = Math.Max(8, (int)MathF.Round(9 * uiScale));
                string fittedMeta = TruncateToWidth(font, metaStr, maxRightSlotW - padX * 2, metaScale);
                Vector2 metaSz = font.MeasureString(fittedMeta) * metaScale;
                int pillW = Math.Min(maxRightSlotW, (int)MathF.Ceiling(metaSz.X) + padX * 2);
                int pillX = rightCursorX - pillW;
                DrawBorderedCard(spriteBatch, pixel, new Rectangle(pillX, headerY, pillW, headerH),
                    new Color(14, 16, 26) * (0.72f * hudAlpha),
                    Color.White * (0.16f * hudAlpha));
                DrawShadowedText(spriteBatch, font, fittedMeta,
                    new Vector2(pillX + padX, headerY + (headerH - metaSz.Y) * 0.5f),
                    new Color(210, 215, 232) * (0.90f * hudAlpha), metaScale, 1.2f, hudAlpha);
            }
        }

        // ── Symmetric 3-part VS Dock (Opponent Card | Lead Badge | Player Card) ──
        float glyphH = MathF.Max(14f, font.MeasureString("A").Y);
        int minFullCardH = (int)MathF.Ceiling(
            6f * uiScale + glyphH * 0.74f * uiScale + 4f * uiScale +
            12f * uiScale + 5f * uiScale + glyphH * 0.62f * uiScale + 7f * uiScale);
        int minCompactCardH = (int)MathF.Ceiling(
            6f * uiScale + glyphH * 0.68f * uiScale + 4f * uiScale +
            10f * uiScale + 8f * uiScale);
        int cardH = compact
            ? Math.Max((int)MathF.Round(52 * uiScale), minCompactCardH)
            : Math.Max((int)MathF.Round(74 * uiScale), minFullCardH);
        int dockY = downscroll
            ? headerY + headerH + Math.Max(6, (int)MathF.Round(8 * uiScale))
            : sh - cardH - Math.Max(10, (int)MathF.Round(12 * uiScale));

        int availableW = sw - marginX * 2;
        int gap = Math.Max(6, (int)MathF.Round(10 * uiScale));
        int centerW = (int)MathF.Round((compact ? 118 : 146) * uiScale);
        int maxSideW = Math.Max(140, (availableW - centerW - gap * 2) / 2);
        int sideW = Math.Min((int)MathF.Round(356 * uiScale), maxSideW);
        int totalDockW = sideW * 2 + centerW + gap * 2;
        int dockX = (sw - totalDockW) / 2;

        var oppCardRect = new Rectangle(dockX, dockY, sideW, cardH);
        var centerRect = new Rectangle(oppCardRect.Right + gap, dockY, centerW, cardH);
        var playerCardRect = new Rectangle(centerRect.Right + gap, dockY, sideW, cardH);

        DrawVsFighterCard(
            spriteBatch, font, pixel, oppCardRect,
            isLocalPlayer: false,
            displayName: oppName,
            icon: healthBar?.OpponentIcon,
            flipIcon: healthBar?.FlipOpponentIcon ?? false,
            accentColor: oppAccent,
            healthRatio: oppHealthClamped,
            score: opponentScore,
            accuracy: opponentAccuracy,
            misses: opponentMisses,
            hideScore: !mp.ShowOpponentScore,
            compact: compact,
            iconBounce: iconBounce,
            uiScale: uiScale,
            hudAlpha: hudAlpha);

        DrawCenterVsBadge(
            spriteBatch, font, pixel, centerRect,
            playerStats.Score, opponentScore, playerStats.Combo,
            mp.ShowOpponentScore, compact, uiScale, hudAlpha);

        DrawVsFighterCard(
            spriteBatch, font, pixel, playerCardRect,
            isLocalPlayer: true,
            displayName: playerName,
            icon: healthBar?.PlayerIcon,
            flipIcon: healthBar?.FlipPlayerIcon ?? false,
            accentColor: playerAccent,
            healthRatio: playerHealth,
            score: playerStats.Score,
            accuracy: playerStats.Accuracy,
            misses: playerStats.Misses,
            hideScore: false,
            compact: compact,
            iconBounce: iconBounce,
            uiScale: uiScale,
            hudAlpha: hudAlpha);

        // Bottom corner connection indicator (if configured for BottomLeft / BottomRight)
        if (mp.ShowConnectionQuality &&
            (mp.ConnectionIndicatorPosition == Multiplayer.MultiplayerSettings.IndicatorPosition.BottomLeft ||
             mp.ConnectionIndicatorPosition == Multiplayer.MultiplayerSettings.IndicatorPosition.BottomRight))
        {
            bool pingLeft = mp.ConnectionIndicatorPosition == Multiplayer.MultiplayerSettings.IndicatorPosition.BottomLeft;
            var pingMetrics = MeasureMultiplayerPingBadge(font, compact, uiScale);
            int gapToDock = Math.Max(8, (int)MathF.Round(10 * uiScale));
            bool fitsAlongsideDock = downscroll || (pingLeft
                ? (oppCardRect.Left - marginX - gapToDock >= pingMetrics.BoxW)
                : (sw - marginX - playerCardRect.Right - gapToDock >= pingMetrics.BoxW));
            int pingY = fitsAlongsideDock
                ? sh - headerH - Math.Max(10, (int)MathF.Round(12 * uiScale))
                : dockY - headerH - Math.Max(6, (int)MathF.Round(8 * uiScale));
            int pingX = pingLeft ? marginX : sw - marginX;
            DrawMultiplayerPingBadge(
                spriteBatch, font, pixel,
                pingX, pingY, headerH,
                alignRight: !pingLeft, compact, uiScale, hudAlpha);
        }
    }

    private readonly record struct PingBadgeMetrics(
        string Text,
        Color Color,
        float TextScale,
        Vector2 TextSize,
        int DotSize,
        int PadX,
        int BoxW);

    private static PingBadgeMetrics MeasureMultiplayerPingBadge(
        SpriteFont font,
        bool compact,
        float uiScale)
    {
        int ping = NetworkManager.Instance.Ping;
        var quality = NetworkManager.Instance.Quality;
        Color pingColor = quality switch
        {
            ConnectionQuality.Excellent => new Color(90, 235, 130),
            ConnectionQuality.Good => new Color(120, 210, 255),
            ConnectionQuality.Fair => new Color(255, 205, 80),
            ConnectionQuality.Poor => new Color(255, 145, 70),
            ConnectionQuality.Critical => new Color(255, 90, 90),
            _ => new Color(175, 180, 195)
        };

        string pingText = compact
            ? (ping > 0 ? $"{ping}ms" : "-- ms")
            : (ping > 0 ? $"Ping {ping}ms ({quality})" : $"Ping -- ({quality})");

        float textScale = 0.68f * uiScale;
        Vector2 txtSize = font.MeasureString(pingText) * textScale;
        int dotSize = Math.Max(6, (int)MathF.Round(7 * uiScale));
        int padX = Math.Max(8, (int)MathF.Round(10 * uiScale));
        int boxW = padX * 2 + dotSize + (int)MathF.Round(7 * uiScale) + (int)MathF.Ceiling(txtSize.X);

        return new PingBadgeMetrics(pingText, pingColor, textScale, txtSize, dotSize, padX, boxW);
    }

    private static int DrawMultiplayerPingBadge(
        SpriteBatch spriteBatch,
        SpriteFont font,
        Texture2D pixel,
        int anchorX,
        int y,
        int h,
        bool alignRight,
        bool compact,
        float uiScale,
        float hudAlpha)
    {
        var m = MeasureMultiplayerPingBadge(font, compact, uiScale);
        int boxX = alignRight ? anchorX - m.BoxW : anchorX;

        DrawBorderedCard(spriteBatch, pixel, new Rectangle(boxX, y, m.BoxW, h),
            new Color(14, 16, 26) * (0.82f * hudAlpha),
            m.Color * (0.45f * hudAlpha));

        int dotY = y + (h - m.DotSize) / 2;
        spriteBatch.Draw(pixel, new Rectangle(boxX + m.PadX, dotY, m.DotSize, m.DotSize), m.Color * hudAlpha);

        int textX = boxX + m.PadX + m.DotSize + (int)MathF.Round(7 * uiScale);
        DrawShadowedText(spriteBatch, font, m.Text,
            new Vector2(textX, y + (h - m.TextSize.Y) * 0.5f),
            m.Color * hudAlpha, m.TextScale, 1.2f, hudAlpha);

        return m.BoxW;
    }

    private static void DrawVsFighterCard(
        SpriteBatch spriteBatch,
        SpriteFont font,
        Texture2D pixel,
        Rectangle rect,
        bool isLocalPlayer,
        string displayName,
        SpriteAnimator? icon,
        bool flipIcon,
        Color accentColor,
        float healthRatio,
        int score,
        float accuracy,
        int misses,
        bool hideScore,
        bool compact,
        float iconBounce,
        float uiScale,
        float hudAlpha)
    {
        bool lowHealth = healthRatio < 0.22f;
        Color brightAccent = Color.Lerp(accentColor, Color.White, 0.28f);
        Color borderColor = lowHealth
            ? new Color(255, 95, 95) * (0.80f * hudAlpha)
            : brightAccent * ((isLocalPlayer ? 0.65f : 0.45f) * hudAlpha);

        DrawBorderedCard(spriteBatch, pixel, rect,
            new Color(14, 16, 26) * (0.88f * hudAlpha),
            borderColor);

        // Crisp side accent stripe on outer edge
        int stripeW = Math.Max(3, (int)MathF.Round(3 * uiScale));
        int stripeX = isLocalPlayer ? rect.Right - stripeW : rect.X;
        spriteBatch.Draw(pixel, new Rectangle(stripeX, rect.Y + 1, stripeW, rect.Height - 2),
            brightAccent * (0.90f * hudAlpha));

        int pad = Math.Max(6, (int)MathF.Round(8 * uiScale));
        int iconBoxSize = Math.Max(34, rect.Height - pad * 2);
        int iconBoxX = isLocalPlayer ? rect.Right - pad - stripeW - iconBoxSize : rect.X + pad + stripeW;
        int iconBoxY = rect.Y + (rect.Height - iconBoxSize) / 2;

        // Framed animated icon slot
        var iconBoxRect = new Rectangle(iconBoxX, iconBoxY, iconBoxSize, iconBoxSize);
        DrawBorderedCard(spriteBatch, pixel, iconBoxRect,
            Color.Lerp(new Color(20, 22, 34), accentColor, 0.22f) * (0.92f * hudAlpha),
            brightAccent * (0.45f * hudAlpha));
        DrawBoundedVsIcon(spriteBatch, icon, iconBoxRect, iconBounce, flipIcon, hudAlpha);

        int contentX = isLocalPlayer ? rect.X + pad : iconBoxRect.Right + pad;
        int contentW = Math.Max(60, (isLocalPlayer ? iconBoxRect.Left - pad : rect.Right - pad) - contentX);

        // Row 1: Player Name (left) + Health % (right)
        string hpStr = $"{healthRatio * 100f:F0}%";
        float hpScale = (compact ? 0.68f : 0.72f) * uiScale;
        Vector2 hpSz = font.MeasureString(hpStr) * hpScale;
        Color hpColor = lowHealth
            ? new Color(255, 100, 100)
            : (healthRatio >= 0.75f ? new Color(130, 250, 155) : Color.White);

        string nameTag = isLocalPlayer ? $"YOU  {displayName}" : displayName;
        float nameScale = (compact ? 0.68f : 0.74f) * uiScale;
        int maxNameW = Math.Max(40, contentW - (int)MathF.Ceiling(hpSz.X) - (int)MathF.Round(8 * uiScale));
        string fittedName = TruncateToWidth(font, ErrorOverlay.Safe(nameTag), maxNameW, nameScale);

        int row1Y = rect.Y + Math.Max(4, (int)MathF.Round((compact ? 5 : 6) * uiScale));
        DrawShadowedText(spriteBatch, font, fittedName,
            new Vector2(contentX, row1Y),
            Color.Lerp(accentColor, Color.White, 0.55f) * hudAlpha, nameScale, 1.5f, hudAlpha);
        DrawShadowedText(spriteBatch, font, hpStr,
            new Vector2(contentX + contentW - hpSz.X, row1Y),
            hpColor * hudAlpha, hpScale, 1.5f, hudAlpha);

        // Row 2: Health Bar
        int barH = Math.Max(8, (int)MathF.Round((compact ? 10 : 12) * uiScale));
        int barY = row1Y + (int)MathF.Ceiling(font.MeasureString("A").Y * nameScale) + Math.Max(3, (int)MathF.Round(4 * uiScale));
        DrawSplitHealthBar(
            spriteBatch, pixel,
            contentX, barY, contentW, barH,
            healthRatio, lowHealth ? new Color(255, 75, 75) : accentColor, hudAlpha);

        if (compact)
            return;

        // Row 3 (Full mode): Score (left) + Accuracy/Misses (right)
        int row3Y = barY + barH + Math.Max(4, (int)MathF.Round(5 * uiScale));
        float subScale = 0.62f * uiScale;

        string scoreText = hideScore ? "Score: --" : $"Score: {score:N0}";
        string rightText = hideScore
            ? "Opponent"
            : $"Acc: {accuracy:F1}%  M:{misses}";

        Vector2 rightSz = font.MeasureString(rightText) * subScale;
        int maxScoreW = Math.Max(40, contentW - (int)MathF.Ceiling(rightSz.X) - (int)MathF.Round(6 * uiScale));
        Vector2 scoreSz = font.MeasureString(scoreText) * subScale;
        if (scoreSz.X > maxScoreW)
            scoreText = hideScore ? "--" : $"{score:N0}";

        DrawShadowedText(spriteBatch, font, ErrorOverlay.Safe(scoreText),
            new Vector2(contentX, row3Y),
            new Color(230, 234, 245) * hudAlpha, subScale, 1.2f, hudAlpha);

        Color rightCol = hideScore
            ? new Color(170, 175, 195)
            : (accuracy >= 95f ? new Color(120, 245, 145) :
               accuracy >= 80f ? new Color(255, 225, 110) :
                                 new Color(255, 130, 130));
        string fittedRight = TruncateToWidth(font, ErrorOverlay.Safe(rightText), contentW, subScale);
        Vector2 fittedRightSz = font.MeasureString(fittedRight) * subScale;
        DrawShadowedText(spriteBatch, font, fittedRight,
            new Vector2(contentX + contentW - fittedRightSz.X, row3Y),
            rightCol * hudAlpha, subScale, 1.2f, hudAlpha);
    }

    private static void DrawCenterVsBadge(
        SpriteBatch spriteBatch,
        SpriteFont font,
        Texture2D pixel,
        Rectangle rect,
        int playerScore,
        int opponentScore,
        int playerCombo,
        bool showOpponentScore,
        bool compact,
        float uiScale,
        float hudAlpha)
    {
        int diff = playerScore - opponentScore;
        Color statusColor = !showOpponentScore
            ? new Color(150, 175, 245)
            : (diff > 0 ? new Color(95, 240, 140) :
               diff < 0 ? new Color(255, 105, 105) :
                          new Color(255, 215, 105));

        DrawBorderedCard(spriteBatch, pixel, rect,
            new Color(14, 16, 26) * (0.90f * hudAlpha),
            statusColor * (0.55f * hudAlpha));

        string topTag = !showOpponentScore ? "VS MATCH"
            : (diff > 0 ? "WINNING" : diff < 0 ? "BEHIND" : "TIED");
        string mainText = !showOpponentScore
            ? "VS"
            : (diff > 0 ? $"+{diff:N0}" : $"{diff:N0}");
        string bottomText = playerCombo > 0 ? $"{playerCombo}x Combo" : "0x Combo";

        float topScale = 0.54f * uiScale;
        float mainScale = (compact ? 0.74f : 0.80f) * uiScale;
        float botScale = 0.56f * uiScale;

        int padX = Math.Max(6, (int)MathF.Round(6 * uiScale));
        int innerW = Math.Max(20, rect.Width - padX * 2);

        string fittedTop = TruncateToWidth(font, topTag, innerW, topScale);
        string fittedMain = TruncateToWidth(font, mainText, innerW, mainScale);
        string fittedBot = TruncateToWidth(font, bottomText, innerW, botScale);

        Vector2 topSz = font.MeasureString(fittedTop) * topScale;
        Vector2 mainSz = font.MeasureString(fittedMain) * mainScale;
        Vector2 botSz = font.MeasureString(fittedBot) * botScale;

        if (compact)
        {
            float totalH = topSz.Y + mainSz.Y + 2f;
            float y0 = rect.Y + (rect.Height - totalH) * 0.5f;
            DrawShadowedText(spriteBatch, font, fittedTop,
                new Vector2(rect.Center.X - topSz.X * 0.5f, y0),
                statusColor * hudAlpha, topScale, 1.2f, hudAlpha);
            DrawShadowedText(spriteBatch, font, fittedMain,
                new Vector2(rect.Center.X - mainSz.X * 0.5f, y0 + topSz.Y + 2f),
                statusColor * hudAlpha, mainScale, 1.5f, hudAlpha);
        }
        else
        {
            float totalH = topSz.Y + mainSz.Y + botSz.Y + 6f;
            float y0 = rect.Y + (rect.Height - totalH) * 0.5f;
            DrawShadowedText(spriteBatch, font, fittedTop,
                new Vector2(rect.Center.X - topSz.X * 0.5f, y0),
                statusColor * hudAlpha, topScale, 1.2f, hudAlpha);
            DrawShadowedText(spriteBatch, font, fittedMain,
                new Vector2(rect.Center.X - mainSz.X * 0.5f, y0 + topSz.Y + 2f),
                statusColor * hudAlpha, mainScale, 1.5f, hudAlpha);
            DrawShadowedText(spriteBatch, font, fittedBot,
                new Vector2(rect.Center.X - botSz.X * 0.5f, y0 + topSz.Y + mainSz.Y + 4f),
                new Color(205, 212, 228) * (0.90f * hudAlpha), botScale, 1.2f, hudAlpha);
        }
    }

    private static void DrawBorderedCard(SpriteBatch sb, Texture2D pixel, Rectangle rect, Color fill, Color border)
    {
        if (rect.Width <= 0 || rect.Height <= 0) return;
        sb.Draw(pixel, rect, fill);
        sb.Draw(pixel, new Rectangle(rect.X, rect.Y, rect.Width, 1), border);
        sb.Draw(pixel, new Rectangle(rect.X, rect.Bottom - 1, rect.Width, 1), border);
        sb.Draw(pixel, new Rectangle(rect.X, rect.Y, 1, rect.Height), border);
        sb.Draw(pixel, new Rectangle(rect.Right - 1, rect.Y, 1, rect.Height), border);
    }

    private static string TruncateToWidth(SpriteFont font, string text, float maxWidth, float scale)
    {
        if (string.IsNullOrEmpty(text) || maxWidth <= 0)
            return "";
        if (font.MeasureString(text).X * scale <= maxWidth)
            return text;

        const string ellipsis = "...";
        if (font.MeasureString(ellipsis).X * scale > maxWidth)
            return "";

        int len = text.Length;
        while (len > 0 && font.MeasureString(text.Substring(0, len) + ellipsis).X * scale > maxWidth)
            len--;
        return text.Substring(0, len) + ellipsis;
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
        spriteBatch.Draw(pixel, new Rectangle(barX - 1, barY - 1, barWidth + 2, barHeight + 2), Color.Black * (0.85f * alpha));
        spriteBatch.Draw(pixel, new Rectangle(barX, barY, barWidth, barHeight), new Color(26, 28, 38) * alpha);

        int fillW = (int)MathF.Round(Math.Clamp(healthRatio, 0f, 1f) * barWidth);
        if (fillW > 0)
        {
            Color fillStart = Color.Lerp(baseColor, Color.Black, 0.25f) * alpha;
            Color fillEnd = Color.Lerp(baseColor, Color.White, 0.25f) * alpha;
            spriteBatch.Draw(pixel, new Rectangle(barX, barY, fillW, barHeight), fillStart);
            int halfW = Math.Max(1, fillW / 2);
            spriteBatch.Draw(pixel, new Rectangle(barX + fillW - halfW, barY, halfW, barHeight), fillEnd);

            // Subtle top highlight for clean polish
            int sheenH = Math.Max(1, barHeight / 4);
            spriteBatch.Draw(pixel, new Rectangle(barX, barY, fillW, sheenH), Color.White * (0.22f * alpha));
        }
    }

    private static void DrawBoundedVsIcon(
        SpriteBatch spriteBatch,
        SpriteAnimator? icon,
        Rectangle box,
        float bounceScale,
        bool flipX = false,
        float alpha = 1f)
    {
        if (icon == null || box.Width <= 0 || box.Height <= 0) return;
        var (tex, src) = icon.GetCurrentFrameTexture();
        if (tex == null || src.Width <= 0 || src.Height <= 0) return;

        float innerSize = (box.Width - 6) * Math.Clamp(bounceScale, 0.9f, 1.15f);
        float fitScale = innerSize / Math.Max(src.Width, src.Height);
        int drawW = Math.Max(1, (int)MathF.Round(src.Width * fitScale));
        int drawH = Math.Max(1, (int)MathF.Round(src.Height * fitScale));
        int drawX = box.Center.X - drawW / 2;
        int drawY = box.Center.Y - drawH / 2;
        var fx = flipX ? SpriteEffects.FlipHorizontally : SpriteEffects.None;
        spriteBatch.Draw(tex, new Rectangle(drawX, drawY, drawW, drawH), src, Color.White * alpha, 0f, Vector2.Zero, fx, 0f);
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
