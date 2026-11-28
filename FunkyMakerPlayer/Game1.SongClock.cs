using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Audio;
using Microsoft.Xna.Framework.Graphics;
using Microsoft.Xna.Framework.Input;
using SixLabors.ImageSharp.Processing;
using FunkyMakerPlayer.Managers;  // ← NUEVO: Import de managers
using FunkyMakerPlayer.UI;  // ← NUEVO: Import de UI menus
using FunkyMakerPlayer.Multiplayer;  // ← NUEVO: Import de multiplayer
using MonoGame.Extended;  // ← MonoGame.Extended for OrthographicCamera

namespace FunkyMakerPlayer;

/// <summary>Song clock: pause, resume, seek, restart (moved out of Game1.cs).</summary>
public partial class Game1
{
    // ── Music ↔ chart sync (fork) ────────────────────────────────────────────────
    // The song clock is the Stopwatch (multiplayer start times and replays rely on it); the music is
    // an OpenAL source playing on its own. Nothing tied them together, so anything that stalled the
    // audio but not the clock (a hitch on Android, an audio underrun, the PCM copy of a timeskip)
    // left them apart for the rest of the song. Now the music's real position is compared with the
    // clock every frame and moved back when they drift apart.
    private float  _musicInstStart;        // song time where the current _musicInst's buffer starts
    private double _musicDriftAvg;         // smoothed (music − clock) in seconds
    private bool   _musicDriftValid;
    private float  _musicSyncHold;         // seconds to wait before measuring (after a play / seek)
    private const double MusicDriftSmoothing = 0.2;   // time constant of the smoothing, seconds
    // Smoothed drift that triggers a resync. Widened (up to 150 ms) if resyncs come back to back,
    // i.e. the device reports its position too coarsely / with a fixed bias, so it never loops.
    private double _musicDriftTolerance = 0.045;
    private double _musicLastResyncAt = double.NegativeInfinity; // wall-clock seconds

    /// <summary>Forget the measured drift (new instance, seek, resume) and wait a moment before measuring.</summary>
    private void ResetMusicSync(float hold = 0.25f)
    {
        _musicDriftValid = false;
        _musicDriftAvg = 0;
        _musicSyncHold = hold;
    }

    /// <summary>
    /// Per frame while the song plays: keeps the music on the song clock. Small, short wobbles (the
    /// mixer reports its position in ~10-20 ms steps) are smoothed out; a real drift moves the music
    /// in place (no PCM copy), or recreates it at the clock's time when that isn't possible.
    /// </summary>
    private void KeepMusicInSync(float dt)
    {
        if (_musicInst == null || _music == null || _musicInst.State != SoundState.Playing)
        {
            _musicDriftValid = false;
            return;
        }
        if (_musicSyncHold > 0f)
        {
            _musicSyncHold -= dt;
            return;
        }
        if (MusicPosition.Get(_musicInst, _musicSampleRate) is not { } pos) return;

        double bufferSeconds = _music.Duration.TotalSeconds;
        if (pos >= bufferSeconds - 0.1) return; // about to end: nothing to fix

        double drift = _musicInstStart + pos - _songTime; // > 0: music ahead of the chart
        if (!_musicDriftValid)
        {
            _musicDriftAvg = drift;
            _musicDriftValid = true;
        }
        else
        {
            double k = 1 - Math.Exp(-Math.Max(dt, 0.001f) / MusicDriftSmoothing);
            _musicDriftAvg += (drift - _musicDriftAvg) * k;
        }
        if (Math.Abs(_musicDriftAvg) < _musicDriftTolerance) return;

        Console.WriteLine($"[Audio Sync] Music {(_musicDriftAvg > 0 ? "ahead of" : "behind")} the chart by " +
                          $"{Math.Abs(_musicDriftAvg) * 1000:0} ms at {_songTime:0.00}s, resyncing");
        double now = Stopwatch.GetTimestamp() / (double)Stopwatch.Frequency;
        if (now - _musicLastResyncAt < 3.0 && _musicDriftTolerance < 0.15)
        {
            _musicDriftTolerance = Math.Min(_musicDriftTolerance * 1.5, 0.15);
            Console.WriteLine($"[Audio Sync] Back-to-back resyncs: tolerance now {_musicDriftTolerance * 1000:0} ms");
        }
        _musicLastResyncAt = now;
        if (!MusicPosition.TrySet(_musicInst, _musicSampleRate, _songTime - _musicInstStart, bufferSeconds))
            RecreateMusicAt(_songTime);
        ResetMusicSync();
    }

    /// <summary>Recreates the music instance from the stored PCM starting at <paramref name="time"/>; plays it unless paused.</summary>
    private bool RecreateMusicAt(float time)
    {
        if (_musicPcm == null) return false;
        var seeked = OggLoader.SeekPcm(_musicPcm, _musicSampleRate, _musicChannels, time);
        if (seeked == null) return false;
        _musicInst?.Dispose();
        _music?.Dispose();
        _music = seeked;
        _musicInstStart = time;
        _musicInst = _music.CreateInstance();
        _musicInst.Volume = SongPlaybackVolume;
        _musicInst.IsLooped = false;
        ApplyRunRateToMusic(); // Freeplay song rate (fork)
        if (!_paused) _musicInst.Play();
        ResetMusicSync();
        return true;
    }

    private void PauseSong()
    {
        if (!_started || _paused) return;
        _paused = true;
        _pauseStarted = _songStopwatch.Elapsed.TotalSeconds;
        _musicInst?.Pause();
    }

    private void ResumeSong()
    {
        if (!_paused) return;
        _paused = false;
        // Accumulate how long we were paused
        _pausedTime += _songStopwatch.Elapsed.TotalSeconds - _pauseStarted;
        // If audio was seeked while paused, it's ready but not playing — start it now
        if (_musicInst?.State == SoundState.Paused)
            _musicInst?.Resume();
        else
            _musicInst?.Play();
        ResetMusicSync();
    }

    /// <summary>
    /// Seeks the song to the given time in seconds.
    /// Recreates the audio instance from the stored PCM at the new offset so
    /// audio and chart stay in sync. Events already fired before the seek point
    /// are skipped by the EventProcessor's next-event index.
    /// </summary>
    /// <param name="breakSkip">
    /// Skip Breaks jump (Game1.BreakSkip.cs): not a timeskip, so the run and its replay stay valid,
    /// it works while watching a replay (that's how playback repeats it) and safe frames keep their start.
    /// </param>
    private void SeekSong(float targetTime, bool breakSkip = false)
    {
        if (_level == null) return;
        if (!breakSkip)
        {
            // Replays (fork): seeking would desync the recording / the playback.
            if (IsWatchingReplay) return;
            InvalidateReplayRecording("timeskip");
            _runAssisted = true; // no personal best after a timeskip (fork)
        }
        targetTime = Math.Clamp(targetTime, 0f, _songEndTime - 0.1f);

        // ── Advance chart timing ─────────────────────────────────────────────
        _pausedTime = _songStopwatch.Elapsed.TotalSeconds - targetTime / _runRate;
        _songTime   = targetTime;
        _songBeats  = _songTime / 60f * (_level.Data.EffectiveBpm);
        // If paused, reset the pause start so accumulated pause time stays correct
        if (_paused) _pauseStarted = _songStopwatch.Elapsed.TotalSeconds;

        // ── Seek note renderers (skip notes before targetTime) ───────────────
        foreach (var r in _noteRenderers)
            r.SeekTo(targetTime, keepStartTime: breakSkip);
        // A backward seek rolls the player's stats back (NoteRenderer.SeekTo); max combo follows
        if (_noteRenderers.Count > 0) _playerMaxCombo = _noteRenderers[0].MaxCombo;

        // ── Seek events ──────────────────────────────────────────────────────
        _events?.SeekTo(targetTime);

        // ── Reset character animators to idle ────────────────────────────────
        // After seek, clear any active sing pose / hold so characters return to idle.
        // Then check for active notes at targetTime and apply correct animations.
        // IMPORTANT: Don't reset animations that were set by events - events have priority!
        foreach (var anim in _objAnimators)
        {
            if (anim == null || !anim.IsCharacter) continue;
            
            // Skip if animation was set by an event - events control these animations
            if (anim.IsSetByEvent) continue;
            
            anim.HoldUntil = -1f;
            anim.SetAnimation(SpriteAnimator.IdleAnim);
        }

        // ── Apply animations for active notes at targetTime ──────────────────
        // This ensures characters show the correct pose when seeking backward/forward
        // during singing or shooting sequences.
        var objs = _level.Scene.Objects;
        for (int strumlineIdx = 0; strumlineIdx < _noteRenderers.Count; strumlineIdx++)
        {
            var renderer = _noteRenderers[strumlineIdx];
            
            // Check each lane for notes active at targetTime
            for (int lane = 0; lane < NoteRenderer.LANE_COUNT; lane++)
            {
                // Find if there's a note active at targetTime in this lane
                // A note is "active" if targetTime is within its hit window or hold duration
                var strumline = _level.Data.Strumlines[strumlineIdx];
                if (lane >= strumline.Count) continue;
                
                var notesInLane = strumline[lane];
                FnmmNote? activeNote = null;
                
                foreach (var note in notesInLane)
                {
                    float noteStart = note.Start;
                    float noteEnd = note.Start + note.Duration;
                    
                    // Check if targetTime is within the note's active period
                    // (hit window before start + hold duration)
                    float hitWindowStart = noteStart - NoteRenderer.HIT_WINDOW;
                    if (targetTime >= hitWindowStart && targetTime <= noteEnd)
                    {
                        activeNote = note;
                        break;
                    }
                }
                
                // If we found an active note, pose the strumline's characters exactly as a
                // normal note hit would (same sing anim, all characters, event-locked ones skipped).
                if (activeNote != null)
                {
                    float holdEnd = activeNote.Duration > 0.1f ? activeNote.Start + activeNote.Duration : -1f;
                    FnmmSceneRenderer.TriggerCharacterSing(_level, _objAnimators, strumlineIdx, lane, targetTime, holdEnd);

                    // Only process one active note per strumline (the first one found)
                    break;
                }
            }
        }

        // ── Seek audio ───────────────────────────────────────────────────────────
        // In place when the current buffer covers the target (instant, no copy): copying the PCM
        // tail can take long enough on Android to leave the music behind the already-moved clock.
        // Otherwise (backwards past the buffer start, stopped instance) recreate it from the PCM.
        if (_music != null && _musicInst != null && _musicInst.State != SoundState.Stopped &&
            MusicPosition.TrySet(_musicInst, _musicSampleRate, targetTime - _musicInstStart, _music.Duration.TotalSeconds))
            ResetMusicSync();
        else
            RecreateMusicAt(targetTime);
    }

    private void RestartSong()
    {
        _songTime        = 0f;  // Will be set to negative during countdown
        _songBeats       = 0f;
        _started         = false;
        _paused          = false;
        _gameplayPresenceStartUtc = null;
        if (_healthBar != null)
            _healthBar.Health = 0.5f;
        _gameOverOverlay?.Hide();
        _countdownOverlay?.EnsureLoaded(SelectedCountdown);
        _countdownOverlay?.Reset();
        
        // Start FNF-style receptor intro animation (pop-in effect)
        // Only if HUD is visible (respects chart events that hide receptors)
        if (!EventHidesHud)
        {
            foreach (var renderer in _noteRenderers)
            {
                renderer?.StartReceptorIntro(0.0f, 0.05f);
            }
        }
        
        // Reset camera zoom
        _cameraManager?.Reset();
        _songTransition  = null;
        _songEndDelay    = 0f;
        _keyBindOverlay.Reset();
        _settingsManager?.Reset();
        _inPauseMenu     = false;
        _notesAlpha      = 1.0f;
        _songStopwatch.Reset();
        _pausedTime      = 0.0;
        _musicInst?.Stop();
        ResetMusicSync();
        if (_level != null) SetupLevel();
    }
}
