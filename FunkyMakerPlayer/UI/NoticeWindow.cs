using System;
using System.Collections.Generic;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using Microsoft.Xna.Framework.Input;

namespace FunkyMakerPlayer.UI;

/// <summary>
/// A small message window with one OK button, drawn by Game1 above every screen (fork, 2026-11-28).
/// While it is open it takes all input (Game1.UpdateInternal returns early), so whatever screen
/// opened it stays exactly as it was. Closes with OK, a tap outside the box, Enter / Escape or the
/// Android back gesture. Used for the editors that are not available on phones
/// (<see cref="Game1"/>.BlockEditorOnMobile).
/// </summary>
public static class NoticeWindow
{
    private static string _title = "";
    private static string _body = "";
    private static float _openTime;
    private static bool _waitRelease;
    private static KeyboardState _prevKb;
    private static Rectangle _box, _okBounds;
    private static bool _okHover;

    public static bool IsOpen { get; private set; }

    public static void Show(string title, string body)
    {
        _title = title;
        _body = body;
        IsOpen = true;
        _openTime = 0f;
        // The tap / click / key that opened it is still down: wait until it is let go
        _waitRelease = true;
        _prevKb = LayoutKeyboard.GetState();
        UiSfx.Play(UiSound.Error);
    }

    /// <summary>Call once per frame while <see cref="IsOpen"/>.</summary>
    public static void Update(float dt)
    {
        if (!IsOpen) return;
        _openTime += dt;
        var kb = LayoutKeyboard.GetState();
        var ms = PointerInput.GetState();
        bool down = ms.LeftButton == ButtonState.Pressed;
        _okHover = _okBounds.Contains(ms.Position);

        if (_waitRelease)
        {
            // Only the pointer: Android can report a key as held all the time (this waited forever there)
            if (!down) _waitRelease = false;
            _prevDown = down;
            _prevKb = kb;
            return;
        }

        bool Pressed(Keys k) => kb.IsKeyDown(k) && !_prevKb.IsKeyDown(k);
        bool close = Pressed(Keys.Enter) || Pressed(Keys.Escape) || Pressed(Keys.Space); // Android back = Escape
        // A click: the press edge (on touch, PointerInput reports a tap as one pressed frame when the
        // finger lifts; the mouse is then swallowed until released, see SwallowInput)
        if (!_prevDown && down && _openTime > 0.15f &&
            (_okBounds.Contains(ms.Position) || !_box.Contains(ms.Position)))
            close = true;
        _prevDown = down;
        _prevKb = kb;

        if (close)
        {
            IsOpen = false;
            _waitAfterClose = true;
            UiSfx.Play(UiSound.Back);
        }
    }

    private static bool _prevDown;
    private static bool _waitAfterClose;

    /// <summary>
    /// True for the rest of the frames until the pointer / keys are released after closing, so the
    /// screen underneath does not see the closing tap or key as its own input.
    /// </summary>
    public static bool SwallowInput()
    {
        if (!_waitAfterClose) return false;
        var kb = LayoutKeyboard.GetState();
        bool keysUp = !kb.IsKeyDown(Keys.Enter) && !kb.IsKeyDown(Keys.Escape) && !kb.IsKeyDown(Keys.Space);
        if (PointerInput.GetState().LeftButton == ButtonState.Released && keysUp)
        {
            _waitAfterClose = false;
            return false;
        }
        return true;
    }

    public static void Draw(SpriteBatch sb, UiDraw ui, int sw, int sh)
    {
        if (!IsOpen) return;
        float s = UiScale.FromScreen(sw, sh);
        Color accent = new Color(255, 68, 136);
        Color gold = new Color(255, 215, 0);

        ui.Rect(sb, 0, 0, sw, sh, new Color(8, 8, 16) * 0.8f);

        int boxW = Math.Min(UiScale.Px(620, s), sw - UiScale.Px(40, s));
        int pad = UiScale.Px(28, s);
        int titleSize = UiScale.FontPx(30, s);
        int bodySize = UiScale.FontPx(19, s);
        int btnH = UiScale.Px(52, s, 44);
        float maxW = boxW - pad * 2;

        var lines = Wrap(ui, ui.Safe(_body), bodySize, maxW);
        float lineH = ui.Measure("Ag", bodySize).Y + UiScale.Px(5, s);
        float titleH = ui.Measure("Ag", titleSize).Y;
        int boxH = (int)(pad + titleH + pad * 0.7f + lines.Count * lineH + pad + btnH + pad);
        int boxX = (sw - boxW) / 2;
        int boxY = Math.Max(UiScale.Px(10, s), (sh - boxH) / 2);
        _box = new Rectangle(boxX, boxY, boxW, boxH);

        int glow = UiScale.Px(3, s, 2);
        ui.Rect(sb, boxX - glow, boxY - glow, boxW + glow * 2, boxH + glow * 2, accent * 0.35f);
        ui.Rect(sb, _box, new Color(22, 22, 38) * 0.98f);
        ui.Rect(sb, boxX, boxY, boxW, UiScale.Px(4, s, 2), accent);

        float y = boxY + pad;
        string title = ui.Safe(_title);
        var tsz = ui.Measure(title, titleSize);
        ui.Text(sb, title, new Vector2(boxX + (boxW - tsz.X) / 2f, y), gold, titleSize);
        y += titleH + pad * 0.7f;
        foreach (var l in lines)
        {
            var lsz = ui.Measure(l, bodySize);
            ui.Text(sb, l, new Vector2(boxX + (boxW - lsz.X) / 2f, y), Color.White * 0.88f, bodySize);
            y += lineH;
        }

        int btnW = Math.Min(UiScale.Px(240, s), boxW - pad * 2);
        _okBounds = new Rectangle(boxX + (boxW - btnW) / 2, boxY + boxH - pad - btnH, btnW, btnH);
        ui.Rect(sb, _okBounds, _okHover ? new Color(60, 46, 82) : new Color(40, 34, 60));
        ui.Rect(sb, _okBounds.X, _okBounds.Y, _okBounds.Width, UiScale.Px(2, s, 1), accent);
        ui.Rect(sb, _okBounds.X, _okBounds.Bottom - UiScale.Px(2, s, 1), _okBounds.Width, UiScale.Px(2, s, 1), accent * 0.6f);
        ui.CenterText(sb, "OK", _okBounds, Color.White, UiScale.FontPx(21, s));
    }

    private static List<string> Wrap(UiDraw ui, string text, int size, float maxW)
    {
        var result = new List<string>();
        foreach (var para in text.Split('\n'))
        {
            string line = "";
            foreach (var word in para.Split(' '))
            {
                string test = line.Length == 0 ? word : line + " " + word;
                if (ui.Measure(test, size).X > maxW && line.Length > 0)
                {
                    result.Add(line);
                    line = word;
                }
                else line = test;
            }
            result.Add(line);
        }
        return result;
    }
}
