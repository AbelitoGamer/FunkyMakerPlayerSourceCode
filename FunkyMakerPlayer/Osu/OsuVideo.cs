using System;
using System.Collections.Concurrent;
using System.IO;
using System.Threading;
using Microsoft.Xna.Framework.Graphics;

namespace FunkyMakerPlayer.Osu;

/// <summary>Size (RGBA frame) and frame rate a video is decoded to.</summary>
internal readonly record struct VideoFormat(int Width, int Height, double Fps)
{
    public int FrameBytes => Width * Height * 4;
}

/// <summary>
/// One decoding run of a video from a start time (fork). Created and read on the video's reader
/// thread; <see cref="Cancel"/> may be called from the game thread to unblock it.
/// </summary>
internal interface IOsuVideoStream : IDisposable
{
    /// <summary>
    /// Blocks until the next frame is decoded into <paramref name="rgba"/>
    /// (the run's <see cref="VideoFormat"/> size, RGBA, letterboxed) and returns its
    /// video time in seconds, or null at the end of the video / on an error.
    /// </summary>
    double? ReadFrame(byte[] rgba);

    /// <summary>Thread-safe: makes a blocked <see cref="ReadFrame"/> return soon.</summary>
    void Cancel();

    /// <summary>
    /// The game's current video time (thread-safe getter), for streams that can skip work on frames
    /// that are already late. Optional.
    /// </summary>
    void SetClock(Func<double> videoNow) { }
}

/// <summary>
/// Background video of an osu! map (fork). MonoGame can't decode arbitrary videos, so the frames come
/// from a platform decoder: a minimal bundled <b>ffmpeg</b> piping raw RGBA on desktop
/// (<see cref="FfmpegVideoStream"/>), <b>MediaCodec</b> on Android
/// (Platforms/Android/MediaCodecVideoStream.cs). A reader thread queues a few frames; the game
/// thread shows the frame for the song time.
/// Like osu! (DrawableStoryboardVideo) the video starts from its beginning at the Video event's
/// time, fades in over 500 ms and out at its end. Seeks / restarts reopen the decoder at the new time.
/// Standard quality decodes every video to 640×360 at 30 fps; Full quality (Settings → osu! songs →
/// Video Quality) keeps the video's own size and frame rate (capped: 4K / 1080p on Android, 60 fps).
/// </summary>
public sealed class OsuVideo : IDisposable
{
    /// <summary>Standard quality: every video is decoded to this size and frame rate (cheap).</summary>
    public const int StandardWidth = 640, StandardHeight = 360, StandardFps = 30;
    /// <summary>Full quality limits (the video's own size / frame rate, capped to these).</summary>
#if ANDROID
    private const int FullMaxWidth = 1920, FullMaxHeight = 1080; // frames are converted on the CPU
#else
    private const int FullMaxWidth = 3840, FullMaxHeight = 2160;
#endif
    private const double FullMaxFps = 60;
    /// <summary>Decoded frames kept in memory per run at most (6 at standard size, fewer for big frames).</summary>
    private const long QueueBudgetBytes = 64L * 1024 * 1024;

    /// <summary>Size and frame rate the frames are decoded to.</summary>
    internal VideoFormat Format { get; }
    /// <summary>Made with Settings → osu! songs → Video Quality = Full.</summary>
    public bool FullQuality { get; }
    private int QueueCapacity => (int)Math.Clamp(QueueBudgetBytes / Format.FrameBytes, 2, 6);

    private sealed class Session
    {
        public readonly BlockingCollection<(double Time, byte[] Pixels)> Frames;
        public Session(int capacity) { Frames = new(boundedCapacity: capacity); }
        public readonly CancellationTokenSource Cts = new();
        public volatile IOsuVideoStream? Stream;
        public volatile bool Ended;
        public double Start;                // video time this run decodes from
        public double Requested;            // video time when it was started (Start minus the lead)
        public bool Arrived;                // its first frame came out of the decoder
        public bool Shown;                  // one of its frames is on screen
    }

    private readonly string _file;
    private readonly double _startSec;
    private readonly Func<string, double, VideoFormat, IOsuVideoStream?> _open;
    private Session? _session;
    private bool _ended;                    // the last run reached the end of the video
    private double _shownTime = -1;
    private (double Time, byte[] Pixels)? _pending;
    private Texture2D? _texture;
    /// <summary>
    /// How far ahead of the song a mid-video (re)start decodes from, so its first frame is due when it
    /// arrives instead of already late (fork). Learned from how long the decoder took to start: ffmpeg
    /// spawns in ~0.1-0.3 s, MediaCodec has to decode from the previous keyframe (can be seconds).
    /// </summary>
    private double _lead = 0.3;
    /// <summary>Video time of the last Update, read by the reader thread (late frames skip conversion).</summary>
    private double _clockNow = double.NegativeInfinity;
    /// <summary>
    /// Frame buffers handed back after upload and reused by the reader thread: a new one per frame
    /// was 8 MB at 1080p, 30 times a second, and the GC churn alone made such videos stutter.
    /// </summary>
    private readonly ConcurrentQueue<byte[]> _freeBuffers = new();

    private void Recycle(byte[] buffer)
    {
        if (buffer.Length == Format.FrameBytes && _freeBuffers.Count < QueueCapacity + 2) _freeBuffers.Enqueue(buffer);
    }

    public Texture2D? Texture => _shownTime >= 0 ? _texture : null;
    /// <summary>0..1 (fade in at the start, out at the end).</summary>
    public float Alpha { get; private set; }

    private OsuVideo(string file, double startSec, VideoFormat format, bool fullQuality,
                     Func<string, double, VideoFormat, IOsuVideoStream?> open)
    {
        _file = file;
        _startSec = startSec;
        Format = format;
        FullQuality = fullQuality;
        _open = open;
    }

    /// <summary>
    /// A player for <paramref name="file"/>, or null (no decoder on this platform, missing file).
    /// <paramref name="fullQuality"/>: decode at the video's own size and frame rate (probed from the
    /// file; falls back to standard quality if that fails) instead of 640×360 at 30 fps.
    /// </summary>
    public static OsuVideo? TryCreate(string? file, double startSec, bool fullQuality = false)
    {
        if (file == null || !File.Exists(file)) return null;
        var standard = new VideoFormat(StandardWidth, StandardHeight, StandardFps);
#if ANDROID
        var format = fullQuality ? FullFormat(Platforms.Android.MediaCodecVideoStream.Probe(file)) ?? standard : standard;
        Console.WriteLine($"[OsuVideo] {Path.GetFileName(file)}: decoding at {format.Width}x{format.Height} {format.Fps:0.##} fps");
        return new OsuVideo(file, startSec, format, fullQuality, (f, t, fmt) => new Platforms.Android.MediaCodecVideoStream(f, t, fmt));
#else
        string? ffmpeg = FfmpegVideoStream.FindFfmpeg();
        if (ffmpeg == null)
        {
            Console.WriteLine("[OsuVideo] ffmpeg not found (next to the game, mods/tools or PATH): video skipped");
            return null;
        }
        var format = fullQuality ? FullFormat(FfmpegVideoStream.Probe(ffmpeg, file)) ?? standard : standard;
        Console.WriteLine($"[OsuVideo] {Path.GetFileName(file)}: decoding at {format.Width}x{format.Height} {format.Fps:0.##} fps");
        return new OsuVideo(file, startSec, format, fullQuality, (f, t, fmt) => new FfmpegVideoStream(ffmpeg, f, t, fmt));
#endif
    }

    /// <summary>The video's own size (fitted into the full-quality cap, even) and frame rate (capped).</summary>
    private static VideoFormat? FullFormat((int Width, int Height, double Fps)? probe)
    {
        if (probe is not { } p || p.Width < 2 || p.Height < 2) return null;
        double scale = Math.Min(1.0, Math.Min(FullMaxWidth / (double)p.Width, FullMaxHeight / (double)p.Height));
        int w = Math.Max(2, (int)(p.Width * scale) & ~1);
        int h = Math.Max(2, (int)(p.Height * scale) & ~1);
        double fps = p.Fps > 0 && double.IsFinite(p.Fps) ? Math.Min(p.Fps, FullMaxFps) : StandardFps;
        return new VideoFormat(w, h, fps);
    }

    /// <summary>
    /// Per frame: keeps the decoder at the right place and uploads the frame for <paramref name="songTime"/>.
    /// <paramref name="clockRunning"/> is false while the song is paused (no lead, no latency learning).
    /// </summary>
    public void Update(double songTime, GraphicsDevice gd, bool clockRunning = true)
    {
        double videoTime = songTime - _startSec;
        Volatile.Write(ref _clockNow, clockRunning ? videoTime : double.NegativeInfinity);
        if (videoTime < -0.5)
        {
            // Before the video (e.g. a restart's countdown): drop the old stream so it starts fresh.
            Alpha = 0f;
            if (_session != null) Stop();
            _ended = false;
            _shownTime = -1;
            return;
        }

        if (_session is { Ended: true } s && s.Frames.Count == 0) _ended = true;

        // (Re)start the decoder when not running or when the song jumped away from the stream. At the
        // video's beginning it is started half a second early (above) so the first frame is ready on
        // time; mid-video it decodes from a bit ahead of the song (_lead) for the same reason.
        // Only frames of the current run count: _shownTime keeps the previous run's frame until the
        // new one arrives (it stays on screen meanwhile), and comparing against it made every frame
        // restart the decoder after a forward timeskip, so the video froze for good.
        bool restart;
        var cur = _session;
        if (cur == null) restart = !_ended;
        else if (cur.Shown)
            restart = (!_ended && videoTime > _shownTime + 1.0)  // fell behind / skipped forward (not after the video ended)
                   || videoTime < _shownTime - 0.25;              // skipped back
        else
            restart = Math.Max(0, videoTime) < cur.Requested - 0.05; // skipped back before its first frame (not the pre-roll)
        if (restart)
        {
            double startAt = videoTime <= 0 ? 0 : videoTime + (clockRunning ? _lead : 0);
            Start(startAt, requested: Math.Max(0, videoTime));
        }
        if (videoTime < 0)
        {
            Alpha = 0f;
            return;
        }

        // Take every queued frame that is due; upload only the newest of them.
        if (_session != null)
        {
            (double Time, byte[] Pixels)? latest = null;
            while (true)
            {
                (double Time, byte[] Pixels) frame;
                if (_pending is { } p) { frame = p; _pending = null; }
                else if (!_session.Frames.TryTake(out frame)) break;
                if (!_session.Arrived)
                {
                    _session.Arrived = true;
                    // Learn the start-up time for the next mid-video start (song seconds the decoder
                    // needed to deliver its first frame).
                    if (clockRunning && _session.Requested > 0)
                        _lead = Math.Clamp((videoTime - _session.Requested) * 1.25 + 0.1, 0.15, 4.0);
                }
                if (frame.Time > videoTime + 0.5 / Format.Fps)
                {
                    _pending = frame; // not due yet
                    break;
                }
                if (latest is { } skipped) Recycle(skipped.Pixels);
                latest = frame;
            }
            if (latest is { } show)
            {
                _texture ??= new Texture2D(gd, Format.Width, Format.Height, false, SurfaceFormat.Color);
                _texture.SetData(show.Pixels);
                Recycle(show.Pixels);
                _shownTime = show.Time;
                _session.Shown = true;
            }
        }

        // Fade in over 500 ms; when the stream ended, the last frame fades out over 500 ms.
        float fadeIn = (float)Math.Clamp(videoTime / 0.5, 0, 1);
        bool finished = _ended && _pending == null;
        float fadeOut = finished && _shownTime >= 0
            ? (float)Math.Clamp(1 - (videoTime - _shownTime - 1.0 / Format.Fps) / 0.5, 0, 1)
            : 1f;
        Alpha = Math.Min(fadeIn, fadeOut);
    }

    private void Start(double videoTime, double requested)
    {
        Stop();
        _ended = false;
        var session = new Session(QueueCapacity) { Start = videoTime, Requested = requested };
        _session = session;
        var token = session.Cts.Token;
        var thread = new Thread(() =>
        {
            IOsuVideoStream? stream = null;
            try
            {
                stream = _open(_file, videoTime, Format);
                session.Stream = stream;
                if (stream == null || token.IsCancellationRequested) return;
                stream.SetClock(() => Volatile.Read(ref _clockNow));
                while (!token.IsCancellationRequested)
                {
                    if (!_freeBuffers.TryDequeue(out var buf)) buf = new byte[Format.FrameBytes];
                    double? time = stream.ReadFrame(buf);
                    if (time == null) { Recycle(buf); break; }
                    session.Frames.Add((time.Value, buf), token);
                }
            }
            catch (OperationCanceledException) { }
            catch (Exception ex) { Console.WriteLine($"[OsuVideo] Decoding stopped: {ex.Message}"); }
            finally
            {
                if (!token.IsCancellationRequested) session.Ended = true;
                try { stream?.Dispose(); } catch { }
            }
        }) { IsBackground = true, Name = "osu video" };
        thread.Start();
        Console.WriteLine($"[OsuVideo] Playing {Path.GetFileName(_file)} from {videoTime:0.00}s");
    }

    private void Stop()
    {
        var s = _session;
        _session = null;
        _pending = null;
        if (s == null) return;
        try { s.Cts.Cancel(); } catch { }
        try { s.Stream?.Cancel(); } catch { }
        // The reader thread disposes the stream; the token source is left to the GC (the thread
        // may still be checking it).
    }

    public void Dispose()
    {
        Stop();
        _texture?.Dispose();
        _texture = null;
    }
}
