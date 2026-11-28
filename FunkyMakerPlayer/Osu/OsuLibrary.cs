using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;

namespace FunkyMakerPlayer.Osu;

/// <summary>One playable (mania 4K) difficulty of a set.</summary>
public sealed record OsuDifficulty(string Path, string Name, float OverallDifficulty, string Creator);

/// <summary>An extracted beatmap set folder with at least one 4K mania difficulty.</summary>
public sealed record OsuSet(string Folder, string Title, string Artist, string Creator, string? BackgroundFile,
    IReadOnlyList<OsuDifficulty> Difficulties, bool HasSkin = false, bool HasVideo = false, bool HasStoryboard = false,
    IReadOnlyList<string>? VideoFiles = null)
{
    public string Name => Artist.Length > 0 ? $"{Artist} - {Title}" : Title;

    /// <summary>Stable id for online play: the folder name (import names it "&lt;set id&gt; &lt;song&gt;").</summary>
    public string Id => System.IO.Path.GetFileName(Folder).ToLowerInvariant();
}

/// <summary>
/// osu! content in this player (fork, semi-separated from the FNF side):
/// <list type="bullet">
/// <item>Beatmap sets (.osz) are extracted to <c>mods/osu/&lt;set&gt;/</c>, like osu! keeps them, so each
/// difficulty is a real .osu file the rest of the game can use by path (stars, attempts, replays).</item>
/// <item>Only osu!mania 4K difficulties are listed / playable (<see cref="OsuBeatmap.IsMania4K"/>).
/// A set without any is rejected on import.</item>
/// <item>Skins (.osk) are extracted to <c>mods/notestyles/&lt;skin&gt;/</c> and used as a note style
/// (<see cref="OsuManiaSkin"/>).</item>
/// </list>
/// </summary>
public static class OsuLibrary
{
    public static string RootFolder
    {
        get
        {
            string dir = Path.Combine(PlatformPaths.GetModsBasePath(), "osu");
            try { Directory.CreateDirectory(dir); } catch { }
            return dir;
        }
    }

    public static bool IsOsuChart(string? path) => path != null && path.EndsWith(".osu", StringComparison.OrdinalIgnoreCase);
    public static bool IsBeatmapArchive(string path) => path.EndsWith(".osz", StringComparison.OrdinalIgnoreCase);
    public static bool IsSkinArchive(string path) => path.EndsWith(".osk", StringComparison.OrdinalIgnoreCase);

    // folder → (stamp, set or null)
    private static readonly ConcurrentDictionary<string, (DateTime Stamp, OsuSet? Set)> _setCache = new();

    /// <summary>Every extracted set with at least one mania 4K difficulty (headers only; cached by folder time).</summary>
    public static List<OsuSet> ScanSets()
    {
        var sets = new List<OsuSet>();
        string root = RootFolder;
        if (!Directory.Exists(root)) return sets;
        ImportLooseArchives(root);
        foreach (var folder in Directory.GetDirectories(root))
        {
            DateTime stamp;
            try { stamp = Directory.GetLastWriteTimeUtc(folder); } catch { continue; }
            if (!_setCache.TryGetValue(folder, out var cached) || cached.Stamp != stamp)
            {
                cached = (stamp, ReadSet(folder));
                _setCache[folder] = cached;
            }
            if (cached.Set != null) sets.Add(cached.Set);
        }
        return sets;
    }

    /// <summary>
    /// .osz / .osk files copied straight into mods/osu (e.g. with a file manager on Android) are
    /// imported (extracted) and the archive removed once that worked.
    /// </summary>
    private static void ImportLooseArchives(string root)
    {
        try
        {
            foreach (var file in Directory.GetFiles(root))
            {
                if (!IsBeatmapArchive(file) && !IsSkinArchive(file)) continue;
                var r = Import(file);
                Console.WriteLine($"[Osu] Loose archive {Path.GetFileName(file)}: {r.Message}");
                if (r.Ok) { try { File.Delete(file); } catch { } }
            }
        }
        catch (Exception ex) { Console.WriteLine($"[Osu] Loose archive import failed: {ex.Message}"); }
    }

    private static OsuSet? ReadSet(string folder)
    {
        var diffs = new List<(OsuDifficulty Diff, OsuBeatmap Map)>();
        foreach (var file in Directory.GetFiles(folder, "*.osu", SearchOption.TopDirectoryOnly))
        {
            try
            {
                var map = OsuBeatmap.ParseFile(file, headerOnly: true);
                if (!map.IsMania4K) continue;
                diffs.Add((new OsuDifficulty(file, map.Version.Length > 0 ? map.Version : Path.GetFileNameWithoutExtension(file),
                    map.OverallDifficulty, map.Creator), map));
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[Osu] Unreadable {Path.GetFileName(file)}: {ex.Message}");
            }
        }
        if (diffs.Count == 0) return null;
        var first = diffs[0].Map;
        var ordered = diffs.OrderBy(d => d.Diff.OverallDifficulty).ThenBy(d => d.Diff.Name, StringComparer.OrdinalIgnoreCase)
            .Select(d => d.Diff).ToList();
        bool storyboard = Directory.EnumerateFiles(folder, "*.osb").Any() ||
                          diffs.Any(d => d.Map.EventLines.Any(l => l.StartsWith("Sprite", StringComparison.Ordinal) || l.StartsWith("Animation", StringComparison.Ordinal)));
        var videos = diffs.Select(d => d.Map.VideoFile).Where(v => !string.IsNullOrWhiteSpace(v))
            .Select(v => v!.Replace('\\', '/')).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        return new OsuSet(folder, first.DisplayTitle, first.DisplayArtist, first.Creator, first.BackgroundFile, ordered,
            HasBeatmapSkin(folder), videos.Count > 0, storyboard, videos);
    }

    // ── Online play (fork): sizes and packing a set to send it ──────────────

    /// <summary>
    /// Bytes of the whole set folder, and without its video file(s) (0 when it has no video). The
    /// packed .osz is about this size (media files don't compress), so it is what the room shows.
    /// </summary>
    public static (long Full, long NoVideo) SetSizes(OsuSet set)
    {
        long full = 0, video = 0;
        var videos = VideoPaths(set);
        try
        {
            foreach (var f in Directory.EnumerateFiles(set.Folder, "*", SearchOption.AllDirectories))
            {
                long len = new FileInfo(f).Length;
                full += len;
                if (videos.Contains(Path.GetFullPath(f))) video += len;
            }
        }
        catch (Exception ex) { Console.WriteLine($"[Osu] Size of {set.Name} failed: {ex.Message}"); }
        return (full, video > 0 ? full - video : 0);
    }

    private static HashSet<string> VideoPaths(OsuSet set)
    {
        var result = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var v in set.VideoFiles ?? Array.Empty<string>())
            if (FindFile(set.Folder, v) is { } path) result.Add(Path.GetFullPath(path));
        return result;
    }

    /// <summary>
    /// Packs the set folder into a .osz at <paramref name="destination"/> (every file, relative
    /// paths kept; <paramref name="withVideo"/> false leaves the video file(s) out). Media is stored,
    /// text is deflated. Returns the archive size.
    /// </summary>
    public static long PackSet(OsuSet set, bool withVideo, string destination)
    {
        var skip = withVideo ? new HashSet<string>() : VideoPaths(set);
        string root = Path.GetFullPath(set.Folder).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        using (var fs = new FileStream(destination, FileMode.Create, FileAccess.Write, FileShare.None))
        using (var zip = new ZipArchive(fs, ZipArchiveMode.Create))
        {
            foreach (var f in Directory.EnumerateFiles(set.Folder, "*", SearchOption.AllDirectories).OrderBy(f => f, StringComparer.Ordinal))
            {
                string full = Path.GetFullPath(f);
                if (skip.Contains(full) || !full.StartsWith(root, StringComparison.OrdinalIgnoreCase)) continue;
                string rel = full[root.Length..].Replace(Path.DirectorySeparatorChar, '/');
                string ext = Path.GetExtension(f).ToLowerInvariant();
                var level = ext is ".osu" or ".osb" or ".ini" or ".txt" ? CompressionLevel.Fastest : CompressionLevel.NoCompression;
                zip.CreateEntryFromFile(f, rel, level);
            }
        }
        return new FileInfo(destination).Length;
    }

    /// <summary>
    /// Imports a set received online and returns the path of the difficulty whose .osu has
    /// <paramref name="osuHash"/>, or null (with the reason in <paramref name="error"/>).
    /// </summary>
    public static string? ImportReceived(string oszPath, string osuHash, out string error)
    {
        var r = Import(oszPath);
        if (!r.Ok || r.Path == null) { error = r.Message; return null; }
        MarkReceived(r.Path);
        foreach (var file in Directory.GetFiles(r.Path, "*.osu", SearchOption.TopDirectoryOnly))
            if (string.Equals(Multiplayer.LevelHasher.CalculateHash(file), osuHash, StringComparison.OrdinalIgnoreCase))
            {
                error = "";
                return file;
            }
        error = "The received set doesn't contain the picked difficulty.";
        return null;
    }

    // ── Sets received online (fork) ─────────────────────────────────────────
    // Kept in a list next to the online settings (a marker file in the set would be sent along with it).

    private static readonly object _receivedLock = new();
    private static HashSet<string>? _received;

    private static string ReceivedListPath =>
        Path.Combine(Multiplayer.MultiplayerSettings.GetStorageDirectory(), "received_osu_sets.txt");

    private static HashSet<string> ReceivedSets()
    {
        if (_received != null) return _received;
        var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            if (File.Exists(ReceivedListPath))
                foreach (var line in File.ReadAllLines(ReceivedListPath))
                    if (line.Trim().Length > 0) set.Add(line.Trim());
        }
        catch (Exception ex) { Console.WriteLine($"[Osu] Could not read the received sets list: {ex.Message}"); }
        return _received = set;
    }

    /// <summary>Remembers that this set folder was downloaded from an online room (not imported by you).</summary>
    public static void MarkReceived(string setFolder)
    {
        string name = Path.GetFileName(setFolder.TrimEnd('/', '\\'));
        if (name.Length == 0) return;
        lock (_receivedLock)
        {
            if (!ReceivedSets().Add(name)) return;
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(ReceivedListPath)!);
                File.WriteAllLines(ReceivedListPath, _received!.OrderBy(x => x, StringComparer.OrdinalIgnoreCase));
            }
            catch (Exception ex) { Console.WriteLine($"[Osu] Could not save the received sets list: {ex.Message}"); }
        }
    }

    /// <summary>The set folder was downloaded from an online room.</summary>
    public static bool IsReceived(string setFolder)
    {
        string name = Path.GetFileName(setFolder.TrimEnd('/', '\\'));
        lock (_receivedLock) return name.Length > 0 && ReceivedSets().Contains(name);
    }

    /// <summary>The set a difficulty belongs to (scans if needed), or null.</summary>
    public static OsuSet? SetOf(string osuPath)
    {
        string? folder = Path.GetDirectoryName(osuPath);
        if (folder == null) return null;
        if (_setCache.TryGetValue(folder, out var c) && c.Set != null) return c.Set;
        return ScanSets().FirstOrDefault(s => string.Equals(s.Folder, folder, StringComparison.OrdinalIgnoreCase));
    }

    // ── Import ──────────────────────────────────────────────────────────────

    public sealed record ImportResult(bool Ok, string Message, string? Path, bool IsSkin);

    /// <summary>Imports a .osz (beatmap set) or .osk (skin). Never throws.</summary>
    public static ImportResult Import(string archivePath)
    {
        try
        {
            if (IsBeatmapArchive(archivePath)) return ImportBeatmapSet(archivePath);
            if (IsSkinArchive(archivePath)) return ImportSkin(archivePath);
            return new ImportResult(false, "Not an osu! file", null, false);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[Osu] Import failed for {Path.GetFileName(archivePath)}: {ex}");
            return new ImportResult(false, $"No se pudo importar {Path.GetFileName(archivePath)}: {ex.Message}", null, false);
        }
    }

    private static ImportResult ImportBeatmapSet(string oszPath)
    {
        using var zip = ZipFile.OpenRead(oszPath);
        // osu! only reads .osu files at the archive root.
        var osuEntries = zip.Entries.Where(e => !e.FullName.Contains('/') && e.Name.EndsWith(".osu", StringComparison.OrdinalIgnoreCase)).ToList();
        if (osuEntries.Count == 0)
            return new ImportResult(false, $"{Path.GetFileName(oszPath)}: no tiene ninguna dificultad (.osu)", null, false);

        OsuBeatmap? sample = null;
        int playable = 0, mania = 0;
        foreach (var e in osuEntries)
        {
            // Each difficulty on its own: a broken / other-mode one never blocks the 4K mania ones.
            OsuBeatmap map;
            try
            {
                using var sr = new StreamReader(e.Open());
                map = OsuBeatmap.Parse(sr.ReadToEnd(), headerOnly: true);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[Osu] Skipped {e.Name}: {ex.Message}");
                continue;
            }
            Console.WriteLine($"[Osu]   {e.Name}: mode {map.Mode}, {map.KeyCount}K");
            if (map.IsMania) mania++;
            if (!map.IsMania4K) continue;
            playable++;
            sample ??= map;
        }
        if (sample == null)
        {
            string why = mania == 0 ? "no es de osu!mania" : "no tiene dificultades de 4 teclas";
            return new ImportResult(false, $"{Path.GetFileNameWithoutExtension(oszPath)}: {why}", null, false);
        }

        string folderName = SafeName(sample.BeatmapSetId > 0
            ? $"{sample.BeatmapSetId} {sample.SongName}"
            : Path.GetFileNameWithoutExtension(oszPath));
        string dest = Path.Combine(RootFolder, folderName);
        ExtractAll(zip, dest);
        _setCache.TryRemove(dest, out _);
        Console.WriteLine($"[Osu] Imported set {folderName} ({playable}/{osuEntries.Count} playable difficulties)");
        return new ImportResult(true, $"{sample.SongName}: {playable} dificultad{(playable == 1 ? "" : "es")} 4K importada{(playable == 1 ? "" : "s")}",
            dest, false);
    }

    private static ImportResult ImportSkin(string oskPath)
    {
        using var zip = ZipFile.OpenRead(oskPath);
        // skin.ini at the root (osu! reads it there); some archives wrap everything in one folder.
        var ini = zip.Entries.FirstOrDefault(e => e.FullName.Equals("skin.ini", StringComparison.OrdinalIgnoreCase))
                  ?? zip.Entries.FirstOrDefault(e => e.Name.Equals("skin.ini", StringComparison.OrdinalIgnoreCase) && e.FullName.Count(c => c == '/') == 1);
        string prefix = ini != null ? ini.FullName[..^ini.Name.Length] : "";
        string name = Path.GetFileNameWithoutExtension(oskPath);
        if (ini != null)
        {
            using var sr = new StreamReader(ini.Open());
            var skinIni = OsuSkinIni.Parse(sr.ReadToEnd());
            if (!string.IsNullOrWhiteSpace(skinIni.Name)) name = skinIni.Name;
        }
        bool hasManiaArt = zip.Entries.Any(e => e.Name.StartsWith("mania-note", StringComparison.OrdinalIgnoreCase) ||
                                                e.Name.StartsWith("mania-key", StringComparison.OrdinalIgnoreCase));
        if (!hasManiaArt && (ini == null || !OsuSkinIni.HasManiaSection(zip, ini)))
            return new ImportResult(false, $"{name}: la skin no tiene elementos de osu!mania", null, true);

        string dest = Path.Combine(PlatformPaths.GetNoteStylesPath(), SafeName("osu! " + name));
        ExtractAll(zip, dest, prefix);
        Console.WriteLine($"[Osu] Imported skin {name} → {dest}");
        return new ImportResult(true, $"Skin \"{name}\" importada. Elígela en Settings → APPEARANCE → Note Style.", dest, true);
    }

    private static void ExtractAll(ZipArchive zip, string dest, string prefix = "")
    {
        Directory.CreateDirectory(dest);
        string root = Path.GetFullPath(dest) + Path.DirectorySeparatorChar;
        foreach (var e in zip.Entries)
        {
            if (string.IsNullOrEmpty(e.Name)) continue; // directory entry
            string rel = e.FullName;
            if (prefix.Length > 0)
            {
                if (!rel.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) continue;
                rel = rel[prefix.Length..];
            }
            string target = Path.GetFullPath(Path.Combine(dest, rel.Replace('/', Path.DirectorySeparatorChar)));
            if (!target.StartsWith(root, StringComparison.OrdinalIgnoreCase)) continue; // zip-slip guard
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            e.ExtractToFile(target, overwrite: true);
        }
        // Touch the folder so the set cache notices a re-import.
        try { Directory.SetLastWriteTimeUtc(dest, DateTime.UtcNow); } catch { }
    }

    private static string SafeName(string name)
    {
        var invalid = Path.GetInvalidFileNameChars();
        string s = new string(name.Select(c => invalid.Contains(c) ? '_' : c).ToArray()).Trim().TrimEnd('.');
        return s.Length > 80 ? s[..80].Trim() : (s.Length == 0 ? "osu set" : s);
    }

    /// <summary>True when a set folder ships its own mania note images (a "beatmap skin").</summary>
    public static bool HasBeatmapSkin(string folder)
    {
        try { return Directory.EnumerateFiles(folder, "mania-note*.*", SearchOption.TopDirectoryOnly).Any(); }
        catch { return false; }
    }

    /// <summary>A file of a set, matched case-insensitively (osu! maps often differ in case).</summary>
    public static string? FindFile(string folder, string? relative)
    {
        if (string.IsNullOrWhiteSpace(relative)) return null;
        string direct = Path.Combine(folder, relative.Replace('/', Path.DirectorySeparatorChar));
        if (File.Exists(direct)) return direct;
        string want = relative.Replace('\\', '/');
        try
        {
            foreach (var f in Directory.EnumerateFiles(folder, "*", SearchOption.AllDirectories))
                if (string.Equals(Path.GetRelativePath(folder, f).Replace('\\', '/'), want, StringComparison.OrdinalIgnoreCase))
                    return f;
        }
        catch { }
        return null;
    }
}
