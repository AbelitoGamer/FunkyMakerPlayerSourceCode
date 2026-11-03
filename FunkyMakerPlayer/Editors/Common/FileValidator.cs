using System;
using System.IO;
using System.IO.Compression;

namespace FunkyMakerPlayer.Editors.Common;

/// <summary>
/// Validates .FNMM files and FnmmLevel structures before saving.
/// </summary>
public static class FileValidator
{
    /// <summary>
    /// Validates that a FnmmLevel has all required fields populated.
    /// Throws if validation fails.
    /// </summary>
    public static void ValidateFnmmLevel(FnmmLevel level)
    {
        if (level == null)
            throw new ArgumentNullException(nameof(level), "Level cannot be null");
        
        if (level.Data == null)
            throw new InvalidOperationException("Level.Data cannot be null");
        
        if (level.Scene == null)
            throw new InvalidOperationException("Level.Scene cannot be null");
        
        if (level.Events == null)
            throw new InvalidOperationException("Level.Events cannot be null");
        
        // Validate BPM (must be > 0)
        if (level.Data.Bpm <= 0)
            throw new InvalidOperationException($"Invalid BPM: {level.Data.Bpm} (must be > 0)");
        
        // Validate strumlines exist (even if empty)
        if (level.Data.Strumlines == null)
            throw new InvalidOperationException("Level.Data.Strumlines cannot be null");
        
        Console.WriteLine($"[FileValidator] ✅ Level validation passed");
        Console.WriteLine($"  BPM: {level.Data.Bpm}");
        Console.WriteLine($"  Strumlines: {level.Data.Strumlines.Count}");
        Console.WriteLine($"  Objects: {level.Scene.Objects.Count}");
        Console.WriteLine($"  Events: {level.Events.Events.Count}");
    }
    
    /// <summary>
    /// Validates that a .FNMM ZIP archive contains required files.
    /// Returns true if valid, false otherwise.
    /// </summary>
    public static bool ValidateFnmmArchive(string path)
    {
        try
        {
            if (!File.Exists(path))
            {
                Console.WriteLine($"[FileValidator] ❌ File does not exist: {path}");
                return false;
            }
            
            using var zip = ZipFile.OpenRead(path);
            
            // Check for required file: data.json (either at root or in subfolder)
            bool hasDataJson = false;
            
            foreach (var entry in zip.Entries)
            {
                if (entry.FullName.EndsWith("data.json", StringComparison.OrdinalIgnoreCase))
                {
                    hasDataJson = true;
                    break;
                }
            }
            
            if (!hasDataJson)
            {
                Console.WriteLine($"[FileValidator] ❌ Missing required file: data.json");
                return false;
            }
            
            Console.WriteLine($"[FileValidator] ✅ Archive validation passed: {path}");
            return true;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[FileValidator] ❌ Archive validation failed: {ex.Message}");
            return false;
        }
    }
}
