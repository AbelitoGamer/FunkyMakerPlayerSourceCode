using System;
using System.Collections.Generic;
using System.IO;

namespace FunkyMakerPlayer.Multiplayer
{
    /// <summary>
    /// Thread-safe RAM + SHA-256 verified disk cache for official levels.
    /// Keeps verified official payloads in app storage outside mods/levels/ so
    /// repeat sessions do not have to re-download large charts from the server.
    /// </summary>
    public class LevelCache
    {
        private static LevelCache? _instance;
        public static LevelCache Instance => _instance ??= new LevelCache();

        private readonly object _sync = new();
        private readonly Dictionary<string, byte[]> _cachedLevels = new(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, OfficialLevelInfo> _levelInfo = new(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, string> _computedHashes = new(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, string> _tempFileHashes = new(StringComparer.OrdinalIgnoreCase);

        private static string GetDiskCacheDirectory()
        {
            string dir = Path.Combine(MultiplayerSettings.GetStorageDirectory(), "official_cache");
            if (!Directory.Exists(dir))
                Directory.CreateDirectory(dir);
            return dir;
        }

        private static string? GetDiskCachePath(string? expectedHash)
        {
            if (string.IsNullOrWhiteSpace(expectedHash))
                return null;

            string clean = expectedHash.Trim().ToLowerInvariant();
            foreach (char c in clean)
            {
                if (!((c >= '0' && c <= '9') || (c >= 'a' && c <= 'f')))
                    return null;
            }

            return Path.Combine(GetDiskCacheDirectory(), $"{clean}.fmc");
        }

        /// <summary>
        /// Store a downloaded level in memory and persist a hash-verified local cache copy.
        /// </summary>
        public void CacheLevel(string levelId, byte[] levelData, OfficialLevelInfo info)
        {
            string actualHash = LevelHasher.CalculateHash(levelData);

            lock (_sync)
            {
                _cachedLevels[levelId] = levelData;
                _levelInfo[levelId] = info;
                if (!string.IsNullOrEmpty(actualHash))
                    _computedHashes[levelId] = actualHash;
            }

            bool hashValid = !string.IsNullOrEmpty(actualHash) &&
                             (string.IsNullOrWhiteSpace(info.Hash) ||
                              actualHash.Equals(info.Hash, StringComparison.OrdinalIgnoreCase));

            if (hashValid)
            {
                SaveToDiskCache(actualHash, levelData);
            }

            Console.WriteLine($"[LevelCache] Cached level: {info.Name} ({levelData.Length} bytes)");
        }

        private static void SaveToDiskCache(string hash, byte[] levelData)
        {
            try
            {
                string? cachePath = GetDiskCachePath(hash);
                if (cachePath == null)
                    return;

                if (File.Exists(cachePath) && new FileInfo(cachePath).Length == levelData.Length)
                    return;

                string tempPath = cachePath + ".tmp";
                File.WriteAllBytes(tempPath, levelData);
                File.Move(tempPath, cachePath, overwrite: true);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[LevelCache] Warning saving disk cache: {ex.Message}");
            }
        }

        /// <summary>
        /// Restore a level into RAM from the verified disk cache if its SHA-256 matches the server metadata.
        /// </summary>
        public bool TryRestoreFromDisk(OfficialLevelInfo info)
        {
            if (info == null || string.IsNullOrWhiteSpace(info.Id))
                return false;

            lock (_sync)
            {
                if (_cachedLevels.ContainsKey(info.Id))
                {
                    if (string.IsNullOrWhiteSpace(info.Hash) ||
                        (_computedHashes.TryGetValue(info.Id, out string? existingHash) &&
                         existingHash.Equals(info.Hash, StringComparison.OrdinalIgnoreCase)))
                    {
                        return true;
                    }

                    // Server version changed; evict stale RAM copy
                    _cachedLevels.Remove(info.Id);
                    _levelInfo.Remove(info.Id);
                    _computedHashes.Remove(info.Id);
                }
            }

            string? cachePath = GetDiskCachePath(info.Hash);
            if (cachePath == null || !File.Exists(cachePath))
                return false;

            try
            {
                var fi = new FileInfo(cachePath);
                if (info.FileSize > 0 && fi.Length != info.FileSize)
                    return false;

                byte[] data = File.ReadAllBytes(cachePath);
                string actualHash = LevelHasher.CalculateHash(data);
                if (!actualHash.Equals(info.Hash, StringComparison.OrdinalIgnoreCase))
                {
                    File.Delete(cachePath);
                    return false;
                }

                lock (_sync)
                {
                    _cachedLevels[info.Id] = data;
                    _levelInfo[info.Id] = info;
                    _computedHashes[info.Id] = actualHash;
                }

                Console.WriteLine($"[LevelCache] Restored from disk cache: {info.Name} ({data.Length} bytes)");
                return true;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[LevelCache] Warning reading disk cache: {ex.Message}");
                return false;
            }
        }

        /// <summary>
        /// Hydrate RAM cache from disk for any matching server-listed official levels.
        /// </summary>
        public void WarmFromDisk(IEnumerable<OfficialLevelInfo>? levels)
        {
            if (levels == null)
                return;

            foreach (var level in levels)
            {
                TryRestoreFromDisk(level);
            }
        }

        /// <summary>
        /// Check if a level is cached in memory.
        /// </summary>
        public bool IsCached(string levelId)
        {
            lock (_sync)
            {
                return _cachedLevels.ContainsKey(levelId);
            }
        }

        /// <summary>
        /// Get cached SHA-256 hash for an official level without re-reading disk.
        /// </summary>
        public string GetCachedHash(string levelId)
        {
            byte[]? dataCopy = null;
            lock (_sync)
            {
                if (_computedHashes.TryGetValue(levelId, out string? cachedHash) && !string.IsNullOrEmpty(cachedHash))
                    return cachedHash;

                _cachedLevels.TryGetValue(levelId, out dataCopy);
            }

            if (dataCopy == null)
                return "";

            string computed = LevelHasher.CalculateHash(dataCopy);
            if (!string.IsNullOrEmpty(computed))
            {
                lock (_sync)
                {
                    _computedHashes[levelId] = computed;
                }
            }
            return computed;
        }

        /// <summary>
        /// Get cached level data.
        /// </summary>
        public byte[]? GetLevelData(string levelId)
        {
            lock (_sync)
            {
                return _cachedLevels.TryGetValue(levelId, out var data) ? data : null;
            }
        }

        /// <summary>
        /// Get cached level info.
        /// </summary>
        public OfficialLevelInfo? GetLevelInfo(string levelId)
        {
            lock (_sync)
            {
                return _levelInfo.TryGetValue(levelId, out var info) ? info : null;
            }
        }

        /// <summary>
        /// Write cached level to a temporary file for gameplay.
        /// Reuses existing temp file if its verified hash already matches.
        /// </summary>
        public string? WriteTempFile(string levelId)
        {
            byte[]? data;
            string expectedHash;
            lock (_sync)
            {
                if (!_cachedLevels.TryGetValue(levelId, out data))
                    return null;

                expectedHash = _computedHashes.TryGetValue(levelId, out string? h) ? h : "";
            }

            if (string.IsNullOrEmpty(expectedHash))
                expectedHash = GetCachedHash(levelId);

            try
            {
                int processId = System.Diagnostics.Process.GetCurrentProcess().Id;
                string tempPath = Path.Combine(Path.GetTempPath(), $"funkymaker_temp_{levelId}_{processId}.FNMM");

                lock (_sync)
                {
                    if (!string.IsNullOrEmpty(expectedHash) &&
                        _tempFileHashes.TryGetValue(tempPath, out string? writtenHash) &&
                        writtenHash == expectedHash &&
                        File.Exists(tempPath) &&
                        new FileInfo(tempPath).Length == data.Length)
                    {
                        return tempPath;
                    }
                }

                File.WriteAllBytes(tempPath, data);

                lock (_sync)
                {
                    _tempFileHashes[tempPath] = expectedHash;
                }

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
        /// Replays (fork): writes the cached official level with this SHA-256 (from the disk cache)
        /// to a temp .FNMM and returns its path, or null if it isn't cached / doesn't match.
        /// </summary>
        public string? WriteTempFileForHash(string hash)
        {
            try
            {
                string? cachePath = GetDiskCachePath(hash);
                if (cachePath == null || !File.Exists(cachePath)) return null;
                byte[] data = File.ReadAllBytes(cachePath);
                if (!string.Equals(LevelHasher.CalculateHash(data), hash, StringComparison.OrdinalIgnoreCase)) return null;
                string tempPath = Path.Combine(Path.GetTempPath(), $"funkymaker_temp_replay{hash[..8]}_{System.Diagnostics.Process.GetCurrentProcess().Id}.FNMM");
                if (!File.Exists(tempPath) || new FileInfo(tempPath).Length != data.Length)
                    File.WriteAllBytes(tempPath, data);
                return tempPath;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[LevelCache] Replay temp file failed: {ex.Message}");
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
                lock (_sync)
                {
                    _tempFileHashes.Remove(tempPath);
                }

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
        /// Clear in-memory cached levels (preserves verified disk cache across sessions).
        /// </summary>
        public void ClearCache()
        {
            int count;
            lock (_sync)
            {
                count = _cachedLevels.Count;
                _cachedLevels.Clear();
                _levelInfo.Clear();
                _computedHashes.Clear();
                _tempFileHashes.Clear();
            }
            Console.WriteLine($"[LevelCache] Cleared {count} cached levels from RAM");
        }

        /// <summary>
        /// Get list of all cached level IDs.
        /// </summary>
        public List<string> GetCachedLevelIds()
        {
            lock (_sync)
            {
                return new List<string>(_cachedLevels.Keys);
            }
        }

        /// <summary>
        /// Get total size of cached levels in bytes.
        /// </summary>
        public long GetCacheSize()
        {
            lock (_sync)
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
}
