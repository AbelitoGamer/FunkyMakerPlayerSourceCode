using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;

namespace FunkyMakerPlayer;

/// <summary>Importing and deleting level files, shared by Freeplay and the online menu (fork).</summary>
public static class LevelFiles
{
    /// <summary>
    /// Opens the platform file picker and copies the chosen levels into the levels folder.
    /// Desktop: blocking native dialog (call from the game thread). Android: use
    /// <see cref="PickAndImportAndroidAsync"/> instead. Returns the imported paths (empty if cancelled).
    /// </summary>
    public static List<string> PickAndImportDesktop()
    {
        var imported = new List<string>();
#if !ANDROID && !BLAZORGL
        var selected = PlatformPaths.PickDesktopFiles(
            "Funky Maker Levels and osu! songs (*.fnmm;*.zip;*.rar;*.osz)\0*.FNMM;*.fnmm;*.ZIP;*.zip;*.RAR;*.rar;*.osz;*.OSZ\0All Files (*.*)\0*.*\0\0",
            "Select Funky Maker Level (.FNMM, .ZIP, .RAR) or osu! song (.OSZ)",
            allowMultiSelect: true);
        foreach (var file in selected)
        {
            if (File.Exists(file) && FreeplayMenu.IsSupportedLevelFile(file))
                imported.Add(ImportFile(file));
            else if (File.Exists(file) && Osu.OsuLibrary.IsBeatmapArchive(file))
                imported.Add(file); // the caller sends .osz files to the osu! importer
        }
#endif
        return imported;
    }

#if ANDROID
    /// <summary>Android file picker (the picker itself copies the file into the levels folder).</summary>
    public static async System.Threading.Tasks.Task<List<string>> PickAndImportAndroidAsync()
    {
        var path = await PlatformPaths.OpenFilePickerAsync();
        return string.IsNullOrEmpty(path) ? new List<string>() : new List<string> { path };
    }
#endif

    /// <summary>
    /// Copies a level archive into the levels folder (same behaviour as drag-and-drop: a file with the
    /// same name is replaced). Returns the path inside the levels folder, or the original on failure.
    /// </summary>
    public static string ImportFile(string sourcePath)
    {
        try
        {
            string levelsFolder = FreeplayMenu.GetDefaultLevelsFolder();
            Directory.CreateDirectory(levelsFolder);
            string destPath = Path.Combine(levelsFolder, Path.GetFileName(sourcePath));
            if (string.Equals(Path.GetFullPath(sourcePath), Path.GetFullPath(destPath), StringComparison.OrdinalIgnoreCase))
                return sourcePath;
            File.Copy(sourcePath, destPath, overwrite: true);
            Console.WriteLine($"[Import] Copied {Path.GetFileName(sourcePath)} → levels/");
            return destPath;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[Import] Copy failed, using original: {ex.Message}");
            return sourcePath;
        }
    }

    /// <summary>True if <paramref name="path"/> is inside the levels folder (only those can be deleted).</summary>
    public static bool IsInLevelsFolder(string path)
        => IsInside(path, FreeplayMenu.GetDefaultLevelsFolder()) || IsInside(path, Osu.OsuLibrary.RootFolder); // + osu! songs (fork)

    private static bool IsInside(string path, string folder)
    {
        try
        {
            string root = Path.GetFullPath(folder).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
            return Path.GetFullPath(path).StartsWith(root, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);
        }
        catch
        {
            return false;
        }
    }

    /// <summary>Where a deleted level went, for the confirmation toast.</summary>
    public enum DeleteOutcome { Failed, RecycleBin, Trash, Deleted }

    /// <summary>
    /// Deletes a level file or unpacked level folder from the levels folder. Windows: Recycle Bin.
    /// Linux: the desktop Trash via <c>gio trash</c> when available. Otherwise (and Android): deleted.
    /// </summary>
    public static DeleteOutcome Delete(string path, out string error)
    {
        error = "";
        if (!IsInLevelsFolder(path))
        {
            error = "Only levels inside the levels folder can be deleted.";
            return DeleteOutcome.Failed;
        }
        bool isDir = Directory.Exists(path);
        if (!isDir && !File.Exists(path))
        {
            error = "The level file no longer exists.";
            return DeleteOutcome.Failed;
        }

        try
        {
#if !ANDROID && !BLAZORGL
            if (OperatingSystem.IsWindows())
            {
                if (isDir)
                    Microsoft.VisualBasic.FileIO.FileSystem.DeleteDirectory(path,
                        Microsoft.VisualBasic.FileIO.UIOption.OnlyErrorDialogs, Microsoft.VisualBasic.FileIO.RecycleOption.SendToRecycleBin);
                else
                    Microsoft.VisualBasic.FileIO.FileSystem.DeleteFile(path,
                        Microsoft.VisualBasic.FileIO.UIOption.OnlyErrorDialogs, Microsoft.VisualBasic.FileIO.RecycleOption.SendToRecycleBin);
                return DeleteOutcome.RecycleBin;
            }
            if (OperatingSystem.IsLinux() && TryGioTrash(path))
                return DeleteOutcome.Trash;
#endif
            if (isDir) Directory.Delete(path, recursive: true);
            else File.Delete(path);
            return DeleteOutcome.Deleted;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[LevelFiles] Delete failed for {path}: {ex}");
            error = ex.Message;
            return DeleteOutcome.Failed;
        }
    }

    public static string DescribeDelete(DeleteOutcome outcome) => outcome switch
    {
        DeleteOutcome.RecycleBin => "moved to the Recycle Bin",
        DeleteOutcome.Trash => "moved to the Trash",
        _ => "deleted",
    };

    private static bool TryGioTrash(string path)
    {
        try
        {
            var psi = new ProcessStartInfo("gio") { UseShellExecute = false, CreateNoWindow = true, RedirectStandardError = true };
            psi.ArgumentList.Add("trash");
            psi.ArgumentList.Add(path);
            using var p = Process.Start(psi);
            if (p == null) return false;
            p.ErrorDataReceived += (_, _) => { };
            p.BeginErrorReadLine();
            if (!p.WaitForExit(5000))
            {
                try { p.Kill(); } catch { }
                return false;
            }
            return p.ExitCode == 0 && !File.Exists(path) && !Directory.Exists(path);
        }
        catch
        {
            return false;
        }
    }
}
