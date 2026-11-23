using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
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

    /// <summary>How long the splash shows: its longest lane's frame count / fps.
    /// (splash.json "duration" is no longer used; the animation plays at "fps".)</summary>
    public float Duration
    {
        get
        {
            float longest = 0f;
            for (int lane = 0; lane < 4; lane++) longest = Math.Max(longest, Length(lane));
            return longest > 0f ? longest : 0.3f;
        }
    }

    /// <summary>Length of one lane's animation in seconds (0 when the lane has no frames).</summary>
    public float Length(int lane) =>
        SplashFrames[lane] is { Count: > 0 } f ? f.Count / Math.Max(1f, AnimationFps) : 0f;

    /// <summary>Frame to show <paramref name="elapsed"/> seconds after the hit; null once the animation is over.</summary>
    public AtlasFrame? FrameAt(int lane, float elapsed)
    {
        if (SplashFrames[lane] is not { Count: > 0 } frames || elapsed < 0f) return null;
        int index = (int)(elapsed * AnimationFps);
        return index < frames.Count ? frames[index] : null;
    }

    /// <summary>splash.json "alpha": opacity of the splash. Default 1.</summary>
    public float Alpha { get; set; } = 1f;

    /// <summary>splash.json "fade": fade out over the animation. Default false.</summary>
    public bool Fade { get; set; } = false;

    /// <summary>splash.json "colors": tint per lane (left, down, up, right). Overrides colorizeByLane.</summary>
    public Color[]? LaneColors { get; set; }

    /// <summary>Tint for a lane: "colors" if set, else <paramref name="laneColor"/> when colorizeByLane, else white.</summary>
    public Color TintFor(int lane, Color laneColor) =>
        LaneColors != null ? LaneColors[lane & 3] : ColorizeByLane ? laneColor : Color.White;

    /// <summary>Opacity <paramref name="elapsed"/> seconds in (alpha, faded when "fade" is on).</summary>
    public float AlphaAt(int lane, float elapsed)
    {
        if (!Fade) return Alpha;
        float len = Length(lane);
        return len > 0f ? Alpha * Math.Clamp(1f - elapsed / len, 0f, 1f) : Alpha;
    }

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
            
            // Any letter case (fork: Android's file system is case-sensitive, "noteSplashes.PNG" from
            // Windows wasn't found there).
            foreach (var c in candidates)
            {
                string p = CaseInsensitivePaths.Find(folderPath, c);
                string x = CaseInsensitivePaths.Find(folderPath, Path.ChangeExtension(c, ".xml"));
                if (File.Exists(p) && File.Exists(x)) { splashPng = p; splashXml = x; break; }
            }

            // Check shared/images subfolder (VSlice layout)
            if (splashPng == null)
            {
                string sub = Path.Combine(folderPath, "shared", "images");
                foreach (var c in candidates)
                {
                    string p = CaseInsensitivePaths.Find(sub, c);
                    string x = CaseInsensitivePaths.Find(sub, Path.ChangeExtension(c, ".xml"));
                    if (File.Exists(p) && File.Exists(x)) { splashPng = p; splashXml = x; break; }
                }
            }

            // NEW: If still not found, search for ANY PNG+XML pair in the folder
            if (splashPng == null)
            {
                try
                {
                    var pngFiles = Directory.GetFiles(folderPath)
                        .Where(f => f.EndsWith(".png", StringComparison.OrdinalIgnoreCase));
                    foreach (var png in pngFiles)
                    {
                        string xml = CaseInsensitivePaths.Find(folderPath, Path.GetFileNameWithoutExtension(png) + ".xml");
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
            string splashJson = CaseInsensitivePaths.Find(folderPath, "splash.json");
            if (File.Exists(splashJson))
            {
                try
                {
                    var doc = System.Text.Json.JsonDocument.Parse(File.ReadAllText(splashJson));
                    var root = doc.RootElement;

                    if (root.TryGetProperty("fps", out var fps))
                        style.AnimationFps = Math.Max(1f, fps.GetSingle());
                    if (root.TryGetProperty("alpha", out var al) && al.ValueKind == System.Text.Json.JsonValueKind.Number)
                        style.Alpha = Math.Clamp(al.GetSingle(), 0f, 1f);
                    if (root.TryGetProperty("fade", out var fd) &&
                        (fd.ValueKind == System.Text.Json.JsonValueKind.True || fd.ValueKind == System.Text.Json.JsonValueKind.False))
                        style.Fade = fd.GetBoolean();
                    if (root.TryGetProperty("colors", out var cols) && cols.ValueKind == System.Text.Json.JsonValueKind.Array)
                    {
                        var lc = new[] { Color.White, Color.White, Color.White, Color.White };
                        int ci = 0;
                        foreach (var c in cols.EnumerateArray())
                        {
                            if (ci >= 4) break;
                            if (FmmNoteSystem.TryReadColor(c, out var parsed)) lc[ci] = parsed;
                            ci++;
                        }
                        style.LaneColors = lc;
                    }
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
    /// All splash style names: the default ("FMM (Default)"), other folders in notesplashes/
    /// (PNG + XML), then the built-in code-drawn ones ("Simple", "Particles: …").
    /// </summary>
    public static List<string> GetAvailableStyles()
    {
        var styles = new List<string> { DefaultMods.SplashName };

        string splashesDir = PlatformPaths.GetNoteSplashesPath();
        if (Directory.Exists(splashesDir))
        {
            foreach (var dir in Directory.GetDirectories(splashesDir))
            {
                string name = Path.GetFileName(dir);
                if (string.IsNullOrEmpty(name) || DefaultMods.IsDefaultSplash(name)) continue;

                // Accept any folder that has at least one PNG and one XML
                bool hasPng = CaseInsensitivePaths.HasFileWithExtension(dir, ".png");
                bool hasXml = CaseInsensitivePaths.HasFileWithExtension(dir, ".xml");
                if (hasPng && hasXml)
                    styles.Add(name);
            }
        }

        styles.Add(DefaultMods.SimpleSplashName);
        styles.AddRange(new[]
        {
            "Particles: CircleExpand",
            "Particles: SplashDrops",
            "Particles: StarBurst",
            "Particles: Explosion",
            "Particles: RingPulse",
            "Particles: Randomizer",  // Random style on each hit
        });
        return styles;
    }

    /// <summary>True for the built-in code-drawn splashes ("Simple" and "Particles: …").</summary>
    public static bool IsBuiltIn(string? name) =>
        name != null && (name.Equals(DefaultMods.SimpleSplashName, StringComparison.OrdinalIgnoreCase) ||
                         name.StartsWith("Particles:", StringComparison.Ordinal));

    /// <summary>
    /// Loads a splash style by name. The default loads mods/notesplashes/FMM (Default)/ (shipped
    /// copy as fallback). Built-in names return null: "Simple" is drawn by NoteRenderer, particles
    /// by ParticleNoteSplash. Other names load notesplashes/&lt;name&gt;/.
    /// </summary>
    public static NoteSplashStyle? LoadByName(string name, GraphicsDevice gd)
    {
        if (IsBuiltIn(name))
            return null;

        if (DefaultMods.IsDefaultSplash(name))
        {
            DefaultMods.EnsureInstalled();
            var style = Directory.Exists(DefaultMods.SplashDir) ? LoadFromFolder(DefaultMods.SplashDir, gd) : null;
            if (style != null) return style;
            string? bundled = DefaultMods.Bundled("notesplashes", DefaultMods.SplashName);
            return bundled != null && Directory.Exists(bundled) ? LoadFromFolder(bundled, gd) : null;
        }

        string styleDir = Path.Combine(PlatformPaths.GetNoteSplashesPath(), name);
        if (!Directory.Exists(styleDir))
            return null;  // Fallback to the code-drawn splash

        return LoadFromFolder(styleDir, gd);
    }
}
