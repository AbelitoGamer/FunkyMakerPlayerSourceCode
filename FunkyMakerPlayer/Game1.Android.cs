using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Audio;
using Microsoft.Xna.Framework.Graphics;
using Microsoft.Xna.Framework.Input;
using SixLabors.ImageSharp.Processing;
using FunkyMakerPlayer.Managers;  // ← NUEVO: Import de managers
using FunkyMakerPlayer.UI;  // ← NUEVO: Import de UI menus
using FunkyMakerPlayer.Multiplayer;  // ← NUEVO: Import de multiplayer
using MonoGame.Extended;  // ← MonoGame.Extended for OrthographicCamera

namespace FunkyMakerPlayer;

/// <summary>Android only: touch guard, APK asset extraction, mods folder / file picker (moved out of Game1.cs).</summary>
public partial class Game1
{
    /// <summary>True once the window ignores touches (always true off Android).</summary>
    private bool AndroidTouchGuardReady()
    {
#if ANDROID
        if (_touchGuardFrames < 0)
        {
            SetWindowTouchable(false);
            _touchGuardFrames = 3;
            return false;
        }
        if (_touchGuardFrames > 0)
        {
            _touchGuardFrames--;
            return false;
        }
#endif
        return true;
    }

    private void ReleaseAndroidTouchGuard()
    {
#if ANDROID
        if (_touchGuardFrames < 0) return;
        _touchGuardFrames = -1;
        SetWindowTouchable(true);
#endif
    }

#if ANDROID
    private static void SetWindowTouchable(bool touchable)
    {
        try
        {
            var activity = Microsoft.Xna.Framework.Game.Activity;
            if (activity == null) return;
            // Runs inline when already on the UI thread (the game loop normally is).
            activity.RunOnUiThread(() =>
            {
                try
                {
                    if (touchable) activity.Window?.ClearFlags(global::Android.Views.WindowManagerFlags.NotTouchable);
                    else activity.Window?.AddFlags(global::Android.Views.WindowManagerFlags.NotTouchable);
                }
                catch (Exception ex) { Console.WriteLine($"[Android] Window touch flag failed: {ex.Message}"); }
            });
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[Android] Window touch flag failed: {ex.Message}");
        }
    }
#endif

#if ANDROID
    /// <summary>
    /// Extract Assets folder from APK to internal storage (Android only)
    /// This allows the game to access bundled assets like fonts, images, etc.
    /// Note: AndroidAsset paths in APK are lowercase: assets/ not Assets/
    /// </summary>
    private static void ExtractAssetsFromAPK()
    {
        try
        {
            var context = global::Android.App.Application.Context;
            var assetManager = context.Assets;
            var filesDir = context.FilesDir?.AbsolutePath;
            
            if (filesDir == null)
            {
                Console.WriteLine("[ExtractAssets] ERROR: FilesDir is null");
                return;
            }
            
            string targetAssetsPath = Path.Combine(filesDir, "Assets");
            
            Console.WriteLine($"[ExtractAssets] Starting extraction to: {targetAssetsPath}");
            
            // List what's at the root of the APK assets
            string[]? rootItems = assetManager?.List("");
            Console.WriteLine($"[ExtractAssets] Root APK assets count: {rootItems?.Length ?? 0}");
            if (rootItems != null)
            {
                foreach (var item in rootItems.Take(10))
                {
                    Console.WriteLine($"[ExtractAssets] Root item: {item}");
                }
            }
            
            // Copy everything that's inside the "assets" folder in the APK
            // Note: In APK, AndroidAsset files are at root level, not inside an "assets" folder
            // We need to filter and copy only the ones we need
            CopyAndroidAssets(assetManager, targetAssetsPath);
            
            Console.WriteLine("[ExtractAssets] Successfully extracted Assets from APK");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[ExtractAssets] ERROR: {ex.Message}\n{ex.StackTrace}");
        }
    }
    
    /// <summary>
    /// Copy Android assets from APK to filesystem, recreating folder structure
    /// Only copies essential files to avoid long extraction times
    /// </summary>
    private static void CopyAndroidAssets(global::Android.Content.Res.AssetManager assetManager, string targetRootPath)
    {
        try
        {
            // List of essential folders to extract (skip large unnecessary files)
            string[] essentialFolders = { "LoadingScreen", "data", "images", "Content" };
            
            Console.WriteLine($"[CopyAndroidAssets] Extracting essential folders to: {targetRootPath}");
            
            foreach (var folder in essentialFolders)
            {
                Console.WriteLine($"[CopyAndroidAssets] Processing folder: {folder}");
                
                // Check if folder exists in APK
                string[] items = assetManager.List(folder) ?? Array.Empty<string>();
                if (items.Length > 0)
                {
                    string targetFolder = Path.Combine(targetRootPath, folder);
                    CopyAssetItemRecursive(assetManager, folder, targetFolder);
                }
            }
            
            Console.WriteLine("[CopyAndroidAssets] Extraction complete");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[CopyAndroidAssets] ERROR: {ex.Message}");
        }
    }
    
    /// <summary>
    /// Recursively copy an asset item (file or folder)
    /// </summary>
    private static void CopyAssetItemRecursive(global::Android.Content.Res.AssetManager assetManager, string assetPath, string targetPath)
    {
        try
        {
            // Check if it's a directory by listing its contents
            string[]? subItems = assetManager.List(assetPath);
            
            if (subItems != null && subItems.Length > 0)
            {
                // It's a directory
                Console.WriteLine($"[CopyAssetItem] Directory: {assetPath} ({subItems.Length} items)");
                
                if (!Directory.Exists(targetPath))
                    Directory.CreateDirectory(targetPath);
                
                foreach (var subItem in subItems)
                {
                    string subAssetPath = $"{assetPath}/{subItem}";
                    string subTargetPath = Path.Combine(targetPath, subItem);
                    CopyAssetItemRecursive(assetManager, subAssetPath, subTargetPath);
                }
            }
            else
            {
                // It's a file
                if (File.Exists(targetPath))
                {
                    Console.WriteLine($"[CopyAssetItem] Skip existing: {targetPath}");
                    return;
                }
                
                Console.WriteLine($"[CopyAssetItem] Copying file: {assetPath} -> {targetPath}");
                
                // Ensure parent directory exists
                string? parentDir = Path.GetDirectoryName(targetPath);
                if (parentDir != null && !Directory.Exists(parentDir))
                    Directory.CreateDirectory(parentDir);
                
                using (var inputStream = assetManager.Open(assetPath))
                using (var outputStream = File.Create(targetPath))
                {
                    inputStream.CopyTo(outputStream);
                }
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[CopyAssetItem] ERROR copying {assetPath}: {ex.Message}");
        }
    }
    
    /// <summary>
    /// Recursively copy a folder from APK assets to internal storage
    /// </summary>
    /// <param name="assetManager">Android AssetManager</param>
    /// <param name="sourceFolder">Source folder path in APK (lowercase, e.g. "assets/Font")</param>
    /// <param name="targetFolder">Target folder path in filesystem</param>
    /// <param name="apkPrefix">Prefix to add to AssetManager.Open() calls (e.g. "assets/")</param>
    private static void CopyAssetFolder(global::Android.Content.Res.AssetManager assetManager, string sourceFolder, string targetFolder, string apkPrefix = "")
    {
        try
        {
            // Create target directory
            if (!Directory.Exists(targetFolder))
            {
                Directory.CreateDirectory(targetFolder);
                Console.WriteLine($"[CopyAssetFolder] Created directory: {targetFolder}");
            }
            
            // List items - AssetManager.List() requires lowercase path without prefix
            string listPath = string.IsNullOrEmpty(sourceFolder) ? "assets" : sourceFolder.ToLowerInvariant().Replace(apkPrefix, "");
            string[]? items = assetManager.List(listPath);
            
            Console.WriteLine($"[CopyAssetFolder] Listing '{listPath}', found {items?.Length ?? 0} items");
            
            if (items == null || items.Length == 0)
            {
                return;
            }
            
            // Process each item
            foreach (string item in items)
            {
                string sourceItemPath = string.IsNullOrEmpty(listPath) || listPath == "assets" 
                    ? item 
                    : $"{listPath}/{item}";
                string targetItemPath = Path.Combine(targetFolder, item);
                
                Console.WriteLine($"[CopyAssetFolder] Processing: {sourceItemPath}");
                
                // Check if it's a folder or file by trying to list it
                string[]? subItems = assetManager.List(sourceItemPath);
                if (subItems != null && subItems.Length > 0)
                {
                    // It's a folder, recurse
                    Console.WriteLine($"[CopyAssetFolder] '{sourceItemPath}' is a folder with {subItems.Length} items");
                    CopyAssetFolder(assetManager, sourceItemPath, targetItemPath, "");
                }
                else
                {
                    // It's a file, copy it
                    Console.WriteLine($"[CopyAssetFolder] '{sourceItemPath}' is a file");
                    CopyAssetFile(assetManager, sourceItemPath, targetItemPath);
                }
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[CopyAssetFolder] ERROR copying {sourceFolder}: {ex.Message}\n{ex.StackTrace}");
        }
    }
    
    /// <summary>
    /// Copy a single file from APK assets to internal storage
    /// </summary>
    private static void CopyAssetFile(global::Android.Content.Res.AssetManager assetManager, string sourceFile, string targetFile)
    {
        try
        {
            // Skip if file already exists
            if (File.Exists(targetFile))
            {
                Console.WriteLine($"[CopyAssetFile] Skipping (exists): {targetFile}");
                return;
            }
            
            // Ensure target directory exists
            string? targetDir = Path.GetDirectoryName(targetFile);
            if (targetDir != null && !Directory.Exists(targetDir))
            {
                Directory.CreateDirectory(targetDir);
            }
            
            using (var inputStream = assetManager.Open(sourceFile))
            using (var outputStream = File.Create(targetFile))
            {
                inputStream.CopyTo(outputStream);
            }
            
            Console.WriteLine($"[CopyAssetFile] Copied: {sourceFile} -> {targetFile}");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[CopyAssetFile] ERROR copying {sourceFile}: {ex.Message}");
        }
    }
#endif

#if ANDROID
    // ══════════════════════════════════════════════════════════════════════════
    // Android-specific: File Picker for Mods
    // ══════════════════════════════════════════════════════════════════════════
    
    /// <summary>
    /// Open the mods folder in file manager (like FNF's "OPEN DATA FOLDER")
    /// </summary>
    private void OpenAndroidModsFolder()
    {
        var activity = Microsoft.Xna.Framework.Game.Activity as Platforms.Android.MainActivity;
        if (activity == null)
        {
            Console.WriteLine("[Game1] Cannot open mods folder: MainActivity not found");
            return;
        }
        
        Console.WriteLine("[Game1] Opening mods folder...");
        activity.OpenModsFolder();
    }
    
    private volatile bool _pendingAndroidModsRefresh;

    /// <summary>
    /// Let user pick the mods folder using Storage Access Framework
    /// This grants persistent read/write access to the selected folder
    /// </summary>
    public void PickAndroidModsFolder()
    {
        var activity = Microsoft.Xna.Framework.Game.Activity as Platforms.Android.MainActivity;
        if (activity == null)
        {
            Console.WriteLine("[Game1] Cannot pick mods folder: MainActivity not found");
            return;
        }
        
        Console.WriteLine("[Game1] Launching folder picker...");
        activity.PickModsFolder((uri) =>
        {
            if (uri != null)
            {
                Console.WriteLine($"[Game1] User selected mods folder: {uri}");
                _pendingAndroidModsRefresh = true;
            }
            else
            {
                Console.WriteLine("[Game1] User cancelled folder picker");
            }
        });
    }

    private void FlushPendingAndroidModsRefresh()
    {
        if (!_pendingAndroidModsRefresh) return;
        _pendingAndroidModsRefresh = false;
        _freeplay?.Refresh();
        _freeplay?.ShowToast("Mods folder imported!");
        _levelSelector?.RefreshLevels();
        _multiplayerMenu?.RefreshLevelList();
    }
    
    /// <summary>
    /// Open Android file picker to load a custom level/mod (.fnmm or .rar)
    /// </summary>
    private void OpenAndroidFilePicker()
    {
        var activity = Microsoft.Xna.Framework.Game.Activity as Platforms.Android.MainActivity;
        if (activity == null)
        {
            Console.WriteLine("[Game1] Cannot open file picker: MainActivity not found");
            return;
        }
        
        Console.WriteLine("[Game1] Opening Android file picker for mods...");
        
        activity.PickFile((uri, fileName, stream) =>
        {
            if (uri == null || stream == null || string.IsNullOrEmpty(fileName))
            {
                Console.WriteLine("[Game1] File picker cancelled or failed");
                return;
            }
            
            try
            {
                Console.WriteLine($"[Game1] File picked: {fileName}");
                
                // Load the level from the stream
                LoadLevelFromStream(stream, fileName);
                
                // Return to freeplay menu to show the loaded level
                _inFreeplay = true;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[Game1] Error loading file from picker: {ex.Message}");
                _errorReturnToMultiplayer = false;
                _errorDismissGrace = 0.25f;
                _errorMsg = $"Failed to load {fileName}:\n{ex.Message}";
            }
            finally
            {
                stream?.Dispose();
            }
        });
    }
    
    /// <summary>
    /// Load a level from a stream (supports .fnmm and .rar files)
    /// </summary>
    private void LoadLevelFromStream(Stream stream, string fileName)
    {
        try
        {
            Console.WriteLine($"[Game1] Loading level from stream: {fileName}");
            
            // Get the mods folder to save the file
            string levelsFolder = FreeplayMenu.GetDefaultLevelsFolder();
            if (!Directory.Exists(levelsFolder))
            {
                Directory.CreateDirectory(levelsFolder);
            }
            
            // Sanitize provider display name and avoid silently overwriting existing files
            string cleanFileName = Path.GetFileName((fileName ?? "imported.fnmm").Replace('\\', '/'));
            if (string.IsNullOrWhiteSpace(cleanFileName)) cleanFileName = "imported.fnmm";
            foreach (char c in Path.GetInvalidFileNameChars())
                cleanFileName = cleanFileName.Replace(c, '_');
            string destPath = Path.Combine(levelsFolder, cleanFileName);

            using (stream)
            using (var fileStream = File.Create(destPath))
            {
                stream.CopyTo(fileStream);
            }
            
            Console.WriteLine($"[Game1] Level saved to: {destPath}");
            
            // Refresh the freeplay menu, select the imported level, and show toast notification
            _freeplay?.OnLevelsImported(new[] { destPath });
            
            // Show success message
            _errorMsg = null; // Clear any previous error
            
            // Return to freeplay
            _inFreeplay = true;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[Game1] Error loading level from stream: {ex.Message}");
            _errorReturnToMultiplayer = false;
            _errorDismissGrace = 0.25f;
            _errorMsg = $"Failed to load {fileName}:\n{ex.Message}";
        }
    }
#endif
}
