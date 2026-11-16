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

// Dark mode P/Invoke helper extracted to UI/DarkModeHelper.cs

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
    private CorneredEventHandler? _corneredEvents;  // Exclusive event handler for Cornered level
    
    // Singleton accessor for NoteRenderer to access Cornered handler
    public static Game1? Instance { get; private set; }
    internal static bool IsWindowActive => Instance?.ComputeWindowActive() ?? true;
    internal static bool CanAcceptInput =>
        Instance == null ||
        (Instance.ComputeWindowActive() && Instance._focusRegainGrace <= 0f && !Instance._waitMouseReleaseAfterFocus);

    internal static MouseState NeutralMouseState(MouseState ms, bool offscreen = false) =>
        new(offscreen ? -9999 : ms.X, offscreen ? -9999 : ms.Y, ms.ScrollWheelValue,
            ButtonState.Released, ButtonState.Released, ButtonState.Released,
            ButtonState.Released, ButtonState.Released);

    private static bool HasAnyMouseButtonPressed(MouseState ms) =>
        ms.LeftButton == ButtonState.Pressed ||
        ms.RightButton == ButtonState.Pressed ||
        ms.MiddleButton == ButtonState.Pressed ||
        ms.XButton1 == ButtonState.Pressed ||
        ms.XButton2 == ButtonState.Pressed;

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsIconic(IntPtr hWnd);

    internal static bool EvaluateWindowFocus(
        bool monoGameActive,
        bool isWindows,
        IntPtr ownHwnd,
        IntPtr foregroundHwnd,
        bool isMinimized)
    {
        if (!monoGameActive)
            return false;

        if (isWindows && ownHwnd != IntPtr.Zero)
            return foregroundHwnd == ownHwnd && !isMinimized;

        return true;
    }

    private IntPtr GetNativeWindowHandle()
    {
        if (_winHwnd == IntPtr.Zero && !_winHwndLookupAttempted && OperatingSystem.IsWindows())
        {
            _winHwndLookupAttempted = true;
            IntPtr sdlHandle = Window?.Handle ?? IntPtr.Zero;
            if (sdlHandle != IntPtr.Zero)
                _winHwnd = DarkMode.TryGetWindowsHandle(sdlHandle);
        }
        return _winHwnd;
    }

    private bool ComputeWindowActive()
    {
        if (!IsActive)
            return false;

        if (OperatingSystem.IsWindows())
        {
            IntPtr ownHwnd = GetNativeWindowHandle();
            if (ownHwnd != IntPtr.Zero)
                return EvaluateWindowFocus(IsActive, true, ownHwnd, GetForegroundWindow(), IsIconic(ownHwnd));
        }

        return true;
    }
    public CorneredEventHandler? CurrentCorneredHandler => _corneredEvents;
    
    private Microsoft.Xna.Framework.Vector2 _initialCameraPos = Microsoft.Xna.Framework.Vector2.Zero;  // Initial camera position for scrollFactor
    
    // ── MonoGame.Extended Camera (Beta Feature) ──────────────────────────────
    public static bool UseExtendedCamera = false;  // Toggle for beta feature

    // ── Sprite Texture Cache ────────────────────────────────────────────────
    // Caches converted textures to avoid reloading on song restart
    // spriteName → (frameIndex → cache)
    private Dictionary<string, Dictionary<int, SpriteTextureCacheEntry>>? _spriteTextureCache = null;
    private bool _spriteCacheOwnedByEditor = false;

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
    
    // ── Editor system ─────────────────────────────────────────────────────────
    private enum ChartEditorReturnTarget { EditorPicker, Freeplay, LevelSelector, Gameplay }
    private ChartEditorReturnTarget _chartEditorReturnTarget = ChartEditorReturnTarget.EditorPicker;
    private ChartEditorReturnTarget _levelSelectorReturnTarget = ChartEditorReturnTarget.EditorPicker;
    private Editors.EditorPicker? _editorPicker;
    private bool _inEditorPicker = false;
    private Editors.Chart.LevelSelector? _levelSelector;
    private bool _inLevelSelector = false;
    private Editors.IEditor? _activeEditor;
    private Editors.IEditor? _suspendedChartEditor;
    private bool _inEditor = false;
    private bool _playtestingFromEditor = false;
    private float _playtestStartSeconds = 0f;
    private string? _playtestTempPath = null;
    private (byte[] Pcm, int SampleRate, int Channels)? _preloadedPlaytestPcm = null;
    
    #if ANDROID
    private ModsHelpScreen? _modsHelpScreen;  // Help screen for Android mods setup
    #endif
    private FontRenderer? _fontRenderer = null;  // TrueType font renderer for crisp UI

    // ── Main Settings menu / HUD Editor launcher ─────────────────────────────
    private enum HudEditorReturnTarget { Freeplay, EditorPicker, FreeplaySettings, GameplaySettings, MultiplayerSettings, PauseMenu }
    private HudEditorReturnTarget _hudEditorReturnTarget = HudEditorReturnTarget.Freeplay;
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
    private string? _opponentPlayerId = null;
    private int _opponentColorHue = 180;
    private int _opponentScore = 0;
    private float _opponentHealth = 0.5f;
    private float _opponentTargetHealth = 0.5f;
    private int _playerMaxCombo = 0;
    private int _opponentMaxCombo = 0;
    private int _opponentMisses = 0;
    private int _opponentSicks = 0;
    private int _opponentGoods = 0;
    private int _opponentBads = 0;
    private int _opponentShits = 0;
    private string _multiplayerMatchId = "";
    private string? _multiplayerLevelName = null;
    private bool _multiplayerMatchRecorded = false;
    private bool _crashLogged = false;  // Prevent spam logging the same crash
    private bool? _opponentReadyState = null;  // Store opponent ready state received before LoadingScreen exists
    private float _stateUpdateTimer = 0f;  // Timer for sending state updates to opponent
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
    private DateTimeOffset? _editorSessionStartUtc = null;
    private DateTimeOffset? _gameplayPresenceStartUtc = null;
    private DateTimeOffset? _multiplayerMatchStartUtc = null;
    private volatile bool _pendingDiscordMpRefresh = false;
    private bool _wasWindowActive = true;
    private bool _pendingFocusInputSync = false;
    private float _focusRegainGrace = 0f;
    private bool _waitMouseReleaseAfterFocus = false;
    private IntPtr _winHwnd = IntPtr.Zero;
    private bool _winHwndLookupAttempted = false;
    
    // High-precision timing to avoid desync when window is moved
    private Stopwatch _songStopwatch = new();
    private double _pausedTime    = 0.0;  // Total accumulated pause duration
    private double _pauseStarted  = 0.0;  // When the current pause started

    // Song end
    private float _songEndTime = float.MaxValue;
    private float _musicDurationSeconds = float.MaxValue;
    private float _songEndDelay = 0f;  // countdown after song ends before transition starts

    // ── Input ────────────────────────────────────────────────────────────────
    private KeyboardState _prevKb;
    private MouseState    _prevMouse;
    private float         _volumeToastTimer = 0f;
    private const float   VOLUME_TOAST_DURATION = 1.6f;
    private float         _timeskipRepeatTimer = 0f;
    private bool          _keyBindCallbacksWired = false;
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
    private int SettingsItemCount => _settingsManager?.GetItemCount(this) ?? 8;
    private bool _pausedByTimeskip = false;  // true if PauseSong was called by timeskip scrubbing

    // ── UI Menus ──────────────────────────────────────────────────────────────
    private ArrowsMenu? _arrowsMenu = null;
    private NoteSplashMenu? _splashMenu = null;
    private CountdownMenu? _countdownMenu = null;
    private FontMenu? _fontMenu = null;

    // ── HUD options ───────────────────────────────────────────────────────────
    public static bool  ShowTimer       = true;   // song timer bar above strumline
    public static bool  ShowCombo       = true;   // combo counter popup (on/off)
    public static bool  TimeskipEnabled = true;   // T or Ctrl+Left/Right to skip in OGG songs
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
    public static float ColorGradeBrightness   = 0.00f; // Neutral 0% brightness by default
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

    // ── Falling combo digits & judgement popups (extracted to UI/JudgementOverlay.cs) ──

    // ── FNMM path ────────────────────────────────────────────────────────────
    public static string FnmmPath = "";

    // ── Error message ────────────────────────────────────────────────────────
    private string? _errorMsg;
    private bool _errorReturnToMultiplayer;
    private float _errorDismissGrace;
    private volatile bool _pendingOpponentDisconnect;
    private string _pendingDisconnectReason = "Opponent left the match";

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
        Instance = this;  // Set singleton instance for NoteRenderer access
        
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
    
    public static string SanitizeLevelDisplayName(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
            return "Unknown Level";

        string name = raw.Trim();
        string lowerExt = Path.GetExtension(name);
        if (lowerExt.Equals(".fnmm", StringComparison.OrdinalIgnoreCase) ||
            lowerExt.Equals(".zip", StringComparison.OrdinalIgnoreCase) ||
            lowerExt.Equals(".rar", StringComparison.OrdinalIgnoreCase))
        {
            name = Path.GetFileNameWithoutExtension(name);
        }

        const string tempPrefix = "funkymaker_temp_";
        if (name.StartsWith(tempPrefix, StringComparison.OrdinalIgnoreCase))
        {
            name = name[tempPrefix.Length..];
            int lastUnderscore = name.LastIndexOf('_');
            if (lastUnderscore > 0 && name[(lastUnderscore + 1)..].All(char.IsDigit))
                name = name[..lastUnderscore];
            name = name.Replace('_', ' ').Trim();
            if (name.Length > 0 && name == name.ToLowerInvariant())
            {
                name = System.Globalization.CultureInfo.CurrentCulture.TextInfo.ToTitleCase(name);
            }
        }

        return string.IsNullOrWhiteSpace(name) ? "Unknown Level" : name;
    }

    /// <summary>
    /// Starts a multiplayer PvP match (called from MultiplayerMenu).
    /// Does NOT create a new Game instance - changes current instance to multiplayer mode.
    /// </summary>
    public void StartMultiplayerMatch(string levelPath, string opponentName, int playerRole, long startTimestamp, string? roomId = null, string? levelDisplayName = null)
    {
        Console.WriteLine($"[Game1] Starting multiplayer match:");
        Console.WriteLine($"  Level: {levelPath}");
        Console.WriteLine($"  Opponent: {opponentName}");
        Console.WriteLine($"  Player Role: {playerRole}");
        Console.WriteLine($"  Start Timestamp: {startTimestamp}");
        
        _isMultiplayerMatch = true;
        _multiplayerLevelName = SanitizeLevelDisplayName(!string.IsNullOrWhiteSpace(levelDisplayName)
            ? levelDisplayName
            : Path.GetFileNameWithoutExtension(levelPath));
        var netMgr = Multiplayer.NetworkManager.Instance;
        string? rawOpponentName = !string.IsNullOrWhiteSpace(netMgr.OpponentName) ? netMgr.OpponentName : opponentName;
        _opponentPlayerId = netMgr.OpponentPlayerId;
        _opponentColorHue = netMgr.OpponentColorHue;
        var userDir = Multiplayer.UserDirectoryClient.Instance;
        if (!string.IsNullOrWhiteSpace(_opponentPlayerId) && !string.IsNullOrWhiteSpace(rawOpponentName))
            userDir.RegisterKnownUser(_opponentPlayerId, rawOpponentName, _opponentColorHue);
        _opponentName = userDir.ResolveUsername(_opponentPlayerId, rawOpponentName);
        _ = userDir.WarmFromServerAsync(netMgr.ServerUrl);
        if (!string.IsNullOrWhiteSpace(_opponentPlayerId) && Multiplayer.UserDirectoryClient.IsGenericUsername(_opponentName))
            _ = userDir.FetchUserByIdAsync(netMgr.ServerUrl, _opponentPlayerId);
        _multiplayerPlayerRole = playerRole;
        _multiplayerStartTimestamp = startTimestamp;
        _multiplayerMatchStartUtc = null;
        _countdownSignalReceived = false;  // Reset countdown signal for new match
        _opponentReadyState = null;  // Reset stored opponent ready state
        _opponentScore = 0;
        _opponentHealth = 0.5f;
        _opponentTargetHealth = 0.5f;
        _playerMaxCombo = 0;
        _opponentMaxCombo = 0;
        _opponentMisses = 0;
        _opponentSicks = 0;
        _opponentGoods = 0;
        _opponentBads = 0;
        _opponentShits = 0;
        _multiplayerMatchId = !string.IsNullOrWhiteSpace(roomId) ? roomId : Guid.NewGuid().ToString("N");
        _multiplayerMatchRecorded = false;
        DiscordRpc.SetParty(_multiplayerMatchId, 2, 2);
        
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
        ResetWindowTitle();
        _freeplay?.Refresh();
        RefreshActiveDiscordPresence(immediate: true);
    }
    
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
        _corneredEvents = null;
        _noteRenderers.Clear();
        if (_healthBar != null)
        {
            _healthBar.OpponentIcon = null;
            _healthBar.PlayerIcon = null;
            _healthBar.FlipOpponentIcon = false;
            _healthBar.FlipPlayerIcon = false;
        }
        if (_spriteCacheOwnedByEditor)
            _spriteTextureCache = null;
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

    private void FinalizeLoadedLevelForGameplay(bool showReadyScreen)
    {
        if (_level == null) return;

        if (_isMultiplayerMatch)
        {
            if (string.IsNullOrWhiteSpace(_level.LevelName) ||
                _level.LevelName.StartsWith("funkymaker_temp_", StringComparison.OrdinalIgnoreCase))
            {
                _level.LevelName = !string.IsNullOrWhiteSpace(_multiplayerLevelName)
                    ? _multiplayerLevelName!
                    : SanitizeLevelDisplayName(_level.LevelName);
            }
            else if (string.IsNullOrWhiteSpace(_multiplayerLevelName))
            {
                _multiplayerLevelName = _level.LevelName;
            }
        }

        SetupLevel();
        _songTime        = 0f;
        _songBeats       = 0f;
        _started         = false;
        _paused          = false;
        if (_healthBar != null)
            _healthBar.Health = 0.5f;
        _gameOverOverlay?.Hide();
        _countdownOverlay?.Reset();

        // Start FNF-style receptor intro animation (pop-in effect)
        // Only if HUD is visible (respects chart events that hide receptors)
        if (_events?.HudVisible != false)
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
        {
            _loadingScreen.MyPlayerRole = _multiplayerPlayerRole;
            if (_multiplayerPlayerRole == 1)
            {
                _loadingScreen.Player1Ready = false;
                _loadingScreen.Player2Ready = false;
            }
            else if (_multiplayerPlayerRole == 2)
            {
                _loadingScreen.Player1Ready = false;
                _loadingScreen.Player2Ready = false;
            }

            Console.WriteLine("[Game1] Level loaded! Sending Ready to server...");
            Multiplayer.NetworkManager.Instance.SendReady(true);

            if (_loadingScreen != null)
            {
                if (_multiplayerPlayerRole == 1)
                {
                    _loadingScreen.Player1Ready = true;
                    Console.WriteLine("[Game1] Set Player1Ready=true (I am Player 1)");
                }
                else if (_multiplayerPlayerRole == 2)
                {
                    _loadingScreen.Player2Ready = true;
                    Console.WriteLine("[Game1] Set Player2Ready=true (I am Player 2)");
                }

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

    private void HandleSetupLevelFailure(Exception ex)
    {
        bool wasMp = _isMultiplayerMatch;
        if (wasMp)
            Multiplayer.NetworkManager.Instance.LeaveMatch(notifyOpponent: true);

        _errorReturnToMultiplayer = wasMp;
        _errorDismissGrace = 0.25f;
        _errorMsg        = ex.Message;
        _loadingComplete = true;
        _loadState       = LoadState.Done;
        CloseAllInMatchOverlays();
        _loadingScreen   = null;  // clear loading screen, error shown via DrawError
        _level           = null;  // clear level so DrawError path is taken
        ResetMultiplayerMatchState();
        Console.Error.WriteLine($"[FunkyMakerPlayer] SetupLevel failed: {ex}");
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
        
        // Check for updates asynchronously without blocking launch (Psych Engine-style in-menu prompt)
#if !DEBUG
#if ANDROID
        // Android: check for updates and open browser to GitHub releases
        Task.Run(async () =>
        {
            var updateInfo = await Updater.CheckForUpdatesAsync();
            if (updateInfo != null)
            {
                var activity = Microsoft.Xna.Framework.Game.Activity as Platforms.Android.MainActivity;
                if (activity != null)
                {
                    activity.RunOnUiThread(() =>
                    {
                        var builder = new global::Android.App.AlertDialog.Builder(activity);
                        builder.SetTitle("Update Available");
                        builder.SetMessage($"Version {updateInfo.Value.tag} is available!\n\nCurrent: {Updater.GetLocalVersionString()}\nLatest: {updateInfo.Value.tag}\n\nWould you like to download it?");
                        builder.SetPositiveButton("Download", (sender, args) =>
                        {
                            var intent = new global::Android.Content.Intent(global::Android.Content.Intent.ActionView);
                            intent.SetData(global::Android.Net.Uri.Parse("https://github.com/Thisisfenix/thisisfenix.github.io/releases/latest"));
                            activity.StartActivity(intent);
                        });
                        builder.SetNegativeButton("Later", (sender, args) => { });
                        builder.Show();
                    });
                }
            }
        });
#else
        // Windows/Linux: non-blocking background release check, surfaced inside main menu
        Task.Run(async () =>
        {
            var updateInfo = await Updater.CheckForUpdatesAsync();
            if (updateInfo != null && !string.IsNullOrEmpty(updateInfo.Value.downloadUrl))
            {
                _freeplay?.SetAvailableUpdate(
                    updateInfo.Value.tag,
                    updateInfo.Value.version.ToString(3),
                    updateInfo.Value.downloadUrl,
                    updateInfo.Value.changelog);
            }
        });
#endif
#endif
    }

    // SDL2 P/Invoke — MonoGame DesktopGL ships SDL2.dll
    [DllImport("SDL2", CallingConvention = CallingConvention.Cdecl)]
    private static extern byte SDL_EventState(uint type, int state);

    private void OnFileDrop(object? sender, FileDropEventArgs e)
    {
        var importedPaths = new List<string>();

        foreach (var file in e.Files)
        {
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

    /// <summary>
    /// Begins async loading of a .FNMM file.
    /// Phase 1 (background thread): parse file, decompress, read JSONs/audio bytes.
    /// Phase 2 (main thread, next Update): create GPU textures, init renderers.
    /// The loading screen is shown immediately and spins while loading.
    /// </summary>
    private string ResetGameplayStateForLevelLoad(string path)
    {
        // Stop current song immediately
        _musicInst?.Stop();
        _started      = false;
        _paused       = false;
        _gameplayPresenceStartUtc = null;
        _errorMsg     = null;
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
        string levelName = ResetGameplayStateForLevelLoad(path);
        _loadState = LoadState.Loading;
        _preloadedPlaytestPcm = null;
        _spriteCacheOwnedByEditor = false;
        if (!preserveSpriteCache)
        {
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
                
                _loadingComplete = true;
                _loadState       = LoadState.Finalizing;
            }
            catch (Exception ex)
            {
                _errorMsg        = ex.Message == "PSYCH_ENGINE_MOD"
                    ? "Psych Engine mods are not supported. Export the level as a .FNMM (ZIP) and try again."
                    : ex.Message;
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
        _winHwnd = DarkMode.TryGetWindowsHandle(Window.Handle);
        _winHwndLookupAttempted = _winHwnd != IntPtr.Zero;

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
        UI.UiSfx.Initialize(assetsRoot);
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
        _mainSettingsMenu?.SetFontRenderer(_fontRenderer);
        
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
#if ANDROID
        _freeplay.IsPointOverOverlayControl = p => _touchControls?.IsPointOnMenuControl(p) ?? false;
#endif
        #endif

        // Init multiplayer menu (for accessing multiplayer lobby)
        _multiplayerMenu = new Multiplayer.MultiplayerMenu();
        _multiplayerMenu.SetGame(this);  // Pass reference to this Game1 instance
        _multiplayerMenu.SetFontRenderer(_fontRenderer);
        _multiplayerMenu.SetIcons(LoadMultiplayerIcons(assetsRoot));
        
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
        {
            _noteStyle = NoteStyle.LoadByName(NoteStyleName, assetsRootForStyle, GraphicsDevice);
            
            // Set the style on all note renderers
            foreach (var r in _noteRenderers)
                r.SetStyle(_noteStyle);
        }
        
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
        
        // Refresh Discord presence on the main thread when database user lookups resolve usernames
        Multiplayer.UserDirectoryClient.Instance.OnUsersUpdated += () =>
        {
            _pendingDiscordMpRefresh = true;
        };

        // Receive opponent's profile updates (real username, ID, color)
        Multiplayer.NetworkManager.Instance.OnOpponentProfileReceived += (packet) =>
        {
            if (!string.IsNullOrWhiteSpace(packet.PlayerId))
                _opponentPlayerId = packet.PlayerId;
            _opponentColorHue = packet.ColorHue;

            var userDir = Multiplayer.UserDirectoryClient.Instance;
            if (!string.IsNullOrWhiteSpace(packet.PlayerId) && !string.IsNullOrWhiteSpace(packet.Username))
                userDir.RegisterKnownUser(packet.PlayerId, packet.Username, packet.ColorHue);

            if (!Multiplayer.UserDirectoryClient.IsGenericUsername(packet.Username))
                _opponentName = packet.Username.Trim();
            else
            {
                string resolved = userDir.ResolveUsername(_opponentPlayerId, _opponentName);
                if (!Multiplayer.UserDirectoryClient.IsGenericUsername(resolved))
                    _opponentName = resolved;
                else if (!string.IsNullOrWhiteSpace(_opponentPlayerId))
                    _ = userDir.FetchUserByIdAsync(Multiplayer.NetworkManager.Instance.ServerUrl, _opponentPlayerId);
            }

            _pendingDiscordMpRefresh = true;
        };

        // Receive opponent's state updates (score, health, combo, ratings)
        Multiplayer.NetworkManager.Instance.OnOpponentState += (packet) =>
        {
            if (!_isMultiplayerMatch) return; // Ignore if not in multiplayer
            
            _opponentScore = packet.Score;
            _opponentMaxCombo = Math.Max(_opponentMaxCombo, packet.Combo);
            _opponentSicks = packet.SickCount;
            _opponentGoods = packet.GoodCount;
            _opponentBads = packet.BadCount;
            _opponentShits = packet.ShitCount;
            _opponentMisses = packet.MissCount;
            _opponentTargetHealth = Math.Clamp(packet.Health, 0f, 1f);
            if (!Multiplayer.MultiplayerSettings.Instance.EnableInterpolation)
                _opponentHealth = _opponentTargetHealth;
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
        
        // Handle opponent disconnection (SignalR fires off the main MonoGame thread)
        Multiplayer.NetworkManager.Instance.OnOpponentDisconnected += (reason) =>
        {
            _pendingDisconnectReason = string.IsNullOrWhiteSpace(reason) ? "Opponent left the match" : reason;
            _pendingOpponentDisconnect = true;
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

    /// <summary>
    /// Called when the window/app loses focus (alt-tab, minimized, another app clicked, etc.)
    /// Auto-pauses solo gameplay and Chart Editor preview playback across PC and Android so the engine never runs away unfocused.
    /// </summary>
    protected override void OnDeactivated(object sender, EventArgs args)
    {
        base.OnDeactivated(sender, args);
        _wasWindowActive = false;

        if (_activeEditor is Editors.Chart.ChartEditor ce)
        {
            ce.PausePlaybackFromFocusLoss();
            ce.SyncInputState();
        }

        // Auto-pause if solo gameplay is active (not in menus or multiplayer)
        // Disabled in multiplayer - can't pause during PvP matches
        if (!_isMultiplayerMatch && _level != null && _started && !_paused && !_inPauseMenu && !(_gameOverOverlay?.IsActive ?? false))
        {
            Console.WriteLine("[Focus] Window deactivated, auto-pausing solo gameplay");
            _inPauseMenu = true;
            PauseSong();
        }
        else if (_isMultiplayerMatch)
        {
            Console.WriteLine("[Focus] Window deactivated during multiplayer - pause disabled");
        }
    }

    /// <summary>
    /// Called when the window/app regains focus.
    /// Defers input polling cleanly so the focus click / Alt+Tab chord never triggers menus or notes.
    /// </summary>
    protected override void OnActivated(object sender, EventArgs args)
    {
        base.OnActivated(sender, args);
        _pendingFocusInputSync = true;
        _focusRegainGrace = 0.08f;
    }

    
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

        // ── Pre-load all sprite textures via shared scene renderer ────────────
        int maxTexSize = GetGpuMaxTextureSize();
        Console.WriteLine($"[GPU] Max texture size: {maxTexSize}px (device limit)");
        Console.WriteLine($"[Scene] Loading {_level.Sprites.Count} sprites from level...");

        _spriteTextureCache ??= new Dictionary<string, Dictionary<int, SpriteTextureCacheEntry>>();

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
            () => RenderLoadingScreen());

        if (_loadingScreen != null)
        {
            _loadingScreen.LoadingProgress = 0.80f;
            _loadingScreen.CurrentStep = "Textures loaded!";
            RenderLoadingScreen();
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
        bool flipIconsForEnemyRole = _isMultiplayerMatch && _multiplayerPlayerRole == 2;
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
            else if (i >= 2 && HideExtraStrumlines)
            {
                renderer.Alpha = 0f;
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
            };

            // Wire judgement display - FNF style stacked popups (max 3)
            _noteRenderers[myStrumlineIndex].OnJudgement = j => 
            {
                // Add judgement popup using overlay
                var hud = HudSettings.Instance;
                if (_judgementOverlay != null && ShowCombo && hud.MasterHudVisible && hud.JudgementVisible && _events?.HudVisible != false)
                {
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
                
                // Show combo using overlay
                var hud = HudSettings.Instance;
                if (ShowCombo && hud.MasterHudVisible && hud.ComboVisible && _events?.HudVisible != false)
                {
                    _judgementOverlay.ShowCombo(newCombo, new Point(hud.ComboPosition.X, hud.ComboPosition.Y));
                }
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
        
        // Initialize Cornered-specific event handler using UUID detection (more reliable than name)
        // UUID: 01kd72pw682p6mrj4wg388zgz5 from Cornered/meta.json
        // Chart was already replaced in SetupLevel before renderers were created
        if ((_level.Meta?.UUID ?? "").Equals("01kd72pw682p6mrj4wg388zgz5", StringComparison.OrdinalIgnoreCase))
        {
            _corneredEvents = new CorneredEventHandler(_events, _level, "default");
            Console.WriteLine($"[Cornered] Exclusive event handler activated");
        }
        else
        {
            _corneredEvents = null;
        }
        
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

            bool isRole2Mp = _isMultiplayerMatch && _multiplayerPlayerRole == 2;
            bool isPlayerSlot = isRole2Mp ? (target == 0) : (target != 0);
            ApplyHealthBarIcon(isPlayerSlot, newAnim, isRole2Mp);
            Color slotColor = isPlayerSlot ? _healthBar.PlayerBarColor : _healthBar.OpponentBarColor;
            Console.WriteLine($"[BarIcon] {(isPlayerSlot ? "Player" : "Opponent")} icon changed to '{spriteName}' (flip={isRole2Mp}) - Color: {slotColor}");
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
        
        if (_preloadedPlaytestPcm is { Pcm: var cachedPcm, SampleRate: var cachedSr, Channels: var cachedCh })
        {
            _musicInst?.Dispose();
            _music?.Dispose();
            _musicSampleRate = cachedSr;
            _musicChannels   = cachedCh;
            _musicPcm        = cachedPcm;
            _music           = OggLoader.CreateFromPcm(cachedPcm, cachedSr, cachedCh);
            _musicInst       = _music.CreateInstance();
            _musicInst.Volume = MusicVolume;
            _musicInst.IsLooped = false;
            _musicDurationSeconds = _music.Duration.TotalSeconds > 0.1
                ? (float)_music.Duration.TotalSeconds
                : float.MaxValue;
        }
        else if (_level.MusicData != null)
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

    // ── Update ───────────────────────────────────────────────────────────────
    protected override void Update(GameTime gameTime)
    {
        var uiSnapshot = CaptureUiSfxSnapshot();
        try
        {
            UpdateInternal(gameTime);
        }
        catch (Exception ex)
        {
            HandleGameplayError(ex, "Update");
        }
        finally
        {
            QueueUiSfxForScreenChanges(uiSnapshot);
            UI.UiSfx.Flush();
        }
    }

    private void UpdateInternal(GameTime gameTime)
    {
        var rawKb  = Keyboard.GetState();
        bool windowActive = ComputeWindowActive();

#if ANDROID
        // Update touch controls before merging virtual keys with physical keyboard state
        _touchControls?.Update();
        
        // Auto-switch touch control mode based on game state
        if (_touchControls != null)
        {
            bool inGameplay = _level != null && _started && !_inPauseMenu && 
                             !_inFreeplay && !(_settingsManager?.IsActive ?? false) &&
                             !(_arrowsMenu?.IsActive ?? false) && !(_splashMenu?.IsActive ?? false) &&
                             !(_countdownMenu?.IsActive ?? false) && !(_fontMenu?.IsActive ?? false) &&
                             !_inMultiplayer && !_inMainSettings && !_inHudEditor &&
                             !_inEditorPicker && !_inLevelSelector && !_inEditor;
            
            var targetMode = inGameplay ? TouchControls.ControlMode.Gameplay : TouchControls.ControlMode.Menu;
            if (_touchControls.GetMode() != targetMode)
            {
                _touchControls.SetMode(targetMode);
            }
            
            // Allow pause button even in multiplayer (Minecraft-style non-blocking pause/settings)
            _touchControls.SetHidePauseButton(false);
        }
        
        // Merge touch input into keyboard state for seamless integration
        rawKb = MergeTouchWithKeyboard(rawKb);
#endif
        
        float dt = Math.Min((float)gameTime.ElapsedGameTime.TotalSeconds, 0.1f);  // clamp to 100ms max to prevent camera jumps on first frame
        _lastDt  = gameTime.ElapsedGameTime.TotalSeconds;

        var rawMs = Mouse.GetState();
        bool wasWaitingMouseRelease = _waitMouseReleaseAfterFocus;
        bool wasInGrace = _focusRegainGrace > 0f || wasWaitingMouseRelease;

        if (_wasWindowActive && !windowActive)
        {
            _waitMouseReleaseAfterFocus = false;
            if (_activeEditor is Editors.Chart.ChartEditor inactiveCe)
            {
                inactiveCe.PausePlaybackFromFocusLoss();
                inactiveCe.SyncInputState();
            }

            if (!_isMultiplayerMatch && _level != null && _started && !_paused && !_inPauseMenu && !(_gameOverOverlay?.IsActive ?? false))
            {
                _inPauseMenu = true;
                PauseSong();
            }
        }
        else if ((!_wasWindowActive && windowActive) || _pendingFocusInputSync)
        {
            _pendingFocusInputSync = false;
            if (HasAnyMouseButtonPressed(rawMs))
                _waitMouseReleaseAfterFocus = true;
            SyncFocusInputsAfterReactivation(rawKb, rawMs);
            _focusRegainGrace = Math.Max(_focusRegainGrace, 0.08f);
            wasInGrace = true;
        }
        _wasWindowActive = windowActive;

        if (windowActive && (_focusRegainGrace > 0f || _waitMouseReleaseAfterFocus))
        {
            if (_focusRegainGrace > 0f)
                _focusRegainGrace = Math.Max(0f, _focusRegainGrace - dt);

            _waitMouseReleaseAfterFocus = HasAnyMouseButtonPressed(rawMs);

            if (_focusRegainGrace <= 0f && !_waitMouseReleaseAfterFocus)
                SyncFocusInputsAfterReactivation(rawKb, rawMs);
        }

        bool suppressUserInput = !windowActive || _focusRegainGrace > 0f || _waitMouseReleaseAfterFocus;
        var kb = suppressUserInput ? default : rawKb;

        var prevMs = _prevMouse;
        _prevMouse = rawMs;
        var ms = suppressUserInput
            ? NeutralMouseState(rawMs, offscreen: true)
            : rawMs;
        if (suppressUserInput)
            prevMs = ms;
        else if (wasInGrace && _focusRegainGrace <= 0f && !_waitMouseReleaseAfterFocus)
            prevMs = ms;

        // Keep SignalR ping, time-sync, and auto-reconnect active across all states (menus, pause, gameplay)
        Multiplayer.NetworkManager.Instance.Update(gameTime);

        HandlePendingOpponentDisconnect();

        if (_pendingDiscordMpRefresh)
        {
            _pendingDiscordMpRefresh = false;
            RefreshActiveDiscordPresence(immediate: true);
        }
        else
        {
            RefreshActiveDiscordPresence(immediate: false);
        }
        DiscordRpc.Update();

        // Always handle active error / disconnect overlay before menus, pause, or gameplay so the player is never trapped
        if (_errorMsg != null)
        {
            if (_errorDismissGrace > 0f)
                _errorDismissGrace = Math.Max(0f, _errorDismissGrace - dt);

            bool escPressed     = kb.IsKeyDown(Keys.Escape) && !_prevKb.IsKeyDown(Keys.Escape);
            bool pErrPressed    = kb.IsKeyDown(Keys.P)      && !_prevKb.IsKeyDown(Keys.P);
            bool confirmPressed = (kb.IsKeyDown(Keys.Enter) && !_prevKb.IsKeyDown(Keys.Enter)) ||
                                  (kb.IsKeyDown(Keys.Space) && !_prevKb.IsKeyDown(Keys.Space));
            bool clicked        = !suppressUserInput && ms.LeftButton == ButtonState.Pressed && prevMs.LeftButton == ButtonState.Released;

            if (escPressed || pErrPressed || (_errorDismissGrace <= 0f && (confirmPressed || clicked)))
                DismissErrorToMenu();

            _prevKb = kb;
            base.Update(gameTime);
            return;
        }

        if (_volumeToastTimer > 0f)
            _volumeToastTimer = Math.Max(0f, _volumeToastTimer - dt);

        // Global FNF-style volume hotkeys (- / +) everywhere except text-entry or keybinding capture
        bool allowVolumeHotkeys = !suppressUserInput && !_keyBindOverlay.IsActive && !_inEditor && !_inLevelSelector && !_inMultiplayer;
        if (allowVolumeHotkeys)
        {
            bool volUp = (kb.IsKeyDown(Keys.OemPlus) && !_prevKb.IsKeyDown(Keys.OemPlus)) ||
                         (kb.IsKeyDown(Keys.Add)     && !_prevKb.IsKeyDown(Keys.Add));
            bool volDown = (kb.IsKeyDown(Keys.OemMinus) && !_prevKb.IsKeyDown(Keys.OemMinus)) ||
                           (kb.IsKeyDown(Keys.Subtract) && !_prevKb.IsKeyDown(Keys.Subtract));
            if (volUp || volDown)
            {
                float step = volUp ? 0.05f : -0.05f;
                float oldVolume = MusicVolume;
                MusicVolume = MathF.Round(Math.Clamp(MusicVolume + step, 0f, 1f), 2);
                UI.UiSfx.Play(MusicVolume == oldVolume ? UI.UiSound.SliderEnd : volUp ? UI.UiSound.SliderUp : UI.UiSound.SliderDown);
                if (_musicInst != null) _musicInst.Volume = MusicVolume;
                _volumeToastTimer = VOLUME_TOAST_DURATION;
                Settings.Save(_graphics.SynchronizeWithVerticalRetrace);
            }
        }

        // ESC opens pause menu (or settings if no level loaded)
        // Skip if in arrows or splash menu (they handle ESC themselves)
        // P key also pauses (for touch controls)
        bool escPressedForPause = kb.IsKeyDown(Keys.Escape) && !_prevKb.IsKeyDown(Keys.Escape);
        bool pPressed = kb.IsKeyDown(Keys.P) && !_prevKb.IsKeyDown(Keys.P);

        if (_playtestingFromEditor && _suspendedChartEditor != null && !_inPauseMenu &&
            !(_settingsManager?.IsActive ?? false) && !_keyBindOverlay.IsActive && escPressedForPause)
        {
            ReturnFromPlaytest();
            _prevKb = kb;
            base.Update(gameTime);
            return;
        }
        
        if ((escPressedForPause || pPressed) && 
            !(_arrowsMenu?.IsActive ?? false) && !(_splashMenu?.IsActive ?? false) &&
            !(_countdownMenu?.IsActive ?? false) && !(_fontMenu?.IsActive ?? false) &&
            !_inMainSettings && !_inHudEditor && !_inMultiplayer &&
            !_inEditorPicker && !_inLevelSelector && !_inEditor && !_keyBindOverlay.IsActive)
        {
            // Allow pause in both singleplayer and multiplayer (Minecraft-style live pause in MP)
            if (_settingsManager?.IsActive == true)
            {
                // If focused on a numeric/adjustable setting, ESC releases focus first
                if (_settingsManager.IsFocused)
                {
                    _settingsManager.Unfocus();
                    AdvanceLiveMultiplayerIfActive(gameTime, dt);
                    _prevKb = kb;
                    base.Update(gameTime);
                    return;
                }

                // Exit settings back to pause menu (or menu if no level loaded)
                _settingsManager.IsActive = false;
                ExitSettingsMenu();
                if (_inFreeplay || _inMultiplayer)
                {
                    _prevKb = kb;
                    base.Update(gameTime);
                    return;
                }
            }
            else if (_level == null)
            {
                // No level loaded - ESC = settings (when not in FreeplayMenu/MultiplayerMenu)
                if (!_inFreeplay && !_inMultiplayer && _settingsManager != null)
                    _settingsManager.IsActive = !_settingsManager.IsActive;
            }
            else
            {
                // Toggle pause menu (singleplayer pauses audio; multiplayer keeps gameplay & audio live!)
                _inPauseMenu = !_inPauseMenu;
                if (_inPauseMenu && _pauseMenu != null)
                {
                    _pauseMenu.IsMultiplayer = _isMultiplayerMatch;
                    _pauseMenu.Selection = 0;
                }
                if (!_isMultiplayerMatch)
                {
                    if (_inPauseMenu && _started && !_paused)
                        PauseSong();
                    else if (!_inPauseMenu && _paused)
                        ResumeSong();
                }
            }
        }

        // ── Pause menu ───────────────────────────────────────────────────────
        if (_inPauseMenu && _level != null && _pauseMenu != null)
        {
            _pauseMenu.IsMultiplayer = _isMultiplayerMatch;
            var action = _pauseMenu.HandleInput(kb, _prevKb);

            switch (action)
            {
                case PauseMenu.PauseAction.Resume:
                    _inPauseMenu = false;
                    _pauseMenu.Selection = 0;
                    if (!_isMultiplayerMatch)
                        ResumeSong();
                    break;

                case PauseMenu.PauseAction.RestartSong:
                    if (!_isMultiplayerMatch)
                    {
                        RestartSong();
                        _inPauseMenu = false;
                    }
                    break;

                case PauseMenu.PauseAction.OpenHudEditor:
                    if (!_isMultiplayerMatch)
                    {
                        _inPauseMenu = false;
                        _hudEditorReturnTarget = HudEditorReturnTarget.PauseMenu;
                        HudSettings.ForceReload();
                        _hudEditor = new HudEditor(_pixel!, _font!, _rtWidth, _rtHeight, GraphicsDevice);
                        _inHudEditor = true;
                    }
                    break;

                case PauseMenu.PauseAction.OpenSettings:
                    _inPauseMenu = false;
                    if (_settingsManager != null) _settingsManager.IsActive = true;
                    break;

                case PauseMenu.PauseAction.ExitToMenu:
                    if (_playtestingFromEditor)
                    {
                        ReturnFromPlaytest();
                        break;
                    }
                    _musicInst?.Stop();
                    _started = false;
                    _paused = false;
                    _gameplayPresenceStartUtc = null;
                    _inPauseMenu = false;
                    _pauseMenu.Selection = 0;
                    _level = null;
                    _songTransition = null;
                    _songEndDelay = 0f;

                    if (_isMultiplayerMatch)
                    {
                        Multiplayer.NetworkManager.Instance.LeaveMatch(notifyOpponent: true);
                        ResetMultiplayerMatchState();
                        _inMultiplayer = true;
                        _multiplayerMenu?.ResetAfterMatch();
                        _multiplayerMenu?.ResetInputCooldown();
                    }
                    else
                    {
                        _inFreeplay = true;
                        _freeplay?.Refresh();
                        _freeplay?.ResetInputCooldown();
                    }
                    ResetWindowTitle();
                    RefreshActiveDiscordPresence(immediate: true);
                    break;
            }

            if (_level != null && _inPauseMenu)
                AdvanceLiveMultiplayerIfActive(gameTime, dt);

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
            
            AdvanceLiveMultiplayerIfActive(gameTime, dt);
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
            
            AdvanceLiveMultiplayerIfActive(gameTime, dt);
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
            
            AdvanceLiveMultiplayerIfActive(gameTime, dt);
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
                        _freeplay?.SetFontRenderer(_fontRenderer);
                        _mainSettingsMenu?.SetFontRenderer(_fontRenderer);
                        _multiplayerMenu?.SetFontRenderer(_fontRenderer);
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
            
            AdvanceLiveMultiplayerIfActive(gameTime, dt);
            _prevKb = kb;
            base.Update(gameTime);
            return;
        }

        // ── Key binding overlay (when opened from Settings in Main Menu or Pause) ──
        if (_keyBindOverlay.IsActive)
        {
            _keyBindOverlay.Update(kb, _prevKb);
            AdvanceLiveMultiplayerIfActive(gameTime, dt);
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

            // Tab navigation with Q/E (also used by Android virtual buttons)
            if (kb.IsKeyDown(Keys.Q) && !_prevKb.IsKeyDown(Keys.Q))
                _settingsManager.PrevTab();

            if (kb.IsKeyDown(Keys.E) && !_prevKb.IsKeyDown(Keys.E))
                _settingsManager.NextTab();

            // Up/Down navigation (releases focus cleanly when switching option)
            int itemCount = _settingsManager.GetItemCount(this);
            if (kb.IsKeyDown(Keys.Up) && !_prevKb.IsKeyDown(Keys.Up))
            {
                _settingsManager.Unfocus();
                _settingsManager.NotifySelectionMoved();
                _settingsManager.Selection = (_settingsManager.Selection - 1 + itemCount) % itemCount;
            }

            if (kb.IsKeyDown(Keys.Down) && !_prevKb.IsKeyDown(Keys.Down))
            {
                _settingsManager.Unfocus();
                _settingsManager.NotifySelectionMoved();
                _settingsManager.Selection = (_settingsManager.Selection + 1) % itemCount;
            }

            // Enter or Space: executes toggle/action OR focuses/unfocuses adjustable option
            if ((kb.IsKeyDown(Keys.Enter) && !_prevKb.IsKeyDown(Keys.Enter)) ||
                (kb.IsKeyDown(Keys.Space) && !_prevKb.IsKeyDown(Keys.Space)))
            {
                var result = _settingsManager.HandleSelectAction(this);
                ApplySettingsSelectResult(result);
            }

            // Left/Right: when focused adjusts value; when unfocused switches tabs
            bool leftPressed = kb.IsKeyDown(Keys.Left) && !_prevKb.IsKeyDown(Keys.Left);
            bool rightPressed = kb.IsKeyDown(Keys.Right) && !_prevKb.IsKeyDown(Keys.Right);
            if (leftPressed || rightPressed)
            {
                if (_settingsManager.IsFocused)
                {
                    var adj = _settingsManager.HandleAdjustment(this, leftPressed);
                    ApplySettingsAdjustmentResult(adj);
                }
                else
                {
                    if (leftPressed) _settingsManager.PrevTab();
                    else _settingsManager.NextTab();
                }
            }

            // Full mouse interaction (hover, click tabs, toggle rows, click < / > arrows, wheel scroll)
            var mouseSelect = _settingsManager.UpdateMouse(this, ms, prevMs, out var mouseAdj, out bool exitClicked);
            if (exitClicked)
            {
                _settingsManager.IsActive = false;
                ExitSettingsMenu();
            }
            if (mouseSelect != null)
                ApplySettingsSelectResult(mouseSelect);
            if (mouseAdj != null)
                ApplySettingsAdjustmentResult(mouseAdj);

            AdvanceLiveMultiplayerIfActive(gameTime, dt);
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
            
            var winBounds = Window.ClientBounds;
            _freeplay.Update(dt, kb, winBounds.Width, winBounds.Height, winBounds.Width, winBounds.Height);
            
            // Check if user pressed ESC in main menu root to open Settings
            if (_freeplay.SettingsRequested)
            {
                _freeplay.ConsumeSettingsRequest();
                if (_settingsManager != null)
                {
                    _settingsManager.IsActive = true;
                }
            }

            // Check if user pressed "7" to open Editor Picker
            if (_freeplay.EditorPickerRequested)
            {
                _freeplay.ConsumeEditorPickerRequest();
                EnterEditorPicker();
            }

            // Check if user requested creating a brand-new chart from Freeplay panel
            if (_freeplay.NewChartRequested)
            {
                _freeplay.ConsumeNewChartRequest();
                _inFreeplay = false;
                _inLevelSelector = true;
                _levelSelectorReturnTarget = ChartEditorReturnTarget.Freeplay;
                _levelSelector = new Editors.Chart.LevelSelector(_pixel!, _font!, _fontRenderer, startCreateModal: true);
            }
            
            // Check if user requested editing a chart from Freeplay panel ("7" or EDIT button)
            if (_freeplay.ChartEditorRequested)
            {
                _freeplay.ConsumeChartEditorRequest();
                OpenChartEditor(_freeplay.LevelPath, ChartEditorReturnTarget.Freeplay);
            }
            
            if (_freeplay.LevelChosen)
            {
                _inFreeplay = false;
                StartLoadingLevel(_freeplay.LevelPath);
                _freeplay.LevelChosen = false;
            }
            // Main-menu actions (HUD Editor is reached via EDITORS / Settings, not MenuAction)
            int menuAction = _freeplay.ConsumeMenuAction();
            // Check if user selected Multiplayer
            if (menuAction == 3)
            {
                _inFreeplay = false;
                _inMultiplayer = true;
                _multiplayerMenu?.ResetInputCooldown();
                RefreshActiveDiscordPresence(immediate: true);
            }
            #if ANDROID
            // Check if user selected IMPORT FILE (Android only)
            if (menuAction == 4)
            {
                OpenAndroidFilePicker();
            }
            // Check if user selected OPEN MODS (Android only)
            if (menuAction == 5)
            {
                OpenAndroidModsFolder();
            }
            // Check if user selected GRANT FOLDER ACCESS (Android only)
            if (menuAction == 6)
            {
                // Show help screen first
                _modsHelpScreen?.Show();

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
        
        // ── Editor Picker ─────────────────────────────────────────────────────
        if (_inEditorPicker && _editorPicker != null)
        {
            var winBounds = Window.ClientBounds;
            _editorPicker.Update(dt, kb, Math.Max(1, winBounds.Width), Math.Max(1, winBounds.Height));
            
            if (_editorPicker.BackPressed)
            {
                ExitEditorPicker();
            }
            else if (_editorPicker.EditorChosen)
            {
                OpenEditor(_editorPicker.ChosenEditor);
            }
            
            _prevKb = kb;
            base.Update(gameTime);
            return;
        }
        
        // ── Level Selector (for Chart Editor) ────────────────────────────────
        if (_inLevelSelector && _levelSelector != null)
        {
            var winBounds = Window.ClientBounds;
            _levelSelector.Update(dt, kb, Math.Max(1, winBounds.Width), Math.Max(1, winBounds.Height));
            
            if (_levelSelector.BackPressed)
            {
                ExitLevelSelector();
            }
            else if (_levelSelector.LevelSelected && _levelSelector.SelectedLevelPath != null)
            {
                OpenChartEditor(_levelSelector.SelectedLevelPath, _levelSelectorReturnTarget);
            }
            
            _prevKb = kb;
            base.Update(gameTime);
            return;
        }
        
        // ── Active Editor (Chart Editor, etc.) ───────────────────────────────
        if (_inEditor && _activeEditor != null)
        {
            var winBounds = Window.ClientBounds;
            if (_activeEditor is Editors.Chart.ChartEditor ce)
            {
                ce.Update(dt, kb, Math.Max(1, winBounds.Width), Math.Max(1, winBounds.Height));
                if (ce.PlaytestRequested)
                {
                    ce.ConsumePlaytestRequest();
                    BeginEditorPlaytest(ce);
                    _prevKb = kb;
                    base.Update(gameTime);
                    return;
                }
            }
            else
            {
                _activeEditor.Update(dt, kb);
            }
            
            if (_activeEditor.BackPressed)
            {
                ExitEditor();
            }
            
            _prevKb = kb;
            base.Update(gameTime);
            return;
        }
        
        // ── Main Settings menu / HUD Editor launcher ─────────────────────────
        if (_inMainSettings && _mainSettingsMenu != null)
        {
            var winBounds = Window.ClientBounds;
            _mainSettingsMenu.Update(dt, kb, winBounds.Width, winBounds.Height);
            
            if (_mainSettingsMenu.BackPressed)
            {
                _inMainSettings = false;
                switch (_hudEditorReturnTarget)
                {
                    case HudEditorReturnTarget.EditorPicker:
                        EnterEditorPicker();
                        break;
                    case HudEditorReturnTarget.FreeplaySettings:
                        _inFreeplay = true;
                        _freeplay?.ResetInputCooldown();
                        if (_settingsManager != null) _settingsManager.IsActive = true;
                        break;
                    case HudEditorReturnTarget.MultiplayerSettings:
                        _inMultiplayer = true;
                        _multiplayerMenu?.ResetInputCooldown();
                        if (_settingsManager != null) _settingsManager.IsActive = true;
                        break;
                    case HudEditorReturnTarget.GameplaySettings:
                        if (_settingsManager != null) _settingsManager.IsActive = true;
                        break;
                    case HudEditorReturnTarget.PauseMenu:
                        _inPauseMenu = true;
                        break;
                    default:
                        _musicInst?.Stop();
                        _inFreeplay = true;
                        ResetWindowTitle();
                        _freeplay?.ResetInputCooldown();
                        break;
                }
                RefreshActiveDiscordPresence(immediate: true);
            }
            
            if (_mainSettingsMenu.OpenHudEditor)
            {
                // Force reload settings from disk to ensure we have the latest values
                HudSettings.ForceReload();
                
                _hudEditor = new HudEditor(_pixel!, _font!, _rtWidth, _rtHeight, GraphicsDevice);
                _inHudEditor = true;
                _inMainSettings = false;
                RefreshActiveDiscordPresence(immediate: true);
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
                _hudEditor = null; // Clean up
                
                if (_hudEditorReturnTarget == HudEditorReturnTarget.PauseMenu)
                {
                    _inPauseMenu = true;
                    _prevKb = kb;
                }
                else
                {
                    _inMainSettings = true;
                    _mainSettingsMenu?.ResetInputCooldown(); // Prevent immediate ENTER detection when returning
                }
                RefreshActiveDiscordPresence(immediate: true);
            }
            
            // Don't update _prevKb here - let HudEditor handle it
            base.Update(gameTime);
            return;
        }

        // ── Multiplayer menu ─────────────────────────────────────────────────
        if (_inMultiplayer && _multiplayerMenu != null)
        {
            _multiplayerMenu.Update(gameTime);

            if (_multiplayerMenu.GameplaySettingsRequested)
            {
                _multiplayerMenu.ConsumeGameplaySettingsRequest();
                if (_settingsManager != null)
                    _settingsManager.IsActive = true;
            }
            
            // Check if returning to freeplay
            if (_multiplayerMenu.ShouldExitToFreeplay())
            {
                _inMultiplayer = false;
                _musicInst?.Stop();  // Stop any background music
                DiscordRpc.ClearParty();
                _inFreeplay = true;
                ResetWindowTitle();  // Reset title when returning to menu
                _freeplay?.ResetInputCooldown();
                _multiplayerMenu.ResetExitFlag();
                RefreshActiveDiscordPresence(immediate: true);
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

        // "7" opens Chart Editor with current level (or returns during playtest)
        if (kb.IsKeyDown(Keys.D7) && !_prevKb.IsKeyDown(Keys.D7))
        {
            if (_playtestingFromEditor)
            {
                ReturnFromPlaytest();
                _prevKb = kb;
                base.Update(gameTime);
                return;
            }
            if (!_isMultiplayerMatch && _level != null && !string.IsNullOrEmpty(FnmmPath))
            {
                Console.WriteLine($"[Game1] Opening Chart Editor from gameplay with level: {FnmmPath}");
                PauseSong(); // Pause the song while in editor
                OpenChartEditor(FnmmPath, ChartEditorReturnTarget.Gameplay);
            }
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

        // Timeskip: Ctrl+Left/Right OR hold/press T (Shift+T backward) when T is not bound to a note lane
        bool ctrlHeld = kb.IsKeyDown(Keys.LeftControl) || kb.IsKeyDown(Keys.RightControl);
        bool shiftHeld = kb.IsKeyDown(Keys.LeftShift) || kb.IsKeyDown(Keys.RightShift);
        if (TimeskipEnabled && _started && !_paused)
        {
            if (ctrlHeld)
            {
                if (kb.IsKeyDown(Keys.Right) && !_prevKb.IsKeyDown(Keys.Right))
                    SeekSong(_songTime + TimeskipSeconds);
                if (kb.IsKeyDown(Keys.Left) && !_prevKb.IsKeyDown(Keys.Left))
                    SeekSong(_songTime - TimeskipSeconds);
            }

            bool tBoundToLane = NoteRenderer.PlayerKeys.Contains(Keys.T);
            if (!tBoundToLane && kb.IsKeyDown(Keys.T))
            {
                float dir = shiftHeld ? -1f : 1f;
                if (!_prevKb.IsKeyDown(Keys.T))
                {
                    SeekSong(_songTime + dir * TimeskipSeconds);
                    _timeskipRepeatTimer = 0.35f;
                }
                else
                {
                    _timeskipRepeatTimer -= dt;
                    if (_timeskipRepeatTimer <= 0f)
                    {
                        SeekSong(_songTime + dir * TimeskipSeconds);
                        _timeskipRepeatTimer = 0.20f;
                    }
                }
            }
            else
            {
                _timeskipRepeatTimer = 0f;
            }
        }

        // PageUp/PageDown adjust visual scroll speed in gameplay (-/+ are dedicated to Master Volume)
        if (_level != null && _started && !_paused)
        {
            bool speedUp = kb.IsKeyDown(Keys.PageUp) && !_prevKb.IsKeyDown(Keys.PageUp);
            bool speedDown = kb.IsKeyDown(Keys.PageDown) && !_prevKb.IsKeyDown(Keys.PageDown);
            if (speedUp)
            {
                NoteRenderer.ScrollMultiplier = MathF.Round(Math.Min(5.0f, NoteRenderer.ScrollMultiplier + 0.25f), 2);
                Settings.Save(_graphics.SynchronizeWithVerticalRetrace);
            }
            if (speedDown)
            {
                NoteRenderer.ScrollMultiplier = MathF.Round(Math.Max(0.25f, NoteRenderer.ScrollMultiplier - 0.25f), 2);
                Settings.Save(_graphics.SynchronizeWithVerticalRetrace);
            }
        }

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
                        FinalizeLoadedLevelForGameplay(showReadyScreen: true);
                    }
                    catch (Exception ex)
                    {
                        HandleSetupLevelFailure(ex);
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
                        _musicInst?.Play();
                        if (_playtestStartSeconds > 0.1f)
                            SeekSong(_playtestStartSeconds);
                    }
                }
                RefreshActiveDiscordPresence(immediate: true);
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
                ResetMultiplayerMatchState();
                
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
                var winBounds = Window.ClientBounds;
                var (goDst, goScale) = ComputeLetterbox(winBounds.Width, winBounds.Height);
                Point rtMouse = goScale > 0.001f
                    ? new Point((int)((ms.X - goDst.X) / goScale), (int)((ms.Y - goDst.Y) / goScale))
                    : new Point(-9999, -9999);
                bool mouseClicked = ms.LeftButton == ButtonState.Pressed && prevMs.LeftButton == ButtonState.Released;

                var decision = _gameOverOverlay.Update(dt, kb, _prevKb, rtMouse, mouseClicked);
                
                if (decision == true)  // Retry
                {
                    RestartSong();
                }
                else if (decision == false)  // Back to menu
                {
                    if (_playtestingFromEditor)
                    {
                        ReturnFromPlaytest();
                    }
                    else
                    {
                        _gameOverOverlay.Hide();
                        _musicInst?.Stop();
                        _started = false;
                        _freeplay?.ResetInputCooldown();
                        _songTransition = new SongTransition(_catTex, _pixel!);
                        _songTransition.Start(_rtWidth, _rtHeight);
                        _transitionToFreeplay = true;
                    }
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

            if (_songTransition == null)
                UpdateGameplayWorld(gameTime, dt, kb, _prevKb);
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
                    _gameplayPresenceStartUtc = null;
                    _inFreeplay = true;
                    ResetWindowTitle();
                    _freeplay?.Refresh();
                    _freeplay?.ResetInputCooldown();
                    _level = null;
                    RefreshActiveDiscordPresence(immediate: true);
                }
                else
                {
                    _musicInst?.Stop();
                    _gameplayPresenceStartUtc = null;
                    _inMultiplayer = true;
                    ResetWindowTitle();
                    _multiplayerMenu?.ResetAfterMatch();
                    _multiplayerMenu?.ResetInputCooldown();
                    Console.WriteLine("[Game1] Returned to multiplayer menu after match");
                    _level = null;
                    RefreshActiveDiscordPresence(immediate: true);
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

        UpdateLiveMultiplayerSync(dt);
        
        base.Update(gameTime);
    }

    private void CloseAllInMatchOverlays()
    {
        _inPauseMenu = false;
        if (_pauseMenu != null) _pauseMenu.Selection = 0;
        _settingsManager?.Reset();
        _keyBindOverlay.Reset();
        if (_arrowsMenu != null) _arrowsMenu.IsActive = false;
        if (_splashMenu != null) _splashMenu.IsActive = false;
        if (_countdownMenu != null) _countdownMenu.IsActive = false;
        if (_fontMenu != null) _fontMenu.IsActive = false;
    }

    private void ResetMultiplayerMatchState()
    {
        _resultsScreen = null;
        _isMultiplayerMatch = false;
        _multiplayerPlayerRole = 0;
        _multiplayerStartTimestamp = 0;
        _multiplayerMatchStartUtc = null;
        _countdownSignalReceived = false;
        _opponentName = null;
        _opponentPlayerId = null;
        _opponentColorHue = 180;
        _opponentScore = 0;
        _opponentHealth = 0.5f;
        _opponentTargetHealth = 0.5f;
        _opponentReadyState = null;
        _multiplayerMatchId = "";
        _multiplayerLevelName = null;
        _stateUpdateTimer = 0f;
        DiscordRpc.ClearParty();
    }

    private void SyncFocusInputsAfterReactivation(KeyboardState kb, MouseState ms)
    {
        _prevKb = kb;
        _prevMouse = NeutralMouseState(ms);

        if (_activeEditor is Editors.Chart.ChartEditor ce)
            ce.SyncInputState();
        _editorPicker?.SyncInputState();
        _levelSelector?.SyncInputState();
        _mainSettingsMenu?.SyncInputState();
        _hudEditor?.SyncInputState();
        _freeplay?.SyncInputState();
        _multiplayerMenu?.SyncInputState();
    }

    private string GetCurrentSongDisplayName(string? fallbackPath = null)
    {
        if (_isMultiplayerMatch)
        {
            if (_level != null && !string.IsNullOrWhiteSpace(_level.LevelName) &&
                !_level.LevelName.StartsWith("funkymaker_temp_", StringComparison.OrdinalIgnoreCase))
                return _level.LevelName;
            if (!string.IsNullOrWhiteSpace(_multiplayerLevelName))
                return _multiplayerLevelName!;
        }
        if (_level != null && !string.IsNullOrWhiteSpace(_level.LevelName))
            return SanitizeLevelDisplayName(_level.LevelName);
        string? path = !string.IsNullOrWhiteSpace(fallbackPath) ? fallbackPath : FnmmPath;
        if (!string.IsNullOrWhiteSpace(path))
        {
            string fromPath = SanitizeLevelDisplayName(Path.GetFileNameWithoutExtension(path));
            if (!string.IsNullOrWhiteSpace(fromPath))
                return fromPath;
        }
        return "Unknown Level";
    }

    private string? GetResolvedOpponentDisplayName()
    {
        string? candidate = !string.IsNullOrWhiteSpace(_opponentName)
            ? _opponentName
            : Multiplayer.NetworkManager.Instance.OpponentName;
        string resolved = Multiplayer.UserDirectoryClient.Instance.ResolveUsername(_opponentPlayerId, candidate);
        if (!Multiplayer.UserDirectoryClient.IsGenericUsername(resolved))
        {
            if (Multiplayer.UserDirectoryClient.IsGenericUsername(_opponentName) ||
                !string.Equals(_opponentName, resolved, StringComparison.Ordinal))
            {
                _opponentName = resolved;
            }
            return resolved;
        }
        return null;
    }

    private void RefreshActiveDiscordPresence(bool immediate = false)
    {
        if (_isMultiplayerMatch && (_level != null || _loadingScreen != null || _loadState == LoadState.Loading || _loadState == LoadState.Finalizing))
        {
            string? resolvedOpponent = GetResolvedOpponentDisplayName();
            string levelName = GetCurrentSongDisplayName();
            if (!string.IsNullOrEmpty(_multiplayerMatchId))
                DiscordRpc.SetParty(_multiplayerMatchId, 2, 2);

            if (_loadingScreen != null || _loadState == LoadState.Loading || _loadState == LoadState.Finalizing)
            {
                DiscordRpc.SetLoadingMultiplayer(levelName, resolvedOpponent);
            }
            else
            {
                _multiplayerMatchStartUtc ??= DateTimeOffset.UtcNow;
                DiscordRpc.SetPlayingMultiplayer(levelName, resolvedOpponent, _multiplayerMatchStartUtc, immediate: immediate);
            }
            return;
        }

        if (_inMultiplayer && _multiplayerMenu != null)
        {
            var mpInfo = _multiplayerMenu.GetDiscordPresenceInfo();
            if (!string.IsNullOrEmpty(mpInfo.PartyId) && mpInfo.PartySize > 0)
                DiscordRpc.SetParty(mpInfo.PartyId, mpInfo.PartySize, Math.Max(mpInfo.PartyMax, mpInfo.PartySize));
            else
                DiscordRpc.ClearParty();

            DiscordRpc.SetMultiplayerLobby(mpInfo.Details, mpInfo.State, immediate: immediate);
            return;
        }

        if (_inEditor && _activeEditor is Editors.Chart.ChartEditor ce)
        {
            _editorSessionStartUtc ??= DateTimeOffset.UtcNow;
            DiscordRpc.SetChartEditor(ce.LevelDisplayName, ce.IsPlayingAudio, _editorSessionStartUtc, immediate: immediate);
            return;
        }

        if (_inEditorPicker)
        {
            DiscordRpc.SetEditorHub("Choosing an editor");
            return;
        }

        if (_inLevelSelector)
        {
            DiscordRpc.SetEditorHub("Selecting a chart");
            return;
        }

        if (_inHudEditor)
        {
            DiscordRpc.SetEditorHub("HUD Editor");
            return;
        }

        if (_inMainSettings)
        {
            DiscordRpc.SetEditorHub(_hudEditorReturnTarget == HudEditorReturnTarget.EditorPicker ? "HUD Editor" : "Settings");
            return;
        }

        if (_level != null || _loadingScreen != null || _loadState == LoadState.Loading || _loadState == LoadState.Finalizing)
        {
            string levelName = GetCurrentSongDisplayName();
            if (_loadingScreen != null || _loadState == LoadState.Loading || _loadState == LoadState.Finalizing)
            {
                DiscordRpc.SetLoading(levelName);
            }
            else
            {
                _gameplayPresenceStartUtc ??= DateTimeOffset.UtcNow;
                DiscordRpc.SetPlaying(levelName, isPlaytest: _playtestingFromEditor, startTime: _gameplayPresenceStartUtc);
            }
            return;
        }

        if (_inFreeplay && _freeplay != null)
        {
            if (_freeplay.IsPanelOpen)
                DiscordRpc.SetBrowsingMusic(_freeplay.SelectedLevelName);
            else
                DiscordRpc.SetMenu();
            return;
        }

        DiscordRpc.SetMenu();
    }

    private void HandlePendingOpponentDisconnect()
    {
        if (!_pendingOpponentDisconnect) return;
        _pendingOpponentDisconnect = false;
        string reason = _pendingDisconnectReason;

        if (!_isMultiplayerMatch || _resultsScreen != null)
            return;

        Console.WriteLine($"[Game1] Opponent disconnected: {reason}");

        _musicInst?.Stop();

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

            RecordMultiplayerMatchResult(playerRend, opponentDisconnected: true);
        }

        CloseAllInMatchOverlays();
        _loadingScreen = null;
        _songTransition = null;
        _songEndDelay = 0f;
        _level = null;
        _pendingLevel = null;
        _started = false;
        _paused = false;
        _loadState = LoadState.Done;
        _loadingComplete = false;
        _inMultiplayer = false;
        _inFreeplay = false;
        ResetMultiplayerMatchState();

        _errorReturnToMultiplayer = true;
        _errorDismissGrace = 0.25f;
        _errorMsg = $"Opponent Disconnected\n{reason}\n\nPress ENTER, ESC, or click to return to menu";
    }

    private void DismissErrorToMenu()
    {
        if (_playtestingFromEditor)
        {
            _errorMsg = null;
            _errorReturnToMultiplayer = false;
            _errorDismissGrace = 0f;
            _pendingOpponentDisconnect = false;
            ReturnFromPlaytest();
            return;
        }
        bool toMultiplayer = _errorReturnToMultiplayer && _multiplayerMenu != null;
        _errorMsg = null;
        _errorReturnToMultiplayer = false;
        _errorDismissGrace = 0f;
        _pendingOpponentDisconnect = false;

        _musicInst?.Stop();
        CloseAllInMatchOverlays();
        _loadingScreen = null;
        _songTransition = null;
        _songEndDelay = 0f;
        _level = null;
        _pendingLevel = null;
        _started = false;
        _paused = false;
        _gameplayPresenceStartUtc = null;
        _loadState = LoadState.Done;
        _loadingComplete = false;
        ResetMultiplayerMatchState();

        if (toMultiplayer)
        {
            _inFreeplay = false;
            _inMultiplayer = true;
            _multiplayerMenu!.ResetAfterMatch();
            _multiplayerMenu.ResetInputCooldown();
        }
        else
        {
            _inMultiplayer = false;
            _inFreeplay = true;
            _freeplay?.Refresh();
            _freeplay?.ResetInputCooldown();
        }

        ResetWindowTitle();
        RefreshActiveDiscordPresence(immediate: true);
    }

    private void AdvanceLiveMultiplayerIfActive(GameTime gameTime, float dt)
    {
        if (!_isMultiplayerMatch || _level == null || _errorMsg != null ||
            _loadingScreen != null || _resultsScreen != null || _songTransition != null)
            return;

        var emptyKb = default(KeyboardState);
        UpdateGameplayWorld(gameTime, dt, emptyKb, emptyKb);
        UpdateLiveMultiplayerSync(dt);

        if (_resultsScreen != null || _songTransition != null || _errorMsg != null)
            CloseAllInMatchOverlays();
    }

    private void UpdateLiveMultiplayerSync(float dt)
    {
        if (!_isMultiplayerMatch || !_started || _paused)
            return;

        var mp = Multiplayer.MultiplayerSettings.Instance;

        // Smoothly interpolate opponent health if enabled
        if (mp.EnableInterpolation)
            _opponentHealth = MathHelper.Lerp(_opponentHealth, _opponentTargetHealth, 1f - MathF.Exp(-12f * dt));
        else
            _opponentHealth = _opponentTargetHealth;

        // Send state updates to opponent according to configured UpdateRate
        _stateUpdateTimer += dt;
        if (_stateUpdateTimer >= mp.GetStateSyncIntervalSeconds() && _noteRenderers.Count > 0)
        {
            _stateUpdateTimer = 0f;

            var playerRenderer = _noteRenderers[0];
            float health = _healthBar?.Health ?? 0.5f;

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
    }

    private void UpdateGameplayWorld(GameTime gameTime, float dt, KeyboardState gameplayKb, KeyboardState gameplayPrevKb)
    {
        if (_level == null) return;

        // Keep solo gameplay & countdown paused while the window is unfocused
        if (!_isMultiplayerMatch && !IsWindowActive)
        {
            if (_started && !_paused)
            {
                _inPauseMenu = true;
                PauseSong();
            }
            return;
        }

        // ── Countdown before music ───────────────────────────────────────
        if (!_started)
        {
            // Multiplayer: Skip countdown, start immediately at synchronized timestamp
            if (_isMultiplayerMatch && _multiplayerStartTimestamp > 0)
            {
                long now = Multiplayer.TimeSyncManager.Instance.GetServerTime();
                long timeUntilStart = _multiplayerStartTimestamp - now;

                if (timeUntilStart <= 0)
                {
                    Console.WriteLine($"[Game1] 🚀 Multiplayer: Starting NOW! (delay: {-timeUntilStart}ms)");
                    _countdownOverlay?.MarkAsDone();
                    _started       = true;
                    _multiplayerMatchStartUtc = DateTimeOffset.UtcNow;
                    _musicInst?.Play();

                    RefreshActiveDiscordPresence(immediate: true);
                    _songStopwatch.Restart();
                    _pausedTime = 0.0;
                    _songTime = 0f;
                    _songBeats = 0f;
                }
                else if (timeUntilStart <= 50)
                {
                    Console.WriteLine($"[Game1] [♪] Multiplayer: Pre-starting music ({timeUntilStart}ms early for sync)");
                    _countdownOverlay?.MarkAsDone();
                    _started       = true;
                    _multiplayerMatchStartUtc = DateTimeOffset.UtcNow;
                    _musicInst?.Play();

                    RefreshActiveDiscordPresence(immediate: true);
                    _songStopwatch.Restart();
                    _pausedTime = 0.0;
                    _songTime = 0f;
                    _songBeats = 0f;
                }
                else
                {
                    float waitTime = timeUntilStart / 1000.0f;
                    if ((int)(now / 300) != (int)((now - 16) / 300))
                        Console.WriteLine($"[Game1] ⏳ Multiplayer: Waiting {waitTime:F3}s for synchronized start...");
                }
            }
            else if (_countdownOverlay != null)
            {
                bool countdownComplete = _countdownOverlay.Update(dt, _level.Data.EffectiveBpm);
                _songTime = _countdownOverlay.GetSongTime(_level.Data.EffectiveBpm);
                _songBeats = _songTime / 60f * _level.Data.EffectiveBpm;

                if (countdownComplete && !_started)
                {
                    _started = true;
                    _gameplayPresenceStartUtc = DateTimeOffset.UtcNow;
                    if (_isMultiplayerMatch)
                        _multiplayerMatchStartUtc ??= DateTimeOffset.UtcNow;
                    _musicInst?.Play();

                    RefreshActiveDiscordPresence(immediate: true);
                    _songStopwatch.Restart();
                    _pausedTime = 0.0;
                }
            }
        }
        else
        {
            // ── High-precision timing using Stopwatch ────────────────────
            if (!_paused)
            {
                double elapsedSeconds = _songStopwatch.Elapsed.TotalSeconds - _pausedTime;
                _songTime = (float)elapsedSeconds;
            }

            _songBeats = _songTime / 60f * _level.Data.EffectiveBpm;

            // ── Camera zoom on beat (FNF style) ──────────────────────────
            int currentBeat = (int)Math.Floor(_songBeats);
            _cameraManager?.UpdateBeatZoom(currentBeat, dt);

            if (_cameraManager?.UseExtendedCamera == true && _events?.Camera != null)
            {
                _cameraManager.UpdateExtendedCamera(_events.Camera, _rtWidth, _rtHeight, _events.HasCameraEvents);
            }

            if (_corneredEvents != null)
            {
                _corneredEvents.Update(_songTime, currentBeat);
            }

            // Audio recovery: if music stopped unexpectedly, try to restart
            // BUT NOT if the song or audio buffer has naturally ended
            bool audioFileEnded = _music != null && _songTime >= _musicDurationSeconds - 0.1f;
            bool songHasEnded = _songTime >= _songEndTime || audioFileEnded;
            if (_musicInst?.State == SoundState.Stopped && _songTime > 0.5f && !songHasEnded)
            {
                Console.WriteLine($"[Audio Recovery] Music stopped unexpectedly at {_songTime:F2}s, restarting...");
                if (_audioDeviceReset)
                {
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
            else if (songHasEnded && _musicInst?.State == SoundState.Stopped)
            {
                Console.WriteLine($"[Audio] Song ended naturally at {_songTime:F2}s (end time: {_songEndTime:F2}s)");
            }
        }

        // ── Update judgement overlay (popups and combo digits) ───────────
        _judgementOverlay?.Update(dt);

        // ── Note renderers ───────────────────────────────────────────────
        foreach (var renderer in _noteRenderers)
            renderer.Update(_songTime, dt, gameplayKb, gameplayPrevKb);

        if (_noteRenderers.Count > 0)
            _playerMaxCombo = Math.Max(_playerMaxCombo, _noteRenderers[0].Combo);

        _particleNoteSplash?.Update(gameTime);

        if (_started)
        {
            _events?.Update(_songTime, dt);

            foreach (var anim in _objAnimators)
                anim?.Update(dt, _songTime);

            _healthBar?.Update(dt, _songTime, _level.Data.EffectiveBpm);
            if (_isMultiplayerMatch && _healthBar != null && _healthBar.OpponentIcon != null)
            {
                int oppTargetAnim = _opponentHealth < 0.20f ? 1 : 0;
                if (oppTargetAnim >= _healthBar.OpponentIcon.AnimCount) oppTargetAnim = 0;
                if (_healthBar.OpponentIcon.CurrentAnimIndex != oppTargetAnim)
                    _healthBar.OpponentIcon.SetAnimation(oppTargetAnim);
            }
        }

        // ── Notes alpha fade ─────────────────────────────────────────────
        float notesTarget = (_events?.HudVisible ?? true) ? 1.0f : 0.0f;
        _notesAlpha = MathHelper.Lerp(_notesAlpha, notesTarget,
            1f - MathF.Exp(-NOTES_FADE_SPEED * dt));

        // ── Health checks ────────────────────────────────────────────────
        if (_healthBar != null && _healthBar.Health <= 0f && _gameOverOverlay != null && !_gameOverOverlay.IsActive && !_isMultiplayerMatch)
        {
            _gameOverOverlay.Show();
            _musicInst?.Stop();
            Console.WriteLine("[Game1] Game over triggered: health <= 0");
        }

        // ── Song end ─────────────────────────────────────────────────────
        bool musicDone = _musicInst?.State != SoundState.Playing;
        bool chartDone = _started && (_songTime > _songEndTime || (_music != null && _songTime >= _musicDurationSeconds - 0.1f));

        if (chartDone && musicDone && _songTransition == null)
        {
            _songEndDelay += dt;
            if (_songEndDelay >= 1.5f)
            {
                _events?.FlushUnknownLog(_level?.LevelName ?? "unknown");
                _started = false;
                _songEndDelay = 0f;
                _musicInst?.Stop();

                if (_isMultiplayerMatch)
                {
                    var playerRenderer = _noteRenderers.Count > 0 ? _noteRenderers[0] : null;
                    if (playerRenderer != null)
                    {
                        var mp = Multiplayer.MultiplayerSettings.Instance;
                        float playerAccuracy = ComputeAccuracy(
                            playerRenderer.Sicks, playerRenderer.Goods,
                            playerRenderer.Bads, playerRenderer.Shits, playerRenderer.Misses);
                        float opponentAccuracy = ComputeOpponentAccuracy(playerAccuracy, playerRenderer.Score);

                        RecordMultiplayerMatchResult(playerRenderer, opponentDisconnected: false);

                        string playerDisplayName = string.IsNullOrWhiteSpace(mp.Username) ? "Player" : mp.Username.Trim();
                        string opponentDisplayName = GetResolvedOpponentDisplayName()
                            ?? (string.IsNullOrWhiteSpace(_opponentName) ? "Opponent" : _opponentName);
                        bool isDraw = playerRenderer.Score == _opponentScore;
                        bool playerWon = playerRenderer.Score > _opponentScore;

                        UI.UiSfx.Play(UI.UiSound.Save);   // match finished
                        _resultsScreen = new MultiplayerResultsScreen(
                            _pixel!,
                            _catTex,
                            playerDisplayName,
                            opponentDisplayName,
                            playerRenderer.Score,
                            _opponentScore,
                            playerAccuracy,
                            opponentAccuracy,
                            playerRenderer.Sicks,
                            playerRenderer.Goods,
                            playerRenderer.Bads,
                            playerRenderer.Shits,
                            playerRenderer.Misses,
                            playerWon,
                            Color.Lerp(mp.GetPlayerColor(), Color.White, 0.25f),
                            Color.Lerp(Multiplayer.MultiplayerSettings.ColorFromHue(_opponentColorHue), Color.White, 0.25f),
                            isDraw
                        );

                        Console.WriteLine($"[Game1] Multiplayer match ended - showing results ({playerDisplayName}: {playerRenderer.Score}, {opponentDisplayName}: {_opponentScore})");
                    }
                }
                else if (_playtestingFromEditor)
                {
                    ReturnFromPlaytest();
                    return;
                }
                else
                {
                    UI.UiSfx.Play(UI.UiSound.Save);   // song finished
                    _songTransition = new SongTransition(_catTex, _pixel!);
                    _songTransition.Start(_rtWidth, _rtHeight);
                    _transitionToFreeplay = true;
                }
            }
        }
        else if (!chartDone || !musicDone)
        {
            _songEndDelay = 0f;
        }
    }

    private static float ComputeAccuracy(int sicks, int goods, int bads, int shits, int misses)
    {
        int totalNotes = sicks + goods + bads + shits + misses;
        if (totalNotes <= 0) return 100f;
        float weightedHits = (sicks * 1.0f) + (goods * 0.75f) + (bads * 0.5f) + (shits * 0.25f);
        return (weightedHits / totalNotes) * 100f;
    }

    private float ComputeOpponentAccuracy(float playerAccuracy, int playerScore)
    {
        int oppTotal = _opponentSicks + _opponentGoods + _opponentBads + _opponentShits + _opponentMisses;
        if (oppTotal > 0)
            return ComputeAccuracy(_opponentSicks, _opponentGoods, _opponentBads, _opponentShits, _opponentMisses);

        if (_opponentScore > 0)
        {
            float scoreRatio = (float)_opponentScore / Math.Max(1, playerScore);
            return Math.Clamp(playerAccuracy * scoreRatio, 0f, 100f);
        }
        return 100f;
    }

    private void RecordMultiplayerMatchResult(NoteRenderer playerRenderer, bool opponentDisconnected)
    {
        if (_multiplayerMatchRecorded) return;
        _multiplayerMatchRecorded = true;

        var mp = Multiplayer.MultiplayerSettings.Instance;
        string playerName = string.IsNullOrWhiteSpace(mp.Username) ? "Player" : mp.Username.Trim();
        string opponentName = string.IsNullOrWhiteSpace(_opponentName) ? "Opponent" : _opponentName;
        string opponentId = !string.IsNullOrWhiteSpace(_opponentPlayerId)
            ? _opponentPlayerId!
            : $"opp_{opponentName}";

        float playerAccuracy = ComputeAccuracy(
            playerRenderer.Sicks, playerRenderer.Goods,
            playerRenderer.Bads, playerRenderer.Shits, playerRenderer.Misses);
        float opponentAccuracy = ComputeOpponentAccuracy(playerAccuracy, playerRenderer.Score);

        var localInfo = new Multiplayer.PlayerMatchInfo
        {
            PlayerId = mp.PlayerId,
            Username = playerName,
            AvatarIndex = 0,
            ColorHue = mp.PlayerColorHue,
            Score = playerRenderer.Score,
            Accuracy = playerAccuracy,
            MaxCombo = _playerMaxCombo,
            Misses = playerRenderer.Misses,
            Disconnected = false
        };

        var oppInfo = new Multiplayer.PlayerMatchInfo
        {
            PlayerId = opponentId,
            Username = opponentName,
            AvatarIndex = 0,
            ColorHue = _opponentColorHue,
            Score = _opponentScore,
            Accuracy = opponentAccuracy,
            MaxCombo = _opponentMaxCombo,
            Misses = _opponentMisses,
            Disconnected = opponentDisconnected
        };

        bool iAmP1 = _multiplayerPlayerRole != 2;
        var p1 = iAmP1 ? localInfo : oppInfo;
        var p2 = iAmP1 ? oppInfo : localInfo;

        int winner = opponentDisconnected
            ? (iAmP1 ? 1 : 2)
            : (p1.Score > p2.Score ? 1 : (p2.Score > p1.Score ? 2 : 0));

        string matchId = !string.IsNullOrWhiteSpace(_multiplayerMatchId)
            ? _multiplayerMatchId
            : Guid.NewGuid().ToString("N");
        string levelName = !string.IsNullOrWhiteSpace(_level?.LevelName)
            ? _level.LevelName
            : (!string.IsNullOrEmpty(FnmmPath) ? Path.GetFileNameWithoutExtension(FnmmPath) : "Unknown");
        int durationSec = (int)Math.Max(0, _songTime);

        var localEntry = new Multiplayer.MatchHistoryEntry
        {
            MatchId = matchId,
            LevelName = levelName,
            Player1 = p1,
            Player2 = p2,
            Winner = winner,
            DurationSeconds = durationSec,
            Timestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds()
        };

        Multiplayer.LeaderboardManager.Instance.RecordMatch(localEntry);

        // Submit once per match from Role 1 on normal completion, or from the remaining player if opponent disconnected,
        // because server LeaderboardService does not deduplicate MatchId.
        if (Multiplayer.NetworkManager.Instance.IsConnected && (iAmP1 || opponentDisconnected))
        {
            static Multiplayer.PlayerMatchData ToPacket(Multiplayer.PlayerMatchInfo info) => new()
            {
                PlayerId = info.PlayerId,
                Username = info.Username,
                AvatarIndex = info.AvatarIndex,
                ColorHue = info.ColorHue,
                Score = info.Score,
                Accuracy = info.Accuracy,
                MaxCombo = info.MaxCombo,
                Misses = info.Misses,
                Disconnected = info.Disconnected
            };

            Multiplayer.NetworkManager.Instance.SubmitMatchResult(new Multiplayer.MatchHistoryPacket
            {
                MatchId = matchId,
                LevelName = levelName,
                Player1 = ToPacket(p1),
                Player2 = ToPacket(p2),
                Winner = winner,
                DurationSeconds = durationSec,
                Timestamp = localEntry.Timestamp
            });
        }
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
        _gameplayPresenceStartUtc = null;
        if (_healthBar != null)
            _healthBar.Health = 0.5f;
        _gameOverOverlay?.Hide();
        _countdownOverlay?.Reset();
        
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
        _songTransition  = null;
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

    private void ExitSettingsMenu()
    {
#if ANDROID
        _touchControls?.SetSettingsButtonsVisible(false);
#endif
        if (_level != null)
        {
            _inPauseMenu = true;
            if (_pauseMenu != null)
            {
                _pauseMenu.IsMultiplayer = _isMultiplayerMatch;
                _pauseMenu.Selection = 0;
            }
            _pausedByTimeskip = false;
        }
        else if (_inMultiplayer)
        {
            _multiplayerMenu?.ResetInputCooldown();
        }
        else if (_inFreeplay)
        {
            _freeplay?.ResetInputCooldown();
        }
        else if (_pausedByTimeskip && _paused && _started && _level != null)
        {
            ResumeSong();
            _pausedByTimeskip = false;
        }
    }

    private void ApplySettingsSelectResult(SettingsActionResult result)
    {
        if (result.NeedsStrumlineReposition && _level != null)
            RepositionStrumlines();

        if (result.NeedsCameraReset)
            _cameraManager?.ResetScaleOverride();

        if (result.OpenKeyBind)
        {
            _keyBindOverlay.IsActive = true;
            if (!_keyBindCallbacksWired)
            {
                _keyBindCallbacksWired = true;
                _keyBindOverlay.OnComplete += () => {
                    if (_settingsManager != null) _settingsManager.IsActive = true;
                };
                _keyBindOverlay.OnCancel += () => {
                    if (_settingsManager != null) _settingsManager.IsActive = true;
                };
            }
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

        if (result.OpenHudEditor)
        {
#if ANDROID
            _touchControls?.SetSettingsButtonsVisible(false);
#endif
            _hudEditorReturnTarget = _inFreeplay
                ? HudEditorReturnTarget.FreeplaySettings
                : (_inMultiplayer
                    ? HudEditorReturnTarget.MultiplayerSettings
                    : HudEditorReturnTarget.GameplaySettings);
            _inFreeplay = false;
            _inMultiplayer = false;
            _inMainSettings = true;
            _mainSettingsMenu?.ResetInputCooldown();
        }

        if (result.ExitMenu)
        {
            if (_settingsManager != null) _settingsManager.IsActive = false;
            bool openedSubmenu = result.OpenKeyBind || result.OpenArrowsMenu ||
                                 result.OpenSplashMenu || result.OpenCountdownMenu ||
                                 result.OpenFontMenu || result.OpenHudEditor;
            if (openedSubmenu)
            {
#if ANDROID
                _touchControls?.SetSettingsButtonsVisible(false);
#endif
            }
            else
            {
                ExitSettingsMenu();
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
                _graphics.PreferredBackBufferWidth = dm.Width;
                _graphics.PreferredBackBufferHeight = dm.Height;
            }
            else
            {
                _graphics.PreferredBackBufferWidth = 1280;
                _graphics.PreferredBackBufferHeight = 720;
            }
            _graphics.ApplyChanges();
        }

        if (result.NeedsStrumlineAlphaUpdate)
        {
            if (_noteRenderers.Count > 0)
                _noteRenderers[0].Alpha = (NoteRenderer.HidePlayerStrumline && !_isMultiplayerMatch) ? 0f : _notesAlpha;
            if (_noteRenderers.Count > 1)
                _noteRenderers[1].Alpha = GetOpponentStrumlineAlpha();
            for (int si = 2; si < _noteRenderers.Count; si++)
                _noteRenderers[si].Alpha = HideExtraStrumlines ? 0f : _notesAlpha;
        }

        if (result.NeedsOpponentAlphaUpdate && _noteRenderers.Count > 1)
        {
            _noteRenderers[1].Alpha = GetOpponentStrumlineAlpha();
        }

        if (result.NeedsSave)
            Settings.Save(_graphics.SynchronizeWithVerticalRetrace);
    }

    private float GetOpponentStrumlineAlpha()
    {
        if (_isMultiplayerMatch)
        {
            var mp = Multiplayer.MultiplayerSettings.Instance;
            return mp.ShowOpponentNotes ? _notesAlpha * (mp.GhostTransparency / 100f) : 0f;
        }
        return HideOpponentStrumline ? 0f : _notesAlpha;
    }

    private void ApplySettingsAdjustmentResult(SettingsActionResult result)
    {
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
            _volumeToastTimer = VOLUME_TOAST_DURATION;
            Settings.Save(_graphics.SynchronizeWithVerticalRetrace);
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

            DrawSettingsAndSubOverlays(winBounds.Width, winBounds.Height, includeKeyBindOverlay: true);
            
            // Draw touch controls LAST before present
            DrawTouchControls();
            return;
        }
        
        // ── Direct-to-screen UI views (IDirectScreen) ────────────────────────
        if (_inEditorPicker && _editorPicker != null)   { DrawDirectScreen(_editorPicker); return; }
        if (_inLevelSelector && _levelSelector != null) { DrawDirectScreen(_levelSelector); return; }
        if (_inEditor && _activeEditor != null)         { DrawDirectScreen(_activeEditor); return; }
        if (_inMainSettings && _mainSettingsMenu != null) { DrawDirectScreen(_mainSettingsMenu); return; }
        
        // ── HUD Editor (rendered to target for exact aspect ratio) ──────────
        if (_inHudEditor && _hudEditor != null)
        {
            // Render to target (like gameplay) for correct aspect ratio
            GraphicsDevice.SetRenderTarget(_renderTarget);
            GraphicsDevice.Clear(Color.Black);
            
            bool hasLiveScene = _level != null && _errorMsg == null;
            if (hasLiveScene)
            {
                var hudCamMatrix = BuildCameraMatrix(sw, sh);
                _lastCamMatrix = hudCamMatrix;
                DrawSceneOrdered(sw, sh, hudCamMatrix);
                _spriteBatch.Begin(SpriteSortMode.Deferred, BlendState.NonPremultiplied, SamplerState.LinearClamp);
                DrawNotes();
                _spriteBatch.End();
            }
            
            _spriteBatch.Begin(samplerState: SamplerState.PointClamp);
            if (_hudEditor != null)
                _hudEditor.Draw(_spriteBatch, _rtWidth, _rtHeight, hasLiveScene);
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

            DrawSettingsAndSubOverlays(winBounds.Width, winBounds.Height, includeKeyBindOverlay: true);
            
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
                ErrorOverlay.DrawError(_spriteBatch, _font, _pixel, sw, sh, _errorMsg);
            else                   
                ErrorOverlay.DrawDropHint(_spriteBatch, _font, sw, sh);
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
        float mpOpponentAccuracy = _isMultiplayerMatch
            ? ComputeAccuracy(_opponentSicks, _opponentGoods, _opponentBads, _opponentShits, _opponentMisses)
            : 100f;
        GameplayHudOverlay.DrawHUD(
            _spriteBatch, _font, _pixel, sw, sh, _rtWidth, _rtHeight,
            _level, _events, _cameraManager, _healthBar, _timerBar,
            _keyBindOverlay, _noteStyle, _noteRenderers,
            _isMultiplayerMatch, _opponentName, _opponentScore, _opponentHealth,
            _songTime, _songEndTime, _songBeats, _started, _paused, _lastDt,
            _graphics.SynchronizeWithVerticalRetrace,
            _opponentMisses, mpOpponentAccuracy, _opponentColorHue);
        _spriteBatch.End();

        // ── Notes: arrows and receptors (drawn on top of everything) ─────────
        _spriteBatch.Begin(SpriteSortMode.Deferred, BlendState.NonPremultiplied, SamplerState.LinearClamp);
        DrawNotes();
        
        // Draw particle notesplash effects (MonoGame.Extended particles) only when player strumline is visible
        if (HudSettings.Instance.MasterHudVisible &&
            NoteRenderer.ShowStrumlines &&
            !NoteRenderer.HidePlayerStrumline &&
            _notesAlpha > 0.01f)
        {
            _particleNoteSplash?.Draw(_spriteBatch);
        }
        
        _spriteBatch.End();

        // Menus use PointClamp for crisp text rendering (FontStashSharp)
        _spriteBatch.Begin(samplerState: SamplerState.PointClamp);
        GameplayHudOverlay.DrawScreenFlashes(_spriteBatch, _pixel, _events, sw, sh);
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

        // ── Pause menu overlay (drawn AFTER blit for crisp text) ──────────────
        if (_inPauseMenu && _pauseMenu != null)
        {
            var winBounds = Window.ClientBounds;
            GraphicsDevice.SetRenderTarget(null);
            var (dst, scale) = ComputeLetterbox(winBounds.Width, winBounds.Height);
            var stats = GameplayHudOverlay.CalculateStats(_noteRenderers.Count > 0 ? _noteRenderers[0] : null);

            _spriteBatch.Begin(samplerState: SamplerState.LinearClamp);
            _pauseMenu.Draw(_spriteBatch, dst.X, dst.Y, dst.Width, dst.Height, scale, _rtWidth, _rtHeight, gameTime,
                stats.Score, stats.Accuracy, stats.Combo, stats.Misses, stats.Sicks, stats.Goods, stats.Bads, stats.Shits);
            _spriteBatch.End();
        }

        // ── Settings & sub-menu overlays (drawn AFTER blit for crisp text) ────
        DrawSettingsAndSubOverlays(Window.ClientBounds.Width, Window.ClientBounds.Height, includeKeyBindOverlay: false);

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
    /// Renders a full-window <see cref="IDirectScreen"/> straight to the backbuffer, then draws touch controls.
    /// </summary>
    private void DrawDirectScreen(IDirectScreen screen)
    {
        var winBounds = Window.ClientBounds;
        GraphicsDevice.SetRenderTarget(null);
        GraphicsDevice.Clear(Color.Black);

        _spriteBatch.Begin(samplerState: SamplerState.LinearClamp);
        screen.Draw(_spriteBatch, winBounds.Width, winBounds.Height);
        _spriteBatch.End();

        DrawVolumeToastOverlay(winBounds.Width);
        DrawTouchControls();
    }

    /// <summary>
    /// Renders active Settings menu and any open modal customization overlays (<see cref="IScreenOverlay"/>).
    /// </summary>
    private void DrawSettingsAndSubOverlays(int winW, int winH, bool includeKeyBindOverlay)
    {
        if (_settingsManager?.IsActive == true && _font != null && _pixel != null)
        {
            GraphicsDevice.SetRenderTarget(null);
            _spriteBatch.Begin(samplerState: SamplerState.LinearClamp);
            _settingsManager.Draw(this, _spriteBatch, _font, _pixel, winW, winH, _fontRenderer);
            _spriteBatch.End();
        }

        IScreenOverlay?[] overlays = { _arrowsMenu, _splashMenu, _countdownMenu, _fontMenu };
        foreach (var overlay in overlays)
        {
            if (overlay?.IsActive != true) continue;
            GraphicsDevice.SetRenderTarget(null);
            _spriteBatch.Begin(samplerState: SamplerState.LinearClamp);
            overlay.Draw(_spriteBatch, winW, winH);
            _spriteBatch.End();
        }

        if (includeKeyBindOverlay && _keyBindOverlay.IsActive && _font != null && _pixel != null)
        {
            GraphicsDevice.SetRenderTarget(null);
            _spriteBatch.Begin(samplerState: SamplerState.LinearClamp);
            _keyBindOverlay.Draw(_spriteBatch, _font, _pixel, winW, winH, _noteStyle);
            _spriteBatch.End();
        }

        DrawVolumeToastOverlay(winW);
    }

    private void DrawVolumeToastOverlay(int winW)
    {
        if (_volumeToastTimer <= 0f || _pixel == null || (_font == null && _fontRenderer == null)) return;

        float alpha = Math.Clamp(_volumeToastTimer / 0.35f, 0f, 1f);
        int boxW = 240;
        int boxH = 54;
        int boxX = (winW - boxW) / 2;
        int boxY = 14;

        GraphicsDevice.SetRenderTarget(null);
        _spriteBatch.Begin(samplerState: SamplerState.LinearClamp);

        _spriteBatch.Draw(_pixel, new Rectangle(boxX, boxY, boxW, boxH), new Color(16, 18, 28) * (0.90f * alpha));
        _spriteBatch.Draw(_pixel, new Rectangle(boxX, boxY, boxW, 2), new Color(0xFF, 0x44, 0x88) * (0.85f * alpha));
        _spriteBatch.Draw(_pixel, new Rectangle(boxX, boxY + boxH - 2, boxW, 2), new Color(0xFF, 0x44, 0x88) * (0.45f * alpha));

        int bars = 10;
        int barGap = 4;
        int barW = 14;
        int totalBarsW = bars * barW + (bars - 1) * barGap;
        int barsStartX = boxX + (boxW - totalBarsW) / 2;
        int barsY = boxY + 10;
        int activeBars = (int)MathF.Round(MusicVolume * bars);
        for (int i = 0; i < bars; i++)
        {
            int bx = barsStartX + i * (barW + barGap);
            Color bCol = i < activeBars ? Color.White * alpha : Color.White * (0.2f * alpha);
            _spriteBatch.Draw(_pixel, new Rectangle(bx, barsY, barW, 12), bCol);
        }

        int pct = (int)Math.Round(MusicVolume * 100);
        string volTxt = pct == 0 ? "VOLUME: MUTED" : $"VOLUME: {pct}%";
        if (_fontRenderer != null)
        {
            var sz = _fontRenderer.MeasureString(volTxt, 16);
            _fontRenderer.DrawString(_spriteBatch, volTxt,
                new Vector2(boxX + (boxW - sz.X) / 2f, boxY + 27), Color.White * alpha, 16);
        }
        else if (_font != null)
        {
            float scale = 16f / Math.Max(16f, (float)_font.LineSpacing);
            var sz = _font.MeasureString(volTxt) * scale;
            _spriteBatch.DrawString(_font, volTxt,
                new Vector2(boxX + (boxW - sz.X) / 2f, boxY + 27), Color.White * alpha,
                0f, Vector2.Zero, scale, SpriteEffects.None, 0f);
        }

        _spriteBatch.End();
    }

    private (Rectangle bounds, float scale) ComputeLetterbox(int targetW, int targetH)
    {
        float scale = Math.Min((float)targetW / _rtWidth, (float)targetH / _rtHeight);
        int dstW = (int)(_rtWidth * scale);
        int dstH = (int)(_rtHeight * scale);
        int dstX = (targetW - dstW) / 2;
        int dstY = (targetH - dstH) / 2;
        return (new Rectangle(dstX, dstY, dstW, dstH), scale);
    }

    /// <summary>
    /// Blits the render target to the actual window with letterboxing (black bars).
    /// Call this at the end of every Draw path, after all rendering to _renderTarget is done.
    /// </summary>
    private void BlitToScreen()
    {
        if (_renderTarget == null) return;
        GraphicsDevice.SetRenderTarget(null);
        var (dst, _) = ComputeLetterbox(
            GraphicsDevice.PresentationParameters.BackBufferWidth,
            GraphicsDevice.PresentationParameters.BackBufferHeight);

        GraphicsDevice.Clear(Color.Black);
        
        // No shader here - draw render target as-is
        _spriteBatch.Begin(samplerState: SamplerState.LinearClamp);
        _spriteBatch.Draw(_renderTarget, dst, Color.White);
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
    // ── GPU Capabilities & Texture Helpers ─────────────────────────────────
    private int GetGpuMaxTextureSize() => FnmmSceneRenderer.GetGpuMaxTextureSize();

    /// <summary>
    /// Decodes PNG bytes via ImageSharp (delegates to shared <see cref="FnmmSceneRenderer"/>).
    /// </summary>
    internal static Texture2D? LoadTextureViaImageSharp(GraphicsDevice gd, byte[] pngBytes)
        => FnmmSceneRenderer.LoadTextureViaImageSharp(gd, pngBytes);

    private Dictionary<string, Texture2D> LoadMultiplayerIcons(string? assetsRoot)
    {
        var icons = new Dictionary<string, Texture2D>();
        if (string.IsNullOrEmpty(assetsRoot)) return icons;

        string dir = Path.Combine(assetsRoot, "images", "ui", "multiplayer");
        string[] keys = { "connect", "leaderboard", "settings", "back", "lock", "create", "join", "quickplay" };
        foreach (string key in keys)
        {
            string filePath = Path.Combine(dir, key + ".png");
            if (!File.Exists(filePath)) continue;
            try
            {
                Texture2D? iconTex = LoadTextureViaImageSharp(GraphicsDevice, File.ReadAllBytes(filePath));
                if (iconTex != null)
                    icons[key] = iconTex;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[Multiplayer] Failed to load icon {key}: {ex.Message}");
            }
        }
        return icons;
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
            Path.Combine(PlatformPaths.GetAppBaseDirectory(), "Assets"),
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
    /// per-object via the shared <see cref="FnmmSceneRenderer"/> pipeline.
    /// </summary>
    private void DrawSceneOrdered(int sw, int sh, Matrix camMatrix)
    {
        if (_level == null) return;

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
            if (_colorGradeShader.Techniques.Count > 0)
                _colorGradeShader.CurrentTechnique = _colorGradeShader.Techniques[0];
        }

        FnmmSceneRenderer.DrawSceneOrdered(
            _spriteBatch, _level, _objAnimators, _events, _cameraManager,
            sw, sh, camMatrix, activeEffect, includeFixedOverlays: true);
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

        if (!HudSettings.Instance.MasterHudVisible || !NoteRenderer.ShowStrumlines)
        {
            for (int i = 0; i < count; i++)
                _noteRenderers[i].Draw(_spriteBatch, _songTime, _font);
            return;
        }

        // Draw all strumlines — opponent first, player on top
        for (int i = count - 1; i >= 0; i--)
        {
            // Extra strumlines (2+): respect HideExtraStrumlines setting
            if (i >= 2 && HideExtraStrumlines)
            {
                _noteRenderers[i].Alpha = 0f;
            }
            // Opponent strumline (1): use multiplayer ghost/visibility settings when in multiplayer
            else if (i == 1)
            {
                _noteRenderers[i].Alpha = GetOpponentStrumlineAlpha();
            }
            // Player strumline (0): respect HidePlayerStrumline setting in single-player
            else if (i == 0 && NoteRenderer.HidePlayerStrumline && !_isMultiplayerMatch)
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

    // ── HUD, screen flashes & error overlays (extracted to UI/GameplayHudOverlay.cs & UI/ErrorOverlay.cs) ──

    // Screen flashes, DropHint, DrawError, Safe & WrapText extracted to UI/GameplayHudOverlay.cs & UI/ErrorOverlay.cs

    // ── Countdown overlay ─────────────────────────────────────────────────────
    private void DrawCountdown(int sw, int sh)
    {
        if (_level == null) return;
        _countdownOverlay?.Draw(sw, sh, _level.Data.EffectiveBpm, _started);
    }

    // ── Judgement overlay ─────────────────────────────────────────────────────
    private void DrawJudgement(int sw, int sh)
    {
        if (_events?.HudVisible == false || !HudSettings.Instance.MasterHudVisible) return;
        // Draw judgement popups and combo digits using overlay
        _judgementOverlay?.Draw();
    }

    // Game over overlay has been extracted to UI/GameOverOverlay.cs

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
            
            // Save the file to the mods folder
            string destPath = Path.Combine(levelsFolder, fileName);
            
            using (var fileStream = File.Create(destPath))
            {
                stream.CopyTo(fileStream);
            }
            
            Console.WriteLine($"[Game1] Level saved to: {destPath}");
            
            // Refresh the freeplay menu, select the imported level, and show toast notification
            _freeplay.OnLevelsImported(new[] { destPath });
            
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
