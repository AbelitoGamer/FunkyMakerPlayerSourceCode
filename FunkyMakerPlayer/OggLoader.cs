using System;
using System.IO;
using Microsoft.Xna.Framework.Audio;
using NVorbis;

namespace FunkyMakerPlayer;

/// <summary>
/// Decodes an OGG Vorbis stream into a MonoGame SoundEffect.
/// Handles OGG files that may contain multiple logical streams.
/// </summary>
public static class OggLoader
{
    public static SoundEffect Load(byte[] oggData)
    {
        // First attempt: normal load
        try
        {
            return LoadInternal(oggData);
        }
        catch (Exception ex) when (IsNonVorbisStreamError(ex))
        {
            // The OGG contains a non-Vorbis stream (e.g. Theora) before the Vorbis stream.
            // Try to find and extract just the Vorbis pages by scanning the OGG structure.
            try
            {
                byte[]? vorbisOnly = ExtractVorbisStream(oggData);
                if (vorbisOnly != null)
                    return LoadInternal(vorbisOnly);
            }
            catch { }

            // Last resort: re-throw original
            throw new Exception($"Could not decode audio: {ex.Message}");
        }
    }

    /// <summary>
    /// Loads OGG and also returns the raw PCM bytes, sample rate, and channel count.
    /// Used for audio seek (timeskip) — create a new SoundEffect from a PCM offset.
    /// </summary>
    public static SoundEffect LoadWithPcm(byte[] oggData,
        out byte[] pcmData, out int sampleRate, out int channels)
    {
        byte[] data = oggData;
        try
        {
            using var ms     = new MemoryStream(data);
            using var reader = new VorbisReader(ms, closeOnDispose: false);
            return DecodeVorbisWithPcm(reader, out pcmData, out sampleRate, out channels);
        }
        catch (Exception ex) when (IsNonVorbisStreamError(ex))
        {
            byte[]? vorbisOnly = ExtractVorbisStream(oggData);
            if (vorbisOnly != null)
            {
                using var ms2     = new MemoryStream(vorbisOnly);
                using var reader2 = new VorbisReader(ms2, closeOnDispose: false);
                return DecodeVorbisWithPcm(reader2, out pcmData, out sampleRate, out channels);
            }
            throw new Exception($"Could not decode audio: {ex.Message}");
        }
    }

    /// <summary>
    /// Creates a SoundEffect starting at the given time offset (seconds) from stored PCM data.
    /// Returns null if the offset is past the end of the audio.
    /// </summary>
    public static SoundEffect? SeekPcm(byte[] pcmData, int sampleRate, int channels, float seekSeconds)
    {
        // PCM is 16-bit (2 bytes per sample), interleaved channels
        int bytesPerSample = 2 * channels;
        long byteOffset    = (long)(seekSeconds * sampleRate) * bytesPerSample;
        // Align to sample boundary
        byteOffset = (byteOffset / bytesPerSample) * bytesPerSample;

        if (byteOffset >= pcmData.Length) return null;

        int remaining = (int)(pcmData.Length - byteOffset);
        var slice     = new byte[remaining];
        Array.Copy(pcmData, byteOffset, slice, 0, remaining);

        return new SoundEffect(slice, sampleRate,
            channels == 2 ? AudioChannels.Stereo : AudioChannels.Mono);
    }

    private static bool IsNonVorbisStreamError(Exception ex)
        => ex.Message.Contains("Theora") ||
           ex.Message.Contains("bitstream") ||
           ex.Message.Contains("not a vorbis") ||
           ex.Message.Contains("not Vorbis") ||
           ex.Message.Contains("Found");

    private static SoundEffect LoadInternal(byte[] oggData)
    {
        using var ms     = new MemoryStream(oggData);
        using var reader = new VorbisReader(ms, closeOnDispose: false);
        return DecodeVorbis(reader);
    }

    /// <summary>
    /// Scans OGG pages and extracts only the pages belonging to the first Vorbis stream.
    /// Handles OGG files where Theora or other streams appear before Vorbis.
    /// </summary>
    private static byte[]? ExtractVorbisStream(byte[] data)
    {
        // Pass 1: find the serial number of the first Vorbis stream
        int vorbisSerial = -1;
        int pos = 0;

        while (pos + 27 < data.Length)
        {
            if (!IsOggPage(data, pos)) { pos++; continue; }

            byte headerType = data[pos + 5];
            int  serial     = BitConverter.ToInt32(data, pos + 14);
            byte numSegs    = data[pos + 26];
            if (pos + 27 + numSegs > data.Length) break;

            int pageDataSize = 0;
            for (int s = 0; s < numSegs; s++) pageDataSize += data[pos + 27 + s];
            int dataStart = pos + 27 + numSegs;
            int pageEnd   = dataStart + pageDataSize;
            if (pageEnd > data.Length) break;

            bool isBos = (headerType & 0x02) != 0;
            if (isBos && vorbisSerial == -1 && dataStart + 6 < data.Length)
            {
                // Vorbis identification header starts with 0x01 + "vorbis"
                if (data[dataStart] == 0x01 &&
                    data[dataStart+1] == 0x76 && data[dataStart+2] == 0x6F &&
                    data[dataStart+3] == 0x72 && data[dataStart+4] == 0x62 &&
                    data[dataStart+5] == 0x69 && data[dataStart+6] == 0x73)
                {
                    vorbisSerial = serial;
                }
            }

            pos = pageEnd;
        }

        if (vorbisSerial == -1) return null;

        // Pass 2: copy only pages belonging to the Vorbis stream
        var output = new MemoryStream();
        pos = 0;

        while (pos + 27 < data.Length)
        {
            if (!IsOggPage(data, pos)) { pos++; continue; }

            byte numSegs = data[pos + 26];
            if (pos + 27 + numSegs > data.Length) break;

            int pageDataSize = 0;
            for (int s = 0; s < numSegs; s++) pageDataSize += data[pos + 27 + s];
            int pageEnd = pos + 27 + numSegs + pageDataSize;
            if (pageEnd > data.Length) break;

            int serial = BitConverter.ToInt32(data, pos + 14);
            if (serial == vorbisSerial)
                output.Write(data, pos, pageEnd - pos);

            pos = pageEnd;
        }

        return output.Length > 0 ? output.ToArray() : null;
    }

    private static bool IsOggPage(byte[] data, int pos)
        => pos + 4 <= data.Length &&
           data[pos]   == 0x4F && data[pos+1] == 0x67 &&
           data[pos+2] == 0x67 && data[pos+3] == 0x53;

    private static SoundEffect DecodeVorbis(VorbisReader reader)
    {
        byte[] pcm; int sr, ch;
        return DecodeVorbisWithPcm(reader, out pcm, out sr, out ch);
    }

    private static SoundEffect DecodeVorbisWithPcm(VorbisReader reader,
        out byte[] pcmData, out int sampleRate, out int channels)
    {
        channels   = reader.Channels;
        sampleRate = reader.SampleRate;
        int totalSamples = (int)reader.TotalSamples * channels;

        float[] floatBuf = new float[Math.Max(totalSamples, 4096 * channels)];
        int   read  = 0;
        int   chunk = 4096 * channels;
        float[] tmp = new float[chunk];

        while (true)
        {
            int got = reader.ReadSamples(tmp, 0, chunk);
            if (got <= 0) break;
            if (read + got > floatBuf.Length)
                Array.Resize(ref floatBuf, read + got + chunk);
            Array.Copy(tmp, 0, floatBuf, read, got);
            read += got;
        }

        pcmData = new byte[read * 2];
        for (int i = 0; i < read; i++)
        {
            short s = (short)Math.Clamp((int)(floatBuf[i] * 32767f), short.MinValue, short.MaxValue);
            pcmData[i * 2]     = (byte)(s & 0xFF);
            pcmData[i * 2 + 1] = (byte)((s >> 8) & 0xFF);
        }

        return new SoundEffect(pcmData, sampleRate,
            channels == 2 ? AudioChannels.Stereo : AudioChannels.Mono);
    }
}
