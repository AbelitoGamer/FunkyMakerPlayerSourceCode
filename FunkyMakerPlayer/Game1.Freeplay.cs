using System;
using Microsoft.Xna.Framework;

namespace FunkyMakerPlayer;

/// <summary>
/// Fork: Freeplay play modifiers and personal bests in gameplay.
/// <list type="bullet">
/// <item>Song rate (<see cref="FreeplayData.SongRate"/>, 0.5–2.0x): song time runs at the rate and the
/// music is pitched to match (like Psych / osu! DT-HT, the pitch changes with the speed).</item>
/// <item>Enemy mode (<see cref="FreeplayData.EnemyMode"/>): strumline 0 (yours, right) gets the enemy
/// chart and strumline 1 the player chart, icons are swapped and mirrored (as online role 2). The
/// level's health effects (PlayerHealth from the timeline or note action packs) are ignored then:
/// they are made against the player side (e.g. Bad Day drains health on a timer and fills the
/// player's bar near the end, which would kill you), so only your hits and misses move the bar.</item>
/// <item>Autoplay is <see cref="NoteRenderer.Botplay"/>.</item>
/// </list>
/// They apply to solo play only: not online, in replays or Chart Editor playtests.
/// Every solo attempt (finished or game over) is saved in freeplay_data.json with its stats and the
/// replay saved with it; the personal best is the best finished run at 1.00x. Runs with botplay
/// at any point or a timeskip aren't saved.
/// </summary>
public partial class Game1
{
    private float _runRate = 1f;
    private bool _runEnemy;
    private bool _runAssisted;          // botplay or a timeskip happened during this run

    /// <summary>Song rate of the level on screen (1 outside solo play).</summary>
    public static float CurrentRunRate => Instance?._runRate ?? 1f;
    /// <summary>The level on screen is played on the enemy side (solo enemy mode).</summary>
    public static bool CurrentRunEnemy => Instance?._runEnemy ?? false;

    private bool RunRateChanged => Math.Abs(_runRate - 1f) > 0.001f;

    /// <summary>Called at the start of SetupLevel (also on restart): reads the Freeplay modifiers.</summary>
    private void ConfigureRunModifiers()
    {
        bool solo = _level != null && !_isMultiplayerMatch && _activeReplay == null && !_playtestingFromEditor;
        var fp = FreeplayData.Instance;
        _runRate = solo ? FreeplayData.ClampRate(fp.SongRate) : 1f;
        _runEnemy = solo && fp.EnemyMode && _level!.Data.Strumlines.Count >= 2;
        _runAssisted = false;
        if (RunRateChanged || _runEnemy)
            Console.WriteLine($"[Freeplay] Modifiers: rate {_runRate:0.00}x{(_runEnemy ? ", enemy mode" : "")}");
    }

    /// <summary>Pitches the music instance to the run's song rate (call after every CreateInstance).</summary>
    private void ApplyRunRateToMusic()
    {
        if (_musicInst == null) return;
        try { _musicInst.Pitch = RunRateChanged ? Math.Clamp(MathF.Log2(_runRate), -1f, 1f) : 0f; }
        catch (Exception ex) { Console.WriteLine($"[Freeplay] Could not set the song rate: {ex.Message}"); }
    }

    /// <summary>Replay file saved for the run that just finished (set by OnSoloSongFinishedForReplay).</summary>
    private string? _lastSavedReplayPath;

    /// <summary>Solo song finished normally: save the attempt (and say so when it's a new best).</summary>
    private void OnSoloSongFinishedForFreeplay()
    {
        string? replay = _lastSavedReplayPath;
        _lastSavedReplayPath = null;
        var run = BuildAttempt(failed: false);
        if (run == null) return;
        run.ReplayPath = replay;
        if (FreeplayData.Instance.RecordAttempt(FnmmPath, run))
        {
            Console.WriteLine($"[Freeplay] New best: {run.Score} ({run.Accuracy:0.00}%)");
            _freeplay?.ShowToastLater($"Nuevo record: {run.Score:N0} ({run.Accuracy:0.00}%)");
        }
    }

    /// <summary>Game over in solo play: saved as a failed attempt.</summary>
    private void OnSoloRunFailedForFreeplay()
    {
        var run = BuildAttempt(failed: true);
        if (run == null) return;
        run.Progress = _songEndTime > 0 ? Math.Clamp(_songTime / _songEndTime, 0f, 1f) : 0f;
        FreeplayData.Instance.RecordAttempt(FnmmPath, run);
    }

    /// <summary>
    /// The current solo run as an attempt, or null when it doesn't count: replays, playtests, online,
    /// botplay at any point, a timeskip, or nothing judged yet. Song-rate runs are kept (marked with
    /// their rate) but never count as a personal best.
    /// </summary>
    private FreeplayAttempt? BuildAttempt(bool failed)
    {
        if (_activeReplay != null || _playtestingFromEditor || _isMultiplayerMatch || _noteRenderers.Count == 0) return null;
        if (string.IsNullOrEmpty(FnmmPath)) return null;
        if (_runAssisted || NoteRenderer.Botplay)
        {
            Console.WriteLine("[Freeplay] Attempt not saved (botplay or timeskip)");
            return null;
        }
        var r = _noteRenderers[0];
        if (r.Sicks + r.Goods + r.Bads + r.Shits + r.Misses == 0) return null;
        return new FreeplayAttempt
        {
            Score = r.Score,
            Accuracy = ComputeAccuracy(r.Sicks, r.Goods, r.Bads, r.Shits, r.Misses),
            Misses = r.Misses,
            MaxCombo = _playerMaxCombo,
            Sicks = r.Sicks, Goods = r.Goods, Bads = r.Bads, Shits = r.Shits,
            DateUtc = DateTime.UtcNow,
            Enemy = _runEnemy,
            Rate = _runRate,
            Failed = failed,
        };
    }
}
