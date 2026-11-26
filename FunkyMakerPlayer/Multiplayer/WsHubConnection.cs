#nullable enable
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace FunkyMakerPlayer.Multiplayer
{
    public enum HubConnectionState { Disconnected, Connecting, Connected, Reconnecting }

    /// <summary>
    /// Client for the online server's WebSocket hub (protocol 4, Cloudflare Workers). Replaces the
    /// SignalR client with the same shape (Invoke / Send / On / automatic reconnect), so
    /// <see cref="NetworkManager"/> barely changed. Every frame is JSON text:
    /// <code>
    /// → {"type":"invoke","id":7,"method":"JoinRoom","args":[...]}     (no id = fire and forget)
    /// ← {"type":"result","id":7,"result":...} / {"type":"result","id":7,"error":"..."}
    /// ← {"type":"event","method":"RoomState","payload":{...}}
    /// ← {"type":"ping"}                                              (every 10 s)
    /// ↔ {"type":"frag","fid":"a1","i":0,"n":3,"data":"..."}          (big messages, split)
    /// </code>
    /// Handlers registered with <see cref="On{T}"/> run on the receive thread.
    /// </summary>
    public sealed class WsHubConnection : IAsyncDisposable
    {
        /// <summary>Same JSON settings the SignalR client used (camelCase out, any casing in).</summary>
        public static readonly JsonSerializerOptions Json = new()
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            PropertyNameCaseInsensitive = true,
        };

        // Outgoing fragments stay well under the Workers WebSocket message limit even after JSON escaping.
        private const int FragmentChars = 64 * 1024;
        private const long MaxIncomingBytes = 64L * 1024 * 1024;

        private readonly Uri _uri;
        private readonly TimeSpan[] _reconnectDelays;
        private readonly Dictionary<string, (Type Type, Action<object?> Handler)> _handlers = new(StringComparer.Ordinal);
        private readonly ConcurrentDictionary<long, TaskCompletionSource<JsonElement>> _pending = new();
        private readonly Dictionary<string, (string?[] Parts, int Received)> _fragments = new(StringComparer.Ordinal);
        private readonly SemaphoreSlim _sendLock = new(1, 1);
        private readonly CancellationTokenSource _lifetime = new();
        private ClientWebSocket? _socket;
        private long _nextId;
        private int _stopped;
        private DateTime _lastReceivedUtc = DateTime.UtcNow;

        public HubConnectionState State { get; private set; } = HubConnectionState.Disconnected;
        /// <summary>The connection counts as dropped after this long without any message (the server pings every 10 s).</summary>
        public TimeSpan ServerTimeout { get; set; } = TimeSpan.FromSeconds(60);

        /// <summary>The connection ended for good (stopped, or every reconnect attempt failed).</summary>
        public event Func<Exception?, Task>? Closed;
        public event Func<Exception?, Task>? Reconnecting;
        /// <summary>A new socket is open after a drop. Say Hello again: the server sees a new connection.</summary>
        public event Func<string?, Task>? Reconnected;

        public WsHubConnection(string httpUrl, TimeSpan[] reconnectDelays)
        {
            string ws = httpUrl.StartsWith("https://", StringComparison.OrdinalIgnoreCase) ? "wss://" + httpUrl[8..]
                      : httpUrl.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ? "ws://" + httpUrl[7..]
                      : httpUrl;
            _uri = new Uri(ws);
            _reconnectDelays = reconnectDelays;
        }

        public void On<T>(string method, Action<T> handler)
        {
            lock (_handlers) _handlers[method] = (typeof(T), o => handler((T)o!));
        }

        public async Task StartAsync(CancellationToken ct = default)
        {
            State = HubConnectionState.Connecting;
            try
            {
                _socket = await OpenAsync(ct).ConfigureAwait(false);
            }
            catch
            {
                State = HubConnectionState.Disconnected;
                throw;
            }
            State = HubConnectionState.Connected;
            StartLoops(_socket);
        }

        private async Task<ClientWebSocket> OpenAsync(CancellationToken ct)
        {
            var socket = new ClientWebSocket();
            socket.Options.KeepAliveInterval = TimeSpan.FromSeconds(20);
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, _lifetime.Token);
            try
            {
                await socket.ConnectAsync(_uri, linked.Token).ConfigureAwait(false);
            }
            catch
            {
                socket.Dispose();
                throw;
            }
            _lastReceivedUtc = DateTime.UtcNow;
            lock (_fragments) _fragments.Clear();
            return socket;
        }

        private void StartLoops(ClientWebSocket socket)
        {
            _ = Task.Run(() => ReceiveLoopAsync(socket));
            _ = Task.Run(() => WatchdogAsync(socket));
        }

        // ── Sending ──────────────────────────────────────────────────────────

        public Task<T> InvokeAsync<T>(string method, object? arg, CancellationToken ct = default) =>
            InvokeCoreAsync<T>(method, new[] { arg }, ct);

        public async Task<T> InvokeCoreAsync<T>(string method, object?[] args, CancellationToken ct = default)
        {
            long id = Interlocked.Increment(ref _nextId);
            var tcs = new TaskCompletionSource<JsonElement>(TaskCreationOptions.RunContinuationsAsynchronously);
            _pending[id] = tcs;
            try
            {
                await SendTextAsync(Serialize(id, method, args), ct).ConfigureAwait(false);
                using (ct.Register(() => tcs.TrySetCanceled(ct)))
                {
                    var result = await tcs.Task.ConfigureAwait(false);
                    return result.ValueKind is JsonValueKind.Undefined or JsonValueKind.Null
                        ? default!
                        : result.Deserialize<T>(Json)!;
                }
            }
            finally
            {
                _pending.TryRemove(id, out _);
            }
        }

        public Task SendCoreAsync(string method, object?[] args, CancellationToken ct = default) =>
            SendTextAsync(Serialize(null, method, args), ct);

        public Task SendAsync(string method, object? arg, CancellationToken ct = default) =>
            SendCoreAsync(method, new[] { arg }, ct);

        private static string Serialize(long? id, string method, object?[] args)
        {
            using var ms = new MemoryStream();
            using (var w = new Utf8JsonWriter(ms))
            {
                w.WriteStartObject();
                w.WriteString("type", "invoke");
                if (id.HasValue) w.WriteNumber("id", id.Value);
                w.WriteString("method", method);
                w.WriteStartArray("args");
                foreach (var a in args)
                {
                    if (a == null) w.WriteNullValue();
                    else JsonSerializer.Serialize(w, a, a.GetType(), Json);
                }
                w.WriteEndArray();
                w.WriteEndObject();
            }
            return Encoding.UTF8.GetString(ms.GetBuffer(), 0, (int)ms.Length);
        }

        private async Task SendTextAsync(string text, CancellationToken ct)
        {
            var socket = _socket;
            if (socket == null || State != HubConnectionState.Connected || socket.State != WebSocketState.Open)
                throw new InvalidOperationException("Not connected to the server.");

            await _sendLock.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                if (text.Length <= FragmentChars)
                {
                    await SendFrameAsync(socket, text, ct).ConfigureAwait(false);
                    return;
                }
                // Big message (thousands of osu! difficulties): split it, the server joins the parts.
                var parts = new List<string>();
                for (int start = 0; start < text.Length;)
                {
                    int len = Math.Min(FragmentChars, text.Length - start);
                    if (start + len < text.Length && char.IsHighSurrogate(text[start + len - 1])) len--;
                    parts.Add(text.Substring(start, len));
                    start += len;
                }
                string fid = Guid.NewGuid().ToString("N")[..12];
                for (int i = 0; i < parts.Count; i++)
                {
                    string frag = JsonSerializer.Serialize(new { type = "frag", fid, i, n = parts.Count, data = parts[i] });
                    await SendFrameAsync(socket, frag, ct).ConfigureAwait(false);
                }
            }
            finally
            {
                _sendLock.Release();
            }
        }

        private static Task SendFrameAsync(ClientWebSocket socket, string text, CancellationToken ct) =>
            socket.SendAsync(Encoding.UTF8.GetBytes(text), WebSocketMessageType.Text, true, ct);

        // ── Receiving ────────────────────────────────────────────────────────

        private async Task ReceiveLoopAsync(ClientWebSocket socket)
        {
            var buffer = new byte[64 * 1024];
            var message = new MemoryStream();
            Exception? error = null;
            try
            {
                while (socket.State == WebSocketState.Open && !_lifetime.IsCancellationRequested)
                {
                    var r = await socket.ReceiveAsync(buffer, _lifetime.Token).ConfigureAwait(false);
                    if (r.MessageType == WebSocketMessageType.Close)
                    {
                        error = new WebSocketException($"The server closed the connection ({socket.CloseStatusDescription ?? r.CloseStatus?.ToString()}).");
                        break;
                    }
                    message.Write(buffer, 0, r.Count);
                    if (message.Length > MaxIncomingBytes)
                        throw new InvalidDataException("Message too large.");
                    if (!r.EndOfMessage) continue;

                    _lastReceivedUtc = DateTime.UtcNow;
                    string text = Encoding.UTF8.GetString(message.GetBuffer(), 0, (int)message.Length);
                    message.SetLength(0);
                    if (message.Capacity > 4 * 1024 * 1024) message = new MemoryStream();
                    try { HandleText(text); }
                    catch (Exception ex) { Console.WriteLine($"[Multiplayer] Bad server message: {ex.Message}"); }
                }
            }
            catch (OperationCanceledException) when (_lifetime.IsCancellationRequested) { }
            catch (Exception ex) { error = ex; }

            if (!ReferenceEquals(socket, _socket)) return; // superseded
            await OnSocketLostAsync(socket, error ?? new WebSocketException("Connection closed.")).ConfigureAwait(false);
        }

        private void HandleText(string text)
        {
            using var doc = JsonDocument.Parse(text);
            var root = doc.RootElement;
            string type = root.TryGetProperty("type", out var t) ? t.GetString() ?? "" : "";
            switch (type)
            {
                case "event":
                {
                    string method = root.TryGetProperty("method", out var m) ? m.GetString() ?? "" : "";
                    (Type Type, Action<object?> Handler) h;
                    lock (_handlers)
                        if (!_handlers.TryGetValue(method, out h)) return;
                    object? payload = root.TryGetProperty("payload", out var p) && p.ValueKind != JsonValueKind.Null
                        ? p.Deserialize(h.Type, Json)
                        : null;
                    try { h.Handler(payload); }
                    catch (Exception ex) { Console.WriteLine($"[Multiplayer] {method} handler failed: {ex}"); }
                    break;
                }
                case "result":
                {
                    if (!root.TryGetProperty("id", out var idEl) || !idEl.TryGetInt64(out long id)) return;
                    if (!_pending.TryGetValue(id, out var tcs)) return;
                    if (root.TryGetProperty("error", out var err) && err.ValueKind == JsonValueKind.String)
                        tcs.TrySetException(new InvalidOperationException(err.GetString()));
                    else
                        tcs.TrySetResult(root.TryGetProperty("result", out var res) ? res.Clone() : default);
                    break;
                }
                case "frag":
                {
                    string fid = root.GetProperty("fid").GetString() ?? "";
                    int i = root.GetProperty("i").GetInt32();
                    int n = root.GetProperty("n").GetInt32();
                    string data = root.GetProperty("data").GetString() ?? "";
                    if (n < 1 || n > 4096 || i < 0 || i >= n) return;
                    string? whole = null;
                    lock (_fragments)
                    {
                        if (!_fragments.TryGetValue(fid, out var entry) || entry.Parts.Length != n)
                        {
                            if (_fragments.Count >= 8) _fragments.Clear(); // abandoned partial messages
                            entry = (new string?[n], 0);
                        }
                        if (entry.Parts[i] == null) entry.Received++;
                        entry.Parts[i] = data;
                        if (entry.Received == n)
                        {
                            _fragments.Remove(fid);
                            whole = string.Concat(entry.Parts);
                        }
                        else _fragments[fid] = entry;
                    }
                    if (whole != null) HandleText(whole);
                    break;
                }
                // "ping" and anything unknown: receiving it already refreshed _lastReceivedUtc.
            }
        }

        private async Task WatchdogAsync(ClientWebSocket socket)
        {
            try
            {
                while (ReferenceEquals(socket, _socket) && socket.State == WebSocketState.Open)
                {
                    await Task.Delay(2000, _lifetime.Token).ConfigureAwait(false);
                    if (DateTime.UtcNow - _lastReceivedUtc > ServerTimeout)
                    {
                        Console.WriteLine("[Multiplayer] No message from the server for too long; dropping the socket.");
                        socket.Abort(); // the receive loop notices and reconnects
                        return;
                    }
                }
            }
            catch (OperationCanceledException) { }
        }

        // ── Drops and reconnects ─────────────────────────────────────────────

        private void FailPending(Exception error)
        {
            foreach (var kv in _pending)
                kv.Value.TrySetException(error);
        }

        private async Task OnSocketLostAsync(ClientWebSocket socket, Exception error)
        {
            try { socket.Dispose(); } catch { }
            FailPending(new IOException("The connection to the server was lost.", error));
            if (Volatile.Read(ref _stopped) != 0) return;

            if (_reconnectDelays.Length > 0)
            {
                State = HubConnectionState.Reconnecting;
                await RaiseAsync(Reconnecting, error).ConfigureAwait(false);
                foreach (var delay in _reconnectDelays)
                {
                    try { await Task.Delay(delay, _lifetime.Token).ConfigureAwait(false); }
                    catch (OperationCanceledException) { return; }
                    if (Volatile.Read(ref _stopped) != 0) return;
                    try
                    {
                        using var attempt = new CancellationTokenSource(TimeSpan.FromSeconds(30));
                        var fresh = await OpenAsync(attempt.Token).ConfigureAwait(false);
                        if (Volatile.Read(ref _stopped) != 0) { fresh.Dispose(); return; }
                        _socket = fresh;
                        State = HubConnectionState.Connected;
                        StartLoops(fresh);
                        var handlers = Reconnected;
                        if (handlers != null)
                            foreach (Func<string?, Task> h in handlers.GetInvocationList())
                                try { await h(null).ConfigureAwait(false); } catch (Exception ex) { Console.WriteLine($"[Multiplayer] Reconnected handler: {ex.Message}"); }
                        return;
                    }
                    catch (Exception ex)
                    {
                        error = ex;
                        Console.WriteLine($"[Multiplayer] Reconnect attempt failed: {ex.Message}");
                    }
                }
            }

            _socket = null;
            State = HubConnectionState.Disconnected;
            await RaiseAsync(Closed, error).ConfigureAwait(false);
        }

        private static async Task RaiseAsync(Func<Exception?, Task>? evt, Exception? error)
        {
            if (evt == null) return;
            foreach (Func<Exception?, Task> h in evt.GetInvocationList())
                try { await h(error).ConfigureAwait(false); } catch (Exception ex) { Console.WriteLine($"[Multiplayer] Connection handler: {ex.Message}"); }
        }

        public async Task StopAsync()
        {
            if (Interlocked.Exchange(ref _stopped, 1) != 0) return;
            var socket = _socket;
            _socket = null;
            State = HubConnectionState.Disconnected;
            try { _lifetime.Cancel(); } catch { }
            FailPending(new OperationCanceledException("The connection was stopped."));
            if (socket != null)
            {
                try
                {
                    if (socket.State == WebSocketState.Open)
                    {
                        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(2));
                        await socket.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, "bye", cts.Token).ConfigureAwait(false);
                    }
                }
                catch { }
                try { socket.Dispose(); } catch { }
            }
            await RaiseAsync(Closed, null).ConfigureAwait(false);
        }

        public async ValueTask DisposeAsync()
        {
            await StopAsync().ConfigureAwait(false);
            try { _lifetime.Dispose(); } catch { }
        }
    }
}
