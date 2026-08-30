using System;
using System.Collections.Generic;
using System.IO;

namespace FunkyMakerPlayer.Multiplayer
{
    /// <summary>
    /// In-memory cache for official levels.
    /// Levels are downloaded to RAM and not saved to disk.
    /// This prevents users from keeping official levels permanently.
    /// </summary>
    public class LevelCache
    {
        private static LevelCache? _instance;
        public static LevelCache Instance => _instance ??= new LevelCache();

        // In-memory storage for level data
        private readonly Dictionary<string, byte[]> _cachedLevels = new();
        private readonly Dictionary<string, OfficialLevelInfo> _levelInfo = new();

        /// <summary>
        /// Store a downloaded level in memory.
        /// </summary>
        public void CacheLevel(string levelId, byte[] levelData, OfficialLevelInfo info)
        {
            _cachedLevels[levelId] = levelData;
            _levelInfo[levelId] = info;
            Console.WriteLine($"[LevelCache] Cached level: {info.Name} ({levelData.Length} bytes)");
        }

        /// <summary>
        /// Check if a level is cached in memory.
        /// </summary>
        public bool IsCached(string levelId)
        {
            return _cachedLevels.ContainsKey(levelId);
        }

        /// <summary>
        /// Get cached level data.
        /// </summary>
        public byte[]? GetLevelData(string levelId)
        {
            return _cachedLevels.TryGetValue(levelId, out var data) ? data : null;
        }

        /// <summary>
        /// Get cached level info.
        /// </summary>
        public OfficialLevelInfo? GetLevelInfo(string levelId)
        {
            return _levelInfo.TryGetValue(levelId, out var info) ? info : null;
        }

        /// <summary>
        /// Write cached level to a temporary file for gameplay.
        /// File will be deleted after gameplay.
        /// Uses process ID to avoid conflicts when multiple clients run simultaneously.
        /// </summary>
        public string? WriteTempFile(string levelId)
        {
            if (!_cachedLevels.TryGetValue(levelId, out var data))
                return null;

            try
            {
                // Create temp file with process ID to avoid conflicts in multiplayer testing
                int processId = System.Diagnostics.Process.GetCurrentProcess().Id;
                string tempPath = Path.Combine(Path.GetTempPath(), $"funkymaker_temp_{levelId}_{processId}.FNMM");
                File.WriteAllBytes(tempPath, data);
                
                Console.WriteLine($"[LevelCache] Created temp file: {tempPath}");
                return tempPath;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[LevelCache] Error creating temp file: {ex.Message}");
                return null;
            }
        }

        /// <summary>
        /// Clean up a temporary file.
        /// </summary>
        public void DeleteTempFile(string tempPath)
        {
            try
            {
                if (File.Exists(tempPath))
                {
                    File.Delete(tempPath);
                    Console.WriteLine($"[LevelCache] Deleted temp file: {tempPath}");
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[LevelCache] Error deleting temp file: {ex.Message}");
            }
        }

        /// <summary>
        /// Clear all cached levels from memory.
        /// </summary>
        public void ClearCache()
        {
            int count = _cachedLevels.Count;
            _cachedLevels.Clear();
            _levelInfo.Clear();
            Console.WriteLine($"[LevelCache] Cleared {count} cached levels");
        }

        /// <summary>
        /// Get list of all cached level IDs.
        /// </summary>
        public List<string> GetCachedLevelIds()
        {
            return new List<string>(_cachedLevels.Keys);
        }

        /// <summary>
        /// Get total size of cached levels in bytes.
        /// </summary>
        public long GetCacheSize()
        {
            long total = 0;
            foreach (var data in _cachedLevels.Values)
            {
                total += data.Length;
            }
            return total;
        }
    }
}
