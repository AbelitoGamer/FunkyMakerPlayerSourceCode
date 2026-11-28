using System;
using System.Collections.Generic;
using System.IO;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Audio;

namespace FunkyMakerPlayer.UI;

/// <summary>Semantic UI sound events. Each maps to a WAV in <c>Assets/sounds/ui/</c>.</summary>
public enum UiSound
{
    // Navigation
    Move,           // cursor moved in a menu / card list
    MoveList,       // cursor moved in a long list (levels, panels)
    MoveRow,        // cursor moved between setting rows
    Tab,            // settings tab change
    SubTab,         // secondary tabs / mode switches
    // Actions
    Confirm,        // open / accept
    Back,           // close / cancel
    Toggle,         // on/off option
    Focus,          // focus an adjustable row / pick a tool
    SliderUp,
    SliderDown,
    SliderEnd,      // value already at its limit
    Error,          // action not allowed
    AlreadyInUse,   // picked the option that is already active
    // Bigger moments
    StartSong,
    StyleSelect,    // note style / splash / countdown / font chosen
    ColorSelect,
    EditorSelect,   // editor chosen in the Editor Hub
    Notice,         // toast / notification
    Receive,        // update available, match found
    Wait,           // waiting / searching started
    Save,
    Delete,
    Undo,
    // Text input
    Type,
    TypeFull,
    // HUD Editor manipulation
    NudgeUp,
    NudgeDown,
    NudgeLeft,
    NudgeRight,
    Grab,
    Release,
    CycleNext,
    CyclePrev,
    // Loading
    LoadTick,
}

/// <summary>
/// UI sound effects. Sounds are loaded lazily from <c>Assets/sounds/ui/*.wav</c> (desktop: next to
/// the exe; Android: extracted assets folder, falling back to the APK via TitleContainer).
///
/// Requests made during a frame are queued and only the highest-priority one plays when
/// <see cref="Flush"/> runs (once per Update). This avoids stacked sounds when one input triggers
/// several reactions (e.g. a menu "confirm" that also opens a screen).
///
/// Effective volume = Master volume (<see cref="Game1.MasterVolume"/>) × <see cref="Volume"/> × per-sound gain.
/// </summary>
public static class UiSfx
{
    /// <summary>SFX volume (0..1), persisted in settings.json as <c>SfxVolume</c>.</summary>
    public static float Volume = 1.0f;

    private readonly record struct SoundDef(string[] Files, int Priority, float Gain);

    private static readonly Dictionary<UiSound, SoundDef> Defs = new()
    {
        [UiSound.Move]         = new(new[] { "cursor_move" },          1, 0.9f),
        [UiSound.MoveList]     = new(new[] { "cursor_panel_move" },    1, 0.9f),
        [UiSound.MoveRow]      = new(new[] { "cursor_normal_move" },   1, 0.9f),
        [UiSound.LoadTick]     = new(new[] { "load1", "load2", "load3", "load4" }, 1, 0.6f),
        [UiSound.SliderUp]     = new(new[] { "slider_tick" },          2, 0.9f),
        [UiSound.SliderDown]   = new(new[] { "slider_tick_down" },     2, 0.9f),
        [UiSound.SliderEnd]    = new(new[] { "slider_end" },           2, 0.9f),
        [UiSound.NudgeUp]      = new(new[] { "position_up" },          2, 0.8f),
        [UiSound.NudgeDown]    = new(new[] { "position_down" },        2, 0.8f),
        [UiSound.NudgeLeft]    = new(new[] { "move_together" },        2, 0.8f),
        [UiSound.NudgeRight]   = new(new[] { "move_apart" },           2, 0.8f),
        [UiSound.CycleNext]    = new(new[] { "rotate_cw" },            2, 0.8f),
        [UiSound.CyclePrev]    = new(new[] { "rotate_ccw" },           2, 0.8f),
        [UiSound.Toggle]       = new(new[] { "select_misc" },          3, 1.0f),
        [UiSound.Focus]        = new(new[] { "cursor_press" },         3, 1.0f),
        [UiSound.Tab]          = new(new[] { "change_tab" },           3, 1.0f),
        [UiSound.SubTab]       = new(new[] { "select_tab" },           3, 0.8f),
        [UiSound.Type]         = new(new[] { "keyboard_ok" },          3, 0.8f),
        [UiSound.Grab]         = new(new[] { "start_rotation" },       3, 0.8f),
        [UiSound.Release]      = new(new[] { "stop_rotation" },        3, 0.8f),
        [UiSound.TypeFull]     = new(new[] { "keyboard_full" },        4, 0.9f),
        [UiSound.Back]         = new(new[] { "back" },                 5, 1.0f),
        [UiSound.Confirm]      = new(new[] { "press" },                6, 0.9f),
        [UiSound.AlreadyInUse] = new(new[] { "select_already_in_use" },6, 1.0f),
        [UiSound.StartSong]    = new(new[] { "select" },               7, 0.9f),
        [UiSound.StyleSelect]  = new(new[] { "select_style" },         7, 0.8f),
        [UiSound.ColorSelect]  = new(new[] { "select_color" },         7, 0.8f),
        [UiSound.EditorSelect] = new(new[] { "select_part" },          7, 0.8f),
        [UiSound.Delete]       = new(new[] { "delete" },               7, 0.7f),
        [UiSound.Undo]         = new(new[] { "undo" },                 7, 0.7f),
        [UiSound.Save]         = new(new[] { "finish" },               8, 0.8f),
        [UiSound.Notice]       = new(new[] { "notice" },               8, 0.8f),
        [UiSound.Wait]         = new(new[] { "wait" },                 8, 0.6f),
        [UiSound.Receive]      = new(new[] { "receive" },              9, 0.7f),
        [UiSound.Error]        = new(new[] { "no_click" },             9, 1.0f),
    };

    private static readonly Dictionary<string, SoundEffect?> _cache = new();
    private static readonly Random _rng = new();
    private static readonly object _lock = new();
    private static string? _assetsRoot;
    private static UiSound? _pending;

    /// <summary>
    /// Frame counter, incremented by <see cref="Flush"/>. Menus that keep a state snapshot across
    /// frames use it to know whether they were updated on the previous frame.
    /// </summary>
    public static long Frame { get; private set; }

    /// <summary>Sets the folder that contains <c>sounds/ui</c> (the game's Assets root). Can be null.</summary>
    public static void Initialize(string? assetsRoot) => _assetsRoot = assetsRoot;

    /// <summary>Queues a UI sound for this frame (highest priority wins). Thread-safe.</summary>
    public static void Play(UiSound sound)
    {
        lock (_lock)
        {
            if (_pending == null || Defs[sound].Priority >= Defs[_pending.Value].Priority)
                _pending = sound;
        }
    }

    /// <summary>Plays the queued sound (if any). Call once per frame from the main thread.</summary>
    public static void Flush()
    {
        UiSound? sound;
        lock (_lock)
        {
            sound = _pending;
            _pending = null;
            Frame++;
        }
        if (sound == null) return;

        float volume = Math.Clamp(Game1.MasterVolume * Volume * Defs[sound.Value].Gain, 0f, 1f);
        if (volume <= 0.001f) return;

        var files = Defs[sound.Value].Files;
        var fx = Load(files[files.Length == 1 ? 0 : _rng.Next(files.Length)]);
        try { fx?.Play(volume, 0f, 0f); }
        catch (Exception ex) { Console.WriteLine($"[UiSfx] Play failed: {ex.Message}"); }
    }

    /// <summary>Drops any queued sound without playing it.</summary>
    public static void Cancel()
    {
        lock (_lock) _pending = null;
    }

    private static SoundEffect? Load(string name)
    {
        if (_cache.TryGetValue(name, out var cached)) return cached;

        SoundEffect? fx = null;
        try
        {
            string? path = _assetsRoot != null ? Path.Combine(_assetsRoot, "sounds", "ui", name + ".wav") : null;
            if (path != null && File.Exists(path))
            {
                using var fs = File.OpenRead(path);
                fx = SoundEffect.FromStream(fs);
            }
            else
            {
                // Android APK assets: the Assets folder is the APK's assets root, so no "Assets/"
                // prefix there (fork fix: UI sounds were silent on Android); the prefixed path is
                // the content root next to the exe.
                Stream s;
                try { s = TitleContainer.OpenStream($"sounds/ui/{name}.wav"); }
                catch { s = TitleContainer.OpenStream($"Assets/sounds/ui/{name}.wav"); }
                using (s) fx = SoundEffect.FromStream(s);
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[UiSfx] Could not load '{name}.wav': {ex.Message}");
        }

        _cache[name] = fx; // cache failures too, so a missing file is not retried every frame
        return fx;
    }
}
