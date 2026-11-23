using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;

namespace FunkyMakerPlayer.Osu;

/// <summary>
/// A [TimingPoints] line: <c>time,beatLength,meter,sampleSet,sampleIndex,volume,uninherited,effects</c>.
/// <see cref="BeatLength"/> is the raw value (negative on inherited points = scroll speed).
/// </summary>
public sealed record OsuTimingPoint(double TimeMs, double BeatLength, bool Uninherited,
    int SampleSet = 0, int SampleIndex = 0, int Volume = 100);

/// <summary>
/// A mania note: column, start and end time in ms (EndMs == StartMs for a tap note) and its hit sound:
/// <see cref="HitSound"/> flags (1 normal, 2 whistle, 4 finish, 8 clap) and the hitSample fields
/// <c>normalSet:additionSet:index:volume:filename</c> (0 / empty = inherit from the timing point).
/// </summary>
public sealed record OsuManiaNote(int Column, double StartMs, double EndMs,
    int HitSound = 0, int NormalSet = 0, int AdditionSet = 0, int Index = 0, int Volume = 0, string? Filename = null)
{
    public bool IsHold => EndMs > StartMs;
}

/// <summary>
/// One osu! difficulty (.osu file), parsed the way osu! (lazer) reads legacy beatmaps
/// (ppy/osu, MIT: LegacyBeatmapDecoder, ConvertHitObjectParser, ManiaBeatmapConverter):
/// <list type="bullet">
/// <item>Format v1–v4 files get <see cref="EarlyVersionOffsetMs"/> (+24 ms) added to every time.</item>
/// <item>Mania key count = round(CircleSize), at least 1; column = clamp(floor(x · keys / 512), 0, keys − 1).</item>
/// <item>Hold note = type bit 128; its end time is the 6th field before the first ':'.</item>
/// <item>Inherited timing points (negative beat length) are scroll speed changes in mania: not used here.</item>
/// </list>
/// </summary>
public sealed class OsuBeatmap
{
    public const int EarlyVersionOffsetMs = 24;
    public const int ManiaMode = 3;

    public int FormatVersion { get; private set; } = 14;

    // [General]
    public string AudioFilename { get; private set; } = "";
    public int AudioLeadIn { get; private set; }
    /// <summary>Preview start in ms (offset applied), or -1 when the map has none.</summary>
    public int PreviewTime { get; private set; } = -1;
    public int Mode { get; private set; }
    /// <summary>[General] SampleSet: 1 normal, 2 soft, 3 drum (default for timing points with set 0).</summary>
    public int SampleSet { get; private set; } = 1;
    public bool WidescreenStoryboard { get; private set; }

    // [Metadata]
    public string Title { get; private set; } = "";
    public string TitleUnicode { get; private set; } = "";
    public string Artist { get; private set; } = "";
    public string ArtistUnicode { get; private set; } = "";
    public string Creator { get; private set; } = "";
    public string Version { get; private set; } = "";
    public string Source { get; private set; } = "";
    public int BeatmapId { get; private set; } = -1;
    public int BeatmapSetId { get; private set; } = -1;

    // [Difficulty]
    public float CircleSize { get; private set; } = 5f;
    public float OverallDifficulty { get; private set; } = 5f;
    public float HpDrainRate { get; private set; } = 5f;

    // [Events]
    public string? BackgroundFile { get; private set; }
    /// <summary>Video event: file and the song time (ms, offset applied) it starts at.</summary>
    public string? VideoFile { get; private set; }
    public double VideoStartMs { get; private set; }
    /// <summary>[Events] lines as written (indentation kept: storyboard commands), comments removed.</summary>
    public List<string> EventLines { get; } = new();
    /// <summary>[Variables] ($name=value) for the storyboard.</summary>
    public List<(string Name, string Value)> Variables { get; } = new();

    public List<OsuTimingPoint> TimingPoints { get; } = new();
    public List<OsuManiaNote> Notes { get; } = new();

    public bool IsMania => Mode == ManiaMode;
    public int KeyCount => Math.Max(1, (int)Math.Round(CircleSize));
    public bool IsMania4K => IsMania && KeyCount == 4;

    public string DisplayTitle => Title.Length > 0 ? Title : TitleUnicode;
    public string DisplayArtist => Artist.Length > 0 ? Artist : ArtistUnicode;
    public string SongName => DisplayArtist.Length > 0 ? $"{DisplayArtist} - {DisplayTitle}" : DisplayTitle;

    /// <summary>
    /// The BPM that lasts longest (osu!'s "main" BPM), from uninherited points; 0 if none.
    /// </summary>
    public float MainBpm
    {
        get
        {
            var timing = TimingPoints.Where(t => t.Uninherited && t.BeatLength > 0).OrderBy(t => t.TimeMs).ToList();
            if (timing.Count == 0) return 0f;
            double end = Notes.Count > 0 ? Notes.Max(n => n.EndMs) : timing[^1].TimeMs;
            var durations = new Dictionary<double, double>();
            for (int i = 0; i < timing.Count; i++)
            {
                double start = i == 0 ? 0 : timing[i].TimeMs;
                double stop = i + 1 < timing.Count ? timing[i + 1].TimeMs : Math.Max(end, start);
                double bl = Math.Round(timing[i].BeatLength, 3);
                durations[bl] = durations.GetValueOrDefault(bl) + Math.Max(0, stop - start);
            }
            double main = durations.OrderByDescending(kv => kv.Value).First().Key;
            return (float)(60000.0 / main);
        }
    }

    private int Offset => FormatVersion < 5 ? EarlyVersionOffsetMs : 0;

    /// <summary>Parses a .osu file. <paramref name="headerOnly"/> stops before [TimingPoints] (fast listing).</summary>
    public static OsuBeatmap Parse(string text, bool headerOnly = false)
    {
        var map = new OsuBeatmap();
        string section = "";
        using var reader = new StringReader(text);
        string? raw;
        bool first = true;
        while ((raw = reader.ReadLine()) != null)
        {
            string line = raw.Trim();
            if (first)
            {
                first = false;
                line = line.TrimStart('﻿');
                const string header = "osu file format v";
                int h = line.IndexOf(header, StringComparison.OrdinalIgnoreCase);
                if (h >= 0 && int.TryParse(line[(h + header.Length)..].Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out int v))
                {
                    map.FormatVersion = v;
                    continue;
                }
            }
            if (line.Length == 0 || line.StartsWith("//", StringComparison.Ordinal)) continue;
            if (line.StartsWith('[') && line.EndsWith(']'))
            {
                section = line[1..^1];
                if (headerOnly && (section == "TimingPoints" || section == "HitObjects")) break;
                continue;
            }
            if (section == "Events")
            {
                // Storyboard commands are nested by leading spaces / underscores: keep them.
                string ev = raw.TrimEnd();
                if (ev.TrimStart().StartsWith("//", StringComparison.Ordinal)) continue;
                map.EventLines.Add(ev);
            }
            if (section != "Metadata")
            {
                int c = line.IndexOf("//", StringComparison.Ordinal);
                if (c > 0) line = line[..c].TrimEnd();
            }

            try
            {
                switch (section)
                {
                    case "General": map.ParseGeneral(line); break;
                    case "Metadata": map.ParseMetadata(line); break;
                    case "Difficulty": map.ParseDifficulty(line); break;
                    case "Events": map.ParseEvent(line); break;
                    case "Variables":
                        int eq = line.IndexOf('=');
                        if (eq > 0) map.Variables.Add((line[..eq].Trim(), line[(eq + 1)..].Trim()));
                        break;
                    case "TimingPoints": map.ParseTimingPoint(line); break;
                    case "HitObjects": map.ParseHitObject(line); break;
                }
            }
            catch (Exception ex)
            {
                // osu! logs and skips bad lines; so do we.
                Console.WriteLine($"[Osu] Skipped line in [{section}]: {ex.Message}");
            }
        }
        map.Notes.Sort((a, b) => a.StartMs != b.StartMs ? a.StartMs.CompareTo(b.StartMs) : a.Column.CompareTo(b.Column));
        return map;
    }

    public static OsuBeatmap ParseFile(string path, bool headerOnly = false) => Parse(File.ReadAllText(path), headerOnly);

    private static (string Key, string Value) KeyValue(string line)
    {
        int i = line.IndexOf(':');
        return i < 0 ? (line.Trim(), "") : (line[..i].Trim(), line[(i + 1)..].Trim());
    }

    private static double D(string s) => double.Parse(s.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture);
    private static int I(string s) => (int)Math.Round(D(s));

    private void ParseGeneral(string line)
    {
        var (k, v) = KeyValue(line);
        switch (k)
        {
            case "AudioFilename": AudioFilename = v.Replace('\\', '/'); break;
            case "AudioLeadIn": AudioLeadIn = I(v); break;
            case "PreviewTime": int t = I(v); PreviewTime = t == -1 ? -1 : t + Offset; break;
            case "Mode": Mode = I(v); break;
            case "SampleSet": SampleSet = BankFromName(v); break;
            case "WidescreenStoryboard": WidescreenStoryboard = v.Trim() == "1"; break;
        }
    }

    private void ParseMetadata(string line)
    {
        var (k, v) = KeyValue(line);
        switch (k)
        {
            case "Title": Title = v; break;
            case "TitleUnicode": TitleUnicode = v; break;
            case "Artist": Artist = v; break;
            case "ArtistUnicode": ArtistUnicode = v; break;
            case "Creator": Creator = v; break;
            case "Version": Version = v; break;
            case "Source": Source = v; break;
            case "BeatmapID": BeatmapId = I(v); break;
            case "BeatmapSetID": BeatmapSetId = I(v); break;
        }
    }

    private void ParseDifficulty(string line)
    {
        var (k, v) = KeyValue(line);
        switch (k)
        {
            case "CircleSize": CircleSize = (float)D(v); break;
            case "OverallDifficulty": OverallDifficulty = Math.Clamp((float)D(v), 0f, 10f); break;
            case "HPDrainRate": HpDrainRate = Math.Clamp((float)D(v), 0f, 10f); break;
        }
    }

    /// <summary>"Normal" / "Soft" / "Drum" (or 1 / 2 / 3) → 1 / 2 / 3; anything else → 1.</summary>
    public static int BankFromName(string v) => v.Trim().ToLowerInvariant() switch
    {
        "soft" or "2" => 2,
        "drum" or "3" => 3,
        _ => 1,
    };

    private void ParseEvent(string line)
    {
        var parts = line.Split(',');
        if (parts.Length < 3) return;
        string type = parts[0].Trim();
        // Video: "Video,startTime,"file"" (or "1,…"); starts at that song time from its beginning.
        if (VideoFile == null && (type == "1" || type.Equals("Video", StringComparison.OrdinalIgnoreCase)))
        {
            VideoFile = CleanFilename(parts[2]);
            VideoStartMs = D(parts[1]) + Offset;
            return;
        }
        // Background: "0,0,"file",x,y"; older maps use "Background"; the first sprite stands in if there's none.
        if (BackgroundFile == null && (type == "0" || type.Equals("Background", StringComparison.OrdinalIgnoreCase)))
            BackgroundFile = CleanFilename(parts[2]);
    }

    private static string CleanFilename(string s) => s.Replace("\\\\", "\\").Trim().Trim('"').Replace('\\', '/');

    private void ParseTimingPoint(string line)
    {
        var p = line.Split(',');
        if (p.Length < 2) return;
        double time = D(p[0]) + Offset;
        double beatLength = D(p[1]);
        bool uninherited = p.Length < 7 || p[6].Trim().StartsWith('1');
        if (double.IsNaN(beatLength)) return;
        int set = p.Length > 3 && p[3].Trim().Length > 0 ? I(p[3]) : 0;
        int index = p.Length > 4 && p[4].Trim().Length > 0 ? I(p[4]) : 0;
        int volume = p.Length > 5 && p[5].Trim().Length > 0 ? I(p[5]) : 100;
        TimingPoints.Add(new OsuTimingPoint(time, beatLength, uninherited && beatLength > 0, set, index, volume));
    }

    private void ParseHitObject(string line)
    {
        var p = line.Split(',');
        if (p.Length < 4) return;
        float x = Math.Clamp((float)D(p[0]), 0f, 512f);
        double start = D(p[2]) + Offset;
        int type = I(p[3]);
        int keys = KeyCount;
        int column = Math.Clamp((int)MathF.Floor(x * keys / 512f), 0, keys - 1);
        double end = start;
        int hitSound = p.Length > 4 && p[4].Trim().Length > 0 ? I(p[4]) : 0;
        string sample = p.Length > 5 ? p[5] : "";
        if ((type & 128) != 0)
        {
            double endRaw = D(p[2]);
            if (sample.Length > 0)
            {
                var parts = sample.Split(':');
                if (parts[0].Length > 0) endRaw = D(parts[0]);
                sample = parts.Length > 1 ? string.Join(':', parts.Skip(1)) : "";
            }
            end = Math.Max(start, endRaw + Offset);
        }
        // hitSample: normalSet:additionSet:index:volume:filename
        var hs = sample.Split(':');
        int Field(int i) => hs.Length > i && int.TryParse(hs[i].Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out int v) ? v : 0;
        string? file = hs.Length > 4 && hs[4].Trim().Length > 0 ? hs[4].Trim() : null;
        Notes.Add(new OsuManiaNote(column, start, end, hitSound, Field(0), Field(1), Field(2), Math.Max(0, Field(3)), file));
    }
}
