using System;

namespace FunkyMakerPlayer.Multiplayer
{
    /// <summary>
    /// Packet sent when a player connects or updates their profile.
    /// Contains user identification and customization data.
    /// </summary>
    public class PlayerProfilePacket
    {
        /// <summary>
        /// Unique player ID (can be connection ID or persistent ID)
        /// </summary>
        public string PlayerId { get; set; } = "";
        
        /// <summary>
        /// Player's display name
        /// </summary>
        public string Username { get; set; } = "Player";
        
        /// <summary>
        /// Avatar index (0-9)
        /// </summary>
        public int AvatarIndex { get; set; } = 0;
        
        /// <summary>
        /// Player color as HSV hue (0-360)
        /// </summary>
        public int PlayerColorHue { get; set; } = 180;

        /// <summary>
        /// Alias matching FunkyMakerServer's PlayerProfilePacket.ColorHue property name.
        /// </summary>
        public int ColorHue
        {
            get => PlayerColorHue;
            set => PlayerColorHue = value;
        }
        
        /// <summary>
        /// Timestamp when profile was last updated
        /// </summary>
        public long Timestamp { get; set; }
        
        /// <summary>
        /// Client version (for compatibility checking)
        /// </summary>
        public string ClientVersion { get; set; } = "0.2.4";
        
        public PlayerProfilePacket()
        {
            Timestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        }
        
        /// <summary>
        /// Create from current settings
        /// </summary>
        public static PlayerProfilePacket FromSettings(MultiplayerSettings settings, string playerId)
        {
            return new PlayerProfilePacket
            {
                PlayerId = playerId,
                Username = settings.Username,
                AvatarIndex = settings.AvatarIndex,
                PlayerColorHue = settings.PlayerColorHue,
                ClientVersion = typeof(PlayerProfilePacket).Assembly.GetName().Version?.ToString() ?? "0.2.4"
            };
        }
    }
}
