#nullable enable
using System.Collections.Generic;

namespace FunkyMakerPlayer.Multiplayer.Rooms;

// Wire types shared with the online server (FunkyMakerTransferServer: src/FunkyMakerTransferServer/Protocol.cs).
// Keep both files in sync and bump Protocol.Version on breaking changes: the server refuses
// clients with a different version (Hello) with a clear "update the game" message.

public static class Protocol
{
    /// <summary>
    /// 4 = Cloudflare Workers server: plain WebSocket JSON hub instead of SignalR, R2 chunked uploads
    ///     (equal-size chunks), X-File-Hash on packed uploads.
    /// 3 = osu!mania songs (level kinds, room song types, packed set transfers with variants).
    /// 2 = room system (room list, waiting room, turns, N players). 1 was the old pair-transfer server.
    /// </summary>
    public const int Version = 4;

    // ── Level kinds ──────────────────────────────────────────────────────────
    /// <summary>A Funky Maker level file (.fnmm / .zip / .rar).</summary>
    public const string KindFnf = "fnf";
    /// <summary>
    /// One 4K difficulty of an osu!mania beatmap set. Hash = SHA-256 of its .osu file; what is sent
    /// is the whole set packed as .osz (made by the uploader, so its size / hash are only known once
    /// uploaded).
    /// </summary>
    public const string KindOsu = "osu";

    // ── Room song types ──────────────────────────────────────────────────────
    public const string SongTypesFnf = "fnf";
    public const string SongTypesOsu = "osu";
    public const string SongTypesBoth = "both";

    // ── Transfer variants (osu!: novideo; fnf: chartonly) ────────────────────
    public const string VariantFull = "full";
    /// <summary>The set without its video file(s) (players who chose to skip videos).</summary>
    public const string VariantNoVideo = "novideo";
    /// <summary>FNF level stripped to chart JSON + music only (no sprite PNGs), for low-RAM devices.</summary>
    public const string VariantChartOnly = "chartonly";

    // ── Server → client methods ──────────────────────────────────────────────
    public const string RoomState = "RoomState";
    public const string Chat = "Chat";
    public const string Kicked = "Kicked";
    public const string TransferUpload = "TransferUpload";
    public const string TransferDownload = "TransferDownload";
    public const string TransferFailed = "TransferFailed";
    /// <summary>A pick was dropped for the turn (the lobby voted no / the level couldn't be sent).</summary>
    public const string SongRejected = "SongRejected";
    public const string MatchLoading = "MatchLoading";
    public const string MatchStart = "MatchStart";
    public const string MatchResults = "MatchResults";
    public const string PlayerState = "PlayerState";
    public const string PlayerHit = "PlayerHit";
    public const string PlayerKeyPress = "PlayerKeyPress";
    public const string Pong = "Pong";

    // ── Enumerations (strings on the wire) ───────────────────────────────────
    public const string ModeVersus = "versus";
    public const string ModeTeams = "teams";
    public const string ModeMirror = "mirror";

    public const string SelectTurns = "turns";
    public const string SelectHost = "host";

    public const string SourceOfficial = "official";
    public const string SourceLocal = "local";

    public const string PhaseLobby = "lobby";           // waiting for the chooser to pick a song
    public const string PhaseSongSelected = "selected"; // song picked, players getting the level / ready
    public const string PhaseLoading = "loading";       // match players are loading the level
    public const string PhasePlaying = "playing";

    public const string StatusNotReady = "notready";
    public const string StatusReady = "ready";
    public const string StatusPlaying = "playing";
    public const string StatusSpectating = "spectating";
    /// <summary>Match participant whose song ended (waiting for the others).</summary>
    public const string StatusFinished = "finished";
    /// <summary>Match participant who left the match (Exit Match) and is back in the room.</summary>
    public const string StatusLeftMatch = "leftmatch";

    public const string LevelMissing = "missing";
    public const string LevelDownloading = "downloading";
    public const string LevelReady = "ready";
    public const string LevelFailed = "failed";

    /// <summary>Side 0 = player / Boyfriend chart (chart 0); side 1 = enemy chart (chart 1).</summary>
    public const int SidePlayer = 0;
    public const int SideEnemy = 1;

    public const string TokenHeader = "X-Transfer-Token";
    /// <summary>SHA-256 (hex) of a packed upload (osu! .osz, FNF chartonly): the server can't hash big files itself.</summary>
    public const string FileHashHeader = "X-File-Hash";

    public const int MinPlayers = 2;
    public const int MaxPlayers = 8;
    public const int MaxRoomNameLength = 32;
    public const int MaxChatLength = 200;
    /// <summary>Songs at least this long (seconds) are put to a lobby vote before they are played (server rule).</summary>
    public const int LongSongSeconds = 240;

    /// <summary>osu! songs (one strumline) can be played in Mirror and Teams (both teams play the same chart).</summary>
    public static bool OsuModeAllowed(string mode) => mode is ModeMirror or ModeTeams;
}

// ══════════════════════════════════════════════════════════════════════════
// Session
// ══════════════════════════════════════════════════════════════════════════

public sealed class HelloRequest
{
    public string PlayerId { get; set; } = "";
    public string Username { get; set; } = "";
    /// <summary>Leaderboard color hue (0-360).</summary>
    public int ColorHue { get; set; } = 180;
    public string ClientVersion { get; set; } = "";
    public int ProtocolVersion { get; set; }
    /// <summary>Receive osu! sets without their video (smaller downloads). Sent again when it changes.</summary>
    public bool OsuSkipVideo { get; set; }
    /// <summary>Receive FNF levels as chart + music only (no sprites). Sent again when it changes.</summary>
    public bool FnfChartOnly { get; set; }
}

public sealed class HelloResult
{
    public bool Ok { get; set; }
    public string Error { get; set; } = "";
    public int ProtocolVersion { get; set; } = Protocol.Version;
    public string ServerVersion { get; set; } = "";
    public long MaxUploadBytes { get; set; }
    /// <summary>Highest player limit a host may set (server Transfer:MaxRoomMembers; 0 = old server, use 8).</summary>
    public int MaxRoomPlayers { get; set; }
    /// <summary>True when the server accepts chunked uploads via PUT /api/transfers/{id}/chunks + POST /complete.</summary>
    public bool SupportsChunkedUpload { get; set; }
    /// <summary>True when the server supports FNF chart-only variant transfers.</summary>
    public bool SupportsFnfChartOnly { get; set; }
    /// <summary>Preferred chunk size in bytes for chunked uploads (0 = client default).</summary>
    public int UploadChunkBytes { get; set; }
}

public sealed class OpResult
{
    public bool Ok { get; set; }
    public string Error { get; set; } = "";
    public string RoomId { get; set; } = "";

    public static OpResult Success(string roomId = "") => new() { Ok = true, RoomId = roomId };
    public static OpResult Fail(string error) => new() { Ok = false, Error = error };
}

// ══════════════════════════════════════════════════════════════════════════
// Rooms
// ══════════════════════════════════════════════════════════════════════════

/// <summary>One level a player has in their local levels folder.</summary>
public sealed class LevelEntry
{
    /// <summary>Lower-case hex SHA-256 of the level file (osu!: of the difficulty's .osu file).</summary>
    public string Hash { get; set; } = "";
    /// <summary>Display name (usually the file name without extension).</summary>
    public string Name { get; set; } = "";
    /// <summary>File name with extension (.fnmm / .zip / .rar; osu!: the set's name + .osz).</summary>
    public string FileName { get; set; } = "";
    /// <summary>Bytes (osu!: about the size of the whole set packed).</summary>
    public long Size { get; set; }
    /// <summary>Star rating computed by the game (0 = unknown).</summary>
    public float Stars { get; set; }
    /// <summary>fnf / osu.</summary>
    public string Kind { get; set; } = Protocol.KindFnf;
    /// <summary>osu!: identifies the beatmap set (its difficulties share it) — groups them in the song select.</summary>
    public string SetId { get; set; } = "";
    /// <summary>osu!: "Artist - Title".</summary>
    public string SetName { get; set; } = "";
    /// <summary>osu!: difficulty name.</summary>
    public string Difficulty { get; set; } = "";
    /// <summary>osu!: about the size of the set without its video; 0 = the set has no video.</summary>
    public long SizeNoVideo { get; set; }
    /// <summary>Song length in seconds (end of the last note), 0 = unknown.</summary>
    public float Duration { get; set; }
}

public sealed class RoomSettings
{
    public string Mode { get; set; } = Protocol.ModeVersus;
    public string SongSelection { get; set; } = Protocol.SelectTurns;
    /// <summary>Empty = open room.</summary>
    public string Password { get; set; } = "";
    public int MaxPlayers { get; set; } = 2;
    /// <summary>Guests may pick levels they (or other guests) have locally, not only official / host levels.</summary>
    public bool AllowGuestLocalLevels { get; set; } = true;
    /// <summary>Songs that may be picked: fnf (official + FNF levels) / osu (osu!mania) / both.</summary>
    public string SongTypes { get; set; } = Protocol.SongTypesBoth;
}

public sealed class CreateRoomRequest
{
    public string Name { get; set; } = "";
    public RoomSettings Settings { get; set; } = new();
    public List<LevelEntry> Levels { get; set; } = new();
}

public sealed class JoinRoomRequest
{
    public string RoomId { get; set; } = "";
    public string Password { get; set; } = "";
    public List<LevelEntry> Levels { get; set; } = new();
}

public sealed class RoomPlayerSummary
{
    public string Id { get; set; } = "";
    public string Username { get; set; } = "";
    public int ColorHue { get; set; } = 180;
}

/// <summary>A row of the room browser.</summary>
public sealed class RoomSummary
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public bool HasPassword { get; set; }
    public string Mode { get; set; } = "";
    public string Phase { get; set; } = "";
    /// <summary>Song being played right now (empty unless loading / playing).</summary>
    public string SongName { get; set; } = "";
    public float SongStars { get; set; }
    public string HostName { get; set; } = "";
    public List<RoomPlayerSummary> Players { get; set; } = new();
    public int PlayerCount { get; set; }
    public int MaxPlayers { get; set; }
}

public sealed class SongInfo
{
    public string Source { get; set; } = Protocol.SourceOfficial;
    public string Hash { get; set; } = "";
    public string Name { get; set; } = "";
    public string FileName { get; set; } = "";
    public long Size { get; set; }
    /// <summary>Official level id (download via /api/verified_levels/{id}/download).</summary>
    public string OfficialId { get; set; } = "";
    public float Stars { get; set; }
    /// <summary>Player who picked it.</summary>
    public string PickedBy { get; set; } = "";
    /// <summary>fnf / osu (official levels are fnf).</summary>
    public string Kind { get; set; } = Protocol.KindFnf;
    /// <summary>osu!: see <see cref="LevelEntry"/>.</summary>
    public string SetId { get; set; } = "";
    public string SetName { get; set; } = "";
    public string Difficulty { get; set; } = "";
    public long SizeNoVideo { get; set; }
    /// <summary>Seconds, 0 = unknown.</summary>
    public float Duration { get; set; }
}

public sealed class RoomMember
{
    public string Id { get; set; } = "";
    public string Username { get; set; } = "";
    public int ColorHue { get; set; } = 180;
    public bool IsHost { get; set; }
    /// <summary>notready / ready / playing / spectating.</summary>
    public string Status { get; set; } = Protocol.StatusNotReady;
    /// <summary>Wants to spectate the next round(s) instead of playing.</summary>
    public bool Spectate { get; set; }
    /// <summary>Level status for the current song: missing / downloading / ready / failed.</summary>
    public string LevelStatus { get; set; } = Protocol.LevelMissing;
    /// <summary>0-1 download progress when downloading.</summary>
    public float LevelProgress { get; set; }
    /// <summary>0-1 while this member uploads the round's level for the room, otherwise -1.</summary>
    public float UploadProgress { get; set; } = -1f;
    /// <summary>Mirror vote for the current song: -1 none, 0 player, 1 enemy.</summary>
    public int Vote { get; set; } = -1;
}

/// <summary>A local level somebody in the room has.</summary>
public sealed class RoomLevel
{
    public string Hash { get; set; } = "";
    public string Name { get; set; } = "";
    public string FileName { get; set; } = "";
    public long Size { get; set; }
    public float Stars { get; set; }
    public bool YouHave { get; set; }
    /// <summary>Ids of the other players who have this exact file.</summary>
    public List<string> Holders { get; set; } = new();
    /// <summary>You may pick it on your turn (host's level, or guest levels allowed; and its kind allowed).</summary>
    public bool Pickable { get; set; }
    /// <summary>fnf / osu, and the osu! set info (see <see cref="LevelEntry"/>).</summary>
    public string Kind { get; set; } = Protocol.KindFnf;
    public string SetId { get; set; } = "";
    public string SetName { get; set; } = "";
    public string Difficulty { get; set; } = "";
    public long SizeNoVideo { get; set; }
    public float Duration { get; set; }
}

public sealed class MatchAssignment
{
    public string PlayerId { get; set; } = "";
    /// <summary>0 = player side (chart 0), 1 = enemy side (chart 1).</summary>
    public int Side { get; set; }
    /// <summary>Teams mode: 0 / 1 (same as the side). -1 in other modes.</summary>
    public int Team { get; set; } = -1;
}

public sealed class MatchInfo
{
    public string MatchId { get; set; } = "";
    public string Mode { get; set; } = "";
    public SongInfo Song { get; set; } = new();
    public List<MatchAssignment> Players { get; set; } = new();
    /// <summary>Synced server time (ms) the song starts at; 0 while loading.</summary>
    public long StartTimestamp { get; set; }
    /// <summary>Mirror: the side everybody plays.</summary>
    public int MirrorSide { get; set; } = -1;
    /// <summary>Participants whose level finished loading (server fills it in room states).</summary>
    public List<string> Loaded { get; set; } = new();
}

public sealed class MatchStartPacket
{
    public string MatchId { get; set; } = "";
    public long StartTimestamp { get; set; }
}

public sealed class PlayerResult
{
    public string PlayerId { get; set; } = "";
    public string Username { get; set; } = "";
    public int ColorHue { get; set; } = 180;
    public int Side { get; set; }
    public int Team { get; set; } = -1;
    public long Score { get; set; }
    public float Accuracy { get; set; }
    public int Misses { get; set; }
    public int Sicks { get; set; }
    public int Goods { get; set; }
    public int Bads { get; set; }
    public int Shits { get; set; }
    public int MaxCombo { get; set; }
    /// <summary>1-based place (players sharing a score share a place). Teams: individual rank.</summary>
    public int Place { get; set; }
    public bool Disconnected { get; set; }
}

public sealed class RoundResult
{
    public string MatchId { get; set; } = "";
    public string Mode { get; set; } = "";
    public SongInfo Song { get; set; } = new();
    public List<PlayerResult> Players { get; set; } = new();
    /// <summary>Teams mode: average score of team 0 / team 1.</summary>
    public List<double> TeamScores { get; set; } = new();
    /// <summary>Teams: 0 / 1, or -1 for a draw / other modes.</summary>
    public int WinningTeam { get; set; } = -1;
    public long FinishedAt { get; set; }
}

public sealed class ChatMessage
{
    /// <summary>Empty for system messages.</summary>
    public string PlayerId { get; set; } = "";
    public string Username { get; set; } = "";
    public int ColorHue { get; set; } = 180;
    public string Text { get; set; } = "";
    public bool System { get; set; }
    public long Timestamp { get; set; }
}

/// <summary>Full view of the room, personalised for the receiving player (sent on every change).</summary>
public sealed class RoomStatePacket
{
    public string RoomId { get; set; } = "";
    public string Name { get; set; } = "";
    /// <summary>Settings; Password is only filled in for the host.</summary>
    public RoomSettings Settings { get; set; } = new();
    public bool HasPassword { get; set; }
    public string Phase { get; set; } = Protocol.PhaseLobby;
    /// <summary>In join order (= turn order). The host is always first.</summary>
    public List<RoomMember> Members { get; set; } = new();
    public string HostId { get; set; } = "";
    public string YouId { get; set; } = "";
    /// <summary>Player whose turn it is to pick a song (empty when nobody can pick).</summary>
    public string TurnPlayerId { get; set; } = "";
    public SongInfo? Song { get; set; }
    /// <summary>Current / last match while loading or playing.</summary>
    public MatchInfo? Match { get; set; }
    /// <summary>Why the round can't start yet (shown under the song), empty when it can.</summary>
    public string StartBlocker { get; set; } = "";
    /// <summary>Every local level in the room. Null = unchanged since the last RoomState you got.</summary>
    public List<RoomLevel>? Levels { get; set; }
    /// <summary>Rounds played in this room, oldest first. Null = unchanged since the last RoomState you got.</summary>
    public List<RoundResult>? History { get; set; }
    /// <summary>A long song waiting for the lobby's vote, or null.</summary>
    public SongVoteInfo? SongVote { get; set; }
    /// <summary>Songs that can't be picked again this turn (voted down / couldn't be sent).</summary>
    public List<string> BlockedHashes { get; set; } = new();
}

/// <summary>"Play this long song?": everybody playing except the picker votes (server closes it after 30 s).</summary>
public sealed class SongVoteInfo
{
    public SongInfo Song { get; set; } = new();
    public int Yes { get; set; }
    public int No { get; set; }
    /// <summary>Players asked to vote.</summary>
    public List<string> Eligible { get; set; } = new();
    /// <summary>Who already voted.</summary>
    public List<string> Voted { get; set; } = new();
    /// <summary>Server time (ms) the vote closes.</summary>
    public long EndsAt { get; set; }
    /// <summary>Your vote: 1 yes, 0 no, -1 none.</summary>
    public int YourVote { get; set; } = -1;
}

public sealed class SongRejectedPacket
{
    public string Hash { get; set; } = "";
    public string Name { get; set; } = "";
    public string Reason { get; set; } = "";
    public string PickedBy { get; set; } = "";
}

// ══════════════════════════════════════════════════════════════════════════
// Round flow
// ══════════════════════════════════════════════════════════════════════════

public sealed class SongSelection
{
    public string Source { get; set; } = Protocol.SourceOfficial;
    public string Hash { get; set; } = "";
    public string Name { get; set; } = "";
    public string OfficialId { get; set; } = "";
    public float Stars { get; set; }
    /// <summary>Song length in seconds as this game measured it (0 = unknown); long songs go to a vote.</summary>
    public float Duration { get; set; }
}

public sealed class LevelStatusUpdate
{
    public string Hash { get; set; } = "";
    public string State { get; set; } = Protocol.LevelMissing;
    public float Progress { get; set; }
}

public sealed class TransferUploadPacket
{
    public string TransferId { get; set; } = "";
    public string Token { get; set; } = "";
    public string Hash { get; set; } = "";
    public string FileName { get; set; } = "";
    /// <summary>Exact size to upload (fnf). osu!: 0 — the packed set may be up to <see cref="MaxSize"/>.</summary>
    public long Size { get; set; }
    public string Kind { get; set; } = Protocol.KindFnf;
    /// <summary>osu!: full / novideo.</summary>
    public string Variant { get; set; } = Protocol.VariantFull;
    public long MaxSize { get; set; }
}

public sealed class TransferDownloadPacket
{
    public string TransferId { get; set; } = "";
    public string Token { get; set; } = "";
    /// <summary>The level (osu!: the .osu difficulty's hash, found in the set after importing it).</summary>
    public string Hash { get; set; } = "";
    public string Name { get; set; } = "";
    public string FileName { get; set; } = "";
    public long Size { get; set; }
    public string Kind { get; set; } = Protocol.KindFnf;
    public string Variant { get; set; } = Protocol.VariantFull;
    /// <summary>SHA-256 of the file to download (= Hash for fnf; the packed .osz for osu!).</summary>
    public string FileHash { get; set; } = "";
}

public sealed class TransferFailedPacket
{
    public string Hash { get; set; } = "";
    public string Reason { get; set; } = "";
}

public sealed class KickedPacket
{
    public string Reason { get; set; } = "";
}

// ══════════════════════════════════════════════════════════════════════════
// In-match relay
// ══════════════════════════════════════════════════════════════════════════

public sealed class PlayerStatePacket
{
    public string PlayerId { get; set; } = "";
    public long Score { get; set; }
    public int Combo { get; set; }
    public int MaxCombo { get; set; }
    public float Accuracy { get; set; }
    public int Sicks { get; set; }
    public int Goods { get; set; }
    public int Bads { get; set; }
    public int Shits { get; set; }
    public int Misses { get; set; }
    /// <summary>Song time (s) of the sender.</summary>
    public float SongTime { get; set; }
    /// <summary>The sender quit the match (Exit Match); their score is final.</summary>
    public bool Left { get; set; }
}

public sealed class PlayerHitPacket
{
    public string PlayerId { get; set; } = "";
    public int Lane { get; set; }
    public string Rating { get; set; } = "";
    /// <summary>Hold end in song seconds, or -1.</summary>
    public float HoldEnd { get; set; } = -1f;
    public float SongTime { get; set; }
}

public sealed class PlayerKeyPressPacket
{
    public string PlayerId { get; set; } = "";
    public int Lane { get; set; }
    public float SongTime { get; set; }
}

public sealed class FinishMatchRequest
{
    public string MatchId { get; set; } = "";
    public long Score { get; set; }
    public float Accuracy { get; set; }
    public int Misses { get; set; }
    public int Sicks { get; set; }
    public int Goods { get; set; }
    public int Bads { get; set; }
    public int Shits { get; set; }
    public int MaxCombo { get; set; }
    /// <summary>Left before the song ended (Exit Match): ranked last like a disconnect.</summary>
    public bool Quit { get; set; }
}
