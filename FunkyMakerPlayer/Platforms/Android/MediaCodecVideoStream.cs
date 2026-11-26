#if ANDROID
using System;
using System.Runtime.InteropServices;
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
/// Containers are whatever MediaExtractor reads (mp4/mov, mkv/webm, 3gp, ts…; not avi/flv/wmv).
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
    private byte[] _rowY = Array.Empty<byte>(), _rowU = Array.Empty<byte>(), _rowV = Array.Empty<byte>();

    private readonly VideoFormat _format;

    /// <summary>
    /// The video track's size and frame rate (0 when the container doesn't say), or null when
    /// MediaExtractor can't read it.
    /// </summary>
    public static (int Width, int Height, double Fps)? Probe(string file)
    {
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
        if (_codec == null || _extractor == null) return null;
        while (!_cancelled && !_outputDone)
        {
            if (!_inputDone) FeedInput();

            int index = _codec.DequeueOutputBuffer(_info, TimeoutUs);
            if (index < 0) continue; // try again later / format or buffers changed
            if ((_info.Flags & MediaCodecBufferFlags.EndOfStream) != 0) _outputDone = true;

            double time = _info.PresentationTimeUs / 1_000_000.0;
            bool keep = _info.Size > 0
                        && _info.PresentationTimeUs >= _startUs - 1_000           // before the seek target: decode only
                        && time >= _lastTime + 1.0 / _format.Fps - 0.002;      // cap at the output frame rate
            bool converted = false;
            if (keep)
            {
                using var image = _codec.GetOutputImage(index);
                if (image != null)
                {
                    Convert(image, rgba);
                    converted = true;
                }
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

    private void Convert(Image image, byte[] rgba)
    {
        var crop = image.CropRect;
        int cx = crop?.Left ?? 0, cy = crop?.Top ?? 0;
        int cw = crop is { } c1 && c1.Width() > 0 ? c1.Width() : image.Width;
        int ch = crop is { } c2 && c2.Height() > 0 ? c2.Height() : image.Height;
        if (cw != _srcW || ch != _srcH || cx != _srcX || cy != _srcY) BuildMap(cx, cy, cw, ch);
        // Each frame buffer is new (all zero): make the letterbox bars opaque black like ffmpeg's pad.
        if (_dstW < _format.Width || _dstH < _format.Height)
            for (int i = 3; i < rgba.Length; i += 4) rgba[i] = 255;

        var planes = image.GetPlanes()!;
        var pY = planes[0]; var pU = planes[1]; var pV = planes[2];
        IntPtr aY = Address(pY.Buffer!, out int lenY);
        IntPtr aU = Address(pU.Buffer!, out int lenU);
        IntPtr aV = Address(pV.Buffer!, out int lenV);
        int rsY = pY.RowStride, rsU = pU.RowStride, rsV = pV.RowStride;
        int psY = pY.PixelStride, psU = pU.PixelStride, psV = pV.PixelStride;
        if (_rowY.Length < rsY) _rowY = new byte[rsY];
        if (_rowU.Length < rsU) _rowU = new byte[rsU];
        if (_rowV.Length < rsV) _rowV = new byte[rsV];

        int lastUvRow = -1;
        for (int dy = 0; dy < _dstH; dy++)
        {
            int sy = _mapY[dy];
            CopyRow(aY, lenY, sy * rsY, _rowY, rsY);
            int uvRow = sy >> 1;
            if (uvRow != lastUvRow)
            {
                CopyRow(aU, lenU, uvRow * rsU, _rowU, rsU);
                CopyRow(aV, lenV, uvRow * rsV, _rowV, rsV);
                lastUvRow = uvRow;
            }
            int o = ((_dstY + dy) * _format.Width + _dstX) * 4;
            for (int dx = 0; dx < _dstW; dx++, o += 4)
            {
                int sx = _mapX[dx];
                int yv = _rowY[sx * psY] - 16;
                int u = _rowU[(sx >> 1) * psU] - 128;
                int v = _rowV[(sx >> 1) * psV] - 128;
                int c = 298 * (yv < 0 ? 0 : yv) + 128;
                rgba[o] = Clamp((c + 409 * v) >> 8);
                rgba[o + 1] = Clamp((c - 100 * u - 208 * v) >> 8);
                rgba[o + 2] = Clamp((c + 516 * u) >> 8);
                rgba[o + 3] = 255;
            }
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
    }

    private static IntPtr Address(Java.Nio.ByteBuffer buffer, out int length)
    {
        length = buffer.Capacity();
        return JNIEnv.GetDirectBufferAddress(buffer.Handle);
    }

    private static void CopyRow(IntPtr plane, int planeLen, int offset, byte[] row, int stride)
    {
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
