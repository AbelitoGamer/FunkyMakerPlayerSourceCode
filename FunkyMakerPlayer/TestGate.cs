using System;
using System.Net.Http;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace FunkyMakerPlayer;

/// <summary>
/// (fork) Test-build gate. A build made with <c>-p:FunkyTestBuild=true</c> (or with
/// <c>&lt;FunkyTestBuild&gt;true&lt;/FunkyTestBuild&gt;</c> in the csproj) refuses to start unless the
/// online server's <c>GET /api/testing</c> answers <c>{"open": true}</c>. It is blocked when there is
/// no internet, when the server can't be reached after <see cref="MaxAttempts"/> attempts, or when
/// the server says testing is closed (server env var <c>Testing__Open=false</c>).
/// Normal builds compile with <see cref="Enabled"/> = false and never call the server for this.
/// </summary>
public sealed class TestGate
{
#if FUNKY_TEST_BUILD
    public const bool Enabled = true;
#else
    public const bool Enabled = false;
#endif

    public const int MaxAttempts = 5;
    private static readonly TimeSpan AttemptTimeout = TimeSpan.FromSeconds(20); // Render free tier cold start
    private static readonly TimeSpan RetryDelay = TimeSpan.FromSeconds(3);

    /// <summary>Always the built-in server: Settings.json / FUNKYMAKER_SERVER_URL overrides are ignored on purpose,
    /// so a tester can't point the gate at their own server.</summary>
    public static string ServerUrl
    {
        get
        {
#if DEBUG
            // Debug builds only: FUNKYMAKER_TESTGATE_URL points the gate at a local server for testing.
            string? debugUrl = Environment.GetEnvironmentVariable("FUNKYMAKER_TESTGATE_URL");
            if (!string.IsNullOrWhiteSpace(debugUrl)) return debugUrl.Trim().TrimEnd('/');
#endif
            return Multiplayer.MultiplayerSettings.DefaultServerUrl.TrimEnd('/');
        }
    }

    public enum GateState { Checking, Passed, Closed, NoInternet, Unreachable }

    private static readonly HttpClient Http = new() { Timeout = Timeout.InfiniteTimeSpan };

    private volatile GateState _state = Enabled ? GateState.Checking : GateState.Passed;
    private volatile int _attempt;
    private volatile string _serverMessage = "";
    private int _running;

    public GateState State => _state;
    public bool Passed => _state == GateState.Passed;
    public int Attempt => _attempt;
    /// <summary>Message the server sent with a "closed" answer (may be empty).</summary>
    public string ServerMessage => _serverMessage;
    public bool CanRetry => _state is GateState.NoInternet or GateState.Unreachable;

    /// <summary>Starts (or restarts after a failure) the check on a background thread.</summary>
    public void Start()
    {
        if (!Enabled || _state is GateState.Passed or GateState.Closed) return;
        if (Interlocked.Exchange(ref _running, 1) == 1) return;
        _state = GateState.Checking;
        _attempt = 0;
        _ = Task.Run(async () =>
        {
            try { _state = await RunAsync(); }
            catch (Exception ex)
            {
                Console.WriteLine($"[TestGate] Check failed: {ex.Message}");
                _state = GateState.Unreachable;
            }
            finally { Interlocked.Exchange(ref _running, 0); }
        });
    }

    private async Task<GateState> RunAsync()
    {
        string version = typeof(TestGate).Assembly.GetName().Version?.ToString(3) ?? "?";
        string url = $"{ServerUrl}/api/testing?version={Uri.EscapeDataString(version)}";
        bool lastWasNoInternet = false;

        for (int attempt = 1; attempt <= MaxAttempts; attempt++)
        {
            _attempt = attempt;
            if (!NetworkLooksAvailable())
            {
                lastWasNoInternet = true;
                Console.WriteLine($"[TestGate] Attempt {attempt}/{MaxAttempts}: no network");
            }
            else
            {
                try
                {
                    using var cts = new CancellationTokenSource(AttemptTimeout);
                    using var resp = await Http.GetAsync(url, cts.Token);
                    lastWasNoInternet = false;
                    if (resp.IsSuccessStatusCode)
                    {
                        string body = await resp.Content.ReadAsStringAsync(cts.Token);
                        using var doc = JsonDocument.Parse(body);
                        var root = doc.RootElement;
                        if (root.ValueKind == JsonValueKind.Object &&
                            root.TryGetProperty("open", out var open) &&
                            (open.ValueKind == JsonValueKind.True || open.ValueKind == JsonValueKind.False))
                        {
                            if (open.GetBoolean())
                            {
                                Console.WriteLine("[TestGate] Testing is open");
                                return GateState.Passed;
                            }
                            _serverMessage = root.TryGetProperty("message", out var m) && m.ValueKind == JsonValueKind.String
                                ? m.GetString() ?? "" : "";
                            Console.WriteLine("[TestGate] Server says testing is closed");
                            return GateState.Closed;
                        }
                        Console.WriteLine($"[TestGate] Attempt {attempt}/{MaxAttempts}: unexpected answer");
                    }
                    else
                    {
                        Console.WriteLine($"[TestGate] Attempt {attempt}/{MaxAttempts}: HTTP {(int)resp.StatusCode}");
                    }
                }
                catch (HttpRequestException ex) when (IsNoInternet(ex))
                {
                    lastWasNoInternet = true;
                    Console.WriteLine($"[TestGate] Attempt {attempt}/{MaxAttempts}: no internet ({ex.Message})");
                }
                catch (Exception ex)
                {
                    lastWasNoInternet = false;
                    Console.WriteLine($"[TestGate] Attempt {attempt}/{MaxAttempts}: {ex.GetType().Name}: {ex.Message}");
                }
            }

            if (attempt < MaxAttempts)
                await Task.Delay(RetryDelay);
        }

        return lastWasNoInternet ? GateState.NoInternet : GateState.Unreachable;
    }

    private static bool NetworkLooksAvailable()
    {
        try { return NetworkInterface.GetIsNetworkAvailable(); }
        catch { return true; } // unknown → just try the request
    }

    /// <summary>DNS / network-level failures (as opposed to the server itself being down or slow).</summary>
    private static bool IsNoInternet(HttpRequestException ex)
    {
        for (Exception? e = ex; e != null; e = e.InnerException)
        {
            if (e is SocketException se)
                return se.SocketErrorCode is SocketError.HostNotFound or SocketError.TryAgain
                    or SocketError.NetworkUnreachable or SocketError.NetworkDown or SocketError.HostUnreachable
                    or SocketError.NoData;
        }
        return false;
    }
}
