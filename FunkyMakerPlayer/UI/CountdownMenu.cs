using System;
using System.Collections.Generic;
using System.IO;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using Microsoft.Xna.Framework.Input;

namespace FunkyMakerPlayer.UI;

/// <summary>
/// Countdown pack selector menu with preview of ready/set/go images.
/// </summary>
public class CountdownMenu : IScreenOverlay
{
    private readonly Texture2D _pixel;
    private readonly SpriteFont _font;
    private readonly FontRenderer? _fontRenderer;
    
    public bool IsActive { get; set; } = false;
    public int Selection { get; set; } = 0;
    public bool SelectionConfirmed { get; private set; } = false;
    private readonly PickerTouch _touch = new(); // taps / clicks (fork)
    
    private List<string> _packs = new();
    private Texture2D? _previewReady = null;
    private Texture2D? _previewSet = null;
    private Texture2D? _previewGo = null;
    
    public CountdownMenu(Texture2D pixel, SpriteFont font, FontRenderer? fontRenderer = null)
    {
        _pixel = pixel;
        _font = font;
        _fontRenderer = fontRenderer;
    }
    
    public void Open(GraphicsDevice graphicsDevice)
    {
        ScanAvailablePacks();
        
        // Select currently active countdown
        Selection = _packs.IndexOf(Game1.SelectedCountdown);
        if (Selection < 0) Selection = 0;
        
        LoadPreview(_packs[Selection], graphicsDevice);
        SelectionConfirmed = false;
        _touch.Sync();
        IsActive = true;
    }
    
    public void Close()
    {
        IsActive = false;
        _previewReady?.Dispose();
        _previewSet?.Dispose();
        _previewGo?.Dispose();
        _previewReady = null;
        _previewSet = null;
        _previewGo = null;
    }
    
    private void ScanAvailablePacks()
    {
        _packs.Clear();
        _packs.Add("default");  // mods/countdowns/Default/ (setting value kept as "default")
        
        try
        {
            string countdownsDir = PlatformPaths.GetCountdownsPath();
            if (Directory.Exists(countdownsDir))
            {
                var dirs = Directory.GetDirectories(countdownsDir);
                foreach (var dir in dirs)
                {
                    string name = Path.GetFileName(dir);
                    if (!string.IsNullOrEmpty(name) && !DefaultMods.IsDefaultCountdown(name))
                    {
                        // Check if it has the required files
                        // Case-insensitive: Android file names are case-sensitive (Ready.png etc.).
                        string readyPath = CaseInsensitivePaths.Find(dir, "ready.png");
                        string setPath = CaseInsensitivePaths.Find(dir, "set.png");
                        string goPath = CaseInsensitivePaths.Find(dir, "go.png");
                        
                        if (File.Exists(readyPath) && File.Exists(setPath) && File.Exists(goPath))
                        {
                            _packs.Add(name);
                        }
                    }
                }
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[CountdownMenu] Error scanning packs: {ex.Message}");
        }
    }
    
    private void LoadPreview(string packName, GraphicsDevice graphicsDevice)
    {
        // Dispose old textures
        _previewReady?.Dispose();
        _previewSet?.Dispose();
        _previewGo?.Dispose();
        _previewReady = null;
        _previewSet = null;
        _previewGo = null;
        
        try
        {
            _previewReady = LoadPreviewImage(packName, "ready.png", graphicsDevice);
            _previewSet = LoadPreviewImage(packName, "set.png", graphicsDevice);
            _previewGo = LoadPreviewImage(packName, "go.png", graphicsDevice);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[CountdownMenu] Error loading preview for '{packName}': {ex.Message}");
        }
    }
    
    private static Texture2D? LoadPreviewImage(string packName, string fileName, GraphicsDevice graphicsDevice)
    {
        foreach (string path in CountdownOverlay.CandidatePaths(packName, fileName))
        {
            if (!File.Exists(path)) continue;
            using var fs = File.OpenRead(path);
            return Texture2D.FromStream(graphicsDevice, fs);
        }
        return null;
    }
    
    public void Update(KeyboardState kb, KeyboardState prevKb, GraphicsDevice graphicsDevice)
    {
        if (!IsActive) return;
        

        // Taps / clicks (fork): a row previews it, the previewed row again or APPLY applies, BACK cancels.
        var tap = _touch.Poll(Selection);
        if (tap.Select is int pick && pick >= 0 && pick < _packs.Count && pick != Selection)
        {
            Selection = pick;
            LoadPreview(_packs[Selection], graphicsDevice);
        }
        // Navigation
        if (kb.IsKeyDown(Keys.Up) && !prevKb.IsKeyDown(Keys.Up))
        {
            Selection = (Selection - 1 + _packs.Count) % _packs.Count;
            LoadPreview(_packs[Selection], graphicsDevice);
        }
        
        if (kb.IsKeyDown(Keys.Down) && !prevKb.IsKeyDown(Keys.Down))
        {
            Selection = (Selection + 1) % _packs.Count;
            LoadPreview(_packs[Selection], graphicsDevice);
        }
        
        // Confirm selection
        if (tap.Confirm ||
            (kb.IsKeyDown(Keys.Enter) && !prevKb.IsKeyDown(Keys.Enter)) ||
            (kb.IsKeyDown(Keys.Space) && !prevKb.IsKeyDown(Keys.Space)))
        {
            Game1.SelectedCountdown = _packs[Selection];
            SelectionConfirmed = true;
            Close();
        }
        
        // Cancel
        if (tap.Cancel || (kb.IsKeyDown(Keys.Escape) && !prevKb.IsKeyDown(Keys.Escape)))
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
            _fontRenderer.DrawString(spriteBatch, "SELECT COUNTDOWN PACK",
                new Vector2(20 * u, 6 * u), new Color(0xFF, 0x44, 0x88), titleSize);
        }
        else
        {
            spriteBatch.DrawUiString(_font, "SELECT COUNTDOWN PACK",
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
        
        for (int i = 0; i < _packs.Count; i++)
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
            string displayName = _packs[i];
            displayName = DefaultMods.CountdownLabel(displayName);
            
            if (_fontRenderer != null)
            {
                int textSize = Math.Clamp((int)(itemH * 0.5f), 18, 40);
                _fontRenderer.DrawString(spriteBatch, displayName,
                    new Vector2(panelX + 10 * u, y + 8 * u), textColor, textSize);
            }
            else
            {
                spriteBatch.DrawUiString(_font, displayName,
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
        
        // Draw preview images
        float previewItemH = previewH / 3f - 20f;
        float previewItemY = previewY + 10f;
        
        DrawCountdownImage(spriteBatch, _previewReady, "READY?", previewX, previewItemY, previewW, previewItemH);
        DrawCountdownImage(spriteBatch, _previewSet, "SET", previewX, previewItemY + previewItemH + 20f, previewW, previewItemH);
        DrawCountdownImage(spriteBatch, _previewGo, "GO!", previewX, previewItemY + (previewItemH + 20f) * 2f, previewW, previewItemH);
        
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
            Vector2 hintMeasure = _font.MeasureUi(hint) * 0.5f;
            spriteBatch.DrawUiString(_font, hint,
                new Vector2(sw / 2f - hintMeasure.X / 2f, sh - 26), hintColor, 
                0f, Vector2.Zero, 0.5f, SpriteEffects.None, 0f);
        }
    }
    
    private void DrawCountdownImage(SpriteBatch sb, Texture2D? texture, string label, float x, float y, float w, float h)
    {
        // Label
        Color labelColor = new Color(0xFF, 0x88, 0xCC);
        if (_fontRenderer != null)
        {
            int labelSize = Math.Clamp((int)(h * 0.15f), 16, 24);
            _fontRenderer.DrawString(sb, label, new Vector2(x + 10, y), labelColor, labelSize);
        }
        else
        {
            sb.DrawUiString(_font, label, new Vector2(x + 10, y), labelColor, 0f, Vector2.Zero, 0.6f, SpriteEffects.None, 0f);
        }
        
        // Image area
        float imgY = y + 30;
        float imgH = h - 30;
        
        if (texture != null)
        {
            // Center and scale image to fit
            float scale = Math.Min(w / texture.Width, imgH / texture.Height) * 0.8f;
            float imgW = texture.Width * scale;
            float imgHScaled = texture.Height * scale;
            float imgX = x + (w - imgW) / 2f;
            float imgYCentered = imgY + (imgH - imgHScaled) / 2f;
            
            sb.Draw(texture, new Vector2(imgX, imgYCentered), null, Color.White, 0f, 
                Vector2.Zero, scale, SpriteEffects.None, 0f);
        }
        else
        {
            // No preview available
            string noPreview = "No Preview";
            Color noPreviewColor = Color.White * 0.3f;
            
            if (_fontRenderer != null)
            {
                int size = Math.Clamp((int)(imgH * 0.2f), 14, 20);
                Vector2 measure = _fontRenderer.MeasureString(noPreview, size);
                _fontRenderer.DrawString(sb, noPreview,
                    new Vector2(x + (w - measure.X) / 2f, imgY + (imgH - measure.Y) / 2f),
                    noPreviewColor, size);
            }
            else
            {
                Vector2 measure = _font.MeasureUi(noPreview) * 0.5f;
                sb.DrawUiString(_font, noPreview,
                    new Vector2(x + (w - measure.X) / 2f, imgY + (imgH - measure.Y) / 2f),
                    noPreviewColor, 0f, Vector2.Zero, 0.5f, SpriteEffects.None, 0f);
            }
        }
    }
    
    private void DrawRect(SpriteBatch sb, int x, int y, int w, int h, Color color)
    {
        sb.Draw(_pixel, new Rectangle(x, y, w, h), color);
    }
}
