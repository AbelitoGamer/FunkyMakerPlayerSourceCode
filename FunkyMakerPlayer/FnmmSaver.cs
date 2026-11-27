using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace FunkyMakerPlayer;

/// <summary>
/// Saves FnmmLevel back to .FNMM (ZIP) format.
/// Preserves ALL properties from original level — compatible with FnmmLoader.
/// </summary>
public static class FnmmSaver
{
    private static readonly JsonSerializerOptions _json = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    /// <summary>
    /// Saves an FnmmLevel to a .FNMM ZIP archive.
    /// Uses flat layout (files at root) for maximum compatibility.
    /// </summary>
    public static void Save(FnmmLevel level, string outputPath)
    {
        try
        {
            // FnmmSaver always writes a ZIP container. A .rar path is a legacy
            // read-only source for us, so redirect to a sibling .fnmm file to
            // keep the extension matching the actual format.
            if (string.Equals(Path.GetExtension(outputPath), ".rar", StringComparison.OrdinalIgnoreCase))
            {
                string fnmmPath = Path.ChangeExtension(outputPath, ".fnmm");
                Console.WriteLine($"[FnmmSaver] .rar is a read-only legacy container; saving ZIP content to {fnmmPath}");
                outputPath = fnmmPath;
            }

            Console.WriteLine($"[FnmmSaver] Saving level to: {outputPath}");
            
            // Create temp file, then replace atomically
            string tempPath = outputPath + ".tmp";
            try
            {
                using (var fs = File.Create(tempPath))
                using (var zip = new ZipArchive(fs, ZipArchiveMode.Create, leaveOpen: false))
                {
                    // 1. meta.json (optional)
                    if (!string.IsNullOrEmpty(level.Meta.Name) ||
                        !string.IsNullOrEmpty(level.Meta.UUID) ||
                        !string.IsNullOrEmpty(level.Meta.Version))
                    {
                        WriteJsonEntry(zip, "meta.json", level.Meta);
                    }

                    // 2. data.json (required)
                    WriteJsonEntry(zip, "data.json", SerializeData(level.Data));

                    // 3. scene.json
                    WriteJsonEntry(zip, "scene.json", SerializeScene(level.Scene));

                    // 4. events.json
                    WriteJsonEntry(zip, "events.json", level.Events);

                    // 5. music.ogg
                    if (level.MusicData != null && level.MusicData.Length > 0)
                    {
                        WriteBinaryEntry(zip, "music.ogg", level.MusicData);
                    }

                    // 6. Loose root PNGs
                    foreach (var kvp in level.LooseImages)
                    {
                        string filename = kvp.Key + ".png";
                        WriteBinaryEntry(zip, filename, kvp.Value);
                    }

                    // 7. sprites/
                    foreach (var spriteKvp in level.Sprites)
                    {
                        string spriteName = spriteKvp.Key;
                        var (def, frames) = spriteKvp.Value;

                        // sprites/SpriteName/sprite.json
                        WriteJsonEntry(zip, $"sprites/{spriteName}/sprite.json", def);

                        // sprites/SpriteName/0.png, 1.png, 2.png, ...
                        for (int i = 0; i < frames.Count; i++)
                        {
                            if (frames[i] != null && frames[i].Length > 0)
                            {
                                WriteBinaryEntry(zip, $"sprites/{spriteName}/{i}.png", frames[i]);
                            }
                        }
                    }
                }

                // Atomic replace — never delete outputPath before Move succeeds
                File.Move(tempPath, outputPath, overwrite: true);
            }
            catch
            {
                try { if (File.Exists(tempPath)) File.Delete(tempPath); } catch { }
                throw;
            }
            
            Console.WriteLine($"[FnmmSaver] ✅ Saved successfully");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[FnmmSaver] ❌ Save failed: {ex.Message}");
            throw;
        }
    }
    
    // ── Custom serialization to preserve exact format ──────────────────────
    
    /// <summary>
    /// Serialize FnmmData, preserving "strumlines" key (not "strumLines").
    /// </summary>
    private static object SerializeData(FnmmData data)
    {
        // Create anonymous object to control exact JSON structure
        return new
        {
            bpm = data.Bpm,
            version = data.Version,
            npb = data.Npb,
            speed = data.Speed,
            strumlines = data.Strumlines, // Use lowercase "strumlines" consistently
        };
    }
    
    /// <summary>
    /// Serialize FnmmScene, handling both V2 (legacy) and V3 (nested) camera formats.
    /// </summary>
    private static object SerializeScene(FnmmScene scene)
    {
        // Serialize cameras with correct format (nested or legacy)
        var serializedCameras = new List<object>();
        foreach (var cam in scene.Cameras)
        {
            if (cam.UsesNestedPositionFormat)
            {
                // V3 format: {position: {x, y}, scale}
                serializedCameras.Add(new
                {
                    position = new { x = cam.X, y = cam.Y },
                    scale = cam.Scale
                });
            }
            else
            {
                // V2 format: {x, y, scale}
                serializedCameras.Add(new
                {
                    x = cam.X,
                    y = cam.Y,
                    scale = cam.Scale
                });
            }
        }
        
        // Serialize objects with all properties preserved
        var serializedObjects = new List<object>();
        foreach (var obj in scene.Objects)
        {
            var objData = new Dictionary<string, object?>();
            
            // Add all properties, preserving both V2 and V3 formats
            objData["spr"] = obj.Spr;
            objData["visible"] = obj.Visible;
            objData["fixed"] = obj.Fixed;
            objData["strumLine"] = obj.StrumLine;
            objData["camera"] = obj.Camera;
            
            // V2 format properties
            if (obj.Transform == null)
            {
                objData["x"] = obj.X;
                objData["y"] = obj.Y;
                if (obj.Scale != null)
                {
                    objData["scale"] = obj.Scale;
                }
            }
            
            // V3 format properties
            if (obj.Transform != null)
            {
                objData["transform"] = obj.Transform;
            }
            if (obj.Color != null)
            {
                objData["color"] = obj.Color;
            }
            if (obj.Opacity != null)
            {
                objData["opacity"] = obj.Opacity;
            }
            
            // Optional animation
            if (obj.Anim != null)
            {
                objData["anim"] = obj.Anim;
            }
            
            serializedObjects.Add(objData);
        }
        
        return new
        {
            objects = serializedObjects,
            cameras = serializedCameras,
            icons = scene.Icons,
            landscape = scene.Landscape
        };
    }
    
    // ── ZIP helper methods ──────────────────────────────────────────────────
    
    private static void WriteJsonEntry(ZipArchive zip, string entryName, object data)
    {
        var entry = zip.CreateEntry(entryName, CompressionLevel.Optimal);
        using var stream = entry.Open();
        using var writer = new StreamWriter(stream, Encoding.UTF8);
        string json = JsonSerializer.Serialize(data, _json);
        writer.Write(json);
    }
    
    private static void WriteBinaryEntry(ZipArchive zip, string entryName, byte[] data)
    {
        var entry = zip.CreateEntry(entryName, CompressionLevel.Optimal);
        using var stream = entry.Open();
        stream.Write(data, 0, data.Length);
    }
}
