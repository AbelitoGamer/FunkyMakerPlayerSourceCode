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
    /// Room browser (fork): replaces QUICK PLAY / PRIVATE ROOM. A vertical list of rooms with a name
    /// search, a "hide password rooms" checkbox and CREATE ROOM. Each card: lock / open, name, mode,
    /// song being played (with stars), host, a square per player (hover = name) and players / max.
    /// </summary>
    public partial class MultiplayerMenu
    {
        private int _roomListSelection;
        private int _roomListScroll;
        private int _roomListVisible = 5;
        private float _roomListTimer;
        private string _roomSearch = "";
        private bool _hidePasswordRooms;
        private bool _joiningRoom;

        // Password prompt for a locked room.
        private RoomSummary? _passwordRoom;
        private string _passwordValue = "";

        private List<RoomSummary> FilteredRooms()
        {
            IEnumerable<RoomSummary> rooms = Room.Rooms;
            if (_hidePasswordRooms) rooms = rooms.Where(r => !r.HasPassword);
            string q = _roomSearch.Trim();
            if (q.Length > 0) rooms = rooms.Where(r => r.Name.Contains(q, StringComparison.OrdinalIgnoreCase));
            return rooms.ToList();
        }

        private void UpdateRoomBrowser(KeyboardState kb, MouseState mouse, float dt)
        {
            if (UpdateTextField(kb, dt)) return;
            if (_inputCooldown > 0f || _joiningRoom) return;

            if (_passwordRoom != null)
            {
                UpdatePasswordPrompt(kb, mouse);
                return;
            }

            var rooms = FilteredRooms();
            _roomListSelection = Math.Clamp(_roomListSelection, 0, Math.Max(0, rooms.Count - 1));

            string? hover = HoverHit();
            if (_mouseMovedThisFrame && hover != null && hover.StartsWith("room:") && int.TryParse(hover[5..], out int hi))
                _roomListSelection = hi;

            int wheel = mouse.ScrollWheelValue - _prevMouse.ScrollWheelValue;
            if (wheel != 0 && rooms.Count > 0)
                _roomListSelection = Math.Clamp(_roomListSelection + (wheel > 0 ? -1 : 1), 0, rooms.Count - 1);
            if (IsKeyPressed(kb, Keys.Down) && rooms.Count > 0) _roomListSelection = (_roomListSelection + 1) % rooms.Count;
            if (IsKeyPressed(kb, Keys.Up) && rooms.Count > 0) _roomListSelection = (_roomListSelection - 1 + rooms.Count) % rooms.Count;
            AdjustRoomListScroll(rooms.Count);

            string? clicked = ClickedHit(mouse);
            if (clicked == "search")
            {
                BeginTextField("search", _roomSearch, 32, v => { _roomSearch = v.Trim(); _roomListSelection = 0; }, title: "Search rooms");
                return;
            }
            if (clicked == "hidepw")
            {
                _hidePasswordRooms = !_hidePasswordRooms;
                _roomListSelection = 0;
                _inputCooldown = INPUT_COOLDOWN_TIME;
                return;
            }
            if (clicked == "refresh")
            {
                Room.RefreshRooms();
                RefreshLevelList();
                RescanSharedLevels();
                _roomListTimer = 3f;
                _inputCooldown = INPUT_COOLDOWN_TIME;
                return;
            }
            if (clicked == "create")
            {
                OpenRoomSettings(RoomSettingsMode.Create);
                return;
            }
            if (clicked != null && clicked.StartsWith("room:") && int.TryParse(clicked[5..], out int ci) && ci < rooms.Count)
            {
                _roomListSelection = ci;
                TryJoinRoom(rooms[ci]);
                return;
            }
            if ((IsKeyPressed(kb, Keys.Enter) || IsKeyPressed(kb, Keys.Space)) && rooms.Count > 0)
            {
                TryJoinRoom(rooms[_roomListSelection]);
                return;
            }
            if (IsKeyPressed(kb, Keys.Escape) || IsRectClicked(mouse, _backButtonRect))
            {
                NetworkManager.Instance.EnableAutoReconnect(false);
                NetworkManager.Instance.Disconnect();
                TransitionToState(MenuState.MainMenu);
            }
        }

        private void AdjustRoomListScroll(int count)
        {
            int visible = Math.Max(1, _roomListVisible);
            if (_roomListSelection >= _roomListScroll + visible) _roomListScroll = _roomListSelection - visible + 1;
            if (_roomListSelection < _roomListScroll) _roomListScroll = _roomListSelection;
            _roomListScroll = Math.Clamp(_roomListScroll, 0, Math.Max(0, count - visible));
        }

        private void TryJoinRoom(RoomSummary room)
        {
            if (room.PlayerCount >= room.MaxPlayers)
            {
                ShowError("This room is full.");
                return;
            }
            if (room.HasPassword)
            {
                _passwordRoom = room;
                _passwordValue = "";
                BeginTextField("password", "", 32, v => { _passwordValue = v; JoinRoomNow(room, v); },
                    password: true, title: $"Password for {room.Name}", cancel: () => _passwordRoom = null,
                    blur: v => _passwordValue = v);
                return;
            }
            JoinRoomNow(room, "");
        }

        private void JoinRoomNow(RoomSummary room, string password)
        {
            _passwordRoom = null;
            _joiningRoom = true;
            Room.JoinRoom(room.Id, password, r =>
            {
                _joiningRoom = false;
                if (!r.Ok)
                {
                    ShowError(r.Error);
                    Room.RefreshRooms();
                    return;
                }
                EnterWaitingRoom();
            });
        }

        private void UpdatePasswordPrompt(KeyboardState kb, MouseState mouse)
        {
            string? clicked = ClickedHit(mouse);
            if (clicked == "pw-cancel" || IsKeyPressed(kb, Keys.Escape))
            {
                _passwordRoom = null;
                EndTextField();
                _inputCooldown = INPUT_COOLDOWN_TIME;
                return;
            }
            if (clicked == "password")
            {
                var room = _passwordRoom!;
                BeginTextField("password", _passwordValue, 32, v => { _passwordValue = v; JoinRoomNow(room, v); },
                    password: true, cancel: () => _passwordRoom = null, blur: v => _passwordValue = v);
                return;
            }
            if (clicked == "pw-join" && _passwordRoom != null)
                JoinRoomNow(_passwordRoom, _passwordValue);
        }

        // ══════════════════════════════════════════════════════════════════
        // Draw
        // ══════════════════════════════════════════════════════════════════

        private void DrawRoomBrowser(SpriteBatch sb, SpriteFont font, Texture2D pixel, int sw, int sh)
        {
            _clickableAreas.Clear();
            _holderSquares.Clear();
            float uiScale = GetUiScale(sw, sh);
            int headerBottom = DrawScreenHeader(sb, font, pixel, sw, uiScale, "ONLINE ROOMS",
                "Join a room, or create your own", ACCENT_PRIMARY, "ESC  Disconnect");
            DrawPlayerIdentityChip(sb, font, pixel, Px(24, uiScale, 12), Px(18, uiScale, 10), uiScale);
            int footerY = DrawFooterBar(sb, font, pixel, sw, sh, uiScale,
                "UP/DOWN: Select  |  ENTER: Join  |  ESC: Disconnect");

            int margin = Px(28, uiScale, 14);
            int barY = headerBottom + Px(8, uiScale);
            int barH = Px(40, uiScale, 30);
            int x = margin, w = sw - margin * 2;

            // Top bar: search | hide password rooms | refresh | create
            int createW = Px(190, uiScale, 140);
            var createRect = new Rectangle(x + w - createW, barY, createW, barH);
            DrawRoomButton(sb, font, pixel, uiScale, "create", createRect, "+  CREATE ROOM", ACCENT_SUCCESS, strong: true);
            int refreshW = Px(110, uiScale, 84);
            var refreshRect = new Rectangle(createRect.X - Px(10, uiScale) - refreshW, barY, refreshW, barH);
            DrawRoomButton(sb, font, pixel, uiScale, "refresh", refreshRect, "Refresh", ACCENT_WARNING);
            var cb = DrawCheckbox(sb, font, pixel, uiScale, "hidepw", x, barY, barH, "Hide password rooms", _hidePasswordRooms);
            int searchX = cb.Right + Px(16, uiScale);
            var searchRect = new Rectangle(searchX, barY, Math.Max(Px(160, uiScale), refreshRect.X - Px(16, uiScale) - searchX), barH);
            DrawTextBox(sb, font, pixel, uiScale, "search", searchRect, _roomSearch, "Search rooms by name...");

            // List
            int listY = barY + barH + Px(14, uiScale);
            int listH = Math.Max(120, footerY - listY - Px(12, uiScale));
            DrawPanel(sb, pixel, x, listY, w, listH, BG_MEDIUM, BORDER_SUBTLE, 1);
            var rooms = FilteredRooms();
            int pad = Px(14, uiScale, 8);
            int cardH = Px(78, uiScale, 58);
            int gap = Px(8, uiScale, 5);
            _roomListVisible = Math.Max(1, (listH - pad * 2 + gap) / (cardH + gap));
            AdjustRoomListScroll(rooms.Count);

            if (!Room.RoomsLoaded)
            {
                DrawCenteredTextInPanel(sb, font, "Loading rooms...", x, listY + listH / 2 - Px(12, uiScale), w, TEXT_SECONDARY, FontPx(18, uiScale, 13, 26));
            }
            else if (rooms.Count == 0)
            {
                string msg = Room.Rooms.Count == 0 ? "No rooms yet. Create the first one!" : "No rooms match your search.";
                DrawCenteredTextInPanel(sb, font, msg, x, listY + listH / 2 - Px(14, uiScale), w, TEXT_SECONDARY, FontPx(18, uiScale, 13, 26));
                DrawCenteredTextInPanel(sb, font, FunkyMakerPlayer.UI.PointerInput.IsTouch ? "Tap CREATE ROOM to make one" : "Press C or click CREATE ROOM", x, listY + listH / 2 + Px(16, uiScale), w, TEXT_DISABLED, FontPx(14, uiScale, 11, 20));
            }
            else
            {
                bool scroll = rooms.Count > _roomListVisible;
                int cardW = w - pad * 2 - (scroll ? Px(16, uiScale, 10) : 0);
                int end = Math.Min(rooms.Count, _roomListScroll + _roomListVisible);
                for (int i = _roomListScroll; i < end; i++)
                {
                    int cy = listY + pad + (i - _roomListScroll) * (cardH + gap);
                    DrawRoomCard(sb, font, pixel, uiScale, rooms[i], i, new Rectangle(x + pad, cy, cardW, cardH), i == _roomListSelection);
                }
                if (scroll)
                {
                    int trackH = _roomListVisible * (cardH + gap) - gap;
                    DrawScrollBar(sb, pixel, x + pad + cardW + Px(6, uiScale), listY + pad, trackH, rooms.Count, _roomListVisible, _roomListScroll, ACCENT_PRIMARY);
                }
            }

            if (_passwordRoom != null) DrawPasswordPrompt(sb, font, pixel, uiScale, sw, sh);
            if (_joiningRoom)
                DrawCenteredText(sb, font, "Joining room...", footerY - Px(34, uiScale), ACCENT_WARNING, FontPx(15, uiScale, 11, 22), sw);
            DrawHolderTooltip(sb, font, pixel, uiScale, sw, sh);
        }

        private void DrawRoomCard(SpriteBatch sb, SpriteFont font, Texture2D pixel, float uiScale, RoomSummary room, int index, Rectangle r, bool selected)
        {
            bool hovered = r.Contains(_lastMousePos);
            bool full = room.PlayerCount >= room.MaxPlayers;
            Color accent = ModeColor(room.Mode);
            DrawPanel(sb, pixel, r.X, r.Y, r.Width, r.Height, selected || hovered ? BG_LIGHT : BG_DARK,
                selected ? accent : hovered ? ACCENT_WARNING : BORDER_SUBTLE, selected ? 2 : 1, selected ? accent : null);
            Hit($"room:{index}", r);

            int pad = Px(14, uiScale, 8);
            int nameF = FontPx(19, uiScale, 13, 28);
            int metaF = FontPx(13, uiScale, 10, 18);
            int pillH = Px(22, uiScale, 18);
            int pillF = FontPx(11, uiScale, 10, 15);
            int row1 = r.Y + Px(10, uiScale, 6);
            int row2 = r.Y + r.Height - pillH - Px(10, uiScale, 6);

            // Right column: players / max and squares.
            string count = $"{room.PlayerCount} / {room.MaxPlayers}";
            int countF = FontPx(18, uiScale, 13, 26);
            Vector2 countSz = MeasureText(font, count, countF);
            int right = r.Right - pad;
            DrawText(sb, font, count, new Vector2(right - countSz.X, row1), (full ? ACCENT_ERROR : TEXT_PRIMARY) * _fadeAlpha, countF, shadow: true);
            int sq = Px(14, uiScale, 10), sqGap = Px(5, uiScale, 3);
            int sqX = right - room.Players.Count * (sq + sqGap) + sqGap;
            foreach (var p in room.Players)
            {
                DrawPlayerSquare(sb, pixel, new Rectangle(sqX, row2 + (pillH - sq) / 2, sq, sq), p.ColorHue, p.Username);
                sqX += sq + sqGap;
            }
            int textRight = Math.Min(right - (int)countSz.X - Px(18, uiScale), right - room.Players.Count * (sq + sqGap) - Px(18, uiScale));

            // Row 1: lock/open + name
            int px = r.X + pad;
            int lockW = DrawPillBadge(sb, font, pixel, px, row1 + Px(2, uiScale), pillH, room.HasPassword ? "PASSWORD" : "OPEN",
                room.HasPassword ? ACCENT_WARNING : ACCENT_SUCCESS, pillF, Px(8, uiScale), filledStrong: room.HasPassword);
            int nameX = px + lockW + Px(10, uiScale);
            string name = TruncateToWidth(font, room.Name, Math.Max(40, textRight - nameX), nameF);
            DrawText(sb, font, name, new Vector2(nameX, row1), (selected ? TEXT_PRIMARY : TEXT_SECONDARY) * _fadeAlpha, nameF, shadow: selected);

            // Row 2: mode | host | song
            int mx = DrawPillBadge(sb, font, pixel, px, row2, pillH, RoomClient.ModeName(room.Mode).ToUpperInvariant(), accent, pillF, Px(8, uiScale), filledStrong: true);
            int metaX = px + mx + Px(10, uiScale);
            string song = room.SongName.Length > 0
                ? $"Playing: {room.SongName}{(room.SongStars > 0.05f ? $"  {StarsText(room.SongStars)}" : "")}"
                : "In the waiting room";
            string meta = $"Host: {room.HostName}   |   {song}";
            meta = TruncateToWidth(font, meta, Math.Max(40, textRight - metaX), metaF);
            Vector2 metaSz = MeasureText(font, meta, metaF);
            DrawText(sb, font, meta, new Vector2(metaX, row2 + (pillH - metaSz.Y) / 2f), (room.SongName.Length > 0 ? ACCENT_PRIMARY : TEXT_DISABLED) * _fadeAlpha, metaF);
        }

        private void DrawPasswordPrompt(SpriteBatch sb, SpriteFont font, Texture2D pixel, float uiScale, int sw, int sh)
        {
            sb.Draw(pixel, new Rectangle(0, 0, sw, sh), Color.Black * 0.6f);
            int w = Math.Min(Px(520, uiScale), sw - 32), h = Px(210, uiScale, 160);
            int x = (sw - w) / 2, y = (sh - h) / 2;
            DrawPanel(sb, pixel, x, y, w, h, BG_MEDIUM, ACCENT_WARNING, 2, ACCENT_WARNING);
            DrawCenteredTextInPanel(sb, font, "PASSWORD REQUIRED", x, y + Px(16, uiScale), w, ACCENT_WARNING, FontPx(20, uiScale, 14, 28), shadow: true);
            DrawCenteredTextInPanel(sb, font, TruncateToWidth(font, _passwordRoom?.Name ?? "", w - 40, FontPx(14, uiScale, 11, 20)), x, y + Px(48, uiScale), w, TEXT_SECONDARY, FontPx(14, uiScale, 11, 20));
            int pad = Px(24, uiScale, 14);
            int boxH = Px(40, uiScale, 30);
            DrawTextBox(sb, font, pixel, uiScale, "password", new Rectangle(x + pad, y + Px(80, uiScale), w - pad * 2, boxH), _passwordValue, "Type the password", password: true);
            int bw = (w - pad * 3) / 2, by = y + h - boxH - Px(16, uiScale);
            DrawRoomButton(sb, font, pixel, uiScale, "pw-cancel", new Rectangle(x + pad, by, bw, boxH), "ESC  Cancel", TEXT_SECONDARY);
            DrawRoomButton(sb, font, pixel, uiScale, "pw-join", new Rectangle(x + pad * 2 + bw, by, bw, boxH), "ENTER  Join", ACCENT_SUCCESS, strong: true);
        }
    }
}
