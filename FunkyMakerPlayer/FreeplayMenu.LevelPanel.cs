using System;
using System.Collections.Generic;
using FunkyMakerPlayer.UI;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;

namespace FunkyMakerPlayer;

/// <summary>
/// Fork: drawing of the right-side level panel: header (title, count, close), toolbar (search, sort,
/// favourites filter, random) and actions (import, new, refresh), the curved level list (icon, name,
/// favourite mark, best rank, star rating, "..." options button), empty / no-results states, footer,
/// and the level options menu.
/// </summary>
public partial class FreeplayMenu
{
    private static int PanelHeaderHeight(float s) => UiScale.Px(132, s, 104);

    private void ClearPanelBounds()
    {
        _panelBounds         = Rectangle.Empty;
        _levelListBounds     = Rectangle.Empty;
        _levelItemBounds.Clear();
        _levelMoreBounds.Clear();
        _importBtnBounds     = Rectangle.Empty;
        _newBtnBounds        = Rectangle.Empty;
        _refreshBtnBounds    = Rectangle.Empty;
        _closePanelBtnBounds = Rectangle.Empty;
        _footerBarBounds     = Rectangle.Empty;
        _searchBoxBounds     = Rectangle.Empty;
        _sortChipBounds      = Rectangle.Empty;
        _favChipBounds       = Rectangle.Empty;
        _randomBtnBounds     = Rectangle.Empty;
    }

    private void DrawLevelPanel(SpriteBatch sb, int sw, int sh, float s, int itemH)
    {
        if (_panelX >= 0.99f || (_font == null && _fontRenderer == null))
        {
            ClearPanelBounds();
            return;
        }

        int panelMaxWidth = GetPanelWidth(sw, sh);
        int panelLeft = (int)(sw - panelMaxWidth * (1f - _panelX));
        int panelW = sw - panelLeft;
        _panelBounds = new Rectangle(panelLeft, 0, Math.Max(0, panelW), sh);

        DrawRect(sb, panelLeft, 0, panelW, sh, new Color(22, 22, 38));
        DrawRect(sb, panelLeft, 0, UiScale.Px(3, s, 2), sh, new Color(255, 68, 136) * 0.8f);
        DrawRect(sb, panelLeft + UiScale.Px(3, s, 2), 0, 1, sh, new Color(255, 215, 0) * 0.4f);

        int headerH = PanelHeaderHeight(s);
        int footerH = UiScale.Px(50, s, 38);
        DrawPanelHeader(sb, panelLeft, panelW, headerH, s);

        int listTop = headerH + UiScale.Px(6, s);
        int listH = Math.Max(0, sh - listTop - footerH - UiScale.Px(5, s));
        if (listH > 0) _lastListHeight = Math.Max(UiScale.Px(120, s), listH);

        _levelItemBounds.Clear();
        _levelMoreBounds.Clear();

        if (_allPaths.Count == 0 && _online != null)
        {
            _levelListBounds = Rectangle.Empty;
            DrawOnlineEmpty(sb, panelLeft, panelW, listTop, listH, s);
        }
        else if (_allPaths.Count == 0)
        {
            _levelListBounds = Rectangle.Empty;
            DrawEmptyDropZone(sb, sw, sh, s);
        }
        else if (_paths.Count == 0)
        {
            _levelListBounds = Rectangle.Empty;
            DrawNoResults(sb, panelLeft, panelW, listTop, listH, s);
        }
        else
        {
            _levelListBounds = new Rectangle(panelLeft, listTop, Math.Max(0, panelW), listH);
            DrawLevelList(sb, sw, sh, s, itemH, panelLeft, panelW, listTop, listH);
        }

        // Footer (click to import)
        // Android: the footer is only a hint (not tappable): it overlapped the touch B button.
        _footerBarBounds = OperatingSystem.IsAndroid() ? Rectangle.Empty : new Rectangle(panelLeft, sh - footerH, panelW, footerH);
        DrawRect(sb, panelLeft, sh - footerH, panelW, footerH,
            _footerBarHover ? new Color(35, 30, 50) * 0.95f : new Color(20, 15, 25) * 0.9f);
        DrawRect(sb, panelLeft, sh - footerH, panelW, 2,
            _footerBarHover ? new Color(0, 200, 255) * 0.8f : new Color(255, 68, 136) * 0.4f);
        string drop = GetPanelBottomHintTextInternal();
        int dropSize = FitFontSize(drop, UiScale.FontPx(14, s), 10, panelW - UiScale.Px(16, s));
        var dsz = MeasureText(drop, dropSize);
        DrawText(sb, drop,
            new Vector2(panelLeft + panelW / 2f - dsz.X / 2f, sh - footerH / 2f - dsz.Y / 2f),
            _footerBarHover ? new Color(100, 220, 255) : new Color(255, 215, 0) * 0.65f, dropSize);
    }

    private void DrawPanelHeader(SpriteBatch sb, int panelLeft, int panelW, int headerH, float s)
    {
        DrawRect(sb, panelLeft, 0, panelW, headerH, new Color(30, 22, 42));
        DrawRect(sb, panelLeft, headerH - 2, panelW, 2, new Color(255, 68, 136) * 0.55f);

        int pad = Math.Clamp((int)(panelW * 0.032f), UiScale.Px(12, s), UiScale.Px(24, s));
        int innerW = Math.Max(180, panelW - pad * 2);

        // Row 1: title, count, close
        int closeSize = UiScale.Px(26, s, 22);
        int closeX = panelLeft + panelW - pad - closeSize;
        int rowY = UiScale.Px(10, s);
        _closePanelBtnBounds = new Rectangle(closeX, rowY, closeSize, closeSize);
        DrawRect(sb, closeX, rowY, closeSize, closeSize, _closePanelBtnHover ? new Color(255, 68, 136) * 0.45f : new Color(45, 34, 58));
        DrawRect(sb, closeX, rowY, closeSize, 1, Color.White * (_closePanelBtnHover ? 0.6f : 0.16f));
        int closeFont = UiScale.FontPx(14, s);
        var closeSz = MeasureText("X", closeFont);
        DrawText(sb, "X", new Vector2(closeX + (closeSize - closeSz.X) / 2f, rowY + (closeSize - closeSz.Y) / 2f),
            Color.White * (_closePanelBtnHover ? 1f : 0.75f), closeFont);

        bool filtered = _paths.Count != _allPaths.Count;
        string countStr = filtered
            ? $"{_paths.Count}/{_allPaths.Count} SONGS"
            : $"{_allPaths.Count} {(_allPaths.Count == 1 ? "SONG" : "SONGS")}";
        int countSize = UiScale.FontPx(13, s);
        var countSz = MeasureText(countStr, countSize);
        int pillW = (int)countSz.X + UiScale.Px(16, s);
        int pillX = closeX - pillW - UiScale.Px(8, s);
        Color countAccent = filtered ? new Color(0, 200, 255) : new Color(255, 68, 136);
        DrawRect(sb, pillX, rowY, pillW, closeSize, countAccent * 0.28f);
        DrawRect(sb, pillX, rowY, UiScale.Px(2, s, 2), closeSize, countAccent);
        DrawText(sb, countStr, new Vector2(pillX + UiScale.Px(8, s), rowY + (closeSize - countSz.Y) / 2f), Color.White * 0.9f, countSize);

        int maxTitleW = Math.Max(80, pillX - (panelLeft + pad) - UiScale.Px(10, s));
        // Fork: FNF / OSU!MANIA tabs where the "LEVELS" title was (Q / E)
        DrawTabs(sb, panelLeft + pad, rowY - UiScale.Px(2, s), closeSize + UiScale.Px(4, s), maxTitleW, s);

        // Row 2: search | sort | favourites | random
        int row2Y = UiScale.Px(48, s);
        int row2H = UiScale.Px(32, s, 26);
        int gap = UiScale.Px(6, s, 4);
        int chipFont = UiScale.FontPx(12, s);
        string sortText = "SORT: " + SortLabels[(int)_sort];
        int sortW = (int)MeasureText(sortText, chipFont).X + UiScale.Px(18, s);
        int favW = (int)MeasureText("FAVS", chipFont).X + UiScale.Px(30, s);
        int rndW = (int)MeasureText("RANDOM", chipFont).X + UiScale.Px(18, s);
        int searchW = innerW - sortW - favW - rndW - gap * 3;
        if (searchW < UiScale.Px(110, s))
        {
            // Narrow panel: short labels
            sortText = SortLabels[(int)_sort];
            sortW = (int)MeasureText(sortText, chipFont).X + UiScale.Px(14, s);
            rndW = (int)MeasureText("?", chipFont).X + UiScale.Px(18, s);
            searchW = innerW - sortW - favW - rndW - gap * 3;
        }
        int x = panelLeft + pad;
        _searchBoxBounds = new Rectangle(x, row2Y, Math.Max(40, searchW), row2H);
        DrawSearchBox(sb, _searchBoxBounds, s);
        x = _searchBoxBounds.Right + gap;
        _sortChipBounds = new Rectangle(x, row2Y, sortW, row2H);
        DrawChip(sb, _sortChipBounds, sortText, _sortChipHover, _sort != SortMode.Name, new Color(190, 140, 255), chipFont);
        x = _sortChipBounds.Right + gap;
        _favChipBounds = new Rectangle(x, row2Y, favW, row2H);
        DrawChip(sb, _favChipBounds, "FAVS", _favChipHover, _favoritesOnly, new Color(255, 205, 60), chipFont, diamond: true);
        x = _favChipBounds.Right + gap;
        _randomBtnBounds = new Rectangle(x, row2Y, rndW, row2H);
        DrawChip(sb, _randomBtnBounds, rndW > UiScale.Px(40, s) ? "RANDOM" : "?", _randomBtnHover, false, new Color(255, 120, 200), chipFont);

        // Row 3: import | new | refresh
        int row3Y = row2Y + row2H + UiScale.Px(8, s);
        int row3H = UiScale.Px(28, s, 22);
        int btnW = (innerW - gap * 2) / 3;
        int actionFont = FitFontSize($"[{Controls.Hint(HotkeyAction.Refresh)}] REFRESH", UiScale.FontPx(12, s), 9, btnW - UiScale.Px(10, s));
        _importBtnBounds  = new Rectangle(panelLeft + pad, row3Y, btnW, row3H);
        _newBtnBounds     = new Rectangle(_importBtnBounds.Right + gap, row3Y, btnW, row3H);
        _refreshBtnBounds = new Rectangle(_newBtnBounds.Right + gap, row3Y, btnW, row3H);
        DrawPanelHeaderButton(sb, _importBtnBounds, $"[{Controls.Hint(HotkeyAction.ImportLevel)}] IMPORT", _importBtnHover, new Color(0, 200, 255), actionFont);
        if (_online != null)
            DrawPanelHeaderButton(sb, _newBtnBounds, "[ESC] BACK", _newBtnHover, new Color(255, 68, 136), actionFont);
        else if (OsuTab)
            DrawPanelHeaderButton(sb, _newBtnBounds, "+ NEW (FNF only)", false, new Color(90, 90, 110), actionFont);
        else
            DrawPanelHeaderButton(sb, _newBtnBounds, $"[{Controls.Hint(HotkeyAction.NewChart)}] + NEW", _newBtnHover, new Color(46, 224, 150), actionFont);
        DrawPanelHeaderButton(sb, _refreshBtnBounds, $"[{Controls.Hint(HotkeyAction.Refresh)}] REFRESH", _refreshBtnHover, new Color(255, 215, 0), actionFont);
    }

    private void DrawSearchBox(SpriteBatch sb, Rectangle r, float s)
    {
        bool active = _searchTyping;
        Color accent = new(0, 200, 255);
        DrawRect(sb, r.X, r.Y, r.Width, r.Height, active ? new Color(16, 26, 40) : _searchBoxHover ? new Color(32, 34, 52) : new Color(18, 18, 32));
        DrawRect(sb, r.X, r.Bottom - 2, r.Width, 2, accent * (active ? 1f : _searchBoxHover ? 0.6f : 0.3f));
        DrawRect(sb, r.X, r.Y, 1, r.Height, Color.White * 0.08f);

        // Magnifier: a small ring + handle
        int ic = Math.Max(8, r.Height / 3);
        int icx = r.X + UiScale.Px(9, s), icy = r.Y + (r.Height - ic) / 2 - 1;
        Color icCol = (active ? accent : Color.White * 0.45f);
        DrawRect(sb, icx, icy, ic, 2, icCol);
        DrawRect(sb, icx, icy + ic - 2, ic, 2, icCol);
        DrawRect(sb, icx, icy, 2, ic, icCol);
        DrawRect(sb, icx + ic - 2, icy, 2, ic, icCol);
        sb.Draw(_pixel, new Rectangle(icx + ic - 1, icy + ic - 1, Math.Max(3, ic / 2), 2), null, icCol, MathHelper.PiOver4, Vector2.Zero, SpriteEffects.None, 0f);

        int font = UiScale.FontPx(13, s);
        float tx = icx + ic + UiScale.Px(9, s);
        float maxW = r.Right - tx - UiScale.Px(6, s);
        bool empty = _search.Length == 0;
        string text = empty
            ? (active ? "" : $"Search  [{Controls.Hint(HotkeyAction.FreeplaySearch)}]")
            : Safe(_search);
        if (!empty)
        {
            // Show the end of a long search
            while (text.Length > 1 && MeasureText(text, font).X > maxW - 6) text = text[1..];
        }
        else text = TruncateText(text, font, maxW);
        var sz = MeasureText(text.Length > 0 ? text : "A", font);
        float ty = r.Y + (r.Height - sz.Y) / 2f;
        if (text.Length > 0)
            DrawText(sb, text, new Vector2(tx, ty), empty ? Color.White * 0.35f : Color.White, font);
        if (active && (int)(_time * 2.2f) % 2 == 0)
        {
            float cx = tx + (empty ? 0 : MeasureText(text, font).X) + 1;
            DrawRect(sb, (int)cx, (int)ty + 2, Math.Max(1, UiScale.Px(2, s, 1)), (int)sz.Y - 4, accent);
        }
    }

    private void DrawChip(SpriteBatch sb, Rectangle r, string label, bool hovered, bool on, Color accent, int font, bool diamond = false)
    {
        Color bg = on ? Color.Lerp(new Color(26, 26, 44), accent, 0.30f) : hovered ? new Color(36, 36, 58) : new Color(26, 26, 44);
        DrawRect(sb, r.X, r.Y, r.Width, r.Height, bg);
        DrawRect(sb, r.X, r.Bottom - 2, r.Width, 2, accent * (on ? 1f : hovered ? 0.7f : 0.3f));
        var sz = MeasureText(label, font);
        float lx = r.X + (r.Width - sz.X) / 2f;
        if (diamond)
        {
            int d = Math.Max(6, (int)(sz.Y * 0.45f));
            lx += d / 2f + 2;
            DrawDiamond(sb, new Vector2(lx - d - 3, r.Center.Y), d, on ? accent : accent * 0.55f);
        }
        DrawText(sb, label, new Vector2(lx, r.Y + (r.Height - sz.Y) / 2f), on || hovered ? Color.White : accent * 0.9f, font);
    }

    /// <summary>Filled diamond (rotated square) centred on <paramref name="centre"/>.</summary>
    private void DrawDiamond(SpriteBatch sb, Vector2 centre, int size, Color color)
    {
        float side = size / 1.4142f;
        sb.Draw(_pixel, centre, null, color, MathHelper.PiOver4, new Vector2(0.5f, 0.5f), side, SpriteEffects.None, 0f);
    }

    private void DrawNoResults(SpriteBatch sb, int panelLeft, int panelW, int top, int height, float s)
    {
        string main = _favoritesOnly && _search.Length == 0 ? "NO FAVORITES YET" : "NO LEVELS FOUND";
        string sub = _search.Length > 0
            ? $"Nothing matches \"{Safe(_search)}\""
            : $"Press [{Controls.Hint(HotkeyAction.FreeplayFavorite)}] on a level to mark it";
        string help = _search.Length > 0 ? "Esc clears the search" : $"[{Controls.Hint(HotkeyAction.FreeplayFavoritesOnly)}] shows every level";
        int mainSize = FitFontSize(main, UiScale.FontPx(24, s), 14, panelW - UiScale.Px(32, s));
        int subSize = FitFontSize(sub, UiScale.FontPx(15, s), 10, panelW - UiScale.Px(32, s));
        int helpSize = UiScale.FontPx(13, s);
        var m = MeasureText(main, mainSize);
        var su = MeasureText(sub, subSize);
        var h = MeasureText(help, helpSize);
        float y = top + height * 0.38f - (m.Y + su.Y + h.Y) / 2f;
        float cx = panelLeft + panelW / 2f;
        DrawText(sb, main, new Vector2(cx - m.X / 2f, y), new Color(255, 215, 0) * 0.85f, mainSize);
        y += m.Y + UiScale.Px(10, s);
        DrawText(sb, sub, new Vector2(cx - su.X / 2f, y), Color.White * 0.75f, subSize);
        y += su.Y + UiScale.Px(8, s);
        DrawText(sb, help, new Vector2(cx - h.X / 2f, y), Color.White * 0.4f, helpSize);
    }

    private void DrawLevelList(SpriteBatch sb, int sw, int sh, float s, int itemH, int panelLeft, int panelW, int listTop, int listH)
    {
        Rectangle scissorRect = Rectangle.Intersect(_levelListBounds, new Rectangle(0, 0, sw, sh));
        if (_graphicsDevice != null)
            scissorRect = Rectangle.Intersect(scissorRect, _graphicsDevice.Viewport.Bounds);
        if (scissorRect.Width <= 0 || scissorRect.Height <= 0) return;

        Rectangle oldScissor = _graphicsDevice?.ScissorRectangle ?? default;
        RasterizerState? oldRasterizer = _graphicsDevice?.RasterizerState;
        if (_graphicsDevice != null)
        {
            sb.End();
            _graphicsDevice.ScissorRectangle = scissorRect;
            _scissorRasterizer ??= new RasterizerState { CullMode = CullMode.None, ScissorTestEnable = true };
            sb.Begin(SpriteSortMode.Deferred, BlendState.AlphaBlend, SamplerState.LinearClamp, DepthStencilState.None, _scissorRasterizer);
        }

        var data = FreeplayData.Instance;
        int itemPadding = Math.Clamp((int)(panelW * 0.015f), UiScale.Px(8, s), UiScale.Px(18, s));
        int nameBaseSize = UiScale.FontPx(19, s);
        int pillH = UiScale.Px(24, s, 19);
        int pillFont = FitFontSize("8.88*", UiScale.FontPx(12, s), 9, UiScale.Px(60, s));
        int moreW = UiScale.Px(30, s, 24);
        int curveStep = UiScale.Px(7, s, 4);
        bool confirmBlink = _confirming && (int)(_confirmTimer * 18f) % 2 == 0;
        float confirmFade = _confirming ? Math.Clamp(_confirmTimer / ConfirmDuration, 0f, 1f) : 0f;
        int firstVisible = Math.Max(0, (int)(_scrollY / itemH));

        for (int i = 0; i < _paths.Count; i++)
        {
            float itemY = listTop + RowTop(i, itemH) - _scrollY;
            if (itemY + itemH < listTop || itemY - GroupHeaderH(itemH) > listTop + listH) continue;

            bool sel = _inPanel && i == _selected;
            bool hovered = _inPanel && i == _hoveredLevelIndex;
            string path = _paths[i];

            // Curved list: rows move right and fade with distance from the (smoothed) selection.
            float dist = Math.Abs(i - _selVisual);
            int curveX = (int)(Math.Min(dist, 6f) * curveStep);
            float alpha = sel ? 1f : Math.Max(0.5f, 1f - Math.Min(dist, 8f) * 0.065f);
            if (hovered && !sel) alpha = Math.Max(alpha, 0.9f);

            // Entry animation: rows slide in one after another when the panel opens.
            float entry = Math.Clamp((_panelAge - (i - firstVisible) * 0.025f) / 0.22f, 0f, 1f);
            float ease = 1f - (1f - entry) * (1f - entry) * (1f - entry);
            curveX += (int)((1f - ease) * panelW * 0.35f);
            alpha *= ease;
            if (_confirming && !sel) alpha *= 1f - confirmFade;
            if (alpha <= 0.01f) continue;

            // Online: section header above the first row of each section (shared / yours / a player's).
            if (StartsGroup(i))
                DrawGroupHeader(sb, i, panelLeft + itemPadding + (int)((1f - ease) * panelW * 0.35f), panelW - itemPadding * 2 - UiScale.Px(12, s),
                    (int)itemY + UiScale.Px(2, s), itemH, s, ease * (_confirming ? 1f - confirmFade : 1f));

            int rowX = panelLeft + itemPadding + curveX;
            int rowW = Math.Max(1, panelW - itemPadding * 2 - curveX - UiScale.Px(12, s));
            int rowY = (int)itemY + UiScale.Px(2, s);
            int rowH = itemH - UiScale.Px(5, s);

            // Hit area spans the whole width (stable while the curve animates).
            Rectangle hit = Rectangle.Intersect(new Rectangle(panelLeft + itemPadding, rowY, panelW - itemPadding * 2, rowH), _levelListBounds);
            if (hit.Width > 0 && hit.Height > 0) _levelItemBounds.Add((i, hit));

            double? stars = StarsOf(path);
            Color accent = stars is double st ? StarAccent(st) : new Color(255, 68, 136);

            // osu! songs: the beatmap background as the row's banner (like osu!'s song select), darkened
            // from the left so the title stays readable; it replaces the square icon.
            Texture2D? banner = OsuTab && OnDevice(path) ? GetBannerTexture(path) : null;
            if (banner != null)
            {
                var r = new Rectangle(rowX, rowY, rowW, rowH);
                float bannerAlpha = sel ? 0.95f : hovered ? 0.85f : 0.70f;
                DrawCover(sb, banner, r, Color.White * (bannerAlpha * alpha));
                DrawShade(sb, r, sel ? 0.70f : 0.75f, sel ? 0.10f : 0.25f, alpha);
                if (sel)
                {
                    DrawRect(sb, rowX, rowY, rowW, 2, new Color(255, 215, 0) * (0.8f * alpha));
                    DrawRect(sb, rowX, rowY + rowH - 2, rowW, 2, accent * (0.8f * alpha));
                    DrawRect(sb, rowX, rowY, UiScale.Px(4, s, 3), rowH, new Color(255, 215, 0) * alpha);
                    if (confirmBlink) DrawRect(sb, rowX, rowY, rowW, rowH, Color.White * 0.35f);
                }
                else
                {
                    DrawRect(sb, rowX, rowY, UiScale.Px(hovered ? 3 : 2, s, 2), rowH, (hovered ? new Color(255, 68, 136) : accent) * (0.7f * alpha));
                }
            }
            else if (sel)
            {
                DrawRect(sb, rowX, rowY, rowW, rowH, Color.Lerp(new Color(40, 26, 56), accent, 0.22f) * alpha);
                DrawRect(sb, rowX, rowY, rowW, 1, new Color(255, 215, 0) * (0.7f * alpha));
                DrawRect(sb, rowX, rowY + rowH - 1, rowW, 1, accent * (0.6f * alpha));
                DrawRect(sb, rowX, rowY, UiScale.Px(4, s, 3), rowH, new Color(255, 215, 0) * alpha);
                if (confirmBlink) DrawRect(sb, rowX, rowY, rowW, rowH, Color.White * 0.35f);
            }
            else if (hovered)
            {
                DrawRect(sb, rowX, rowY, rowW, rowH, new Color(55, 40, 72) * (0.72f * alpha));
                DrawRect(sb, rowX, rowY, rowW, 1, new Color(255, 68, 136) * (0.45f * alpha));
                DrawRect(sb, rowX, rowY, UiScale.Px(3, s, 2), rowH, new Color(255, 68, 136) * (0.8f * alpha));
            }
            else
            {
                Color bg = (i % 2 == 0) ? new Color(28, 28, 45) : new Color(24, 24, 40);
                DrawRect(sb, rowX + 2, rowY + 1, rowW - 4, rowH - 2, bg * (0.6f * alpha));
                DrawRect(sb, rowX + 2, rowY + 1, UiScale.Px(2, s, 2), rowH - 2, accent * (0.45f * alpha));
            }

            // Icon
            int iconSize = rowH - UiScale.Px(6, s);
            int iconX = rowX + UiScale.Px(sel ? 10 : 8, s);
            var iconBox = new Rectangle(iconX, rowY + (rowH - iconSize) / 2, iconSize, iconSize);
            float iconBump = sel ? 1f + BeatBump() * 0.12f : 1f;
            bool enemySide = ViewSide(GetInfo(path)) == 1;
            if (banner == null)
                DrawLevelIcon(sb, path, _names[i], iconBox, alpha, iconBump);
            else
                iconBox = new Rectangle(iconX + UiScale.Px(4, s), iconBox.Y, 0, iconBox.Height); // no icon: the banner is the art

            // Right side, from the edge inwards: "..." (selected / hovered), star pill, rank badge
            int right = rowX + rowW - UiScale.Px(6, s);
            int pillY = rowY + (rowH - pillH) / 2;
            if (sel || hovered)
            {
                var more = new Rectangle(right - moreW, pillY, moreW, pillH);
                _ui.MoreButton(sb, more, _hoveredMoreIndex == i, alpha, s);
                Rectangle moreHit = Rectangle.Intersect(more, _levelListBounds);
                if (moreHit.Width > 0 && moreHit.Height > 0) _levelMoreBounds.Add((i, moreHit));
                right = more.X - UiScale.Px(6, s);
            }

            if (stars is double stv)
            {
                string starText = stv.ToString("0.00", System.Globalization.CultureInfo.InvariantCulture) + "*";
                var starSz = MeasureText(starText, pillFont);
                int starW = (int)starSz.X + UiScale.Px(12, s);
                var starRect = new Rectangle(right - starW, pillY, starW, pillH);
                DrawRect(sb, starRect.X, starRect.Y, starRect.Width, starRect.Height, StarColor(stv) * ((sel ? 1f : 0.85f) * alpha));
                DrawText(sb, starText, new Vector2(starRect.X + (starW - starSz.X) / 2f, starRect.Y + (pillH - starSz.Y) / 2f),
                    StarTextColor(stv) * alpha, pillFont);
                right = starRect.X - UiScale.Px(6, s);
            }

            // Online song select: OFFICIAL tag, or the squares of the players who have it.
            if (_online != null) right = DrawOnlineRowTag(sb, path, right, rowY, rowH, pillH, pillFont, s, alpha);

            var rec = data.Get(path);
            var best = rec?.Best(enemySide);
            if (best != null)
            {
                string rank = best.Rank;
                var rSz = MeasureText(rank, pillFont);
                int rW = Math.Max(pillH, (int)rSz.X + UiScale.Px(10, s));
                var rRect = new Rectangle(right - rW, pillY, rW, pillH);
                Color rc = RankColor(rank);
                DrawRect(sb, rRect.X, rRect.Y, rRect.Width, rRect.Height, new Color(16, 16, 28) * (0.9f * alpha));
                DrawRect(sb, rRect.X, rRect.Bottom - 2, rRect.Width, 2, rc * alpha);
                DrawText(sb, rank, new Vector2(rRect.X + (rW - rSz.X) / 2f, rRect.Y + (pillH - rSz.Y) / 2f), rc * alpha, pillFont);
                right = rRect.X - UiScale.Px(6, s);
            }

            // Name (+ favourite diamond). The selected row scrolls a long name instead of cutting it.
            float nameX = iconBox.Right + UiScale.Px(10, s);
            bool fav = data.IsFavorite(path);
            int favSize = UiScale.Px(10, s, 8);
            if (fav)
            {
                float pulse = sel ? 1f + _favPulse * 0.6f : 1f;
                DrawDiamond(sb, new Vector2(nameX + favSize / 2f, rowY + rowH / 2f), (int)(favSize * pulse), new Color(255, 205, 60) * alpha);
                nameX += favSize + UiScale.Px(7, s);
            }
            int nameSize = sel ? nameBaseSize + UiScale.Px(2, s, 1) : nameBaseSize;
            float nameMaxW = Math.Max(30f, right - UiScale.Px(4, s) - nameX);
            string fullName = Safe(_names[i]);
            // osu! songs: the chosen difficulty after the title
            if (OsuSetOf(path) is { } osuSet)
                fullName += $"  [{Safe(DifficultyLabel(path, out _, out _))}]" + (osuSet.Difficulties.Count > 1 ? $" +{osuSet.Difficulties.Count - 1}" : "");
            Color nameColor = (sel ? Color.White : new Color(205, 205, 225)) * alpha;
            var fullSz = MeasureText(fullName, nameSize);
            float nameY = rowY + (rowH - fullSz.Y) / 2f;
            if (sel && fullSz.X > nameMaxW && _graphicsDevice != null)
                DrawMarquee(sb, fullName, nameSize, new Rectangle((int)nameX, rowY, (int)nameMaxW, rowH), nameY, nameColor, scissorRect);
            else
                DrawText(sb, TruncateText(fullName, nameSize, nameMaxW), new Vector2(nameX, nameY), nameColor, nameSize);
        }

        if (_graphicsDevice != null)
        {
            sb.End();
            _graphicsDevice.RasterizerState = oldRasterizer;
            _graphicsDevice.ScissorRectangle = oldScissor;
            sb.Begin(SpriteSortMode.Deferred, BlendState.AlphaBlend, SamplerState.LinearClamp, DepthStencilState.None, RasterizerState.CullNone);
        }

        // Scrollbar
        float totalH = ListContentHeight(itemH);
        if (totalH > listH)
        {
            float barH = Math.Max(UiScale.Px(32, s), listH * listH / totalH);
            float clamped = Math.Clamp(_scrollY, 0f, totalH - listH);
            float barY = listTop + (clamped / (totalH - listH)) * (listH - barH);
            int sbW = UiScale.Px(6, s, 4);
            int sbX = panelLeft + panelW - sbW - UiScale.Px(4, s, 2);
            DrawRect(sb, sbX, listTop, sbW, listH, new Color(20, 20, 30) * 0.5f);
            DrawRect(sb, sbX, (int)barY, sbW, (int)barH, new Color(255, 68, 136) * 0.6f);
        }
    }

    /// <summary>Draws a long name scrolling back and forth inside <paramref name="area"/> (clipped).</summary>
    private void DrawMarquee(SpriteBatch sb, string text, int size, Rectangle area, float y, Color color, Rectangle outerScissor)
    {
        float overflow = MeasureText(text, size).X - area.Width;
        // pause 1 s, scroll at 40 px/s, pause, scroll back
        float scrollTime = overflow / 40f;
        float cycle = 2f + scrollTime * 2f;
        float t = _time % cycle;
        float offset = t < 1f ? 0f
            : t < 1f + scrollTime ? (t - 1f) * 40f
            : t < 2f + scrollTime ? overflow
            : overflow - (t - 2f - scrollTime) * 40f;

        var clip = Rectangle.Intersect(area, outerScissor);
        if (clip.Width <= 0 || clip.Height <= 0) return;
        sb.End();
        _graphicsDevice!.ScissorRectangle = clip;
        sb.Begin(SpriteSortMode.Deferred, BlendState.AlphaBlend, SamplerState.LinearClamp, DepthStencilState.None, _scissorRasterizer);
        DrawText(sb, text, new Vector2(area.X - offset, y), color, size);
        sb.End();
        _graphicsDevice.ScissorRectangle = outerScissor;
        sb.Begin(SpriteSortMode.Deferred, BlendState.AlphaBlend, SamplerState.LinearClamp, DepthStencilState.None, _scissorRasterizer);
    }

    private static Color RankColor(string rank) => rank switch
    {
        "S+" => new Color(255, 225, 90),
        "S" => new Color(255, 200, 60),
        "A" => new Color(110, 235, 130),
        "B" => new Color(90, 200, 255),
        "C" => new Color(200, 150, 255),
        _ => new Color(255, 110, 120),
    };

    /// <summary>The "..." options menu and the modal windows, drawn above the panel.</summary>
    private void DrawContextMenu(SpriteBatch sb, int sw, int sh, float s)
    {
        _options.Draw(sb, sw, sh);
        _modWindow.Draw(sb, sw, sh);
        _attemptsWindow.Draw(sb, sw, sh);
    }

    // ── Empty state (no levels at all): drop zone ───────────────────────────
    private void DrawEmptyDropZone(SpriteBatch sb, int sw, int sh, float s)
    {
        // ── EMPTY STATE: Drop Zone Visual ─────────────────────────────
        float pulseAlpha = 0.72f + (_dropZonePulse * 0.28f);
        var dropBox = GetDropBoxBounds(sw, sh);
        int dropBoxW = dropBox.Width;
        int dropBoxH = dropBox.Height;
        int dropBoxX = dropBox.X;
        int dropBoxY = dropBox.Y;

        if (dropBoxW >= 140 && dropBoxH >= 160)
        {
            Color boxBg = _folderIconHover
                ? new Color(28, 36, 62) * 0.90f
                : new Color(26, 28, 48) * 0.72f;
            DrawRect(sb, dropBoxX, dropBoxY, dropBoxW, dropBoxH, boxBg);
            DrawRect(sb, dropBoxX, dropBoxY, dropBoxW, dropBoxH,
                new Color(0, 180, 255) * (_folderIconHover ? 0.10f : 0.04f));

            Color borderColor = (_folderIconHover ? new Color(80, 230, 255) : new Color(0, 180, 255)) * pulseAlpha;
            int borderThickness = UiScale.Px(2, s, 2);
            int dashStep = UiScale.Px(22, s, 14);
            int dashSize = UiScale.Px(12, s, 8);

            for (int i = 0; i < dropBoxW; i += dashStep)
            {
                int dashLen = Math.Min(dashSize, dropBoxW - i);
                DrawRect(sb, dropBoxX + i, dropBoxY, dashLen, borderThickness, borderColor);
                DrawRect(sb, dropBoxX + i, dropBoxY + dropBoxH - borderThickness, dashLen, borderThickness, borderColor);
            }
            for (int i = 0; i < dropBoxH; i += dashStep)
            {
                int dashLen = Math.Min(dashSize, dropBoxH - i);
                DrawRect(sb, dropBoxX, dropBoxY + i, borderThickness, dashLen, borderColor);
                DrawRect(sb, dropBoxX + dropBoxW - borderThickness, dropBoxY + i, borderThickness, dashLen, borderColor);
            }

            int innerW = Math.Max(100, dropBoxW - UiScale.Px(40, s));
            int badgeSize = Math.Clamp((int)(dropBoxH * 0.25f), UiScale.Px(64, s), UiScale.Px(140, s));
            int iconSize = Math.Clamp((int)(badgeSize * 0.58f), UiScale.Px(40, s), UiScale.Px(84, s));

            string mainText = GetEmptyStateMainTextInternal();
            int mainSize = FitFontSize(mainText, Math.Clamp((int)(dropBoxH * 0.088f), UiScale.FontPx(22, s), UiScale.FontPx(38, s)), 16, innerW);
            var mainMeasure = MeasureText(mainText, mainSize);

            string secondaryText = GetEmptyStateSecondaryTextInternal();
            int secondSize = FitFontSize(secondaryText, Math.Clamp((int)(dropBoxH * 0.054f), UiScale.FontPx(14, s), UiScale.FontPx(22, s)), 12, innerW - UiScale.Px(24, s));
            var secondMeasure = MeasureText(secondaryText, secondSize);

            string helpText = GetEmptyStateHelpTextInternal();
            int helpSize = FitFontSize(helpText, Math.Clamp((int)(dropBoxH * 0.044f), UiScale.FontPx(12, s), UiScale.FontPx(18, s)), 10, innerW);
            var helpMeasure = MeasureText(helpText, helpSize);

            int pillPadX = UiScale.Px(16, s);
            int pillPadY = UiScale.Px(7, s);
            int pillW = (int)Math.Min(innerW, secondMeasure.X + pillPadX * 2);
            int pillH = (int)MathF.Ceiling(secondMeasure.Y) + pillPadY * 2;

            int gap1 = UiScale.Px(20, s);
            int gap2 = UiScale.Px(12, s);
            int gap3 = UiScale.Px(20, s);

            float totalContentH = badgeSize + gap1 + mainMeasure.Y + gap2 + pillH + gap3 + helpMeasure.Y;
            float curY = dropBoxY + Math.Max(UiScale.Px(14, s), (dropBoxH - totalContentH) / 2f);

            int badgeX = dropBoxX + (dropBoxW - badgeSize) / 2;
            int badgeY = (int)curY;
            DrawRect(sb, badgeX, badgeY, badgeSize, badgeSize,
                new Color(0, 180, 255) * (_folderIconHover ? 0.22f : 0.11f));
            Color badgeBorder = new Color(0, 200, 255) * (_folderIconHover ? 0.85f : 0.42f);
            DrawRect(sb, badgeX, badgeY, badgeSize, 1, badgeBorder);
            DrawRect(sb, badgeX, badgeY + badgeSize - 1, badgeSize, 1, badgeBorder);
            DrawRect(sb, badgeX, badgeY, 1, badgeSize, badgeBorder);
            DrawRect(sb, badgeX + badgeSize - 1, badgeY, 1, badgeSize, badgeBorder);

            if (_folderIcon != null)
            {
                float hoverScale = _folderIconHover ? 1.08f : 1.0f;
                int scaledSize = (int)(iconSize * hoverScale);
                int iconX = badgeX + (badgeSize - scaledSize) / 2;
                int iconY = badgeY + (badgeSize - scaledSize) / 2;
                Color iconColor = (_folderIconHover ? new Color(110, 240, 255) : new Color(210, 235, 255)) * pulseAlpha;
                sb.Draw(_folderIcon, new Rectangle(iconX, iconY, scaledSize, scaledSize), iconColor);
            }
            else
            {
                string fallbackIcon = "[+]";
                int fSize = Math.Clamp(iconSize / 2, UiScale.FontPx(22, s), UiScale.FontPx(40, s));
                var fMeasure = MeasureText(fallbackIcon, fSize);
                DrawText(sb, fallbackIcon,
                    new Vector2(badgeX + (badgeSize - fMeasure.X) / 2f, badgeY + (badgeSize - fMeasure.Y) / 2f),
                    new Color(160, 230, 255) * pulseAlpha, fSize);
            }

            curY += badgeSize + gap1;

            float mainX = dropBoxX + (dropBoxW - mainMeasure.X) / 2f;
            DrawText(sb, mainText, new Vector2(mainX + 1, curY + 1), Color.Black * 0.45f, mainSize);
            DrawText(sb, mainText, new Vector2(mainX, curY),
                (_folderIconHover ? new Color(110, 235, 255) : new Color(0, 195, 255)) * pulseAlpha, mainSize);

            curY += mainMeasure.Y + gap2;

            int pillX = dropBoxX + (dropBoxW - pillW) / 2;
            int pillY = (int)curY;
            DrawRect(sb, pillX, pillY, pillW, pillH, new Color(14, 16, 30) * 0.85f);
            Color pillBorder = new Color(0, 180, 255) * 0.35f;
            DrawRect(sb, pillX, pillY, pillW, 1, pillBorder);
            DrawRect(sb, pillX, pillY + pillH - 1, pillW, 1, pillBorder);
            DrawRect(sb, pillX, pillY, 1, pillH, pillBorder);
            DrawRect(sb, pillX + pillW - 1, pillY, 1, pillH, pillBorder);
            DrawText(sb, secondaryText,
                new Vector2(pillX + (pillW - secondMeasure.X) / 2f, pillY + (pillH - secondMeasure.Y) / 2f),
                Color.White * 0.90f, secondSize);

            curY += pillH + gap3;

            float helpX = dropBoxX + (dropBoxW - helpMeasure.X) / 2f;
            DrawText(sb, helpText, new Vector2(helpX, curY),
                new Color(195, 205, 225) * (_folderIconHover ? 1.0f : 0.72f), helpSize);
        }
    }
}
