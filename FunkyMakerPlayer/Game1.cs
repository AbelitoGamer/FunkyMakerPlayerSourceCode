using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
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

// ── Dark Mode P/Invoke ──────────────────────────────────────────────────────
internal static class DarkMode
{
    [DllImport("dwmapi.dll", PreserveSig = true)]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int attrValue, int attrSize);

    [DllImport("SDL2", CallingConvention = CallingConvention.Cdecl)]
    private static extern IntPtr SDL_GetWindowWMInfo(IntPtr window, ref SDL_SysWMinfo info);

    [StructLayout(LayoutKind.Sequential)]
    private struct SDL_version
    {
        public byte major;
        public byte minor;
        public byte patch;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct SDL_SysWMinfo
    {
        public SDL_version version;
        public SDL_SYSWM_TYPE subsystem;
        public IntPtr win_window; // HWND on Windows
    }

    private enum SDL_SYSWM_TYPE
    {
        SDL_SYSWM_UNKNOWN = 0,
        SDL_SYSWM_WINDOWS = 1,
    }

    private const int DWMWA_USE_IMMERSIVE_DARK_MODE_BEFORE_20H1 = 19;
    private const int DWMWA_USE_IMMERSIVE_DARK_MODE = 20;

    /// <summary>
    /// Enables Windows 10/11 dark mode for the window title bar.
    /// </summary>
    public static void EnableDarkMode(IntPtr sdlWindowHandle)
    {
        if (!OperatingSystem.IsWindows())
            return;

        try
        {
            // Get native Windows HWND from SDL window
            var wmInfo = new SDL_SysWMinfo();
            wmInfo.version.major = 2;
            wmInfo.version.minor = 0;
            wmInfo.version.patch = 0;

            SDL_GetWindowWMInfo(sdlWindowHandle, ref wmInfo);

            if (wmInfo.subsystem == SDL_SYSWM_TYPE.SDL_SYSWM_WINDOWS && wmInfo.win_window != IntPtr.Zero)
            {
                int darkMode = 1;
                // Try Windows 11/10 20H1+ attribute first
                int result = DwmSetWindowAttribute(wmInfo.win_window, DWMWA_USE_IMMERSIVE_DARK_MODE, ref darkMode, sizeof(int));
                // Fallback to older Windows 10 builds
                if (result != 0)
                    DwmSetWindowAttribute(wmInfo.win_window, DWMWA_USE_IMMERSIVE_DARK_MODE_BEFORE_20H1, ref darkMode, sizeof(int));
                
                Console.WriteLine($"[DarkMode] Applied dark mode to window (result={result})");
            }
            else
            {
                Console.WriteLine($"[DarkMode] Failed to get Windows HWND from SDL");
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[DarkMode] Error: {ex.Message}");
        }
    }
}

public partial class Game1 : Game
{
    // ── Managers ──────────────────────────────────────────────────────────────
    public CameraManager? _cameraManager;  // Public for SettingsManager
    private SettingsManager? _settingsManager;
    // private AudioManager? _audioManager;  // DISABLED - causando bugs
    // TODO: GameStateManager disponible en docs/Managers_Reference/ cuando esté listo
    // private GameStateManager? _stateManager;

    // ── Graphics ─────────────────────────────────────────────────────────────
    public GraphicsDeviceManager _graphics;  // Public for SettingsManager
    private SpriteBatch           _spriteBatch = null!;
    private SpriteFont?           _font;
    private Texture2D?            _pixel;
    public bool                  _isFullscreen = false;  // Public for SettingsManager
    
#if ANDROID
    // ── Touch Controls (Android only) ────────────────────────────────────────
    private TouchControls? _touchControls;
#endif

    // ── Letterbox render target ───────────────────────────────────────────────
    // The game always renders to a fixed-size target (the level's native resolution),
    // then that target is scaled to fit the window with black bars — exactly like
    // GameMaker / Funky Maker does on different screen sizes.
    private RenderTarget2D? _renderTarget;
    private int             _rtWidth  = 1280;
    private int             _rtHeight = 720;

    // NOTA: Camera zoom ahora manejado por CameraManager
    // (eliminados: _camScaleOverride, _camZoom, _lastBeat, ZOOM_BEAT_AMOUNT, ZOOM_LERP_SPEED)

    // ── Level ────────────────────────────────────────────────────────────────
    public FnmmLevel?      _level;  // Public for SettingsManager
    private List<NoteRenderer> _noteRenderers = new();  // Support multiple strumlines
    public EventProcessor? _events;  // Public for SettingsManager
    private Microsoft.Xna.Framework.Vector2 _initialCameraPos = Microsoft.Xna.Framework.Vector2.Zero;  // Initial camera position for scrollFactor
    
    // ── MonoGame.Extended Camera (Beta Feature) ──────────────────────────────
    public static bool UseExtendedCamera = false;  // Toggle for beta feature

    // ── Sprite Texture Cache ────────────────────────────────────────────────
    // Caches converted textures to avoid reloading on song restart
    private class SpriteTextureCache
    {
        public List<Texture2D> Textures = new List<Texture2D>();
        public List<AtlasPacker.FrameRect>? AtlasFrameRects = null;
        public int SplitOffset = -1;
        public int ChunkFrames = 0;
        public int FrameWidth = 0;
        public int TextureIndex = -1;
        public float DownscaleCompensation = 1.0f;
    }
    
    // spriteName → (frameIndex → cache)
    private Dictionary<string, Dictionary<int, SpriteTextureCache>>? _spriteTextureCache = null;

    // ── Scene objects: dynamic array of animators indexed by object index ──
    // Key = object index, matching scene.objects[i]
    private SpriteAnimator?[] _objAnimators = Array.Empty<SpriteAnimator?>();

    // ── Note style (custom arrow sprites) ────────────────────────────────────
    private NoteStyle? _noteStyle;
    private NoteSplashStyle? _noteSplashStyle;  // null = use programmatic default
    private ParticleNoteSplash? _particleNoteSplash;  // Particle-based notesplash (MonoGame.Extended)

    // ── Freeplay menu ─────────────────────────────────────────────────────────
    private FreeplayMenu? _freeplay;
    private bool          _inFreeplay = false;
    private Texture2D?    _folderIcon = null;  // Folder icon for empty state
    
    #if ANDROID
    private ModsHelpScreen? _modsHelpScreen;  // Help screen for Android mods setup
    #endif
    private FontRenderer? _fontRenderer = null;  // TrueType font renderer for crisp UI

    // ── Main Settings menu (accessible from main menu) ───────────────────────
    private MainSettingsMenu? _mainSettingsMenu = null;
    private bool              _inMainSettings = false;
    private HudEditor?        _hudEditor = null;
    private bool              _inHudEditor = false;

    // ── Multiplayer menu ─────────────────────────────────────────────────────
    private Multiplayer.MultiplayerMenu? _multiplayerMenu;
    private bool          _inMultiplayer = false;
    
    // ── Multiplayer match state ──────────────────────────────────────────────
    private bool _isMultiplayerMatch = false;  // true if this is a PvP match
    private int _multiplayerPlayerRole = 0;  // 1 = Player 1 (plays chart 1 - Boyfriend), 2 = Player 2 (plays chart 0 - Enemy)
    private long _multiplayerStartTimestamp = 0;  // Server timestamp (milliseconds) for synchronized start
    private bool _countdownSignalReceived = false;  // true when server sends CountdownStart
    private string? _opponentName = null;
    private int _opponentScore = 0;
    private float _opponentHealth = 0.5f;
    private bool _crashLogged = false;  // Prevent spam logging the same crash
    private bool? _opponentReadyState = null;  // Store opponent ready state received before LoadingScreen exists
    private float _stateUpdateTimer = 0f;  // Timer for sending state updates to opponent (every 200ms)
    private MultiplayerResultsScreen? _resultsScreen = null;  // Results screen shown after match ends
    
    // ── Health Bar (UI component) ────────────────────────────────────────────
    private UI.HealthBar? _healthBar;
    
    // ── Timer Bar (UI component) ─────────────────────────────────────────────
    private UI.TimerBar? _timerBar;
    
    // ── Judgement Overlay (UI component) ─────────────────────────────────────
    private UI.JudgementOverlay? _judgementOverlay;

    // ── Loading state ────────────────────────────────────────────────────────
    private enum LoadState { Idle, Loading, Finalizing, Done }
    private LoadState   _loadState    = LoadState.Idle;
    private FnmmLevel?  _pendingLevel = null;  // loaded in background, applied on main thread
    private string?     _pendingPath  = null;

    // ── Audio ────────────────────────────────────────────────────────────────
    private SoundEffect?         _music;
    private SoundEffectInstance? _musicInst;
    private bool                 _audioDeviceReset = false; // set when audio device changes
    // Raw PCM for audio seek (timeskip). Null for WAV files (no seek support).
    private byte[]? _musicPcm;
    private int     _musicSampleRate;
    private int     _musicChannels;
    
    // ── Timing ───────────────────────────────────────────────────────────────
    private float _songTime  = 0f;
    private float _songBeats = 0f;
    private bool  _started   = false;
    private bool  _paused    = false;
    private float _discordUpdateTimer = 0f;  // throttle Discord RPC updates
    private bool  _parallaxLoggedOnce = false;  // prevent parallax debug spam
    
    // High-precision timing to avoid desync when window is moved
    private Stopwatch _songStopwatch = new();
    private double _pausedTime    = 0.0;  // Total accumulated pause duration
    private double _pauseStarted  = 0.0;  // When the current pause started

    // Song end
    private float _songEndTime = float.MaxValue;
    private float _songEndDelay = 0f;  // countdown after song ends before transition starts

    // ── Input ────────────────────────────────────────────────────────────────
    private KeyboardState _prevKb;
    private double        _lastDt = 1.0 / 60.0;
    private Matrix        _lastCamMatrix = Matrix.Identity;

    // ── Key binding mode ─────────────────────────────────────────────────────
    private KeyBindingOverlay _keyBindOverlay = new();

    // ── Pause menu ───────────────────────────────────────────────────────────
    private bool _inPauseMenu = false;
    private PauseMenu? _pauseMenu = null;
    
    // ── Settings menu (managed by SettingsManager) ──────────────────────────
    // _inSettings → _settingsManager.IsActive
    // _settingsSelection → _settingsManager.Selection
    // Settings item count delegated to SettingsManager
    private int SettingsItemCount => _settingsManager?.GetItemCount() ?? 30;
    private bool _pausedByTimeskip = false;  // true if PauseSong was called by timeskip scrubbing

    // ── UI Menus ──────────────────────────────────────────────────────────────
    private ArrowsMenu? _arrowsMenu = null;
    private NoteSplashMenu? _splashMenu = null;
    private CountdownMenu? _countdownMenu = null;
    private FontMenu? _fontMenu = null;

    // ── HUD options ───────────────────────────────────────────────────────────
    public static bool  ShowTimer       = true;   // song timer bar above strumline
    public static bool  ShowCombo       = true;   // combo counter popup (on/off)
    public static bool  TimeskipEnabled = true;   // Tab to skip forward, Shift+Tab backward
    public static float TimeskipSeconds = 10f;    // seconds per timeskip step
    public static bool  ShowDebugInfo   = false;  // Level debug: BPM, cam, events (off by default)
#if ANDROID
    public static bool  ShowFpsCounter  = true;   // FPS counter (enabled by default on Android)
#else
    public static bool  ShowFpsCounter  = false;  // FPS counter (off by default on PC)
#endif
    
    // ── Timer Bar Options (V-Slice style with Legacy support) ─────────────────
    public static string TimerPreset    = "Custom HUD";  // Legacy, Custom HUD, Top, Left, Middle, Right, Strumline
    public static string TimerTextMode  = "Time Left";   // Time Left, Time Elapsed, Song Name, None
    public static bool   TimerForceTop  = false;         // Ignore downscroll (only for Top/Left/Middle/Right/Strumline presets)
    
    // ── Color Grading Shader (FNF Erect-Style - Shadow Tint Only) ────────────
    private Effect?     _colorGradeShader = null;
    public static bool  ColorGradeEnabled      = false;  // OFF by default
    // HSB adjustments (Hue, Saturation, Brightness)
    public static float ColorGradeHue          = -0.05f; // -5% hue shift (cool tone)
    public static float ColorGradeSaturation   = 0.80f;  // 80% saturation (desaturate)
    public static float ColorGradeBrightness   = -0.20f; // -20% brightness (darker)
    // Shadow-only approach: NO saturation/contrast, ONLY cool shadow tint
    public static float ColorGradeContrast      = 1.00f;  // OFF (not used)
    public static float ColorGradeShadowAmount  = 0.35f;  // 35% shadow tint (noticeable)
    public static float ColorGradeShadowTintR   = 0.65f;  // Cool blue/purple shadows
    public static float ColorGradeShadowTintG   = 0.55f;  // Less green
    public static float ColorGradeShadowTintB   = 0.90f;  // Blue tint
    public static bool  HideExtraStrumlines = false;  // hide strumlines beyond player+opponent
    public static bool  HideOpponentStrumline = false; // hide opponent strumline (index 1)
    // Timer color presets: 0=white, 1=cyan, 2=pink, 3=yellow, 4=green, 5=custom
    public static int   TimerColorPreset = 0;
    public static Color TimerColorCustom = new Color(0xE0, 0xE0, 0xFF);
    // Music volume: 0.0 to 1.0
    public static float MusicVolume = 0.85f;
    // Bilinear filtering for sprites (smooth vs pixel-perfect)
    public static bool   BilinearFiltering = true;
    // ── Android Touch Controls Settings ───────────────────────────────────────
    // Hide touch hitbox colors (android only)
    public static bool   HideTouchHitboxColors = false;
    // Note style name — "Default" or a folder name in mods/notestyles/
    public static string NoteStyleName     = "Default";
    // NoteSplash style name — "Default" (programmatic) or a folder name in mods/notesplashes/
    public static string NoteSplashStyleName = "Default";
    // Icon mode: 0 = Fixed (FNF base - icons move with health %), 1 = Dynamic (icons stay at ends)
    public static int IconMode = 0;
    // Selected countdown pack name from mods/countdowns/[name]/
    public static string SelectedCountdown = "default";

    // ── Notes/arrows fade (controlled by GUI event HudVisible) ───────────────
    // Smoothly fades notes in/out when HudVisible changes.
    private float _notesAlpha = 1.0f;  // current alpha applied to all NoteRenderers
    private const float NOTES_FADE_SPEED = 4.0f;  // units/sec (reaches ~98% in ~1s)

    // ── Loading screen ───────────────────────────────────────────────────────
    private LoadingScreen? _loadingScreen;
    private bool           _loadingComplete = false;

    // ── Game state ────────────────────────────────────────────────────────────
    private GameOverOverlay? _gameOverOverlay;

    // ── Countdown ────────────────────────────────────────────────────────────
    private UI.CountdownOverlay? _countdownOverlay;

    // ── Hit judgement display ─────────────────────────────────────────────────
    private string? _judgementText  = null;
    private float   _judgementTimer = 0f;
    private Color   _judgementColor = Color.White;
    private const float JUDGEMENT_SHOW = 0.6f;
    
    // ── Combo display (FNF style with falling numbers) ──────────────────────
    private int     _lastCombo      = 0;
    private float   _comboTimer     = 0f;
    private const float COMBO_SHOW  = 1.5f;  // Show for 1.5 seconds after combo increase
    
    // ── Falling combo digits system (extracted to UI/JudgementOverlay.cs) ────
    // ── Judgement popups system (extracted to UI/JudgementOverlay.cs) ────────

    // ── FNMM path ────────────────────────────────────────────────────────────
    public static string FnmmPath = "";

    // ── Error message ────────────────────────────────────────────────────────
    private string? _errorMsg;

    // ── Song transition (AnkushCat cascade) ──────────────────────────────────
    private SongTransition? _songTransition;
    private bool            _transitionToFreeplay = false; // go to freeplay after transition
    private Texture2D?      _catTex;  // AnkushCat texture shared by LoadingScreen and SongTransition

    // ────────────────────────────────────────────────────────────────────────
    // ── Error Handling ───────────────────────────────────────────────────────
    // ────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Handles errors that occur during gameplay (Update/Draw).
    /// Generates a crash report and exits gracefully.
    /// </summary>
    private void HandleGameplayError(Exception ex, string context)
    {
        try
        {
            // Only log once to prevent spam
            if (_crashLogged)
            {
                return;
            }
            _crashLogged = true;
            
            // Log the crash
            string crashReport = CrashLogger.LogException(ex, $"Gameplay error in {context}");
            
            // Write to console
            Console.Error.WriteLine($"\n[GAMEPLAY ERROR in {context}]");
            Console.Error.WriteLine(ex.ToString());
            
#if ANDROID
            // Android: Show crash dialog that allows copying the error
            var activity = (Android.App.Activity)Microsoft.Xna.Framework.Game.Activity;
            if (activity != null)
            {
                Platforms.Android.CrashDialog.Show(activity, crashReport);
            }
            else
            {
                // Fallback if activity is null
                Exit();
            }
#else
            // Desktop: Write to crash.log file
            try
            {
                string crashPath = Path.Combine(AppContext.BaseDirectory, "crash.log");
                File.WriteAllText(crashPath, crashReport);
            }
            catch { /* Ignore if file write fails */ }
            
            // Try to show error message in-game if possible
            _errorMsg = $"Gameplay error in {context}: {ex.Message}";
            
            // Stop music to avoid audio loops
            _musicInst?.Stop();
            
            // Exit the game after showing error
            System.Threading.Tasks.Task.Delay(1000).ContinueWith(_ => Exit());
#endif
        }
        catch
        {
            // If error handling itself fails, just exit immediately
            Exit();
        }
    }

    // ────────────────────────────────────────────────────────────────────────
    public Game1()
    {
        _graphics = new GraphicsDeviceManager(this)
        {
#if ANDROID
            // Android: use full screen resolution (detected at runtime in Initialize)
            IsFullScreen = true,
            SupportedOrientations = DisplayOrientation.Portrait | DisplayOrientation.LandscapeLeft | DisplayOrientation.LandscapeRight,
#else
            PreferredBackBufferWidth  = 1280,
            PreferredBackBufferHeight = 720,
            HardwareModeSwitch = false,             // borderless windowed fullscreen — allows screen capture
#endif
            SynchronizeWithVerticalRetrace = true,  // vsync on by default
            // Force HiDef profile to support larger textures (up to 16384px vs 2048px in Reach)
            // Required for Android/PC to handle large spritesheets
            GraphicsProfile = GraphicsProfile.HiDef,
        };
        Content.RootDirectory = "Content";
        IsMouseVisible  = true;
        IsFixedTimeStep = false;  // uncapped — runs as fast as the PC allows
        Window.Title    = "Funky Maker Player v" + System.Reflection.Assembly.GetExecutingAssembly().GetName().Version?.ToString(3);
    }
    
    /// <summary>
    /// Starts a multiplayer PvP match (called from MultiplayerMenu).
    /// Does NOT create a new Game instance - changes current instance to multiplayer mode.
    /// </summary>
    public void StartMultiplayerMatch(string levelPath, string opponentName, int playerRole, long startTimestamp)
    {
        Console.WriteLine($"[Game1] Starting multiplayer match:");
        Console.WriteLine($"  Level: {levelPath}");
        Console.WriteLine($"  Opponent: {opponentName}");
        Console.WriteLine($"  Player Role: {playerRole}");
        Console.WriteLine($"  Start Timestamp: {startTimestamp}");
        
        _isMultiplayerMatch = true;
        _opponentName = opponentName;
        _multiplayerPlayerRole = playerRole;
        _multiplayerStartTimestamp = startTimestamp;
        _countdownSignalReceived = false;  // Reset countdown signal for new match
        _opponentReadyState = null;  // Reset stored opponent ready state
        
        // Disable autoplay in multiplayer - players must play manually
        NoteRenderer.Autoplay = false;
        Console.WriteLine("[Game1] Autoplay disabled for multiplayer match");
        
        // Exit multiplayer menu
        _inMultiplayer = false;
        
        // Load the level (same as single player)
        StartLoadingLevel(levelPath);
    }

    /// <summary>
    /// Resets window title to default (called when returning to main menu).
    /// </summary>
    private void ResetWindowTitle()
    {
        Window.Title = "Funky Maker Player v" + System.Reflection.Assembly.GetExecutingAssembly().GetName().Version?.ToString(3);
    }
    
    /// <summary>
    /// Enters freeplay mode and updates Discord RPC
    /// </summary>
    private void EnterFreeplay()
    {
        _inFreeplay = true;
        DiscordRpc.SetBrowsingMusic();
        ResetWindowTitle();
        _freeplay?.Refresh();
    }

    protected override void Initialize()
    {
#if ANDROID
        // Android: Adapt to device screen resolution
        var displayMode = GraphicsAdapter.DefaultAdapter.CurrentDisplayMode;
        _graphics.PreferredBackBufferWidth = displayMode.Width;
        _graphics.PreferredBackBufferHeight = displayMode.Height;
        _graphics.IsFullScreen = true;
        _graphics.ApplyChanges();
        
        // Hide mouse cursor on Android (touch only)
        IsMouseVisible = false;
        
        Console.WriteLine($"[Android] Screen resolution: {displayMode.Width}x{displayMode.Height}");
        
        // Initialize crash logger with Android dialog callback
        CrashLogger.Initialize(crashReport =>
        {
            var activity = Microsoft.Xna.Framework.Game.Activity as Platforms.Android.MainActivity;
            if (activity != null)
            {
                Platforms.Android.CrashDialog.Show(activity, crashReport);
            }
        });
        
        // Initialize mod directories and scan for content
        string scanSummary = PlatformPaths.InitializeModDirectories();
        
        // Show scan result in Toast notification
        var activity = Microsoft.Xna.Framework.Game.Activity as Platforms.Android.MainActivity;
        if (activity != null)
        {
            activity.RunOnUiThread(() =>
            {
                var toast = global::Android.Widget.Toast.MakeText(
                    activity,
                    scanSummary,
                    global::Android.Widget.ToastLength.Long
                );
                toast?.Show();
            });
        }
#else
        // Enable Game Mode hints for better recording software detection
        GameModeHelper.EnableGameMode();
        
        Window.AllowUserResizing = true;
        try { SDL_EventState(0x1000 /*SDL_DROPFILE*/, 1 /*SDL_ENABLE*/); } catch { }
        Window.FileDrop += OnFileDrop;
        
        // Initialize crash logger (desktop writes to file)
        CrashLogger.Initialize();
#endif

        // Hook audio device change
        GraphicsDevice.DeviceReset += (_, _) => _audioDeviceReset = true;

        // Discord Rich Presence
        DiscordRpc.Initialize();
        Exiting += (_, _) => DiscordRpc.Shutdown();

        // Initialize managers
        _cameraManager = new CameraManager();
        _settingsManager = new SettingsManager();
        
        // NOTE: UI menus initialized in LoadContent() after _pixel and _font are created
        
        // Hook audio device reset
        GraphicsDevice.DeviceReset += (_, _) => _audioDeviceReset = true;
        
        // AudioManager disabled - causing bugs (music not stopping, etc.)
        // _stateManager = new GameStateManager();  // TODO: Implementar cuando esté listo

        base.Initialize();
    }

    // SDL2 P/Invoke — MonoGame DesktopGL ships SDL2.dll
    [DllImport("SDL2", CallingConvention = CallingConvention.Cdecl)]
    private static extern byte SDL_EventState(uint type, int state);

    private void OnFileDrop(object? sender, FileDropEventArgs e)
    {
        foreach (var file in e.Files)
        {
            if (file.EndsWith(".FNMM", StringComparison.OrdinalIgnoreCase) ||
                file.EndsWith(".rar",  StringComparison.OrdinalIgnoreCase))
            {
                string imported = ImportLevelFile(file);
                StartLoadingLevel(imported);
                break;
            }
            // Psych Engine mod folder (drag the mod folder itself)
            if (Directory.Exists(file))
            {
                StartLoadingLevel(file);
                break;
            }
        }
    }

    /// <summary>
    /// Copies a dropped .FNMM or .rar file into the levels folder (if it isn't already there).
    /// Returns the path to use for loading — either the copy or the original if copy fails.
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

            // Refresh freeplay list so the new level appears immediately
            _freeplay?.Refresh();

            return destPath;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[Import] Copy failed, loading from original: {ex.Message}");
            return sourcePath;
        }
    }

    /// <summary>
    /// Begins async loading of a .FNMM file.
    /// Phase 1 (background thread): parse file, decompress, read JSONs/audio bytes.
    /// Phase 2 (main thread, next Update): create GPU textures, init renderers.
    /// The loading screen is shown immediately and spins while loading.
    /// </summary>
    private void StartLoadingLevel(string path)
    {
        // Stop current song immediately
        _musicInst?.Stop();
        _started      = false;
        _errorMsg     = null;
        _loadState    = LoadState.Loading;
        _pendingPath  = path;
        _inFreeplay   = false;
        _parallaxLoggedOnce = false;  // Reset parallax debug log for new level
        _crashLogged  = false;  // Reset crash log flag for new level
        // Reset all overlay states so they don't persist into the new level
        _keyBindOverlay.Reset();
        _settingsManager?.Reset();  // Resets IsActive and Selection
        _inPauseMenu  = false;
        if (_arrowsMenu != null) _arrowsMenu.IsActive = false;
        if (_splashMenu != null) _splashMenu.IsActive = false;

        string levelName = System.IO.Path.GetFileNameWithoutExtension(path);
        DiscordRpc.SetLoading(levelName);
        FnmmPath    = path;

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

        // Load file on background thread (no GPU calls here)
        System.Threading.Tasks.Task.Run(() =>
        {
            try
            {
                // Pass progress callback to loading screen
                _pendingLevel = FnmmLoader.Load(path, (progress, step) =>
                {
                    // Update main window loading screen
                    _loadingScreen.LoadingProgress = progress;
                    _loadingScreen.CurrentStep = step;
                });
                
                // Clear sprite texture cache when loading a new level (not a restart)
                _spriteTextureCache = null;
                Console.WriteLine("[Cache] Cleared sprite texture cache for new level");
                
                _loadingComplete = true;
                _loadState       = LoadState.Finalizing;
            }
            catch (Exception ex)
            {
                _errorMsg        = ex.Message;
                _loadingComplete = true;
                _loadState       = LoadState.Done;
                Console.Error.WriteLine($"[FunkyMakerPlayer] {ex}");
            }
        });
    }
    protected override void LoadContent()
    {
        // Enable dark mode for window title bar (Windows 10/11)
        DarkMode.EnableDarkMode(Window.Handle);

        _spriteBatch = new SpriteBatch(GraphicsDevice);

        _pixel = new Texture2D(GraphicsDevice, 1, 1);
        _pixel.SetData(new[] { Color.White });

        _font = Content.Load<SpriteFont>("DefaultFont");
        
        // Initialize main settings menu
        _mainSettingsMenu = new MainSettingsMenu(_pixel, _font);
        
        // Initialize Extended Camera (for parallax scrollFactor support)
        _cameraManager?.InitializeExtendedCamera(GraphicsDevice);
        Console.WriteLine("[Game1] Extended camera initialized");
        
        // Load color grading shader (try/catch for compatibility)
        try
        {
            Console.WriteLine("[ColorGrade] Attempting to load shader...");
            _colorGradeShader = Content.Load<Effect>("ColorGrade");
            Console.WriteLine("[ColorGrade] Shader loaded successfully!");
            if (_colorGradeShader != null)
            {
                var paramNames = string.Join(", ", _colorGradeShader.Parameters.Select(p => p.Name));
                Console.WriteLine($"[ColorGrade] Parameters available: {paramNames}");
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[ColorGrade] FAILED to load shader!");
            Console.WriteLine($"[ColorGrade] Error: {ex.Message}");
            Console.WriteLine($"[ColorGrade] Stack: {ex.StackTrace}");
            _colorGradeShader = null;
            ColorGradeEnabled = false;  // Disable if shader can't load
        }

        // Create initial render target (landscape 1280×720 by default)
        _renderTarget = new RenderTarget2D(GraphicsDevice, _rtWidth, _rtHeight,
            false, SurfaceFormat.Color, DepthFormat.None,
            0, RenderTargetUsage.DiscardContents);

        // Load note style assets from the Assets folder next to the project
        string? assetsRoot = FindAssetsRoot();
        if (assetsRoot != null)
        {
            // Note: NoteStyle is loaded AFTER Settings.Load so NoteStyleName is correct
            // Load AnkushCat texture for loading screen and song transitions
            string catPath = System.IO.Path.Combine(assetsRoot, "LoadingScreen", "AnkushCat.png");
            if (File.Exists(catPath))
            {
                _catTex = LoadTextureViaImageSharp(GraphicsDevice, File.ReadAllBytes(catPath));
            }
            // Load countdown sprites using CountdownOverlay
            _countdownOverlay = new UI.CountdownOverlay(GraphicsDevice, _spriteBatch, _font);
            _countdownOverlay.LoadCountdownAssets(SelectedCountdown, assetsRoot);
        }

        // Ensure levels folder exists
        string levelsFolder = FreeplayMenu.GetDefaultLevelsFolder();
        if (!Directory.Exists(levelsFolder))
            Directory.CreateDirectory(levelsFolder);

        // Load folder icon for empty state
        if (assetsRoot != null)
        {
            string folderIconPath = System.IO.Path.Combine(assetsRoot, "images", "foldericon.png");
            if (File.Exists(folderIconPath))
            {
                _folderIcon = LoadTextureViaImageSharp(GraphicsDevice, File.ReadAllBytes(folderIconPath));
                Console.WriteLine($"[FreeplayMenu] Loaded folder icon from: {folderIconPath}");
            }
            else
            {
                Console.WriteLine($"[FreeplayMenu] Folder icon not found at: {folderIconPath}");
            }
            
            // Load TrueType font for crisp UI rendering
            // Priority: mods/fonts/main.otf > mods/fonts/main.ttf > Assets/Font/MilkyNice.otf
            string? fontPath = null;
            
            // Check mods/fonts/ first using PlatformPaths (works on Android publicmods)
            string modsFontsFolder = PlatformPaths.GetFontsPath();
            string[] customFontPaths = {
                Path.Combine(modsFontsFolder, "main.otf"),
                Path.Combine(modsFontsFolder, "main.ttf"),
                Path.Combine(modsFontsFolder, "MilkyNice.otf"),
                Path.Combine(modsFontsFolder, "MilkyNice.ttf")
            };
            
            foreach (var customPath in customFontPaths)
            {
                if (File.Exists(customPath))
                {
                    fontPath = customPath;
                    Console.WriteLine($"[FontRenderer] Found custom font: {customPath}");
                    break;
                }
            }
            
            // Fallback to default font
            if (fontPath == null)
            {
                fontPath = System.IO.Path.Combine(assetsRoot, "Font", "MilkyNice.otf");
            }
            
            if (File.Exists(fontPath))
            {
                try
                {
                    _fontRenderer = new FontRenderer(GraphicsDevice, fontPath);
                    Console.WriteLine($"[FontRenderer] Loaded TrueType font from: {fontPath}");
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"[FontRenderer] Failed to load font: {ex.Message}");
                }
            }
            else
            {
                Console.WriteLine($"[FontRenderer] No font file found at: {fontPath}");
            }
        }

        // Init freeplay menu
        _freeplay = new FreeplayMenu(_pixel, _font, levelsFolder, _folderIcon, _fontRenderer);
        
        #if ANDROID
        // Init mods help screen
        _modsHelpScreen = new ModsHelpScreen(_fontRenderer, _pixel);
        #endif
        
        // Init pause menu
        _pauseMenu = new PauseMenu(_pixel, _font, _fontRenderer);
        
        // Init UI menus (arrows and note splash selectors)
        _arrowsMenu = new UI.ArrowsMenu(_pixel, _font, _fontRenderer);
        _splashMenu = new UI.NoteSplashMenu(_pixel, _font, GraphicsDevice, _fontRenderer);
        _countdownMenu = new UI.CountdownMenu(_pixel, _font, _fontRenderer);
        _fontMenu = new UI.FontMenu(_pixel, _font, _fontRenderer);
        
        // Init UI components (health bar, game over overlay, timer bar, judgement overlay, etc.)
        _healthBar = new UI.HealthBar(_pixel, _spriteBatch);
        _gameOverOverlay = new UI.GameOverOverlay(_pixel, _spriteBatch, _font);
        _timerBar = new UI.TimerBar(_pixel, _spriteBatch, _font);
        _judgementOverlay = new UI.JudgementOverlay(_spriteBatch, _font);
        
        // Blazor only: Wire file picker callback
        #if BLAZORGL
        _freeplay.OnFilePickerRequested = () => RequestFilePicker();
        #endif

        // Init multiplayer menu (for accessing multiplayer lobby)
        _multiplayerMenu = new Multiplayer.MultiplayerMenu();
        _multiplayerMenu.SetGame(this);  // Pass reference to this Game1 instance
        
#if ANDROID
        // Initialize touch controls for Android
        _touchControls = new TouchControls();
        _touchControls.Initialize(GraphicsDevice, 
            GraphicsDevice.PresentationParameters.BackBufferWidth,
            GraphicsDevice.PresentationParameters.BackBufferHeight);
        Console.WriteLine("[TouchControls] Initialized for Android");
        
        // Initialize touch controls for multiplayer menu
        _multiplayerMenu.InitializeTouchControls(GraphicsDevice,
            GraphicsDevice.PresentationParameters.BackBufferWidth,
            GraphicsDevice.PresentationParameters.BackBufferHeight);
#endif

        // Load persisted settings — must happen before NoteStyle so NoteStyleName is set
        Settings.Load(this);

        // Now load NoteStyle and NoteSplashStyle with the correct names from settings
        string? assetsRootForStyle = FindAssetsRoot();
        if (assetsRootForStyle != null)
            _noteStyle = NoteStyle.LoadByName(NoteStyleName, assetsRootForStyle, GraphicsDevice);
        
        // Load notesplash style (null = programmatic default)
        _noteSplashStyle = NoteSplashStyle.LoadByName(NoteSplashStyleName, GraphicsDevice);
        
        // If user selected "Particles: *", initialize MonoGame.Extended particle system with specific style
        if (NoteSplashStyleName.StartsWith("Particles:"))
        {
            _particleNoteSplash = new ParticleNoteSplash(GraphicsDevice);
            
            // Parse which style was selected
            if (NoteSplashStyleName.Contains("CircleExpand"))
                _particleNoteSplash.CurrentStyle = ParticleNoteSplash.SplashStyle.CircleExpand;
            else if (NoteSplashStyleName.Contains("SplashDrops"))
                _particleNoteSplash.CurrentStyle = ParticleNoteSplash.SplashStyle.SplashDrops;
            else if (NoteSplashStyleName.Contains("StarBurst"))
                _particleNoteSplash.CurrentStyle = ParticleNoteSplash.SplashStyle.StarBurst;
            else if (NoteSplashStyleName.Contains("Explosion"))
                _particleNoteSplash.CurrentStyle = ParticleNoteSplash.SplashStyle.Explosion;
            else if (NoteSplashStyleName.Contains("RingPulse"))
                _particleNoteSplash.CurrentStyle = ParticleNoteSplash.SplashStyle.RingPulse;
            else if (NoteSplashStyleName.Contains("Randomizer"))
                _particleNoteSplash.CurrentStyle = ParticleNoteSplash.SplashStyle.Randomizer;
            
            Console.WriteLine($"[Game1] Loaded particle-based notesplash: {_particleNoteSplash.CurrentStyle}");
        }

        // Setup multiplayer event handlers (ALWAYS subscribe, even if not in multiplayer yet)
        // These handlers will be ready when StartMultiplayerMatch() is called
        Console.WriteLine("[Game1] Setting up multiplayer event handlers...");
        
        // Receive opponent's note hits
        Multiplayer.NetworkManager.Instance.OnOpponentNoteHit += (lane, time, rating) =>
        {
            if (!_isMultiplayerMatch) return; // Ignore if not in multiplayer
            
            // In multiplayer, BOTH players see opponent on strumline 1 (left side)
            // Player strumline is always 0 (right side), opponent is always 1 (left side)
            int opponentStrumlineIndex = 1;
            
            if (_noteRenderers.Count > opponentStrumlineIndex)
            {
                bool isSick = rating == "Sick!";
                _noteRenderers[opponentStrumlineIndex].TriggerManualHit(lane, isSick);
                Console.WriteLine($"[Multiplayer] Opponent hit: lane={lane}, rating={rating}, strumline={opponentStrumlineIndex}");
            }
        };
        
        // Receive opponent's state updates (score, health, combo)
        Multiplayer.NetworkManager.Instance.OnOpponentState += (packet) =>
        {
            if (!_isMultiplayerMatch) return; // Ignore if not in multiplayer
            
            _opponentScore = packet.Score;
            _opponentHealth = packet.Health;
            // Could show this in UI later
        };
        
        // Receive opponent's ready state changes (for loading screen)
        Multiplayer.NetworkManager.Instance.OnOpponentReadyChanged += (isReady) =>
        {
            Console.WriteLine($"[Game1] OnOpponentReadyChanged fired: isReady={isReady}, _loadingScreen={((_loadingScreen != null) ? "exists" : "null")}");
            
            if (!_isMultiplayerMatch)
            {
                Console.WriteLine($"[Game1] Not in multiplayer match, ignoring OpponentReady event");
                return; // Ignore if not in multiplayer
            }
            
            // Store the opponent ready state
            _opponentReadyState = isReady;
            
            if (_loadingScreen != null)
            {
                // Update loading screen with opponent ready state
                // Player 1 sees Player 2's ready state, Player 2 sees Player 1's ready state
                if (_multiplayerPlayerRole == 1)
                {
                    _loadingScreen.Player2Ready = isReady;
                    Console.WriteLine($"[Game1] Updated Player2Ready={isReady} (I am Player 1)");
                }
                else if (_multiplayerPlayerRole == 2)
                {
                    _loadingScreen.Player1Ready = isReady;
                    Console.WriteLine($"[Game1] Updated Player1Ready={isReady} (I am Player 2)");
                }
            }
            else
            {
                Console.WriteLine($"[Game1] LoadingScreen doesn't exist yet, stored opponent ready state for later");
            }
        };
        
        // Receive countdown start signal from server
        Multiplayer.NetworkManager.Instance.OnCountdownStart += (countdownStartTime) =>
        {
            if (!_isMultiplayerMatch) return; // Ignore if not in multiplayer
            
            _countdownSignalReceived = true;
            Console.WriteLine($"[Game1] CountdownStart received from server! Time: {countdownStartTime}ms");
        };
        
        // Receive opponent's key presses for real-time animation
        Multiplayer.NetworkManager.Instance.OnOpponentKeyPress += (lane) =>
        {
            if (!_isMultiplayerMatch) return;
            
            Console.WriteLine($"[Game1] Received OpponentKeyPress: lane={lane}, time={_songTime:F2}");
            
            // Animate opponent's arrows (strumline 1)
            int opponentStrumlineIndex = 1;
            if (_noteRenderers != null && _noteRenderers.Count > opponentStrumlineIndex)
            {
                // Trigger arrow glow/animation on opponent's strumline
                // This makes their arrows light up when they press keys
                _noteRenderers[opponentStrumlineIndex].TriggerManualHit(lane, isSick: false);
            }
            
            // Animate opponent's character sprite
            if (_objAnimators == null || _level == null) return;
            
            var objs = _level.Scene?.Objects;
            if (objs == null || objs.Count == 0) return;
            
            // Determine which character to animate based on opponent's chart
            // Player 1's opponent plays chart 1 (Enemy) → animate strumLine 1
            // Player 2's opponent plays chart 0 (BF) → animate strumLine 0
            int opponentChartIndex = (_multiplayerPlayerRole == 1) ? 1 : 0;
            
            // Animate character with opponent's strumLine
            for (int j = 0; j < objs.Count; j++)
            {
                if ((int)objs[j].StrumLine != opponentChartIndex) continue;
                var anim = _objAnimators[j];
                if (anim == null || !anim.Visible || anim.Opacity <= 0.01f) continue;
                if (anim.IsSetByEvent) continue; // Don't override event-controlled animations
                
                // Animate based on lane: 0=left, 1=down, 2=up, 3=right
                // singAnim: lane+1 maps to the sing animation (1=left,2=down,3=up,4=right)
                int singAnim = Math.Clamp(lane + 1, 1, Math.Max(1, anim.AnimCount - 1));
                anim.SetAnimation(singAnim, setByEvent: false);
            }
        };
        
        // Handle opponent disconnection
        Multiplayer.NetworkManager.Instance.OnOpponentDisconnected += (reason) =>
        {
            if (!_isMultiplayerMatch) return;
            
            Console.WriteLine($"[Game1] Opponent disconnected: {reason}");
            
            // Stop the music
            _musicInst?.Stop();
            
            // Get player's final stats from renderer
            if (_noteRenderers != null && _noteRenderers.Count > 0)
            {
                var playerRend = _noteRenderers[0];
                int totalNotes = playerRend.Sicks + playerRend.Goods + playerRend.Bads + playerRend.Shits + playerRend.Misses;
                float acc = 100f;
                if (totalNotes > 0)
                {
                    float weightedHits = (playerRend.Sicks * 1.0f) + (playerRend.Goods * 0.75f) + 
                                        (playerRend.Bads * 0.5f) + (playerRend.Shits * 0.25f);
                    acc = (weightedHits / totalNotes) * 100f;
                }
                
                Console.WriteLine($"[Game1] Match ended due to disconnect");
                Console.WriteLine($"  Your Score: {playerRend.Score} | Accuracy: {acc:F1}%");
                Console.WriteLine($"  Sicks: {playerRend.Sicks} | Goods: {playerRend.Goods} | Bads: {playerRend.Bads} | Shits: {playerRend.Shits} | Misses: {playerRend.Misses}");
            }
            
            // Set error message to show disconnect reason
            _errorMsg = $"Opponent Disconnected\n{reason}\n\nPress ESC to return to menu";
            
            // Clear multiplayer state
            _isMultiplayerMatch = false;
        };
        
        Console.WriteLine($"[Game1] Multiplayer event handlers subscribed and ready");

        try
        {
            if (!string.IsNullOrEmpty(FnmmPath))
                StartLoadingLevel(FnmmPath);
            else
                _inFreeplay = true;  // No level passed → go straight to freeplay
        }
        catch (Exception ex)
        {
            _errorMsg = ex.Message;
            Console.Error.WriteLine($"[FunkyMakerPlayer] {ex}");
        }
    }

#if ANDROID
    /// <summary>
    /// Called when the app loses focus (minimized, switched away, etc.)
    /// Auto-pause to prevent music/gameplay desync on Android
    /// </summary>
    protected override void OnDeactivated(object sender, EventArgs args)
    {
        base.OnDeactivated(sender, args);
        
        // Auto-pause if gameplay is active (not in menus)
        // Disabled in multiplayer - can't pause during PvP matches
        if (!_isMultiplayerMatch && _level != null && _started && !_paused && !_inPauseMenu && !(_gameOverOverlay?.IsActive ?? false))
        {
            Console.WriteLine("[Android] App deactivated, auto-pausing gameplay");
            _inPauseMenu = true;
            PauseSong();
        }
        else if (_isMultiplayerMatch)
        {
            Console.WriteLine("[Android] App deactivated during multiplayer - pause disabled");
        }
    }
    
    /// <summary>
    /// Called when the app regains focus
    /// </summary>
    protected override void OnActivated(object sender, EventArgs args)
    {
        base.OnActivated(sender, args);
        Console.WriteLine("[Android] App reactivated");
    }
#endif

    
    // ── SetupLevel ───────────────────────────────────────────────────────────
    private void SetupLevel()
    {
        if (_level == null) return;

        try
        {
            // Disable middlescroll in multiplayer (force side-by-side layout)
            if (_isMultiplayerMatch && NoteRenderer.Midlescroll)
            {
                NoteRenderer.Midlescroll = false;
                Console.WriteLine("[Multiplayer] Middlescroll disabled for PvP");
            }
            
            // Update loading screen progress during GPU texture creation
            if (_loadingScreen != null)
            {
                _loadingScreen.CurrentStep = "Creating GPU textures...";
                _loadingScreen.LoadingProgress = 0.0f;
            }

            // Reset scene objects to their initial state (undo any event mutations from previous run)
            foreach (var obj in _level.Scene.Objects)
                obj.ResetToInitialState();

            _audioDeviceReset = false;  // clear any stale device reset flag
            _musicInst?.Dispose();
            _music?.Dispose();

        // ── Window aspect ratio / render target ──────────────────────────────
        bool landscape = _level.Scene.Landscape;
        
        // Use fixed resolution for gameplay (letterboxing will be applied)
        int  rtW = landscape ? 1280 : 720;
        int  rtH = landscape ? 720  : 1280;

        // Recreate render target if size changed
        if (_renderTarget == null || _rtWidth != rtW || _rtHeight != rtH)
        {
            _renderTarget?.Dispose();
            _rtWidth  = rtW;
            _rtHeight = rtH;
            _renderTarget = new RenderTarget2D(GraphicsDevice, rtW, rtH,
                false, SurfaceFormat.Color, DepthFormat.None,
                0, RenderTargetUsage.DiscardContents);
        }
        int sw = rtW;
        int sh = rtH;

        // ── Pre-load all sprite textures into a name→textures cache ──────────
        // We keep textures alive for the level lifetime.
        // For very wide strips (exceeding GPU max texture size), split into
        // per-frame textures — but only for background/prop sprites (strumLine = -1).
        
        // Get ACTUAL GPU max texture size from OpenGL
        int maxTexSize = GetGpuMaxTextureSize();
        Console.WriteLine($"[GPU] Max texture size: {maxTexSize}px (device limit)");

        // Build set of sprite names used ONLY by background objects (strumLine < 0)
        // AND never used by characters (strumLine >= 0)
        var spriteUsage = new Dictionary<string, (bool usedByBg, bool usedByChar)>();
        foreach (var obj in _level.Scene.Objects)
        {
            if (!spriteUsage.ContainsKey(obj.Spr))
                spriteUsage[obj.Spr] = (false, false);
            
            var (usedByBg, usedByChar) = spriteUsage[obj.Spr];
            if (obj.StrumLine < 0f)
                spriteUsage[obj.Spr] = (true, usedByChar);
            else
                spriteUsage[obj.Spr] = (usedByBg, true);
        }

        var texCache = new Dictionary<string, List<Texture2D>>(StringComparer.OrdinalIgnoreCase);

        Console.WriteLine($"[Scene] Loading {_level.Sprites.Count} sprites from level...");
        
        // Initialize cache if this is the first load of this level
        if (_spriteTextureCache == null)
        {
            _spriteTextureCache = new Dictionary<string, Dictionary<int, SpriteTextureCache>>();
            Console.WriteLine("[Cache] Initialized sprite texture cache");
        }
        
        // Reset SplitOffset on all sprite anim data before rebuilding textures.
        // This is needed for restarts — SplitOffset is set during texture loading
        // and must be cleared so the split logic runs fresh on each SetupLevel call.
        foreach (var (_, (def, _)) in _level.Sprites)
            foreach (var animData in def.Data)
            {
                animData.SplitOffset          = -1;
                animData.ChunkFrames          = 0;
                animData.FrameWidth           = 0;
                animData.TextureIndex         = -1;
                animData.DownscaleCompensation = 1.0f;
            }

        int spriteIndex = 0;
        int totalSprites = _level.Sprites.Count;
        var stopwatch = System.Diagnostics.Stopwatch.StartNew();
        int spritesProcessedSinceLastUpdate = 0;
        const int SPRITES_PER_UPDATE = 5;  // Update screen every 5 sprites instead of every sprite
        
        foreach (var (name, (def, frames)) in _level.Sprites)
        {
            // Report progress (0% to 80% during texture loading)
            if (totalSprites > 0)
            {
                float progress = (spriteIndex / (float)totalSprites) * 0.8f;
                string step = $"Creating GPU textures... ({spriteIndex + 1}/{totalSprites})";
                
                if (_loadingScreen != null)
                {
                    _loadingScreen.LoadingProgress = progress;
                    _loadingScreen.CurrentStep = step;
                }
                
                // Update screen every N sprites or every 500ms (whichever comes first)
                spritesProcessedSinceLastUpdate++;
                if (_loadingScreen != null && 
                    (spritesProcessedSinceLastUpdate >= SPRITES_PER_UPDATE || stopwatch.ElapsedMilliseconds >= 500))
                {
                    Console.WriteLine($"[SetupLevel] Rendering loading screen: {_loadingScreen.LoadingProgress * 100:F0}% - {_loadingScreen.CurrentStep}");
                    RenderLoadingScreen();
                    stopwatch.Restart();
                    spritesProcessedSinceLastUpdate = 0;
                }
            }
            spriteIndex++;
            
            // Check if this sprite is already cached
            if (!_spriteTextureCache.ContainsKey(name))
                _spriteTextureCache[name] = new Dictionary<int, SpriteTextureCache>();
            
            var spriteCache = _spriteTextureCache[name];
            bool useCachedTextures = spriteCache.Count == frames.Count && spriteCache.All(kvp => kvp.Value.Textures.Count > 0);
            
            if (useCachedTextures)
            {
                Console.WriteLine($"[Cache] Using cached textures for '{name}' ({frames.Count} frames)");
                
                // Restore cached textures and metadata
                for (int fi = 0; fi < frames.Count; fi++)
                {
                    if (!spriteCache.ContainsKey(fi)) continue;
                    
                    var cached = spriteCache[fi];
                    if (fi < def.Data.Count)
                    {
                        def.Data[fi].SplitOffset = cached.SplitOffset;
                        def.Data[fi].ChunkFrames = cached.ChunkFrames;
                        def.Data[fi].FrameWidth = cached.FrameWidth;
                        def.Data[fi].TextureIndex = cached.TextureIndex;
                        def.Data[fi].DownscaleCompensation = cached.DownscaleCompensation;
                        def.Data[fi].AtlasFrameRects = cached.AtlasFrameRects;
                    }
                }
                
                // Build texture list from cache
                var cachedTextures = new List<Texture2D>();
                for (int fi = 0; fi < frames.Count; fi++)
                {
                    if (spriteCache.ContainsKey(fi))
                    {
                        cachedTextures.AddRange(spriteCache[fi].Textures);
                    }
                }
                
                texCache[name] = cachedTextures;
                continue;  // Skip texture creation, use cached
            }
            
            // No cache hit - create textures normally
            Console.WriteLine($"[Cache] Creating new textures for '{name}'");
            var textures  = new List<Texture2D>();
            // Sprite is splittable if:
            // - (type 2.0 = explicit background/UI) OR (type 0.0 AND used only by backgrounds)
            var (usedByBg, usedByChar) = spriteUsage.GetValueOrDefault(name, (false, false));
            // Split rules by Funky Maker sprite type:
            // type 1 (character): NEVER split — multi-anim spritesheet, corrupts if split
            // type 2 (background): split if too wide — single large spritesheet
            // type 0 (item): split if too wide — multiple PNGs, each can be a wide strip
            // type 3 (icon): never split — small PNGs
            bool isBgSprite = def.Type == 2.0f || (def.Type == 0.0f && !usedByChar);

            // Track frame processing for loading screen updates
            // On Android: Update VERY frequently to prevent ANR (every frame OR every 100ms)
            // On PC: Less frequent updates (every 5 frames OR every 500ms)
#if ANDROID
            const int FRAMES_PER_UPDATE = 1;  // Update EVERY frame on Android
            const int MAX_MS_BETWEEN_UPDATES = 100;  // Max 100ms between updates (ANR limit is 5000ms)
#else
            const int FRAMES_PER_UPDATE = 5;
            const int MAX_MS_BETWEEN_UPDATES = 500;
#endif
            var lastFrameUpdateTime = System.Diagnostics.Stopwatch.StartNew();
            
            for (int fi = 0; fi < frames.Count; fi++)
            {
                // Update loading screen periodically during frame processing
                if (fi % FRAMES_PER_UPDATE == 0 || lastFrameUpdateTime.ElapsedMilliseconds > MAX_MS_BETWEEN_UPDATES)
                {
                    RenderLoadingScreen();
                    lastFrameUpdateTime.Restart();
                }
                
                int frameCount = fi < def.Data.Count ? def.Data[fi].FrameCount : 1;
                // Peek PNG dimensions without full decode to decide split strategy
                int pngW = 0, pngH = 0;
                if (frames[fi].Length > 24)
                {
                    // Read dimensions as big-endian signed integers (PNG standard format)
                    // PNG uses signed 32-bit integers, so negative values are theoretically possible
                    // but in practice indicate corrupt/unusual headers
                    int signedW = (frames[fi][16] << 24) | (frames[fi][17] << 16) | (frames[fi][18] << 8) | frames[fi][19];
                    int signedH = (frames[fi][20] << 24) | (frames[fi][21] << 16) | (frames[fi][22] << 8) | frames[fi][23];
                    
                    // Accept any non-zero dimension (including negative, which we'll convert to positive)
                    // Negative values often indicate byte order issues or unusual PNG encoders
                    if (signedW != 0) pngW = Math.Abs(signedW);
                    if (signedH != 0) pngH = Math.Abs(signedH);
                    
                    // Sanity check: reject dimensions over 65536 (likely corrupted)
                    if (pngW > 65536) pngW = 0;
                    if (pngH > 65536) pngH = 0;
                }

                // Skip corrupt/empty PNGs (zero dimensions or empty bytes)
                // Note: pngW/pngH from header peek may be wrong for unusual PNG formats.
                // If the file has data, always try ImageSharp first — it handles edge cases.
                if (frames[fi].Length == 0)
                {
                    Console.WriteLine($"[Texture] {name}[{fi}]: skipped — empty file");
                    var dummy = new Texture2D(GraphicsDevice, 1, 1);
                    dummy.SetData(new[] { Color.Transparent });
                    textures.Add(dummy);
                    continue;
                }

                // If header peek gives invalid dimensions, try to load anyway and let ImageSharp decide
                bool headerInvalid = pngW <= 0 || pngH <= 0;
                if (headerInvalid)
                {
                    Console.WriteLine($"[Texture] {name}[{fi}]: header invalid ({pngW}x{pngH}), attempting ImageSharp load...");
                    
                    // Update loading screen to show we're fixing invalid sprite
                    if (_loadingScreen != null)
                    {
                        _loadingScreen.CurrentStep = $"Fixing invalid sprite: {name}";
                        RenderLoadingScreen();
                    }
                    
                    try
                    {
                        using var msProbe = new MemoryStream(frames[fi]);
                        using var imgProbe = SixLabors.ImageSharp.Image
                            .Load<SixLabors.ImageSharp.PixelFormats.Rgba32>(msProbe);
                        // Got dimensions from ImageSharp
                        int imgSharpW = imgProbe.Width;
                        int imgSharpH = imgProbe.Height;
                        Console.WriteLine($"[Texture] {name}[{fi}]: ImageSharp resolved to {imgSharpW}x{imgSharpH}");
                        
                        // Validate ImageSharp dimensions against expected frame width
                        // If ImageSharp reports width that doesn't make sense with frameCount,
                        // calculate real width from frameCount instead
                        if (frameCount > 1)
                        {
                            int expectedFrameWidth = imgSharpW / frameCount;
                            // If frame width is unreasonably large (>16K), ImageSharp dimensions are corrupt too
                            if (expectedFrameWidth > 16384)
                            {
                                Console.WriteLine($"[Texture] {name}[{fi}]: ImageSharp width {imgSharpW} seems corrupt (frame would be {expectedFrameWidth}px wide). Using XML to calculate real width.");
                                // Assume height is correct but width is corrupt
                                // Calculate real width as frameCount * reasonable_frame_width
                                // We'll detect real frame width when we actually load the image data
                                pngW = imgSharpW;  // Keep for now, will recalculate after loading
                                pngH = imgSharpH;
                            }
                            else
                            {
                                pngW = imgSharpW;
                                pngH = imgSharpH;
                            }
                        }
                        else
                        {
                            pngW = imgSharpW;
                            pngH = imgSharpH;
                        }
                    }
                    catch (Exception exProbe)
                    {
                        Console.WriteLine($"[Texture] {name}[{fi}]: skipped — ImageSharp also failed: {exProbe.Message}");
                        var dummy = new Texture2D(GraphicsDevice, 1, 1);
                        dummy.SetData(new[] { Color.Transparent });
                        textures.Add(dummy);
                        continue;
                    }
                }

                // Validate frame count vs PNG width
                int actualFrameCount = frameCount;
                int fw0 = frameCount > 1 ? pngW / frameCount : pngW;
                
                // Detect if frame width is unreasonably small (likely corrupted XML)
                if (frameCount > 1 && fw0 < 10)
                {
                    Console.WriteLine($"[Texture] {name}[{fi}]: WARNING - XML says {frameCount} frames but PNG is only {pngW}px wide ({fw0}px per frame). Treating as 1 frame.");
                    actualFrameCount = 1;
                    fw0 = pngW;  // Use full width as single frame
                }
                bool isCharSprite = def.Type == 1.0f || (def.Type == 0.0f && usedByChar);

                // ── Oversized sprite strategy ─────────────────────────────────
                // The key question is whether the INDIVIDUAL FRAME fits in GPU:
                //   fw0 = pngW / frameCount  (width of one frame)
                //
                // Case A: fw0 > maxTexSize OR pngH > maxTexSize — frame dimensions exceed GPU limit
                //   → Downscale to fit within maxTexSize x maxTexSize
                //   → Sets DownscaleCompensation so the sprite draws at original visual size.
                //
                // Case B: fw0 <= maxTexSize AND pngH <= maxTexSize but pngW > maxTexSize (many frames, strip too wide).
                //   → Split per-frame (char) or into chunks (bg). Each frame loads fine.
                //
                // Case B2: bg single-frame, pngW > maxTexSize — tile into maxTexSize-wide chunks.
                //   → Full quality, no downscale. SpriteAnimator uses chunk mode to draw.
                //
                // Case C: pngW <= maxTexSize AND pngH <= maxTexSize — texture fits as-is. Load normally.
                bool needsDownscale = (fw0 > maxTexSize || pngH > maxTexSize);
                bool needsBgTile    = isBgSprite && actualFrameCount == 1 && pngW > maxTexSize;
                // Apply splitting to ALL sprites that exceed GPU limits, not just bg/char
                bool needsSplit     = !needsDownscale && !needsBgTile && pngW > maxTexSize && actualFrameCount > 1;
                
                // Log when texture exceeds limits
                if (needsDownscale)
                {
                    Console.WriteLine($"[Texture] {name}[{fi}]: Needs downscale - frame size {fw0}x{pngH}px exceeds GPU limit {maxTexSize}px");
                }
                else if (needsSplit)
                {
                    Console.WriteLine($"[Texture] {name}[{fi}]: Needs split - total width {pngW}px exceeds GPU limit {maxTexSize}px");
                }

                Texture2D? fullTex = null;
                if (!needsSplit && !needsBgTile)
                {
                    try
                    {
#if BLAZORGL
                        // BlazorGL/WebGL has a 4096px texture limit - downscale if needed
                        const int BLAZOR_MAX_TEX = 4096;
                        if (pngW > BLAZOR_MAX_TEX || pngH > BLAZOR_MAX_TEX)
                        {
                            using var msDownscale = new MemoryStream(frames[fi]);
                            using var imgDownscale = SixLabors.ImageSharp.Image
                                .Load<SixLabors.ImageSharp.PixelFormats.Rgba32>(msDownscale);
                            
                            float scale = Math.Min((float)BLAZOR_MAX_TEX / pngW, (float)BLAZOR_MAX_TEX / pngH);
                            int newW = (int)(pngW * scale);
                            int newH = (int)(pngH * scale);
                            
                            Console.WriteLine($"[Texture] {name}[{fi}]: WebGL downscale {pngW}x{pngH}→{newW}x{newH}");
                            
                            imgDownscale.Mutate(ctx => ctx.Resize(newW, newH,
                                SixLabors.ImageSharp.Processing.KnownResamplers.Lanczos3));
                            
                            var pixels = new Color[newW * newH];
                            imgDownscale.ProcessPixelRows(acc =>
                            {
                                for (int y = 0; y < newH; y++)
                                {
                                    var row = acc.GetRowSpan(y);
                                    int rb = y * newW;
                                    for (int x = 0; x < newW; x++)
                                    {
                                        var p = row[x];
                                        pixels[rb + x] = Color.FromNonPremultiplied(p.R, p.G, p.B, p.A);
                                    }
                                }
                            });
                            
                            fullTex = new Texture2D(GraphicsDevice, newW, newH);
                            fullTex.SetData(pixels);
                            
                            // Set compensation so sprite draws at original size
                            if (fi < def.Data.Count)
                                def.Data[fi].DownscaleCompensation = 1.0f / scale;
                        }
                        else
                        {
                            fullTex = LoadTextureViaImageSharp(GraphicsDevice, frames[fi]);
                        }
#else
                        fullTex = LoadTextureViaImageSharp(GraphicsDevice, frames[fi]);
#endif
                        
                        // Update loading screen after each texture load (especially important for heavy textures)
#if ANDROID
                        RenderLoadingScreen();
#endif
                    }
                    catch (Exception ex)
                    {
                        Console.WriteLine($"[Texture] Failed to load {name}[{fi}]: {ex.Message}");
                    }
                }

                // Character atlas packing: frame is too wide for GPU — pack into 2D atlas
                // instead of downscaling to preserve full quality (same as backgrounds)
                if (fullTex == null && needsDownscale)
                {
                    try
                    {
                        int fw = fw0;
                        int fh = pngH;
                        Console.WriteLine($"[Texture] {name}[{fi}]: {pngW}x{pngH} (character atlas packing → {actualFrameCount} frames, fw={fw})");
                        var packResults = AtlasPacker.PackHorizontalStrip(
                            frames[fi], actualFrameCount, fw, fh, maxTexSize, GraphicsDevice,
                            (currentFrame, totalFrames) =>
                            {
                                // Update loading screen during atlas packing
                                if (_loadingScreen != null)
                                {
                                    _loadingScreen.CurrentStep = $"Packing atlas: {name} ({currentFrame}/{totalFrames} frames)";
                                    RenderLoadingScreen();
                                }
                            });
                        
                        if (packResults.Count > 0)
                        {
                            int splitStart = textures.Count;
                            
                            // Add packed atlas textures
                            foreach (var pack in packResults)
                            {
                                textures.Add(pack.AtlasTexture);
                            }
                            
                            // Configure sprite to use packed atlas (NO downscale compensation needed)
                            if (fi < def.Data.Count)
                            {
                                def.Data[fi].SplitOffset       = splitStart;
                                def.Data[fi].UsesPackedAtlas   = true;
                                def.Data[fi].AtlasFrameRects   = packResults[0].FrameCoordinates;
                                def.Data[fi].FrameWidth        = fw;
                                // No DownscaleCompensation — atlas uses original size
                            }
                        }
                        else
                        {
                            Console.WriteLine($"[Texture] {name}[{fi}]: Atlas packing failed for character");
                        }
                        
                        // Update loading screen after atlas packing
#if ANDROID
                        RenderLoadingScreen();
#endif
                    }
                    catch (Exception ex2)
                    {
                        Console.WriteLine($"[Texture] {name}[{fi}]: character atlas packing failed: {ex2.Message}");
                    }
                }

                if (fullTex == null && !needsSplit)
                {
                    var dummy = new Texture2D(GraphicsDevice, 1, 1);
                    dummy.SetData(new[] { Color.Transparent });
                    textures.Add(dummy);
                    continue;
                }

                if (needsSplit)
                {
                    // Too wide for GPU — split into chunks (bg sprites) or per-frame (characters)
                    int fw = fw0;
                    int fh = pngH;
                    if (isBgSprite)
                    {
                        // Use AtlasPacker to create 2D atlas instead of horizontal chunks
                        Console.WriteLine($"[Texture] {name}[{fi}]: {pngW}x{pngH} (atlas packing → {actualFrameCount} frames)");
                        var packResults = AtlasPacker.PackHorizontalStrip(
                            frames[fi], actualFrameCount, fw, fh, maxTexSize, GraphicsDevice,
                            (currentFrame, totalFrames) =>
                            {
                                // Update loading screen during atlas packing
                                if (_loadingScreen != null)
                                {
                                    _loadingScreen.CurrentStep = $"Packing atlas: {name} ({currentFrame}/{totalFrames} frames)";
                                    RenderLoadingScreen();
                                }
                            });
                        
                        if (packResults.Count > 0)
                        {
                            int splitStart = textures.Count;
                            
                            // Add packed atlas textures
                            foreach (var pack in packResults)
                            {
                                textures.Add(pack.AtlasTexture);
                            }
                            
                            // Configure sprite to use packed atlas
                            if (fi < def.Data.Count)
                            {
                                def.Data[fi].SplitOffset       = splitStart;
                                def.Data[fi].UsesPackedAtlas   = true;
                                def.Data[fi].AtlasFrameRects   = packResults[0].FrameCoordinates;
                                def.Data[fi].FrameWidth        = fw;
                            }
                            
                            // Update loading screen after atlas packing
#if ANDROID
                            RenderLoadingScreen();
#endif
                        }
                        else
                        {
                            Console.WriteLine($"[Texture] {name}[{fi}]: Atlas packing failed, falling back to chunks");
                            // Fallback to old chunk method
                            int framesPerChunk = Math.Max(1, maxTexSize / Math.Max(1, fw));
                            int splitStart = textures.Count;
                            SplitPngIntoChunks(frames[fi], actualFrameCount, fw, fh, framesPerChunk, textures);
                            if (fi < def.Data.Count)
                            {
                                def.Data[fi].SplitOffset  = splitStart;
                                def.Data[fi].ChunkFrames  = framesPerChunk;
                                def.Data[fi].FrameWidth   = fw;
                            }
                            
                            // Update loading screen after chunking
#if ANDROID
                            RenderLoadingScreen();
#endif
                        }
                    }
                    else
                    {
                        // Characters: use atlas packing instead of per-frame split to preserve quality
                        Console.WriteLine($"[Texture] {name}[{fi}]: {pngW}x{pngH} (character atlas packing → {actualFrameCount} frames)");
                        var packResults = AtlasPacker.PackHorizontalStrip(
                            frames[fi], actualFrameCount, fw, fh, maxTexSize, GraphicsDevice,
                            (currentFrame, totalFrames) =>
                            {
                                // Update loading screen during atlas packing
                                if (_loadingScreen != null)
                                {
                                    _loadingScreen.CurrentStep = $"Packing atlas: {name} ({currentFrame}/{totalFrames} frames)";
                                    RenderLoadingScreen();
                                }
                            });
                        
                        if (packResults.Count > 0)
                        {
                            int splitStart = textures.Count;
                            
                            // Add packed atlas textures
                            foreach (var pack in packResults)
                            {
                                textures.Add(pack.AtlasTexture);
                            }
                            
                            // Configure sprite to use packed atlas
                            if (fi < def.Data.Count)
                            {
                                def.Data[fi].SplitOffset       = splitStart;
                                def.Data[fi].UsesPackedAtlas   = true;
                                def.Data[fi].AtlasFrameRects   = packResults[0].FrameCoordinates;
                                def.Data[fi].FrameWidth        = fw;
                            }
                            
                            // Update loading screen after character atlas packing
#if ANDROID
                            RenderLoadingScreen();
#endif
                        }
                        else
                        {
                            Console.WriteLine($"[Texture] {name}[{fi}]: Atlas packing failed for character, using fallback");
                            // Fallback: per-frame split (old behavior)
                            int splitStart = textures.Count;
                            SplitPngIntoFrames(frames[fi], actualFrameCount, fw, fh, textures);
                            if (fi < def.Data.Count)
                            {
                                def.Data[fi].SplitOffset  = splitStart;
                                def.Data[fi].ChunkFrames  = 0;
                                def.Data[fi].FrameWidth   = fw;
                            }
                            
                            // Update loading screen after frame splitting
#if ANDROID
                            RenderLoadingScreen();
#endif
                        }
                    }
                    continue;
                }

                if (needsBgTile)
                {
                    // Single-frame bg too wide for GPU — tile into maxTexSize-wide chunks.
                    // Full quality: no downscale. SpriteAnimator chunk mode draws them in sequence.
                    int tileW      = maxTexSize;
                    int tileCount  = (int)Math.Ceiling((double)pngW / tileW);
                    Console.WriteLine($"[Texture] {name}[{fi}]: {pngW}x{pngH} (bg tile → {tileCount} tiles of up to {tileW}px)");
                    int splitStart = textures.Count;
                    SplitBgIntoTiles(frames[fi], pngW, pngH, tileW, textures);
                    if (fi < def.Data.Count)
                    {
                        def.Data[fi].SplitOffset = splitStart;
                        def.Data[fi].ChunkFrames = 1;
                        def.Data[fi].FrameWidth  = tileW;
                    }
                    
                    // Update loading screen after tiling
#if ANDROID
                    RenderLoadingScreen();
#endif
                    continue;
                }

                Console.WriteLine($"[Texture] {name}[{fi}]: {fullTex!.Width}x{fullTex.Height}, frames={frameCount}");
                // GPU split for oversized bg strips
                if (isBgSprite && fullTex.Width > maxTexSize && frameCount > 1)
                {
                    int fw = fullTex.Width / frameCount;
                    int fh = fullTex.Height;
                    Console.WriteLine($"[SplitGpu] Splitting {name}[{fi}] into {frameCount} frames of {fw}x{fh}");
                    int splitStart = textures.Count;
                    SplitTextureGpu(fullTex, frameCount, fw, fh, textures);
                    fullTex.Dispose();
                    if (fi < def.Data.Count)
                        def.Data[fi].SplitOffset = splitStart;
                }
                else
                {
                    // Record the texture index for this animation so DrawStrip can find it
                    if (fi < def.Data.Count)
                        def.Data[fi].TextureIndex = textures.Count;
                    textures.Add(fullTex);
                }
            }

            texCache[name] = textures;
            
            // Save to persistent cache for next restart
            for (int fi = 0; fi < frames.Count; fi++)
            {
                if (fi < def.Data.Count)
                {
                    var cached = new SpriteTextureCache
                    {
                        Textures = new List<Texture2D>(),
                        SplitOffset = def.Data[fi].SplitOffset,
                        ChunkFrames = def.Data[fi].ChunkFrames,
                        FrameWidth = def.Data[fi].FrameWidth,
                        TextureIndex = def.Data[fi].TextureIndex,
                        DownscaleCompensation = def.Data[fi].DownscaleCompensation,
                        AtlasFrameRects = def.Data[fi].AtlasFrameRects
                    };
                    
                    // Add textures for this frame
                    if (def.Data[fi].SplitOffset >= 0)
                    {
                        // Split frames: collect chunk textures
                        int startIdx = def.Data[fi].SplitOffset;
                        int endIdx = fi + 1 < def.Data.Count ? def.Data[fi + 1].SplitOffset : textures.Count;
                        if (endIdx < 0) endIdx = textures.Count;
                        for (int t = startIdx; t < endIdx && t < textures.Count; t++)
                            cached.Textures.Add(textures[t]);
                    }
                    else if (def.Data[fi].TextureIndex >= 0 && def.Data[fi].TextureIndex < textures.Count)
                    {
                        // Single texture
                        cached.Textures.Add(textures[def.Data[fi].TextureIndex]);
                    }
                    
                    spriteCache[fi] = cached;
                }
            }
            
            Console.WriteLine($"[Cache] Saved {name} to cache ({spriteCache.Count} frames)");
        }

        // GPU texture creation complete
        if (_loadingScreen != null)
        {
            _loadingScreen.LoadingProgress = 0.80f;
            _loadingScreen.CurrentStep = "Textures loaded!";
            RenderLoadingScreen();  // Force render
        }

        // ── Build one animator per scene object (index 0-74) ─────────────────
        if (_loadingScreen != null)
        {
            _loadingScreen.LoadingProgress = 0.85f;
            _loadingScreen.CurrentStep = "Initializing scene objects...";
            RenderLoadingScreen();  // Force render
        }
        
        // Each object has a current sprite name that can change via ObjectSpriteSet.
        // The animator is keyed by object index, not sprite name.
        var objs = _level.Scene.Objects;
        _objAnimators = new SpriteAnimator?[objs.Count];

        for (int i = 0; i < objs.Count; i++)
        {
            try
            {
                var obj = objs[i];
                Console.WriteLine($"[Scene] Loading object {i}: spr={obj.Spr}, visible={obj.Visible}, transform={(obj.Transform != null ? obj.Transform.Count : 0)}");
                
                if (!_level.Sprites.TryGetValue(obj.Spr, out var sprData))
                {
                    Console.WriteLine($"[Scene] Object {i} sprite '{obj.Spr}' not found in level.Sprites!");
                    continue;
                }
                
                if (!texCache.TryGetValue(obj.Spr, out var textures))
                {
                    Console.WriteLine($"[Scene] Object {i} sprite '{obj.Spr}' not found in texCache!");
                    continue;
                }
                
                if (textures == null || textures.Count == 0)
                {
                    Console.WriteLine($"[Scene] Object {i} sprite '{obj.Spr}' has no textures!");
                    continue;  // Skip if no textures
                }

                var pos = obj.GetPosition();
                var scale = obj.GetScale();
                
                Console.WriteLine($"[Scene] Object {i} pos=({pos.X}, {pos.Y}), scale=({scale.X}, {scale.Y}), textures={textures.Count}");
                
                // CRITICAL FIX: For Fixed objects with Transform array, use Transform[0] as initial position
                // This ensures the first ObjectPositionSet tween starts from the EXACT position specified in events
                if (obj.Fixed && obj.Transform != null && obj.Transform.Count > 0)
                {
                    // Use Transform[0] as the initial position for Fixed objects
                    var transform0 = obj.Transform[0];
                    bool isNormalized = transform0.Position.X >= 0f && transform0.Position.X <= 1f && 
                                       transform0.Position.Y >= 0f && transform0.Position.Y <= 1f;
                    
                    if (isNormalized)
                    {
                        // Convert from normalized to pixels
                        pos = new Vec2
                        {
                            X = transform0.Position.X * rtW,
                            Y = transform0.Position.Y * rtH
                        };
                        Console.WriteLine($"[Scene] Object {i} (Fixed): Using Transform[0].Position converted to pixels: ({pos.X:F1}, {pos.Y:F1})");
                    }
                    else
                    {
                        // Use Transform[0] directly (already in pixels)
                        pos = new Vec2
                        {
                            X = transform0.Position.X,
                            Y = transform0.Position.Y
                        };
                        Console.WriteLine($"[Scene] Object {i} (Fixed): Using Transform[0].Position as-is: ({pos.X:F1}, {pos.Y:F1})");
                    }
                }
                
                Console.WriteLine($"[Scene] Object {i} pos=({pos.X}, {pos.Y}), scale=({scale.X}, {scale.Y}), textures={textures.Count}");

                // Debug: Log sprite offset from first animation
                if (sprData.Def.Data != null && sprData.Def.Data.Count > 0)
                {
                    var firstAnim = sprData.Def.Data[0];
                    Console.WriteLine($"[Scene] Object {i} ({obj.Spr}) first anim offset: ({firstAnim.Offset.X}, {firstAnim.Offset.Y}), scale: {firstAnim.Scale}");
                }

                _objAnimators[i] = new SpriteAnimator(obj.Spr, sprData.Def, textures,
                    new Vector2(pos.X, pos.Y),
                    new Vector2(scale.X, scale.Y))
                {
                    Bpm          = _level.Data.EffectiveBpm,
                    Visible      = obj.Visible,
                    IsCharacter  = obj.StrumLine >= 0f,
                    IsBG         = sprData.Def.Type == 2.0f,
                    LevelVersion = _level.Data.Version,
                    IsFixed      = obj.Fixed,
                    RenderWidth  = rtW,
                    RenderHeight = rtH,
                };
                
                Console.WriteLine($"[Scene] Object {i} animator created successfully!");

                // Apply color and opacity from v3 format
                if (obj.Color != null)
                {
                    _objAnimators[i]!.TintColor = new Color(
                        (byte)obj.Color.R,
                        (byte)obj.Color.G,
                        (byte)obj.Color.B
                    );
                }
                if (obj.Opacity.HasValue)
                {
                    _objAnimators[i]!.Opacity = obj.Opacity.Value;
                }

                // Apply initial animation if specified in scene.json
                if (obj.Anim.HasValue)
                {
                    _objAnimators[i]!.SetAnimation((int)obj.Anim.Value);
                    Console.WriteLine($"[Scene] Object {i} set to animation {obj.Anim.Value} from scene.json");
                }
                // For characters (strumLine >= 0): initialize with idle animation
                // This ensures characters always have an active animation, even without ObjectAnimationSet events
                else if (obj.StrumLine >= 0f && obj.Visible && sprData.Def.Data.Count > 0)
                {
                    // If anim 0 is a 1-frame placeholder, use anim 1 (proper idle)
                    // Otherwise use anim 0 (idle animation with multiple frames)
                    int idleAnim = (sprData.Def.Data.Count > 1 && sprData.Def.Data[0].FrameCount == 1) ? 1 : 0;
                    if (_objAnimators[i] != null && idleAnim < sprData.Def.Data.Count)
                    {
                        _objAnimators[i]!.SetAnimation(idleAnim);
                        var animData = sprData.Def.Data[idleAnim]!;
                        Console.WriteLine($"[Scene] Character object {i} '{obj.Spr}' (strumLine={obj.StrumLine}) initialized with animation {idleAnim} ({animData.FrameCount} frames, loop={animData.Loop})");
                    }
                }
            }
            catch (Exception exObj)
            {
                Console.WriteLine($"[SetupLevel] Failed to create animator for object {i}: {exObj.Message}");
                _objAnimators[i] = null;  // Skip this object
            }
        }

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
        
        // Multiplayer: Swap icons based on player role
        // Player 1 (BF chart): Normal assignment (opponent=0, player=1)
        // Player 2 (Enemy chart): Swapped (opponent=1, player=0)
        if (_isMultiplayerMatch && _multiplayerPlayerRole == 2)
        {
            // Player 2: I play Enemy chart, so I am the "opponent" character
            var opponentIcon = BuildIconAnimator(icons.Count > 1 ? icons[1] : null, texCache);  // BF is my opponent
            var playerIcon = BuildIconAnimator(icons.Count > 0 ? icons[0] : null, texCache);  // Enemy is me
            
            if (_healthBar != null)
            {
                _healthBar.OpponentIcon = opponentIcon;
                _healthBar.PlayerIcon = playerIcon;
                _healthBar.OpponentBarColor = SampleIconColor(opponentIcon) ?? new Color(0xFF, 0x00, 0x00);
                _healthBar.PlayerBarColor = SampleIconColor(playerIcon) ?? new Color(0x66, 0xFF, 0x66);
                _healthBar.IconMode = IconMode;
            }
            
            Console.WriteLine("[Game1] Multiplayer Role 2: Swapped icons (I am Enemy character)");
        }
        else
        {
            // Normal: Player 1 or single-player
            var opponentIcon = BuildIconAnimator(icons.Count > 0 ? icons[0] : null, texCache);
            var playerIcon = BuildIconAnimator(icons.Count > 1 ? icons[1] : null, texCache);
            
            if (_healthBar != null)
            {
                _healthBar.OpponentIcon = opponentIcon;
                _healthBar.PlayerIcon = playerIcon;
                _healthBar.OpponentBarColor = SampleIconColor(opponentIcon) ?? new Color(0xFF, 0x00, 0x00);
                _healthBar.PlayerBarColor = SampleIconColor(playerIcon) ?? new Color(0x66, 0xFF, 0x66);
                _healthBar.IconMode = IconMode;
            }
            
            if (_isMultiplayerMatch)
                Console.WriteLine("[Game1] Multiplayer Role 1: Normal icons (I am BF character)");
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
        
        if (_isMultiplayerMatch)
        {
            if (_multiplayerPlayerRole == 1)
            {
                // Player 1: I control Boyfriend (right side)
                myChartIndex = 0;  // Chart 0 = Player/BF in standard FNF
                opponentChartIndex = 1;  // Chart 1 = Enemy/Dad
                Console.WriteLine("[Game1] Multiplayer: Role 1 (Player 1) - I control BOYFRIEND (chart 0), opponent is ENEMY (chart 1)");
            }
            else if (_multiplayerPlayerRole == 2)
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
            
            // In multiplayer, force exactly 2 strumlines (mine + opponent visual)
            strumlineCount = 2;
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
            if (_isMultiplayerMatch && strumlineCount >= 2)
            {
                if (i == 0)
                    chartIndex = myChartIndex;  // Player strumline uses MY chart
                else if (i == 1)
                    chartIndex = opponentChartIndex;  // Opponent strumline uses THEIR chart
            }
            else if (_isMultiplayerMatch && strumlineCount < 2)
            {
                chartIndex = 0;  // Fallback: use chart 0 if level only has 1
            }
            
            var renderer = new NoteRenderer(_level, chartIndex, xOffset, GraphicsDevice, _noteStyle);
            renderer.SetSplashStyle(_noteSplashStyle);  // Apply splash style (null = programmatic default)
            renderer.SetParticleSplash(_particleNoteSplash);  // Apply particle splash (null if not using)
            
            // Set whether this strumline is controlled by local player
            // In single player: always strumline 0 (player on right)
            // In multiplayer: BOTH players always control strumline 0 (right side)
            //   - Player 1 plays chart 0 (BF) on strumline 0 (right)
            //   - Player 2 plays chart 1 (Enemy) on strumline 0 (right)
            //   - Strumline 1 (left) shows OPPONENT's hits for both players
            bool isMyStrumline = (i == 0);  // Always strumline 0 in multiplayer
            renderer.SetIsPlayer(isMyStrumline);
            
#if ANDROID
            // Set touch controls for Android input detection (player strumline only)
            if (i == 0 && _touchControls != null)
            {
                renderer.SetTouchControls(_touchControls);
            }
#endif
            
            // Opponent strumline (index 1): always semi-transparent so player doesn't confuse it with theirs
            // In multiplayer, always show opponent regardless of HideOpponentStrumline setting
            if (i == 1)
            {
                renderer.Alpha = (_isMultiplayerMatch || !HideOpponentStrumline) ? 0.5f : 0f;
            }
            // With 3+ strumlines: extras (2+) hide when idle
            if (strumlineCount >= 3 && i >= 2) renderer.HideWhenIdle = true;
            // Multiplayer: Disable autoplay for opponent (index 1) — hits come from network
            if (_isMultiplayerMatch && i == 1) renderer.DisableAutoplay = true;
            renderer.SetRenderTargetSize(rtW, rtH);
            _noteRenderers.Add(renderer);
        }

        // Apply midlescroll split to opponent strumline if needed
        if (strumlineCount == 2 && NoteRenderer.Midlescroll)
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
        for (int i = 0; i < _noteRenderers.Count; i++)
        {
            int strumlineIdx = i;
            
            // Determine which character strumLine this renderer should animate
            // In single player: renderer i animates characters with strumLine i
            // In multiplayer: renderer 0 animates based on myChartIndex (Player 1=0, Player 2=1)
            //                 renderer 1 animates based on opponentChartIndex (for visual feedback)
            int characterStrumLineToAnimate = strumlineIdx;  // Default: same as renderer index
            if (_isMultiplayerMatch && strumlineCount >= 2)
            {
                if (i == 0)
                    characterStrumLineToAnimate = myChartIndex;  // Player renderer animates their chart's character
                else if (i == 1)
                    characterStrumLineToAnimate = opponentChartIndex;  // Opponent renderer animates opponent's character
            }
            
            _noteRenderers[i].OnNoteHit = (lane, holdEnd) =>
            {
                // Animate ALL visible characters on this strumline
                // This allows multiple characters with the same strumline to sing together
                for (int j = 0; j < objs.Count; j++)
                {
                    if ((int)objs[j].StrumLine != characterStrumLineToAnimate) continue;
                    var anim = _objAnimators[j];
                    if (anim == null) continue;
                    if (!anim.Visible || anim.Opacity <= 0.01f) continue;
                    
                    // Skip if this character's animation is currently controlled by an event
                    // Events have priority over note-driven animations
                    if (anim.IsSetByEvent)
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
                    if (_healthBar != null)
                        _healthBar.Health = MathHelper.Clamp(_healthBar.Health + 0.023f, 0f, 1f);
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
                if (_healthBar != null)
                    _healthBar.Health = MathHelper.Clamp(_healthBar.Health - 0.0475f, 0f, 1f);
                // Animate ALL visible characters on MY strumline
                // In multiplayer:
                //   Player 1 (plays chart 0/BF) animates characters with strumLine: 0
                //   Player 2 (plays chart 1/Enemy) animates characters with strumLine: 1
                int myAnimStrumline = 0;  // Default for single player
                if (_isMultiplayerMatch)
                {
                    // In multiplayer, animate based on which chart I'm playing
                    // Player 1 (role=1) plays chart 0 (BF) → strumLine 0
                    // Player 2 (role=2) plays chart 1 (Enemy) → strumLine 1
                    myAnimStrumline = myChartIndex;  // Use the chart we determined earlier
                }
                for (int j = 0; j < objs.Count; j++)
                {
                    if ((int)objs[j].StrumLine != myAnimStrumline) continue;
                    var anim = _objAnimators[j];
                    if (anim == null) continue;
                    if (!anim.Visible || anim.Opacity <= 0.01f) continue;
                    
                    // Skip if controlled by event
                    if (anim.IsSetByEvent)
                    {
                        Console.WriteLine($"[NoteMiss] t={_songTime:F2} Obj {j} ({objs[j].Spr}) - skipping miss anim (controlled by event)");
                        continue;
                    }
                    
                    int missAnim = anim.AnimCount > 5 ? 5 : anim.AnimCount - 1;
                    anim.SetAnimation(missAnim, setByEvent: false);
                }
                ShowJudgement("Miss", new Color(255, 80, 80));
            };

            // Wire judgement display - FNF style stacked popups (max 3)
            _noteRenderers[myStrumlineIndex].OnJudgement = j => 
            {
                // Add judgement popup using overlay
                if (_judgementOverlay != null)
                {
                    var hud = HudSettings.Instance;
                    _judgementOverlay.AddJudgement(j, new Point(hud.JudgementPosition.X, hud.JudgementPosition.Y));
                }
                
                // Multiplayer: Send hit to opponent (only for player strumline)
                if (_isMultiplayerMatch && _noteRenderers.Count > myStrumlineIndex)
                {
                    var playerRenderer = _noteRenderers[myStrumlineIndex];
                    int noteId = playerRenderer.Combo;
                    long timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
                    
                    Multiplayer.NetworkManager.Instance.SendNoteHit(
                        noteId,
                        0,  // Lane will be tracked from OnNoteHit callback
                        timestamp, 
                        j,  // rating string
                        playerRenderer.Combo
                    );
                }
            };
            
            // Wire combo changed event - spawn falling digits (FNF style)
            _noteRenderers[myStrumlineIndex].OnComboChanged = newCombo =>
            {
                if (newCombo <= 0) return;  // Don't show combo = 0
                if (_judgementOverlay == null) return;  // Safety check
                
                _lastCombo = newCombo;
                _comboTimer = COMBO_SHOW;
                
                // Show combo using overlay
                var hud = HudSettings.Instance;
                _judgementOverlay.ShowCombo(newCombo, new Point(hud.ComboPosition.X, hud.ComboPosition.Y));
            };
            
            // Wire key press callback for multiplayer real-time animation
            _noteRenderers[myStrumlineIndex].OnKeyPress = lane =>
            {
                if (_isMultiplayerMatch)
                {
                    Console.WriteLine($"[Game1] Sending KeyPress: lane={lane}, time={_songTime:F2}");
                    Multiplayer.NetworkManager.Instance.SendKeyPress(lane);
                }
            };
            
            // Wire note events — connect OnNotePressEvent for ALL strumlines
            // so action packs triggered by enemy notes also fire correctly
            foreach (var renderer in _noteRenderers)
            {
                var r = renderer;  // capture for lambda
                r.OnNotePressEvent = eventId => _events?.FireActionPack(eventId);
                r.OnNoteMissEvent  = eventId => _events?.FireActionPack(eventId);
            }

            // Fallback mechanics for notes WITHOUT onPressEvent.
            // If the level defines onPressEvent, it handles health via action_packs.
            // These callbacks only fire when no event is defined (legacy levels).
            _noteRenderers[myStrumlineIndex].OnDamageNoteHit = damage =>
            {
                if (NoteRenderer.Autoplay) return;
                if (_healthBar != null)
                    _healthBar.Health = MathHelper.Clamp(_healthBar.Health - damage, 0f, 1f);
                ShowJudgement("Damage!", new Color(255, 60, 60));
            };
            _noteRenderers[myStrumlineIndex].OnInstakillNoteHit = () =>
            {
                if (NoteRenderer.Autoplay) return;
                if (_healthBar != null)
                    _healthBar.Health = 0f;
                ShowJudgement("INSTAKILL", new Color(180, 0, 255));
            };
            _noteRenderers[myStrumlineIndex].OnHealNoteHit = () =>
            {
                if (_healthBar != null)
                    _healthBar.Health = MathHelper.Clamp(_healthBar.Health + 0.15f, 0f, 1f);
                ShowJudgement("Heal!", new Color(0, 230, 80));
            };
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

        // Wire BarIconChange — swap health bar icons mid-song
        _events.OnBarIconChange = (target, spriteName) =>
        {
            var newAnim = BuildIconAnimator(spriteName, texCache);
            if (newAnim == null || _healthBar == null) return;
            
            if (target == 0)
            {
                // Opponent icon changed
                _healthBar.OpponentIcon = newAnim;
                _healthBar.OpponentBarColor = SampleIconColor(newAnim) ?? new Color(0xFF, 0x00, 0x00);
                Console.WriteLine($"[BarIcon] Opponent icon changed to '{spriteName}' - Color: {_healthBar.OpponentBarColor}");
            }
            else
            {
                // Player icon changed
                _healthBar.PlayerIcon = newAnim;
                _healthBar.PlayerBarColor = SampleIconColor(newAnim) ?? new Color(0x66, 0xFF, 0x66);
                Console.WriteLine($"[BarIcon] Player icon changed to '{spriteName}' - Color: {_healthBar.PlayerBarColor}");
            }
        };

        // Wire PlayerHealth events
        _events.OnPlayerHealth = (delta, minHealth) =>
        {
            if (_healthBar != null)
            {
                float newHealth = MathHelper.Clamp(_healthBar.Health + delta, minHealth, 1f);
                _healthBar.Health = newHealth;
            }
        };
        _events.OnPlayerHealthSet = (targetHealth, minHealth) =>
        {
            // op=set: set health to exact value, clamped to [minHealth, 1]
            if (_healthBar != null)
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
        
        if (_level.MusicData != null)
        {
            _musicInst?.Dispose();
            _music?.Dispose();
            _musicPcm = null;

            // Decode .ogg with PCM data for seek capability
            _music = OggLoader.LoadWithPcm(_level.MusicData,
                out byte[] pcm, out int sampleRate, out int channels);
            
            _musicSampleRate = sampleRate;
            _musicChannels   = channels;
            _musicPcm        = pcm;
            
            _musicInst = _music.CreateInstance();
            _musicInst.Volume = MusicVolume;
            _musicInst.IsLooped = false;
        }

        Window.Title = $"Funky Maker Player — {_level.LevelName}";

        // Calculate song end time (last note start + duration, already in seconds)
        _songEndTime = 0f;
        foreach (var sl in _level.Data.Strumlines)
            foreach (var lane in sl)
                foreach (var note in lane)
                    _songEndTime = Math.Max(_songEndTime,
                        _level.Data.ToSeconds(note.Start) + _level.Data.ToSeconds(note.Duration));

        // Mark setup complete
        if (_loadingScreen != null)
        {
            _loadingScreen.LoadingProgress = 1.0f;
            _loadingScreen.CurrentStep = "Ready!";
            
            // Give time for the 100% progress to render before continuing
            // This ensures the user sees the complete progress bar
            System.Threading.Thread.Sleep(200);  // 200ms delay
        }
        
        _loadingComplete = true;
        }
        catch (Exception exSetup)
        {
            Console.WriteLine($"[SetupLevel] FATAL ERROR: {exSetup.Message}");
            Console.WriteLine($"[SetupLevel] Stack: {exSetup.StackTrace}");
            _errorMsg = $"Failed to setup level: {exSetup.Message}";
            _loadingComplete = true;
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

    // ── Update ───────────────────────────────────────────────────────────────
    protected override void Update(GameTime gameTime)
    {
        try
        {
            UpdateInternal(gameTime);
        }
        catch (Exception ex)
        {
            HandleGameplayError(ex, "Update");
        }
    }

    private void UpdateInternal(GameTime gameTime)
    {
        var kb  = Keyboard.GetState();
        
#if ANDROID
        // Merge touch controls with keyboard input (Android only)
        kb = MergeTouchWithKeyboard(kb);
#endif
        
#if ANDROID
        // Update touch controls
        _touchControls?.Update();
        
        // Auto-switch touch control mode based on game state
        if (_touchControls != null)
        {
            bool inGameplay = _level != null && _started && !_inPauseMenu && 
                             !_inFreeplay && !(_settingsManager?.IsActive ?? false) &&
                             !(_arrowsMenu?.IsActive ?? false) && !(_splashMenu?.IsActive ?? false) &&
                             !(_countdownMenu?.IsActive ?? false) && !(_fontMenu?.IsActive ?? false) &&
                             !_inMultiplayer && !_inMainSettings && !_inHudEditor;
            
            var targetMode = inGameplay ? TouchControls.ControlMode.Gameplay : TouchControls.ControlMode.Menu;
            if (_touchControls.GetMode() != targetMode)
            {
                _touchControls.SetMode(targetMode);
            }
            
            // Hide pause button in multiplayer
            if (_isMultiplayerMatch)
            {
                _touchControls.SetHidePauseButton(true);
            }
        }
        
        // Merge touch input into keyboard state for seamless integration
        kb = MergeTouchWithKeyboard(kb);
#endif
        
        float dt = Math.Min((float)gameTime.ElapsedGameTime.TotalSeconds, 0.1f);  // clamp to 100ms max to prevent camera jumps on first frame
        _lastDt  = gameTime.ElapsedGameTime.TotalSeconds;

        // ESC opens pause menu (or settings if no level loaded)
        // Skip if in arrows or splash menu (they handle ESC themselves)
        // P key also pauses (for touch controls)
        bool escPressedForPause = kb.IsKeyDown(Keys.Escape) && !_prevKb.IsKeyDown(Keys.Escape);
        bool pPressed = kb.IsKeyDown(Keys.P) && !_prevKb.IsKeyDown(Keys.P);
        
        if ((escPressedForPause || pPressed) && 
            !(_arrowsMenu?.IsActive ?? false) && !(_splashMenu?.IsActive ?? false) &&
            !(_countdownMenu?.IsActive ?? false) && !(_fontMenu?.IsActive ?? false))
        {
            // If on error screen, ESC/Enter goes back to freeplay
            if (_errorMsg != null && _level == null)
            {
                _errorMsg   = null;
                _musicInst?.Stop();  // Stop any background music
                _inFreeplay = true;
                ResetWindowTitle();  // Reset title when returning to menu
                _freeplay?.Refresh();
                _freeplay?.ResetInputCooldown();
                _prevKb = kb;
                base.Update(gameTime);
                return;
            }

            // Disable pause during multiplayer matches
            if (_isMultiplayerMatch && _level != null)
            {
                Console.WriteLine("[Game1] Pause disabled during multiplayer match");
            }
            else if (_level == null)
            {
                // No level loaded - ESC = settings (credits handled inside FreeplayMenu)
                if (!_inFreeplay && _settingsManager != null)
                    _settingsManager.IsActive = !_settingsManager.IsActive;
            }
            else if (_settingsManager?.IsActive == true)
            {
                // In settings - go back to pause menu (disabled in multiplayer)
                if (_settingsManager != null) _settingsManager.IsActive = false;
#if ANDROID
                // Hide Q and E buttons when leaving settings
                _touchControls?.SetSettingsButtonsVisible(false);
#endif
                if (!_isMultiplayerMatch)
                {
                    _inPauseMenu = true;
                    // Never auto-resume when going back to pause menu — user stays paused
                    _pausedByTimeskip = false;
                }
            }
            else if (!_isMultiplayerMatch)  // Pause only allowed in single player
            {
                // Toggle pause menu
                _inPauseMenu = !_inPauseMenu;
                if (_inPauseMenu && _started && !_paused)
                    PauseSong();
                else if (!_inPauseMenu && _paused)
                    ResumeSong();
            }
        }

        // ── Pause menu ───────────────────────────────────────────────────────
        if (_inPauseMenu && _level != null)
        if (_pauseMenu != null)
        {
            // Handle input and get action
            var action = _pauseMenu.HandleInput(kb, _prevKb);
            
            switch (action)
            {
                case PauseMenu.PauseAction.Resume:
                    _inPauseMenu = false;
                    ResumeSong();
                    break;
                    
                case PauseMenu.PauseAction.RestartSong:
                    RestartSong();
                    _inPauseMenu = false;
                    break;
                    
                case PauseMenu.PauseAction.OpenSettings:
                    _inPauseMenu = false;
                    if (_settingsManager != null) _settingsManager.IsActive = true;
                    break;
                    
                case PauseMenu.PauseAction.ExitToMenu:
                    _musicInst?.Stop();
                    _started = false;
                    
                    // Clean up gameplay state before returning to menu
                    _level = null;
                    
                    _inFreeplay = true;
                    _inPauseMenu = false;
                    ResetWindowTitle();  // Reset title when returning to menu
                    _freeplay?.Refresh();
                    _freeplay?.ResetInputCooldown();
                    break;
            }
            
            _prevKb = kb;
            base.Update(gameTime);
            return;
        }

        // ── Arrows menu ──────────────────────────────────────────────────────
        if (_arrowsMenu?.IsActive == true)
        {
            string? assetsRoot = FindAssetsRoot();
            _arrowsMenu.Update(kb, _prevKb, GraphicsDevice, assetsRoot);
            
            // If menu was closed and we selected a style, apply it
            if (!_arrowsMenu.IsActive)
            {
                // Apply to all note renderers
                if (assetsRoot != null)
                {
                    _noteStyle = NoteStyle.LoadByName(NoteStyleName, assetsRoot, GraphicsDevice);
                    foreach (var r in _noteRenderers)
                        r.SetStyle(_noteStyle);
                }
                Settings.Save(_graphics.SynchronizeWithVerticalRetrace);
                if (_settingsManager != null) _settingsManager.IsActive = true;
            }
            
            _prevKb = kb;
            base.Update(gameTime);
            return;
        }

        // ── NoteSplash menu ──────────────────────────────────────────────────
        if (_splashMenu?.IsActive == true)
        {
            _splashMenu.Update(kb, _prevKb, (float)gameTime.ElapsedGameTime.TotalSeconds, GraphicsDevice, _noteRenderers);
            
            // If menu was closed, reopen settings
            if (!_splashMenu.IsActive && _settingsManager != null)
                _settingsManager.IsActive = true;
            
            _prevKb = kb;
            base.Update(gameTime);
            return;
        }

        // ── Countdown menu ───────────────────────────────────────────────────
        if (_countdownMenu?.IsActive == true)
        {
            string? assetsRoot = FindAssetsRoot();
            _countdownMenu.Update(kb, _prevKb, GraphicsDevice, assetsRoot);
            
            // If menu was closed, reopen settings
            if (!_countdownMenu.IsActive && _settingsManager != null)
                _settingsManager.IsActive = true;
            
            _prevKb = kb;
            base.Update(gameTime);
            return;
        }

        // ── Font menu ────────────────────────────────────────────────────────
        if (_fontMenu?.IsActive == true)
        {
            _fontMenu.Update(kb, _prevKb, GraphicsDevice);
            
            // If menu was closed, check if we need to reload font
            if (!_fontMenu.IsActive)
            {
                string newFontPath = _fontMenu.GetSelectedFontPath();
                if (!string.IsNullOrEmpty(newFontPath) && System.IO.File.Exists(newFontPath))
                {
                    try
                    {
                        _fontRenderer = new FontRenderer(GraphicsDevice, newFontPath);
                        Console.WriteLine($"[Game1] Font changed to: {newFontPath}");
                    }
                    catch (Exception ex)
                    {
                        Console.WriteLine($"[Game1] Error loading font: {ex.Message}");
                    }
                }
                
                if (_settingsManager != null)
                    _settingsManager.IsActive = true;
            }
            
            _prevKb = kb;
            base.Update(gameTime);
            return;
        }

        // ── Settings menu ────────────────────────────────────────────────────
        if (_settingsManager?.IsActive == true && _settingsManager != null)
        {
#if ANDROID
            // Show Q and E buttons for tab navigation on Android
            _touchControls?.SetSettingsButtonsVisible(true);
#endif
            
            // Tab navigation with Q/E
            if (kb.IsKeyDown(Keys.Q) && !_prevKb.IsKeyDown(Keys.Q))
                _settingsManager.PrevTab();
            
            if (kb.IsKeyDown(Keys.E) && !_prevKb.IsKeyDown(Keys.E))
                _settingsManager.NextTab();
            
            // Navigation
            int itemCount = _settingsManager.GetItemCount();
            if (kb.IsKeyDown(Keys.Up) && !_prevKb.IsKeyDown(Keys.Up))
                _settingsManager.Selection = (_settingsManager.Selection - 1 + itemCount) % itemCount;
            
            if (kb.IsKeyDown(Keys.Down) && !_prevKb.IsKeyDown(Keys.Down))
                _settingsManager.Selection = (_settingsManager.Selection + 1) % itemCount;
            
            // Selection with Enter or Space
            if ((kb.IsKeyDown(Keys.Enter) && !_prevKb.IsKeyDown(Keys.Enter)) ||
                (kb.IsKeyDown(Keys.Space) && !_prevKb.IsKeyDown(Keys.Space)))
            {
                var result = _settingsManager.HandleSelectAction(this);
                
                // Apply side effects based on result
                if (result.NeedsStrumlineReposition && _level != null)
                    RepositionStrumlines();
                
                if (result.NeedsCameraReset)
                    _cameraManager?.ResetScaleOverride();
                
                if (result.OpenKeyBind)
                {
                    _keyBindOverlay.IsActive = true;
                    _keyBindOverlay.OnComplete += () => {
                        if (_settingsManager != null) _settingsManager.IsActive = true;
                    };
                    _keyBindOverlay.OnCancel += () => {
                        if (_settingsManager != null) _settingsManager.IsActive = true;
                    };
                }
                
                if (result.OpenArrowsMenu)
                {
                    string? assetsRoot = FindAssetsRoot();
                    _arrowsMenu?.Open(GraphicsDevice, assetsRoot);
                }
                
                if (result.OpenSplashMenu)
                    _splashMenu?.Open(GraphicsDevice);
                
                if (result.OpenCountdownMenu)
                {
                    string? assetsRoot = FindAssetsRoot();
                    _countdownMenu?.Open(GraphicsDevice, assetsRoot);
                }
                
                if (result.OpenFontMenu)
                {
                    string? assetsRoot = FindAssetsRoot();
                    _fontMenu?.Open(GraphicsDevice, assetsRoot);
                }
                
                if (result.ExitMenu)
                {
#if ANDROID
                    // Hide Q and E buttons when exiting settings menu
                    _touchControls?.SetSettingsButtonsVisible(false);
#endif
                    // Return to pause menu only if not in multiplayer
                    if (_level != null && !_isMultiplayerMatch)
                        _inPauseMenu = true;
                    if (_pausedByTimeskip && _paused && _started && _level != null)
                    {
                        ResumeSong();
                        _pausedByTimeskip = false;
                    }
                }
                
                if (result.ToggleVSync)
                {
                    _graphics.SynchronizeWithVerticalRetrace = !_graphics.SynchronizeWithVerticalRetrace;
                    _graphics.ApplyChanges();
                }
                
                if (result.ToggleFullscreen)
                {
                    _isFullscreen = !_isFullscreen;
                    _graphics.HardwareModeSwitch = false;
                    _graphics.IsFullScreen = _isFullscreen;
                    if (_isFullscreen)
                    {
                        var dm = GraphicsDevice.Adapter.CurrentDisplayMode;
                        _graphics.PreferredBackBufferWidth  = dm.Width;
                        _graphics.PreferredBackBufferHeight = dm.Height;
                    }
                    else
                    {
                        _graphics.PreferredBackBufferWidth  = 1280;
                        _graphics.PreferredBackBufferHeight = 720;
                    }
                    _graphics.ApplyChanges();
                }
                
                if (result.NeedsStrumlineAlphaUpdate)
                {
                    for (int si = 2; si < _noteRenderers.Count; si++)
                        _noteRenderers[si].Alpha = HideExtraStrumlines ? 0f : 1f;
                }
                
                if (result.NeedsOpponentAlphaUpdate && _noteRenderers.Count > 1)
                {
                    _noteRenderers[1].Alpha = (HideOpponentStrumline && !_isMultiplayerMatch) ? 0f : _notesAlpha;
                }
                
                if (result.NeedsSave)
                    Settings.Save(_graphics.SynchronizeWithVerticalRetrace);
            }
            
            // Left/Right adjustments for various settings
            bool leftPressed = kb.IsKeyDown(Keys.Left) && !_prevKb.IsKeyDown(Keys.Left);
            bool rightPressed = kb.IsKeyDown(Keys.Right) && !_prevKb.IsKeyDown(Keys.Right);
            
            if (leftPressed || rightPressed)
            {
                var result = _settingsManager.HandleAdjustment(this, leftPressed);
                
                // Apply side effects
                if (result.AdjustCameraZoom != 0 && _events != null && !_events.HasCameraEvents)
                {
                    if (result.AdjustCameraZoom < 0)
                        _cameraManager?.IncreaseZoom(_events.Camera.Scale);
                    else
                        _cameraManager?.DecreaseZoom(_events.Camera.Scale);
                }
                
                if (result.AdjustVolume != 0f)
                {
                    MusicVolume = MathF.Round(Math.Clamp(MusicVolume + result.AdjustVolume * 0.05f, 0f, 1f), 2);
                    if (_musicInst != null) _musicInst.Volume = MusicVolume;
                }
                
                if (result.SeekSong != 0f && _started)
                {
                    SeekSong(_songTime + result.SeekSong);
                    if (!_paused)
                    {
                        PauseSong();
                        _pausedByTimeskip = true;
                    }
                }
                
                if (result.NeedsSave)
                    Settings.Save(_graphics.SynchronizeWithVerticalRetrace);
            }
            _prevKb = kb;
            base.Update(gameTime);
            return;
        }

        // ── Freeplay menu ────────────────────────────────────────────────────
        if (_inFreeplay && _freeplay != null && _loadingScreen == null)
        {
            #if ANDROID
            // Update mods help screen if visible
            if (_modsHelpScreen != null && _modsHelpScreen.IsVisible)
            {
                _modsHelpScreen.Update(gameTime);
                
                // If user requested to launch folder picker
                if (_modsHelpScreen.LaunchFolderPicker)
                {
                    PickAndroidModsFolder();
                }
                
                // If user closes help screen, return to freeplay
                if (!_modsHelpScreen.IsVisible)
                {
                    _freeplay.ResetInputCooldown();
                    // Show touch controls again
                    _touchControls?.SetAllControlsVisible(true);
                }
                
                _prevKb = kb;
                base.Update(gameTime);
                return;
            }
            #endif
            
            var winBounds = _graphics.GraphicsDevice.Viewport.Bounds;
            _freeplay.Update(dt, kb, _rtWidth, _rtHeight, winBounds.Width, winBounds.Height);
            if (_freeplay.LevelChosen)
            {
                _inFreeplay = false;
                StartLoadingLevel(_freeplay.LevelPath);
                _freeplay.LevelChosen = false;
            }
            // Check if user selected HUD Editor
            if (_freeplay.MenuAction == 1)
            {
                _inFreeplay = false;
                _inMainSettings = true;
                _mainSettingsMenu?.ResetInputCooldown(); // Prevent immediate ENTER detection
                _freeplay.MenuAction = -1;
            }
            // Check if user selected Multiplayer
            if (_freeplay.MenuAction == 3)
            {
                _inFreeplay = false;
                _inMultiplayer = true;
                _multiplayerMenu?.ResetInputCooldown();
                _freeplay.MenuAction = -1;
            }
            #if ANDROID
            // Check if user selected IMPORT FILE (Android only)
            if (_freeplay.MenuAction == 4)
            {
                OpenAndroidFilePicker();
                _freeplay.MenuAction = -1;
            }
            // Check if user selected OPEN MODS (Android only)
            if (_freeplay.MenuAction == 5)
            {
                OpenAndroidModsFolder();
                _freeplay.MenuAction = -1;
            }
            // Check if user selected GRANT FOLDER ACCESS (Android only)
            if (_freeplay.MenuAction == 6)
            {
                // Show help screen first
                _modsHelpScreen?.Show();
                _freeplay.MenuAction = -1;
                
                // Hide touch controls while help screen is visible
                _touchControls?.SetAllControlsVisible(false);
            }
            #endif
            
            // Update song transition if active (stickers fading out over menu)
            if (_songTransition != null)
            {
                var prevPhase = _songTransition.CurrentPhase;
                _songTransition.Update((float)gameTime.ElapsedGameTime.TotalSeconds);
                
                // When stickers finish fading out completely, clean up transition
                if (_songTransition.CurrentPhase == SongTransition.Phase.Done)
                {
                    _songTransition = null;
                }
            }
            
            _prevKb = kb;
            base.Update(gameTime);
            return;
        }
        
        // ── Main Settings menu ───────────────────────────────────────────────
        if (_inMainSettings && _mainSettingsMenu != null)
        {
            _mainSettingsMenu.Update(dt, _rtWidth, _rtHeight);
            
            if (_mainSettingsMenu.BackPressed)
            {
                _inMainSettings = false;
                _musicInst?.Stop();  // Stop any background music
                _inFreeplay = true;
                ResetWindowTitle();  // Reset title when returning to menu
                _freeplay?.ResetInputCooldown();
            }
            
            if (_mainSettingsMenu.OpenHudEditor)
            {
                // Force reload settings from disk to ensure we have the latest values
                HudSettings.ForceReload();
                
                _hudEditor = new HudEditor(_pixel!, _font!, _rtWidth, _rtHeight, GraphicsDevice);
                _inHudEditor = true;
                _inMainSettings = false;
            }
            
            _prevKb = kb;
            base.Update(gameTime);
            return;
        }
        
        // ── HUD Editor ───────────────────────────────────────────────────────
        if (_inHudEditor && _hudEditor != null)
        {
            _hudEditor.Update(dt, _rtWidth, _rtHeight);
            
            if (_hudEditor.BackPressed)
            {
                _inHudEditor = false;
                _inMainSettings = true;
                _mainSettingsMenu?.ResetInputCooldown(); // Prevent immediate ENTER detection when returning
                _hudEditor = null; // Clean up
                
                // Don't reload settings here - the singleton already has the current state
                // If the user saved (S key), settings are on disk
                // If they didn't save, their edits are preserved in memory until they decide to save or reset
            }
            
            // Don't update _prevKb here - let HudEditor handle it
            base.Update(gameTime);
            return;
        }

        // ── Multiplayer menu ─────────────────────────────────────────────────
        if (_inMultiplayer && _multiplayerMenu != null)
        {
            _multiplayerMenu.Update(gameTime);
            
            // Check if returning to freeplay
            if (_multiplayerMenu.ShouldExitToFreeplay())
            {
                _inMultiplayer = false;
                _musicInst?.Stop();  // Stop any background music
                _inFreeplay = true;
                ResetWindowTitle();  // Reset title when returning to menu
                _freeplay?.ResetInputCooldown();
                _multiplayerMenu.ResetExitFlag();
            }
            
            // NOTE: GameMultiplayer is now launched directly by MultiplayerMenu after countdown
            // No need to check for game start here anymore
            
            // Update song transition if active (stickers fading out over menu)
            if (_songTransition != null)
            {
                var prevPhase = _songTransition.CurrentPhase;
                _songTransition.Update((float)gameTime.ElapsedGameTime.TotalSeconds);
                
                // When stickers finish fading out completely, clean up transition
                if (_songTransition.CurrentPhase == SongTransition.Phase.Done)
                {
                    _songTransition = null;
                }
            }
            
            _prevKb = kb;
            base.Update(gameTime);
            return;
        }

        // P toggles pause (disabled during multiplayer matches)
        if (kb.IsKeyDown(Keys.P) && !_prevKb.IsKeyDown(Keys.P))
        {
            if (_isMultiplayerMatch)
            {
                Console.WriteLine("[Game1] Pause disabled during multiplayer match");
            }
            else
            {
                if (_paused) ResumeSong(); else PauseSong();
            }
        }

        // Ctrl+Right = timeskip forward, Ctrl+Left = timeskip backward (if enabled in settings)
        bool ctrlHeld = kb.IsKeyDown(Keys.LeftControl) || kb.IsKeyDown(Keys.RightControl);
        if (ctrlHeld && TimeskipEnabled && _started && !_paused)
        {
            if (kb.IsKeyDown(Keys.Right) && !_prevKb.IsKeyDown(Keys.Right))
                SeekSong(_songTime + TimeskipSeconds);
            if (kb.IsKeyDown(Keys.Left) && !_prevKb.IsKeyDown(Keys.Left))
                SeekSong(_songTime - TimeskipSeconds);
        }

        // PageUp/PageDown adjust visual scroll speed (no timing impact)
        // Also support numpad + / - and regular + / -
        bool plusPressed =
            (kb.IsKeyDown(Keys.PageUp)    && !_prevKb.IsKeyDown(Keys.PageUp))    ||
            (kb.IsKeyDown(Keys.Add)       && !_prevKb.IsKeyDown(Keys.Add))       ||
            (kb.IsKeyDown(Keys.OemPlus)   && !_prevKb.IsKeyDown(Keys.OemPlus));
        bool minusPressed =
            (kb.IsKeyDown(Keys.PageDown)  && !_prevKb.IsKeyDown(Keys.PageDown))  ||
            (kb.IsKeyDown(Keys.Subtract)  && !_prevKb.IsKeyDown(Keys.Subtract))  ||
            (kb.IsKeyDown(Keys.OemMinus)  && !_prevKb.IsKeyDown(Keys.OemMinus));

        if (plusPressed)
            NoteRenderer.ScrollMultiplier = MathF.Round(Math.Min(5.0f, NoteRenderer.ScrollMultiplier + 0.25f), 2);
        if (minusPressed)
            NoteRenderer.ScrollMultiplier = MathF.Round(Math.Max(0.25f, NoteRenderer.ScrollMultiplier - 0.25f), 2);

        // Key binding: delegate to overlay
        _keyBindOverlay.Update(kb, _prevKb);
        if (_keyBindOverlay.IsActive)
        {
            _prevKb = kb;
            base.Update(gameTime);
            return;
        }

        // ── Loading screen update (runs regardless of _level state) ─────────
        if (_loadingScreen != null)
        {
            // Finalize on main thread when background load is done
            if (_loadState == LoadState.Finalizing)
            {
                if (_pendingLevel != null)
                {
                    // Regular FNMM level
                    _level        = _pendingLevel;
                    _pendingLevel = null;
                    _loadState    = LoadState.Done;
                    try
                    {
                        SetupLevel();
                        _songTime        = 0f;
                        _songBeats       = 0f;
                        _started         = false;
                        _paused          = false;
                        if (_healthBar != null)
                            _healthBar.Health = 0.5f;
                        _gameOverOverlay?.Hide();
                        _countdownOverlay?.Reset();
                        _judgementText   = null;
                        
                        // Start FNF-style receptor intro animation (pop-in effect)
                        // Only if HUD is visible (respects chart events that hide receptors)
                        if (_events?.HudVisible != false)
                        {
                            foreach (var renderer in _noteRenderers)
                            {
                                renderer?.StartReceptorIntro(0.0f, 0.05f);
                            }
                        }
                        
                        // Reset camera manager
                        _cameraManager?.Reset();
                        
                        // Reset icon bounce
                        _healthBar?.ResetBounce();
                        
                        _songStopwatch.Reset();
                        _pausedTime      = 0.0;
                        
                        // Update loading screen with real level name
                        // In multiplayer, show loading screen normally and send Ready when done
                        _loadingScreen = new LoadingScreen(
                            _level.LevelName, _pixel!, _font,
                            FindAssetsRoot(), GraphicsDevice, _isMultiplayerMatch);
                        _loadingComplete = true;
                        
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
                        
                        // Multiplayer: Send Ready signal to server now that level is loaded
                        if (_isMultiplayerMatch)
                        {
                            Console.WriteLine("[Game1] Level loaded! Sending Ready to server...");
                            Multiplayer.NetworkManager.Instance.SendReady(true);
                            
                            // Update MY ready state in LoadingScreen
                            if (_loadingScreen != null)
                            {
                                if (_multiplayerPlayerRole == 1)
                                {
                                    _loadingScreen.Player1Ready = true;  // I'm Player 1, so I'm ready
                                    Console.WriteLine("[Game1] Set Player1Ready=true (I am Player 1)");
                                }
                                else if (_multiplayerPlayerRole == 2)
                                {
                                    _loadingScreen.Player2Ready = true;  // I'm Player 2, so I'm ready
                                    Console.WriteLine("[Game1] Set Player2Ready=true (I am Player 2)");
                                }
                                
                                // Apply stored opponent ready state if it was received before LoadingScreen existed
                                if (_opponentReadyState.HasValue)
                                {
                                    Console.WriteLine($"[Game1] Applying stored opponent ready state: {_opponentReadyState.Value}");
                                    if (_multiplayerPlayerRole == 1)
                                    {
                                        _loadingScreen.Player2Ready = _opponentReadyState.Value;
                                        Console.WriteLine($"[Game1] Set Player2Ready={_opponentReadyState.Value} (from stored state)");
                                    }
                                    else if (_multiplayerPlayerRole == 2)
                                    {
                                        _loadingScreen.Player1Ready = _opponentReadyState.Value;
                                        Console.WriteLine($"[Game1] Set Player1Ready={_opponentReadyState.Value} (from stored state)");
                                    }
                                }
                            }
                            
                            Console.WriteLine("[Game1] Waiting for opponent to be ready...");
                        }
                    }
                    catch (Exception ex)
                    {
                        _errorMsg        = ex.Message;
                        _loadingComplete = true;
                        _loadState       = LoadState.Done;
                        // Keep loading screen alive so the error can be shown to the user.
                        // The loading screen will transition to error state and let user press ESC/Enter.
                        // Don't null out _loadingScreen here — DrawError handles it via _errorMsg.
                        _loadingScreen   = null;  // clear loading screen, error shown via DrawError
                        _level           = null;  // clear level so DrawError path is taken
                        Console.Error.WriteLine($"[FunkyMakerPlayer] SetupLevel failed: {ex}");
                        // Don't go to freeplay immediately — show error screen first
                        // User can press ESC or Enter to go back
                    }
                }
            }

            bool enterPressed = kb.IsKeyDown(Keys.Enter) && !_prevKb.IsKeyDown(Keys.Enter);
            
            // Debug log for countdown signal in multiplayer
            if (_isMultiplayerMatch && _loadingScreen != null && _loadingScreen.CurrentPhase == LoadingScreen.Phase.Ready)
            {
                Console.WriteLine($"[Game1] LoadingScreen Phase=Ready: countdown={_countdownSignalReceived}, P1={_loadingScreen.Player1Ready}, P2={_loadingScreen.Player2Ready}");
            }
            
            _loadingScreen?.Update(dt, _loadingComplete && _loadState == LoadState.Done, enterPressed, _rtWidth, _rtHeight, _countdownSignalReceived);
            if (_loadingScreen?.CurrentPhase == LoadingScreen.Phase.Done)
            {
                _loadingScreen = null;
                // Cats cleared — countdown starts naturally next frame (_started is false)
            }
            _prevKb = kb;
            base.Update(gameTime);
            return;
        }

        // ── Results Screen (Multiplayer) ───────────────────────────────────
        if (_resultsScreen != null)
        {
            _resultsScreen.Update(dt, kb, _prevKb);
            
            if (_resultsScreen.CurrentPhase == MultiplayerResultsScreen.Phase.Done)
            {
                // Results screen finished - clean up multiplayer state and return to menu
                Console.WriteLine("[Game1] Results screen done - cleaning up multiplayer state");
                
                // Clear multiplayer state
                _resultsScreen = null;
                _isMultiplayerMatch = false;
                _multiplayerPlayerRole = 0;
                _multiplayerStartTimestamp = 0;
                _countdownSignalReceived = false;
                _opponentName = null;
                _opponentScore = 0;
                _opponentHealth = 0.5f;
                _opponentReadyState = null;
                _stateUpdateTimer = 0f;
                
                // Disconnect from match (but stay connected to server)
                Console.WriteLine("[Game1] Player left match, returning to multiplayer menu");
                
                // Trigger transition back to multiplayer menu
                _songTransition = new SongTransition(_catTex, _pixel!);
                _songTransition.Start(_rtWidth, _rtHeight);
                _transitionToFreeplay = false;  // Go to multiplayer menu, not freeplay
            }
            
            _prevKb = kb;
            base.Update(gameTime);
            return;
        }

        if (_level != null && _errorMsg == null)
        {
            // ── Game over ────────────────────────────────────────────────────
            // In multiplayer, skip game over entirely (continue until song ends)
            if (_gameOverOverlay != null && _gameOverOverlay.IsActive && !_isMultiplayerMatch)
            {
                var decision = _gameOverOverlay.Update(dt, kb, _prevKb);
                
                if (decision == true)  // Retry
                {
                    RestartSong();
                }
                else if (decision == false)  // Back to menu
                {
                    _musicInst?.Stop();
                    _started = false;
                    _freeplay?.ResetInputCooldown();
                    _songTransition = new SongTransition(_catTex, _pixel!);
                    _songTransition.Start(_rtWidth, _rtHeight);
                    _transitionToFreeplay = true;
                }
                
                _prevKb = kb;
                base.Update(gameTime);
                return;
            }
            
            // Safety: Hide game over if somehow triggered in multiplayer
            if (_gameOverOverlay != null && _gameOverOverlay.IsActive && _isMultiplayerMatch)
            {
                _gameOverOverlay.Hide();
                Console.WriteLine("[Game1] Game over overlay hidden (multiplayer mode)");
            }

            // ── Countdown before music ───────────────────────────────────────
            if (!_started)
            {
                // Multiplayer: Skip countdown, start immediately at synchronized timestamp
                if (_isMultiplayerMatch && _multiplayerStartTimestamp > 0)
                {
                    // Use synchronized server time, NOT local client time
                    long now = Multiplayer.TimeSyncManager.Instance.GetServerTime();
                    
                    // Calculate time until start with microsecond precision
                    long timeUntilStart = _multiplayerStartTimestamp - now;
                    
                    if (timeUntilStart <= 0)
                    {
                        // Time has passed - start immediately
                        Console.WriteLine($"[Game1] 🚀 Multiplayer: Starting NOW! (delay: {-timeUntilStart}ms)");
                        _countdownOverlay?.MarkAsDone();
                        _started       = true;
                        _musicInst?.Play();
                        
                        // Update Discord RPC for multiplayer
                        string levelName = System.IO.Path.GetFileNameWithoutExtension(FnmmPath);
                        DiscordRpc.SetPlayingMultiplayer(levelName);
                        _songStopwatch.Restart();
                        _pausedTime = 0.0;
                        _songTime = 0f;
                        _songBeats = 0f;

                    }
                    else if (timeUntilStart <= 50)
                    {
                        // Within 50ms of start time - pre-start the music so it's ready
                        // This compensates for audio latency and frame timing
                        Console.WriteLine($"[Game1] [♪] Multiplayer: Pre-starting music ({timeUntilStart}ms early for sync)");
                        _countdownOverlay?.MarkAsDone();
                        _started       = true;
                        _musicInst?.Play();
                        
                        // Update Discord RPC for multiplayer
                        string levelName = System.IO.Path.GetFileNameWithoutExtension(FnmmPath);
                        DiscordRpc.SetPlayingMultiplayer(levelName);
                        _songStopwatch.Restart();
                        _pausedTime = 0.0;
                        _songTime = 0f;
                        _songBeats = 0f;

                    }
                    else
                    {
                        // Wait for synchronized start time
                        float waitTime = timeUntilStart / 1000.0f;
                        
                        // Only log every ~300ms to avoid spam
                        if ((int)(now / 300) != (int)((now - 16) / 300))
                        {
                            Console.WriteLine($"[Game1] ⏳ Multiplayer: Waiting {waitTime:F3}s for synchronized start...");
                        }
                    }
                }
                else
                {
                    // Normal single-player countdown
                    if (_countdownOverlay != null)
                    {
                        bool countdownComplete = _countdownOverlay.Update(dt, _level.Data.EffectiveBpm);
                        
                        // songTime is negative during countdown, reaches 0 when music starts
                        _songTime = _countdownOverlay.GetSongTime(_level.Data.EffectiveBpm);
                        _songBeats = _songTime / 60f * _level.Data.EffectiveBpm;

                        if (countdownComplete && !_started)
                        {
                            _started = true;
                            _musicInst?.Play();
                            
                            // Update Discord RPC
                            string levelName = System.IO.Path.GetFileNameWithoutExtension(FnmmPath);
                            if (_isMultiplayerMatch)
                                DiscordRpc.SetPlayingMultiplayer(levelName);
                            else
                                DiscordRpc.SetPlaying(levelName);
                            _songStopwatch.Restart();
                            _pausedTime = 0.0;
                        }
                    }
                }
            }
            else
            {
                // ── High-precision timing using Stopwatch ────────────────────
                // Stopwatch starts at 0 when music starts, so _songTime = real music position.
                if (!_paused)
                {
                    double elapsedSeconds = _songStopwatch.Elapsed.TotalSeconds - _pausedTime;
                    _songTime = (float)elapsedSeconds;
                }
                // If paused, _songTime stays frozen

                _songBeats = _songTime / 60f * _level.Data.EffectiveBpm;
                
                // ── Camera zoom on beat (FNF style) ──────────────────────────
                int currentBeat = (int)Math.Floor(_songBeats);
                _cameraManager?.UpdateBeatZoom(currentBeat, dt);
                
                // Update Extended Camera (for scrollFactor parallax support)
                if (_cameraManager?.UseExtendedCamera == true && _events?.Camera != null)
                {
                    var winBounds = Window.ClientBounds;
                    _cameraManager.UpdateExtendedCamera(_events.Camera, _rtWidth, _rtHeight, _events.HasCameraEvents);
                }
                
                // Audio recovery: if music stopped unexpectedly, try to restart
                if (_musicInst?.State == SoundState.Stopped && _songTime > 0.5f)
                {
                    Console.WriteLine($"[Audio Recovery] Music stopped at {_songTime:F2}s, restarting...");
                    if (_audioDeviceReset)
                    {
                        // Device changed → fully reload
                        Console.WriteLine("[Audio Recovery] Device reset detected, reloading music...");
                        if (_level?.MusicData != null)
                        {
                            _musicInst?.Dispose();
                            _music?.Dispose();
                            
                            _music = OggLoader.LoadWithPcm(_level.MusicData,
                                out byte[] pcm, out int sr, out int ch);
                            
                            _musicSampleRate = sr;
                            _musicChannels = ch;
                            _musicPcm = pcm;
                            
                            _musicInst = _music.CreateInstance();
                            _musicInst.Volume = MusicVolume;
                            _musicInst.IsLooped = false;
                        }
                        _audioDeviceReset = false;
                    }
                    // Seek to current position and resume
                    if (_musicPcm != null)
                    {
                        var seeked = OggLoader.SeekPcm(_musicPcm, _musicSampleRate, _musicChannels, _songTime);
                        if (seeked != null)
                        {
                            _musicInst?.Dispose();
                            _music?.Dispose();
                            _music = seeked;
                            _musicInst = _music.CreateInstance();
                            _musicInst.Volume = MusicVolume;
                            _musicInst.IsLooped = false;
                            if (!_paused) _musicInst.Play();
                        }
                    }
                }
            }

            // ── Judgement timer ──────────────────────────────────────────────
            if (_judgementTimer > 0f)
                _judgementTimer -= dt;
            
            // ── Combo timer ──────────────────────────────────────────────────
            if (_noteRenderers.Count > 0 && _comboTimer > 0f)
                _comboTimer -= dt;
            
            // ── Update judgement overlay (popups and combo digits) ───────────
            _judgementOverlay?.Update(dt);

            // ── Note renderers (fires OnNoteHit → SetAnimation + NotifyNoteHit) ──
            foreach (var renderer in _noteRenderers)
                renderer.Update(_songTime, dt, kb, _prevKb);
            
            // ── Update particle notesplash system (if active) ────────────────
            _particleNoteSplash?.Update(gameTime);

            // ── Events and Animators: Only update AFTER countdown finishes ──
            // This prevents background animations and events from triggering during countdown
            if (_started)
            {
                // ── Events (CameraSet uses updated NotifyNoteHit target from above) ──
                _events?.Update(_songTime, dt);

                // ── Animators (decrements sing timers, advances frames) ──────
                foreach (var anim in _objAnimators)
                    anim?.Update(dt, _songTime);
                
                // Update health bar (icons, beat bounce, etc.)
                _healthBar?.Update(dt, _songTime, _level.Data.EffectiveBpm);
            }

            // ── Notes alpha fade (GUI event HudVisible controls arrows visibility) ──
            float notesTarget = (_events?.HudVisible ?? true) ? 1.0f : 0.0f;
            _notesAlpha = MathHelper.Lerp(_notesAlpha, notesTarget,
                1f - MathF.Exp(-NOTES_FADE_SPEED * dt));

            // ── Health checks ────────────────────────────────────────────────
            // Disable game over in multiplayer (continue until song ends)
            if (_healthBar != null && _healthBar.Health <= 0f && _gameOverOverlay != null && !_gameOverOverlay.IsActive && !_isMultiplayerMatch)
            {
                _gameOverOverlay.Show();
                _musicInst?.Stop();
                Console.WriteLine("[Game1] Game over triggered: health <= 0");
            }
            else if (_healthBar != null && _healthBar.Health <= 0f && _isMultiplayerMatch)
            {
                // Debug: Log when health hits 0 in multiplayer but game over is blocked
                Console.WriteLine($"[Game1] Health at 0 in multiplayer - game over BLOCKED (Match: {_isMultiplayerMatch}, Role: {_multiplayerPlayerRole})");
            }

            // ── Song end ─────────────────────────────────────────────────────
            // Wait for the music to actually stop playing, then return to menu.
            // Only use the chart end time as a fallback (e.g. levels with no music).
            bool musicDone = _musicInst?.State != SoundState.Playing;
            bool chartDone = _started && _songTime > _songEndTime;

            if (chartDone && musicDone && _songTransition == null)
            {
                // Wait a bit after song ends so end-of-song stickers/animations can finish
                _songEndDelay += dt;
                if (_songEndDelay >= 1.5f)
                {
                    _events?.FlushUnknownLog(_level?.LevelName ?? "unknown");
                    _started = false;
                    _songEndDelay = 0f;
                    // Stop music explicitly before transition (prevents ghost audio on menu)
                    _musicInst?.Stop();
                    
                    // Multiplayer: Show results screen instead of going to menu
                    if (_isMultiplayerMatch)
                    {
                        // Calculate player stats
                        var playerRenderer = _noteRenderers.Count > 0 ? _noteRenderers[0] : null;
                        if (playerRenderer != null)
                        {
                            int totalNotes = playerRenderer.Sicks + playerRenderer.Goods + 
                                           playerRenderer.Bads + playerRenderer.Shits + playerRenderer.Misses;
                            float playerAccuracy = 100f;
                            if (totalNotes > 0)
                            {
                                float weightedHits = (playerRenderer.Sicks * 1.0f) + (playerRenderer.Goods * 0.75f) + 
                                                    (playerRenderer.Bads * 0.5f) + (playerRenderer.Shits * 0.25f);
                                playerAccuracy = (weightedHits / totalNotes) * 100f;
                            }
                            
                            // Calculate opponent accuracy (estimate based on score differential)
                            float opponentAccuracy = 100f;  // Default if no data
                            if (_opponentScore > 0)
                            {
                                // Rough estimate: opponent accuracy based on score comparison
                                // This is imperfect but works until we get full opponent stats from server
                                float scoreRatio = (float)_opponentScore / Math.Max(1, playerRenderer.Score);
                                opponentAccuracy = playerAccuracy * scoreRatio;
                                opponentAccuracy = Math.Clamp(opponentAccuracy, 0f, 100f);
                            }
                            
                            bool playerWon = playerRenderer.Score > _opponentScore;
                            
                            // Create results screen
                            _resultsScreen = new MultiplayerResultsScreen(
                                _pixel!,
                                _catTex,
                                "You",  // Player name
                                _opponentName ?? "Opponent",
                                playerRenderer.Score,
                                _opponentScore,
                                playerAccuracy,
                                opponentAccuracy,
                                playerRenderer.Sicks,
                                playerRenderer.Goods,
                                playerRenderer.Bads,
                                playerRenderer.Shits,
                                playerRenderer.Misses,
                                playerWon
                            );
                            
                            Console.WriteLine($"[Game1] Multiplayer match ended - showing results (Player: {playerRenderer.Score}, Opponent: {_opponentScore})");
                        }
                    }
                    else
                    {
                        // Single player: Normal transition to freeplay
                        // Trigger AnkushCat transition
                        _songTransition = new SongTransition(_catTex, _pixel!);
                        _songTransition.Start(_rtWidth, _rtHeight);
                        _transitionToFreeplay = true;
                    }
                }
            }
            else if (!chartDone || !musicDone)
            {
                _songEndDelay = 0f;  // reset if song isn't done yet
            }
        } // end if (_level != null)

        // ── Song transition update (only used for end-of-song → freeplay/multiplayer) ─────
        if (_songTransition != null)
        {
            var prevPhase = _songTransition.CurrentPhase;
            _songTransition.Update((float)gameTime.ElapsedGameTime.TotalSeconds);
            
            // When screen is fully covered (Phase.Full), switch to menu instantly
            // User won't notice because stickers cover everything, then they fade out over menu
            if (prevPhase == SongTransition.Phase.Filling && 
                _songTransition.CurrentPhase == SongTransition.Phase.Full)
            {
                if (_transitionToFreeplay)
                {
                    _transitionToFreeplay = false;
                    _musicInst?.Stop();
                    _inFreeplay = true;
                    ResetWindowTitle();
                    _freeplay?.Refresh();
                    _freeplay?.ResetInputCooldown();
                    DiscordRpc.SetMenu();
                    _level = null;
                }
                else
                {
                    _musicInst?.Stop();
                    _inMultiplayer = true;
                    ResetWindowTitle();
                    _multiplayerMenu?.ResetInputCooldown();
                    // Multiplayer state reset removed - Game1 no longer handles multiplayer
                    Console.WriteLine("[Game1] Returned to multiplayer menu after match");
                    DiscordRpc.SetMenu();
                    _level = null;
                }
                // Don't clear _songTransition yet - let it continue to Clearing phase
            }
            
            // When stickers finish fading out completely, clean up transition
            if (_songTransition.CurrentPhase == SongTransition.Phase.Done)
            {
                _songTransition = null;
            }
        }

        _prevKb = kb;
        DiscordRpc.Update();

        // Update Discord RPC score every second during play
        if (_started && !_paused && _level != null && _noteRenderers.Count > 0)
        {
            _discordUpdateTimer += (float)gameTime.ElapsedGameTime.TotalSeconds;
            if (_discordUpdateTimer >= 1f)
            {
                _discordUpdateTimer = 0f;
                var pr = _noteRenderers[0];
                // Note: UpdateScore is obsolete - causes issues with Discord RPC
                // DiscordRpc.UpdateScore(_level.LevelName, pr.Score, pr.Combo, pr.Misses);
            }
        }
        
        // ── Update NetworkManager ────────────────────────────────────────────
        Multiplayer.NetworkManager.Instance.Update(gameTime);
        
        // ── Update Multiplayer Components ────────────────────────────────────
        if (_isMultiplayerMatch && _started && !_paused)
        {
            // Update interpolator with current server time
            long currentServerTime = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            // Multiplayer UI removed: null?.Update(dt, currentServerTime);
            
            // Send state updates to opponent every 200ms (5 times per second)
            _stateUpdateTimer += dt;
            if (_stateUpdateTimer >= 0.2f && _noteRenderers.Count > 0)
            {
                _stateUpdateTimer = 0f;
                
                var playerRenderer = _noteRenderers[0];
                float health = _healthBar?.Health ?? 0.5f;
                
                // Send current state to opponent
                Multiplayer.NetworkManager.Instance.SendStateUpdate(
                    playerRenderer.Score,
                    playerRenderer.Combo,
                    health,
                    playerRenderer.Sicks,
                    playerRenderer.Goods,
                    playerRenderer.Bads,
                    playerRenderer.Shits,
                    playerRenderer.Misses
                );
            }
            
            // Update HUD animations
            // Multiplayer UI removed: null?.Update(dt);
        }
        
        base.Update(gameTime);
    }

    /// <summary>
    /// Returns the visual left-to-right order for strumlines.
    /// Layout: opponent side on left, player (SL0) always rightmost.
    /// For 4 SLs: [3, 1, 2, 0] → white-left, enemy-left, red-right, player-right
    /// For 2 SLs: [1, 0]
    /// </summary>
    private static int[] BuildStrumlineOrder(int count)
    {
        // SL0 = player (rightmost), SL1 = opponent (leftmost)
        // SL2, SL3... fill in between, alternating from left
        var order = new int[count];
        order[count - 1] = 0;  // player always rightmost
        if (count > 1) order[0] = 1;  // opponent always leftmost

        // Fill remaining slots left-to-right: 2, 3, 4...
        int slot = 1;
        for (int sl = 2; sl < count; sl++)
        {
            order[slot++] = sl;
        }
        return order;
    }

    /// <summary>
    /// Repositions strumlines immediately when midlescroll is toggled at runtime.
    /// Only affects the player strumline (index 0) in 2-strumline layouts.
    /// In midlescroll mode with 2 strumlines, the opponent strumline is split:
    /// lanes 0-1 (left/down) go to the left of center, lanes 2-3 (up/right) go to the right.
    /// </summary>
    private void RepositionStrumlines()
    {
        if (_level == null || _noteRenderers.Count == 0) return;
        int sw = _rtWidth;
        float totalWidth = NoteRenderer.LANE_COUNT * NoteRenderer.LANE_WIDTH;  // 448px
        // Use smaller gap for Midlescroll (opponent arrows split around player)
        float gap = NoteRenderer.Midlescroll ? 50f : 200f;
        int strumlineCount = _noteRenderers.Count;
        
        // Treat as 2 strumlines if HideExtraStrumlines is enabled (classic mode)
        int effectiveCount = HideExtraStrumlines ? Math.Min(strumlineCount, 2) : strumlineCount;

        if (effectiveCount == 1)
        {
            _noteRenderers[0].SetXOffset(sw / 2f - totalWidth / 2f);
        }
        else if (effectiveCount == 2 && NoteRenderer.Midlescroll)
        {
            // Player: centered
            _noteRenderers[0].SetXOffset(sw / 2f - totalWidth / 2f);
            // Opponent: split around the player
            // Left group (lanes 0-1): to the left of the player
            // Right group (lanes 2-3): to the right of the player
            float playerLeft  = sw / 2f - totalWidth / 2f;          // where player starts
            float playerRight = playerLeft + totalWidth;             // where player ends
            float halfWidth   = (NoteRenderer.LANE_COUNT / 2) * NoteRenderer.LANE_WIDTH;  // 2 lanes = 224px
            float oppLeftX    = playerLeft - halfWidth - gap;        // right-align 2 lanes left of player
            float oppRightX   = playerRight + gap;                   // left-align 2 lanes right of player
            _noteRenderers[1].SetSplitXOffset(oppLeftX, oppRightX);
        }
        else
        {
            // Normal layout (no midlescroll, or 3+ strumlines)
            float playerX = NoteRenderer.Midlescroll
                ? sw / 2f - totalWidth / 2f
                : sw / 2f + gap / 2f;
            _noteRenderers[0].SetXOffset(playerX);
            _noteRenderers[1].SetXOffset(sw / 2f - totalWidth - gap / 2f);
        }
    }

    private void RestartSong()
    {
        _songTime        = 0f;  // Will be set to negative during countdown
        _songBeats       = 0f;
        _started         = false;
        _paused          = false;
        if (_healthBar != null)
            _healthBar.Health = 0.5f;
        _gameOverOverlay?.Hide();
        _countdownOverlay?.Reset();
        _judgementText   = null;
        
        // Start FNF-style receptor intro animation (pop-in effect)
        // Only if HUD is visible (respects chart events that hide receptors)
        if (_events?.HudVisible != false)
        {
            foreach (var renderer in _noteRenderers)
            {
                renderer?.StartReceptorIntro(0.0f, 0.05f);
            }
        }
        
        // Reset camera zoom
        _cameraManager?.Reset();
        _judgementTimer  = 0f;
        _songEndDelay    = 0f;
        _keyBindOverlay.Reset();
        _settingsManager?.Reset();
        _inPauseMenu     = false;
        _notesAlpha      = 1.0f;
        _songStopwatch.Reset();
        _pausedTime      = 0.0;
        _musicInst?.Stop();
        if (_level != null) SetupLevel();
    }

    /// <summary>Called by Settings.Load to apply VSync which needs GraphicsDeviceManager.</summary>
    public void ApplyLoadedSettings(bool vsync)
    {
        _graphics.SynchronizeWithVerticalRetrace = vsync;
        _graphics.ApplyChanges();
    }

    private void PauseSong()
    {
        if (!_started || _paused) return;
        _paused = true;
        _pauseStarted = _songStopwatch.Elapsed.TotalSeconds;
        _musicInst?.Pause();
    }

    private void ResumeSong()
    {
        if (!_paused) return;
        _paused = false;
        // Accumulate how long we were paused
        _pausedTime += _songStopwatch.Elapsed.TotalSeconds - _pauseStarted;
        // If audio was seeked while paused, it's ready but not playing — start it now
        if (_musicInst?.State == SoundState.Paused)
            _musicInst?.Resume();
        else
            _musicInst?.Play();
    }

    /// <summary>
    /// Seeks the song to the given time in seconds.
    /// Recreates the audio instance from the stored PCM at the new offset so
    /// audio and chart stay in sync. Events already fired before the seek point
    /// are skipped by the EventProcessor's next-event index.
    /// </summary>
    private void SeekSong(float targetTime)
    {
        if (_level == null) return;
        targetTime = Math.Clamp(targetTime, 0f, _songEndTime - 0.1f);

        // ── Advance chart timing ─────────────────────────────────────────────
        _pausedTime = _songStopwatch.Elapsed.TotalSeconds - targetTime;
        _songTime   = targetTime;
        _songBeats  = _songTime / 60f * (_level.Data.EffectiveBpm);
        // If paused, reset the pause start so accumulated pause time stays correct
        if (_paused) _pauseStarted = _songStopwatch.Elapsed.TotalSeconds;

        // ── Seek note renderers (skip notes before targetTime) ───────────────
        foreach (var r in _noteRenderers)
            r.SeekTo(targetTime);

        // ── Seek events ──────────────────────────────────────────────────────
        _events?.SeekTo(targetTime);

        // ── Reset character animators to idle ────────────────────────────────
        // After seek, clear any active sing pose / hold so characters return to idle.
        // Then check for active notes at targetTime and apply correct animations.
        // IMPORTANT: Don't reset animations that were set by events - events have priority!
        foreach (var anim in _objAnimators)
        {
            if (anim == null || !anim.IsCharacter) continue;
            
            // Skip if animation was set by an event - events control these animations
            if (anim.IsSetByEvent) continue;
            
            anim.HoldUntil = -1f;
            // Return to idle (anim 0, or anim 1 if 0 is a placeholder)
            int idleAnim = (anim.AnimCount > 1 && anim.CurrentAnimData.FrameCount == 1) ? 1 : 0;
            anim.SetAnimation(idleAnim);
        }

        // ── Apply animations for active notes at targetTime ──────────────────
        // This ensures characters show the correct pose when seeking backward/forward
        // during singing or shooting sequences.
        var objs = _level.Scene.Objects;
        for (int strumlineIdx = 0; strumlineIdx < _noteRenderers.Count; strumlineIdx++)
        {
            var renderer = _noteRenderers[strumlineIdx];
            
            // Check each lane for notes active at targetTime
            for (int lane = 0; lane < NoteRenderer.LANE_COUNT; lane++)
            {
                // Find if there's a note active at targetTime in this lane
                // A note is "active" if targetTime is within its hit window or hold duration
                var strumline = _level.Data.Strumlines[strumlineIdx];
                if (lane >= strumline.Count) continue;
                
                var notesInLane = strumline[lane];
                FnmmNote? activeNote = null;
                
                foreach (var note in notesInLane)
                {
                    float noteStart = note.Start;
                    float noteEnd = note.Start + note.Duration;
                    
                    // Check if targetTime is within the note's active period
                    // (hit window before start + hold duration)
                    float hitWindowStart = noteStart - NoteRenderer.HIT_WINDOW;
                    if (targetTime >= hitWindowStart && targetTime <= noteEnd)
                    {
                        activeNote = note;
                        break;
                    }
                }
                
                // If we found an active note, apply the corresponding animation
                if (activeNote != null)
                {
                    // Find the character for this strumline
                    int bestJ = -1;
                    float bestDist = float.MaxValue;
                    float camY = _events?.Camera.Y ?? 0f;
                    
                    for (int j = 0; j < objs.Count; j++)
                    {
                        if ((int)objs[j].StrumLine != strumlineIdx) continue;
                        var anim = _objAnimators[j];
                        if (anim == null) continue;
                        if (!anim.Visible) continue;
                        
                        float dist = MathF.Abs(objs[j].Y - camY);
                        if (dist < bestDist)
                        {
                            bestDist = dist;
                            bestJ = j;
                        }
                    }
                    
                    // Apply the sing animation for this lane
                    if (bestJ >= 0 && _objAnimators[bestJ] != null)
                    {
                        var charAnim = _objAnimators[bestJ]!;
                        
                        // Animation index: 0=idle, 1=idle/left, 2=down, 3=up, 4=right
                        // Lane: 0=left, 1=down, 2=up, 3=right
                        int singAnim = lane + 2;  // lane 0→anim 2, lane 1→anim 3, etc.
                        
                        // Handle placeholder idle (anim 0 is 1-frame)
                        if (charAnim.AnimCount > 1 && charAnim.CurrentAnimData.FrameCount == 1)
                            singAnim = lane + 1;  // lane 0→anim 1, lane 1→anim 2, etc.
                        
                        if (singAnim < charAnim.AnimCount)
                        {
                            charAnim.SetAnimation(singAnim);
                            
                            // If it's a hold note, set HoldUntil
                            if (activeNote.Duration > 0.1f)
                            {
                                float noteEnd = activeNote.Start + activeNote.Duration;
                                charAnim.HoldUntil = noteEnd;
                            }
                        }
                    }
                    
                    // Only process one active note per strumline (the first one found)
                    break;
                }
            }
        }

        // ── Seek audio ───────────────────────────────────────────────────────────
        if (_musicPcm != null)
        {
            var seeked = OggLoader.SeekPcm(_musicPcm, _musicSampleRate, _musicChannels, targetTime);
            if (seeked != null)
            {
                _musicInst?.Dispose();
                _music?.Dispose();
                _music = seeked;
                _musicInst = _music.CreateInstance();
                _musicInst.Volume = MusicVolume;
                _musicInst.IsLooped = false;
                if (!_paused) _musicInst.Play();
            }
        }
    }

    // ── Draw ─────────────────────────────────────────────────────────────────
    protected override void Draw(GameTime gameTime)
    {
        try
        {
            DrawInternal(gameTime);
        }
        catch (Exception ex)
        {
            HandleGameplayError(ex, "Draw");
        }
    }

    private void DrawInternal(GameTime gameTime)
    {
        // Safety: Close any pending SpriteBatch before starting
        try { _spriteBatch?.End(); } catch { }
        
        int sw = _rtWidth;
        int sh = _rtHeight;

        // ── Render everything to the fixed-size render target ─────────────────
        if (_renderTarget != null)
            GraphicsDevice.SetRenderTarget(_renderTarget);

        GraphicsDevice.Clear(Color.Black);

#nullable disable  // Disable nullability warnings for Draw() method - all null checks are in place
        // ── Freeplay menu (direct to screen - no render target) ──────────────
        if (_inFreeplay && _freeplay != null && _loadingScreen == null)
        {
            // Draw directly to window for crisp UI
            var winBounds = Window.ClientBounds;
            GraphicsDevice.SetRenderTarget(null);
            GraphicsDevice.Clear(Color.Black);
            
            _spriteBatch.Begin(samplerState: SamplerState.LinearClamp);
            if (_freeplay != null)
                _freeplay.Draw(_spriteBatch, winBounds.Width, winBounds.Height, winBounds.Width, winBounds.Height);
            _spriteBatch.End();
            
            #if ANDROID
            // Draw mods help screen on top if visible
#pragma warning disable CS8602 // Null check already done in if condition
            if (_modsHelpScreen != null && _modsHelpScreen.IsVisible)
            {
                _spriteBatch.Begin(samplerState: SamplerState.LinearClamp);
                _modsHelpScreen!.Draw(_spriteBatch, winBounds.Width, winBounds.Height);
                _spriteBatch.End();
            }
#pragma warning restore CS8602
            #endif
            
            // If there's an active song transition, render it over the menu then return
#pragma warning disable CS8602 // Null check already done in if condition
            if (_songTransition != null)
            {
                _spriteBatch.Begin(samplerState: SamplerState.LinearClamp);
                _songTransition!.Draw(_spriteBatch, winBounds.Width, winBounds.Height);
                _spriteBatch.End();
            }
#pragma warning restore CS8602
            
            // Draw touch controls LAST before present
            DrawTouchControls();
            return;
        }
        
        // ── Main Settings menu (direct to screen - no render target) ─────────
        if (_inMainSettings && _mainSettingsMenu != null)
        {
            // Draw directly to window for crisp UI
            var winBounds = Window.ClientBounds;
            GraphicsDevice.SetRenderTarget(null);
            GraphicsDevice.Clear(Color.Black);
            
            _spriteBatch.Begin(samplerState: SamplerState.LinearClamp);
            if (_mainSettingsMenu != null)
                _mainSettingsMenu.Draw(_spriteBatch, winBounds.Width, winBounds.Height);
            _spriteBatch.End();
            
            // Draw touch controls LAST before present
            DrawTouchControls();
            return;
        }
        
        // ── HUD Editor (direct to screen - no render target) ─────────────────
        if (_inHudEditor && _hudEditor != null)
        {
            // Render to target (like gameplay) for correct aspect ratio
            GraphicsDevice.SetRenderTarget(_renderTarget);
            GraphicsDevice.Clear(Color.Black);
            
            _spriteBatch.Begin(samplerState: SamplerState.PointClamp);
            if (_hudEditor != null)
                _hudEditor.Draw(_spriteBatch, _rtWidth, _rtHeight);
            _spriteBatch.End();
            
            // Blit to screen with letterboxing
            BlitToScreen();
            
            // Draw touch controls LAST before present
            DrawTouchControls();
            return;
        }

        // ── Multiplayer menu (direct to screen - no render target) ───────────
        if (_inMultiplayer && _multiplayerMenu != null)
        {
            // Draw directly to window for crisp UI
            var winBounds = Window.ClientBounds;
            GraphicsDevice.SetRenderTarget(null);
            GraphicsDevice.Clear(Color.Black);
            
            _spriteBatch.Begin(samplerState: SamplerState.LinearClamp);
#pragma warning disable CS8602 // Null checks already done in if condition
            if (_pixel != null && _font != null)
                _multiplayerMenu!.Draw(_spriteBatch, _font, _pixel, winBounds.Width, winBounds.Height);
#pragma warning restore CS8602
            _spriteBatch.End();
            
            // If there's an active song transition, render it over the menu then return
#pragma warning disable CS8602 // Null check already done in if condition
            if (_songTransition != null)
            {
                _spriteBatch.Begin(samplerState: SamplerState.LinearClamp);
                _songTransition!.Draw(_spriteBatch, winBounds.Width, winBounds.Height);
                _spriteBatch.End();
            }
#pragma warning restore CS8602
            
            // Draw touch controls LAST before present
            DrawTouchControls();
            return;
        }

        // ── Loading screen (shown while loading or waiting for Enter) ─────────
        if (_loadingScreen != null)
        {
            if (_level != null)
            {
                // Draw scene behind loading screen
                var loadCamMatrix = BuildCameraMatrix(sw, sh);
                DrawSceneOrdered(sw, sh, loadCamMatrix);
            }
            _spriteBatch.Begin(samplerState: SamplerState.PointClamp);
            if (_loadingScreen != null)
                _loadingScreen.Draw(_spriteBatch, sw, sh);
            _spriteBatch.End();
            BlitToScreen();
            
            // Draw touch controls LAST before present
            DrawTouchControls();
            return;
        }

        // ── Results Screen (Multiplayer) ───────────────────────────────────
        if (_resultsScreen != null)
        {
            GraphicsDevice.SetRenderTarget(_renderTarget);
            GraphicsDevice.Clear(Color.Black);
            
            _spriteBatch.Begin(samplerState: SamplerState.PointClamp);
            _resultsScreen.Draw(_spriteBatch, _font, sw, sh);
            _spriteBatch.End();
            
            BlitToScreen();
            return;
        }

        // ── Error or no level ─────────────────────────────────────────────────
        if (_errorMsg != null || _level == null)
        {
            _spriteBatch.Begin();
            if (_errorMsg != null) 
                DrawError(_errorMsg);
            else                   
                DrawDropHint();
            _spriteBatch.End();
            BlitToScreen();
            
            // Draw touch controls LAST before present
            DrawTouchControls();
            return;
        }

        // ── Scene: draw all objects in index order (world + fixed interleaved) ──
        // This preserves the author's intended draw order exactly as defined in scene.json.
        var camMatrix = BuildCameraMatrix(sw, sh);
        _lastCamMatrix = camMatrix;
        DrawSceneOrdered(sw, sh, camMatrix);

        // ── HUD: health bar, icons, stats (drawn before notes) ───────────────
        _spriteBatch.Begin(SpriteSortMode.Deferred, BlendState.NonPremultiplied, SamplerState.LinearClamp);
        DrawHUD(sw, sh);
        _spriteBatch.End();

        // ── Notes: arrows and receptors (drawn on top of everything) ─────────
        _spriteBatch.Begin(SpriteSortMode.Deferred, BlendState.NonPremultiplied, SamplerState.LinearClamp);
        DrawNotes();
        
        // Draw particle notesplash effects (MonoGame.Extended particles)
        _particleNoteSplash?.Draw(_spriteBatch);
        
        _spriteBatch.End();

        // Menus use PointClamp for crisp text rendering (FontStashSharp)
        _spriteBatch.Begin(samplerState: SamplerState.PointClamp);
        DrawScreenFlashes(sw, sh);
        DrawCountdown(sw, sh);
        DrawJudgement(sw, sh);
        
        // Draw game over overlay using extracted UI component
        if (_gameOverOverlay != null && _gameOverOverlay.IsActive && _noteRenderers.Count > 0)
        {
            _gameOverOverlay.Draw(sw, sh, _noteRenderers[0]);
        }
        
        _spriteBatch.End();

        // ── Song transition overlay (AnkushCat cascade) ───────────────────────
        if (_songTransition != null && _songTransition.CurrentPhase != SongTransition.Phase.Done)
        {
            _spriteBatch.Begin(samplerState: SamplerState.LinearClamp);
            _songTransition.Draw(_spriteBatch, sw, sh);
            _spriteBatch.End();
        }

        // ── Letterbox blit to screen ──────────────────────────────────────────
        BlitToScreen();

        // ── Settings menu overlay (drawn AFTER blit for crisp text) ───────────
        if (_settingsManager?.IsActive == true)
        {
            var winBounds = Window.ClientBounds;
            GraphicsDevice.SetRenderTarget(null);
            
            _spriteBatch.Begin(samplerState: SamplerState.LinearClamp);
            _settingsManager.Draw(this, _spriteBatch, _font!, _pixel!, winBounds.Width, winBounds.Height, _fontRenderer);
            _spriteBatch.End();
        }

        // ── Pause menu overlay (drawn AFTER blit for crisp text) ──────────────
        if (_inPauseMenu && _pauseMenu != null)
        {
            var winBounds = Window.ClientBounds;
            GraphicsDevice.SetRenderTarget(null);
            
            // Calculate letterbox area (same as BlitToScreen)
            int winW = winBounds.Width;
            int winH = winBounds.Height;
            float scaleX = (float)winW / _rtWidth;
            float scaleY = (float)winH / _rtHeight;
            float scale  = Math.Min(scaleX, scaleY);
            int   dstW   = (int)(_rtWidth  * scale);
            int   dstH   = (int)(_rtHeight * scale);
            int   dstX   = (winW - dstW) / 2;
            int   dstY   = (winH - dstH) / 2;
            
            // Get stats from player renderer
            int score = 0, misses = 0, combo = 0;
            int sicks = 0, goods = 0, bads = 0, shits = 0;
            if (_noteRenderers.Count > 0 && _noteRenderers[0] != null)
            {
                var r = _noteRenderers[0];
                score = r.Score;
                misses = r.Misses;
                combo = r.Combo;
                sicks = r.Sicks;
                goods = r.Goods;
                bads = r.Bads;
                shits = r.Shits;
            }
            
            // Calculate accuracy
            int totalNotes = sicks + goods + bads + shits + misses;
            float accuracy = 100f;
            if (totalNotes > 0)
            {
                float weightedHits = (sicks * 1.0f) + (goods * 0.75f) + (bads * 0.5f) + (shits * 0.25f);
                accuracy = (weightedHits / totalNotes) * 100f;
            }
            
            _spriteBatch.Begin(samplerState: SamplerState.LinearClamp);
            _pauseMenu.Draw(_spriteBatch, dstX, dstY, dstW, dstH, scale, _rtWidth, _rtHeight, gameTime,
                score, accuracy, combo, misses, sicks, goods, bads, shits);
            _spriteBatch.End();
        }

        // ── Arrows menu overlay (drawn AFTER blit for crisp text) ─────────────
        if (_arrowsMenu?.IsActive == true)
        {
            var winBounds = Window.ClientBounds;
            
            _spriteBatch.Begin(samplerState: SamplerState.LinearClamp);
            _arrowsMenu.Draw(_spriteBatch, winBounds.Width, winBounds.Height);
            _spriteBatch.End();
        }

        // ── NoteSplash menu overlay (drawn AFTER blit for crisp text) ─────────
        if (_splashMenu?.IsActive == true)
        {
            var winBounds = Window.ClientBounds;
            
            _spriteBatch.Begin(samplerState: SamplerState.LinearClamp);
            _splashMenu.Draw(_spriteBatch, winBounds.Width, winBounds.Height);
            _spriteBatch.End();
        }

        // ── Countdown menu overlay (drawn AFTER blit for crisp text) ──────────
        if (_countdownMenu?.IsActive == true)
        {
            var winBounds = Window.ClientBounds;
            
            _spriteBatch.Begin(samplerState: SamplerState.LinearClamp);
            _countdownMenu.Draw(_spriteBatch, winBounds.Width, winBounds.Height);
            _spriteBatch.End();
        }

        // ── Font menu overlay (drawn AFTER blit for crisp text) ───────────────
        if (_fontMenu?.IsActive == true)
        {
            var winBounds = Window.ClientBounds;
            
            _spriteBatch.Begin(samplerState: SamplerState.LinearClamp);
            _fontMenu.Draw(_spriteBatch, winBounds.Width, winBounds.Height);
            _spriteBatch.End();
        }

        // ── Touch controls (Android only) - drawn LAST on top of everything ───
        DrawTouchControls();

        base.Draw(gameTime);
#nullable restore  // Re-enable nullability warnings
    }

    /// <summary>
    /// Draw touch controls on top of everything (Android only).
    /// Must be called AFTER all other rendering is done.
    /// </summary>
    private void DrawTouchControls()
    {
#if ANDROID
        if (_touchControls == null) return;
        
        try
        {
            // CRITICAL: Asegurar que dibujamos en la pantalla REAL, no en render target
            GraphicsDevice.SetRenderTarget(null);
            
            // Safety: Terminar cualquier SpriteBatch pendiente
            try { _spriteBatch?.End(); } catch { /* ignore */ }
            
            // Comenzar nuevo batch con blend alpha para transparencias
            _spriteBatch.Begin(
                sortMode: SpriteSortMode.Deferred,
                blendState: BlendState.AlphaBlend,
                samplerState: SamplerState.PointClamp,
                depthStencilState: null,
                rasterizerState: null,
                effect: null,
                transformMatrix: null
            );
            
            _touchControls.Draw(_spriteBatch, _font);
            _spriteBatch.End();
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[TouchControls Draw Error] {ex.Message}");
        }
#endif
    }

    /// <summary>
    /// Blits the render target to the actual window with letterboxing (black bars).
    /// Call this at the end of every Draw path, after all rendering to _renderTarget is done.
    /// </summary>
    private void BlitToScreen()
    {
        if (_renderTarget == null) return;
        GraphicsDevice.SetRenderTarget(null);
        int winW = GraphicsDevice.PresentationParameters.BackBufferWidth;
        int winH = GraphicsDevice.PresentationParameters.BackBufferHeight;

        // Letterbox: scale to fit while preserving aspect ratio
        float scaleX = (float)winW / _rtWidth;
        float scaleY = (float)winH / _rtHeight;
        float scale  = Math.Min(scaleX, scaleY);
        int   dstW   = (int)(_rtWidth  * scale);
        int   dstH   = (int)(_rtHeight * scale);
        int   dstX   = (winW - dstW) / 2;
        int   dstY   = (winH - dstH) / 2;

        GraphicsDevice.Clear(Color.Black);
        
        // No shader here - draw render target as-is
        _spriteBatch.Begin(samplerState: SamplerState.LinearClamp);
        _spriteBatch.Draw(_renderTarget, new Rectangle(dstX, dstY, dstW, dstH), Color.White);
        _spriteBatch.End();
    }

    //
    // GameMaker/Funky Maker camera:
    //   cam.X/Y  = world-space CENTER of the viewport.
    //   cam.Scale = zoom relative to base resolution.
    //              scale=1 → shows BASE_W×BASE_H world units.
    //              scale=0.55 → shows BASE_W/0.55 world units wide (zoomed out).
    //
    // BASE_W is derived from the level: the width of the world the author designed for.
    // Funky Maker landscape levels use 2160px wide world (standard GameMaker room).
    //
    // ── GPU Capabilities ──────────────────────────────────────────────────────
    
    // MonoGame internal limit (regardless of GPU capability)
    private const int MONOGAME_MAX_TEXTURE_SIZE = 16384;
    
    // P/Invoke para OpenGL (Windows/Linux/Android)
    [System.Runtime.InteropServices.DllImport("opengl32.dll", EntryPoint = "glGetIntegerv")]
    private static extern void glGetIntegerv_Windows(int pname, out int value);
    
    [System.Runtime.InteropServices.DllImport("libGL.so.1", EntryPoint = "glGetIntegerv")]
    private static extern void glGetIntegerv_Linux(int pname, out int value);
    
    [System.Runtime.InteropServices.DllImport("libGLESv2.so", EntryPoint = "glGetIntegerv")]
    private static extern void glGetIntegerv_Android(int pname, out int value);
    
    private const int GL_MAX_TEXTURE_SIZE = 0x0D33;
    
    /// <summary>
    /// Gets the maximum texture size supported by the device.
    /// Returns the MINIMUM of GPU limit and MonoGame internal limit (16384px).
    /// </summary>
    private int GetGpuMaxTextureSize()
    {
        try
        {
            int gpuMaxSize = 0;
            
            // Try to query OpenGL directly based on platform
            try
            {
#if ANDROID
                glGetIntegerv_Android(GL_MAX_TEXTURE_SIZE, out gpuMaxSize);
                Console.WriteLine($"[GPU] Device GPU reports max texture size: {gpuMaxSize}px");
#elif WINDOWS
                glGetIntegerv_Windows(GL_MAX_TEXTURE_SIZE, out gpuMaxSize);
                Console.WriteLine($"[GPU] Device GPU reports max texture size: {gpuMaxSize}px");
#else
                // Try Linux first, fallback to Windows
                try
                {
                    glGetIntegerv_Linux(GL_MAX_TEXTURE_SIZE, out gpuMaxSize);
                    Console.WriteLine($"[GPU] Device GPU reports max texture size: {gpuMaxSize}px");
                }
                catch
                {
                    glGetIntegerv_Windows(GL_MAX_TEXTURE_SIZE, out gpuMaxSize);
                    Console.WriteLine($"[GPU] Device GPU reports max texture size: {gpuMaxSize}px");
                }
#endif
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[GPU] Failed to query GPU limit via OpenGL: {ex.Message}");
            }
            
            // Validate GPU query result (sanity check)
            if (gpuMaxSize <= 0 || gpuMaxSize > 65536)
            {
                Console.WriteLine($"[GPU] Invalid GPU limit ({gpuMaxSize}px), using safe fallback");
#if ANDROID
                gpuMaxSize = 4096;  // Conservative Android fallback
#else
                gpuMaxSize = 16384; // Desktop fallback (MonoGame max)
#endif
            }
            
            // Apply MonoGame internal limit - never exceed 16384px
            int finalLimit = Math.Min(gpuMaxSize, MONOGAME_MAX_TEXTURE_SIZE);
            
            if (gpuMaxSize > MONOGAME_MAX_TEXTURE_SIZE)
            {
                Console.WriteLine($"[GPU] GPU supports {gpuMaxSize}px but MonoGame max is {MONOGAME_MAX_TEXTURE_SIZE}px");
                Console.WriteLine($"[GPU] Capping at MonoGame limit to prevent issues: {finalLimit}px");
            }
            else
            {
                Console.WriteLine($"[GPU] Using GPU limit: {finalLimit}px");
            }
            
            return finalLimit;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[GPU] Error detecting max texture size: {ex.Message}");
#if ANDROID
            return 4096;  // Safe Android fallback
#else
            return 16384; // Safe desktop fallback (MonoGame max)
#endif
        }
    }
    
    // ── Texture splitting ────────────────────────────────────────────────────
    /// <summary>
    /// Splits a wide texture strip into individual frame textures using the GPU.
    /// Each frame is copied via RenderTarget2D + SpriteBatch for perfect quality.
    /// The source texture is NOT disposed here — caller handles that.
    /// </summary>
    private void SplitTextureGpu(Texture2D source, int frameCount, int fw, int fh,
                                  List<Texture2D> output)
    {
        var sb = new SpriteBatch(GraphicsDevice);
        for (int f = 0; f < frameCount; f++)
        {
            var rt = new RenderTarget2D(GraphicsDevice, fw, fh,
                false, SurfaceFormat.Color, DepthFormat.None,
                0, RenderTargetUsage.PreserveContents);

            GraphicsDevice.SetRenderTarget(rt);
            GraphicsDevice.Clear(Color.Transparent);

            sb.Begin(samplerState: SamplerState.PointClamp);
            sb.Draw(source,
                destinationRectangle: new Rectangle(0, 0, fw, fh),
                sourceRectangle:      new Rectangle(f * fw, 0, fw, fh),
                color:                Color.White);
            sb.End();

            GraphicsDevice.SetRenderTarget(null);
            output.Add(rt);
        }
        sb.Dispose();
        Console.WriteLine($"[SplitGpu] Split {frameCount} frames of {fw}x{fh}");
    }

    /// <summary>
    /// Splits a wide PNG strip into chunk textures using ImageSharp for high-quality decoding.
    /// Each chunk holds framesPerChunk frames, keeping textures under GPU size limits.
    /// </summary>
    private void SplitPngIntoChunks(byte[] pngBytes, int frameCount, int fw, int fh,
                                     int framesPerChunk, List<Texture2D> output)
    {
        try
        {
            using var img = SixLabors.ImageSharp.Image.Load<SixLabors.ImageSharp.PixelFormats.Rgba32>(pngBytes);
            int chunkCount = (frameCount + framesPerChunk - 1) / framesPerChunk;

            for (int c = 0; c < chunkCount; c++)
            {
                int firstFrame    = c * framesPerChunk;
                int framesInChunk = Math.Min(framesPerChunk, frameCount - firstFrame);
                int chunkW        = framesInChunk * fw;

                // Crop the chunk region from the full strip
                var cropRect = new SixLabors.ImageSharp.Rectangle(firstFrame * fw, 0, chunkW, fh);
                using var chunk = img.Clone(ctx => ctx.Crop(cropRect));

                var chunkTex    = new Texture2D(GraphicsDevice, chunkW, fh);
                var chunkPixels = new Color[chunkW * fh];

                chunk.ProcessPixelRows(accessor =>
                {
                    for (int row = 0; row < accessor.Height; row++)
                    {
                        var rowSpan = accessor.GetRowSpan(row);
                        int rowBase = row * chunkW;
                        for (int x = 0; x < chunkW; x++)
                        {
                            var p = rowSpan[x];
                            chunkPixels[rowBase + x] = Color.FromNonPremultiplied(p.R, p.G, p.B, p.A);
                        }
                    }
                });

                chunkTex.SetData(chunkPixels);
                output.Add(chunkTex);
            }
            Console.WriteLine($"[SplitChunk] ImageSharp: {chunkCount} chunks, {framesPerChunk} frames each ({fw * framesPerChunk}px wide)");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[SplitChunk] ImageSharp failed: {ex.Message}");
        }
    }

    /// <summary>
    /// Splits a wide PNG strip into individual frame textures using ImageSharp.
    /// Fallback for non-chunk split paths.
    /// </summary>
    private void SplitPngIntoFrames(byte[] pngBytes, int frameCount, int fw, int fh,
                                     List<Texture2D> output)
    {
        try
        {
            using var img = SixLabors.ImageSharp.Image.Load<SixLabors.ImageSharp.PixelFormats.Rgba32>(pngBytes);
            for (int f = 0; f < frameCount; f++)
            {
                var cropRect = new SixLabors.ImageSharp.Rectangle(f * fw, 0, fw, fh);
                using var frame = img.Clone(ctx => ctx.Crop(cropRect));

                var frameTex    = new Texture2D(GraphicsDevice, fw, fh);
                var framePixels = new Color[fw * fh];

                frame.ProcessPixelRows(accessor =>
                {
                    for (int row = 0; row < accessor.Height; row++)
                    {
                        var rowSpan = accessor.GetRowSpan(row);
                        int rowBase = row * fw;
                        for (int x = 0; x < fw; x++)
                        {
                            var p = rowSpan[x];
                            framePixels[rowBase + x] = Color.FromNonPremultiplied(p.R, p.G, p.B, p.A);
                        }
                    }
                });

                frameTex.SetData(framePixels);
                output.Add(frameTex);
            }
            Console.WriteLine($"[SplitPng] ImageSharp: {frameCount} frames of {fw}x{fh}");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[SplitPng] ImageSharp failed: {ex.Message}");
        }
    }

    /// <summary>
    /// Splits a single-frame oversized bg PNG into maxTexSize-wide tiles at full quality.
    /// The last tile may be narrower than tileW if pngW is not a multiple of tileW.
    /// SpriteAnimator uses ChunkFrames=1 + FrameWidth=tileW to reconstruct the image.
    /// </summary>
    private void SplitBgIntoTiles(byte[] pngBytes, int pngW, int pngH, int tileW,
                                   List<Texture2D> output)
    {
        try
        {
            using var img = SixLabors.ImageSharp.Image
                .Load<SixLabors.ImageSharp.PixelFormats.Rgba32>(pngBytes);

            int tileCount = (int)Math.Ceiling((double)pngW / tileW);
            for (int t = 0; t < tileCount; t++)
            {
                int x0          = t * tileW;
                int actualW     = Math.Min(tileW, pngW - x0);
                var cropRect    = new SixLabors.ImageSharp.Rectangle(x0, 0, actualW, pngH);
                using var tile  = img.Clone(ctx => ctx.Crop(cropRect));

                var tileTex     = new Texture2D(GraphicsDevice, actualW, pngH);
                var tilePixels  = new Color[actualW * pngH];
                tile.ProcessPixelRows(accessor =>
                {
                    for (int row = 0; row < accessor.Height; row++)
                    {
                        var rowSpan = accessor.GetRowSpan(row);
                        int rowBase = row * actualW;
                        for (int x = 0; x < actualW; x++)
                        {
                            var p = rowSpan[x];
                            tilePixels[rowBase + x] = Color.FromNonPremultiplied(p.R, p.G, p.B, p.A);
                        }
                    }
                });
                tileTex.SetData(tilePixels);
                output.Add(tileTex);
            }
            Console.WriteLine($"[BgTile] ImageSharp: {tileCount} tiles of {tileW}px (last={Math.Min(tileW, pngW - (tileCount-1)*tileW)}px)");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[BgTile] ImageSharp failed: {ex.Message}");
        }
    }

    /// <summary>
    /// Loads a PNG from raw bytes using ImageSharp (handles all PNG variants),
    /// then uploads the decoded RGBA32 pixels to a MonoGame Texture2D.
    /// This avoids DirectX's limited PNG format support.
    /// </summary>
    private Texture2D? LoadTextureViaImageSharp(GraphicsDevice gd, byte[] pngBytes)
    {
        using var ms  = new MemoryStream(pngBytes);
        // Use DecoderOptions to ignore color profiles that can cause incorrect colors
        var decoderOptions = new SixLabors.ImageSharp.Formats.DecoderOptions
        {
            SkipMetadata = true,
        };
        using var img = SixLabors.ImageSharp.Image
            .Load<SixLabors.ImageSharp.PixelFormats.Rgba32>(decoderOptions, ms);

        int srcW = img.Width;
        int srcH = img.Height;
        if (srcW <= 0 || srcH <= 0) return null;
        if (srcW > 16384 || srcH > 16384) return null;
        // DirectX requires width to be a multiple of 4.
        // If not, pad the image to the next multiple of 4 using ImageSharp,
        // so tex.Width stays consistent with what SpriteAnimator expects.
        int texW = (srcW + 3) & ~3;
        int texH = srcH;

        SixLabors.ImageSharp.Image<SixLabors.ImageSharp.PixelFormats.Rgba32> source = img;
        SixLabors.ImageSharp.Image<SixLabors.ImageSharp.PixelFormats.Rgba32>? padded = null;

        if (texW != srcW)
        {
            // Pad right side with transparent pixels — copy manually for reliability
            padded = new SixLabors.ImageSharp.Image<SixLabors.ImageSharp.PixelFormats.Rgba32>(texW, texH);
            img.ProcessPixelRows(padded, (srcAcc, dstAcc) =>
            {
                for (int y = 0; y < srcH; y++)
                {
                    var srcRow = srcAcc.GetRowSpan(y);
                    var dstRow = dstAcc.GetRowSpan(y);
                    srcRow.CopyTo(dstRow);
                    // Remaining pixels in dstRow are already default (transparent)
                }
            });
            source = padded;
        }

        var pixels = new Microsoft.Xna.Framework.Color[texW * texH];
        source.ProcessPixelRows(accessor =>
        {
            for (int y = 0; y < texH; y++)
            {
                var row     = accessor.GetRowSpan(y);
                int rowBase = y * texW;
                for (int x = 0; x < texW; x++)
                {
                    var p = row[x];
                    pixels[rowBase + x] = Microsoft.Xna.Framework.Color.FromNonPremultiplied(p.R, p.G, p.B, p.A);
                }
            }
        });

        padded?.Dispose();

        var tex = new Texture2D(gd, texW, texH, false, Microsoft.Xna.Framework.Graphics.SurfaceFormat.Color);
        try
        {
            tex.SetData(pixels);
        }
        catch (Exception ex)
        {
            tex.Dispose();
            Console.WriteLine($"[Texture] SetData failed {texW}x{texH} (src {srcW}x{srcH}): {ex.Message}");
            return null;
        }
        return tex;
    }
    private static string? FindAssetsRoot()
    {
#if ANDROID
        // On Android, Assets are bundled in the APK and copied to internal storage
        try
        {
            var filesDir = global::Android.App.Application.Context.FilesDir?.AbsolutePath;
            if (filesDir != null)
            {
                string assetsPath = Path.Combine(filesDir, "Assets");
                
                // Check if extraction is needed
                if (!Directory.Exists(assetsPath) || !File.Exists(Path.Combine(assetsPath, "Font", "MilkyNice.otf")))
                {
                    Console.WriteLine("[FindAssetsRoot] Assets folder not found or incomplete");
                    
                    // Check if extraction is already in progress or completed
                    string markerFile = Path.Combine(filesDir, ".assets_extracted");
                    if (File.Exists(markerFile))
                    {
                        // Already extracted, just use it
                        Console.WriteLine("[FindAssetsRoot] Assets already extracted previously");
                    }
                    else
                    {
                        // Extract in background thread to avoid ANR (Application Not Responding)
                        Console.WriteLine("[FindAssetsRoot] Starting background extraction...");
                        System.Threading.Tasks.Task.Run(() => {
                            try
                            {
                                ExtractAssetsFromAPK();
                                // Create marker file to indicate extraction is complete
                                File.WriteAllText(markerFile, DateTime.Now.ToString());
                                Console.WriteLine("[FindAssetsRoot] Background extraction completed");
                            }
                            catch (Exception ex)
                            {
                                Console.WriteLine($"[FindAssetsRoot] Background extraction failed: {ex.Message}");
                            }
                        });
                        
                        // Wait a bit for extraction to start (non-blocking)
                        for (int i = 0; i < 50 && !Directory.Exists(assetsPath); i++)
                        {
                            System.Threading.Thread.Sleep(100); // 100ms * 50 = 5 seconds max
                        }
                    }
                }
                
                if (Directory.Exists(assetsPath))
                {
                    Console.WriteLine($"[FindAssetsRoot] Found Assets at: {assetsPath}");
                    return assetsPath;
                }
                else
                {
                    Console.WriteLine("[FindAssetsRoot] Assets not found yet, extraction may still be in progress");
                    // Return the path anyway, files will be loaded as they become available
                    return assetsPath;
                }
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[FindAssetsRoot] Error accessing Assets: {ex.Message}");
        }
        
        return null;
#else
        // Look for the Assets folder relative to exe or working directory
        string[] candidates =
        {
            Path.Combine(AppContext.BaseDirectory, "Assets"),
            Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "Assets"),
            Path.Combine(Directory.GetCurrentDirectory(), "Assets"),
        };
        foreach (var c in candidates)
        {
            string norm = Path.GetFullPath(c);
            if (Directory.Exists(norm) &&
                File.Exists(Path.Combine(norm, "shared", "images", "fmm.png")))
                return norm;
        }
        return null;
#endif
    }

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
            string[]? rootItems = assetManager.List("");
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
            string[] essentialFolders = { "Font", "LoadingScreen", "data", "images", "shared", "Content" };
            
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

    private float GetBaseW() => _level?.Scene.Landscape == true ? 2160f : 1920f;
    private float GetBaseH() => _level?.Scene.Landscape == true ? 1920f : 2160f;

    /// <summary>
    /// Force render the loading screen immediately (used during SetupLevel to keep Android responsive)
    /// </summary>
    private void RenderLoadingScreen()
    {
        if (_loadingScreen == null) return;
        
        try
        {
            // Force a complete frame cycle on ALL platforms to show loading progress
            int sw = _rtWidth;
            int sh = _rtHeight;
            
            // Update loading screen
            _loadingScreen.Update(0.016f, false, false, sw, sh);
            
            // Draw the loading screen
            if (_renderTarget != null && _spriteBatch != null)
            {
                GraphicsDevice.SetRenderTarget(_renderTarget);
                GraphicsDevice.Clear(Color.Black);
                _spriteBatch.Begin(samplerState: SamplerState.PointClamp);
                _loadingScreen.Draw(_spriteBatch, sw, sh);
                _spriteBatch.End();
                
                // Blit to screen
                GraphicsDevice.SetRenderTarget(null);
                GraphicsDevice.Clear(Color.Black);
                var winBounds = Window.ClientBounds;
                int ww = winBounds.Width;
                int wh = winBounds.Height;
                float scaleX = ww / (float)sw;
                float scaleY = wh / (float)sh;
                float scale  = Math.Min(scaleX, scaleY);
                int   dstW   = (int)(sw * scale);
                int   dstH   = (int)(sh * scale);
                int   offsetX = (ww - dstW) / 2;
                int   offsetY = (wh - dstH) / 2;
                _spriteBatch.Begin(samplerState: BilinearFiltering ? SamplerState.LinearClamp : SamplerState.PointClamp);
                _spriteBatch.Draw(_renderTarget, new Rectangle(offsetX, offsetY, dstW, dstH), Color.White);
                _spriteBatch.End();
                
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

#if ANDROID
    /// <summary>
    /// Merge touch controls into keyboard state (Android only)
    /// </summary>
    private KeyboardState MergeTouchWithKeyboard(KeyboardState kb)
    {
        if (_touchControls == null) return kb;
        
        // Get all currently pressed keys from keyboard
        var pressedKeys = new List<Keys>(kb.GetPressedKeys());
        
        // Add virtual keys from touch controls
        // Include D, F, J, K for gameplay (note lanes) + menu navigation keys + Q, E for settings tabs
        foreach (var key in new[] { Keys.D, Keys.F, Keys.J, Keys.K, Keys.Left, Keys.Right, Keys.Up, Keys.Down, Keys.Space, Keys.Enter, Keys.Escape, Keys.P, Keys.Q, Keys.E })
        {
            if (_touchControls.IsKeyDown(key) && !pressedKeys.Contains(key))
                pressedKeys.Add(key);
        }
        
        // Create new keyboard state with merged keys
        return new KeyboardState(pressedKeys.ToArray());
    }
#endif

    /// <summary>
    /// Check if a key is down (combines physical keyboard + touch controls on Android)
    /// </summary>
    private bool IsKeyDown(KeyboardState kb, Keys key)
    {
#if ANDROID
        // On Android, check both keyboard (for testing) and touch controls
        return kb.IsKeyDown(key) || (_touchControls?.IsKeyDown(key) ?? false);
#else
        return kb.IsKeyDown(key);
#endif
    }


    private float CalculateFixedPixelScale(int sw, int sh)
    {
        // Use actual render target dimensions instead of hardcoded values
        float camW = _rtWidth;
        float camH = _rtHeight;
        bool isLandscape = _level?.Scene.Landscape == true;
        return isLandscape 
            ? Math.Min((float)sw / camW, (float)sh / camH)
            : Math.Max((float)sw / camW, (float)sh / camH);
    }

    private Matrix BuildCameraMatrix(int sw, int sh)
    {
        if (_events == null) return Matrix.Identity;
        
        // Usar CameraManager para construir la matriz con zoom beat-synced
        return _cameraManager?.BuildCameraMatrix(
            sw, sh, 
            _events.Camera,
            _events.HasCameraEvents
        ) ?? Matrix.Identity;
    }

    /// <summary>
    /// Camera matrix for fixed (screen-space) objects when zoom override is active.
    /// Uses the world center as the camera pivot so fixed objects don't drift.
    /// </summary>
    private Matrix BuildFixedZoomMatrix(int sw, int sh)
    {
        bool isLandscape = _level?.Scene.Landscape == true;
        return _cameraManager?.BuildFixedZoomMatrix(sw, sh, isLandscape) ?? Matrix.Identity;
    }

    // ── Scene drawing ────────────────────────────────────────────────────────
    // DrawScene is called with SpriteBatch already Begin'd with LinearClamp + camMatrix.
    // Objects with bilinear=false need PointClamp — we flush and restart the batch for those.
    private void DrawScene()
    {
        if (_level == null) return;
        DrawObjectGroup(worldOnly: true);
    }

    /// <summary>Draw fixed=true objects without camera transform (HUD layer).</summary>
    private void DrawFixedObjects()
    {
        if (_level == null) return;
        DrawObjectGroup(worldOnly: false);
    }

    /// <summary>
    /// Draws ALL scene objects in their index order, switching camera transform
    /// per-object. This preserves the author's intended layering — fixed objects
    /// can appear above or below world objects.
    /// </summary>
    private void DrawSceneOrdered(int sw, int sh, Matrix camMatrix)
    {
        if (_level == null) return;
        var objs = _level.Scene.Objects;

        Matrix fixedWorldMatrix = BuildFixedMatrix(sw, sh);
        int  currentMode     = -1;
        bool currentBilinear = true;

        // Apply color grading shader parameters if enabled (FNF Erect-style - Subtle!)
        Effect? activeEffect = null;
        if (ColorGradeEnabled && _colorGradeShader != null)
        {
            activeEffect = _colorGradeShader;
            _colorGradeShader.Parameters["Hue"]?.SetValue(ColorGradeHue);
            _colorGradeShader.Parameters["Saturation"]?.SetValue(ColorGradeSaturation);
            _colorGradeShader.Parameters["Brightness"]?.SetValue(ColorGradeBrightness);
            _colorGradeShader.Parameters["Contrast"]?.SetValue(ColorGradeContrast);
            _colorGradeShader.Parameters["ShadowAmount"]?.SetValue(ColorGradeShadowAmount);
            _colorGradeShader.Parameters["ShadowTint"]?.SetValue(new Microsoft.Xna.Framework.Vector3(
                ColorGradeShadowTintR,
                ColorGradeShadowTintG,
                ColorGradeShadowTintB
            ));
            
            // Set technique
            if (_colorGradeShader.Techniques.Count > 0)
            {
                _colorGradeShader.CurrentTechnique = _colorGradeShader.Techniques[0];
            }
        }

        for (int i = 0; i < objs.Count; i++)
        {
            var obj  = objs[i];
            var anim = i < _objAnimators.Length ? _objAnimators[i] : null;
            if (anim == null)
            {
                if (i < objs.Count && !string.IsNullOrEmpty(objs[i].Spr))
                    Console.WriteLine($"[Scene] Object {i} ({objs[i].Spr}) has no animator — sprite missing from level?");
                continue;
            }

            // Use bilinear from JSON if sprite specifies it, otherwise use global setting
            bool wantBilinear = anim?.CurrentBilinear ?? false;
            int  wantMode;

            var pos = obj.GetPosition();
            var scale = obj.GetScale();

            // Check if object has Transform array (for scrollFactor)
            // scrollFactor applies to all objects with Transform array, regardless of count
            bool hasTransformArray = obj.Transform != null && obj.Transform.Count > 0;
            bool hasPositionEvents = hasTransformArray && _events != null && _events.HasPositionEvents(i);

            // Log first 3 visible objects once at start
            // Removed spammy debug log

            if (!obj.Fixed)
                wantMode = 0;
            else
            {
                // Use initial coordinates (obj.X/Y) to determine if normalized, not current position
                bool normalized = obj.X >= 0f && obj.X <= 1f && obj.Y >= 0f && obj.Y <= 1f;
                wantMode = normalized ? 1 : 2;
            }

            // For Fixed objects with position events using pixel coords (wantMode 2),
            // use null matrix (like wantMode 1) to avoid fixedWorldMatrix transformation
            int matrixMode = wantMode;
            if (obj.Fixed && wantMode == 2 && hasPositionEvents)
            {
                matrixMode = 1; // Use null matrix for rendering
            }

            if (matrixMode != currentMode || wantBilinear != currentBilinear)
            {
                if (currentMode >= 0) _spriteBatch.End();
                Matrix? matrix = matrixMode == 0 ? camMatrix
                               : matrixMode == 2 ? fixedWorldMatrix
                               : (Matrix?)null;
                var sampler = wantBilinear ? SamplerState.LinearClamp : SamplerState.PointClamp;
                // Apply shader effect here (only to scene objects, not to HUD/arrows)
                _spriteBatch.Begin(SpriteSortMode.Immediate, BlendState.AlphaBlend,
                    sampler, null, null, activeEffect, matrix);
                currentMode     = matrixMode;
                currentBilinear = wantBilinear;
            }

            // Check for active tween
            bool hasActiveTween = _events != null && _events.HasActiveTween(i);
            
            // Update position and scale if NOT controlled by active tween
            if (!hasActiveTween)
            {
                if (obj.Fixed && wantMode == 2 && hasPositionEvents)
                {
                    // Fixed pixel-coordinate objects with position events:
                    // EventProcessor controls position/scale via tweens - don't override
                    // Position is already set by EventProcessor in anim.Position
                }
                else if (!hasPositionEvents || hasTransformArray)
                {
                    Vector2 finalPos = new Vector2(pos.X, pos.Y);
                    
                    // Apply scrollFactor for parallax depth effect (FNF/HaxeFlixel style)
                    // FunkyMaker behavior:
                    //   - Objects with scrollFactor < 1.0 appear to move slower (parallax background)
                    //   - BUT they must arrive at their final position when camera transition completes
                    //   - The parallax effect is visual only - not a timing delay
                    //
                    // Formula: finalPos = objectWorldPos - (cameraCurrentPos * (scrollFactor - 1.0))
                    // This ensures objects reach their final screen position in sync with camera,
                    // but appear to move at different speeds during transition.
                    if (hasTransformArray)
                    {
                        Vec2 scrollFactor = obj.GetScrollFactor();
                        var cam = _events?.Camera;
                        
                        if (cam != null && anim != null && (Math.Abs(scrollFactor.X - 1f) > 0.001f || Math.Abs(scrollFactor.Y - 1f) > 0.001f))
                        {
                            // Clamp scrollFactor to valid range [0.01, 4.0]
                            float clampedFactorX = Math.Clamp(scrollFactor.X, 0.01f, 4.0f);
                            float clampedFactorY = Math.Clamp(scrollFactor.Y, 0.01f, 4.0f);
                            
                            // FunkyMaker scrollFactor formula:
                            // scrollFactor determines what PERCENTAGE of camera movement affects the object
                            // - scrollFactor = 1.0: object moves 100% with camera (normal, no parallax)
                            // - scrollFactor = 0.7: object moves 70% with camera (slower background)
                            // - scrollFactor = 0.5: object moves 50% with camera (much slower background)
                            // 
                            // Formula: finalPos = worldPos + (cameraPos * (1.0 - scrollFactor))
                            // This makes the object appear to move slower by offsetting it based on camera movement
                            finalPos.X = pos.X + cam.X * (1.0f - clampedFactorX);
                            finalPos.Y = pos.Y + cam.Y * (1.0f - clampedFactorY);
                            
                            // Debug log once per level (prevent spam)
                            if (!_parallaxLoggedOnce && i < 3)
                            {
                                Console.WriteLine($"[FunkyMaker-Parallax] Obj{i}: world=({pos.X:F0},{pos.Y:F0}) cam=({cam.X:F0},{cam.Y:F0}) scroll=({clampedFactorX:F2},{clampedFactorY:F2}) offset=({cam.X * (1.0f - clampedFactorX):F0},{cam.Y * (1.0f - clampedFactorY):F0}) → final=({finalPos.X:F0},{finalPos.Y:F0})");
                                if (i == 2) _parallaxLoggedOnce = true;  // Mark as logged after 3rd object
                            }
                        }
                    }
                    
                    if (anim != null)
                    {
                        anim.Position = finalPos;
                        anim.Scale = new Vector2(scale.X, scale.Y);
                    }
                }
            }
            
            // Ensure anim is not null before continuing
            if (anim == null) continue;
            
            // Set FixedPixelScale for Fixed objects (using Blazor logic)
            if (obj.Fixed)
            {
                if (wantMode == 1)
                {
                    // Normalized coordinates (0-1): scale to screen pixels and apply FixedPixelScale
                    // Only update position if no active tween (EventProcessor controls during tweens)
                    if (!hasActiveTween)
                    {
                        anim.Position = new Vector2(pos.X * sw, pos.Y * sh);
                    }
                    const float CAM_W = 1920f;
                    const float CAM_H = 1080f;
                    float camScale = _events?.Camera.Scale ?? 1f;
                    float actual = Math.Min((float)sw / (CAM_W * camScale), (float)sh / (CAM_H * camScale));
                    float basePs = Math.Min((float)sw / CAM_W, (float)sh / CAM_H);
                    anim.FixedPixelScale = Math.Min(actual, basePs);
                }
                else // wantMode == 2
                {
                    // Fixed world coordinates (mode 2): use same FixedPixelScale formula as mode 1
                    // Only update position if no position events AND no active tween
                    if (!hasPositionEvents && !hasActiveTween)
                    {
                        // No position events: update position from Transform
                        anim.Position = new Vector2(pos.X, pos.Y);
                    }
                    // else: EventProcessor controls position via tweens, keep current anim.Position
                    
                    const float CAM_W = 1920f;
                    const float CAM_H = 1080f;
                    float camScale = _events?.Camera.Scale ?? 1f;
                    float actual = Math.Min((float)sw / (CAM_W * camScale), (float)sh / (CAM_H * camScale));
                    float basePs = Math.Min((float)sw / CAM_W, (float)sh / CAM_H);
                    anim.FixedPixelScale = Math.Min(actual, basePs);
                }
            }
            else
            {
                anim.FixedPixelScale = 0f;
            }

            anim.Draw(_spriteBatch);
        }

        if (currentMode >= 0) _spriteBatch.End();
    }

    /// <summary>
    /// Camera-independent matrix for fixed world-coord objects.
    /// Same pixel scale as the world camera but pivoted at world center — no pan/shake.
    /// </summary>
    private Matrix BuildFixedMatrix(int sw, int sh)
    {
        bool isLandscape = _level?.Scene.Landscape == true;
        float camScale = _events?.Camera.Scale ?? 1f;
        return _cameraManager?.BuildFixedMatrix(sw, sh, isLandscape, camScale) ?? Matrix.Identity;
    }

    /// <summary>
    /// Draws scene objects, switching sampler state per-object based on bilinear flag.
    /// SpriteBatch must already be Begin'd before calling.
    /// worldOnly=true → skip fixed objects. worldOnly=false → only fixed objects.
    /// </summary>
    private void DrawObjectGroup(bool worldOnly)
    {
        if (_level == null) return;
        var objs = _level.Scene.Objects;
        int sw = _rtWidth;
        int sh = _rtHeight;

        bool currentBilinear = true; // SpriteBatch starts with LinearClamp

        for (int i = 0; i < objs.Count; i++)
        {
            var obj = objs[i];
            if (worldOnly  &&  obj.Fixed) continue;
            if (!worldOnly && !obj.Fixed) continue;

            var anim = i < _objAnimators.Length ? _objAnimators[i] : null;
            if (anim == null) continue;

            // Fixed objects use normalized coords (0..1) → scale to screen pixels
            // e.g. x=0.5, y=0.5 → center of screen
            if (!worldOnly)
            {
                anim.Position = new Vector2(obj.X * sw, obj.Y * sh);
            }

            bool wantBilinear = anim.CurrentBilinear;

            if (wantBilinear != currentBilinear)
            {
                _spriteBatch.End();
                if (wantBilinear)
                    _spriteBatch.Begin(
                        transformMatrix: worldOnly ? _lastCamMatrix : (Matrix?)null,
                        samplerState: SamplerState.LinearClamp);
                else
                    _spriteBatch.Begin(
                        transformMatrix: worldOnly ? _lastCamMatrix : (Matrix?)null,
                        samplerState: SamplerState.PointClamp);
                currentBilinear = wantBilinear;
            }

            anim.Draw(_spriteBatch);
        }

        // Restore to LinearClamp with the correct transform
        if (!currentBilinear)
        {
            _spriteBatch.End();
            _spriteBatch.Begin(
                transformMatrix: worldOnly ? _lastCamMatrix : (Matrix?)null,
                samplerState: SamplerState.LinearClamp);
        }
    }

    private void ShowJudgement(string text, Color color)
    {
        _judgementText  = text;
        _judgementColor = color;
        _judgementTimer = JUDGEMENT_SHOW;
    }

    // ── Notes drawing ────────────────────────────────────────────────────────
    private void DrawNotes()
    {
        if (_level == null) return;
        float laneW = NoteRenderer.LANE_COUNT * NoteRenderer.LANE_WIDTH;
        var   objs  = _level.Scene.Objects;
        int   count = _noteRenderers.Count;

        // Position strumlines based on mode:
        // - Classic mode (HideExtraStrumlines = true): 2 strumlines in fixed FNF positions (centered)
        // - Extra strumlines mode (HideExtraStrumlines = false AND count >= 3): position strumlines 1+ above characters
        // This allows levels with 3+ strumlines to work in classic mode when the setting is enabled
        
        bool useClassicMode = HideExtraStrumlines || count < 3;
        
        if (!useClassicMode)
        {
            // Extra strumlines mode: position opponent (1) and extras (2+) over their characters
            // For each strumline 1+, find the VISIBLE character currently assigned to it
            // Use _objAnimators to get runtime strumLine (handles ObjectChartChange events)
            for (int slIndex = 1; slIndex < count; slIndex++)
            {
                SpriteAnimator? bestChar = null;
                int bestObjIndex = -1;

                // Find the first visible object currently on this strumLine
                for (int objIdx = 0; objIdx < _objAnimators.Length && objIdx < objs.Count; objIdx++)
                {
                    var anim = _objAnimators[objIdx];
                    var obj  = objs[objIdx];
                    
                    // Check if this object is currently assigned to this strumLine
                    // Use runtime strumLine from scene object (updated by ObjectChartChange)
                    int currentStrumLine = (int)obj.StrumLine;
                    
                    // Check visibility
                    bool isVisible = anim != null && anim.Visible;
                    bool isOnThisStrumLine = currentStrumLine == slIndex;
                    
                    if (isVisible && isOnThisStrumLine)
                    {
                        bestChar = anim;
                        bestObjIndex = objIdx;
                        break;  // Use first visible character on this strumLine
                    }
                }

                // Position strumLine over the character
                if (bestChar != null && bestObjIndex >= 0)
                {
                    var obj = objs[bestObjIndex];
                    var v   = Vector2.Transform(new Vector2(obj.X, obj.Y), _lastCamMatrix);
                    float sy = _rtHeight / 720f;
                    _noteRenderers[slIndex].SetXOffset(v.X - laneW * 0.6f / 2f);
                    _noteRenderers[slIndex].SetStrumY(v.Y - 160f * sy);
                    _noteRenderers[slIndex].NoteScale = 0.6f;
                }
                else
                {
                    // Fallback: if no character found, stack above opponent (strumLine 1)
                    // Extra strumlines (2+) should appear above the enemy, not the player
                    var oppObj = objs.Find(o => (int)o.StrumLine == 1);
                    if (oppObj != null)
                    {
                        var v = Vector2.Transform(new Vector2(oppObj.X, oppObj.Y), _lastCamMatrix);
                        float sy = _rtHeight / 720f;
                        // Position above opponent, stacked vertically for multiple extra strumlines
                        float verticalOffset = 180f * sy * (slIndex - 1);  // Stack upward
                        _noteRenderers[slIndex].SetXOffset(v.X - laneW * 0.6f / 2f);
                        _noteRenderers[slIndex].SetStrumY(v.Y - 160f * sy - verticalOffset);
                        _noteRenderers[slIndex].NoteScale = 0.6f;
                    }
                }
            }
        }

        // Draw all strumlines — opponent first, player on top
        for (int i = count - 1; i >= 0; i--)
        {
            // Extra strumlines (2+): respect HideExtraStrumlines setting
            if (i >= 2 && HideExtraStrumlines)
            {
                _noteRenderers[i].Alpha = 0f;
            }
            // Opponent strumline (1): ALWAYS show during multiplayer, respect setting otherwise
            else if (i == 1 && HideOpponentStrumline && !_isMultiplayerMatch)
            {
                _noteRenderers[i].Alpha = 0f;
            }
            else
            {
                _noteRenderers[i].Alpha = _notesAlpha;
            }
            _noteRenderers[i].Draw(_spriteBatch, _songTime, _font);
        }
    }

    // ── HUD ──────────────────────────────────────────────────────────────────

    /// <summary>
    /// Draws a health bar icon using its SpriteAnimator.
    /// destX/destY/size define the bounding square in screen-space pixels.
    /// The icon is scaled to fit while preserving aspect ratio, centered in the square.
    /// flipX=true mirrors the icon (player icon faces left in FNF).
    /// bounceScale applies additional scaling for beat bounce animation.
    /// </summary>
    private void DrawIcon(SpriteAnimator? anim, int destX, int destY, int size, bool flipX, float bounceScale = 1.0f)
    {
        if (anim == null) return;
        var (tex, src) = anim.GetCurrentFrameTexture();
        if (tex == null || src.Width <= 0 || src.Height <= 0) return;

        // Scale to fit the bounding square, preserving aspect ratio
        float scaleX = (float)size / src.Width;
        float scaleY = (float)size / src.Height;
        float scale  = Math.Min(scaleX, scaleY) * bounceScale;  // Apply bounce scale

        int drawW = (int)(src.Width  * scale);
        int drawH = (int)(src.Height * scale);

        // Center within the bounding square (bounce scales from center)
        int drawX = destX + (size - drawW) / 2;
        int drawY = destY + (size - drawH) / 2;

        var dest = new Rectangle(drawX, drawY, drawW, drawH);
        var fx   = flipX ? SpriteEffects.FlipHorizontally : SpriteEffects.None;

        // Use bilinear filtering for icons if the animator requests it
        var sampler = anim.CurrentBilinear
            ? SamplerState.LinearClamp
            : SamplerState.PointClamp;

        // SpriteBatch is already open — we can't change sampler mid-batch.
        // DrawIcon is called inside the HUD spriteBatch.Begin(PointClamp) block,
        // so we just draw normally; bilinear is a nice-to-have, not critical here.
        _spriteBatch.Draw(tex, dest, src, Color.White, 0f, Vector2.Zero, fx, 0f);
    }

    private void DrawHUD(int sw, int sh)
    {
        // GUI event can hide the entire engine HUD (health bar, score, timer, etc.)
        if (_events?.HudVisible == false) return;

        // Get HUD settings (adjust for downscroll if needed)
        var hudSettings = NoteRenderer.Downscroll 
            ? HudSettings.Instance.GetDownscrollAdjusted(sh) 
            : HudSettings.Instance;

        // ── Health bar (FNF style) ───────────────────────────────────────────
        // If the level has its own "barra" sprite object, it handles the bar visually.
        // We still draw the engine bar so health is always visible, but skip it
        // when the level provides its own UI layer.
        bool levelHasOwnBar = _level != null &&
            _level.Scene.Objects.Exists(o => o.Spr == "barra" && o.Fixed);

        if (!levelHasOwnBar && _healthBar != null)
        {
            _healthBar.Draw(sh, NoteRenderer.Downscroll);
        }
        
        if (_font == null || _noteRenderers.Count == 0 || _level == null) return;

        // Get player renderer (strumline 0)
        var playerRenderer = _noteRenderers.Count > 0 ? _noteRenderers[0] : null;
        if (playerRenderer == null) return;

        // ── Stats below health bar (FNF style) ───────────────────────────────
        if (!levelHasOwnBar)
        {
            // ── MULTIPLAYER HUD ──────────────────────────────────────────────
            if (_isMultiplayerMatch && _font != null && _pixel != null && _noteRenderers.Count >= 2)
            {
                float statsScale = 0.75f;
                var playerRend = _noteRenderers[0];
                
                // ── SPLIT HEALTH BARS (Left: Opponent, Right: Player) ────────
                int barWidth = 300;
                int barHeight = 20;
                int barY = sh - 100; // Position near bottom
                
                // Left side: Opponent health bar
                int opponentBarX = sw / 4 - barWidth / 2; // Center of left quarter
                
                // Opponent bar background
                DrawRect(opponentBarX - 3, barY - 3, barWidth + 6, barHeight + 6, Color.Black * 0.6f);
                DrawRect(opponentBarX - 2, barY - 2, barWidth + 4, barHeight + 4, Color.Black);
                DrawRect(opponentBarX, barY, barWidth, barHeight, new Color(30, 30, 30));
                
                // Opponent health fill (full = 100%, empty = 0%)
                int opponentHealthW = (int)(_opponentHealth * barWidth);
                for (int i = 0; i < opponentHealthW; i++)
                {
                    float t = (float)i / barWidth;
                    Color opponentColor = _healthBar?.OpponentBarColor ?? new Color(0xFF, 0x00, 0x00);
                    Color c = Color.Lerp(opponentColor * 0.7f, opponentColor * 1.1f, t);
                    DrawRect(opponentBarX + i, barY, 1, barHeight, c);
                }
                
                // Opponent icon above bar
                int iconSize = 80;
                int opponentIconX = opponentBarX + barWidth / 2 - iconSize / 2;
                int opponentIconY = barY - iconSize - 10;
                if (_healthBar?.OpponentIcon != null)
                {
                    var (tex, src) = _healthBar.OpponentIcon.GetCurrentFrameTexture();
                    if (tex != null && src.Width > 0 && src.Height > 0)
                    {
                        float scale = (float)iconSize / Math.Max(src.Width, src.Height);
                        int drawW = (int)(src.Width * scale);
                        int drawH = (int)(src.Height * scale);
                        int drawX = opponentIconX + (iconSize - drawW) / 2;
                        int drawY = opponentIconY + (iconSize - drawH) / 2;
                        _spriteBatch.Draw(tex, new Rectangle(drawX, drawY, drawW, drawH), src, Color.White);
                    }
                }
                
                // Opponent stats below bar
                string opponentStats = $"{_opponentName ?? "Opponent"}\nScore: {_opponentScore}\nHealth: {(_opponentHealth * 100):F0}%";
                Vector2 opponentStatsSize = _font.MeasureString(opponentStats) * statsScale;
                Vector2 opponentStatsPos = new Vector2(opponentBarX + barWidth / 2 - opponentStatsSize.X / 2, barY + barHeight + 8);
                
                _spriteBatch.DrawString(_font, opponentStats,
                    opponentStatsPos + new Vector2(2, 2), Color.Black * 0.7f, 
                    0f, Vector2.Zero, statsScale, SpriteEffects.None, 0f);
                _spriteBatch.DrawString(_font, opponentStats,
                    opponentStatsPos, new Color(255, 100, 100), 
                    0f, Vector2.Zero, statsScale, SpriteEffects.None, 0f);
                
                // Right side: Player health bar
                int playerBarX = (sw * 3) / 4 - barWidth / 2; // Center of right quarter
                
                // Player bar background
                DrawRect(playerBarX - 3, barY - 3, barWidth + 6, barHeight + 6, Color.Black * 0.6f);
                DrawRect(playerBarX - 2, barY - 2, barWidth + 4, barHeight + 4, Color.Black);
                DrawRect(playerBarX, barY, barWidth, barHeight, new Color(30, 30, 30));
                
                // Player health fill
                float playerHealth = _healthBar?.Health ?? 0.5f;
                int playerHealthW = (int)(playerHealth * barWidth);
                for (int i = 0; i < playerHealthW; i++)
                {
                    float t = (float)i / barWidth;
                    Color playerColor = _healthBar?.PlayerBarColor ?? new Color(0x66, 0xFF, 0x66);
                    Color c = Color.Lerp(playerColor * 0.7f, playerColor * 1.1f, t);
                    DrawRect(playerBarX + i, barY, 1, barHeight, c);
                }
                
                // Player icon above bar
                int playerIconX = playerBarX + barWidth / 2 - iconSize / 2;
                int playerIconY = barY - iconSize - 10;
                if (_healthBar?.PlayerIcon != null)
                {
                    var (tex, src) = _healthBar.PlayerIcon.GetCurrentFrameTexture();
                    if (tex != null && src.Width > 0 && src.Height > 0)
                    {
                        float scale = (float)iconSize / Math.Max(src.Width, src.Height);
                        int drawW = (int)(src.Width * scale);
                        int drawH = (int)(src.Height * scale);
                        int drawX = playerIconX + (iconSize - drawW) / 2;
                        int drawY = playerIconY + (iconSize - drawH) / 2;
                        _spriteBatch.Draw(tex, new Rectangle(drawX, drawY, drawW, drawH), src, Color.White);
                    }
                }
                
                // Player stats below bar
                int totalPlayerNotes = playerRend.Sicks + playerRend.Goods + playerRend.Bads + 
                                     playerRend.Shits + playerRend.Misses;
                float playerAccuracy = 100f;
                if (totalPlayerNotes > 0)
                {
                    float weightedHits = (playerRend.Sicks * 1.0f) + (playerRend.Goods * 0.75f) + 
                                        (playerRend.Bads * 0.5f) + (playerRend.Shits * 0.25f);
                    playerAccuracy = (weightedHits / totalPlayerNotes) * 100f;
                }
                
                string playerStats = $"You\nScore: {playerRend.Score}\nAccuracy: {playerAccuracy:F0}%\nCombo: x{playerRend.Combo}";
                Vector2 playerStatsSize = _font.MeasureString(playerStats) * statsScale;
                Vector2 playerStatsPos = new Vector2(playerBarX + barWidth / 2 - playerStatsSize.X / 2, barY + barHeight + 8);
                
                _spriteBatch.DrawString(_font, playerStats,
                    playerStatsPos + new Vector2(2, 2), Color.Black * 0.7f, 
                    0f, Vector2.Zero, statsScale, SpriteEffects.None, 0f);
                _spriteBatch.DrawString(_font, playerStats,
                    playerStatsPos, new Color(100, 255, 100), 
                    0f, Vector2.Zero, statsScale, SpriteEffects.None, 0f);
                
                // ── CENTER INFO (Accuracy & Score comparison) ─────────────────
                float centerScale = 0.9f;
                int centerY = sh - 180;
                
                // Accuracy comparison
                string accuracyText = $"Accuracy: {playerAccuracy:F1}%";
                Vector2 accuracySize = _font.MeasureString(accuracyText) * centerScale;
                Vector2 accuracyPos = new Vector2(sw / 2 - accuracySize.X / 2, centerY);
                
                _spriteBatch.DrawString(_font, accuracyText,
                    accuracyPos + new Vector2(2, 2), Color.Black * 0.7f, 
                    0f, Vector2.Zero, centerScale, SpriteEffects.None, 0f);
                _spriteBatch.DrawString(_font, accuracyText,
                    accuracyPos, Color.White, 
                    0f, Vector2.Zero, centerScale, SpriteEffects.None, 0f);
                
                // Score comparison
                int scoreDiff = playerRend.Score - _opponentScore;
                string scoreText = scoreDiff >= 0 ? $"+{scoreDiff}" : $"{scoreDiff}";
                Color scoreColor = scoreDiff >= 0 ? new Color(100, 255, 100) : new Color(255, 100, 100);
                
                Vector2 scoreSize = _font.MeasureString(scoreText) * centerScale;
                Vector2 scorePos = new Vector2(sw / 2 - scoreSize.X / 2, centerY + 30);
                
                _spriteBatch.DrawString(_font, scoreText,
                    scorePos + new Vector2(2, 2), Color.Black * 0.7f, 
                    0f, Vector2.Zero, centerScale, SpriteEffects.None, 0f);
                _spriteBatch.DrawString(_font, scoreText,
                    scorePos, scoreColor, 
                    0f, Vector2.Zero, centerScale, SpriteEffects.None, 0f);
                
                // ── PING INDICATOR ────────────────────────────────────────────
                int ping = Multiplayer.NetworkManager.Instance.Ping;
                var quality = Multiplayer.NetworkManager.Instance.Quality;
                
                // Determine color based on ping quality
                Color pingColor = quality switch
                {
                    Multiplayer.ConnectionQuality.Excellent => new Color(100, 255, 100),  // Green
                    Multiplayer.ConnectionQuality.Good => new Color(150, 255, 150),       // Light green
                    Multiplayer.ConnectionQuality.Fair => Color.Yellow,                   // Yellow
                    Multiplayer.ConnectionQuality.Poor => new Color(255, 150, 0),         // Orange
                    Multiplayer.ConnectionQuality.Critical => new Color(255, 50, 50),     // Red
                    _ => Color.Gray                                                        // Unknown
                };
                
                string pingText = ping > 0 ? $"{ping}ms" : "...";
                float pingScale = 0.7f;
                Vector2 pingSize = _font.MeasureString(pingText) * pingScale;
                Vector2 pingPos = new Vector2(sw / 2 - pingSize.X / 2, centerY + 65);
                
                // Ping background (subtle)
                DrawRect((int)(pingPos.X - 5), (int)(pingPos.Y - 2), (int)(pingSize.X + 10), (int)(pingSize.Y + 4), 
                         Color.Black * 0.5f);
                
                // Ping shadow
                _spriteBatch.DrawString(_font, pingText,
                    pingPos + new Vector2(1, 1), Color.Black * 0.7f, 
                    0f, Vector2.Zero, pingScale, SpriteEffects.None, 0f);
                
                // Ping text
                _spriteBatch.DrawString(_font, pingText,
                    pingPos, pingColor, 
                    0f, Vector2.Zero, pingScale, SpriteEffects.None, 0f);
            }
            // ── SINGLEPLAYER HUD ─────────────────────────────────────────────
            else
            {
                // DOWNSCROLL MODE: stats positioned relative to healthbar
                float statsScale = 0.85f;
                
                // Calculate accuracy using FNF rating system
                int totalNotes = playerRenderer.Sicks + playerRenderer.Goods + playerRenderer.Bads + 
                               playerRenderer.Shits + playerRenderer.Misses;
                float accuracy = 100f;
                if (totalNotes > 0)
                {
                    float weightedHits = (playerRenderer.Sicks * 1.0f) + (playerRenderer.Goods * 0.75f) + 
                                        (playerRenderer.Bads * 0.5f) + (playerRenderer.Shits * 0.25f);
                    accuracy = (weightedHits / totalNotes) * 100f;
                }
                
                // Score (using HudSettings position)
                string scoreText = $"Score: {playerRenderer.Score}";
                Vector2 scorePos = new Vector2(hudSettings.ScorePosition.X, hudSettings.ScorePosition.Y);
                // Shadow
                _spriteBatch.DrawString(_font!, Safe(scoreText),
                    scorePos + new Vector2(2, 2), Color.Black * 0.7f, 0f, Vector2.Zero, statsScale, SpriteEffects.None, 0f);
                // Main text
                _spriteBatch.DrawString(_font!, Safe(scoreText),
                    scorePos, Color.White, 0f, Vector2.Zero, statsScale, SpriteEffects.None, 0f);
                
                // Misses (using HudSettings position)
                string missesText = $"Misses: {playerRenderer.Misses}";
                Vector2 missesPos = new Vector2(hudSettings.MissesPosition.X, hudSettings.MissesPosition.Y);
                Color missesColor = playerRenderer.Misses > 0 ? new Color(255, 100, 100) : Color.White;
                // Shadow
                _spriteBatch.DrawString(_font!, Safe(missesText),
                    missesPos + new Vector2(2, 2), Color.Black * 0.7f, 0f, Vector2.Zero, statsScale, SpriteEffects.None, 0f);
                // Main text
                _spriteBatch.DrawString(_font!, Safe(missesText),
                    missesPos, missesColor, 0f, Vector2.Zero, statsScale, SpriteEffects.None, 0f);
                
                // Accuracy (using HudSettings position)
                string accuracyText = $"Accuracy: {accuracy:F1}%";
                Color accColor = accuracy >= 95f ? new Color(100, 255, 100) : 
                               accuracy >= 80f ? Color.Yellow : 
                               accuracy >= 60f ? new Color(255, 200, 100) : 
                               new Color(255, 100, 100);
                Vector2 accPos = new Vector2(hudSettings.AccuracyPosition.X, hudSettings.AccuracyPosition.Y);
                // Shadow
                _spriteBatch.DrawString(_font!, Safe(accuracyText),
                    accPos + new Vector2(2, 2), Color.Black * 0.7f, 0f, Vector2.Zero, statsScale, SpriteEffects.None, 0f);
                // Main text
                _spriteBatch.DrawString(_font!, Safe(accuracyText),
                    accPos, accColor, 0f, Vector2.Zero, statsScale, SpriteEffects.None, 0f);
            }
        }

        // ── Song timer bar ──────────────────────────────────────────────────────
        if (_timerBar != null)
        {
            // Sync configuration from Game1 static fields
            _timerBar.ShowTimer = ShowTimer;
            _timerBar.Preset = TimerPreset;
            _timerBar.TextMode = TimerTextMode;
            _timerBar.ForceTop = TimerForceTop;
            _timerBar.ColorPreset = TimerColorPreset;
            _timerBar.Position = new Point(hudSettings.TimerBarPosition.X, hudSettings.TimerBarPosition.Y);
            _timerBar.Size = new Point(hudSettings.TimerBarSize.X, hudSettings.TimerBarSize.Y);
            
            // Draw timer bar
            _timerBar.Draw(sw, sh, _songTime, _songEndTime, NoteRenderer.Downscroll, 
                         _level?.LevelName, _healthBar?.PlayerBarColor, _healthBar?.OpponentBarColor, _started);
        }

        // Song info (using HudSettings position) with shadow
        Vector2 songInfoPos = new Vector2(hudSettings.SongInfoPosition.X, hudSettings.SongInfoPosition.Y);
        float songScale = 1.0f;
        string songTitle = Safe(_level.LevelName);
        
        // Shadow for better readability
        _spriteBatch.DrawString(_font, songTitle,
            songInfoPos + new Vector2(2, 2), Color.Black * 0.7f, 0f, Vector2.Zero, songScale, SpriteEffects.None, 0f);
        
        // Main text
        _spriteBatch.DrawString(_font, songTitle,
            songInfoPos, Color.White, 0f, Vector2.Zero, songScale, SpriteEffects.None, 0f);
        
        
        // Debug Info: Level technical details (always top-left below song title and FPS)
        if (ShowDebugInfo)
        {
            float debugInfoY = 48; // Fixed position below song title
            _spriteBatch.DrawString(_font,
                Safe($"BPM: {_level.Data.EffectiveBpm}  Beat: {_songBeats:F1}"),
                new Vector2(20, debugInfoY), Color.White * 0.5f);
            if (_events != null)
            {
                float debugCamY = 58; // Fixed position
                float worldW   = _level?.Scene.Landscape == true ? 2160f : 1920f;
                float worldH   = _level?.Scene.Landscape == true ? 1920f : 2160f;
                float dbgScale = _cameraManager?.GetEffectiveScale(_events.Camera) ?? 1f;
                float dbgPx    = Math.Min((float)_rtWidth  / (worldW * dbgScale),
                                          (float)_rtHeight / (worldH * dbgScale));
                float dbgSx = (_events.Camera.X) * dbgPx;
                float dbgSy = (_events.Camera.Y) * dbgPx;
                _spriteBatch.DrawString(_font,
                    Safe($"Cam: ({_events.Camera.X:F0},{_events.Camera.Y:F0}) zoom={_events.Camera.Scale:F3} base={_events.Camera.BaseScale:F3} px={dbgPx:F3}" +
                         (_cameraManager?.HasManualOverride == true ? $" [manual:{_cameraManager.ScaleOverride:F2}]" : "") +
                         (_events.HasCameraEvents ? " [EVENT]" : $" [AUTO→({_events.AutoCamTargetX:F0},{_events.AutoCamTargetY:F0})]")),
                    new Vector2(20, debugCamY), Color.Cyan * 0.7f);

                // Show unknown event types encountered so far — helps identify missing implementations
                var unknown = _events.UnknownEventTypes;
                if (unknown.Count > 0)
                {
                    string unknownStr = "UNKNOWN EVENTS: " + string.Join(", ", unknown);
                    _spriteBatch.DrawString(_font, Safe(unknownStr),
                        new Vector2(20, 76), new Color(255, 80, 80) * 0.9f);
                }
            }
        }
        
        // FPS Counter: Platform-adaptive display (if enabled)
        if (ShowFpsCounter)
        {
            float fps = (float)(1.0 / Math.Max(0.0001, _lastDt));
            
#if ANDROID
            // Mobile: Top-right corner with background for better visibility
            string fpsText = Safe($"FPS: {fps:F0}");
            Vector2 textSize = _font.MeasureString(fpsText);
            Vector2 fpsPos = new Vector2(sw - textSize.X - 20, 20);
            
            // Background box for contrast
            Rectangle bgBox = new Rectangle(
                (int)(fpsPos.X - 8),
                (int)(fpsPos.Y - 4),
                (int)(textSize.X + 16),
                (int)(textSize.Y + 8)
            );
            _spriteBatch.Draw(_pixel, bgBox, Color.Black * 0.7f);
            
            // Color-coded FPS (green = good, yellow = ok, red = bad)
            Color fpsColor = fps >= 55 ? Color.LimeGreen : (fps >= 30 ? Color.Yellow : Color.Red);
            
            // Shadow for mobile readability
            _spriteBatch.DrawString(_font, fpsText,
                fpsPos + new Vector2(2, 2), Color.Black * 0.8f);
            
            // Main text
            _spriteBatch.DrawString(_font, fpsText,
                fpsPos, fpsColor);
#else
            // PC: Below song title with VSYNC info
            Vector2 fpsPos = new Vector2(songInfoPos.X, songInfoPos.Y + 28);
            string fpsText = Safe($"FPS: {fps:F0}  {(_graphics.SynchronizeWithVerticalRetrace ? "VSYNC" : "UNCAPPED")}");
            
            // Shadow
            _spriteBatch.DrawString(_font, fpsText,
                fpsPos + new Vector2(2, 2), Color.Black * 0.6f);
            
            // Main text
            _spriteBatch.DrawString(_font, fpsText,
                fpsPos, Color.White * 0.7f);
#endif
        }

        // Controls status (always visible — useful info for the player)
        string autoStr  = NoteRenderer.Autoplay   ? "[AUTO]"  : "[MANUAL]";
        string scrollStr= NoteRenderer.Downscroll ? "[DOWN]"  : "[UP]";
        string pauseStr = _paused                 ? "[PAUSED]": "";
        string speedStr = Safe($"Speed:x{NoteRenderer.ScrollMultiplier:F2}");
        _spriteBatch.DrawString(_font,
            Safe($"{autoStr} {scrollStr} {speedStr} {pauseStr}"),
            new Vector2(20, sh - 28),
            NoteRenderer.Autoplay ? Color.Yellow * 0.8f : Color.White * 0.4f);

        // Unknown event types — only shown in debug mode to avoid being intrusive for normal players
        // (also shown in the debug overlay above when ShowDebugInfo is on)

        // Key binding overlay
        _keyBindOverlay.Draw(_spriteBatch, _font!, _pixel!, sw, sh, _noteStyle);
    }

    // ── Screen flashes ───────────────────────────────────────────────────────
    private void DrawScreenFlashes(int sw, int sh)
    {
        if (_events == null) return;
        foreach (var flash in _events.ActiveFlashes)
        {
            Color c = flash.Color * flash.Alpha;
            DrawRect(0, 0, sw, sh, c);
        }
    }

    // ── Helpers ──────────────────────────────────────────────────────────────
    private void DrawRect(int x, int y, int w, int h, Color color)
        => _spriteBatch.Draw(_pixel!, new Rectangle(x, y, w, h), color);

    private void DrawDropHint()
    {
        if (_font == null) return;
        int sw = _rtWidth;
        int sh = _rtHeight;
        string[] lines = { "Funky Maker Player", "", "Drag a .FNMM or .rar file here", "", "[ESC] Settings" };
        int lineH = (int)_font.MeasureString("A").Y + 6;
        int totalH = lines.Length * lineH;
        int y = sh / 2 - totalH / 2;
        foreach (var line in lines)
        {
            if (line.Length == 0) { y += lineH; continue; }
            var sz = _font.MeasureString(line);
            _spriteBatch.DrawString(_font, line,
                new Vector2(sw / 2f - sz.X / 2f, y),
                line == "Funky Maker Player" ? Color.Yellow : Color.White * 0.7f);
            y += lineH;
        }
    }

    private void DrawError(string msg)
    {
        if (_font == null) return;
        int sw = _rtWidth;
        int sh = _rtHeight;

        // Dark background
        DrawRect(0, 0, sw, sh, new Color(10, 5, 5) * 0.95f);
        DrawRect(0, 0, sw, 4, new Color(0xFF, 0x22, 0x22));

        // Header
        _spriteBatch.DrawString(_font, "LEVEL LOAD ERROR",
            new Vector2(20, 14), new Color(0xFF, 0x44, 0x44), 0f, Vector2.Zero, 1.1f, SpriteEffects.None, 0f);

        // Error message
        int lineH = (int)(_font.MeasureString("A").Y * 0.8f) + 4;
        int y = 52;
        foreach (var line in WrapText(Safe(msg), 90))
        {
            _spriteBatch.DrawString(_font, line, new Vector2(20, y), Color.White * 0.85f, 0f, Vector2.Zero, 0.8f, SpriteEffects.None, 0f);
            y += lineH;
        }

        // Hint
        string hint = "ENTER / ESC  Back to menu";
        var hintSz = _font.MeasureString(hint) * 0.65f;
        _spriteBatch.DrawString(_font, hint,
            new Vector2(sw / 2f - hintSz.X / 2f, sh - 28f),
            Color.White * 0.35f, 0f, Vector2.Zero, 0.65f, SpriteEffects.None, 0f);
    }

    private static string Safe(string? s)
    {
        if (s == null) return "";
        // Keep only printable ASCII (32–126) that the default SpriteFont supports
        var sb = new System.Text.StringBuilder(s.Length);
        foreach (char c in s)
            sb.Append(c >= 32 && c <= 126 ? c : '?');
        return sb.ToString();
    }

    private static IEnumerable<string> WrapText(string text, int maxLen)
    {
        while (text.Length > maxLen)
        {
            int cut = text.LastIndexOf(' ', maxLen);
            if (cut < 0) cut = maxLen;
            yield return text[..cut];
            text = text[cut..].TrimStart();
        }
        if (text.Length > 0) yield return text;
    }

    // ── Countdown overlay ─────────────────────────────────────────────────────
    private void DrawCountdown(int sw, int sh)
    {
        if (_level == null) return;
        _countdownOverlay?.Draw(sw, sh, _level.Data.EffectiveBpm, _started);
    }

    // ── Judgement overlay ─────────────────────────────────────────────────────
    private void DrawJudgement(int sw, int sh)
    {
        // Draw judgement popups and combo digits using overlay
        _judgementOverlay?.Draw();
    }

    // Game over overlay has been extracted to UI/GameOverOverlay.cs

    
    /// <summary>
    /// <summary>
    /// Helper method to draw a stat label and value in the stats panel.
    /// </summary>
    private void DrawStat(string label, string value, float x, float y, Color valueColor, float scale = 1.0f)
    {
        if (_font == null) return;
        
        float labelScale = 0.65f * scale;
        float valueScale = 1.1f * scale;
        
        // Label
        _spriteBatch.DrawString(_font, label,
            new Vector2(x, y),
            Color.White * 0.5f, 0f, Vector2.Zero, labelScale, SpriteEffects.None, 0f);
        
        // Value
        float valueY = y + _font.MeasureString(label).Y * labelScale + 3f;
        _spriteBatch.DrawString(_font, value,
            new Vector2(x, valueY),
            valueColor, 0f, Vector2.Zero, valueScale, SpriteEffects.None, 0f);
    }

    // ══════════════════════════════════════════════════════════════════════════
    // Multiplayer Event Handlers
    // ══════════════════════════════════════════════════════════════════════════

    /// <summary>
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
                // TODO: Rescan mods from the selected folder
            }
            else
            {
                Console.WriteLine("[Game1] User cancelled folder picker");
            }
        });
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
            
            // Save the file to the mods folder
            string destPath = Path.Combine(levelsFolder, fileName);
            
            using (var fileStream = File.Create(destPath))
            {
                stream.CopyTo(fileStream);
            }
            
            Console.WriteLine($"[Game1] Level saved to: {destPath}");
            
            // Refresh the freeplay menu to show the new level
            _freeplay.Refresh();
            
            // Show success message
            _errorMsg = null; // Clear any previous error
            
            // Return to freeplay
            _inFreeplay = true;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[Game1] Error loading level from stream: {ex.Message}");
            _errorMsg = $"Failed to load {fileName}:\n{ex.Message}";
        }
    }
#endif

}
