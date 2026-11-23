using System;
using System.Collections.Concurrent;
using System.IO;
using System.Threading;
using Microsoft.Xna.Framework.Graphics;

namespace FunkyMakerPlayer.Osu;

/// <summary>
/// One decoding run of a video from a start time (fork). Created and read on the video's reader
/// thread; <see cref="Cancel"/> may be called from the game thread to unblock it.
/// </summary>
internal interface IOsuVideoStream : IDisposable
{
    /// <summary>
    /// Blocks until the next frame is decoded into <paramref name="rgba"/>
    /// (<see cref="OsuVideo.Width"/>×<see cref="OsuVideo.Height"/> RGBA, letterboxed) and returns its
    /// video time in seconds, or null at the end of the video / on an error.
    /// </summary>
    double? ReadFrame(byte[] rgba);

    /// <summary>Thread-safe: makes a blocked <see cref="ReadFrame"/> return soon.</summary>
    void Cancel();
}

/// <summary>
/// Background video of an osu! map (fork). MonoGame can't decode arbitrary videos, so the frames come
/// from a platform decoder: a minimal bundled <b>ffmpeg</b> piping raw RGBA on desktop
/// (<see cref="FfmpegVideoStream"/>), <b>MediaCodec</b> on Android
/// (Platforms/Android/MediaCodecVideoStream.cs). A reader thread queues a few frames; the game
/// thread shows the frame for the song time.
/// Like osu! (DrawableStoryboardVideo) the video starts from its beginning at the Video event's
/// time, fades in over 500 ms and out at its end. Seeks / restarts reopen the decoder at the new time.
/// </summary>
public sealed class OsuVideo : IDisposable
{
    public const int Width = 640, Height = 360, Fps = 30;
    internal const int FrameBytes = Width * Height * 4;

    private sealed class Session
    {
        public readonly BlockingCollection<(double Time, byte[] Pixels)> Frames = new(boundedCapacity: 6);
        public readonly CancellationTokenSource Cts = new();
        public volatile IOsuVideoStream? Stream;
        public volatile bool Ended;
        public double Start;                // video time this run started at
    }

    private readonly string _file;
    private readonly double _startSec;
    private readonly Func<string, double, IOsuVideoStream?> _open;
    private Session? _session;
    private bool _ended;                    // the last run reached the end of the video
    private double _shownTime = -1;
    private (double Time, byte[] Pixels)? _pending;
    private Texture2D? _texture;

    public Texture2D? Texture => _shownTime >= 0 ? _texture : null;
    /// <summary>0..1 (fade in at the start, out at the end).</summary>
    public float Alpha { get; private set; }

    private OsuVideo(string file, double startSec, Func<string, double, IOsuVideoStream?> open)
    {
        _file = file;
        _startSec = startSec;
        _open = open;
    }

    /// <summary>A player for <paramref name="file"/>, or null (no decoder on this platform, missing file).</summary>
    public static OsuVideo? TryCreate(string? file, double startSec)
    {
        if (file == null || !File.Exists(file)) return null;
#if ANDROID
        return new OsuVideo(file, startSec, (f, t) => new Platforms.Android.MediaCodecVideoStream(f, t));
#else
        string? ffmpeg = FfmpegVideoStream.FindFfmpeg();
        if (ffmpeg == null)
        {
            Console.WriteLine("[OsuVideo] ffmpeg not found (next to the game, mods/tools or PATH): video skipped");
            return null;
        }
        return new OsuVideo(file, startSec, (f, t) => new FfmpegVideoStream(ffmpeg, f, t));
#endif
    }

    /// <summary>Per frame: keeps the decoder at the right place and uploads the frame for <paramref name="songTime"/>.</summary>
    public void Update(double songTime, GraphicsDevice gd)
    {
        double videoTime = songTime - _startSec;
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

        // (Re)start the decoder when not running or when the song jumped away from the stream. It
        // is started half a second early so the first frame is ready on time.
        double startAt = Math.Max(0, videoTime);
        bool behind = !_ended && _shownTime >= 0 && videoTime > _shownTime + 1.0; // not after the video ended
        bool before = _session != null && startAt < _session.Start - 0.05;
        if ((_session == null && !_ended) || behind || before)
            Start(startAt);
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
                if (frame.Time > videoTime + 0.5 / Fps)
                {
                    _pending = frame; // not due yet
                    break;
                }
                latest = frame;
            }
            if (latest is { } show)
            {
                _texture ??= new Texture2D(gd, Width, Height, false, SurfaceFormat.Color);
                _texture.SetData(show.Pixels);
                _shownTime = show.Time;
            }
        }

        // Fade in over 500 ms; when the stream ended, the last frame fades out over 500 ms.
        float fadeIn = (float)Math.Clamp(videoTime / 0.5, 0, 1);
        bool finished = _ended && _pending == null;
        float fadeOut = finished && _shownTime >= 0
            ? (float)Math.Clamp(1 - (videoTime - _shownTime - 1.0 / Fps) / 0.5, 0, 1)
            : 1f;
        Alpha = Math.Min(fadeIn, fadeOut);
    }

    private void Start(double videoTime)
    {
        Stop();
        _ended = false;
        var session = new Session { Start = videoTime };
        _session = session;
        var token = session.Cts.Token;
        var thread = new Thread(() =>
        {
            IOsuVideoStream? stream = null;
            try
            {
                stream = _open(_file, videoTime);
                session.Stream = stream;
                if (stream == null || token.IsCancellationRequested) return;
                while (!token.IsCancellationRequested)
                {
                    var buf = new byte[FrameBytes];
                    double? time = stream.ReadFrame(buf);
                    if (time == null) break;
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
