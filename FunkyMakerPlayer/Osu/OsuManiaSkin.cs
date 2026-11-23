using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using Microsoft.Xna.Framework.Graphics;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;
using SixLabors.ImageSharp.Processing;
using XnaColor = Microsoft.Xna.Framework.Color;
using XnaRectangle = Microsoft.Xna.Framework.Rectangle;

namespace FunkyMakerPlayer.Osu;

/// <summary>skin.ini: [General] Name / Author / Version and the [Mania] section for a key count.</summary>
public sealed class OsuSkinIni
{
    public string Name { get; private set; } = "";
    public string Author { get; private set; } = "";
    /// <summary>[General] Version; 1.0 when skin.ini has none (osu! LegacySkinDecoder), "latest" = 2.7.</summary>
    public decimal Version { get; private set; } = 1.0m;
    /// <summary>Keys: N → key/value pairs of that [Mania] section (the first section per key count wins, as in osu!).</summary>
    public Dictionary<int, Dictionary<string, string>> Mania { get; } = new();

    public static OsuSkinIni Parse(string text)
    {
        var ini = new OsuSkinIni();
        string section = "";
        Dictionary<string, string>? mania = null;
        var pending = new List<(string, string)>();
        foreach (var raw in text.Split('\n'))
        {
            string line = raw.Trim().TrimStart('﻿');
            if (line.Length == 0 || line.StartsWith("//", StringComparison.Ordinal)) continue;
            if (line.StartsWith('[') && line.EndsWith(']'))
            {
                section = line[1..^1].Trim();
                mania = null;
                pending.Clear();
                continue;
            }
            int c = line.IndexOf("//", StringComparison.Ordinal);
            if (c > 0) line = line[..c].TrimEnd();
            int i = line.IndexOf(':');
            if (i < 0) continue;
            string k = line[..i].Trim(), v = line[(i + 1)..].Trim();
            if (section.Equals("General", StringComparison.OrdinalIgnoreCase))
            {
                if (k == "Name") ini.Name = v;
                else if (k == "Author") ini.Author = v;
                else if (k == "Version")
                {
                    if (v.Equals("latest", StringComparison.OrdinalIgnoreCase)) ini.Version = 2.7m;
                    else if (decimal.TryParse(v, System.Globalization.NumberStyles.Number, System.Globalization.CultureInfo.InvariantCulture, out var ver)) ini.Version = ver;
                }
            }
            else if (section.Equals("Mania", StringComparison.OrdinalIgnoreCase))
            {
                if (k == "Keys" && int.TryParse(v, out int keys))
                {
                    if (ini.Mania.ContainsKey(keys)) { mania = null; continue; } // duplicates are ignored
                    mania = new Dictionary<string, string>(StringComparer.Ordinal);
                    foreach (var (pk, pv) in pending) mania[pk] = pv;
                    pending.Clear();
                    ini.Mania[keys] = mania;
                }
                else if (mania != null) mania[k] = v;
                else pending.Add((k, v));
            }
        }
        return ini;
    }

    /// <summary>True when the archive's skin.ini has a [Mania] section.</summary>
    public static bool HasManiaSection(ZipArchive zip, ZipArchiveEntry ini)
    {
        using var sr = new StreamReader(ini.Open());
        return Parse(sr.ReadToEnd()).Mania.Count > 0;
    }
}

/// <summary>
/// An osu!mania skin folder (from a .osk) turned into a <see cref="NoteStyle"/> (fork).
/// Follows osu!'s legacy mania lookups (ppy/osu, MIT: LegacyManiaSkinDecoder, LegacySkin,
/// LegacyManiaColumnElement, LegacyNotePiece, LegacyHoldNoteTailPiece):
/// <list type="bullet">
/// <item>[Mania] Keys: 4 → NoteImage{i}, NoteImage{i}H / L / T (i = 0..3, 0-based). Missing keys use
/// the defaults mania-note{1|2}{,H,L,T} with columns 1,2,2,1 (distance to the edge, even → 1, odd → 2).</item>
/// <item>name@2x is preferred over name; for animated elements (name-0, name-1…) frame 0 is used.</item>
/// <item>Tail falls back to head, then to the note.</item>
/// </list>
/// The player is FNF-style, so: every image is resized to one width (note width = lane width),
/// receptors are made from the note image (dim when idle, bright when hit) because mania key images
/// are the key area under the column, and the hold body / tail are packed into the 8-column hold
/// texture the atlas renderer uses, flipped to the FNF upscroll orientation (the renderer flips it
/// back for downscroll, which is osu!'s own orientation). Hit-lighting, stage art, key images, colours
/// and column widths are not used.
/// </summary>
public static class OsuManiaSkin
{
    private const int Width = 160;                 // every image is resized to this width
    private const float TargetLaneFill = 0.95f;    // note width as a fraction of the lane

    /// <summary>A folder that holds an osu! skin with mania notes (skin.ini or mania-note images).</summary>
    public static bool IsSkinFolder(string folder)
    {
        try
        {
            if (!Directory.Exists(folder)) return false;
            if (File.Exists(Path.Combine(folder, "skin.json"))) return false; // FNF / FMM style
            bool hasIni = Directory.GetFiles(folder, "skin.ini").Length > 0;
            bool hasManiaNote = Directory.EnumerateFiles(folder, "mania-note*.*", SearchOption.TopDirectoryOnly).Any();
            return hasManiaNote || (hasIni && Directory.GetFiles(folder, "*.png").Length > 0);
        }
        catch { return false; }
    }

    /// <param name="beatmapSkin">
    /// A beatmap set folder: like osu!'s LegacyBeatmapSkin only its images count (no skin.ini /
    /// [Mania] config, <c>AllowManiaConfigLookups =&gt; false</c>) and @2x images aren't preferred.
    /// </param>
    public static NoteStyle? Load(string folder, GraphicsDevice gd, bool beatmapSkin = false)
    {
        try
        {
            var files = Directory.EnumerateFiles(folder, "*.*", SearchOption.AllDirectories)
                .Where(f => f.EndsWith(".png", StringComparison.OrdinalIgnoreCase) || f.EndsWith(".jpg", StringComparison.OrdinalIgnoreCase))
                .GroupBy(f => Path.ChangeExtension(Path.GetRelativePath(folder, f), null)!.Replace('\\', '/').ToLowerInvariant())
                .ToDictionary(g => g.Key, g => g.First());

            string iniPath = Path.Combine(folder, "skin.ini");
            var ini = !beatmapSkin && File.Exists(iniPath) ? OsuSkinIni.Parse(File.ReadAllText(iniPath)) : new OsuSkinIni();
            ini.Mania.TryGetValue(4, out var cfg);
            cfg ??= new Dictionary<string, string>();

            string? Find(string name)
            {
                if (string.IsNullOrWhiteSpace(name)) return null;
                string key = name.Replace('\\', '/').Trim().ToLowerInvariant();
                foreach (var ext in new[] { ".png", ".jpg" })
                    if (key.EndsWith(ext)) key = key[..^ext.Length];
                var candidates = beatmapSkin ? new[] { key, key + "-0" } : new[] { key + "@2x", key, key + "-0@2x", key + "-0" };
                foreach (var candidate in candidates)
                    if (files.TryGetValue(candidate, out var f)) return f;
                return null;
            }

            string? Element(int lane, string suffix)
            {
                string typeIndex = Math.Min(lane, 3 - lane) % 2 == 0 ? "1" : "2"; // 4K: 1,2,2,1
                string? custom = cfg.TryGetValue($"NoteImage{lane}{suffix}", out var v) ? Find(v) : null;
                return custom ?? Find($"mania-note{typeIndex}{suffix}");
            }

            var style = new NoteStyle { StrumAlpha = 1f, HoldAlpha = 0.9f };
            int notesFound = 0;
            var noteImages = new Image<Rgba32>?[4];
            var bodyImages = new Image<Rgba32>?[4];
            var tailImages = new Image<Rgba32>?[4];
            try
            {
                for (int lane = 0; lane < 4; lane++)
                {
                    noteImages[lane] = LoadScaled(Element(lane, ""));
                    if (noteImages[lane] != null) notesFound++;
                    bodyImages[lane] = LoadScaled(Element(lane, "L"));
                    tailImages[lane] = LoadScaled(Element(lane, "T")) ?? LoadScaled(Element(lane, "H")) ?? (noteImages[lane]?.Clone());
                }
                if (notesFound == 0)
                {
                    Console.WriteLine($"[OsuSkin] {Path.GetFileName(folder)}: no mania note images");
                    return null;
                }

                // osu! skins are drawn for notes falling down; upscroll shows them flipped (osu! does the same).
                bool flip = !NoteRenderer.Downscroll;
                if (flip)
                    foreach (var img in noteImages)
                        img?.Mutate(x => x.Flip(FlipMode.Vertical));
                for (int lane = 0; lane < 4; lane++)
                {
                    var note = noteImages[lane] ?? noteImages.First(n => n != null)!;
                    style.NoteFrames[lane, 0] = Frame(gd, note);

                    // Receptors from the note: idle dim, press brighter, confirm full + white flash.
                    style.ReceptorFrames[lane, 0] = new List<AtlasFrame> { Frame(gd, Tinted(note, 0.55f, 0.40f)) };
                    style.ReceptorFrames[lane, 1] = new List<AtlasFrame> { Frame(gd, Tinted(note, 0.80f, 0.75f)) };
                    style.ReceptorFrames[lane, 2] = new List<AtlasFrame> { Frame(gd, Tinted(note, 1.25f, 1f)) };
                    style.ReceptorFrames[lane, 3] = style.ReceptorFrames[lane, 2];
                }

                style.HoldTexture = BuildHoldTexture(gd, bodyImages, tailImages, noteImages);
            }
            finally
            {
                foreach (var img in noteImages.Concat(bodyImages).Concat(tailImages)) img?.Dispose();
            }

            style.NoteScale = style.ReceptorScale = NoteRenderer.LANE_WIDTH * TargetLaneFill / Width;
            style.OsuSkinFolder = folder;
            style.OsuBuiltForDownscroll = NoteRenderer.Downscroll;
            if (!beatmapSkin) style.LoadMissSounds(folder);
            Console.WriteLine($"[OsuSkin] Loaded '{(ini.Name.Length > 0 ? ini.Name : Path.GetFileName(folder))}' ({notesFound}/4 note images, skin.ini v{ini.Version})");
            return style;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[OsuSkin] Failed to load {folder}: {ex.Message}");
            return null;
        }
    }

    /// <summary>Loads an image resized to <see cref="Width"/> (aspect kept), or null.</summary>
    private static Image<Rgba32>? LoadScaled(string? path)
    {
        if (path == null) return null;
        try
        {
            var img = Image.Load<Rgba32>(path);
            if (img.Width < 2 || img.Height < 1) { img.Dispose(); return null; }
            int h = Math.Max(1, (int)Math.Round(img.Height * (double)Width / img.Width));
            img.Mutate(x => x.Resize(Width, h));
            return img;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[OsuSkin] Unreadable image {Path.GetFileName(path)}: {ex.Message}");
            return null;
        }
    }

    private static Image<Rgba32> Tinted(Image<Rgba32> src, float brightness, float alpha)
    {
        var img = src.Clone();
        img.ProcessPixelRows(acc =>
        {
            for (int y = 0; y < acc.Height; y++)
            {
                var row = acc.GetRowSpan(y);
                for (int x = 0; x < row.Length; x++)
                {
                    var p = row[x];
                    row[x] = new Rgba32(
                        (byte)Math.Min(255, p.R * brightness),
                        (byte)Math.Min(255, p.G * brightness),
                        (byte)Math.Min(255, p.B * brightness),
                        (byte)(p.A * alpha));
                }
            }
        });
        return img;
    }

    private static AtlasFrame Frame(GraphicsDevice gd, Image<Rgba32> img)
    {
        var tex = ToTexture(gd, img);
        return new AtlasFrame { Texture = tex, Source = new XnaRectangle(0, 0, tex.Width, tex.Height), FrameW = tex.Width, FrameH = tex.Height };
    }

    private static Texture2D ToTexture(GraphicsDevice gd, Image<Rgba32> img)
    {
        var pixels = new XnaColor[img.Width * img.Height];
        img.ProcessPixelRows(acc =>
        {
            for (int y = 0; y < acc.Height; y++)
            {
                var row = acc.GetRowSpan(y);
                for (int x = 0; x < row.Length; x++)
                    pixels[y * img.Width + x] = XnaColor.FromNonPremultiplied(row[x].R, row[x].G, row[x].B, row[x].A);
            }
        });
        var tex = new Texture2D(gd, img.Width, img.Height);
        tex.SetData(pixels);
        return tex;
    }

    /// <summary>
    /// 8 columns (body, cap) × 4 lanes, the layout the atlas hold renderer expects. Body = L image
    /// (stretched along the hold), cap = T image. Both flipped vertically: osu! art is drawn for notes
    /// falling down, the FNF hold texture for notes going up.
    /// </summary>
    private static Texture2D? BuildHoldTexture(GraphicsDevice gd, Image<Rgba32>?[] bodies, Image<Rgba32>?[] tails, Image<Rgba32>?[] notes)
    {
        int capH = Math.Max(8, tails.Where(t => t != null).Select(t => t!.Height).DefaultIfEmpty(Width / 2).Max());
        int texH = (int)Math.Ceiling(capH / 0.9) + 2; // the renderer only shows the top 90 % of a cap column
        using var sheet = new Image<Rgba32>(Width * 8, texH);
        for (int lane = 0; lane < 4; lane++)
        {
            var body = bodies[lane];
            if (body == null)
            {
                // No body image: a strip of the note's centre colour, slightly transparent.
                var note = notes[lane] ?? notes.FirstOrDefault(n => n != null);
                var c = note != null ? note[note.Width / 2, note.Height / 2] : new Rgba32(220, 220, 220, 255);
                body = new Image<Rgba32>(Width, 4, new Rgba32(c.R, c.G, c.B, (byte)(c.A * 0.8f)));
                bodies[lane] = body;
            }
            using (var b = body.Clone(x => x.Resize(Width, texH).Flip(FlipMode.Vertical)))
                sheet.Mutate(x => x.DrawImage(b, new Point(lane * 2 * Width, 0), 1f));
            // Tail: osu! draws it flipped for falling notes; the renderer flips this column for
            // downscroll, so it is stored as the file is (upscroll then shows it unflipped, like osu!).
            if (tails[lane] is { } tail)
                sheet.Mutate(x => x.DrawImage(tail, new Point((lane * 2 + 1) * Width, 0), 1f));
        }
        return ToTexture(gd, sheet);
    }
}
