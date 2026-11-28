using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using Microsoft.Xna.Framework.Input;

namespace FunkyMakerPlayer.Managers;

/// <summary>
/// Settings Manager with tabbed interface (Codename Engine style).
/// Tabs: GAMEPLAY, APPEARANCE, HUD, AUDIO, VIDEO, osu!, CONTROLS. Rows can be grouped under
/// non-selectable section headers. A search box (type anywhere on desktop, tap on touch) filters
/// every tab at once. Supports focus-based keyboard adjustment and full mouse interaction.
/// </summary>
public class SettingsManager
{
    public enum SettingKind
    {
        Toggle,
        Action,
        Adjustable,
        /// <summary>Section title: drawn differently and skipped by navigation.</summary>
        Header,
    }

    private sealed class SettingItem
    {
        public string Label { get; init; } = "";
        public string Value { get; init; } = "";
        public string Description { get; init; } = "";
        public SettingKind Kind { get; init; } = SettingKind.Toggle;
        public Func<SettingsActionResult>? OnSelect { get; init; }
        public Func<bool, float, SettingsActionResult>? OnAdjust { get; init; }
        /// <summary>Set on rows that can't be changed right now: why, and what to do (drawn dimmed, does nothing).</summary>
        public string? LockedReason { get; init; }
    }

    private readonly struct RowHitbox
    {
        public int Index { get; init; }
        public Rectangle RowRect { get; init; }
        public Rectangle? LeftArrowRect { get; init; }
        public Rectangle? RightArrowRect { get; init; }
    }

    // ── State ────────────────────────────────────────────────────────────────
    private bool _isActive;
    public bool IsActive
    {
        get => _isActive;
        set
        {
            if (_isActive && !value) OnClosed();
            _isActive = value;
        }
    }
    public int Selection { get; set; } = 0;
    public int CurrentTab { get; set; } = 0;  // see the *Tab constants below
    public bool IsFocused { get; set; } = false;
    public float ScrollOffset { get; set; } = 0f;

    private static readonly string[] TabNames = { "GAMEPLAY", "APPEARANCE", "HUD", "AUDIO", "VIDEO", "osu!", "CONTROLS" };
    private const int GameplayTab = 0;
    private const int AppearanceTab = 1;
    private const int HudTab = 2;
    private const int AudioTab = 3;
    private const int VideoTab = 4;
    private const int OsuTab = 5;
    private const int ControlsTab = 6;

    private static readonly Color Accent = new(0xFF, 0x44, 0x88);
    private static readonly Color FocusAccent = new(255, 200, 60);
    private static readonly Color LockedColor = new(255, 150, 90);

    // Scroll & mouse hit-test state (updated during Draw)
    private bool _followSelectionScroll = true;
    private int _lastItemCount = 8;
    private readonly Rectangle[] _tabRects = new Rectangle[TabNames.Length];
    private Rectangle _backBtnRect = Rectangle.Empty;
    private bool _backBtnHovered = false;
    private int _hoveredTab = -1;
    private int _hoveredRow = -1;
    private int _hoveredArrow = 0; // -1 = '<', +1 = '>'
    private Point _lastMousePos = new(-9999, -9999);
    private long _arrowHoldStart, _arrowNextRepeat; // touch hold-to-repeat on < > (fork)
    private readonly List<RowHitbox> _rowHitboxes = new();

    // ── Initialization ───────────────────────────────────────────────────────

    public SettingsManager()
    {
        Console.WriteLine("[SettingsManager] Initialized with tabs");
    }

    public void Reset()
    {
        IsActive = false;
        IsFocused = false;
        Selection = 0;
        CurrentTab = 0;
        ScrollOffset = 0f;
        _followSelectionScroll = true;
        _hoveredTab = -1;
        _hoveredRow = -1;
        _hoveredArrow = 0;
        Console.WriteLine("[SettingsManager] Reset");
    }

    /// <summary>Settings just closed: stop typing into the search box and forget the search.</summary>
    private void OnClosed()
    {
        IsFocused = false;
        _captureAction = null;
        _search = "";
        ReleaseTextEntry();
    }

    public void Unfocus()
    {
        IsFocused = false;
    }

    public void NotifySelectionMoved()
    {
        _followSelectionScroll = true;
    }

    /// <summary>
    /// ESC inside the settings: releases a focused value first, then clears the search.
    /// Returns true when it used the key (otherwise ESC closes the settings).
    /// </summary>
    public bool HandleEscape()
    {
        if (IsFocused)
        {
            IsFocused = false;
            return true;
        }
        if (IsSearching)
        {
            SetSearch("");
            UI.UiSfx.Play(UI.UiSound.Back);
            return true;
        }
        return false;
    }

    // ── Tab Navigation ───────────────────────────────────────────────────────

    public void NextTab() => SetTab((CurrentTab + 1) % TabNames.Length);

    public void PrevTab() => SetTab((CurrentTab - 1 + TabNames.Length) % TabNames.Length);

    public void SetTab(int tabIndex)
    {
        if (tabIndex < 0 || tabIndex >= TabNames.Length) return;
        if (IsSearching) _search = "";   // picking a tab leaves the search results
        IsFocused = false;
        CurrentTab = tabIndex;
        Selection = 0;
        ScrollOffset = 0f;
        _followSelectionScroll = true;
    }

    // ── Search ───────────────────────────────────────────────────────────────

    private string _search = "";
    private bool _ownsTextEntry;
    private volatile string? _pendingTouchSearch;
    private float _searchBackHold, _searchBackRepeat;
    private Rectangle _searchBoxRect = Rectangle.Empty;
    private Rectangle _searchClearRect = Rectangle.Empty;
    private float _caretTime;

    /// <summary>True while the list shows search results instead of a tab.</summary>
    public bool IsSearching => _search.Length > 0;

    private void SetSearch(string text)
    {
        text = text.Length > 40 ? text[..40] : text;
        if (text == _search) return;
        _search = text;
        IsFocused = false;
        Selection = 0;
        ScrollOffset = 0f;
        _followSelectionScroll = true;
    }

    private void ReleaseTextEntry()
    {
        if (!_ownsTextEntry) return;
        _ownsTextEntry = false;
        UI.TextEntry.Capturing = false;
        UI.TextEntry.Clear();
    }

    /// <summary>
    /// Typing into the search box (call every frame while the settings are open, before the
    /// navigation keys). Desktop: letters and digits typed anywhere go to the search; Space only
    /// once something is typed (before that it still selects). Backspace deletes, Ctrl+Backspace
    /// clears, ESC (see <see cref="HandleEscape"/>) clears.
    /// </summary>
    public void UpdateSearch(KeyboardState kb, KeyboardState prev, float dt)
    {
        _caretTime += dt;
        if (_pendingTouchSearch is { } typedOnPhone)
        {
            _pendingTouchSearch = null;
            SetSearch(typedOnPhone.Trim());
        }

#if !ANDROID
        if (_captureAction != null)
        {
            if (_ownsTextEntry) UI.TextEntry.Clear();  // rebinding a key: nothing goes to the search
            return;
        }
        if (!UI.TextEntry.Capturing)
        {
            UI.TextEntry.Clear();
            UI.TextEntry.Capturing = true;
            _ownsTextEntry = true;
        }

        string text = _search;
        foreach (char c in UI.TextEntry.Drain())
        {
            if (char.IsLetterOrDigit(c) || (c == ' ' && text.Length > 0 && !text.EndsWith(' ')) || c is '!' or '&' or '/' or '.')
                text += c;
        }

        if (kb.IsKeyDown(Keys.Back) && text.Length > 0)
        {
            bool fresh = !prev.IsKeyDown(Keys.Back);
            if (fresh) { _searchBackHold = 0f; _searchBackRepeat = 0f; }
            else _searchBackHold += dt;
            bool ctrl = kb.IsKeyDown(Keys.LeftControl) || kb.IsKeyDown(Keys.RightControl);
            if (fresh && ctrl) text = "";
            else if (fresh || (_searchBackHold >= 0.4f && (_searchBackRepeat += dt) >= 0.04f))
            {
                _searchBackRepeat = 0f;
                text = text[..^1];
            }
        }

        if (text != _search)
        {
            _caretTime = 0f;
            SetSearch(text);
        }
#endif
    }

    private void OpenTouchSearchPrompt()
    {
#if ANDROID
        UI.TextEntry.PromptAsync("Search settings", "", _search, false).ContinueWith(t =>
        {
            if (t.IsCompletedSuccessfully && t.Result != null) _pendingTouchSearch = t.Result;
        });
#endif
    }

    /// <summary>Lower case without accents, so "Música" matches "musica".</summary>
    private static string Fold(string s)
    {
        var sb = new StringBuilder(s.Length);
        foreach (char c in s.ToLowerInvariant().Normalize(NormalizationForm.FormD))
            if (CharUnicodeInfo.GetUnicodeCategory(c) != UnicodeCategory.NonSpacingMark)
                sb.Append(c);
        return sb.ToString();
    }

    /// <summary>Every row of every tab whose name, section or description contains all the typed words.</summary>
    private void BuildSearchResults(Game1? game, List<SettingItem> entries)
    {
        string[] terms = Fold(_search).Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var tabRows = new List<SettingItem>();
        for (int tab = 0; tab < TabNames.Length; tab++)
        {
            tabRows.Clear();
            // Rows hidden by another option are listed too, locked, with the reason
            _includeLocked = true;
            try { BuildEntriesFor(tab, game, tabRows); }
            finally { _includeLocked = false; _lockedBy = null; }
            string section = "";
            string? shownGroup = null;
            foreach (var item in tabRows)
            {
                if (item.Kind == SettingKind.Header)
                {
                    section = item.Label;
                    continue;
                }
                string label = item.Label.Trim();
                string hay = Fold($"{label} {section} {TabNames[tab]} {item.Description}");
                bool all = true;
                foreach (var t in terms)
                    if (!hay.Contains(t)) { all = false; break; }
                if (!all) continue;

                string group = section.Length > 0 ? $"{TabNames[tab]}  -  {section}" : TabNames[tab];
                if (group != shownGroup)
                {
                    entries.Add(Header(group));
                    shownGroup = group;
                }
                entries.Add(new SettingItem
                {
                    Label = label,
                    Value = item.Value,
                    Description = item.Description,
                    Kind = item.Kind,
                    OnSelect = item.OnSelect,
                    OnAdjust = item.OnAdjust,
                    LockedReason = item.LockedReason,
                });
            }
        }
    }

    // ── Unified Data-Driven Tab Definitions ──────────────────────────────────

    private List<SettingItem> BuildTabEntries(Game1? game)
    {
        var entries = new List<SettingItem>();
        if (IsSearching) BuildSearchResults(game, entries);
        else BuildEntriesFor(CurrentTab, game, entries);

        _lastItemCount = Math.Max(1, entries.Count);
        if (entries.Count > 0 && Selection >= entries.Count)
            Selection = entries.Count - 1;
        else if (Selection < 0)
            Selection = 0;

        // Section headers can't be selected: land on the nearest real row
        if (entries.Count > 0 && !IsSelectable(entries[Selection]))
        {
            int down = Selection;
            while (down < entries.Count && !IsSelectable(entries[down])) down++;
            if (down < entries.Count) Selection = down;
            else
            {
                int up = Selection;
                while (up >= 0 && !IsSelectable(entries[up])) up--;
                Selection = Math.Max(0, up);
            }
        }

        return entries;
    }

    private void BuildEntriesFor(int tab, Game1? game, List<SettingItem> entries)
    {
        switch (tab)
        {
            case GameplayTab: BuildGameplayEntries(game, entries); break;
            case AppearanceTab: BuildAppearanceEntries(entries); break;
            case HudTab: BuildHudEntries(entries); break;
            case AudioTab: BuildAudioEntries(entries); break;
            case VideoTab: BuildVideoEntries(game, entries); break;
            case OsuTab: BuildOsuEntries(entries); break;
            case ControlsTab: BuildControlsEntries(entries); break;
        }
    }

    private static bool IsSelectable(SettingItem item) => item.Kind != SettingKind.Header;

    /// <summary>Moves the selection up (-1) or down (+1), skipping section headers and wrapping around.</summary>
    public void MoveSelection(Game1 game, int direction)
    {
        var entries = BuildTabEntries(game);
        if (entries.Count == 0) return;
        int idx = Selection;
        for (int n = 0; n < entries.Count; n++)
        {
            idx = (idx + direction + entries.Count) % entries.Count;
            if (IsSelectable(entries[idx]))
            {
                Selection = idx;
                return;
            }
        }
    }

    public int GetItemCount() => _lastItemCount;

    public int GetItemCount(Game1 game) => BuildTabEntries(game).Count;

    /// <summary>True while a multiplayer match is running: unfair options are locked.</summary>
    private static bool InMultiplayerMatch => Game1.Instance?.IsMultiplayerMatch == true;

    /// <summary>A row that shows its state but can't be changed during a multiplayer match.</summary>
    private static SettingItem LockedInMatch(string label, string value) => new SettingItem
    {
        Label = label,
        Value = value,
        Kind = SettingKind.Action,
        LockedReason = "Can't be changed during an online match.",
    };

    // ── Rows that depend on another option ──────────────────────────────────
    // Normally a dependent row is simply not listed while its condition is off. While searching,
    // it is listed anyway but locked, with the reason (so searching "effect type" finds it).

    private bool _includeLocked;     // true while building the search results
    private string? _lockedBy;       // reason applied to the rows added right now (outermost failing condition)

    /// <summary>
    /// Starts a block of rows that need <paramref name="condition"/>. Returns false when the block must
    /// be skipped; otherwise build it and call <see cref="EndIf"/> with <paramref name="saved"/>.
    /// </summary>
    private bool BeginIf(bool condition, string reason, out string? saved)
    {
        saved = _lockedBy;
        if (condition) return true;
        if (!_includeLocked) return false;
        _lockedBy ??= reason;
        return true;
    }

    private void EndIf(string? saved) => _lockedBy = saved;

    /// <summary>Adds a row, locked when it is inside a block whose condition is off.</summary>
    private void Add(List<SettingItem> entries, SettingItem item)
    {
        if (_lockedBy == null || item.Kind == SettingKind.Header || item.LockedReason != null)
        {
            entries.Add(item);
            return;
        }
        entries.Add(new SettingItem
        {
            Label = item.Label,
            Value = item.Value,
            Description = item.Description,
            Kind = SettingKind.Action,
            LockedReason = _lockedBy,
        });
    }

    private static string NeedsOn(string option, string tab) => $"Locked: turn on {option} ({tab} tab) to change this.";

    /// <summary>A non-selectable section title inside a tab.</summary>
    private static SettingItem Header(string title) => new SettingItem
    {
        Label = title.ToUpperInvariant(),
        Kind = SettingKind.Header,
    };

    private static string OnOff(bool on) => on ? "ON" : "OFF";

    private static string OffsetText(int ms) => ms == 0 ? "0 ms" : ms > 0 ? $"+{ms} ms" : $"{ms} ms";

    private static string Percent(float v) => $"{(int)Math.Round(v * 100)}%";

    /// <summary>A plain ON / OFF row.</summary>
    private static SettingItem Toggle(string label, bool value, string description, Action toggle,
                                      Func<SettingsActionResult>? result = null) => new()
    {
        Label = label,
        Value = OnOff(value),
        Kind = SettingKind.Toggle,
        Description = description,
        OnSelect = () => { toggle(); return result?.Invoke() ?? new SettingsActionResult { NeedsSave = true }; }
    };

    /// <summary>A 0-100% row changed in 5% steps.</summary>
    private static SettingItem PercentRow(string label, float value, string description, Action<float> set,
                                          Func<SettingsActionResult>? result = null) => new()
    {
        Label = label,
        Value = Percent(value),
        Kind = SettingKind.Adjustable,
        Description = description,
        OnAdjust = (_, delta) =>
        {
            set(MathF.Round(Math.Clamp(value + delta * 0.05f, 0f, 1f), 2));
            return result?.Invoke() ?? new SettingsActionResult { NeedsSave = true };
        }
    };

    /// <summary>A row that opens a picker / another screen.</summary>
    private SettingItem OpenRow(string label, string value, string description, Func<SettingsActionResult> open) => new()
    {
        Label = label,
        Value = value,
        Kind = SettingKind.Action,
        Description = description,
        OnSelect = () => { IsActive = false; return open(); }
    };

    // ── GAMEPLAY ─────────────────────────────────────────────────────────────

    private void BuildGameplayEntries(Game1? game, List<SettingItem> entries)
    {
        Add(entries, Header("Scrolling"));

        Add(entries, Toggle("Downscroll", NoteRenderer.Downscroll,
            "Your arrows sit at the bottom of the screen and the notes fall down to them. OFF: the arrows are at the top and the notes rise.",
            () => NoteRenderer.Downscroll = !NoteRenderer.Downscroll));

        Add(entries, Toggle("Middlescroll", NoteRenderer.Midlescroll,
            "Puts your arrows in the middle of the screen. The opponent's arrows are split to both sides of yours.",
            () => NoteRenderer.Midlescroll = !NoteRenderer.Midlescroll,
            () => new SettingsActionResult { NeedsStrumlineReposition = true, NeedsSave = true }));

        Add(entries, new SettingItem
        {
            Label = "Scroll Speed",
            Value = $"{NoteRenderer.ScrollMultiplier:F2}x",
            Kind = SettingKind.Adjustable,
            Description = $"How fast the notes move (1.00x = the speed the chart was made with). Faster notes are spread out and easier to read, but you see them for less time. During a song: {Controls.Describe(HotkeyAction.ScrollSpeedDown)} / {Controls.Describe(HotkeyAction.ScrollSpeedUp)}.",
            OnAdjust = (_, delta) =>
            {
                NoteRenderer.ScrollMultiplier = MathF.Round(
                    Math.Clamp(NoteRenderer.ScrollMultiplier + delta * 0.10f, 0.25f, 5.0f), 2);
                return new SettingsActionResult { NeedsSave = true };
            }
        });

        Add(entries, Header("Input"));

        Add(entries, Toggle("Ghost Tapping", NoteRenderer.GhostTapping,
            "ON: pressing a key when there is no note to hit does nothing. OFF: every press that doesn't hit a note counts as a miss.",
            () => NoteRenderer.GhostTapping = !NoteRenderer.GhostTapping));

        Add(entries, Header("Practice"));

        if (InMultiplayerMatch)
            Add(entries, LockedInMatch("Botplay", "OFF"));
        else
            Add(entries, Toggle("Botplay", NoteRenderer.Botplay,
                "The game plays every note for you, to watch a chart or test a level. Scores and personal bests are not saved. Same as the Autoplay modifier in Freeplay.",
                () => NoteRenderer.Botplay = !NoteRenderer.Botplay));

        if (InMultiplayerMatch)
        {
            Add(entries, LockedInMatch("Timeskip", "OFF"));
            return;
        }

        Add(entries, new SettingItem
        {
            Label = "Timeskip",
            Value = Game1.TimeskipSeconds <= 0f ? "OFF" : $"{Game1.TimeskipSeconds:F0}s",
            Kind = SettingKind.Adjustable,
            Description = $"How many seconds {Controls.Describe(HotkeyAction.TimeskipForward)} (forward) and {Controls.Describe(HotkeyAction.TimeskipBackward)} (back) jump during a song. OFF turns those keys off. A run where you skip doesn't count as a personal best.",
            OnAdjust = (_, delta) =>
            {
                Game1.TimeskipSeconds = MathF.Round(Math.Clamp(Game1.TimeskipSeconds + delta * 5f, 0f, 60f), 0);
                return new SettingsActionResult { NeedsSave = true };
            }
        });

        string jumpReason = !Game1.TimeskipEnabled
            ? "Locked: set Timeskip (just above) to a number of seconds to use this."
            : "Locked: only while a song is loaded (open Settings from the pause menu).";
        if (BeginIf(Game1.TimeskipEnabled && game?._level != null, jumpReason, out var sJump))
        {
            Add(entries, new SettingItem
            {
                Label = "  Jump in This Song",
                Value = "< >",
                Kind = SettingKind.Adjustable,
                Description = "Moves the song you are playing back (<) or forward (>) by the Timeskip amount. The song stays paused.",
                OnAdjust = (_, delta) => new SettingsActionResult
                {
                    SeekSong = delta * Game1.TimeskipSeconds,
                    NeedsSave = false
                }
            });
            EndIf(sJump);
        }
    }

    // ── APPEARANCE ───────────────────────────────────────────────────────────

    private void BuildAppearanceEntries(List<SettingItem> entries)
    {
        Add(entries, Header("Style"));

        Add(entries, OpenRow("Note Style", Game1.NoteStyleName,
            "The look of your notes and arrows. Opens the list of installed styles (add your own in mods/notestyles/).",
            () => new SettingsActionResult { OpenArrowsMenu = true }));

        Add(entries, OpenRow("Note Splash", Game1.NoteSplashStyleName,
            "The burst that appears on your arrow when you hit a note perfectly (Sick!). Add your own in mods/notesplashes/.",
            () => new SettingsActionResult { OpenSplashMenu = true }));

        Add(entries, OpenRow("Countdown Style", Game1.SelectedCountdown,
            "The 3, 2, 1, GO! shown before a song starts. Add your own in mods/countdowns/.",
            () => new SettingsActionResult { OpenCountdownMenu = true }));

        Add(entries, Header("Readability"));

        Add(entries, new SettingItem
        {
            Label = "Lane Underlay",
            Value = NoteRenderer.LaneUnderlay <= 0f ? "OFF" : Percent(NoteRenderer.LaneUnderlay),
            Kind = SettingKind.Adjustable,
            Description = "A dark strip behind your arrows so the notes stand out over busy backgrounds. The higher, the darker (up to 75%).",
            OnAdjust = (_, delta) =>
            {
                NoteRenderer.LaneUnderlay = MathF.Round(
                    Math.Clamp(NoteRenderer.LaneUnderlay + delta * 0.05f, 0f, NoteRenderer.LaneUnderlayMax), 2);
                return new SettingsActionResult { NeedsSave = true };
            }
        });

        Add(entries, Toggle("Show Key Labels", NoteRenderer.ShowKeyLabels,
            "Writes the key to press on each of your arrows. Hidden while Botplay is on.",
            () => NoteRenderer.ShowKeyLabels = !NoteRenderer.ShowKeyLabels));

        Add(entries, Header("Effects"));

        Add(entries, Toggle("Arrow Effects", ArrowEffectManager.Enabled,
            "Your arrows move to the beat of the music. Only for looks: it doesn't change the gameplay.",
            () => ArrowEffectManager.Enabled = !ArrowEffectManager.Enabled));

        if (BeginIf(ArrowEffectManager.Enabled, NeedsOn("Arrow Effects", "APPEARANCE"), out var sFx))
        {
            Add(entries, new SettingItem
            {
                Label = "  Effect Type",
                Value = ArrowEffectManager.GetSelectedEffectName(),
                Kind = SettingKind.Adjustable,
                Description = "Which movement the arrows do: Random, Glow, Vertical Bounce, Spin 360, Horizontal Bounce or Dizzy.",
                OnAdjust = (isLeft, _) =>
                {
                    int newEffect = ArrowEffectManager.SelectedEffect + (isLeft ? -1 : 1);
                    ArrowEffectManager.SelectedEffect = (newEffect + 6) % 6;
                    return new SettingsActionResult { NeedsSave = true };
                }
            });
            EndIf(sFx);
        }
    }

    // ── HUD ──────────────────────────────────────────────────────────────────

    // "Legacy" drew exactly like "HUD Editor" (both use the HUD Editor's place), so it is no longer offered.
    private static readonly string[] TimerPresets = { "HUD Editor", "Top", "Middle", "Left", "Right", "Strumline" };
    private static readonly string[] TimerTextModes = { "Time Left", "Time Elapsed", "Song Name", "Song Name + Difficulty", "None" };

    private void BuildHudEntries(List<SettingItem> entries)
    {
        var hud = HudSettings.Instance;

        #if !ANDROID
        Add(entries, OpenRow("HUD Editor", "[Open]",
            "Opens the HUD Editor, where you drag, resize and hide each part of the HUD with the mouse.",
            () => new SettingsActionResult { OpenHudEditor = true }));
        #endif

        Add(entries, new SettingItem
        {
            Label = "Show HUD",
            Value = OnOff(hud.MasterHudVisible),
            Kind = SettingKind.Toggle,
            Description = "Shows or hides everything drawn over the song at once: arrows, notes, health bar, score, timer... Only the stage and the characters stay.",
            OnSelect = () =>
            {
                hud.MasterHudVisible = !hud.MasterHudVisible;
                hud.Save();
                return new SettingsActionResult { NeedsSave = true };
            }
        });

        // Everything up to the Debug section needs Show HUD
        bool hudShown = BeginIf(hud.MasterHudVisible, NeedsOn("Show HUD", "HUD"), out var sHud);
        if (hudShown) {

        // ── Strumlines ──
        Add(entries, Header("Arrows & Notes"));

        Add(entries, Toggle("Strumlines", NoteRenderer.ShowStrumlines,
            "Shows or hides every set of arrows and notes at once: yours, the opponent's and any extra ones.",
            () => NoteRenderer.ShowStrumlines = !NoteRenderer.ShowStrumlines,
            () => new SettingsActionResult { NeedsStrumlineAlphaUpdate = true, NeedsSave = true }));

        if (BeginIf(NoteRenderer.ShowStrumlines, NeedsOn("Strumlines", "HUD"), out var sStrum))
        {
            Add(entries, Toggle("  Your Arrows", !NoteRenderer.HidePlayerStrumline,
                "Shows or hides your own arrows, notes and splashes. You can still play with them hidden.",
                () => NoteRenderer.HidePlayerStrumline = !NoteRenderer.HidePlayerStrumline,
                () => new SettingsActionResult { NeedsStrumlineAlphaUpdate = true, NeedsSave = true }));

            Add(entries, Toggle("  Opponent's Arrows", !Game1.HideOpponentStrumline,
                "Shows or hides the opponent's arrows and notes.",
                () => Game1.HideOpponentStrumline = !Game1.HideOpponentStrumline,
                () => new SettingsActionResult { NeedsOpponentAlphaUpdate = true, NeedsSave = true }));

            if (BeginIf(!Game1.HideOpponentStrumline, NeedsOn("Opponent's Arrows", "HUD"), out var sOpp))
            {
                var mp = Multiplayer.MultiplayerSettings.Instance;
                Add(entries, new SettingItem
                {
                    Label = "    Opponent Opacity (Online)",
                    Value = $"{mp.GhostTransparency}%",
                    Kind = SettingKind.Adjustable,
                    Description = "How solid the opponent's arrows and notes look in online matches (100% = fully visible). Solo songs always show them fully.",
                    OnAdjust = (_, delta) =>
                    {
                        mp.GhostTransparency = Math.Clamp(mp.GhostTransparency + (int)MathF.Round(delta * 10f), 0, 100);
                        mp.Save();
                        return new SettingsActionResult { NeedsOpponentAlphaUpdate = true };
                    }
                });
                EndIf(sOpp);
            }

            Add(entries, Toggle("  Extra Arrows", !Game1.HideExtraStrumlines,
                "Shows or hides the extra sets of arrows some levels have besides yours and the opponent's. In online matches they are always hidden.",
                () => Game1.HideExtraStrumlines = !Game1.HideExtraStrumlines,
                () => new SettingsActionResult { NeedsStrumlineAlphaUpdate = true, NeedsSave = true }));
            EndIf(sStrum);
        }

        // ── Elements ──
        Add(entries, Header("On Screen"));

        Add(entries, new SettingItem
        {
            Label = "Combo & Judgements",
            Value = OnOff(Game1.ShowCombo),
            Kind = SettingKind.Toggle,
            Description = "Shows your combo and the rating of each hit (Sick!, Good, Bad, Shit).",
            OnSelect = () =>
            {
                Game1.ShowCombo = !Game1.ShowCombo;
                hud.ComboVisible = Game1.ShowCombo;
                hud.JudgementVisible = Game1.ShowCombo;
                hud.Save();
                return new SettingsActionResult { NeedsSave = true };
            }
        });

        Add(entries, new SettingItem
        {
            Label = "Score & Stats",
            Value = OnOff(hud.ScoreVisible || hud.MissesVisible || hud.AccuracyVisible),
            Kind = SettingKind.Toggle,
            Description = "Shows your score, misses and accuracy.",
            OnSelect = () =>
            {
                bool next = !(hud.ScoreVisible || hud.MissesVisible || hud.AccuracyVisible);
                hud.ScoreVisible = next;
                hud.MissesVisible = next;
                hud.AccuracyVisible = next;
                hud.Save();
                return new SettingsActionResult { NeedsSave = true };
            }
        });

        Add(entries, new SettingItem
        {
            Label = "Health Bar",
            Value = OnOff(hud.HealthBarVisible),
            Kind = SettingKind.Toggle,
            Description = "Shows the health bar and the characters' icons on it.",
            OnSelect = () =>
            {
                bool next = !hud.HealthBarVisible;
                hud.HealthBarVisible = next;
                hud.OpponentIconVisible = next;
                hud.PlayerIconVisible = next;
                hud.Save();
                return new SettingsActionResult { NeedsSave = true };
            }
        });

        if (BeginIf(hud.HealthBarVisible, NeedsOn("Health Bar", "HUD"), out var sHealth))
        {
            Add(entries, new SettingItem
            {
                Label = "  Health Icons",
                Value = Game1.IconMode == 0 ? "Move with bar" : "Pinned to ends",
                Kind = SettingKind.Toggle,
                Description = "Move with bar: the icons slide along the bar as health goes up and down (like in FNF). Pinned to ends: they stay at the two ends of the bar.",
                OnSelect = () =>
                {
                    Game1.IconMode = (Game1.IconMode + 1) % 2;
                    return new SettingsActionResult { NeedsSave = true };
                }
            });
            EndIf(sHealth);
        }

        Add(entries, new SettingItem
        {
            Label = "Song Title",
            Value = OnOff(hud.SongInfoVisible),
            Kind = SettingKind.Toggle,
            Description = "Shows the name of the song (top-left corner, unless you moved it in the HUD Editor).",
            OnSelect = () =>
            {
                hud.SongInfoVisible = !hud.SongInfoVisible;
                hud.Save();
                return new SettingsActionResult { NeedsSave = true };
            }
        });

        Add(entries, new SettingItem
        {
            Label = "Status Line",
            Value = OnOff(hud.StatusInfoVisible),
            Kind = SettingKind.Toggle,
            Description = "Shows the small line in the bottom-left corner with BOTPLAY / MANUAL, the scroll direction and the scroll speed.",
            OnSelect = () =>
            {
                hud.StatusInfoVisible = !hud.StatusInfoVisible;
                hud.Save();
                return new SettingsActionResult { NeedsSave = true };
            }
        });

        // ── Timer ──
        Add(entries, Header("Song Timer"));

        Add(entries, new SettingItem
        {
            Label = "Song Timer",
            Value = OnOff(Game1.ShowTimer),
            Kind = SettingKind.Toggle,
            Description = "Shows a bar that fills up as the song plays.",
            OnSelect = () =>
            {
                Game1.ShowTimer = !Game1.ShowTimer;
                hud.Save();
                return new SettingsActionResult { NeedsSave = true };
            }
        });

        if (BeginIf(Game1.ShowTimer, NeedsOn("Song Timer", "HUD"), out var sTimer))
        {
            string preset = Game1.TimerPreset == "Legacy" ? "HUD Editor" : Game1.TimerPreset;
            Add(entries, new SettingItem
            {
                Label = "  Position",
                Value = preset,
                Kind = SettingKind.Adjustable,
                Description = "HUD Editor: wherever you placed it there. Top / Middle: across the screen. Left / Right: a vertical bar on that side. Strumline: near your arrows.",
                OnAdjust = (isLeft, _) =>
                {
                    int idx = Array.IndexOf(TimerPresets, preset);
                    if (idx < 0) idx = 0;
                    idx = (idx + (isLeft ? -1 : 1) + TimerPresets.Length) % TimerPresets.Length;
                    Game1.TimerPreset = TimerPresets[idx];
                    return new SettingsActionResult { NeedsSave = true };
                }
            });

            Add(entries, new SettingItem
            {
                Label = "  Color",
                Value = Game1.TimerColorPreset switch
                {
                    1 => "Cyan",
                    2 => "Pink",
                    3 => "Yellow",
                    4 => "Green",
                    5 => "Player",
                    6 => "Opponent",
                    _ => "White"
                },
                Kind = SettingKind.Adjustable,
                Description = "The color the bar fills with. Player / Opponent use that character's health bar color.",
                OnAdjust = (isLeft, _) =>
                {
                    const int count = 7;
                    Game1.TimerColorPreset = (Game1.TimerColorPreset + (isLeft ? -1 : 1) + count) % count;
                    return new SettingsActionResult { NeedsSave = true };
                }
            });

            Add(entries, new SettingItem
            {
                Label = "  Text",
                Value = Game1.TimerTextMode,
                Kind = SettingKind.Adjustable,
                Description = "What is written on the bar: time left, time elapsed, the song's name, the name with its difficulty, or nothing.",
                OnAdjust = (isLeft, _) =>
                {
                    int idx = Array.IndexOf(TimerTextModes, Game1.TimerTextMode);
                    if (idx < 0) idx = 0;
                    idx = (idx + (isLeft ? -1 : 1) + TimerTextModes.Length) % TimerTextModes.Length;
                    Game1.TimerTextMode = TimerTextModes[idx];
                    return new SettingsActionResult { NeedsSave = true };
                }
            });

            // Only the Top position moves to the bottom with Downscroll, so only it needs this
            if (BeginIf(preset == "Top", "Locked: only for the Top position. Set Position (just above) to Top to change this.", out var sTop))
            {
                Add(entries, Toggle("  Always on Top", Game1.TimerForceTop,
                    "With Downscroll on, the Top timer moves to the bottom of the screen. This keeps it at the top.",
                    () => Game1.TimerForceTop = !Game1.TimerForceTop));
                EndIf(sTop);
            }
            EndIf(sTimer);
        }

            EndIf(sHud);
        }

        // ── Debug (not part of the HUD switch) ──
        Add(entries, Header("Debug"));

        Add(entries, Toggle("FPS Counter", Game1.ShowFpsCounter,
            "Shows the frame rate (and whether VSync is on) in the top-right corner.",
            () => Game1.ShowFpsCounter = !Game1.ShowFpsCounter));

        Add(entries, Toggle("Debug Info", Game1.ShowDebugInfo,
            "Shows technical info during a song: BPM, current beat, camera position and events. Useful when making levels.",
            () => Game1.ShowDebugInfo = !Game1.ShowDebugInfo));
    }

    // ── AUDIO ────────────────────────────────────────────────────────────────

    private void BuildAudioEntries(List<SettingItem> entries)
    {
        Add(entries, Header("Volume"));

        Add(entries, new SettingItem
        {
            Label = "Master Volume",
            Value = Percent(Game1.MasterVolume),
            Kind = SettingKind.Adjustable,
            Description = $"The volume of everything in the game; the three below are a part of it. {Controls.Describe(HotkeyAction.VolumeDown)} / {Controls.Describe(HotkeyAction.VolumeUp)} change it from anywhere.",
            OnAdjust = (_, delta) => new SettingsActionResult { AdjustVolume = delta, NeedsSave = true }
        });

        Add(entries, PercentRow("  Music", Game1.SongVolume,
            "The song you are playing.",
            v => Game1.SongVolume = v,
            () => new SettingsActionResult { SongVolumeChanged = true, NeedsSave = true }));

        Add(entries, PercentRow("  Menu Music", OnlineMenuMusic.Volume,
            "The music that plays in the menus.",
            v => OnlineMenuMusic.Volume = v));

        Add(entries, PercentRow("  Sound Effects", UI.UiSfx.Volume,
            "Menu sounds, the countdown and the miss sound.",
            v => UI.UiSfx.Volume = v));

        Add(entries, Header("Sync"));

        Add(entries, new SettingItem
        {
            Label = "Audio Offset",
            Value = OffsetText(Game1.GlobalOffsetMs),
            Kind = SettingKind.Adjustable,
            Description = "Use it when the notes and the music don't line up. If the music reaches you late (Bluetooth headphones, a TV), raise it: the notes come later. Every song, solo and online.",
            OnAdjust = (_, delta) =>
            {
                Game1.GlobalOffsetMs = Math.Clamp(Game1.GlobalOffsetMs + (int)MathF.Round(delta * 5f), -Game1.MaxOffsetMs, Game1.MaxOffsetMs);
                return new SettingsActionResult { NeedsSave = true };
            }
        });

        Add(entries, Header("Sounds"));

        Add(entries, Toggle("Miss Sound", NoteRenderer.MissSounds,
            "Plays a sound when you miss a note (the one that comes with your note style).",
            () => NoteRenderer.MissSounds = !NoteRenderer.MissSounds));
    }

    // ── VIDEO ────────────────────────────────────────────────────────────────

    private void BuildVideoEntries(Game1? game, List<SettingItem> entries)
    {
        Add(entries, Header("Display"));

        #if !ANDROID
        Add(entries, new SettingItem
        {
            Label = "Fullscreen",
            Value = OnOff(game != null && game._isFullscreen),
            Kind = SettingKind.Toggle,
            Description = $"Switches between window and fullscreen. {Controls.Describe(HotkeyAction.ToggleFullscreen)} does it anytime.",
            OnSelect = () => new SettingsActionResult { ToggleFullscreen = true, NeedsSave = false }
        });
        #endif

        Add(entries, new SettingItem
        {
            Label = "VSync",
            Value = OnOff(game != null && game._graphics.SynchronizeWithVerticalRetrace),
            Kind = SettingKind.Toggle,
            Description = "Syncs the frame rate with your screen's refresh rate to stop screen tearing (horizontal cuts in the image).",
            OnSelect = () => new SettingsActionResult { ToggleVSync = true, NeedsSave = true }
        });

        Add(entries, new SettingItem
        {
            Label = "Render Resolution",
            Value = Game1.NativeRenderResolution ? "Native" : "720p",
            Kind = SettingKind.Toggle,
            Description = "Native: songs are drawn at your screen's resolution (sharpest). 720p: drawn at 1280x720 and stretched to fit (lighter for weak graphics cards).",
            OnSelect = () =>
            {
                Game1.NativeRenderResolution = !Game1.NativeRenderResolution;
                return new SettingsActionResult { NeedsSave = true };
            }
        });

        Add(entries, new SettingItem
        {
            Label = "FPS Cap",
            Value = Game1.FpsCap > 0 ? $"{Game1.FpsCap} FPS" : "Unlimited",
            Kind = SettingKind.Adjustable,
            Description = "The highest frame rate the game runs at. Unlimited: as high as your computer allows (with VSync on, your screen's refresh rate is the limit).",
            OnAdjust = (isLeft, _) =>
            {
                var opts = Game1.FpsCapOptions;
                int idx = Array.IndexOf(opts, Game1.FpsCap);
                if (idx < 0) idx = 0;
                idx = Math.Clamp(idx + (isLeft ? -1 : 1), 0, opts.Length - 1);
                Game1.FpsCap = opts[idx];
                return new SettingsActionResult { FpsCapChanged = true, NeedsSave = true };
            }
        });

        Add(entries, Header("Interface"));

        Add(entries, OpenRow("Menu Font",
            Game1.CurrentFontPath is { } fontPath ? System.IO.Path.GetFileNameWithoutExtension(fontPath) : "Default",
            "The font used in the menus. Add your own .ttf / .otf fonts to mods/fonts/.",
            () => new SettingsActionResult { OpenFontMenu = true }));
    }

    // ── osu! songs (fork, Osu/OsuSettings.cs) ────────────────────────────────

    private static SettingsActionResult Saved() => new() { NeedsSave = true };

    private void BuildOsuEntries(List<SettingItem> entries)
    {
        Add(entries, Header("Gameplay"));

        Add(entries, Toggle("Scroll Speed Changes", Osu.OsuSettings.ScrollSpeedChanges,
            "Notes speed up and slow down where the map says, like in osu!mania. OFF: they always move at your Scroll Speed.",
            () => Osu.OsuSettings.ScrollSpeedChanges = !Osu.OsuSettings.ScrollSpeedChanges));

        Add(entries, new SettingItem
        {
            Label = "Note Skin",
            Value = Osu.OsuSettings.PreferBeatmapSkins ? "Beatmap's" : "Mine",
            Kind = SettingKind.Adjustable,
            Description = "Beatmap's: songs that come with their own note images use them. Mine: always your Note Style (APPEARANCE tab).",
            OnSelect = () => { Osu.OsuSettings.PreferBeatmapSkins = !Osu.OsuSettings.PreferBeatmapSkins; return Saved(); },
            OnAdjust = (_, _) => { Osu.OsuSettings.PreferBeatmapSkins = !Osu.OsuSettings.PreferBeatmapSkins; return Saved(); }
        });

        Add(entries, Header("Hitsounds"));

        Add(entries, Toggle("Hitsounds", Osu.OsuSettings.Hitsounds,
            "Plays the map's sounds when you hit notes (and your osu! skin's sounds, if it has them).",
            () => Osu.OsuSettings.Hitsounds = !Osu.OsuSettings.Hitsounds));

        if (BeginIf(Osu.OsuSettings.Hitsounds, NeedsOn("Hitsounds", "osu!"), out var sHit))
        {
            Add(entries, PercentRow("  Hitsound Volume", Osu.OsuSettings.HitsoundVolume,
                "How loud the hitsounds are (part of the Master Volume).",
                v => Osu.OsuSettings.HitsoundVolume = v));
            EndIf(sHit);
        }

        Add(entries, Header("Background"));

        Add(entries, PercentRow("Background Dim", Osu.OsuSettings.BackgroundDim,
            "How much darker the background, video and storyboard are drawn, so the notes are easier to see.",
            v => Osu.OsuSettings.BackgroundDim = v));

        Add(entries, Toggle("Video", Osu.OsuSettings.Video,
#if ANDROID
            "Plays the map's background video (mp4, mkv and webm; avi, flv and wmv videos can't be played on phones).",
#else
            "Plays the map's background video.",
#endif
            () => Osu.OsuSettings.Video = !Osu.OsuSettings.Video));

        if (BeginIf(Osu.OsuSettings.Video, NeedsOn("Video", "osu!"), out var sVid))
        {
            Add(entries, new SettingItem
            {
                Label = "  Video Quality",
                Value = Osu.OsuSettings.VideoFullQuality ? "Full" : "Standard",
                Kind = SettingKind.Adjustable,
#if ANDROID
                Description = "Standard: 640x360 at 30 FPS, light. Full: the video's own quality (up to 1080p at 60 FPS); uses more battery and memory.",
#else
                Description = "Standard: 640x360 at 30 FPS, light. Full: the video's own quality (up to 4K at 60 FPS); uses more CPU and memory.",
#endif
                OnSelect = () => { Osu.OsuSettings.VideoFullQuality = !Osu.OsuSettings.VideoFullQuality; return Saved(); },
                OnAdjust = (_, _) => { Osu.OsuSettings.VideoFullQuality = !Osu.OsuSettings.VideoFullQuality; return Saved(); }
            });

            Add(entries, Toggle("  Background Behind Video", Osu.OsuSettings.BackgroundBehindVideo,
                "When a video doesn't fill the screen, the map's background image shows around it. OFF: black bars.",
                () => Osu.OsuSettings.BackgroundBehindVideo = !Osu.OsuSettings.BackgroundBehindVideo));
            EndIf(sVid);
        }

        Add(entries, Toggle("Storyboard", Osu.OsuSettings.Storyboard,
            "Plays the map's storyboard: the animated images some maps show behind and over the notes.",
            () => Osu.OsuSettings.Storyboard = !Osu.OsuSettings.Storyboard));
    }

    // ── CONTROLS tab (fork): note keys / touch + every rebindable hotkey ─────

    // Key capture: Enter on a hotkey row waits for one key (with optional Ctrl/Shift/Alt).
    private HotkeyAction? _captureAction;

    /// <summary>True while a CONTROLS row is waiting for a key (Game1 then withholds keys from the rest of the game).</summary>
    public bool IsCapturingKey => _captureAction != null;

    private void BuildControlsEntries(List<SettingItem> entries)
    {
        #if !ANDROID
        Add(entries, Header("Keyboard"));
        Add(entries, OpenRow("Note Keys",
            $"{Controls.KeyName(NoteRenderer.PlayerKeys[0])} / {Controls.KeyName(NoteRenderer.PlayerKeys[1])} / {Controls.KeyName(NoteRenderer.PlayerKeys[2])} / {Controls.KeyName(NoteRenderer.PlayerKeys[3])}",
            "The four keys you play with: left, down, up and right. Opens a screen to change them.",
            () => new SettingsActionResult { OpenKeyBind = true }));
        #else
        Add(entries, Header("Touch"));
        Add(entries, Toggle("Touch Hitbox Colors", !Game1.HideTouchHitboxColors,
            "Lights up the part of the screen you are pressing while you play. OFF: the touch areas are invisible.",
            () => Game1.HideTouchHitboxColors = !Game1.HideTouchHitboxColors));
        if (BeginIf(!Game1.HideTouchHitboxColors, NeedsOn("Touch Hitbox Colors", "CONTROLS"), out var sTouch))
        {
            Add(entries, PercentRow("  Hitbox Opacity", Game1.TouchHitboxOpacity,
                "How strongly a touch area lights up while you press it (default 30%).",
                v => Game1.TouchHitboxOpacity = v));
            EndIf(sTouch);
        }
        Add(entries, new SettingItem
        {
            Label = "Menu Buttons",
            Value = Game1.MenuButtonsMode switch { 1 => "ON", 2 => "OFF", _ => "AUTO" },
            Kind = SettingKind.Adjustable,
            Description = "The on-screen arrows and A / B buttons in menus. AUTO: only where tapping the screen isn't enough (the back gesture works as B). ON: always. OFF: never.",
            OnAdjust = (_, delta) =>
            {
                Game1.MenuButtonsMode = ((Game1.MenuButtonsMode + (delta > 0 ? 1 : -1)) % 3 + 3) % 3;
                return new SettingsActionResult { NeedsSave = true };
            }
        });
        #endif

        string? group = null;
        foreach (var info in Controls.All)
        {
            if (info.Group != group)
            {
                group = info.Group;
                Add(entries, Header(group));
            }

            var action = info.Action;
            bool capturing = _captureAction == action;
            Add(entries, new SettingItem
            {
                Label = info.Label,
                Value = capturing ? "Press a key..." : Controls.Describe(action),
                Kind = SettingKind.Action,
                Description = capturing
                    ? "Press the new key (hold Ctrl / Shift / Alt with it for a combination). Backspace: no key. Esc: cancel."
                    : info.Description + " Select it to change the key.",
                OnSelect = () =>
                {
                    _captureAction = action;
                    return new SettingsActionResult { NeedsSave = false };
                }
            });
        }

        Add(entries, Header("Reset"));

        Add(entries, new SettingItem
        {
            Label = "Reset Hotkeys",
            Value = "",
            Kind = SettingKind.Action,
            Description = "Puts every hotkey above back to its default key. Your note keys don't change.",
            OnSelect = () =>
            {
                Controls.ResetToDefaults();
                _captureAction = null;
                return new SettingsActionResult { NeedsSave = true };
            }
        });
    }

    /// <summary>
    /// Feeds raw keyboard input to an active key capture. Returns true when a binding changed
    /// (caller saves settings).
    /// </summary>
    public bool UpdateKeyCapture(KeyboardState kb, KeyboardState prev)
    {
        if (_captureAction is not HotkeyAction action) return false;

        foreach (var key in kb.GetPressedKeys())
        {
            if (prev.IsKeyDown(key) || Controls.IsModifier(key)) continue;

            if (key == Keys.Escape)
            {
                _captureAction = null;  // cancel, keep the current binding
                UI.UiSfx.Play(UI.UiSound.Back);
                return false;
            }

            bool ctrl = kb.IsKeyDown(Keys.LeftControl) || kb.IsKeyDown(Keys.RightControl);
            bool shift = kb.IsKeyDown(Keys.LeftShift) || kb.IsKeyDown(Keys.RightShift);
            bool alt = kb.IsKeyDown(Keys.LeftAlt) || kb.IsKeyDown(Keys.RightAlt);
            var binding = key == Keys.Back ? KeyBinding.None : new KeyBinding(key, ctrl, shift, alt);

            // Bare menu-navigation keys would make menus unusable
            if (binding.ModifierCount == 0 && key is Keys.Up or Keys.Down or Keys.Left or Keys.Right or Keys.Enter or Keys.Space)
            {
                UI.UiSfx.Play(UI.UiSound.SliderEnd);
                return false;
            }

            Controls.Set(action, binding);
            UI.UiSfx.Play(UI.UiSound.Confirm);
            _captureAction = null;
            return true;
        }
        return false;
    }

    /// <summary>Stops any key capture (e.g. when the settings menu closes).</summary>
    public void CancelKeyCapture()
    {
        _captureAction = null;
    }

    // ── Action & Adjustment Dispatch ─────────────────────────────────────────

    public SettingsActionResult HandleSelectAction(Game1 game)
    {
        var entries = BuildTabEntries(game);
        if (Selection < 0 || Selection >= entries.Count)
            return new SettingsActionResult();

        var item = entries[Selection];
        if (item.LockedReason != null)
        {
            IsFocused = false;
            UI.UiSfx.Play(UI.UiSound.Error);   // the description box says why
            return new SettingsActionResult();
        }
        if (item.Kind == SettingKind.Adjustable)
        {
            // Enter focuses / unfocuses an adjustable value (Scroll Speed, Volume, ...).
            IsFocused = !IsFocused;
            return new SettingsActionResult();
        }

        IsFocused = false;
        if (item.OnSelect == null) return new SettingsActionResult();
        var result = item.OnSelect();
        // Sub-menus / overlays get their open sound from Game1; plain toggles/actions click here.
        bool opensSomething = result.OpenKeyBind || result.OpenArrowsMenu || result.OpenSplashMenu ||
                              result.OpenCountdownMenu || result.OpenFontMenu || result.OpenHudEditor || result.ExitMenu;
        if (!opensSomething)
            UI.UiSfx.Play(item.Kind == SettingKind.Toggle ? UI.UiSound.Toggle : UI.UiSound.Confirm);
        return result;
    }

    public SettingsActionResult HandleAdjustment(Game1 game, bool isLeft)
    {
        var entries = BuildTabEntries(game);
        if (Selection < 0 || Selection >= entries.Count)
            return new SettingsActionResult();

        return RunAdjust(game, entries[Selection], isLeft);
    }

    /// <summary>Runs an adjustable row's OnAdjust and plays a tick, or the "limit" sound if the value did not change.</summary>
    private SettingsActionResult RunAdjust(Game1 game, SettingItem item, bool isLeft)
    {
        if (item.OnAdjust == null) return new SettingsActionResult();
        string before = item.Value;
        var result = item.OnAdjust(isLeft, isLeft ? -1f : 1f);

        // Some rows apply their change through the result (master volume, seek)
        bool viaResult = (result.AdjustVolume > 0f && Game1.MasterVolume < 1f) ||
                         (result.AdjustVolume < 0f && Game1.MasterVolume > 0f) ||
                         result.SeekSong != 0f;
        var after = BuildTabEntries(game);
        bool changed = viaResult || (Selection < after.Count && after[Selection].Value != before);
        UI.UiSfx.Play(!changed ? UI.UiSound.SliderEnd : isLeft ? UI.UiSound.SliderDown : UI.UiSound.SliderUp);
        return result;
    }

    public SettingsActionResult? UpdateMouse(
        Game1 game,
        MouseState mouse,
        MouseState prevMouse,
        out SettingsActionResult? adjustmentResult,
        out bool exitClicked)
    {
        adjustmentResult = null;
        exitClicked = false;

        Point pos = mouse.Position;
        bool moved = Math.Abs(pos.X - _lastMousePos.X) > 2 || Math.Abs(pos.Y - _lastMousePos.Y) > 2;
        if (moved) _lastMousePos = pos;

        int wheelDelta = mouse.ScrollWheelValue - prevMouse.ScrollWheelValue;
        if (UI.PointerInput.IsTouch)
        {
            // Touch (fork): the list follows the finger 1:1 instead of in wheel notches.
            float drag = UI.PointerInput.DragDeltaY;
            if (drag != 0f)
            {
                _followSelectionScroll = false;
                ScrollOffset = Math.Max(0f, ScrollOffset - drag);
            }
        }
        else if (wheelDelta != 0)
        {
            _followSelectionScroll = false;
            ScrollOffset = Math.Max(0f, ScrollOffset - (wheelDelta / 120f) * 56f);
        }

        _backBtnHovered = _backBtnRect != Rectangle.Empty && _backBtnRect.Contains(pos);

        _hoveredTab = -1;
        for (int i = 0; i < _tabRects.Length; i++)
        {
            if (_tabRects[i] != Rectangle.Empty && _tabRects[i].Contains(pos))
            {
                _hoveredTab = i;
                break;
            }
        }

        _hoveredRow = -1;
        _hoveredArrow = 0;
        foreach (var hb in _rowHitboxes)
        {
            if (hb.RowRect.Contains(pos))
            {
                _hoveredRow = hb.Index;
                if (hb.LeftArrowRect.HasValue && hb.LeftArrowRect.Value.Contains(pos))
                    _hoveredArrow = -1;
                else if (hb.RightArrowRect.HasValue && hb.RightArrowRect.Value.Contains(pos))
                    _hoveredArrow = 1;

                // Hover-select follows the mouse; a finger only selects by tapping (it would select
                // every row it crosses while scrolling). Pointer selections never re-centre the list.
                if (moved && !IsFocused && !UI.PointerInput.IsTouch && Selection != hb.Index)
                {
                    Selection = hb.Index;
                    _followSelectionScroll = false;
                }
                break;
            }
        }

        bool clicked = mouse.LeftButton == ButtonState.Pressed && prevMouse.LeftButton == ButtonState.Released;

        // Touch (fork): holding a finger on < or > repeats it (a tap is one step). PointerInput
        // turns a hold into a long press, so the lift that ends it is not also a click.
        if (UI.PointerInput.IsTouch)
        {
            bool holding = UI.PointerInput.IsDown && !UI.PointerInput.IsDragging && _hoveredRow >= 0 && _hoveredArrow != 0;
            long now = Environment.TickCount64;
            if (!holding) _arrowHoldStart = 0;
            else if (_arrowHoldStart == 0) { _arrowHoldStart = now; _arrowNextRepeat = now + 500; }
            else if (now >= _arrowNextRepeat)
            {
                _arrowNextRepeat = now + 90;
                var holdEntries = BuildTabEntries(game);
                if (_hoveredRow < holdEntries.Count && holdEntries[_hoveredRow] is { Kind: SettingKind.Adjustable, OnAdjust: not null } held)
                {
                    Selection = _hoveredRow;
                    _followSelectionScroll = false;
                    IsFocused = true;
                    adjustmentResult = RunAdjust(game, held, _hoveredArrow < 0);
                    return null;
                }
            }
        }

        if (!clicked) return null;

        if (_backBtnHovered)
        {
            IsFocused = false;
            exitClicked = true;
            return null;
        }

        if (IsSearching && _searchClearRect != Rectangle.Empty && _searchClearRect.Contains(pos))
        {
            SetSearch("");
            UI.UiSfx.Play(UI.UiSound.Back);
            return null;
        }
        if (_searchBoxRect != Rectangle.Empty && _searchBoxRect.Contains(pos))
        {
            OpenTouchSearchPrompt();   // desktop: typing already goes to the box
            return null;
        }

        if (_hoveredTab >= 0)
        {
            if (_hoveredTab != CurrentTab || IsSearching)
                SetTab(_hoveredTab);
            return null;
        }

        if (_hoveredRow >= 0)
        {
            var entries = BuildTabEntries(game);
            if (_hoveredRow < entries.Count)
            {
                if (_hoveredRow != Selection)
                {
                    Selection = _hoveredRow;
                    IsFocused = false;
                }
                _followSelectionScroll = false;

                var item = entries[Selection];
                if (_hoveredArrow != 0 && item.Kind == SettingKind.Adjustable && item.OnAdjust != null)
                {
                    IsFocused = true;
                    adjustmentResult = RunAdjust(game, item, _hoveredArrow < 0);
                    return null;
                }
                // Touch: "focus" only means something with the D-pad; tapping the row selects it
                // (shows its description) and the < > buttons change it.
                if (UI.PointerInput.IsTouch && item.Kind == SettingKind.Adjustable)
                    return null;

                return HandleSelectAction(game);
            }
        }

        return null;
    }

    // ── Text helpers (FontRenderer when available, SpriteFont fallback scaled to the same size) ──

    private static Vector2 Measure(SpriteFont font, FontRenderer? fr, string text, int size)
        => fr != null ? fr.MeasureString(text, size) : font.MeasureUi(text) * (size / 36f);

    private static void Text(SpriteBatch sb, SpriteFont font, FontRenderer? fr, string text, Vector2 pos, Color color, int size)
    {
        if (fr != null) fr.DrawString(sb, text, pos, color, size);
        else sb.DrawUiString(font, text, pos, color, 0f, Vector2.Zero, size / 36f, SpriteEffects.None, 0f);
    }

    /// <summary>Largest size ≤ <paramref name="size"/> (and ≥ <paramref name="min"/>) at which the text fits <paramref name="maxW"/>.</summary>
    private static int FitSize(SpriteFont font, FontRenderer? fr, string text, int size, int min, float maxW)
    {
        while (size > min && Measure(font, fr, text, size).X > maxW) size--;
        return size;
    }

    /// <summary>Word-wraps <paramref name="text"/> to <paramref name="maxW"/>.</summary>
    private static List<string> Wrap(SpriteFont font, FontRenderer? fr, string text, int size, float maxW)
    {
        var lines = new List<string>();
        string line = "";
        foreach (var word in text.Split(' ', StringSplitOptions.RemoveEmptyEntries))
        {
            string test = line.Length == 0 ? word : line + " " + word;
            if (line.Length > 0 && Measure(font, fr, test, size).X > maxW)
            {
                lines.Add(line);
                line = word;
            }
            else line = test;
        }
        if (line.Length > 0) lines.Add(line);
        return lines;
    }

    // ── Drawing (Preserves Codename Engine Pink Visual Style) ─────────────────

    public void Draw(Game1 game, SpriteBatch spriteBatch, SpriteFont font, Texture2D pixel,
                     int screenWidth, int screenHeight, FontRenderer? fontRenderer = null)
    {
        if (font == null) return;
        var fr = fontRenderer;

        const float referenceHeight = 720f;
        float scaleFactor = screenHeight / referenceHeight;

        // ── Background ────────────────────────────────────────────────────────
        for (int y = 0; y < screenHeight; y += 4)
        {
            float factor = (float)y / screenHeight;
            Color bgColor = Color.Lerp(new Color(8, 8, 15), new Color(12, 12, 22), factor) * 0.95f;
            DrawRect(spriteBatch, pixel, 0, y, screenWidth, 4, bgColor);
        }

        // ── Header with tabs ──────────────────────────────────────────────────
        int headerH = (int)(80 * scaleFactor);
        int tabH = (int)(40 * scaleFactor);
        int tabY = headerH - tabH;

        for (int y = 0; y < headerH; y++)
        {
            float factor = (float)y / headerH;
            Color barColor = Color.Lerp(new Color(20, 12, 30), new Color(12, 12, 20), factor);
            DrawRect(spriteBatch, pixel, 0, y, screenWidth, 1, barColor);
        }

        // Title with shadow
        int titleSize = Math.Max(16, (int)(48 * scaleFactor));
        float titleX = 22 * scaleFactor;
        Vector2 titleSz = Measure(font, fr, "SETTINGS", titleSize);
        float titleY = Math.Max(0f, (tabY - titleSz.Y) / 2f);
        Text(spriteBatch, font, fr, "SETTINGS", new Vector2(titleX + 2, titleY + 2), Color.Black * 0.5f, titleSize);
        Text(spriteBatch, font, fr, "SETTINGS", new Vector2(titleX, titleY), Accent, titleSize);

        // Top-right clickable back button
        // Touch: a bigger "< BACK" (the button read "ESC: Back" and was 26 px tall at 720p).
        int backBtnW = (int)((UI.PointerInput.IsTouch ? 140 : 110) * scaleFactor);
        int backBtnH = (int)((UI.PointerInput.IsTouch ? 36 : 26) * scaleFactor);
        int backBtnX = screenWidth - backBtnW - (int)(18 * scaleFactor);
        int backBtnY = Math.Max(4, (tabY - backBtnH) / 2);
        _backBtnRect = new Rectangle(backBtnX, backBtnY, backBtnW, backBtnH);
        DrawRect(spriteBatch, pixel, backBtnX, backBtnY, backBtnW, backBtnH,
            _backBtnHovered ? Accent * 0.35f : new Color(25, 22, 38) * 0.85f);
        DrawRect(spriteBatch, pixel, backBtnX, backBtnY + backBtnH - 1, backBtnW, 1,
            _backBtnHovered ? Accent : Accent * 0.45f);
        string backLabel = UI.PointerInput.IsTouch ? "<  BACK" : "ESC: Back";
        int backSzPx = FitSize(font, fr, backLabel, Math.Max(12, (int)(15 * scaleFactor)), 9, backBtnW - 8);
        var bsz = Measure(font, fr, backLabel, backSzPx);
        Text(spriteBatch, font, fr, backLabel,
            new Vector2(backBtnX + (backBtnW - bsz.X) / 2f, backBtnY + (backBtnH - bsz.Y) / 2f),
            _backBtnHovered ? Color.White : Color.White * 0.7f, backSzPx);

        // Search box between the title and the back button
        DrawSearchBox(spriteBatch, font, pixel, fr, titleX + titleSz.X + 24 * scaleFactor,
            backBtnX - 16 * scaleFactor, tabY, scaleFactor);

        // ── Draw Tabs ─────────────────────────────────────────────────────────
        int tabCount = TabNames.Length;
        float tabW = screenWidth / (float)tabCount;

        // One text size for every tab: the largest at which the longest name still fits
        int tabTextSize = Math.Max(10, (int)(24 * scaleFactor));
        foreach (var name in TabNames)
            tabTextSize = FitSize(font, fr, name, tabTextSize, 9, tabW - 14 * scaleFactor);

        for (int i = 0; i < tabCount; i++)
        {
            bool isSelected = i == CurrentTab && !IsSearching;
            bool isHovered = i == _hoveredTab;
            float tx = i * tabW;
            _tabRects[i] = new Rectangle((int)tx, tabY, (int)tabW, tabH);

            Color tabBg = isSelected
                ? Accent * 0.35f
                : (isHovered ? Accent * 0.16f : new Color(15, 15, 25) * 0.7f);

            for (int ty = 0; ty < tabH; ty++)
            {
                float gradFactor = (float)ty / tabH;
                Color lineColor = isSelected
                    ? Color.Lerp(tabBg, tabBg * 0.8f, gradFactor)
                    : Color.Lerp(tabBg, tabBg * 1.2f, gradFactor);
                DrawRect(spriteBatch, pixel, (int)tx, tabY + ty, (int)tabW, 1, lineColor);
            }

            if (isSelected)
                DrawRect(spriteBatch, pixel, (int)tx, tabY, (int)tabW, 3, Accent);

            if (i < tabCount - 1)
                DrawRect(spriteBatch, pixel, (int)(tx + tabW) - 1, tabY, 1, tabH, Color.Black * 0.3f);

            string tabName = TabNames[i];
            Color tabColor = isSelected ? Color.White : (isHovered ? Color.White * 0.9f : Color.White * 0.65f);
            Vector2 measure = Measure(font, fr, tabName, tabTextSize);
            var tabPos = new Vector2(tx + tabW / 2f - measure.X / 2f, tabY + tabH / 2f - measure.Y / 2f);
            if (isSelected)
                Text(spriteBatch, font, fr, tabName, tabPos + Vector2.One, Color.Black * 0.4f, tabTextSize);
            Text(spriteBatch, font, fr, tabName, tabPos, tabColor, tabTextSize);
        }

        // Accent line below tabs
        DrawRect(spriteBatch, pixel, 0, headerH - 3, screenWidth, 3, Accent * 0.7f);

        // ── Bottom: hint bar, then the description box above it ──────────────
        int hintBarH = Math.Clamp((int)(36 * scaleFactor), 28, 60);
        var entries = BuildTabEntries(game);
        var selectedItem = (Selection >= 0 && Selection < entries.Count) ? entries[Selection] : null;
        string description = selectedItem?.Description ?? "";
        string? lockedReason = selectedItem?.LockedReason;

        int margin = Math.Max(12, (int)(24 * scaleFactor));
        int descBoxW = (int)Math.Min(1000 * scaleFactor, screenWidth - margin * 2);
        int descPad = Math.Clamp((int)(14 * scaleFactor), 8, 24);
        int descSize = Math.Clamp((int)MathF.Round(19 * scaleFactor), 14, 32);
        float descLineGap = Math.Max(2f, 4 * scaleFactor);
        // Room for three lines at the normal size; the list never moves when the text changes.
        int descBoxH = (int)(descPad * 2 + 3 * (Measure(font, fr, "Ag", descSize).Y + descLineGap));
        int descBoxY = screenHeight - hintBarH - descBoxH - Math.Max(6, (int)(10 * scaleFactor));
        int descBoxX = screenWidth / 2 - descBoxW / 2;

        // ── Content area ──────────────────────────────────────────────────────
        float listTop = headerH + 6 * scaleFactor;
        float listBottom = descBoxY - 8 * scaleFactor;
        DrawList(spriteBatch, font, pixel, screenWidth, listTop, listBottom, entries, fr, scaleFactor);

        if (!string.IsNullOrEmpty(description) || lockedReason != null)
            DrawDescription(spriteBatch, font, pixel, fr, lockedReason, description, descBoxX, descBoxY, descBoxW, descBoxH,
                descPad, descSize, descLineGap);

        // ── Navigation hint bar ───────────────────────────────────────────────
        string hint = UI.PointerInput.IsTouch
            ? "Tap a tab or option   |   Tap  <  >  to change values (hold to repeat)   |   Drag to scroll   |   Back: return"
            : _captureAction != null
            ? "Press the new key   |   BACKSPACE: No key   |   ESC: Cancel"
            : IsFocused
            ? "LEFT/RIGHT: Change value   |   ENTER / UP / DOWN / ESC: Done"
            : IsSearching
            ? "UP/DOWN: Navigate   |   ENTER: Select   |   BACKSPACE: Edit search   |   ESC: Clear search"
            : "UP/DOWN: Navigate   |   LEFT/RIGHT: Tab   |   ENTER: Select   |   Type to search   |   ESC: Back";

        for (int y = 0; y < hintBarH; y++)
        {
            float factor = (float)y / hintBarH;
            Color hintBg = Color.Lerp(new Color(10, 10, 18), new Color(8, 8, 12), factor) * 0.9f;
            DrawRect(spriteBatch, pixel, 0, screenHeight - hintBarH + y, screenWidth, 1, hintBg);
        }

        DrawRect(spriteBatch, pixel, 0, screenHeight - hintBarH, screenWidth, 2,
            (IsFocused ? FocusAccent : Accent) * 0.45f);

        Color hintColor = IsFocused ? new Color(255, 225, 130) : Color.White * 0.65f;
        int hintSize = FitSize(font, fr, hint, Math.Clamp((int)(17 * scaleFactor), 13, 24), 9, screenWidth - margin * 2);
        Vector2 hintMeasure = Measure(font, fr, hint, hintSize);
        Text(spriteBatch, font, fr, hint,
            new Vector2(screenWidth / 2f - hintMeasure.X / 2f, screenHeight - hintBarH + (hintBarH - hintMeasure.Y) / 2f),
            hintColor, hintSize);
    }

    private void DrawSearchBox(SpriteBatch sb, SpriteFont font, Texture2D pixel, FontRenderer? fr,
                               float left, float right, int headerTopH, float scaleFactor)
    {
        float w = Math.Min(420 * scaleFactor, right - left);
        if (w < 90)
        {
            _searchBoxRect = Rectangle.Empty;
            _searchClearRect = Rectangle.Empty;
            return;
        }
        int h = Math.Max(20, (int)(headerTopH - 12 * scaleFactor));
        int y = Math.Max(2, (headerTopH - h) / 2);
        var box = new Rectangle((int)left, y, (int)w, h);
        _searchBoxRect = box;

        bool hovered = box.Contains(_lastMousePos);
        bool active = IsSearching;
        DrawRect(sb, pixel, box.X, box.Y, box.Width, box.Height, new Color(18, 16, 28) * 0.95f);
        Color border = active ? Accent : (hovered ? Accent * 0.6f : Color.White * 0.18f);
        DrawRect(sb, pixel, box.X, box.Bottom - 2, box.Width, 2, border);
        DrawRect(sb, pixel, box.X, box.Y, box.Width, 1, border * 0.5f);
        DrawRect(sb, pixel, box.X, box.Y, 1, box.Height, border * 0.5f);
        DrawRect(sb, pixel, box.Right - 1, box.Y, 1, box.Height, border * 0.5f);

        int pad = Math.Max(6, (int)(10 * scaleFactor));
        int size = Math.Clamp((int)(17 * scaleFactor), 11, 26);

        // Clear "X" button while something is typed
        _searchClearRect = Rectangle.Empty;
        float textRight = box.Right - pad;
        if (active)
        {
            int cw = h;
            _searchClearRect = new Rectangle(box.Right - cw, box.Y, cw, box.Height);
            bool clearHover = _searchClearRect.Contains(_lastMousePos);
            var xs = Measure(font, fr, "X", size);
            Text(sb, font, fr, "X", new Vector2(_searchClearRect.Center.X - xs.X / 2f, box.Y + (h - xs.Y) / 2f),
                clearHover ? Color.White : Color.White * 0.55f, size);
            textRight = _searchClearRect.X - 4;
        }

        float maxW = textRight - (box.X + pad);
        string shown;
        Color color;
        if (active)
        {
            bool caret = !UI.PointerInput.IsTouch && (_caretTime % 1f) < 0.55f;
            shown = _search + (caret ? "|" : "");
            // Long searches show their end
            while (shown.Length > 1 && Measure(font, fr, shown, size).X > maxW) shown = shown[1..];
            color = Color.White;
        }
        else
        {
            shown = UI.PointerInput.IsTouch ? "Tap to search" : "Type to search settings...";
            color = Color.White * 0.38f;
            size = FitSize(font, fr, shown, size, 9, maxW);
        }
        var ts = Measure(font, fr, shown.Length > 0 ? shown : "A", size);
        Text(sb, font, fr, shown, new Vector2(box.X + pad, box.Y + (h - ts.Y) / 2f), color, size);
    }

    /// <summary>
    /// Description box: fixed height (three lines at the normal size), word-wrapped; long text
    /// shrinks the font a little and, if it still doesn't fit, is cut with "...".
    /// </summary>
    private void DrawDescription(SpriteBatch sb, SpriteFont font, Texture2D pixel, FontRenderer? fr, string? lockedReason,
                                 string text, int x, int y, int w, int h, int pad, int size, float lineGap)
    {
        for (int yy = 0; yy < h; yy++)
        {
            float factor = (float)yy / h;
            DrawRect(sb, pixel, x, y + yy, w, 1, Color.Lerp(new Color(0, 0, 0, 200), new Color(0, 0, 0, 180), factor));
        }
        Color boxBorder = lockedReason != null ? LockedColor : IsFocused ? FocusAccent : Accent;
        DrawRect(sb, pixel, x, y, w, 2, boxBorder * 0.6f);
        DrawRect(sb, pixel, x, y + h - 2, w, 2, boxBorder * 0.6f);
        DrawRect(sb, pixel, x, y, 2, h, boxBorder * 0.4f);
        DrawRect(sb, pixel, x + w - 2, y, 2, h, boxBorder * 0.4f);

        float maxW = w - pad * 2;
        float maxH = h - pad * 2;
        int minSize = Math.Max(12, (int)(size * 0.8f));
        // A locked row's reason comes first, in its own colour
        List<string> lines;
        int reasonLines = 0;
        float lineH;
        while (true)
        {
            lines = new List<string>();
            if (lockedReason != null) lines.AddRange(Wrap(font, fr, lockedReason, size, maxW));
            reasonLines = lines.Count;
            lines.AddRange(Wrap(font, fr, text, size, maxW));
            lineH = Measure(font, fr, "Ag", size).Y + lineGap;
            if (lines.Count * lineH - lineGap <= maxH || size <= minSize) break;
            size--;
        }

        int maxLines = Math.Max(1, (int)((maxH + lineGap) / lineH));
        if (lines.Count > maxLines)
        {
            lines = lines.GetRange(0, maxLines);
            string last = lines[^1];
            while (last.Length > 1 && Measure(font, fr, last + "...", size).X > maxW) last = last[..^1];
            lines[^1] = last.TrimEnd() + "...";
        }

        // Vertically centred in the box
        float blockH = lines.Count * lineH - lineGap;
        float ty = y + (h - blockH) / 2f;
        for (int i = 0; i < lines.Count; i++)
        {
            Text(sb, font, fr, lines[i], new Vector2(x + pad, ty), i < reasonLines ? LockedColor : Color.White * 0.85f, size);
            ty += lineH;
        }
    }

    private void DrawList(SpriteBatch sb, SpriteFont font, Texture2D pixel, int sw, float listTop, float listBottom,
                          List<SettingItem> entries, FontRenderer? fr, float scaleFactor)
    {
        float itemH = Math.Max(34f, 56f * scaleFactor);
        float panelW = Math.Min(900f * scaleFactor, sw * 0.7f);
        float panelX = (sw - panelW) / 2f;
        int textSize = Math.Clamp((int)(28 * scaleFactor), 14, 44);

        float startY = listTop + 8 * scaleFactor;
        float availableHeight = Math.Max(itemH, listBottom - startY);
        float totalHeight = entries.Count * itemH;
        float maxScroll = Math.Max(0f, totalHeight - availableHeight);

        if (maxScroll <= 0f)
        {
            ScrollOffset = 0f;
        }
        else if (_followSelectionScroll)
        {
            float selectedY = Selection * itemH;
            float targetScroll = selectedY - (availableHeight / 2f) + (itemH / 2f);
            targetScroll = Math.Clamp(targetScroll, 0f, maxScroll);

            float scrollDiff = targetScroll - ScrollOffset;
            if (Math.Abs(scrollDiff) > 0.5f)
                ScrollOffset += scrollDiff * 0.3f;
            else
                ScrollOffset = targetScroll;
        }

        ScrollOffset = Math.Clamp(ScrollOffset, 0f, maxScroll);

        _rowHitboxes.Clear();

        if (entries.Count == 0)
        {
            string msg = IsSearching ? $"No settings match \"{_search}\"" : "Nothing here";
            int ms = FitSize(font, fr, msg, textSize, 10, panelW);
            var msz = Measure(font, fr, msg, ms);
            Text(sb, font, fr, msg, new Vector2(sw / 2f - msz.X / 2f, startY + 30 * scaleFactor), Color.White * 0.55f, ms);
            return;
        }

        var oldScissor = sb.GraphicsDevice.ScissorRectangle;
        var viewportTop = (int)listTop;
        var viewportBottom = (int)listBottom;
        var scissorRect = Rectangle.Intersect(
            new Rectangle((int)panelX - 20, viewportTop, (int)panelW + 40, Math.Max(1, viewportBottom - viewportTop)),
            sb.GraphicsDevice.Viewport.Bounds);

        sb.End();
        sb.Begin(SpriteSortMode.Deferred, BlendState.AlphaBlend, SamplerState.PointClamp, null,
            new RasterizerState { ScissorTestEnable = true });
        sb.GraphicsDevice.ScissorRectangle = scissorRect;

        float textH = Measure(font, fr, "Ag", textSize).Y;

        for (int i = 0; i < entries.Count; i++)
        {
            bool sel = i == Selection;
            bool focused = sel && IsFocused;
            bool hovered = i == _hoveredRow;
            float y = startY + i * itemH - ScrollOffset;

            if (y + itemH < viewportTop || y > viewportBottom)
                continue;

            var item = entries[i];
            if (item.Kind == SettingKind.Header)
            {
                DrawSectionHeader(sb, font, pixel, fr, item.Label, panelX, panelW, y, itemH, scaleFactor);
                continue;
            }

            int rowX = (int)panelX - 12;
            int rowY = (int)y + 2;
            int rowW = (int)panelW + 24;
            int rowH = (int)itemH - 4;
            var fullRowRect = new Rectangle(rowX, rowY, rowW, rowH);

            if (sel)
            {
                Color accent = focused ? FocusAccent : Accent;
                DrawRect(sb, pixel, rowX, rowY, rowW, rowH, accent * (focused ? 0.28f : 0.25f));
                DrawRect(sb, pixel, rowX, rowY, 5, rowH, accent);
                DrawRect(sb, pixel, rowX, rowY, rowW, 2, accent * 0.55f);
                DrawRect(sb, pixel, rowX, rowY + rowH - 2, rowW, 2, accent * 0.55f);
            }
            else if (hovered)
            {
                DrawRect(sb, pixel, rowX, rowY, rowW, rowH, Color.White * 0.06f);
            }

            Color textColor = focused ? new Color(255, 235, 150) : (sel ? Color.White : Color.White * 0.78f);
            Color valueColor = focused ? Color.Gold : (sel ? new Color(0xFF, 0xAA, 0xDD) : new Color(0xAA, 0xAA, 0xBB));
            if (item.LockedReason != null)
            {
                // Locked: dimmed, value in the locked colour
                textColor = Color.White * (sel ? 0.5f : 0.32f);
                valueColor = LockedColor * (sel ? 0.75f : 0.45f);
            }
            float textY = rowY + (rowH - textH) / 2f;

            // Label
            Text(sb, font, fr, item.Label, new Vector2(panelX + 10, textY), textColor, textSize);

            // Value / Adjustable arrows
            if (item.Kind == SettingKind.Adjustable && item.OnAdjust != null)
            {
                Vector2 valSz = Measure(font, fr, item.Value, textSize);
                Vector2 arrowSz = Measure(font, fr, "<", textSize);

                float padX = 10f;
                // Touch: finger-sized < > buttons (they were ~36 px wide on a phone).
                float arrowW = UI.PointerInput.IsTouch
                    ? Math.Max(rowH * 1.1f, arrowSz.X + 24f)
                    : Math.Max(24f * scaleFactor, arrowSz.X + 12f);
                float rightEdge = panelX + panelW - padX;
                float rightArrowX = rightEdge - arrowW;
                float valX = rightArrowX - 8f - valSz.X;
                float leftArrowX = valX - 8f - arrowW;

                var leftRect = new Rectangle((int)leftArrowX, rowY, (int)arrowW, rowH);
                var rightRect = new Rectangle((int)rightArrowX, rowY, (int)arrowW, rowH);

                if (UI.PointerInput.IsTouch)
                {
                    var bg = Color.White * (sel ? 0.10f : 0.05f);
                    DrawRect(sb, pixel, leftRect.X + 3, leftRect.Y + 4, leftRect.Width - 6, leftRect.Height - 8, bg);
                    DrawRect(sb, pixel, rightRect.X + 3, rightRect.Y + 4, rightRect.Width - 6, rightRect.Height - 8, bg);
                }

                bool lHover = hovered && _hoveredArrow == -1;
                bool rHover = hovered && _hoveredArrow == 1;
                Color lCol = lHover ? Color.White : (focused ? Color.Gold : valueColor * (sel ? 1f : 0.55f));
                Color rCol = rHover ? Color.White : (focused ? Color.Gold : valueColor * (sel ? 1f : 0.55f));

                Text(sb, font, fr, "<", new Vector2(leftArrowX + (arrowW - arrowSz.X) / 2f, textY), lCol, textSize);
                Text(sb, font, fr, item.Value, new Vector2(valX, textY), valueColor, textSize);
                Text(sb, font, fr, ">", new Vector2(rightArrowX + (arrowW - arrowSz.X) / 2f, textY), rCol, textSize);

                // Clip hitbox inside visible viewport
                var clippedRow = Rectangle.Intersect(fullRowRect, scissorRect);
                if (!clippedRow.IsEmpty)
                {
                    _rowHitboxes.Add(new RowHitbox
                    {
                        Index = i,
                        RowRect = clippedRow,
                        LeftArrowRect = Rectangle.Intersect(leftRect, scissorRect),
                        RightArrowRect = Rectangle.Intersect(rightRect, scissorRect)
                    });
                }
            }
            else
            {
                if (!string.IsNullOrEmpty(item.Value))
                {
                    Vector2 valueMeasure = Measure(font, fr, item.Value, textSize);
                    Text(sb, font, fr, item.Value, new Vector2(panelX + panelW - valueMeasure.X - 10, textY), valueColor, textSize);
                }

                var clippedRow = Rectangle.Intersect(fullRowRect, scissorRect);
                if (!clippedRow.IsEmpty)
                {
                    _rowHitboxes.Add(new RowHitbox { Index = i, RowRect = clippedRow });
                }
            }
        }

        sb.End();
        sb.Begin();
        sb.GraphicsDevice.ScissorRectangle = oldScissor;

        if (maxScroll > 0f)
        {
            float trackTop = listTop + 8 * scaleFactor;
            float trackH = Math.Max(20f, listBottom - trackTop - 8 * scaleFactor);
            float scrollBarH = Math.Max(30f, trackH * (availableHeight / totalHeight));
            float scrollBarY = trackTop + (trackH - scrollBarH) * (ScrollOffset / maxScroll);
            int barW = Math.Max(4, (int)(8 * scaleFactor));
            int barX = sw - barW - Math.Max(6, (int)(12 * scaleFactor));

            DrawRect(sb, pixel, barX, (int)trackTop, barW, (int)trackH, Color.Black * 0.3f);
            DrawRect(sb, pixel, barX, (int)scrollBarY, barW, (int)scrollBarH, Accent * 0.7f);
        }
    }

    /// <summary>Section title row: small pink caps with a divider line.</summary>
    private void DrawSectionHeader(SpriteBatch sb, SpriteFont font, Texture2D pixel, FontRenderer? fr,
                                   string title, float panelX, float panelW, float y, float itemH, float scaleFactor)
    {
        int size = Math.Clamp((int)(19 * scaleFactor), 12, 30);
        size = FitSize(font, fr, title, size, 10, panelW - 40);
        Vector2 sz = Measure(font, fr, title, size);
        float textY = y + itemH - sz.Y - 10 * scaleFactor;
        Text(sb, font, fr, title, new Vector2(panelX + 10, textY), Accent * 0.9f, size);

        int lineX = (int)(panelX + 10 + sz.X + 12);
        int lineY = (int)(textY + sz.Y / 2f);
        DrawRect(sb, pixel, lineX, lineY, (int)(panelX + panelW - 10) - lineX, 1, Accent * 0.35f);
    }

    private void DrawRect(SpriteBatch sb, Texture2D pixel, int x, int y, int w, int h, Color color)
    {
        if (w <= 0 || h <= 0) return;
        sb.Draw(pixel, new Rectangle(x, y, w, h), color);
    }
}

// ── Action Result ────────────────────────────────────────────────────────────

public class SettingsActionResult
{
    public bool NeedsSave = false;
    public bool NeedsStrumlineReposition = false;
    public bool NeedsCameraReset = false;
    public bool NeedsStrumlineAlphaUpdate = false;
    public bool NeedsOpponentAlphaUpdate = false;
    public bool OpenKeyBind = false;
    public bool FpsCapChanged = false;
    public bool OpenArrowsMenu = false;
    public bool OpenSplashMenu = false;
    public bool OpenCountdownMenu = false;
    public bool OpenFontMenu = false;
    public bool OpenHudEditor = false;
    public bool ExitMenu = false;
    public bool ToggleVSync = false;
    public bool ToggleFullscreen = false;
    public bool SongVolumeChanged = false;
    public float AdjustVolume = 0f;
    public float SeekSong = 0f;
}
