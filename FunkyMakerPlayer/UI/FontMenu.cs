using System;
using System.Collections.Generic;
using System.IO;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using Microsoft.Xna.Framework.Input;

namespace FunkyMakerPlayer.UI;

/// <summary>
/// Font selector menu with live preview.
/// </summary>
public class FontMenu : IScreenOverlay
{
    private readonly Texture2D _pixel;
    private readonly SpriteFont _font;
    private readonly FontRenderer? _fontRenderer;
    
    public bool IsActive { get; set; } = false;
    public int Selection { get; set; } = 0;
    public bool SelectionConfirmed { get; private set; } = false;
    private readonly PickerTouch _touch = new(); // taps / clicks (fork)
    
    private List<(string name, string path)> _fonts = new();
    private FontRenderer? _previewFont = null;
    private string _selectedFontPath = "";
    
    public FontMenu(Texture2D pixel, SpriteFont font, FontRenderer? fontRenderer = null)
    {
        _pixel = pixel;
        _font = font;
        _fontRenderer = fontRenderer;
    }
    
    public void Open(GraphicsDevice graphicsDevice)
    {
        ScanAvailableFonts();
        
        // The default font is listed first
        Selection = 0;
        SelectionConfirmed = false;
        
        if (_fonts.Count > 0)
            LoadPreview(_fonts[Selection].path, graphicsDevice);
        _touch.Sync();
        IsActive = true;
    }
    
    public void Close()
    {
        IsActive = false;
        _previewFont = null;
    }
    
    private void ScanAvailableFonts()
    {
        _fonts.Clear();
        string defaultName = Path.GetFileNameWithoutExtension(DefaultMods.FontFileName);
        
        // Every .otf/.ttf in mods/fonts/ — including the default MilkyNice.otf, listed first
        try
        {
            string fontsDir = PlatformPaths.GetFontsPath();
            if (Directory.Exists(fontsDir))
            {
                var files = Directory.GetFiles(fontsDir);
                Array.Sort(files, StringComparer.OrdinalIgnoreCase);
                foreach (var file in files)
                {
                    string ext = Path.GetExtension(file).ToLowerInvariant();
                    if (ext != ".otf" && ext != ".ttf") continue;
                    
                    string name = Path.GetFileNameWithoutExtension(file);
                    if (Path.GetFileName(file).Equals(DefaultMods.FontFileName, StringComparison.OrdinalIgnoreCase))
                        _fonts.Insert(0, ($"{name} (Default)", file));
                    else
                        _fonts.Add((name, file));
                }
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[FontMenu] Error scanning fonts: {ex.Message}");
        }
        
        // Player deleted the default font from mods/fonts/ → offer the shipped copy
        bool hasDefault = _fonts.Count > 0 && _fonts[0].name.EndsWith("(Default)", StringComparison.Ordinal);
        if (!hasDefault)
        {
            string? bundled = DefaultMods.Bundled("fonts", DefaultMods.FontFileName);
            if (bundled != null && File.Exists(bundled))
                _fonts.Insert(0, ($"{defaultName} (Default)", bundled));
        }
        
        // Fallback if no fonts found
        if (_fonts.Count == 0)
        {
            _fonts.Add(("Default", ""));
        }
    }
    
    private void LoadPreview(string fontPath, GraphicsDevice graphicsDevice)
    {
        _previewFont = null;
        _selectedFontPath = fontPath;
        
        if (!string.IsNullOrEmpty(fontPath) && File.Exists(fontPath))
        {
            try
            {
                _previewFont = new FontRenderer(graphicsDevice, fontPath);
                Console.WriteLine($"[FontMenu] Loaded preview font: {fontPath}");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[FontMenu] Error loading preview font: {ex.Message}");
            }
        }
    }
    
    public void Update(KeyboardState kb, KeyboardState prevKb, GraphicsDevice graphicsDevice)
    {
        if (!IsActive) return;
        

        // Taps / clicks (fork): a row previews it, the previewed row again or APPLY applies, BACK cancels.
        var tap = _touch.Poll(Selection);
        if (tap.Select is int pick && pick >= 0 && pick < _fonts.Count && pick != Selection)
        {
            Selection = pick;
            LoadPreview(_fonts[Selection].path, graphicsDevice);
        }
        // Navigation
        if (_fonts.Count > 0 && kb.IsKeyDown(Keys.Up) && !prevKb.IsKeyDown(Keys.Up))
        {
            Selection = (Selection - 1 + _fonts.Count) % _fonts.Count;
            LoadPreview(_fonts[Selection].path, graphicsDevice);
        }
        
        if (_fonts.Count > 0 && kb.IsKeyDown(Keys.Down) && !prevKb.IsKeyDown(Keys.Down))
        {
            Selection = (Selection + 1) % _fonts.Count;
            LoadPreview(_fonts[Selection].path, graphicsDevice);
        }
        
        // Confirm selection
        if (tap.Confirm ||
            (kb.IsKeyDown(Keys.Enter) && !prevKb.IsKeyDown(Keys.Enter)) ||
            (kb.IsKeyDown(Keys.Space) && !prevKb.IsKeyDown(Keys.Space)))
        {
            // Font selection confirmed
            SelectionConfirmed = _fonts.Count > 0 && !string.IsNullOrEmpty(_selectedFontPath);
            Close();
        }
        
        // Cancel
        if (tap.Cancel || (kb.IsKeyDown(Keys.Escape) && !prevKb.IsKeyDown(Keys.Escape)))
        {
            Close();
        }
    }
    
    public string GetSelectedFontPath()
    {
        return _selectedFontPath;
    }
    
    public void Draw(SpriteBatch spriteBatch, int screenWidth, int screenHeight)
    {
        if (!IsActive) return;
        
        int sw = screenWidth;
        int sh = screenHeight;
        
        // Background
        DrawRect(spriteBatch, 0, 0, sw, sh, Color.Black * 0.92f);
        
        // Header (fork: scales with the screen; it was fixed pixels, tiny rows on phones)
        float u = PickerTouch.Scale(sh);
        int hh = PickerTouch.HeaderHeight(sh);
        _touch.BeginRows();
        for (int y = 0; y < hh; y++)
        {
            float factor = (float)y / hh;
            Color barColor = Color.Lerp(new Color(25, 15, 35), new Color(15, 15, 28), factor);
            DrawRect(spriteBatch, 0, y, sw, 1, barColor);
        }
        
        DrawRect(spriteBatch, 0, hh - 2, sw, 3, new Color(0xFF, 0x44, 0x88) * 0.8f);
        
        // Title
        if (_fontRenderer != null)
        {
            int titleSize = Math.Clamp((int)(sh * 0.055f), 32, 64);
            _fontRenderer.DrawString(spriteBatch, "SELECT FONT",
                new Vector2(20 * u, 6 * u), new Color(0xFF, 0x44, 0x88), titleSize);
        }
        else
        {
            spriteBatch.DrawString(_font, "SELECT FONT",
                new Vector2(20 * u, 6 * u), new Color(0xFF, 0x44, 0x88), 0f, Vector2.Zero, 1.1f, SpriteEffects.None, 0f);
        }
        
        _touch.DrawHeaderButtons(spriteBatch, _pixel, _font, _fontRenderer, sw, sh, new Color(0xFF, 0x44, 0x88));

        // List panel
        float panelX = sw * 0.08f;
        float panelW = sw * 0.35f;
        float panelY = hh + 26f * u;
        float panelH = sh - panelY - 40f * u;
        
        DrawRect(spriteBatch, (int)panelX - 5, (int)panelY - 5, (int)panelW + 10, (int)panelH + 10, 
            new Color(15, 15, 25) * 0.8f);
        
        // List items
        float itemH = 48f * u;
        float listY = panelY + 10f * u;
        
        for (int i = 0; i < _fonts.Count; i++)
        {
            bool sel = i == Selection;
            float y = listY + i * itemH;
            
            _touch.AddRow(i, new Rectangle((int)panelX, (int)y - 2, (int)panelW, (int)itemH - 4));
            if (sel)
            {
                DrawRect(spriteBatch, (int)panelX, (int)y - 2, (int)panelW, (int)itemH - 4, 
                    new Color(0xFF, 0x44, 0x88) * 0.3f);
                DrawRect(spriteBatch, (int)panelX, (int)y - 2, 4, (int)itemH - 4, 
                    new Color(0xFF, 0x44, 0x88));
            }
            
            Color textColor = sel ? Color.White : Color.White * 0.7f;
            
            if (_fontRenderer != null)
            {
                int textSize = Math.Clamp((int)(itemH * 0.5f), 18, 40);
                _fontRenderer.DrawString(spriteBatch, _fonts[i].name,
                    new Vector2(panelX + 10 * u, y + 8 * u), textColor, textSize);
            }
            else
            {
                spriteBatch.DrawString(_font, _fonts[i].name,
                    new Vector2(panelX + 10 * u, y + 8 * u), textColor, 0f, Vector2.Zero, 0.7f, SpriteEffects.None, 0f);
            }
        }
        
        // Preview panel
        float previewX = panelX + panelW + 40f;
        float previewW = sw - previewX - 40f;
        float previewY = panelY;
        float previewH = panelH;
        
        DrawRect(spriteBatch, (int)previewX - 5, (int)previewY - 5, (int)previewW + 10, (int)previewH + 10, 
            new Color(15, 15, 25) * 0.8f);
        
        // Preview text
        if (_previewFont != null)
        {
            float textY = previewY + 20f;
            Color textColor = Color.White;
            
            string[] samples = {
                "The quick brown fox",
                "jumps over the lazy dog",
                "",
                "ABCDEFGHIJKLMNOPQRSTUVWXYZ",
                "abcdefghijklmnopqrstuvwxyz",
                "0123456789",
                "",
                "Combo: 123",
                "Sick!! · Good · Bad · Shit",
                "",
                "Settings · Gameplay · Graphics"
            };
            
            int baseSize = Math.Clamp((int)(previewH * 0.05f), 20, 40);
            
            foreach (var sample in samples)
            {
                if (string.IsNullOrEmpty(sample))
                {
                    textY += baseSize * 0.5f;
                    continue;
                }
                
                _previewFont.DrawString(spriteBatch, sample,
                    new Vector2(previewX + 10, textY), textColor, baseSize);
                
                textY += baseSize + 8;
                
                if (textY > previewY + previewH - 40)
                    break;
            }
        }
        else
        {
            // No preview available
            string noPreview = "Font preview unavailable";
            Color noPreviewColor = Color.White * 0.4f;
            
            if (_fontRenderer != null)
            {
                int size = Math.Clamp((int)(previewH * 0.04f), 16, 24);
                Vector2 measure = _fontRenderer.MeasureString(noPreview, size);
                _fontRenderer.DrawString(spriteBatch, noPreview,
                    new Vector2(previewX + (previewW - measure.X) / 2f, previewY + previewH / 2f - measure.Y / 2f),
                    noPreviewColor, size);
            }
            else
            {
                Vector2 measure = _font.MeasureString(noPreview) * 0.6f;
                spriteBatch.DrawString(_font, noPreview,
                    new Vector2(previewX + (previewW - measure.X) / 2f, previewY + previewH / 2f - measure.Y / 2f),
                    noPreviewColor, 0f, Vector2.Zero, 0.6f, SpriteEffects.None, 0f);
            }
        }
        
        // Instructions
        string hint = PickerTouch.HintText;
        Color hintColor = Color.White * 0.5f;
        
        if (_fontRenderer != null)
        {
            int hintSize = Math.Clamp((int)(sh * 0.022f), 14, 22);
            Vector2 hintMeasure = _fontRenderer.MeasureString(hint, hintSize);
            _fontRenderer.DrawString(spriteBatch, hint,
                new Vector2(sw / 2f - hintMeasure.X / 2f, sh - 26), hintColor, hintSize);
        }
        else
        {
            Vector2 hintMeasure = _font.MeasureString(hint) * 0.5f;
            spriteBatch.DrawString(_font, hint,
                new Vector2(sw / 2f - hintMeasure.X / 2f, sh - 26), hintColor, 
                0f, Vector2.Zero, 0.5f, SpriteEffects.None, 0f);
        }
    }
    
    private void DrawRect(SpriteBatch sb, int x, int y, int w, int h, Color color)
    {
        sb.Draw(_pixel, new Rectangle(x, y, w, h), color);
    }
}
