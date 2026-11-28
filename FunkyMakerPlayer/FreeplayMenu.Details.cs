using System;
using System.Globalization;
using System.Linq;
using FunkyMakerPlayer.UI;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;

namespace FunkyMakerPlayer;

/// <summary>
/// Fork: details of the selected level, drawn on the left while the level panel is open (the main
/// menu cards fade out): icon (bumps on the preview's beat), name, BPM / length / notes, star rating,
/// note-density graph with the preview position, stats, personal best and the play modifiers.
/// </summary>
public partial class FreeplayMenu
{
    /// <summary>0..1 pulse on each beat of the playing preview (0 without a preview).</summary>
    private float BeatBump()
    {
        if (PreviewClock is not { } c) return 0f;
        double beat = c.Seconds * c.Bpm / 60.0;
        float phase = (float)(beat - Math.Floor(beat));
        float k = 1f - phase;
        return k * k * k * c.Fade;
    }

    private static string FormatTime(float seconds)
    {
        int t = Math.Max(0, (int)MathF.Round(seconds));
        return $"{t / 60}:{t % 60:00}";
    }

    private static string AgoText(DateTime utc)
    {
        var d = DateTime.UtcNow - utc;
        if (d.TotalMinutes < 1) return "just now";
        if (d.TotalHours < 1) return $"{(int)d.TotalMinutes} min ago";
        if (d.TotalDays < 1) return $"{(int)d.TotalHours} h ago";
        if (d.TotalDays < 30) return $"{(int)d.TotalDays} d ago";
        return utc.ToLocalTime().ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
    }

    private void DrawCard(SpriteBatch sb, Rectangle r, float alpha, Color accent)
    {
        DrawRect(sb, r.X, r.Y, r.Width, r.Height, new Color(26, 24, 42) * (0.88f * alpha));
        DrawRect(sb, r.X, r.Y, r.Width, 1, Color.White * (0.08f * alpha));
        DrawRect(sb, r.X, r.Y, UiScale.Px(3, UiScale.FromScreen(_lastScreenWidth, _lastScreenHeight), 2), r.Height, accent * (0.8f * alpha));
    }

    private void DrawCardLabel(SpriteBatch sb, string label, int x, int y, float alpha, float s)
        => DrawText(sb, label, new Vector2(x, y), new Color(255, 215, 0) * (0.75f * alpha), UiScale.FontPx(12, s));

    /// <summary>Draws the details of the selected level in the area left of the panel.</summary>
    private void DrawLevelDetails(SpriteBatch sb, int leftW, int sh, float s, float alpha)
    {
        _modBarBounds = Rectangle.Empty;
        _bestCardBounds = Rectangle.Empty;
        _diffPrevBounds = _diffNextBounds = Rectangle.Empty;
        if (alpha <= 0.01f || leftW < UiScale.Px(300, s, 240)) return;

        int pad = UiScale.Px(28, s, 16);
        int contentW = Math.Min(leftW - pad * 2, UiScale.Px(660, s));
        int x0 = (leftW - contentW) / 2;
        int y = UiScale.Px(22, s, 12);
        int bottomLimit = sh - UiScale.Px(46, s);
        float bump = BeatBump();

        // Header strip (online: "PICK A SONG" and whose turn / which room)
        string header = _online != null ? Safe(_online.Title) : "FREEPLAY";
        DrawText(sb, header, new Vector2(x0, y), new Color(255, 68, 136), UiScale.FontPx(16, s));
        bool filtersShown = _favoritesOnly || _search.Length > 0 || _sort != SortMode.Name;
        if (_online != null && !filtersShown)
        {
            int ff = UiScale.FontPx(12, s);
            float hx = x0 + MeasureText(header, UiScale.FontPx(16, s)).X + UiScale.Px(14, s);
            string sub = TruncateText(Safe(_online.Subtitle), ff, x0 + contentW - hx);
            DrawText(sb, sub, new Vector2(x0 + contentW - MeasureText(sub, ff).X, y + UiScale.Px(3, s)), Color.White * (0.55f * alpha), ff);
        }
        if (filtersShown)
        {
            string filt = (_favoritesOnly ? "FAVORITES  " : "") + (_search.Length > 0 ? $"\"{Safe(_search)}\"  " : "") +
                          (_sort != SortMode.Name ? "BY " + SortLabels[(int)_sort] : "");
            int ff = UiScale.FontPx(12, s);
            filt = TruncateText(filt.Trim(), ff, contentW * 0.6f);
            var fsz = MeasureText(filt, ff);
            DrawText(sb, filt, new Vector2(x0 + contentW - fsz.X, y + UiScale.Px(3, s)), new Color(0, 200, 255) * (0.8f * alpha), ff);
        }
        y += UiScale.Px(30, s);

        string? path = SelectedPath;
        if (path == null)
        {
            string msg = _online != null
                ? (_allPaths.Count == 0 ? "Nothing to pick in this tab" : "No song selected")
                : _allPaths.Count == 0 ? "Import a level to start playing" : "No level selected";
            int ms = UiScale.FontPx(20, s);
            var msz = MeasureText(msg, ms);
            DrawText(sb, msg, new Vector2(x0 + (contentW - msz.X) / 2f, sh * 0.42f), Color.White * (0.5f * alpha), ms);
            var bar = new Rectangle(x0, Math.Max(y, bottomLimit - ModifierBarHeight(s)), contentW, ModifierBarHeight(s));
            if (_online != null) DrawOnlineRoomCard(sb, bar, s, alpha);
            else DrawModifierBar(sb, bar, s, alpha);
            return;
        }

        // osu! songs: the background, very faint, behind the whole details side
        if (OsuSetOf(path) != null && OnDevice(path) && (GetDetailBackground(path) ?? GetBannerTexture(path)) is { } backdrop)
        {
            var area = new Rectangle(0, 0, leftW, sh);
            DrawCover(sb, backdrop, area, Color.White * (0.12f * alpha));
            DrawShade(sb, area, 0.0f, 0.35f, alpha);
        }

        var info = GetInfo(path);
        int side = ViewSide(info);
        var si = info?.Sides[side];
        double? stars = StarsOf(path);
        Color accent = stars is double st0 ? StarAccent(st0) : new Color(255, 68, 136);
        var rec = FreeplayData.Instance.Get(path);
        var best = rec?.Best(side == 1);

        // ── Hero: icon + name + meta + rating ──────────────────────────────
        var osuSet = OsuSetOf(path);
        int heroH = Math.Clamp((int)(sh * 0.17f), UiScale.Px(96, s, 80), UiScale.Px(150, s));
        if (osuSet != null) heroH += UiScale.Px(30, s); // room for the difficulty selector
        // osu! songs: the hero is a banner with the beatmap background (sharp copy when loaded,
        // the list banner meanwhile), the text drawn over it. Other levels keep the icon layout.
        Texture2D? heroBg = osuSet != null && OnDevice(path) ? (GetDetailBackground(path) ?? GetBannerTexture(path)) : null;
        int textX, textW;
        float heroTextTop = y + UiScale.Px(2, s);
        if (heroBg != null)
        {
            heroH += UiScale.Px(20, s);
            var hero = new Rectangle(x0, y, contentW, heroH);
            DrawRect(sb, hero.X - 2, hero.Y - 2, hero.Width + 4, hero.Height + 4, accent * (0.35f * alpha));
            DrawCover(sb, heroBg, hero, Color.White * ((0.88f + bump * 0.12f) * alpha));
            DrawShade(sb, hero, 0.80f, 0.20f, alpha);
            DrawRect(sb, hero.X, hero.Bottom - UiScale.Px(3, s, 2), hero.Width, UiScale.Px(3, s, 2), accent * alpha);
            textX = x0 + UiScale.Px(20, s);
            textW = contentW - UiScale.Px(40, s);
            heroTextTop = y + UiScale.Px(12, s);
        }
        else
        {
            int iconSize = heroH;
            var iconBox = new Rectangle(x0, y, iconSize, iconSize);
            DrawRect(sb, iconBox.X, iconBox.Y, iconBox.Width, iconBox.Height, accent * (0.10f * alpha));
            DrawLevelIcon(sb, path, _names[_selected], iconBox, alpha, 1f + bump * 0.08f);
            textX = iconBox.Right + UiScale.Px(18, s);
            textW = x0 + contentW - textX;
        }
        string name = Safe(_names[_selected]);
        int nameSize = FitFontSize(name, UiScale.FontPx(34, s), UiScale.FontPx(18, s), textW);
        string shownName = TruncateText(name, nameSize, textW);
        var nameSz = MeasureText(shownName, nameSize);
        float ny = heroTextTop;
        DrawText(sb, shownName, new Vector2(textX + 2, ny + 2), Color.Black * (0.4f * alpha), nameSize);
        DrawText(sb, shownName, new Vector2(textX, ny), Color.Lerp(Color.White, new Color(255, 230, 150), bump * 0.6f) * alpha, nameSize);
        ny += nameSz.Y + UiScale.Px(4, s);

        int metaSize = UiScale.FontPx(15, s);
        string meta;
        if (info == null && OnlineSongOf(path) is { OnDevice: false } remote)
            meta = remote.Official
                ? "Official level  |  not downloaded yet"
                : $"Not on this device yet  |  {MegaBytes(remote.Size)}" + (remote.Difficulty.Length > 0 && osuSet == null ? $"  |  {Safe(remote.Difficulty)}" : "");
        else if (info == null) meta = "Reading chart...";
        else
        {
            var parts = new System.Collections.Generic.List<string>();
            if (info.Bpm > 0) parts.Add($"{info.Bpm:0.#} BPM");
            if (info.LengthSec > 0) parts.Add(FormatTime(info.LengthSec / FreeplayData.Instance.SongRate));
            if (si != null) parts.Add($"{si.Taps + si.Holds} notes");
            if (side == 1) parts.Add("ENEMY CHART");
            if (osuSet != null)
            {
                var diff = osuSet.Difficulties.FirstOrDefault(d => string.Equals(d.Path, path, StringComparison.OrdinalIgnoreCase));
                parts.Add($"mapped by {Safe(diff?.Creator ?? osuSet.Creator)}");
                // What the set brings beyond the chart (each can be turned off in Settings → osu! songs)
                if (osuSet.HasVideo) parts.Add("video");
                if (osuSet.HasStoryboard) parts.Add("storyboard");
                if (osuSet.HasSkin) parts.Add(Osu.OsuSettings.PreferBeatmapSkins ? "own note skin" : "own note skin (off)");
            }
            meta = string.Join("   |   ", parts);
        }
        DrawText(sb, TruncateText(meta, metaSize, textW), new Vector2(textX, ny), Color.White * ((heroBg != null ? 0.85f : 0.6f) * alpha), metaSize);
        ny += MeasureText("A", metaSize).Y + UiScale.Px(8, s);
        if (osuSet != null)
        {
            DrawDifficultySelector(sb, path, textX, (int)ny, textW, s, alpha);
            ny += UiScale.Px(32, s);
        }
        else
        {
            _diffPrevBounds = _diffNextBounds = Rectangle.Empty;
        }

        // Rating row: star pill, rank, combo label, favourite
        int pillH = UiScale.Px(28, s, 22);
        int pillFont = UiScale.FontPx(15, s);
        int px = textX;
        if (stars is double stv)
        {
            string starText = stv.ToString("0.00", CultureInfo.InvariantCulture) + " *";
            var ssz = MeasureText(starText, pillFont);
            int w = (int)ssz.X + UiScale.Px(18, s);
            DrawRect(sb, px, (int)ny, w, pillH, StarColor(stv) * alpha);
            DrawText(sb, starText, new Vector2(px + (w - ssz.X) / 2f, ny + (pillH - ssz.Y) / 2f), StarTextColor(stv) * alpha, pillFont);
            px += w + UiScale.Px(8, s);
        }
        if (best != null)
        {
            string rank = best.Rank;
            var rsz = MeasureText(rank, pillFont);
            int w = (int)rsz.X + UiScale.Px(18, s);
            Color rc = RankColor(rank);
            DrawRect(sb, px, (int)ny, w, pillH, new Color(16, 16, 28) * (0.9f * alpha));
            DrawRect(sb, px, (int)ny + pillH - 2, w, 2, rc * alpha);
            DrawText(sb, rank, new Vector2(px + (w - rsz.X) / 2f, ny + (pillH - rsz.Y) / 2f), rc * alpha, pillFont);
            px += w + UiScale.Px(8, s);
            if (best.ComboLabel.Length > 0)
            {
                string fc = best.ComboLabel;
                var fsz = MeasureText(fc, pillFont);
                int fw = (int)fsz.X + UiScale.Px(18, s);
                DrawRect(sb, px, (int)ny, fw, pillH, new Color(255, 215, 0) * (0.22f * alpha));
                DrawText(sb, fc, new Vector2(px + (fw - fsz.X) / 2f, ny + (pillH - fsz.Y) / 2f), new Color(255, 225, 120) * alpha, pillFont);
                px += fw + UiScale.Px(8, s);
            }
        }
        if (FreeplayData.Instance.IsFavorite(path))
        {
            int d = UiScale.Px(14, s, 10);
            DrawDiamond(sb, new Vector2(px + d / 2f + 2, ny + pillH / 2f), (int)(d * (1f + _favPulse * 0.5f)), new Color(255, 205, 60) * alpha);
        }
        y += heroH + UiScale.Px(16, s);

        // ── Remaining cards, dropped from the bottom up when the window is short ──
        int modsH = ModifierBarHeight(s);
        int bestH = UiScale.Px(84, s, 68);
        if (_online != null) bestH += OnlineAvailabilityExtra(path, s); // room for who has it (pictures)
        int statsH = UiScale.Px(58, s, 48);
        int graphH = Math.Clamp((int)(sh * 0.12f), UiScale.Px(60, s, 48), UiScale.Px(110, s));
        int gap = UiScale.Px(12, s, 8);
        int avail = bottomLimit - y;
        bool showGraph = true, showStats = true;
        if (graphH + statsH + bestH + modsH + gap * 4 > avail) showStats = false;
        if (graphH + bestH + modsH + gap * 3 > avail) showGraph = false;

        if (showGraph)
        {
            DrawDensityGraph(sb, new Rectangle(x0, y, contentW, graphH), info, side, accent, s, alpha);
            y += graphH + gap;
        }
        if (showStats)
        {
            DrawStatTiles(sb, new Rectangle(x0, y, contentW, statsH), info, side, s, alpha);
            y += statsH + gap;
        }
        if (_online != null)
        {
            // Online: who has the song and what picking it does, then the room's settings.
            _bestCardBounds = Rectangle.Empty;
            DrawOnlineAvailability(sb, new Rectangle(x0, y, contentW, bestH), path, s, alpha);
            y += bestH + gap;
            DrawOnlineRoomCard(sb, new Rectangle(x0, Math.Min(y, bottomLimit - modsH), contentW, modsH), s, alpha);
            _modBarBounds = Rectangle.Empty;
            return;
        }
        DrawBestCard(sb, new Rectangle(x0, y, contentW, bestH), rec, best, side, s, alpha);
        y += bestH + gap;
        DrawModifierBar(sb, new Rectangle(x0, Math.Min(y, bottomLimit - modsH), contentW, modsH), s, alpha);
    }

    private void DrawDensityGraph(SpriteBatch sb, Rectangle r, LevelInfo? info, int side, Color accent, float s, float alpha)
    {
        DrawCard(sb, r, alpha, accent);
        int labelPad = UiScale.Px(12, s);
        DrawCardLabel(sb, "NOTE DENSITY", r.X + labelPad, r.Y + UiScale.Px(6, s), alpha, s);
        if (info?.Sides[side] is not { } si || si.Density.Length == 0)
        {
            DrawText(sb, info == null ? "..." : "No notes", new Vector2(r.X + labelPad, r.Y + r.Height / 2f), Color.White * (0.4f * alpha), UiScale.FontPx(13, s));
            return;
        }

        if (si.PeakNps > 0)
        {
            string nps = $"PEAK {si.PeakNps * FreeplayData.Instance.SongRate:0.#} NPS";
            int f = UiScale.FontPx(12, s);
            var nsz = MeasureText(nps, f);
            DrawText(sb, nps, new Vector2(r.Right - labelPad - nsz.X, r.Y + UiScale.Px(6, s)), Color.White * (0.55f * alpha), f);
        }

        var g = new Rectangle(r.X + labelPad, r.Y + UiScale.Px(24, s), r.Width - labelPad * 2, r.Height - UiScale.Px(32, s));
        if (g.Height < 8) return;
        int n = si.Density.Length;
        float bw = g.Width / (float)n;
        var other = info.Sides[1 - side];
        for (int i = 0; i < n; i++)
        {
            int bx = g.X + (int)(i * bw);
            int w = Math.Max(1, (int)((i + 1) * bw) - (int)(i * bw) - 1);
            if (other != null && other.Density.Length == n)
            {
                int oh = (int)(other.Density[i] * g.Height);
                DrawRect(sb, bx, g.Bottom - oh, w, oh, Color.White * (0.07f * alpha));
            }
            int h = Math.Max(1, (int)(si.Density[i] * g.Height));
            float hot = si.Density[i];
            DrawRect(sb, bx, g.Bottom - h, w, h, Color.Lerp(accent * 0.55f, accent, hot) * alpha);
        }
        DrawRect(sb, g.X, g.Bottom, g.Width, 1, Color.White * (0.15f * alpha));

        // Preview position
        if (PreviewPath == SelectedPath && PreviewClock is { } c && info.LengthSec > 0)
        {
            float t = (float)Math.Clamp(c.Seconds / info.LengthSec, 0, 1);
            int lx = g.X + (int)(t * g.Width);
            DrawRect(sb, lx, g.Y - 2, Math.Max(2, UiScale.Px(2, s, 1)), g.Height + 4, Color.White * (0.85f * alpha * c.Fade));
        }
    }

    private void DrawStatTiles(SpriteBatch sb, Rectangle r, LevelInfo? info, int side, float s, float alpha)
    {
        var si = info?.Sides[side];
        var other = info?.Sides[1 - side];
        string? path = SelectedPath;
        var osuSet = OsuSetOf(path);
        var osuDiff = osuSet?.Difficulties.FirstOrDefault(d => string.Equals(d.Path, path, StringComparison.OrdinalIgnoreCase));
        var tiles = osuSet != null
            ? new (string Label, string Value)[]
            {
                ("TAPS", si != null ? si.Taps.ToString() : "-"),
                ("HOLDS", si != null ? si.Holds.ToString() : "-"),
                ("OSU! OD", osuDiff != null ? osuDiff.OverallDifficulty.ToString("0.#", CultureInfo.InvariantCulture) : "-"),
                ("4K DIFFICULTIES", osuSet.Difficulties.Count.ToString()),
            }
            : new (string Label, string Value)[]
            {
                ("TAPS", si != null ? si.Taps.ToString() : "-"),
                ("HOLDS", si != null ? si.Holds.ToString() : "-"),
                (side == 1 ? "PLAYER NOTES" : "ENEMY NOTES", other != null ? (other.Taps + other.Holds).ToString() : "-"),
                ("STRUMLINES", info != null ? info.StrumlineCount.ToString() : "-"),
            };
        int gap = UiScale.Px(8, s);
        int w = (r.Width - gap * (tiles.Length - 1)) / tiles.Length;
        for (int i = 0; i < tiles.Length; i++)
        {
            var t = new Rectangle(r.X + i * (w + gap), r.Y, w, r.Height);
            DrawCard(sb, t, alpha, new Color(0, 200, 255));
            int lf = FitFontSize(tiles[i].Label, UiScale.FontPx(11, s), 8, w - UiScale.Px(16, s));
            DrawText(sb, tiles[i].Label, new Vector2(t.X + UiScale.Px(10, s), t.Y + UiScale.Px(6, s)), Color.White * (0.5f * alpha), lf);
            int vf = UiScale.FontPx(20, s);
            DrawText(sb, tiles[i].Value, new Vector2(t.X + UiScale.Px(10, s), t.Y + UiScale.Px(22, s)), Color.White * alpha, vf);
        }
    }

    private void DrawBestCard(SpriteBatch sb, Rectangle r, FreeplayLevelRecord? rec, FreeplayAttempt? best, int side, float s, float alpha)
    {
        _bestCardBounds = r;
        if (_bestCardHover) DrawRect(sb, r.X - 2, r.Y - 2, r.Width + 4, r.Height + 4, new Color(255, 215, 0) * (0.35f * alpha));
        DrawCard(sb, r, alpha, new Color(255, 215, 0));
        int lp = UiScale.Px(12, s);
        DrawCardLabel(sb, side == 1 ? "PERSONAL BEST (ENEMY)" : "PERSONAL BEST", r.X + lp, r.Y + UiScale.Px(6, s), alpha, s);

        int attempts = rec?.AttemptCount(side == 1) ?? 0;
        string info = (attempts > 0 ? $"{attempts} attempt{(attempts == 1 ? "" : "s")}" : "No attempts") +
                      (rec?.Plays > 0 ? $"  |  {rec.Plays} {(rec.Plays == 1 ? "play" : "plays")}" : "") +
                      (rec?.LastPlayedUtc is { } lp2 ? $"  |  {AgoText(lp2)}" : "");
        int pf = UiScale.FontPx(12, s);
        var psz = MeasureText(info, pf);
        DrawText(sb, info, new Vector2(r.Right - lp - psz.X, r.Y + UiScale.Px(6, s)), Color.White * (0.5f * alpha), pf);

        string open = "ALL ATTEMPTS  >";
        var osz = MeasureText(open, pf);
        DrawText(sb, open, new Vector2(r.Right - lp - osz.X, r.Bottom - UiScale.Px(8, s) - osz.Y),
            (_bestCardHover ? new Color(255, 215, 0) : Color.White * 0.45f) * alpha, pf);

        float vy = r.Y + UiScale.Px(26, s);
        if (best == null)
        {
            string none = attempts > 0 ? "No finished run at 1.00x yet" : "No score yet";
            DrawText(sb, none, new Vector2(r.X + lp, vy + UiScale.Px(4, s)), Color.White * (0.45f * alpha), UiScale.FontPx(20, s));
            return;
        }

        int bigF = UiScale.FontPx(28, s);
        string score = best.Score.ToString("N0", CultureInfo.InvariantCulture);
        var ssz = MeasureText(score, bigF);
        DrawText(sb, score, new Vector2(r.X + lp, vy), Color.White * alpha, bigF);

        int sf = UiScale.FontPx(14, s);
        string detail = $"{best.Accuracy.ToString("0.00", CultureInfo.InvariantCulture)}%   |   {best.Misses} {(best.Misses == 1 ? "miss" : "misses")}   |   {best.MaxCombo}x max combo" +
                        (best.HasReplay ? "   |   replay" : "");
        float dx = r.X + lp + ssz.X + UiScale.Px(18, s);
        detail = TruncateText(detail, sf, r.Right - lp - osz.X - UiScale.Px(12, s) - dx);
        DrawText(sb, detail, new Vector2(dx, vy + (ssz.Y - MeasureText("A", sf).Y) / 2f + 2), Color.White * (0.75f * alpha), sf);
    }

    private static int ModifierBarHeight(float s) => UiScale.Px(58, s, 48);

    /// <summary>Summary of the active modifiers; click (or M) opens the modifiers window.</summary>
    private void DrawModifierBar(SpriteBatch sb, Rectangle r, float s, float alpha)
    {
        _modBarBounds = r;
        Color acc = new(190, 140, 255);
        if (_modBarHover) DrawRect(sb, r.X - 2, r.Y - 2, r.Width + 4, r.Height + 4, acc * (0.35f * alpha));
        DrawCard(sb, r, alpha, acc);
        int lp = UiScale.Px(12, s);
        DrawCardLabel(sb, "MODIFIERS", r.X + lp, r.Y + UiScale.Px(6, s), alpha, s);

        int pf = UiScale.FontPx(12, s);
        string open = "CHANGE  >";
        var osz = MeasureText(open, pf);
        DrawText(sb, open, new Vector2(r.Right - lp - osz.X, r.Y + UiScale.Px(6, s)), (_modBarHover ? acc : Color.White * 0.45f) * alpha, pf);

        int cy = r.Y + UiScale.Px(26, s);
        int ch = r.Bottom - UiScale.Px(7, s) - cy;
        int cx = r.X + lp;
        int cf = UiScale.FontPx(12, s);
        var active = PlayModifiers.Active.ToList();
        if (active.Count == 0)
        {
            DrawText(sb, "None", new Vector2(cx, cy + (ch - MeasureText("A", cf).Y) / 2f), Color.White * (0.4f * alpha), cf);
        }
        foreach (var m in active)
        {
            string label = m.ShortLabel;
            var lsz = MeasureText(label, cf);
            int w = (int)lsz.X + UiScale.Px(14, s);
            if (cx + w > r.Right - lp) break;
            bool na = m.Unavailable?.Invoke(SelectedPath) != null;
            DrawRect(sb, cx, cy, w, ch, (na ? Color.White * 0.08f : acc * 0.25f) * alpha);
            DrawText(sb, label, new Vector2(cx + UiScale.Px(7, s), cy + (ch - lsz.Y) / 2f), (na ? Color.White * 0.4f : Color.White) * alpha, cf);
            cx += w + UiScale.Px(6, s);
        }
        if (PlayModifiers.RecordsBlocked)
        {
            string warn = "Best scores and replays aren't saved";
            int wf = UiScale.FontPx(11, s);
            var wsz = MeasureText(warn, wf);
            if (cx + wsz.X < r.Right - lp)
                DrawText(sb, warn, new Vector2(r.Right - lp - wsz.X, cy + (ch - wsz.Y) / 2f), new Color(255, 190, 90) * alpha, wf);
        }
    }
}
