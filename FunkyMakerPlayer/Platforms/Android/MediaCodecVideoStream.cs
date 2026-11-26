#if ANDROID
using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using Android.Media;
using Android.Runtime;
using FunkyMakerPlayer.Osu;

namespace FunkyMakerPlayer.Platforms.Android;

/// <summary>
/// Android video decoder for <see cref="OsuVideo"/> (fork): MediaExtractor + MediaCodec (the
/// device's hardware decoders, nothing bundled) in synchronous mode on the video's reader thread.
/// Each decoded YUV_420_888 <see cref="Image"/> is letterboxed and converted to RGBA
/// (nearest-neighbour, BT.601) at the run's <see cref="VideoFormat"/> (640×360, or the video's own size
/// at full quality) on the CPU; only the source rows that are sampled are copied out of
/// the codec's buffers. Frames closer than one output frame to the previous one are dropped, like ffmpeg's -r.
/// Containers are whatever MediaExtractor reads (mp4/mov, mkv/webm, 3gp, ts…), plus AVI through
/// <see cref="AviDemuxer"/> (H.264, MPEG-4/Xvid); not flv/wmv.
/// </summary>
internal sealed class MediaCodecVideoStream : IOsuVideoStream
{
    private const long TimeoutUs = 10_000;

    private readonly MediaExtractor? _extractor;
    private readonly MediaCodec? _codec;
    private readonly MediaCodec.BufferInfo _info = new();
    private readonly long _startUs;
    private volatile bool _cancelled;
    private bool _inputDone, _outputDone;
    private double _lastTime = double.NegativeInfinity;

    // Letterbox mapping, rebuilt when the decoded size changes.
    private int _srcW = -1, _srcH = -1, _srcX, _srcY;
    private int _dstX, _dstY, _dstW, _dstH;
    private int[] _mapX = Array.Empty<int>(), _mapY = Array.Empty<int>();
    // Per output column: byte offset of its sample in a source row (Y, U, V); per band: row buffers.
    private int[] _colY = Array.Empty<int>(), _colU = Array.Empty<int>(), _colV = Array.Empty<int>();
    private int _psY = -1, _psU = -1, _psV = -1;
    private byte[][][] _bandRows = Array.Empty<byte[][]>();

    /// <summary>The game's current video time (set by <see cref="OsuVideo"/>), or null.</summary>
    private Func<double>? _clock;
    public void SetClock(Func<double> videoNow) => _clock = videoNow;

    private readonly VideoFormat _format;

    // AVI (MediaExtractor can't read it): our own demuxer feeds the codec. Its frames come in decode
    // order with slot times, so with B-frames an output frame's time is the smallest time still
    // pending, not the one MediaCodec attaches (that of the input that completed it).
    private readonly AviDemuxer? _avi;
    private byte[] _aviBuf = new byte[1 << 16];
    private readonly PriorityQueue<long, long>? _pending;

    /// <summary>
    /// The video track's size and frame rate (0 when the container doesn't say), or null when
    /// MediaExtractor can't read it.
    /// </summary>
    public static (int Width, int Height, double Fps)? Probe(string file)
    {
        if (AviDemuxer.IsAvi(file))
        {
            using var avi = AviDemuxer.Open(file);
            return avi == null ? null : (avi.Width, avi.Height, avi.Fps);
        }
        MediaExtractor? ex = null;
        try
        {
            ex = new MediaExtractor();
            ex.SetDataSource(file);
            for (int i = 0; i < ex.TrackCount; i++)
            {
                var f = ex.GetTrackFormat(i);
                string? mime = f.GetString(MediaFormat.KeyMime);
                if (mime == null || !mime.StartsWith("video/", StringComparison.Ordinal)) continue;
                int w = f.ContainsKey(MediaFormat.KeyWidth) ? f.GetInteger(MediaFormat.KeyWidth) : 0;
                int h = f.ContainsKey(MediaFormat.KeyHeight) ? f.GetInteger(MediaFormat.KeyHeight) : 0;
                double fps = 0;
                if (f.ContainsKey(MediaFormat.KeyFrameRate))
                {
                    try { fps = f.GetInteger(MediaFormat.KeyFrameRate); }
                    catch { try { fps = f.GetFloat(MediaFormat.KeyFrameRate); } catch { } }
                }
                return (w, h, fps);
            }
        }
        catch (Exception e) { Console.WriteLine($"[OsuVideo] Probe failed: {e.Message}"); }
        finally { try { ex?.Release(); } catch { } ex?.Dispose(); }
        return null;
    }

    public MediaCodecVideoStream(string file, double startSec, VideoFormat outFormat)
    {
        _format = outFormat;
        _startUs = (long)(Math.Max(0, startSec) * 1_000_000);
        try
        {
            if (AviDemuxer.IsAvi(file))
            {
                _avi = AviDemuxer.Open(file);
                if (_avi == null) return;
                var avi = MediaFormat.CreateVideoFormat(_avi.Mime!, _avi.Width, _avi.Height);
                for (int i = 0; i < _avi.CodecConfig.Count; i++)
                    avi.SetByteBuffer($"csd-{i}", Java.Nio.ByteBuffer.Wrap(_avi.CodecConfig[i]));
                avi.SetInteger(MediaFormat.KeyMaxInputSize, Math.Max(_avi.MaxFrameSize * 2 + 1024, 1 << 16));
                avi.SetInteger(MediaFormat.KeyColorFormat, (int)MediaCodecCapabilities.Formatyuv420flexible);
                _pending = new PriorityQueue<long, long>();
                _codec = MediaCodec.CreateDecoderByType(_avi.Mime!);
                _codec.Configure(avi, null, null, MediaCodecConfigFlags.None);
                _codec.Start();
                _avi.SeekTo(startSec);
                Console.WriteLine($"[OsuVideo] AVI {_avi.FourCC.Trim()} {_avi.Width}x{_avi.Height} {_avi.Fps:0.##} fps, {_avi.FrameCount} frames, {_avi.CodecConfig.Count} config");
                return;
            }
            _extractor = new MediaExtractor();
            _extractor.SetDataSource(file);
            MediaFormat? format = null;
            string? mime = null;
            for (int i = 0; i < _extractor.TrackCount; i++)
            {
                var f = _extractor.GetTrackFormat(i);
                string? m = f.GetString(MediaFormat.KeyMime);
                if (m != null && m.StartsWith("video/", StringComparison.Ordinal))
                {
                    _extractor.SelectTrack(i);
                    format = f;
                    mime = m;
                    break;
                }
            }
            if (format == null || mime == null)
            {
                Console.WriteLine("[OsuVideo] No video track MediaExtractor can read");
                Release();
                return;
            }
            format.SetInteger(MediaFormat.KeyColorFormat, (int)MediaCodecCapabilities.Formatyuv420flexible);
            _codec = MediaCodec.CreateDecoderByType(mime);
            _codec.Configure(format, null, null, MediaCodecConfigFlags.None);
            _codec.Start();
            _extractor.SeekTo(_startUs, MediaExtractorSeekTo.PreviousSync);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[OsuVideo] MediaCodec can't play this video: {ex.Message}");
            Release();
        }
    }

    public double? ReadFrame(byte[] rgba)
    {
        if (_codec == null || (_extractor == null && _avi == null)) return null;
        while (!_cancelled && !_outputDone)
        {
            if (!_inputDone) FeedInput();

            int index = _codec.DequeueOutputBuffer(_info, TimeoutUs);
            if (index < 0) continue; // try again later / format or buffers changed
            if ((_info.Flags & MediaCodecBufferFlags.EndOfStream) != 0) _outputDone = true;

            long ptsUs = _info.PresentationTimeUs;
            if (_pending != null && _info.Size > 0 && _pending.TryDequeue(out long slotUs, out _))
                ptsUs = slotUs;
            double time = ptsUs / 1_000_000.0;
            bool keep = _info.Size > 0
                        && ptsUs >= _startUs - 1_000                               // before the seek target: decode only
                        && time >= _lastTime + 1.0 / _format.Fps - 0.002       // cap at the output frame rate
                        // Already late for the game (the conversion fell behind): decode only, so the
                        // decoder catches up instead of the video drifting back and restarting.
                        // Never two in a row skipped for longer than 0.25 s, so something is shown.
                        && !(_clock != null && time < _clock() - 1.5 / _format.Fps && time < _lastTime + 0.25);
            bool converted = false;
            if (keep)
            {
                using var image = _codec.GetOutputImage(index);
                if (image != null)
                    converted = Convert(image, rgba);
            }
            _codec.ReleaseOutputBuffer(index, false);
            if (converted)
            {
                _lastTime = time;
                return time;
            }
        }
        return null;
    }

    private void FeedInput()
    {
        int index = _codec!.DequeueInputBuffer(TimeoutUs);
        if (index < 0) return;
        var buffer = _codec.GetInputBuffer(index);
        if (_avi != null)
        {
            if (buffer == null || !_avi.Read(ref _aviBuf, out int n, out double t, out bool key))
            {
                _codec.QueueInputBuffer(index, 0, 0, 0, MediaCodecBufferFlags.EndOfStream);
                _inputDone = true;
                return;
            }
            n = Math.Min(n, buffer.Capacity());
            IntPtr dst = JNIEnv.GetDirectBufferAddress(buffer.Handle);
            if (dst != IntPtr.Zero)
                Marshal.Copy(_aviBuf, 0, dst, n);
            else
            {
                buffer.Clear();
                buffer.Put(_aviBuf, 0, n);
            }
            long us = (long)Math.Round(t * 1_000_000);
            _pending!.Enqueue(us, us);
            _codec.QueueInputBuffer(index, 0, n, us, key ? MediaCodecBufferFlags.KeyFrame : MediaCodecBufferFlags.None);
            return;
        }
        int size = buffer == null ? -1 : _extractor!.ReadSampleData(buffer, 0);
        if (size < 0)
        {
            _codec.QueueInputBuffer(index, 0, 0, 0, MediaCodecBufferFlags.EndOfStream);
            _inputDone = true;
        }
        else
        {
            _codec.QueueInputBuffer(index, 0, size, _extractor!.SampleTime, MediaCodecBufferFlags.None);
            _extractor.Advance();
        }
    }

    private bool Convert(Image image, byte[] rgba)
    {
        var crop = image.CropRect;
        int cx = crop?.Left ?? 0, cy = crop?.Top ?? 0;
        int cw = crop is { } c1 && c1.Width() > 0 ? c1.Width() : image.Width;
        int ch = crop is { } c2 && c2.Height() > 0 ? c2.Height() : image.Height;
        if (cw != _srcW || ch != _srcH || cx != _srcX || cy != _srcY) BuildMap(cx, cy, cw, ch);

        var planes = image.GetPlanes()!;
        var pY = planes[0]; var pU = planes[1]; var pV = planes[2];
        IntPtr aY = Address(pY.Buffer!, out int lenY);
        IntPtr aU = Address(pU.Buffer!, out int lenU);
        IntPtr aV = Address(pV.Buffer!, out int lenV);
        if (aY == IntPtr.Zero || aU == IntPtr.Zero || aV == IntPtr.Zero) return false;
        // Frame buffers are reused (OsuVideo's pool): paint the letterbox bars opaque black every
        // frame, like ffmpeg's pad.
        if (_dstW < _format.Width || _dstH < _format.Height) PaintBars(rgba);
        int rsY = pY.RowStride, rsU = pU.RowStride, rsV = pV.RowStride;
        int psY = pY.PixelStride, psU = pU.PixelStride, psV = pV.PixelStride;
        if (psY != _psY || psU != _psU || psV != _psV || _colY.Length != _dstW)
        {
            _psY = psY; _psU = psU; _psV = psV;
            _colY = new int[_dstW]; _colU = new int[_dstW]; _colV = new int[_dstW];
            for (int dx = 0; dx < _dstW; dx++)
            {
                int sx = _mapX[dx];
                _colY[dx] = sx * psY;
                _colU[dx] = (sx >> 1) * psU;
                _colV[dx] = (sx >> 1) * psV;
            }
        }

        // Rows in parallel bands (one core can't keep up with 1080p at 30 fps), each with its own
        // row buffers: only the sampled source rows are copied out of the codec's buffers.
        int bands = Math.Clamp(Math.Min(Environment.ProcessorCount, 4), 1, Math.Max(1, _dstH / 16));
        if (_bandRows.Length != bands) _bandRows = new byte[bands][][];
        int[] colY = _colY, colU = _colU, colV = _colV, mapY = _mapY;
        int dstX = _dstX, dstY = _dstY, dstW = _dstW, dstH = _dstH, outW = _format.Width;
        var bandRows = _bandRows;
        Parallel.For(0, bands, band =>
        {
            var rows = bandRows[band];
            if (rows == null || rows[0].Length < rsY || rows[1].Length < rsU || rows[2].Length < rsV)
                rows = bandRows[band] = new[] { new byte[rsY], new byte[rsU], new byte[rsV] };
            byte[] rowY = rows[0], rowU = rows[1], rowV = rows[2];
            int from = dstH * band / bands, to = dstH * (band + 1) / bands;
            int lastUvRow = -1;
            for (int dy = from; dy < to; dy++)
            {
                int sy = mapY[dy];
                CopyRow(aY, lenY, sy * rsY, rowY, rsY);
                int uvRow = sy >> 1;
                if (uvRow != lastUvRow)
                {
                    CopyRow(aU, lenU, uvRow * rsU, rowU, rsU);
                    CopyRow(aV, lenV, uvRow * rsV, rowV, rsV);
                    lastUvRow = uvRow;
                }
                int o = ((dstY + dy) * outW + dstX) * 4;
                for (int dx = 0; dx < dstW; dx++, o += 4)
                {
                    int yv = rowY[colY[dx]] - 16;
                    int u = rowU[colU[dx]] - 128;
                    int v = rowV[colV[dx]] - 128;
                    int c = 298 * (yv < 0 ? 0 : yv) + 128;
                    rgba[o] = Clamp((c + 409 * v) >> 8);
                    rgba[o + 1] = Clamp((c - 100 * u - 208 * v) >> 8);
                    rgba[o + 2] = Clamp((c + 516 * u) >> 8);
                    rgba[o + 3] = 255;
                }
            }
        });
        return true;
    }

    /// <summary>Opaque black outside the picture rect.</summary>
    private void PaintBars(byte[] rgba)
    {
        int w = _format.Width, h = _format.Height;
        for (int y = 0; y < h; y++)
        {
            bool inside = y >= _dstY && y < _dstY + _dstH;
            if (!inside) { Black(rgba, y * w, w); continue; }
            Black(rgba, y * w, _dstX);
            Black(rgba, y * w + _dstX + _dstW, w - _dstX - _dstW);
        }
    }

    private static void Black(byte[] rgba, int pixel, int count)
    {
        for (int i = pixel * 4, end = (pixel + count) * 4; i < end; i += 4)
        {
            rgba[i] = 0; rgba[i + 1] = 0; rgba[i + 2] = 0; rgba[i + 3] = 255;
        }
    }

    /// <summary>Fits the crop rect into the output size (black bars) and sets every pixel's source column/row.</summary>
    private void BuildMap(int cx, int cy, int cw, int ch)
    {
        _srcX = cx; _srcY = cy; _srcW = cw; _srcH = ch;
        double scale = Math.Min(_format.Width / (double)cw, _format.Height / (double)ch);
        _dstW = Math.Clamp((int)Math.Round(cw * scale), 1, _format.Width);
        _dstH = Math.Clamp((int)Math.Round(ch * scale), 1, _format.Height);
        _dstX = (_format.Width - _dstW) / 2;
        _dstY = (_format.Height - _dstH) / 2;
        _mapX = new int[_dstW];
        _mapY = new int[_dstH];
        for (int x = 0; x < _dstW; x++) _mapX[x] = cx + Math.Min(cw - 1, (int)((x + 0.5) * cw / _dstW));
        for (int y = 0; y < _dstH; y++) _mapY[y] = cy + Math.Min(ch - 1, (int)((y + 0.5) * ch / _dstH));
        _psY = -1; // force _colY/_colU/_colV rebuild even when _dstW didn't change
    }

    private static IntPtr Address(Java.Nio.ByteBuffer buffer, out int length)
    {
        length = buffer.Capacity();
        return JNIEnv.GetDirectBufferAddress(buffer.Handle);
    }

    private static void CopyRow(IntPtr plane, int planeLen, int offset, byte[] row, int stride)
    {
        if (offset < 0) return;
        int n = Math.Min(stride, planeLen - offset); // the last row of a plane can be shorter
        if (n > 0) Marshal.Copy(plane + offset, row, 0, n);
    }

    private static byte Clamp(int v) => (byte)(v < 0 ? 0 : v > 255 ? 255 : v);

    public void Cancel() => _cancelled = true;

    private void Release()
    {
        try { _codec?.Stop(); } catch { }
        try { _codec?.Release(); } catch { }
        try { _extractor?.Release(); } catch { }
        _avi?.Dispose();
    }

    public void Dispose()
    {
        Release();
        _codec?.Dispose();
        _extractor?.Dispose();
        _info.Dispose();
    }
}
#endif
