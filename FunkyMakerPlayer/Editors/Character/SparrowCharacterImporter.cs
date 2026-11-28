using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Xml.Linq;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;
using SixLabors.ImageSharp.Processing;

namespace FunkyMakerPlayer.Editors.Character;

/// <summary>
/// Imports Sparrow v2 (.xml + .png) character sprite sheets into an editable <see cref="CharacterProject"/>.
/// Automatically detects and applies companion Psych Engine, V-Slice, or Codename Engine character
/// definition files (.json / .xml) and nearby health icons when present.
/// </summary>
public static class SparrowCharacterImporter
{
    private sealed class SubTexEntry
    {
        public string Name = "";
        public int X, Y, Width, Height;
        public int FrameX, FrameY, FrameWidth, FrameHeight;
        public bool Rotated;
    }

    private sealed class ExternalAnimDef
    {
        public string AnimRoleName = ""; // e.g. "idle", "singLEFT", "danceLeft"
        public string Prefix = "";       // XML SubTexture prefix
        public List<int>? Indices;
        public float Fps = 24f;
        public bool Loop = false;
        public Vec2 Offset = new();
        public bool HasOffset;
    }

    private sealed class ExternalCharDef
    {
        public string? Name;
        public string? ImageHint;
        public string? HealthIcon;
        public float Scale = 1f;
        public bool FlipX;
        public bool NoAntialiasing;
        public List<ExternalAnimDef> Animations = new();
    }

    public static CharacterProject ImportFromFiles(
        string primaryPath,
        string? explicitPngPath = null,
        string? explicitDefPath = null)
    {
        if (!File.Exists(primaryPath))
            throw new FileNotFoundException("Character file not found", primaryPath);

        string dir = Path.GetDirectoryName(primaryPath) ?? "";
        string ext = Path.GetExtension(primaryPath).ToLowerInvariant();

        string? xmlPath = null;
        string? pngPath = explicitPngPath;
        string? defPath = explicitDefPath;

        if (ext == ".json")
        {
            defPath = primaryPath;
            var preDef = TryParseExternalDef(defPath);
            if (preDef != null && !string.IsNullOrWhiteSpace(preDef.ImageHint))
            {
                string imgLeaf = Path.GetFileNameWithoutExtension(preDef.ImageHint.Replace('\\', '/'));
                xmlPath = FindNearbyFile(dir, imgLeaf + ".xml");
                pngPath ??= FindNearbyFile(dir, imgLeaf + ".png");
            }
            xmlPath ??= FindFirstSparrowXml(dir);
        }
        else if (ext == ".xml")
        {
            // Could be a Codename character XML pointing to a Sparrow sheet, or the Sparrow TextureAtlas itself.
            var rootName = PeekXmlRootName(primaryPath);
            if (string.Equals(rootName, "character", StringComparison.OrdinalIgnoreCase))
            {
                defPath = primaryPath;
                var preDef = TryParseExternalDef(defPath);
                if (preDef != null && !string.IsNullOrWhiteSpace(preDef.ImageHint))
                {
                    string imgLeaf = Path.GetFileNameWithoutExtension(preDef.ImageHint.Replace('\\', '/'));
                    xmlPath = FindNearbyFile(dir, imgLeaf + ".xml");
                    pngPath ??= FindNearbyFile(dir, imgLeaf + ".png");
                }
                xmlPath ??= FindFirstSparrowXml(dir, excludePath: primaryPath);
            }
            else
            {
                xmlPath = primaryPath;
            }
        }
        else if (ext == ".png")
        {
            pngPath = primaryPath;
            string stem = Path.GetFileNameWithoutExtension(primaryPath);
            xmlPath = FindNearbyFile(dir, stem + ".xml") ?? FindFirstSparrowXml(dir);
        }

        if (string.IsNullOrEmpty(xmlPath) || !File.Exists(xmlPath))
            throw new InvalidOperationException("No Sparrow v2 .xml TextureAtlas found for this character.");

        string xmlStem = Path.GetFileNameWithoutExtension(xmlPath);
        if (string.IsNullOrEmpty(pngPath))
        {
            string? xmlImageAttr = PeekXmlImagePath(xmlPath);
            if (!string.IsNullOrWhiteSpace(xmlImageAttr))
            {
                string attrLeaf = Path.GetFileName(xmlImageAttr.Replace('\\', '/'));
                if (!string.IsNullOrWhiteSpace(attrLeaf))
                    pngPath = FindNearbyFile(dir, attrLeaf);
            }
        }
        pngPath ??= FindNearbyFile(dir, xmlStem + ".png")
                 ?? Directory.EnumerateFiles(dir, "*", SearchOption.TopDirectoryOnly)
                        .FirstOrDefault(p =>
                            string.Equals(Path.GetExtension(p), ".png", StringComparison.OrdinalIgnoreCase) &&
                            !Path.GetFileName(p).StartsWith("icon-", StringComparison.OrdinalIgnoreCase));

        if (string.IsNullOrEmpty(pngPath) || !File.Exists(pngPath))
            throw new InvalidOperationException($"Matching PNG atlas not found for '{Path.GetFileName(xmlPath)}'.");

        defPath ??= FindCompanionCharacterDef(dir, xmlStem);
        var extDef = !string.IsNullOrEmpty(defPath) ? TryParseExternalDef(defPath) : null;

        var subTextures = ParseSparrowXml(xmlPath);
        if (subTextures.Count == 0)
            throw new InvalidOperationException($"'{Path.GetFileName(xmlPath)}' contains no <SubTexture> entries.");

        using var sheet = Image.Load<Rgba32>(pngPath);

        string rawName = !string.IsNullOrWhiteSpace(extDef?.Name)
            ? extDef!.Name!
            : (!string.IsNullOrEmpty(defPath) ? Path.GetFileNameWithoutExtension(defPath) : xmlStem);

        var project = new CharacterProject
        {
            Name = CleanDisplayName(rawName),
            Slug = CharacterLibrary.SanitizeSlug(rawName),
            SourceKind = CharacterSourceKind.SparrowXml,
            Scale = extDef != null && extDef.Scale > 0.05f ? Math.Clamp(extDef.Scale, 0.1f, 8f) : 1.0f,
            Bilinear = extDef == null || !extDef.NoAntialiasing,
            BakeFlipX = false,
            // In Psych/FNF, flip_x == true in JSON usually means playable BF-style art drawn facing left/right:
            // Actually in Psych Engine: enemy sprites have flip_x=false (drawn facing right), while BF has flip_x=true
            // (drawn facing left, so Psych flips BF when used as opponent!). Thus flip_x=true => naturally faces Left!
            NativeFacing = extDef != null
                ? (extDef.FlipX ? CharacterFacing.Left : CharacterFacing.Right)
                : GuessFacingFromName(rawName),
        };

        // Build clips: if external character definition exists, build clips for its animations first,
        // plus any remaining XML prefixes so user can still pick any prefix in the editor!
        try
        {
            var usedPrefixes = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            if (extDef != null && extDef.Animations.Count > 0)
            {
                foreach (var anim in extDef.Animations)
                {
                    var matchedFrames = MatchSubTexturesForDef(subTextures, anim.Prefix, anim.Indices);
                    if (matchedFrames.Count == 0) continue;

                    string clipName = string.IsNullOrWhiteSpace(anim.AnimRoleName) ? anim.Prefix : anim.AnimRoleName;
                    // Ensure unique clip name
                    string uniqueName = clipName;
                    int dup = 2;
                    while (project.FindClip(uniqueName) != null)
                        uniqueName = $"{clipName}_{dup++}";

                    var clip = BuildClipFromSubTextures(sheet, uniqueName, matchedFrames);
                    if (clip != null)
                    {
                        clip.SuggestedFps = Math.Clamp(anim.Fps > 0 ? anim.Fps : 24f, 1f, 120f);
                        clip.SuggestedLoop = anim.Loop;
                        clip.ImportedOffset = anim.Offset;
                        clip.HasImportedOffset = anim.HasOffset;
                        project.AvailableClips.Add(clip);
                        usedPrefixes.Add(anim.Prefix);
                    }
                }
            }

            // Also group all XML prefixes so raw XML+PNG imports (and unmapped prefixes) are all selectable
            var groupedByPrefix = GroupSubTexturesByPrefix(subTextures);
            foreach (var (prefix, entries) in groupedByPrefix)
            {
                if (usedPrefixes.Contains(prefix) && project.AvailableClips.Count > 0)
                    continue;
                if (project.FindClip(prefix) != null)
                    continue;

                var clip = BuildClipFromSubTextures(sheet, prefix, entries);
                if (clip != null)
                    project.AvailableClips.Add(clip);
            }

            if (project.AvailableClips.Count == 0)
                throw new InvalidOperationException("Could not decode any valid animation frames from the Sparrow atlas.");

            AutoMapCharacterSlots(project);
            TryAutoLoadNearbyIcon(project, dir, extDef?.HealthIcon, xmlStem);

            return project;
        }
        catch
        {
            project.Dispose();
            throw;
        }
    }

    public static void AutoMapCharacterSlots(CharacterProject project)
    {
        project.EnsureCoreSlots();
        var clips = project.AvailableClips;
        if (clips.Count == 0) return;

        // Helper: find best non-miss clip matching keywords
        ImportedAnimClip? Pick(string[] primaryKeywords, string[]? fallbackKeywords = null, bool allowMiss = false)
        {
            foreach (var kw in primaryKeywords)
            {
                var exact = clips.FirstOrDefault(c =>
                    (!IsMissAnimName(c.Name) || allowMiss) &&
                    !IsAltAnimName(c.Name) &&
                    ContainsKeyword(c.Name, kw));
                if (exact != null) return exact;
            }
            foreach (var kw in primaryKeywords)
            {
                var any = clips.FirstOrDefault(c =>
                    (!IsMissAnimName(c.Name) || allowMiss) &&
                    ContainsKeyword(c.Name, kw));
                if (any != null) return any;
            }
            if (fallbackKeywords != null)
            {
                foreach (var kw in fallbackKeywords)
                {
                    var fb = clips.FirstOrDefault(c =>
                        (!IsMissAnimName(c.Name) || allowMiss) &&
                        ContainsKeyword(c.Name, kw));
                    if (fb != null) return fb;
                }
            }
            return null;
        }

        var idle = Pick(new[] { "idle", "danceleft", "dance" }) ?? clips[0];
        var left = Pick(new[] { "singleft", "note left", "note_left", "left" }) ?? idle;
        var down = Pick(new[] { "singdown", "note down", "note_down", "down" }) ?? idle;
        var up = Pick(new[] { "singup", "note up", "note_up", "up" }) ?? idle;
        var right = Pick(new[] { "singright", "note right", "note_right", "right" }) ?? idle;

        ImportedAnimClip[] chosen = { idle, left, down, up, right };
        for (int i = 0; i < 5; i++)
        {
            project.Slots[i].ClipName = chosen[i].Name;
            project.Slots[i].Fps = chosen[i].SuggestedFps > 0 ? chosen[i].SuggestedFps : 24f;
            // Core character slots in FunkyMaker must have Loop=true so Idle loops and Sing poses hold until _singTimer returns to Idle
            project.Slots[i].Loop = true;
            project.Slots[i].UserOffsetX = 0f;
            project.Slots[i].UserOffsetY = 0f;
        }
    }

    internal static bool IsMissAnimName(string name)
    {
        string lower = name.ToLowerInvariant();
        return lower.Contains("miss") || lower.Contains("fail") || lower.Contains("dead") || lower.Contains("death");
    }

    private static bool IsAltAnimName(string name)
    {
        string lower = name.ToLowerInvariant();
        return lower.Contains("-alt") || lower.Contains(" alt") || lower.EndsWith("alt", StringComparison.Ordinal);
    }

    private static bool ContainsKeyword(string text, string keyword)
    {
        return text.IndexOf(keyword, StringComparison.OrdinalIgnoreCase) >= 0;
    }

    private static ImportedAnimClip? BuildClipFromSubTextures(Image<Rgba32> sheet, string clipName, List<SubTexEntry> entries)
    {
        if (entries.Count == 0) return null;

        // First compute the tight untrimmed bounding box across all frames of this clip.
        // In Sparrow v2 / Starling:
        //   If rotated == true, the rect (Width x Height) in the atlas is rotated 90 deg CW,
        //   so after rotating 90 deg CCW (Rotate270), the unrotated sub-image has size (Height x Width).
        //   Inside the logical frame, the trimmed sub-image top-left sits at (-FrameX, -FrameY).
        int minX = int.MaxValue, minY = int.MaxValue;
        int maxX = int.MinValue, maxY = int.MinValue;

        var validEntries = new List<(SubTexEntry E, int UnrotW, int UnrotH, int DstX, int DstY)>(entries.Count);
        foreach (var e in entries)
        {
            int sx = Math.Clamp(e.X, 0, Math.Max(0, sheet.Width - 1));
            int sy = Math.Clamp(e.Y, 0, Math.Max(0, sheet.Height - 1));
            int sw = Math.Clamp(e.Width, 0, sheet.Width - sx);
            int sh = Math.Clamp(e.Height, 0, sheet.Height - sy);
            if (sw <= 0 || sh <= 0) continue;

            int uw = e.Rotated ? sh : sw;
            int uh = e.Rotated ? sw : sh;
            int dx = -e.FrameX;
            int dy = -e.FrameY;

            minX = Math.Min(minX, dx);
            minY = Math.Min(minY, dy);
            maxX = Math.Max(maxX, dx + uw);
            maxY = Math.Max(maxY, dy + uh);
            validEntries.Add((e, uw, uh, dx, dy));
        }

        if (validEntries.Count == 0) return null;

        int canvasW = Math.Max(1, maxX - minX);
        int canvasH = Math.Max(1, maxY - minY);

        var clip = new ImportedAnimClip
        {
            Name = clipName,
            CanvasWidth = canvasW,
            CanvasHeight = canvasH,
            WorldMinX = minX,
            WorldMinY = minY,
        };

        foreach (var (e, _, _, dx, dy) in validEntries)
        {
            int sx = Math.Clamp(e.X, 0, sheet.Width - 1);
            int sy = Math.Clamp(e.Y, 0, sheet.Height - 1);
            int sw = Math.Clamp(e.Width, 1, sheet.Width - sx);
            int sh = Math.Clamp(e.Height, 1, sheet.Height - sy);

            using var cropped = sheet.Clone(ctx =>
            {
                ctx.Crop(new Rectangle(sx, sy, sw, sh));
                if (e.Rotated)
                    ctx.Rotate(RotateMode.Rotate270);
            });

            var canvas = new Image<Rgba32>(canvasW, canvasH);
            int targetX = dx - minX;
            int targetY = dy - minY;
            CopyPixelsDirect(cropped, canvas, targetX, targetY);
            clip.Frames.Add(canvas);
        }

        return clip;
    }

    private static void CopyPixelsDirect(Image<Rgba32> src, Image<Rgba32> dst, int dstX, int dstY)
    {
        src.ProcessPixelRows(dst, (srcAcc, dstAcc) =>
        {
            for (int y = 0; y < srcAcc.Height; y++)
            {
                int ty = dstY + y;
                if ((uint)ty >= (uint)dstAcc.Height) continue;

                var srcRow = srcAcc.GetRowSpan(y);
                var dstRow = dstAcc.GetRowSpan(ty);
                int startX = Math.Max(0, -dstX);
                int endX = Math.Min(srcAcc.Width, dstAcc.Width - dstX);
                if (endX <= startX) continue;

                srcRow.Slice(startX, endX - startX).CopyTo(dstRow.Slice(dstX + startX, endX - startX));
            }
        });
    }

    private static List<SubTexEntry> ParseSparrowXml(string xmlPath)
    {
        var list = new List<SubTexEntry>();
        string raw = File.ReadAllText(xmlPath);
        if (raw.Length > 0 && raw[0] == '\uFEFF')
            raw = raw.Substring(1);

        var doc = XDocument.Parse(raw);
        if (doc.Root == null) return list;

        foreach (var el in doc.Root.Elements())
        {
            if (!string.Equals(el.Name.LocalName, "SubTexture", StringComparison.OrdinalIgnoreCase))
                continue;

            int w = ParseIntAttr(el, "width");
            int h = ParseIntAttr(el, "height");
            if (w <= 0 || h <= 0) continue;

            bool rot = ParseBoolAttr(el, "rotated");
            int defFw = rot ? h : w;
            int defFh = rot ? w : h;

            list.Add(new SubTexEntry
            {
                Name = (string?)el.Attribute("name") ?? "",
                X = ParseIntAttr(el, "x"),
                Y = ParseIntAttr(el, "y"),
                Width = w,
                Height = h,
                FrameX = ParseIntAttr(el, "frameX"),
                FrameY = ParseIntAttr(el, "frameY"),
                FrameWidth = ParseIntAttr(el, "frameWidth", defFw),
                FrameHeight = ParseIntAttr(el, "frameHeight", defFh),
                Rotated = rot,
            });
        }

        return list;
    }

    private static List<(string Prefix, List<SubTexEntry> Entries)> GroupSubTexturesByPrefix(List<SubTexEntry> all)
    {
        var map = new Dictionary<string, List<(SubTexEntry Entry, int FrameNum, int Order)>>(StringComparer.Ordinal);
        var orderList = new List<string>();

        for (int i = 0; i < all.Count; i++)
        {
            var e = all[i];
            SplitPrefixAndNumber(e.Name, out string prefix, out int frameNum);
            if (string.IsNullOrWhiteSpace(prefix))
                prefix = "anim";

            if (!map.TryGetValue(prefix, out var bucket))
            {
                bucket = new List<(SubTexEntry, int, int)>();
                map[prefix] = bucket;
                orderList.Add(prefix);
            }
            bucket.Add((e, frameNum, i));
        }

        var result = new List<(string, List<SubTexEntry>)>(orderList.Count);
        foreach (var prefix in orderList)
        {
            var bucket = map[prefix];
            bucket.Sort((a, b) =>
            {
                int cmp = a.FrameNum.CompareTo(b.FrameNum);
                return cmp != 0 ? cmp : a.Order.CompareTo(b.Order);
            });
            result.Add((prefix, bucket.Select(x => x.Entry).ToList()));
        }
        return result;
    }

    private static List<SubTexEntry> MatchSubTexturesForDef(List<SubTexEntry> all, string prefix, List<int>? indices)
    {
        if (string.IsNullOrWhiteSpace(prefix))
            return new List<SubTexEntry>();

        // Match entries whose name starts with prefix AND remaining chars (if any) are digits or instance digits
        var candidates = new List<(SubTexEntry Entry, int FrameNum, int Order)>();
        for (int i = 0; i < all.Count; i++)
        {
            var e = all[i];
            if (!e.Name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                continue;

            string suffix = e.Name.Substring(prefix.Length);
            // Avoid matching "BF idle dance" against "BF idle dance alt0000" when non-alt exists
            bool suffixDigitsOnly = suffix.Length == 0 || suffix.All(c => char.IsDigit(c) || c == ' ' || c == '_');
            if (!suffixDigitsOnly) continue;

            SplitPrefixAndNumber(e.Name, out _, out int num);
            candidates.Add((e, num, i));
        }

        // Fallback: looser prefix match if strict digit-suffix match found nothing
        if (candidates.Count == 0)
        {
            for (int i = 0; i < all.Count; i++)
            {
                var e = all[i];
                if (!e.Name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                    continue;
                SplitPrefixAndNumber(e.Name, out _, out int num);
                candidates.Add((e, num, i));
            }
        }

        candidates.Sort((a, b) =>
        {
            int cmp = a.FrameNum.CompareTo(b.FrameNum);
            return cmp != 0 ? cmp : a.Order.CompareTo(b.Order);
        });

        if (indices != null && indices.Count > 0 && candidates.Count > 0)
        {
            var indexed = new List<SubTexEntry>(indices.Count);
            foreach (int idx in indices)
            {
                // Try direct index in sorted candidate list (Psych Engine convention)
                if (idx >= 0 && idx < candidates.Count)
                    indexed.Add(candidates[idx].Entry);
                else
                {
                    var byNum = candidates.FirstOrDefault(c => c.FrameNum == idx);
                    if (byNum.Entry != null)
                        indexed.Add(byNum.Entry);
                }
            }
            if (indexed.Count > 0)
                return indexed;
        }

        return candidates.Select(c => c.Entry).ToList();
    }

    internal static void SplitPrefixAndNumber(string rawName, out string prefix, out int frameNum)
    {
        frameNum = 0;
        if (string.IsNullOrEmpty(rawName))
        {
            prefix = "";
            return;
        }

        int end = rawName.Length;
        while (end > 0 && char.IsDigit(rawName[end - 1]))
            end--;

        if (end < rawName.Length && int.TryParse(rawName.AsSpan(end), NumberStyles.Integer, CultureInfo.InvariantCulture, out int parsed))
            frameNum = parsed;

        prefix = rawName.Substring(0, end).TrimEnd();
    }

    private static ExternalCharDef? TryParseExternalDef(string path)
    {
        try
        {
            string ext = Path.GetExtension(path).ToLowerInvariant();
            string text = File.ReadAllText(path);
            if (text.Length > 0 && text[0] == '\uFEFF')
                text = text.Substring(1);

            if (ext == ".xml")
                return TryParseCodenameXml(text, Path.GetFileNameWithoutExtension(path));

            using var doc = JsonDocument.Parse(text, new JsonDocumentOptions
            {
                AllowTrailingCommas = true,
                CommentHandling = JsonCommentHandling.Skip,
            });
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
                return null;

            // Must look like a character JSON (has "animations" array)
            if (!TryGetProp(root, "animations", out var animsEl) || animsEl.ValueKind != JsonValueKind.Array)
                return null;

            var def = new ExternalCharDef
            {
                Name = GetStringProp(root, "name") ?? Path.GetFileNameWithoutExtension(path),
                ImageHint = GetStringProp(root, "image") ?? GetStringProp(root, "assetPath"),
                HealthIcon = GetStringProp(root, "healthicon")
                    ?? (TryGetProp(root, "healthIcon", out var hi) && hi.ValueKind == JsonValueKind.Object
                        ? GetStringProp(hi, "id")
                        : GetStringProp(root, "healthIcon")),
                Scale = GetFloatProp(root, "scale", 1f),
                FlipX = GetBoolProp(root, "flip_x") || GetBoolProp(root, "flipX"),
                NoAntialiasing = GetBoolProp(root, "no_antialiasing") || GetBoolProp(root, "isPixel"),
            };

            foreach (var item in animsEl.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.Object) continue;

                // Psych Engine: { "anim": "idle", "name": "BF idle dance", "fps": 24, "loop": false, "indices": [], "offsets": [-5, 0] }
                // V-Slice:      { "name": "idle", "prefix": "BF idle dance", "frameRate": 24, "looped": false, "frameIndices": [], "offsets": [0, 0] }
                string? roleName = GetStringProp(item, "anim");
                string? prefix = GetStringProp(item, "prefix") ?? GetStringProp(item, "name");
                if (roleName == null)
                {
                    roleName = GetStringProp(item, "name") ?? prefix ?? "";
                }
                if (string.IsNullOrWhiteSpace(prefix))
                    continue;

                var a = new ExternalAnimDef
                {
                    AnimRoleName = roleName,
                    Prefix = prefix,
                    Fps = GetFloatProp(item, "fps", GetFloatProp(item, "frameRate", 24f)),
                    Loop = GetBoolProp(item, "loop") || GetBoolProp(item, "looped"),
                };

                if ((TryGetProp(item, "indices", out var idxEl) || TryGetProp(item, "frameIndices", out idxEl))
                    && idxEl.ValueKind == JsonValueKind.Array)
                {
                    var idxList = new List<int>();
                    foreach (var n in idxEl.EnumerateArray())
                    {
                        if (n.ValueKind == JsonValueKind.Number && n.TryGetInt32(out int v))
                            idxList.Add(v);
                    }
                    if (idxList.Count > 0)
                        a.Indices = idxList;
                }

                if (TryGetProp(item, "offsets", out var offEl) && offEl.ValueKind == JsonValueKind.Array)
                {
                    var offs = new List<float>(2);
                    foreach (var n in offEl.EnumerateArray())
                    {
                        if (n.ValueKind == JsonValueKind.Number)
                            offs.Add((float)n.GetDouble());
                    }
                    if (offs.Count >= 2)
                    {
                        a.Offset = new Vec2 { X = offs[0], Y = offs[1] };
                        a.HasOffset = true;
                    }
                }

                def.Animations.Add(a);
            }

            return def.Animations.Count > 0 ? def : null;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[SparrowImporter] Companion definition skipped ({Path.GetFileName(path)}): {ex.Message}");
            return null;
        }
    }

    private static ExternalCharDef? TryParseCodenameXml(string xmlText, string fallbackName)
    {
        var doc = XDocument.Parse(xmlText);
        if (doc.Root == null || !string.Equals(doc.Root.Name.LocalName, "character", StringComparison.OrdinalIgnoreCase))
            return null;

        var root = doc.Root;
        var def = new ExternalCharDef
        {
            Name = fallbackName,
            ImageHint = (string?)root.Attribute("sprite") ?? fallbackName,
            HealthIcon = (string?)root.Attribute("icon") ?? fallbackName,
            Scale = ParseFloatAttr(root, "scale", 1f),
            FlipX = ParseBoolAttr(root, "flipX"),
            NoAntialiasing = string.Equals((string?)root.Attribute("antialiasing"), "false", StringComparison.OrdinalIgnoreCase),
        };

        foreach (var el in root.Elements())
        {
            if (!string.Equals(el.Name.LocalName, "anim", StringComparison.OrdinalIgnoreCase))
                continue;

            string role = (string?)el.Attribute("name") ?? "";
            string prefix = (string?)el.Attribute("anim") ?? role;
            if (string.IsNullOrWhiteSpace(prefix)) continue;

            var a = new ExternalAnimDef
            {
                AnimRoleName = role,
                Prefix = prefix,
                Fps = ParseFloatAttr(el, "fps", 24f),
                Loop = ParseBoolAttr(el, "loop"),
                Offset = new Vec2
                {
                    X = ParseFloatAttr(el, "x", 0f),
                    Y = ParseFloatAttr(el, "y", 0f),
                },
                HasOffset = el.Attribute("x") != null || el.Attribute("y") != null,
            };

            string? idxStr = (string?)el.Attribute("indices");
            if (!string.IsNullOrWhiteSpace(idxStr))
            {
                var idxList = new List<int>();
                foreach (var part in idxStr.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                {
                    int dotDot = part.IndexOf("..", StringComparison.Ordinal);
                    if (dotDot > 0 &&
                        int.TryParse(part.AsSpan(0, dotDot), out int rStart) &&
                        int.TryParse(part.AsSpan(dotDot + 2), out int rEnd))
                    {
                        int step = rEnd >= rStart ? 1 : -1;
                        for (int k = rStart; k != rEnd + step; k += step)
                            idxList.Add(k);
                    }
                    else if (int.TryParse(part, out int v))
                    {
                        idxList.Add(v);
                    }
                }
                if (idxList.Count > 0)
                    a.Indices = idxList;
            }

            def.Animations.Add(a);
        }

        return def.Animations.Count > 0 ? def : null;
    }

    internal static void TryAutoLoadNearbyIcon(CharacterProject project, string dir, string? iconHint, string stem)
    {
        if (string.IsNullOrEmpty(dir) || !Directory.Exists(dir)) return;

        var candidates = new List<string>();
        void AddIconCandidates(string baseDir)
        {
            if (string.IsNullOrEmpty(baseDir) || !Directory.Exists(baseDir)) return;
            if (!string.IsNullOrWhiteSpace(iconHint))
            {
                var h1 = FindNearbyFile(baseDir, $"icon-{iconHint}.png");
                if (h1 != null) candidates.Add(h1);
                var h2 = FindNearbyFile(baseDir, $"{iconHint}.png");
                if (h2 != null) candidates.Add(h2);
            }
            var s1 = FindNearbyFile(baseDir, $"icon-{stem}.png");
            if (s1 != null) candidates.Add(s1);
            var s2 = FindNearbyFile(baseDir, "icon.png");
            if (s2 != null) candidates.Add(s2);
            foreach (var f in Directory.EnumerateFiles(baseDir, "*", SearchOption.TopDirectoryOnly))
            {
                string fn = Path.GetFileName(f);
                if (fn.StartsWith("icon-", StringComparison.OrdinalIgnoreCase) &&
                    fn.EndsWith(".png", StringComparison.OrdinalIgnoreCase))
                    candidates.Add(f);
            }
        }

        static string? FindSubdirIgnoreCase(string parentDir, string name) =>
            !string.IsNullOrEmpty(parentDir) && Directory.Exists(parentDir)
                ? Directory.EnumerateDirectories(parentDir, "*", SearchOption.TopDirectoryOnly)
                    .FirstOrDefault(d => string.Equals(Path.GetFileName(d), name, StringComparison.OrdinalIgnoreCase))
                : null;

        AddIconCandidates(dir);
        var iconsSub = FindSubdirIgnoreCase(dir, "icons");
        if (iconsSub != null) AddIconCandidates(iconsSub);
        string? parent = Path.GetDirectoryName(dir);
        if (!string.IsNullOrEmpty(parent))
        {
            var parentIcons = FindSubdirIgnoreCase(parent, "icons");
            if (parentIcons != null) AddIconCandidates(parentIcons);
        }

        foreach (var path in candidates)
        {
            if (File.Exists(path))
            {
                try
                {
                    CharacterLibrary.LoadIconFromPng(project, path);
                    Console.WriteLine($"[CharacterImporter] Auto-loaded health icon: {Path.GetFileName(path)}");
                    return;
                }
                catch { }
            }
        }
    }

    private static string? FindCompanionCharacterDef(string dir, string xmlStem)
    {
        if (string.IsNullOrEmpty(dir) || !Directory.Exists(dir)) return null;

        // Direct stem match first (case-insensitive on Linux/Android)
        string? directJson = FindNearbyFile(dir, xmlStem + ".json");
        if (directJson != null && TryParseExternalDef(directJson) != null)
            return directJson;

        // Any JSON in the same folder or parent characters/ folder whose "image" matches xmlStem (or if only 1 char json exists)
        var jsonFiles = Directory.EnumerateFiles(dir, "*", SearchOption.TopDirectoryOnly)
            .Where(p => string.Equals(Path.GetExtension(p), ".json", StringComparison.OrdinalIgnoreCase)
                     && !Path.GetFileName(p).StartsWith("spritemap", StringComparison.OrdinalIgnoreCase)
                     && !string.Equals(Path.GetFileName(p), "Animation.json", StringComparison.OrdinalIgnoreCase)
                     && !string.Equals(Path.GetFileName(p), "animations.json", StringComparison.OrdinalIgnoreCase)
                     && !string.Equals(Path.GetFileName(p), "metadata.json", StringComparison.OrdinalIgnoreCase)
                     && !string.Equals(Path.GetFileName(p), "sprite.json", StringComparison.OrdinalIgnoreCase)
                     && !string.Equals(Path.GetFileName(p), "character.json", StringComparison.OrdinalIgnoreCase))
            .ToList();

        string? singleValid = null;
        int validCount = 0;
        foreach (var jf in jsonFiles)
        {
            var parsed = TryParseExternalDef(jf);
            if (parsed == null) continue;
            validCount++;
            singleValid = jf;
            if (!string.IsNullOrWhiteSpace(parsed.ImageHint) &&
                Path.GetFileNameWithoutExtension(parsed.ImageHint.Replace('\\', '/'))
                    .Equals(xmlStem, StringComparison.OrdinalIgnoreCase))
            {
                return jf;
            }
        }

        return validCount == 1 ? singleValid : null;
    }

    private static string? FindNearbyFile(string dir, string fileName)
    {
        if (string.IsNullOrEmpty(dir) || !Directory.Exists(dir)) return null;
        string direct = Path.Combine(dir, fileName);
        if (File.Exists(direct)) return direct;
        return Directory.EnumerateFiles(dir, "*", SearchOption.TopDirectoryOnly)
            .FirstOrDefault(p => string.Equals(Path.GetFileName(p), fileName, StringComparison.OrdinalIgnoreCase));
    }

    private static string? FindFirstSparrowXml(string dir, string? excludePath = null)
    {
        if (string.IsNullOrEmpty(dir) || !Directory.Exists(dir)) return null;
        foreach (var xp in Directory.EnumerateFiles(dir, "*", SearchOption.TopDirectoryOnly))
        {
            if (!string.Equals(Path.GetExtension(xp), ".xml", StringComparison.OrdinalIgnoreCase))
                continue;
            if (excludePath != null && string.Equals(xp, excludePath, StringComparison.OrdinalIgnoreCase))
                continue;
            if (string.Equals(PeekXmlRootName(xp), "TextureAtlas", StringComparison.OrdinalIgnoreCase))
                return xp;
        }
        return null;
    }

    private static string? PeekXmlImagePath(string xmlPath)
    {
        try
        {
            string raw = File.ReadAllText(xmlPath);
            if (raw.Length > 0 && raw[0] == '\uFEFF') raw = raw.Substring(1);
            var doc = XDocument.Parse(raw);
            return (string?)doc.Root?.Attribute("imagePath");
        }
        catch
        {
            return null;
        }
    }

    private static string? PeekXmlRootName(string xmlPath)
    {
        try
        {
            string raw = File.ReadAllText(xmlPath);
            if (raw.Length > 0 && raw[0] == '\uFEFF') raw = raw.Substring(1);
            var doc = XDocument.Parse(raw);
            return doc.Root?.Name.LocalName;
        }
        catch
        {
            return null;
        }
    }

    internal static CharacterFacing GuessFacingFromName(string name)
    {
        string lower = name.ToLowerInvariant();
        if (lower.Contains("bf") || lower.Contains("boyfriend") || lower.Contains("playable") || lower.Contains("player"))
            return CharacterFacing.Left;
        return CharacterFacing.Left; // Default playable orientation (user can toggle 1-click in editor)
    }

    internal static string CleanDisplayName(string raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return "Custom Character";
        string cleaned = raw.Replace('_', ' ').Replace('-', ' ').Trim();
        return cleaned.Length > 0 ? cleaned : "Custom Character";
    }

    private static int ParseIntAttr(XElement el, string name, int fallback = 0)
    {
        var attr = el.Attribute(name);
        if (attr == null) return fallback;
        if (int.TryParse(attr.Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out int v))
            return v;
        if (float.TryParse(attr.Value, NumberStyles.Float, CultureInfo.InvariantCulture, out float fv))
            return (int)MathF.Round(fv);
        return fallback;
    }

    private static float ParseFloatAttr(XElement el, string name, float fallback = 0f)
    {
        var attr = el.Attribute(name);
        if (attr == null) return fallback;
        return float.TryParse(attr.Value, NumberStyles.Float, CultureInfo.InvariantCulture, out float v) ? v : fallback;
    }

    private static bool ParseBoolAttr(XElement el, string name)
    {
        var attr = el.Attribute(name);
        return attr != null && (string.Equals(attr.Value, "true", StringComparison.OrdinalIgnoreCase) || attr.Value == "1");
    }

    private static bool TryGetProp(JsonElement obj, string name, out JsonElement val)
    {
        if (obj.TryGetProperty(name, out val)) return true;
        foreach (var p in obj.EnumerateObject())
        {
            if (string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase))
            {
                val = p.Value;
                return true;
            }
        }
        val = default;
        return false;
    }

    private static string? GetStringProp(JsonElement obj, string name) =>
        TryGetProp(obj, name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    private static float GetFloatProp(JsonElement obj, string name, float fallback) =>
        TryGetProp(obj, name, out var v) && v.ValueKind == JsonValueKind.Number ? (float)v.GetDouble() : fallback;

    private static bool GetBoolProp(JsonElement obj, string name) =>
        TryGetProp(obj, name, out var v) && (v.ValueKind == JsonValueKind.True ||
            (v.ValueKind == JsonValueKind.String && string.Equals(v.GetString(), "true", StringComparison.OrdinalIgnoreCase)));
}
