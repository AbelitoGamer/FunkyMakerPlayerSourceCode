using System;
using System.Collections.Generic;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using Microsoft.Xna.Framework.Input;

namespace FunkyMakerPlayer.UI;

/// <summary>
/// Arrows/NoteStyle selector menu with preview.
/// </summary>
public class ArrowsMenu : IScreenOverlay
{
    private readonly Texture2D _pixel;
    private readonly SpriteFont _font;
    private readonly FontRenderer? _fontRenderer;
    
    public bool IsActive { get; set; } = false;
    public int Selection { get; set; } = 0;
    
    private List<string> _styles = new();
    private NoteStyle? _previewStyle = null;
    
    public ArrowsMenu(Texture2D pixel, SpriteFont font, FontRenderer? fontRenderer = null)
    {
        _pixel = pixel;
        _font = font;
        _fontRenderer = fontRenderer;
    }
    
    public void Open(GraphicsDevice graphicsDevice)
    {
        _styles = NoteStyle.GetAvailableStyles();
        
        // Select currently active style
        Selection = _styles.IndexOf(Game1.NoteStyleName);
        if (Selection < 0) Selection = 0;
        
        LoadPreview(_styles[Selection], graphicsDevice);
        IsActive = true;
    }
    
    public void Close()
    {
        IsActive = false;
        _previewStyle = null;
    }
    
    private void LoadPreview(string styleName, GraphicsDevice graphicsDevice)
    {
        _previewStyle = NoteStyle.LoadByName(styleName, graphicsDevice);
    }
    
    public void Update(KeyboardState kb, KeyboardState prevKb, GraphicsDevice graphicsDevice)
    {
        if (!IsActive) return;
        
        // Navigation
        if (kb.IsKeyDown(Keys.Up) && !prevKb.IsKeyDown(Keys.Up))
        {
            Selection = (Selection - 1 + _styles.Count) % _styles.Count;
            LoadPreview(_styles[Selection], graphicsDevice);
        }
        
        if (kb.IsKeyDown(Keys.Down) && !prevKb.IsKeyDown(Keys.Down))
        {
            Selection = (Selection + 1) % _styles.Count;
            LoadPreview(_styles[Selection], graphicsDevice);
        }
        
        // Confirm selection
        if ((kb.IsKeyDown(Keys.Enter) && !prevKb.IsKeyDown(Keys.Enter)) ||
            (kb.IsKeyDown(Keys.Space) && !prevKb.IsKeyDown(Keys.Space)))
        {
            Game1.NoteStyleName = _styles[Selection];
            Close();
        }
        
        // Cancel
        if (kb.IsKeyDown(Keys.Escape) && !prevKb.IsKeyDown(Keys.Escape))
        {
            Close();
        }
    }
    
    public void Draw(SpriteBatch spriteBatch, int screenWidth, int screenHeight)
    {
        if (!IsActive) return;
        
        int sw = screenWidth;
        int sh = screenHeight;
        
        // Background
        DrawRect(spriteBatch, 0, 0, sw, sh, Color.Black * 0.92f);
        
        // Header
        DrawRect(spriteBatch, 0, 0, sw, 54, new Color(15, 15, 28));
        DrawRect(spriteBatch, 0, 52, sw, 2, new Color(0xFF, 0x44, 0x88) * 0.7f);
        DrawRect(spriteBatch, 0, 0, 4, 54, new Color(0xFF, 0x44, 0x88));
        
        if (_fontRenderer != null)
        {
            int titleSize = (int)(sh * 0.055f);
            titleSize = Math.Clamp(titleSize, 32, 64);
            _fontRenderer.DrawString(spriteBatch, "ARROWS",
                new Vector2(20, 10), new Color(0xFF, 0x44, 0x88), titleSize);
        }
        else
        {
            spriteBatch.DrawString(_font, "ARROWS",
                new Vector2(20, 10), new Color(0xFF, 0x44, 0x88), 0f, Vector2.Zero, 1.3f, SpriteEffects.None, 0f);
        }
        
        // ── Left panel: skin list ─────────────────────────────────────────────
        float listX = 16f;
        float listW = sw * 0.38f;
        float listY = 64f;
        float itemH = 38f;
        
        if (_fontRenderer != null)
        {
            int labelSize = (int)(sh * 0.028f);
            labelSize = Math.Clamp(labelSize, 16, 28);
            _fontRenderer.DrawString(spriteBatch, "SKINS",
                new Vector2(listX + 8, listY), new Color(0xFF, 0x44, 0x88) * 0.6f, labelSize);
        }
        else
        {
            spriteBatch.DrawString(_font, "SKINS",
                new Vector2(listX + 8, listY),
                new Color(0xFF, 0x44, 0x88) * 0.6f, 0f, Vector2.Zero, 0.65f, SpriteEffects.None, 0f);
        }
        listY += 22f;
        
        for (int i = 0; i < _styles.Count; i++)
        {
            float iy = listY + i * itemH;
            bool sel = i == Selection;
            bool cur = _styles[i] == Game1.NoteStyleName;
            
            if (sel)
            {
                DrawRect(spriteBatch, (int)listX, (int)(iy - 2), (int)listW, (int)(itemH - 2),
                    new Color(0xFF, 0x44, 0x88) * 0.18f);
                DrawRect(spriteBatch, (int)listX, (int)(iy - 2), 3, (int)(itemH - 2),
                    new Color(0xFF, 0x44, 0x88));
            }
            
            Color textColor = sel ? Color.White : (cur ? new Color(0xFF, 0xCC, 0x44) : Color.White * 0.6f);
            string label = _styles[i] + (cur ? " (active)" : "");
            
            if (_fontRenderer != null)
            {
                int itemSize = (int)(sh * 0.032f);
                itemSize = Math.Clamp(itemSize, 18, 32);
                _fontRenderer.DrawString(spriteBatch, label,
                    new Vector2(listX + 12, iy + 8), textColor, itemSize);
            }
            else
            {
                spriteBatch.DrawString(_font, label,
                    new Vector2(listX + 12, iy + 8),
                    textColor, 0f, Vector2.Zero, 0.82f, SpriteEffects.None, 0f);
            }
        }
        
        // ── Right panel: strumline preview ────────────────────────────────────
        float previewX = sw * 0.42f;
        float previewW = sw - previewX - 16f;
        float previewCx = previewX + previewW / 2f;
        
        if (_fontRenderer != null)
        {
            int previewLabelSize = (int)(sh * 0.028f);
            previewLabelSize = Math.Clamp(previewLabelSize, 16, 28);
            _fontRenderer.DrawString(spriteBatch, "PREVIEW",
                new Vector2(previewX + 8, 64f), new Color(0xFF, 0x44, 0x88) * 0.6f, previewLabelSize);
        }
        else
        {
            spriteBatch.DrawString(_font, "PREVIEW",
                new Vector2(previewX + 8, 64f),
                new Color(0xFF, 0x44, 0x88) * 0.6f, 0f, Vector2.Zero, 0.65f, SpriteEffects.None, 0f);
        }
        
        // Draw 4 receptors in a row (static state)
        float laneW = NoteRenderer.LANE_WIDTH * 0.85f;
        float totalW = laneW * 4f;
        float startRX = previewCx - totalW / 2f;
        float receptY = sh / 2f;
        
        var style = _previewStyle;
        
        for (int lane = 0; lane < 4; lane++)
        {
            float cx = startRX + lane * laneW + laneW / 2f;
            
            if (style?.ReceptorFrames[lane, 0] is List<AtlasFrame> frames && frames.Count > 0)
            {
                var f = frames[0];
                float s = (style.ReceptorScale * 0.85f);
                float drawX = cx - f.FrameW * s / 2f + (-f.OffsetX) * s;
                float drawY = receptY - f.FrameH * s / 2f + (-f.OffsetY) * s;
                spriteBatch.Draw(f.Texture, new Vector2(drawX, drawY), f.Source,
                    Color.White * 0.7f, 0f, Vector2.Zero, s, SpriteEffects.None, 0f);
            }
            else
            {
                // Fallback colored rect
                Color[] lc = { new Color(0xC2, 0x4B, 0x99), new Color(0, 0xFF, 0xFF),
                               new Color(0x12, 0xFA, 0x05), new Color(0xF9, 0x39, 0x3F) };
                DrawRect(spriteBatch, (int)(cx - laneW * 0.4f), (int)(receptY - 20),
                    (int)(laneW * 0.8f), 40, lc[lane] * 0.5f);
            }
            
            // Note arrow below receptor
            float noteY = receptY + 80f;
            if (style?.GetNoteFrame(lane, 0) is AtlasFrame nf)
            {
                float s = (style.NoteScale * 0.85f);
                float drawX = cx - nf.FrameW * s / 2f + (-nf.OffsetX) * s;
                float drawY = noteY - nf.FrameH * s / 2f + (-nf.OffsetY) * s;
                spriteBatch.Draw(nf.Texture, new Vector2(drawX, drawY), nf.Source,
                    Color.White, 0f, Vector2.Zero, s, SpriteEffects.None, 0f);
            }
        }
        
        // Footer hint
        string hint = "UP/DOWN  Select    ENTER  Apply    ESC  Back";
        if (_fontRenderer != null)
        {
            int hintSize = (int)(sh * 0.023f);
            hintSize = Math.Clamp(hintSize, 12, 22);
            Vector2 hintMeasure = _fontRenderer.MeasureString(hint, hintSize);
            _fontRenderer.DrawString(spriteBatch, hint,
                new Vector2(sw / 2f - hintMeasure.X / 2f, sh - 22),
                Color.White * 0.2f, hintSize);
        }
        else
        {
            var hintSz = _font.MeasureString(hint) * 0.55f;
            spriteBatch.DrawString(_font, hint,
                new Vector2(sw / 2f - hintSz.X / 2f, sh - 22),
                Color.White * 0.2f, 0f, Vector2.Zero, 0.55f, SpriteEffects.None, 0f);
        }
    }
    
    private void DrawRect(SpriteBatch sb, int x, int y, int w, int h, Color c)
    {
        if (w <= 0 || h <= 0) return;
        sb.Draw(_pixel, new Rectangle(x, y, w, h), c);
    }
}
