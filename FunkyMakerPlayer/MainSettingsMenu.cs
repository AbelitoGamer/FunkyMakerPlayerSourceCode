using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using Microsoft.Xna.Framework.Input;
using System;

namespace FunkyMakerPlayer;

/// <summary>
/// HUD Editor launcher screen - accessible from main menu.
/// Explains how the HUD editor works and lets you open it.
/// </summary>
public class MainSettingsMenu
{
    private readonly Texture2D _pixel;
    private readonly SpriteFont _font;
    private KeyboardState _prevKb;
    private float _inputCooldown = 0.2f; // Cooldown to prevent immediate ENTER detection
    
    public bool BackPressed { get; private set; }
    public bool OpenHudEditor { get; private set; }

    public MainSettingsMenu(Texture2D pixel, SpriteFont font)
    {
        _pixel = pixel;
        _font = font;
        _prevKb = Keyboard.GetState();
    }

    public void Update(float dt, int screenWidth, int screenHeight)
    {
        var kb = Keyboard.GetState();
        
        BackPressed = false;
        OpenHudEditor = false;
        
        // Decrease cooldown timer
        if (_inputCooldown > 0f)
        {
            _inputCooldown -= dt;
            _prevKb = kb;
            return; // Don't process input during cooldown
        }
        
        // ESC to go back to main menu
        if (kb.IsKeyDown(Keys.Escape) && !_prevKb.IsKeyDown(Keys.Escape))
        {
            BackPressed = true;
        }
        
        // ENTER to open HUD Editor (only after cooldown)
        if (kb.IsKeyDown(Keys.Enter) && !_prevKb.IsKeyDown(Keys.Enter))
        {
            OpenHudEditor = true;
        }
        
        _prevKb = kb;
    }
    
    /// <summary>
    /// Reset input cooldown when entering this screen
    /// </summary>
    public void ResetInputCooldown()
    {
        _inputCooldown = 0.2f;
    }

    public void Draw(SpriteBatch sb, int screenWidth, int screenHeight)
    {
        int centerX = screenWidth / 2;
        int centerY = screenHeight / 2;
        
        // Calculate scaling factor based on screen height
        float scale = screenHeight / 720f; // Base scale on 720p
        
        // Title
        string title = "HUD EDITOR";
        float titleScale = 1.5f * scale;
        var titleSize = _font.MeasureString(title) * titleScale;
        sb.DrawString(_font, title, 
            new Vector2(centerX - titleSize.X / 2, centerY - 250 * scale), 
            Color.Cyan, 0f, Vector2.Zero, titleScale, SpriteEffects.None, 0f);
        
        // Description box - scale with screen
        int boxWidth = (int)(700 * scale);
        int boxHeight = (int)(380 * scale);
        int boxX = centerX - boxWidth / 2;
        int boxY = (int)(centerY - 120 * scale);
        
        // Draw box background
        sb.Draw(_pixel, new Rectangle(boxX, boxY, boxWidth, boxHeight), new Color(20, 20, 30, 230));
        DrawRectangleBorder(sb, boxX, boxY, boxWidth, boxHeight, Color.Cyan * 0.7f, (int)(3 * scale));
        
        // Instructions text
        string[] instructions = new[]
        {
            "The HUD Editor lets you customize the position",
            "of all HUD elements (health bar, score, misses,",
            "accuracy, combo, judgement, etc.).",
            "",
            "CONTROLS:",
            "  - Click and drag to move elements",
            "  - S: Save layout",
            "  - R: Reset to default positions",
            "  - H: Hide/Show instructions",
            "  - ESC: Exit editor",
            "",
            "Changes are saved to:",
            "  Assets/data/hud_settings.json",
            "",
            "NOTE: Icons CANNOT be moved - they follow",
            "the health bar automatically (FNF style)."
        };
        
        int textY = (int)(boxY + 30 * scale);
        float lineHeight = 28f * scale;
        
        foreach (var line in instructions)
        {
            float lineScale = line.StartsWith("  ") ? 0.85f : 0.9f;
            lineScale *= scale;
            var lineSize = _font.MeasureString(line) * lineScale;
            Color lineColor = line.StartsWith("CONTROLS:") || line.StartsWith("NOTE:") 
                ? Color.Yellow 
                : Color.White;
            
            sb.DrawString(_font, line,
                new Vector2(boxX + 40 * scale, textY),
                lineColor, 0f, Vector2.Zero, lineScale, SpriteEffects.None, 0f);
            
            textY += (int)lineHeight;
        }
        
        // "Press ENTER" prompt with pulsing effect
        float pulse = (float)Math.Sin(DateTime.Now.Millisecond / 1000f * Math.PI * 2) * 0.15f + 0.85f;
        string enterPrompt = "Press ENTER to open";
        float enterScale = 1.2f * scale;
        var enterSize = _font.MeasureString(enterPrompt) * enterScale;
        sb.DrawString(_font, enterPrompt,
            new Vector2(centerX - enterSize.X / 2, centerY + 280 * scale),
            Color.LimeGreen * pulse, 0f, Vector2.Zero, enterScale, SpriteEffects.None, 0f);
        
        // Bottom instructions
        string bottomInstr = "ESC: Back to main menu";
        float bottomScale = 0.9f * scale;
        var bottomSize = _font.MeasureString(bottomInstr) * bottomScale;
        sb.DrawString(_font, bottomInstr,
            new Vector2(centerX - bottomSize.X / 2, screenHeight - 60 * scale),
            Color.Gray, 0f, Vector2.Zero, bottomScale, SpriteEffects.None, 0f);
    }
    
    private void DrawRectangleBorder(SpriteBatch sb, int x, int y, int width, int height, Color color, int thickness)
    {
        // Top
        sb.Draw(_pixel, new Rectangle(x, y, width, thickness), color);
        // Bottom
        sb.Draw(_pixel, new Rectangle(x, y + height - thickness, width, thickness), color);
        // Left
        sb.Draw(_pixel, new Rectangle(x, y, thickness, height), color);
        // Right
        sb.Draw(_pixel, new Rectangle(x + width - thickness, y, thickness, height), color);
    }
}
