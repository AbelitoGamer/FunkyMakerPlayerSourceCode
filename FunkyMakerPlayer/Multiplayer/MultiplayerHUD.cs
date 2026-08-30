using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using System;
using FunkyMakerPlayer.Multiplayer.UI;

namespace FunkyMakerPlayer.Multiplayer
{
    /// <summary>
    /// Configurable HUD for multiplayer gameplay.
    /// Renders player and opponent stats, avatars, scores, and connection quality.
    /// </summary>
    public class MultiplayerHUD
    {
        private readonly MultiplayerSettings _settings;
        private readonly ConnectionIndicator _connectionIndicator;
        
        // Player data
        private string _playerUsername = "";
        private int _playerScore = 0;
        private int _playerCombo = 0;
        private int _playerMisses = 0;
        private float _playerAccuracy = 100f;
        private float _playerHealth = 0.5f;
        
        // Opponent data
        private string _opponentUsername = "";
        private int _opponentScore = 0;
        private int _opponentCombo = 0;
        private int _opponentMisses = 0;
        private float _opponentAccuracy = 100f;
        
        // Colors
        private Color _playerBarColor = new Color(0x31, 0xB0, 0xD5);   // Cyan
        private Color _opponentBarColor = new Color(0xAF, 0x66, 0xCE); // Purple
        
        // Avatar bounce animation
        private float _iconBounceScale = 1f;
        private float _iconBounceTimer = 0f;
        
        public MultiplayerHUD(ConnectionIndicator connectionIndicator)
        {
            _settings = MultiplayerSettings.Instance;
            _connectionIndicator = connectionIndicator;
        }
        
        /// <summary>
        /// Update player stats for HUD display.
        /// </summary>
        public void UpdatePlayerStats(string username, int score, int combo, int misses, float accuracy, float health)
        {
            _playerUsername = username;
            _playerScore = score;
            _playerCombo = combo;
            _playerMisses = misses;
            _playerAccuracy = accuracy;
            _playerHealth = health;
        }
        
        /// <summary>
        /// Update opponent stats for HUD display.
        /// </summary>
        public void UpdateOpponentStats(string username, int score, int combo, int misses, float accuracy)
        {
            _opponentUsername = username;
            _opponentScore = score;
            _opponentCombo = combo;
            _opponentMisses = misses;
            _opponentAccuracy = accuracy;
        }
        
        /// <summary>
        /// Set custom health bar colors.
        /// </summary>
        public void SetBarColors(Color playerColor, Color opponentColor)
        {
            _playerBarColor = playerColor;
            _opponentBarColor = opponentColor;
        }
        
        /// <summary>
        /// Trigger icon bounce animation when hitting a note.
        /// </summary>
        public void TriggerIconBounce()
        {
            _iconBounceScale = 1.25f;
            _iconBounceTimer = 0.1f;
        }
        
        /// <summary>
        /// Update HUD animations.
        /// </summary>
        public void Update(float dt)
        {
            // Icon bounce animation
            if (_iconBounceTimer > 0f)
            {
                _iconBounceTimer -= dt;
                if (_iconBounceTimer <= 0f)
                {
                    _iconBounceScale = 1f;
                    _iconBounceTimer = 0f;
                }
            }
            
            // Update connection indicator
            _connectionIndicator?.Update(dt);
        }
        
        /// <summary>
        /// Draw the complete multiplayer HUD.
        /// </summary>
        public void Draw(SpriteBatch sb, SpriteFont font, Texture2D pixel, int screenWidth, int screenHeight, bool downscroll)
        {
            if (font == null || pixel == null) return;
            
            float hudOpacity = _settings.HudOpacity;
            
            // Draw health bar
            DrawHealthBar(sb, pixel, screenWidth, screenHeight, downscroll, hudOpacity);
            
            // Draw player avatars
            DrawAvatars(sb, font, screenWidth, screenHeight, downscroll, hudOpacity);
            
            // Draw stats below health bar
            DrawStats(sb, font, screenWidth, screenHeight, downscroll, hudOpacity);
            
            // Draw connection indicator
            if (_connectionIndicator != null)
            {
                _connectionIndicator.Draw(sb, font, pixel, screenWidth, screenHeight);
            }
        }
        
        private void DrawHealthBar(SpriteBatch sb, Texture2D pixel, int sw, int sh, bool downscroll, float opacity)
        {
            // FNF-style health bar
            int barW = 600, barH = 24;
            int barX = sw / 2 - barW / 2;
            int barY = downscroll ? 40 : (sh - 60);
            
            // Black background with border
            DrawRect(sb, pixel, barX - 4, barY - 4, barW + 8, barH + 8, Color.Black * opacity);
            DrawRect(sb, pixel, barX - 2, barY - 2, barW + 4, barH + 4, new Color(40, 40, 40) * opacity);
            
            // Calculate bar widths
            int playerW = (int)(_playerHealth * barW);
            int enemyW = barW - playerW;
            
            // Draw opponent side (left) with gradient
            for (int i = 0; i < enemyW; i++)
            {
                float t = (float)i / barW;
                Color c = Color.Lerp(_opponentBarColor * 0.7f, _opponentBarColor, t);
                DrawRect(sb, pixel, barX + i, barY, 1, barH, c * opacity);
            }
            
            // Draw player side (right) with gradient
            for (int i = 0; i < playerW; i++)
            {
                float t = (float)i / barW;
                Color c = Color.Lerp(_playerBarColor, _playerBarColor * 0.7f, t);
                DrawRect(sb, pixel, barX + enemyW + i, barY, 1, barH, c * opacity);
            }
            
            // Center divider (white line)
            DrawRect(sb, pixel, barX + enemyW - 2, barY - 4, 4, barH + 8, Color.White * opacity);
        }
        
        private void DrawAvatars(SpriteBatch sb, SpriteFont font, int sw, int sh, bool downscroll, float opacity)
        {
            int barW = 600;
            int barH = 24;
            int barX = sw / 2 - barW / 2;
            int barY = downscroll ? 40 : (sh - 60);
            
            int playerW = (int)(_playerHealth * barW);
            int enemyW = barW - playerW;
            int divX = barX + enemyW;
            
            // Avatar size and position
            int iconSize = 80;
            int iconY = barY + barH / 2 - iconSize / 2;
            
            int opponentIconX = divX - iconSize - 10;
            int playerIconX = divX + 10;
            
            // Draw opponent avatar
            string opponentAvatar = _settings.GetAvatarIcon();
            Vector2 opponentAvatarSize = font.MeasureString(opponentAvatar);
            float opponentAvatarScale = iconSize / Math.Max(opponentAvatarSize.X, opponentAvatarSize.Y);
            Vector2 opponentAvatarPos = new Vector2(
                opponentIconX + iconSize / 2 - (opponentAvatarSize.X * opponentAvatarScale) / 2,
                iconY + iconSize / 2 - (opponentAvatarSize.Y * opponentAvatarScale) / 2
            );
            
            // Background circle for opponent avatar
            DrawCircle(sb, font, opponentIconX + iconSize / 2, iconY + iconSize / 2, iconSize / 2, 
                      new Color(40, 40, 60) * opacity);
            
            sb.DrawString(font, opponentAvatar, opponentAvatarPos, 
                         _settings.GetPlayerColor() * opacity * _iconBounceScale, 
                         0f, Vector2.Zero, opponentAvatarScale, SpriteEffects.None, 0f);
            
            // Draw player avatar
            string playerAvatar = _settings.GetAvatarIcon();
            Vector2 playerAvatarSize = font.MeasureString(playerAvatar);
            float playerAvatarScale = iconSize / Math.Max(playerAvatarSize.X, playerAvatarSize.Y);
            Vector2 playerAvatarPos = new Vector2(
                playerIconX + iconSize / 2 - (playerAvatarSize.X * playerAvatarScale) / 2,
                iconY + iconSize / 2 - (playerAvatarSize.Y * playerAvatarScale) / 2
            );
            
            // Background circle for player avatar
            DrawCircle(sb, font, playerIconX + iconSize / 2, iconY + iconSize / 2, iconSize / 2, 
                      new Color(40, 40, 60) * opacity);
            
            sb.DrawString(font, playerAvatar, playerAvatarPos, 
                         _settings.GetPlayerColor() * opacity * _iconBounceScale, 
                         0f, Vector2.Zero, playerAvatarScale, SpriteEffects.None, 0f);
        }
        
        private void DrawStats(SpriteBatch sb, SpriteFont font, int sw, int sh, bool downscroll, float opacity)
        {
            int statsY = downscroll ? 130 : (sh - 30);
            float statsScale = 0.8f;
            
            // Player stats (bottom row)
            int playerStatsY = statsY;
            
            // Player score (left)
            if (_settings.ShowOpponentScore || true) // Always show player score
            {
                string playerScoreText = $"{_playerUsername}: {_playerScore}";
                sb.DrawString(font, SanitizeText(font, playerScoreText),
                    new Vector2(sw / 2 - 280, playerStatsY), Color.White * opacity, 
                    0f, Vector2.Zero, statsScale, SpriteEffects.None, 0f);
            }
            
            // Player misses (center-left)
            if (_settings.ShowOpponentScore)
            {
                string playerMissesText = $"Misses: {_playerMisses}";
                Color missesColor = _playerMisses > 0 ? new Color(255, 100, 100) : Color.White;
                sb.DrawString(font, SanitizeText(font, playerMissesText),
                    new Vector2(sw / 2 - 80, playerStatsY), missesColor * opacity, 
                    0f, Vector2.Zero, statsScale, SpriteEffects.None, 0f);
            }
            
            // Player accuracy (center-right)
            if (_settings.ShowOpponentScore)
            {
                string playerAccText = $"{_playerAccuracy:F1}%";
                Color accColor = _playerAccuracy >= 95f ? new Color(100, 255, 100) : 
                               _playerAccuracy >= 80f ? Color.Yellow : 
                               _playerAccuracy >= 60f ? new Color(255, 200, 100) : 
                               new Color(255, 100, 100);
                sb.DrawString(font, SanitizeText(font, playerAccText),
                    new Vector2(sw / 2 + 80, playerStatsY), accColor * opacity, 
                    0f, Vector2.Zero, statsScale, SpriteEffects.None, 0f);
            }
            
            // Opponent stats (top row) - only show if enabled
            if (_settings.ShowOpponentScore)
            {
                int opponentStatsY = statsY - 25;
                
                // Opponent score (left)
                string opponentScoreText = $"{_opponentUsername}: {_opponentScore}";
                sb.DrawString(font, SanitizeText(font, opponentScoreText),
                    new Vector2(sw / 2 - 280, opponentStatsY), new Color(200, 180, 255) * opacity, 
                    0f, Vector2.Zero, statsScale, SpriteEffects.None, 0f);
                
                // Opponent misses (center-left)
                string opponentMissesText = $"Misses: {_opponentMisses}";
                Color opponentMissesColor = _opponentMisses > 0 ? new Color(255, 100, 100) : new Color(180, 180, 180);
                sb.DrawString(font, SanitizeText(font, opponentMissesText),
                    new Vector2(sw / 2 - 80, opponentStatsY), opponentMissesColor * opacity, 
                    0f, Vector2.Zero, statsScale, SpriteEffects.None, 0f);
                
                // Opponent accuracy (center-right)
                string opponentAccText = $"{_opponentAccuracy:F1}%";
                Color opponentAccColor = _opponentAccuracy >= 95f ? new Color(100, 255, 100) : 
                                        _opponentAccuracy >= 80f ? Color.Yellow : 
                                        _opponentAccuracy >= 60f ? new Color(255, 200, 100) : 
                                        new Color(255, 100, 100);
                sb.DrawString(font, SanitizeText(font, opponentAccText),
                    new Vector2(sw / 2 + 80, opponentStatsY), opponentAccColor * opacity * 0.8f, 
                    0f, Vector2.Zero, statsScale, SpriteEffects.None, 0f);
            }
        }
        
        /// <summary>
        /// Get opponent notes transparency based on settings.
        /// </summary>
        public float GetOpponentNotesOpacity()
        {
            if (!_settings.ShowOpponentNotes)
                return 0f;
            
            return _settings.GhostTransparency / 100f;
        }
        
        // Helper methods
        
        private void DrawRect(SpriteBatch sb, Texture2D pixel, int x, int y, int w, int h, Color color)
        {
            sb.Draw(pixel, new Rectangle(x, y, w, h), color);
        }
        
        private void DrawCircle(SpriteBatch sb, SpriteFont font, int centerX, int centerY, int radius, Color color)
        {
            // Simple circle approximation using a filled square
            // For a proper circle, you'd need a circle texture or shader
            int size = radius * 2;
            // This is a placeholder - in production you'd want an actual circle sprite
            string circleChar = "O";
            Vector2 circleSize = font.MeasureString(circleChar);
            float scale = size / Math.Max(circleSize.X, circleSize.Y);
            Vector2 pos = new Vector2(
                centerX - (circleSize.X * scale) / 2,
                centerY - (circleSize.Y * scale) / 2
            );
            sb.DrawString(font, circleChar, pos, color, 0f, Vector2.Zero, scale, SpriteEffects.None, 0f);
        }
        
        private string SanitizeText(SpriteFont font, string text)
        {
            if (string.IsNullOrEmpty(text)) return "";
            
            // Replace problematic characters
            text = text.Replace("°", " deg")
                      .Replace("™", "(TM)")
                      .Replace("©", "(C)")
                      .Replace("®", "(R)")
                      .Replace("€", "EUR")
                      .Replace("£", "GBP")
                      .Replace("¥", "YEN")
                      .Replace("•", "*")
                      .Replace("…", "...")
                      .Replace("—", "-")
                      .Replace("–", "-");
            
            // Filter to ASCII 32-126 + whitespace
            var filtered = new System.Text.StringBuilder();
            foreach (char c in text)
            {
                if ((c >= 32 && c <= 126) || char.IsWhiteSpace(c))
                    filtered.Append(c);
                else
                    filtered.Append('?');
            }
            
            return filtered.ToString();
        }
    }
}
