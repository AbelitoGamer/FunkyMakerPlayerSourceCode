using System;
using System.IO;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using FontStashSharp;

namespace FunkyMakerPlayer;

/// <summary>
/// Wrapper for FontStashSharp to provide crisp TrueType font rendering.
/// <para>
/// Glyphs are always rasterized at the size they will occupy on screen in PHYSICAL pixels, so text
/// stays sharp at any size: when drawing into the letterbox render target (which is drawn through
/// <c>RtScaleMatrix</c>), the logical size is multiplied by <see cref="RenderTargetScale"/> and the
/// result is drawn back at 1/scale, landing 1:1 on the target's pixels. Positions are snapped to the
/// physical pixel grid for the same reason.
/// </para>
/// </summary>
public class FontRenderer
{
    /// <summary>The renderer for the UI font currently in use (null until one loads).</summary>
    public static FontRenderer? Current { get; set; }

    /// <summary>
    /// Physical pixels per logical pixel of the letterbox render target (Game1._rtScale).
    /// Applied whenever a render target is bound, since every pass into it draws through RtScaleMatrix.
    /// </summary>
    public static float RenderTargetScale { get; set; } = 1f;

    private const int MinRasterSize = 4;
    private const int MaxRasterSize = 512;
    /// <summary>Size used for size-independent measurements (see <see cref="MeasureScaled"/>).</summary>
    private const int ReferenceSize = 128;

    private readonly FontSystem _fontSystem;
    private readonly GraphicsDevice _graphicsDevice;

    /// <summary>Font size whose line height is exactly 1 logical pixel (size / LineHeight).</summary>
    public float SizePerLineHeight { get; }

    public FontRenderer(GraphicsDevice graphicsDevice, string fontPath)
    {
        _graphicsDevice = graphicsDevice;
        _fontSystem = new FontSystem(new FontSystemSettings
        {
            // Glyphs are rasterized at their final on-screen pixel size (see class remarks), so no
            // supersampling is needed; KernelWidth/Height are a box-blur prefilter and must stay 0.
            FontResolutionFactor = 1,
            KernelWidth = 0,
            KernelHeight = 0,
            // Large atlases so big titles (hundreds of px) don't thrash pages
            TextureWidth = 2048,
            TextureHeight = 2048,
        });

        byte[] fontData = File.ReadAllBytes(fontPath);
        _fontSystem.AddFont(fontData);

        var reference = _fontSystem.GetFont(ReferenceSize);
        SizePerLineHeight = reference.LineHeight > 0 ? ReferenceSize / (float)reference.LineHeight : 1f;
    }

    /// <summary>Physical pixels per logical pixel for whatever is being drawn right now.</summary>
    private float PixelScale =>
        _graphicsDevice.RenderTargetCount > 0 ? Math.Max(0.01f, RenderTargetScale) : 1f;

    private static int RasterSize(float physicalSize) =>
        Math.Clamp((int)MathF.Round(physicalSize), MinRasterSize, MaxRasterSize);

    /// <summary>
    /// Get a font rasterized at exactly <paramref name="size"/> pixels. Note: drawing it yourself
    /// bypasses the render-target scale correction; prefer the DrawString overloads.
    /// </summary>
    public DynamicSpriteFont GetFont(int size) => _fontSystem.GetFont(Math.Clamp(size, MinRasterSize, MaxRasterSize));

    /// <summary>Draw text with automatic size calculation (scale 1 = 32 px).</summary>
    public void DrawString(SpriteBatch spriteBatch, string text, Vector2 position, Color color, float scale = 1.0f)
        => DrawSized(spriteBatch, text, position, color, 32f * scale, 0f, Vector2.Zero);

    /// <summary>Draw text with explicit font size in logical pixels.</summary>
    public void DrawString(SpriteBatch spriteBatch, string text, Vector2 position, Color color, int fontSize)
        => DrawSized(spriteBatch, text, position, color, fontSize, 0f, Vector2.Zero);

    /// <summary>
    /// Draw text at any (fractional) logical font size, with optional rotation around
    /// <paramref name="origin"/> (given in logical pixels relative to the text's top-left).
    /// </summary>
    public void DrawSized(SpriteBatch spriteBatch, string text, Vector2 position, Color color,
        float fontSize, float rotation, Vector2 origin)
    {
        if (string.IsNullOrEmpty(text) || fontSize <= 0f) return;

        float ps = PixelScale;
        int raster = RasterSize(fontSize * ps);
        var font = _fontSystem.GetFont(raster);
        // Logical size / raster size: 1/ps unless the raster size was rounded or clamped
        float s = fontSize / raster;

        if (rotation == 0f)
        {
            // Snap the (origin-adjusted) top-left to the physical pixel grid for 1:1 texel mapping
            position -= origin;
            position = new Vector2(MathF.Round(position.X * ps) / ps, MathF.Round(position.Y * ps) / ps);
            font.DrawText(spriteBatch, text, position, color, scale: new Vector2(s));
        }
        else
        {
            // FontStashSharp's origin is in unscaled (raster) units
            font.DrawText(spriteBatch, text, position, color, scale: new Vector2(s),
                rotation: rotation, origin: origin / s);
        }
    }

    /// <summary>Measure string size at an explicit font size (logical pixels).</summary>
    public Vector2 MeasureString(string text, int fontSize) => MeasureSized(text, fontSize);

    /// <summary>Measure string size with scale (scale 1 = 32 px).</summary>
    public Vector2 MeasureString(string text, float scale = 1.0f) => MeasureSized(text, 32f * scale);

    /// <summary>
    /// Measure using the same raster size <see cref="DrawSized"/> would pick right now, so centring
    /// math matches what actually lands on screen.
    /// </summary>
    public Vector2 MeasureSized(string text, float fontSize)
    {
        if (string.IsNullOrEmpty(text) || fontSize <= 0f) return Vector2.Zero;
        int raster = RasterSize(fontSize * PixelScale);
        return Measure(_fontSystem.GetFont(raster), text) * (fontSize / raster);
    }

    /// <summary>
    /// Size-independent measurement: the size of <paramref name="text"/> at a font size of 1,
    /// measured at a high reference size so it scales linearly (used where the caller multiplies
    /// by its own scale afterwards).
    /// </summary>
    public Vector2 MeasureScaled(string text)
    {
        if (string.IsNullOrEmpty(text)) return Vector2.Zero;
        return Measure(_fontSystem.GetFont(ReferenceSize), text) / ReferenceSize;
    }

    /// <summary>
    /// Width from FontStashSharp, height = line count × line height. FontStashSharp's own
    /// MeasureString().Y is the bottom of the glyph ink measured from the line top (e.g. 169 for
    /// "Hello" but 201 for "gyHj" at 200 px), so centring with it pushed text down by a different
    /// amount for every string. Line height matches what <see cref="DrawSized"/> draws from the
    /// top-left and what SpriteFont.MeasureString returned.
    /// </summary>
    private static Vector2 Measure(DynamicSpriteFont font, string text)
    {
        int lines = 1;
        foreach (char c in text) if (c == '\n') lines++;
        return new Vector2(font.MeasureString(text).X, lines * font.LineHeight);
    }
}
