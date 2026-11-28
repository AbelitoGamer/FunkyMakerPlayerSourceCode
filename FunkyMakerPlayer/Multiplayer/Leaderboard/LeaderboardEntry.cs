using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Serialization;

namespace FunkyMakerPlayer.Multiplayer
{
    /// <summary>
    /// Represents a single entry in the leaderboard.
    /// Can represent overall ranking, level-specific ranking, or match history.
    /// </summary>
    public class LeaderboardEntry
    {
        /// <summary>
        /// Unique player ID
        /// </summary>
        public string PlayerId { get; set; } = "";
        
        /// <summary>
        /// Player's display name
        /// </summary>
        public string Username { get; set; } = "Unknown";
        
        /// <summary>
        /// Player's avatar index
        /// </summary>
        public int AvatarIndex { get; set; } = 0;
        
        /// <summary>
        /// Player's color hue
        /// </summary>
        public int ColorHue { get; set; } = 180;
        /// <summary>Avatar URL (Discord CDN), empty = none (protocol 5).</summary>
        public string Avatar { get; set; } = "";
        
        // ══════════════════════════════════════════════════════════════════════
        // Statistics
        // ══════════════════════════════════════════════════════════════════════
        
        /// <summary>
        /// Total score/rating (used for ranking)
        /// </summary>
        public long TotalScore { get; set; } = 0;
        
        /// <summary>
        /// Total wins
        /// </summary>
        public int Wins { get; set; } = 0;
        
        /// <summary>
        /// Total losses
        /// </summary>
        public int Losses { get; set; } = 0;
        
        /// <summary>
        /// Total draws
        /// </summary>
        public int Draws { get; set; } = 0;
        
        /// <summary>
        /// Win rate percentage (0-100)
        /// </summary>
        public float WinRate => TotalMatches > 0 ? (Wins / (float)TotalMatches) * 100f : 0f;
        
        /// <summary>
        /// Total matches played
        /// </summary>
        public int TotalMatches => Wins + Losses + Draws;
        
        /// <summary>
        /// Highest score achieved in a single match
        /// </summary>
        public long HighestScore { get; set; } = 0;
        
        /// <summary>
        /// Highest combo achieved
        /// </summary>
        public int HighestCombo { get; set; } = 0;
        
        /// <summary>
        /// Best accuracy percentage (0-100)
        /// </summary>
        public float BestAccuracy { get; set; } = 0f;
        
        /// <summary>
        /// Current ranking position (1 = best)
        /// </summary>
        public int Rank { get; set; } = 0;
        
        /// <summary>
        /// ELO rating (optional competitive rating)
        /// </summary>
        public int EloRating { get; set; } = 1000;
        
        /// <summary>
        /// Last time this player played (Unix timestamp)
        /// </summary>
        public long LastPlayedTimestamp { get; set; }
        
        /// <summary>
        /// Account creation timestamp
        /// </summary>
        public long CreatedTimestamp { get; set; }
        
        public LeaderboardEntry()
        {
            CreatedTimestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            LastPlayedTimestamp = CreatedTimestamp;
        }
    }
    
    /// <summary>
    /// Represents a match in the match history.
    /// </summary>
    public class MatchHistoryEntry
    {
        /// <summary>
        /// Unique match ID
        /// </summary>
        public string MatchId { get; set; } = "";
        
        /// <summary>
        /// Level/song name
        /// </summary>
        public string LevelName { get; set; } = "";
        
        /// <summary>
        /// Player 1 info
        /// </summary>
        public PlayerMatchInfo Player1 { get; set; } = new();
        
        /// <summary>
        /// Player 2 info
        /// </summary>
        public PlayerMatchInfo Player2 { get; set; } = new();
        
        /// <summary>
        /// Winner (0=draw, 1=player1, 2=player2)
        /// </summary>
        public int Winner { get; set; } = 0;
        
        /// <summary>
        /// Match duration in seconds
        /// </summary>
        public int DurationSeconds { get; set; } = 0;
        
        /// <summary>
        /// When the match was played
        /// </summary>
        public long Timestamp { get; set; }

        /// <summary>"versus" / "teams" / "mirror"; empty for old 1v1 duels.</summary>
        public string Mode { get; set; } = "";

        /// <summary>Teams mode: 0 = Team Player, 1 = Team Enemy, -1 = draw / not a team match.</summary>
        public int WinningTeam { get; set; } = -1;

        /// <summary>Every participant (room matches). Empty for old 1v1 duels: use <see cref="Participants"/>.</summary>
        public List<PlayerMatchInfo> Players { get; set; } = new();

        /// <summary>True for matches played in rooms (2+ players, any mode).</summary>
        [JsonIgnore]
        public bool IsRoomMatch => Players.Count > 0;

        /// <summary>
        /// All participants ordered by place (disconnected players last). Old 1v1 duels are turned into
        /// a two-player list whose places come from <see cref="Winner"/>.
        /// </summary>
        [JsonIgnore]
        public List<PlayerMatchInfo> Participants
        {
            get
            {
                if (Players.Count > 0)
                    return Players
                        .OrderBy(p => p.Disconnected ? 1 : 0)
                        .ThenBy(p => p.Place <= 0 ? int.MaxValue : p.Place)
                        .ThenByDescending(p => p.Score)
                        .ToList();
                Player1.Place = Winner == 2 ? 2 : 1;
                Player2.Place = Winner == 1 ? 2 : 1;
                return Winner == 2 ? new List<PlayerMatchInfo> { Player2, Player1 } : new List<PlayerMatchInfo> { Player1, Player2 };
            }
        }

        /// <summary>Did this player take part (by id, or by name for old entries without ids)?</summary>
        public bool Involves(string? playerId, string? username)
        {
            foreach (var p in Participants)
            {
                if (!string.IsNullOrWhiteSpace(playerId) && p.PlayerId == playerId) return true;
                if (!string.IsNullOrWhiteSpace(username) && string.Equals(p.Username, username, StringComparison.OrdinalIgnoreCase)) return true;
            }
            return false;
        }

        /// <summary>
        /// Is this match from today?
        /// </summary>
        public bool IsToday
        {
            get
            {
                var matchDate = DateTimeOffset.FromUnixTimeSeconds(Timestamp).Date;
                return matchDate == DateTime.Today;
            }
        }
        
        /// <summary>
        /// Get formatted time ago (e.g., "2 hours ago", "5 days ago")
        /// </summary>
        public string TimeAgo
        {
            get
            {
                var matchTime = DateTimeOffset.FromUnixTimeSeconds(Timestamp);
                var elapsed = DateTimeOffset.UtcNow - matchTime;
                
                if (elapsed.TotalMinutes < 1)
                    return "Just now";
                if (elapsed.TotalMinutes < 60)
                    return $"{(int)elapsed.TotalMinutes} min ago";
                if (elapsed.TotalHours < 24)
                    return $"{(int)elapsed.TotalHours}h ago";
                if (elapsed.TotalDays < 7)
                    return $"{(int)elapsed.TotalDays}d ago";
                if (elapsed.TotalDays < 30)
                    return $"{(int)(elapsed.TotalDays / 7)}w ago";
                
                return matchTime.ToString("MMM dd, yyyy");
            }
        }
        
        public MatchHistoryEntry()
        {
            Timestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        }
    }
    
    /// <summary>
    /// Player information within a match.
    /// </summary>
    public class PlayerMatchInfo
    {
        public string PlayerId { get; set; } = "";
        public string Username { get; set; } = "Unknown";
        public int AvatarIndex { get; set; } = 0;
        public int ColorHue { get; set; } = 180;
        /// <summary>Avatar URL (Discord CDN), empty = none (protocol 5).</summary>
        public string Avatar { get; set; } = "";
        
        /// <summary>
        /// Final score
        /// </summary>
        public long Score { get; set; } = 0;
        
        /// <summary>
        /// Accuracy percentage (0-100)
        /// </summary>
        public float Accuracy { get; set; } = 0f;
        
        /// <summary>
        /// Max combo achieved
        /// </summary>
        public int MaxCombo { get; set; } = 0;
        
        /// <summary>
        /// Number of misses
        /// </summary>
        public int Misses { get; set; } = 0;
        
        /// <summary>
        /// Did this player disconnect/quit?
        /// </summary>
        public bool Disconnected { get; set; } = false;

        /// <summary>1-based place (0 = unknown).</summary>
        public int Place { get; set; } = 0;

        /// <summary>Teams mode: 0 = Player, 1 = Enemy; -1 otherwise.</summary>
        public int Team { get; set; } = -1;

        /// <summary>Side played: 0 = Player, 1 = Enemy.</summary>
        public int Side { get; set; } = 0;

        /// <summary>ELO gained / lost in this match.</summary>
        public int EloChange { get; set; } = 0;
    }
}
