using System;
using System.Collections.Generic;
using System.Linq;
using FunkyMakerPlayer.Replay;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using Microsoft.Xna.Framework.Input;

namespace FunkyMakerPlayer.UI;

/// <summary>
/// REPLAYS screen (fork, main menu): lists saved replays, newest first, with ALL / SOLO / ONLINE
/// filters. Up/Down or wheel select, Enter / click watch, Q/E or tabs filter, Esc / BACK returns.
/// Each row has a "..." options menu (<see cref="OptionsMenu"/>: click "...", right click the row or
/// the options key): WATCH, SHOW IN FOLDER (desktop), DELETE (asks first). Del opens the delete
/// confirmation directly. Game1 reads <see cref="ChosenReplay"/> and <see cref="BackPressed"/>.
/// </summary>
public sealed class ReplayBrowser : IDirectScreen
{
    private readonly Texture2D _pixel;
    private readonly SpriteFont? _font;
    private readonly FontRenderer? _fontRenderer;

    private List<ReplayFile> _all = new();
    private List<ReplayFile> _shown = new();
    private int _filter; // 0 all, 1 solo, 2 online
    private int _selected;
    private int _scroll;
    private int _visibleRows = 8;
    private readonly List<Rectangle> _rowRects = new();
    private readonly Rectangle[] _tabRects = new Rectangle[3];
    private Rectangle _backRect;
    private KeyboardState _prevKb;
    private MouseState _prevMouse;
    private Point _lastMouse = new(-9999, -9999);
    private float _cooldown = 0.2f;
    private string? _message;
    private float _messageTimer;
    private bool _messageIsError;
    private float _time;
    private readonly UiDraw _ui;
    private readonly OptionsMenu _menu;
    private readonly List<(int Index, Rectangle Rect)> _moreRects = new();
    private int _menuIndex = -1;

    private static readonly Color Bg = new(14, 14, 24);
    private static readonly Color Card = new(24, 24, 40);
    private static readonly Color CardHi = new(36, 36, 58);
    private static readonly Color Pink = new(255, 68, 136);
    private static readonly Color Gold = new(255, 215, 0);
    private static readonly Color Cyan = new(80, 205, 255);
    private static readonly Color Green = new(75, 225, 130);
    private static readonly Color Dim = new(140, 145, 170);
    private static readonly string[] Tabs = { "ALL", "SOLO", "ONLINE" };

    public bool BackPressed { get; private set; }
    /// <summary>Replay picked to watch (Game1 clears it with <see cref="ClearChoice"/>).</summary>
    public ReplayFile? ChosenReplay { get; private set; }

    public ReplayBrowser(Texture2D pixel, SpriteFont? font, FontRenderer? fontRenderer)
    {
        _pixel = pixel;
        _font = font;
        _fontRenderer = fontRenderer;
        _ui = new UiDraw(pixel, font, fontRenderer);
        _menu = new OptionsMenu(_ui);
        _prevKb = LayoutKeyboard.GetState();
        _prevMouse = Mouse.GetState();
        Reload();
    }

    public void Reload()
    {
        try { _all = ReplayStore.List(); }
        catch (Exception ex) { _all = new(); ShowMessage($"Could not read replays: {ex.Message}", true); }
        ApplyFilter();
    }

    public void ClearChoice() => ChosenReplay = null;

    public void ShowMessage(string text, bool error = false)
    {
        _message = text;
        _messageIsError = error;
        _messageTimer = 6f;
    }

    public void ResetInputCooldown()
    {
        _cooldown = 0.2f;
        BackPressed = false;
        ChosenReplay = null;
        _prevKb = LayoutKeyboard.GetState();
        _prevMouse = Mouse.GetState();
    }

    private void ApplyFilter()
    {
        _shown = _filter switch
        {
            1 => _all.Where(r => !r.IsOnline).ToList(),
            2 => _all.Where(r => r.IsOnline).ToList(),
            _ => _all.ToList(),
        };
        _selected = Math.Clamp(_selected, 0, Math.Max(0, _shown.Count - 1));
        KeepSelectionVisible();
    }

    private void KeepSelectionVisible()
    {
        if (_selected < _scroll) _scroll = _selected;
        if (_selected >= _scroll + _visibleRows) _scroll = _selected - _visibleRows + 1;
        _scroll = Math.Clamp(_scroll, 0, Math.Max(0, _shown.Count - _visibleRows));
    }

    public void Update(float dt, KeyboardState kb)
    {
        _time += dt;
        if (_messageTimer > 0f && (_messageTimer -= dt) <= 0f) _message = null;
        var ms = Mouse.GetState();
        if (!Game1.CanAcceptInput)
        {
            _prevKb = default;
            _prevMouse = Game1.NeutralMouseState(ms);
            return;
        }
        if (_cooldown > 0f)
        {
            _cooldown -= dt;
            _prevKb = kb;
            _prevMouse = ms;
            return;
        }

        bool Pressed(Keys k) => kb.IsKeyDown(k) && !_prevKb.IsKeyDown(k);
        bool clicked = ms.LeftButton == ButtonState.Released && _prevMouse.LeftButton == ButtonState.Pressed;
        bool moved = ms.Position != _lastMouse;
        _lastMouse = ms.Position;

        bool rightClicked = ms.RightButton == ButtonState.Released && _prevMouse.RightButton == ButtonState.Pressed;
        if (_menu.Update(kb, ms.Position, clicked, rightClicked, moved, out string? chosen))
        {
            if (chosen != null) RunOption(chosen);
            _prevKb = kb;
            _prevMouse = ms;
            return;
        }

        int hovered = -1;
        for (int i = 0; i < _rowRects.Count; i++)
            if (_rowRects[i].Contains(ms.Position)) { hovered = _scroll + i; break; }
        int hoveredMore = _moreRects.FindIndex(m => m.Rect.Contains(ms.Position));
        if (hovered >= 0 && moved && hovered != _selected) { _selected = hovered; UiSfx.Play(UiSound.MoveList); }

        int wheel = ms.ScrollWheelValue - _prevMouse.ScrollWheelValue;
        if (wheel != 0 && _shown.Count > 0)
        {
            _selected = Math.Clamp(_selected + (wheel > 0 ? -1 : 1), 0, _shown.Count - 1);
            KeepSelectionVisible();
        }

        if (Pressed(Keys.Escape) || (clicked && _backRect.Contains(ms.Position)))
        {
            BackPressed = true;
            UiSfx.Play(UiSound.Back);
        }
        else if (Controls.Pressed(HotkeyAction.PrevTab, kb, _prevKb) || Pressed(Keys.Left) ||
                 Controls.Pressed(HotkeyAction.NextTab, kb, _prevKb) || Pressed(Keys.Right))
        {
            int dir = Controls.Pressed(HotkeyAction.PrevTab, kb, _prevKb) || Pressed(Keys.Left) ? -1 : 1;
            _filter = (_filter + dir + Tabs.Length) % Tabs.Length;
            _selected = 0;
            _scroll = 0;
            ApplyFilter();
            UiSfx.Play(UiSound.Move);
        }
        else if (Pressed(Keys.Down) && _shown.Count > 0)
        {
            _selected = (_selected + 1) % _shown.Count;
            KeepSelectionVisible();
            UiSfx.Play(UiSound.MoveList);
        }
        else if (Pressed(Keys.Up) && _shown.Count > 0)
        {
            _selected = (_selected - 1 + _shown.Count) % _shown.Count;
            KeepSelectionVisible();
            UiSfx.Play(UiSound.MoveList);
        }
        else if (Controls.Pressed(HotkeyAction.DeleteItem, kb, _prevKb) && _selected < _shown.Count)
        {
            OpenOptions(_selected, null, kb, confirmFirst: "delete");
        }
        else if (Controls.Pressed(HotkeyAction.FreeplayOptions, kb, _prevKb) && _selected < _shown.Count)
        {
            OpenOptions(_selected, null, kb);
        }
        else if (rightClicked && hovered >= 0 && hovered < _shown.Count)
        {
            OpenOptions(hovered, ms.Position, kb);
        }
        else if ((Pressed(Keys.Enter) || Pressed(Keys.Space)) && _selected < _shown.Count)
        {
            ChosenReplay = _shown[_selected];
            UiSfx.Play(UiSound.StartSong);
        }
        else if (clicked)
        {
            for (int t = 0; t < _tabRects.Length; t++)
            {
                if (_tabRects[t].Contains(ms.Position) && t != _filter)
                {
                    _filter = t;
                    _selected = 0;
                    _scroll = 0;
                    ApplyFilter();
                    UiSfx.Play(UiSound.Move);
                }
            }
            if (hoveredMore >= 0)
            {
                OpenOptions(_moreRects[hoveredMore].Index, null, kb);
            }
            else if (hovered >= 0 && hovered < _shown.Count)
            {
                ChosenReplay = _shown[hovered];
                UiSfx.Play(UiSound.StartSong);
            }
        }

        _prevKb = kb;
        _prevMouse = ms;
    }

    // ── Options menu ("...") ────────────────────────────────────────────────

    private void OpenOptions(int index, Point? at, KeyboardState kb, string? confirmFirst = null)
    {
        if (index < 0 || index >= _shown.Count) return;
        _selected = index;
        _menuIndex = index;
        var r = _shown[index];
        var more = _moreRects.FirstOrDefault(m => m.Index == index).Rect;
        Point anchor = at ?? (more != Rectangle.Empty ? new Point(more.Right, more.Bottom) : _lastMouse);
        var items = new List<OptionsMenu.Item> { new("watch", "WATCH") };
#if !ANDROID
        items.Add(new("folder", "SHOW IN FOLDER"));
#endif
        items.Add(new("delete", "DELETE", Danger: true, Confirm: "Delete this replay? This can't be undone."));
        _menu.Open(r.LevelName.Length > 0 ? r.LevelName : r.LevelFileName, items, anchor, kb, confirmFirst);
    }

    private void RunOption(string id)
    {
        if (_menuIndex < 0 || _menuIndex >= _shown.Count) return;
        var r = _shown[_menuIndex];
        switch (id)
        {
            case "watch":
                ChosenReplay = r;
                UiSfx.Play(UiSound.StartSong);
                break;
            case "folder":
#if !ANDROID
                if (!PlatformShare.Reveal(r.FilePath)) ShowMessage("Could not open the folder.", true);
#endif
                break;
            case "delete":
                ReplayStore.Delete(r);
                ShowMessage("Replay deleted.");
                Reload();
                break;
        }
    }

    // ── Drawing ──────────────────────────────────────────────────────────────

    public void Draw(SpriteBatch sb, int sw, int sh)
    {
        float s = UiScale.FromScreen(sw, sh);
        Rect(sb, 0, 0, sw, sh, Bg);

        int margin = UiScale.Px(28, s);
        int top = UiScale.Px(22, s);

        // Back button
        int backW = UiScale.Px(112, s), backH = UiScale.Px(36, s);
        _backRect = new Rectangle(margin, top, backW, backH);
        bool backHover = _backRect.Contains(_lastMouse);
        Rect(sb, _backRect.X, _backRect.Y, backW, backH, backHover ? CardHi : Card);
        Rect(sb, _backRect.X, _backRect.Bottom - 2, backW, 2, backHover ? Pink : Pink * 0.5f);
        CenterText(sb, "ESC  Back", _backRect, Color.White, UiScale.FontPx(15, s));

        // Title
        int titleSize = UiScale.FontPx(42, s);
        var titleSz = Measure("REPLAYS", titleSize);
        float titleX = sw / 2f - titleSz.X / 2f;
        Text(sb, "REPLAYS", new Vector2(titleX + 2, top + 2), Pink * 0.4f, titleSize);
        Text(sb, "REPLAYS", new Vector2(titleX, top), Gold, titleSize);

        // Tabs
        int tabY = top + (int)titleSz.Y + UiScale.Px(14, s);
        int tabW = UiScale.Px(130, s), tabH = UiScale.Px(34, s), tabGap = UiScale.Px(10, s);
        int tabsX = sw / 2 - (Tabs.Length * tabW + (Tabs.Length - 1) * tabGap) / 2;
        for (int t = 0; t < Tabs.Length; t++)
        {
            var r = new Rectangle(tabsX + t * (tabW + tabGap), tabY, tabW, tabH);
            _tabRects[t] = r;
            bool sel = t == _filter;
            bool hov = r.Contains(_lastMouse);
            Rect(sb, r.X, r.Y, r.Width, r.Height, sel ? CardHi : hov ? Card : Card * 0.6f);
            Rect(sb, r.X, r.Bottom - 2, r.Width, 2, sel ? Gold : Color.White * 0.1f);
            int count = t switch { 1 => _all.Count(x => !x.IsOnline), 2 => _all.Count(x => x.IsOnline), _ => _all.Count };
            CenterText(sb, $"{Tabs[t]} ({count})", r, sel ? Gold : Dim, UiScale.FontPx(14, s));
        }

        // List
        int listTop = tabY + tabH + UiScale.Px(18, s);
        int footerH = UiScale.Px(34, s);
        int listBottom = sh - footerH - UiScale.Px(12, s);
        int rowH = Math.Max(40, UiScale.Px(56, s));
        int gap = UiScale.Px(6, s, 4);
        int listW = Math.Min(sw - margin * 2, UiScale.Px(1100, s));
        int listX = sw / 2 - listW / 2;
        _visibleRows = Math.Max(1, (listBottom - listTop + gap) / (rowH + gap));
        KeepSelectionVisible();
        _rowRects.Clear();
        _moreRects.Clear();

        if (_shown.Count == 0)
        {
            string empty = _all.Count == 0
                ? "No replays yet. Finish a song (or an online match) and it is saved here automatically."
                : "No replays in this category.";
            int f = UiScale.FontPx(17, s);
            var sz = Measure(empty, f);
            Text(sb, empty, new Vector2(sw / 2f - sz.X / 2f, listTop + UiScale.Px(40, s)), Dim, f);
        }

        int nameF = UiScale.FontPx(17, s), metaF = UiScale.FontPx(13, s), badgeF = UiScale.FontPx(12, s);
        for (int i = _scroll; i < Math.Min(_shown.Count, _scroll + _visibleRows); i++)
        {
            var r = _shown[i];
            var rect = new Rectangle(listX, listTop + (i - _scroll) * (rowH + gap), listW, rowH);
            _rowRects.Add(rect);
            bool sel = i == _selected;
            Rect(sb, rect.X, rect.Y, rect.Width, rect.Height, sel ? CardHi : Card);
            Rect(sb, rect.X, rect.Y, UiScale.Px(4, s, 2), rect.Height, sel ? Pink : Pink * 0.25f);

            int pad = UiScale.Px(14, s);
            // Kind badge
            bool online = r.IsOnline;
            bool partial = online && r.Tracks.Count < 2;
            string badge = online ? (partial ? "ONLINE - YOU ONLY" : "ONLINE") : "SOLO";
            Color badgeColor = online ? (partial ? Gold : Cyan) : Green;
            var bsz = Measure(badge, badgeF);
            int bw = (int)bsz.X + UiScale.Px(16, s), bh = UiScale.Px(22, s, 18);
            var brect = new Rectangle(rect.X + pad, rect.Y + (rowH - bh) / 2, bw, bh);
            Rect(sb, brect.X, brect.Y, bw, bh, badgeColor * 0.18f);
            Rect(sb, brect.X, brect.Bottom - 1, bw, 1, badgeColor);
            CenterText(sb, badge, brect, badgeColor, badgeF);

            // Right side: "..." (selected / hovered row), date + duration
            int moreW = UiScale.Px(30, s, 24), moreH = UiScale.Px(26, s, 22);
            var more = new Rectangle(rect.Right - pad - moreW, rect.Y + (rowH - moreH) / 2, moreW, moreH);
            if (sel || rect.Contains(_lastMouse))
            {
                _ui.MoreButton(sb, more, more.Contains(_lastMouse), 1f, s);
                _moreRects.Add((i, more));
            }
            string when = DateTimeOffset.FromUnixTimeSeconds(r.CreatedUnix).ToLocalTime().ToString("yyyy-MM-dd HH:mm");
            string dur = TimeSpan.FromSeconds(Math.Max(0, r.DurationSeconds)).ToString(@"m\:ss");
            string right = $"{when}   {dur}";
            var rsz = Measure(right, metaF);
            Text(sb, right, new Vector2(more.X - UiScale.Px(12, s) - rsz.X, rect.Y + (rowH - rsz.Y) / 2f), Dim, metaF);

            // Name + players line
            int textX = brect.Right + UiScale.Px(12, s);
            float maxW = more.X - UiScale.Px(12, s) - rsz.X - UiScale.Px(16, s) - textX;
            string level = Truncate(r.LevelName.Length > 0 ? r.LevelName : r.LevelFileName, nameF, maxW);
            Text(sb, level, new Vector2(textX, rect.Y + UiScale.Px(7, s)), sel ? Color.White : new Color(210, 210, 228), nameF);
            string players = string.Join("   vs   ", r.Tracks.Select(t => $"{t.Username}  {t.Result.Score:N0}  ({t.Result.Accuracy:F1}%, {t.Result.Misses} miss)"));
            Text(sb, Truncate(players, metaF, maxW), new Vector2(textX, rect.Bottom - UiScale.Px(7, s) - Measure("A", metaF).Y), Dim, metaF);
        }

        // Scrollbar
        if (_shown.Count > _visibleRows)
        {
            int trackH = _visibleRows * (rowH + gap) - gap;
            int barH = Math.Max(UiScale.Px(24, s), trackH * _visibleRows / _shown.Count);
            int barY = listTop + (trackH - barH) * _scroll / Math.Max(1, _shown.Count - _visibleRows);
            Rect(sb, listX + listW + UiScale.Px(8, s), listTop, UiScale.Px(4, s, 2), trackH, Color.White * 0.06f);
            Rect(sb, listX + listW + UiScale.Px(8, s), barY, UiScale.Px(4, s, 2), barH, Pink * 0.8f);
        }

        // Footer
        Rect(sb, 0, sh - footerH, sw, footerH, Color.Black * 0.45f);
        string hint = $"UP/DOWN: Select  |  ENTER: Watch  |  [{Controls.Hint(HotkeyAction.FreeplayOptions)}] / ...: Options  |  DEL: Delete  |  Q/E: Filter  |  ESC: Back";
        int hf = UiScale.FontPx(13, s);
        var hsz = Measure(hint, hf);
        Text(sb, hint, new Vector2(sw / 2f - hsz.X / 2f, sh - footerH + (footerH - hsz.Y) / 2f), Dim, hf);

        // Message toast
        if (_message != null)
        {
            int mf = UiScale.FontPx(15, s);
            var msz = Measure(_message, mf);
            int mw = (int)msz.X + UiScale.Px(28, s), mh = (int)msz.Y + UiScale.Px(16, s);
            var mr = new Rectangle(sw / 2 - mw / 2, sh - footerH - mh - UiScale.Px(14, s), mw, mh);
            Rect(sb, mr.X, mr.Y, mw, mh, Color.Black * 0.85f);
            Rect(sb, mr.X, mr.Bottom - 2, mw, 2, _messageIsError ? new Color(255, 95, 115) : Green);
            CenterText(sb, _message, mr, Color.White, mf);
        }

        _menu.Draw(sb, sw, sh);
    }

    // ── Helpers ──────────────────────────────────────────────────────────────

    private void Rect(SpriteBatch sb, int x, int y, int w, int h, Color c) => sb.Draw(_pixel, new Rectangle(x, y, w, h), c);

    private void Text(SpriteBatch sb, string text, Vector2 pos, Color color, int size)
    {
        if (_fontRenderer != null) _fontRenderer.DrawString(sb, text, pos, color, size);
        else if (_font != null) sb.DrawString(_font, text, pos, color, 0f, Vector2.Zero, size / 32f, SpriteEffects.None, 0f);
    }

    private Vector2 Measure(string text, int size)
    {
        if (_fontRenderer != null) return _fontRenderer.MeasureString(text, size);
        if (_font != null) return _font.MeasureString(text) * (size / 32f);
        return new Vector2(text.Length * size * 0.5f, size);
    }

    private void CenterText(SpriteBatch sb, string text, Rectangle r, Color color, int size)
    {
        var sz = Measure(text, size);
        Text(sb, text, new Vector2(r.X + (r.Width - sz.X) / 2f, r.Y + (r.Height - sz.Y) / 2f), color, size);
    }

    private string Truncate(string text, int size, float maxW)
    {
        if (maxW <= 10 || Measure(text, size).X <= maxW) return text;
        for (int n = text.Length - 1; n > 1; n--)
        {
            string t = text[..n] + "...";
            if (Measure(t, size).X <= maxW) return t;
        }
        return "...";
    }
}
