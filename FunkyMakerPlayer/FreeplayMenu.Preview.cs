using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Xna.Framework.Audio;
using NLayer;
using NVorbis;

namespace FunkyMakerPlayer;

/// <summary>
/// Fork: audio preview of the selected Freeplay level. After the selection rests for
/// <see cref="PreviewDelay"/>, the level's music.ogg is read (only that file) and streamed with NVorbis
/// from just before the first note, looping back there at the end. It fades in / out on every change
/// and stops when Freeplay isn't on screen. Volume = master × song volume.
/// </summary>
public partial class FreeplayMenu
{
    private const float PreviewDelay = 0.35f;
    private const float PreviewFadeIn = 0.6f;
    private const float PreviewFadeOut = 0.3f;
    private const float PreviewLeadIn = 0.15f;   // start a little before the first note

    private PreviewStream? _preview;         // the selected level's (or loading → null)
    private PreviewStream? _previewOld;      // fading out
    private string? _previewWanted;
    private float _previewWait;
    private string? _previewLoading;
    private int _previewGen;
    private (int Gen, string Path, byte[] Ogg, float Start)? _previewLoaded; // worker → main thread
    private readonly object _previewLock = new();

    /// <summary>BPM and song position of the playing preview (beat-synced visuals), or null.</summary>
    private (float Bpm, double Seconds, float Fade)? PreviewClock
    {
        get
        {
            var p = _preview;
            if (p == null || !p.Started) return null;
            var info = GetInfo(p.Path);
            return info != null && info.Bpm > 0 ? (info.Bpm, p.Position, p.Fade) : null;
        }
    }

    /// <summary>Path of the level whose preview is audible, or null.</summary>
    private string? PreviewPath => _preview is { Started: true } p ? p.Path : null;

    /// <summary>
    /// Called by Game1 every frame, also while the menu isn't updated (so leaving Freeplay stops the
    /// preview). <paramref name="allowed"/>: Freeplay is the active screen.
    /// </summary>
    public void UpdatePreview(float dt, bool allowed)
    {
        if (!allowed) StopSearchTyping(); // never keep capturing text off-screen
        string? want = allowed && _panelOpen && !_confirming && _selected >= 0 && _selected < _paths.Count
            ? _paths[_selected] : null;
        if (!OnDevice(want)) want = null; // online songs not on this device have no audio yet

        if (want != _previewWanted)
        {
            _previewWanted = want;
            _previewWait = 0f;
        }
        else
        {
            _previewWait += dt;
        }

        // The playing preview belongs to another level → fade it out.
        if (_preview != null && _preview.Path != want)
        {
            _previewOld?.Dispose();
            _previewOld = _preview;
            _preview = null;
        }

        // Start loading once the selection has rested a moment.
        if (want != null && _preview == null && _previewLoading != want && _previewWait >= PreviewDelay)
            BeginPreviewLoad(want);

        // Pick up a finished load (stream objects are created on the game thread).
        (int Gen, string Path, byte[] Ogg, float Start)? loaded;
        lock (_previewLock) { loaded = _previewLoaded; _previewLoaded = null; }
        if (loaded is { } l && l.Gen == _previewGen)
        {
            _previewLoading = null;
            if (l.Path == want && _preview == null)
                _preview = PreviewStream.TryCreate(l.Path, l.Ogg, l.Start);
        }

        float volume = Game1.SongPlaybackVolume;
        _preview?.Step(dt, true, volume);
        if (_previewOld != null && !_previewOld.Step(dt, false, volume))
        {
            _previewOld.Dispose();
            _previewOld = null;
        }
    }

    private void BeginPreviewLoad(string path)
    {
        int gen = ++_previewGen;
        _previewLoading = path;
        Task.Run(() =>
        {
            byte[]? ogg;
            float start;
            if (Osu.OsuLibrary.IsOsuChart(path))
            {
                // osu! songs (fork): the map's own audio (MP3 or OGG) from its PreviewTime;
                // without one, 40 % into the song like osu!.
                ogg = Osu.OsuLevelLoader.ReadAudio(path, out float previewSec);
                start = previewSec >= 0 ? previewSec : -1f;
            }
            else
            {
                ogg = FnmmLoader.WithLevelFiles(path, read => read("music.ogg"));
                // First note: from the analysis if it's done, else read the chart now.
                float first = GetInfo(path)?.FirstNoteSec ?? FirstNoteOf(path);
                start = Math.Max(0f, first - PreviewLeadIn);
            }
            if (ogg == null || gen != Volatile.Read(ref _previewGen))
            {
                if (ogg == null) Console.WriteLine($"[FreeplayMenu] No preview audio for {Path.GetFileName(path)}");
                return;
            }
            lock (_previewLock)
                _previewLoaded = (gen, path, ogg, start);
        });
    }

    private static float FirstNoteOf(string path)
    {
        var data = FnmmLoader.TryLoadChartData(path);
        if (data == null) return 0f;
        double first = double.MaxValue;
        foreach (var sl in data.Strumlines)
            foreach (var lane in sl)
                foreach (var n in lane)
                    first = Math.Min(first, data.ToSeconds(n.Start));
        return first == double.MaxValue ? 0f : (float)first;
    }

    /// <summary>Stops the preview at once (Freeplay closed for good, e.g. the game exits).</summary>
    public void StopPreview()
    {
        _previewGen++;
        _previewLoading = null;
        _preview?.Dispose();
        _previewOld?.Dispose();
        _preview = _previewOld = null;
    }

    /// <summary>Decoded samples of an OGG (NVorbis) or MP3 (NLayer) file, with seeking.</summary>
    private interface ISampleSource : IDisposable
    {
        int Channels { get; }
        int SampleRate { get; }
        double TotalSeconds { get; }
        double Position { get; set; }
        int Read(float[] buffer, int offset, int count);
    }

    private sealed class VorbisSource : ISampleSource
    {
        private readonly VorbisReader _r;
        public VorbisSource(byte[] data) => _r = new VorbisReader(new MemoryStream(data), closeOnDispose: true);
        public int Channels => _r.Channels;
        public int SampleRate => _r.SampleRate;
        public double TotalSeconds => _r.TotalTime.TotalSeconds;
        public double Position { get => _r.TimePosition.TotalSeconds; set => _r.TimePosition = TimeSpan.FromSeconds(value); }
        public int Read(float[] buffer, int offset, int count) => _r.ReadSamples(buffer, offset, count);
        public void Dispose() => _r.Dispose();
    }

    private sealed class Mp3Source : ISampleSource
    {
        private readonly MpegFile _m;
        public Mp3Source(byte[] data) => _m = new MpegFile(new MemoryStream(data));
        public int Channels => _m.Channels;
        public int SampleRate => _m.SampleRate;
        public double TotalSeconds => _m.Duration.TotalSeconds;
        public double Position { get => _m.Time.TotalSeconds; set => _m.Time = TimeSpan.FromSeconds(value); }
        public int Read(float[] buffer, int offset, int count) => _m.ReadSamples(buffer, offset, count);
        public void Dispose() => _m.Dispose();
    }

    /// <summary>Audio stream (OGG or MP3) on a DynamicSoundEffectInstance with a linear fade.</summary>
    private sealed class PreviewStream : IDisposable
    {
        private const float BufferSeconds = 0.1f;
        private const int QueuedBuffers = 3;

        public readonly string Path;
        private readonly ISampleSource _reader;
        private readonly DynamicSoundEffectInstance _inst;
        private readonly float[] _floatBuf;
        private readonly byte[] _byteBuf;
        private readonly int _channels, _sampleRate;
        private readonly double _loopStart, _length;
        private double _submittedEnd;   // song time at the end of the last submitted buffer
        private bool _disposed;

        public float Fade { get; private set; }
        public bool Started { get; private set; }

        /// <summary>Approximate song time being heard now.</summary>
        public double Position
        {
            get
            {
                double pos = _submittedEnd - _inst.PendingBufferCount * BufferSeconds;
                if (pos < _loopStart && _length > _loopStart) pos += _length - _loopStart; // just looped
                return Math.Max(0, pos);
            }
        }

        private PreviewStream(string path, ISampleSource reader, double start)
        {
            Path = path;
            _reader = reader;
            _channels = reader.Channels;
            _sampleRate = reader.SampleRate;
            _length = reader.TotalSeconds;
            if (start < 0) start = _length * 0.4; // no preview point (osu!: 40 % in)
            _loopStart = start < _length - 1 ? start : 0;
            _inst = new DynamicSoundEffectInstance(_sampleRate, _channels == 1 ? AudioChannels.Mono : AudioChannels.Stereo);
            int samples = (int)(_sampleRate * BufferSeconds) * _channels;
            _floatBuf = new float[samples];
            _byteBuf = new byte[samples * 2];
            Seek(_loopStart);
            _inst.BufferNeeded += (_, _) => SubmitBuffer();
        }

        public static PreviewStream? TryCreate(string path, byte[] ogg, float start)
        {
            try
            {
                ISampleSource reader = AudioDecoder.IsOgg(ogg) ? new VorbisSource(ogg) : new Mp3Source(ogg);
                if (reader.Channels is < 1 or > 2 || reader.SampleRate < 8000 || reader.SampleRate > 48000)
                {
                    Console.WriteLine($"[FreeplayMenu] Preview format not supported ({reader.Channels} ch, {reader.SampleRate} Hz)");
                    reader.Dispose();
                    return null;
                }
                return new PreviewStream(path, reader, start);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[FreeplayMenu] Preview failed for {System.IO.Path.GetFileName(path)}: {ex.Message}");
                return null;
            }
        }

        private void Seek(double seconds)
        {
            try { _reader.Position = seconds; }
            catch { try { _reader.Position = 0; } catch { } }
            _submittedEnd = _reader.Position;
        }

        /// <summary>Advances the fade. Returns false once a fading-out stream is silent.</summary>
        public bool Step(float dt, bool want, float volume)
        {
            if (_disposed) return false;
            try
            {
                if (want)
                {
                    if (!Started)
                    {
                        for (int i = 0; i < QueuedBuffers; i++) SubmitBuffer();
                        _inst.Volume = 0f;
                        _inst.Play();
                        Started = true;
                    }
                    Fade = Math.Min(1f, Fade + dt / PreviewFadeIn);
                }
                else
                {
                    Fade = Math.Max(0f, Fade - dt / PreviewFadeOut);
                    if (Fade <= 0f) return false;
                }
                _inst.Volume = Math.Clamp(volume * Fade, 0f, 1f);
                return true;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[FreeplayMenu] Preview playback error: {ex.Message}");
                return false;
            }
        }

        private void SubmitBuffer()
        {
            if (_disposed) return;
            try
            {
                int filled = 0, guard = 0;
                while (filled < _floatBuf.Length && guard++ < 4)
                {
                    int read = _reader.Read(_floatBuf, filled, _floatBuf.Length - filled);
                    filled += read;
                    if (read == 0) Seek(_loopStart); // end of song → back to the first note
                }
                if (filled == 0) return;

                for (int i = 0; i < filled; i++)
                {
                    short v = (short)(Math.Clamp(_floatBuf[i], -1f, 1f) * short.MaxValue);
                    _byteBuf[i * 2] = (byte)v;
                    _byteBuf[i * 2 + 1] = (byte)(v >> 8);
                }
                int bytes = filled * 2;
                bytes -= bytes % (2 * _channels);
                if (bytes > 0) _inst.SubmitBuffer(_byteBuf, 0, bytes);
                _submittedEnd = _reader.Position;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[FreeplayMenu] Preview decode error: {ex.Message}");
            }
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            try { _inst.Stop(); _inst.Dispose(); } catch { }
            try { _reader.Dispose(); } catch { }
        }
    }
}
