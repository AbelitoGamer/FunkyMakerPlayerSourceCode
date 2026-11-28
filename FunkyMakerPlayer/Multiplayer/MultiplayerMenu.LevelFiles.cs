using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using Microsoft.Xna.Framework.Input;

namespace FunkyMakerPlayer.Multiplayer
{
    /// <summary>
    /// LOCAL level management in the online lobby (fork): the list follows the levels folder
    /// automatically, IMPORT adds levels without leaving the online menu, DELETE removes one.
    /// </summary>
    public partial class MultiplayerMenu
    {
        // ── Auto refresh ─────────────────────────────────────────────────────
        private FileSystemWatcher? _levelsWatcher;
        private string _watchedFolder = "";
        private int _levelsDirty;
        private float _levelsDirtyDelay;
        private int _seenModsGeneration = ModsStorage.Generation;

        // ── Import / delete ──────────────────────────────────────────────────
        private Rectangle _importButtonRect;
        private Rectangle _deleteButtonRect;
        private Rectangle _deleteYesRect;
        private Rectangle _deleteNoRect;
        private string? _deleteConfirmPath;      // full path of the level being confirmed
        private string _deleteConfirmName = "";
        private int _deleteConfirmSelection = 1; // 0 = DELETE, 1 = CANCEL (default)
        private List<string>? _pendingImported;
        private volatile bool _levelsChangedForOthers;

        /// <summary>True once after this menu imported or deleted levels (Game1 then refreshes Freeplay).</summary>
        public bool ConsumeLevelsChanged()
        {
            if (!_levelsChangedForOthers) return false;
            _levelsChangedForOthers = false;
            return true;
        }

        private bool DeleteConfirmOpen => _deleteConfirmPath != null;

        /// <summary>Per frame: watch the levels folder and refresh the LOCAL list while in the lobby.</summary>
        private void UpdateLevelFolderWatch(float dt)
        {
            var imported = Interlocked.Exchange(ref _pendingImported, null);
            if (imported != null) OnLevelsImported(imported);

            string folder = _levelsFolder;
            if (!string.Equals(folder, _watchedFolder, StringComparison.Ordinal))
                StartWatching(folder);
            if (_seenModsGeneration != ModsStorage.Generation)
            {
                _seenModsGeneration = ModsStorage.Generation;
                Interlocked.Exchange(ref _levelsDirty, 1);
            }

            if (Volatile.Read(ref _levelsDirty) == 0) return;
            // Only change the list while browsing it: during matchmaking / a match the selected
            // index decides which level is played.
            if (_state is not (MenuState.SongSelect or MenuState.WaitingRoom or MenuState.RoomBrowser) || DeleteConfirmOpen) return;
            _levelsDirtyDelay -= dt;
            if (_levelsDirtyDelay > 0f) return; // let a burst of file events settle
            Interlocked.Exchange(ref _levelsDirty, 0);
            RefreshLevelListKeepingSelection();
            RescanSharedLevels();
        }

        private void StartWatching(string folder)
        {
            _watchedFolder = folder;
            try { _levelsWatcher?.Dispose(); } catch { }
            _levelsWatcher = null;
            try
            {
                if (!Directory.Exists(folder)) return;
                var w = new FileSystemWatcher(folder)
                {
                    IncludeSubdirectories = true,
                    NotifyFilter = NotifyFilters.FileName | NotifyFilters.DirectoryName | NotifyFilters.LastWrite | NotifyFilters.Size,
                };
                FileSystemEventHandler changed = (_, e) => MarkLevelsDirty(e.FullPath);
                w.Created += changed;
                w.Deleted += changed;
                w.Changed += changed;
                w.Renamed += (_, e) => MarkLevelsDirty(e.FullPath);
                w.Error += (_, _) => MarkLevelsDirty(null);
                w.EnableRaisingEvents = true;
                _levelsWatcher = w;
            }
            catch (Exception ex)
            {
                // Some file systems don't support watching; entering the lobby and R still refresh.
                Console.WriteLine($"[Multiplayer] Can't watch levels folder: {ex.Message}");
            }
        }

        private void MarkLevelsDirty(string? path)
        {
            // Partial downloads (Received/.<id>.part) aren't levels; ignore them.
            if (path != null && path.EndsWith(".part", StringComparison.OrdinalIgnoreCase)) return;
            _levelsDirtyDelay = 0.4f;
            Interlocked.Exchange(ref _levelsDirty, 1);
        }

        /// <summary>Rescans mods/levels and keeps the same level selected if it still exists.</summary>
        private void RefreshLevelListKeepingSelection()
        {
            RefreshLevelList(); // RebuildLocalRows keeps the selected row by its key
            ClampLevelSelection();
            AdjustLobbyScroll();
        }

        private void StopWatchingLevels()
        {
            try { _levelsWatcher?.Dispose(); } catch { }
            _levelsWatcher = null;
            _watchedFolder = "";
        }

        // ── Import ───────────────────────────────────────────────────────────

        /// <summary>I key / IMPORT pill: pick level files and add them without leaving the online menu.</summary>
        private void TriggerLobbyImport()
        {
#if ANDROID
            _ = System.Threading.Tasks.Task.Run(async () =>
            {
                try
                {
                    var paths = await LevelFiles.PickAndImportAndroidAsync();
                    if (paths.Count > 0) Interlocked.Exchange(ref _pendingImported, paths);
                }
                catch (Exception ex) { Console.WriteLine($"[Multiplayer] Import failed: {ex.Message}"); }
            });
#else
            try
            {
                var paths = LevelFiles.PickAndImportDesktop();
                if (paths.Count > 0) OnLevelsImported(paths);
            }
            catch (Exception ex)
            {
                ShowError($"Import failed: {ex.Message}");
            }
            _inputCooldown = INPUT_COOLDOWN_TIME;
#endif
        }

        /// <summary>
        /// Levels were imported (IMPORT here, or a file dropped on the window while this menu is open):
        /// show them in the LOCAL list and select the last one. Does not switch to Freeplay.
        /// </summary>
        public void OnLevelsImported(IReadOnlyList<string> paths)
        {
            if (paths == null || paths.Count == 0) return;
            // osu! songs (fork): .osz files go to the osu! importer (the Android picker copied them
            // into the levels folder; that copy is removed).
            string? message = null;
            var levels = new List<string>();
            foreach (var p in paths)
            {
                if (!Osu.OsuLibrary.IsBeatmapArchive(p)) { levels.Add(p); continue; }
                var r = Osu.OsuLibrary.Import(p);
                message = r.Message;
                if (LevelFiles.IsInLevelsFolder(p)) { try { File.Delete(p); } catch { } }
            }
            RefreshLevelList();
            RescanSharedLevels();
            _songPicker?.Refresh();
            _levelsChangedForOthers = true;
            if (levels.Count > 0)
            {
                string name = Path.GetFileNameWithoutExtension(levels[^1]);
                ShowInfo(levels.Count == 1 ? $"{name} imported" : $"{levels.Count} levels imported", 3f);
            }
            else if (message != null)
            {
                ShowInfo(message, 3f);
            }
        }

        // ── Delete ───────────────────────────────────────────────────────────

        /// <summary>Del key / DELETE pill on your own selected LOCAL level.</summary>
        private void OpenDeleteConfirm()
        {
            if (SelectedOwnRel is not { } rel) return;
            _deleteConfirmPath = Path.GetFullPath(Path.Combine(_levelsFolder, rel));
            _deleteConfirmName = Path.GetFileNameWithoutExtension(rel);
            _deleteConfirmSelection = 1;
            _inputCooldown = INPUT_COOLDOWN_TIME;
        }

        private void UpdateDeleteConfirm(KeyboardState kb, MouseState mouse)
        {
            if (_inputCooldown > 0f) return;
            if (IsKeyPressed(kb, Keys.Left) || IsKeyPressed(kb, Keys.Right))
                _deleteConfirmSelection = 1 - _deleteConfirmSelection;
            if (_deleteYesRect.Contains(_lastMousePos)) _deleteConfirmSelection = 0;
            else if (_deleteNoRect.Contains(_lastMousePos)) _deleteConfirmSelection = 1;

            bool yes = IsRectClicked(mouse, _deleteYesRect) ||
                       (IsKeyPressed(kb, Keys.Enter) && _deleteConfirmSelection == 0);
            bool no = IsRectClicked(mouse, _deleteNoRect) || IsKeyPressed(kb, Keys.Escape) ||
                      (IsKeyPressed(kb, Keys.Enter) && _deleteConfirmSelection == 1);

            if (yes)
            {
                string path = _deleteConfirmPath!;
                string name = _deleteConfirmName;
                _deleteConfirmPath = null;
                var outcome = LevelFiles.Delete(path, out string error);
                if (outcome == LevelFiles.DeleteOutcome.Failed)
                    ShowError($"Could not delete {name}: {error}");
                else
                    ShowInfo($"{name} {LevelFiles.DescribeDelete(outcome)}", 4f);
                RefreshLevelListKeepingSelection();
                RescanSharedLevels();
                _levelsChangedForOthers = true;
                _inputCooldown = INPUT_COOLDOWN_TIME;
            }
            else if (no)
            {
                _deleteConfirmPath = null;
                _inputCooldown = INPUT_COOLDOWN_TIME;
            }
        }

        private void DrawDeleteConfirm(SpriteBatch sb, SpriteFont font, Texture2D pixel, int sw, int sh)
        {
            if (!DeleteConfirmOpen) return;
            float uiScale = GetUiScale(sw, sh);
            sb.Draw(pixel, new Rectangle(0, 0, sw, sh), Color.Black * 0.6f);

            int w = ClampUi(Px(560, uiScale), 300, sw - 32);
            int h = Px(210, uiScale, 170);
            int x = sw / 2 - w / 2;
            int y = sh / 2 - h / 2;
            DrawPanel(sb, pixel, x, y, w, h, BG_MEDIUM, ACCENT_ERROR, 2, ACCENT_ERROR);

            int titleFont = FontPx(20, uiScale, 14, 28);
            int bodyFont = FontPx(14, uiScale, 11, 20);
            DrawCenteredTextInPanel(sb, font, "DELETE LEVEL?", x, y + Px(20, uiScale), w, TEXT_PRIMARY, titleFont, shadow: true);
            string name = TruncateToWidth(font, _deleteConfirmName, w - Px(40, uiScale), bodyFont);
            DrawCenteredTextInPanel(sb, font, name, x, y + Px(62, uiScale), w, ACCENT_WARNING, bodyFont);
            string where = OperatingSystem.IsWindows() ? "It goes to the Recycle Bin." : "This can't be undone.";
#if ANDROID
            where = "This can't be undone.";
#endif
            DrawCenteredTextInPanel(sb, font, where, x, y + Px(90, uiScale), w, TEXT_SECONDARY, FontPx(13, uiScale, 10, 18));

            int btnW = Px(170, uiScale, 110);
            int btnH = Px(44, uiScale, 34);
            int gap = Px(20, uiScale, 10);
            int bx = sw / 2 - btnW - gap / 2;
            int by = y + h - btnH - Px(20, uiScale);
            _deleteYesRect = new Rectangle(bx, by, btnW, btnH);
            _deleteNoRect = new Rectangle(bx + btnW + gap, by, btnW, btnH);
            DrawConfirmButton(sb, font, pixel, uiScale, _deleteYesRect, "DELETE", _deleteConfirmSelection == 0, ACCENT_ERROR);
            DrawConfirmButton(sb, font, pixel, uiScale, _deleteNoRect, "CANCEL", _deleteConfirmSelection == 1, ACCENT_PRIMARY);
        }

        private void DrawConfirmButton(SpriteBatch sb, SpriteFont font, Texture2D pixel, float uiScale, Rectangle r, string label, bool selected, Color accent)
        {
            DrawPanel(sb, pixel, r.X, r.Y, r.Width, r.Height, selected ? BG_LIGHT : BG_DARK, selected ? accent : BORDER_SUBTLE, selected ? 2 : 1);
            int f = FontPx(15, uiScale, 11, 22);
            Vector2 sz = MeasureText(font, label, f);
            DrawText(sb, font, label, new Vector2(r.X + (r.Width - sz.X) / 2f, r.Y + (r.Height - sz.Y) / 2f), (selected ? accent : TEXT_SECONDARY) * _fadeAlpha, f, shadow: selected);
        }

        /// <summary>A small clickable pill (used for IMPORT / DELETE).</summary>
        private Rectangle DrawPillButton(SpriteBatch sb, SpriteFont font, Texture2D pixel, float uiScale, int rightX, int y, int h, string label, Color hoverColor, int fontSize)
        {
            Vector2 sz = MeasureText(font, label, fontSize);
            int w = (int)MathF.Ceiling(sz.X) + Px(18, uiScale);
            var rect = new Rectangle(rightX - w, y, w, h);
            bool hovered = rect.Contains(_lastMousePos);
            DrawPanel(sb, pixel, rect.X, rect.Y, w, h, hovered ? BG_LIGHT : BG_DARK, hovered ? hoverColor : BORDER_SUBTLE, 1);
            DrawText(sb, font, label, new Vector2(rect.X + (w - sz.X) / 2f, rect.Y + (h - sz.Y) / 2f), (hovered ? hoverColor : TEXT_SECONDARY) * _fadeAlpha, fontSize);
            return rect;
        }
    }
}
