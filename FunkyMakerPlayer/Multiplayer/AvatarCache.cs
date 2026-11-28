#nullable enable
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;

namespace FunkyMakerPlayer.Multiplayer;

/// <summary>
/// Discord profile pictures (protocol 5) as small round textures. <see cref="Get"/> returns null until
/// the picture is ready (callers draw <see cref="Draw"/>'s fallback: a circle in the player's colour with
/// their initial). Pictures are 64 px PNGs from cdn.discordapp.com only, downloaded one at a time,
/// kept on disk in <c>avatars/</c> (a week) and as at most <see cref="MaxTextures"/> textures
/// (unused ones are dropped first). Android 32-bit friendly: 16 KB of pixels each, no big buffers.
/// </summary>
public sealed class AvatarCache
{
    public static AvatarCache Instance { get; } = new();

    private const int MaxTextures = 64;
    private const int MaxBytes = 512 * 1024;
    private static readonly TimeSpan DiskLifetime = TimeSpan.FromDays(7);
    private static readonly TimeSpan RetryAfter = TimeSpan.FromMinutes(2);

    private sealed class Entry
    {
        public Texture2D? Texture;
        public long LastUsed;
        public bool Queued;
        public DateTime FailedAt = DateTime.MinValue;
    }

    private readonly Dictionary<string, Entry> _entries = new();
    private readonly ConcurrentQueue<(string Url, byte[] Bytes)> _decoded = new();
    private readonly ConcurrentQueue<string> _toDownload = new();
    private readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(15) };
    private int _downloading;

    private AvatarCache() { }

    private static string Folder => Path.Combine(MultiplayerSettings.GetStorageDirectory(), "avatars");

    public static bool IsAllowedUrl(string? url) =>
        !string.IsNullOrEmpty(url) && Uri.TryCreate(url, UriKind.Absolute, out var u) &&
        u.Scheme == Uri.UriSchemeHttps && u.Host == "cdn.discordapp.com";

    /// <summary>The round avatar texture, or null while it loads / when there is none. Call from the draw thread.</summary>
    public Texture2D? Get(GraphicsDevice gd, string? url)
    {
        if (!IsAllowedUrl(url)) return null;
        FinishDecoded(gd);
        if (!_entries.TryGetValue(url!, out var e))
        {
            e = new Entry();
            _entries[url!] = e;
        }
        e.LastUsed = Environment.TickCount64;
        if (e.Texture != null && !e.Texture.IsDisposed) return e.Texture;
        if (!e.Queued && DateTime.UtcNow - e.FailedAt > RetryAfter)
        {
            e.Queued = true;
            _toDownload.Enqueue(url!);
            PumpDownloads();
        }
        return null;
    }

    /// <summary>
    /// Draws the avatar in <paramref name="rect"/>; until it is ready (or with no avatar) a circle in the
    /// player's colour with the first letter of their name.
    /// </summary>
    public void Draw(SpriteBatch sb, GraphicsDevice gd, string? url, Rectangle rect, Color playerColor, string name,
        Action<string, Vector2, Color, float>? drawCenteredText = null, float alpha = 1f)
    {
        var tex = Get(gd, url);
        if (tex != null)
        {
            sb.Draw(tex, rect, Color.White * alpha);
            return;
        }
        sb.Draw(Circle(gd), rect, playerColor * (0.85f * alpha));
        if (drawCenteredText != null && !string.IsNullOrWhiteSpace(name))
            drawCenteredText(name.Trim().Substring(0, 1).ToUpperInvariant(), rect.Center.ToVector2(), Color.White * alpha, rect.Height * 0.6f);
    }

    /// <summary>Drops a picture (after logging out, so the next account's shows fresh).</summary>
    public void Forget(string? url)
    {
        if (string.IsNullOrEmpty(url) || !_entries.TryGetValue(url, out var e)) return;
        _entries.Remove(url);
        e.Texture?.Dispose();
    }

    // ── Downloads (background) ─────────────────────────────────────────────

    private void PumpDownloads()
    {
        if (Interlocked.CompareExchange(ref _downloading, 1, 0) != 0) return;
        _ = Task.Run(async () =>
        {
            try
            {
                while (_toDownload.TryDequeue(out var url))
                {
                    byte[]? bytes = await LoadAsync(url).ConfigureAwait(false);
                    _decoded.Enqueue((url, bytes ?? Array.Empty<byte>()));
                }
            }
            finally
            {
                Interlocked.Exchange(ref _downloading, 0);
                if (!_toDownload.IsEmpty) PumpDownloads();
            }
        });
    }

    private async Task<byte[]?> LoadAsync(string url)
    {
        string path = Path.Combine(Folder, HashName(url) + ".png");
        try
        {
            var info = new FileInfo(path);
            if (info.Exists && info.Length > 0 && DateTime.UtcNow - info.LastWriteTimeUtc < DiskLifetime)
                return await File.ReadAllBytesAsync(path).ConfigureAwait(false);
        }
        catch (Exception ex) { Console.WriteLine($"[Avatar] Cache read failed: {ex.Message}"); }

        try
        {
            using var res = await _http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead).ConfigureAwait(false);
            if (!res.IsSuccessStatusCode) return null;
            if (res.Content.Headers.ContentLength is > MaxBytes) return null;
            byte[] bytes = await res.Content.ReadAsByteArrayAsync().ConfigureAwait(false);
            if (bytes.Length == 0 || bytes.Length > MaxBytes) return null;
            try
            {
                Directory.CreateDirectory(Folder);
                await File.WriteAllBytesAsync(path, bytes).ConfigureAwait(false);
            }
            catch (Exception ex) { Console.WriteLine($"[Avatar] Cache write failed: {ex.Message}"); }
            return bytes;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[Avatar] Download failed: {ex.Message}");
            return null;
        }
    }

    private static string HashName(string url)
    {
        byte[] h = SHA1.HashData(Encoding.UTF8.GetBytes(url));
        return Convert.ToHexString(h, 0, 10).ToLowerInvariant();
    }

    // ── Textures (draw thread) ─────────────────────────────────────────────

    private void FinishDecoded(GraphicsDevice gd)
    {
        while (_decoded.TryDequeue(out var item))
        {
            if (!_entries.TryGetValue(item.Url, out var e)) continue;
            e.Queued = false;
            if (item.Bytes.Length == 0) { e.FailedAt = DateTime.UtcNow; continue; }
            try
            {
                using var ms = new MemoryStream(item.Bytes);
                var tex = Texture2D.FromStream(gd, ms);
                if (tex.Width > 256 || tex.Height > 256) { tex.Dispose(); e.FailedAt = DateTime.UtcNow; continue; }
                MaskRound(tex);
                e.Texture?.Dispose();
                e.Texture = tex;
                EvictIfNeeded(item.Url);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[Avatar] Bad image: {ex.Message}");
                e.FailedAt = DateTime.UtcNow;
            }
        }
    }

    /// <summary>Cuts the picture to a circle (soft 1 px edge) and premultiplies alpha.</summary>
    private static void MaskRound(Texture2D tex)
    {
        int w = tex.Width, h = tex.Height;
        var px = new Color[w * h];
        tex.GetData(px);
        float cx = (w - 1) / 2f, cy = (h - 1) / 2f, r = Math.Min(w, h) / 2f;
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
            {
                float d = MathF.Sqrt((x - cx) * (x - cx) + (y - cy) * (y - cy));
                float mask = Math.Clamp(r - d, 0f, 1f);
                var c = px[y * w + x];
                float a = c.A / 255f * mask;
                px[y * w + x] = new Color((byte)(c.R * a), (byte)(c.G * a), (byte)(c.B * a), (byte)(a * 255f));
            }
        tex.SetData(px);
    }

    /// <summary>Keeps at most MaxTextures; only drops pictures nobody drew in the last 5 seconds.</summary>
    private void EvictIfNeeded(string keep)
    {
        int count = 0;
        foreach (var e in _entries.Values) if (e.Texture != null) count++;
        if (count <= MaxTextures) return;
        long cutoff = Environment.TickCount64 - 5000;
        string? oldest = null;
        long oldestUse = long.MaxValue;
        foreach (var (url, e) in _entries)
            if (e.Texture != null && url != keep && e.LastUsed < cutoff && e.LastUsed < oldestUse)
            {
                oldest = url;
                oldestUse = e.LastUsed;
            }
        if (oldest != null) Forget(oldest);
    }

    private static Texture2D? _circle;

    /// <summary>A white anti-aliased disc (premultiplied), for the fallback avatar.</summary>
    public static Texture2D Circle(GraphicsDevice gd)
    {
        if (_circle != null && !_circle.IsDisposed) return _circle;
        const int size = 64;
        var px = new Color[size * size];
        float c = (size - 1) / 2f, r = size / 2f;
        for (int y = 0; y < size; y++)
            for (int x = 0; x < size; x++)
            {
                float a = Math.Clamp(r - MathF.Sqrt((x - c) * (x - c) + (y - c) * (y - c)), 0f, 1f);
                px[y * size + x] = new Color(a, a, a, a);
            }
        _circle = new Texture2D(gd, size, size);
        _circle.SetData(px);
        return _circle;
    }
}
