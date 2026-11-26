using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using Microsoft.Xna.Framework.Input;
using Microsoft.Xna.Framework.Input.Touch;
using UiSfx = FunkyMakerPlayer.UI.UiSfx;
using UiSound = FunkyMakerPlayer.UI.UiSound;
using FunkyMakerPlayer.Multiplayer.Rooms;

namespace FunkyMakerPlayer.Multiplayer
{
    /// <summary>
    /// Online menu: connection, room browser, room settings, waiting room, song select, settings, leaderboard.
    /// </summary>
    public partial class MultiplayerMenu
    {
        // Menu states (fork: room system)
        private enum MenuState
        {
            MainMenu,      // Connect / Leaderboard / Settings / Back
            Connecting,    // Connecting to server...
            RoomBrowser,   // List of rooms + search + create
            RoomSettings,  // Create a room / edit (host) / view its settings
            WaitingRoom,   // Players, rounds, chat
            SongSelect,    // Pick the round's song (OFFICIAL / LOCAL)
            Settings,      // Settings menu
            Leaderboard    // Leaderboard and stats
        }

        // ══════════════════════════════════════════════════════════════════════
        // Public Properties for Game1 Integration
        // ══════════════════════════════════════════════════════════════════════

        /// <summary>True while the "Connecting to server" screen is showing (Game1 switches menu music on it).</summary>
        public bool IsConnectingScreen => _state == MenuState.Connecting;

        /// <summary>True while a text field is being edited (Game1 skips the -/+ volume keys then).</summary>
        public bool IsEditingText => _isEditingText || TextFieldActive || PickerTyping;

        public bool GameplaySettingsRequested { get; private set; }
        public void ConsumeGameplaySettingsRequest() => GameplaySettingsRequested = false;

        /// <summary>
        /// Get the connection indicator instance for use during gameplay.
        /// </summary>
        public UI.ConnectionIndicator GetConnectionIndicator()
        {
            return _connectionIndicator;
        }

        private MenuState _state = MenuState.MainMenu;
        private int _selection = 0;
        private KeyboardState _prevKb;
        private MouseState _prevMouse;
        private float _inputCooldown = 0f;
        private const float INPUT_COOLDOWN_TIME = 0.15f;
        
#if ANDROID
        private TouchControls? _touchControls;
#endif
        
        // Mouse/Touch support
        private List<Rectangle> _clickableAreas = new();
        private List<Rectangle> _leftArrowAreas = new(); // Areas for left arrows (decrease value)
        private List<Rectangle> _rightArrowAreas = new(); // Areas for right arrows (increase value)
        private List<Rectangle> _tabHitboxes = new();
        private Rectangle _backButtonRect = Rectangle.Empty;
        private Rectangle _refreshButtonRect = Rectangle.Empty;
        private Rectangle _gameplaySettingsRect = Rectangle.Empty;
        private Rectangle _shareButtonRect = Rectangle.Empty;
        private int _hoveredButton = -1;
        private Point _lastMousePos = Point.Zero;
        private bool _mouseMovedThisFrame = false;

        // Connection
        private string _username = "Player";
        
        // Settings menu
        private int _settingsCategory = 0; // 0-3 (User, Gameplay, Performance, Visual)
        private int _settingsSelection = 0;
        private int _settingsScrollOffset = 0;
        private bool _isSettingFocused = false;
        private const int MAX_SETTINGS_VISIBLE = 6;
        /// <summary>Editing the username (fork: the shared text field, so Android gets the system keyboard).</summary>
        private bool _isEditingText => IsTextField("username");
        private string _editingTextValue => _textFieldValue;
        private float _backspaceHoldTimer = 0f;
        private float _backspaceRepeatTimer = 0f;
        
        // Leaderboard menu
        private int _leaderboardTab = 0; // 0=Top Players, 1=Recent Matches, 2=My Stats
        private int _leaderboardSelection = 0;
        private int _leaderboardScrollOffset = 0;
        private const int MAX_LEADERBOARD_VISIBLE = 7;
        private bool _leaderboardSyncedOnline = false;
        private volatile bool _leaderboardSyncInFlight = false;
        private static readonly HttpClient _leaderboardHttpClient = new() { Timeout = TimeSpan.FromSeconds(10) };
        private static readonly JsonSerializerOptions _leaderboardJsonOptions = new() { PropertyNameCaseInsensitive = true };
        private HttpLeaderboardPayload? _pendingHttpLeaderboard;

        private sealed class HttpLeaderboardPayload
        {
            public List<LeaderboardEntryPacket>? Entries { get; set; }
            public List<MatchHistoryPacket>? Matches { get; set; }
        }

        // Song select (fork: was the matchmaking lobby)
        private List<string> _availableLevels = new();
        private int _levelSelection = 0;
        private int _levelScrollOffset = 0;
        private LevelSource _selectedSource = LevelSource.Official;
        private List<OfficialLevelInfo> _officialLevels => RoomClient.Instance.OfficialLevels;
        // Same folder as Freeplay, looked up each time (it moves with Settings → Move Mods Folder).
        private string _levelsFolder => FreeplayMenu.GetDefaultLevelsFolder();
        private Game1? _game = null; // Reference to main game instance
        private float _searchTimer = 0f;

        // Server info
        private string _serverVersion = "Unknown";
        private int _serverLevelCount = 0;

        // Visual & Animations
        private float _pulseTimer = 0f;
        private const float PULSE_SPEED = 2f;
        private float _fadeAlpha = 0f;
        private float _stateTransitionTimer = 0f;
        private const float TRANSITION_DURATION = 0.3f;
        private float _errorMessageTimer = 0f;
        private string _errorMessage = "";
        private float _infoMessageTimer = 0f;
        private string _infoMessage = "";
        private float _connectionRetryTimer = 0f;
        private int _connectionAttempts = 0;
        private const int MAX_CONNECTION_ATTEMPTS = 5;
        private int _pendingConnectedSignal = 0;
        private string? _pendingDisconnectReason = null;
        private string? _pendingConnectionError = null;
        
        // Connection indicator, icons & typography
        private UI.ConnectionIndicator _connectionIndicator;
        private FontRenderer? _fontRenderer;
        private Dictionary<string, Texture2D> _menuIcons = new();
        
        // Color palette (harmonized dark studio slate + modern multiplayer accents)
        private static readonly Color BG_DARK = new Color(14, 14, 24);
        private static readonly Color BG_MEDIUM = new Color(22, 22, 36);
        private static readonly Color BG_LIGHT = new Color(34, 34, 54);
        private static readonly Color BORDER_SUBTLE = new Color(56, 58, 84);
        private static readonly Color ACCENT_PRIMARY = new Color(80, 205, 255);    // Electric cyan
        private static readonly Color ACCENT_SECONDARY = new Color(255, 175, 75);  // Warm amber
        private static readonly Color ACCENT_SUCCESS = new Color(75, 225, 130);    // Emerald
        private static readonly Color ACCENT_WARNING = new Color(255, 210, 80);    // Gold
        private static readonly Color ACCENT_ERROR = new Color(255, 95, 115);      // Coral red
        private static readonly Color TEXT_PRIMARY = Color.White;
        private static readonly Color TEXT_SECONDARY = new Color(185, 190, 212);
        private static readonly Color TEXT_DISABLED = new Color(115, 120, 145);
        
        // Exit flag
        private bool _wantsExit = false;

        public MultiplayerMenu()
        {
            // Levels folder: same as Freeplay (fork: was AppContext.BaseDirectory/mods/levels, which is a
            // different folder on Android and in single-file desktop builds). See _levelsFolder.
            
            // Load settings
            var settings = MultiplayerSettings.Instance;
            _username = settings.Username;
            
            // Load available levels from freeplay
            RefreshLevelList();
            
            // Initialize previous keyboard state
            _prevKb = LayoutKeyboard.GetState();
            _prevMouse = Mouse.GetState();
            
            // Initialize connection indicator with settings
            _connectionIndicator = new UI.ConnectionIndicator
            {
                Mode = settings.InterfaceMode == MultiplayerSettings.UIMode.Compact 
                    ? UI.ConnectionIndicator.DisplayMode.Compact 
                    : UI.ConnectionIndicator.DisplayMode.Detailed,
                Position = (UI.ConnectionIndicator.IndicatorPosition)((int)settings.ConnectionIndicatorPosition)
            };
            
            HookRoomEvents();

            NetworkManager.Instance.OnLeaderboardReceived -= OnLeaderboardReceived;
            NetworkManager.Instance.OnLeaderboardReceived += OnLeaderboardReceived;
            NetworkManager.Instance.OnMatchHistoryReceived -= OnMatchHistoryReceived;
            NetworkManager.Instance.OnMatchHistoryReceived += OnMatchHistoryReceived;
        }

        private void OnLeaderboardReceived(LeaderboardResponsePacket packet)
        {
            if (packet?.Entries == null) return;
            LeaderboardManager.Instance.ApplyServerLeaderboard(packet.Entries);
            UserDirectoryClient.Instance.SeedFromLeaderboardEntries(packet.Entries);
            _leaderboardSyncedOnline = true;
        }

        private void OnMatchHistoryReceived(MatchHistoryResponsePacket packet)
        {
            if (packet?.Matches == null) return;
            LeaderboardManager.Instance.ApplyServerMatchHistory(packet.Matches);
            UserDirectoryClient.Instance.SeedFromMatchHistory(packet.Matches);
            _leaderboardSyncedOnline = true;
        }

        private void OpenLeaderboard()
        {
            TransitionToState(MenuState.Leaderboard);
            _leaderboardTab = 0;
            _leaderboardSelection = 0;
            _leaderboardScrollOffset = 0;
            _leaderboardSyncedOnline = false;
            RequestLeaderboardRefresh();
        }

        private void RequestLeaderboardRefresh()
        {
            if (NetworkManager.Instance.IsConnected)
            {
                NetworkManager.Instance.RequestGlobalLeaderboard(0, 50);
                NetworkManager.Instance.RequestMatchHistory(null, 0, 50);
            }
            else
            {
                _ = SyncLeaderboardFromHttpAsync();
            }
        }

        private async Task SyncLeaderboardFromHttpAsync()
        {
            if (_leaderboardSyncInFlight)
                return;

            _leaderboardSyncInFlight = true;
            try
            {
                string baseUrl = NetworkManager.Instance.ServerUrl;
                if (string.IsNullOrWhiteSpace(baseUrl))
                    baseUrl = "https://funkymakerserver.onrender.com";

                string url = $"{baseUrl.TrimEnd('/')}/api/leaderboard?take=50&matchesTake=50";
                string response = await _leaderboardHttpClient.GetStringAsync(url);
                var payload = JsonSerializer.Deserialize<HttpLeaderboardPayload>(response, _leaderboardJsonOptions);
                if (payload != null && (payload.Entries != null || payload.Matches != null))
                {
                    Interlocked.Exchange(ref _pendingHttpLeaderboard, payload);
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[Multiplayer] HTTP leaderboard sync failed: {ex.Message}");
            }
            finally
            {
                _leaderboardSyncInFlight = false;
            }
        }
        
        /// <summary>
        /// Sets the Game1 instance reference (must be called from Game1.LoadContent)
        /// </summary>
        public void SetGame(Game1 game)
        {
            _game = game;
        }

        /// <summary>
        /// Sets the TrueType FontRenderer for crisp text rendering across all resolutions.
        /// </summary>
        public void SetFontRenderer(FontRenderer? fontRenderer)
        {
            _fontRenderer = fontRenderer;
            _connectionIndicator?.SetFontRenderer(fontRenderer);
        }

        /// <summary>
        /// Sets preloaded crisp PNG icons for multiplayer menu cards and buttons.
        /// </summary>
        public void SetIcons(Dictionary<string, Texture2D>? icons)
        {
            _menuIcons = icons ?? new Dictionary<string, Texture2D>();
        }

        /// <summary>
        /// Initialize touch controls for Android (must be called after GraphicsDevice is ready)
        /// </summary>
        public void InitializeTouchControls(GraphicsDevice graphicsDevice, int screenWidth, int screenHeight)
        {
#if ANDROID
            _touchControls = new TouchControls();
            _touchControls.Initialize(graphicsDevice, screenWidth, screenHeight);
            _touchControls.SetMode(TouchControls.ControlMode.Menu);
            // Room screens are made for tapping: their buttons win over the D-pad drawn on top of them
            // (READY sits under the D-pad, the chat box under A/B on most phones).
            _touchControls.IgnorePoint = IsRoomHitAt;
            _touchScreenHeight = screenHeight;
            Console.WriteLine("[MultiplayerMenu] Touch controls initialized for Android (Menu mode)");
#endif
        }

        /// <summary>
        /// Refresh the list of available levels for multiplayer.
        /// </summary>
        public void RefreshLevelList()
        {
            _availableLevels.Clear();
            
            // Scan levels folder for .fnmm, .zip, and .rar files (excluding Backups)
            if (System.IO.Directory.Exists(_levelsFolder))
            {
                string backupsSegment = System.IO.Path.DirectorySeparatorChar + "Backups" + System.IO.Path.DirectorySeparatorChar;
                var files = System.IO.Directory.GetFiles(_levelsFolder, "*.*", System.IO.SearchOption.AllDirectories);
                foreach (var file in files)
                {
                    if (file.Contains(backupsSegment, StringComparison.OrdinalIgnoreCase))
                        continue;

                    if (FreeplayMenu.IsSupportedLevelFile(file))
                    {
                        // Relative to the levels folder, so levels in subfolders (e.g. Received/) resolve.
                        _availableLevels.Add(System.IO.Path.GetRelativePath(_levelsFolder, file));
                    }
                }
            }
            
            // If no levels found, add placeholder
            if (_availableLevels.Count == 0)
            {
                Console.WriteLine("[Multiplayer] No levels found in mods/levels/ folder");
            }

            RebuildLocalRows();
        }

        /// <summary>
        /// Update menu logic.
        /// </summary>
        public void Update(GameTime gameTime)
        {
            // Compare against the state seen at the end of the previous frame, so changes made by
            // network callbacks between frames (match found, opponent ready…) are heard too.
            // If the menu was not updated last frame, start from the current state.
            var b = _sfxPrevFrame == UiSfx.Frame - 1 ? _sfxPrev : CaptureSfxState();

            UpdateCore(gameTime);
            QueueSfx(b);
            _sfxPrev = CaptureSfxState();
            _sfxPrevFrame = UiSfx.Frame;
        }

        private readonly record struct SfxState(
            MenuState _state, int _selection, int _settingsCategory, int _settingsSelection,
            bool _isSettingFocused, bool _isEditingText, string _editingTextValue, int _leaderboardTab,
            int _leaderboardSelection, int _levelSelection, LevelSource _selectedSource, bool _amReady,
            int _readyCount, string _errorMessage, int _chatCount, bool _myTurn, int _roomListSelection, string _textField);

        private SfxState _sfxPrev;
        private long _sfxPrevFrame = -10;

        private SfxState CaptureSfxState()
        {
            var room = RoomClient.Instance;
            int ready = room.State?.Members.Count(m => m.Status == Protocol.StatusReady) ?? 0;
            return new(_state, _selection, _settingsCategory, _settingsSelection, _isSettingFocused,
                _isEditingText, _editingTextValue, _leaderboardTab, _leaderboardSelection, _levelSelection,
                _selectedSource, room.Me?.Status == Protocol.StatusReady, ready, _errorMessage, room.Chat.Count,
                room.IsMyTurn, _roomListSelection, _textFieldValue);
        }

        /// <summary>Queues UI sounds for what changed since <paramref name="b"/>.</summary>
        private void QueueSfx(SfxState b)
        {

            if (_state != b._state)
            {
                if (b._state == MenuState.Connecting && _state == MenuState.RoomBrowser) UiSfx.Play(UiSound.Notice);
                else UiSfx.Play(StateDepth(_state) > StateDepth(b._state) ? UiSound.Confirm : UiSound.Back);
            }
            else
            {
                if (_selection != b._selection || _roomListSelection != b._roomListSelection) UiSfx.Play(UiSound.Move);
                if (_settingsSelection != b._settingsSelection) UiSfx.Play(UiSound.MoveRow);
                if (_leaderboardSelection != b._leaderboardSelection || _levelSelection != b._levelSelection) UiSfx.Play(UiSound.MoveList);
                if (_settingsCategory != b._settingsCategory || _leaderboardTab != b._leaderboardTab || _selectedSource != b._selectedSource)
                    UiSfx.Play(UiSound.SubTab);
                if (_isSettingFocused != b._isSettingFocused) UiSfx.Play(_isSettingFocused ? UiSound.Focus : UiSound.Back);
                if (_isEditingText != b._isEditingText) UiSfx.Play(_isEditingText ? UiSound.Focus : UiSound.Confirm);
                else if (_isEditingText && _editingTextValue != b._editingTextValue) UiSfx.Play(UiSound.Type);
                if (_textFieldValue != b._textField && _textFieldValue.Length > b._textField.Length) UiSfx.Play(UiSound.Type);
                if (_amReadyChanged(b)) UiSfx.Play(UiSound.Toggle);
                else if (b._readyCount < CaptureReadyCount()) UiSfx.Play(UiSound.Notice);
                if (RoomClient.Instance.IsMyTurn && !b._myTurn && _state == MenuState.WaitingRoom) UiSfx.Play(UiSound.Receive);
                if (RoomClient.Instance.Chat.Count > b._chatCount && _state == MenuState.WaitingRoom) UiSfx.Play(UiSound.Notice);
            }

            if (!string.IsNullOrEmpty(_errorMessage) && _errorMessage != b._errorMessage) UiSfx.Play(UiSound.Error);
        }

        /// <summary>Navigation depth of each menu state (for Confirm vs Back sounds).</summary>
        private static int StateDepth(MenuState state) => state switch
        {
            MenuState.MainMenu => 0,
            MenuState.Settings or MenuState.Leaderboard or MenuState.Connecting or MenuState.RoomBrowser => 1,
            MenuState.RoomSettings => 2,
            MenuState.WaitingRoom => 3,
            _ => 4, // SongSelect
        };

        private bool _amReadyChanged(SfxState b) => (RoomClient.Instance.Me?.Status == Protocol.StatusReady) != b._amReady;
        private static int CaptureReadyCount() => RoomClient.Instance.State?.Members.Count(m => m.Status == Protocol.StatusReady) ?? 0;

        private void UpdateCore(GameTime gameTime)
        {
            float dt = (float)gameTime.ElapsedGameTime.TotalSeconds;
            _pulseTimer += dt * PULSE_SPEED;
            _searchTimer += dt;

            UpdateRoomScreens(dt);
            UpdateLevelFolderWatch(dt);

            var pendingLeaderboard = Interlocked.Exchange(ref _pendingHttpLeaderboard, null);
            if (pendingLeaderboard != null)
            {
                if (pendingLeaderboard.Entries != null)
                {
                    LeaderboardManager.Instance.ApplyServerLeaderboard(pendingLeaderboard.Entries);
                    UserDirectoryClient.Instance.SeedFromLeaderboardEntries(pendingLeaderboard.Entries);
                }
                if (pendingLeaderboard.Matches != null)
                {
                    LeaderboardManager.Instance.ApplyServerMatchHistory(pendingLeaderboard.Matches);
                    UserDirectoryClient.Instance.SeedFromMatchHistory(pendingLeaderboard.Matches);
                }
                _leaderboardSyncedOnline = true;
            }

            // Update connection indicator
            _connectionIndicator?.Update(dt);
            
            // Smooth fade-in effect
            if (_fadeAlpha < 1f)
                _fadeAlpha = Math.Min(1f, _fadeAlpha + dt * 3f);
            
            // State transition animation
            if (_stateTransitionTimer > 0f)
                _stateTransitionTimer -= dt;
            
            // Error message timeout
            if (_errorMessageTimer > 0f)
            {
                _errorMessageTimer -= dt;
                if (_errorMessageTimer <= 0f)
                    _errorMessage = "";
            }
            if (_infoMessageTimer > 0f)
            {
                _infoMessageTimer -= dt;
                if (_infoMessageTimer <= 0f)
                    _infoMessage = "";
            }

            // Marshal SignalR connection lifecycle notifications onto the main update thread
            string? pendingDisconnect = Interlocked.Exchange(ref _pendingDisconnectReason, null);
            if (!string.IsNullOrEmpty(pendingDisconnect))
            {
                Console.WriteLine($"[Multiplayer] Disconnected: {pendingDisconnect}");
                if (_state != MenuState.Connecting && _state != MenuState.MainMenu)
                {
                    _connectionRetryTimer = 0f;
                    _connectionAttempts = 0;
                    TransitionToState(MenuState.MainMenu);
                    ShowError(pendingDisconnect);
                }
            }

            string? pendingConnError = Interlocked.Exchange(ref _pendingConnectionError, null);
            if (!string.IsNullOrEmpty(pendingConnError) && _state != MenuState.Connecting)
            {
                ShowError(pendingConnError);
            }

            if (Interlocked.Exchange(ref _pendingConnectedSignal, 0) == 1)
            {
                CompleteServerConnection();
            }

            // Input cooldown
            if (_inputCooldown > 0f)
                _inputCooldown -= dt;

            if (!Game1.CanAcceptInput)
            {
                _hoveredButton = -1;
                _mouseMovedThisFrame = false;
                _prevKb = default;
                _prevMouse = Game1.NeutralMouseState(Mouse.GetState());
                return;
            }

            var kb = LayoutKeyboard.GetState();
#if ANDROID
            // Taps act as mouse clicks here (Mouse.GetState does not see touches on Android).
            var mouse = TouchAsMouse();
#else
            var mouse = Mouse.GetState();
#endif
            _frameMouse = mouse;
            
#if ANDROID
            // Update touch controls
            _touchControls?.Update();
            
            // Merge touch input with keyboard
            kb = MergeTouchWithKeyboard(kb);
#endif
            
            Point curMouse = new Point(mouse.X, mouse.Y);
            _mouseMovedThisFrame = curMouse != _lastMousePos;
            _lastMousePos = curMouse;

            // Update mouse hover detection
            UpdateMouseHover(mouse);

            // Global shortcut: F1 or clicking "Keybinds & Settings" pill opens Gameplay Settings without leaving Multiplayer
            if (!_isEditingText && !TextFieldActive && _state != MenuState.Connecting &&
                _inputCooldown <= 0f &&
                (IsKeyPressed(kb, HotkeyAction.MultiplayerSettings) || IsRectClicked(mouse, _gameplaySettingsRect)))
            {
                GameplaySettingsRequested = true;
                _inputCooldown = INPUT_COOLDOWN_TIME;
                _prevKb = kb;
                _prevMouse = mouse;
                return;
            }

            // Handle input based on current state
            switch (_state)
            {
                case MenuState.MainMenu:
                    UpdateMainMenu(kb, mouse, dt);
                    break;
                case MenuState.Connecting:
                    UpdateConnecting(kb, mouse, dt);
                    break;
                case MenuState.RoomBrowser:
                    UpdateRoomBrowser(kb, mouse, dt);
                    break;
                case MenuState.RoomSettings:
                    UpdateRoomSettings(kb, mouse, dt);
                    break;
                case MenuState.WaitingRoom:
                    UpdateWaitingRoom(kb, mouse, dt);
                    break;
                case MenuState.SongSelect:
                    UpdateSongSelect(kb, mouse, dt);
                    break;
                case MenuState.Settings:
                    UpdateSettings(kb, mouse, dt);
                    break;
                case MenuState.Leaderboard:
                    UpdateLeaderboard(kb, mouse, dt);
                    break;
            }

            _prevKb = kb;
            _prevMouse = mouse;
        }

        private bool IsKeyPressed(KeyboardState kb, Keys key) => kb.IsKeyDown(key) && !_prevKb.IsKeyDown(key);

        /// <summary>Rebindable hotkey (fork: Settings → CONTROLS).</summary>
        private bool IsKeyPressed(KeyboardState kb, HotkeyAction action) => Controls.Pressed(action, kb, _prevKb);

        private bool IsLeftClickReleased(MouseState mouse) =>
            mouse.LeftButton == ButtonState.Released && _prevMouse.LeftButton == ButtonState.Pressed;

        private bool IsRectClicked(MouseState mouse, Rectangle rect) =>
            !rect.IsEmpty && IsLeftClickReleased(mouse) && rect.Contains(new Point(mouse.X, mouse.Y));

        private void ResetHitboxes()
        {
            _clickableAreas.Clear();
            _leftArrowAreas.Clear();
            _rightArrowAreas.Clear();
            _tabHitboxes.Clear();
            _backButtonRect = Rectangle.Empty;
            _refreshButtonRect = Rectangle.Empty;
            _gameplaySettingsRect = Rectangle.Empty;
            _shareButtonRect = Rectangle.Empty;
            _hoveredButton = -1;
            _isSettingFocused = false;
            if (TextFieldActive) EndTextField();
            _prevKb = LayoutKeyboard.GetState();
            _prevMouse = Mouse.GetState();
            _inputCooldown = INPUT_COOLDOWN_TIME;
        }
        
        /// <summary>
        /// Update mouse hover detection for buttons.
        /// </summary>
        private void UpdateMouseHover(MouseState mouse)
        {
            _hoveredButton = -1;
            Point mousePos = new Point(mouse.X, mouse.Y);
            
            for (int i = 0; i < _clickableAreas.Count; i++)
            {
                if (_clickableAreas[i].Contains(mousePos))
                {
                    _hoveredButton = i;
                    break;
                }
            }
        }
        
        /// <summary>
        /// Check if a button was clicked.
        /// </summary>
        private bool IsButtonClicked(MouseState mouse, int buttonIndex)
        {
            if (buttonIndex < 0 || buttonIndex >= _clickableAreas.Count)
                return false;
                
            if (IsLeftClickReleased(mouse))
            {
                Point mousePos = new Point(mouse.X, mouse.Y);
                return _clickableAreas[buttonIndex].Contains(mousePos);
            }
            
            return false;
        }

        private void UpdateMainMenu(KeyboardState kb, MouseState mouse, float dt)
        {
            if (_inputCooldown > 0f) return;
            
            // Mouse hover updates selection only when mouse moved
            if (_mouseMovedThisFrame && _hoveredButton >= 0 && _hoveredButton < 4)
            {
                _selection = _hoveredButton;
            }

            // Navigate menu with keyboard
            if (IsKeyPressed(kb, Keys.Down))
            {
                _selection = (_selection + 1) % 4;
                _inputCooldown = INPUT_COOLDOWN_TIME;
            }
            if (IsKeyPressed(kb, Keys.Up))
            {
                _selection = (_selection - 1 + 4) % 4;
                _inputCooldown = INPUT_COOLDOWN_TIME;
            }

            // Select option with keyboard
            if (IsKeyPressed(kb, Keys.Enter) || IsKeyPressed(kb, Keys.Space))
            {
                ExecuteMainMenuAction(_selection);
                return;
            }
            
            // Select option with mouse click
            for (int i = 0; i < 4; i++)
            {
                if (IsButtonClicked(mouse, i))
                {
                    _selection = i;
                    ExecuteMainMenuAction(i);
                    return;
                }
            }
            
            // Also allow ESC to go back
            if (IsKeyPressed(kb, Keys.Escape) || IsRectClicked(mouse, _backButtonRect))
            {
                _wantsExit = true;
            }
        }
        
        private void ExecuteMainMenuAction(int action)
        {
            switch (action)
            {
                case 0: // Connect
                    ConnectToServer();
                    break;
                case 1: // Leaderboard
                    OpenLeaderboard();
                    break;
                case 2: // Settings
                    TransitionToState(MenuState.Settings);
                    _settingsCategory = 0;
                    _settingsSelection = 0;
                    _settingsScrollOffset = 0;
                    break;
                case 3: // Back to freeplay
                    _wantsExit = true;
                    break;
            }
        }

        private void UpdateConnecting(KeyboardState kb, MouseState mouse, float dt)
        {
            // Allow cancel with ESC or clicking Cancel button
            if (IsKeyPressed(kb, Keys.Escape) || IsRectClicked(mouse, _backButtonRect))
            {
                Interlocked.Exchange(ref _pendingConnectedSignal, 0);
                Interlocked.Exchange(ref _pendingConnectionError, null);
                _connectionAttempts = 0;
                _connectionRetryTimer = 0f;
                NetworkManager.Instance.Disconnect();
                TransitionToState(MenuState.MainMenu);
                return;
            }

            // Enter server ONLY when actually connected and still in Connecting state
            if (NetworkManager.Instance.IsConnected)
            {
                CompleteServerConnection();
                return;
            }

            // Wait out backoff between retry attempts (up to MAX_CONNECTION_ATTEMPTS = 5)
            if (_connectionRetryTimer > 0f)
            {
                _connectionRetryTimer -= dt;
                if (_connectionRetryTimer <= 0f)
                {
                    _connectionRetryTimer = 0f;
                    if (_connectionAttempts < MAX_CONNECTION_ATTEMPTS)
                    {
                        _connectionAttempts++;
                        Console.WriteLine($"[Multiplayer] Retry attempt {_connectionAttempts}/{MAX_CONNECTION_ATTEMPTS}");
                        NetworkManager.Instance.Connect(_username);
                    }
                }
                return;
            }

            if (!NetworkManager.Instance.IsConnecting)
            {
                // Current attempt failed or timed out
                if (_connectionAttempts < MAX_CONNECTION_ATTEMPTS)
                {
                    Console.WriteLine($"[Multiplayer] Connection attempt {_connectionAttempts}/{MAX_CONNECTION_ATTEMPTS} failed, retrying in 2 seconds...");
                    _connectionRetryTimer = 2f;
                }
                else
                {
                    // All attempts exhausted - ensure any background hub is torn down and return to menu
                    Interlocked.Exchange(ref _pendingConnectedSignal, 0);
                    string? lastErr = Interlocked.Exchange(ref _pendingConnectionError, null);
                    NetworkManager.Instance.Disconnect();
                    ShowError(string.IsNullOrWhiteSpace(lastErr)
                        ? $"Connection failed after {MAX_CONNECTION_ATTEMPTS} attempts."
                        : $"{lastErr} (failed after {MAX_CONNECTION_ATTEMPTS} attempts)");
                    TransitionToState(MenuState.MainMenu);
                    _connectionAttempts = 0;
                    _connectionRetryTimer = 0f;
                }
            }
        }
        
        private void UpdateSettings(KeyboardState kb, MouseState mouse, float dt)
        {
            var settings = MultiplayerSettings.Instance;

            // Handle text editing BEFORE cooldown so typed characters are never dropped
            if (UpdateTextField(kb, dt)) return;

            if (_inputCooldown > 0f) return;

            // Clicking top-right ESC: Back saves & exits
            if (IsRectClicked(mouse, _backButtonRect))
            {
                SaveSettings();
                TransitionToState(MenuState.MainMenu);
                return;
            }

            // Clicking category tabs switches category immediately
            for (int c = 0; c < _tabHitboxes.Count; c++)
            {
                if (IsRectClicked(mouse, _tabHitboxes[c]))
                {
                    _settingsCategory = c;
                    _settingsSelection = 0;
                    _settingsScrollOffset = 0;
                    _isSettingFocused = false;
                    _inputCooldown = INPUT_COOLDOWN_TIME;
                    return;
                }
            }

            int itemCount = GetSettingsCategoryItemCount(_settingsCategory);
            var categorySettings = GetCategorySettings(_settingsCategory, settings);

            // Mouse wheel scrolls setting list
            int wheelDelta = mouse.ScrollWheelValue - _prevMouse.ScrollWheelValue;
            if (wheelDelta != 0 && itemCount > 0 && !_isSettingFocused)
            {
                int step = wheelDelta > 0 ? -1 : 1;
                _settingsSelection = (_settingsSelection + step + itemCount) % itemCount;
                AdjustSettingsScroll();
            }

            // Mouse arrow & row interactions
            for (int i = 0; i < _clickableAreas.Count; i++)
            {
                int actualIndex = _settingsScrollOffset + i;
                if (actualIndex < 0 || actualIndex >= categorySettings.Count)
                    continue;

                bool isAdjustable = categorySettings[actualIndex].IsAdjustable;

                // Click left arrow (<) or right arrow (>)
                if (IsLeftClickReleased(mouse))
                {
                    Point mousePos = new Point(mouse.X, mouse.Y);
                    if (isAdjustable && i < _leftArrowAreas.Count && _leftArrowAreas[i].Contains(mousePos))
                    {
                        _settingsSelection = actualIndex;
                        _isSettingFocused = false;
                        if (categorySettings[actualIndex].IsToggle)
                            ToggleSettingValue(_settingsCategory, actualIndex);
                        else
                            ModifySettingValue(_settingsCategory, actualIndex, -1);
                        _inputCooldown = INPUT_COOLDOWN_TIME;
                        return;
                    }
                    if (isAdjustable && i < _rightArrowAreas.Count && _rightArrowAreas[i].Contains(mousePos))
                    {
                        _settingsSelection = actualIndex;
                        _isSettingFocused = false;
                        if (categorySettings[actualIndex].IsToggle)
                            ToggleSettingValue(_settingsCategory, actualIndex);
                        else
                            ModifySettingValue(_settingsCategory, actualIndex, 1);
                        _inputCooldown = INPUT_COOLDOWN_TIME;
                        return;
                    }
                }

                // Single-click row body: edit Username / toggle bool / focus adjustable
                if (IsButtonClicked(mouse, i))
                {
                    _settingsSelection = actualIndex;
                    if (_settingsCategory == 0 && actualIndex == 0)
                    {
                        BeginUsernameEdit();
                        _isSettingFocused = false;
                    }
                    else if (categorySettings[actualIndex].IsToggle)
                    {
                        _isSettingFocused = false;
                        ToggleSettingValue(_settingsCategory, actualIndex);
                    }
                    else
                    {
                        _isSettingFocused = !_isSettingFocused;
                    }
                    _inputCooldown = INPUT_COOLDOWN_TIME;
                    return;
                }

                // Hover updates selection ONLY when mouse moved
                if (_mouseMovedThisFrame && _hoveredButton == i && actualIndex != _settingsSelection)
                {
                    _settingsSelection = actualIndex;
                    _isSettingFocused = false;
                }
            }

            // Q / E always switch categories (matching SettingsManager)
            if (IsKeyPressed(kb, HotkeyAction.PrevTab))
            {
                _settingsCategory = (_settingsCategory - 1 + 4) % 4;
                _settingsSelection = 0;
                _settingsScrollOffset = 0;
                _isSettingFocused = false;
                _inputCooldown = INPUT_COOLDOWN_TIME;
                return;
            }
            if (IsKeyPressed(kb, HotkeyAction.NextTab))
            {
                _settingsCategory = (_settingsCategory + 1) % 4;
                _settingsSelection = 0;
                _settingsScrollOffset = 0;
                _isSettingFocused = false;
                _inputCooldown = INPUT_COOLDOWN_TIME;
                return;
            }

            // LEFT / RIGHT: adjust value if focused, else switch category (matching SettingsManager!)
            if (IsKeyPressed(kb, Keys.Left))
            {
                if (_isSettingFocused && _settingsSelection < categorySettings.Count)
                {
                    if (categorySettings[_settingsSelection].IsToggle)
                        ToggleSettingValue(_settingsCategory, _settingsSelection);
                    else
                        ModifySettingValue(_settingsCategory, _settingsSelection, -1);
                }
                else
                {
                    _settingsCategory = (_settingsCategory - 1 + 4) % 4;
                    _settingsSelection = 0;
                    _settingsScrollOffset = 0;
                    _isSettingFocused = false;
                }
                _inputCooldown = INPUT_COOLDOWN_TIME;
                return;
            }
            if (IsKeyPressed(kb, Keys.Right))
            {
                if (_isSettingFocused && _settingsSelection < categorySettings.Count)
                {
                    if (categorySettings[_settingsSelection].IsToggle)
                        ToggleSettingValue(_settingsCategory, _settingsSelection);
                    else
                        ModifySettingValue(_settingsCategory, _settingsSelection, 1);
                }
                else
                {
                    _settingsCategory = (_settingsCategory + 1) % 4;
                    _settingsSelection = 0;
                    _settingsScrollOffset = 0;
                    _isSettingFocused = false;
                }
                _inputCooldown = INPUT_COOLDOWN_TIME;
                return;
            }

            // Navigate settings with UP/DOWN (also clears focus)
            if (itemCount > 0)
            {
                if (IsKeyPressed(kb, Keys.Down))
                {
                    _settingsSelection = (_settingsSelection + 1) % itemCount;
                    _isSettingFocused = false;
                    AdjustSettingsScroll();
                    _inputCooldown = INPUT_COOLDOWN_TIME;
                }
                if (IsKeyPressed(kb, Keys.Up))
                {
                    _settingsSelection = (_settingsSelection - 1 + itemCount) % itemCount;
                    _isSettingFocused = false;
                    AdjustSettingsScroll();
                    _inputCooldown = INPUT_COOLDOWN_TIME;
                }

                // ENTER / SPACE: start editing Username / toggle bool / focus adjustable
                if (IsKeyPressed(kb, Keys.Enter) || IsKeyPressed(kb, Keys.Space))
                {
                    if (_settingsCategory == 0 && _settingsSelection == 0)
                    {
                        BeginUsernameEdit();
                        _isSettingFocused = false;
                    }
                    else if (_settingsSelection < categorySettings.Count && categorySettings[_settingsSelection].IsToggle)
                    {
                        _isSettingFocused = false;
                        ToggleSettingValue(_settingsCategory, _settingsSelection);
                    }
                    else
                    {
                        _isSettingFocused = !_isSettingFocused;
                    }
                    _inputCooldown = INPUT_COOLDOWN_TIME;
                }
            }

            // R to reset to defaults
            if (IsKeyPressed(kb, HotkeyAction.ResetOnlineSettings))
            {
                settings.ResetToDefaults();
                SaveSettings();
                _inputCooldown = INPUT_COOLDOWN_TIME;
            }

            // ESC unfocuses first, then saves & exits to MainMenu
            if (IsKeyPressed(kb, Keys.Escape))
            {
                if (_isSettingFocused)
                {
                    _isSettingFocused = false;
                    _inputCooldown = INPUT_COOLDOWN_TIME;
                }
                else
                {
                    SaveSettings();
                    TransitionToState(MenuState.MainMenu);
                }
            }
        }

        private void AdjustSettingsScroll()
        {
            if (_settingsSelection >= _settingsScrollOffset + MAX_SETTINGS_VISIBLE)
                _settingsScrollOffset = _settingsSelection - MAX_SETTINGS_VISIBLE + 1;
            else if (_settingsSelection < _settingsScrollOffset)
                _settingsScrollOffset = _settingsSelection;
        }
        
        private void SaveSettings()
        {
            var settings = MultiplayerSettings.Instance;
            settings.Save();
            
            // Apply connection indicator settings
            _connectionIndicator.Mode = settings.InterfaceMode == MultiplayerSettings.UIMode.Compact 
                ? UI.ConnectionIndicator.DisplayMode.Compact 
                : UI.ConnectionIndicator.DisplayMode.Detailed;
            _connectionIndicator.Position = (UI.ConnectionIndicator.IndicatorPosition)((int)settings.ConnectionIndicatorPosition);
            
            // Update username
            _username = settings.Username;
            
            // Send updated settings to server if connected
            if (NetworkManager.Instance.IsConnected)
                ResendProfile();
            
            Console.WriteLine("[Settings] Settings saved and synced");
        }
        
        private int GetSettingsCategoryItemCount(int category)
        {
            return category switch
            {
                0 => 3,  // User: Username, Player Color, My Character (Local)
                1 => 6,  // Gameplay: ShowOpponentNotes, Scores list, Scores list side, ShowConnectionQuality, Opponent Notes Opacity, Teams rival strumline
                2 => 4,  // Performance: UpdateRate, EnableInterpolation, osu! videos, Chart Only (No Sprites)
                _ => 3,  // Visual: HudOpacity, ConnectionIndicatorPosition, InterfaceMode
            };
        }

        private void CycleEquippedCharacter(int direction)
        {
            var settings = MultiplayerSettings.Instance;
            var chars = Editors.Character.CharacterLibrary.ScanCharacters(bakedOnly: false);
            var options = new List<string>(chars.Count + 1) { "" };
            foreach (var c in chars)
                options.Add(c.Slug);

            int curIdx = options.FindIndex(s => string.Equals(s, settings.EquippedCharacter ?? "", StringComparison.OrdinalIgnoreCase));
            if (curIdx < 0) curIdx = 0;
            int nextIdx = (curIdx + direction + options.Count) % options.Count;
            settings.EquippedCharacter = options[nextIdx];
            settings.Save();
        }

        private static string GetEquippedCharacterLabel(string? slug)
        {
            if (string.IsNullOrWhiteSpace(slug))
                return "Level Default";
            var chars = Editors.Character.CharacterLibrary.ScanCharacters(bakedOnly: false);
            var match = chars.Find(c => string.Equals(c.Slug, slug, StringComparison.OrdinalIgnoreCase));
            return match != null ? match.Name : $"{slug} (missing)";
        }
        
        private void ModifySettingValue(int category, int selection, int direction)
        {
            var settings = MultiplayerSettings.Instance;
            
            switch (category)
            {
                case 0: // User
                    if (selection == 1) // Player Color
                        settings.PlayerColorHue = (settings.PlayerColorHue + direction * 10 + 360) % 360;
                    else if (selection == 2) // My Character (Local)
                    {
                        CycleEquippedCharacter(direction);
                        return;
                    }
                    break;
                    
                case 1: // Gameplay
                    if (selection == 4) // GhostTransparency
                        settings.GhostTransparency = Math.Clamp(settings.GhostTransparency + direction * 10, 0, 100);
                    else
                        ToggleSettingValue(category, selection);
                    break;
                    
                case 2: // Performance
                    if (selection == 0) // UpdateRate
                    {
                        if (direction > 0)
                        {
                            settings.UpdateRate = settings.UpdateRate switch
                            {
                                MultiplayerSettings.NetworkUpdateRate.Low => MultiplayerSettings.NetworkUpdateRate.Medium,
                                MultiplayerSettings.NetworkUpdateRate.Medium => MultiplayerSettings.NetworkUpdateRate.High,
                                _ => MultiplayerSettings.NetworkUpdateRate.High
                            };
                        }
                        else
                        {
                            settings.UpdateRate = settings.UpdateRate switch
                            {
                                MultiplayerSettings.NetworkUpdateRate.High => MultiplayerSettings.NetworkUpdateRate.Medium,
                                MultiplayerSettings.NetworkUpdateRate.Medium => MultiplayerSettings.NetworkUpdateRate.Low,
                                _ => MultiplayerSettings.NetworkUpdateRate.Low
                            };
                        }
                    }
                    else
                    {
                        ToggleSettingValue(category, selection);
                        return; // saved there
                    }
                    break;
                    
                case 3: // Visual
                    switch (selection)
                    {
                        case 0: // HudOpacity
                            settings.HudOpacity = Math.Clamp(settings.HudOpacity + direction * 10, 10, 100);
                            break;
                        case 1: // ConnectionIndicatorPosition
                            int posCount = Enum.GetValues(typeof(MultiplayerSettings.IndicatorPosition)).Length;
                            int currentPos = (int)settings.ConnectionIndicatorPosition;
                            settings.ConnectionIndicatorPosition = (MultiplayerSettings.IndicatorPosition)((currentPos + direction + posCount) % posCount);
                            _connectionIndicator.Position = (UI.ConnectionIndicator.IndicatorPosition)((int)settings.ConnectionIndicatorPosition);
                            break;
                        case 2: // InterfaceMode
                            settings.InterfaceMode = settings.InterfaceMode == MultiplayerSettings.UIMode.Compact 
                                ? MultiplayerSettings.UIMode.Full 
                                : MultiplayerSettings.UIMode.Compact;
                            _connectionIndicator.Mode = settings.InterfaceMode == MultiplayerSettings.UIMode.Compact 
                                ? UI.ConnectionIndicator.DisplayMode.Compact 
                                : UI.ConnectionIndicator.DisplayMode.Detailed;
                            break;
                    }
                    break;
            }
            
            settings.Save();
            if (NetworkManager.Instance.IsConnected)
            {
                if (category == 0) ResendProfile();
            }
        }
        
        private void BeginUsernameEdit()
        {
            var settings = MultiplayerSettings.Instance;
            BeginTextField("username", settings.Username, 20, v =>
            {
                string trimmed = v.Trim();
                if (trimmed.Length == 0) return;
                settings.Username = trimmed;
                _username = trimmed;
                settings.Save();
                if (NetworkManager.Instance.IsConnected) ResendProfile();
            }, title: "Username");
        }

        private void ToggleSettingValue(int category, int selection)
        {
            var settings = MultiplayerSettings.Instance;
            
            switch (category)
            {
                case 0: // User
                    if (selection == 0) // Username - start text editing
                        BeginUsernameEdit();
                    else if (selection == 2)
                        CycleEquippedCharacter(1);
                    break;
                    
                case 1: // Gameplay
                    switch (selection)
                    {
                        case 0:
                            // Same setting as Settings → Opponent Strumline (saved in settings.json)
                            settings.ShowOpponentNotes = !settings.ShowOpponentNotes;
                            Game1.SaveSettings();
                            break;
                        case 1: settings.ShowOpponentScore = !settings.ShowOpponentScore; break;
                        case 2:
                            settings.ScoreListPosition = settings.ScoreListPosition == MultiplayerSettings.ScoreListSide.Left
                                ? MultiplayerSettings.ScoreListSide.Right : MultiplayerSettings.ScoreListSide.Left;
                            break;
                        case 3: settings.ShowConnectionQuality = !settings.ShowConnectionQuality; break;
                        case 5:
                            settings.TeamsRivalStrumline = settings.TeamsRivalStrumline == MultiplayerSettings.RivalStrumlineMode.RealPlayer
                                ? MultiplayerSettings.RivalStrumlineMode.Cpu : MultiplayerSettings.RivalStrumlineMode.RealPlayer;
                            break;
                    }
                    break;
                    
                case 2: // Performance
                    if (selection == 1) // EnableInterpolation
                        settings.EnableInterpolation = !settings.EnableInterpolation;
                    else if (selection == 2) // osu! songs without video
                    {
                        settings.OsuSkipVideo = !settings.OsuSkipVideo;
                        if (NetworkManager.Instance.IsConnected) ResendProfile(); // the server picks the variant
                    }
                    else if (selection == 3) // FNF Chart Only (No Sprites)
                    {
                        settings.ChartOnlyMode = !settings.ChartOnlyMode;
                        RoomClient.Instance.OnChartOnlyModeChanged();
                        if (NetworkManager.Instance.IsConnected) ResendProfile(); // tells the server to send chartonly variant
                    }
                    break;
            }

            settings.Save();
        }

        /// <summary>
        /// Connect to multiplayer server.
        /// </summary>
        private void ConnectToServer()
        {
            TransitionToState(MenuState.Connecting);
            _connectionAttempts = 1;
            _connectionRetryTimer = 0f;
            Interlocked.Exchange(ref _pendingConnectedSignal, 0);
            Interlocked.Exchange(ref _pendingDisconnectReason, null);
            Interlocked.Exchange(ref _pendingConnectionError, null);

            // Subscribe cleanly without duplicate handlers on reconnect
            NetworkManager.Instance.OnConnected -= OnConnected;
            NetworkManager.Instance.OnConnected += OnConnected;
            NetworkManager.Instance.OnDisconnected -= OnDisconnected;
            NetworkManager.Instance.OnDisconnected += OnDisconnected;
            NetworkManager.Instance.OnConnectionError -= OnNetworkConnectionError;
            NetworkManager.Instance.OnConnectionError += OnNetworkConnectionError;
            NetworkManager.Instance.OnLevelListReceived -= OnLevelListReceived;
            NetworkManager.Instance.OnLevelListReceived += OnLevelListReceived;

            Console.WriteLine($"[Multiplayer] Starting connection attempt 1/{MAX_CONNECTION_ATTEMPTS}...");
            NetworkManager.Instance.Connect(_username);
        }
        
        /// <summary>
        /// Show an error message to the user.
        /// </summary>
        private void ShowError(string message)
        {
            _errorMessage = message;
            _errorMessageTimer = 5f; // Show for 5 seconds
            Console.WriteLine($"[Multiplayer Error] {message}");
        }

        /// <summary>
        /// Show a non-error notice (green toast) to the user.
        /// </summary>
        internal void ShowInfo(string message, float seconds = 6f)
        {
            _infoMessage = message;
            _infoMessageTimer = seconds;
            UiSfx.Play(UiSound.Notice);
            Console.WriteLine($"[Multiplayer] {message}");
        }
        
        /// <summary>
        /// Trigger a smooth state transition and clear stale input/hitboxes.
        /// </summary>
        private void TransitionToState(MenuState newState)
        {
            if (newState == MenuState.SongSelect)
            {
                _deleteConfirmPath = null;
                // Pick up levels added or removed while we were elsewhere (fork).
                if (_state != MenuState.SongSelect)
                {
                    RefreshLevelListKeepingSelection();
                    OpenSongPicker();
                }
            }
            _state = newState;
            _stateTransitionTimer = TRANSITION_DURATION;
            _fadeAlpha = 0f;
            ResetHitboxes();
        }

        // ══════════════════════════════════════════════════════════════════════
        // Network Event Handlers
        // ══════════════════════════════════════════════════════════════════════

        private static bool TryGetJsonPropertyIgnoreCase(JsonElement element, string propertyName, out JsonElement value)
        {
            if (element.ValueKind == JsonValueKind.Object)
            {
                if (element.TryGetProperty(propertyName, out value))
                    return true;

                foreach (var prop in element.EnumerateObject())
                {
                    if (string.Equals(prop.Name, propertyName, StringComparison.OrdinalIgnoreCase))
                    {
                        value = prop.Value;
                        return true;
                    }
                }
            }

            value = default;
            return false;
        }

        /// <summary>
        /// Check server information and version.
        /// </summary>
        private async void CheckServerInfo()
        {
            try
            {
                using var client = new HttpClient();
                client.Timeout = TimeSpan.FromSeconds(5);

                string serverUrl = string.IsNullOrWhiteSpace(NetworkManager.Instance.ServerUrl)
                    ? "https://funkymakerserver.onrender.com"
                    : NetworkManager.Instance.ServerUrl.TrimEnd('/');
                string response = await client.GetStringAsync($"{serverUrl}/api/server/info");

                using var json = JsonDocument.Parse(response);

                if (TryGetJsonPropertyIgnoreCase(json.RootElement, "Version", out var versionElement))
                {
                    _serverVersion = versionElement.GetString() ?? "Unknown";
                    Console.WriteLine($"[Multiplayer] Server version: {_serverVersion}");
                }

                if (TryGetJsonPropertyIgnoreCase(json.RootElement, "Levels", out var levelsElement))
                {
                    if (TryGetJsonPropertyIgnoreCase(levelsElement, "Count", out var countElement))
                    {
                        _serverLevelCount = countElement.GetInt32();
                        Console.WriteLine($"[Multiplayer] Server has {_serverLevelCount} official levels");
                    }

                    if (TryGetJsonPropertyIgnoreCase(levelsElement, "Names", out var namesElement))
                    {
                        var names = new List<string>();
                        foreach (var nameElement in namesElement.EnumerateArray())
                        {
                            var name = nameElement.GetString();
                            if (name != null) names.Add(name);
                        }
                        Console.WriteLine($"[Multiplayer] Available: {string.Join(", ", names)}");
                    }
                }

                if (TryGetJsonPropertyIgnoreCase(json.RootElement, "Architecture", out var archElement))
                {
                    if (TryGetJsonPropertyIgnoreCase(archElement, "Type", out var typeElement))
                    {
                        string archType = typeElement.GetString() ?? "Unknown";
                        Console.WriteLine($"[Multiplayer] Architecture: {archType}");
                    }
                }

                if (TryGetJsonPropertyIgnoreCase(json.RootElement, "Database", out var dbElement))
                {
                    string provider = TryGetJsonPropertyIgnoreCase(dbElement, "Provider", out var provEl)
                        ? (provEl.GetString() ?? "Unknown")
                        : "Unknown";
                    int dbPlayers = TryGetJsonPropertyIgnoreCase(dbElement, "Players", out var playersEl) ? playersEl.GetInt32() : 0;
                    int dbMatches = TryGetJsonPropertyIgnoreCase(dbElement, "Matches", out var matchesEl) ? matchesEl.GetInt32() : 0;
                    Console.WriteLine($"[Multiplayer] Server persistence: {provider} (players={dbPlayers}, matches={dbMatches})");

                    if (TryGetJsonPropertyIgnoreCase(dbElement, "LastError", out var errEl) &&
                        errEl.ValueKind == JsonValueKind.String &&
                        !string.IsNullOrWhiteSpace(errEl.GetString()))
                    {
                        Console.WriteLine($"[Multiplayer] Server DB warning: {errEl.GetString()}");
                    }
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[Multiplayer] Failed to get server info: {ex.Message}");
                _serverVersion = "Unknown";
            }
        }

        private void CompleteServerConnection()
        {
            if (_state != MenuState.Connecting || !NetworkManager.Instance.IsConnected)
            {
                Console.WriteLine($"[Multiplayer] Ignoring connection completion in state {_state} (IsConnected={NetworkManager.Instance.IsConnected})");
                return;
            }

            Interlocked.Exchange(ref _pendingConnectedSignal, 0);
            Console.WriteLine("[Multiplayer] Connected to server");
            _connectionAttempts = 0;
            _connectionRetryTimer = 0f;
            CheckServerInfo();
            NetworkManager.Instance.EnableAutoReconnect(true);
            OpenRoomBrowser();
            NetworkManager.Instance.RequestOfficialLevels();
            NetworkManager.Instance.RequestGlobalLeaderboard(0, 50);
            NetworkManager.Instance.RequestMatchHistory(null, 0, 50);
            _ = UserDirectoryClient.Instance.WarmFromServerAsync(NetworkManager.Instance.ServerUrl);
        }

        private void OnConnected()
        {
            Interlocked.Exchange(ref _pendingConnectedSignal, 1);
        }

        private void OnDisconnected(string reason)
        {
            Interlocked.Exchange(ref _pendingDisconnectReason, string.IsNullOrWhiteSpace(reason) ? "Disconnected from server" : reason);
        }

        private void OnNetworkConnectionError(string error)
        {
            if (!string.IsNullOrWhiteSpace(error))
                Interlocked.Exchange(ref _pendingConnectionError, error);
        }

        private void OnLevelListReceived(LevelListPacket packet)
        {
            Console.WriteLine($"[Multiplayer] Received {packet.Levels.Count} official levels");
            // RoomClient keeps the list (and warms the disk cache).
        }

        // ══════════════════════════════════════════════════════════════════════
        // Rendering
        // ══════════════════════════════════════════════════════════════════════

        /// <summary>
        /// Draw the multiplayer menu.
        /// </summary>
        public void Draw(SpriteBatch sb, SpriteFont font, Texture2D pixel, int screenWidth, int screenHeight)
        {
            if (screenWidth <= 0 || screenHeight <= 0)
                return;

            // Clear dynamic per-frame hitboxes so no stale areas survive across states
            _clickableAreas.Clear();
            _leftArrowAreas.Clear();
            _rightArrowAreas.Clear();
            _tabHitboxes.Clear();
            _backButtonRect = Rectangle.Empty;
            _refreshButtonRect = Rectangle.Empty;
            _gameplaySettingsRect = Rectangle.Empty;
            _shareButtonRect = Rectangle.Empty;
            _importButtonRect = Rectangle.Empty;
            _deleteButtonRect = Rectangle.Empty;
            _deleteYesRect = Rectangle.Empty;
            _deleteNoRect = Rectangle.Empty;
            _roomHit.Clear();

            float uiScale = GetUiScale(screenWidth, screenHeight);

            // Full screen modern slate background with subtle studio grid
            DrawGradientBackground(sb, pixel, screenWidth, screenHeight, uiScale);

            // Draw based on state
            switch (_state)
            {
                case MenuState.MainMenu:
                    DrawMainMenuModern(sb, font, pixel, screenWidth, screenHeight);
                    break;
                case MenuState.Connecting:
                    DrawConnectingModern(sb, font, pixel, screenWidth, screenHeight);
                    break;
                case MenuState.RoomBrowser:
                    DrawRoomBrowser(sb, font, pixel, screenWidth, screenHeight);
                    break;
                case MenuState.RoomSettings:
                    DrawRoomSettings(sb, font, pixel, screenWidth, screenHeight);
                    break;
                case MenuState.WaitingRoom:
                    DrawWaitingRoom(sb, font, pixel, screenWidth, screenHeight);
                    break;
                case MenuState.SongSelect:
                    DrawSongSelect(sb, font, pixel, screenWidth, screenHeight);
                    DrawDeleteConfirm(sb, font, pixel, screenWidth, screenHeight);
                    break;
                case MenuState.Settings:
                    DrawSettingsModern(sb, font, pixel, screenWidth, screenHeight);
                    break;
                case MenuState.Leaderboard:
                    DrawLeaderboardModern(sb, font, pixel, screenWidth, screenHeight);
                    break;
            }
            
            // Draw connection indicator (shown when connected outside main/connecting screens)
            if (NetworkManager.Instance.IsConnected && 
                _state != MenuState.MainMenu && 
                _state != MenuState.Connecting &&
                _state != MenuState.SongSelect) // the Freeplay browser uses the whole screen (fork)
            {
                _connectionIndicator?.SetFontRenderer(_fontRenderer);
                int topRightSafeY = _backButtonRect != Rectangle.Empty ? _backButtonRect.Bottom + 8 : 0;
                if (_gameplaySettingsRect != Rectangle.Empty && _gameplaySettingsRect.X >= screenWidth / 2)
                    topRightSafeY = Math.Max(topRightSafeY, _gameplaySettingsRect.Bottom + 8);
                if (_refreshButtonRect != Rectangle.Empty && _refreshButtonRect.X >= screenWidth / 2)
                    topRightSafeY = Math.Max(topRightSafeY, _refreshButtonRect.Bottom + 8);
                _connectionIndicator?.Draw(sb, font, pixel, screenWidth, screenHeight, topRightSafeY);
            }
            
            // Draw error toast overlay if present
            if (_errorMessageTimer > 0f && !string.IsNullOrEmpty(_errorMessage))
            {
                DrawToast(sb, font, pixel, screenWidth, screenHeight, _errorMessage, _errorMessageTimer, ACCENT_ERROR, new Color(36, 16, 24), "!", 0);
            }
            if (_infoMessageTimer > 0f && !string.IsNullOrEmpty(_infoMessage))
            {
                // Stack below the error toast when both are visible
                bool errorShown = _errorMessageTimer > 0f && !string.IsNullOrEmpty(_errorMessage);
                int offset = errorShown ? Px(84, GetUiScale(screenWidth, screenHeight)) : 0;
                DrawToast(sb, font, pixel, screenWidth, screenHeight, _infoMessage, _infoMessageTimer, ACCENT_SUCCESS, new Color(14, 34, 26), "i", offset);
            }
            
#if ANDROID
            // Draw touch controls
            _touchControls?.Draw(sb, font);
#endif
        }

        // ══════════════════════════════════════════════════════════════════════
        // Responsive Typography & Modern UI Primitives
        // ══════════════════════════════════════════════════════════════════════

        private static float GetUiScale(int sw, int sh) =>
            Math.Clamp(Math.Min(sw / 1280f, sh / 720f), 0.58f, 1.85f);

        private static int Px(float value, float scale, int min = 1) =>
            Math.Max(min, (int)MathF.Round(value * scale));

        /// <summary>
        /// Layout clamp that never throws: when the window is small enough that <paramref name="max"/>
        /// drops below <paramref name="min"/>, <paramref name="min"/> wins. Math.Clamp throws there, and a
        /// Draw exception closes the game (players resizing the window in the online menu hit it).
        /// </summary>
        private static int ClampUi(int value, int min, int max) => Math.Max(min, Math.Min(value, max));

        private static int FontPx(float baseSize, float scale, int min = 11, int max = 128) =>
            Math.Clamp((int)MathF.Round(baseSize * scale), min, max);

        private Vector2 MeasureText(SpriteFont? font, string text, int fontSize)
        {
            if (string.IsNullOrEmpty(text))
                return Vector2.Zero;

            if (_fontRenderer != null)
                return _fontRenderer.MeasureString(text, fontSize);

            if (font != null)
            {
                string safe = SanitizeText(font, text);
                return font.MeasureString(safe) * (fontSize / 26f);
            }

            return Vector2.Zero;
        }

        private void DrawText(SpriteBatch sb, SpriteFont? font, string text, Vector2 pos, Color color, int fontSize, bool shadow = false, float shadowOffset = 2f)
        {
            if (string.IsNullOrEmpty(text))
                return;

            if (shadow)
            {
                float a = (color.A / 255f) * 0.5f;
                Color shadowColor = Color.Black * a;
                Vector2 sPos = pos + new Vector2(shadowOffset, shadowOffset);
                if (_fontRenderer != null)
                {
                    _fontRenderer.DrawString(sb, text, sPos, shadowColor, fontSize);
                }
                else if (font != null)
                {
                    sb.DrawString(font, SanitizeText(font, text), sPos, shadowColor, 0f, Vector2.Zero, fontSize / 26f, SpriteEffects.None, 0f);
                }
            }

            if (_fontRenderer != null)
            {
                _fontRenderer.DrawString(sb, text, pos, color, fontSize);
            }
            else if (font != null)
            {
                sb.DrawString(font, SanitizeText(font, text), pos, color, 0f, Vector2.Zero, fontSize / 26f, SpriteEffects.None, 0f);
            }
        }

        private void DrawCenteredText(SpriteBatch sb, SpriteFont font, string text, int y, Color color, int fontSize = 18, int screenWidth = 0, bool shadow = true)
        {
            if (string.IsNullOrEmpty(text)) return;
            if (screenWidth <= 0) screenWidth = sb.GraphicsDevice.Viewport.Width;

            Vector2 size = MeasureText(font, text, fontSize);
            Vector2 pos = new Vector2(screenWidth / 2f - size.X / 2f, y);
            DrawText(sb, font, text, pos, color * _fadeAlpha, fontSize, shadow);
        }

        private void DrawCenteredTextInPanel(SpriteBatch sb, SpriteFont font, string text, int panelX, int y, int panelW, Color color, int fontSize = 18, bool shadow = false)
        {
            if (string.IsNullOrEmpty(text)) return;
            Vector2 size = MeasureText(font, text, fontSize);
            Vector2 pos = new Vector2(panelX + panelW / 2f - size.X / 2f, y);
            DrawText(sb, font, text, pos, color * _fadeAlpha, fontSize, shadow);
        }

        private string TruncateToWidth(SpriteFont? font, string text, float maxWidth, int fontSize)
        {
            if (string.IsNullOrEmpty(text) || maxWidth <= 20f)
                return text ?? "";

            if (MeasureText(font, text, fontSize).X <= maxWidth)
                return text;

            const string ellipsis = "...";
            int len = text.Length;
            while (len > 1 && MeasureText(font, text.Substring(0, len) + ellipsis, fontSize).X > maxWidth)
                len--;
            return len > 0 ? text.Substring(0, len) + ellipsis : ellipsis;
        }

        private string WrapTextToWidth(SpriteFont? font, string text, float maxWidth, int fontSize)
        {
            if (string.IsNullOrEmpty(text)) return "";
            var words = text.Replace('\n', ' ').Split(' ');
            var sb = new System.Text.StringBuilder();
            string line = "";
            foreach (var word in words)
            {
                string w = word;
                // A word wider than the line (links, "aaaaaa...") is cut by characters; it used to run
                // off the chat panel because lines only broke at spaces.
                while (MeasureText(font, w, fontSize).X > maxWidth && w.Length > 1)
                {
                    if (line.Length > 0)
                    {
                        if (sb.Length > 0) sb.Append('\n');
                        sb.Append(line);
                        line = "";
                    }
                    int fit = 1;
                    while (fit < w.Length && MeasureText(font, w[..(fit + 1)], fontSize).X <= maxWidth) fit++;
                    if (sb.Length > 0) sb.Append('\n');
                    sb.Append(w[..fit]);
                    w = w[fit..];
                }
                string candidate = line.Length == 0 ? w : $"{line} {w}";
                if (MeasureText(font, candidate, fontSize).X > maxWidth && line.Length > 0)
                {
                    if (sb.Length > 0) sb.Append('\n');
                    sb.Append(line);
                    line = w;
                }
                else
                {
                    line = candidate;
                }
            }
            if (line.Length > 0)
            {
                if (sb.Length > 0) sb.Append('\n');
                sb.Append(line);
            }
            return sb.ToString();
        }

        private void DrawBorder(SpriteBatch sb, Texture2D pixel, int x, int y, int w, int h, Color color, int thickness)
        {
            if (w <= 0 || h <= 0 || thickness <= 0) return;
            int t = Math.Min(thickness, Math.Min(w, h) / 2);
            if (t <= 0) t = 1;
            sb.Draw(pixel, new Rectangle(x, y, w, t), color);
            sb.Draw(pixel, new Rectangle(x, y + h - t, w, t), color);
            sb.Draw(pixel, new Rectangle(x, y, t, h), color);
            sb.Draw(pixel, new Rectangle(x + w - t, y, t, h), color);
        }

        private void DrawGradientBackground(SpriteBatch sb, Texture2D pixel, int sw, int sh, float uiScale = 1f)
        {
            // Dark studio slate gradient matching FreeplayMenu / MainSettingsMenu
            const int steps = 32;
            Color top = new Color(14, 14, 24);
            Color bottom = new Color(22, 20, 34);
            for (int i = 0; i < steps; i++)
            {
                float ratio = i / (float)(steps - 1);
                Color color = Color.Lerp(top, bottom, ratio);
                int y = (int)MathF.Floor(sh * i / (float)steps);
                int nextY = (int)MathF.Ceiling(sh * (i + 1) / (float)steps);
                sb.Draw(pixel, new Rectangle(0, y, sw, Math.Max(1, nextY - y)), color);
            }

            // Subtle ambient header band
            int glowH = Px(130, uiScale);
            sb.Draw(pixel, new Rectangle(0, 0, sw, glowH), ACCENT_PRIMARY * 0.03f);

            // Clean architectural grid lines (same subtle grid motif as FreeplayMenu)
            int gridStep = Px(110, uiScale, 60);
            for (int y = 0; y < sh; y += gridStep)
                sb.Draw(pixel, new Rectangle(0, y, sw, 1), Color.White * 0.022f);
            for (int x = 0; x < sw; x += gridStep)
                sb.Draw(pixel, new Rectangle(x, 0, 1, sh), Color.White * 0.022f);
        }

        private void DrawPanel(SpriteBatch sb, Texture2D pixel, int x, int y, int w, int h, Color bgColor, Color borderColor, int borderWidth = 1, Color? leftAccent = null)
        {
            if (w <= 0 || h <= 0) return;

            // Soft layered drop shadow
            sb.Draw(pixel, new Rectangle(x + 4, y + 5, w, h), Color.Black * (0.24f * _fadeAlpha));
            sb.Draw(pixel, new Rectangle(x + 2, y + 2, w, h), Color.Black * (0.14f * _fadeAlpha));

            // Surface body
            sb.Draw(pixel, new Rectangle(x, y, w, h), bgColor * _fadeAlpha);

            // Subtle top highlight sheen
            sb.Draw(pixel, new Rectangle(x + 1, y + 1, Math.Max(0, w - 2), 1), Color.White * (0.055f * _fadeAlpha));

            // Clean outer border
            DrawBorder(sb, pixel, x, y, w, h, borderColor * _fadeAlpha, borderWidth);

            // Optional crisp accent bar on the left edge
            if (leftAccent.HasValue)
            {
                int barW = Math.Min(4, Math.Max(2, borderWidth + 2));
                sb.Draw(pixel, new Rectangle(x, y, barW, h), leftAccent.Value * _fadeAlpha);
            }
        }

        private int DrawPillBadge(SpriteBatch sb, SpriteFont? font, Texture2D pixel, int x, int y, int h, string text, Color accent, int fontSize, int padX = 12, bool filledStrong = false)
        {
            Vector2 sz = MeasureText(font, text, fontSize);
            int w = (int)MathF.Ceiling(sz.X) + padX * 2;
            Color bg = accent * ((filledStrong ? 0.28f : 0.14f) * _fadeAlpha);
            Color border = accent * ((filledStrong ? 0.85f : 0.55f) * _fadeAlpha);
            sb.Draw(pixel, new Rectangle(x, y, w, h), bg);
            DrawBorder(sb, pixel, x, y, w, h, border, 1);

            Color textCol = Color.Lerp(accent, Color.White, filledStrong ? 0.72f : 0.45f) * _fadeAlpha;
            DrawText(sb, font, text, new Vector2(x + (w - sz.X) / 2f, y + (h - sz.Y) / 2f), textCol, fontSize);
            return w;
        }

        private void DrawScrollBar(SpriteBatch sb, Texture2D pixel, int trackX, int trackY, int trackH, int totalItems, int visibleCount, int scrollOffset, Color accent)
        {
            if (totalItems <= visibleCount || trackH <= 16) return;

            const int trackW = 6;
            sb.Draw(pixel, new Rectangle(trackX, trackY, trackW, trackH), Color.White * (0.07f * _fadeAlpha));

            float visibleRatio = Math.Clamp(visibleCount / (float)totalItems, 0.12f, 1f);
            int thumbH = Math.Max(24, (int)MathF.Round(trackH * visibleRatio));
            int maxScroll = Math.Max(1, totalItems - visibleCount);
            float scrollRatio = Math.Clamp(scrollOffset / (float)maxScroll, 0f, 1f);
            int thumbY = trackY + (int)MathF.Round((trackH - thumbH) * scrollRatio);

            sb.Draw(pixel, new Rectangle(trackX, thumbY, trackW, thumbH), accent * (0.78f * _fadeAlpha));
            sb.Draw(pixel, new Rectangle(trackX, thumbY, trackW, 2), Color.White * (0.35f * _fadeAlpha));
        }

        private void DrawGameplaySettingsButton(SpriteBatch sb, SpriteFont font, Texture2D pixel, int x, int y, float uiScale)
        {
            string key = Controls.Hint(HotkeyAction.MultiplayerSettings);
            string label = key == "-" ? "Settings" : $"{key}  Settings";
            int fontSize = FontPx(13, uiScale, 10, 18);
            Vector2 sz = MeasureText(font, label, fontSize);
            int btnW = (int)MathF.Ceiling(sz.X) + Px(24, uiScale);
            int btnH = Px(32, uiScale, 24);
            _gameplaySettingsRect = new Rectangle(x, y, btnW, btnH);

            bool hovered = _gameplaySettingsRect.Contains(_lastMousePos);
            Color bg = hovered ? BG_LIGHT : BG_MEDIUM;
            Color border = hovered ? ACCENT_PRIMARY : ACCENT_PRIMARY * 0.55f;
            DrawPanel(sb, pixel, x, y, btnW, btnH, bg, border, hovered ? 2 : 1, ACCENT_PRIMARY);

            Vector2 pos = new Vector2(x + (btnW - sz.X) / 2f, y + (btnH - sz.Y) / 2f);
            DrawText(sb, font, label, pos, (hovered ? Color.White : ACCENT_PRIMARY) * _fadeAlpha, fontSize);
        }

        private void DrawGameplaySettingsButtonTopRight(SpriteBatch sb, SpriteFont font, Texture2D pixel, int screenWidth, float uiScale)
        {
            string key = Controls.Hint(HotkeyAction.MultiplayerSettings);
            string label = key == "-" ? "Settings" : $"{key}  Settings";
            int fontSize = FontPx(13, uiScale, 10, 18);
            Vector2 sz = MeasureText(font, label, fontSize);
            int btnW = (int)MathF.Ceiling(sz.X) + Px(24, uiScale);
            int btnH = Px(32, uiScale, 24);

            int gap = Px(10, uiScale, 6);
            int margin = Px(24, uiScale, 12);
            int btnX = _backButtonRect != Rectangle.Empty
                ? _backButtonRect.Left - btnW - gap
                : screenWidth - btnW - margin;
            int btnY = _backButtonRect != Rectangle.Empty
                ? _backButtonRect.Y + (_backButtonRect.Height - btnH) / 2
                : Px(18, uiScale, 10);

            if (btnX < screenWidth / 2 + Px(140, uiScale) && _backButtonRect != Rectangle.Empty)
            {
                btnX = _backButtonRect.Right - btnW;
                btnY = _backButtonRect.Bottom + Px(6, uiScale);
            }

            DrawGameplaySettingsButton(sb, font, pixel, btnX, btnY, uiScale);
        }

        private void DrawBackButton(SpriteBatch sb, SpriteFont font, Texture2D pixel, int screenWidth, float uiScale, string label = "ESC  Back")
        {
            int fontSize = FontPx(14, uiScale, 11, 20);
            Vector2 sz = MeasureText(font, label, fontSize);
            int btnW = Math.Max(Px(120, uiScale), (int)sz.X + Px(28, uiScale));
            int btnH = Px(34, uiScale, 28);
            int margin = Px(24, uiScale, 12);
            int btnX = screenWidth - btnW - margin;
            int btnY = Px(18, uiScale, 10);
            _backButtonRect = new Rectangle(btnX, btnY, btnW, btnH);

            bool hovered = _backButtonRect.Contains(_lastMousePos);
            Color bg = hovered ? BG_LIGHT : BG_MEDIUM;
            Color border = hovered ? ACCENT_WARNING : BORDER_SUBTLE;
            DrawPanel(sb, pixel, btnX, btnY, btnW, btnH, bg, border, hovered ? 2 : 1);

            Vector2 pos = new Vector2(btnX + btnW / 2f - sz.X / 2f, btnY + btnH / 2f - sz.Y / 2f);
            DrawText(sb, font, label, pos, (hovered ? Color.White : TEXT_SECONDARY) * _fadeAlpha, fontSize);
        }

        private void DrawPlayerIdentityChip(SpriteBatch sb, SpriteFont font, Texture2D pixel, int x, int y, float uiScale, bool alignRight = false)
        {
            var settings = MultiplayerSettings.Instance;
            Color myColor = settings.GetPlayerColor();
            string username = string.IsNullOrWhiteSpace(settings.Username) ? "Player" : settings.Username;
            bool isOnline = NetworkManager.Instance.IsConnected;

            int fontSize = FontPx(15, uiScale, 11, 22);
            int subSize = FontPx(11, uiScale, 10, 16);
            Vector2 nameSz = MeasureText(font, username, fontSize);
            string statusTxt = isOnline ? "ONLINE" : "READY";
            Vector2 statusSz = MeasureText(font, statusTxt, subSize);

            int swatchSize = Px(20, uiScale, 14);
            int chipH = Px(38, uiScale, 28);
            int pad = Px(10, uiScale, 8);
            int chipW = pad + swatchSize + Px(10, uiScale) + (int)MathF.Ceiling(nameSz.X) + Px(12, uiScale) + (int)MathF.Ceiling(statusSz.X) + pad;
            if (alignRight)
                x -= chipW;

            DrawPanel(sb, pixel, x, y, chipW, chipH, BG_MEDIUM, BORDER_SUBTLE, 1, myColor);

            int swatchY = y + (chipH - swatchSize) / 2;
            sb.Draw(pixel, new Rectangle(x + pad, swatchY, swatchSize, swatchSize), myColor * _fadeAlpha);
            DrawBorder(sb, pixel, x + pad, swatchY, swatchSize, swatchSize, Color.White * (0.4f * _fadeAlpha), 1);

            float textX = x + pad + swatchSize + Px(10, uiScale);
            DrawText(sb, font, username, new Vector2(textX, y + (chipH - nameSz.Y) / 2f), Color.Lerp(myColor, Color.White, 0.35f) * _fadeAlpha, fontSize);

            int badgeX = (int)(textX + nameSz.X + Px(10, uiScale));
            Color statusCol = isOnline ? ACCENT_SUCCESS : ACCENT_PRIMARY;
            int dot = Px(6, uiScale, 5);
            sb.Draw(pixel, new Rectangle(badgeX, y + (chipH - dot) / 2, dot, dot), statusCol * _fadeAlpha);
            DrawText(sb, font, statusTxt, new Vector2(badgeX + dot + 5, y + (chipH - statusSz.Y) / 2f), statusCol * _fadeAlpha, subSize);
        }

        private int DrawScreenHeader(SpriteBatch sb, SpriteFont font, Texture2D pixel, int sw, float uiScale, string title, string? subtitle, Color accent, string backLabel = "ESC  Back")
        {
            DrawBackButton(sb, font, pixel, sw, uiScale, backLabel);

            int titleSize = FontPx(30, uiScale, 18, 44);
            Vector2 tSz = MeasureText(font, title, titleSize);
            int titleY = Px(18, uiScale, 12);
            DrawText(sb, font, title, new Vector2(sw / 2f - tSz.X / 2f, titleY), accent * _fadeAlpha, titleSize, shadow: true);

            // Freeplay-style underline bar with dual end caps
            int lineY = titleY + (int)MathF.Ceiling(tSz.Y) + Px(5, uiScale, 3);
            int lineW = Math.Max(Px(160, uiScale), Math.Min((int)(tSz.X * 1.15f), sw / 2));
            int lineX = sw / 2 - lineW / 2;
            int lineH = Math.Max(2, Px(2, uiScale));
            sb.Draw(pixel, new Rectangle(lineX, lineY, lineW, lineH), accent * (0.55f * _fadeAlpha));
            int capW = Px(6, uiScale, 4);
            int capGap = Px(6, uiScale, 4);
            sb.Draw(pixel, new Rectangle(lineX - capW - capGap, lineY, capW, lineH), ACCENT_WARNING * (0.85f * _fadeAlpha));
            sb.Draw(pixel, new Rectangle(lineX + lineW + capGap, lineY, capW, lineH), ACCENT_WARNING * (0.85f * _fadeAlpha));

            int bottomY = lineY + lineH + Px(6, uiScale, 4);
            if (!string.IsNullOrEmpty(subtitle))
            {
                int subSize = FontPx(14, uiScale, 11, 20);
                Vector2 sSz = MeasureText(font, subtitle, subSize);
                DrawText(sb, font, subtitle, new Vector2(sw / 2f - sSz.X / 2f, bottomY), TEXT_SECONDARY * _fadeAlpha, subSize);
                bottomY += (int)MathF.Ceiling(sSz.Y) + Px(6, uiScale, 4);
            }

            return bottomY;
        }

        private int DrawFooterBar(SpriteBatch sb, SpriteFont font, Texture2D pixel, int sw, int sh, float uiScale, string hintText)
        {
            int footerH = Px(36, uiScale, 28);
            int footerY = sh - footerH;
            sb.Draw(pixel, new Rectangle(0, footerY, sw, footerH), new Color(10, 10, 18) * (0.78f * _fadeAlpha));
            sb.Draw(pixel, new Rectangle(0, footerY, sw, 1), Color.White * (0.07f * _fadeAlpha));

            int fontSize = FontPx(13, uiScale, 10, 18);
            Vector2 sz = MeasureText(font, hintText, fontSize);
            while (sz.X > sw - 32 && fontSize > 10)
            {
                fontSize--;
                sz = MeasureText(font, hintText, fontSize);
            }

            DrawText(sb, font, hintText, new Vector2(sw / 2f - sz.X / 2f, footerY + (footerH - sz.Y) / 2f), TEXT_SECONDARY * (0.78f * _fadeAlpha), fontSize);
            return footerY;
        }

        private void DrawMenuIcon(SpriteBatch sb, Texture2D pixel, int cx, int cy, int size, int kind, Color color)
        {
            string? iconKey = kind switch
            {
                0 => "connect",
                1 => "leaderboard",
                2 => "settings",
                3 => "back",
                4 => "lock",
                5 => "create",
                6 => "join",
                7 => "quickplay",
                _ => null
            };
            if (iconKey != null && _menuIcons.TryGetValue(iconKey, out var tex) && tex != null)
            {
                int drawSize = Math.Max(14, size);
                Rectangle dest = new Rectangle(cx - drawSize / 2, cy - drawSize / 2, drawSize, drawSize);
                sb.Draw(tex, dest, color * _fadeAlpha);
                return;
            }

            // Minimal fallback ONLY if a PNG icon file failed to load
            Color c = color * _fadeAlpha;
            int r = Math.Max(6, size / 2);
            sb.Draw(pixel, new Rectangle(cx - r / 2, cy - r / 2, r, r), c * 0.65f);
        }
        
        /// <summary>
        /// Draw a toast (error or notice) at the top of the screen.
        /// </summary>
        private void DrawToast(SpriteBatch sb, SpriteFont font, Texture2D pixel, int screenWidth, int screenHeight,
            string message, float timer, Color accent, Color background, string glyph, int yOffset)
        {
            float uiScale = GetUiScale(screenWidth, screenHeight);
            float alpha = Math.Min(1f, timer / 0.35f);
            if (timer < 0.5f) alpha = Math.Min(alpha, timer / 0.5f);

            int fontSize = FontPx(16, uiScale, 12, 24);
            int maxW = Math.Clamp(Px(620, uiScale), 280, Math.Max(280, screenWidth - 48));
            string wrapped = WrapTextToWidth(font, message, maxW - Px(72, uiScale), fontSize);
            Vector2 textSz = MeasureText(font, wrapped, fontSize);

            int toastW = ClampUi((int)MathF.Ceiling(textSz.X) + Px(84, uiScale), Px(340, uiScale), maxW);
            int toastH = Math.Max(Px(54, uiScale), (int)MathF.Ceiling(textSz.Y) + Px(24, uiScale));
            int toastX = screenWidth / 2 - toastW / 2;
            int toastY = Px(20, uiScale, 12) + yOffset;

            sb.Draw(pixel, new Rectangle(toastX + 3, toastY + 4, toastW, toastH), Color.Black * (0.4f * alpha));
            sb.Draw(pixel, new Rectangle(toastX, toastY, toastW, toastH), background * (0.95f * alpha));
            DrawBorder(sb, pixel, toastX, toastY, toastW, toastH, accent * alpha, 2);
            sb.Draw(pixel, new Rectangle(toastX, toastY, 4, toastH), accent * alpha);

            // Badge ("!" for errors, "i" for notices)
            int badgeSize = Px(26, uiScale, 18);
            int badgeX = toastX + Px(16, uiScale);
            int badgeY = toastY + (toastH - badgeSize) / 2;
            sb.Draw(pixel, new Rectangle(badgeX, badgeY, badgeSize, badgeSize), accent * (0.25f * alpha));
            DrawBorder(sb, pixel, badgeX, badgeY, badgeSize, badgeSize, accent * alpha, 1);
            Vector2 glyphSz = MeasureText(font, glyph, fontSize);
            DrawText(sb, font, glyph, new Vector2(badgeX + (badgeSize - glyphSz.X) / 2f, badgeY + (badgeSize - glyphSz.Y) / 2f), accent * alpha, fontSize);

            Vector2 textPos = new Vector2(badgeX + badgeSize + Px(14, uiScale), toastY + (toastH - textSz.Y) / 2f);
            DrawText(sb, font, wrapped, textPos, TEXT_PRIMARY * alpha, fontSize, shadow: true);
        }

        private void DrawCard(SpriteBatch sb, SpriteFont font, Texture2D pixel, int x, int y, int w, int h, string title, string subtitle, bool selected, bool glowing = false, Color? accentOverride = null)
        {
            Color accent = accentOverride ?? ACCENT_PRIMARY;
            Color bgColor = selected ? BG_LIGHT : BG_MEDIUM;
            Color borderColor = selected ? accent : BORDER_SUBTLE;

            if (glowing && selected)
            {
                float pulse = (float)Math.Sin(_pulseTimer * 1.4f) * 0.25f + 0.75f;
                Color glowColor = accent * (0.14f * pulse * _fadeAlpha);
                sb.Draw(pixel, new Rectangle(x - 5, y - 5, w + 10, h + 10), glowColor);
                sb.Draw(pixel, new Rectangle(x - 2, y - 2, w + 4, h + 4), glowColor * 0.7f);
            }

            DrawPanel(sb, pixel, x, y, w, h, bgColor, borderColor, selected ? 2 : 1, selected ? accent : null);

            if (!string.IsNullOrEmpty(title))
            {
                int titleSize = FontPx(20, Math.Clamp(h / 90f, 0.75f, 1.35f), 14, 28);
                Vector2 tSz = MeasureText(font, title, titleSize);
                bool hasSub = !string.IsNullOrEmpty(subtitle);
                float titleY = hasSub ? (y + h / 2f - tSz.Y - 2) : (y + (h - tSz.Y) / 2f);
                DrawText(sb, font, title, new Vector2(x + w / 2f - tSz.X / 2f, titleY), (selected ? accent : TEXT_PRIMARY) * _fadeAlpha, titleSize, shadow: true);
            }

            if (!string.IsNullOrEmpty(subtitle))
            {
                int subSize = FontPx(14, Math.Clamp(h / 90f, 0.75f, 1.3f), 11, 20);
                Vector2 sSz = MeasureText(font, subtitle, subSize);
                DrawText(sb, font, subtitle, new Vector2(x + w / 2f - sSz.X / 2f, y + h / 2f + 4), TEXT_SECONDARY * _fadeAlpha, subSize);
            }
        }

        private void DrawMainMenuModern(SpriteBatch sb, SpriteFont font, Texture2D pixel, int screenWidth, int screenHeight)
        {
            _clickableAreas.Clear();
            float uiScale = GetUiScale(screenWidth, screenHeight);

            DrawPlayerIdentityChip(sb, font, pixel, Px(24, uiScale, 12), Px(18, uiScale, 10), uiScale);
            DrawGameplaySettingsButton(sb, font, pixel, Px(24, uiScale, 12), Px(64, uiScale, 44), uiScale);

            // Hero Header
            int heroTop = Px(74, uiScale, 52);
            int titleSize = FontPx(44, uiScale, 26, 68);
            string title = "MULTIPLAYER";
            Vector2 titleSz = MeasureText(font, title, titleSize);
            float titleX = screenWidth / 2f - titleSz.X / 2f;

            // Subtle cyan/magenta accent echo behind hero title
            DrawText(sb, font, title, new Vector2(titleX + 3, heroTop + 3), new Color(255, 68, 136) * (0.18f * _fadeAlpha), titleSize);
            float titlePulse = (float)Math.Sin(_pulseTimer * 0.7f) * 0.1f + 0.9f;
            DrawText(sb, font, title, new Vector2(titleX, heroTop), ACCENT_PRIMARY * titlePulse * _fadeAlpha, titleSize, shadow: true);

            int accentY = heroTop + (int)MathF.Ceiling(titleSz.Y) + Px(6, uiScale);
            int lineW = ClampUi((int)(titleSz.X * 0.92f), Px(180, uiScale), screenWidth - 80);
            int lineX = screenWidth / 2 - lineW / 2;
            sb.Draw(pixel, new Rectangle(lineX, accentY, lineW, Math.Max(2, Px(3, uiScale))), ACCENT_PRIMARY * (0.55f * _fadeAlpha));

            string subtitle = "Rooms for 2-8 players: Versus, Teams & Mirror, global rankings";
            int subSize = FontPx(15, uiScale, 12, 22);
            Vector2 subSz = MeasureText(font, subtitle, subSize);
            int subY = accentY + Px(12, uiScale);
            DrawText(sb, font, subtitle, new Vector2(screenWidth / 2f - subSz.X / 2f, subY), TEXT_SECONDARY * _fadeAlpha, subSize);

            int footerY = DrawFooterBar(sb, font, pixel, screenWidth, screenHeight, uiScale,
                "UP/DOWN or MOUSE: Navigate  |  ENTER or CLICK: Select  |  ESC: Back");

            // Vertical stack of 4 sleek option cards framed cleanly between hero and footer
            int contentTop = subY + (int)MathF.Ceiling(subSz.Y) + Px(20, uiScale);
            int availableH = Math.Max(200, footerY - contentTop - Px(16, uiScale));
            int cardSpacing = Math.Clamp(Px(14, uiScale), 8, 22);
            int cardHeight = ClampUi((availableH - cardSpacing * 3) / 4, 48, Px(84, uiScale));
            int totalStackH = cardHeight * 4 + cardSpacing * 3;
            int startY = contentTop + Math.Max(0, (availableH - totalStackH) / 2);
            int cardWidth = ClampUi(Px(460, uiScale), 300, screenWidth - Px(60, uiScale));
            int cardX = screenWidth / 2 - cardWidth / 2;

            string[] options = { "CONNECT TO SERVER", "LEADERBOARD", "SETTINGS", "BACK TO MENU" };
            string[] sublabels =
            {
                "Browse rooms or create your own",
                "Global top players, match history & your stats",
                "Profile name, player color, HUD & sync options",
                "Return to the main title menu"
            };
            int[] iconKinds = { 0, 1, 2, 3 };
            Color[] accentColors = { ACCENT_SUCCESS, ACCENT_PRIMARY, ACCENT_WARNING, ACCENT_ERROR };

            int optTitleSize = FontPx(20, uiScale, 14, 28);
            int optSubSize = FontPx(13, uiScale, 10, 18);

            for (int i = 0; i < options.Length; i++)
            {
                bool selected = i == _selection;
                int cardY = startY + i * (cardHeight + cardSpacing);
                _clickableAreas.Add(new Rectangle(cardX, cardY, cardWidth, cardHeight));

                DrawCard(sb, font, pixel, cardX, cardY, cardWidth, cardHeight, "", "", selected, glowing: true, accentOverride: accentColors[i]);

                // Icon badge box on left
                int iconBoxSize = ClampUi(cardHeight - Px(22, uiScale), 28, Px(52, uiScale));
                int iconBoxX = cardX + Px(18, uiScale);
                int iconBoxY = cardY + (cardHeight - iconBoxSize) / 2;
                Color cAccent = accentColors[i];
                sb.Draw(pixel, new Rectangle(iconBoxX, iconBoxY, iconBoxSize, iconBoxSize), cAccent * ((selected ? 0.22f : 0.1f) * _fadeAlpha));
                DrawBorder(sb, pixel, iconBoxX, iconBoxY, iconBoxSize, iconBoxSize, cAccent * ((selected ? 0.85f : 0.35f) * _fadeAlpha), 1);
                DrawMenuIcon(sb, pixel, iconBoxX + iconBoxSize / 2, iconBoxY + iconBoxSize / 2, iconBoxSize - Px(12, uiScale), iconKinds[i], selected ? cAccent : cAccent * 0.7f);

                // Title & subtitle
                int textX = iconBoxX + iconBoxSize + Px(16, uiScale);
                Vector2 tSz = MeasureText(font, options[i], optTitleSize);
                Vector2 sSz = MeasureText(font, sublabels[i], optSubSize);
                float textBlockH = tSz.Y + sSz.Y + 2f;
                float topTextY = cardY + (cardHeight - textBlockH) / 2f;

                DrawText(sb, font, options[i], new Vector2(textX, topTextY), (selected ? TEXT_PRIMARY : TEXT_SECONDARY) * _fadeAlpha, optTitleSize, shadow: selected);
                DrawText(sb, font, sublabels[i], new Vector2(textX, topTextY + tSz.Y + 2f), (selected ? TEXT_SECONDARY : TEXT_DISABLED) * _fadeAlpha, optSubSize);

                // Right chevron on selected card
                if (selected)
                {
                    Vector2 arrowSz = MeasureText(font, ">", optTitleSize);
                    DrawText(sb, font, ">", new Vector2(cardX + cardWidth - Px(28, uiScale) - arrowSz.X, cardY + (cardHeight - arrowSz.Y) / 2f), cAccent * _fadeAlpha, optTitleSize);
                }
            }
        }

        private void DrawConnectingModern(SpriteBatch sb, SpriteFont font, Texture2D pixel, int screenWidth, int screenHeight)
        {
            float uiScale = GetUiScale(screenWidth, screenHeight);
            DrawBackButton(sb, font, pixel, screenWidth, uiScale, "ESC  Cancel");

            int panelW = ClampUi(Px(520, uiScale), 300, screenWidth - 40);
            int panelH = Px(250, uiScale, 190);
            int panelX = screenWidth / 2 - panelW / 2;
            int panelY = screenHeight / 2 - panelH / 2;

            DrawPanel(sb, pixel, panelX, panelY, panelW, panelH, BG_MEDIUM, ACCENT_PRIMARY, 2, ACCENT_PRIMARY);

            // Animated 4-node note-colored pulse loader
            Color[] nodeColors =
            {
                new Color(194, 75, 153),
                new Color(0, 255, 255),
                new Color(18, 250, 5),
                new Color(249, 57, 63)
            };
            int nodeCount = 4;
            int baseNodeSize = Px(14, uiScale, 10);
            int nodeGap = Px(14, uiScale, 8);
            int loaderW = nodeCount * baseNodeSize + (nodeCount - 1) * nodeGap;
            int loaderStartX = screenWidth / 2 - loaderW / 2;
            int loaderY = panelY + Px(42, uiScale);

            for (int i = 0; i < nodeCount; i++)
            {
                float wave = MathF.Sin(_searchTimer * 6f - i * 0.9f) * 0.5f + 0.5f;
                int sz = baseNodeSize + (int)MathF.Round(wave * Px(6, uiScale));
                int nx = loaderStartX + i * (baseNodeSize + nodeGap) + (baseNodeSize - sz) / 2;
                int ny = loaderY + (baseNodeSize - sz) / 2 - (int)MathF.Round(wave * Px(8, uiScale));
                sb.Draw(pixel, new Rectangle(nx, ny, sz, sz), nodeColors[i] * ((0.45f + 0.55f * wave) * _fadeAlpha));
            }

            int dotCount = ((int)(_searchTimer * 2.5f) % 4);
            string text = $"Connecting to server{new string('.', dotCount)}";
            int titleSize = FontPx(22, uiScale, 15, 32);
            DrawCenteredTextInPanel(sb, font, text, panelX, panelY + Px(86, uiScale), panelW, TEXT_PRIMARY, titleSize, shadow: true);

            if (_connectionAttempts > 0)
            {
                string attemptText = $"Attempt {_connectionAttempts} / {MAX_CONNECTION_ATTEMPTS}";
                DrawCenteredTextInPanel(sb, font, attemptText, panelX, panelY + Px(120, uiScale), panelW, ACCENT_WARNING, FontPx(14, uiScale, 11, 20));
            }

            // Progress bar
            int barW = ClampUi(panelW - Px(96, uiScale), 180, Px(380, uiScale));
            int barH = Px(10, uiScale, 8);
            int barX = screenWidth / 2 - barW / 2;
            int barY = panelY + Px(160, uiScale);

            sb.Draw(pixel, new Rectangle(barX, barY, barW, barH), Color.Black * (0.55f * _fadeAlpha));
            DrawBorder(sb, pixel, barX, barY, barW, barH, BORDER_SUBTLE * _fadeAlpha, 1);

            float progress = (_searchTimer % 1.4f) / 1.4f;
            int segW = Math.Max(Px(48, uiScale), barW / 3);
            int segX = barX + (int)((barW + segW) * progress) - segW;
            int clippedLeft = Math.Max(barX, segX);
            int clippedRight = Math.Min(barX + barW, segX + segW);
            if (clippedRight > clippedLeft)
            {
                Color fillCol = Color.Lerp(ACCENT_PRIMARY, ACCENT_SUCCESS, progress);
                sb.Draw(pixel, new Rectangle(clippedLeft, barY, clippedRight - clippedLeft, barH), fillCol * _fadeAlpha);
            }

            string statusText;
            if (_connectionRetryTimer > 0f)
                statusText = $"Retrying connection in {Math.Max(1, (int)MathF.Ceiling(_connectionRetryTimer))}s...";
            else if (_connectionAttempts > 1)
                statusText = "Retrying connection...";
            else
                statusText = "Authenticating & syncing server catalog...";
            DrawCenteredTextInPanel(sb, font, statusText, panelX, panelY + Px(190, uiScale), panelW, TEXT_SECONDARY, FontPx(14, uiScale, 11, 20));

            DrawFooterBar(sb, font, pixel, screenWidth, screenHeight, uiScale, "ESC or CLICK Cancel: Abort connection");
        }

        /// <summary>
        /// Reset input cooldown (called when menu is opened).
        /// </summary>
        public void ResetInputCooldown()
        {
            _inputCooldown = INPUT_COOLDOWN_TIME;
            _prevKb = LayoutKeyboard.GetState();
            _prevMouse = Game1.NeutralMouseState(Mouse.GetState());
            _hoveredButton = -1;
            _mouseMovedThisFrame = false;
        }

        /// <summary>
        /// Syncs input snapshots when window regains focus without clearing state.
        /// </summary>
        public void SyncInputState()
        {
            _inputCooldown = Math.Max(_inputCooldown, INPUT_COOLDOWN_TIME);
            _prevKb = LayoutKeyboard.GetState();
            _prevMouse = Game1.NeutralMouseState(Mouse.GetState());
            _lastMousePos = _prevMouse.Position;
            _hoveredButton = -1;
            _mouseMovedThisFrame = false;
        }

        /// <summary>
        /// Builds the current Discord Rich Presence payload for the active Multiplayer Menu state.
        /// </summary>
        public (string Details, string? State, string? PartyId, int PartySize, int PartyMax) GetDiscordPresenceInfo()
        {
            var room = RoomClient.Instance;
            switch (_state)
            {
                case MenuState.Connecting:
                    return ("Multiplayer", "Connecting to server", null, 0, 2);
                case MenuState.RoomBrowser:
                    return ("Multiplayer", "Browsing rooms", null, 0, 2);
                case MenuState.RoomSettings when !room.InRoom:
                    return ("Multiplayer", "Creating a room", null, 0, 2);
                case MenuState.WaitingRoom:
                case MenuState.SongSelect:
                case MenuState.RoomSettings:
                    var st = room.State;
                    if (st == null) return ("Multiplayer", "In a room", null, 0, 2);
                    string what = st.Song != null ? $"Next: {st.Song.Name}"
                        : _state == MenuState.SongSelect ? "Picking a song" : "Waiting room";
                    return ($"Room: {st.Name} ({RoomClient.ModeName(st.Settings.Mode)})", what, $"room_{st.RoomId}",
                        st.Members.Count, st.Settings.MaxPlayers);
                case MenuState.Leaderboard:
                    return ("Multiplayer", "Viewing Leaderboard", null, 0, 2);
                case MenuState.Settings:
                    return ("Multiplayer", "Multiplayer Settings", null, 0, 2);
                default:
                    return ("Multiplayer", "In Multiplayer Menu", null, 0, 2);
            }
        }

        /// <summary>
        /// Check if we should exit to freeplay.
        /// </summary>
        public bool ShouldExitToFreeplay()
        {
            return _wantsExit;
        }
        
        /// <summary>
        /// Reset exit flag (called after returning to freeplay).
        /// </summary>
        public void ResetExitFlag()
        {
            _wantsExit = false;
        }

        /// <summary>
        /// Reset menu state after a multiplayer match ends.
        /// </summary>
        public void ResetAfterMatch()
        {
            RoomClient.Instance.LeftMatchScreen();
            if (RoomClient.Instance.InRoom) TransitionToState(MenuState.WaitingRoom);
            else if (NetworkManager.Instance.IsConnected) OpenRoomBrowser();
            else TransitionToState(MenuState.MainMenu);
            RequestLeaderboardRefresh();
            Console.WriteLine("[MultiplayerMenu] Back from the match");
        }

        /// <summary>
        /// Clean up resources.
        /// </summary>
        public void Cleanup()
        {
            StopWatchingLevels();
            RoomClient.Instance.LeaveRoom();
            UnhookRoomEvents();
            NetworkManager.Instance.OnConnected -= OnConnected;
            NetworkManager.Instance.OnDisconnected -= OnDisconnected;
            NetworkManager.Instance.OnConnectionError -= OnNetworkConnectionError;
            NetworkManager.Instance.OnLevelListReceived -= OnLevelListReceived;
            NetworkManager.Instance.OnLeaderboardReceived -= OnLeaderboardReceived;
            NetworkManager.Instance.OnMatchHistoryReceived -= OnMatchHistoryReceived;
            
            // Clear all cached official levels from memory
            LevelCache.Instance.ClearCache();
        }
        
        private void DrawSettingsModern(SpriteBatch sb, SpriteFont font, Texture2D pixel, int screenWidth, int screenHeight)
        {
            var settings = MultiplayerSettings.Instance;
            float uiScale = GetUiScale(screenWidth, screenHeight);

            DrawBackButton(sb, font, pixel, screenWidth, uiScale, "ESC  Back");
            DrawGameplaySettingsButtonTopRight(sb, font, pixel, screenWidth, uiScale);

            // Title
            int titleFont = FontPx(28, uiScale, 18, 40);
            string title = "MULTIPLAYER SETTINGS";
            Vector2 titleSize = MeasureText(font, title, titleFont);
            int titleY = Px(16, uiScale, 10);
            DrawText(sb, font, title, new Vector2(screenWidth / 2f - titleSize.X / 2f, titleY), ACCENT_WARNING * _fadeAlpha, titleFont, shadow: true);

            // Category tabs (4 active categories only)
            string[] categories = { "USER", "GAMEPLAY", "PERFORMANCE", "VISUAL" };
            int tabSpacing = Px(12, uiScale, 6);
            int maxTabBarW = ClampUi(Px(860, uiScale), 340, screenWidth - Px(60, uiScale));
            int tabW = Math.Min(Px(190, uiScale), (maxTabBarW - tabSpacing * (categories.Length - 1)) / categories.Length);
            int tabH = Px(40, uiScale, 30);
            int totalTabsW = categories.Length * tabW + (categories.Length - 1) * tabSpacing;
            int tabStartX = screenWidth / 2 - totalTabsW / 2;
            int tabY = titleY + (int)MathF.Ceiling(titleSize.Y) + Px(12, uiScale, 8);
            int tabFont = FontPx(14, uiScale, 11, 20);

            for (int c = 0; c < categories.Length; c++)
            {
                bool selected = c == _settingsCategory;
                int catX = tabStartX + c * (tabW + tabSpacing);
                var tabRect = new Rectangle(catX, tabY, tabW, tabH);
                _tabHitboxes.Add(tabRect);

                bool hovered = tabRect.Contains(_lastMousePos);
                Color bgColor = selected ? BG_LIGHT : (hovered ? new Color(28, 28, 46) : BG_MEDIUM);
                Color borderColor = selected ? ACCENT_WARNING : (hovered ? ACCENT_PRIMARY : BORDER_SUBTLE);

                DrawPanel(sb, pixel, catX, tabY, tabW, tabH, bgColor, borderColor, selected ? 2 : 1);
                if (selected)
                    sb.Draw(pixel, new Rectangle(catX, tabY + tabH - Px(3, uiScale, 2), tabW, Px(3, uiScale, 2)), ACCENT_WARNING * _fadeAlpha);

                Vector2 catTextSize = MeasureText(font, categories[c], tabFont);
                Vector2 catTextPos = new Vector2(catX + tabW / 2f - catTextSize.X / 2f, tabY + tabH / 2f - catTextSize.Y / 2f);
                Color textColor = selected ? ACCENT_WARNING : (hovered ? TEXT_PRIMARY : TEXT_SECONDARY);
                DrawText(sb, font, categories[c], catTextPos, textColor * _fadeAlpha, tabFont, shadow: selected);
            }

            int footerY = DrawFooterBar(sb, font, pixel, screenWidth, screenHeight, uiScale,
                "Q/E or LEFT/RIGHT: Category  |  UP/DOWN: Navigate  |  ENTER/CLICK: Edit/Toggle/Focus  |  </> or L/R (Focused): Adjust  |  R: Reset  |  ESC: Save & Back");

            int panelW = ClampUi(Px(920, uiScale), 360, screenWidth - Px(60, uiScale));
            int panelX = screenWidth / 2 - panelW / 2;
            int panelY = tabY + tabH + Px(14, uiScale, 8);
            int descH = Px(74, uiScale, 58);
            int descGap = Px(10, uiScale, 6);
            int descY = footerY - Px(10, uiScale, 6) - descH;
            int panelH = Math.Max(180, descY - descGap - panelY);

            DrawPanel(sb, pixel, panelX, panelY, panelW, panelH, BG_MEDIUM, ACCENT_PRIMARY * 0.7f, 2);

            var categorySettings = GetCategorySettings(_settingsCategory, settings);
            DrawSettingsCategory(sb, font, pixel, uiScale, panelX, panelY, panelW, panelH, settings, categorySettings);

            // Description box matching SettingsManager style
            DrawPanel(sb, pixel, panelX, descY, panelW, descH, BG_DARK, BORDER_SUBTLE, 1, ACCENT_WARNING);

            if (categorySettings.Count > 0)
            {
                int safeIdx = Math.Clamp(_settingsSelection, 0, categorySettings.Count - 1);
                var activeSetting = categorySettings[safeIdx];
                int hdrFont = FontPx(14, uiScale, 11, 20);
                int descFont = FontPx(14, uiScale, 11, 19);

                DrawText(sb, font, activeSetting.Label, new Vector2(panelX + Px(18, uiScale), descY + Px(10, uiScale)),
                    ACCENT_WARNING * _fadeAlpha, hdrFont, shadow: true);

                string wrappedDesc = WrapTextToWidth(font, activeSetting.Description, panelW - Px(36, uiScale), descFont);
                DrawText(sb, font, wrappedDesc, new Vector2(panelX + Px(18, uiScale), descY + Px(32, uiScale)),
                    TEXT_SECONDARY * _fadeAlpha, descFont);
            }
        }

        private void DrawSettingsCategory(SpriteBatch sb, SpriteFont font, Texture2D pixel, float uiScale, int panelX, int panelY, int panelW, int panelH, MultiplayerSettings settings, List<SettingInfo> categorySettings)
        {
            int padTop = Px(16, uiScale, 10);
            int settingY = panelY + padTop;
            int settingH = Math.Clamp(Px(62, uiScale), 46, 80);
            int settingMargin = Px(8, uiScale, 5);
            int totalSettingH = settingH + settingMargin;

            bool hasScroll = categorySettings.Count > MAX_SETTINGS_VISIBLE;
            int rowPadX = Px(20, uiScale, 12);
            int rowW = panelW - rowPadX * 2 - (hasScroll ? Px(18, uiScale, 12) : 0);
            int rowX = panelX + rowPadX;

            int startIdx = _settingsScrollOffset;
            int endIdx = Math.Min(startIdx + MAX_SETTINGS_VISIBLE, categorySettings.Count);

            for (int i = startIdx; i < endIdx; i++)
            {
                var setting = categorySettings[i];
                bool selected = i == _settingsSelection;
                int displayIndex = i - startIdx;
                int currentY = settingY + displayIndex * totalSettingH;

                if (currentY + settingH > panelY + panelH - Px(12, uiScale))
                    break;

                _clickableAreas.Add(new Rectangle(rowX, currentY, rowW, settingH));

                Color? swatch = setting.Label == "Player Color" ? settings.GetPlayerColor() : null;
                DrawSettingItem(sb, font, pixel, uiScale, rowX, currentY, rowW, settingH, setting, selected, swatch);
            }

            if (hasScroll)
            {
                DrawScrollBar(sb, pixel, rowX + rowW + Px(10, uiScale), settingY, panelH - padTop * 2, categorySettings.Count, MAX_SETTINGS_VISIBLE, _settingsScrollOffset, ACCENT_PRIMARY);
            }
        }
        
        private List<SettingInfo> GetCategorySettings(int category, MultiplayerSettings settings)
        {
            var list = new List<SettingInfo>();
            
            switch (category)
            {
                case 0: // User
                    string usernameValue = _isEditingText && _settingsSelection == 0 ? _editingTextValue + "_" : settings.Username;
                    list.Add(new SettingInfo
                    {
                        Label = "Username",
                        Value = usernameValue,
                        IsToggle = false,
                        IsAdjustable = false,
                        Description = "Display name shown to opponents in matches and on the leaderboard. Press ENTER or click to type (supports uppercase)."
                    });
                    list.Add(new SettingInfo
                    {
                        Label = "Player Color",
                        Value = $"Hue: {settings.PlayerColorHue} deg",
                        IsToggle = false,
                        IsAdjustable = true,
                        Description = "Color hue (0-350 deg) used to tint your player name in the multiplayer HUD, results screen, and leaderboard."
                    });
                    list.Add(new SettingInfo
                    {
                        Label = "My Character (Local)",
                        Value = GetEquippedCharacterLabel(settings.EquippedCharacter),
                        IsToggle = false,
                        IsAdjustable = true,
                        Description = "Equip a custom character from mods/characters/ on your side during online matches (local visual skin & health icon; skipped in Chart-Only mode)."
                    });
                    break;
                    
                case 1: // Gameplay
                    list.Add(new SettingInfo
                    {
                        Label = "Show Opponent Notes",
                        Value = settings.ShowOpponentNotes ? "ON" : "OFF",
                        IsToggle = true,
                        IsAdjustable = true,
                        Description = "Show or hide the opponent's notes. Same setting as Opponent Strumline in the main Settings (NOTES tab)."
                    });
                    list.Add(new SettingInfo
                    {
                        Label = "Show Scores List",
                        Value = settings.ShowOpponentScore ? "ON" : "OFF",
                        IsToggle = true,
                        IsAdjustable = true,
                        Description = "During online matches, show the small list of players and their live scores."
                    });
                    list.Add(new SettingInfo
                    {
                        Label = "Scores List Side",
                        Value = settings.ScoreListPosition == MultiplayerSettings.ScoreListSide.Right ? "Right" : "Left",
                        IsToggle = false,
                        IsAdjustable = true,
                        Description = "Side of the screen where the live scores list is drawn."
                    });
                    list.Add(new SettingInfo
                    {
                        Label = "Show Connection Quality",
                        Value = settings.ShowConnectionQuality ? "ON" : "OFF",
                        IsToggle = true,
                        IsAdjustable = true,
                        Description = "Display the live ping and connection quality overlay during multiplayer gameplay."
                    });
                    list.Add(new SettingInfo
                    {
                        Label = "Opponent Notes Opacity",
                        Value = $"{settings.GhostTransparency}%",
                        IsToggle = false,
                        IsAdjustable = true,
                        Description = "How visible the opponent's notes are during online matches (0-100%), when Show Opponent Notes is on."
                    });
                    list.Add(new SettingInfo
                    {
                        Label = "Teams: Rival Strumline",
                        Value = settings.TeamsRivalStrumline == MultiplayerSettings.RivalStrumlineMode.Cpu ? "CPU" : "Real player",
                        IsToggle = false,
                        IsAdjustable = true,
                        Description = "In Teams matches, the other side's strumline shows the presses of a player of the other team, or is played by the CPU."
                    });
                    break;
                    
                case 2: // Performance
                    int realHz = (int)Math.Round(1f / settings.GetStateSyncIntervalSeconds());
                    list.Add(new SettingInfo
                    {
                        Label = "Network Update Rate",
                        Value = $"{settings.UpdateRate} ({realHz} Hz)",
                        IsToggle = false,
                        IsAdjustable = true,
                        Description = "How frequently score, combo, and health packets are sent to your opponent (Low=5Hz, Medium=10Hz, High=20Hz)."
                    });
                    list.Add(new SettingInfo
                    {
                        Label = "Enable Interpolation",
                        Value = settings.EnableInterpolation ? "ON" : "OFF",
                        IsToggle = true,
                        IsAdjustable = true,
                        Description = "Smoothly interpolate opponent health transitions between incoming network packets instead of snapping."
                    });
                    list.Add(new SettingInfo
                    {
                        Label = "Skip osu! Videos",
                        Value = settings.OsuSkipVideo ? "ON" : "OFF",
                        IsToggle = true,
                        IsAdjustable = true,
                        Description = "osu! songs you get from other players come without their background video: much smaller downloads. Songs you already have keep theirs."
                    });
                    list.Add(new SettingInfo
                    {
                        Label = "Chart Only (No Sprites)",
                        Value = settings.ChartOnlyMode ? "ON" : "OFF",
                        IsToggle = true,
                        IsAdjustable = true,
                        Description = "Online FNF matches: only download and load charts + music (no sprites or stage). Much faster & prevents OOM on low-RAM / 32-bit phones."
                    });
                    break;
                    
                case 3: // Visual
                    list.Add(new SettingInfo
                    {
                        Label = "HUD Opacity",
                        Value = $"{settings.HudOpacity}%",
                        IsToggle = false,
                        IsAdjustable = true,
                        Description = "Overall opacity (10-100%) for multiplayer HUD bars, names, and connection quality badge."
                    });
                    list.Add(new SettingInfo
                    {
                        Label = "Connection Indicator",
                        Value = settings.ConnectionIndicatorPosition.ToString(),
                        IsToggle = false,
                        IsAdjustable = true,
                        Description = "Corner of the screen where the live ping indicator is displayed during gameplay and lobbies."
                    });
                    list.Add(new SettingInfo
                    {
                        Label = "Interface Mode",
                        Value = settings.InterfaceMode.ToString(),
                        IsToggle = false,
                        IsAdjustable = true,
                        Description = "Detail level of the ping indicator (Compact shows ping only; Full shows ping and quality tier)."
                    });
                    break;
            }
            
            return list;
        }
        
        private class SettingInfo
        {
            public string Label { get; set; } = "";
            public string Value { get; set; } = "";
            public bool IsToggle { get; set; }
            public bool IsAdjustable { get; set; }
            public string Description { get; set; } = "";
        }
        
        private void DrawSettingItem(SpriteBatch sb, SpriteFont font, Texture2D pixel, float uiScale, int x, int y, int w, int h, SettingInfo setting, bool selected, Color? swatchColor)
        {
            bool focused = selected && _isSettingFocused;
            if (setting.Label == "Username") Hit("username", new Rectangle(x, y, w, h)); // clicks here keep editing
            Color bgColor = selected ? BG_LIGHT : BG_DARK;
            Color borderColor = focused ? ACCENT_WARNING : (selected ? ACCENT_PRIMARY : BORDER_SUBTLE);
            Color? stripe = focused ? ACCENT_WARNING : (selected ? ACCENT_PRIMARY : null);

            DrawPanel(sb, pixel, x, y, w, h, bgColor, borderColor, focused ? 2 : 1, stripe);

            string label = setting.Label;
            string value = setting.Value;

            int labelFont = FontPx(17, uiScale, 12, 24);
            int valueFont = FontPx(16, uiScale, 12, 22);

            Vector2 labelSize = MeasureText(font, label, labelFont);
            Vector2 labelPos = new Vector2(x + Px(20, uiScale), y + h / 2f - labelSize.Y / 2f);
            Color labelColor = focused ? ACCENT_WARNING : (selected ? Color.White : TEXT_PRIMARY);
            DrawText(sb, font, label, labelPos, labelColor * _fadeAlpha, labelFont, shadow: selected);

            Color valueColor = focused ? ACCENT_WARNING : (selected ? ACCENT_SECONDARY : TEXT_SECONDARY);
            if (setting.IsToggle)
                valueColor = value == "ON" ? ACCENT_SUCCESS : ACCENT_ERROR;
            else if (setting.Label == "Username" && _isEditingText)
                valueColor = ACCENT_WARNING;

            Vector2 valueSize = MeasureText(font, value, valueFont);

            if (setting.IsAdjustable)
            {
                int arrowBtnW = Px(28, uiScale, 22);
                int arrowBtnH = Math.Min(h - Px(14, uiScale), Px(34, uiScale, 24));
                int arrowBtnY = y + (h - arrowBtnH) / 2;
                int rightPad = Px(16, uiScale);

                int rightBtnX = x + w - rightPad - arrowBtnW;
                int valueGap = Px(12, uiScale);
                int valueW = Math.Max(Px(68, uiScale), (int)MathF.Ceiling(valueSize.X) + Px(20, uiScale));
                int valueBoxX = rightBtnX - valueGap - valueW;
                int leftBtnX = valueBoxX - valueGap - arrowBtnW;

                var leftRect = new Rectangle(leftBtnX - 4, y, arrowBtnW + 8, h);
                var rightRect = new Rectangle(rightBtnX - 4, y, arrowBtnW + 8, h);
                _leftArrowAreas.Add(leftRect);
                _rightArrowAreas.Add(rightRect);

                // Optional color preview swatch positioned cleanly left of stepper arrows
                if (swatchColor.HasValue)
                {
                    int swatchW = Px(38, uiScale, 24);
                    int swatchH = Math.Min(h - Px(18, uiScale), Px(28, uiScale, 18));
                    int swatchX = leftBtnX - Px(16, uiScale) - swatchW;
                    int swatchY = y + (h - swatchH) / 2;
                    sb.Draw(pixel, new Rectangle(swatchX, swatchY, swatchW, swatchH), swatchColor.Value * _fadeAlpha);
                    DrawBorder(sb, pixel, swatchX, swatchY, swatchW, swatchH, Color.White * (0.55f * _fadeAlpha), 1);
                }

                bool leftHovered = leftRect.Contains(_lastMousePos);
                bool rightHovered = rightRect.Contains(_lastMousePos);
                Color arrowColorL = (focused || leftHovered) ? ACCENT_WARNING : (selected ? ACCENT_PRIMARY : TEXT_SECONDARY);
                Color arrowColorR = (focused || rightHovered) ? ACCENT_WARNING : (selected ? ACCENT_PRIMARY : TEXT_SECONDARY);

                // Stepper pills
                sb.Draw(pixel, new Rectangle(leftBtnX, arrowBtnY, arrowBtnW, arrowBtnH), (leftHovered ? BG_LIGHT : BG_MEDIUM) * _fadeAlpha);
                DrawBorder(sb, pixel, leftBtnX, arrowBtnY, arrowBtnW, arrowBtnH, arrowColorL * (0.5f * _fadeAlpha), 1);
                Vector2 lSz = MeasureText(font, "<", valueFont);
                DrawText(sb, font, "<", new Vector2(leftBtnX + (arrowBtnW - lSz.X) / 2f, arrowBtnY + (arrowBtnH - lSz.Y) / 2f), arrowColorL * _fadeAlpha, valueFont);

                // Value pill
                if (setting.IsToggle)
                {
                    Color pillAccent = value == "ON" ? ACCENT_SUCCESS : ACCENT_ERROR;
                    sb.Draw(pixel, new Rectangle(valueBoxX, arrowBtnY, valueW, arrowBtnH), pillAccent * ((value == "ON" ? 0.22f : 0.14f) * _fadeAlpha));
                    DrawBorder(sb, pixel, valueBoxX, arrowBtnY, valueW, arrowBtnH, pillAccent * (0.65f * _fadeAlpha), 1);
                }
                else
                {
                    sb.Draw(pixel, new Rectangle(valueBoxX, arrowBtnY, valueW, arrowBtnH), new Color(12, 12, 22) * (0.75f * _fadeAlpha));
                    DrawBorder(sb, pixel, valueBoxX, arrowBtnY, valueW, arrowBtnH, (focused ? ACCENT_WARNING : BORDER_SUBTLE) * _fadeAlpha, 1);
                }
                DrawText(sb, font, value, new Vector2(valueBoxX + (valueW - valueSize.X) / 2f, arrowBtnY + (arrowBtnH - valueSize.Y) / 2f), valueColor * _fadeAlpha, valueFont);

                sb.Draw(pixel, new Rectangle(rightBtnX, arrowBtnY, arrowBtnW, arrowBtnH), (rightHovered ? BG_LIGHT : BG_MEDIUM) * _fadeAlpha);
                DrawBorder(sb, pixel, rightBtnX, arrowBtnY, arrowBtnW, arrowBtnH, arrowColorR * (0.5f * _fadeAlpha), 1);
                Vector2 rSz = MeasureText(font, ">", valueFont);
                DrawText(sb, font, ">", new Vector2(rightBtnX + (arrowBtnW - rSz.X) / 2f, arrowBtnY + (arrowBtnH - rSz.Y) / 2f), arrowColorR * _fadeAlpha, valueFont);
            }
            else
            {
                _leftArrowAreas.Add(Rectangle.Empty);
                _rightArrowAreas.Add(Rectangle.Empty);

                int rightPad = Px(16, uiScale);
                int boxH = Math.Min(h - Px(14, uiScale), Px(36, uiScale, 26));
                int boxW = Math.Max(Px(160, uiScale), (int)MathF.Ceiling(valueSize.X) + Px(28, uiScale));
                int boxX = x + w - rightPad - boxW;
                int boxY = y + (h - boxH) / 2;
                bool editingUsername = setting.Label == "Username" && _isEditingText;

                sb.Draw(pixel, new Rectangle(boxX, boxY, boxW, boxH), (editingUsername ? new Color(28, 26, 44) : new Color(12, 12, 22)) * _fadeAlpha);
                DrawBorder(sb, pixel, boxX, boxY, boxW, boxH, (editingUsername ? ACCENT_WARNING : (selected ? ACCENT_PRIMARY * 0.7f : BORDER_SUBTLE)) * _fadeAlpha, editingUsername ? 2 : 1);
                DrawText(sb, font, value, new Vector2(boxX + (boxW - valueSize.X) / 2f, boxY + (boxH - valueSize.Y) / 2f), valueColor * _fadeAlpha, valueFont);
            }
        }
        
        /// <summary>
        /// Sanitize text by replacing unsupported characters with safe alternatives.
        /// Prevents crashes when SpriteFont doesn't support certain Unicode characters.
        /// </summary>
        private string SanitizeText(SpriteFont font, string text)
        {
            if (string.IsNullOrEmpty(text))
                return text;
            
            // Simple approach: replace known problematic characters
            text = text.Replace("°", " deg");  // degree symbol
            text = text.Replace("™", "TM");    // trademark
            text = text.Replace("©", "(c)");   // copyright
            text = text.Replace("®", "(R)");   // registered
            text = text.Replace("€", "EUR");   // euro
            text = text.Replace("£", "GBP");   // pound
            text = text.Replace("¥", "YEN");   // yen
            text = text.Replace("•", "*");     // bullet
            text = text.Replace("…", "...");   // ellipsis
            text = text.Replace("—", "-");     // em dash
            text = text.Replace("–", "-");     // en dash
            
            // Remove any remaining non-ASCII characters that might cause issues
            var sanitized = new System.Text.StringBuilder(text.Length);
            foreach (char c in text)
            {
                // Keep ASCII printable characters (32-126) and common extended ASCII
                if (c >= 32 && c <= 126)
                {
                    sanitized.Append(c);
                }
                else if (c == '\n' || c == '\r' || c == '\t')
                {
                    sanitized.Append(c); // Keep whitespace
                }
                else
                {
                    sanitized.Append('?'); // Replace unknown characters
                }
            }
            
            return sanitized.ToString();
        }
        
        // ══════════════════════════════════════════════════════════════════════
        // Leaderboard Update and Draw
        // ══════════════════════════════════════════════════════════════════════
        
        private void UpdateLeaderboard(KeyboardState kb, MouseState mouse, float dt)
        {
            if (_inputCooldown > 0f) return;

            // Back via ESC button or key
            if (IsKeyPressed(kb, Keys.Escape) || IsRectClicked(mouse, _backButtonRect))
            {
                TransitionToState(MenuState.MainMenu);
                return;
            }

            // Refresh / Sync global leaderboard (SignalR when connected, HTTP fallback otherwise)
            if (IsKeyPressed(kb, HotkeyAction.Refresh) || IsRectClicked(mouse, _refreshButtonRect))
            {
                RequestLeaderboardRefresh();
                _inputCooldown = INPUT_COOLDOWN_TIME;
                return;
            }

            // Click tabs
            for (int t = 0; t < _tabHitboxes.Count; t++)
            {
                if (IsRectClicked(mouse, _tabHitboxes[t]))
                {
                    _leaderboardTab = t;
                    _leaderboardSelection = 0;
                    _leaderboardScrollOffset = 0;
                    _inputCooldown = INPUT_COOLDOWN_TIME;
                    return;
                }
            }

            // Switch tabs with LEFT/RIGHT or Q/E
            if (IsKeyPressed(kb, Keys.Left) || IsKeyPressed(kb, HotkeyAction.PrevTab))
            {
                _leaderboardTab = (_leaderboardTab - 1 + 3) % 3;
                _leaderboardSelection = 0;
                _leaderboardScrollOffset = 0;
                _inputCooldown = INPUT_COOLDOWN_TIME;
                return;
            }
            if (IsKeyPressed(kb, Keys.Right) || IsKeyPressed(kb, HotkeyAction.NextTab))
            {
                _leaderboardTab = (_leaderboardTab + 1) % 3;
                _leaderboardSelection = 0;
                _leaderboardScrollOffset = 0;
                _inputCooldown = INPUT_COOLDOWN_TIME;
                return;
            }

            // Navigate items with UP/DOWN and Mouse Wheel
            int itemCount = GetLeaderboardItemCount();
            if (itemCount > 0)
            {
                int wheelDelta = mouse.ScrollWheelValue - _prevMouse.ScrollWheelValue;
                if (wheelDelta != 0)
                {
                    int step = wheelDelta > 0 ? -1 : 1;
                    _leaderboardSelection = (_leaderboardSelection + step + itemCount) % itemCount;
                    AdjustLeaderboardScroll();
                }

                if (_mouseMovedThisFrame && _hoveredButton >= 0 && _hoveredButton < _clickableAreas.Count)
                {
                    int hoveredIdx = _hoveredButton + _leaderboardScrollOffset;
                    if (hoveredIdx >= 0 && hoveredIdx < itemCount)
                        _leaderboardSelection = hoveredIdx;
                }

                if (IsKeyPressed(kb, Keys.Down))
                {
                    _leaderboardSelection = (_leaderboardSelection + 1) % itemCount;
                    AdjustLeaderboardScroll();
                    _inputCooldown = INPUT_COOLDOWN_TIME;
                }
                if (IsKeyPressed(kb, Keys.Up))
                {
                    _leaderboardSelection = (_leaderboardSelection - 1 + itemCount) % itemCount;
                    AdjustLeaderboardScroll();
                    _inputCooldown = INPUT_COOLDOWN_TIME;
                }
            }
        }

        private void AdjustLeaderboardScroll()
        {
            if (_leaderboardSelection >= _leaderboardScrollOffset + MAX_LEADERBOARD_VISIBLE)
                _leaderboardScrollOffset = _leaderboardSelection - MAX_LEADERBOARD_VISIBLE + 1;
            else if (_leaderboardSelection < _leaderboardScrollOffset)
                _leaderboardScrollOffset = _leaderboardSelection;
        }
        
        private int GetLeaderboardItemCount()
        {
            return _leaderboardTab switch
            {
                0 => LeaderboardManager.Instance.GetTopPlayers(50).Count,
                1 => LeaderboardManager.Instance.GetRecentMatches(50).Count,
                2 => 1, // My stats is a single screen
                _ => 0
            };
        }
        
        private void DrawLeaderboardModern(SpriteBatch sb, SpriteFont font, Texture2D pixel, int screenWidth, int screenHeight)
        {
            float uiScale = GetUiScale(screenWidth, screenHeight);
            DrawBackButton(sb, font, pixel, screenWidth, uiScale, "ESC  Back");

            // Top-right Sync / Status button (works both via SignalR when connected and HTTP before connecting)
            int gap = Px(10, uiScale, 6);
            int topY = _backButtonRect != Rectangle.Empty ? _backButtonRect.Y : Px(18, uiScale, 10);
            int rightAnchor = _backButtonRect != Rectangle.Empty ? _backButtonRect.Left - gap : screenWidth - Px(24, uiScale, 12);
            {
                string syncLabel = _leaderboardSyncInFlight
                    ? "Syncing..."
                    : (_leaderboardSyncedOnline ? "F5  Online Synced" : "F5  Sync Online");
                Color syncAccent = _leaderboardSyncInFlight
                    ? ACCENT_WARNING
                    : (_leaderboardSyncedOnline ? ACCENT_SUCCESS : ACCENT_PRIMARY);
                int syncFont = FontPx(13, uiScale, 10, 19);
                Vector2 sSz = MeasureText(font, syncLabel, syncFont);
                int syncW = Math.Max(Px(140, uiScale), (int)MathF.Ceiling(sSz.X) + Px(26, uiScale));
                int syncH = Px(34, uiScale, 28);
                int syncX = rightAnchor - syncW;
                if (syncX < screenWidth / 2 + Px(150, uiScale) && _backButtonRect != Rectangle.Empty)
                {
                    syncX = _backButtonRect.Right - syncW;
                    topY = _backButtonRect.Bottom + Px(6, uiScale);
                }
                _refreshButtonRect = new Rectangle(syncX, topY, syncW, syncH);
                bool syncHovered = _refreshButtonRect.Contains(_lastMousePos);
                DrawPanel(sb, pixel, syncX, topY, syncW, syncH, syncHovered ? BG_LIGHT : BG_MEDIUM, syncAccent, syncHovered ? 2 : 1);
                DrawText(sb, font, syncLabel, new Vector2(syncX + syncW / 2f - sSz.X / 2f, topY + syncH / 2f - sSz.Y / 2f),
                    (syncHovered ? Color.White : syncAccent) * _fadeAlpha, syncFont);
            }

            // Title
            int titleFont = FontPx(28, uiScale, 18, 40);
            string title = "LEADERBOARD & STATS";
            Vector2 titleSize = MeasureText(font, title, titleFont);
            int titleY = Px(16, uiScale, 10);
            DrawText(sb, font, title, new Vector2(screenWidth / 2f - titleSize.X / 2f, titleY), ACCENT_PRIMARY * _fadeAlpha, titleFont, shadow: true);

            // Clickable Tabs
            string[] tabs = { "TOP PLAYERS", "RECENT MATCHES", "MY STATS" };
            int tabSpacing = Px(14, uiScale, 8);
            int maxTabBarW = ClampUi(Px(760, uiScale), 340, screenWidth - Px(60, uiScale));
            int tabW = Math.Min(Px(230, uiScale), (maxTabBarW - tabSpacing * 2) / 3);
            int tabH = Px(42, uiScale, 32);
            int totalTabsW = tabW * 3 + tabSpacing * 2;
            int tabStartX = screenWidth / 2 - totalTabsW / 2;
            int tabY = titleY + (int)MathF.Ceiling(titleSize.Y) + Px(12, uiScale, 8);
            int tabFont = FontPx(15, uiScale, 11, 21);

            for (int i = 0; i < tabs.Length; i++)
            {
                bool selected = i == _leaderboardTab;
                int tabX = tabStartX + i * (tabW + tabSpacing);
                var tabRect = new Rectangle(tabX, tabY, tabW, tabH);
                _tabHitboxes.Add(tabRect);
                bool hovered = tabRect.Contains(_lastMousePos);

                Color bgColor = selected ? BG_LIGHT : (hovered ? new Color(28, 28, 46) : BG_MEDIUM);
                Color borderColor = selected ? ACCENT_PRIMARY : (hovered ? ACCENT_WARNING : BORDER_SUBTLE);

                DrawPanel(sb, pixel, tabX, tabY, tabW, tabH, bgColor, borderColor, selected ? 2 : 1);
                if (selected)
                    sb.Draw(pixel, new Rectangle(tabX, tabY + tabH - Px(3, uiScale, 2), tabW, Px(3, uiScale, 2)), ACCENT_PRIMARY * _fadeAlpha);

                Vector2 tabTextSize = MeasureText(font, tabs[i], tabFont);
                Vector2 tabTextPos = new Vector2(tabX + tabW / 2f - tabTextSize.X / 2f, tabY + tabH / 2f - tabTextSize.Y / 2f);
                Color textColor = selected ? ACCENT_PRIMARY : (hovered ? TEXT_PRIMARY : TEXT_SECONDARY);
                DrawText(sb, font, tabs[i], tabTextPos, textColor * _fadeAlpha, tabFont, shadow: selected);
            }

            int footerY = DrawFooterBar(sb, font, pixel, screenWidth, screenHeight, uiScale,
                "Q/E or LEFT/RIGHT: Switch Tab  |  UP/DOWN or WHEEL: Scroll  |  F5: Sync Online  |  ESC: Back");

            int panelW = ClampUi(Px(960, uiScale), 360, screenWidth - Px(60, uiScale));
            int panelY = tabY + tabH + Px(14, uiScale, 8);
            int panelH = Math.Max(220, footerY - panelY - Px(12, uiScale));
            int panelX = screenWidth / 2 - panelW / 2;

            DrawPanel(sb, pixel, panelX, panelY, panelW, panelH, BG_MEDIUM, ACCENT_PRIMARY * 0.7f, 2);

            switch (_leaderboardTab)
            {
                case 0:
                    DrawTopPlayersTab(sb, font, pixel, uiScale, panelX, panelY, panelW, panelH);
                    break;
                case 1:
                    DrawRecentMatchesTab(sb, font, pixel, uiScale, panelX, panelY, panelW, panelH);
                    break;
                case 2:
                    DrawMyStatsTab(sb, font, pixel, uiScale, panelX, panelY, panelW, panelH);
                    break;
            }
        }

        private void DrawTopPlayersTab(SpriteBatch sb, SpriteFont font, Texture2D pixel, float uiScale, int panelX, int panelY, int panelW, int panelH)
        {
            var topPlayers = LeaderboardManager.Instance.GetTopPlayers(50);

            if (topPlayers.Count == 0)
            {
                DrawCenteredTextInPanel(sb, font, "No leaderboard data yet", panelX, panelY + panelH / 2 - Px(20, uiScale), panelW, TEXT_SECONDARY, FontPx(19, uiScale, 14, 28));
                DrawCenteredTextInPanel(sb, font, "Finish a multiplayer match to appear here!", panelX, panelY + panelH / 2 + Px(12, uiScale), panelW, TEXT_DISABLED, FontPx(14, uiScale, 11, 20));
                return;
            }

            bool hasScroll = topPlayers.Count > MAX_LEADERBOARD_VISIBLE;
            int padX = Px(24, uiScale, 12);
            int usableW = panelW - padX * 2 - (hasScroll ? Px(18, uiScale, 12) : 0);

            int headerY = panelY + Px(16, uiScale);
            int headerFont = FontPx(13, uiScale, 10, 19);
            int rowFont = FontPx(16, uiScale, 12, 23);

            int col1X = panelX + padX + Px(12, uiScale);
            int col2X = panelX + padX + Px(96, uiScale);
            int col3X = panelX + padX + (int)(usableW * 0.48f);
            int col4X = panelX + padX + (int)(usableW * 0.62f);
            int col5X = panelX + padX + (int)(usableW * 0.74f);
            int col6X = panelX + padX + (int)(usableW * 0.87f);

            DrawText(sb, font, "RANK", new Vector2(col1X, headerY), ACCENT_WARNING * _fadeAlpha, headerFont);
            DrawText(sb, font, "PLAYER", new Vector2(col2X, headerY), ACCENT_WARNING * _fadeAlpha, headerFont);
            DrawText(sb, font, "ELO", new Vector2(col3X, headerY), ACCENT_WARNING * _fadeAlpha, headerFont);
            DrawText(sb, font, "W / L", new Vector2(col4X, headerY), ACCENT_WARNING * _fadeAlpha, headerFont);
            DrawText(sb, font, "WIN %", new Vector2(col5X, headerY), ACCENT_WARNING * _fadeAlpha, headerFont);
            DrawText(sb, font, "SCORE", new Vector2(col6X, headerY), ACCENT_WARNING * _fadeAlpha, headerFont);

            int divY = headerY + Px(26, uiScale);
            sb.Draw(pixel, new Rectangle(panelX + padX, divY, usableW, 1), Color.White * (0.1f * _fadeAlpha));

            int listTop = divY + Px(10, uiScale);
            int availH = Math.Max(140, panelY + panelH - Px(14, uiScale) - listTop);
            int rowGap = Px(6, uiScale, 4);
            int rowH = ClampUi((availH - rowGap * (MAX_LEADERBOARD_VISIBLE - 1)) / MAX_LEADERBOARD_VISIBLE, 36, Px(58, uiScale));

            int startIdx = _leaderboardScrollOffset;
            int endIdx = Math.Min(startIdx + MAX_LEADERBOARD_VISIBLE, topPlayers.Count);
            string localUser = MultiplayerSettings.Instance.Username;

            for (int i = startIdx; i < endIdx; i++)
            {
                var player = topPlayers[i];
                bool selected = i == _leaderboardSelection;
                bool isSelf = !string.IsNullOrWhiteSpace(localUser) && string.Equals(player.Username, localUser, StringComparison.OrdinalIgnoreCase);
                int displayIdx = i - startIdx;
                int currentY = listTop + displayIdx * (rowH + rowGap);

                if (currentY + rowH > panelY + panelH - Px(10, uiScale))
                    break;

                var rowRect = new Rectangle(panelX + padX, currentY, usableW, rowH);
                _clickableAreas.Add(rowRect);

                Color rowBg = selected ? BG_LIGHT : (isSelf ? new Color(24, 34, 48) : BG_DARK);
                Color rowBorder = selected ? ACCENT_PRIMARY : (isSelf ? ACCENT_SUCCESS * 0.5f : Color.White * 0.04f);
                DrawPanel(sb, pixel, rowRect.X, rowRect.Y, rowRect.Width, rowRect.Height, rowBg, rowBorder, selected ? 2 : 1, selected ? ACCENT_PRIMARY : (isSelf ? ACCENT_SUCCESS : null));

                Color rankColor = player.Rank switch
                {
                    1 => new Color(255, 215, 0),
                    2 => new Color(205, 212, 224),
                    3 => new Color(218, 138, 65),
                    _ => TEXT_SECONDARY
                };
                int badgeH = Px(24, uiScale, 18);
                DrawPillBadge(sb, font, pixel, col1X, currentY + (rowH - badgeH) / 2, badgeH, $"#{player.Rank}", rankColor, FontPx(12, uiScale, 10, 17), 8, filledStrong: player.Rank <= 3);

                Color playerColor = GetColorFromHue(player.ColorHue);
                int swatchSz = Px(16, uiScale, 12);
                sb.Draw(pixel, new Rectangle(col2X, currentY + (rowH - swatchSz) / 2, swatchSz, swatchSz), playerColor * _fadeAlpha);
                DrawBorder(sb, pixel, col2X, currentY + (rowH - swatchSz) / 2, swatchSz, swatchSz, Color.White * (0.4f * _fadeAlpha), 1);

                float maxUserW = Math.Max(60f, col3X - (col2X + swatchSz + Px(20, uiScale)));
                string pName = TruncateToWidth(font, string.IsNullOrWhiteSpace(player.Username) ? "Player" : player.Username, maxUserW, rowFont);
                Vector2 nameSz = MeasureText(font, pName, rowFont);
                float textCenterY = currentY + (rowH - nameSz.Y) / 2f;
                DrawText(sb, font, pName, new Vector2(col2X + swatchSz + Px(10, uiScale), textCenterY),
                    (isSelf ? ACCENT_SUCCESS : TEXT_PRIMARY) * _fadeAlpha, rowFont, shadow: selected);

                DrawText(sb, font, player.EloRating.ToString(), new Vector2(col3X, textCenterY), ACCENT_SECONDARY * _fadeAlpha, rowFont);
                DrawText(sb, font, $"{player.Wins}/{player.Losses}", new Vector2(col4X, textCenterY), TEXT_SECONDARY * _fadeAlpha, rowFont);

                Color winRateColor = player.WinRate >= 60 ? ACCENT_SUCCESS : (player.WinRate >= 40 ? ACCENT_WARNING : ACCENT_ERROR);
                DrawText(sb, font, $"{player.WinRate:F1}%", new Vector2(col5X, textCenterY), winRateColor * _fadeAlpha, rowFont);
                DrawText(sb, font, FormatScore(player.TotalScore), new Vector2(col6X, textCenterY), TEXT_PRIMARY * _fadeAlpha, rowFont);
            }

            if (hasScroll)
            {
                DrawScrollBar(sb, pixel, panelX + padX + usableW + Px(10, uiScale), listTop, availH, topPlayers.Count, MAX_LEADERBOARD_VISIBLE, _leaderboardScrollOffset, ACCENT_PRIMARY);
            }
        }

        private void DrawRecentMatchesTab(SpriteBatch sb, SpriteFont font, Texture2D pixel, float uiScale, int panelX, int panelY, int panelW, int panelH)
        {
            var recentMatches = LeaderboardManager.Instance.GetRecentMatches(50);

            if (recentMatches.Count == 0)
            {
                DrawCenteredTextInPanel(sb, font, "No match history yet", panelX, panelY + panelH / 2 - Px(20, uiScale), panelW, TEXT_SECONDARY, FontPx(19, uiScale, 14, 28));
                DrawCenteredTextInPanel(sb, font, "Play your first multiplayer match!", panelX, panelY + panelH / 2 + Px(12, uiScale), panelW, TEXT_DISABLED, FontPx(14, uiScale, 11, 20));
                return;
            }

            bool hasScroll = recentMatches.Count > MAX_LEADERBOARD_VISIBLE;
            int padX = Px(22, uiScale, 12);
            int cardW = panelW - padX * 2 - (hasScroll ? Px(18, uiScale, 12) : 0);
            int listTop = panelY + Px(16, uiScale);
            int availH = Math.Max(160, panelY + panelH - Px(16, uiScale) - listTop);
            int cardGap = Px(8, uiScale, 5);
            int cardH = ClampUi((availH - cardGap * (MAX_LEADERBOARD_VISIBLE - 1)) / MAX_LEADERBOARD_VISIBLE, 46, Px(68, uiScale));

            int startIdx = _leaderboardScrollOffset;
            int endIdx = Math.Min(startIdx + MAX_LEADERBOARD_VISIBLE, recentMatches.Count);

            int levelFont = FontPx(15, uiScale, 12, 21);
            int timeFont = FontPx(12, uiScale, 10, 17);
            int rowFont = FontPx(14, uiScale, 11, 20);
            int badgeFont = FontPx(11, uiScale, 9, 15);

            for (int i = startIdx; i < endIdx; i++)
            {
                var match = recentMatches[i];
                bool selected = i == _leaderboardSelection;
                int displayIdx = i - startIdx;
                int currentY = listTop + displayIdx * (cardH + cardGap);

                if (currentY + cardH > panelY + panelH - Px(10, uiScale))
                    break;

                var cardRect = new Rectangle(panelX + padX, currentY, cardW, cardH);
                _clickableAreas.Add(cardRect);

                Color winAccent = match.Winner > 0 ? ACCENT_SUCCESS : ACCENT_WARNING;
                DrawPanel(sb, pixel, cardRect.X, cardRect.Y, cardRect.Width, cardRect.Height,
                    selected ? BG_LIGHT : BG_DARK, selected ? ACCENT_PRIMARY : BORDER_SUBTLE, selected ? 2 : 1,
                    selected ? ACCENT_PRIMARY : winAccent * 0.7f);

                int infoX = cardRect.X + Px(16, uiScale);
                string levelName = string.IsNullOrWhiteSpace(match.LevelName) ? "Match" : match.LevelName;
                DrawText(sb, font, levelName, new Vector2(infoX, currentY + Px(6, uiScale)), ACCENT_PRIMARY * _fadeAlpha, levelFont, shadow: selected);

                Vector2 timeSize = MeasureText(font, match.TimeAgo, timeFont);
                DrawText(sb, font, match.TimeAgo, new Vector2(cardRect.Right - Px(16, uiScale) - timeSize.X, currentY + Px(8, uiScale)), TEXT_DISABLED * _fadeAlpha, timeFont);

                int vsY = currentY + cardH - Px(26, uiScale);
                string player1Text = $"{match.Player1.Username} ({match.Player1.Score:N0})";
                string vsText = "  VS  ";
                string player2Text = $"{match.Player2.Username} ({match.Player2.Score:N0})";
                string winBadge = match.Winner == 1 ? "WIN P1" : (match.Winner == 2 ? "WIN P2" : "DRAW");

                Color p1Color = match.Winner == 1 ? ACCENT_SUCCESS : TEXT_SECONDARY;
                Color p2Color = match.Winner == 2 ? ACCENT_SUCCESS : TEXT_SECONDARY;
                Color badgeColor = match.Winner > 0 ? ACCENT_SUCCESS : ACCENT_WARNING;

                int pillH = Px(20, uiScale, 16);
                Vector2 bSz = MeasureText(font, winBadge, badgeFont);
                int pillW = (int)MathF.Ceiling(bSz.X) + Px(16, uiScale);
                int pillX = cardRect.Right - Px(16, uiScale) - pillW;
                DrawPillBadge(sb, font, pixel, pillX, vsY - Px(2, uiScale), pillH, winBadge, badgeColor, badgeFont, Px(8, uiScale), filledStrong: true);

                float vsWidth = MeasureText(font, vsText, rowFont).X;
                float availVsRow = Math.Max(120f, pillX - Px(14, uiScale) - infoX - vsWidth);
                float maxPlayerW = availVsRow / 2f;
                string fitP1 = TruncateToWidth(font, player1Text, maxPlayerW, rowFont);
                string fitP2 = TruncateToWidth(font, player2Text, maxPlayerW, rowFont);

                float drawX = infoX;
                DrawText(sb, font, fitP1, new Vector2(drawX, vsY), p1Color * _fadeAlpha, rowFont);
                drawX += MeasureText(font, fitP1, rowFont).X;

                DrawText(sb, font, vsText, new Vector2(drawX, vsY), TEXT_DISABLED * _fadeAlpha, rowFont);
                drawX += vsWidth;

                DrawText(sb, font, fitP2, new Vector2(drawX, vsY), p2Color * _fadeAlpha, rowFont);
            }

            if (hasScroll)
            {
                DrawScrollBar(sb, pixel, panelX + padX + cardW + Px(10, uiScale), listTop, availH, recentMatches.Count, MAX_LEADERBOARD_VISIBLE, _leaderboardScrollOffset, ACCENT_PRIMARY);
            }
        }

        private void DrawMyStatsTab(SpriteBatch sb, SpriteFont font, Texture2D pixel, float uiScale, int panelX, int panelY, int panelW, int panelH)
        {
            var settings = MultiplayerSettings.Instance;
            var myStats = LeaderboardManager.Instance.GetPlayerStats(settings.PlayerId, settings.Username);

            if (myStats == null)
            {
                DrawCenteredTextInPanel(sb, font, "No personal stats yet", panelX, panelY + panelH / 2 - Px(20, uiScale), panelW, TEXT_SECONDARY, FontPx(19, uiScale, 14, 28));
                DrawCenteredTextInPanel(sb, font, "Play matches to see your statistics here!", panelX, panelY + panelH / 2 + Px(12, uiScale), panelW, TEXT_DISABLED, FontPx(14, uiScale, 11, 20));
                return;
            }

            int padX = Px(28, uiScale, 14);
            int bannerY = panelY + Px(20, uiScale);
            int bannerW = panelW - padX * 2;
            int bannerH = Px(74, uiScale, 54);
            Color myColor = settings.GetPlayerColor();

            DrawPanel(sb, pixel, panelX + padX, bannerY, bannerW, bannerH, BG_DARK, myColor * 0.65f, 2, myColor);

            int swatchSz = Px(32, uiScale, 22);
            int swatchX = panelX + padX + Px(18, uiScale);
            int swatchY = bannerY + (bannerH - swatchSz) / 2;
            sb.Draw(pixel, new Rectangle(swatchX, swatchY, swatchSz, swatchSz), myColor * _fadeAlpha);
            DrawBorder(sb, pixel, swatchX, swatchY, swatchSz, swatchSz, Color.White * (0.5f * _fadeAlpha), 1);

            int nameFont = FontPx(24, uiScale, 16, 34);
            string displayName = string.IsNullOrWhiteSpace(settings.Username) ? "Player" : settings.Username;
            Vector2 nameSz = MeasureText(font, displayName, nameFont);
            DrawText(sb, font, displayName, new Vector2(swatchX + swatchSz + Px(14, uiScale), bannerY + (bannerH - nameSz.Y) / 2f),
                Color.Lerp(myColor, Color.White, 0.3f) * _fadeAlpha, nameFont, shadow: true);

            // Right-side ELO & Rank highlight pills in banner
            int pillFont = FontPx(13, uiScale, 10, 18);
            int pillH = Px(28, uiScale, 22);
            string eloBadge = $"ELO  {myStats.EloRating}";
            string rankBadge = myStats.Rank > 0 ? $"RANK  #{myStats.Rank}" : "UNRANKED";
            int eloW = (int)MathF.Ceiling(MeasureText(font, eloBadge, pillFont).X) + Px(24, uiScale);
            int rankW = (int)MathF.Ceiling(MeasureText(font, rankBadge, pillFont).X) + Px(24, uiScale);
            int pillY = bannerY + (bannerH - pillH) / 2;
            DrawPillBadge(sb, font, pixel, panelX + padX + bannerW - Px(16, uiScale) - eloW, pillY, pillH, eloBadge, ACCENT_SECONDARY, pillFont, Px(12, uiScale), filledStrong: true);
            DrawPillBadge(sb, font, pixel, panelX + padX + bannerW - Px(16, uiScale) - eloW - Px(10, uiScale) - rankW, pillY, pillH, rankBadge, ACCENT_WARNING, pillFont, Px(12, uiScale), filledStrong: myStats.Rank > 0);

            // 2-column x 4-row sleek Stat Tiles grid
            int gridTop = bannerY + bannerH + Px(16, uiScale);
            int gridAvailH = Math.Max(180, panelY + panelH - Px(20, uiScale) - gridTop);
            int colGap = Px(16, uiScale, 10);
            int rowGap = Px(12, uiScale, 8);
            int tileW = (bannerW - colGap) / 2;
            int tileH = ClampUi((gridAvailH - rowGap * 3) / 4, 46, Px(84, uiScale));
            int col1X = panelX + padX;
            int col2X = col1X + tileW + colGap;

            DrawStatTile(sb, font, pixel, uiScale, col1X, gridTop, tileW, tileH, "RANK", myStats.Rank > 0 ? $"#{myStats.Rank}" : "-", ACCENT_WARNING);
            DrawStatTile(sb, font, pixel, uiScale, col2X, gridTop, tileW, tileH, "ELO RATING", myStats.EloRating.ToString(), ACCENT_SECONDARY);

            int r2Y = gridTop + (tileH + rowGap);
            DrawStatTile(sb, font, pixel, uiScale, col1X, r2Y, tileW, tileH, "TOTAL MATCHES", myStats.TotalMatches.ToString(), ACCENT_PRIMARY);
            DrawStatTile(sb, font, pixel, uiScale, col2X, r2Y, tileW, tileH, "WIN RATE", $"{myStats.WinRate:F1}%", myStats.WinRate >= 50 ? ACCENT_SUCCESS : ACCENT_WARNING);

            int r3Y = gridTop + (tileH + rowGap) * 2;
            DrawStatTile(sb, font, pixel, uiScale, col1X, r3Y, tileW, tileH, "WINS / DRAWS", $"{myStats.Wins} / {myStats.Draws}", ACCENT_SUCCESS);
            DrawStatTile(sb, font, pixel, uiScale, col2X, r3Y, tileW, tileH, "LOSSES", myStats.Losses.ToString(), ACCENT_ERROR);

            int r4Y = gridTop + (tileH + rowGap) * 3;
            DrawStatTile(sb, font, pixel, uiScale, col1X, r4Y, tileW, tileH, "HIGHEST SCORE", FormatScore(myStats.HighestScore), ACCENT_PRIMARY);
            DrawStatTile(sb, font, pixel, uiScale, col2X, r4Y, tileW, tileH, "BEST COMBO / ACC", $"{myStats.HighestCombo}x / {myStats.BestAccuracy:F1}%", ACCENT_SECONDARY);
        }

        private void DrawStatTile(SpriteBatch sb, SpriteFont font, Texture2D pixel, float uiScale, int x, int y, int w, int h, string label, string value, Color valueColor)
        {
            DrawPanel(sb, pixel, x, y, w, h, BG_DARK, BORDER_SUBTLE, 1, valueColor * 0.8f);
            int labelFont = FontPx(12, uiScale, 10, 17);
            int valFont = FontPx(22, uiScale, 15, 32);

            int padX = Px(18, uiScale);
            int padTop = Px(10, uiScale);
            DrawText(sb, font, label, new Vector2(x + padX, y + padTop), TEXT_DISABLED * _fadeAlpha, labelFont);
            DrawText(sb, font, value, new Vector2(x + padX, y + padTop + Px(20, uiScale)), valueColor * _fadeAlpha, valFont, shadow: true);
        }
        
        private Color GetColorFromHue(int hue)
        {
            float h = hue / 360f;
            return ColorFromHSV(h, 0.8f, 0.9f);
        }
        
        private static Color ColorFromHSV(float h, float s, float v)
        {
            int i = (int)(h * 6);
            float f = h * 6 - i;
            float p = v * (1 - s);
            float q = v * (1 - f * s);
            float t = v * (1 - (1 - f) * s);

            float r, g, b;
            switch (i % 6)
            {
                case 0: r = v; g = t; b = p; break;
                case 1: r = q; g = v; b = p; break;
                case 2: r = p; g = v; b = t; break;
                case 3: r = p; g = q; b = v; break;
                case 4: r = t; g = p; b = v; break;
                default: r = v; g = p; b = q; break;
            }

            return new Color((int)(r * 255), (int)(g * 255), (int)(b * 255));
        }
        
        private string FormatScore(long score)
        {
            if (score >= 1000000)
                return $"{score / 1000000.0:F1}M";
            if (score >= 1000)
                return $"{score / 1000.0:F1}K";
            return score.ToString();
        }
        
#if ANDROID
        /// <summary>
        /// Merge touch controls into keyboard state (Android only)
        /// </summary>
        // ── Touch as mouse (fork) ────────────────────────────────────────────
        private Point _touchPos = new(-10000, -10000);
        private Vector2 _touchStart, _touchLast;
        private bool _touchDragged;
        private int _touchWheel;
        private float _touchWheelAccum;
        private int _touchId = -1;
        private int _touchScreenHeight = 720;

        /// <summary>True when a button / text box of the current room screen is under the point.</summary>
        private bool IsRoomHitAt(Point p)
        {
            for (int i = _roomHit.Count - 1; i >= 0; i--)
                if (_roomHit[i].Rect.Contains(p)) return true;
            return false;
        }

        /// <summary>
        /// The first touch that is not on the on-screen D-pad, as a mouse: finger down = left button
        /// held, lifting it = a click where it was. Dragging scrolls lists (as a mouse wheel) and then
        /// does not click. A button of the room screens under the finger wins over the D-pad.
        /// <para>A new finger is recognised by its touch id, never by <see cref="TouchLocationState.Pressed"/>:
        /// Game1 reads <see cref="TouchPanel.GetState"/> first every frame and MonoGame turns Pressed into
        /// Moved (and drops Released) on every read, so this method never sees those states. Waiting for
        /// Pressed left the drag start stale, every tap counted as a drag and no button could be tapped.</para>
        /// </summary>
        private MouseState TouchAsMouse()
        {
            bool down = false;
            // Fingers wobble a few millimetres while tapping: scale with the screen (high-DPI phones).
            float dragThreshold = Math.Max(28f, _touchScreenHeight * 0.045f);
            foreach (var t in TouchPanel.GetState())
            {
                var p = t.Position;
                var pt = new Point((int)p.X, (int)p.Y);
                if (_touchControls != null && _touchControls.IsPointOnMenuControl(pt) && !IsRoomHitAt(pt)) continue;
                if (t.Id != _touchId)
                {
                    _touchId = t.Id;
                    _touchStart = _touchLast = p;
                    _touchDragged = false;
                    _touchWheelAccum = 0f;
                }
                if (t.State is TouchLocationState.Pressed or TouchLocationState.Moved)
                {
                    down = true;
                    if (Vector2.Distance(p, _touchStart) > dragThreshold) _touchDragged = true;
                    if (_touchDragged)
                    {
                        // Finger down = content follows = like scrolling the wheel up.
                        _touchWheelAccum += p.Y - _touchLast.Y;
                        while (_touchWheelAccum > 40f) { _touchWheel += 120; _touchWheelAccum -= 40f; }
                        while (_touchWheelAccum < -40f) { _touchWheel -= 120; _touchWheelAccum += 40f; }
                    }
                    _touchLast = p;
                }
                _touchPos = pt;
                break;
            }
            // A drag never ends in a click.
            Point pos = !down && _touchDragged ? new Point(-10000, -10000) : _touchPos;
            return new MouseState(pos.X, pos.Y, _touchWheel,
                down ? ButtonState.Pressed : ButtonState.Released, ButtonState.Released, ButtonState.Released,
                ButtonState.Released, ButtonState.Released);
        }

        private KeyboardState MergeTouchWithKeyboard(KeyboardState kb)
        {
            if (_touchControls == null) return kb;
            
            // Get all currently pressed keys from keyboard
            var pressedKeys = new List<Keys>(kb.GetPressedKeys());
            
            // Add virtual keys from touch controls (menu navigation)
            foreach (var key in new[] { Keys.Left, Keys.Right, Keys.Up, Keys.Down, Keys.Enter, Keys.Escape, Keys.Space, Keys.Q, Keys.E })
            {
                if (_touchControls.IsKeyDown(key) && !pressedKeys.Contains(key))
                    pressedKeys.Add(key);
            }
            
            // Create new keyboard state with merged keys
            return new KeyboardState(pressedKeys.ToArray());
        }
#endif
    }
}
