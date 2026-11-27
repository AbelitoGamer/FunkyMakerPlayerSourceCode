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
                string? path = CaseInsensitivePaths.ResolveFile(dir, name + ext);
                if (path != null) return path;
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
            if (ext == ".wav")
            {
                using var ms = new MemoryStream(data, writable: false);
                return SoundEffect.FromStream(ms);
            }
            if (ext == ".mp3")
                return AudioDecoder.LoadWithPcm(data, out _, out _, out _);
            return OggLoader.Load(data);
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
        float volume = Math.Clamp(Game1.MasterVolume * UI.UiSfx.Volume * gain, 0f, 1f);
        if (volume <= 0f) return;
        try { sound.Play(volume, 0f, 0f); }
        catch (Exception ex) { Console.WriteLine($"[SoundFiles] Play failed: {ex.Message}"); }
    }
}
