using System;
using System.Collections.Generic;

namespace FunkyMakerPlayer.Multiplayer
{
    // Packets shared with the online server that are not part of the room protocol
    // (that one is in Rooms/RoomProtocol.cs): ping, official level list, leaderboard / history.
    // The old 1v1 matchmaking packets (FindMatch, MatchFound, GameStart, Opponent*) were removed
    // with the room system (fork).

    /// <summary>Where a level comes from.</summary>
    public enum LevelSource
    {
        Official,  // Downloaded from the server's verified_levels
        Local      // A file in the player's levels folder
    }

    /// <summary>Ping request to measure latency (and sync the clock).</summary>
    public class PingPacket
    {
        public long ClientTimestamp { get; set; }
    }

    /// <summary>Pong response with the server time.</summary>
    public class PongPacket
    {
        public long ServerTimestamp { get; set; }
        public long ClientTimestamp { get; set; }
        public int Ping { get; set; }
    }

    /// <summary>Official level information from /api/verified_levels.</summary>
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

    /// <summary>List of official levels available for download.</summary>
    public class LevelListPacket
    {
        public List<OfficialLevelInfo> Levels { get; set; } = new();
        public long Timestamp { get; set; }
    }

    // ══════════════════════════════════════════════════════════════════════════
    // LEADERBOARD
    // ══════════════════════════════════════════════════════════════════════════

    public class GetLeaderboardPacket
    {
        public int Skip { get; set; } = 0;
        public int Take { get; set; } = 100;
        public long Timestamp { get; set; }
    }

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

    public class LeaderboardResponsePacket
    {
        public List<LeaderboardEntryPacket> Entries { get; set; } = new();
        public int TotalPlayers { get; set; } = 0;
        public long Timestamp { get; set; }
    }

    public class GetMatchHistoryPacket
    {
        public string? PlayerId { get; set; } = null;
        public int Skip { get; set; } = 0;
        public int Take { get; set; } = 50;
        public long Timestamp { get; set; }
    }

    /// <summary>One player in a recorded match.</summary>
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
        /// <summary>Room system: 1-based place, team (-1 none), side (0 player / 1 enemy), ELO change.</summary>
        public int Place { get; set; } = 0;
        public int Team { get; set; } = -1;
        public int Side { get; set; } = 0;
        public int EloChange { get; set; } = 0;
    }

    /// <summary>A recorded match (server history). Player1/Player2 are the first two of <see cref="Players"/>.</summary>
    public class MatchHistoryPacket
    {
        public string MatchId { get; set; } = "";
        public string LevelName { get; set; } = "";
        public PlayerMatchData Player1 { get; set; } = new();
        public PlayerMatchData Player2 { get; set; } = new();
        public int Winner { get; set; } = 0;
        public int DurationSeconds { get; set; } = 0;
        public long Timestamp { get; set; }
        /// <summary>"versus" / "teams" / "mirror" (empty for old 1v1 matches).</summary>
        public string Mode { get; set; } = "";
        public List<PlayerMatchData> Players { get; set; } = new();
        public int WinningTeam { get; set; } = -1;
    }

    public class MatchHistoryResponsePacket
    {
        public List<MatchHistoryPacket> Matches { get; set; } = new();
        public int TotalMatches { get; set; } = 0;
        public long Timestamp { get; set; }
    }
}
