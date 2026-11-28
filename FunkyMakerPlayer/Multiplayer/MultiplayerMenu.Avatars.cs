#nullable enable
using System;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using FunkyMakerPlayer.Multiplayer.Rooms;

namespace FunkyMakerPlayer.Multiplayer
{
    public partial class MultiplayerMenu
    {
        /// <summary>
        /// A player's round Discord picture (protocol 5). Until it has loaded, or for players without one,
        /// a disc in their colour with their initial. The colour itself now tints their name.
        /// </summary>
        private void DrawAvatar(SpriteBatch sb, SpriteFont? font, string? url, Rectangle rect, int hue, string name, float alpha = 1f)
        {
            if (rect.Width <= 0 || rect.Height <= 0) return;
            AvatarCache.Instance.Draw(sb, sb.GraphicsDevice, url, rect, MultiplayerSettings.ColorFromHue(hue), name,
                (text, center, color, px) =>
                {
                    int size = Math.Max(9, (int)px);
                    Vector2 sz = MeasureText(font, text, size);
                    DrawText(sb, font, text, center - sz / 2f, color, size);
                }, alpha);
        }

        /// <summary>The name colour for a player hue: their colour, lifted a bit so it reads on the dark panels.</summary>
        private static Color NameColor(int hue) => Color.Lerp(MultiplayerSettings.ColorFromHue(hue), Color.White, 0.25f);

        /// <summary>Guests can't create or join rooms: shows why and starts the Discord login.</summary>
        private bool BlockGuest()
        {
            if (!NetworkManager.Instance.IsGuest) return false;
            ShowInfo("Log in with Discord to create or join rooms. Opening Discord...");
            DiscordLogin.Instance.Start();
            return true;
        }
    }
}
