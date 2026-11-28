using System;
using System.Collections.Generic;
using System.Linq;
using FunkyMakerPlayer.Multiplayer.Rooms;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using Microsoft.Xna.Framework.Input;

namespace FunkyMakerPlayer;

/// <summary>
/// Results screen after an online round (fork: room system). A leaderboard of every player: place,
/// name, score, accuracy, misses (and team averages in Teams). While the others are still playing it
/// shows "waiting for results" with your own stats; the standings appear when the server sends them.
/// ENTER / ESC returns to the waiting room at any time (the round also shows up in its list).
/// </summary>
public class MultiplayerResultsScreen
{
    public enum Phase { FadeIn, Display, FadeOut, Done }

    public Phase CurrentPhase { get; private set; } = Phase.FadeIn;

    private readonly Texture2D _pixel;
    private readonly string _localId;
    private readonly string _songName;
    private readonly string _mode;
    private readonly PlayerResult? _mine;
    private RoundResult? _result;

    private float _timer;
    private float _alpha;
    private float _sinceResult;

    private const float FADE_IN_DURATION = 0.4f;
    private const float FADE_OUT_DURATION = 0.3f;
    /// <summary>Back to the room on its own this long after the standings arrive.</summary>
    private const float AUTO_CLOSE = 15f;

    /// <param name="mine">Your own final stats (null when you were spectating).</param>
    public MultiplayerResultsScreen(Texture2D pixel, string localId, string songName, string mode, PlayerResult? mine, RoundResult? result = null)
    {
        _pixel = pixel;
        _localId = localId;
        _songName = songName;
        _mode = mode;
        _mine = mine;
        _result = result;
    }

    public bool HasResult => _result != null;

    /// <summary>
    /// While the server ranking hasn't arrived: everybody's latest score (the others' live states),
    /// with whether they're still playing. It used to show only your own row until then.
    /// </summary>
    public Func<List<(PlayerResult Row, bool Playing)>>? LiveRows { get; set; }

    public void SetResult(RoundResult result)
    {
        _result = result;
        _sinceResult = 0f;
        var me = result.Players.FirstOrDefault(p => p.PlayerId == _localId);
        bool won = result.Mode == Protocol.ModeTeams
            ? me != null && me.Team == result.WinningTeam
            : me?.Place == 1;
        UI.UiSfx.Play(won ? UI.UiSound.Save : UI.UiSound.Receive);
    }

    /// <param name="tapped">Android: a finger was lifted this frame (tap anywhere = continue).</param>
    public void Update(float dt, KeyboardState kb, KeyboardState prevKb, bool tapped = false)
    {
        _timer += dt;
        if (_result != null) _sinceResult += dt;
        switch (CurrentPhase)
        {
            case Phase.FadeIn:
                _alpha = Math.Min(1f, _timer / FADE_IN_DURATION);
                if (_timer >= FADE_IN_DURATION) { _timer = 0f; CurrentPhase = Phase.Display; }
                break;
            case Phase.Display:
                // A short delay so a finger still on a lane when the song ended doesn't skip the screen.
                bool enter = (kb.IsKeyDown(Keys.Enter) && !prevKb.IsKeyDown(Keys.Enter)) || (tapped && _timer >= 0.35f);
                bool esc = kb.IsKeyDown(Keys.Escape) && !prevKb.IsKeyDown(Keys.Escape);
                if (enter || esc || (_result != null && _sinceResult >= AUTO_CLOSE))
                {
                    if (enter || esc) UI.UiSfx.Play(enter ? UI.UiSound.Confirm : UI.UiSound.Back);
                    _timer = 0f;
                    CurrentPhase = Phase.FadeOut;
                }
                break;
            case Phase.FadeOut:
                _alpha = Math.Max(0f, 1f - _timer / FADE_OUT_DURATION);
                if (_timer >= FADE_OUT_DURATION) CurrentPhase = Phase.Done;
                break;
        }
    }

    public void Draw(SpriteBatch sb, SpriteFont font, int sw, int sh)
    {
        if (CurrentPhase == Phase.Done) return;
        float a = _alpha;
        sb.Draw(_pixel, new Rectangle(0, 0, sw, sh), Color.Black * (0.86f * a));

        // ── Title ────────────────────────────────────────────────────────
        string title;
        Color titleColor;
        if (_result == null)
        {
            int dots = (int)(_timer * 2.5f) % 4;
            title = "WAITING FOR RESULTS" + new string('.', dots);
            titleColor = new Color(255, 215, 80);
        }
        else
        {
            var me = _result.Players.FirstOrDefault(p => p.PlayerId == _localId);
            if (_result.Mode == Protocol.ModeTeams)
            {
                if (_result.WinningTeam < 0) { title = "DRAW!"; titleColor = new Color(255, 215, 80); }
                else if (me == null) { title = _result.WinningTeam == 0 ? "TEAM PLAYER WINS" : "TEAM ENEMY WINS"; titleColor = Color.White; }
                else if (me.Team == _result.WinningTeam) { title = "VICTORY!"; titleColor = new Color(100, 255, 100); }
                else { title = "DEFEAT"; titleColor = new Color(255, 100, 100); }
            }
            else if (me == null)
            {
                var w = _result.Players.FirstOrDefault(p => p.Place == 1);
                title = w != null ? $"{w.Username} WINS" : "RESULTS";
                titleColor = Color.White;
            }
            else
            {
                title = me.Place switch { 1 => "1ST PLACE!", 2 => "2ND PLACE", 3 => "3RD PLACE", _ => $"{me.Place}TH PLACE" };
                titleColor = me.Place == 1 ? new Color(100, 255, 100) : me.Place <= 3 ? new Color(255, 215, 80) : new Color(255, 120, 120);
            }
        }
        DrawCentered(sb, font, UI.ErrorOverlay.Safe(title), sh * 0.06f, 1.8f, titleColor * a, sw);
        string sub = $"{_songName}   -   {RoomClient.ModeName(_mode)}";
        if (_result?.Mode == Protocol.ModeTeams && _result.TeamScores.Count == 2)
            sub += $"   -   Team Player {_result.TeamScores[0]:N0} avg  vs  Team Enemy {_result.TeamScores[1]:N0} avg";
        DrawCentered(sb, font, UI.ErrorOverlay.Safe(sub), sh * 0.06f + 64, 0.7f, new Color(185, 190, 212) * a, sw);

        // ── Table ────────────────────────────────────────────────────────
        if (_result != null && _result.Mode == Protocol.ModeTeams)
        {
            DrawTeamsTables(sb, font, sw, sh, sh * 0.06f + 110, a);
        }
        else
        {
            DrawSingleTable(sb, font, sw, sh, a);
        }

        // Your judgement breakdown.
        if (_mine != null)
        {
            string stats = $"Sick: {_mine.Sicks}   Good: {_mine.Goods}   Bad: {_mine.Bads}   Shit: {_mine.Shits}   Miss: {_mine.Misses}   Max combo: {_mine.MaxCombo}";
            DrawCentered(sb, font, stats, sh * 0.88f, 0.55f, Color.Gray * a, sw);
        }

        if (CurrentPhase == Phase.Display)
        {
            float pulse = 0.7f + 0.3f * MathF.Sin(_timer * 3f);
#if ANDROID
            string hint = _result == null ? "Tap to go back to the room (results will be in the round list)" : "Tap to continue";
#else
            string hint = UI.PointerInput.IsTouch
                ? (_result == null ? "Tap to go back to the room (results will be in the round list)" : "Tap to continue")
                : _result == null ? "Press ENTER or ESC to go back to the room (results will be in the round list)" : "Press ENTER or ESC to continue";
#endif
            DrawCentered(sb, font, hint, sh * 0.93f, 0.55f, Color.White * (a * pulse), sw);
        }
    }

    /// <summary>
    /// Teams (fork): one column per team (Team Player left, Team Enemy right) with its average score;
    /// the winning team's header is highlighted. Rows keep the overall place.
    /// </summary>
    private void DrawTeamsTables(SpriteBatch sb, SpriteFont font, int sw, int sh, float top, float a)
    {
        var result = _result!;
        float totalW = Math.Min(sw - 60, 1180);
        float gap = 24f;
        float colW = (totalW - gap) / 2f;
        float left = (sw - totalW) / 2f;
        int maxRows = Math.Max(1, Math.Max(result.Players.Count(p => p.Team == 0), result.Players.Count(p => p.Team == 1)));
        float headerH = 40f;
        float rowsTop = top + headerH + 30;
        float rowH = Math.Clamp((sh * 0.86f - rowsTop) / maxRows - 6, 28, 50);
        float textScale = Math.Clamp(rowH / 52f, 0.5f, 0.75f);
        float[] cols = { 0f, 0.10f, 0.56f, 0.78f, 0.93f };
        string[] heads = { "#", "PLAYER", "SCORE", "ACC", "MISS" };

        for (int team = 0; team < 2; team++)
        {
            float x0 = left + team * (colW + gap);
            bool won = result.WinningTeam == team;
            Color teamColor = team == 0 ? new Color(90, 200, 255) : new Color(255, 100, 140);
            var header = new Rectangle((int)x0, (int)top, (int)colW, (int)headerH);
            sb.Draw(_pixel, header, Color.Lerp(new Color(22, 22, 36), teamColor, won ? 0.35f : 0.12f) * (0.95f * a));
            if (won) DrawBorder(sb, header, new Color(255, 215, 80) * (0.9f * a));
            string avg = result.TeamScores.Count == 2 ? $"   {result.TeamScores[team]:N0} avg" : "";
            string label = (team == 0 ? "TEAM PLAYER" : "TEAM ENEMY") + avg + (won ? "   WIN" : "");
            Text(sb, font, Fit(font, label, colW - 20, 0.7f), x0 + 10, top + (headerH - font.LineSpacing * 0.7f) / 2f, 0.7f,
                (won ? new Color(255, 215, 80) : Color.Lerp(teamColor, Color.White, 0.3f)) * a);

            for (int c = 0; c < heads.Length; c++)
                sb.DrawUiString(font, heads[c], new Vector2(x0 + 8 + (colW - 16) * cols[c], top + headerH + 6), new Color(115, 120, 145) * a, 0f, Vector2.Zero, 0.5f, SpriteEffects.None, 0f);

            float y = rowsTop;
            foreach (var p in result.Players.Where(p => p.Team == team))
            {
                bool me = p.PlayerId == _localId;
                Color c = Multiplayer.MultiplayerSettings.ColorFromHue(p.ColorHue);
                var r = new Rectangle((int)x0, (int)y, (int)colW, (int)rowH);
                sb.Draw(_pixel, r, (me ? new Color(34, 34, 54) : new Color(22, 22, 36)) * (0.95f * a));
                sb.Draw(_pixel, new Rectangle(r.X, r.Y, 5, r.Height), c * a);
                float ty = y + (rowH - font.LineSpacing * textScale) / 2f;
                float cx(int i) => x0 + 8 + (colW - 16) * cols[i];
                Color placeCol = p.Place switch
                {
                    1 => new Color(255, 215, 80), 2 => new Color(200, 210, 225), 3 => new Color(215, 150, 90), _ => new Color(185, 190, 212),
                };
                Text(sb, font, p.Disconnected ? "-" : p.Place.ToString(), cx(0), ty, textScale, placeCol * a);
                float av = rowH - 10;
                Avatar(sb, font, p, cx(1), y + 5, av, a);
                string name = p.Username + (me ? " (you)" : "") + (p.Disconnected ? " [left]" : "");
                Text(sb, font, Fit(font, name, (colW - 16) * (cols[2] - cols[1]) - 16 - av, textScale), cx(1) + av + 8, ty, textScale, Color.Lerp(c, Color.White, 0.4f) * a);
                Text(sb, font, p.Score.ToString("N0"), cx(2), ty, textScale, Color.White * a);
                Text(sb, font, $"{p.Accuracy:F1}%", cx(3), ty, textScale, (PerfectAccuracy(p) ? Perfect : Color.White) * a);
                Text(sb, font, p.Misses.ToString(), cx(4), ty, textScale, (NoMisses(p) ? Perfect : Color.White) * a);
                y += rowH + 6;
            }
        }
    }

    private void DrawSingleTable(SpriteBatch sb, SpriteFont font, int sw, int sh, float a)
    {
        var live = _result == null ? LiveRows?.Invoke() : null;
        var rows = _result?.Players ?? live?.Select(l => l.Row).ToList()
                   ?? (_mine != null ? new List<PlayerResult> { _mine } : new List<PlayerResult>());
        var stillPlaying = live?.Where(l => l.Playing).Select(l => l.Row.PlayerId).ToHashSet() ?? new HashSet<string>();
        float tableW = Math.Min(sw - 80, 980);
        float x0 = (sw - tableW) / 2f;
        float y = sh * 0.06f + 110;
        float[] cols = { 0f, 0.07f, 0.42f, 0.57f, 0.73f, 0.87f };
        string[] heads = { "#", "PLAYER", _mode == Protocol.ModeTeams ? "TEAM" : "SIDE", "SCORE", "ACCURACY", "MISSES" };
        for (int c = 0; c < heads.Length; c++)
            sb.DrawUiString(font, heads[c], new Vector2(x0 + tableW * cols[c], y), new Color(115, 120, 145) * a, 0f, Vector2.Zero, 0.55f, SpriteEffects.None, 0f);
        y += 28;
        float rowH = Math.Clamp((sh * 0.86f - y) / Math.Max(1, rows.Count) - 6, 30, 52);
        float textScale = Math.Clamp(rowH / 52f, 0.55f, 0.8f);
        foreach (var p in rows)
        {
            bool me = p.PlayerId == _localId;
            Color c = Multiplayer.MultiplayerSettings.ColorFromHue(p.ColorHue);
            var r = new Rectangle((int)x0 - 8, (int)y, (int)tableW + 16, (int)rowH);
            sb.Draw(_pixel, r, (me ? new Color(34, 34, 54) : new Color(22, 22, 36)) * (0.95f * a));
            sb.Draw(_pixel, new Rectangle(r.X, r.Y, 5, r.Height), c * a);
            if (p.Place == 1 && _result != null)
                DrawBorder(sb, r, new Color(255, 215, 80) * (0.8f * a));
            float ty = y + (rowH - font.LineSpacing * textScale) / 2f;
            Color placeCol = _result == null ? Color.Gray : p.Place switch
            {
                1 => new Color(255, 215, 80), 2 => new Color(200, 210, 225), 3 => new Color(215, 150, 90), _ => new Color(185, 190, 212),
            };
            Text(sb, font, _result == null ? "?" : p.Disconnected ? "-" : p.Place.ToString(), x0 + tableW * cols[0] + 4, ty, textScale, placeCol * a);
            string name = p.Username + (me ? " (you)" : "") + (p.Disconnected ? " [left]" : "")
                          + (stillPlaying.Contains(p.PlayerId) ? " [playing]" : "");
            float av = rowH - 10;
            Avatar(sb, font, p, x0 + tableW * cols[1], y + 5, av, a);
            Text(sb, font, Fit(font, name, tableW * (cols[2] - cols[1]) - 18 - av, textScale), x0 + tableW * cols[1] + av + 8, ty, textScale, Color.Lerp(c, Color.White, 0.4f) * a);
            string side = _mode == Protocol.ModeTeams ? (p.Team == 0 ? "Player" : "Enemy") : RoomClient.SideName(p.Side);
            Text(sb, font, side, x0 + tableW * cols[2], ty, textScale, new Color(185, 190, 212) * a);
            Text(sb, font, p.Score.ToString("N0"), x0 + tableW * cols[3], ty, textScale, Color.White * a);
            Text(sb, font, $"{p.Accuracy:F2}%", x0 + tableW * cols[4], ty, textScale, (PerfectAccuracy(p) ? Perfect : Color.White) * a);
            Text(sb, font, p.Misses.ToString(), x0 + tableW * cols[5], ty, textScale, (NoMisses(p) ? Perfect : Color.White) * a);
            y += rowH + 6;
        }
    }

    /// <summary>The player's Discord picture (protocol 5), or a disc in their colour with their initial.</summary>
    private static void Avatar(SpriteBatch sb, SpriteFont font, PlayerResult p, float x, float y, float size, float a)
    {
        if (size < 8) return;
        Multiplayer.AvatarCache.Instance.Draw(sb, sb.GraphicsDevice, p.Avatar, new Rectangle((int)x, (int)y, (int)size, (int)size),
            Multiplayer.MultiplayerSettings.ColorFromHue(p.ColorHue), p.Username, (t, center, color, px) =>
            {
                float scale = px / Math.Max(1, font.LineSpacing);
                Vector2 sz = font.MeasureUi(UI.ErrorOverlay.Safe(t)) * scale;
                Text(sb, font, t, center.X - sz.X / 2f, center.Y - sz.Y / 2f, scale, color);
            }, a);
    }

    /// <summary>Played at least one note (a perfect stat has to be earned, not given for not playing).</summary>
    public static bool HitSomething(PlayerResult p) => p.Sicks + p.Goods + p.Bads + p.Shits > 0;

    /// <summary>100 % accuracy (as shown, 2 decimals) on a run that actually hit notes: shown in green.</summary>
    public static bool PerfectAccuracy(PlayerResult p) => HitSomething(p) && p.Accuracy >= 99.995f;

    /// <summary>0 misses on a run that actually hit notes: shown in green.</summary>
    public static bool NoMisses(PlayerResult p) => HitSomething(p) && p.Misses == 0;

    private static readonly Color Perfect = new(100, 255, 100);

    private static string Fit(SpriteFont font, string text, float maxW, float scale)
    {
        text = UI.ErrorOverlay.Safe(text);
        if (font.MeasureUi(text).X * scale <= maxW) return text;
        while (text.Length > 1 && font.MeasureUi(text + "...").X * scale > maxW) text = text[..^1];
        return text + "...";
    }

    private static void Text(SpriteBatch sb, SpriteFont font, string text, float x, float y, float scale, Color color)
    {
        text = UI.ErrorOverlay.Safe(text);
        sb.DrawUiString(font, text, new Vector2(x + 2, y + 2), Color.Black * (color.A / 255f * 0.7f), 0f, Vector2.Zero, scale, SpriteEffects.None, 0f);
        sb.DrawUiString(font, text, new Vector2(x, y), color, 0f, Vector2.Zero, scale, SpriteEffects.None, 0f);
    }

    private static void DrawCentered(SpriteBatch sb, SpriteFont font, string text, float y, float scale, Color color, int sw)
    {
        text = UI.ErrorOverlay.Safe(text);
        float w = font.MeasureUi(text).X * scale;
        if (w > sw - 40) { scale *= (sw - 40) / w; w = sw - 40; }
        Text(sb, font, text, (sw - w) / 2f, y, scale, color);
    }

    private void DrawBorder(SpriteBatch sb, Rectangle r, Color c)
    {
        sb.Draw(_pixel, new Rectangle(r.X, r.Y, r.Width, 2), c);
        sb.Draw(_pixel, new Rectangle(r.X, r.Bottom - 2, r.Width, 2), c);
        sb.Draw(_pixel, new Rectangle(r.X, r.Y, 2, r.Height), c);
        sb.Draw(_pixel, new Rectangle(r.Right - 2, r.Y, 2, r.Height), c);
    }
}
