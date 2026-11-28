using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Numerics;
using System.Text.Json;
using System.Threading.Tasks;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;
using SixLabors.ImageSharp.Processing;

namespace FunkyMakerPlayer.Editors.Character;

/// <summary>
/// Full Adobe Animate Texture Atlas ("Spritemap" / FlxAnimate) character importer and software rasterizer.
/// Supports Animation.json (both optimized compact keys AN/SD/TL/L/FR/E/ASI/SI/M3D/MX and verbose keys)
/// + one or multiple spritemap*.json / spritemap*.png sheets, frame-label timelines, nested symbol timelines,
/// loop modes (LP / PO / SF), color/alpha multipliers, and companion Psych / V-Slice character JSONs.
/// </summary>
public static class SpritemapCharacterImporter
{
    private const int MAX_RASTER_DIMENSION = 1024;
    private const int MAX_FRAMES_PER_CLIP = 96;
    private const int MAX_RECURSION_DEPTH = 24;

    private sealed class LimbSprite : IDisposable
    {
        public string Name = "";
        public Image<Rgba32> Image = null!;
        public float Resolution = 1f; // from spritemap meta.resolution / scale (e.g. 2 = 2x pixels per stage unit)
        public int Width => Image.Width;
        public int Height => Image.Height;

        public void Dispose()
        {
            try { Image?.Dispose(); } catch { }
        }
    }

    private sealed class AnimateElement
    {
        public bool IsSymbol;
        public string Name = ""; // Limb sprite name (ASI) or Symbol name (SI)
        public string SymbolType = "G"; // G = graphic, MC = movieclip, B = button
        public Matrix3x2 Matrix = Matrix3x2.Identity;
        public string LoopMode = "LP"; // LP = loop, PO = play once, SF = single frame, LPR/POR = reverse
        public int FirstFrame;
        public Vector4 ColorMult = Vector4.One; // RGBA multiplier (0..1)
        public Vector4 ColorOffset = Vector4.Zero; // RGBA additive offset (0..255)
    }

    private sealed class AnimateKeyframe
    {
        public int Index;
        public int Duration = 1;
        public string? Label;
        public List<AnimateElement> Elements = new();
    }

    private sealed class AnimateLayer
    {
        public string Name = "";
        public List<AnimateKeyframe> Frames = new();
        public int TotalFrames;
    }

    private sealed class AnimateTimeline
    {
        public string Name = "";
        public List<AnimateLayer> Layers = new();
        public int TotalFrames;
    }

    private readonly struct DrawCmd
    {
        public readonly LimbSprite Limb;
        public readonly Matrix3x2 WorldMatrix;
        public readonly Vector4 ColorMult;
        public readonly Vector4 ColorOffset;

        public DrawCmd(LimbSprite limb, Matrix3x2 worldMatrix, Vector4 colorMult, Vector4 colorOffset)
        {
            Limb = limb;
            WorldMatrix = worldMatrix;
            ColorMult = colorMult;
            ColorOffset = colorOffset;
        }
    }

    public static bool IsSpritemapFolder(string dirPath)
    {
        if (string.IsNullOrEmpty(dirPath) || !Directory.Exists(dirPath)) return false;
        bool hasSpritemap = FindSpritemapJsons(dirPath).Count > 0;
        if (!hasSpritemap) return false;

        bool hasAnim = FindAnimationJson(dirPath) != null;
        bool hasLibrary = FindLibraryDirectory(dirPath) != null;
        bool hasMeta = FindMetadataJson(dirPath) != null;
        return hasAnim || hasLibrary || hasMeta;
    }

    private static string? FindAnimationJson(string dirPath) =>
        Directory.EnumerateFiles(dirPath, "*", SearchOption.TopDirectoryOnly)
            .FirstOrDefault(f =>
            {
                string n = Path.GetFileName(f);
                return string.Equals(n, "Animation.json", StringComparison.OrdinalIgnoreCase)
                    || string.Equals(n, "animations.json", StringComparison.OrdinalIgnoreCase);
            });

    private static string? FindMetadataJson(string dirPath) =>
        Directory.EnumerateFiles(dirPath, "*", SearchOption.TopDirectoryOnly)
            .FirstOrDefault(f => string.Equals(Path.GetFileName(f), "metadata.json", StringComparison.OrdinalIgnoreCase));

    private static string? FindLibraryDirectory(string dirPath) =>
        Directory.EnumerateDirectories(dirPath, "*", SearchOption.TopDirectoryOnly)
            .FirstOrDefault(d => string.Equals(Path.GetFileName(d), "library", StringComparison.OrdinalIgnoreCase));

    private static List<string> FindSpritemapJsons(string dirPath) =>
        Directory.EnumerateFiles(dirPath, "*", SearchOption.TopDirectoryOnly)
            .Where(f =>
                string.Equals(Path.GetExtension(f), ".json", StringComparison.OrdinalIgnoreCase) &&
                Path.GetFileName(f).StartsWith("spritemap", StringComparison.OrdinalIgnoreCase))
            .OrderBy(p => p, StringComparer.OrdinalIgnoreCase)
            .ToList();

    private static string NormalizeSymbolKey(string key) => key.Replace('\\', '/').Trim();

    public static string? FindSpritemapRoot(string fileOrFolderPath)
    {
        if (string.IsNullOrEmpty(fileOrFolderPath)) return null;
        string dir = Directory.Exists(fileOrFolderPath)
            ? fileOrFolderPath
            : (Path.GetDirectoryName(fileOrFolderPath) ?? "");
        if (string.IsNullOrEmpty(dir) || !Directory.Exists(dir)) return null;
        if (IsSpritemapFolder(dir)) return dir;

        var cur = new DirectoryInfo(dir);
        for (int up = 0; up < 3 && cur?.Parent != null; up++)
        {
            cur = cur.Parent;
            if (IsSpritemapFolder(cur.FullName))
                return cur.FullName;
        }
        return null;
    }

    public static CharacterProject ImportFromPath(string fileOrFolderPath)
    {
        string dir = FindSpritemapRoot(fileOrFolderPath)
            ?? (Directory.Exists(fileOrFolderPath) ? fileOrFolderPath : (Path.GetDirectoryName(fileOrFolderPath) ?? ""));

        if (string.IsNullOrEmpty(dir) || !Directory.Exists(dir))
            throw new DirectoryNotFoundException($"Spritemap folder not found: {fileOrFolderPath}");

        string? animJsonPath = FindAnimationJson(dir);
        string? metaJsonPath = FindMetadataJson(dir);
        string? libraryDir = FindLibraryDirectory(dir);

        if (animJsonPath == null && libraryDir == null)
            throw new InvalidOperationException("Neither Animation.json/animations.json nor a LIBRARY/ folder was found in the selected Spritemap folder.");

        var spritemapJsons = FindSpritemapJsons(dir);
        if (spritemapJsons.Count == 0)
            throw new InvalidOperationException("No spritemap*.json files were found in the selected Spritemap folder.");

        var limbs = new Dictionary<string, LimbSprite>(StringComparer.OrdinalIgnoreCase);
        try
        {
            foreach (var smJson in spritemapJsons)
                LoadSpritemapSheet(smJson, limbs);

            if (limbs.Count == 0)
                throw new InvalidOperationException("No limb sprites could be decoded from spritemap*.png.");

            float docFps = 24f;
            string animDisplayName = new DirectoryInfo(dir).Name;
            string? metaDefaultSymbol = null;

            // 1. Optional metadata.json (BetterTextureAtlas / flixel-animate v2)
            if (metaJsonPath != null)
            {
                try
                {
                    using var metaDoc = JsonDocument.Parse(StripBom(File.ReadAllText(metaJsonPath)), new JsonDocumentOptions
                    {
                        AllowTrailingCommas = true,
                        CommentHandling = JsonCommentHandling.Skip,
                    });
                    var mRoot = metaDoc.RootElement;
                    docFps = GetFloatProp(mRoot, "FRT", GetFloatProp(mRoot, "framerate", docFps));
                    string? mName = GetStringProp(mRoot, "N") ?? GetStringProp(mRoot, "name");
                    if (!string.IsNullOrWhiteSpace(mName)) animDisplayName = mName!;
                    metaDefaultSymbol = GetStringProp(mRoot, "AN") ?? GetStringProp(mRoot, "SN") ?? GetStringProp(mRoot, "SYMBOL_name");
                }
                catch { }
            }

            var symbols = new Dictionary<string, AnimateTimeline>(StringComparer.OrdinalIgnoreCase);

            // 2. Load external LIBRARY/ (or library/) symbol JSON files (BTA / Adobe Animate split-library exports)
            if (libraryDir != null)
                LoadLibraryFolderSymbols(libraryDir, symbols);

            AnimateTimeline? mainTimeline = null;
            Matrix3x2 stageRootMatrix = Matrix3x2.Identity;

            // 3. Parse Animation.json / animations.json if present
            if (animJsonPath != null)
            {
                string animRaw = StripBom(File.ReadAllText(animJsonPath));
                using var animDoc = JsonDocument.Parse(animRaw, new JsonDocumentOptions
                {
                    AllowTrailingCommas = true,
                    CommentHandling = JsonCommentHandling.Skip,
                });

                var root = animDoc.RootElement;
                if (TryGetProp(root, "MD", out var md) || TryGetProp(root, "METADATA", out md))
                    docFps = GetFloatProp(md, "FRT", GetFloatProp(md, "framerate", docFps));
                else
                    docFps = GetFloatProp(root, "FRT", GetFloatProp(root, "framerate", docFps));

                // Inline Symbol Dictionary (SD / SYMBOL_DICTIONARY)
                if ((TryGetProp(root, "SD", out var sd) || TryGetProp(root, "SYMBOL_DICTIONARY", out sd)))
                {
                    if ((TryGetProp(sd, "S", out var symArr) || TryGetProp(sd, "Symbols", out symArr)) && symArr.ValueKind == JsonValueKind.Array)
                    {
                        foreach (var sEl in symArr.EnumerateArray())
                        {
                            if (sEl.ValueKind != JsonValueKind.Object) continue;
                            string sName = NormalizeSymbolKey(GetStringProp(sEl, "SN") ?? GetStringProp(sEl, "SYMBOL_name") ?? GetStringProp(sEl, "name") ?? "");
                            if (string.IsNullOrEmpty(sName)) continue;
                            if (TryGetProp(sEl, "TL", out var tlEl) || TryGetProp(sEl, "TIMELINE", out tlEl))
                                symbols[sName] = ParseTimeline(sName, tlEl);
                        }
                    }
                    else if (sd.ValueKind == JsonValueKind.Array)
                    {
                        foreach (var sEl in sd.EnumerateArray())
                        {
                            if (sEl.ValueKind != JsonValueKind.Object) continue;
                            string sName = NormalizeSymbolKey(GetStringProp(sEl, "SN") ?? GetStringProp(sEl, "SYMBOL_name") ?? GetStringProp(sEl, "name") ?? "");
                            if (string.IsNullOrEmpty(sName)) continue;
                            if (TryGetProp(sEl, "TL", out var tlEl) || TryGetProp(sEl, "TIMELINE", out tlEl))
                                symbols[sName] = ParseTimeline(sName, tlEl);
                        }
                    }
                }

                if (TryGetProp(root, "AN", out var an) || TryGetProp(root, "ANIMATION", out an))
                {
                    if (an.ValueKind == JsonValueKind.Object)
                    {
                        string? anName = GetStringProp(an, "SN") ?? GetStringProp(an, "SYMBOL_name") ?? GetStringProp(an, "N") ?? GetStringProp(an, "name");
                        if (!string.IsNullOrWhiteSpace(anName))
                            animDisplayName = anName!;

                        if (TryGetProp(an, "TL", out var mainTlEl) || TryGetProp(an, "TIMELINE", out mainTlEl))
                            mainTimeline = ParseTimeline("Main", mainTlEl);

                        // Some Adobe Animate / BTA exports put an empty/1-frame TL on AN and reference the real timeline via STI (StageInstance)
                        if ((mainTimeline == null || mainTimeline.TotalFrames <= 1) &&
                            (TryGetProp(an, "STI", out var sti) || TryGetProp(an, "StageInstance", out sti)))
                        {
                            var siEl = (TryGetProp(sti, "SI", out var innerSi) || TryGetProp(sti, "SYMBOL_instance", out innerSi))
                                ? innerSi : sti;
                            string? stageSym = GetStringProp(siEl, "SN") ?? GetStringProp(siEl, "SYMBOL_name");
                            if (!string.IsNullOrEmpty(stageSym) && TryLookupSymbol(symbols, stageSym!, out var symTl))
                            {
                                mainTimeline = symTl;
                                stageRootMatrix = ParseElementMatrix(siEl);
                            }
                        }

                        // Fallback: AN.SN references a symbol stored in LIBRARY/
                        if ((mainTimeline == null || mainTimeline.TotalFrames == 0) &&
                            !string.IsNullOrWhiteSpace(anName) &&
                            TryLookupSymbol(symbols, anName!, out var symByAn))
                        {
                            mainTimeline = symByAn;
                        }
                    }
                }

                // BTA / direct-root timeline exports (Animation.json has TL or L/LAYERS directly at root)
                if (mainTimeline == null || mainTimeline.TotalFrames == 0)
                {
                    if (TryGetProp(root, "TL", out var rootTl) || TryGetProp(root, "TIMELINE", out rootTl))
                        mainTimeline = ParseTimeline("Main", rootTl);
                    else if (TryGetProp(root, "L", out _) || TryGetProp(root, "LAYERS", out _))
                        mainTimeline = ParseTimeline("Main", root);
                }

                // BTA root STI fallback
                if ((mainTimeline == null || mainTimeline.TotalFrames <= 1) &&
                    (TryGetProp(root, "STI", out var rootSti) || TryGetProp(root, "StageInstance", out rootSti)))
                {
                    var siEl = (TryGetProp(rootSti, "SI", out var innerSi) || TryGetProp(rootSti, "SYMBOL_instance", out innerSi))
                        ? innerSi : rootSti;
                    string? stageSym = GetStringProp(siEl, "SN") ?? GetStringProp(siEl, "SYMBOL_name");
                    if (!string.IsNullOrEmpty(stageSym) && TryLookupSymbol(symbols, stageSym!, out var symTl))
                    {
                        mainTimeline = symTl;
                        stageRootMatrix = ParseElementMatrix(siEl);
                    }
                }
            }

            docFps = Math.Clamp(docFps, 1f, 120f);

            if ((mainTimeline == null || mainTimeline.TotalFrames == 0) &&
                !string.IsNullOrWhiteSpace(metaDefaultSymbol) &&
                TryLookupSymbol(symbols, metaDefaultSymbol!, out var metaSymTl))
            {
                mainTimeline = metaSymTl;
            }

            mainTimeline ??= symbols.Values.OrderByDescending(t => t.TotalFrames).FirstOrDefault()
                ?? throw new InvalidOperationException("Spritemap contains no timeline data in Animation.json or LIBRARY/.");

            // Check for optional companion Psych / V-Slice character JSON in this folder or parent folder
            var extCharJson = FindAndParseCompanionJson(dir);

            var project = new CharacterProject
            {
                Name = SparrowCharacterImporter.CleanDisplayName(extCharJson?.Name ?? animDisplayName),
                Slug = CharacterLibrary.SanitizeSlug(extCharJson?.Name ?? animDisplayName),
                SourceKind = CharacterSourceKind.AnimateSpritemap,
                Scale = extCharJson != null && extCharJson.Scale > 0.05f ? Math.Clamp(extCharJson.Scale, 0.1f, 8f) : 1.0f,
                Bilinear = extCharJson == null || !extCharJson.NoAntialiasing,
                NativeFacing = extCharJson != null
                    ? (extCharJson.FlipX ? CharacterFacing.Left : CharacterFacing.Right)
                    : SparrowCharacterImporter.GuessFacingFromName(animDisplayName),
            };

            // Discover clip definitions:
            // 1. Companion JSON animation list (frame labels or symbol names + indices + offsets)
            // 2. Frame labels on mainTimeline
            // 3. Unlabeled pose-switcher keyframes on mainTimeline (preserves shared AN stage alignment!)
            // 4. Multi-frame or named character symbols in SymbolDictionary
            var clipSpecs = new List<(string ClipName, AnimateTimeline Timeline, List<int> Frames, Matrix3x2 RootMatrix, float Fps, bool Loop, Vec2 Offset, bool HasOff)>();
            var addedNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            var labelRanges = ExtractFrameLabelRanges(mainTimeline);

            if (extCharJson != null && extCharJson.Anims.Count > 0)
            {
                foreach (var ca in extCharJson.Anims)
                {
                    if (TryResolveJsonAnimToTimeline(ca.Prefix, ca.Indices, mainTimeline, labelRanges, symbols, out var tl, out var frameIndices))
                    {
                        string cName = string.IsNullOrWhiteSpace(ca.RoleName) ? ca.Prefix : ca.RoleName;
                        if (addedNames.Add(cName))
                        {
                            var rootMtx = ReferenceEquals(tl, mainTimeline) ? stageRootMatrix : Matrix3x2.Identity;
                            clipSpecs.Add((cName, tl, frameIndices, rootMtx, ca.Fps > 0 ? ca.Fps : docFps, ca.Loop, ca.Offset, ca.HasOffset));
                        }
                    }
                }
            }

            foreach (var (label, startFrame, endFrame) in labelRanges)
            {
                if (!addedNames.Add(label)) continue;
                int count = Math.Clamp(endFrame - startFrame + 1, 1, MAX_FRAMES_PER_CLIP);
                var frames = Enumerable.Range(startFrame, count).ToList();
                clipSpecs.Add((label, mainTimeline, frames, stageRootMatrix, docFps, false, new Vec2(), false));
            }

            // If mainTimeline has no frame labels, check if it sequentially places distinct pose symbols
            // (e.g. BTA / Adobe Animate character sheets with Idle, Left, Down, Right, Up placed on AN with stage MX offsets).
            if (labelRanges.Count == 0)
            {
                foreach (var seg in ExtractMainTimelineSymbolSegments(mainTimeline, symbols, stageRootMatrix))
                {
                    if (!addedNames.Add(seg.ClipName)) continue;
                    clipSpecs.Add((seg.ClipName, seg.Timeline, seg.Frames, seg.RootMatrix, docFps, false, new Vec2(), false));
                }
            }

            // Add symbol timelines that look like animations (or all multi-frame symbols if no primary labels/JSON existed)
            bool hasPrimaryClips = clipSpecs.Count > 0;
            foreach (var (symName, symTl) in symbols)
            {
                if (symName.StartsWith("_bta_", StringComparison.OrdinalIgnoreCase)) continue;
                if (symTl.TotalFrames <= 1 && hasPrimaryClips) continue;
                string cleanSym = symName.Contains('/') ? symName.Substring(symName.LastIndexOf('/') + 1) : symName;
                if (cleanSym.StartsWith("_bta_", StringComparison.OrdinalIgnoreCase)) continue;
                if (hasPrimaryClips && !LooksLikeCharacterAnimSymbol(cleanSym))
                    continue;
                if (!hasPrimaryClips && symTl.TotalFrames <= 1 && !LooksLikeCharacterAnimSymbol(cleanSym))
                    continue;
                if (!addedNames.Add(cleanSym)) continue;

                int count = Math.Clamp(symTl.TotalFrames, 1, MAX_FRAMES_PER_CLIP);
                var frames = Enumerable.Range(0, count).ToList();
                clipSpecs.Add((cleanSym, symTl, frames, Matrix3x2.Identity, docFps, false, new Vec2(), false));
            }

            // Ultimate fallback: whole main timeline
            if (clipSpecs.Count == 0 && mainTimeline.TotalFrames > 0)
            {
                int count = Math.Clamp(mainTimeline.TotalFrames, 1, MAX_FRAMES_PER_CLIP);
                clipSpecs.Add(("Main Timeline", mainTimeline, Enumerable.Range(0, count).ToList(), stageRootMatrix, docFps, true, new Vec2(), false));
            }

            // Compute a shared raster scale if the rig's raw limb resolution is huge (> MAX_RASTER_DIMENSION)
            float globalRasterScale = ComputeSafeRasterScale(clipSpecs, symbols, limbs);
            if (globalRasterScale < 0.999f)
            {
                // Compensate project scale so stage world size remains faithful
                project.Scale = MathF.Round(project.Scale / globalRasterScale, 3);
            }

            try
            {
                foreach (var spec in clipSpecs)
                {
                    var clip = RasterizeClip(spec.ClipName, spec.Timeline, spec.Frames, symbols, limbs, spec.RootMatrix, globalRasterScale, project.Bilinear);
                    if (clip != null && clip.Frames.Count > 0)
                    {
                        clip.SuggestedFps = spec.Fps;
                        clip.SuggestedLoop = spec.Loop;
                        clip.ImportedOffset = new Vec2
                        {
                            X = spec.Offset.X * globalRasterScale,
                            Y = spec.Offset.Y * globalRasterScale,
                        };
                        clip.HasImportedOffset = spec.HasOff;
                        project.AvailableClips.Add(clip);
                    }
                }

                if (project.AvailableClips.Count == 0)
                    throw new InvalidOperationException("Spritemap timelines contained no renderable limb frames.");

                SparrowCharacterImporter.AutoMapCharacterSlots(project);
                SparrowCharacterImporter.TryAutoLoadNearbyIcon(project, dir, extCharJson?.HealthIcon, project.Slug);
                return project;
            }
            catch
            {
                project.Dispose();
                throw;
            }
        }
        finally
        {
            foreach (var l in limbs.Values)
                l.Dispose();
        }
    }

    /// <summary>
    /// Loads external per-symbol timeline JSON files from a BetterTextureAtlas / Adobe Animate <c>LIBRARY/</c> folder.
    /// Each <c>LIBRARY/path/to/Symbol.json</c> registers symbol name <c>path/to/Symbol</c>.
    /// </summary>
    private static void LoadLibraryFolderSymbols(string libraryDir, Dictionary<string, AnimateTimeline> symbols)
    {
        foreach (var file in Directory.EnumerateFiles(libraryDir, "*", SearchOption.AllDirectories))
        {
            if (!string.Equals(Path.GetExtension(file), ".json", StringComparison.OrdinalIgnoreCase))
                continue;
            try
            {
                string rel = Path.GetRelativePath(libraryDir, file);
                string relNoExt = NormalizeSymbolKey(Path.ChangeExtension(rel, null) ?? Path.GetFileNameWithoutExtension(file));
                if (string.IsNullOrEmpty(relNoExt)) continue;

                using var doc = JsonDocument.Parse(StripBom(File.ReadAllText(file)), new JsonDocumentOptions
                {
                    AllowTrailingCommas = true,
                    CommentHandling = JsonCommentHandling.Skip,
                });
                var root = doc.RootElement;
                if (root.ValueKind != JsonValueKind.Object) continue;

                string explicitName = NormalizeSymbolKey(GetStringProp(root, "SN") ?? GetStringProp(root, "SYMBOL_name") ?? "");
                string symKey = !string.IsNullOrEmpty(explicitName) ? explicitName : relNoExt;

                AnimateTimeline? tl = null;
                if (TryGetProp(root, "TL", out var tlEl) || TryGetProp(root, "TIMELINE", out tlEl))
                    tl = ParseTimeline(symKey, tlEl);
                else if (TryGetProp(root, "L", out _) || TryGetProp(root, "LAYERS", out _))
                    tl = ParseTimeline(symKey, root);

                if (tl != null)
                {
                    symbols[symKey] = tl;
                    if (!string.Equals(symKey, relNoExt, StringComparison.OrdinalIgnoreCase) && !symbols.ContainsKey(relNoExt))
                        symbols[relNoExt] = tl;
                }
            }
            catch { }
        }
    }

    private static bool TryLookupSymbol(Dictionary<string, AnimateTimeline> symbols, string rawName, out AnimateTimeline timeline)
    {
        string norm = NormalizeSymbolKey(rawName);
        if (symbols.TryGetValue(norm, out timeline!))
            return true;

        // Strip leading "LIBRARY/" prefix if the exporter included it
        if (norm.StartsWith("LIBRARY/", StringComparison.OrdinalIgnoreCase) &&
            symbols.TryGetValue(norm.Substring(8), out timeline!))
            return true;

        // Fallback: match by leaf name if unambiguous
        string leaf = norm.Contains('/') ? norm.Substring(norm.LastIndexOf('/') + 1) : norm;
        foreach (var kvp in symbols)
        {
            string kLeaf = kvp.Key.Contains('/') ? kvp.Key.Substring(kvp.Key.LastIndexOf('/') + 1) : kvp.Key;
            if (string.Equals(kLeaf, leaf, StringComparison.OrdinalIgnoreCase))
            {
                timeline = kvp.Value;
                return true;
            }
        }
        timeline = null!;
        return false;
    }

    private static float ComputeSafeRasterScale(
        List<(string ClipName, AnimateTimeline Timeline, List<int> Frames, Matrix3x2 RootMatrix, float Fps, bool Loop, Vec2 Offset, bool HasOff)> specs,
        Dictionary<string, AnimateTimeline> symbols,
        Dictionary<string, LimbSprite> limbs)
    {
        float maxDim = 1f;
        var sampleCmds = new List<DrawCmd>(64);
        foreach (var spec in specs.Take(6))
        {
            if (spec.Frames.Count == 0) continue;
            sampleCmds.Clear();
            CollectFrameCommands(spec.Timeline, spec.Frames[0], spec.RootMatrix, Vector4.One, Vector4.Zero, symbols, limbs, sampleCmds, 0);
            if (TryComputeBounds(sampleCmds, out float minX, out float minY, out float maxX, out float maxY))
            {
                maxDim = MathF.Max(maxDim, MathF.Max(maxX - minX, maxY - minY));
            }
        }
        return maxDim > MAX_RASTER_DIMENSION ? (MAX_RASTER_DIMENSION / maxDim) : 1f;
    }

    private static ImportedAnimClip? RasterizeClip(
        string clipName,
        AnimateTimeline timeline,
        List<int> frameIndices,
        Dictionary<string, AnimateTimeline> symbols,
        Dictionary<string, LimbSprite> limbs,
        Matrix3x2 rootMatrix,
        float rasterScale,
        bool bilinear)
    {
        var scaleMtx = Matrix3x2.CreateScale(rasterScale);
        var effectiveRoot = rootMatrix * scaleMtx;

        var perFrameCmds = new List<List<DrawCmd>>(frameIndices.Count);
        float clipMinX = float.MaxValue, clipMinY = float.MaxValue;
        float clipMaxX = float.MinValue, clipMaxY = float.MinValue;
        bool anyVisible = false;

        foreach (int f in frameIndices)
        {
            var cmds = new List<DrawCmd>(32);
            CollectFrameCommands(timeline, f, effectiveRoot, Vector4.One, Vector4.Zero, symbols, limbs, cmds, 0);
            perFrameCmds.Add(cmds);

            if (TryComputeBounds(cmds, out float fMinX, out float fMinY, out float fMaxX, out float fMaxY))
            {
                clipMinX = MathF.Min(clipMinX, fMinX);
                clipMinY = MathF.Min(clipMinY, fMinY);
                clipMaxX = MathF.Max(clipMaxX, fMaxX);
                clipMaxY = MathF.Max(clipMaxY, fMaxY);
                anyVisible = true;
            }
        }

        if (!anyVisible) return null;

        int originX = (int)MathF.Floor(clipMinX) - 1;
        int originY = (int)MathF.Floor(clipMinY) - 1;
        int canvasW = Math.Clamp((int)MathF.Ceiling(clipMaxX) - originX + 1, 1, 2048);
        int canvasH = Math.Clamp((int)MathF.Ceiling(clipMaxY) - originY + 1, 1, 2048);

        var renderedFrames = new Image<Rgba32>[perFrameCmds.Count];
        // On 32-bit Android keep rasterization single-threaded to avoid concurrent multi-MB LOH canvas spikes
        int maxDop = OperatingSystem.IsAndroid()
            ? (Environment.Is64BitProcess ? 2 : 1)
            : Math.Clamp(Environment.ProcessorCount, 1, 4);

        try
        {
            Parallel.For(0, perFrameCmds.Count, new ParallelOptions { MaxDegreeOfParallelism = maxDop }, idx =>
            {
                var canvas = new Image<Rgba32>(canvasW, canvasH);
                var cmds = perFrameCmds[idx];
                var shift = Matrix3x2.CreateTranslation(-originX, -originY);

                for (int c = 0; c < cmds.Count; c++)
                {
                    var cmd = cmds[c];
                    var m = cmd.WorldMatrix * shift;
                    RasterizeLimb(canvas, cmd.Limb.Image, m, cmd.ColorMult, cmd.ColorOffset, bilinear);
                }
                renderedFrames[idx] = canvas;
            });
        }
        catch
        {
            for (int i = 0; i < renderedFrames.Length; i++)
                try { renderedFrames[i]?.Dispose(); } catch { }
            throw;
        }

        return new ImportedAnimClip
        {
            Name = clipName,
            Frames = renderedFrames.ToList(),
            CanvasWidth = canvasW,
            CanvasHeight = canvasH,
            WorldMinX = originX,
            WorldMinY = originY,
        };
    }

    private static void CollectFrameCommands(
        AnimateTimeline timeline,
        int frameIndex,
        Matrix3x2 parentMatrix,
        Vector4 parentColorMult,
        Vector4 parentColorOff,
        Dictionary<string, AnimateTimeline> symbols,
        Dictionary<string, LimbSprite> limbs,
        List<DrawCmd> output,
        int depth)
    {
        if (depth > MAX_RECURSION_DEPTH || timeline.Layers.Count == 0)
            return;

        // Adobe Animate lists layers top-to-bottom; draw in reverse (bottom-to-top) so upper layers composite over lower ones!
        for (int li = timeline.Layers.Count - 1; li >= 0; li--)
        {
            var layer = timeline.Layers[li];
            var kf = FindActiveKeyframe(layer, frameIndex);
            if (kf == null || kf.Elements.Count == 0) continue;

            int offsetInKeyframe = Math.Max(0, frameIndex - kf.Index);
            for (int ei = 0; ei < kf.Elements.Count; ei++)
            {
                var el = kf.Elements[ei];
                var worldMtx = el.Matrix * parentMatrix;
                var colMult = parentColorMult * el.ColorMult;
                var colOff = parentColorMult * el.ColorOffset + parentColorOff;

                if (colMult.W <= 0.002f) continue;

                if (!el.IsSymbol)
                {
                    if (limbs.TryGetValue(el.Name, out var limb))
                    {
                        var limbMtx = MathF.Abs(limb.Resolution - 1f) > 0.001f
                            ? Matrix3x2.CreateScale(1f / limb.Resolution) * worldMtx
                            : worldMtx;
                        output.Add(new DrawCmd(limb, limbMtx, colMult, colOff));
                    }
                }
                else if (TryLookupSymbol(symbols, el.Name, out var childTl) && childTl.TotalFrames > 0)
                {
                    int childFrame = ResolveChildFrame(el, offsetInKeyframe, childTl.TotalFrames);
                    CollectFrameCommands(childTl, childFrame, worldMtx, colMult, colOff, symbols, limbs, output, depth + 1);
                }
            }
        }
    }

    private static int ResolveChildFrame(AnimateElement el, int offsetInKeyframe, int totalFrames)
    {
        if (totalFrames <= 1) return 0;
        if (string.Equals(el.LoopMode, "SF", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(el.LoopMode, "singleframe", StringComparison.OrdinalIgnoreCase))
        {
            return Math.Clamp(el.FirstFrame, 0, totalFrames - 1);
        }
        if (string.Equals(el.LoopMode, "PO", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(el.LoopMode, "playonce", StringComparison.OrdinalIgnoreCase))
        {
            return Math.Clamp(el.FirstFrame + offsetInKeyframe, 0, totalFrames - 1);
        }
        if (string.Equals(el.LoopMode, "POR", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(el.LoopMode, "playoncereverse", StringComparison.OrdinalIgnoreCase))
        {
            return Math.Clamp(el.FirstFrame - offsetInKeyframe, 0, totalFrames - 1);
        }
        if (string.Equals(el.LoopMode, "LPR", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(el.LoopMode, "loopreverse", StringComparison.OrdinalIgnoreCase))
        {
            int fr = (el.FirstFrame - offsetInKeyframe) % totalFrames;
            return fr < 0 ? fr + totalFrames : fr;
        }
        // Default: LP (loop)
        int f = (el.FirstFrame + offsetInKeyframe) % totalFrames;
        return f < 0 ? f + totalFrames : f;
    }

    private static AnimateKeyframe? FindActiveKeyframe(AnimateLayer layer, int frameIndex)
    {
        var frames = layer.Frames;
        int lo = 0, hi = frames.Count - 1;
        while (lo <= hi)
        {
            int mid = (lo + hi) >> 1;
            var kf = frames[mid];
            if (frameIndex < kf.Index)
                hi = mid - 1;
            else if (frameIndex >= kf.Index + kf.Duration)
                lo = mid + 1;
            else
                return kf;
        }
        return null;
    }

    private static bool TryComputeBounds(List<DrawCmd> cmds, out float minX, out float minY, out float maxX, out float maxY)
    {
        minX = float.MaxValue; minY = float.MaxValue;
        maxX = float.MinValue; maxY = float.MinValue;
        bool any = false;

        for (int i = 0; i < cmds.Count; i++)
        {
            var cmd = cmds[i];
            float w = cmd.Limb.Width;
            float h = cmd.Limb.Height;
            var m = cmd.WorldMatrix;

            Vector2 p0 = Vector2.Transform(new Vector2(0, 0), m);
            Vector2 p1 = Vector2.Transform(new Vector2(w, 0), m);
            Vector2 p2 = Vector2.Transform(new Vector2(0, h), m);
            Vector2 p3 = Vector2.Transform(new Vector2(w, h), m);
            if (!float.IsFinite(p0.X) || !float.IsFinite(p0.Y) || !float.IsFinite(p1.X) || !float.IsFinite(p1.Y) ||
                !float.IsFinite(p2.X) || !float.IsFinite(p2.Y) || !float.IsFinite(p3.X) || !float.IsFinite(p3.Y))
                continue;

            minX = MathF.Min(minX, MathF.Min(MathF.Min(p0.X, p1.X), MathF.Min(p2.X, p3.X)));
            minY = MathF.Min(minY, MathF.Min(MathF.Min(p0.Y, p1.Y), MathF.Min(p2.Y, p3.Y)));
            maxX = MathF.Max(maxX, MathF.Max(MathF.Max(p0.X, p1.X), MathF.Max(p2.X, p3.X)));
            maxY = MathF.Max(maxY, MathF.Max(MathF.Max(p0.Y, p1.Y), MathF.Max(p2.Y, p3.Y)));
            any = true;
        }
        return any;
    }

    private static void RasterizeLimb(
        Image<Rgba32> dst,
        Image<Rgba32> src,
        Matrix3x2 worldMatrix,
        Vector4 colMult,
        Vector4 colOff,
        bool bilinear)
    {
        if (!float.IsFinite(worldMatrix.M11) || !float.IsFinite(worldMatrix.M12) ||
            !float.IsFinite(worldMatrix.M21) || !float.IsFinite(worldMatrix.M22) ||
            !float.IsFinite(worldMatrix.M31) || !float.IsFinite(worldMatrix.M32) ||
            !Matrix3x2.Invert(worldMatrix, out var inv) ||
            !float.IsFinite(inv.M11) || !float.IsFinite(inv.M12) ||
            !float.IsFinite(inv.M21) || !float.IsFinite(inv.M22) ||
            !float.IsFinite(inv.M31) || !float.IsFinite(inv.M32))
            return;

        float sw = src.Width;
        float sh = src.Height;
        Vector2 p0 = Vector2.Transform(new Vector2(0, 0), worldMatrix);
        Vector2 p1 = Vector2.Transform(new Vector2(sw, 0), worldMatrix);
        Vector2 p2 = Vector2.Transform(new Vector2(0, sh), worldMatrix);
        Vector2 p3 = Vector2.Transform(new Vector2(sw, sh), worldMatrix);

        float fMinX = MathF.Min(MathF.Min(p0.X, p1.X), MathF.Min(p2.X, p3.X));
        float fMinY = MathF.Min(MathF.Min(p0.Y, p1.Y), MathF.Min(p2.Y, p3.Y));
        float fMaxX = MathF.Max(MathF.Max(p0.X, p1.X), MathF.Max(p2.X, p3.X));
        float fMaxY = MathF.Max(MathF.Max(p0.Y, p1.Y), MathF.Max(p2.Y, p3.Y));
        if (!float.IsFinite(fMinX) || !float.IsFinite(fMinY) || !float.IsFinite(fMaxX) || !float.IsFinite(fMaxY))
            return;

        int x0 = (int)Math.Clamp(MathF.Floor(fMinX) - 1f, 0f, dst.Width);
        int y0 = (int)Math.Clamp(MathF.Floor(fMinY) - 1f, 0f, dst.Height);
        int x1 = (int)Math.Clamp(MathF.Ceiling(fMaxX) + 1f, -1f, dst.Width - 1);
        int y1 = (int)Math.Clamp(MathF.Ceiling(fMaxY) + 1f, -1f, dst.Height - 1);
        if (x1 < x0 || y1 < y0) return;

        bool hasColorTransform = colMult != Vector4.One || colOff != Vector4.Zero;

        src.ProcessPixelRows(dst, (srcAcc, dstAcc) =>
        {
            int srcW = srcAcc.Width;
            int srcH = srcAcc.Height;

            for (int y = y0; y <= y1; y++)
            {
                var dstRow = dstAcc.GetRowSpan(y);
                float py = y + 0.5f;

                for (int x = x0; x <= x1; x++)
                {
                    float px = x + 0.5f;
                    float u = px * inv.M11 + py * inv.M21 + inv.M31 - 0.5f;
                    float v = px * inv.M12 + py * inv.M22 + inv.M32 - 0.5f;

                    if (u < -0.5f || v < -0.5f || u > srcW - 0.5f || v > srcH - 0.5f)
                        continue;

                    Rgba32 sample;
                    if (!bilinear)
                    {
                        int ix = Math.Clamp((int)MathF.Round(u), 0, srcW - 1);
                        int iy = Math.Clamp((int)MathF.Round(v), 0, srcH - 1);
                        sample = srcAcc.GetRowSpan(iy)[ix];
                    }
                    else
                    {
                        int ix0 = (int)MathF.Floor(u);
                        int iy0 = (int)MathF.Floor(v);
                        float fx = u - ix0;
                        float fy = v - iy0;

                        int cx0 = Math.Clamp(ix0, 0, srcW - 1);
                        int cx1 = Math.Clamp(ix0 + 1, 0, srcW - 1);
                        int cy0 = Math.Clamp(iy0, 0, srcH - 1);
                        int cy1 = Math.Clamp(iy0 + 1, 0, srcH - 1);

                        var row0 = srcAcc.GetRowSpan(cy0);
                        var row1 = srcAcc.GetRowSpan(cy1);
                        Rgba32 c00 = row0[cx0], c10 = row0[cx1];
                        Rgba32 c01 = row1[cx0], c11 = row1[cx1];

                        // Premultiplied bilinear interpolation prevents dark fringes around limb edges!
                        float w00 = (1f - fx) * (1f - fy);
                        float w10 = fx * (1f - fy);
                        float w01 = (1f - fx) * fy;
                        float w11 = fx * fy;

                        float a00 = c00.A * w00, a10 = c10.A * w10, a01 = c01.A * w01, a11 = c11.A * w11;
                        float outA = a00 + a10 + a01 + a11;
                        if (outA < 1f) continue;

                        float invA = 1f / outA;
                        byte r = (byte)Math.Clamp((c00.R * a00 + c10.R * a10 + c01.R * a01 + c11.R * a11) * invA, 0f, 255f);
                        byte g = (byte)Math.Clamp((c00.G * a00 + c10.G * a10 + c01.G * a01 + c11.G * a11) * invA, 0f, 255f);
                        byte b = (byte)Math.Clamp((c00.B * a00 + c10.B * a10 + c01.B * a01 + c11.B * a11) * invA, 0f, 255f);
                        sample = new Rgba32(r, g, b, (byte)Math.Clamp(outA, 0f, 255f));
                    }

                    if (sample.A == 0) continue;

                    if (hasColorTransform)
                    {
                        sample = new Rgba32(
                            (byte)Math.Clamp(sample.R * colMult.X + colOff.X, 0f, 255f),
                            (byte)Math.Clamp(sample.G * colMult.Y + colOff.Y, 0f, 255f),
                            (byte)Math.Clamp(sample.B * colMult.Z + colOff.Z, 0f, 255f),
                            (byte)Math.Clamp(sample.A * colMult.W + colOff.W, 0f, 255f));
                        if (sample.A == 0) continue;
                    }

                    // Straight-alpha source-over blend onto dstRow[x]
                    Rgba32 bg = dstRow[x];
                    if (bg.A == 0 || sample.A == 255)
                    {
                        dstRow[x] = sample;
                    }
                    else
                    {
                        float sa = sample.A / 255f;
                        float ba = bg.A / 255f;
                        float oa = sa + ba * (1f - sa);
                        if (oa <= 0.001f) continue;
                        float invOa = 1f / oa;
                        dstRow[x] = new Rgba32(
                            (byte)Math.Clamp((sample.R * sa + bg.R * ba * (1f - sa)) * invOa, 0f, 255f),
                            (byte)Math.Clamp((sample.G * sa + bg.G * ba * (1f - sa)) * invOa, 0f, 255f),
                            (byte)Math.Clamp((sample.B * sa + bg.B * ba * (1f - sa)) * invOa, 0f, 255f),
                            (byte)Math.Clamp(oa * 255f, 0f, 255f));
                    }
                }
            }
        });
    }

    private static void LoadSpritemapSheet(string smJsonPath, Dictionary<string, LimbSprite> limbs)
    {
        string dir = Path.GetDirectoryName(smJsonPath) ?? "";
        string raw = StripBom(File.ReadAllText(smJsonPath));
        using var doc = JsonDocument.Parse(raw, new JsonDocumentOptions
        {
            AllowTrailingCommas = true,
            CommentHandling = JsonCommentHandling.Skip,
        });

        var root = doc.RootElement;
        string? imgName = null;
        float resolution = 1f;
        if (TryGetProp(root, "meta", out var meta))
        {
            imgName = GetStringProp(meta, "image");
            resolution = ParseResolutionProp(meta);
        }

        string stem = Path.GetFileNameWithoutExtension(smJsonPath);
        static string? ResolveFileIgnoreCase(string baseDir, string fileName)
        {
            string direct = Path.Combine(baseDir, fileName);
            if (File.Exists(direct)) return direct;
            if (!Directory.Exists(baseDir)) return null;
            return Directory.EnumerateFiles(baseDir, "*", SearchOption.TopDirectoryOnly)
                .FirstOrDefault(f => string.Equals(Path.GetFileName(f), fileName, StringComparison.OrdinalIgnoreCase));
        }

        string? pngPath = (!string.IsNullOrWhiteSpace(imgName) ? ResolveFileIgnoreCase(dir, Path.GetFileName(imgName!.Replace('\\', '/'))) : null)
            ?? ResolveFileIgnoreCase(dir, stem + ".png");

        if (string.IsNullOrEmpty(pngPath) || !File.Exists(pngPath)) return;

        using var sheet = Image.Load<Rgba32>(pngPath);

        if (TryGetProp(root, "ATLAS", out var atlas) &&
            TryGetProp(atlas, "SPRITES", out var spritesArr) &&
            spritesArr.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in spritesArr.EnumerateArray())
            {
                var s = TryGetProp(item, "SPRITE", out var inner) ? inner : item;
                if (s.ValueKind != JsonValueKind.Object) continue;

                string? name = GetStringProp(s, "name");
                if (string.IsNullOrEmpty(name) || limbs.ContainsKey(name!)) continue;

                AddCroppedLimb(sheet, limbs, name!,
                    (int)GetFloatProp(s, "x", 0f),
                    (int)GetFloatProp(s, "y", 0f),
                    (int)GetFloatProp(s, "w", GetFloatProp(s, "width", 0f)),
                    (int)GetFloatProp(s, "h", GetFloatProp(s, "height", 0f)),
                    GetBoolProp(s, "rotated"),
                    resolution);
            }
            return;
        }

        // Fallback: TexturePacker / JSON-Hash / JSON-Array "frames" format
        if (TryGetProp(root, "frames", out var framesEl))
        {
            if (framesEl.ValueKind == JsonValueKind.Object)
            {
                foreach (var prop in framesEl.EnumerateObject())
                {
                    if (prop.Value.ValueKind != JsonValueKind.Object || limbs.ContainsKey(prop.Name)) continue;
                    var f = TryGetProp(prop.Value, "frame", out var fr) ? fr : prop.Value;
                    AddCroppedLimb(sheet, limbs, prop.Name,
                        (int)GetFloatProp(f, "x", 0f),
                        (int)GetFloatProp(f, "y", 0f),
                        (int)GetFloatProp(f, "w", GetFloatProp(f, "width", 0f)),
                        (int)GetFloatProp(f, "h", GetFloatProp(f, "height", 0f)),
                        GetBoolProp(prop.Value, "rotated"),
                        resolution);
                }
            }
            else if (framesEl.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in framesEl.EnumerateArray())
                {
                    if (item.ValueKind != JsonValueKind.Object) continue;
                    string? name = GetStringProp(item, "filename") ?? GetStringProp(item, "name");
                    if (string.IsNullOrEmpty(name) || limbs.ContainsKey(name!)) continue;
                    var f = TryGetProp(item, "frame", out var fr) ? fr : item;
                    AddCroppedLimb(sheet, limbs, name!,
                        (int)GetFloatProp(f, "x", 0f),
                        (int)GetFloatProp(f, "y", 0f),
                        (int)GetFloatProp(f, "w", GetFloatProp(f, "width", 0f)),
                        (int)GetFloatProp(f, "h", GetFloatProp(f, "height", 0f)),
                        GetBoolProp(item, "rotated"),
                        resolution);
                }
            }
        }
    }

    private static float ParseResolutionProp(JsonElement meta)
    {
        if (TryGetProp(meta, "resolution", out var rEl) || TryGetProp(meta, "scale", out rEl))
        {
            if (rEl.ValueKind == JsonValueKind.Number)
            {
                float v = (float)rEl.GetDouble();
                if (v > 0.05f && v < 16f) return v;
            }
            else if (rEl.ValueKind == JsonValueKind.String &&
                     float.TryParse(rEl.GetString(), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out float parsed) &&
                     parsed > 0.05f && parsed < 16f)
            {
                return parsed;
            }
        }
        return 1f;
    }

    private static void AddCroppedLimb(
        Image<Rgba32> sheet,
        Dictionary<string, LimbSprite> limbs,
        string name,
        int x, int y, int w, int h,
        bool rotated,
        float resolution)
    {
        int sx = Math.Clamp(x, 0, Math.Max(0, sheet.Width - 1));
        int sy = Math.Clamp(y, 0, Math.Max(0, sheet.Height - 1));
        int sw = Math.Clamp(w, 0, sheet.Width - sx);
        int sh = Math.Clamp(h, 0, sheet.Height - sy);
        if (sw <= 0 || sh <= 0) return;

        var limbImg = sheet.Clone(ctx =>
        {
            ctx.Crop(new Rectangle(sx, sy, sw, sh));
            if (rotated)
                ctx.Rotate(RotateMode.Rotate270);
        });

        limbs[name] = new LimbSprite { Name = name, Image = limbImg, Resolution = resolution };
    }

    private static AnimateTimeline ParseTimeline(string name, JsonElement tlEl)
    {
        var tl = new AnimateTimeline { Name = name };
        if (!(TryGetProp(tlEl, "L", out var layersEl) || TryGetProp(tlEl, "LAYERS", out layersEl)) ||
            layersEl.ValueKind != JsonValueKind.Array)
            return tl;

        int maxFrames = 0;
        foreach (var lEl in layersEl.EnumerateArray())
        {
            if (lEl.ValueKind != JsonValueKind.Object) continue;
            // Skip mask/clipper layers (LT = "Clp" / "Clipper") so mask shapes don't paint solid blobs over the rig
            string? layerType = GetStringProp(lEl, "LT") ?? GetStringProp(lEl, "Layer_type");
            if (string.Equals(layerType, "Clp", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(layerType, "Clipper", StringComparison.OrdinalIgnoreCase))
                continue;

            var layer = new AnimateLayer
            {
                Name = GetStringProp(lEl, "LN") ?? GetStringProp(lEl, "Layer_name") ?? "",
            };

            if ((TryGetProp(lEl, "FR", out var framesEl) || TryGetProp(lEl, "Frames", out framesEl)) &&
                framesEl.ValueKind == JsonValueKind.Array)
            {
                foreach (var fEl in framesEl.EnumerateArray())
                {
                    if (fEl.ValueKind != JsonValueKind.Object) continue;
                    int idx = (int)GetFloatProp(fEl, "I", GetFloatProp(fEl, "index", 0f));
                    int dur = Math.Max(1, (int)GetFloatProp(fEl, "DU", GetFloatProp(fEl, "duration", 1f)));
                    string? label = GetStringProp(fEl, "N") ?? GetStringProp(fEl, "name");

                    var kf = new AnimateKeyframe
                    {
                        Index = idx,
                        Duration = dur,
                        Label = string.IsNullOrWhiteSpace(label) ? null : label!.Trim(),
                    };

                    if ((TryGetProp(fEl, "E", out var elemsEl) || TryGetProp(fEl, "elements", out elemsEl)) &&
                        elemsEl.ValueKind == JsonValueKind.Array)
                    {
                        foreach (var eWrap in elemsEl.EnumerateArray())
                        {
                            if (eWrap.ValueKind != JsonValueKind.Object) continue;
                            if (TryGetProp(eWrap, "ASI", out var asi) || TryGetProp(eWrap, "ATLAS_SPRITE_instance", out asi))
                            {
                                string? limbName = GetStringProp(asi, "N") ?? GetStringProp(asi, "name");
                                if (!string.IsNullOrEmpty(limbName))
                                {
                                    var elem = new AnimateElement
                                    {
                                        IsSymbol = false,
                                        Name = limbName!,
                                        Matrix = ParseElementMatrix(asi),
                                    };
                                    ParseColorEffect(asi, out elem.ColorMult, out elem.ColorOffset);
                                    kf.Elements.Add(elem);
                                }
                            }
                            else if (TryGetProp(eWrap, "SI", out var si) || TryGetProp(eWrap, "SYMBOL_instance", out si))
                            {
                                string? symName = GetStringProp(si, "SN") ?? GetStringProp(si, "SYMBOL_name");
                                if (!string.IsNullOrEmpty(symName))
                                {
                                    string? symType = GetStringProp(si, "ST") ?? GetStringProp(si, "symbolType");
                                    string? explicitLoop = GetStringProp(si, "LP") ?? GetStringProp(si, "loop");
                                    // In Adobe Animate's timeline and flxanimate/flixel-animate (default swfMode=false),
                                    // MovieClip ("MC") and Button ("B") symbol instances do NOT scrub/advance with the parent
                                    // timeline — they hold frame 0 (Single Frame) unless an explicit loop mode was authored.
                                    // Artists routinely leave part-bank symbols (Body, Hands, Nose) as MC when using frame 0!
                                    bool isStaticUnlessLooped =
                                        string.Equals(symType, "MC", StringComparison.OrdinalIgnoreCase) ||
                                        string.Equals(symType, "movieclip", StringComparison.OrdinalIgnoreCase) ||
                                        string.Equals(symType, "B", StringComparison.OrdinalIgnoreCase) ||
                                        string.Equals(symType, "button", StringComparison.OrdinalIgnoreCase);

                                    var elem = new AnimateElement
                                    {
                                        IsSymbol = true,
                                        Name = symName!,
                                        SymbolType = symType ?? "G",
                                        Matrix = ParseElementMatrix(si),
                                        LoopMode = explicitLoop ?? (isStaticUnlessLooped ? "SF" : "LP"),
                                        FirstFrame = (int)GetFloatProp(si, "FF", GetFloatProp(si, "firstFrame", 0f)),
                                    };
                                    ParseColorEffect(si, out elem.ColorMult, out elem.ColorOffset);
                                    kf.Elements.Add(elem);
                                }
                            }
                        }
                    }

                    layer.Frames.Add(kf);
                    layer.TotalFrames = Math.Max(layer.TotalFrames, idx + dur);
                }
            }

            layer.Frames.Sort((a, b) => a.Index.CompareTo(b.Index));
            maxFrames = Math.Max(maxFrames, layer.TotalFrames);
            tl.Layers.Add(layer);
        }

        tl.TotalFrames = maxFrames;
        return tl;
    }

    private static Matrix3x2 ParseElementMatrix(JsonElement inst)
    {
        // 1. 2D Matrix (MX / Matrix) — 6-float [a, b, c, d, tx, ty], 16-float M3D, or object {a,b,c,d,tx,ty}
        if (TryGetProp(inst, "MX", out var mx) || TryGetProp(inst, "Matrix", out mx))
        {
            if (mx.ValueKind == JsonValueKind.Array)
            {
                Span<float> v = stackalloc float[16];
                int n = 0;
                foreach (var el in mx.EnumerateArray())
                {
                    if (n >= 16) break;
                    v[n++] = el.ValueKind == JsonValueKind.Number ? (float)el.GetDouble() : 0f;
                }
                if (n >= 14)
                    return new Matrix3x2(v[0], v[1], v[4], v[5], v[12], v[13]);
                if (n >= 6)
                    return new Matrix3x2(v[0], v[1], v[2], v[3], v[4], v[5]);
            }
            else if (mx.ValueKind == JsonValueKind.Object)
            {
                return new Matrix3x2(
                    GetFloatProp(mx, "a", 1f),
                    GetFloatProp(mx, "b", 0f),
                    GetFloatProp(mx, "c", 0f),
                    GetFloatProp(mx, "d", 1f),
                    GetFloatProp(mx, "tx", 0f),
                    GetFloatProp(mx, "ty", 0f));
            }
        }

        // 2. Matrix3D (M3D / Matrix3D) — supports 16-float Adobe 4x4, 6-float BTA 2D shorthand, or object {m00..m31}
        if (TryGetProp(inst, "M3D", out var m3d) || TryGetProp(inst, "Matrix3D", out m3d))
        {
            if (m3d.ValueKind == JsonValueKind.Array)
            {
                Span<float> v = stackalloc float[16];
                int n = 0;
                foreach (var el in m3d.EnumerateArray())
                {
                    if (n >= 16) break;
                    v[n++] = el.ValueKind == JsonValueKind.Number ? (float)el.GetDouble() : 0f;
                }
                if (n >= 14)
                    return new Matrix3x2(v[0], v[1], v[4], v[5], v[12], v[13]);
                if (n >= 6)
                    return new Matrix3x2(v[0], v[1], v[2], v[3], v[4], v[5]);
            }
            else if (m3d.ValueKind == JsonValueKind.Object)
            {
                return new Matrix3x2(
                    GetFloatProp(m3d, "m00", 1f),
                    GetFloatProp(m3d, "m01", 0f),
                    GetFloatProp(m3d, "m10", 0f),
                    GetFloatProp(m3d, "m11", 1f),
                    GetFloatProp(m3d, "m30", 0f),
                    GetFloatProp(m3d, "m31", 0f));
            }
        }

        // 3. Position-only shorthand (POS / Position) — emitted by Adobe Animate & BTA for unrotated ASI limbs!
        if (TryGetProp(inst, "POS", out var pos) || TryGetProp(inst, "Position", out pos))
        {
            if (pos.ValueKind == JsonValueKind.Object)
                return Matrix3x2.CreateTranslation(GetFloatProp(pos, "x", 0f), GetFloatProp(pos, "y", 0f));
            if (pos.ValueKind == JsonValueKind.Array)
            {
                Span<float> p = stackalloc float[2];
                int n = 0;
                foreach (var el in pos.EnumerateArray())
                {
                    if (n >= 2) break;
                    p[n++] = el.ValueKind == JsonValueKind.Number ? (float)el.GetDouble() : 0f;
                }
                if (n >= 2)
                    return Matrix3x2.CreateTranslation(p[0], p[1]);
            }
        }

        // 4. DecomposedMatrix fallback
        if (TryGetProp(inst, "DecomposedMatrix", out var dm) && dm.ValueKind == JsonValueKind.Object)
        {
            float tx = 0f, ty = 0f, sx = 1f, sy = 1f, rotZ = 0f;
            if (TryGetProp(dm, "Position", out var dPos) && dPos.ValueKind == JsonValueKind.Object)
            {
                tx = GetFloatProp(dPos, "x", 0f);
                ty = GetFloatProp(dPos, "y", 0f);
            }
            if (TryGetProp(dm, "Scaling", out var dSc) && dSc.ValueKind == JsonValueKind.Object)
            {
                sx = GetFloatProp(dSc, "x", 1f);
                sy = GetFloatProp(dSc, "y", 1f);
            }
            if (TryGetProp(dm, "Rotation", out var dRot) && dRot.ValueKind == JsonValueKind.Object)
                rotZ = GetFloatProp(dRot, "z", 0f);
            return Matrix3x2.CreateScale(sx, sy) * Matrix3x2.CreateRotation(rotZ) * Matrix3x2.CreateTranslation(tx, ty);
        }

        return Matrix3x2.Identity;
    }

    private static void ParseColorEffect(JsonElement inst, out Vector4 mult, out Vector4 offset)
    {
        mult = Vector4.One;
        offset = Vector4.Zero;

        if (!(TryGetProp(inst, "C", out var c) || TryGetProp(inst, "color", out c)) || c.ValueKind != JsonValueKind.Object)
            return;

        static float NormMult(float v, float maxVal = 1f)
        {
            if (MathF.Abs(v) > 1.01f) v /= 100f;
            return Math.Clamp(v, 0f, maxVal);
        }

        string mode = GetStringProp(c, "M") ?? GetStringProp(c, "mode") ?? "";
        if (string.Equals(mode, "CA", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(mode, "Alpha", StringComparison.OrdinalIgnoreCase))
        {
            mult.W = NormMult(GetFloatProp(c, "AM", GetFloatProp(c, "alphaMultiplier", 1f)), 1f);
        }
        else if (string.Equals(mode, "AD", StringComparison.OrdinalIgnoreCase) ||
                 string.Equals(mode, "Advanced", StringComparison.OrdinalIgnoreCase))
        {
            mult = new Vector4(
                NormMult(GetFloatProp(c, "RM", GetFloatProp(c, "RedMultiplier", 1f)), 2f),
                NormMult(GetFloatProp(c, "GM", GetFloatProp(c, "GreenMultiplier", 1f)), 2f),
                NormMult(GetFloatProp(c, "BM", GetFloatProp(c, "BlueMultiplier", 1f)), 2f),
                NormMult(GetFloatProp(c, "AM", GetFloatProp(c, "alphaMultiplier", 1f)), 1f));
            offset = new Vector4(
                GetFloatProp(c, "RO", GetFloatProp(c, "redOffset", 0f)),
                GetFloatProp(c, "GO", GetFloatProp(c, "greenOffset", 0f)),
                GetFloatProp(c, "BO", GetFloatProp(c, "blueOffset", 0f)),
                GetFloatProp(c, "AO", GetFloatProp(c, "alphaOffset", 0f)));
        }
        else if (string.Equals(mode, "T", StringComparison.OrdinalIgnoreCase) ||
                 string.Equals(mode, "Tint", StringComparison.OrdinalIgnoreCase))
        {
            float tm = NormMult(GetFloatProp(c, "TM", GetFloatProp(c, "tintMultiplier", 0f)), 1f);
            string? hex = GetStringProp(c, "TC") ?? GetStringProp(c, "tintColor");
            if (!string.IsNullOrEmpty(hex) && TryParseHexColor(hex!, out float tr, out float tg, out float tb))
            {
                mult = new Vector4(1f - tm, 1f - tm, 1f - tm, 1f);
                offset = new Vector4(tr * tm, tg * tm, tb * tm, 0f);
            }
        }
        else if (string.Equals(mode, "B", StringComparison.OrdinalIgnoreCase) ||
                 string.Equals(mode, "CBRT", StringComparison.OrdinalIgnoreCase) ||
                 string.Equals(mode, "Brightness", StringComparison.OrdinalIgnoreCase))
        {
            float brt = GetFloatProp(c, "BRT", GetFloatProp(c, "brightness", 0f));
            if (MathF.Abs(brt) > 1.01f) brt /= 100f;
            brt = Math.Clamp(brt, -1f, 1f);
            float m = 1f - MathF.Abs(brt);
            float o = brt > 0f ? 255f * brt : 0f;
            mult = new Vector4(m, m, m, 1f);
            offset = new Vector4(o, o, o, 0f);
        }
    }

    private static bool TryParseHexColor(string hex, out float r, out float g, out float b)
    {
        r = g = b = 0f;
        string s = hex.TrimStart('#');
        if (s.Length == 6 && uint.TryParse(s, System.Globalization.NumberStyles.HexNumber, null, out uint rgb))
        {
            r = (rgb >> 16) & 0xFF;
            g = (rgb >> 8) & 0xFF;
            b = rgb & 0xFF;
            return true;
        }
        return false;
    }

    private static List<(string Label, int Start, int End)> ExtractFrameLabelRanges(AnimateTimeline tl)
    {
        var rawLabels = new List<(string Label, int Start, int Duration)>();
        foreach (var layer in tl.Layers)
        {
            var layerLabels = layer.Frames.Where(f => !string.IsNullOrWhiteSpace(f.Label)).ToList();
            if (layerLabels.Count == 0) continue;

            for (int i = 0; i < layerLabels.Count; i++)
            {
                var cur = layerLabels[i];
                int nextStart = (i + 1 < layerLabels.Count) ? layerLabels[i + 1].Index : tl.TotalFrames;
                int end = Math.Max(cur.Index, nextStart - 1);
                rawLabels.Add((cur.Label!, cur.Index, end));
            }
        }

        rawLabels.Sort((a, b) => a.Start.CompareTo(b.Start));
        var dedup = new List<(string Label, int Start, int End)>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var item in rawLabels)
        {
            if (seen.Add(item.Label))
                dedup.Add(item);
        }
        return dedup;
    }

    /// <summary>
    /// Extracts sequential pose segments from an unlabeled main timeline when a layer places distinct
    /// top-level character pose symbols one after another (e.g. Idle -> Left -> Down -> Right -> Up).
    /// Rendering through the main timeline (or applying the keyframe's placement matrix) keeps every
    /// pose in the shared Adobe Animate stage coordinate space so feet/anchors align automatically.
    /// </summary>
    private static List<(string ClipName, AnimateTimeline Timeline, List<int> Frames, Matrix3x2 RootMatrix)> ExtractMainTimelineSymbolSegments(
        AnimateTimeline mainTimeline,
        Dictionary<string, AnimateTimeline> symbols,
        Matrix3x2 stageRootMatrix)
    {
        var results = new List<(string ClipName, AnimateTimeline Timeline, List<int> Frames, Matrix3x2 RootMatrix)>();

        // Find the best pose-switcher layer: keyframes with a single top-level symbol instance spanning >= 2 distinct symbols
        AnimateLayer? bestLayer = null;
        int bestDistinct = 1;
        foreach (var layer in mainTimeline.Layers)
        {
            if (layer.Frames.Count < 2) continue;
            var distinct = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            bool valid = true;
            foreach (var kf in layer.Frames)
            {
                if (kf.Elements.Count == 0) continue;
                if (kf.Elements.Count != 1 || !kf.Elements[0].IsSymbol)
                {
                    valid = false;
                    break;
                }
                string symName = kf.Elements[0].Name;
                if (symName.StartsWith("_bta_", StringComparison.OrdinalIgnoreCase))
                {
                    valid = false;
                    break;
                }
                if (TryLookupSymbol(symbols, symName, out var childTl) && childTl.TotalFrames > 0)
                    distinct.Add(symName);
            }

            if (valid && distinct.Count > bestDistinct)
            {
                bestDistinct = distinct.Count;
                bestLayer = layer;
            }
        }

        if (bestLayer == null) return results;

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var kf in bestLayer.Frames)
        {
            if (kf.Elements.Count != 1 || !kf.Elements[0].IsSymbol) continue;
            var el = kf.Elements[0];
            if (!TryLookupSymbol(symbols, el.Name, out var childTl) || childTl.TotalFrames <= 0)
                continue;

            string norm = NormalizeSymbolKey(el.Name);
            string cleanName = norm.Contains('/') ? norm.Substring(norm.LastIndexOf('/') + 1) : norm;
            if (string.IsNullOrWhiteSpace(cleanName) || !seen.Add(cleanName))
                continue;

            if (kf.Duration > 1 || childTl.TotalFrames <= 1)
            {
                int count = Math.Clamp(kf.Duration, 1, MAX_FRAMES_PER_CLIP);
                results.Add((cleanName, mainTimeline, Enumerable.Range(kf.Index, count).ToList(), stageRootMatrix));
            }
            else
            {
                // Single-frame keyframe referencing a multi-frame symbol: render full child timeline at its stage placement!
                int count = Math.Clamp(childTl.TotalFrames, 1, MAX_FRAMES_PER_CLIP);
                results.Add((cleanName, childTl, Enumerable.Range(0, count).ToList(), el.Matrix * stageRootMatrix));
            }
        }

        return results;
    }

    private static bool TryResolveJsonAnimToTimeline(
        string prefix,
        List<int>? indices,
        AnimateTimeline mainTl,
        List<(string Label, int Start, int End)> labelRanges,
        Dictionary<string, AnimateTimeline> symbols,
        out AnimateTimeline resolvedTl,
        out List<int> resolvedFrames)
    {
        resolvedTl = mainTl;
        resolvedFrames = new List<int>();

        // 1. Frame label match on main timeline
        var lbl = labelRanges.FirstOrDefault(l =>
            string.Equals(l.Label, prefix, StringComparison.OrdinalIgnoreCase) ||
            l.Label.StartsWith(prefix, StringComparison.OrdinalIgnoreCase));
        if (!string.IsNullOrEmpty(lbl.Label))
        {
            if (indices != null && indices.Count > 0)
            {
                // V-Slice frameIndices on a main timeline can be absolute timeline frame numbers OR relative to label start.
                int labelLen = Math.Max(1, lbl.End - lbl.Start + 1);
                bool looksAbsolute = lbl.Start > 0 && indices.Min() >= lbl.Start && indices.Max() < mainTl.TotalFrames && indices.Max() >= labelLen;
                foreach (int idx in indices)
                {
                    int f = looksAbsolute ? idx : (lbl.Start + idx);
                    if (f >= 0 && f < mainTl.TotalFrames)
                        resolvedFrames.Add(f);
                }
            }
            if (resolvedFrames.Count == 0)
            {
                int count = Math.Clamp(lbl.End - lbl.Start + 1, 1, MAX_FRAMES_PER_CLIP);
                resolvedFrames = Enumerable.Range(lbl.Start, count).ToList();
            }
            return resolvedFrames.Count > 0;
        }

        // 2. Symbol dictionary match (addBySymbol)
        var symMatch = symbols.FirstOrDefault(kvp =>
            string.Equals(kvp.Key, prefix, StringComparison.OrdinalIgnoreCase) ||
            kvp.Key.EndsWith("/" + prefix, StringComparison.OrdinalIgnoreCase));
        if (symMatch.Value != null && symMatch.Value.TotalFrames > 0)
        {
            resolvedTl = symMatch.Value;
            if (indices != null && indices.Count > 0)
            {
                foreach (int idx in indices)
                    if (idx >= 0 && idx < resolvedTl.TotalFrames)
                        resolvedFrames.Add(idx);
            }
            if (resolvedFrames.Count == 0)
            {
                int count = Math.Clamp(resolvedTl.TotalFrames, 1, MAX_FRAMES_PER_CLIP);
                resolvedFrames = Enumerable.Range(0, count).ToList();
            }
            return resolvedFrames.Count > 0;
        }

        // 3. Direct frame indices on main timeline (V-Slice atlas animations with empty prefix + frameIndices: [0..13])
        if (indices != null && indices.Count > 0 && mainTl.TotalFrames > 0)
        {
            foreach (int idx in indices)
                if (idx >= 0 && idx < mainTl.TotalFrames)
                    resolvedFrames.Add(idx);
            return resolvedFrames.Count > 0;
        }

        return false;
    }

    private static bool LooksLikeCharacterAnimSymbol(string name)
    {
        string l = name.ToLowerInvariant();
        return l.Contains("idle") || l.Contains("dance") || l.Contains("left") || l.Contains("down")
            || l.Contains("up") || l.Contains("right") || l.Contains("sing") || l.Contains("note")
            || l.Contains("hey") || l.Contains("cheer") || l.Contains("miss");
    }

    private sealed class CompanionCharJson
    {
        public string? Name;
        public string? HealthIcon;
        public float Scale = 1f;
        public bool FlipX;
        public bool NoAntialiasing;
        public List<(string RoleName, string Prefix, List<int>? Indices, float Fps, bool Loop, Vec2 Offset, bool HasOffset)> Anims = new();
    }

    private static CompanionCharJson? FindAndParseCompanionJson(string dir)
    {
        var candidates = Directory.EnumerateFiles(dir, "*", SearchOption.TopDirectoryOnly)
            .Where(p =>
            {
                if (!string.Equals(Path.GetExtension(p), ".json", StringComparison.OrdinalIgnoreCase))
                    return false;
                string fn = Path.GetFileName(p);
                return !fn.StartsWith("spritemap", StringComparison.OrdinalIgnoreCase)
                    && !string.Equals(fn, "Animation.json", StringComparison.OrdinalIgnoreCase)
                    && !string.Equals(fn, "animations.json", StringComparison.OrdinalIgnoreCase)
                    && !string.Equals(fn, "metadata.json", StringComparison.OrdinalIgnoreCase)
                    && !string.Equals(fn, "sprite.json", StringComparison.OrdinalIgnoreCase)
                    && !string.Equals(fn, "character.json", StringComparison.OrdinalIgnoreCase);
            })
            .ToList();

        // Also check parent directory for a matching <folderName>.json (case-insensitive on Linux/Android)
        var parentDir = Directory.GetParent(dir);
        if (parentDir != null && parentDir.Exists)
        {
            string folderStem = new DirectoryInfo(dir).Name + ".json";
            string? parentMatch = Directory.EnumerateFiles(parentDir.FullName, "*", SearchOption.TopDirectoryOnly)
                .FirstOrDefault(f => string.Equals(Path.GetFileName(f), folderStem, StringComparison.OrdinalIgnoreCase));
            if (parentMatch != null)
                candidates.Add(parentMatch);
        }

        foreach (var path in candidates)
        {
            try
            {
                string raw = StripBom(File.ReadAllText(path));
                using var doc = JsonDocument.Parse(raw, new JsonDocumentOptions
                {
                    AllowTrailingCommas = true,
                    CommentHandling = JsonCommentHandling.Skip,
                });
                var root = doc.RootElement;
                if (root.ValueKind != JsonValueKind.Object || !TryGetProp(root, "animations", out var anims) || anims.ValueKind != JsonValueKind.Array)
                    continue;

                var res = new CompanionCharJson
                {
                    Name = GetStringProp(root, "name") ?? Path.GetFileNameWithoutExtension(path),
                    HealthIcon = GetStringProp(root, "healthicon")
                        ?? (TryGetProp(root, "healthIcon", out var hi) && hi.ValueKind == JsonValueKind.Object
                            ? GetStringProp(hi, "id")
                            : GetStringProp(root, "healthIcon")),
                    Scale = GetFloatProp(root, "scale", 1f),
                    FlipX = GetBoolProp(root, "flip_x") || GetBoolProp(root, "flipX"),
                    NoAntialiasing = GetBoolProp(root, "no_antialiasing") || GetBoolProp(root, "isPixel"),
                };

                foreach (var item in anims.EnumerateArray())
                {
                    if (item.ValueKind != JsonValueKind.Object) continue;
                    string role = GetStringProp(item, "anim") ?? GetStringProp(item, "name") ?? "";
                    string prefix = GetStringProp(item, "prefix") ?? GetStringProp(item, "name") ?? role;
                    float fps = GetFloatProp(item, "fps", GetFloatProp(item, "frameRate", 24f));
                    bool loop = GetBoolProp(item, "loop") || GetBoolProp(item, "looped");

                    List<int>? idxList = null;
                    if ((TryGetProp(item, "indices", out var idxEl) || TryGetProp(item, "frameIndices", out idxEl)) && idxEl.ValueKind == JsonValueKind.Array)
                    {
                        idxList = new List<int>();
                        foreach (var n in idxEl.EnumerateArray())
                            if (n.ValueKind == JsonValueKind.Number && n.TryGetInt32(out int v))
                                idxList.Add(v);
                    }

                    var off = new Vec2();
                    bool hasOff = false;
                    if (TryGetProp(item, "offsets", out var offEl) && offEl.ValueKind == JsonValueKind.Array)
                    {
                        var offs = new List<float>(2);
                        foreach (var n in offEl.EnumerateArray())
                            if (n.ValueKind == JsonValueKind.Number) offs.Add((float)n.GetDouble());
                        if (offs.Count >= 2)
                        {
                            off = new Vec2 { X = offs[0], Y = offs[1] };
                            hasOff = true;
                        }
                    }

                    res.Anims.Add((role, prefix, idxList, fps, loop, off, hasOff));
                }

                if (res.Anims.Count > 0)
                    return res;
            }
            catch { }
        }
        return null;
    }

    private static string StripBom(string s) => s.Length > 0 && s[0] == '\uFEFF' ? s.Substring(1) : s;

    private static bool TryGetProp(JsonElement obj, string name, out JsonElement val)
    {
        if (obj.ValueKind == JsonValueKind.Object)
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
