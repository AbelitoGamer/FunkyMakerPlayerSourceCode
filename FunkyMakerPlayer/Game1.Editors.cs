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

/// <summary>Editor Hub, Level Selector and Chart Editor navigation, playtests (moved out of Game1.cs).</summary>
public partial class Game1
{
    /// <summary>
    /// Opens the Editor Picker menu
    /// </summary>
    private void EnterEditorPicker()
    {
        _inEditorPicker = true;
        _inFreeplay = false;
        _editorPicker = new Editors.EditorPicker(_pixel!, _font, _fontRenderer);
        _editorPicker.ResetInputCooldown();
        RefreshActiveDiscordPresence(immediate: true);
        Console.WriteLine("[Game1] Entered Editor Picker");
    }

    /// <summary>
    /// Exits the Editor Picker and returns to main menu
    /// </summary>
    private void ExitEditorPicker()
    {
        _inEditorPicker = false;
        _inFreeplay = true;
        _editorPicker = null;
        _freeplay?.ResetInputCooldown();
        RefreshActiveDiscordPresence(immediate: true);
        Console.WriteLine("[Game1] Exited Editor Picker");
    }

    /// <summary>
    /// Opens a specific editor tool
    /// </summary>
    private void OpenEditor(Editors.EditorType type)
    {
        Console.WriteLine($"[Game1] Opening editor: {type}");
        
        if (type == Editors.EditorType.NewChart || type == Editors.EditorType.Chart)
        {
            _inEditorPicker = false;
            _inLevelSelector = true;
            _levelSelectorReturnTarget = ChartEditorReturnTarget.EditorPicker;
            _levelSelector = new Editors.Chart.LevelSelector(
                _pixel!, _font!, _fontRenderer,
                startCreateModal: type == Editors.EditorType.NewChart);
            RefreshActiveDiscordPresence(immediate: true);
            Console.WriteLine($"[Game1] Opened Level Selector (startCreateModal={type == Editors.EditorType.NewChart})");
        }
        else if (type == Editors.EditorType.Hud)
        {
            // HUD Editor: open launcher screen and return to Editor Hub on ESC
            _inEditorPicker = false;
            _hudEditorReturnTarget = HudEditorReturnTarget.EditorPicker;
            _inMainSettings = true;
            _mainSettingsMenu?.ResetInputCooldown();
            RefreshActiveDiscordPresence(immediate: true);
            Console.WriteLine("[Game1] Opened HUD Editor from Editor Hub");
        }
        else if (type == Editors.EditorType.Character)
        {
            _inEditorPicker = false;
            _inEditor = true;
            _chartEditorReturnTarget = ChartEditorReturnTarget.EditorPicker;
            var charEd = new Editors.Character.CharacterEditor(GraphicsDevice, _pixel!, _font, _fontRenderer);
            charEd.ResetInputCooldown();
            _activeEditor = charEd;
            _editorSessionStartUtc = DateTimeOffset.UtcNow;
            Window.Title = "Funky Maker Character Editor";
            RefreshActiveDiscordPresence(immediate: true);
            Console.WriteLine("[Game1] Opened Character Editor from Editor Hub");
        }
        else
        {
            // WIP editors
            Console.WriteLine($"[Game1] {type} editor is WIP");
            // Stay in editor picker
        }
    }

    /// <summary>
    /// Exits the active editor and returns to the caller (Editor Hub, Freeplay panel, or Gameplay)
    /// </summary>
    private void ExitEditor()
    {
        bool savedInSession = false;
        string editedPath = FnmmPath;
        bool reuseGameplayCache = false;
        if (_activeEditor is Editors.Chart.ChartEditor ce)
        {
            savedInSession = ce.SavedInSession;
            editedPath = ce.LevelPath;
            reuseGameplayCache = !ce.OwnsPreviewTextures
                && _spriteTextureCache != null
                && string.Equals(FnmmPath, editedPath, StringComparison.OrdinalIgnoreCase);
            ce.DisposeAudio();
        }
        else if (_activeEditor is Editors.Character.CharacterEditor charEd)
        {
            string? savedSlug = charEd.LastSavedSlug;
            charEd.Dispose();
            if (_suspendedChartEditor is Editors.Chart.ChartEditor resumedCe)
            {
                _suspendedChartEditor = null;
                resumedCe.OnReturnedFromCharacterEditor(savedSlug);
                resumedCe.ResetInputCooldown();
                _activeEditor = resumedCe;
                _inEditor = true;
                Window.Title = $"Funky Maker Chart Editor — {Path.GetFileName(resumedCe.LevelPath)}";
                RefreshActiveDiscordPresence(immediate: true);
                return;
            }
        }

        var target = _chartEditorReturnTarget;
        _chartEditorReturnTarget = ChartEditorReturnTarget.EditorPicker;
        _inEditor = false;
        _activeEditor = null;
        _editorSessionStartUtc = null;

        switch (target)
        {
            case ChartEditorReturnTarget.Freeplay:
                _inFreeplay = true;
                _freeplay?.Refresh();
                _freeplay?.OpenLevelsPanel();
                _freeplay?.ResetInputCooldown();
                ResetWindowTitle();
                break;

            case ChartEditorReturnTarget.Gameplay:
                if (!string.IsNullOrEmpty(editedPath) && (savedInSession || _level == null))
                {
                    StartLoadingLevel(editedPath, preserveSpriteCache: reuseGameplayCache);
                }
                else if (_level != null)
                {
                    Window.Title = $"Funky Maker Player — {_level.LevelName}";
                    ResumeSong();
                }
                else
                {
                    _inFreeplay = true;
                    _freeplay?.Refresh();
                    _freeplay?.OpenLevelsPanel();
                    _freeplay?.ResetInputCooldown();
                    ResetWindowTitle();
                }
                break;

            default:
                _inEditorPicker = true;
                _editorPicker = new Editors.EditorPicker(_pixel!, _font, _fontRenderer);
                _editorPicker.ResetInputCooldown();
                ResetWindowTitle();
                break;
        }
        RefreshActiveDiscordPresence(immediate: true);
        Console.WriteLine($"[Game1] Exited editor -> {target}");
    }

    /// <summary>
    /// Exits level selector and returns to Editor Picker or Freeplay
    /// </summary>
    private void ExitLevelSelector()
    {
        var target = _levelSelectorReturnTarget;
        _levelSelectorReturnTarget = ChartEditorReturnTarget.EditorPicker;
        _inLevelSelector = false;
        _levelSelector = null;

        if (target == ChartEditorReturnTarget.Freeplay)
        {
            _inFreeplay = true;
            _freeplay?.Refresh();
            _freeplay?.OpenLevelsPanel();
            _freeplay?.ResetInputCooldown();
            ResetWindowTitle();
        }
        else
        {
            _inEditorPicker = true;
            _editorPicker = new Editors.EditorPicker(_pixel!, _font, _fontRenderer);
            _editorPicker.ResetInputCooldown();
            ResetWindowTitle();
        }
        RefreshActiveDiscordPresence(immediate: true);
        Console.WriteLine($"[Game1] Exited Level Selector -> {target}");
    }

    /// <summary>
    /// Opens Chart Editor with selected level
    /// </summary>
    private void OpenChartEditor(string levelPath, ChartEditorReturnTarget returnTarget = ChartEditorReturnTarget.EditorPicker)
    {
        // osu! difficulties are read-only conversions (fork): the editor would save them as .fnmm over a .osu.
        if (Osu.OsuLibrary.IsOsuChart(levelPath))
        {
            _freeplay?.ShowToast("Los charts de osu! no se pueden editar aquí");
            return;
        }
        Console.WriteLine($"[Game1] Opening Chart Editor with level: {levelPath} (return={returnTarget})");

        _chartEditorReturnTarget = returnTarget;
        _inLevelSelector = false;
        _inFreeplay = false;
        _inEditorPicker = false;
        _inEditor = true;
        _levelSelector = null;

        Dictionary<string, Dictionary<int, SpriteTextureCacheEntry>>? sharedSpriteCache = null;
        if (returnTarget == ChartEditorReturnTarget.Gameplay &&
            _spriteTextureCache != null &&
            _spriteTextureCache.Count > 0 &&
            string.Equals(FnmmPath, levelPath, StringComparison.OrdinalIgnoreCase))
        {
            sharedSpriteCache = _spriteTextureCache;
        }

        var ce = new Editors.Chart.ChartEditor(levelPath, _pixel!, _font, _fontRenderer, _noteStyle, sharedSpriteCache);
        ce.ResetInputCooldown();
        _activeEditor = ce;
        _editorSessionStartUtc = DateTimeOffset.UtcNow;
        Window.Title = $"Funky Maker Chart Editor — {Path.GetFileName(levelPath)}";
        RefreshActiveDiscordPresence(immediate: true);
    }

    private void BeginEditorPlaytest(Editors.Chart.ChartEditor ce)
    {
        if (!string.IsNullOrEmpty(_playtestTempPath))
        {
            try { if (File.Exists(_playtestTempPath)) File.Delete(_playtestTempPath); } catch { }
            _playtestTempPath = null;
        }

        var editedLevel = ce.GetEditedLevel();
        if (editedLevel == null)
        {
            string levelToLoad = ce.LevelPath;
            if (string.IsNullOrEmpty(levelToLoad))
                return;

            _suspendedChartEditor = ce;
            _activeEditor = null;
            _inEditor = false;
            _playtestingFromEditor = true;
            _playtestStartSeconds = Math.Max(0f, ce.PlaytestStartSeconds);
            StartLoadingLevel(levelToLoad);
            return;
        }

        _suspendedChartEditor = ce;
        _activeEditor = null;
        _inEditor = false;
        _playtestingFromEditor = true;
        _playtestStartSeconds = Math.Max(0f, ce.PlaytestStartSeconds);

        ResetGameplayStateForLevelLoad(ce.LevelPath);
        _spriteTextureCache = ce.PreviewSpriteTextureCache;
        _spriteCacheOwnedByEditor = ce.OwnsPreviewTextures;

        var (cachedPcm, sampleRate, channels) = ce.GetCachedAudioPcm();
        _preloadedPlaytestPcm = cachedPcm != null ? (cachedPcm, sampleRate, channels) : null;

        _pendingLevel = FnmmSceneRenderer.CloneForGameplay(editedLevel);

        if (ce.HasCachedPreviewTextures)
        {
            Console.WriteLine($"[Playtest] Reusing cached preview textures ({editedLevel.Sprites.Count} sprite(s)) — instant start");
            _level = _pendingLevel;
            _pendingLevel = null;
            _loadState = LoadState.Done;
            try
            {
                FinalizeLoadedLevelForGameplay(showReadyScreen: false);
            }
            catch (Exception ex)
            {
                HandleSetupLevelFailure(ex);
            }
        }
        else
        {
            string levelName = Path.GetFileNameWithoutExtension(ce.LevelPath);
            _loadingScreen = new LoadingScreen(levelName, _pixel!, _font, FindAssetsRoot(), GraphicsDevice, false);
            _loadingComplete = true;
            _loadState = LoadState.Finalizing;
        }
        RefreshActiveDiscordPresence(immediate: true);
    }

    private void ReturnFromPlaytest()
    {
        _musicInst?.Stop();
        _musicInst?.Dispose();
        _music?.Dispose();
        _musicInst = null;
        _music = null;
        _musicPcm = null;
        _preloadedPlaytestPcm = null;
        _started = false;
        _paused = false;
        _gameplayPresenceStartUtc = null;
        _inPauseMenu = false;
        _gameOverOverlay?.Hide();
        _songTransition = null;
        _transitionToFreeplay = false;
        _songEndDelay = 0f;
        _loadingScreen = null;
        _pendingLevel = null;
        _loadState = LoadState.Done;
        _level = null;
        _objAnimators = Array.Empty<SpriteAnimator?>();
        _events = null;
        _noteRenderers.Clear();
        if (_healthBar != null)
        {
            _healthBar.OpponentIcon = null;
            _healthBar.PlayerIcon = null;
            _healthBar.FlipOpponentIcon = false;
            _healthBar.FlipPlayerIcon = false;
        }
        if (_spriteCacheOwnedByEditor)
        {
            _spriteTextureCache = null;
            _levelTextures.Clear(); // the editor's textures: never ours to free
        }
        _spriteCacheOwnedByEditor = false;
        _playtestingFromEditor = false;
        _playtestStartSeconds = 0f;

        if (!string.IsNullOrEmpty(_playtestTempPath))
        {
            try { if (File.Exists(_playtestTempPath)) File.Delete(_playtestTempPath); } catch { }
            _playtestTempPath = null;
        }

        if (_suspendedChartEditor is Editors.Chart.ChartEditor ce)
        {
            FnmmPath = ce.LevelPath;
            _activeEditor = ce;
            _suspendedChartEditor = null;
            _inEditor = true;
            ce.ResetInputCooldown();
            Window.Title = $"Funky Maker Chart Editor — {Path.GetFileName(ce.LevelPath)}";
        }
        else
        {
            _suspendedChartEditor = null;
            _editorSessionStartUtc = null;
            _inFreeplay = true;
            _freeplay?.Refresh();
            _freeplay?.ResetInputCooldown();
            ResetWindowTitle();
        }
        RefreshActiveDiscordPresence(immediate: true);
        Console.WriteLine("[Game1] Returned from Playtest to Chart Editor");
    }
}
