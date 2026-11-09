using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using Microsoft.Xna.Framework.Input;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace FunkyMakerPlayer.Editors.Chart;

/// <summary>
/// Level selector for Chart Editor - shows list of available .fnmm files
/// </summary>
public class LevelSelector
{
    private readonly Texture2D _pixel;
    private readonly SpriteFont _font;
    private readonly FontRenderer? _fontRenderer;
    
    private List<string> _levelPaths = new();
    private List<string> _levelNames = new();
    private int _selectedIndex = 0;
    private KeyboardState _prevKb;
    private float _scrollOffset = 0f;
    private float _targetScrollOffset = 0f;
    
    public bool BackPressed { get; private set; }
    public bool LevelSelected { get; private set; }
    public string? SelectedLevelPath { get; private set; }
    
    public LevelSelector(Texture2D pixel, SpriteFont font, FontRenderer? fontRenderer)
    {
        _pixel = pixel;
        _font = font;
        _fontRenderer = fontRenderer;
        _prevKb = Keyboard.GetState();
        
        LoadLevelList();
    }
    
    private void LoadLevelList()
    {
        try
        {
            // Use the same method as FreeplayMenu to get levels folder
            string levelsDir = FreeplayMenu.GetDefaultLevelsFolder();
            
            if (!Directory.Exists(levelsDir))
            {
                Console.WriteLine($"[LevelSelector] ⚠️ Levels directory not found: {levelsDir}");
                Console.WriteLine("[LevelSelector] Creating directory...");
                Directory.CreateDirectory(levelsDir);
            }
            
            // Scan for .FNMM, .fnmm, and .rar files (same as FreeplayMenu)
            var files = new List<string>();
            
            // Add .FNMM files
            files.AddRange(Directory.GetFiles(levelsDir, "*.FNMM", SearchOption.AllDirectories));
            
            // Add .fnmm files (lowercase)
            foreach (var f in Directory.GetFiles(levelsDir, "*.fnmm", SearchOption.AllDirectories))
            {
                if (!files.Contains(f)) files.Add(f);
            }
            
            // Add .rar archives
            foreach (var f in Directory.GetFiles(levelsDir, "*.rar", SearchOption.AllDirectories))
            {
                if (!files.Contains(f)) files.Add(f);
            }
            
            // Sort by filename
            files.Sort((a, b) => string.Compare(Path.GetFileName(a), Path.GetFileName(b), StringComparison.OrdinalIgnoreCase));
            
            _levelPaths = files;
            _levelNames = files.Select(f => Path.GetFileNameWithoutExtension(f)).ToList();
            
            Console.WriteLine($"[LevelSelector] Found {_levelPaths.Count} levels in {levelsDir}");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[LevelSelector] ❌ Failed to load level list: {ex.Message}");
        }
    }
    
    public void Update(float dt, KeyboardState kb)
    {
        BackPressed = false;
        LevelSelected = false;
        
        bool up = kb.IsKeyDown(Keys.Up) && !_prevKb.IsKeyDown(Keys.Up);
        bool down = kb.IsKeyDown(Keys.Down) && !_prevKb.IsKeyDown(Keys.Down);
        bool enter = kb.IsKeyDown(Keys.Enter) && !_prevKb.IsKeyDown(Keys.Enter);
        bool esc = kb.IsKeyDown(Keys.Escape) && !_prevKb.IsKeyDown(Keys.Escape);
        
        if (up && _selectedIndex > 0)
        {
            _selectedIndex--;
            _targetScrollOffset = _selectedIndex * 60f;
        }
        
        if (down && _selectedIndex < _levelPaths.Count - 1)
        {
            _selectedIndex++;
            _targetScrollOffset = _selectedIndex * 60f;
        }
        
        if (enter && _levelPaths.Count > 0)
        {
            LevelSelected = true;
            SelectedLevelPath = _levelPaths[_selectedIndex];
            Console.WriteLine($"[LevelSelector] Selected: {_levelNames[_selectedIndex]}");
        }
        
        if (esc)
        {
            BackPressed = true;
        }
        
        // Smooth scroll
        _scrollOffset += (_targetScrollOffset - _scrollOffset) * 10f * dt;
        
        _prevKb = kb;
    }
    
    public void Draw(SpriteBatch sb, int screenWidth, int screenHeight)
    {
        // Background
        sb.Draw(_pixel, new Rectangle(0, 0, screenWidth, screenHeight), new Color(15, 15, 20));
        
        // Title
        string title = "SELECT LEVEL";
        var titleSize = _font.MeasureString(title) * 1.2f;
        sb.DrawString(_font, title,
            new Vector2(screenWidth / 2 - titleSize.X / 2, 40),
            new Color(255, 68, 136), 0f, Vector2.Zero, 1.2f, SpriteEffects.None, 0f);
        
        // Level count
        string count = $"{_levelPaths.Count} levels found";
        var countSize = _font.MeasureString(count) * 0.7f;
        sb.DrawString(_font, count,
            new Vector2(screenWidth / 2 - countSize.X / 2, 100),
            Color.Gray, 0f, Vector2.Zero, 0.7f, SpriteEffects.None, 0f);
        
        // Level list
        int listTop = 150;
        int listHeight = screenHeight - listTop - 100;
        int itemHeight = 60;
        
        // Scissor rect for scrolling
        var prevScissor = sb.GraphicsDevice.ScissorRectangle;
        var prevRasterizerState = sb.GraphicsDevice.RasterizerState;
        
        sb.End();
        sb.Begin(SpriteSortMode.Deferred, BlendState.AlphaBlend, SamplerState.PointClamp, 
            DepthStencilState.None, new RasterizerState { ScissorTestEnable = true });
        
        sb.GraphicsDevice.ScissorRectangle = new Rectangle(0, listTop, screenWidth, listHeight);
        
        for (int i = 0; i < _levelPaths.Count; i++)
        {
            int y = (int)(listTop + i * itemHeight - _scrollOffset);
            
            // Skip if out of visible area
            if (y + itemHeight < listTop || y > listTop + listHeight)
                continue;
            
            bool selected = i == _selectedIndex;
            
            // Item background
            int itemX = screenWidth / 2 - 400;
            int itemW = 800;
            
            if (selected)
            {
                sb.Draw(_pixel, new Rectangle(itemX - 5, y - 2, itemW + 10, itemHeight + 4),
                    new Color(255, 68, 136) * 0.3f);
                sb.Draw(_pixel, new Rectangle(itemX, y, itemW, itemHeight),
                    new Color(60, 60, 80));
                
                // Left accent bar
                sb.Draw(_pixel, new Rectangle(itemX, y, 5, itemHeight),
                    new Color(255, 68, 136));
            }
            else
            {
                sb.Draw(_pixel, new Rectangle(itemX, y, itemW, itemHeight),
                    new Color(30, 30, 40) * 0.8f);
            }
            
            // Level name
            float textScale = selected ? 0.9f : 0.8f;
            Color textColor = selected ? Color.White : new Color(180, 180, 200);
            
            var nameSize = _font.MeasureString(_levelNames[i]) * textScale;
            sb.DrawString(_font, _levelNames[i],
                new Vector2(itemX + 20, y + itemHeight / 2 - nameSize.Y / 2),
                textColor, 0f, Vector2.Zero, textScale, SpriteEffects.None, 0f);
            
            // Index number
            string num = $"#{i + 1}";
            var numSize = _font.MeasureString(num) * 0.6f;
            sb.DrawString(_font, num,
                new Vector2(itemX + itemW - numSize.X - 20, y + itemHeight / 2 - numSize.Y / 2),
                Color.Gray, 0f, Vector2.Zero, 0.6f, SpriteEffects.None, 0f);
        }
        
        sb.End();
        sb.GraphicsDevice.ScissorRectangle = prevScissor;
        sb.Begin(SpriteSortMode.Deferred, BlendState.AlphaBlend, SamplerState.PointClamp, 
            DepthStencilState.None, prevRasterizerState);
        
        // Bottom hint
        string hint = "UP/DOWN Navigate | ENTER Select | ESC Back";
        var hintSize = _font.MeasureString(hint) * 0.7f;
        sb.DrawString(_font, hint,
            new Vector2(screenWidth / 2 - hintSize.X / 2, screenHeight - 60),
            Color.White * 0.5f, 0f, Vector2.Zero, 0.7f, SpriteEffects.None, 0f);
        
        // Empty state message
        if (_levelPaths.Count == 0)
        {
            string emptyMsg = "No levels found in mods/levels directory";
            var emptySize = _font.MeasureString(emptyMsg);
            sb.DrawString(_font, emptyMsg,
                new Vector2(screenWidth / 2 - emptySize.X / 2, screenHeight / 2),
                Color.Red * 0.8f);
            
            // Help text
            string helpMsg = "Copy .FNMM or .rar files to the mods/levels folder";
            var helpSize = _font.MeasureString(helpMsg) * 0.7f;
            sb.DrawString(_font, helpMsg,
                new Vector2(screenWidth / 2 - helpSize.X / 2, screenHeight / 2 + 40),
                Color.Gray, 0f, Vector2.Zero, 0.7f, SpriteEffects.None, 0f);
        }
    }
}
