using System;
using System.Collections.Generic;

namespace FunkyMakerPlayer.Multiplayer
{
    /// <summary>
    /// Network packet classes for client-server communication.
    /// All packets are serialized to JSON for SignalR transmission.
    /// </summary>

    // ══════════════════════════════════════════════════════════════════════════
    // CLIENT → SERVER PACKETS
    // ══════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// Initial connection packet sent when joining server.
    /// </summary>
    public class ConnectPacket
    {
        public string Username { get; set; } = "";
        public string Version { get; set; } = "";
        public long Timestamp { get; set; }
    }

    /// <summary>
    /// Level source type for security.
    /// </summary>
    public enum LevelSource
    {
        Official,  // Downloaded from trusted server (100% safe)
        Local      // Already on user's PC (hash matching only, no transfer)
    }

    /// <summary>
    /// Request to find a match for a specific level.
    /// </summary>
    public class FindMatchPacket
    {
        public string LevelHash { get; set; } = "";
        public string LevelName { get; set; } = "";
        public string Difficulty { get; set; } = "Normal";
        public LevelSource Source { get; set; } = LevelSource.Local;
        public long Timestamp { get; set; }
    }

    /// <summary>
    /// Cancel ongoing matchmaking search.
    /// </summary>
    public class CancelMatchmakingPacket
    {
        public long Timestamp { get; set; }
    }

    /// <summary>
    /// Player is ready to start the game.
    /// </summary>
    public class ReadyPacket
    {
        public bool IsReady { get; set; }
        public long Timestamp { get; set; }
    }

    /// <summary>
    /// Note hit event (sent every time player hits/misses a note).
    /// </summary>
    public class NoteHitPacket
    {
        public int NoteId { get; set; }
        public int Lane { get; set; }  // Lane index (0-3) for visual feedback
        public long Timestamp { get; set; }
        public string Rating { get; set; } = ""; // "Sick", "Good", "Bad", "Shit", "Miss"
        public int Combo { get; set; }
    }

    /// <summary>
    /// Player state update (score, combo, health) - sent periodically.
    /// </summary>
    public class PlayerStatePacket
    {
        public int Score { get; set; }
        public int Combo { get; set; }
        public float Health { get; set; }
        public int SickCount { get; set; }
        public int GoodCount { get; set; }
        public int BadCount { get; set; }
        public int ShitCount { get; set; }
        public int MissCount { get; set; }
        public long Timestamp { get; set; }
    }

    /// <summary>
    /// Request to disconnect from server.
    /// </summary>
    public class DisconnectPacket
    {
        public string Reason { get; set; } = "User disconnected";
        public long Timestamp { get; set; }
    }

    /// <summary>
    /// Ping request to measure latency.
    /// </summary>
    public class PingPacket
    {
        public long ClientTimestamp { get; set; }
    }

    // ══════════════════════════════════════════════════════════════════════════
    // SERVER → CLIENT PACKETS
    // ══════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// Official level information from server.
    /// </summary>
    public class OfficialLevelInfo
    {
        public string Id { get; set; } = "";
        public string Name { get; set; } = "";
        public string Hash { get; set; } = "";
        public long FileSize { get; set; }
        public string Filename { get; set; } = "";
        public string Difficulty { get; set; } = "Normal";
        public string Description { get; set; } = "";
    }

    /// <summary>
    /// List of official levels available for download.
    /// </summary>
    public class LevelListPacket
    {
        public List<OfficialLevelInfo> Levels { get; set; } = new();
        public long Timestamp { get; set; }
    }

    /// <summary>
    /// Match found response with opponent info.
    /// </summary>
    public class MatchFoundPacket
    {
        public string OpponentName { get; set; } = "";
        public string RoomId { get; set; } = "";
        public int PlayerRole { get; set; } = 1;    // 1 = Player 1, 2 = Player 2
        public string LevelHash { get; set; } = ""; // Level hash for verification
        public long Timestamp { get; set; }
    }

    /// <summary>
    /// Both players ready - start synchronized countdown.
    /// </summary>
    public class CountdownStartPacket
    {
        public long CountdownStartTime { get; set; }  // Server timestamp when countdown should start
        public long Timestamp { get; set; }
    }

    /// <summary>
    /// Game start signal with synchronized timestamp.
    /// </summary>
    public class GameStartPacket
    {
        public string LevelData { get; set; } = ""; // JSON or level path
        public string LevelHash { get; set; } = ""; // Level hash for verification
        public string OpponentName { get; set; } = ""; // Opponent's username
        public int PlayerRole { get; set; } = 1;    // 1 = Player 1 (chart 0), 2 = Player 2 (chart 1)
        public long StartTimestamp { get; set; }    // Server-provided start time
        public long Timestamp { get; set; }
    }

    /// <summary>
    /// Opponent hit event received from server.
    /// </summary>
    public class OpponentHitPacket
    {
        public int NoteId { get; set; }
        public int Lane { get; set; }  // Lane index (0-3) for visual feedback
        public string Rating { get; set; } = "";
        public int Combo { get; set; }
        public long Timestamp { get; set; }
    }

    /// <summary>
    /// Opponent state update received from server.
    /// </summary>
    public class OpponentStatePacket
    {
        public int Score { get; set; }
        public int Combo { get; set; }
        public float Health { get; set; }
        public int SickCount { get; set; }
        public int GoodCount { get; set; }
        public int BadCount { get; set; }
        public int ShitCount { get; set; }
        public int MissCount { get; set; }
        public long Timestamp { get; set; }
    }

    /// <summary>
    /// Game end signal with results.
    /// </summary>
    public class GameEndPacket
    {
        public bool Won { get; set; }
        public int FinalScore { get; set; }
        public int OpponentScore { get; set; }
        public string WinnerName { get; set; } = "";
        public long Timestamp { get; set; }
    }

    /// <summary>
    /// Server time synchronization packet.
    /// </summary>
    public class SyncPacket
    {
        public long ServerTimestamp { get; set; }
    }

    /// <summary>
    /// Pong response with latency measurement.
    /// </summary>
    public class PongPacket
    {
        public long ServerTimestamp { get; set; }
        public long ClientTimestamp { get; set; }
        public int Ping { get; set; } // Calculated ping in ms
    }

    /// <summary>
    /// Error packet from server.
    /// </summary>
    public class ErrorPacket
    {
        public string ErrorMessage { get; set; } = "";
        public string ErrorCode { get; set; } = "";
        public long Timestamp { get; set; }
    }

    /// <summary>
    /// Opponent disconnected notification.
    /// </summary>
    public class OpponentDisconnectedPacket
    {
        public string Reason { get; set; } = "";
        public long Timestamp { get; set; }
    }
    
    // ══════════════════════════════════════════════════════════════════════════
    // ENHANCED MULTIPLAYER PACKETS (v0.2.4)
    // ══════════════════════════════════════════════════════════════════════════
    
    /// <summary>
    /// Request global leaderboard from server.
    /// </summary>
    public class GetLeaderboardPacket
    {
        public int Skip { get; set; } = 0;
        public int Take { get; set; } = 100;
        public long Timestamp { get; set; }
    }
    
    /// <summary>
    /// Leaderboard entry data.
    /// </summary>
    public class LeaderboardEntryPacket
    {
        public string PlayerId { get; set; } = "";
        public string Username { get; set; } = "";
        public int AvatarIndex { get; set; } = 0;
        public int ColorHue { get; set; } = 180;
        public long TotalScore { get; set; } = 0;
        public int Wins { get; set; } = 0;
        public int Losses { get; set; } = 0;
        public int Draws { get; set; } = 0;
        public long HighestScore { get; set; } = 0;
        public int HighestCombo { get; set; } = 0;
        public float BestAccuracy { get; set; } = 0f;
        public int EloRating { get; set; } = 1000;
        public long LastPlayedTimestamp { get; set; }
        public long CreatedTimestamp { get; set; }
    }
    
    /// <summary>
    /// Global leaderboard response from server.
    /// </summary>
    public class LeaderboardResponsePacket
    {
        public List<LeaderboardEntryPacket> Entries { get; set; } = new();
        public int TotalPlayers { get; set; } = 0;
        public long Timestamp { get; set; }
    }
    
    /// <summary>
    /// Request match history from server.
    /// </summary>
    public class GetMatchHistoryPacket
    {
        public string? PlayerId { get; set; } = null;
        public int Skip { get; set; } = 0;
        public int Take { get; set; } = 50;
        public long Timestamp { get; set; }
    }
    
    /// <summary>
    /// Player data within a match.
    /// </summary>
    public class PlayerMatchData
    {
        public string PlayerId { get; set; } = "";
        public string Username { get; set; } = "";
        public int AvatarIndex { get; set; } = 0;
        public int ColorHue { get; set; } = 180;
        public long Score { get; set; } = 0;
        public float Accuracy { get; set; } = 0f;
        public int MaxCombo { get; set; } = 0;
        public int Misses { get; set; } = 0;
        public bool Disconnected { get; set; } = false;
    }
    
    /// <summary>
    /// Match history entry.
    /// </summary>
    public class MatchHistoryPacket
    {
        public string MatchId { get; set; } = "";
        public string LevelName { get; set; } = "";
        public PlayerMatchData Player1 { get; set; } = new();
        public PlayerMatchData Player2 { get; set; } = new();
        public int Winner { get; set; } = 0;
        public int DurationSeconds { get; set; } = 0;
        public long Timestamp { get; set; }
    }
    
    /// <summary>
    /// Match history response from server.
    /// </summary>
    public class MatchHistoryResponsePacket
    {
        public List<MatchHistoryPacket> Matches { get; set; } = new();
        public int TotalMatches { get; set; } = 0;
        public long Timestamp { get; set; }
    }

    // ==================== PvP Character Sync Packets ====================
    
    /// <summary>
    /// Packet to sync player character sprite data at match start
    /// </summary>
    public class PlayerCharacterPacket
    {
        public string PlayerId { get; set; } = string.Empty;
        public string CharacterName { get; set; } = string.Empty;
        public bool HasCustomSprite { get; set; }
        public byte[]? SpriteData { get; set; } // PNG data if custom
        public int SpriteWidth { get; set; }
        public int SpriteHeight { get; set; }
        public string CharacterColor { get; set; } = "#FFFFFF"; // Hex color
    }

    /// <summary>
    /// Packet to sync character animation state
    /// </summary>
    public class CharacterAnimationPacket
    {
        public string PlayerId { get; set; } = string.Empty;
        public string AnimationState { get; set; } = "idle"; // idle, sing_left, sing_down, sing_up, sing_right
        public float AnimationTime { get; set; }
        public int Direction { get; set; } // 0=left, 1=down, 2=up, 3=right
    }

    /// <summary>
    /// Enhanced match found packet with character info
    /// </summary>
    public class MatchFoundPacketV2
    {
        public string OpponentId { get; set; } = string.Empty;
        public string OpponentName { get; set; } = string.Empty;
        public string LevelId { get; set; } = string.Empty;
        public string LevelName { get; set; } = string.Empty;
        public LevelSource Source { get; set; }
        public string? LevelUrl { get; set; }
        public PlayerCharacterPacket? OpponentCharacter { get; set; }
    }
}
