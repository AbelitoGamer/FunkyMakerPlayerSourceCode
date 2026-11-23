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
    }

    /// <summary>
    /// Seeks the song to the given time in seconds.
    /// Recreates the audio instance from the stored PCM at the new offset so
    /// audio and chart stay in sync. Events already fired before the seek point
    /// are skipped by the EventProcessor's next-event index.
    /// </summary>
    private void SeekSong(float targetTime)
    {
        if (_level == null) return;
        // Replays (fork): seeking would desync the recording / the playback.
        if (IsWatchingReplay) return;
        InvalidateReplayRecording("timeskip");
        _runAssisted = true; // no personal best after a timeskip (fork)
        targetTime = Math.Clamp(targetTime, 0f, _songEndTime - 0.1f);

        // ── Advance chart timing ─────────────────────────────────────────────
        _pausedTime = _songStopwatch.Elapsed.TotalSeconds - targetTime / _runRate;
        _songTime   = targetTime;
        _songBeats  = _songTime / 60f * (_level.Data.EffectiveBpm);
        // If paused, reset the pause start so accumulated pause time stays correct
        if (_paused) _pauseStarted = _songStopwatch.Elapsed.TotalSeconds;

        // ── Seek note renderers (skip notes before targetTime) ───────────────
        foreach (var r in _noteRenderers)
            r.SeekTo(targetTime);

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
        if (_musicPcm != null)
        {
            var seeked = OggLoader.SeekPcm(_musicPcm, _musicSampleRate, _musicChannels, targetTime);
            if (seeked != null)
            {
                _musicInst?.Dispose();
                _music?.Dispose();
                _music = seeked;
                _musicInst = _music.CreateInstance();
                _musicInst.Volume = SongPlaybackVolume;
                _musicInst.IsLooped = false;
                ApplyRunRateToMusic(); // Freeplay song rate (fork)
                if (!_paused) _musicInst.Play();
            }
        }
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
        if (_level != null) SetupLevel();
    }
}
