using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using FunkyMakerPlayer.Multiplayer.Rooms;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using Microsoft.Xna.Framework.Input;

namespace FunkyMakerPlayer.Multiplayer
{
    /// <summary>
    /// Song select (fork): the Freeplay browser itself (<see cref="FreeplayMenu"/> in online mode,
    /// FreeplayMenu.Online.cs) fed by this menu as its <see cref="IOnlineSongSource"/>: OFFICIAL
    /// (server charts), FNF (levels anybody in the room has) and OSU!MANIA (osu! songs anybody has),
    /// as the room's song types allow. Rows show who has each song; picking sends the choice and goes
    /// back to the waiting room. Downloads / transfers happen afterwards for whoever needs the level.
    /// </summary>
    public partial class MultiplayerMenu : IOnlineSongSource
    {
        private bool _pickingSong;
        private FreeplayMenu? _songPicker;
        private Texture2D? _pickerPixel, _pickerFolderIcon;
        private SpriteFont? _pickerFont;
        private int _pickerW = 1280, _pickerH = 720;
        private int _pickerLocalVersion;

        /// <summary>Game1 hands over what the Freeplay browser needs to draw (once, at startup).</summary>
        public void SetSongPickerResources(Texture2D pixel, SpriteFont? font, Texture2D? folderIcon)
        {
            _pickerPixel = pixel;
            _pickerFont = font;
            _pickerFolderIcon = folderIcon;
        }

        private FreeplayMenu? SongPicker
        {
            get
            {
                if (_songPicker == null && _pickerPixel != null)
                {
                    _songPicker = new FreeplayMenu(_pickerPixel, _pickerFont, "", _pickerFolderIcon, _fontRenderer, online: this);
#if ANDROID
                    _songPicker.IsPointOverOverlayControl = p => _touchControls?.IsPointOnMenuControl(p) == true;
#endif
                    Room.LocalLevelsChanged += () => _pickerLocalVersion++;
                }
                return _songPicker;
            }
        }

        /// <summary>A song preview is playing in the song select (the menu music steps aside).</summary>
        public bool SongPreviewPlaying => _songPicker?.PreviewAudible == true;

        /// <summary>Game1, every frame: the song select's audio preview (stops when it isn't on screen).</summary>
        public void UpdateSongPreview(float dt, bool allowed) =>
            _songPicker?.UpdatePreview(dt, allowed && _state == MenuState.SongSelect);

        /// <summary>The song select is typing into its search box (global hotkeys wait).</summary>
        private bool PickerTyping => _state == MenuState.SongSelect && _songPicker?.IsTypingSearch == true;

        /// <summary>Entering the song select.</summary>
        private void OpenSongPicker()
        {
            _pickerLocalVersion++;
            SongPicker?.OpenOnline();
        }

        private void UpdateSongSelect(KeyboardState kb, MouseState mouse, float dt)
        {
            if (!Room.IsMyTurn || Room.State?.Phase is Protocol.PhaseLoading or Protocol.PhasePlaying
                || (Room.State?.Song != null && !_pickingSong)) // already picked: final
            {
                _pickConfirm = null;
                if (!_pickingSong)
                {
                    ShowInfo(Room.IsMyTurn && Room.State?.Song != null
                        ? "The song for this round was already picked." : "It's no longer your turn to pick.", 3f);
                    TransitionToState(MenuState.WaitingRoom);
                }
                return;
            }
            var picker = SongPicker;
            if (picker == null) return;
            bool confirming = UpdatePickNotice(kb, mouse) || UpdatePickConfirm(kb, mouse);
            // While the popup is open the picker only keeps its input state in sync (it's Busy).
            picker.Update(dt, kb, _pickerW, _pickerH, _pickerW, _pickerH);
            if (confirming) return;
            if (picker.LevelsDeleted)
            {
                // Deleted one of your levels / osu! songs from its "..." menu.
                picker.LevelsDeleted = false;
                RefreshLevelList();
                RescanSharedLevels();
                _levelsChangedForOthers = true;
            }
        }

        private void DrawSongSelect(SpriteBatch sb, SpriteFont font, Texture2D pixel, int sw, int sh)
        {
            _clickableAreas.Clear();
            _pickerW = sw;
            _pickerH = sh;
            SongPicker?.Draw(sb, sw, sh, sw, sh);
            DrawPickConfirm(sb, font, pixel, sw, sh);
            DrawPickNotice(sb, font, pixel, sw, sh);
        }

        // ── IOnlineSongSource ────────────────────────────────────────────────

        string IOnlineSongSource.Title => "PICK A SONG";

        string IOnlineSongSource.Subtitle
        {
            get
            {
                var st = Room.State;
                return $"Your turn in {st?.Name ?? "the room"}  |  {RoomClient.ModeName(st?.Settings.Mode ?? "")}";
            }
        }

        string IOnlineSongSource.RoomInfo
        {
            get
            {
                var st = Room.State;
                if (st == null) return "";
                var s = st.Settings;
                string guests = s.AllowGuestLocalLevels ? "everybody's levels" : "official + host's levels";
                return $"{RoomClient.ModeName(s.Mode)}   |   {SongTypesName(s.SongTypes)}   |   {guests}   |   {st.Members.Count}/{s.MaxPlayers} players";
            }
        }

        int IOnlineSongSource.Version => HashCode.Combine(
            RuntimeHelpers.GetHashCode(Room.State?.Levels ?? (object)this),
            Room.State?.Settings.SongTypes,
            Room.State?.Settings.AllowGuestLocalLevels,
            Room.State?.Settings.Mode,
            RuntimeHelpers.GetHashCode(Room.OfficialLevels),
            Room.Scanning,
            HashCode.Combine(_pickerLocalVersion,
                string.Join(",", Room.State?.BlockedHashes ?? new List<string>()),
                NetworkManager.Instance.MaxUploadBytes));

        bool IOnlineSongSource.Busy => _pickingSong || _pickConfirm != null || _pickNotice != null;

        IReadOnlyList<FreeplayTab> IOnlineSongSource.Tabs => (Room.State?.Settings.SongTypes ?? Protocol.SongTypesBoth) switch
        {
            Protocol.SongTypesFnf => new[] { FreeplayTab.Official, FreeplayTab.Fnf },
            Protocol.SongTypesOsu => new[] { FreeplayTab.Osu },
            _ => new[] { FreeplayTab.Official, FreeplayTab.Fnf, FreeplayTab.Osu },
        };

        IEnumerable<OnlineSong> IOnlineSongSource.Songs(FreeplayTab tab)
        {
            if (tab == FreeplayTab.Official)
            {
                foreach (var l in _officialLevels)
                {
                    string hash = (l.Hash ?? "").ToLowerInvariant();
                    if (hash.Length == 0) continue;
                    // Cached official levels have a file here (details, icon, preview, stars).
                    string? file = null;
                    if (LevelCache.Instance.IsCached(l.Id) &&
                        string.Equals(LevelCache.Instance.GetCachedHash(l.Id), hash, StringComparison.OrdinalIgnoreCase))
                        file = LevelCache.Instance.WriteTempFile(l.Id);
                    yield return new OnlineSong
                    {
                        Key = file ?? FreeplayMenu.OnlineKey(hash),
                        Name = string.IsNullOrWhiteSpace(l.Name) ? "Untitled Level" : l.Name,
                        Hash = hash, Official = true, OfficialId = l.Id, Size = l.FileSize, OnDevice = file != null,
                    };
                }
                yield break;
            }

            var st = Room.State;
            if (st?.Levels == null) yield break;
            bool osuTab = tab == FreeplayTab.Osu;
            // Songs refused this turn stay in the list (greyed out) instead of vanishing.
            var levels = st.Levels.Where(l => (l.Pickable || Room.IsBlocked(l.Hash)) && (l.Kind == Protocol.KindOsu) == osuTab).ToList();
            Transfer.LocalLevel? LocalOf(RoomLevel l)
            {
                var local = l.YouHave ? Room.LocalByHash(l.Hash) : null;
                return local != null && System.IO.File.Exists(local.FullPath) ? local : null;
            }
            // Sections: osu! difficulties go where their set goes (you have any of it / who else has it).
            var sections = new Dictionary<string, (string Group, int Order)>();
            foreach (var g in levels.GroupBy(l => osuTab ? "set:" + l.SetId : "lvl:" + l.Hash))
            {
                var mine = g.Select(LocalOf).Where(x => x != null).ToList();
                sections[g.Key] = SectionOf(st, mine.Count > 0, mine.Count > 0 && mine.All(x => x!.Received), g.SelectMany(l => l.Holders).ToHashSet());
            }
            foreach (var l in levels)
            {
                string? path = LocalOf(l)?.FullPath;
                bool here = path != null;
                var section = sections[osuTab ? "set:" + l.SetId : "lvl:" + l.Hash];
                bool blocked = Room.IsBlocked(l.Hash);
                yield return new OnlineSong
                {
                    Key = path ?? FreeplayMenu.OnlineKey(l.Hash),
                    Name = string.IsNullOrWhiteSpace(l.Name) ? l.FileName : l.Name,
                    Hash = l.Hash, Stars = l.Stars > 0 ? l.Stars : Room.StarsFor(l.Hash), Size = l.Size, SizeNoVideo = l.SizeNoVideo,
                    OnDevice = here, Yours = here, Holders = HoldersFor(l),
                    SetId = l.SetId, SetName = l.SetName, Difficulty = l.Difficulty,
                    Group = section.Group, GroupOrder = section.Order,
                    Duration = l.Duration > 0 ? l.Duration : Room.DurationFor(l.Hash),
                    DisabledTag = blocked ? "REFUSED" : "",
                    DisabledTitle = blocked ? "Refused for this turn." : "",
                    DisabledReason = blocked ? "The lobby voted it down or it couldn't be sent. Pick another song; it's back next turn." : "",
                };
            }

            // Your own songs the server left out because they're bigger than it accepts: shown greyed out
            // at the end (picking one explains why) instead of silently missing.
            if (Room.Scanning) yield break;
            var listed = st.Levels.Select(l => l.Hash).ToHashSet(StringComparer.OrdinalIgnoreCase);
            long maxMb = NetworkManager.Instance.MaxUploadBytes / (1024 * 1024);
            foreach (var local in Room.LocalLevels)
            {
                if (local.IsOsu != osuTab || listed.Contains(local.Hash) || !Room.TooHeavy(local)) continue;
                if (!System.IO.File.Exists(local.FullPath)) continue;
                long size = local.IsOsu && local.SizeNoVideo > 0 && local.SizeNoVideo < local.Size ? local.SizeNoVideo : local.Size;
                yield return new OnlineSong
                {
                    Key = local.FullPath,
                    Name = local.Name,
                    Hash = local.Hash, Stars = Room.StarsFor(local.Hash), Size = local.Size, SizeNoVideo = local.SizeNoVideo,
                    OnDevice = true, Yours = true,
                    Holders = new List<(Color Color, string Name)> { (GetColorFromHue(MultiplayerSettings.Instance.PlayerColorHue), (Room.Me?.Username ?? "You") + " (you)") },
                    SetId = local.SetId, SetName = local.SetName, Difficulty = local.Difficulty,
                    Group = "TOO BIG TO SEND", GroupOrder = 2000,
                    Duration = Room.DurationFor(local.Hash),
                    DisabledTag = "TOO BIG",
                    DisabledTitle = $"Too big to send ({size / (1024.0 * 1024.0):0.0} MB).",
                    DisabledReason = $"The server accepts levels up to {maxMb} MB, so it can't be picked online.",
                };
            }
        }

        /// <summary>
        /// List section of a song: SHARED (you imported it and somebody else has it), YOURS (only you),
        /// DOWNLOADED (you got it from an online room), else the first player (join order) who has it:
        /// "&lt;NAME&gt;'S LEVELS".
        /// </summary>
        private static (string Group, int Order) SectionOf(RoomStatePacket st, bool you, bool downloaded, HashSet<string> others)
        {
            others.Remove(st.YouId);
            if (you && downloaded) return ("DOWNLOADED LEVELS", 2);
            if (you) return others.Count > 0 ? ("SHARED LEVELS", 0) : ("YOUR LEVELS", 1);
            for (int i = 0; i < st.Members.Count; i++)
                if (others.Contains(st.Members[i].Id))
                    return ($"{st.Members[i].Username.ToUpperInvariant()}'S LEVELS", 3 + i);
            return ("OTHER LEVELS", 1000);
        }

        (string Main, string Sub) IOnlineSongSource.EmptyText(FreeplayTab tab)
        {
            var st = Room.State;
            if (tab == FreeplayTab.Official)
                return ("Loading official levels from the server...", "");
            if (Room.Scanning)
                return ($"Scanning your levels... {Room.ScanDone}/{Room.ScanTotal}", "");
            if (tab == FreeplayTab.Osu && st != null && !Protocol.OsuModeAllowed(st.Settings.Mode))
                return ("osu!mania songs are only played in Mirror or Teams", Room.IsHost
                    ? "They have a single strumline: switch the room to Mirror or Teams in the room settings to pick them"
                    : "They have a single strumline: the host can switch the room to Mirror or Teams");
            string restricted = st?.Settings.AllowGuestLocalLevels == false && !Room.IsHost
                ? "This room only allows official levels and the host's levels" : "";
            return tab == FreeplayTab.Osu
                ? ("No osu!mania songs to pick here", restricted.Length > 0 ? restricted : "Put .osz files in mods/osu/ or press I to import (4K mania only)")
                : ("No FNF levels to pick here", restricted.Length > 0 ? restricted : "Put .fnmm / .zip / .rar levels in mods/levels/ or press I to import");
        }

        /// <summary>Song waiting for "are you sure?" (a pick can't be changed once sent), or null.</summary>
        private (SongSelection Selection, string Title)? _pickConfirm;

        void IOnlineSongSource.Pick(OnlineSong song, float stars, float seconds)
        {
            if (_pickingSong || _pickConfirm != null || _pickNotice != null) return;
            string title = song.SetName.Length > 0
                ? (song.Difficulty.Length > 0 ? $"{song.SetName} [{song.Difficulty}]" : song.SetName)
                : song.Name;
            if (song.Disabled)
            {
                // Too big to send / refused this turn: explain instead of sending a pick the server refuses.
                _pickNotice = (title, song.DisabledTitle, song.DisabledReason);
                _inputCooldown = INPUT_COOLDOWN_TIME;
                return;
            }
            var selection = song.Official
                ? new SongSelection { Source = Protocol.SourceOfficial, Hash = song.Hash, Name = song.Name, OfficialId = song.OfficialId, Stars = stars, Duration = seconds }
                : new SongSelection { Source = Protocol.SourceLocal, Hash = song.Hash, Name = song.Name, Stars = stars, Duration = seconds };
            _pickConfirm = (selection, title);
            _inputCooldown = INPUT_COOLDOWN_TIME;
        }

        /// <summary>"Can't pick this song" window (title, headline, explanation), or null.</summary>
        private (string Title, string Headline, string Reason)? _pickNotice;
        private Rectangle _pickNoticeRect;

        /// <summary>The "can't pick" window: any key / click closes it. True while it's open.</summary>
        private bool UpdatePickNotice(KeyboardState kb, MouseState mouse)
        {
            if (_pickNotice == null) return false;
            if (_inputCooldown > 0f) return true;
            string? clicked = ClickedHit(mouse);
            if (clicked == "notice-ok" || IsKeyPressed(kb, Keys.Enter) || IsKeyPressed(kb, Keys.Escape) || IsKeyPressed(kb, Keys.Back)
                || IsKeyPressed(kb, Keys.Space) || (clicked == null && IsLeftClickReleased(mouse)))
            {
                _pickNotice = null;
                FunkyMakerPlayer.UI.UiSfx.Play(FunkyMakerPlayer.UI.UiSound.Back);
                _inputCooldown = INPUT_COOLDOWN_TIME;
            }
            return true;
        }

        private void DrawPickNotice(SpriteBatch sb, SpriteFont font, Texture2D pixel, int sw, int sh)
        {
            if (_pickNotice is not { } n) return;
            float uiScale = GetUiScale(sw, sh);
            sb.Draw(pixel, new Rectangle(0, 0, sw, sh), Color.Black * 0.65f);
            int w = Math.Min(Px(600, uiScale), sw - 32), h = Px(220, uiScale, 160);
            int x = (sw - w) / 2, y = (sh - h) / 2;
            _pickNoticeRect = new Rectangle(x, y, w, h);
            DrawPanel(sb, pixel, x, y, w, h, BG_MEDIUM, ACCENT_ERROR, 2, ACCENT_ERROR);
            int pad = Px(20, uiScale, 12);
            int titleF = FontPx(20, uiScale, 14, 28), textF = FontPx(13, uiScale, 10, 18);
            DrawCenteredTextInPanel(sb, font, TruncateToWidth(font, n.Headline, w - pad * 2, titleF), x, y + Px(18, uiScale), w, ACCENT_ERROR, titleF, shadow: true);
            DrawCenteredTextInPanel(sb, font, TruncateToWidth(font, n.Title, w - pad * 2, textF), x, y + Px(56, uiScale), w, TEXT_PRIMARY, textF);
            var lines = WrapTextToWidth(font, n.Reason, w - pad * 2, textF).Split('\n');
            for (int i = 0; i < lines.Length && i < 3; i++)
                DrawCenteredTextInPanel(sb, font, lines[i], x, y + Px(84, uiScale) + i * Px(20, uiScale), w, TEXT_SECONDARY, textF);
            int bh = Px(36, uiScale, 28), bw = Math.Min(Px(200, uiScale), w - pad * 2);
            DrawRoomButton(sb, font, pixel, uiScale, "notice-ok", new Rectangle(x + (w - bw) / 2, y + h - pad - bh, bw, bh), "OK", ACCENT_WARNING, strong: true);
        }

        /// <summary>The confirmation popup over the song select. True while it's open (the picker waits).</summary>
        private bool UpdatePickConfirm(KeyboardState kb, MouseState mouse)
        {
            if (_pickConfirm is not { } pending) return false;
            if (_inputCooldown > 0f) return true;
            string? clicked = ClickedHit(mouse);
            if (clicked == "pick-yes" || IsKeyPressed(kb, Keys.Enter))
            {
                _pickConfirm = null;
                FunkyMakerPlayer.UI.UiSfx.Play(FunkyMakerPlayer.UI.UiSound.Confirm);
                SendPick(pending.Selection);
            }
            else if (clicked == "pick-no" || IsKeyPressed(kb, Keys.Escape)
                     || IsKeyPressed(kb, Keys.Back) || (clicked == null && IsLeftClickReleased(mouse) && !_pickConfirmRect.Contains(mouse.X, mouse.Y)))
            {
                _pickConfirm = null;
                FunkyMakerPlayer.UI.UiSfx.Play(FunkyMakerPlayer.UI.UiSound.Back);
            }
            _inputCooldown = Math.Max(_inputCooldown, _pickConfirm == null ? INPUT_COOLDOWN_TIME : 0f);
            return true;
        }

        private Rectangle _pickConfirmRect;

        private void DrawPickConfirm(SpriteBatch sb, SpriteFont font, Texture2D pixel, int sw, int sh)
        {
            if (_pickConfirm is not { } pending) return;
            float uiScale = GetUiScale(sw, sh);
            sb.Draw(pixel, new Rectangle(0, 0, sw, sh), Color.Black * 0.65f);
            int w = Math.Min(Px(560, uiScale), sw - 32), h = Px(200, uiScale, 150);
            int x = (sw - w) / 2, y = (sh - h) / 2;
            _pickConfirmRect = new Rectangle(x, y, w, h);
            DrawPanel(sb, pixel, x, y, w, h, BG_MEDIUM, ACCENT_WARNING, 2, ACCENT_WARNING);
            int pad = Px(20, uiScale, 12);
            int titleF = FontPx(20, uiScale, 14, 28), textF = FontPx(13, uiScale, 10, 18);
            DrawCenteredTextInPanel(sb, font, "Pick this song?", x, y + Px(18, uiScale), w, TEXT_PRIMARY, titleF, shadow: true);
            DrawCenteredTextInPanel(sb, font, TruncateToWidth(font, pending.Title, w - pad * 2, textF), x, y + Px(54, uiScale), w, ACCENT_WARNING, textF);
            bool longSong = pending.Selection.Duration >= Protocol.LongSongSeconds && (Room.State?.Members.Count(m => !m.Spectate) ?? 0) > 1;
            DrawCenteredTextInPanel(sb, font, longSong
                    ? $"It's {Clock(pending.Selection.Duration)} long: the other players vote whether to play it."
                    : "Once confirmed you can't change it for this round.",
                x, y + Px(82, uiScale), w, longSong ? ACCENT_WARNING : TEXT_SECONDARY, textF);
            int bh = Px(36, uiScale, 28), bw = (w - pad * 3) / 2, by = y + h - pad - bh;
            DrawRoomButton(sb, font, pixel, uiScale, "pick-no", new Rectangle(x + pad, by, bw, bh), "Cancel", TEXT_SECONDARY);
            DrawRoomButton(sb, font, pixel, uiScale, "pick-yes", new Rectangle(x + pad * 2 + bw, by, bw, bh), "CONFIRM", ACCENT_SUCCESS, strong: true);
        }

        private void SendPick(SongSelection selection)
        {
            if (_pickingSong) return;
            _pickingSong = true;
            Room.SelectSong(selection, r =>
            {
                _pickingSong = false;
                if (!r.Ok)
                {
                    ShowError(r.Error);
                    _songPicker?.ShowToast(r.Error);
                    return;
                }
                TransitionToState(MenuState.WaitingRoom);
            });
        }

        void IOnlineSongSource.Back() => TransitionToState(MenuState.WaitingRoom);

        void IOnlineSongSource.Import() => TriggerLobbyImport();

        void IOnlineSongSource.Refresh()
        {
            RefreshLevelList();
            RescanSharedLevels();
            NetworkManager.Instance.RequestOfficialLevels();
            _pickerLocalVersion++;
        }

        // ── Kept for the level folder watcher (LevelFiles) ───────────────────

        private void AdjustLobbyScroll()
        {
            int visible = Math.Max(1, _visibleRows);
            _levelScrollOffset = Math.Clamp(_levelScrollOffset, 0, Math.Max(0, LobbyRowCount - visible));
        }

        /// <summary>Keeps the old LOCAL row selection inside the list (the song select itself is the Freeplay browser).</summary>
        private void ClampLevelSelection()
        {
            int count = _localRows.Count;
            _levelSelection = count == 0 ? 0 : Math.Clamp(_levelSelection, 0, count - 1);
        }
    }
}
