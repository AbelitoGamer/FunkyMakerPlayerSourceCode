using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;

namespace FunkyMakerPlayer.Multiplayer.Transfer
{
    /// <summary>
    /// One local level file with its SHA-256. For osu! (fork) it is one 4K difficulty: FullPath and
    /// Hash are its .osu file, Size / SizeNoVideo the whole set's, and what gets sent is the set packed.
    /// </summary>
    public sealed class LocalLevel
    {
        /// <summary>Path relative to the levels folder (osu!: to mods/osu).</summary>
        public string RelativePath { get; init; } = "";
        public string FullPath { get; init; } = "";
        public long Size { get; init; }
        public string Hash { get; init; } = "";

        /// <summary>fnf / osu (<see cref="Rooms.Protocol"/> kinds).</summary>
        public string Kind { get; init; } = Rooms.Protocol.KindFnf;
        public bool IsOsu => Kind == Rooms.Protocol.KindOsu;
        /// <summary>osu!: the set folder, its id, "Artist - Title", the difficulty name, size without video (0 = none).</summary>
        public string SetFolder { get; init; } = "";
        public string SetId { get; init; } = "";
        public string SetName { get; init; } = "";
        public string Difficulty { get; init; } = "";
        public long SizeNoVideo { get; init; }

        /// <summary>The file other players receive (osu!: the set packed as .osz).</summary>
        public string FileName => IsOsu ? Path.GetFileName(SetFolder) + ".osz" : Path.GetFileName(RelativePath);
        public string Name => IsOsu ? $"{SetName} [{Difficulty}]" : Path.GetFileNameWithoutExtension(RelativePath);

        /// <summary>
        /// Downloaded from an online room rather than imported by you: FNF levels land in
        /// levels/Received/, osu! sets are listed by <see cref="Osu.OsuLibrary.MarkReceived"/>.
        /// </summary>
        public bool Received => IsOsu
            ? Osu.OsuLibrary.IsReceived(SetFolder)
            : RelativePath.Replace('\\', '/').StartsWith("Received/", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Hashes local level files for private-room level sharing. Hashes are cached on disk
    /// (<c>level_hash_cache.json</c> in the multiplayer storage folder) keyed by full path, size and
    /// last-write time, so entering a room only re-hashes files that changed.
    /// Thread-safe; <see cref="Scan"/> is meant to run on a background thread.
    /// </summary>
    public sealed class LocalLevelIndex
    {
        public static LocalLevelIndex Instance { get; } = new();

        private sealed class CacheEntry
        {
            public long Size { get; set; }
            public long LastWriteTicks { get; set; }
            public string Hash { get; set; } = "";
        }

        private readonly object _lock = new();
        private Dictionary<string, CacheEntry>? _cache;
        private bool _dirty;

        private static string CachePath => Path.Combine(MultiplayerSettings.GetStorageDirectory(), "level_hash_cache.json");

        /// <summary>Hashes every file (using the cache when possible). <paramref name="progress"/> gets (done, total).</summary>
        public List<LocalLevel> Scan(string levelsFolder, IReadOnlyList<string> relativePaths, Action<int, int>? progress = null)
        {
            var result = new List<LocalLevel>(relativePaths.Count);
            for (int i = 0; i < relativePaths.Count; i++)
            {
                string rel = relativePaths[i];
                string full = Path.GetFullPath(Path.Combine(levelsFolder, rel));
                var level = Get(full, rel);
                if (level != null) result.Add(level);
                progress?.Invoke(i + 1, relativePaths.Count);
            }
            Save();
            return result;
        }

        /// <summary>Hash of one file (cached), or null if it can't be read.</summary>
        public LocalLevel? Get(string fullPath, string relativePath)
        {
            FileInfo info;
            try
            {
                info = new FileInfo(fullPath);
                if (!info.Exists) return null;
            }
            catch
            {
                return null;
            }

            long ticks = info.LastWriteTimeUtc.Ticks;
            lock (_lock)
            {
                var cache = LoadLocked();
                if (cache.TryGetValue(fullPath, out var hit) && hit.Size == info.Length && hit.LastWriteTicks == ticks && hit.Hash.Length == 64)
                    return new LocalLevel { RelativePath = relativePath, FullPath = fullPath, Size = info.Length, Hash = hit.Hash };
            }

            string hash = LevelHasher.CalculateHash(fullPath);
            if (string.IsNullOrEmpty(hash)) return null;

            lock (_lock)
            {
                LoadLocked()[fullPath] = new CacheEntry { Size = info.Length, LastWriteTicks = ticks, Hash = hash };
                _dirty = true;
            }
            return new LocalLevel { RelativePath = relativePath, FullPath = fullPath, Size = info.Length, Hash = hash };
        }

        /// <summary>Records a hash that was computed elsewhere (e.g. while downloading).</summary>
        public void Remember(string fullPath, string hash)
        {
            try
            {
                var info = new FileInfo(fullPath);
                if (!info.Exists) return;
                lock (_lock)
                {
                    LoadLocked()[fullPath] = new CacheEntry { Size = info.Length, LastWriteTicks = info.LastWriteTimeUtc.Ticks, Hash = hash };
                    _dirty = true;
                }
                Save();
            }
            catch { }
        }

        private Dictionary<string, CacheEntry> LoadLocked()
        {
            if (_cache != null) return _cache;
            _cache = new Dictionary<string, CacheEntry>(StringComparer.Ordinal);
            try
            {
                if (File.Exists(CachePath))
                {
                    var loaded = JsonSerializer.Deserialize<Dictionary<string, CacheEntry>>(File.ReadAllText(CachePath));
                    if (loaded != null)
                        foreach (var kv in loaded) _cache[kv.Key] = kv.Value;
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[LevelIndex] Ignoring unreadable hash cache: {ex.Message}");
            }
            return _cache;
        }

        private void Save()
        {
            string json;
            lock (_lock)
            {
                if (!_dirty || _cache == null) return;
                // Drop entries for files that no longer exist so the cache doesn't grow forever.
                var keep = new Dictionary<string, CacheEntry>(StringComparer.Ordinal);
                foreach (var kv in _cache)
                    if (File.Exists(kv.Key)) keep[kv.Key] = kv.Value;
                _cache = keep;
                json = JsonSerializer.Serialize(keep);
                _dirty = false;
            }
            try
            {
                string tmp = CachePath + ".tmp";
                File.WriteAllText(tmp, json);
                File.Move(tmp, CachePath, overwrite: true);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[LevelIndex] Could not save hash cache: {ex.Message}");
            }
        }
    }
}
