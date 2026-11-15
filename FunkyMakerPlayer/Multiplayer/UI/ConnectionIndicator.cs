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
        private FontRenderer? _fontRenderer;

        public void SetFontRenderer(FontRenderer? fontRenderer) => _fontRenderer = fontRenderer;
        
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
        
        private IndicatorPosition _position = IndicatorPosition.TopLeft;
        public IndicatorPosition Position
        {
            get => _position;
            set => _position = value;
        }
        
        // Base visual settings (scaled with screen size)
        private const int COMPACT_WIDTH = 128;
        private const int COMPACT_HEIGHT = 38;
        private const int DETAILED_WIDTH = 236;
        private const int DETAILED_HEIGHT = 104;
        private const int MARGIN = 14;
        
        // Colors
        private static readonly Color BG_COLOR = new Color(18, 18, 30, 225);
        private static readonly Color BG_SHADOW = new Color(0, 0, 0, 120);
        private static readonly Color BAR_INACTIVE = new Color(65, 68, 90, 170);
        private static readonly Color BORDER_EXCELLENT = new Color(80, 220, 120);
        private static readonly Color BORDER_GOOD = new Color(100, 200, 255);
        private static readonly Color BORDER_FAIR = new Color(255, 200, 80);
        private static readonly Color BORDER_POOR = new Color(255, 150, 80);
        private static readonly Color BORDER_CRITICAL = new Color(255, 100, 100);
        private static readonly Color TEXT_COLOR = Color.White;
        private static readonly Color TEXT_SECONDARY = new Color(175, 180, 205);
        
        public void Update(float deltaTime)
        {
            _pulseTimer += deltaTime * PULSE_SPEED;
        }
        
        /// <summary>
        /// Draw the connection indicator.
        /// </summary>
        public void Draw(SpriteBatch sb, SpriteFont font, Texture2D pixel, int screenWidth, int screenHeight, int topRightOffsetY = 0)
        {
            if (_mode == DisplayMode.Hidden)
                return;
            
            if (!NetworkManager.Instance.IsConnected)
                return; // Only show when connected
            
            // Get current network stats
            int ping = NetworkManager.Instance.Ping;
            bool hasPing = NetworkManager.Instance.HasMeasuredPing;
            ConnectionQuality quality = NetworkManager.Instance.Quality;
            bool isSynced = TimeSyncManager.Instance.IsSynced;
            int estimatedLag = TimeSyncManager.Instance.GetEstimatedLag();
            long clockOffsetMs = TimeSyncManager.Instance.GetClockOffsetMs();
            int syncSamples = TimeSyncManager.Instance.GetSampleCount();
            
            float uiScale = Math.Clamp(Math.Min(screenWidth / 1280f, screenHeight / 720f), 0.72f, 1.5f);
            int margin = Math.Max(10, (int)MathF.Round(MARGIN * uiScale));
            int width = (int)MathF.Round((_mode == DisplayMode.Compact ? COMPACT_WIDTH : DETAILED_WIDTH) * uiScale);
            int height = (int)MathF.Round((_mode == DisplayMode.Compact ? COMPACT_HEIGHT : DETAILED_HEIGHT) * uiScale);
            
            // Calculate position
            int x, y;
            switch (_position)
            {
                case IndicatorPosition.TopLeft:
                    x = margin;
                    y = margin;
                    break;
                case IndicatorPosition.TopRight:
                    x = screenWidth - width - margin;
                    y = topRightOffsetY > 0 ? Math.Max(margin, topRightOffsetY) : margin;
                    break;
                case IndicatorPosition.BottomLeft:
                    x = margin;
                    y = screenHeight - height - margin;
                    break;
                case IndicatorPosition.BottomRight:
                default:
                    x = screenWidth - width - margin;
                    y = screenHeight - height - margin;
                    break;
            }
            x = Math.Clamp(x, 4, Math.Max(4, screenWidth - width - 4));
            y = Math.Clamp(y, 4, Math.Max(4, screenHeight - height - 4));
            
            // Determine accent color based on quality
            Color qualityColor = GetQualityColor(quality);
            
            // Pulsing effect for poor/critical quality
            Color borderColor = qualityColor * 0.75f;
            if (quality == ConnectionQuality.Poor || quality == ConnectionQuality.Critical)
            {
                float pulse = 0.65f + (float)Math.Sin(_pulseTimer * 2.2f) * 0.35f;
                borderColor = qualityColor * pulse;
            }
            
            // Soft shadow + card body + subtle border + left accent bar
            sb.Draw(pixel, new Rectangle(x + 3, y + 3, width, height), BG_SHADOW);
            sb.Draw(pixel, new Rectangle(x, y, width, height), BG_COLOR);
            int borderWidth = (quality == ConnectionQuality.Poor || quality == ConnectionQuality.Critical) ? 2 : 1;
            DrawBorder(sb, pixel, x, y, width, height, borderColor, borderWidth);
            int accentW = Math.Max(3, (int)MathF.Round(3 * uiScale));
            sb.Draw(pixel, new Rectangle(x, y, accentW, height), qualityColor);
            
            if (_mode == DisplayMode.Compact)
            {
                DrawCompactMode(sb, font, pixel, x, y, width, height, uiScale, ping, hasPing, quality, qualityColor, isSynced);
            }
            else
            {
                DrawDetailedMode(sb, font, pixel, x, y, width, height, uiScale, ping, hasPing, quality, qualityColor, isSynced, estimatedLag, clockOffsetMs, syncSamples);
            }
        }

        private void DrawSignalBars(SpriteBatch sb, Texture2D pixel, int x, int baselineY, float uiScale, ConnectionQuality quality, Color activeColor)
        {
            int activeBars = quality switch
            {
                ConnectionQuality.Excellent => 4,
                ConnectionQuality.Good => 3,
                ConnectionQuality.Fair => 2,
                ConnectionQuality.Poor => 1,
                _ => 0
            };

            int barW = Math.Max(3, (int)MathF.Round(4 * uiScale));
            int gap = Math.Max(2, (int)MathF.Round(2 * uiScale));
            for (int i = 0; i < 4; i++)
            {
                int barH = Math.Max(4, (int)MathF.Round((5 + i * 3.5f) * uiScale));
                Color c = i < activeBars ? activeColor : BAR_INACTIVE;
                sb.Draw(pixel, new Rectangle(x + i * (barW + gap), baselineY - barH, barW, barH), c);
            }
        }

        private Vector2 MeasureText(SpriteFont? font, string text, int fontSize)
        {
            if (string.IsNullOrEmpty(text)) return Vector2.Zero;
            if (_fontRenderer != null)
                return _fontRenderer.MeasureString(text, fontSize);
            if (font != null)
                return font.MeasureString(text) * (fontSize / 28f);
            return Vector2.Zero;
        }

        private void DrawText(SpriteBatch sb, SpriteFont? font, string text, Vector2 pos, Color color, int fontSize)
        {
            if (string.IsNullOrEmpty(text)) return;
            if (_fontRenderer != null)
            {
                _fontRenderer.DrawString(sb, text, pos, color, fontSize);
            }
            else if (font != null)
            {
                float scale = fontSize / 28f;
                sb.DrawString(font, text, pos, color, 0f, Vector2.Zero, scale, SpriteEffects.None, 0f);
            }
        }
        
        private void DrawCompactMode(SpriteBatch sb, SpriteFont font, Texture2D pixel, int x, int y, int w, int h,
            float uiScale, int ping, bool hasPing, ConnectionQuality quality, Color qualityColor, bool isSynced)
        {
            int padX = Math.Max(10, (int)MathF.Round(12 * uiScale));
            int baselineY = y + h / 2 + (int)MathF.Round(8 * uiScale);
            DrawSignalBars(sb, pixel, x + padX, baselineY, uiScale, quality, qualityColor);
            
            int fontSize = Math.Clamp((int)MathF.Round(16 * uiScale), 12, 24);
            string pingText = hasPing ? $"{ping} ms" : "...";
            Vector2 textSize = MeasureText(font, pingText, fontSize);
            Vector2 textPos = new Vector2(x + w - textSize.X - padX, y + (h - textSize.Y) / 2f);
            
            Color textColor = hasPing ? TEXT_COLOR : TEXT_SECONDARY;
            DrawText(sb, font, pingText, textPos, textColor, fontSize);
        }
        
        private void DrawDetailedMode(SpriteBatch sb, SpriteFont font, Texture2D pixel, int x, int y, int w, int h,
            float uiScale, int ping, bool hasPing, ConnectionQuality quality, Color qualityColor, bool isSynced, int estimatedLag, long clockOffsetMs, int syncSamples)
        {
            int padX = Math.Max(10, (int)MathF.Round(12 * uiScale));
            int padY = Math.Max(7, (int)MathF.Round(8 * uiScale));
            int headerSize = Math.Clamp((int)MathF.Round(15 * uiScale), 12, 22);
            int bodySize = Math.Clamp((int)MathF.Round(14 * uiScale), 11, 20);
            int rowStep = Math.Max(16, (int)MathF.Round(21 * uiScale));

            int lineY = y + padY;
            DrawSignalBars(sb, pixel, x + padX, lineY + (int)MathF.Round(15 * uiScale), uiScale, quality, qualityColor);
            DrawText(sb, font, quality.ToString().ToUpperInvariant(),
                new Vector2(x + padX + (int)MathF.Round(28 * uiScale), lineY), qualityColor, headerSize);

            // Divider
            int divY = lineY + rowStep;
            sb.Draw(pixel, new Rectangle(x + padX, divY - 2, w - padX * 2, 1), Color.White * 0.08f);
            lineY += rowStep + 2;

            // Ping
            string pingText = hasPing ? $"Ping: {ping} ms" : "Ping: Measuring...";
            DrawText(sb, font, pingText, new Vector2(x + padX, lineY), hasPing ? TEXT_COLOR : TEXT_SECONDARY, bodySize);
            lineY += rowStep;

            // Clock offset & one-way lag
            string offsetStr = Math.Abs(clockOffsetMs) >= 1000
                ? $"{clockOffsetMs / 1000.0:+0.0;-0.0;0.0}s"
                : $"{clockOffsetMs:+#;-#;0} ms";
            string lagText = isSynced
                ? $"Offset: {offsetStr} (~{estimatedLag}ms)"
                : (hasPing ? $"Offset: Syncing ({syncSamples}/3)" : "Offset: Syncing...");
            DrawText(sb, font, lagText, new Vector2(x + padX, lineY), TEXT_SECONDARY, bodySize);
            lineY += rowStep;

            // Sync badge with clean indicator square
            int dotSize = Math.Max(6, (int)MathF.Round(7 * uiScale));
            Color syncColor = isSynced ? BORDER_EXCELLENT : BORDER_FAIR;
            float dotAlpha = isSynced ? 1f : (0.5f + 0.5f * MathF.Sin(_pulseTimer * 3f));
            sb.Draw(pixel, new Rectangle(x + padX, lineY + (int)MathF.Round(4 * uiScale), dotSize, dotSize), syncColor * dotAlpha);
            string syncText = isSynced ? "Time Synced" : (hasPing ? $"Syncing Clock ({syncSamples}/3)" : "Syncing Clock...");
            DrawText(sb, font, syncText, new Vector2(x + padX + dotSize + 7, lineY), syncColor, bodySize);
        }
        
        private void DrawBorder(SpriteBatch sb, Texture2D pixel, int x, int y, int w, int h, Color color, int thickness)
        {
            sb.Draw(pixel, new Rectangle(x, y, w, thickness), color);
            sb.Draw(pixel, new Rectangle(x, y + h - thickness, w, thickness), color);
            sb.Draw(pixel, new Rectangle(x, y, thickness, h), color);
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
    }
}
