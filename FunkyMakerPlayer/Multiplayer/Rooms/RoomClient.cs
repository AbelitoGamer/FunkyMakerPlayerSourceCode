using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using FunkyMakerPlayer.Multiplayer.Transfer;

namespace FunkyMakerPlayer.Multiplayer.Rooms
{
    /// <summary>
    /// Client side of the room system (fork). Keeps the room browser list, the current room's state,
    /// chat, the current match and the other players' live stats, and gets the round's level onto
    /// this machine (official download, server transfer, or the local file). Everything runs on the
    /// game thread: <see cref="NetworkManager"/> raises server messages from its Update.
    ///
    /// The menu (<see cref="MultiplayerMenu"/>) draws from this; <see cref="Game1"/> starts / watches
    /// matches when <see cref="MatchReadyToLoad"/> fires.
    /// </summary>
    public sealed class RoomClient
    {
        public static RoomClient Instance { get; } = new();

        private NetworkManager Net => NetworkManager.Instance;

        private RoomClient()
        {
            Net.RoomStateReceived += OnRoomState;
            Net.ChatReceived += OnChat;
            Net.KickedReceived += p => { var why = p.Reason; ResetRoom(); Kicked?.Invoke(why); };
            Net.TransferUploadReceived += OnTransferUpload;
            Net.TransferDownloadReceived += OnTransferDownload;
            Net.TransferFailedReceived += p =>
            {
                if (CurrentSong?.Hash == p.Hash) { LevelError = p.Reason; LevelState = Protocol.LevelFailed; }
                Error?.Invoke(p.Reason);
            };
            Net.SongRejectedReceived += p => { if (InRoom) SongRejected?.Invoke(p); };
            Net.MatchLoadingReceived += OnMatchLoading;
            Net.MatchStartReceived += OnMatchStart;
            Net.MatchResultsReceived += OnMatchResults;
            Net.PlayerStateReceived += p => LiveStates[p.PlayerId] = p;
            Net.OnReconnected += Rejoin;
            Net.OnDisconnected += _ => { if (InRoom) ResetRoom(); };
            Net.OnLevelListReceived += p =>
            {
                OfficialLevels = p.Levels;
                _ = Task.Run(() => LevelCache.Instance.WarmFromDisk(p.Levels));
                if (CurrentSong != null) EnsureSongLevel();
            };
        }

        // ══════════════════════════════════════════════════════════════════
        // Events (game thread)
        // ══════════════════════════════════════════════════════════════════

        /// <summary>You were removed from the room (kick, joined elsewhere). Arg: reason.</summary>
        public event Action<string>? Kicked;
        /// <summary>Something failed (shown as a toast).</summary>
        public event Action<string>? Error;
        public event Action<ChatMessage>? ChatAdded;
        /// <summary>A level was downloaded into the levels folder (lists should rescan).</summary>
        public event Action? LocalLevelsChanged;
        /// <summary>
        /// The match is loading and this client has the level: Game1 should load it.
        /// Args: match, level path, your assignment (null = spectating).
        /// </summary>
        public event Action<MatchInfo, string, MatchAssignment?>? MatchReadyToLoad;
        public event Action<MatchStartPacket>? MatchStarting;
        public event Action<RoundResult>? RoundFinished;
        /// <summary>A pick was dropped for this turn (lobby voted no / couldn't be sent); the picker picks again.</summary>
        public event Action<SongRejectedPacket>? SongRejected;

        // ══════════════════════════════════════════════════════════════════
        // Room browser
        // ══════════════════════════════════════════════════════════════════

        public List<RoomSummary> Rooms { get; private set; } = new();
        public bool RoomsLoaded { get; private set; }
        private bool _listing;

        public void RefreshRooms()
        {
            if (_listing || !Net.IsConnected) return;
            _listing = true;
            Net.Call<List<RoomSummary>>("ListRooms", Array.Empty<object?>(), list =>
            {
                _listing = false;
                Rooms = list ?? new List<RoomSummary>();
                RoomsLoaded = true;
            }, _ => _listing = false);
        }

        // ══════════════════════════════════════════════════════════════════
        // Current room
        // ══════════════════════════════════════════════════════════════════

        /// <summary>Latest room state (Levels / History merged), or null outside a room.</summary>
        public RoomStatePacket? State { get; private set; }
        public bool InRoom => State != null;
        public string RoomId => State?.RoomId ?? "";
        public bool IsHost => State != null && State.HostId == State.YouId;
        public string YouId => State?.YouId ?? MultiplayerSettings.Instance.PlayerId;
        public RoomMember? Me => State?.Members.FirstOrDefault(m => m.Id == State.YouId);
        public bool IsMyTurn => State != null && State.TurnPlayerId == State.YouId;
        public List<ChatMessage> Chat { get; } = new();
        /// <summary>Bumped on every room state (cheap "did it change" check for the UI).</summary>
        public int Version { get; private set; }

        private string _joinedRoomId = "";
        private string _joinedPassword = "";
        /// <summary>
        /// A create / join is in flight. The server sends the first RoomState and the chat history
        /// before it answers the call, so they are kept here and applied when the answer arrives.
        /// </summary>
        private bool _joining;
        private RoomStatePacket? _earlyState;
        private readonly List<ChatMessage> _earlyChat = new();

        public RoomMember? MemberById(string id) => State?.Members.FirstOrDefault(m => m.Id == id);

        public void CreateRoom(string name, RoomSettings settings, Action<OpResult> done)
        {
            var request = new CreateRoomRequest { Name = name, Settings = settings, Levels = LevelEntries() };
            BeginJoin();
            Net.Call<OpResult>("CreateRoom", new object?[] { request }, r =>
            {
                if (r.Ok) PrepareForRoom(r.RoomId, settings.Password);
                else _joining = false;
                done(r);
            }, e => { _joining = false; done(OpResult.Fail(e)); });
        }

        public void JoinRoom(string roomId, string password, Action<OpResult> done)
        {
            var request = new JoinRoomRequest { RoomId = roomId, Password = password, Levels = LevelEntries() };
            BeginJoin();
            Net.Call<OpResult>("JoinRoom", new object?[] { request }, r =>
            {
                if (r.Ok) PrepareForRoom(r.RoomId, password);
                else _joining = false;
                done(r);
            }, e => { _joining = false; done(OpResult.Fail(e)); });
        }

        private void BeginJoin()
        {
            _joining = true;
            _earlyState = null;
            _earlyChat.Clear();
        }

        private void PrepareForRoom(string roomId, string password)
        {
            bool sameRoom = _joinedRoomId == roomId;
            if (!sameRoom) Chat.Clear();
            _joinedRoomId = roomId;
            _joinedPassword = password ?? "";
            _joining = false;
            if (!sameRoom)
            {
                foreach (var m in _earlyChat) OnChat(m);
            }
            _earlyChat.Clear();
            var early = _earlyState;
            _earlyState = null;
            if (early != null && early.RoomId == roomId) OnRoomState(early);
        }

        public void LeaveRoom()
        {
            if (_joinedRoomId.Length > 0) Net.Send("LeaveRoom");
            ResetRoom();
        }

        private void ResetRoom()
        {
            _joining = false;
            _earlyState = null;
            _earlyChat.Clear();
            State = null;
            _joinedRoomId = "";
            _joinedPassword = "";
            Chat.Clear();
            Match = null;
            LastResult = null;
            LiveStates.Clear();
            _songHash = "";
            LevelState = Protocol.LevelMissing;
            LevelError = "";
            _transferCts?.Cancel();
            foreach (var uCts in _uploadCtsByKey.Values)
            {
                try { uCts.Cancel(); } catch { }
                try { uCts.Dispose(); } catch { }
            }
            _uploadCtsByKey.Clear();
            _uploadId = "";
            UploadProgress = -1f;
            Version++;
        }

        /// <summary>After an automatic reconnect: join the same room again (same PlayerId keeps your place).</summary>
        private void Rejoin()
        {
            if (_joinedRoomId.Length == 0) return;
            string id = _joinedRoomId, pw = _joinedPassword;
            JoinRoom(id, pw, r =>
            {
                if (!r.Ok) { ResetRoom(); Kicked?.Invoke($"Lost the room after reconnecting: {r.Error}"); }
            });
        }

        public void UpdateSettings(RoomSettings settings, Action<OpResult> done)
        {
            Net.Call<OpResult>("UpdateSettings", new object?[] { settings }, r =>
            {
                if (r.Ok) _joinedPassword = settings.Password;
                done(r);
            }, e => done(OpResult.Fail(e)));
        }

        public void Kick(string playerId) =>
            Net.Call<OpResult>("Kick", new object?[] { playerId }, r => { if (!r.Ok) Error?.Invoke(r.Error); });

        public void SendChat(string text)
        {
            text = text.Trim();
            if (text.Length == 0) return;
            Net.Send("SendChat", text.Length > Protocol.MaxChatLength ? text[..Protocol.MaxChatLength] : text);
        }

        public void SetSpectate(bool spectate) => Net.Send("SetSpectate", spectate);

        public void SelectSong(SongSelection selection, Action<OpResult> done) =>
            Net.Call<OpResult>("SelectSong", new object?[] { selection }, done, e => done(OpResult.Fail(e)));

        public void SetReady(bool ready) =>
            Net.Call<OpResult>("SetReady", new object?[] { ready }, r => { if (!r.Ok) Error?.Invoke(r.Error); });

        /// <summary>Long-song vote: play it (true) or not.</summary>
        public void VoteSong(bool yes) =>
            Net.Call<OpResult>("VoteSong", new object?[] { yes }, r => { if (!r.Ok) Error?.Invoke(r.Error); });

        /// <summary>The song can't be picked again this turn (voted down / couldn't be sent).</summary>
        public bool IsBlocked(string hash) => State?.BlockedHashes?.Contains(hash) == true;

        /// <summary>
        /// The level is bigger than the server accepts (its smallest variant: osu! without video), so the
        /// server leaves it out of the room's list. The song select shows it greyed out instead of hiding it.
        /// </summary>
        public bool TooHeavy(LocalLevel l)
        {
            long max = Net.MaxUploadBytes;
            if (max <= 0) return false;
            long smallest = l.Kind == Protocol.KindOsu && l.SizeNoVideo > 0 && l.SizeNoVideo < l.Size ? l.SizeNoVideo : l.Size;
            return smallest > max;
        }

        /// <param name="then">Runs after the server accepted the vote (Mirror: then READY, in that order).</param>
        public void VoteSide(int side, Action? then = null) =>
            Net.Call<OpResult>("VoteSide", new object?[] { side }, r =>
            {
                if (!r.Ok) { Error?.Invoke(r.Error); return; }
                then?.Invoke();
            });

        private void OnRoomState(RoomStatePacket p)
        {
            if (_joining && p.RoomId != _joinedRoomId)
            {
                // Merge Levels / History like below once the join answer tells us the room.
                if (_earlyState != null && _earlyState.RoomId == p.RoomId)
                {
                    p.Levels ??= _earlyState.Levels;
                    p.History ??= _earlyState.History;
                }
                _earlyState = p;
                return;
            }
            if (_joinedRoomId.Length == 0 || p.RoomId != _joinedRoomId) return;
            p.Levels ??= State?.Levels ?? new List<RoomLevel>();
            p.History ??= State?.History ?? new List<RoundResult>();
            State = p;
            Version++;
            if (p.Match != null && (Match == null || Match.MatchId != p.Match.MatchId))
                Match = p.Match;
            if (p.Match != null && Match != null && p.Match.StartTimestamp > 0) Match.StartTimestamp = p.Match.StartTimestamp;
            EnsureSongLevel();
            if (_pendingWatch && LevelState == Protocol.LevelReady) TryRaiseMatchLoad();
            LeaveMatchIfNotPlaying(p);
        }

        /// <summary>
        /// The server still counts you as playing a match this game isn't in (it was closed or
        /// crashed and you joined again, or you went back to the room some other way): tell it you
        /// left, so the round doesn't wait for you and the others' CPU takes your strumline.
        /// </summary>
        private void LeaveMatchIfNotPlaying(RoomStatePacket p)
        {
            var match = p.Match;
            if (match == null || p.Phase is not (Protocol.PhaseLoading or Protocol.PhasePlaying)) return;
            if (_pendingWatch || ActiveMatchId == match.MatchId) return;
            if (match.Players.All(a => a.PlayerId != YouId)) return;
            if (Me?.Status != Protocol.StatusPlaying) return;
            if (_quitSentFor == match.MatchId) return; // once per match
            _quitSentFor = match.MatchId;
            _leftMatchIds.Add(match.MatchId);
            Console.WriteLine($"[Rooms] Not in match {match.MatchId} any more: telling the server");
            Net.Send("FinishMatch", new FinishMatchRequest { MatchId = match.MatchId, Quit = true });
        }

        /// <summary>Matches you quit (or that this game isn't in): watching them only spectates.</summary>
        private readonly HashSet<string> _leftMatchIds = new(StringComparer.Ordinal);
        private string _quitSentFor = "";

        /// <summary>Game1: you quit this match (Exit Match / failed load).</summary>
        public void MarkLeftMatch(string matchId)
        {
            if (matchId.Length > 0) _leftMatchIds.Add(matchId);
        }

        /// <summary>You're out of the current match (quit / finished / dropped): you can only watch it.</summary>
        public bool LeftCurrentMatch =>
            Match != null && (_leftMatchIds.Contains(Match.MatchId)
                || (State?.Match?.MatchId == Match.MatchId && Me?.Status is Protocol.StatusLeftMatch or Protocol.StatusFinished));

        private void OnChat(ChatMessage m)
        {
            if (_joining)
            {
                _earlyChat.Add(m);
                return;
            }
            if (_joinedRoomId.Length == 0) return;
            Chat.Add(m);
            if (Chat.Count > 200) Chat.RemoveAt(0);
            if (!string.IsNullOrEmpty(m.PlayerId)) UserDirectoryClient.Instance.RegisterKnownUser(m.PlayerId, m.Username, m.ColorHue);
            ChatAdded?.Invoke(m);
        }

        // ══════════════════════════════════════════════════════════════════
        // Local levels (hashes + star ratings) shared with the room
        // ══════════════════════════════════════════════════════════════════

        private readonly ConcurrentDictionary<string, LocalLevel> _localByHash = new(StringComparer.Ordinal);
        private readonly ConcurrentDictionary<string, float> _starsByHash = new(StringComparer.Ordinal);
        private readonly ConcurrentDictionary<string, float> _durationByHash = new(StringComparer.Ordinal);
        private int _scanSession;
        public bool Scanning { get; private set; }
        public int ScanDone, ScanTotal;

        /// <summary>Hashes of your levels (relative path → level).</summary>
        public IReadOnlyCollection<LocalLevel> LocalLevels => _localByHash.Values.ToList();
        public bool HasLocal(string hash) => _localByHash.ContainsKey(hash);
        public LocalLevel? LocalByHash(string hash) => _localByHash.TryGetValue(hash, out var l) ? l : null;
        public float StarsFor(string hash) => _starsByHash.TryGetValue(hash, out var s) ? s : 0f;
        /// <summary>Song length (s) of one of your levels, 0 until it was analysed.</summary>
        public float DurationFor(string hash) => _durationByHash.TryGetValue(hash, out var d) ? d : 0f;

        /// <summary>End of the last note in seconds (what the Freeplay details call the song length).</summary>
        public static float ChartLength(FnmmData data)
        {
            double last = 0;
            foreach (var strum in data.Strumlines)
                foreach (var lane in strum)
                    foreach (var n in lane)
                        last = Math.Max(last, data.ToSeconds(n.Start) + Math.Max(0, data.ToSeconds(n.Duration)));
            return double.IsFinite(last) ? (float)last : 0f;
        }

        /// <summary>Hashes the level files (cached on disk), then sends the list to the room and rates them.</summary>
        public void ScanLocalLevels(string levelsFolder, IReadOnlyList<string> relativePaths)
        {
            int session = ++_scanSession;
            Scanning = true;
            ScanDone = 0;
            ScanTotal = relativePaths.Count;
            var paths = relativePaths.ToList();
            _ = Task.Run(() =>
            {
                var levels = LocalLevelIndex.Instance.Scan(levelsFolder, paths, (d, t) => { ScanDone = d; ScanTotal = t; });
                levels.AddRange(ScanOsuLevels());
                Net.Post(() =>
                {
                    if (session != _scanSession) return;
                    _localByHash.Clear();
                    foreach (var l in levels) _localByHash.TryAdd(l.Hash, l);
                    Scanning = false;
                    PushLevels();
                    EnsureSongLevel();
                });
                // Star ratings (player chart), then push again with them.
                bool any = false;
                foreach (var l in levels)
                {
                    if (session != _scanSession) return;
                    if (_starsByHash.ContainsKey(l.Hash)) continue;
                    float stars = 0f, length = 0f;
                    try
                    {
                        var data = FnmmLoader.TryLoadChartData(l.FullPath);
                        if (data != null)
                        {
                            stars = (float)ChartDifficulty.Calculate(data, 0);
                            length = ChartLength(data);
                        }
                    }
                    catch { }
                    _durationByHash[l.Hash] = length;
                    _starsByHash[l.Hash] = float.IsFinite(stars) ? stars : 0f;
                    any = true;
                }
                if (any) Net.Post(() => { if (session == _scanSession) PushLevels(); });
            });
        }

        /// <summary>Every 4K difficulty of your osu! sets (mods/osu), hashed like level files (background thread).</summary>
        private static List<LocalLevel> ScanOsuLevels()
        {
            var result = new List<LocalLevel>();
            try
            {
                string root = Osu.OsuLibrary.RootFolder;
                foreach (var set in Osu.OsuLibrary.ScanSets())
                {
                    var (full, noVideo) = Osu.OsuLibrary.SetSizes(set);
                    if (full <= 0) continue;
                    foreach (var diff in set.Difficulties)
                    {
                        string rel = Path.GetRelativePath(root, diff.Path);
                        var hashed = LocalLevelIndex.Instance.Get(diff.Path, rel);
                        if (hashed == null) continue;
                        result.Add(new LocalLevel
                        {
                            RelativePath = rel, FullPath = diff.Path, Hash = hashed.Hash, Size = full, SizeNoVideo = noVideo,
                            Kind = Protocol.KindOsu, SetFolder = set.Folder, SetId = set.Id, SetName = set.Name, Difficulty = diff.Name,
                        });
                    }
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[Rooms] osu! scan failed: {ex.Message}");
            }
            return result;
        }

        private List<LevelEntry> LevelEntries() => _localByHash.Values.Select(l => new LevelEntry
        {
            Hash = l.Hash, Name = l.Name, FileName = l.FileName, Size = l.Size, Stars = StarsFor(l.Hash),
            Kind = l.Kind, SetId = l.SetId, SetName = l.SetName, Difficulty = l.Difficulty, SizeNoVideo = l.SizeNoVideo,
            Duration = DurationFor(l.Hash),
        }).ToList();

        private void PushLevels()
        {
            if (InRoom) Net.Send("UpdateLevels", LevelEntries());
        }

        // ══════════════════════════════════════════════════════════════════
        // The round's level
        // ══════════════════════════════════════════════════════════════════

        public List<OfficialLevelInfo> OfficialLevels { get; private set; } = new();

        /// <summary>Song being prepared or played (selected song, else the match's).</summary>
        public SongInfo? CurrentSong => State?.Song ?? State?.Match?.Song ?? Match?.Song;

        /// <summary>Your status for <see cref="CurrentSong"/>: missing / downloading / ready / failed.</summary>
        public string LevelState { get; private set; } = Protocol.LevelMissing;
        public float LevelProgress { get; private set; }
        public string LevelError { get; private set; } = "";
        /// <summary>Path of the current song's level file once it's here.</summary>
        public string? LevelPath { get; private set; }

        private string _songHash = "";
        private LevelDownloader? _officialDownloader;
        private CancellationTokenSource? _transferCts;
        private float _lastReportedProgress = -1f;

        /// <summary>Makes sure the current song's level is here or on its way, and tells the server.</summary>
        private void EnsureSongLevel()
        {
            var song = CurrentSong;
            if (song == null)
            {
                _songHash = "";
                LevelPath = null;
                LevelState = Protocol.LevelMissing;
                return;
            }

            bool newSong = song.Hash != _songHash;
            if (newSong)
            {
                _songHash = song.Hash;
                LevelPath = null;
                LevelError = "";
                LevelProgress = 0f;
                LevelState = Protocol.LevelMissing;
            }
            if (LevelState == Protocol.LevelReady && LevelPath != null && File.Exists(LevelPath))
            {
                // The server forgets after reconnects; keep it informed.
                var me = Me;
                if (me != null && me.LevelStatus != Protocol.LevelReady) ReportLevel(Protocol.LevelReady, 1f);
                return;
            }

            if (song.Source == Protocol.SourceLocal)
            {
                var local = LocalByHash(song.Hash);
                if (local != null && File.Exists(local.FullPath))
                {
                    LevelPath = local.FullPath;
                    LevelState = Protocol.LevelReady;
                    LevelProgress = 1f;
                    ReportLevel(Protocol.LevelReady, 1f);
                    return;
                }
                if (song.Kind == Protocol.KindFnf && MultiplayerSettings.Instance.ChartOnlyMode)
                {
                    string chartPath = ChartOnlyCachePath(song.Hash);
                    if (File.Exists(chartPath))
                    {
                        LevelPath = chartPath;
                        LevelState = Protocol.LevelReady;
                        LevelProgress = 1f;
                        ReportLevel(Protocol.LevelReady, 1f);
                        return;
                    }
                }
                // else: the server sends TransferDownload when the file is on its way.
                return;
            }

            // Official level: from the RAM / disk cache, else download it.
            var info = OfficialInfo(song);
            if (LevelCache.Instance.IsCached(info.Id) || LevelCache.Instance.TryRestoreFromDisk(info))
            {
                if (string.Equals(LevelCache.Instance.GetCachedHash(info.Id), song.Hash, StringComparison.OrdinalIgnoreCase))
                {
                    LevelPath = LevelCache.Instance.WriteTempFile(info.Id);
                    if (LevelPath != null)
                    {
                        LevelState = Protocol.LevelReady;
                        LevelProgress = 1f;
                        ReportLevel(Protocol.LevelReady, 1f);
                        return;
                    }
                }
            }
            if (LevelState is Protocol.LevelDownloading) return;
            StartOfficialDownload(info);
        }

        private static OfficialLevelInfo OfficialInfo(SongInfo song) => new()
        {
            Id = song.OfficialId, Name = song.Name, Hash = song.Hash, FileSize = song.Size, Filename = song.FileName,
        };

        private void StartOfficialDownload(OfficialLevelInfo info)
        {
            LevelState = Protocol.LevelDownloading;
            LevelProgress = 0f;
            ReportLevel(Protocol.LevelDownloading, 0f);
            string hash = info.Hash;
            var dl = _officialDownloader = new LevelDownloader();
            dl.OnDownloadBytesProgress += (pct, _, _) => Net.Post(() =>
            {
                if (_songHash != hash) return;
                LevelProgress = pct / 100f;
                ReportProgressThrottled();
            });
            dl.OnDownloadComplete += (_, _) => Net.Post(() =>
            {
                if (_songHash != hash) return;
                LevelState = Protocol.LevelMissing; // re-evaluated from the cache
                EnsureSongLevel();
            });
            dl.OnDownloadError += err => Net.Post(() =>
            {
                if (_songHash != hash) return;
                LevelState = Protocol.LevelFailed;
                LevelError = err;
                ReportLevel(Protocol.LevelFailed, 0f);
                Error?.Invoke($"Download failed: {err}");
            });
            _ = dl.DownloadLevel(info, Net.ServerUrl);
        }

        /// <summary>Retry after a failed download / transfer.</summary>
        public void RetryLevel()
        {
            LevelState = Protocol.LevelMissing;
            LevelError = "";
            var song = CurrentSong;
            if (song == null) return;
            if (song.Source == Protocol.SourceLocal) ReportLevel(Protocol.LevelMissing, 0f); // asks the server to send it again
            else EnsureSongLevel();
        }

        private void ReportProgressThrottled()
        {
            if (Math.Abs(LevelProgress - _lastReportedProgress) < 0.05f && LevelProgress < 1f) return;
            _lastReportedProgress = LevelProgress;
            ReportLevel(Protocol.LevelDownloading, LevelProgress);
        }

        private void ReportLevel(string state, float progress)
        {
            if (!InRoom || _songHash.Length == 0) return;
            Net.Send("SetLevelStatus", new LevelStatusUpdate { Hash = _songHash, State = state, Progress = progress });
            if (state == Protocol.LevelReady && _pendingWatch) TryRaiseMatchLoad();
        }

        private static string ChartOnlyCachePath(string hash) =>
            Path.Combine(MultiplayerSettings.GetStorageDirectory(), "chartonly_cache", SafeFileName($"{hash}.fnmm"));

        /// <summary>Called when the player toggles Chart-Only mode (settings or waiting room).</summary>
        public void OnChartOnlyModeChanged()
        {
            var song = CurrentSong;
            if (!InRoom || song == null || song.Source != Protocol.SourceLocal || song.Kind != Protocol.KindFnf)
                return;

            bool chartOnly = MultiplayerSettings.Instance.ChartOnlyMode;
            bool hasFull = LocalByHash(song.Hash) is { } local && File.Exists(local.FullPath);
            if (!hasFull && ((!chartOnly && LevelState == Protocol.LevelReady) || LevelState == Protocol.LevelDownloading))
            {
                // Switched mode while holding only chart-only or downloading the previous variant: re-request.
                _transferCts?.Cancel();
                LevelPath = null;
                LevelState = Protocol.LevelMissing;
                LevelProgress = 0f;
            }
            EnsureSongLevel();
        }

        /// <summary>0-1 while you upload the round's level for the room (osu! / chartonly: also while packing = 0), else -1.</summary>
        public float UploadProgress { get; private set; } = -1f;
        private string _uploadId = "";
        private readonly Dictionary<string, CancellationTokenSource> _uploadCtsByKey = new(StringComparer.Ordinal);

        private void OnTransferUpload(TransferUploadPacket t)
        {
            var local = LocalByHash(t.Hash);
            if (local == null || !File.Exists(local.FullPath))
            {
                Error?.Invoke("The server asked for a level you no longer have.");
                return;
            }
            string uploadKey = $"{t.Hash}:{t.Variant}";
            if (_uploadCtsByKey.TryGetValue(uploadKey, out var prevCts))
            {
                try { prevCts.Cancel(); } catch { }
                try { prevCts.Dispose(); } catch { }
            }
            var cts = new CancellationTokenSource();
            _uploadCtsByKey[uploadKey] = cts;

            bool osu = t.Kind == Protocol.KindOsu;
            bool chartOnly = !osu && t.Variant == Protocol.VariantChartOnly;
            _uploadId = t.TransferId;
            UploadProgress = 0f;
            Console.WriteLine($"[Rooms] Uploading {local.FileName}{(osu || chartOnly ? $" ({t.Variant})" : "")} for the room");
            _ = Task.Run(async () =>
            {
                string? packed = null;
                try
                {
                    string path = local.FullPath;
                    if (osu)
                    {
                        // osu!: the whole set, packed now (without the video for the novideo variant).
                        var set = Osu.OsuLibrary.SetOf(local.FullPath) ?? throw new IOException("The osu! set is no longer there.");
                        packed = Path.Combine(Path.GetTempPath(), $"fmp-{t.TransferId}.osz");
                        long size = Osu.OsuLibrary.PackSet(set, withVideo: t.Variant != Protocol.VariantNoVideo, packed);
                        if (t.MaxSize > 0 && size > t.MaxSize)
                            throw new IOException($"The set is {size / (1024 * 1024)} MB, more than the server allows ({t.MaxSize / (1024 * 1024)} MB).");
                        path = packed;
                    }
                    else if (chartOnly)
                    {
                        // FNF chart-only variant: strip all sprite PNGs and send only chart + audio (~3-12 MB).
                        packed = Path.Combine(Path.GetTempPath(), $"fmp-{t.TransferId}-chartonly.fnmm");
                        long size = FnmmLoader.PackChartOnlyFnmm(local.FullPath, packed);
                        if (t.MaxSize > 0 && size > t.MaxSize)
                            throw new IOException($"The chart-only file is {size / (1024 * 1024)} MB, more than the server allows ({t.MaxSize / (1024 * 1024)} MB).");
                        path = packed;
                    }
                    long total = Math.Max(1, new FileInfo(path).Length);
                    Net.Post(() => { if (_uploadId == t.TransferId) UploadProgress = 0f; });
                    var progress = new Progress<long>(bytes => Net.Post(() =>
                    {
                        if (_uploadId == t.TransferId) UploadProgress = Math.Clamp((float)bytes / total, 0f, 1f);
                    }));
                    await LevelTransferClient.UploadAsync(t, path, progress, cts.Token);
                }
                catch (OperationCanceledException) when (cts.IsCancellationRequested)
                {
                    Console.WriteLine($"[Rooms] Cancelled superseded/abandoned upload {t.TransferId}");
                }
                catch (Exception ex)
                {
                    Net.Post(() => Error?.Invoke($"Sending the level failed: {ex.Message}"));
                }
                finally
                {
                    Net.Post(() =>
                    {
                        if (_uploadCtsByKey.TryGetValue(uploadKey, out var cur) && ReferenceEquals(cur, cts))
                            _uploadCtsByKey.Remove(uploadKey);
                        try { cts.Dispose(); } catch { }
                        if (_uploadId == t.TransferId) { _uploadId = ""; UploadProgress = -1f; }
                    });
                    if (packed != null) { try { File.Delete(packed); } catch { } }
                }
            });
        }

        private void OnTransferDownload(TransferDownloadPacket t)
        {
            if (HasLocal(t.Hash)) return;
            _transferCts?.Cancel();
            var cts = _transferCts = new CancellationTokenSource();
            bool osu = t.Kind == Protocol.KindOsu;
            bool chartOnly = !osu && t.Variant == Protocol.VariantChartOnly;
            string folder = chartOnly
                ? Path.Combine(MultiplayerSettings.GetStorageDirectory(), "chartonly_cache")
                : Path.Combine(FreeplayMenu.GetDefaultLevelsFolder(), "Received");
            if (CurrentSong?.Hash == t.Hash)
            {
                LevelState = Protocol.LevelDownloading;
                LevelProgress = 0f;
            }
            string hash = t.Hash;
            var progress = new Progress<long>(bytes => Net.Post(() =>
            {
                if (_songHash == hash && t.Size > 0) LevelProgress = (float)bytes / t.Size;
            }));
            _ = Task.Run(async () =>
            {
                if (osu) folder = Path.GetTempPath();
                Directory.CreateDirectory(folder);
                string part = Path.Combine(folder, osu ? $"fmp-{t.TransferId}.part" : $".{t.TransferId}.part");
                try
                {
                    string got = await LevelTransferClient.DownloadAsync(t, part, progress, cts.Token);
                    string expected = t.FileHash.Length > 0 ? t.FileHash : t.Hash;
                    if (!string.Equals(got, expected, StringComparison.OrdinalIgnoreCase))
                        throw new IOException("The downloaded file doesn't match the level (hash check failed).");
                    if (chartOnly)
                    {
                        // Store in app-storage chartonly_cache/<hash>.fnmm outside mods/levels/ so
                        // Freeplay / LocalLevelIndex never list it and we never claim full sprites.
                        string finalChartPath = ChartOnlyCachePath(t.Hash);
                        Directory.CreateDirectory(Path.GetDirectoryName(finalChartPath)!);
                        if (File.Exists(finalChartPath)) File.Delete(finalChartPath);
                        File.Move(part, finalChartPath);
                        Net.Post(() =>
                        {
                            Net.Send("CompleteDownload", t.TransferId, true, null);
                            if (_songHash == t.Hash && MultiplayerSettings.Instance.ChartOnlyMode)
                            {
                                LevelPath = finalChartPath;
                                LevelState = Protocol.LevelReady;
                                LevelProgress = 1f;
                                ReportLevel(Protocol.LevelReady, 1f);
                            }
                        });
                        return;
                    }
                    LocalLevel level;
                    string final;
                    if (osu)
                    {
                        // osu!: a packed set. Import it into mods/osu like a dropped .osz, then
                        // find the picked difficulty in it.
                        string osz = Path.Combine(folder, string.IsNullOrWhiteSpace(t.FileName) ? $"fmp-{t.TransferId}.osz" : SafeFileName(t.FileName));
                        if (File.Exists(osz)) File.Delete(osz);
                        File.Move(part, osz);
                        try
                        {
                            final = Osu.OsuLibrary.ImportReceived(osz, t.Hash, out string importError)
                                    ?? throw new IOException(importError);
                        }
                        finally
                        {
                            try { File.Delete(osz); } catch { }
                        }
                        var set = Osu.OsuLibrary.SetOf(final);
                        var (full, noVideo) = set != null ? Osu.OsuLibrary.SetSizes(set) : (t.Size, 0L);
                        LocalLevelIndex.Instance.Remember(final, t.Hash);
                        level = new LocalLevel
                        {
                            RelativePath = Path.GetRelativePath(Osu.OsuLibrary.RootFolder, final), FullPath = final, Hash = t.Hash,
                            Size = full, SizeNoVideo = noVideo, Kind = Protocol.KindOsu, SetFolder = set?.Folder ?? Path.GetDirectoryName(final)!,
                            SetId = set?.Id ?? "", SetName = set?.Name ?? t.Name,
                            Difficulty = set?.Difficulties.FirstOrDefault(d => string.Equals(d.Path, final, StringComparison.OrdinalIgnoreCase))?.Name ?? "",
                        };
                        got = t.Hash;
                    }
                    else
                    {
                        final = UniquePath(folder, string.IsNullOrWhiteSpace(t.FileName) ? t.Name + ".fnmm" : t.FileName);
                        File.Move(part, final);
                        LocalLevelIndex.Instance.Remember(final, got);
                        string rel = Path.GetRelativePath(FreeplayMenu.GetDefaultLevelsFolder(), final);
                        level = new LocalLevel { RelativePath = rel, FullPath = final, Size = t.Size, Hash = got };
                    }
                    Net.Post(() =>
                    {
                        _localByHash[got] = level;
                        Net.Send("CompleteDownload", t.TransferId, true, null);
                        if (_songHash == got)
                        {
                            LevelPath = final;
                            LevelState = Protocol.LevelReady;
                            LevelProgress = 1f;
                            ReportLevel(Protocol.LevelReady, 1f);
                        }
                        LocalLevelsChanged?.Invoke();
                    });
                }
                catch (Exception ex)
                {
                    try { if (File.Exists(part)) File.Delete(part); } catch { }
                    if (cts.IsCancellationRequested) return;
                    Net.Post(() =>
                    {
                        Net.Send("CompleteDownload", t.TransferId, false, ex.Message);
                        if (_songHash == hash) { LevelState = Protocol.LevelFailed; LevelError = ex.Message; }
                    });
                }
            });
        }

        /// <summary>A file name from the server made safe for this file system.</summary>
        private static string SafeFileName(string name)
        {
            var invalid = Path.GetInvalidFileNameChars();
            string s = new string(Path.GetFileName(name.Replace('\\', '/').Split('/').Last()).Select(c => invalid.Contains(c) ? '_' : c).ToArray()).Trim();
            return s.Length == 0 ? "set.osz" : s;
        }

        private static string UniquePath(string folder, string fileName)
        {
            string path = Path.Combine(folder, fileName);
            string stem = Path.GetFileNameWithoutExtension(fileName), ext = Path.GetExtension(fileName);
            for (int i = 2; File.Exists(path); i++)
                path = Path.Combine(folder, $"{stem} ({i}){ext}");
            return path;
        }

        // ══════════════════════════════════════════════════════════════════
        // Match
        // ══════════════════════════════════════════════════════════════════

        /// <summary>Current match (loading / playing), or null.</summary>
        public MatchInfo? Match { get; private set; }
        public RoundResult? LastResult { get; private set; }
        /// <summary>Latest live stats of the other players, by PlayerId.</summary>
        public Dictionary<string, PlayerStatePacket> LiveStates { get; } = new();
        /// <summary>Waiting for the level before loading the match (spectator who lacks it).</summary>
        private bool _pendingWatch;
        /// <summary>The match id Game1 is in (playing or watching).</summary>
        public string ActiveMatchId { get; private set; } = "";

        /// <summary>Your side in the current match; null when you don't play it (any more).</summary>
        public MatchAssignment? MyAssignment => LeftCurrentMatch ? null : Match?.Players.FirstOrDefault(p => p.PlayerId == YouId);
        public bool IsParticipant => MyAssignment != null;
        public bool MatchInProgress => State?.Phase is Protocol.PhaseLoading or Protocol.PhasePlaying && Match != null;

        private void OnMatchLoading(MatchInfo info)
        {
            Match = info;
            LiveStates.Clear();
            var me = info.Players.FirstOrDefault(p => p.PlayerId == YouId);
            bool spectateNext = Me?.Spectate == true;
            // Players always load; spectators who chose to spectate watch automatically.
            if (me != null || spectateNext)
            {
                _pendingWatch = true;
                EnsureSongLevel();
                TryRaiseMatchLoad();
            }
        }

        /// <summary>"Watch" button for players who joined mid-match (or skipped the start).</summary>
        public void WatchCurrentMatch()
        {
            if (Match == null) return;
            _pendingWatch = true;
            EnsureSongLevel();
            TryRaiseMatchLoad();
        }

        private void TryRaiseMatchLoad()
        {
            var match = Match;
            if (!_pendingWatch || match == null) return;
            if (LevelState != Protocol.LevelReady || LevelPath == null || !File.Exists(LevelPath)) return;
            _pendingWatch = false;
            ActiveMatchId = match.MatchId;
            MatchReadyToLoad?.Invoke(match, LevelPath, MyAssignment);
        }

        /// <summary>Game1 finished loading the level for the match.</summary>
        public void ReportMatchLoaded(string matchId)
        {
            if (IsParticipant) Net.Send("MatchLoaded", matchId);
        }

        private void OnMatchStart(MatchStartPacket p)
        {
            if (Match != null && Match.MatchId == p.MatchId) Match.StartTimestamp = p.StartTimestamp;
            MatchStarting?.Invoke(p);
        }

        public void FinishMatch(FinishMatchRequest result) => Net.Send("FinishMatch", result);

        private void OnMatchResults(RoundResult r)
        {
            LastResult = r;
            _pendingWatch = false;
            RoundFinished?.Invoke(r);
            if (Match?.MatchId == r.MatchId) Match = null;
            ActiveMatchId = "";
        }

        /// <summary>Game1 left the match screen (results closed, or quit watching).</summary>
        public void LeftMatchScreen() => ActiveMatchId = "";

        // ══════════════════════════════════════════════════════════════════
        // Display helpers
        // ══════════════════════════════════════════════════════════════════

        public static string ModeName(string mode) => mode switch
        {
            Protocol.ModeTeams => "Teams",
            Protocol.ModeMirror => "Mirror",
            _ => "Versus",
        };

        public static string SideName(int side) => side == Protocol.SideEnemy ? "Enemy" : "Player";

        public static string SelectionName(string sel) => sel == Protocol.SelectHost ? "Host only" : "Turns";

        public string NameOf(string playerId) =>
            MemberById(playerId)?.Username
            ?? LastResult?.Players.FirstOrDefault(p => p.PlayerId == playerId)?.Username
            ?? UserDirectoryClient.Instance.ResolveUsername(playerId, "Player");

        public int HueOf(string playerId) =>
            MemberById(playerId)?.ColorHue
            ?? LastResult?.Players.FirstOrDefault(p => p.PlayerId == playerId)?.ColorHue
            ?? 180;
    }
}
