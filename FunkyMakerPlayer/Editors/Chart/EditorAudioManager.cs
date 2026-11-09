using Microsoft.Xna.Framework.Audio;
using System;

namespace FunkyMakerPlayer.Editors.Chart;

/// <summary>
/// Simplified audio manager for Chart Editor
/// </summary>
public class EditorAudioManager : IDisposable
{
    private SoundEffect? _music;
    private SoundEffectInstance? _musicInst;
    private byte[]? _musicPcm;
    private int _sampleRate;
    private int _channels;
    private float _playbackStartTime = 0f;
    private System.Diagnostics.Stopwatch _playbackTimer = new();
    
    public bool IsLoaded => _music != null;
    public bool IsPlaying => _musicInst?.State == SoundState.Playing;
    public bool IsPaused => _musicInst?.State == SoundState.Paused;
    public float Volume { get; set; } = 0.5f;
    
    /// <summary>
    /// Current playback time in milliseconds
    /// </summary>
    public float CurrentTime
    {
        get
        {
            if (IsPlaying && _playbackTimer.IsRunning)
            {
                return _playbackStartTime + (float)_playbackTimer.Elapsed.TotalMilliseconds;
            }
            return _playbackStartTime;
        }
    }
    
    /// <summary>
    /// Total duration in milliseconds
    /// </summary>
    public float Duration
    {
        get
        {
            if (_music == null || _sampleRate == 0) return 0f;
            float samples = _musicPcm?.Length ?? 0;
            float bytesPerSample = 2; // 16-bit audio
            float totalSamples = samples / (bytesPerSample * _channels);
            return (totalSamples / _sampleRate) * 1000f;
        }
    }
    
    public void LoadFromLevel(FnmmLevel level)
    {
        try
        {
            if (level.MusicData == null)
            {
                Console.WriteLine("[EditorAudio] No music data in level");
                return;
            }
            
            Console.WriteLine("[EditorAudio] Loading music...");
            
            // Dispose old audio
            _musicInst?.Stop();
            _musicInst?.Dispose();
            _music?.Dispose();
            
            // Load OGG with PCM for seeking
            _music = OggLoader.LoadWithPcm(level.MusicData,
                out byte[] pcm, out int sampleRate, out int channels);
            
            _musicPcm = pcm;
            _sampleRate = sampleRate;
            _channels = channels;
            
            _musicInst = _music.CreateInstance();
            _musicInst.Volume = Volume;
            _musicInst.IsLooped = true; // Loop in editor
            
            Console.WriteLine($"[EditorAudio] ✅ Music loaded ({_musicPcm.Length} bytes, {sampleRate}Hz, {channels}ch)");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[EditorAudio] ❌ Failed to load music: {ex.Message}");
        }
    }
    
    public void Play()
    {
        if (_musicInst == null)
        {
            Console.WriteLine("[EditorAudio] Cannot play - no music loaded");
            return;
        }
        
        _musicInst.Volume = Volume;
        _musicInst.Play();
        _playbackTimer.Start();
        Console.WriteLine("[EditorAudio] Playing");
    }
    
    public void Pause()
    {
        if (_musicInst == null) return;
        _musicInst.Pause();
        _playbackTimer.Stop();
        _playbackStartTime = CurrentTime;
        Console.WriteLine("[EditorAudio] Paused");
    }
    
    public void Stop()
    {
        if (_musicInst == null) return;
        _musicInst.Stop();
        _playbackTimer.Stop();
        _playbackTimer.Reset();
        _playbackStartTime = 0f;
        Console.WriteLine("[EditorAudio] Stopped");
    }
    
    public void Seek(float timeMs)
    {
        bool wasPlaying = IsPlaying;
        
        if (_musicInst != null)
        {
            _musicInst.Stop();
        }
        
        _playbackStartTime = Math.Clamp(timeMs, 0f, Duration);
        _playbackTimer.Reset();
        
        if (wasPlaying && _musicInst != null)
        {
            _musicInst.Play();
            _playbackTimer.Start();
        }
    }
    
    public void Dispose()
    {
        _musicInst?.Stop();
        _musicInst?.Dispose();
        _music?.Dispose();
        _musicPcm = null;
    }
}
