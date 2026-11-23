using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace FunkyMakerPlayer.Osu;

/// <summary>
/// Turns an osu!mania 4K difficulty (.osu inside an extracted set folder) into an in-memory
/// <see cref="FnmmLevel"/> the normal gameplay runs (fork). The .osu file is never modified.
/// <list type="bullet">
/// <item>One strumline (yours, centred), 4 lanes = the 4 mania columns, times in seconds
/// (<c>Data.Version = 2</c>), BPM = the map's main BPM (countdown / beat effects).</item>
/// <item>No scene: the background image is drawn behind the notes (<see cref="FnmmLevel.BackgroundImage"/>).</item>
/// <item>Not converted: scroll speed changes (inherited timing points), hitsounds, storyboards,
/// videos, OD (FNF judgement is used), HP drain.</item>
/// </list>
/// </summary>
public static class OsuLevelLoader
{
    public static FnmmLevel Load(string osuPath, Action<float, string>? progress = null)
    {
        progress?.Invoke(0.1f, "Reading osu! beatmap...");
        var map = OsuBeatmap.ParseFile(osuPath);
        if (!map.IsMania)
            throw new Exception("Only osu!mania beatmaps are supported.");
        if (map.KeyCount != 4)
            throw new Exception($"Only 4-key osu!mania beatmaps are supported (this one is {map.KeyCount}K).");
        if (map.Notes.Count == 0)
            throw new Exception("This osu! beatmap has no notes.");

        progress?.Invoke(0.3f, "Converting chart...");
        var level = new FnmmLevel
        {
            IsOsu = true,
            Data = ToChart(map),
            Scene = new FnmmScene { Landscape = true },
            Events = new FnmmEvents(),
            LevelName = DisplayName(map),
        };
        level.Meta.Name = level.LevelName;
        level.Meta.UUID = map.BeatmapId > 0 ? $"osu-{map.BeatmapId}" : $"osu-{Path.GetFileNameWithoutExtension(osuPath)}";

        progress?.Invoke(0.5f, "Loading audio...");
        string folder = Path.GetDirectoryName(osuPath)!;
        string? audio = OsuLibrary.FindFile(folder, map.AudioFilename);
        if (audio == null) throw new Exception($"Audio file not found: {map.AudioFilename}");
        level.MusicData = File.ReadAllBytes(audio);

        progress?.Invoke(0.8f, "Loading background...");
        string? bg = OsuLibrary.FindFile(folder, map.BackgroundFile);
        if (bg != null)
        {
            try { level.BackgroundImage = File.ReadAllBytes(bg); }
            catch (Exception ex) { Console.WriteLine($"[Osu] Background unreadable: {ex.Message}"); }
        }

        progress?.Invoke(0.9f, "Loading hitsounds and storyboard...");
        level.OsuExtras = BuildExtras(map, folder);

        progress?.Invoke(1f, "Complete!");
        return level;
    }

    /// <summary>SV timeline, hitsounds (decoded), storyboard (parsed; images load in SetupLevel), video, skin.</summary>
    private static OsuLevelExtras BuildExtras(OsuBeatmap map, string folder)
    {
        var extras = new OsuLevelExtras
        {
            Beatmap = map,
            Folder = folder,
            Scroll = BuildScroll(map),
            HasBeatmapSkin = OsuLibrary.HasBeatmapSkin(folder),
            VideoPath = OsuLibrary.FindFile(folder, map.VideoFile),
            VideoStartSec = map.VideoStartMs / 1000.0,
        };

        try
        {
            string? userSkin = Path.Combine(PlatformPaths.GetNoteStylesPath(), Game1.NoteStyleName);
            if (!OsuManiaSkin.IsSkinFolder(userSkin)) userSkin = null;
            extras.Hitsounds = OsuHitsounds.Build(map, folder, userSkin);
            extras.Hitsounds?.LoadSounds();
        }
        catch (Exception ex) { Console.WriteLine($"[Osu] Hitsounds failed: {ex.Message}"); }

        try
        {
            // The set's .osb (shared by every difficulty) first, then this difficulty's [Events].
            var lines = new List<string>();
            var vars = new List<(string, string)>();
            string? osb = Directory.EnumerateFiles(folder, "*.osb").FirstOrDefault();
            if (osb != null) ReadOsb(File.ReadAllText(osb), lines, vars);
            lines.AddRange(map.EventLines);
            vars.AddRange(map.Variables);
            extras.Storyboard = OsuStoryboard.Parse(lines, vars, map.WidescreenStoryboard);
        }
        catch (Exception ex) { Console.WriteLine($"[Osu] Storyboard failed: {ex.Message}"); }
        return extras;
    }

    private static void ReadOsb(string text, List<string> lines, List<(string, string)> vars)
    {
        string section = "";
        foreach (var raw in text.Split('\n'))
        {
            string line = raw.TrimEnd('\r', ' ', '\t');
            string trimmed = line.Trim().TrimStart('\uFEFF');
            if (trimmed.Length == 0 || trimmed.StartsWith("//", StringComparison.Ordinal)) continue;
            if (trimmed.StartsWith('[') && trimmed.EndsWith(']')) { section = trimmed[1..^1]; continue; }
            if (section == "Events") lines.Add(line);
            else if (section == "Variables")
            {
                int eq = trimmed.IndexOf('=');
                if (eq > 0) vars.Add((trimmed[..eq].Trim(), trimmed[(eq + 1)..].Trim()));
            }
        }
    }

    /// <summary>
    /// osu!mania's Sequential scrolling: multiplier = SV × mostCommonBeatLength / beatLength (ppy/osu,
    /// MIT: MultiplierControlPoint, DrawableManiaRuleset RelativeScaleBeatLengths). SV = 100 / −beatLength
    /// of an inherited point, clamped 0.01..10; an uninherited point resets it to 1; when both share a
    /// time the inherited one wins. Null when the speed never changes.
    /// </summary>
    public static ScrollTimeline? BuildScroll(OsuBeatmap map)
    {
        if (map.TimingPoints.Count == 0) return null;
        float bpm = map.MainBpm;
        double baseBeat = bpm > 0 ? 60000.0 / bpm : 500.0;
        double beat = baseBeat, speed = 1;
        var points = new List<(double, double)>();
        foreach (var group in map.TimingPoints.GroupBy(t => t.TimeMs).OrderBy(g => g.Key))
        {
            var timing = group.LastOrDefault(t => t.Uninherited);
            var inherited = group.LastOrDefault(t => !t.Uninherited && t.BeatLength < 0);
            if (timing != null) { beat = timing.BeatLength; speed = 1; }
            if (inherited != null) speed = Math.Clamp(100.0 / -inherited.BeatLength, 0.01, 10);
            points.Add((group.Key / 1000.0, speed * baseBeat / beat));
        }
        if (points.All(p => Math.Abs(p.Item2 - 1) < 0.001)) return null;
        return new ScrollTimeline(points);
    }

    /// <summary>Chart only (Freeplay analysis: stars, density, first note). Null if it isn't 4K mania.</summary>
    public static FnmmData? LoadChartData(string osuPath)
    {
        try
        {
            var map = OsuBeatmap.ParseFile(osuPath);
            return map.IsMania4K ? ToChart(map) : null;
        }
        catch { return null; }
    }

    public static string DisplayName(OsuBeatmap map)
        => map.Version.Length > 0 ? $"{map.SongName} [{map.Version}]" : map.SongName;

    private static FnmmData ToChart(OsuBeatmap map)
    {
        var lanes = new List<List<FnmmNote>> { new(), new(), new(), new() };
        foreach (var n in map.Notes)
        {
            lanes[n.Column].Add(new FnmmNote
            {
                Start = (float)(n.StartMs / 1000.0),
                Duration = n.IsHold ? (float)((n.EndMs - n.StartMs) / 1000.0) : 0f,
            });
        }
        return new FnmmData
        {
            Version = 2f,          // times in seconds (FnmmTimeConverter)
            Bpm = map.MainBpm,
            Speed = 1f,
            Strumlines = new List<List<List<FnmmNote>>> { lanes },
        };
    }

    /// <summary>The difficulty's audio file bytes (preview), or null.</summary>
    public static byte[]? ReadAudio(string osuPath, out float previewSeconds)
    {
        previewSeconds = -1f;
        try
        {
            var map = OsuBeatmap.ParseFile(osuPath, headerOnly: true);
            previewSeconds = map.PreviewTime >= 0 ? map.PreviewTime / 1000f : -1f;
            string? audio = OsuLibrary.FindFile(Path.GetDirectoryName(osuPath)!, map.AudioFilename);
            return audio != null ? File.ReadAllBytes(audio) : null;
        }
        catch { return null; }
    }

    /// <summary>The difficulty's background image bytes (list icon), or null.</summary>
    public static byte[]? ReadBackground(string osuPath)
    {
        try
        {
            var map = OsuBeatmap.ParseFile(osuPath, headerOnly: true);
            string? bg = OsuLibrary.FindFile(Path.GetDirectoryName(osuPath)!, map.BackgroundFile);
            return bg != null ? File.ReadAllBytes(bg) : null;
        }
        catch { return null; }
    }
}

/// <summary>What an osu! level carries beyond the chart (fork).</summary>
public sealed class OsuLevelExtras
{
    public OsuBeatmap Beatmap { get; init; } = null!;
    public string Folder { get; init; } = "";
    public ScrollTimeline? Scroll { get; init; }
    public OsuHitsounds? Hitsounds { get; set; }
    public OsuStoryboard? Storyboard { get; set; }
    public string? VideoPath { get; init; }
    public double VideoStartSec { get; init; }
    public bool HasBeatmapSkin { get; init; }
}
