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
            return LoadWithPcmCore(data, out pcmData, out sampleRate, out channels, forceCompact: false);
        }
        catch (OutOfMemoryException)
        {
            Console.WriteLine("[OggLoader] OutOfMemoryException during normal decode; retrying in compact 22050Hz mono mode...");
            FnmmSceneRenderer.ForceCompactingGc();
            return LoadWithPcmCore(data, out pcmData, out sampleRate, out channels, forceCompact: true);
        }
        catch (Exception ex) when (IsNonVorbisStreamError(ex))
        {
            byte[]? vorbisOnly = ExtractVorbisStream(oggData);
            if (vorbisOnly != null)
            {
                try
                {
                    return LoadWithPcmCore(vorbisOnly, out pcmData, out sampleRate, out channels, forceCompact: false);
                }
                catch (OutOfMemoryException)
                {
                    Console.WriteLine("[OggLoader] OutOfMemoryException on extracted Vorbis; retrying in compact 22050Hz mono mode...");
                    FnmmSceneRenderer.ForceCompactingGc();
                    return LoadWithPcmCore(vorbisOnly, out pcmData, out sampleRate, out channels, forceCompact: true);
                }
            }
            throw new Exception($"Could not decode audio: {ex.Message}");
        }
    }

    private static SoundEffect LoadWithPcmCore(byte[] data,
        out byte[] pcmData, out int sampleRate, out int channels, bool forceCompact)
    {
        using var ms = new MemoryStream(data, writable: false);
        using var reader = new VorbisReader(ms, closeOnDispose: false);
        return DecodeVorbisWithPcm(reader, out pcmData, out sampleRate, out channels, forceCompact);
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

        // Validate and clamp sample rate to supported values
        int validatedSampleRate = ValidateSampleRate(sampleRate);

        return new SoundEffect(slice, validatedSampleRate,
            channels == 2 ? AudioChannels.Stereo : AudioChannels.Mono);
    }

    /// <summary>
    /// Creates a SoundEffect directly from an existing PCM buffer without copying or re-decoding OGG.
    /// </summary>
    public static SoundEffect CreateFromPcm(byte[] pcmData, int sampleRate, int channels)
    {
        int validatedSampleRate = ValidateSampleRate(sampleRate);
        return new SoundEffect(pcmData, validatedSampleRate,
            channels == 2 ? AudioChannels.Stereo : AudioChannels.Mono);
    }
    
    /// <summary>Closest sample rate MonoGame can play (fork: shared with the MP3 decoder).</summary>
    public static int ValidSampleRate(int sampleRate) => ValidateSampleRate(sampleRate);

    /// <summary>Linear resampling of 16-bit PCM (fork: shared with the MP3 decoder).</summary>
    public static byte[] Resample(byte[] pcmData, int originalRate, int targetRate, int channels)
        => ResamplePcm(pcmData, originalRate, targetRate, channels);

    private static int ValidateSampleRate(int sampleRate)
    {
        // MonoGame supports: 8000, 11025, 16000, 22050, 32000, 44100, 48000
        if (sampleRate < 8000)
            return 8000;
        if (sampleRate > 48000)
            return 48000;
        
        int[] validRates = { 8000, 11025, 16000, 22050, 32000, 44100, 48000 };
        
        // Check if already valid
        foreach (int rate in validRates)
        {
            if (sampleRate == rate)
                return sampleRate;
        }
        
        // Find closest valid sample rate
        int closest = validRates[0];
        int minDiff = Math.Abs(sampleRate - closest);
        
        foreach (int rate in validRates)
        {
            int diff = Math.Abs(sampleRate - rate);
            if (diff < minDiff)
            {
                minDiff = diff;
                closest = rate;
            }
        }
        
        Console.WriteLine($"[OggLoader] Invalid sample rate {sampleRate}Hz, using closest valid: {closest}Hz");
        return closest;
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
        return DecodeVorbisWithPcm(reader, out pcm, out sr, out ch, forceCompact: false);
    }

    /// <summary>
    /// Chooses target sample rate and channel count for streaming PCM decode.
    /// Desktop 64-bit always keeps native quality (just validated to a MonoGame-supported rate).
    /// Android / 32-bit automatically streams marathon songs (e.g. 10-25 min OGGs) at 22050Hz
    /// (and mono on 32-bit or huge >180MB songs) so we never allocate 150-300MB contiguous PCM arrays.
    /// </summary>
    internal static void ChooseTargetAudioFormat(
        int srcRate, int srcChannels, long estFullPcmBytes, bool forceCompact,
        out int dstRate, out int dstChannels)
    {
        dstRate = ValidateSampleRate(srcRate);
        dstChannels = Math.Clamp(srcChannels, 1, 2);

        if (forceCompact)
        {
            if (dstRate > 22050) dstRate = 22050;
            dstChannels = 1;
            return;
        }

#if ANDROID
        var mem = Platforms.Android.AndroidMemoryBudget.Query();
        if (estFullPcmBytes > mem.AudioMonoThresholdBytes)
        {
            if (dstRate > 22050) dstRate = 22050;
            dstChannels = 1;
        }
        else if (estFullPcmBytes > mem.AudioHalfRateThresholdBytes)
        {
            if (dstRate > 22050) dstRate = 22050;
        }
#else
        if (!Environment.Is64BitProcess)
        {
            if (estFullPcmBytes > 64L * 1024 * 1024)
            {
                if (dstRate > 22050) dstRate = 22050;
                dstChannels = 1;
            }
            else if (estFullPcmBytes > 24L * 1024 * 1024)
            {
                if (dstRate > 22050) dstRate = 22050;
            }
        }
#endif
    }

    /// <summary>
    /// Streams float samples from <paramref name="readFloats"/> into 16-bit PCM using fixed 1 MB chunks
    /// (eliminating MemoryStream doubling + ToArray double-copy spikes) with optional on-the-fly
    /// linear resampling and stereo-to-mono downmix.
    /// </summary>
    internal static byte[] DecodeFloatStreamToPcm(
        Func<float[], int, int, int> readFloats,
        int srcRate,
        int srcChannels,
        int dstRate,
        int dstChannels)
    {
        const int CHUNK_BYTES = 1024 * 1024; // 1 MB fixed chunks -> zero MemoryStream capacity doubling
        int dstFrameBytes = 2 * dstChannels;
        int alignedChunkBytes = (CHUNK_BYTES / dstFrameBytes) * dstFrameBytes;

        var chunks = new System.Collections.Generic.List<byte[]>(16);
        byte[] currentChunk = new byte[alignedChunkBytes];
        int chunkPos = 0;
        long totalBytes = 0;

        void WriteSample(short s)
        {
            if (chunkPos + 2 > currentChunk.Length)
            {
                chunks.Add(currentChunk);
                currentChunk = new byte[alignedChunkBytes];
                chunkPos = 0;
            }
            currentChunk[chunkPos++] = (byte)(s & 0xFF);
            currentChunk[chunkPos++] = (byte)((s >> 8) & 0xFF);
            totalBytes += 2;
        }

        int readFloatsPerCall = 4096 * srcChannels;
        float[] tmp = new float[readFloatsPerCall];
        bool sameRate = srcRate == dstRate;
        bool foldMono = srcChannels == 2 && dstChannels == 1;

        if (sameRate && !foldMono)
        {
            while (true)
            {
                int got = readFloats(tmp, 0, readFloatsPerCall);
                if (got <= 0) break;
                for (int i = 0; i < got; i++)
                {
                    short s = (short)Math.Clamp((int)(tmp[i] * 32767f), short.MinValue, short.MaxValue);
                    WriteSample(s);
                }
            }
        }
        else if (sameRate && foldMono)
        {
            while (true)
            {
                int got = readFloats(tmp, 0, readFloatsPerCall);
                if (got <= 0) break;
                int framesGot = got / 2;
                for (int f = 0; f < framesGot; f++)
                {
                    float mono = (tmp[f * 2] + tmp[f * 2 + 1]) * 0.5f;
                    short s = (short)Math.Clamp((int)(mono * 32767f), short.MinValue, short.MaxValue);
                    WriteSample(s);
                }
            }
        }
        else
        {
            // Streaming linear resampler across arbitrary chunk boundaries (zero second PCM buffer!)
            double step = (double)srcRate / dstRate;
            double pos = 0.0;
            bool hasPrev = false;
            float prevL = 0f, prevR = 0f;

            while (true)
            {
                int got = readFloats(tmp, 0, readFloatsPerCall);
                if (got <= 0) break;
                int framesGot = got / srcChannels;
                if (framesGot <= 0) break;

                // Indexed conceptually as: index -1 = previous chunk's last frame (when hasPrev),
                // indices 0..framesGot-1 = current chunk frames.
                while (pos < framesGot - 1 || (!hasPrev && pos < framesGot && framesGot == 1))
                {
                    int idx0 = (int)Math.Floor(pos);
                    double frac = pos - idx0;
                    int idx1 = Math.Min(idx0 + 1, framesGot - 1);

                    float s0L, s0R, s1L, s1R;
                    if (idx0 < 0 && hasPrev)
                    {
                        s0L = prevL;
                        s0R = prevR;
                    }
                    else
                    {
                        int c0 = Math.Clamp(idx0, 0, framesGot - 1) * srcChannels;
                        s0L = tmp[c0];
                        s0R = srcChannels == 2 ? tmp[c0 + 1] : s0L;
                    }

                    int c1 = idx1 * srcChannels;
                    s1L = tmp[c1];
                    s1R = srcChannels == 2 ? tmp[c1 + 1] : s1L;

                    float outL = (float)(s0L + (s1L - s0L) * frac);
                    if (dstChannels == 1)
                    {
                        float outR = (float)(s0R + (s1R - s0R) * frac);
                        float mono = foldMono ? (outL + outR) * 0.5f : outL;
                        WriteSample((short)Math.Clamp((int)(mono * 32767f), short.MinValue, short.MaxValue));
                    }
                    else
                    {
                        float outR = (float)(s0R + (s1R - s0R) * frac);
                        WriteSample((short)Math.Clamp((int)(outL * 32767f), short.MinValue, short.MaxValue));
                        WriteSample((short)Math.Clamp((int)(outR * 32767f), short.MinValue, short.MaxValue));
                    }

                    pos += step;
                }

                int lastOff = (framesGot - 1) * srcChannels;
                prevL = tmp[lastOff];
                prevR = srcChannels == 2 ? tmp[lastOff + 1] : prevL;
                hasPrev = true;
                pos -= framesGot;
            }
        }

        if (totalBytes <= 0)
            return Array.Empty<byte>();

        // Single exact-sized allocation; release chunk references as we copy so GC can reclaim them immediately.
        byte[] result = new byte[totalBytes];
        int writeOff = 0;
        for (int i = 0; i < chunks.Count; i++)
        {
            var c = chunks[i];
            Buffer.BlockCopy(c, 0, result, writeOff, c.Length);
            writeOff += c.Length;
            chunks[i] = Array.Empty<byte>();
        }
        if (chunkPos > 0)
            Buffer.BlockCopy(currentChunk, 0, result, writeOff, chunkPos);
        chunks.Clear();

        return result;
    }

    private static SoundEffect DecodeVorbisWithPcm(VorbisReader reader,
        out byte[] pcmData, out int sampleRate, out int channels, bool forceCompact = false)
    {
        int srcChannels = reader.Channels;
        int srcRate = reader.SampleRate;
        long estFullPcmBytes = reader.TotalSamples > 0 ? reader.TotalSamples * srcChannels * 2L : 0L;

        ChooseTargetAudioFormat(srcRate, srcChannels, estFullPcmBytes, forceCompact,
            out int dstRate, out int dstChannels);

        if (dstRate != srcRate || dstChannels != srcChannels)
        {
            Console.WriteLine(
                $"[OggLoader] Streaming audio optimization: {srcRate}Hz {srcChannels}ch " +
                $"(est {estFullPcmBytes / (1024f * 1024f):0.#}MB) -> {dstRate}Hz {dstChannels}ch");
        }

        pcmData = DecodeFloatStreamToPcm(
            (buf, off, cnt) => reader.ReadSamples(buf, off, cnt),
            srcRate, srcChannels, dstRate, dstChannels);

        sampleRate = dstRate;
        channels = dstChannels;

        return new SoundEffect(pcmData, dstRate,
            dstChannels == 2 ? AudioChannels.Stereo : AudioChannels.Mono);
    }
    
    private static byte[] ResamplePcm(byte[] pcmData, int originalRate, int targetRate, int channels)
    {
        // Simple linear interpolation resampling
        int originalSamples = pcmData.Length / (2 * channels); // 16-bit = 2 bytes
        double ratio = (double)targetRate / originalRate;
        int targetSamples = (int)(originalSamples * ratio);
        byte[] resampled = new byte[targetSamples * 2 * channels];
        
        for (int i = 0; i < targetSamples; i++)
        {
            double sourcePos = i / ratio;
            int sourceIndex = (int)sourcePos;
            double fraction = sourcePos - sourceIndex;
            
            if (sourceIndex >= originalSamples - 1)
                sourceIndex = originalSamples - 2;
            
            for (int ch = 0; ch < channels; ch++)
            {
                int offset1 = (sourceIndex * channels + ch) * 2;
                int offset2 = ((sourceIndex + 1) * channels + ch) * 2;
                
                short sample1 = (short)(pcmData[offset1] | (pcmData[offset1 + 1] << 8));
                short sample2 = (short)(pcmData[offset2] | (pcmData[offset2 + 1] << 8));
                
                // Linear interpolation
                short interpolated = (short)(sample1 + (sample2 - sample1) * fraction);
                
                int targetOffset = (i * channels + ch) * 2;
                resampled[targetOffset] = (byte)(interpolated & 0xFF);
                resampled[targetOffset + 1] = (byte)((interpolated >> 8) & 0xFF);
            }
        }
        
        Console.WriteLine($"[OggLoader] Resampled audio from {originalRate}Hz to {targetRate}Hz");
        return resampled;
    }
}
