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

/// <summary>Level loading: reset, background load, finalize, SetupLevel and its helpers (moved out of Game1.cs).</summary>
public partial class Game1
{
    private Exception? _pendingLoadException;

    /// <summary>
    /// Begins async loading of a .FNMM file.
    /// Phase 1 (background thread): parse file, decompress, read JSONs/audio bytes.
    /// Phase 2 (main thread, next Update): create GPU textures, init renderers.
    /// The loading screen is shown immediately and spins while loading.
    /// </summary>
    private string ResetGameplayStateForLevelLoad(string path)
    {
        BeginReplayStateForLoad(); // replays (fork): a pending replay becomes active, recording resets

        // Stop current song immediately
        _musicInst?.Stop();
        _started      = false;
        _paused       = false;
        _gameplayPresenceStartUtc = null;
        _errorMsg     = null;
        _pendingLoadException = null;
        _songTransition = null;
        _songEndDelay = 0f;
        _gameOverOverlay?.Hide();
        _pendingPath  = path;
        _inFreeplay   = false;
        _crashLogged  = false;  // Reset crash log flag for new level
        // Reset all overlay states so they don't persist into the new level
        _keyBindOverlay.Reset();
        _settingsManager?.Reset();  // Resets IsActive and Selection
        _inPauseMenu  = false;
        if (_arrowsMenu != null) _arrowsMenu.IsActive = false;
        if (_splashMenu != null) _splashMenu.IsActive = false;

        string rawLevelName = System.IO.Path.GetFileNameWithoutExtension(path);
        string levelName = _isMultiplayerMatch
            ? (!string.IsNullOrWhiteSpace(_multiplayerLevelName) ? _multiplayerLevelName! : SanitizeLevelDisplayName(rawLevelName))
            : rawLevelName;
        FnmmPath = path;
        if (_isMultiplayerMatch)
            DiscordRpc.SetLoadingMultiplayer(levelName, GetResolvedOpponentDisplayName());
        else
            DiscordRpc.SetLoading(levelName);
        return levelName;
    }

    private void StartLoadingLevel(string path, bool preserveSpriteCache = false)
    {
        try
        {
            string levelName = ResetGameplayStateForLevelLoad(path);
            _loadState = LoadState.Loading;
            _preloadedPlaytestPcm = null;
            _musicPcm = null; // a new level never reuses the previous level's decoded music
            bool cacheOwnedByEditor = _spriteCacheOwnedByEditor;
            _spriteCacheOwnedByEditor = false;
            if (!preserveSpriteCache)
            {
                // A chart editor (open or suspended for a playtest) may share these textures: never free
                // them then. Online matches and normal play never have one.
                bool editorAround = cacheOwnedByEditor || _activeEditor != null || _suspendedChartEditor != null;
                if (!editorAround) DisposeLevelTextures();
                else _levelTextures.Clear();
                _spriteTextureCache = null;
                Console.WriteLine("[Cache] Cleared sprite texture cache for new level");
            }
            else
            {
                Console.WriteLine("[Cache] Preserved shared sprite texture cache for reload");
            }

            // Show loading screen in main window
            _loadingScreen   = new LoadingScreen(levelName, _pixel!, _font,
                                   FindAssetsRoot(), GraphicsDevice, _isMultiplayerMatch);
            _loadingComplete = false;
            
            // Initialize multiplayer state in loading screen
            if (_isMultiplayerMatch)
            {
                _loadingScreen.MyPlayerRole = _multiplayerPlayerRole;
                // Initialize ready states: I'm not ready yet, opponent status unknown
                if (_multiplayerPlayerRole == 1)
                {
                    _loadingScreen.Player1Ready = false;  // Me
                    _loadingScreen.Player2Ready = false;  // Opponent
                }
                else if (_multiplayerPlayerRole == 2)
                {
                    _loadingScreen.Player1Ready = false;  // Opponent
                    _loadingScreen.Player2Ready = false;  // Me
                }
            }
        }
        catch (Exception ex)
        {
            HandleSetupLevelFailure(ex);
            return;
        }

        // Load file on background thread (no GPU calls here)
        bool chartOnly = _isMultiplayerMatch && MultiplayerSettings.Instance.ChartOnlyMode && !Osu.OsuLibrary.IsOsuChart(path);
        if (chartOnly)
            Console.WriteLine("[Online] Chart-Only mode active: loading chart + music only (skipping all sprites)");
        System.Threading.Tasks.Task.Run(() =>
        {
            try
            {
                // Pass progress callback to loading screen (capture local ref so main-thread nulling can't NRE)
                _pendingLevel = FnmmLoader.Load(path, (progress, step) =>
                {
                    var ls = _loadingScreen;
                    if (ls != null)
                    {
                        ls.LoadingProgress = progress;
                        ls.CurrentStep = step;
                    }
                }, chartOnly: chartOnly);
                
                _loadingComplete = true;
                _loadState       = LoadState.Finalizing;
            }
            catch (Exception ex)
            {
                string msg = ex.Message == "PSYCH_ENGINE_MOD"
                    ? "Psych Engine mods are not supported. Export the level as a .FNMM (ZIP) and try again."
                    : ex.Message;
                _pendingLoadException = new Exception(msg, ex);
                _loadingComplete = true;
                _loadState       = LoadState.Finalizing;
                Console.Error.WriteLine($"[FunkyMakerPlayer] {ex}");
            }
        });
    }

    private void FinalizeLoadedLevelForGameplay(bool showReadyScreen)
    {
        if (_level == null) return;

        // Fork: the displayed level name is the one the menus show (file / folder name; for official
        // multiplayer levels the server's name), not meta.json's name, so Freeplay, the online menu,
        // the loading screen, results, window title, Discord and match history all agree.
        if (_isMultiplayerMatch)
        {
            _level.LevelName = !string.IsNullOrWhiteSpace(_multiplayerLevelName)
                ? _multiplayerLevelName!
                : SanitizeLevelDisplayName(FileDisplayName(FnmmPath));
            _multiplayerLevelName = _level.LevelName;
        }
        else if (_activeReplay != null && !string.IsNullOrWhiteSpace(_activeReplay.LevelName))
        {
            _level.LevelName = _activeReplay.LevelName; // replays: the recorded name (official temp files have none)
        }
        else if (!string.IsNullOrWhiteSpace(FnmmPath))
        {
            _level.LevelName = FileDisplayName(FnmmPath);
        }

        SetupLevel();
        if (_errorMsg != null || _level == null)
            return;

        _songTime        = 0f;
        _songBeats       = 0f;
        _started         = false;
        _paused          = false;
        if (_healthBar != null)
            _healthBar.Health = 0.5f;
        _gameOverOverlay?.Hide();
        _countdownOverlay?.EnsureLoaded(SelectedCountdown);
        _countdownOverlay?.Reset();

        // Start FNF-style receptor intro animation (pop-in effect)
        // Only if HUD is visible (respects chart events that hide receptors)
        if (!EventHidesHud)
        {
            foreach (var renderer in _noteRenderers)
                renderer?.StartReceptorIntro(0.0f, 0.05f);
        }

        _cameraManager?.Reset();
        _healthBar?.ResetBounce();
        _songStopwatch.Reset();
        _pausedTime = 0.0;

        if (!showReadyScreen)
        {
            _loadingScreen = null;
            _loadingComplete = true;
            if (_playtestingFromEditor && _level != null)
            {
                _started = true;
                _gameplayPresenceStartUtc = DateTimeOffset.UtcNow;
                _songStopwatch.Restart();
                _pausedTime = 0.0;
                if (!IsWindowActive)
                {
                    _inPauseMenu = true;
                    PauseSong();
                }
                else
                {
                    if (_playtestStartSeconds > 0.1f)
                        SeekSong(_playtestStartSeconds);
                    else
                        _musicInst?.Play();
                }
                RefreshActiveDiscordPresence(immediate: true);
            }
            return;
        }

        // Update loading screen with real level name
        // In multiplayer, show loading screen normally and send Ready when done
        _loadingScreen = new LoadingScreen(
            _level.LevelName, _pixel!, _font,
            FindAssetsRoot(), GraphicsDevice, _isMultiplayerMatch);
        _loadingComplete = true;

        if (_isMultiplayerMatch)
            OnRoomMatchLevelLoaded();
    }

    private void HandleSetupLevelFailure(Exception ex)
    {
        bool wasMp = _isMultiplayerMatch;
        if (wasMp)
            QuitRoomMatch();

        _errorReturnToMultiplayer = wasMp;
        _errorDismissGrace = 0.5f;
        _errorMsg        = ex.Message;
        _loadingComplete = true;
        _loadState       = LoadState.Done;
        CloseAllInMatchOverlays();
        _loadingScreen   = null;  // clear loading screen, error shown via DrawError
        _level           = null;  // clear level so DrawError path is taken
        ResetMultiplayerMatchState();
        Console.Error.WriteLine($"[FunkyMakerPlayer] SetupLevel failed: {ex}");
    }

    /// <summary>
    /// Frees the previous level's textures when the next one loads (fork). They used to be dropped
    /// without Dispose: MonoGame only releases GPU textures when the GC finalizes them, which managed
    /// memory pressure (not GPU memory) decides. Online rounds load level after level straight from
    /// the room, so phones ran out of GPU memory and the next levels came out black or partly black.
    /// </summary>
    private void DisposeLevelTextures()
    {
        DisposeOsuExtras();
        var all = new HashSet<Texture2D>(_levelTextures);
        _levelTextures.Clear();
        if (_spriteTextureCache != null)
            foreach (var sprite in _spriteTextureCache.Values)
                foreach (var entry in sprite.Values)
                    foreach (var t in entry.Textures)
                        if (t != null) all.Add(t);
        // Nothing may draw them any more (the loading screen draws _level's scene behind it).
        _objAnimators = Array.Empty<SpriteAnimator?>();
        if (_healthBar != null)
        {
            _healthBar.OpponentIcon = null;
            _healthBar.PlayerIcon = null;
        }
        int freed = 0;
        foreach (var t in all)
        {
            if (t.IsDisposed) continue;
            try { t.Dispose(); freed++; } catch { }
        }
        if (freed > 0) Console.WriteLine($"[Cache] Freed {freed} textures of the previous level");
    }

    private void SetupLevel()
    {
        if (_level == null) return;

        try
        {
            // Update loading screen progress during GPU texture creation
            if (_loadingScreen != null)
            {
                _loadingScreen.CurrentStep = "Creating GPU textures...";
                _loadingScreen.LoadingProgress = 0.0f;
            }

            // Reset scene objects to their initial state (undo any event mutations from previous run)
            foreach (var obj in _level.Scene.Objects)
                obj.ResetToInitialState();

            ConfigureRunModifiers(); // Freeplay song rate / enemy mode (fork, Game1.Freeplay.cs)

            _audioDeviceReset = false;  // clear any stale device reset flag
            _musicInst?.Dispose();
            _music?.Dispose();
            _musicInst = null; // never leave a disposed instance behind (Play() on it throws)
            _music = null;

        // ── Window aspect ratio / render target ──────────────────────────────
        bool landscape = _level.Scene.Landscape;
        
        // Use fixed resolution for gameplay (letterboxing will be applied)
        int  rtW = landscape ? 1280 : 720;
        int  rtH = landscape ? 720  : 1280;

        // Recreate render target if size changed (physical size follows the window, see EnsureRenderTarget)
        _rtWidth  = rtW;
        _rtHeight = rtH;
        EnsureRenderTarget();
        int sw = rtW;
        int sh = rtH;

        // ── Optional Online Local Custom Character Equip (client-side only) ──
        if (_isMultiplayerMatch && !_onlineSpectating && !_level.IsOsu)
        {
            var mpSettings = Multiplayer.MultiplayerSettings.Instance;
            if (!mpSettings.ChartOnlyMode && !string.IsNullOrWhiteSpace(mpSettings.EquippedCharacter))
            {
                var eqPkg = Editors.Character.CharacterLibrary.LoadPackage(mpSettings.EquippedCharacter);
                if (eqPkg != null)
                {
                    int myLocalCharStrum = (_level.Data.Strumlines.Count >= 2 && _multiplayerPlayerRole == 2) ? 1 : 0;
                    _level = FnmmSceneRenderer.CloneForGameplay(_level);
                    var applied = Editors.Character.CharacterLibrary.ApplyCharacterToLevel(
                        _level,
                        mpSettings.EquippedCharacter,
                        myLocalCharStrum,
                        updateIcon: true,
                        autoScaleToReplaced: true,
                        spriteKeyOverride: "__mp_local_char",
                        preloadedPackage: eqPkg);
                    if (applied != null)
                    {
                        Console.WriteLine($"[Multiplayer] Equipped local custom character '{eqPkg.Metadata.Name}' on strumLine {myLocalCharStrum}");
                    }
                }
            }
        }

        // ── Pre-load all sprite textures via shared scene renderer ────────────
        int maxTexSize = GetGpuMaxTextureSize();
        Console.WriteLine($"[GPU] Max texture size: {maxTexSize}px (device limit)");
        Console.WriteLine($"[Scene] Loading {_level.Sprites.Count} sprites from level...");

        _spriteTextureCache ??= new Dictionary<string, Dictionary<int, SpriteTextureCacheEntry>>();

#if ANDROID
        // On Android (both 32-bit and 64-bit), free each raw compressed PNG byte[] immediately
        // after GPU upload unless playtesting from Chart Editor (which shares the edited level's buffers).
        bool releaseRawSpriteBytes = !_playtestingFromEditor;
#else
        bool releaseRawSpriteBytes = !Environment.Is64BitProcess && _isMultiplayerMatch;
#endif

        var texCache = FnmmSceneRenderer.LoadLevelTextures(
            GraphicsDevice,
            _level,
            maxTexSize,
            _spriteTextureCache,
            (progress, step) =>
            {
                if (_loadingScreen == null) return;
                _loadingScreen.LoadingProgress = progress;
                _loadingScreen.CurrentStep = step;
                RenderLoadingScreen();
            },
            () => RenderLoadingScreen(),
            releaseSourceBytes: releaseRawSpriteBytes);

        if (_loadingScreen != null)
        {
            _loadingScreen.LoadingProgress = 0.80f;
            _loadingScreen.CurrentStep = "Textures loaded!";
            RenderLoadingScreen();
        }
        foreach (var list in texCache.Values)
            foreach (var t in list)
                if (t != null) _levelTextures.Add(t);

        // Failed images are skipped; say so instead of showing a silent black scene (32-bit phones
        // running out of memory on big levels).
        _textureLoadWarning = FnmmSceneRenderer.LastFailedTextures > 0
            ? $"{FnmmSceneRenderer.LastFailedTextures} image(s) of this level couldn't be loaded ({FnmmSceneRenderer.LastTextureError}). " +
              "The device may be out of memory; parts of the scene will be missing."
            : null;
        if (_textureLoadWarning != null) Console.WriteLine($"[Scene] {_textureLoadWarning}");

        // Free any remaining raw PNG byte[] buffers and compact before decoding the song audio.
        if (releaseRawSpriteBytes)
        {
            foreach (var (_, (_, frames)) in _level.Sprites)
            {
                for (int fi = 0; fi < frames.Count; fi++)
                    frames[fi] = Array.Empty<byte>();
            }
            _level.LooseImages.Clear();
            FnmmSceneRenderer.ForceCompactingGc();
        }

        // ── Build one animator per scene object ──────────────────────────────
        if (_loadingScreen != null)
        {
            _loadingScreen.LoadingProgress = 0.85f;
            _loadingScreen.CurrentStep = "Initializing scene objects...";
            RenderLoadingScreen();
        }

        var objs = _level.Scene.Objects;
        _objAnimators = FnmmSceneRenderer.CreateAnimators(_level, texCache, rtW, rtH);

        // ── Health icons ─────────────────────────────────────────────────────
        if (_loadingScreen != null)
        {
            _loadingScreen.LoadingProgress = 0.90f;
            _loadingScreen.CurrentStep = "Loading icons...";
            RenderLoadingScreen();  // Force render
        }
        
        // FNF convention: icons[0] = opponent (left/red side), icons[1] = player (right/green side)
        // Icons are SpriteAnimators: anim 0 = normal, anim 1 = losing
        var icons = _level.Scene.Icons;
        
        // Multiplayer: Swap icons AND mirror them horizontally when Player 2 plays Enemy
        // Player 1 (BF chart): Normal assignment (opponent=0, player=1), no mirror
        // Player 2 (Enemy chart): Swapped (opponent=1, player=0) + horizontal mirror so both face inward
        // Replays (fork): an online replay uses the match layout ("duel") without being a live match.
        bool duelLayout = _isMultiplayerMatch || IsDuelReplay;
        int duelRole = _isMultiplayerMatch ? _multiplayerPlayerRole : DuelReplayRole;
        bool flipIconsForEnemyRole = (duelLayout && duelRole == 2) || (!duelLayout && _runEnemy);
        var opponentIcon = BuildIconAnimator(
            flipIconsForEnemyRole ? (icons.Count > 1 ? icons[1] : null) : (icons.Count > 0 ? icons[0] : null),
            texCache);
        var playerIcon = BuildIconAnimator(
            flipIconsForEnemyRole ? (icons.Count > 0 ? icons[0] : null) : (icons.Count > 1 ? icons[1] : null),
            texCache);

        ApplyHealthBarIcon(isPlayerSlot: false, opponentIcon, flipIconsForEnemyRole);
        ApplyHealthBarIcon(isPlayerSlot: true, playerIcon, flipIconsForEnemyRole);
        if (_healthBar != null)
            _healthBar.IconMode = IconMode;

        if (_isMultiplayerMatch)
        {
            Console.WriteLine(flipIconsForEnemyRole
                ? "[Game1] Multiplayer Role 2: Swapped & mirrored icons (I am Enemy character)"
                : "[Game1] Multiplayer Role 1: Normal icons (I am BF character)");
        }

        // ── Note renderers ───────────────────────────────────────────────────
        // Support multiple strumlines (not just 2)
        // FNF layout: Strumline 0 = PLAYER (RIGHT), Strumline 1 = OPPONENT (LEFT)
        // Additional strumlines are placed to the right
        _noteRenderers.Clear();
        int strumlineCount = _level.Data.Strumlines.Count;
        
        // Multiplayer: Assign charts based on player role for competitive gameplay
        // Each player plays a DIFFERENT chart but sees it in THEIR player position (right side)
        // Player 1 = BF/Player chart (chart 1)
        // Player 2 = Enemy/Opponent chart (chart 0)
        // This way both charts are played competitively
        int myChartIndex = 1;
        int opponentChartIndex = 1;
        
        Console.WriteLine($"[Game1] SetupLevel: _isMultiplayerMatch={_isMultiplayerMatch}, _multiplayerPlayerRole={_multiplayerPlayerRole}");
        
        if (duelLayout)
        {
            if (duelRole == 1)
            {
                // Player 1: I control Boyfriend (right side)
                myChartIndex = 0;  // Chart 0 = Player/BF in standard FNF
                opponentChartIndex = 1;  // Chart 1 = Enemy/Dad
                Console.WriteLine("[Game1] Multiplayer: Role 1 (Player 1) - I control BOYFRIEND (chart 0), opponent is ENEMY (chart 1)");
            }
            else if (duelRole == 2)
            {
                // Player 2: I control Enemy (still shows on right side for me)
                myChartIndex = 1;  // Chart 1 = Enemy/Dad
                opponentChartIndex = 0;  // Chart 0 = Player/BF
                Console.WriteLine("[Game1] Multiplayer: Role 2 (Player 2) - I control ENEMY (chart 1), opponent is BOYFRIEND (chart 0)");
            }
            else
            {
                // Fallback: Role not set correctly!
                Console.WriteLine($"[Game1] ERROR: Multiplayer role is {_multiplayerPlayerRole} (should be 1 or 2)!");
                myChartIndex = 1;  // Default to BF chart
                opponentChartIndex = 1;
            }
            
            // Duel replays: exactly 2 strumlines (the two tracks). Live matches (fork): keep every
            // strumline; the extra ones (2+) are played by the CPU so all characters keep singing.
            strumlineCount = _isMultiplayerMatch ? Math.Max(2, _level.Data.Strumlines.Count) : 2;
            // osu! songs (fork): one strumline, centred, like in single player (only Mirror rounds
            // play them, so everybody plays that chart; the others' scores stay in the online HUD).
            if (_level.IsOsu) strumlineCount = 1;
            if (_level.Data.Strumlines.Count < 2)
            {
                Console.WriteLine("[Game1] Multiplayer: Level has only 1 chart - both will use chart 0");
                myChartIndex = 0;
                opponentChartIndex = 0;  // Both use chart 0 if only 1 exists
            }
        }
        
        float totalWidth = NoteRenderer.LANE_COUNT * NoteRenderer.LANE_WIDTH;  // 448px
        // Use smaller gap for Midlescroll (opponent arrows split around player)
        float gap = NoteRenderer.Midlescroll ? 50f : 200f;
        
        for (int i = 0; i < strumlineCount; i++)
        {
            float xOffset;
            if (strumlineCount == 1)
            {
                // Single strumline: center it
                xOffset = sw / 2f - totalWidth / 2f;
            }
            else
            {
                // Standard FNF layout:
                // Strumline 0 = PLAYER (right side) - this is what YOU control
                // Strumline 1 = OPPONENT (left side) - visual only, shows opponent's hits
                // In multiplayer: both players always play on the RIGHT side (strumline 0)
                // but load different charts (Player 1 = chart 0/BF, Player 2 = chart 1/Enemy)
                
                if (i == 0)
                {
                    // Player strumline (right side) - YOU control this
                    xOffset = NoteRenderer.Midlescroll
                        ? sw / 2f - totalWidth / 2f
                        : sw / 2f + gap / 2f;  // RIGHT
                }
                else if (i == 1)
                {
                    // Opponent strumline (left side) - visual only
                    xOffset = sw / 2f - totalWidth - gap / 2f;  // LEFT
                }
                else
                {
                    // Extra strumlines: same as opponent, repositioned in DrawNotes
                    xOffset = sw / 2f - totalWidth - gap / 2f;
                }
            }
            
            // Determine which chart to load for this strumline
            int chartIndex = i;
            if (duelLayout && strumlineCount >= 2)
            {
                if (i == 0)
                    chartIndex = myChartIndex;  // Player strumline uses MY chart
                else if (i == 1)
                    chartIndex = opponentChartIndex;  // Opponent strumline uses THEIR chart
            }
            else if (duelLayout && strumlineCount < 2)
            {
                chartIndex = 0;  // Fallback: use chart 0 if level only has 1
            }
            else if (_runEnemy && i <= 1)
            {
                chartIndex = 1 - i;  // Freeplay enemy mode (fork): you play the enemy chart
            }
            
            // Teams (fork): a song with notes on only one of the two playable charts gives both teams
            // that chart (the empty side gets the other side's notes).
            if (i <= 1) chartIndex = TeamsChartFor(chartIndex);
            var renderer = new NoteRenderer(_level, chartIndex, xOffset, GraphicsDevice, LevelNoteStyle()); // osu! beatmap skin (fork)
            renderer.SetSplashStyle(_noteSplashStyle);  // Apply splash style (null = programmatic default)
            renderer.SetParticleSplash(_particleNoteSplash);  // Apply particle splash (null if not using)
            
            // Set whether this strumline is controlled by local player
            // In single player: always strumline 0 (player on right)
            // In multiplayer: BOTH players always control strumline 0 (right side)
            //   - Player 1 plays chart 0 (BF) on strumline 0 (right)
            //   - Player 2 plays chart 1 (Enemy) on strumline 0 (right)
            //   - Strumline 1 (left) shows OPPONENT's hits for both players
            bool isMyStrumline = (i == 0) && !(_isMultiplayerMatch && _onlineSpectating);  // spectators watch
            renderer.SetIsPlayer(isMyStrumline);
            // Online side balancing (fork): set before the replay recorder copies it.
            if (_isMultiplayerMatch && i == 0 && !_onlineSpectating)
                renderer.ScoreMultiplier = NoteRenderer.SideScoreMultiplier(_level, chartIndex);
            
#if ANDROID
            // Set touch controls for Android input detection (player strumline only)
            if (i == 0 && _touchControls != null)
            {
                renderer.SetTouchControls(_touchControls);
            }
#endif
            
            // Player strumline (index 0): respect HidePlayerStrumline setting in single-player
            if (i == 0 && NoteRenderer.HidePlayerStrumline && !_isMultiplayerMatch)
            {
                renderer.Alpha = 0f;
            }
            // Opponent strumline (index 1): respect GhostTransparency & ShowOpponentNotes in multiplayer
            else if (i == 1)
            {
                if (_isMultiplayerMatch)
                {
                    var mp = Multiplayer.MultiplayerSettings.Instance;
                    renderer.Alpha = mp.ShowOpponentNotes ? mp.GhostTransparency / 100f : 0f;
                }
                else
                {
                    renderer.Alpha = !HideOpponentStrumline ? 0.5f : 0f;
                }
            }
            else if (i >= 2 && HideExtras)
            {
                renderer.Alpha = 0f;
            }
            // HUD editor strumline offsets: index 0 = player, 1 = opponent
            renderer.HudRole = i <= 1 ? i : -1;
            // Multiplayer: Disable botplay for opponent (index 1) — hits come from network
            if (duelLayout && i == 1) renderer.DisableBotplay = true;
            renderer.SetRenderTargetSize(rtW, rtH);
            ConfigureReplayForStrumline(i, chartIndex, renderer); // replays (fork): record or play back
            _noteRenderers.Add(renderer);
        }

        if (_isMultiplayerMatch)
            ConfigureRoomMatchRenderers(TeamsChartFor(myChartIndex));

        // Apply midlescroll split to opponent strumline if needed (online: also with extra strumlines,
        // strumline 1 keeps its fixed place there, see DrawNotes)
        if (NoteRenderer.Midlescroll && (strumlineCount == 2 || (duelLayout && strumlineCount > 2)))
        {
            float playerLeft  = sw / 2f - totalWidth / 2f;
            float playerRight = playerLeft + totalWidth;
            float halfWidth   = (NoteRenderer.LANE_COUNT / 2) * NoteRenderer.LANE_WIDTH;
            float oppLeftX    = playerLeft - halfWidth - gap;
            float oppRightX   = playerRight + gap;
            _noteRenderers[1].SetSplitXOffset(oppLeftX, oppRightX);
        }

        // Wire character animations for each strumline.
        // Callbacks read objs[j].StrumLine at call time so ObjectChartChange
        // events that reassign an object to a different strumline take effect immediately.
        _rendererCharStrumLine = new int[_noteRenderers.Count];
        ResetStageStrumlines();
        for (int i = 0; i < _noteRenderers.Count; i++)
        {
            int strumlineIdx = i;
            
            // Determine which character strumLine this renderer should animate
            // In single player: renderer i animates characters with strumLine i
            // In multiplayer: renderer 0 animates based on myChartIndex (Player 1=0, Player 2=1)
            //                 renderer 1 animates based on opponentChartIndex (for visual feedback)
            int characterStrumLineToAnimate = strumlineIdx;  // Default: same as renderer index
            if (duelLayout && strumlineCount >= 2)
            {
                if (i == 0)
                    characterStrumLineToAnimate = myChartIndex;  // Player renderer animates their chart's character
                else if (i == 1)
                    characterStrumLineToAnimate = opponentChartIndex;  // Opponent renderer animates opponent's character
            }
            else if (_runEnemy && i <= 1)
            {
                characterStrumLineToAnimate = 1 - i;  // Freeplay enemy mode (fork)
            }
            _rendererCharStrumLine[i] = characterStrumLineToAnimate; // stage strumlines float over these

            var hitRenderer = _noteRenderers[i];
            _noteRenderers[i].OnNoteHit = (lane, holdEnd) =>
            {
                // Animate ALL characters on this strumline (hidden ones too, so they already show the
                // right pose if a visibility/opacity event reveals them on the same note)
                // This allows multiple characters with the same strumline to sing together
                for (int j = 0; j < objs.Count; j++)
                {
                    if ((int)objs[j].StrumLine != characterStrumLineToAnimate) continue;
                    var anim = _objAnimators[j];
                    if (anim == null) continue;
                    
                    // Skip if an event animation of this note's moment controls this character (a later note interrupts it)
                    // Events have priority over note-driven animations
                    if (anim.BlocksNoteAt(hitRenderer.LastHitNoteTime))
                    {
                        Console.WriteLine($"[NoteHit] t={_songTime:F2} Obj {j} ({objs[j].Spr}) - skipping note anim (controlled by event)");
                        continue;
                    }
                    
                    // singAnim: lane+1 maps to the sing animation (1=left,2=down,3=up,4=right)
                    // Clamp to available animations — if character has fewer anims, use the last one
                    // Minimum 1 so we never send it to idle (anim 0) on a note hit
                    int singAnim = Math.Clamp(lane + 1, 1, Math.Max(1, anim.AnimCount - 1));
                    anim.SetAnimation(singAnim, setByEvent: false);
                    
                    if (holdEnd > 0f)
                    {
                        anim.HoldUntil = holdEnd;
                        float duration = holdEnd - _songTime;
                        Console.WriteLine($"[Hold] t={_songTime:F2} Obj {j} ({objs[j].Spr}) lane={lane} anim={singAnim} → hold until {holdEnd:F2} (dur={duration:F2}s)");
                    }
                }

                _events?.NotifyNoteHit(strumlineIdx);

                // In multiplayer, only MY strumline affects health
                // BOTH players always control strumline 0 (right side)
                bool isMyStrumline = (strumlineIdx == 0);

                if (isMyStrumline)
                {
                    // Online (fork): the health bar shows the score difference instead.
                    if (_healthBar != null && !_isMultiplayerMatch)
                        _healthBar.Health = MathHelper.Clamp(_healthBar.Health + 0.023f, 0f, 1f);
                    if (_isMultiplayerMatch) OnLocalNoteHit(lane, holdEnd);
                }
            };
        }

        // Wire miss penalty and judgement for player strumline
        // In multiplayer, BOTH players control strumline 0 (right side)
        int myStrumlineIndex = 0;  // Always 0 for both players
        
        if (_noteRenderers.Count > myStrumlineIndex)
        {
            _noteRenderers[myStrumlineIndex].OnNoteMiss = () =>
            {
                if (_healthBar != null && !_isMultiplayerMatch)
                    _healthBar.Health = MathHelper.Clamp(_healthBar.Health - 0.0475f, 0f, 1f);
                // Animate ALL characters on MY strumline (hidden ones too, see OnNoteHit)
                // In multiplayer:
                //   Player 1 (plays chart 0/BF) animates characters with strumLine: 0
                //   Player 2 (plays chart 1/Enemy) animates characters with strumLine: 1
                int myAnimStrumline = 0;  // Default for single player
                if (duelLayout)
                {
                    // In multiplayer, animate based on which chart I'm playing
                    // Player 1 (role=1) plays chart 0 (BF) → strumLine 0
                    // Player 2 (role=2) plays chart 1 (Enemy) → strumLine 1
                    myAnimStrumline = myChartIndex;  // Use the chart we determined earlier
                }
                else if (_runEnemy)
                {
                    myAnimStrumline = 1;  // Freeplay enemy mode (fork)
                }
                for (int j = 0; j < objs.Count; j++)
                {
                    if ((int)objs[j].StrumLine != myAnimStrumline) continue;
                    var anim = _objAnimators[j];
                    if (anim == null) continue;
                    
                    // Skip if an event animation of this moment controls it (a passed note is
                    // detected up to HIT_WINDOW after its time, hence the wider window)
                    if (anim.BlocksNoteAt(_songTime, 2f * NoteRenderer.HIT_WINDOW))
                    {
                        Console.WriteLine($"[NoteMiss] t={_songTime:F2} Obj {j} ({objs[j].Spr}) - skipping miss anim (controlled by event)");
                        continue;
                    }
                    
                    if (myAnimStrumline != 0)
                    {
                        // Not the player's strumline (e.g. playing as the enemy in multiplayer):
                        // FMM levels are single player, so these characters have no real miss
                        // animation (that slot is empty or used for something else). Play the
                        // note's sing animation instead, tinted dark purple.
                        int lane = _noteRenderers[myStrumlineIndex].LastMissLane;
                        int singAnim = Math.Clamp(lane + 1, 1, Math.Max(1, anim.AnimCount - 1));
                        anim.SetAnimation(singAnim, setByEvent: false);
                        anim.SetMissTint(NonPlayerMissTint);
                        continue;
                    }

                    int missAnim = anim.AnimCount > 5 ? 5 : anim.AnimCount - 1;
                    anim.SetAnimation(missAnim, setByEvent: false);
                }
            };

            // Wire judgement display - FNF style stacked popups (max 3)
            _noteRenderers[myStrumlineIndex].OnJudgement = j => 
            {
                // Add judgement popup using overlay
                var hud = HudSettings.Instance;
                if (_judgementOverlay != null && ShowCombo && hud.MasterHudVisible && hud.JudgementVisible && !EventHidesHud)
                {
                    _judgementOverlay.AddJudgement(j, new Point(hud.JudgementPosition.X, hud.JudgementPosition.Y), hud.JudgementScale);
                }
                
                // Online (fork): tell the room about the hit (lane from OnNoteHit).
                OnLocalJudgement(j);
            };

            // Wire combo changed event - spawn falling digits (FNF style)
            _noteRenderers[myStrumlineIndex].OnComboChanged = newCombo =>
            {
                if (newCombo <= 0) return;  // Don't show combo = 0
                if (_judgementOverlay == null) return;  // Safety check
                
                // Show combo using overlay
                var hud = HudSettings.Instance;
                if (ShowCombo && hud.MasterHudVisible && hud.ComboVisible && !EventHidesHud)
                {
                    _judgementOverlay.ShowCombo(newCombo, new Point(hud.ComboPosition.X, hud.ComboPosition.Y), hud.ComboScale);
                }
            };
            
            // Wire key press callback for multiplayer real-time animation
            _noteRenderers[myStrumlineIndex].OnKeyPress = lane =>
            {
                OnLocalKeyPress(lane);
            };
            
            // Wire note events — connect OnNotePressEvent for ALL strumlines
            // so action packs triggered by enemy notes also fire correctly
            foreach (var renderer in _noteRenderers)
            {
                var r = renderer;  // capture for lambda
                r.OnNotePressEvent = eventId => _events?.FireActionPack(eventId, r.LastHitNoteTime);
                r.OnNoteMissEvent  = eventId => _events?.FireActionPack(eventId);
            }

            // No skin-based fallback effects: note skins are visual only and any
            // damage/instakill/heal comes from the note's action packs (NoteSkins).
        }

        // ── Events ───────────────────────────────────────────────────────────
        if (_loadingScreen != null)
        {
            _loadingScreen.LoadingProgress = 0.93f;
            _loadingScreen.CurrentStep = "Initializing events...";
            RenderLoadingScreen();  // Force render
        }
        
        _events = new EventProcessor(_level, _objAnimators, texCache);
        
        // Store initial camera position for scrollFactor calculations
        if (_events.Camera != null)
        {
            _initialCameraPos = new Microsoft.Xna.Framework.Vector2(_events.Camera.X, _events.Camera.Y);
            Console.WriteLine($"[ScrollFactor] Initial camera position: ({_initialCameraPos.X}, {_initialCameraPos.Y})");
        }

        // Wire BarIconChange — swap health bar icons mid-song (respects Multiplayer Role 2 swap & mirror)
        _events.OnBarIconChange = (target, spriteName) =>
        {
            var newAnim = BuildIconAnimator(spriteName, texCache);
            if (newAnim == null || _healthBar == null) return;

            bool isRole2Mp = (_isMultiplayerMatch && _multiplayerPlayerRole == 2) || _runEnemy;
            bool isPlayerSlot = isRole2Mp ? (target == 0) : (target != 0);
            ApplyHealthBarIcon(isPlayerSlot, newAnim, isRole2Mp);
            Color slotColor = isPlayerSlot ? _healthBar.PlayerBarColor : _healthBar.OpponentBarColor;
            Console.WriteLine($"[BarIcon] {(isPlayerSlot ? "Player" : "Opponent")} icon changed to '{spriteName}' (flip={isRole2Mp}) - Color: {slotColor}");
        };

        // Wire PlayerHealth events
        _events.OnPlayerHealth = (delta, minHealth) =>
        {
            // Freeplay enemy mode (fork): the level's health effects target the player side; ignored.
            if (_healthBar != null && !_isMultiplayerMatch && !_runEnemy)
            {
                float newHealth = MathHelper.Clamp(_healthBar.Health + delta, minHealth, 1f);
                _healthBar.Health = newHealth;
            }
        };
        _events.OnPlayerHealthSet = (targetHealth, minHealth) =>
        {
            // op=set: set health to exact value, clamped to [minHealth, 1]
            if (_healthBar != null && !_isMultiplayerMatch && !_runEnemy)
                _healthBar.Health = MathHelper.Clamp(targetHealth, minHealth, 1f);
        };

        // Apply only instantaneous events at time=0 so objects start in the correct
        // state (visibility, opacity) without starting long transitions prematurely.
        // All events (including time=0) will fire again naturally when music starts.
        _events.ApplyInitialState();

        // ── Music ────────────────────────────────────────────────────────────
        if (_loadingScreen != null)
        {
            _loadingScreen.LoadingProgress = 0.96f;
            _loadingScreen.CurrentStep = "Loading music...";
            RenderLoadingScreen();  // Force render
        }
        
        if (_preloadedPlaytestPcm is { Pcm: var cachedPcm, SampleRate: var cachedSr, Channels: var cachedCh })
        {
            _musicInst?.Dispose();
            _music?.Dispose();
            _musicSampleRate = cachedSr;
            _musicChannels   = cachedCh;
            _musicPcm        = cachedPcm;
            _music           = OggLoader.CreateFromPcm(cachedPcm, cachedSr, cachedCh);
            _musicInst       = _music.CreateInstance();
            _musicInstStart  = 0f;
            ResetMusicSync();
            _musicInst.Volume = SongPlaybackVolume;
            _musicInst.IsLooped = false;
            ApplyRunRateToMusic(); // Freeplay song rate (fork)
            _musicDurationSeconds = _music.Duration.TotalSeconds > 0.1
                ? (float)_music.Duration.TotalSeconds
                : float.MaxValue;

            _audioRecoveryFailed = false;
            // Compressed audio bytes are never needed again after decoding (except editor playtest).
            // Retain _musicPcm on 64-bit so Android audio recovery works in multiplayer; only drop on 32-bit MP to save LOH.
#if ANDROID
            if (!_playtestingFromEditor)
                _level.MusicData = null;
            if (_isMultiplayerMatch && !_onlineSpectating && !Environment.Is64BitProcess)
                _musicPcm = null;
#else
            if (_isMultiplayerMatch)
            {
                _level.MusicData = null;
                if (!Environment.Is64BitProcess && !_onlineSpectating)
                    _musicPcm = null;
            }
#endif
        }
        else if (_level.MusicData != null)
        {
            _musicInst?.Dispose();
            _music?.Dispose();
            _musicPcm = null;

            // Decode .ogg with PCM data for seek capability
            _music = AudioDecoder.LoadWithPcm(_level.MusicData,
                out byte[] pcm, out int sampleRate, out int channels);
            
            _musicSampleRate = sampleRate;
            _musicChannels   = channels;
            _musicPcm        = pcm;
            
            _musicInst = _music.CreateInstance();
            _musicInstStart = 0f;
            ResetMusicSync();
            _musicInst.Volume = SongPlaybackVolume;
            _musicInst.IsLooped = false;
            ApplyRunRateToMusic(); // Freeplay song rate (fork)
            _musicDurationSeconds = _music.Duration.TotalSeconds > 0.1
                ? (float)_music.Duration.TotalSeconds
                : float.MaxValue;
            _audioRecoveryFailed = false;

            // Compressed audio bytes are never needed again after decoding (except editor playtest).
            // Retain _musicPcm on 64-bit so Android audio recovery works in multiplayer; only drop on 32-bit MP to save LOH.
#if ANDROID
            if (!_playtestingFromEditor)
                _level.MusicData = null;
            if (_isMultiplayerMatch && !_onlineSpectating && !Environment.Is64BitProcess)
                _musicPcm = null;
#else
            if (_isMultiplayerMatch)
            {
                _level.MusicData = null;
                if (!Environment.Is64BitProcess && !_onlineSpectating)
                    _musicPcm = null;
            }
#endif
        }
        else if (_musicPcm != null)
        {
            // Restart after MusicData was freed (Android frees it once decoded): rebuild from the
            // decoded PCM kept for seeking. Without this a restart played a disposed instance.
            _music = OggLoader.CreateFromPcm(_musicPcm, _musicSampleRate, _musicChannels);
            _musicInst = _music.CreateInstance();
            _musicInstStart = 0f;
            ResetMusicSync();
            _musicInst.Volume = SongPlaybackVolume;
            _musicInst.IsLooped = false;
            ApplyRunRateToMusic(); // Freeplay song rate (fork)
            _musicDurationSeconds = _music.Duration.TotalSeconds > 0.1
                ? (float)_music.Duration.TotalSeconds
                : float.MaxValue;
        }
        else
        {
            _musicDurationSeconds = float.MaxValue;
        }

        Window.Title = $"Funky Maker Player — {_level.LevelName}";

        // Calculate song end time (last note start + duration, already in seconds)
        _songEndTime = 0f;
        foreach (var sl in _level.Data.Strumlines)
            foreach (var lane in sl)
                foreach (var note in lane)
                    _songEndTime = Math.Max(_songEndTime,
                        _level.Data.ToSeconds(note.Start) + _level.Data.ToSeconds(note.Duration));
        if (_songEndTime < 1f)
            _songEndTime = _musicDurationSeconds < float.MaxValue ? _musicDurationSeconds : 30f;

        // Mark setup complete
        if (_loadingScreen != null)
        {
            _loadingScreen.LoadingProgress = 1.0f;
            _loadingScreen.CurrentStep = "Ready!";
            
            // Give time for the 100% progress to render before continuing
            // This ensures the user sees the complete progress bar
            System.Threading.Thread.Sleep(200);  // 200ms delay
        }
        
        SetupOsuExtras(); // osu! levels (fork, Game1.Osu.cs)
        _loadingComplete = true;
        }
        catch (Exception exSetup)
        {
            Console.WriteLine($"[SetupLevel] FATAL ERROR: {exSetup.Message}");
            Console.WriteLine($"[SetupLevel] Stack: {exSetup.StackTrace}");
            HandleSetupLevelFailure(new Exception($"Failed to setup level: {exSetup.Message}", exSetup));
        }
    }

    /// <summary>
    /// Assigns a health bar icon slot with its mirror flag and sampled bar color.
    /// </summary>
    private void ApplyHealthBarIcon(bool isPlayerSlot, SpriteAnimator? icon, bool flipX)
    {
        if (_healthBar == null) return;
        if (isPlayerSlot)
        {
            _healthBar.PlayerIcon = icon;
            _healthBar.FlipPlayerIcon = flipX;
            _healthBar.PlayerBarColor = SampleIconColor(icon) ?? new Color(0x66, 0xFF, 0x66);
        }
        else
        {
            _healthBar.OpponentIcon = icon;
            _healthBar.FlipOpponentIcon = flipX;
            _healthBar.OpponentBarColor = SampleIconColor(icon) ?? new Color(0xFF, 0x00, 0x00);
        }
    }

    /// <summary>
    /// Builds a SpriteAnimator for a health bar icon.
    /// anim 0 = normal/win pose, anim 1 = losing pose.
    /// Position is set to zero — DrawHUD positions it manually.
    /// </summary>
    private SpriteAnimator? BuildIconAnimator(string? iconName,
                                              Dictionary<string, List<Texture2D>> cache)
    {
        if (iconName == null) return null;
        if (!_level!.Sprites.TryGetValue(iconName, out var sprData)) return null;
        if (!cache.TryGetValue(iconName, out var textures)) return null;
        return new SpriteAnimator(iconName, sprData.Def, textures,
            Vector2.Zero, Vector2.One)
        {
            Bpm = _level.Data.EffectiveBpm,
        };
    }

    /// <summary>
    /// Samples the dominant (most saturated, non-transparent) color from an icon's texture.
    /// Returns null if no texture is available.
    /// </summary>
    private static Color? SampleIconColor(SpriteAnimator? anim)
    {
        if (anim == null) return null;
        var (tex, src) = anim.GetCurrentFrameTexture();
        if (tex == null || tex.Width == 0 || tex.Height == 0) return null;

        // Read a small sample of pixels from the center of the icon
        int sampleW = Math.Min(tex.Width,  32);
        int sampleH = Math.Min(tex.Height, 32);
        int offX    = (tex.Width  - sampleW) / 2;
        int offY    = (tex.Height - sampleH) / 2;

        var pixels = new Color[sampleW * sampleH];
        try { tex.GetData(0, new Rectangle(offX, offY, sampleW, sampleH), pixels, 0, pixels.Length); }
        catch { return null; }

        // Find the most saturated opaque pixel
        float bestSat = -1f;
        Color best    = Color.White;
        foreach (var p in pixels)
        {
            if (p.A < 128) continue;
            float r = p.R / 255f, g = p.G / 255f, b = p.B / 255f;
            float max = Math.Max(r, Math.Max(g, b));
            float min = Math.Min(r, Math.Min(g, b));
            float sat = max > 0 ? (max - min) / max : 0f;
            // Boost brightness so the bar color is vivid
            float brightness = (r + g + b) / 3f;
            float score = sat * 0.7f + brightness * 0.3f;
            if (score > bestSat) { bestSat = score; best = p; }
        }
        // Ensure the color is bright enough for the bar
        float scale = bestSat > 0.1f ? Math.Max(1f, 200f / Math.Max(1f, Math.Max(best.R, Math.Max(best.G, best.B)))) : 1f;
        return new Color(
            (int)Math.Min(255, best.R * scale),
            (int)Math.Min(255, best.G * scale),
            (int)Math.Min(255, best.B * scale));
    }

    /// <summary>
    /// Force render the loading screen immediately (used during SetupLevel to keep Android responsive)
    /// </summary>
    private void RenderLoadingScreen()
    {
        if (_isMultiplayerMatch)
            NetworkManager.Instance.PumpKeepAliveDuringLoad();
        if (_loadingScreen == null) return;
        
        try
        {
            // Force a complete frame cycle on ALL platforms to show loading progress
            int sw = _rtWidth;
            int sh = _rtHeight;
            
            // Update loading screen
            _loadingScreen.Update(0.016f, false, false, sw, sh);
            
            // Draw the loading screen
            EnsureRenderTarget();
            if (_renderTarget != null && _spriteBatch != null)
            {
                GraphicsDevice.SetRenderTarget(_renderTarget);
                GraphicsDevice.Clear(Color.Black);
                _spriteBatch.Begin(samplerState: SamplerState.PointClamp, transformMatrix: RtScaleMatrix);
                _loadingScreen.Draw(_spriteBatch, sw, sh);
                _spriteBatch.End();
                
                // Blit to screen (same sharp scaling as gameplay)
                var winBounds = Window.ClientBounds;
                var (dst, _) = ComputeLetterbox(winBounds.Width, winBounds.Height);
                PresentRenderTarget(dst);
                
                // Present immediately to show progress
                GraphicsDevice.Present();
            }
            
#if ANDROID
            // Give Android MINIMAL time to process events
            // Reduced to 1ms to let system respond faster (ANR happens after 5 seconds of blocking)
            System.Threading.Thread.Sleep(1);
#endif
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[RenderLoadingScreen] Error: {ex.Message}");
        }
    }
}
