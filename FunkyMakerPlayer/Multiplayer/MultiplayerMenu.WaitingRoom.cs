using System;
using System.Collections.Generic;
using System.Linq;
using FunkyMakerPlayer.Multiplayer.Rooms;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using Microsoft.Xna.Framework.Input;

namespace FunkyMakerPlayer.Multiplayer
{
    /// <summary>
    /// Waiting room (fork). Left: players in join order (= turn order) with colour, host badge, status
    /// (ready / not ready / playing / spectating), whose turn it is, level download state and the
    /// host's KICK. Centre: the current song slot (SELECT SONG on your turn, the picked song with the
    /// ready / vote controls, or the match in progress) and the rounds played so far (click = standings).
    /// Right: chat. Buttons: READY, SPECTATE, WATCH MATCH, ROOM SETTINGS.
    /// </summary>
    public partial class MultiplayerMenu
    {
        private float _awaitingRoomState;
        private int _roundSelection = -1;   // index into history (newest first); -1 = none
        private int _roundScroll;
        private int _roundsVisible = 4;
        private RoundResult? _roundDetail;
        private string? _kickConfirmId;
        private int _chatScroll;            // lines scrolled up from the bottom
        private int _playerScroll;          // first player row shown
        private Rectangle _playersPanelRect;

        private void EnterWaitingRoom()
        {
            TransitionToState(MenuState.WaitingRoom);
            _awaitingRoomState = 6f;
            _roundSelection = -1;
            _roundScroll = 0;
            _roundDetail = null;
            _kickConfirmId = null;
            _chatScroll = 0;
            _playerScroll = 0;
            RescanSharedLevels();
            if (Room.OfficialLevels.Count == 0) NetworkManager.Instance.RequestOfficialLevels();
        }

        private List<RoundResult> RoundsNewestFirst() =>
            (Room.State?.History ?? new List<RoundResult>()).AsEnumerable().Reverse().ToList();

        private void UpdateWaitingRoom(KeyboardState kb, MouseState mouse, float dt)
        {
            if (_awaitingRoomState > 0f)
            {
                _awaitingRoomState = Room.InRoom ? 0f : _awaitingRoomState - dt;
                if (!Room.InRoom) return;
            }
            if (UpdateTextField(kb, dt)) return;
            if (_inputCooldown > 0f) return;
            var st = Room.State;
            if (st == null) return;

            string? clicked = ClickedHit(mouse);

            // ── Popups ───────────────────────────────────────────────────
            if (_roundDetail != null)
            {
                if (clicked == "detail-close" || IsKeyPressed(kb, Keys.Escape) || IsKeyPressed(kb, Keys.Enter) || (clicked == null && IsLeftClickReleased(mouse)))
                {
                    _roundDetail = null;
                    _inputCooldown = INPUT_COOLDOWN_TIME;
                }
                return;
            }
            if (_kickConfirmId != null)
            {
                if (clicked == "kick-yes" || IsKeyPressed(kb, Keys.Y))
                {
                    Room.Kick(_kickConfirmId);
                    _kickConfirmId = null;
                }
                else if (clicked == "kick-no" || IsKeyPressed(kb, Keys.Escape) || IsKeyPressed(kb, Keys.N))
                {
                    _kickConfirmId = null;
                }
                _inputCooldown = INPUT_COOLDOWN_TIME;
                return;
            }

            var rounds = RoundsNewestFirst();
            var me = Room.Me;
            bool inMatch = st.Phase is Protocol.PhaseLoading or Protocol.PhasePlaying;

            // ── Scrolling ────────────────────────────────────────────────
            int wheel = mouse.ScrollWheelValue - _prevMouse.ScrollWheelValue;
            if (wheel != 0)
            {
                if (_chatPanelRect.Contains(_lastMousePos))
                    _chatScroll = Math.Max(0, _chatScroll + (wheel > 0 ? 2 : -2));
                else if (_playersPanelRect.Contains(_lastMousePos))
                    _playerScroll = Math.Max(0, _playerScroll + (wheel > 0 ? -1 : 1)); // clamped when drawn
                else if (rounds.Count > 0)
                    _roundScroll = Math.Clamp(_roundScroll + (wheel > 0 ? -1 : 1), 0, Math.Max(0, rounds.Count - _roundsVisible));
            }
            if (rounds.Count > 0)
            {
                if (IsKeyPressed(kb, Keys.Down)) _roundSelection = Math.Min(rounds.Count - 1, _roundSelection + 1);
                if (IsKeyPressed(kb, Keys.Up)) _roundSelection = Math.Max(-1, _roundSelection - 1);
                if (_roundSelection >= 0)
                {
                    if (_roundSelection < _roundScroll) _roundScroll = _roundSelection;
                    if (_roundSelection >= _roundScroll + _roundsVisible) _roundScroll = _roundSelection - _roundsVisible + 1;
                }
            }

            // ── Clicks ───────────────────────────────────────────────────
            if (clicked != null)
            {
                if (clicked == "chat") { BeginChat(); return; }
                if (clicked == "pick") { OpenSongSelect(); return; }
                if (clicked == "ready") { ToggleReady(); return; }
                if (clicked == "spectate") { SpectateOrWatch(); return; }
                if (clicked == "watch") { Room.WatchCurrentMatch(); return; }
                if (clicked == "settings") { OpenRoomSettings(Room.IsHost ? RoomSettingsMode.Edit : RoomSettingsMode.View); return; }
                if (clicked == "vote0") { VoteAndReady(Protocol.SidePlayer); return; }
                if (clicked == "vote1") { VoteAndReady(Protocol.SideEnemy); return; }
                if (clicked == "chartonly") { ToggleChartOnlyMode(); return; }
                if (clicked == "mychar") { CycleWaitingRoomCharacter(); return; }
                if (clicked == "retry") { Room.RetryLevel(); return; }
                if (clicked.StartsWith("kick:")) { _kickConfirmId = clicked[5..]; return; }
                if (clicked.StartsWith("round:") && int.TryParse(clicked[6..], out int ri) && ri < rounds.Count)
                {
                    _roundSelection = ri;
                    _roundDetail = rounds[ri];
                    return;
                }
            }

            // ── Keys ─────────────────────────────────────────────────────
            if (IsKeyPressed(kb, Keys.T)) { BeginChat(); return; }
            if (IsKeyPressed(kb, Keys.R)) { ToggleReady(); return; }
            if (IsKeyPressed(kb, Keys.S)) { SpectateOrWatch(); return; }
            if (IsKeyPressed(kb, Keys.M)) { ToggleChartOnlyMode(); return; }
            if (IsKeyPressed(kb, Keys.B)) { CycleWaitingRoomCharacter(); return; }
            if (IsKeyPressed(kb, Keys.C)) { OpenRoomSettings(Room.IsHost ? RoomSettingsMode.Edit : RoomSettingsMode.View); return; }
            if (IsKeyPressed(kb, Keys.W) && inMatch) { Room.WatchCurrentMatch(); return; }
            if (st.Settings.Mode == Protocol.ModeMirror && st.Song != null)
            {
                if (IsKeyPressed(kb, Keys.D1)) VoteAndReady(Protocol.SidePlayer);
                if (IsKeyPressed(kb, Keys.D2)) VoteAndReady(Protocol.SideEnemy);
            }
            if (IsKeyPressed(kb, Keys.Enter) || IsKeyPressed(kb, Keys.Space) || IsKeyPressed(kb, Keys.P))
            {
                if (_roundSelection >= 0 && _roundSelection < rounds.Count && !IsKeyPressed(kb, Keys.P))
                    _roundDetail = rounds[_roundSelection];
                else if (Room.IsMyTurn && !inMatch)
                    OpenSongSelect();
                _inputCooldown = INPUT_COOLDOWN_TIME;
                return;
            }
            if (IsKeyPressed(kb, Keys.Escape) || IsRectClicked(mouse, _backButtonRect))
            {
                Room.LeaveRoom();
                OpenRoomBrowser();
            }
        }

        /// <summary>Unsent chat text kept when the chat box loses focus.</summary>
        private string _chatDraft = "";

        private void BeginChat()
        {
            _chatScroll = 0;
            BeginTextField("chat", _chatDraft, Protocol.MaxChatLength, text => { _chatDraft = ""; Room.SendChat(text); },
                sticky: true, title: "Chat", blur: v => _chatDraft = v);
        }

        private void ToggleReady()
        {
            var st = Room.State;
            var me = Room.Me;
            if (st?.Song == null || me == null) return;
            if (me.Spectate) { ShowError("You're spectating. Press S to play."); return; }
            if (Room.LevelState != Protocol.LevelReady) { ShowError("Wait until you have the level."); return; }
            Room.SetReady(me.Status != Protocol.StatusReady);
            _inputCooldown = INPUT_COOLDOWN_TIME;
        }

        /// <summary>Mirror: voting a side also means "I'm ready" (it used to need READY as well).</summary>
        private void VoteAndReady(int side)
        {
            Room.VoteSide(side, () =>
            {
                var me = Room.Me;
                if (me != null && !me.Spectate && Room.State?.Song != null && Room.LevelState == Protocol.LevelReady
                    && me.Status != Protocol.StatusReady)
                    Room.SetReady(true);
            });
            _inputCooldown = INPUT_COOLDOWN_TIME;
        }

        private void ToggleChartOnlyMode()
        {
            var settings = MultiplayerSettings.Instance;
            settings.ChartOnlyMode = !settings.ChartOnlyMode;
            settings.Save();
            Room.OnChartOnlyModeChanged();
            if (NetworkManager.Instance.IsConnected) ResendProfile();
            ShowInfo(settings.ChartOnlyMode
                ? "Chart Only ON: FNF matches load & download only chart + music (no sprites)."
                : "Chart Only OFF: FNF matches load full sprites & stage.", 3.5f);
            _inputCooldown = INPUT_COOLDOWN_TIME;
        }

        private void CycleWaitingRoomCharacter()
        {
            var chars = Editors.Character.CharacterLibrary.ScanCharacters(bakedOnly: false);
            if (chars.Count == 0)
            {
                ShowInfo("No custom characters in mods/characters/ yet. Create one in Editor Hub -> Character Editor!", 3.5f);
                _inputCooldown = INPUT_COOLDOWN_TIME;
                return;
            }
            CycleEquippedCharacter(1);
            string label = GetEquippedCharacterLabel(MultiplayerSettings.Instance.EquippedCharacter);
            ShowInfo($"My Online Character (Local): {label}", 2.8f);
            _inputCooldown = INPUT_COOLDOWN_TIME;
        }

        /// <summary>A match you aren't playing is in progress and you aren't watching it yet.</summary>
        private bool CanWatchMatch(RoomStatePacket st) =>
            st.Phase is Protocol.PhaseLoading or Protocol.PhasePlaying && st.Match != null
            && !Room.IsParticipant && Room.ActiveMatchId.Length == 0;

        /// <summary>
        /// SPECTATE: while a match you aren't in is running it watches that match (it used to only toggle
        /// "spectate next rounds", watching was W only); otherwise toggles spectating.
        /// </summary>
        private void SpectateOrWatch()
        {
            var st = Room.State;
            if (st != null && CanWatchMatch(st))
                Room.WatchCurrentMatch();
            else
                Room.SetSpectate(!(Room.Me?.Spectate ?? false));
            _inputCooldown = INPUT_COOLDOWN_TIME;
        }

        private void OpenSongSelect()
        {
            if (!Room.IsMyTurn) { ShowError("It's not your turn to pick."); return; }
            // A pick is final (server rule): no song select once one was picked.
            if (Room.State?.Song != null) { ShowError("The song for this round was already picked."); return; }
            _selectedSource = LevelSource.Official;
            _levelSelection = 0;
            _levelScrollOffset = 0;
            TransitionToState(MenuState.SongSelect);
        }

        // ══════════════════════════════════════════════════════════════════
        // Draw
        // ══════════════════════════════════════════════════════════════════

        private Rectangle _chatPanelRect;

        private void DrawWaitingRoom(SpriteBatch sb, SpriteFont font, Texture2D pixel, int sw, int sh)
        {
            _clickableAreas.Clear();
            _holderSquares.Clear();
            float uiScale = GetUiScale(sw, sh);
            var st = Room.State;
            if (st == null)
            {
                DrawScreenHeader(sb, font, pixel, sw, uiScale, "JOINING ROOM", "Please wait...", ACCENT_PRIMARY, "ESC  Leave");
                return;
            }

            string privacy = st.HasPassword ? "Password" : "Open";
            string subtitle = $"ID {st.RoomId}   •   {RoomClient.ModeName(st.Settings.Mode)}   •   {RoomClient.SelectionName(st.Settings.SongSelection)}   •   {st.Members.Count}/{st.Settings.MaxPlayers} players   •   {privacy}";
            int headerBottom = DrawScreenHeader(sb, font, pixel, sw, uiScale, st.Name.ToUpperInvariant(), subtitle, ModeColor(st.Settings.Mode), "ESC  Leave");
            int footerY = DrawFooterBar(sb, font, pixel, sw, sh, uiScale,
                "T: Chat  |  R: Ready  |  S: Spectate  |  M: Chart Only  |  B: My Char  |  P: Pick song  |  C: Room settings");

            int margin = Px(22, uiScale, 12);
            int gap = Px(14, uiScale, 8);
            int top = headerBottom + Px(8, uiScale);
            int height = Math.Max(200, footerY - top - Px(10, uiScale));
            // ClampUi, not Math.Clamp: below 720 px wide the upper bound drops under the lower one and
            // Math.Clamp threw (that closed the game when resizing the window in a room).
            int leftW = ClampUi(Px(300, uiScale), 220, sw / 4 + 40);
            int rightW = ClampUi(Px(330, uiScale), 230, sw / 4 + 60);
            int centerX = margin + leftW + gap;
            int centerW = sw - margin * 2 - leftW - rightW - gap * 2;

            DrawPlayersPanel(sb, font, pixel, uiScale, st, new Rectangle(margin, top, leftW, height));
            DrawRoundsPanel(sb, font, pixel, uiScale, st, new Rectangle(centerX, top, centerW, height));
            DrawChatPanel(sb, font, pixel, uiScale, new Rectangle(centerX + centerW + gap, top, rightW, height));

            if (_roundDetail != null) DrawRoundDetail(sb, font, pixel, uiScale, sw, sh, _roundDetail);
            if (_kickConfirmId != null) DrawKickConfirm(sb, font, pixel, uiScale, sw, sh);
            DrawHolderTooltip(sb, font, pixel, uiScale, sw, sh);
        }

        private void DrawPlayersPanel(SpriteBatch sb, SpriteFont font, Texture2D pixel, float uiScale, RoomStatePacket st, Rectangle p)
        {
            DrawPanel(sb, pixel, p.X, p.Y, p.Width, p.Height, BG_MEDIUM, BORDER_SUBTLE, 1);
            int pad = Px(12, uiScale, 8);
            int headF = FontPx(13, uiScale, 10, 18);
            DrawText(sb, font, $"PLAYERS  {st.Members.Count}/{st.Settings.MaxPlayers}", new Vector2(p.X + pad, p.Y + pad), TEXT_DISABLED * _fadeAlpha, headF);

            // Buttons at the bottom.
            int btnGap = Px(6, uiScale, 4);
            var me = Room.Me;
            bool inMatch = st.Phase is Protocol.PhaseLoading or Protocol.PhasePlaying;
            var buttons = new List<(string Key, string Label, Color Accent, bool Enabled, bool Strong)>();
            if (st.Song != null && me != null && !me.Spectate)
            {
                bool ready = me.Status == Protocol.StatusReady;
                bool canReady = Room.LevelState == Protocol.LevelReady;
                buttons.Add(("ready", ready ? "R  UNREADY" : "R  I'M READY", ACCENT_SUCCESS, canReady, !ready && canReady));
            }
            if (CanWatchMatch(st))
                buttons.Add(("spectate", "S  WATCH MATCH", ACCENT_PRIMARY, true, true));
            else
                buttons.Add(("spectate", me?.Spectate == true ? "S  SPECTATING (ON)" : "S  SPECTATE", ACCENT_SECONDARY, true, me?.Spectate == true));
            bool chartOnlyOn = MultiplayerSettings.Instance.ChartOnlyMode;
            buttons.Add(("chartonly", chartOnlyOn ? "M  CHART ONLY (ON)" : "M  CHART ONLY (OFF)", chartOnlyOn ? ACCENT_SUCCESS : TEXT_SECONDARY, true, chartOnlyOn));
            string eqSlug = MultiplayerSettings.Instance.EquippedCharacter;
            bool hasCustomChar = !string.IsNullOrWhiteSpace(eqSlug);
            string charBtnLbl = hasCustomChar ? $"B  CHAR: {GetEquippedCharacterLabel(eqSlug)}" : "B  CHAR: DEFAULT";
            buttons.Add(("mychar", charBtnLbl, hasCustomChar ? new Color(178, 102, 255) : TEXT_SECONDARY, true, hasCustomChar));
            buttons.Add(("settings", Room.IsHost ? "C  ROOM SETTINGS" : "C  VIEW SETTINGS", ACCENT_WARNING, true, false));
            int btnH = buttons.Count >= 5 ? Px(30, uiScale, 22) : (buttons.Count >= 4 ? Px(34, uiScale, 25) : Px(38, uiScale, 28));
            int by = p.Bottom - pad - buttons.Count * (btnH + btnGap) + btnGap;
            foreach (var b in buttons)
            {
                DrawRoomButton(sb, font, pixel, uiScale, b.Key, new Rectangle(p.X + pad, by, p.Width - pad * 2, btnH), b.Label, b.Accent, b.Enabled, b.Strong, FontPx(13, uiScale, 10, 18));
                by += btnH + btnGap;
            }

            // Player rows (scrollable: wheel / drag over the list; they used to be cut off).
            _playersPanelRect = p;
            int listTop = p.Y + pad + Px(24, uiScale);
            int rowH = Px(50, uiScale, 40);
            int rowGap = Px(4, uiScale, 3);
            int limit = p.Bottom - pad - buttons.Count * (btnH + btnGap) - Px(6, uiScale);
            int visibleRows = Math.Max(1, (limit - listTop + rowGap) / (rowH + rowGap));
            _playerScroll = Math.Clamp(_playerScroll, 0, Math.Max(0, st.Members.Count - visibleRows));
            bool scrollable = st.Members.Count > visibleRows;
            int barW = scrollable ? Px(8, uiScale, 6) : 0;
            int rowY = listTop;
            int nameF = FontPx(15, uiScale, 11, 21);
            int smallF = FontPx(11, uiScale, 9, 15);
            int pillH = Px(17, uiScale, 13);
            if (scrollable)
                DrawScrollBar(sb, pixel, p.Right - pad - barW + Px(2, uiScale), listTop, visibleRows * (rowH + rowGap) - rowGap,
                    st.Members.Count, visibleRows, _playerScroll, ACCENT_PRIMARY);
            for (int i = _playerScroll; i < st.Members.Count; i++)
            {
                if (rowY + rowH > limit) break;
                var m = st.Members[i];
                var r = new Rectangle(p.X + pad, rowY, p.Width - pad * 2 - (scrollable ? barW + Px(4, uiScale) : 0), rowH);
                bool isMe = m.Id == st.YouId;
                bool turn = m.Id == st.TurnPlayerId;
                bool hovered = r.Contains(_lastMousePos);
                Color c = GetColorFromHue(m.ColorHue);
                DrawPanel(sb, pixel, r.X, r.Y, r.Width, r.Height, hovered ? BG_LIGHT : BG_DARK, turn ? ACCENT_WARNING : isMe ? c * 0.8f : BORDER_SUBTLE, turn ? 2 : 1, c);

                int ip = Px(10, uiScale, 6);
                int sq = Px(14, uiScale, 10);
                DrawPlayerSquare(sb, pixel, new Rectangle(r.X + ip + 2, r.Y + ip + 2, sq, sq), m.ColorHue, m.Username);
                int nx = r.X + ip + sq + Px(10, uiScale);

                // Right side of line 1: status pill.
                string status = StatusLabel(m.Status);
                int statusW = (int)MathF.Ceiling(MeasureText(font, status, smallF).X) + Px(12, uiScale);
                DrawPillBadge(sb, font, pixel, r.Right - ip - statusW, r.Y + ip, pillH, status, StatusColor(m.Status), smallF, Px(6, uiScale), filledStrong: m.Status == Protocol.StatusReady);
                int nameRight = r.Right - ip - statusW - Px(6, uiScale);

                string name = m.Username + (isMe ? " (you)" : "");
                name = TruncateToWidth(font, name, Math.Max(30, nameRight - nx), nameF);
                DrawText(sb, font, name, new Vector2(nx, r.Y + ip - 2), Color.Lerp(c, Color.White, 0.45f) * _fadeAlpha, nameF, shadow: true);

                // Line 2: HOST / PICKING / vote / level state, and KICK for the host.
                int lx = nx;
                int ly = r.Bottom - ip - pillH;
                if (m.IsHost) lx += DrawPillBadge(sb, font, pixel, lx, ly, pillH, "HOST", ACCENT_WARNING, smallF, Px(6, uiScale), filledStrong: true) + Px(5, uiScale);
                if (turn) lx += DrawPillBadge(sb, font, pixel, lx, ly, pillH, "PICKING", ACCENT_PRIMARY, smallF, Px(6, uiScale)) + Px(5, uiScale);
                if (st.Settings.Mode == Protocol.ModeMirror && m.Vote >= 0 && st.Song != null && st.Song.Kind != Protocol.KindOsu)
                    lx += DrawPillBadge(sb, font, pixel, lx, ly, pillH, $"VOTE: {RoomClient.SideName(m.Vote).ToUpperInvariant()}", ACCENT_SECONDARY, smallF, Px(6, uiScale)) + Px(5, uiScale);
                string? levelText = null;
                if (st.Song != null || inMatch)
                {
                    // Your own row uses what this game knows right now (the server's copy lags a
                    // little); everybody else's comes from the server, the same for every viewer.
                    string levelStatus = isMe ? Room.LevelState : m.LevelStatus;
                    float levelProgress = isMe ? Room.LevelProgress : m.LevelProgress;
                    float upload = isMe && Room.UploadProgress >= 0f ? Room.UploadProgress : m.UploadProgress;
                    levelText = upload >= 0f
                        ? $"Sending level {(int)(Math.Clamp(upload, 0f, 1f) * 100)}%"
                        : levelStatus switch
                        {
                            Protocol.LevelDownloading => $"Getting level {(int)(Math.Clamp(levelProgress, 0f, 1f) * 100)}%",
                            Protocol.LevelFailed => "Level failed",
                            Protocol.LevelReady => null,
                            _ => "No level yet",
                        };
                }
                int kickW = 0;
                if (Room.IsHost && !isMe && hovered)
                {
                    kickW = Px(46, uiScale, 36);
                    DrawRoomButton(sb, font, pixel, uiScale, $"kick:{m.Id}", new Rectangle(r.Right - ip - kickW, ly - 1, kickW, pillH + 2), "KICK", ACCENT_ERROR, fontSize: smallF);
                }
                if (levelText != null)
                {
                    string t = TruncateToWidth(font, levelText, Math.Max(20, r.Right - ip - kickW - Px(6, uiScale) - lx), smallF);
                    Vector2 tsz = MeasureText(font, t, smallF);
                    DrawText(sb, font, t, new Vector2(lx, ly + (pillH - tsz.Y) / 2f),
                        (m.LevelStatus == Protocol.LevelFailed ? ACCENT_ERROR : TEXT_DISABLED) * _fadeAlpha, smallF);
                }
                rowY += rowH + rowGap;
            }
        }

        private void DrawRoundsPanel(SpriteBatch sb, SpriteFont font, Texture2D pixel, float uiScale, RoomStatePacket st, Rectangle p)
        {
            DrawPanel(sb, pixel, p.X, p.Y, p.Width, p.Height, BG_MEDIUM, ModeColor(st.Settings.Mode) * 0.5f, 2);
            int pad = Px(14, uiScale, 8);
            int headF = FontPx(13, uiScale, 10, 18);
            DrawText(sb, font, "CURRENT SONG", new Vector2(p.X + pad, p.Y + pad), TEXT_DISABLED * _fadeAlpha, headF);

            int slotY = p.Y + pad + Px(22, uiScale);
            bool songShown = st.Song != null || (st.Phase is Protocol.PhaseLoading or Protocol.PhasePlaying && st.Match != null);
            int slotH = songShown ? Px(196, uiScale, 150) : Px(150, uiScale, 112);
            var slot = new Rectangle(p.X + pad, slotY, p.Width - pad * 2, slotH);
            DrawCurrentSongSlot(sb, font, pixel, uiScale, st, slot);

            int listTop = slot.Bottom + Px(16, uiScale);
            var rounds = RoundsNewestFirst();
            DrawText(sb, font, $"ROUNDS PLAYED  ({rounds.Count})", new Vector2(p.X + pad, listTop), TEXT_DISABLED * _fadeAlpha, headF);
            listTop += Px(22, uiScale);
            int cardH = Px(56, uiScale, 42);
            int gap = Px(6, uiScale, 4);
            int avail = p.Bottom - pad - listTop;
            _roundsVisible = Math.Max(1, (avail + gap) / (cardH + gap));
            _roundScroll = Math.Clamp(_roundScroll, 0, Math.Max(0, rounds.Count - _roundsVisible));
            if (rounds.Count == 0)
            {
                DrawCenteredTextInPanel(sb, font, "No rounds yet. Results appear here after each song.", p.X, listTop + Px(18, uiScale), p.Width, TEXT_DISABLED, FontPx(13, uiScale, 10, 18));
                return;
            }
            bool scroll = rounds.Count > _roundsVisible;
            int cardW = p.Width - pad * 2 - (scroll ? Px(14, uiScale, 10) : 0);
            int end = Math.Min(rounds.Count, _roundScroll + _roundsVisible);
            for (int i = _roundScroll; i < end; i++)
            {
                var r = new Rectangle(p.X + pad, listTop + (i - _roundScroll) * (cardH + gap), cardW, cardH);
                DrawRoundCard(sb, font, pixel, uiScale, rounds[i], rounds.Count - i, i, r, i == _roundSelection);
            }
            if (scroll)
                DrawScrollBar(sb, pixel, p.X + pad + cardW + Px(5, uiScale), listTop, _roundsVisible * (cardH + gap) - gap, rounds.Count, _roundsVisible, _roundScroll, ACCENT_PRIMARY);
        }

        private void DrawCurrentSongSlot(SpriteBatch sb, SpriteFont font, Texture2D pixel, float uiScale, RoomStatePacket st, Rectangle r)
        {
            int pad = Px(14, uiScale, 8);
            int titleF = FontPx(20, uiScale, 14, 28);
            int textF = FontPx(14, uiScale, 11, 20);
            int smallF = FontPx(12, uiScale, 10, 16);
            bool inMatch = st.Phase is Protocol.PhaseLoading or Protocol.PhasePlaying;

            if (inMatch && st.Match != null)
            {
                DrawPanel(sb, pixel, r.X, r.Y, r.Width, r.Height, BG_DARK, ACCENT_PRIMARY, 2, ACCENT_PRIMARY);
                int tx = DrawSongArt(sb, pixel, uiScale, st.Match.Song, r, pad, Px(34, uiScale, 26));
                int tw = r.Right - pad - tx;
                string state = st.Phase == Protocol.PhaseLoading ? "LOADING" : "NOW PLAYING";
                DrawPillBadge(sb, font, pixel, tx, r.Y + pad, Px(22, uiScale, 18), state, ACCENT_PRIMARY, smallF, Px(8, uiScale), true);
                var (mTitle, mSub) = SongLines(st.Match.Song);
                DrawText(sb, font, TruncateToWidth(font, mTitle, tw, titleF), new Vector2(tx, r.Y + pad + Px(28, uiScale)), TEXT_PRIMARY * _fadeAlpha, titleF, shadow: true);
                if (mSub.Length > 0)
                    DrawText(sb, font, TruncateToWidth(font, mSub, tw, smallF), new Vector2(tx, r.Y + pad + Px(58, uiScale)), TEXT_SECONDARY * _fadeAlpha, smallF);
                string who = string.Join("  vs  ", st.Match.Players.GroupBy(a => a.Side).Select(g =>
                    $"{RoomClient.SideName(g.Key)}: {string.Join(", ", g.Select(a => Room.NameOf(a.PlayerId)))}"));
                if (st.Match.Mode == Protocol.ModeMirror)
                    who = st.Match.Song.Kind == Protocol.KindOsu ? "Everybody plays the same chart" : $"Everybody plays {RoomClient.SideName(st.Match.MirrorSide)}";
                DrawText(sb, font, TruncateToWidth(font, $"{RoomClient.ModeName(st.Match.Mode)}  |  {who}", tw, textF),
                    new Vector2(tx, r.Y + pad + Px(82, uiScale)), TEXT_SECONDARY * _fadeAlpha, textF);
                if (!Room.IsParticipant && Room.ActiveMatchId.Length == 0)
                {
                    int bw = Px(190, uiScale, 140), bh = Px(36, uiScale, 28);
                    DrawRoomButton(sb, font, pixel, uiScale, "watch", new Rectangle(r.Right - pad - bw, r.Bottom - pad - bh, bw, bh), "W  WATCH MATCH", ACCENT_PRIMARY, strong: true);
                }
                else if (Room.ActiveMatchId.Length > 0)
                {
                    DrawText(sb, font, "Loading the match...", new Vector2(r.X + pad, r.Bottom - pad - Px(18, uiScale)), ACCENT_WARNING * _fadeAlpha, smallF);
                }
                return;
            }

            if (st.Song != null)
            {
                var song = st.Song;
                bool osu = song.Kind == Protocol.KindOsu;
                Color accent = song.Source == Protocol.SourceOfficial ? ACCENT_PRIMARY : osu ? OSU_PINK : ACCENT_SECONDARY;
                DrawPanel(sb, pixel, r.X, r.Y, r.Width, r.Height, BG_DARK, accent, 2, accent);
                int bh = Px(34, uiScale, 26);
                int tx = DrawSongArt(sb, pixel, uiScale, song, r, pad, bh);
                int tw = r.Right - pad - tx;
                int bx = tx;
                string kindTag = song.Source == Protocol.SourceOfficial ? "OFFICIAL" : osu ? "OSU!MANIA" : "LOCAL";
                bx += DrawPillBadge(sb, font, pixel, bx, r.Y + pad, Px(22, uiScale, 18), kindTag, accent, smallF, Px(8, uiScale), true) + Px(8, uiScale);
                DrawText(sb, font, TruncateToWidth(font, $"picked by {Room.NameOf(song.PickedBy)}", Math.Max(20, r.Right - pad - bx), smallF),
                    new Vector2(bx, r.Y + pad + Px(2, uiScale)), TEXT_DISABLED * _fadeAlpha, smallF);
                var (title, sub) = SongLines(song);
                DrawText(sb, font, TruncateToWidth(font, title, tw, titleF), new Vector2(tx, r.Y + pad + Px(28, uiScale)), TEXT_PRIMARY * _fadeAlpha, titleF, shadow: true);

                // Second line: difficulty / stars / size, and who has it (squares, hover = names).
                int subY = r.Y + pad + Px(58, uiScale);
                var level = song.Source == Protocol.SourceLocal ? st.Levels?.FirstOrDefault(l => l.Hash == song.Hash) : null;
                var holders = level != null ? HoldersFor(level) : new List<(Color Color, string Name)>();
                int hw = HolderSquaresWidth(holders.Count, uiScale);
                if (hw > 0) DrawHolderSquares(sb, pixel, uiScale, r.Right - pad, subY + Px(8, uiScale), holders);
                if (sub.Length > 0)
                    DrawText(sb, font, TruncateToWidth(font, sub, Math.Max(20, tw - hw - Px(8, uiScale)), smallF), new Vector2(tx, subY), TEXT_SECONDARY * _fadeAlpha, smallF);

                // My level / blocker line.
                string line;
                Color lineColor = ACCENT_WARNING;
                float barValue = -1f;
                if (Room.UploadProgress >= 0f)
                {
                    // You're the holder sending it to the room (it didn't show before).
                    line = Room.UploadProgress <= 0f && osu
                        ? "Packing the set to send it to the room..."
                        : $"Sending the level to the room... {(int)(Room.UploadProgress * 100)}%";
                    barValue = Room.UploadProgress;
                }
                else if (Room.LevelState == Protocol.LevelDownloading)
                {
                    line = $"Getting the level... {(int)(Room.LevelProgress * 100)}%";
                    barValue = Room.LevelProgress;
                }
                else if (Room.LevelState == Protocol.LevelFailed)
                {
                    line = $"Couldn't get the level: {Room.LevelError}";
                    lineColor = ACCENT_ERROR;
                }
                else if (!string.IsNullOrEmpty(st.StartBlocker)) line = st.StartBlocker;
                else line = "Starting...";
                int lineY = r.Y + pad + Px(82, uiScale);
                DrawText(sb, font, TruncateToWidth(font, line, tw - Px(90, uiScale), textF), new Vector2(tx, lineY), lineColor * _fadeAlpha, textF);
                if (barValue >= 0f)
                {
                    int barY = lineY + Px(22, uiScale);
                    int barW = tw;
                    Color barColor = Room.UploadProgress >= 0f ? ACCENT_PRIMARY : ACCENT_SUCCESS;
                    sb.Draw(pixel, new Rectangle(tx, barY, barW, Px(6, uiScale, 4)), Color.Black * 0.5f);
                    sb.Draw(pixel, new Rectangle(tx, barY, (int)(barW * Math.Clamp(barValue, 0f, 1f)), Px(6, uiScale, 4)), barColor * _fadeAlpha);
                }
                if (Room.LevelState == Protocol.LevelFailed)
                    DrawRoomButton(sb, font, pixel, uiScale, "retry", new Rectangle(r.Right - pad - Px(80, uiScale), lineY - 4, Px(80, uiScale), Px(26, uiScale, 20)), "RETRY", ACCENT_WARNING, fontSize: smallF);

                // Bottom row: mirror vote (not for osu! songs: one chart) + quick Chart Only toggle for FNF.
                int byy = r.Bottom - pad - bh;
                int bxx = r.X + pad;
                if (st.Settings.Mode == Protocol.ModeMirror && Room.Me?.Spectate == false && !osu)
                {
                    int votesP = st.Members.Count(m => m.Vote == Protocol.SidePlayer);
                    int votesE = st.Members.Count(m => m.Vote == Protocol.SideEnemy);
                    int vw = Px(136, uiScale, 96);
                    int myVote = Room.Me?.Vote ?? -1;
                    DrawRoomButton(sb, font, pixel, uiScale, "vote0", new Rectangle(bxx, byy, vw, bh), $"1  PLAYER ({votesP})", ACCENT_PRIMARY, strong: myVote == 0, fontSize: smallF);
                    bxx += vw + Px(6, uiScale);
                    DrawRoomButton(sb, font, pixel, uiScale, "vote1", new Rectangle(bxx, byy, vw, bh), $"2  ENEMY ({votesE})", ACCENT_ERROR, strong: myVote == 1, fontSize: smallF);
                    bxx += vw + Px(6, uiScale);
                    bool co = MultiplayerSettings.Instance.ChartOnlyMode;
                    int cw = Math.Min(Px(160, uiScale, 110), Math.Max(Px(90, uiScale, 76), r.Right - pad - bxx));
                    if (bxx + cw <= r.Right - pad + 2)
                        DrawRoomButton(sb, font, pixel, uiScale, "chartonly", new Rectangle(bxx, byy, cw, bh), co ? "CHART ONLY: ON" : "CHART ONLY: OFF", co ? ACCENT_SUCCESS : TEXT_SECONDARY, strong: co, fontSize: smallF);
                }
                // No CHANGE SONG: a pick is final for the round.
                return;
            }

            if (Room.IsMyTurn)
            {
                float pulse = (float)Math.Sin(_pulseTimer * 1.6f) * 0.5f + 0.5f;
                sb.Draw(pixel, new Rectangle(r.X - 4, r.Y - 4, r.Width + 8, r.Height + 8), ACCENT_SUCCESS * (0.10f + 0.10f * pulse) * _fadeAlpha);
                DrawRoomButton(sb, font, pixel, uiScale, "pick", r, "P  SELECT SONG", ACCENT_SUCCESS, strong: true, fontSize: FontPx(24, uiScale, 16, 34));
                DrawCenteredTextInPanel(sb, font, "It's your turn to pick!", r.X, r.Bottom - Px(26, uiScale), r.Width, ACCENT_SUCCESS, smallF);
                return;
            }

            DrawPanel(sb, pixel, r.X, r.Y, r.Width, r.Height, BG_DARK, BORDER_SUBTLE, 1);
            string waiting = string.IsNullOrEmpty(st.TurnPlayerId)
                ? (st.Members.Count(m => !m.Spectate) == 0 ? "Everybody is spectating" : "Waiting for players...")
                : $"Waiting for {Room.NameOf(st.TurnPlayerId)} to pick a song...";
            int dots = (int)(_pulseTimer * 1.2f) % 4;
            DrawCenteredTextInPanel(sb, font, waiting + new string('.', dots), r.X, r.Y + r.Height / 2 - Px(12, uiScale), r.Width, TEXT_SECONDARY, textF);
            if (st.Settings.SongSelection == Protocol.SelectHost)
                DrawCenteredTextInPanel(sb, font, "Only the host picks songs in this room", r.X, r.Y + r.Height / 2 + Px(14, uiScale), r.Width, TEXT_DISABLED, smallF);
        }

        private static readonly Color OSU_PINK = new(255, 102, 170);

        /// <summary>Title and detail line of a song for the slot (osu!: the set, then "[difficulty]").</summary>
        private static (string Title, string Sub) SongLines(SongInfo song)
        {
            string stars = StarsText(song.Stars);
            string size = song.Size > 0 ? $"{song.Size / (1024.0 * 1024.0):0.0} MB" : "";
            if (song.Kind == Protocol.KindOsu)
            {
                string title = song.SetName.Length > 0 ? song.SetName : song.Name;
                var parts = new List<string>();
                if (song.Difficulty.Length > 0) parts.Add($"[{song.Difficulty}]");
                if (stars.Length > 0) parts.Add(stars);
                if (size.Length > 0)
                    parts.Add(song.SizeNoVideo > 0 && song.SizeNoVideo < song.Size ? $"{size} ({song.SizeNoVideo / (1024.0 * 1024.0):0.0} MB without video)" : size);
                return (title, string.Join("   ", parts));
            }
            return ($"{song.Name}  {stars}".TrimEnd(), size);
        }

        /// <summary>
        /// The song's art in the slot, from the Freeplay browser's icon cache: osu! songs their background
        /// behind the whole slot (darkened), FNF levels their icon in a square on the left; "?" while
        /// this device doesn't have the level yet. Returns where the text starts.
        /// </summary>
        private int DrawSongArt(SpriteBatch sb, Texture2D pixel, float uiScale, SongInfo song, Rectangle r, int pad, int bottomRowH)
        {
            var picker = SongPicker;
            if (picker == null) return r.X + pad;
            string? path = Room.CurrentSong?.Hash == song.Hash && Room.LevelState == Protocol.LevelReady
                && Room.LevelPath != null && System.IO.File.Exists(Room.LevelPath) ? Room.LevelPath : null;
            var inner = new Rectangle(r.X + 2, r.Y + 2, r.Width - 4, r.Height - 4);
            if (song.Kind == Protocol.KindOsu && picker.SongBanner(sb, path) is { } banner)
            {
                FreeplayMenu.DrawCoverPublic(sb, banner, inner, Color.White * (0.85f * _fadeAlpha));
                const int steps = 24;
                for (int i = 0; i < steps; i++)
                {
                    int x0 = inner.X + inner.Width * i / steps, x1 = inner.X + inner.Width * (i + 1) / steps;
                    float t = (i + 0.5f) / steps;
                    sb.Draw(pixel, new Rectangle(x0, inner.Y, x1 - x0, inner.Height), Color.Black * (MathHelper.Lerp(0.82f, 0.35f, t) * _fadeAlpha));
                }
                sb.Draw(pixel, new Rectangle(inner.X, inner.Bottom - bottomRowH - pad * 2, inner.Width, bottomRowH + pad * 2), Color.Black * (0.35f * _fadeAlpha));
                return r.X + pad;
            }
            int size = r.Height - pad * 3 - bottomRowH;
            if (size < Px(48, uiScale, 36) || r.Width < size * 3) return r.X + pad; // too narrow: text only
            var box = new Rectangle(r.X + pad, r.Y + pad, size, size);
            sb.Draw(pixel, new Rectangle(box.X - 2, box.Y - 2, box.Width + 4, box.Height + 4), Color.Black * (0.45f * _fadeAlpha));
            picker.DrawSongArt(sb, path, song.Name, box, _fadeAlpha);
            return box.Right + pad;
        }

        private void DrawRoundCard(SpriteBatch sb, SpriteFont font, Texture2D pixel, float uiScale, RoundResult round, int number, int index, Rectangle r, bool selected)
        {
            bool hovered = r.Contains(_lastMousePos);
            Color accent = ModeColor(round.Mode);
            DrawPanel(sb, pixel, r.X, r.Y, r.Width, r.Height, selected || hovered ? BG_LIGHT : BG_DARK, selected ? accent : hovered ? ACCENT_WARNING : BORDER_SUBTLE, selected ? 2 : 1, accent);
            Hit($"round:{index}", r);
            int pad = Px(12, uiScale, 8);
            int nameF = FontPx(15, uiScale, 11, 21);
            int smallF = FontPx(12, uiScale, 10, 16);

            string winner = round.Mode == Protocol.ModeTeams
                ? round.WinningTeam switch { 0 => "Team Player won", 1 => "Team Enemy won", _ => "Draw" }
                : round.Players.Count(p => p.Place == 1 && !p.Disconnected) == 1
                    ? $"{round.Players.First(p => p.Place == 1).Username} won"
                    : "Draw";
            var me = round.Players.FirstOrDefault(p => p.PlayerId == Room.YouId);
            string mine = me != null ? $"You: #{me.Place}" : "You spectated";

            string right = $"{RoomClient.ModeName(round.Mode)}  |  {mine}";
            Vector2 rsz = MeasureText(font, right, smallF);
            DrawText(sb, font, right, new Vector2(r.Right - pad - rsz.X, r.Y + pad - 2), TEXT_DISABLED * _fadeAlpha, smallF);
            string title = TruncateToWidth(font, $"#{number}  {round.Song.Name}  {StarsText(round.Song.Stars)}", r.Width - pad * 3 - rsz.X, nameF);
            DrawText(sb, font, title, new Vector2(r.X + pad, r.Y + pad - 3), TEXT_PRIMARY * _fadeAlpha, nameF);

            string top3 = string.Join("   ", round.Players.Take(3).Select(p => $"{p.Place}. {p.Username} {p.Score:N0}"));
            DrawText(sb, font, TruncateToWidth(font, $"{winner}   —   {top3}", r.Width - pad * 2, smallF),
                new Vector2(r.X + pad, r.Bottom - pad - MeasureText(font, "A", smallF).Y + 2), accent * _fadeAlpha, smallF);
        }

        private void DrawChatPanel(SpriteBatch sb, SpriteFont font, Texture2D pixel, float uiScale, Rectangle p)
        {
            _chatPanelRect = p;
            DrawPanel(sb, pixel, p.X, p.Y, p.Width, p.Height, BG_MEDIUM, BORDER_SUBTLE, 1);
            int pad = Px(12, uiScale, 8);
            DrawText(sb, font, "CHAT", new Vector2(p.X + pad, p.Y + pad), TEXT_DISABLED * _fadeAlpha, FontPx(13, uiScale, 10, 18));

            int boxH = Px(38, uiScale, 28);
            var box = new Rectangle(p.X + pad, p.Bottom - pad - boxH, p.Width - pad * 2, boxH);
#if ANDROID
            DrawTextBox(sb, font, pixel, uiScale, "chat", box, _chatDraft, "Tap to chat...");
#else
            DrawTextBox(sb, font, pixel, uiScale, "chat", box, _chatDraft, "Press T or click to chat...");
#endif

            int f = FontPx(13, uiScale, 10, 18);
            float lineH = MeasureText(font, "Ag", f).Y + Px(2, uiScale);
            int areaTop = p.Y + pad + Px(24, uiScale);
            int areaBottom = box.Y - Px(8, uiScale);
            float maxW = p.Width - pad * 2;

            // Build wrapped lines (oldest → newest), then draw the last ones that fit.
            var lines = new List<(string Text, Color Color, string? Name, Color NameColor)>();
            foreach (var m in Room.Chat)
            {
                if (m.System)
                {
                    foreach (var l in WrapTextToWidth(font, m.Text, maxW, f).Split('\n'))
                        lines.Add((l, TEXT_DISABLED, null, Color.White));
                }
                else
                {
                    string name = m.Username + ": ";
                    float nameW = MeasureText(font, name, f).X;
                    var wrapped = WrapTextToWidth(font, m.Text, Math.Max(40, maxW - nameW), f).Split('\n');
                    Color nc = Color.Lerp(GetColorFromHue(m.ColorHue), Color.White, 0.3f);
                    for (int i = 0; i < wrapped.Length; i++)
                        lines.Add((wrapped[i], TEXT_PRIMARY, i == 0 ? name : "", nc));
                }
            }
            int fit = Math.Max(1, (int)((areaBottom - areaTop) / lineH));
            _chatScroll = Math.Clamp(_chatScroll, 0, Math.Max(0, lines.Count - fit));
            int start = Math.Max(0, lines.Count - fit - _chatScroll);
            int endIdx = Math.Min(lines.Count, start + fit);
            float y = areaBottom - (endIdx - start) * lineH;
            for (int i = start; i < endIdx; i++)
            {
                var (text, color, name, nameColor) = lines[i];
                float x = p.X + pad;
                if (name != null)
                {
                    if (name.Length > 0)
                    {
                        DrawText(sb, font, name, new Vector2(x, y), nameColor * _fadeAlpha, f);
                        x += MeasureText(font, name, f).X;
                    }
                    else
                    {
                        x += MeasureText(font, "    ", f).X;
                    }
                }
                DrawText(sb, font, text, new Vector2(x, y), color * _fadeAlpha, f);
                y += lineH;
            }
            if (_chatScroll > 0)
                DrawText(sb, font, "▼ newer messages", new Vector2(p.X + pad, areaBottom - lineH + 2), ACCENT_WARNING * _fadeAlpha, FontPx(11, uiScale, 9, 15));
        }

        private void DrawRoundDetail(SpriteBatch sb, SpriteFont font, Texture2D pixel, float uiScale, int sw, int sh, RoundResult round)
        {
            sb.Draw(pixel, new Rectangle(0, 0, sw, sh), Color.Black * 0.65f);
            int w = Math.Min(Px(820, uiScale), sw - 32);
            int rowH = Px(36, uiScale, 26);
            // Title + subtitle (70) + table header (20) + one row per player + footer hint (30) + padding.
            int h = Math.Min(sh - 40, Px(36, uiScale) + Px(70, uiScale) + Px(24, uiScale) + round.Players.Count * (rowH + 4) + Px(44, uiScale));
            int x = (sw - w) / 2, y = (sh - h) / 2;
            Color accent = ModeColor(round.Mode);
            DrawPanel(sb, pixel, x, y, w, h, BG_MEDIUM, accent, 2, accent);
            int pad = Px(18, uiScale, 10);
            DrawCenteredTextInPanel(sb, font, TruncateToWidth(font, $"{round.Song.Name}  {StarsText(round.Song.Stars)}", w - pad * 2, FontPx(22, uiScale, 15, 30)),
                x, y + pad, w, TEXT_PRIMARY, FontPx(22, uiScale, 15, 30), shadow: true);
            string sub = RoomClient.ModeName(round.Mode);
            if (round.Mode == Protocol.ModeTeams && round.TeamScores.Count == 2)
                sub += $"   |   Team Player {round.TeamScores[0]:N0} avg   vs   Team Enemy {round.TeamScores[1]:N0} avg   |   " +
                       (round.WinningTeam switch { 0 => "TEAM PLAYER WINS", 1 => "TEAM ENEMY WINS", _ => "DRAW" });
            DrawCenteredTextInPanel(sb, font, TruncateToWidth(font, sub, w - pad * 2, FontPx(14, uiScale, 11, 20)), x, y + pad + Px(34, uiScale), w, accent, FontPx(14, uiScale, 11, 20));

            int ty = y + pad + Px(70, uiScale);
            DrawStandingsTable(sb, font, pixel, uiScale, round, new Rectangle(x + pad, ty, w - pad * 2, y + h - pad - ty - Px(30, uiScale)), rowH);
            DrawCenteredTextInPanel(sb, font, "ENTER / ESC / click: close", x, y + h - pad - Px(16, uiScale), w, TEXT_DISABLED, FontPx(12, uiScale, 10, 16));
            Hit("detail-close", new Rectangle(x, y, w, h));
        }

        /// <summary>Leaderboard table of a round: place, player, (team / side), score, accuracy, misses.</summary>
        private void DrawStandingsTable(SpriteBatch sb, SpriteFont font, Texture2D pixel, float uiScale, RoundResult round, Rectangle r, int rowH)
        {
            int f = FontPx(14, uiScale, 11, 20);
            int hf = FontPx(11, uiScale, 9, 15);
            float[] cols = { 0f, 0.08f, 0.44f, 0.60f, 0.76f, 0.89f };
            string[] heads = { "#", "PLAYER", round.Mode == Protocol.ModeTeams ? "TEAM" : "SIDE", "SCORE", "ACCURACY", "MISSES" };
            for (int c = 0; c < heads.Length; c++)
                DrawText(sb, font, heads[c], new Vector2(r.X + r.Width * cols[c], r.Y), TEXT_DISABLED * _fadeAlpha, hf);
            int y = r.Y + Px(20, uiScale);
            foreach (var p in round.Players)
            {
                if (y + rowH > r.Bottom) break;
                bool me = p.PlayerId == Room.YouId;
                Color c = GetColorFromHue(p.ColorHue);
                DrawPanel(sb, pixel, r.X - 4, y, r.Width + 8, rowH, me ? BG_LIGHT : BG_DARK, p.Place == 1 ? ACCENT_WARNING : BORDER_SUBTLE, 1, c);
                float ty = y + (rowH - MeasureText(font, "A", f).Y) / 2f;
                Color placeCol = p.Place switch { 1 => ACCENT_WARNING, 2 => new Color(200, 210, 225), 3 => new Color(215, 150, 90), _ => TEXT_SECONDARY };
                DrawText(sb, font, p.Disconnected ? "-" : p.Place.ToString(), new Vector2(r.X + r.Width * cols[0] + 6, ty), placeCol * _fadeAlpha, f, shadow: true);
                string name = TruncateToWidth(font, p.Username + (me ? " (you)" : "") + (p.Disconnected ? "  [left]" : ""), r.Width * (cols[2] - cols[1]) - 8, f);
                DrawText(sb, font, name, new Vector2(r.X + r.Width * cols[1], ty), Color.Lerp(c, Color.White, 0.4f) * _fadeAlpha, f);
                string side = round.Mode == Protocol.ModeTeams ? (p.Team == 0 ? "Player" : "Enemy") : RoomClient.SideName(p.Side);
                DrawText(sb, font, side, new Vector2(r.X + r.Width * cols[2], ty), TEXT_SECONDARY * _fadeAlpha, f);
                DrawText(sb, font, p.Score.ToString("N0"), new Vector2(r.X + r.Width * cols[3], ty), TEXT_PRIMARY * _fadeAlpha, f);
                DrawText(sb, font, $"{p.Accuracy:F2}%", new Vector2(r.X + r.Width * cols[4], ty),
                    (MultiplayerResultsScreen.PerfectAccuracy(p) ? ACCENT_SUCCESS : TEXT_PRIMARY) * _fadeAlpha, f);
                DrawText(sb, font, p.Misses.ToString(), new Vector2(r.X + r.Width * cols[5], ty),
                    (MultiplayerResultsScreen.NoMisses(p) ? ACCENT_SUCCESS : TEXT_PRIMARY) * _fadeAlpha, f);
                y += rowH + 4;
            }
        }

        private void DrawKickConfirm(SpriteBatch sb, SpriteFont font, Texture2D pixel, float uiScale, int sw, int sh)
        {
            sb.Draw(pixel, new Rectangle(0, 0, sw, sh), Color.Black * 0.6f);
            int w = Math.Min(Px(460, uiScale), sw - 32), h = Px(160, uiScale, 120);
            int x = (sw - w) / 2, y = (sh - h) / 2;
            DrawPanel(sb, pixel, x, y, w, h, BG_MEDIUM, ACCENT_ERROR, 2, ACCENT_ERROR);
            string name = Room.NameOf(_kickConfirmId ?? "");
            DrawCenteredTextInPanel(sb, font, $"Kick {name}?", x, y + Px(22, uiScale), w, TEXT_PRIMARY, FontPx(20, uiScale, 14, 28), shadow: true);
            DrawCenteredTextInPanel(sb, font, "They can't rejoin for 5 minutes.", x, y + Px(56, uiScale), w, TEXT_SECONDARY, FontPx(13, uiScale, 10, 18));
            int pad = Px(20, uiScale, 12), bh = Px(36, uiScale, 28), bw = (w - pad * 3) / 2, by = y + h - pad - bh;
            DrawRoomButton(sb, font, pixel, uiScale, "kick-no", new Rectangle(x + pad, by, bw, bh), "N  Cancel", TEXT_SECONDARY);
            DrawRoomButton(sb, font, pixel, uiScale, "kick-yes", new Rectangle(x + pad * 2 + bw, by, bw, bh), "Y  KICK", ACCENT_ERROR, strong: true);
        }
    }
}
