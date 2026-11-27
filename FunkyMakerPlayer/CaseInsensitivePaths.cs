using System;
using System.IO;
using System.Linq;

namespace FunkyMakerPlayer;

/// <summary>
/// Fork: file lookups that ignore letter case. Android's (and Linux's) file system is
/// case-sensitive, so mods copied from Windows ("Ready.PNG", "Splash.XML") weren't found by
/// exact names or "*.png" patterns there, while they worked on Windows.
/// </summary>
public static class CaseInsensitivePaths
{
    /// <summary>True when <paramref name="dir"/> has a file ending in <paramref name="extension"/> (".png"), any case.</summary>
    public static bool HasFileWithExtension(string dir, string extension)
    {
        try { return Directory.EnumerateFiles(dir).Any(f => f.EndsWith(extension, StringComparison.OrdinalIgnoreCase)); }
        catch { return false; }
    }

    /// <summary><paramref name="dir"/>/<paramref name="fileName"/> matched in any case, or the exact path when not found.</summary>
    public static string Find(string dir, string fileName)
    {
        string exact = Path.Combine(dir, fileName);
        if (File.Exists(exact)) return exact;
        try
        {
            return Directory.EnumerateFiles(dir).FirstOrDefault(f => string.Equals(Path.GetFileName(f), fileName, StringComparison.OrdinalIgnoreCase)) ?? exact;
        }
        catch { return exact; }
    }

    /// <summary>
    /// Walks <paramref name="segments"/> case-insensitively under <paramref name="baseDir"/> and returns
    /// the resolved existing file path, or <c>null</c> if any segment or the final file does not exist.
    /// </summary>
    public static string? ResolveFile(string baseDir, params string[] segments)
    {
        if (string.IsNullOrEmpty(baseDir) || segments == null || segments.Length == 0 || !Directory.Exists(baseDir))
            return null;

        string current = baseDir;
        for (int i = 0; i < segments.Length - 1; i++)
        {
            string seg = segments[i];
            string exactDir = Path.Combine(current, seg);
            if (Directory.Exists(exactDir))
            {
                current = exactDir;
                continue;
            }
            try
            {
                string? matched = Directory.EnumerateDirectories(current)
                    .FirstOrDefault(d => string.Equals(Path.GetFileName(d), seg, StringComparison.OrdinalIgnoreCase));
                if (matched == null) return null;
                current = matched;
            }
            catch { return null; }
        }

        string found = Find(current, segments[^1]);
        return File.Exists(found) ? found : null;
    }
}
