using System;
using System.Net.Http;
using System.Text.Json;
using System.Threading.Tasks;
#if !ANDROID
using DiscordRPC;
using DiscordRPC.Logging;
#endif

namespace FunkyMakerPlayer;

/// <summary>
/// Manages Discord Rich Presence for Funky Maker Player.
///
/// Shows:
///   - In menu:   "In the menu"
///   - Loading:   "Loading: LevelName"
///   - Playing:   "Playing: LevelName"  detail: "Score: X | Combo: Yx"
///   - Multiplayer: Shows party info (players in room)
///
/// Features:
///   - Download button (links to latest GitHub release)
///   - Website button (links to GitHub Pages)
///   - Auto-detects latest release URL
///   - Party info for multiplayer
///
/// To use Discord RPC you need a Discord Application ID.
/// Create one at https://discord.com/developers/applications
/// and replace CLIENT_ID below with your app's ID.
/// </summary>
public static class DiscordRpc
{
#if !ANDROID
    // ── Configuration ─────────────────────────────────────────────────────────
    private const string CLIENT_ID = "1476660744673169489";
    private const string GITHUB_REPO = "Thisisfenix/FunkyMakerPlayerSourceCode";
    private const string FUNKYMAKER_WEBSITE = "https://thisisfenix.github.io/FunkyMakerPlayer/index.html";
    
    // ── State ─────────────────────────────────────────────────────────────────
    private static string _latestReleaseUrl = "https://github.com/Thisisfenix/thisisfenix.github.io/releases/latest"; // se reemplaza por el repo oficial cuando tenga releases
    
    private static DiscordRpcClient? _client;
    private static bool _initialized = false;
    
    // Party info for multiplayer
    private static string? _currentPartyId;
    private static int _partySize = 0;
    private static int _partyMax = 0;

    // Rate-limit / deduplication state
    private static string? _lastSentSignature;
    private static long _lastSentTicks;
    private static PendingPresence? _pendingPresence;
    private static readonly long MinUpdateIntervalTicks = TimeSpan.FromMilliseconds(1200).Ticks;
    internal static string? LastRequestedDetails { get; private set; }
    internal static string? LastRequestedState { get; private set; }

    private sealed class PendingPresence
    {
        public required string Details { get; init; }
        public string? State { get; init; }
        public string? LargeImage { get; init; }
        public string? LargeText { get; init; }
        public DateTimeOffset? StartTime { get; init; }
        public string? PartyId { get; init; }
        public int PartySize { get; init; }
        public int PartyMax { get; init; }
        public required string Signature { get; init; }
    }

    public static void Initialize()
    {
        try
        {
            _client = new DiscordRpcClient(CLIENT_ID)
            {
                Logger = new NullLogger(),
            };
            _client.OnReady        += (_, e) => Console.WriteLine($"[Discord] Connected as {e.User.Username}");
            _client.OnError        += (_, e) => Console.WriteLine($"[Discord] Error: {e.Message}");
            _client.OnConnectionFailed += (_, _) => { /* Discord not running — ignore */ };
            _client.Initialize();
            _initialized = true;
            
            // Fetch latest release URL asynchronously
            _ = FetchLatestReleaseUrl();
            
            SetMenu();
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[Discord] RPC init failed: {ex.Message}");
        }
    }
    
    /// <summary>
    /// Fetches the latest release download URL from GitHub
    /// </summary>
    private static async Task FetchLatestReleaseUrl()
    {
        try
        {
            using var client = new HttpClient();
            client.DefaultRequestHeaders.Add("User-Agent", "FunkyMakerPlayer");
            
            // Repo oficial primero; si aún no tiene releases (404), usar el repo legacy.
            foreach (var repo in new[] { GITHUB_REPO, "Thisisfenix/thisisfenix.github.io" })
            {
                try
                {
                    var response = await client.GetStringAsync($"https://api.github.com/repos/{repo}/releases/latest");
                    var json = JsonDocument.Parse(response);
                    if (json.RootElement.TryGetProperty("html_url", out var htmlUrl) && htmlUrl.GetString() is string u)
                    {
                        _latestReleaseUrl = u;
                        Console.WriteLine($"[Discord] Latest release: {_latestReleaseUrl}");
                        return;
                    }
                }
                catch (HttpRequestException) { }
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[Discord] Failed to fetch latest release: {ex.Message}");
        }
    }

    public static void Update()
    {
        if (!_initialized) return;
        try
        {
            if (_pendingPresence != null && (DateTime.UtcNow.Ticks - _lastSentTicks) >= MinUpdateIntervalTicks)
            {
                var next = _pendingPresence;
                _pendingPresence = null;
                ApplyPresenceNow(next);
            }
            _client?.Invoke();
        }
        catch { }
    }

    public static void Shutdown()
    {
        if (!_initialized) return;
        try
        {
            _pendingPresence = null;
            _lastSentSignature = null;
            _client?.Dispose();
        }
        catch { }
        _initialized = false;
    }

    // ── Presence states ───────────────────────────────────────────────────────

    public static void SetMenu()
    {
        Set("In the menu", null,
            largeImage: "logo",
            largeText:  "Funky Maker Player",
            immediate:  true);
    }

    public static void SetBrowsingMusic(string? selectedLevel = null)
    {
        string? state = string.IsNullOrWhiteSpace(selectedLevel)
            ? null
            : $"Selected: {Truncate(selectedLevel.Trim(), 46)}";
        Set("Browsing music", state,
            largeImage: "logo",
            largeText:  "Funky Maker Player",
            immediate:  state == null);
    }

    public static void SetEditorHub(string? subtitle = null)
    {
        Set("In Level Editors",
            string.IsNullOrWhiteSpace(subtitle) ? "Choosing an editor" : Truncate(subtitle.Trim(), 50),
            largeImage: "logo",
            largeText:  "Funky Maker Player",
            immediate:  true);
    }

    public static void SetChartEditor(string levelName, bool isPlayingPreview, DateTimeOffset? sessionStartUtc = null, bool immediate = false)
    {
        string cleanName = string.IsNullOrWhiteSpace(levelName) ? "Untitled" : levelName.Trim();
        string state = isPlayingPreview ? "Playing preview" : "Editing chart";
        Set($"Chart Editor: {Truncate(cleanName, 46)}",
            state,
            largeImage: "logo",
            largeText:  "Funky Maker Player",
            startTime:  sessionStartUtc ?? DateTimeOffset.UtcNow,
            immediate:  immediate);
    }

    public static void SetLoading(string levelName)
    {
        Set($"Loading: {Truncate(levelName, 50)}", null,
            largeImage: "logo",
            largeText:  "Funky Maker Player",
            immediate:  true);
    }

    public static void SetLoadingMultiplayer(string levelName, string? opponentName = null)
    {
        string cleanLevel = string.IsNullOrWhiteSpace(levelName) ? "Unknown" : levelName.Trim();
        string state = !string.IsNullOrWhiteSpace(opponentName)
            ? $"vs {Truncate(opponentName.Trim(), 44)}"
            : "Multiplayer Match";
        Set($"Loading: {Truncate(cleanLevel, 48)}",
            state,
            largeImage: "logo",
            largeText:  "Funky Maker Player",
            partyId:    _currentPartyId,
            partySize:  _partySize,
            partyMax:   _partyMax,
            immediate:  true);
    }

    public static void SetPlaying(string levelName, bool isPlaytest = false, DateTimeOffset? startTime = null)
    {
        string cleanName = string.IsNullOrWhiteSpace(levelName) ? "Unknown" : levelName.Trim();
        string details = isPlaytest
            ? $"Playtesting: {Truncate(cleanName, 46)}"
            : $"Playing: {Truncate(cleanName, 50)}";
        string? state = isPlaytest ? "Chart Editor" : null;
        Set(details, state,
            largeImage: "logo",
            largeText:  "Funky Maker Player",
            startTime:  startTime ?? DateTimeOffset.UtcNow,
            immediate:  true);
    }

    public static void SetPlayingMultiplayer(string levelName, string? opponentName = null, DateTimeOffset? startTime = null, bool immediate = true)
    {
        string cleanName = string.IsNullOrWhiteSpace(levelName) ? "Unknown" : levelName.Trim();
        string details = $"Playing: {Truncate(cleanName, 46)}";
        string state = !string.IsNullOrWhiteSpace(opponentName)
            ? $"vs {Truncate(opponentName.Trim(), 44)}"
            : "Multiplayer Match";

        Set(details, state,
            largeImage: "logo",
            largeText:  "Funky Maker Player",
            startTime:  startTime ?? DateTimeOffset.UtcNow,
            partyId:    _currentPartyId,
            partySize:  _partySize,
            partyMax:   _partyMax,
            immediate:  immediate);
    }

    [Obsolete("Use SetPlaying(levelName) instead - score/combo updates cause issues")]
    public static void UpdateScore(string levelName, int score, int combo, int misses)
    {
        // Deprecated: frequent updates cause RPC issues
        // Only update detail line — don't reset the start timestamp
        if (!_initialized || _client == null) return;
        try
        {
            var current = _client.CurrentPresence;
            if (current == null) return;
            current.Details = $"Playing: {Truncate(levelName, 40)}";
            current.State   = $"Score: {score}  |  {combo}x combo  |  {misses} miss";
            _client.SetPresence(current);
        }
        catch { }
    }
    
    // ── Multiplayer support ───────────────────────────────────────────────────
    
    /// <summary>
    /// Set multiplayer party info
    /// </summary>
    public static void SetParty(string partyId, int currentSize, int maxSize)
    {
        _currentPartyId = partyId;
        _partySize = currentSize;
        _partyMax = maxSize;
    }
    
    /// <summary>
    /// Clear party info (when leaving multiplayer)
    /// </summary>
    public static void ClearParty()
    {
        _currentPartyId = null;
        _partySize = 0;
        _partyMax = 0;
    }
    
    /// <summary>
    /// Set multiplayer state (in lobby or matchmaking) with unambiguous details + state.
    /// </summary>
    public static void SetMultiplayerLobby(string details, string? state = null, DateTimeOffset? startTime = null, bool immediate = false)
    {
        Set(details, state,
            largeImage: "logo",
            largeText:  "Funky Maker Player",
            startTime:  startTime,
            partyId:    _currentPartyId,
            partySize:  _partySize,
            partyMax:   _partyMax,
            immediate:  immediate);
    }

    /// <summary>
    /// Set multiplayer state (in lobby or playing)
    /// </summary>
    public static void SetMultiplayer(string details, string? state = null)
    {
        SetMultiplayerLobby(details, state, immediate: true);
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private static void Set(string details, string? state,
        string? largeImage = null, string? largeText = null,
        DateTimeOffset? startTime = null,
        string? partyId = null, int partySize = 0, int partyMax = 0,
        bool immediate = false)
    {
        LastRequestedDetails = details;
        LastRequestedState = state;
        if (!_initialized || _client == null) return;

        long startEpoch = startTime.HasValue ? startTime.Value.ToUnixTimeSeconds() : 0;
        string cleanPartyId = !string.IsNullOrEmpty(partyId) && partyMax > 0 ? partyId : "";
        string signature = $"{details}|{state ?? ""}|{largeImage ?? "logo"}|{startEpoch > 0}|{cleanPartyId}|{partySize}|{partyMax}";

        if (string.Equals(_lastSentSignature, signature, StringComparison.Ordinal))
        {
            _pendingPresence = null;
            return;
        }

        var pending = new PendingPresence
        {
            Details = details,
            State = state,
            LargeImage = largeImage,
            LargeText = largeText,
            StartTime = startTime,
            PartyId = string.IsNullOrEmpty(cleanPartyId) ? null : cleanPartyId,
            PartySize = partySize,
            PartyMax = partyMax,
            Signature = signature
        };

        long nowTicks = DateTime.UtcNow.Ticks;
        if (immediate || (nowTicks - _lastSentTicks) >= MinUpdateIntervalTicks)
        {
            _pendingPresence = null;
            ApplyPresenceNow(pending);
        }
        else
        {
            _pendingPresence = pending;
        }
    }

    private static void ApplyPresenceNow(PendingPresence p)
    {
        if (!_initialized || _client == null) return;
        try
        {
            var presence = new RichPresence
            {
                Details  = p.Details,
                State    = p.State,
                Assets   = new Assets
                {
                    LargeImageKey  = p.LargeImage ?? "logo",
                    LargeImageText = p.LargeText  ?? "Funky Maker Player",
                },
                Buttons = new[]
                {
                    new DiscordRPC.Button { Label = "Download Latest", Url = _latestReleaseUrl },
                    new DiscordRPC.Button { Label = "Visit Website", Url = FUNKYMAKER_WEBSITE }
                }
            };
            
            if (p.StartTime.HasValue)
                presence.Timestamps = new Timestamps(p.StartTime.Value.UtcDateTime);
            
            // Add party info for multiplayer
            if (!string.IsNullOrEmpty(p.PartyId) && p.PartyMax > 0)
            {
                presence.Party = new Party
                {
                    ID = p.PartyId,
                    Size = p.PartySize,
                    Max = p.PartyMax
                };
            }
            
            _client.SetPresence(presence);
            _lastSentSignature = p.Signature;
            _lastSentTicks = DateTime.UtcNow.Ticks;
        }
        catch { }
    }

    private static string Truncate(string? s, int max)
    {
        if (string.IsNullOrEmpty(s)) return "";
        return s.Length <= max ? s : s[..max] + "…";
    }
#else
    // ═══════════════════════════════════════════════════════════════════════
    // ANDROID STUBS - Discord RPC not available on mobile
    // ═══════════════════════════════════════════════════════════════════════
    public static void Initialize() { }
    public static void Update() { }
    public static void Shutdown() { }
    public static void SetMenu() { }
    public static void SetBrowsingMusic(string? selectedLevel = null) { }
    public static void SetEditorHub(string? subtitle = null) { }
    public static void SetChartEditor(string levelName, bool isPlayingPreview, DateTimeOffset? sessionStartUtc = null, bool immediate = false) { }
    public static void SetLoading(string levelName) { }
    public static void SetLoadingMultiplayer(string levelName, string? opponentName = null) { }
    public static void SetPlaying(string levelName, bool isPlaytest = false, DateTimeOffset? startTime = null) { }
    public static void SetPlayingMultiplayer(string levelName, string? opponentName = null, DateTimeOffset? startTime = null, bool immediate = true) { }
    [Obsolete("Use SetPlaying(levelName) instead")]
    public static void UpdateScore(string levelName, int score, int combo, int misses) { }
    public static void SetParty(string partyId, int currentSize, int maxSize) { }
    public static void ClearParty() { }
    public static void SetMultiplayerLobby(string details, string? state = null, DateTimeOffset? startTime = null, bool immediate = false) { }
    public static void SetMultiplayer(string details, string? state = null) { }
#endif
}
