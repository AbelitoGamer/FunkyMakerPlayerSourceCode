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
    private const string GITHUB_REPO = "Thisisfenix/thisisfenix.github.io";
    private const string FUNKYMAKER_WEBSITE = "https://thisisfenix.github.io/FunkyMakerPlayer/index.html";
    
    // ── State ─────────────────────────────────────────────────────────────────
    private static string _latestReleaseUrl = "https://github.com/YOUR_USERNAME/FunkyMakerPlayer/releases/latest";
    
    private static DiscordRpcClient? _client;
    private static bool _initialized = false;
    
    // Party info for multiplayer
    private static string? _currentPartyId;
    private static int _partySize = 0;
    private static int _partyMax = 0;

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
            
            var url = $"https://api.github.com/repos/{GITHUB_REPO}/releases/latest";
            var response = await client.GetStringAsync(url);
            var json = JsonDocument.Parse(response);
            
            if (json.RootElement.TryGetProperty("html_url", out var htmlUrl))
            {
                _latestReleaseUrl = htmlUrl.GetString() ?? _latestReleaseUrl;
                Console.WriteLine($"[Discord] Latest release: {_latestReleaseUrl}");
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
        try { _client?.Invoke(); }
        catch { }
    }

    public static void Shutdown()
    {
        if (!_initialized) return;
        try { _client?.Dispose(); }
        catch { }
        _initialized = false;
    }

    // ── Presence states ───────────────────────────────────────────────────────

    public static void SetMenu()
    {
        Set("In the menu", null,
            largeImage: "logo",
            largeText:  "Funky Maker Player");
    }
    
    public static void SetBrowsingMusic()
    {
        Set("Browsing music", null,
            largeImage: "logo",
            largeText:  "Funky Maker Player");
    }

    public static void SetLoading(string levelName)
    {
        Set($"Loading: {Truncate(levelName, 50)}", null,
            largeImage: "logo",
            largeText:  "Funky Maker Player");
    }

    public static void SetPlaying(string levelName)
    {
        Set($"Playing: {Truncate(levelName, 50)}", null,
            largeImage: "logo",
            largeText:  "Funky Maker Player",
            startTime:  DateTimeOffset.UtcNow);
    }
    
    public static void SetPlayingMultiplayer(string levelName)
    {
        if (!string.IsNullOrEmpty(_currentPartyId))
        {
            Set($"Playing: {Truncate(levelName, 40)}", "Multiplayer Match",
                largeImage: "logo",
                largeText: "Funky Maker Player",
                startTime: DateTimeOffset.UtcNow,
                partyId: _currentPartyId,
                partySize: _partySize,
                partyMax: _partyMax);
        }
        else
        {
            Set($"Playing: {Truncate(levelName, 50)}", "Multiplayer Match",
                largeImage: "logo",
                largeText: "Funky Maker Player",
                startTime: DateTimeOffset.UtcNow);
        }
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
    /// Set multiplayer state (in lobby or playing)
    /// </summary>
    public static void SetMultiplayer(string state, string? details = null)
    {
        if (!string.IsNullOrEmpty(_currentPartyId))
        {
            Set(state, details,
                largeImage: "logo",
                largeText: "Funky Maker Player",
                partyId: _currentPartyId,
                partySize: _partySize,
                partyMax: _partyMax);
        }
        else
        {
            Set(state, details, largeImage: "logo", largeText: "Funky Maker Player");
        }
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private static void Set(string details, string? state,
        string? largeImage = null, string? largeText = null,
        DateTimeOffset? startTime = null,
        string? partyId = null, int partySize = 0, int partyMax = 0)
    {
        if (!_initialized || _client == null) return;
        try
        {
            var presence = new RichPresence
            {
                Details  = details,
                State    = state,
                Assets   = new Assets
                {
                    LargeImageKey  = largeImage ?? "logo",
                    LargeImageText = largeText  ?? "Funky Maker Player",
                },
                Buttons = new[]
                {
                    new DiscordRPC.Button { Label = "Download Latest", Url = _latestReleaseUrl },
                    new DiscordRPC.Button { Label = "Visit Website", Url = FUNKYMAKER_WEBSITE }
                }
            };
            
            if (startTime.HasValue)
                presence.Timestamps = new Timestamps(startTime.Value.UtcDateTime);
            
            // Add party info for multiplayer
            if (!string.IsNullOrEmpty(partyId) && partyMax > 0)
            {
                presence.Party = new Party
                {
                    ID = partyId,
                    Size = partySize,
                    Max = partyMax
                };
            }
            
            _client.SetPresence(presence);
        }
        catch { }
    }

    private static string Truncate(string s, int max) =>
        s.Length <= max ? s : s[..max] + "…";
#else
    // ═══════════════════════════════════════════════════════════════════════
    // ANDROID STUBS - Discord RPC not available on mobile
    // ═══════════════════════════════════════════════════════════════════════
    public static void Initialize() { }
    public static void Update() { }
    public static void Shutdown() { }
    public static void SetMenu() { }
    public static void SetBrowsingMusic() { }
    public static void SetLoading(string levelName) { }
    public static void SetPlaying(string levelName) { }
    public static void SetPlayingMultiplayer(string levelName) { }
    [Obsolete("Use SetPlaying(levelName) instead")]
    public static void UpdateScore(string levelName, int score, int combo, int misses) { }
    public static void SetParty(string partyId, int currentSize, int maxSize) { }
    public static void ClearParty() { }
    public static void SetMultiplayer(string state, string? details = null) { }
#endif
}
