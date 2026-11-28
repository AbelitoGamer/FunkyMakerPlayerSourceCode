using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using Microsoft.Xna.Framework.Input;

namespace FunkyMakerPlayer.UI;

/// <summary>
/// Modal window with every play modifier, grouped by <see cref="ModifierCategory"/> in a 2x2 grid
/// (fork). Self-contained so any screen can open it: the owner calls <see cref="Open"/>, then
/// <see cref="Update"/> (returns true while it takes the input) and <see cref="Draw"/> each frame.
/// Keyboard: Up/Down (or Tab) through the modifiers, Enter/Space toggles, Left/Right change a value
/// (Left on an "off" row does nothing), R resets all, Esc / the open key closes. Mouse: click a row
/// to toggle, -/+ for values, RESET ALL, CLOSE, click outside to close.
/// </summary>
public sealed class ModifiersWindow
{
    private readonly UiDraw _ui;
    private KeyboardState _prevKb;
    private int _sel;
    private float _anim;
    private string? _levelPath;
    private readonly List<(PlayModifier Mod, Rectangle Row, Rectangle Minus, Rectangle Plus)> _rows = new();
    private Rectangle _panel, _resetBtn, _closeBtn;
    private Point _mouse;

    public bool IsOpen { get; private set; }

    public ModifiersWindow(UiDraw ui) => _ui = ui;

    /// <summary>Opens the window. <paramref name="levelPath"/> = the level it applies to (for availability), or null.</summary>
    public void Open(string? levelPath, KeyboardState kb)
    {
        _levelPath = levelPath;
        _prevKb = kb;
        _sel = 0;
        _anim = 0f;
        IsOpen = true;
        UiSfx.Play(UiSound.Confirm);
    }

    public void Close()
    {
        if (!IsOpen) return;
        IsOpen = false;
        UiSfx.Play(UiSound.Back);
    }

    private static List<PlayModifier> Ordered()
        => PlayModifiers.Categories.SelectMany(c => PlayModifiers.InCategory(c.Category)).ToList();

    private void Toggle(PlayModifier m)
    {
        if (!m.IsOn() && m.Unavailable?.Invoke(_levelPath) is string) { UiSfx.Play(UiSound.Error); return; }
        bool on = !m.IsOn();
        // Mutually exclusive rate modifiers: switching one on replaces the other (both use SongRate).
        m.SetOn(on);
        UiSfx.Play(on ? UiSound.Confirm : UiSound.Back);
    }

    private void Step(PlayModifier m, int dir)
    {
        if (!m.HasValue || !m.IsOn()) return;
        float old = m.GetValue!();
        float v = Math.Clamp(old + dir * m.Step, m.Min, m.Max);
        if (Math.Abs(v - old) < 0.0001f) { UiSfx.Play(UiSound.SliderEnd); return; }
        m.SetValue!(v);
        UiSfx.Play(dir > 0 ? UiSound.SliderUp : UiSound.SliderDown);
    }

    /// <summary>Input while open; returns true when it used it.</summary>
    public bool Update(float dt, KeyboardState kb, Point mouse, bool clicked, bool mouseMoved)
    {
        _anim = MathHelper.Lerp(_anim, IsOpen ? 1f : 0f, Math.Min(1f, dt * 16f));
        _mouse = mouse;
        if (!IsOpen) { _prevKb = kb; return false; }

        var mods = Ordered();
        bool Pressed(Keys k) => kb.IsKeyDown(k) && !_prevKb.IsKeyDown(k);

        int hover = _rows.FindIndex(r => r.Row.Contains(mouse));
        if (hover >= 0 && mouseMoved && hover != _sel) { _sel = hover; UiSfx.Play(UiSound.Move); }

        if (Pressed(Keys.Escape) || Pressed(Keys.Back))   // Back = Android back button
            Close();
        else if (mods.Count > 0 && (Pressed(Keys.Up) || Pressed(Keys.Down)))
        {
            int dir = Pressed(Keys.Up) ? -1 : 1;
            _sel = (_sel + dir + mods.Count) % mods.Count;
            UiSfx.Play(UiSound.Move);
        }
        else if (mods.Count > 0 && (Pressed(Keys.Enter) || Pressed(Keys.Space)))
            Toggle(mods[Math.Clamp(_sel, 0, mods.Count - 1)]);
        else if (mods.Count > 0 && (Pressed(Keys.Left) || Pressed(Keys.Right)))
        {
            var m = mods[Math.Clamp(_sel, 0, mods.Count - 1)];
            if (m.HasValue && m.IsOn()) Step(m, Pressed(Keys.Right) ? 1 : -1);
            else if (Pressed(Keys.Right) != m.IsOn()) Toggle(m); // Right = on, Left = off
        }
        else if (clicked)
        {
            if (_closeBtn.Contains(mouse)) Close();
            else if (_resetBtn.Contains(mouse)) { PlayModifiers.ResetAll(); UiSfx.Play(UiSound.Confirm); }
            else if (!_panel.Contains(mouse)) Close();
            else
            {
                foreach (var r in _rows)
                {
                    if (r.Minus.Contains(mouse)) { Step(r.Mod, -1); break; }
                    if (r.Plus.Contains(mouse)) { Step(r.Mod, 1); break; }
                    if (r.Row.Contains(mouse)) { Toggle(r.Mod); break; }
                }
            }
        }

        _prevKb = kb;
        return true;
    }

    public void Draw(SpriteBatch sb, int sw, int sh)
    {
        _rows.Clear();
        float a = _anim;
        if (!IsOpen) return; // (fade-in only: Update stops running once closed)
        float s = UiScale.FromScreen(sw, sh);

        int w = Math.Min(sw - UiScale.Px(40, s), UiScale.Px(980, s));
        int h = Math.Min(sh - UiScale.Px(40, s), UiScale.Px(560, s));
        _panel = new Rectangle((sw - w) / 2, (sh - h) / 2 - (int)((1f - a) * UiScale.Px(14, s)), w, h);
        _ui.ModalFrame(sb, sw, sh, _panel, a, new Color(190, 140, 255));

        int pad = UiScale.Px(22, s);
        int titleF = UiScale.FontPx(26, s);
        _ui.Text(sb, "MODIFIERS", new Vector2(_panel.X + pad, _panel.Y + UiScale.Px(16, s)), new Color(255, 215, 0) * a, titleF);
        string sub = _levelPath != null ? "Apply to solo play (not online, replays or playtests)" : "Apply to solo play";
        int subF = UiScale.FontPx(13, s);
        var subSz = _ui.Measure(sub, subF);
        _ui.Text(sb, sub, new Vector2(_panel.Right - pad - subSz.X, _panel.Y + UiScale.Px(26, s)), Color.White * (0.45f * a), subF);

        // 2x2 category grid
        int gridTop = _panel.Y + UiScale.Px(64, s);
        int footerH = UiScale.Px(64, s);
        int gap = UiScale.Px(14, s);
        int cellW = (w - pad * 2 - gap) / 2;
        int cellH = (_panel.Bottom - footerH - gridTop - gap) / 2;
        var ordered = Ordered();
        int rowH = UiScale.Px(46, s, 38);
        int nameF = UiScale.FontPx(16, s), descF = UiScale.FontPx(12, s), catF = UiScale.FontPx(13, s);
        Color[] catColors = { new(90, 200, 255), new(255, 110, 120), new(46, 224, 150), new(255, 190, 70) };

        for (int c = 0; c < PlayModifiers.Categories.Length; c++)
        {
            var (cat, title) = PlayModifiers.Categories[c];
            var cell = new Rectangle(_panel.X + pad + (c % 2) * (cellW + gap), gridTop + (c / 2) * (cellH + gap), cellW, cellH);
            Color cc = catColors[c % catColors.Length];
            _ui.Rect(sb, cell, new Color(30, 28, 48) * (0.9f * a));
            _ui.Rect(sb, cell.X, cell.Y, cell.Width, 2, cc * a);
            _ui.Text(sb, title.ToUpperInvariant(), new Vector2(cell.X + UiScale.Px(12, s), cell.Y + UiScale.Px(8, s)), cc * a, catF);

            var mods = PlayModifiers.InCategory(cat).ToList();
            int y = cell.Y + UiScale.Px(32, s);
            if (mods.Count == 0)
            {
                _ui.Text(sb, "Nothing here yet", new Vector2(cell.X + UiScale.Px(12, s), y + UiScale.Px(6, s)), Color.White * (0.3f * a), descF);
                continue;
            }
            foreach (var m in mods)
            {
                if (y + rowH > cell.Bottom) break;
                var row = new Rectangle(cell.X + UiScale.Px(6, s), y, cell.Width - UiScale.Px(12, s), rowH);
                int index = ordered.IndexOf(m);
                bool sel = index == _sel;
                bool on = m.IsOn();
                string? why = m.Unavailable?.Invoke(_levelPath);
                if (sel) _ui.Rect(sb, row, new Color(190, 140, 255) * (0.16f * a));
                else if (row.Contains(_mouse)) _ui.Rect(sb, row, Color.White * (0.04f * a));

                // Toggle
                int th = UiScale.Px(24, s, 20), tw = UiScale.Px(46, s, 38);
                var toggle = new Rectangle(row.X + UiScale.Px(8, s), row.Y + (rowH - th) / 2, tw, th);
                _ui.Rect(sb, toggle, (on ? new Color(46, 224, 150) * 0.75f : new Color(50, 46, 68)) * a);
                int knob = th - 4;
                _ui.Rect(sb, on ? toggle.Right - knob - 2 : toggle.X + 2, toggle.Y + 2, knob, knob, (on ? Color.White : new Color(160, 155, 180)) * a);

                // Value (-/+), right aligned
                var minus = Rectangle.Empty;
                var plus = Rectangle.Empty;
                int right = row.Right - UiScale.Px(6, s);
                if (m.HasValue)
                {
                    int bw = th + UiScale.Px(4, s);
                    plus = new Rectangle(right - bw, row.Y + (rowH - th) / 2, bw, th);
                    string val = m.Format?.Invoke(m.GetValue!()) ?? m.GetValue!().ToString("0.##");
                    int vw = (int)_ui.Measure("0.00x", nameF).X + UiScale.Px(12, s);
                    var valRect = new Rectangle(plus.X - UiScale.Px(4, s) - vw, plus.Y, vw, th);
                    minus = new Rectangle(valRect.X - UiScale.Px(4, s) - bw, plus.Y, bw, th);
                    float va = on ? 1f : 0.35f;
                    _ui.Rect(sb, minus, (minus.Contains(_mouse) && on ? new Color(190, 140, 255) * 0.5f : new Color(44, 40, 60)) * a);
                    _ui.Rect(sb, plus, (plus.Contains(_mouse) && on ? new Color(190, 140, 255) * 0.5f : new Color(44, 40, 60)) * a);
                    _ui.CenterText(sb, "-", minus, Color.White * (va * a), nameF);
                    _ui.CenterText(sb, "+", plus, Color.White * (va * a), nameF);
                    _ui.CenterText(sb, val, valRect, (on ? new Color(255, 205, 90) : Color.White * 0.4f) * a, nameF);
                    right = minus.X - UiScale.Px(6, s);
                }

                int tx = toggle.Right + UiScale.Px(12, s);
                float textW = right - tx;
                Color nameCol = (why != null && !on ? Color.White * 0.35f : Color.White) * a;
                _ui.Text(sb, _ui.Truncate(m.Name, nameF, textW), new Vector2(tx, row.Y + UiScale.Px(4, s)), nameCol, nameF);
                string desc = why ?? m.Description;
                _ui.Text(sb, _ui.Truncate(desc, descF, textW), new Vector2(tx, row.Y + rowH - UiScale.Px(6, s) - _ui.Measure("A", descF).Y),
                    (why != null ? new Color(255, 160, 120) : Color.White * 0.5f) * a, descF);

                _rows.Add((m, row, minus, plus));
                y += rowH + UiScale.Px(4, s);
            }
        }

        // Footer: warning + buttons
        int fy = _panel.Bottom - footerH + UiScale.Px(14, s);
        int bh = UiScale.Px(36, s, 30);
        int btnF = UiScale.FontPx(14, s);
        int cw = (int)_ui.Measure("CLOSE", btnF).X + UiScale.Px(36, s);
        int rw = (int)_ui.Measure("RESET ALL", btnF).X + UiScale.Px(28, s);
        _closeBtn = new Rectangle(_panel.Right - pad - cw, fy, cw, bh);
        _resetBtn = new Rectangle(_closeBtn.X - UiScale.Px(10, s) - rw, fy, rw, bh);
        foreach (var (r, label, acc) in new[] { (_resetBtn, "RESET ALL", new Color(255, 110, 120)), (_closeBtn, "CLOSE", new Color(255, 68, 136)) })
        {
            bool hv = r.Contains(_mouse);
            _ui.Rect(sb, r, (hv ? Color.Lerp(new Color(36, 34, 56), acc, 0.3f) : new Color(36, 34, 56)) * a);
            _ui.Rect(sb, r.X, r.Bottom - 2, r.Width, 2, acc * a);
            _ui.CenterText(sb, label, r, Color.White * a, btnF);
        }
        string warn = PlayModifiers.RecordsBlocked
            ? "Active modifiers: personal bests and replays aren't saved"
            : "UP/DOWN Select  |  ENTER Toggle  |  LEFT/RIGHT Value  |  ESC Close";
        int wf = _ui.FitFontSize(warn, UiScale.FontPx(13, s), 9, _resetBtn.X - _panel.X - pad * 2);
        var wsz = _ui.Measure(warn, wf);
        _ui.Text(sb, warn, new Vector2(_panel.X + pad, fy + (bh - wsz.Y) / 2f),
            (PlayModifiers.RecordsBlocked ? new Color(255, 190, 90) : Color.White * 0.4f) * a, wf);
    }
}
