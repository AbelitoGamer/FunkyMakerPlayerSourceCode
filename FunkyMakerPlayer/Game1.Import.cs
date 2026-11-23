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

/// <summary>Level import: drag-and-drop and copying files / folders into the levels folder (moved out of Game1.cs).</summary>
public partial class Game1
{
    private void OnFileDrop(object? sender, FileDropEventArgs e)
    {
        var importedPaths = new List<string>();

        foreach (var file in e.Files)
        {
            // osu! beatmap sets (.osz) and skins (.osk) (fork, Game1.Osu.cs)
            if (TryImportOsuFile(file))
                continue;

            if (FreeplayMenu.IsSupportedLevelFile(file))
            {
                string imported = ImportLevelFile(file);
                importedPaths.Add(imported);
                continue;
            }

            // Unpacked Funky Maker level folder (contains data.json or meta.json)
            if (Directory.Exists(file))
            {
                if (File.Exists(Path.Combine(file, "data.json")) || File.Exists(Path.Combine(file, "meta.json")))
                {
                    string importedDir = ImportLevelDirectory(file);
                    importedPaths.Add(importedDir);
                    continue;
                }

                // Psych Engine mod folder (drag the mod folder itself)
                StartLoadingLevel(file);
                return;
            }
        }

        if (importedPaths.Count > 0 && _inMultiplayer && _multiplayerMenu != null)
        {
            // Dropped on the online menu: add them there and stay (fork; this used to open Freeplay
            // and its "play now?" prompt).
            _multiplayerMenu.OnLevelsImported(importedPaths);
            _multiplayerMenu.ConsumeLevelsChanged();
            _freeplay?.Refresh();
            _levelSelector?.RefreshLevels();
            return;
        }

        if (importedPaths.Count > 0)
        {
            _freeplay?.OnLevelsImported(importedPaths);
            _levelSelector?.RefreshLevels();

            // If on the empty fallback drop screen (no level loaded), switch to Freeplay
            if (!_inFreeplay && _level == null && _loadState == LoadState.Idle)
            {
                _inFreeplay = true;
            }
        }
    }

    /// <summary>
    /// Copies a dropped .FNMM, .ZIP, or .RAR file into the levels folder (if it isn't already there).
    /// Returns the path in the levels folder (or the original if copy fails).
    /// </summary>
    private string ImportLevelFile(string sourcePath)
    {
        try
        {
            string levelsFolder = FreeplayMenu.GetDefaultLevelsFolder();
            if (!Directory.Exists(levelsFolder))
                Directory.CreateDirectory(levelsFolder);

            string fileName = Path.GetFileName(sourcePath);
            string destPath = Path.Combine(levelsFolder, fileName);

            // Already in the levels folder — no copy needed
            if (string.Equals(Path.GetFullPath(sourcePath), Path.GetFullPath(destPath),
                    StringComparison.OrdinalIgnoreCase))
                return sourcePath;

            // If a file with the same name exists, keep the new one (overwrite)
            File.Copy(sourcePath, destPath, overwrite: true);
            Console.WriteLine($"[Import] Copied {fileName} → levels/");

            return destPath;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[Import] Copy failed, using original: {ex.Message}");
            return sourcePath;
        }
    }

    /// <summary>
    /// Copies an unpacked level folder into the levels folder if it isn't already there.
    /// </summary>
    private string ImportLevelDirectory(string sourceDir)
    {
        try
        {
            string levelsFolder = FreeplayMenu.GetDefaultLevelsFolder();
            if (!Directory.Exists(levelsFolder))
                Directory.CreateDirectory(levelsFolder);

            string dirName = Path.GetFileName(sourceDir.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
            string destDir = Path.Combine(levelsFolder, dirName);

            if (string.Equals(Path.GetFullPath(sourceDir), Path.GetFullPath(destDir),
                    StringComparison.OrdinalIgnoreCase))
                return sourceDir;

            foreach (var dirPath in Directory.GetDirectories(sourceDir, "*", SearchOption.AllDirectories))
                Directory.CreateDirectory(dirPath.Replace(sourceDir, destDir));

            foreach (var filePath in Directory.GetFiles(sourceDir, "*.*", SearchOption.AllDirectories))
            {
                string targetFile = filePath.Replace(sourceDir, destDir);
                Directory.CreateDirectory(Path.GetDirectoryName(targetFile)!);
                File.Copy(filePath, targetFile, overwrite: true);
            }

            Console.WriteLine($"[Import] Copied folder {dirName} → levels/");
            return destDir;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[Import] Folder copy failed, using original: {ex.Message}");
            return sourceDir;
        }
    }
}
