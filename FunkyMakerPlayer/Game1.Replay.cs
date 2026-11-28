using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using FunkyMakerPlayer.Replay;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;

namespace FunkyMakerPlayer;

/// <summary>
/// Replays (fork). Recording: every solo run (and, with the online part, every match) records the
/// player strumline's input frames (<see cref="NoteRenderer.InputRecorder"/>) and saves them when
/// the song ends normally. Playback: <see cref="StartReplay"/> loads the level and drives the
/// player strumline from the recording (<see cref="NoteRenderer.StartReplay"/>), judged at the
/// recorded song times, so the result is identical to the original run.
/// </summary>
public partial class Game1
{
    private bool _inReplays;
    private UI.ReplayBrowser? _replayBrowser;
    private ReplayRecorder? _replayRecorder;
    private ReplayFile? _pendingReplay;   // set by StartReplay, picked up by the next level load
    private ReplayFile? _activeReplay;    // replay being watched (null = normal play)
    private string? _replayMessage;
    private float _replayMessageTimer;

    private void EnterReplayBrowser()
    {
        _inFreeplay = false;
        _inReplays = true;
        _replayBrowser = new UI.ReplayBrowser(_pixel!, _font, _fontRenderer);
        _replayBrowser.ResetInputCooldown();
        RefreshActiveDiscordPresence(immediate: true);
    }

    private void ExitReplayBrowser()
    {
        _inReplays = false;
        _replayBrowser = null;
        _inFreeplay = true;
        _freeplay?.ResetInputCooldown();
        RefreshActiveDiscordPresence(immediate: true);
    }

    private void UpdateReplayBrowser(float dt, Microsoft.Xna.Framework.Input.KeyboardState kb)
    {
        var browser = _replayBrowser!;
        browser.Update(dt, kb);
        if (browser.BackPressed)
        {
            ExitReplayBrowser();
            return;
        }
        if (browser.ChosenReplay is { } replay)
        {
            browser.ClearChoice();
            string? error = StartReplay(replay);
            if (error != null)
            {
                browser.ShowMessage(error, error: true);
                return;
            }
            _inReplays = false;
            _replayBrowser = null;
        }
    }

    /// <summary>An online replay is loaded: the level uses the match layout (you right, opponent left).</summary>
    private bool IsDuelReplay => _activeReplay?.IsOnline == true;

    /// <summary>Role (1/2) of the saving player in the watched online replay.</summary>
    private int DuelReplayRole => _activeReplay?.Local?.Role is 1 or 2 ? _activeReplay!.Local!.Role : 1;

    private int _duelSeenHits, _duelSeenMisses;

    /// <summary>True while a replay is being watched (no recording, no stats).</summary>
    public bool IsWatchingReplay => _activeReplay != null && _level != null;

    /// <summary>
    /// Starts watching a replay. Returns an error message when it can't be played
    /// (level missing or changed), or null on success.
    /// </summary>
    public string? StartReplay(ReplayFile replay)
    {
        if (replay.Tracks.Count == 0 || replay.Local == null)
            return "This replay has no recording.";
        string? path = FindLevelForReplay(replay);
        if (path == null)
            return $"Level not found: \"{replay.LevelName}\". You need the exact level file the replay was recorded on.";

        _pendingReplay = replay;
        _inFreeplay = false;
        StartLoadingLevel(path);
        return null;
    }

    /// <summary>Finds the level file with the replay's hash (local levels, then the official cache).</summary>
    private static string? FindLevelForReplay(ReplayFile replay)
    {
        string folder = FreeplayMenu.GetDefaultLevelsFolder();
        try
        {
            var rels = Directory.GetFiles(folder, "*.*", SearchOption.AllDirectories)
                .Where(FreeplayMenu.IsSupportedLevelFile)
                .Select(f => Path.GetRelativePath(folder, f))
                .ToList();
            // Try files with the recorded name first (cheap), then everything (hashes are cached).
            rels = rels.OrderBy(r => Path.GetFileName(r).Equals(replay.LevelFileName, StringComparison.OrdinalIgnoreCase) ? 0 : 1).ToList();
            foreach (var rel in rels)
            {
                var level = Multiplayer.Transfer.LocalLevelIndex.Instance.Get(Path.GetFullPath(Path.Combine(folder, rel)), rel);
                if (level != null && level.Hash == replay.LevelHash)
                    return level.FullPath;
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[Replay] Level search failed: {ex.Message}");
        }
        // osu!mania difficulties (fork): .osu files in mods/osu
        if (replay.LevelFileName.EndsWith(".osu", StringComparison.OrdinalIgnoreCase) && FindOsuLevelForReplay(replay.LevelHash) is { } osu)
            return osu;
        return Multiplayer.LevelCache.Instance.WriteTempFileForHash(replay.LevelHash);
    }

    /// <summary>Called when a level starts loading: a pending replay becomes active, recording resets.</summary>
    private void BeginReplayStateForLoad()
    {
        _activeReplay = _pendingReplay;
        _pendingReplay = null;
        _replayRecorder = null;
        _soloResults = null; // a new level replaces the results screen (fork)
    }

    /// <summary>Called for each strumline as it is created in SetupLevel.</summary>
    private void ConfigureReplayForStrumline(int index, int chartIndex, NoteRenderer renderer)
    {
        if (_activeReplay != null)
        {
            var track = TrackForStrumline(_activeReplay, index);
            if (track != null)
            {
                renderer.StartReplay(track.Frames, track.InputBuffer, track.GhostTapping, track.SafeFrames, track.SongStartTime);
                renderer.ScoreMultiplier = track.ScoreMultiplier > 0 ? track.ScoreMultiplier : 1.0;
                renderer.SetIsPlayer(true);
                renderer.DisableBotplay = true;
                renderer.MuteMissSound = index != 0;
            }
            if (index == 1 && _activeReplay.IsOnline)
            {
                // The match HUD shows the opponent from these (fed by UpdateDuelReplayStats).
                _opponentName = track?.Username ?? "Opponent";
                _opponentColorHue = track?.ColorHue ?? 180;
                _opponentScore = 0;
                _opponentMisses = _opponentSicks = _opponentGoods = _opponentBads = _opponentShits = 0;
                _opponentHealth = _opponentTargetHealth = 0.5f;
                _duelSeenHits = _duelSeenMisses = 0;
                if (track == null)
                    ShowReplayMessage("The opponent's recording isn't available: only your side is replayed.", 8f);
            }
            return;
        }

        if (index != 0 || _playtestingFromEditor || _onlineSpectating || string.IsNullOrEmpty(FnmmPath) || Directory.Exists(FnmmPath))
            return;

        var recorder = new ReplayRecorder();
        recorder.Track.ChartIndex = chartIndex;
        recorder.Track.Role = _isMultiplayerMatch ? _multiplayerPlayerRole : 0;
        recorder.Track.InputBuffer = NoteRenderer.InputBuffer;
        recorder.Track.GhostTapping = NoteRenderer.GhostTapping;
        recorder.Track.SafeFrames = NoteRenderer.SafeFramesTime;
        recorder.Track.ScoreMultiplier = renderer.ScoreMultiplier;
        var mp = Multiplayer.MultiplayerSettings.Instance;
        recorder.Track.PlayerId = mp.PlayerId;
        recorder.Track.Username = string.IsNullOrWhiteSpace(mp.Username) ? "Player" : mp.Username.Trim();
        recorder.Track.ColorHue = mp.PlayerColorHue;
        if (NoteRenderer.Botplay && !_isMultiplayerMatch)
            recorder.Invalidate("botplay");
        // Freeplay song rate / enemy mode (fork): playback doesn't reproduce them yet.
        if (RunRateChanged || _runEnemy)
            recorder.Invalidate("freeplay modifiers");
        renderer.InputRecorder = recorder.Add;
        _replayRecorder = recorder;
    }

    /// <summary>
    /// Which track drives a strumline. Solo: the only track on strumline 0. Online: strumline 0 is
    /// the saving player (as in the match), strumline 1 the opponent.
    /// </summary>
    private static ReplayTrack? TrackForStrumline(ReplayFile replay, int index)
    {
        if (index == 0) return replay.Local;
        if (index == 1 && replay.IsOnline)
            return replay.Tracks.Where((_, i) => i != replay.LocalTrack).FirstOrDefault();
        return null;
    }

    /// <summary>Online replay: mirror the opponent strumline into the match HUD's opponent numbers.</summary>
    private void UpdateDuelReplayStats()
    {
        if (!IsDuelReplay || _noteRenderers.Count < 2) return;
        var opp = _noteRenderers[1];
        _opponentScore = opp.Score;
        _opponentMisses = opp.Misses;
        _opponentSicks = opp.Sicks;
        _opponentGoods = opp.Goods;
        _opponentBads = opp.Bads;
        _opponentShits = opp.Shits;
        _opponentMaxCombo = Math.Max(_opponentMaxCombo, opp.Combo);
        // Same health rule as the player's bar (+0.023 per hit, -0.0475 per miss); level events
        // that change the opponent's health are not replayed.
        int hits = opp.Sicks + opp.Goods + opp.Bads + opp.Shits;
        int newHits = hits - _duelSeenHits, newMisses = opp.Misses - _duelSeenMisses;
        _duelSeenHits = hits;
        _duelSeenMisses = opp.Misses;
        for (int i = 0; i < newHits; i++) _opponentTargetHealth = Math.Clamp(_opponentTargetHealth + 0.023f, 0f, 1f);
        for (int i = 0; i < newMisses; i++) _opponentTargetHealth = Math.Clamp(_opponentTargetHealth - 0.0475f, 0f, 1f);
        _opponentHealth = _opponentTargetHealth;
    }

    /// <summary>Per frame while playing: botplay or a seek make the recording unusable.</summary>
    private void UpdateReplayRecording()
    {
        UpdateDuelReplayStats();
        if (NoteRenderer.Botplay && !_isMultiplayerMatch) _runAssisted = true; // no personal best (fork)
        if (_replayRecorder is { Valid: true } && NoteRenderer.Botplay && !_isMultiplayerMatch)
            _replayRecorder.Invalidate("botplay");
    }

    private void InvalidateReplayRecording(string reason) => _replayRecorder?.Invalidate(reason);

    private static ReplayResult ResultOf(NoteRenderer r, int maxCombo) => new()
    {
        Score = r.Score, Misses = r.Misses, Sicks = r.Sicks, Goods = r.Goods, Bads = r.Bads, Shits = r.Shits,
        MaxCombo = maxCombo,
        Accuracy = ComputeAccuracy(r.Sicks, r.Goods, r.Bads, r.Shits, r.Misses),
    };

    /// <summary>Song finished normally (solo): save the recording, or check a watched replay.</summary>
    private void OnSoloSongFinishedForReplay()
    {
        if (_noteRenderers.Count == 0) return;
        var player = _noteRenderers[0];

        if (_activeReplay != null)
        {
            CheckReplayResult(_activeReplay.Local, player);
            if (_activeReplay.IsOnline && _noteRenderers.Count > 1 && TrackForStrumline(_activeReplay, 1) is { } oppTrack)
                CheckReplayResult(oppTrack, _noteRenderers[1]);
            return;
        }

        _lastSavedReplayPath = null;
        var recorder = _replayRecorder;
        _replayRecorder = null;
        if (recorder == null) return;
        if (!recorder.Valid || recorder.Track.Frames.Count == 0)
        {
            Console.WriteLine($"[Replay] Not saved ({(recorder.Valid ? "no input" : recorder.InvalidReason)})");
            return;
        }

        try
        {
            var replay = BuildReplayFile(recorder, player, kind: "solo", matchId: "");
            string path = ReplayStore.Save(replay);
            _lastSavedReplayPath = path; // linked to the Freeplay attempt (fork)
            Console.WriteLine($"[Replay] Saved {Path.GetFileName(path)} ({recorder.Track.Frames.Count} frames)");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[Replay] Save failed: {ex.Message}");
        }
    }

    /// <summary>
    /// Online match finished normally: save your side now, then (in the background) upload it to the
    /// transfer server and fetch the opponent's recording, which is added to the same file.
    /// Their frames carry their own song times, so the replay shows when they really pressed,
    /// not when their hits reached you.
    /// </summary>
    private void OnOnlineMatchFinishedForReplay()
    {
        var recorder = _replayRecorder;
        _replayRecorder = null;
        if (recorder == null || !recorder.Valid || recorder.Track.Frames.Count == 0 || _noteRenderers.Count == 0)
            return;

        ReplayFile replay;
        try
        {
            recorder.Track.MatchStartServerMs = Multiplayer.TimeSyncManager.Instance.GetServerTime() - (long)(_songTime * 1000f);
            replay = BuildReplayFile(recorder, _noteRenderers[0], kind: "online", matchId: _multiplayerMatchId ?? "");
            ReplayStore.Save(replay);
            Console.WriteLine($"[Replay] Saved online replay {Path.GetFileName(replay.FilePath)} (your side)");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[Replay] Save failed: {ex.Message}");
            return;
        }

        string me = Multiplayer.MultiplayerSettings.Instance.PlayerId;
        string? opponent = _opponentPlayerId;
        if (string.IsNullOrWhiteSpace(me) || string.IsNullOrWhiteSpace(opponent) || string.IsNullOrEmpty(replay.LevelHash))
            return;
        _ = System.Threading.Tasks.Task.Run(() => ExchangeReplayTracksAsync(replay, me, opponent!));
    }

    private static async System.Threading.Tasks.Task ExchangeReplayTracksAsync(ReplayFile replay, string me, string opponent)
    {
        var mine = replay.Tracks[0];
        byte[] data = ReplayStore.SerializeTrack(mine);
        for (int attempt = 0; attempt < 3; attempt++)
        {
            if (await Multiplayer.Transfer.LevelTransferClient.UploadReplayTrackAsync(me, opponent, replay.LevelHash, data)) break;
            await System.Threading.Tasks.Task.Delay(3000);
        }

        // The opponent may finish a moment later (or their upload may wake the server): poll.
        var deadline = DateTime.UtcNow.AddSeconds(120);
        while (DateTime.UtcNow < deadline)
        {
            var bytes = await Multiplayer.Transfer.LevelTransferClient.DownloadReplayTrackAsync(opponent, me, replay.LevelHash);
            if (bytes != null && ReplayStore.DeserializeTrack(bytes) is { } theirs && theirs.Frames.Count > 0)
            {
                // Same match? Both song starts are in synced server time; an older match differs by minutes.
                if (mine.MatchStartServerMs != 0 && theirs.MatchStartServerMs != 0 &&
                    Math.Abs(mine.MatchStartServerMs - theirs.MatchStartServerMs) > 30_000)
                {
                    await System.Threading.Tasks.Task.Delay(4000);
                    continue;
                }
                try
                {
                    replay.Tracks.Add(theirs);
                    ReplayStore.WriteTo(replay, replay.FilePath);
                    Console.WriteLine($"[Replay] Added {theirs.Username}'s recording ({theirs.Frames.Count} frames)");
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"[Replay] Could not add opponent track: {ex.Message}");
                }
                return;
            }
            await System.Threading.Tasks.Task.Delay(4000);
        }
        Console.WriteLine("[Replay] Opponent recording not received; the replay keeps only your side");
    }

    private ReplayFile BuildReplayFile(ReplayRecorder recorder, NoteRenderer player, string kind, string matchId)
    {
        recorder.Track.SongStartTime = player.SongStartTime;
        recorder.Track.Result = ResultOf(player, _playerMaxCombo);
        return new ReplayFile
        {
            Kind = kind,
            MatchId = matchId,
            GameVersion = typeof(Game1).Assembly.GetName().Version?.ToString(3) ?? "",
            LevelHash = Multiplayer.LevelHasher.CalculateHash(FnmmPath!),
            LevelFileName = Path.GetFileName(FnmmPath!),
            LevelName = _isMultiplayerMatch && !string.IsNullOrWhiteSpace(_multiplayerLevelName)
                ? _multiplayerLevelName!
                : FileDisplayName(FnmmPath),
            DurationSeconds = _songTime,
            LocalTrack = 0,
            Tracks = new List<ReplayTrack> { recorder.Track },
        };
    }

    /// <summary>Compares a watched track's result with the recorded one; warns if it drifted.</summary>
    private void CheckReplayResult(ReplayTrack? track, NoteRenderer renderer)
    {
        if (track == null) return;
        var now = ResultOf(renderer, 0);
        if (now.SameJudgementsAs(track.Result))
        {
            Console.WriteLine($"[Replay] {track.Username}: matches the recorded result ({now.Score})");
            return;
        }
        Console.WriteLine($"[Replay] {track.Username}: DESYNC recorded {track.Result.Score}/{track.Result.Misses} " +
                          $"got {now.Score}/{now.Misses}");
        ShowReplayMessage($"Replay drifted from the original run ({track.Username}: recorded {track.Result.Score}, replayed {now.Score}).");
    }

    private void ShowReplayMessage(string message, float seconds = 6f)
    {
        _replayMessage = message;
        _replayMessageTimer = seconds;
    }

    private void UpdateReplayMessage(float dt)
    {
        if (_replayMessageTimer > 0f)
        {
            _replayMessageTimer -= dt;
            if (_replayMessageTimer <= 0f) _replayMessage = null;
        }
    }

    /// <summary>"REPLAY" banner with the player names while watching.</summary>
    private void DrawReplayBanner(int sw, int sh)
    {
        if (_font == null || _pixel == null) return;
        if (IsWatchingReplay)
        {
            var r = _activeReplay!;
            string who = r.IsOnline && _noteRenderers.Count > 1
                ? $"{r.Local?.Username} {_noteRenderers[0].Score:N0}  vs  {_opponentName} {_noteRenderers[1].Score:N0}"
                : r.Local?.Username ?? "";
            string text = $"REPLAY  |  {who}  |  {DateTimeOffset.FromUnixTimeSeconds(r.CreatedUnix).ToLocalTime():yyyy-MM-dd HH:mm}";
            float scale = Math.Clamp(sh / 720f, 0.6f, 2f) * 0.6f;
            Vector2 size = _font.MeasureUi(text) * scale;
            int padX = (int)(14 * scale / 0.6f), padY = (int)(6 * scale / 0.6f);
            var rect = new Rectangle((int)(sw / 2f - size.X / 2f) - padX, (int)(sh * 0.012f), (int)size.X + padX * 2, (int)size.Y + padY * 2);
            _spriteBatch.Draw(_pixel, rect, Color.Black * 0.6f);
            _spriteBatch.Draw(_pixel, new Rectangle(rect.X, rect.Bottom - 2, rect.Width, 2), new Color(255, 68, 136));
            _spriteBatch.DrawUiString(_font, text, new Vector2(rect.X + padX, rect.Y + padY), Color.White, 0f, Vector2.Zero, scale, SpriteEffects.None, 0f);
        }

        if (_replayMessage != null)
        {
            float scale = Math.Clamp(sh / 720f, 0.6f, 2f) * 0.5f;
            Vector2 size = _font.MeasureUi(_replayMessage) * scale;
            var pos = new Vector2(sw / 2f - size.X / 2f, sh * 0.08f);
            _spriteBatch.Draw(_pixel, new Rectangle((int)pos.X - 10, (int)pos.Y - 5, (int)size.X + 20, (int)size.Y + 10), Color.Black * 0.7f);
            _spriteBatch.DrawUiString(_font, _replayMessage, pos, new Color(255, 210, 80), 0f, Vector2.Zero, scale, SpriteEffects.None, 0f);
        }
    }
}
