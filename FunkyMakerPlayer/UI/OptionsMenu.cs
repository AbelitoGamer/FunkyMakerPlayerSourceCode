using System;
using System.Collections.Generic;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using Microsoft.Xna.Framework.Input;

namespace FunkyMakerPlayer.UI;

/// <summary>
/// Reusable "..." options menu (fork). A list row shows a "..." button (<see cref="UiDraw.MoreButton"/>);
/// clicking it, right-clicking the row or pressing the options key opens this small menu next to it
/// with the row's extra actions. Destructive actions set <see cref="Item.Confirm"/>: picking them
/// first turns the menu into "question / YES / CANCEL", so nothing is deleted by one key press.
/// Up/Down + Enter/Space, mouse hover + click, Esc / Backspace / click outside close it.
/// The owner calls <see cref="Update"/> before its own input (it returns true when it used the
/// input) and <see cref="Draw"/> last; the chosen action comes back as <see cref="Item.Id"/>.
/// </summary>
public sealed class OptionsMenu
{
    public sealed record Item(string Id, string Label, bool Danger = false, bool Enabled = true, string? Confirm = null);

    private readonly UiDraw _ui;
    private readonly List<Item> _items = new();
    private readonly List<Rectangle> _bounds = new();
    private string _title = "";
    private Point _anchor;
    private int _sel;
    private Item? _confirming;
    private KeyboardState _prevKb;

    public bool IsOpen { get; private set; }

    public OptionsMenu(UiDraw ui) => _ui = ui;

    /// <summary>
    /// Opens the menu. <paramref name="anchor"/> is the point it hangs from (its top-right corner,
    /// moved to stay on screen). <paramref name="confirmFirst"/> opens straight at that item's
    /// confirmation (e.g. the Delete key).
    /// </summary>
    public void Open(string title, IEnumerable<Item> items, Point anchor, KeyboardState kb, string? confirmFirst = null)
    {
        _title = title;
        _items.Clear();
        _items.AddRange(items);
        _anchor = anchor;
        _sel = Math.Max(0, _items.FindIndex(i => i.Enabled));
        _confirming = confirmFirst != null ? _items.Find(i => i.Id == confirmFirst && i.Enabled) : null;
        if (_confirming != null) _sel = 1; // CANCEL by default
        _prevKb = kb;
        IsOpen = true;
        UiSfx.Play(UiSound.Confirm);
    }

    public void Close()
    {
        IsOpen = false;
        _confirming = null;
    }

    private int RowCount => _confirming != null ? 2 : _items.Count;
    private bool RowEnabled(int i) => _confirming != null || (i >= 0 && i < _items.Count && _items[i].Enabled);

    /// <summary>
    /// Handles input while open. Returns true when the input was used (the caller skips its own);
    /// <paramref name="chosen"/> is set when an action was picked (after its confirmation, if any).
    /// </summary>
    public bool Update(KeyboardState kb, Point mouse, bool clicked, bool rightClicked, bool mouseMoved, out string? chosen)
    {
        chosen = null;
        if (!IsOpen) { _prevKb = kb; return false; }

        int hover = -1;
        for (int i = 0; i < _bounds.Count; i++)
            if (_bounds[i].Contains(mouse)) { hover = i; break; }
        if (hover >= 0 && mouseMoved && RowEnabled(hover) && hover != _sel) { _sel = hover; UiSfx.Play(UiSound.Move); }

        bool Pressed(Keys k) => kb.IsKeyDown(k) && !_prevKb.IsKeyDown(k);
        bool consumedRightClick = true;
        if (Pressed(Keys.Escape) || Pressed(Keys.Back))
        {
            if (_confirming != null) { _confirming = null; _sel = 0; }
            else Close();
            UiSfx.Play(UiSound.Back);
        }
        else if (Pressed(Keys.Up) || Pressed(Keys.Down))
        {
            int dir = Pressed(Keys.Up) ? -1 : 1;
            for (int n = 0; n < RowCount; n++)
            {
                _sel = (_sel + dir + RowCount) % RowCount;
                if (RowEnabled(_sel)) break;
            }
            UiSfx.Play(UiSound.Move);
        }
        else if (Pressed(Keys.Enter) || Pressed(Keys.Space))
        {
            chosen = Pick(_sel);
        }
        else if (clicked)
        {
            if (hover >= 0) chosen = Pick(hover);
            else { Close(); UiSfx.Play(UiSound.Back); }
        }
        else if (rightClicked && hover < 0)
        {
            // A right click elsewhere closes it and may open it again on another row.
            Close();
            consumedRightClick = false;
        }

        _prevKb = kb;
        return consumedRightClick;
    }

    private string? Pick(int index)
    {
        if (_confirming != null)
        {
            var item = _confirming;
            _confirming = null;
            if (index == 0) { Close(); return item.Id; }
            _sel = Math.Max(0, _items.IndexOf(item));
            UiSfx.Play(UiSound.Back);
            return null;
        }
        if (index < 0 || index >= _items.Count || !_items[index].Enabled) return null;
        var it = _items[index];
        if (it.Confirm != null)
        {
            _confirming = it;
            _sel = 1;
            UiSfx.Play(UiSound.Notice);
            return null;
        }
        Close();
        return it.Id;
    }

    public void Draw(SpriteBatch sb, int sw, int sh)
    {
        _bounds.Clear();
        if (!IsOpen) return;
        float s = UiScale.FromScreen(sw, sh);

        int font = UiScale.FontPx(15, s);
        int itemH = UiScale.Px(36, s, 30);
        int padY = UiScale.Px(6, s);
        int titleH = UiScale.Px(30, s, 24);
        string title = _confirming?.Confirm ?? _title;
        var rows = new List<(string Label, bool Danger, bool Enabled)>();
        if (_confirming != null)
        {
            rows.Add(("YES, " + _confirming.Label, true, true));
            rows.Add(("CANCEL", false, true));
        }
        else
        {
            foreach (var it in _items) rows.Add((it.Label, it.Danger, it.Enabled));
        }

        int width = UiScale.Px(230, s, 190);
        foreach (var r in rows) width = Math.Max(width, (int)_ui.Measure(r.Label, font).X + UiScale.Px(40, s));
        int tFont = UiScale.FontPx(12, s);
        width = Math.Max(width, Math.Min(UiScale.Px(420, s), (int)_ui.Measure(title, tFont).X + UiScale.Px(24, s)));
        int height = titleH + rows.Count * itemH + padY * 2;

        int x = Math.Clamp(_anchor.X - width, UiScale.Px(8, s), Math.Max(UiScale.Px(8, s), sw - width - UiScale.Px(8, s)));
        int y = Math.Clamp(_anchor.Y, UiScale.Px(8, s), Math.Max(UiScale.Px(8, s), sh - height - UiScale.Px(8, s)));

        Color frame = _confirming != null ? new Color(255, 90, 110) : new Color(255, 68, 136);
        _ui.Rect(sb, x - 2, y - 2, width + 4, height + 4, frame * 0.45f);
        _ui.Rect(sb, x, y, width, height, new Color(24, 22, 38));
        _ui.Rect(sb, x, y, width, UiScale.Px(3, s, 2), _confirming != null ? frame : new Color(255, 215, 0));
        string t = _ui.Truncate(title, tFont, width - UiScale.Px(20, s));
        var tSz = _ui.Measure(t, tFont);
        _ui.Text(sb, t, new Vector2(x + UiScale.Px(12, s), y + (titleH - tSz.Y) / 2f + 1),
            (_confirming != null ? new Color(255, 160, 170) : new Color(255, 215, 0)) * 0.9f, tFont);
        _ui.Rect(sb, x + UiScale.Px(8, s), y + titleH, width - UiScale.Px(16, s), 1, Color.White * 0.1f);

        int iy = y + titleH + padY;
        for (int i = 0; i < rows.Count; i++)
        {
            var r = new Rectangle(x + 4, iy, width - 8, itemH);
            _bounds.Add(r);
            var (label, danger, enabled) = rows[i];
            bool sel = i == _sel && enabled;
            Color acc = danger ? new Color(255, 90, 110) : new Color(255, 68, 136);
            if (sel)
            {
                _ui.Rect(sb, r, acc * 0.30f);
                _ui.Rect(sb, r.X, r.Y, UiScale.Px(3, s, 2), r.Height, danger ? acc : new Color(255, 215, 0));
            }
            Color col = !enabled ? Color.White * 0.3f
                : danger ? new Color(255, 140, 150)
                : sel ? Color.White : new Color(200, 200, 220);
            _ui.LeftText(sb, label, r, col, font, UiScale.Px(14, s));
            iy += itemH;
        }
    }
}
