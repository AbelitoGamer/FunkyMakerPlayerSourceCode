using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace FunkyMakerPlayer.Osu;

/// <summary>
/// Minimal AVI reader (fork) for the Android video path: Android's MediaExtractor doesn't read AVI,
/// and many osu! beatmaps ship their video as one (H.264 or MPEG-4/Xvid inside). This finds the first
/// video stream, lists its frame slots (a scan of the movi chunks, OpenDML RIFF-AVIX parts included;
/// zero-size "null" chunks are dropped frames and keep their slot) and hands out the compressed frames
/// in file (decode) order, H.264 as Annex B, with each slot's time. The decoder does the rest.
/// </summary>
internal sealed class AviDemuxer : IDisposable
{
    private readonly FileStream _fs;
    private readonly List<(long Offset, int Size, bool Key)> _frames = new();
    private int _next;
    private int _nalLengthSize; // > 0: H.264 stored length-prefixed (avcC), converted to Annex B on read
    private readonly double _frameDuration;
    private readonly double _start;

    public string FourCC { get; }
    public int Width { get; }
    public int Height { get; }
    public double Fps => 1.0 / _frameDuration;
    /// <summary>The MediaCodec mime type for <see cref="FourCC"/>, or null when it's a codec we don't map.</summary>
    public string? Mime { get; }
    /// <summary>Codec config (H.264: SPS, PPS as Annex B; MPEG-4: the VOL header), or empty.</summary>
    public List<byte[]> CodecConfig { get; } = new();
    public int FrameCount => _frames.Count;
    public int MaxFrameSize { get; private set; }

    /// <summary>True when the file starts like an AVI (RIFF....AVI ).</summary>
    public static bool IsAvi(string path)
    {
        try
        {
            using var fs = File.OpenRead(path);
            Span<byte> h = stackalloc byte[12];
            return fs.Read(h) == 12 && h[..4].SequenceEqual("RIFF"u8) && h[8..12].SequenceEqual("AVI "u8);
        }
        catch { return false; }
    }

    /// <summary>Opens <paramref name="path"/>, or returns null (with a log line) when it isn't a readable AVI video.</summary>
    public static AviDemuxer? Open(string path)
    {
        try { return new AviDemuxer(path); }
        catch (Exception ex)
        {
            Console.WriteLine($"[OsuVideo] AVI not readable: {ex.Message}");
            return null;
        }
    }

    private AviDemuxer(string path)
    {
        _fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 16);
        try
        {
            var r = new BinaryReader(_fs, Encoding.ASCII, leaveOpen: true);
            int videoIndex = -1, streamCount = 0;
            uint scale = 0, rate = 0, startFrames = 0;
            string fourcc = "";
            int width = 0, height = 0;
            byte[] extradata = Array.Empty<byte>();
            var keyFlags = new List<bool>(); // idx1 keyframe flags of the video stream, in order
            bool haveIdx1 = false;

            long fileLen = _fs.Length;
            long pos = 0;
            // Top level: RIFF AVI , then optional RIFF AVIX parts (OpenDML, files over 1 GB).
            while (pos + 12 <= fileLen)
            {
                _fs.Position = pos;
                string id = Fcc(r);
                long size = r.ReadUInt32();
                string form = Fcc(r);
                if (id != "RIFF" || (form != "AVI " && form != "AVIX")) break;
                long end = Math.Min(fileLen, pos + 8 + size);
                ParseList(r, pos + 12, end);
                pos = end + (size & 1);
            }

            void ParseList(BinaryReader rd, long from, long to)
            {
                long p = from;
                while (p + 8 <= to)
                {
                    _fs.Position = p;
                    string id = Fcc(rd);
                    long size = rd.ReadUInt32();
                    long data = p + 8;
                    if (size > to - data) size = to - data; // truncated file: keep what's there
                    if (id == "LIST" && size >= 4)
                    {
                        string type = Fcc(rd);
                        if (type == "strl") ParseStrl(rd, data + 4, data + size);
                        else if (type == "movi") ScanMovi(rd, data + 4, data + size);
                        else if (type is "hdrl" or "rec ") ParseList(rd, data + 4, data + size);
                    }
                    else if (id == "idx1" && videoIndex >= 0 && !haveIdx1)
                    {
                        haveIdx1 = true;
                        ReadIdx1(rd, data, size);
                    }
                    p = data + size + (size & 1);
                }
            }

            void ParseStrl(BinaryReader rd, long from, long to)
            {
                int stream = streamCount++;
                bool isVideo = false;
                long p = from;
                while (p + 8 <= to)
                {
                    _fs.Position = p;
                    string id = Fcc(rd);
                    long size = rd.ReadUInt32();
                    long data = p + 8;
                    if (id == "strh" && size >= 32)
                    {
                        string type = Fcc(rd);
                        isVideo = type == "vids" && videoIndex < 0;
                        if (isVideo)
                        {
                            videoIndex = stream;
                            _fs.Position = data + 20;
                            scale = rd.ReadUInt32();
                            rate = rd.ReadUInt32();
                            startFrames = rd.ReadUInt32();
                        }
                    }
                    else if (id == "strf" && isVideo && size >= 40)
                    {
                        uint biSize = rd.ReadUInt32();
                        width = rd.ReadInt32();
                        height = Math.Abs(rd.ReadInt32());
                        _fs.Position = data + 16;
                        fourcc = Fcc(rd);
                        long extraLen = Math.Min(size, Math.Max(biSize, 40)) - 40;
                        if (extraLen > 0 && extraLen < 1 << 20)
                        {
                            _fs.Position = data + 40;
                            extradata = rd.ReadBytes((int)extraLen);
                        }
                    }
                    p = data + size + (size & 1);
                }
            }

            void ScanMovi(BinaryReader rd, long from, long to)
            {
                if (videoIndex < 0) return;
                string tag = videoIndex.ToString("00");
                long p = from;
                while (p + 8 <= to)
                {
                    _fs.Position = p;
                    string id = Fcc(rd);
                    long size = rd.ReadUInt32();
                    long data = p + 8;
                    if (size > to - data) break; // truncated last chunk
                    if (id == "LIST" && size >= 4)
                    {
                        if (Fcc(rd) == "rec ") ScanMovi(rd, data + 4, data + size);
                    }
                    else if (id.Length == 4 && id[0] == tag[0] && id[1] == tag[1] && (id[2..] is "dc" or "db"))
                    {
                        _frames.Add((data, (int)size, false));
                        if (size > MaxFrameSize) MaxFrameSize = (int)size;
                    }
                    p = data + size + (size & 1);
                }
            }

            void ReadIdx1(BinaryReader rd, long data, long size)
            {
                string tag = videoIndex.ToString("00");
                long n = size / 16;
                _fs.Position = data;
                var buf = rd.ReadBytes((int)Math.Min(size, int.MaxValue));
                for (long i = 0; i < n; i++)
                {
                    int o = (int)(i * 16);
                    if (buf[o] != tag[0] || buf[o + 1] != tag[1]) continue;
                    if (!((buf[o + 2] == 'd' && buf[o + 3] == 'c') || (buf[o + 2] == 'd' && buf[o + 3] == 'b'))) continue;
                    keyFlags.Add((BitConverter.ToUInt32(buf, o + 4) & 0x10) != 0);
                }
            }

            if (videoIndex < 0 || _frames.Count == 0) throw new InvalidDataException("no video stream");
            if (scale == 0 || rate == 0) { scale = 1; rate = 30; }
            _frameDuration = scale / (double)rate;
            _start = startFrames * _frameDuration;
            FourCC = fourcc;
            Width = width;
            Height = height;
            Mime = MimeFor(fourcc);
            if (Mime == null) throw new InvalidDataException($"unsupported codec '{fourcc.Trim()}'");

            // Keyframes: idx1's flags when they line up with the scanned frames (the usual case: one
            // RIFF part), otherwise from the frame data itself.
            bool useIdx1 = haveIdx1 && keyFlags.Count == _frames.Count;
            if (Mime == "video/avc" && extradata.Length > 7 && extradata[0] == 1)
                ParseAvcC(extradata);
            else if (Mime == "video/mp4v-es" && extradata.Length > 0)
                CodecConfig.Add(extradata);
            for (int i = 0; i < _frames.Count; i++)
            {
                var f = _frames[i];
                if (f.Size == 0) continue;
                bool key = useIdx1 ? keyFlags[i] : IsKeyframe(Peek(f.Offset, Math.Min(f.Size, 512)));
                _frames[i] = (f.Offset, f.Size, key);
            }
            if (CodecConfig.Count == 0) FindInBandConfig();
        }
        catch
        {
            _fs.Dispose();
            throw;
        }
    }

    private static string Fcc(BinaryReader r) => Encoding.ASCII.GetString(r.ReadBytes(4));

    private static string? MimeFor(string fourcc) => fourcc.ToUpperInvariant() switch
    {
        "H264" or "X264" or "AVC1" or "DAVC" or "VSSH" => "video/avc",
        "XVID" or "DIVX" or "DX50" or "FMP4" or "MP4V" or "M4S2" => "video/mp4v-es",
        _ => null,
    };

    private byte[] Peek(long offset, int count)
    {
        _fs.Position = offset;
        var b = new byte[count];
        int got = 0;
        while (got < count)
        {
            int n = _fs.Read(b, got, count - got);
            if (n <= 0) break;
            got += n;
        }
        return got == count ? b : b.AsSpan(0, got).ToArray();
    }

    /// <summary>avcC extradata: SPS/PPS as Annex B config, and frames are length-prefixed.</summary>
    private void ParseAvcC(byte[] x)
    {
        _nalLengthSize = (x[4] & 3) + 1;
        int p = 5;
        for (int set = 0; set < 2 && p < x.Length; set++)
        {
            int count = set == 0 ? x[p++] & 0x1F : x[p++];
            for (int i = 0; i < count && p + 2 <= x.Length; i++)
            {
                int len = (x[p] << 8) | x[p + 1];
                p += 2;
                if (p + len > x.Length) return;
                var nal = new byte[len + 4];
                nal[3] = 1;
                Array.Copy(x, p, nal, 4, len);
                CodecConfig.Add(nal);
                p += len;
            }
        }
    }

    /// <summary>Annex B in-band config: the first keyframe's SPS + PPS (H.264) or the header before the first VOP (MPEG-4).</summary>
    private void FindInBandConfig()
    {
        foreach (var f in _frames)
        {
            if (!f.Key || f.Size == 0) continue;
            var data = Peek(f.Offset, f.Size);
            if (Mime == "video/avc")
            {
                byte[]? sps = null, pps = null;
                foreach (var (start, len) in AnnexBNals(data))
                {
                    int type = data[start] & 0x1F;
                    if (type == 7 && sps == null) sps = WithStartCode(data, start, len);
                    else if (type == 8 && pps == null) pps = WithStartCode(data, start, len);
                }
                if (sps != null && pps != null) { CodecConfig.Add(sps); CodecConfig.Add(pps); }
            }
            else
            {
                int vop = IndexOfStartCode(data, 0xB6);
                if (vop > 0) CodecConfig.Add(data.AsSpan(0, vop).ToArray());
            }
            return;
        }
    }

    private bool IsKeyframe(byte[] data)
    {
        if (Mime == "video/avc")
        {
            if (_nalLengthSize > 0)
            {
                for (int p = 0; p + _nalLengthSize < data.Length;)
                {
                    int len = 0;
                    for (int i = 0; i < _nalLengthSize; i++) len = (len << 8) | data[p + i];
                    p += _nalLengthSize;
                    int type = data[p] & 0x1F;
                    if (type == 5) return true;
                    if (type is 1) return false;
                    p += len;
                }
                return false;
            }
            foreach (var (start, _) in AnnexBNals(data))
            {
                int type = data[start] & 0x1F;
                if (type == 5) return true;
                if (type == 1) return false;
            }
            return false;
        }
        // MPEG-4 part 2: vop_coding_type (top two bits after 00 00 01 B6) 0 = I-VOP.
        int v = IndexOfStartCode(data, 0xB6);
        return v >= 0 && v + 4 < data.Length && (data[v + 4] >> 6) == 0;
    }

    private static int IndexOfStartCode(byte[] d, byte code)
    {
        for (int i = 0; i + 3 < d.Length; i++)
            if (d[i] == 0 && d[i + 1] == 0 && d[i + 2] == 1 && d[i + 3] == code) return i;
        return -1;
    }

    /// <summary>(payload start, payload length) of every Annex B NAL unit in <paramref name="d"/>.</summary>
    private static IEnumerable<(int Start, int Length)> AnnexBNals(byte[] d)
    {
        int start = -1;
        for (int i = 0; i + 2 < d.Length; i++)
        {
            if (d[i] != 0 || d[i + 1] != 0 || d[i + 2] != 1) continue;
            if (start >= 0)
            {
                int end = i > 0 && d[i - 1] == 0 ? i - 1 : i;
                if (end > start) yield return (start, end - start);
            }
            start = i + 3;
            i += 2;
        }
        if (start >= 0 && start < d.Length) yield return (start, d.Length - start);
    }

    private static byte[] WithStartCode(byte[] d, int start, int len)
    {
        var nal = new byte[len + 4];
        nal[3] = 1;
        Array.Copy(d, start, nal, 4, len);
        return nal;
    }

    /// <summary>Next reads start at the last keyframe at or before <paramref name="sec"/>.</summary>
    public void SeekTo(double sec)
    {
        int target = (int)Math.Floor((sec - _start) / _frameDuration + 1e-6);
        target = Math.Clamp(target, 0, _frames.Count - 1);
        int k = target;
        while (k > 0 && !_frames[k].Key) k--;
        _next = k;
    }

    /// <summary>
    /// The next non-empty frame (decode order) into <paramref name="buffer"/> (grown as needed), with
    /// its slot time in seconds. False at the end.
    /// </summary>
    public bool Read(ref byte[] buffer, out int size, out double time, out bool key)
    {
        while (_next < _frames.Count)
        {
            int i = _next++;
            var f = _frames[i];
            if (f.Size == 0) continue; // null frame: the previous picture stays up
            time = _start + i * _frameDuration;
            key = f.Key;
            _fs.Position = f.Offset;
            int cap = _nalLengthSize > 0 ? f.Size * 2 + 16 : f.Size;
            if (buffer.Length < cap) buffer = new byte[cap];
            if (_nalLengthSize == 0)
            {
                size = ReadFully(buffer, 0, f.Size);
                return true;
            }
            var raw = Peek(f.Offset, f.Size);
            size = 0;
            for (int p = 0; p + _nalLengthSize <= raw.Length;)
            {
                int len = 0;
                for (int b = 0; b < _nalLengthSize; b++) len = (len << 8) | raw[p + b];
                p += _nalLengthSize;
                if (len <= 0 || p + len > raw.Length) break;
                if (size + len + 4 > buffer.Length) Array.Resize(ref buffer, (size + len + 4) * 2);
                buffer[size] = 0; buffer[size + 1] = 0; buffer[size + 2] = 0; buffer[size + 3] = 1;
                Array.Copy(raw, p, buffer, size + 4, len);
                size += len + 4;
                p += len;
            }
            return true;
        }
        size = 0;
        time = 0;
        key = false;
        return false;
    }

    private int ReadFully(byte[] b, int off, int count)
    {
        int got = 0;
        while (got < count)
        {
            int n = _fs.Read(b, off + got, count - got);
            if (n <= 0) break;
            got += n;
        }
        return got;
    }

    public void Dispose() => _fs.Dispose();
}
