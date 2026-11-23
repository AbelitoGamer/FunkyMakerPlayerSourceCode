using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Microsoft.Xna.Framework.Audio;

namespace FunkyMakerPlayer.Osu;

/// <summary>
/// Hitsounds / keysounds of an osu!mania difficulty (fork), resolved like osu! (ppy/osu, MIT:
/// ConvertHitObjectParser, HitSampleInfo, LegacyHitSampleInfo, LegacyBeatmapSkin, LegacyBeatmapDecoder):
/// <list type="bullet">
/// <item>Bank (1 normal, 2 soft, 3 drum), custom index and volume come from the note's hitSample
/// fields, or (when 0) from the timing point at the note's end time + 5 ms; bank 0 on the timing point
/// = [General] SampleSet. Additions use the addition bank (or the normal bank).</item>
/// <item>hitnormal plays when the note has the normal flag or no flags (mania doesn't layer it under
/// additions); whistle 2, finish 4, clap 8 are added on top.</item>
/// <item>A filename in the hitSample plays only that file (keysounded maps).</item>
/// <item>Index ≥ 1 → the beatmap's own file "{bank}-{name}{index ≥ 2 ? index : ""}" (.wav/.ogg/.mp3);
/// otherwise the user's osu! skin "{bank}-{name}" when the note style is one; else nothing (osu!'s
/// default sounds aren't shipped).</item>
/// <item>Hold notes only sound at the head. They play when the key is pressed (the next note of the
/// column, like osu!mania's key sounds) and on autoplay hits.</item>
/// </list>
/// </summary>
public sealed class OsuHitsounds
{
    private sealed record Sound(string Path, float Volume);

    private readonly List<(float Time, Sound[] Sounds)>[] _lanes = { new(), new(), new(), new() };
    private readonly Dictionary<string, SoundEffect?> _effects = new(StringComparer.OrdinalIgnoreCase);

    public int SoundCount => _effects.Count;

    private static readonly string[] Banks = { "normal", "normal", "soft", "drum" };
    private static readonly string[] Extensions = { ".wav", ".ogg", ".mp3" };

    /// <summary>Resolves every note's sounds. <paramref name="userSkinFolder"/>: the selected osu! skin folder, or null.</summary>
    public static OsuHitsounds? Build(OsuBeatmap map, string folder, string? userSkinFolder)
    {
        var hs = new OsuHitsounds();
        var files = IndexFolder(folder);
        var skinFiles = userSkinFolder != null ? IndexFolder(userSkinFolder) : null;
        var points = map.TimingPoints.OrderBy(t => t.TimeMs).ToList();

        OsuTimingPoint? PointAt(double time)
        {
            OsuTimingPoint? found = null;
            foreach (var tp in points)
            {
                if (tp.TimeMs > time) break;
                found = tp;
            }
            return found ?? points.FirstOrDefault();
        }

        string? Find(Dictionary<string, string>? index, string name)
        {
            if (index == null) return null;
            string key = name.Replace('\\', '/').ToLowerInvariant();
            if (index.TryGetValue(key, out var exact)) return exact;
            string bare = Path.ChangeExtension(key, null)!;
            foreach (var ext in Extensions)
                if (index.TryGetValue(bare + ext, out var p)) return p;
            return null;
        }

        foreach (var n in map.Notes)
        {
            var tp = PointAt(n.EndMs + 5);
            float volume = (n.Volume > 0 ? n.Volume : tp?.Volume ?? 100) / 100f;
            var sounds = new List<Sound>();

            if (n.Filename != null)
            {
                if (Find(files, n.Filename) is { } f) sounds.Add(new Sound(f, volume));
            }
            else
            {
                int tpSet = tp?.SampleSet is > 0 and <= 3 ? tp.SampleSet : map.SampleSet;
                int normalSet = n.NormalSet is > 0 and <= 3 ? n.NormalSet : tpSet;
                int additionSet = n.AdditionSet is > 0 and <= 3 ? n.AdditionSet : normalSet;
                int index = n.Index != 0 ? n.Index : tp?.SampleIndex ?? 0;

                var names = new List<(int Bank, string Name)>();
                if (n.HitSound == 0 || (n.HitSound & 1) != 0) names.Add((normalSet, "hitnormal"));
                if ((n.HitSound & 2) != 0) names.Add((additionSet, "hitwhistle"));
                if ((n.HitSound & 4) != 0) names.Add((additionSet, "hitfinish"));
                if ((n.HitSound & 8) != 0) names.Add((additionSet, "hitclap"));

                foreach (var (bank, name) in names)
                {
                    string baseName = $"{Banks[bank]}-{name}";
                    string? path = index >= 1 ? Find(files, baseName + (index >= 2 ? index.ToString() : "")) : null;
                    path ??= Find(skinFiles, baseName);
                    if (path != null) sounds.Add(new Sound(path, volume));
                }
            }

            if (sounds.Count > 0)
                hs._lanes[n.Column].Add(((float)(n.StartMs / 1000.0), sounds.ToArray()));
            else
                hs._lanes[n.Column].Add(((float)(n.StartMs / 1000.0), Array.Empty<Sound>()));
        }

        foreach (var lane in hs._lanes)
            foreach (var (_, sounds) in lane)
                foreach (var s in sounds)
                    hs._effects.TryAdd(s.Path, null);

        if (hs._effects.Count == 0) return null;
        Console.WriteLine($"[Osu] Hitsounds: {hs._effects.Count} sound file(s)");
        return hs;
    }

    private static Dictionary<string, string> IndexFolder(string folder)
    {
        var d = new Dictionary<string, string>(StringComparer.Ordinal);
        try
        {
            foreach (var f in Directory.EnumerateFiles(folder, "*.*", SearchOption.AllDirectories))
            {
                string ext = Path.GetExtension(f).ToLowerInvariant();
                if (ext is not (".wav" or ".ogg" or ".mp3")) continue;
                d.TryAdd(Path.GetRelativePath(folder, f).Replace('\\', '/').ToLowerInvariant(), f);
            }
        }
        catch { }
        return d;
    }

    /// <summary>Decodes the sound files (call once, off the game thread is fine).</summary>
    public void LoadSounds()
    {
        foreach (var path in _effects.Keys.ToList())
            _effects[path] = SoundFiles.Load(path);
    }

    /// <summary>Plays the sounds of the note a key press / hit on <paramref name="lane"/> belongs to.</summary>
    public void PlayForLane(int lane, float songTime)
    {
        if (!OsuSettings.Hitsounds || lane < 0 || lane >= _lanes.Length) return;
        var list = _lanes[lane];
        if (list.Count == 0) return;
        // The next note that isn't past the hit window, else the last one (osu!mania's key sounds).
        int lo = 0, hi = list.Count - 1, pick = list.Count - 1;
        float from = songTime - NoteRenderer.HIT_WINDOW;
        while (lo <= hi)
        {
            int mid = (lo + hi) / 2;
            if (list[mid].Time >= from) { pick = mid; hi = mid - 1; }
            else lo = mid + 1;
        }
        float gainBase = Math.Clamp(Game1.MasterVolume * OsuSettings.HitsoundVolume, 0f, 1f);
        foreach (var s in list[pick].Sounds)
        {
            if (_effects.TryGetValue(s.Path, out var fx) && fx != null)
            {
                try { fx.Play(Math.Clamp(gainBase * s.Volume, 0f, 1f), 0f, 0f); }
                catch (Exception ex) { Console.WriteLine($"[Osu] Hitsound failed: {ex.Message}"); }
            }
        }
    }

    public void Dispose()
    {
        foreach (var fx in _effects.Values) fx?.Dispose();
        _effects.Clear();
    }
}
