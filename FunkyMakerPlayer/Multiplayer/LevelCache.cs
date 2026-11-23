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
        /// Store a downloaded level and persist a hash-verified local cache copy.
        /// Once persisted to disk (`official_cache/*.fmc`), the raw byte[] is not kept in RAM so
        /// 32-bit Android devices do not waste hundreds of MB of virtual address space.
        /// </summary>
        public void CacheLevel(string levelId, byte[] levelData, OfficialLevelInfo info)
        {
            string actualHash = LevelHasher.CalculateHash(levelData);

            bool hashValid = !string.IsNullOrEmpty(actualHash) &&
                             (string.IsNullOrWhiteSpace(info.Hash) ||
                              actualHash.Equals(info.Hash, StringComparison.OrdinalIgnoreCase));

            bool savedToDisk = hashValid && SaveToDiskCache(actualHash, levelData);

            lock (_sync)
            {
                if (savedToDisk)
                    _cachedLevels.Remove(levelId);
                else
                    _cachedLevels[levelId] = levelData;

                _levelInfo[levelId] = info;
                if (!string.IsNullOrEmpty(actualHash))
                    _computedHashes[levelId] = actualHash;
            }

            Console.WriteLine($"[LevelCache] Cached level: {info.Name} ({levelData.Length} bytes, disk={savedToDisk})");
        }

        private static bool SaveToDiskCache(string hash, byte[] levelData)
        {
            try
            {
                string? cachePath = GetDiskCachePath(hash);
                if (cachePath == null)
                    return false;

                if (File.Exists(cachePath) && new FileInfo(cachePath).Length == levelData.Length)
                    return true;

                string tempPath = cachePath + ".tmp";
                File.WriteAllBytes(tempPath, levelData);
                File.Move(tempPath, cachePath, overwrite: true);
                return true;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[LevelCache] Warning saving disk cache: {ex.Message}");
                return false;
            }
        }

        /// <summary>
        /// Verify a level exists in the disk cache with matching SHA-256 metadata.
        /// Uses streaming SHA-256 verification and keeps only metadata/hashes in RAM.
        /// </summary>
        public bool TryRestoreFromDisk(OfficialLevelInfo info)
        {
            if (info == null || string.IsNullOrWhiteSpace(info.Id))
                return false;

            lock (_sync)
            {
                if (_computedHashes.TryGetValue(info.Id, out string? existingHash))
                {
                    bool hashMatches = string.IsNullOrWhiteSpace(info.Hash) ||
                                       existingHash.Equals(info.Hash, StringComparison.OrdinalIgnoreCase);
                    string? existingDisk = GetDiskCachePath(existingHash);
                    if (hashMatches && (_cachedLevels.ContainsKey(info.Id) || (existingDisk != null && File.Exists(existingDisk))))
                    {
                        _levelInfo[info.Id] = info;
                        return true;
                    }

                    // Server version changed or disk file missing; evict stale metadata
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

                string actualHash = LevelHasher.CalculateHash(cachePath);
                if (string.IsNullOrEmpty(actualHash) || !actualHash.Equals(info.Hash, StringComparison.OrdinalIgnoreCase))
                {
                    File.Delete(cachePath);
                    return false;
                }

                lock (_sync)
                {
                    _cachedLevels.Remove(info.Id);
                    _levelInfo[info.Id] = info;
                    _computedHashes[info.Id] = actualHash;
                }

                Console.WriteLine($"[LevelCache] Verified disk cache: {info.Name} ({fi.Length} bytes)");
                return true;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[LevelCache] Warning reading disk cache: {ex.Message}");
                return false;
            }
        }

        /// <summary>
        /// Hydrate cache metadata from disk for any matching server-listed official levels.
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
        /// Check if a level is cached (in verified disk cache or RAM fallback).
        /// </summary>
        public bool IsCached(string levelId)
        {
            lock (_sync)
            {
                if (_cachedLevels.ContainsKey(levelId))
                    return true;
                if (_computedHashes.TryGetValue(levelId, out string? hash))
                {
                    string? path = GetDiskCachePath(hash);
                    return path != null && File.Exists(path);
                }
                return false;
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
        /// Get cached level data (reads from disk cache on demand if not in RAM).
        /// </summary>
        public byte[]? GetLevelData(string levelId)
        {
            string? diskPath = null;
            lock (_sync)
            {
                if (_cachedLevels.TryGetValue(levelId, out var data))
                    return data;
                if (_computedHashes.TryGetValue(levelId, out string? hash))
                    diskPath = GetDiskCachePath(hash);
            }

            if (diskPath != null && File.Exists(diskPath))
            {
                try { return File.ReadAllBytes(diskPath); }
                catch { return null; }
            }
            return null;
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
        /// Reuses existing temp file if its verified hash already matches, and copies directly
        /// from disk cache without allocating a full-file byte[] in RAM.
        /// </summary>
        public string? WriteTempFile(string levelId)
        {
            byte[]? data;
            string expectedHash;
            lock (_sync)
            {
                _cachedLevels.TryGetValue(levelId, out data);
                expectedHash = _computedHashes.TryGetValue(levelId, out string? h) ? h : "";
            }

            if (string.IsNullOrEmpty(expectedHash))
                expectedHash = GetCachedHash(levelId);

            string? diskPath = GetDiskCachePath(expectedHash);
            bool hasDiskFile = diskPath != null && File.Exists(diskPath);
            if (data == null && !hasDiskFile)
                return null;

            try
            {
                int processId = System.Diagnostics.Process.GetCurrentProcess().Id;
                string tempPath = Path.Combine(Path.GetTempPath(), $"funkymaker_temp_{levelId}_{processId}.FNMM");
                long expectedLen = data != null ? data.Length : (hasDiskFile ? new FileInfo(diskPath!).Length : -1);

                lock (_sync)
                {
                    if (!string.IsNullOrEmpty(expectedHash) &&
                        _tempFileHashes.TryGetValue(tempPath, out string? writtenHash) &&
                        writtenHash == expectedHash &&
                        File.Exists(tempPath) &&
                        (expectedLen < 0 || new FileInfo(tempPath).Length == expectedLen))
                    {
                        return tempPath;
                    }
                }

                if (data != null)
                    File.WriteAllBytes(tempPath, data);
                else
                    File.Copy(diskPath!, tempPath, overwrite: true);

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
                string actualHash = LevelHasher.CalculateHash(cachePath);
                if (!string.Equals(actualHash, hash, StringComparison.OrdinalIgnoreCase)) return null;
                long expectedLen = new FileInfo(cachePath).Length;
                string tempPath = Path.Combine(Path.GetTempPath(), $"funkymaker_temp_replay{hash[..8]}_{System.Diagnostics.Process.GetCurrentProcess().Id}.FNMM");
                if (!File.Exists(tempPath) || new FileInfo(tempPath).Length != expectedLen)
                    File.Copy(cachePath, tempPath, overwrite: true);
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
        /// Get cached level size in bytes without reading disk into RAM.
        /// </summary>
        public long GetCachedFileSize(string levelId)
        {
            string? diskPath = null;
            long fallbackSize = 0;
            lock (_sync)
            {
                if (_cachedLevels.TryGetValue(levelId, out var data))
                    return data.Length;
                if (_levelInfo.TryGetValue(levelId, out var info) && info.FileSize > 0)
                    fallbackSize = info.FileSize;
                if (_computedHashes.TryGetValue(levelId, out string? hash))
                    diskPath = GetDiskCachePath(hash);
            }
            if (diskPath != null && File.Exists(diskPath))
            {
                try { return new FileInfo(diskPath).Length; }
                catch { }
            }
            return fallbackSize;
        }

        /// <summary>
        /// Get list of all cached level IDs.
        /// </summary>
        public List<string> GetCachedLevelIds()
        {
            lock (_sync)
            {
                var ids = new HashSet<string>(_cachedLevels.Keys, StringComparer.OrdinalIgnoreCase);
                foreach (var id in _computedHashes.Keys)
                    ids.Add(id);
                return new List<string>(ids);
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
