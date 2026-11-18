using System;
using System.Collections.Generic;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using Microsoft.Xna.Framework.Input;

namespace FunkyMakerPlayer.Managers;

/// <summary>
/// Settings Manager with tabbed interface (Codename Engine style).
/// Unified tabs across platforms: GAMEPLAY, GRAPHICS, GENERAL, EXPERIMENTAL.
/// Supports focus-based keyboard adjustment and full mouse interaction.
/// </summary>
public class SettingsManager
{
    public enum SettingKind
    {
        Toggle,
        Action,
        Adjustable,
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
    public int CurrentTab { get; set; } = 0;  // 0=Gameplay, 1=Graphics, 2=General, 3=Experimental
    public bool IsFocused { get; set; } = false;
    public float ScrollOffset { get; set; } = 0f;

    private static readonly string[] TabNames = { "GAMEPLAY", "GRAPHICS", "GENERAL", "EXPERIMENTAL" };

    // Available countdown packs (cached)
    private List<string> _availableCountdowns = new() { "default" };

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
    private readonly List<RowHitbox> _rowHitboxes = new();

    // ── Initialization ───────────────────────────────────────────────────────

    public SettingsManager()
    {
        Console.WriteLine("[SettingsManager] Initialized with tabs");
        ScanAvailableCountdowns();
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

    /// <summary>
    /// Scans mods/countdowns/ folder for available countdown packs.
    /// </summary>
    private void ScanAvailableCountdowns()
    {
        _availableCountdowns.Clear();
        _availableCountdowns.Add("default");

        try
        {
            string countdownsDir = PlatformPaths.GetCountdownsPath();
            if (System.IO.Directory.Exists(countdownsDir))
            {
                foreach (var dir in System.IO.Directory.GetDirectories(countdownsDir))
                {
                    string name = System.IO.Path.GetFileName(dir);
                    if (!string.IsNullOrEmpty(name) && !DefaultMods.IsDefaultCountdown(name))
                    {
                        string readyPath = System.IO.Path.Combine(dir, "ready.png");
                        string setPath = System.IO.Path.Combine(dir, "set.png");
                        string goPath = System.IO.Path.Combine(dir, "go.png");

                        if (System.IO.File.Exists(readyPath) &&
                            System.IO.File.Exists(setPath) &&
                            System.IO.File.Exists(goPath))
                        {
                            _availableCountdowns.Add(name);
                        }
                    }
                }
            }

            Console.WriteLine($"[SettingsManager] Found {_availableCountdowns.Count} countdown packs: {string.Join(", ", _availableCountdowns)}");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[SettingsManager] Error scanning countdowns: {ex.Message}");
        }
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
            case 0:
                BuildGameplayEntries(entries);
                break;
            case 1:
                BuildGraphicsEntries(game, entries);
                break;
            case 2:
                BuildGeneralEntries(game, entries);
                break;
            case 3:
                BuildExperimentalEntries(game, entries);
                break;
        }

        _lastItemCount = Math.Max(1, entries.Count);
        if (entries.Count > 0 && Selection >= entries.Count)
            Selection = entries.Count - 1;
        else if (Selection < 0)
            Selection = 0;

        return entries;
    }

    public int GetItemCount() => _lastItemCount;

    public int GetItemCount(Game1 game) => BuildTabEntries(game).Count;

    private void BuildGameplayEntries(List<SettingItem> entries)
    {
        entries.Add(new SettingItem
        {
            Label = "Botplay",
            Value = NoteRenderer.Autoplay ? "ON" : "OFF",
            Kind = SettingKind.Toggle,
            Description = "CPU plays all player notes automatically. Great for watching charts or testing levels.",
            OnSelect = () =>
            {
                NoteRenderer.Autoplay = !NoteRenderer.Autoplay;
                return new SettingsActionResult { NeedsSave = true };
            }
        });

        entries.Add(new SettingItem
        {
            Label = "Downscroll",
            Value = NoteRenderer.Downscroll ? "ON" : "OFF",
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
            Value = NoteRenderer.Midlescroll ? "ON" : "OFF",
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
            Description = "Visual note speed multiplier (default: 1.00x, range: 0.25x - 5.00x). Press ENTER to focus, LEFT/RIGHT to change.",
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
            Value = NoteRenderer.GhostTapping ? "ON" : "OFF",
            Kind = SettingKind.Toggle,
            Description = "Prevents misses when tapping keys with no notes nearby (OFF = strict timing).",
            OnSelect = () =>
            {
                NoteRenderer.GhostTapping = !NoteRenderer.GhostTapping;
                return new SettingsActionResult { NeedsSave = true };
            }
        });

        entries.Add(new SettingItem
        {
            Label = "Show Key Labels",
            Value = NoteRenderer.ShowKeyLabels ? "ON" : "OFF",
            Kind = SettingKind.Toggle,
            Description = "Shows the key for each lane on top of your strums.",
            OnSelect = () =>
            {
                NoteRenderer.ShowKeyLabels = !NoteRenderer.ShowKeyLabels;
                return new SettingsActionResult { NeedsSave = true };
            }
        });

        entries.Add(new SettingItem
        {
            Label = "Input Buffer",
            Value = $"{(int)Math.Round(NoteRenderer.InputBuffer * 1000)}ms",
            Kind = SettingKind.Adjustable,
            Description = "Early hit buffer capped at 10ms default. You can lower it (0ms - 10ms) for stricter timing.",
            OnAdjust = (_, delta) =>
            {
                NoteRenderer.InputBuffer = MathF.Round(
                    Math.Clamp(NoteRenderer.InputBuffer + delta * 0.002f, 0f, 0.010f), 3);
                return new SettingsActionResult { NeedsSave = true };
            }
        });

        entries.Add(new SettingItem
        {
            Label = "Safe Frames",
            Value = $"{(int)Math.Round(NoteRenderer.SafeFramesTime * 1000)}ms",
            Kind = SettingKind.Adjustable,
            Description = "Start-of-song miss protection capped at 83ms (~5 frames) default. Can only be lowered (0ms - 83ms).",
            OnAdjust = (isLeft, _) =>
            {
                float next = NoteRenderer.SafeFramesTime + (isLeft ? -0.017f : 0.017f);
                if (!isLeft && next > 0.080f) next = 0.083f;
                NoteRenderer.SafeFramesTime = MathF.Round(Math.Clamp(next, 0f, 0.083f), 3);
                return new SettingsActionResult { NeedsSave = true };
            }
        });

        #if !ANDROID
        entries.Add(new SettingItem
        {
            Label = "Rebind Keys",
            Value = $"{NoteRenderer.PlayerKeys[0]}/{NoteRenderer.PlayerKeys[1]}/{NoteRenderer.PlayerKeys[2]}/{NoteRenderer.PlayerKeys[3]}",
            Kind = SettingKind.Action,
            Description = "Customize keyboard controls for Left, Down, Up, and Right note lanes.",
            OnSelect = () =>
            {
                IsActive = false;
                return new SettingsActionResult { OpenKeyBind = true, NeedsSave = false };
            }
        });
        #endif
    }

    private void BuildGraphicsEntries(Game1? game, List<SettingItem> entries)
    {
        bool canOverrideZoom = game?._events == null || !game._events.HasCameraEvents;
        float currentZoom = game?._cameraManager?.GetEffectiveScale(game._events?.Camera) ?? 1f;

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
            Kind = SettingKind.Adjustable,
            Description = "Visual 3-2-1-GO countdown pack before the song starts (ENTER opens preview menu, LEFT/RIGHT cycles).",
            OnSelect = () =>
            {
                IsActive = false;
                return new SettingsActionResult { OpenCountdownMenu = true, NeedsSave = false };
            },
            OnAdjust = (isLeft, _) =>
            {
                int currentIndex = _availableCountdowns.IndexOf(Game1.SelectedCountdown);
                if (currentIndex == -1) currentIndex = 0;
                int newIndex = (currentIndex + (isLeft ? -1 : 1) + _availableCountdowns.Count) % _availableCountdowns.Count;
                Game1.SelectedCountdown = _availableCountdowns[newIndex];
                return new SettingsActionResult { NeedsSave = true };
            }
        });

        entries.Add(new SettingItem
        {
            Label = "Font Style",
            Value = "[Select]",
            Kind = SettingKind.Action,
            Description = "Choose the UI font used throughout menus. Add custom .ttf/.otf fonts to mods/fonts/.",
            OnSelect = () =>
            {
                IsActive = false;
                return new SettingsActionResult { OpenFontMenu = true, NeedsSave = false };
            }
        });

        entries.Add(new SettingItem
        {
            Label = "Show Combo",
            Value = Game1.ShowCombo ? "ON" : "OFF",
            Kind = SettingKind.Toggle,
            Description = "Display combo numbers and judgement popups (Sick!/Good/Bad/Shit) on hits.",
            OnSelect = () =>
            {
                Game1.ShowCombo = !Game1.ShowCombo;
                HudSettings.Instance.ComboVisible = Game1.ShowCombo;
                HudSettings.Instance.JudgementVisible = Game1.ShowCombo;
                HudSettings.Instance.Save();
                return new SettingsActionResult { NeedsSave = true };
            }
        });

        #if !ANDROID
        entries.Add(new SettingItem
        {
            Label = "HUD Editor",
            Value = "[Open]",
            Kind = SettingKind.Action,
            Description = "Open interactive HUD Editor to move, resize, and toggle individual HUD elements.",
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
            Value = HudSettings.Instance.MasterHudVisible ? "ON" : "OFF",
            Kind = SettingKind.Toggle,
            Description = "Master toggle to show or hide the entire gameplay HUD at once.",
            OnSelect = () =>
            {
                HudSettings.Instance.MasterHudVisible = !HudSettings.Instance.MasterHudVisible;
                HudSettings.Instance.Save();
                return new SettingsActionResult { NeedsSave = true };
            }
        });

        if (HudSettings.Instance.MasterHudVisible)
        {
            entries.Add(new SettingItem
            {
                Label = "  Song Timer",
                Value = Game1.ShowTimer ? "ON" : "OFF",
                Kind = SettingKind.Toggle,
                Description = "Show or hide the song progress timer bar.",
                OnSelect = () =>
                {
                    Game1.ShowTimer = !Game1.ShowTimer;
                    HudSettings.Instance.Save();
                    return new SettingsActionResult { NeedsSave = true };
                }
            });

            entries.Add(new SettingItem
            {
                Label = "  Timer Color",
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
                Description = "Color scheme used for the song progress timer bar.",
                OnAdjust = (isLeft, _) =>
                {
                    const int count = 7;
                    Game1.TimerColorPreset = (Game1.TimerColorPreset + (isLeft ? -1 : 1) + count) % count;
                    return new SettingsActionResult { NeedsSave = true };
                }
            });

            if (Game1.ShowTimer)
            {
                entries.Add(new SettingItem
                {
                    Label = "    Preset",
                    Value = Game1.TimerPreset,
                    Kind = SettingKind.Adjustable,
                    Description = "Timer position preset: Custom HUD (editor), Legacy, Top, Left, Middle, Right, or Strumline.",
                    OnAdjust = (isLeft, _) =>
                    {
                        string[] presets = { "Legacy", "Custom HUD", "Top", "Left", "Middle", "Right", "Strumline" };
                        int idx = Array.IndexOf(presets, Game1.TimerPreset);
                        if (idx < 0) idx = 1;
                        idx = (idx + (isLeft ? -1 : 1) + presets.Length) % presets.Length;
                        Game1.TimerPreset = presets[idx];
                        return new SettingsActionResult { NeedsSave = true };
                    }
                });

                if (Game1.TimerPreset != "Legacy" && Game1.TimerPreset != "Custom HUD")
                {
                    entries.Add(new SettingItem
                    {
                        Label = "      Text Mode",
                        Value = Game1.TimerTextMode,
                        Kind = SettingKind.Adjustable,
                        Description = "Text displayed inside the timer bar (Time Left, Time Elapsed, Song Name, None).",
                        OnAdjust = (isLeft, _) =>
                        {
                            string[] modes = { "Time Left", "Time Elapsed", "Song Name", "None" };
                            int idx = Array.IndexOf(modes, Game1.TimerTextMode);
                            if (idx < 0) idx = 0;
                            idx = (idx + (isLeft ? -1 : 1) + modes.Length) % modes.Length;
                            Game1.TimerTextMode = modes[idx];
                            return new SettingsActionResult { NeedsSave = true };
                        }
                    });

                    entries.Add(new SettingItem
                    {
                        Label = "      Force Top",
                        Value = Game1.TimerForceTop ? "ON" : "OFF",
                        Kind = SettingKind.Toggle,
                        Description = "Always place timer bar at the top, ignoring Downscroll.",
                        OnSelect = () =>
                        {
                            Game1.TimerForceTop = !Game1.TimerForceTop;
                            return new SettingsActionResult { NeedsSave = true };
                        }
                    });
                }
            }

            entries.Add(new SettingItem
            {
                Label = "  Song Title",
                Value = HudSettings.Instance.SongInfoVisible ? "ON" : "OFF",
                Kind = SettingKind.Toggle,
                Description = "Show or hide the level/song title in the top-left corner.",
                OnSelect = () =>
                {
                    HudSettings.Instance.SongInfoVisible = !HudSettings.Instance.SongInfoVisible;
                    HudSettings.Instance.Save();
                    return new SettingsActionResult { NeedsSave = true };
                }
            });

            entries.Add(new SettingItem
            {
                Label = "  Status Info",
                Value = HudSettings.Instance.StatusInfoVisible ? "ON" : "OFF",
                Kind = SettingKind.Toggle,
                Description = "Show or hide the status bar ([BOTPLAY]/[MANUAL], scroll direction, speed).",
                OnSelect = () =>
                {
                    HudSettings.Instance.StatusInfoVisible = !HudSettings.Instance.StatusInfoVisible;
                    HudSettings.Instance.Save();
                    return new SettingsActionResult { NeedsSave = true };
                }
            });

            entries.Add(new SettingItem
            {
                Label = "  Health Bar",
                Value = HudSettings.Instance.HealthBarVisible ? "ON" : "OFF",
                Kind = SettingKind.Toggle,
                Description = "Show or hide the health bar and character icons during gameplay.",
                OnSelect = () =>
                {
                    bool next = !HudSettings.Instance.HealthBarVisible;
                    HudSettings.Instance.HealthBarVisible = next;
                    HudSettings.Instance.OpponentIconVisible = next;
                    HudSettings.Instance.PlayerIconVisible = next;
                    HudSettings.Instance.Save();
                    return new SettingsActionResult { NeedsSave = true };
                }
            });

            entries.Add(new SettingItem
            {
                Label = "  Score & Stats",
                Value = (HudSettings.Instance.ScoreVisible || HudSettings.Instance.MissesVisible || HudSettings.Instance.AccuracyVisible) ? "ON" : "OFF",
                Kind = SettingKind.Toggle,
                Description = "Show or hide Score, Misses, and Accuracy counters during gameplay.",
                OnSelect = () =>
                {
                    bool next = !(HudSettings.Instance.ScoreVisible || HudSettings.Instance.MissesVisible || HudSettings.Instance.AccuracyVisible);
                    HudSettings.Instance.ScoreVisible = next;
                    HudSettings.Instance.MissesVisible = next;
                    HudSettings.Instance.AccuracyVisible = next;
                    HudSettings.Instance.Save();
                    return new SettingsActionResult { NeedsSave = true };
                }
            });

            entries.Add(new SettingItem
            {
                Label = "  Strumlines",
                Value = NoteRenderer.ShowStrumlines ? "ON" : "OFF",
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
                    Label = "    Player Strumline",
                    Value = !NoteRenderer.HidePlayerStrumline ? "ON" : "OFF",
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
                    Label = "    Opponent Strumline",
                    Value = !Game1.HideOpponentStrumline ? "ON" : "OFF",
                    Kind = SettingKind.Toggle,
                    Description = "Show or hide the opponent's strumline (ignored in Multiplayer).",
                    OnSelect = () =>
                    {
                        Game1.HideOpponentStrumline = !Game1.HideOpponentStrumline;
                        return new SettingsActionResult { NeedsOpponentAlphaUpdate = true, NeedsSave = true };
                    }
                });

                entries.Add(new SettingItem
                {
                    Label = "    Extra Strumlines",
                    Value = !Game1.HideExtraStrumlines ? "ON" : "OFF",
                    Kind = SettingKind.Toggle,
                    Description = "Show or hide extra strumlines beyond player and opponent.",
                    OnSelect = () =>
                    {
                        Game1.HideExtraStrumlines = !Game1.HideExtraStrumlines;
                        return new SettingsActionResult { NeedsStrumlineAlphaUpdate = true, NeedsSave = true };
                    }
                });
            }
        }

        entries.Add(new SettingItem
        {
            Label = "Camera Zoom",
            Value = canOverrideZoom ? $"{currentZoom:F2}" : $"{currentZoom:F2} (locked)",
            Kind = SettingKind.Adjustable,
            Description = "Manual stage camera zoom when the chart has no camera zoom events (locked if events override zoom).",
            OnAdjust = (isLeft, _) => new SettingsActionResult
            {
                AdjustCameraZoom = canOverrideZoom ? (isLeft ? -1 : 1) : 0,
                NeedsSave = false
            }
        });

        entries.Add(new SettingItem
        {
            Label = "Icon Mode",
            Value = Game1.IconMode == 0 ? "FNF Base" : "Fixed",
            Kind = SettingKind.Toggle,
            Description = "FNF Base moves icons along the health bar divider; Fixed anchors icons at the bar ends.",
            OnSelect = () =>
            {
                Game1.IconMode = (Game1.IconMode + 1) % 2;
                return new SettingsActionResult { NeedsSave = true };
            }
        });

        entries.Add(new SettingItem
        {
            Label = "Color Grading",
            Value = Game1.ColorGradeEnabled ? "ON" : "OFF",
            Kind = SettingKind.Toggle,
            Description = "Optional HSB stage color shader (off by default, leaves HUD untouched).",
            OnSelect = () =>
            {
                Game1.ColorGradeEnabled = !Game1.ColorGradeEnabled;
                return new SettingsActionResult { NeedsSave = true };
            }
        });

        if (Game1.ColorGradeEnabled)
        {
            entries.Add(new SettingItem
            {
                Label = "  Hue",
                Value = $"{Game1.ColorGradeHue:F2}",
                Kind = SettingKind.Adjustable,
                Description = "Shift stage colors along the color wheel (-0.50 to +0.50, 0.00 = original).",
                OnAdjust = (_, delta) =>
                {
                    Game1.ColorGradeHue = MathF.Round(Math.Clamp(Game1.ColorGradeHue + delta * 0.02f, -0.50f, 0.50f), 2);
                    return new SettingsActionResult { NeedsSave = true };
                }
            });

            entries.Add(new SettingItem
            {
                Label = "  Saturation",
                Value = $"{Game1.ColorGradeSaturation:F2}",
                Kind = SettingKind.Adjustable,
                Description = "Stage color intensity (-1.00 = grayscale, 0.00 = normal, +1.00 = vivid).",
                OnAdjust = (_, delta) =>
                {
                    Game1.ColorGradeSaturation = MathF.Round(Math.Clamp(Game1.ColorGradeSaturation + delta * 0.05f, -1.0f, 1.0f), 2);
                    return new SettingsActionResult { NeedsSave = true };
                }
            });

            entries.Add(new SettingItem
            {
                Label = "  Brightness",
                Value = $"{Game1.ColorGradeBrightness:F2}",
                Kind = SettingKind.Adjustable,
                Description = "Stage shader brightness offset (-0.50 to +0.50, 0.00 = neutral).",
                OnAdjust = (_, delta) =>
                {
                    Game1.ColorGradeBrightness = MathF.Round(Math.Clamp(Game1.ColorGradeBrightness + delta * 0.05f, -0.50f, 0.50f), 2);
                    return new SettingsActionResult { NeedsSave = true };
                }
            });
        }
    }

    private void BuildGeneralEntries(Game1? game, List<SettingItem> entries)
    {
        entries.Add(new SettingItem
        {
            Label = "Master Volume",
            Value = $"{(int)Math.Round(Game1.MusicVolume * 100)}%",
            Kind = SettingKind.Adjustable,
            Description = "Master audio volume (you can also press - and + keys anytime to lower or raise volume).",
            OnAdjust = (_, delta) => new SettingsActionResult { AdjustVolume = delta, NeedsSave = true }
        });

        entries.Add(new SettingItem
        {
            Label = "SFX Volume",
            Value = $"{(int)Math.Round(UI.UiSfx.Volume * 100)}%",
            Kind = SettingKind.Adjustable,
            Description = "Volume of menu and editor sound effects (scaled by Master Volume).",
            OnAdjust = (_, delta) =>
            {
                UI.UiSfx.Volume = MathF.Round(Math.Clamp(UI.UiSfx.Volume + delta * 0.05f, 0f, 1f), 2);
                return new SettingsActionResult { NeedsSave = true };
            }
        });

        entries.Add(new SettingItem
        {
            Label = "Menu Music Volume",
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
            Label = "VSync",
            Value = game != null && game._graphics.SynchronizeWithVerticalRetrace ? "ON" : "OFF",
            Kind = SettingKind.Toggle,
            Description = "Synchronizes frame rate with monitor refresh rate to prevent screen tearing.",
            OnSelect = () => new SettingsActionResult { ToggleVSync = true, NeedsSave = true }
        });

        #if !ANDROID
        entries.Add(new SettingItem
        {
            Label = "Fullscreen",
            Value = game != null && game._isFullscreen ? "ON" : "OFF",
            Kind = SettingKind.Toggle,
            Description = "Toggle borderless fullscreen mode. Press Alt+Enter anytime to toggle quickly.",
            OnSelect = () => new SettingsActionResult { ToggleFullscreen = true, NeedsSave = false }
        });
        #endif

        entries.Add(new SettingItem
        {
            Label = "FPS Counter",
            Value = Game1.ShowFpsCounter ? "ON" : "OFF",
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
            Label = "Debug Info",
            Value = Game1.ShowDebugInfo ? "ON" : "OFF",
            Kind = SettingKind.Toggle,
            Description = "Show technical song debug info (BPM, beat count, camera position, and events).",
            OnSelect = () =>
            {
                Game1.ShowDebugInfo = !Game1.ShowDebugInfo;
                return new SettingsActionResult { NeedsSave = true };
            }
        });
    }

    private void BuildExperimentalEntries(Game1? game, List<SettingItem> entries)
    {
        entries.Add(new SettingItem
        {
            Label = "Camera Follow Player",
            Value = NoteRenderer.CameraFollowPlayer ? "ON" : "OFF",
            Kind = SettingKind.Toggle,
            Description = "[EXPERIMENTAL / DEPRECATED] Automatically pans camera toward singing notes. Disable for stage camera.",
            OnSelect = () =>
            {
                NoteRenderer.CameraFollowPlayer = !NoteRenderer.CameraFollowPlayer;
                return new SettingsActionResult { NeedsSave = true };
            }
        });

        entries.Add(new SettingItem
        {
            Label = "Extended Camera (Beta)",
            Value = Game1.UseExtendedCamera ? "ON" : "OFF",
            Kind = SettingKind.Toggle,
            Description = "[DEPRECATED / EXPERIMENTAL] Warning: may break custom stage camera positions! Disabling it restores normal cameras.",
            OnSelect = () =>
            {
                Game1.UseExtendedCamera = !Game1.UseExtendedCamera;
                if (game?._cameraManager != null)
                    game._cameraManager.UseExtendedCamera = Game1.UseExtendedCamera;
                Console.WriteLine($"[Settings] Extended Camera (Beta): {(Game1.UseExtendedCamera ? "ON" : "OFF")}");
                return new SettingsActionResult { NeedsSave = true };
            }
        });

        entries.Add(new SettingItem
        {
            Label = "Timeskip",
            Value = Game1.TimeskipEnabled ? "ON" : "OFF",
            Kind = SettingKind.Toggle,
            Description = "[EXPERIMENTAL / DEPRECATED] Hold T (if unbound) or Ctrl+Right/Left in OGG songs to skip.",
            OnSelect = () =>
            {
                Game1.TimeskipEnabled = !Game1.TimeskipEnabled;
                return new SettingsActionResult { NeedsSave = true };
            }
        });

        if (Game1.TimeskipEnabled)
        {
            entries.Add(new SettingItem
            {
                Label = "  Timeskip Seconds",
                Value = $"{Game1.TimeskipSeconds:F0}s",
                Kind = SettingKind.Adjustable,
                Description = "[EXPERIMENTAL] Seconds skipped per Timeskip step (5s - 60s).",
                OnAdjust = (_, delta) =>
                {
                    Game1.TimeskipSeconds = MathF.Round(Math.Clamp(Game1.TimeskipSeconds + delta * 5f, 5f, 60f), 0);
                    return new SettingsActionResult { NeedsSave = true };
                }
            });

            if (game?._level != null)
            {
                entries.Add(new SettingItem
                {
                    Label = "  Seek Current Song",
                    Value = "Scrub",
                    Kind = SettingKind.Adjustable,
                    Description = "[EXPERIMENTAL] Scrub backward/forward in the currently paused OGG song.",
                    OnAdjust = (_, delta) => new SettingsActionResult
                    {
                        SeekSong = delta * Game1.TimeskipSeconds,
                        NeedsSave = false
                    }
                });
            }
        }

        entries.Add(new SettingItem
        {
            Label = "Bilinear Filter",
            Value = Game1.BilinearFiltering ? "ON" : "OFF",
            Kind = SettingKind.Toggle,
            Description = "[DEPRECATED] Loading screen filter only. Scene/FMM sprites auto-detect bilinear smoothing from their JSON.",
            OnSelect = () =>
            {
                Game1.BilinearFiltering = !Game1.BilinearFiltering;
                return new SettingsActionResult { NeedsSave = true };
            }
        });

        entries.Add(new SettingItem
        {
            Label = "Arrow Effects",
            Value = ArrowEffectManager.Enabled ? "ON" : "OFF",
            Kind = SettingKind.Toggle,
            Description = "[EXPERIMENTAL] Optional cosmetic BPM-synced animations on receptor arrows (off by default).",
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
                Label = "  Effect Intensity",
                Value = $"{(int)Math.Round(ArrowEffectManager.EffectIntensity * 100)}%",
                Kind = SettingKind.Adjustable,
                Description = "Strength multiplier for experimental arrow effects (0% - 100%).",
                OnAdjust = (_, delta) =>
                {
                    ArrowEffectManager.EffectIntensity = MathF.Round(
                        Math.Clamp(ArrowEffectManager.EffectIntensity + delta * 0.1f, 0f, 1f), 2);
                    return new SettingsActionResult { NeedsSave = true };
                }
            });

            entries.Add(new SettingItem
            {
                Label = "  Effect Type",
                Value = ArrowEffectManager.GetSelectedEffectName(),
                Kind = SettingKind.Adjustable,
                Description = "Select effect type: Random, Glow, Vertical Bounce, Spin 360, Horizontal Bounce, or Dizzy.",
                OnAdjust = (isLeft, _) =>
                {
                    int newEffect = ArrowEffectManager.SelectedEffect + (isLeft ? -1 : 1);
                    ArrowEffectManager.SelectedEffect = (newEffect + 6) % 6;
                    return new SettingsActionResult { NeedsSave = true };
                }
            });

            entries.Add(new SettingItem
            {
                Label = "  Effect Chance",
                Value = $"{(int)Math.Round(ArrowEffectManager.EffectChance * 100)}%",
                Kind = SettingKind.Adjustable,
                Description = "Probability per beat that an arrow effect triggers (0% - 100%).",
                OnAdjust = (_, delta) =>
                {
                    ArrowEffectManager.EffectChance = MathF.Round(
                        Math.Clamp(ArrowEffectManager.EffectChance + delta * 0.05f, 0f, 1f), 2);
                    return new SettingsActionResult { NeedsSave = true };
                }
            });
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
            // Countdown Style keeps Enter-to-open-menu? Wait: if focused, Enter unfocuses;
            // if it's a pure adjustable value (Scroll Speed, Volume, etc.), Enter focuses/unfocuses!
            if (item.OnSelect != null && IsFocused)
            {
                IsFocused = false;
                return new SettingsActionResult();
            }
            if (item.OnSelect != null && !IsFocused && item.Label == "Countdown Style")
            {
                // Still allow clicking '<' '>' or focusing? Better: Enter toggles focus cleanly on ALL adjustable items,
                // OR for Countdown Style opens the Countdown menu! Let's check: Countdown Style has < default > visual preview,
                // so Left/Right focuses/cycles OR Enter opens visual preview! Calling OnSelect opens the visual picker menu cleanly.
                return item.OnSelect();
            }

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

        // Some rows apply their change through the result (volume, camera zoom, seek)
        bool viaResult = (result.AdjustVolume > 0f && Game1.MusicVolume < 1f) ||
                         (result.AdjustVolume < 0f && Game1.MusicVolume > 0f) ||
                         result.AdjustCameraZoom != 0 || result.SeekSong != 0f;
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
        if (wheelDelta != 0)
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

                if (moved && !IsFocused)
                    Selection = hb.Index;
                break;
            }
        }

        bool clicked = mouse.LeftButton == ButtonState.Pressed && prevMouse.LeftButton == ButtonState.Released;
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

                var item = entries[Selection];
                if (_hoveredArrow != 0 && item.Kind == SettingKind.Adjustable && item.OnAdjust != null)
                {
                    IsFocused = true;
                    adjustmentResult = RunAdjust(game, item, _hoveredArrow < 0);
                    return null;
                }

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
        int backBtnW = (int)(110 * scaleFactor);
        int backBtnH = (int)(26 * scaleFactor);
        int backBtnX = screenWidth - backBtnW - (int)(18 * scaleFactor);
        int backBtnY = Math.Max(4, (tabY - backBtnH) / 2);
        _backBtnRect = new Rectangle(backBtnX, backBtnY, backBtnW, backBtnH);
        DrawRect(spriteBatch, pixel, backBtnX, backBtnY, backBtnW, backBtnH,
            _backBtnHovered ? new Color(0xFF, 0x44, 0x88) * 0.35f : new Color(25, 22, 38) * 0.85f);
        DrawRect(spriteBatch, pixel, backBtnX, backBtnY + backBtnH - 1, backBtnW, 1,
            _backBtnHovered ? new Color(0xFF, 0x44, 0x88) : new Color(0xFF, 0x44, 0x88) * 0.45f);
        string backLabel = "ESC: Back";
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
            bool isExp = i == 3;
            float tx = i * tabW;
            _tabRects[i] = new Rectangle((int)tx, tabY, (int)tabW, tabH);

            Color tabAccent = isExp ? new Color(255, 180, 50) : new Color(0xFF, 0x44, 0x88);
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
        Color headerAccent = CurrentTab == 3 ? new Color(255, 180, 50) * 0.75f : new Color(0xFF, 0x44, 0x88) * 0.7f;
        DrawRect(spriteBatch, pixel, 0, headerH - 3, screenWidth, 3, headerAccent);

        // ── Content area ──────────────────────────────────────────────────────
        var entries = BuildTabEntries(game);
        float panelY = headerH + 10;
        float panelH = screenHeight - panelY - 20;

        if (CurrentTab == 3)
        {
            float warnW = Math.Min(900f * scaleFactor, screenWidth * 0.7f);
            float warnX = (screenWidth - warnW) / 2f;
            int warnH = (int)(28 * scaleFactor);
            DrawRect(spriteBatch, pixel, (int)warnX, (int)panelY, (int)warnW, warnH, new Color(55, 35, 10) * 0.85f);
            DrawRect(spriteBatch, pixel, (int)warnX, (int)panelY, (int)warnW, 2, new Color(255, 180, 50) * 0.7f);
            string warnMsg = "EXPERIMENTAL / DEPRECATED OPTIONS: Off by default or handled automatically.";
            if (fontRenderer != null)
            {
                int wszPx = Math.Max(13, (int)(16 * scaleFactor));
                var wsz = fontRenderer.MeasureString(warnMsg, wszPx);
                fontRenderer.DrawString(spriteBatch, warnMsg,
                    new Vector2(warnX + (warnW - wsz.X) / 2f, panelY + (warnH - wsz.Y) / 2f),
                    new Color(255, 210, 120), wszPx);
            }
            else
            {
                float ws = 0.48f * scaleFactor;
                var wsz = font.MeasureString(warnMsg) * ws;
                spriteBatch.DrawString(font, warnMsg,
                    new Vector2(warnX + (warnW - wsz.X) / 2f, panelY + (warnH - wsz.Y) / 2f),
                    new Color(255, 210, 120), 0f, Vector2.Zero, ws, SpriteEffects.None, 0f);
            }
            panelY += warnH + 6;
            panelH -= warnH + 6;
        }

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
        string hint = IsFocused
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
                float arrowW = Math.Max(24f * scaleFactor, arrowSz.X + 12f);
                float rightEdge = panelX + panelW - padX;
                float rightArrowX = rightEdge - arrowW;
                float valX = rightArrowX - 8f - valSz.X;
                float leftArrowX = valX - 8f - arrowW;

                var leftRect = new Rectangle((int)leftArrowX, rowY, (int)arrowW, rowH);
                var rightRect = new Rectangle((int)rightArrowX, rowY, (int)arrowW, rowH);

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
    public bool OpenArrowsMenu = false;
    public bool OpenSplashMenu = false;
    public bool OpenCountdownMenu = false;
    public bool OpenFontMenu = false;
    public bool OpenHudEditor = false;
    public bool ExitMenu = false;
    public bool ToggleVSync = false;
    public bool ToggleFullscreen = false;
    public int AdjustCameraZoom = 0;
    public float AdjustVolume = 0f;
    public float SeekSong = 0f;
}
