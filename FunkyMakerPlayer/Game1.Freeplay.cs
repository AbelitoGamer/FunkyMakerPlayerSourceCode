using System;
using System.Collections.Generic;
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

    /// <summary>
    /// Song time the notes are judged and drawn at: the clock minus the global audio offset
    /// (positive = notes later; scaled by the song rate since song time runs at it). Events,
    /// characters and the music keep the plain song time.
    /// </summary>
    private float NoteTime => _songTime - GlobalOffsetMs / 1000f * _runRate;

    /// <summary>Called at the start of SetupLevel (also on restart): reads the Freeplay modifiers.</summary>
    private void ConfigureRunModifiers()
    {
        bool solo = _level != null && !_isMultiplayerMatch && _activeReplay == null && !_playtestingFromEditor;
        var fp = FreeplayData.Instance;
        _runRate = solo ? FreeplayData.ClampRate(fp.SongRate) : 1f;
        _runEnemy = solo && fp.EnemyMode && _level!.Data.Strumlines.Count >= 2;
        _runAssisted = false;
        _playerMaxCombo = 0;
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

    /// <summary>Timing errors of the run in real milliseconds (song time / rate; negative = early).</summary>
    private List<float> RunHitErrorsMs()
    {
        var list = new List<float>();
        if (_noteRenderers.Count == 0) return list;
        float rate = _runRate > 0f ? _runRate : 1f;
        foreach (float e in _noteRenderers[0].HitErrors) list.Add(e * 1000f / rate);
        return list;
    }

    /// <summary>Average error (ms) and unstable rate (10 × standard deviation in ms), or nulls with no hits.</summary>
    public static (float? Mean, float? Ur) TimingStats(IReadOnlyList<float> errorsMs)
    {
        if (errorsMs.Count == 0) return (null, null);
        double sum = 0;
        foreach (float e in errorsMs) sum += e;
        double mean = sum / errorsMs.Count;
        double var = 0;
        foreach (float e in errorsMs) var += (e - mean) * (e - mean);
        return ((float)mean, (float)(Math.Sqrt(var / errorsMs.Count) * 10.0));
    }

    /// <summary>Notes on the side being played (the full combo) and its star rating at the run's rate.</summary>
    private (int Notes, double Stars) RunChartInfo()
    {
        try
        {
            if (_level == null) return (0, -1);
            int side = _runEnemy && _level.Data.Strumlines.Count >= 2 ? 1 : 0;
            var notes = ChartDifficulty.ExtractNotes(_level.Data, side, out int columns);
            return (notes.Count, ChartDifficulty.Calculate(notes, columns, _runRate));
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[Freeplay] Could not rate the chart for the results: {ex.Message}");
            return (0, -1);
        }
    }

    /// <summary>
    /// Solo song finished normally: save the attempt (and say so when it's a new best), and return
    /// what the results screen shows (this run plus every other attempt on the same side).
    /// </summary>
    private UI.SoloResultsScreen.Run OnSoloSongFinishedForFreeplay()
    {
        string? replay = _lastSavedReplayPath;
        _lastSavedReplayPath = null;
        var r = _noteRenderers.Count > 0 ? _noteRenderers[0] : null;
        var errors = RunHitErrorsMs();
        var (mean, ur) = TimingStats(errors);
        var (chartNotes, stars) = RunChartInfo();

        // This run as an attempt (a copy is shown when it isn't saved: replay, botplay...).
        var current = new FreeplayAttempt
        {
            Score = r?.Score ?? 0,
            Accuracy = r == null ? 0f : ComputeAccuracy(r.Sicks, r.Goods, r.Bads, r.Shits, r.Misses),
            Misses = r?.Misses ?? 0,
            MaxCombo = _playerMaxCombo,
            Sicks = r?.Sicks ?? 0, Goods = r?.Goods ?? 0, Bads = r?.Bads ?? 0, Shits = r?.Shits ?? 0,
            DateUtc = DateTime.UtcNow,
            Enemy = _runEnemy,
            Rate = _runRate,
            ReplayPath = replay,
            HitErrorMs = mean,
            UnstableRate = ur,
            ChartNotes = chartNotes,
        };
        var result = new UI.SoloResultsScreen.Run
        {
            Stars = stars,
            HitErrorsMs = errors,
            Current = current,
        };
        FillSongInfo(result);

        string? path = string.IsNullOrEmpty(FnmmPath) ? null : FnmmPath;
        var record = path != null ? FreeplayData.Instance.Get(path) : null;
        // Watching a replay: the run it was recorded from is this one (file name ends with the replay id).
        FreeplayAttempt? source = null;
        if (_activeReplay != null && record != null && _activeReplay.Id.Length >= 6)
        {
            string tail = $" {_activeReplay.Id[..6]}{Replay.ReplayFile.Extension}";
            source = record.Attempts.Find(a => a.ReplayPath != null && a.ReplayPath.EndsWith(tail, StringComparison.OrdinalIgnoreCase));
            if (source != null)
            {
                current.Enemy = source.Enemy;
                current.Rate = source.Rate;
                current.DateUtc = source.DateUtc;
                current.ReplayPath = source.ReplayPath;
            }
        }
        void AddOthers()
        {
            if (record == null) return;
            foreach (var a in record.Attempts)
                if (a.Enemy == current.Enemy && a != source) result.Others.Add(a);
        }

        if (_activeReplay != null) { result.NotSavedReason = "Replay"; AddOthers(); return result; }
        if (path == null) { result.NotSavedReason = "Not saved"; return result; }
        var run = BuildAttempt(failed: false);
        if (run == null)
        {
            result.NotSavedReason = _runAssisted || NoteRenderer.Botplay ? "Botplay / timeskip: not saved" : "Not saved";
            AddOthers();
            return result;
        }
        run.ReplayPath = replay;
        AddOthers(); // before saving: the others don't include this run
        if (FreeplayData.Instance.RecordAttempt(path, run))
        {
            Console.WriteLine($"[Freeplay] New best: {run.Score} ({run.Accuracy:0.00}%)");
            result.NewBest = true;
        }
        else if (!run.Ranked)
        {
            result.NotSavedReason = $"{_runRate:0.00}x: doesn't count as a record";
        }
        result.Current = run;
        return result;
    }

    /// <summary>Title, mapper and difficulty name for the results screen.</summary>
    private void FillSongInfo(UI.SoloResultsScreen.Run result)
    {
        result.SongName = _level?.LevelName ?? "";
        if (_level?.IsOsu == true && !string.IsNullOrEmpty(FnmmPath))
        {
            try
            {
                var map = Osu.OsuBeatmap.ParseFile(FnmmPath, headerOnly: true);
                if (map.SongName.Length > 0) result.SongName = map.SongName;
                result.Mapper = map.Creator;
                result.Difficulty = map.Version.Length > 0 ? map.Version : "osu!mania";
            }
            catch (Exception ex) { Console.WriteLine($"[Freeplay] Could not read the beatmap header: {ex.Message}"); }
        }
        else
        {
            result.Difficulty = _runEnemy ? "Enemy side" : "Player side";
        }
        if (_activeReplay != null) result.Difficulty += "  |  Replay";
    }

    /// <summary>Results screen input: continue to Freeplay, retry, or watch this run's replay.</summary>
    private void UpdateSoloResults(float dt, Microsoft.Xna.Framework.Input.KeyboardState kb,
        Microsoft.Xna.Framework.Input.MouseState ms, Microsoft.Xna.Framework.Input.MouseState prevMs)
    {
        var results = _soloResults!;
        var win = Window.ClientBounds;
        var (dst, scale) = ComputeLetterbox(win.Width, win.Height);
        Point pointer = scale > 0.001f
            ? new Point((int)((ms.X - dst.X) / scale), (int)((ms.Y - dst.Y) / scale))
            : new Point(-9999, -9999);
        bool down = ms.LeftButton == Microsoft.Xna.Framework.Input.ButtonState.Pressed;
        // Wheel notches (vertical or horizontal; + = left)
        float wheel = (ms.ScrollWheelValue - prevMs.ScrollWheelValue) / 120f
                      - (ms.HorizontalScrollWheelValue - prevMs.HorizontalScrollWheelValue) / 120f;
        if (OperatingSystem.IsAndroid())
        {
            // Touches don't arrive as mouse input on Android: a finger down = the button held.
            down = false;
            foreach (var t in Microsoft.Xna.Framework.Input.Touch.TouchPanel.GetState())
            {
                if (t.State is Microsoft.Xna.Framework.Input.Touch.TouchLocationState.Released
                    or Microsoft.Xna.Framework.Input.Touch.TouchLocationState.Invalid) continue;
                down = true;
                _soloResultsTouchPos = scale > 0.001f
                    ? new Point((int)((t.Position.X - dst.X) / scale), (int)((t.Position.Y - dst.Y) / scale))
                    : new Point(-9999, -9999);
                break;
            }
            pointer = _soloResultsTouchPos;
            wheel = 0f;
        }

        switch (results.Update(dt, kb, _prevKb, pointer, down, wheel))
        {
            case UI.SoloResultsScreen.Choice.Retry:
                _soloResults = null;
                RestartSong(); // like the game over's Retry (SetupLevel: new attempt, new recording)
                break;
            case UI.SoloResultsScreen.Choice.WatchReplay:
                string? path = results.ReplayPath;
                _soloResults = null;
                var replay = path != null ? Replay.ReplayStore.Load(path) : null;
                string? error = replay == null ? "No se pudo abrir la replay" : StartReplay(replay);
                if (error != null) { _freeplay?.ShowToastLater(error); GoToFreeplayAfterResults(); }
                break;
            case UI.SoloResultsScreen.Choice.Continue:
                _soloResults = null;
                GoToFreeplayAfterResults();
                break;
        }
    }

    private void GoToFreeplayAfterResults()
    {
        _freeplay?.ResetInputCooldown();
        _songTransition = new SongTransition(_catTex, _pixel!);
        _songTransition.Start(_rtWidth, _rtHeight);
        _transitionToFreeplay = true;
    }

    /// <summary>Game over in solo play: saved as a failed attempt.</summary>
    private void OnSoloRunFailedForFreeplay()
    {
        var run = BuildAttempt(failed: true);
        if (run == null) return;
        run.Progress = _chartEndTime > 0 ? Math.Clamp(_songTime / _chartEndTime, 0f, 1f) : 0f; // progress through the chart
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
        var (mean, ur) = TimingStats(RunHitErrorsMs());
        return new FreeplayAttempt
        {
            HitErrorMs = mean,
            UnstableRate = ur,
            ChartNotes = RunChartInfo().Notes,
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
