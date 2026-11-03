using System;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using Microsoft.Xna.Framework.Input;

namespace FunkyMakerPlayer.Editors;

/// <summary>
/// Editor Picker — Codename Engine inspired menu for selecting editor tools.
/// Modern card-based UI with 8 editor tools.
/// </summary>
public class EditorPicker
{
    private readonly Texture2D _pixel;
    private readonly SpriteFont? _font;
    private readonly FontRenderer? _fontRenderer;
    
    private int _selected = 0;
    private KeyboardState _prevKb;
    private float _inputCooldown = 0f;
    private const float INPUT_COOLDOWN_DURATION = 0.25f;
    
    public bool BackPressed { get; private set; }
    public bool EditorChosen { get; private set; }
    public EditorType ChosenEditor { get; private set; }
    
    // Editor definitions
    private struct EditorCard
    {
        public string Name;
        public Color AccentColor;
        public bool IsWip;
        public EditorType Type;
    }
    
    private readonly EditorCard[] _cards = new[]
    {
        new EditorCard { Name = "Chart Editor", AccentColor = new Color(255, 68, 136), IsWip = false, Type = EditorType.Chart },
        new EditorCard { Name = "Character Editor", AccentColor = new Color(68, 136, 255), IsWip = true, Type = EditorType.Character },
        new EditorCard { Name = "Stage Editor", AccentColor = new Color(68, 255, 136), IsWip = true, Type = EditorType.Stage },
        new EditorCard { Name = "Alphabet Editor", AccentColor = new Color(255, 215, 0), IsWip = true, Type = EditorType.Alphabet },
        new EditorCard { Name = "Sprite / Animation Editor", AccentColor = new Color(255, 140, 0), IsWip = true, Type = EditorType.Sprite },
        new EditorCard { Name = "Event Editor", AccentColor = new Color(150, 68, 255), IsWip = true, Type = EditorType.Event },
        new EditorCard { Name = "Week / Song Editor", AccentColor = new Color(0, 200, 255), IsWip = true, Type = EditorType.Week },
        new EditorCard { Name = "Dialogue Editor", AccentColor = new Color(255, 20, 147), IsWip = true, Type = EditorType.Dialogue }
    };
    
    public EditorPicker(Texture2D pixel, SpriteFont? font, FontRenderer? fontRenderer)
    {
        _pixel = pixel;
        _font = font;
        _fontRenderer = fontRenderer;
        ResetInputCooldown();
    }
    
    public void ResetInputCooldown()
    {
        _inputCooldown = INPUT_COOLDOWN_DURATION;
    }
    
    public void Update(float dt, KeyboardState kb, int screenWidth, int screenHeight)
    {
        // Input cooldown to prevent accidental selections
        if (_inputCooldown > 0f)
        {
            _inputCooldown -= dt;
            _prevKb = kb;
            return;
        }
        
        bool up = kb.IsKeyDown(Keys.Up) && !_prevKb.IsKeyDown(Keys.Up);
        bool down = kb.IsKeyDown(Keys.Down) && !_prevKb.IsKeyDown(Keys.Down);
        bool enter = kb.IsKeyDown(Keys.Enter) && !_prevKb.IsKeyDown(Keys.Enter);
        bool esc = kb.IsKeyDown(Keys.Escape) && !_prevKb.IsKeyDown(Keys.Escape);
        bool seven = kb.IsKeyDown(Keys.D7) && !_prevKb.IsKeyDown(Keys.D7);
        
        if (up)
        {
            _selected = (_selected - 1 + _cards.Length) % _cards.Length;
        }
        
        if (down)
        {
            _selected = (_selected + 1) % _cards.Length;
        }
        
        if (enter)
        {
            EditorChosen = true;
            ChosenEditor = _cards[_selected].Type;
            Console.WriteLine($"[EditorPicker] Selected: {_cards[_selected].Name} ({ChosenEditor})");
        }
        
        if (esc || seven)
        {
            BackPressed = true;
            Console.WriteLine($"[EditorPicker] Back pressed");
        }
        
        _prevKb = kb;
    }
    
    public void Draw(SpriteBatch sb, int screenWidth, int screenHeight)
    {
        // Modern dark gradient background
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
        
        // Title
        string title = "EDITOR HUB";
        int titleSize = Math.Clamp((int)(screenHeight * 0.065f), 32, 72);
        var titleSz = MeasureText(title, titleSize);
        float titleX = screenWidth / 2f - titleSz.X / 2f;
        float titleY = screenHeight * 0.10f;
        
        // Glow effect
        Color glowColor = new Color(255, 68, 136) * 0.15f;
        DrawText(sb, title, new Vector2(titleX + 2, titleY + 2), glowColor, titleSize);
        DrawText(sb, title, new Vector2(titleX, titleY), new Color(255, 215, 0), titleSize);
        
        // Subtitle
        string subtitle = "Select a tool to start creating";
        int subSize = Math.Clamp((int)(screenHeight * 0.025f), 14, 26);
        var subSz = MeasureText(subtitle, subSize);
        DrawText(sb, subtitle, new Vector2(screenWidth / 2f - subSz.X / 2f, titleY + titleSz.Y + 10),
            Color.White * 0.4f, subSize);
        
        // Cards (2 columns x 4 rows)
        int cardW = Math.Clamp((int)(screenWidth * 0.35f), 250, 500);
        int cardH = Math.Clamp((int)(screenHeight * 0.10f), 60, 100);
        int cardSpacing = Math.Clamp((int)(screenHeight * 0.025f), 15, 30);
        int columnSpacing = Math.Clamp((int)(screenWidth * 0.08f), 50, 100);
        
        float startY = titleY + titleSz.Y + 80;
        
        for (int i = 0; i < _cards.Length; i++)
        {
            var card = _cards[i];
            bool selected = i == _selected;
            
            // Calculate position (2 columns)
            int col = i % 2;
            int row = i / 2;
            
            int cardX = screenWidth / 2 - (cardW + columnSpacing / 2) + col * (cardW + columnSpacing);
            int cardY = (int)(startY + row * (cardH + cardSpacing));
            
            // Card background
            if (selected)
            {
                // Selected: glowing accent border
                int glowSize = Math.Max(2, (int)(screenHeight * 0.004f));
                DrawRect(sb, cardX - glowSize, cardY - glowSize, cardW + glowSize * 2, cardH + glowSize * 2, 
                    card.AccentColor * 0.6f);
                
                // Gradient fill
                Color cardTop = card.AccentColor * 0.25f;
                Color cardBottom = card.AccentColor * 0.15f;
                for (int g = 0; g < 10; g++)
                {
                    float gt = g / 10f;
                    DrawRect(sb, cardX, cardY + g * (cardH / 10), cardW, cardH / 10 + 1,
                        Color.Lerp(cardTop, cardBottom, gt));
                }
                
                // Left accent bar
                int accentWidth = Math.Max(3, (int)(cardW * 0.020f));
                DrawRect(sb, cardX, cardY, accentWidth, cardH, card.AccentColor);
            }
            else
            {
                // Not selected: subtle dark card
                DrawRect(sb, cardX, cardY, cardW, cardH, new Color(35, 35, 55) * 0.4f);
                
                // Thin accent border
                DrawRect(sb, cardX, cardY, cardW, 2, card.AccentColor * 0.3f);
                DrawRect(sb, cardX, cardY + cardH - 2, cardW, 2, card.AccentColor * 0.3f);
            }
            
            // Card text
            int textSize = selected ? Math.Clamp((int)(screenHeight * 0.030f), 18, 36) : Math.Clamp((int)(screenHeight * 0.028f), 16, 32);
            Color textColor = selected ? Color.White : new Color(200, 200, 220);
            var textSz = MeasureText(card.Name, textSize);
            float textX = cardX + (cardW - textSz.X) / 2f;
            float textY = cardY + (cardH - textSz.Y) / 2f;
            
            DrawText(sb, card.Name, new Vector2(textX, textY), textColor, textSize);
            
            // WIP badge
            if (card.IsWip)
            {
                string wipText = "WIP";
                int wipSize = Math.Clamp((int)(screenHeight * 0.020f), 12, 20);
                var wipSz = MeasureText(wipText, wipSize);
                int wipX = cardX + cardW - (int)wipSz.X - 15;
                int wipY = cardY + 10;
                int wipW = (int)wipSz.X + 10;
                int wipH = (int)wipSz.Y + 4;
                
                DrawRect(sb, wipX - 5, wipY - 2, wipW, wipH, new Color(255, 140, 0) * 0.4f);
                DrawText(sb, wipText, new Vector2(wipX, wipY), new Color(255, 200, 0), wipSize);
            }
        }
        
        // Bottom hint
        string hint = "UP/DOWN Navigate  |  ENTER Select  |  ESC/7 Back";
        int hintSize = Math.Clamp((int)(screenHeight * 0.023f), 13, 24);
        var hintSz = MeasureText(hint, hintSize);
        float hintY = Math.Max(screenHeight - screenHeight * 0.049f, screenHeight - 40);
        DrawText(sb, hint, new Vector2(screenWidth / 2f - hintSz.X / 2f, hintY),
            Color.White * 0.35f, hintSize);
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
