using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.Xna.Framework.Input;

namespace FunkyMakerPlayer;

/// <summary>
/// Rebindable non-lane hotkeys (fork: Settings → CONTROLS). Only actions with no on-screen button
/// get a hotkey: anything a menu already shows as a button is used by clicking / selecting it.
/// </summary>
public enum HotkeyAction
{
    VolumeUp,
    VolumeDown,
    ScrollSpeedDown,
    ScrollSpeedUp,
    TimeskipForward,
    TimeskipBackward,
    OpenChartEditor,
    ToggleFullscreen,
}

/// <summary>One key plus the modifiers that must be held with it.</summary>
public readonly record struct KeyBinding(Keys Key, bool Ctrl = false, bool Shift = false, bool Alt = false)
{
    public static readonly KeyBinding None = new(Keys.None);
    public bool IsNone => Key == Keys.None;
    public int ModifierCount => (Ctrl ? 1 : 0) + (Shift ? 1 : 0) + (Alt ? 1 : 0);

    public override string ToString()
    {
        if (IsNone) return "-";
        string s = "";
        if (Ctrl) s += "Ctrl+";
        if (Shift) s += "Shift+";
        if (Alt) s += "Alt+";
        return s + Controls.KeyName(Key);
    }

    /// <summary>Saved form, e.g. "Ctrl+Shift+T" (enum names, parsed back by <see cref="Parse"/>).</summary>
    public string Serialize()
        => IsNone ? "None" : (Ctrl ? "Ctrl+" : "") + (Shift ? "Shift+" : "") + (Alt ? "Alt+" : "") + Key;

    public static KeyBinding Parse(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return None;
        var parts = text.Split('+', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        bool ctrl = false, shift = false, alt = false;
        Keys key = Keys.None;
        foreach (var p in parts)
        {
            if (p.Equals("Ctrl", StringComparison.OrdinalIgnoreCase)) ctrl = true;
            else if (p.Equals("Shift", StringComparison.OrdinalIgnoreCase)) shift = true;
            else if (p.Equals("Alt", StringComparison.OrdinalIgnoreCase)) alt = true;
            else if (Enum.TryParse(p, true, out Keys k)) key = k;
        }
        return key == Keys.None ? None : new KeyBinding(key, ctrl, shift, alt);
    }
}

/// <summary>
/// Rebindable hotkeys (fork). Each action has exactly one binding. A binding matches when its key is
/// pressed and its modifiers are held; extra held modifiers are allowed unless a more specific
/// binding on the same key also matches (so Shift+T wins over T).
/// </summary>
public static class Controls
{
    public sealed record Info(HotkeyAction Action, string Label, string Description, KeyBinding Default, string Group);

    // Section headers in Settings → CONTROLS (rows of a group are listed together, in this order)
    public const string GroupGlobal = "Anywhere";
    public const string GroupSong   = "During a song";

    public static readonly Info[] All =
    {
#if !ANDROID
        new(HotkeyAction.ToggleFullscreen, "Toggle Fullscreen", "Switches between window and fullscreen.", new(Keys.F11), GroupGlobal),
#endif
        new(HotkeyAction.VolumeUp,         "Volume Up",         "Turns the master volume up by 5%. Works in menus and during songs (not while typing or in the editors).", new(Keys.OemPlus), GroupGlobal),
        new(HotkeyAction.VolumeDown,       "Volume Down",       "Turns the master volume down by 5%. Works in menus and during songs (not while typing or in the editors).", new(Keys.OemMinus), GroupGlobal),
        new(HotkeyAction.ScrollSpeedDown,  "Slower Notes",      "Lowers the scroll speed by 0.10x without leaving the song (also while paused).", new(Keys.F3), GroupSong),
        new(HotkeyAction.ScrollSpeedUp,    "Faster Notes",      "Raises the scroll speed by 0.10x without leaving the song (also while paused).", new(Keys.F4), GroupSong),
        new(HotkeyAction.TimeskipForward,  "Skip Forward",      "Jumps ahead in the song by the Timeskip amount (GAMEPLAY tab). Hold it to keep skipping. Not in online matches.", new(Keys.T), GroupSong),
        new(HotkeyAction.TimeskipBackward, "Skip Backward",     "Jumps back in the song by the Timeskip amount (GAMEPLAY tab). Hold it to keep skipping. Not in online matches.", new(Keys.T, Shift: true), GroupSong),
        new(HotkeyAction.OpenChartEditor,  "Open Chart Editor", "Opens the song you are playing in the Chart Editor (or returns to it after a playtest). In the main menu it opens the editors.", new(Keys.D7), GroupSong),
    };

    private static readonly Dictionary<HotkeyAction, KeyBinding> _bindings = new();

    static Controls() => ResetToDefaults();

    public static void ResetToDefaults()
    {
        _bindings.Clear();
        foreach (var i in All)
            _bindings[i.Action] = i.Default;
    }

    public static KeyBinding Get(HotkeyAction action)
        => _bindings.TryGetValue(action, out var b) ? b : KeyBinding.None;

    public static void Set(HotkeyAction action, KeyBinding binding) => _bindings[action] = binding;

    /// <summary>"Shift+T" style text for a settings row or description ("Unbound" when none).</summary>
    public static string Describe(HotkeyAction action)
    {
        var b = Get(action);
        return b.IsNone ? "Unbound" : b.ToString();
    }

    /// <summary>The binding for on-screen hints like "ESC / 7: Back" ("-" when unbound).</summary>
    public static string Hint(HotkeyAction action) => Get(action).ToString();

    // ── Matching ─────────────────────────────────────────────────────────────

    private static bool ModsHeld(KeyBinding b, KeyboardState kb)
    {
        bool ctrl = kb.IsKeyDown(Keys.LeftControl) || kb.IsKeyDown(Keys.RightControl);
        bool shift = kb.IsKeyDown(Keys.LeftShift) || kb.IsKeyDown(Keys.RightShift);
        bool alt = kb.IsKeyDown(Keys.LeftAlt) || kb.IsKeyDown(Keys.RightAlt);
        return (!b.Ctrl || ctrl) && (!b.Shift || shift) && (!b.Alt || alt);
    }

    /// <summary>True when a more specific binding (any action) on the same key is also held.</summary>
    private static bool Shadowed(KeyBinding b, KeyboardState kb)
    {
        foreach (var o in _bindings.Values)
            if (!o.IsNone && o.Key == b.Key && o.ModifierCount > b.ModifierCount &&
                (!b.Ctrl || o.Ctrl) && (!b.Shift || o.Shift) && (!b.Alt || o.Alt) && ModsHeld(o, kb))
                return true;
        return false;
    }

    private static bool Matches(KeyBinding b, KeyboardState kb, Func<Keys, bool>? ignoreKey)
        => !b.IsNone && kb.IsKeyDown(b.Key) && ModsHeld(b, kb) && !Shadowed(b, kb) &&
           !(ignoreKey != null && b.ModifierCount == 0 && ignoreKey(b.Key));

    /// <summary>Action key held this frame.</summary>
    public static bool Down(HotkeyAction action, KeyboardState kb, Func<Keys, bool>? ignoreKey = null)
        => Matches(Get(action), kb, ignoreKey);

    /// <summary>Action key went down this frame.</summary>
    public static bool Pressed(HotkeyAction action, KeyboardState kb, KeyboardState prev, Func<Keys, bool>? ignoreKey = null)
    {
        var b = Get(action);
        return Matches(b, kb, ignoreKey) && !prev.IsKeyDown(b.Key);
    }

    // ── Persistence ──────────────────────────────────────────────────────────

    /// <summary>action name → [binding] for settings.json (an array: older versions saved two slots).</summary>
    public static Dictionary<string, string[]> Export()
        => _bindings.ToDictionary(kv => kv.Key.ToString(), kv => new[] { kv.Value.Serialize() });

    public static void Import(Dictionary<string, string[]>? data)
    {
        ResetToDefaults();
        if (data == null) return;
        foreach (var (name, slots) in data)
        {
            // Actions that no longer exist (or don't exist on this platform) are dropped.
            if (!Enum.TryParse(name, out HotkeyAction action) || !_bindings.ContainsKey(action) ||
                slots == null || slots.Length == 0) continue;
            // Older versions saved [primary, alternate] and a rebind cleared the alternate, so a filled
            // alternate means the player never changed it: keep the new default instead.
            if (slots.Length > 1 && !KeyBinding.Parse(slots[1]).IsNone) continue;
            _bindings[action] = KeyBinding.Parse(slots[0]);
        }
    }

    // ── Display names ────────────────────────────────────────────────────────

    public static string KeyName(Keys k) => LayoutKeyboard.DisplayName(k) ?? k switch
    {
        Keys.OemPlus => "=",
        Keys.OemMinus => "-",
        Keys.Add => "Num+",
        Keys.Subtract => "Num-",
        Keys.Multiply => "Num*",
        Keys.Divide => "Num/",
        Keys.OemOpenBrackets => "[",
        Keys.OemCloseBrackets => "]",
        Keys.OemComma => ",",
        Keys.OemPeriod => ".",
        Keys.OemQuestion => "/",
        Keys.OemSemicolon => ";",
        Keys.OemQuotes => "'",
        Keys.OemTilde => "`",
        Keys.OemPipe => "\\",
        Keys.PageUp => "PgUp",
        Keys.PageDown => "PgDn",
        >= Keys.D0 and <= Keys.D9 => ((int)k - (int)Keys.D0).ToString(),
        >= Keys.NumPad0 and <= Keys.NumPad9 => "Num" + ((int)k - (int)Keys.NumPad0),
        _ => k.ToString(),
    };

    /// <summary>
    /// <see cref="KeyName"/> limited to ASCII for the SpriteFont (32–126): accents are dropped
    /// (Ñ → N, Ç → C), anything else becomes '?'.
    /// </summary>
    public static string AsciiKeyName(Keys k)
    {
        var sb = new System.Text.StringBuilder();
        foreach (char c in KeyName(k).Normalize(System.Text.NormalizationForm.FormD))
        {
            if (System.Globalization.CharUnicodeInfo.GetUnicodeCategory(c) == System.Globalization.UnicodeCategory.NonSpacingMark) continue;
            sb.Append(c >= 32 && c <= 126 ? c : '?');
        }
        return sb.ToString();
    }

    public static bool IsModifier(Keys k) => k is Keys.LeftControl or Keys.RightControl or Keys.LeftShift
        or Keys.RightShift or Keys.LeftAlt or Keys.RightAlt or Keys.LeftWindows or Keys.RightWindows;
}
