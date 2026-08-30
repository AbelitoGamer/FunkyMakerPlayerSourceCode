using System;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;

namespace FunkyMakerPlayer.Multiplayer.UI
{
    /// <summary>
    /// Static helper methods for drawing modern UI elements.
    /// </summary>
    public static class UIHelpers
    {
        /// <summary>
        /// Draw a smooth gradient background from top to bottom.
        /// </summary>
        public static void DrawGradientBackground(SpriteBatch sb, Texture2D pixel, int sw, int sh)
        {
            // Dark gradient from top to bottom (20 steps for smoothness)
            int steps = 20;
            for (int i = 0; i < steps; i++)
            {
                float ratio = i / (float)steps;
                Color color = Color.Lerp(new Color(10, 10, 25), new Color(5, 5, 15), ratio);
                int y = (sh / steps) * i;
                int h = (sh / steps) + 1;
                sb.Draw(pixel, new Rectangle(0, y, sw, h), color);
            }
        }

        /// <summary>
        /// Draw a panel with shadow, background, and border.
        /// </summary>
        public static void DrawPanel(SpriteBatch sb, Texture2D pixel, int x, int y, int w, int h, 
            Color bgColor, Color borderColor, int borderWidth = 2)
        {
            // Shadow (offset by 4 pixels)
            sb.Draw(pixel, new Rectangle(x + 4, y + 4, w, h), Color.Black * 0.5f);

            // Background
            sb.Draw(pixel, new Rectangle(x, y, w, h), bgColor);

            // Border (top, bottom, left, right)
            sb.Draw(pixel, new Rectangle(x, y, w, borderWidth), borderColor);
            sb.Draw(pixel, new Rectangle(x, y + h - borderWidth, w, borderWidth), borderColor);
            sb.Draw(pixel, new Rectangle(x, y, borderWidth, h), borderColor);
            sb.Draw(pixel, new Rectangle(x + w - borderWidth, y, borderWidth, h), borderColor);
        }

        /// <summary>
        /// Draw a card-style UI element with optional glow effect.
        /// </summary>
        public static void DrawCard(SpriteBatch sb, SpriteFont font, Texture2D pixel, int x, int y, int w, int h, 
            string title, string subtitle, bool selected, bool glowing = false, float pulseTimer = 0f)
        {
            Color bgColor = selected ? new Color(30, 30, 50) : new Color(20, 20, 35);
            Color borderColor = selected ? Color.Yellow : new Color(50, 50, 70);

            if (glowing && selected)
            {
                // Pulsing glow effect
                float pulse = (float)Math.Sin(pulseTimer) * 0.5f + 0.5f;
                Color glowColor = Color.Lerp(Color.Yellow, Color.Orange, pulse) * 0.3f;
                sb.Draw(pixel, new Rectangle(x - 6, y - 6, w + 12, h + 12), glowColor);
            }

            DrawPanel(sb, pixel, x, y, w, h, bgColor, borderColor, 3);

            // Title
            Vector2 titleSize = font.MeasureString(title);
            Vector2 titlePos = new Vector2(x + w / 2 - titleSize.X / 2, y + h / 2 - titleSize.Y / 2 - 10);
            sb.DrawString(font, title, titlePos, selected ? Color.Yellow : Color.White);

            // Subtitle
            if (!string.IsNullOrEmpty(subtitle))
            {
                Vector2 subSize = font.MeasureString(subtitle);
                Vector2 subPos = new Vector2(x + w / 2 - subSize.X / 2, y + h / 2 + 10);
                sb.DrawString(font, subtitle, subPos, Color.Gray);
            }
        }

        /// <summary>
        /// Draw centered text on screen.
        /// </summary>
        public static void DrawCenteredText(SpriteBatch sb, SpriteFont font, string text, int screenWidth, int y, Color color)
        {
            Vector2 size = font.MeasureString(text);
            Vector2 pos = new Vector2(screenWidth / 2 - size.X / 2, y);
            sb.DrawString(font, text, pos, color);
        }

        /// <summary>
        /// Draw centered text within a panel.
        /// </summary>
        public static void DrawCenteredTextInPanel(SpriteBatch sb, SpriteFont font, string text, int panelX, int y, int panelW, Color color)
        {
            Vector2 size = font.MeasureString(text);
            Vector2 pos = new Vector2(panelX + panelW / 2 - size.X / 2, y);
            sb.DrawString(font, text, pos, color);
        }
    }
}
