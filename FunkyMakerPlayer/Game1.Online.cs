using System;
using System.Collections.Generic;
using System.Linq;
using FunkyMakerPlayer.Multiplayer;
using FunkyMakerPlayer.Multiplayer.Rooms;
using FunkyMakerPlayer.UI;
using Microsoft.Xna.Framework;

namespace FunkyMakerPlayer;

/// <summary>
/// Online room matches in the game (fork). The room system can have 2-8 players and spectators:
/// <list type="bullet">
/// <item>Each player plays their side (0 = player chart, 1 = enemy chart) on strumline 0, like before.</item>
/// <item>Strumline 1 (the other side) shows a real player's presses (Versus: the opponent; Teams: the
/// first player of the other team, or the CPU per setting) or is played by the CPU (Mirror).</item>
/// <item>Extra strumlines (2+) are played by the CPU so every character keeps singing.</item>
/// <item>Spectators: strumline 0 follows one player's presses; nothing is sent.</item>
/// <item>The health bar shows the score difference (nobody dies online); a small list shows every score.</item>
/// <item>Side balancing: the side with fewer points available gets a multiplier on every point
/// (<see cref="NoteRenderer.SideScoreMultiplier"/>).</item>
/// </list>
/// </summary>
public partial class Game1
{
    private MatchInfo? _roomMatch;
    /// <summary>Watching the match instead of playing it.</summary>
    private bool _onlineSpectating;
    /// <summary>Player whose presses drive strumline 1 (null = CPU).</summary>
    private string? _rivalPlayerId;
    /// <summary>Spectating: player whose presses drive strumline 0.</summary>
    private string? _focusPlayerId;
    private bool _roomFinishSent;
    private long _perfectScore = 1;
    private int _lastLocalHitLane = -1;
    private float _lastLocalHoldEnd = -1f;
    private readonly List<MatchScoreEntry> _scoreList = new();
    /// <summary>Participants already announced as gone (left the room or quit the match).</summary>
    private readonly HashSet<string> _goneAnnounced = new();

    /// <summary>Called once from LoadContent.</summary>
    private void HookRoomMatchEvents()
    {
        var room = RoomClient.Instance;
        room.MatchReadyToLoad += StartRoomMatch;
        room.MatchStarting += OnRoomMatchStarting;
        room.RoundFinished += OnRoomRoundFinished;
        var net = NetworkManager.Instance;
        net.PlayerHitReceived += OnRemoteHit;
        net.PlayerKeyPressReceived += OnRemoteKeyPress;
        UserDirectoryClient.Instance.OnUsersUpdated += () => _pendingDiscordMpRefresh = true;
    }

    /// <summary>
    /// The "Extra Strumlines" setting (<see cref="HideExtraStrumlines"/>) is ignored in online matches:
    /// the extra strumlines (2+) are always hidden there (the CPU still plays them), whatever each
    /// player chose.
    /// </summary>
    private bool HideExtras => HideExtraStrumlines || _isMultiplayerMatch;

    /// <summary>
    /// Accuracy reported online. 100 % has to be earned: with no judged notes at all (an empty chart
    /// on your side, or nothing played yet) it is 0 %, not the 100 % <see cref="ComputeAccuracy"/> gives.
    /// </summary>
    private static float MatchAccuracy(NoteRenderer r) =>
        r.Sicks + r.Goods + r.Bads + r.Shits + r.Misses == 0 ? 0f : ComputeAccuracy(r.Sicks, r.Goods, r.Bads, r.Shits, r.Misses);

    private static int MatchMisses(NoteRenderer r) => r.Misses;

    private int MyTeam(string playerId) => _roomMatch?.Players.FirstOrDefault(p => p.PlayerId == playerId)?.Team ?? -1;

    /// <summary>
    /// Whose presses strumline 1 shows, for the player on strumline 0 (<paramref name="pov"/>):
    /// Versus: the other player. Teams: the first player of the other team (or the CPU, per the
    /// online setting). Mirror: nobody plays that side, so the CPU.
    /// </summary>
    private string? PickRival(MatchInfo info, string pov)
    {
        var mine = info.Players.FirstOrDefault(p => p.PlayerId == pov);
        if (mine == null) return null;
        switch (info.Mode)
        {
            case Protocol.ModeMirror:
                return null;
            case Protocol.ModeTeams:
                if (MultiplayerSettings.Instance.TeamsRivalStrumline == MultiplayerSettings.RivalStrumlineMode.Cpu) return null;
                return info.Players.FirstOrDefault(p => p.Team != mine.Team)?.PlayerId;
            default:
                return info.Players.FirstOrDefault(p => p.PlayerId != pov)?.PlayerId;
        }
    }

    /// <summary>RoomClient: the match is loading and the level is here. Play it, or watch it.</summary>
    private void StartRoomMatch(MatchInfo info, string levelPath, MatchAssignment? me)
    {
        Console.WriteLine($"[Online] Match {info.MatchId}: {info.Song.Name} ({info.Mode}), {(me == null ? "spectating" : $"side {me.Side}")}");

#if ANDROID
        // The chat's system text dialog stayed open over the loading level (a player crashed when the
        // song started while typing in the chat): close it, the unsent text is dropped.
        try { if (Microsoft.Xna.Framework.Input.KeyboardInput.IsVisible) Microsoft.Xna.Framework.Input.KeyboardInput.Cancel(null!); }
        catch (Exception ex) { Console.WriteLine($"[Online] Closing the text dialog failed: {ex.Message}"); }
#endif
        // Leave whatever is on screen (menu, last round's results, a match being watched).
        if (_isMultiplayerMatch || _resultsScreen != null)
        {
            _musicInst?.Stop();
            ResetMultiplayerMatchState();
        }
        CloseAllInMatchOverlays();
        _songTransition = null;
        _inMainSettings = false;

        _roomMatch = info;
        _onlineSpectating = me == null;
        _roomFinishSent = false;
        string pov;
        int side;
        if (me != null)
        {
            pov = me.PlayerId;
            side = me.Side;
            _focusPlayerId = null;
        }
        else
        {
            var focus = info.Players.OrderBy(p => p.Side).First();
            pov = focus.PlayerId;
            side = focus.Side;
            _focusPlayerId = focus.PlayerId;
        }
        _rivalPlayerId = PickRival(info, pov);

        var room = RoomClient.Instance;
        _opponentPlayerId = _rivalPlayerId;
        _opponentName = _rivalPlayerId != null ? room.NameOf(_rivalPlayerId) : (info.Mode == Protocol.ModeMirror ? "CPU" : "Team");
        _opponentColorHue = _rivalPlayerId != null ? room.HueOf(_rivalPlayerId) : 180;

        StartMultiplayerMatch(levelPath, side + 1, info.StartTimestamp, info.MatchId, info.Song.Name);
        if (info.StartTimestamp > 0)
        {
            // Joined after the start (spectator): no countdown to wait for.
            _countdownSignalReceived = true;
        }
    }

    /// <summary>Level loaded for a room match: tell the server (players) and show the waiting screen.</summary>
    private void OnRoomMatchLevelLoaded()
    {
        if (_loadingScreen != null)
        {
            _loadingScreen.MyPlayerRole = _multiplayerPlayerRole;
            bool started = _multiplayerStartTimestamp > 0;
            _loadingScreen.Player1Ready = started || _onlineSpectating;
            _loadingScreen.Player2Ready = started || _onlineSpectating;
            if (_multiplayerPlayerRole == 1) _loadingScreen.Player1Ready = true;
            else _loadingScreen.Player2Ready = true;
        }
        if (_roomMatch != null && !_onlineSpectating)
            RoomClient.Instance.ReportMatchLoaded(_roomMatch.MatchId);
        Console.WriteLine("[Online] Level loaded, waiting for the synced start");
    }

    /// <summary>
    /// Loading screen of a room match: every participant and whether their level has loaded (server
    /// room state <see cref="MatchInfo.Loaded"/>; yourself from the local load; everyone once started).
    /// </summary>
    private void UpdateLoadingScreenRoomPlayers()
    {
        if (_loadingScreen == null || !_isMultiplayerMatch || _roomMatch == null) return;
        var room = RoomClient.Instance;
        var live = room.State?.Match;
        var loaded = live != null && live.MatchId == _roomMatch.MatchId ? live.Loaded : null;
        bool started = _multiplayerStartTimestamp > 0 || _countdownSignalReceived;
        bool meLoaded = _loadState == LoadState.Done && _level != null;
        string me = room.YouId;

        var list = _loadingScreen.RoomPlayers ??= new List<LoadingScreen.RoomLoadEntry>();
        list.Clear();
        foreach (var a in _roomMatch.Players)
        {
            bool isMe = a.PlayerId == me && !_onlineSpectating;
            bool ok = started || (isMe ? meLoaded : loaded?.Contains(a.PlayerId) == true);
            string side = _roomMatch.Mode switch
            {
                Protocol.ModeTeams => a.Team == 0 ? "Team Player" : "Team Enemy",
                Protocol.ModeMirror => "",
                _ => RoomClient.SideName(a.Side),
            };
            list.Add(new LoadingScreen.RoomLoadEntry(room.NameOf(a.PlayerId), MultiplayerSettings.ColorFromHue(room.HueOf(a.PlayerId)), side, ok, isMe));
        }
        _loadingScreen.RoomMode = _roomMatch.Mode;
    }

    /// <summary>
    /// Guards the multiplayer loading/waiting screen against hanging forever if the server cancelled
    /// the match, timed out / dropped this player during a slow load, lost connection, or already
    /// started while <see cref="SetupLevel"/> was running on the main thread.
    /// Returns true if the loading screen was aborted back to the room/error screen.
    /// </summary>
    private bool CheckMultiplayerLoadingScreenStatus(bool userCancelled)
    {
        if (_loadingScreen == null || !_isMultiplayerMatch || _roomMatch == null) return false;

        var net = NetworkManager.Instance;
        var room = RoomClient.Instance;

        if (userCancelled && _loadState == LoadState.Done)
        {
            Console.WriteLine("[Online] Player cancelled while waiting on multiplayer loading screen");
            QuitRoomMatch();
            ResetMultiplayerMatchState();
            _loadingScreen = null;
            _level = null;
            _inMultiplayer = true;
            _multiplayerMenu?.ShowInfo("Cancelled loading and returned to the room.", 4f);
            return true;
        }

        // Recover MatchStart from RoomState if it arrived while SetupLevel was busy or after reconnect.
        var live = room.State?.Match;
        if (live != null && live.MatchId == _roomMatch.MatchId && live.StartTimestamp > 0 && !_countdownSignalReceived)
        {
            Console.WriteLine($"[Online] Recovered MatchStart from RoomState (start={live.StartTimestamp})");
            _roomMatch.StartTimestamp = live.StartTimestamp;
            _multiplayerStartTimestamp = live.StartTimestamp;
            _countdownSignalReceived = true;
            _loadingScreen.Player1Ready = true;
            _loadingScreen.Player2Ready = true;
            return false;
        }

        if (_loadState != LoadState.Done) return false;

        if (!net.IsConnected && !net.IsConnecting)
        {
            Console.WriteLine("[Online] Disconnected while waiting on loading screen; returning to online menu");
            HandleSetupLevelFailure(new Exception("Connection lost while loading the match."));
            return true;
        }

        var st = room.State;
        if (st == null || st.Phase == Protocol.PhaseLobby || live == null || live.MatchId != _roomMatch.MatchId)
        {
            Console.WriteLine($"[Online] Server cancelled/ended match while loading (phase={st?.Phase ?? "null"}, liveMatch={live?.MatchId ?? "null"}); returning to room");
            ResetMultiplayerMatchState();
            _loadingScreen = null;
            _level = null;
            _inMultiplayer = true;
            _multiplayerMenu?.ShowInfo("Server cancelled the match (load timeout or opponent left).", 5f);
            return true;
        }

        if (!_onlineSpectating && live.Players.All(p => p.PlayerId != room.YouId))
        {
            Console.WriteLine("[Online] Server dropped this player during loading (load timeout); returning to room");
            room.MarkLeftMatch(_roomMatch.MatchId);
            room.LeftMatchScreen();
            ResetMultiplayerMatchState();
            _loadingScreen = null;
            _level = null;
            _inMultiplayer = true;
            _multiplayerMenu?.ShowInfo("Loading took too long — returned to the room (press Watch to spectate).", 6f);
            return true;
        }

        return false;
    }

    private void OnRoomMatchStarting(MatchStartPacket p)
    {
        if (_roomMatch == null || p.MatchId != _roomMatch.MatchId) return;
        _roomMatch.StartTimestamp = p.StartTimestamp;
        _multiplayerStartTimestamp = p.StartTimestamp;
        _countdownSignalReceived = true;
        if (_loadingScreen != null)
        {
            _loadingScreen.Player1Ready = true;
            _loadingScreen.Player2Ready = true;
        }
    }

    /// <summary>Strumline setup for a room match (from SetupLevel, after the renderers are created).</summary>
    private void ConfigureRoomMatchRenderers(int myChartIndex)
    {
        if (_roomMatch == null || _level == null || _noteRenderers.Count == 0) return;
        long a = NoteRenderer.MaxScoreForChart(_level, 0), b = NoteRenderer.MaxScoreForChart(_level, 1);
        _perfectScore = Math.Max(1, Math.Max(a, b));

        var me = _noteRenderers[0];
        if (_onlineSpectating)
        {
            // Strumline 0 follows the watched player (hits come from the network).
            me.SetIsPlayer(false);
            me.DisableBotplay = true;
        }
        else
        {
            me.ScoreMultiplier = NoteRenderer.SideScoreMultiplier(_level, myChartIndex);
            if (me.ScoreMultiplier > 1.0001)
                Console.WriteLine($"[Online] Side balancing: your points ×{me.ScoreMultiplier:F3} (perfect run = {_perfectScore})");
        }

        // Strumline 1: a real player's presses, or the CPU.
        if (_noteRenderers.Count > 1)
            _noteRenderers[1].DisableBotplay = _rivalPlayerId != null;
    }

    // ══════════════════════════════════════════════════════════════════════
    // Relay
    // ══════════════════════════════════════════════════════════════════════

    /// <summary>Local hit on strumline 0 (from OnNoteHit; the rating follows in OnJudgement).</summary>
    private void OnLocalNoteHit(int lane, float holdEnd)
    {
        _lastLocalHitLane = lane;
        _lastLocalHoldEnd = holdEnd;
    }

    private void OnLocalJudgement(string judgement)
    {
        if (!_isMultiplayerMatch || _onlineSpectating || _roomMatch == null) return;
        if (judgement == "Miss" || _lastLocalHitLane < 0) return;
        NetworkManager.Instance.SendHit(_lastLocalHitLane, judgement, _lastLocalHoldEnd, _songTime);
        _lastLocalHitLane = -1;
    }

    private void OnLocalKeyPress(int lane)
    {
        if (_isMultiplayerMatch && !_onlineSpectating && _roomMatch != null)
            NetworkManager.Instance.SendKeyPress(lane, _songTime);
    }

    /// <summary>Which strumline a remote player's presses drive (-1 = none).</summary>
    private int StrumlineFor(string playerId)
    {
        if (!_isMultiplayerMatch || _roomMatch == null) return -1;
        if (playerId == _rivalPlayerId) return 1;
        if (_onlineSpectating && playerId == _focusPlayerId) return 0;
        return -1;
    }

    private void OnRemoteHit(PlayerHitPacket p)
    {
        int sl = StrumlineFor(p.PlayerId);
        if (sl < 0 || sl >= _noteRenderers.Count || !_started) return;
        _noteRenderers[sl].TriggerManualHit(p.Lane, p.Rating == "Sick!");
        AnimateRemoteSing(sl, p.Lane, p.HoldEnd);
    }

    private void OnRemoteKeyPress(PlayerKeyPressPacket p)
    {
        int sl = StrumlineFor(p.PlayerId);
        if (sl < 0 || sl >= _noteRenderers.Count || !_started) return;
        _noteRenderers[sl].TriggerManualHit(p.Lane, isSick: false);
        AnimateRemoteSing(sl, p.Lane, -1f);
    }

    /// <summary>Makes the characters of the chart on strumline <paramref name="sl"/> sing a lane.</summary>
    private void AnimateRemoteSing(int sl, int lane, float holdEnd)
    {
        if (_objAnimators == null || _level == null) return;
        var objs = _level.Scene?.Objects;
        if (objs == null) return;
        int chart = sl == 0 ? _multiplayerPlayerRole - 1 : 2 - _multiplayerPlayerRole;
        for (int j = 0; j < objs.Count && j < _objAnimators.Length; j++)
        {
            if ((int)objs[j].StrumLine != chart) continue;
            var anim = _objAnimators[j];
            if (anim == null || anim.IsSetByEvent) continue;
            int singAnim = Math.Clamp(lane + 1, 1, Math.Max(1, anim.AnimCount - 1));
            anim.SetAnimation(singAnim, setByEvent: false);
            if (holdEnd > 0f) anim.HoldUntil = holdEnd;
        }
    }

    // ══════════════════════════════════════════════════════════════════════
    // Per frame: state, health bar = score difference, score list
    // ══════════════════════════════════════════════════════════════════════

    private float _roomStateTimer;

    private void UpdateRoomMatch(float dt)
    {
        if (!_isMultiplayerMatch || _roomMatch == null || _noteRenderers.Count == 0) return;
        var room = RoomClient.Instance;
        var mine = _noteRenderers[0];

        if (!_onlineSpectating && _started)
        {
            _roomStateTimer += dt;
            if (_roomStateTimer >= MultiplayerSettings.Instance.GetStateSyncIntervalSeconds())
            {
                _roomStateTimer = 0f;
                NetworkManager.Instance.SendState(new PlayerStatePacket
                {
                    Score = mine.Score, Combo = mine.Combo, MaxCombo = _playerMaxCombo,
                    Accuracy = MatchAccuracy(mine),
                    Sicks = mine.Sicks, Goods = mine.Goods, Bads = mine.Bads, Shits = mine.Shits, Misses = MatchMisses(mine),
                    SongTime = _songTime,
                });
            }
        }

        // Scores of everybody in the match.
        string me = room.YouId;
        bool serverMatch = room.State?.Match?.MatchId == _roomMatch.MatchId;
        _scoreList.Clear();
        var scores = new Dictionary<string, long>();
        foreach (var a in _roomMatch.Players)
        {
            long score;
            int misses;
            if (a.PlayerId == me && !_onlineSpectating)
            {
                score = mine.Score;
                misses = mine.Misses;
            }
            else if (room.LiveStates.TryGetValue(a.PlayerId, out var st))
            {
                score = st.Score;
                misses = st.Misses;
            }
            else
            {
                score = 0;
                misses = 0;
            }
            // Gone: left the room, said so (relayed state), or the server marked them as out of the
            // match (quit, or stopped sending anything: crashed / closed the game).
            bool gone = a.PlayerId != me && (room.MemberById(a.PlayerId) == null
                || (room.LiveStates.TryGetValue(a.PlayerId, out var ls) && ls.Left)
                || (serverMatch && room.MemberById(a.PlayerId)?.Status == Protocol.StatusLeftMatch)
                // Dropped while loading (quit / too slow): no longer in the server's match.
                || (serverMatch && room.State!.Match!.Players.All(p => p.PlayerId != a.PlayerId)));
            if (!gone) scores[a.PlayerId] = score;
            if (gone && _goneAnnounced.Add(a.PlayerId))
            {
                string who = room.NameOf(a.PlayerId);
                if (a.PlayerId == _rivalPlayerId || a.PlayerId == _focusPlayerId)
                {
                    // Their strumline would freeze: the CPU plays it from here on.
                    int sl = a.PlayerId == _rivalPlayerId ? 1 : 0;
                    if (sl == 1) _rivalPlayerId = null; else _focusPlayerId = null;
                    if (sl < _noteRenderers.Count) _noteRenderers[sl].DisableBotplay = false;
                    ShowReplayMessage($"{who} left the match. The CPU plays their notes now.", 6f);
                }
                else
                {
                    ShowReplayMessage($"{who} left the match.", 6f);
                }
                UI.UiSfx.Play(UI.UiSound.Error);
            }
            _scoreList.Add(new MatchScoreEntry(room.NameOf(a.PlayerId), room.HueOf(a.PlayerId), score, misses, a.PlayerId == me, a.Team, gone));
        }
        // Players who left go to the bottom (they're ranked last too).
        _scoreList.Sort((x, y) => x.Gone != y.Gone ? x.Gone.CompareTo(y.Gone) : y.Score.CompareTo(x.Score));

        // Health bar: score difference from the point of view of strumline 0's player.
        string pov = _onlineSpectating ? (_focusPlayerId ?? "") : me;
        long diff = ScoreDifference(pov, scores);
        float scale = Math.Max(1500f, _perfectScore * 0.12f);
        float target = 0.5f + 0.5f * Math.Clamp(diff / scale, -1f, 1f);
        if (_healthBar != null)
            _healthBar.Health = MathHelper.Lerp(_healthBar.Health, Math.Clamp(target, 0.02f, 0.98f), 1f - MathF.Exp(-6f * dt));
        // Icons: the losing side shows its "losing" frame.
        _opponentHealth = 1f - (_healthBar?.Health ?? 0.5f);
    }

    /// <summary>
    /// Versus: you − the other player. Teams: your team's average − the other team's. Mirror: you −
    /// the leader (or − the runner-up when you lead).
    /// </summary>
    private long ScoreDifference(string pov, Dictionary<string, long> scores)
    {
        if (_roomMatch == null || !scores.TryGetValue(pov, out long mine)) return 0;
        switch (_roomMatch.Mode)
        {
            case Protocol.ModeTeams:
            {
                int team = MyTeam(pov);
                var mineT = _roomMatch.Players.Where(p => p.Team == team).Select(p => scores.GetValueOrDefault(p.PlayerId)).ToList();
                var other = _roomMatch.Players.Where(p => p.Team != team).Select(p => scores.GetValueOrDefault(p.PlayerId)).ToList();
                double a = mineT.Count > 0 ? mineT.Average() : 0, b = other.Count > 0 ? other.Average() : 0;
                return (long)Math.Round(a - b);
            }
            case Protocol.ModeMirror:
            {
                var others = scores.Where(kv => kv.Key != pov).Select(kv => kv.Value).OrderByDescending(v => v).ToList();
                return others.Count == 0 ? 0 : mine - others[0];
            }
            default:
                return mine - scores.Where(kv => kv.Key != pov).Select(kv => kv.Value).DefaultIfEmpty(0).Max();
        }
    }

    private bool ScoreListIsTeams => _roomMatch?.Mode == Protocol.ModeTeams;

    // ══════════════════════════════════════════════════════════════════════
    // End of the round
    // ══════════════════════════════════════════════════════════════════════

    /// <summary>Song finished in a room match: report the result and show the (waiting) results screen.</summary>
    private void FinishRoomMatchSong()
    {
        if (_roomMatch == null) return;
        PlayerResult? mine = null;
        if (!_onlineSpectating && _noteRenderers.Count > 0)
        {
            var r = _noteRenderers[0];
            float acc = MatchAccuracy(r);
            mine = new PlayerResult
            {
                PlayerId = RoomClient.Instance.YouId,
                Username = string.IsNullOrWhiteSpace(MultiplayerSettings.Instance.Username) ? "Player" : MultiplayerSettings.Instance.Username,
                ColorHue = MultiplayerSettings.Instance.PlayerColorHue,
                Side = _multiplayerPlayerRole - 1,
                Team = MyTeam(RoomClient.Instance.YouId),
                Score = r.Score, Accuracy = acc, Misses = MatchMisses(r),
                Sicks = r.Sicks, Goods = r.Goods, Bads = r.Bads, Shits = r.Shits, MaxCombo = _playerMaxCombo,
            };
            SendRoomFinish(mine);
            OnOnlineMatchFinishedForReplay(); // replays (fork): save + exchange (Versus)
        }

        var already = RoomClient.Instance.LastResult;
        _resultsScreen = new MultiplayerResultsScreen(_pixel!, RoomClient.Instance.YouId, _roomMatch.Song.Name, _roomMatch.Mode, mine,
            already?.MatchId == _roomMatch.MatchId ? already : null);
        var match = _roomMatch;
        _resultsScreen.LiveRows = () => LiveResultRows(match, mine);
        UI.UiSfx.Play(UI.UiSound.Save);
    }

    /// <summary>
    /// Results screen while the server ranking is on its way: every participant with their latest
    /// relayed score (you: your final one), finished / left / still playing from the room state.
    /// </summary>
    private static List<(PlayerResult Row, bool Playing)> LiveResultRows(MatchInfo match, PlayerResult? mine)
    {
        var room = RoomClient.Instance;
        bool sameMatch = room.State?.Match?.MatchId == match.MatchId;
        var rows = new List<(PlayerResult Row, bool Playing)>();
        foreach (var a in match.Players)
        {
            if (mine != null && a.PlayerId == mine.PlayerId) { rows.Add((mine, false)); continue; }
            var member = room.MemberById(a.PlayerId);
            room.LiveStates.TryGetValue(a.PlayerId, out var st);
            bool left = member == null || st?.Left == true || (sameMatch && member.Status == Protocol.StatusLeftMatch)
                        || (sameMatch && room.State!.Match!.Players.All(p => p.PlayerId != a.PlayerId));
            bool playing = !left && sameMatch && member!.Status == Protocol.StatusPlaying;
            rows.Add((new PlayerResult
            {
                PlayerId = a.PlayerId, Username = room.NameOf(a.PlayerId), ColorHue = room.HueOf(a.PlayerId),
                Side = a.Side, Team = a.Team, Disconnected = left,
                Score = st?.Score ?? 0, Accuracy = st?.Accuracy ?? 0f, Misses = st?.Misses ?? 0,
                Sicks = st?.Sicks ?? 0, Goods = st?.Goods ?? 0, Bads = st?.Bads ?? 0, Shits = st?.Shits ?? 0, MaxCombo = st?.MaxCombo ?? 0,
            }, playing));
        }
        rows.Sort((x, y) => x.Row.Disconnected != y.Row.Disconnected
            ? x.Row.Disconnected.CompareTo(y.Row.Disconnected)
            : y.Row.Score.CompareTo(x.Row.Score));
        return rows;
    }

    private void SendRoomFinish(PlayerResult r, bool quit = false)
    {
        if (_roomFinishSent || _roomMatch == null) return;
        _roomFinishSent = true;
        RoomClient.Instance.FinishMatch(new FinishMatchRequest
        {
            MatchId = _roomMatch.MatchId, Score = r.Score, Accuracy = r.Accuracy, Misses = r.Misses,
            Sicks = r.Sicks, Goods = r.Goods, Bads = r.Bads, Shits = r.Shits, MaxCombo = r.MaxCombo, Quit = quit,
        });
    }

    /// <summary>Exit Match from the pause overlay: report what you have and go back to the room.</summary>
    private void QuitRoomMatch()
    {
        if (_roomMatch != null && !_onlineSpectating && !_roomFinishSent)
        {
            // Also before the song started (loading / countdown): the server used to keep waiting
            // for a player who quit there until its timeouts ran out.
            var r = _noteRenderers.Count > 0 ? _noteRenderers[0] : null;
            float acc = r != null ? MatchAccuracy(r) : 0f;
            int misses = r != null ? MatchMisses(r) : 0;
            // Tell the others right away (relay), then the server (ranked last as "left").
            NetworkManager.Instance.SendState(new PlayerStatePacket
            {
                Score = r?.Score ?? 0, Combo = r?.Combo ?? 0, MaxCombo = _playerMaxCombo, Misses = misses,
                Accuracy = acc, SongTime = _songTime, Left = true,
            });
            SendRoomFinish(new PlayerResult
            {
                Score = r?.Score ?? 0, Accuracy = acc, Misses = misses,
                Sicks = r?.Sicks ?? 0, Goods = r?.Goods ?? 0, Bads = r?.Bads ?? 0, Shits = r?.Shits ?? 0, MaxCombo = _playerMaxCombo,
            }, quit: true);
        }
        // Out of this match for good: watching it again only spectates.
        if (_roomMatch != null && !_onlineSpectating) RoomClient.Instance.MarkLeftMatch(_roomMatch.MatchId);
        RoomClient.Instance.LeftMatchScreen();
    }

    /// <summary>The server ranked the round.</summary>
    private void OnRoomRoundFinished(RoundResult r)
    {
        if (_roomMatch == null || r.MatchId != _roomMatch.MatchId) return;
        if (_resultsScreen != null)
        {
            _resultsScreen.SetResult(r);
            return;
        }
        // Still in the song (spectating, or the others finished long ago): end it and show the standings.
        if (_isMultiplayerMatch && _level != null)
        {
            _started = false;
            _musicInst?.Stop();
            CloseAllInMatchOverlays();
            _resultsScreen = new MultiplayerResultsScreen(_pixel!, RoomClient.Instance.YouId, _roomMatch.Song.Name, _roomMatch.Mode, null, r);
        }
    }

    private void ResetRoomMatchState()
    {
        _roomMatch = null;
        _onlineSpectating = false;
        _rivalPlayerId = null;
        _focusPlayerId = null;
        _roomFinishSent = false;
        _perfectScore = 1;
        _scoreList.Clear();
        _goneAnnounced.Clear();
        _roomStateTimer = 0f;
        _lastLocalHitLane = -1;
    }

    /// <summary>Score list for duel replays (both tracks are on screen).</summary>
    private IReadOnlyList<MatchScoreEntry>? CurrentScoreList()
    {
        if (_isMultiplayerMatch) return _scoreList;
        if (IsDuelReplay && _noteRenderers.Count > 1 && _activeReplay != null)
        {
            var list = new List<MatchScoreEntry>
            {
                new(_activeReplay.Local?.Username ?? "Player", _activeReplay.Local?.ColorHue ?? 180, _noteRenderers[0].Score, _noteRenderers[0].Misses, true, -1, false),
                new(_opponentName ?? "Opponent", _opponentColorHue, _noteRenderers[1].Score, _noteRenderers[1].Misses, false, -1, false),
            };
            list.Sort((x, y) => y.Score.CompareTo(x.Score));
            return list;
        }
        return null;
    }
}
