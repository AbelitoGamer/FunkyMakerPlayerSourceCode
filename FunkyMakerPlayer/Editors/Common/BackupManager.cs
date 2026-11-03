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
    /// Pattern: Levels/Backups/LevelName/original.fnmm
    /// </summary>
    public static string GetBackupPath(string levelPath)
    {
        string levelName = Path.GetFileNameWithoutExtension(levelPath);
        string levelsFolder = Path.GetDirectoryName(levelPath)!;
        
        // Navigate to parent folder (assuming levelPath is in Levels/)
        string parentFolder = Path.GetDirectoryName(levelsFolder)!;
        
        // Create backup path: Levels/Backups/LevelName/original.fnmm
        string backupDir = Path.Combine(parentFolder, "Backups", levelName);
        string backupPath = Path.Combine(backupDir, "original.fnmm");
        
        return backupPath;
    }
}
