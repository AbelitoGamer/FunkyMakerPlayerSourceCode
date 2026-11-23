using System;
using System.IO;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Audio;
using NLayer;

namespace FunkyMakerPlayer.Managers;

/// <summary>
/// Background music for the online (multiplayer) menu.
/// - "Portraits of Kings" loops while the online menu is open (fades in on enter / back from a sub-screen).
/// - While the "Connecting to server" screen is up, Portraits fades out quickly (and pauses where it was)
///   and "Jazzroom" plays instead; leaving that screen fades Jazzroom out and Portraits back in.
/// Both tracks are MP3s in <c>Assets/music/online/</c>, streamed with NLayer (no full decode in memory).
/// Effective volume = master volume (<see cref="Game1.MasterVolume"/>) × <see cref="Volume"/> × fade.
/// </summary>
public sealed class OnlineMenuMusic : IDisposable
{
    /// <summary>"Menu Music Volume" setting (0..1), persisted in settings.json as <c>MenuMusicVolume</c>.</summary>
    public static float Volume = 0.7f;

    private const float FadeInSeconds = 1.5f;       // Portraits coming back
    private const float FadeOutSeconds = 0.35f;     // "fades out quickly"
    private const float JazzFadeInSeconds = 0.15f;  // Jazzroom starts almost immediately

    private readonly string? _assetsRoot;
    private Mp3LoopStream? _portraits;
    private Mp3LoopStream? _jazz;
    private bool _loadFailed;

    public OnlineMenuMusic(string? assetsRoot) => _assetsRoot = assetsRoot;

    /// <summary>
    /// Call every frame. <paramref name="inOnlineMenu"/>: the online menu (or a screen opened from it)
    /// is showing. <paramref name="connecting"/>: the "Connecting to server" screen is showing.
    /// </summary>
    public void Update(float dt, bool inOnlineMenu, bool connecting)
    {
        if (inOnlineMenu && !_loadFailed && _portraits == null)
            Load();

        bool wantPortraits = inOnlineMenu && !connecting;
        bool wantJazz = inOnlineMenu && connecting;

        // Jazzroom restarts from the top each time the connecting screen appears.
        if (wantJazz && _jazz != null && !_jazz.Active) _jazz.Restart();

        _portraits?.Step(dt, wantPortraits, FadeInSeconds, FadeOutSeconds);
        _jazz?.Step(dt, wantJazz, JazzFadeInSeconds, FadeOutSeconds);
    }

    private void Load()
    {
        _portraits = Mp3LoopStream.TryOpen(_assetsRoot, "portraits_of_kings");
        _jazz = Mp3LoopStream.TryOpen(_assetsRoot, "jazzroom");
        if (_portraits == null) _loadFailed = true; // don't retry every frame
    }

    public void Dispose()
    {
        _portraits?.Dispose();
        _jazz?.Dispose();
        _portraits = _jazz = null;
    }

    /// <summary>Looping MP3 stream on a DynamicSoundEffectInstance with a simple linear fade.</summary>
    private sealed class Mp3LoopStream : IDisposable
    {
        private const float BufferSeconds = 0.1f;
        private const int QueuedBuffers = 3;

        private readonly MpegFile _mp3;
        private readonly DynamicSoundEffectInstance _inst;
        private readonly float[] _floatBuf;
        private readonly byte[] _byteBuf;
        private float _fade;          // 0..1
        private bool _started;

        /// <summary>True while audible or fading (not paused / stopped).</summary>
        public bool Active => _inst.State == SoundState.Playing;

        private Mp3LoopStream(MpegFile mp3)
        {
            _mp3 = mp3;
            var channels = mp3.Channels == 1 ? AudioChannels.Mono : AudioChannels.Stereo;
            _inst = new DynamicSoundEffectInstance(mp3.SampleRate, channels);
            int samples = (int)(mp3.SampleRate * BufferSeconds) * mp3.Channels;
            _floatBuf = new float[samples];
            _byteBuf = new byte[samples * 2];
            _inst.BufferNeeded += (_, _) => SubmitBuffer();
        }

        public static Mp3LoopStream? TryOpen(string? assetsRoot, string name)
        {
            try
            {
                // Read the whole (small) MP3 into memory: APK asset streams are not seekable,
                // and looping needs to seek back to the start.
                byte[] data;
                string? path = assetsRoot != null ? Path.Combine(assetsRoot, "music", "online", name + ".mp3") : null;
                if (path != null && File.Exists(path))
                    data = File.ReadAllBytes(path);
                else
                {
                    // APK assets have no "Assets/" prefix (fork fix); the prefixed path is next to the exe.
                    Stream s;
                    try { s = TitleContainer.OpenStream($"music/online/{name}.mp3"); }
                    catch { s = TitleContainer.OpenStream($"Assets/music/online/{name}.mp3"); }
                    using var _ = s;
                    using var ms = new MemoryStream();
                    s.CopyTo(ms);
                    data = ms.ToArray();
                }
                return new Mp3LoopStream(new MpegFile(new MemoryStream(data)));
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[OnlineMenuMusic] Could not load '{name}.mp3': {ex.Message}");
                return null;
            }
        }

        /// <summary>Rewinds to the start and plays (from silence; <see cref="Step"/> fades it in).</summary>
        public void Restart()
        {
            try
            {
                _inst.Stop();
                _mp3.Time = TimeSpan.Zero;
                _fade = 0f;
                _started = false;
            }
            catch (Exception ex) { Console.WriteLine($"[OnlineMenuMusic] Restart failed: {ex.Message}"); }
        }

        public void Step(float dt, bool want, float fadeIn, float fadeOut)
        {
            try
            {
                if (want)
                {
                    if (!_started)
                    {
                        for (int i = 0; i < QueuedBuffers; i++) SubmitBuffer();
                        _inst.Volume = 0f;
                        _inst.Play();
                        _started = true;
                    }
                    else if (_inst.State == SoundState.Paused)
                        _inst.Resume();
                    _fade = Math.Min(1f, _fade + dt / fadeIn);
                }
                else if (_started && _inst.State == SoundState.Playing)
                {
                    _fade = Math.Max(0f, _fade - dt / fadeOut);
                    if (_fade <= 0f) _inst.Pause(); // keep the position for when it fades back in
                }

                _inst.Volume = Math.Clamp(Game1.MasterVolume * Volume * _fade, 0f, 1f);
            }
            catch (Exception ex) { Console.WriteLine($"[OnlineMenuMusic] Playback error: {ex.Message}"); }
        }

        private void SubmitBuffer()
        {
            int filled = 0;
            int guard = 0;
            while (filled < _floatBuf.Length && guard++ < 4)
            {
                int read = _mp3.ReadSamples(_floatBuf, filled, _floatBuf.Length - filled);
                filled += read;
                if (read == 0) _mp3.Time = TimeSpan.Zero; // end of track → loop
            }
            if (filled == 0) return;

            for (int i = 0; i < filled; i++)
            {
                short v = (short)(Math.Clamp(_floatBuf[i], -1f, 1f) * short.MaxValue);
                _byteBuf[i * 2] = (byte)v;
                _byteBuf[i * 2 + 1] = (byte)(v >> 8);
            }
            // Submit whole sample frames only
            int bytes = filled * 2;
            bytes -= bytes % (2 * _mp3.Channels);
            if (bytes > 0) _inst.SubmitBuffer(_byteBuf, 0, bytes);
        }

        public void Dispose()
        {
            try { _inst.Stop(); _inst.Dispose(); } catch { }
            _mp3.Dispose();
        }
    }
}
