using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;

namespace FunkyMakerPlayer;

/// <summary>
/// Drop-in replacements for <c>SpriteBatch.DrawString(SpriteFont, …)</c> and
/// <c>SpriteFont.MeasureString</c> that render through the TrueType <see cref="FontRenderer.Current"/>
/// instead of the 16 px bitmap SpriteFont, so scaled-up text stays sharp.
/// <para>
/// Sizes keep the SpriteFont's metrics: text drawn at scale k gets a line height of
/// <c>font.LineSpacing × k</c>, and <see cref="MeasureUi"/> returns SpriteFont-space units, so existing
/// layout code (which multiplies measurements by its own scale) works unchanged.
/// Falls back to the bitmap font when no TrueType font is loaded, or for flipped / non-uniformly
/// scaled text.
/// </para>
/// </summary>
public static class UiText
{
    public static void DrawUiString(this SpriteBatch sb, SpriteFont font, string text, Vector2 position, Color color)
        => DrawUiString(sb, font, text, position, color, 0f, Vector2.Zero, Vector2.One, SpriteEffects.None, 0f);

    public static void DrawUiString(this SpriteBatch sb, SpriteFont font, string text, Vector2 position, Color color,
        float rotation, Vector2 origin, float scale, SpriteEffects effects, float layerDepth)
        => DrawUiString(sb, font, text, position, color, rotation, origin, new Vector2(scale), effects, layerDepth);

    public static void DrawUiString(this SpriteBatch sb, SpriteFont font, string text, Vector2 position, Color color,
        float rotation, Vector2 origin, Vector2 scale, SpriteEffects effects, float layerDepth)
    {
        if (string.IsNullOrEmpty(text)) return;
        var r = FontRenderer.Current;
        if (r == null || effects != SpriteEffects.None || scale.X != scale.Y || scale.X <= 0f)
        {
            sb.DrawString(font, text, position, color, rotation, origin, scale, effects, layerDepth);
            return;
        }
        float size = font.LineSpacing * scale.X * r.SizePerLineHeight;
        r.DrawSized(sb, text, position, color, size, rotation, origin * scale.X);
    }

    public static void DrawUiString(this SpriteBatch sb, SpriteFont font, System.Text.StringBuilder text, Vector2 position, Color color)
        => DrawUiString(sb, font, text.ToString(), position, color);

    /// <summary>Text size in SpriteFont units (multiply by the draw scale, as with MeasureString).</summary>
    public static Vector2 MeasureUi(this SpriteFont font, string text)
    {
        var r = FontRenderer.Current;
        if (r == null) return font.MeasureString(text ?? "");
        return r.MeasureScaled(text ?? "") * (font.LineSpacing * r.SizePerLineHeight);
    }

    public static Vector2 MeasureUi(this SpriteFont font, System.Text.StringBuilder text) => MeasureUi(font, text.ToString());
}
