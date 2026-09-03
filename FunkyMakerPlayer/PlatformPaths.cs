using System;
using System.IO;

namespace FunkyMakerPlayer;

/// <summary>
/// Platform-specific path handling for FunkyMaker Player.
/// Handles differences between Windows/Linux (local folders) and Android (external storage).
/// </summary>
public static class PlatformPaths
{
    private const string APP_FOLDER = "FunkyMakerPlayer";
    private const string MODS_FOLDER = "mods";
    
    /// <summary>
    /// Get the base mods directory
    /// - Windows/Linux: ./mods/ (next to exe)
    /// - Android: /storage/emulated/0/Documents/FunkyMakerPlayerPublicMods/mods/ (user-accessible via file manager)
    /// </summary>
    public static string GetModsBasePath()
    {
#if ANDROID
        // Use Documents/FunkyMakerPlayerPublicMods/mods/ for easy user access via file managers
        // This is the SAME path shown in "OPEN MODS FOLDER" button
        var documentsDir = Android.OS.Environment.GetExternalStoragePublicDirectory(Android.OS.Environment.DirectoryDocuments);
        
        if (documentsDir == null)
        {
            Console.WriteLine("[PlatformPaths] WARNING: Documents directory is null, using fallback");
            // Fallback to external files dir (app-private but better than nothing)
            var externalStorage = Android.App.Application.Context.GetExternalFilesDir(null)?.AbsolutePath ?? "";
            var fallbackModsPath = Path.Combine(externalStorage, APP_FOLDER, MODS_FOLDER);
            
            if (!Directory.Exists(fallbackModsPath))
            {
                Directory.CreateDirectory(fallbackModsPath);
                Console.WriteLine($"[PlatformPaths] Created fallback mods directory: {fallbackModsPath}");
            }
            
            return fallbackModsPath;
        }
        
        // Create FunkyMakerPlayerPublicMods/mods/ in Documents
        var funkyMakerPublicDir = Path.Combine(documentsDir.AbsolutePath, "FunkyMakerPlayerPublicMods");
        var modsPath = Path.Combine(funkyMakerPublicDir, MODS_FOLDER);
        
        if (!Directory.Exists(modsPath))
        {
            Directory.CreateDirectory(modsPath);
            Console.WriteLine($"[PlatformPaths] Created public mods directory: {modsPath}");
        }
        
        return modsPath;
#else
        return Path.Combine(AppDomain.CurrentDomain.BaseDirectory, MODS_FOLDER);
#endif
    }
    
    /// <summary>
    /// Get the levels directory path (custom user levels)
    /// </summary>
    public static string GetLevelsPath()
    {
        var levelsPath = Path.Combine(GetModsBasePath(), "levels");
        
        if (!Directory.Exists(levelsPath))
        {
            Directory.CreateDirectory(levelsPath);
        }
        
        return levelsPath;
    }
    
    /// <summary>
    /// Get the notestyles directory path (custom note skins)
    /// </summary>
    public static string GetNoteStylesPath()
    {
        var noteStylesPath = Path.Combine(GetModsBasePath(), "notestyles");
        
        if (!Directory.Exists(noteStylesPath))
        {
            Directory.CreateDirectory(noteStylesPath);
        }
        
        return noteStylesPath;
    }
    
    /// <summary>
    /// Get the notesplashes directory path (custom splash effects)
    /// </summary>
    public static string GetNoteSplashesPath()
    {
        var noteSplashesPath = Path.Combine(GetModsBasePath(), "notesplashes");
        
        if (!Directory.Exists(noteSplashesPath))
        {
            Directory.CreateDirectory(noteSplashesPath);
        }
        
        return noteSplashesPath;
    }
    
    /// <summary>
    /// Get the fonts directory path (custom fonts)
    /// </summary>
    public static string GetFontsPath()
    {
        var fontsPath = Path.Combine(GetModsBasePath(), "fonts");
        
        if (!Directory.Exists(fontsPath))
        {
            Directory.CreateDirectory(fontsPath);
        }
        
        return fontsPath;
    }
    
    /// <summary>
    /// Get the countdowns directory path (custom countdown assets)
    /// </summary>
    public static string GetCountdownsPath()
    {
        var countdownsPath = Path.Combine(GetModsBasePath(), "countdowns");
        
        if (!Directory.Exists(countdownsPath))
        {
            Directory.CreateDirectory(countdownsPath);
        }
        
        return countdownsPath;
    }
    
    /// <summary>
    /// Get the Assets directory path (readonly, bundled with app)
    /// </summary>
    public static string GetAssetsPath()
    {
#if ANDROID
        // On Android, Assets are bundled inside the APK
        return "Assets"; // Will be accessed via ContentManager/AssetManager
#else
        return Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Assets");
#endif
    }
    
    /// <summary>
    /// Check if we need to request storage permissions (Android 6.0+)
    /// </summary>
    public static bool NeedsStoragePermission()
    {
#if ANDROID
        return Android.OS.Build.VERSION.SdkInt >= Android.OS.BuildVersionCodes.M;
#else
        return false;
#endif
    }
    
    /// <summary>
    /// Initialize all mod directories and scan for content.
    /// Call this once at app startup to ensure folders exist and content is detected.
    /// Returns a summary message for display.
    /// </summary>
    public static string InitializeModDirectories()
    {
        Console.WriteLine("[PlatformPaths] Initializing mod directories...");
        
        try
        {
            // Create all directories (these methods auto-create if missing)
            string modsBase = GetModsBasePath();
            string levels = GetLevelsPath();
            string noteStyles = GetNoteStylesPath();
            string noteSplashes = GetNoteSplashesPath();
            string fonts = GetFontsPath();
            string countdowns = GetCountdownsPath();
            
            Console.WriteLine($"[PlatformPaths] ✓ Mods base: {modsBase}");
            Console.WriteLine($"[PlatformPaths] ✓ Levels: {levels}");
            Console.WriteLine($"[PlatformPaths] ✓ NoteStyles: {noteStyles}");
            Console.WriteLine($"[PlatformPaths] ✓ NoteSplashes: {noteSplashes}");
            Console.WriteLine($"[PlatformPaths] ✓ Fonts: {fonts}");
            Console.WriteLine($"[PlatformPaths] ✓ Countdowns: {countdowns}");
            
            // Scan for content and count items
            int totalLevels = ScanModContent(levels, "levels", new[] { ".fnmm", ".rar" });
            int totalNoteStyles = ScanModContent(noteStyles, "notestyles", null);
            int totalNoteSplashes = ScanModContent(noteSplashes, "notesplashes", null);
            int totalFonts = ScanModContent(fonts, "fonts", new[] { ".otf", ".ttf" });
            int totalCountdowns = ScanModContent(countdowns, "countdowns", null);
            
            int totalItems = totalLevels + totalNoteStyles + totalNoteSplashes + totalFonts + totalCountdowns;
            
            Console.WriteLine("[PlatformPaths] Mod directories initialized successfully!");
            
            // Build summary message
            if (totalItems > 0)
            {
                string summary = $"📁 Carpetas escaneadas!\n\n";
                if (totalLevels > 0) summary += $"🎵 {totalLevels} niveles\n";
                if (totalNoteStyles > 0) summary += $"🎹 {totalNoteStyles} note styles\n";
                if (totalNoteSplashes > 0) summary += $"✨ {totalNoteSplashes} note splashes\n";
                if (totalFonts > 0) summary += $"🔤 {totalFonts} fuentes\n";
                if (totalCountdowns > 0) summary += $"⏱️ {totalCountdowns} countdowns\n";
                return summary.TrimEnd();
            }
            else
            {
                return "📁 Carpetas de mods creadas!\n\nColoca tus archivos en:\nDocuments → FunkyMakerPlayerPublicMods → mods";
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[PlatformPaths] ERROR initializing mod directories: {ex.Message}");
            return $"⚠️ Error al escanear carpetas:\n{ex.Message}";
        }
    }
    
    /// <summary>
    /// Scan a mod directory and log what's found
    /// Returns the total count of items found
    /// </summary>
    private static int ScanModContent(string path, string category, string[]? extensions)
    {
        try
        {
            if (!Directory.Exists(path))
            {
                Console.WriteLine($"[PlatformPaths] {category}: directory not found, skipping scan");
                return 0;
            }
            
            int totalCount = 0;
            
            // Count files
            if (extensions != null)
            {
                foreach (var ext in extensions)
                {
                    totalCount += Directory.GetFiles(path, $"*{ext}", SearchOption.TopDirectoryOnly).Length;
                }
                
                if (totalCount > 0)
                    Console.WriteLine($"[PlatformPaths] {category}: found {totalCount} file(s)");
            }
            
            // Count folders (for notestyles, notesplashes, countdowns, fonts)
            var dirs = Directory.GetDirectories(path);
            if (dirs.Length > 0)
            {
                totalCount += dirs.Length;
                Console.WriteLine($"[PlatformPaths] {category}: found {dirs.Length} folder(s)");
                foreach (var dir in dirs)
                {
                    Console.WriteLine($"[PlatformPaths]   - {Path.GetFileName(dir)}");
                }
            }
            
            return totalCount;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[PlatformPaths] ERROR scanning {category}: {ex.Message}");
            return 0;
        }
    }
    
    /// <summary>
    /// Get a user-friendly message about where to place custom content
    /// </summary>
    public static string GetCustomContentInstructions()
    {
#if ANDROID
        return $"Place custom content in:\n" +
               $"Documents → FunkyMakerPlayerPublicMods → mods\n\n" +
               $"Subfolders:\n" +
               $"• levels/ - Custom levels (.fnmm, .rar)\n" +
               $"• notestyles/ - Note skins\n" +
               $"• notesplashes/ - Splash effects\n" +
               $"• fonts/ - Custom fonts\n" +
               $"• countdowns/ - Countdown assets\n\n" +
               $"💡 Use 'OPEN MODS FOLDER' button to open in file manager!";
#else
        return $"Place custom content in the 'mods' folder next to the game executable.\n\n" +
               $"Subfolders: levels, notestyles, notesplashes, fonts, countdowns";
#endif
    }

#if ANDROID
    /// <summary>
    /// Open Android file picker to select a .FNMM file
    /// Returns the path to the imported file in the levels directory, or null if cancelled
    /// </summary>
    public static System.Threading.Tasks.Task<string?> OpenFilePickerAsync()
    {
        var tcs = new System.Threading.Tasks.TaskCompletionSource<string?>();
        
        try
        {
            var activity = Microsoft.Xna.Framework.Game.Activity as Platforms.Android.MainActivity;
            if (activity == null)
            {
                Console.WriteLine("[PlatformPaths] Failed to get MainActivity reference");
                tcs.SetResult(null);
                return tcs.Task;
            }
            
            activity.PickFile((uri, fileName, stream) =>
            {
                if (uri == null || stream == null)
                {
                    Console.WriteLine("[PlatformPaths] File picker cancelled or failed");
                    tcs.SetResult(null);
                    return;
                }
                
                try
                {
                    // Save to levels directory
                    var levelsPath = GetLevelsPath();
                    var destPath = Path.Combine(levelsPath, fileName ?? "imported.fnmm");
                    
                    // Ensure unique filename
                    int counter = 1;
                    while (File.Exists(destPath))
                    {
                        var nameWithoutExt = Path.GetFileNameWithoutExtension(fileName ?? "imported");
                        var ext = Path.GetExtension(fileName ?? ".fnmm");
                        destPath = Path.Combine(levelsPath, $"{nameWithoutExt}_{counter}{ext}");
                        counter++;
                    }
                    
                    // Copy file from stream to destination
                    using (var fileStream = File.Create(destPath))
                    {
                        stream.CopyTo(fileStream);
                    }
                    
                    Console.WriteLine($"[PlatformPaths] File imported to: {destPath}");
                    tcs.SetResult(destPath);
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"[PlatformPaths] Failed to import file: {ex.Message}");
                    tcs.SetResult(null);
                }
            });
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[PlatformPaths] Failed to open file picker: {ex.Message}");
            tcs.SetResult(null);
        }
        
        return tcs.Task;
    }
    
    /// <summary>
    /// Get the crash logs directory path
    /// </summary>
    public static string GetCrashLogsPath()
    {
        var externalStorage = Android.OS.Environment.ExternalStorageDirectory?.AbsolutePath;
        if (string.IsNullOrEmpty(externalStorage))
        {
            externalStorage = Android.App.Application.Context.GetExternalFilesDir(null)?.AbsolutePath ?? "";
        }
        
        var crashPath = Path.Combine(externalStorage, APP_FOLDER, "crashes");
        
        if (!Directory.Exists(crashPath))
        {
            Directory.CreateDirectory(crashPath);
            Console.WriteLine($"[PlatformPaths] Created crash logs directory: {crashPath}");
        }
        
        return crashPath;
    }
#endif
}
