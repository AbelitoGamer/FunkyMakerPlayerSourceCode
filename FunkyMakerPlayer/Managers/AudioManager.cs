using System;
using Microsoft.Xna.Framework.Audio;

namespace FunkyMakerPlayer.Managers;

/// <summary>
/// Manages audio playback, seeking, volume control, and device recovery.
/// Simplified version - timing managed externally by Game1.
/// </summary>
public class AudioManager
{
    // ── Audio State ───────────────────────────────────────────────────────────
    private SoundEffect?         _music;
    private SoundEffectInstance? _musicInst;
    private byte[]?              _musicPcm;        // Raw PCM for seeking (OGG only)
    private int                  _musicSampleRate;
    private int                  _musicChannels;

    // ── Properties ────────────────────────────────────────────────────────────
    public float Volume
    {
        get => _musicInst?.Volume ?? 0f;
        set
        {
            if (_musicInst != null)
                _musicInst.Volume = Math.Clamp(value, 0f, 1f);
        }
    }

    public bool IsPlaying => _musicInst?.State == SoundState.Playing;
    public bool IsLoaded => _music != null;
    public bool CanSeek => _musicPcm != null;
    public SoundState State => _musicInst?.State ?? SoundState.Stopped;

    // ── Events ────────────────────────────────────────────────────────────────
    public event Action? OnAudioDeviceReset;

    // ── Initialization ────────────────────────────────────────────────────────

    public AudioManager()
    {
        Console.WriteLine("[AudioManager] Initialized");
    }

    /// <summary>
    /// Loads music from raw data (supports WAV and OGG).
    /// </summary>
    public void LoadMusic(byte[] musicData, float volume)
    {
        Dispose();

        // Detect WAV vs OGG by magic bytes
        bool isWav = musicData.Length > 4 &&
                     musicData[0] == 'R' && musicData[1] == 'I' &&
                     musicData[2] == 'F' && musicData[3] == 'F';

        if (isWav)
        {
            _music = SoundEffect.FromStream(new System.IO.MemoryStream(musicData));
            _musicPcm = null;  // WAV: no seek support
            Console.WriteLine("[AudioManager] Loaded WAV music (no seek support)");
        }
        else
        {
            _music = OggLoader.LoadWithPcm(musicData,
                out _musicPcm, out _musicSampleRate, out _musicChannels);
            Console.WriteLine($"[AudioManager] Loaded OGG music (seek enabled, {_musicSampleRate}Hz, {_musicChannels}ch)");
        }

        _musicInst = _music.CreateInstance();
        _musicInst.Volume = volume;
        _musicInst.IsLooped = false;
    }

    // ── Playback Control ──────────────────────────────────────────────────────

    /// <summary>
    /// Starts playing the music from the beginning.
    /// </summary>
    public void Play()
    {
        if (_musicInst == null)
        {
            Console.WriteLine("[AudioManager] Cannot play - no music loaded");
            return;
        }

        _musicInst.Play();
        Console.WriteLine("[AudioManager] Started playback");
    }

    /// <summary>
    /// Pauses the music.
    /// </summary>
    public void Pause()
    {
        if (_musicInst == null)
            return;

        _musicInst.Pause();
        Console.WriteLine("[AudioManager] Paused");
    }

    /// <summary>
    /// Resumes the music.
    /// </summary>
    public void Resume()
    {
        if (_musicInst == null)
            return;

        _musicInst.Resume();
        Console.WriteLine("[AudioManager] Resumed");
    }

    /// <summary>
    /// Stops the music completely.
    /// </summary>
    public void Stop()
    {
        if (_musicInst == null)
            return;

        _musicInst.Stop();
        Console.WriteLine("[AudioManager] Stopped");
    }

    // ── Seeking ───────────────────────────────────────────────────────────────

    /// <summary>
    /// Seeks to a specific time in the song (OGG only).
    /// Returns the new SoundEffect instance.
    /// </summary>
    public void Seek(float targetTime, bool shouldPlay)
    {
        if (_musicPcm == null)
        {
            Console.WriteLine("[AudioManager] Seek not supported for WAV files");
            return;
        }

        try
        {
            // Dispose current instance
            _musicInst?.Stop();
            _musicInst?.Dispose();
            _music?.Dispose();

            // Seek using OggLoader
            _music = OggLoader.SeekPcm(_musicPcm, _musicSampleRate, _musicChannels, targetTime);
            
            if (_music != null)
            {
                _musicInst = _music.CreateInstance();
                _musicInst.Volume = Volume;
                _musicInst.IsLooped = false;
                
                if (shouldPlay)
                    _musicInst.Play();
                
                Console.WriteLine($"[AudioManager] Seeked to {targetTime:F2}s");
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[AudioManager] Seek failed: {ex.Message}");
        }
    }

    // ── Device Recovery ───────────────────────────────────────────────────────

    /// <summary>
    /// Marks that the audio device was reset.
    /// </summary>
    public void MarkDeviceReset()
    {
        Console.WriteLine("[AudioManager] Audio device reset detected");
        OnAudioDeviceReset?.Invoke();
    }

    /// <summary>
    /// Attempts to recover from audio device issues.
    /// Call when audio unexpectedly stops.
    /// </summary>
    public bool TryRecover(float currentTime, bool shouldPlay)
    {
        if (_music == null)
            return false;

        try
        {
            Console.WriteLine($"[AudioManager] Attempting recovery at {currentTime:F2}s");
            
            _musicInst?.Dispose();
            _musicInst = _music.CreateInstance();
            _musicInst.Volume = Volume;
            _musicInst.IsLooped = false;
            
            if (shouldPlay)
                _musicInst.Play();
            
            Console.WriteLine("[AudioManager] Recovery successful");
            return true;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[AudioManager] Recovery failed: {ex.Message}");
            return false;
        }
    }

    // ── Cleanup ───────────────────────────────────────────────────────────────

    /// <summary>
    /// Disposes all audio resources.
    /// </summary>
    public void Dispose()
    {
        _musicInst?.Stop();
        _musicInst?.Dispose();
        _music?.Dispose();

        _musicInst = null;
        _music = null;
        _musicPcm = null;

        Console.WriteLine("[AudioManager] Disposed");
    }
}
