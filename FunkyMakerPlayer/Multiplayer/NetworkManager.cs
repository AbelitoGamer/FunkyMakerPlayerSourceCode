using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using FunkyMakerPlayer.Multiplayer.Rooms;
using Microsoft.Xna.Framework;
using System.Net.WebSockets;

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
    /// The WebSocket connection to the online server (room system, protocol 4; see <see cref="WsHubConnection"/>).
    /// <para>
    /// Server messages arrive on thread-pool threads. Except Pong (timed on arrival for the clock sync)
    /// they are queued and raised on the game thread by <see cref="Update"/>, so handlers can touch game
    /// and UI state freely. Results of hub calls made with <see cref="Call{T}"/> are delivered the same way.
    /// </para>
    /// Room state itself is kept by <see cref="RoomClient"/>.
    /// </summary>
    public class NetworkManager
    {
        private static NetworkManager? _instance;
        public static NetworkManager Instance => _instance ??= new NetworkManager();

        // Connection state
        public bool IsConnected { get; private set; }
        public bool IsConnecting { get; private set; }
        public string Username { get; private set; } = "";
        public int Ping { get; private set; }
        public bool HasMeasuredPing { get; private set; }
        public ConnectionQuality Quality { get; private set; } = ConnectionQuality.Unknown;
        /// <summary>Largest level the server accepts for transfers (from Hello).</summary>
        public long MaxUploadBytes { get; private set; } = 100L * 1024 * 1024;
        /// <summary>Highest room player limit the server allows (from Hello).</summary>
        public int MaxRoomPlayers { get; private set; } = Protocol.MaxPlayers;
        /// <summary>True when the connected server supports chunked transfer uploads.</summary>
        public bool SupportsChunkedUpload { get; private set; }
        /// <summary>True when the connected server supports FNF chart-only variant transfers.</summary>
        public bool SupportsFnfChartOnly { get; private set; }
        /// <summary>Preferred chunk size in bytes for chunked uploads (default 4 MB).</summary>
        public int UploadChunkBytes { get; private set; } = 4 * 1024 * 1024;
        public string ServerVersion { get; private set; } = "";

        private const string HubEndpoint = "/multiplayer";
        private string? _serverUrlOverride;

        /// <summary>Base URL of the online server (no trailing slash).</summary>
        public string ServerUrl
        {
            get => _serverUrlOverride ?? MultiplayerSettings.Instance.EffectiveServerUrl;
            set => _serverUrlOverride = string.IsNullOrWhiteSpace(value) ? null : value.Trim().TrimEnd('/');
        }

        public string HubUrl => $"{ServerUrl}{HubEndpoint}";

        // Hub connection & cancellation tracking
        private WsHubConnection? _hubConnection;
        private CancellationTokenSource? _connectCts;
        private int _connectGeneration = 0;
        private readonly object _connectionStateLock = new object();
        private readonly ConcurrentQueue<Action> _events = new();

        // ── Connection events (game thread) ──────────────────────────────────
        public event Action? OnConnected;
        public event Action<string>? OnDisconnected;
        public event Action<string>? OnConnectionError;
        public event Action<ConnectionQuality>? OnQualityChanged;
        public event Action<int>? OnPingUpdate;
        public event Action<LevelListPacket>? OnLevelListReceived;
        public event Action<LeaderboardResponsePacket>? OnLeaderboardReceived;
        public event Action<MatchHistoryResponsePacket>? OnMatchHistoryReceived;
        /// <summary>The connection came back after a drop (Hello done again).</summary>
        public event Action? OnReconnected;

        // ── Room events (game thread) ────────────────────────────────────────
        public event Action<RoomStatePacket>? RoomStateReceived;
        public event Action<ChatMessage>? ChatReceived;
        public event Action<KickedPacket>? KickedReceived;
        public event Action<TransferUploadPacket>? TransferUploadReceived;
        public event Action<TransferDownloadPacket>? TransferDownloadReceived;
        public event Action<TransferFailedPacket>? TransferFailedReceived;
        public event Action<SongRejectedPacket>? SongRejectedReceived;
        public event Action<MatchInfo>? MatchLoadingReceived;
        public event Action<MatchStartPacket>? MatchStartReceived;
        public event Action<RoundResult>? MatchResultsReceived;
        public event Action<PlayerStatePacket>? PlayerStateReceived;
        public event Action<PlayerHitPacket>? PlayerHitReceived;
        public event Action<PlayerKeyPressPacket>? PlayerKeyPressReceived;

        // Ping measurement & quality tracking
        private readonly object _pingLock = new object();
        private float _pingTimer;
        private const float PING_INTERVAL = 2f; // seconds
        private const float PING_BOOTSTRAP_INTERVAL = 0.35f; // rapid initial clock sync samples
        private readonly List<int> _pingHistory = new List<int>();
        private const int PING_HISTORY_SIZE = 10;
        private float _lastPacketReceivedTime;
        private const float CONNECTION_TIMEOUT = 70f; // Match server ClientTimeoutInterval (60s) + 10s buffer
        private readonly System.Diagnostics.Stopwatch _loadPingWatch = System.Diagnostics.Stopwatch.StartNew();

        /// <summary>
        /// Keeps hub pings flowing while heavy main-thread level loading (e.g. SetupLevel on 5 GB levels)
        /// temporarily pauses the normal Update() loop, preventing false silence/connection timeouts.
        /// </summary>
        public void PumpKeepAliveDuringLoad()
        {
            if (!IsConnected) return;
            var hub = _hubConnection;
            if (hub == null || hub.State != HubConnectionState.Connected) return;
            _lastPacketReceivedTime = 0f;
            if (_loadPingWatch.ElapsedMilliseconds >= (long)(PING_INTERVAL * 1000f))
            {
                _loadPingWatch.Restart();
                _pingTimer = 0f;
                SendPing();
            }
        }

        // Reconnection with exponential backoff
        private bool _shouldReconnect = false;
        private float _reconnectTimer = 0f;
        private int _reconnectAttempts = 0;
        private const int MAX_RECONNECT_ATTEMPTS = 5;
        private float _currentReconnectDelay = 2f;
        private const float MIN_RECONNECT_DELAY = 2f;
        private const float MAX_RECONNECT_DELAY = 30f;

        // Connection timeout (the Workers server never sleeps, so this only covers bad networks).
        private float _connectionTimeoutTimer = 0f;
        private const float CONNECTION_ATTEMPT_TIMEOUT = 30f;

        // KeyPress throttling (prevent spam)
        private readonly Dictionary<int, long> _lastKeyPressSent = new Dictionary<int, long>();
        private const long KEY_PRESS_THROTTLE_MS = 50;

        private NetworkManager() { }

        /// <summary>Queues work to run on the game thread at the next <see cref="Update"/>.</summary>
        public void Post(Action action) => _events.Enqueue(action);

        private bool IsActiveAttemptLocked(WsHubConnection hub, int generation) =>
            generation == _connectGeneration && ReferenceEquals(_hubConnection, hub);

        private void DetachConnectionLocked(out WsHubConnection? hubToDispose, out CancellationTokenSource? ctsToDispose)
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
            if (cts == null) return;
            try { cts.Cancel(); } catch { }
            try { cts.Dispose(); } catch { }
        }

        private static async Task DisposeHubQuietlyAsync(WsHubConnection? hub)
        {
            if (hub == null) return;
            try { await hub.StopAsync().ConfigureAwait(false); } catch { }
            try { await hub.DisposeAsync().ConfigureAwait(false); } catch { }
        }

        private void HandleFailedInitialAttempt(WsHubConnection hub, int generation, string errorMessage)
        {
            WsHubConnection? hubToDispose = null;
            CancellationTokenSource? ctsToDispose = null;
            bool wasActiveAttempt;

            lock (_connectionStateLock)
            {
                wasActiveAttempt = IsActiveAttemptLocked(hub, generation);
                if (wasActiveAttempt)
                    DetachConnectionLocked(out hubToDispose, out ctsToDispose);
            }

            CancelAndDisposeCts(ctsToDispose);
            _ = DisposeHubQuietlyAsync(wasActiveAttempt ? hubToDispose : hub);

            if (wasActiveAttempt)
                Post(() => OnConnectionError?.Invoke(errorMessage));
        }

        private HelloRequest BuildHello()
        {
            var settings = MultiplayerSettings.Instance;
            return new HelloRequest
            {
                PlayerId = settings.PlayerId,
                Username = string.IsNullOrWhiteSpace(settings.Username) ? "Player" : settings.Username.Trim(),
                ColorHue = settings.PlayerColorHue,
                ClientVersion = typeof(NetworkManager).Assembly.GetName().Version?.ToString(3) ?? "",
                ProtocolVersion = Protocol.Version,
                OsuSkipVideo = settings.OsuSkipVideo,
                FnfChartOnly = settings.ChartOnlyMode,
            };
        }

        /// <summary>
        /// Connect to the online server and say Hello (protocol / profile).
        /// </summary>
        public async void Connect(string username)
        {
            WsHubConnection? staleHub = null;
            CancellationTokenSource? staleCts = null;
            WsHubConnection hub;
            CancellationTokenSource cts;
            int generation;

            lock (_connectionStateLock)
            {
                if (IsConnected || IsConnecting)
                {
                    Console.WriteLine("[Multiplayer] Already connected or connecting");
                    return;
                }

                if (_hubConnection != null || _connectCts != null)
                    DetachConnectionLocked(out staleHub, out staleCts);

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

                Console.WriteLine($"[Multiplayer] Connecting to {ServerUrl} as {username}...");

                hub = new WsHubConnection(HubUrl, new[]
                {
                    TimeSpan.FromSeconds(0),
                    TimeSpan.FromSeconds(2),
                    TimeSpan.FromSeconds(5),
                    TimeSpan.FromSeconds(10),
                    TimeSpan.FromSeconds(20),
                });
                hub.ServerTimeout = TimeSpan.FromSeconds(60);

                _hubConnection = hub;
            }

            CancelAndDisposeCts(staleCts);
            if (staleHub != null)
                _ = DisposeHubQuietlyAsync(staleHub);

            try
            {
                hub.Closed += error =>
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
                            if (wasConnected && _shouldReconnect && _reconnectAttempts < MAX_RECONNECT_ATTEMPTS)
                            {
                                _reconnectTimer = _currentReconnectDelay;
                                _reconnectAttempts++;
                                _currentReconnectDelay = Math.Min(_currentReconnectDelay * 1.5f, MAX_RECONNECT_DELAY);
                            }
                        }
                    }
                    if (!shouldHandle) return Task.CompletedTask;

                    string reason = error?.Message ?? "Connection closed";
                    Console.WriteLine($"[Multiplayer] Connection closed: {reason}");
                    Post(() => OnDisconnected?.Invoke(reason));
                    return Task.CompletedTask;
                };

                hub.Reconnecting += error =>
                {
                    lock (_connectionStateLock)
                    {
                        if (!IsActiveAttemptLocked(hub, generation)) return Task.CompletedTask;
                        IsConnected = false;
                        IsConnecting = true;
                    }
                    Console.WriteLine($"[Multiplayer] Reconnecting... {error?.Message ?? ""}");
                    return Task.CompletedTask;
                };

                hub.Reconnected += async connectionId =>
                {
                    lock (_connectionStateLock)
                    {
                        if (!IsActiveAttemptLocked(hub, generation)) return;
                    }
                    HelloResult? reconnectHello = null;
                    string? helloError = null;
                    try
                    {
                        reconnectHello = await hub.InvokeAsync<HelloResult>("Hello", BuildHello());
                        if (!reconnectHello.Ok)
                            helloError = string.IsNullOrWhiteSpace(reconnectHello.Error) ? "Server rejected reconnect Hello." : reconnectHello.Error;
                    }
                    catch (Exception ex)
                    {
                        helloError = ex.Message;
                        Console.WriteLine($"[Multiplayer] Hello after reconnect failed: {ex.Message}");
                    }
                    if (helloError != null || reconnectHello is not { Ok: true })
                    {
                        WsHubConnection? rejectedHub = null;
                        CancellationTokenSource? rejectedCts = null;
                        lock (_connectionStateLock)
                        {
                            if (!IsActiveAttemptLocked(hub, generation)) return;
                            DetachConnectionLocked(out rejectedHub, out rejectedCts);
                        }
                        CancelAndDisposeCts(rejectedCts);
                        _ = DisposeHubQuietlyAsync(rejectedHub);
                        Post(() =>
                        {
                            OnConnectionError?.Invoke($"Reconnect failed: {helloError ?? "Hello rejected"}");
                            OnDisconnected?.Invoke(helloError ?? "Reconnect rejected by server");
                        });
                        return;
                    }
                    lock (_connectionStateLock)
                    {
                        if (!IsActiveAttemptLocked(hub, generation)) return;
                        IsConnecting = false;
                        IsConnected = true;
                        _reconnectAttempts = 0;
                        _currentReconnectDelay = MIN_RECONNECT_DELAY;
                        _lastPacketReceivedTime = 0f;
                        _pingTimer = 0f;
                        MaxUploadBytes = reconnectHello.MaxUploadBytes > 0 ? reconnectHello.MaxUploadBytes : MaxUploadBytes;
                        MaxRoomPlayers = reconnectHello.MaxRoomPlayers >= Protocol.MinPlayers ? reconnectHello.MaxRoomPlayers : Protocol.MaxPlayers;
                        SupportsChunkedUpload = reconnectHello.SupportsChunkedUpload;
                        SupportsFnfChartOnly = reconnectHello.SupportsFnfChartOnly;
                        UploadChunkBytes = reconnectHello.UploadChunkBytes > 0 ? reconnectHello.UploadChunkBytes : 4 * 1024 * 1024;
                    }
                    Console.WriteLine($"[Multiplayer] Reconnected ({connectionId})");
                    SendPing();
                    Post(() => OnReconnected?.Invoke());
                };

                // Server → client. Queued for the game thread (Pong is handled right away).
                On<RoomStatePacket>(hub, generation, Protocol.RoomState, p => RoomStateReceived?.Invoke(p));
                On<ChatMessage>(hub, generation, Protocol.Chat, p => ChatReceived?.Invoke(p));
                On<KickedPacket>(hub, generation, Protocol.Kicked, p => KickedReceived?.Invoke(p));
                On<TransferUploadPacket>(hub, generation, Protocol.TransferUpload, p => TransferUploadReceived?.Invoke(p));
                On<TransferDownloadPacket>(hub, generation, Protocol.TransferDownload, p => TransferDownloadReceived?.Invoke(p));
                On<TransferFailedPacket>(hub, generation, Protocol.TransferFailed, p => TransferFailedReceived?.Invoke(p));
                On<SongRejectedPacket>(hub, generation, Protocol.SongRejected, p => SongRejectedReceived?.Invoke(p));
                On<MatchInfo>(hub, generation, Protocol.MatchLoading, p => MatchLoadingReceived?.Invoke(p));
                On<MatchStartPacket>(hub, generation, Protocol.MatchStart, p => MatchStartReceived?.Invoke(p));
                On<RoundResult>(hub, generation, Protocol.MatchResults, p => MatchResultsReceived?.Invoke(p));
                On<PlayerStatePacket>(hub, generation, Protocol.PlayerState, p => PlayerStateReceived?.Invoke(p));
                On<PlayerHitPacket>(hub, generation, Protocol.PlayerHit, p => PlayerHitReceived?.Invoke(p));
                On<PlayerKeyPressPacket>(hub, generation, Protocol.PlayerKeyPress, p => PlayerKeyPressReceived?.Invoke(p));
                On<LeaderboardResponsePacket>(hub, generation, "LeaderboardResponse", p => OnLeaderboardReceived?.Invoke(p));
                On<MatchHistoryResponsePacket>(hub, generation, "MatchHistoryResponse", p => OnMatchHistoryReceived?.Invoke(p));
                hub.On<PongPacket>(Protocol.Pong, p =>
                {
                    if (generation == _connectGeneration) OnPongPacket(p);
                });

                Console.WriteLine($"[Multiplayer] Opening WebSocket: {HubUrl}");
                await hub.StartAsync(cts.Token);

                var hello = await hub.InvokeAsync<HelloResult>("Hello", BuildHello(), cts.Token);
                if (!hello.Ok)
                {
                    HandleFailedInitialAttempt(hub, generation, string.IsNullOrWhiteSpace(hello.Error) ? "The server refused the connection." : hello.Error);
                    return;
                }

                bool promoted = false;
                CancellationTokenSource? completedCts = null;
                lock (_connectionStateLock)
                {
                    if (IsActiveAttemptLocked(hub, generation) && !cts.IsCancellationRequested)
                    {
                        IsConnecting = false;
                        IsConnected = true;
                        _reconnectAttempts = 0;
                        _currentReconnectDelay = MIN_RECONNECT_DELAY;
                        _lastPacketReceivedTime = 0f;
                        _pingTimer = 0f;
                        MaxUploadBytes = hello.MaxUploadBytes > 0 ? hello.MaxUploadBytes : MaxUploadBytes;
                        MaxRoomPlayers = hello.MaxRoomPlayers >= Protocol.MinPlayers ? hello.MaxRoomPlayers : Protocol.MaxPlayers;
                        SupportsChunkedUpload = hello.SupportsChunkedUpload;
                        SupportsFnfChartOnly = hello.SupportsFnfChartOnly;
                        UploadChunkBytes = hello.UploadChunkBytes > 0 ? hello.UploadChunkBytes : 4 * 1024 * 1024;
                        ServerVersion = hello.ServerVersion;
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

                Console.WriteLine($"[Multiplayer] [OK] Connected (server {hello.ServerVersion}, protocol {hello.ProtocolVersion})");
                SendPing();
                Post(() => OnConnected?.Invoke());
            }
            catch (OperationCanceledException)
            {
                Console.WriteLine("[Multiplayer] [X] Connection attempt timed out or was cancelled");
                HandleFailedInitialAttempt(hub, generation, "Connection timeout");
            }
            catch (Exception ex) when (ex is System.Net.Http.HttpRequestException or WebSocketException)
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
                HandleFailedInitialAttempt(hub, generation, $"Connection failed: {ex.Message}");
            }
        }

        private void On<T>(WsHubConnection hub, int generation, string method, Action<T> handler) =>
            hub.On<T>(method, payload =>
            {
                if (generation != _connectGeneration) return;
                _lastPacketReceivedTime = 0f;
                Post(() => handler(payload));
            });

        /// <summary>
        /// Disconnect from the server or abort any pending connection attempt.
        /// </summary>
        public async void Disconnect()
        {
            bool wasConnected;
            bool hadPendingState;
            WsHubConnection? hubToStop;
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
            if (!hadPendingState && hubToStop == null) return;

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
                TimeSyncManager.Instance.Reset();
                if (wasConnected)
                    Post(() => OnDisconnected?.Invoke("User disconnected"));
            }
        }

        // ══════════════════════════════════════════════════════════════════════
        // Hub calls
        // ══════════════════════════════════════════════════════════════════════

        /// <summary>
        /// Invokes a hub method; <paramref name="onResult"/> / <paramref name="onError"/> run on the game thread.
        /// </summary>
        public void Call<T>(string method, object?[] args, Action<T>? onResult, Action<string>? onError = null)
        {
            var hub = _hubConnection;
            if (hub == null || !IsConnected)
            {
                if (onError != null) Post(() => onError("Not connected to the server."));
                return;
            }
            _ = Task.Run(async () =>
            {
                try
                {
                    var result = await hub.InvokeCoreAsync<T>(method, args);
                    if (onResult != null) Post(() => onResult(result));
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"[Multiplayer] {method} failed: {ex.Message}");
                    if (onError != null) Post(() => onError(ex.Message));
                }
            });
        }

        /// <summary>Fire-and-forget hub method (errors are only logged).</summary>
        public void Send(string method, params object?[] args)
        {
            var hub = _hubConnection;
            if (hub == null || !IsConnected || hub.State != HubConnectionState.Connected) return;
            _ = Task.Run(async () =>
            {
                try { await hub.SendCoreAsync(method, args); }
                catch (Exception ex) { Console.WriteLine($"[Multiplayer] {method} failed: {ex.Message}"); }
            });
        }

        /// <summary>Sends the (changed) username / color again.</summary>
        public void ResendProfile() => Call<HelloResult>("Hello", new object?[] { BuildHello() }, null);

        // ── In-match relay ───────────────────────────────────────────────────

        public void SendState(PlayerStatePacket packet) => Send("SendState", packet);

        public void SendHit(int lane, string rating, float holdEnd, float songTime) =>
            Send("SendHit", new PlayerHitPacket { Lane = lane, Rating = rating, HoldEnd = holdEnd, SongTime = songTime });

        /// <summary>Key press for the other players' visuals. Throttled to one per 50 ms per lane.</summary>
        public void SendKeyPress(int lane, float songTime)
        {
            long now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            if (_lastKeyPressSent.TryGetValue(lane, out long last) && now - last < KEY_PRESS_THROTTLE_MS) return;
            _lastKeyPressSent[lane] = now;
            Send("SendKeyPress", new PlayerKeyPressPacket { Lane = lane, SongTime = songTime });
        }

        // ── Official levels (HTTP) ───────────────────────────────────────────

        private static readonly System.Net.Http.HttpClient _officialLevelsHttp = new() { Timeout = TimeSpan.FromSeconds(100) };

        /// <summary>Fetches /api/verified_levels; raises <see cref="OnLevelListReceived"/> on the game thread.</summary>
        public async void RequestOfficialLevels()
        {
            try
            {
                var response = await _officialLevelsHttp.GetStringAsync($"{ServerUrl}/api/verified_levels");
                var options = new System.Text.Json.JsonSerializerOptions { PropertyNameCaseInsensitive = true };
                var packet = System.Text.Json.JsonSerializer.Deserialize<LevelListPacket>(response, options);
                if (packet?.Levels != null)
                {
                    Console.WriteLine($"[Multiplayer] Received {packet.Levels.Count} official levels");
                    Post(() => OnLevelListReceived?.Invoke(packet));
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[Multiplayer] Error fetching official levels: {ex.Message}");
            }
        }

        // ── Leaderboard ──────────────────────────────────────────────────────

        public void RequestGlobalLeaderboard(int skip = 0, int take = 100) =>
            Send("GetLeaderboard", new GetLeaderboardPacket { Skip = skip, Take = take, Timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() });

        public void RequestMatchHistory(string? playerId = null, int skip = 0, int take = 50) =>
            Send("GetMatchHistory", new GetMatchHistoryPacket { PlayerId = playerId, Skip = skip, Take = take, Timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() });

        // ══════════════════════════════════════════════════════════════════════
        // Ping, health, reconnect
        // ══════════════════════════════════════════════════════════════════════

        private async void SendPing()
        {
            var hub = _hubConnection;
            if (hub == null || hub.State != HubConnectionState.Connected) return;
            try
            {
                await hub.SendAsync("Ping", new PingPacket { ClientTimestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() });
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[Multiplayer] SendPing error: {ex.Message}");
            }
        }

        /// <summary>
        /// Called every frame from Game1: raises queued server messages, pings, connection health, reconnects.
        /// </summary>
        public void Update(GameTime gameTime)
        {
            // Clamp dt so a long synchronous SetupLevel() frame cannot jump _lastPacketReceivedTime
            // straight past CONNECTION_TIMEOUT and drop an otherwise healthy connection.
            float dt = Math.Min((float)gameTime.ElapsedGameTime.TotalSeconds, 0.5f);

            while (_events.TryDequeue(out var action))
            {
                try { action(); }
                catch (Exception ex) { Console.WriteLine($"[Multiplayer] Event handler error: {ex}"); }
            }

            bool timedOut = false;
            WsHubConnection? timedOutHub = null;
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

            if (IsConnected)
            {
                _lastPacketReceivedTime += dt;
                if (_lastPacketReceivedTime >= CONNECTION_TIMEOUT)
                {
                    Console.WriteLine("[Multiplayer] Connection lost - no packets received");
                    WsHubConnection? staleHub = null;
                    CancellationTokenSource? staleCts = null;
                    lock (_connectionStateLock)
                    {
                        DetachConnectionLocked(out staleHub, out staleCts);
                    }
                    CancelAndDisposeCts(staleCts);
                    _ = DisposeHubQuietlyAsync(staleHub);
                    OnConnectionError?.Invoke("Connection lost");
                    OnDisconnected?.Invoke("Connection lost - no response from server");
                    if (_shouldReconnect) _reconnectTimer = _currentReconnectDelay;
                }
                else
                {
                    float pingInterval = TimeSyncManager.Instance.IsSynced ? PING_INTERVAL : PING_BOOTSTRAP_INTERVAL;
                    _pingTimer += dt;
                    if (_pingTimer >= pingInterval)
                    {
                        _pingTimer = 0f;
                        SendPing();
                    }
                    UpdateConnectionQuality();
                }
            }

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
                    Quality = ConnectionQuality.Unknown;
                    return;
                }

                int sum = 0;
                foreach (int p in _pingHistory) sum += p;
                avgPing = sum / _pingHistory.Count;

                int variance = 0;
                foreach (int p in _pingHistory)
                {
                    int diff = p - avgPing;
                    variance += diff * diff;
                }
                variance /= _pingHistory.Count;
                stdDev = (int)Math.Sqrt(variance);

                bool isStable = stdDev < 30;
                if (avgPing < 50 && isStable) newQuality = ConnectionQuality.Excellent;
                else if (avgPing < 100 && isStable) newQuality = ConnectionQuality.Good;
                else if (avgPing < 200) newQuality = ConnectionQuality.Fair;
                else if (avgPing < 500) newQuality = ConnectionQuality.Poor;
                else newQuality = ConnectionQuality.Critical;

                if (newQuality != Quality)
                {
                    Quality = newQuality;
                    changed = true;
                }
            }

            if (changed)
            {
                Console.WriteLine($"[Multiplayer] Connection quality: {newQuality} (Ping: {avgPing}ms, StdDev: {stdDev}ms)");
                OnQualityChanged?.Invoke(newQuality);
            }
        }

        private void OnPongPacket(PongPacket packet)
        {
            long now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            int currentPing = (int)Math.Clamp(now - packet.ClientTimestamp, 0, 60000);
            Ping = currentPing;
            HasMeasuredPing = true;

            if (packet.ServerTimestamp > 0)
                TimeSyncManager.Instance.UpdateClockOffset(packet.ClientTimestamp, packet.ServerTimestamp, packet.ServerTimestamp, now);
            else
                TimeSyncManager.Instance.UpdateClockOffsetSimple(packet.ClientTimestamp, currentPing);

            lock (_pingLock)
            {
                _pingHistory.Add(currentPing);
                if (_pingHistory.Count > PING_HISTORY_SIZE)
                    _pingHistory.RemoveAt(0);
            }

            _lastPacketReceivedTime = 0f;
            Post(() => OnPingUpdate?.Invoke(currentPing));
        }

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

        public string GetConnectionDiagnostics()
        {
            if (!IsConnected && !IsConnecting) return "Not connected";
            if (IsConnecting) return $"Connecting... (Attempt {_reconnectAttempts + 1}/{MAX_RECONNECT_ATTEMPTS})";
            string status = $"Connected | Ping: {Ping}ms | Quality: {Quality}";
            if (_hubConnection != null) status += $" | State: {_hubConnection.State}";
            return status;
        }

        public void ForceReset()
        {
            WsHubConnection? hubToReset;
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
            _ = DisposeHubQuietlyAsync(hubToReset);
        }

        public void Cleanup()
        {
            if (IsConnected || IsConnecting || _hubConnection != null || _connectCts != null)
                Disconnect();
        }
    }
}
