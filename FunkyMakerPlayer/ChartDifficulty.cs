using System;
using System.Collections.Generic;
using System.Linq;

namespace FunkyMakerPlayer;

/// <summary>
/// Star rating for a strumline's chart (fork), ported from osu!mania's difficulty calculator
/// (ppy/osu, MIT licence: ManiaDifficultyCalculator, Skills/Strain, Evaluators/Individual- and
/// OverallStrainEvaluator, StrainSkill / StrainDecaySkill). Each lane is a mania column and the
/// chart is rated at 1.0x clock rate with no mods. Hold notes count as long notes.
/// </summary>
public static class ChartDifficulty
{
    private const double DifficultyMultiplier = 0.018;
    private const double IndividualDecayBase  = 0.125;
    private const double OverallDecayBase     = 0.30;
    private const double ReleaseThreshold     = 30;
    private const int    SectionLength        = 400;   // ms
    private const double DecayWeight          = 0.9;

    private sealed class Obj
    {
        public double StartTime, EndTime, DeltaTime, ColumnStrainTime;
        public int    Column;
        public Obj?[] PreviousHitObjects = Array.Empty<Obj?>();
    }

    /// <summary>
    /// Star rating of <paramref name="strumline"/> in <paramref name="data"/> (0 if it has under 2 notes).
    /// <paramref name="clockRate"/> is the song rate (fork: Freeplay rate modifier), like osu!'s DT/HT.
    /// </summary>
    public static double Calculate(FnmmData data, int strumline = 0, double clockRate = 1.0)
    {
        if (strumline < 0 || strumline >= data.Strumlines.Count) return 0;
        var notes = ExtractNotes(data, strumline, out int columns);
        return Calculate(notes, columns, clockRate);
    }

    /// <summary>(startMs, endMs, column) of every note of a strumline, in chart order.</summary>
    public static List<(double Start, double End, int Column)> ExtractNotes(FnmmData data, int strumline, out int columns)
    {
        var notes = new List<(double Start, double End, int Column)>();
        columns = 0;
        if (strumline < 0 || strumline >= data.Strumlines.Count) return notes;
        var lanes = data.Strumlines[strumline];
        columns = lanes.Count;
        for (int c = 0; c < columns; c++)
        {
            foreach (var n in lanes[c])
            {
                double start = data.ToSeconds(n.Start) * 1000.0;
                double end   = n.Duration > 0f ? start + data.ToSeconds(n.Duration) * 1000.0 : start;
                notes.Add((start, end, c));
            }
        }
        return notes;
    }

    /// <summary>Star rating of notes from <see cref="ExtractNotes"/> at <paramref name="clockRate"/>.</summary>
    public static double Calculate(IReadOnlyList<(double Start, double End, int Column)> source, int columns, double clockRate = 1.0)
    {
        if (columns == 0 || source.Count < 2) return 0;
        double rate = clockRate > 0 ? clockRate : 1.0;

        // Ordered like osu: by rounded start time. A faster rate squeezes every gap.
        var notes = source
            .Select(n => (Start: n.Start / rate, End: n.End / rate, n.Column))
            .OrderBy(n => (int)Math.Round(n.Start)).ThenBy(n => n.Column).ToList();

        // ── Difficulty hit objects (the first note produces none, as in osu) ──
        var objects = new List<Obj>(notes.Count - 1);
        var lastInColumn = new Obj?[columns];
        for (int i = 1; i < notes.Count; i++)
        {
            var o = new Obj
            {
                StartTime = notes[i].Start,
                EndTime   = notes[i].End,
                DeltaTime = notes[i].Start - notes[i - 1].Start,
                Column    = notes[i].Column,
                PreviousHitObjects = new Obj?[columns],
            };
            var prevInColumn = lastInColumn[o.Column];
            o.ColumnStrainTime = prevInColumn != null ? o.StartTime - prevInColumn.StartTime : o.StartTime;

            if (objects.Count > 0)
            {
                var prev = objects[^1];
                Array.Copy(prev.PreviousHitObjects, o.PreviousHitObjects, columns);
                o.PreviousHitObjects[prev.Column] = prev;
            }
            objects.Add(o);
            lastInColumn[o.Column] = o;
        }

        // ── Strain skill ─────────────────────────────────────────────────────
        var individualStrains = new double[columns];
        double highestIndividualStrain = 0;
        double overallStrain = 1;
        double currentStrain = 0;

        var peaks = new List<double>();
        double currentSectionPeak = 0;
        double currentSectionEnd = 0;

        for (int index = 0; index < objects.Count; index++)
        {
            var cur = objects[index];

            if (index == 0)
                currentSectionEnd = Math.Ceiling(cur.StartTime / SectionLength) * SectionLength;

            while (cur.StartTime > currentSectionEnd)
            {
                peaks.Add(currentSectionPeak);
                // Strain level at the start of the new section (Strain.CalculateInitialStrain)
                double sinceLast = currentSectionEnd - objects[index - 1].StartTime;
                currentSectionPeak = ApplyDecay(highestIndividualStrain, sinceLast, IndividualDecayBase)
                                   + ApplyDecay(overallStrain,           sinceLast, OverallDecayBase);
                currentSectionEnd += SectionLength;
            }

            // Strain.StrainValueOf (StrainDecayBase = 1, so CurrentStrain itself never decays)
            individualStrains[cur.Column] = ApplyDecay(individualStrains[cur.Column], cur.ColumnStrainTime, IndividualDecayBase);
            individualStrains[cur.Column] += IndividualStrain(cur);

            // Chords: take the hardest column so processing order doesn't matter
            highestIndividualStrain = cur.DeltaTime <= 1
                ? Math.Max(highestIndividualStrain, individualStrains[cur.Column])
                : individualStrains[cur.Column];

            overallStrain = ApplyDecay(overallStrain, cur.DeltaTime, OverallDecayBase);
            overallStrain += OverallStrain(cur);

            currentStrain += highestIndividualStrain + overallStrain - currentStrain;
            currentSectionPeak = Math.Max(currentStrain, currentSectionPeak);
        }
        peaks.Add(currentSectionPeak);

        // StrainSkill.DifficultyValue: weighted sum of section peaks, highest first
        double difficulty = 0, weight = 1;
        foreach (double strain in peaks.Where(p => p > 0).OrderByDescending(p => p))
        {
            difficulty += strain * weight;
            weight *= DecayWeight;
        }
        return difficulty * DifficultyMultiplier;
    }

    private static double IndividualStrain(Obj cur)
    {
        double holdFactor = 1.0;
        foreach (var prev in cur.PreviousHitObjects)
        {
            if (prev == null) continue;
            if (DefinitelyBigger(prev.EndTime, cur.EndTime, 1) && DefinitelyBigger(cur.StartTime, prev.StartTime, 1))
            {
                holdFactor = 1.25;
                break;
            }
        }
        return 2.0 * holdFactor;
    }

    private static double OverallStrain(Obj cur)
    {
        double startTime = cur.StartTime, endTime = cur.EndTime;
        bool isOverlapping = false;
        double closestEndTime = Math.Abs(endTime - startTime);
        double holdFactor = 1.0;
        double holdAddition = 0;

        foreach (var prev in cur.PreviousHitObjects)
        {
            if (prev == null) continue;

            isOverlapping |= DefinitelyBigger(prev.EndTime, startTime, 1) &&
                             DefinitelyBigger(endTime, prev.EndTime, 1) &&
                             DefinitelyBigger(startTime, prev.StartTime, 1);

            if (DefinitelyBigger(prev.EndTime, endTime, 1) && DefinitelyBigger(startTime, prev.StartTime, 1))
                holdFactor = 1.25;

            closestEndTime = Math.Min(closestEndTime, Math.Abs(endTime - prev.EndTime));
        }

        if (isOverlapping)
            holdAddition = Logistic(closestEndTime, midpointOffset: ReleaseThreshold, multiplier: 0.27);

        return (1 + holdAddition) * holdFactor;
    }

    private static double ApplyDecay(double value, double deltaTime, double decayBase)
        => value * Math.Pow(decayBase, deltaTime / 1000);

    // osu.Framework Precision.DefinitelyBigger
    private static bool DefinitelyBigger(double a, double b, double acceptableDifference)
        => a - acceptableDifference > b;

    // osu DiffUtils.Logistic
    private static double Logistic(double x, double midpointOffset, double multiplier, double maxValue = 1)
        => maxValue / (1 + Math.Exp(multiplier * (midpointOffset - x)));
}
