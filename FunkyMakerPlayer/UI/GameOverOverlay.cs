using System;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using Microsoft.Xna.Framework.Input;

namespace FunkyMakerPlayer.UI;

/// <summary>
/// Game over overlay with animated stats panel and retry/menu options.
/// Shows score, accuracy, misses, and rating breakdown with smooth fade-in animations.
/// </summary>
public class GameOverOverlay
{
    private readonly Texture2D _pixel;
    private readonly SpriteBatch _spriteBatch;
    private readonly SpriteFont _font;
    
    private bool _isActive = false;
    private float _timer = 0f;
    
    private const float GAME_OVER_WAIT = 2.5f;
    
    public bool IsActive => _isActive;
    public float Timer => _timer;
    
    public GameOverOverlay(Texture2D pixel, SpriteBatch spriteBatch, SpriteFont font)
    {
        _pixel = pixel;
        _spriteBatch = spriteBatch;
        _font = font;
    }
    
    /// <summary>
    /// Activates the game over overlay.
    /// </summary>
    public void Show()
    {
        _isActive = true;
        _timer = 0f;
    }
    
    /// <summary>
    /// Hides the game over overlay.
    /// </summary>
    public void Hide()
    {
        _isActive = false;
        _timer = 0f;
    }
    
    /// <summary>
    /// Updates the overlay timer and handles input.
    /// Returns true if user wants to retry, false if going back to menu, null if still waiting.
    /// </summary>
    public bool? Update(float deltaTime, KeyboardState currentKb, KeyboardState prevKb)
    {
        if (!_isActive) return null;
        
        _timer += deltaTime;
        
        bool enterPressed = currentKb.IsKeyDown(Keys.Enter) && !prevKb.IsKeyDown(Keys.Enter);
        bool escPressed = currentKb.IsKeyDown(Keys.Escape) && !prevKb.IsKeyDown(Keys.Escape);
        
        // Auto-restart after wait time or manual restart
        if (_timer >= GAME_OVER_WAIT || enterPressed)
        {
            return true;  // Retry
        }
        
        // ESC → go back to menu
        if (escPressed)
        {
            return false;  // Back to menu
        }
        
        return null;  // Still waiting
    }
    
    /// <summary>
    /// Draws the game over overlay with stats.
    /// </summary>
    public void Draw(int screenWidth, int screenHeight, NoteRenderer? playerRenderer)
    {
        if (!_isActive || playerRenderer == null) return;
        
        int sw = screenWidth;
        int sh = screenHeight;
        
        // Fade in over 0.6s
        float fadeT = Math.Min(_timer / 0.6f, 1f);
        // Text slides in from top over 0.8s
        float slideT = Math.Min(_timer / 0.8f, 1f);
        float ease = 1f - MathF.Pow(1f - slideT, 3f);  // cubic ease out
        
        // Dark overlay
        DrawRect(0, 0, sw, sh, Color.Black * (fadeT * 0.85f));
        
        // Red accent bar at top
        int barH = (int)(4 * fadeT);
        DrawRect(0, 0, sw, barH, new Color(0xFF, 0x22, 0x22));
        
        // "GAME OVER" — slides down from above
        float titleScale = 2.2f;
        string title = "GAME OVER";
        var titleSz = _font.MeasureString(title) * titleScale;
        float titleY = -titleSz.Y + (sh / 2f - 80f - (-titleSz.Y)) * ease;
        _spriteBatch.DrawString(_font, title,
            new Vector2(sw / 2f - titleSz.X / 2f, titleY),
            new Color(0xFF, 0x33, 0x33) * fadeT, 0f, Vector2.Zero, titleScale, SpriteEffects.None, 0f);
        
        // Stats panel — fades in after 0.4s
        float statsT = Math.Max(0f, Math.Min((_timer - 0.4f) / 0.5f, 1f));
        if (statsT > 0f)
        {
            DrawStatsPanel(sw, sh, playerRenderer, statsT);
        }
        
        // Hint — fades in after 0.7s
        float hintT = Math.Max(0f, Math.Min((_timer - 0.7f) / 0.4f, 1f));
        if (hintT > 0f)
        {
            // Pulsing effect
            float pulse = 0.7f + 0.3f * MathF.Sin(_timer * 4f);
            string hint = "ENTER  Retry    ESC  Menu";
            var hintSz = _font.MeasureString(hint) * 0.65f;
            _spriteBatch.DrawString(_font, hint,
                new Vector2(sw / 2f - hintSz.X / 2f, sh - 36f),
                Color.White * (hintT * pulse * 0.6f), 0f, Vector2.Zero, 0.65f, SpriteEffects.None, 0f);
        }
    }
    
    private void DrawStatsPanel(int sw, int sh, NoteRenderer renderer, float alpha)
    {
        // Calculate accuracy using FNF rating system
        int totalNotes = renderer.Sicks + renderer.Goods + renderer.Bads + renderer.Shits + renderer.Misses;
        float acc = 100f;
        if (totalNotes > 0)
        {
            float weightedHits = (renderer.Sicks * 1.0f) + (renderer.Goods * 0.75f) + 
                               (renderer.Bads * 0.5f) + (renderer.Shits * 0.25f);
            acc = (weightedHits / totalNotes) * 100f;
        }
        
        float panelW = 420f;  // Wider to fit more stats
        float panelH = 130f;  // Taller for ratings breakdown
        float panelX = sw / 2f - panelW / 2f;
        float panelY = sh / 2f - 20f;
        
        DrawRect((int)panelX, (int)panelY, (int)panelW, (int)panelH,
            new Color(20, 10, 10) * (0.8f * alpha));
        DrawRect((int)panelX, (int)panelY, (int)panelW, 2,
            new Color(0xFF, 0x22, 0x22) * alpha);
        
        float col1 = panelX + 20f;
        float col2 = panelX + panelW / 2f + 10f;
        float row1 = panelY + 14f;
        float row2 = panelY + 50f;
        float ss = 0.75f;
        
        // Score
        _spriteBatch.DrawString(_font, "Score",
            new Vector2(col1, row1), Color.White * 0.5f * alpha, 0f, Vector2.Zero, ss * 0.7f, SpriteEffects.None, 0f);
        _spriteBatch.DrawString(_font, renderer.Score.ToString(),
            new Vector2(col1, row1 + 14f), Color.White * alpha, 0f, Vector2.Zero, ss, SpriteEffects.None, 0f);
        
        // Misses
        _spriteBatch.DrawString(_font, "Misses",
            new Vector2(col2, row1), Color.White * 0.5f * alpha, 0f, Vector2.Zero, ss * 0.7f, SpriteEffects.None, 0f);
        _spriteBatch.DrawString(_font, renderer.Misses.ToString(),
            new Vector2(col2, row1 + 14f), new Color(0xFF, 0x66, 0x66) * alpha, 0f, Vector2.Zero, ss, SpriteEffects.None, 0f);
        
        // Accuracy
        _spriteBatch.DrawString(_font, "Accuracy",
            new Vector2(col1, row2), Color.White * 0.5f * alpha, 0f, Vector2.Zero, ss * 0.7f, SpriteEffects.None, 0f);
        Color accColor = acc >= 95f ? new Color(0, 255, 100) : acc >= 80f ? new Color(255, 200, 0) : new Color(255, 100, 100);
        _spriteBatch.DrawString(_font, $"{acc:F1}%",
            new Vector2(col1, row2 + 14f), accColor * alpha, 0f, Vector2.Zero, ss, SpriteEffects.None, 0f);
        
        // Ratings breakdown (compact)
        _spriteBatch.DrawString(_font, "Ratings",
            new Vector2(col2, row2), Color.White * 0.5f * alpha, 0f, Vector2.Zero, ss * 0.7f, SpriteEffects.None, 0f);
        string ratings = $"S:{renderer.Sicks} G:{renderer.Goods} B:{renderer.Bads} Sh:{renderer.Shits}";
        _spriteBatch.DrawString(_font, ratings,
            new Vector2(col2, row2 + 14f), new Color(150, 200, 255) * alpha, 0f, Vector2.Zero, ss * 0.7f, SpriteEffects.None, 0f);
    }
    
    private void DrawRect(int x, int y, int w, int h, Color color)
    {
        if (w <= 0 || h <= 0) return;
        _spriteBatch.Draw(_pixel, new Rectangle(x, y, w, h), color);
    }
}
