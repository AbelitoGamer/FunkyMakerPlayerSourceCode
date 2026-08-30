using System;
using System.IO;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using FontStashSharp;

namespace FunkyMakerPlayer;

/// <summary>
/// Wrapper for FontStashSharp to provide crisp TrueType font rendering.
/// </summary>
public class FontRenderer
{
    private readonly FontSystem _fontSystem;
    private readonly DynamicSpriteFont _defaultFont;
    
    public FontRenderer(GraphicsDevice graphicsDevice, string fontPath)
    {
        _fontSystem = new FontSystem(new FontSystemSettings
        {
            FontResolutionFactor = 2,  // Higher resolution for better quality
            KernelWidth = 2,
            KernelHeight = 2
        });
        
        // Load the font file
        byte[] fontData = File.ReadAllBytes(fontPath);
        _fontSystem.AddFont(fontData);
        
        // Get default font at size 32 (will be scaled as needed)
        _defaultFont = _fontSystem.GetFont(32);
    }
    
    /// <summary>
    /// Get a font at specific size for sharp rendering.
    /// </summary>
    public DynamicSpriteFont GetFont(int size)
    {
        return _fontSystem.GetFont(size);
    }
    
    /// <summary>
    /// Draw text with automatic size calculation for best quality.
    /// </summary>
    public void DrawString(SpriteBatch spriteBatch, string text, Vector2 position, Color color, float scale = 1.0f)
    {
        // Calculate appropriate font size based on scale
        int fontSize = (int)(32 * scale);
        fontSize = Math.Max(12, Math.Min(fontSize, 128)); // Clamp between 12-128
        
        var font = _fontSystem.GetFont(fontSize);
        font.DrawText(spriteBatch, text, position, color);
    }
    
    /// <summary>
    /// Draw text with explicit font size.
    /// </summary>
    public void DrawString(SpriteBatch spriteBatch, string text, Vector2 position, Color color, int fontSize)
    {
        var font = _fontSystem.GetFont(fontSize);
        font.DrawText(spriteBatch, text, position, color);
    }
    
    /// <summary>
    /// Measure string size.
    /// </summary>
    public Vector2 MeasureString(string text, int fontSize)
    {
        var font = _fontSystem.GetFont(fontSize);
        return font.MeasureString(text);
    }
    
    /// <summary>
    /// Measure string size with scale.
    /// </summary>
    public Vector2 MeasureString(string text, float scale = 1.0f)
    {
        int fontSize = (int)(32 * scale);
        fontSize = Math.Max(12, Math.Min(fontSize, 128));
        
        var font = _fontSystem.GetFont(fontSize);
        return font.MeasureString(text);
    }
}
