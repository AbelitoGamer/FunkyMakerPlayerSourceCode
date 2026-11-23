using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Audio;
using Microsoft.Xna.Framework.Graphics;

namespace FunkyMakerPlayer.Osu;

public enum SbLayer { Background = 0, Fail = 1, Pass = 2, Foreground = 3, Overlay = 4 }

/// <summary>
/// osu! storyboard (fork): the set's .osb plus the difficulty's [Events], parsed and played like
/// osu! (ppy/osu, MIT: LegacyStoryboardDecoder, StoryboardSprite / Animation, StoryboardLoopingGroup,
/// osu-framework easings):
/// <list type="bullet">
/// <item>Elements: Sprite, Animation (frames "name0.png", "name1.png"…, LoopForever / LoopOnce),
/// Sample. Layers Background, Fail, Pass, Foreground (behind the notes) and Overlay (over them).</item>
/// <item>Commands F, M, MX, MY, S, V, R, C, P (H / V / A) with the 35 easings, chained values
/// ("F,0,1000,1500,0,1,0" = two segments) and L loops (period = last end − first start of the loop,
/// iterations = loop count). T (trigger) groups are skipped. [Variables] ($name) are substituted.</item>
/// <item>Coordinates: 640×480 (widescreen: x −107..747), scaled ×1.5 and centred in the 1280×720 frame.</item>
/// <item>Before its first command a sprite isn't drawn; alpha &gt; 1 wraps (stable's flicker rule).</item>
/// </list>
/// Pass / Fail follow the health bar (passing = health ≥ 50 %).
/// </summary>
public sealed class OsuStoryboard
{
    private enum Kind { F, MX, MY, S, VX, VY, R, CR, CG, CB, PH, PV, PA, Count }

    private sealed class Command
    {
        public Kind Kind;
        public int Easing;
        public double Start, End;
        public float From, To;
    }

    private sealed class Element
    {
        public SbLayer Layer;
        public Vector2 Origin;            // 0..1 of the texture
        public string Path = "";
        public float X, Y;
        public bool IsAnimation;
        public int FrameCount = 1;
        public double FrameDelay = 1;
        public bool LoopOnce;
        public readonly List<Command>[] Commands = Enumerable.Range(0, (int)Kind.Count).Select(_ => new List<Command>()).ToArray();
        public double Start = double.MaxValue, End = double.MinValue;
        public Texture2D?[] Frames = Array.Empty<Texture2D?>();
    }

    private sealed record Sample(double Time, SbLayer Layer, string Path, float Volume);

    private readonly List<Element> _elements = new();
    private readonly List<Sample> _samples = new();
    private readonly Dictionary<string, SoundEffect?> _sampleFx = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<Texture2D> _textures = new();
    private int _nextSample;
    private double _lastTime = double.MinValue;

    public bool Widescreen { get; private set; }
    public bool HasDrawable => _elements.Count > 0;

    // ── Parsing ─────────────────────────────────────────────────────────────

    /// <summary>Parses storyboard lines (.osb first, then the .osu [Events]); null when there's nothing to show.</summary>
    public static OsuStoryboard? Parse(IEnumerable<string> lines, IEnumerable<(string Name, string Value)> variables, bool widescreen)
    {
        var sb = new OsuStoryboard { Widescreen = widescreen };
        var vars = variables.OrderByDescending(v => v.Name.Length).ToList();
        Element? current = null;
        List<Command>? loopCommands = null;
        double loopStart = 0;
        int loopCount = 1;
        bool inTrigger = false;

        void FlushLoop()
        {
            if (current != null && loopCommands is { Count: > 0 })
            {
                double first = loopCommands.Min(c => c.Start), last = loopCommands.Max(c => c.End);
                double period = Math.Max(0, last - first);
                int iterations = Math.Clamp(loopCount, 1, 10000);
                for (int i = 0; i < iterations; i++)
                    foreach (var c in loopCommands)
                        current.Commands[(int)c.Kind].Add(new Command
                        {
                            Kind = c.Kind, Easing = c.Easing,
                            Start = loopStart + c.Start + i * period, End = loopStart + c.End + i * period,
                            From = c.From, To = c.To,
                        });
            }
            loopCommands = null;
        }

        void FinishElement()
        {
            FlushLoop();
            inTrigger = false;
            if (current == null) return;
            foreach (var list in current.Commands)
            {
                list.Sort((a, b) => a.Start.CompareTo(b.Start));
                foreach (var c in list)
                {
                    current.Start = Math.Min(current.Start, c.Start);
                    current.End = Math.Max(current.End, c.End);
                }
            }
            if (current.Start <= current.End) sb._elements.Add(current);
            current = null;
        }

        foreach (var rawLine in lines)
        {
            string line = rawLine;
            if (line.Contains('$'))
                foreach (var (name, value) in vars)
                    line = line.Replace(name, value);
            int depth = 0;
            while (depth < line.Length && (line[depth] == ' ' || line[depth] == '_')) depth++;
            string body = line[depth..];
            if (body.Length == 0) continue;
            var p = body.Split(',');

            try
            {
                if (depth == 0)
                {
                    FinishElement();
                    string type = p[0].Trim();
                    switch (type)
                    {
                        case "Sprite" or "4" when p.Length >= 6:
                            current = new Element
                            {
                                Layer = ParseLayer(p[1]), Origin = ParseOrigin(p[2]), Path = Clean(p[3]),
                                X = F(p[4]), Y = F(p[5]),
                            };
                            break;
                        case "Animation" or "6" when p.Length >= 8:
                            current = new Element
                            {
                                Layer = ParseLayer(p[1]), Origin = ParseOrigin(p[2]), Path = Clean(p[3]),
                                X = F(p[4]), Y = F(p[5]), IsAnimation = true,
                                FrameCount = Math.Max(1, (int)F(p[6])), FrameDelay = Math.Max(1, D(p[7])),
                                LoopOnce = p.Length > 8 && (p[8].Trim() == "LoopOnce" || p[8].Trim() == "1"),
                            };
                            break;
                        case "Sample" or "5" when p.Length >= 4:
                            sb._samples.Add(new Sample(D(p[1]) / 1000.0, ParseLayer(p[2]), Clean(p[3]),
                                p.Length > 4 ? Math.Clamp(F(p[4]) / 100f, 0f, 1f) : 1f));
                            break;
                    }
                    continue;
                }

                if (current == null) continue;
                string cmd = p[0].Trim();
                if (depth == 1)
                {
                    FlushLoop();
                    inTrigger = false;
                    if (cmd == "L" && p.Length >= 3)
                    {
                        loopCommands = new List<Command>();
                        loopStart = D(p[1]);
                        loopCount = (int)F(p[2]);
                        continue;
                    }
                    if (cmd == "T") { inTrigger = true; continue; } // trigger groups (hitsound / pass / fail) aren't played
                    AddCommands(current.Commands, p);
                }
                else if (loopCommands != null && !inTrigger)
                {
                    var tmp = Enumerable.Range(0, (int)Kind.Count).Select(_ => new List<Command>()).ToArray();
                    AddCommands(tmp, p);
                    foreach (var l in tmp) loopCommands.AddRange(l);
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[Storyboard] Skipped line '{body}': {ex.Message}");
            }
        }
        FinishElement();
        sb._samples.Sort((a, b) => a.Time.CompareTo(b.Time));
        Console.WriteLine($"[Storyboard] {sb._elements.Count} sprite(s), {sb._samples.Count} sample(s)");
        return sb._elements.Count > 0 || sb._samples.Count > 0 ? sb : null;
    }

    /// <summary>"type,easing,start,end,values…" (times in ms) → commands in seconds, chained values expanded.</summary>
    private static void AddCommands(List<Command>[] target, string[] p)
    {
        if (p.Length < 4) return;
        string type = p[0].Trim();
        int easing = (int)F(p[1]);
        double start = D(p[2]) / 1000.0;
        double end = string.IsNullOrWhiteSpace(p[3]) ? start : D(p[3]) / 1000.0;
        double duration = end - start;
        var values = p.Skip(4).Where(v => v.Trim().Length > 0).ToArray();

        void Chain(int width, params Kind[] kinds)
        {
            if (values.Length < width) return;
            var nums = values.Select(F).ToArray();
            int segments = Math.Max(1, nums.Length / width - 1);
            for (int s = 0; s < segments; s++)
            {
                int a = s * width, b = (s + 1) * width;
                for (int k = 0; k < kinds.Length; k++)
                {
                    float from = nums[a + k];
                    float to = b + k < nums.Length ? nums[b + k] : from;
                    if (kinds[k] == Kind.R) { } // radians as given
                    target[(int)kinds[k]].Add(new Command
                    {
                        Kind = kinds[k], Easing = easing,
                        Start = start + s * duration, End = end + s * duration, From = from, To = to,
                    });
                }
            }
        }

        switch (type)
        {
            case "F": Chain(1, Kind.F); break;
            case "S": Chain(1, Kind.S); break;
            case "R": Chain(1, Kind.R); break;
            case "MX": Chain(1, Kind.MX); break;
            case "MY": Chain(1, Kind.MY); break;
            case "M": Chain(2, Kind.MX, Kind.MY); break;
            case "V": Chain(2, Kind.VX, Kind.VY); break;
            case "C": Chain(3, Kind.CR, Kind.CG, Kind.CB); break;
            case "P" when values.Length > 0:
                var kind = values[0].Trim() switch { "H" => Kind.PH, "V" => Kind.PV, "A" => Kind.PA, _ => Kind.Count };
                if (kind != Kind.Count)
                    target[(int)kind].Add(new Command { Kind = kind, Start = start, End = end, From = 1, To = 1 });
                break;
        }
    }

    private static string Clean(string s) => s.Trim().Trim('"').Replace('\\', '/');
    private static float F(string s) => float.Parse(s.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture);
    private static double D(string s) => double.Parse(s.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture);

    private static SbLayer ParseLayer(string s) => s.Trim() switch
    {
        "Background" or "0" => SbLayer.Background,
        "Fail" or "1" => SbLayer.Fail,
        "Pass" or "2" => SbLayer.Pass,
        "Foreground" or "3" => SbLayer.Foreground,
        "Overlay" or "4" => SbLayer.Overlay,
        _ => SbLayer.Foreground,
    };

    // LegacyOrigins: TopLeft, Centre, CentreLeft, TopRight, BottomCentre, TopCentre, Custom, CentreRight, BottomLeft, BottomRight
    private static Vector2 ParseOrigin(string s) => s.Trim() switch
    {
        "TopLeft" or "0" => new(0f, 0f),
        "Centre" or "1" => new(0.5f, 0.5f),
        "CentreLeft" or "2" => new(0f, 0.5f),
        "TopRight" or "3" => new(1f, 0f),
        "BottomCentre" or "4" => new(0.5f, 1f),
        "TopCentre" or "5" => new(0.5f, 0f),
        "CentreRight" or "7" => new(1f, 0.5f),
        "BottomLeft" or "8" => new(0f, 1f),
        "BottomRight" or "9" => new(1f, 1f),
        _ => new(0f, 0f), // Custom
    };

    /// <summary>True when the Background layer shows the beatmap's background image (osu! then hides its own copy).</summary>
    public bool ReplacesBackground(string? backgroundFile)
        => backgroundFile != null && _elements.Any(e => e.Layer == SbLayer.Background &&
            string.Equals(e.Path, backgroundFile.Replace('\\', '/'), StringComparison.OrdinalIgnoreCase));

    // ── Resources ───────────────────────────────────────────────────────────

    /// <summary>Loads every image (game thread) and the samples. Missing images just aren't drawn.</summary>
    public void LoadResources(GraphicsDevice gd, string folder)
    {
        var index = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var f in Directory.EnumerateFiles(folder, "*.*", SearchOption.AllDirectories))
            index.TryAdd(System.IO.Path.GetRelativePath(folder, f).Replace('\\', '/').ToLowerInvariant(), f);

        var cache = new Dictionary<string, Texture2D?>(StringComparer.OrdinalIgnoreCase);
        Texture2D? Load(string rel)
        {
            string key = rel.ToLowerInvariant();
            if (cache.TryGetValue(key, out var t)) return t;
            Texture2D? tex = null;
            if (index.TryGetValue(key, out var file))
            {
                try
                {
                    tex = LoadExact(gd, File.ReadAllBytes(file));
                    if (tex != null) _textures.Add(tex);
                }
                catch (Exception ex) { Console.WriteLine($"[Storyboard] Image failed {rel}: {ex.Message}"); }
            }
            cache[key] = tex;
            return tex;
        }

        foreach (var e in _elements)
        {
            if (!e.IsAnimation) { e.Frames = new[] { Load(e.Path) }; continue; }
            int dot = e.Path.LastIndexOf('.');
            e.Frames = new Texture2D?[e.FrameCount];
            for (int i = 0; i < e.FrameCount; i++)
                e.Frames[i] = dot > 0 ? Load(e.Path[..dot] + i + e.Path[dot..]) ?? Load(e.Path.Replace(".", $"{i}.")) : null;
        }

        foreach (var s in _samples)
        {
            if (_sampleFx.ContainsKey(s.Path)) continue;
            string key = s.Path.ToLowerInvariant();
            _sampleFx[s.Path] = index.TryGetValue(key, out var f) ? SoundFiles.Load(f) : null;
        }
        Console.WriteLine($"[Storyboard] {_textures.Count} image(s) loaded");
    }

    /// <summary>Texture at the image's exact size (the shared loader pads widths to 4, which shifts origins).</summary>
    private static Texture2D? LoadExact(GraphicsDevice gd, byte[] bytes)
    {
        using var img = SixLabors.ImageSharp.Image.Load<SixLabors.ImageSharp.PixelFormats.Rgba32>(bytes);
        if (img.Width <= 0 || img.Height <= 0 || img.Width > 8192 || img.Height > 8192) return null;
        var pixels = new Color[img.Width * img.Height];
        img.ProcessPixelRows(acc =>
        {
            for (int y = 0; y < acc.Height; y++)
            {
                var row = acc.GetRowSpan(y);
                for (int x = 0; x < row.Length; x++)
                    pixels[y * img.Width + x] = Color.FromNonPremultiplied(row[x].R, row[x].G, row[x].B, row[x].A);
            }
        });
        var tex = new Texture2D(gd, img.Width, img.Height, false, SurfaceFormat.Color);
        tex.SetData(pixels);
        return tex;
    }

    public void Dispose()
    {
        foreach (var t in _textures) t.Dispose();
        _textures.Clear();
        foreach (var s in _sampleFx.Values) s?.Dispose();
        _sampleFx.Clear();
    }

    // ── Playback ────────────────────────────────────────────────────────────

    /// <summary>Plays storyboard samples the song passed since the last call (none after a jump back).</summary>
    public void UpdateSamples(double songTime, bool passing, float volume)
    {
        if (songTime < _lastTime - 0.05 || songTime > _lastTime + 1.0)
        {
            // Seek / restart: continue from here without replaying everything in between.
            _nextSample = _samples.FindIndex(s => s.Time >= songTime);
            if (_nextSample < 0) _nextSample = _samples.Count;
        }
        _lastTime = songTime;
        while (_nextSample < _samples.Count && _samples[_nextSample].Time <= songTime)
        {
            var s = _samples[_nextSample++];
            if ((s.Layer == SbLayer.Fail && passing) || (s.Layer == SbLayer.Pass && !passing)) continue;
            if (_sampleFx.TryGetValue(s.Path, out var fx) && fx != null)
            {
                try { fx.Play(Math.Clamp(volume * s.Volume, 0f, 1f), 0f, 0f); } catch { }
            }
        }
    }

    /// <summary>
    /// Draws the given layers at <paramref name="songTime"/> into a 1280×720 logical frame
    /// (<paramref name="transform"/> = the render target scale). Begins / ends its own batches.
    /// </summary>
    public void Draw(SpriteBatch sb, Matrix transform, double songTime, bool overlay, bool passing, float brightness)
    {
        const float scale = 1.5f;      // 480 → 720
        const float offsetX = 160f;    // x 0..640 centred in 1280 (widescreen −107..747 → 0..1280)
        BlendState? current = null;

        // A 4:3 storyboard (WidescreenStoryboard 0) only shows 640×480, like osu!: sprites placed
        // outside it on purpose stay hidden instead of showing in the 16:9 margins.
        var gd = sb.GraphicsDevice;
        RasterizerState raster = RasterizerState.CullNone;
        Rectangle oldScissor = gd.ScissorRectangle;
        if (!Widescreen)
        {
            float k = transform.M11; // logical → physical render target pixels
            gd.ScissorRectangle = new Rectangle((int)(offsetX * k), 0, (int)(640 * scale * k), (int)(480 * scale * k));
            raster = ClipRaster;
        }

        foreach (var e in _elements)
        {
            if (overlay != (e.Layer == SbLayer.Overlay)) continue;
            if (e.Layer == SbLayer.Fail && passing) continue;
            if (e.Layer == SbLayer.Pass && !passing) continue;
            if (songTime < e.Start || songTime > e.End) continue;

            float alpha = Value(e, Kind.F, songTime, 1f);
            if (alpha > 1f) alpha %= 1f;
            if (alpha <= 0.001f) continue;
            var tex = FrameAt(e, songTime);
            if (tex == null) continue;

            float x = Value(e, Kind.MX, songTime, e.X);
            float y = Value(e, Kind.MY, songTime, e.Y);
            float s = Value(e, Kind.S, songTime, 1f);
            float vx = Value(e, Kind.VX, songTime, 1f) * s, vy = Value(e, Kind.VY, songTime, 1f) * s;
            if (Math.Abs(vx) < 0.0001f || Math.Abs(vy) < 0.0001f) continue;
            float rot = Value(e, Kind.R, songTime, 0f);
            var color = new Color(
                Math.Clamp(Value(e, Kind.CR, songTime, 255f) / 255f, 0f, 1f) * brightness,
                Math.Clamp(Value(e, Kind.CG, songTime, 255f) / 255f, 0f, 1f) * brightness,
                Math.Clamp(Value(e, Kind.CB, songTime, 255f) / 255f, 0f, 1f) * brightness) * alpha;
            var effects = SpriteEffects.None;
            if (FlagAt(e, Kind.PH, songTime)) effects |= SpriteEffects.FlipHorizontally;
            if (FlagAt(e, Kind.PV, songTime)) effects |= SpriteEffects.FlipVertically;
            var blend = FlagAt(e, Kind.PA, songTime) ? PremultipliedAdditive : BlendState.AlphaBlend; // textures are premultiplied

            if (blend != current)
            {
                if (current != null) sb.End();
                sb.Begin(SpriteSortMode.Deferred, blend, SamplerState.LinearClamp, null, raster, null, transform);
                current = blend;
            }
            var origin = new Vector2(e.Origin.X * tex.Width, e.Origin.Y * tex.Height);
            // Negative scale flips around the origin, like osu!.
            if (vx < 0) { effects ^= SpriteEffects.FlipHorizontally; vx = -vx; }
            if (vy < 0) { effects ^= SpriteEffects.FlipVertically; vy = -vy; }
            sb.Draw(tex, new Vector2(offsetX + x * scale, y * scale), null, color, rot, origin,
                new Vector2(vx * scale, vy * scale), effects, 0f);
        }
        if (current != null) sb.End();
        gd.ScissorRectangle = oldScissor;
    }

    private static readonly RasterizerState ClipRaster = new() { CullMode = CullMode.None, ScissorTestEnable = true };

    private static readonly BlendState PremultipliedAdditive = new()
    {
        ColorSourceBlend = Blend.One, AlphaSourceBlend = Blend.One,
        ColorDestinationBlend = Blend.One, AlphaDestinationBlend = Blend.One,
    };

    private static Texture2D? FrameAt(Element e, double t)
    {
        if (e.Frames.Length == 0) return null;
        if (!e.IsAnimation) return e.Frames[0];
        int frame = (int)Math.Floor((t - e.Start) * 1000.0 / e.FrameDelay);
        frame = e.LoopOnce ? Math.Clamp(frame, 0, e.FrameCount - 1) : ((frame % e.FrameCount) + e.FrameCount) % e.FrameCount;
        return e.Frames[frame];
    }

    /// <summary>Value of a property: the latest started command (interpolated while running, its end value after).</summary>
    private static float Value(Element e, Kind kind, double t, float fallback)
    {
        var list = e.Commands[(int)kind];
        if (list.Count == 0) return fallback;
        int lo = 0, hi = list.Count - 1, idx = -1;
        while (lo <= hi)
        {
            int mid = (lo + hi) / 2;
            if (list[mid].Start <= t) { idx = mid; lo = mid + 1; }
            else hi = mid - 1;
        }
        if (idx < 0) return list[0].From;
        var c = list[idx];
        if (t >= c.End || c.End <= c.Start) return c.To;
        double p = (t - c.Start) / (c.End - c.Start);
        return c.From + (c.To - c.From) * (float)Ease(c.Easing, p);
    }

    /// <summary>P flags: on during the command, or from its start on when start == end.</summary>
    private static bool FlagAt(Element e, Kind kind, double t)
    {
        foreach (var c in e.Commands[(int)kind])
            if (t >= c.Start && (t <= c.End || c.End <= c.Start)) return true;
        return false;
    }

    // ── Easings (osu-framework order, as storyboards number them) ───────────

    public static double Ease(int easing, double t)
    {
        t = Math.Clamp(t, 0, 1);
        const double back = 1.70158, back2 = back * 1.525;
        const double elastic = 2 * Math.PI / 0.3, elasticShift = 0.3 / 4;
        switch (easing)
        {
            case 1: case 4: return t * (2 - t);                                     // Out / OutQuad
            case 2: case 3: return t * t;                                           // In / InQuad
            case 5: return t < 0.5 ? 2 * t * t : -1 + (4 - 2 * t) * t;              // InOutQuad
            case 6: return t * t * t;
            case 7: { double u = t - 1; return u * u * u + 1; }
            case 8: return t < 0.5 ? 4 * t * t * t : (t - 1) * (2 * t - 2) * (2 * t - 2) + 1;
            case 9: return t * t * t * t;
            case 10: { double u = t - 1; return 1 - u * u * u * u; }
            case 11: { if (t < 0.5) return 8 * t * t * t * t; double u = t - 1; return 1 - 8 * u * u * u * u; }
            case 12: return t * t * t * t * t;
            case 13: { double u = t - 1; return u * u * u * u * u + 1; }
            case 14: { if (t < 0.5) return 16 * t * t * t * t * t; double u = t - 1; return 1 + 16 * u * u * u * u * u; }
            case 15: return 1 - Math.Cos(t * Math.PI / 2);
            case 16: return Math.Sin(t * Math.PI / 2);
            case 17: return 0.5 - 0.5 * Math.Cos(Math.PI * t);
            case 18: return t == 0 ? 0 : Math.Pow(2, 10 * (t - 1));
            case 19: return t == 1 ? 1 : 1 - Math.Pow(2, -10 * t);
            case 20:
                if (t == 0 || t == 1) return t;
                return t < 0.5 ? 0.5 * Math.Pow(2, 20 * t - 10) : 1 - 0.5 * Math.Pow(2, -20 * t + 10);
            case 21: return 1 - Math.Sqrt(1 - t * t);
            case 22: { double u = t - 1; return Math.Sqrt(1 - u * u); }
            case 23:
                return t < 0.5 ? 0.5 - 0.5 * Math.Sqrt(1 - 4 * t * t) : 0.5 + 0.5 * Math.Sqrt(1 - 4 * (t - 1) * (t - 1));
            case 24: return t == 0 || t == 1 ? t : -Math.Pow(2, 10 * (t - 1)) * Math.Sin((t - 1 - elasticShift) * elastic);
            case 25: return t == 0 || t == 1 ? t : Math.Pow(2, -10 * t) * Math.Sin((t - elasticShift) * elastic) + 1;
            case 26: return t == 0 || t == 1 ? t : Math.Pow(2, -10 * t) * Math.Sin((0.5 * t - elasticShift) * elastic) + 1;
            case 27: return t == 0 || t == 1 ? t : Math.Pow(2, -10 * t) * Math.Sin((0.25 * t - elasticShift) * elastic) + 1;
            case 28:
                if (t == 0 || t == 1) return t;
                t *= 2;
                return t < 1
                    ? -0.5 * Math.Pow(2, 10 * (t - 1)) * Math.Sin((t - 1 - elasticShift * 1.5) * elastic / 1.5)
                    : 0.5 * Math.Pow(2, -10 * (t - 1)) * Math.Sin((t - 1 - elasticShift * 1.5) * elastic / 1.5) + 1;
            case 29: return t * t * ((back + 1) * t - back);
            case 30: { double u = t - 1; return u * u * ((back + 1) * u + back) + 1; }
            case 31:
                t *= 2;
                if (t < 1) return 0.5 * (t * t * ((back2 + 1) * t - back2));
                t -= 2;
                return 0.5 * (t * t * ((back2 + 1) * t + back2) + 2);
            case 32: return 1 - BounceOut(1 - t);
            case 33: return BounceOut(t);
            case 34: return t < 0.5 ? 0.5 - 0.5 * BounceOut(1 - 2 * t) : 0.5 + 0.5 * BounceOut(2 * t - 1);
            default: return t; // 0 = linear
        }
    }

    private static double BounceOut(double t)
    {
        if (t < 1 / 2.75) return 7.5625 * t * t;
        if (t < 2 / 2.75) { t -= 1.5 / 2.75; return 7.5625 * t * t + 0.75; }
        if (t < 2.5 / 2.75) { t -= 2.25 / 2.75; return 7.5625 * t * t + 0.9375; }
        t -= 2.625 / 2.75;
        return 7.5625 * t * t + 0.984375;
    }
}
