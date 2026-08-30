using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Microsoft.Xna.Framework;
using Microsoft.AspNetCore.SignalR.Client;

namespace FunkyMakerPlayer.Multiplayer
{
    /// <summary>
    /// Network connection quality indicator.
    /// </summary>
    public enum ConnectionQuality
    {
        Unknown,    // Not yet determined
        Excellent,  // < 50ms ping, stable
        Good,       // 50-100ms ping, stable
        Fair,       // 100-200ms ping or some packet loss
        Poor,       // > 200ms ping or significant packet loss
        Critical    // > 500ms ping or severe issues
    }

    /// <summary>
    /// Manages network connections and communication with the multiplayer server.
    /// Singleton pattern for global access.
    /// Uses SignalR for real-time communication (websockets).
    /// </summary>
    public class NetworkManager
    {
        private static NetworkManager? _instance;
        public static NetworkManager Instance => _instance ??= new NetworkManager();

        // Connection state
        public bool IsConnected { get; private set; }
        public bool IsConnecting { get; private set; }
        public string Username { get; private set; } = "";
        public string OpponentName { get; private set; } = "";  // Current opponent's name
        public int PlayerRole { get; private set; } = 1;  // 1 = Player 1, 2 = Player 2
        public int Ping { get; private set; }
        public ConnectionQuality Quality { get; private set; } = ConnectionQuality.Unknown;

        // Server configuration
        private string _serverUrl = "https://funkymakerserver.onrender.com";
        private string _signalREndpoint = "/multiplayer";
        
        public string ServerUrl
        {
            get => _serverUrl;
            set => _serverUrl = value;
        }
        
        public string SignalRUrl => $"{_serverUrl}{_signalREndpoint}";

        // SignalR connection
        private HubConnection? _hubConnection;

        // Events
        public event Action? OnConnected;
        public event Action<string>? OnDisconnected;
        public event Action<string>? OnConnectionError; // Specific error messages
        public event Action<ConnectionQuality>? OnQualityChanged; // Network quality changed
        public event Action<string, string, int>? OnMatchFound; // opponentName, roomId, playerRole
        public event Action<bool>? OnOpponentReadyChanged; // opponent ready state changed
        public event Action<PlayerProfilePacket>? OnOpponentProfileReceived; // opponent profile (username, avatar, color)
        public event Action<SettingsSyncPacket>? OnOpponentSettingsReceived; // opponent settings
        public event Action<long>? OnCountdownStart; // countdownStartTime from server
        public event Action<long, int>? OnGameStart; // startTimestamp, playerRole (1=Player1/chart0, 2=Player2/chart1)
        public event Action<int>? OnPingUpdate;
        public event Action<OpponentHitPacket>? OnOpponentHit;
        public event Action<OpponentStatePacket>? OnOpponentState;
        public event Action<GameEndPacket>? OnGameEnd;
        public event Action<LevelListPacket>? OnLevelListReceived;
        public event Action<LeaderboardResponsePacket>? OnLeaderboardReceived; // v0.2.4 - Global leaderboard
        public event Action<MatchHistoryResponsePacket>? OnMatchHistoryReceived; // v0.2.4 - Match history

        // Ping measurement & quality tracking
        private long _lastPingTime;
        private float _pingTimer;
        private const float PING_INTERVAL = 2f; // seconds
        private List<int> _pingHistory = new List<int>();
        private const int PING_HISTORY_SIZE = 10;
        private float _lastPacketReceivedTime;
        private const float CONNECTION_TIMEOUT = 15f; // Consider connection lost after 15s of no packets

        // Reconnection with exponential backoff
        private bool _shouldReconnect = false;
        private float _reconnectTimer = 0f;
        private int _reconnectAttempts = 0;
        private const int MAX_RECONNECT_ATTEMPTS = 5;
        private float _currentReconnectDelay = 2f; // Start with 2 seconds
        private const float MIN_RECONNECT_DELAY = 2f;
        private const float MAX_RECONNECT_DELAY = 30f;
        
        // Connection timeout
        private float _connectionTimeoutTimer = 0f;
        private const float CONNECTION_ATTEMPT_TIMEOUT = 10f; // Timeout for initial connection

        private NetworkManager()
        {
            IsConnected = false;
            IsConnecting = false;
        }

        /// <summary>
        /// Connect to the multiplayer server with improved error handling.
        /// </summary>
        public async void Connect(string username)
        {
            if (IsConnected || IsConnecting)
            {
                Console.WriteLine("[Multiplayer] Already connected or connecting");
                return;
            }

            Username = username;
            IsConnecting = true;
            _connectionTimeoutTimer = 0f;

            Console.WriteLine($"[Multiplayer] Connecting to {_serverUrl} as {username}...");
            Console.WriteLine($"[Multiplayer] Attempt {_reconnectAttempts + 1}/{MAX_RECONNECT_ATTEMPTS}");

            try
            {
                // Build SignalR connection with timeout and retry policies
                _hubConnection = new HubConnectionBuilder()
                    .WithUrl(SignalRUrl)
                    .WithAutomaticReconnect(new[] { 
                        TimeSpan.FromSeconds(0),    // First retry: immediate
                        TimeSpan.FromSeconds(2),    // Second retry: 2s
                        TimeSpan.FromSeconds(5),    // Third retry: 5s
                        TimeSpan.FromSeconds(10)    // Fourth+ retry: 10s
                    })
                    .Build();
                
                // Register disconnection handler
                _hubConnection.Closed += async (error) =>
                {
                    IsConnected = false;
                    IsConnecting = false;
                    string reason = error?.Message ?? "Connection closed";
                    Console.WriteLine($"[Multiplayer] Connection closed: {reason}");
                    OnDisconnected?.Invoke(reason);
                    
                    // Trigger reconnection if enabled
                    if (_shouldReconnect && _reconnectAttempts < MAX_RECONNECT_ATTEMPTS)
                    {
                        Console.WriteLine($"[Multiplayer] Will attempt reconnection in {_currentReconnectDelay:F1}s");
                        _reconnectTimer = _currentReconnectDelay;
                        _reconnectAttempts++;
                        
                        // Exponential backoff
                        _currentReconnectDelay = Math.Min(_currentReconnectDelay * 1.5f, MAX_RECONNECT_DELAY);
                    }
                    
                    await Task.CompletedTask;
                };
                
                // Register reconnecting handler
                _hubConnection.Reconnecting += (error) =>
                {
                    Console.WriteLine($"[Multiplayer] Reconnecting... {error?.Message ?? ""}");
                    IsConnected = false;
                    IsConnecting = true;
                    return Task.CompletedTask;
                };
                
                // Register reconnected handler
                _hubConnection.Reconnected += (connectionId) =>
                {
                    Console.WriteLine($"[Multiplayer] Reconnected successfully! ConnectionId: {connectionId}");
                    IsConnecting = false;
                    IsConnected = true;
                    _reconnectAttempts = 0;
                    _currentReconnectDelay = MIN_RECONNECT_DELAY;
                    OnConnected?.Invoke();
                    return Task.CompletedTask;
                };
                
                // Register server event handlers
                _hubConnection.On<MatchFoundPacket>("MatchFound", OnMatchFoundPacket);
                _hubConnection.On<ReadyStatePacket>("OpponentReady", OnOpponentReadyPacket);
                _hubConnection.On<PlayerProfilePacket>("OpponentProfile", OnOpponentProfilePacket);
                _hubConnection.On<SettingsSyncPacket>("OpponentSettings", OnOpponentSettingsPacket);
                _hubConnection.On<CountdownStartPacket>("CountdownStart", OnCountdownStartPacket);
                _hubConnection.On<OpponentHitPacket>("OpponentHit", OnOpponentHitPacket);
                _hubConnection.On<OpponentStatePacket>("OpponentState", OnOpponentStatePacket);
                _hubConnection.On<GameStartPacket>("GameStart", OnGameStartPacket);
                _hubConnection.On<GameEndPacket>("GameEnd", OnGameEndPacket);
                _hubConnection.On<PongPacket>("Pong", OnPongPacket);
                _hubConnection.On<LeaderboardResponsePacket>("LeaderboardResponse", OnLeaderboardResponse);
                _hubConnection.On<MatchHistoryResponsePacket>("MatchHistoryResponse", OnMatchHistoryResponse);
                
                Console.WriteLine($"[Multiplayer] Starting SignalR connection: {SignalRUrl}");
                await _hubConnection.StartAsync();
                
                IsConnecting = false;
                IsConnected = true;
                _reconnectAttempts = 0; // Reset on successful connection
                _currentReconnectDelay = MIN_RECONNECT_DELAY;
                _lastPacketReceivedTime = 0f;
                
                OnConnected?.Invoke();
                
                Console.WriteLine("[Multiplayer] [OK] Connected successfully to SignalR hub");
                
                // Send player profile and settings after connecting
                SendPlayerProfile();
                SendSettingsSync();
            }
            catch (System.Net.Http.HttpRequestException ex)
            {
                Console.WriteLine($"[Multiplayer] [X] Network error: {ex.Message}");
                IsConnecting = false;
                IsConnected = false;
                OnConnectionError?.Invoke("Network error. Check your internet connection.");
                OnDisconnected?.Invoke($"Network error: {ex.Message}");
            }
            catch (TimeoutException ex)
            {
                Console.WriteLine($"[Multiplayer] [X] Connection timeout: {ex.Message}");
                IsConnecting = false;
                IsConnected = false;
                OnConnectionError?.Invoke("Connection timeout. Server may be down.");
                OnDisconnected?.Invoke($"Timeout: {ex.Message}");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[Multiplayer] [X] Connection failed: {ex.Message}");
                Console.WriteLine($"[Multiplayer] Exception type: {ex.GetType().Name}");
                IsConnecting = false;
                IsConnected = false;
                OnConnectionError?.Invoke($"Connection failed: {ex.Message}");
                OnDisconnected?.Invoke($"Connection failed: {ex.Message}");
            }
        }

        /// <summary>
        /// Disconnect from the server.
        /// </summary>
        public async void Disconnect()
        {
            if (!IsConnected && !IsConnecting)
                return;

            Console.WriteLine("[Multiplayer] Disconnecting...");

            try
            {
                // Send disconnect packet
                var packet = new DisconnectPacket
                {
                    Reason = "User disconnected",
                    Timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()
                };

                // Stop SignalR connection
                if (_hubConnection != null)
                {
                    await _hubConnection.StopAsync();
                    await _hubConnection.DisposeAsync();
                    _hubConnection = null;
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[Multiplayer] Disconnect error: {ex.Message}");
            }
            finally
            {
                IsConnected = false;
                IsConnecting = false;
                
                // Reset time synchronization
                TimeSyncManager.Instance.Reset();
                
                OnDisconnected?.Invoke("User disconnected");
            }
        }

        /// <summary>
        /// Find a match with specified level.
        /// </summary>
        public async void FindMatch(string levelHash, string levelName = "Unknown", LevelSource source = LevelSource.Local, string levelPath = "")
        {
            if (!IsConnected)
            {
                Console.WriteLine("[Multiplayer] Not connected to server");
                return;
            }

            // Calculate hash if not provided and levelPath is given
            if (string.IsNullOrEmpty(levelHash) && !string.IsNullOrEmpty(levelPath))
            {
                levelHash = LevelHasher.CalculateHash(levelPath);
                if (string.IsNullOrEmpty(levelHash))
                {
                    Console.WriteLine("[Multiplayer] Failed to calculate level hash");
                    return;
                }
            }

            Console.WriteLine($"[Multiplayer] Finding match for level: {levelName} (Hash: {levelHash[..8]}..., Source: {source})");

            var packet = new FindMatchPacket
            {
                LevelHash = levelHash,
                LevelName = levelName,
                Difficulty = "Normal",
                Source = source,
                Timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()
            };

            try
            {
                await _hubConnection?.SendAsync("FindMatch", packet)!;
                Console.WriteLine($"[Multiplayer] FindMatch request sent to server (Hash: {levelHash[..8]}..., Source: {source})");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[Multiplayer] FindMatch error: {ex.Message}");
            }
        }

        /// <summary>
        /// Request list of official levels from server.
        /// </summary>
        public async void RequestOfficialLevels()
        {
            if (!IsConnected)
            {
                Console.WriteLine("[Multiplayer] Not connected to server");
                return;
            }

            Console.WriteLine("[Multiplayer] Requesting official levels list...");

            try
            {
                // Make HTTP GET request to /api/verified_levels
                using var httpClient = new System.Net.Http.HttpClient();
                httpClient.Timeout = TimeSpan.FromSeconds(10);
                
                string levelsUrl = $"{_serverUrl}/api/verified_levels";
                Console.WriteLine($"[Multiplayer] Fetching from: {levelsUrl}");
                
                var response = await httpClient.GetStringAsync(levelsUrl);
                Console.WriteLine($"[Multiplayer] Raw response: {response}");
                
                // Use case-insensitive deserialization to handle lowercase JSON from server
                var options = new System.Text.Json.JsonSerializerOptions
                {
                    PropertyNameCaseInsensitive = true
                };
                
                var levelListPacket = System.Text.Json.JsonSerializer.Deserialize<LevelListPacket>(response, options);
                
                if (levelListPacket != null && levelListPacket.Levels != null)
                {
                    Console.WriteLine($"[Multiplayer] Received {levelListPacket.Levels.Count} official levels");
                    foreach (var level in levelListPacket.Levels)
                    {
                        Console.WriteLine($"[Multiplayer]   - {level.Name} (ID: {level.Id}, Size: {level.FileSize} bytes)");
                    }
                    OnLevelListReceived?.Invoke(levelListPacket);
                }
                else
                {
                    Console.WriteLine("[Multiplayer] Failed to parse level list or levels array is null");
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[Multiplayer] RequestOfficialLevels error: {ex.Message}");
            }
        }

        /// <summary>
        /// Cancel matchmaking search.
        /// </summary>
        public async void CancelMatchmaking()
        {
            if (!IsConnected)
                return;

            Console.WriteLine("[Multiplayer] Canceling matchmaking");

            var packet = new CancelMatchmakingPacket
            {
                Timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()
            };

            try
            {
                await _hubConnection?.SendAsync("CancelMatchmaking", packet)!;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[Multiplayer] CancelMatchmaking error: {ex.Message}");
            }
        }

        /// <summary>
        /// Send Ready signal to server (level is loaded and ready to start).
        /// Server will wait for both players to be ready before starting countdown.
        /// </summary>
        /// <summary>
        /// Send player profile (username, avatar, color) to server.
        /// Should be called after connecting.
        /// </summary>
        public async void SendPlayerProfile()
        {
            if (!IsConnected)
            {
                Console.WriteLine("[Multiplayer] Cannot send PlayerProfile - not connected");
                return;
            }

            var settings = MultiplayerSettings.Instance;
            var packet = new PlayerProfilePacket
            {
                PlayerId = "", // Server will assign
                Username = settings.Username,
                AvatarIndex = settings.AvatarIndex,
                PlayerColorHue = settings.PlayerColorHue,
                ClientVersion = "0.2.3"
            };

            try
            {
                await _hubConnection?.SendAsync("PlayerProfile", packet)!;
                Console.WriteLine($"[Multiplayer] PlayerProfile sent: {settings.Username}, Avatar: {settings.AvatarIndex}, Color: {settings.PlayerColorHue}");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[Multiplayer] SendPlayerProfile error: {ex.Message}");
            }
        }
        
        /// <summary>
        /// Send settings sync packet to server.
        /// Server will forward opponent's settings to us.
        /// </summary>
        public async void SendSettingsSync()
        {
            if (!IsConnected)
            {
                Console.WriteLine("[Multiplayer] Cannot send SettingsSync - not connected");
                return;
            }

            var settings = MultiplayerSettings.Instance;
            var packet = new SettingsSyncPacket
            {
                PlayerId = "", // Server will assign
                // Gameplay settings
                ShowOpponentNotes = settings.ShowOpponentNotes,
                ShowOpponentScore = settings.ShowOpponentScore,
                ShowConnectionQuality = settings.ShowConnectionQuality,
                GhostTransparency = settings.GhostTransparency,
                
                // Visual
                HudOpacity = settings.HudOpacity,
                ConnectionIndicatorPosition = (int)settings.ConnectionIndicatorPosition,
                InterfaceMode = (int)settings.InterfaceMode,
                
                // Performance
                NetworkUpdateRate = (int)settings.UpdateRate,
                EnableInterpolation = settings.EnableInterpolation
            };

            try
            {
                await _hubConnection?.SendAsync("SettingsSync", packet)!;
                Console.WriteLine("[Multiplayer] SettingsSync sent to server");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[Multiplayer] SendSettingsSync error: {ex.Message}");
            }
        }
        
        public async void SendReady(bool isReady = true)
        {
            if (!IsConnected)
            {
                Console.WriteLine("[Multiplayer] Cannot send Ready - not connected");
                return;
            }

            Console.WriteLine($"[Multiplayer] Sending Ready signal: {isReady}");

            var packet = new ReadyPacket
            {
                IsReady = isReady,
                Timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()
            };

            try
            {
                await _hubConnection?.SendAsync("Ready", packet)!;
                Console.WriteLine("[Multiplayer] Ready packet sent to server");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[Multiplayer] SendReady error: {ex.Message}");
            }
        }

        /// <summary>
        /// Send note hit event to opponent with validation.
        /// </summary>
        public async void SendNoteHit(int noteId, int lane, long timestamp, string rating, int combo)
        {
            if (!IsConnected)
            {
                Console.WriteLine("[Multiplayer] Cannot send NoteHit - not connected");
                return;
            }
            
            if (_hubConnection?.State != HubConnectionState.Connected)
            {
                Console.WriteLine($"[Multiplayer] Cannot send NoteHit - connection state: {_hubConnection?.State}");
                return;
            }

            var packet = new NoteHitPacket
            {
                NoteId = noteId,
                Lane = lane,
                Timestamp = timestamp,
                Rating = rating,
                Combo = combo
            };

            try
            {
                await _hubConnection.SendAsync("NoteHit", packet);
                _lastPacketReceivedTime = 0f; // Reset timeout on successful send
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[Multiplayer] SendNoteHit error: {ex.Message}");
                // Don't spam console, this might happen frequently
            }
        }

        /// <summary>
        /// Send player state update with validation.
        /// </summary>
        public async void SendStateUpdate(int score, int combo, float health, 
            int sickCount, int goodCount, int badCount, int shitCount, int missCount)
        {
            if (!IsConnected)
                return;
                
            if (_hubConnection?.State != HubConnectionState.Connected)
                return;

            var packet = new PlayerStatePacket
            {
                Score = score,
                Combo = combo,
                Health = health,
                SickCount = sickCount,
                GoodCount = goodCount,
                BadCount = badCount,
                ShitCount = shitCount,
                MissCount = missCount,
                Timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()
            };

            try
            {
                await _hubConnection?.SendAsync("PlayerState", packet)!;
                _lastPacketReceivedTime = 0f;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[Multiplayer] SendStateUpdate error: {ex.Message}");
            }
        }

        /// <summary>
        /// Send ping packet to measure latency.
        /// </summary>
        private async void SendPing()
        {
            if (!IsConnected)
                return;

            _lastPingTime = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

            var packet = new PingPacket
            {
                ClientTimestamp = _lastPingTime
            };

            try
            {
                await _hubConnection?.SendAsync("Ping", packet)!;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[Multiplayer] SendPing error: {ex.Message}");
            }
        }

        /// <summary>
        /// Update method called from Game1.Update - handles ping, reconnection, and connection health.
        /// </summary>
        public void Update(GameTime gameTime)
        {
            float dt = (float)gameTime.ElapsedGameTime.TotalSeconds;

            // Track connection timeout during initial connection
            if (IsConnecting)
            {
                _connectionTimeoutTimer += dt;
                if (_connectionTimeoutTimer >= CONNECTION_ATTEMPT_TIMEOUT)
                {
                    Console.WriteLine("[Multiplayer] Connection attempt timed out");
                    IsConnecting = false;
                    OnConnectionError?.Invoke("Connection timeout");
                    OnDisconnected?.Invoke("Connection timeout");
                }
            }

            // Periodic ping and connection health check
            if (IsConnected)
            {
                _lastPacketReceivedTime += dt;
                
                // Detect connection loss (no packets received for too long)
                if (_lastPacketReceivedTime >= CONNECTION_TIMEOUT)
                {
                    Console.WriteLine("[Multiplayer] Connection lost - no packets received");
                    IsConnected = false;
                    OnConnectionError?.Invoke("Connection lost");
                    OnDisconnected?.Invoke("Connection lost - no response from server");
                    
                    // Attempt to reconnect
                    if (_shouldReconnect)
                    {
                        _reconnectTimer = _currentReconnectDelay;
                    }
                }
                
                // Send periodic ping
                _pingTimer += dt;
                if (_pingTimer >= PING_INTERVAL)
                {
                    _pingTimer = 0f;
                    SendPing();
                }
                
                // Update connection quality based on ping history
                UpdateConnectionQuality();
            }

            // Reconnection logic with exponential backoff
            if (_shouldReconnect && !IsConnected && !IsConnecting)
            {
                _reconnectTimer -= dt;
                if (_reconnectTimer <= 0f)
                {
                    if (_reconnectAttempts < MAX_RECONNECT_ATTEMPTS)
                    {
                        _reconnectTimer = 0f;
                        Console.WriteLine($"[Multiplayer] Reconnection attempt {_reconnectAttempts + 1}/{MAX_RECONNECT_ATTEMPTS}");
                        Connect(Username);
                    }
                    else
                    {
                        Console.WriteLine("[Multiplayer] Max reconnection attempts reached");
                        _shouldReconnect = false;
                        OnConnectionError?.Invoke("Failed to reconnect after multiple attempts");
                    }
                }
            }
        }
        
        /// <summary>
        /// Update connection quality based on ping history.
        /// </summary>
        private void UpdateConnectionQuality()
        {
            if (_pingHistory.Count == 0)
            {
                Quality = ConnectionQuality.Unknown;
                return;
            }
            
            // Calculate average ping
            int sum = 0;
            foreach (int p in _pingHistory)
                sum += p;
            int avgPing = sum / _pingHistory.Count;
            
            // Calculate ping variance for stability check
            int variance = 0;
            foreach (int p in _pingHistory)
            {
                int diff = p - avgPing;
                variance += diff * diff;
            }
            variance /= _pingHistory.Count;
            int stdDev = (int)Math.Sqrt(variance);
            
            // Determine quality
            ConnectionQuality newQuality;
            bool isStable = stdDev < 30; // Low variance = stable connection
            
            if (avgPing < 50 && isStable)
                newQuality = ConnectionQuality.Excellent;
            else if (avgPing < 100 && isStable)
                newQuality = ConnectionQuality.Good;
            else if (avgPing < 200)
                newQuality = ConnectionQuality.Fair;
            else if (avgPing < 500)
                newQuality = ConnectionQuality.Poor;
            else
                newQuality = ConnectionQuality.Critical;
            
            // Notify if quality changed
            if (newQuality != Quality)
            {
                Quality = newQuality;
                Console.WriteLine($"[Multiplayer] Connection quality: {Quality} (Ping: {avgPing}ms, StdDev: {stdDev}ms)");
                OnQualityChanged?.Invoke(Quality);
            }
        }

        // ══════════════════════════════════════════════════════════════════════
        // Packet Handlers (Called by SignalR when server sends data)
        // ══════════════════════════════════════════════════════════════════════

        private void OnMatchFoundPacket(MatchFoundPacket packet)
        {
            _lastPacketReceivedTime = 0f; // Reset timeout
            
            Console.WriteLine($"[Multiplayer] Match found: {packet.OpponentName} in room {packet.RoomId}");
            Console.WriteLine($"[Multiplayer] Assigned role: Player {packet.PlayerRole} (plays chart {(packet.PlayerRole == 1 ? "1 - BF" : "0 - Enemy")})");
            OpponentName = packet.OpponentName;
            PlayerRole = packet.PlayerRole;
            OnMatchFound?.Invoke(packet.OpponentName, packet.RoomId, packet.PlayerRole);
        }
        
        private void OnOpponentReadyPacket(ReadyStatePacket packet)
        {
            _lastPacketReceivedTime = 0f; // Reset timeout
            
            Console.WriteLine($"[Multiplayer] Opponent ready state changed: {packet.IsReady}");
            
            OnOpponentReadyChanged?.Invoke(packet.IsReady);
        }
        
        private void OnOpponentProfilePacket(PlayerProfilePacket packet)
        {
            _lastPacketReceivedTime = 0f; // Reset timeout
            
            Console.WriteLine($"[Multiplayer] Received opponent profile: {packet.Username}, Avatar: {packet.AvatarIndex}, Color: {packet.PlayerColorHue}");
            
            OnOpponentProfileReceived?.Invoke(packet);
        }
        
        private void OnOpponentSettingsPacket(SettingsSyncPacket packet)
        {
            _lastPacketReceivedTime = 0f; // Reset timeout
            
            Console.WriteLine($"[Multiplayer] Received opponent settings");
            
            OnOpponentSettingsReceived?.Invoke(packet);
        }

        private void OnCountdownStartPacket(CountdownStartPacket packet)
        {
            _lastPacketReceivedTime = 0f; // Reset timeout
            
            Console.WriteLine($"[Multiplayer] [COUNTDOWN] START RECEIVED!");
            Console.WriteLine($"[Multiplayer]   Countdown starts at: {packet.CountdownStartTime}");
            
            long now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            long delay = packet.CountdownStartTime - now;
            Console.WriteLine($"[Multiplayer]   Delay until countdown: {delay}ms");
            
            OnCountdownStart?.Invoke(packet.CountdownStartTime);
        }

        private void OnOpponentHitPacket(OpponentHitPacket packet)
        {
            _lastPacketReceivedTime = 0f; // Reset timeout
            OnOpponentHit?.Invoke(packet);
        }

        private void OnOpponentStatePacket(OpponentStatePacket packet)
        {
            _lastPacketReceivedTime = 0f; // Reset timeout
            OnOpponentState?.Invoke(packet);
        }

        private void OnGameStartPacket(GameStartPacket packet)
        {
            _lastPacketReceivedTime = 0f; // Reset timeout
            
            Console.WriteLine($"[Multiplayer] [GAME START] RECEIVED!");
            Console.WriteLine($"[Multiplayer]   Opponent: {packet.OpponentName}");
            Console.WriteLine($"[Multiplayer]   Player Role: {packet.PlayerRole} ({(packet.PlayerRole == 1 ? "Player 1 / Chart 0" : "Player 2 / Chart 1")})");
            Console.WriteLine($"[Multiplayer]   Level Hash: {packet.LevelHash[..8]}...");
            Console.WriteLine($"[Multiplayer]   Start Time: {packet.StartTimestamp}");
            
            OpponentName = packet.OpponentName;
            
            long now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            long timeUntilStart = packet.StartTimestamp - now;
            Console.WriteLine($"[Multiplayer]   Starting in: {timeUntilStart}ms ({timeUntilStart / 1000.0:F1}s)");
            
            OnGameStart?.Invoke(packet.StartTimestamp, packet.PlayerRole);
        }

        private void OnGameEndPacket(GameEndPacket packet)
        {
            _lastPacketReceivedTime = 0f; // Reset timeout
            
            Console.WriteLine($"[Multiplayer] Game ended. Winner: {packet.WinnerName}");
            OnGameEnd?.Invoke(packet);
        }

        private void OnPongPacket(PongPacket packet)
        {
            // Calculate ping
            long now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            int currentPing = (int)(now - packet.ClientTimestamp);
            Ping = currentPing;
            
            // Update time synchronization with server
            TimeSyncManager.Instance.UpdateClockOffsetSimple(packet.ClientTimestamp, currentPing);
            
            // Update ping history for quality tracking
            _pingHistory.Add(currentPing);
            if (_pingHistory.Count > PING_HISTORY_SIZE)
                _pingHistory.RemoveAt(0);
            
            // Reset packet received timer (connection is alive)
            _lastPacketReceivedTime = 0f;
            
            OnPingUpdate?.Invoke(Ping);
            
            // Log if ping is unusually high
            if (currentPing > 300)
                Console.WriteLine($"[Multiplayer] [!] High ping detected: {currentPing}ms");
        }

        /// <summary>
        /// Enable automatic reconnection on disconnect.
        /// </summary>
        public void EnableAutoReconnect(bool enable)
        {
            _shouldReconnect = enable;
            if (!enable)
            {
                _reconnectTimer = 0f;
                _reconnectAttempts = 0;
                _currentReconnectDelay = MIN_RECONNECT_DELAY;
            }
        }
        
        /// <summary>
        /// Get connection diagnostics for debugging or display.
        /// </summary>
        public string GetConnectionDiagnostics()
        {
            if (!IsConnected && !IsConnecting)
                return "Not connected";
                
            if (IsConnecting)
                return $"Connecting... (Attempt {_reconnectAttempts + 1}/{MAX_RECONNECT_ATTEMPTS})";
            
            string status = $"Connected | Ping: {Ping}ms | Quality: {Quality}";
            
            if (_pingHistory.Count > 0)
            {
                int sum = 0;
                foreach (int p in _pingHistory) sum += p;
                int avg = sum / _pingHistory.Count;
                status += $" | Avg: {avg}ms";
            }
            
            if (_hubConnection != null)
                status += $" | State: {_hubConnection.State}";
            
            return status;
        }
        
        /// <summary>
        /// Force reset connection state (use with caution).
        /// </summary>
        public void ForceReset()
        {
            Console.WriteLine("[Multiplayer] Force resetting connection...");
            
            IsConnected = false;
            IsConnecting = false;
            _reconnectAttempts = 0;
            _reconnectTimer = 0f;
            _currentReconnectDelay = MIN_RECONNECT_DELAY;
            _pingHistory.Clear();
            Quality = ConnectionQuality.Unknown;
            
            if (_hubConnection != null)
            {
                try
                {
                    _hubConnection.StopAsync().Wait(1000);
                    _hubConnection.DisposeAsync().AsTask().Wait(1000);
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"[Multiplayer] Error during force reset: {ex.Message}");
                }
                _hubConnection = null;
            }
        }

        /// <summary>
        /// Clean up resources.
        /// </summary>
        public void Cleanup()
        {
            if (IsConnected)
                Disconnect();
        }
        
        // ══════════════════════════════════════════════════════════════════════
        // LEADERBOARD METHODS (v0.2.4)
        // ══════════════════════════════════════════════════════════════════════
        
        /// <summary>
        /// Request global leaderboard from server.
        /// </summary>
        public async void RequestGlobalLeaderboard(int skip = 0, int take = 100)
        {
            if (_hubConnection == null || _hubConnection.State != HubConnectionState.Connected)
            {
                Console.WriteLine("[Multiplayer] ERROR: Cannot request leaderboard - not connected");
                return;
            }
            
            try
            {
                var packet = new GetLeaderboardPacket
                {
                    Skip = skip,
                    Take = take,
                    Timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()
                };
                
                Console.WriteLine($"[Multiplayer] Requesting global leaderboard (skip={skip}, take={take})...");
                await _hubConnection.InvokeAsync("GetLeaderboard", packet);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[Multiplayer] ERROR requesting leaderboard: {ex.Message}");
            }
        }
        
        /// <summary>
        /// Request match history from server.
        /// </summary>
        public async void RequestMatchHistory(string? playerId = null, int skip = 0, int take = 50)
        {
            if (_hubConnection == null || _hubConnection.State != HubConnectionState.Connected)
            {
                Console.WriteLine("[Multiplayer] ERROR: Cannot request match history - not connected");
                return;
            }
            
            try
            {
                var packet = new GetMatchHistoryPacket
                {
                    PlayerId = playerId,
                    Skip = skip,
                    Take = take,
                    Timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()
                };
                
                Console.WriteLine($"[Multiplayer] Requesting match history (playerId={playerId ?? "ALL"}, skip={skip}, take={take})...");
                await _hubConnection.InvokeAsync("GetMatchHistory", packet);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[Multiplayer] ERROR requesting match history: {ex.Message}");
            }
        }
        
        /// <summary>
        /// Submit match result to server for global leaderboard.
        /// </summary>
        public async void SubmitMatchResult(MatchHistoryPacket matchData)
        {
            if (_hubConnection == null || _hubConnection.State != HubConnectionState.Connected)
            {
                Console.WriteLine("[Multiplayer] WARNING: Cannot submit match result - not connected (will save locally only)");
                return;
            }
            
            try
            {
                Console.WriteLine($"[Multiplayer] Submitting match result: {matchData.Player1.Username} vs {matchData.Player2.Username}");
                await _hubConnection.InvokeAsync("SubmitMatchResult", matchData);
                Console.WriteLine("[Multiplayer] Match result submitted successfully");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[Multiplayer] ERROR submitting match result: {ex.Message}");
            }
        }
        
        // ══════════════════════════════════════════════════════════════════════
        // LEADERBOARD HANDLERS (v0.2.4)
        // ══════════════════════════════════════════════════════════════════════
        
        private void OnLeaderboardResponse(LeaderboardResponsePacket packet)
        {
            _lastPacketReceivedTime = 0f; // Reset timeout
            Console.WriteLine($"[Multiplayer] Received leaderboard: {packet.Entries.Count}/{packet.TotalPlayers} players");
            OnLeaderboardReceived?.Invoke(packet);
        }
        
        private void OnMatchHistoryResponse(MatchHistoryResponsePacket packet)
        {
            _lastPacketReceivedTime = 0f; // Reset timeout
            Console.WriteLine($"[Multiplayer] Received match history: {packet.Matches.Count}/{packet.TotalMatches} matches");
            OnMatchHistoryReceived?.Invoke(packet);
        }
    }
}
