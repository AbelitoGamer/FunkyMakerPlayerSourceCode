using System;
using System.IO;
using Microsoft.Xna.Framework.Audio;
using NLayer;

namespace FunkyMakerPlayer;

/// <summary>
/// Song audio decoding for gameplay (fork): OGG Vorbis (FNMM levels, via <see cref="OggLoader"/>) or
/// MP3 (most osu! beatmaps, via NLayer, which the online menu music already uses). Both end up as
/// 16-bit PCM so seeking (<see cref="OggLoader.SeekPcm"/>) works the same.
/// </summary>
public static class AudioDecoder
{
    public static bool IsOgg(byte[] data)
        => data.Length >= 4 && data[0] == 'O' && data[1] == 'g' && data[2] == 'g' && data[3] == 'S';

    public static SoundEffect LoadWithPcm(byte[] data, out byte[] pcmData, out int sampleRate, out int channels)
    {
        if (IsOgg(data))
            return OggLoader.LoadWithPcm(data, out pcmData, out sampleRate, out channels);

        try
        {
            pcmData = DecodeMp3(data, out sampleRate, out channels);
            return OggLoader.CreateFromPcm(pcmData, sampleRate, channels);
        }
        catch (Exception ex)
        {
            throw new Exception($"Could not decode the song audio (only OGG and MP3 are supported): {ex.Message}");
        }
    }

    /// <summary>Decodes a whole MP3 into 16-bit PCM (resampled when MonoGame can't play its rate).</summary>
    public static byte[] DecodeMp3(byte[] data, out int sampleRate, out int channels)
    {
        using var mp3 = new MpegFile(new MemoryStream(data));
        sampleRate = mp3.SampleRate;
        channels = mp3.Channels;
        if (channels is < 1 or > 2) throw new Exception($"{channels} channels");

        var floats = new float[Math.Max(4096, (int)Math.Min(int.MaxValue / 4, (long)(mp3.Duration.TotalSeconds * sampleRate * channels) + 4096))];
        int read = 0;
        var chunk = new float[4096 * channels];
        while (true)
        {
            int got = mp3.ReadSamples(chunk, 0, chunk.Length);
            if (got <= 0) break;
            if (read + got > floats.Length) Array.Resize(ref floats, read + got + chunk.Length * 16);
            Array.Copy(chunk, 0, floats, read, got);
            read += got;
        }
        if (read == 0) throw new Exception("no audio frames");

        var pcm = new byte[read * 2];
        for (int i = 0; i < read; i++)
        {
            short v = (short)Math.Clamp((int)(floats[i] * 32767f), short.MinValue, short.MaxValue);
            pcm[i * 2] = (byte)v;
            pcm[i * 2 + 1] = (byte)(v >> 8);
        }
        int valid = OggLoader.ValidSampleRate(sampleRate);
        if (valid != sampleRate)
        {
            pcm = OggLoader.Resample(pcm, sampleRate, valid, channels);
            sampleRate = valid;
        }
        return pcm;
    }
}
