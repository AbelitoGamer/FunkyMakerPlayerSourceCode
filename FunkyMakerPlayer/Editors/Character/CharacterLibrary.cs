using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Formats.Png;
using SixLabors.ImageSharp.PixelFormats;
using SixLabors.ImageSharp.Processing;

namespace FunkyMakerPlayer.Editors.Character;

/// <summary>
/// Manages custom characters stored in <c>mods/characters/&lt;slug&gt;/</c>, converts any supported input
/// (Sparrow XML+PNG, Adobe Animate Spritemap folder, .ZIP archive, or .FNMM sprite), bakes FunkyMaker
/// horizontal strips (<c>sprite.json + 0.png..N.png + icon/</c>), and applies characters to <see cref="FnmmLevel"/>
/// scenes for both the Chart Editor and Online Local Character Equip.
/// </summary>
public static class CharacterLibrary
{
    private static readonly JsonSerializerOptions _json = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    private static readonly PngEncoder _pngEncoder = new()
    {
        CompressionLevel = PngCompressionLevel.DefaultCompression,
        ColorType = PngColorType.RgbWithAlpha,
    };

    public sealed class BakedCharacterPackage
    {
        public CharacterMetadata Metadata { get; set; } = new();
        public FnmmSprite Sprite { get; set; } = new();
        public List<byte[]> Frames { get; set; } = new();
        public FnmmSprite? IconSprite { get; set; }
        public List<byte[]>? IconFrames { get; set; }
    }

    public static string SanitizeSlug(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return "custom_char";
        var sb = new StringBuilder(raw.Length);
        foreach (char c in raw.Trim().ToLowerInvariant())
        {
            if ((c >= 'a' && c <= 'z') || (c >= '0' && c <= '9'))
                sb.Append(c);
            else if (c == '_' || c == '-' || c == ' ')
            {
                if (sb.Length > 0 && sb[^1] != '_')
                    sb.Append('_');
            }
        }
        string res = sb.ToString().Trim('_');
        return string.IsNullOrEmpty(res) ? "custom_char" : (res.Length > 40 ? res.Substring(0, 40) : res);
    }

    /// <summary>
    /// Scans <c>mods/characters/</c> for both saved FunkyMaker character packages and raw unbaked source folders.
    /// </summary>
    public static List<CharacterSummary> ScanCharacters(bool bakedOnly = false)
    {
        var list = new List<CharacterSummary>();
        string root = PlatformPaths.GetCharactersPath();
        if (!Directory.Exists(root)) return list;

        foreach (var dir in Directory.EnumerateDirectories(root).OrderBy(d => d, StringComparer.OrdinalIgnoreCase))
        {
            string folderName = Path.GetFileName(dir);
            if (string.IsNullOrWhiteSpace(folderName) || folderName.StartsWith("."))
                continue;

            string sprJsonPath = Path.Combine(dir, "sprite.json");
            string metaPath = Path.Combine(dir, "character.json");
            string frame0Path = Path.Combine(dir, "0.png");

            if (File.Exists(sprJsonPath) && File.Exists(frame0Path))
            {
                CharacterMetadata? meta = TryReadMetadata(metaPath);
                bool hasIcon = Directory.Exists(Path.Combine(dir, "icon"))
                            && File.Exists(Path.Combine(dir, "icon", "sprite.json"))
                            && File.Exists(Path.Combine(dir, "icon", "0.png"));

                int idleH = meta?.IdleHeight ?? 0;
                if (idleH <= 0 && TryReadPngDimensions(frame0Path, out _, out int h0))
                    idleH = h0;

                list.Add(new CharacterSummary
                {
                    Slug = folderName,
                    Name = !string.IsNullOrWhiteSpace(meta?.Name) ? meta!.Name : SparrowCharacterImporter.CleanDisplayName(folderName),
                    DirectoryPath = dir,
                    IsBakedFnmm = true,
                    IsRawSourceFolder = false,
                    HasIcon = hasIcon,
                    NativeFacing = ParseFacing(meta?.NativeFacing),
                    Scale = meta != null && meta.Scale > 0.05f ? meta.Scale : 1.0f,
                    IdleHeight = idleH,
                });
            }
            else if (!bakedOnly)
            {
                // Check if user dropped a raw Sparrow or Spritemap folder directly into mods/characters/<folder>/
                if (SpritemapCharacterImporter.IsSpritemapFolder(dir))
                {
                    list.Add(new CharacterSummary
                    {
                        Slug = folderName,
                        Name = SparrowCharacterImporter.CleanDisplayName(folderName),
                        DirectoryPath = dir,
                        IsBakedFnmm = false,
                        IsRawSourceFolder = true,
                        RawSourceHint = "SPRITEMAP (RAW)",
                    });
                }
                else if (HasFileWithExtension(dir, ".xml") && HasFileWithExtension(dir, ".png"))
                {
                    list.Add(new CharacterSummary
                    {
                        Slug = folderName,
                        Name = SparrowCharacterImporter.CleanDisplayName(folderName),
                        DirectoryPath = dir,
                        IsBakedFnmm = false,
                        IsRawSourceFolder = true,
                        RawSourceHint = "XML+PNG (RAW)",
                    });
                }
            }
        }

        return list;
    }

    /// <summary>
    /// Universal smart importer: accepts a .xml, .png, Animation.json, spritemap*.json, Psych/V-Slice .json,
    /// .zip character pack, .fnmm level, or folder (baked FNMM or raw Sparrow/Spritemap)!
    /// </summary>
    public static CharacterProject ImportAny(string path, string? fnmmSpriteChoice = null)
    {
        if (Directory.Exists(path))
        {
            if (File.Exists(Path.Combine(path, "sprite.json")) && File.Exists(Path.Combine(path, "0.png")))
                return ImportFromBakedFolder(path);
            if (SpritemapCharacterImporter.IsSpritemapFolder(path))
                return SpritemapCharacterImporter.ImportFromPath(path);

            var xml = FindFirstFileWithExtension(path, ".xml");
            if (xml != null)
                return SparrowCharacterImporter.ImportFromFiles(xml);

            throw new InvalidOperationException("Folder does not contain a recognizable character (XML+PNG, Animation.json, or sprite.json).");
        }

        if (!File.Exists(path))
            throw new FileNotFoundException("Selected file does not exist.", path);

        string ext = Path.GetExtension(path).ToLowerInvariant();
        string fileName = Path.GetFileName(path);

        if (ext == ".fnmm")
            return ImportFromFnmmLevel(path, fnmmSpriteChoice);

        if (ext == ".zip")
            return ImportFromZipArchive(path);

        if (string.Equals(fileName, "Animation.json", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(fileName, "animations.json", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(fileName, "metadata.json", StringComparison.OrdinalIgnoreCase) ||
            fileName.StartsWith("spritemap", StringComparison.OrdinalIgnoreCase))
        {
            return SpritemapCharacterImporter.ImportFromPath(path);
        }

        if (string.Equals(fileName, "sprite.json", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(fileName, "character.json", StringComparison.OrdinalIgnoreCase))
        {
            return ImportFromBakedFolder(Path.GetDirectoryName(path)!);
        }

        // Check if any .json inside a Spritemap folder or its LIBRARY/ subfolder was picked
        if (ext == ".json")
        {
            string? atlasRoot = SpritemapCharacterImporter.FindSpritemapRoot(path);
            if (atlasRoot != null)
                return SpritemapCharacterImporter.ImportFromPath(atlasRoot);
        }

        return SparrowCharacterImporter.ImportFromFiles(path);
    }

    public static CharacterProject ImportFromZipArchive(string zipPath)
    {
        // First check if this ZIP is actually an .fnmm level (has data.json + scene.json)
        using (var probe = ZipFile.OpenRead(zipPath))
        {
            bool hasData = probe.Entries.Any(e => string.Equals(e.FullName.Replace('\\', '/'), "data.json", StringComparison.OrdinalIgnoreCase));
            bool hasSpritesDir = probe.Entries.Any(e => e.FullName.Replace('\\', '/').StartsWith("sprites/", StringComparison.OrdinalIgnoreCase));
            if (hasData && hasSpritesDir)
                return ImportFromFnmmLevel(zipPath);
        }

        string tempDir = Path.Combine(Path.GetTempPath(), "fmp_char_zip_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);
        try
        {
            ZipFile.ExtractToDirectory(zipPath, tempDir, overwriteFiles: true);
            string bestDir = FindBestCharacterSubdirectory(tempDir);
            var proj = ImportAny(bestDir);
            if (proj.Slug == "custom_char" || proj.Slug.StartsWith("fmp_char_zip"))
            {
                string zipName = Path.GetFileNameWithoutExtension(zipPath);
                proj.Name = SparrowCharacterImporter.CleanDisplayName(zipName);
                proj.Slug = SanitizeSlug(zipName);
            }
            return proj;
        }
        finally
        {
            try { Directory.Delete(tempDir, recursive: true); } catch { }
        }
    }

    private static bool HasFileWithExtension(string dir, string ext) =>
        FindFirstFileWithExtension(dir, ext) != null;

    private static string? FindFirstFileWithExtension(string dir, string ext)
    {
        if (!Directory.Exists(dir)) return null;
        return Directory.EnumerateFiles(dir, "*", SearchOption.TopDirectoryOnly)
            .FirstOrDefault(f => string.Equals(Path.GetExtension(f), ext, StringComparison.OrdinalIgnoreCase));
    }

    private static string FindBestCharacterSubdirectory(string rootDir)
    {
        if (SpritemapCharacterImporter.IsSpritemapFolder(rootDir) ||
            File.Exists(Path.Combine(rootDir, "sprite.json")) ||
            HasFileWithExtension(rootDir, ".xml"))
        {
            return rootDir;
        }

        foreach (var sub in Directory.EnumerateDirectories(rootDir, "*", SearchOption.AllDirectories))
        {
            if (SpritemapCharacterImporter.IsSpritemapFolder(sub) ||
                File.Exists(Path.Combine(sub, "sprite.json")) ||
                HasFileWithExtension(sub, ".xml"))
            {
                return sub;
            }
        }

        return rootDir;
    }

    public static CharacterProject ImportFromBakedFolder(string dir)
    {
        string sprJson = Path.Combine(dir, "sprite.json");
        var def = JsonSerializer.Deserialize<FnmmSprite>(File.ReadAllText(sprJson))
            ?? throw new InvalidOperationException("Invalid sprite.json");
        var meta = TryReadMetadata(Path.Combine(dir, "character.json"));

        var frames = new List<byte[]>();
        for (int i = 0; ; i++)
        {
            string fp = Path.Combine(dir, $"{i}.png");
            if (!File.Exists(fp)) break;
            frames.Add(File.ReadAllBytes(fp));
        }

        string folderSlug = Path.GetFileName(dir.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
        var proj = BuildProjectFromFnmmSprite(
            meta?.Name ?? SparrowCharacterImporter.CleanDisplayName(folderSlug),
            folderSlug,
            def,
            frames,
            meta);

        string iconDir = Path.Combine(dir, "icon");
        string icon0 = Path.Combine(iconDir, "0.png");
        string icon1 = Path.Combine(iconDir, "1.png");
        if (File.Exists(icon0))
        {
            proj.IconNormal = Image.Load<Rgba32>(icon0);
            if (File.Exists(icon1))
                proj.IconLosing = Image.Load<Rgba32>(icon1);
        }

        return proj;
    }

    public static CharacterProject ImportFromFnmmLevel(string fnmmPath, string? preferredSprite = null)
    {
        var level = FnmmLoader.Load(fnmmPath);
        // Pick character sprites (multi-anim sprites used by scene objects, or sprites with >= 2 anims)
        string? chosenName = preferredSprite;
        if (string.IsNullOrEmpty(chosenName) || !level.Sprites.ContainsKey(chosenName))
        {
            var charObj = level.Scene.Objects.FirstOrDefault(o =>
                !string.IsNullOrEmpty(o.Spr) &&
                level.Sprites.TryGetValue(o.Spr, out var sp) &&
                sp.Def.Data.Count >= 2);

            chosenName = charObj?.Spr
                ?? level.Sprites.FirstOrDefault(kvp => kvp.Value.Def.Data.Count >= 2).Key
                ?? level.Sprites.Keys.FirstOrDefault();
        }

        if (string.IsNullOrEmpty(chosenName) || !level.Sprites.TryGetValue(chosenName, out var entry))
            throw new InvalidOperationException("This .FNMM level contains no sprites to import.");

        var proj = BuildProjectFromFnmmSprite(
            SparrowCharacterImporter.CleanDisplayName(chosenName),
            SanitizeSlug(chosenName),
            entry.Def,
            entry.Frames,
            meta: null);

        // Also grab matching health icon if this sprite was BF (strumLine 0 -> icons[1]) or Opponent (strumLine 1 -> icons[0])
        var matchingObj = level.Scene.Objects.FirstOrDefault(o => string.Equals(o.Spr, chosenName, StringComparison.OrdinalIgnoreCase));
        if (matchingObj != null)
        {
            int sl = (int)matchingObj.StrumLine;
            proj.NativeFacing = sl == 1 ? CharacterFacing.Right : CharacterFacing.Left;
            string? iconName = sl == 0 && level.Scene.Icons.Count > 1 ? level.Scene.Icons[1]
                             : sl == 1 && level.Scene.Icons.Count > 0 ? level.Scene.Icons[0]
                             : null;
            if (!string.IsNullOrEmpty(iconName) && level.Sprites.TryGetValue(iconName, out var iconEntry) && iconEntry.Frames.Count > 0)
            {
                if (iconEntry.Frames[0].Length > 0)
                    proj.IconNormal = Image.Load<Rgba32>(iconEntry.Frames[0]);
                if (iconEntry.Frames.Count > 1 && iconEntry.Frames[1].Length > 0)
                    proj.IconLosing = Image.Load<Rgba32>(iconEntry.Frames[1]);
            }
        }

        return proj;
    }

    private static CharacterProject BuildProjectFromFnmmSprite(
        string displayName,
        string slug,
        FnmmSprite def,
        List<byte[]> stripPngs,
        CharacterMetadata? meta)
    {
        var proj = new CharacterProject
        {
            Name = displayName,
            Slug = SanitizeSlug(slug),
            SourceKind = CharacterSourceKind.FnmmSprite,
            Scale = meta != null && meta.Scale > 0.05f ? meta.Scale : (def.Data.Count > 0 && def.Data[0].Scale > 0.05f ? def.Data[0].Scale : 1f),
            Bilinear = meta?.Bilinear ?? (def.Data.Count == 0 || def.Data[0].Bilinear),
            BakeFlipX = false, // Already baked if reloading from sprite.json
            NativeFacing = ParseFacing(meta?.NativeFacing),
        };

        int count = Math.Min(def.Data.Count, stripPngs.Count);
        var usedClipNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var savedOffsets = new List<Vec2>(count);
        for (int i = 0; i < count; i++)
        {
            if (stripPngs[i] == null || stripPngs[i].Length == 0) continue;
            var animData = def.Data[i];
            int fc = Math.Max(1, animData.FrameCount);

            using var strip = Image.Load<Rgba32>(stripPngs[i]);
            int fw = Math.Max(1, strip.Width / fc);
            int fh = Math.Max(1, strip.Height);

            var slotMeta = (meta != null && i < meta.Slots.Count) ? meta.Slots[i] : null;
            string slotLabel = i < CharacterProject.CoreSlotNames.Length
                ? CharacterProject.CoreSlotNames[i]
                : (slotMeta != null && !string.IsNullOrWhiteSpace(slotMeta.Label)
                    ? slotMeta.Label
                    : $"EXTRA {i - 4}");

            string baseClipName = slotMeta != null && !string.IsNullOrWhiteSpace(slotMeta.ClipName)
                ? slotMeta.ClipName.Trim()
                : $"{i}: {slotLabel}";
            string clipName = baseClipName;
            int suffix = 2;
            while (!usedClipNames.Add(clipName))
                clipName = $"{baseClipName} ({suffix++})";

            float uox = slotMeta?.UserOffsetX ?? 0f;
            float uoy = slotMeta?.UserOffsetY ?? 0f;

            var clip = new ImportedAnimClip
            {
                Name = clipName,
                CanvasWidth = fw,
                CanvasHeight = fh,
                // Place WorldMin so that with Idle world anchor = (0, 0), ComputeFnmmOffset reproduces animData.Offset exactly!
                WorldMinX = -(animData.Offset.X + uox),
                WorldMinY = -(animData.Offset.Y + uoy),
                SuggestedFps = animData.SpdFps && animData.Spd > 0 ? animData.Spd : 24f,
                SuggestedLoop = animData.Loop,
            };

            for (int f = 0; f < fc; f++)
            {
                int sx = Math.Clamp(f * fw, 0, Math.Max(0, strip.Width - 1));
                int sw = Math.Clamp(fw, 1, strip.Width - sx);
                clip.Frames.Add(strip.Clone(ctx => ctx.Crop(new Rectangle(sx, 0, sw, fh))));
            }

            proj.AvailableClips.Add(clip);
            savedOffsets.Add(animData.Offset);
            proj.Slots.Add(new CharacterAnimSlot
            {
                SlotIndex = i,
                Label = slotLabel,
                ClipName = clipName,
                Fps = clip.SuggestedFps,
                Loop = animData.Loop,
                UserOffsetX = uox,
                UserOffsetY = uoy,
            });
        }

        // Normalize Idle's WorldMin so GetIdleWorldFootAnchor() sits at (0, 0), preserving exact
        // saved Idle offset in Slot 0's UserOffsetX/Y AND exact saved offsets for every other slot!
        if (proj.AvailableClips.Count > 0 && proj.Slots.Count > 0)
        {
            var idle = proj.AvailableClips[0];
            idle.WorldMinX = -idle.CanvasWidth * 0.5f;
            idle.WorldMinY = -idle.CanvasHeight;
            proj.Slots[0].UserOffsetX = MathF.Round(idle.CanvasWidth * 0.5f - savedOffsets[0].X, 2);
            proj.Slots[0].UserOffsetY = MathF.Round(idle.CanvasHeight - savedOffsets[0].Y, 2);
        }

        proj.EnsureCoreSlots();
        for (int i = 0; i < proj.Slots.Count; i++)
        {
            if (string.IsNullOrEmpty(proj.Slots[i].ClipName) && proj.AvailableClips.Count > 0)
                proj.Slots[i].ClipName = proj.AvailableClips[0].Name;
        }

        return proj;
    }

    /// <summary>
    /// Loads a health icon PNG into the project. Automatically detects standard FNF 2-frame horizontal
    /// icon strips (e.g. 300x150 = [normal | losing]) and splits them into IconNormal + IconLosing!
    /// </summary>
    public static void LoadIconFromPng(CharacterProject project, string pngPath, bool asLosingOnly = false)
    {
        using var src = Image.Load<Rgba32>(pngPath);
        if (asLosingOnly)
        {
            project.IconLosing?.Dispose();
            project.IconLosing = src.Clone();
            return;
        }

        project.IconNormal?.Dispose();
        project.IconLosing?.Dispose();
        project.IconNormal = null;
        project.IconLosing = null;

        if (src.Width >= (int)(src.Height * 1.75f) && src.Width >= 64)
        {
            int halfW = src.Width / 2;
            project.IconNormal = src.Clone(ctx => ctx.Crop(new Rectangle(0, 0, halfW, src.Height)));
            project.IconLosing = src.Clone(ctx => ctx.Crop(new Rectangle(halfW, 0, src.Width - halfW, src.Height)));
        }
        else
        {
            project.IconNormal = src.Clone();
        }
    }

    /// <summary>
    /// Bakes a <see cref="CharacterProject"/> into a FunkyMaker-compatible character package on disk
    /// (<c>mods/characters/&lt;slug&gt;/</c>).
    /// </summary>
    public static string BakeAndSave(CharacterProject project, string? targetDirectoryOverride = null)
    {
        project.EnsureCoreSlots();
        string slug = SanitizeSlug(!string.IsNullOrWhiteSpace(project.Slug) ? project.Slug : project.Name);
        project.Slug = slug;

        string outDir = !string.IsNullOrWhiteSpace(targetDirectoryOverride)
            ? targetDirectoryOverride!
            : Path.Combine(PlatformPaths.GetCharactersPath(), slug);

        // Bake in memory FIRST so a baking failure (e.g. OOM) never deletes an existing saved character on disk!
        var baked = BakeInMemory(project);

        if (Directory.Exists(outDir))
        {
            foreach (var oldPng in Directory.EnumerateFiles(outDir, "*", SearchOption.TopDirectoryOnly))
            {
                if (string.Equals(Path.GetExtension(oldPng), ".png", StringComparison.OrdinalIgnoreCase) &&
                    int.TryParse(Path.GetFileNameWithoutExtension(oldPng), out _))
                    try { File.Delete(oldPng); } catch { }
            }
        }
        else
        {
            Directory.CreateDirectory(outDir);
        }

        File.WriteAllText(Path.Combine(outDir, "sprite.json"), JsonSerializer.Serialize(baked.Sprite, _json), Encoding.UTF8);
        for (int i = 0; i < baked.Frames.Count; i++)
            File.WriteAllBytes(Path.Combine(outDir, $"{i}.png"), baked.Frames[i]);

        string iconDir = Path.Combine(outDir, "icon");
        if (baked.IconSprite != null && baked.IconFrames != null && baked.IconFrames.Count > 0)
        {
            Directory.CreateDirectory(iconDir);
            File.WriteAllText(Path.Combine(iconDir, "sprite.json"), JsonSerializer.Serialize(baked.IconSprite, _json), Encoding.UTF8);
            for (int i = 0; i < baked.IconFrames.Count; i++)
                File.WriteAllBytes(Path.Combine(iconDir, $"{i}.png"), baked.IconFrames[i]);
        }
        else if (Directory.Exists(iconDir))
        {
            try { Directory.Delete(iconDir, recursive: true); } catch { }
        }

        File.WriteAllText(Path.Combine(outDir, "character.json"), JsonSerializer.Serialize(baked.Metadata, _json), Encoding.UTF8);
        Console.WriteLine($"[CharacterLibrary] Saved character '{project.Name}' ({slug}) -> {outDir}");
        return outDir;
    }

    public static BakedCharacterPackage BakeInMemory(CharacterProject project)
    {
        project.EnsureCoreSlots();
        var fallbackClip = project.FindClip(project.Slots[0].ClipName)
            ?? (project.AvailableClips.Count > 0 ? project.AvailableClips[0] : throw new InvalidOperationException("No clips available to bake."));

        var sprDef = new FnmmSprite { Type = 1f };
        var strips = new List<byte[]>(project.Slots.Count);
        var slotMetas = new List<CharacterSlotMeta>(project.Slots.Count);

        int idleW = fallbackClip.CanvasWidth;
        int idleH = fallbackClip.CanvasHeight;

        for (int i = 0; i < project.Slots.Count; i++)
        {
            var slot = project.Slots[i];
            var clip = project.FindClip(slot.ClipName) ?? fallbackClip;
            int fc = Math.Max(1, clip.Frames.Count);
            int fw = Math.Max(1, clip.CanvasWidth);
            int fh = Math.Max(1, clip.CanvasHeight);
            if (i == 0) { idleW = fw; idleH = fh; }

            // Build horizontal strip (fw * fc) x fh with overflow/32-bit OOM guard
            long stripPixels = (long)fw * fc * fh;
            long maxStripPixels = OperatingSystem.IsAndroid() && !Environment.Is64BitProcess
                ? 24L * 1024 * 1024 // ~96 MB RGBA max single strip on 32-bit Android
                : 128L * 1024 * 1024;
            if ((long)fw * fc > int.MaxValue / 4 || stripPixels > maxStripPixels)
            {
                throw new InvalidOperationException(
                    $"Animation '{slot.Label}' ({fc} frames of {fw}x{fh}) exceeds safe strip memory limit ({stripPixels * 4 / (1024 * 1024)} MB).");
            }
            using var stripImg = new Image<Rgba32>(fw * fc, fh);
            for (int f = 0; f < fc; f++)
            {
                var frameSrc = clip.Frames[f];
                if (project.BakeFlipX)
                {
                    using var flipped = frameSrc.Clone(ctx => ctx.Flip(FlipMode.Horizontal));
                    CopySubImage(flipped, stripImg, f * fw, 0);
                }
                else
                {
                    CopySubImage(frameSrc, stripImg, f * fw, 0);
                }
            }

            using var ms = new MemoryStream();
            stripImg.Save(ms, _pngEncoder);
            strips.Add(ms.ToArray());

            var fnmmOffset = project.ComputeFnmmOffset(slot, clip);
            sprDef.Data.Add(new SpriteAnimData
            {
                Bilinear = project.Bilinear,
                Loop = slot.Loop,
                Num = fc,
                Spd = Math.Clamp(slot.Fps, 1f, 120f),
                SpdFps = true,
                Offset = fnmmOffset,
                Scale = Math.Clamp(project.Scale, 0.05f, 10f),
            });

            slotMetas.Add(new CharacterSlotMeta
            {
                Index = i,
                Label = slot.Label,
                ClipName = slot.ClipName,
                Fps = slot.Fps,
                Loop = slot.Loop,
                UserOffsetX = slot.UserOffsetX,
                UserOffsetY = slot.UserOffsetY,
            });
        }

        FnmmSprite? iconDef = null;
        List<byte[]>? iconBytes = null;
        if (project.IconNormal != null)
        {
            iconDef = new FnmmSprite { Type = 3f };
            iconBytes = new List<byte[]>(2);

            byte[] normPng = EncodeIconFrame(project.IconNormal, project.IconFlipX);
            byte[] losePng = project.IconLosing != null
                ? EncodeIconFrame(project.IconLosing, project.IconFlipX)
                : normPng;

            iconBytes.Add(normPng);
            iconBytes.Add(losePng);

            for (int k = 0; k < 2; k++)
            {
                iconDef.Data.Add(new SpriteAnimData
                {
                    Bilinear = project.Bilinear,
                    Loop = false,
                    Num = 1,
                    Spd = 1f,
                    SpdFps = true,
                    Offset = new Vec2 { X = 0, Y = 0 },
                    Scale = 1f,
                });
            }
        }

        var meta = new CharacterMetadata
        {
            Name = project.Name,
            Slug = project.Slug,
            SourceKind = project.SourceKind switch
            {
                CharacterSourceKind.AnimateSpritemap => "spritemap",
                CharacterSourceKind.FnmmSprite => "fnmm",
                _ => "sparrow",
            },
            Scale = project.Scale,
            Bilinear = project.Bilinear,
            // Once baked into 0.png..N.png, BakeFlipX is already applied to the pixels on disk!
            BakeFlipX = false,
            NativeFacing = project.NativeFacing == CharacterFacing.Right ? "right" : "left",
            HasIcon = iconDef != null,
            IdleWidth = idleW,
            IdleHeight = idleH,
            Slots = slotMetas,
        };

        return new BakedCharacterPackage
        {
            Metadata = meta,
            Sprite = sprDef,
            Frames = strips,
            IconSprite = iconDef,
            IconFrames = iconBytes,
        };
    }

    private static byte[] EncodeIconFrame(Image<Rgba32> img, bool flipH)
    {
        using var clone = img.Clone(ctx =>
        {
            if (flipH)
                ctx.Flip(FlipMode.Horizontal);
            if (img.Width > 160 || img.Height > 160)
                ctx.Resize(new ResizeOptions { Size = new Size(150, 150), Mode = ResizeMode.Max });
        });
        using var ms = new MemoryStream();
        clone.Save(ms, _pngEncoder);
        return ms.ToArray();
    }

    /// <summary>
    /// Loads a character from <c>mods/characters/&lt;slug&gt;/</c> ready for scene injection.
    /// If the folder is an unbaked raw Sparrow/Spritemap folder, bakes it automatically on first use!
    /// </summary>
    public static BakedCharacterPackage? LoadPackage(string slug)
    {
        if (string.IsNullOrWhiteSpace(slug)) return null;
        string root = PlatformPaths.GetCharactersPath();
        string dir = Path.Combine(root, slug);
        if (!Directory.Exists(dir))
        {
            string san = SanitizeSlug(slug);
            dir = Path.Combine(root, san);
            if (!Directory.Exists(dir))
                return null;
        }

        string sprJsonPath = Path.Combine(dir, "sprite.json");
        string frame0Path = Path.Combine(dir, "0.png");

        if (!File.Exists(sprJsonPath) || !File.Exists(frame0Path))
        {
            // Auto-bake raw folder in-place on first use!
            try
            {
                using var imported = ImportAny(dir);
                BakeAndSave(imported, targetDirectoryOverride: dir);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[CharacterLibrary] Failed to auto-bake '{slug}': {ex.Message}");
                return null;
            }
        }

        try
        {
            var def = JsonSerializer.Deserialize<FnmmSprite>(File.ReadAllText(sprJsonPath))
                ?? throw new InvalidOperationException("Invalid sprite.json");
            var meta = TryReadMetadata(Path.Combine(dir, "character.json")) ?? new CharacterMetadata
            {
                Slug = slug,
                Name = SparrowCharacterImporter.CleanDisplayName(slug),
            };

            var frames = new List<byte[]>();
            for (int i = 0; ; i++)
            {
                string fp = Path.Combine(dir, $"{i}.png");
                if (!File.Exists(fp)) break;
                frames.Add(File.ReadAllBytes(fp));
            }
            if (frames.Count == 0) return null;

            if (meta.IdleHeight <= 0 && TryReadPngDimensions(frame0Path, out int w0, out int h0))
            {
                int fc0 = def.Data.Count > 0 ? Math.Max(1, def.Data[0].FrameCount) : 1;
                meta.IdleWidth = Math.Max(1, w0 / fc0);
                meta.IdleHeight = h0;
            }

            FnmmSprite? iconSpr = null;
            List<byte[]>? iconFrames = null;
            string iconDir = Path.Combine(dir, "icon");
            string iconJson = Path.Combine(iconDir, "sprite.json");
            string icon0 = Path.Combine(iconDir, "0.png");
            if (File.Exists(iconJson) && File.Exists(icon0))
            {
                iconSpr = JsonSerializer.Deserialize<FnmmSprite>(File.ReadAllText(iconJson));
                iconFrames = new List<byte[]> { File.ReadAllBytes(icon0) };
                string icon1 = Path.Combine(iconDir, "1.png");
                iconFrames.Add(File.Exists(icon1) ? File.ReadAllBytes(icon1) : iconFrames[0]);
            }

            return new BakedCharacterPackage
            {
                Metadata = meta,
                Sprite = def,
                Frames = frames,
                IconSprite = iconSpr,
                IconFrames = iconFrames,
            };
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[CharacterLibrary] Failed to load package '{slug}': {ex.Message}");
            return null;
        }
    }

    /// <summary>
    /// Applies a character package onto <paramref name="targetStrumline"/> of <paramref name="level"/>.
    /// Preserves foot anchor alignment with any replaced character, ensures valid stage cameras on blank charts,
    /// mirrors &amp; swaps Left/Right poses automatically when placed on the opposite stage side, and updates health icons.
    /// </summary>
    public static SceneObject? ApplyCharacterToLevel(
        FnmmLevel level,
        string slug,
        int targetStrumline,
        Vec2? posOverride = null,
        float? scaleOverride = null,
        bool updateIcon = true,
        bool autoScaleToReplaced = false,
        string? spriteKeyOverride = null,
        BakedCharacterPackage? preloadedPackage = null)
    {
        var pkg = preloadedPackage ?? LoadPackage(slug);
        if (pkg == null) return null;

        // Ensure strumline exists in chart data
        while (level.Data.Strumlines.Count <= targetStrumline)
            level.Data.Strumlines.Add(new List<List<FnmmNote>>());
        while (level.Data.Strumlines[targetStrumline].Count < 4)
            level.Data.Strumlines[targetStrumline].Add(new List<FnmmNote>());

        // Ensure default landscape camera if level had a completely empty scene (e.g. New Chart)
        if (level.Scene.Objects.Count == 0 && level.Scene.Cameras.Count == 0)
        {
            level.Scene.Landscape = true;
            level.Scene.Cameras.Add(new SceneCamera { X = 640f, Y = 360f, Scale = 1.0f });
        }

        var existingObjs = level.Scene.Objects
            .Where(o => (int)o.StrumLine == targetStrumline)
            .ToList();
        var primaryExisting = existingObjs.FirstOrDefault(o => o.Visible) ?? existingObjs.FirstOrDefault();

        // Determine desired facing direction on this stage:
        // In FNF: Strumline 0 (BF, right side) faces Left; Strumline 1 (Opponent, left side) faces Right.
        CharacterFacing desiredFacing = targetStrumline == 1 ? CharacterFacing.Right : CharacterFacing.Left;
        if (primaryExisting != null)
        {
            var otherSide = level.Scene.Objects.FirstOrDefault(o => (int)o.StrumLine == (targetStrumline == 0 ? 1 : 0));
            if (otherSide != null)
            {
                float myX = primaryExisting.GetPosition().X;
                float otherX = otherSide.GetPosition().X;
                if (MathF.Abs(myX - otherX) > 20f)
                    desiredFacing = myX >= otherX ? CharacterFacing.Left : CharacterFacing.Right;
            }
        }

        CharacterFacing nativeFacing = ParseFacing(pkg.Metadata.NativeFacing);
        bool needMirror = nativeFacing != desiredFacing;

        // Clone SpriteDef so per-strumline mirroring/Left-Right swap never mutates shared metadata
        var clonedSprite = CloneSpriteDef(pkg.Sprite);
        var clonedFrames = new List<byte[]>(pkg.Frames);

        if (needMirror && clonedSprite.Data.Count >= 5 && clonedFrames.Count >= 5)
        {
            // When we mirror horizontally via Scale.X < 0, Sing Left (1) and Sing Right (4) visually invert!
            // Swapping slots 1 <-> 4 makes mirrored Left lean left and mirrored Right lean right with 0 RAM overhead!
            (clonedSprite.Data[1], clonedSprite.Data[4]) = (clonedSprite.Data[4], clonedSprite.Data[1]);
            (clonedFrames[1], clonedFrames[4]) = (clonedFrames[4], clonedFrames[1]);
        }

        string sprKey = !string.IsNullOrWhiteSpace(spriteKeyOverride)
            ? spriteKeyOverride!
            : $"char_{SanitizeSlug(slug)}_sl{targetStrumline}";
        level.Sprites[sprKey] = (clonedSprite, clonedFrames);

        // Compute target world foot position & scale
        float newSprScale = clonedSprite.Data.Count > 0 && clonedSprite.Data[0].Scale > 0.05f ? clonedSprite.Data[0].Scale : 1f;
        float newNaturalWorldH = Math.Max(32f, pkg.Metadata.IdleHeight * newSprScale);

        Vec2 footPos;
        float objUniformScale = scaleOverride ?? 1.0f;

        if (primaryExisting != null && TryComputeObjectWorldFootAndHeight(level, primaryExisting, out Vec2 oldFoot, out float oldWorldH))
        {
            footPos = posOverride ?? oldFoot;
            if (scaleOverride == null && autoScaleToReplaced && oldWorldH > 24f)
            {
                // Match the replaced character's stage scale seamlessly (clamped so pixel/chibi scales stay reasonable)
                float ratio = oldWorldH / newNaturalWorldH;
                objUniformScale = Math.Clamp(ratio, 0.20f, 4.5f);
            }
        }
        else
        {
            // Default stage positions relative to Camera 0 (or 1280x720 center)
            float camX = level.Scene.Cameras.Count > 0
                ? (level.Scene.Cameras[0].NestedPosition?.X ?? level.Scene.Cameras[0].X)
                : 640f;
            float camY = level.Scene.Cameras.Count > 0
                ? (level.Scene.Cameras[0].NestedPosition?.Y ?? level.Scene.Cameras[0].Y)
                : 360f;
            float camScale = level.Scene.Cameras.Count > 0 && level.Scene.Cameras[0].Scale > 0.05f
                ? level.Scene.Cameras[0].Scale
                : 1f;

            float halfSpanX = 270f / camScale;
            float floorY = camY + (235f / camScale);
            float defaultX = targetStrumline == 0 ? (camX + halfSpanX)
                           : targetStrumline == 1 ? (camX - halfSpanX)
                           : camX + (targetStrumline - 2) * (120f / camScale);

            footPos = posOverride ?? new Vec2 { X = MathF.Round(defaultX, 1), Y = MathF.Round(floorY, 1) };
            if (scaleOverride == null)
            {
                float targetStageH = 400f / camScale;
                if (newNaturalWorldH > targetStageH * 1.45f)
                    objUniformScale = MathF.Round(targetStageH / newNaturalWorldH, 3);
            }
        }

        float signedScaleX = needMirror ? -MathF.Abs(objUniformScale) : MathF.Abs(objUniformScale);
        float scaleY = MathF.Abs(objUniformScale);

        SceneObject targetObj;
        if (primaryExisting != null)
        {
            targetObj = primaryExisting;
            targetObj.Spr = sprKey;
            targetObj.Visible = true;
            targetObj.Opacity = 1f;
            SetObjectTransform(targetObj, footPos, new Vec2 { X = signedScaleX, Y = scaleY });
            targetObj.SaveInitialState();

            // Hide secondary phase/form character objects on the same strumline when equipping locally in Online
            if (autoScaleToReplaced)
            {
                foreach (var extra in existingObjs)
                {
                    if (!ReferenceEquals(extra, targetObj))
                    {
                        extra.Visible = false;
                        extra.SaveInitialState();
                    }
                }
            }
        }
        else
        {
            targetObj = new SceneObject
            {
                Spr = sprKey,
                Visible = true,
                Fixed = false,
                StrumLine = targetStrumline,
                Camera = -1,
                X = footPos.X,
                Y = footPos.Y,
                Scale = new Vec2 { X = signedScaleX, Y = scaleY },
            };
            targetObj.SaveInitialState();
            level.Scene.Objects.Add(targetObj);
        }

        // Update health icon if available (icons[0] = opponent SL1 facing Right, icons[1] = player SL0 facing Left)
        if (updateIcon && pkg.IconSprite != null && pkg.IconFrames != null && pkg.IconFrames.Count > 0 && (targetStrumline == 0 || targetStrumline == 1))
        {
            CharacterFacing desiredIconFacing = targetStrumline == 0 ? CharacterFacing.Left : CharacterFacing.Right;
            bool needIconMirror = nativeFacing != desiredIconFacing;

            var iconFrames = new List<byte[]>(pkg.IconFrames.Count);
            foreach (var rawIcon in pkg.IconFrames)
                iconFrames.Add(needIconMirror ? FlipPngBytesHorizontally(rawIcon) : rawIcon);

            string iconKey = $"{sprKey}_icon";
            level.Sprites[iconKey] = (CloneSpriteDef(pkg.IconSprite), iconFrames);
            while (level.Scene.Icons.Count < 2)
                level.Scene.Icons.Add("");
            int iconSlot = targetStrumline == 0 ? 1 : 0;
            level.Scene.Icons[iconSlot] = iconKey;
        }

        return targetObj;
    }

    private static byte[] FlipPngBytesHorizontally(byte[] pngBytes)
    {
        if (pngBytes == null || pngBytes.Length == 0) return pngBytes ?? Array.Empty<byte>();
        try
        {
            using var img = Image.Load<Rgba32>(pngBytes);
            img.Mutate(ctx => ctx.Flip(FlipMode.Horizontal));
            using var ms = new MemoryStream();
            img.Save(ms, _pngEncoder);
            return ms.ToArray();
        }
        catch
        {
            return pngBytes;
        }
    }

    public static bool TryComputeObjectWorldFootAndHeight(FnmmLevel level, SceneObject obj, out Vec2 worldFoot, out float worldHeight)
    {
        var pos = obj.GetPosition();
        var sc = obj.GetScale();
        worldFoot = pos;
        worldHeight = 0f;

        if (string.IsNullOrEmpty(obj.Spr) || !level.Sprites.TryGetValue(obj.Spr, out var sprEntry))
            return false;
        if (sprEntry.Frames.Count == 0 || sprEntry.Frames[0] == null || sprEntry.Frames[0].Length < 24)
            return false;
        if (!TryReadPngDimensionsFromBytes(sprEntry.Frames[0], out int stripW, out int stripH))
            return false;

        var anim0 = sprEntry.Def.Data.Count > 0 ? sprEntry.Def.Data[0] : null;
        int fc = anim0 != null ? Math.Max(1, anim0.FrameCount) : 1;
        float fw = Math.Max(1, stripW / fc);
        float fh = Math.Max(1, stripH);
        float sprScale = anim0 != null && anim0.Scale > 0.01f ? anim0.Scale : 1f;
        float ox = anim0?.Offset.X ?? 0f;
        float oy = anim0?.Offset.Y ?? 0f;

        float absSX = MathF.Abs(sc.X) > 0.001f ? MathF.Abs(sc.X) : 1f;
        float absSY = MathF.Abs(sc.Y) > 0.001f ? MathF.Abs(sc.Y) : 1f;

        // Attach-to-camera scale multiplier if obj.Camera >= 0
        float camAttachScale = 1f;
        if (obj.Camera >= 0 && obj.Camera < level.Scene.Cameras.Count && level.Scene.Cameras[obj.Camera].Scale > 0.01f)
            camAttachScale = level.Scene.Cameras[obj.Camera].Scale;

        float effOX = sc.X < 0 ? (fw - ox) : ox;
        float effOY = sc.Y < 0 ? (fh - oy) : oy;

        float topLeftX = pos.X - effOX * absSX * sprScale * camAttachScale;
        float topLeftY = pos.Y - effOY * absSY * sprScale * camAttachScale;
        float worldW = fw * absSX * sprScale * camAttachScale;
        worldHeight = fh * absSY * sprScale * camAttachScale;

        worldFoot = new Vec2
        {
            X = MathF.Round(topLeftX + worldW * 0.5f, 1),
            Y = MathF.Round(topLeftY + worldHeight, 1),
        };
        return true;
    }

    public static void SetObjectTransform(SceneObject obj, Vec2 pos, Vec2 scale)
    {
        obj.X = pos.X;
        obj.Y = pos.Y;
        obj.Scale = new Vec2 { X = scale.X, Y = scale.Y };

        if (obj.Transform != null && obj.Transform.Count > 0)
        {
            var sf = obj.Transform[0].ScrollFactor ?? new Vec2 { X = 1f, Y = 1f };
            obj.Transform[0] = new Transform
            {
                Position = new Vec2 { X = pos.X, Y = pos.Y },
                Scale = new Vec2 { X = scale.X, Y = scale.Y },
                ScrollFactor = sf,
            };
        }
    }

    private static FnmmSprite CloneSpriteDef(FnmmSprite src)
    {
        var copy = new FnmmSprite { Type = src.Type };
        foreach (var d in src.Data)
        {
            copy.Data.Add(new SpriteAnimData
            {
                Bilinear = d.Bilinear,
                Loop = d.Loop,
                Num = d.FrameCount,
                Spd = d.Spd,
                SpdFps = d.SpdFps,
                Offset = new Vec2 { X = d.Offset.X, Y = d.Offset.Y },
                Scale = d.Scale > 0.01f ? d.Scale : 1f,
            });
        }
        return copy;
    }

    private static CharacterMetadata? TryReadMetadata(string path)
    {
        if (!File.Exists(path)) return null;
        try { return JsonSerializer.Deserialize<CharacterMetadata>(File.ReadAllText(path)); }
        catch { return null; }
    }

    public static CharacterFacing ParseFacing(string? facing) =>
        string.Equals(facing, "right", StringComparison.OrdinalIgnoreCase)
            ? CharacterFacing.Right
            : CharacterFacing.Left;

    private static bool TryReadPngDimensions(string path, out int width, out int height)
    {
        width = height = 0;
        try
        {
            Span<byte> hdr = stackalloc byte[24];
            using var fs = File.OpenRead(path);
            if (fs.Read(hdr) < 24) return false;
            return ParsePngHeader(hdr, out width, out height);
        }
        catch { return false; }
    }

    public static bool TryReadPngDimensionsFromBytes(byte[] bytes, out int width, out int height)
    {
        width = height = 0;
        if (bytes == null || bytes.Length < 24) return false;
        return ParsePngHeader(bytes.AsSpan(0, 24), out width, out height);
    }

    private static bool ParsePngHeader(ReadOnlySpan<byte> hdr, out int width, out int height)
    {
        width = height = 0;
        if (hdr[0] != 0x89 || hdr[1] != 0x50 || hdr[2] != 0x4E || hdr[3] != 0x47)
            return false;
        width = (hdr[16] << 24) | (hdr[17] << 16) | (hdr[18] << 8) | hdr[19];
        height = (hdr[20] << 24) | (hdr[21] << 16) | (hdr[22] << 8) | hdr[23];
        return width > 0 && height > 0;
    }

    private static void CopySubImage(Image<Rgba32> src, Image<Rgba32> dst, int dstX, int dstY)
    {
        src.ProcessPixelRows(dst, (srcAcc, dstAcc) =>
        {
            int copyW = Math.Min(srcAcc.Width, dstAcc.Width - dstX);
            if (copyW <= 0) return;
            for (int y = 0; y < srcAcc.Height && (dstY + y) < dstAcc.Height; y++)
            {
                srcAcc.GetRowSpan(y).Slice(0, copyW).CopyTo(dstAcc.GetRowSpan(dstY + y).Slice(dstX, copyW));
            }
        });
    }
}
