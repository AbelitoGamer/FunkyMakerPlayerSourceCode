using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using Microsoft.Xna.Framework.Input;
using Microsoft.Xna.Framework.Input.Touch;

namespace FunkyMakerPlayer.Multiplayer
{
    /// <summary>
    /// Dedicated multiplayer UI menu with connection, lobby, and matchmaking.
    /// </summary>
    public class MultiplayerMenu
    {
        // Menu states
        private enum MenuState
        {
            MainMenu,      // Connect / Settings / Back
            Connecting,    // Connecting to server...
            ModeSelect,    // Choose: Matchmaking or Private Room
            Lobby,         // Connected, select level
            CreateRoom,    // Create private room with code
            JoinRoom,      // Join private room with code
            Searching,     // Searching for match...
            MatchFound,    // Match found, waiting for ready (launches GameMultiplayer after countdown)
            Settings,      // Settings menu
            Leaderboard    // Leaderboard and stats
        }
        
        // ══════════════════════════════════════════════════════════════════════
        // Public Properties for Game1 Integration
        // ══════════════════════════════════════════════════════════════════════
        
        /// <summary>
        /// Returns the file path of the selected level for gameplay.
        /// For Official levels: returns temp file path from LevelCache
        /// For Local levels: returns full path from levels/ folder
        /// </summary>
        public string? SelectedLevelPath
        {
            get
            {
                if (_selectedSource == LevelSource.Official && _officialLevels.Count > 0 && _levelSelection < _officialLevels.Count)
                {
                    var level = _officialLevels[_levelSelection];
                    return LevelCache.Instance.WriteTempFile(level.Id);
                }
                else if (_selectedSource == LevelSource.Local && _availableLevels.Count > 0 && _levelSelection < _availableLevels.Count)
                {
                    return System.IO.Path.Combine(_levelsFolder, _availableLevels[_levelSelection]);
                }
                return null;
            }
        }
        
        /// <summary>
        /// Returns the display name of the selected level.
        /// </summary>
        public string? SelectedLevelName
        {
            get
            {
                if (_selectedSource == LevelSource.Official && _officialLevels.Count > 0 && _levelSelection < _officialLevels.Count)
                {
                    return _officialLevels[_levelSelection].Name;
                }
                else if (_selectedSource == LevelSource.Local && _availableLevels.Count > 0 && _levelSelection < _availableLevels.Count)
                {
                    return _availableLevels[_levelSelection];
                }
                return null;
            }
        }
        
        /// <summary>
        /// Returns the hash of the selected level (used for matchmaking).
        /// </summary>
        public string? SelectedLevelHash
        {
            get
            {
                string? path = SelectedLevelPath;
                if (string.IsNullOrEmpty(path) || !System.IO.File.Exists(path))
                    return null;
                return LevelHasher.CalculateHash(path);
            }
        }
        
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
        private bool _isEditingText = false;
        private string _editingTextValue = "";
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

        // Lobby
        private List<string> _availableLevels = new();
        private int _levelSelection = 0;
        private int _levelScrollOffset = 0; // NEW: For scrolling
        private const int MAX_VISIBLE_LEVELS = 5; // Show 5 levels at a time
        private LevelSource _selectedSource = LevelSource.Local;
        private List<OfficialLevelInfo> _officialLevels = new();
        private LevelDownloader? _levelDownloader = null;
        private volatile bool _isDownloading = false;
        private volatile int _downloadProgress = 0;
        private long _downloadBytesReceived = 0;
        private long _downloadBytesTotal = 0;
        private string? _pendingAutoMatchmakingLevelId = null;
        private string? _pendingDownloadError = null;
        private string _levelsFolder = ""; // Will be set in constructor
        private Game1? _game = null; // Reference to main game instance

        // Matchmaking
        private float _searchTimer = 0f;
        private string? _opponentName = null;
        private string? _roomId = null;
        private bool _isReady = false;
        private bool _opponentReady = false;
        private long _gameStartTimestamp = 0; // Server timestamp for synchronized start
        
        // Countdown system
        private bool _isCountdownActive = false;
        private long _countdownStartTime = 0; // Server timestamp when countdown should start
        private float _countdownValue = 0f; // Current countdown value (3, 2, 1, GO!)
        
        // Room system
        private string _roomCode = "";
        private bool _isPrivateRoom = false;
        private int _modeSelection = 0; // 0 = Matchmaking, 1 = Private Room
        
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
            // Use the same levels folder as Freeplay
            _levelsFolder = System.IO.Path.Combine(AppContext.BaseDirectory, "mods", "levels");
            
            // Load settings
            var settings = MultiplayerSettings.Instance;
            _username = settings.Username;
            
            // Load available levels from freeplay
            RefreshLevelList();
            
            // Initialize previous keyboard state
            _prevKb = Keyboard.GetState();
            _prevMouse = Mouse.GetState();
            
            // Initialize connection indicator with settings
            _connectionIndicator = new UI.ConnectionIndicator
            {
                Mode = settings.InterfaceMode == MultiplayerSettings.UIMode.Compact 
                    ? UI.ConnectionIndicator.DisplayMode.Compact 
                    : UI.ConnectionIndicator.DisplayMode.Detailed,
                Position = (UI.ConnectionIndicator.IndicatorPosition)((int)settings.ConnectionIndicatorPosition)
            };
            
            // Initialize level downloader
            _levelDownloader = new LevelDownloader();
            _levelDownloader.OnDownloadProgress += (progress) => {
                _downloadProgress = progress;
            };
            _levelDownloader.OnDownloadBytesProgress += (progress, received, total) => {
                _downloadProgress = progress;
                Interlocked.Exchange(ref _downloadBytesReceived, received);
                Interlocked.Exchange(ref _downloadBytesTotal, total);
            };
            _levelDownloader.OnDownloadComplete += (levelId, info) => {
                _isDownloading = false;
                Interlocked.Exchange(ref _pendingAutoMatchmakingLevelId, levelId);
                Console.WriteLine($"[Multiplayer] Downloaded and cached: {info.Name}");
            };
            _levelDownloader.OnDownloadError += (error) => {
                _isDownloading = false;
                Interlocked.Exchange(ref _pendingDownloadError, error);
                Console.WriteLine($"[Multiplayer] Download error: {error}");
            };

            NetworkManager.Instance.OnLeaderboardReceived -= OnLeaderboardReceived;
            NetworkManager.Instance.OnLeaderboardReceived += OnLeaderboardReceived;
            NetworkManager.Instance.OnMatchHistoryReceived -= OnMatchHistoryReceived;
            NetworkManager.Instance.OnMatchHistoryReceived += OnMatchHistoryReceived;
        }

        private void OnLeaderboardReceived(LeaderboardResponsePacket packet)
        {
            if (packet?.Entries == null) return;
            LeaderboardManager.Instance.ApplyServerLeaderboard(packet.Entries);
            _leaderboardSyncedOnline = true;
        }

        private void OnMatchHistoryReceived(MatchHistoryResponsePacket packet)
        {
            if (packet?.Matches == null) return;
            LeaderboardManager.Instance.ApplyServerMatchHistory(packet.Matches);
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
                        string filename = System.IO.Path.GetFileName(file);
                        _availableLevels.Add(filename);
                    }
                }
            }
            
            // If no levels found, add placeholder
            if (_availableLevels.Count == 0)
            {
                Console.WriteLine("[Multiplayer] No levels found in mods/levels/ folder");
            }
        }

        /// <summary>
        /// Update menu logic.
        /// </summary>
        public void Update(GameTime gameTime)
        {
            float dt = (float)gameTime.ElapsedGameTime.TotalSeconds;
            _pulseTimer += dt * PULSE_SPEED;
            _searchTimer += dt;

            var pendingLeaderboard = Interlocked.Exchange(ref _pendingHttpLeaderboard, null);
            if (pendingLeaderboard != null)
            {
                if (pendingLeaderboard.Entries != null)
                    LeaderboardManager.Instance.ApplyServerLeaderboard(pendingLeaderboard.Entries);
                if (pendingLeaderboard.Matches != null)
                    LeaderboardManager.Instance.ApplyServerMatchHistory(pendingLeaderboard.Matches);
                _leaderboardSyncedOnline = true;
            }

            string? dlError = Interlocked.Exchange(ref _pendingDownloadError, null);
            if (!string.IsNullOrEmpty(dlError))
            {
                ShowError($"Download failed: {dlError}");
            }

            string? completedLevelId = Interlocked.Exchange(ref _pendingAutoMatchmakingLevelId, null);
            if (!string.IsNullOrEmpty(completedLevelId) &&
                _state == MenuState.Lobby &&
                _selectedSource == LevelSource.Official &&
                _officialLevels.Count > 0)
            {
                int idx = _officialLevels.FindIndex(l => string.Equals(l.Id, completedLevelId, StringComparison.OrdinalIgnoreCase));
                if (idx >= 0)
                {
                    _levelSelection = idx;
                    StartMatchmaking();
                }
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

            var kb = Keyboard.GetState();
            var mouse = Mouse.GetState();
            
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
            if (!_isEditingText && _state != MenuState.Connecting && _state != MenuState.MatchFound &&
                _inputCooldown <= 0f &&
                (IsKeyPressed(kb, Keys.F1) || IsRectClicked(mouse, _gameplaySettingsRect)))
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
                case MenuState.ModeSelect:
                    UpdateModeSelect(kb, mouse, dt);
                    break;
                case MenuState.Lobby:
                    UpdateLobby(kb, mouse, dt);
                    break;
                case MenuState.CreateRoom:
                    UpdateCreateRoom(kb, mouse, dt);
                    break;
                case MenuState.JoinRoom:
                    UpdateJoinRoom(kb, mouse, dt);
                    break;
                case MenuState.Searching:
                    UpdateSearching(kb, mouse, dt);
                    break;
                case MenuState.MatchFound:
                    UpdateMatchFound(kb, mouse, dt);
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
            _hoveredButton = -1;
            _isSettingFocused = false;
            _isEditingText = false;
            _editingTextValue = "";
            _prevKb = Keyboard.GetState();
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
        
        private void UpdateModeSelect(KeyboardState kb, MouseState mouse, float dt)
        {
            if (_inputCooldown > 0f) return;
            
            // Mouse hover updates selection only when mouse moved
            if (_mouseMovedThisFrame && _hoveredButton >= 0 && _hoveredButton < 2)
            {
                _modeSelection = _hoveredButton;
            }

            // Navigate modes with LEFT/RIGHT or Q/E
            if (IsKeyPressed(kb, Keys.Right) || IsKeyPressed(kb, Keys.E))
            {
                _modeSelection = (_modeSelection + 1) % 2;
                _inputCooldown = INPUT_COOLDOWN_TIME;
            }
            if (IsKeyPressed(kb, Keys.Left) || IsKeyPressed(kb, Keys.Q))
            {
                _modeSelection = (_modeSelection - 1 + 2) % 2;
                _inputCooldown = INPUT_COOLDOWN_TIME;
            }

            // Select mode with keyboard
            if (IsKeyPressed(kb, Keys.Enter) || IsKeyPressed(kb, Keys.Space))
            {
                ExecuteModeSelectAction(_modeSelection);
                return;
            }
            
            // Select mode with mouse single-click
            for (int i = 0; i < 2; i++)
            {
                if (IsButtonClicked(mouse, i))
                {
                    _modeSelection = i;
                    ExecuteModeSelectAction(i);
                    return;
                }
            }

            // Back
            if (IsKeyPressed(kb, Keys.Escape) || IsRectClicked(mouse, _backButtonRect))
            {
                NetworkManager.Instance.Disconnect();
                TransitionToState(MenuState.MainMenu);
            }
        }
        
        private void ExecuteModeSelectAction(int mode)
        {
            if (mode == 0)
            {
                // Quick Play (Matchmaking)
                _isPrivateRoom = false;
                TransitionToState(MenuState.Lobby);
            }
            else
            {
                // Private Room
                _isPrivateRoom = true;
                _selection = 0;
                TransitionToState(MenuState.CreateRoom);
                _roomCode = GenerateRoomCode();
            }
        }
        
        private void UpdateCreateRoom(KeyboardState kb, MouseState mouse, float dt)
        {
            if (_inputCooldown > 0f) return;

            if (_mouseMovedThisFrame && _hoveredButton >= 0 && _hoveredButton < 2)
            {
                _selection = _hoveredButton;
            }

            // Navigate options with LEFT/RIGHT or Q/E
            if (IsKeyPressed(kb, Keys.Right) || IsKeyPressed(kb, Keys.E))
            {
                _selection = (_selection + 1) % 2; // 0 = Create, 1 = Join Existing
                _inputCooldown = INPUT_COOLDOWN_TIME;
            }
            if (IsKeyPressed(kb, Keys.Left) || IsKeyPressed(kb, Keys.Q))
            {
                _selection = (_selection - 1 + 2) % 2;
                _inputCooldown = INPUT_COOLDOWN_TIME;
            }

            if (IsKeyPressed(kb, Keys.Enter) || IsKeyPressed(kb, Keys.Space))
            {
                ExecuteCreateRoomAction(_selection);
                return;
            }

            for (int i = 0; i < 2; i++)
            {
                if (IsButtonClicked(mouse, i))
                {
                    _selection = i;
                    ExecuteCreateRoomAction(i);
                    return;
                }
            }

            // Back
            if (IsKeyPressed(kb, Keys.Escape) || IsRectClicked(mouse, _backButtonRect))
            {
                TransitionToState(MenuState.ModeSelect);
            }
        }

        private void ExecuteCreateRoomAction(int action)
        {
            if (action == 0)
            {
                Console.WriteLine($"[Multiplayer] Creating room: {_roomCode}");
                TransitionToState(MenuState.Lobby);
            }
            else
            {
                _roomCode = "";
                TransitionToState(MenuState.JoinRoom);
            }
        }
        
        private void UpdateJoinRoom(KeyboardState kb, MouseState mouse, float dt)
        {
            // Back / Cancel first
            if (IsKeyPressed(kb, Keys.Escape) || IsRectClicked(mouse, _backButtonRect))
            {
                _selection = 0;
                TransitionToState(MenuState.CreateRoom);
                return;
            }

            // Backspace with hold repeat
            if (kb.IsKeyDown(Keys.Back))
            {
                if (!_prevKb.IsKeyDown(Keys.Back))
                {
                    if (_roomCode.Length > 0)
                        _roomCode = _roomCode.Substring(0, _roomCode.Length - 1);
                    _backspaceHoldTimer = 0f;
                    _backspaceRepeatTimer = 0f;
                }
                else
                {
                    _backspaceHoldTimer += dt;
                    if (_backspaceHoldTimer >= 0.4f)
                    {
                        _backspaceRepeatTimer += dt;
                        if (_backspaceRepeatTimer >= 0.05f && _roomCode.Length > 0)
                        {
                            _roomCode = _roomCode.Substring(0, _roomCode.Length - 1);
                            _backspaceRepeatTimer = 0f;
                        }
                    }
                }
            }
            else
            {
                _backspaceHoldTimer = 0f;
                _backspaceRepeatTimer = 0f;
            }

            // Type room code (A-Z, 0-9, max 6 characters) without inputCooldown interference
            foreach (var key in kb.GetPressedKeys())
            {
                if (_prevKb.IsKeyUp(key) && _roomCode.Length < 6)
                {
                    if (key >= Keys.A && key <= Keys.Z)
                        _roomCode += key.ToString();
                    else if (key >= Keys.D0 && key <= Keys.D9)
                        _roomCode += ((int)key - (int)Keys.D0).ToString();
                    else if (key >= Keys.NumPad0 && key <= Keys.NumPad9)
                        _roomCode += ((int)key - (int)Keys.NumPad0).ToString();
                }
            }

            // Enter or click JOIN button when complete
            if (_roomCode.Length == 6 && (IsKeyPressed(kb, Keys.Enter) || IsButtonClicked(mouse, 0)))
            {
                Console.WriteLine($"[Multiplayer] Joining room: {_roomCode}");
                TransitionToState(MenuState.Lobby);
            }
        }

        private void UpdateLobby(KeyboardState kb, MouseState mouse, float dt)
        {
            if (_inputCooldown > 0f) return;
            if (_isDownloading) return; // Disable input during download

            int currentLevelCount = _selectedSource == LevelSource.Official ? _officialLevels.Count : _availableLevels.Count;

            // Mouse hover highlights level ONLY when mouse moved — NEVER switches source tabs on hover!
            if (_mouseMovedThisFrame && _hoveredButton >= 2)
            {
                int levelIndex = _hoveredButton - 2 + _levelScrollOffset;
                if (levelIndex >= 0 && levelIndex < currentLevelCount)
                    _levelSelection = levelIndex;
            }

            // Mouse wheel scrolls level selection
            int wheelDelta = mouse.ScrollWheelValue - _prevMouse.ScrollWheelValue;
            if (wheelDelta != 0 && currentLevelCount > 0)
            {
                int step = wheelDelta > 0 ? -1 : 1;
                _levelSelection = (_levelSelection + step + currentLevelCount) % currentLevelCount;
                AdjustLobbyScroll();
            }

            // Navigate levels with UP/DOWN
            if (currentLevelCount > 0)
            {
                if (IsKeyPressed(kb, Keys.Down))
                {
                    _levelSelection = (_levelSelection + 1) % currentLevelCount;
                    AdjustLobbyScroll();
                    _inputCooldown = INPUT_COOLDOWN_TIME;
                }
                if (IsKeyPressed(kb, Keys.Up))
                {
                    _levelSelection = (_levelSelection - 1 + currentLevelCount) % currentLevelCount;
                    AdjustLobbyScroll();
                    _inputCooldown = INPUT_COOLDOWN_TIME;
                }
            }

            // Switch between Official and Local with LEFT/RIGHT, Q/E, or tab click
            if (IsKeyPressed(kb, Keys.Left) || IsKeyPressed(kb, Keys.Q) || IsButtonClicked(mouse, 0))
            {
                if (_selectedSource != LevelSource.Official)
                {
                    _selectedSource = LevelSource.Official;
                    _levelSelection = 0;
                    _levelScrollOffset = 0;
                    _inputCooldown = INPUT_COOLDOWN_TIME;
                }
            }
            if (IsKeyPressed(kb, Keys.Right) || IsKeyPressed(kb, Keys.E) || IsButtonClicked(mouse, 1))
            {
                if (_selectedSource != LevelSource.Local)
                {
                    _selectedSource = LevelSource.Local;
                    _levelSelection = 0;
                    _levelScrollOffset = 0;
                    _inputCooldown = INPUT_COOLDOWN_TIME;
                }
            }

            // ENTER or SPACE: Search match OR Download (Official)
            if (IsKeyPressed(kb, Keys.Enter) || IsKeyPressed(kb, Keys.Space))
            {
                ExecuteLobbyAction();
                _inputCooldown = INPUT_COOLDOWN_TIME;
            }
            
            // Click on level items to select and start
            for (int i = 2; i < _clickableAreas.Count; i++)
            {
                if (IsButtonClicked(mouse, i))
                {
                    int levelIndex = i - 2 + _levelScrollOffset;
                    if (levelIndex >= 0 && levelIndex < currentLevelCount)
                    {
                        _levelSelection = levelIndex;
                        ExecuteLobbyAction();
                        _inputCooldown = INPUT_COOLDOWN_TIME;
                        break;
                    }
                }
            }
            
            // R key or clicking Refresh button refreshes both local mods/levels and official server charts
            if (IsKeyPressed(kb, Keys.R) || IsRectClicked(mouse, _refreshButtonRect))
            {
                RefreshLevelList();
                if (NetworkManager.Instance.IsConnected)
                    NetworkManager.Instance.RequestOfficialLevels();
                _inputCooldown = INPUT_COOLDOWN_TIME;
                return;
            }

            // S key: Share level (Local mode only)
            if (IsKeyPressed(kb, Keys.S) && _selectedSource == LevelSource.Local)
            {
                if (_availableLevels.Count > 0 && _levelSelection < _availableLevels.Count)
                    ShareLevel(_availableLevels[_levelSelection]);
                _inputCooldown = INPUT_COOLDOWN_TIME;
            }

            // Back
            if (IsKeyPressed(kb, Keys.Escape) || IsRectClicked(mouse, _backButtonRect))
            {
                TransitionToState(_isPrivateRoom ? MenuState.CreateRoom : MenuState.ModeSelect);
            }
        }

        private void AdjustLobbyScroll()
        {
            if (_levelSelection >= _levelScrollOffset + MAX_VISIBLE_LEVELS)
                _levelScrollOffset = _levelSelection - MAX_VISIBLE_LEVELS + 1;
            else if (_levelSelection < _levelScrollOffset)
                _levelScrollOffset = _levelSelection;
        }
        
        private void ExecuteLobbyAction()
        {
            if (_selectedSource == LevelSource.Official)
            {
                if (_officialLevels.Count > 0 && _levelSelection < _officialLevels.Count)
                {
                    var level = _officialLevels[_levelSelection];
                    
                    if (LevelCache.Instance.IsCached(level.Id) || LevelCache.Instance.TryRestoreFromDisk(level))
                    {
                        StartMatchmaking();
                    }
                    else
                    {
                        DownloadOfficialLevel(level);
                    }
                }
            }
            else // Local
            {
                StartMatchmaking();
            }
        }

        private void UpdateSearching(KeyboardState kb, MouseState mouse, float dt)
        {
            // Cancel search via keyboard or mouse
            if (IsKeyPressed(kb, Keys.Escape) || IsRectClicked(mouse, _backButtonRect))
            {
                NetworkManager.Instance.CancelMatchmaking();
                TransitionToState(MenuState.Lobby);
                _searchTimer = 0f;
            }
        }

        private void UpdateMatchFound(KeyboardState kb, MouseState mouse, float dt)
        {
            if (_inputCooldown > 0f) return;
            
            // Update countdown if active
            if (_isCountdownActive)
            {
                long now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
                long timeSinceStart = now - _countdownStartTime;
                
                if (timeSinceStart < 1000)
                {
                    _countdownValue = 3;
                }
                else if (timeSinceStart < 2000)
                {
                    _countdownValue = 2;
                }
                else if (timeSinceStart < 3000)
                {
                    _countdownValue = 1;
                }
                else
                {
                    _countdownValue = 0;
                    _isCountdownActive = false;
                    Console.WriteLine("[MultiplayerMenu] Countdown complete! Launching GameMultiplayer...");
                    LaunchGameMultiplayer();
                    return;
                }
            }

            // Toggle ready with ENTER, SPACE, or clicking READY button/card (only when countdown is not active)
            if (!_isCountdownActive &&
                (IsKeyPressed(kb, Keys.Enter) || IsKeyPressed(kb, Keys.Space) || IsButtonClicked(mouse, 0)))
            {
                _isReady = !_isReady;
                NetworkManager.Instance.SendReady(_isReady);
                Console.WriteLine($"[Multiplayer] Ready: {_isReady}");
                _inputCooldown = INPUT_COOLDOWN_TIME;
            }

            // Cancel match with ESC or Cancel button (only when countdown is not active)
            if (!_isCountdownActive && (IsKeyPressed(kb, Keys.Escape) || IsRectClicked(mouse, _backButtonRect)))
            {
                NetworkManager.Instance.LeaveMatch(notifyOpponent: true);
                TransitionToState(MenuState.Lobby);
                _opponentName = null;
                _roomId = null;
                _isReady = false;
                _opponentReady = false;
                _isCountdownActive = false;
            }
        }
        
        private void UpdateSettings(KeyboardState kb, MouseState mouse, float dt)
        {
            var settings = MultiplayerSettings.Instance;

            // Handle text editing mode BEFORE cooldown so typed characters are never dropped
            if (_isEditingText)
            {
                HandleTextEditing(kb, mouse, dt);
                return;
            }

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
                        _isEditingText = true;
                        _editingTextValue = settings.Username;
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
            if (IsKeyPressed(kb, Keys.Q))
            {
                _settingsCategory = (_settingsCategory - 1 + 4) % 4;
                _settingsSelection = 0;
                _settingsScrollOffset = 0;
                _isSettingFocused = false;
                _inputCooldown = INPUT_COOLDOWN_TIME;
                return;
            }
            if (IsKeyPressed(kb, Keys.E))
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
                        _isEditingText = true;
                        _editingTextValue = settings.Username;
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
            if (IsKeyPressed(kb, Keys.R))
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
            {
                NetworkManager.Instance.SendPlayerProfile();
                NetworkManager.Instance.SendSettingsSync();
            }
            
            Console.WriteLine("[Settings] Settings saved and synced");
        }
        
        private void HandleTextEditing(KeyboardState kb, MouseState mouse, float dt)
        {
            var settings = MultiplayerSettings.Instance;
            bool shift = kb.IsKeyDown(Keys.LeftShift) || kb.IsKeyDown(Keys.RightShift);
            bool upper = shift ^ kb.CapsLock;

            // Backspace with hold repeat
            if (kb.IsKeyDown(Keys.Back))
            {
                if (!_prevKb.IsKeyDown(Keys.Back))
                {
                    if (_editingTextValue.Length > 0)
                        _editingTextValue = _editingTextValue.Substring(0, _editingTextValue.Length - 1);
                    _backspaceHoldTimer = 0f;
                    _backspaceRepeatTimer = 0f;
                }
                else
                {
                    _backspaceHoldTimer += dt;
                    if (_backspaceHoldTimer >= 0.4f)
                    {
                        _backspaceRepeatTimer += dt;
                        if (_backspaceRepeatTimer >= 0.05f && _editingTextValue.Length > 0)
                        {
                            _editingTextValue = _editingTextValue.Substring(0, _editingTextValue.Length - 1);
                            _backspaceRepeatTimer = 0f;
                        }
                    }
                }
            }
            else
            {
                _backspaceHoldTimer = 0f;
                _backspaceRepeatTimer = 0f;
            }

            foreach (var key in kb.GetPressedKeys())
            {
                if (!_prevKb.IsKeyUp(key) || _editingTextValue.Length >= 20)
                    continue;

                if (key == Keys.Space)
                {
                    _editingTextValue += " ";
                }
                else if (key >= Keys.A && key <= Keys.Z)
                {
                    char c = upper ? key.ToString()[0] : char.ToLower(key.ToString()[0]);
                    _editingTextValue += c;
                }
                else if (key >= Keys.D0 && key <= Keys.D9 && !shift)
                {
                    _editingTextValue += ((int)key - (int)Keys.D0).ToString();
                }
                else if (key >= Keys.NumPad0 && key <= Keys.NumPad9)
                {
                    _editingTextValue += ((int)key - (int)Keys.NumPad0).ToString();
                }
                else if (key == Keys.OemMinus)
                {
                    _editingTextValue += shift ? '_' : '-';
                }
                else if (key == Keys.Subtract)
                {
                    _editingTextValue += '-';
                }
                else if (key == Keys.OemPeriod || key == Keys.Decimal)
                {
                    _editingTextValue += '.';
                }
            }

            // Clicking outside row OR pressing ENTER confirms
            bool clickedElsewhere = IsLeftClickReleased(mouse) &&
                (_clickableAreas.Count == 0 || !_clickableAreas[0].Contains(new Point(mouse.X, mouse.Y)));

            if (IsKeyPressed(kb, Keys.Enter) || clickedElsewhere)
            {
                string trimmed = _editingTextValue.Trim();
                if (!string.IsNullOrWhiteSpace(trimmed))
                {
                    settings.Username = trimmed;
                    _username = trimmed;
                    settings.Save();
                    if (NetworkManager.Instance.IsConnected)
                        NetworkManager.Instance.SendPlayerProfile();
                    Console.WriteLine($"[Settings] Username changed to: {settings.Username}");
                }
                _isEditingText = false;
                _editingTextValue = "";
                _inputCooldown = INPUT_COOLDOWN_TIME;
                return;
            }

            // ESC to cancel
            if (IsKeyPressed(kb, Keys.Escape))
            {
                _isEditingText = false;
                _editingTextValue = "";
                _inputCooldown = INPUT_COOLDOWN_TIME;
                Console.WriteLine("[Settings] Username editing cancelled");
            }
        }
        
        private int GetSettingsCategoryItemCount(int category)
        {
            return category switch
            {
                0 => 2,  // User: Username, Player Color
                1 => 4,  // Gameplay: ShowOpponentNotes, ShowOpponentScore, ShowConnectionQuality, GhostTransparency
                2 => 2,  // Performance: UpdateRate, EnableInterpolation
                _ => 3,  // Visual: HudOpacity, ConnectionIndicatorPosition, InterfaceMode
            };
        }
        
        private void ModifySettingValue(int category, int selection, int direction)
        {
            var settings = MultiplayerSettings.Instance;
            
            switch (category)
            {
                case 0: // User
                    if (selection == 1) // Player Color
                        settings.PlayerColorHue = (settings.PlayerColorHue + direction * 10 + 360) % 360;
                    break;
                    
                case 1: // Gameplay
                    if (selection == 3) // GhostTransparency
                        settings.GhostTransparency = Math.Clamp(settings.GhostTransparency + direction * 10, 0, 100);
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
                if (category == 0) NetworkManager.Instance.SendPlayerProfile();
                else if (category == 2) NetworkManager.Instance.SendSettingsSync();
            }
        }
        
        private void ToggleSettingValue(int category, int selection)
        {
            var settings = MultiplayerSettings.Instance;
            
            switch (category)
            {
                case 0: // User
                    if (selection == 0) // Username - start text editing
                    {
                        _isEditingText = true;
                        _editingTextValue = settings.Username;
                        Console.WriteLine("[Settings] Started editing username");
                    }
                    break;
                    
                case 1: // Gameplay
                    switch (selection)
                    {
                        case 0: settings.ShowOpponentNotes = !settings.ShowOpponentNotes; break;
                        case 1: settings.ShowOpponentScore = !settings.ShowOpponentScore; break;
                        case 2: settings.ShowConnectionQuality = !settings.ShowConnectionQuality; break;
                    }
                    break;
                    
                case 2: // Performance
                    if (selection == 1) // EnableInterpolation
                        settings.EnableInterpolation = !settings.EnableInterpolation;
                    break;
            }
            
            settings.Save();
            if (NetworkManager.Instance.IsConnected && category == 2)
                NetworkManager.Instance.SendSettingsSync();
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
            NetworkManager.Instance.OnMatchFound -= OnMatchFound;
            NetworkManager.Instance.OnMatchFound += OnMatchFound;
            NetworkManager.Instance.OnOpponentProfileReceived -= OnMenuOpponentProfile;
            NetworkManager.Instance.OnOpponentProfileReceived += OnMenuOpponentProfile;
            NetworkManager.Instance.OnOpponentDisconnected -= OnMenuOpponentDisconnected;
            NetworkManager.Instance.OnOpponentDisconnected += OnMenuOpponentDisconnected;
            NetworkManager.Instance.OnOpponentReadyChanged -= OnOpponentReadyChanged;
            NetworkManager.Instance.OnOpponentReadyChanged += OnOpponentReadyChanged;
            NetworkManager.Instance.OnCountdownStart -= OnCountdownStart;
            NetworkManager.Instance.OnCountdownStart += OnCountdownStart;
            NetworkManager.Instance.OnGameStart -= OnGameStart;
            NetworkManager.Instance.OnGameStart += OnGameStart;
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
        /// Trigger a smooth state transition and clear stale input/hitboxes.
        /// </summary>
        private void TransitionToState(MenuState newState)
        {
            _state = newState;
            _stateTransitionTimer = TRANSITION_DURATION;
            _fadeAlpha = 0f;
            ResetHitboxes();
        }

        /// <summary>
        /// Start matchmaking for selected level.
        /// </summary>
        private void StartMatchmaking()
        {
            if (_selectedSource == LevelSource.Official)
            {
                if (_officialLevels.Count == 0)
                {
                    Console.WriteLine("[Multiplayer] No official levels available");
                    return;
                }

                if (_levelSelection >= _officialLevels.Count)
                    _levelSelection = 0;

                var level = _officialLevels[_levelSelection];
                
                // Check if cached in memory or verified disk cache
                if (!LevelCache.Instance.IsCached(level.Id) && !LevelCache.Instance.TryRestoreFromDisk(level))
                {
                    Console.WriteLine("[Multiplayer] Level not cached, download first");
                    return;
                }

                // Create temporary file for gameplay (reuses unchanged temp file)
                string? tempPath = LevelCache.Instance.WriteTempFile(level.Id);
                if (tempPath == null)
                {
                    Console.WriteLine("[Multiplayer] Failed to create temp file");
                    return;
                }

                // Reuse cached SHA-256 hash instead of re-reading 20+ MB from disk
                string actualHash = LevelCache.Instance.GetCachedHash(level.Id);
                if (string.IsNullOrEmpty(actualHash))
                    actualHash = LevelHasher.CalculateHash(tempPath);
                
                _state = MenuState.Searching;
                _searchTimer = 0f;
                NetworkManager.Instance.FindMatch(actualHash, level.Name, LevelSource.Official, tempPath, _isPrivateRoom ? _roomCode : "");
            }
            else // Local
            {
                if (_availableLevels.Count == 0)
                {
                    Console.WriteLine("[Multiplayer] No local levels available");
                    return;
                }

                string levelFilename = _availableLevels[_levelSelection];
                string levelPath = System.IO.Path.Combine(_levelsFolder, levelFilename);
                string levelHash = LevelHasher.CalculateHash(levelPath);
                
                if (string.IsNullOrEmpty(levelHash))
                {
                    Console.WriteLine("[Multiplayer] Failed to calculate level hash");
                    return;
                }

                _state = MenuState.Searching;
                _searchTimer = 0f;
                NetworkManager.Instance.FindMatch(levelHash, levelFilename, LevelSource.Local, levelPath, _isPrivateRoom ? _roomCode : "");
            }
        }

        /// <summary>
        /// Generate hash for level using SHA256.
        /// </summary>
        private string GenerateLevelHash(string levelFilename)
        {
            string levelPath = System.IO.Path.Combine(_levelsFolder, levelFilename);
            return LevelHasher.CalculateHash(levelPath);
        }
        
        /// <summary>
        /// Download an official level from the server to memory cache.
        /// </summary>
        private async void DownloadOfficialLevel(OfficialLevelInfo level)
        {
            if (_levelDownloader == null) return;
            if (_isDownloading) return;

            _isDownloading = true;
            _downloadProgress = 0;
            Interlocked.Exchange(ref _downloadBytesReceived, 0);
            Interlocked.Exchange(ref _downloadBytesTotal, level.FileSize);

            Console.WriteLine($"[Multiplayer] Downloading: {level.Name}");

            // Get server base URL (remove /multiplayer)
            string serverUrl = NetworkManager.Instance.ServerUrl.Replace("/multiplayer", "").TrimEnd('/');

            await _levelDownloader.DownloadLevel(level, serverUrl);
        }
        
        /// <summary>
        /// Share a local level (show path for manual sharing).
        /// </summary>
        private void ShareLevel(string levelFilename)
        {
            string levelPath = System.IO.Path.GetFullPath(System.IO.Path.Combine(_levelsFolder, levelFilename));
            
            Console.WriteLine("==============================================");
            Console.WriteLine("[MULTIPLAYER] SHARE THIS LEVEL:");
            Console.WriteLine($"File: {levelPath}");
            Console.WriteLine("");
            Console.WriteLine("HOW TO SHARE:");
            Console.WriteLine("1. Upload this file to Discord/Google Drive/Dropbox");
            Console.WriteLine("2. Send the download link to your friend");
            Console.WriteLine("3. Friend downloads and puts in their levels/ folder");
            Console.WriteLine("4. Both search for match with this level!");
            Console.WriteLine("==============================================");
            
            // Also calculate and show hash for verification
            string hash = LevelHasher.CalculateHash(levelPath);
            if (!string.IsNullOrEmpty(hash))
            {
                Console.WriteLine($"[INFO] Level Hash: {hash[..16]}... (for verification)");
            }
        }

        /// <summary>
        /// Generate a random 6-character room code (letters and numbers).
        /// </summary>
        private string GenerateRoomCode()
        {
            const string chars = "ABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789";
            var random = new Random();
            var code = new char[6];
            for (int i = 0; i < 6; i++)
            {
                code[i] = chars[random.Next(chars.Length)];
            }
            return new string(code);
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
            TransitionToState(MenuState.ModeSelect);
            _modeSelection = 0;
            NetworkManager.Instance.RequestOfficialLevels();
            NetworkManager.Instance.RequestGlobalLeaderboard(0, 50);
            NetworkManager.Instance.RequestMatchHistory(null, 0, 50);
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

        private void OnMatchFound(string opponentName, string roomId, int playerRole)
        {
            Console.WriteLine($"[Multiplayer] Match found! Opponent: {opponentName}, Role: Player {playerRole}");
            _opponentName = string.IsNullOrWhiteSpace(NetworkManager.Instance.OpponentName) ? opponentName : NetworkManager.Instance.OpponentName;
            _roomId = roomId;
            TransitionToState(MenuState.MatchFound);
            _isReady = false;
            _opponentReady = false;
            _isCountdownActive = false;
        }

        private void OnMenuOpponentProfile(PlayerProfilePacket packet)
        {
            if (!string.IsNullOrWhiteSpace(packet.Username))
                _opponentName = packet.Username;
        }

        private void OnMenuOpponentDisconnected(string reason)
        {
            if (_state != MenuState.MatchFound)
                return;

            _opponentName = null;
            _roomId = null;
            _isReady = false;
            _opponentReady = false;
            _isCountdownActive = false;
            _countdownValue = 0f;
            TransitionToState(MenuState.Lobby);
            ShowError(string.IsNullOrWhiteSpace(reason) ? "Opponent left the room" : reason);
        }
        
        private void OnOpponentReadyChanged(bool isReady)
        {
            Console.WriteLine($"[Multiplayer] Opponent ready: {isReady}");
            _opponentReady = isReady;
        }
        
        private void OnCountdownStart(long countdownStartTime)
        {
            Console.WriteLine($"[Multiplayer] Countdown starting at: {countdownStartTime}");
            _isCountdownActive = true;
            _countdownStartTime = countdownStartTime;
        }
        
        private void OnGameStart(long startTimestamp, int playerRole)
        {
            Console.WriteLine($"[Multiplayer] Game starting at timestamp: {startTimestamp}, role: {playerRole}");
            _gameStartTimestamp = startTimestamp;
        }

        private void OnLevelListReceived(LevelListPacket packet)
        {
            Console.WriteLine($"[Multiplayer] Received {packet.Levels.Count} official levels");
            _officialLevels = packet.Levels;
            _ = Task.Run(() => LevelCache.Instance.WarmFromDisk(packet.Levels));
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
                case MenuState.ModeSelect:
                    DrawModeSelectModern(sb, font, pixel, screenWidth, screenHeight);
                    break;
                case MenuState.Lobby:
                    DrawLobbyModern(sb, font, pixel, screenWidth, screenHeight);
                    break;
                case MenuState.CreateRoom:
                    DrawCreateRoomModern(sb, font, pixel, screenWidth, screenHeight);
                    break;
                case MenuState.JoinRoom:
                    DrawJoinRoomModern(sb, font, pixel, screenWidth, screenHeight);
                    break;
                case MenuState.Searching:
                    DrawSearchingModern(sb, font, pixel, screenWidth, screenHeight);
                    break;
                case MenuState.MatchFound:
                    DrawMatchFoundModern(sb, font, pixel, screenWidth, screenHeight);
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
                _state != MenuState.Connecting)
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
                DrawErrorMessage(sb, font, pixel, screenWidth, screenHeight);
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
            var words = text.Split(' ');
            var sb = new System.Text.StringBuilder();
            string line = "";
            foreach (var w in words)
            {
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
            string label = "F1  Keybinds & Gameplay";
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
            string label = "F1  Keybinds & Gameplay";
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
        /// Draw error message toast at the top of the screen.
        /// </summary>
        private void DrawErrorMessage(SpriteBatch sb, SpriteFont font, Texture2D pixel, int screenWidth, int screenHeight)
        {
            float uiScale = GetUiScale(screenWidth, screenHeight);
            float alpha = Math.Min(1f, _errorMessageTimer / 0.35f);
            if (_errorMessageTimer < 0.5f) alpha = Math.Min(alpha, _errorMessageTimer / 0.5f);

            int fontSize = FontPx(16, uiScale, 12, 24);
            int maxW = Math.Clamp(Px(620, uiScale), 280, Math.Max(280, screenWidth - 48));
            string wrapped = WrapTextToWidth(font, _errorMessage, maxW - Px(72, uiScale), fontSize);
            Vector2 textSz = MeasureText(font, wrapped, fontSize);

            int errorW = Math.Clamp((int)MathF.Ceiling(textSz.X) + Px(84, uiScale), Px(340, uiScale), maxW);
            int errorH = Math.Max(Px(54, uiScale), (int)MathF.Ceiling(textSz.Y) + Px(24, uiScale));
            int errorX = screenWidth / 2 - errorW / 2;
            int errorY = Px(20, uiScale, 12);

            sb.Draw(pixel, new Rectangle(errorX + 3, errorY + 4, errorW, errorH), Color.Black * (0.4f * alpha));
            sb.Draw(pixel, new Rectangle(errorX, errorY, errorW, errorH), new Color(36, 16, 24) * (0.95f * alpha));
            DrawBorder(sb, pixel, errorX, errorY, errorW, errorH, ACCENT_ERROR * alpha, 2);
            sb.Draw(pixel, new Rectangle(errorX, errorY, 4, errorH), ACCENT_ERROR * alpha);

            // Alert badge
            int badgeSize = Px(26, uiScale, 18);
            int badgeX = errorX + Px(16, uiScale);
            int badgeY = errorY + (errorH - badgeSize) / 2;
            sb.Draw(pixel, new Rectangle(badgeX, badgeY, badgeSize, badgeSize), ACCENT_ERROR * (0.25f * alpha));
            DrawBorder(sb, pixel, badgeX, badgeY, badgeSize, badgeSize, ACCENT_ERROR * alpha, 1);
            Vector2 bangSz = MeasureText(font, "!", fontSize);
            DrawText(sb, font, "!", new Vector2(badgeX + (badgeSize - bangSz.X) / 2f, badgeY + (badgeSize - bangSz.Y) / 2f), ACCENT_ERROR * alpha, fontSize);

            Vector2 textPos = new Vector2(badgeX + badgeSize + Px(14, uiScale), errorY + (errorH - textSz.Y) / 2f);
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
            int lineW = Math.Clamp((int)(titleSz.X * 0.92f), Px(180, uiScale), screenWidth - 80);
            int lineX = screenWidth / 2 - lineW / 2;
            sb.Draw(pixel, new Rectangle(lineX, accentY, lineW, Math.Max(2, Px(3, uiScale))), ACCENT_PRIMARY * (0.55f * _fadeAlpha));

            string subtitle = "Real-time 1v1 battles, private rooms & global rankings";
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
            int cardHeight = Math.Clamp((availableH - cardSpacing * 3) / 4, 48, Px(84, uiScale));
            int totalStackH = cardHeight * 4 + cardSpacing * 3;
            int startY = contentTop + Math.Max(0, (availableH - totalStackH) / 2);
            int cardWidth = Math.Clamp(Px(460, uiScale), 300, screenWidth - Px(60, uiScale));
            int cardX = screenWidth / 2 - cardWidth / 2;

            string[] options = { "CONNECT TO SERVER", "LEADERBOARD", "SETTINGS", "BACK TO MENU" };
            string[] sublabels =
            {
                "Quick Play matchmaking or Private Rooms",
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
                int iconBoxSize = Math.Clamp(cardHeight - Px(22, uiScale), 28, Px(52, uiScale));
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

            int panelW = Math.Clamp(Px(520, uiScale), 300, screenWidth - 40);
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
            int barW = Math.Clamp(panelW - Px(96, uiScale), 180, Px(380, uiScale));
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

        private void DrawModeSelectModern(SpriteBatch sb, SpriteFont font, Texture2D pixel, int screenWidth, int screenHeight)
        {
            _clickableAreas.Clear();
            float uiScale = GetUiScale(screenWidth, screenHeight);

            int headerBottom = DrawScreenHeader(sb, font, pixel, screenWidth, uiScale,
                "SELECT GAME MODE", "Choose how you want to find an opponent", ACCENT_PRIMARY, "ESC  Disconnect");
            DrawGameplaySettingsButtonTopRight(sb, font, pixel, screenWidth, uiScale);
            int chipRightX = _backButtonRect != Rectangle.Empty ? _backButtonRect.Right : screenWidth - Px(24, uiScale, 12);
            int chipY = Math.Max(_backButtonRect.Bottom, _gameplaySettingsRect.Bottom) + Px(8, uiScale);
            DrawPlayerIdentityChip(sb, font, pixel, chipRightX, chipY, uiScale, alignRight: true);
            int footerY = DrawFooterBar(sb, font, pixel, screenWidth, screenHeight, uiScale,
                "LEFT/RIGHT or Q/E: Select Mode  |  ENTER or CLICK: Confirm  |  ESC: Disconnect");

            int spacing = Px(36, uiScale, 16);
            int maxCardW = (screenWidth - Px(80, uiScale) - spacing) / 2;
            int cardW = Math.Clamp(Px(380, uiScale), 240, maxCardW);
            int availH = Math.Max(220, footerY - headerBottom - Px(24, uiScale));
            int cardH = Math.Clamp(Px(330, uiScale), 220, availH);
            int totalW = cardW * 2 + spacing;
            int startX = screenWidth / 2 - totalW / 2;
            int cardY = headerBottom + Math.Max(8, (availH - cardH) / 2);

            DrawModeCard(sb, font, pixel, uiScale, startX, cardY, cardW, cardH,
                selected: _modeSelection == 0,
                accent: ACCENT_PRIMARY,
                iconKind: 7,
                title: "QUICK PLAY",
                subtitle: "Random Matchmaking",
                feature1: "Instant public queue",
                feature2: "Fast & casual battles",
                badgeText: "MATCHMAKING");

            DrawModeCard(sb, font, pixel, uiScale, startX + cardW + spacing, cardY, cardW, cardH,
                selected: _modeSelection == 1,
                accent: ACCENT_SUCCESS,
                iconKind: 4,
                title: "PRIVATE ROOM",
                subtitle: "Play with Friends",
                feature1: "Create a 6-char room code",
                feature2: "Invite-only lobbies",
                badgeText: "PRIVATE LOBBY");
        }

        private void DrawModeCard(SpriteBatch sb, SpriteFont font, Texture2D pixel, float uiScale, int x, int y, int w, int h,
            bool selected, Color accent, int iconKind, string title, string subtitle, string feature1, string feature2, string badgeText)
        {
            _clickableAreas.Add(new Rectangle(x, y, w, h));

            Color bg = selected ? BG_LIGHT : BG_MEDIUM;
            Color border = selected ? accent : BORDER_SUBTLE;
            if (selected)
            {
                float pulse = (float)Math.Sin(_pulseTimer * 1.4f) * 0.25f + 0.75f;
                sb.Draw(pixel, new Rectangle(x - 6, y - 6, w + 12, h + 12), accent * (0.14f * pulse * _fadeAlpha));
            }
            DrawPanel(sb, pixel, x, y, w, h, bg, border, selected ? 2 : 1);

            // Top color strip
            sb.Draw(pixel, new Rectangle(x, y, w, Px(4, uiScale, 3)), (selected ? accent : accent * 0.4f) * _fadeAlpha);

            // Badge pill at top center
            int badgeFont = FontPx(11, uiScale, 10, 16);
            int badgeH = Px(24, uiScale, 20);
            Vector2 bSz = MeasureText(font, badgeText, badgeFont);
            int badgeW = (int)MathF.Ceiling(bSz.X) + Px(20, uiScale);
            DrawPillBadge(sb, font, pixel, x + w / 2 - badgeW / 2, y + Px(22, uiScale), badgeH, badgeText, accent, badgeFont, Px(10, uiScale), filledStrong: selected);

            // Emblem box
            int emblemSize = Px(56, uiScale, 40);
            int emblemX = x + w / 2 - emblemSize / 2;
            int emblemY = y + Px(62, uiScale);
            sb.Draw(pixel, new Rectangle(emblemX, emblemY, emblemSize, emblemSize), accent * ((selected ? 0.22f : 0.1f) * _fadeAlpha));
            DrawBorder(sb, pixel, emblemX, emblemY, emblemSize, emblemSize, accent * ((selected ? 0.9f : 0.4f) * _fadeAlpha), 1);
            DrawMenuIcon(sb, pixel, x + w / 2, emblemY + emblemSize / 2, emblemSize - Px(14, uiScale), iconKind, selected ? accent : accent * 0.75f);

            int titleY = emblemY + emblemSize + Px(18, uiScale);
            DrawCenteredTextInPanel(sb, font, title, x, titleY, w, selected ? accent : TEXT_PRIMARY, FontPx(23, uiScale, 16, 32), shadow: selected);
            DrawCenteredTextInPanel(sb, font, subtitle, x, titleY + Px(32, uiScale), w, selected ? TEXT_PRIMARY : TEXT_SECONDARY, FontPx(15, uiScale, 12, 22));

            int divY = titleY + Px(66, uiScale);
            sb.Draw(pixel, new Rectangle(x + Px(36, uiScale), divY, w - Px(72, uiScale), 1), Color.White * (0.08f * _fadeAlpha));

            int featSize = FontPx(13, uiScale, 10, 18);
            DrawCenteredTextInPanel(sb, font, feature1, x, divY + Px(14, uiScale), w, TEXT_SECONDARY, featSize);
            DrawCenteredTextInPanel(sb, font, feature2, x, divY + Px(36, uiScale), w, TEXT_DISABLED, featSize);
        }

        private void DrawCreateRoomModern(SpriteBatch sb, SpriteFont font, Texture2D pixel, int screenWidth, int screenHeight)
        {
            float uiScale = GetUiScale(screenWidth, screenHeight);
            int headerBottom = DrawScreenHeader(sb, font, pixel, screenWidth, uiScale,
                "PRIVATE ROOM", "Host a private room or join using a friend's code", ACCENT_SUCCESS);
            int footerY = DrawFooterBar(sb, font, pixel, screenWidth, screenHeight, uiScale,
                "LEFT/RIGHT: Select Option  |  ENTER or CLICK: Confirm  |  ESC: Back");

            int panelW = Math.Clamp(Px(680, uiScale), 340, screenWidth - 48);
            int availH = Math.Max(260, footerY - headerBottom - Px(20, uiScale));
            int panelH = Math.Clamp(Px(430, uiScale), 260, availH);
            int panelX = screenWidth / 2 - panelW / 2;
            int panelY = headerBottom + Math.Max(6, (availH - panelH) / 2);

            DrawPanel(sb, pixel, panelX, panelY, panelW, panelH, BG_MEDIUM, ACCENT_SUCCESS * 0.7f, 2);

            int cardGap = Px(28, uiScale, 14);
            int cardW = (panelW - Px(64, uiScale) - cardGap) / 2;
            int cardH = Px(132, uiScale, 92);
            int cardsY = panelY + Px(34, uiScale);

            int card1X = panelX + Px(32, uiScale);
            int card2X = card1X + cardW + cardGap;

            DrawRoomActionCard(sb, font, pixel, uiScale, card1X, cardsY, cardW, cardH,
                selected: _selection == 0, accent: ACCENT_SUCCESS, iconKind: 5,
                title: "CREATE ROOM", subtitle: "Generate room code");
            DrawRoomActionCard(sb, font, pixel, uiScale, card2X, cardsY, cardW, cardH,
                selected: _selection == 1, accent: ACCENT_PRIMARY, iconKind: 6,
                title: "JOIN ROOM", subtitle: "Enter 6-char code");

            int bottomAreaY = cardsY + cardH + Px(28, uiScale);
            int divW = panelW - Px(64, uiScale);
            sb.Draw(pixel, new Rectangle(panelX + Px(32, uiScale), bottomAreaY - Px(14, uiScale), divW, 1), Color.White * (0.08f * _fadeAlpha));

            if (_selection == 0 && !string.IsNullOrEmpty(_roomCode))
            {
                DrawCenteredTextInPanel(sb, font, "YOUR PRIVATE ROOM CODE", panelX, bottomAreaY, panelW, TEXT_SECONDARY, FontPx(13, uiScale, 11, 18));

                int slotW = Px(44, uiScale, 32);
                int slotH = Px(56, uiScale, 40);
                int slotGap = Px(8, uiScale, 5);
                int totalCodeW = _roomCode.Length * slotW + Math.Max(0, _roomCode.Length - 1) * slotGap;
                int codeStartX = screenWidth / 2 - totalCodeW / 2;
                int codeBoxY = bottomAreaY + Px(28, uiScale);
                float pulse = (float)Math.Sin(_pulseTimer * 2.2f) * 0.25f + 0.75f;
                int charFont = FontPx(26, uiScale, 18, 36);

                for (int i = 0; i < _roomCode.Length; i++)
                {
                    int sx = codeStartX + i * (slotW + slotGap);
                    DrawPanel(sb, pixel, sx, codeBoxY, slotW, slotH, BG_DARK, ACCENT_WARNING * pulse, 2);
                    string ch = _roomCode[i].ToString();
                    Vector2 cSz = MeasureText(font, ch, charFont);
                    DrawText(sb, font, ch, new Vector2(sx + (slotW - cSz.X) / 2f, codeBoxY + (slotH - cSz.Y) / 2f), ACCENT_WARNING * _fadeAlpha, charFont, shadow: true);
                }

                DrawCenteredTextInPanel(sb, font, "Share this code with your friend, then press ENTER to open lobby",
                    panelX, codeBoxY + slotH + Px(16, uiScale), panelW, TEXT_SECONDARY, FontPx(14, uiScale, 11, 20));
            }
            else if (_selection == 0)
            {
                DrawCenteredTextInPanel(sb, font, "Press ENTER to host a new Private Room and receive a code",
                    panelX, bottomAreaY + Px(36, uiScale), panelW, TEXT_SECONDARY, FontPx(15, uiScale, 12, 22));
            }
            else
            {
                DrawCenteredTextInPanel(sb, font, "Press ENTER to type your friend's 6-character invite code",
                    panelX, bottomAreaY + Px(36, uiScale), panelW, TEXT_SECONDARY, FontPx(15, uiScale, 12, 22));
            }
        }

        private void DrawRoomActionCard(SpriteBatch sb, SpriteFont font, Texture2D pixel, float uiScale, int x, int y, int w, int h,
            bool selected, Color accent, int iconKind, string title, string subtitle)
        {
            _clickableAreas.Add(new Rectangle(x, y, w, h));
            DrawCard(sb, font, pixel, x, y, w, h, "", "", selected, glowing: true, accentOverride: accent);

            int iconSize = Px(36, uiScale, 26);
            int iconX = x + w / 2 - iconSize / 2;
            int iconY = y + Px(18, uiScale);
            sb.Draw(pixel, new Rectangle(iconX, iconY, iconSize, iconSize), accent * ((selected ? 0.22f : 0.1f) * _fadeAlpha));
            DrawBorder(sb, pixel, iconX, iconY, iconSize, iconSize, accent * ((selected ? 0.85f : 0.35f) * _fadeAlpha), 1);
            DrawMenuIcon(sb, pixel, x + w / 2, iconY + iconSize / 2, iconSize - Px(10, uiScale), iconKind, selected ? accent : accent * 0.75f);

            int titleY = iconY + iconSize + Px(12, uiScale);
            DrawCenteredTextInPanel(sb, font, title, x, titleY, w, selected ? accent : TEXT_PRIMARY, FontPx(18, uiScale, 13, 26), shadow: selected);
            DrawCenteredTextInPanel(sb, font, subtitle, x, titleY + Px(26, uiScale), w, TEXT_SECONDARY, FontPx(13, uiScale, 10, 18));
        }

        private void DrawJoinRoomModern(SpriteBatch sb, SpriteFont font, Texture2D pixel, int screenWidth, int screenHeight)
        {
            float uiScale = GetUiScale(screenWidth, screenHeight);
            int headerBottom = DrawScreenHeader(sb, font, pixel, screenWidth, uiScale,
                "JOIN PRIVATE ROOM", "Enter the 6-character code shared by your friend", ACCENT_SUCCESS, "ESC  Cancel");
            int footerY = DrawFooterBar(sb, font, pixel, screenWidth, screenHeight, uiScale,
                "TYPE: A-Z / 0-9  |  BACKSPACE: Delete  |  ENTER or CLICK Join: Connect  |  ESC: Cancel");

            int panelW = Math.Clamp(Px(600, uiScale), 340, screenWidth - 48);
            int availH = Math.Max(240, footerY - headerBottom - Px(20, uiScale));
            int panelH = Math.Clamp(Px(350, uiScale), 230, availH);
            int panelX = screenWidth / 2 - panelW / 2;
            int panelY = headerBottom + Math.Max(6, (availH - panelH) / 2);

            DrawPanel(sb, pixel, panelX, panelY, panelW, panelH, BG_MEDIUM, ACCENT_SUCCESS * 0.75f, 2);

            DrawCenteredTextInPanel(sb, font, "ROOM INVITE CODE", panelX, panelY + Px(36, uiScale), panelW, TEXT_SECONDARY, FontPx(14, uiScale, 11, 20));

            int slotW = Px(58, uiScale, 38);
            int slotH = Px(74, uiScale, 50);
            int slotSpacing = Px(14, uiScale, 8);
            int totalW = slotW * 6 + slotSpacing * 5;
            if (totalW > panelW - 40)
            {
                slotW = Math.Max(30, (panelW - 40 - slotSpacing * 5) / 6);
                totalW = slotW * 6 + slotSpacing * 5;
            }

            int startX = screenWidth / 2 - totalW / 2;
            int slotY = panelY + Px(82, uiScale);
            int charFont = FontPx(30, uiScale, 20, 42);

            for (int i = 0; i < 6; i++)
            {
                int slotX = startX + i * (slotW + slotSpacing);
                bool hasChar = i < _roomCode.Length;
                bool isCurrent = i == _roomCode.Length;

                Color slotBg = hasChar ? BG_LIGHT : BG_DARK;
                float pulse = (float)Math.Sin(_pulseTimer * 4f) * 0.35f + 0.65f;
                Color slotBorder = isCurrent ? ACCENT_WARNING * pulse : (hasChar ? ACCENT_SUCCESS : BORDER_SUBTLE);

                DrawPanel(sb, pixel, slotX, slotY, slotW, slotH, slotBg, slotBorder, (isCurrent || hasChar) ? 2 : 1);

                if (hasChar)
                {
                    string c = _roomCode[i].ToString();
                    Vector2 charSz = MeasureText(font, c, charFont);
                    DrawText(sb, font, c, new Vector2(slotX + (slotW - charSz.X) / 2f, slotY + (slotH - charSz.Y) / 2f), TEXT_PRIMARY * _fadeAlpha, charFont, shadow: true);
                }
                else if (isCurrent)
                {
                    // Animated cursor underline
                    int caretW = slotW - Px(18, uiScale, 10);
                    sb.Draw(pixel, new Rectangle(slotX + (slotW - caretW) / 2, slotY + slotH - Px(14, uiScale), caretW, 3), ACCENT_WARNING * (pulse * _fadeAlpha));
                }
            }

            int actionY = slotY + slotH + Px(42, uiScale);
            if (_roomCode.Length < 6)
            {
                string prompt = $"Type {6 - _roomCode.Length} more character{(6 - _roomCode.Length == 1 ? "" : "s")} (A-Z, 0-9)...";
                DrawCenteredTextInPanel(sb, font, prompt, panelX, actionY + Px(8, uiScale), panelW, TEXT_SECONDARY, FontPx(15, uiScale, 12, 22));
            }
            else
            {
                int btnW = Px(240, uiScale, 180);
                int btnH = Px(50, uiScale, 38);
                int btnX = screenWidth / 2 - btnW / 2;
                var joinRect = new Rectangle(btnX, actionY, btnW, btnH);
                _clickableAreas.Add(joinRect);
                bool hovered = joinRect.Contains(_lastMousePos);
                DrawPanel(sb, pixel, btnX, actionY, btnW, btnH, hovered ? ACCENT_SUCCESS : new Color(36, 138, 78), Color.Lerp(ACCENT_SUCCESS, Color.White, hovered ? 0.7f : 0.25f), 2);
                int btnFont = FontPx(18, uiScale, 14, 26);
                Vector2 bSz = MeasureText(font, "JOIN ROOM", btnFont);
                DrawText(sb, font, "JOIN ROOM", new Vector2(btnX + (btnW - bSz.X) / 2f, actionY + (btnH - bSz.Y) / 2f), Color.White * _fadeAlpha, btnFont, shadow: true);
            }
        }

        private void DrawLobbyModern(SpriteBatch sb, SpriteFont font, Texture2D pixel, int screenWidth, int screenHeight)
        {
            _clickableAreas.Clear();
            float uiScale = GetUiScale(screenWidth, screenHeight);

            string title = (_isPrivateRoom && !string.IsNullOrEmpty(_roomCode))
                ? $"PRIVATE ROOM  •  {_roomCode}"
                : "QUICK PLAY LOBBY";
            Color headerAccent = _isPrivateRoom ? ACCENT_SUCCESS : ACCENT_PRIMARY;
            string subtitle = _isPrivateRoom
                ? "Pick a chart to play in your private lobby"
                : "Choose an Official or Local chart to enter matchmaking";

            int headerBottom = DrawScreenHeader(sb, font, pixel, screenWidth, uiScale, title, subtitle, headerAccent);
            DrawGameplaySettingsButtonTopRight(sb, font, pixel, screenWidth, uiScale);
            int footerY = DrawFooterBar(sb, font, pixel, screenWidth, screenHeight, uiScale,
                "Q/E: Source  |  UP/DOWN: Select  |  ENTER: Play  |  R: Refresh  |  F1: Keybinds & Gameplay");

            // Download progress takes center stage while downloading official chart to RAM
            if (_isDownloading)
            {
                int dlW = Math.Clamp(Px(580, uiScale), 300, screenWidth - 48);
                int dlH = Px(180, uiScale, 140);
                int dlX = screenWidth / 2 - dlW / 2;
                int dlY = headerBottom + Math.Max(12, (footerY - headerBottom - dlH) / 2);

                DrawPanel(sb, pixel, dlX, dlY, dlW, dlH, BG_MEDIUM, ACCENT_PRIMARY, 2, ACCENT_PRIMARY);
                DrawCenteredTextInPanel(sb, font, "DOWNLOADING OFFICIAL LEVEL", dlX, dlY + Px(26, uiScale), dlW, TEXT_PRIMARY, FontPx(20, uiScale, 14, 28), shadow: true);

                int barW = dlW - Px(80, uiScale);
                int barH = Px(24, uiScale, 16);
                int barX = screenWidth / 2 - barW / 2;
                int barY = dlY + Px(72, uiScale);

                sb.Draw(pixel, new Rectangle(barX, barY, barW, barH), Color.Black * (0.6f * _fadeAlpha));
                DrawBorder(sb, pixel, barX, barY, barW, barH, BORDER_SUBTLE * _fadeAlpha, 1);

                int progress = _downloadProgress;
                long recBytes = Interlocked.Read(ref _downloadBytesReceived);
                long totBytes = Interlocked.Read(ref _downloadBytesTotal);
                float pct = Math.Clamp(progress / 100f, 0f, 1f);
                int progressW = (int)(barW * pct);
                Color progressColor = Color.Lerp(ACCENT_PRIMARY, ACCENT_SUCCESS, pct);
                if (progressW > 0)
                    sb.Draw(pixel, new Rectangle(barX, barY, progressW, barH), progressColor * _fadeAlpha);

                string progressLabel = totBytes > 0
                    ? $"{progress}%  ({recBytes / (1024.0 * 1024.0):F1} / {totBytes / (1024.0 * 1024.0):F1} MB)"
                    : $"{progress}%";
                DrawCenteredTextInPanel(sb, font, progressLabel, dlX, barY + Px(2, uiScale), dlW, Color.White, FontPx(14, uiScale, 11, 20), shadow: true);
                DrawCenteredTextInPanel(sb, font, "SHA-256 verified cache — starting matchmaking once complete...", dlX, dlY + Px(120, uiScale), dlW, TEXT_SECONDARY, FontPx(13, uiScale, 10, 18));
                return;
            }

            int marginX = Px(28, uiScale, 14);
            int gapX = Px(22, uiScale, 12);
            int panelY = headerBottom + Px(10, uiScale);
            int panelH = Math.Max(240, footerY - panelY - Px(14, uiScale));
            int leftW = Math.Clamp(Px(290, uiScale), 200, (int)(screenWidth * 0.3f));
            int rightX = marginX + leftW + gapX;
            int rightW = Math.Max(280, screenWidth - rightX - marginX);

            // ── Left Sidebar: Source Selector & Info ────────────────────────────
            DrawPanel(sb, pixel, marginX, panelY, leftW, panelH, BG_MEDIUM, BORDER_SUBTLE, 1);

            int sidePad = Px(16, uiScale, 10);
            DrawText(sb, font, "LEVEL SOURCE", new Vector2(marginX + sidePad, panelY + Px(16, uiScale)), TEXT_DISABLED * _fadeAlpha, FontPx(12, uiScale, 10, 16));

            int tabW = leftW - sidePad * 2;
            int tabH = Px(78, uiScale, 58);
            int tabStartY = panelY + Px(42, uiScale);
            int tabGap = Px(12, uiScale, 8);

            bool officialSelected = _selectedSource == LevelSource.Official;
            bool localSelected = _selectedSource == LevelSource.Local;

            // Keep exact indices: _clickableAreas[0] = Official, _clickableAreas[1] = Local
            var officialRect = new Rectangle(marginX + sidePad, tabStartY, tabW, tabH);
            var localRect = new Rectangle(marginX + sidePad, tabStartY + tabH + tabGap, tabW, tabH);
            _clickableAreas.Add(officialRect);
            _clickableAreas.Add(localRect);

            DrawSourceTabCard(sb, font, pixel, uiScale, officialRect, "OFFICIAL", "Verified Server Charts",
                _officialLevels.Count.ToString(), officialSelected, _hoveredButton == 0, ACCENT_PRIMARY);
            DrawSourceTabCard(sb, font, pixel, uiScale, localRect, "LOCAL", "Your Mods Folder",
                _availableLevels.Count.ToString(), localSelected, _hoveredButton == 1, ACCENT_SECONDARY);

            // Security / info card anchored cleanly inside bottom of left column
            int infoH = Px(112, uiScale, 84);
            int infoY = panelY + panelH - infoH - sidePad;
            if (infoY > localRect.Bottom + Px(12, uiScale))
            {
                Color badgeAccent = officialSelected ? ACCENT_SUCCESS : ACCENT_SECONDARY;
                DrawPanel(sb, pixel, marginX + sidePad, infoY, tabW, infoH, BG_DARK, badgeAccent * 0.45f, 1, badgeAccent);

                string badgeTitle = officialSelected ? "VERIFIED" : "CUSTOM";
                DrawPillBadge(sb, font, pixel, marginX + sidePad + Px(12, uiScale), infoY + Px(12, uiScale), Px(22, uiScale, 18), badgeTitle, badgeAccent, FontPx(11, uiScale, 10, 16), 10, true);

                string line1 = officialSelected ? "Auto-download to RAM" : "Share file with opponent";
                string line2 = officialSelected ? "Safe multiplayer charts" : "Press S on selected level";
                int infoFont = FontPx(13, uiScale, 10, 18);
                DrawText(sb, font, line1, new Vector2(marginX + sidePad + Px(12, uiScale), infoY + Px(46, uiScale)), TEXT_PRIMARY * _fadeAlpha, infoFont);
                DrawText(sb, font, line2, new Vector2(marginX + sidePad + Px(12, uiScale), infoY + Px(68, uiScale)), TEXT_SECONDARY * _fadeAlpha, FontPx(12, uiScale, 10, 16));
            }

            // ── Right Panel: Level Browser ──────────────────────────────────────
            DrawPanel(sb, pixel, rightX, panelY, rightW, panelH, BG_MEDIUM, headerAccent * 0.65f, 2);

            int rPad = Px(20, uiScale, 12);
            int listHeaderY = panelY + Px(16, uiScale);
            string listHeading = officialSelected ? "OFFICIAL SERVER CHARTS" : "LOCAL LEVELS (MODS/LEVELS)";
            DrawText(sb, font, listHeading, new Vector2(rightX + rPad, listHeaderY), headerAccent * _fadeAlpha, FontPx(14, uiScale, 11, 20));

            int levelCount = officialSelected ? _officialLevels.Count : _availableLevels.Count;
            string countBadge = levelCount > 0 ? $"{_levelSelection + 1} / {levelCount}" : "0 LEVELS";
            int countFont = FontPx(12, uiScale, 10, 17);
            Vector2 countSz = MeasureText(font, countBadge, countFont);
            int badgePadW = (int)MathF.Ceiling(countSz.X) + Px(20, uiScale);
            int badgeX = rightX + rightW - rPad - badgePadW;
            DrawPillBadge(sb, font, pixel, badgeX, listHeaderY - 2, Px(24, uiScale, 20), countBadge, headerAccent, countFont, Px(10, uiScale));

            // Clickable Refresh pill button to rescan local levels + re-fetch official server list
            string refLabel = "R  Refresh";
            Vector2 refSz = MeasureText(font, refLabel, countFont);
            int refW = (int)MathF.Ceiling(refSz.X) + Px(18, uiScale);
            int refH = Px(24, uiScale, 20);
            int refX = badgeX - refW - Px(10, uiScale);
            int refY = listHeaderY - 2;
            _refreshButtonRect = new Rectangle(refX, refY, refW, refH);
            bool refHovered = _refreshButtonRect.Contains(_lastMousePos);
            DrawPanel(sb, pixel, refX, refY, refW, refH, refHovered ? BG_LIGHT : BG_DARK, refHovered ? ACCENT_WARNING : BORDER_SUBTLE, 1);
            DrawText(sb, font, refLabel, new Vector2(refX + (refW - refSz.X) / 2f, refY + (refH - refSz.Y) / 2f),
                (refHovered ? ACCENT_WARNING : TEXT_SECONDARY) * _fadeAlpha, countFont);

            int headerDivY = listHeaderY + Px(30, uiScale);
            sb.Draw(pixel, new Rectangle(rightX + rPad, headerDivY, rightW - rPad * 2, 1), Color.White * (0.08f * _fadeAlpha));

            int listAreaTop = headerDivY + Px(12, uiScale);
            int listAreaBottom = panelY + panelH - Px(14, uiScale);
            int availListH = Math.Max(140, listAreaBottom - listAreaTop);
            int rowGap = Px(10, uiScale, 6);
            int cardH = Math.Clamp((availListH - rowGap * (MAX_VISIBLE_LEVELS - 1)) / MAX_VISIBLE_LEVELS, 48, Px(84, uiScale));
            bool hasScroll = levelCount > MAX_VISIBLE_LEVELS;
            int cardW = rightW - rPad * 2 - (hasScroll ? Px(18, uiScale, 12) : 0);
            int cardX = rightX + rPad;

            if (officialSelected)
            {
                if (_officialLevels.Count == 0)
                {
                    DrawCenteredTextInPanel(sb, font, "Loading official levels from server...", rightX, panelY + panelH / 2 - Px(12, uiScale), rightW, TEXT_SECONDARY, FontPx(17, uiScale, 13, 24));
                }
                else
                {
                    int startIdx = _levelScrollOffset;
                    int endIdx = Math.Min(startIdx + MAX_VISIBLE_LEVELS, _officialLevels.Count);

                    for (int i = startIdx; i < endIdx; i++)
                    {
                        var level = _officialLevels[i];
                        int displayIdx = i - startIdx;
                        int cardY = listAreaTop + displayIdx * (cardH + rowGap);
                        var cardRect = new Rectangle(cardX, cardY, cardW, cardH);
                        _clickableAreas.Add(cardRect);

                        bool selected = i == _levelSelection;
                        bool hovered = _hoveredButton == (2 + displayIdx);
                        bool active = selected || hovered;
                        bool isCached = LevelCache.Instance.IsCached(level.Id);
                        Color rowAccent = isCached ? ACCENT_SUCCESS : ACCENT_PRIMARY;

                        DrawPanel(sb, pixel, cardX, cardY, cardW, cardH, active ? BG_LIGHT : BG_DARK,
                            selected ? headerAccent : (hovered ? ACCENT_WARNING : BORDER_SUBTLE), selected ? 2 : 1,
                            selected ? headerAccent : (hovered ? ACCENT_WARNING * 0.7f : null));

                        int statusBadgeH = Px(24, uiScale, 18);
                        int statusFont = FontPx(11, uiScale, 10, 16);
                        string statusLabel = isCached ? "CACHED" : "DOWNLOAD";
                        int badgeW = DrawPillBadge(sb, font, pixel, cardX + Px(14, uiScale), cardY + (cardH - statusBadgeH) / 2,
                            statusBadgeH, statusLabel, rowAccent, statusFont, Px(10, uiScale), filledStrong: isCached);

                        int infoX = cardX + Px(14, uiScale) + badgeW + Px(14, uiScale);
                        int nameFont = FontPx(18, uiScale, 13, 26);
                        int diffFont = FontPx(13, uiScale, 10, 18);
                        string cta = selected ? (isCached ? "ENTER: Find Match  >" : "ENTER: Download  >") : "";
                        float ctaReserve = selected ? (MeasureText(font, cta, diffFont).X + Px(24, uiScale)) : 0f;
                        float maxNameW = Math.Max(80f, cardX + cardW - Px(16, uiScale) - ctaReserve - infoX);
                        string levelName = TruncateToWidth(font, string.IsNullOrWhiteSpace(level.Name) ? "Untitled Level" : level.Name, maxNameW, nameFont);
                        DrawText(sb, font, levelName, new Vector2(infoX, cardY + Px(10, uiScale)), (selected ? TEXT_PRIMARY : TEXT_SECONDARY) * _fadeAlpha, nameFont, shadow: selected);

                        string diffText = string.IsNullOrWhiteSpace(level.Difficulty) ? "Standard" : level.Difficulty;
                        string metaText = level.FileSize > 0
                            ? $"Difficulty: {diffText}  |  {level.FileSize / (1024.0 * 1024.0):F1} MB"
                            : $"Difficulty: {diffText}";
                        DrawText(sb, font, metaText, new Vector2(infoX, cardY + cardH - Px(26, uiScale)), TEXT_DISABLED * _fadeAlpha, diffFont);

                        if (selected)
                        {
                            Vector2 ctaSz = MeasureText(font, cta, diffFont);
                            DrawText(sb, font, cta, new Vector2(cardX + cardW - Px(16, uiScale) - ctaSz.X, cardY + (cardH - ctaSz.Y) / 2f), headerAccent * _fadeAlpha, diffFont);
                        }
                    }
                }
            }
            else
            {
                if (_availableLevels.Count == 0)
                {
                    int cy = panelY + panelH / 2 - Px(26, uiScale);
                    DrawCenteredTextInPanel(sb, font, "No local levels found", rightX, cy, rightW, TEXT_SECONDARY, FontPx(18, uiScale, 14, 26));
                    DrawCenteredTextInPanel(sb, font, "Put .fnmm / .zip / .rar levels in mods/levels/", rightX, cy + Px(30, uiScale), rightW, TEXT_DISABLED, FontPx(14, uiScale, 11, 20));
                }
                else
                {
                    int startIdx = _levelScrollOffset;
                    int endIdx = Math.Min(startIdx + MAX_VISIBLE_LEVELS, _availableLevels.Count);

                    for (int i = startIdx; i < endIdx; i++)
                    {
                        int displayIdx = i - startIdx;
                        int cardY = listAreaTop + displayIdx * (cardH + rowGap);
                        var cardRect = new Rectangle(cardX, cardY, cardW, cardH);
                        _clickableAreas.Add(cardRect);

                        bool selected = i == _levelSelection;
                        bool hovered = _hoveredButton == (2 + displayIdx);
                        bool active = selected || hovered;
                        string filename = System.IO.Path.GetFileNameWithoutExtension(_availableLevels[i]);
                        string ext = System.IO.Path.GetExtension(_availableLevels[i]).TrimStart('.').ToUpperInvariant();
                        if (string.IsNullOrEmpty(ext)) ext = "LOCAL";

                        DrawPanel(sb, pixel, cardX, cardY, cardW, cardH, active ? BG_LIGHT : BG_DARK,
                            selected ? ACCENT_SECONDARY : (hovered ? ACCENT_WARNING : BORDER_SUBTLE), selected ? 2 : 1,
                            selected ? ACCENT_SECONDARY : (hovered ? ACCENT_WARNING * 0.7f : null));

                        int statusBadgeH = Px(24, uiScale, 18);
                        int badgeW = DrawPillBadge(sb, font, pixel, cardX + Px(14, uiScale), cardY + (cardH - statusBadgeH) / 2,
                            statusBadgeH, ext, ACCENT_SECONDARY, FontPx(11, uiScale, 10, 16), Px(10, uiScale), filledStrong: selected);

                        int infoX = cardX + Px(14, uiScale) + badgeW + Px(14, uiScale);
                        int nameFont = FontPx(18, uiScale, 13, 26);
                        int ctaFont = FontPx(13, uiScale, 10, 18);
                        string cta = "S: Share   |   ENTER: Play  >";
                        float ctaReserve = selected ? (MeasureText(font, cta, ctaFont).X + Px(24, uiScale)) : 0f;
                        string displayName = TruncateToWidth(font, filename, Math.Max(80f, cardX + cardW - Px(16, uiScale) - ctaReserve - infoX), nameFont);
                        Vector2 nSz = MeasureText(font, displayName, nameFont);
                        DrawText(sb, font, displayName, new Vector2(infoX, cardY + (cardH - nSz.Y) / 2f), (selected ? TEXT_PRIMARY : TEXT_SECONDARY) * _fadeAlpha, nameFont, shadow: selected);

                        if (selected)
                        {
                            Vector2 ctaSz = MeasureText(font, cta, ctaFont);
                            DrawText(sb, font, cta, new Vector2(cardX + cardW - Px(16, uiScale) - ctaSz.X, cardY + (cardH - ctaSz.Y) / 2f), ACCENT_SECONDARY * _fadeAlpha, ctaFont);
                        }
                    }
                }
            }

            if (hasScroll)
            {
                int trackH = MAX_VISIBLE_LEVELS * cardH + (MAX_VISIBLE_LEVELS - 1) * rowGap;
                DrawScrollBar(sb, pixel, cardX + cardW + Px(10, uiScale), listAreaTop, trackH, levelCount, MAX_VISIBLE_LEVELS, _levelScrollOffset, headerAccent);
            }
        }

        private void DrawSourceTabCard(SpriteBatch sb, SpriteFont font, Texture2D pixel, float uiScale, Rectangle rect,
            string title, string subtitle, string countText, bool selected, bool hovered, Color accent)
        {
            Color bg = selected ? BG_LIGHT : (hovered ? new Color(28, 28, 46) : BG_DARK);
            Color border = selected ? accent : (hovered ? ACCENT_WARNING : BORDER_SUBTLE);
            DrawPanel(sb, pixel, rect.X, rect.Y, rect.Width, rect.Height, bg, border, selected ? 2 : 1, selected ? accent : null);

            int titleFont = FontPx(18, uiScale, 13, 26);
            int subFont = FontPx(12, uiScale, 10, 17);
            DrawText(sb, font, title, new Vector2(rect.X + Px(16, uiScale), rect.Y + Px(14, uiScale)), (selected ? accent : TEXT_PRIMARY) * _fadeAlpha, titleFont, shadow: selected);
            DrawText(sb, font, subtitle, new Vector2(rect.X + Px(16, uiScale), rect.Y + Px(42, uiScale)), TEXT_SECONDARY * _fadeAlpha, subFont);

            int countFont = FontPx(11, uiScale, 10, 16);
            int countPadX = Px(8, uiScale);
            int countBadgeW = (int)MathF.Ceiling(MeasureText(font, countText, countFont).X) + countPadX * 2;
            DrawPillBadge(sb, font, pixel, rect.Right - Px(12, uiScale) - countBadgeW, rect.Y + Px(14, uiScale), Px(22, uiScale, 18), countText, accent, countFont, countPadX, filledStrong: selected);
        }

        private void DrawSearchingModern(SpriteBatch sb, SpriteFont font, Texture2D pixel, int screenWidth, int screenHeight)
        {
            float uiScale = GetUiScale(screenWidth, screenHeight);
            int headerBottom = DrawScreenHeader(sb, font, pixel, screenWidth, uiScale,
                "MATCHMAKING", _isPrivateRoom ? $"Waiting inside Private Room • {_roomCode}" : "Searching public queue for an opponent",
                _isPrivateRoom ? ACCENT_SUCCESS : ACCENT_PRIMARY, "ESC  Cancel");
            int footerY = DrawFooterBar(sb, font, pixel, screenWidth, screenHeight, uiScale,
                "ESC or CLICK Cancel: Return to lobby");

            int panelW = Math.Clamp(Px(540, uiScale), 320, screenWidth - 48);
            int panelH = Px(290, uiScale, 220);
            int panelX = screenWidth / 2 - panelW / 2;
            int panelY = headerBottom + Math.Max(8, (footerY - headerBottom - panelH) / 2);

            Color accent = _isPrivateRoom ? ACCENT_SUCCESS : ACCENT_PRIMARY;
            DrawPanel(sb, pixel, panelX, panelY, panelW, panelH, BG_MEDIUM, accent, 2, accent);

            // Spinning circle (ChartEditor style)
            int cx = screenWidth / 2;
            int cy = panelY + Px(116, uiScale);
            int spinnerSize = Px(76, uiScale, 52);
            float spinAngleBase = _searchTimer * 3f;
            float radius = spinnerSize / 3f;
            int segSize = Px(10, uiScale, 6);
            Color spinnerColor = _isPrivateRoom ? ACCENT_SUCCESS : new Color(100, 150, 255);

            for (int i = 0; i < 8; i++)
            {
                float angle = spinAngleBase + (i * MathF.PI / 4f);
                float alpha = (1f - (i / 8f) * 0.7f) * _fadeAlpha;

                float sx = cx + MathF.Cos(angle) * radius;
                float sy = cy + MathF.Sin(angle) * radius;

                sb.Draw(pixel,
                    new Rectangle((int)MathF.Round(sx - segSize / 2f), (int)MathF.Round(sy - segSize / 2f), segSize, segSize),
                    spinnerColor * alpha);
            }

            string dots = new string('.', ((int)(_searchTimer * 3) % 4));
            string searchTitle = _isPrivateRoom ? $"Waiting for player{dots}" : $"Searching for opponent{dots}";
            DrawCenteredTextInPanel(sb, font, searchTitle, panelX, panelY + Px(28, uiScale), panelW, TEXT_PRIMARY, FontPx(22, uiScale, 15, 30), shadow: true);

            // Elapsed time + selected level badge
            string lvlName = SelectedLevelName ?? "Selected Chart";
            string srcTag = _selectedSource == LevelSource.Official ? "OFFICIAL" : "LOCAL";
            string levelBadge = $"{lvlName}  [{srcTag}]";
            DrawCenteredTextInPanel(sb, font, levelBadge, panelX + Px(20, uiScale), panelY + Px(176, uiScale), panelW - Px(40, uiScale), accent, FontPx(14, uiScale, 11, 20));

            string timerText = $"Elapsed: {_searchTimer:F1}s";
            DrawCenteredTextInPanel(sb, font, timerText, panelX, panelY + Px(204, uiScale), panelW, TEXT_SECONDARY, FontPx(15, uiScale, 12, 22));

            if (_isPrivateRoom && !string.IsNullOrEmpty(_roomCode))
            {
                DrawCenteredTextInPanel(sb, font, $"Share Room Code: {_roomCode}", panelX, panelY + Px(236, uiScale), panelW, ACCENT_WARNING, FontPx(15, uiScale, 12, 20));
            }
            else
            {
                DrawCenteredTextInPanel(sb, font, "Stay on this screen — match starts automatically", panelX, panelY + Px(236, uiScale), panelW, TEXT_DISABLED, FontPx(13, uiScale, 10, 18));
            }
        }

        private void DrawMatchFoundModern(SpriteBatch sb, SpriteFont font, Texture2D pixel, int screenWidth, int screenHeight)
        {
            if (_isCountdownActive)
            {
                DrawCountdownScreen(sb, font, pixel, screenWidth, screenHeight);
                return;
            }

            float uiScale = GetUiScale(screenWidth, screenHeight);
            int headerBottom = DrawScreenHeader(sb, font, pixel, screenWidth, uiScale,
                "MATCH FOUND!", "Toggle Ready to begin the countdown", ACCENT_SUCCESS, "ESC  Leave");
            int footerY = DrawFooterBar(sb, font, pixel, screenWidth, screenHeight, uiScale,
                "ENTER / SPACE or CLICK Your Card: Toggle Ready  |  ESC: Leave Match");

            var settings = MultiplayerSettings.Instance;
            Color yourColor = settings.GetPlayerColor();
            Color oppColor = GetColorFromHue(NetworkManager.Instance.OpponentColorHue);
            string yourName = string.IsNullOrWhiteSpace(settings.Username) ? "You" : settings.Username;
            string oppName = string.IsNullOrWhiteSpace(_opponentName) ? "Opponent" : _opponentName;

            int containerW = Math.Clamp(Px(760, uiScale), 420, screenWidth - 48);
            int availH = Math.Max(260, footerY - headerBottom - Px(20, uiScale));
            int containerH = Math.Clamp(Px(370, uiScale), 260, availH);
            int containerX = screenWidth / 2 - containerW / 2;
            int containerY = headerBottom + Math.Max(6, (availH - containerH) / 2);

            DrawPanel(sb, pixel, containerX, containerY, containerW, containerH, BG_MEDIUM, ACCENT_SUCCESS * 0.7f, 2);

            int vsGap = Px(84, uiScale, 52);
            int sidePad = Px(30, uiScale, 14);
            int cardW = (containerW - sidePad * 2 - vsGap) / 2;
            int cardH = containerH - Px(76, uiScale);
            int cardsY = containerY + Px(38, uiScale);

            // ── Your Player Card (Clickable Ready Toggle) ───────────────────────
            int youCardX = containerX + sidePad;
            var readyRect = new Rectangle(youCardX, cardsY, cardW, cardH);
            _clickableAreas.Add(readyRect);
            bool readyHovered = readyRect.Contains(_lastMousePos);
            Color yourBorder = _isReady ? ACCENT_SUCCESS : (readyHovered ? ACCENT_WARNING : yourColor);

            DrawPanel(sb, pixel, youCardX, cardsY, cardW, cardH, readyHovered ? BG_LIGHT : BG_DARK, yourBorder, (_isReady || readyHovered) ? 2 : 1);
            sb.Draw(pixel, new Rectangle(youCardX, cardsY, cardW, Px(5, uiScale, 4)), yourColor * _fadeAlpha);

            int badgeFont = FontPx(11, uiScale, 10, 16);
            int swatchSz = Px(20, uiScale, 14);
            DrawPillBadge(sb, font, pixel, youCardX + Px(16, uiScale), cardsY + Px(18, uiScale), Px(22, uiScale, 18), "YOU", yourColor, badgeFont, 10, filledStrong: true);

            // Color swatch + player name
            int nameRowY = cardsY + Px(64, uiScale);
            int nameFont = FontPx(22, uiScale, 15, 30);
            float maxPlayerNameW = Math.Max(80f, cardW - Px(36, uiScale) - swatchSz - Px(10, uiScale));
            string fitYourName = TruncateToWidth(font, yourName, maxPlayerNameW, nameFont);
            sb.Draw(pixel, new Rectangle(youCardX + Px(18, uiScale), nameRowY + 4, swatchSz, swatchSz), yourColor * _fadeAlpha);
            DrawBorder(sb, pixel, youCardX + Px(18, uiScale), nameRowY + 4, swatchSz, swatchSz, Color.White * (0.4f * _fadeAlpha), 1);
            DrawText(sb, font, fitYourName, new Vector2(youCardX + Px(18, uiScale) + swatchSz + Px(10, uiScale), nameRowY), Color.Lerp(yourColor, Color.White, 0.35f) * _fadeAlpha, nameFont, shadow: true);

            // Interactive Ready CTA pill inside bottom of Your card
            int ctaW = cardW - Px(32, uiScale);
            int ctaH = Px(52, uiScale, 40);
            int ctaX = youCardX + Px(16, uiScale);
            int ctaY = cardsY + cardH - ctaH - Px(18, uiScale);
            Color ctaBg = _isReady ? new Color(32, 130, 74) : (readyHovered ? new Color(85, 65, 28) : BG_MEDIUM);
            Color ctaBorder = _isReady ? ACCENT_SUCCESS : (readyHovered ? ACCENT_WARNING : ACCENT_ERROR);
            DrawPanel(sb, pixel, ctaX, ctaY, ctaW, ctaH, ctaBg, ctaBorder, 2);
            string ctaLabel = _isReady ? "READY!" : "CLICK TO READY";
            DrawCenteredTextInPanel(sb, font, ctaLabel, ctaX, ctaY + Px(8, uiScale), ctaW, _isReady ? Color.White : ctaBorder, FontPx(17, uiScale, 13, 24), shadow: true);
            DrawCenteredTextInPanel(sb, font, "Press ENTER / SPACE / CLICK", ctaX, ctaY + Px(30, uiScale), ctaW, TEXT_SECONDARY * 0.85f, FontPx(11, uiScale, 9, 15));

            // ── Center VS Emblem ────────────────────────────────────────────────
            int vsBoxSize = Px(56, uiScale, 38);
            int vsBoxX = containerX + containerW / 2 - vsBoxSize / 2;
            int vsBoxY = cardsY + cardH / 2 - vsBoxSize / 2;
            DrawPanel(sb, pixel, vsBoxX, vsBoxY, vsBoxSize, vsBoxSize, BG_DARK, ACCENT_WARNING * 0.8f, 2);
            int vsFont = FontPx(20, uiScale, 14, 28);
            Vector2 vsSz = MeasureText(font, "VS", vsFont);
            DrawText(sb, font, "VS", new Vector2(vsBoxX + (vsBoxSize - vsSz.X) / 2f, vsBoxY + (vsBoxSize - vsSz.Y) / 2f), ACCENT_WARNING * _fadeAlpha, vsFont, shadow: true);

            // ── Opponent Player Card ────────────────────────────────────────────
            int oppCardX = containerX + containerW - sidePad - cardW;
            Color oppBorder = _opponentReady ? ACCENT_SUCCESS : BORDER_SUBTLE;
            DrawPanel(sb, pixel, oppCardX, cardsY, cardW, cardH, BG_DARK, oppBorder, _opponentReady ? 2 : 1);
            sb.Draw(pixel, new Rectangle(oppCardX, cardsY, cardW, Px(5, uiScale, 4)), oppColor * _fadeAlpha);

            DrawPillBadge(sb, font, pixel, oppCardX + Px(16, uiScale), cardsY + Px(18, uiScale), Px(22, uiScale, 18), "OPPONENT", oppColor, badgeFont, 10, filledStrong: true);

            string fitOppName = TruncateToWidth(font, oppName, maxPlayerNameW, nameFont);
            sb.Draw(pixel, new Rectangle(oppCardX + Px(18, uiScale), nameRowY + 4, swatchSz, swatchSz), oppColor * _fadeAlpha);
            DrawBorder(sb, pixel, oppCardX + Px(18, uiScale), nameRowY + 4, swatchSz, swatchSz, Color.White * (0.4f * _fadeAlpha), 1);
            DrawText(sb, font, fitOppName, new Vector2(oppCardX + Px(18, uiScale) + swatchSz + Px(10, uiScale), nameRowY), Color.Lerp(oppColor, Color.White, 0.35f) * _fadeAlpha, nameFont, shadow: true);

            Color oppStatBg = _opponentReady ? new Color(26, 95, 58) : BG_MEDIUM;
            Color oppStatBorder = _opponentReady ? ACCENT_SUCCESS : ACCENT_WARNING * 0.7f;
            DrawPanel(sb, pixel, oppCardX + Px(16, uiScale), ctaY, ctaW, ctaH, oppStatBg, oppStatBorder, 1);
            string oppStatusLabel = _opponentReady ? "READY!" : "WAITING FOR READY...";
            int oppStatusFont = FontPx(16, uiScale, 12, 22);
            Vector2 oppStatSz = MeasureText(font, oppStatusLabel, oppStatusFont);
            DrawText(sb, font, oppStatusLabel, new Vector2(oppCardX + Px(16, uiScale) + (ctaW - oppStatSz.X) / 2f, ctaY + (ctaH - oppStatSz.Y) / 2f),
                (_opponentReady ? ACCENT_SUCCESS : ACCENT_WARNING) * _fadeAlpha, oppStatusFont, shadow: true);
        }

        private void DrawCountdownScreen(SpriteBatch sb, SpriteFont font, Texture2D pixel, int screenWidth, int screenHeight)
        {
            float uiScale = GetUiScale(screenWidth, screenHeight);
            sb.Draw(pixel, new Rectangle(0, 0, screenWidth, screenHeight), Color.Black * (0.78f * _fadeAlpha));

            string countdownText = _countdownValue > 0 ? ((int)_countdownValue).ToString() : "GO!";
            Color accent = _countdownValue > 0 ? ACCENT_WARNING : ACCENT_SUCCESS;

            // Beat pulse ring expanding cleanly behind the countdown number
            float beatFrac = _countdownValue > 0 ? (1f - (_countdownValue % 1f)) : 0.4f;
            int ringSize = Px(190, uiScale, 110) + (int)(Px(60, uiScale) * beatFrac);
            DrawBorder(sb, pixel, screenWidth / 2 - ringSize / 2, screenHeight / 2 - ringSize / 2, ringSize, ringSize, accent * ((1f - beatFrac) * 0.55f * _fadeAlpha), 2);

            int bigFont = FontPx(116, uiScale, 60, 128);
            Vector2 textSz = MeasureText(font, countdownText, bigFont);
            Vector2 textPos = new Vector2(screenWidth / 2f - textSz.X / 2f, screenHeight / 2f - textSz.Y / 2f);

            // Soft layered glow around crisp TrueType text
            for (int i = 0; i < 6; i++)
            {
                float angle = i * (MathF.PI / 3f);
                Vector2 offset = new Vector2(MathF.Cos(angle), MathF.Sin(angle)) * Px(4, uiScale, 2);
                DrawText(sb, font, countdownText, textPos + offset, accent * (0.28f * _fadeAlpha), bigFont);
            }
            DrawText(sb, font, countdownText, textPos, TEXT_PRIMARY * _fadeAlpha, bigFont, shadow: true, shadowOffset: 4f);

            var settings = MultiplayerSettings.Instance;
            string vsLine = $"{settings.Username}  VS  {(_opponentName ?? "Opponent")}";
            DrawCenteredText(sb, font, _countdownValue > 0 ? "GET READY!" : "LET'S GO!", screenHeight / 2 - Px(140, uiScale, 90), accent, FontPx(26, uiScale, 16, 38), screenWidth);
            DrawCenteredText(sb, font, vsLine, screenHeight / 2 + Px(110, uiScale, 75), TEXT_SECONDARY, FontPx(16, uiScale, 12, 24), screenWidth);
        }

        /// <summary>
        /// Reset input cooldown (called when menu is opened).
        /// </summary>
        public void ResetInputCooldown()
        {
            _inputCooldown = INPUT_COOLDOWN_TIME;
            _prevKb = Keyboard.GetState();
            _prevMouse = Mouse.GetState();
            _hoveredButton = -1;
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
            NetworkManager.Instance.LeaveMatch(notifyOpponent: false);
            TransitionToState(MenuState.Lobby);
            _isReady = false;
            _opponentReady = false;
            _isCountdownActive = false;
            _countdownValue = 0f;
            _searchTimer = 0f;
            RequestLeaderboardRefresh();
            Console.WriteLine("[MultiplayerMenu] Reset after match, returning to lobby");
        }

        /// <summary>
        /// Launch GameMultiplayer instance with current level and opponent info.
        /// </summary>
        private void LaunchGameMultiplayer()
        {
            string? levelPath = SelectedLevelPath;
            if (string.IsNullOrEmpty(levelPath) || !System.IO.File.Exists(levelPath))
            {
                Console.WriteLine($"[MultiplayerMenu] ERROR: Level path invalid: {levelPath}");
                ShowError("Failed to load level");
                TransitionToState(MenuState.Lobby);
                return;
            }

            if (_game == null)
            {
                Console.WriteLine($"[MultiplayerMenu] ERROR: Game instance not set!");
                ShowError("Game instance not initialized");
                TransitionToState(MenuState.Lobby);
                return;
            }

            string opponentName = !string.IsNullOrWhiteSpace(NetworkManager.Instance.OpponentName)
                ? NetworkManager.Instance.OpponentName
                : (_opponentName ?? "Opponent");
            int playerRole = NetworkManager.Instance.PlayerRole;

            Console.WriteLine($"[MultiplayerMenu] Starting multiplayer match in existing Game1 instance");
            Console.WriteLine($"  Level: {levelPath}");
            Console.WriteLine($"  Opponent: {opponentName}");
            Console.WriteLine($"  Player Role: {playerRole}");

            try
            {
                // Start multiplayer match in existing Game1 instance (no new window)
                _game.StartMultiplayerMatch(levelPath, opponentName, playerRole, _gameStartTimestamp, _roomId);
                
                Console.WriteLine("[MultiplayerMenu] Match started successfully");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[MultiplayerMenu] ERROR starting match: {ex.Message}");
                Console.WriteLine($"[MultiplayerMenu] Stack trace: {ex.StackTrace}");
                ShowError($"Failed to start match: {ex.Message}");
                TransitionToState(MenuState.Lobby);
            }
        }

        /// <summary>
        /// Clean up resources.
        /// </summary>
        public void Cleanup()
        {
            NetworkManager.Instance.OnConnected -= OnConnected;
            NetworkManager.Instance.OnDisconnected -= OnDisconnected;
            NetworkManager.Instance.OnConnectionError -= OnNetworkConnectionError;
            NetworkManager.Instance.OnMatchFound -= OnMatchFound;
            NetworkManager.Instance.OnOpponentProfileReceived -= OnMenuOpponentProfile;
            NetworkManager.Instance.OnOpponentDisconnected -= OnMenuOpponentDisconnected;
            NetworkManager.Instance.OnOpponentReadyChanged -= OnOpponentReadyChanged;
            NetworkManager.Instance.OnCountdownStart -= OnCountdownStart;
            NetworkManager.Instance.OnGameStart -= OnGameStart;
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
            int maxTabBarW = Math.Clamp(Px(860, uiScale), 340, screenWidth - Px(60, uiScale));
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

            int panelW = Math.Clamp(Px(920, uiScale), 360, screenWidth - Px(60, uiScale));
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
                    break;
                    
                case 1: // Gameplay
                    list.Add(new SettingInfo
                    {
                        Label = "Show Opponent Notes",
                        Value = settings.ShowOpponentNotes ? "ON" : "OFF",
                        IsToggle = true,
                        IsAdjustable = true,
                        Description = "Show or hide the opponent's note highway on the left during PvP matches."
                    });
                    list.Add(new SettingInfo
                    {
                        Label = "Show Opponent Score",
                        Value = settings.ShowOpponentScore ? "ON" : "OFF",
                        IsToggle = true,
                        IsAdjustable = true,
                        Description = "Display the opponent's live score and center score differential in the gameplay HUD."
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
                        Label = "Ghost Transparency",
                        Value = $"{settings.GhostTransparency}%",
                        IsToggle = false,
                        IsAdjustable = true,
                        Description = "Opacity percentage applied to the opponent's note highway when Show Opponent Notes is enabled."
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
            if (IsKeyPressed(kb, Keys.F5) || IsRectClicked(mouse, _refreshButtonRect))
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
            if (IsKeyPressed(kb, Keys.Left) || IsKeyPressed(kb, Keys.Q))
            {
                _leaderboardTab = (_leaderboardTab - 1 + 3) % 3;
                _leaderboardSelection = 0;
                _leaderboardScrollOffset = 0;
                _inputCooldown = INPUT_COOLDOWN_TIME;
                return;
            }
            if (IsKeyPressed(kb, Keys.Right) || IsKeyPressed(kb, Keys.E))
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
            int maxTabBarW = Math.Clamp(Px(760, uiScale), 340, screenWidth - Px(60, uiScale));
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

            int panelW = Math.Clamp(Px(960, uiScale), 360, screenWidth - Px(60, uiScale));
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
            int rowH = Math.Clamp((availH - rowGap * (MAX_LEADERBOARD_VISIBLE - 1)) / MAX_LEADERBOARD_VISIBLE, 36, Px(58, uiScale));

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
            int cardH = Math.Clamp((availH - cardGap * (MAX_LEADERBOARD_VISIBLE - 1)) / MAX_LEADERBOARD_VISIBLE, 46, Px(68, uiScale));

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
            int tileH = Math.Clamp((gridAvailH - rowGap * 3) / 4, 46, Px(84, uiScale));
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
