using System;
using System.Collections.Generic;
using System.IO;
using Microsoft.Xna.Framework.Audio;
using NLayer;

namespace FunkyMakerPlayer;

/// <summary>
/// Short sound effects shipped inside mod folders (countdown voices, note skin miss sounds).
/// Accepts .ogg, .wav and .mp3; everything is decoded into a SoundEffect up front.
/// </summary>
public static class SoundFiles
{
    public static readonly string[] Extensions = { ".ogg", ".wav", ".mp3" };

    /// <summary>First existing file named <c>&lt;baseName&gt;&lt;ext&gt;</c> in <paramref name="dir"/>
    /// for any of the base names (in order) and supported extensions; null if none.</summary>
    public static string? Find(string dir, params string[] baseNames)
    {
        if (!Directory.Exists(dir)) return null;
        foreach (string name in baseNames)
            foreach (string ext in Extensions)
            {
                string path = Path.Combine(dir, name + ext);
                if (File.Exists(path)) return path;
            }
        return null;
    }

    /// <summary>Loads a sound file; returns null (and logs) when it is missing or unreadable.</summary>
    public static SoundEffect? Load(string? path)
    {
        if (string.IsNullOrEmpty(path) || !File.Exists(path)) return null;
        try
        {
            byte[] data = File.ReadAllBytes(path);
            string ext = Path.GetExtension(path).ToLowerInvariant();
            return ext switch
            {
                ".wav" => SoundEffect.FromStream(new MemoryStream(data)),
                ".mp3" => LoadMp3(data),
                _ => OggLoader.Load(data),
            };
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[SoundFiles] Could not load {path}: {ex.Message}");
            return null;
        }
    }

    /// <summary>Plays a sound at the game's SFX level (master × SFX Volume) times <paramref name="gain"/>.</summary>
    public static void Play(SoundEffect? sound, float gain = 1f)
    {
        if (sound == null) return;
        float volume = Math.Clamp(Game1.MusicVolume * UI.UiSfx.Volume * gain, 0f, 1f);
        if (volume <= 0f) return;
        try { sound.Play(volume, 0f, 0f); }
        catch (Exception ex) { Console.WriteLine($"[SoundFiles] Play failed: {ex.Message}"); }
    }

    private static SoundEffect LoadMp3(byte[] data)
    {
        using var mp3 = new MpegFile(new MemoryStream(data));
        var samples = new List<float>();
        var buffer = new float[4096];
        int read;
        while ((read = mp3.ReadSamples(buffer, 0, buffer.Length)) > 0)
            for (int i = 0; i < read; i++) samples.Add(buffer[i]);

        var pcm = new byte[samples.Count * 2];
        for (int i = 0; i < samples.Count; i++)
        {
            short v = (short)Math.Clamp((int)(samples[i] * 32767f), short.MinValue, short.MaxValue);
            pcm[i * 2] = (byte)(v & 0xFF);
            pcm[i * 2 + 1] = (byte)((v >> 8) & 0xFF);
        }
        return OggLoader.CreateFromPcm(pcm, mp3.SampleRate, mp3.Channels);
    }
}
