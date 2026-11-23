using System;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;

namespace FunkyMakerPlayer.UI;

/// <summary>
/// Song timer bar component with multiple display styles (Legacy, V-Slice).
/// Supports horizontal and vertical orientations with customizable colors.
/// </summary>
public class TimerBar
{
    private readonly Texture2D _pixel;
    private readonly SpriteBatch _spriteBatch;
    private readonly SpriteFont _font;
    
    // Configuration (from Game1 static fields)
    public bool ShowTimer { get; set; } = true;
    public string Preset { get; set; } = "HUD Editor";  // Legacy, HUD Editor, Top, Left, Middle, Right, Strumline
    public string TextMode { get; set; } = "Time Left";  // Time Left, Time Elapsed, Song Name, Song Name + Difficulty, None
    /// <summary>Star rating shown by the "Song Name + Difficulty" text mode (null = unknown).</summary>
    public double? Stars { get; set; }
    public bool ForceTop { get; set; } = false;
    public int ColorPreset { get; set; } = 0;  // 0=white, 1=cyan, 2=pink, 3=yellow, 4=green, 5=player, 6=opponent
    
    // HUD Editor position/size (from HudSettings)
    public Point Position { get; set; } = new Point(340, 4);
    public Point Size { get; set; } = new Point(600, 32);
    
    public TimerBar(Texture2D pixel, SpriteBatch spriteBatch, SpriteFont font)
    {
        _pixel = pixel;
        _spriteBatch = spriteBatch;
        _font = font;
    }
    
    /// <summary>
    /// Draws the timer bar based on current settings.
    /// </summary>
    public void Draw(int screenWidth, int screenHeight, float songTime, float songEndTime, 
                    bool downscroll, string? levelName, Color? playerBarColor, Color? opponentBarColor, bool started = true)
    {
        if (!ShowTimer || !started || songEndTime <= 0f || songEndTime >= float.MaxValue) return;
        
        if (Preset == "Legacy" || Preset == "HUD Editor")
        {
            DrawLegacyTimer(screenWidth, screenHeight, songTime, songEndTime, levelName, playerBarColor, opponentBarColor);
        }
        else
        {
            DrawVSliceTimer(screenWidth, screenHeight, songTime, songEndTime, downscroll, levelName, playerBarColor, opponentBarColor);
        }
    }
    
    /// <summary>Text inside the bar for the current <see cref="TextMode"/> (every preset uses it).</summary>
    private string GetText(float songTime, float songEndTime, string? levelName)
    {
        float elapsed = Math.Max(0f, songTime);
        float left = Math.Max(0f, songEndTime - songTime);
        return TextMode switch
        {
            "Time Elapsed" => $"{(int)(elapsed / 60)}:{(int)(elapsed % 60):D2}",
            "Song Name" => levelName ?? "Unknown",
            "Song Name + Difficulty" => Stars is double st
                ? $"{levelName ?? "Unknown"} ({st.ToString("0.00", System.Globalization.CultureInfo.InvariantCulture)}*)"
                : levelName ?? "Unknown",
            "None" => "",
            _ => $"{(int)(left / 60)}:{(int)(left % 60):D2}",   // Time Left
        };
    }

    private void DrawLegacyTimer(int sw, int sh, float songTime, float songEndTime, string? levelName,
                                Color? playerBarColor, Color? opponentBarColor)
    {
        float progress = Math.Clamp(songTime / songEndTime, 0f, 1f);
        string timeStr = GetText(songTime, songEndTime, levelName);
        float labelScale = 0.9f;
        var labelSize = _font.MeasureString(timeStr) * labelScale;
        
        // Use custom position/size
        int barW = Size.X;
        int barH = Size.Y;
        int barX = Position.X;
        int barY = Position.Y;
        
        // Fill color from preset
        Color timerFill = GetTimerColor(playerBarColor, opponentBarColor);
        
        // Outer shadow/glow
        DrawRect(barX - 3, barY - 3, barW + 6, barH + 6, Color.Black * 0.5f);
        
        // Dark background bar with border
        DrawRect(barX, barY, barW, barH, new Color(20, 20, 20, 220));
        
        // Thin border (highlight)
        DrawRect(barX, barY, barW, 2, Color.White * 0.3f); // Top
        DrawRect(barX, barY + barH - 2, barW, 2, Color.Black * 0.5f); // Bottom
        DrawRect(barX, barY, 2, barH, Color.White * 0.2f); // Left
        DrawRect(barX + barW - 2, barY, 2, barH, Color.Black * 0.4f); // Right
        
        // Fill from left with gradient effect
        int fillW = (int)(barW * progress);
        if (fillW > 0)
        {
            // Draw fill in segments for gradient
            int segments = Math.Min(fillW, 20);
            int segmentWidth = Math.Max(1, fillW / segments);
            
            for (int i = 0; i < segments; i++)
            {
                float t = i / (float)segments;
                float gradientFactor = 0.7f + (t * 0.3f); // Darker at start, brighter at end
                
                int x = barX + (i * segmentWidth);
                int w = (i == segments - 1) ? (fillW - (i * segmentWidth)) : segmentWidth;
                
                DrawRect(x, barY + 2, w, barH - 4, timerFill * gradientFactor);
            }
            
            // Add a glow effect on the right edge of the fill
            if (fillW < barW - 4)
            {
                DrawRect(barX + fillW, barY + 2, 3, barH - 4, timerFill * 0.5f);
            }
        }
        
        if (timeStr.Length == 0) return;

        // Time text with shadow for readability (centered on the bar)
        float textX = barX + barW / 2f - labelSize.X / 2f;
        float textY = barY + (barH - labelSize.Y) / 2f;
        
        // Text shadow
        _spriteBatch.DrawString(_font, timeStr,
            new Vector2(textX + 2, textY + 2),
            Color.Black * 0.8f, 0f, Vector2.Zero, labelScale, SpriteEffects.None, 0f);
        
        // Main text
        _spriteBatch.DrawString(_font, timeStr,
            new Vector2(textX, textY),
            Color.White, 0f, Vector2.Zero, labelScale, SpriteEffects.None, 0f);
    }
    
    private void DrawVSliceTimer(int sw, int sh, float songTime, float songEndTime, bool downscroll, 
                                string? levelName, Color? playerBarColor, Color? opponentBarColor)
    {
        float progress = Math.Clamp(songTime / songEndTime, 0f, 1f);
        
        string timeStr = GetText(songTime, songEndTime, levelName);
        
        Color timerFill = GetTimerColor(playerBarColor, opponentBarColor);
        
        // Bar dimensions (matching V-Slice standard sizes)
        int barW = 400;  // Content width
        int barH = 20;   // Content height
        int barX = sw / 2 - barW / 2;
        int barY = 10;
        
        bool isVertical = Preset == "Left" || Preset == "Right";
        
        // Calculate position based on preset
        switch (Preset)
        {
            case "Top":
                barY = (ForceTop || !downscroll) ? 10 : sh - barH - 10;
                break;
            case "Middle":
                barX = sw / 2 - barW / 2;
                barY = sh / 2 - barH / 2;
                break;
            case "Right":
                // Vertical bar on the right
                barX = sw - barH * 2;
                barY = sh / 2 - barW / 2;
                break;
            case "Left":
                // Vertical bar on the left
                barX = barH;
                barY = sh / 2 - barW / 2;
                break;
            case "Strumline":
                // Above player strumline
                barX = sw / 2 - barW / 2;
                barY = downscroll ? 100 : sh - 200;
                break;
        }
        
        if (isVertical)
        {
            DrawTimerBar_Vertical(barX, barY, barW, barH, progress, timeStr, timerFill);
        }
        else
        {
            DrawTimerBar_Horizontal(barX, barY, barW, barH, progress, timeStr, timerFill);
        }
    }
    
    private void DrawTimerBar_Horizontal(int barX, int barY, int barW, int barH, float progress, string timeStr, Color timerFill)
    {
        // Background (black border + dark gray fill) - matching Haxe with 4px padding on each side
        int bgW = barW + 8;  // +4px on each side
        int bgH = barH + 8;
        
        DrawRect(barX - 4, barY - 4, bgW, bgH, Color.Black * 0.8f);
        DrawRect(barX, barY, barW, barH, new Color(20, 20, 20, 220));
        
        // Fill (left to right)
        int fillW = (int)(barW * progress);
        if (fillW > 0)
            DrawRect(barX, barY, fillW, barH, timerFill);
        
        // Text (centered on bar)
        if (!string.IsNullOrEmpty(timeStr))
        {
            float labelScale = 0.6f;
            var labelSize = _font.MeasureString(timeStr) * labelScale;
            float textX = barX + (barW - labelSize.X) / 2f;
            float textY = barY + (barH - labelSize.Y) / 2f;
            
            // Outline (2px border like Haxe)
            _spriteBatch.DrawString(_font, timeStr, new Vector2(textX + 2, textY), Color.Black, 0f, Vector2.Zero, labelScale, SpriteEffects.None, 0f);
            _spriteBatch.DrawString(_font, timeStr, new Vector2(textX - 2, textY), Color.Black, 0f, Vector2.Zero, labelScale, SpriteEffects.None, 0f);
            _spriteBatch.DrawString(_font, timeStr, new Vector2(textX, textY + 2), Color.Black, 0f, Vector2.Zero, labelScale, SpriteEffects.None, 0f);
            _spriteBatch.DrawString(_font, timeStr, new Vector2(textX, textY - 2), Color.Black, 0f, Vector2.Zero, labelScale, SpriteEffects.None, 0f);
            
            // Main text
            _spriteBatch.DrawString(_font, timeStr, new Vector2(textX, textY), Color.White, 0f, Vector2.Zero, labelScale, SpriteEffects.None, 0f);
        }
    }
    
    private void DrawTimerBar_Vertical(int barX, int barY, int barW, int barH, float progress, string timeStr, Color timerFill)
    {
        // For vertical bars: swap W and H visually (rotate 90 degrees)
        // The bar goes from bottom to top (matching Haxe BOTTOM_TO_TOP fill)
        
        // Background (with 4px padding like Haxe)
        int bgW = barH + 8;  // Swapped because vertical
        int bgH = barW + 8;
        
        DrawRect(barX - 4, barY - 4, bgW, bgH, Color.Black * 0.8f);
        DrawRect(barX, barY, barH, barW, new Color(20, 20, 20, 220));
        
        // Fill (bottom to top)
        int fillH = (int)(barW * progress);
        if (fillH > 0)
        {
            int fillY = barY + barW - fillH;
            DrawRect(barX, fillY, barH, fillH, timerFill);
        }
        
        // Text (positioned next to the bar like Haxe does)
        if (!string.IsNullOrEmpty(timeStr))
        {
            float labelScale = 0.6f;
            var labelSize = _font.MeasureString(timeStr) * labelScale;
            
            // Position text next to the bar based on preset
            float textX = Preset == "Left" ? barX + bgW + 5 : barX - labelSize.X - 5;
            float textY = barY + (barW - labelSize.Y) / 2f;
            
            // Outline (2px border)
            _spriteBatch.DrawString(_font, timeStr, new Vector2(textX + 2, textY), Color.Black, 0f, Vector2.Zero, labelScale, SpriteEffects.None, 0f);
            _spriteBatch.DrawString(_font, timeStr, new Vector2(textX - 2, textY), Color.Black, 0f, Vector2.Zero, labelScale, SpriteEffects.None, 0f);
            _spriteBatch.DrawString(_font, timeStr, new Vector2(textX, textY + 2), Color.Black, 0f, Vector2.Zero, labelScale, SpriteEffects.None, 0f);
            _spriteBatch.DrawString(_font, timeStr, new Vector2(textX, textY - 2), Color.Black, 0f, Vector2.Zero, labelScale, SpriteEffects.None, 0f);
            
            // Main text
            _spriteBatch.DrawString(_font, timeStr, new Vector2(textX, textY), Color.White, 0f, Vector2.Zero, labelScale, SpriteEffects.None, 0f);
        }
    }
    
    private Color GetTimerColor(Color? playerBarColor, Color? opponentBarColor)
    {
        return ColorPreset switch
        {
            1 => new Color(0x00, 0xFF, 0xFF),       // Cyan
            2 => new Color(0xFF, 0x69, 0xB4),       // Pink
            3 => new Color(0xFF, 0xD7, 0x00),       // Yellow
            4 => new Color(0x66, 0xFF, 0x66),       // Green
            5 => playerBarColor ?? new Color(0x66, 0xFF, 0x66),     // Player color
            6 => opponentBarColor ?? new Color(0xFF, 0x00, 0x00),   // Opponent color
            _ => Color.White,
        };
    }
    
    private void DrawRect(int x, int y, int w, int h, Color color)
    {
        if (w <= 0 || h <= 0) return;
        _spriteBatch.Draw(_pixel, new Rectangle(x, y, w, h), color);
    }
}
