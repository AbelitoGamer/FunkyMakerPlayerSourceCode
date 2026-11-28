using System;
using System.Collections.Generic;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using Microsoft.Xna.Framework.Input;

namespace FunkyMakerPlayer.UI;

/// <summary>
/// Tap / click support for the Settings style pickers (Arrows, Note Splash, Countdown, Font; fork,
/// 2026-11-23). They were keyboard / D-pad only, with 38 px rows and the touch Q button over the
/// list. Each picker records its rows while drawing (<see cref="AddRow"/>), draws the header buttons
/// with <see cref="DrawHeaderButtons"/>, and asks <see cref="Poll"/> once per update:
/// tap a row = preview it, tap the previewed row again or APPLY = apply, BACK = cancel.
/// </summary>
public sealed class PickerTouch
{
    public readonly record struct Tap(int? Select, bool Confirm, bool Cancel);

    private readonly List<(int Index, Rectangle Rect)> _rows = new();
    private Rectangle _backRect, _applyRect;
    private MouseState _prev;
    private int _hoverBtn; // 0 none, 1 back, 2 apply

    /// <summary>Layout scale for pixel-based pickers: 1 at 720 px tall, larger on bigger screens.</summary>
    public static float Scale(int screenHeight) => Math.Max(1f, screenHeight / 720f);

    /// <summary>Header bar height at this screen size (was a fixed 54 px).</summary>
    public static int HeaderHeight(int screenHeight) => (int)(54 * Scale(screenHeight));

    /// <summary>Call when the picker opens, so the tap that opened it doesn't also hit it.</summary>
    public void Sync() => _prev = PointerInput.GetState();

    /// <summary>Call at the start of Draw, then <see cref="AddRow"/> for every row drawn.</summary>
    public void BeginRows() => _rows.Clear();

    public void AddRow(int index, Rectangle rect) => _rows.Add((index, rect));

    public Tap Poll(int selection)
    {
        var ms = PointerInput.GetState();
        bool clicked = ms.LeftButton == ButtonState.Pressed && _prev.LeftButton == ButtonState.Released;
        _prev = ms;
        var p = ms.Position;
        _hoverBtn = _backRect.Contains(p) ? 1 : _applyRect.Contains(p) ? 2 : 0;
        if (!clicked) return default;
        if (_hoverBtn == 1) return new Tap(null, false, true);
        if (_hoverBtn == 2) return new Tap(null, true, false);
        foreach (var (i, r) in _rows)
        {
            if (!r.Contains(p)) continue;
            return i == selection ? new Tap(null, true, false) : new Tap(i, false, false);
        }
        return default;
    }

    /// <summary>BACK and APPLY buttons on the right of the header bar.</summary>
    public void DrawHeaderButtons(SpriteBatch sb, Texture2D pixel, SpriteFont font, FontRenderer? fr,
        int sw, int sh, Color accent)
    {
        int headerH = HeaderHeight(sh);
        int h = (int)(headerH * 0.72f);
        int w = (int)(h * 2.6f);
        int gap = h / 3;
        int y = (headerH - h) / 2;
        _applyRect = new Rectangle(sw - gap - w, y, w, h);
        _backRect = new Rectangle(_applyRect.X - gap - w, y, w, h);
        DrawButton(sb, pixel, font, fr, _backRect, "BACK", _hoverBtn == 1, Color.White * 0.25f);
        DrawButton(sb, pixel, font, fr, _applyRect, "APPLY", _hoverBtn == 2, accent * 0.6f);
    }

    /// <summary>One footer hint line at the bottom of the screen.</summary>
    public static void DrawHint(SpriteBatch sb, SpriteFont font, FontRenderer? fr, int sw, int sh, string text)
    {
        int size = Math.Clamp((int)(sh * 0.026f), 14, 30);
        if (fr != null)
        {
            var sz = fr.MeasureString(text, size);
            fr.DrawString(sb, text, new Vector2((sw - sz.X) / 2f, sh - sz.Y - size * 0.6f), Color.White * 0.5f, size);
        }
        else
        {
            var sz = font.MeasureUi(text) * 0.6f;
            sb.DrawUiString(font, text, new Vector2((sw - sz.X) / 2f, sh - sz.Y - 8), Color.White * 0.5f,
                0f, Vector2.Zero, 0.6f, SpriteEffects.None, 0f);
        }
    }

    /// <summary>The hint for the pickers' footers, by platform.</summary>
    public static string HintText => PointerInput.IsTouch
        ? "Tap to preview   |   Tap again or APPLY to use it   |   Back: cancel"
        : "UP/DOWN or click: preview   |   ENTER / click again: apply   |   ESC: back";

    private static void DrawButton(SpriteBatch sb, Texture2D pixel, SpriteFont font, FontRenderer? fr,
        Rectangle r, string label, bool hover, Color fill)
    {
        sb.Draw(pixel, r, hover ? fill * 1.6f : fill);
        sb.Draw(pixel, new Rectangle(r.X, r.Bottom - 2, r.Width, 2), Color.White * (hover ? 0.9f : 0.4f));
        int size = Math.Max(14, (int)(r.Height * 0.5f));
        if (fr != null)
        {
            var sz = fr.MeasureString(label, size);
            fr.DrawString(sb, label, new Vector2(r.X + (r.Width - sz.X) / 2f, r.Y + (r.Height - sz.Y) / 2f), Color.White, size);
        }
        else
        {
            float s = r.Height / 40f;
            var sz = font.MeasureUi(label) * s;
            sb.DrawUiString(font, label, new Vector2(r.X + (r.Width - sz.X) / 2f, r.Y + (r.Height - sz.Y) / 2f),
                Color.White, 0f, Vector2.Zero, s, SpriteEffects.None, 0f);
        }
    }
}
