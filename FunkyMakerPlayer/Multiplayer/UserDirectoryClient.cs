using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace FunkyMakerPlayer.Multiplayer
{
    /// <summary>
    /// Cached database user record resolved from server REST APIs (/api/users, /api/leaderboard)
    /// and live SignalR profile packets.
    /// </summary>
    public sealed class CachedUserRecord
    {
        public string PlayerId { get; init; } = "";
        public string Username { get; set; } = "";
        public int ColorHue { get; set; } = 180;
        public long LastSeenTimestamp { get; set; }
        public DateTime CachedAtUtc { get; set; } = DateTime.UtcNow;
    }

    /// <summary>
    /// Fetches and caches multiplayer player usernames from the backend database without blocking the render thread.
    /// Supports both the dedicated /api/users endpoints and backward-compatible extraction from /api/leaderboard.
    /// </summary>
    public sealed class UserDirectoryClient
    {
        private static readonly Lazy<UserDirectoryClient> _lazy = new(() => new UserDirectoryClient());
        public static UserDirectoryClient Instance => _lazy.Value;

        private static readonly HttpClient _http = new()
        {
            Timeout = TimeSpan.FromSeconds(8)
        };

        private readonly ConcurrentDictionary<string, CachedUserRecord> _usersById = new(StringComparer.Ordinal);
        private readonly ConcurrentDictionary<string, byte> _inFlightPlayerLookups = new(StringComparer.Ordinal);
        private int _warmInFlight = 0;
        private long _lastWarmTicks = 0;
        private int _usersEndpointState = 0; // 0 = unknown, 1 = supported, -1 = unsupported (404)

        public event Action? OnUsersUpdated;

        private UserDirectoryClient() { }

        public int CachedCount => _usersById.Count;

        public static bool IsGenericUsername(string? username)
        {
            if (string.IsNullOrWhiteSpace(username))
                return true;

            string trimmed = username.Trim();
            return trimmed.Equals("Player", StringComparison.OrdinalIgnoreCase)
                || trimmed.Equals("Opponent", StringComparison.OrdinalIgnoreCase)
                || trimmed.Equals("Unknown", StringComparison.OrdinalIgnoreCase)
                || trimmed.Equals("Player 1", StringComparison.OrdinalIgnoreCase)
                || trimmed.Equals("Player 2", StringComparison.OrdinalIgnoreCase);
        }

        public static string NormalizeBaseUrl(string? serverUrl)
        {
            string url = string.IsNullOrWhiteSpace(serverUrl)
                ? NetworkManager.Instance.ServerUrl
                : serverUrl;

            if (string.IsNullOrWhiteSpace(url))
                url = MultiplayerSettings.DefaultServerUrl;

            url = url.Trim().TrimEnd('/');
            if (url.EndsWith("/multiplayer", StringComparison.OrdinalIgnoreCase))
                url = url[..^"/multiplayer".Length].TrimEnd('/');

            return url;
        }

        public bool RegisterKnownUser(string? playerId, string? username, int colorHue = 180, long lastSeenTimestamp = 0)
        {
            if (string.IsNullOrWhiteSpace(playerId) || string.IsNullOrWhiteSpace(username))
                return false;

            string cleanId = playerId.Trim();
            string cleanName = username.Trim();
            bool incomingGeneric = IsGenericUsername(cleanName);
            bool updated = false;

            _usersById.AddOrUpdate(
                cleanId,
                _ =>
                {
                    updated = !incomingGeneric;
                    return new CachedUserRecord
                    {
                        PlayerId = cleanId,
                        Username = cleanName,
                        ColorHue = colorHue,
                        LastSeenTimestamp = lastSeenTimestamp,
                        CachedAtUtc = DateTime.UtcNow
                    };
                },
                (_, existing) =>
                {
                    bool existingGeneric = IsGenericUsername(existing.Username);
                    bool shouldReplaceName = !incomingGeneric && (existingGeneric || lastSeenTimestamp >= existing.LastSeenTimestamp);
                    if (shouldReplaceName && !string.Equals(existing.Username, cleanName, StringComparison.Ordinal))
                    {
                        existing.Username = cleanName;
                        updated = true;
                    }
                    existing.ColorHue = colorHue;
                    if (lastSeenTimestamp > existing.LastSeenTimestamp)
                        existing.LastSeenTimestamp = lastSeenTimestamp;
                    existing.CachedAtUtc = DateTime.UtcNow;
                    return existing;
                });

            if (updated)
                OnUsersUpdated?.Invoke();

            return updated;
        }

        public string ResolveUsername(string? playerId, string? fallbackName = null)
        {
            string? cleanFallback = string.IsNullOrWhiteSpace(fallbackName) ? null : fallbackName.Trim();

            if (!IsGenericUsername(cleanFallback))
                return cleanFallback!;

            if (!string.IsNullOrWhiteSpace(playerId))
            {
                string trimmedId = playerId.Trim();
                if (_usersById.TryGetValue(trimmedId, out var cached) && !IsGenericUsername(cached.Username))
                    return cached.Username;

                var localStats = LeaderboardManager.Instance.GetPlayerStats(trimmedId, cleanFallback);
                if (localStats != null && !IsGenericUsername(localStats.Username))
                    return localStats.Username.Trim();
            }

            if (!string.IsNullOrWhiteSpace(cleanFallback))
                return cleanFallback;

            if (!string.IsNullOrWhiteSpace(playerId) && _usersById.TryGetValue(playerId.Trim(), out var anyCached) && !string.IsNullOrWhiteSpace(anyCached.Username))
                return anyCached.Username;

            return "Opponent";
        }

        public void SeedFromLeaderboardEntries(IEnumerable<LeaderboardEntryPacket>? entries)
        {
            if (entries == null) return;
            foreach (var entry in entries)
            {
                if (entry == null) continue;
                RegisterKnownUser(entry.PlayerId, entry.Username, entry.ColorHue, entry.LastPlayedTimestamp);
            }
        }

        public void SeedFromMatchHistory(IEnumerable<MatchHistoryPacket>? matches)
        {
            if (matches == null) return;
            foreach (var match in matches)
            {
                if (match == null) continue;
                if (match.Player1 != null)
                    RegisterKnownUser(match.Player1.PlayerId, match.Player1.Username, match.Player1.ColorHue, match.Timestamp);
                if (match.Player2 != null)
                    RegisterKnownUser(match.Player2.PlayerId, match.Player2.Username, match.Player2.ColorHue, match.Timestamp);
            }
        }

        public async Task WarmFromServerAsync(string? serverUrl = null, bool force = false)
        {
            long nowTicks = DateTime.UtcNow.Ticks;
            long prevTicks = Interlocked.Read(ref _lastWarmTicks);
            if (!force && prevTicks > 0 && (nowTicks - prevTicks) < TimeSpan.FromSeconds(15).Ticks)
                return;

            if (Interlocked.CompareExchange(ref _warmInFlight, 1, 0) != 0)
                return;

            try
            {
                Interlocked.Exchange(ref _lastWarmTicks, nowTicks);
                string baseUrl = NormalizeBaseUrl(serverUrl);
                bool anyChanges = false;

                if (Volatile.Read(ref _usersEndpointState) != -1)
                {
                    try
                    {
                        using var response = await _http.GetAsync($"{baseUrl}/api/users?take=200").ConfigureAwait(false);
                        if (response.StatusCode == HttpStatusCode.NotFound)
                        {
                            Volatile.Write(ref _usersEndpointState, -1);
                        }
                        else if (response.IsSuccessStatusCode)
                        {
                            Volatile.Write(ref _usersEndpointState, 1);
                            string body = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
                            if (IngestApiPayloadJson(body) > 0)
                                anyChanges = true;
                        }
                    }
                    catch
                    {
                        // Network issue or cold server; fallback below still runs
                    }
                }

                try
                {
                    using var lbResponse = await _http.GetAsync($"{baseUrl}/api/leaderboard?take=100&matchesTake=100").ConfigureAwait(false);
                    if (lbResponse.IsSuccessStatusCode)
                    {
                        string body = await lbResponse.Content.ReadAsStringAsync().ConfigureAwait(false);
                        if (IngestApiPayloadJson(body) > 0)
                            anyChanges = true;
                    }
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"[UserDirectory] Warm fetch failed: {ex.Message}");
                }

                if (anyChanges)
                    OnUsersUpdated?.Invoke();
            }
            finally
            {
                Interlocked.Exchange(ref _warmInFlight, 0);
            }
        }

        public async Task<string?> FetchUserByIdAsync(string? serverUrl, string? playerId)
        {
            if (string.IsNullOrWhiteSpace(playerId))
                return null;

            string trimmedId = playerId.Trim();
            if (_usersById.TryGetValue(trimmedId, out var cached) &&
                !IsGenericUsername(cached.Username) &&
                (DateTime.UtcNow - cached.CachedAtUtc) < TimeSpan.FromSeconds(45))
            {
                return cached.Username;
            }

            if (!_inFlightPlayerLookups.TryAdd(trimmedId, 1))
                return cached?.Username;

            try
            {
                string baseUrl = NormalizeBaseUrl(serverUrl);
                bool updated = false;

                if (Volatile.Read(ref _usersEndpointState) != -1)
                {
                    try
                    {
                        using var response = await _http.GetAsync($"{baseUrl}/api/users/{Uri.EscapeDataString(trimmedId)}").ConfigureAwait(false);
                        if (response.StatusCode == HttpStatusCode.NotFound)
                        {
                            // Distinguish route 404 vs user-not-found by leaving endpoint state untouched unless /api/users fails
                        }
                        else if (response.IsSuccessStatusCode)
                        {
                            Volatile.Write(ref _usersEndpointState, 1);
                            string body = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
                            if (IngestApiPayloadJson(body) > 0)
                                updated = true;
                        }
                    }
                    catch
                    {
                        // Ignore and fall back to /api/leaderboard query
                    }
                }

                if (!_usersById.TryGetValue(trimmedId, out var resolvedAfterDirect) || IsGenericUsername(resolvedAfterDirect.Username))
                {
                    try
                    {
                        string lbUrl = $"{baseUrl}/api/leaderboard?playerId={Uri.EscapeDataString(trimmedId)}&take=100&matchesTake=50";
                        using var lbResponse = await _http.GetAsync(lbUrl).ConfigureAwait(false);
                        if (lbResponse.IsSuccessStatusCode)
                        {
                            string body = await lbResponse.Content.ReadAsStringAsync().ConfigureAwait(false);
                            if (IngestApiPayloadJson(body) > 0)
                                updated = true;
                        }
                    }
                    catch (Exception ex)
                    {
                        Console.WriteLine($"[UserDirectory] Player lookup failed for {trimmedId}: {ex.Message}");
                    }
                }

                if (updated)
                    OnUsersUpdated?.Invoke();

                return _usersById.TryGetValue(trimmedId, out var finalUser) ? finalUser.Username : null;
            }
            finally
            {
                _inFlightPlayerLookups.TryRemove(trimmedId, out _);
            }
        }

        /// <summary>
        /// Parses JSON payloads returned by /api/users, /api/users/{id}, or /api/leaderboard
        /// and registers every discovered PlayerId -> Username mapping without firing intermediate events.
        /// Returns the number of newly added/updated non-generic user records.
        /// </summary>
        internal int IngestApiPayloadJson(string json)
        {
            if (string.IsNullOrWhiteSpace(json))
                return 0;

            int updates = 0;
            try
            {
                using var doc = JsonDocument.Parse(json);
                var root = doc.RootElement;

                if (TryGetPropertyIgnoreCase(root, "User", out var singleUser) && singleUser.ValueKind == JsonValueKind.Object)
                    updates += IngestUserObjectSilent(singleUser);

                if (TryGetPropertyIgnoreCase(root, "Users", out var usersArr) && usersArr.ValueKind == JsonValueKind.Array)
                {
                    foreach (var el in usersArr.EnumerateArray())
                        updates += IngestUserObjectSilent(el);
                }

                if (TryGetPropertyIgnoreCase(root, "Entries", out var entriesArr) && entriesArr.ValueKind == JsonValueKind.Array)
                {
                    foreach (var el in entriesArr.EnumerateArray())
                        updates += IngestUserObjectSilent(el);
                }

                if (TryGetPropertyIgnoreCase(root, "Matches", out var matchesArr) && matchesArr.ValueKind == JsonValueKind.Array)
                {
                    foreach (var m in matchesArr.EnumerateArray())
                    {
                        if (m.ValueKind != JsonValueKind.Object) continue;
                        long ts = TryGetInt64IgnoreCase(m, "Timestamp");
                        if (TryGetPropertyIgnoreCase(m, "Player1", out var p1) && p1.ValueKind == JsonValueKind.Object)
                            updates += IngestUserObjectSilent(p1, ts);
                        if (TryGetPropertyIgnoreCase(m, "Player2", out var p2) && p2.ValueKind == JsonValueKind.Object)
                            updates += IngestUserObjectSilent(p2, ts);
                    }
                }
            }
            catch
            {
                return 0;
            }

            return updates;
        }

        private int IngestUserObjectSilent(JsonElement el, long fallbackTimestamp = 0)
        {
            if (el.ValueKind != JsonValueKind.Object)
                return 0;

            string? playerId = TryGetStringIgnoreCase(el, "PlayerId");
            string? username = TryGetStringIgnoreCase(el, "Username");
            if (string.IsNullOrWhiteSpace(playerId) || string.IsNullOrWhiteSpace(username))
                return 0;

            int hue = (int)TryGetInt64IgnoreCase(el, "ColorHue", 180);
            long ts = TryGetInt64IgnoreCase(el, "LastSeenTimestamp");
            if (ts <= 0) ts = TryGetInt64IgnoreCase(el, "LastPlayedTimestamp");
            if (ts <= 0) ts = fallbackTimestamp;

            string cleanId = playerId.Trim();
            string cleanName = username.Trim();
            bool incomingGeneric = IsGenericUsername(cleanName);
            bool changed = false;

            _usersById.AddOrUpdate(
                cleanId,
                _ =>
                {
                    if (!incomingGeneric) changed = true;
                    return new CachedUserRecord
                    {
                        PlayerId = cleanId,
                        Username = cleanName,
                        ColorHue = hue,
                        LastSeenTimestamp = ts,
                        CachedAtUtc = DateTime.UtcNow
                    };
                },
                (_, existing) =>
                {
                    bool existingGeneric = IsGenericUsername(existing.Username);
                    if (!incomingGeneric && (existingGeneric || ts >= existing.LastSeenTimestamp) &&
                        !string.Equals(existing.Username, cleanName, StringComparison.Ordinal))
                    {
                        existing.Username = cleanName;
                        changed = true;
                    }
                    existing.ColorHue = hue;
                    if (ts > existing.LastSeenTimestamp)
                        existing.LastSeenTimestamp = ts;
                    existing.CachedAtUtc = DateTime.UtcNow;
                    return existing;
                });

            return changed ? 1 : 0;
        }

        private static bool TryGetPropertyIgnoreCase(JsonElement obj, string name, out JsonElement value)
        {
            if (obj.ValueKind == JsonValueKind.Object)
            {
                if (obj.TryGetProperty(name, out value))
                    return true;

                foreach (var prop in obj.EnumerateObject())
                {
                    if (string.Equals(prop.Name, name, StringComparison.OrdinalIgnoreCase))
                    {
                        value = prop.Value;
                        return true;
                    }
                }
            }
            value = default;
            return false;
        }

        private static string? TryGetStringIgnoreCase(JsonElement obj, string name)
        {
            return TryGetPropertyIgnoreCase(obj, name, out var value) && value.ValueKind == JsonValueKind.String
                ? value.GetString()
                : null;
        }

        private static long TryGetInt64IgnoreCase(JsonElement obj, string name, long defaultValue = 0)
        {
            if (!TryGetPropertyIgnoreCase(obj, name, out var value))
                return defaultValue;

            return value.ValueKind == JsonValueKind.Number && value.TryGetInt64(out long parsed)
                ? parsed
                : defaultValue;
        }
    }
}
