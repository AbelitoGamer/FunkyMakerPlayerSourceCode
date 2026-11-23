using System;
using System.Collections.Generic;
using System.Linq;
using FunkyMakerPlayer.Multiplayer.Rooms;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;

namespace FunkyMakerPlayer.Multiplayer
{
    /// <summary>
    /// The song select LOCAL list as rows (fork). Only levels you may pick in this room are listed
    /// (the server marks them: the host's levels, or everybody's when guests' local levels are allowed),
    /// grouped into <c>YOUR LEVELS</c> and one <c>FROM &lt;player&gt;</c> section per other player for
    /// the levels you don't have (those are sent to you when picked). Each level shows a square per
    /// player who has it (hover = name). Section headers are rows too but can't be selected.
    /// </summary>
    public partial class MultiplayerMenu
    {
        private sealed class LocalRow
        {
            /// <summary>Non-null for a section header.</summary>
            public string? Header { get; init; }
            public Color HeaderColor { get; init; }
            public Color? HeaderSquare { get; init; }
            /// <summary>The level (for level rows).</summary>
            public RoomLevel? Level { get; init; }
            /// <summary>Index into _availableLevels when you have the file, else -1.</summary>
            public int OwnIndex { get; init; } = -1;
            public string Key { get; init; } = "";
            public bool IsLevel => Header == null;
        }

        private List<LocalRow> _localRows = new();
        /// <summary>Rows that fit in the list (from the last draw; used for scrolling).</summary>
        private int _visibleRows = 6;
        private readonly List<(Rectangle Rect, string Name)> _holderSquares = new();
        private int _localRowsVersion = -1;

        private int LocalLevelCount => _localRows.Count;
        private int LocalLevelTotal => _localRows.Count(r => r.IsLevel);
        private LocalRow? RowAt(int index) => index >= 0 && index < _localRows.Count ? _localRows[index] : null;
        private int LobbyRowCount => _selectedSource == LevelSource.Official ? _officialLevels.Count : _localRows.Count;

        private bool IsLobbyRowSelectable(int index) =>
            _selectedSource == LevelSource.Official
                ? index >= 0 && index < _officialLevels.Count
                : RowAt(index)?.IsLevel == true;

        /// <summary>Your own level file at a LOCAL row (relative path), or null.</summary>
        private string? OwnRelAt(int index)
        {
            var row = RowAt(index);
            return row != null && row.OwnIndex >= 0 && row.OwnIndex < _availableLevels.Count ? _availableLevels[row.OwnIndex] : null;
        }

        private string? SelectedOwnRel => _selectedSource == LevelSource.Local ? OwnRelAt(_levelSelection) : null;

        private int FirstSelectableLobbyRow()
        {
            int count = LobbyRowCount;
            for (int i = 0; i < count; i++)
                if (IsLobbyRowSelectable(i)) return i;
            return 0;
        }

        /// <summary>Up/Down/wheel: moves to the next selectable row (wrapping), skipping headers.</summary>
        private void MoveLobbySelection(int delta)
        {
            int count = LobbyRowCount;
            if (count == 0) return;
            int i = Math.Clamp(_levelSelection, 0, count - 1);
            for (int n = 0; n < count; n++)
            {
                i = (i + delta + count) % count;
                if (IsLobbyRowSelectable(i))
                {
                    _levelSelection = i;
                    return;
                }
            }
        }

        private int SelectedLevelOrdinal()
        {
            if (_selectedSource == LevelSource.Official) return _levelSelection + 1;
            int n = 0;
            for (int i = 0; i <= _levelSelection && i < _localRows.Count; i++)
                if (_localRows[i].IsLevel) n++;
            return n;
        }

        /// <summary>Rebuilds the LOCAL rows from the room's level list, keeping the selection.</summary>
        private void RebuildLocalRows()
        {
            string? selectedKey = _selectedSource == LevelSource.Local ? RowAt(_levelSelection)?.Key : null;
            var rows = new List<LocalRow>();
            var st = Room.State;
            _localRowsVersion = Room.Version;

            if (st?.Levels != null)
            {
                var pickable = st.Levels.Where(l => l.Pickable).OrderBy(l => l.Name, StringComparer.OrdinalIgnoreCase).ToList();
                var mine = pickable.Where(l => l.YouHave).ToList();
                if (mine.Count > 0)
                {
                    rows.Add(new LocalRow { Header = $"YOUR LEVELS  ({mine.Count})", HeaderColor = ACCENT_SECONDARY });
                    foreach (var l in mine)
                        rows.Add(new LocalRow { Level = l, OwnIndex = OwnIndexOf(l.Hash), Key = "lvl:" + l.Hash });
                }
                foreach (var member in st.Members.Where(m => m.Id != st.YouId))
                {
                    var theirs = pickable.Where(l => !l.YouHave && l.Holders.Contains(member.Id)).ToList();
                    if (theirs.Count == 0) continue;
                    Color c = GetColorFromHue(member.ColorHue);
                    rows.Add(new LocalRow { Header = $"FROM {member.Username}  ({theirs.Count})", HeaderColor = c, HeaderSquare = c });
                    foreach (var l in theirs)
                        rows.Add(new LocalRow { Level = l, Key = $"from:{member.Id}:{l.Hash}" });
                }
            }

            _localRows = rows;
            if (_selectedSource != LevelSource.Local) return;
            int idx = selectedKey == null ? -1 : rows.FindIndex(r => r.Key == selectedKey);
            if (idx >= 0) _levelSelection = idx;
            ClampLevelSelection();
            AdjustLobbyScroll();
        }

        private int OwnIndexOf(string hash)
        {
            var local = Room.LocalByHash(hash);
            if (local == null) return -1;
            return _availableLevels.FindIndex(r => string.Equals(r, local.RelativePath, StringComparison.OrdinalIgnoreCase));
        }

        /// <summary>Squares for the players who have a level (you first).</summary>
        private List<(Color Color, string Name)> HoldersFor(RoomLevel level)
        {
            var list = new List<(Color, string)>();
            var st = Room.State;
            if (level.YouHave)
            {
                var me = MultiplayerSettings.Instance;
                list.Add((GetColorFromHue(me.PlayerColorHue), $"{me.Username} (you)"));
            }
            foreach (var id in level.Holders)
            {
                var m = st?.Members.FirstOrDefault(x => x.Id == id);
                if (m != null) list.Add((GetColorFromHue(m.ColorHue), m.Username));
            }
            return list;
        }

        private int HolderSquaresWidth(int count, float uiScale) =>
            count == 0 ? 0 : count * Px(12, uiScale, 9) + (count - 1) * Px(4, uiScale, 3);

        /// <summary>Draws holder squares ending at <paramref name="rightX"/>; records them for the tooltip.</summary>
        private void DrawHolderSquares(SpriteBatch sb, Texture2D pixel, float uiScale, int rightX, int centerY, List<(Color Color, string Name)> holders)
        {
            int size = Px(12, uiScale, 9);
            int gap = Px(4, uiScale, 3);
            int x = rightX - HolderSquaresWidth(holders.Count, uiScale);
            foreach (var (color, name) in holders)
            {
                var r = new Rectangle(x, centerY - size / 2, size, size);
                if (r.Contains(_lastMousePos))
                    sb.Draw(pixel, new Rectangle(r.X - 2, r.Y - 2, r.Width + 4, r.Height + 4), Color.White * (0.9f * _fadeAlpha));
                sb.Draw(pixel, r, color * _fadeAlpha);
                DrawBorder(sb, pixel, r.X, r.Y, r.Width, r.Height, Color.Black * (0.5f * _fadeAlpha), 1);
                _holderSquares.Add((r, name));
                x += size + gap;
            }
        }

        /// <summary>Username tooltip for the hovered square (drawn last, on top).</summary>
        private void DrawHolderTooltip(SpriteBatch sb, SpriteFont font, Texture2D pixel, float uiScale, int sw, int sh)
        {
            foreach (var (rect, name) in _holderSquares)
            {
                if (!rect.Contains(_lastMousePos)) continue;
                int f = FontPx(13, uiScale, 10, 18);
                Vector2 sz = MeasureText(font, name, f);
                int padX = Px(10, uiScale, 6);
                int padY = Px(6, uiScale, 4);
                int w = (int)MathF.Ceiling(sz.X) + padX * 2;
                int h = (int)MathF.Ceiling(sz.Y) + padY * 2;
                int x = ClampUi(rect.Center.X - w / 2, 4, sw - w - 4);
                int y = rect.Y - h - Px(6, uiScale, 4);
                if (y < 4) y = rect.Bottom + Px(6, uiScale, 4);
                DrawPanel(sb, pixel, x, y, w, h, BG_DARK, BORDER_SUBTLE, 1);
                DrawText(sb, font, name, new Vector2(x + padX, y + padY), TEXT_PRIMARY, f);
                return;
            }
        }

        private void DrawLocalSectionHeader(SpriteBatch sb, SpriteFont font, Texture2D pixel, float uiScale, LocalRow row, int x, int y, int w, int h)
        {
            int f = FontPx(13, uiScale, 10, 18);
            int textX = x + Px(4, uiScale);
            Vector2 sz = MeasureText(font, row.Header!, f);
            float textY = y + h - sz.Y - Px(4, uiScale, 2);
            if (row.HeaderSquare is { } sq)
            {
                int size = Px(12, uiScale, 9);
                sb.Draw(pixel, new Rectangle(textX, (int)(textY + (sz.Y - size) / 2f), size, size), sq * _fadeAlpha);
                textX += size + Px(8, uiScale, 5);
            }
            string text = TruncateToWidth(font, row.Header!, w - (textX - x) - Px(8, uiScale), f);
            DrawText(sb, font, text, new Vector2(textX, textY), row.HeaderColor * _fadeAlpha, f, shadow: true);
            int lineX = textX + (int)MathF.Ceiling(MeasureText(font, text, f).X) + Px(10, uiScale);
            int lineY = (int)(textY + sz.Y / 2f);
            if (lineX < x + w)
                sb.Draw(pixel, new Rectangle(lineX, lineY, x + w - lineX, 1), row.HeaderColor * (0.35f * _fadeAlpha));
        }
    }
}
