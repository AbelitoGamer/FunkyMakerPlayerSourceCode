using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;

namespace FunkyMakerPlayer;

/// <summary>
/// One attempt at a level from Freeplay (fork): a finished run or a game over, with its stats, the
/// side played, the song rate and the replay saved with it (if any).
/// </summary>
public sealed class FreeplayAttempt
{
    public int Score { get; set; }
    public float Accuracy { get; set; }
    public int Misses { get; set; }
    public int MaxCombo { get; set; }
    public int Sicks { get; set; }
    public int Goods { get; set; }
    public int Bads { get; set; }
    public int Shits { get; set; }
    public DateTime DateUtc { get; set; }
    /// <summary>Played on the enemy side (enemy mode).</summary>
    public bool Enemy { get; set; }
    public float Rate { get; set; } = 1f;
    /// <summary>Ended in a game over (<see cref="Progress"/> = how far it got, 0..1).</summary>
    public bool Failed { get; set; }
    public float Progress { get; set; } = 1f;
    /// <summary>Replay file saved with this run (may have been deleted since), or null.</summary>
    public string? ReplayPath { get; set; }

    /// <summary>Counts for the personal best: finished, at 1.00x.</summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public bool Ranked => !Failed && Math.Abs(Rate - 1f) < 0.001f;

    [System.Text.Json.Serialization.JsonIgnore]
    public bool HasReplay => !string.IsNullOrEmpty(ReplayPath) && File.Exists(ReplayPath);

    /// <summary>FNF-style combo label: SFC (all Sicks), GFC (no Bad/Shit), FC, or "" with misses.</summary>
    public string ComboLabel
        => Misses > 0 ? "" : Goods + Bads + Shits == 0 ? "SFC" : Bads + Shits == 0 ? "GFC" : "FC";

    public string Rank => FreeplayData.RankFor(Accuracy);
}

/// <summary>Per-level Freeplay data.</summary>
public sealed class FreeplayLevelRecord
{
    public bool Favorite { get; set; }
    public int Plays { get; set; }
    public DateTime? LastPlayedUtc { get; set; }
    /// <summary>Every attempt, oldest first (capped at <see cref="FreeplayData.MaxAttemptsPerLevel"/>).</summary>
    public List<FreeplayAttempt> Attempts { get; set; } = new();

    // Format of the first fork version (best only); moved into Attempts on load.
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    public FreeplayAttempt? Player { get; set; }
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    public FreeplayAttempt? Enemy { get; set; }

    /// <summary>Highest ranked score of a side, or null.</summary>
    public FreeplayAttempt? Best(bool enemy)
    {
        FreeplayAttempt? best = null;
        foreach (var a in Attempts)
            if (a.Enemy == enemy && a.Ranked && (best == null || a.Score > best.Score)) best = a;
        return best;
    }

    public int AttemptCount(bool enemy)
    {
        int n = 0;
        foreach (var a in Attempts) if (a.Enemy == enemy) n++;
        return n;
    }
}

/// <summary>
/// Freeplay data saved in <c>freeplay_data.json</c> (fork): favourites, play counts, personal bests,
/// the last selected level, the list order / filter and the play modifiers (song rate, enemy mode;
/// autoplay is <see cref="NoteRenderer.Botplay"/>, saved in settings.json).
/// Levels are keyed by their path relative to the levels folder, so moving the mods folder keeps them.
/// </summary>
public sealed class FreeplayData
{
    public const float MinRate = 0.5f, MaxRate = 2.0f, RateStep = 0.05f;
    public const int MaxAttemptsPerLevel = 200;

    public Dictionary<string, FreeplayLevelRecord> Levels { get; set; } = new();
    public string? LastSelected { get; set; }
    public string Sort { get; set; } = "Name";
    public bool FavoritesOnly { get; set; }
    public float SongRate { get; set; } = 1f;
    public bool EnemyMode { get; set; }
    /// <summary>Rates the Slower / Faster modifiers use when switched on (see <see cref="PlayModifiers"/>).</summary>
    public float SlowRate { get; set; } = 0.75f;
    public float FastRate { get; set; } = 1.25f;
    /// <summary>Freeplay tab: "fnf" or "osu" (osu!mania songs).</summary>
    public string Tab { get; set; } = "fnf";
    /// <summary>osu! set folder name (lower-case) → chosen difficulty file name.</summary>
    public Dictionary<string, string> OsuDifficulty { get; set; } = new();

    /// <summary>Favourites of osu! songs belong to the set, not to one difficulty.</summary>
    private static string FavoritePath(string levelPath)
        => Osu.OsuLibrary.IsOsuChart(levelPath) ? (Path.GetDirectoryName(levelPath) ?? levelPath) : levelPath;

    private static FreeplayData? _instance;
    private static readonly object _lock = new();

    public static FreeplayData Instance
    {
        get
        {
            lock (_lock)
                return _instance ??= Load();
        }
    }

    private static string FilePath
        => Path.Combine(Multiplayer.MultiplayerSettings.GetStorageDirectory(), "freeplay_data.json");

    private static readonly JsonSerializerOptions _json = new() { WriteIndented = true };

    private static FreeplayData Load()
    {
        try
        {
            if (File.Exists(FilePath))
            {
                var data = JsonSerializer.Deserialize<FreeplayData>(File.ReadAllText(FilePath));
                if (data != null)
                {
                    // Keys are stored lower-case; rebuild in case the file was edited by hand.
                    var levels = new Dictionary<string, FreeplayLevelRecord>();
                    foreach (var (k, v) in data.Levels ?? new())
                    {
                        if (v == null) continue;
                        v.Attempts ??= new();
                        if (v.Player != null) { v.Attempts.Add(v.Player); v.Player = null; }
                        if (v.Enemy != null) { v.Enemy.Enemy = true; v.Attempts.Add(v.Enemy); v.Enemy = null; }
                        levels[k.ToLowerInvariant()] = v;
                    }
                    data.Levels = levels;
                    data.SongRate = ClampRate(data.SongRate);
                    data.SlowRate = Math.Clamp(ClampRate(data.SlowRate), MinRate, 0.95f);
                    data.FastRate = Math.Clamp(ClampRate(data.FastRate), 1.05f, MaxRate);
                    return data;
                }
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[FreeplayData] Could not read {FilePath}: {ex.Message}");
        }
        return new FreeplayData();
    }

    public void Save()
    {
        try
        {
            string json;
            lock (_lock) json = JsonSerializer.Serialize(this, _json);
            string tmp = FilePath + ".tmp";
            File.WriteAllText(tmp, json);
            File.Move(tmp, FilePath, overwrite: true);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[FreeplayData] Save failed: {ex.Message}");
        }
    }

    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, string> _keyCache = new();
    private static string? _keyRoot;

    /// <summary>Key of a level: path relative to the levels folder, lower-case, '/' separators.</summary>
    public static string KeyFor(string levelPath)
    {
        if (string.IsNullOrEmpty(levelPath)) return "";
        try
        {
            string root = Path.GetFullPath(FreeplayMenu.GetDefaultLevelsFolder());
            if (root != _keyRoot) { _keyCache.Clear(); _keyRoot = root; } // mods folder moved
            return _keyCache.GetOrAdd(levelPath, p =>
            {
                string full = Path.GetFullPath(p).TrimEnd('/', '\\');
                string rel = Path.GetRelativePath(root, full);
                string key = rel.StartsWith("..", StringComparison.Ordinal) || Path.IsPathRooted(rel) ? full : rel;
                return key.Replace('\\', '/').ToLowerInvariant();
            });
        }
        catch
        {
            return levelPath.Replace('\\', '/').ToLowerInvariant();
        }
    }

    /// <summary>Remembers the selected level without counting a play.</summary>
    public void SetLastSelected(string? levelPath)
    {
        string? key = levelPath == null ? null : KeyFor(levelPath);
        if (key == LastSelected) return;
        LastSelected = key;
        Save();
    }

    public FreeplayLevelRecord? Get(string levelPath)
    {
        lock (_lock)
            return Levels.TryGetValue(KeyFor(levelPath), out var r) ? r : null;
    }

    private FreeplayLevelRecord GetOrAdd(string levelPath)
    {
        string key = KeyFor(levelPath);
        if (!Levels.TryGetValue(key, out var r))
            Levels[key] = r = new FreeplayLevelRecord();
        return r;
    }

    public bool IsFavorite(string levelPath) => Get(FavoritePath(levelPath))?.Favorite == true;

    /// <summary>Toggles the favourite mark. Returns the new state.</summary>
    public bool ToggleFavorite(string levelPath)
    {
        bool now;
        lock (_lock)
        {
            var r = GetOrAdd(FavoritePath(levelPath));
            r.Favorite = !r.Favorite;
            now = r.Favorite;
        }
        Save();
        return now;
    }

    /// <summary>A level was started from Freeplay (play count, "recent" order).</summary>
    public void MarkPlayed(string levelPath)
    {
        lock (_lock)
        {
            var r = GetOrAdd(levelPath);
            r.Plays++;
            r.LastPlayedUtc = DateTime.UtcNow;
            LastSelected = KeyFor(levelPath);
        }
        Save();
    }

    /// <summary>Saves an attempt. Returns true when it is a new personal best for its side.</summary>
    public bool RecordAttempt(string levelPath, FreeplayAttempt run)
    {
        bool better;
        lock (_lock)
        {
            var r = GetOrAdd(levelPath);
            var best = r.Best(run.Enemy);
            better = run.Ranked && (best == null || run.Score > best.Score);
            r.Attempts.Add(run);
            // Cap: drop the oldest attempts that aren't a side's best.
            while (r.Attempts.Count > MaxAttemptsPerLevel)
            {
                var keep = new HashSet<FreeplayAttempt?> { r.Best(false), r.Best(true) };
                int idx = r.Attempts.FindIndex(a => !keep.Contains(a));
                if (idx < 0) break;
                r.Attempts.RemoveAt(idx);
            }
        }
        Save();
        return better;
    }

    /// <summary>Removes one attempt (its replay file is left alone).</summary>
    public void DeleteAttempt(string levelPath, FreeplayAttempt attempt)
    {
        bool removed;
        lock (_lock) removed = Get(levelPath)?.Attempts.Remove(attempt) == true;
        if (removed) Save();
    }

    /// <summary>Forgets a deleted level.</summary>
    public void Forget(string levelPath)
    {
        bool removed;
        lock (_lock) removed = Levels.Remove(KeyFor(levelPath));
        if (removed) Save();
    }

    public static float ClampRate(float rate)
    {
        if (float.IsNaN(rate) || rate <= 0f) return 1f;
        return Math.Clamp(MathF.Round(rate / RateStep) * RateStep, MinRate, MaxRate);
    }

    /// <summary>Letter rank from accuracy (Psych-style thresholds).</summary>
    public static string RankFor(float accuracy) => accuracy switch
    {
        >= 99.5f => "S+",
        >= 95f => "S",
        >= 90f => "A",
        >= 80f => "B",
        >= 70f => "C",
        _ => "D",
    };
}
