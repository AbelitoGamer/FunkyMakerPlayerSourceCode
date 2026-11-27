using Microsoft.Xna.Framework.Audio;
using System;

namespace FunkyMakerPlayer.Editors.Chart;

/// <summary>
/// Simplified audio manager for Chart Editor
/// </summary>
public class EditorAudioManager : IDisposable
{
    private readonly object _lock = new();
    private bool _disposed = false;
    private bool _pendingAudioSeek = false;
    private long _lastSeekTicks = 0;
    private SoundEffect? _music;
    private SoundEffect? _seekMusic;
    private SoundEffectInstance? _musicInst;
    private byte[]? _musicPcm;
    private int _sampleRate;
    private int _channels;
    private float _playbackStartTime = 0f;
    private float _virtualDurationMs = 0f;
    private bool _hasRealAudio = false;
    private bool _isPlaying = false;
    private System.Diagnostics.Stopwatch _playbackTimer = new();

    public bool IsLoaded => _hasRealAudio || _virtualDurationMs > 0f;
    public bool HasAudioTrack => _hasRealAudio;
    public byte[]? CachedPcm => _hasRealAudio ? _musicPcm : null;
    public int SampleRate => _sampleRate;
    public int Channels => _channels;
    public bool IsPlaying => _isPlaying;
    public bool IsPaused => !_isPlaying && _playbackStartTime > 0f;
    public float Volume { get; set; } = 0.5f;

    /// <summary>
    /// Current playback time in milliseconds.
    /// </summary>
    public float CurrentTime
    {
        get
        {
            float t = _isPlaying && _playbackTimer.IsRunning
                ? _playbackStartTime + (float)_playbackTimer.Elapsed.TotalMilliseconds
                : _playbackStartTime;
            float dur = Duration;
            return dur > 0f ? Math.Clamp(t, 0f, dur) : Math.Max(0f, t);
        }
    }

    /// <summary>
    /// Total duration in milliseconds.
    /// </summary>
    public float Duration
    {
        get
        {
            if (_hasRealAudio && _musicPcm != null && _sampleRate > 0 && _channels > 0)
            {
                float bytesPerSample = 2f; // 16-bit PCM
                float totalSamples = _musicPcm.Length / (bytesPerSample * _channels);
                return (totalSamples / _sampleRate) * 1000f;
            }
            return _virtualDurationMs;
        }
    }

    public void LoadFromLevel(FnmmLevel level)
    {
        SoundEffect? decodedMusic = null;
        try
        {
            lock (_lock)
            {
                if (_disposed) return;
                StopAndDisposeInstancesLocked();
                _playbackStartTime = 0f;
                _playbackTimer.Reset();
                _isPlaying = false;
                _pendingAudioSeek = false;
            }

            if (level.MusicData == null || level.MusicData.Length == 0)
            {
                lock (_lock)
                {
                    if (_disposed) return;
                    _hasRealAudio = false;
                    _virtualDurationMs = 180_000f; // 3-minute virtual timeline for silent/template charts
                }
                Console.WriteLine("[EditorAudio] No music track in level — using virtual timeline (180s)");
                return;
            }

            Console.WriteLine("[EditorAudio] Loading music...");
            decodedMusic = AudioDecoder.LoadWithPcm(level.MusicData,
                out byte[] pcm, out int sampleRate, out int channels);

            lock (_lock)
            {
                if (_disposed)
                {
                    decodedMusic.Dispose();
                    return;
                }
                _music = decodedMusic;
                decodedMusic = null;
                _musicPcm = pcm;
                _sampleRate = sampleRate;
                _channels = channels;
                _hasRealAudio = true;
                _virtualDurationMs = 0f;

                CreateInstanceFromOffsetLocked(0f);
            }

            Console.WriteLine($"[EditorAudio] Music loaded ({pcm.Length} bytes, {sampleRate}Hz, {channels}ch, {Duration / 1000f:F2}s)");
        }
        catch (Exception ex)
        {
            try { decodedMusic?.Dispose(); } catch { }
            lock (_lock)
            {
                if (_disposed) return;
                _hasRealAudio = false;
                _virtualDurationMs = 180_000f;
            }
            Console.WriteLine($"[EditorAudio] Failed to load music: {ex.Message}");
        }
    }

    public void Update()
    {
        lock (_lock)
        {
            if (_disposed || !_isPlaying) return;

            if (_pendingAudioSeek && _hasRealAudio)
            {
                double elapsedMs = (System.Diagnostics.Stopwatch.GetTimestamp() - _lastSeekTicks) * 1000.0
                    / System.Diagnostics.Stopwatch.Frequency;
                if (elapsedMs >= 110.0)
                {
                    _pendingAudioSeek = false;
                    float cur = CurrentTime;
                    _playbackStartTime = cur;
                    CreateInstanceFromOffsetLocked(_playbackStartTime);
                    if (_musicInst != null)
                    {
                        _musicInst.Volume = Math.Clamp(Volume * Game1.MasterVolume, 0f, 1f);
                        _musicInst.Play();
                    }
                    _playbackTimer.Restart();
                }
            }

            float dur = Duration;
            if (dur > 0f && CurrentTime >= dur - 20f)
            {
                _playbackStartTime = dur;
                _playbackTimer.Reset();
                _isPlaying = false;
                _pendingAudioSeek = false;
                _musicInst?.Stop();
            }
        }
    }

    public void SetVolume(float volume)
    {
        Volume = Math.Clamp(volume, 0f, 1f);
        lock (_lock)
        {
            if (_musicInst != null)
                _musicInst.Volume = Math.Clamp(Volume * Game1.MasterVolume, 0f, 1f);
        }
    }

    public void Play()
    {
        lock (_lock)
        {
            if (_disposed || !IsLoaded) return;

            if (_playbackStartTime >= Duration - 25f && Duration > 0f)
                _playbackStartTime = 0f;

            _pendingAudioSeek = false;
            if (_hasRealAudio)
            {
                CreateInstanceFromOffsetLocked(_playbackStartTime);
                if (_musicInst != null)
                {
                    _musicInst.Volume = Math.Clamp(Volume * Game1.MasterVolume, 0f, 1f);
                    _musicInst.Play();
                }
            }

            _playbackTimer.Restart();
            _isPlaying = true;
        }
        Console.WriteLine($"[EditorAudio] Playing from {_playbackStartTime / 1000f:F2}s");
    }

    public void Pause()
    {
        lock (_lock)
        {
            if (!_isPlaying) return;
            _playbackStartTime = CurrentTime;
            _playbackTimer.Reset();
            _isPlaying = false;
            _pendingAudioSeek = false;
            _musicInst?.Stop();
        }
        Console.WriteLine($"[EditorAudio] Paused at {_playbackStartTime / 1000f:F2}s");
    }

    public void Stop()
    {
        lock (_lock)
        {
            _musicInst?.Stop();
            _playbackTimer.Reset();
            _playbackStartTime = 0f;
            _isPlaying = false;
            _pendingAudioSeek = false;
        }
        Console.WriteLine("[EditorAudio] Stopped");
    }

    public void Seek(float timeMs)
    {
        lock (_lock)
        {
            if (_disposed) return;
            bool wasPlaying = _isPlaying;
            _musicInst?.Stop();
            _playbackTimer.Reset();

            _playbackStartTime = Math.Clamp(timeMs, 0f, Math.Max(0f, Duration));

            if (wasPlaying)
            {
                long now = System.Diagnostics.Stopwatch.GetTimestamp();
                double sinceLastMs = _lastSeekTicks == 0 ? 1000.0
                    : (now - _lastSeekTicks) * 1000.0 / System.Diagnostics.Stopwatch.Frequency;
                _lastSeekTicks = now;

                if (_hasRealAudio)
                {
                    // Debounce full-PCM SoundEffect rebuilds while dragging the timeline scrubber
                    if (sinceLastMs < 110.0)
                    {
                        _pendingAudioSeek = true;
                    }
                    else
                    {
                        _pendingAudioSeek = false;
                        CreateInstanceFromOffsetLocked(_playbackStartTime);
                        if (_musicInst != null)
                        {
                            _musicInst.Volume = Math.Clamp(Volume * Game1.MasterVolume, 0f, 1f);
                            _musicInst.Play();
                        }
                    }
                }
                _playbackTimer.Restart();
            }
        }
    }

    private void CreateInstanceFromOffsetLocked(float offsetMs)
    {
        _musicInst?.Stop();
        _musicInst?.Dispose();
        _musicInst = null;

        _seekMusic?.Dispose();
        _seekMusic = null;

        if (!_hasRealAudio || _musicPcm == null || _sampleRate <= 0 || _channels <= 0)
            return;

        float seekSeconds = Math.Max(0f, offsetMs / 1000f);
        SoundEffect? source = seekSeconds <= 0.005f
            ? _music
            : (_seekMusic = OggLoader.SeekPcm(_musicPcm, _sampleRate, _channels, seekSeconds));

        if (source == null) return;

        _musicInst = source.CreateInstance();
        _musicInst.Volume = Math.Clamp(Volume * Game1.MasterVolume, 0f, 1f);
        _musicInst.IsLooped = false;
    }

    private void StopAndDisposeInstancesLocked()
    {
        _musicInst?.Stop();
        _musicInst?.Dispose();
        _musicInst = null;

        _seekMusic?.Dispose();
        _seekMusic = null;

        _music?.Dispose();
        _music = null;
        _musicPcm = null;
    }

    public void Dispose()
    {
        lock (_lock)
        {
            _disposed = true;
            _isPlaying = false;
            _pendingAudioSeek = false;
            StopAndDisposeInstancesLocked();
        }
    }
}
