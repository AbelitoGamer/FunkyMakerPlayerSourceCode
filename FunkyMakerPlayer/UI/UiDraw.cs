using System;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;

namespace FunkyMakerPlayer.UI;

/// <summary>
/// Shared drawing helpers for menus drawn straight to the window (fork): rectangles, text with the
/// TrueType font (FontStashSharp) or the bitmap fallback, measuring, centring, truncation.
/// Used by the reusable UI pieces (<see cref="OptionsMenu"/>, <see cref="ModifiersWindow"/>) so every
/// screen draws them the same way.
/// </summary>
public sealed class UiDraw
{
    public readonly Texture2D Pixel;
    private readonly SpriteFont? _font;
    public FontRenderer? FontRenderer { get; set; }

    public UiDraw(Texture2D pixel, SpriteFont? font, FontRenderer? fontRenderer)
    {
        Pixel = pixel;
        _font = font;
        FontRenderer = fontRenderer;
    }

    public void Rect(SpriteBatch sb, int x, int y, int w, int h, Color c)
    {
        if (w <= 0 || h <= 0) return;
        sb.Draw(Pixel, new Rectangle(x, y, w, h), c);
    }

    public void Rect(SpriteBatch sb, Rectangle r, Color c) => Rect(sb, r.X, r.Y, r.Width, r.Height, c);

    /// <summary>Text the active font can draw (the bitmap fallback only has ASCII).</summary>
    public string Safe(string s)
    {
        if (string.IsNullOrEmpty(s) || FontRenderer != null) return s ?? "";
        var b = new System.Text.StringBuilder(s.Length);
        foreach (char c in s) b.Append(c >= 32 && c < 127 ? c : '?');
        return b.ToString();
    }

    public void Text(SpriteBatch sb, string text, Vector2 pos, Color color, int size)
    {
        if (FontRenderer != null) FontRenderer.DrawString(sb, text, pos, color, size);
        else if (_font != null) sb.DrawUiString(_font, Safe(text), pos, color, 0f, Vector2.Zero, size / 32f, SpriteEffects.None, 0f);
    }

    public Vector2 Measure(string text, int size)
    {
        if (FontRenderer != null) return FontRenderer.MeasureString(text, size);
        if (_font != null) return _font.MeasureUi(Safe(text)) * (size / 32f);
        return new Vector2(text.Length * size * 0.5f, size);
    }

    public void CenterText(SpriteBatch sb, string text, Rectangle r, Color color, int size)
    {
        var sz = Measure(text, size);
        Text(sb, text, new Vector2(r.X + (r.Width - sz.X) / 2f, r.Y + (r.Height - sz.Y) / 2f), color, size);
    }

    /// <summary>Text vertically centred in <paramref name="r"/>, starting at its left edge + <paramref name="padX"/>.</summary>
    public void LeftText(SpriteBatch sb, string text, Rectangle r, Color color, int size, int padX = 0)
    {
        var sz = Measure(text, size);
        Text(sb, text, new Vector2(r.X + padX, r.Y + (r.Height - sz.Y) / 2f), color, size);
    }

    public string Truncate(string text, int size, float maxW)
    {
        if (string.IsNullOrEmpty(text) || Measure(text, size).X <= maxW) return text;
        if (maxW <= 10) return "";
        int lo = 1, hi = text.Length;
        string best = "...";
        while (lo < hi)
        {
            int mid = (lo + hi + 1) / 2;
            string c = text[..mid] + "...";
            if (Measure(c, size).X <= maxW) { best = c; lo = mid; }
            else hi = mid - 1;
        }
        return best;
    }

    public int FitFontSize(string text, int preferred, int min, float maxW)
    {
        int size = Math.Max(min, preferred);
        while (size > min && Measure(text, size).X > maxW) size--;
        return size;
    }

    /// <summary>A "..." button (three dots); the convention for "more options" on a row (fork).</summary>
    public void MoreButton(SpriteBatch sb, Rectangle r, bool hovered, float alpha, float s)
    {
        Rect(sb, r, (hovered ? new Color(255, 68, 136) * 0.45f : new Color(40, 36, 60)) * alpha);
        int dot = Math.Max(2, UiScale.Px(3, s, 2));
        for (int d = -1; d <= 1; d++)
            Rect(sb, r.Center.X + d * dot * 2 - dot / 2, r.Center.Y - dot / 2, dot, dot, Color.White * ((hovered ? 1f : 0.7f) * alpha));
    }

    /// <summary>Dimmed backdrop + framed panel used by modal windows.</summary>
    public void ModalFrame(SpriteBatch sb, int sw, int sh, Rectangle panel, float alpha, Color accent)
    {
        Rect(sb, 0, 0, sw, sh, new Color(8, 8, 16) * (0.72f * alpha));
        Rect(sb, panel.X - 3, panel.Y - 3, panel.Width + 6, panel.Height + 6, accent * (0.4f * alpha));
        Rect(sb, panel, new Color(22, 20, 36) * (0.98f * alpha));
        Rect(sb, panel.X, panel.Y, panel.Width, 4, new Color(255, 215, 0) * alpha);
    }
}
