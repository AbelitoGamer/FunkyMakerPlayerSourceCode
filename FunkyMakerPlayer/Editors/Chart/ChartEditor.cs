using System;
using System.IO;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using Microsoft.Xna.Framework.Input;
using FunkyMakerPlayer.Editors.Common;

namespace FunkyMakerPlayer.Editors.Chart;

/// <summary>
/// Chart Editor — Edit notes, sections, BPM, events, etc.
/// Phase 1: Basic skeleton showing level info (full editing in future phases).
/// </summary>
public class ChartEditor : IEditor
{
    private readonly Texture2D _pixel;
    private readonly SpriteFont? _font;
    private readonly FontRenderer? _fontRenderer;
    private readonly string _levelPath;
    
    private FnmmLevel? _level;
    private KeyboardState _prevKb;
    private bool _backPressed;
    private bool _isModified;
    
    public bool BackPressed => _backPressed;
    public bool IsModified => _isModified;
    
    public ChartEditor(string levelPath, Texture2D pixel, SpriteFont? font, FontRenderer? fontRenderer)
    {
        _levelPath = levelPath;
        _pixel = pixel;
        _font = font;
        _fontRenderer = fontRenderer;
        
        LoadLevel();
    }
    
    private void LoadLevel()
    {
        try
        {
            Console.WriteLine($"[ChartEditor] Loading level: {_levelPath}");
            _level = FnmmLoader.Load(_levelPath);
            Console.WriteLine($"[ChartEditor] ✅ Loaded: {_level.LevelName}");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[ChartEditor] ❌ Failed to load level: {ex.Message}");
            _level = null;
        }
    }
    
    public void Update(float dt, KeyboardState kb)
    {
        bool esc = kb.IsKeyDown(Keys.Escape) && !_prevKb.IsKeyDown(Keys.Escape);
        
        if (esc)
        {
            _backPressed = true;
            Console.WriteLine($"[ChartEditor] Back pressed");
        }
        
        _prevKb = kb;
    }
    
    public void Draw(SpriteBatch sb, int screenWidth, int screenHeight)
    {
        // Modern dark background
        DrawRect(sb, 0, 0, screenWidth, screenHeight, new Color(20, 18, 28));
        
        // Subtle pattern overlay
        for (int y = 0; y < screenHeight; y += 120)
        {
            DrawRect(sb, 0, y, screenWidth, 1, Color.White * 0.02f);
        }
        for (int x = 0; x < screenWidth; x += 120)
        {
            DrawRect(sb, x, 0, 1, screenHeight, Color.White * 0.02f);
        }
        
        if (_level == null)
        {
            // Error state
            string errorMsg = "Failed to load level";
            int errorSize = 32;
            var errorSz = MeasureText(errorMsg, errorSize);
            DrawText(sb, errorMsg, new Vector2(screenWidth / 2f - errorSz.X / 2f, screenHeight / 2f - errorSz.Y / 2f),
                Color.Red, errorSize);
            
            string hintMsg = "Press ESC to return";
            int hintSize = 20;
            var hintSz = MeasureText(hintMsg, hintSize);
            DrawText(sb, hintMsg, new Vector2(screenWidth / 2f - hintSz.X / 2f, screenHeight / 2f + errorSz.Y),
                Color.White * 0.6f, hintSize);
            return;
        }
        
        // Title
        string title = "CHART EDITOR";
        int titleSize = Math.Clamp((int)(screenHeight * 0.065f), 32, 72);
        var titleSz = MeasureText(title, titleSize);
        float titleX = screenWidth / 2f - titleSz.X / 2f;
        float titleY = screenHeight * 0.10f;
        
        // Glow effect
        Color glowColor = new Color(255, 68, 136) * 0.15f;
        DrawText(sb, title, new Vector2(titleX + 2, titleY + 2), glowColor, titleSize);
        DrawText(sb, title, new Vector2(titleX, titleY), new Color(255, 215, 0), titleSize);
        
        // Level info card
        int cardW = Math.Clamp((int)(screenWidth * 0.60f), 400, 800);
        int cardH = Math.Clamp((int)(screenHeight * 0.50f), 300, 500);
        int cardX = screenWidth / 2 - cardW / 2;
        int cardY = (int)(titleY + titleSz.Y + 60);
        
        // Card background with accent border
        Color accentColor = new Color(255, 68, 136);
        DrawRect(sb, cardX - 3, cardY - 3, cardW + 6, cardH + 6, accentColor * 0.6f);
        DrawRect(sb, cardX, cardY, cardW, cardH, new Color(30, 30, 50) * 0.8f);
        
        // Level info text
        int infoSize = Math.Clamp((int)(screenHeight * 0.030f), 18, 36);
        float infoY = cardY + 30;
        float lineHeight = MeasureText("A", infoSize).Y + 10;
        
        DrawInfoLine(sb, cardX + 30, ref infoY, "Level Name:", _level.LevelName, infoSize, lineHeight);
        DrawInfoLine(sb, cardX + 30, ref infoY, "BPM:", _level.Data.Bpm.ToString("F1"), infoSize, lineHeight);
        DrawInfoLine(sb, cardX + 30, ref infoY, "Strumlines:", _level.Data.Strumlines.Count.ToString(), infoSize, lineHeight);
        
        // Count notes per strumline
        for (int i = 0; i < _level.Data.Strumlines.Count; i++)
        {
            int totalNotes = 0;
            var strumline = _level.Data.Strumlines[i];
            foreach (var lane in strumline)
            {
                totalNotes += lane.Count;
            }
            DrawInfoLine(sb, cardX + 50, ref infoY, $"  Strumline {i}:", $"{totalNotes} notes", infoSize, lineHeight);
        }
        
        DrawInfoLine(sb, cardX + 30, ref infoY, "Objects:", _level.Scene.Objects.Count.ToString(), infoSize, lineHeight);
        DrawInfoLine(sb, cardX + 30, ref infoY, "Events:", _level.Events.Events.Count.ToString(), infoSize, lineHeight);
        
        // WIP notice
        infoY += 20;
        string wipMsg = "Full editing features coming soon!";
        int wipSize = Math.Clamp((int)(screenHeight * 0.025f), 16, 28);
        var wipSz = MeasureText(wipMsg, wipSize);
        DrawText(sb, wipMsg, new Vector2(cardX + cardW / 2f - wipSz.X / 2f, infoY),
            new Color(255, 200, 0), wipSize);
        
        // Bottom hint
        string bottomHint = "ESC Return to Editor Hub";
        int bottomHintSize = Math.Clamp((int)(screenHeight * 0.023f), 13, 24);
        var bottomHintSz = MeasureText(bottomHint, bottomHintSize);
        float bottomHintY = Math.Max(screenHeight - screenHeight * 0.049f, screenHeight - 40);
        DrawText(sb, bottomHint, new Vector2(screenWidth / 2f - bottomHintSz.X / 2f, bottomHintY),
            Color.White * 0.35f, bottomHintSize);
    }
    
    private void DrawInfoLine(SpriteBatch sb, float x, ref float y, string label, string value, int size, float lineHeight)
    {
        DrawText(sb, label, new Vector2(x, y), Color.White * 0.7f, size);
        var labelSz = MeasureText(label, size);
        DrawText(sb, value, new Vector2(x + labelSz.X + 10, y), new Color(255, 215, 0), size);
        y += lineHeight;
    }
    
    private void DrawRect(SpriteBatch sb, int x, int y, int w, int h, Color color)
    {
        sb.Draw(_pixel, new Rectangle(x, y, w, h), color);
    }
    
    private Vector2 MeasureText(string text, int size)
    {
        if (_fontRenderer != null)
        {
            return _fontRenderer.MeasureString(text, size);
        }
        else if (_font != null)
        {
            float scale = size / 32f;
            var baseSz = _font.MeasureString(text);
            return baseSz * scale;
        }
        return new Vector2(text.Length * size * 0.5f, size);
    }
    
    private void DrawText(SpriteBatch sb, string text, Vector2 pos, Color color, int size)
    {
        if (_fontRenderer != null)
        {
            _fontRenderer.DrawString(sb, text, pos, color, size);
        }
        else if (_font != null)
        {
            float scale = size / 32f;
            sb.DrawString(_font, text, pos, color, 0f, Vector2.Zero, scale, SpriteEffects.None, 0f);
        }
    }
}
