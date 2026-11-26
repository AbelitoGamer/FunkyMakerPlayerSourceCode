using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.Xna.Framework.Input;

namespace FunkyMakerPlayer;

/// <summary>Rebindable non-lane hotkeys (fork: Settings → CONTROLS).</summary>
public enum HotkeyAction
{
    VolumeUp,
    VolumeDown,
    ScrollSpeedDown,
    ScrollSpeedUp,
    ScrollSpeedDownBig,
    ScrollSpeedUpBig,
    Pause,
    TimeskipForward,
    TimeskipBackward,
    Retry,
    OpenChartEditor,
    PrevTab,
    NextTab,
    Refresh,
    NewChart,
    ImportLevel,
    ShareLevel,
    DeleteItem,
    ShowUpdatePrompt,
    FreeplaySearch,
    FreeplayFavorite,
    FreeplayFavoritesOnly,
    FreeplaySort,
    FreeplayRandom,
    FreeplayModifiers,
    FreeplayOptions,
    FreeplayAttempts,
    MultiplayerSettings,
    ResetOnlineSettings,
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
/// Rebindable hotkeys (fork). Each action has two slots (primary / alternate). A binding matches
/// when its key is pressed and its modifiers are held; extra held modifiers are allowed unless a
/// more specific binding on the same key also matches (so Shift+T wins over T, Ctrl+Right over Right).
/// </summary>
public static class Controls
{
    public sealed record Info(HotkeyAction Action, string Label, string Description, KeyBinding Primary, KeyBinding Alternate, string Group);

    // Section headers in Settings → CONTROLS (rows of a group are listed together, in this order)
    public const string GroupGlobal = "Anywhere";
    public const string GroupSong   = "In a song";
    public const string GroupMenus  = "Menus";
    public const string GroupOnline = "Online";

    public static readonly Info[] All =
    {
        new(HotkeyAction.ToggleFullscreen,    "Toggle Fullscreen",       "Switches between windowed and borderless fullscreen (desktop, anywhere outside text boxes).", new(Keys.Enter, Alt: true), new(Keys.F11), GroupGlobal),
        new(HotkeyAction.VolumeUp,           "Volume Up",               "Raises the master volume by 5% (anywhere outside text boxes and the Chart Editor).", new(Keys.OemPlus), new(Keys.Add), GroupGlobal),
        new(HotkeyAction.VolumeDown,          "Volume Down",             "Lowers the master volume by 5% (anywhere outside text boxes and the Chart Editor).", new(Keys.OemMinus), new(Keys.Subtract), GroupGlobal),
        new(HotkeyAction.ScrollSpeedDown,     "Scroll Speed -0.10",      "In a song (also paused): lowers the scroll speed by 0.10x.", new(Keys.F3), KeyBinding.None, GroupSong),
        new(HotkeyAction.ScrollSpeedUp,       "Scroll Speed +0.10",      "In a song (also paused): raises the scroll speed by 0.10x.", new(Keys.F4), KeyBinding.None, GroupSong),
        new(HotkeyAction.ScrollSpeedDownBig,  "Scroll Speed -0.25",      "While playing: lowers the scroll speed by 0.25x.", new(Keys.PageDown), KeyBinding.None, GroupSong),
        new(HotkeyAction.ScrollSpeedUpBig,    "Scroll Speed +0.25",      "While playing: raises the scroll speed by 0.25x.", new(Keys.PageUp), KeyBinding.None, GroupSong),
        new(HotkeyAction.Pause,               "Pause / Resume",          "Pauses or resumes the song (Esc always opens the pause menu too). Also dismisses error screens.", new(Keys.P), KeyBinding.None, GroupSong),
        new(HotkeyAction.TimeskipForward,     "Timeskip Forward",        "Skips forward by the Timeskip setting (hold to repeat). Needs Timeskip set above Off (GAMEPLAY tab); not in multiplayer. Ignored if it is a note lane key.", new(Keys.T), new(Keys.Right, Ctrl: true), GroupSong),
        new(HotkeyAction.TimeskipBackward,    "Timeskip Backward",       "Skips backward by the Timeskip setting (hold to repeat). Needs Timeskip set above Off (GAMEPLAY tab); not in multiplayer.", new(Keys.T, Shift: true), new(Keys.Left, Ctrl: true), GroupSong),
        new(HotkeyAction.Retry,               "Retry (Game Over)",       "Game over screen: restarts the song.", new(Keys.R), KeyBinding.None, GroupSong),
        new(HotkeyAction.OpenChartEditor,     "Open Chart Editor",       "In a song: opens the Chart Editor on it (or returns from a playtest). Freeplay list: edits the selected level. Main menu / Editor Hub: opens or closes the Editor Hub.", new(Keys.D7), KeyBinding.None, GroupSong),
        new(HotkeyAction.PrevTab,             "Previous Tab",            "Settings, online menu and replay browser: previous tab / category.", new(Keys.Q), KeyBinding.None, GroupMenus),
        new(HotkeyAction.NextTab,             "Next Tab",                "Settings, online menu and replay browser: next tab / category.", new(Keys.E), KeyBinding.None, GroupMenus),
        new(HotkeyAction.Refresh,             "Refresh",                 "Freeplay and online level lists: rescan levels. Online leaderboard: refresh.", new(Keys.R), new(Keys.F5), GroupMenus),
        new(HotkeyAction.NewChart,            "New Chart",               "Freeplay list: opens the New Chart wizard.", new(Keys.N), KeyBinding.None, GroupMenus),
        new(HotkeyAction.ImportLevel,         "Import Level",            "Freeplay and online level lists: import a level file.", new(Keys.I), new(Keys.O), GroupMenus),
        new(HotkeyAction.ShareLevel,          "Share Level",             "Online level list: shares the selected local level.", new(Keys.S), KeyBinding.None, GroupMenus),
        new(HotkeyAction.DeleteItem,          "Delete",                  "Freeplay / online level lists and replay browser: deletes the selected item (asks first).", new(Keys.Delete), KeyBinding.None, GroupMenus),
        new(HotkeyAction.ShowUpdatePrompt,    "Show Update Prompt",      "Main menu: re-opens a dismissed update prompt.", new(Keys.U), KeyBinding.None, GroupMenus),
        new(HotkeyAction.FreeplaySearch,      "Freeplay: Search",        "Freeplay list: type to filter the levels by name (Enter / Esc stop typing).", new(Keys.F, Ctrl: true), new(Keys.F3), GroupMenus),
        new(HotkeyAction.FreeplayFavorite,    "Freeplay: Favorite",      "Freeplay list: marks / unmarks the selected level as a favorite.", new(Keys.F), KeyBinding.None, GroupMenus),
        new(HotkeyAction.FreeplayFavoritesOnly, "Freeplay: Favorites Only", "Freeplay list: shows only favorite levels, or all of them again.", new(Keys.F, Shift: true), KeyBinding.None, GroupMenus),
        new(HotkeyAction.FreeplaySort,        "Freeplay: Sort",          "Freeplay list: next order (A-Z, stars, recently played, most played, newest).", new(Keys.Tab), KeyBinding.None, GroupMenus),
        new(HotkeyAction.FreeplayRandom,      "Freeplay: Random Level",  "Freeplay list: selects a random level of the list.", new(Keys.R, Shift: true), KeyBinding.None, GroupMenus),
        new(HotkeyAction.FreeplayModifiers,   "Freeplay: Modifiers",     "Freeplay list: opens the modifiers window (also from other screens later).", new(Keys.M), KeyBinding.None, GroupMenus),
        new(HotkeyAction.FreeplayOptions,     "Options Menu (...)",      "Lists with a \"...\" button (Freeplay levels, attempts, replays): opens the selected row's options. Right click a row does the same.", new(Keys.C), new(Keys.Apps), GroupMenus),
        new(HotkeyAction.FreeplayAttempts,    "Freeplay: Attempts",      "Freeplay list: every attempt at the selected level, with stats and replays.", new(Keys.H), KeyBinding.None, GroupMenus),
        new(HotkeyAction.MultiplayerSettings, "Online: Open Settings",   "Online menu: opens the game settings without leaving the online menu.", new(Keys.F1), KeyBinding.None, GroupOnline),
        new(HotkeyAction.ResetOnlineSettings, "Online: Reset Settings",  "Online settings screen: resets them to defaults.", new(Keys.R), KeyBinding.None, GroupOnline),
    };

    private static readonly Dictionary<HotkeyAction, KeyBinding[]> _bindings = new();

    static Controls() => ResetToDefaults();

    public static void ResetToDefaults()
    {
        foreach (var i in All)
            _bindings[i.Action] = new[] { i.Primary, i.Alternate };
    }

    public static KeyBinding Get(HotkeyAction action, int slot) => _bindings[action][slot];

    public static void Set(HotkeyAction action, int slot, KeyBinding binding) => _bindings[action][slot] = binding;

    /// <summary>"T / Ctrl+Right" style text for a settings row or description.</summary>
    public static string Describe(HotkeyAction action)
    {
        var b = _bindings[action];
        if (b[0].IsNone && b[1].IsNone) return "Unbound";
        if (b[1].IsNone) return b[0].ToString();
        if (b[0].IsNone) return b[1].ToString();
        return $"{b[0]} / {b[1]}";
    }

    /// <summary>Primary binding (or the alternate if there is none) for on-screen hints like "[R] REFRESH".</summary>
    public static string Hint(HotkeyAction action)
    {
        var b = _bindings[action];
        return (!b[0].IsNone ? b[0] : b[1]).ToString();
    }

    /// <summary>
    /// "[key] " in front of a button label, or nothing on touch screens (fork: phones showed
    /// keyboard keys on every button).
    /// </summary>
    public static string Tag(HotkeyAction action) => UI.PointerInput.IsTouch ? "" : $"[{Hint(action)}] ";

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
        foreach (var slots in _bindings.Values)
            foreach (var o in slots)
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
        => _bindings[action].Any(b => Matches(b, kb, ignoreKey));

    /// <summary>Action key went down this frame.</summary>
    public static bool Pressed(HotkeyAction action, KeyboardState kb, KeyboardState prev, Func<Keys, bool>? ignoreKey = null)
        => _bindings[action].Any(b => Matches(b, kb, ignoreKey) && !prev.IsKeyDown(b.Key));

    // ── Persistence ──────────────────────────────────────────────────────────

    /// <summary>action name → [primary, alternate] for settings.json.</summary>
    public static Dictionary<string, string[]> Export()
        => _bindings.ToDictionary(kv => kv.Key.ToString(), kv => kv.Value.Select(b => b.Serialize()).ToArray());

    public static void Import(Dictionary<string, string[]>? data)
    {
        ResetToDefaults();
        if (data == null) return;
        foreach (var (name, slots) in data)
        {
            if (!Enum.TryParse(name, out HotkeyAction action) || slots == null) continue;
            for (int i = 0; i < 2 && i < slots.Length; i++)
                _bindings[action][i] = KeyBinding.Parse(slots[i]);
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
