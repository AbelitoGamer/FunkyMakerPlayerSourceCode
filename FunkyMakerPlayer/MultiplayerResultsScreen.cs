using System;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using Microsoft.Xna.Framework.Input;

namespace FunkyMakerPlayer;

/// <summary>
/// Results screen shown after a multiplayer match ends.
/// Displays winner, final scores, accuracy, and stats.
/// </summary>
public class MultiplayerResultsScreen
{
    public enum Phase { FadeIn, Display, FadeOut, Done }
    
    public Phase CurrentPhase { get; private set; } = Phase.FadeIn;
    
    // Visual resources
    private readonly Texture2D _pixel;
    private readonly Texture2D? _catTex;
    
    // Results data
    private readonly string _playerName;
    private readonly string _opponentName;
    private readonly int _playerScore;
    private readonly int _opponentScore;
    private readonly float _playerAccuracy;
    private readonly float _opponentAccuracy;
    private readonly int _playerSicks;
    private readonly int _playerGoods;
    private readonly int _playerBads;
    private readonly int _playerShits;
    private readonly int _playerMisses;
    private readonly bool _playerWon;
    private readonly bool _isDraw;
    private readonly Color _playerColor;
    private readonly Color _opponentColor;
    
    // Animation state
    private float _timer = 0f;
    private float _alpha = 0f;
    private float _textScale = 0f;
    
    // Constants
    private const float FADE_IN_DURATION = 0.5f;
    private const float DISPLAY_DURATION = 5f;  // Show results for 5 seconds
    private const float FADE_OUT_DURATION = 0.3f;
    
    public MultiplayerResultsScreen(
        Texture2D pixel,
        Texture2D? catTex,
        string playerName,
        string opponentName,
        int playerScore,
        int opponentScore,
        float playerAccuracy,
        float opponentAccuracy,
        int playerSicks,
        int playerGoods,
        int playerBads,
        int playerShits,
        int playerMisses,
        bool playerWon,
        Color? playerColor = null,
        Color? opponentColor = null,
        bool isDraw = false)
    {
        _pixel = pixel;
        _catTex = catTex;
        _playerName = playerName;
        _opponentName = opponentName;
        _playerScore = playerScore;
        _opponentScore = opponentScore;
        _playerAccuracy = playerAccuracy;
        _opponentAccuracy = opponentAccuracy;
        _playerSicks = playerSicks;
        _playerGoods = playerGoods;
        _playerBads = playerBads;
        _playerShits = playerShits;
        _playerMisses = playerMisses;
        _playerWon = playerWon;
        _isDraw = isDraw;
        _playerColor = playerColor ?? new Color(100, 255, 100);
        _opponentColor = opponentColor ?? new Color(255, 100, 100);
    }
    
    public void Update(float dt, KeyboardState kb, KeyboardState prevKb)
    {
        _timer += dt;
        
        switch (CurrentPhase)
        {
            case Phase.FadeIn:
                _alpha = Math.Min(1f, _timer / FADE_IN_DURATION);
                _textScale = Math.Min(1f, (_timer / FADE_IN_DURATION) * 1.2f);  // Slight overshoot
                
                if (_timer >= FADE_IN_DURATION)
                {
                    _timer = 0f;
                    CurrentPhase = Phase.Display;
                }
                break;
                
            case Phase.Display:
                // Check for ENTER or ESCAPE to skip to fade out
                bool enterPressed = kb.IsKeyDown(Keys.Enter) && !prevKb.IsKeyDown(Keys.Enter);
                bool escapePressed = kb.IsKeyDown(Keys.Escape) && !prevKb.IsKeyDown(Keys.Escape);
                
                if (_timer >= DISPLAY_DURATION || enterPressed || escapePressed)
                {
                    _timer = 0f;
                    CurrentPhase = Phase.FadeOut;
                }
                break;
                
            case Phase.FadeOut:
                _alpha = Math.Max(0f, 1f - (_timer / FADE_OUT_DURATION));
                
                if (_timer >= FADE_OUT_DURATION)
                {
                    CurrentPhase = Phase.Done;
                }
                break;
        }
    }
    
    public void Draw(SpriteBatch sb, SpriteFont font, int screenWidth, int screenHeight)
    {
        if (CurrentPhase == Phase.Done) return;
        
        int sw = screenWidth;
        int sh = screenHeight;
        
        // Semi-transparent background
        sb.Draw(_pixel, new Rectangle(0, 0, sw, sh), Color.Black * (0.85f * _alpha));
        
        // Winner announcement
        string winnerText = _isDraw ? "DRAW!" : (_playerWon ? "VICTORY!" : "DEFEAT");
        Color winnerColor = _isDraw ? new Color(255, 215, 80) : (_playerWon ? new Color(100, 255, 100) : new Color(255, 100, 100));
        float winnerScale = 2.0f * _textScale;
        Vector2 winnerSize = font.MeasureString(winnerText) * winnerScale;
        Vector2 winnerPos = new Vector2(sw / 2 - winnerSize.X / 2, sh * 0.15f);
        
        // Winner text shadow
        sb.DrawString(font, winnerText, 
            winnerPos + new Vector2(3, 3), Color.Black * (0.7f * _alpha),
            0f, Vector2.Zero, winnerScale, SpriteEffects.None, 0f);
        
        // Winner text
        sb.DrawString(font, winnerText, 
            winnerPos, winnerColor * _alpha,
            0f, Vector2.Zero, winnerScale, SpriteEffects.None, 0f);
        
        // Final scores section
        float scoreY = sh * 0.35f;
        float scoreScale = 1.2f * _textScale;
        
        // Player score (left side)
        string playerScoreText = UI.ErrorOverlay.Safe($"{_playerName}\n{_playerScore:N0}");
        Vector2 playerScoreSize = font.MeasureString(playerScoreText) * scoreScale;
        Vector2 playerScorePos = new Vector2(sw / 4 - playerScoreSize.X / 2, scoreY);
        
        sb.DrawString(font, playerScoreText,
            playerScorePos + new Vector2(2, 2), Color.Black * (0.7f * _alpha),
            0f, Vector2.Zero, scoreScale, SpriteEffects.None, 0f);
        sb.DrawString(font, playerScoreText,
            playerScorePos, _playerColor * _alpha,
            0f, Vector2.Zero, scoreScale, SpriteEffects.None, 0f);
        
        // VS separator
        string vsText = "VS";
        Vector2 vsSize = font.MeasureString(vsText) * scoreScale;
        Vector2 vsPos = new Vector2(sw / 2 - vsSize.X / 2, scoreY + 20);
        
        sb.DrawString(font, vsText,
            vsPos + new Vector2(2, 2), Color.Black * (0.7f * _alpha),
            0f, Vector2.Zero, scoreScale, SpriteEffects.None, 0f);
        sb.DrawString(font, vsText,
            vsPos, Color.White * _alpha,
            0f, Vector2.Zero, scoreScale, SpriteEffects.None, 0f);
        
        // Opponent score (right side)
        string oppScoreText = UI.ErrorOverlay.Safe($"{_opponentName}\n{_opponentScore:N0}");
        Vector2 oppScoreSize = font.MeasureString(oppScoreText) * scoreScale;
        Vector2 oppScorePos = new Vector2(sw * 3 / 4 - oppScoreSize.X / 2, scoreY);
        
        sb.DrawString(font, oppScoreText,
            oppScorePos + new Vector2(2, 2), Color.Black * (0.7f * _alpha),
            0f, Vector2.Zero, scoreScale, SpriteEffects.None, 0f);
        sb.DrawString(font, oppScoreText,
            oppScorePos, _opponentColor * _alpha,
            0f, Vector2.Zero, scoreScale, SpriteEffects.None, 0f);
        
        // Accuracy section
        float accY = sh * 0.55f;
        float accScale = 0.9f * _textScale;
        
        string playerAccText = $"Accuracy: {_playerAccuracy:F1}%";
        Vector2 playerAccSize = font.MeasureString(playerAccText) * accScale;
        Vector2 playerAccPos = new Vector2(sw / 4 - playerAccSize.X / 2, accY);
        
        sb.DrawString(font, playerAccText,
            playerAccPos + new Vector2(2, 2), Color.Black * (0.7f * _alpha),
            0f, Vector2.Zero, accScale, SpriteEffects.None, 0f);
        sb.DrawString(font, playerAccText,
            playerAccPos, Color.White * _alpha,
            0f, Vector2.Zero, accScale, SpriteEffects.None, 0f);
        
        string oppAccText = $"Accuracy: {_opponentAccuracy:F1}%";
        Vector2 oppAccSize = font.MeasureString(oppAccText) * accScale;
        Vector2 oppAccPos = new Vector2(sw * 3 / 4 - oppAccSize.X / 2, accY);
        
        sb.DrawString(font, oppAccText,
            oppAccPos + new Vector2(2, 2), Color.Black * (0.7f * _alpha),
            0f, Vector2.Zero, accScale, SpriteEffects.None, 0f);
        sb.DrawString(font, oppAccText,
            oppAccPos, Color.White * _alpha,
            0f, Vector2.Zero, accScale, SpriteEffects.None, 0f);
        
        // Player stats (detailed)
        float statsY = sh * 0.70f;
        float statsScale = 0.7f * _textScale;
        
        string statsText = $"Sick: {_playerSicks}  Good: {_playerGoods}  Bad: {_playerBads}\nShit: {_playerShits}  Miss: {_playerMisses}";
        Vector2 statsSize = font.MeasureString(statsText) * statsScale;
        Vector2 statsPos = new Vector2(sw / 2 - statsSize.X / 2, statsY);
        
        sb.DrawString(font, statsText,
            statsPos + new Vector2(2, 2), Color.Black * (0.7f * _alpha),
            0f, Vector2.Zero, statsScale, SpriteEffects.None, 0f);
        sb.DrawString(font, statsText,
            statsPos, Color.Gray * _alpha,
            0f, Vector2.Zero, statsScale, SpriteEffects.None, 0f);
        
        // Instructions
        if (CurrentPhase == Phase.Display)
        {
            string instructText = "Press ENTER or ESC to continue";
            float instructScale = 0.6f * _textScale;
            Vector2 instructSize = font.MeasureString(instructText) * instructScale;
            Vector2 instructPos = new Vector2(sw / 2 - instructSize.X / 2, sh * 0.90f);
            
            // Pulse effect
            float pulse = 0.7f + 0.3f * MathF.Sin(_timer * 3f);
            
            sb.DrawString(font, instructText,
                instructPos + new Vector2(1, 1), Color.Black * (0.7f * _alpha * pulse),
                0f, Vector2.Zero, instructScale, SpriteEffects.None, 0f);
            sb.DrawString(font, instructText,
                instructPos, Color.White * (_alpha * pulse),
                0f, Vector2.Zero, instructScale, SpriteEffects.None, 0f);
        }
    }
}
