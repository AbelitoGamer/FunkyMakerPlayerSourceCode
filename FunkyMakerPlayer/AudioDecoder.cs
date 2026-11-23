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
            pcmData = DecodeMp3(data, out sampleRate, out channels, forceCompact: false);
            return OggLoader.CreateFromPcm(pcmData, sampleRate, channels);
        }
        catch (OutOfMemoryException)
        {
            Console.WriteLine("[AudioDecoder] OutOfMemoryException during MP3 decode; retrying in compact 22050Hz mono mode...");
            FnmmSceneRenderer.ForceCompactingGc();
            pcmData = DecodeMp3(data, out sampleRate, out channels, forceCompact: true);
            return OggLoader.CreateFromPcm(pcmData, sampleRate, channels);
        }
        catch (Exception ex)
        {
            throw new Exception($"Could not decode the song audio (only OGG and MP3 are supported): {ex.Message}");
        }
    }

    /// <summary>Decodes a whole MP3 into 16-bit PCM using fixed chunks (zero MemoryStream doubling) and streaming resampling.</summary>
    public static byte[] DecodeMp3(byte[] data, out int sampleRate, out int channels, bool forceCompact = false)
    {
        using var mp3 = new MpegFile(new MemoryStream(data, writable: false));
        int srcRate = mp3.SampleRate;
        int srcChannels = mp3.Channels;
        if (srcChannels is < 1 or > 2) throw new Exception($"{srcChannels} channels");

        long estFullPcmBytes = mp3.Duration.TotalSeconds > 0
            ? (long)(mp3.Duration.TotalSeconds * srcRate * srcChannels * 2.0)
            : 0L;

        OggLoader.ChooseTargetAudioFormat(srcRate, srcChannels, estFullPcmBytes, forceCompact,
            out int dstRate, out int dstChannels);

        if (dstRate != srcRate || dstChannels != srcChannels)
        {
            Console.WriteLine(
                $"[AudioDecoder] Streaming MP3 optimization: {srcRate}Hz {srcChannels}ch " +
                $"(est {estFullPcmBytes / (1024f * 1024f):0.#}MB) -> {dstRate}Hz {dstChannels}ch");
        }

        byte[] pcm = OggLoader.DecodeFloatStreamToPcm(
            (buf, off, cnt) => mp3.ReadSamples(buf, off, cnt),
            srcRate, srcChannels, dstRate, dstChannels);

        if (pcm.Length == 0) throw new Exception("no audio frames");

        sampleRate = dstRate;
        channels = dstChannels;
        return pcm;
    }
}
