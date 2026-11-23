using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using SixLabors.ImageSharp.Processing;
using FunkyMakerPlayer.Managers;

namespace FunkyMakerPlayer;

/// <summary>
/// Shared runtime texture cache entry for a single sprite animation strip/frame.
/// </summary>
public sealed class SpriteTextureCacheEntry
{
    public List<Texture2D> Textures { get; } = new();
    public List<AtlasPacker.FrameRect>? AtlasFrameRects { get; set; }
    public bool UsesPackedAtlas { get; set; }
    public int SplitOffset { get; set; } = -1;
    public int ChunkFrames { get; set; }
    public int FrameWidth { get; set; }
    public int TextureIndex { get; set; } = -1;
    public float DownscaleCompensation { get; set; } = 1.0f;
}

/// <summary>
/// Shared scene texture loader, animator factory, and ordered scene drawer used by both
/// live gameplay (<see cref="Game1"/>) and in-editor stage previews (<see cref="Editors.Chart.ChartEditor"/>).
/// </summary>
public static class FnmmSceneRenderer
{
    public const int MONOGAME_MAX_TEXTURE_SIZE = 16384;
    private const int GL_MAX_TEXTURE_SIZE = 0x0D33;
    private const float CAM_W = 1920f;
    private const float CAM_H = 1080f;

    [DllImport("opengl32.dll", EntryPoint = "glGetIntegerv")]
    private static extern void glGetIntegerv_Windows(int pname, out int value);

    [DllImport("libGL.so.1", EntryPoint = "glGetIntegerv")]
    private static extern void glGetIntegerv_Linux(int pname, out int value);

    [DllImport("libGLESv2.so", EntryPoint = "glGetIntegerv")]
    private static extern void glGetIntegerv_Android(int pname, out int value);

    /// <summary>
    /// Queries OpenGL max texture size clamped to MonoGame's internal 16384px ceiling.
    /// </summary>
    /// <summary>Images of the last <see cref="LoadLevelTextures"/> that failed to load (they are skipped,
    /// so a phone out of memory showed a black scene with no message). First error kept for the message.</summary>
    public static int LastFailedTextures { get; private set; }
    public static string? LastTextureError { get; private set; }

    private static void NoteTextureFailure(Exception ex)
    {
        LastFailedTextures++;
        LastTextureError ??= ex is OutOfMemoryException ? "out of memory" : ex.Message;
    }

    public static int GetGpuMaxTextureSize()
    {
        try
        {
            int gpuMaxSize = 0;
            try
            {
#if ANDROID
                glGetIntegerv_Android(GL_MAX_TEXTURE_SIZE, out gpuMaxSize);
#elif WINDOWS
                glGetIntegerv_Windows(GL_MAX_TEXTURE_SIZE, out gpuMaxSize);
#else
                try { glGetIntegerv_Linux(GL_MAX_TEXTURE_SIZE, out gpuMaxSize); }
                catch { glGetIntegerv_Windows(GL_MAX_TEXTURE_SIZE, out gpuMaxSize); }
#endif
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[GPU] Failed to query GPU limit via OpenGL: {ex.Message}");
            }

            if (gpuMaxSize <= 0 || gpuMaxSize > 65536)
            {
#if ANDROID
                gpuMaxSize = 4096;
#else
                gpuMaxSize = MONOGAME_MAX_TEXTURE_SIZE;
#endif
            }

            return Math.Min(gpuMaxSize, MONOGAME_MAX_TEXTURE_SIZE);
        }
        catch
        {
#if ANDROID
            return 4096;
#else
            return MONOGAME_MAX_TEXTURE_SIZE;
#endif
        }
    }

    /// <summary>
    /// Loads GPU textures for every sprite in <paramref name="level"/> using ImageSharp,
    /// AtlasPacker, chunk/tile splitting, and optional per-level texture caching.
    /// </summary>
    public static Dictionary<string, List<Texture2D>> LoadLevelTextures(
        GraphicsDevice gd,
        FnmmLevel level,
        int maxTexSize,
        Dictionary<string, Dictionary<int, SpriteTextureCacheEntry>>? persistentCache = null,
        Action<float, string>? onProgress = null,
        Action? onRenderTick = null)
    {
        LastFailedTextures = 0;
        LastTextureError = null;
        if (!Environment.Is64BitProcess)
            maxTexSize = Math.Min(maxTexSize, 4096);
        var texCache = new Dictionary<string, List<Texture2D>>(StringComparer.OrdinalIgnoreCase);

        var spriteUsage = new Dictionary<string, (bool usedByBg, bool usedByChar)>(StringComparer.OrdinalIgnoreCase);
        foreach (var obj in level.Scene.Objects)
        {
            if (!spriteUsage.TryGetValue(obj.Spr, out var usage))
                usage = (false, false);

            spriteUsage[obj.Spr] = obj.StrumLine < 0f
                ? (true, usage.usedByChar)
                : (usage.usedByBg, true);
        }

        // Clear transient split/index metadata before rebuilding textures cleanly
        foreach (var (_, (def, _)) in level.Sprites)
        {
            foreach (var animData in def.Data)
            {
                animData.SplitOffset = -1;
                animData.ChunkFrames = 0;
                animData.FrameWidth = 0;
                animData.TextureIndex = -1;
                animData.DownscaleCompensation = 1.0f;
                animData.UsesPackedAtlas = false;
                animData.AtlasFrameRects = null;
            }
        }

        int spriteIndex = 0;
        int totalSprites = level.Sprites.Count;
        int cacheHits = 0;
        var stopwatch = System.Diagnostics.Stopwatch.StartNew();
        int spritesProcessedSinceUpdate = 0;
        const int SPRITES_PER_UPDATE = 5;

        foreach (var (name, (def, frames)) in level.Sprites)
        {
            if (totalSprites > 0)
            {
                float progress = (spriteIndex / (float)totalSprites) * 0.8f;
                string step = $"Creating GPU textures... ({spriteIndex + 1}/{totalSprites})";
                spritesProcessedSinceUpdate++;
                if (onProgress != null && (spritesProcessedSinceUpdate >= SPRITES_PER_UPDATE || stopwatch.ElapsedMilliseconds >= 500))
                {
                    onProgress(progress, step);
                    stopwatch.Restart();
                    spritesProcessedSinceUpdate = 0;
                }
            }
            spriteIndex++;

            Dictionary<int, SpriteTextureCacheEntry>? spriteCache = null;
            if (persistentCache != null)
            {
                if (!persistentCache.TryGetValue(name, out spriteCache))
                {
                    spriteCache = new Dictionary<int, SpriteTextureCacheEntry>();
                    persistentCache[name] = spriteCache;
                }

                if (spriteCache.Count == frames.Count &&
                    spriteCache.All(kvp => kvp.Value.Textures.Count > 0 &&
                                           kvp.Value.Textures.All(t => t != null && !t.IsDisposed)))
                {
                    for (int fi = 0; fi < frames.Count; fi++)
                    {
                        if (!spriteCache.TryGetValue(fi, out var cached)) continue;
                        if (fi < def.Data.Count)
                        {
                            def.Data[fi].SplitOffset = cached.SplitOffset;
                            def.Data[fi].ChunkFrames = cached.ChunkFrames;
                            def.Data[fi].FrameWidth = cached.FrameWidth;
                            def.Data[fi].TextureIndex = cached.TextureIndex;
                            def.Data[fi].DownscaleCompensation = cached.DownscaleCompensation;
                            def.Data[fi].UsesPackedAtlas = cached.UsesPackedAtlas;
                            def.Data[fi].AtlasFrameRects = cached.AtlasFrameRects;
                        }
                    }

                    var cachedTextures = new List<Texture2D>();
                    for (int fi = 0; fi < frames.Count; fi++)
                    {
                        if (spriteCache.TryGetValue(fi, out var cached))
                            cachedTextures.AddRange(cached.Textures);
                    }
                    texCache[name] = cachedTextures;
                    cacheHits++;
                    continue;
                }

                spriteCache.Clear();
            }

            var textures = new List<Texture2D>();
            var frameTexRanges = new (int Start, int Count)[frames.Count];
            var (_, usedByChar) = spriteUsage.GetValueOrDefault(name, (false, false));
            bool isBgSprite = def.Type == 2.0f || (def.Type == 0.0f && !usedByChar);

#if ANDROID
            const int FRAMES_PER_UPDATE = 1;
            const int MAX_MS_BETWEEN_UPDATES = 100;
#else
            const int FRAMES_PER_UPDATE = 5;
            const int MAX_MS_BETWEEN_UPDATES = 500;
#endif
            var lastFrameUpdateTime = System.Diagnostics.Stopwatch.StartNew();

            for (int fi = 0; fi < frames.Count; fi++)
            {
                int frameTexStart = textures.Count;
                long estRawBytes = 0;
                try
                {
                    if (onRenderTick != null && (fi % FRAMES_PER_UPDATE == 0 || lastFrameUpdateTime.ElapsedMilliseconds > MAX_MS_BETWEEN_UPDATES))
                    {
                        onRenderTick();
                        lastFrameUpdateTime.Restart();
                    }

                int frameCount = fi < def.Data.Count ? def.Data[fi].FrameCount : 1;
                byte[] frameBytes = frames[fi];
                if (frameBytes.Length == 0)
                    continue;

                int pngW = 0, pngH = 0;
                if (frameBytes.Length >= 24 &&
                    frameBytes[0] == 0x89 && frameBytes[1] == 0x50 && frameBytes[2] == 0x4E && frameBytes[3] == 0x47 &&
                    frameBytes[4] == 0x0D && frameBytes[5] == 0x0A && frameBytes[6] == 0x1A && frameBytes[7] == 0x0A)
                {
                    uint rawW = ((uint)frameBytes[16] << 24) | ((uint)frameBytes[17] << 16) | ((uint)frameBytes[18] << 8) | frameBytes[19];
                    uint rawH = ((uint)frameBytes[20] << 24) | ((uint)frameBytes[21] << 16) | ((uint)frameBytes[22] << 8) | frameBytes[23];
                    if (rawW > 0 && rawW <= 2_000_000) pngW = (int)rawW;
                    if (rawH > 0 && rawH <= 2_000_000) pngH = (int)rawH;
                }

                if (pngW <= 0 || pngH <= 0)
                {
                    onProgress?.Invoke((spriteIndex / (float)Math.Max(1, totalSprites)) * 0.8f, $"Inspecting sprite: {name}");
                    try
                    {
                        using var msProbe = new MemoryStream(frameBytes, writable: false);
                        var info = SixLabors.ImageSharp.Image.Identify(msProbe);
                        pngW = info.Width;
                        pngH = info.Height;
                    }
                    catch (Exception exProbe)
                    {
                        NoteTextureFailure(exProbe);
                        Console.WriteLine($"[Texture] {name}[{fi}]: skipped — ImageSharp failed: {exProbe.Message}");
                        continue;
                    }
                }

                estRawBytes = (long)pngW * Math.Max(1, pngH) * 4L;
                int actualFrameCount = Math.Max(1, frameCount);
                int fw0 = actualFrameCount > 1 ? pngW / actualFrameCount : pngW;
                if (actualFrameCount > 1 && fw0 < 10)
                {
                    actualFrameCount = 1;
                    fw0 = pngW;
                }

                bool needsDownscale = fw0 > maxTexSize || pngH > maxTexSize ||
                                      (!Environment.Is64BitProcess && actualFrameCount > 1 &&
                                       estRawBytes > AtlasPacker.GetDefaultStripVramBudgetBytes());
                bool needsBgTile = isBgSprite && actualFrameCount == 1 && pngW > maxTexSize;
                bool needsSplit = !needsDownscale && !needsBgTile && pngW > maxTexSize && actualFrameCount > 1;

                Texture2D? fullTex = null;
                if (!needsDownscale && !needsSplit && !needsBgTile)
                {
                    try
                    {
#if BLAZORGL
                        const int BLAZOR_MAX_TEX = 4096;
                        if (pngW > BLAZOR_MAX_TEX || pngH > BLAZOR_MAX_TEX)
                        {
                            using var msDownscale = new MemoryStream(frames[fi]);
                            using var imgDownscale = SixLabors.ImageSharp.Image.Load<SixLabors.ImageSharp.PixelFormats.Rgba32>(msDownscale);
                            float ds = Math.Min((float)BLAZOR_MAX_TEX / pngW, (float)BLAZOR_MAX_TEX / pngH);
                            int newW = (int)(pngW * ds);
                            int newH = (int)(pngH * ds);
                            imgDownscale.Mutate(ctx => ctx.Resize(newW, newH,
                                SixLabors.ImageSharp.Processing.KnownResamplers.Lanczos3));
                            var pixels = new Color[newW * newH];
                            imgDownscale.ProcessPixelRows(acc =>
                            {
                                for (int y = 0; y < newH; y++)
                                {
                                    var row = acc.GetRowSpan(y);
                                    int rb = y * newW;
                                    for (int x = 0; x < newW; x++)
                                    {
                                        var p = row[x];
                                        pixels[rb + x] = Color.FromNonPremultiplied(p.R, p.G, p.B, p.A);
                                    }
                                }
                            });
                            fullTex = new Texture2D(gd, newW, newH);
                            fullTex.SetData(pixels);
                            if (fi < def.Data.Count)
                                def.Data[fi].DownscaleCompensation = 1.0f / ds;
                        }
                        else
                        {
                            fullTex = LoadTextureViaImageSharp(gd, frames[fi]);
                        }
#else
                        fullTex = LoadTextureViaImageSharp(gd, frames[fi]);
#endif
#if ANDROID
                        onRenderTick?.Invoke();
#endif
                    }
                    catch (Exception ex)
                    {
                        NoteTextureFailure(ex);
                        Console.WriteLine($"[Texture] Failed to load {name}[{fi}]: {ex.Message}");
                    }
                }

                var animData = fi < def.Data.Count ? def.Data[fi] : null;

                if (fullTex == null && needsDownscale)
                {
                    try
                    {
                        TryPackAtlas(gd, name, frameBytes, actualFrameCount, fw0, pngH, maxTexSize, textures, animData, onProgress, spriteIndex, totalSprites);
#if ANDROID
                        onRenderTick?.Invoke();
#endif
                    }
                    catch (Exception ex2)
                    {
                        NoteTextureFailure(ex2);
                        Console.WriteLine($"[Texture] {name}[{fi}]: character atlas packing failed: {ex2.Message}");
                    }
                }

                if (fullTex == null && !needsSplit && !needsBgTile && !(animData != null && animData.IsSplit))
                {
                    continue;
                }

                if (needsSplit)
                {
                    if (!TryPackAtlas(gd, name, frameBytes, actualFrameCount, fw0, pngH, maxTexSize, textures, animData, onProgress, spriteIndex, totalSprites))
                    {
                        int splitStart = textures.Count;
                        if (isBgSprite)
                        {
                            int framesPerChunk = Math.Max(1, maxTexSize / Math.Max(1, fw0));
                            SplitPngIntoChunks(gd, frameBytes, actualFrameCount, fw0, pngH, framesPerChunk, textures);
                            if (animData != null && textures.Count > splitStart)
                            {
                                animData.SplitOffset = splitStart;
                                animData.ChunkFrames = framesPerChunk;
                                animData.FrameWidth = fw0;
                            }
                        }
                        else
                        {
                            SplitPngIntoFrames(gd, frameBytes, actualFrameCount, fw0, pngH, textures);
                            if (animData != null && textures.Count > splitStart)
                            {
                                animData.SplitOffset = splitStart;
                                animData.ChunkFrames = 0;
                                animData.FrameWidth = fw0;
                            }
                        }
                    }
#if ANDROID
                    onRenderTick?.Invoke();
#endif
                    continue;
                }

                if (needsBgTile)
                {
                    int tileW = maxTexSize;
                    int splitStart = textures.Count;
                    SplitBgIntoTiles(gd, frameBytes, pngW, pngH, tileW, textures);
                    if (animData != null && textures.Count > splitStart)
                    {
                        animData.SplitOffset = splitStart;
                        animData.ChunkFrames = 1;
                        animData.FrameWidth = tileW;
                    }
#if ANDROID
                    onRenderTick?.Invoke();
#endif
                    continue;
                }

                if (isBgSprite && fullTex!.Width > maxTexSize && frameCount > 1)
                {
                    int fw = fullTex.Width / frameCount;
                    int fh = fullTex.Height;
                    int splitStart = textures.Count;
                    SplitTextureGpu(gd, fullTex, frameCount, fw, fh, textures);
                    fullTex.Dispose();
                    if (animData != null && textures.Count > splitStart)
                        animData.SplitOffset = splitStart;
                }
                else
                {
                    if (animData != null)
                        animData.TextureIndex = textures.Count;
                    textures.Add(fullTex!);
                }
                }
                finally
                {
                    if (textures.Count == frameTexStart)
                    {
                        if (fi < def.Data.Count)
                        {
                            def.Data[fi].SplitOffset = -1;
                            def.Data[fi].UsesPackedAtlas = false;
                            def.Data[fi].AtlasFrameRects = null;
                        }
                        var dummy = new Texture2D(gd, 1, 1);
                        dummy.SetData(new[] { Color.Transparent });
                        textures.Add(dummy);
                    }
                    frameTexRanges[fi] = (frameTexStart, Math.Max(0, textures.Count - frameTexStart));
                    long compactThreshold = (!Environment.Is64BitProcess ? 16L : 64L) * 1024 * 1024;
                    if (estRawBytes >= compactThreshold)
                    {
                        System.Runtime.GCSettings.LargeObjectHeapCompactionMode =
                            System.Runtime.GCLargeObjectHeapCompactionMode.CompactOnce;
                        GC.Collect(GC.MaxGeneration, GCCollectionMode.Forced, blocking: true, compacting: true);
                        GC.WaitForPendingFinalizers();
                    }
                }
            }

            texCache[name] = textures;

            if (spriteCache != null)
            {
                for (int fi = 0; fi < frames.Count; fi++)
                {
                    var animData = fi < def.Data.Count ? def.Data[fi] : null;
                    var cached = new SpriteTextureCacheEntry
                    {
                        SplitOffset = animData?.SplitOffset ?? -1,
                        ChunkFrames = animData?.ChunkFrames ?? 0,
                        FrameWidth = animData?.FrameWidth ?? 0,
                        TextureIndex = animData?.TextureIndex ?? -1,
                        DownscaleCompensation = animData?.DownscaleCompensation ?? 1.0f,
                        UsesPackedAtlas = animData?.UsesPackedAtlas ?? false,
                        AtlasFrameRects = animData?.AtlasFrameRects
                    };

                    var (texStart, texCount) = frameTexRanges[fi];
                    for (int t = texStart; t < texStart + texCount && t < textures.Count; t++)
                        cached.Textures.Add(textures[t]);

                    spriteCache[fi] = cached;
                }
            }
        }

        if (persistentCache != null && totalSprites > 0)
            Console.WriteLine($"[SceneCache] Textures: {cacheHits}/{totalSprites} sprite(s) reused from cache");

        return texCache;
    }

    private static bool TryPackAtlas(
        GraphicsDevice gd,
        string spriteName,
        byte[] frameBytes,
        int frameCount,
        int frameW,
        int frameH,
        int maxTexSize,
        List<Texture2D> textures,
        SpriteAnimData? animData,
        Action<float, string>? onProgress,
        int spriteIndex,
        int totalSprites)
    {
        var packResults = AtlasPacker.PackHorizontalStrip(
            frameBytes, frameCount, frameW, frameH, maxTexSize, gd,
            onProgress != null
                ? (curr, tot) => onProgress((spriteIndex / (float)Math.Max(1, totalSprites)) * 0.8f, $"Packing atlas: {spriteName} ({curr}/{tot} frames)")
                : null);

        if (packResults.Count == 0)
            return false;

        ApplyPackedAtlasResults(packResults, textures, animData, frameW);
        return true;
    }

    private static void ApplyPackedAtlasResults(
        List<AtlasPacker.PackResult> packResults,
        List<Texture2D> textures,
        SpriteAnimData? animData,
        int origFrameW)
    {
        int splitStart = textures.Count;
        var allRects = new List<AtlasPacker.FrameRect>();
        for (int i = 0; i < packResults.Count; i++)
        {
            textures.Add(packResults[i].AtlasTexture);
            var coords = packResults[i].FrameCoordinates;
            for (int r = 0; r < coords.Count; r++)
            {
                var rect = coords[r];
                rect.AtlasIndex = i;
                allRects.Add(rect);
            }
        }

        if (animData != null)
        {
            animData.SplitOffset = splitStart;
            animData.UsesPackedAtlas = true;
            animData.AtlasFrameRects = allRects;
            animData.FrameWidth = allRects.Count > 0 ? allRects[0].Width : origFrameW;

            float ds = packResults[0].DownscaleFactor;
            animData.DownscaleCompensation = ds > 0.001f && ds < 0.999f ? 1.0f / ds : 1.0f;
        }
    }

    /// <summary>
    /// Builds one <see cref="SpriteAnimator"/> per entry in <see cref="FnmmScene.Objects"/>,
    /// matching live gameplay transform, version, color/opacity, and character idle setup.
    /// </summary>
    public static SpriteAnimator?[] CreateAnimators(
        FnmmLevel level,
        Dictionary<string, List<Texture2D>> texCache,
        int rtW,
        int rtH)
    {
        var objs = level.Scene.Objects;
        var animators = new SpriteAnimator?[objs.Count];

        for (int i = 0; i < objs.Count; i++)
        {
            try
            {
                var obj = objs[i];
                if (!level.Sprites.TryGetValue(obj.Spr, out var sprData)) continue;
                if (!texCache.TryGetValue(obj.Spr, out var textures) || textures == null || textures.Count == 0) continue;

                var pos = obj.GetPosition();
                var scale = obj.GetScale();

                if (obj.Fixed && obj.Transform != null && obj.Transform.Count > 0)
                {
                    var t0 = obj.Transform[0];
                    bool isNorm = t0.Position.X >= 0f && t0.Position.X <= 1f &&
                                  t0.Position.Y >= 0f && t0.Position.Y <= 1f;
                    pos = new Vec2
                    {
                        X = isNorm ? t0.Position.X * rtW : t0.Position.X,
                        Y = isNorm ? t0.Position.Y * rtH : t0.Position.Y
                    };
                }

                var anim = new SpriteAnimator(obj.Spr, sprData.Def, textures,
                    new Vector2(pos.X, pos.Y),
                    new Vector2(scale.X, scale.Y))
                {
                    Bpm = level.Data.EffectiveBpm,
                    Visible = obj.Visible,
                    IsCharacter = obj.StrumLine >= 0f,
                    IsBG = sprData.Def.Type == 2.0f,
                    LevelVersion = level.Data.Version,
                    IsFixed = obj.Fixed,
                    RenderWidth = rtW,
                    RenderHeight = rtH,
                };

                if (obj.Color != null)
                    anim.TintColor = new Color((byte)obj.Color.R, (byte)obj.Color.G, (byte)obj.Color.B);
                if (obj.Opacity.HasValue)
                    anim.Opacity = obj.Opacity.Value;

                if (obj.Anim.HasValue)
                {
                    anim.SetAnimation((int)obj.Anim.Value);
                }
                else if (obj.StrumLine >= 0f && obj.Visible && sprData.Def.Data.Count > 0)
                {
                    anim.SetAnimation(SpriteAnimator.IdleAnim);
                }

                animators[i] = anim;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[FnmmSceneRenderer] Failed to create animator {i}: {ex.Message}");
                animators[i] = null;
            }
        }

        return animators;
    }

    /// <summary>
    /// Draws all scene objects in their exact scene order with world camera, fixed matrices,
    /// scrollFactor parallax, sampler state switching, and optional shader effect.
    /// </summary>
    public static void DrawSceneOrdered(
        SpriteBatch sb,
        FnmmLevel level,
        SpriteAnimator?[] animators,
        EventProcessor? events,
        CameraManager? cameraManager,
        int sw,
        int sh,
        Matrix camMatrix,
        Effect? activeEffect = null,
        bool includeFixedOverlays = true,
        Matrix? outputTransform = null)
    {
        // outputTransform (fork): logical → physical render-target scale, applied after every
        // object matrix so callers keep working in logical (e.g. 1280×720) coordinates.
        var objs = level.Scene.Objects;
        bool isLandscape = level.Scene.Landscape;
        float camScale = events?.Camera.Scale ?? 1f;
        Matrix fixedWorldMatrix = cameraManager?.BuildFixedMatrix(sw, sh, isLandscape, camScale) ?? Matrix.Identity;

        int currentMode = -1;
        bool currentBilinear = true;

        for (int i = 0; i < objs.Count; i++)
        {
            var obj = objs[i];
            if (!includeFixedOverlays && obj.Fixed && obj.StrumLine < 0f)
            {
                // Keep fixed objects that look like the actual stage background, skip small HUD/cinema overlays
                bool hasBgSprite = level.Sprites.TryGetValue(obj.Spr, out var sprCheck) && sprCheck.Def.Type == 2.0f;
                if (!hasBgSprite && i > 1) continue;
            }

            var anim = i < animators.Length ? animators[i] : null;
            if (anim == null) continue;

            bool wantBilinear = anim.CurrentBilinear;
            int wantMode;

            var pos = obj.GetPosition();
            var scale = obj.GetScale();
            bool hasTransformArray = obj.Transform != null && obj.Transform.Count > 0;
            bool hasPositionEvents = hasTransformArray && events != null && events.HasPositionEvents(i);

            if (!obj.Fixed)
            {
                wantMode = 0;
            }
            else
            {
                bool normalized = obj.X >= 0f && obj.X <= 1f && obj.Y >= 0f && obj.Y <= 1f;
                wantMode = normalized ? 1 : 2;
            }

            int matrixMode = wantMode;
            if (obj.Fixed && wantMode == 2 && hasPositionEvents)
                matrixMode = 1;

            if (matrixMode != currentMode || wantBilinear != currentBilinear)
            {
                if (currentMode >= 0) sb.End();
                Matrix? matrix = matrixMode == 0 ? camMatrix
                               : matrixMode == 2 ? fixedWorldMatrix
                               : (Matrix?)null;
                if (outputTransform.HasValue)
                    matrix = (matrix ?? Matrix.Identity) * outputTransform.Value;
                var sampler = wantBilinear ? SamplerState.LinearClamp : SamplerState.PointClamp;
                sb.Begin(SpriteSortMode.Immediate, BlendState.AlphaBlend,
                    sampler, null, null, activeEffect, matrix);
                currentMode = matrixMode;
                currentBilinear = wantBilinear;
            }

            bool hasActiveTween = events != null && events.HasActiveTween(i);

            if (!hasActiveTween)
            {
                if (obj.Fixed && wantMode == 2 && hasPositionEvents)
                {
                    // EventProcessor owns anim.Position via tween
                }
                else if (!hasPositionEvents || hasTransformArray)
                {
                    Vector2 finalPos = new Vector2(pos.X, pos.Y);
                    if (hasTransformArray)
                    {
                        Vec2 scrollFactor = obj.GetScrollFactor();
                        var cam = events?.Camera;
                        if (cam != null && (Math.Abs(scrollFactor.X - 1f) > 0.001f || Math.Abs(scrollFactor.Y - 1f) > 0.001f))
                        {
                            float clampedX = Math.Clamp(scrollFactor.X, 0.01f, 4.0f);
                            float clampedY = Math.Clamp(scrollFactor.Y, 0.01f, 4.0f);
                            finalPos.X = pos.X + cam.X * (1.0f - clampedX);
                            finalPos.Y = pos.Y + cam.Y * (1.0f - clampedY);
                        }
                    }

                    anim.Position = finalPos;
                    anim.Scale = new Vector2(scale.X, scale.Y);
                }
            }

            if (obj.Fixed)
            {
                if (wantMode == 1 && !hasActiveTween)
                    anim.Position = new Vector2(pos.X * sw, pos.Y * sh);

                float actual = Math.Min((float)sw / (CAM_W * camScale), (float)sh / (CAM_H * camScale));
                float basePs = Math.Min((float)sw / CAM_W, (float)sh / CAM_H);
                anim.FixedPixelScale = Math.Min(actual, basePs);
            }
            else
            {
                anim.FixedPixelScale = 0f;
            }

            anim.Draw(sb);
        }

        if (currentMode >= 0) sb.End();
    }

    /// <summary>
    /// Creates a lightweight isolated copy of a level's Scene, Events, Data metadata, and sprite
    /// definitions (sharing raw PNG byte buffers and note lists) so an editor preview can run
    /// <see cref="EventProcessor"/> and seek freely without mutating the saveable source level.
    /// </summary>
    public static FnmmLevel CloneForStagePreview(FnmmLevel source) =>
        CloneLevelCore(source, cloneNotesAndLooseData: false);

    /// <summary>
    /// Creates an isolated in-memory gameplay clone of <paramref name="source"/> (deep-copying
    /// notes, scene objects, events, and sprite runtime metadata while sharing immutable raw PNG/OGG
    /// byte buffers) so editor playtesting can start without serializing or reloading disk archives.
    /// </summary>
    public static FnmmLevel CloneForGameplay(FnmmLevel source) =>
        CloneLevelCore(source, cloneNotesAndLooseData: true);

    private static FnmmLevel CloneLevelCore(FnmmLevel source, bool cloneNotesAndLooseData)
    {
        var strumlines = source.Data.Strumlines;
        if (cloneNotesAndLooseData)
        {
            var clonedStrumlines = new List<List<List<FnmmNote>>>(source.Data.Strumlines.Count);
            foreach (var sl in source.Data.Strumlines)
            {
                var slCopy = new List<List<FnmmNote>>(sl.Count);
                foreach (var lane in sl)
                {
                    var laneCopy = new List<FnmmNote>(lane.Count);
                    foreach (var note in lane)
                    {
                        laneCopy.Add(new FnmmNote
                        {
                            Start = note.Start,
                            Duration = note.Duration,
                            Props = new NoteProps
                            {
                                Skin = note.Props.Skin,
                                OnPressEvent = note.Props.OnPressEvent,
                                OnMissEvent = note.Props.OnMissEvent
                            }
                        });
                    }
                    slCopy.Add(laneCopy);
                }
                clonedStrumlines.Add(slCopy);
            }
            strumlines = clonedStrumlines;
        }

        var clone = new FnmmLevel
        {
            LevelName = source.LevelName,
            Meta = source.Meta,
            Data = new FnmmData
            {
                Bpm = source.Data.Bpm,
                Version = source.Data.Version,
                Npb = source.Data.Npb,
                Speed = source.Data.Speed,
                Strumlines = strumlines,
            },
            Scene = new FnmmScene
            {
                Landscape = source.Scene.Landscape,
                Icons = new List<string>(source.Scene.Icons),
            },
            Events = new FnmmEvents(),
            LooseImages = cloneNotesAndLooseData
                ? new Dictionary<string, byte[]>(source.LooseImages, StringComparer.OrdinalIgnoreCase)
                : new Dictionary<string, byte[]>(StringComparer.OrdinalIgnoreCase),
            MusicData = cloneNotesAndLooseData ? source.MusicData : null,
        };

        foreach (var cam in source.Scene.Cameras)
        {
            clone.Scene.Cameras.Add(new SceneCamera
            {
                X = cam.X,
                Y = cam.Y,
                Scale = cam.Scale,
                NestedPosition = cam.NestedPosition == null ? null : new SceneCamera.Position
                {
                    X = cam.NestedPosition.X,
                    Y = cam.NestedPosition.Y
                },
                UsesNestedPositionFormat = cam.UsesNestedPositionFormat,
            });
        }

        foreach (var obj in source.Scene.Objects)
        {
            var copy = new SceneObject
            {
                Spr = obj.Spr,
                Visible = obj.Visible,
                Fixed = obj.Fixed,
                StrumLine = obj.StrumLine,
                Camera = obj.Camera,
                X = obj.X,
                Y = obj.Y,
                Scale = obj.Scale == null ? null : new Vec2 { X = obj.Scale.X, Y = obj.Scale.Y },
                Color = obj.Color == null ? null : new RgbColor { R = obj.Color.R, G = obj.Color.G, B = obj.Color.B },
                Opacity = obj.Opacity,
                Anim = obj.Anim,
            };

            if (obj.Transform != null)
            {
                copy.Transform = obj.Transform.Select(t => new Transform
                {
                    Position = new Vec2 { X = t.Position.X, Y = t.Position.Y },
                    Scale = new Vec2 { X = t.Scale.X, Y = t.Scale.Y },
                    ScrollFactor = new Vec2 { X = t.ScrollFactor.X, Y = t.ScrollFactor.Y },
                }).ToList();
            }

            copy.SaveInitialState();
            clone.Scene.Objects.Add(copy);
        }

        foreach (var ev in source.Events.Events)
        {
            clone.Events.Events.Add(new FnmmEvent
            {
                Time = ev.Time,
                Actions = ev.Actions.Select(a => new EventAction
                {
                    Type = a.Type,
                    Props = a.Props,
                }).ToList(),
            });
        }

        clone.Events.ActionPacks.AddRange(source.Events.ActionPacks);

        foreach (var (name, (def, frames)) in source.Sprites)
        {
            var defCopy = new FnmmSprite
            {
                Type = def.Type,
                Data = def.Data.Select(d => new SpriteAnimData
                {
                    Bilinear = d.Bilinear,
                    Loop = d.Loop,
                    Num = d.Num,
                    Spd = d.Spd,
                    SpdFps = d.SpdFps,
                    Offset = new Vec2 { X = d.Offset.X, Y = d.Offset.Y },
                    Scale = d.Scale,
                }).ToList(),
            };
            clone.Sprites[name] = (defCopy, frames);
        }

        return clone;
    }

    public static void SplitTextureGpu(GraphicsDevice gd, Texture2D source, int frameCount, int fw, int fh, List<Texture2D> output)
    {
        var sb = new SpriteBatch(gd);
        for (int f = 0; f < frameCount; f++)
        {
            var rt = new RenderTarget2D(gd, fw, fh,
                false, SurfaceFormat.Color, DepthFormat.None,
                0, RenderTargetUsage.PreserveContents);

            gd.SetRenderTarget(rt);
            gd.Clear(Color.Transparent);

            sb.Begin(samplerState: SamplerState.PointClamp);
            sb.Draw(source,
                destinationRectangle: new Rectangle(0, 0, fw, fh),
                sourceRectangle: new Rectangle(f * fw, 0, fw, fh),
                color: Color.White);
            sb.End();

            gd.SetRenderTarget(null);
            output.Add(rt);
        }
        sb.Dispose();
    }

    public static void SplitPngIntoChunks(GraphicsDevice gd, byte[] pngBytes, int frameCount, int fw, int fh, int framesPerChunk, List<Texture2D> output)
    {
        try
        {
            using var img = SixLabors.ImageSharp.Image.Load<SixLabors.ImageSharp.PixelFormats.Rgba32>(pngBytes);
            int chunkCount = (frameCount + framesPerChunk - 1) / framesPerChunk;

            for (int c = 0; c < chunkCount; c++)
            {
                int firstFrame = c * framesPerChunk;
                int framesInChunk = Math.Min(framesPerChunk, frameCount - firstFrame);
                int chunkW = framesInChunk * fw;

                var cropRect = new SixLabors.ImageSharp.Rectangle(firstFrame * fw, 0, chunkW, fh);
                using var chunk = img.Clone(ctx => ctx.Crop(cropRect));

                var chunkTex = new Texture2D(gd, chunkW, fh);
                var chunkPixels = new Color[chunkW * fh];
                chunk.ProcessPixelRows(accessor =>
                {
                    for (int row = 0; row < accessor.Height; row++)
                    {
                        var rowSpan = accessor.GetRowSpan(row);
                        int rowBase = row * chunkW;
                        for (int x = 0; x < chunkW; x++)
                        {
                            var p = rowSpan[x];
                            chunkPixels[rowBase + x] = Color.FromNonPremultiplied(p.R, p.G, p.B, p.A);
                        }
                    }
                });

                chunkTex.SetData(chunkPixels);
                output.Add(chunkTex);
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[SplitChunk] ImageSharp failed: {ex.Message}");
        }
    }

    public static void SplitPngIntoFrames(GraphicsDevice gd, byte[] pngBytes, int frameCount, int fw, int fh, List<Texture2D> output)
    {
        try
        {
            using var img = SixLabors.ImageSharp.Image.Load<SixLabors.ImageSharp.PixelFormats.Rgba32>(pngBytes);
            for (int f = 0; f < frameCount; f++)
            {
                var cropRect = new SixLabors.ImageSharp.Rectangle(f * fw, 0, fw, fh);
                using var frame = img.Clone(ctx => ctx.Crop(cropRect));

                var frameTex = new Texture2D(gd, fw, fh);
                var framePixels = new Color[fw * fh];
                frame.ProcessPixelRows(accessor =>
                {
                    for (int row = 0; row < accessor.Height; row++)
                    {
                        var rowSpan = accessor.GetRowSpan(row);
                        int rowBase = row * fw;
                        for (int x = 0; x < fw; x++)
                        {
                            var p = rowSpan[x];
                            framePixels[rowBase + x] = Color.FromNonPremultiplied(p.R, p.G, p.B, p.A);
                        }
                    }
                });

                frameTex.SetData(framePixels);
                output.Add(frameTex);
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[SplitPng] ImageSharp failed: {ex.Message}");
        }
    }

    public static void SplitBgIntoTiles(GraphicsDevice gd, byte[] pngBytes, int pngW, int pngH, int tileW, List<Texture2D> output)
    {
        try
        {
            using var img = SixLabors.ImageSharp.Image.Load<SixLabors.ImageSharp.PixelFormats.Rgba32>(pngBytes);
            int tileCount = (int)Math.Ceiling((double)pngW / tileW);
            for (int t = 0; t < tileCount; t++)
            {
                int x0 = t * tileW;
                int actualW = Math.Min(tileW, pngW - x0);
                var cropRect = new SixLabors.ImageSharp.Rectangle(x0, 0, actualW, pngH);
                using var tile = img.Clone(ctx => ctx.Crop(cropRect));

                var tileTex = new Texture2D(gd, actualW, pngH);
                var tilePixels = new Color[actualW * pngH];
                tile.ProcessPixelRows(accessor =>
                {
                    for (int row = 0; row < accessor.Height; row++)
                    {
                        var rowSpan = accessor.GetRowSpan(row);
                        int rowBase = row * actualW;
                        for (int x = 0; x < actualW; x++)
                        {
                            var p = rowSpan[x];
                            tilePixels[rowBase + x] = Color.FromNonPremultiplied(p.R, p.G, p.B, p.A);
                        }
                    }
                });
                tileTex.SetData(tilePixels);
                output.Add(tileTex);
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[BgTile] ImageSharp failed: {ex.Message}");
        }
    }

    /// <summary>
    /// Triggers the sing animation (1..4) and optional hold lock for all non-event-locked (including hidden)
    /// characters assigned to <paramref name="strumlineIndex"/>.
    /// </summary>
    public static void TriggerCharacterSing(
        FnmmLevel level,
        SpriteAnimator?[] animators,
        int strumlineIndex,
        int lane,
        float songTime,
        float holdEnd = -1f)
    {
        var objs = level.Scene.Objects;
        for (int j = 0; j < objs.Count && j < animators.Length; j++)
        {
            if ((int)objs[j].StrumLine != strumlineIndex) continue;
            var anim = animators[j];
            if (anim == null) continue;
            if (anim.IsSetByEvent) continue;

            int singAnim = Math.Clamp(lane + 1, 1, Math.Max(1, anim.AnimCount - 1));
            anim.SetAnimation(singAnim, setByEvent: false);
            if (holdEnd > songTime)
                anim.HoldUntil = holdEnd;
        }
    }

    /// <summary>
    /// Computes an overview camera framing all visible stage objects (plus camera targets as fallback).
    /// </summary>
    public static (float x, float y, float scale) ComputeFitCamera(FnmmLevel level, SpriteAnimator?[] animators)
    {
        float minX = float.MaxValue, minY = float.MaxValue;
        float maxX = float.MinValue, maxY = float.MinValue;
        int count = 0;

        var objs = level.Scene.Objects;
        for (int i = 0; i < objs.Count; i++)
        {
            var obj = objs[i];
            if (obj.Fixed || !obj.Visible) continue;
            var anim = i < animators.Length ? animators[i] : null;
            Vector2 pos = anim != null ? anim.Position : new Vector2(obj.GetPosition().X, obj.GetPosition().Y);
            Vector2 size = anim != null
                ? new Vector2(
                    Math.Abs(anim.CurrentFrameSize.X * anim.Scale.X * Math.Max(0.1f, anim.CurrentSprScale)),
                    Math.Abs(anim.CurrentFrameSize.Y * anim.Scale.Y * Math.Max(0.1f, anim.CurrentSprScale)))
                : new Vector2(200f, 200f);

            float halfW = Math.Clamp(size.X * 0.5f, 80f, 1600f);
            float halfH = Math.Clamp(size.Y * 0.5f, 80f, 1200f);
            minX = Math.Min(minX, pos.X - halfW);
            maxX = Math.Max(maxX, pos.X + halfW);
            minY = Math.Min(minY, pos.Y - halfH);
            maxY = Math.Max(maxY, pos.Y + halfH);
            count++;
        }

        foreach (var c in level.Scene.Cameras)
        {
            minX = Math.Min(minX, c.X - 350f);
            maxX = Math.Max(maxX, c.X + 350f);
            minY = Math.Min(minY, c.Y - 220f);
            maxY = Math.Max(maxY, c.Y + 220f);
            count++;
        }

        if (count == 0 || minX >= maxX || minY >= maxY)
            return (1080f, 760f, 1.0f);

        float cx = (minX + maxX) * 0.5f;
        float cy = (minY + maxY) * 0.5f;
        float spanW = Math.Max(640f, maxX - minX);
        float spanH = Math.Max(360f, maxY - minY);
        float scale = Math.Clamp(Math.Max(spanW / CAM_W, spanH / CAM_H) * 1.08f, 0.55f, 2.5f);
        return (cx, cy, scale);
    }

    /// <summary>
    /// Decodes a PNG from raw bytes via ImageSharp (avoiding DirectX PNG decoder limitations)
    /// and uploads RGBA32 pixels padded to 4px width alignment for DirectX/MonoGame compatibility.
    /// </summary>
    public static Texture2D? LoadTextureViaImageSharp(GraphicsDevice gd, byte[] pngBytes)
    {
        using var ms = new MemoryStream(pngBytes);
        var decoderOptions = new SixLabors.ImageSharp.Formats.DecoderOptions { SkipMetadata = true };
        using var img = SixLabors.ImageSharp.Image.Load<SixLabors.ImageSharp.PixelFormats.Rgba32>(decoderOptions, ms);

        int srcW = img.Width;
        int srcH = img.Height;
        if (srcW <= 0 || srcH <= 0 || srcW > 16384 || srcH > 16384) return null;

        int texW = (srcW + 3) & ~3;
        int texH = srcH;

        SixLabors.ImageSharp.Image<SixLabors.ImageSharp.PixelFormats.Rgba32> source = img;
        SixLabors.ImageSharp.Image<SixLabors.ImageSharp.PixelFormats.Rgba32>? padded = null;

        if (texW != srcW)
        {
            padded = new SixLabors.ImageSharp.Image<SixLabors.ImageSharp.PixelFormats.Rgba32>(texW, texH);
            img.ProcessPixelRows(padded, (srcAcc, dstAcc) =>
            {
                for (int y = 0; y < srcH; y++)
                    srcAcc.GetRowSpan(y).CopyTo(dstAcc.GetRowSpan(y));
            });
            source = padded;
        }

        var pixels = new Color[texW * texH];
        source.ProcessPixelRows(accessor =>
        {
            for (int y = 0; y < texH; y++)
            {
                var row = accessor.GetRowSpan(y);
                int rowBase = y * texW;
                for (int x = 0; x < texW; x++)
                {
                    var p = row[x];
                    pixels[rowBase + x] = Color.FromNonPremultiplied(p.R, p.G, p.B, p.A);
                }
            }
        });

        padded?.Dispose();

        var tex = new Texture2D(gd, texW, texH, false, SurfaceFormat.Color);
        try
        {
            tex.SetData(pixels);
            return tex;
        }
        catch (Exception ex)
        {
            tex.Dispose();
            NoteTextureFailure(ex);
            Console.WriteLine($"[Texture] SetData failed {texW}x{texH} (src {srcW}x{srcH}): {ex.Message}");
            return null;
        }
    }
}
