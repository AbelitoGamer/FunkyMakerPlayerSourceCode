using System;
using System.Collections.Generic;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;
using SixLabors.ImageSharp.Processing;

namespace FunkyMakerPlayer;

/// <summary>
/// Packs large horizontal spritesheets into 2D texture atlases automatically.
/// Converts 230400x450px strips into efficient 4096x4096 atlases (like FNF Sparrow format).
/// Maintains full quality without downscaling.
/// </summary>
public static class AtlasPacker
{
    /// <summary>
    /// Rectangle representing a frame's position in the atlas.
    /// </summary>
    public struct FrameRect
    {
        public int X;
        public int Y;
        public int Width;
        public int Height;
        
        public FrameRect(int x, int y, int w, int h)
        {
            X = x;
            Y = y;
            Width = w;
            Height = h;
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
    }
    
    /// <summary>
    /// Packs a horizontal spritesheet into one or more 2D texture atlases.
    /// Uses simple shelf packing algorithm (good for same-height frames).
    /// </summary>
    /// <param name="pngBytes">Original horizontal spritesheet PNG bytes</param>
    /// <param name="frameCount">Number of frames in the strip</param>
    /// <param name="frameWidth">Width of each frame</param>
    /// <param name="frameHeight">Height of each frame</param>
    /// <param name="maxAtlasSize">Maximum atlas size (GPU limit, typically 16384)</param>
    /// <param name="graphicsDevice">MonoGame GraphicsDevice for creating textures</param>
    /// <param name="onProgress">Optional callback for progress updates (current frame, total frames)</param>
    /// <returns>List of packed atlases (usually just 1)</returns>
    public static List<PackResult> PackHorizontalStrip(
        byte[] pngBytes,
        int frameCount,
        int frameWidth,
        int frameHeight,
        int maxAtlasSize,
        GraphicsDevice graphicsDevice,
        Action<int, int>? onProgress = null)
    {
        Console.WriteLine($"[AtlasPacker] Packing {frameCount} frames ({frameWidth}x{frameHeight}) into 2D atlas...");
        
        var results = new List<PackResult>();
        
        try
        {
            // Load the original strip
            using var ms = new System.IO.MemoryStream(pngBytes);
            using var stripImage = Image.Load<Rgba32>(ms);
            
            // Validate actual image dimensions vs expected
            int actualWidth = stripImage.Width;
            int actualHeight = stripImage.Height;
            int expectedWidth = frameCount * frameWidth;
            
            Console.WriteLine($"[AtlasPacker] Strip actual size: {actualWidth}x{actualHeight}, expected: {expectedWidth}x{frameHeight}");
            
            // If actual width doesn't match expected, the PNG header was corrupt
            // Recalculate frame dimensions based on actual image size
            int effectiveFrameWidth = frameWidth;
            int effectiveFrameHeight = frameHeight;
            
            if (actualWidth != expectedWidth && actualWidth > 0)
            {
                // Use actual width to calculate real frame width
                effectiveFrameWidth = actualWidth / frameCount;
                effectiveFrameHeight = actualHeight;
                Console.WriteLine($"[AtlasPacker] Adjusted frame size from {frameWidth}x{frameHeight} to {effectiveFrameWidth}x{effectiveFrameHeight} based on actual image");
            }
            
            // Calculate optimal atlas size
            // For 288 frames of 800x450: 
            // - 18 columns × 16 rows = 288 frames
            // - Atlas: 14400 × 7200 (fits in 16384 limit)
            int atlasWidth = maxAtlasSize;
            int atlasHeight = maxAtlasSize;
            
            // Use shelf packing: pack frames left-to-right, top-to-bottom
            int framesPerRow = atlasWidth / effectiveFrameWidth;
            int rowsNeeded = (int)Math.Ceiling((double)frameCount / framesPerRow);
            
            // Optimize atlas height to fit exactly
            atlasHeight = Math.Min(rowsNeeded * effectiveFrameHeight, maxAtlasSize);
            
            // If it doesn't fit in one atlas, we need multiple
            int framesPerAtlas = framesPerRow * (atlasHeight / effectiveFrameHeight);
            int atlasCount = (int)Math.Ceiling((double)frameCount / framesPerAtlas);
            
            Console.WriteLine($"[AtlasPacker] Creating {atlasCount} atlas(es) of {atlasWidth}x{atlasHeight}");
            Console.WriteLine($"[AtlasPacker] Layout: {framesPerRow} frames/row, {rowsNeeded} rows total");
            
            int frameIndex = 0;
            
            for (int atlasIdx = 0; atlasIdx < atlasCount; atlasIdx++)
            {
                int framesInThisAtlas = Math.Min(framesPerAtlas, frameCount - frameIndex);
                int rowsInThisAtlas = (int)Math.Ceiling((double)framesInThisAtlas / framesPerRow);
                int thisAtlasHeight = rowsInThisAtlas * effectiveFrameHeight;
                
                // Create atlas image
                using var atlasImage = new Image<Rgba32>(atlasWidth, thisAtlasHeight);
                var frameCoords = new List<FrameRect>();
                
                // Pack frames into atlas
                for (int i = 0; i < framesInThisAtlas; i++)
                {
                    int globalFrameIdx = frameIndex + i;
                    int row = i / framesPerRow;
                    int col = i % framesPerRow;
                    
                    int dstX = col * effectiveFrameWidth;
                    int dstY = row * effectiveFrameHeight;
                    int srcX = globalFrameIdx * effectiveFrameWidth;
                    
                    // Extract frame from strip and place in atlas (pixel-perfect copy, no interpolation)
                    // Instead of DrawImage (which may apply blending), copy pixels directly
                    for (int py = 0; py < effectiveFrameHeight; py++)
                    {
                        for (int px = 0; px < effectiveFrameWidth; px++)
                        {
                            // Safety bounds check
                            if (srcX + px < actualWidth && py < actualHeight)
                            {
                                atlasImage[dstX + px, dstY + py] = stripImage[srcX + px, py];
                            }
                        }
                    }
                    
                    frameCoords.Add(new FrameRect(dstX, dstY, effectiveFrameWidth, effectiveFrameHeight));
                    
                    // Report progress every 5 frames to allow UI updates (especially on Android)
                    if (onProgress != null && (globalFrameIdx % 5 == 0 || globalFrameIdx == frameCount - 1))
                    {
                        onProgress(globalFrameIdx + 1, frameCount);
                        
                        // Yield to allow UI thread to update (prevents ANR on Android)
                        System.Threading.Thread.Sleep(1);
                    }
                }
                
                // Convert atlas to MonoGame texture
                var pixels = new Microsoft.Xna.Framework.Color[atlasWidth * thisAtlasHeight];
                atlasImage.ProcessPixelRows(acc =>
                {
                    for (int y = 0; y < thisAtlasHeight; y++)
                    {
                        var row = acc.GetRowSpan(y);
                        int rowBase = y * atlasWidth;
                        for (int x = 0; x < atlasWidth; x++)
                        {
                            var p = row[x];
                            pixels[rowBase + x] = Microsoft.Xna.Framework.Color.FromNonPremultiplied(
                                p.R, p.G, p.B, p.A);
                        }
                    }
                });
                
                var atlasTex = new Texture2D(graphicsDevice, atlasWidth, thisAtlasHeight);
                atlasTex.SetData(pixels);
                
                results.Add(new PackResult
                {
                    AtlasTexture = atlasTex,
                    FrameCoordinates = frameCoords,
                    AtlasWidth = atlasWidth,
                    AtlasHeight = thisAtlasHeight
                });
                
                frameIndex += framesInThisAtlas;
                
                Console.WriteLine($"[AtlasPacker] Atlas {atlasIdx}: {framesInThisAtlas} frames packed into {atlasWidth}x{thisAtlasHeight}");
            }
            
            Console.WriteLine($"[AtlasPacker] Successfully packed {frameCount} frames into {results.Count} atlas(es)");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[AtlasPacker] ERROR: {ex.Message}");
            Console.WriteLine($"[AtlasPacker] Stack: {ex.StackTrace}");
        }
        
        return results;
    }
    
    /// <summary>
    /// Extracts a frame from a packed atlas texture.
    /// Used by SpriteAnimator to draw individual frames.
    /// </summary>
    public static Microsoft.Xna.Framework.Rectangle GetFrameSourceRect(FrameRect frame)
    {
        return new Microsoft.Xna.Framework.Rectangle(frame.X, frame.Y, frame.Width, frame.Height);
    }
}
