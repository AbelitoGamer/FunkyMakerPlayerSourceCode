using System;
using System.Collections.Generic;
using System.Linq;

namespace FunkyMakerPlayer;

/// <summary>Modifier categories (fork), in display order.</summary>
public enum ModifierCategory { DifficultyReduction, DifficultyIncrease, Automation, Fun }

/// <summary>
/// One play modifier: an on/off switch, optionally with a value (e.g. a rate) that can be changed
/// while it's on. Everything is read / written through delegates, so a modifier can be backed by
/// any setting.
/// </summary>
public sealed class PlayModifier
{
    public required string Id { get; init; }
    public required string Name { get; init; }
    public required string Description { get; init; }
    public required ModifierCategory Category { get; init; }
    public required Func<bool> IsOn { get; init; }
    public required Action<bool> SetOn { get; init; }

    /// <summary>Optional value (shown with -/+ buttons).</summary>
    public Func<float>? GetValue { get; init; }
    public Action<float>? SetValue { get; init; }
    public float Min { get; init; }
    public float Max { get; init; }
    public float Step { get; init; }
    public Func<float, string>? Format { get; init; }

    /// <summary>Why it can't apply to a level right now (null = it can). The argument is the level path.</summary>
    public Func<string?, string?>? Unavailable { get; init; }
    /// <summary>True when scores / replays aren't saved while this is on.</summary>
    public bool BlocksRecords { get; init; }
    /// <summary>Short chip text for summaries ("FASTER 1.25x").</summary>
    public string ShortLabel => GetValue != null && Format != null ? $"{Name.ToUpperInvariant()} {Format(GetValue())}" : Name.ToUpperInvariant();

    public bool HasValue => GetValue != null && SetValue != null;
}

/// <summary>
/// Registry of the play modifiers (fork). They are configured in <see cref="UI.ModifiersWindow"/>
/// (opened from Freeplay for now, built so any screen can open it) and applied in gameplay by
/// <c>Game1.ConfigureRunModifiers</c> (solo play only). Add new ones to <see cref="All"/> with
/// their category; empty categories are shown as "nothing here yet".
/// <list type="bullet">
/// <item>Slower / Faster: the song rate (<see cref="FreeplayData.SongRate"/>), one of them at a time;
/// each remembers its own rate (<see cref="FreeplayData.SlowRate"/>, <see cref="FreeplayData.FastRate"/>).</item>
/// <item>Autoplay: <see cref="NoteRenderer.Botplay"/> (also Settings → Practice).</item>
/// <item>Enemy Mode: play the opponent's chart.</item>
/// </list>
/// </summary>
public static class PlayModifiers
{
    public static readonly (ModifierCategory Category, string Title)[] Categories =
    {
        (ModifierCategory.DifficultyReduction, "Difficulty reduction"),
        (ModifierCategory.DifficultyIncrease, "Difficulty increase"),
        (ModifierCategory.Automation, "Automation"),
        (ModifierCategory.Fun, "Fun"),
    };

    /// <summary>Set by the owner screen: the level-specific availability check for Enemy Mode.</summary>
    public static Func<string?, bool>? HasEnemyChart { get; set; }

    /// <summary>Raised after any modifier changed (so lists can re-rate levels, etc.).</summary>
    public static event Action? Changed;

    private static FreeplayData Data => FreeplayData.Instance;
    private static string Rate(float r) => r.ToString("0.00", System.Globalization.CultureInfo.InvariantCulture) + "x";

    private static void Commit()
    {
        Data.Save();
        Changed?.Invoke();
    }

    public static readonly PlayModifier[] All =
    {
        new()
        {
            Id = "slower", Name = "Slower", Category = ModifierCategory.DifficultyReduction,
            Description = "Plays the song slower (and lower).",
            IsOn = () => Data.SongRate < 0.999f,
            SetOn = on => { Data.SongRate = on ? Data.SlowRate : 1f; Commit(); },
            GetValue = () => Data.SlowRate,
            SetValue = v => { Data.SlowRate = Math.Clamp(FreeplayData.ClampRate(v), 0.5f, 0.95f); if (Data.SongRate < 0.999f) Data.SongRate = Data.SlowRate; Commit(); },
            Min = 0.5f, Max = 0.95f, Step = FreeplayData.RateStep, Format = Rate,
            BlocksRecords = true,
        },
        new()
        {
            Id = "faster", Name = "Faster", Category = ModifierCategory.DifficultyIncrease,
            Description = "Plays the song faster (and higher).",
            IsOn = () => Data.SongRate > 1.001f,
            SetOn = on => { Data.SongRate = on ? Data.FastRate : 1f; Commit(); },
            GetValue = () => Data.FastRate,
            SetValue = v => { Data.FastRate = Math.Clamp(FreeplayData.ClampRate(v), 1.05f, 2f); if (Data.SongRate > 1.001f) Data.SongRate = Data.FastRate; Commit(); },
            Min = 1.05f, Max = 2f, Step = FreeplayData.RateStep, Format = Rate,
            BlocksRecords = true,
        },
        new()
        {
            Id = "autoplay", Name = "Autoplay", Category = ModifierCategory.Automation,
            Description = "The game plays every note for you.",
            IsOn = () => NoteRenderer.Botplay,
            SetOn = on => { NoteRenderer.Botplay = on; Game1.SaveSettings(); Changed?.Invoke(); },
            BlocksRecords = true,
        },
        new()
        {
            Id = "enemy", Name = "Enemy Mode", Category = ModifierCategory.Fun,
            Description = "Play the opponent's chart. The level's health events are ignored.",
            IsOn = () => Data.EnemyMode,
            SetOn = on => { Data.EnemyMode = on; Commit(); },
            Unavailable = path => path != null && HasEnemyChart?.Invoke(path) == false ? "This level has no enemy chart" : null,
        },
    };

    public static IEnumerable<PlayModifier> InCategory(ModifierCategory c) => All.Where(m => m.Category == c);

    public static IEnumerable<PlayModifier> Active => All.Where(m => m.IsOn());

    /// <summary>True when an active modifier stops scores / replays from being saved.</summary>
    public static bool RecordsBlocked => All.Any(m => m.BlocksRecords && m.IsOn());

    /// <summary>Turns every modifier off.</summary>
    public static void ResetAll()
    {
        foreach (var m in All.Where(m => m.IsOn())) m.SetOn(false);
    }
}
