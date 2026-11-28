using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using FunkyMakerPlayer.UI;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using Microsoft.Xna.Framework.Input;

namespace FunkyMakerPlayer;

/// <summary>
/// Fork: Freeplay level browsing. The scanned levels (<see cref="_allPaths"/>) are filtered by the
/// search text and the favourites filter and sorted into the visible list (<see cref="_paths"/>, which
/// every other part of the menu indexes). Also: the level options menu (<see cref="OptionsMenu"/>:
/// right click / "..."), the modifiers window (<see cref="ModifiersWindow"/>), the attempts
/// (the personal best card: the results screen, <see cref="SoloResultsScreen"/>, in browse mode),
/// random pick, and the short confirm animation before a level loads.
/// </summary>
public partial class FreeplayMenu
{
    private enum SortMode { Name, Stars, Recent, Plays, Added }
    private static readonly string[] SortLabels = { "A-Z", "STARS", "RECENT", "MOST PLAYED", "NEWEST" };

    private readonly List<string> _allPaths = new();
    private readonly List<string> _allNames = new();
    private readonly Dictionary<string, DateTime> _addedUtc = new();
    private bool _restoredLastSelection;

    // Search
    private string _search = "";
    private bool _searchTyping;
    private float _searchBackHold, _searchBackRepeat;
    private volatile string? _androidSearchResult;

    // Order / filter
    private SortMode _sort = SortMode.Name;
    private bool _favoritesOnly;
    private int _seenInfoGeneration;
    private float _resortTimer;

    // Toolbar (search box, sort chip, favourites chip, random)
    private Rectangle _searchBoxBounds, _sortChipBounds, _favChipBounds, _randomBtnBounds;
    private bool _searchBoxHover, _sortChipHover, _favChipHover, _randomBtnHover;

    // Per-row "..." button
    private readonly List<(int Index, Rectangle Bounds)> _levelMoreBounds = new();
    private int _hoveredMoreIndex = -1;

    // Shared UI pieces (fork): "..." options menu, modifiers window, attempts (results screen)
    private UiDraw _ui = null!;
    private OptionsMenu _options = null!;
    private ModifiersWindow _modWindow = null!;
    private SoloResultsScreen? _attemptsView;
    private string _attemptsPath = "";
    private bool _attemptsEnemy;
    private bool _pointerDown; // left button / a finger held this frame (the attempts row drags with it)
    private int _optionsIndex = -1;

    // Details panel hot areas (modifiers bar, personal best card)
    private Rectangle _modBarBounds, _bestCardBounds;
    private bool _modBarHover, _bestCardHover;

    /// <summary>Replay file to watch, picked in the attempts window (Game1 starts it and clears this).</summary>
    public string? ReplayRequested { get; set; }

    // Confirm animation (the chosen row flashes, the preview fades, then LevelChosen)
    private const float ConfirmDuration = 0.45f;
    private bool _confirming;
    private float _confirmTimer;
    private string _confirmPath = "";

    // Visuals
    private float _selVisual;       // smoothed selection, for the curved list
    private float _panelAge;        // seconds since the panel opened (row entry animation)
    private float _time;
    private Color _bgTint = new(20, 18, 28);
    private float _favPulse;
    private string? _pendingToast;

    /// <summary>True while the Freeplay search box takes typed text (global letter hotkeys must wait).</summary>
    public bool IsTypingSearch => _searchTyping;

    /// <summary>Creates the shared UI pieces (constructor).</summary>
    private void InitBrowseUi(Texture2D pixel, SpriteFont? font, FontRenderer? fontRenderer)
    {
        _ui = new UiDraw(pixel, font, fontRenderer);
        _options = new OptionsMenu(_ui);
        _modWindow = new ModifiersWindow(_ui);
        PlayModifiers.HasEnemyChart = path => GetInfo(path!) is not { StrumlineCount: < 2 };
        PlayModifiers.Changed += () =>
        {
            QueueRatedStars();
            if (_sort == SortMode.Stars) RebuildView(SelectedPath);
        };
    }

    /// <summary>True while a modal window of the menu (modifiers, attempts) is open.</summary>
    private bool WindowOpen => _modWindow.IsOpen || _attemptsView != null;

    private void LoadBrowseState()
    {
        var data = FreeplayData.Instance;
        _sort = Enum.TryParse(data.Sort, out SortMode m) ? m : SortMode.Name;
        _favoritesOnly = data.FavoritesOnly;
        _tab = data.Tab == "osu" ? FreeplayTab.Osu : FreeplayTab.Fnf;
    }

    private void SaveBrowseState()
    {
        var data = FreeplayData.Instance;
        data.Sort = _sort.ToString();
        data.FavoritesOnly = _favoritesOnly;
        data.Save();
    }

    /// <summary>Shows a toast the next time the menu updates (e.g. a new best set during gameplay).</summary>
    public void ShowToastLater(string message) => _pendingToast = message;

    // ── View (filter + sort) ────────────────────────────────────────────────

    private static string Fold(string s)
    {
        var sb = new StringBuilder(s.Length);
        foreach (char c in s.ToLowerInvariant().Normalize(NormalizationForm.FormD))
            if (CharUnicodeInfo.GetUnicodeCategory(c) != UnicodeCategory.NonSpacingMark)
                sb.Append(c);
        return sb.ToString();
    }

    private string? SelectedPath => _selected >= 0 && _selected < _paths.Count ? _paths[_selected] : null;

    /// <summary>Rebuilds the visible list, keeping <paramref name="keepPath"/> selected when it's still listed.</summary>
    private void RebuildView(string? keepPath)
    {
        var data = FreeplayData.Instance;
        string q = Fold(_search.Trim());
        var rows = new List<int>();
        for (int i = 0; i < _allPaths.Count; i++)
        {
            if (q.Length > 0 && !Fold(_allNames[i]).Contains(q)) continue;
            if (_favoritesOnly && !data.IsFavorite(_allPaths[i])) continue;
            rows.Add(i);
        }

        IEnumerable<int> ordered = _sort switch
        {
            SortMode.Stars => rows.OrderBy(i => StarsOf(_allPaths[i]) ?? double.MaxValue),
            SortMode.Recent => rows.OrderByDescending(i => data.Get(_allPaths[i])?.LastPlayedUtc ?? DateTime.MinValue),
            SortMode.Plays => rows.OrderByDescending(i => data.Get(_allPaths[i])?.Plays ?? 0),
            SortMode.Added => rows.OrderByDescending(i => _addedUtc.TryGetValue(_allPaths[i], out var t) ? t : DateTime.MinValue),
            _ => rows,
        };
        if (_sort != SortMode.Name)
            ordered = ((IOrderedEnumerable<int>)ordered).ThenBy(i => _allNames[i], StringComparer.OrdinalIgnoreCase);
        ordered = GroupOnline(ordered); // online: shared / yours / each player's (fork)

        _paths.Clear();
        _names.Clear();
        foreach (int i in ordered)
        {
            _paths.Add(_allPaths[i]);
            _names.Add(_allNames[i]);
        }
        RebuildGroupHeaders();

        int found = keepPath != null ? _paths.IndexOf(keepPath) : -1;
        _selected = found >= 0 ? found : Math.Clamp(_selected, 0, Math.Max(0, _paths.Count - 1));
        if (found < 0) _selVisual = _selected;
        _manualScroll = false;
        _options.Close();
        _seenInfoGeneration = _infoGeneration;
    }

    // ── Per-frame browse logic ──────────────────────────────────────────────

    /// <summary>Timers and deferred work; runs every menu update, before input.</summary>
    private void UpdateBrowseTimers(float dt)
    {
        _time += dt;
        _panelAge = _panelOpen ? _panelAge + dt : 0f;
        _favPulse = Math.Max(0f, _favPulse - dt * 3f);
        _selVisual = MathHelper.Lerp(_selVisual, _selected, Math.Min(1f, dt * 14f));
        if (Math.Abs(_selVisual - _selected) < 0.001f) _selVisual = _selected;

        if (_pendingToast != null)
        {
            ShowToast(_pendingToast);
            _pendingToast = null;
        }

        if (_androidSearchResult is { } typed)
        {
            _androidSearchResult = null;
            SetSearch(typed);
        }

        // Ratings came in: the stars order may have changed (at most twice a second).
        _resortTimer -= dt;
        if (_sort == SortMode.Stars && _infoGeneration != _seenInfoGeneration && _resortTimer <= 0f)
        {
            _resortTimer = 0.5f;
            RebuildView(SelectedPath);
        }

        // Background tint follows the selected level's difficulty.
        Color target = new(20, 18, 28);
        if (_panelOpen && SelectedPath is { } sp && StarsOf(sp) is double st)
            target = Color.Lerp(new Color(20, 18, 28), StarAccent(st), 0.09f);
        _bgTint = Color.Lerp(_bgTint, target, Math.Min(1f, dt * 4f));

        if (_confirming)
        {
            _confirmTimer += dt;
            if (_confirmTimer >= ConfirmDuration)
            {
                _confirming = false;
                LevelPath = _confirmPath;
                LevelChosen = true;
                _levelChosenByConfirm = true;
            }
        }
    }

    private bool _levelChosenByConfirm;

    /// <summary>Plays a level: short flash on its row, then <see cref="LevelChosen"/>. Online: picks it.</summary>
    private void BeginConfirm(int index)
    {
        if (_online != null) { PickOnline(index); return; }
        if (index < 0 || index >= _paths.Count || _confirming) return;
        _selected = index;
        _confirmPath = _paths[index];
        _confirming = true;
        _confirmTimer = 0f;
        _options.Close();
        StopSearchTyping();
        UI.UiSfx.Play(UI.UiSound.StartSong);
    }

    // ── Search box ───────────────────────────────────────────────────────────

    private void StartSearchTyping()
    {
#if ANDROID
        if (KeyboardInput.IsVisible) return;
        TextEntry.PromptAsync("Search levels", "", _search, false).ContinueWith(t =>
        {
            if (t.IsCompletedSuccessfully && t.Result != null) _androidSearchResult = t.Result;
        });
#else
        _searchTyping = true;
        TextEntry.Clear();
        TextEntry.Capturing = true;
#endif
        _options.Close();
    }

    private void StopSearchTyping()
    {
        if (!_searchTyping) return;
        _searchTyping = false;
        TextEntry.Capturing = false;
        TextEntry.Clear();
    }

    private void SetSearch(string text)
    {
        text = text.Length > 48 ? text[..48] : text;
        if (text == _search) return;
        string? keep = SelectedPath;
        _search = text;
        RebuildView(keep);
        _targetScrollY = 0f;
    }

    /// <summary>Typing into the search box. Returns true when it used the frame's keyboard input.</summary>
    private bool UpdateSearchTyping(float dt, KeyboardState kb)
    {
        if (!_searchTyping) return false;

        string typed = TextEntry.Drain();
        string text = _search;
        if (typed.Length > 0) text += typed;

        if (kb.IsKeyDown(Keys.Back))
        {
            bool fresh = !_prevKb.IsKeyDown(Keys.Back);
            if (fresh) { _searchBackHold = 0f; _searchBackRepeat = 0f; }
            else _searchBackHold += dt;
            bool ctrl = kb.IsKeyDown(Keys.LeftControl) || kb.IsKeyDown(Keys.RightControl);
            if (fresh && ctrl) text = "";
            else if (fresh || (_searchBackHold >= 0.4f && (_searchBackRepeat += dt) >= 0.04f))
            {
                _searchBackRepeat = 0f;
                if (text.Length > 0) text = text[..^1];
            }
        }
        SetSearch(text);

        bool Pressed(Keys k) => kb.IsKeyDown(k) && !_prevKb.IsKeyDown(k);
        if (Pressed(Keys.Enter) || Pressed(Keys.Escape))
        {
            StopSearchTyping();
            return true;
        }
        // Up / Down still move through the results while typing.
        if (Pressed(Keys.Up) && _paths.Count > 0) { _selected = (_selected - 1 + _paths.Count) % _paths.Count; _manualScroll = false; }
        if (Pressed(Keys.Down) && _paths.Count > 0) { _selected = (_selected + 1) % _paths.Count; _manualScroll = false; }
        return true;
    }

    // ── Modifiers ───────────────────────────────────────────────────────────

    private static bool EnemyAvailable(string? path) => path != null && GetInfo(path) is { StrumlineCount: >= 2 };

    /// <summary>Left / Right in the list: song rate in 0.05 steps (Slower below 1x, Faster above).</summary>
    private void ChangeRate(float delta)
    {
        var data = FreeplayData.Instance;
        float old = data.SongRate;
        data.SongRate = FreeplayData.ClampRate(old + delta);
        if (data.SongRate == old) { UI.UiSfx.Play(UI.UiSound.SliderEnd); return; }
        if (data.SongRate < 0.999f) data.SlowRate = data.SongRate;
        if (data.SongRate > 1.001f) data.FastRate = data.SongRate;
        UI.UiSfx.Play(delta > 0 ? UI.UiSound.SliderUp : UI.UiSound.SliderDown);
        data.Save();
        QueueRatedStars();
    }

    private void OpenModifiers() => _modWindow.Open(SelectedPath, LayoutKeyboard.GetState());

    private void OpenAttempts()
    {
        if (SelectedPath is not { } path) return;
        int side = SideFor(GetInfo(path));
        OpenAttemptsView(path, _names[_selected], side == 1, null, skipIntro: false);
    }

    /// <summary>
    /// The attempts of a level and side on the results screen (browse mode): the personal best in the
    /// middle (the best finished run when nothing is ranked yet), the others around it.
    /// </summary>
    private void OpenAttemptsView(string path, string name, bool enemy, FreeplayAttempt? focus, bool skipIntro)
    {
        _attemptsView = null;
        var attempts = FreeplayData.Instance.Get(path)?.Attempts.Where(a => a.Enemy == enemy).ToList() ?? new();
        if (attempts.Count == 0 || _font == null)
        {
            if (!skipIntro) { UiSfx.Play(UiSound.Error); ShowToast("Todavia no hay intentos en esta cancion"); }
            return;
        }
        var anchor = FreeplayData.Instance.Get(path)?.Best(enemy)
                     ?? attempts.OrderBy(a => a.Failed).ThenByDescending(a => a.Score).First();

        var run = new SoloResultsScreen.Run { Browse = true, Current = anchor, SongName = name };
        foreach (var a in attempts) if (a != anchor) run.Others.Add(a);
        if (GetInfo(path)?.Sides[enemy ? 1 : 0] is { } si)
        {
            run.Stars = si.Stars; // at 1.00x (the best is a 1.00x run)
            run.ChartNotes = si.Notes.Count;
        }
        run.Difficulty = enemy ? "Enemy side" : "Player side";
        if (Osu.OsuLibrary.IsOsuChart(path))
        {
            try
            {
                var map = Osu.OsuBeatmap.ParseFile(path, headerOnly: true);
                if (map.SongName.Length > 0) run.SongName = map.SongName;
                run.Mapper = map.Creator;
                run.Difficulty = map.Version.Length > 0 ? map.Version : "osu!mania";
            }
            catch (Exception ex) { Console.WriteLine($"[Freeplay] Could not read the beatmap header: {ex.Message}"); }
        }

        _attemptsPath = path;
        _attemptsEnemy = enemy;
        _attemptsView = new SoloResultsScreen(_pixel, run, focus, skipIntro);
    }

    /// <summary>Attempts screen input (it takes all input while open).</summary>
    private void UpdateAttemptsView(float dt, KeyboardState kb, MouseState mouse, Point rtMouse, int wheelDelta)
    {
        var view = _attemptsView!;
        float wheel = wheelDelta / 120f - (mouse.HorizontalScrollWheelValue - _prevMs.HorizontalScrollWheelValue) / 120f;
        switch (view.Update(dt, kb, _prevKb, rtMouse, _pointerDown, wheel))
        {
            case SoloResultsScreen.Choice.Continue:
                _attemptsView = null;
                break;
            case SoloResultsScreen.Choice.WatchReplay:
                ReplayRequested = view.ReplayPath;
                _attemptsView = null;
                break;
            case SoloResultsScreen.Choice.Delete:
                var gone = view.FocusedAttempt;
                var next = view.FocusNeighbor;
                FreeplayData.Instance.DeleteAttempt(_attemptsPath, gone);
                ShowToast("Intento borrado (su replay se conserva)");
                OpenAttemptsView(_attemptsPath, _names.Count > _selected && _selected >= 0 ? _names[_selected] : "",
                    _attemptsEnemy, next, skipIntro: true); // closes when it was the last one
                break;
        }
    }

    // ── Level options menu ("...") ──────────────────────────────────────────

    private void OpenLevelOptions(int index, Point anchor, string? confirmFirst = null)
    {
        if (index < 0 || index >= _paths.Count) return;
        _selected = index;
        _optionsIndex = index;
        StopSearchTyping();
        string path = _paths[index];
        var rec = FreeplayData.Instance.Get(path);
        bool enemy = SideFor(GetInfo(path)) == 1;
        int attempts = rec?.AttemptCount(enemy) ?? 0;
        var items = new List<OptionsMenu.Item>();
        if (_online != null)
        {
            // Online song select: pick it, favourite it, and your own files can be shown / deleted.
            items.Add(new("play", "PICK THIS SONG"));
            items.Add(new("favorite", FreeplayData.Instance.IsFavorite(path) ? "REMOVE FAVORITE" : "ADD TO FAVORITES"));
#if !ANDROID
            if (OnlineSongOf(path)?.Yours == true) items.Add(new("folder", "SHOW IN FOLDER"));
#endif
            if (CanDelete(path)) items.Add(new("delete", OsuTab ? "DELETE SONG" : "DELETE", Danger: true));
            _options.Open(Safe(_names[index]), items, anchor, LayoutKeyboard.GetState(), confirmFirst);
            return;
        }
        items.Add(new("play", "PLAY"));
        items.Add(new("attempts", attempts > 0 ? $"ATTEMPTS ({attempts})" : "ATTEMPTS"));
        items.Add(new("edit", OsuTab ? "EDIT CHART (osu!: no)" : "EDIT CHART", Enabled: !OsuTab));
        items.Add(new("favorite", FreeplayData.Instance.IsFavorite(path) ? "REMOVE FAVORITE" : "ADD TO FAVORITES"));
#if !ANDROID
        items.Add(new("folder", "SHOW IN FOLDER"));
#endif
        items.Add(new("delete", OsuTab ? "DELETE SONG" : "DELETE", Danger: true));
        _options.Open(Safe(_names[index]), items, anchor, LayoutKeyboard.GetState(), confirmFirst);
    }

    private void RunLevelOption(string id)
    {
        int index = _optionsIndex;
        if (index < 0 || index >= _paths.Count) return;
        string path = _paths[index];
        switch (id)
        {
            case "play": BeginConfirm(index); break;
            case "attempts": _selected = index; OpenAttempts(); break;
            case "edit":
                _selected = index;
                LevelPath = path;
                ChartEditorRequested = true;
                break;
            case "favorite": ToggleFavorite(index); break;
            case "folder":
#if !ANDROID
                if (!PlatformShare.Reveal(path)) ShowToast("No se pudo abrir la carpeta");
#endif
                break;
            case "delete": OpenDeleteModal(index); break; // the delete modal is the confirmation
        }
    }

    // ── Actions ─────────────────────────────────────────────────────────────

    private void ToggleFavorite(int index)
    {
        if (index < 0 || index >= _paths.Count) return;
        string path = _paths[index];
        bool fav = FreeplayData.Instance.ToggleFavorite(path);
        _favPulse = 1f;
        ShowToast(fav ? $"{_names[index]}: favorito" : $"{_names[index]}: ya no es favorito");
        if (_favoritesOnly && !fav)
        {
            string? neighbour = index + 1 < _paths.Count ? _paths[index + 1] : index > 0 ? _paths[index - 1] : null;
            RebuildView(neighbour);
        }
    }

    private void SelectRandom()
    {
        if (_paths.Count < 2) return;
        int pick;
        do pick = Random.Shared.Next(_paths.Count); while (pick == _selected);
        _selected = pick;
        _manualScroll = false;
    }

    private void CycleSort()
    {
        _sort = (SortMode)(((int)_sort + 1) % SortLabels.Length);
        RebuildView(SelectedPath);
        SaveBrowseState();
        ShowToast($"Orden: {SortLabels[(int)_sort]}");
    }

    private void ToggleFavoritesOnly()
    {
        _favoritesOnly = !_favoritesOnly;
        RebuildView(SelectedPath);
        SaveBrowseState();
        if (_favoritesOnly && _paths.Count == 0) ShowToast("No tienes favoritos (pulsa F en un nivel)");
        else ShowToast(_favoritesOnly ? "Solo favoritos" : "Todos los niveles");
    }

    /// <summary>
    /// Online: the level is your own file (official levels and other players' songs can't be deleted
    /// here). Freeplay: always.
    /// </summary>
    private bool CanDelete(string? path) => _online == null || OnlineSongOf(path)?.Yours == true;

    /// <summary>R / REFRESH: rescans (online: the room's lists, official levels and your files too).</summary>
    private void RefreshFromPanel()
    {
        if (_online != null)
        {
            _online.Refresh();
            Refresh();
            ShowToast("Lista actualizada");
            return;
        }
        Refresh();
        ShowToast("Lista de niveles actualizada");
    }

    /// <summary>I / IMPORT: the file picker (online: imported songs are shared with the room).</summary>
    private void ImportFromPanel()
    {
        if (_online != null) _online.Import();
        else TriggerFileImport();
    }

    private void ClosePanel()
    {
        if (_online != null)
        {
            // Online song select: back to the waiting room (the list stays as it is).
            StopSearchTyping();
            _options.Close();
            _online.Back();
            return;
        }
        FreeplayData.Instance.SetLastSelected(SelectedPath);
        StopSearchTyping();
        _options.Close();
        _inPanel = false;
        _panelOpen = false;
    }

    // ── Panel input (replaces the old per-block handling in UpdateCore) ──────

    /// <summary>Hover state of the panel's controls, rows and the details panel.</summary>
    private void UpdatePanelHover(Point rtMouse)
    {
        _importBtnHover     = _importBtnBounds.Contains(rtMouse);
        _newBtnHover        = _newBtnBounds.Contains(rtMouse);
        _refreshBtnHover    = _refreshBtnBounds.Contains(rtMouse);
        _closePanelBtnHover = _closePanelBtnBounds.Contains(rtMouse);
        _footerBarHover     = _footerBarBounds.Contains(rtMouse);
        _searchBoxHover     = _searchBoxBounds.Contains(rtMouse);
        _sortChipHover      = _sortChipBounds.Contains(rtMouse);
        _favChipHover       = _favChipBounds.Contains(rtMouse);
        _randomBtnHover     = _randomBtnBounds.Contains(rtMouse);
        _hoveredMoreIndex = -1;

        bool modal = WindowOpen || _options.IsOpen;
        _tabHover = null;
        if (!modal)
            foreach (var (tab, bounds) in _tabBounds)
                if (bounds.Contains(rtMouse)) _tabHover = tab;
        _diffPrevHover = !modal && _diffPrevBounds.Contains(rtMouse);
        _diffNextHover = !modal && _diffNextBounds.Contains(rtMouse);
        // Online: no modifiers / attempts (the cards there only show room info).
        _modBarHover = !modal && _online == null && _modBarBounds.Contains(rtMouse);
        _bestCardHover = !modal && _online == null && _bestCardBounds.Contains(rtMouse);

        bool overToolbar = _importBtnHover || _newBtnHover || _refreshBtnHover || _closePanelBtnHover ||
                           _footerBarHover || _searchBoxHover || _sortChipHover || _favChipHover || _randomBtnHover ||
                           _tabHover != null;

        if (_allPaths.Count == 0)
        {
            if (_online != null) { _folderIconHover = false; return; } // no import drop zone online
            var dropBounds = GetDropBoxBounds(_lastScreenWidth, _lastScreenHeight);
            _folderIconHover = !overToolbar && dropBounds.Contains(rtMouse);
            return;
        }
        _folderIconHover = false;
        if (overToolbar || modal || !_levelListBounds.Contains(rtMouse)) return;

        foreach (var b in _levelMoreBounds)
            if (b.Bounds.Contains(rtMouse)) { _hoveredMoreIndex = b.Index; _hoveredLevelIndex = b.Index; return; }
        foreach (var item in _levelItemBounds)
            if (item.Bounds.Contains(rtMouse)) { _hoveredLevelIndex = item.Index; return; }
    }

    /// <summary>
    /// All input while the level panel is open. Returns true when the frame's input was consumed
    /// (the caller then stores the previous input state and returns).
    /// </summary>
    private bool UpdatePanelInput(float dt, KeyboardState kb, MouseState mouse, Point rtMouse,
        bool mouseClicked, bool mouseMoved, int wheelDelta, int screenWidth, int screenHeight)
    {
        bool rightClicked = mouse.RightButton == ButtonState.Pressed && _prevMs.RightButton == ButtonState.Released;
        int itemH = GetItemHeight(screenWidth, screenHeight);

        // Modal windows take all input while open.
        if (_modWindow.IsOpen)
        {
            _modWindow.Update(dt, kb, rtMouse, mouseClicked, mouseMoved);
            UpdateListScroll(dt, itemH);
            return true;
        }
        if (_attemptsView != null)
        {
            UpdateAttemptsView(dt, kb, mouse, rtMouse, wheelDelta);
            UpdateListScroll(dt, itemH);
            return true;
        }

        // Wheel scrolls the list (it closes the options menu)
        if (wheelDelta != 0 && _paths.Count > 0 && _panelBounds.Contains(rtMouse))
        {
            float maxScroll = Math.Max(0f, ListContentHeight(itemH) - _lastListHeight);
            _manualScroll = true;
            _targetScrollY = Math.Clamp(_targetScrollY - (wheelDelta / 120f) * itemH * 1.5f, 0f, maxScroll);
            _options.Close();
        }

        bool consumed = _options.Update(kb, rtMouse, mouseClicked, rightClicked, mouseMoved, out string? chosen);
        if (chosen != null) RunLevelOption(chosen);
        if (!consumed && UpdateSearchTyping(dt, kb))
        {
            // Clicking outside the search box stops typing; the click still reaches what was clicked.
            if (mouseClicked && !_searchBoxHover) StopSearchTyping();
            else consumed = true;
        }

        if (!consumed && (mouseClicked || rightClicked) && HandlePanelClick(rtMouse, mouseClicked, rightClicked))
            return true;
        if (consumed)
        {
            UpdateListScroll(dt, itemH);
            return false; // let the caller store input state normally
        }

        // Hover selects, without re-centering the list (it would slide away under the pointer).
        // Not on Android: a finger landing to drag the list would pick the row under it (a tap
        // selects there instead, see HandlePanelClick).
        if (mouseMoved && !OperatingSystem.IsAndroid() && _hoveredLevelIndex >= 0 && _hoveredLevelIndex < _paths.Count && _hoveredLevelIndex != _selected)
        {
            _selected = _hoveredLevelIndex;
            _manualScroll = true;
        }

        bool Pressed(Keys k) => kb.IsKeyDown(k) && !_prevKb.IsKeyDown(k);
        bool up = Pressed(Keys.Up), down = Pressed(Keys.Down), enter = Pressed(Keys.Enter), esc = Pressed(Keys.Escape);
        bool pageUp = Pressed(Keys.PageUp), pageDown = Pressed(Keys.PageDown), home = Pressed(Keys.Home), end = Pressed(Keys.End);

        // Everything else in the panel (tabs, search, sort, favorites, random, import, new, refresh,
        // the "..." options, modifiers, attempts) is a button: no extra hotkeys (fork).

        // Left / Right: difficulty (osu! songs) or song rate (FNF levels; not online, rates are Freeplay modifiers)
        if (Pressed(Keys.Left)) { if (OsuTab) ChangeDifficulty(-1); else if (_online == null) ChangeRate(-FreeplayData.RateStep); }
        if (Pressed(Keys.Right)) { if (OsuTab) ChangeDifficulty(1); else if (_online == null) ChangeRate(FreeplayData.RateStep); }

        if (_paths.Count > 0)
        {
            int page = Math.Max(1, _lastListHeight / Math.Max(1, itemH) - 1);
            if (up) _selected = (_selected - 1 + _paths.Count) % _paths.Count;
            if (down) _selected = (_selected + 1) % _paths.Count;
            if (pageUp) _selected = Math.Max(0, _selected - page);
            if (pageDown) _selected = Math.Min(_paths.Count - 1, _selected + page);
            if (home) _selected = 0;
            if (end) _selected = _paths.Count - 1;
            if (up || down || pageUp || pageDown || home || end) _manualScroll = false;
        }

        if (enter && SelectedPath != null) BeginConfirm(_selected);

        if (esc)
        {
            if (_search.Length > 0) SetSearch("");
            else ClosePanel();
        }

        UpdateListScroll(dt, itemH);
        return false;
    }

    /// <summary>Clicks in the panel / details. Returns true when one was handled.</summary>
    private bool HandlePanelClick(Point rtMouse, bool clicked, bool rightClicked)
    {
        if (rightClicked)
        {
            if (_hoveredLevelIndex >= 0 && _hoveredLevelIndex < _paths.Count)
            {
                OpenLevelOptions(_hoveredLevelIndex, rtMouse);
                return true;
            }
            return false;
        }

        if (_tabHover is { } tab) { SwitchTab(tab); return true; }
        if (_diffPrevHover) { ChangeDifficulty(-1); return true; }
        if (_diffNextHover) { ChangeDifficulty(1); return true; }
        if (_modBarHover) { OpenModifiers(); return true; }
        if (_bestCardHover) { OpenAttempts(); return true; }
        if (_closePanelBtnHover) { ClosePanel(); return true; }
        if (_searchBoxHover) { StartSearchTyping(); return true; }
        if (_sortChipHover) { CycleSort(); return true; }
        if (_favChipHover) { ToggleFavoritesOnly(); return true; }
        if (_randomBtnHover) { SelectRandom(); return true; }
        if (_importBtnHover || (_online == null && (_footerBarHover || _folderIconHover)))
        {
            ImportFromPanel();
            _prevMs = Mouse.GetState();
            return true;
        }
        if (_newBtnHover)
        {
            if (_online != null) ClosePanel(); // online: the middle button is BACK
            else if (!OsuTab) NewChartRequested = true;
            return true;
        }
        if (_refreshBtnHover)
        {
            RefreshFromPanel();
            return true;
        }
        if (_hoveredMoreIndex >= 0 && _hoveredMoreIndex < _paths.Count)
        {
            var b = _levelMoreBounds.First(x => x.Index == _hoveredMoreIndex).Bounds;
            OpenLevelOptions(_hoveredMoreIndex, new Point(b.Right, b.Bottom));
            return true;
        }
        if (_hoveredLevelIndex >= 0 && _hoveredLevelIndex < _paths.Count)
        {
            if (OperatingSystem.IsAndroid() && _hoveredLevelIndex != _selected)
            {
                // Touch: the first tap selects the row, a tap on the selected row plays it.
                _selected = _hoveredLevelIndex;
                _manualScroll = true;
                return true;
            }
            BeginConfirm(_hoveredLevelIndex);
            return true;
        }
        return false;
    }

    private void UpdateListScroll(float dt, int itemH)
    {
        float maxScroll = Math.Max(0f, ListContentHeight(itemH) - _lastListHeight);
        if (!_manualScroll)
        {
            // Keep the selection near the middle of the list (FNF-style), clamped at the ends.
            float centre = RowTop(_selected, itemH) + itemH / 2f - _lastListHeight / 2f;
            if (StartsGroup(_selected)) centre -= GroupHeaderH(itemH) / 2f; // keep its section header in view
            _targetScrollY = centre;
        }
        _targetScrollY = Math.Clamp(_targetScrollY, 0f, maxScroll);
        _scrollY = MathHelper.Lerp(_scrollY, _targetScrollY, Math.Min(1f, dt * 12f));
    }
}
