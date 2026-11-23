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
}
