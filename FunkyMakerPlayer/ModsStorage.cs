using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;

namespace FunkyMakerPlayer;

/// <summary>
/// Where the <c>mods</c> folder lives on desktop, and moving it (fork).
/// <list type="bullet">
/// <item><see cref="Location.AppData"/>: <c>%AppData%/FunkyMakerPlayer/mods</c>
/// (<c>~/.config/FunkyMakerPlayer/mods</c> on Linux). The default: shared by every copy of the game.</item>
/// <item><see cref="Location.NextToGame"/>: <c>&lt;exe dir&gt;/mods</c> (portable, easier to edit by hand,
/// but each copy of the game has its own levels).</item>
/// </list>
/// With no saved choice yet, an existing <c>&lt;exe dir&gt;/mods</c> holding the player's own levels or mods
/// keeps being used (so updating doesn't hide them); otherwise AppData is chosen.
/// The choice is stored in <c>mods_location.txt</c> next to settings.json, so it is known before
/// anything reads a mod path. Android has a single location (see PlatformPaths), so this is unused there.
/// </summary>
public static class ModsStorage
{
    public enum Location { NextToGame, AppData }

    private const string ModsFolder = "mods";
    private const string LocationFile = "mods_location.txt";

    private static Location? _current;
    private static volatile string? _status;
    private static int _moving;
    private static int _generation;

    /// <summary>Increments after each completed move; menus compare it to know when to rescan.</summary>
    public static int Generation => Volatile.Read(ref _generation);

    /// <summary>Folder holding settings.json etc. (%AppData%/FunkyMakerPlayer).</summary>
    public static string AppDataRoot =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "FunkyMakerPlayer");

    public static string PathFor(Location location) => location == Location.AppData
        ? Path.Combine(AppDataRoot, ModsFolder)
        : Path.Combine(PlatformPaths.GetAppBaseDirectory(), ModsFolder);

    public static Location Current
    {
        get
        {
            if (_current is { } c) return c;
            Location loaded = Location.AppData;
            try
            {
                string file = Path.Combine(AppDataRoot, LocationFile);
                if (File.Exists(file))
                {
                    loaded = File.ReadAllText(file).Trim().Equals("nexttogame", StringComparison.OrdinalIgnoreCase)
                        ? Location.NextToGame
                        : Location.AppData;
                }
                else
                {
                    // First run with this setting: keep a next-to-game folder the player already uses
                    loaded = HasPlayerContent(PathFor(Location.NextToGame)) ? Location.NextToGame : Location.AppData;
                    Save(loaded);
                    Console.WriteLine($"[ModsStorage] No saved mods location; using {loaded}");
                }
            }
            catch { }
            _current = loaded;
            return loaded;
        }
    }

    public static string CurrentPath => PathFor(Current);

    /// <summary>True while a move is running (mod paths should not be used for writing meanwhile).</summary>
    public static bool IsMoving => Volatile.Read(ref _moving) == 1;

    /// <summary>Progress / result text of the last move, shown by the settings row. Null when idle.</summary>
    public static string? Status => _status;

    public static void ClearStatus() => _status = null;

    public sealed record MoveResult(int Moved, int Duplicates, int Renamed, int Failed, string Target);

    /// <summary>
    /// Moves every mod from the current folder to <paramref name="target"/> and switches to it.
    /// Per item (a level file or level folder, a note style folder, a font file...):
    /// missing at the target → moved; identical copy already there → the source copy is removed;
    /// different item with the same name → moved as "name (2)". Runs on a background thread.
    /// </summary>
    public static Task<MoveResult?> MoveToAsync(Location target)
    {
        if (target == Current || Interlocked.Exchange(ref _moving, 1) == 1)
            return Task.FromResult<MoveResult?>(null);

        return Task.Run<MoveResult?>(() =>
        {
            string from = CurrentPath;
            string to = PathFor(target);
            try
            {
                _status = "Moving...";
                var result = Move(from, to);
                if (result.Failed == 0 || result.Moved > 0 || result.Renamed > 0 || result.Duplicates > 0)
                {
                    Save(target);
                    _current = target;
                    Interlocked.Increment(ref _generation);
                }
                _status = result.Failed > 0
                    ? $"Moved, {result.Failed} item(s) could not be moved"
                    : result.Renamed > 0
                        ? $"Moved ({result.Renamed} renamed as duplicates)"
                        : "Moved";
                Console.WriteLine($"[ModsStorage] {from} -> {to}: moved {result.Moved}, duplicates {result.Duplicates}, renamed {result.Renamed}, failed {result.Failed}");
                return result;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[ModsStorage] Move failed: {ex}");
                _status = $"Move failed: {ex.Message}";
                return null;
            }
            finally
            {
                Volatile.Write(ref _moving, 0);
            }
        });
    }

    /// <summary>True when <paramref name="root"/> holds anything besides the shipped default mods.</summary>
    private static bool HasPlayerContent(string root)
    {
        if (!Directory.Exists(root)) return false;
        return CollectUnits(root).Any(u => !IsShippedDefault(Path.GetRelativePath(root, u)));
    }

    private static void Save(Location location)
    {
        Directory.CreateDirectory(AppDataRoot);
        File.WriteAllText(Path.Combine(AppDataRoot, LocationFile), location == Location.AppData ? "appdata" : "nexttogame");
    }

    // ── Moving ───────────────────────────────────────────────────────────

    private static MoveResult Move(string from, string to)
    {
        Directory.CreateDirectory(to);
        int moved = 0, dupes = 0, renamed = 0, failed = 0;
        if (!Directory.Exists(from))
            return new MoveResult(0, 0, 0, 0, to);

        // In normal (non single-file) builds the shipped defaults live in <exe>/mods itself; they
        // are the game's fallback copies (DefaultMods.Bundled), so copy those instead of moving them.
        bool fromIsBundled = SamePath(from, Path.Combine(AppContext.BaseDirectory, ModsFolder));
        var units = CollectUnits(from).ToList();
        for (int i = 0; i < units.Count; i++)
        {
            string src = units[i];
            string rel = Path.GetRelativePath(from, src);
            string dst = Path.Combine(to, rel);
            _status = $"Moving... {i * 100 / Math.Max(1, units.Count)}%";
            try
            {
                bool keepSource = fromIsBundled && IsShippedDefault(rel);
                if (!Exists(dst))
                {
                    if (keepSource) CopyItem(src, dst);
                    else MoveItem(src, dst);
                    moved++;
                }
                else if (SameContent(src, dst))
                {
                    if (!keepSource) DeleteItem(src);
                    dupes++;
                }
                else if (keepSource)
                {
                    dupes++; // the player's edited copy at the target wins; the shipped one stays
                }
                else
                {
                    MoveItem(src, UniquePath(dst));
                    renamed++;
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[ModsStorage] Could not move {rel}: {ex.Message}");
                failed++;
            }
        }

        DeleteEmptyDirectories(from);
        return new MoveResult(moved, dupes, renamed, failed, to);
    }

    /// <summary>
    /// The things that are moved as a whole. Inside <c>levels</c> a folder with meta.json/data.json is
    /// one level, other folders (Received/, user subfolders, Backups/) are walked into. Inside the
    /// other categories (notestyles, notesplashes, countdowns, fonts...) each entry is one item.
    /// Loose files in the mods root are items too.
    /// </summary>
    private static IEnumerable<string> CollectUnits(string root)
    {
        foreach (var file in Directory.GetFiles(root))
            yield return file;
        foreach (var dir in Directory.GetDirectories(root))
        {
            if (Path.GetFileName(dir).Equals("levels", StringComparison.OrdinalIgnoreCase))
            {
                foreach (var unit in CollectLevelUnits(dir)) yield return unit;
            }
            else
            {
                foreach (var entry in Directory.GetFileSystemEntries(dir)) yield return entry;
            }
        }
    }

    private static IEnumerable<string> CollectLevelUnits(string dir)
    {
        foreach (var file in Directory.GetFiles(dir))
            yield return file;
        foreach (var sub in Directory.GetDirectories(dir))
        {
            bool isLevel = File.Exists(Path.Combine(sub, "meta.json")) || File.Exists(Path.Combine(sub, "data.json"));
            if (isLevel)
                yield return sub;
            else
                foreach (var unit in CollectLevelUnits(sub)) yield return unit;
        }
    }

    /// <summary>Top-level items of the repo's DefaultMods/ folder (shipped as mods/).</summary>
    private static bool IsShippedDefault(string rel)
    {
        string r = rel.Replace('\\', '/');
        return r.Equals("README.txt", StringComparison.OrdinalIgnoreCase)
            || r.EndsWith("/README.txt", StringComparison.OrdinalIgnoreCase) && r.Count(c => c == '/') == 1
            || r.Equals($"notestyles/{DefaultMods.NoteStyleName}", StringComparison.OrdinalIgnoreCase)
            || r.Equals("notestyles/FMM (XML)", StringComparison.OrdinalIgnoreCase)
            || r.Equals($"notesplashes/{DefaultMods.SplashName}", StringComparison.OrdinalIgnoreCase)
            || r.Equals($"countdowns/{DefaultMods.CountdownName}", StringComparison.OrdinalIgnoreCase)
            || r.Equals($"fonts/{DefaultMods.FontFileName}", StringComparison.OrdinalIgnoreCase);
    }

    private static void CopyItem(string src, string dst)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(dst)!);
        if (File.Exists(src)) File.Copy(src, dst);
        else CopyDirectory(src, dst);
    }

    private static bool SamePath(string a, string b) =>
        string.Equals(Path.GetFullPath(a).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
            Path.GetFullPath(b).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
            OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);

    private static bool Exists(string path) => File.Exists(path) || Directory.Exists(path);

    private static void MoveItem(string src, string dst)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(dst)!);
        if (File.Exists(src))
        {
            // File.Move copies + deletes when the folders are on different drives.
            File.Move(src, dst);
            return;
        }
        if (SameVolume(src, dst))
        {
            try
            {
                Directory.Move(src, dst);
                return;
            }
            catch (IOException)
            {
                // Cross-device link (EXDEV on Linux mount points like /home vs /run/media,
                // or NTFS volume mount points on Windows) — fall back cleanly to copy + delete!
            }
        }
        CopyDirectory(src, dst);
        Directory.Delete(src, recursive: true);
    }

    private static void DeleteItem(string path)
    {
        if (File.Exists(path)) File.Delete(path);
        else if (Directory.Exists(path)) Directory.Delete(path, recursive: true);
    }

    private static bool SameVolume(string a, string b) =>
        string.Equals(Path.GetPathRoot(Path.GetFullPath(a)), Path.GetPathRoot(Path.GetFullPath(b)), StringComparison.OrdinalIgnoreCase);

    private static void CopyDirectory(string src, string dst)
    {
        Directory.CreateDirectory(dst);
        foreach (var file in Directory.GetFiles(src))
            File.Copy(file, Path.Combine(dst, Path.GetFileName(file)));
        foreach (var dir in Directory.GetDirectories(src))
            CopyDirectory(dir, Path.Combine(dst, Path.GetFileName(dir)));
    }

    /// <summary>Same file bytes, or folders with the same files (relative paths and bytes).</summary>
    private static bool SameContent(string a, string b)
    {
        if (File.Exists(a) && File.Exists(b))
            return SameFile(a, b);
        if (Directory.Exists(a) && Directory.Exists(b))
        {
            var fa = Directory.GetFiles(a, "*", SearchOption.AllDirectories).Select(f => Path.GetRelativePath(a, f)).OrderBy(f => f, StringComparer.OrdinalIgnoreCase).ToList();
            var fb = Directory.GetFiles(b, "*", SearchOption.AllDirectories).Select(f => Path.GetRelativePath(b, f)).OrderBy(f => f, StringComparer.OrdinalIgnoreCase).ToList();
            if (!fa.SequenceEqual(fb, StringComparer.OrdinalIgnoreCase)) return false;
            for (int i = 0; i < fa.Count; i++)
            {
                if (!SameFile(Path.Combine(a, fa[i]), Path.Combine(b, fb[i])))
                    return false;
            }
            return true;
        }
        return false;
    }

    private static bool SameFile(string a, string b)
    {
        var ia = new FileInfo(a);
        var ib = new FileInfo(b);
        if (ia.Length != ib.Length) return false;
        using var sa = File.OpenRead(a);
        using var sb = File.OpenRead(b);
        return SHA256.HashData(sa).AsSpan().SequenceEqual(SHA256.HashData(sb));
    }

    /// <summary>"name (2).ext", "name (3).ext"... for files; "name (2)" for folders.</summary>
    public static string UniquePath(string path)
    {
        string dir = Path.GetDirectoryName(path)!;
        bool isDir = Directory.Exists(path) && !File.Exists(path);
        string name = isDir ? Path.GetFileName(path) : Path.GetFileNameWithoutExtension(path);
        string ext = isDir ? "" : Path.GetExtension(path);
        for (int i = 2; ; i++)
        {
            string candidate = Path.Combine(dir, $"{name} ({i}){ext}");
            if (!Exists(candidate)) return candidate;
        }
    }

    private static void DeleteEmptyDirectories(string root)
    {
        try
        {
            foreach (var dir in Directory.GetDirectories(root))
                DeleteEmptyDirectories(dir);
            if (!Directory.EnumerateFileSystemEntries(root).Any())
                Directory.Delete(root);
        }
        catch { }
    }
}
