using System;
using System.Collections.Generic;
using System.Linq;
using FunkyMakerPlayer.Multiplayer.Rooms;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using Microsoft.Xna.Framework.Input;

namespace FunkyMakerPlayer.Multiplayer
{
    /// <summary>
    /// Leaderboard → RECENT MATCHES. Each card shows the mode, the real result (winner / winning team /
    /// draw) and every participant by place with their score; ENTER or a click opens the full standings
    /// (score, accuracy, combo, misses, ELO change). Old 1v1 duels (no participant list) still show as duels.
    /// </summary>
    public partial class MultiplayerMenu
    {
        /// <summary>Match whose standings are open on top of the leaderboard (null = none).</summary>
        private MatchHistoryEntry? _matchDetail;

        /// <summary>Handles the RECENT MATCHES detail popup. Returns true when it consumed the input.</summary>
        private bool UpdateMatchDetail(KeyboardState kb, MouseState mouse)
        {
            if (_matchDetail != null)
            {
                if (IsKeyPressed(kb, Keys.Escape) || IsKeyPressed(kb, Keys.Enter) || IsKeyPressed(kb, Keys.Space) || IsLeftClickReleased(mouse))
                {
                    _matchDetail = null;
                    _inputCooldown = INPUT_COOLDOWN_TIME;
                }
                return true;
            }

            if (_leaderboardTab != 1) return false;
            var matches = LeaderboardManager.Instance.GetRecentMatches(50);
            if (matches.Count == 0) return false;

            int open = -1;
            if (IsKeyPressed(kb, Keys.Enter) || IsKeyPressed(kb, Keys.Space))
                open = _leaderboardSelection;
            else if (IsLeftClickReleased(mouse))
                for (int k = 0; k < _clickableAreas.Count; k++)
                    if (_clickableAreas[k].Contains(mouse.X, mouse.Y)) { open = k + _leaderboardScrollOffset; break; }

            if (open < 0 || open >= matches.Count) return false;
            _leaderboardSelection = open;
            _matchDetail = matches[open];
            _inputCooldown = INPUT_COOLDOWN_TIME;
            return true;
        }

        private static string MatchModeName(MatchHistoryEntry m) => m.IsRoomMatch ? RoomClient.ModeName(m.Mode) : "Duel";

        private Color MatchModeColor(MatchHistoryEntry m) => m.IsRoomMatch ? ModeColor(m.Mode) : ACCENT_WARNING;

        private static bool IsTeamsMatch(MatchHistoryEntry m) => m.IsRoomMatch && m.Mode == Protocol.ModeTeams;

        private static string TeamName(int team) => team == 0 ? "Team Player" : "Team Enemy";

        /// <summary>Average score of each team (index 0 = Player, 1 = Enemy), disconnected players excluded.</summary>
        private static long[] TeamAverages(MatchHistoryEntry m)
        {
            var avg = new long[2];
            for (int t = 0; t < 2; t++)
            {
                var scores = m.Players.Where(p => p.Team == t && !p.Disconnected).Select(p => p.Score).ToList();
                avg[t] = scores.Count > 0 ? (long)Math.Round(scores.Average()) : 0;
            }
            return avg;
        }

        /// <summary>"X won" / "Team Player won" / "Draw".</summary>
        private static string MatchResultText(MatchHistoryEntry m, List<PlayerMatchInfo> ps)
        {
            if (IsTeamsMatch(m))
                return m.WinningTeam is 0 or 1 ? $"{TeamName(m.WinningTeam)} won" : "Draw";
            var firsts = ps.Where(p => p.Place == 1 && !p.Disconnected).ToList();
            if (!m.IsRoomMatch && m.Winner == 0) return "Draw";
            return firsts.Count == 1 ? $"{DisplayName(firsts[0])} won" : "Draw";
        }

        private static string DisplayName(PlayerMatchInfo p) => string.IsNullOrWhiteSpace(p.Username) ? "Player" : p.Username;

        private static bool IsLocalPlayer(PlayerMatchInfo p)
        {
            var s = MultiplayerSettings.Instance;
            if (!string.IsNullOrWhiteSpace(p.PlayerId) && p.PlayerId == s.OnlinePlayerId) return true;
            return string.IsNullOrWhiteSpace(p.PlayerId) && !string.IsNullOrWhiteSpace(s.Username)
                && string.Equals(p.Username, s.Username, StringComparison.OrdinalIgnoreCase);
        }

        private static string EloText(int change) => change > 0 ? $"+{change}" : change < 0 ? change.ToString() : "±0";

        private void DrawRecentMatchesTab(SpriteBatch sb, SpriteFont font, Texture2D pixel, float uiScale, int panelX, int panelY, int panelW, int panelH)
        {
            var recentMatches = LeaderboardManager.Instance.GetRecentMatches(50);

            if (recentMatches.Count == 0)
            {
                DrawCenteredTextInPanel(sb, font, "No match history yet", panelX, panelY + panelH / 2 - Px(20, uiScale), panelW, TEXT_SECONDARY, FontPx(19, uiScale, 14, 28));
                DrawCenteredTextInPanel(sb, font, "Play your first multiplayer match!", panelX, panelY + panelH / 2 + Px(12, uiScale), panelW, TEXT_DISABLED, FontPx(14, uiScale, 11, 20));
                return;
            }

            // Card height comes from its two text lines (fork, 2026-11-28): it used to be 1/7 of the
            // panel, so on a phone (bigger text, shorter panel) the two lines overlapped. As many cards
            // as fit are shown and the rest scroll.
            int levelFont = FontPx(15, uiScale, 12, 30);
            int timeFont = FontPx(12, uiScale, 10, 24);
            int rowFont = FontPx(13, uiScale, 10, 26);
            int badgeFont = FontPx(11, uiScale, 9, 22);
            int pillH = Math.Max(Px(20, uiScale, 16), (int)MeasureText(font, "Ag", badgeFont).Y + Px(6, uiScale, 4));
            int line1H = Math.Max(pillH, (int)MeasureText(font, "Ag", levelFont).Y);
            int line2H = Math.Max(pillH, (int)MeasureText(font, "Ag", rowFont).Y);
            int padV = Px(8, uiScale, 5);
            int lineGap = Px(6, uiScale, 4);
            int cardH = padV * 2 + line1H + lineGap + line2H;

            int padX = Px(22, uiScale, 12);
            int listTop = panelY + Px(14, uiScale);
            int availH = Math.Max(cardH, panelY + panelH - Px(14, uiScale) - listTop);
            int cardGap = Px(8, uiScale, 5);
            _leaderboardVisible = Math.Max(1, (availH + cardGap) / (cardH + cardGap));
            bool hasScroll = recentMatches.Count > _leaderboardVisible;
            int cardW = panelW - padX * 2 - (hasScroll ? Px(18, uiScale, 12) : 0);
            _leaderboardScrollOffset = Math.Clamp(_leaderboardScrollOffset, 0, Math.Max(0, recentMatches.Count - _leaderboardVisible));
            AdjustLeaderboardScroll();

            int startIdx = _leaderboardScrollOffset;
            int endIdx = Math.Min(startIdx + _leaderboardVisible, recentMatches.Count);

            for (int i = startIdx; i < endIdx; i++)
            {
                var match = recentMatches[i];
                var ps = match.Participants;
                bool selected = i == _leaderboardSelection;
                int currentY = listTop + (i - startIdx) * (cardH + cardGap);

                var cardRect = new Rectangle(panelX + padX, currentY, cardW, cardH);
                _clickableAreas.Add(cardRect);

                Color modeColor = MatchModeColor(match);
                var me = ps.FirstOrDefault(IsLocalPlayer);
                DrawPanel(sb, pixel, cardRect.X, cardRect.Y, cardRect.Width, cardRect.Height,
                    selected ? BG_LIGHT : BG_DARK, selected ? ACCENT_PRIMARY : BORDER_SUBTLE, selected ? 2 : 1,
                    selected ? ACCENT_PRIMARY : modeColor * 0.8f);

                int infoX = cardRect.X + Px(16, uiScale);
                int right = cardRect.Right - Px(16, uiScale);

                // ── Line 1: [MODE · N players]  Level name ............ time ago
                string modeBadge = ps.Count > 2 ? $"{MatchModeName(match).ToUpperInvariant()} · {ps.Count}P" : MatchModeName(match).ToUpperInvariant();
                int modeW = (int)MathF.Ceiling(MeasureText(font, modeBadge, badgeFont).X) + Px(16, uiScale);
                int line1Y = currentY + padV;
                DrawPillBadge(sb, font, pixel, infoX, line1Y + (line1H - pillH) / 2, pillH, modeBadge, modeColor, badgeFont, Px(8, uiScale), filledStrong: false);

                Vector2 timeSize = MeasureText(font, match.TimeAgo, timeFont);
                DrawText(sb, font, match.TimeAgo, new Vector2(right - timeSize.X, line1Y + (line1H - timeSize.Y) / 2f), TEXT_DISABLED * _fadeAlpha, timeFont);

                int levelX = infoX + modeW + Px(10, uiScale);
                string levelName = TruncateToWidth(font, string.IsNullOrWhiteSpace(match.LevelName) ? "Match" : match.LevelName,
                    Math.Max(40f, right - timeSize.X - Px(14, uiScale) - levelX), levelFont);
                float levelY = line1Y + (line1H - MeasureText(font, "Ag", levelFont).Y) / 2f;
                DrawText(sb, font, levelName, new Vector2(levelX, levelY), ACCENT_PRIMARY * _fadeAlpha, levelFont, shadow: selected);

                // ── Line 2: result pill (right) + participants by place with scores
                int line2Top = line1Y + line1H + lineGap;
                int line2Y = line2Top + (line2H - (int)MeasureText(font, "Ag", rowFont).Y) / 2;
                string result = MatchResultText(match, ps);
                string resultBadge = me != null ? $"YOU #{(me.Disconnected ? "-" : me.Place.ToString())}" : result.ToUpperInvariant();
                Color resultColor = me != null
                    ? (me.Place == 1 && !me.Disconnected ? ACCENT_SUCCESS : TEXT_SECONDARY)
                    : (result == "Draw" ? ACCENT_WARNING : ACCENT_SUCCESS);
                int resW = (int)MathF.Ceiling(MeasureText(font, resultBadge, badgeFont).X) + Px(16, uiScale);
                int resX = right - resW;
                DrawPillBadge(sb, font, pixel, resX, line2Top + (line2H - pillH) / 2, pillH, resultBadge, resultColor, badgeFont, Px(8, uiScale), filledStrong: true);

                var segments = new List<(string Text, Color Color, bool IsPlayer)>();
                if (me != null)
                    segments.Add(($"{result}   ", result == "Draw" ? ACCENT_WARNING : modeColor, false));
                if (IsTeamsMatch(match))
                {
                    var avg = TeamAverages(match);
                    for (int t = 0; t < 2; t++)
                    {
                        if (t == 1) segments.Add(("   vs   ", TEXT_DISABLED, false));
                        Color tc = match.WinningTeam == t ? ACCENT_SUCCESS : TEXT_SECONDARY;
                        segments.Add(($"{(t == 0 ? "PLAYER" : "ENEMY")} {avg[t]:N0}: ", tc, false));
                        var members = ps.Where(p => p.Team == t).ToList();
                        for (int k = 0; k < members.Count; k++)
                            segments.Add(((k > 0 ? ", " : "") + DisplayName(members[k]), PlayerTextColor(members[k]), true));
                    }
                }
                else if (!match.IsRoomMatch && ps.Count == 2)
                {
                    // Old 1v1 duel: "A (score)  VS  B (score)" like before, winner first.
                    segments.Add(($"{DisplayName(ps[0])} ({ps[0].Score:N0})", PlaceColor(ps[0], match), true));
                    segments.Add(("  VS  ", TEXT_DISABLED, false));
                    segments.Add(($"{DisplayName(ps[1])} ({ps[1].Score:N0})", PlaceColor(ps[1], match), true));
                }
                else
                {
                    for (int k = 0; k < ps.Count; k++)
                    {
                        var p = ps[k];
                        string place = p.Disconnected ? "-" : p.Place.ToString();
                        segments.Add(($"{(k > 0 ? "    " : "")}{place}. {DisplayName(p)} {p.Score:N0}", PlaceColor(p, match), true));
                    }
                }
                DrawSegmentsFit(sb, font, segments, infoX, line2Y, resX - Px(14, uiScale) - infoX, rowFont);
            }

            if (hasScroll)
            {
                DrawScrollBar(sb, pixel, panelX + padX + cardW + Px(10, uiScale), listTop, availH, recentMatches.Count, _leaderboardVisible, _leaderboardScrollOffset, ACCENT_PRIMARY);
            }
        }

        private Color PlayerTextColor(PlayerMatchInfo p) =>
            p.Disconnected ? TEXT_DISABLED : Color.Lerp(GetColorFromHue(p.ColorHue), Color.White, 0.45f);

        private Color PlaceColor(PlayerMatchInfo p, MatchHistoryEntry m)
        {
            if (p.Disconnected) return TEXT_DISABLED;
            bool soleWinner = p.Place == 1 && m.Participants.Count(x => x.Place == 1 && !x.Disconnected) == 1;
            return soleWinner ? ACCENT_SUCCESS : PlayerTextColor(p);
        }

        /// <summary>
        /// Draws coloured text pieces left to right. When they don't all fit, it stops before the first
        /// piece that doesn't and writes "+N more" (N = player pieces not drawn).
        /// </summary>
        private void DrawSegmentsFit(SpriteBatch sb, SpriteFont font, List<(string Text, Color Color, bool IsPlayer)> segments, float x, float y, float maxW, int size)
        {
            float end = x + Math.Max(40f, maxW);
            var widths = segments.Select(s => MeasureText(font, s.Text, size).X).ToList();
            for (int i = 0; i < segments.Count; i++)
            {
                bool restFits = x + widths.Skip(i).Sum() <= end;
                int playersAfter = segments.Skip(i + 1).Count(s => s.IsPlayer);
                float reserve = restFits || playersAfter == 0 ? 0f : MeasureText(font, $"  +{playersAfter} more", size).X;
                if (restFits || x + widths[i] + reserve <= end)
                {
                    DrawText(sb, font, segments[i].Text, new Vector2(x, y), segments[i].Color * _fadeAlpha, size);
                    x += widths[i];
                    continue;
                }
                if (i == 0)
                {
                    // Nothing drawn yet: show the start of the first piece rather than an empty line.
                    string cut = TruncateToWidth(font, segments[0].Text, Math.Max(0f, end - x - reserve), size);
                    DrawText(sb, font, cut, new Vector2(x, y), segments[0].Color * _fadeAlpha, size);
                    x += MeasureText(font, cut, size).X;
                    if (playersAfter == 0) return;
                    DrawText(sb, font, $"  +{playersAfter} more", new Vector2(x, y), TEXT_DISABLED * _fadeAlpha, size);
                    return;
                }
                int notDrawn = segments.Skip(i).Count(s => s.IsPlayer);
                string rest = notDrawn > 0 ? $"  +{notDrawn} more" : "…";
                DrawText(sb, font, rest, new Vector2(x, y), TEXT_DISABLED * _fadeAlpha, size);
                return;
            }
        }

        /// <summary>Full standings of a recorded match, drawn over the leaderboard.</summary>
        private void DrawMatchDetail(SpriteBatch sb, SpriteFont font, Texture2D pixel, float uiScale, int sw, int sh, MatchHistoryEntry m)
        {
            var ps = m.Participants;
            bool teams = IsTeamsMatch(m);
            bool room = m.IsRoomMatch;
            Color accent = MatchModeColor(m);

            sb.Draw(pixel, new Rectangle(0, 0, sw, sh), Color.Black * (0.65f * _fadeAlpha));
            int w = Math.Min(Px(900, uiScale), sw - 32);
            int rowH = Px(36, uiScale, 26);
            int headerH = Px(96, uiScale, 72);
            int h = Math.Min(sh - 40, headerH + Px(24, uiScale) + ps.Count * (rowH + 4) + Px(50, uiScale));
            int x = (sw - w) / 2, y = (sh - h) / 2;
            DrawPanel(sb, pixel, x, y, w, h, BG_MEDIUM, accent, 2, accent);
            int pad = Px(18, uiScale, 10);

            int titleF = FontPx(22, uiScale, 15, 30);
            string level = string.IsNullOrWhiteSpace(m.LevelName) ? "Match" : m.LevelName;
            DrawCenteredTextInPanel(sb, font, TruncateToWidth(font, level, w - pad * 2, titleF), x, y + pad, w, TEXT_PRIMARY, titleF, shadow: true);

            var stamp = DateTimeOffset.FromUnixTimeSeconds(m.Timestamp).ToLocalTime();
            string sub = $"{MatchModeName(m)}   |   {ps.Count} players   |   {stamp:MMM dd, yyyy HH:mm} ({m.TimeAgo})";
            int subF = FontPx(13, uiScale, 10, 18);
            DrawCenteredTextInPanel(sb, font, TruncateToWidth(font, sub, w - pad * 2, subF), x, y + pad + Px(34, uiScale), w, TEXT_SECONDARY, subF);

            string result = MatchResultText(m, ps).ToUpperInvariant();
            if (teams)
            {
                var avg = TeamAverages(m);
                result = $"Team Player {avg[0]:N0} avg   vs   Team Enemy {avg[1]:N0} avg   |   {result}";
            }
            int resF = FontPx(15, uiScale, 11, 21);
            DrawCenteredTextInPanel(sb, font, TruncateToWidth(font, result, w - pad * 2, resF), x, y + pad + Px(56, uiScale), w,
                result.EndsWith("DRAW") ? ACCENT_WARNING : accent, resF);

            // Table
            var r = new Rectangle(x + pad, y + headerH, w - pad * 2, h - headerH - pad - Px(30, uiScale));
            int f = FontPx(14, uiScale, 11, 20);
            int hf = FontPx(11, uiScale, 9, 15);
            var heads = new List<(string Head, float Col)> { ("#", 0f), ("PLAYER", 0.06f) };
            if (room) heads.Add((teams ? "TEAM" : "SIDE", 0.36f));
            heads.Add(("SCORE", 0.47f));
            heads.Add(("ACCURACY", 0.60f));
            heads.Add(("COMBO", 0.73f));
            heads.Add(("MISSES", 0.82f));
            if (room) heads.Add(("ELO", 0.92f));
            float Col(string head) => heads.First(hd => hd.Head == head).Col;
            foreach (var (head, col) in heads)
                DrawText(sb, font, head, new Vector2(r.X + r.Width * col, r.Y), TEXT_DISABLED * _fadeAlpha, hf);

            int ry = r.Y + Px(20, uiScale);
            int shown = 0;
            foreach (var p in ps)
            {
                if (ry + rowH > r.Bottom) break;
                shown++;
                bool me = IsLocalPlayer(p);
                Color c = GetColorFromHue(p.ColorHue);
                DrawPanel(sb, pixel, r.X - 4, ry, r.Width + 8, rowH, me ? BG_LIGHT : BG_DARK, p.Place == 1 && !p.Disconnected ? ACCENT_WARNING : BORDER_SUBTLE, 1, c);
                float ty = ry + (rowH - MeasureText(font, "A", f).Y) / 2f;
                Color placeCol = p.Disconnected ? TEXT_DISABLED : p.Place switch { 1 => ACCENT_WARNING, 2 => new Color(200, 210, 225), 3 => new Color(215, 150, 90), _ => TEXT_SECONDARY };
                DrawText(sb, font, p.Disconnected || p.Place <= 0 ? "-" : p.Place.ToString(), new Vector2(r.X + r.Width * Col("#") + 6, ty), placeCol * _fadeAlpha, f, shadow: true);

                float nameEnd = room ? Col(teams ? "TEAM" : "SIDE") : Col("SCORE");
                int av = Math.Max(10, rowH - Px(10, uiScale, 6));
                int nameX = (int)(r.X + r.Width * Col("PLAYER"));
                DrawAvatar(sb, font, p.Avatar, new Rectangle(nameX, ry + (rowH - av) / 2, av, av), p.ColorHue, DisplayName(p), p.Disconnected ? 0.5f * _fadeAlpha : _fadeAlpha);
                nameX += av + Px(8, uiScale, 5);
                string name = TruncateToWidth(font, DisplayName(p) + (me ? " (you)" : "") + (p.Disconnected ? "  [left]" : ""), r.X + r.Width * nameEnd - nameX - 8, f);
                DrawText(sb, font, name, new Vector2(nameX, ty), PlayerTextColor(p) * _fadeAlpha, f);

                if (room)
                {
                    string side = teams ? (p.Team == 0 ? "Player" : p.Team == 1 ? "Enemy" : "-") : RoomClient.SideName(p.Side);
                    DrawText(sb, font, side, new Vector2(r.X + r.Width * Col(teams ? "TEAM" : "SIDE"), ty),
                        (teams && p.Team == m.WinningTeam ? ACCENT_SUCCESS : TEXT_SECONDARY) * _fadeAlpha, f);
                }
                DrawText(sb, font, p.Score.ToString("N0"), new Vector2(r.X + r.Width * Col("SCORE"), ty), TEXT_PRIMARY * _fadeAlpha, f);
                DrawText(sb, font, $"{p.Accuracy:F2}%", new Vector2(r.X + r.Width * Col("ACCURACY"), ty),
                    (p.Accuracy >= 99.995f ? ACCENT_SUCCESS : TEXT_PRIMARY) * _fadeAlpha, f);
                DrawText(sb, font, p.MaxCombo.ToString("N0"), new Vector2(r.X + r.Width * Col("COMBO"), ty), TEXT_PRIMARY * _fadeAlpha, f);
                DrawText(sb, font, p.Misses.ToString(), new Vector2(r.X + r.Width * Col("MISSES"), ty),
                    (p.Misses == 0 && !p.Disconnected ? ACCENT_SUCCESS : TEXT_PRIMARY) * _fadeAlpha, f);
                if (room)
                {
                    Color eloCol = p.Disconnected ? TEXT_DISABLED : p.EloChange > 0 ? ACCENT_SUCCESS : p.EloChange < 0 ? ACCENT_ERROR : TEXT_SECONDARY;
                    DrawText(sb, font, p.Disconnected ? "-" : EloText(p.EloChange), new Vector2(r.X + r.Width * Col("ELO"), ty), eloCol * _fadeAlpha, f);
                }
                ry += rowH + 4;
            }
            if (shown < ps.Count)
                DrawText(sb, font, $"+{ps.Count - shown} more", new Vector2(r.X, ry), TEXT_DISABLED * _fadeAlpha, hf);

            DrawCenteredTextInPanel(sb, font, FunkyMakerPlayer.UI.PointerInput.IsTouch ? "Tap anywhere to close" : "ENTER / ESC / click: close", x, y + h - pad - Px(16, uiScale), w, TEXT_DISABLED, FontPx(12, uiScale, 10, 16));
        }
    }
}
