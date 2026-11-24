using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Runtime.InteropServices;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Formats;
using SixLabors.ImageSharp.PixelFormats;
using Color = Microsoft.Xna.Framework.Color;

namespace FunkyMakerPlayer;

/// <summary>
/// Packs horizontal spritesheets and large single-frame sprites into 2D texture atlases automatically.
/// Features:
/// - Streaming row-by-row PNG decoding for standard 8-bit non-interlaced PNGs (avoids 500+ MB full-strip allocations)
/// - Adaptive bilinear downscaling respecting both per-strip and level-wide VRAM budgets
/// - Tight transparent-margin trimming (with 1px gutter anti-bleed padding)
/// - Exact pixel-verified frame deduplication (reusing identical or empty frames without duplicating pixels)
/// - Multi-page shelf packing (NFDH) with automatic downscale retry on GPU/RAM allocation failure
/// </summary>
public static class AtlasPacker
{
    private const int MaxSingleAtlasDim = 4096;
    private const float MinAdaptiveScale64 = 0.25f;
    private const float MinAdaptiveScaleAndroid64 = 0.20f;
    private const float MinAdaptiveScale32 = 0.15f;

    internal static float GetMinAdaptiveScale()
    {
#if ANDROID
        return Platforms.Android.AndroidMemoryBudget.GetMinAdaptiveScale();
#else
        if (!Environment.Is64BitProcess)
            return MinAdaptiveScale32;
        return MinAdaptiveScale64;
#endif
    }

    /// <summary>
    /// Rectangle representing a frame's position and trim metadata in a packed atlas.
    /// </summary>
    public struct FrameRect
    {
        public int X;
        public int Y;
        public int Width;
        public int Height;
        public int AtlasIndex;
        public int TrimX;
        public int TrimY;
        public int OrigWidth;
        public int OrigHeight;

        public FrameRect(
            int x, int y, int w, int h, int atlasIndex = 0,
            int trimX = 0, int trimY = 0, int origW = 0, int origH = 0)
        {
            X = x;
            Y = y;
            Width = w;
            Height = h;
            AtlasIndex = atlasIndex;
            TrimX = trimX;
            TrimY = trimY;
            OrigWidth = origW > 0 ? origW : w;
            OrigHeight = origH > 0 ? origH : h;
        }

        public readonly bool IsEmpty => Width <= 0 || Height <= 0;
    }

    /// <summary>
    /// Result of packing frames into an atlas page.
    /// Page 0 holds the complete ordered <see cref="FrameCoordinates"/> list for all frames (0..frameCount-1),
    /// each tagged with its target <see cref="FrameRect.AtlasIndex"/>.
    /// </summary>
    public class PackResult
    {
        public Texture2D AtlasTexture { get; set; } = null!;
        public List<FrameRect> FrameCoordinates { get; set; } = new();
        public int AtlasWidth { get; set; }
        public int AtlasHeight { get; set; }
        public float DownscaleFactor { get; set; } = 1.0f;
        public long EstimatedVramBytes => (long)AtlasWidth * AtlasHeight * 4L;
    }

    internal static long GetDefaultStripVramBudgetBytes()
    {
#if ANDROID
        return Platforms.Android.AndroidMemoryBudget.GetStripVramBudgetBytes();
#else
        if (!Environment.Is64BitProcess)
            return 10L * 1024 * 1024;
        long totalAvail = GC.GetGCMemoryInfo().TotalAvailableMemoryBytes;
        if (totalAvail > 0 && totalAvail <= 4L * 1024 * 1024 * 1024)
            return 128L * 1024 * 1024;
        // PC 64-bit (>4 GB RAM): never downscale sprites by per-strip VRAM budget; rely on
        // lossless transparent-margin trimming + exact deduplication + GPU max texture fit.
        return long.MaxValue;
#endif
    }

    internal static long GetDefaultLevelVramBudgetBytes()
    {
#if ANDROID
        return Platforms.Android.AndroidMemoryBudget.GetLevelVramBudgetBytes();
#else
        if (!Environment.Is64BitProcess)
            return 112L * 1024 * 1024;
        return long.MaxValue;
#endif
    }

    internal sealed class PreparedAtlasPage
    {
        public int Width;
        public int Height;
        public Color[]? Pixels; // Exact Width * Height premultiplied pixels; nulled right after GL upload
    }

    internal sealed class PreparedAtlas
    {
        public List<PreparedAtlasPage> Pages { get; } = new();
        public List<FrameRect> FrameCoordinates { get; set; } = new();
        public float DownscaleFactor { get; set; } = 1.0f;
        public long TotalPackedBytes { get; set; }
    }

    /// <summary>
    /// Thread-safe CPU-only half of atlas packing: streams PNG rows, downscales, trims transparent
    /// margins, deduplicates frames, shelf-packs pages, and prepares page pixel buffers without
    /// touching MonoGame's <see cref="GraphicsDevice"/>. Call <see cref="UploadPreparedAtlas"/> on
    /// the main/GL thread to upload the pages to GPU.
    /// </summary>
    internal static PreparedAtlas? PrepareHorizontalStripCpu(
        byte[] pngBytes,
        int frameCount,
        int frameWidth,
        int frameHeight,
        int maxAtlasSize,
        Action<int, int>? onProgress = null,
        long maxStripVramBytes = 0,
        float maxScale = 1.0f)
    {
        if (frameCount <= 0) frameCount = 1;
        if (maxAtlasSize <= 0) maxAtlasSize = 4096;

        float minScale = GetMinAdaptiveScale();
        maxScale = Math.Clamp(maxScale, minScale, 1.0f);

        Console.WriteLine($"[AtlasPacker] Packing {frameCount} frame(s) ({frameWidth}x{frameHeight}, maxScale={maxScale:0.###}) into 2D atlas...");

        try
        {
            bool canStreamPng = TryParseStreamingPngHeader(pngBytes, out var pngHeader);
            int actualWidth;
            int actualHeight;
            Image<Rgba32>? fallbackImage = null;

            if (canStreamPng)
            {
                actualWidth = pngHeader.Width;
                actualHeight = pngHeader.Height;
            }
            else
            {
                using var ms = new MemoryStream(pngBytes, writable: false);
                var decoderOptions = new DecoderOptions { SkipMetadata = true };
                fallbackImage = Image.Load<Rgba32>(decoderOptions, ms);
                actualWidth = fallbackImage.Width;
                actualHeight = fallbackImage.Height;
            }

            try
            {
                int expectedWidth = frameCount * frameWidth;
                int effectiveFrameWidth = Math.Max(1, frameWidth);
                int effectiveFrameHeight = Math.Max(1, frameHeight);

                if (actualWidth != expectedWidth && actualWidth > 0)
                {
                    effectiveFrameWidth = Math.Max(1, actualWidth / frameCount);
                    effectiveFrameHeight = actualHeight;
                    Console.WriteLine($"[AtlasPacker] Adjusted frame size from {frameWidth}x{frameHeight} to {effectiveFrameWidth}x{effectiveFrameHeight} based on actual image ({actualWidth}x{actualHeight})");
                }

                long vramBudget = maxStripVramBytes > 0 ? maxStripVramBytes : GetDefaultStripVramBudgetBytes();
                long rawStripBytes = (long)frameCount * effectiveFrameWidth * effectiveFrameHeight * 4L;

                float fitScale = 1.0f;
                if (effectiveFrameWidth > maxAtlasSize || effectiveFrameHeight > maxAtlasSize)
                    fitScale = Math.Min((float)maxAtlasSize / effectiveFrameWidth, (float)maxAtlasSize / effectiveFrameHeight);

                // Lossless-first sizing: when a streaming PNG's raw untrimmed bytes exceed vramBudget,
                // run a ~0-RAM scanline probe to measure the EXACT trimmed + deduplicated footprint at
                // 1.0x native quality BEFORE deciding to downscale. Sprites that fit after lossless
                // trim + dedup keep 100% native quality; strips that still exceed budget only downscale
                // by the exact minimum factor needed after trim + dedup.
                StreamProbeFrame[]? probeFrames = null;
                long effectiveEstimateBytes = rawStripBytes;
                if (canStreamPng && vramBudget < long.MaxValue && rawStripBytes > vramBudget && fitScale >= 0.995f)
                {
                    try
                    {
                        probeFrames = ProbeStreamingPngTrimAndDedup(
                            pngBytes, pngHeader!, frameCount, effectiveFrameWidth, effectiveFrameHeight,
                            out long uniqueTrimmedArea, out _);
                        long losslessPackedEst = (long)(uniqueTrimmedArea * 4L * 1.12);
                        effectiveEstimateBytes = Math.Max(4L, losslessPackedEst);
                        Console.WriteLine(
                            $"[AtlasPacker] Lossless probe: raw={rawStripBytes / (1024f * 1024f):0.##}MB -> " +
                            $"trimmed+dedupEst={effectiveEstimateBytes / (1024f * 1024f):0.##}MB (budget={vramBudget / (1024f * 1024f):0.##}MB)");
                    }
                    catch (Exception probeEx)
                    {
                        Console.WriteLine($"[AtlasPacker] Streaming lossless probe skipped ({probeEx.Message}); using raw estimate");
                        probeFrames = null;
                    }
                }

                float budgetScale;
                if (vramBudget >= long.MaxValue / 2)
                {
                    budgetScale = 1.0f;
                }
                else if (probeFrames != null)
                {
                    // Exact lossless estimate already includes 12% shelf headroom: keep 1.0x whenever it fits!
                    budgetScale = effectiveEstimateBytes > vramBudget
                        ? MathF.Sqrt((float)vramBudget / effectiveEstimateBytes)
                        : 1.0f;
                }
                else
                {
                    double headroom = rawStripBytes > 64L * 1024 * 1024 ? 1.35 : 1.50;
                    long preTrimBudget = (long)(vramBudget * headroom);
                    budgetScale = rawStripBytes > preTrimBudget
                        ? MathF.Sqrt((float)preTrimBudget / rawStripBytes)
                        : 1.0f;
                }

                float attemptScale = Math.Clamp(Math.Min(Math.Min(fitScale, budgetScale), maxScale), minScale, 1.0f);

                while (true)
                {
                    try
                    {
                        var prepared = PrepareWithScaleCpu(
                            pngBytes,
                            canStreamPng ? pngHeader : null,
                            fallbackImage,
                            actualWidth,
                            actualHeight,
                            frameCount,
                            effectiveFrameWidth,
                            effectiveFrameHeight,
                            maxAtlasSize,
                            attemptScale,
                            vramBudget,
                            minScale,
                            probeFrames,
                            onProgress,
                            out long packedBytes,
                            out bool overBudgetNeedsRetry);

                        if (overBudgetNeedsRetry && attemptScale > minScale + 0.01f)
                        {
                            float shrink = packedBytes > 0
                                ? MathF.Sqrt((float)vramBudget / packedBytes) * 0.94f
                                : 0.72f;
                            float nextScale = Math.Max(minScale, Math.Min(attemptScale * 0.85f, attemptScale * shrink));
                            Console.WriteLine($"[AtlasPacker] Packed layout {packedBytes / (1024f * 1024f):0.##}MB > budget {vramBudget / (1024f * 1024f):0.##}MB at scale {attemptScale:0.###}; retrying at {nextScale:0.###}...");
                            attemptScale = nextScale;
                            continue;
                        }

                        return prepared;
                    }
                    catch (Exception attemptEx) when (attemptScale > minScale + 0.01f)
                    {
                        float nextScale = Math.Max(minScale, attemptScale * 0.65f);
                        Console.WriteLine($"[AtlasPacker] CPU atlas prep failed at scale {attemptScale:0.###} ({attemptEx.Message}); retrying at scale {nextScale:0.###}...");
                        attemptScale = nextScale;
                        FnmmSceneRenderer.ForceCompactingGc();
                    }
                }
            }
            finally
            {
                fallbackImage?.Dispose();
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[AtlasPacker] ERROR: {ex.Message}");
            Console.WriteLine($"[AtlasPacker] Stack: {ex.StackTrace}");
            return null;
        }
    }

    /// <summary>
    /// Main/GL-thread half of atlas packing: uploads prepared CPU page pixel buffers into GPU
    /// <see cref="Texture2D"/> instances and releases each page's pixel array immediately after upload.
    /// </summary>
    internal static List<PackResult> UploadPreparedAtlas(PreparedAtlas prepared, GraphicsDevice graphicsDevice)
    {
        var results = new List<PackResult>(prepared.Pages.Count);
        try
        {
            for (int p = 0; p < prepared.Pages.Count; p++)
            {
                var page = prepared.Pages[p];
                var pixels = page.Pixels ?? throw new InvalidOperationException("Prepared atlas page pixels already released.");
                var atlasTex = new Texture2D(graphicsDevice, page.Width, page.Height);
                atlasTex.SetData(pixels, 0, page.Width * page.Height);
                page.Pixels = null; // Free CPU pixel buffer immediately after GPU upload

                results.Add(new PackResult
                {
                    AtlasTexture = atlasTex,
                    FrameCoordinates = p == 0 ? prepared.FrameCoordinates : new List<FrameRect>(),
                    AtlasWidth = page.Width,
                    AtlasHeight = page.Height,
                    DownscaleFactor = prepared.DownscaleFactor
                });

                Console.WriteLine($"[AtlasPacker] Atlas {p}: uploaded {page.Width}x{page.Height}");
            }

            Console.WriteLine($"[AtlasPacker] Successfully packed {prepared.FrameCoordinates.Count} frame(s) into {results.Count} atlas(es) (scale={prepared.DownscaleFactor:0.###}, vram={prepared.TotalPackedBytes / (1024f * 1024f):0.##}MB)");
            return results;
        }
        catch
        {
            foreach (var partial in results)
                partial.AtlasTexture?.Dispose();
            results.Clear();
            throw;
        }
    }

    /// <summary>
    /// Packs a horizontal spritesheet (or single-frame sprite) into one or more 2D texture atlases.
    /// Streams row-by-row from PNG without allocating a full-resolution RGBA image for standard PNGs,
    /// trims transparent margins, deduplicates identical frames, and enforces strip + level VRAM budgets.
    /// </summary>
    public static List<PackResult> PackHorizontalStrip(
        byte[] pngBytes,
        int frameCount,
        int frameWidth,
        int frameHeight,
        int maxAtlasSize,
        GraphicsDevice graphicsDevice,
        Action<int, int>? onProgress = null,
        long maxStripVramBytes = 0,
        float maxScale = 1.0f)
    {
        float minScale = GetMinAdaptiveScale();
        float currentMaxScale = Math.Clamp(maxScale, minScale, 1.0f);

        while (true)
        {
            var prepared = PrepareHorizontalStripCpu(
                pngBytes, frameCount, frameWidth, frameHeight, maxAtlasSize,
                onProgress, maxStripVramBytes, currentMaxScale);
            if (prepared == null)
                return new List<PackResult>();

            try
            {
                return UploadPreparedAtlas(prepared, graphicsDevice);
            }
            catch (Exception uploadEx) when (prepared.DownscaleFactor > minScale + 0.01f)
            {
                foreach (var page in prepared.Pages) page.Pixels = null;
                float nextScale = Math.Max(minScale, prepared.DownscaleFactor * 0.65f);
                Console.WriteLine($"[AtlasPacker] GPU upload failed at scale {prepared.DownscaleFactor:0.###} ({uploadEx.Message}); retrying at scale {nextScale:0.###}...");
                currentMaxScale = nextScale;
                FnmmSceneRenderer.ForceCompactingGc();
            }
            catch (Exception uploadEx)
            {
                foreach (var page in prepared.Pages) page.Pixels = null;
                Console.WriteLine($"[AtlasPacker] GPU upload ERROR: {uploadEx.Message}");
                return new List<PackResult>();
            }
        }
    }

    private struct StreamProbeFrame
    {
        public int MinX;
        public int MinY;
        public int MaxX;
        public int MaxY;
        public int NonZeroPixels;
        public ulong Hash1;
        public ulong Hash2;
        public int DuplicateOfFrame; // -1 if unique in probe, else earlier identical frame index

        public readonly bool IsEmpty => NonZeroPixels <= 0 || MaxX < MinX || MaxY < MinY;
        public readonly int TrimW => IsEmpty ? 0 : (MaxX - MinX + 1);
        public readonly int TrimH => IsEmpty ? 0 : (MaxY - MinY + 1);
    }

    private sealed class DecodedFrameData
    {
        public Color[]? Pixels; // BufferWidth * bufH (null if empty or probe-deduped)
        public int BufferWidth;
        public int BufOriginX;
        public int BufOriginY;
        public int MinX = int.MaxValue;
        public int MinY = int.MaxValue;
        public int MaxX = -1;
        public int MaxY = -1;
        public int DuplicateOfFrame = -1;

        public bool IsEmpty => (Pixels == null && DuplicateOfFrame < 0) || MaxX < MinX || MaxY < MinY;
        public int TrimW => IsEmpty ? 0 : (MaxX - MinX + 1);
        public int TrimH => IsEmpty ? 0 : (MaxY - MinY + 1);
    }

    private struct UniqueTile
    {
        public int SourceFrameIndex;
        public int TrimX;
        public int TrimY;
        public int Width;
        public int Height;
        public int PackedX;
        public int PackedY;
        public int PackedAtlasIndex;
    }

    private sealed class ShelfPage
    {
        public int Width;
        public int Height;
        public int CursorX;
        public int CursorY;
        public int ShelfHeight;
        public readonly List<int> TileIndices = new();
    }

    private static PreparedAtlas PrepareWithScaleCpu(
        byte[] pngBytes,
        StreamingPngHeader? pngHeader,
        Image<Rgba32>? fallbackImage,
        int actualWidth,
        int actualHeight,
        int frameCount,
        int srcFrameW,
        int srcFrameH,
        int gpuMaxAtlasSize,
        float requestedScale,
        long vramBudget,
        float minScale,
        StreamProbeFrame[]? probeFrames,
        Action<int, int>? onProgress,
        out long totalPackedBytes,
        out bool overBudgetNeedsRetry)
    {
        totalPackedBytes = 0;
        overBudgetNeedsRetry = false;

        bool useNative = requestedScale >= 0.995f && srcFrameW <= gpuMaxAtlasSize && srcFrameH <= gpuMaxAtlasSize;
        int dstFrameW = useNative ? srcFrameW : Math.Clamp((int)Math.Round(srcFrameW * requestedScale), 1, gpuMaxAtlasSize);
        int dstFrameH = useNative ? srcFrameH : Math.Clamp((int)Math.Round(srcFrameH * requestedScale), 1, gpuMaxAtlasSize);

        float actualDownscale = useNative
            ? 1.0f
            : Math.Min((float)dstFrameW / srcFrameW, (float)dstFrameH / srcFrameH);

        // Step 1: Decode + downscale all frames while tracking exact per-frame tight non-transparent bounds.
        DecodedFrameData[] frames;
        if (pngHeader != null)
        {
            try
            {
                frames = DecodeStreamingPngToFrames(
                    pngBytes, pngHeader, frameCount, srcFrameW, srcFrameH,
                    dstFrameW, dstFrameH, useNative, useNative ? probeFrames : null, onProgress);
            }
            catch (Exception streamEx)
            {
                Console.WriteLine($"[AtlasPacker] Streaming PNG decode fallback to ImageSharp: {streamEx.Message}");
                using var ms = new MemoryStream(pngBytes, writable: false);
                using var img = Image.Load<Rgba32>(new DecoderOptions { SkipMetadata = true }, ms);
                frames = DecodeImageSharpToFrames(
                    img, actualWidth, actualHeight, frameCount, srcFrameW, srcFrameH,
                    dstFrameW, dstFrameH, useNative, onProgress);
            }
        }
        else
        {
            frames = DecodeImageSharpToFrames(
                fallbackImage!, actualWidth, actualHeight, frameCount, srcFrameW, srcFrameH,
                dstFrameW, dstFrameH, useNative, onProgress);
        }

        // Step 2: Deduplicate identical trimmed frames (exact pixel verification after 64-bit FNV-1a hash).
        // Skip hashing entirely for single-frame sprites since they cannot contain duplicate frames.
        var uniqueTiles = new List<UniqueTile>(frameCount);
        var frameToTile = new int[frameCount];
        Dictionary<ulong, List<int>>? hashBuckets = frameCount > 1 ? new Dictionary<ulong, List<int>>(frameCount) : null;
        int emptyCount = 0;
        int dedupCount = 0;

        for (int f = 0; f < frameCount; f++)
        {
            var fd = frames[f];
            if (fd.IsEmpty)
            {
                frameToTile[f] = -1;
                emptyCount++;
                continue;
            }

            // Probe-deduplicated exact duplicate (same tight bounds + 128-bit pixel hash, skipped RAM alloc)
            if (fd.DuplicateOfFrame >= 0 && fd.DuplicateOfFrame < f && frameToTile[fd.DuplicateOfFrame] >= 0)
            {
                frameToTile[f] = frameToTile[fd.DuplicateOfFrame];
                dedupCount++;
                continue;
            }

            int tx = fd.MinX;
            int ty = fd.MinY;
            int tw = fd.TrimW;
            int th = fd.TrimH;
            int bufStride = fd.BufferWidth > 0 ? fd.BufferWidth : dstFrameW;
            int localTx = tx - fd.BufOriginX;
            int localTy = ty - fd.BufOriginY;
            int matchedTile = -1;
            List<int>? candidates = null;
            ulong hash = 0;

            if (hashBuckets != null)
            {
                hash = ComputeTrimmedFrameHash(fd.Pixels!, bufStride, localTx, localTy, tw, th);
                if (hashBuckets.TryGetValue(hash, out candidates))
                {
                    for (int c = 0; c < candidates.Count; c++)
                    {
                        int tileIdx = candidates[c];
                        var existing = uniqueTiles[tileIdx];
                        var exFd = frames[existing.SourceFrameIndex];
                        int exStride = exFd.BufferWidth > 0 ? exFd.BufferWidth : dstFrameW;
                        if (existing.Width == tw && existing.Height == th &&
                            TrimmedPixelsEqual(
                                fd.Pixels!, bufStride, localTx, localTy,
                                exFd.Pixels!, exStride, existing.TrimX - exFd.BufOriginX, existing.TrimY - exFd.BufOriginY,
                                tw, th))
                        {
                            matchedTile = tileIdx;
                            break;
                        }
                    }
                }
            }

            if (matchedTile >= 0)
            {
                frameToTile[f] = matchedTile;
                // Release duplicate pixel buffer right away
                fd.Pixels = null;
                dedupCount++;
            }
            else
            {
                int newIdx = uniqueTiles.Count;
                uniqueTiles.Add(new UniqueTile
                {
                    SourceFrameIndex = f,
                    TrimX = tx,
                    TrimY = ty,
                    Width = tw,
                    Height = th
                });
                frameToTile[f] = newIdx;

                if (hashBuckets != null)
                {
                    if (candidates == null)
                    {
                        candidates = new List<int>(2);
                        hashBuckets[hash] = candidates;
                    }
                    candidates.Add(newIdx);
                }
            }
        }

        // Handle completely empty strip (all frames transparent)
        if (uniqueTiles.Count == 0)
        {
            var emptyCoords = new List<FrameRect>(frameCount);
            for (int f = 0; f < frameCount; f++)
                emptyCoords.Add(new FrameRect(0, 0, 0, 0, 0, 0, 0, dstFrameW, dstFrameH));
            totalPackedBytes = 4;
            var emptyPrepared = new PreparedAtlas
            {
                FrameCoordinates = emptyCoords,
                DownscaleFactor = actualDownscale,
                TotalPackedBytes = 4
            };
            emptyPrepared.Pages.Add(new PreparedAtlasPage
            {
                Width = 1,
                Height = 1,
                Pixels = new[] { Color.Transparent }
            });
            return emptyPrepared;
        }

        // Step 3: Shelf-pack (NFDH: height descending, then width descending) into bounded 2D atlas pages.
        // Add a 1px transparent gutter between frames whenever a page holds >1 unique tile so bilinear
        // filtering never bleeds across neighboring trimmed sprites.
        int pad = uniqueTiles.Count > 1 ? 1 : 0;
        int capDimW = Math.Clamp(Math.Max(Math.Min(gpuMaxAtlasSize, MaxSingleAtlasDim), dstFrameW), 1, gpuMaxAtlasSize);
        int capDimH = Math.Clamp(Math.Max(Math.Min(gpuMaxAtlasSize, MaxSingleAtlasDim), dstFrameH), 1, gpuMaxAtlasSize);

        var sortedIndices = new int[uniqueTiles.Count];
        for (int i = 0; i < uniqueTiles.Count; i++) sortedIndices[i] = i;
        Array.Sort(sortedIndices, (a, b) =>
        {
            int cmpH = uniqueTiles[b].Height.CompareTo(uniqueTiles[a].Height);
            if (cmpH != 0) return cmpH;
            return uniqueTiles[b].Width.CompareTo(uniqueTiles[a].Width);
        });

        var pages = new List<ShelfPage>(4);
        long packedPixelArea = 0;

        for (int si = 0; si < sortedIndices.Length; si++)
        {
            int tileIdx = sortedIndices[si];
            var tile = uniqueTiles[tileIdx];
            int placedPage = -1;
            int px = 0, py = 0;

            for (int p = 0; p < pages.Count; p++)
            {
                var page = pages[p];
                int stepX = page.CursorX > 0 ? pad : 0;
                if (page.CursorX + stepX + tile.Width <= capDimW &&
                    page.CursorY + Math.Max(page.ShelfHeight, tile.Height) <= capDimH)
                {
                    px = page.CursorX + stepX;
                    py = page.CursorY;
                    page.CursorX = px + tile.Width;
                    if (tile.Height > page.ShelfHeight) page.ShelfHeight = tile.Height;
                    if (page.CursorX > page.Width) page.Width = page.CursorX;
                    if (py + tile.Height > page.Height) page.Height = py + tile.Height;
                    placedPage = p;
                    break;
                }

                // Try opening a new shelf on this existing page
                int nextY = page.CursorY + page.ShelfHeight + pad;
                if (nextY + tile.Height <= capDimH && tile.Width <= capDimW)
                {
                    px = 0;
                    py = nextY;
                    page.CursorX = tile.Width;
                    page.CursorY = nextY;
                    page.ShelfHeight = tile.Height;
                    if (page.CursorX > page.Width) page.Width = page.CursorX;
                    if (py + tile.Height > page.Height) page.Height = py + tile.Height;
                    placedPage = p;
                    break;
                }
            }

            if (placedPage < 0)
            {
                var newPage = new ShelfPage
                {
                    Width = tile.Width,
                    Height = tile.Height,
                    CursorX = tile.Width,
                    CursorY = 0,
                    ShelfHeight = tile.Height
                };
                placedPage = pages.Count;
                px = 0;
                py = 0;
                pages.Add(newPage);
            }

            tile.PackedX = px;
            tile.PackedY = py;
            tile.PackedAtlasIndex = placedPage;
            uniqueTiles[tileIdx] = tile;
            pages[placedPage].TileIndices.Add(tileIdx);
        }

        int maxPagePixels = 0;
        for (int p = 0; p < pages.Count; p++)
        {
            int pw = Math.Max(1, pages[p].Width);
            int ph = Math.Max(1, pages[p].Height);
            pages[p].Width = pw;
            pages[p].Height = ph;
            long area = (long)pw * ph;
            packedPixelArea += area;
            if (area > maxPagePixels) maxPagePixels = (int)area;
        }

        totalPackedBytes = packedPixelArea * 4L;

        // If the trimmed+deduplicated packed atlas still significantly exceeds the strip budget
        // and we can still scale down further, release frame buffers and let caller retry lower.
        if (vramBudget < long.MaxValue && totalPackedBytes > (long)(vramBudget * 1.15) && requestedScale > minScale + 0.01f)
        {
            for (int f = 0; f < frames.Length; f++) frames[f].Pixels = null;
            overBudgetNeedsRetry = true;
            return new PreparedAtlas();
        }

        Console.WriteLine(
            $"[AtlasPacker] Layout: {pages.Count} atlas(es), unique={uniqueTiles.Count}/{frameCount} (dedup={dedupCount}, empty={emptyCount}), " +
            $"frame={dstFrameW}x{dstFrameH} (scale={actualDownscale:0.###}), vram={totalPackedBytes / (1024f * 1024f):0.##}MB");

        // Step 4: Blit unique tiles into CPU page buffers (ready for main-thread GL upload).
        var prepared = new PreparedAtlas
        {
            DownscaleFactor = actualDownscale,
            TotalPackedBytes = totalPackedBytes
        };

        for (int p = 0; p < pages.Count; p++)
        {
            var page = pages[p];
            int pw = page.Width;
            int ph = page.Height;
            var pagePixels = new Color[pw * ph];

            var tileIndices = page.TileIndices;
            for (int i = 0; i < tileIndices.Count; i++)
            {
                var tile = uniqueTiles[tileIndices[i]];
                var srcFd = frames[tile.SourceFrameIndex];
                var srcPixels = srcFd.Pixels!;
                int srcStride = srcFd.BufferWidth > 0 ? srcFd.BufferWidth : dstFrameW;
                int srcOffX = tile.TrimX - srcFd.BufOriginX;
                int srcOffY = tile.TrimY - srcFd.BufOriginY;
                for (int row = 0; row < tile.Height; row++)
                {
                    int srcOff = (srcOffY + row) * srcStride + srcOffX;
                    int dstOff = (tile.PackedY + row) * pw + tile.PackedX;
                    Array.Copy(srcPixels, srcOff, pagePixels, dstOff, tile.Width);
                }
                // Release frame pixels as soon as copied to page buffer
                srcFd.Pixels = null;
            }

            prepared.Pages.Add(new PreparedAtlasPage
            {
                Width = pw,
                Height = ph,
                Pixels = pagePixels
            });
        }

        // Populate the complete ordered FrameCoordinates list (0..frameCount-1).
        // Each frame carries its own TrimX/TrimY even when sharing deduplicated tile pixels.
        var allCoords = new List<FrameRect>(frameCount);
        for (int f = 0; f < frameCount; f++)
        {
            int tileIdx = frameToTile[f];
            if (tileIdx < 0)
            {
                allCoords.Add(new FrameRect(0, 0, 0, 0, 0, 0, 0, dstFrameW, dstFrameH));
            }
            else
            {
                var tile = uniqueTiles[tileIdx];
                var fd = frames[f];
                allCoords.Add(new FrameRect(
                    tile.PackedX,
                    tile.PackedY,
                    tile.Width,
                    tile.Height,
                    tile.PackedAtlasIndex,
                    fd.MinX,
                    fd.MinY,
                    dstFrameW,
                    dstFrameH));
            }
        }
        prepared.FrameCoordinates = allCoords;

        return prepared;
    }

    // ══════════════════════════════════════════════════════════════════════════
    // Streaming Row-by-Row PNG Decoder (zero full-strip Rgba32 allocation)
    // Supports standard non-interlaced 8-bit PNGs (Gray, RGB, Indexed+PLTE, GrayA, RGBA + tRNS)
    // ══════════════════════════════════════════════════════════════════════════

    private sealed class StreamingPngHeader
    {
        public int Width;
        public int Height;
        public byte ColorType; // 0=Gray, 2=RGB, 3=Indexed, 4=GrayA, 6=RGBA
        public int BytesPerPixel;
        public byte[]? Palette;       // 3 bytes per entry
        public byte[]? PaletteAlpha;  // tRNS alpha per index
        public bool HasTransparentColor;
        public byte TrnsR, TrnsG, TrnsB;
        public readonly List<(int Offset, int Length)> IdatChunks = new();
    }

    private sealed class PngIdatConcatStream : Stream
    {
        private readonly byte[] _png;
        private readonly List<(int Offset, int Length)> _chunks;
        private int _chunkIdx;
        private int _chunkPos;

        public PngIdatConcatStream(byte[] png, List<(int Offset, int Length)> chunks)
        {
            _png = png;
            _chunks = chunks;
        }

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        public override int Read(byte[] buffer, int offset, int count)
        {
            int totalRead = 0;
            while (count > 0 && _chunkIdx < _chunks.Count)
            {
                var (cOff, cLen) = _chunks[_chunkIdx];
                int avail = cLen - _chunkPos;
                if (avail <= 0)
                {
                    _chunkIdx++;
                    _chunkPos = 0;
                    continue;
                }
                int toCopy = Math.Min(count, avail);
                Buffer.BlockCopy(_png, cOff + _chunkPos, buffer, offset, toCopy);
                _chunkPos += toCopy;
                offset += toCopy;
                count -= toCopy;
                totalRead += toCopy;
            }
            return totalRead;
        }
    }

    private static bool TryParseStreamingPngHeader(byte[] data, out StreamingPngHeader header)
    {
        header = null!;
        if (data.Length < 33) return false;
        if (data[0] != 0x89 || data[1] != 0x50 || data[2] != 0x4E || data[3] != 0x47 ||
            data[4] != 0x0D || data[5] != 0x0A || data[6] != 0x1A || data[7] != 0x0A)
            return false;

        var h = new StreamingPngHeader();
        bool sawIhdr = false;
        int pos = 8;

        while (pos + 12 <= data.Length)
        {
            uint lenU = ReadUInt32BE(data, pos);
            if (lenU > (uint)(data.Length - pos - 12)) return false;
            int len = (int)lenU;
            int typeOff = pos + 4;
            int dataOff = pos + 8;

            byte t0 = data[typeOff], t1 = data[typeOff + 1], t2 = data[typeOff + 2], t3 = data[typeOff + 3];

            if (t0 == 'I' && t1 == 'H' && t2 == 'D' && t3 == 'R')
            {
                if (len < 13) return false;
                uint w = ReadUInt32BE(data, dataOff);
                uint ht = ReadUInt32BE(data, dataOff + 4);
                byte bitDepth = data[dataOff + 8];
                byte colorType = data[dataOff + 9];
                byte compression = data[dataOff + 10];
                byte filter = data[dataOff + 11];
                byte interlace = data[dataOff + 12];

                if (w == 0 || ht == 0 || w > 2_000_000 || ht > 2_000_000) return false;
                if (bitDepth != 8 || compression != 0 || filter != 0 || interlace != 0) return false;

                int bpp = colorType switch
                {
                    0 => 1,
                    2 => 3,
                    3 => 1,
                    4 => 2,
                    6 => 4,
                    _ => 0
                };
                if (bpp == 0) return false;

                h.Width = (int)w;
                h.Height = (int)ht;
                h.ColorType = colorType;
                h.BytesPerPixel = bpp;
                sawIhdr = true;
            }
            else if (t0 == 'P' && t1 == 'L' && t2 == 'T' && t3 == 'E')
            {
                h.Palette = new byte[len];
                Buffer.BlockCopy(data, dataOff, h.Palette, 0, len);
            }
            else if (t0 == 't' && t1 == 'R' && t2 == 'N' && t3 == 'S')
            {
                if (h.ColorType == 3)
                {
                    h.PaletteAlpha = new byte[len];
                    Buffer.BlockCopy(data, dataOff, h.PaletteAlpha, 0, len);
                }
                else if (h.ColorType == 2 && len >= 6)
                {
                    h.HasTransparentColor = true;
                    h.TrnsR = data[dataOff + 1];
                    h.TrnsG = data[dataOff + 3];
                    h.TrnsB = data[dataOff + 5];
                }
                else if (h.ColorType == 0 && len >= 2)
                {
                    h.HasTransparentColor = true;
                    h.TrnsR = h.TrnsG = h.TrnsB = data[dataOff + 1];
                }
            }
            else if (t0 == 'I' && t1 == 'D' && t2 == 'A' && t3 == 'T')
            {
                if (len > 0)
                    h.IdatChunks.Add((dataOff, len));
            }
            else if (t0 == 'I' && t1 == 'E' && t2 == 'N' && t3 == 'D')
            {
                break;
            }

            pos = dataOff + len + 4; // skip CRC
        }

        if (!sawIhdr || h.IdatChunks.Count == 0) return false;
        if (h.ColorType == 3 && h.Palette == null) return false;

        header = h;
        return true;
    }

    private static uint ReadUInt32BE(byte[] b, int off) =>
        ((uint)b[off] << 24) | ((uint)b[off + 1] << 16) | ((uint)b[off + 2] << 8) | b[off + 3];

    /// <summary>
    /// Zero-frame-RAM streaming PNG pre-scan: streams scanlines through ZLib without allocating any
    /// per-frame pixel buffers, computing each frame's exact tight non-transparent bounding box and a
    /// 128-bit content fingerprint to measure the real post-trim + post-dedup GPU area at 1.0x native quality.
    /// </summary>
    private static StreamProbeFrame[] ProbeStreamingPngTrimAndDedup(
        byte[] pngBytes,
        StreamingPngHeader h,
        int frameCount,
        int srcFrameW,
        int srcFrameH,
        out long uniqueTrimmedArea,
        out long totalTrimmedArea)
    {
        const ulong fnvOffset1 = 14695981039346656037UL;
        const ulong fnvPrime1 = 1099511628211UL;
        const ulong fnvOffset2 = 0x9E3779B97F4A7C15UL;
        const ulong fnvPrime2 = 0xBF58476D1CE4E5B9UL;

        var probe = new StreamProbeFrame[frameCount];
        for (int f = 0; f < frameCount; f++)
        {
            probe[f] = new StreamProbeFrame
            {
                MinX = int.MaxValue,
                MinY = int.MaxValue,
                MaxX = -1,
                MaxY = -1,
                Hash1 = fnvOffset1,
                Hash2 = fnvOffset2,
                DuplicateOfFrame = -1
            };
        }

        int actualW = h.Width;
        int actualH = Math.Min(h.Height, srcFrameH);
        int bpp = h.BytesPerPixel;
        int stride = actualW * bpp;

        using var idatStream = new PngIdatConcatStream(pngBytes, h.IdatChunks);
        using var zlib = new ZLibStream(idatStream, CompressionMode.Decompress);

        byte[] rawLine = new byte[1 + stride];
        byte[] prevUnfiltered = new byte[stride];
        byte[] currUnfiltered = new byte[stride];
        Rgba32[] rowRgba = new Rgba32[actualW];

        for (int py = 0; py < actualH; py++)
        {
            ReadExact(zlib, rawLine, 0, rawLine.Length);
            UnfilterScanline(rawLine, prevUnfiltered, currUnfiltered, stride, bpp);

            if (h.ColorType == 6)
                MemoryMarshal.Cast<byte, Rgba32>(currUnfiltered.AsSpan(0, stride)).CopyTo(rowRgba);
            else
                ConvertScanlineToRgba(currUnfiltered, rowRgba, actualW, h);

            for (int f = 0; f < frameCount; f++)
            {
                int srcX = f * srcFrameW;
                if (srcX >= actualW) break;
                int copyW = Math.Min(srcFrameW, actualW - srcX);

                ref var pf = ref probe[f];
                int minX = pf.MinX;
                int maxX = pf.MaxX;
                ulong h1 = pf.Hash1;
                ulong h2 = pf.Hash2;
                int nonZero = pf.NonZeroPixels;
                bool rowHasAlpha = false;

                for (int px = 0; px < copyW; px++)
                {
                    var p = rowRgba[srcX + px];
                    if (p.A == 0) continue;

                    uint packed = ToPremultipliedColor(p).PackedValue;
                    uint coord = ((uint)py << 16) ^ (uint)px;
                    h1 = (h1 ^ packed) * fnvPrime1;
                    h1 = (h1 ^ coord) * fnvPrime1;
                    h2 = (h2 ^ (packed ^ (coord * 2654435761U))) * fnvPrime2;
                    if (px < minX) minX = px;
                    if (px > maxX) maxX = px;
                    nonZero++;
                    rowHasAlpha = true;
                }

                if (rowHasAlpha)
                {
                    pf.MinX = minX;
                    pf.MaxX = maxX;
                    if (py < pf.MinY) pf.MinY = py;
                    if (py > pf.MaxY) pf.MaxY = py;
                    pf.NonZeroPixels = nonZero;
                    pf.Hash1 = h1;
                    pf.Hash2 = h2;
                }
            }

            var tmp = prevUnfiltered;
            prevUnfiltered = currUnfiltered;
            currUnfiltered = tmp;
        }

        uniqueTrimmedArea = 0;
        totalTrimmedArea = 0;
        var seen = new Dictionary<(int MinX, int MinY, int MaxX, int MaxY, int Count, ulong H1, ulong H2), int>(frameCount);
        int pad = frameCount > 1 ? 1 : 0;

        for (int f = 0; f < frameCount; f++)
        {
            ref var pf = ref probe[f];
            if (pf.IsEmpty) continue;
            long area = (long)(pf.TrimW + pad) * (pf.TrimH + pad);
            totalTrimmedArea += area;

            var key = (pf.MinX, pf.MinY, pf.MaxX, pf.MaxY, pf.NonZeroPixels, pf.Hash1, pf.Hash2);
            if (seen.TryGetValue(key, out int firstIdx))
            {
                pf.DuplicateOfFrame = firstIdx;
            }
            else
            {
                seen[key] = f;
                uniqueTrimmedArea += area;
            }
        }

        return probe;
    }

    private static DecodedFrameData[] DecodeStreamingPngToFrames(
        byte[] pngBytes,
        StreamingPngHeader h,
        int frameCount,
        int srcFrameW,
        int srcFrameH,
        int dstFrameW,
        int dstFrameH,
        bool useNative,
        StreamProbeFrame[]? tightNativeProbe,
        Action<int, int>? onProgress)
    {
        var frames = new DecodedFrameData[frameCount];
        bool useTightBuffers = useNative && tightNativeProbe != null && tightNativeProbe.Length == frameCount;
        for (int f = 0; f < frameCount; f++)
        {
            var fd = new DecodedFrameData { BufferWidth = dstFrameW };
            if (useTightBuffers)
            {
                var pf = tightNativeProbe![f];
                if (!pf.IsEmpty)
                {
                    fd.MinX = pf.MinX;
                    fd.MinY = pf.MinY;
                    fd.MaxX = pf.MaxX;
                    fd.MaxY = pf.MaxY;
                    fd.BufOriginX = pf.MinX;
                    fd.BufOriginY = pf.MinY;
                    fd.BufferWidth = pf.TrimW;
                    if (pf.DuplicateOfFrame >= 0)
                    {
                        fd.DuplicateOfFrame = pf.DuplicateOfFrame;
                    }
                    else
                    {
                        fd.Pixels = new Color[pf.TrimW * pf.TrimH];
                    }
                }
            }
            frames[f] = fd;
        }

        int actualW = h.Width;
        int actualH = h.Height;
        int bpp = h.BytesPerPixel;
        int stride = actualW * bpp;

        int[]? x0Table = null, x1Table = null, wx0Table = null, wx1Table = null;
        int[]? y0Table = null, y1Table = null, wy0Table = null, wy1Table = null;
        if (!useNative)
        {
            BuildBilinearAxisTable(srcFrameW, dstFrameW, out x0Table, out x1Table, out wx0Table, out wx1Table);
            BuildBilinearAxisTable(Math.Min(srcFrameH, actualH), dstFrameH, out y0Table, out y1Table, out wy0Table, out wy1Table);
        }

        using var idatStream = new PngIdatConcatStream(pngBytes, h.IdatChunks);
        using var zlib = new ZLibStream(idatStream, CompressionMode.Decompress);

        byte[] rawLine = new byte[1 + stride];
        byte[] prevUnfiltered = new byte[stride];
        byte[] currUnfiltered = new byte[stride];

        // Converted RGBA32 scanline cache: since y0Table[py] and y1Table[py] are monotonically
        // non-decreasing with y1 - y0 <= 1, we only ever need at most two decoded RGBA scanlines in memory,
        // and we skip RGBA conversion completely for source rows skipped during downscaling!
        Rgba32[] rowCacheA = new Rgba32[actualW];
        Rgba32[] rowCacheB = new Rgba32[actualW];
        int rowIndexA = -1;
        int rowIndexB = -1;
        int nextSourceRowToRead = 0;

        void EnsureSourceRows(int needSy0, int needSy1)
        {
            while (nextSourceRowToRead <= needSy1 && nextSourceRowToRead < actualH)
            {
                ReadExact(zlib, rawLine, 0, rawLine.Length);
                UnfilterScanline(rawLine, prevUnfiltered, currUnfiltered, stride, bpp);

                if (nextSourceRowToRead == needSy0 || nextSourceRowToRead == needSy1)
                {
                    // Rotate caches so rowA holds the previous needed row and rowB holds the newest row
                    var tmpRgba = rowCacheA;
                    rowCacheA = rowCacheB;
                    rowCacheB = tmpRgba;
                    rowIndexA = rowIndexB;

                    if (h.ColorType == 6)
                    {
                        MemoryMarshal.Cast<byte, Rgba32>(currUnfiltered.AsSpan(0, stride)).CopyTo(rowCacheB);
                    }
                    else
                    {
                        ConvertScanlineToRgba(currUnfiltered, rowCacheB, actualW, h);
                    }
                    rowIndexB = nextSourceRowToRead;
                }

                var tmpBytes = prevUnfiltered;
                prevUnfiltered = currUnfiltered;
                currUnfiltered = tmpBytes;

                nextSourceRowToRead++;
            }
        }

        Rgba32[] GetRowBuffer(int sy)
        {
            if (sy == rowIndexB) return rowCacheB;
            if (sy == rowIndexA) return rowCacheA;
            throw new InvalidOperationException($"Streaming PNG row {sy} out of sliding window ({rowIndexA}, {rowIndexB})");
        }

        int copyH = useNative ? Math.Min(dstFrameH, actualH) : dstFrameH;
        var progressSw = System.Diagnostics.Stopwatch.StartNew();

        for (int py = 0; py < copyH; py++)
        {
            if (useTightBuffers)
            {
                EnsureSourceRows(py, py);
                var srcRow = GetRowBuffer(py);
                WriteTightNativeRowToFrames(srcRow, actualW, frameCount, srcFrameW, py, frames);
            }
            else if (useNative)
            {
                EnsureSourceRows(py, py);
                var srcRow = GetRowBuffer(py);
                WriteNativeRowToFrames(srcRow, actualW, frameCount, srcFrameW, py, dstFrameW, dstFrameH, frames);
            }
            else
            {
                int sy0 = y0Table![py];
                int sy1 = y1Table![py];
                EnsureSourceRows(sy0, sy1);
                var srcRow0 = GetRowBuffer(sy0);
                var srcRow1 = sy1 == sy0 ? srcRow0 : GetRowBuffer(sy1);
                WriteBilinearRowToFrames(
                    srcRow0, srcRow1, actualW, frameCount, srcFrameW,
                    py, dstFrameW, dstFrameH,
                    x0Table!, x1Table!, wx0Table!, wx1Table!,
                    wy0Table![py], wy1Table![py], frames);
            }

            if (onProgress != null && (py == copyH - 1 || progressSw.ElapsedMilliseconds >= 120))
            {
                int estFrame = Math.Clamp((int)((long)(py + 1) * frameCount / Math.Max(1, copyH)), 1, frameCount);
                onProgress(estFrame, frameCount);
                progressSw.Restart();
            }
        }

        return frames;
    }

    private static void WriteTightNativeRowToFrames(
        ReadOnlySpan<Rgba32> srcRow,
        int actualWidth,
        int frameCount,
        int srcFrameW,
        int py,
        DecodedFrameData[] frames)
    {
        for (int f = 0; f < frameCount; f++)
        {
            var fd = frames[f];
            var pixels = fd.Pixels;
            if (pixels == null || py < fd.MinY || py > fd.MaxY)
                continue;

            int srcFrameStart = f * srcFrameW;
            if (srcFrameStart >= actualWidth) break;

            int rowOffset = (py - fd.BufOriginY) * fd.BufferWidth;
            int minX = fd.MinX;
            int maxX = Math.Min(fd.MaxX, actualWidth - srcFrameStart - 1);
            for (int px = minX; px <= maxX; px++)
            {
                var p = srcRow[srcFrameStart + px];
                if (p.A != 0)
                    pixels[rowOffset + (px - minX)] = ToPremultipliedColor(p);
            }
        }
    }

    private static DecodedFrameData[] DecodeImageSharpToFrames(
        Image<Rgba32> stripImage,
        int actualWidth,
        int actualHeight,
        int frameCount,
        int srcFrameW,
        int srcFrameH,
        int dstFrameW,
        int dstFrameH,
        bool useNative,
        Action<int, int>? onProgress)
    {
        var frames = new DecodedFrameData[frameCount];
        for (int f = 0; f < frameCount; f++) frames[f] = new DecodedFrameData();

        int[]? x0Table = null, x1Table = null, wx0Table = null, wx1Table = null;
        int[]? y0Table = null, y1Table = null, wy0Table = null, wy1Table = null;
        if (!useNative)
        {
            BuildBilinearAxisTable(srcFrameW, dstFrameW, out x0Table, out x1Table, out wx0Table, out wx1Table);
            BuildBilinearAxisTable(Math.Min(srcFrameH, actualHeight), dstFrameH, out y0Table, out y1Table, out wy0Table, out wy1Table);
        }

        int copyH = useNative ? Math.Min(dstFrameH, actualHeight) : dstFrameH;
        var progressSw = System.Diagnostics.Stopwatch.StartNew();

        stripImage.ProcessPixelRows(accessor =>
        {
            for (int py = 0; py < copyH; py++)
            {
                if (useNative)
                {
                    var srcRow = accessor.GetRowSpan(py);
                    WriteNativeRowToFrames(srcRow, actualWidth, frameCount, srcFrameW, py, dstFrameW, dstFrameH, frames);
                }
                else
                {
                    int sy0 = y0Table![py];
                    int sy1 = y1Table![py];
                    var srcRow0 = accessor.GetRowSpan(sy0);
                    var srcRow1 = sy1 == sy0 ? srcRow0 : accessor.GetRowSpan(sy1);
                    WriteBilinearRowToFrames(
                        srcRow0, srcRow1, actualWidth, frameCount, srcFrameW,
                        py, dstFrameW, dstFrameH,
                        x0Table!, x1Table!, wx0Table!, wx1Table!,
                        wy0Table![py], wy1Table![py], frames);
                }

                if (onProgress != null && (py == copyH - 1 || progressSw.ElapsedMilliseconds >= 120))
                {
                    int estFrame = Math.Clamp((int)((long)(py + 1) * frameCount / Math.Max(1, copyH)), 1, frameCount);
                    onProgress(estFrame, frameCount);
                    progressSw.Restart();
                }
            }
        });

        return frames;
    }

    private static void WriteNativeRowToFrames(
        ReadOnlySpan<Rgba32> srcRow,
        int actualWidth,
        int frameCount,
        int srcFrameW,
        int py,
        int dstFrameW,
        int dstFrameH,
        DecodedFrameData[] frames)
    {
        int rowOffset = py * dstFrameW;
        for (int f = 0; f < frameCount; f++)
        {
            int srcX = f * srcFrameW;
            if (srcX >= actualWidth) break;

            int copyW = Math.Min(dstFrameW, actualWidth - srcX);
            var fd = frames[f];
            Color[]? pixels = fd.Pixels;
            int minX = fd.MinX;
            int maxX = fd.MaxX;
            bool rowHasAlpha = false;

            for (int px = 0; px < copyW; px++)
            {
                var p = srcRow[srcX + px];
                if (p.A == 0) continue;

                if (pixels == null)
                {
                    pixels = new Color[dstFrameW * dstFrameH];
                    fd.Pixels = pixels;
                }
                pixels[rowOffset + px] = ToPremultipliedColor(p);
                if (px < minX) minX = px;
                if (px > maxX) maxX = px;
                rowHasAlpha = true;
            }

            if (rowHasAlpha)
            {
                fd.MinX = minX;
                fd.MaxX = maxX;
                if (py < fd.MinY) fd.MinY = py;
                if (py > fd.MaxY) fd.MaxY = py;
            }
        }
    }

    private static void WriteBilinearRowToFrames(
        ReadOnlySpan<Rgba32> srcRow0,
        ReadOnlySpan<Rgba32> srcRow1,
        int actualWidth,
        int frameCount,
        int srcFrameW,
        int py,
        int dstFrameW,
        int dstFrameH,
        int[] x0Table, int[] x1Table, int[] wx0Table, int[] wx1Table,
        int wy0, int wy1,
        DecodedFrameData[] frames)
    {
        int rowOffset = py * dstFrameW;
        for (int f = 0; f < frameCount; f++)
        {
            int srcX = f * srcFrameW;
            if (srcX >= actualWidth) break;

            int maxLocalX = Math.Min(srcFrameW, actualWidth - srcX) - 1;
            bool fullFrameWidth = maxLocalX == srcFrameW - 1;
            var fd = frames[f];
            Color[]? pixels = fd.Pixels;
            int minX = fd.MinX;
            int maxX = fd.MaxX;
            bool rowHasAlpha = false;

            for (int px = 0; px < dstFrameW; px++)
            {
                int lx0 = fullFrameWidth ? x0Table[px] : Math.Min(x0Table[px], maxLocalX);
                int lx1 = fullFrameWidth ? x1Table[px] : Math.Min(x1Table[px], maxLocalX);

                var p00 = srcRow0[srcX + lx0];
                var p10 = srcRow0[srcX + lx1];
                var p01 = srcRow1[srcX + lx0];
                var p11 = srcRow1[srcX + lx1];

                if ((p00.A | p10.A | p01.A | p11.A) == 0)
                    continue;

                int wx0 = wx0Table[px];
                int wx1 = wx1Table[px];
                int w00 = wx0 * wy0;
                int w10 = wx1 * wy0;
                int w01 = wx0 * wy1;
                int w11 = wx1 * wy1;

                Color outColor;
                if ((p00.A & p10.A & p01.A & p11.A) == 255)
                {
                    byte r = (byte)((w00 * p00.R + w10 * p10.R + w01 * p01.R + w11 * p11.R + 32768) >> 16);
                    byte g = (byte)((w00 * p00.G + w10 * p10.G + w01 * p01.G + w11 * p11.G + 32768) >> 16);
                    byte b = (byte)((w00 * p00.B + w10 * p10.B + w01 * p01.B + w11 * p11.B + 32768) >> 16);
                    outColor = new Color(r, g, b, (byte)255);
                }
                else
                {
                    var c00 = ToPremultipliedColor(p00);
                    var c10 = ToPremultipliedColor(p10);
                    var c01 = ToPremultipliedColor(p01);
                    var c11 = ToPremultipliedColor(p11);

                    byte a = (byte)((w00 * c00.A + w10 * c10.A + w01 * c01.A + w11 * c11.A + 32768) >> 16);
                    if (a == 0) continue;

                    byte r = (byte)((w00 * c00.R + w10 * c10.R + w01 * c01.R + w11 * c11.R + 32768) >> 16);
                    byte g = (byte)((w00 * c00.G + w10 * c10.G + w01 * c01.G + w11 * c11.G + 32768) >> 16);
                    byte b = (byte)((w00 * c00.B + w10 * c10.B + w01 * c01.B + w11 * c11.B + 32768) >> 16);
                    outColor = new Color(r, g, b, a);
                }

                if (pixels == null)
                {
                    pixels = new Color[dstFrameW * dstFrameH];
                    fd.Pixels = pixels;
                }
                pixels[rowOffset + px] = outColor;
                if (px < minX) minX = px;
                if (px > maxX) maxX = px;
                rowHasAlpha = true;
            }

            if (rowHasAlpha)
            {
                fd.MinX = minX;
                fd.MaxX = maxX;
                if (py < fd.MinY) fd.MinY = py;
                if (py > fd.MaxY) fd.MaxY = py;
            }
        }
    }

    private static void ReadExact(Stream s, byte[] buf, int offset, int count)
    {
        while (count > 0)
        {
            int n = s.Read(buf, offset, count);
            if (n <= 0) throw new EndOfStreamException("Unexpected end of PNG IDAT stream");
            offset += n;
            count -= n;
        }
    }

    private static void UnfilterScanline(byte[] rawLine, byte[] prevArr, byte[] currArr, int stride, int bpp)
    {
        byte filter = rawLine[0];
        ReadOnlySpan<byte> raw = rawLine.AsSpan(1, stride);
        ReadOnlySpan<byte> prev = prevArr.AsSpan(0, stride);
        Span<byte> curr = currArr.AsSpan(0, stride);

        switch (filter)
        {
            case 0: // None
                raw.CopyTo(curr);
                break;

            case 1: // Sub
                for (int i = 0; i < bpp; i++)
                    curr[i] = raw[i];
                for (int i = bpp; i < curr.Length; i++)
                    curr[i] = (byte)(raw[i] + curr[i - bpp]);
                break;

            case 2: // Up
                for (int i = 0; i < curr.Length; i++)
                    curr[i] = (byte)(raw[i] + prev[i]);
                break;

            case 3: // Average
                for (int i = 0; i < bpp; i++)
                    curr[i] = (byte)(raw[i] + (prev[i] >> 1));
                for (int i = bpp; i < curr.Length; i++)
                    curr[i] = (byte)(raw[i] + ((curr[i - bpp] + prev[i]) >> 1));
                break;

            case 4: // Paeth
                for (int i = 0; i < bpp; i++)
                    curr[i] = (byte)(raw[i] + prev[i]);
                for (int i = bpp; i < curr.Length; i++)
                {
                    int a = curr[i - bpp];
                    int b = prev[i];
                    int c = prev[i - bpp];
                    int pa = b - c;
                    int pb = a - c;
                    int pc = pa + pb;
                    if (pa < 0) pa = -pa;
                    if (pb < 0) pb = -pb;
                    if (pc < 0) pc = -pc;
                    int pr = (pa <= pb && pa <= pc) ? a : (pb <= pc ? b : c);
                    curr[i] = (byte)(raw[i] + pr);
                }
                break;

            default:
                throw new InvalidDataException($"Unsupported PNG filter type {filter}");
        }
    }

    private static void ConvertScanlineToRgba(byte[] src, Rgba32[] dst, int width, StreamingPngHeader h)
    {
        switch (h.ColorType)
        {
            case 6: // RGBA8
                for (int x = 0; x < width; x++)
                {
                    int o = x << 2;
                    dst[x] = new Rgba32(src[o], src[o + 1], src[o + 2], src[o + 3]);
                }
                break;

            case 2: // RGB8
                if (!h.HasTransparentColor)
                {
                    for (int x = 0, o = 0; x < width; x++, o += 3)
                        dst[x] = new Rgba32(src[o], src[o + 1], src[o + 2], 255);
                }
                else
                {
                    byte tr = h.TrnsR, tg = h.TrnsG, tb = h.TrnsB;
                    for (int x = 0, o = 0; x < width; x++, o += 3)
                    {
                        byte r = src[o], g = src[o + 1], b = src[o + 2];
                        byte a = (r == tr && g == tg && b == tb) ? (byte)0 : (byte)255;
                        dst[x] = new Rgba32(r, g, b, a);
                    }
                }
                break;

            case 3: // Indexed8
            {
                var pal = h.Palette!;
                var alpha = h.PaletteAlpha;
                int palEntries = pal.Length / 3;
                for (int x = 0; x < width; x++)
                {
                    int idx = src[x];
                    if (idx < palEntries)
                    {
                        int po = idx * 3;
                        byte a = (alpha != null && idx < alpha.Length) ? alpha[idx] : (byte)255;
                        dst[x] = new Rgba32(pal[po], pal[po + 1], pal[po + 2], a);
                    }
                    else
                    {
                        dst[x] = default;
                    }
                }
                break;
            }

            case 4: // GrayAlpha8
                for (int x = 0, o = 0; x < width; x++, o += 2)
                {
                    byte v = src[o];
                    dst[x] = new Rgba32(v, v, v, src[o + 1]);
                }
                break;

            case 0: // Gray8
                for (int x = 0; x < width; x++)
                {
                    byte v = src[x];
                    byte a = (h.HasTransparentColor && v == h.TrnsR) ? (byte)0 : (byte)255;
                    dst[x] = new Rgba32(v, v, v, a);
                }
                break;
        }
    }

    private static ulong ComputeTrimmedFrameHash(Color[] pixels, int stride, int tx, int ty, int tw, int th)
    {
        const ulong fnvOffset = 14695981039346656037UL;
        const ulong fnvPrime = 1099511628211UL;
        ulong h = fnvOffset;
        h = (h ^ (uint)tw) * fnvPrime;
        h = (h ^ (uint)th) * fnvPrime;

        for (int row = 0; row < th; row++)
        {
            int off = (ty + row) * stride + tx;
            for (int x = 0; x < tw; x++)
                h = (h ^ pixels[off + x].PackedValue) * fnvPrime;
        }
        return h;
    }

    private static bool TrimmedPixelsEqual(
        Color[] a, int strideA, int ax, int ay,
        Color[] b, int strideB, int bx, int by,
        int w, int h)
    {
        for (int row = 0; row < h; row++)
        {
            int offA = (ay + row) * strideA + ax;
            int offB = (by + row) * strideB + bx;
            for (int x = 0; x < w; x++)
            {
                if (a[offA + x].PackedValue != b[offB + x].PackedValue)
                    return false;
            }
        }
        return true;
    }

    private static Color ToPremultipliedColor(Rgba32 p)
    {
        if (p.A == 255) return new Color(p.R, p.G, p.B, (byte)255);
        if (p.A == 0) return Color.Transparent;
        return new Color(
            (byte)((p.R * p.A + 127) / 255),
            (byte)((p.G * p.A + 127) / 255),
            (byte)((p.B * p.A + 127) / 255),
            p.A);
    }

    private static void BuildBilinearAxisTable(
        int srcSize,
        int dstSize,
        out int[] i0Table,
        out int[] i1Table,
        out int[] w0Table,
        out int[] w1Table)
    {
        i0Table = new int[dstSize];
        i1Table = new int[dstSize];
        w0Table = new int[dstSize];
        w1Table = new int[dstSize];

        float scale = (float)srcSize / dstSize;
        int maxSrc = Math.Max(0, srcSize - 1);

        for (int i = 0; i < dstSize; i++)
        {
            float srcCoord = Math.Clamp((i + 0.5f) * scale - 0.5f, 0f, maxSrc);
            int i0 = (int)srcCoord;
            int i1 = Math.Min(i0 + 1, maxSrc);
            int w1 = Math.Clamp((int)Math.Round((srcCoord - i0) * 256f), 0, 256);
            i0Table[i] = i0;
            i1Table[i] = i1;
            w1Table[i] = w1;
            w0Table[i] = 256 - w1;
        }
    }

    /// <summary>
    /// Extracts a frame's source rectangle within its packed atlas texture.
    /// Used by SpriteAnimator to draw individual frames.
    /// </summary>
    public static Microsoft.Xna.Framework.Rectangle GetFrameSourceRect(FrameRect frame)
    {
        return new Microsoft.Xna.Framework.Rectangle(frame.X, frame.Y, frame.Width, frame.Height);
    }
}
