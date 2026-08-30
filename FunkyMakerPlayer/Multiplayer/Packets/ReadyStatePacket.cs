using System;

namespace FunkyMakerPlayer.Multiplayer
{
    /// <summary>
    /// Packet sent when player changes ready state.
    /// Used to synchronize game start between both players.
    /// </summary>
    public class ReadyStatePacket
    {
        /// <summary>
        /// Player who is ready/not ready
        /// </summary>
        public string PlayerId { get; set; } = "";
        
        /// <summary>
        /// Is the player ready to start?
        /// </summary>
        public bool IsReady { get; set; } = false;
        
        /// <summary>
        /// Timestamp when ready state changed
        /// </summary>
        public long Timestamp { get; set; }
        
        public ReadyStatePacket()
        {
            Timestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        }
    }
    
    /// <summary>
    /// Packet sent by server when both players are ready.
    /// Contains countdown information for synchronized start.
    /// </summary>
    public class GameStartCountdownPacket
    {
        /// <summary>
        /// Room/match ID
        /// </summary>
        public string RoomId { get; set; } = "";
        
        /// <summary>
        /// Countdown duration in seconds (e.g., 3 for "3...2...1...GO!")
        /// </summary>
        public int CountdownSeconds { get; set; } = 3;
        
        /// <summary>
        /// Server timestamp when countdown should start
        /// Used for perfect synchronization across clients
        /// </summary>
        public long StartTimestamp { get; set; }
        
        /// <summary>
        /// Server timestamp when game should actually begin
        /// </summary>
        public long GameStartTimestamp { get; set; }
        
        public GameStartCountdownPacket()
        {
            StartTimestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            GameStartTimestamp = StartTimestamp + CountdownSeconds;
        }
    }
    
    /// <summary>
    /// Packet sent to confirm both clients have started the game.
    /// </summary>
    public class GameStartConfirmPacket
    {
        /// <summary>
        /// Player who confirmed start
        /// </summary>
        public string PlayerId { get; set; } = "";
        
        /// <summary>
        /// Client's local timestamp when they started
        /// </summary>
        public long ClientTimestamp { get; set; }
        
        public GameStartConfirmPacket()
        {
            ClientTimestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        }
    }
}
