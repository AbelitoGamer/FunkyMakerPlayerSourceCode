using System;
using System.IO;
using System.Collections.Generic;
using System.Runtime.InteropServices;

namespace FunkyMakerPlayer;

/// <summary>
/// Platform-specific path handling for FunkyMaker Player.
/// Handles differences between Windows/Linux (local folders) and Android (storage).
/// 
/// Android Strategy:
/// - Use app-private storage as the working directory
/// - When user selects a custom folder via SAF, copy files to app-private storage
/// - All file operations work on local copies (fast and reliable)
/// </summary>
public static class PlatformPaths
{
    private const string APP_FOLDER = "FunkyMakerPlayer";
    private const string MODS_FOLDER = "mods";
    
    /// <summary>
    /// Get the base mods directory
    /// - Windows/Linux: ./mods/ (next to exe)
    /// - Android: App-private storage (fast, always accessible)
    /// </summary>
    /// <summary>
    /// Gets the actual directory containing the executable (handles single-file builds cleanly
    /// where AppContext.BaseDirectory points to a temp extraction folder).
    /// </summary>
    public static string GetAppBaseDirectory()
    {
        try
        {
            string? processPath = Environment.ProcessPath;
            if (!string.IsNullOrEmpty(processPath))
            {
                string? dir = Path.GetDirectoryName(processPath);
                if (!string.IsNullOrEmpty(dir) && Directory.Exists(dir))
                    return dir;
            }
        }
        catch { }

        return AppDomain.CurrentDomain.BaseDirectory;
    }

    public static string GetModsBasePath()
    {
#if ANDROID
        var context = Android.App.Application.Context;
        var appStorage = context.GetExternalFilesDir(null)?.AbsolutePath ?? context.FilesDir?.AbsolutePath ?? "";
        var modsPath = Path.Combine(appStorage, MODS_FOLDER);
        
        if (!Directory.Exists(modsPath))
        {
            Directory.CreateDirectory(modsPath);
            Console.WriteLine($"[PlatformPaths] Created mods directory: {modsPath}");
        }
        
        return modsPath;
#else
        return Path.Combine(GetAppBaseDirectory(), MODS_FOLDER);
#endif
    }
    
#if ANDROID
    /// <summary>
    /// Import mods from a user-selected folder (via SAF) to app storage.
    /// This imports levels, notestyles, notesplashes, fonts, and countdowns.
    /// </summary>
    public static void ImportFromCustomFolder(Android.Net.Uri folderUri)
    {
        try
        {
            Console.WriteLine($"[PlatformPaths] ===  IMPORT START ===");
            Console.WriteLine($"[PlatformPaths] Importing from URI: {folderUri}");
            
            var context = Android.App.Application.Context;
            
            // Use AndroidX DocumentFile to navigate the selected folder
            var rootFolder = AndroidX.DocumentFile.Provider.DocumentFile.FromTreeUri(context, folderUri);
            if (rootFolder == null || !rootFolder.Exists())
            {
                Console.WriteLine($"[PlatformPaths] ERROR: Cannot access selected folder");
                return;
            }
            
            Console.WriteLine($"[PlatformPaths] Root folder: {rootFolder.Name}");
            
            // Find the mods folder
            var modsFolder = FindModsFolder(rootFolder);
            if (modsFolder == null)
            {
                Console.WriteLine($"[PlatformPaths] ERROR: No mods folder found");
                Console.WriteLine($"[PlatformPaths] Please select a folder containing 'mods/' subdirectory");
                return;
            }
            
            Console.WriteLine($"[PlatformPaths] Found mods folder: {modsFolder.Name}");
            
            int totalImported = 0;
            
            // Import levels
            var levelsFolder = FindSubfolder(modsFolder, "levels");
            if (levelsFolder != null)
            {
                Console.WriteLine($"[PlatformPaths] --- Importing levels ---");
                int count = ImportLevelFiles(context, levelsFolder, GetLevelsPath());
                Console.WriteLine($"[PlatformPaths] Imported {count} levels");
                totalImported += count;
            }
            
            // Import notestyles
            var notestylesFolder = FindSubfolder(modsFolder, "notestyles");
            if (notestylesFolder != null)
            {
                Console.WriteLine($"[PlatformPaths] --- Importing notestyles ---");
                int count = ImportFolderContents(context, notestylesFolder, GetNoteStylesPath());
                Console.WriteLine($"[PlatformPaths] Imported {count} notestyle items");
                totalImported += count;
            }
            
            // Import notesplashes
            var notesplashesFolder = FindSubfolder(modsFolder, "notesplashes");
            if (notesplashesFolder != null)
            {
                Console.WriteLine($"[PlatformPaths] --- Importing notesplashes ---");
                int count = ImportFolderContents(context, notesplashesFolder, GetNoteSplashesPath());
                Console.WriteLine($"[PlatformPaths] Imported {count} notesplash items");
                totalImported += count;
            }
            
            // Import fonts
            var fontsFolder = FindSubfolder(modsFolder, "fonts");
            if (fontsFolder != null)
            {
                Console.WriteLine($"[PlatformPaths] --- Importing fonts ---");
                int count = ImportFolderContents(context, fontsFolder, GetFontsPath());
                Console.WriteLine($"[PlatformPaths] Imported {count} font items");
                totalImported += count;
            }
            
            // Import countdowns
            var countdownsFolder = FindSubfolder(modsFolder, "countdowns");
            if (countdownsFolder != null)
            {
                Console.WriteLine($"[PlatformPaths] --- Importing countdowns ---");
                int count = ImportFolderContents(context, countdownsFolder, GetCountdownsPath());
                Console.WriteLine($"[PlatformPaths] Imported {count} countdown items");
                totalImported += count;
            }
            
            Console.WriteLine($"[PlatformPaths] === IMPORT COMPLETE: {totalImported} total items ===");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[PlatformPaths] ERROR importing: {ex.Message}");
            Console.WriteLine($"[PlatformPaths] Stack: {ex.StackTrace}");
        }
    }
    
    /// <summary>
    /// Find the mods folder in the selected directory
    /// </summary>
    private static AndroidX.DocumentFile.Provider.DocumentFile? FindModsFolder(AndroidX.DocumentFile.Provider.DocumentFile rootFolder)
    {
        try
        {
            var files = rootFolder.ListFiles();
            if (files == null) return null;
            
            // Check if root folder is already named "mods"
            if (rootFolder.Name?.Equals("mods", StringComparison.OrdinalIgnoreCase) == true)
            {
                Console.WriteLine($"[PlatformPaths] Root folder is 'mods'");
                return rootFolder;
            }
            
            // Look for mods subfolder
            foreach (var item in files)
            {
                if (item.IsDirectory && item.Name?.Equals("mods", StringComparison.OrdinalIgnoreCase) == true)
                {
                    Console.WriteLine($"[PlatformPaths] Found 'mods' subfolder");
                    return item;
                }
            }
            
            Console.WriteLine($"[PlatformPaths] No 'mods' folder found");
            return null;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[PlatformPaths] Error finding mods folder: {ex.Message}");
            return null;
        }
    }
    
    /// <summary>
    /// Find a subfolder by name
    /// </summary>
    private static AndroidX.DocumentFile.Provider.DocumentFile? FindSubfolder(AndroidX.DocumentFile.Provider.DocumentFile parentFolder, string folderName)
    {
        try
        {
            var files = parentFolder.ListFiles();
            if (files == null) return null;
            
            foreach (var item in files)
            {
                if (item.IsDirectory && item.Name?.Equals(folderName, StringComparison.OrdinalIgnoreCase) == true)
                {
                    return item;
                }
            }
            
            return null;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[PlatformPaths] Error finding subfolder {folderName}: {ex.Message}");
            return null;
        }
    }
    
    /// <summary>
    /// Import all files from a folder (recursively)
    /// Used for notestyles, notesplashes, fonts, countdowns
    /// </summary>
    private static int ImportFolderContents(Android.Content.Context context, AndroidX.DocumentFile.Provider.DocumentFile sourceFolder, string destPath)
    {
        int count = 0;
        
        try
        {
            var files = sourceFolder.ListFiles();
            if (files == null) return 0;
            
            foreach (var item in files)
            {
                if (item.IsFile)
                {
                    var fileName = item.Name ?? "unknown";
                    var destFile = Path.Combine(destPath, fileName);
                    
                    // Check if already exists
                    if (File.Exists(destFile))
                    {
                        var existingSize = new FileInfo(destFile).Length;
                        var sourceSize = item.Length();
                        
                        if (existingSize == sourceSize)
                        {
                            Console.WriteLine($"[PlatformPaths] Skipped (exists): {fileName}");
                            continue;
                        }
                    }
                    
                    // Copy file
                    using var inputStream = context.ContentResolver?.OpenInputStream(item.Uri);
                    if (inputStream != null)
                    {
                        using var outputStream = File.Create(destFile);
                        inputStream.CopyTo(outputStream);
                        Console.WriteLine($"[PlatformPaths] Imported: {fileName}");
                        count++;
                    }
                }
                else if (item.IsDirectory)
                {
                    // Create subdirectory and import recursively
                    var subfolderName = item.Name ?? "unknown";
                    var destSubfolder = Path.Combine(destPath, subfolderName);
                    
                    if (!Directory.Exists(destSubfolder))
                    {
                        Directory.CreateDirectory(destSubfolder);
                        Console.WriteLine($"[PlatformPaths] Created subfolder: {subfolderName}");
                    }
                    
                    count += ImportFolderContents(context, item, destSubfolder);
                }
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[PlatformPaths] Error importing folder contents: {ex.Message}");
        }
        
        return count;
    }
    
    /// <summary>
    /// Import all level files from a DocumentFile folder
    /// </summary>
    private static int ImportLevelFiles(Android.Content.Context context, AndroidX.DocumentFile.Provider.DocumentFile sourceFolder, string destPath)
    {
        int count = 0;
        
        try
        {
            var files = sourceFolder.ListFiles();
            if (files == null) return 0;
            
            foreach (var file in files)
            {
                if (file.IsFile && IsLevelFile(file.Name))
                {
                    var fileName = file.Name ?? "unknown.fnmm";
                    var destFile = Path.Combine(destPath, fileName);
                    
                    // Check if already exists
                    if (File.Exists(destFile))
                    {
                        var existingSize = new FileInfo(destFile).Length;
                        var sourceSize = file.Length();
                        
                        if (existingSize == sourceSize)
                        {
                            Console.WriteLine($"[PlatformPaths] Skipped (exists): {fileName}");
                            continue;
                        }
                    }
                    
                    // Copy file
                    using var inputStream = context.ContentResolver?.OpenInputStream(file.Uri);
                    if (inputStream != null)
                    {
                        using var outputStream = File.Create(destFile);
                        inputStream.CopyTo(outputStream);
                        Console.WriteLine($"[PlatformPaths] Imported: {fileName}");
                        count++;
                    }
                }
                else if (file.IsDirectory)
                {
                    // Recursively import from subdirectories
                    count += ImportLevelFiles(context, file, destPath);
                }
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[PlatformPaths] Error importing files: {ex.Message}");
        }
        
        return count;
    }
    
    /// <summary>
    /// Check if a filename is a level file
    /// </summary>
    private static bool IsLevelFile(string? fileName)
    {
        if (string.IsNullOrEmpty(fileName)) return false;
        
        return fileName.EndsWith(".fnmm", StringComparison.OrdinalIgnoreCase) ||
               fileName.EndsWith(".zip",  StringComparison.OrdinalIgnoreCase) ||
               fileName.EndsWith(".rar",  StringComparison.OrdinalIgnoreCase);
    }
#endif
    
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
        string exeAssets = Path.Combine(GetAppBaseDirectory(), "Assets");
        if (Directory.Exists(exeAssets))
            return exeAssets;
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
            
            Console.WriteLine($"[PlatformPaths] [OK] Mods base: {modsBase}");
            Console.WriteLine($"[PlatformPaths] [OK] Levels: {levels}");
            Console.WriteLine($"[PlatformPaths] [OK] NoteStyles: {noteStyles}");
            Console.WriteLine($"[PlatformPaths] [OK] NoteSplashes: {noteSplashes}");
            Console.WriteLine($"[PlatformPaths] [OK] Fonts: {fonts}");
            Console.WriteLine($"[PlatformPaths] [OK] Countdowns: {countdowns}");
            
            // Scan for content and count items
            int totalLevels = ScanModContent(levels, "levels", new[] { ".fnmm", ".zip", ".rar" });
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
                return "📁 Carpetas de mods creadas!\n\nCrea una carpeta con una subcarpeta 'mods' y usa 'GRANT FOLDER ACCESS' para importarla, o 'IMPORT FILE' para un nivel suelto.";
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
            Console.WriteLine($"[PlatformPaths] === Scanning {category} ===");
            Console.WriteLine($"[PlatformPaths] Path: {path}");
            Console.WriteLine($"[PlatformPaths] Exists: {Directory.Exists(path)}");
            
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
                    // For levels, search recursively to find files in mods/levels/ subdirectory
                    var searchOption = category == "levels" ? SearchOption.AllDirectories : SearchOption.TopDirectoryOnly;
                    var files = Directory.GetFiles(path, $"*{ext}", searchOption);
                    Console.WriteLine($"[PlatformPaths] Files with extension {ext}: {files.Length}");
                    if (files.Length > 0)
                    {
                        foreach (var file in files)
                        {
                            Console.WriteLine($"[PlatformPaths]   FILE: {Path.GetFileName(file)}");
                        }
                    }
                    totalCount += files.Length;
                }
                
                if (totalCount > 0)
                    Console.WriteLine($"[PlatformPaths] {category}: found {totalCount} file(s)");
            }
            
            // Count folders (for notestyles, notesplashes, countdowns, fonts)
            var dirs = Directory.GetDirectories(path);
            Console.WriteLine($"[PlatformPaths] Total directories found: {dirs.Length}");
            if (dirs.Length > 0)
            {
                totalCount += dirs.Length;
                Console.WriteLine($"[PlatformPaths] {category}: found {dirs.Length} folder(s)");
                foreach (var dir in dirs)
                {
                    Console.WriteLine($"[PlatformPaths]   FOLDER: {Path.GetFileName(dir)}");
                }
            }
            
            Console.WriteLine($"[PlatformPaths] === Total {category}: {totalCount} items ===");
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
        return $"The app imports mods from a folder you select (it copies them into app storage).\n\n" +
               $"1. Create a folder with a 'mods' subfolder, e.g.:\n" +
               $"   Documents → FunkyMakerPlayerPublicMods → mods\n\n" +
               $"2. Put your content in mods:\n" +
               $"   • levels/ - Custom levels (.fnmm, .rar)\n" +
               $"   • notestyles/ - Note skins\n" +
               $"   • notesplashes/ - Splash effects\n" +
               $"   • fonts/ - Custom fonts\n" +
               $"   • countdowns/ - Countdown assets\n\n" +
               $"3. Use 'GRANT FOLDER ACCESS' and select the folder containing 'mods'.\n" +
               $"   Or use 'IMPORT FILE' to import a single level.";
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

#if !BLAZORGL && !ANDROID
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct OPENFILENAMEW
    {
        public int lStructSize;
        public IntPtr hwndOwner;
        public IntPtr hInstance;
        public string lpstrFilter;
        public IntPtr lpstrCustomFilter;
        public int nMaxCustFilter;
        public int nFilterIndex;
        public IntPtr lpstrFile;
        public int nMaxFile;
        public IntPtr lpstrFileTitle;
        public int nMaxFileTitle;
        public string? lpstrInitialDir;
        public string lpstrTitle;
        public int Flags;
        public short nFileOffset;
        public short nFileExtension;
        public string? lpstrDefExt;
        public IntPtr lCustData;
        public IntPtr lpfnHook;
        public string? lpTemplateName;
        public IntPtr pvReserved;
        public int dwReserved;
        public int flagsEx;
    }

    [DllImport("comdlg32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool GetOpenFileNameW(ref OPENFILENAMEW ofn);

    /// <summary>
    /// Opens a native Windows file picker dialog with the specified null-delimited filter.
    /// </summary>
    public static List<string> PickDesktopFiles(string filter, string title, bool allowMultiSelect = false)
    {
        var results = new List<string>();
        if (!OperatingSystem.IsWindows())
            return results;

        const int OFN_NOCHANGEDIR      = 0x00000008;
        const int OFN_ALLOWMULTISELECT = 0x00000200;
        const int OFN_PATHMUSTEXIST    = 0x00000800;
        const int OFN_FILEMUSTEXIST    = 0x00001000;
        const int OFN_EXPLORER         = 0x00080000;
        const int MAX_BUFFER_CHARS     = 65536;

        IntPtr fileBuffer = Marshal.AllocHGlobal(MAX_BUFFER_CHARS * 2);
        try
        {
            Marshal.WriteInt16(fileBuffer, 0);
            int flags = OFN_EXPLORER | OFN_FILEMUSTEXIST | OFN_PATHMUSTEXIST | OFN_NOCHANGEDIR;
            if (allowMultiSelect) flags |= OFN_ALLOWMULTISELECT;

            var ofn = new OPENFILENAMEW
            {
                lStructSize = Marshal.SizeOf<OPENFILENAMEW>(),
                hwndOwner   = IntPtr.Zero,
                lpstrFilter = filter,
                nFilterIndex = 1,
                lpstrFile   = fileBuffer,
                nMaxFile    = MAX_BUFFER_CHARS,
                lpstrTitle  = title,
                Flags       = flags
            };

            if (GetOpenFileNameW(ref ofn))
            {
                var parts = new List<string>();
                int offsetBytes = 0;
                while (offsetBytes < MAX_BUFFER_CHARS * 2)
                {
                    string? s = Marshal.PtrToStringUni(IntPtr.Add(fileBuffer, offsetBytes));
                    if (string.IsNullOrEmpty(s))
                        break;
                    parts.Add(s);
                    offsetBytes += (s.Length + 1) * 2;
                }

                if (parts.Count == 1)
                {
                    results.Add(parts[0]);
                }
                else if (parts.Count > 1)
                {
                    string directory = parts[0];
                    for (int i = 1; i < parts.Count; i++)
                        results.Add(Path.Combine(directory, parts[i]));
                }
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[PlatformPaths] Error opening desktop file dialog: {ex.Message}");
        }
        finally
        {
            Marshal.FreeHGlobal(fileBuffer);
        }

        return results;
    }
#endif
}
