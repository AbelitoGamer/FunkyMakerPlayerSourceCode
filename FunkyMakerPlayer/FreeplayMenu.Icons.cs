using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;
using SixLabors.ImageSharp.Processing;
using Color = Microsoft.Xna.Framework.Color;
using Rectangle = Microsoft.Xna.Framework.Rectangle;

namespace FunkyMakerPlayer;

/// <summary>
/// Fork: health-bar icons of each Freeplay level (scene.json "icons": [0] opponent, [1] player),
/// loaded on demand for the rows on screen. Only scene.json, the icon's sprite.json and its 0.png are
/// read; the first frame is cut out and shrunk to <see cref="IconMaxSize"/> on a worker thread, and
/// the texture is created on the game thread. Levels without icons (or RAR packages) get a generated
/// badge with the level's initials.
/// </summary>
public partial class FreeplayMenu
{
    private const int IconMaxSize = 112;
    private const int IconTextureBudget = 160;

    private sealed class IconImage
    {
        public int W, H;
        public Color[] Pixels = Array.Empty<Color>();
        public Texture2D? Texture;
        public long LastUsedFrame;
    }

    private sealed class LevelIcons
    {
        public DateTime Stamp;
        public IconImage? Opponent, Player;
        /// <summary>osu! songs: the background at 16:9, used as the row banner.</summary>
        public IconImage? Banner;
    }

    private static readonly ConcurrentDictionary<string, LevelIcons> _iconCache = new();
    private readonly ConcurrentQueue<string> _iconQueue = new();
    private readonly HashSet<string> _iconQueued = new();
    private bool _iconWorkerRunning;
    private readonly object _iconLock = new();
    private long _iconFrame;

    /// <summary>Asks for a level's icons (cheap when already loaded or queued).</summary>
    private void RequestIcons(string path)
    {
        if (_iconCache.ContainsKey(path)) return; // stale entries are dropped by Refresh (PruneIconCache)
        lock (_iconLock)
        {
            if (!_iconQueued.Add(path)) return;
            _iconQueue.Enqueue(path);
            if (_iconWorkerRunning) return;
            _iconWorkerRunning = true;
        }

        Task.Run(() =>
        {
            while (true)
            {
                if (!_iconQueue.TryDequeue(out var p))
                {
                    lock (_iconLock)
                    {
                        if (_iconQueue.IsEmpty) { _iconWorkerRunning = false; return; }
                    }
                    continue;
                }
                var icons = LoadIcons(p);
                if (_iconCache.TryGetValue(p, out var old)) DisposeIconsLater(old);
                _iconCache[p] = icons;
                lock (_iconLock) _iconQueued.Remove(p);
            }
        });
    }

    private static LevelIcons LoadIcons(string path)
    {
        var result = new LevelIcons { Stamp = LevelStamp(path) };
        if (Osu.OsuLibrary.IsOsuChart(path))
        {
            // osu! songs (fork): the beatmap background, cut to a square (icon) and 16:9 (row banner)
            byte[]? bg = Osu.OsuLevelLoader.ReadBackground(path);
            result.Opponent = CoverIcon(bg);
            result.Banner = ResizedImage(bg, BannerWidth, BannerHeight);
            return result;
        }
        FnmmLoader.WithLevelFiles<object>(path, read =>
        {
            var scene = FnmmLoader.TryParseJson<FnmmScene>(read("scene.json"));
            var names = scene?.Icons ?? new List<string>();
            if (names.Count == 0) return null;
            result.Opponent = ExtractIcon(read, names[0]);
            result.Player = names.Count > 1 ? ExtractIcon(read, names[1]) : null;
            return null;
        });
        return result;
    }

    private static IconImage? ExtractIcon(Func<string, byte[]?> read, string? sprite)
    {
        if (string.IsNullOrWhiteSpace(sprite)) return null;
        try
        {
            byte[]? png = read($"sprites/{sprite}/0.png");
            if (png == null || png.Length == 0) return null;
            var def = FnmmLoader.TryParseJson<FnmmSprite>(read($"sprites/{sprite}/sprite.json"));

            // 0.png is animation 0 as a horizontal strip of "num" frames (also for type 3 icons).
            int frames = 1;
            if (def != null && def.Data.Count > 0)
            {
                try { frames = Math.Max(1, def.Data[0].FrameCount); } catch { frames = 1; }
            }

            using var img = SixLabors.ImageSharp.Image.Load<Rgba32>(png);
            int frameW = Math.Max(1, img.Width / frames);

            // Trim transparent borders of the first frame; a blank frame (levels that hide the
            // icons use a 1x1 or empty image) means "no icon".
            int minX = int.MaxValue, minY = int.MaxValue, maxX = -1, maxY = -1, solid = 0;
            img.ProcessPixelRows(acc =>
            {
                for (int y = 0; y < acc.Height; y++)
                {
                    var row = acc.GetRowSpan(y);
                    for (int x = 0; x < frameW && x < row.Length; x++)
                    {
                        if (row[x].A < 24) continue;
                        solid++;
                        if (x < minX) minX = x;
                        if (x > maxX) maxX = x;
                        if (y < minY) minY = y;
                        if (y > maxY) maxY = y;
                    }
                }
            });
            if (maxX < 0 || solid < 16 || maxX - minX < 4 || maxY - minY < 4) return null;

            img.Mutate(x => x
                .Crop(new SixLabors.ImageSharp.Rectangle(minX, minY, maxX - minX + 1, maxY - minY + 1))
                .Resize(new ResizeOptions { Size = new SixLabors.ImageSharp.Size(IconMaxSize, IconMaxSize), Mode = ResizeMode.Max }));

            var icon = new IconImage { W = img.Width, H = img.Height, Pixels = new Color[img.Width * img.Height] };
            img.ProcessPixelRows(acc =>
            {
                for (int y = 0; y < acc.Height; y++)
                {
                    var row = acc.GetRowSpan(y);
                    for (int x = 0; x < row.Length; x++)
                        icon.Pixels[y * icon.W + x] = Color.FromNonPremultiplied(row[x].R, row[x].G, row[x].B, row[x].A);
                }
            });
            return icon;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[FreeplayMenu] Icon '{sprite}' unreadable: {ex.Message}");
            return null;
        }
    }

    /// <summary>Drops icons of levels that changed on disk or are gone (called by Refresh).</summary>
    private void PruneIconCache()
    {
        var listed = new HashSet<string>(_allPaths);
        foreach (var (path, icons) in _iconCache)
        {
            if (listed.Contains(path) && icons.Stamp == LevelStamp(path)) continue;
            if (_iconCache.TryRemove(path, out var old)) DisposeIconsLater(old);
        }
    }

    /// <summary>A centred square of an image, shrunk to the icon size (osu! backgrounds).</summary>
    private const int BannerWidth = 384, BannerHeight = 216;
    private const int DetailBackgroundWidth = 1280;

    /// <summary>An image cut (centred) to <paramref name="w"/>:<paramref name="h"/> and resized to that size.</summary>
    private static IconImage? ResizedImage(byte[]? bytes, int w, int h)
    {
        if (bytes == null || bytes.Length == 0) return null;
        try
        {
            using var img = SixLabors.ImageSharp.Image.Load<Rgba32>(bytes);
            img.Mutate(x => x.Resize(new ResizeOptions { Size = new SixLabors.ImageSharp.Size(w, h), Mode = ResizeMode.Crop }));
            return ToIconImage(img);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[FreeplayMenu] osu! background unreadable: {ex.Message}");
            return null;
        }
    }

    private static IconImage ToIconImage(SixLabors.ImageSharp.Image<Rgba32> img)
    {
        var icon = new IconImage { W = img.Width, H = img.Height, Pixels = new Color[img.Width * img.Height] };
        img.ProcessPixelRows(acc =>
        {
            for (int y = 0; y < acc.Height; y++)
            {
                var row = acc.GetRowSpan(y);
                for (int x = 0; x < row.Length; x++)
                    icon.Pixels[y * icon.W + x] = Color.FromNonPremultiplied(row[x].R, row[x].G, row[x].B, row[x].A);
            }
        });
        return icon;
    }

    private Texture2D? MakeTexture(IconImage img)
    {
        img.LastUsedFrame = _iconFrame;
        if (img.Texture != null || _graphicsDevice == null) return img.Texture;
        try
        {
            img.Texture = new Texture2D(_graphicsDevice, img.W, img.H);
            img.Texture.SetData(img.Pixels);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[FreeplayMenu] Banner texture failed: {ex.Message}");
            img.Texture = null;
        }
        return img.Texture;
    }

    /// <summary>Row banner of an osu! song (the background), or null.</summary>
    private Texture2D? GetBannerTexture(string path)
    {
        if (!_iconCache.TryGetValue(path, out var icons)) { RequestIcons(path); return null; }
        return icons.Banner != null ? MakeTexture(icons.Banner) : null;
    }

    // Full-size background of the selected osu! song (details panel); one at a time.
    private string? _detailBgPath;
    private Texture2D? _detailBg;
    private volatile IconImage? _detailBgPending;
    private string? _detailBgPendingPath;
    private string? _detailBgLoading;

    /// <summary>The selected osu! song's background at up to 1280 px wide (loaded in the background), or null.</summary>
    private Texture2D? GetDetailBackground(string path)
    {
        if (_detailBgPending is { } ready && _detailBgPendingPath == path)
        {
            _detailBgPending = null;
            _detailBg?.Dispose();
            _detailBg = null;
            _detailBgPath = path;
            var img = ready;
            if (_graphicsDevice != null)
            {
                try { _detailBg = new Texture2D(_graphicsDevice, img.W, img.H); _detailBg.SetData(img.Pixels); }
                catch { _detailBg = null; }
            }
        }
        if (_detailBgPath == path) return _detailBg;
        if (_detailBgLoading != path)
        {
            _detailBgLoading = path;
            Task.Run(() =>
            {
                IconImage? img = null;
                byte[]? bytes = Osu.OsuLevelLoader.ReadBackground(path);
                if (bytes != null)
                {
                    try
                    {
                        using var full = SixLabors.ImageSharp.Image.Load<Rgba32>(bytes);
                        if (full.Width > DetailBackgroundWidth)
                            full.Mutate(x => x.Resize(DetailBackgroundWidth, 0));
                        img = ToIconImage(full);
                    }
                    catch (Exception ex) { Console.WriteLine($"[FreeplayMenu] osu! background unreadable: {ex.Message}"); }
                }
                if (_detailBgLoading != path) return; // the selection moved on
                if (img == null) { _detailBgPath = path; return; }
                _detailBgPendingPath = path;
                _detailBgPending = img;
            });
        }
        return null;
    }

    /// <summary>Draws <paramref name="tex"/> cut (centred) to fill <paramref name="dst"/>, like osu!'s banners.</summary>
    private static void DrawCover(SpriteBatch sb, Texture2D tex, Rectangle dst, Color color)
    {
        if (dst.Width <= 0 || dst.Height <= 0) return;
        float dstAspect = dst.Width / (float)dst.Height, texAspect = tex.Width / (float)tex.Height;
        Rectangle src = dstAspect > texAspect
            ? new Rectangle(0, (int)((tex.Height - tex.Width / dstAspect) / 2), tex.Width, Math.Max(1, (int)(tex.Width / dstAspect)))
            : new Rectangle((int)((tex.Width - tex.Height * dstAspect) / 2), 0, Math.Max(1, (int)(tex.Height * dstAspect)), tex.Height);
        sb.Draw(tex, dst, src, color);
    }

    /// <summary>Darkens <paramref name="r"/> with a left-to-right black gradient (text over images).</summary>
    private void DrawShade(SpriteBatch sb, Rectangle r, float left, float right, float alpha, int steps = 24)
    {
        for (int i = 0; i < steps; i++)
        {
            int x0 = r.X + r.Width * i / steps, x1 = r.X + r.Width * (i + 1) / steps;
            float t = (i + 0.5f) / steps;
            DrawRect(sb, x0, r.Y, x1 - x0, r.Height, Color.Black * (MathHelper.Lerp(left, right, t) * alpha));
        }
    }

    private static IconImage? CoverIcon(byte[]? bytes)
    {
        if (bytes == null || bytes.Length == 0) return null;
        try
        {
            using var img = SixLabors.ImageSharp.Image.Load<Rgba32>(bytes);
            int side = Math.Min(img.Width, img.Height);
            img.Mutate(x => x
                .Crop(new SixLabors.ImageSharp.Rectangle((img.Width - side) / 2, (img.Height - side) / 2, side, side))
                .Resize(IconMaxSize, IconMaxSize));
            var icon = new IconImage { W = img.Width, H = img.Height, Pixels = new Color[img.Width * img.Height] };
            img.ProcessPixelRows(acc =>
            {
                for (int y = 0; y < acc.Height; y++)
                {
                    var row = acc.GetRowSpan(y);
                    for (int x = 0; x < row.Length; x++)
                        icon.Pixels[y * icon.W + x] = Color.FromNonPremultiplied(row[x].R, row[x].G, row[x].B, row[x].A);
                }
            });
            return icon;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[FreeplayMenu] osu! background unreadable: {ex.Message}");
            return null;
        }
    }

    private readonly ConcurrentQueue<Texture2D> _iconTexturesToDispose = new();

    private void DisposeIconsLater(LevelIcons icons)
    {
        foreach (var i in new[] { icons.Opponent, icons.Player, icons.Banner })
            if (i?.Texture is { } t) _iconTexturesToDispose.Enqueue(t);
    }

    /// <summary>
    /// The level's icon (game thread; the texture is created on first use). Like FNF's freeplay, a song
    /// is shown by its opponent's icon (the player is usually the same character everywhere); the
    /// player icon is the fallback.
    /// </summary>
    private Texture2D? GetIconTexture(string path)
    {
        if (!_iconCache.TryGetValue(path, out var icons)) { RequestIcons(path); return null; }
        var img = icons.Opponent ?? icons.Player;
        if (img == null || _graphicsDevice == null) return null;
        img.LastUsedFrame = _iconFrame;
        if (img.Texture == null)
        {
            try
            {
                img.Texture = new Texture2D(_graphicsDevice, img.W, img.H);
                img.Texture.SetData(img.Pixels);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[FreeplayMenu] Icon texture failed: {ex.Message}");
                img.Texture = null;
            }
        }
        return img.Texture;
    }

    /// <summary>Once per frame (game thread): frees replaced textures and the least recently drawn ones.</summary>
    private void TrimIconTextures()
    {
        _iconFrame++;
        while (_iconTexturesToDispose.TryDequeue(out var t)) t.Dispose();
        if (_iconFrame % 120 != 0) return;

        var live = _iconCache.Values
            .SelectMany(i => new[] { i.Opponent, i.Player, i.Banner })
            .Where(i => i?.Texture != null).Distinct().ToList();
        if (live.Count <= IconTextureBudget) return;
        foreach (var img in live.OrderBy(i => i!.LastUsedFrame).Take(live.Count - IconTextureBudget))
        {
            img!.Texture!.Dispose();
            img.Texture = null;
        }
    }

    /// <summary>
    /// Draws a level's icon fitted in <paramref name="box"/>, or a generated initials badge.
    /// <paramref name="scale"/> grows it around the centre (beat bump).
    /// </summary>
    private void DrawLevelIcon(SpriteBatch sb, string path, string name, Rectangle box, float alpha, float scale = 1f)
    {
        if (scale != 1f)
        {
            int nw = (int)(box.Width * scale), nh = (int)(box.Height * scale);
            box = new Rectangle(box.Center.X - nw / 2, box.Center.Y - nh / 2, nw, nh);
        }

        // Online songs not on this device yet (fork): a "?" badge until they're downloaded.
        if (!OnDevice(path))
        {
            DrawUnknownIcon(sb, box, alpha);
            return;
        }

        var tex = GetIconTexture(path);
        if (tex != null)
        {
            float fit = Math.Min(box.Width / (float)tex.Width, box.Height / (float)tex.Height);
            int w = (int)(tex.Width * fit), h = (int)(tex.Height * fit);
            var dst = new Rectangle(box.Center.X - w / 2, box.Center.Y - h / 2, w, h);
            sb.Draw(tex, dst, Color.White * alpha);
            return;
        }

        // Generated badge: colour from the name, up to two initials.
        Color baseCol = NameColor(name);
        DrawRect(sb, box.X, box.Y, box.Width, box.Height, Color.Lerp(baseCol, Color.Black, 0.55f) * alpha);
        int inset = Math.Max(2, box.Width / 14);
        DrawRect(sb, box.X + inset, box.Y + inset, box.Width - inset * 2, box.Height - inset * 2, baseCol * (0.85f * alpha));
        DrawRect(sb, box.X + inset, box.Y + inset, box.Width - inset * 2, Math.Max(1, inset / 2), Color.White * (0.35f * alpha));
        string initials = Initials(name);
        int fs = FitFontSize(initials, Math.Max(8, (int)(box.Height * 0.5f)), 8, box.Width - inset * 3);
        var sz = MeasureText(initials, fs);
        DrawText(sb, initials, new Vector2(box.Center.X - sz.X / 2f + 1, box.Center.Y - sz.Y / 2f + 1), Color.Black * (0.45f * alpha), fs);
        DrawText(sb, initials, new Vector2(box.Center.X - sz.X / 2f, box.Center.Y - sz.Y / 2f), Color.White * alpha, fs);
    }

    /// <summary>Grey "?" badge: a song this device doesn't have yet.</summary>
    private void DrawUnknownIcon(SpriteBatch sb, Rectangle box, float alpha)
    {
        DrawRect(sb, box.X, box.Y, box.Width, box.Height, new Color(22, 22, 34) * alpha);
        int inset = Math.Max(2, box.Width / 14);
        DrawRect(sb, box.X + inset, box.Y + inset, box.Width - inset * 2, box.Height - inset * 2, new Color(58, 58, 78) * (0.9f * alpha));
        DrawRect(sb, box.X + inset, box.Y + inset, box.Width - inset * 2, Math.Max(1, inset / 2), Color.White * (0.18f * alpha));
        int fs = Math.Max(8, (int)(box.Height * 0.62f));
        var sz = MeasureText("?", fs);
        DrawText(sb, "?", new Vector2(box.Center.X - sz.X / 2f + 1, box.Center.Y - sz.Y / 2f + 1), Color.Black * (0.45f * alpha), fs);
        DrawText(sb, "?", new Vector2(box.Center.X - sz.X / 2f, box.Center.Y - sz.Y / 2f), new Color(200, 200, 225) * alpha, fs);
    }

    /// <summary>
    /// Waiting room (fork): a song's art in <paramref name="box"/>: osu! songs their background
    /// (cut to fill), FNF levels their icon or initials, "?" when <paramref name="path"/> is null
    /// (not on this device yet). Shares the browser's icon cache.
    /// </summary>
    public void DrawSongArt(SpriteBatch sb, string? path, string name, Rectangle box, float alpha)
    {
        _graphicsDevice ??= sb.GraphicsDevice; // the browser may not have been drawn yet
        if (path == null || !File.Exists(path)) { DrawUnknownIcon(sb, box, alpha); return; }
        if (Osu.OsuLibrary.IsOsuChart(path))
        {
            var banner = GetBannerTexture(path);
            if (banner != null) { DrawCover(sb, banner, box, Color.White * alpha); return; }
        }
        DrawLevelIcon(sb, path, name, box, alpha);
    }

    /// <summary>The osu! song's background is loaded (the waiting room uses it as the slot backdrop).</summary>
    public Texture2D? SongBanner(SpriteBatch sb, string? path)
    {
        _graphicsDevice ??= sb.GraphicsDevice;
        return path != null && Osu.OsuLibrary.IsOsuChart(path) && File.Exists(path) ? GetBannerTexture(path) : null;
    }

    /// <summary>Draws <paramref name="tex"/> cut to fill <paramref name="dst"/> (public for the waiting room).</summary>
    public static void DrawCoverPublic(SpriteBatch sb, Texture2D tex, Rectangle dst, Color color) => DrawCover(sb, tex, dst, color);

    private static string Initials(string name)
    {
        var words = name.Split(new[] { ' ', '-', '_', '.', '(', ')', '[', ']' }, StringSplitOptions.RemoveEmptyEntries)
            .Where(w => char.IsLetterOrDigit(w[0])).ToList();
        if (words.Count == 0) return "?";
        string s = words.Count == 1
            ? new string(words[0].Where(char.IsLetterOrDigit).Take(2).ToArray())
            : $"{words[0][0]}{words[1][0]}";
        s = s.ToUpperInvariant();
        // The bitmap fallback font only has ASCII.
        return new string(s.Select(c => c < 127 ? c : '?').ToArray());
    }

    private static Color NameColor(string name)
    {
        uint h = 2166136261;
        foreach (char c in name.ToLowerInvariant()) h = (h ^ c) * 16777619;
        return HsvToColor(h % 360, 0.55f, 0.85f);
    }

    private static Color HsvToColor(float hue, float sat, float val)
    {
        float c = val * sat, x = c * (1 - Math.Abs(hue / 60f % 2 - 1)), m = val - c;
        (float r, float g, float b) = (hue % 360) switch
        {
            < 60 => (c, x, 0f),
            < 120 => (x, c, 0f),
            < 180 => (0f, c, x),
            < 240 => (0f, x, c),
            < 300 => (x, 0f, c),
            _ => (c, 0f, x),
        };
        return new Color(r + m, g + m, b + m);
    }
}
