using System;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;

namespace FunkyMakerPlayer.Multiplayer.UI
{
    /// <summary>
    /// Visual indicator for connection status, ping, and quality during multiplayer.
    /// Displays in-game overlay with real-time network information.
    /// </summary>
    public class ConnectionIndicator
    {
        private float _pulseTimer = 0f;
        private const float PULSE_SPEED = 2f;
        
        // Display modes
        public enum DisplayMode
        {
            Compact,    // Just ping and quality icon
            Detailed,   // Ping, quality, sync status, packet loss
            Hidden      // Don't show
        }
        
        private DisplayMode _mode = DisplayMode.Compact;
        public DisplayMode Mode
        {
            get => _mode;
            set => _mode = value;
        }
        
        // Positioning
        public enum IndicatorPosition
        {
            TopLeft,
            TopRight,
            BottomLeft,
            BottomRight
        }
        
        private IndicatorPosition _position = IndicatorPosition.TopRight;
        public IndicatorPosition Position
        {
            get => _position;
            set => _position = value;
        }
        
        // Visual settings
        private const int COMPACT_WIDTH = 120;
        private const int COMPACT_HEIGHT = 40;
        private const int DETAILED_WIDTH = 200;
        private const int DETAILED_HEIGHT = 90;
        private const int MARGIN = 10;
        
        // Colors
        private static readonly Color BG_COLOR = new Color(0, 0, 0, 180);
        private static readonly Color BORDER_EXCELLENT = new Color(80, 220, 120);
        private static readonly Color BORDER_GOOD = new Color(100, 200, 255);
        private static readonly Color BORDER_FAIR = new Color(255, 200, 80);
        private static readonly Color BORDER_POOR = new Color(255, 150, 80);
        private static readonly Color BORDER_CRITICAL = new Color(255, 100, 100);
        private static readonly Color TEXT_COLOR = Color.White;
        private static readonly Color TEXT_SECONDARY = new Color(180, 180, 200);
        
        public void Update(float deltaTime)
        {
            _pulseTimer += deltaTime * PULSE_SPEED;
        }
        
        /// <summary>
        /// Draw the connection indicator.
        /// </summary>
        public void Draw(SpriteBatch sb, SpriteFont font, Texture2D pixel, int screenWidth, int screenHeight)
        {
            if (_mode == DisplayMode.Hidden)
                return;
            
            if (!NetworkManager.Instance.IsConnected)
                return; // Only show when connected
            
            // Get current network stats
            int ping = NetworkManager.Instance.Ping;
            ConnectionQuality quality = NetworkManager.Instance.Quality;
            bool isSynced = TimeSyncManager.Instance.IsSynced;
            int estimatedLag = TimeSyncManager.Instance.GetEstimatedLag();
            
            // Determine size based on mode
            int width = _mode == DisplayMode.Compact ? COMPACT_WIDTH : DETAILED_WIDTH;
            int height = _mode == DisplayMode.Compact ? COMPACT_HEIGHT : DETAILED_HEIGHT;
            
            // Calculate position
            int x, y;
            switch (_position)
            {
                case IndicatorPosition.TopLeft:
                    x = MARGIN;
                    y = MARGIN;
                    break;
                case IndicatorPosition.TopRight:
                    x = screenWidth - width - MARGIN;
                    y = MARGIN;
                    break;
                case IndicatorPosition.BottomLeft:
                    x = MARGIN;
                    y = screenHeight - height - MARGIN;
                    break;
                case IndicatorPosition.BottomRight:
                default:
                    x = screenWidth - width - MARGIN;
                    y = screenHeight - height - MARGIN;
                    break;
            }
            
            // Determine border color based on quality
            Color borderColor = GetQualityColor(quality);
            
            // Pulsing effect for poor/critical quality
            float alpha = 1f;
            if (quality == ConnectionQuality.Poor || quality == ConnectionQuality.Critical)
            {
                alpha = 0.7f + (float)Math.Sin(_pulseTimer * 2) * 0.3f;
                borderColor *= alpha;
            }
            
            // Draw background
            sb.Draw(pixel, new Rectangle(x, y, width, height), BG_COLOR);
            
            // Draw border (thicker for bad connection)
            int borderWidth = (quality == ConnectionQuality.Poor || quality == ConnectionQuality.Critical) ? 3 : 2;
            DrawBorder(sb, pixel, x, y, width, height, borderColor, borderWidth);
            
            if (_mode == DisplayMode.Compact)
            {
                DrawCompactMode(sb, font, x, y, width, height, ping, quality, isSynced);
            }
            else
            {
                DrawDetailedMode(sb, font, x, y, width, height, ping, quality, isSynced, estimatedLag);
            }
        }
        
        private void DrawCompactMode(SpriteBatch sb, SpriteFont font, int x, int y, int w, int h, 
            int ping, ConnectionQuality quality, bool isSynced)
        {
            // Quality icon
            string icon = GetQualityIcon(quality);
            Vector2 iconSize = font.MeasureString(icon);
            Vector2 iconPos = new Vector2(x + 10, y + h / 2 - iconSize.Y / 2);
            sb.DrawString(font, icon, iconPos, GetQualityColor(quality));
            
            // Ping text - Always show ping value, even if not fully synced
            string pingText = ping > 0 ? $"{ping}ms" : "...";
            Vector2 textSize = font.MeasureString(pingText);
            Vector2 textPos = new Vector2(x + w - textSize.X - 10, y + h / 2 - textSize.Y / 2);
            
            // Dim text color if not synced yet
            Color textColor = isSynced ? TEXT_COLOR : TEXT_SECONDARY;
            sb.DrawString(font, pingText, textPos, textColor);
        }
        
        private void DrawDetailedMode(SpriteBatch sb, SpriteFont font, int x, int y, int w, int h,
            int ping, ConnectionQuality quality, bool isSynced, int estimatedLag)
        {
            int lineY = y + 8;
            int lineHeight = 18;
            
            // Line 1: Quality
            string qualityText = $"{GetQualityIcon(quality)} {quality}";
            sb.DrawString(font, qualityText, new Vector2(x + 8, lineY), GetQualityColor(quality), 0f, Vector2.Zero, 0.8f, SpriteEffects.None, 0f);
            lineY += lineHeight;
            
            // Line 2: Ping - Always show value if available
            string pingText = ping > 0 ? $"Ping: {ping}ms" : "Ping: Measuring...";
            Color pingColor = isSynced ? TEXT_COLOR : TEXT_SECONDARY;
            sb.DrawString(font, pingText, new Vector2(x + 8, lineY), pingColor, 0f, Vector2.Zero, 0.8f, SpriteEffects.None, 0f);
            lineY += lineHeight;
            
            // Line 3: Lag compensation
            string lagText = estimatedLag > 0 ? $"Lag: ~{estimatedLag}ms" : "Lag: Calculating...";
            sb.DrawString(font, lagText, new Vector2(x + 8, lineY), TEXT_SECONDARY, 0f, Vector2.Zero, 0.8f, SpriteEffects.None, 0f);
            lineY += lineHeight;
            
            // Line 4: Sync status
            string syncText = isSynced ? "[OK] Synced" : "[..] Syncing...";
            Color syncColor = isSynced ? BORDER_EXCELLENT : TEXT_SECONDARY;
            sb.DrawString(font, syncText, new Vector2(x + 8, lineY), syncColor, 0f, Vector2.Zero, 0.7f, SpriteEffects.None, 0f);
        }
        
        private void DrawBorder(SpriteBatch sb, Texture2D pixel, int x, int y, int w, int h, Color color, int thickness)
        {
            // Top
            sb.Draw(pixel, new Rectangle(x, y, w, thickness), color);
            // Bottom
            sb.Draw(pixel, new Rectangle(x, y + h - thickness, w, thickness), color);
            // Left
            sb.Draw(pixel, new Rectangle(x, y, thickness, h), color);
            // Right
            sb.Draw(pixel, new Rectangle(x + w - thickness, y, thickness, h), color);
        }
        
        private Color GetQualityColor(ConnectionQuality quality)
        {
            return quality switch
            {
                ConnectionQuality.Excellent => BORDER_EXCELLENT,
                ConnectionQuality.Good => BORDER_GOOD,
                ConnectionQuality.Fair => BORDER_FAIR,
                ConnectionQuality.Poor => BORDER_POOR,
                ConnectionQuality.Critical => BORDER_CRITICAL,
                _ => TEXT_SECONDARY
            };
        }
        
        private string GetQualityIcon(ConnectionQuality quality)
        {
            return quality switch
            {
                ConnectionQuality.Excellent => "||||",
                ConnectionQuality.Good => "|||'",
                ConnectionQuality.Fair => "||''",
                ConnectionQuality.Poor => "|'''",
                ConnectionQuality.Critical => "''''",
                _ => "...."
            };
        }
    }
}
