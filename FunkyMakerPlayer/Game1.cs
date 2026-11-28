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
    // Settings → CONTROLS key capture (fork)
    private KeyboardState   _keyCapturePrevKb;
    private bool            _keyCaptureHoldoff;
    // _rtWidth/_rtHeight are the LOGICAL size (1280×720 / 720×1280) every gameplay layout uses.
    // (fork) The physical _renderTarget is that size × _rtScale (the window's letterbox scale when
    // NativeRenderResolution is on), and every pass into it draws through RtScaleMatrix, so the
    // game renders at the screen's real resolution without any layout code changing.
    private int             _rtWidth  = 1280;
    private int             _rtHeight = 720;
    private float           _rtScale  = 1f;
    private Matrix RtScaleMatrix => Matrix.CreateScale(_rtScale, _rtScale, 1f);
    /// <summary>(fork) Settings → Display → Render Resolution: true = native window resolution, false = classic 720p.</summary>
    public static bool NativeRenderResolution = true;

    // NOTA: Camera zoom ahora manejado por CameraManager
    // (eliminados: _camScaleOverride, _camZoom, _lastBeat, ZOOM_BEAT_AMOUNT, ZOOM_LERP_SPEED)

    // ── Level ────────────────────────────────────────────────────────────────
    public FnmmLevel?      _level;  // Public for SettingsManager
    private List<NoteRenderer> _noteRenderers = new();  // Support multiple strumlines
    public EventProcessor? _events;  // Public for SettingsManager
    
    public static Game1? Instance { get; private set; }

    /// <summary>Saves settings.json from anywhere (keeps the current VSync state).</summary>
    public static void SaveSettings() =>
        Settings.Save(Instance?._graphics?.SynchronizeWithVerticalRetrace ?? true);

    /// <summary>File of the UI font in use (shown in Settings → Font Style).</summary>
    public static string? CurrentFontPath { get; private set; }

    /// <summary>Fork: the UI font chosen in Settings (file name in mods/fonts), saved in settings.json; null = default.</summary>
    public static string? UiFontFile;

    /// <summary>Switches the UI font to <paramref name="path"/> everywhere; false when it can't be loaded.</summary>
    private bool ApplyUiFont(string? path)
    {
        if (string.IsNullOrEmpty(path) || !System.IO.File.Exists(path)) return false;
        try
        {
            _fontRenderer = new FontRenderer(GraphicsDevice, path);
            FontRenderer.Current = _fontRenderer;
            CurrentFontPath = path;
            _freeplay?.SetFontRenderer(_fontRenderer);
            _mainSettingsMenu?.SetFontRenderer(_fontRenderer);
            _multiplayerMenu?.SetFontRenderer(_fontRenderer);
            Console.WriteLine($"[Game1] Font changed to: {path}");
            return true;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[Game1] Error loading font: {ex.Message}");
            return false;
        }
    }
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
    
    private Microsoft.Xna.Framework.Vector2 _initialCameraPos = Microsoft.Xna.Framework.Vector2.Zero;  // Initial camera position for scrollFactor
    
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
    
    private ModsHelpScreen? _modsHelpScreen;  // Help screen for Android mods setup
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
    private Managers.OnlineMenuMusic? _onlineMenuMusic;   // Portraits of Kings / Jazzroom
    private bool          _inMultiplayer = false;
    
    // ── Multiplayer match state ──────────────────────────────────────────────
    private bool _isMultiplayerMatch = false;  // true if this is a PvP match
    /// <summary>True while a multiplayer match runs (settings lock unfair options).</summary>
    public bool IsMultiplayerMatch => _isMultiplayerMatch;

    // Botplay is switched off for a match and restored when it ends (it used to stay off)
    private bool _hasPreMatchSettings;
    private bool _preMatchBotplay;

    // F3 / F4 scroll speed toast
    private float _scrollToastTimer = 0f;
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
    private MultiplayerResultsScreen? _resultsScreen = null;
    /// <summary>Fork: osu!-style results after a solo song (null otherwise).</summary>
    private UI.SoloResultsScreen? _soloResults;
    private Point _soloResultsTouchPos = new(-9999, -9999);  // Results screen shown after match ends
    
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
    // Song length shown by the timer and used to clamp seeks: the music length when known, else the
    // end of the last note (fork: it used to always be the last note, so the timer hit 0 early and a
    // seek past the last note jumped back).
    private float _songEndTime = float.MaxValue;
    // End of the last note (start + hold), in song seconds.
    private float _chartEndTime = float.MaxValue;
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
    public static float TimeskipSeconds = 0f;     // seconds per timeskip step (0 = Timeskip off)
    /// <summary>
    /// Fork: audio offset in ms for every song (Settings → GAMEPLAY), like FNF's note offset.
    /// Positive = the notes come later (for audio that reaches you late, e.g. Bluetooth).
    /// </summary>
    public static int GlobalOffsetMs = 0;
    public const int MaxOffsetMs = 500;
    public static bool  TimeskipEnabled => TimeskipSeconds > 0f;
    public static bool  ShowDebugInfo   = false;  // Level debug: BPM, cam, events (off by default)
#if ANDROID
    public static bool  ShowFpsCounter  = true;   // FPS counter (enabled by default on Android)
#else
    public static bool  ShowFpsCounter  = false;  // FPS counter (off by default on PC)
#endif
    
    // ── Timer Bar Options (V-Slice style with Legacy support) ─────────────────
    public static string TimerPreset    = "HUD Editor";  // Legacy, HUD Editor, Top, Left, Middle, Right, Strumline
    public static string TimerTextMode  = "Time Left";   // Time Left, Time Elapsed, Song Name, None
    public static bool   TimerForceTop  = false;         // Ignore downscroll (only for Top/Left/Middle/Right/Strumline presets)
    

    public static bool  HideExtraStrumlines = true;   // hide strumlines beyond player+opponent
    public static bool  HideOpponentStrumline = false; // hide opponent strumline (index 1)

    /// <summary>Tint of a non-player character's "miss" (sing animation in dark purple).</summary>
    private static readonly Color NonPlayerMissTint = new Color(0x5B, 0x3A, 0x8C);
    // Timer color presets: 0=white, 1=cyan, 2=pink, 3=yellow, 4=green, 5=custom
    public static int   TimerColorPreset = 1;
    public static Color TimerColorCustom = new Color(0xE0, 0xE0, 0xFF);
    // Master volume (0..1): scales every sound — song, menu music and sound effects.
    public static float MasterVolume = 0.5f;
    // Song music volume (0..1), scaled by MasterVolume.
    public static float SongVolume = 1.0f;
    /// <summary>Volume the playing song's music instance uses.</summary>
    public static float SongPlaybackVolume => Math.Clamp(MasterVolume * SongVolume, 0f, 1f);
    // ── Android Touch Controls Settings ───────────────────────────────────────
    // Hide touch hitbox colors (android only)
    public static bool   HideTouchHitboxColors = false;
    /// <summary>(fork) Opacity of a pressed touch hitbox (Settings → Touch Hitbox Opacity), default 10 %.</summary>
    public static float  TouchHitboxOpacity = 0.1f;
    /// <summary>(fork) On-screen menu buttons (Android): 0 = Auto (hidden on tap-ready screens), 1 = On, 2 = Off.</summary>
    public static int    MenuButtonsMode = 0;
    // Note style name — "Default" or a folder name in mods/notestyles/
    public static string NoteStyleName     = DefaultMods.NoteStyleName;
    // NoteSplash style name — "Default" (programmatic) or a folder name in mods/notesplashes/
    public static string NoteSplashStyleName = DefaultMods.SplashName;
    // Health icons: 0 = move with the health bar divider (FNF style), 1 = pinned to the bar ends
    public static int IconMode = 0;
    // Selected countdown pack name from mods/countdowns/[name]/
    public static string SelectedCountdown = "default";

    // ── Notes/arrows fade (controlled by GUI event HudVisible) ───────────────
    // Smoothly fades notes in/out when HudVisible changes.
    private float _notesAlpha = 1.0f;  // current alpha applied to all NoteRenderers

    /// <summary>
    /// Fork: online matches (and duel replays) don't let chart "hide HUD" (GUI) events hide anything,
    /// since that hurts players; the strumlines only drop to 50% opacity while the event is active.
    /// </summary>
    private bool SoftHudEvents => _isMultiplayerMatch || IsDuelReplay || _runEnemy; // + Freeplay enemy mode

    /// <summary>True when a chart GUI event currently hides the HUD (never in online matches).</summary>
    private bool EventHidesHud => _events?.HudVisible == false && !SoftHudEvents;
    private const float NOTES_FADE_SPEED = 4.0f;  // units/sec (reaches ~98% in ~1s)

    // ── Loading screen ───────────────────────────────────────────────────────
    private LoadingScreen? _loadingScreen;
    private bool           _loadingComplete = false;

    // ── Game state ────────────────────────────────────────────────────────────
    private GameOverOverlay? _gameOverOverlay;
    private bool _gameOverTouchDown;                       // Android: a finger was down last frame
    private Point _gameOverTouchPos = new(-9999, -9999);   // Android: where it last was (render-target px)

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
            _errorMsg ??= $"Gameplay error in {context}: {ex.Message}";
            // CrashLogger.LogException above already invokes CrashDialog.Show once via _onCrashCallback.
            if (Microsoft.Xna.Framework.Game.Activity == null)
            {
                Exit();
            }
#else
            // Desktop: Write to crash.log next to the real executable (not single-file temp dir)
            try
            {
                string crashPath = Path.Combine(PlatformPaths.GetAppBaseDirectory(), "crash.log");
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
#if !ANDROID
        ApplyDevHarnessStartup(); // FMP_* environment variables only (Game1.DevShot.cs)
#endif
        Content.RootDirectory = "Content";
        IsMouseVisible  = true;
        IsFixedTimeStep = false;  // uncapped — runs as fast as the PC allows
        Window.Title    = "Funky Maker Player v" + System.Reflection.Assembly.GetExecutingAssembly().GetName().Version?.ToString(3);
    }
    
    /// <summary>Name shown for a level path: folder name for unpacked levels, else file name without extension.</summary>
    public static string FileDisplayName(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return "";
        string trimmed = path.TrimEnd('/', '\\');
        return Directory.Exists(trimmed) ? Path.GetFileName(trimmed) : Path.GetFileNameWithoutExtension(trimmed);
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
    public void StartMultiplayerMatch(string levelPath, int playerRole, long startTimestamp, string matchId, string? levelDisplayName = null)
    {
        Console.WriteLine($"[Game1] Starting online match {matchId}: {levelPath}, role {playerRole}, start {startTimestamp}");

        _isMultiplayerMatch = true;
        _multiplayerLevelName = SanitizeLevelDisplayName(!string.IsNullOrWhiteSpace(levelDisplayName)
            ? levelDisplayName
            : Path.GetFileNameWithoutExtension(levelPath));
        _multiplayerPlayerRole = playerRole;
        _multiplayerStartTimestamp = startTimestamp;
        _multiplayerMatchStartUtc = null;
        _countdownSignalReceived = false;
        _opponentReadyState = null;
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
        _multiplayerMatchId = matchId;
        _multiplayerMatchRecorded = false;
        var room = Multiplayer.Rooms.RoomClient.Instance;
        DiscordRpc.SetParty(matchId, room.State?.Members.Count ?? 2, room.State?.Settings.MaxPlayers ?? 2);

        // Disable botplay in multiplayer - players must play manually.
        // Remember the player's own settings so the match doesn't change them for good.
        if (!_hasPreMatchSettings)
        {
            _preMatchBotplay = NoteRenderer.Botplay;
            _hasPreMatchSettings = true;
        }
        NoteRenderer.Botplay = false;

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
    
    
    
    
    
    


    /// <summary>A level is loaded or loading (mods folder moves are blocked meanwhile).</summary>
    public bool HasActiveLevel => _level != null || _pendingLevel != null || _loadState == LoadState.Loading || _loadState == LoadState.Finalizing;

    /// <summary>Marks the opponent as ready on the multiplayer loading screen (now, or once it exists).</summary>
    private void MarkOpponentReadyForStart(string source)
    {
        if (!_isMultiplayerMatch) return;
        _opponentReadyState = true;
        var screen = _loadingScreen;
        if (screen == null) return;
        if (_multiplayerPlayerRole == 1) screen.Player2Ready = true;
        else if (_multiplayerPlayerRole == 2) screen.Player1Ready = true;
        Console.WriteLine($"[Game1] Opponent marked ready ({source})");
    }


    protected override void Initialize()
    {
        // Test builds (fork): start the server check right away (wakes a sleeping server early)
        _testGate.Start();

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
        try
        {
            // Phone-sized menus (fork): bigger menus on physically small screens (UI/UiScale.Touch)
            var metrics = Microsoft.Xna.Framework.Game.Activity?.Resources?.DisplayMetrics;
            if (metrics != null && metrics.Xdpi > 1f && metrics.Ydpi > 1f)
                UI.UiScale.InitTouchFromScreen(Math.Min(metrics.WidthPixels / metrics.Xdpi, metrics.HeightPixels / metrics.Ydpi));
        }
        catch (Exception ex) { Console.WriteLine($"[UiScale] DisplayMetrics failed: {ex.Message}"); }
        
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
        // The menus are laid out for ≥ 800×450: smaller windows hit Math.Clamp(min > max) in their layout
        // (e.g. the waiting room below 720 px wide), the Draw error handler then closed the game. Players
        // shrinking / dragging the window fast got there easily.
        TryConfigureDesktopSdlWindow(Window.Handle, MinWindowWidth, MinWindowHeight);
        Window.FileDrop += OnFileDrop;
        
        // Initialize crash logger (desktop writes to file)
        CrashLogger.Initialize();
#endif

        // Hook audio device change
        GraphicsDevice.DeviceReset += (_, _) => _audioDeviceReset = true;

        // Discord Rich Presence & exit cleanup (also terminates any active ffmpeg video process)
        DiscordRpc.Initialize();
        Exiting += (_, _) =>
        {
            try { DisposeOsuExtras(); } catch { }
            try { DisposeLevelTextures(); } catch { }
            try { (_suspendedChartEditor as Editors.Chart.ChartEditor)?.DisposeAudio(); } catch { }
            try { (_activeEditor as IDisposable)?.Dispose(); } catch { }
            DiscordRpc.Shutdown();
        };

        // Initialize managers
        _cameraManager = new CameraManager();
        _settingsManager = new SettingsManager();
        
        // NOTE: UI menus initialized in LoadContent() after _pixel and _font are created
        
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
                            intent.SetData(global::Android.Net.Uri.Parse(Updater.LatestReleasePageUrl));
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

    // Cross-platform SDL2 loader — works on Windows (SDL2.dll) and Linux (libSDL2-2.0.so.0 bundled by MonoGame)
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate byte SdlEventStateDelegate(uint type, int state);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate void SdlSetWindowMinSizeDelegate(IntPtr window, int minW, int minH);

    private static void TryConfigureDesktopSdlWindow(IntPtr windowHandle, int minW, int minH)
    {
        if (windowHandle == IntPtr.Zero) return;
        IntPtr sdl = IntPtr.Zero;
        string[] candidates = OperatingSystem.IsWindows()
            ? new[] { "SDL2.dll", "SDL2" }
            : OperatingSystem.IsMacOS()
                ? new[] { "libSDL2-2.0.0.dylib", "libSDL2.dylib", "SDL2" }
                : new[] { "libSDL2-2.0.so.0", "libSDL2.so", "SDL2" };

        var mgAsm = typeof(Microsoft.Xna.Framework.Game).Assembly;
        foreach (var lib in candidates)
        {
            if (NativeLibrary.TryLoad(lib, mgAsm, null, out sdl) || NativeLibrary.TryLoad(lib, out sdl))
                break;
        }
        if (sdl == IntPtr.Zero) return;

        try
        {
            if (NativeLibrary.TryGetExport(sdl, "SDL_SetWindowMinimumSize", out var minPtr))
                Marshal.GetDelegateForFunctionPointer<SdlSetWindowMinSizeDelegate>(minPtr)(windowHandle, minW, minH);
        }
        catch { }

        try
        {
            if (NativeLibrary.TryGetExport(sdl, "SDL_EventState", out var evtPtr))
                Marshal.GetDelegateForFunctionPointer<SdlEventStateDelegate>(evtPtr)(0x1000 /*SDL_DROPFILE*/, 1 /*SDL_ENABLE*/);
        }
        catch { }
    }

    private const int MinWindowWidth = 800;
    private const int MinWindowHeight = 450;
    private bool _audioRecoveryFailed;
#if ANDROID
    private static int _androidAssetsExtracting;
#endif


    // ── Android "not responding" guard (fork) ───────────────────────────────
    // MonoGame runs every frame on the Android UI thread (AndroidGameActivity.RenderOnUIThread = true),
    // and finishing a level load (texture uploads in SetupLevel) is one long frame: seconds for a big
    // level on a slow phone. A tap during that frame can't be delivered, and after 5 s Android shows
    // "isn't responding" (the dialog then stays up while the game runs fine). So the window stops taking
    // touches a few frames before that frame (the flag needs a layout pass to apply) and takes them
    // again right after it. Moving the game loop off the UI thread isn't an option: MonoGame's
    // TouchPanelState has no locking.
    private int _touchGuardFrames = -1;   // -1 = off; >0 = frames left to wait; 0 = window untouchable

    /// <summary>Shown once the level starts when some of its images failed to load (SetupLevel).</summary>
    private string? _textureLoadWarning;

    /// <summary>Every GPU texture of the current level's scene (SetupLevel), freed by <see cref="DisposeLevelTextures"/>.</summary>
    private readonly HashSet<Texture2D> _levelTextures = new();


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
        
        // Create initial render target (landscape 1280×720 logical by default)
        EnsureRenderTarget();

        // Load note style assets from the Assets folder next to the project
        string? assetsRoot = FindAssetsRoot();
        // Copy the default note style / countdown / font into mods/ before anything loads them
        DefaultMods.EnsureInstalled();
        UI.UiSfx.Initialize(assetsRoot);
        _onlineMenuMusic = new Managers.OnlineMenuMusic(assetsRoot);
        if (assetsRoot != null)
        {
            // Note: NoteStyle is loaded AFTER Settings.Load so NoteStyleName is correct
            // Load AnkushCat texture for loading screen and song transitions
            string catPath = System.IO.Path.Combine(assetsRoot, "LoadingScreen", "AnkushCat.png");
            if (File.Exists(catPath))
            {
                _catTex = LoadTextureViaImageSharp(GraphicsDevice, File.ReadAllBytes(catPath));
            }
        }
        // Countdown sprites are (re)loaded from mods/countdowns/ when a song starts
        _countdownOverlay = new UI.CountdownOverlay(GraphicsDevice, _spriteBatch, _font);

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
            // Priority: mods/fonts/main.otf > main.ttf > MilkyNice.otf (the default) > MilkyNice.ttf
            //           > the shipped copy of the default font
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
            
            // Fallback to the shipped copy of the default font
            if (fontPath == null)
            {
                fontPath = DefaultMods.Bundled("fonts", DefaultMods.FontFileName) ?? DefaultMods.FontPath;
            }
            
            if (File.Exists(fontPath))
            {
                try
                {
                    _fontRenderer = new FontRenderer(GraphicsDevice, fontPath);
                    FontRenderer.Current = _fontRenderer;
                    CurrentFontPath = fontPath;
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
        
        // Init mods help screen
        _modsHelpScreen = new ModsHelpScreen(_fontRenderer, _pixel);
        
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
        #if ANDROID
        _freeplay.IsPointOverOverlayControl = p => _touchControls?.IsPointOnMenuControl(p) ?? false;
        #endif

        // Init multiplayer menu (for accessing multiplayer lobby)
        _multiplayerMenu = new Multiplayer.MultiplayerMenu();
        _multiplayerMenu.SetGame(this);  // Pass reference to this Game1 instance
        _multiplayerMenu.SetFontRenderer(_fontRenderer);
        _multiplayerMenu.SetIcons(LoadMultiplayerIcons(assetsRoot));
        // The online song select is the Freeplay browser (fork, FreeplayMenu.Online.cs)
        if (_pixel != null) _multiplayerMenu.SetSongPickerResources(_pixel, _font, _folderIcon);
        
#if ANDROID
        // Initialize touch controls for Android
        _touchControls = new TouchControls();
        _touchControls.Initialize(GraphicsDevice, 
            GraphicsDevice.PresentationParameters.BackBufferWidth,
            GraphicsDevice.PresentationParameters.BackBufferHeight);
        _touchControls.IsLaneHolding = lane => _noteRenderers.Count > 0 && _noteRenderers[0].IsHoldActive(lane);
        Console.WriteLine("[TouchControls] Initialized for Android");
        
        // Initialize touch controls for multiplayer menu
        _multiplayerMenu.InitializeTouchControls(GraphicsDevice,
            GraphicsDevice.PresentationParameters.BackBufferWidth,
            GraphicsDevice.PresentationParameters.BackBufferHeight);
#endif

        // Load persisted settings — must happen before NoteStyle so NoteStyleName is set
        Settings.Load(this);

        // Fork: the UI font chosen in Settings (any letter case: Android is case-sensitive)
        if (!string.IsNullOrEmpty(UiFontFile))
        {
            string saved = CaseInsensitivePaths.Find(PlatformPaths.GetFontsPath(), UiFontFile);
            if (!ApplyUiFont(saved)) UiFontFile = null; // deleted: back to the default
        }

        // Now load NoteStyle and NoteSplashStyle with the correct names from settings
        _noteStyle = NoteStyle.LoadByName(NoteStyleName, GraphicsDevice);
        foreach (var r in _noteRenderers)
            r.SetStyle(_noteStyle);
        
        // Load notesplash style (null = programmatic default)
        ApplyNoteSplashStyle();

        // Online room matches (fork): RoomClient / NetworkManager events, raised on the game thread.
        HookRoomMatchEvents();
        UI.TextEntry.Attach(Window);

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


    // ── Update ───────────────────────────────────────────────────────────────
    private int _seenModsGeneration = ModsStorage.Generation;

    protected override void Update(GameTime gameTime)
    {
        // Test builds (fork): nothing else runs until the server allows it (Game1.TestGate.cs)
        if (UpdateTestGate(gameTime))
        {
            base.Update(gameTime);
            return;
        }

#if !ANDROID
        UpdateDevHarness((float)gameTime.ElapsedGameTime.TotalSeconds); // FMP_* dev harness only
#endif
        var uiSnapshot = CaptureUiSfxSnapshot();
        try
        {
            if (_seenModsGeneration != ModsStorage.Generation)
            {
                // The mods folder moved (Settings → Move Mods Folder): rescan the level lists (fork).
                _seenModsGeneration = ModsStorage.Generation;
                _freeplay?.Refresh();
                _levelSelector?.RefreshLevels();
            }
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
            UpdateOnlineMenuMusic(gameTime);
            SyncChosenStyles(); // styles changed anywhere apply at once (fork, Game1.StyleSync.cs)
            // Freeplay audio preview (fork): stops on its own when Freeplay isn't the active screen
            _freeplay?.UpdatePreview((float)gameTime.ElapsedGameTime.TotalSeconds,
                _inFreeplay && _loadingScreen == null && _errorMsg == null);
            _multiplayerMenu?.UpdateSongPreview((float)gameTime.ElapsedGameTime.TotalSeconds,
                _inMultiplayer && _loadingScreen == null && _errorMsg == null);
        }
    }

    /// <summary>
    /// Online menu background music. Keeps playing while a settings / HUD editor screen opened
    /// from the online menu is up; stops (fades out) when a match starts or the player leaves.
    /// </summary>
    private void UpdateOnlineMenuMusic(GameTime gameTime)
    {
        if (_onlineMenuMusic == null) return;
        bool fromOnlineMenu = (_inHudEditor || _inMainSettings) && _hudEditorReturnTarget == HudEditorReturnTarget.MultiplayerSettings;
        bool inOnlineMenu = (_inMultiplayer || fromOnlineMenu) && _multiplayerMenu != null
                            && !_multiplayerMenu.SongPreviewPlaying; // the song select's preview plays alone
        bool connecting = _inMultiplayer && _multiplayerMenu != null && _multiplayerMenu.IsConnectingScreen;
        _onlineMenuMusic.Update((float)gameTime.ElapsedGameTime.TotalSeconds, inOnlineMenu, connecting);
    }

    private void UpdateInternal(GameTime gameTime)
    {
        UpdateAndroidBack(); // before the first keyboard read: back = Escape this frame (Game1.Touch.cs)
        var rawKb  = LayoutKeyboard.GetState();
        bool windowActive = ComputeWindowActive();

#if ANDROID
        // Never leave the window untouchable (see AndroidTouchGuardReady).
        if (_touchGuardFrames >= 0 && _loadState != LoadState.Finalizing) ReleaseAndroidTouchGuard();

        // Sync touch mode & screen bounds BEFORE polling TouchPanel.GetState() so gameplay hitboxes
        // match the live touch coordinate space and are already active during countdown.
        if (_touchControls != null)
        {
            int touchW = Microsoft.Xna.Framework.Input.Touch.TouchPanel.DisplayWidth;
            int touchH = Microsoft.Xna.Framework.Input.Touch.TouchPanel.DisplayHeight;
            if (touchW <= 0 || touchH <= 0)
            {
                touchW = GraphicsDevice.PresentationParameters.BackBufferWidth;
                touchH = GraphicsDevice.PresentationParameters.BackBufferHeight;
            }
            _touchControls.SyncBounds(touchW, touchH);

            // Results / Game Over / Loading count as menu (A/B buttons instead of the 4 lane hitboxes).
            bool inGameplay = _level != null && _loadingScreen == null && _errorMsg == null &&
                             _songTransition == null && !_inPauseMenu && _resultsScreen == null && _soloResults == null &&
                             !(_gameOverOverlay?.IsActive ?? false) &&
                             !_inFreeplay && !(_settingsManager?.IsActive ?? false) &&
                             !(_arrowsMenu?.IsActive ?? false) && !(_splashMenu?.IsActive ?? false) &&
                             !(_countdownMenu?.IsActive ?? false) && !(_fontMenu?.IsActive ?? false) &&
                             !_inMultiplayer && !_inMainSettings && !_inHudEditor &&
                             !_inEditorPicker && !_inLevelSelector && !_inEditor && !_inReplays &&
                             !_keyBindOverlay.IsActive;
            
            var targetMode = inGameplay ? TouchControls.ControlMode.Gameplay : TouchControls.ControlMode.Menu;
            if (_touchControls.GetMode() != targetMode)
            {
                _touchControls.SetMode(targetMode);
            }
            
            // Allow pause button even in multiplayer (Minecraft-style non-blocking pause/settings)
            _touchControls.SetHidePauseButton(false);
        }

        // Update touch controls before merging virtual keys with physical keyboard state
        _touchControls?.Update();
        UpdateTouchPointer(gameTime); // taps / drags as a mouse for every menu (UI/PointerInput)

        // Merge touch input into keyboard state for seamless integration
        rawKb = MergeTouchWithKeyboard(rawKb);
#endif
        
        float dt = Math.Min((float)gameTime.ElapsedGameTime.TotalSeconds, 0.1f);  // clamp to 100ms max to prevent camera jumps on first frame
        _lastDt  = gameTime.ElapsedGameTime.TotalSeconds;

        var rawMs = UI.PointerInput.GetState(); // the mouse, or touches as a mouse on Android
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

        // Notice window (fork, UI/NoticeWindow.cs): takes every input while open, and for the frames
        // until the closing tap / key is released, so the screen underneath doesn't get it.
        if (UI.NoticeWindow.IsOpen || UI.NoticeWindow.SwallowInput())
        {
            UI.NoticeWindow.Update(dt);
            _prevKb = kb;
            base.Update(gameTime);
            return;
        }

        // Settings → CONTROLS key capture (fork): the capture gets the raw keys; the rest of the game
        // sees no keyboard until every key is released, so the captured key doesn't also act.
        if (_settingsManager != null)
        {
            if (_settingsManager.IsCapturingKey && !_settingsManager.IsActive)
                _settingsManager.CancelKeyCapture();
            if (_settingsManager.IsCapturingKey)
            {
                if (!suppressUserInput && _settingsManager.UpdateKeyCapture(rawKb, _keyCapturePrevKb))
                    Settings.Save(_graphics.SynchronizeWithVerticalRetrace);
                _keyCaptureHoldoff = true;
            }
            _keyCapturePrevKb = rawKb;
            if (_keyCaptureHoldoff)
            {
                if (!_settingsManager.IsCapturingKey && rawKb.GetPressedKeyCount() == 0)
                    _keyCaptureHoldoff = false;
                else
                    kb = default;
            }
        }

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
            bool confirmPressed = (kb.IsKeyDown(Keys.Enter) && !_prevKb.IsKeyDown(Keys.Enter)) ||
                                  (kb.IsKeyDown(Keys.Space) && !_prevKb.IsKeyDown(Keys.Space));
            bool clicked        = !suppressUserInput && ms.LeftButton == ButtonState.Pressed && prevMs.LeftButton == ButtonState.Released;
#if ANDROID
            if (!suppressUserInput && _touchControls?.TapReleased == true)
                clicked = true;
#endif

            if (escPressed || (_errorDismissGrace <= 0f && (confirmPressed || clicked)))
                DismissErrorToMenu();

            _prevKb = kb;
            base.Update(gameTime);
            return;
        }

        if (_volumeToastTimer > 0f)
            _volumeToastTimer = Math.Max(0f, _volumeToastTimer - dt);
        if (_scrollToastTimer > 0f)
            _scrollToastTimer = Math.Max(0f, _scrollToastTimer - dt);

        // Global FNF-style volume hotkeys (- / +) everywhere except text-entry or keybinding capture
        // Menus too; skipped only while typing text (level name, multiplayer fields) and in the
        // Chart Editor, where - / + zoom the grid.
        bool typingText = (_inLevelSelector && _levelSelector != null && _levelSelector.IsTypingName) ||
                          (_inMultiplayer && _multiplayerMenu != null && _multiplayerMenu.IsEditingText) ||
                          (_inFreeplay && _freeplay != null && _freeplay.IsTypingSearch) ||
                          (_settingsManager?.IsActive == true && _settingsManager.IsSearching);
        bool allowVolumeHotkeys = !suppressUserInput && !_keyBindOverlay.IsActive && !_inEditor && !typingText;
        if (allowVolumeHotkeys)
        {
            bool volUp = Controls.Pressed(HotkeyAction.VolumeUp, kb, _prevKb);
            bool volDown = Controls.Pressed(HotkeyAction.VolumeDown, kb, _prevKb);
            if (volUp || volDown)
            {
                float step = volUp ? 0.05f : -0.05f;
                float oldVolume = MasterVolume;
                MasterVolume = MathF.Round(Math.Clamp(MasterVolume + step, 0f, 1f), 2);
                UI.UiSfx.Play(MasterVolume == oldVolume ? UI.UiSound.SliderEnd : volUp ? UI.UiSound.SliderUp : UI.UiSound.SliderDown);
                if (_musicInst != null) _musicInst.Volume = SongPlaybackVolume;
                _volumeToastTimer = VOLUME_TOAST_DURATION;
                Settings.Save(_graphics.SynchronizeWithVerticalRetrace);
            }
        }

        // F3 / F4: scroll speed down / up while a song is on screen, paused or not (no other menu open).
        // Visual only, so it is allowed in multiplayer too.
        bool inSong = _level != null && !_inFreeplay && !_inMultiplayer && !_inEditor && !_inReplays &&
                      !_inEditorPicker && !_inLevelSelector && !_inMainSettings && !_inHudEditor &&
                      !(_settingsManager?.IsActive ?? false) && !_keyBindOverlay.IsActive;
        if (inSong && !suppressUserInput)
        {
            bool slower = Controls.Pressed(HotkeyAction.ScrollSpeedDown, kb, _prevKb);
            bool faster = Controls.Pressed(HotkeyAction.ScrollSpeedUp, kb, _prevKb);
            if (slower || faster)
            {
                float old = NoteRenderer.ScrollMultiplier;
                // Same range as the Scroll Speed setting, in 0.25x steps (the setting row itself moves 0.10x)
                NoteRenderer.ScrollMultiplier = MathF.Round(
                    Math.Clamp(old + (faster ? 0.25f : -0.25f), 0.25f, 5.0f), 2);
                UI.UiSfx.Play(NoteRenderer.ScrollMultiplier == old ? UI.UiSound.SliderEnd
                    : faster ? UI.UiSound.SliderUp : UI.UiSound.SliderDown);
                _scrollToastTimer = VOLUME_TOAST_DURATION;
                Settings.Save(_graphics.SynchronizeWithVerticalRetrace);
            }
        }

#if !ANDROID
        // Toggle borderless fullscreen (default F11, rebindable). Keys are then withheld
        // until released so e.g. the Enter of Alt+Enter doesn't also confirm a menu.
        if (!suppressUserInput && !typingText && !_keyBindOverlay.IsActive &&
            Controls.Pressed(HotkeyAction.ToggleFullscreen, kb, _prevKb))
        {
            SetFullscreen(!_isFullscreen);
            _keyCaptureHoldoff = true;
            kb = default;
        }
#endif

        // ESC opens pause menu (or settings if no level loaded)
        // Skip if in arrows or splash menu (they handle ESC themselves)
        // (the touch Pause button sends ESC too, see MergeTouchWithKeyboard)
        bool escPressedForPause = kb.IsKeyDown(Keys.Escape) && !_prevKb.IsKeyDown(Keys.Escape);

        if (_playtestingFromEditor && _suspendedChartEditor != null && !_inPauseMenu &&
            !(_settingsManager?.IsActive ?? false) && !_keyBindOverlay.IsActive && escPressedForPause)
        {
            ReturnFromPlaytest();
            _prevKb = kb;
            base.Update(gameTime);
            return;
        }
        
        // Online menu: ESC closes the settings overlay opened with F1 / the settings pill. The general
        // ESC handling below skips the online menu (it handles its own ESC), and the menu itself is not
        // updated while the overlay is up, so without this nothing would close it.
        if (escPressedForPause && _inMultiplayer && _settingsManager?.IsActive == true &&
            !(_arrowsMenu?.IsActive ?? false) && !(_splashMenu?.IsActive ?? false) &&
            !(_countdownMenu?.IsActive ?? false) && !(_fontMenu?.IsActive ?? false) &&
            !_keyBindOverlay.IsActive)
        {
            if (!_settingsManager.HandleEscape())   // first ESC releases a focused row / clears the search
            {
                _settingsManager.IsActive = false;
#if ANDROID
                _touchControls?.SetSettingsButtonsVisible(false);
#endif
                // Re-sync the menu's own input snapshot so this ESC isn't seen again as "go back".
                _multiplayerMenu?.ResetInputCooldown();
            }
            _prevKb = kb;
            base.Update(gameTime);
            return;
        }

        if (escPressedForPause &&
            !(_arrowsMenu?.IsActive ?? false) && !(_splashMenu?.IsActive ?? false) &&
            !(_countdownMenu?.IsActive ?? false) && !(_fontMenu?.IsActive ?? false) &&
            !_inMainSettings && !_inHudEditor && !_inMultiplayer &&
            !_inEditorPicker && !_inLevelSelector && !_inEditor && !_inReplays && !_keyBindOverlay.IsActive)
        {
            // Allow pause in both singleplayer and multiplayer (Minecraft-style live pause in MP)
            if (_settingsManager?.IsActive == true)
            {
                // ESC first releases a focused value / clears the search
                if (_settingsManager.HandleEscape())
                {
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
            var action = _pauseMenu.HandleInput(kb, _prevKb, ms, prevMs); // keys, mouse or taps

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
                    if (!_isMultiplayerMatch && !BlockEditorOnMobile("HUD Editor"))
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
                        QuitRoomMatch();
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
            _arrowsMenu.Update(kb, _prevKb, GraphicsDevice);
            
            // If menu was closed and we selected a style, apply it
            if (!_arrowsMenu.IsActive)
            {
                if (_arrowsMenu.SelectionConfirmed)
                {
                    // Apply to all note renderers
                    _noteStyle = NoteStyle.LoadByName(NoteStyleName, GraphicsDevice);
                    foreach (var r in _noteRenderers)
                        r.SetStyle(_noteStyle);
                    Settings.Save(_graphics.SynchronizeWithVerticalRetrace);
                }
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
            
            // If menu was closed, apply selection (updating cached _noteSplashStyle / _particleNoteSplash) and reopen settings
            if (!_splashMenu.IsActive)
            {
                if (_splashMenu.SelectionConfirmed)
                {
                    ApplyNoteSplashStyle();
                    Settings.Save(_graphics.SynchronizeWithVerticalRetrace);
                }
                if (_settingsManager != null)
                    _settingsManager.IsActive = true;
            }
            
            AdvanceLiveMultiplayerIfActive(gameTime, dt);
            _prevKb = kb;
            base.Update(gameTime);
            return;
        }

        // ── Countdown menu ───────────────────────────────────────────────────
        if (_countdownMenu?.IsActive == true)
        {
            _countdownMenu.Update(kb, _prevKb, GraphicsDevice);
            
            // If menu was closed, save selection and reopen settings
            if (!_countdownMenu.IsActive)
            {
                if (_countdownMenu.SelectionConfirmed)
                {
                    _countdownOverlay?.EnsureLoaded(SelectedCountdown);
                    Settings.Save(_graphics.SynchronizeWithVerticalRetrace);
                }
                if (_settingsManager != null)
                    _settingsManager.IsActive = true;
            }
            
            AdvanceLiveMultiplayerIfActive(gameTime, dt);
            _prevKb = kb;
            base.Update(gameTime);
            return;
        }

        // ── Font menu ────────────────────────────────────────────────────────
        if (_fontMenu?.IsActive == true)
        {
            _fontMenu.Update(kb, _prevKb, GraphicsDevice);
            
            // If menu was closed, check if we need to reload font (only on confirm, not ESC cancel)
            if (!_fontMenu.IsActive)
            {
                if (_fontMenu.SelectionConfirmed)
                {
                    string newFontPath = _fontMenu.GetSelectedFontPath();
                    if (ApplyUiFont(newFontPath))
                    {
                        // Fork: remember it (it used to reset to the default on every start).
                        // A font from mods/fonts is stored by file name; the shipped default as null.
                        string fontsDir = System.IO.Path.GetFullPath(PlatformPaths.GetFontsPath());
                        UiFontFile = System.IO.Path.GetFullPath(newFontPath).StartsWith(fontsDir, StringComparison.OrdinalIgnoreCase)
                            ? System.IO.Path.GetFileName(newFontPath)
                            : null;
                        SaveSettings();
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
            UI.TextEntry.Clear(); // keys pressed to bind must not reach the settings search afterwards
            _keyBindOverlay.Update(kb, _prevKb);
            AdvanceLiveMultiplayerIfActive(gameTime, dt);
            _prevKb = kb;
            base.Update(gameTime);
            return;
        }

        // ── Settings menu ────────────────────────────────────────────────────
        if (_settingsManager?.IsActive == true && _settingsManager != null)
        {
            // Search box: letters / digits typed anywhere filter every tab
            _settingsManager.UpdateSearch(kb, _prevKb, dt, discardTyping: _keyCaptureHoldoff);

            // Up/Down navigation (releases focus cleanly when switching option; skips section headers)
            if (kb.IsKeyDown(Keys.Up) && !_prevKb.IsKeyDown(Keys.Up))
            {
                _settingsManager.Unfocus();
                _settingsManager.NotifySelectionMoved();
                _settingsManager.MoveSelection(this, -1);
            }

            if (kb.IsKeyDown(Keys.Down) && !_prevKb.IsKeyDown(Keys.Down))
            {
                _settingsManager.Unfocus();
                _settingsManager.NotifySelectionMoved();
                _settingsManager.MoveSelection(this, 1);
            }

            // Enter or Space: executes toggle/action OR focuses/unfocuses adjustable option
            // (Space types into the search while searching)
            if ((kb.IsKeyDown(Keys.Enter) && !_prevKb.IsKeyDown(Keys.Enter)) ||
                (!_settingsManager.IsSearching && kb.IsKeyDown(Keys.Space) && !_prevKb.IsKeyDown(Keys.Space)))
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
                else if (!_settingsManager.IsSearching)   // search results span every tab
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
            // Update mods help screen if visible
            if (_modsHelpScreen != null && _modsHelpScreen.IsVisible)
            {
                _modsHelpScreen.Update(gameTime);
                
                // If user requested to launch folder picker
                if (_modsHelpScreen.LaunchFolderPicker)
                {
#if ANDROID
                    PickAndroidModsFolder();
#endif
                }
                
                // If user closes help screen, return to freeplay
                if (!_modsHelpScreen.IsVisible)
                {
                    _freeplay.ResetInputCooldown();
                    // Show touch controls again
#if ANDROID
                    _touchControls?.SetAllControlsVisible(true);
#endif
                }
                
                _prevKb = kb;
                base.Update(gameTime);
                return;
            }
            
            var winBounds = Window.ClientBounds;
            _freeplay.Update(dt, kb, winBounds.Width, winBounds.Height, winBounds.Width, winBounds.Height);
            if (_freeplay.LevelsDeleted)
            {
                _freeplay.LevelsDeleted = false;
                _levelSelector?.RefreshLevels(); // the online menu watches the folder itself
            }
            
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
                if (!BlockEditorOnMobile("Chart Editor"))
                {
                    _inFreeplay = false;
                    _inLevelSelector = true;
                    _levelSelectorReturnTarget = ChartEditorReturnTarget.Freeplay;
                    _levelSelector = new Editors.Chart.LevelSelector(_pixel!, _font!, _fontRenderer, startCreateModal: true);
                }
            }
            
            // Check if user requested editing a chart from Freeplay panel ("7" or EDIT button)
            if (_freeplay.ChartEditorRequested)
            {
                _freeplay.ConsumeChartEditorRequest();
                OpenChartEditor(_freeplay.LevelPath, ChartEditorReturnTarget.Freeplay);
            }
            
            // Watch a replay picked in Freeplay's attempts window (fork); it returns to Freeplay.
            if (_freeplay.ReplayRequested is { } replayPath)
            {
                _freeplay.ReplayRequested = null;
                var replay = Replay.ReplayStore.Load(replayPath);
                string? replayError = replay == null ? "No se pudo abrir la replay" : StartReplay(replay);
                if (replayError != null) _freeplay.ShowToast(replayError);
            }

            if (_freeplay.LevelChosen)
            {
                _inFreeplay = false;
                FreeplayData.Instance.MarkPlayed(_freeplay.LevelPath); // play count / recent order (fork)
                StartLoadingLevel(_freeplay.LevelPath);
                _freeplay.LevelChosen = false;
            }
            // Main-menu actions (HUD Editor is reached via EDITORS / Settings, not MenuAction)
            int menuAction = _freeplay.ConsumeMenuAction();
            // Check if user selected Multiplayer
            if (menuAction == 7)
            {
                EnterReplayBrowser();
            }
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
        // ── Replays (fork) ───────────────────────────────────────────────────
        if (_inReplays && _replayBrowser != null)
        {
            UpdateReplayBrowser(dt, kb);
            _prevKb = kb;
            base.Update(gameTime);
            return;
        }

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
                if (ce.OpenCharacterEditorRequested)
                {
                    string? initSlug = ce.ConsumeOpenCharacterEditorRequest();
                    _suspendedChartEditor = ce;
                    var charEd = new Editors.Character.CharacterEditor(GraphicsDevice, _pixel!, _font, _fontRenderer, initSlug);
                    charEd.ResetInputCooldown();
                    _activeEditor = charEd;
                    Window.Title = "Funky Maker Character Editor";
                    _prevKb = kb;
                    base.Update(gameTime);
                    return;
                }
            }
            else if (_activeEditor is Editors.Character.CharacterEditor charEd)
            {
                charEd.Update(dt, kb, Math.Max(1, winBounds.Width), Math.Max(1, winBounds.Height));
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
            if (_multiplayerMenu.ConsumeLevelsChanged())
            {
                // Imported / deleted from the online menu: keep the other level lists in step (fork).
                _freeplay?.Refresh();
                _levelSelector?.RefreshLevels();
            }

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
        if (Controls.Pressed(HotkeyAction.OpenChartEditor, kb, _prevKb))
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

        // Timeskip (rebindable, Settings → CONTROLS; default T forward, Shift+T back).
        // Unmodified keys that are also note lane keys are ignored. Holding repeats.
        // Never in multiplayer: skipping desyncs the song from the opponent
        if (TimeskipEnabled && _started && !_paused && !_isMultiplayerMatch)
        {
            Func<Keys, bool> isLaneKey = k => NoteRenderer.PlayerKeys.Contains(k);
            bool back = Controls.Down(HotkeyAction.TimeskipBackward, kb, isLaneKey);
            bool fwd  = !back && Controls.Down(HotkeyAction.TimeskipForward, kb, isLaneKey);
            if (back || fwd)
            {
                float dir = back ? -1f : 1f;
                bool justPressed = back
                    ? Controls.Pressed(HotkeyAction.TimeskipBackward, kb, _prevKb, isLaneKey)
                    : Controls.Pressed(HotkeyAction.TimeskipForward, kb, _prevKb, isLaneKey);
                if (justPressed)
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

        // Skip Breaks (fork): Space skips a long stretch without arrows; replays repeat recorded skips
        UpdateBreakSkip(kb, dt);

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
                if (_pendingLoadException != null)
                {
                    var loadEx = _pendingLoadException;
                    _pendingLoadException = null;
                    _loadState = LoadState.Done;
                    HandleSetupLevelFailure(loadEx);
                    ReleaseAndroidTouchGuard();
                }
                else if (_pendingLevel != null && AndroidTouchGuardReady())
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
                    finally
                    {
                        ReleaseAndroidTouchGuard();
                    }
                }
            }

            // Enter, or (fork) a click / tap anywhere: phones had to find the on-screen A button.
            bool enterPressed = (kb.IsKeyDown(Keys.Enter) && !_prevKb.IsKeyDown(Keys.Enter)) ||
                                (!suppressUserInput && ms.LeftButton == ButtonState.Pressed && prevMs.LeftButton == ButtonState.Released);
            bool cancelLoadingPressed = _isMultiplayerMatch && _loadState == LoadState.Done &&
                (kb.IsKeyDown(Keys.Escape) && !_prevKb.IsKeyDown(Keys.Escape));
#if ANDROID
            if (_isMultiplayerMatch && _loadState == LoadState.Done &&
                ((kb.IsKeyDown(Keys.Back) && !_prevKb.IsKeyDown(Keys.Back)) ||
                 _touchControls?.IsKeyJustPressed(Keys.Escape) == true))
            {
                cancelLoadingPressed = true;
            }
#endif

            if (CheckMultiplayerLoadingScreenStatus(cancelLoadingPressed))
            {
                _prevKb = kb;
                base.Update(gameTime);
                return;
            }
            
            // (The per-frame "LoadingScreen Phase=Ready" log that was here wrote a line every frame while
            // waiting for the online countdown; on Android that's the UI thread.)
            UpdateLoadingScreenRoomPlayers();
            _loadingScreen?.Update(dt, _loadingComplete && _loadState == LoadState.Done, enterPressed, _rtWidth, _rtHeight, _countdownSignalReceived);
            if (_loadingScreen?.CurrentPhase == LoadingScreen.Phase.Done)
            {
                _loadingScreen = null;
                if (_textureLoadWarning != null)
                {
                    ShowReplayMessage(_textureLoadWarning, 12f);
                    _textureLoadWarning = null;
                }
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
#if ANDROID
            // The results only listened to ENTER / ESC, which a phone doesn't have: a tap continues.
            _resultsScreen.Update(dt, kb, _prevKb, _touchControls?.TapReleased ?? false);
#else
            _resultsScreen.Update(dt, kb, _prevKb);
#endif
            
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

        // ── Results Screen (solo, fork) ────────────────────────────────────
        if (_soloResults != null && _songTransition == null)
        {
            UpdateSoloResults(dt, kb, ms, prevMs);
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
                if (OperatingSystem.IsAndroid())
                {
                    // Fork: touches don't arrive as mouse input on Android. A finger lifted = a click
                    // where it last was (MonoGame turns Pressed into Moved on every read, so use down/up).
                    bool down = false;
                    foreach (var t in Microsoft.Xna.Framework.Input.Touch.TouchPanel.GetState())
                    {
                        if (t.State is Microsoft.Xna.Framework.Input.Touch.TouchLocationState.Released
                            or Microsoft.Xna.Framework.Input.Touch.TouchLocationState.Invalid) continue;
                        down = true;
                        _gameOverTouchPos = goScale > 0.001f
                            ? new Point((int)((t.Position.X - goDst.X) / goScale), (int)((t.Position.Y - goDst.Y) / goScale))
                            : new Point(-9999, -9999);
                        break;
                    }
                    rtMouse = _gameOverTouchPos;
                    mouseClicked = !down && _gameOverTouchDown;
                    _gameOverTouchDown = down;
                }

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
        if (_hasPreMatchSettings)
        {
            // Give back the Botplay setting the match switched off
            NoteRenderer.Botplay = _preMatchBotplay;
            _hasPreMatchSettings = false;
            Settings.Save(_graphics.SynchronizeWithVerticalRetrace);
        }
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
        ResetRoomMatchState();
        DiscordRpc.ClearParty();
    }

    private void SyncFocusInputsAfterReactivation(KeyboardState kb, MouseState ms)
    {
        _prevKb = kb;
        _prevMouse = NeutralMouseState(ms);

        if (_activeEditor is Editors.Chart.ChartEditor ce)
            ce.SyncInputState();
        else if (_activeEditor is Editors.Character.CharacterEditor charEd)
            charEd.SyncInputState();
        _editorPicker?.SyncInputState();
        _levelSelector?.SyncInputState();
        _mainSettingsMenu?.SyncInputState();
        _hudEditor?.SyncInputState();
        _freeplay?.SyncInputState();
        _multiplayerMenu?.SyncInputState();
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

            // Fork: a match cut short by a disconnect is not a result. It used to be recorded as a
            // win for the remaining player (local history + server leaderboard); now nothing is sent.
            _multiplayerMatchRecorded = true;
            Console.WriteLine("[Game1] Match not recorded (ended by disconnect)");
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
        _errorMsg = $"Opponent Disconnected\n{reason}\n\n{(UI.PointerInput.IsTouch ? "Tap to return to menu" : "Press ENTER, ESC, or click to return to menu")}";
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
        if (!_isMultiplayerMatch || _paused)
            return;
        UpdateRoomMatch(dt);
    }

    private void UpdateGameplayWorld(GameTime gameTime, float dt, KeyboardState gameplayKb, KeyboardState gameplayPrevKb)
    {
        if (_level == null) return;
        UpdateReplayRecording();
        UpdateOsuExtras(); // osu! video / storyboard samples (fork)
        UpdateReplayMessage(dt);

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
                    // Joined late (spectating a match in progress, or a slow load): catch up.
                    if (-timeUntilStart > 800)
                        SeekSong(-timeUntilStart / 1000f);
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
                    // Fork: 3, 2, 1, Go synced to the server start time (it used to be skipped),
                    // and the notes approach like in solo (negative song time).
                    float waitTime = timeUntilStart / 1000.0f;
                    _countdownOverlay?.SyncTo(waitTime, _level.Data.EffectiveBpm);
                    _songTime = -waitTime;
                    _songBeats = _songTime / 60f * _level.Data.EffectiveBpm;
                }
            }
            else if (_countdownOverlay != null)
            {
                bool countdownComplete = _countdownOverlay.Update(dt * _runRate, _level.Data.EffectiveBpm);
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
                _songTime = (float)(elapsedSeconds * _runRate); // Freeplay song rate (fork)
            }

            _songBeats = _songTime / 60f * _level.Data.EffectiveBpm;

            // ── Camera zoom on beat (FNF style) ──────────────────────────
            int currentBeat = (int)Math.Floor(_songBeats);
            _cameraManager?.UpdateBeatZoom(currentBeat, dt);

            // Audio recovery: if music stopped unexpectedly, try to restart
            // BUT NOT if the song or audio buffer has naturally ended
            bool audioFileEnded = _music != null && _songTime >= _musicDurationSeconds - 0.1f;
            bool songHasEnded = _songTime >= _songEndTime || audioFileEnded;
            if (_musicInst?.State == SoundState.Stopped && _songTime > 0.5f && !songHasEnded && !_audioRecoveryFailed)
            {
                Console.WriteLine($"[Audio Recovery] Music stopped unexpectedly at {_songTime:F2}s, restarting...");
                if (_audioDeviceReset)
                {
                    Console.WriteLine("[Audio Recovery] Device reset detected, reloading music...");
                    if (_level?.MusicData != null)
                    {
                        _musicInst?.Dispose();
                        _music?.Dispose();

                        _music = AudioDecoder.LoadWithPcm(_level.MusicData,
                            out byte[] pcm, out int sr, out int ch);

                        _musicSampleRate = sr;
                        _musicChannels = ch;
                        _musicPcm = pcm;

                        _musicInst = _music.CreateInstance();
                        _musicInstStart = 0f;
                        _musicInst.Volume = SongPlaybackVolume;
                        _musicInst.IsLooped = false;
                        ApplyRunRateToMusic(); // Freeplay song rate (fork)
                    }
                    _audioDeviceReset = false;
                }
                if (!RecreateMusicAt(_songTime))
                {
                    // Avoid spamming recovery logs every single frame when PCM/MusicData was freed (e.g. 32-bit MP)
                    _audioRecoveryFailed = true;
                    Console.WriteLine("[Audio Recovery] Unable to recreate music instance (no PCM/source bytes retained); suppressing further retries for this song.");
                }
            }
            else if (songHasEnded && _musicInst?.State == SoundState.Stopped)
            {
                Console.WriteLine($"[Audio] Song ended naturally at {_songTime:F2}s (end time: {_songEndTime:F2}s)");
            }
            else if (!_paused && !songHasEnded)
            {
                KeepMusicInSync(dt); // fork: pull the music back onto the song clock after stalls
            }
        }

        // ── Update judgement overlay (popups and combo digits) ───────────
        _judgementOverlay?.Update(dt);

        // ── Note renderers ───────────────────────────────────────────────
        // Song-relative timers follow the Freeplay song rate (fork; 1 outside solo play)
        float songDt = dt * _runRate;
        foreach (var renderer in _noteRenderers)
            renderer.Update(NoteTime, songDt, gameplayKb, gameplayPrevKb);

        if (_noteRenderers.Count > 0)
            _playerMaxCombo = Math.Max(_playerMaxCombo, _noteRenderers[0].Combo);

        _particleNoteSplash?.Update(gameTime);

        if (_started)
        {
            _events?.Update(_songTime, songDt);

            foreach (var anim in _objAnimators)
                anim?.Update(songDt, _songTime);

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
        // GUI "hide HUD" event: notes fade out, or to 50% in online matches (SoftHudEvents)
        float notesTarget = (_events?.HudVisible ?? true) ? 1.0f : (SoftHudEvents ? 0.5f : 0.0f);
        _notesAlpha = MathHelper.Lerp(_notesAlpha, notesTarget,
            1f - MathF.Exp(-NOTES_FADE_SPEED * dt));

        // ── Health checks ────────────────────────────────────────────────
        if (_healthBar != null && _healthBar.Health <= 0f && _gameOverOverlay != null && !_gameOverOverlay.IsActive && !_isMultiplayerMatch && !IsWatchingReplay)
        {
            _gameOverOverlay.Show();
            _musicInst?.Stop();
            OnSoloRunFailedForFreeplay(); // failed attempt (fork)
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
                    FinishRoomMatchSong();
                }
                else if (_playtestingFromEditor)
                {
                    ReturnFromPlaytest();
                    return;
                }
                else
                {
                    OnSoloSongFinishedForReplay(); // replays (fork): save the run / check a watched replay
                    var results = OnSoloSongFinishedForFreeplay(); // personal best (fork)
                    // osu!-style results screen (fork); it continues to Freeplay, retries or watches the replay.
                    _soloResults = new UI.SoloResultsScreen(_pixel!, results);
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
        int effectiveCount = HideExtras ? Math.Min(strumlineCount, 2) : strumlineCount;

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


    /// <summary>Called by Settings.Load to apply VSync which needs GraphicsDeviceManager.</summary>
    public void ApplyLoadedSettings(bool vsync)
    {
        _graphics.SynchronizeWithVerticalRetrace = vsync;
        _graphics.ApplyChanges();
        ApplyFpsCap();
    }

    // ── Frame rate cap (fork) ────────────────────────────────────────────────

    /// <summary>Selectable FPS caps for Settings → GENERAL → FPS Cap (0 = unlimited).</summary>
    public static readonly int[] FpsCapOptions = { 0, 30, 60, 75, 90, 120, 144, 165, 180, 240, 360 };

    /// <summary>Frame rate cap in FPS; 0 = unlimited (VSync can still limit it).</summary>
    public static int FpsCap = 0;

    // MonoGame's fixed time step sleeps with the coarse OS timer (~15.6 ms on Windows) and then
    // runs catch-up updates, so caps held only sometimes. Instead the loop stays variable and
    // EndDraw waits for the next frame slot: Sleep for most of it (1 ms timer) and spin the rest.
    private readonly System.Diagnostics.Stopwatch _frameClock = System.Diagnostics.Stopwatch.StartNew();
    private long _nextFrameTick;
    private static bool _timerResolutionRaised;

    [System.Runtime.InteropServices.DllImport("winmm.dll")]
    private static extern uint timeBeginPeriod(uint uPeriod);


    // ── Borderless fullscreen (fork) ─────────────────────────────────────────

    private Point _windowedPosition = new(-1, -1);


    // ── Draw ─────────────────────────────────────────────────────────────────
    protected override void Draw(GameTime gameTime)
    {
        try
        {
            if (DrawTestGate()) return;  // test build still gated (fork)
            DrawInternal(gameTime);
            DrawNoticeWindow();
#if !ANDROID
            DevShotAfterDraw();
#endif
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

        // ── Render everything to the render target (logical size × _rtScale) ──
        EnsureRenderTarget();
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
            
            // Draw mods help screen on top if visible
#pragma warning disable CS8602 // Null check already done in if condition
            if (_modsHelpScreen != null && _modsHelpScreen.IsVisible)
            {
                _spriteBatch.Begin(samplerState: SamplerState.LinearClamp);
                _modsHelpScreen!.Draw(_spriteBatch, winBounds.Width, winBounds.Height);
                _spriteBatch.End();
            }
#pragma warning restore CS8602
            
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
        if (_inReplays && _replayBrowser != null)       { DrawDirectScreen(_replayBrowser); return; }
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
                DrawSceneOrdered(sw, sh, hudCamMatrix, stageStrumlines: true);
                _spriteBatch.Begin(SpriteSortMode.Deferred, BlendState.NonPremultiplied, SamplerState.LinearClamp,
                    null, null, null, RtScaleMatrix);
                DrawLaneUnderlay();
                DrawNotes();
                _spriteBatch.End();
            }

            _spriteBatch.Begin(samplerState: RtTextSampler, transformMatrix: RtScaleMatrix);
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
        if (_loadingScreen != null && _errorMsg == null)
        {
            if (_level != null)
            {
                // Draw scene behind loading screen
                var loadCamMatrix = BuildCameraMatrix(sw, sh);
                DrawSceneOrdered(sw, sh, loadCamMatrix);
            }
            _spriteBatch.Begin(samplerState: SamplerState.PointClamp, transformMatrix: RtScaleMatrix);
            if (_loadingScreen != null)
                _loadingScreen.Draw(_spriteBatch, sw, sh);
            _spriteBatch.End();
            BlitToScreen();
            
            // Draw touch controls LAST before present
            DrawTouchControls();
            return;
        }

        // ── Results Screen (solo, fork) ────────────────────────────────────
        if (_soloResults != null && _songTransition == null)
        {
            GraphicsDevice.SetRenderTarget(_renderTarget);
            GraphicsDevice.Clear(Color.Black);
            _spriteBatch.Begin(samplerState: RtTextSampler, transformMatrix: RtScaleMatrix);
            _soloResults.Draw(_spriteBatch, _font, sw, sh);
            _spriteBatch.End();
            BlitToScreen();
            DrawTouchControls();
            return;
        }

        // ── Results Screen (Multiplayer) ───────────────────────────────────
        if (_resultsScreen != null)
        {
            GraphicsDevice.SetRenderTarget(_renderTarget);
            GraphicsDevice.Clear(Color.Black);
            
            _spriteBatch.Begin(samplerState: RtTextSampler, transformMatrix: RtScaleMatrix);
            _resultsScreen.Draw(_spriteBatch, _font, sw, sh);
            _spriteBatch.End();
            
            BlitToScreen();
            return;
        }

        // ── Error or no level ─────────────────────────────────────────────────
        if (_errorMsg != null || _level == null)
        {
            _spriteBatch.Begin(transformMatrix: RtScaleMatrix);
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
        DrawOsuBackground(sw, sh); // osu! levels only (fork)
        DrawSceneOrdered(sw, sh, camMatrix, stageStrumlines: true);

        // ── HUD: health bar, icons, stats (drawn before notes) ───────────────
        _spriteBatch.Begin(SpriteSortMode.Deferred, BlendState.NonPremultiplied, SamplerState.LinearClamp,
            null, null, null, RtScaleMatrix);
        // Lane underlay first: above the stage, below every HUD element and the notes
        DrawLaneUnderlay();
        GameplayHudOverlay.DrawHUD(
            _spriteBatch, _font, _pixel, sw, sh, _rtWidth, _rtHeight,
            _level, _events, _cameraManager, _healthBar, _timerBar,
            _keyBindOverlay, _noteStyle, _noteRenderers,
            _isMultiplayerMatch || IsDuelReplay, CurrentScoreList(), ScoreListIsTeams,
            _songTime, _songEndTime, _songBeats, _started, _paused, _lastDt,
            _graphics.SynchronizeWithVerticalRetrace, SoftHudEvents);
        _spriteBatch.End();

        // ── Notes: arrows and receptors (drawn on top of everything) ─────────
        _spriteBatch.Begin(SpriteSortMode.Deferred, BlendState.NonPremultiplied, SamplerState.LinearClamp,
            null, null, null, RtScaleMatrix);
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
        DrawOsuOverlay(); // osu! storyboard Overlay layer, above the notes (fork)

        // Menus use PointClamp for crisp text rendering (FontStashSharp)
        _spriteBatch.Begin(samplerState: RtTextSampler, transformMatrix: RtScaleMatrix);
        GameplayHudOverlay.DrawScreenFlashes(_spriteBatch, _pixel, _events, sw, sh);
        DrawCountdown(sw, sh);
        DrawJudgement(sw, sh);
        DrawReplayBanner(sw, sh);
        DrawBreakSkipPrompt(sw, sh);
        
        // Draw game over overlay using extracted UI component
        if (_gameOverOverlay != null && _gameOverOverlay.IsActive && _noteRenderers.Count > 0)
        {
            _gameOverOverlay.Draw(sw, sh, _noteRenderers[0]);
        }
        
        _spriteBatch.End();

        // ── Song transition overlay (AnkushCat cascade) ───────────────────────
        if (_songTransition != null && _songTransition.CurrentPhase != SongTransition.Phase.Done)
        {
            _spriteBatch.Begin(samplerState: SamplerState.LinearClamp, transformMatrix: RtScaleMatrix);
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
        DrawScrollToastOverlay(winBounds.Width);
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
        DrawScrollToastOverlay(winW);
    }

    /// <summary>"SCROLL SPEED: 1.20x" toast for F3 / F4, under the volume toast if both are up.</summary>
    private void DrawScrollToastOverlay(int winW)
    {
        if (_scrollToastTimer <= 0f || _pixel == null || (_font == null && _fontRenderer == null)) return;

        float alpha = Math.Clamp(_scrollToastTimer / 0.35f, 0f, 1f);
        int boxW = 240;
        int boxH = 34;
        int boxX = (winW - boxW) / 2;
        int boxY = _volumeToastTimer > 0f ? 14 + 54 + 6 : 14;

        GraphicsDevice.SetRenderTarget(null);
        _spriteBatch.Begin(samplerState: SamplerState.LinearClamp);
        _spriteBatch.Draw(_pixel, new Rectangle(boxX, boxY, boxW, boxH), new Color(16, 18, 28) * (0.90f * alpha));
        _spriteBatch.Draw(_pixel, new Rectangle(boxX, boxY, boxW, 2), new Color(0xFF, 0x44, 0x88) * (0.85f * alpha));
        _spriteBatch.Draw(_pixel, new Rectangle(boxX, boxY + boxH - 2, boxW, 2), new Color(0xFF, 0x44, 0x88) * (0.45f * alpha));

        string text = $"SCROLL SPEED: {NoteRenderer.ScrollMultiplier:F2}x";
        if (_fontRenderer != null)
        {
            var sz = _fontRenderer.MeasureString(text, 16);
            _fontRenderer.DrawString(_spriteBatch, text,
                new Vector2(boxX + (boxW - sz.X) / 2f, boxY + (boxH - sz.Y) / 2f), Color.White * alpha, 16);
        }
        else if (_font != null)
        {
            float scale = 16f / Math.Max(16f, (float)_font.LineSpacing);
            var sz = _font.MeasureUi(text) * scale;
            _spriteBatch.DrawUiString(_font, text,
                new Vector2(boxX + (boxW - sz.X) / 2f, boxY + (boxH - sz.Y) / 2f), Color.White * alpha,
                0f, Vector2.Zero, scale, SpriteEffects.None, 0f);
        }
        _spriteBatch.End();
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
        int activeBars = (int)MathF.Round(MasterVolume * bars);
        for (int i = 0; i < bars; i++)
        {
            int bx = barsStartX + i * (barW + barGap);
            Color bCol = i < activeBars ? Color.White * alpha : Color.White * (0.2f * alpha);
            _spriteBatch.Draw(_pixel, new Rectangle(bx, barsY, barW, 12), bCol);
        }

        int pct = (int)Math.Round(MasterVolume * 100);
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
            var sz = _font.MeasureUi(volTxt) * scale;
            _spriteBatch.DrawUiString(_font, volTxt,
                new Vector2(boxX + (boxW - sz.X) / 2f, boxY + 27), Color.White * alpha,
                0f, Vector2.Zero, scale, SpriteEffects.None, 0f);
        }

        _spriteBatch.End();
    }


    /// <summary>(fork) Text overlays used PointClamp for crisp 1:1 text; once scaled, linear looks better.</summary>
    private SamplerState RtTextSampler => Math.Abs(_rtScale - 1f) < 0.01f ? SamplerState.PointClamp : SamplerState.LinearClamp;

    // Integer nearest-neighbour upscale of _renderTarget used by PresentRenderTarget (fork)
    private RenderTarget2D? _upscaleTarget;
    private int _maxTexForUpscale;


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
                if (!Directory.Exists(assetsPath) || !File.Exists(Path.Combine(assetsPath, "LoadingScreen", "AnkushCat.png")))
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
                        if (System.Threading.Interlocked.CompareExchange(ref _androidAssetsExtracting, 1, 0) == 0)
                        {
                            // Extract in background thread to avoid ANR (Application Not Responding)
                            Console.WriteLine("[FindAssetsRoot] Starting background extraction...");
                            System.Threading.Tasks.Task.Run(() =>
                            {
                                try
                                {
                                    ExtractAssetsFromAPK();
                                    File.WriteAllText(markerFile, DateTime.Now.ToString());
                                    Console.WriteLine("[FindAssetsRoot] Background extraction completed");
                                }
                                catch (Exception ex)
                                {
                                    Console.WriteLine($"[FindAssetsRoot] Background extraction failed: {ex.Message}");
                                }
                                finally
                                {
                                    System.Threading.Interlocked.Exchange(ref _androidAssetsExtracting, 0);
                                }
                            });
                        }

                        // Wait briefly for extraction to complete (up to 4s) so initial UI textures exist
                        for (int i = 0; i < 40 && !File.Exists(markerFile); i++)
                        {
                            System.Threading.Thread.Sleep(100);
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
                File.Exists(Path.Combine(norm, "LoadingScreen", "AnkushCat.png")))
                return norm;
        }
        return null;
#endif
    }


    private float GetBaseW() => _level?.Scene.Landscape == true ? 2160f : 1920f;
    private float GetBaseH() => _level?.Scene.Landscape == true ? 1920f : 2160f;


#if ANDROID
    /// <summary>
    /// Merge touch controls into keyboard state (Android only)
    /// </summary>
    private KeyboardState MergeTouchWithKeyboard(KeyboardState kb)
    {
        if (_touchControls == null) return kb;
        
        // Get all currently pressed keys from keyboard
        var pressedKeys = new List<Keys>(kb.GetPressedKeys());
        
        // Add virtual gameplay lane keys (following current NoteRenderer.PlayerKeys)
        for (int lane = 0; lane < NoteRenderer.LANE_COUNT && lane < NoteRenderer.PlayerKeys.Length; lane++)
        {
            if (_touchControls.IsLaneDown(lane))
            {
                var laneKey = NoteRenderer.PlayerKeys[lane];
                if (!pressedKeys.Contains(laneKey))
                    pressedKeys.Add(laneKey);
            }
        }

        // Add virtual menu navigation keys; the on-screen Pause button (P) acts as ESC (opens / closes the pause menu)
        foreach (var key in new[] { Keys.Left, Keys.Right, Keys.Up, Keys.Down, Keys.Space, Keys.Enter, Keys.Escape, Keys.P })
        {
            if (!_touchControls.IsKeyDown(key)) continue;
            var mapped = key == Keys.P ? Keys.Escape : key;
            if (!pressedKeys.Contains(mapped))
                pressedKeys.Add(mapped);
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
    /// <param name="stageStrumlines">Also draw the stage strumlines, each right below its character
    /// (positioned/faded first by <see cref="UpdateStageStrumlines"/>).</param>
    private void DrawSceneOrdered(int sw, int sh, Matrix camMatrix, bool stageStrumlines = false)
    {
        if (_level == null) return;

        if (stageStrumlines) UpdateStageStrumlines((float)_lastDt);
        bool withStage = stageStrumlines && _stageStrumlinesActive;
        FnmmSceneRenderer.DrawSceneOrdered(
            _spriteBatch, _level, _objAnimators, _events, _cameraManager,
            sw, sh, camMatrix, null, includeFixedOverlays: true, outputTransform: RtScaleMatrix,
            hasLayerBelow: withStage ? HasStageStrumlineBelow : null,
            drawLayerBelow: withStage ? DrawStageStrumlinesBelow : null);
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
                        transformMatrix: (worldOnly ? _lastCamMatrix : Matrix.Identity) * RtScaleMatrix,
                        samplerState: SamplerState.LinearClamp);
                else
                    _spriteBatch.Begin(
                        transformMatrix: (worldOnly ? _lastCamMatrix : Matrix.Identity) * RtScaleMatrix,
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
                transformMatrix: (worldOnly ? _lastCamMatrix : Matrix.Identity) * RtScaleMatrix,
                samplerState: SamplerState.LinearClamp);
        }
    }

    // ── Notes drawing ────────────────────────────────────────────────────────
    /// <summary>Black lane underlay behind the player strumline (Settings → Graphics → Lane Underlay).</summary>
    private void DrawLaneUnderlay()
    {
        if (_level == null || _noteRenderers.Count == 0 || NoteRenderer.LaneUnderlay <= 0f) return;
        // Same opacity DrawNotes gives the player strumline this frame
        float alpha = NoteRenderer.HidePlayerStrumline && !_isMultiplayerMatch ? 0f : _notesAlpha;
        _noteRenderers[0].DrawLaneUnderlay(_spriteBatch, alpha);
    }

    private void DrawNotes()
    {
        if (_level == null) return;
        int count = _noteRenderers.Count;
        // Stage strumlines (1+ with 3+ strumlines and the extras setting on) are drawn inside the
        // scene by DrawStageStrumlinesBelow and positioned by UpdateStageStrumlines; only the HUD
        // ones are drawn here. Classic layout: player + opponent in their FNF places.

        if (!HudSettings.Instance.MasterHudVisible || !NoteRenderer.ShowStrumlines)
        {
            for (int i = 0; i < count; i++)
                if (!IsStageStrumline(i)) _noteRenderers[i].Draw(_spriteBatch, NoteTime, _font);
            return;
        }

        // Draw all strumlines — opponent first, player on top
        for (int i = count - 1; i >= 0; i--)
        {
            if (IsStageStrumline(i)) continue;
            // Extra strumlines (2+): respect HideExtraStrumlines setting
            if (i >= 2 && HideExtras)
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
            _noteRenderers[i].Draw(_spriteBatch, NoteTime, _font);
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
        if (EventHidesHud || !HudSettings.Instance.MasterHudVisible) return;
        // Draw judgement popups and combo digits using overlay
        _judgementOverlay?.Draw();
    }

    // Game over overlay has been extracted to UI/GameOverOverlay.cs


    private void ApplyNoteSplashStyle()
    {
        _noteSplashStyle = NoteSplashStyle.LoadByName(NoteSplashStyleName, GraphicsDevice);

        if (NoteSplashStyleName.StartsWith("Particles:"))
        {
            _particleNoteSplash = new ParticleNoteSplash(GraphicsDevice);

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
        else
        {
            _particleNoteSplash = null;
        }

        foreach (var r in _noteRenderers)
        {
            r.SetSplashStyle(_noteSplashStyle);
            r.SetParticleSplash(_particleNoteSplash);
        }
    }

}
