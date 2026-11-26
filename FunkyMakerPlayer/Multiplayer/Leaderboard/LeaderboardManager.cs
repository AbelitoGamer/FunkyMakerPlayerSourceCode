using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;

namespace FunkyMakerPlayer.Multiplayer
{
    /// <summary>
    /// Manages leaderboard data, match history, and player statistics.
    /// Uses local JSON files for storage with optional server sync.
    /// </summary>
    public class LeaderboardManager
    {
        private static LeaderboardManager? _instance;
        public static LeaderboardManager Instance
        {
            get
            {
                if (_instance == null)
                {
                    _instance = new LeaderboardManager();
                    _instance.Load();
                }
                return _instance;
            }
        }
        
        private const string LEADERBOARD_FILE = "leaderboard_data.json";
        private const string MATCH_HISTORY_FILE = "match_history.json";
        
        // ══════════════════════════════════════════════════════════════════════
        // Data Storage
        // ══════════════════════════════════════════════════════════════════════
        
        private List<LeaderboardEntry> _leaderboard = new();
        private List<MatchHistoryEntry> _matchHistory = new();
        
        // ══════════════════════════════════════════════════════════════════════
        // Public Properties
        // ══════════════════════════════════════════════════════════════════════
        
        /// <summary>
        /// All leaderboard entries sorted by rank
        /// </summary>
        public IReadOnlyList<LeaderboardEntry> Leaderboard => _leaderboard.AsReadOnly();
        
        /// <summary>
        /// All match history entries
        /// </summary>
        public IReadOnlyList<MatchHistoryEntry> MatchHistory => _matchHistory.AsReadOnly();
        
        /// <summary>
        /// Get top N players
        /// </summary>
        public List<LeaderboardEntry> GetTopPlayers(int count = 10)
        {
            return _leaderboard
                .OrderByDescending(e => e.TotalScore)
                .Take(count)
                .ToList();
        }
        
        /// <summary>
        /// Get recent matches (last N)
        /// </summary>
        public List<MatchHistoryEntry> GetRecentMatches(int count = 10)
        {
            return _matchHistory
                .OrderByDescending(m => m.Timestamp)
                .Take(count)
                .ToList();
        }
        
        /// <summary>
        /// Get matches from today
        /// </summary>
        public List<MatchHistoryEntry> GetTodayMatches()
        {
            return _matchHistory
                .Where(m => m.IsToday)
                .OrderByDescending(m => m.Timestamp)
                .ToList();
        }
        
        /// <summary>
        /// Get player stats by ID (or username fallback)
        /// </summary>
        public LeaderboardEntry? GetPlayerStats(string playerId, string? username = null)
        {
            return _leaderboard.FirstOrDefault(e =>
                (!string.IsNullOrWhiteSpace(playerId) && e.PlayerId == playerId) ||
                (!string.IsNullOrWhiteSpace(username) &&
                 string.Equals(e.Username, username, StringComparison.OrdinalIgnoreCase)));
        }
        
        /// <summary>
        /// Get match history for a specific player
        /// </summary>
        public List<MatchHistoryEntry> GetPlayerMatchHistory(string playerId, int count = 10, string? username = null)
        {
            return _matchHistory
                .Where(m => m.Involves(playerId, username))
                .OrderByDescending(m => m.Timestamp)
                .Take(count)
                .ToList();
        }
        
        // ══════════════════════════════════════════════════════════════════════
        // Data Modification
        // ══════════════════════════════════════════════════════════════════════
        
        /// <summary>
        /// Add or update a player's profile in the leaderboard.
        /// </summary>
        public void UpdatePlayerProfile(string playerId, string username, int avatarIndex, int colorHue)
        {
            var entry = _leaderboard.FirstOrDefault(e => e.PlayerId == playerId);
            
            if (entry == null)
            {
                entry = new LeaderboardEntry
                {
                    PlayerId = playerId,
                    Username = username,
                    AvatarIndex = avatarIndex,
                    ColorHue = colorHue
                };
                _leaderboard.Add(entry);
            }
            else
            {
                entry.Username = username;
                entry.AvatarIndex = avatarIndex;
                entry.ColorHue = colorHue;
                entry.LastPlayedTimestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            }
            
            Save();
        }
        
        /// <summary>
        /// Record a completed match and update player statistics.
        /// </summary>
        public void RecordMatch(MatchHistoryEntry match)
        {
            if (!string.IsNullOrEmpty(match.MatchId) && _matchHistory.Any(m => m.MatchId == match.MatchId))
                return;

            // Add to history
            _matchHistory.Add(match);
            
            // Update player 1 stats
            UpdatePlayerStatsFromMatch(match.Player1, match.Winner == 1, match.Winner == 0);
            
            // Update player 2 stats
            UpdatePlayerStatsFromMatch(match.Player2, match.Winner == 2, match.Winner == 0);
            
            // Update rankings
            UpdateRankings();
            
            Save();
        }
        
        private void UpdatePlayerStatsFromMatch(PlayerMatchInfo playerInfo, bool won, bool draw)
        {
            var entry = _leaderboard.FirstOrDefault(e =>
                (!string.IsNullOrWhiteSpace(playerInfo.PlayerId) && e.PlayerId == playerInfo.PlayerId) ||
                (!string.IsNullOrWhiteSpace(playerInfo.Username) &&
                 string.Equals(e.Username, playerInfo.Username, StringComparison.OrdinalIgnoreCase)));
            
            if (entry == null)
            {
                entry = new LeaderboardEntry
                {
                    PlayerId = string.IsNullOrWhiteSpace(playerInfo.PlayerId) ? playerInfo.Username : playerInfo.PlayerId,
                    Username = playerInfo.Username,
                    AvatarIndex = playerInfo.AvatarIndex,
                    ColorHue = playerInfo.ColorHue
                };
                _leaderboard.Add(entry);
            }
            else
            {
                if (!string.IsNullOrWhiteSpace(playerInfo.PlayerId))
                    entry.PlayerId = playerInfo.PlayerId;
                if (!string.IsNullOrWhiteSpace(playerInfo.Username))
                    entry.Username = playerInfo.Username;
                entry.ColorHue = playerInfo.ColorHue;
            }
            
            // Update match count
            if (draw)
                entry.Draws++;
            else if (won)
                entry.Wins++;
            else
                entry.Losses++;
            
            // Update statistics
            entry.TotalScore += playerInfo.Score;
            entry.LastPlayedTimestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            
            if (playerInfo.Score > entry.HighestScore)
                entry.HighestScore = playerInfo.Score;
            
            if (playerInfo.MaxCombo > entry.HighestCombo)
                entry.HighestCombo = playerInfo.MaxCombo;
            
            if (playerInfo.Accuracy > entry.BestAccuracy)
                entry.BestAccuracy = playerInfo.Accuracy;
            
            // Simple ELO calculation
            if (won)
                entry.EloRating += 25;
            else if (!draw)
                entry.EloRating = Math.Max(0, entry.EloRating - 15);
        }

        /// <summary>
        /// Merge global leaderboard entries received from server without losing offline records.
        /// </summary>
        public void ApplyServerLeaderboard(IEnumerable<LeaderboardEntryPacket> serverEntries)
        {
            bool changed = false;
            foreach (var pkt in serverEntries)
            {
                var existing = _leaderboard.FirstOrDefault(e =>
                    (!string.IsNullOrWhiteSpace(pkt.PlayerId) && e.PlayerId == pkt.PlayerId) ||
                    (!string.IsNullOrWhiteSpace(pkt.Username) &&
                     string.Equals(e.Username, pkt.Username, StringComparison.OrdinalIgnoreCase)));

                int serverMatches = pkt.Wins + pkt.Losses + pkt.Draws;
                if (existing == null)
                {
                    _leaderboard.Add(new LeaderboardEntry
                    {
                        PlayerId = pkt.PlayerId,
                        Username = pkt.Username,
                        AvatarIndex = pkt.AvatarIndex,
                        ColorHue = pkt.ColorHue,
                        TotalScore = pkt.TotalScore,
                        Wins = pkt.Wins,
                        Losses = pkt.Losses,
                        Draws = pkt.Draws,
                        HighestScore = pkt.HighestScore,
                        HighestCombo = pkt.HighestCombo,
                        BestAccuracy = pkt.BestAccuracy,
                        EloRating = pkt.EloRating,
                        LastPlayedTimestamp = pkt.LastPlayedTimestamp,
                        CreatedTimestamp = pkt.CreatedTimestamp
                    });
                    changed = true;
                }
                else if (serverMatches >= existing.TotalMatches)
                {
                    if (!string.IsNullOrWhiteSpace(pkt.PlayerId))
                        existing.PlayerId = pkt.PlayerId;
                    if (!string.IsNullOrWhiteSpace(pkt.Username))
                        existing.Username = pkt.Username;
                    existing.ColorHue = pkt.ColorHue;
                    existing.TotalScore = Math.Max(existing.TotalScore, pkt.TotalScore);
                    existing.Wins = pkt.Wins;
                    existing.Losses = pkt.Losses;
                    existing.Draws = pkt.Draws;
                    existing.HighestScore = Math.Max(existing.HighestScore, pkt.HighestScore);
                    existing.HighestCombo = Math.Max(existing.HighestCombo, pkt.HighestCombo);
                    existing.BestAccuracy = Math.Max(existing.BestAccuracy, pkt.BestAccuracy);
                    existing.EloRating = pkt.EloRating;
                    existing.LastPlayedTimestamp = Math.Max(existing.LastPlayedTimestamp, pkt.LastPlayedTimestamp);
                    changed = true;
                }
            }

            if (changed)
            {
                UpdateRankings();
                Save();
            }
        }

        /// <summary>
        /// Merge match history received from server deduplicating by MatchId.
        /// </summary>
        public void ApplyServerMatchHistory(IEnumerable<MatchHistoryPacket> serverMatches)
        {
            bool changed = false;
            foreach (var pkt in serverMatches)
            {
                if (pkt == null) continue;
                var players = (pkt.Players ?? new List<PlayerMatchData>()).Select(ToInfo).ToList();
                var existing = string.IsNullOrEmpty(pkt.MatchId) ? null : _matchHistory.FirstOrDefault(m => m.MatchId == pkt.MatchId);
                if (existing != null)
                {
                    // Older builds cached room matches as plain 1v1 duels: fill in the full participant list.
                    if (existing.Players.Count == 0 && players.Count > 0)
                    {
                        existing.Players = players;
                        existing.Mode = pkt.Mode ?? "";
                        existing.WinningTeam = pkt.WinningTeam;
                        changed = true;
                    }
                    continue;
                }

                _matchHistory.Add(new MatchHistoryEntry
                {
                    MatchId = pkt.MatchId,
                    LevelName = pkt.LevelName,
                    Winner = pkt.Winner,
                    DurationSeconds = pkt.DurationSeconds,
                    Timestamp = pkt.Timestamp,
                    Mode = pkt.Mode ?? "",
                    WinningTeam = pkt.WinningTeam,
                    Player1 = ToInfo(pkt.Player1),
                    Player2 = ToInfo(pkt.Player2),
                    Players = players,
                });
                changed = true;
            }

            if (changed)
                Save();
        }

        private static PlayerMatchInfo ToInfo(PlayerMatchData? p) => p == null ? new PlayerMatchInfo() : new PlayerMatchInfo
        {
            PlayerId = p.PlayerId,
            Username = p.Username,
            AvatarIndex = p.AvatarIndex,
            ColorHue = p.ColorHue,
            Score = p.Score,
            Accuracy = p.Accuracy,
            MaxCombo = p.MaxCombo,
            Misses = p.Misses,
            Disconnected = p.Disconnected,
            Place = p.Place,
            Team = p.Team,
            Side = p.Side,
            EloChange = p.EloChange,
        };
        
        /// <summary>
        /// Recalculate rankings based on total score.
        /// </summary>
        private void UpdateRankings()
        {
            var sorted = _leaderboard
                .OrderByDescending(e => e.EloRating)
                .ThenByDescending(e => e.TotalScore)
                .ToList();
            
            for (int i = 0; i < sorted.Count; i++)
            {
                sorted[i].Rank = i + 1;
            }
        }
        
        /// <summary>
        /// Clear all data (for testing or reset).
        /// </summary>
        public void ClearAll()
        {
            _leaderboard.Clear();
            _matchHistory.Clear();
            Save();
        }
        
        /// <summary>
        /// Clear old match history (keep last N days).
        /// </summary>
        public void ClearOldMatches(int keepDays = 30)
        {
            long cutoffTimestamp = DateTimeOffset.UtcNow.AddDays(-keepDays).ToUnixTimeSeconds();
            _matchHistory.RemoveAll(m => m.Timestamp < cutoffTimestamp);
            Save();
        }
        
        // ══════════════════════════════════════════════════════════════════════
        // Save / Load
        // ══════════════════════════════════════════════════════════════════════
        
        private static string GetLeaderboardPath() =>
            Path.Combine(MultiplayerSettings.GetStorageDirectory(), LEADERBOARD_FILE);

        private static string GetMatchHistoryPath() =>
            Path.Combine(MultiplayerSettings.GetStorageDirectory(), MATCH_HISTORY_FILE);

        /// <summary>
        /// Save all data to JSON files.
        /// </summary>
        public void Save()
        {
            try
            {
                var options = new JsonSerializerOptions { WriteIndented = true };
                
                // Save leaderboard
                string leaderboardPath = GetLeaderboardPath();
                string leaderboardJson = JsonSerializer.Serialize(_leaderboard, options);
                File.WriteAllText(leaderboardPath, leaderboardJson);
                
                // Save match history
                string historyPath = GetMatchHistoryPath();
                string historyJson = JsonSerializer.Serialize(_matchHistory, options);
                File.WriteAllText(historyPath, historyJson);
                
                Console.WriteLine("[Leaderboard] Data saved successfully");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[Leaderboard] Failed to save: {ex.Message}");
            }
        }
        
        /// <summary>
        /// Load data from JSON files.
        /// </summary>
        public void Load()
        {
            try
            {
                // Load leaderboard (with legacy BaseDirectory migration fallback)
                string leaderboardPath = GetLeaderboardPath();
                string legacyLeaderboardPath = Path.Combine(AppContext.BaseDirectory, LEADERBOARD_FILE);
                string? lbSource = File.Exists(leaderboardPath) ? leaderboardPath
                    : (File.Exists(legacyLeaderboardPath) ? legacyLeaderboardPath : null);
                if (lbSource != null)
                {
                    string json = File.ReadAllText(lbSource);
                    var loaded = JsonSerializer.Deserialize<List<LeaderboardEntry>>(json);
                    if (loaded != null)
                    {
                        _leaderboard = loaded;
                        Console.WriteLine($"[Leaderboard] Loaded {_leaderboard.Count} entries from {lbSource}");
                    }
                }
                
                // Load match history (with legacy BaseDirectory migration fallback)
                string historyPath = GetMatchHistoryPath();
                string legacyHistoryPath = Path.Combine(AppContext.BaseDirectory, MATCH_HISTORY_FILE);
                string? histSource = File.Exists(historyPath) ? historyPath
                    : (File.Exists(legacyHistoryPath) ? legacyHistoryPath : null);
                if (histSource != null)
                {
                    string json = File.ReadAllText(histSource);
                    var loaded = JsonSerializer.Deserialize<List<MatchHistoryEntry>>(json);
                    if (loaded != null)
                    {
                        _matchHistory = loaded;
                        Console.WriteLine($"[Leaderboard] Loaded {_matchHistory.Count} matches from {histSource}");
                    }
                }
                
                // Update rankings after load
                UpdateRankings();

                if ((lbSource == legacyLeaderboardPath || histSource == legacyHistoryPath) &&
                    (_leaderboard.Count > 0 || _matchHistory.Count > 0))
                {
                    Save();
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[Leaderboard] Failed to load: {ex.Message}");
            }
        }
        
        // ══════════════════════════════════════════════════════════════════════
        // Statistics
        // ══════════════════════════════════════════════════════════════════════
        
        /// <summary>
        /// Get total number of registered players.
        /// </summary>
        public int TotalPlayers => _leaderboard.Count;
        
        /// <summary>
        /// Get total number of matches played.
        /// </summary>
        public int TotalMatches => _matchHistory.Count;
        
        /// <summary>
        /// Get number of matches played today.
        /// </summary>
        public int TodayMatches => _matchHistory.Count(m => m.IsToday);
        
        /// <summary>
        /// Get most played level.
        /// </summary>
        public string? MostPlayedLevel
        {
            get
            {
                return _matchHistory
                    .GroupBy(m => m.LevelName)
                    .OrderByDescending(g => g.Count())
                    .FirstOrDefault()
                    ?.Key;
            }
        }
    }
}
