using System;
using System.IO;

namespace FunkyMakerPlayer.Editors.Common;

/// <summary>
/// Manages backups for .FNMM files when editing levels.
/// Creates a single "original.fnmm" backup the first time a level is saved (never overwritten).
/// </summary>
public static class BackupManager
{
    /// <summary>
    /// Creates a backup of the level file ONLY if it doesn't already exist.
    /// Backup location: Levels/Backups/LevelName/original.fnmm
    /// </summary>
    public static void CreateBackupIfNeeded(string levelPath)
    {
        try
        {
            string backupPath = GetBackupPath(levelPath);
            
            // If backup already exists, do nothing (preserve the original forever)
            if (File.Exists(backupPath))
            {
                Console.WriteLine($"[BackupManager] Backup already exists: {backupPath}");
                return;
            }
            
            // Create backup directory
            string backupDir = Path.GetDirectoryName(backupPath)!;
            if (!Directory.Exists(backupDir))
            {
                Directory.CreateDirectory(backupDir);
                Console.WriteLine($"[BackupManager] Created backup directory: {backupDir}");
            }
            
            // Copy original file to backup
            File.Copy(levelPath, backupPath, overwrite: false);
            Console.WriteLine($"[BackupManager] ✅ Created backup: {backupPath}");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[BackupManager] ❌ Failed to create backup: {ex.Message}");
            throw;
        }
    }
    
    /// <summary>
    /// Restores the original backup to the level path (overwriting current edits).
    /// </summary>
    public static void RestoreOriginal(string levelPath)
    {
        try
        {
            string backupPath = GetBackupPath(levelPath);
            
            if (!File.Exists(backupPath))
            {
                throw new FileNotFoundException($"No backup found for level: {levelPath}");
            }
            
            // Copy backup back to original location
            File.Copy(backupPath, levelPath, overwrite: true);
            Console.WriteLine($"[BackupManager] ✅ Restored original from backup: {backupPath}");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[BackupManager] ❌ Failed to restore backup: {ex.Message}");
            throw;
        }
    }
    
    /// <summary>
    /// Returns the path where the backup should be stored.
    /// Pattern: {levelsRootFolder}/Backups/LevelName/original.fnmm
    /// Example: mods/levels/Backups/MyLevel/original.fnmm
    /// </summary>
    public static string GetBackupPath(string levelPath)
    {
        string levelName = Path.GetFileNameWithoutExtension(levelPath);
        
        // Find the root "levels" folder by walking up the directory tree
        string? currentDir = Path.GetDirectoryName(levelPath);
        string? levelsRoot = null;
        
        while (currentDir != null)
        {
            if (Path.GetFileName(currentDir).Equals("levels", StringComparison.OrdinalIgnoreCase))
            {
                levelsRoot = currentDir;
                break;
            }
            currentDir = Path.GetDirectoryName(currentDir);
        }
        
        // If we couldn't find "levels" folder, fall back to immediate parent
        if (levelsRoot == null)
        {
            levelsRoot = Path.GetDirectoryName(levelPath)!;
        }
        
        // Create backup path: {levelsRoot}/Backups/LevelName/original.fnmm
        string backupDir = Path.Combine(levelsRoot, "Backups", levelName);
        string backupPath = Path.Combine(backupDir, "original.fnmm");
        
        return backupPath;
    }
}
