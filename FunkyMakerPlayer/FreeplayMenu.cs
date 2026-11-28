using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using FunkyMakerPlayer.UI;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using Microsoft.Xna.Framework.Input;

namespace FunkyMakerPlayer;

/// <summary>
/// Main menu with a slide-in level panel on the right side.
/// Layout:
///   Left area  — title, nav buttons (Freeplay, HUD Editor, Credits)
///   Right panel — slides in when Freeplay is selected, shows level list
/// </summary>
public partial class FreeplayMenu
{
    // ── Public ───────────────────────────────────────────────────────────────
    public bool   LevelChosen  { get; set; }
    public string LevelPath    { get; private set; } = "";
    /// <summary>-1=none, 1=HUD Editor, 2=Credits, 3=Multiplayer, 4=Import File (Android), 5=Open Mods Folder (Android), 6=Grant Folder Access (Android) — consumed once read</summary>
    public int    MenuAction   { get; internal set; } = -1;
    
    /// <summary>True when user presses "7" key in main menu to open Editor Picker</summary>
    public bool EditorPickerRequested { get; private set; }
    
    /// <summary>True when user presses "7" while viewing level list to open Chart Editor directly</summary>
    public bool ChartEditorRequested { get; private set; }

    /// <summary>True when user requests creating a brand-new chart from Freeplay panel</summary>
    public bool NewChartRequested { get; private set; }
    
    /// <summary>True when user presses ESC in the main menu root to open Settings</summary>
    public bool SettingsRequested { get; private set; }
    
    /// <summary>Consumes the editor picker request flag</summary>
    public void ConsumeEditorPickerRequest() => EditorPickerRequested = false;
    
    /// <summary>Consumes the chart editor request flag</summary>
    public void ConsumeChartEditorRequest() => ChartEditorRequested = false;

    /// <summary>Consumes the new chart creator request flag</summary>
    public void ConsumeNewChartRequest() => NewChartRequested = false;
    
    /// <summary>Consumes the settings request flag</summary>
    public void ConsumeSettingsRequest() => SettingsRequested = false;

    /// <summary>Opens and focuses the Freeplay levels sidebar panel directly.</summary>
    public void OpenLevelsPanel()
    {
        Refresh();
        _panelOpen = true;
        _inPanel = true;
        _menuSel = 0;
        ResetInputCooldown();
    }

    public bool IsPanelOpen => _panelOpen;

    /// <summary>
    /// The plain main menu is showing (no panel, credits, modal or animation): Android's back
    /// gesture leaves the app from here instead of opening Settings (fork).
    /// </summary>
    public bool IsAtRoot => !_panelOpen && !_inPanel && !_importModalOpen && !_creditsOpen &&
                            _transition == TransitionState.None && !_updateModalOpen && !_confirming;
    public string? SelectedLevelName => (_panelOpen && _selected >= 0 && _selected < _names.Count)
        ? _names[_selected]
        : null;
    
    /// <summary>Updates the TrueType font renderer when font is changed in Settings</summary>
    public void SetFontRenderer(FontRenderer? fontRenderer)
    {
        _fontRenderer = fontRenderer;
        _ui.FontRenderer = fontRenderer;
    }
    
    /// <summary>Action to call when file picker should be opened (Blazor only)</summary>
    public Action? OnFilePickerRequested { get; set; }

    /// <summary>Optional hit-test returning true when a point overlaps a virtual overlay control (Android D-pad/buttons).</summary>
    public Func<Point, bool>? IsPointOverOverlayControl { get; set; }

    // ── Private ──────────────────────────────────────────────────────────────
    private readonly List<string> _paths = new();
    private readonly List<string> _names = new();
    private int   _selected   = 0;
    private float _scrollY    = 0f;
    private float _targetScrollY = 0f;

    private readonly Texture2D   _pixel;
    private readonly SpriteFont? _font;
    private FontRenderer? _fontRenderer;  // Nueva fuente TrueType
    // Fork: looked up each time, so moving the mods folder (Settings → Move Mods Folder) takes effect at once.
    private string               _levelsFolder => GetDefaultLevelsFolder();
    private readonly Texture2D?  _folderIcon;  // Folder icon texture
    private GraphicsDevice? _graphicsDevice;  // For scissor rectangle clipping

    private KeyboardState _prevKb;
    private MouseState    _prevMs;

    // Panel slide animation
    private float _panelX     = 1f;   // 0 = fully open, 1 = fully closed (normalized)
    private bool  _panelOpen  = false;

    // Drop zone pulse animation
    private float _dropZonePulse = 0f;
    private bool  _dropZonePulseDirection = true;
    
    // Folder icon and import UI hover states
    private bool  _folderIconHover     = false;
    private bool  _importBtnHover      = false;
    private bool  _newBtnHover         = false;
    private bool  _refreshBtnHover     = false;
    private bool  _closePanelBtnHover  = false;
    private bool  _footerBarHover      = false;
    private Rectangle _importBtnBounds     = Rectangle.Empty;
    private Rectangle _newBtnBounds        = Rectangle.Empty;
    private Rectangle _refreshBtnBounds    = Rectangle.Empty;
    private Rectangle _closePanelBtnBounds = Rectangle.Empty;
    private Rectangle _footerBarBounds     = Rectangle.Empty;
    private RasterizerState? _scissorRasterizer = null;

    // Toast notification for imported levels ("[Nombre del nivel] importado")
    private string? _toastMessage = null;
    private float   _toastTimer   = 0f;
    private const float TOAST_DURATION = 3.5f;

    // Modal confirmation after importing a level ("¿Quieres jugarlo ahora? [SÍ] / [NO]")
    private bool      _importModalOpen      = false;
    private string    _importModalLevelPath = "";
    private string    _importModalLevelName = "";
    private int       _importModalCount     = 1;
    private int       _importModalSelection = 0; // 0 = SÍ (Jugar), 1 = NO (Solo añadir)
    private float     _importModalAnim      = 0f;
    private bool      _modalYesHover        = false;
    private bool      _modalNoHover         = false;
    private Rectangle _modalYesBounds       = Rectangle.Empty;
    private Rectangle _modalNoBounds        = Rectangle.Empty;

    // Psych Engine-style non-blocking in-menu update prompt
    private bool      _updateAvailable      = false;
    private bool      _updateModalOpen      = false;
    private bool      _updateDismissed      = false;
    private int       _updateModalSel       = 0; // 0 = Update Now, 1 = Later
    private string    _updateTag            = "";
    private string    _updateVersion        = "";
    private string    _updateUrl            = "";
    private string    _updateChangelog      = "";
    private bool      _updateDownloading    = false;
    private float     _updateProgress       = 0f;
    private string    _updateStatus         = "";
    private string?   _updateError          = null;
    private Rectangle _updateNowBounds      = Rectangle.Empty;
    private Rectangle _updateLaterBounds    = Rectangle.Empty;
    private Rectangle _updateBadgeBounds    = Rectangle.Empty;
    private bool      _updateBadgeHover     = false;
    #if ANDROID
    private bool      _prevTouchDown        = false;
    private Point     _touchStart, _touchLast;
    private bool      _touchDragging;
    private float     _touchHeld;          // fork: hold a row = options menu (like a right click)
    private bool      _touchLongPressed;
    #endif
    
    // Screen dimensions for mouse coordinate transformation
    private int _lastScreenWidth = 1280;   // Render target width
    private int _lastScreenHeight = 720;   // Render target height
    private int _lastWindowWidth = 1280;   // Actual window width
    private int _lastWindowHeight = 720;   // Actual window height

    // Main menu selection: 0=Freeplay, 1=Multiplayer, 2=Editors, 3=Settings, 4=Credits (Desktop)
    private int  _menuSel     = 0;
    private bool _inPanel     = false;  // true when navigating the level list
    private readonly List<Rectangle> _mainMenuCardBounds = new();
    private readonly List<(int Index, Rectangle Bounds)> _levelItemBounds = new();
    // Fork: delete confirmation (reuses the import modal with _modalIsDelete).
    private bool   _modalIsDelete;
    private string _deleteModalPath = "";
    private Rectangle _panelBounds     = Rectangle.Empty;
    private Rectangle _levelListBounds = Rectangle.Empty;
    private int   _hoveredLevelIndex   = -1;
    private int   _hoveredMainCard     = -1;
    private int   _lastListHeight      = 580;
    private bool  _manualScroll        = false;
    private bool  _syncPointerNextUpdate = false;
    private Point _lastMenuMousePos    = new(-9999, -9999);
    private Point _lastPointerPos      = new(-9999, -9999);

    // Transition animations
    private enum TransitionState { None, HudEditorFall, CreditsIn }
    private TransitionState _transition = TransitionState.None;
    private float _transTimer  = 0f;
    private const float TRANS_DURATION = 0.45f;

    // Credits fade
    private float _creditsAlpha = 0f;
    private bool  _creditsOpen  = false;
    
    // Clickable links in credits
    private struct ClickableLink
    {
        public Rectangle Bounds;
        public string Url;
        public bool IsHovered;
    }
    private List<ClickableLink> _creditLinks = new();

    private const float PANEL_W_RATIO = 0.35f;  // Panel occupies 35% of screen width

    private int GetItemHeight(int screenWidth, int screenHeight) =>
        UiScale.Px(48, UiScale.FromScreen(screenWidth, screenHeight), 36);

    // Helper for panel width responsive to screen size
    private int GetPanelWidth(int screenWidth, int screenHeight)
    {
        float s = UiScale.FromScreen(screenWidth, screenHeight);
        return Math.Clamp((int)(screenWidth * PANEL_W_RATIO), UiScale.Px(430, s, 320), UiScale.Px(740, s));
    }

    private int GetPanelWidth(int screenWidth) => GetPanelWidth(screenWidth, _lastScreenHeight > 0 ? _lastScreenHeight : 720);

    // Input cooldown — ignores inputs for a short time after opening to prevent accidental selections
    private float _inputCooldown = 0f;
    private const float INPUT_COOLDOWN_DURATION = 0.25f;

    /// <param name="online">Fork: makes this instance the online song select (FreeplayMenu.Online.cs).</param>
    public FreeplayMenu(Texture2D pixel, SpriteFont? font, string levelsFolder, Texture2D? folderIcon = null, FontRenderer? fontRenderer = null,
        IOnlineSongSource? online = null)
    {
        _pixel        = pixel;
        _font         = font;
        _fontRenderer = fontRenderer;
        _folderIcon   = folderIcon;
        _online       = online;
        InitBrowseUi(pixel, font, fontRenderer);
        LoadBrowseState();
        if (_online != null)
        {
            // Always the level panel (no main menu); the tab follows the room's song types.
            _panelOpen = _inPanel = true;
            _panelX = 0f;
            EnsureOnlineTab();
        }
        Refresh();
    }

    /// <summary>Call this when the freeplay menu becomes visible to prevent input bleed-through.</summary>
    public void ResetInputCooldown()
    {
        _inputCooldown = INPUT_COOLDOWN_DURATION;
        _syncPointerNextUpdate = true;
        _hoveredLevelIndex = -1;
        _hoveredMoreIndex = -1;
        _hoveredMainCard = -1;
    }

    /// <summary>Syncs previous input snapshots after window focus changes without resetting scroll or selection.</summary>
    public void SyncInputState()
    {
        _inputCooldown = Math.Max(_inputCooldown, 0.08f);
        _syncPointerNextUpdate = true;
        _prevKb = LayoutKeyboard.GetState();
        _prevMs = Mouse.GetState();
        _hoveredLevelIndex = -1;
        _hoveredMainCard = -1;
        _folderIconHover = false;
        _importBtnHover = false;
        _newBtnHover = false;
        _refreshBtnHover = false;
        _closePanelBtnHover = false;
        _footerBarHover = false;
    }

    /// <summary>Notifies the main menu that a newer GitHub release is available without blocking launch.</summary>
    public void SetAvailableUpdate(string tag, string version, string downloadUrl, string changelog)
    {
        _updateAvailable   = true;
        _updateTag         = tag;
        _updateVersion     = version;
        _updateUrl         = downloadUrl;
        _updateChangelog   = changelog ?? "";
        _updateDownloading = false;
        _updateError       = null;
        if (!_updateDismissed)
        {
            _updateModalOpen = true;
            _updateModalSel  = 0;
        }
    }

    private void BeginInMenuUpdate()
    {
        if (_updateDownloading || string.IsNullOrWhiteSpace(_updateUrl))
            return;

        _updateDownloading = true;
        _updateError       = null;
        _updateProgress    = 0.02f;
        _updateStatus      = "Starting download...";

        System.Threading.Tasks.Task.Run(async () =>
        {
            await Updater.DownloadAndApplyUpdateAsync(
                _updateUrl,
                _updateVersion,
                (p, status) =>
                {
                    _updateProgress = p;
                    _updateStatus   = status;
                },
                err =>
                {
                    _updateError       = err;
                    _updateDownloading = false;
                });
        });
    }

    /// <summary>Shows a temporary toast notification in the Freeplay menu.</summary>
    public void ShowToast(string message)
    {
        _toastMessage = message;
        _toastTimer   = TOAST_DURATION;
        UI.UiSfx.Play(UI.UiSound.Notice);
        Console.WriteLine($"[FreeplayMenu] Toast: {message}");
    }

    /// <summary>
    /// Called when one or more level files are imported (via drag-and-drop or file picker).
    /// Refreshes the level list, opens the sidebar, selects the newly imported level,
    /// and displays a "[Nombre del nivel] importado" notification without forcing level load.
    /// </summary>
    public void OnLevelsImported(IReadOnlyList<string> importedPaths)
    {
        if (importedPaths == null || importedPaths.Count == 0)
            return;

        // osu! files (fork): the Android picker / stream import copy every file into the levels
        // folder; .osz / .osk go to the osu! importer instead (and the copy is removed).
        var osuFiles = importedPaths.Where(p => Osu.OsuLibrary.IsBeatmapArchive(p) || Osu.OsuLibrary.IsSkinArchive(p)).ToList();
        if (osuFiles.Count > 0)
        {
            foreach (var file in osuFiles)
            {
                var r = Osu.OsuLibrary.Import(file);
                if (LevelFiles.IsInLevelsFolder(file) && !file.StartsWith(Osu.OsuLibrary.RootFolder, StringComparison.OrdinalIgnoreCase))
                {
                    try { File.Delete(file); } catch { }
                }
                if (r.Ok && !r.IsSkin && r.Path != null) OnOsuSetImported(r.Path, r.Message);
                else ShowToast(r.Message);
            }
            importedPaths = importedPaths.Except(osuFiles).ToList();
            if (importedPaths.Count == 0) return;
        }

        // Fork: clear the search / favourites filter so the new level is listed.
        _search = "";
        _favoritesOnly = false;
        StopSearchTyping();
        _tab = FreeplayTab.Fnf; // FNF levels live in the FNF tab (fork)
        FreeplayData.Instance.Tab = "fnf";
        Refresh();

        // Ensure the Freeplay sidebar panel is open and focused so the user sees the imported song
        _panelOpen = true;
        _inPanel   = true;
        _menuSel   = 0;

        // Select the last imported level in the list
        string targetPath = importedPaths[^1];
        string targetName = Path.GetFileNameWithoutExtension(targetPath);
        int foundIdx = _paths.FindIndex(p =>
            string.Equals(Path.GetFullPath(p), Path.GetFullPath(targetPath), StringComparison.OrdinalIgnoreCase) ||
            string.Equals(Path.GetFileNameWithoutExtension(p), targetName, StringComparison.OrdinalIgnoreCase));

        if (foundIdx >= 0)
        {
            int itemH = GetItemHeight(_lastScreenWidth, _lastScreenHeight);
            _selected = foundIdx;
            _manualScroll = false;
            _targetScrollY = _selected * itemH;
        }

        if (importedPaths.Count == 1)
        {
            ShowToast($"{targetName} importado");
        }
        else
        {
            ShowToast($"{importedPaths.Count} niveles importados ({targetName})");
        }

        // Open confirmation modal asking whether to play the imported level immediately
        _importModalOpen      = true;
        _modalIsDelete        = false;
        _importModalLevelPath = targetPath;
        _importModalLevelName = targetName;
        _importModalCount     = importedPaths.Count;
        _importModalSelection = 0; // Default to "SÍ"
        _importModalAnim      = 0f;
        ResetInputCooldown();
    }

    /// <summary>Checks whether a file path has a supported Funky Maker level archive extension (.fnmm, .zip, .rar).</summary>
    public static bool IsSupportedLevelFile(string filePath)
    {
        if (string.IsNullOrEmpty(filePath)) return false;
        return filePath.EndsWith(".fnmm", StringComparison.OrdinalIgnoreCase) ||
               filePath.EndsWith(".zip",  StringComparison.OrdinalIgnoreCase) ||
               filePath.EndsWith(".rar",  StringComparison.OrdinalIgnoreCase);
    }

    public void Refresh()
    {
        // Fork: keep the selected level selected (by path); the first scan restores the last one played.
        string? keepPath = SelectedPath;
        if (!_restoredLastSelection)
        {
            _restoredLastSelection = true;
            keepPath = FreeplayData.Instance.LastSelected;
        }
        _allPaths.Clear();
        _allNames.Clear();
        _addedUtc.Clear();

        if (_online == null) Console.WriteLine($"[FreeplayMenu] === REFRESH START ({_tab}) ===");
        // Fork: the online song select lists the room's songs (FreeplayMenu.Online.cs); the
        // OSU!MANIA tab lists osu! beatmap sets (FreeplayMenu.Osu.cs).
        if (_online != null) ScanOnline();
        else if (_tab == FreeplayTab.Osu) ScanOsuSets();
        else if (!ScanFnfLevels())
        {
            RebuildView(null);
            return;
        }

        // A key from freeplay_data.json (relative, lower-case) → the listed path.
        if (keepPath != null && !_allPaths.Contains(keepPath))
        {
            string key = keepPath.ToLowerInvariant();
            keepPath = _allPaths.Find(p => FreeplayData.KeyFor(p) == key) ?? SameOsuSet(keepPath);
        }

        RebuildView(keepPath);
        _selVisual = _selected;
        QueueStarRatings();
        PruneIconCache();
        if (_online == null) Console.WriteLine($"[FreeplayMenu] === REFRESH COMPLETE: {_allPaths.Count} levels ===");
    }

    /// <summary>Scans the levels folder into the full list (FNF tab). False if the folder can't be created.</summary>
    private bool ScanFnfLevels()
    {
        Console.WriteLine($"[FreeplayMenu] Levels folder: {_levelsFolder}");

        // Ensure folder exists before scanning
        if (!Directory.Exists(_levelsFolder))
        {
            Console.WriteLine($"[FreeplayMenu] Levels folder doesn't exist yet: {_levelsFolder}");
            Console.WriteLine($"[FreeplayMenu] Creating it now...");
            try
            {
                Directory.CreateDirectory(_levelsFolder);
                Console.WriteLine($"[FreeplayMenu] ✅ Created: {_levelsFolder}");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[FreeplayMenu] ❌ Failed to create folder: {ex.Message}");
                return false;
            }
        }

        var files = new List<string>();
        string backupsSegment = Path.DirectorySeparatorChar + "Backups" + Path.DirectorySeparatorChar;

        // Scan for .fnmm, .zip, and .rar level archives (case-insensitive)
        foreach (var f in Directory.GetFiles(_levelsFolder, "*.*", SearchOption.AllDirectories))
        {
            if (f.Contains(backupsSegment, StringComparison.OrdinalIgnoreCase))
                continue;

            if (IsSupportedLevelFile(f))
                files.Add(f);
        }
        Console.WriteLine($"[FreeplayMenu] Found {files.Count} level archive files (.fnmm / .zip / .rar)");
        
        // Also list folders that contain a meta.json or data.json file (unpacked levels)
        foreach (var dir in Directory.GetDirectories(_levelsFolder))
        {
            if (Path.GetFileName(dir).Equals("Backups", StringComparison.OrdinalIgnoreCase))
                continue;

            string metaPath = Path.Combine(dir, "meta.json");
            string dataPath = Path.Combine(dir, "data.json");
            if (File.Exists(metaPath) || File.Exists(dataPath))
                files.Add(dir);
        }
        Console.WriteLine($"[FreeplayMenu] Total after folders: {files.Count} items");

        files.Sort(StringComparer.OrdinalIgnoreCase);

        foreach (var f in files)
        {
            bool isDir = Directory.Exists(f);
            _allPaths.Add(f);
            // Directories are unpacked levels: show the folder name as-is
            // (Path.GetFileNameWithoutExtension would strip a dot in the name).
            _allNames.Add(isDir ? Path.GetFileName(f) : Path.GetFileNameWithoutExtension(f));
            // "Newest" order: when the file arrived (a copy gets a new creation time) or was last changed.
            try
            {
                var created = isDir ? Directory.GetCreationTimeUtc(f) : File.GetCreationTimeUtc(f);
                var written = isDir ? Directory.GetLastWriteTimeUtc(f) : File.GetLastWriteTimeUtc(f);
                _addedUtc[f] = created > written ? created : written;
            }
            catch { }
            Console.WriteLine($"[FreeplayMenu]   - {Path.GetFileName(f)}");
        }

        return true;
    }

    public void Update(float dt, KeyboardState kb)
    {
        Update(dt, kb, 1280, 720, 1280, 720); // Default resolution
    }
    
    public void Update(float dt, KeyboardState kb, int screenWidth, int screenHeight, int windowWidth = 0, int windowHeight = 0)
    {
        // Compare against the state seen at the end of the previous frame (so changes made between
        // frames, e.g. the update prompt opened from a background thread, are heard too), then
        // run the update. If the menu was not updated last frame, start from the current state.
        var b = _sfxPrevFrame == UI.UiSfx.Frame - 1 ? _sfxPrev : CaptureSfxState();

        UpdateCore(dt, kb, screenWidth, screenHeight, windowWidth, windowHeight);
        QueueSfx(b);
        _sfxPrev = CaptureSfxState();
        _sfxPrevFrame = UI.UiSfx.Frame;
    }

    private readonly record struct SfxState(
        int _menuSel, string? _selected, bool _panelOpen, bool _creditsOpen, TransitionState _transition,
        bool _importModalOpen, int _importModalSelection, bool _updateModalOpen, int _updateModalSel,
        bool _updateDownloading, bool HasUpdateError);

    private SfxState _sfxPrev;
    private long _sfxPrevFrame = -10;

    private SfxState CaptureSfxState() => new(
        _menuSel, SelectedPath, _panelOpen, _creditsOpen, _transition, _importModalOpen, _importModalSelection,
        _updateModalOpen, _updateModalSel, _updateDownloading, _updateError != null);

    /// <summary>Queues UI sounds for what changed since <paramref name="b"/>.</summary>
    private void QueueSfx(SfxState b)
    {
        // A level played from the list already sounded when its confirm animation started (fork).
        if (LevelChosen && !_levelChosenByConfirm) UI.UiSfx.Play(UI.UiSound.StartSong);
        _levelChosenByConfirm = false;
        if (EditorPickerRequested || ChartEditorRequested || NewChartRequested || SettingsRequested || MenuAction >= 0)
            UI.UiSfx.Play(UI.UiSound.Confirm);

        if (_menuSel != b._menuSel) UI.UiSfx.Play(UI.UiSound.Move);
        if (SelectedPath != b._selected && b._selected != null && _panelOpen && b._panelOpen) UI.UiSfx.Play(UI.UiSound.MoveList);
        if (_panelOpen != b._panelOpen) UI.UiSfx.Play(_panelOpen ? UI.UiSound.Confirm : UI.UiSound.Back);
        if (_transition == TransitionState.CreditsIn && b._transition != TransitionState.CreditsIn) UI.UiSfx.Play(UI.UiSound.Confirm);
        if (b._creditsOpen && !_creditsOpen && !LevelChosen) UI.UiSfx.Play(UI.UiSound.Back);
        if (b._transition == TransitionState.CreditsIn && _transition == TransitionState.None && !_creditsOpen) UI.UiSfx.Play(UI.UiSound.Back);

        // Import "play now?" modal
        if (_importModalOpen && b._importModalOpen && _importModalSelection != b._importModalSelection) UI.UiSfx.Play(UI.UiSound.Move);
        if (b._importModalOpen && !_importModalOpen && !LevelChosen) UI.UiSfx.Play(UI.UiSound.Back);

        // Update prompt (opened from a background thread → detected here on the main thread)
        if (_updateModalOpen && !b._updateModalOpen) UI.UiSfx.Play(UI.UiSound.Receive);
        if (_updateModalOpen && b._updateModalOpen && _updateModalSel != b._updateModalSel) UI.UiSfx.Play(UI.UiSound.Move);
        if (b._updateModalOpen && !_updateModalOpen) UI.UiSfx.Play(UI.UiSound.Back);
        if (_updateDownloading && !b._updateDownloading) UI.UiSfx.Play(UI.UiSound.Wait);
        if (_updateError != null && !b.HasUpdateError) UI.UiSfx.Play(UI.UiSound.Error);
    }

    private void UpdateCore(float dt, KeyboardState kb, int screenWidth, int screenHeight, int windowWidth, int windowHeight)
    {
        // Use window dimensions if provided, otherwise use screen dimensions
        if (windowWidth == 0) windowWidth = screenWidth;
        if (windowHeight == 0) windowHeight = screenHeight;
        
        // Animate panel
        float target = _panelOpen ? 0f : 1f;
        _panelX = MathHelper.Lerp(_panelX, target, Math.Min(1f, dt * 14f));

        // Animate import confirmation modal
        float modalTarget = _importModalOpen ? 1f : 0f;
        _importModalAnim = MathHelper.Lerp(_importModalAnim, modalTarget, Math.Min(1f, dt * 16f));

        // Update toast notification timer
        if (_toastTimer > 0f)
        {
            _toastTimer -= dt;
            if (_toastTimer <= 0f)
            {
                _toastTimer = 0f;
                _toastMessage = null;
            }
        }

        // Animate drop zone pulse (for when list is empty)
        if (_dropZonePulseDirection)
        {
            _dropZonePulse += dt * 2f;
            if (_dropZonePulse >= 1f)
            {
                _dropZonePulse = 1f;
                _dropZonePulseDirection = false;
            }
        }
        else
        {
            _dropZonePulse -= dt * 2f;
            if (_dropZonePulse <= 0f)
            {
                _dropZonePulse = 0f;
                _dropZonePulseDirection = true;
            }
        }

        // Fork: browse timers (selection smoothing, background tint, deferred toasts, confirm animation)
        UpdateBrowseTimers(dt);
        UpdateOnlineSource(); // online song select: follow the room's lists (FreeplayMenu.Online.cs)

        // Store screen dimensions for mouse coordinate transformation
        _lastScreenWidth = screenWidth;
        _lastScreenHeight = screenHeight;
        _lastWindowWidth = windowWidth;
        _lastWindowHeight = windowHeight;
        
        var mouse = Mouse.GetState();
        Point rtMouse = TransformMouseToRenderTarget(mouse.Position, windowWidth, windowHeight, screenWidth, screenHeight);
        bool mouseClicked = mouse.LeftButton == ButtonState.Pressed && 
                           _prevMs.LeftButton == ButtonState.Released;
        _pointerDown = mouse.LeftButton == ButtonState.Pressed;

        #if ANDROID
        var touches = Microsoft.Xna.Framework.Input.Touch.TouchPanel.GetState();
        bool touchDown = false;
        bool hasAnyTouch = touches.Count > 0;
        foreach (var t in touches)
        {
            if (t.State == Microsoft.Xna.Framework.Input.Touch.TouchLocationState.Pressed ||
                t.State == Microsoft.Xna.Framework.Input.Touch.TouchLocationState.Moved)
            {
                Point touchPt = new Point((int)t.Position.X, (int)t.Position.Y);
                if (IsPointOverOverlayControl?.Invoke(touchPt) == true)
                    continue;

                touchDown = true;
                rtMouse = TransformMouseToRenderTarget(touchPt, windowWidth, windowHeight, screenWidth, screenHeight);
                break;
            }
        }
        // Fork: a touch is a tap only when released without dragging; dragging scrolls the list
        // (it used to select rows as the finger moved).
        mouseClicked = false;
        bool touchLongPress = false;
        if (touchDown && !_prevTouchDown)
        {
            _touchStart = rtMouse;
            _touchLast = rtMouse;
            _touchDragging = false;
            _touchHeld = 0f;
            _touchLongPressed = false;
        }
        else if (touchDown)
        {
            if (!_touchDragging && Math.Abs(rtMouse.Y - _touchStart.Y) > UiScale.Px(14, UiScale.FromScreen(screenWidth, screenHeight)))
                _touchDragging = true;
            if (!_touchDragging && !_touchLongPressed && (_touchHeld += dt) >= UI.PointerInput.LongPressSeconds)
            {
                _touchLongPressed = true;
                touchLongPress = true;
            }
            if (_touchDragging && _panelOpen && _attemptsView == null) // the attempts row drags itself
            {
                _manualScroll = true;
                _targetScrollY += _touchLast.Y - rtMouse.Y;
                _scrollY = _targetScrollY; // follow the finger
            }
            _touchLast = rtMouse;
        }
        else if (_prevTouchDown)
        {
            rtMouse = _touchLast;
            mouseClicked = !_touchDragging && !_touchLongPressed;
        }
        _prevTouchDown = touchDown;
        _pointerDown = touchDown;
        if (touchLongPress)
            mouse = new MouseState(mouse.X, mouse.Y, mouse.ScrollWheelValue, ButtonState.Released,
                ButtonState.Released, ButtonState.Pressed, ButtonState.Released, ButtonState.Released);
        #endif

        if (!Game1.CanAcceptInput)
        {
            _hoveredLevelIndex = -1;
            _hoveredMoreIndex = -1;
            _hoveredMainCard = -1;
            _newBtnHover = false;
            _closePanelBtnHover = false;
            _searchBoxHover = _sortChipHover = _favChipHover = _randomBtnHover = false;
            _modBarHover = _bestCardHover = false;
            _folderIconHover = false;
            _importBtnHover  = false;
            _refreshBtnHover = false;
            _footerBarHover  = false;
            _modalYesHover   = false;
            _modalNoHover    = false;
            _syncPointerNextUpdate = true;
            _prevKb = default;
            _prevMs = Game1.NeutralMouseState(mouse);
            return;
        }

        if (_syncPointerNextUpdate)
        {
            _syncPointerNextUpdate = false;
            mouseClicked = false;
            _prevMs = mouse;
            _lastMenuMousePos = rtMouse;
            #if ANDROID
            _prevTouchDown = touchDown;
            #endif
        }
        _lastPointerPos = rtMouse;
        _hoveredLevelIndex = -1;
        _hoveredMoreIndex = -1;
        _hoveredMainCard = -1;
        _newBtnHover = false;
        _closePanelBtnHover = false;

        // Fork: while the chosen level flashes (or an online pick is being sent), ignore input.
        if (_confirming || _online?.Busy == true)
        {
            _prevKb = kb;
            _prevMs = mouse;
            return;
        }

        // ── Import Confirmation Modal ("¿Quieres jugarlo ahora? [SÍ] / [NO]") ──
        if (_importModalOpen)
        {
            _folderIconHover = false;
            _importBtnHover  = false;
            _refreshBtnHover = false;
            _footerBarHover  = false;

            _modalYesHover = _modalYesBounds != Rectangle.Empty && _modalYesBounds.Contains(rtMouse);
            _modalNoHover  = _modalNoBounds  != Rectangle.Empty && _modalNoBounds.Contains(rtMouse);

            if (_modalYesHover) _importModalSelection = 0;
            if (_modalNoHover)  _importModalSelection = 1;

            if (_inputCooldown > 0f)
            {
                _inputCooldown -= dt;
                _prevKb = kb;
                _prevMs = mouse;
                return;
            }

            if (mouseClicked)
            {
                if (_modalYesHover)
                {
                    _importModalOpen = false;
                    if (_modalIsDelete)
                        PerformDelete();
                    else
                    {
                        LevelPath   = _importModalLevelPath;
                        LevelChosen = true;
                    }
                    _prevKb = kb;
                    _prevMs = mouse;
                    return;
                }
                if (_modalNoHover)
                {
                    _importModalOpen = false;
                    _prevKb = kb;
                    _prevMs = mouse;
                    return;
                }
            }

            bool mLeft  = (kb.IsKeyDown(Keys.Left)  && !_prevKb.IsKeyDown(Keys.Left))  || (kb.IsKeyDown(Keys.Up)   && !_prevKb.IsKeyDown(Keys.Up));
            bool mRight = (kb.IsKeyDown(Keys.Right) && !_prevKb.IsKeyDown(Keys.Right)) || (kb.IsKeyDown(Keys.Down) && !_prevKb.IsKeyDown(Keys.Down));
            bool mEnter = (kb.IsKeyDown(Keys.Enter) && !_prevKb.IsKeyDown(Keys.Enter)) || (kb.IsKeyDown(Keys.Space) && !_prevKb.IsKeyDown(Keys.Space));
            bool mEsc   = kb.IsKeyDown(Keys.Escape) && !_prevKb.IsKeyDown(Keys.Escape);

            if (mLeft || mRight)
            {
                _importModalSelection = 1 - _importModalSelection;
            }
            else if (mEnter && _importModalSelection == 0)
            {
                _importModalOpen = false;
                if (_modalIsDelete)
                    PerformDelete();
                else
                {
                    LevelPath   = _importModalLevelPath;
                    LevelChosen = true;
                }
            }
            else if (mEsc || (mEnter && _importModalSelection == 1))
            {
                _importModalOpen = false;
            }

            _prevKb = kb;
            _prevMs = mouse;
            return;
        }
        else
        {
            _modalYesHover = false;
            _modalNoHover  = false;
        }
        
        bool mouseMoved = Math.Abs(rtMouse.X - _lastMenuMousePos.X) > 2 || Math.Abs(rtMouse.Y - _lastMenuMousePos.Y) > 2;
        if (mouseMoved) _lastMenuMousePos = rtMouse;
        #if ANDROID
        if (_touchDragging) mouseMoved = false; // a drag scrolls, it doesn't pick rows
        #endif
        int wheelDelta = mouse.ScrollWheelValue - _prevMs.ScrollWheelValue;

        // Hover states of the panel's controls, rows and the details panel (fork: FreeplayMenu.Browse.cs)
        if (_panelOpen && !_creditsOpen)
        {
            UpdatePanelHover(rtMouse);
        }
        else
        {
            _folderIconHover    = false;
            _importBtnHover     = false;
            _newBtnHover        = false;
            _refreshBtnHover    = false;
            _closePanelBtnHover = false;
            _footerBarHover     = false;
            _searchBoxHover = _sortChipHover = _favChipHover = _randomBtnHover = false;
            _modBarHover = _bestCardHover = false;
        }

        // Input cooldown — ignore all inputs briefly after opening
        if (_inputCooldown > 0f)
        {
            _inputCooldown -= dt;
            _prevKb = kb;
            _prevMs = mouse;
            return;
        }

        // Handle transition animations (ESC cancels fade cleanly instead of locking input)
        if (_transition != TransitionState.None)
        {
            if (kb.IsKeyDown(Keys.Escape) && !_prevKb.IsKeyDown(Keys.Escape))
            {
                _transition = TransitionState.None;
                _transTimer = 0f;
                _creditsOpen = false;
                _creditsAlpha = 0f;
                _prevKb = kb;
                _prevMs = mouse;
                return;
            }

            _transTimer += dt;
            if (_transition == TransitionState.CreditsIn)
                _creditsAlpha = Math.Min(1f, _transTimer / TRANS_DURATION);

            if (_transTimer >= TRANS_DURATION)
            {
                if (_transition == TransitionState.CreditsIn)
                {
                    _creditsOpen = true;
                    _creditsAlpha = 1f;
                }
                _transition = TransitionState.None;
                _transTimer = 0f;
            }
            _prevKb = kb;
            _prevMs = mouse;
            return;
        }

        bool up     = kb.IsKeyDown(Keys.Up)    && !_prevKb.IsKeyDown(Keys.Up);
        bool down   = kb.IsKeyDown(Keys.Down)  && !_prevKb.IsKeyDown(Keys.Down);
        bool enter  = kb.IsKeyDown(Keys.Enter) && !_prevKb.IsKeyDown(Keys.Enter);
        bool esc    = kb.IsKeyDown(Keys.Escape) && !_prevKb.IsKeyDown(Keys.Escape);
        bool seven  = Controls.Pressed(HotkeyAction.OpenChartEditor, kb, _prevKb);

        // Check for "7" key press to open Editor Picker (ONLY in main menu, NOT in panel)
        if (!_inPanel && seven)
        {
            EditorPickerRequested = true;
            Console.WriteLine("[FreeplayMenu] Editor Picker requested (7 key pressed)");
        }

        // Handle credits links & close with ESC, Enter, or click outside links
        if (_creditsOpen)
        {
            bool clickedLink = false;
            if (mouseClicked)
            {
                foreach (var link in _creditLinks)
                {
                    if (link.Bounds.Contains(rtMouse) && !string.IsNullOrEmpty(link.Url))
                    {
                        clickedLink = true;
                        try
                        {
                            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
                            {
                                FileName = link.Url,
                                UseShellExecute = true
                            });
                            Console.WriteLine($"[Credits] Opening URL: {link.Url}");
                        }
                        catch (Exception ex)
                        {
                            Console.WriteLine($"[Credits] Failed to open URL: {ex.Message}");
                        }
                        break;
                    }
                }
            }

            if (esc || enter || (mouseClicked && !clickedLink))
            {
                _creditsOpen  = false;
                _creditsAlpha = 0f;
            }
            
            _prevKb = kb;
            _prevMs = mouse;
            return;
        }

        // ── Psych Engine-style Update Modal (non-blocking, main menu only) ────
        if (_updateAvailable && _updateModalOpen && !_panelOpen && !_importModalOpen)
        {
            bool nowHover   = _updateNowBounds   != Rectangle.Empty && _updateNowBounds.Contains(rtMouse);
            bool laterHover = _updateLaterBounds != Rectangle.Empty && _updateLaterBounds.Contains(rtMouse);
            if (!_updateDownloading)
            {
                if (mouseMoved && nowHover)   _updateModalSel = 0;
                if (mouseMoved && laterHover) _updateModalSel = 1;

                if ((kb.IsKeyDown(Keys.Left) && !_prevKb.IsKeyDown(Keys.Left)) ||
                    (kb.IsKeyDown(Keys.Right) && !_prevKb.IsKeyDown(Keys.Right)) ||
                    (kb.IsKeyDown(Keys.Up) && !_prevKb.IsKeyDown(Keys.Up)) ||
                    (kb.IsKeyDown(Keys.Down) && !_prevKb.IsKeyDown(Keys.Down)))
                {
                    _updateModalSel = 1 - _updateModalSel;
                }

                if ((mouseClicked && laterHover) || esc)
                {
                    _updateModalOpen = false;
                    _updateDismissed = true;
                }
                else if ((mouseClicked && nowHover) || enter)
                {
                    if (_updateModalSel == 0)
                        BeginInMenuUpdate();
                    else
                    {
                        _updateModalOpen = false;
                        _updateDismissed = true;
                    }
                }
            }

            _prevKb = kb;
            _prevMs = mouse;
            return;
        }

        // The corner badge re-opens the update prompt when dismissed
        if (_updateAvailable && !_panelOpen)
        {
            _updateBadgeHover = _updateBadgeBounds != Rectangle.Empty && _updateBadgeBounds.Contains(rtMouse);
            if (_updateBadgeHover && mouseClicked)
            {
                _updateModalOpen = true;
                _updateDismissed = false;
                _updateModalSel  = 0;
                _prevKb = kb;
                _prevMs = mouse;
                return;
            }
        }
        else
        {
            _updateBadgeHover = false;
        }

        if (_panelOpen)
        {
            // Fork: all level panel input lives in FreeplayMenu.Browse.cs
            UpdatePanelInput(dt, kb, mouse, rtMouse, mouseClicked, mouseMoved, wheelDelta, screenWidth, screenHeight);
        }
        else
        {
            #if ANDROID
            const int menuCount = 9;
            #else
            const int menuCount = 6;
            #endif

            if (up)
            {
                // Skip disabled items
                do {
                    _menuSel = (_menuSel - 1 + menuCount) % menuCount;
                } while (!IsMenuItemEnabledInternal(_menuSel));
            }
            if (down)
            {
                // Skip disabled items
                do {
                    _menuSel = (_menuSel + 1) % menuCount;
                } while (!IsMenuItemEnabledInternal(_menuSel));
            }

            if (esc)
            {
                SettingsRequested = true;
            }

            // Mouse hover & click on main menu cards
            int clickedCard = -1;
            for (int i = 0; i < _mainMenuCardBounds.Count && i < menuCount; i++)
            {
                if (!IsMenuItemEnabledInternal(i)) continue;
                if (_mainMenuCardBounds[i].Contains(rtMouse))
                {
                    if (mouseMoved) _menuSel = i;
                    if (mouseClicked) clickedCard = i;
                    break;
                }
            }

            if (clickedCard >= 0)
            {
                _menuSel = clickedCard;
                ExecuteMainMenuSelection(_menuSel);
            }
            else if (enter && IsMenuItemEnabledInternal(_menuSel))
            {
                ExecuteMainMenuSelection(_menuSel);
            }
        }

        _prevKb = kb;
        _prevMs = mouse;
    }

    private void ExecuteMainMenuSelection(int sel)
    {
        if (sel == 0)  // Freeplay
        {
            _panelOpen = true;
            _inPanel   = true;
        }
        else if (sel == 1)  // Multiplayer
        {
            MenuAction = 3;  // Signal to open multiplayer menu
        }
        else if (sel == 2)  // Replays (fork)
        {
            MenuAction = 7;  // Signal to open the replay browser
        }
        else if (sel == 3)  // Editors Hub (visible on main menu!)
        {
            EditorPickerRequested = true;
        }
        else if (sel == 4)  // Settings
        {
            SettingsRequested = true;
        }
        #if ANDROID
        else if (sel == 5)  // OPEN MODS (Android only)
        {
            MenuAction = 5;  // Signal to open mods folder
        }
        else if (sel == 6)  // IMPORT FILE (Android only)
        {
            MenuAction = 4;  // Signal to open file picker for mods
        }
        else if (sel == 7)  // GRANT FOLDER ACCESS (Android only)
        {
            MenuAction = 6;  // Signal to open SAF folder picker
        }
        else if (sel == 8)  // Credits — fade in
        {
            _transition   = TransitionState.CreditsIn;
            _transTimer   = 0f;
            _creditsAlpha = 0f;
        }
        #else
        else if (sel == 5)  // Credits — fade in
        {
            _transition   = TransitionState.CreditsIn;
            _transTimer   = 0f;
            _creditsAlpha = 0f;
        }
        #endif
    }

    /// <summary>Consumes and returns the current main menu selection action, or -1 if none.</summary>
    public int ConsumeMenuAction()
    {
        int action = MenuAction;
        MenuAction = -1;
        return action;
    }

    public void Draw(SpriteBatch sb, int sw, int sh, int windowWidth = 0, int windowHeight = 0)
    {
        if (sw <= 0 || sh <= 0)
            return;

        // Capture GraphicsDevice for scissor rectangle
        _graphicsDevice = sb.GraphicsDevice;
        
        // Use window dimensions if provided, otherwise use render target dimensions
        if (windowWidth == 0) windowWidth = sw;
        if (windowHeight == 0) windowHeight = sh;
        
        // Store for mouse transformation
        _lastScreenWidth = sw;
        _lastScreenHeight = sh;
        _lastWindowWidth = windowWidth;
        _lastWindowHeight = windowHeight;
        float s = UiScale.FromScreen(sw, sh);
        int itemH = GetItemHeight(sw, sh);

        TrimIconTextures();
        float beat = _panelOpen ? BeatBump() : 0f;

        // ── Background: tinted by the selected level's difficulty (fork) ─────
        DrawRect(sb, 0, 0, sw, sh, _bgTint);
        // Soft glow rising from the bottom, brighter on the preview's beat
        int glowBands = 12;
        int glowH = (int)(sh * 0.45f);
        for (int g = 0; g < glowBands; g++)
        {
            float k = (g + 1) / (float)glowBands;
            int bandY = sh - (int)(glowH * k);
            DrawRect(sb, 0, bandY, sw, glowH / glowBands + 1, Color.Lerp(_bgTint, Color.White, 0.05f) * ((1f - k) * (0.35f + beat * 0.25f)));
        }

        // Subtle pattern overlay
        int bgGridStep = UiScale.Px(120, s, 64);
        float gridAlpha = 0.02f + beat * 0.03f;
        for (int y = 0; y < sh; y += bgGridStep)
        {
            DrawRect(sb, 0, y, sw, 1, Color.White * gridAlpha);
        }
        for (int x = 0; x < sw; x += bgGridStep)
        {
            DrawRect(sb, x, 0, 1, sh, Color.White * gridAlpha);
        }

        // ── Left area — title + menu ──────────────────────────────────────────
        int panelW = GetPanelWidth(sw, sh);
        int leftW = sw - (int)((1f - _panelX) * panelW);
        // Fork: while the level panel is open, the left side shows the selected level's details.
        bool showDetails = _panelX < 0.5f;
        if (showDetails) _mainMenuCardBounds.Clear();

        if ((_font != null || _fontRenderer != null) && !showDetails)
        {
            // Menu items — play modes & Editors clearly accessible on main menu
            #if ANDROID
            string[] items = { "FREEPLAY", "MULTIPLAYER", "REPLAYS", "EDITORS", "SETTINGS", "OPEN MODS", "IMPORT FILE", "GRANT FOLDER ACCESS", "CREDITS" };
            #else
            string[] items = { "FREEPLAY", "MULTIPLAYER", "REPLAYS", "EDITORS", "SETTINGS", "CREDITS" };
            #endif

            // Title with glow effect - scaled cleanly across 720p .. 4K
            int maxLeftTitleW = Math.Max(140, leftW - UiScale.Px(36, s));
            int titleSize = FitFontSize("FUNKY MAKER", UiScale.FontPx(48, s), 20, maxLeftTitleW);
            string title = "FUNKY MAKER";
            var titleSz = MeasureText(title, titleSize);
            float titleX = leftW / 2f - titleSz.X / 2f;
            #if ANDROID
            float titleY = sh * 0.07f;
            #else
            float titleY = sh * 0.11f;
            #endif
            
            int glowOff = UiScale.Px(2, s, 1);
            Color glowColor = new Color(255, 68, 136) * 0.12f;
            DrawText(sb, title, new Vector2(titleX + glowOff, titleY + glowOff), glowColor, titleSize);
            DrawText(sb, title, new Vector2(titleX, titleY), new Color(255, 215, 0), titleSize);
            
            // "PLAYER" subtitle
            string player = "PLAYER";
            int playerSize = FitFontSize(player, UiScale.FontPx(36, s), 18, maxLeftTitleW);
            var playerSz = MeasureText(player, playerSize);
            float playerY = titleY + titleSz.Y - sh * 0.007f;
            DrawText(sb, player, new Vector2(leftW / 2f - playerSz.X / 2f, playerY),
                new Color(255, 68, 136), playerSize);

            // Decorative line under title - responsive to leftW & uiScale
            int lineY = (int)(playerY + playerSz.Y + UiScale.Px(14, s));
            int lineW = Math.Clamp((int)(leftW * 0.45f), UiScale.Px(140, s), UiScale.Px(380, s));
            int lineThickness = UiScale.Px(3, s, 2);
            DrawRect(sb, leftW / 2 - lineW / 2, lineY, lineW, lineThickness, new Color(255, 68, 136) * 0.6f);
            int dotSize = UiScale.Px(8, s, 5);
            int dotOffset = UiScale.Px(15, s, 10);
            DrawRect(sb, leftW / 2 - lineW / 2 - dotOffset, lineY, dotSize, lineThickness, new Color(255, 215, 0));
            DrawRect(sb, leftW / 2 + lineW / 2 + dotOffset - dotSize, lineY, dotSize, lineThickness, new Color(255, 215, 0));

            // Subtitle - fitted so it never overflows into right panel
            string sub = UI.PointerInput.IsTouch ? "Fan-made player for Funky Maker Mobile" : "Fan-made PC player for Funky Maker Mobile";
            int subSize = FitFontSize(sub, UiScale.FontPx(18, s), 11, maxLeftTitleW);
            var subSz = MeasureText(sub, subSize);
            float subY = lineY + UiScale.Px(16, s);
            DrawText(sb, sub, new Vector2(leftW / 2f - subSz.X / 2f, subY),
                Color.White * 0.42f, subSize);

            // Bottom hint position
            #if ANDROID
            string mainMenuHint = "Tap an option  |  Back gesture: return";
            #else
            string mainMenuHint = $"UP/DOWN Navigate  |  ENTER / Click Select  |  [{Controls.Hint(HotkeyAction.OpenChartEditor)}] Editors  |  ESC Settings";
            #endif
            string hint = _inPanel 
                ? "< ESC Back" 
                : mainMenuHint;
            int hintSize = FitFontSize(hint, UiScale.FontPx(17, s), 11, leftW - UiScale.Px(24, s));
            var hintSz = MeasureText(hint, hintSize);
            float hintY = sh - UiScale.Px(38, s);

            // Card dimensions scaled smoothly via uiScale
            int minCardW = Math.Min(UiScale.Px(260, s), Math.Max(140, leftW - UiScale.Px(32, s)));
            #if ANDROID
            int menuSize = UiScale.FontPx(24, s);
            int cardWidth = Math.Clamp((int)(leftW * 0.64f), Math.Min(UiScale.Px(250, s), minCardW), UiScale.Px(540, s));
            int cardHeight = UiScale.Px(46, s, 38);
            int cardSpacing = UiScale.Px(10, s, 6);
            #else
            int menuSize = UiScale.FontPx(28, s);
            int cardWidth = Math.Clamp((int)(leftW * 0.60f), minCardW, UiScale.Px(540, s));
            int cardHeight = UiScale.Px(56, s, 46);
            int cardSpacing = UiScale.Px(16, s, 10);
            #endif

            float topBoundary = subY + subSz.Y + UiScale.Px(20, s);
            float bottomBoundary = hintY - UiScale.Px(18, s);
            float availableCardH = Math.Max(100f, bottomBoundary - topBoundary);
            int totalMenuH = items.Length * cardHeight + (items.Length - 1) * cardSpacing;
            if (totalMenuH > availableCardH && items.Length > 0)
            {
                float ratio = availableCardH / totalMenuH;
                cardSpacing = Math.Max(4, (int)(cardSpacing * ratio));
                cardHeight = Math.Max(28, (int)((availableCardH - (items.Length - 1) * cardSpacing) / items.Length));
                menuSize = Math.Clamp((int)(cardHeight * 0.56f), 14, menuSize);
                totalMenuH = items.Length * cardHeight + (items.Length - 1) * cardSpacing;
            }
            int startCardY = (int)Math.Max(topBoundary, topBoundary + (availableCardH - totalMenuH) / 2f);

            _mainMenuCardBounds.Clear();
            for (int i = 0; i < items.Length; i++)
            {
                bool sel = !_inPanel && i == _menuSel;
                bool cardHovered = _inPanel && i == _hoveredMainCard;
                bool enabled = IsMenuItemEnabledInternal(i);
                Color textCol = enabled 
                    ? ((sel || cardHovered) ? Color.White : new Color(200, 200, 220))
                    : new Color(100, 60, 80);
                
                int itemSize = sel ? menuSize + UiScale.Px(2, s, 1) : menuSize;
                var sz = MeasureText(items[i], itemSize);
                
                int cardX = leftW / 2 - cardWidth / 2;
                int cardY = startCardY + i * (cardHeight + cardSpacing);
                _mainMenuCardBounds.Add(new Rectangle(cardX, cardY, cardWidth, cardHeight));
                
                // Card background
                if (enabled)
                {
                    if (sel)
                    {
                        Color cardTop = new Color(255, 68, 136) * 0.28f;
                        Color cardBottom = new Color(255, 68, 136) * 0.16f;
                        int glowSize = UiScale.Px(3, s, 2);
                        DrawRect(sb, cardX - glowSize, cardY - glowSize, cardWidth + glowSize * 2, cardHeight + glowSize * 2, 
                            new Color(255, 68, 136) * 0.4f);
                        DrawRect(sb, cardX, cardY, cardWidth, cardHeight, new Color(30, 24, 42) * 0.9f);

                        for (int g = 0; g < 10; g++)
                        {
                            float gt = g / 10f;
                            int bandTop = cardY + (g * cardHeight) / 10;
                            int bandBottom = cardY + ((g + 1) * cardHeight) / 10;
                            DrawRect(sb, cardX, bandTop, cardWidth, Math.Max(1, bandBottom - bandTop),
                                Color.Lerp(cardTop, cardBottom, gt));
                        }
                        
                        DrawRect(sb, cardX, cardY, cardWidth, 1, new Color(255, 215, 0) * 0.45f);
                        DrawRect(sb, cardX, cardY + cardHeight - 1, cardWidth, 1, new Color(255, 68, 136) * 0.6f);
                        int accentWidth = Math.Max(3, (int)(cardWidth * 0.015f));
                        DrawRect(sb, cardX, cardY, accentWidth, cardHeight, new Color(255, 215, 0));
                    }
                    else
                    {
                        DrawRect(sb, cardX, cardY, cardWidth, cardHeight,
                            cardHovered ? new Color(55, 42, 75) * 0.72f : new Color(35, 35, 55) * 0.45f);
                        DrawRect(sb, cardX, cardY, cardWidth, 1,
                            cardHovered ? new Color(255, 68, 136) * 0.5f : Color.White * 0.1f);
                        DrawRect(sb, cardX, cardY + cardHeight - 1, cardWidth, 1,
                            cardHovered ? new Color(255, 215, 0) * 0.4f : Color.White * 0.05f);
                        if (cardHovered)
                            DrawRect(sb, cardX, cardY, UiScale.Px(3, s, 2), cardHeight, new Color(255, 68, 136) * 0.8f);
                    }
                }
                else
                {
                    DrawRect(sb, cardX, cardY, cardWidth, cardHeight, new Color(25, 15, 20) * 0.5f);
                    int strikeMargin = (int)(cardWidth * 0.10f);
                    int strikeThickness = UiScale.Px(2, s, 2);
                    DrawRect(sb, cardX + strikeMargin, cardY + cardHeight / 2, cardWidth - strikeMargin * 2, strikeThickness, new Color(255, 0, 0) * 0.3f);
                }
                
                float textX = cardX + (cardWidth - sz.X) / 2f;
                float textY = cardY + (cardHeight - sz.Y) / 2f;
                DrawText(sb, items[i], new Vector2(textX, textY), textCol, itemSize);
            }

            DrawText(sb, hint, new Vector2(leftW / 2f - hintSz.X / 2f, hintY),
                Color.White * 0.35f, hintSize);
        }

        if (!showDetails && _panelX < 1f)
        {
            // Main menu fading out as the panel slides in
            DrawRect(sb, 0, 0, leftW, sh, _bgTint * Math.Clamp((1f - _panelX) * 2f, 0f, 1f));
        }
        else if (showDetails && (_font != null || _fontRenderer != null))
        {
            float detailsAlpha = Math.Clamp((0.5f - _panelX) * 2f, 0f, 1f);
            DrawLevelDetails(sb, leftW, sh, s, detailsAlpha);

            string panelHint = UI.PointerInput.IsTouch && !_searchTyping
                ? (_online != null
                    ? "Tap a song, tap it again to pick  |  \"...\" Options  |  Drag to scroll  |  Back: close"
                    : "Tap a song, tap it again to play  |  \"...\" or hold: Options  |  Drag to scroll  |  Back: close")
                : _searchTyping
                ? "Type to search  |  UP/DOWN Results  |  ENTER / ESC Done  |  Ctrl+Backspace Clear"
                : _online != null
                    ? "UP/DOWN Select  |  ENTER Pick  |  " + (OsuTab ? "LEFT/RIGHT Difficulty  |  " : "") +
                      "\"...\" or right click: Options  |  ESC Back"
                : "UP/DOWN Select  |  ENTER Play  |  " + (OsuTab ? "LEFT/RIGHT Difficulty" : "LEFT/RIGHT Song Speed") +
                  "  |  \"...\" or right click: Options  |  ESC Back";
            int phSize = FitFontSize(panelHint, UiScale.FontPx(14, s), 9, leftW - UiScale.Px(24, s));
            var phSz = MeasureText(panelHint, phSize);
            DrawText(sb, panelHint, new Vector2(leftW / 2f - phSz.X / 2f, sh - UiScale.Px(30, s)),
                Color.White * (0.38f * detailsAlpha), phSize);
        }

        // ── Right panel — level list (FreeplayMenu.LevelPanel.cs) ────────────
        DrawLevelPanel(sb, sw, sh, s, itemH);
        DrawContextMenu(sb, sw, sh, s);

        // ── Toast Notification Banner (e.g. "[Nombre del nivel] importado") ──
        if (_toastTimer > 0f && !string.IsNullOrEmpty(_toastMessage) && (_font != null || _fontRenderer != null))
        {
            float fadeIn  = Math.Clamp((TOAST_DURATION - _toastTimer) / 0.18f, 0f, 1f);
            float fadeOut = Math.Clamp(_toastTimer / 0.45f, 0f, 1f);
            float alpha   = Math.Min(fadeIn, fadeOut);

            int toastFontSize = UiScale.FontPx(18, s);
            string safeToast = Safe(_toastMessage);
            var textSz = MeasureText(safeToast, toastFontSize);

            int padX = UiScale.Px(22, s);
            int padY = UiScale.Px(12, s);
            int boxW = Math.Min((int)textSz.X + padX * 2 + UiScale.Px(8, s), Math.Max(100, sw - UiScale.Px(32, s)));
            int boxH = (int)textSz.Y + padY * 2;

            int panelMaxW = GetPanelWidth(sw, sh);
            int panelLeftPos = (int)(sw - panelMaxW * (1f - _panelX));
            int anchorCenterX = _panelOpen ? (panelLeftPos + (sw - panelLeftPos) / 2) : (sw / 2);
            int marginX = UiScale.Px(16, s);
            int boxX = Math.Clamp(anchorCenterX - boxW / 2, marginX, Math.Max(marginX, sw - boxW - marginX));
            int boxY = (int)(UiScale.Px(92, s) - (1f - fadeIn) * UiScale.Px(12, s));

            DrawRect(sb, boxX - 2, boxY - 2, boxW + 4, boxH + 4, new Color(0, 200, 255) * (0.35f * alpha));
            DrawRect(sb, boxX, boxY, boxW, boxH, new Color(20, 24, 38) * (0.96f * alpha));
            DrawRect(sb, boxX, boxY, UiScale.Px(4, s, 3), boxH, new Color(80, 240, 150) * alpha);
            DrawRect(sb, boxX, boxY, boxW, 1, new Color(255, 215, 0) * (0.5f * alpha));
            DrawRect(sb, boxX, boxY + boxH - 1, boxW, 1, new Color(0, 200, 255) * (0.5f * alpha));

            DrawText(sb, safeToast,
                new Vector2(boxX + padX + UiScale.Px(4, s), boxY + (boxH - textSz.Y) / 2f),
                Color.White * alpha, toastFontSize);
        }

        // ── Import Confirmation Modal ("¿Quieres jugarlo ahora? [SÍ] / [NO]") ──
        if ((_importModalOpen || _importModalAnim > 0.01f) && (_font != null || _fontRenderer != null))
        {
            float mAlpha = Math.Clamp(_importModalAnim, 0f, 1f);

            DrawRect(sb, 0, 0, sw, sh, new Color(8, 8, 16) * (0.76f * mAlpha));

            int modalW = Math.Clamp((int)(sw * 0.44f), UiScale.Px(420, s), UiScale.Px(680, s));
            int modalH = Math.Clamp((int)(sh * 0.36f), UiScale.Px(240, s), UiScale.Px(380, s));
            int modalX = (sw - modalW) / 2;
            int modalY = (sh - modalH) / 2 - (int)((1f - mAlpha) * UiScale.Px(14, s));

            int framePad = UiScale.Px(3, s, 2);
            DrawRect(sb, modalX - framePad, modalY - framePad, modalW + framePad * 2, modalH + framePad * 2, new Color(255, 68, 136) * (0.4f * mAlpha));
            DrawRect(sb, modalX, modalY, modalW, modalH, new Color(24, 22, 38) * (0.98f * mAlpha));
            DrawRect(sb, modalX, modalY, modalW, UiScale.Px(4, s, 2), new Color(255, 215, 0) * mAlpha);
            DrawRect(sb, modalX, modalY + modalH - 2, modalW, 2, new Color(255, 68, 136) * (0.7f * mAlpha));
            DrawRect(sb, modalX, modalY, 1, modalH, Color.White * (0.12f * mAlpha));
            DrawRect(sb, modalX + modalW - 1, modalY, 1, modalH, Color.White * (0.12f * mAlpha));

            string headerText = _modalIsDelete ? "BORRAR NIVEL"
                : _importModalCount > 1
                ? $"{_importModalCount} NIVELES IMPORTADOS"
                : "NIVEL IMPORTADO";
            int headerSize = UiScale.FontPx(18, s);
            var headerSz = MeasureText(headerText, headerSize);
            int topOff = UiScale.Px(20, s);
            DrawText(sb, headerText,
                new Vector2(modalX + (modalW - headerSz.X) / 2f, modalY + topOff),
                new Color(255, 215, 0) * mAlpha, headerSize);

            int nameSize = UiScale.FontPx(26, s);
            string displayLevel = TruncateText(Safe(_modalIsDelete ? _importModalLevelName : $"{_importModalLevelName} importado"), nameSize, modalW - UiScale.Px(48, s));
            var nameSz = MeasureText(displayLevel, nameSize);
            DrawText(sb, displayLevel,
                new Vector2(modalX + (modalW - nameSz.X) / 2f, modalY + topOff + headerSz.Y + UiScale.Px(10, s)),
                (_modalIsDelete ? new Color(255, 120, 135) : new Color(80, 240, 160)) * mAlpha, nameSize);

            string questionText = !_modalIsDelete ? "Quieres jugarlo ahora?"
                : OperatingSystem.IsWindows() ? "Seguro? Ira a la Papelera de reciclaje."
                : "Seguro? No se puede deshacer.";
            int questionSize = UiScale.FontPx(20, s);
            var questionSz = MeasureText(questionText, questionSize);
            float questionY = modalY + topOff + headerSz.Y + UiScale.Px(10, s) + nameSz.Y + UiScale.Px(12, s);
            DrawText(sb, questionText,
                new Vector2(modalX + (modalW - questionSz.X) / 2f, questionY),
                Color.White * (0.9f * mAlpha), questionSize);

            int btnGap = UiScale.Px(20, s);
            int sidePad = UiScale.Px(32, s);
            int btnW   = (modalW - sidePad * 2 - btnGap) / 2;
            int btnH   = UiScale.Px(48, s, 40);
            int btnY   = modalY + modalH - btnH - UiScale.Px(44, s);
            int yesX   = modalX + sidePad;
            int noX    = yesX + btnW + btnGap;

            _modalYesBounds = new Rectangle(yesX, btnY, btnW, btnH);
            _modalNoBounds  = new Rectangle(noX, btnY, btnW, btnH);

            int btnTextSize = UiScale.FontPx(19, s);

            // Draw "SI, JUGAR" button
            bool yesSel = _importModalSelection == 0;
            if (yesSel)
            {
                DrawRect(sb, yesX - 2, btnY - 2, btnW + 4, btnH + 4, new Color(255, 68, 136) * (0.45f * mAlpha));
                DrawRect(sb, yesX, btnY, btnW, btnH, new Color(36, 24, 48) * mAlpha);
                Color yesTop = new Color(255, 68, 136) * (0.38f * mAlpha);
                Color yesBot = new Color(255, 68, 136) * (0.20f * mAlpha);
                for (int g = 0; g < 8; g++)
                {
                    int bTop = btnY + (g * btnH) / 8;
                    int bBot = btnY + ((g + 1) * btnH) / 8;
                    DrawRect(sb, yesX, bTop, btnW, Math.Max(1, bBot - bTop), Color.Lerp(yesTop, yesBot, g / 8f));
                }
                DrawRect(sb, yesX, btnY, 4, btnH, new Color(255, 215, 0) * mAlpha);
                DrawRect(sb, yesX, btnY, btnW, 1, new Color(255, 215, 0) * (0.7f * mAlpha));
                DrawRect(sb, yesX, btnY + btnH - 1, btnW, 1, new Color(255, 68, 136) * (0.8f * mAlpha));
            }
            else
            {
                DrawRect(sb, yesX, btnY, btnW, btnH, new Color(35, 35, 55) * (0.65f * mAlpha));
                DrawRect(sb, yesX, btnY, btnW, 1, Color.White * (0.15f * mAlpha));
            }
            string yesLabel = _modalIsDelete ? "SI, BORRAR" : "SI, JUGAR";
            var yesSz = MeasureText(yesLabel, btnTextSize);
            DrawText(sb, yesLabel,
                new Vector2(yesX + (btnW - yesSz.X) / 2f, btnY + (btnH - yesSz.Y) / 2f),
                (yesSel ? Color.White : new Color(190, 190, 210)) * mAlpha, btnTextSize);

            // Draw "NO, EN LISTA" button
            bool noSel = _importModalSelection == 1;
            if (noSel)
            {
                DrawRect(sb, noX - 2, btnY - 2, btnW + 4, btnH + 4, new Color(0, 200, 255) * (0.45f * mAlpha));
                DrawRect(sb, noX, btnY, btnW, btnH, new Color(22, 34, 48) * mAlpha);
                Color noTop = new Color(0, 190, 255) * (0.32f * mAlpha);
                Color noBot = new Color(0, 190, 255) * (0.16f * mAlpha);
                for (int g = 0; g < 8; g++)
                {
                    int bTop = btnY + (g * btnH) / 8;
                    int bBot = btnY + ((g + 1) * btnH) / 8;
                    DrawRect(sb, noX, bTop, btnW, Math.Max(1, bBot - bTop), Color.Lerp(noTop, noBot, g / 8f));
                }
                DrawRect(sb, noX, btnY, 4, btnH, new Color(0, 220, 255) * mAlpha);
                DrawRect(sb, noX, btnY, btnW, 1, new Color(0, 220, 255) * (0.7f * mAlpha));
                DrawRect(sb, noX, btnY + btnH - 1, btnW, 1, new Color(0, 180, 255) * (0.8f * mAlpha));
            }
            else
            {
                DrawRect(sb, noX, btnY, btnW, btnH, new Color(35, 35, 55) * (0.65f * mAlpha));
                DrawRect(sb, noX, btnY, btnW, 1, Color.White * (0.15f * mAlpha));
            }
            string noLabel = _modalIsDelete ? "NO, CANCELAR" : "NO, SOLO ANADIR";
            var noSz = MeasureText(noLabel, btnTextSize);
            DrawText(sb, noLabel,
                new Vector2(noX + (btnW - noSz.X) / 2f, btnY + (btnH - noSz.Y) / 2f),
                (noSel ? Color.White : new Color(190, 190, 210)) * mAlpha, btnTextSize);

            // Bottom hint inside modal
            #if ANDROID
            string modalHint = "< / > Seleccionar  |  [A] Confirmar  |  [B] No";
            #else
            string modalHint = "LEFT/RIGHT Seleccionar  |  ENTER Confirmar  |  ESC No";
            #endif
            int mHintSize = UiScale.FontPx(14, s);
            var mHintSz = MeasureText(modalHint, mHintSize);
            DrawText(sb, modalHint,
                new Vector2(modalX + (modalW - mHintSz.X) / 2f, modalY + modalH - UiScale.Px(26, s)),
                Color.White * (0.45f * mAlpha), mHintSize);
        }
        else
        {
            _modalYesBounds = Rectangle.Empty;
            _modalNoBounds  = Rectangle.Empty;
        }

        // ── Credits overlay (fade in) ─────────────────────────────────────────
        float credAlpha = _creditsAlpha;
        if (_transition == TransitionState.CreditsIn)
            credAlpha = Math.Min(1f, _transTimer / TRANS_DURATION);

        if (credAlpha > 0.01f && (_font != null || _fontRenderer != null))
        {
            DrawRect(sb, 0, 0, sw, sh, new Color(10, 10, 18) * credAlpha);
            
            // Clear previous frame's links
            _creditLinks.Clear();
            
            // Credit lines with URLs
            var creditData = new (string text, string? url)[] {
                ("Funky Maker Player", null),
                ("", null),
                ("Main Developer", null),
                ("Just Fenix (@thisisankush)", "https://github.com/Thisisfenix"),
                ("", null),
                ("Contributor", null),
                ("AbelitoGamer (@abelitogamer)", "https://github.com/AbelitoGamer"),
                ("", null),
                ("Based on Funky Maker, by KinGamesCreator", null),
                ("Android - Google Play", "https://play.google.com/store/apps/details?id=com.kingamescreator.fnmm"),
                ("iOS - App Store", "https://apps.apple.com/app/funky-maker-mobile/id6761866418"),
                ("", null),
                ("Online menu music: Portraits of Kings & Jazzroom (Duck Game OST) - superjoebob", null),
                ("UI sounds: Mii Maker / Tomodachi Life (Nintendo)", null),
                ("UI Icons by Icons8 - icons8.com", "https://icons8.com"),
                ("Star rating based on osu!mania's difficulty calculator (ppy/osu, MIT)", "https://github.com/ppy/osu"),
                ("", null),
                ("Built with", null),
                ("  MonoGame  MonoGame.Extended  FontStashSharp  NVorbis  NLayer", null),
                ("  ImageSharp  SharpCompress  DiscordRichPresence", null),
                ("", null),
                ("Special Thanks", null),
                ("  AnnaHolOl (Ideas & Testing)", null),
                ("", null),
                ("[Enter] or [ESC] to close", null),
            };
            
            int creditSize = UiScale.FontPx(23, s);
            // Shrink to fit short / narrow windows (the list is long).
            float widest = 0f;
            foreach (var (t, _) in creditData) widest = Math.Max(widest, MeasureText(t, creditSize).X);
            while (creditSize > 10 &&
                   ((MeasureText("A", creditSize).Y + UiScale.Px(6, s)) * creditData.Length > sh * 0.92f || widest > sw * 0.94f))
            {
                creditSize--;
                widest = 0f;
                foreach (var (t, _) in creditData) widest = Math.Max(widest, MeasureText(t, creditSize).X);
            }
            int ch = (int)(MeasureText("A", creditSize).Y) + UiScale.Px(6, s);
            int totalCH = creditData.Length * ch;
            int cy = sh / 2 - totalCH / 2;
            
            Point pointerPos = _lastPointerPos;
            
            foreach (var (text, url) in creditData)
            {
                if (text.Length == 0) { cy += ch; continue; }
                
                var sz = MeasureText(text, creditSize);
                float textX = sw / 2f - sz.X / 2f;
                
                Color col;
                bool isTitle = text == "Funky Maker Player";
                bool isIndented = text.StartsWith("  ");
                bool isLink = url != null;
                
                // Create bounds for clickable links (matching screen draw coordinates)
                Rectangle bounds = new Rectangle((int)textX, cy, (int)sz.X, (int)sz.Y);
                bool isHovered = isLink && bounds.Contains(pointerPos);
                
                if (isLink)
                {
                    _creditLinks.Add(new ClickableLink 
                    { 
                        Bounds = bounds, 
                        Url = url ?? string.Empty,
                        IsHovered = isHovered
                    });
                }
                
                // Color logic
                if (isTitle)
                    col = new Color(255, 215, 0) * credAlpha;  // Gold
                else if (isLink && isHovered)
                    col = new Color(255, 215, 0) * credAlpha;  // Gold on hover
                else if (isLink)
                    col = new Color(0, 180, 255) * credAlpha;  // Cyan for links
                else if (isIndented)
                    col = Color.White * 0.5f * credAlpha;
                else
                    col = Color.White * 0.85f * credAlpha;
                
                // Underline for links
                if (isLink)
                {
                    int underlineY = cy + (int)sz.Y + 2;
                    DrawRect(sb, (int)textX, underlineY, (int)sz.X, 1, col * 0.6f);
                }
                
                DrawText(sb, text, new Vector2(textX, cy), col, creditSize);
                cy += ch;
            }
        }
        else
        {
            _creditLinks.Clear();
        }

        // ── Update Available Badge & Psych Engine-style Modal ─────────────────
        if (_updateAvailable && !_panelOpen && !_creditsOpen && (_font != null || _fontRenderer != null))
        {
            if (_updateModalOpen)
            {
                _updateBadgeBounds = Rectangle.Empty;
                DrawUpdateModal(sb, sw, sh);
            }
            else
            {
                _updateNowBounds   = Rectangle.Empty;
                _updateLaterBounds = Rectangle.Empty;
                DrawUpdateCornerBadge(sb, sw, sh);
            }
        }
        else
        {
            _updateNowBounds   = Rectangle.Empty;
            _updateLaterBounds = Rectangle.Empty;
            _updateBadgeBounds = Rectangle.Empty;
        }
    }

    private void DrawUpdateCornerBadge(SpriteBatch sb, int sw, int sh)
    {
        float s = UiScale.FromScreen(sw, sh);
        string text = $"UPDATE {_updateTag} AVAILABLE";
        int fontSize = UiScale.FontPx(15, s);
        var sz = MeasureText(text, fontSize);
        int padX = UiScale.Px(14, s);
        int padY = UiScale.Px(8, s);
        int w = (int)sz.X + padX * 2;
        int h = (int)sz.Y + padY * 2;
        int x = sw - w - UiScale.Px(20, s);
        int y = UiScale.Px(18, s);
        _updateBadgeBounds = new Rectangle(x, y, w, h);

        Color accent = new Color(80, 230, 145);
        DrawRect(sb, x, y, w, h, _updateBadgeHover ? new Color(34, 52, 44) * 0.95f : new Color(22, 30, 36) * 0.88f);
        DrawRect(sb, x, y, UiScale.Px(4, s, 3), h, accent);
        DrawRect(sb, x, y, w, 1, accent * (_updateBadgeHover ? 0.9f : 0.45f));
        DrawRect(sb, x, y + h - 1, w, 1, accent * (_updateBadgeHover ? 0.9f : 0.45f));
        DrawText(sb, text, new Vector2(x + padX, y + padY), _updateBadgeHover ? Color.White : accent, fontSize);
    }

    private void DrawUpdateModal(SpriteBatch sb, int sw, int sh)
    {
        float s = UiScale.FromScreen(sw, sh);
        DrawRect(sb, 0, 0, sw, sh, new Color(8, 8, 16) * 0.78f);

        int modalW = Math.Clamp((int)(sw * 0.50f), UiScale.Px(460, s), UiScale.Px(760, s));
        int modalH = Math.Clamp((int)(sh * 0.50f), UiScale.Px(320, s), UiScale.Px(500, s));
        int modalX = (sw - modalW) / 2;
        int modalY = (sh - modalH) / 2;

        Color accentGold  = new Color(255, 215, 0);
        Color accentGreen = new Color(80, 240, 150);
        Color accentPink  = new Color(255, 68, 136);

        int glowPad = UiScale.Px(3, s, 2);
        DrawRect(sb, modalX - glowPad, modalY - glowPad, modalW + glowPad * 2, modalH + glowPad * 2, accentGreen * 0.35f);
        DrawRect(sb, modalX, modalY, modalW, modalH, new Color(22, 22, 38) * 0.98f);
        DrawRect(sb, modalX, modalY, modalW, UiScale.Px(4, s, 2), accentGreen);
        DrawRect(sb, modalX, modalY + modalH - 2, modalW, 2, accentPink * 0.7f);

        int pad = UiScale.Px(26, s);
        string header = "NEW UPDATE AVAILABLE!";
        int headerSz = UiScale.FontPx(25, s);
        var hMeasure = MeasureText(header, headerSz);
        DrawText(sb, header, new Vector2(modalX + (modalW - hMeasure.X) / 2f, modalY + UiScale.Px(20, s)), accentGold, headerSz);

        string localVer = Updater.GetLocalVersionString();
        string verLine  = Safe($"v{localVer}  ->  {_updateTag}");
        int verSz       = UiScale.FontPx(20, s);
        var vMeasure    = MeasureText(verLine, verSz);
        DrawText(sb, verLine, new Vector2(modalX + (modalW - vMeasure.X) / 2f, modalY + UiScale.Px(28, s) + hMeasure.Y), accentGreen, verSz);

        int notesY = (int)(modalY + UiScale.Px(38, s) + hMeasure.Y + vMeasure.Y);
        int notesH = modalH - (notesY - modalY) - UiScale.Px(118, s);
        int notesW = modalW - pad * 2;
        DrawRect(sb, modalX + pad, notesY, notesW, notesH, new Color(14, 14, 24) * 0.88f);
        DrawRect(sb, modalX + pad, notesY, notesW, 1, Color.White * 0.12f);

        int noteFontSz = UiScale.FontPx(15, s);
        string rawNotes = string.IsNullOrWhiteSpace(_updateChangelog)
            ? "Improvements, bug fixes, and multiplayer stability updates."
            : _updateChangelog;
        string[] rawLines = rawNotes.Replace("\r", "").Split('\n');
        int lineY = notesY + UiScale.Px(10, s);
        int lineStep = (int)MeasureText("A", noteFontSz).Y + UiScale.Px(4, s);
        foreach (string rLine in rawLines)
        {
            string trimmed = Safe(rLine.TrimStart('#', ' ', '*', '-').Trim());
            if (string.IsNullOrEmpty(trimmed)) continue;
            if (lineY + lineStep > notesY + notesH - UiScale.Px(8, s)) break;
            DrawText(sb, TruncateText($"* {trimmed}", noteFontSz, notesW - UiScale.Px(20, s)),
                new Vector2(modalX + pad + UiScale.Px(10, s), lineY), new Color(210, 215, 235), noteFontSz);
            lineY += lineStep;
        }

        if (_updateDownloading)
        {
            _updateNowBounds   = Rectangle.Empty;
            _updateLaterBounds = Rectangle.Empty;

            int barW = modalW - pad * 2;
            int barH = UiScale.Px(24, s);
            int barX = modalX + pad;
            int barY = modalY + modalH - UiScale.Px(78, s);

            DrawRect(sb, barX, barY, barW, barH, Color.Black * 0.65f);
            int fillW = (int)(barW * Math.Clamp(_updateProgress, 0f, 1f));
            if (fillW > 0)
                DrawRect(sb, barX, barY, fillW, barH, accentGreen);

            string st = Safe(string.IsNullOrEmpty(_updateStatus) ? "Downloading..." : _updateStatus);
            int stSz = UiScale.FontPx(14, s);
            var stMeasure = MeasureText(st, stSz);
            DrawText(sb, TruncateText(st, stSz, barW),
                new Vector2(modalX + (modalW - Math.Min(stMeasure.X, barW)) / 2f, barY + barH + UiScale.Px(8, s)),
                Color.White * 0.9f, stSz);
            return;
        }

        if (!string.IsNullOrEmpty(_updateError))
        {
            int errSz = UiScale.FontPx(14, s);
            DrawText(sb, TruncateText(Safe($"Error: {_updateError}"), errSz, modalW - pad * 2),
                new Vector2(modalX + pad, modalY + modalH - UiScale.Px(96, s)), new Color(255, 110, 110), errSz);
        }

        int btnGap = UiScale.Px(22, s);
        int btnW = (modalW - pad * 2 - btnGap) / 2;
        int btnH = UiScale.Px(46, s, 40);
        int btnY = modalY + modalH - btnH - UiScale.Px(26, s);
        int nowX = modalX + pad;
        int laterX = nowX + btnW + btnGap;
        _updateNowBounds   = new Rectangle(nowX, btnY, btnW, btnH);
        _updateLaterBounds = new Rectangle(laterX, btnY, btnW, btnH);

        int btnFontSz = UiScale.FontPx(18, s);
        DrawModalChoiceButton(sb, _updateNowBounds, _updateModalSel == 0,
            string.IsNullOrEmpty(_updateError) ? "UPDATE NOW (ENTER)" : "RETRY UPDATE", accentGreen, btnFontSz);
        DrawModalChoiceButton(sb, _updateLaterBounds, _updateModalSel == 1,
            "LATER (ESC)", accentPink, btnFontSz);
    }

    private void DrawModalChoiceButton(SpriteBatch sb, Rectangle bounds, bool selected, string label, Color accent, int fontSize)
    {
        if (selected)
        {
            DrawRect(sb, bounds.X - 2, bounds.Y - 2, bounds.Width + 4, bounds.Height + 4, accent * 0.45f);
            DrawRect(sb, bounds.X, bounds.Y, bounds.Width, bounds.Height, accent * 0.25f);
            DrawRect(sb, bounds.X, bounds.Y, 4, bounds.Height, accent);
            DrawRect(sb, bounds.X, bounds.Y, bounds.Width, 1, accent * 0.8f);
        }
        else
        {
            DrawRect(sb, bounds.X, bounds.Y, bounds.Width, bounds.Height, new Color(34, 34, 52) * 0.7f);
            DrawRect(sb, bounds.X, bounds.Y, bounds.Width, 1, Color.White * 0.14f);
        }
        var lSz = MeasureText(label, fontSize);
        DrawText(sb, label,
            new Vector2(bounds.X + (bounds.Width - lSz.X) / 2f, bounds.Y + (bounds.Height - lSz.Y) / 2f),
            selected ? Color.White : new Color(185, 190, 210), fontSize);
    }

    // ── Helpers ──────────────────────────────────────────────────────────────
    private void DrawRect(SpriteBatch sb, int x, int y, int w, int h, Color c)
    {
        if (w <= 0 || h <= 0) return;
        sb.Draw(_pixel, new Rectangle(x, y, w, h), c);
    }
    
    /// <summary>
    /// Draw text using FontStashSharp if available, fallback to SpriteFont.
    /// </summary>
    private void DrawText(SpriteBatch sb, string text, Vector2 position, Color color, int fontSize)
    {
        if (_fontRenderer != null)
        {
            _fontRenderer.DrawString(sb, text, position, color, fontSize);
        }
        else if (_font != null)
        {
            // Fallback to bitmap font with scale
            float scale = fontSize / 32f;  // Assuming base font is ~32px
            sb.DrawUiString(_font, text, position, color, 0f, Vector2.Zero, scale, SpriteEffects.None, 0f);
        }
    }
    
    /// <summary>
    /// Measure text using FontStashSharp if available, fallback to SpriteFont.
    /// </summary>
    private Vector2 MeasureText(string text, int fontSize)
    {
        if (_fontRenderer != null)
        {
            return _fontRenderer.MeasureString(text, fontSize);
        }
        else if (_font != null)
        {
            float scale = fontSize / 32f;
            return _font.MeasureUi(text) * scale;
        }
        return Vector2.Zero;
    }

    private string Safe(string s)
    {
        if (string.IsNullOrEmpty(s) || _font == null) return s ?? "";
        var sb = new System.Text.StringBuilder(s.Length);
        foreach (char c in s)
            sb.Append(c >= 32 && c < 127 ? c : '?');
        return sb.ToString();
    }

    public static string GetDefaultLevelsFolder()
    {
        return PlatformPaths.GetLevelsPath();
    }
    
    // ── Platform-specific menu item state ────────────────────────────────────
    /// <summary>
    /// Check if a menu item is enabled. Defaults to enabled on all platforms.
    /// Blazor override disables HUD Editor and Multiplayer.
    /// </summary>
    private bool IsMenuItemEnabledInternal(int index)
    {
        #if BLAZORGL
        return IsMenuItemEnabled(index);
        #else
        return true;  // All items enabled on desktop and Android
        #endif
    }
    
    /// <summary>
    /// Get color for menu item. Defaults to standard colors on all platforms.
    /// Blazor override shows disabled items in dark red.
    /// </summary>
    private Color GetMenuItemColorInternal(int index, bool isSelected)
    {
        #if BLAZORGL
        return GetMenuItemColor(index, isSelected);
        #else
        // Desktop: standard colors
        return isSelected ? new Color(0xFF, 0x44, 0x88) : Color.White * 0.55f;
        #endif
    }
    
    /// <summary>
    /// Get main text for empty state (no levels found).
    /// </summary>
    private string GetEmptyStateMainTextInternal()
    {
        #if BLAZORGL
        return GetEmptyStateMainText();
        #else
        #if ANDROID
        return "TAP TO IMPORT"; // nothing can be dragged onto the app on Android
        #else
        return OsuTab ? "DROP .OSZ" : "DRAG & DROP";
        #endif
        #endif
    }
    
    /// <summary>
    /// Get secondary text for empty state.
    /// </summary>
    private string GetEmptyStateSecondaryTextInternal()
    {
        #if BLAZORGL
        return GetEmptyStateSecondaryText();
        #else
        return OsuTab ? "osu!mania 4K beatmap sets" : ".FNMM / .ZIP / .RAR";
        #endif
    }
    
    /// <summary>
    /// Get help text for empty state.
    /// </summary>
    private string GetEmptyStateHelpTextInternal()
    {
        #if BLAZORGL
        return GetEmptyStateHelpText();
        #else
        #if ANDROID
        return "Tap here to pick a file";
        #else
        return "Click here to browse files";
        #endif
        #endif
    }
    
    /// <summary>
    /// Get bottom panel hint text.
    /// </summary>
    private string GetPanelBottomHintTextInternal()
    {
        #if BLAZORGL
        return GetPanelBottomHintText();
        #else
        if (_online != null)
            return _tab == FreeplayTab.Official
                ? "Official levels are downloaded from the server by everybody"
                : "Songs somebody lacks are sent to them when picked";
        #if ANDROID
        return OsuTab ? "Use IMPORT to add .OSZ songs / .OSK skins" : "Use IMPORT to add .FNMM / .ZIP / .RAR levels";
        #else
        return OsuTab
            ? "Drop .OSZ songs / .OSK skins here, or use IMPORT"
            : "Drop .FNMM / .ZIP / .RAR levels here, or use IMPORT";
        #endif
        #endif
    }
    
    private int FitFontSize(string text, int preferredSize, int minSize, float maxWidth)
    {
        int size = Math.Max(minSize, preferredSize);
        while (size > minSize && MeasureText(text, size).X > maxWidth)
            size--;
        return size;
    }

    private void DrawPanelHeaderButton(SpriteBatch sb, Rectangle bounds, string label, bool hovered, Color accent, int fontSize)
    {
        Color bg = hovered ? Color.Lerp(new Color(32, 34, 52), accent, 0.28f) : new Color(26, 26, 44);
        DrawRect(sb, bounds.X, bounds.Y, bounds.Width, bounds.Height, bg);
        DrawRect(sb, bounds.X, bounds.Y, bounds.Width, hovered ? 2 : 1, accent * (hovered ? 0.95f : 0.65f));
        DrawRect(sb, bounds.X, bounds.Bottom - 1, bounds.Width, 1, accent * (hovered ? 0.6f : 0.25f));
        DrawRect(sb, bounds.X, bounds.Y, 2, bounds.Height, accent * (hovered ? 1f : 0.7f));
        var sz = MeasureText(label, fontSize);
        DrawText(sb, label,
            new Vector2(bounds.X + (bounds.Width - sz.X) / 2f, bounds.Y + (bounds.Height - sz.Y) / 2f),
            hovered ? Color.White : accent, fontSize);
    }

    /// <summary>
    /// Get the bounds of the empty-state drop box for click detection.
    /// </summary>
    private Rectangle GetDropBoxBounds(int screenWidth, int screenHeight)
    {
        float s = UiScale.FromScreen(screenWidth, screenHeight);
        int panelW = GetPanelWidth(screenWidth, screenHeight);
        int panelLeft = (int)(screenWidth - panelW * (1f - _panelX));
        int visibleW = Math.Max(0, screenWidth - panelLeft);

        int padX = Math.Clamp((int)(panelW * 0.08f), UiScale.Px(22, s), UiScale.Px(52, s));
        int targetW = Math.Clamp(panelW - padX * 2, UiScale.Px(220, s), UiScale.Px(620, s));
        int dropBoxW = Math.Min(targetW, Math.Max(0, visibleW - UiScale.Px(24, s)));

        int areaTop = PanelHeaderHeight(s) + UiScale.Px(6, s);
        int footerReserve = UiScale.Px(55, s);
        int areaBottom = Math.Max(areaTop + 120, screenHeight - footerReserve);
        int availH = areaBottom - areaTop;
        int dropBoxH = Math.Clamp((int)(availH * 0.72f), UiScale.Px(230, s), Math.Max(UiScale.Px(230, s), availH - UiScale.Px(28, s)));

        int dropBoxX = panelLeft + (visibleW - dropBoxW) / 2;
        int dropBoxY = areaTop + Math.Max(UiScale.Px(8, s), (availH - dropBoxH) / 2);

        return new Rectangle(dropBoxX, dropBoxY, dropBoxW, dropBoxH);
    }
    
    /// <summary>
    /// Transforms pointer coordinates from window space to menu draw space.
    /// Returns raw window coordinates when rendering directly at window size.
    /// </summary>
    private Point TransformMouseToRenderTarget(Point mousePos, int windowWidth, int windowHeight, int rtWidth, int rtHeight)
    {
        if (windowWidth <= 0 || windowHeight <= 0 || rtWidth <= 0 || rtHeight <= 0)
            return mousePos;

        if (windowWidth == rtWidth && windowHeight == rtHeight)
            return mousePos;

        // Calculate how a render target would be letterboxed in the window
        float windowAspect = windowWidth / (float)windowHeight;
        float rtAspect = rtWidth / (float)rtHeight;
        
        int dstW, dstH, offsetX, offsetY;
        
        if (windowAspect > rtAspect)
        {
            // Window is wider - letterbox on sides
            dstH = windowHeight;
            dstW = (int)(rtAspect * dstH);
            offsetX = (windowWidth - dstW) / 2;
            offsetY = 0;
        }
        else
        {
            // Window is taller - letterbox on top/bottom
            dstW = windowWidth;
            dstH = (int)(dstW / rtAspect);
            offsetX = 0;
            offsetY = (windowHeight - dstH) / 2;
        }

        if (dstW <= 0 || dstH <= 0)
            return mousePos;
        
        // Transform mouse from window space to render target space
        int rtX = (int)((mousePos.X - offsetX) * rtWidth / (float)dstW);
        int rtY = (int)((mousePos.Y - offsetY) * rtHeight / (float)dstH);
        
        return new Point(rtX, rtY);
    }

    /// <summary>Asks to delete the level at <paramref name="index"/> (fork).</summary>
    private void OpenDeleteModal(int index)
    {
        if (index < 0 || index >= _paths.Count) return;
        // osu! songs: the whole set folder (every difficulty) goes (fork)
        _deleteModalPath      = OsuSetOf(_paths[index])?.Folder ?? _paths[index];
        _importModalLevelName = _names[index];
        _importModalCount     = 1;
        _modalIsDelete        = true;
        _importModalSelection = 1; // default to "NO"
        _importModalAnim      = 0f;
        _importModalOpen      = true;
        ResetInputCooldown();
    }

    private void PerformDelete()
    {
        string path = _deleteModalPath;
        string name = _importModalLevelName;
        _modalIsDelete = false;
        _deleteModalPath = "";
        if (string.IsNullOrEmpty(path)) return;

        var outcome = LevelFiles.Delete(path, out string error);
        if (outcome == LevelFiles.DeleteOutcome.Failed)
        {
            ShowToast($"No se pudo borrar {name}: {error}");
            return;
        }
        FreeplayData.Instance.Forget(path);
        string where = outcome switch
        {
            LevelFiles.DeleteOutcome.RecycleBin => "enviado a la Papelera",
            LevelFiles.DeleteOutcome.Trash => "enviado a la Papelera",
            _ => "borrado",
        };
        int keep = _selected;
        Refresh();
        _selected = Math.Clamp(keep, 0, Math.Max(0, _paths.Count - 1));
        LevelsDeleted = true;
        ShowToast($"{name} {where}");
    }

    /// <summary>Set after a level was deleted here; Game1 refreshes the other level lists and clears it.</summary>
    public bool LevelsDeleted { get; set; }

    /// <summary>
    /// Opens the platform-appropriate file picker to import level archives (.fnmm, .zip, .rar).
    /// </summary>
    public void TriggerFileImport()
    {
        #if BLAZORGL
        OnFilePickerRequested?.Invoke();
        #elif ANDROID
        OpenAndroidFilePicker();
        #else
        OpenFolderBrowser();
        #endif
    }

    /// <summary>
    /// Opens native desktop file browser to select .FNMM, .ZIP, or .RAR level files.
    /// </summary>
    private void OpenFolderBrowser()
    {
        #if !BLAZORGL && !ANDROID
        try
        {
            var selectedFiles = PlatformPaths.PickDesktopFiles(
                OsuTab
                    ? "osu! files (*.osz;*.osk)\0*.osz;*.OSZ;*.osk;*.OSK\0All Files (*.*)\0*.*\0\0"
                    : "Funky Maker Levels (*.fnmm;*.zip;*.rar)\0*.FNMM;*.fnmm;*.ZIP;*.zip;*.RAR;*.rar\0osu! files (*.osz;*.osk)\0*.osz;*.osk\0All Files (*.*)\0*.*\0\0",
                OsuTab ? "Select osu!mania beatmap sets (.OSZ) or skins (.OSK)" : "Select Funky Maker Level (.FNMM, .ZIP, .RAR)",
                allowMultiSelect: true);

            var imported = new List<string>();
            Directory.CreateDirectory(_levelsFolder);
            foreach (var file in selectedFiles)
            {
                if (!File.Exists(file)) continue;
                // osu! beatmap sets / skins (fork)
                if (Osu.OsuLibrary.IsBeatmapArchive(file) || Osu.OsuLibrary.IsSkinArchive(file))
                {
                    var r = Osu.OsuLibrary.Import(file);
                    if (r.Ok && !r.IsSkin && r.Path != null) OnOsuSetImported(r.Path, r.Message);
                    else ShowToast(r.Message);
                    continue;
                }
                string destPath = Path.Combine(_levelsFolder, Path.GetFileName(file));
                if (!string.Equals(Path.GetFullPath(file), Path.GetFullPath(destPath), StringComparison.OrdinalIgnoreCase))
                {
                    File.Copy(file, destPath, overwrite: true);
                }
                imported.Add(destPath);
            }

            if (imported.Count > 0)
            {
                OnLevelsImported(imported);
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[FreeplayMenu] Error opening file dialog: {ex.Message}");
        }
        finally
        {
            ResetInputCooldown();
        }
        #endif
    }

#if ANDROID
    /// <summary>
    /// Opens Android file picker to select .FNMM, .ZIP, or .RAR files.
    /// </summary>
    private async void OpenAndroidFilePicker()
    {
        try
        {
            var filePath = await PlatformPaths.OpenFilePickerAsync();
            
            if (!string.IsNullOrEmpty(filePath))
            {
                Console.WriteLine($"[FreeplayMenu] File selected: {filePath}");
                OnLevelsImported(new[] { filePath });
            }
            else
            {
                Console.WriteLine("[FreeplayMenu] File picker cancelled");
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[FreeplayMenu] Error opening Android file picker: {ex.Message}");
        }
    }
#endif

    // ── Text truncation helper ──────────────────────────────────────────────
    private string TruncateText(string text, int fontSize, float maxWidth)
    {
        if (string.IsNullOrEmpty(text)) return text;
        
        var measured = MeasureText(text, fontSize);
        if (measured.X <= maxWidth) return text;
        
        // Binary search for the right length
        int left = 1;
        int right = text.Length;
        string truncated = text;
        
        while (left < right)
        {
            int mid = (left + right + 1) / 2;
            string candidate = text.Substring(0, mid) + "...";
            var candidateSz = MeasureText(candidate, fontSize);
            
            if (candidateSz.X <= maxWidth)
            {
                truncated = candidate;
                left = mid;
            }
            else
            {
                right = mid - 1;
            }
        }
        
        return truncated;
    }
}
