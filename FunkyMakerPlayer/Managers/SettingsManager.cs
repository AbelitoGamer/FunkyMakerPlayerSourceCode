using System;
using System.Collections.Generic;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using Microsoft.Xna.Framework.Input;

namespace FunkyMakerPlayer.Managers;

/// <summary>
/// Settings Manager with tabbed interface (Codename Engine style).
/// Unified tabs across platforms: GAMEPLAY, NOTES, HUD, AUDIO & VIDEO, CONTROLS.
/// Rows can be grouped under non-selectable section headers.
/// Supports focus-based keyboard adjustment and full mouse interaction.
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
    }

    private readonly struct RowHitbox
    {
        public int Index { get; init; }
        public Rectangle RowRect { get; init; }
        public Rectangle? LeftArrowRect { get; init; }
        public Rectangle? RightArrowRect { get; init; }
    }

    // ── State ────────────────────────────────────────────────────────────────
    public bool IsActive { get; set; } = false;
    public int Selection { get; set; } = 0;
    public int CurrentTab { get; set; } = 0;  // see the *Tab constants below
    public bool IsFocused { get; set; } = false;
    public float ScrollOffset { get; set; } = 0f;

    private static readonly string[] TabNames = { "GAMEPLAY", "NOTES", "HUD", "AUDIO & VIDEO", "CONTROLS" };
    private const int GameplayTab = 0;
    private const int NotesTab = 1;
    private const int HudTab = 2;
    private const int AudioVideoTab = 3;
    private const int ControlsTab = 4;

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

    public void Unfocus()
    {
        IsFocused = false;
    }

    public void NotifySelectionMoved()
    {
        _followSelectionScroll = true;
    }


    // ── Tab Navigation ───────────────────────────────────────────────────────

    public void NextTab()
    {
        IsFocused = false;
        CurrentTab = (CurrentTab + 1) % TabNames.Length;
        Selection = 0;
        ScrollOffset = 0f;
        _followSelectionScroll = true;
    }

    public void PrevTab()
    {
        IsFocused = false;
        CurrentTab = (CurrentTab - 1 + TabNames.Length) % TabNames.Length;
        Selection = 0;
        ScrollOffset = 0f;
        _followSelectionScroll = true;
    }

    public void SetTab(int tabIndex)
    {
        if (tabIndex < 0 || tabIndex >= TabNames.Length) return;
        IsFocused = false;
        CurrentTab = tabIndex;
        Selection = 0;
        ScrollOffset = 0f;
        _followSelectionScroll = true;
    }

    // ── Unified Data-Driven Tab Definitions ──────────────────────────────────

    private List<SettingItem> BuildTabEntries(Game1? game)
    {
        var entries = new List<SettingItem>();

        switch (CurrentTab)
        {
            case GameplayTab:
                BuildGameplayEntries(game, entries);
                break;
            case NotesTab:
                BuildNotesEntries(entries);
                break;
            case HudTab:
                BuildHudEntries(entries);
                break;
            case AudioVideoTab:
                BuildAudioVideoEntries(game, entries);
                break;
            case ControlsTab:
                BuildControlsEntries(entries);
                break;
        }

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
        Description = "Disabled during multiplayer matches.",
        OnSelect = () => new SettingsActionResult { NeedsSave = false },
    };

    /// <summary>A non-selectable section title inside a tab.</summary>
    private static SettingItem Header(string title) => new SettingItem
    {
        Label = title.ToUpperInvariant(),
        Kind = SettingKind.Header,
    };

    private static string OnOff(bool on) => on ? "ON" : "OFF";

    // ── GAMEPLAY ─────────────────────────────────────────────────────────────

    // "Advanced Timing" rows are collapsed until the player opens them (not saved).
    private bool _showAdvancedTiming;

    private void BuildGameplayEntries(Game1? game, List<SettingItem> entries)
    {
        entries.Add(new SettingItem
        {
            Label = "Downscroll",
            Value = OnOff(NoteRenderer.Downscroll),
            Kind = SettingKind.Toggle,
            Description = "Notes scroll from top to bottom instead of bottom to top.",
            OnSelect = () =>
            {
                NoteRenderer.Downscroll = !NoteRenderer.Downscroll;
                return new SettingsActionResult { NeedsSave = true };
            }
        });

        entries.Add(new SettingItem
        {
            Label = "Middlescroll",
            Value = OnOff(NoteRenderer.Midlescroll),
            Kind = SettingKind.Toggle,
            Description = "Centers your strumline horizontally and splits/dims opponent arrows.",
            OnSelect = () =>
            {
                NoteRenderer.Midlescroll = !NoteRenderer.Midlescroll;
                return new SettingsActionResult { NeedsStrumlineReposition = true, NeedsSave = true };
            }
        });

        entries.Add(new SettingItem
        {
            Label = "Scroll Speed",
            Value = $"{NoteRenderer.ScrollMultiplier:F2}x",
            Kind = SettingKind.Adjustable,
            Description = $"Visual note speed multiplier (default: 1.00x, range: 0.25x - 5.00x). Press ENTER to focus, LEFT/RIGHT to change. In a song: {Controls.Describe(HotkeyAction.ScrollSpeedDown)} slower, {Controls.Describe(HotkeyAction.ScrollSpeedUp)} faster (CONTROLS tab).",
            OnAdjust = (_, delta) =>
            {
                NoteRenderer.ScrollMultiplier = MathF.Round(
                    Math.Clamp(NoteRenderer.ScrollMultiplier + delta * 0.10f, 0.25f, 5.0f), 2);
                return new SettingsActionResult { NeedsSave = true };
            }
        });

        entries.Add(new SettingItem
        {
            Label = "Ghost Tapping",
            Value = OnOff(NoteRenderer.GhostTapping),
            Kind = SettingKind.Toggle,
            Description = "Prevents misses when tapping keys with no notes nearby (OFF = strict timing).",
            OnSelect = () =>
            {
                NoteRenderer.GhostTapping = !NoteRenderer.GhostTapping;
                return new SettingsActionResult { NeedsSave = true };
            }
        });

        // ── Practice ──
        entries.Add(Header("Practice"));

        if (InMultiplayerMatch)
            entries.Add(LockedInMatch("Botplay", "OFF"));
        else
            entries.Add(new SettingItem
            {
                Label = "Botplay",
                Value = OnOff(NoteRenderer.Botplay),
                Kind = SettingKind.Toggle,
                Description = "The CPU plays all your notes. Great for watching charts or testing levels.",
                OnSelect = () =>
                {
                    NoteRenderer.Botplay = !NoteRenderer.Botplay;
                    return new SettingsActionResult { NeedsSave = true };
                }
            });

        if (InMultiplayerMatch)
            entries.Add(LockedInMatch("Timeskip", "OFF"));
        else
        {
            entries.Add(new SettingItem
            {
                Label = "Timeskip",
                Value = Game1.TimeskipSeconds <= 0f ? "OFF" : $"{Game1.TimeskipSeconds:F0}s",
                Kind = SettingKind.Adjustable,
                Description = $"Seconds skipped per press of the Timeskip keys ({Controls.Describe(HotkeyAction.TimeskipForward)} forward, {Controls.Describe(HotkeyAction.TimeskipBackward)} back). Set it to OFF to disable Timeskip. Not available in multiplayer.",
                OnAdjust = (_, delta) =>
                {
                    Game1.TimeskipSeconds = MathF.Round(Math.Clamp(Game1.TimeskipSeconds + delta * 5f, 0f, 60f), 0);
                    return new SettingsActionResult { NeedsSave = true };
                }
            });

            if (Game1.TimeskipEnabled && game?._level != null)
            {
                entries.Add(new SettingItem
                {
                    Label = "  Seek Current Song",
                    Value = "Scrub",
                    Kind = SettingKind.Adjustable,
                    Description = "Scrub backward/forward through the current song by the Timeskip amount (the song stays paused).",
                    OnAdjust = (_, delta) => new SettingsActionResult
                    {
                        SeekSong = delta * Game1.TimeskipSeconds,
                        NeedsSave = false
                    }
                });
            }
        }

        // ── Advanced timing (collapsed) ──
        entries.Add(new SettingItem
        {
            Label = "Advanced Timing",
            Value = _showAdvancedTiming ? "[Hide]" : "[Show]",
            Kind = SettingKind.Action,
            Description = "Fine-tuning for very strict timing: Input Buffer and Safe Frames. The defaults suit almost everyone.",
            OnSelect = () =>
            {
                _showAdvancedTiming = !_showAdvancedTiming;
                return new SettingsActionResult();
            }
        });

        if (_showAdvancedTiming)
        {
            entries.Add(new SettingItem
            {
                Label = "  Input Buffer",
                Value = $"{(int)Math.Round(NoteRenderer.InputBuffer * 1000)}ms",
                Kind = SettingKind.Adjustable,
                Description = "How early a key press may still hit an upcoming note (default and maximum: 10ms). Lower it for stricter timing.",
                OnAdjust = (_, delta) =>
                {
                    NoteRenderer.InputBuffer = MathF.Round(
                        Math.Clamp(NoteRenderer.InputBuffer + delta * 0.002f, 0f, 0.010f), 3);
                    return new SettingsActionResult { NeedsSave = true };
                }
            });

            entries.Add(new SettingItem
            {
                Label = "  Safe Frames",
                Value = $"{(int)Math.Round(NoteRenderer.SafeFramesTime * 1000)}ms",
                Kind = SettingKind.Adjustable,
                Description = "Time at the very start of a song where misses don't count (default and maximum: 83ms, ~5 frames). Lower it for stricter play.",
                OnAdjust = (isLeft, _) =>
                {
                    float next = NoteRenderer.SafeFramesTime + (isLeft ? -0.017f : 0.017f);
                    if (!isLeft && next > 0.080f) next = 0.083f;
                    NoteRenderer.SafeFramesTime = MathF.Round(Math.Clamp(next, 0f, 0.083f), 3);
                    return new SettingsActionResult { NeedsSave = true };
                }
            });
        }

        BuildOsuEntries(entries);
    }

    // ── osu! songs (fork, Osu/OsuSettings.cs) ────────────────────────────────

    private static SettingItem OsuToggle(string label, bool value, string description, Action toggle) => new()
    {
        Label = label,
        Value = OnOff(value),
        Kind = SettingKind.Toggle,
        Description = description,
        OnSelect = () => { toggle(); return new SettingsActionResult { NeedsSave = true }; }
    };

    private void BuildOsuEntries(List<SettingItem> entries)
    {
        entries.Add(Header("osu! songs"));
        entries.Add(new SettingItem
        {
            Label = "Note Skin",
            Value = Osu.OsuSettings.PreferBeatmapSkins ? "Beatmap's" : "Mine",
            Kind = SettingKind.Adjustable,
            Description = "Beatmap's: a song that brings its own mania note images uses them (only the images, like osu!: never its skin.ini). Mine: always your Note Style.",
            OnSelect = () => { Osu.OsuSettings.PreferBeatmapSkins = !Osu.OsuSettings.PreferBeatmapSkins; return new SettingsActionResult { NeedsSave = true }; },
            OnAdjust = (_, _) => { Osu.OsuSettings.PreferBeatmapSkins = !Osu.OsuSettings.PreferBeatmapSkins; return new SettingsActionResult { NeedsSave = true }; }
        });
        entries.Add(OsuToggle("Scroll Speed Changes", Osu.OsuSettings.ScrollSpeedChanges,
            "The map's scroll speed changes (SV) and BPM changes move the notes faster or slower, like osu!mania. OFF: notes always scroll at your Scroll Speed.",
            () => Osu.OsuSettings.ScrollSpeedChanges = !Osu.OsuSettings.ScrollSpeedChanges));
        entries.Add(OsuToggle("Hitsounds", Osu.OsuSettings.Hitsounds,
            "Notes play the map's hitsounds / keysounds when you press (and your osu! note skin's sounds, if it has them).",
            () => Osu.OsuSettings.Hitsounds = !Osu.OsuSettings.Hitsounds));
        entries.Add(new SettingItem
        {
            Label = "  Hitsound Volume",
            Value = $"{(int)Math.Round(Osu.OsuSettings.HitsoundVolume * 100)}%",
            Kind = SettingKind.Adjustable,
            Description = "Volume of osu! hitsounds (scaled by Master Volume).",
            OnAdjust = (_, delta) =>
            {
                Osu.OsuSettings.HitsoundVolume = MathF.Round(Math.Clamp(Osu.OsuSettings.HitsoundVolume + delta * 0.05f, 0f, 1f), 2);
                return new SettingsActionResult { NeedsSave = true };
            }
        });
        entries.Add(OsuToggle("Video", Osu.OsuSettings.Video,
#if ANDROID
            "Plays the map's background video with the device's decoders (mp4 / mkv / webm; not avi, flv or wmv).",
#else
            "Plays the map's background video (bundled ffmpeg; or one on PATH / in mods/tools).",
#endif
            () => Osu.OsuSettings.Video = !Osu.OsuSettings.Video));
        entries.Add(OsuToggle("Storyboard", Osu.OsuSettings.Storyboard,
            "Plays the map's storyboard (.osb / [Events]) behind and over the notes.",
            () => Osu.OsuSettings.Storyboard = !Osu.OsuSettings.Storyboard));
        entries.Add(new SettingItem
        {
            Label = "Background Dim",
            Value = $"{(int)Math.Round(Osu.OsuSettings.BackgroundDim * 100)}%",
            Kind = SettingKind.Adjustable,
            Description = "How much the background, video and storyboard are darkened (the storyboard's Overlay layer too).",
            OnAdjust = (_, delta) =>
            {
                Osu.OsuSettings.BackgroundDim = MathF.Round(Math.Clamp(Osu.OsuSettings.BackgroundDim + delta * 0.05f, 0f, 1f), 2);
                return new SettingsActionResult { NeedsSave = true };
            }
        });
    }

    // ── NOTES ────────────────────────────────────────────────────────────────

    private void BuildNotesEntries(List<SettingItem> entries)
    {
        entries.Add(new SettingItem
        {
            Label = "Note Style",
            Value = Game1.NoteStyleName,
            Kind = SettingKind.Action,
            Description = "Visual style pack for notes and receptors (Assets or mods/notestyles/).",
            OnSelect = () =>
            {
                IsActive = false;
                return new SettingsActionResult { OpenArrowsMenu = true, NeedsSave = false };
            }
        });

        entries.Add(new SettingItem
        {
            Label = "Note Splash",
            Value = Game1.NoteSplashStyleName,
            Kind = SettingKind.Action,
            Description = "Splash particle effect triggered on Sick! hits (mods/notesplashes/).",
            OnSelect = () =>
            {
                IsActive = false;
                return new SettingsActionResult { OpenSplashMenu = true, NeedsSave = false };
            }
        });

        entries.Add(new SettingItem
        {
            Label = "Countdown Style",
            Value = Game1.SelectedCountdown,
            Kind = SettingKind.Action,
            Description = "Visual 3-2-1-GO countdown pack before the song starts (mods/countdowns/).",
            OnSelect = () =>
            {
                IsActive = false;
                return new SettingsActionResult { OpenCountdownMenu = true, NeedsSave = false };
            }
        });

        entries.Add(Header("Readability"));

        entries.Add(new SettingItem
        {
            Label = "Lane Underlay",
            Value = NoteRenderer.LaneUnderlay <= 0f ? "OFF" : $"{(int)MathF.Round(NoteRenderer.LaneUnderlay * 100)}%",
            Kind = SettingKind.Adjustable,
            Description = "Black backdrop behind your strumline so notes stand out from the background (0% = off, up to 75%). Press ENTER to focus, LEFT/RIGHT to change.",
            OnAdjust = (_, delta) =>
            {
                NoteRenderer.LaneUnderlay = MathF.Round(
                    Math.Clamp(NoteRenderer.LaneUnderlay + delta * 0.05f, 0f, NoteRenderer.LaneUnderlayMax), 2);
                return new SettingsActionResult { NeedsSave = true };
            }
        });

        entries.Add(new SettingItem
        {
            Label = "Show Key Labels",
            Value = OnOff(NoteRenderer.ShowKeyLabels),
            Kind = SettingKind.Toggle,
            Description = "Shows the key for each lane on top of your strums.",
            OnSelect = () =>
            {
                NoteRenderer.ShowKeyLabels = !NoteRenderer.ShowKeyLabels;
                return new SettingsActionResult { NeedsSave = true };
            }
        });

        entries.Add(Header("Strumlines"));

        entries.Add(new SettingItem
        {
            Label = "Strumlines",
            Value = OnOff(NoteRenderer.ShowStrumlines),
            Kind = SettingKind.Toggle,
            Description = "Master toggle to show or hide all note strumlines at once.",
            OnSelect = () =>
            {
                NoteRenderer.ShowStrumlines = !NoteRenderer.ShowStrumlines;
                return new SettingsActionResult { NeedsStrumlineAlphaUpdate = true, NeedsSave = true };
            }
        });

        if (NoteRenderer.ShowStrumlines)
        {
            entries.Add(new SettingItem
            {
                Label = "  Player Strumline",
                Value = OnOff(!NoteRenderer.HidePlayerStrumline),
                Kind = SettingKind.Toggle,
                Description = "Show or hide your own strumline (receptors, notes, and splashes).",
                OnSelect = () =>
                {
                    NoteRenderer.HidePlayerStrumline = !NoteRenderer.HidePlayerStrumline;
                    return new SettingsActionResult { NeedsStrumlineAlphaUpdate = true, NeedsSave = true };
                }
            });

            entries.Add(new SettingItem
            {
                Label = "  Opponent Strumline",
                Value = OnOff(!Game1.HideOpponentStrumline),
                Kind = SettingKind.Toggle,
                Description = "Show or hide the opponent's notes. Same setting as Show Opponent Notes in the online settings, where you can also set their opacity.",
                OnSelect = () =>
                {
                    Game1.HideOpponentStrumline = !Game1.HideOpponentStrumline;
                    return new SettingsActionResult { NeedsOpponentAlphaUpdate = true, NeedsSave = true };
                }
            });

            entries.Add(new SettingItem
            {
                Label = "  Extra Strumlines",
                Value = OnOff(!Game1.HideExtraStrumlines),
                Kind = SettingKind.Toggle,
                Description = "Show or hide extra strumlines beyond player and opponent.",
                OnSelect = () =>
                {
                    Game1.HideExtraStrumlines = !Game1.HideExtraStrumlines;
                    return new SettingsActionResult { NeedsStrumlineAlphaUpdate = true, NeedsSave = true };
                }
            });
        }

        entries.Add(Header("Effects"));

        entries.Add(new SettingItem
        {
            Label = "Arrow Effects",
            Value = OnOff(ArrowEffectManager.Enabled),
            Kind = SettingKind.Toggle,
            Description = "Cosmetic beat-synced animations on your receptor arrows (off by default).",
            OnSelect = () =>
            {
                ArrowEffectManager.Enabled = !ArrowEffectManager.Enabled;
                return new SettingsActionResult { NeedsSave = true };
            }
        });

        if (ArrowEffectManager.Enabled)
        {
            entries.Add(new SettingItem
            {
                Label = "  Effect Type",
                Value = ArrowEffectManager.GetSelectedEffectName(),
                Kind = SettingKind.Adjustable,
                Description = "Which animation plays: Random, Glow, Vertical Bounce, Spin 360, Horizontal Bounce, or Dizzy.",
                OnAdjust = (isLeft, _) =>
                {
                    int newEffect = ArrowEffectManager.SelectedEffect + (isLeft ? -1 : 1);
                    ArrowEffectManager.SelectedEffect = (newEffect + 6) % 6;
                    return new SettingsActionResult { NeedsSave = true };
                }
            });
        }
    }

    // ── HUD ──────────────────────────────────────────────────────────────────

    private static readonly string[] TimerPresets = { "Legacy", "HUD Editor", "Top", "Left", "Middle", "Right", "Strumline" };
    private static readonly string[] TimerTextModes = { "Time Left", "Time Elapsed", "Song Name", "Song Name + Difficulty", "None" };

    private void BuildHudEntries(List<SettingItem> entries)
    {
        var hud = HudSettings.Instance;

        #if !ANDROID
        entries.Add(new SettingItem
        {
            Label = "HUD Editor",
            Value = "[Open]",
            Kind = SettingKind.Action,
            Description = "Open the interactive HUD Editor to move, resize, and toggle individual HUD elements.",
            OnSelect = () =>
            {
                IsActive = false;
                return new SettingsActionResult { OpenHudEditor = true, NeedsSave = false };
            }
        });
        #endif

        entries.Add(new SettingItem
        {
            Label = "Show HUD",
            Value = OnOff(hud.MasterHudVisible),
            Kind = SettingKind.Toggle,
            Description = "Master toggle: shows or hides the entire gameplay HUD at once, strumlines included.",
            OnSelect = () =>
            {
                hud.MasterHudVisible = !hud.MasterHudVisible;
                hud.Save();
                return new SettingsActionResult { NeedsSave = true };
            }
        });

        if (!hud.MasterHudVisible) return;

        entries.Add(new SettingItem
        {
            Label = "  Combo & Judgements",
            Value = OnOff(Game1.ShowCombo),
            Kind = SettingKind.Toggle,
            Description = "Show the combo counter and the judgement popups (Sick!/Good/Bad/Shit) on hits.",
            OnSelect = () =>
            {
                Game1.ShowCombo = !Game1.ShowCombo;
                hud.ComboVisible = Game1.ShowCombo;
                hud.JudgementVisible = Game1.ShowCombo;
                hud.Save();
                return new SettingsActionResult { NeedsSave = true };
            }
        });

        entries.Add(new SettingItem
        {
            Label = "  Score & Stats",
            Value = OnOff(hud.ScoreVisible || hud.MissesVisible || hud.AccuracyVisible),
            Kind = SettingKind.Toggle,
            Description = "Show or hide the Score, Misses, and Accuracy counters during gameplay.",
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

        entries.Add(new SettingItem
        {
            Label = "  Health Bar",
            Value = OnOff(hud.HealthBarVisible),
            Kind = SettingKind.Toggle,
            Description = "Show or hide the health bar and the character icons during gameplay.",
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

        if (hud.HealthBarVisible)
        {
            entries.Add(new SettingItem
            {
                Label = "    Health Icons",
                Value = Game1.IconMode == 0 ? "Move with bar" : "Pinned to ends",
                Kind = SettingKind.Toggle,
                Description = "Move with bar: the character icons slide along the health bar as health changes (FNF style). Pinned to ends: the icons stay at the two ends of the bar.",
                OnSelect = () =>
                {
                    Game1.IconMode = (Game1.IconMode + 1) % 2;
                    return new SettingsActionResult { NeedsSave = true };
                }
            });
        }

        entries.Add(new SettingItem
        {
            Label = "  Song Title",
            Value = OnOff(hud.SongInfoVisible),
            Kind = SettingKind.Toggle,
            Description = "Show or hide the level/song title in the top-left corner.",
            OnSelect = () =>
            {
                hud.SongInfoVisible = !hud.SongInfoVisible;
                hud.Save();
                return new SettingsActionResult { NeedsSave = true };
            }
        });

        entries.Add(new SettingItem
        {
            Label = "  Botplay/Speed Indicator",
            Value = OnOff(hud.StatusInfoVisible),
            Kind = SettingKind.Toggle,
            Description = "Show or hide the bottom-left status line: [BOTPLAY]/[MANUAL], scroll direction and scroll speed.",
            OnSelect = () =>
            {
                hud.StatusInfoVisible = !hud.StatusInfoVisible;
                hud.Save();
                return new SettingsActionResult { NeedsSave = true };
            }
        });

        entries.Add(new SettingItem
        {
            Label = "  Song Timer",
            Value = OnOff(Game1.ShowTimer),
            Kind = SettingKind.Toggle,
            Description = "Show or hide the song progress timer bar.",
            OnSelect = () =>
            {
                Game1.ShowTimer = !Game1.ShowTimer;
                hud.Save();
                return new SettingsActionResult { NeedsSave = true };
            }
        });

        if (!Game1.ShowTimer) return;

        entries.Add(new SettingItem
        {
            Label = "    Position",
            Value = Game1.TimerPreset,
            Kind = SettingKind.Adjustable,
            Description = "Where the timer bar sits: HUD Editor (wherever you placed it in the HUD Editor), Legacy, Top, Left, Middle, Right, or Strumline. Force Top appears for the automatic positions (Top to Strumline).",
            OnAdjust = (isLeft, _) =>
            {
                int idx = Array.IndexOf(TimerPresets, Game1.TimerPreset);
                if (idx < 0) idx = 1;
                idx = (idx + (isLeft ? -1 : 1) + TimerPresets.Length) % TimerPresets.Length;
                Game1.TimerPreset = TimerPresets[idx];
                return new SettingsActionResult { NeedsSave = true };
            }
        });

        entries.Add(new SettingItem
        {
            Label = "    Color",
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
            Description = "Fill color of the timer bar (Player / Opponent use that character's health bar color).",
            OnAdjust = (isLeft, _) =>
            {
                const int count = 7;
                Game1.TimerColorPreset = (Game1.TimerColorPreset + (isLeft ? -1 : 1) + count) % count;
                return new SettingsActionResult { NeedsSave = true };
            }
        });

        entries.Add(new SettingItem
        {
            Label = "    Text",
            Value = Game1.TimerTextMode,
            Kind = SettingKind.Adjustable,
            Description = "Text shown inside the timer bar: Time Left, Time Elapsed, Song Name, Song Name + Difficulty (star rating), or None.",
            OnAdjust = (isLeft, _) =>
            {
                int idx = Array.IndexOf(TimerTextModes, Game1.TimerTextMode);
                if (idx < 0) idx = 0;
                idx = (idx + (isLeft ? -1 : 1) + TimerTextModes.Length) % TimerTextModes.Length;
                Game1.TimerTextMode = TimerTextModes[idx];
                return new SettingsActionResult { NeedsSave = true };
            }
        });

        // Force Top only matters for the automatic positions (you place the other two yourself)
        if (Game1.TimerPreset != "Legacy" && Game1.TimerPreset != "HUD Editor")
        {
            entries.Add(new SettingItem
            {
                Label = "    Force Top",
                Value = OnOff(Game1.TimerForceTop),
                Kind = SettingKind.Toggle,
                Description = "Always place the timer bar at the top, even with Downscroll on.",
                OnSelect = () =>
                {
                    Game1.TimerForceTop = !Game1.TimerForceTop;
                    return new SettingsActionResult { NeedsSave = true };
                }
            });
        }
    }

    // ── AUDIO & VIDEO ────────────────────────────────────────────────────────

    private void BuildAudioVideoEntries(Game1? game, List<SettingItem> entries)
    {
        entries.Add(Header("Audio"));

        entries.Add(new SettingItem
        {
            Label = "Master Volume",
            Value = $"{(int)Math.Round(Game1.MasterVolume * 100)}%",
            Kind = SettingKind.Adjustable,
            Description = $"Overall volume of every sound in the game: songs, menu music and sound effects. {Controls.Describe(HotkeyAction.VolumeDown)} / {Controls.Describe(HotkeyAction.VolumeUp)} change it anytime.",
            OnAdjust = (_, delta) => new SettingsActionResult { AdjustVolume = delta, NeedsSave = true }
        });

        entries.Add(new SettingItem
        {
            Label = "  Music Volume",
            Value = $"{(int)Math.Round(Game1.SongVolume * 100)}%",
            Kind = SettingKind.Adjustable,
            Description = "Volume of the song while you play (scaled by Master Volume).",
            OnAdjust = (_, delta) =>
            {
                Game1.SongVolume = MathF.Round(Math.Clamp(Game1.SongVolume + delta * 0.05f, 0f, 1f), 2);
                return new SettingsActionResult { SongVolumeChanged = true, NeedsSave = true };
            }
        });

        entries.Add(new SettingItem
        {
            Label = "  Menu Music Volume",
            Value = $"{(int)Math.Round(OnlineMenuMusic.Volume * 100)}%",
            Kind = SettingKind.Adjustable,
            Description = "Volume of the menu background music (scaled by Master Volume).",
            OnAdjust = (_, delta) =>
            {
                OnlineMenuMusic.Volume = MathF.Round(Math.Clamp(OnlineMenuMusic.Volume + delta * 0.05f, 0f, 1f), 2);
                return new SettingsActionResult { NeedsSave = true };
            }
        });

        entries.Add(new SettingItem
        {
            Label = "  SFX Volume",
            Value = $"{(int)Math.Round(UI.UiSfx.Volume * 100)}%",
            Kind = SettingKind.Adjustable,
            Description = "Volume of menu, countdown and miss sound effects (scaled by Master Volume).",
            OnAdjust = (_, delta) =>
            {
                UI.UiSfx.Volume = MathF.Round(Math.Clamp(UI.UiSfx.Volume + delta * 0.05f, 0f, 1f), 2);
                return new SettingsActionResult { NeedsSave = true };
            }
        });

        entries.Add(Header("Display"));

        #if !ANDROID
        entries.Add(new SettingItem
        {
            Label = "Fullscreen",
            Value = OnOff(game != null && game._isFullscreen),
            Kind = SettingKind.Toggle,
            Description = $"Toggle borderless fullscreen mode. Press {Controls.Describe(HotkeyAction.ToggleFullscreen)} anytime to toggle quickly.",
            OnSelect = () => new SettingsActionResult { ToggleFullscreen = true, NeedsSave = false }
        });
        #endif

        entries.Add(new SettingItem
        {
            Label = "VSync",
            Value = OnOff(game != null && game._graphics.SynchronizeWithVerticalRetrace),
            Kind = SettingKind.Toggle,
            Description = "Synchronizes frame rate with monitor refresh rate to prevent screen tearing.",
            OnSelect = () => new SettingsActionResult { ToggleVSync = true, NeedsSave = true }
        });

        entries.Add(new SettingItem
        {
            Label = "Render Resolution",
            Value = Game1.NativeRenderResolution ? "Native" : "720p",
            Kind = SettingKind.Toggle,
            Description = "Native renders gameplay at your window/screen resolution (sharpest). 720p is the classic fixed resolution, upscaled (faster on weak GPUs).",
            OnSelect = () =>
            {
                Game1.NativeRenderResolution = !Game1.NativeRenderResolution;
                return new SettingsActionResult { NeedsSave = true };
            }
        });

        entries.Add(new SettingItem
        {
            Label = "FPS Cap",
            Value = Game1.FpsCap > 0 ? $"{Game1.FpsCap} FPS" : "Unlimited",
            Kind = SettingKind.Adjustable,
            Description = "Maximum frame rate (Unlimited = as fast as possible). With VSync ON the monitor refresh rate also limits it. Press ENTER to focus, LEFT/RIGHT to change.",
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

        entries.Add(new SettingItem
        {
            Label = "FPS Counter",
            Value = OnOff(Game1.ShowFpsCounter),
            Kind = SettingKind.Toggle,
            Description = "Display frames per second (FPS) and VSync status in the top-right corner.",
            OnSelect = () =>
            {
                Game1.ShowFpsCounter = !Game1.ShowFpsCounter;
                return new SettingsActionResult { NeedsSave = true };
            }
        });

        entries.Add(new SettingItem
        {
            Label = "Font Style",
            Value = Game1.CurrentFontPath is { } fontPath ? System.IO.Path.GetFileNameWithoutExtension(fontPath) : "Default",
            Kind = SettingKind.Action,
            Description = "Choose the UI font used throughout menus. Add custom .ttf/.otf fonts to mods/fonts/.",
            OnSelect = () =>
            {
                IsActive = false;
                return new SettingsActionResult { OpenFontMenu = true, NeedsSave = false };
            }
        });

        entries.Add(Header("Other"));

        entries.Add(new SettingItem
        {
            Label = "Debug Info",
            Value = OnOff(Game1.ShowDebugInfo),
            Kind = SettingKind.Toggle,
            Description = "Show technical song debug info (BPM, beat count, camera position, and events).",
            OnSelect = () =>
            {
                Game1.ShowDebugInfo = !Game1.ShowDebugInfo;
                return new SettingsActionResult { NeedsSave = true };
            }
        });

        #if !ANDROID
        entries.Add(Header("Storage"));
        AddModsFolderEntry(game, entries);
        #endif
    }

    // ── CONTROLS tab (fork): note keys + every rebindable hotkey ─────────────

    // Key capture: Enter on a hotkey row waits for one key (with optional Ctrl/Shift/Alt).
    private HotkeyAction? _captureAction;

    /// <summary>True while a CONTROLS row is waiting for a key (Game1 then withholds keys from the rest of the game).</summary>
    public bool IsCapturingKey => _captureAction != null || _modsMoveTarget != null;

    private void BuildControlsEntries(List<SettingItem> entries)
    {
        #if !ANDROID
        entries.Add(new SettingItem
        {
            Label = "Note Keys",
            Value = $"{Controls.KeyName(NoteRenderer.PlayerKeys[0])} / {Controls.KeyName(NoteRenderer.PlayerKeys[1])} / {Controls.KeyName(NoteRenderer.PlayerKeys[2])} / {Controls.KeyName(NoteRenderer.PlayerKeys[3])}",
            Kind = SettingKind.Action,
            Description = "Customize keyboard controls for Left, Down, Up, and Right note lanes.",
            OnSelect = () =>
            {
                IsActive = false;
                return new SettingsActionResult { OpenKeyBind = true, NeedsSave = false };
            }
        });
        #else
        entries.Add(new SettingItem
        {
            Label = "Touch Hitbox Colors",
            Value = OnOff(!Game1.HideTouchHitboxColors),
            Kind = SettingKind.Toggle,
            Description = "Tint the touch lanes while you press them. OFF makes the touch hitboxes completely invisible.",
            OnSelect = () =>
            {
                Game1.HideTouchHitboxColors = !Game1.HideTouchHitboxColors;
                return new SettingsActionResult { NeedsSave = true };
            }
        });
        entries.Add(new SettingItem
        {
            Label = "  Touch Hitbox Opacity",
            Value = $"{(int)Math.Round(Game1.TouchHitboxOpacity * 100)}%",
            Kind = SettingKind.Adjustable,
            Description = "How visible a touch lane is while you press it (0-100%). Default 30%.",
            OnAdjust = (_, delta) =>
            {
                Game1.TouchHitboxOpacity = MathF.Round(Math.Clamp(Game1.TouchHitboxOpacity + delta * 0.05f, 0f, 1f), 2);
                return new SettingsActionResult { NeedsSave = true };
            }
        });
        entries.Add(new SettingItem
        {
            Label = "Menu Buttons",
            Value = Game1.MenuButtonsMode switch { 1 => "ON", 2 => "OFF", _ => "AUTO" },
            Kind = SettingKind.Adjustable,
            Description = "On-screen D-pad and A / B in menus. AUTO: hidden where tapping works (back gesture = B). ON: always. OFF: never.",
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
                entries.Add(Header(group));
            }

            var action = info.Action;
            bool capturing = _captureAction == action;
            entries.Add(new SettingItem
            {
                Label = info.Label,
                Value = capturing
                    ? "Press key... (Esc cancel, Backspace none)"
                    : Controls.Describe(action),
                Kind = SettingKind.Action,
                Description = capturing
                    ? "Hold Ctrl / Shift / Alt with the key for a combination. Esc and the arrow / Enter keys used to navigate menus can't be bound alone."
                    : info.Description + "  ENTER: rebind.",
                OnSelect = () =>
                {
                    _captureAction = action;
                    return new SettingsActionResult { NeedsSave = false };
                }
            });
        }

        entries.Add(Header("Reset"));

        entries.Add(new SettingItem
        {
            Label = "Reset Controls",
            Value = "",
            Kind = SettingKind.Action,
            Description = "Restores every hotkey above to its default (note keys are not changed).",
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
        if (_modsMoveTarget != null)
        {
            UpdateModsMovePrompt(kb, prev);
            return false;
        }

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
            if (binding.ModifierCount == 0 && key is Keys.Up or Keys.Down or Keys.Left or Keys.Right or Keys.Enter)
            {
                UI.UiSfx.Play(UI.UiSound.SliderEnd);
                return false;
            }

            // One key per rebind: it replaces both slots (defaults may still carry an alternate)
            Controls.Set(action, 0, binding);
            Controls.Set(action, 1, KeyBinding.None);
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
        _modsMoveTarget = null;
    }

    // ── Mods folder (desktop, fork) ──────────────────────────────────────────

    // Explanations shown in the row description and in the confirmation window.
    private const string ModsAppDataInfo =
        "AppData (recommended): one mods folder shared by every copy of the game on this computer, " +
        "kept safe when you update or reinstall.";
    private const string ModsNextToGameInfo =
        "Next to the game: the mods folder sits beside the game's .exe, so levels and mods are easier to find, " +
        "edit and manage by hand. Downside: each copy of the game has its own separate levels, which are no " +
        "longer shared like in AppData. If you don't manage level files often, move it back to AppData.";

    // Confirmation window for moving the mods folder (null = closed).
    private ModsStorage.Location? _modsMoveTarget;
    private Rectangle _modsMoveYesRect = Rectangle.Empty;
    private Rectangle _modsMoveNoRect = Rectangle.Empty;

    /// <summary>True while the "Move mods folder?" window is open (it takes all input).</summary>
    public bool IsModsMovePromptOpen => _modsMoveTarget != null;

    private static string ModsLocationName(ModsStorage.Location l) =>
        l == ModsStorage.Location.AppData ? "AppData" : "Next to game";

    /// <summary>Settings → AUDIO &amp; VIDEO → Storage → Move Mods Folder.</summary>
    private void AddModsFolderEntry(Game1? game, List<SettingItem> entries)
    {
        var current = ModsStorage.Current;
        var other = current == ModsStorage.Location.AppData ? ModsStorage.Location.NextToGame : ModsStorage.Location.AppData;
        bool busy = game?.HasActiveLevel == true;

        string value = ModsStorage.IsMoving ? (ModsStorage.Status ?? "Moving...") : ModsLocationName(current);
        string description = ModsStorage.IsMoving
            ? "Moving your levels and mods. Please wait..."
            : busy
                ? "Close the current level first (go back to the menus) to move the mods folder."
                : $"Your levels, note styles, splashes, countdowns and fonts are in: {ModsStorage.CurrentPath}. " +
                  (current == ModsStorage.Location.AppData ? ModsAppDataInfo : ModsNextToGameInfo) +
                  $" ENTER: move it to {ModsLocationName(other)}." +
                  (ModsStorage.Status is { } st ? $" Last move: {st}." : "");

        entries.Add(new SettingItem
        {
            Label = "Move Mods Folder",
            Value = value,
            Kind = SettingKind.Action,
            Description = description,
            OnSelect = () =>
            {
                if (ModsStorage.IsMoving || busy) return new SettingsActionResult();
                _modsMoveTarget = other;   // opens the confirmation window
                return new SettingsActionResult();
            }
        });
    }

    /// <summary>Keyboard for the confirmation window: Y / Enter moves, N / Esc cancels.</summary>
    private void UpdateModsMovePrompt(KeyboardState kb, KeyboardState prev)
    {
        bool Pressed(Keys k) => kb.IsKeyDown(k) && !prev.IsKeyDown(k);
        if (Pressed(Keys.Y) || Pressed(Keys.Enter))
            ConfirmModsMove(true);
        else if (Pressed(Keys.N) || Pressed(Keys.Escape))
            ConfirmModsMove(false);
    }

    private void ConfirmModsMove(bool move)
    {
        if (_modsMoveTarget is not { } target) return;
        _modsMoveTarget = null;
        if (move && !ModsStorage.IsMoving)
        {
            _ = ModsStorage.MoveToAsync(target);
            UI.UiSfx.Play(UI.UiSound.Confirm);
        }
        else
        {
            UI.UiSfx.Play(UI.UiSound.Back);
        }
    }

    private void DrawModsMovePrompt(SpriteBatch sb, SpriteFont font, Texture2D pixel, FontRenderer? fr,
                                    int sw, int sh, float scaleFactor)
    {
        if (_modsMoveTarget is not { } target) return;
        var from = ModsStorage.Current;
        Color accent = new Color(0xFF, 0x44, 0x88);

        DrawRect(sb, pixel, 0, 0, sw, sh, Color.Black * 0.7f);

        int boxW = Math.Min((int)(820 * scaleFactor), sw - 40);
        int pad = Math.Max(14, (int)(22 * scaleFactor));
        int titleSize = Math.Max(18, (int)(32 * scaleFactor));
        int bodySize = Math.Max(12, (int)(19 * scaleFactor));
        float maxW = boxW - pad * 2;

        Vector2 Measure(string t, int size) => fr != null ? fr.MeasureString(t, size) : font.MeasureString(t) * (size / 36f);
        void Text(string t, float x, float y, Color c, int size)
        {
            if (fr != null) fr.DrawString(sb, t, new Vector2(x, y), c, size);
            else sb.DrawString(font, t, new Vector2(x, y), c, 0f, Vector2.Zero, size / 36f, SpriteEffects.None, 0f);
        }

        // (text, color) lines wrapped to the box width; null text = small gap
        var lines = new List<(string? Text, Color Color)>();
        void Para(string text, Color c)
        {
            string line = "";
            foreach (var word in text.Split(' '))
            {
                string test = line.Length == 0 ? word : line + " " + word;
                if (Measure(test, bodySize).X > maxW && line.Length > 0)
                {
                    lines.Add((line, c));
                    line = word;
                }
                else line = test;
            }
            if (line.Length > 0) lines.Add((line, c));
        }

        Para($"From ({ModsLocationName(from)}): {ModsStorage.PathFor(from)}", Color.White * 0.7f);
        Para($"To ({ModsLocationName(target)}): {ModsStorage.PathFor(target)}", Color.White * 0.95f);
        lines.Add((null, Color.White));
        Para(target == ModsStorage.Location.AppData ? ModsAppDataInfo : ModsNextToGameInfo, new Color(255, 225, 150));
        lines.Add((null, Color.White));
        Para("Everything is moved: levels, note styles, splashes, countdowns and fonts. Identical files are merged; " +
             "different files with the same name are kept as \"name (2)\".", Color.White * 0.75f);

        float lineH = Measure("Ag", bodySize).Y + 4;
        float titleH = Measure("Ag", titleSize).Y;
        int btnH = Math.Max(30, (int)(40 * scaleFactor));
        float bodyH = 0;
        foreach (var l in lines) bodyH += l.Text == null ? lineH * 0.5f : lineH;
        int boxH = (int)(pad + titleH + pad * 0.6f + bodyH + pad + btnH + pad);
        int boxX = (sw - boxW) / 2;
        int boxY = Math.Max(10, (sh - boxH) / 2);

        DrawRect(sb, pixel, boxX, boxY, boxW, boxH, new Color(22, 18, 32));
        DrawRect(sb, pixel, boxX, boxY, boxW, 3, accent);
        DrawRect(sb, pixel, boxX, boxY + boxH - 2, boxW, 2, accent * 0.5f);

        float y = boxY + pad;
        Text("Move mods folder?", boxX + pad, y, accent, titleSize);
        y += titleH + pad * 0.6f;
        foreach (var l in lines)
        {
            if (l.Text == null) { y += lineH * 0.5f; continue; }
            Text(l.Text, boxX + pad, y, l.Color, bodySize);
            y += lineH;
        }

        // Buttons
        const string yes = "Y / ENTER: Move";
        const string no = "N / ESC: Cancel";
        int btnW = Math.Max((int)Measure(yes, bodySize).X, (int)Measure(no, bodySize).X) + pad * 2;
        int btnY = boxY + boxH - pad - btnH;
        int yesX = boxX + boxW / 2 - pad / 2 - btnW;
        int noX = boxX + boxW / 2 + pad / 2;
        _modsMoveYesRect = new Rectangle(yesX, btnY, btnW, btnH);
        _modsMoveNoRect = new Rectangle(noX, btnY, btnW, btnH);

        var buttons = new[]
        {
            (Rect: _modsMoveYesRect, Label: yes, Color: new Color(0, 220, 140)),
            (Rect: _modsMoveNoRect, Label: no, Color: Color.White * 0.8f),
        };
        foreach (var b in buttons)
        {
            bool hover = b.Rect.Contains(_lastMousePos);
            DrawRect(sb, pixel, b.Rect.X, b.Rect.Y, b.Rect.Width, b.Rect.Height, b.Color * (hover ? 0.30f : 0.14f));
            DrawRect(sb, pixel, b.Rect.X, b.Rect.Y + b.Rect.Height - 2, b.Rect.Width, 2, b.Color);
            var sz = Measure(b.Label, bodySize);
            Text(b.Label, b.Rect.X + (b.Rect.Width - sz.X) / 2f, b.Rect.Y + (b.Rect.Height - sz.Y) / 2f,
                hover ? Color.White : b.Color, bodySize);
        }
    }

    // ── Action & Adjustment Dispatch ─────────────────────────────────────────

    public SettingsActionResult HandleSelectAction(Game1 game)
    {
        var entries = BuildTabEntries(game);
        if (Selection < 0 || Selection >= entries.Count)
            return new SettingsActionResult();

        var item = entries[Selection];
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
        if (UI.PointerInput.IsTouch && _modsMoveTarget == null)
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

        // The confirmation window swallows every click (outside its buttons does nothing)
        if (_modsMoveTarget != null)
        {
            _hoveredRow = -1;
            _hoveredTab = -1;
            _backBtnHovered = false;
            if (clicked && _modsMoveYesRect.Contains(pos)) ConfirmModsMove(true);
            else if (clicked && _modsMoveNoRect.Contains(pos)) ConfirmModsMove(false);
            return null;
        }
        if (!clicked) return null;

        if (_backBtnHovered)
        {
            IsFocused = false;
            exitClicked = true;
            return null;
        }

        if (_hoveredTab >= 0)
        {
            if (_hoveredTab != CurrentTab)
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

    // ── Drawing (Preserves Codename Engine Pink Visual Style) ─────────────────

    public void Draw(Game1 game, SpriteBatch spriteBatch, SpriteFont font, Texture2D pixel,
                     int screenWidth, int screenHeight, FontRenderer? fontRenderer = null)
    {
        if (font == null) return;

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
        if (fontRenderer != null)
        {
            int titleSize = (int)(48 * scaleFactor);
            float titleY = 1 * scaleFactor;
            fontRenderer.DrawString(spriteBatch, "SETTINGS",
                new Vector2(24 * scaleFactor, titleY + 2),
                Color.Black * 0.5f, titleSize);
            fontRenderer.DrawString(spriteBatch, "SETTINGS",
                new Vector2(22 * scaleFactor, titleY),
                new Color(0xFF, 0x44, 0x88), titleSize);
        }
        else
        {
            spriteBatch.DrawString(font, "SETTINGS",
                new Vector2(24, 15),
                Color.Black * 0.5f, 0f, Vector2.Zero, 1.1f, SpriteEffects.None, 0f);
            spriteBatch.DrawString(font, "SETTINGS",
                new Vector2(22, 13),
                new Color(0xFF, 0x44, 0x88), 0f, Vector2.Zero, 1.1f, SpriteEffects.None, 0f);
        }

        // Top-right subtle clickable [ESC Back] button
        // Touch: a bigger "< BACK" (the button read "ESC: Back" and was 26 px tall at 720p).
        int backBtnW = (int)((UI.PointerInput.IsTouch ? 140 : 110) * scaleFactor);
        int backBtnH = (int)((UI.PointerInput.IsTouch ? 36 : 26) * scaleFactor);
        int backBtnX = screenWidth - backBtnW - (int)(18 * scaleFactor);
        int backBtnY = Math.Max(4, (tabY - backBtnH) / 2);
        _backBtnRect = new Rectangle(backBtnX, backBtnY, backBtnW, backBtnH);
        DrawRect(spriteBatch, pixel, backBtnX, backBtnY, backBtnW, backBtnH,
            _backBtnHovered ? new Color(0xFF, 0x44, 0x88) * 0.35f : new Color(25, 22, 38) * 0.85f);
        DrawRect(spriteBatch, pixel, backBtnX, backBtnY + backBtnH - 1, backBtnW, 1,
            _backBtnHovered ? new Color(0xFF, 0x44, 0x88) : new Color(0xFF, 0x44, 0x88) * 0.45f);
        string backLabel = UI.PointerInput.IsTouch ? "<  BACK" : "ESC: Back";
        if (fontRenderer != null)
        {
            int backSzPx = Math.Max(12, (int)(15 * scaleFactor));
            var bsz = fontRenderer.MeasureString(backLabel, backSzPx);
            fontRenderer.DrawString(spriteBatch, backLabel,
                new Vector2(backBtnX + (backBtnW - bsz.X) / 2f, backBtnY + (backBtnH - bsz.Y) / 2f),
                _backBtnHovered ? Color.White : Color.White * 0.7f, backSzPx);
        }
        else
        {
            float bs = 0.45f * scaleFactor;
            var bsz = font.MeasureString(backLabel) * bs;
            spriteBatch.DrawString(font, backLabel,
                new Vector2(backBtnX + (backBtnW - bsz.X) / 2f, backBtnY + (backBtnH - bsz.Y) / 2f),
                _backBtnHovered ? Color.White : Color.White * 0.7f, 0f, Vector2.Zero, bs, SpriteEffects.None, 0f);
        }

        // ── Draw Tabs ─────────────────────────────────────────────────────────
        int tabCount = TabNames.Length;
        float tabW = screenWidth / (float)tabCount;

        for (int i = 0; i < tabCount; i++)
        {
            bool isSelected = i == CurrentTab;
            bool isHovered = i == _hoveredTab;
            float tx = i * tabW;
            _tabRects[i] = new Rectangle((int)tx, tabY, (int)tabW, tabH);

            Color tabAccent = new Color(0xFF, 0x44, 0x88);
            Color tabBg = isSelected
                ? tabAccent * 0.35f
                : (isHovered ? tabAccent * 0.16f : new Color(15, 15, 25) * 0.7f);

            for (int ty = 0; ty < tabH; ty++)
            {
                float gradFactor = (float)ty / tabH;
                Color lineColor = isSelected
                    ? Color.Lerp(tabBg, tabBg * 0.8f, gradFactor)
                    : Color.Lerp(tabBg, tabBg * 1.2f, gradFactor);
                DrawRect(spriteBatch, pixel, (int)tx, tabY + ty, (int)tabW, 1, lineColor);
            }

            if (isSelected)
                DrawRect(spriteBatch, pixel, (int)tx, tabY, (int)tabW, 3, tabAccent);

            if (i < tabCount - 1)
                DrawRect(spriteBatch, pixel, (int)(tx + tabW) - 1, tabY, 1, tabH, Color.Black * 0.3f);

            string tabName = TabNames[i];
            Color tabColor = isSelected ? Color.White : (isHovered ? Color.White * 0.9f : Color.White * 0.65f);

            if (fontRenderer != null)
            {
                int tabTextSize = (int)(24 * scaleFactor);
                Vector2 measure = fontRenderer.MeasureString(tabName, tabTextSize);

                if (isSelected)
                {
                    fontRenderer.DrawString(spriteBatch, tabName,
                        new Vector2(tx + tabW / 2f - measure.X / 2f + 1, tabY + tabH / 2f - measure.Y / 2f + 1),
                        Color.Black * 0.4f, tabTextSize);
                }

                fontRenderer.DrawString(spriteBatch, tabName,
                    new Vector2(tx + tabW / 2f - measure.X / 2f, tabY + tabH / 2f - measure.Y / 2f),
                    tabColor, tabTextSize);
            }
            else
            {
                float textScale = 0.65f;
                Vector2 measure = font.MeasureString(tabName) * textScale;

                if (isSelected)
                {
                    spriteBatch.DrawString(font, tabName,
                        new Vector2(tx + tabW / 2f - measure.X / 2f + 1, tabY + tabH / 2f - measure.Y / 2f + 1),
                        Color.Black * 0.4f, 0f, Vector2.Zero, textScale, SpriteEffects.None, 0f);
                }

                spriteBatch.DrawString(font, tabName,
                    new Vector2(tx + tabW / 2f - measure.X / 2f, tabY + tabH / 2f - measure.Y / 2f),
                    tabColor, 0f, Vector2.Zero, textScale, SpriteEffects.None, 0f);
            }
        }

        // Accent line below tabs
        Color headerAccent = new Color(0xFF, 0x44, 0x88) * 0.7f;
        DrawRect(spriteBatch, pixel, 0, headerH - 3, screenWidth, 3, headerAccent);

        // ── Content area ──────────────────────────────────────────────────────
        var entries = BuildTabEntries(game);
        float panelY = headerH + 10;
        float panelH = screenHeight - panelY - 20;

        DrawList(spriteBatch, font, pixel, screenWidth, panelY, panelH, entries, fontRenderer, scaleFactor);

        // ── Description box (centered at bottom, above hint bar) ──────────────
        int hintBarH = 36;
        string description = (Selection >= 0 && Selection < entries.Count) ? entries[Selection].Description : "";
        if (!string.IsNullOrEmpty(description))
        {
            int descBoxH = 80;
            int descBoxY = screenHeight - hintBarH - descBoxH - 10;
            int descBoxW = Math.Min(900, screenWidth - 100);
            int descBoxX = screenWidth / 2 - descBoxW / 2;

            for (int y = 0; y < descBoxH; y++)
            {
                float factor = (float)y / descBoxH;
                Color descBg = Color.Lerp(new Color(0, 0, 0, 200), new Color(0, 0, 0, 180), factor);
                DrawRect(spriteBatch, pixel, descBoxX, descBoxY + y, descBoxW, 1, descBg);
            }

            Color boxBorder = IsFocused ? new Color(255, 200, 60) : new Color(0xFF, 0x44, 0x88);
            DrawRect(spriteBatch, pixel, descBoxX, descBoxY, descBoxW, 2, boxBorder * 0.6f);
            DrawRect(spriteBatch, pixel, descBoxX, descBoxY + descBoxH - 2, descBoxW, 2, boxBorder * 0.6f);
            DrawRect(spriteBatch, pixel, descBoxX, descBoxY, 2, descBoxH, boxBorder * 0.4f);
            DrawRect(spriteBatch, pixel, descBoxX + descBoxW - 2, descBoxY, 2, descBoxH, boxBorder * 0.4f);

            Color descColor = Color.White * 0.85f;
            int padding = 15;
            float maxWidth = descBoxW - padding * 2;
            string[] words = description.Split(' ');
            float lineX = descBoxX + padding;
            float lineY = descBoxY + padding;
            string currentLine = "";

            if (fontRenderer != null)
            {
                int descSize = (int)(20 * scaleFactor);
                foreach (string word in words)
                {
                    string testLine = string.IsNullOrEmpty(currentLine) ? word : currentLine + " " + word;
                    if (fontRenderer.MeasureString(testLine, descSize).X > maxWidth && !string.IsNullOrEmpty(currentLine))
                    {
                        fontRenderer.DrawString(spriteBatch, currentLine, new Vector2(lineX, lineY), descColor, descSize);
                        lineY += descSize + 4;
                        currentLine = word;
                    }
                    else
                    {
                        currentLine = testLine;
                    }
                }
                if (!string.IsNullOrEmpty(currentLine))
                    fontRenderer.DrawString(spriteBatch, currentLine, new Vector2(lineX, lineY), descColor, descSize);
            }
            else
            {
                float descScale = 0.55f;
                foreach (string word in words)
                {
                    string testLine = string.IsNullOrEmpty(currentLine) ? word : currentLine + " " + word;
                    if (font.MeasureString(testLine).X * descScale > maxWidth && !string.IsNullOrEmpty(currentLine))
                    {
                        spriteBatch.DrawString(font, currentLine, new Vector2(lineX, lineY), descColor, 0f, Vector2.Zero, descScale, SpriteEffects.None, 0f);
                        lineY += 22;
                        currentLine = word;
                    }
                    else
                    {
                        currentLine = testLine;
                    }
                }
                if (!string.IsNullOrEmpty(currentLine))
                    spriteBatch.DrawString(font, currentLine, new Vector2(lineX, lineY), descColor, 0f, Vector2.Zero, descScale, SpriteEffects.None, 0f);
            }
        }

        // ── Navigation hint bar (ASCII keys guaranteed to render cleanly) ─────
        string hint = UI.PointerInput.IsTouch
            ? "Tap a tab or option   |   Tap  <  >  to change values (hold to repeat)   |   Drag to scroll   |   Back: return"
            : IsFocused
            ? "LEFT/RIGHT: Adjust Value   |   ENTER / UP / DOWN / ESC: Confirm & Unfocus   |   -/+: Volume"
            : "UP/DOWN: Navigate   |   LEFT/RIGHT or Q/E: Switch Tab   |   ENTER: Select / Focus   |   -/+: Volume   |   ESC: Back";

        for (int y = 0; y < hintBarH; y++)
        {
            float factor = (float)y / hintBarH;
            Color hintBg = Color.Lerp(new Color(10, 10, 18), new Color(8, 8, 12), factor) * 0.9f;
            DrawRect(spriteBatch, pixel, 0, screenHeight - hintBarH + y, screenWidth, 1, hintBg);
        }

        DrawRect(spriteBatch, pixel, 0, screenHeight - hintBarH, screenWidth, 2,
            (IsFocused ? new Color(255, 200, 60) : new Color(0xFF, 0x44, 0x88)) * 0.45f);

        Color hintColor = IsFocused ? new Color(255, 225, 130) : Color.White * 0.65f;
        if (fontRenderer != null)
        {
            int hintSize = Math.Clamp((int)(17 * scaleFactor), 13, 20);
            Vector2 hintMeasure = fontRenderer.MeasureString(hint, hintSize);
            fontRenderer.DrawString(spriteBatch, hint,
                new Vector2(screenWidth / 2f - hintMeasure.X / 2f, screenHeight - hintBarH + (hintBarH - hintMeasure.Y) / 2f),
                hintColor, hintSize);
        }
        else
        {
            float hintScale = 0.50f;
            Vector2 hintMeasure = font.MeasureString(hint) * hintScale;
            spriteBatch.DrawString(font, hint,
                new Vector2(screenWidth / 2f - hintMeasure.X / 2f, screenHeight - hintBarH + (hintBarH - hintMeasure.Y) / 2f),
                hintColor, 0f, Vector2.Zero, hintScale, SpriteEffects.None, 0f);
        }

        DrawModsMovePrompt(spriteBatch, font, pixel, fontRenderer, screenWidth, screenHeight, scaleFactor);
    }

    private void DrawList(SpriteBatch sb, SpriteFont font, Texture2D pixel, int sw, float panelY, float panelH,
                          List<SettingItem> entries, FontRenderer? fr, float scaleFactor)
    {
        float itemH = 56f * scaleFactor;
        float startY = panelY + 28 * scaleFactor;
        float panelW = Math.Min(900f * scaleFactor, sw * 0.7f);
        float panelX = (sw - panelW) / 2f;

        float totalHeight = entries.Count * itemH;
        float descBoxReservedSpace = 100f;
        float availableHeight = Math.Max(itemH, panelH - 60 - descBoxReservedSpace);
        float maxScroll = Math.Max(0f, totalHeight - availableHeight);

        if (totalHeight <= availableHeight)
        {
            startY = panelY + (availableHeight - totalHeight) / 2f + 12;
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

        var oldScissor = sb.GraphicsDevice.ScissorRectangle;
        var viewportTop = (int)panelY;
        var viewportBottom = (int)(panelY + panelH - descBoxReservedSpace + 12);
        var scissorRect = new Rectangle((int)panelX - 20, viewportTop, (int)panelW + 40, Math.Max(1, viewportBottom - viewportTop));

        sb.End();
        sb.Begin(SpriteSortMode.Deferred, BlendState.AlphaBlend, SamplerState.PointClamp, null,
            new RasterizerState { ScissorTestEnable = true });
        sb.GraphicsDevice.ScissorRectangle = scissorRect;

        _rowHitboxes.Clear();

        for (int i = 0; i < entries.Count; i++)
        {
            bool sel = i == Selection;
            bool focused = sel && IsFocused;
            bool hovered = i == _hoveredRow;
            float y = startY + i * itemH - ScrollOffset;

            if (y + itemH < viewportTop || y > viewportBottom)
                continue;

            int rowX = (int)panelX - 12;
            int rowY = (int)y - 6;
            int rowW = (int)panelW + 24;
            int rowH = (int)itemH - 8;
            var fullRowRect = new Rectangle(rowX, rowY, rowW, rowH);

            if (sel)
            {
                Color accent = focused ? new Color(255, 200, 50) : new Color(0xFF, 0x44, 0x88);
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

            var item = entries[i];
            int textSize = (int)(28 * scaleFactor);
            float scale = 0.8f;

            if (item.Kind == SettingKind.Header)
            {
                DrawSectionHeader(sb, font, pixel, fr, item.Label, panelX, panelW, y, itemH, scaleFactor);
                continue;
            }

            // Label
            if (fr != null)
                fr.DrawString(sb, item.Label, new Vector2(panelX + 10, y + 4), textColor, textSize);
            else
                sb.DrawString(font, item.Label, new Vector2(panelX + 10, y + 4), textColor, 0f, Vector2.Zero, scale, SpriteEffects.None, 0f);

            // Value / Adjustable arrows
            if (item.Kind == SettingKind.Adjustable && item.OnAdjust != null)
            {
                Vector2 valSz = fr != null ? fr.MeasureString(item.Value, textSize) : font.MeasureString(item.Value) * scale;
                Vector2 arrowSz = fr != null ? fr.MeasureString("<", textSize) : font.MeasureString("<") * scale;

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

                if (fr != null)
                {
                    fr.DrawString(sb, "<", new Vector2(leftArrowX + (arrowW - arrowSz.X) / 2f, y + 4), lCol, textSize);
                    fr.DrawString(sb, item.Value, new Vector2(valX, y + 4), valueColor, textSize);
                    fr.DrawString(sb, ">", new Vector2(rightArrowX + (arrowW - arrowSz.X) / 2f, y + 4), rCol, textSize);
                }
                else
                {
                    sb.DrawString(font, "<", new Vector2(leftArrowX + (arrowW - arrowSz.X) / 2f, y + 4), lCol, 0f, Vector2.Zero, scale, SpriteEffects.None, 0f);
                    sb.DrawString(font, item.Value, new Vector2(valX, y + 4), valueColor, 0f, Vector2.Zero, scale, SpriteEffects.None, 0f);
                    sb.DrawString(font, ">", new Vector2(rightArrowX + (arrowW - arrowSz.X) / 2f, y + 4), rCol, 0f, Vector2.Zero, scale, SpriteEffects.None, 0f);
                }

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
                    Vector2 valueMeasure = fr != null ? fr.MeasureString(item.Value, textSize) : font.MeasureString(item.Value) * scale;
                    if (fr != null)
                        fr.DrawString(sb, item.Value, new Vector2(panelX + panelW - valueMeasure.X - 10, y + 4), valueColor, textSize);
                    else
                        sb.DrawString(font, item.Value, new Vector2(panelX + panelW - valueMeasure.X - 10, y + 4), valueColor, 0f, Vector2.Zero, scale, SpriteEffects.None, 0f);
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
            float scrollBarH = Math.Max(40f, availableHeight * (availableHeight / totalHeight));
            float scrollBarY = panelY + 20 + (availableHeight - scrollBarH - 40) * (ScrollOffset / maxScroll);

            DrawRect(sb, pixel, sw - 20, (int)panelY + 20, 8, (int)(availableHeight - 40), Color.Black * 0.3f);
            DrawRect(sb, pixel, sw - 20, (int)scrollBarY, 8, (int)scrollBarH, new Color(0xFF, 0x44, 0x88) * 0.7f);
        }
    }

    /// <summary>Section title row: small pink caps with a divider line.</summary>
    private void DrawSectionHeader(SpriteBatch sb, SpriteFont font, Texture2D pixel, FontRenderer? fr,
                                   string title, float panelX, float panelW, float y, float itemH, float scaleFactor)
    {
        Color accent = new Color(0xFF, 0x44, 0x88);
        int size = Math.Max(12, (int)(19 * scaleFactor));
        Vector2 sz = fr != null ? fr.MeasureString(title, size) : font.MeasureString(title) * 0.55f;
        float textY = y + itemH - 8 - sz.Y - 10 * scaleFactor;
        if (fr != null)
            fr.DrawString(sb, title, new Vector2(panelX + 10, textY), accent * 0.9f, size);
        else
            sb.DrawString(font, title, new Vector2(panelX + 10, textY), accent * 0.9f, 0f, Vector2.Zero, 0.55f, SpriteEffects.None, 0f);

        int lineX = (int)(panelX + 10 + sz.X + 12);
        int lineY = (int)(textY + sz.Y / 2f);
        DrawRect(sb, pixel, lineX, lineY, (int)(panelX + panelW - 10) - lineX, 1, accent * 0.35f);
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
