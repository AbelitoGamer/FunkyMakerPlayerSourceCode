using System;
using System.IO;
using System.Linq;

namespace FunkyMakerPlayer;

/// <summary>
/// Default content for the player-moddable folders (note style, countdown, font, plus the
/// README/template files). The repo keeps it in <c>DefaultMods/</c>, which the build ships
/// as <c>mods/</c>. At startup any default file missing from the real mods folder is copied
/// there, so players can see the defaults and use them as examples. Existing files are never
/// overwritten, so player edits survive; a deleted default comes back on the next start.
///
/// The shipped copy stays untouched and is the fallback when a player breaks a default:
/// - Desktop: <c>&lt;AppContext.BaseDirectory&gt;/mods</c>. For a single-file publish that is
///   the self-extract folder; for a normal build it is the mods folder itself.
/// - Android: extracted from the APK to <c>FilesDir/DefaultMods</c> (refreshed per version).
/// </summary>
public static class DefaultMods
{
    /// <summary>Folder name (and setting value) of the default note style: mods/notestyles/FMM (Default).</summary>
    public const string NoteStyleName = "FMM (Default)";
    /// <summary>Setting value used before the FMM default skin (still accepted, maps to the default).</summary>
    public const string LegacyNoteStyleName = "Default";
    /// <summary>Folder name (and setting value) of the default splash: mods/notesplashes/FMM (Default).</summary>
    public const string SplashName = "FMM (Default)";
    /// <summary>Old setting value. It meant the code-drawn splash; it now maps to the default splash.</summary>
    public const string LegacySplashName = "Default";
    /// <summary>The built-in code-drawn splash (previously listed as "Default").</summary>
    public const string SimpleSplashName = "Simple";

    public static string SplashDir => Path.Combine(PlatformPaths.GetNoteSplashesPath(), SplashName);

    /// <summary>Splash setting value / folder name that means "the default splash".</summary>
    public static bool IsDefaultSplash(string? name) =>
        string.IsNullOrEmpty(name) ||
        name.Equals(SplashName, StringComparison.OrdinalIgnoreCase) ||
        name.Equals(LegacySplashName, StringComparison.OrdinalIgnoreCase);

    /// <summary>Folder name of the default countdown (mods/countdowns/Default).</summary>
    public const string CountdownName = "Default";
    /// <summary>File name of the default UI font (mods/fonts/MilkyNice.otf).</summary>
    public const string FontFileName = "MilkyNice.otf";

    private const string VersionMarker = ".version";

    private static readonly object _lock = new();
    private static bool _installed;
    private static string? _bundledRoot;

    public static string NoteStyleDir => Path.Combine(PlatformPaths.GetNoteStylesPath(), NoteStyleName);
    public static string CountdownDir => Path.Combine(PlatformPaths.GetCountdownsPath(), CountdownName);
    public static string FontPath => Path.Combine(PlatformPaths.GetFontsPath(), FontFileName);

    /// <summary>Note style setting value / folder name that means "the default style"
    /// (the current name, or the legacy "Default").</summary>
    public static bool IsDefaultNoteStyle(string? name) =>
        string.IsNullOrEmpty(name) ||
        name.Equals(NoteStyleName, StringComparison.OrdinalIgnoreCase) ||
        name.Equals(LegacyNoteStyleName, StringComparison.OrdinalIgnoreCase);

    /// <summary>Countdown setting value / folder name that means "the default countdown"
    /// (the setting value is the legacy lowercase "default").</summary>
    public static bool IsDefaultCountdown(string? name) =>
        string.IsNullOrEmpty(name) || name.Equals(CountdownName, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Path inside the shipped (untouched) copy of the defaults, e.g.
    /// <c>Bundled("countdowns", "Default", "go.png")</c>. Null when no shipped copy exists or
    /// when it is the same folder as the player's mods folder (no separate fallback).
    /// </summary>
    public static string? Bundled(params string[] parts)
    {
        EnsureInstalled();
        if (_bundledRoot == null) return null;
        if (SamePath(_bundledRoot, PlatformPaths.GetModsBasePath())) return null;
        return Path.Combine(new[] { _bundledRoot }.Concat(parts).ToArray());
    }

    /// <summary>
    /// Copies every shipped default file that is missing from the player's mods folder.
    /// Safe to call many times; only the first call does work.
    /// </summary>
    public static void EnsureInstalled()
    {
        lock (_lock)
        {
            if (_installed) return;
            _installed = true;

            try
            {
                _bundledRoot = LocateBundledRoot();
                if (_bundledRoot == null)
                {
                    Console.WriteLine("[DefaultMods] No shipped defaults found; skipping install");
                    return;
                }

                string target = PlatformPaths.GetModsBasePath();
                if (SamePath(_bundledRoot, target))
                {
                    Console.WriteLine($"[DefaultMods] Defaults already live in {target}");
                    return;
                }

                int copied = CopyMissing(_bundledRoot, target);
                Console.WriteLine($"[DefaultMods] Installed {copied} missing default file(s) into {target}");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[DefaultMods] Install failed: {ex.Message}");
            }
        }
    }

    private static string? LocateBundledRoot()
    {
#if ANDROID
        return ExtractFromApk();
#else
        string[] candidates =
        {
            Path.Combine(AppContext.BaseDirectory, "Assets", "DefaultMods"),
            Path.Combine(PlatformPaths.GetAppBaseDirectory(), "Assets", "DefaultMods"),
            Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "DefaultMods"),
            Path.Combine(AppContext.BaseDirectory, "mods"),
        };
        foreach (string root in candidates)
        {
            if (Directory.Exists(Path.Combine(root, "notestyles", NoteStyleName)))
                return Path.GetFullPath(root);
        }
        return null;
#endif
    }

    private static int CopyMissing(string sourceDir, string targetDir)
    {
        int copied = 0;
        Directory.CreateDirectory(targetDir);

        foreach (string file in Directory.GetFiles(sourceDir))
        {
            string name = Path.GetFileName(file);
            if (name == VersionMarker) continue;

            string dest = Path.Combine(targetDir, name);
            if (File.Exists(dest)) continue;
            try
            {
                File.Copy(file, dest);
                copied++;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[DefaultMods] Could not copy {name}: {ex.Message}");
            }
        }

        foreach (string dir in Directory.GetDirectories(sourceDir))
            copied += CopyMissing(dir, Path.Combine(targetDir, Path.GetFileName(dir)));

        return copied;
    }

    private static bool SamePath(string a, string b)
    {
        try
        {
            string na = Path.GetFullPath(a).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            string nb = Path.GetFullPath(b).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            return string.Equals(na, nb, OperatingSystem.IsWindows()
                ? StringComparison.OrdinalIgnoreCase
                : StringComparison.Ordinal);
        }
        catch
        {
            return false;
        }
    }

#if ANDROID
    /// <summary>
    /// Extracts the APK's <c>mods/</c> assets to <c>FilesDir/DefaultMods</c>, once per app
    /// version, and returns that folder.
    /// </summary>
    private static string? ExtractFromApk()
    {
        var context = global::Android.App.Application.Context;
        string? filesDir = context.FilesDir?.AbsolutePath;
        if (filesDir == null) return null;

        string cache = Path.Combine(filesDir, "DefaultMods");
        string marker = Path.Combine(cache, VersionMarker);
        string version = typeof(DefaultMods).Assembly.GetName().Version?.ToString() ?? "0";

        try
        {
            if (File.Exists(marker) && File.ReadAllText(marker).Trim() == version)
                return cache;

            if (Directory.Exists(cache))
                Directory.Delete(cache, recursive: true);

            int count = ExtractAssetDir(context.Assets!, "mods", cache);
            File.WriteAllText(marker, version);
            Console.WriteLine($"[DefaultMods] Extracted {count} default file(s) from APK to {cache}");
            return cache;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[DefaultMods] APK extraction failed: {ex.Message}");
            return Directory.Exists(cache) ? cache : null;
        }
    }

    private static int ExtractAssetDir(global::Android.Content.Res.AssetManager assets, string assetPath, string targetDir)
    {
        int count = 0;
        string[] items = assets.List(assetPath) ?? Array.Empty<string>();
        if (items.Length == 0) return 0;

        Directory.CreateDirectory(targetDir);
        foreach (string item in items)
        {
            string childAsset = $"{assetPath}/{item}";
            string childTarget = Path.Combine(targetDir, item);

            // AssetManager.List returns children for a folder and nothing for a file.
            string[] sub = assets.List(childAsset) ?? Array.Empty<string>();
            if (sub.Length > 0)
            {
                count += ExtractAssetDir(assets, childAsset, childTarget);
                continue;
            }

            using var input = assets.Open(childAsset);
            using var output = File.Create(childTarget);
            input.CopyTo(output);
            count++;
        }
        return count;
    }
#endif
}
