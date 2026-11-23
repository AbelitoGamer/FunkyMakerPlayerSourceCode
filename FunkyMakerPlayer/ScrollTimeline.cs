using System;
using System.Collections.Generic;
using System.Linq;

namespace FunkyMakerPlayer;

/// <summary>
/// Variable scroll speed (fork; used by osu! songs' SV): maps song time to a "scroll position", the
/// integral of the speed multiplier over time — osu!'s SequentialScrollAlgorithm
/// (ppy/osu, MIT: <c>relativePositionAt = mapping.Position + (time - mapping.Time) * multiplier</c>).
/// With a constant multiplier of 1 the position equals the time, so a renderer can always draw a
/// note at <c>(Position(note) - Position(now)) × pixelsPerSecond</c>.
/// </summary>
public sealed class ScrollTimeline
{
    private readonly double[] _times;      // seconds, ascending
    private readonly double[] _multipliers;
    private readonly double[] _positions;  // accumulated position at each point

    /// <param name="points">(time in seconds, speed multiplier from that time on); at least one.</param>
    public ScrollTimeline(IEnumerable<(double Time, double Multiplier)> points)
    {
        var list = points.OrderBy(p => p.Time).ToList();
        if (list.Count == 0) list.Add((0, 1));
        _times = list.Select(p => p.Time).ToArray();
        _multipliers = list.Select(p => p.Multiplier).ToArray();
        _positions = new double[list.Count];
        for (int i = 1; i < list.Count; i++)
            _positions[i] = _positions[i - 1] + (_times[i] - _times[i - 1]) * _multipliers[i - 1];
    }

    /// <summary>Scroll position at <paramref name="time"/> (seconds). Before the first point, its multiplier applies.</summary>
    public double Position(double time)
    {
        int i = Array.BinarySearch(_times, time);
        if (i < 0) i = Math.Max(0, ~i - 1);
        return _positions[i] + (time - _times[i]) * _multipliers[i];
    }

    /// <summary>Speed multiplier at <paramref name="time"/>.</summary>
    public double MultiplierAt(double time)
    {
        int i = Array.BinarySearch(_times, time);
        if (i < 0) i = Math.Max(0, ~i - 1);
        return _multipliers[i];
    }

    public int Count => _times.Length;
}
