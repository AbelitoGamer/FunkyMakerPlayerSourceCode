using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text.Json;
using SharpCompress.Archives;
using SharpCompress.Common;

namespace FunkyMakerPlayer;

/// <summary>
/// Loads a .FNMM file.
/// Supports two container formats:
///   • ZIP  (PK header) — native format used by Funky Maker itself.
///   • RAR  (Rar! header) — older export format.
///
/// Internal layout variants:
///   • Flat  — files at root: data.json, scene.json, events.json, music.ogg, sprites/…
///   • Nested — files inside a single sub-folder (UUID-named).
/// </summary>
public static class FnmmLoader
{
    private static readonly JsonSerializerOptions _json = new()
    {
        PropertyNameCaseInsensitive = true,
        AllowTrailingCommas         = true,
        ReadCommentHandling         = JsonCommentHandling.Skip,
    };

    // ── Public entry point ──────────────────────────────────────────────────

    public static FnmmLevel Load(string fnmmPath, Action<float, string>? progressCallback = null)
    {
        // osu!mania difficulty (fork, Osu/): converted in memory
        if (Osu.OsuLibrary.IsOsuChart(fnmmPath))
            return Osu.OsuLevelLoader.Load(fnmmPath, progressCallback);

        progressCallback?.Invoke(0.05f, "Reading archive...");
        
        // Psych Engine mod folder or unpacked Funky Maker level folder
        if (Directory.Exists(fnmmPath))
        {
            string? unpackedLevelDir = FindLevelDir(fnmmPath);
            if (unpackedLevelDir != null)
            {
                return LoadFromDirectory(unpackedLevelDir, Path.GetFileName(fnmmPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)), progressCallback);
            }

            // Check if it's a Psych Engine mod
            string dataDir = Path.Combine(fnmmPath, "data");
            if (Directory.Exists(dataDir))
            {
                throw new Exception("PSYCH_ENGINE_MOD"); // Special marker for Game1 to handle
            }
            throw new Exception($"Not a valid level folder: {fnmmPath}");
        }

        byte[] header = new byte[4];
        using (var fs = File.OpenRead(fnmmPath))
            _ = fs.Read(header, 0, 4);

        progressCallback?.Invoke(0.1f, "Reading archive...");

        bool isZip = header[0] == 0x50 && header[1] == 0x4B; // PK (.fnmm or .zip)
        bool isRar = header[0] == 0x52 && header[1] == 0x61; // Ra (.rar)

        if (isZip)
        {
            try
            {
                // Check if old format and convert if needed
                byte[]? converted = FnmmConverter.ConvertIfNeeded(fnmmPath);
                if (converted != null)
                {
                    // Load from converted in-memory bytes
                    using var ms = new MemoryStream(converted);
                    using var zip = new ZipArchive(ms, ZipArchiveMode.Read);
                    return LoadFromZipArchive(zip, Path.GetFileNameWithoutExtension(fnmmPath), progressCallback);
                }
                return LoadFromZip(fnmmPath, progressCallback);
            }
            catch
            {
                // Fallback to SharpCompress extraction (same logic as .rar) — handles .zip files
                // that contain an inner .fnmm/.rar archive or non-standard directory structures
                return LoadFromRar(fnmmPath, progressCallback);
            }
        }
        if (isRar)
            return LoadFromRar(fnmmPath, progressCallback);

        // Final fallback: attempt SharpCompress extraction for any other supported archive container
        try
        {
            return LoadFromRar(fnmmPath, progressCallback);
        }
        catch
        {
            throw new Exception($"Unknown level container format (header: {header[0]:X2} {header[1]:X2})");
        }
    }

    /// <summary>
    /// Reads only a level's data.json (fork: freeplay difficulty). Folders and ZIP .fnmm are read
    /// directly; old-format ZIPs, RAR and other containers go through the full <see cref="Load"/>.
    /// Returns null if the chart can't be read.
    /// </summary>
    public static FnmmData? TryLoadChartData(string path)
    {
        try
        {
            if (Osu.OsuLibrary.IsOsuChart(path))
                return Osu.OsuLevelLoader.LoadChartData(path);
            if (Directory.Exists(path))
            {
                string? dir = FindLevelDir(path);
                return dir == null ? null : ParseJson<FnmmData>(File.ReadAllText(Path.Combine(dir, "data.json")));
            }

            byte[] header = new byte[2];
            using (var fs = File.OpenRead(path))
                _ = fs.Read(header, 0, 2);

            if (header[0] == 0x50 && header[1] == 0x4B)
            {
                try
                {
                    byte[]? converted = FnmmConverter.ConvertIfNeeded(path);
                    using var zip = converted != null
                        ? new ZipArchive(new MemoryStream(converted), ZipArchiveMode.Read)
                        : ZipFile.OpenRead(path);
                    var entry = zip.GetEntry("data.json")
                        ?? zip.Entries.FirstOrDefault(e => e.FullName.EndsWith("/data.json", StringComparison.OrdinalIgnoreCase));
                    if (entry != null)
                    {
                        using var sr = new StreamReader(entry.Open());
                        return ParseJson<FnmmData>(sr.ReadToEnd());
                    }
                }
                catch { /* fall through to the full loader */ }
            }

            return Load(path).Data;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Opens a level and lets <paramref name="use"/> read single files by their path inside the level
    /// ("music.ogg", "scene.json", "sprites/x/0.png"; null when missing), without loading the rest
    /// (fork: Freeplay audio preview and icons). Folders and ZIP .fnmm only (old-format ZIPs are
    /// converted in memory); RAR and other containers return default.
    /// </summary>
    public static T? WithLevelFiles<T>(string path, Func<Func<string, byte[]?>, T?> use)
    {
        try
        {
            if (Directory.Exists(path))
            {
                string? dir = FindLevelDir(path);
                if (dir == null) return default;
                return use(rel =>
                {
                    string f = Path.Combine(dir, rel.Replace('/', Path.DirectorySeparatorChar));
                    return File.Exists(f) ? File.ReadAllBytes(f) : null;
                });
            }

            byte[] header = new byte[2];
            using (var fs = File.OpenRead(path))
                _ = fs.Read(header, 0, 2);
            if (header[0] != 0x50 || header[1] != 0x4B) return default;

            byte[]? converted = FnmmConverter.ConvertIfNeeded(path);
            using var zip = converted != null
                ? new ZipArchive(new MemoryStream(converted), ZipArchiveMode.Read)
                : ZipFile.OpenRead(path);
            string prefix = "";
            if (zip.GetEntry("data.json") == null)
            {
                var nested = zip.Entries.FirstOrDefault(e => e.FullName.EndsWith("/data.json", StringComparison.OrdinalIgnoreCase));
                if (nested == null) return default;
                prefix = nested.FullName[..^"data.json".Length];
            }
            return use(rel => TryReadZipBytes(zip, prefix + rel));
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[FnmmLoader] WithLevelFiles({Path.GetFileName(path)}) failed: {ex.Message}");
            return default;
        }
    }

    /// <summary>Parses a level JSON file (same rules as the loader), or null if it's invalid.</summary>
    public static T? TryParseJson<T>(byte[]? utf8) where T : class
    {
        if (utf8 == null) return null;
        try
        {
            using var sr = new StreamReader(new MemoryStream(utf8)); // drops a BOM, like the loader's readers
            return ParseJson<T>(sr.ReadToEnd());
        }
        catch { return null; }
    }

    // ── ZIP loader ──────────────────────────────────────────────────────────

    private static FnmmLevel LoadFromZip(string path, Action<float, string>? progressCallback = null)
    {
        using var zip = ZipFile.OpenRead(path);
        return LoadFromZipArchive(zip, Path.GetFileNameWithoutExtension(path), progressCallback);
    }

    private static FnmmLevel LoadFromZipArchive(ZipArchive zip, string fallbackName, Action<float, string>? progressCallback = null)
    {
        progressCallback?.Invoke(0.15f, "Parsing level data...");
        
        // Detect layout: flat (data.json at root) or nested (subfolder/data.json)
        string prefix = "";
        if (zip.GetEntry("data.json") == null)
        {
            var nested = zip.Entries
                .FirstOrDefault(e => e.FullName.EndsWith("/data.json", StringComparison.OrdinalIgnoreCase));
            if (nested == null)
                throw new Exception("data.json not found in .FNMM archive.");
            prefix = nested.FullName[..^"data.json".Length];
        }

        var level = new FnmmLevel();
        level.LevelName = fallbackName;

        // ── Core JSONs ───────────────────────────────────────────────────────
        progressCallback?.Invoke(0.25f, "Parsing level data...");
        level.Data  = ParseJson<FnmmData>(ReadZipText(zip, prefix + "data.json"));

        string? sceneText  = TryReadZipText(zip, prefix + "scene.json");
        string? eventsText = TryReadZipText(zip, prefix + "events.json");
        level.Scene  = sceneText  != null ? ParseJson<FnmmScene> (sceneText)  : new FnmmScene();
        level.Events = eventsText != null ? ParseJson<FnmmEvents>(eventsText) : new FnmmEvents();

        // Detect if cameras use nested position format {position: {x, y}, scale}
        if (sceneText != null)
            DetectNestedCameraFormat(level.Scene, sceneText);

        progressCallback?.Invoke(0.35f, "Parsing level data...");
        
        string? metaText = TryReadZipText(zip, prefix + "meta.json");
        if (metaText != null)
        {
            level.Meta = ParseJson<FnmmMeta>(metaText);
            if (!string.IsNullOrEmpty(level.Meta.Name))
                level.LevelName = level.Meta.Name;
        }

        // ── Music ────────────────────────────────────────────────────────────
        progressCallback?.Invoke(0.45f, "Loading audio...");
        level.MusicData = TryReadZipBytes(zip, prefix + "music.ogg");

        // ── Loose root PNGs ──────────────────────────────────────────────────
        progressCallback?.Invoke(0.55f, "Loading textures...");
        foreach (var entry in zip.Entries)
        {
            string rel = entry.FullName;
            if (!string.IsNullOrEmpty(prefix) && rel.StartsWith(prefix))
                rel = rel[prefix.Length..];
            if (!rel.Contains('/') && rel.EndsWith(".png", StringComparison.OrdinalIgnoreCase))
            {
                string key = Path.GetFileNameWithoutExtension(rel);
                level.LooseImages[key] = ReadZipBytes(zip, entry.FullName);
            }
        }

        // ── Sprites ──────────────────────────────────────────────────────────
        progressCallback?.Invoke(0.65f, "Loading textures...");
        string spritesPrefix = prefix + "sprites/";
        var spriteEntries = zip.Entries
            .Where(e => e.FullName.StartsWith(spritesPrefix, StringComparison.OrdinalIgnoreCase))
            .ToList();

        var spriteNames = spriteEntries
            .Select(e =>
            {
                string rel = e.FullName[spritesPrefix.Length..];
                int slash = rel.IndexOf('/');
                return slash >= 0 ? rel[..slash] : null;
            })
            .Where(n => n != null)
            .Distinct()
            .ToList();

        int spriteCount = 0;
        int totalSprites = spriteNames.Count;
        foreach (var sprName in spriteNames)
        {
            // Report progress while loading sprites (65% to 90%)
            float spriteProgress = 0.65f + (spriteCount / (float)Math.Max(1, totalSprites)) * 0.25f;
            progressCallback?.Invoke(spriteProgress, "Loading textures...");
            spriteCount++;
            
            string sprPrefix = spritesPrefix + sprName + "/";
            string? jsonText = TryReadZipText(zip, sprPrefix + "sprite.json");
            if (jsonText == null) continue;

            var def = ParseJson<FnmmSprite>(jsonText);

            // Map frames by numeric filename to preserve index alignment
            // (missing files leave a gap filled with empty bytes → dummy texture)
            var pngEntries = spriteEntries
                .Where(e => e.FullName.StartsWith(sprPrefix) &&
                            e.FullName.EndsWith(".png", StringComparison.OrdinalIgnoreCase))
                .Select(e => (idx: int.TryParse(Path.GetFileNameWithoutExtension(e.FullName), out int i) ? i : -1, entry: e))
                .Where(x => x.idx >= 0)
                .OrderBy(x => x.idx)
                .ToList();

            int maxIdx = pngEntries.Count > 0 ? pngEntries.Max(x => x.idx) : -1;
            var frames = new List<byte[]>();
            for (int fi = 0; fi <= maxIdx; fi++)
            {
                var match = pngEntries.FirstOrDefault(x => x.idx == fi);
                frames.Add(match.entry != null
                    ? ReadZipBytes(zip, match.entry.FullName)
                    : Array.Empty<byte>());
            }

            level.Sprites[sprName!] = (def, frames);
        }

        // Save initial state of all scene objects so they can be reset on restart
        progressCallback?.Invoke(0.95f, "Finalizing...");
        foreach (var obj in level.Scene.Objects)
            obj.SaveInitialState();

        progressCallback?.Invoke(1.0f, "Complete!");
        return level;
    }

    // ── Archive extraction loader (RAR / ZIP / nested containers) ──────────

    private static FnmmLevel LoadFromRar(string path, Action<float, string>? progressCallback = null)
    {
        progressCallback?.Invoke(0.15f, "Extracting archive...");
        string tmp = Path.Combine(Path.GetTempPath(), "fnmm_" + Path.GetRandomFileName());
        Directory.CreateDirectory(tmp);

        try
        {
            ExtractRar(path, tmp);
            progressCallback?.Invoke(0.3f, "Parsing level data...");
            string? levelDir = FindLevelDir(tmp);
            if (levelDir != null)
            {
                FnmmConverter.ConvertDirectoryIfNeeded(levelDir);
                return LoadFromDirectory(levelDir, Path.GetFileNameWithoutExtension(path), progressCallback);
            }

            // Check if the archive contains an inner .fnmm / .zip / .rar level package
            var innerArchive = Directory.EnumerateFiles(tmp, "*.*", SearchOption.AllDirectories)
                .FirstOrDefault(f =>
                    f.EndsWith(".fnmm", StringComparison.OrdinalIgnoreCase) ||
                    f.EndsWith(".zip",  StringComparison.OrdinalIgnoreCase) ||
                    f.EndsWith(".rar",  StringComparison.OrdinalIgnoreCase));
            if (innerArchive != null)
            {
                var loaded = Load(innerArchive, progressCallback);
                if (string.IsNullOrEmpty(loaded.Meta.Name))
                    loaded.LevelName = Path.GetFileNameWithoutExtension(path);
                return loaded;
            }

            throw new Exception("No data.json or .FNMM level found in archive.");
        }
        finally
        {
            try { Directory.Delete(tmp, true); } catch { }
        }
    }

    private static FnmmLevel LoadFromDirectory(string dir, string fallbackName, Action<float, string>? progressCallback = null)
    {
        var level = new FnmmLevel { LevelName = fallbackName };

        level.Data  = ParseJson<FnmmData>(File.ReadAllText(Path.Combine(dir, "data.json")));

        string scenePath  = Path.Combine(dir, "scene.json");
        string eventsPath = Path.Combine(dir, "events.json");
        level.Scene  = File.Exists(scenePath)  ? ParseJson<FnmmScene> (File.ReadAllText(scenePath))  : new FnmmScene();
        level.Events = File.Exists(eventsPath) ? ParseJson<FnmmEvents>(File.ReadAllText(eventsPath)) : new FnmmEvents();

        // Detect if cameras use nested position format {position: {x, y}, scale}
        if (File.Exists(scenePath))
            DetectNestedCameraFormat(level.Scene, File.ReadAllText(scenePath));

        string metaPath = Path.Combine(dir, "meta.json");
        if (File.Exists(metaPath))
        {
            level.Meta = ParseJson<FnmmMeta>(File.ReadAllText(metaPath));
            if (!string.IsNullOrEmpty(level.Meta.Name))
                level.LevelName = level.Meta.Name;
        }

        string musicPath = Path.Combine(dir, "music.ogg");
        if (File.Exists(musicPath))
            level.MusicData = File.ReadAllBytes(musicPath);

        // Loose PNGs
        foreach (var f in Directory.EnumerateFiles(dir, "*.png"))
            level.LooseImages[Path.GetFileNameWithoutExtension(f)] = File.ReadAllBytes(f);

        // Sprites
        string spritesDir = Path.Combine(dir, "sprites");
        if (Directory.Exists(spritesDir))
        {
            foreach (var sprDir in Directory.EnumerateDirectories(spritesDir))
            {
                string sprName = Path.GetFileName(sprDir);
                string sprJson = Path.Combine(sprDir, "sprite.json");
                if (!File.Exists(sprJson)) continue;

                var def = ParseJson<FnmmSprite>(File.ReadAllText(sprJson));

                // Map frames by their numeric filename so missing files leave gaps
                // (e.g. if 0.png is missing, index 0 = empty, 1.png → index 1, etc.)
                var pngFiles = Directory.EnumerateFiles(sprDir, "*.png")
                    .Select(f => (idx: int.TryParse(Path.GetFileNameWithoutExtension(f), out int i) ? i : -1, path: f))
                    .Where(x => x.idx >= 0)
                    .OrderBy(x => x.idx)
                    .ToList();

                int maxIdx = pngFiles.Count > 0 ? pngFiles.Max(x => x.idx) : -1;
                var frames = new List<byte[]>();
                for (int fi = 0; fi <= maxIdx; fi++)
                {
                    var match = pngFiles.FirstOrDefault(x => x.idx == fi);
                    frames.Add(match.path != null
                        ? File.ReadAllBytes(match.path)
                        : Array.Empty<byte>());  // empty = missing frame → dummy texture
                }

                level.Sprites[sprName] = (def, frames);
            }
        }

        // Save initial state of all scene objects so they can be reset on restart
        foreach (var obj in level.Scene.Objects)
            obj.SaveInitialState();

        return level;
    }

    // ── RAR extraction ──────────────────────────────────────────────────────

    private static void ExtractRar(string rarPath, string outDir)
    {
        // Use SharpCompress — no external tools needed
        using var archive = ArchiveFactory.Open(rarPath);
        foreach (var entry in archive.Entries)
        {
            if (entry.IsDirectory) continue;
            string destPath = Path.Combine(outDir, entry.Key!.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(destPath)!);
            entry.WriteToFile(destPath, new ExtractionOptions { Overwrite = true });
        }
    }

    private static string? FindLevelDir(string root)
    {
        foreach (var f in Directory.EnumerateFiles(root, "data.json", SearchOption.AllDirectories))
            return Path.GetDirectoryName(f);
        return null;
    }

    // ── ZIP helpers ─────────────────────────────────────────────────────────

    private static string ReadZipText(ZipArchive zip, string entryName)
    {
        var entry = zip.GetEntry(entryName)
            ?? throw new Exception($"Entry not found in archive: {entryName}");
        using var sr = new StreamReader(entry.Open());
        return sr.ReadToEnd();
    }

    private static string? TryReadZipText(ZipArchive zip, string entryName)
    {
        var entry = zip.GetEntry(entryName);
        if (entry == null) return null;
        using var sr = new StreamReader(entry.Open());
        return sr.ReadToEnd();
    }

    private static byte[] ReadZipBytes(ZipArchive zip, string entryName)
    {
        var entry = zip.GetEntry(entryName)
            ?? throw new Exception($"Entry not found: {entryName}");
        using var ms = new MemoryStream();
        entry.Open().CopyTo(ms);
        return ms.ToArray();
    }

    private static byte[]? TryReadZipBytes(ZipArchive zip, string entryName)
    {
        var entry = zip.GetEntry(entryName);
        if (entry == null) return null;
        using var ms = new MemoryStream();
        entry.Open().CopyTo(ms);
        return ms.ToArray();
    }

    // ── JSON helper ─────────────────────────────────────────────────────────

    private static T ParseJson<T>(string json)
        => JsonSerializer.Deserialize<T>(NormalizeJson(json), _json)
           ?? throw new Exception($"Failed to deserialize {typeof(T).Name}");

    /// <summary>
    /// Normalize known key variants before deserialization.
    /// "strumLines" → "strumlines" so the single property picks it up.
    /// </summary>
    private static string NormalizeJson(string json)
        => json
            .Replace("\"strumLines\"", "\"strumlines\"")
            .Replace("\"StrumLines\"", "\"strumlines\"")
            .Replace("\"Strumlines\"", "\"strumlines\"")
            .Replace("\"STRUMLINES\"", "\"strumlines\"");

    /// <summary>
    /// Detects if cameras use nested position format {position: {x, y}, scale}
    /// and marks the UsesNestedPositionFormat flag for each camera.
    /// Also manually extracts position.x and position.y for nested format cameras.
    /// This is needed to distinguish between legacy format {x, y, scale} and new format.
    /// </summary>
    private static void DetectNestedCameraFormat(FnmmScene scene, string sceneJson)
    {
        try
        {
            Console.WriteLine($"[FnmmLoader] ========================================");
            Console.WriteLine($"[FnmmLoader] Detecting camera format for {scene.Cameras.Count} cameras");
            
            // After JSON deserialization, check if cameras have NestedPosition populated
            for (int i = 0; i < scene.Cameras.Count; i++)
            {
                var cam = scene.Cameras[i];
                
                if (cam.NestedPosition != null)
                {
                    // Nested format detected - copy position from NestedPosition
                    cam.X = cam.NestedPosition.X;
                    cam.Y = cam.NestedPosition.Y;
                    cam.UsesNestedPositionFormat = true;
                    
                    Console.WriteLine($"[FnmmLoader] Camera {i}: NESTED format");
                    Console.WriteLine($"              Position: ({cam.X:F1}, {cam.Y:F1})");
                    Console.WriteLine($"              Scale: {cam.Scale:F3}");
                }
                else
                {
                    // Legacy format - X and Y are already set directly
                    cam.UsesNestedPositionFormat = false;
                    Console.WriteLine($"[FnmmLoader] Camera {i}: LEGACY format");
                    Console.WriteLine($"              Position: ({cam.X:F1}, {cam.Y:F1})");
                    Console.WriteLine($"              Scale: {cam.Scale:F3}");
                }
            }
            
            Console.WriteLine($"[FnmmLoader] ========================================");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[FnmmLoader] Warning: Could not detect camera format: {ex.Message}");
        }
    }
}
