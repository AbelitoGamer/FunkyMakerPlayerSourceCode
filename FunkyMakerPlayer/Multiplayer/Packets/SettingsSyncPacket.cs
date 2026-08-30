using System;

namespace FunkyMakerPlayer.Multiplayer
{
    /// <summary>
    /// Packet sent to sync gameplay settings between players.
    /// Used to configure HUD and gameplay display options.
    /// </summary>
    public class SettingsSyncPacket
    {
        /// <summary>
        /// Player who owns these settings
        /// </summary>
        public string PlayerId { get; set; } = "";
        
        // ══════════════════════════════════════════════════════════════════════
        // Gameplay Settings (what the player wants to see)
        // ══════════════════════════════════════════════════════════════════════
        
        /// <summary>
        /// Show opponent's notes on screen
        /// </summary>
        public bool ShowOpponentNotes { get; set; } = true;
        
        /// <summary>
        /// Show opponent's score in real-time
        /// </summary>
        public bool ShowOpponentScore { get; set; } = true;
        
        /// <summary>
        /// Show connection quality indicator
        /// </summary>
        public bool ShowConnectionQuality { get; set; } = true;
        
        /// <summary>
        /// Transparency of opponent's notes (0-100%)
        /// </summary>
        public int GhostTransparency { get; set; } = 50;
        
        // ══════════════════════════════════════════════════════════════════════
        // Visual Settings (HUD appearance)
        // ══════════════════════════════════════════════════════════════════════
        
        /// <summary>
        /// HUD opacity (0-100%)
        /// </summary>
        public int HudOpacity { get; set; } = 100;
        
        /// <summary>
        /// Connection indicator position (0=TopLeft, 1=TopRight, 2=BottomLeft, 3=BottomRight)
        /// </summary>
        public int ConnectionIndicatorPosition { get; set; } = 1;
        
        /// <summary>
        /// Interface mode (0=Compact, 1=Full)
        /// </summary>
        public int InterfaceMode { get; set; } = 1;
        
        // ══════════════════════════════════════════════════════════════════════
        // Performance Settings (affect network behavior)
        // ══════════════════════════════════════════════════════════════════════
        
        /// <summary>
        /// Network update rate in Hz (30, 60, or 120)
        /// </summary>
        public int NetworkUpdateRate { get; set; } = 60;
        
        /// <summary>
        /// Enable interpolation for smooth opponent animations
        /// </summary>
        public bool EnableInterpolation { get; set; } = true;
        
        /// <summary>
        /// Timestamp when settings were last changed
        /// </summary>
        public long Timestamp { get; set; }
        
        public SettingsSyncPacket()
        {
            Timestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        }
        
        /// <summary>
        /// Create from current settings
        /// </summary>
        public static SettingsSyncPacket FromSettings(MultiplayerSettings settings, string playerId)
        {
            return new SettingsSyncPacket
            {
                PlayerId = playerId,
                ShowOpponentNotes = settings.ShowOpponentNotes,
                ShowOpponentScore = settings.ShowOpponentScore,
                ShowConnectionQuality = settings.ShowConnectionQuality,
                GhostTransparency = settings.GhostTransparency,
                HudOpacity = settings.HudOpacity,
                ConnectionIndicatorPosition = (int)settings.ConnectionIndicatorPosition,
                InterfaceMode = (int)settings.InterfaceMode,
                NetworkUpdateRate = (int)settings.UpdateRate,
                EnableInterpolation = settings.EnableInterpolation
            };
        }
    }
}
