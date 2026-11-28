using System;
using System.Collections.Generic;
using FunkyMakerPlayer.Multiplayer.Rooms;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using Microsoft.Xna.Framework.Input;

namespace FunkyMakerPlayer.Multiplayer
{
    /// <summary>
    /// Room settings screen (fork): create a room, edit it (host, between rounds; the name is fixed),
    /// or just look at it (everybody else).
    /// Rows: name, game mode, song selection, privacy, password, player limit, guests' local levels,
        /// song types (FNF / osu!mania / both).
    /// </summary>
    public partial class MultiplayerMenu
    {
        private enum RoomSettingsMode { Create, Edit, View }

        private RoomSettingsMode _roomSettingsMode;
        private int _roomSettingsRow;
        private string _rsName = "";
        private RoomSettings _rs = new();
        private bool _rsPrivate;
        private bool _rsBusy;

        private const int RS_NAME = 0, RS_MODE = 1, RS_SELECT = 2, RS_PRIVACY = 3, RS_PASSWORD = 4, RS_LIMIT = 5, RS_GUESTS = 6, RS_TYPES = 7, RS_ROWS = 8;

        private static readonly string[] SongTypesOrder = { Protocol.SongTypesBoth, Protocol.SongTypesFnf, Protocol.SongTypesOsu };

        private static readonly string[] ModeOrder = { Protocol.ModeVersus, Protocol.ModeTeams, Protocol.ModeMirror };

        private void OpenRoomSettings(RoomSettingsMode mode)
        {
            _roomSettingsMode = mode;
            _roomSettingsRow = mode == RoomSettingsMode.Create ? RS_NAME : RS_MODE;
            _rsBusy = false;
            if (mode == RoomSettingsMode.Create)
            {
                var me = MultiplayerSettings.Instance;
                _rsName = $"{(string.IsNullOrWhiteSpace(me.Username) ? "Player" : me.Username.Trim())}'s room";
                if (_rsName.Length > Protocol.MaxRoomNameLength) _rsName = _rsName[..Protocol.MaxRoomNameLength];
                _rs = new RoomSettings { Mode = Protocol.ModeVersus, SongSelection = Protocol.SelectTurns, MaxPlayers = 2, AllowGuestLocalLevels = true, SongTypes = Protocol.SongTypesBoth };
                _rsPrivate = false;
            }
            else
            {
                var st = Room.State;
                _rsName = st?.Name ?? "";
                var cur = st?.Settings ?? new RoomSettings();
                _rs = new RoomSettings
                {
                    Mode = cur.Mode, SongSelection = cur.SongSelection, MaxPlayers = cur.MaxPlayers,
                    AllowGuestLocalLevels = cur.AllowGuestLocalLevels, Password = cur.Password, SongTypes = cur.SongTypes,
                };
                _rsPrivate = st?.HasPassword == true;
            }
            TransitionToState(MenuState.RoomSettings);
        }

        private bool RowEditable(int row) => _roomSettingsMode switch
        {
            RoomSettingsMode.View => false,
            RoomSettingsMode.Edit => row != RS_NAME && (row != RS_PASSWORD || _rsPrivate),
            _ => row != RS_PASSWORD || _rsPrivate,
        };

        private void UpdateRoomSettings(KeyboardState kb, MouseState mouse, float dt)
        {
            if (UpdateTextField(kb, dt)) return;
            if (_inputCooldown > 0f || _rsBusy) return;

            if (_roomSettingsMode != RoomSettingsMode.Create && Room.State != null && _roomSettingsMode == RoomSettingsMode.Edit
                && Room.State.Phase is Protocol.PhaseLoading or Protocol.PhasePlaying)
            {
                ShowError("A round started; settings can be changed after it.");
                TransitionToState(MenuState.WaitingRoom);
                return;
            }

            string? hover = HoverHit();
            if (_mouseMovedThisFrame && hover != null && hover.StartsWith("rs:") && int.TryParse(hover[3..], out int hr))
                _roomSettingsRow = hr;

            if (IsKeyPressed(kb, Keys.Down)) _roomSettingsRow = NextRow(_roomSettingsRow, 1);
            if (IsKeyPressed(kb, Keys.Up)) _roomSettingsRow = NextRow(_roomSettingsRow, -1);

            string? clicked = ClickedHit(mouse);
            if (clicked == "rs-back" || IsKeyPressed(kb, Keys.Escape) || IsRectClicked(mouse, _backButtonRect))
            {
                if (_roomSettingsMode == RoomSettingsMode.Create) OpenRoomBrowser();
                else TransitionToState(MenuState.WaitingRoom);
                return;
            }
            if (_roomSettingsMode == RoomSettingsMode.View) return;

            if (clicked == "rs-save" || (IsKeyPressed(kb, Keys.Enter) && kb.IsKeyDown(Keys.LeftControl)))
            {
                SubmitRoomSettings();
                return;
            }

            int clickedRow = -1, clickedDir = 0;
            if (clicked != null && clicked.StartsWith("rs:") && int.TryParse(clicked[3..], out int cr)) { clickedRow = cr; clickedDir = 1; }
            if (clicked != null && clicked.StartsWith("rs<") && int.TryParse(clicked[3..], out int cl)) { clickedRow = cl; clickedDir = -1; }
            if (clicked != null && clicked.StartsWith("rs>") && int.TryParse(clicked[3..], out int cg)) { clickedRow = cg; clickedDir = 1; }
            if (clickedRow >= 0)
            {
                _roomSettingsRow = clickedRow;
                ActivateRoomSettingsRow(clickedRow, clickedDir);
                return;
            }

            if (IsKeyPressed(kb, Keys.Left)) ChangeRoomSetting(_roomSettingsRow, -1);
            if (IsKeyPressed(kb, Keys.Right)) ChangeRoomSetting(_roomSettingsRow, 1);
            if (IsKeyPressed(kb, Keys.Enter) || IsKeyPressed(kb, Keys.Space))
                ActivateRoomSettingsRow(_roomSettingsRow, 1);
        }

        private int NextRow(int row, int dir)
        {
            for (int n = 0; n < RS_ROWS; n++)
            {
                row = (row + dir + RS_ROWS) % RS_ROWS;
                if (row != RS_PASSWORD || _rsPrivate) return row;
            }
            return row;
        }

        private void ActivateRoomSettingsRow(int row, int dir)
        {
            if (!RowEditable(row)) return;
            if (row == RS_NAME)
                BeginTextField("rs-name", _rsName, Protocol.MaxRoomNameLength, v => { if (v.Trim().Length > 0) _rsName = v.Trim(); }, title: "Room name");
            else if (row == RS_PASSWORD)
                BeginTextField("rs-password", _rs.Password, 32, v => _rs.Password = v.Trim(), title: "Room password");
            else
                ChangeRoomSetting(row, dir == 0 ? 1 : dir);
        }

        private void ChangeRoomSetting(int row, int dir)
        {
            if (!RowEditable(row)) return;
            switch (row)
            {
                case RS_MODE:
                    int i = Array.IndexOf(ModeOrder, _rs.Mode);
                    _rs.Mode = ModeOrder[((i < 0 ? 0 : i) + dir + ModeOrder.Length) % ModeOrder.Length];
                    // osu! songs have one strumline: only Mirror and Teams play them.
                    if (!Protocol.OsuModeAllowed(_rs.Mode) && _rs.SongTypes == Protocol.SongTypesOsu)
                        _rs.SongTypes = Protocol.SongTypesBoth;
                    break;
                case RS_SELECT:
                    _rs.SongSelection = _rs.SongSelection == Protocol.SelectTurns ? Protocol.SelectHost : Protocol.SelectTurns;
                    break;
                case RS_PRIVACY:
                    _rsPrivate = !_rsPrivate;
                    if (_rsPrivate && string.IsNullOrEmpty(_rs.Password))
                        BeginTextField("rs-password", "", 32, v => _rs.Password = v.Trim(), title: "Room password");
                    break;
                case RS_LIMIT:
                    int min = _roomSettingsMode == RoomSettingsMode.Edit ? Math.Max(Protocol.MinPlayers, Room.State?.Members.Count ?? 2) : Protocol.MinPlayers;
                    _rs.MaxPlayers = Math.Clamp(_rs.MaxPlayers + dir, min, Math.Max(min, NetworkManager.Instance.MaxRoomPlayers));
                    break;
                case RS_GUESTS:
                    _rs.AllowGuestLocalLevels = !_rs.AllowGuestLocalLevels;
                    break;
                case RS_TYPES:
                    int ti = Array.IndexOf(SongTypesOrder, _rs.SongTypes);
                    _rs.SongTypes = SongTypesOrder[((ti < 0 ? 0 : ti) + dir + SongTypesOrder.Length) % SongTypesOrder.Length];
                    if (_rs.SongTypes == Protocol.SongTypesOsu && !Protocol.OsuModeAllowed(_rs.Mode)) _rs.Mode = Protocol.ModeMirror; // osu!: Mirror or Teams
                    break;
                default:
                    return;
            }
            FunkyMakerPlayer.UI.UiSfx.Play(FunkyMakerPlayer.UI.UiSound.Toggle);
        }

        private void SubmitRoomSettings()
        {
            var settings = new RoomSettings
            {
                Mode = _rs.Mode,
                SongSelection = _rs.SongSelection,
                MaxPlayers = _rs.MaxPlayers,
                AllowGuestLocalLevels = _rs.AllowGuestLocalLevels,
                SongTypes = _rs.SongTypes,
                Password = _rsPrivate ? _rs.Password.Trim() : "",
            };
            if (_rsPrivate && settings.Password.Length == 0)
            {
                ShowError("Type a password, or make the room open.");
                _roomSettingsRow = RS_PASSWORD;
                return;
            }
            _rsBusy = true;
            if (_roomSettingsMode == RoomSettingsMode.Create)
            {
                if (_rsName.Trim().Length == 0) { _rsBusy = false; ShowError("The room needs a name."); return; }
                Room.CreateRoom(_rsName.Trim(), settings, r =>
                {
                    _rsBusy = false;
                    if (!r.Ok) { ShowError(r.Error); return; }
                    EnterWaitingRoom();
                });
            }
            else
            {
                Room.UpdateSettings(settings, r =>
                {
                    _rsBusy = false;
                    if (!r.Ok) { ShowError(r.Error); return; }
                    ShowInfo("Room settings saved.", 2.5f);
                    TransitionToState(MenuState.WaitingRoom);
                });
            }
        }

        // ══════════════════════════════════════════════════════════════════
        // Draw
        // ══════════════════════════════════════════════════════════════════

        private void DrawRoomSettings(SpriteBatch sb, SpriteFont font, Texture2D pixel, int sw, int sh)
        {
            _clickableAreas.Clear();
            float uiScale = GetUiScale(sw, sh);
            string title = _roomSettingsMode switch
            {
                RoomSettingsMode.Create => "CREATE ROOM",
                RoomSettingsMode.Edit => "ROOM SETTINGS",
                _ => "ROOM SETTINGS (VIEW)",
            };
            string subtitle = _roomSettingsMode switch
            {
                RoomSettingsMode.Create => "You'll be the host of this room",
                RoomSettingsMode.Edit => "Changes apply from the next round and are announced in the chat",
                _ => "Only the host can change these",
            };
            int headerBottom = DrawScreenHeader(sb, font, pixel, sw, uiScale, title, subtitle, ACCENT_SUCCESS);
            int footerY = DrawFooterBar(sb, font, pixel, sw, sh, uiScale, _roomSettingsMode == RoomSettingsMode.View
                ? "ESC: Back"
                : "UP/DOWN: Select  |  LEFT/RIGHT: Change  |  ENTER: Edit text  |  CTRL+ENTER: Save  |  ESC: Back");

            // Layout (fork, 2026-11-28): the rows used to be one fixed-height column that ran under the
            // buttons and off the screen on a phone. Now: one column when it fits, otherwise two
            // columns (filled top to bottom, left column first) on a wider panel; the panel is always
            // exactly as tall as its content and stays above the footer.
            var rows = new List<int>();
            for (int row = 0; row < RS_ROWS; row++)
                if (row != RS_PASSWORD || _rsPrivate) rows.Add(row);

            int y = headerBottom + Px(10, uiScale);
            int pad = Px(18, uiScale, 10);
            int rowH = Px(50, uiScale, 40);
            int gap = Px(8, uiScale, 5);
            int buttonsH = Px(50, uiScale, 40);
            int df = FontPx(14, uiScale, 11, 19);
            float descLineH = MeasureText(font, "Ag", df).Y + Px(2, uiScale);
            int descH = (int)(descLineH * 2) + Px(10, uiScale);
            int maxPanelH = footerY - y - Px(10, uiScale);
            int fixedH = pad + pad + descH + buttonsH + pad;

            int cols = 1;
            int perCol = rows.Count;
            if (fixedH + perCol * (rowH + gap) > maxPanelH)
            {
                cols = 2;
                perCol = (rows.Count + 1) / 2;
            }
            // Still too tall: shrink the rows (never below a comfortable tap size)
            int fitRowH = (maxPanelH - fixedH) / Math.Max(1, perCol) - gap;
            if (fitRowH < rowH) rowH = Math.Max(Px(36, uiScale, 30), fitRowH);

            int w = cols == 1
                ? Math.Min(Px(760, uiScale), sw - Px(40, uiScale))
                : Math.Min(Px(1180, uiScale), sw - Px(32, uiScale));
            int x = (sw - w) / 2;
            int panelH = Math.Min(maxPanelH, fixedH + perCol * (rowH + gap) - gap);
            DrawPanel(sb, pixel, x, y, w, panelH, BG_MEDIUM, ACCENT_SUCCESS * 0.6f, 2);

            int colGap = Px(14, uiScale, 8);
            int cellW = (w - pad * 2 - colGap * (cols - 1)) / cols;
            int labelF = FontPx(16, uiScale, 12, 22);
            int ry = y + pad;
            for (int k = 0; k < rows.Count; k++)
            {
                int row = rows[k];
                int col = k / perCol;
                var r = new Rectangle(x + pad + col * (cellW + colGap), y + pad + (k % perCol) * (rowH + gap), cellW, rowH);
                ry = Math.Max(ry, r.Bottom + gap);
                bool selected = row == _roomSettingsRow && _roomSettingsMode != RoomSettingsMode.View;
                bool editable = RowEditable(row);
                DrawPanel(sb, pixel, r.X, r.Y, r.Width, r.Height, selected ? BG_LIGHT : BG_DARK,
                    selected ? ACCENT_SUCCESS : BORDER_SUBTLE, selected ? 2 : 1, selected ? ACCENT_SUCCESS : null);
                if (editable && row != RS_NAME && row != RS_PASSWORD) Hit($"rs:{row}", r);

                // Value control: right-aligned and only as wide as it needs (it used to stretch over
                // 58 % of the row, with the arrows far from the value)
                int innerPad = Px(12, uiScale, 8);
                bool textRow = row == RS_NAME || row == RS_PASSWORD;
                int valueW = textRow ? (int)(r.Width * 0.58f) : Math.Min(Px(230, uiScale), (int)(r.Width * 0.58f));
                int valueX = r.Right - innerPad - valueW;

                string label = TruncateToWidth(font, RowLabel(row), valueX - r.X - innerPad * 2, labelF);
                Vector2 lsz = MeasureText(font, label, labelF);
                DrawText(sb, font, label, new Vector2(r.X + innerPad + Px(4, uiScale), r.Y + (r.Height - lsz.Y) / 2f), (editable ? TEXT_PRIMARY : TEXT_SECONDARY) * _fadeAlpha, labelF);
                if (row == RS_NAME || row == RS_PASSWORD)
                {
                    int boxH = rowH - Px(12, uiScale, 6);
                    var box = new Rectangle(valueX, r.Y + (rowH - boxH) / 2, valueW, boxH);
                    string id = row == RS_NAME ? "rs-name" : "rs-password";
                    DrawTextBox(sb, font, pixel, uiScale, id, box, row == RS_NAME ? _rsName : _rs.Password,
                        row == RS_NAME ? "Room name" : "Password", password: false, enabled: editable);
                    if (editable) Hit($"rs:{row}", box);
                }
                else
                {
                    int arrowW = Px(38, uiScale, 30);
                    int vf = FontPx(16, uiScale, 12, 22);
                    string value = TruncateToWidth(font, RowValue(row), valueW - arrowW * 2 - Px(8, uiScale), vf);
                    Vector2 vsz = MeasureText(font, value, vf);
                    float vx = valueX + (valueW - vsz.X) / 2f;
                    DrawText(sb, font, value, new Vector2(vx, r.Y + (r.Height - vsz.Y) / 2f),
                        (editable ? (selected ? ACCENT_SUCCESS : TEXT_PRIMARY) : TEXT_SECONDARY) * _fadeAlpha, vf, shadow: selected);
                    if (editable)
                    {
                        var left = new Rectangle(valueX, r.Y + Px(6, uiScale), arrowW, rowH - Px(12, uiScale));
                        var right = new Rectangle(valueX + valueW - arrowW, left.Y, arrowW, left.Height);
                        DrawRoomButton(sb, font, pixel, uiScale, $"rs<{row}", left, "<", ACCENT_SUCCESS);
                        DrawRoomButton(sb, font, pixel, uiScale, $"rs>{row}", right, ">", ACCENT_SUCCESS);
                    }
                }
            }

            // Description of the selected row (two lines; smaller text if it needs more)
            string desc = RowDescription(_roomSettingsRow);
            string wrapped = WrapTextToWidth(font, desc, w - pad * 2, df);
            while (df > 10 && MeasureText(font, wrapped, df).Y > descH - Px(6, uiScale))
            {
                df--;
                wrapped = WrapTextToWidth(font, desc, w - pad * 2, df);
            }
            DrawText(sb, font, wrapped, new Vector2(x + pad, ry + Px(2, uiScale)), TEXT_SECONDARY * _fadeAlpha, df);

            int by = y + panelH - pad - buttonsH;
            int bw = (w - pad * 3) / 2;
            DrawRoomButton(sb, font, pixel, uiScale, "rs-back", new Rectangle(x + pad, by, bw, buttonsH), FunkyMakerPlayer.UI.PointerInput.BackLabel, TEXT_SECONDARY);
            if (_roomSettingsMode != RoomSettingsMode.View)
            {
                string save = _rsBusy ? "..." : _roomSettingsMode == RoomSettingsMode.Create ? "CREATE ROOM" : "SAVE";
                DrawRoomButton(sb, font, pixel, uiScale, "rs-save", new Rectangle(x + pad * 2 + bw, by, bw, buttonsH), save, ACCENT_SUCCESS, enabled: !_rsBusy, strong: true);
            }
        }

        public static string SongTypesName(string types) => types switch
        {
            Protocol.SongTypesFnf => "FNF only",
            Protocol.SongTypesOsu => "osu!mania only",
            _ => "FNF & osu!",
        };

        private static string RowLabel(int row) => row switch
        {
            RS_NAME => "Room name",
            RS_MODE => "Game mode",
            RS_SELECT => "Song selection",
            RS_PRIVACY => "Privacy",
            RS_PASSWORD => "Password",
            RS_LIMIT => "Player limit",
            RS_TYPES => "Song types",
            _ => "Guests' local levels",
        };

        private string RowValue(int row) => row switch
        {
            RS_MODE => RoomClient.ModeName(_rs.Mode),
            RS_SELECT => _rs.SongSelection == Protocol.SelectHost ? "Host only" : "By turns",
            RS_PRIVACY => _rsPrivate ? "Password" : "Open",
            RS_LIMIT => $"{_rs.MaxPlayers} players",
            RS_GUESTS => _rs.AllowGuestLocalLevels ? "Allowed" : "Official / host only",
            RS_TYPES => SongTypesName(_rs.SongTypes),
            _ => "",
        };

        private string RowDescription(int row) => row switch
        {
            RS_NAME => _roomSettingsMode == RoomSettingsMode.Create
                ? "Shown in the room list; players can search for it. It can't be changed later."
                : "The room name can't be changed after the room is created.",
            RS_MODE => _rs.Mode switch
            {
                Protocol.ModeTeams => "TEAMS: players are split at random into Player and Enemy. The team with the higher average score wins.",
                Protocol.ModeMirror => "MIRROR: everybody plays the same side. Before the round, players vote Player or Enemy; the best score wins.",
                _ => "VERSUS: two players, one as Player and one as Enemy; the best score wins. With more players, the rest must spectate.",
            },
            RS_SELECT => _rs.SongSelection == Protocol.SelectHost
                ? "HOST ONLY: only the host picks the songs."
                : "BY TURNS: players pick songs one after another, in the order they joined (host first). Spectators are skipped.",
            RS_PRIVACY => "Password rooms show a lock in the list and ask for the password to join.",
            RS_PASSWORD => "Players need this to join. Only you (the host) can see it.",
            RS_LIMIT => $"Between {Protocol.MinPlayers} and {NetworkManager.Instance.MaxRoomPlayers} players.",
            RS_TYPES => _rs.SongTypes switch
            {
                Protocol.SongTypesFnf => "FNF ONLY: official levels and Funky Maker levels (.fnmm / .zip / .rar).",
                Protocol.SongTypesOsu => "OSU!MANIA ONLY: 4K osu!mania songs from the players' mods/osu folders. Mirror or Teams (one strumline: in Teams both teams play the same chart).",
                _ => Protocol.OsuModeAllowed(_rs.Mode)
                    ? "FNF & OSU!: official levels, Funky Maker levels and 4K osu!mania songs."
                    : "FNF & OSU!: official levels, Funky Maker levels and 4K osu!mania songs (osu! songs only in Mirror and Teams).",
            },
            _ => _rs.AllowGuestLocalLevels
                ? "ALLOWED: anybody can pick a level that someone in the room has locally; it is sent to whoever lacks it."
                : "OFFICIAL / HOST ONLY: players can pick official levels and the host's local levels only.",
        };
    }
}
