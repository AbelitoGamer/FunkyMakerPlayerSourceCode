#nullable enable
using System;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using FunkyMakerPlayer.Multiplayer.Rooms;

namespace FunkyMakerPlayer.Multiplayer;

/// <summary>
/// Logging in with Discord (protocol 5). The game can't receive the OAuth redirect itself, so the
/// online server does the OAuth flow and the game waits for it (server: worker/src/auth.ts):
/// POST /api/auth/start → open the returned URL in the browser → POST /api/auth/poll every 2 s until
/// the player has approved on Discord → session token + account saved in <see cref="MultiplayerSettings"/>,
/// and the open connection says Hello again so it becomes that account.
/// State changes and the result happen on the game thread (via <see cref="NetworkManager.Post"/>).
/// </summary>
public sealed class DiscordLogin
{
    public static DiscordLogin Instance { get; } = new();

    public enum LoginState { Idle, Starting, Waiting, Failed }

    public LoginState State { get; private set; } = LoginState.Idle;
    /// <summary>Shown under the login button: what is happening / what went wrong.</summary>
    public string Message { get; private set; } = "";
    /// <summary>The page opened in the browser (shown so it can be opened by hand if nothing opened).</summary>
    public string LoginUrl { get; private set; } = "";
    public bool Busy => State is LoginState.Starting or LoginState.Waiting;

    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(20) };
    private static readonly JsonSerializerOptions Json = new() { PropertyNameCaseInsensitive = true };
    private CancellationTokenSource? _cts;

    private DiscordLogin() { }

    private sealed class StartResponse
    {
        public string LoginId { get; set; } = "";
        public string PollKey { get; set; } = "";
        public string Url { get; set; } = "";
        public int ExpiresIn { get; set; } = 600;
        public int Interval { get; set; } = 2;
        public string Error { get; set; } = "";
    }

    private sealed class PollResponse
    {
        public string Status { get; set; } = "";
        public string Error { get; set; } = "";
        public string SessionToken { get; set; } = "";
        public AccountInfo? Account { get; set; }
    }

    /// <summary>Starts a login (opens the browser). Does nothing while one is running.</summary>
    public void Start()
    {
        if (Busy) return;
        _cts?.Cancel();
        var cts = _cts = new CancellationTokenSource();
        State = LoginState.Starting;
        Message = "Opening Discord...";
        LoginUrl = "";
        string server = NetworkManager.Instance.ServerUrl;
        _ = Task.Run(() => RunAsync(server, cts.Token));
    }

    public void Cancel()
    {
        _cts?.Cancel();
        _cts = null;
        State = LoginState.Idle;
        Message = "";
        LoginUrl = "";
    }

    /// <summary>Forgets the account; the connection goes back to being a guest (leaves any room).</summary>
    public void Logout()
    {
        Cancel();
        MultiplayerSettings.Instance.ClearAccount();
        if (NetworkManager.Instance.IsConnected) NetworkManager.Instance.ResendProfile();
    }

    private async Task RunAsync(string server, CancellationToken ct)
    {
        try
        {
            using var startRes = await Http.PostAsync($"{server}/api/auth/start", null, ct).ConfigureAwait(false);
            var start = await startRes.Content.ReadFromJsonAsync<StartResponse>(Json, ct).ConfigureAwait(false);
            if (!startRes.IsSuccessStatusCode || start == null || string.IsNullOrEmpty(start.LoginId))
            {
                Fail(ct, string.IsNullOrEmpty(start?.Error) ? $"The server couldn't start the login ({(int)startRes.StatusCode})." : start!.Error);
                return;
            }

            bool opened = PlatformShare.OpenUrl(start.Url);
            NetworkManager.Instance.Post(() =>
            {
                if (ct.IsCancellationRequested) return;
                State = LoginState.Waiting;
                LoginUrl = start.Url;
                Message = opened
                    ? "Approve the login on Discord, then come back here."
                    : "Couldn't open the browser. Open the link below to log in.";
            });

            var deadline = DateTime.UtcNow.AddSeconds(Math.Clamp(start.ExpiresIn, 60, 900));
            var interval = TimeSpan.FromSeconds(Math.Clamp(start.Interval, 1, 10));
            while (DateTime.UtcNow < deadline)
            {
                await Task.Delay(interval, ct).ConfigureAwait(false);
                PollResponse? poll;
                try
                {
                    using var res = await Http.PostAsJsonAsync($"{server}/api/auth/poll", new { loginId = start.LoginId, pollKey = start.PollKey }, ct).ConfigureAwait(false);
                    poll = await res.Content.ReadFromJsonAsync<PollResponse>(Json, ct).ConfigureAwait(false);
                }
                catch (HttpRequestException) { continue; } // network blip: keep waiting
                catch (TaskCanceledException) when (!ct.IsCancellationRequested) { continue; } // request timeout

                if (poll == null || poll.Status == "pending") continue;
                if (poll.Status == "ok" && poll.Account != null && !string.IsNullOrEmpty(poll.SessionToken))
                {
                    var account = poll.Account;
                    string token = poll.SessionToken;
                    NetworkManager.Instance.Post(() =>
                    {
                        if (ct.IsCancellationRequested) return;
                        var settings = MultiplayerSettings.Instance;
                        // A new account has the default colour: keep the one chosen on this device.
                        if (account.ColorHue != 180) settings.PlayerColorHue = account.ColorHue;
                        settings.ApplyAccount(account, token);
                        State = LoginState.Idle;
                        Message = "";
                        LoginUrl = "";
                        NetworkManager.Instance.SessionNotice = "";
                        if (NetworkManager.Instance.IsConnected) NetworkManager.Instance.ResendProfile();
                        Console.WriteLine($"[Discord] Logged in as {account.Username} ({account.PlayerId})");
                    });
                    return;
                }
                Fail(ct, string.IsNullOrEmpty(poll.Error) ? "The login failed. Try again." : poll.Error);
                return;
            }
            Fail(ct, "The login took too long. Try again.");
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
        catch (Exception ex)
        {
            Console.WriteLine($"[Discord] Login failed: {ex.Message}");
            Fail(ct, "Couldn't reach the server. Check your connection and try again.");
        }
    }

    private void Fail(CancellationToken ct, string message)
    {
        NetworkManager.Instance.Post(() =>
        {
            if (ct.IsCancellationRequested) return;
            State = LoginState.Failed;
            Message = message;
            LoginUrl = "";
        });
    }
}
