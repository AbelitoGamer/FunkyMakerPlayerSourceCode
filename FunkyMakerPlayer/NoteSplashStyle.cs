using System;
using System.Collections.Generic;
using System.IO;
using System.Xml;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;

namespace FunkyMakerPlayer;

/// <summary>
/// Represents a notesplash animation style.
/// Supports both XML-based texture atlases (custom skins) and programmatic rendering (default fallback).
/// </summary>
public class NoteSplashStyle
{
    /// <summary>
    /// Animation frames for each lane [0-3].
    /// If null, falls back to programmatic rendering (DrawNoteSplashProgrammatic).
    /// </summary>
    public List<AtlasFrame>?[] SplashFrames { get; } = new List<AtlasFrame>?[4];

    /// <summary>Animation FPS for splash effect. Default: 24 FPS.</summary>
    public float AnimationFps { get; set; } = 24f;

    /// <summary>Total duration of splash effect in seconds. Default: 0.3s.</summary>
    public float Duration { get; set; } = 0.3f;

    /// <summary>Scale multiplier for splash rendering. Default: 1.0.</summary>
    public float Scale { get; set; } = 1.0f;

    /// <summary>Offset X position (optional, per-lane). Default: [0, 0, 0, 0].</summary>
    public int[] OffsetX { get; set; } = { 0, 0, 0, 0 };

    /// <summary>Offset Y position (optional, per-lane). Default: [0, 0, 0, 0].</summary>
    public int[] OffsetY { get; set; } = { 0, 0, 0, 0 };

    /// <summary>Whether to colorize splash by lane color. Default: false (use texture colors).</summary>
    public bool ColorizeByLane { get; set; } = false;

    /// <summary>
    /// Checks if this style has texture-based splash animations.
    /// Returns false if using programmatic fallback.
    /// </summary>
    public bool HasTextures => SplashFrames[0] != null || SplashFrames[1] != null 
                            || SplashFrames[2] != null || SplashFrames[3] != null;

    private static readonly string[] Directions = { "Left", "Down", "Up", "Right" };
    private static readonly string[] ColorNames = { "purple", "blue", "green", "red" };

    /// <summary>
    /// Loads a notesplash style from a specific folder.
    /// Searches for: noteSplashes.png/xml, splash.png/xml, or any PNG+XML with "splash" in name.
    /// Returns null if no valid files found (will use programmatic fallback).
    /// </summary>
    public static NoteSplashStyle? LoadFromFolder(string folderPath, GraphicsDevice gd)
    {
        try
        {
            var style = new NoteSplashStyle();

            // ── Find notesplash PNG+XML ───────────────────────────────────────
            string? splashPng = null, splashXml = null;
            string[] candidates = { "noteSplashes.png", "splash.png", "notesplash.png" };
            
            foreach (var c in candidates)
            {
                string p = Path.Combine(folderPath, c);
                string x = Path.ChangeExtension(p, ".xml");
                if (File.Exists(p) && File.Exists(x)) { splashPng = p; splashXml = x; break; }
            }

            // Check shared/images subfolder (VSlice layout)
            if (splashPng == null)
            {
                foreach (var c in candidates)
                {
                    string p = Path.Combine(folderPath, "shared", "images", c);
                    string x = Path.ChangeExtension(p, ".xml");
                    if (File.Exists(p) && File.Exists(x)) { splashPng = p; splashXml = x; break; }
                }
            }

            // NEW: If still not found, search for ANY PNG+XML pair in the folder
            if (splashPng == null)
            {
                try
                {
                    var pngFiles = Directory.GetFiles(folderPath, "*.png", SearchOption.TopDirectoryOnly);
                    foreach (var png in pngFiles)
                    {
                        string xml = Path.ChangeExtension(png, ".xml");
                        if (File.Exists(xml))
                        {
                            splashPng = png;
                            splashXml = xml;
                            Console.WriteLine($"[NoteSplash] Auto-detected splash files: {Path.GetFileName(png)} + {Path.GetFileName(xml)}");
                            break;
                        }
                    }
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"[NoteSplash] Error searching for PNG+XML files: {ex.Message}");
                }
            }

            // No texture files found → use programmatic fallback
            if (splashPng == null) return null;

            Texture2D splashTex;
            using (var fs = File.OpenRead(splashPng))
                splashTex = Texture2D.FromStream(gd, fs);
            var atlas = new TextureAtlas(splashTex, File.ReadAllText(splashXml!));

            // ── Load splash frames for each lane ──────────────────────────────
            // Standard naming: "noteX splash" where X = Left/Down/Up/Right
            // Alternative: "X splash" or color-based "purple splash", "blue splash", etc.
            for (int lane = 0; lane < 4; lane++)
            {
                string dir = Directions[lane];
                string color = ColorNames[lane];

                // Try multiple naming conventions
                List<AtlasFrame>? frames = null;

                // Standard FNF: "note<Direction> splash"
                frames = atlas.GetFrames("note" + dir + " splash");
                if (frames.Count == 0)
                    frames = atlas.GetFrames("note" + dir + "Splash");
                
                // Compact: "<Direction> splash"
                if (frames.Count == 0)
                    frames = atlas.GetFrames(dir + " splash");
                if (frames.Count == 0)
                    frames = atlas.GetFrames(dir + "Splash");
                
                // Color-based: "purple splash", "blue splash", etc.
                if (frames.Count == 0)
                    frames = atlas.GetFrames(color + " splash");
                if (frames.Count == 0)
                    frames = atlas.GetFrames(color + "Splash");

                // FNF Base format: "note impact 1 purple", "note impact 2 blue", etc.
                if (frames.Count == 0)
                    frames = atlas.GetFrames("note impact 1 " + color);
                if (frames.Count == 0)
                    frames = atlas.GetFrames("note impact 2 " + color);
                if (frames.Count == 0)
                    frames = atlas.GetFrames("note impact 1  " + color);  // Double space variant
                if (frames.Count == 0)
                    frames = atlas.GetFrames("note impact 2  " + color);  // Double space variant

                // Generic fallback: "splash" (shared for all lanes)
                if (frames.Count == 0)
                    frames = atlas.GetFrames("splash");

                style.SplashFrames[lane] = frames.Count > 0 ? frames : null;
            }

            // ── Optional splash.json — per-splash configuration ───────────────
            string splashJson = Path.Combine(folderPath, "splash.json");
            if (File.Exists(splashJson))
            {
                try
                {
                    var doc = System.Text.Json.JsonDocument.Parse(File.ReadAllText(splashJson));
                    var root = doc.RootElement;

                    if (root.TryGetProperty("fps", out var fps))
                        style.AnimationFps = fps.GetSingle();
                    if (root.TryGetProperty("duration", out var dur))
                        style.Duration = dur.GetSingle();
                    if (root.TryGetProperty("scale", out var sc))
                        style.Scale = sc.GetSingle();
                    if (root.TryGetProperty("colorizeByLane", out var cbl))
                        style.ColorizeByLane = cbl.GetBoolean();

                    // Offset arrays: { "offsetX": [0, 0, 0, 0], "offsetY": [0, 0, 0, 0] }
                    if (root.TryGetProperty("offsetX", out var ox) && ox.ValueKind == System.Text.Json.JsonValueKind.Array)
                    {
                        int idx = 0;
                        foreach (var v in ox.EnumerateArray())
                        {
                            if (idx >= 4) break;
                            style.OffsetX[idx++] = v.GetInt32();
                        }
                    }
                    if (root.TryGetProperty("offsetY", out var oy) && oy.ValueKind == System.Text.Json.JsonValueKind.Array)
                    {
                        int idx = 0;
                        foreach (var v in oy.EnumerateArray())
                        {
                            if (idx >= 4) break;
                            style.OffsetY[idx++] = v.GetInt32();
                        }
                    }
                }
                catch (Exception exJson)
                {
                    Console.Error.WriteLine($"[NoteSplashStyle] splash.json parse error in {folderPath}: {exJson.Message}");
                }
            }

            return style;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[NoteSplashStyle] Failed to load from {folderPath}: {ex.Message}");
            return null;
        }
    }

    /// <summary>
    /// Returns all available notesplash style names: "Default" (programmatic) + any folder in notesplashes/.
    /// A folder is valid if it has at least one PNG and one XML file.
    /// </summary>
    public static List<string> GetAvailableStyles()
    {
        var styles = new List<string> 
        { 
            "Default",
            // Particle styles (procedural, no textures)
            "Particles: CircleExpand",
            "Particles: SplashDrops",
            "Particles: StarBurst",
            "Particles: Explosion",
            "Particles: RingPulse",
            "Particles: Randomizer"  // Random style on each hit
        };
        
        string splashesDir = PlatformPaths.GetNoteSplashesPath();
        
        if (Directory.Exists(splashesDir))
        {
            foreach (var dir in Directory.GetDirectories(splashesDir))
            {
                string name = Path.GetFileName(dir);
                if (string.IsNullOrEmpty(name)) continue;

                // Accept any folder that has at least one PNG and one XML
                bool hasPng = Directory.GetFiles(dir, "*.png").Length > 0;
                bool hasXml = Directory.GetFiles(dir, "*.xml").Length > 0;
                if (hasPng && hasXml)
                    styles.Add(name);
            }
        }
        
        return styles;
    }

    /// <summary>
    /// Loads a notesplash style by name. 
    /// "Default" returns null (programmatic fallback).
    /// "Particles: *" returns null (will use ParticleNoteSplash instead).
    /// Other names load from notesplashes/<name>/ folder.
    /// </summary>
    public static NoteSplashStyle? LoadByName(string name, GraphicsDevice gd)
    {
        // Default = programmatic fallback (no textures)
        // Particles: * = return null (will be handled by ParticleNoteSplash)
        if (name == "Default" || name.StartsWith("Particles:") || string.IsNullOrEmpty(name))
            return null;

        string splashesDir = PlatformPaths.GetNoteSplashesPath();
        string styleDir = Path.Combine(splashesDir, name);
        
        if (!Directory.Exists(styleDir))
            return null;  // Fallback to programmatic

        return LoadFromFolder(styleDir, gd);
    }
}
