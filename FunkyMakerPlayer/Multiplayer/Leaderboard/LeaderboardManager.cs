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
        /// Get player stats by ID
        /// </summary>
        public LeaderboardEntry? GetPlayerStats(string playerId)
        {
            return _leaderboard.FirstOrDefault(e => e.PlayerId == playerId);
        }
        
        /// <summary>
        /// Get match history for a specific player
        /// </summary>
        public List<MatchHistoryEntry> GetPlayerMatchHistory(string playerId, int count = 10)
        {
            return _matchHistory
                .Where(m => m.Player1.PlayerId == playerId || m.Player2.PlayerId == playerId)
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
            // Add to history
            _matchHistory.Add(match);
            
            // Update player 1 stats
            UpdatePlayerStatsFromMatch(match.Player1, match.Winner == 1);
            
            // Update player 2 stats
            UpdatePlayerStatsFromMatch(match.Player2, match.Winner == 2);
            
            // Update rankings
            UpdateRankings();
            
            Save();
        }
        
        private void UpdatePlayerStatsFromMatch(PlayerMatchInfo playerInfo, bool won)
        {
            var entry = _leaderboard.FirstOrDefault(e => e.PlayerId == playerInfo.PlayerId);
            
            if (entry == null)
            {
                entry = new LeaderboardEntry
                {
                    PlayerId = playerInfo.PlayerId,
                    Username = playerInfo.Username,
                    AvatarIndex = playerInfo.AvatarIndex,
                    ColorHue = playerInfo.ColorHue
                };
                _leaderboard.Add(entry);
            }
            
            // Update match count
            if (won)
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
            
            // Simple ELO calculation (can be refined)
            if (won)
                entry.EloRating += 25;
            else
                entry.EloRating = Math.Max(0, entry.EloRating - 15);
        }
        
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
        
        /// <summary>
        /// Save all data to JSON files.
        /// </summary>
        public void Save()
        {
            try
            {
                var options = new JsonSerializerOptions { WriteIndented = true };
                
                // Save leaderboard
                string leaderboardPath = Path.Combine(AppContext.BaseDirectory, LEADERBOARD_FILE);
                string leaderboardJson = JsonSerializer.Serialize(_leaderboard, options);
                File.WriteAllText(leaderboardPath, leaderboardJson);
                
                // Save match history
                string historyPath = Path.Combine(AppContext.BaseDirectory, MATCH_HISTORY_FILE);
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
                // Load leaderboard
                string leaderboardPath = Path.Combine(AppContext.BaseDirectory, LEADERBOARD_FILE);
                if (File.Exists(leaderboardPath))
                {
                    string json = File.ReadAllText(leaderboardPath);
                    var loaded = JsonSerializer.Deserialize<List<LeaderboardEntry>>(json);
                    if (loaded != null)
                    {
                        _leaderboard = loaded;
                        Console.WriteLine($"[Leaderboard] Loaded {_leaderboard.Count} entries");
                    }
                }
                
                // Load match history
                string historyPath = Path.Combine(AppContext.BaseDirectory, MATCH_HISTORY_FILE);
                if (File.Exists(historyPath))
                {
                    string json = File.ReadAllText(historyPath);
                    var loaded = JsonSerializer.Deserialize<List<MatchHistoryEntry>>(json);
                    if (loaded != null)
                    {
                        _matchHistory = loaded;
                        Console.WriteLine($"[Leaderboard] Loaded {_matchHistory.Count} matches");
                    }
                }
                
                // Update rankings after load
                UpdateRankings();
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
