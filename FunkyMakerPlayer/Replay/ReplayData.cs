using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace FunkyMakerPlayer.Replay;

/// <summary>
/// One frame of recorded input for one strumline (fork, replays).
/// <list type="bullet">
/// <item><c>T</c>: song time (seconds, the player's own song clock) the frame was judged at.</item>
/// <item><c>P</c>: song time of the frame before it (<see cref="NoteRenderer.NoPreviousFrame"/> if none).</item>
/// <item><c>H</c> / <c>R</c> / <c>J</c>: lane bit masks (bit 0 = left ... bit 3 = right) of keys held
/// this frame, held the previous frame, and just pressed this frame.</item>
/// </list>
/// Only frames where something changed are stored.
/// </summary>
public readonly record struct ReplayFrame(float T, float P, int H, int R, int J);

/// <summary>
/// (fork) A Skip Breaks jump (<see cref="Game1"/>): at song time <c>From</c> the song jumped to <c>To</c>.
/// Playback repeats it at the same song time, so the replay stays valid.
/// </summary>
public readonly record struct ReplaySkip(float From, float To);

/// <summary>Final numbers of a track, used to show results and to detect a replay that drifted.</summary>
public sealed class ReplayResult
{
    public int Score { get; set; }
    public int Misses { get; set; }
    public int Sicks { get; set; }
    public int Goods { get; set; }
    public int Bads { get; set; }
    public int Shits { get; set; }
    public int MaxCombo { get; set; }
    public float Accuracy { get; set; }

    public bool SameJudgementsAs(ReplayResult other) =>
        Score == other.Score && Misses == other.Misses && Sicks == other.Sicks &&
        Goods == other.Goods && Bads == other.Bads && Shits == other.Shits;
}

/// <summary>One player's recording.</summary>
public sealed class ReplayTrack
{
    public string PlayerId { get; set; } = "";
    public string Username { get; set; } = "";
    public int ColorHue { get; set; } = 180;
    /// <summary>Chart (strumline index in the level data) this player played.</summary>
    public int ChartIndex { get; set; }
    /// <summary>Multiplayer role (1 = Player 1, 2 = Player 2); 0 for solo.</summary>
    public int Role { get; set; }
    // Judging settings of the recorded player.
    public float InputBuffer { get; set; }
    public bool GhostTapping { get; set; } = true;
    public float SafeFrames { get; set; }
    /// <summary>First positive song time of the run (safe frames start there).</summary>
    public float SongStartTime { get; set; } = -1f;
    /// <summary>
    /// Online: when this player's song started, in synced server time (ms). Both players' values
    /// are within a fraction of a second for the same match; used to reject an opponent track
    /// from an older match on the same level.
    /// </summary>
    public long MatchStartServerMs { get; set; }
    /// <summary>(fork) Online side balancing multiplier the run was scored with (1 = none).</summary>
    public double ScoreMultiplier { get; set; } = 1.0;
    public List<ReplayFrame> Frames { get; set; } = new();
    /// <summary>(fork) Skip Breaks jumps, in order (empty in older replays).</summary>
    public List<ReplaySkip> Skips { get; set; } = new();
    public ReplayResult Result { get; set; } = new();
}

/// <summary>A saved replay: the level fingerprint plus one (solo) or two (online) tracks.</summary>
public sealed class ReplayFile
{
    public const int CurrentVersion = 1;
    public const string Extension = ".fmr";

    public int Version { get; set; } = CurrentVersion;
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public long CreatedUnix { get; set; } = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
    public string GameVersion { get; set; } = "";
    /// <summary>"solo" or "online".</summary>
    public string Kind { get; set; } = "solo";
    /// <summary>Online: the match id (same on both players' machines).</summary>
    public string MatchId { get; set; } = "";
    /// <summary>SHA-256 of the level file; playback needs this exact file.</summary>
    public string LevelHash { get; set; } = "";
    public string LevelFileName { get; set; } = "";
    public string LevelName { get; set; } = "";
    public float DurationSeconds { get; set; }
    /// <summary>Index into <see cref="Tracks"/> of the player who saved this replay.</summary>
    public int LocalTrack { get; set; }
    public List<ReplayTrack> Tracks { get; set; } = new();

    [JsonIgnore] public string FilePath { get; set; } = "";
    [JsonIgnore] public bool IsOnline => Kind == "online";
    [JsonIgnore] public ReplayTrack? Local => LocalTrack >= 0 && LocalTrack < Tracks.Count ? Tracks[LocalTrack] : null;
}

/// <summary>Saving, loading and listing replays (<c>&lt;multiplayer storage&gt;/replays/*.fmr</c>, gzip JSON).</summary>
public static class ReplayStore
{
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = false };

    public static string Directory
    {
        get
        {
            string dir = Path.Combine(Multiplayer.MultiplayerSettings.GetStorageDirectory(), "replays");
            System.IO.Directory.CreateDirectory(dir);
            return dir;
        }
    }

    public static string Save(ReplayFile replay)
    {
        string stamp = DateTimeOffset.FromUnixTimeSeconds(replay.CreatedUnix).ToLocalTime().ToString("yyyyMMdd-HHmmss");
        string safeName = string.Concat((replay.LevelName.Length > 0 ? replay.LevelName : "level")
            .Select(c => Path.GetInvalidFileNameChars().Contains(c) ? '_' : c));
        if (safeName.Length > 40) safeName = safeName[..40];
        string path = Path.Combine(Directory, $"{stamp} {safeName} {replay.Id[..6]}{ReplayFile.Extension}");
        WriteTo(replay, path);
        replay.FilePath = path;
        return path;
    }

    public static void WriteTo(ReplayFile replay, string path)
    {
        string tmp = path + ".tmp";
        using (var file = File.Create(tmp))
        using (var gz = new GZipStream(file, CompressionLevel.Optimal))
            JsonSerializer.Serialize(gz, replay, Json);
        File.Move(tmp, path, overwrite: true);
    }

    public static ReplayFile? Load(string path)
    {
        try
        {
            using var file = File.OpenRead(path);
            using var gz = new GZipStream(file, CompressionMode.Decompress);
            var replay = JsonSerializer.Deserialize<ReplayFile>(gz, Json);
            if (replay == null || replay.Version > ReplayFile.CurrentVersion) return null;
            replay.FilePath = path;
            return replay;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[Replay] Could not read {Path.GetFileName(path)}: {ex.Message}");
            return null;
        }
    }

    /// <summary>All readable replays, newest first.</summary>
    public static List<ReplayFile> List()
    {
        var list = new List<ReplayFile>();
        foreach (var path in System.IO.Directory.GetFiles(Directory, "*" + ReplayFile.Extension))
            if (Load(path) is { } r) list.Add(r);
        return list.OrderByDescending(r => r.CreatedUnix).ToList();
    }

    /// <summary>The saved online replay for a match, if any.</summary>
    public static ReplayFile? FindByMatch(string matchId) =>
        string.IsNullOrEmpty(matchId) ? null : List().FirstOrDefault(r => r.MatchId == matchId);

    public static void Delete(ReplayFile replay)
    {
        try { if (File.Exists(replay.FilePath)) File.Delete(replay.FilePath); }
        catch (Exception ex) { Console.WriteLine($"[Replay] Could not delete: {ex.Message}"); }
    }

    /// <summary>Gzip JSON bytes of one track (sent to the opponent after an online match).</summary>
    public static byte[] SerializeTrack(ReplayTrack track)
    {
        using var ms = new MemoryStream();
        using (var gz = new GZipStream(ms, CompressionLevel.Optimal, leaveOpen: true))
            JsonSerializer.Serialize(gz, track, Json);
        return ms.ToArray();
    }

    public static ReplayTrack? DeserializeTrack(byte[] data)
    {
        try
        {
            using var ms = new MemoryStream(data);
            using var gz = new GZipStream(ms, CompressionMode.Decompress);
            return JsonSerializer.Deserialize<ReplayTrack>(gz, Json);
        }
        catch
        {
            return null;
        }
    }
}

/// <summary>Collects one strumline's input while it is played live.</summary>
public sealed class ReplayRecorder
{
    public ReplayTrack Track { get; } = new();

    /// <summary>False once something made the recording unusable (botplay, timeskip...).</summary>
    public bool Valid { get; private set; } = true;
    public string InvalidReason { get; private set; } = "";

    public void Add(ReplayFrame frame) => Track.Frames.Add(frame);

    public void Invalidate(string reason)
    {
        if (!Valid) return;
        Valid = false;
        InvalidReason = reason;
        Console.WriteLine($"[Replay] Recording stopped: {reason}");
    }
}
