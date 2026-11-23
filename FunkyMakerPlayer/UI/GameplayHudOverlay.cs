using System;
using System.Linq;
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
/// <summary>One row of the in-match scores list (fork, room system).</summary>
public readonly record struct MatchScoreEntry(string Name, int Hue, long Score, int Misses, bool IsLocal, int Team, bool Gone);

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
        IReadOnlyList<MatchScoreEntry>? scoreList,
        bool teamsScoreList,
        float songTime,
        float songEndTime,
        float songBeats,
        bool started,
        bool paused,
        double lastDt,
        bool vsyncEnabled,
        bool softHudEvents = false)
    {
        // GUI event can hide the entire engine HUD (health bar, score, timer, etc.).
        // Fork: not in online matches / duel replays (isMultiplayerMatch covers both), where it
        // only halves the strumline opacity (Game1.SoftHudEvents); Freeplay enemy mode does the same.
        if (events?.HudVisible == false && !isMultiplayerMatch && !softHudEvents) return;

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

        // Fork: online matches use the normal health bar too (it shows the score difference there,
        // see Game1.Online.cs); the old VS HUD with two bars is gone.
        if (!levelHasOwnBar && healthBar != null)
            healthBar.Draw(sh, NoteRenderer.Downscroll);

        if (font == null || noteRenderers.Count == 0 || level == null) return;

        var playerRenderer = noteRenderers[0];
        if (playerRenderer == null) return;

        var playerStats = CalculateStats(playerRenderer);

        {
            if (!levelHasOwnBar)
                DrawSingleplayerStats(spriteBatch, font, hudSettings, playerStats);

            if (timerBar != null)
            {
                timerBar.ShowTimer = Game1.ShowTimer;
                timerBar.Preset = Game1.TimerPreset;
                timerBar.TextMode = Game1.TimerTextMode;
                timerBar.Stars = Game1.TimerTextMode == "Song Name + Difficulty" ? StarsFor(level) : null;
                timerBar.ForceTop = Game1.TimerForceTop;
                timerBar.ColorPreset = Game1.TimerColorPreset;
                timerBar.Position = new Point(hudSettings.TimerBarPosition.X, hudSettings.TimerBarPosition.Y);
                timerBar.Size = new Point(hudSettings.TimerBarSize.X, hudSettings.TimerBarSize.Y);

                timerBar.Draw(sw, sh, songTime, songEndTime, NoteRenderer.Downscroll,
                    level.LevelName, healthBar?.PlayerBarColor, healthBar?.OpponentBarColor, started);
            }

            Vector2 songInfoPos = new Vector2(hudSettings.SongInfoPosition.X, hudSettings.SongInfoPosition.Y);
            if (hudSettings.SongInfoVisible)
                DrawShadowedText(spriteBatch, font, ErrorOverlay.Safe(level.LevelName), songInfoPos, Color.White, 1.0f * hudSettings.SongInfoScale);

            if (Game1.ShowFpsCounter)
                DrawFpsCounter(spriteBatch, font, pixel, sw, songInfoPos, hudSettings.SongInfoVisible, lastDt, vsyncEnabled);

            if (hudSettings.StatusInfoVisible)
            {
                string autoStr = NoteRenderer.Botplay ? "[BOTPLAY]" : "[MANUAL]";
                string scrollStr = NoteRenderer.Downscroll ? "[DOWN]" : "[UP]";
                string pauseStr = paused ? "[PAUSED]" : "";
                string speedStr = ErrorOverlay.Safe($"Speed:x{NoteRenderer.ScrollMultiplier:F2}");
                // Freeplay modifiers (fork)
                if (Math.Abs(Game1.CurrentRunRate - 1f) > 0.001f)
                    speedStr += ErrorOverlay.Safe($" [RATE x{Game1.CurrentRunRate:F2}]");
                if (Game1.CurrentRunEnemy) speedStr += " [ENEMY]";
                Vector2 statusPos = new Vector2(hudSettings.StatusInfoPosition.X, hudSettings.StatusInfoPosition.Y);
                spriteBatch.DrawString(font,
                    ErrorOverlay.Safe($"{autoStr} {scrollStr} {speedStr} {pauseStr}"),
                    statusPos,
                    NoteRenderer.Botplay ? Color.Yellow * 0.8f : Color.White * 0.4f,
                    0f, Vector2.Zero, hudSettings.StatusInfoScale, SpriteEffects.None, 0f);
            }
        }

        if (isMultiplayerMatch && pixel != null)
        {
            var mp = Multiplayer.MultiplayerSettings.Instance;
            if (scoreList != null && scoreList.Count > 0 && mp.ShowOpponentScore)
                DrawMatchScoreList(spriteBatch, font, pixel, sw, sh, scoreList, mp.ScoreListPosition == Multiplayer.MultiplayerSettings.ScoreListSide.Right, teamsScoreList);
            if (mp.ShowConnectionQuality && NetworkManager.Instance.IsConnected)
            {
                bool compact = mp.InterfaceMode == Multiplayer.MultiplayerSettings.UIMode.Compact;
                float uiScale = Math.Clamp(Math.Min(sw / 1280f, sh / 720f), 0.6f, 1.6f);
                int h = Math.Max(20, (int)MathF.Round(24 * uiScale));
                int margin = Math.Max(8, (int)MathF.Round(10 * uiScale));
                var pos = mp.ConnectionIndicatorPosition;
                bool left = pos is Multiplayer.MultiplayerSettings.IndicatorPosition.TopLeft or Multiplayer.MultiplayerSettings.IndicatorPosition.BottomLeft;
                bool top = pos is Multiplayer.MultiplayerSettings.IndicatorPosition.TopLeft or Multiplayer.MultiplayerSettings.IndicatorPosition.TopRight;
                DrawMultiplayerPingBadge(spriteBatch, font, pixel, left ? margin : sw - margin, top ? margin : sh - h - margin, h,
                    alignRight: !left, compact, uiScale, 1f);
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

    // Star rating of the level on screen, computed once per level (fork).
    private static FnmmLevel? _starsLevel;
    private static double? _starsValue;
    private static (float Rate, bool Enemy) _starsMods;

    private static double? StarsFor(FnmmLevel level)
    {
        var mods = (Game1.CurrentRunRate, Game1.CurrentRunEnemy);
        if (!ReferenceEquals(level, _starsLevel) || mods != _starsMods)
        {
            _starsLevel = level;
            _starsMods = mods;
            try
            {
                // Side and song rate of the Freeplay modifiers (fork)
                double st = ChartDifficulty.Calculate(level.Data, mods.Item2 ? 1 : 0, mods.Item1);
                _starsValue = st > 0 ? st : null;
            }
            catch { _starsValue = null; }
        }
        return _starsValue;
    }

    /// <summary>
    /// Small list of the match's players and their live scores (fork). <paramref name="list"/> is
    /// already sorted best first; in Teams it is grouped by team with the team averages.
    /// </summary>
    private static void DrawMatchScoreList(SpriteBatch sb, SpriteFont font, Texture2D pixel, int sw, int sh,
        IReadOnlyList<MatchScoreEntry> list, bool rightSide, bool teams)
    {
        float uiScale = Math.Clamp(Math.Min(sw / 1280f, sh / 720f), 0.6f, 1.6f);
        float textScale = 0.5f * uiScale;
        int rowH = (int)MathF.Round(24 * uiScale);
        int w = (int)MathF.Round(232 * uiScale);
        int margin = (int)MathF.Round(12 * uiScale);
        int x = rightSide ? sw - margin - w : margin;
        int rows = list.Count + (teams ? 2 : 0);
        int y = (int)(sh * 0.30f);
        if (y + rows * rowH > sh - margin) y = Math.Max(margin, sh - margin - rows * rowH);

        void Row(MatchScoreEntry e, int place, ref int yy)
        {
            var r = new Rectangle(x, yy, w, rowH - 2);
            Color c = Multiplayer.MultiplayerSettings.ColorFromHue(e.Hue);
            sb.Draw(pixel, r, (e.IsLocal ? new Color(40, 40, 64) : new Color(14, 16, 26)) * (e.Gone ? 0.45f : 0.78f));
            sb.Draw(pixel, new Rectangle(r.X, r.Y, Math.Max(3, (int)(4 * uiScale)), r.Height), c * (e.Gone ? 0.5f : 1f));
            float ty = r.Y + (r.Height - font.LineSpacing * textScale) / 2f;
            string placeText = $"{place}.";
            DrawShadowedText(sb, font, placeText, new Vector2(r.X + 8 * uiScale, ty), Color.White * 0.75f, textScale);
            // Players who left: "LEFT" tag instead of the live score, name struck through.
            string score = e.Gone ? $"LEFT  {e.Score:N0}" : e.Score.ToString("N0");
            float scoreW = font.MeasureString(score).X * textScale;
            float nameX = r.X + 30 * uiScale;
            string name = TruncateToWidth(font, ErrorOverlay.Safe(e.Name), r.Right - 10 * uiScale - scoreW - nameX - 6, textScale);
            Color nameColor = Color.Lerp(c, Color.White, e.IsLocal ? 0.6f : 0.35f) * (e.Gone ? 0.5f : 1f);
            DrawShadowedText(sb, font, name, new Vector2(nameX, ty), nameColor, textScale);
            if (e.Gone)
            {
                float nameW = font.MeasureString(name).X * textScale;
                sb.Draw(pixel, new Rectangle((int)nameX, (int)(ty + font.LineSpacing * textScale * 0.55f), (int)nameW, Math.Max(1, (int)(2 * uiScale))), nameColor);
            }
            DrawShadowedText(sb, font, score, new Vector2(r.Right - 8 * uiScale - scoreW, ty), e.Gone ? new Color(255, 110, 120) : Color.White, textScale);
            yy += rowH;
        }

        if (!teams)
        {
            int place = 0;
            long last = long.MinValue;
            for (int i = 0; i < list.Count; i++)
            {
                if (list[i].Score != last) place = i + 1;
                last = list[i].Score;
                Row(list[i], place, ref y);
            }
            return;
        }

        for (int team = 0; team <= 1; team++)
        {
            var members = list.Where(e => e.Team == team).ToList();
            if (members.Count == 0) continue;
            double avg = members.Average(e => (double)e.Score);
            string head = $"{(team == 0 ? "TEAM PLAYER" : "TEAM ENEMY")}   avg {avg:N0}";
            DrawShadowedText(sb, font, ErrorOverlay.Safe(head), new Vector2(x + 4, y + 2), team == 0 ? new Color(120, 210, 255) : new Color(255, 120, 140), textScale);
            y += rowH;
            for (int i = 0; i < members.Count; i++) Row(members[i], i + 1, ref y);
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
                Color.White, statsScale * hudSettings.ScoreScale);
        }

        if (hudSettings.MissesVisible)
        {
            Color missesColor = stats.Misses > 0 ? new Color(255, 100, 100) : Color.White;
            DrawShadowedText(
                spriteBatch, font,
                ErrorOverlay.Safe($"Misses: {stats.Misses}"),
                new Vector2(hudSettings.MissesPosition.X, hudSettings.MissesPosition.Y),
                missesColor, statsScale * hudSettings.MissesScale);
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
                accColor, statsScale * hudSettings.AccuracyScale);
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
