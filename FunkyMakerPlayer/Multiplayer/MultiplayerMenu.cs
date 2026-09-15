using System;
using System.Collections.Generic;
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
        private int _hoveredButton = -1;

        // Connection
        private string _username = "Player";
        
        // Settings menu
        private int _settingsCategory = 0; // 0-5 for different categories
        private int _settingsSelection = 0;
        private int _settingsScrollOffset = 0;
        private const int MAX_SETTINGS_VISIBLE = 6;
        private bool _isEditingText = false;
        private string _editingTextValue = "";
        
        // Leaderboard menu
        private int _leaderboardTab = 0; // 0=Top Players, 1=Recent Matches, 2=My Stats
        private int _leaderboardSelection = 0;
        private int _leaderboardScrollOffset = 0;
        private const int MAX_LEADERBOARD_VISIBLE = 8;

        // Lobby
        private List<string> _availableLevels = new();
        private int _levelSelection = 0;
        private int _levelScrollOffset = 0; // NEW: For scrolling
        private const int MAX_VISIBLE_LEVELS = 5; // Show 5 levels at a time
        private LevelSource _selectedSource = LevelSource.Local;
        private List<OfficialLevelInfo> _officialLevels = new();
        private LevelDownloader? _levelDownloader = null;
        private bool _isDownloading = false;
        private int _downloadProgress = 0;
        private string _levelsFolder = ""; // Will be set in constructor
        private Game1? _game = null; // Reference to main game instance

        // Matchmaking
        private float _searchTimer = 0f;
        private string? _opponentName = null;
        private string? _roomId = null;
        private bool _isReady = false;
        private bool _opponentReady = false;
        
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
        private const int MAX_CONNECTION_ATTEMPTS = 3;
        
        // Connection indicator
        private UI.ConnectionIndicator _connectionIndicator;
        
        // Color palette (cohesivo y profesional)
        private static readonly Color BG_DARK = new Color(12, 12, 22);
        private static readonly Color BG_MEDIUM = new Color(20, 20, 35);
        private static readonly Color BG_LIGHT = new Color(30, 30, 50);
        private static readonly Color ACCENT_PRIMARY = new Color(100, 200, 255); // Azul claro
        private static readonly Color ACCENT_SECONDARY = new Color(255, 180, 80); // Naranja suave
        private static readonly Color ACCENT_SUCCESS = new Color(80, 220, 120); // Verde
        private static readonly Color ACCENT_WARNING = new Color(255, 200, 80); // Amarillo
        private static readonly Color ACCENT_ERROR = new Color(255, 100, 100); // Rojo suave
        private static readonly Color TEXT_PRIMARY = Color.White;
        private static readonly Color TEXT_SECONDARY = new Color(180, 180, 200);
        private static readonly Color TEXT_DISABLED = new Color(100, 100, 120);
        
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
            _levelDownloader.OnDownloadComplete += (levelId, info) => {
                _isDownloading = false;
                Console.WriteLine($"[Multiplayer] Downloaded and cached: {info.Name}");
                // Level is now in memory cache, ready to use
            };
            _levelDownloader.OnDownloadError += (error) => {
                _isDownloading = false;
                Console.WriteLine($"[Multiplayer] Download error: {error}");
            };
        }
        
        /// <summary>
        /// Sets the Game1 instance reference (must be called from Game1.LoadContent)
        /// </summary>
        public void SetGame(Game1 game)
        {
            _game = game;
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
            
            // Scan levels folder for .FNMM files
            if (System.IO.Directory.Exists(_levelsFolder))
            {
                var files = System.IO.Directory.GetFiles(_levelsFolder, "*.FNMM", System.IO.SearchOption.AllDirectories);
                foreach (var file in files)
                {
                    string filename = System.IO.Path.GetFileName(file);
                    _availableLevels.Add(filename);
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
            
            // Connection retry logic
            if (_connectionRetryTimer > 0f)
            {
                _connectionRetryTimer -= dt;
                if (_connectionRetryTimer <= 0f && _state == MenuState.Connecting)
                {
                    if (_connectionAttempts < MAX_CONNECTION_ATTEMPTS)
                    {
                        _connectionAttempts++;
                        Console.WriteLine($"[Multiplayer] Retry attempt {_connectionAttempts}/{MAX_CONNECTION_ATTEMPTS}");
                        NetworkManager.Instance.Connect(_username);
                    }
                    else
                    {
                        ShowError("Connection failed after multiple attempts");
                        _state = MenuState.MainMenu;
                        _connectionAttempts = 0;
                    }
                }
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
            
            // Update mouse hover detection
            UpdateMouseHover(mouse);

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
                
            if (mouse.LeftButton == ButtonState.Released && _prevMouse.LeftButton == ButtonState.Pressed)
            {
                Point mousePos = new Point(mouse.X, mouse.Y);
                return _clickableAreas[buttonIndex].Contains(mousePos);
            }
            
            return false;
        }

        private void UpdateMainMenu(KeyboardState kb, MouseState mouse, float dt)
        {
            if (_inputCooldown > 0f) return;
            
            // Mouse hover changes selection
            if (_hoveredButton >= 0 && _hoveredButton < 4)
            {
                _selection = _hoveredButton;
            }

            // Navigate menu with keyboard
            if (kb.IsKeyDown(Keys.Down) && !_prevKb.IsKeyDown(Keys.Down))
            {
                _selection = (_selection + 1) % 4;
                _inputCooldown = INPUT_COOLDOWN_TIME;
            }
            if (kb.IsKeyDown(Keys.Up) && !_prevKb.IsKeyDown(Keys.Up))
            {
                _selection = (_selection - 1 + 4) % 4;
                _inputCooldown = INPUT_COOLDOWN_TIME;
            }

            // Select option with keyboard
            if (kb.IsKeyDown(Keys.Enter) && !_prevKb.IsKeyDown(Keys.Enter))
            {
                ExecuteMainMenuAction(_selection);
                _inputCooldown = INPUT_COOLDOWN_TIME;
            }
            
            // Select option with mouse click
            for (int i = 0; i < 4; i++)
            {
                if (IsButtonClicked(mouse, i))
                {
                    ExecuteMainMenuAction(i);
                    _inputCooldown = INPUT_COOLDOWN_TIME;
                    break;
                }
            }
            
            // Also allow ESC to go back
            if (kb.IsKeyDown(Keys.Escape) && !_prevKb.IsKeyDown(Keys.Escape))
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
                    _state = MenuState.Leaderboard;
                    _leaderboardTab = 0;
                    _leaderboardSelection = 0;
                    _leaderboardScrollOffset = 0;
                    break;
                case 2: // Settings
                    _state = MenuState.Settings;
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
            // Check connection status
            if (NetworkManager.Instance.IsConnected)
            {
                TransitionToState(MenuState.ModeSelect);
                _modeSelection = 0;
                _connectionAttempts = 0;
            }
            else if (!NetworkManager.Instance.IsConnecting && _connectionRetryTimer <= 0f)
            {
                // Connection failed, prepare retry
                if (_connectionAttempts < MAX_CONNECTION_ATTEMPTS)
                {
                    Console.WriteLine($"[Multiplayer] Connection attempt {_connectionAttempts} failed, retrying in 3 seconds...");
                    _connectionRetryTimer = 3f;
                }
                else
                {
                    // Max attempts reached
                    ShowError("Connection failed. Check your internet connection.");
                    TransitionToState(MenuState.MainMenu);
                    _connectionAttempts = 0;
                }
            }

            // Allow cancel with ESC
            if (kb.IsKeyDown(Keys.Escape) && !_prevKb.IsKeyDown(Keys.Escape))
            {
                NetworkManager.Instance.Disconnect();
                TransitionToState(MenuState.MainMenu);
                _connectionAttempts = 0;
                _connectionRetryTimer = 0f;
            }
        }
        
        private void UpdateModeSelect(KeyboardState kb, MouseState mouse, float dt)
        {
            if (_inputCooldown > 0f) return;
            
            // Mouse hover changes selection
            if (_hoveredButton >= 0 && _hoveredButton < 2)
            {
                _modeSelection = _hoveredButton;
            }

            // Navigate modes with LEFT/RIGHT
            if (kb.IsKeyDown(Keys.Right) && !_prevKb.IsKeyDown(Keys.Right))
            {
                _modeSelection = (_modeSelection + 1) % 2;
                _inputCooldown = INPUT_COOLDOWN_TIME;
            }
            if (kb.IsKeyDown(Keys.Left) && !_prevKb.IsKeyDown(Keys.Left))
            {
                _modeSelection = (_modeSelection - 1 + 2) % 2;
                _inputCooldown = INPUT_COOLDOWN_TIME;
            }

            // Select mode with keyboard
            if (kb.IsKeyDown(Keys.Enter) && !_prevKb.IsKeyDown(Keys.Enter))
            {
                ExecuteModeSelectAction(_modeSelection);
                _inputCooldown = INPUT_COOLDOWN_TIME;
            }
            
            // Select mode with mouse click
            for (int i = 0; i < 2; i++)
            {
                if (IsButtonClicked(mouse, i))
                {
                    ExecuteModeSelectAction(i);
                    _inputCooldown = INPUT_COOLDOWN_TIME;
                    break;
                }
            }

            // Back
            if (kb.IsKeyDown(Keys.Escape) && !_prevKb.IsKeyDown(Keys.Escape))
            {
                NetworkManager.Instance.Disconnect();
                _state = MenuState.MainMenu;
            }
        }
        
        private void ExecuteModeSelectAction(int mode)
        {
            if (mode == 0)
            {
                // Quick Play (Matchmaking)
                _isPrivateRoom = false;
                _state = MenuState.Lobby;
            }
            else
            {
                // Private Room
                _isPrivateRoom = true;
                _state = MenuState.CreateRoom;
                _roomCode = GenerateRoomCode();
            }
        }
        
        private void UpdateCreateRoom(KeyboardState kb, MouseState mouse, float dt)
        {
            if (_inputCooldown > 0f) return;

            // Navigate options with LEFT/RIGHT
            if (kb.IsKeyDown(Keys.Right) && !_prevKb.IsKeyDown(Keys.Right))
            {
                _selection = (_selection + 1) % 2; // 0 = Create, 1 = Join Existing
                _inputCooldown = INPUT_COOLDOWN_TIME;
            }
            if (kb.IsKeyDown(Keys.Left) && !_prevKb.IsKeyDown(Keys.Left))
            {
                _selection = (_selection - 1 + 2) % 2;
                _inputCooldown = INPUT_COOLDOWN_TIME;
            }

            if (kb.IsKeyDown(Keys.Enter) && !_prevKb.IsKeyDown(Keys.Enter))
            {
                if (_selection == 0)
                {
                    // Create new room with current code
                    Console.WriteLine($"[Multiplayer] Creating room: {_roomCode}");
                    _state = MenuState.Lobby;
                }
                else
                {
                    // Join existing room
                    _roomCode = "";
                    _state = MenuState.JoinRoom;
                }
                _inputCooldown = INPUT_COOLDOWN_TIME;
            }

            // Back
            if (kb.IsKeyDown(Keys.Escape) && !_prevKb.IsKeyDown(Keys.Escape))
            {
                _state = MenuState.ModeSelect;
            }
        }
        
        private void UpdateJoinRoom(KeyboardState kb, MouseState mouse, float dt)
        {
            if (_inputCooldown > 0f) return;

            // Type room code (A-Z, 0-9, max 6 characters)
            var pressedKeys = kb.GetPressedKeys();
            foreach (var key in pressedKeys)
            {
                if (_prevKb.IsKeyUp(key) && _roomCode.Length < 6)
                {
                    // Allow letters and numbers
                    if (key >= Keys.A && key <= Keys.Z)
                    {
                        _roomCode += key.ToString();
                        _inputCooldown = 0.1f;
                    }
                    else if (key >= Keys.D0 && key <= Keys.D9)
                    {
                        _roomCode += key.ToString().Replace("D", "");
                        _inputCooldown = 0.1f;
                    }
                }
            }

            // Backspace
            if (kb.IsKeyDown(Keys.Back) && !_prevKb.IsKeyDown(Keys.Back) && _roomCode.Length > 0)
            {
                _roomCode = _roomCode.Substring(0, _roomCode.Length - 1);
                _inputCooldown = INPUT_COOLDOWN_TIME;
            }

            // Enter to join
            if (kb.IsKeyDown(Keys.Enter) && !_prevKb.IsKeyDown(Keys.Enter) && _roomCode.Length == 6)
            {
                Console.WriteLine($"[Multiplayer] Joining room: {_roomCode}");
                _state = MenuState.Lobby;
                _inputCooldown = INPUT_COOLDOWN_TIME;
            }

            // Cancel
            if (kb.IsKeyDown(Keys.Escape) && !_prevKb.IsKeyDown(Keys.Escape))
            {
                _state = MenuState.CreateRoom;
                _selection = 0;
            }
        }

        private void UpdateLobby(KeyboardState kb, MouseState mouse, float dt)
        {
            if (_inputCooldown > 0f) return;
            if (_isDownloading) return; // Disable input during download
            
            // Mouse interaction for tabs (first 2 clickable areas)
            if (_hoveredButton == 0)
            {
                _selectedSource = LevelSource.Official;
                _levelSelection = 0;
                _levelScrollOffset = 0;
            }
            else if (_hoveredButton == 1)
            {
                _selectedSource = LevelSource.Local;
                _levelSelection = 0;
                _levelScrollOffset = 0;
            }
            
            // Mouse interaction for level items (clickable areas starting from index 2)
            if (_hoveredButton >= 2)
            {
                int levelIndex = _hoveredButton - 2 + _levelScrollOffset;
                int totalLevels = _selectedSource == LevelSource.Official ? _officialLevels.Count : _availableLevels.Count;
                if (levelIndex < totalLevels)
                {
                    _levelSelection = levelIndex;
                }
            }

            // Navigate levels with scrolling
            int currentLevelCount = _selectedSource == LevelSource.Official ? _officialLevels.Count : _availableLevels.Count;
            if (currentLevelCount > 0)
            {
                if (kb.IsKeyDown(Keys.Down) && !_prevKb.IsKeyDown(Keys.Down))
                {
                    _levelSelection = (_levelSelection + 1) % currentLevelCount;
                    
                    // Adjust scroll offset
                    if (_levelSelection >= _levelScrollOffset + MAX_VISIBLE_LEVELS)
                    {
                        _levelScrollOffset = _levelSelection - MAX_VISIBLE_LEVELS + 1;
                    }
                    else if (_levelSelection < _levelScrollOffset)
                    {
                        _levelScrollOffset = _levelSelection;
                    }
                    
                    _inputCooldown = INPUT_COOLDOWN_TIME;
                }
                if (kb.IsKeyDown(Keys.Up) && !_prevKb.IsKeyDown(Keys.Up))
                {
                    _levelSelection = (_levelSelection - 1 + currentLevelCount) % currentLevelCount;
                    
                    // Adjust scroll offset
                    if (_levelSelection < _levelScrollOffset)
                    {
                        _levelScrollOffset = _levelSelection;
                    }
                    else if (_levelSelection >= _levelScrollOffset + MAX_VISIBLE_LEVELS)
                    {
                        _levelScrollOffset = _levelSelection - MAX_VISIBLE_LEVELS + 1;
                    }
                    
                    _inputCooldown = INPUT_COOLDOWN_TIME;
                }
            }

            // Switch between Official and Local with Left/Right or Tab clicks
            if (kb.IsKeyDown(Keys.Left) && !_prevKb.IsKeyDown(Keys.Left))
            {
                _selectedSource = LevelSource.Official;
                _levelSelection = 0;
                _levelScrollOffset = 0;
                _inputCooldown = INPUT_COOLDOWN_TIME;
            }
            if (kb.IsKeyDown(Keys.Right) && !_prevKb.IsKeyDown(Keys.Right))
            {
                _selectedSource = LevelSource.Local;
                _levelSelection = 0;
                _levelScrollOffset = 0;
                _inputCooldown = INPUT_COOLDOWN_TIME;
            }
            
            // Click on tabs
            if (IsButtonClicked(mouse, 0))
            {
                _selectedSource = LevelSource.Official;
                _levelSelection = 0;
                _levelScrollOffset = 0;
                _inputCooldown = INPUT_COOLDOWN_TIME;
            }
            if (IsButtonClicked(mouse, 1))
            {
                _selectedSource = LevelSource.Local;
                _levelSelection = 0;
                _levelScrollOffset = 0;
                _inputCooldown = INPUT_COOLDOWN_TIME;
            }

            // ENTER: Search match OR Download (Official) OR Share (Local)
            if (kb.IsKeyDown(Keys.Enter) && !_prevKb.IsKeyDown(Keys.Enter))
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
                    int totalLevels = _selectedSource == LevelSource.Official ? _officialLevels.Count : _availableLevels.Count;
                    if (levelIndex < totalLevels)
                    {
                        _levelSelection = levelIndex;
                        ExecuteLobbyAction();
                        _inputCooldown = INPUT_COOLDOWN_TIME;
                        break;
                    }
                }
            }
            
            // S key: Share level (Local mode only)
            if (kb.IsKeyDown(Keys.S) && !_prevKb.IsKeyDown(Keys.S) && _selectedSource == LevelSource.Local)
            {
                if (_availableLevels.Count > 0 && _levelSelection < _availableLevels.Count)
                {
                    ShareLevel(_availableLevels[_levelSelection]);
                }
                _inputCooldown = INPUT_COOLDOWN_TIME;
            }

            // Disconnect
            if (kb.IsKeyDown(Keys.Escape) && !_prevKb.IsKeyDown(Keys.Escape))
            {
                NetworkManager.Instance.Disconnect();
                _state = MenuState.MainMenu;
            }
        }
        
        private void ExecuteLobbyAction()
        {
            if (_selectedSource == LevelSource.Official)
            {
                // Check if level is cached in memory
                if (_officialLevels.Count > 0 && _levelSelection < _officialLevels.Count)
                {
                    var level = _officialLevels[_levelSelection];
                    
                    if (LevelCache.Instance.IsCached(level.Id))
                    {
                        // Already cached, start matchmaking
                        StartMatchmaking();
                    }
                    else
                    {
                        // Download level to memory cache
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
            // Cancel search
            if (kb.IsKeyDown(Keys.Escape) && !_prevKb.IsKeyDown(Keys.Escape))
            {
                NetworkManager.Instance.CancelMatchmaking();
                _state = MenuState.Lobby;
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
                
                // Countdown duration: 3 seconds
                // 0-1000ms: show "3"
                // 1000-2000ms: show "2"
                // 2000-3000ms: show "1"
                // 3000+ms: show "GO!" and transition to InGame
                
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
                    // Countdown finished, launch GameMultiplayer!
                    _countdownValue = 0;
                    _isCountdownActive = false;
                    
                    Console.WriteLine("[MultiplayerMenu] Countdown complete! Launching GameMultiplayer...");
                    LaunchGameMultiplayer();
                    return;
                }
            }

            // Toggle ready with ENTER (only when countdown is not active)
            if (!_isCountdownActive && kb.IsKeyDown(Keys.Enter) && !_prevKb.IsKeyDown(Keys.Enter))
            {
                _isReady = !_isReady;
                
                // Send ready state to server
                NetworkManager.Instance.SendReady(_isReady);
                Console.WriteLine($"[Multiplayer] Ready: {_isReady}");
                _inputCooldown = INPUT_COOLDOWN_TIME;
            }

            // Cancel match with ESC (only when countdown is not active)
            if (!_isCountdownActive && kb.IsKeyDown(Keys.Escape) && !_prevKb.IsKeyDown(Keys.Escape))
            {
                NetworkManager.Instance.CancelMatchmaking();
                _state = MenuState.Lobby;
                _opponentName = null;
                _roomId = null;
                _isReady = false;
                _opponentReady = false;
                _isCountdownActive = false;
            }
        }
        
        private void UpdateSettings(KeyboardState kb, MouseState mouse, float dt)
        {
            if (_inputCooldown > 0f) return;
            
            var settings = MultiplayerSettings.Instance;
            
            // Handle text editing mode
            if (_isEditingText)
            {
                HandleTextEditing(kb);
                return;
            }
            
            // Mouse click on SAVE button (index 999 special case)
            if (IsButtonClicked(mouse, _clickableAreas.Count - 1)) // Last button is SAVE
            {
                SaveSettings();
                _inputCooldown = INPUT_COOLDOWN_TIME;
                return;
            }
            
            // Mouse hover and click on settings items
            int itemCount = GetSettingsCategoryItemCount(_settingsCategory);
            for (int i = 0; i < Math.Min(itemCount, MAX_SETTINGS_VISIBLE); i++)
            {
                if (i < _clickableAreas.Count - 1) // Exclude SAVE button
                {
                    int actualIndex = _settingsScrollOffset + i;
                    
                    // Check if clicked on left arrow (decrease value)
                    if (mouse.LeftButton == ButtonState.Pressed && _prevMouse.LeftButton == ButtonState.Released)
                    {
                        Point mousePos = new Point(mouse.X, mouse.Y);
                        if (i < _leftArrowAreas.Count && _leftArrowAreas[i].Contains(mousePos))
                        {
                            _settingsSelection = actualIndex;
                            ModifySettingValue(_settingsCategory, actualIndex, -1);
                            _inputCooldown = INPUT_COOLDOWN_TIME;
                            continue;
                        }
                        
                        // Check if clicked on right arrow (increase value)
                        if (i < _rightArrowAreas.Count && _rightArrowAreas[i].Contains(mousePos))
                        {
                            _settingsSelection = actualIndex;
                            ModifySettingValue(_settingsCategory, actualIndex, 1);
                            _inputCooldown = INPUT_COOLDOWN_TIME;
                            continue;
                        }
                    }
                    
                    // Regular click on setting item
                    if (IsButtonClicked(mouse, i))
                    {
                        // Double-click to edit username (only for username setting)
                        if (_settingsCategory == 0 && actualIndex == 0 && !_isEditingText)
                        {
                            // Start editing
                            _isEditingText = true;
                            _editingTextValue = MultiplayerSettings.Instance.Username;
                            _settingsSelection = actualIndex;
                        }
                        else
                        {
                            _settingsSelection = actualIndex;
                        }
                        _inputCooldown = INPUT_COOLDOWN_TIME;
                    }
                    // Set selection on hover
                    else if (_hoveredButton == i)
                    {
                        if (actualIndex != _settingsSelection)
                        {
                            _settingsSelection = actualIndex;
                        }
                    }
                }
            }
            
            // Navigate categories with LEFT/RIGHT
            if (kb.IsKeyDown(Keys.Left) && !_prevKb.IsKeyDown(Keys.Left))
            {
                _settingsCategory = (_settingsCategory - 1 + 6) % 6; // 6 categories (0-5)
                _settingsSelection = 0;
                _settingsScrollOffset = 0;
                _inputCooldown = INPUT_COOLDOWN_TIME;
            }
            if (kb.IsKeyDown(Keys.Right) && !_prevKb.IsKeyDown(Keys.Right))
            {
                _settingsCategory = (_settingsCategory + 1) % 6; // 6 categories (0-5)
                _settingsSelection = 0;
                _settingsScrollOffset = 0;
                _inputCooldown = INPUT_COOLDOWN_TIME;
            }
            
            // Navigate settings with UP/DOWN
            if (itemCount > 0)
            {
                if (kb.IsKeyDown(Keys.Down) && !_prevKb.IsKeyDown(Keys.Down))
                {
                    _settingsSelection = (_settingsSelection + 1) % itemCount;
                    
                    // Adjust scroll
                    if (_settingsSelection >= _settingsScrollOffset + MAX_SETTINGS_VISIBLE)
                    {
                        _settingsScrollOffset = _settingsSelection - MAX_SETTINGS_VISIBLE + 1;
                    }
                    else if (_settingsSelection < _settingsScrollOffset)
                    {
                        _settingsScrollOffset = _settingsSelection;
                    }
                    
                    _inputCooldown = INPUT_COOLDOWN_TIME;
                }
                if (kb.IsKeyDown(Keys.Up) && !_prevKb.IsKeyDown(Keys.Up))
                {
                    _settingsSelection = (_settingsSelection - 1 + itemCount) % itemCount;
                    
                    // Adjust scroll
                    if (_settingsSelection < _settingsScrollOffset)
                    {
                        _settingsScrollOffset = _settingsSelection;
                    }
                    else if (_settingsSelection >= _settingsScrollOffset + MAX_SETTINGS_VISIBLE)
                    {
                        _settingsScrollOffset = _settingsSelection - MAX_SETTINGS_VISIBLE + 1;
                    }
                    
                    _inputCooldown = INPUT_COOLDOWN_TIME;
                }
                
                // Modify setting with A/D or LEFT/RIGHT (when not changing category)
                if (kb.IsKeyDown(Keys.A) && !_prevKb.IsKeyDown(Keys.A))
                {
                    ModifySettingValue(_settingsCategory, _settingsSelection, -1);
                    _inputCooldown = INPUT_COOLDOWN_TIME;
                }
                if (kb.IsKeyDown(Keys.D) && !_prevKb.IsKeyDown(Keys.D))
                {
                    ModifySettingValue(_settingsCategory, _settingsSelection, 1);
                    _inputCooldown = INPUT_COOLDOWN_TIME;
                }
                
                // ENTER to toggle boolean values
                if (kb.IsKeyDown(Keys.Enter) && !_prevKb.IsKeyDown(Keys.Enter))
                {
                    ToggleSettingValue(_settingsCategory, _settingsSelection);
                    _inputCooldown = INPUT_COOLDOWN_TIME;
                }
            }
            
            // R to reset to defaults
            if (kb.IsKeyDown(Keys.R) && !_prevKb.IsKeyDown(Keys.R))
            {
                settings.ResetToDefaults();
                _inputCooldown = INPUT_COOLDOWN_TIME;
            }
            
            // ESC to go back (also saves)
            if (kb.IsKeyDown(Keys.Escape) && !_prevKb.IsKeyDown(Keys.Escape))
            {
                SaveSettings();
                _state = MenuState.MainMenu;
            }
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
        
        private void HandleTextEditing(KeyboardState kb)
        {
            var settings = MultiplayerSettings.Instance;
            
            // Get all pressed keys
            var pressedKeys = kb.GetPressedKeys();
            
            foreach (var key in pressedKeys)
            {
                if (_prevKb.IsKeyUp(key))
                {
                    // Handle backspace
                    if (key == Keys.Back && _editingTextValue.Length > 0)
                    {
                        _editingTextValue = _editingTextValue.Substring(0, _editingTextValue.Length - 1);
                    }
                    // Handle space
                    else if (key == Keys.Space && _editingTextValue.Length < 20)
                    {
                        _editingTextValue += " ";
                    }
                    // Handle letters (A-Z)
                    else if (key >= Keys.A && key <= Keys.Z && _editingTextValue.Length < 20)
                    {
                        bool shift = kb.IsKeyDown(Keys.LeftShift) || kb.IsKeyDown(Keys.RightShift);
                        char c = shift ? key.ToString()[0] : char.ToLower(key.ToString()[0]);
                        _editingTextValue += c;
                    }
                    // Handle numbers (0-9)
                    else if (key >= Keys.D0 && key <= Keys.D9 && _editingTextValue.Length < 20)
                    {
                        _editingTextValue += key.ToString().Replace("D", "");
                    }
                    // Handle numpad numbers
                    else if (key >= Keys.NumPad0 && key <= Keys.NumPad9 && _editingTextValue.Length < 20)
                    {
                        _editingTextValue += ((int)key - (int)Keys.NumPad0).ToString();
                    }
                }
            }
            
            // ENTER to confirm
            if (kb.IsKeyDown(Keys.Enter) && !_prevKb.IsKeyDown(Keys.Enter))
            {
                if (!string.IsNullOrWhiteSpace(_editingTextValue))
                {
                    settings.Username = _editingTextValue.Trim();
                    settings.Save();
                    Console.WriteLine($"[Settings] Username changed to: {settings.Username}");
                }
                _isEditingText = false;
                _editingTextValue = "";
            }
            
            // ESC to cancel
            if (kb.IsKeyDown(Keys.Escape) && !_prevKb.IsKeyDown(Keys.Escape))
            {
                _isEditingText = false;
                _editingTextValue = "";
                Console.WriteLine("[Settings] Username editing cancelled");
            }
        }
        
        private int GetSettingsCategoryItemCount(int category)
        {
            return category switch
            {
                0 => 3,  // User: Username, Avatar, Color
                1 => 4,  // Gameplay: ShowOpponentNotes, ShowOpponentScore, ShowConnectionQuality, GhostTransparency
                2 => 3,  // Notifications: MatchFoundSound, ChatNotifications, ReadyAlerts
                3 => 3,  // Privacy: AllowMatchInvites, ShowOnlineStatus, AnonymousMode
                4 => 3,  // Performance: UpdateRate, EnableInterpolation, BandwidthLimit
                _ => 3,  // Visual: HudOpacity, ConnectionIndicatorPosition, InterfaceMode
            };
        }
        
        private void ModifySettingValue(int category, int selection, int direction)
        {
            var settings = MultiplayerSettings.Instance;
            
            switch (category)
            {
                case 0: // User
                    switch (selection)
                    {
                        case 0: // Username - can't modify with A/D, use text input (TODO)
                            break;
                        case 1: // Avatar
                            settings.AvatarIndex = (settings.AvatarIndex + direction + 10) % 10;
                            break;
                        case 2: // Color
                            settings.PlayerColorHue = (settings.PlayerColorHue + direction * 10 + 360) % 360;
                            break;
                    }
                    break;
                    
                case 1: // Gameplay
                    switch (selection)
                    {
                        case 3: // GhostTransparency
                            settings.GhostTransparency = Math.Clamp(settings.GhostTransparency + direction * 10, 0, 100);
                            break;
                    }
                    break;
                    
                case 4: // Performance
                    switch (selection)
                    {
                        case 0: // UpdateRate
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
                            break;
                        case 2: // BandwidthLimit
                            int[] limits = { 0, 256, 512, 1024, 2048 }; // 0 = unlimited
                            int currentIndex = Array.IndexOf(limits, settings.BandwidthLimitKbps);
                            if (currentIndex < 0) currentIndex = 0;
                            currentIndex = (currentIndex + direction + limits.Length) % limits.Length;
                            settings.BandwidthLimitKbps = limits[currentIndex];
                            break;
                    }
                    break;
                    
                case 5: // Visual
                    switch (selection)
                    {
                        case 0: // HudOpacity
                            settings.HudOpacity = Math.Clamp(settings.HudOpacity + direction * 10, 0, 100);
                            break;
                        case 1: // ConnectionIndicatorPosition
                            int posCount = Enum.GetValues(typeof(MultiplayerSettings.IndicatorPosition)).Length;
                            int currentPos = (int)settings.ConnectionIndicatorPosition;
                            settings.ConnectionIndicatorPosition = (MultiplayerSettings.IndicatorPosition)((currentPos + direction + posCount) % posCount);
                            break;
                        case 2: // InterfaceMode
                            settings.InterfaceMode = settings.InterfaceMode == MultiplayerSettings.UIMode.Compact 
                                ? MultiplayerSettings.UIMode.Full 
                                : MultiplayerSettings.UIMode.Compact;
                            break;
                    }
                    break;
            }
            
            settings.Save();
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
                    
                case 2: // Notifications
                    switch (selection)
                    {
                        case 0: settings.MatchFoundSound = !settings.MatchFoundSound; break;
                        case 1: settings.ChatNotifications = !settings.ChatNotifications; break;
                        case 2: settings.ReadyAlerts = !settings.ReadyAlerts; break;
                    }
                    break;
                    
                case 3: // Privacy
                    switch (selection)
                    {
                        case 0: settings.AllowMatchInvites = !settings.AllowMatchInvites; break;
                        case 1: settings.ShowOnlineStatus = !settings.ShowOnlineStatus; break;
                        case 2: settings.AnonymousMode = !settings.AnonymousMode; break;
                    }
                    break;
                    
                case 4: // Performance
                    if (selection == 1) // EnableInterpolation
                    {
                        settings.EnableInterpolation = !settings.EnableInterpolation;
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
            _state = MenuState.Connecting;
            _stateTransitionTimer = TRANSITION_DURATION;
            _connectionAttempts = 1;
            _connectionRetryTimer = 0f;
            NetworkManager.Instance.Connect(_username);

            // Subscribe to connection events
            NetworkManager.Instance.OnConnected += OnConnected;
            NetworkManager.Instance.OnDisconnected += OnDisconnected;
            NetworkManager.Instance.OnMatchFound += OnMatchFound;
            NetworkManager.Instance.OnOpponentReadyChanged += OnOpponentReadyChanged;
            NetworkManager.Instance.OnCountdownStart += OnCountdownStart;
            NetworkManager.Instance.OnLevelListReceived += OnLevelListReceived;
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
        /// Trigger a smooth state transition.
        /// </summary>
        private void TransitionToState(MenuState newState)
        {
            _state = newState;
            _stateTransitionTimer = TRANSITION_DURATION;
            _fadeAlpha = 0f;
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
                
                // Check if cached in memory
                if (!LevelCache.Instance.IsCached(level.Id))
                {
                    Console.WriteLine("[Multiplayer] Level not cached, download first");
                    return;
                }
                
                // Create temporary file for gameplay
                string? tempPath = LevelCache.Instance.WriteTempFile(level.Id);
                if (tempPath == null)
                {
                    Console.WriteLine("[Multiplayer] Failed to create temp file");
                    return;
                }
                
                // Calculate hash from temp file
                string actualHash = LevelHasher.CalculateHash(tempPath);
                
                _state = MenuState.Searching;
                _searchTimer = 0f;
                NetworkManager.Instance.FindMatch(actualHash, level.Name, LevelSource.Official, tempPath);
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
                NetworkManager.Instance.FindMatch(levelHash, levelFilename, LevelSource.Local, levelPath);
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
            
            Console.WriteLine($"[Multiplayer] Downloading: {level.Name}");
            
            // Get server base URL (remove /multiplayer)
            string serverUrl = NetworkManager.Instance.ServerUrl.Replace("/multiplayer", "");
            
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

        /// <summary>
        /// Check server information and version.
        /// </summary>
        private async void CheckServerInfo()
        {
            try
            {
                using var client = new System.Net.Http.HttpClient();
                client.Timeout = TimeSpan.FromSeconds(5);
                
                string serverUrl = "https://funkymakerserver.onrender.com";
                string response = await client.GetStringAsync($"{serverUrl}/api/server/info");
                
                // Parse JSON
                var json = System.Text.Json.JsonDocument.Parse(response);
                
                if (json.RootElement.TryGetProperty("Version", out var versionElement))
                {
                    _serverVersion = versionElement.GetString() ?? "Unknown";
                    Console.WriteLine($"[Multiplayer] Server version: {_serverVersion}");
                }
                
                if (json.RootElement.TryGetProperty("Levels", out var levelsElement))
                {
                    if (levelsElement.TryGetProperty("Count", out var countElement))
                    {
                        _serverLevelCount = countElement.GetInt32();
                        Console.WriteLine($"[Multiplayer] Server has {_serverLevelCount} official levels");
                    }
                    
                    if (levelsElement.TryGetProperty("Names", out var namesElement))
                    {
                        var names = new System.Collections.Generic.List<string>();
                        foreach (var nameElement in namesElement.EnumerateArray())
                        {
                            var name = nameElement.GetString();
                            if (name != null) names.Add(name);
                        }
                        Console.WriteLine($"[Multiplayer] Available: {string.Join(", ", names)}");
                    }
                }
                
                if (json.RootElement.TryGetProperty("Architecture", out var archElement))
                {
                    if (archElement.TryGetProperty("Type", out var typeElement))
                    {
                        string archType = typeElement.GetString() ?? "Unknown";
                        Console.WriteLine($"[Multiplayer] Architecture: {archType}");
                    }
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[Multiplayer] Failed to get server info: {ex.Message}");
                _serverVersion = "Unknown";
            }
        }

        private void OnConnected()
        {
            Console.WriteLine("[Multiplayer] Connected to server");
            
            // Check server information
            CheckServerInfo();
            
            _state = MenuState.ModeSelect; // Choose mode first
            _modeSelection = 0;
            
            // Request official levels list
            NetworkManager.Instance.RequestOfficialLevels();
        }

        private void OnDisconnected(string reason)
        {
            Console.WriteLine($"[Multiplayer] Disconnected: {reason}");
            _state = MenuState.MainMenu;
        }

        private void OnMatchFound(string opponentName, string roomId, int playerRole)
        {
            Console.WriteLine($"[Multiplayer] Match found! Opponent: {opponentName}, Role: Player {playerRole}");
            _opponentName = opponentName;
            _roomId = roomId;
            _state = MenuState.MatchFound;
            _isReady = false;
            _opponentReady = false;
            _isCountdownActive = false;
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

        private void OnLevelListReceived(LevelListPacket packet)
        {
            Console.WriteLine($"[Multiplayer] Received {packet.Levels.Count} official levels");
            _officialLevels = packet.Levels;
        }

        // ══════════════════════════════════════════════════════════════════════
        // Rendering
        // ══════════════════════════════════════════════════════════════════════

        /// <summary>
        /// Draw the multiplayer menu.
        /// </summary>
        public void Draw(SpriteBatch sb, SpriteFont font, Texture2D pixel, int screenWidth, int screenHeight)
        {
            // Full screen gradient background
            DrawGradientBackground(sb, pixel, screenWidth, screenHeight);

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
            
            // Draw connection indicator (shown when connected)
            if (NetworkManager.Instance.IsConnected && 
                _state != MenuState.MainMenu && 
                _state != MenuState.Connecting)
            {
                _connectionIndicator?.Draw(sb, font, pixel, screenWidth, screenHeight);
            }
            
            // Draw error message overlay if present
            if (_errorMessageTimer > 0f && !string.IsNullOrEmpty(_errorMessage))
            {
                DrawErrorMessage(sb, font, pixel, screenWidth, screenHeight);
            }
            
#if ANDROID
            // Draw touch controls
            _touchControls?.Draw(sb, font);
#endif
        }
        
        /// <summary>
        /// Draw error message at the top of the screen.
        /// </summary>
        private void DrawErrorMessage(SpriteBatch sb, SpriteFont font, Texture2D pixel, int screenWidth, int screenHeight)
        {
            float alpha = Math.Min(1f, _errorMessageTimer / 0.5f); // Fade in during first 0.5s
            if (_errorMessageTimer < 0.5f) alpha = _errorMessageTimer / 0.5f; // Fade out in last 0.5s
            
            int errorW = 600;
            int errorH = 80;
            int errorX = screenWidth / 2 - errorW / 2;
            int errorY = 30;
            
            // Semi-transparent background
            sb.Draw(pixel, new Rectangle(errorX, errorY, errorW, errorH), ACCENT_ERROR * 0.2f * alpha);
            
            // Border
            DrawPanel(sb, pixel, errorX, errorY, errorW, errorH, ACCENT_ERROR * 0.15f * alpha, ACCENT_ERROR * alpha, 2);
            
            // Icon
            string icon = "!";
            Vector2 iconSize = font.MeasureString(icon);
            sb.DrawString(font, icon, new Vector2(errorX + 15, errorY + errorH / 2 - iconSize.Y / 2), ACCENT_ERROR * alpha);
            
            // Message text
            Vector2 textSize = font.MeasureString(_errorMessage);
            Vector2 textPos = new Vector2(errorX + errorW / 2 - textSize.X / 2, errorY + errorH / 2 - textSize.Y / 2);
            sb.DrawString(font, _errorMessage, textPos + new Vector2(1, 1), Color.Black * 0.5f * alpha);
            sb.DrawString(font, _errorMessage, textPos, TEXT_PRIMARY * alpha);
        }
        
        // ══════════════════════════════════════════════════════════════════════
        // Modern Draw Helpers
        // ══════════════════════════════════════════════════════════════════════
        
        /// <summary>
        /// Draw text centered on screen with shadow.
        /// </summary>
        private void DrawCenteredText(SpriteBatch sb, SpriteFont font, string text, int y, Color color)
        {
            if (string.IsNullOrEmpty(text)) return;
            
            Vector2 size = font.MeasureString(text);
            int screenWidth = sb.GraphicsDevice.Viewport.Width;
            Vector2 pos = new Vector2(screenWidth / 2 - size.X / 2, y);
            
            // Subtle shadow for readability
            sb.DrawString(font, text, pos + new Vector2(2, 2), Color.Black * 0.4f);
            sb.DrawString(font, text, pos, color * _fadeAlpha);
        }
        
        private void DrawGradientBackground(SpriteBatch sb, Texture2D pixel, int sw, int sh)
        {
            // Smooth dark gradient from top to bottom
            int steps = 30; // Más pasos = más suave
            for (int i = 0; i < steps; i++)
            {
                float ratio = i / (float)steps;
                Color color = Color.Lerp(new Color(8, 8, 18), new Color(12, 12, 22), ratio);
                int y = (sh / steps) * i;
                int h = (sh / steps) + 1;
                sb.Draw(pixel, new Rectangle(0, y, sw, h), color);
            }
            
            // Subtle scanline effect
            for (int y = 0; y < sh; y += 4)
            {
                sb.Draw(pixel, new Rectangle(0, y, sw, 1), Color.White * 0.01f);
            }
        }
        
        private void DrawPanel(SpriteBatch sb, Texture2D pixel, int x, int y, int w, int h, Color bgColor, Color borderColor, int borderWidth = 2)
        {
            // Soft shadow with blur effect (multiple layers)
            for (int i = 3; i >= 0; i--)
            {
                float alpha = 0.15f - (i * 0.03f);
                sb.Draw(pixel, new Rectangle(x + 2 + i, y + 2 + i, w, h), Color.Black * alpha);
            }
            
            // Background with fade alpha
            sb.Draw(pixel, new Rectangle(x, y, w, h), bgColor * _fadeAlpha);
            
            // Border with smooth corners (fake it with lines)
            int cornerSize = 8;
            
            // Top border
            sb.Draw(pixel, new Rectangle(x + cornerSize, y, w - cornerSize * 2, borderWidth), borderColor * _fadeAlpha);
            // Bottom border
            sb.Draw(pixel, new Rectangle(x + cornerSize, y + h - borderWidth, w - cornerSize * 2, borderWidth), borderColor * _fadeAlpha);
            // Left border
            sb.Draw(pixel, new Rectangle(x, y + cornerSize, borderWidth, h - cornerSize * 2), borderColor * _fadeAlpha);
            // Right border
            sb.Draw(pixel, new Rectangle(x + w - borderWidth, y + cornerSize, borderWidth, h - cornerSize * 2), borderColor * _fadeAlpha);
            
            // Corner dots (decorative)
            sb.Draw(pixel, new Rectangle(x + 4, y + 4, 4, 4), borderColor * _fadeAlpha * 0.5f);
            sb.Draw(pixel, new Rectangle(x + w - 8, y + 4, 4, 4), borderColor * _fadeAlpha * 0.5f);
            sb.Draw(pixel, new Rectangle(x + 4, y + h - 8, 4, 4), borderColor * _fadeAlpha * 0.5f);
            sb.Draw(pixel, new Rectangle(x + w - 8, y + h - 8, 4, 4), borderColor * _fadeAlpha * 0.5f);
        }
        
        private void DrawCard(SpriteBatch sb, SpriteFont font, Texture2D pixel, int x, int y, int w, int h, string title, string subtitle, bool selected, bool glowing = false)
        {
            Color bgColor = selected ? BG_LIGHT : BG_MEDIUM;
            Color borderColor = selected ? ACCENT_PRIMARY : new Color(50, 50, 70);
            
            // Smooth hover animation
            if (selected)
            {
                float hoverOffset = (float)Math.Sin(_pulseTimer * 3) * 2f;
                y -= (int)hoverOffset;
            }
            
            if (glowing && selected)
            {
                // Soft pulsing glow
                float pulse = (float)Math.Sin(_pulseTimer) * 0.4f + 0.6f;
                Color glowColor = Color.Lerp(ACCENT_PRIMARY, ACCENT_SECONDARY, pulse) * 0.25f;
                
                // Multiple glow layers for smooth effect
                sb.Draw(pixel, new Rectangle(x - 8, y - 8, w + 16, h + 16), glowColor);
                sb.Draw(pixel, new Rectangle(x - 4, y - 4, w + 8, h + 8), glowColor * 0.5f);
            }
            
            DrawPanel(sb, pixel, x, y, w, h, bgColor, borderColor, 2);
            
            // Title
            if (!string.IsNullOrEmpty(title))
            {
                Vector2 titleSize = font.MeasureString(title);
                Vector2 titlePos = new Vector2(x + w / 2 - titleSize.X / 2, y + h / 2 - titleSize.Y / 2 - 10);
                Color titleColor = selected ? ACCENT_PRIMARY : TEXT_PRIMARY;
                sb.DrawString(font, title, titlePos + new Vector2(1, 1), Color.Black * 0.3f);
                sb.DrawString(font, title, titlePos, titleColor * _fadeAlpha);
            }
            
            // Subtitle
            if (!string.IsNullOrEmpty(subtitle))
            {
                Vector2 subSize = font.MeasureString(subtitle);
                Vector2 subPos = new Vector2(x + w / 2 - subSize.X / 2, y + h / 2 + 10);
                sb.DrawString(font, subtitle, subPos, TEXT_SECONDARY * _fadeAlpha);
            }
        }

        private void DrawMainMenuModern(SpriteBatch sb, SpriteFont font, Texture2D pixel, int screenWidth, int screenHeight)
        {
            // Clear clickable areas at the start of each draw
            _clickableAreas.Clear();
            
            // Title with modern effect
            string title = "MULTIPLAYER";
            Vector2 titleSize = font.MeasureString(title);
            Vector2 titlePos = new Vector2(screenWidth / 2 - titleSize.X / 2, 80);
            
            // Layered shadow for depth
            sb.DrawString(font, title, titlePos + new Vector2(4, 4), Color.Black * 0.5f);
            sb.DrawString(font, title, titlePos + new Vector2(2, 2), Color.Black * 0.3f);
            
            // Main title with gradient simulation (using tinted color)
            float titlePulse = (float)Math.Sin(_pulseTimer * 0.5f) * 0.2f + 0.8f;
            sb.DrawString(font, title, titlePos, ACCENT_PRIMARY * titlePulse * _fadeAlpha);
            
            // Subtitle with fade
            string subtitle = "Connect and compete with players worldwide";
            Vector2 subSize = font.MeasureString(subtitle);
            Vector2 subPos = new Vector2(screenWidth / 2 - subSize.X / 2, 135);
            sb.DrawString(font, subtitle, subPos, TEXT_SECONDARY * 0.9f * _fadeAlpha);
            
            // Decorative line
            int lineW = (int)(titleSize.X * 0.8f);
            int lineX = screenWidth / 2 - lineW / 2;
            sb.Draw(pixel, new Rectangle(lineX, 165, lineW, 2), ACCENT_PRIMARY * 0.3f * _fadeAlpha);
            
            // Menu cards
            int cardWidth = 380;
            int cardHeight = 85;
            int cardSpacing = 25;
            int startY = 230;
            
            string[] options = { "CONNECT TO SERVER", "LEADERBOARD", "SETTINGS", "BACK TO MENU" };
            string[] icons = { ">", "#", "*", "<" };
            Color[] iconColors = { ACCENT_SUCCESS, ACCENT_PRIMARY, ACCENT_WARNING, ACCENT_ERROR };
            
            for (int i = 0; i < options.Length; i++)
            {
                bool selected = i == _selection || i == _hoveredButton;
                int cardX = screenWidth / 2 - cardWidth / 2;
                int cardY = startY + i * (cardHeight + cardSpacing);
                
                // Register clickable area
                _clickableAreas.Add(new Rectangle(cardX, cardY, cardWidth, cardHeight));
                
                // Draw card background
                DrawCard(sb, font, pixel, cardX, cardY, cardWidth, cardHeight, "", "", selected, true);
                
                // Draw icon and text manually for better control
                string icon = icons[i];
                Vector2 iconSize = font.MeasureString(icon);
                Vector2 iconPos = new Vector2(cardX + 20, cardY + cardHeight / 2 - iconSize.Y / 2);
                
                Color currentIconColor = selected ? iconColors[i] : iconColors[i] * 0.6f;
                sb.DrawString(font, icon, iconPos, currentIconColor * _fadeAlpha);
                
                // Option text
                Vector2 textSize = font.MeasureString(options[i]);
                Vector2 textPos = new Vector2(cardX + 60, cardY + cardHeight / 2 - textSize.Y / 2);
                Color textColor = selected ? TEXT_PRIMARY : TEXT_SECONDARY;
                
                sb.DrawString(font, options[i], textPos + new Vector2(1, 1), Color.Black * 0.3f);
                sb.DrawString(font, options[i], textPos, textColor * _fadeAlpha);
                
                // Selection indicator
                if (selected)
                {
                    int indicatorW = 4;
                    int indicatorH = cardHeight - 20;
                    sb.Draw(pixel, new Rectangle(cardX - 12, cardY + 10, indicatorW, indicatorH), ACCENT_PRIMARY * _fadeAlpha);
                }
            }
            
            // Footer hint
            DrawCenteredText(sb, font, "UP/DOWN or MOUSE: Navigate  |  ENTER or CLICK: Select  |  ESC: Back", screenHeight - 60, TEXT_DISABLED * _fadeAlpha);
        }

        private void DrawConnectingModern(SpriteBatch sb, SpriteFont font, Texture2D pixel, int screenWidth, int screenHeight)
        {
            // Clear clickable areas (no buttons in this screen)
            _clickableAreas.Clear();
            
            // Central panel
            int panelW = 520;
            int panelH = 240;
            int panelX = screenWidth / 2 - panelW / 2;
            int panelY = screenHeight / 2 - panelH / 2;
            
            DrawPanel(sb, pixel, panelX, panelY, panelW, panelH, BG_MEDIUM, ACCENT_PRIMARY, 2);
            
            // Animated dots
            int dotCount = ((int)(_searchTimer * 2) % 4);
            string dots = new string('.', dotCount);
            string text = $"Connecting to server{dots}";
            
            Vector2 textSize = font.MeasureString(text);
            Vector2 textPos = new Vector2(screenWidth / 2 - textSize.X / 2, screenHeight / 2 - 40);
            sb.DrawString(font, text, textPos + new Vector2(1, 1), Color.Black * 0.3f);
            sb.DrawString(font, text, textPos, TEXT_PRIMARY * _fadeAlpha);
            
            // Connection attempt info
            if (_connectionAttempts > 1)
            {
                string attemptText = $"Attempt {_connectionAttempts}/{MAX_CONNECTION_ATTEMPTS}";
                DrawCenteredText(sb, font, attemptText, screenHeight / 2 - 10, ACCENT_WARNING * _fadeAlpha);
            }
            
            // Smooth loading bar
            int barW = 360;
            int barH = 10;
            int barX = screenWidth / 2 - barW / 2;
            int barY = screenHeight / 2 + 30;
            
            // Bar background with subtle border
            sb.Draw(pixel, new Rectangle(barX - 1, barY - 1, barW + 2, barH + 2), Color.White * 0.1f * _fadeAlpha);
            sb.Draw(pixel, new Rectangle(barX, barY, barW, barH), Color.Black * 0.6f);
            
            // Animated progress (smooth wave)
            float progress = (_searchTimer % 1.5f) / 1.5f;
            int fillW = (int)(barW * progress);
            
            // Gradient effect on progress bar
            Color progressColor = Color.Lerp(ACCENT_PRIMARY, ACCENT_SECONDARY, progress);
            sb.Draw(pixel, new Rectangle(barX, barY, fillW, barH), progressColor * _fadeAlpha);
            
            // Glow effect on progress tip
            if (fillW > 0)
            {
                sb.Draw(pixel, new Rectangle(barX + fillW - 8, barY - 2, 12, barH + 4), progressColor * 0.5f * _fadeAlpha);
            }
            
            // Status text
            string statusText = "Please wait...";
            if (_connectionAttempts > 1)
                statusText = "Retrying connection...";
            
            DrawCenteredText(sb, font, statusText, screenHeight / 2 + 60, TEXT_SECONDARY * 0.8f * _fadeAlpha);
            
            // Footer
            DrawCenteredText(sb, font, "ESC: Cancel", screenHeight - 60, TEXT_DISABLED * _fadeAlpha);
        }
        
        private void DrawModeSelectModern(SpriteBatch sb, SpriteFont font, Texture2D pixel, int screenWidth, int screenHeight)
        {
            // Clear clickable areas
            _clickableAreas.Clear();
            
            // Title
            string title = "SELECT GAME MODE";
            Vector2 titleSize = font.MeasureString(title);
            Vector2 titlePos = new Vector2(screenWidth / 2 - titleSize.X / 2, 80);
            sb.DrawString(font, title, titlePos + new Vector2(2, 2), Color.Black * 0.5f);
            sb.DrawString(font, title, titlePos, Color.Cyan);
            
            // Two big cards side by side
            int cardW = 350;
            int cardH = 300;
            int spacing = 50;
            int totalW = cardW * 2 + spacing;
            int startX = screenWidth / 2 - totalW / 2;
            int cardY = 200;
            
            // Quick Play Card
            int quickPlayX = startX;
            bool quickSelected = _modeSelection == 0 || _hoveredButton == 0;
            
            // Register clickable area for Quick Play
            _clickableAreas.Add(new Rectangle(quickPlayX, cardY, cardW, cardH));
            
            DrawCard(sb, font, pixel, quickPlayX, cardY, cardW, cardH, "", "", quickSelected, true);
            
            // Icon
            DrawCenteredTextInPanel(sb, font, "[PLAY]", quickPlayX, cardY + 50, cardW, quickSelected ? Color.Cyan : Color.White);
            DrawCenteredTextInPanel(sb, font, "QUICK PLAY", quickPlayX, cardY + 100, cardW, quickSelected ? Color.Yellow : Color.White);
            DrawCenteredTextInPanel(sb, font, "Random Matchmaking", quickPlayX, cardY + 140, cardW, Color.Gray);
            DrawCenteredTextInPanel(sb, font, "Find any opponent", quickPlayX, cardY + 170, cardW, Color.Gray * 0.7f);
            DrawCenteredTextInPanel(sb, font, "Fast and casual", quickPlayX, cardY + 195, cardW, Color.Gray * 0.7f);
            
            // Private Room Card
            int privateX = startX + cardW + spacing;
            bool privateSelected = _modeSelection == 1 || _hoveredButton == 1;
            
            // Register clickable area for Private Room
            _clickableAreas.Add(new Rectangle(privateX, cardY, cardW, cardH));
            
            DrawCard(sb, font, pixel, privateX, cardY, cardW, cardH, "", "", privateSelected, true);
            
            // Icon
            DrawCenteredTextInPanel(sb, font, "[ROOM]", privateX, cardY + 50, cardW, privateSelected ? Color.Green : Color.White);
            DrawCenteredTextInPanel(sb, font, "PRIVATE ROOM", privateX, cardY + 100, cardW, privateSelected ? Color.Yellow : Color.White);
            DrawCenteredTextInPanel(sb, font, "Play with Friends", privateX, cardY + 140, cardW, Color.Gray);
            DrawCenteredTextInPanel(sb, font, "Use room codes", privateX, cardY + 170, cardW, Color.Gray * 0.7f);
            DrawCenteredTextInPanel(sb, font, "Invite specific players", privateX, cardY + 195, cardW, Color.Gray * 0.7f);
            
            // Footer
            DrawCenteredText(sb, font, "LEFT/RIGHT or CLICK: Select Mode  |  ENTER or DOUBLE-CLICK: Confirm  |  ESC: Back", screenHeight - 60, Color.Gray * 0.6f);
        }
        
        private void DrawCenteredTextInPanel(SpriteBatch sb, SpriteFont font, string text, int panelX, int y, int panelW, Color color)
        {
            Vector2 size = font.MeasureString(text);
            Vector2 pos = new Vector2(panelX + panelW / 2 - size.X / 2, y);
            sb.DrawString(font, text, pos, color);
        }
        
        private void DrawCreateRoomModern(SpriteBatch sb, SpriteFont font, Texture2D pixel, int screenWidth, int screenHeight)
        {
            // Title
            DrawCenteredText(sb, font, "PRIVATE ROOM", 70, Color.Green);
            
            // Main panel
            int panelW = 600;
            int panelH = 400;
            int panelX = screenWidth / 2 - panelW / 2;
            int panelY = 150;
            
            DrawPanel(sb, pixel, panelX, panelY, panelW, panelH, new Color(20, 20, 35), Color.Green, 3);
            
            // Two options as smaller cards
            int cardW = 250;
            int cardH = 120;
            int cardsStartY = panelY + 80;
            
            // Create Room option
            int card1X = panelX + (panelW / 2 - cardW) / 2;
            bool createSelected = _selection == 0;
            DrawCard(sb, font, pixel, card1X, cardsStartY, cardW, cardH, "CREATE ROOM", "", createSelected, true);
            
            // Join Room option
            int card2X = panelX + panelW / 2 + (panelW / 2 - cardW) / 2;
            bool joinSelected = _selection == 1;
            DrawCard(sb, font, pixel, card2X, cardsStartY, cardW, cardH, "JOIN ROOM", "", joinSelected, true);
            
            // Show room code if creating
            if (createSelected && !string.IsNullOrEmpty(_roomCode))
            {
                int codeY = cardsStartY + cardH + 50;
                DrawCenteredText(sb, font, "Your Room Code:", codeY, Color.White);
                
                // Code box with glow
                int codeBoxW = 250;
                int codeBoxH = 60;
                int codeBoxX = screenWidth / 2 - codeBoxW / 2;
                int codeBoxY = codeY + 35;
                
                float pulse = (float)Math.Sin(_pulseTimer * 2) * 0.3f + 0.7f;
                DrawPanel(sb, pixel, codeBoxX, codeBoxY, codeBoxW, codeBoxH, new Color(30, 30, 50), Color.Yellow * pulse, 3);
                
                Vector2 codeSize = font.MeasureString(_roomCode);
                Vector2 codePos = new Vector2(screenWidth / 2 - codeSize.X / 2, codeBoxY + codeBoxH / 2 - codeSize.Y / 2);
                sb.DrawString(font, _roomCode, codePos, Color.Yellow);
                
                DrawCenteredText(sb, font, "Share this code with your friend!", codeY + 110, Color.Gray);
            }
            
            // Instructions at bottom
            DrawCenteredText(sb, font, "LEFT/RIGHT: Select  |  ENTER: Confirm  |  ESC: Back", screenHeight - 60, Color.Gray * 0.6f);
        }
        
        private void DrawJoinRoomModern(SpriteBatch sb, SpriteFont font, Texture2D pixel, int screenWidth, int screenHeight)
        {
            // Title
            DrawCenteredText(sb, font, "JOIN PRIVATE ROOM", 70, Color.Green);
            
            // Main panel
            int panelW = 550;
            int panelH = 350;
            int panelX = screenWidth / 2 - panelW / 2;
            int panelY = 180;
            
            DrawPanel(sb, pixel, panelX, panelY, panelW, panelH, new Color(20, 20, 35), Color.Green, 3);
            
            // Instructions
            DrawCenteredText(sb, font, "Enter 6-character room code", panelY + 50, Color.White);
            
            // Input box with 6 slots
            int slotW = 50;
            int slotH = 70;
            int slotSpacing = 15;
            int totalW = slotW * 6 + slotSpacing * 5;
            int startX = screenWidth / 2 - totalW / 2;
            int slotY = panelY + 120;
            
            for (int i = 0; i < 6; i++)
            {
                int slotX = startX + i * (slotW + slotSpacing);
                bool hasChar = i < _roomCode.Length;
                bool isCurrent = i == _roomCode.Length;
                
                Color slotBg = hasChar ? new Color(30, 40, 50) : new Color(15, 15, 25);
                Color slotBorder = isCurrent ? Color.Yellow : new Color(50, 50, 70);
                
                if (isCurrent)
                {
                    // Pulsing cursor
                    float pulse = (float)Math.Sin(_pulseTimer * 4) * 0.3f + 0.7f;
                    slotBorder = Color.Yellow * pulse;
                }
                
                DrawPanel(sb, pixel, slotX, slotY, slotW, slotH, slotBg, slotBorder, 2);
                
                if (hasChar)
                {
                    string c = _roomCode[i].ToString();
                    Vector2 charSize = font.MeasureString(c);
                    Vector2 charPos = new Vector2(slotX + slotW / 2 - charSize.X / 2, slotY + slotH / 2 - charSize.Y / 2);
                    sb.DrawString(font, c, charPos, Color.White);
                }
            }
            
            // Hint text
            if (_roomCode.Length < 6)
            {
                DrawCenteredText(sb, font, "Type letters and numbers...", panelY + 230, Color.Gray);
            }
            else
            {
                DrawCenteredText(sb, font, "Press ENTER to join!", panelY + 230, Color.Green);
            }
            
            // Instructions at bottom
            DrawCenteredText(sb, font, "TYPE: A-Z, 0-9  |  BACKSPACE: Delete  |  ESC: Cancel", screenHeight - 60, Color.Gray * 0.6f);
        }

        private void DrawLobby(SpriteBatch sb, SpriteFont font, Texture2D pixel, int screenWidth, int screenHeight)
        {
            // Title with room code if private
            if (_isPrivateRoom && !string.IsNullOrEmpty(_roomCode))
            {
                DrawCenteredText(sb, font, $"========== PRIVATE ROOM: {_roomCode} ==========", screenHeight / 7, Color.Green);
            }
            else
            {
                DrawCenteredText(sb, font, "========== QUICK PLAY ==========", screenHeight / 7, Color.Cyan);
            }
            
            DrawCenteredText(sb, font, "Select Level to Play", screenHeight / 7 + 35, Color.White);

            // Tabs with better design
            int tabY = screenHeight / 4 + 20;
            
            // Tab borders
            if (_selectedSource == LevelSource.Official)
            {
                DrawCenteredText(sb, font, "===================   -------------------", tabY, Color.Yellow);
                DrawCenteredText(sb, font, "[ OFFICIAL LEVELS ]   LOCAL LEVELS", tabY + 30, Color.Yellow);
                DrawCenteredText(sb, font, "===================   -------------------", tabY + 60, Color.Yellow);
            }
            else
            {
                DrawCenteredText(sb, font, "-------------------   ===================", tabY, Color.Yellow);
                DrawCenteredText(sb, font, "OFFICIAL LEVELS   [ LOCAL LEVELS ]", tabY + 30, Color.Yellow);
                DrawCenteredText(sb, font, "-------------------   ===================", tabY + 60, Color.Yellow);
            }
            
            // Security indicator with icons
            string securityText = _selectedSource == LevelSource.Official 
                ? "[SAFE] VERIFIED | Auto-download from server" 
                : "[CUSTOM] Share via Discord/Drive (Press S)";
            Color securityColor = _selectedSource == LevelSource.Official ? Color.LimeGreen : Color.Orange;
            DrawCenteredText(sb, font, securityText, tabY + 90, securityColor);
            
            // Download progress bar (improved design)
            if (_isDownloading)
            {
                int barY = tabY + 130;
                int barWidth = 500;
                int barHeight = 30;
                int barX = (screenWidth - barWidth) / 2;
                
                // Outer border
                sb.Draw(pixel, new Rectangle(barX - 2, barY - 2, barWidth + 4, barHeight + 4), Color.White);
                
                // Background
                sb.Draw(pixel, new Rectangle(barX, barY, barWidth, barHeight), Color.DarkGray);
                
                // Progress (gradient effect)
                int progressWidth = (int)(barWidth * (_downloadProgress / 100f));
                Color progressColor = Color.Lerp(Color.Yellow, Color.Green, _downloadProgress / 100f);
                sb.Draw(pixel, new Rectangle(barX, barY, progressWidth, barHeight), progressColor);
                
                // Text overlay
                DrawCenteredText(sb, font, $"Downloading... {_downloadProgress}%", barY + 5, Color.White);
                
                DrawCenteredText(sb, font, "Please wait...", barY + barHeight + 20, Color.Gray);
                return; // Don't show level list while downloading
            }

            // Level list with better presentation
            int listY = screenHeight / 2 + 20;
            
            if (_selectedSource == LevelSource.Official)
            {
                // Official levels from server
                if (_officialLevels.Count == 0)
                {
                    DrawCenteredText(sb, font, "Loading official levels...", listY, Color.Gray);
                }
                else
                {
                    DrawCenteredText(sb, font, "--- AVAILABLE LEVELS ---", listY - 30, Color.Cyan);
                    
                    // Show scroll indicators
                    int totalLevels = _officialLevels.Count;
                    if (totalLevels > MAX_VISIBLE_LEVELS)
                    {
                        DrawCenteredText(sb, font, $"[{_levelSelection + 1}/{totalLevels}]", listY - 50, Color.Gray);
                        if (_levelScrollOffset > 0)
                            DrawCenteredText(sb, font, "^^^ MORE ^^^", listY - 10, Color.Yellow);
                    }
                    
                    // Calculate visible range
                    int startIdx = _levelScrollOffset;
                    int endIdx = Math.Min(startIdx + MAX_VISIBLE_LEVELS, totalLevels);
                    
                    for (int i = startIdx; i < endIdx; i++)
                    {
                        var level = _officialLevels[i];
                        bool selected = i == _levelSelection;
                        int displayIdx = i - startIdx;
                        
                        // Check if cached in memory
                        bool isCached = LevelCache.Instance.IsCached(level.Id);
                        
                        string statusIcon = isCached ? "[OK]" : "[DL]";
                        string diffDisplay = $"[{level.Difficulty}]";
                        
                        if (selected)
                        {
                            // Selected item with border
                            DrawCenteredText(sb, font, "=====================================", listY + displayIdx * 60 - 15, Color.Yellow);
                            DrawCenteredText(sb, font, $">>> {statusIcon} {level.Name} {diffDisplay} <<<", listY + displayIdx * 60 + 10, Color.Yellow);
                            DrawCenteredText(sb, font, "=====================================", listY + displayIdx * 60 + 35, Color.Yellow);
                            
                            if (!isCached)
                            {
                                DrawCenteredText(sb, font, "ENTER: Download (RAM only)", listY + displayIdx * 60 + 50, Color.Cyan);
                            }
                            else
                            {
                                DrawCenteredText(sb, font, "ENTER: Find Match", listY + displayIdx * 60 + 50, Color.Cyan);
                            }
                        }
                        else
                        {
                            DrawCenteredText(sb, font, $"{statusIcon} {level.Name} {diffDisplay}", listY + displayIdx * 60 + 10, Color.White);
                        }
                    }
                    
                    // Show bottom scroll indicator
                    if (endIdx < totalLevels)
                    {
                        DrawCenteredText(sb, font, "vvv MORE vvv", listY + MAX_VISIBLE_LEVELS * 60 + 20, Color.Yellow);
                    }
                }
            }
            else
            {
                // Local levels
                if (_availableLevels.Count == 0)
                {
                    DrawCenteredText(sb, font, "--- NO LOCAL LEVELS ---", listY - 30, Color.Gray);
                    DrawCenteredText(sb, font, "Put .FNMM files in levels/ folder", listY + 10, Color.Gray);
                    DrawCenteredText(sb, font, "or", listY + 40, Color.Gray * 0.6f);
                    DrawCenteredText(sb, font, "Download from OFFICIAL tab", listY + 70, Color.Gray);
                }
                else
                {
                    DrawCenteredText(sb, font, "--- YOUR LEVELS ---", listY - 30, Color.Cyan);
                    
                    // Show scroll indicators
                    int totalLevels = _availableLevels.Count;
                    if (totalLevels > MAX_VISIBLE_LEVELS)
                    {
                        DrawCenteredText(sb, font, $"[{_levelSelection + 1}/{totalLevels}]", listY - 50, Color.Gray);
                        if (_levelScrollOffset > 0)
                            DrawCenteredText(sb, font, "^^^ MORE ^^^", listY - 10, Color.Yellow);
                    }
                    
                    // Calculate visible range
                    int startIdx = _levelScrollOffset;
                    int endIdx = Math.Min(startIdx + MAX_VISIBLE_LEVELS, totalLevels);
                    
                    for (int i = startIdx; i < endIdx; i++)
                    {
                        bool selected = i == _levelSelection;
                        int displayIdx = i - startIdx;
                        string filename = _availableLevels[i];
                        string displayName = filename.Replace(".FNMM", "");
                        
                        if (selected)
                        {
                            DrawCenteredText(sb, font, "============================", listY + displayIdx * 55 - 10, Color.Yellow);
                            DrawCenteredText(sb, font, $">>> {displayName} <<<", listY + displayIdx * 55 + 15, Color.Yellow);
                            DrawCenteredText(sb, font, "============================", listY + displayIdx * 55 + 40, Color.Yellow);
                            
                            DrawCenteredText(sb, font, "S: Share | ENTER: Find Match", listY + displayIdx * 55 + 50, Color.Cyan);
                        }
                        else
                        {
                            DrawCenteredText(sb, font, $"* {displayName}", listY + displayIdx * 55 + 15, Color.White);
                        }
                    }
                    
                    // Show bottom scroll indicator
                    if (endIdx < totalLevels)
                    {
                        DrawCenteredText(sb, font, "vvv MORE vvv", listY + MAX_VISIBLE_LEVELS * 55 + 20, Color.Yellow);
                    }
                }
            }

            // Instructions at bottom (improved)
            DrawCenteredText(sb, font, "==========================================", screenHeight - 110, Color.Gray * 0.5f);
            DrawCenteredText(sb, font, "LEFT/RIGHT: Tabs | UP/DOWN: Select | ENTER: Confirm | ESC: Back", screenHeight - 75, Color.Gray);
        }
        
        private void DrawLobbyModern(SpriteBatch sb, SpriteFont font, Texture2D pixel, int screenWidth, int screenHeight)
        {
            // Clear clickable areas
            _clickableAreas.Clear();
            
            // Header with room code
            if (_isPrivateRoom && !string.IsNullOrEmpty(_roomCode))
            {
                DrawCenteredText(sb, font, $"PRIVATE ROOM: {_roomCode}", 50, Color.Green);
            }
            else
            {
                DrawCenteredText(sb, font, "QUICK PLAY", 50, Color.Cyan);
            }
            
            // Download progress takes full attention
            if (_isDownloading)
            {
                int dlPanelW = 600;
                int dlPanelH = 150;
                int dlPanelX = screenWidth / 2 - dlPanelW / 2;
                int dlPanelY = screenHeight / 2 - dlPanelH / 2;
                
                DrawPanel(sb, pixel, dlPanelX, dlPanelY, dlPanelW, dlPanelH, new Color(20, 20, 35), Color.Cyan, 3);
                
                DrawCenteredText(sb, font, "Downloading Level...", dlPanelY + 30, Color.White);
                
                // Progress bar
                int barW = 500;
                int barH = 30;
                int barX = screenWidth / 2 - barW / 2;
                int barY = dlPanelY + 70;
                
                sb.Draw(pixel, new Rectangle(barX - 2, barY - 2, barW + 4, barH + 4), Color.White * 0.3f);
                sb.Draw(pixel, new Rectangle(barX, barY, barW, barH), Color.Black * 0.5f);
                
                int progressW = (int)(barW * (_downloadProgress / 100f));
                Color progressColor = Color.Lerp(Color.Yellow, Color.Green, _downloadProgress / 100f);
                sb.Draw(pixel, new Rectangle(barX, barY, progressW, barH), progressColor);
                
                DrawCenteredText(sb, font, $"{_downloadProgress}%", barY + 6, Color.White);
                return;
            }
            
            // Two-panel layout: Tabs on left, level list on right
            int leftPanelW = 300;
            int rightPanelW = screenWidth - leftPanelW - 100;
            int lobbyPanelH = screenHeight - 200;
            int lobbyPanelY = 100;
            
            // Left panel - Tabs
            DrawPanel(sb, pixel, 30, lobbyPanelY, leftPanelW, lobbyPanelH, new Color(20, 20, 35), Color.Cyan, 3);
            
            // Tab buttons
            int tabH = 100;
            int tabY = lobbyPanelY + 50;
            
            // Register tab clickable areas (indices 0 and 1)
            _clickableAreas.Add(new Rectangle(50, tabY, leftPanelW - 40, tabH));  // Official tab
            _clickableAreas.Add(new Rectangle(50, tabY + tabH + 20, leftPanelW - 40, tabH));  // Local tab
            
            bool officialSelected = _selectedSource == LevelSource.Official || _hoveredButton == 0;
            bool localSelected = _selectedSource == LevelSource.Local || _hoveredButton == 1;
            
            DrawCard(sb, font, pixel, 50, tabY, leftPanelW - 40, tabH, "OFFICIAL", "Verified Levels", officialSelected, true);
            DrawCard(sb, font, pixel, 50, tabY + tabH + 20, leftPanelW - 40, tabH, "LOCAL", "Your Levels", localSelected, true);
            
            // Security badge
            int badgeY = lobbyPanelY + lobbyPanelH - 100;
            string badgeText = _selectedSource == LevelSource.Official ? "[SAFE] Verified" : "[CUSTOM] Shared";
            Color badgeColor = _selectedSource == LevelSource.Official ? Color.LimeGreen : Color.Orange;
            DrawCenteredTextInPanel(sb, font, badgeText, 30, badgeY, leftPanelW, badgeColor);
            
            // Right panel - Level list
            int rightX = 30 + leftPanelW + 40;
            DrawPanel(sb, pixel, rightX, lobbyPanelY, rightPanelW, lobbyPanelH, new Color(20, 20, 35), Color.Cyan, 3);
            
            // Level list header
            int listY = lobbyPanelY + 30;
            int levelCount = _selectedSource == LevelSource.Official ? _officialLevels.Count : _availableLevels.Count;
            
            if (levelCount > MAX_VISIBLE_LEVELS)
            {
                DrawCenteredTextInPanel(sb, font, $"[{_levelSelection + 1}/{levelCount}]", rightX, listY, rightPanelW, Color.Gray);
                listY += 30;
            }
            
            // Levels as cards
            if (_selectedSource == LevelSource.Official)
            {
                if (_officialLevels.Count == 0)
                {
                    DrawCenteredTextInPanel(sb, font, "Loading levels...", rightX, lobbyPanelY + lobbyPanelH / 2, rightPanelW, Color.Gray);
                }
                else
                {
                    int startIdx = _levelScrollOffset;
                    int endIdx = Math.Min(startIdx + MAX_VISIBLE_LEVELS, _officialLevels.Count);
                    
                    for (int i = startIdx; i < endIdx; i++)
                    {
                        var level = _officialLevels[i];
                        bool selected = i == _levelSelection;
                        int displayIdx = i - startIdx;
                        bool isCached = LevelCache.Instance.IsCached(level.Id);
                        
                        int cardX = rightX + 20;
                        int cardY = listY + displayIdx * 75;
                        int cardW = rightPanelW - 40;
                        int cardH = 65;
                        
                        // Register clickable area for this level (starting from index 2)
                        _clickableAreas.Add(new Rectangle(cardX, cardY, cardW, cardH));
                        
                        // Check if hovered
                        bool hovered = _hoveredButton == (2 + displayIdx);
                        
                        DrawCard(sb, font, pixel, cardX, cardY, cardW, cardH, "", "", selected || hovered, true);
                        
                        // Level info
                        string status = isCached ? "[OK]" : "[DL]";
                        Color statusColor = isCached ? Color.Green : Color.Yellow;
                        sb.DrawString(font, status, new Vector2(cardX + 15, cardY + 15), statusColor);
                        sb.DrawString(font, level.Name, new Vector2(cardX + 80, cardY + 15), Color.White);
                        sb.DrawString(font, $"[{level.Difficulty}]", new Vector2(cardX + 15, cardY + 38), Color.Gray);
                    }
                }
            }
            else
            {
                if (_availableLevels.Count == 0)
                {
                    DrawCenteredTextInPanel(sb, font, "No local levels", rightX, lobbyPanelY + lobbyPanelH / 2 - 30, rightPanelW, Color.Gray);
                    DrawCenteredTextInPanel(sb, font, "Put .FNMM files in levels/", rightX, lobbyPanelY + lobbyPanelH / 2, rightPanelW, Color.Gray * 0.7f);
                }
                else
                {
                    int startIdx = _levelScrollOffset;
                    int endIdx = Math.Min(startIdx + MAX_VISIBLE_LEVELS, _availableLevels.Count);
                    
                    for (int i = startIdx; i < endIdx; i++)
                    {
                        bool selected = i == _levelSelection;
                        int displayIdx = i - startIdx;
                        string filename = _availableLevels[i].Replace(".FNMM", "");
                        
                        int cardX = rightX + 20;
                        int cardY = listY + displayIdx * 75;
                        int cardW = rightPanelW - 40;
                        int cardH = 65;
                        
                        // Register clickable area for this level (starting from index 2)
                        _clickableAreas.Add(new Rectangle(cardX, cardY, cardW, cardH));
                        
                        // Check if hovered
                        bool hovered = _hoveredButton == (2 + displayIdx);
                        
                        DrawCard(sb, font, pixel, cardX, cardY, cardW, cardH, "", "", selected || hovered, true);
                        sb.DrawString(font, filename, new Vector2(cardX + 15, cardY + 22), Color.White);
                    }
                }
            }
            
            // Footer hints
            DrawCenteredText(sb, font, "LEFT/RIGHT or CLICK TABS: Switch  |  UP/DOWN or CLICK: Select  |  ENTER or DOUBLE-CLICK: Confirm  |  ESC: Back", screenHeight - 50, Color.Gray * 0.6f);
        }
        
        private void DrawSearchingModern(SpriteBatch sb, SpriteFont font, Texture2D pixel, int screenWidth, int screenHeight)
        {
            // Clear clickable areas (no buttons in this screen)
            _clickableAreas.Clear();
            
            // Central animated panel
            int panelW = 500;
            int panelH = 250;
            int panelX = screenWidth / 2 - panelW / 2;
            int panelY = screenHeight / 2 - panelH / 2;
            
            DrawPanel(sb, pixel, panelX, panelY, panelW, panelH, new Color(20, 20, 35), Color.Cyan, 3);
            
            // Animated searching text
            string dots = new string('.', ((int)(_searchTimer * 3) % 4));
            DrawCenteredText(sb, font, $"Searching for opponent{dots}", panelY + 60, Color.White);
            
            // Spinning animation (fake with pulsing)
            float pulse = (float)Math.Sin(_searchTimer * 3) * 0.5f + 0.5f;
            int circleSize = 60;
            int circleX = screenWidth / 2 - circleSize / 2;
            int circleY = panelY + 120;
            sb.Draw(pixel, new Rectangle(circleX, circleY, circleSize, circleSize), Color.Cyan * pulse);
            
            // Timer
            DrawCenteredText(sb, font, $"Time: {_searchTimer:F1}s", panelY + 200, Color.Gray);
            
            // Hint
            DrawCenteredText(sb, font, "ESC to cancel", screenHeight - 60, Color.Gray * 0.6f);
        }
        
        private void DrawMatchFoundModern(SpriteBatch sb, SpriteFont font, Texture2D pixel, int screenWidth, int screenHeight)
        {
            // If countdown is active, show countdown screen instead
            if (_isCountdownActive)
            {
                DrawCountdownScreen(sb, font, pixel, screenWidth, screenHeight);
                return;
            }
            
            // Success panel
            int panelW = 600;
            int panelH = 350;
            int panelX = screenWidth / 2 - panelW / 2;
            int panelY = screenHeight / 2 - panelH / 2;
            
            DrawPanel(sb, pixel, panelX, panelY, panelW, panelH, new Color(20, 20, 35), Color.Green, 3);
            
            // Title with glow
            DrawCenteredText(sb, font, "MATCH FOUND!", panelY + 40, Color.Green);
            
            // Opponent info
            DrawCenteredText(sb, font, "VS", panelY + 100, Color.Gray);
            DrawCenteredText(sb, font, _opponentName ?? "Unknown", panelY + 130, Color.White);
            
            // Ready status cards
            int cardW = 200;
            int cardH = 80;
            int cardSpacing = 50;
            int cardsY = panelY + 190;
            
            // Your status
            int card1X = panelX + (panelW / 2 - cardW) / 2 - cardSpacing / 2;
            Color yourColor = _isReady ? Color.Green : Color.Red;
            DrawCard(sb, font, pixel, card1X, cardsY, cardW, cardH, _isReady ? "READY" : "NOT READY", "You", false, false);
            
            // Opponent status
            int card2X = panelX + panelW / 2 + cardSpacing / 2;
            Color oppColor = _opponentReady ? Color.Green : Color.Red;
            DrawCard(sb, font, pixel, card2X, cardsY, cardW, cardH, _opponentReady ? "READY" : "WAITING", "Opponent", false, false);
            
            // Hint
            DrawCenteredText(sb, font, "ENTER: Toggle Ready  |  ESC: Cancel", screenHeight - 60, Color.Gray * 0.6f);
        }
        
        private void DrawCountdownScreen(SpriteBatch sb, SpriteFont font, Texture2D pixel, int screenWidth, int screenHeight)
        {
            // Darkened overlay
            sb.Draw(pixel, new Rectangle(0, 0, screenWidth, screenHeight), Color.Black * 0.7f * _fadeAlpha);
            
            // Countdown number or "GO!"
            string countdownText = _countdownValue > 0 ? ((int)_countdownValue).ToString() : "GO!";
            Vector2 textSize = font.MeasureString(countdownText);
            
            // Scale text for dramatic effect
            float scale = _countdownValue > 0 ? 8f : 6f;
            Vector2 scaledSize = textSize * scale;
            Vector2 textPos = new Vector2(screenWidth / 2 - scaledSize.X / 2, screenHeight / 2 - scaledSize.Y / 2);
            
            // Draw with glow effect
            Color glowColor = _countdownValue > 0 ? ACCENT_WARNING : ACCENT_SUCCESS;
            
            // Outer glow
            for (int i = 0; i < 8; i++)
            {
                float angle = (float)(i * Math.PI / 4);
                Vector2 offset = new Vector2((float)Math.Cos(angle), (float)Math.Sin(angle)) * 8f;
                sb.DrawString(font, countdownText, textPos + offset, glowColor * 0.3f * _fadeAlpha, 0f, Vector2.Zero, scale, SpriteEffects.None, 0f);
            }
            
            // Main text
            sb.DrawString(font, countdownText, textPos, TEXT_PRIMARY * _fadeAlpha, 0f, Vector2.Zero, scale, SpriteEffects.None, 0f);
            
            // "Get Ready!" text above countdown
            if (_countdownValue > 0)
            {
                DrawCenteredText(sb, font, "GET READY!", screenHeight / 2 - 150, ACCENT_PRIMARY * _fadeAlpha);
            }
        }

        private void DrawSearching(SpriteBatch sb, SpriteFont font, int screenWidth, int screenHeight)
        {
            string dots = new string('.', ((int)(_searchTimer * 3) % 4));
            string text = $"Searching for opponent{dots}";
            DrawCenteredText(sb, font, text, screenHeight / 2, Color.White);

            string timer = $"Time: {_searchTimer:F1}s";
            DrawCenteredText(sb, font, timer, screenHeight / 2 + 50, Color.Gray);

            DrawCenteredText(sb, font, "ESC to cancel", screenHeight - 100, Color.Gray);
        }

        private void DrawMatchFound(SpriteBatch sb, SpriteFont font, Texture2D pixel, int screenWidth, int screenHeight)
        {
            DrawCenteredText(sb, font, "Match Found!", screenHeight / 3, Color.Green);
            DrawCenteredText(sb, font, $"Opponent: {_opponentName}", screenHeight / 2 - 50, Color.White);

            // Ready status
            string readyText = _isReady ? "[READY]" : "[NOT READY]";
            Color readyColor = _isReady ? Color.Green : Color.Red;
            DrawCenteredText(sb, font, readyText, screenHeight / 2 + 20, readyColor);

            string oppReadyText = _opponentReady ? "Opponent: READY" : "Opponent: Waiting...";
            Color oppReadyColor = _opponentReady ? Color.Green : Color.Gray;
            DrawCenteredText(sb, font, oppReadyText, screenHeight / 2 + 60, oppReadyColor);

            DrawCenteredText(sb, font, "ENTER to toggle ready | ESC to cancel", screenHeight - 100, Color.Gray);
        }

        /// <summary>
        /// Reset input cooldown (called when menu is opened).
        /// </summary>
        public void ResetInputCooldown()
        {
            _inputCooldown = INPUT_COOLDOWN_TIME;
            _prevKb = Keyboard.GetState();
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
            _state = MenuState.Lobby;
            _isReady = false;
            _opponentReady = false;
            _isCountdownActive = false;
            _countdownValue = 0f;
            _searchTimer = 0f;
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
                _state = MenuState.Lobby;
                return;
            }

            if (_game == null)
            {
                Console.WriteLine($"[MultiplayerMenu] ERROR: Game instance not set!");
                ShowError("Game instance not initialized");
                _state = MenuState.Lobby;
                return;
            }

            string opponentName = NetworkManager.Instance.OpponentName ?? "Opponent";
            int playerRole = NetworkManager.Instance.PlayerRole;

            Console.WriteLine($"[MultiplayerMenu] Starting multiplayer match in existing Game1 instance");
            Console.WriteLine($"  Level: {levelPath}");
            Console.WriteLine($"  Opponent: {opponentName}");
            Console.WriteLine($"  Player Role: {playerRole}");

            try
            {
                // Start multiplayer match in existing Game1 instance (no new window)
                _game.StartMultiplayerMatch(levelPath, opponentName, playerRole);
                
                Console.WriteLine("[MultiplayerMenu] Match started successfully");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[MultiplayerMenu] ERROR starting match: {ex.Message}");
                Console.WriteLine($"[MultiplayerMenu] Stack trace: {ex.StackTrace}");
                ShowError($"Failed to start match: {ex.Message}");
                _state = MenuState.Lobby;
            }
        }

        /// <summary>
        /// Clean up resources.
        /// </summary>
        public void Cleanup()
        {
            NetworkManager.Instance.OnConnected -= OnConnected;
            NetworkManager.Instance.OnDisconnected -= OnDisconnected;
            NetworkManager.Instance.OnMatchFound -= OnMatchFound;
            NetworkManager.Instance.OnOpponentReadyChanged -= OnOpponentReadyChanged;
            NetworkManager.Instance.OnCountdownStart -= OnCountdownStart;
            NetworkManager.Instance.OnLevelListReceived -= OnLevelListReceived;
            
            // Clear all cached official levels from memory
            LevelCache.Instance.ClearCache();
        }
        
        private void DrawSettingsModern(SpriteBatch sb, SpriteFont font, Texture2D pixel, int screenWidth, int screenHeight)
        {
            // Clear clickable areas
            _clickableAreas.Clear();
            
            var settings = MultiplayerSettings.Instance;
            
            // Title
            string title = "MULTIPLAYER SETTINGS";
            Vector2 titleSize = font.MeasureString(title);
            Vector2 titlePos = new Vector2(screenWidth / 2 - titleSize.X / 2, 60);
            sb.DrawString(font, title, titlePos + new Vector2(2, 2), Color.Black * 0.5f);
            sb.DrawString(font, title, titlePos, ACCENT_WARNING * _fadeAlpha);
            
            // Category tabs at top (scrollable)
            string[] categories = { "USER", "GAMEPLAY", "NOTIFICATIONS", "PRIVACY", "PERFORMANCE", "VISUAL" };
            int tabW = 150;
            int tabH = 45;
            int tabSpacing = 8;
            int maxVisibleTabs = Math.Min(6, (screenWidth - 100) / (tabW + tabSpacing));
            int tabStartX = screenWidth / 2 - (Math.Min(maxVisibleTabs, categories.Length) * (tabW + tabSpacing) - tabSpacing) / 2;
            int tabY = 130;
            
            // Calculate tab scroll offset to keep selected category visible
            int tabScrollOffset = 0;
            if (_settingsCategory >= maxVisibleTabs)
            {
                tabScrollOffset = Math.Min(_settingsCategory - maxVisibleTabs + 1, categories.Length - maxVisibleTabs);
            }
            
            // Draw visible category tabs
            for (int i = 0; i < Math.Min(maxVisibleTabs, categories.Length); i++)
            {
                int catIndex = tabScrollOffset + i;
                if (catIndex >= categories.Length) break;
                
                bool selected = catIndex == _settingsCategory;
                int catX = tabStartX + i * (tabW + tabSpacing);
                
                Color bgColor = selected ? BG_LIGHT : BG_MEDIUM;
                Color borderColor = selected ? ACCENT_WARNING : new Color(60, 60, 80);
                
                DrawPanel(sb, pixel, catX, tabY, tabW, tabH, bgColor, borderColor, 2);
                
                Vector2 catTextSize = font.MeasureString(categories[catIndex]) * 0.75f;
                Vector2 catTextPos = new Vector2(catX + tabW / 2 - catTextSize.X / 2, tabY + tabH / 2 - catTextSize.Y / 2);
                
                Color textColor = selected ? ACCENT_WARNING : TEXT_SECONDARY;
                sb.DrawString(font, categories[catIndex], catTextPos, textColor * _fadeAlpha, 0f, Vector2.Zero, 0.75f, SpriteEffects.None, 0f);
            }
            
            // Tab scroll indicators
            if (tabScrollOffset > 0)
            {
                DrawCenteredText(sb, font, "<<<", tabY + 15, ACCENT_PRIMARY * 0.7f * _fadeAlpha);
            }
            if (tabScrollOffset + maxVisibleTabs < categories.Length)
            {
                DrawCenteredText(sb, font, ">>>", tabY + 15, ACCENT_PRIMARY * 0.7f * _fadeAlpha);
            }
            
            // Settings panel
            int panelW = Math.Min(950, screenWidth - 100);
            int panelH = screenHeight - 350;
            int panelX = screenWidth / 2 - panelW / 2;
            int panelY = tabY + tabH + 30;
            
            DrawPanel(sb, pixel, panelX, panelY, panelW, panelH, BG_MEDIUM, ACCENT_PRIMARY, 3);
            
            // Draw settings with scrolling support
            DrawSettingsCategory(sb, font, pixel, panelX, panelY, panelW, panelH, settings);
            
            // SAVE button at bottom of panel
            int saveButtonW = 200;
            int saveButtonH = 50;
            int saveButtonX = screenWidth / 2 - saveButtonW / 2;
            int saveButtonY = panelY + panelH + 10;
            
            bool saveHovered = _hoveredButton == 999; // Special index for save button
            Color saveColor = saveHovered ? ACCENT_SUCCESS : new Color(40, 180, 100);
            
            DrawPanel(sb, pixel, saveButtonX, saveButtonY, saveButtonW, saveButtonH, saveColor, Color.White, 3);
            
            Vector2 saveTextSize = font.MeasureString("SAVE");
            Vector2 saveTextPos = new Vector2(saveButtonX + saveButtonW / 2 - saveTextSize.X / 2, saveButtonY + saveButtonH / 2 - saveTextSize.Y / 2);
            sb.DrawString(font, "SAVE", saveTextPos, Color.White * _fadeAlpha);
            
            // Register save button as clickable
            _clickableAreas.Add(new Rectangle(saveButtonX, saveButtonY, saveButtonW, saveButtonH));
            
            // Help text at bottom
            int helpY = saveButtonY + saveButtonH + 10;
            DrawCenteredText(sb, font, "LEFT/RIGHT: Change Category  |  UP/DOWN: Navigate  |  A/D or ARROWS: Adjust  |  ENTER: Toggle", helpY, TEXT_DISABLED * _fadeAlpha);
            DrawCenteredText(sb, font, "R: Reset to Defaults  |  ESC: Save & Back", helpY + 25, TEXT_DISABLED * _fadeAlpha);
        }
        
        private void DrawSettingsCategory(SpriteBatch sb, SpriteFont font, Texture2D pixel, int panelX, int panelY, int panelW, int panelH, MultiplayerSettings settings)
        {
            int settingY = panelY + 20;
            int settingH = 65;
            int settingMargin = 8;
            int totalSettingH = settingH + settingMargin;
            
            // Clear arrow areas for this frame
            _leftArrowAreas.Clear();
            _rightArrowAreas.Clear();
            
            // Get settings for current category
            var categorySettings = GetCategorySettings(_settingsCategory, settings);
            
            // Calculate visible range with scrolling
            int startIdx = _settingsScrollOffset;
            int endIdx = Math.Min(startIdx + MAX_SETTINGS_VISIBLE, categorySettings.Count);
            
            // Draw each visible setting
            for (int i = startIdx; i < endIdx; i++)
            {
                var setting = categorySettings[i];
                bool selected = i == _settingsSelection;
                int displayIndex = i - startIdx;
                int currentY = settingY + displayIndex * totalSettingH;
                
                // Skip if out of panel bounds
                if (currentY + settingH > panelY + panelH - 20)
                    break;
                
                // Register as clickable area
                _clickableAreas.Add(new Rectangle(panelX + 30, currentY, panelW - 60, settingH));
                
                // Register left and right arrow areas (for mouse clicks to modify values)
                int arrowSize = 30;
                _leftArrowAreas.Add(new Rectangle(panelX + 30, currentY, arrowSize, settingH)); // Left arrow
                _rightArrowAreas.Add(new Rectangle(panelX + panelW - 30 - arrowSize, currentY, arrowSize, settingH)); // Right arrow
                
                DrawSettingItem(sb, font, pixel, panelX + 30, currentY, panelW - 60, settingH, 
                    setting.Label, setting.Value, i, setting.IsToggle, selected);
                
                // Draw special visualizations
                if (setting.Label == "Player Color")
                {
                    int colorBoxSize = 35;
                    int colorBoxX = panelX + panelW - 70;
                    int colorBoxY = currentY + settingH / 2 - colorBoxSize / 2;
                    sb.Draw(pixel, new Rectangle(colorBoxX, colorBoxY, colorBoxSize, colorBoxSize), settings.GetPlayerColor() * _fadeAlpha);
                }
            }
            
            // Scroll indicators
            if (_settingsScrollOffset > 0)
            {
                DrawCenteredText(sb, font, "^^^ MORE ^^^", panelY + 5, ACCENT_PRIMARY * 0.7f * _fadeAlpha);
            }
            if (endIdx < categorySettings.Count)
            {
                DrawCenteredText(sb, font, "vvv MORE vvv", panelY + panelH - 15, ACCENT_PRIMARY * 0.7f * _fadeAlpha);
            }
        }
        
        private List<SettingInfo> GetCategorySettings(int category, MultiplayerSettings settings)
        {
            var list = new List<SettingInfo>();
            
            switch (category)
            {
                case 0: // User
                    string usernameValue = _isEditingText && _settingsSelection == 0 ? _editingTextValue + "_" : settings.Username;
                    list.Add(new SettingInfo { Label = "Username", Value = usernameValue, IsToggle = false });
                    list.Add(new SettingInfo { Label = "Avatar", Value = settings.GetAvatarIcon(), IsToggle = false });
                    list.Add(new SettingInfo { Label = "Player Color", Value = $"Hue: {settings.PlayerColorHue} deg", IsToggle = false });
                    break;
                    
                case 1: // Gameplay
                    list.Add(new SettingInfo { Label = "Show Opponent Notes", Value = settings.ShowOpponentNotes ? "ON" : "OFF", IsToggle = true });
                    list.Add(new SettingInfo { Label = "Show Opponent Score", Value = settings.ShowOpponentScore ? "ON" : "OFF", IsToggle = true });
                    list.Add(new SettingInfo { Label = "Show Connection Quality", Value = settings.ShowConnectionQuality ? "ON" : "OFF", IsToggle = true });
                    list.Add(new SettingInfo { Label = "Ghost Transparency", Value = $"{settings.GhostTransparency}%", IsToggle = false });
                    break;
                    
                case 2: // Notifications
                    list.Add(new SettingInfo { Label = "Match Found Sound", Value = settings.MatchFoundSound ? "ON" : "OFF", IsToggle = true });
                    list.Add(new SettingInfo { Label = "Chat Notifications", Value = settings.ChatNotifications ? "ON" : "OFF", IsToggle = true });
                    list.Add(new SettingInfo { Label = "Ready Alerts", Value = settings.ReadyAlerts ? "ON" : "OFF", IsToggle = true });
                    break;
                    
                case 3: // Privacy
                    list.Add(new SettingInfo { Label = "Allow Match Invites", Value = settings.AllowMatchInvites ? "ON" : "OFF", IsToggle = true });
                    list.Add(new SettingInfo { Label = "Show Online Status", Value = settings.ShowOnlineStatus ? "ON" : "OFF", IsToggle = true });
                    list.Add(new SettingInfo { Label = "Anonymous Mode", Value = settings.AnonymousMode ? "ON" : "OFF", IsToggle = true });
                    break;
                    
                case 4: // Performance
                    list.Add(new SettingInfo { Label = "Network Update Rate", Value = $"{(int)settings.UpdateRate} Hz", IsToggle = false });
                    list.Add(new SettingInfo { Label = "Enable Interpolation", Value = settings.EnableInterpolation ? "ON" : "OFF", IsToggle = true });
                    string bandwidthText = settings.BandwidthLimitKbps == 0 ? "Unlimited" : $"{settings.BandwidthLimitKbps} Kbps";
                    list.Add(new SettingInfo { Label = "Bandwidth Limit", Value = bandwidthText, IsToggle = false });
                    break;
                    
                case 5: // Visual
                    list.Add(new SettingInfo { Label = "HUD Opacity", Value = $"{settings.HudOpacity}%", IsToggle = false });
                    list.Add(new SettingInfo { Label = "Connection Indicator", Value = settings.ConnectionIndicatorPosition.ToString(), IsToggle = false });
                    list.Add(new SettingInfo { Label = "Interface Mode", Value = settings.InterfaceMode.ToString(), IsToggle = false });
                    break;
            }
            
            return list;
        }
        
        private class SettingInfo
        {
            public string Label { get; set; } = "";
            public string Value { get; set; } = "";
            public bool IsToggle { get; set; }
        }
        
        private void DrawSettingItem(SpriteBatch sb, SpriteFont font, Texture2D pixel, int x, int y, int w, int h, string label, string value, int index, bool isToggle, bool selected)
        {
            Color bgColor = selected ? BG_LIGHT : BG_DARK;
            Color borderColor = selected ? ACCENT_PRIMARY : new Color(40, 40, 60);
            
            DrawPanel(sb, pixel, x, y, w, h, bgColor, borderColor, 2);
            
            // Sanitize text to prevent crashes with unsupported characters
            label = SanitizeText(font, label);
            value = SanitizeText(font, value);
            
            // Label (smaller font for better spacing)
            float labelScale = 0.85f;
            Vector2 labelSize = font.MeasureString(label) * labelScale;
            Vector2 labelPos = new Vector2(x + 20, y + h / 2 - labelSize.Y / 2);
            sb.DrawString(font, label, labelPos, TEXT_PRIMARY * _fadeAlpha, 0f, Vector2.Zero, labelScale, SpriteEffects.None, 0f);
            
            // Value
            Color valueColor = selected ? ACCENT_SECONDARY : TEXT_SECONDARY;
            if (isToggle)
            {
                valueColor = value == "ON" ? ACCENT_SUCCESS : ACCENT_ERROR;
            }
            
            float valueScale = 0.9f;
            Vector2 valueSize = font.MeasureString(value) * valueScale;
            Vector2 valuePos = new Vector2(x + w - valueSize.X - 20, y + h / 2 - valueSize.Y / 2);
            sb.DrawString(font, value, valuePos, valueColor * _fadeAlpha, 0f, Vector2.Zero, valueScale, SpriteEffects.None, 0f);
            
            // Selection arrows with pulse animation
            if (selected)
            {
                string arrow = ">";
                float arrowScale = 0.8f;
                Vector2 arrowSize = font.MeasureString(arrow) * arrowScale;
                Vector2 leftArrow = new Vector2(x + 5, y + h / 2 - arrowSize.Y / 2);
                Vector2 rightArrow = new Vector2(x + w - arrowSize.X - 5, y + h / 2 - arrowSize.Y / 2);
                
                float pulse = (float)Math.Sin(_pulseTimer * 3) * 0.3f + 0.7f;
                sb.DrawString(font, arrow, leftArrow, ACCENT_PRIMARY * pulse * _fadeAlpha, 0f, Vector2.Zero, arrowScale, SpriteEffects.None, 0f);
                sb.DrawString(font, "<", rightArrow, ACCENT_PRIMARY * pulse * _fadeAlpha, 0f, Vector2.Zero, arrowScale, SpriteEffects.None, 0f);
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
            
            // Switch tabs with LEFT/RIGHT
            if (kb.IsKeyDown(Keys.Left) && !_prevKb.IsKeyDown(Keys.Left))
            {
                _leaderboardTab = (_leaderboardTab - 1 + 3) % 3;
                _leaderboardSelection = 0;
                _leaderboardScrollOffset = 0;
                _inputCooldown = INPUT_COOLDOWN_TIME;
            }
            if (kb.IsKeyDown(Keys.Right) && !_prevKb.IsKeyDown(Keys.Right))
            {
                _leaderboardTab = (_leaderboardTab + 1) % 3;
                _leaderboardSelection = 0;
                _leaderboardScrollOffset = 0;
                _inputCooldown = INPUT_COOLDOWN_TIME;
            }
            
            // Navigate items with UP/DOWN
            int itemCount = GetLeaderboardItemCount();
            if (itemCount > 0)
            {
                if (kb.IsKeyDown(Keys.Down) && !_prevKb.IsKeyDown(Keys.Down))
                {
                    _leaderboardSelection = (_leaderboardSelection + 1) % itemCount;
                    
                    // Adjust scroll
                    if (_leaderboardSelection >= _leaderboardScrollOffset + MAX_LEADERBOARD_VISIBLE)
                    {
                        _leaderboardScrollOffset = _leaderboardSelection - MAX_LEADERBOARD_VISIBLE + 1;
                    }
                    else if (_leaderboardSelection < _leaderboardScrollOffset)
                    {
                        _leaderboardScrollOffset = _leaderboardSelection;
                    }
                    
                    _inputCooldown = INPUT_COOLDOWN_TIME;
                }
                if (kb.IsKeyDown(Keys.Up) && !_prevKb.IsKeyDown(Keys.Up))
                {
                    _leaderboardSelection = (_leaderboardSelection - 1 + itemCount) % itemCount;
                    
                    // Adjust scroll
                    if (_leaderboardSelection < _leaderboardScrollOffset)
                    {
                        _leaderboardScrollOffset = _leaderboardSelection;
                    }
                    else if (_leaderboardSelection >= _leaderboardScrollOffset + MAX_LEADERBOARD_VISIBLE)
                    {
                        _leaderboardScrollOffset = _leaderboardSelection - MAX_LEADERBOARD_VISIBLE + 1;
                    }
                    
                    _inputCooldown = INPUT_COOLDOWN_TIME;
                }
            }
            
            // ESC to go back
            if (kb.IsKeyDown(Keys.Escape) && !_prevKb.IsKeyDown(Keys.Escape))
            {
                _state = MenuState.MainMenu;
            }
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
            // Clear clickable areas
            _clickableAreas.Clear();
            
            // Title
            string title = "LEADERBOARD & STATS";
            Vector2 titleSize = font.MeasureString(title);
            Vector2 titlePos = new Vector2(screenWidth / 2 - titleSize.X / 2, 60);
            sb.DrawString(font, title, titlePos + new Vector2(2, 2), Color.Black * 0.5f);
            sb.DrawString(font, title, titlePos, ACCENT_PRIMARY * _fadeAlpha);
            
            // Tabs
            string[] tabs = { "TOP PLAYERS", "RECENT MATCHES", "MY STATS" };
            int tabW = 220;
            int tabH = 50;
            int tabSpacing = 15;
            int totalTabsW = (tabW * 3) + (tabSpacing * 2);
            int tabStartX = screenWidth / 2 - totalTabsW / 2;
            int tabY = 130;
            
            for (int i = 0; i < tabs.Length; i++)
            {
                bool selected = i == _leaderboardTab;
                int tabX = tabStartX + i * (tabW + tabSpacing);
                
                Color bgColor = selected ? BG_LIGHT : BG_MEDIUM;
                Color borderColor = selected ? ACCENT_PRIMARY : new Color(60, 60, 80);
                
                DrawPanel(sb, pixel, tabX, tabY, tabW, tabH, bgColor, borderColor, 2);
                
                Vector2 tabTextSize = font.MeasureString(tabs[i]);
                Vector2 tabTextPos = new Vector2(tabX + tabW / 2 - tabTextSize.X / 2, tabY + tabH / 2 - tabTextSize.Y / 2);
                
                Color textColor = selected ? ACCENT_PRIMARY : TEXT_SECONDARY;
                sb.DrawString(font, tabs[i], tabTextPos, textColor * _fadeAlpha);
            }
            
            // Content panel
            int panelW = 950;
            int panelH = 520;
            int panelX = screenWidth / 2 - panelW / 2;
            int panelY = tabY + tabH + 30;
            
            DrawPanel(sb, pixel, panelX, panelY, panelW, panelH, BG_MEDIUM, ACCENT_PRIMARY, 3);
            
            // Draw content based on tab
            switch (_leaderboardTab)
            {
                case 0:
                    DrawTopPlayersTab(sb, font, pixel, panelX, panelY, panelW, panelH);
                    break;
                case 1:
                    DrawRecentMatchesTab(sb, font, pixel, panelX, panelY, panelW, panelH);
                    break;
                case 2:
                    DrawMyStatsTab(sb, font, pixel, panelX, panelY, panelW, panelH);
                    break;
            }
            
            // Footer
            int footerY = panelY + panelH + 20;
            DrawCenteredText(sb, font, "LEFT/RIGHT: Change Tab  |  UP/DOWN: Scroll  |  ESC: Back", footerY, TEXT_DISABLED * _fadeAlpha);
        }
        
        private void DrawTopPlayersTab(SpriteBatch sb, SpriteFont font, Texture2D pixel, int panelX, int panelY, int panelW, int panelH)
        {
            var topPlayers = LeaderboardManager.Instance.GetTopPlayers(50);
            
            if (topPlayers.Count == 0)
            {
                DrawCenteredText(sb, font, "No leaderboard data yet", panelY + panelH / 2, TEXT_SECONDARY * _fadeAlpha);
                DrawCenteredText(sb, font, "Play some matches to appear here!", panelY + panelH / 2 + 30, TEXT_DISABLED * _fadeAlpha);
                return;
            }
            
            // Header
            int headerY = panelY + 20;
            int col1X = panelX + 40;
            int col2X = panelX + 100;
            int col3X = panelX + 400;
            int col4X = panelX + 550;
            int col5X = panelX + 680;
            int col6X = panelX + 800;
            
            sb.DrawString(font, "RANK", new Vector2(col1X, headerY), ACCENT_WARNING * _fadeAlpha);
            sb.DrawString(font, "PLAYER", new Vector2(col2X, headerY), ACCENT_WARNING * _fadeAlpha);
            sb.DrawString(font, "ELO", new Vector2(col3X, headerY), ACCENT_WARNING * _fadeAlpha);
            sb.DrawString(font, "W/L", new Vector2(col4X, headerY), ACCENT_WARNING * _fadeAlpha);
            sb.DrawString(font, "WIN%", new Vector2(col5X, headerY), ACCENT_WARNING * _fadeAlpha);
            sb.DrawString(font, "SCORE", new Vector2(col6X, headerY), ACCENT_WARNING * _fadeAlpha);
            
            // Draw line
            sb.Draw(pixel, new Rectangle(panelX + 20, headerY + 25, panelW - 40, 2), ACCENT_PRIMARY * 0.3f * _fadeAlpha);
            
            // List items
            int itemY = headerY + 40;
            int itemH = 50;
            
            int startIdx = _leaderboardScrollOffset;
            int endIdx = Math.Min(startIdx + MAX_LEADERBOARD_VISIBLE, topPlayers.Count);
            
            for (int i = startIdx; i < endIdx; i++)
            {
                var player = topPlayers[i];
                bool selected = i == _leaderboardSelection;
                int displayIdx = i - startIdx;
                int currentY = itemY + displayIdx * itemH;
                
                // Background
                if (selected)
                {
                    sb.Draw(pixel, new Rectangle(panelX + 20, currentY - 5, panelW - 40, itemH - 5), BG_LIGHT * _fadeAlpha);
                }
                
                // Rank with medal for top 3
                string rankText = $"#{player.Rank}";
                Color rankColor = player.Rank switch
                {
                    1 => new Color(255, 215, 0),   // Gold
                    2 => new Color(192, 192, 192), // Silver
                    3 => new Color(205, 127, 50),  // Bronze
                    _ => TEXT_PRIMARY
                };
                sb.DrawString(font, rankText, new Vector2(col1X, currentY), rankColor * _fadeAlpha);
                
                // Avatar and player name
                string avatar = new[] { "@", "#", "$", "%", "&", "*", "+", "=", "~", "^" }[player.AvatarIndex % 10];
                sb.DrawString(font, avatar, new Vector2(col2X, currentY), GetColorFromHue(player.ColorHue) * _fadeAlpha);
                sb.DrawString(font, SanitizeText(font, player.Username), new Vector2(col2X + 25, currentY), TEXT_PRIMARY * _fadeAlpha);
                
                // ELO
                sb.DrawString(font, player.EloRating.ToString(), new Vector2(col3X, currentY), ACCENT_SECONDARY * _fadeAlpha);
                
                // W/L
                string wlText = $"{player.Wins}/{player.Losses}";
                sb.DrawString(font, wlText, new Vector2(col4X, currentY), TEXT_SECONDARY * _fadeAlpha);
                
                // Win%
                string winRateText = $"{player.WinRate:F1}%";
                Color winRateColor = player.WinRate >= 60 ? ACCENT_SUCCESS : player.WinRate >= 40 ? ACCENT_WARNING : ACCENT_ERROR;
                sb.DrawString(font, winRateText, new Vector2(col5X, currentY), winRateColor * _fadeAlpha);
                
                // Total score (formatted)
                string scoreText = FormatScore(player.TotalScore);
                sb.DrawString(font, scoreText, new Vector2(col6X, currentY), TEXT_SECONDARY * _fadeAlpha);
            }
            
            // Scroll indicators
            if (_leaderboardScrollOffset > 0)
            {
                DrawCenteredText(sb, font, "^^^ MORE ^^^", panelY + panelH - 30, ACCENT_PRIMARY * 0.7f * _fadeAlpha);
            }
            if (endIdx < topPlayers.Count)
            {
                DrawCenteredText(sb, font, "vvv MORE vvv", panelY + panelH - 10, ACCENT_PRIMARY * 0.7f * _fadeAlpha);
            }
        }
        
        private void DrawRecentMatchesTab(SpriteBatch sb, SpriteFont font, Texture2D pixel, int panelX, int panelY, int panelW, int panelH)
        {
            var recentMatches = LeaderboardManager.Instance.GetRecentMatches(50);
            
            if (recentMatches.Count == 0)
            {
                DrawCenteredText(sb, font, "No match history yet", panelY + panelH / 2, TEXT_SECONDARY * _fadeAlpha);
                DrawCenteredText(sb, font, "Play your first multiplayer match!", panelY + panelH / 2 + 30, TEXT_DISABLED * _fadeAlpha);
                return;
            }
            
            // List items
            int itemY = panelY + 30;
            int itemH = 60;
            
            int startIdx = _leaderboardScrollOffset;
            int endIdx = Math.Min(startIdx + MAX_LEADERBOARD_VISIBLE, recentMatches.Count);
            
            for (int i = startIdx; i < endIdx; i++)
            {
                var match = recentMatches[i];
                bool selected = i == _leaderboardSelection;
                int displayIdx = i - startIdx;
                int currentY = itemY + displayIdx * itemH;
                
                // Card background
                Color cardBg = selected ? BG_LIGHT : BG_DARK;
                DrawPanel(sb, pixel, panelX + 20, currentY, panelW - 40, itemH - 5, cardBg, ACCENT_PRIMARY * 0.5f, 1);
                
                // Match info
                int infoX = panelX + 40;
                
                // Level name
                string levelText = SanitizeText(font, match.LevelName);
                sb.DrawString(font, levelText, new Vector2(infoX, currentY + 5), ACCENT_PRIMARY * _fadeAlpha);
                
                // Time ago
                sb.DrawString(font, match.TimeAgo, new Vector2(panelX + panelW - 150, currentY + 5), TEXT_DISABLED * _fadeAlpha);
                
                // Player 1 vs Player 2
                int vsY = currentY + 30;
                string player1Text = $"{match.Player1.Username} ({match.Player1.Score})";
                string vsText = " VS ";
                string player2Text = $"{match.Player2.Username} ({match.Player2.Score})";
                
                Color p1Color = match.Winner == 1 ? ACCENT_SUCCESS : TEXT_SECONDARY;
                Color p2Color = match.Winner == 2 ? ACCENT_SUCCESS : TEXT_SECONDARY;
                
                sb.DrawString(font, SanitizeText(font, player1Text), new Vector2(infoX, vsY), p1Color * _fadeAlpha);
                sb.DrawString(font, vsText, new Vector2(infoX + 200, vsY), TEXT_DISABLED * _fadeAlpha);
                sb.DrawString(font, SanitizeText(font, player2Text), new Vector2(infoX + 250, vsY), p2Color * _fadeAlpha);
                
                // Winner indicator
                if (match.Winner > 0)
                {
                    string winnerText = match.Winner == 1 ? "WIN" : "WIN";
                    int winX = match.Winner == 1 ? infoX + 150 : infoX + 450;
                    sb.DrawString(font, winnerText, new Vector2(winX, vsY), ACCENT_SUCCESS * _fadeAlpha);
                }
                else
                {
                    sb.DrawString(font, "DRAW", new Vector2(infoX + 300, vsY), ACCENT_WARNING * _fadeAlpha);
                }
            }
            
            // Scroll indicators
            if (_leaderboardScrollOffset > 0)
            {
                DrawCenteredText(sb, font, "^^^ MORE ^^^", panelY + 10, ACCENT_PRIMARY * 0.7f * _fadeAlpha);
            }
            if (endIdx < recentMatches.Count)
            {
                DrawCenteredText(sb, font, "vvv MORE vvv", panelY + panelH - 20, ACCENT_PRIMARY * 0.7f * _fadeAlpha);
            }
        }
        
        private void DrawMyStatsTab(SpriteBatch sb, SpriteFont font, Texture2D pixel, int panelX, int panelY, int panelW, int panelH)
        {
            var settings = MultiplayerSettings.Instance;
            var myStats = LeaderboardManager.Instance.GetPlayerStats(settings.Username);
            
            if (myStats == null)
            {
                DrawCenteredText(sb, font, "No personal stats yet", panelY + panelH / 2, TEXT_SECONDARY * _fadeAlpha);
                DrawCenteredText(sb, font, "Play matches to see your statistics here!", panelY + panelH / 2 + 30, TEXT_DISABLED * _fadeAlpha);
                return;
            }
            
            // Personal info section
            int sectionY = panelY + 30;
            int col1X = panelX + 50;
            int col2X = panelX + panelW / 2 + 50;
            
            // Avatar and name
            string avatar = settings.GetAvatarIcon();
            sb.DrawString(font, avatar, new Vector2(col1X, sectionY), settings.GetPlayerColor() * _fadeAlpha);
            sb.DrawString(font, settings.Username, new Vector2(col1X + 40, sectionY), ACCENT_PRIMARY * _fadeAlpha);
            
            sectionY += 60;
            
            // Stats grid
            DrawStatItem(sb, font, pixel, col1X, sectionY, "RANK", $"#{myStats.Rank}", ACCENT_WARNING);
            DrawStatItem(sb, font, pixel, col2X, sectionY, "ELO RATING", myStats.EloRating.ToString(), ACCENT_SECONDARY);
            
            sectionY += 80;
            DrawStatItem(sb, font, pixel, col1X, sectionY, "TOTAL MATCHES", myStats.TotalMatches.ToString(), TEXT_PRIMARY);
            DrawStatItem(sb, font, pixel, col2X, sectionY, "WIN RATE", $"{myStats.WinRate:F1}%", ACCENT_SUCCESS);
            
            sectionY += 80;
            DrawStatItem(sb, font, pixel, col1X, sectionY, "WINS", myStats.Wins.ToString(), ACCENT_SUCCESS);
            DrawStatItem(sb, font, pixel, col2X, sectionY, "LOSSES", myStats.Losses.ToString(), ACCENT_ERROR);
            
            sectionY += 80;
            DrawStatItem(sb, font, pixel, col1X, sectionY, "HIGHEST SCORE", FormatScore(myStats.HighestScore), ACCENT_PRIMARY);
            DrawStatItem(sb, font, pixel, col2X, sectionY, "BEST COMBO", myStats.HighestCombo.ToString(), ACCENT_SECONDARY);
        }
        
        private void DrawStatItem(SpriteBatch sb, SpriteFont font, Texture2D pixel, int x, int y, string label, string value, Color valueColor)
        {
            sb.DrawString(font, label, new Vector2(x, y), TEXT_DISABLED * _fadeAlpha);
            sb.DrawString(font, value, new Vector2(x, y + 30), valueColor * _fadeAlpha);
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
