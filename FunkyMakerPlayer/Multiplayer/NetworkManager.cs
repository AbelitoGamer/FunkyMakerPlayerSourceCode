using System;
using System.Collections.Generic;
using System.Threading;
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
        public string OpponentPlayerId { get; private set; } = "";
        public int OpponentColorHue { get; private set; } = 180;
        public int PlayerRole { get; private set; } = 1;  // 1 = Player 1, 2 = Player 2
        public int Ping { get; private set; }
        public bool HasMeasuredPing { get; private set; }
        public ConnectionQuality Quality { get; private set; } = ConnectionQuality.Unknown;

        // Server configuration
        private string _serverUrl = "https://funkymakerserver.onrender.com";
        private string _signalREndpoint = "/multiplayer";
        
        public string ServerUrl
        {
            get => _serverUrl;
            set => _serverUrl = string.IsNullOrWhiteSpace(value)
                ? "https://funkymakerserver.onrender.com"
                : value.Trim().TrimEnd('/');
        }
        
        public string SignalRUrl => $"{_serverUrl}{_signalREndpoint}";

        // SignalR connection & cancellation tracking
        private HubConnection? _hubConnection;
        private CancellationTokenSource? _connectCts;
        private int _connectGeneration = 0;
        private readonly object _connectionStateLock = new object();

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
        
        // ── PvP Character Events (v0.4.3+) ──────────────────────────────────────
        #pragma warning disable CS0067 // Event never used
        public event Action<int, float, string>? OnOpponentNoteHit; // direction, time, rating
        #pragma warning restore CS0067
        public event Action<int>? OnOpponentKeyPress; // lane index (0-3)
        public event Action<string>? OnOpponentDisconnected; // reason
        public event Action<string, string, int>? OnCharacterAnimation; // playerId, animState, direction
        public event Action<string, byte[]?>? OnCharacterSprite; // playerId, spriteData

        // Ping measurement & quality tracking
        private readonly object _pingLock = new object();
        private long _lastPingTime;
        private float _pingTimer;
        private const float PING_INTERVAL = 2f; // seconds
        private const float PING_BOOTSTRAP_INTERVAL = 0.35f; // rapid initial clock sync samples
        private List<int> _pingHistory = new List<int>();
        private const int PING_HISTORY_SIZE = 10;
        private float _lastPacketReceivedTime;
        private const float CONNECTION_TIMEOUT = 70f; // Match server ClientTimeoutInterval (60s) + 10s buffer

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

        // KeyPress throttling (prevent spam)
        private Dictionary<int, long> _lastKeyPressSent = new Dictionary<int, long>();
        private const long KEY_PRESS_THROTTLE_MS = 50; // Min 50ms between keypress packets per lane

        private NetworkManager()
        {
            IsConnected = false;
            IsConnecting = false;
        }

        private bool IsActiveAttemptLocked(HubConnection hub, int generation) =>
            generation == _connectGeneration && ReferenceEquals(_hubConnection, hub);

        private void DetachConnectionLocked(out HubConnection? hubToDispose, out CancellationTokenSource? ctsToDispose)
        {
            unchecked { _connectGeneration++; }
            ctsToDispose = _connectCts;
            _connectCts = null;
            hubToDispose = _hubConnection;
            _hubConnection = null;
            IsConnecting = false;
            IsConnected = false;
        }

        private static void CancelAndDisposeCts(CancellationTokenSource? cts)
        {
            if (cts == null)
                return;

            try { cts.Cancel(); } catch { }
            try { cts.Dispose(); } catch { }
        }

        private static async Task DisposeHubQuietlyAsync(HubConnection? hub)
        {
            if (hub == null)
                return;

            try { await hub.StopAsync().ConfigureAwait(false); } catch { }
            try { await hub.DisposeAsync().ConfigureAwait(false); } catch { }
        }

        private void HandleFailedInitialAttempt(HubConnection hub, int generation, string errorMessage)
        {
            HubConnection? hubToDispose = null;
            CancellationTokenSource? ctsToDispose = null;
            bool wasActiveAttempt;

            lock (_connectionStateLock)
            {
                wasActiveAttempt = IsActiveAttemptLocked(hub, generation);
                if (wasActiveAttempt)
                {
                    DetachConnectionLocked(out hubToDispose, out ctsToDispose);
                }
            }

            CancelAndDisposeCts(ctsToDispose);
            _ = DisposeHubQuietlyAsync(wasActiveAttempt ? hubToDispose : hub);

            if (wasActiveAttempt)
            {
                OnConnectionError?.Invoke(errorMessage);
            }
        }

        /// <summary>
        /// Connect to the multiplayer server with improved error handling.
        /// </summary>
        public async void Connect(string username)
        {
            HubConnection? staleHub = null;
            CancellationTokenSource? staleCts = null;
            HubConnection hub;
            CancellationTokenSource cts;
            int generation;

            lock (_connectionStateLock)
            {
                if (IsConnected || IsConnecting)
                {
                    Console.WriteLine("[Multiplayer] Already connected or connecting");
                    return;
                }

                // Clear any leftover hub/CTS from a previous timed-out or failed attempt
                if (_hubConnection != null || _connectCts != null)
                {
                    DetachConnectionLocked(out staleHub, out staleCts);
                }

                generation = unchecked(++_connectGeneration);
                cts = new CancellationTokenSource();
                cts.CancelAfter(TimeSpan.FromSeconds(CONNECTION_ATTEMPT_TIMEOUT));
                _connectCts = cts;

                Username = username;
                IsConnecting = true;
                IsConnected = false;
                _shouldReconnect = false;
                _connectionTimeoutTimer = 0f;
                HasMeasuredPing = false;
                Ping = 0;
                Quality = ConnectionQuality.Unknown;
                lock (_pingLock) _pingHistory.Clear();

                Console.WriteLine($"[Multiplayer] Connecting to {_serverUrl} as {username}...");

                hub = new HubConnectionBuilder()
                    .WithUrl(SignalRUrl)
                    .WithAutomaticReconnect(new[] {
                        TimeSpan.FromSeconds(0),    // First retry: immediate
                        TimeSpan.FromSeconds(2),    // Second retry: 2s
                        TimeSpan.FromSeconds(5),    // Third retry: 5s
                        TimeSpan.FromSeconds(10)    // Fourth+ retry: 10s
                    })
                    .Build();

                // Match server SignalR settings (KeepAlive: 10s, ClientTimeout: 60s)
                hub.KeepAliveInterval = TimeSpan.FromSeconds(10);
                hub.ServerTimeout = TimeSpan.FromSeconds(60);
                hub.HandshakeTimeout = TimeSpan.FromSeconds(CONNECTION_ATTEMPT_TIMEOUT);

                _hubConnection = hub;
            }

            CancelAndDisposeCts(staleCts);
            if (staleHub != null)
                _ = DisposeHubQuietlyAsync(staleHub);

            try
            {
                // Register disconnection handler
                hub.Closed += async (error) =>
                {
                    bool shouldHandle;
                    lock (_connectionStateLock)
                    {
                        shouldHandle = IsActiveAttemptLocked(hub, generation);
                        if (shouldHandle)
                        {
                            bool wasConnected = IsConnected;
                            IsConnected = false;
                            IsConnecting = false;

                            // Trigger reconnection if enabled after an established session
                            if (wasConnected && _shouldReconnect && _reconnectAttempts < MAX_RECONNECT_ATTEMPTS)
                            {
                                Console.WriteLine($"[Multiplayer] Will attempt reconnection in {_currentReconnectDelay:F1}s");
                                _reconnectTimer = _currentReconnectDelay;
                                _reconnectAttempts++;

                                // Exponential backoff
                                _currentReconnectDelay = Math.Min(_currentReconnectDelay * 1.5f, MAX_RECONNECT_DELAY);
                            }
                        }
                    }

                    if (!shouldHandle)
                        return;

                    string reason = error?.Message ?? "Connection closed";
                    Console.WriteLine($"[Multiplayer] Connection closed: {reason}");
                    OnDisconnected?.Invoke(reason);
                    await Task.CompletedTask;
                };
                
                // Register reconnecting handler
                hub.Reconnecting += (error) =>
                {
                    lock (_connectionStateLock)
                    {
                        if (!IsActiveAttemptLocked(hub, generation))
                            return Task.CompletedTask;

                        IsConnected = false;
                        IsConnecting = true;
                    }

                    Console.WriteLine($"[Multiplayer] Reconnecting... {error?.Message ?? ""}");
                    return Task.CompletedTask;
                };
                
                // Register reconnected handler
                hub.Reconnected += (connectionId) =>
                {
                    lock (_connectionStateLock)
                    {
                        if (!IsActiveAttemptLocked(hub, generation))
                            return Task.CompletedTask;

                        IsConnecting = false;
                        IsConnected = true;
                        _reconnectAttempts = 0;
                        _currentReconnectDelay = MIN_RECONNECT_DELAY;
                        _lastPacketReceivedTime = 0f;
                        _pingTimer = 0f;
                    }

                    Console.WriteLine($"[Multiplayer] Reconnected successfully! ConnectionId: {connectionId}");
                    OnConnected?.Invoke();
                    SendPlayerProfile();
                    SendSettingsSync();
                    SendPing();
                    return Task.CompletedTask;
                };
                
                // Register server event handlers
                hub.On<MatchFoundPacket>("MatchFound", OnMatchFoundPacket);
                hub.On<ReadyStatePacket>("OpponentReady", OnOpponentReadyPacket);
                hub.On<PlayerProfilePacket>("OpponentProfile", OnOpponentProfilePacket);
                hub.On<SettingsSyncPacket>("OpponentSettings", OnOpponentSettingsPacket);
                hub.On<CountdownStartPacket>("CountdownStart", OnCountdownStartPacket);
                hub.On<OpponentHitPacket>("OpponentHit", OnOpponentHitPacket);
                hub.On<OpponentStatePacket>("OpponentState", OnOpponentStatePacket);
                hub.On<GameStartPacket>("GameStart", OnGameStartPacket);
                hub.On<GameEndPacket>("GameEnd", OnGameEndPacket);
                hub.On<PongPacket>("Pong", OnPongPacket);
                hub.On<LeaderboardResponsePacket>("LeaderboardResponse", OnLeaderboardResponse);
                hub.On<MatchHistoryResponsePacket>("MatchHistoryResponse", OnMatchHistoryResponse);
                hub.On<System.Text.Json.JsonElement>("MatchRecorded", _ =>
                    Console.WriteLine("[Multiplayer] Server confirmed match recorded"));
                
                // PvP Character handlers (v0.4.3+)
                hub.On<PlayerCharacterPacket>("CharacterInfo", OnCharacterInfoPacket);
                hub.On<CharacterAnimationPacket>("CharacterAnimation", OnCharacterAnimationPacket);
                hub.On<OpponentKeyPressPacket>("OpponentKeyPress", OnOpponentKeyPressPacket);
                
                // Opponent disconnection handler
                hub.On<OpponentDisconnectedPacket>("OpponentDisconnected", OnOpponentDisconnectedPacket);
                
                Console.WriteLine($"[Multiplayer] Starting SignalR connection: {SignalRUrl}");
                await hub.StartAsync(cts.Token);

                bool promoted = false;
                CancellationTokenSource? completedCts = null;
                lock (_connectionStateLock)
                {
                    if (IsActiveAttemptLocked(hub, generation) && !cts.IsCancellationRequested)
                    {
                        IsConnecting = false;
                        IsConnected = true;
                        _reconnectAttempts = 0; // Reset on successful connection
                        _currentReconnectDelay = MIN_RECONNECT_DELAY;
                        _lastPacketReceivedTime = 0f;
                        _pingTimer = 0f;
                        promoted = true;

                        if (ReferenceEquals(_connectCts, cts))
                        {
                            completedCts = _connectCts;
                            _connectCts = null;
                        }
                    }
                }

                try { completedCts?.Dispose(); } catch { }

                if (!promoted)
                {
                    Console.WriteLine("[Multiplayer] Ignoring late connection completion after timeout or cancel");
                    _ = DisposeHubQuietlyAsync(hub);
                    return;
                }
                
                OnConnected?.Invoke();
                
                Console.WriteLine("[Multiplayer] [OK] Connected successfully to SignalR hub");
                
                // Send player profile, settings, and immediate ping after connecting
                SendPlayerProfile();
                SendSettingsSync();
                SendPing();
            }
            catch (OperationCanceledException)
            {
                Console.WriteLine("[Multiplayer] [X] Connection attempt timed out or was cancelled");
                HandleFailedInitialAttempt(hub, generation, "Connection timeout");
            }
            catch (System.Net.Http.HttpRequestException ex)
            {
                Console.WriteLine($"[Multiplayer] [X] Network error: {ex.Message}");
                HandleFailedInitialAttempt(hub, generation, "Network error. Check your internet connection.");
            }
            catch (TimeoutException ex)
            {
                Console.WriteLine($"[Multiplayer] [X] Connection timeout: {ex.Message}");
                HandleFailedInitialAttempt(hub, generation, "Connection timeout. Server may be down.");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[Multiplayer] [X] Connection failed: {ex.Message}");
                Console.WriteLine($"[Multiplayer] Exception type: {ex.GetType().Name}");
                HandleFailedInitialAttempt(hub, generation, $"Connection failed: {ex.Message}");
            }
        }

        /// <summary>
        /// Disconnect from the server or abort any pending connection attempt.
        /// </summary>
        public async void Disconnect()
        {
            bool wasConnected;
            bool hadPendingState;
            HubConnection? hubToStop;
            CancellationTokenSource? ctsToDispose;

            lock (_connectionStateLock)
            {
                wasConnected = IsConnected;
                hadPendingState = IsConnected || IsConnecting || _hubConnection != null || _connectCts != null;
                _shouldReconnect = false;
                _reconnectAttempts = 0;
                _reconnectTimer = 0f;
                _connectionTimeoutTimer = 0f;
                DetachConnectionLocked(out hubToStop, out ctsToDispose);
            }

            CancelAndDisposeCts(ctsToDispose);

            if (!hadPendingState && hubToStop == null)
                return;

            Console.WriteLine("[Multiplayer] Disconnecting...");

            try
            {
                if (hubToStop != null)
                {
                    await hubToStop.StopAsync().ConfigureAwait(false);
                    await hubToStop.DisposeAsync().ConfigureAwait(false);
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[Multiplayer] Disconnect error: {ex.Message}");
            }
            finally
            {
                HasMeasuredPing = false;
                Ping = 0;
                Quality = ConnectionQuality.Unknown;
                lock (_pingLock) _pingHistory.Clear();
                
                // Reset time synchronization
                TimeSyncManager.Instance.Reset();
                
                if (wasConnected)
                    OnDisconnected?.Invoke("User disconnected");
            }
        }

        /// <summary>
        /// Find a match with specified level (and optional private room code).
        /// </summary>
        public async void FindMatch(string levelHash, string levelName = "Unknown", LevelSource source = LevelSource.Local, string levelPath = "", string roomCode = "")
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

            string hashPreview = levelHash.Length >= 8 ? levelHash[..8] : levelHash;
            string cleanUsername = string.IsNullOrWhiteSpace(MultiplayerSettings.Instance.Username)
                ? "Player"
                : MultiplayerSettings.Instance.Username.Trim();
            string cleanRoomCode = string.IsNullOrWhiteSpace(roomCode) ? "" : roomCode.Trim().ToUpperInvariant();

            Console.WriteLine($"[Multiplayer] Finding match for level: {levelName} (Hash: {hashPreview}..., Source: {source}, Room: {(string.IsNullOrEmpty(cleanRoomCode) ? "Public" : cleanRoomCode)})");

            var packet = new FindMatchPacket
            {
                LevelHash = levelHash,
                LevelName = levelName,
                Difficulty = "Normal",
                Source = source,
                Username = cleanUsername,
                RoomCode = cleanRoomCode,
                Timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()
            };

            try
            {
                await _hubConnection?.SendAsync("FindMatch", packet)!;
                Console.WriteLine($"[Multiplayer] FindMatch request sent to server (Hash: {hashPreview}..., Source: {source})");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[Multiplayer] FindMatch error: {ex.Message}");
            }
        }

        /// <summary>
        /// Notify the server that this client is leaving the active match room.
        /// </summary>
        public async void LeaveMatch(bool notifyOpponent = false)
        {
            if (!IsConnected || _hubConnection == null)
                return;

            try
            {
                await _hubConnection.SendAsync("LeaveMatch", notifyOpponent);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[Multiplayer] LeaveMatch error: {ex.Message}");
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
            string cleanUsername = string.IsNullOrWhiteSpace(settings.Username) ? "Player" : settings.Username.Trim();
            var packet = new PlayerProfilePacket
            {
                PlayerId = settings.PlayerId,
                Username = cleanUsername,
                AvatarIndex = 0,
                PlayerColorHue = settings.PlayerColorHue,
                ColorHue = settings.PlayerColorHue,
                ClientVersion = typeof(PlayerProfilePacket).Assembly.GetName().Version?.ToString(3) ?? "0.4.3"
            };

            try
            {
                await _hubConnection?.SendAsync("PlayerProfile", packet)!;
                Console.WriteLine($"[Multiplayer] PlayerProfile sent: {cleanUsername} (ID={settings.PlayerId}), Color={settings.PlayerColorHue}");
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
                PlayerId = settings.PlayerId,
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

            Console.WriteLine($"[Multiplayer] ===== SENDING READY SIGNAL: {isReady} =====");
            Console.WriteLine($"[Multiplayer] Connection State: {_hubConnection?.State}");
            Console.WriteLine($"[Multiplayer] OpponentName: {OpponentName}");

            var packet = new ReadyPacket
            {
                IsReady = isReady,
                Timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()
            };

            try
            {
                await _hubConnection?.SendAsync("Ready", packet)!;
                Console.WriteLine("[Multiplayer] ===== Ready packet SENT to server =====");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[Multiplayer] SendReady ERROR: {ex.Message}");
                Console.WriteLine($"[Multiplayer] Stack trace: {ex.StackTrace}");
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
        /// Send key press event to opponent for real-time animation.
        /// Called every time player presses an arrow key (not just on successful hits).
        /// Throttled to prevent network spam (max 1 per 50ms per lane).
        /// </summary>
        public async void SendKeyPress(int lane)
        {
            if (!IsConnected || _hubConnection?.State != HubConnectionState.Connected)
                return;

            long now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            
            // Throttle: Only send if enough time has passed since last send for this lane
            if (_lastKeyPressSent.TryGetValue(lane, out long lastSent))
            {
                if (now - lastSent < KEY_PRESS_THROTTLE_MS)
                {
                    // Too soon, skip this packet
                    return;
                }
            }
            
            _lastKeyPressSent[lane] = now;

            var packet = new KeyPressPacket
            {
                Lane = lane,
                Timestamp = now
            };

            try
            {
                await _hubConnection.SendAsync("KeyPress", packet);
            }
            catch (Exception)
            {
                // Don't spam console - key presses happen frequently
                // Console.WriteLine($"[Multiplayer] SendKeyPress error: {ex.Message}");
            }
        }

        /// <summary>
        /// Update method called from Game1.Update - handles ping, reconnection, and connection health.
        /// </summary>
        public void Update(GameTime gameTime)
        {
            float dt = (float)gameTime.ElapsedGameTime.TotalSeconds;

            // Track connection timeout during initial connection
            bool timedOut = false;
            HubConnection? timedOutHub = null;
            CancellationTokenSource? timedOutCts = null;

            lock (_connectionStateLock)
            {
                if (IsConnecting)
                {
                    _connectionTimeoutTimer += dt;
                    if (_connectionTimeoutTimer >= CONNECTION_ATTEMPT_TIMEOUT)
                    {
                        timedOut = true;
                        DetachConnectionLocked(out timedOutHub, out timedOutCts);
                    }
                }
            }

            if (timedOut)
            {
                Console.WriteLine("[Multiplayer] Connection attempt timed out");
                CancelAndDisposeCts(timedOutCts);
                _ = DisposeHubQuietlyAsync(timedOutHub);
                OnConnectionError?.Invoke("Connection timeout");
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
                
                // Send periodic ping (faster initial burst until TimeSync reaches 3 samples)
                float pingInterval = TimeSyncManager.Instance.IsSynced ? PING_INTERVAL : PING_BOOTSTRAP_INTERVAL;
                _pingTimer += dt;
                if (_pingTimer >= pingInterval)
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
            ConnectionQuality newQuality;
            int avgPing;
            int stdDev;
            bool changed = false;

            lock (_pingLock)
            {
                if (_pingHistory.Count == 0)
                {
                    if (Quality != ConnectionQuality.Unknown)
                        Quality = ConnectionQuality.Unknown;
                    return;
                }

                // Calculate average ping
                int sum = 0;
                foreach (int p in _pingHistory)
                    sum += p;
                avgPing = sum / _pingHistory.Count;

                // Calculate ping variance for stability check
                int variance = 0;
                foreach (int p in _pingHistory)
                {
                    int diff = p - avgPing;
                    variance += diff * diff;
                }
                variance /= _pingHistory.Count;
                stdDev = (int)Math.Sqrt(variance);

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

                if (newQuality != Quality)
                {
                    Quality = newQuality;
                    changed = true;
                }
            }

            // Notify outside lock to prevent deadlocks
            if (changed)
            {
                Console.WriteLine($"[Multiplayer] Connection quality: {newQuality} (Ping: {avgPing}ms, StdDev: {stdDev}ms)");
                OnQualityChanged?.Invoke(newQuality);
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
            OpponentPlayerId = "";
            OpponentColorHue = 180;
            OpponentName = string.IsNullOrWhiteSpace(packet.OpponentName) ? "Opponent" : packet.OpponentName;
            PlayerRole = packet.PlayerRole;
            // Re-send profile once matched in _activeMatches so opponent receives real Username/PlayerId/ColorHue
            // (workaround for server FindMatch setting _playerNames[id] = LevelName).
            SendPlayerProfile();
            OnMatchFound?.Invoke(packet.OpponentName, packet.RoomId, packet.PlayerRole);
        }
        
        private void OnOpponentReadyPacket(ReadyStatePacket packet)
        {
            _lastPacketReceivedTime = 0f; // Reset timeout
            
            Console.WriteLine($"[Multiplayer] ===== RECEIVED OPPONENT READY =====");
            Console.WriteLine($"[Multiplayer] Opponent ready state: {packet.IsReady}");
            Console.WriteLine($"[Multiplayer] Timestamp: {packet.Timestamp}");
            Console.WriteLine($"[Multiplayer] Firing OnOpponentReadyChanged event...");
            
            OnOpponentReadyChanged?.Invoke(packet.IsReady);
            
            Console.WriteLine($"[Multiplayer] ===== OnOpponentReadyChanged event fired =====");
        }
        
        private void OnOpponentProfilePacket(PlayerProfilePacket packet)
        {
            _lastPacketReceivedTime = 0f; // Reset timeout
            
            Console.WriteLine($"[Multiplayer] Received opponent profile: {packet.Username}, ID: {packet.PlayerId}, Color: {packet.PlayerColorHue}");
            if (!string.IsNullOrWhiteSpace(packet.Username))
                OpponentName = packet.Username;
            if (!string.IsNullOrWhiteSpace(packet.PlayerId))
                OpponentPlayerId = packet.PlayerId;
            OpponentColorHue = packet.PlayerColorHue;
            
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

        private void OnOpponentKeyPressPacket(OpponentKeyPressPacket packet)
        {
            _lastPacketReceivedTime = 0f; // Reset timeout
            OnOpponentKeyPress?.Invoke(packet.Lane);
        }

        private void OnOpponentDisconnectedPacket(OpponentDisconnectedPacket packet)
        {
            _lastPacketReceivedTime = 0f; // Reset timeout
            
            Console.WriteLine($"[Multiplayer] Opponent disconnected: {packet.Reason}");
            OnOpponentDisconnected?.Invoke(packet.Reason);
        }

        private void OnGameStartPacket(GameStartPacket packet)
        {
            _lastPacketReceivedTime = 0f; // Reset timeout
            
            Console.WriteLine($"[Multiplayer] [GAME START] RECEIVED!");
            Console.WriteLine($"[Multiplayer]   Opponent: {packet.OpponentName}");
            Console.WriteLine($"[Multiplayer]   Player Role: {packet.PlayerRole} ({(packet.PlayerRole == 1 ? "Player 1 / Chart 0" : "Player 2 / Chart 1")})");
            Console.WriteLine($"[Multiplayer]   Level Hash: {packet.LevelHash[..8]}...");
            Console.WriteLine($"[Multiplayer]   Start Time: {packet.StartTimestamp}");
            
            if (string.IsNullOrWhiteSpace(OpponentPlayerId) && !string.IsNullOrWhiteSpace(packet.OpponentName))
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
            // Calculate ping (clamp >= 0 even on fast loopback or clock adjustment)
            long now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            int currentPing = (int)Math.Clamp(now - packet.ClientTimestamp, 0, 60000);
            Ping = currentPing;
            HasMeasuredPing = true;

            // Update time synchronization with server using real ServerTimestamp when provided
            if (packet.ServerTimestamp > 0)
                TimeSyncManager.Instance.UpdateClockOffset(packet.ClientTimestamp, packet.ServerTimestamp, packet.ServerTimestamp, now);
            else
                TimeSyncManager.Instance.UpdateClockOffsetSimple(packet.ClientTimestamp, currentPing);

            // Update ping history for quality tracking
            lock (_pingLock)
            {
                _pingHistory.Add(currentPing);
                if (_pingHistory.Count > PING_HISTORY_SIZE)
                    _pingHistory.RemoveAt(0);
            }

            // Reset packet received timer (connection is alive)
            _lastPacketReceivedTime = 0f;

            UpdateConnectionQuality();
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

            HubConnection? hubToReset;
            CancellationTokenSource? ctsToReset;
            lock (_connectionStateLock)
            {
                _shouldReconnect = false;
                _reconnectAttempts = 0;
                _reconnectTimer = 0f;
                _connectionTimeoutTimer = 0f;
                _currentReconnectDelay = MIN_RECONNECT_DELAY;
                DetachConnectionLocked(out hubToReset, out ctsToReset);
            }

            CancelAndDisposeCts(ctsToReset);
            lock (_pingLock) _pingHistory.Clear();
            Quality = ConnectionQuality.Unknown;
            HasMeasuredPing = false;
            Ping = 0;
            
            if (hubToReset != null)
            {
                try
                {
                    hubToReset.StopAsync().Wait(1000);
                    hubToReset.DisposeAsync().AsTask().Wait(1000);
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"[Multiplayer] Error during force reset: {ex.Message}");
                }
            }
        }

        /// <summary>
        /// Clean up resources.
        /// </summary>
        public void Cleanup()
        {
            if (IsConnected || IsConnecting || _hubConnection != null || _connectCts != null)
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
        
        // ══════════════════════════════════════════════════════════════════════
        // PVP CHARACTER SYNC METHODS (v0.4.3+)
        // ══════════════════════════════════════════════════════════════════════
        
        /// <summary>
        /// Send character sprite and info to opponent
        /// </summary>
        public async void SendCharacterInfo(PlayerCharacterPacket packet)
        {
            if (!IsConnected || _hubConnection == null) return;
            
            try
            {
                await _hubConnection.InvokeAsync("SendCharacterInfo", packet);
                Console.WriteLine($"[Multiplayer] Sent character info: {packet.CharacterName}");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[Multiplayer] Error sending character info: {ex.Message}");
            }
        }
        
        /// <summary>
        /// Send character animation state to opponent
        /// </summary>
        public async void SendCharacterAnimation(CharacterAnimationPacket packet)
        {
            if (!IsConnected || _hubConnection == null) return;
            
            try
            {
                await _hubConnection.InvokeAsync("SendCharacterAnimation", packet);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[Multiplayer] Error sending character animation: {ex.Message}");
            }
        }
        
        /// <summary>
        /// Simplified note hit for PvP (just direction and timing)
        /// </summary>
        public async void SendNoteHit(int direction, float time, string rating)
        {
            if (!IsConnected || _hubConnection == null) return;
            
            try
            {
                var packet = new NoteHitPacket
                {
                    NoteId = direction,
                    Lane = direction,
                    Timestamp = (long)time,
                    Rating = rating,
                    Combo = 0
                };
                
                await _hubConnection.InvokeAsync("SendNoteHit", packet);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[Multiplayer] Error sending note hit: {ex.Message}");
            }
        }
        
        private void OnCharacterInfoPacket(PlayerCharacterPacket packet)
        {
            Console.WriteLine($"[Multiplayer] Received character info: {packet.CharacterName}");
            OnCharacterSprite?.Invoke(packet.PlayerId, packet.SpriteData);
        }
        
        private void OnCharacterAnimationPacket(CharacterAnimationPacket packet)
        {
            OnCharacterAnimation?.Invoke(packet.PlayerId, packet.AnimationState, packet.Direction);
        }
        
        /// <summary>
        /// Property to expose PlayerId for GameMultiplayer
        /// </summary>
        public string PlayerId { get; private set; } = Guid.NewGuid().ToString();
        
        /// <summary>
        /// Property to expose PlayerName for GameMultiplayer
        /// </summary>
        public string PlayerName => Username;
    }
}
