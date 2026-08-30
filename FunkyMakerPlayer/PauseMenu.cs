using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using System;

namespace FunkyMakerPlayer;

/// <summary>
/// Pause menu UI with crisp TrueType font rendering.
/// Drawn directly to window after letterboxing for best quality.
/// </summary>
public class PauseMenu
{
    private readonly Texture2D _pixel;
    private readonly SpriteFont _font;
    private readonly FontRenderer? _fontRenderer;
    
    public int Selection { get; set; } = 0;
    public int ItemCount => 4;
    
    public PauseMenu(Texture2D pixel, SpriteFont font, FontRenderer? fontRenderer = null)
    {
        _pixel = pixel;
        _font = font;
        _fontRenderer = fontRenderer;
    }
    
    /// <summary>
    /// Draw pause menu adjusted to letterbox window coordinates.
    /// </summary>
    public void Draw(SpriteBatch spriteBatch, int offsetX, int offsetY, int displayW, int displayH, 
        float scale, int rtWidth, int rtHeight, GameTime gameTime, 
        int score = 0, float accuracy = 100f, int combo = 0, int misses = 0,
        int sicks = 0, int goods = 0, int bads = 0, int shits = 0)
    {
        // Transform render target coordinates to window coordinates
        float TransformX(float rtX) => offsetX + rtX * scale;
        float TransformY(float rtY) => offsetY + rtY * scale;
        float TransformSize(float rtSize) => rtSize * scale;
        
        // Use render target dimensions for layout calculations
        int sw = rtWidth;
        int sh = rtHeight;
        
        // Dark overlay
        DrawRect(spriteBatch, offsetX, offsetY, displayW, displayH, Color.Black * 0.5f);
        
        // Colors
        Color accentColor = new Color(255, 68, 136);
        
        // ── Left panel: Stats ─────────────────────────────────────────────────
        float panelW = TransformSize(240f);
        float panelH = TransformSize(sh - 100f);
        float panelX = TransformX(20f);
        float panelY = TransformY(50f);
        
        DrawRect(spriteBatch, (int)panelX, (int)panelY, (int)panelW, (int)panelH, 
            new Color(20, 18, 28) * 0.85f);
        DrawRect(spriteBatch, (int)panelX, (int)panelY, 3, (int)panelH, accentColor);
        
        // Stats title - RESPONSIVE
        int statsTitleSize = (int)(displayH * 0.045f);
        statsTitleSize = Math.Clamp(statsTitleSize, 24, 56);
        
        if (_fontRenderer != null)
        {
            _fontRenderer.DrawString(spriteBatch, "STATS",
                new Vector2(panelX + TransformSize(15), panelY + TransformSize(15)),
                accentColor, statsTitleSize);
        }
        else
        {
            spriteBatch.DrawString(_font, "STATS",
                new Vector2(panelX + TransformSize(15), panelY + TransformSize(15)),
                accentColor, 0f, Vector2.Zero, 0.9f, SpriteEffects.None, 0f);
        }
        
        float statY = panelY + TransformSize(55f);
        float statSpacing = TransformSize(65f);
        
        // Draw stats - RESPONSIVE
        int statLabelSize = (int)(displayH * 0.032f);
        statLabelSize = Math.Clamp(statLabelSize, 18, 40);
        
        DrawStat(spriteBatch, "SCORE", score.ToString("N0"), panelX + TransformSize(15), statY, accentColor, statLabelSize, TransformSize);
        statY += statSpacing;
        
        Color accColor = accuracy >= 95f ? new Color(0, 255, 100) : 
                        accuracy >= 80f ? new Color(255, 200, 0) : 
                        new Color(255, 100, 100);
        DrawStat(spriteBatch, "ACCURACY", $"{accuracy:F1}%", panelX + TransformSize(15), statY, accColor, statLabelSize, TransformSize);
        statY += statSpacing;
        
        DrawStat(spriteBatch, "COMBO", combo.ToString(), panelX + TransformSize(15), statY, new Color(0, 200, 255), statLabelSize, TransformSize);
        statY += statSpacing;
        
        Color missColor = misses == 0 ? new Color(0, 255, 100) : new Color(255, 100, 100);
        DrawStat(spriteBatch, "MISSES", misses.ToString(), panelX + TransformSize(15), statY, missColor, statLabelSize, TransformSize);
        statY += statSpacing;
        
        // Rating breakdown title
        int ratingTitleSize = (int)(displayH * 0.030f);
        ratingTitleSize = Math.Clamp(ratingTitleSize, 16, 32);
        
        if (_fontRenderer != null)
        {
            _fontRenderer.DrawString(spriteBatch, "RATINGS",
                new Vector2(panelX + TransformSize(15), statY),
                new Color(150, 150, 200), ratingTitleSize);
        }
        else
        {
            spriteBatch.DrawString(_font, "RATINGS",
                new Vector2(panelX + TransformSize(15), statY),
                new Color(150, 150, 200), 0f, Vector2.Zero, 0.7f, SpriteEffects.None, 0f);
        }
        statY += TransformSize(35f);
        
        // Individual ratings
        float ratingSpacing = TransformSize(50f);
        int ratingSize = (int)(displayH * 0.028f);
        ratingSize = Math.Clamp(ratingSize, 16, 36);
        
        DrawStat(spriteBatch, "Sick!", sicks.ToString(), panelX + TransformSize(15), statY, new Color(0, 255, 255), ratingSize, TransformSize);
        statY += ratingSpacing;
        DrawStat(spriteBatch, "Good", goods.ToString(), panelX + TransformSize(15), statY, new Color(0, 255, 0), ratingSize, TransformSize);
        statY += ratingSpacing;
        DrawStat(spriteBatch, "Bad", bads.ToString(), panelX + TransformSize(15), statY, new Color(255, 170, 0), ratingSize, TransformSize);
        statY += ratingSpacing;
        DrawStat(spriteBatch, "Shit", shits.ToString(), panelX + TransformSize(15), statY, new Color(150, 75, 75), ratingSize, TransformSize);
        
        // Menu area
        float menuX = TransformX(sw / 2f - 200f);
        float menuY = TransformY(sh / 2f - 120f);
        
        // "PAUSED" title - RESPONSIVE
        string title = "PAUSED";
        int titleFontSize = (int)(displayH * 0.11f);  // 11% of display height
        titleFontSize = Math.Clamp(titleFontSize, 60, 140);
        
        float titleX = menuX;
        float titleY = menuY - TransformSize(90f);
        
        // Title with shadow
        if (_fontRenderer != null)
        {
            _fontRenderer.DrawString(spriteBatch, title,
                new Vector2(titleX + 4, titleY + 4),
                Color.Black * 0.5f, titleFontSize);
            _fontRenderer.DrawString(spriteBatch, title,
                new Vector2(titleX, titleY),
                Color.White, titleFontSize);
        }
        else
        {
            spriteBatch.DrawString(_font, title,
                new Vector2(titleX + 4, titleY + 4),
                Color.Black * 0.5f, 0f, Vector2.Zero, 2.2f, SpriteEffects.None, 0f);
            spriteBatch.DrawString(_font, title,
                new Vector2(titleX, titleY),
                Color.White, 0f, Vector2.Zero, 2.2f, SpriteEffects.None, 0f);
        }

        // Menu items
        var items = new[]
        {
            ("Resume", "Continue playing"),
            ("Restart Song", "Start from beginning"),
            ("Settings", "Adjust game options"),
            ("Exit to Menu", "Return to song select"),
        };

        float itemH = TransformSize(70f);
        float itemW = TransformSize(420f);
        
        for (int i = 0; i < items.Length; i++)
        {
            var (label, desc) = items[i];
            bool isSelected = (i == Selection);
            
            float itemY = menuY + i * itemH;
            
            // Selection background with animation
            if (isSelected)
            {
                float pulse = 0.5f + 0.5f * MathF.Sin((float)gameTime.TotalGameTime.TotalSeconds * 4f);
                Color selBg = accentColor * (0.25f + pulse * 0.1f);
                DrawRect(spriteBatch, (int)(menuX - TransformSize(10)), (int)(itemY - TransformSize(5)), 
                    (int)(itemW + TransformSize(20)), (int)(itemH - TransformSize(15)), selBg);
                
                DrawRect(spriteBatch, (int)(menuX - TransformSize(10)), (int)(itemY - TransformSize(5)), 
                    4, (int)(itemH - TransformSize(15)), accentColor);
            }
            
            // Item label - RESPONSIVE
            Color labelColor = isSelected ? Color.White : Color.White * 0.6f;
            int labelFontSize = (int)(displayH * 0.050f);
            labelFontSize = Math.Clamp(labelFontSize, 28, 72);
            if (isSelected) labelFontSize += 4;
            
            if (_fontRenderer != null)
            {
                _fontRenderer.DrawString(spriteBatch, label,
                    new Vector2(menuX + TransformSize(10), itemY),
                    labelColor, labelFontSize);
            }
            else
            {
                float labelScale = isSelected ? 1.3f : 1.1f;
                spriteBatch.DrawString(_font, label,
                    new Vector2(menuX + TransformSize(10), itemY),
                    labelColor, 0f, Vector2.Zero, labelScale, SpriteEffects.None, 0f);
            }
            
            // Item description
            if (isSelected)
            {
                int descFontSize = (int)(displayH * 0.028f);
                descFontSize = Math.Clamp(descFontSize, 18, 36);
                
                Vector2 labelMeasure = _fontRenderer != null
                    ? _fontRenderer.MeasureString(label, labelFontSize)
                    : _font.MeasureString(label) * (isSelected ? 1.3f : 1.1f);
                    
                float descY = itemY + labelMeasure.Y + TransformSize(5f);
                
                if (_fontRenderer != null)
                {
                    _fontRenderer.DrawString(spriteBatch, desc,
                        new Vector2(menuX + TransformSize(10), descY),
                        accentColor * 0.7f, descFontSize);
                }
                else
                {
                    spriteBatch.DrawString(_font, desc,
                        new Vector2(menuX + TransformSize(10), descY),
                        accentColor * 0.7f, 0f, Vector2.Zero, 0.7f, SpriteEffects.None, 0f);
                }
            }
            
            // Arrow indicator
            if (isSelected)
            {
                string arrow = ">";
                int arrowSize = (int)(displayH * 0.058f);
                arrowSize = Math.Clamp(arrowSize, 32, 76);
                
                if (_fontRenderer != null)
                {
                    _fontRenderer.DrawString(spriteBatch, arrow,
                        new Vector2(menuX - TransformSize(30), itemY + TransformSize(5)),
                        accentColor, arrowSize);
                }
                else
                {
                    spriteBatch.DrawString(_font, arrow,
                        new Vector2(menuX - TransformSize(30), itemY + TransformSize(5)),
                        accentColor, 0f, Vector2.Zero, 1.5f, SpriteEffects.None, 0f);
                }
            }
        }

        // Bottom instructions
        float bottomY = TransformY(sh - 35f);
        string instructions = "UP/DOWN  Navigate     ENTER  Select     ESC  Resume";
        int instrFontSize = (int)(displayH * 0.030f);
        instrFontSize = Math.Clamp(instrFontSize, 18, 38);
        
        Vector2 instrSize = _fontRenderer != null
            ? _fontRenderer.MeasureString(instructions, instrFontSize)
            : _font.MeasureString(instructions) * 0.75f;
            
        float instrX = offsetX + displayW / 2f - instrSize.X / 2f;
        
        DrawRect(spriteBatch, (int)instrX - 15, (int)bottomY - 8, (int)instrSize.X + 30, 30,
            new Color(20, 20, 35) * 0.7f);
        
        if (_fontRenderer != null)
        {
            _fontRenderer.DrawString(spriteBatch, instructions,
                new Vector2(instrX, bottomY),
                Color.White * 0.5f, instrFontSize);
        }
        else
        {
            spriteBatch.DrawString(_font, instructions,
                new Vector2(instrX, bottomY),
                Color.White * 0.5f, 0f, Vector2.Zero, 0.75f, SpriteEffects.None, 0f);
        }
    }
    
    private void DrawRect(SpriteBatch sb, int x, int y, int w, int h, Color c)
    {
        if (w <= 0 || h <= 0) return;
        sb.Draw(_pixel, new Rectangle(x, y, w, h), c);
    }
    
    private void DrawStat(SpriteBatch sb, string label, string value, float x, float y, Color valueColor, int fontSize, Func<float, float> transformSize)
    {
        // Label
        int labelSize = (int)(fontSize * 0.75f);
        if (_fontRenderer != null)
        {
            _fontRenderer.DrawString(sb, label,
                new Vector2(x, y),
                Color.White * 0.6f, labelSize);
            
            Vector2 labelMeasure = _fontRenderer.MeasureString(label, labelSize);
            
            // Value
            _fontRenderer.DrawString(sb, value,
                new Vector2(x, y + labelMeasure.Y + transformSize(3f)),
                valueColor, fontSize);
        }
        else
        {
            sb.DrawString(_font, label,
                new Vector2(x, y),
                Color.White * 0.6f, 0f, Vector2.Zero, 0.65f, SpriteEffects.None, 0f);
            
            Vector2 labelMeasure = _font.MeasureString(label) * 0.65f;
            
            // Value
            sb.DrawString(_font, value,
                new Vector2(x, y + labelMeasure.Y + transformSize(3f)),
                valueColor, 0f, Vector2.Zero, 0.85f, SpriteEffects.None, 0f);
        }
    }
}
