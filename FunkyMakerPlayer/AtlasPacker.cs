using System;
using System.Collections.Generic;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Formats;
using SixLabors.ImageSharp.PixelFormats;
using Color = Microsoft.Xna.Framework.Color;

namespace FunkyMakerPlayer;

/// <summary>
/// Packs large horizontal spritesheets into 2D texture atlases automatically.
/// Uses bounded atlas dimensions, direct row-span packing, and adaptive VRAM downscaling.
/// </summary>
public static class AtlasPacker
{
    private const int MaxSingleAtlasDim = 4096;
    private const float MinAdaptiveScale = 0.25f;

    /// <summary>
    /// Rectangle representing a frame's position in a packed atlas.
    /// </summary>
    public struct FrameRect
    {
        public int X;
        public int Y;
        public int Width;
        public int Height;
        public int AtlasIndex;

        public FrameRect(int x, int y, int w, int h, int atlasIndex = 0)
        {
            X = x;
            Y = y;
            Width = w;
            Height = h;
            AtlasIndex = atlasIndex;
        }
    }

    /// <summary>
    /// Result of packing frames into an atlas.
    /// </summary>
    public class PackResult
    {
        public Texture2D AtlasTexture { get; set; } = null!;
        public List<FrameRect> FrameCoordinates { get; set; } = new();
        public int AtlasWidth { get; set; }
        public int AtlasHeight { get; set; }
        public float DownscaleFactor { get; set; } = 1.0f;
    }

    internal static long GetDefaultStripVramBudgetBytes()
    {
        if (!Environment.Is64BitProcess)
            return 20L * 1024 * 1024;
#if ANDROID
        return 48L * 1024 * 1024;
#else
        long totalAvail = GC.GetGCMemoryInfo().TotalAvailableMemoryBytes;
        if (totalAvail > 0 && totalAvail <= 4L * 1024 * 1024 * 1024)
            return 96L * 1024 * 1024;
        if (totalAvail > 0 && totalAvail <= 8L * 1024 * 1024 * 1024)
            return 128L * 1024 * 1024;
        return 192L * 1024 * 1024;
#endif
    }

    /// <summary>
    /// Packs a horizontal spritesheet into one or more 2D texture atlases.
    /// Streams directly from decoded strip rows into a reusable pixel buffer with
    /// adaptive VRAM budgeting and automatic downscale retry on allocation failure.
    /// </summary>
    /// <param name="pngBytes">Original horizontal spritesheet PNG bytes</param>
    /// <param name="frameCount">Number of frames in the strip</param>
    /// <param name="frameWidth">Width of each frame</param>
    /// <param name="frameHeight">Height of each frame</param>
    /// <param name="maxAtlasSize">Maximum atlas size (GPU limit, e.g. 4096-16384)</param>
    /// <param name="graphicsDevice">MonoGame GraphicsDevice for creating textures</param>
    /// <param name="onProgress">Optional callback for progress updates (current frame, total frames)</param>
    /// <param name="maxStripVramBytes">Optional VRAM budget for a single animation strip (0 = auto)</param>
    /// <returns>List of packed atlases</returns>
    public static List<PackResult> PackHorizontalStrip(
        byte[] pngBytes,
        int frameCount,
        int frameWidth,
        int frameHeight,
        int maxAtlasSize,
        GraphicsDevice graphicsDevice,
        Action<int, int>? onProgress = null,
        long maxStripVramBytes = 0)
    {
        Console.WriteLine($"[AtlasPacker] Packing {frameCount} frames ({frameWidth}x{frameHeight}) into 2D atlas...");

        var results = new List<PackResult>();
        if (frameCount <= 0) frameCount = 1;
        if (maxAtlasSize <= 0) maxAtlasSize = 4096;

        try
        {
            using var ms = new System.IO.MemoryStream(pngBytes, writable: false);
            var decoderOptions = new DecoderOptions { SkipMetadata = true };
            using var stripImage = Image.Load<Rgba32>(decoderOptions, ms);

            int actualWidth = stripImage.Width;
            int actualHeight = stripImage.Height;
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

            float budgetScale = rawStripBytes > vramBudget
                ? MathF.Sqrt((float)vramBudget / rawStripBytes)
                : 1.0f;

            float attemptScale = Math.Clamp(Math.Min(fitScale, budgetScale), MinAdaptiveScale, 1.0f);

            while (true)
            {
                try
                {
                    results = PackWithScale(
                        stripImage,
                        actualWidth,
                        actualHeight,
                        frameCount,
                        effectiveFrameWidth,
                        effectiveFrameHeight,
                        maxAtlasSize,
                        attemptScale,
                        graphicsDevice,
                        onProgress);

                    Console.WriteLine($"[AtlasPacker] Successfully packed {frameCount} frames into {results.Count} atlas(es) (scale={results[0].DownscaleFactor:0.###})");
                    return results;
                }
                catch (Exception attemptEx) when (attemptScale > MinAdaptiveScale + 0.01f)
                {
                    foreach (var partial in results)
                        partial.AtlasTexture?.Dispose();
                    results.Clear();

                    float nextScale = Math.Max(MinAdaptiveScale, attemptScale * 0.65f);
                    Console.WriteLine($"[AtlasPacker] Atlas allocation failed at scale {attemptScale:0.###} ({attemptEx.Message}); retrying at scale {nextScale:0.###}...");
                    attemptScale = nextScale;
                    GC.Collect();
                    GC.WaitForPendingFinalizers();
                }
            }
        }
        catch (Exception ex)
        {
            foreach (var partial in results)
                partial.AtlasTexture?.Dispose();
            results.Clear();
            Console.WriteLine($"[AtlasPacker] ERROR: {ex.Message}");
            Console.WriteLine($"[AtlasPacker] Stack: {ex.StackTrace}");
        }

        return results;
    }

    private static List<PackResult> PackWithScale(
        Image<Rgba32> stripImage,
        int actualWidth,
        int actualHeight,
        int frameCount,
        int srcFrameW,
        int srcFrameH,
        int gpuMaxAtlasSize,
        float requestedScale,
        GraphicsDevice graphicsDevice,
        Action<int, int>? onProgress)
    {
        bool useNative = requestedScale >= 0.995f && srcFrameW <= gpuMaxAtlasSize && srcFrameH <= gpuMaxAtlasSize;
        int dstFrameW = useNative ? srcFrameW : Math.Clamp((int)Math.Round(srcFrameW * requestedScale), 1, gpuMaxAtlasSize);
        int dstFrameH = useNative ? srcFrameH : Math.Clamp((int)Math.Round(srcFrameH * requestedScale), 1, gpuMaxAtlasSize);

        float actualDownscale = useNative
            ? 1.0f
            : Math.Min((float)dstFrameW / srcFrameW, (float)dstFrameH / srcFrameH);

        int capDimW = Math.Clamp(Math.Max(Math.Min(gpuMaxAtlasSize, MaxSingleAtlasDim), dstFrameW), 1, gpuMaxAtlasSize);
        int capDimH = Math.Clamp(Math.Max(Math.Min(gpuMaxAtlasSize, MaxSingleAtlasDim), dstFrameH), 1, gpuMaxAtlasSize);

        int framesPerRow = Math.Max(1, capDimW / dstFrameW);
        int rowsPerAtlas = Math.Max(1, capDimH / dstFrameH);
        int framesPerAtlas = Math.Max(1, framesPerRow * rowsPerAtlas);
        int atlasCount = (int)Math.Ceiling((double)frameCount / framesPerAtlas);

        int firstAtlasFrames = Math.Min(framesPerAtlas, frameCount);
        int colsUsed = Math.Min(framesPerRow, firstAtlasFrames);
        int atlasWidth = Math.Max(1, colsUsed * dstFrameW);
        int maxRowsInAnyAtlas = Math.Min(rowsPerAtlas, (int)Math.Ceiling((double)firstAtlasFrames / colsUsed));
        int maxBufferPixels = atlasWidth * maxRowsInAnyAtlas * dstFrameH;

        Console.WriteLine($"[AtlasPacker] Layout: {atlasCount} atlas(es), max {atlasWidth}x{maxRowsInAnyAtlas * dstFrameH}, frame={dstFrameW}x{dstFrameH} (scale={actualDownscale:0.###}), {colsUsed} cols/row");

        var pixelBuffer = new Color[maxBufferPixels];
        var results = new List<PackResult>(atlasCount);

        int[]? x0Table = null, x1Table = null, wx0Table = null, wx1Table = null;
        int[]? y0Table = null, y1Table = null, wy0Table = null, wy1Table = null;
        if (!useNative)
        {
            BuildBilinearAxisTable(srcFrameW, dstFrameW, out x0Table, out x1Table, out wx0Table, out wx1Table);
            BuildBilinearAxisTable(Math.Min(srcFrameH, actualHeight), dstFrameH, out y0Table, out y1Table, out wy0Table, out wy1Table);
        }

        try
        {
            stripImage.ProcessPixelRows(stripAccessor =>
            {
                int frameStart = 0;
                for (int atlasIdx = 0; atlasIdx < atlasCount; atlasIdx++)
                {
                    int framesInAtlas = Math.Min(framesPerAtlas, frameCount - frameStart);
                    int rowsInAtlas = (int)Math.Ceiling((double)framesInAtlas / colsUsed);
                    int thisAtlasWidth = atlasIdx == 0 ? atlasWidth : Math.Min(colsUsed, framesInAtlas) * dstFrameW;
                    int thisAtlasHeight = rowsInAtlas * dstFrameH;
                    int pixelCount = thisAtlasWidth * thisAtlasHeight;

                    Array.Clear(pixelBuffer, 0, pixelCount);
                    var frameCoords = new List<FrameRect>(framesInAtlas);

                    for (int i = 0; i < framesInAtlas; i++)
                    {
                        int row = i / colsUsed;
                        int col = i % colsUsed;
                        frameCoords.Add(new FrameRect(col * dstFrameW, row * dstFrameH, dstFrameW, dstFrameH, atlasIdx));
                    }

                    for (int row = 0; row < rowsInAtlas; row++)
                    {
                        int rowFirstIdx = row * colsUsed;
                        int rowColCount = Math.Min(colsUsed, framesInAtlas - rowFirstIdx);
                        int dstRowY = row * dstFrameH;

                        if (useNative)
                        {
                            CopyNativeRowBand(
                                stripAccessor, actualWidth, actualHeight,
                                frameStart + rowFirstIdx, rowColCount, srcFrameW,
                                dstRowY, dstFrameW, dstFrameH, thisAtlasWidth, pixelBuffer);
                        }
                        else
                        {
                            CopyBilinearRowBand(
                                stripAccessor, actualWidth,
                                frameStart + rowFirstIdx, rowColCount, srcFrameW,
                                dstRowY, dstFrameW, dstFrameH, thisAtlasWidth, pixelBuffer,
                                x0Table!, x1Table!, wx0Table!, wx1Table!,
                                y0Table!, y1Table!, wy0Table!, wy1Table!);
                        }

                        if (onProgress != null)
                        {
                            int doneFrames = frameStart + rowFirstIdx + rowColCount;
                            if (doneFrames == frameCount || row == rowsInAtlas - 1 || doneFrames % 25 < colsUsed)
                            {
                                onProgress(doneFrames, frameCount);
#if ANDROID
                                System.Threading.Thread.Sleep(1);
#endif
                            }
                        }
                    }

                    var atlasTex = new Texture2D(graphicsDevice, thisAtlasWidth, thisAtlasHeight);
                    atlasTex.SetData(pixelBuffer, 0, pixelCount);

                    results.Add(new PackResult
                    {
                        AtlasTexture = atlasTex,
                        FrameCoordinates = frameCoords,
                        AtlasWidth = thisAtlasWidth,
                        AtlasHeight = thisAtlasHeight,
                        DownscaleFactor = actualDownscale
                    });

                    Console.WriteLine($"[AtlasPacker] Atlas {atlasIdx}: {framesInAtlas} frames packed into {thisAtlasWidth}x{thisAtlasHeight}");
                    frameStart += framesInAtlas;
                }
            });

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

    private static void CopyNativeRowBand(
        PixelAccessor<Rgba32> stripAccessor,
        int actualWidth,
        int actualHeight,
        int firstGlobalFrame,
        int colCount,
        int srcFrameW,
        int dstRowY,
        int dstFrameW,
        int dstFrameH,
        int atlasWidth,
        Color[] pixelBuffer)
    {
        int copyH = Math.Min(dstFrameH, actualHeight);
        for (int py = 0; py < copyH; py++)
        {
            var srcRow = stripAccessor.GetRowSpan(py);
            int dstYOffset = (dstRowY + py) * atlasWidth;

            for (int col = 0; col < colCount; col++)
            {
                int srcX = (firstGlobalFrame + col) * srcFrameW;
                if (srcX >= actualWidth) continue;

                int copyW = Math.Min(dstFrameW, actualWidth - srcX);
                int dstXOffset = dstYOffset + col * dstFrameW;

                for (int px = 0; px < copyW; px++)
                    pixelBuffer[dstXOffset + px] = ToPremultipliedColor(srcRow[srcX + px]);
            }
        }
    }

    private static void CopyBilinearRowBand(
        PixelAccessor<Rgba32> stripAccessor,
        int actualWidth,
        int firstGlobalFrame,
        int colCount,
        int srcFrameW,
        int dstRowY,
        int dstFrameW,
        int dstFrameH,
        int atlasWidth,
        Color[] pixelBuffer,
        int[] x0Table, int[] x1Table, int[] wx0Table, int[] wx1Table,
        int[] y0Table, int[] y1Table, int[] wy0Table, int[] wy1Table)
    {
        for (int py = 0; py < dstFrameH; py++)
        {
            int sy0 = y0Table[py];
            int sy1 = y1Table[py];
            int wy0 = wy0Table[py];
            int wy1 = wy1Table[py];

            var srcRow0 = stripAccessor.GetRowSpan(sy0);
            var srcRow1 = sy1 == sy0 ? srcRow0 : stripAccessor.GetRowSpan(sy1);
            int dstYOffset = (dstRowY + py) * atlasWidth;

            for (int col = 0; col < colCount; col++)
            {
                int srcX = (firstGlobalFrame + col) * srcFrameW;
                if (srcX >= actualWidth) continue;

                int maxLocalX = Math.Min(srcFrameW, actualWidth - srcX) - 1;
                bool fullFrameWidth = maxLocalX == srcFrameW - 1;
                int dstXOffset = dstYOffset + col * dstFrameW;

                for (int px = 0; px < dstFrameW; px++)
                {
                    int lx0 = fullFrameWidth ? x0Table[px] : Math.Min(x0Table[px], maxLocalX);
                    int lx1 = fullFrameWidth ? x1Table[px] : Math.Min(x1Table[px], maxLocalX);

                    var p00 = srcRow0[srcX + lx0];
                    var p10 = srcRow0[srcX + lx1];
                    var p01 = srcRow1[srcX + lx0];
                    var p11 = srcRow1[srcX + lx1];

                    int wx0 = wx0Table[px];
                    int wx1 = wx1Table[px];
                    int w00 = wx0 * wy0;
                    int w10 = wx1 * wy0;
                    int w01 = wx0 * wy1;
                    int w11 = wx1 * wy1;

                    if ((p00.A & p10.A & p01.A & p11.A) == 255)
                    {
                        byte r = (byte)((w00 * p00.R + w10 * p10.R + w01 * p01.R + w11 * p11.R + 32768) >> 16);
                        byte g = (byte)((w00 * p00.G + w10 * p10.G + w01 * p01.G + w11 * p11.G + 32768) >> 16);
                        byte b = (byte)((w00 * p00.B + w10 * p10.B + w01 * p01.B + w11 * p11.B + 32768) >> 16);
                        pixelBuffer[dstXOffset + px] = new Color(r, g, b, (byte)255);
                    }
                    else if ((p00.A | p10.A | p01.A | p11.A) == 0)
                    {
                        pixelBuffer[dstXOffset + px] = Color.Transparent;
                    }
                    else
                    {
                        var c00 = ToPremultipliedColor(p00);
                        var c10 = ToPremultipliedColor(p10);
                        var c01 = ToPremultipliedColor(p01);
                        var c11 = ToPremultipliedColor(p11);

                        byte r = (byte)((w00 * c00.R + w10 * c10.R + w01 * c01.R + w11 * c11.R + 32768) >> 16);
                        byte g = (byte)((w00 * c00.G + w10 * c10.G + w01 * c01.G + w11 * c11.G + 32768) >> 16);
                        byte b = (byte)((w00 * c00.B + w10 * c10.B + w01 * c01.B + w11 * c11.B + 32768) >> 16);
                        byte a = (byte)((w00 * c00.A + w10 * c10.A + w01 * c01.A + w11 * c11.A + 32768) >> 16);
                        pixelBuffer[dstXOffset + px] = new Color(r, g, b, a);
                    }
                }
            }
        }
    }

    private static Color ToPremultipliedColor(Rgba32 p)
    {
        if (p.A == 255) return new Color(p.R, p.G, p.B, (byte)255);
        if (p.A == 0) return Color.Transparent;
        return Color.FromNonPremultiplied(p.R, p.G, p.B, p.A);
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
