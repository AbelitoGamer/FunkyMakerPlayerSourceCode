using System;
using System.Collections.Generic;

namespace FunkyMakerPlayer;

/// <summary>
/// FunkyMaker note skins and note-effect analysis.
///
/// Skins are <b>purely visual</b>:
///   0 = default note, 1 = negative note (X), 2 = positive note (O).
/// There is no skin 3; any value other than 1 or 2 is drawn as the default note.
///
/// Gameplay effects (damage, instakill, healing…) come <b>only</b> from the note's
/// <c>onPressEvent</c> / <c>onMissEvent</c> action packs (-1 = no event). The player
/// never applies a hardcoded effect based on the skin.
/// </summary>
public static class NoteSkins
{
    /// <summary>
    /// FunkyMaker health scale: 30 = 100% of the bar. <c>PlayerHealth</c> values and
    /// <c>minHealth</c> in levels use this scale; the engine stores health as 0..1.
    /// </summary>
    public const float FmmMaxHealth = 30f;

    public const int Default  = 0;
    public const int Negative = 1;
    public const int Positive = 2;

    /// <summary>Maps any raw skin value to 0, 1 or 2 (unknown values → <see cref="Default"/>).</summary>
    public static int Normalize(float skin)
    {
        int s = (int)MathF.Round(skin);
        return s == Negative || s == Positive ? s : Default;
    }

    public static string GetName(float skin) => Normalize(skin) switch
    {
        Negative => "Negative",
        Positive => "Positive",
        _        => "Default",
    };

    /// <summary>
    /// Net player-health effect of an action pack, as a fraction of the full bar
    /// (FMM values / <see cref="FmmMaxHealth"/>).
    /// <c>add</c> = +value, <c>remove</c> = −value, <c>set</c> = value − half bar (relative to
    /// the default half-full bar, so "set 0" is strongly negative and "set 30" strongly positive).
    /// Nested <c>RunActionPack</c> actions are followed (cycles are ignored).
    /// Returns 0 for -1 / out-of-range indices or packs without <c>PlayerHealth</c> actions.
    /// </summary>
    public static float GetPackHealthEffect(FnmmEvents? events, int packIndex)
        => GetPackHealthEffect(events, packIndex, new HashSet<int>());

    private static float GetPackHealthEffect(FnmmEvents? events, int packIndex, HashSet<int> visited)
    {
        if (events == null || packIndex < 0 || packIndex >= events.ActionPacks.Count) return 0f;
        if (!visited.Add(packIndex)) return 0f;

        float net = 0f;
        foreach (var action in events.ActionPacks[packIndex].Actions)
        {
            if (action.Type == "PlayerHealth")
            {
                float val = (action.Props.ValueFloat ?? 0f) / FmmMaxHealth;
                net += (action.Props.Operation ?? "remove") switch
                {
                    "add" => val,
                    "set" => val - 0.5f,
                    _     => -val,
                };
            }
            else if (action.Type == "RunActionPack")
            {
                net += GetPackHealthEffect(events, (int)(action.Props.ActionPack ?? -1f), visited);
            }
        }
        return net;
    }

    /// <summary>Health effect of pressing the note (its onPressEvent pack).</summary>
    public static float GetPressEffect(FnmmEvents? events, NoteProps props)
        => GetPackHealthEffect(events, (int)props.OnPressEvent);

    /// <summary>Health effect of letting the note pass (its onMissEvent pack).</summary>
    public static float GetMissEffect(FnmmEvents? events, NoteProps props)
        => GetPackHealthEffect(events, (int)props.OnMissEvent);

    /// <summary>
    /// True when the note is meant to be avoided: letting it pass is better for the player
    /// than pressing it (e.g. pressing damages / kills, or missing heals).
    /// Such notes do not count as a miss when they pass, and Botplay skips them.
    /// Notes without events (or with equal effects) are never "avoid" notes, whatever their skin.
    /// </summary>
    public static bool ShouldAvoid(FnmmEvents? events, NoteProps props)
        => GetMissEffect(events, props) > GetPressEffect(events, props);

    /// <summary>
    /// True when letting the note pass must not count as a miss (no miss count, no combo
    /// break, no default miss health penalty). Applies to avoid-notes and to every note that
    /// heals when pressed (optional bonus notes). The note's onMissEvent still fires.
    /// </summary>
    public static bool IsMissFree(FnmmEvents? events, NoteProps props)
        => ShouldAvoid(events, props) || GetPressEffect(events, props) > 0f;
}
