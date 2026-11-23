using System;
using System.Collections.Generic;
using System.Linq;
using FunkyMakerPlayer.Multiplayer.Rooms;
using FunkyMakerPlayer.UI;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using Microsoft.Xna.Framework.Input;

namespace FunkyMakerPlayer.Multiplayer
{
    /// <summary>
    /// Shared pieces of the room screens (fork): room events, named click targets, a free-text field
    /// (chat, room name, password, search) and the local level scan.
    /// </summary>
    public partial class MultiplayerMenu
    {
        private RoomClient Room => RoomClient.Instance;
        private bool _roomEventsHooked;

        private void HookRoomEvents()
        {
            if (_roomEventsHooked) return;
            _roomEventsHooked = true;
            Room.Kicked += OnRoomKicked;
            Room.Error += OnRoomError;
            Room.LocalLevelsChanged += OnRoomLocalLevelsChanged;
            Room.RoundFinished += OnRoomRoundFinished;
        }

        private void UnhookRoomEvents()
        {
            if (!_roomEventsHooked) return;
            _roomEventsHooked = false;
            Room.Kicked -= OnRoomKicked;
            Room.Error -= OnRoomError;
            Room.LocalLevelsChanged -= OnRoomLocalLevelsChanged;
            Room.RoundFinished -= OnRoomRoundFinished;
        }

        private void OnRoomKicked(string reason)
        {
            ShowError(string.IsNullOrWhiteSpace(reason) ? "You left the room." : reason);
            if (_state is MenuState.WaitingRoom or MenuState.SongSelect or MenuState.RoomSettings)
                OpenRoomBrowser();
        }

        private void OnRoomError(string message)
        {
            if (!string.IsNullOrWhiteSpace(message)) ShowError(message);
        }

        private void OnRoomLocalLevelsChanged()
        {
            RefreshLevelListKeepingSelection();
            _levelsChangedForOthers = true;
        }

        private void OnRoomRoundFinished(RoundResult r)
        {
            // Rounds show up in the waiting room's list; nothing to do in the menu itself.
        }

        /// <summary>Per frame: leave room screens when the room is gone, poll the room list.</summary>
        private void UpdateRoomScreens(float dt)
        {
            bool roomScreen = _state is MenuState.WaitingRoom or MenuState.SongSelect
                || (_state == MenuState.RoomSettings && _roomSettingsMode != RoomSettingsMode.Create);
            if (roomScreen && !Room.InRoom && !_joiningRoom && _awaitingRoomState <= 0f)
            {
                if (NetworkManager.Instance.IsConnected) OpenRoomBrowser();
                else TransitionToState(MenuState.MainMenu);
            }

            if (_state == MenuState.RoomBrowser && NetworkManager.Instance.IsConnected)
            {
                _roomListTimer -= dt;
                if (_roomListTimer <= 0f)
                {
                    _roomListTimer = 3f;
                    Room.RefreshRooms();
                }
            }
        }

        private void ResendProfile() => NetworkManager.Instance.ResendProfile();

        /// <summary>Room browser right after connecting (and when leaving a room).</summary>
        private void OpenRoomBrowser()
        {
            TransitionToState(MenuState.RoomBrowser);
            _roomListTimer = 0f;
            _roomListSelection = 0;
            _roomListScroll = 0;
            Room.RefreshRooms();
            if (Room.OfficialLevels.Count == 0) NetworkManager.Instance.RequestOfficialLevels();
            RescanSharedLevels();
        }

        /// <summary>Hashes your levels and tells the room (RoomClient sends the list when in a room).</summary>
        private void RescanSharedLevels() => Room.ScanLocalLevels(_levelsFolder, _availableLevels.ToList());

        private static string FormatMb(long bytes) => $"{bytes / (1024.0 * 1024.0):F1} MB";

        private static string StarsText(float stars) => stars > 0.05f ? $"★{stars:0.0}" : "";

        // ══════════════════════════════════════════════════════════════════
        // Named click targets (cleared every Draw)
        // ══════════════════════════════════════════════════════════════════

        private readonly List<(Rectangle Rect, string Key)> _roomHit = new();

        private Rectangle Hit(string key, Rectangle r)
        {
            _roomHit.Add((r, key));
            return r;
        }

        private bool HitHovered(string key) => _roomHit.Any(h => h.Key == key && h.Rect.Contains(_lastMousePos));

        /// <summary>Key of the topmost click target under the mouse that was clicked this frame, or null.</summary>
        private string? ClickedHit(MouseState mouse)
        {
            if (!IsLeftClickReleased(mouse)) return null;
            var p = new Point(mouse.X, mouse.Y);
            for (int i = _roomHit.Count - 1; i >= 0; i--)
                if (_roomHit[i].Rect.Contains(p)) return _roomHit[i].Key;
            return null;
        }

        /// <summary>Key under the mouse (hover), or null.</summary>
        private string? HoverHit()
        {
            for (int i = _roomHit.Count - 1; i >= 0; i--)
                if (_roomHit[i].Rect.Contains(_lastMousePos)) return _roomHit[i].Key;
            return null;
        }

        /// <summary>A clickable button panel. Returns its rectangle.</summary>
        private Rectangle DrawRoomButton(SpriteBatch sb, SpriteFont font, Texture2D pixel, float uiScale, string key,
            Rectangle r, string label, Color accent, bool enabled = true, bool strong = false, int? fontSize = null)
        {
            bool hovered = enabled && r.Contains(_lastMousePos);
            Color bg = !enabled ? BG_DARK : hovered ? BG_LIGHT : strong ? Color.Lerp(BG_MEDIUM, accent, 0.18f) : BG_MEDIUM;
            Color border = !enabled ? BORDER_SUBTLE : hovered ? ACCENT_WARNING : accent * (strong ? 1f : 0.7f);
            DrawPanel(sb, pixel, r.X, r.Y, r.Width, r.Height, bg, border, hovered || strong ? 2 : 1, enabled ? accent : null);
            int f = fontSize ?? FontPx(15, uiScale, 11, 22);
            Vector2 sz = MeasureText(font, label, f);
            string text = TruncateToWidth(font, label, r.Width - Px(16, uiScale), f);
            sz = MeasureText(font, text, f);
            DrawText(sb, font, text, new Vector2(r.X + (r.Width - sz.X) / 2f, r.Y + (r.Height - sz.Y) / 2f),
                (enabled ? (hovered ? Color.White : Color.Lerp(accent, Color.White, 0.55f)) : TEXT_DISABLED) * _fadeAlpha, f, shadow: enabled);
            if (enabled) Hit(key, r);
            return r;
        }

        /// <summary>A small coloured square for a player; records it for the name tooltip.</summary>
        private void DrawPlayerSquare(SpriteBatch sb, Texture2D pixel, Rectangle r, int hue, string name)
        {
            bool hovered = r.Contains(_lastMousePos);
            if (hovered)
                sb.Draw(pixel, new Rectangle(r.X - 2, r.Y - 2, r.Width + 4, r.Height + 4), Color.White * (0.9f * _fadeAlpha));
            sb.Draw(pixel, r, GetColorFromHue(hue) * _fadeAlpha);
            DrawBorder(sb, pixel, r.X, r.Y, r.Width, r.Height, Color.Black * (0.5f * _fadeAlpha), 1);
            _holderSquares.Add((r, name));
        }

        // ══════════════════════════════════════════════════════════════════
        // Free text field (one active at a time)
        // ══════════════════════════════════════════════════════════════════

        private string? _textFieldId;
        private string _textFieldValue = "";
        private int _textFieldMax = 32;
        private bool _textFieldPassword;
        private Action<string>? _textFieldCommit;
        private Action? _textFieldCancel;
        /// <summary>Clicking outside the field: what happens to the typed text (null = dropped).</summary>
        private Action<string>? _textFieldBlur;
        /// <summary>Mouse state of this frame (set in UpdateCore; used for click-outside).</summary>
        private MouseState _frameMouse;
        /// <summary>Keep the field focused after Enter (chat).</summary>
        private bool _textFieldSticky;
        private float _textBackspaceHold, _textBackspaceRepeat;

        private bool TextFieldActive => _textFieldId != null;
        private bool IsTextField(string id) => _textFieldId == id;

        /// <summary>Starts editing <paramref name="id"/>. Android opens the system text dialog instead.</summary>
        private void BeginTextField(string id, string initial, int maxLength, Action<string> commit,
            bool password = false, bool sticky = false, string title = "", Action? cancel = null, Action<string>? blur = null)
        {
#if ANDROID
            // System text dialog (there is no hardware keyboard to poll). Opened from the game thread;
            // the answer comes back on a worker thread and is posted to the game thread.
            if (KeyboardInput.IsVisible) return;
            TextEntry.PromptAsync(string.IsNullOrEmpty(title) ? "Text" : title, "", initial ?? "", password).ContinueWith(t =>
            {
                string? text = t.IsCompletedSuccessfully ? t.Result : null;
                if (text != null)
                {
                    if (text.Length > maxLength) text = text[..maxLength];
                    NetworkManager.Instance.Post(() => commit(text));
                }
                else if (cancel != null)
                {
                    NetworkManager.Instance.Post(cancel);
                }
            });
#else
            _textFieldId = id;
            _textFieldValue = initial ?? "";
            _textFieldMax = maxLength;
            _textFieldPassword = password;
            _textFieldCommit = commit;
            _textFieldCancel = cancel;
            _textFieldSticky = sticky;
            // Default for clicking elsewhere: keep what was typed (name, search, username); a sticky
            // field (chat) or one with a cancel action (password) only does what blur says.
            _textFieldBlur = blur ?? (sticky || cancel != null ? null : commit);
            TextEntry.Clear();
            TextEntry.Capturing = true;
#endif
        }

        private void EndTextField()
        {
            _textFieldId = null;
            _textFieldCommit = null;
            _textFieldCancel = null;
            _textFieldBlur = null;
            TextEntry.Capturing = false;
            TextEntry.Clear();
        }

        /// <summary>
        /// Handles typing for the active field. Returns true when it consumed the frame's input
        /// (the screen should not process its own hotkeys).
        /// </summary>
        private bool UpdateTextField(KeyboardState kb, float dt)
        {
            if (!TextFieldActive) return false;

            // Clicking outside the field stops editing; the click still reaches what was clicked.
            if (IsLeftClickReleased(_frameMouse) && !HitHovered(_textFieldId!))
            {
                var blur = _textFieldBlur;
                string value = _textFieldValue;
                EndTextField();
                blur?.Invoke(value);
                return false;
            }

            string typed = TextEntry.Drain();
            foreach (char c in typed)
                if (_textFieldValue.Length < _textFieldMax) _textFieldValue += c;

            if (kb.IsKeyDown(Keys.Back))
            {
                if (!_prevKb.IsKeyDown(Keys.Back))
                {
                    TrimLastChar();
                    _textBackspaceHold = 0f;
                    _textBackspaceRepeat = 0f;
                }
                else
                {
                    _textBackspaceHold += dt;
                    if (_textBackspaceHold >= 0.4f)
                    {
                        _textBackspaceRepeat += dt;
                        if (_textBackspaceRepeat >= 0.04f) { TrimLastChar(); _textBackspaceRepeat = 0f; }
                    }
                }
            }

            if (IsKeyPressed(kb, Keys.Enter))
            {
                var commit = _textFieldCommit;
                string value = _textFieldValue;
                if (_textFieldSticky) _textFieldValue = "";
                else EndTextField();
                commit?.Invoke(value);
                return true;
            }
            if (IsKeyPressed(kb, Keys.Escape))
            {
                // Fields with a cancel action cancel; other non-sticky fields keep what was typed
                // (search, name) and Esc just stops editing.
                var commit = _textFieldCommit;
                var cancel = _textFieldCancel;
                string value = _textFieldValue;
                bool sticky = _textFieldSticky;
                EndTextField();
                if (cancel != null) cancel();
                else if (!sticky) commit?.Invoke(value);
                _inputCooldown = INPUT_COOLDOWN_TIME;
                return true;
            }
            return true;
        }

        private void TrimLastChar()
        {
            if (_textFieldValue.Length > 0) _textFieldValue = _textFieldValue[..^1];
        }

        /// <summary>Draws a text box; clicking it starts editing (handled by the screen via its key).</summary>
        private Rectangle DrawTextBox(SpriteBatch sb, SpriteFont font, Texture2D pixel, float uiScale, string id,
            Rectangle r, string value, string placeholder, bool password = false, bool enabled = true)
        {
            bool editing = IsTextField(id);
            bool hovered = enabled && r.Contains(_lastMousePos);
            DrawPanel(sb, pixel, r.X, r.Y, r.Width, r.Height, editing ? BG_LIGHT : BG_DARK,
                editing ? ACCENT_PRIMARY : hovered ? ACCENT_WARNING : BORDER_SUBTLE, editing ? 2 : 1);
            string shown = editing ? _textFieldValue : value;
            if (password || (editing && _textFieldPassword)) shown = new string('•', shown.Length);
            int f = FontPx(15, uiScale, 11, 22);
            int pad = Px(10, uiScale, 6);
            bool empty = string.IsNullOrEmpty(shown);
            string text = empty ? placeholder : shown;
            float maxW = r.Width - pad * 2 - Px(8, uiScale);
            // Keep the end of long text visible while typing.
            if (editing && MeasureText(font, text, f).X > maxW)
            {
                while (text.Length > 1 && MeasureText(font, "..." + text, f).X > maxW) text = text[1..];
                text = "..." + text;
            }
            else
            {
                text = TruncateToWidth(font, text, maxW, f);
            }
            Vector2 sz = MeasureText(font, text.Length == 0 ? "A" : text, f);
            float ty = r.Y + (r.Height - sz.Y) / 2f;
            DrawText(sb, font, text, new Vector2(r.X + pad, ty), (empty ? TEXT_DISABLED : enabled ? TEXT_PRIMARY : TEXT_SECONDARY) * _fadeAlpha, f);
            if (editing && ((int)(_pulseTimer * 1.6f) % 2 == 0))
            {
                float cx = r.X + pad + (empty ? 0 : MeasureText(font, text, f).X) + 2;
                sb.Draw(pixel, new Rectangle((int)cx, (int)ty + 2, Math.Max(2, Px(2, uiScale)), (int)sz.Y - 4), ACCENT_PRIMARY * _fadeAlpha);
            }
            if (enabled) Hit(id, r);
            return r;
        }

        /// <summary>Checkbox with a label. Returns its full hit rectangle.</summary>
        private Rectangle DrawCheckbox(SpriteBatch sb, SpriteFont font, Texture2D pixel, float uiScale, string key,
            int x, int y, int h, string label, bool on)
        {
            int f = FontPx(14, uiScale, 11, 20);
            Vector2 sz = MeasureText(font, label, f);
            int box = h - Px(8, uiScale, 4);
            var r = new Rectangle(x, y, box + Px(10, uiScale) + (int)MathF.Ceiling(sz.X) + Px(6, uiScale), h);
            bool hovered = r.Contains(_lastMousePos);
            var b = new Rectangle(x, y + (h - box) / 2, box, box);
            sb.Draw(pixel, b, (on ? ACCENT_SUCCESS * 0.35f : BG_DARK) * _fadeAlpha);
            DrawBorder(sb, pixel, b.X, b.Y, b.Width, b.Height, (hovered ? ACCENT_WARNING : on ? ACCENT_SUCCESS : BORDER_SUBTLE) * _fadeAlpha, 2);
            if (on)
            {
                int inset = Math.Max(3, box / 4);
                sb.Draw(pixel, new Rectangle(b.X + inset, b.Y + inset, b.Width - inset * 2, b.Height - inset * 2), ACCENT_SUCCESS * _fadeAlpha);
            }
            DrawText(sb, font, label, new Vector2(b.Right + Px(10, uiScale), y + (h - sz.Y) / 2f), (hovered ? Color.White : TEXT_SECONDARY) * _fadeAlpha, f);
            return Hit(key, r);
        }

        /// <summary>Status pill colours for a room member.</summary>
        private Color StatusColor(string status) => status switch
        {
            Protocol.StatusReady => ACCENT_SUCCESS,
            Protocol.StatusPlaying => ACCENT_PRIMARY,
            Protocol.StatusSpectating => ACCENT_SECONDARY,
            Protocol.StatusFinished => ACCENT_WARNING,
            _ => TEXT_DISABLED,
        };

        private static string StatusLabel(string status) => status switch
        {
            Protocol.StatusReady => "READY",
            Protocol.StatusPlaying => "PLAYING",
            Protocol.StatusSpectating => "SPECTATING",
            Protocol.StatusFinished => "FINISHED",
            Protocol.StatusLeftMatch => "LEFT MATCH",
            _ => "NOT READY",
        };

        private Color ModeColor(string mode) => mode switch
        {
            Protocol.ModeTeams => ACCENT_SUCCESS,
            Protocol.ModeMirror => ACCENT_SECONDARY,
            _ => ACCENT_PRIMARY,
        };
    }
}
