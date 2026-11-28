using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Xna.Framework;

namespace FunkyMakerPlayer;

/// <summary>
/// Fork: background analysis of every listed level, run after <see cref="Refresh"/>: osu!mania-style
/// star rating (player and enemy charts, also at the Freeplay song rate), BPM, note / hold counts,
/// peak notes per second, a note-density graph and the first note time (where the audio preview starts).
/// Only data.json is read (<see cref="FnmmLoader.TryLoadChartData"/>); results are cached by path +
/// last write time.
/// </summary>
public partial class FreeplayMenu
{
    private const int DensityBuckets = 72;

    private sealed class SideInfo
    {
        public List<(double Start, double End, int Column)> Notes = new();
        public int Columns, Taps, Holds;
        public float PeakNps;
        public double Stars;                       // at 1.0x
        public float[] Density = Array.Empty<float>(); // 0..1, normalized over both sides
    }

    private sealed class LevelInfo
    {
        public DateTime Stamp;
        public bool Failed;
        public float Bpm;
        public int StrumlineCount;
        public readonly SideInfo?[] Sides = new SideInfo?[2];
        public float FirstNoteSec;
        public float LengthSec;
    }

    private static readonly ConcurrentDictionary<string, LevelInfo> _infoCache = new();
    // (path, side, rate×100) -> stars, for song rates other than 1.0x
    private static readonly ConcurrentDictionary<(string Path, int Side, int Rate), double> _ratedStars = new();
    private readonly object _starQueueLock = new();
    private readonly Queue<string> _starQueue = new();
    private bool _starWorkerRunning;
    private int _infoGeneration;      // bumped whenever an analysis finishes
    private int _ratedGeneration;     // cancels stale rated-star passes

    private static DateTime LevelStamp(string path)
    {
        try { return Directory.Exists(path) ? Directory.GetLastWriteTimeUtc(path) : File.GetLastWriteTimeUtc(path); }
        catch { return DateTime.MinValue; }
    }

    /// <summary>Queues analysis for every listed level that isn't cached yet.</summary>
    private void QueueStarRatings()
    {
        lock (_starQueueLock)
        {
            _starQueue.Clear();
            foreach (var p in _allPaths)
                if (!_infoCache.TryGetValue(p, out var c) || c.Stamp != LevelStamp(p))
                    _starQueue.Enqueue(p);

            if (_starWorkerRunning || _starQueue.Count == 0) return;
            _starWorkerRunning = true;
        }

        var snapshot = _allPaths.ToArray(); // the list belongs to the game thread
        Task.Run(() =>
        {
            while (true)
            {
                string path;
                lock (_starQueueLock)
                {
                    if (_starQueue.Count == 0) { _starWorkerRunning = false; break; }
                    path = _starQueue.Dequeue();
                }
                _infoCache[path] = Analyze(path);
                Interlocked.Increment(ref _infoGeneration);
            }
            QueueRatedStars(snapshot);
        });
    }

    private static LevelInfo Analyze(string path)
    {
        var info = new LevelInfo { Stamp = LevelStamp(path) };
        try
        {
            var data = FnmmLoader.TryLoadChartData(path);
            if (data == null) { info.Failed = true; return info; }

            info.Bpm = data.Bpm > 0 ? data.Bpm : 0f;
            info.StrumlineCount = data.Strumlines.Count;

            double first = double.MaxValue, last = 0;
            for (int s = 0; s < data.Strumlines.Count; s++)
            {
                foreach (var lane in data.Strumlines[s])
                    foreach (var n in lane)
                    {
                        double start = data.ToSeconds(n.Start);
                        first = Math.Min(first, start);
                        last = Math.Max(last, start + Math.Max(0, data.ToSeconds(n.Duration)));
                    }
            }
            info.FirstNoteSec = first == double.MaxValue ? 0f : (float)first;
            info.LengthSec = (float)last;

            var counts = new int[2][];
            for (int side = 0; side < 2 && side < data.Strumlines.Count; side++)
            {
                var si = new SideInfo { Notes = ChartDifficulty.ExtractNotes(data, side, out int columns) };
                si.Columns = columns;
                si.Holds = si.Notes.Count(n => n.End > n.Start);
                si.Taps = si.Notes.Count - si.Holds;
                si.Stars = ChartDifficulty.Calculate(si.Notes, columns);

                // Notes per second: the busiest 1 s window (by note start)
                var starts = si.Notes.Select(n => n.Start).OrderBy(t => t).ToArray();
                int peak = 0;
                for (int i = 0, j = 0; i < starts.Length; i++)
                {
                    while (starts[i] - starts[j] >= 1000.0) j++;
                    peak = Math.Max(peak, i - j + 1);
                }
                si.PeakNps = peak;

                var bucket = new int[DensityBuckets];
                double lenMs = Math.Max(1.0, last * 1000.0);
                foreach (var t in starts)
                    bucket[Math.Clamp((int)(t / lenMs * DensityBuckets), 0, DensityBuckets - 1)]++;
                counts[side] = bucket;
                info.Sides[side] = si;
            }

            int max = 1;
            foreach (var c in counts) if (c != null) foreach (int v in c) max = Math.Max(max, v);
            for (int side = 0; side < 2; side++)
                if (info.Sides[side] is { } si && counts[side] is { } c)
                    si.Density = c.Select(v => v / (float)max).ToArray();
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[FreeplayMenu] Analysis failed for {Path.GetFileName(path)}: {ex.Message}");
            info.Failed = true;
        }
        return info;
    }

    private static LevelInfo? GetInfo(string path)
        => _infoCache.TryGetValue(path, out var i) && !i.Failed ? i : null;

    private static int RateKey(float rate) => (int)MathF.Round(rate * 100f);

    /// <summary>Side the player would play on this level: enemy only when enabled and the chart exists.</summary>
    private static int SideFor(LevelInfo? info)
        => FreeplayData.Instance.EnemyMode && info != null && info.StrumlineCount >= 2 ? 1 : 0;

    /// <summary>Star rating of a listed level for the current side and song rate, or null while unknown.</summary>
    private static double? GetStars(string path)
    {
        var info = GetInfo(path);
        if (info == null) return null;
        int side = SideFor(info);
        var si = info.Sides[side];
        if (si == null) return null;
        float rate = FreeplayData.Instance.SongRate;
        if (RateKey(rate) == 100) return si.Stars;
        return _ratedStars.TryGetValue((path, side, RateKey(rate)), out double v) ? v : si.Stars;
    }

    /// <summary>Computes star ratings at the current side / song rate in the background.</summary>
    private void QueueRatedStars(string[]? paths = null)
    {
        float rate = FreeplayData.Instance.SongRate;
        int rk = RateKey(rate);
        if (rk == 100) return;
        int gen = Interlocked.Increment(ref _ratedGeneration);
        paths ??= _allPaths.ToArray(); // game thread when no snapshot is given
        bool enemy = FreeplayData.Instance.EnemyMode;
        Task.Run(() =>
        {
            foreach (var p in paths)
            {
                if (gen != Volatile.Read(ref _ratedGeneration)) return;
                var info = GetInfo(p);
                if (info == null) continue;
                int side = enemy && info.StrumlineCount >= 2 ? 1 : 0;
                if (info.Sides[side] is not { } si || _ratedStars.ContainsKey((p, side, rk))) continue;
                _ratedStars[(p, side, rk)] = ChartDifficulty.Calculate(si.Notes, si.Columns, rate);
                Interlocked.Increment(ref _infoGeneration);
            }
        });
    }

    // osu! star rating colour spectrum
    private static readonly (double Stars, Color Color)[] StarSpectrum =
    {
        (0.1, new Color(0x42, 0x90, 0xFB)),
        (1.25, new Color(0x4F, 0xC0, 0xFF)),
        (2.0, new Color(0x4F, 0xFF, 0xD5)),
        (2.5, new Color(0x7C, 0xFF, 0x4F)),
        (3.3, new Color(0xF6, 0xF0, 0x5C)),
        (4.2, new Color(0xFF, 0x80, 0x68)),
        (4.9, new Color(0xFF, 0x4E, 0x6F)),
        (5.8, new Color(0xC6, 0x45, 0xB8)),
        (6.7, new Color(0x65, 0x63, 0xDE)),
        (7.7, new Color(0x18, 0x15, 0x8E)),
        (9.0, new Color(0x00, 0x00, 0x00)),
    };

    internal static Color StarColor(double stars)
    {
        if (stars <= StarSpectrum[0].Stars) return StarSpectrum[0].Color;
        for (int i = 1; i < StarSpectrum.Length; i++)
        {
            if (stars <= StarSpectrum[i].Stars)
            {
                var (s0, c0) = StarSpectrum[i - 1];
                var (s1, c1) = StarSpectrum[i];
                return Color.Lerp(c0, c1, (float)((stars - s0) / (s1 - s0)));
            }
        }
        return StarSpectrum[^1].Color;
    }

    internal static Color StarTextColor(double stars)
        => stars < 6.5 ? new Color(20, 20, 30) : new Color(255, 217, 102);

    /// <summary>Star colour made readable on the dark UI (very hard ratings go near-black in osu!).</summary>
    private static Color StarAccent(double stars)
    {
        var c = StarColor(stars);
        float lum = (0.299f * c.R + 0.587f * c.G + 0.114f * c.B) / 255f;
        return lum < 0.35f ? Color.Lerp(c, new Color(255, 217, 102), 0.55f) : c;
    }
}
