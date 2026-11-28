using FunkyMakerPlayer.UI;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using Microsoft.Xna.Framework.Input;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace FunkyMakerPlayer.Editors.Chart;

/// <summary>
/// Level Selector &amp; New Chart Creator for Chart Editor.
/// Scans mods/levels (.fnmm, .zip, .rar, unpacked folders), supports mouse &amp; keyboard,
/// and lets creators build a brand-new .FNMM chart directly inside the engine.
/// </summary>
public class LevelSelector : FunkyMakerPlayer.UI.IDirectScreen
{
    private readonly Texture2D _pixel;
    private readonly SpriteFont? _font;
    private readonly FontRenderer? _fontRenderer;

    private List<string> _levelPaths = new();
    private List<string> _levelNames = new();
    private int _selectedIndex = 0;
    private int _hoveredIndex = -1;
    private float _scrollOffset = 0f;
    private float _targetScrollOffset = 0f;
    private int _listHeight = 460;
    private int _screenWidth = 1280;
    private int _screenHeight = 720;

    private KeyboardState _prevKb;
    private MouseState _prevMs;
    private Point _lastMousePos = new(-9999, -9999);
    private float _inputCooldown = 0.2f;

    private RasterizerState? _scissorRasterizer;
    private readonly List<(int Index, Rectangle Rect)> _itemRects = new();
    private Rectangle _newChartBtnRect;
    private Rectangle _refreshBtnRect;
    private Rectangle _backBtnRect;
    private Rectangle _listRect;

    // ── New Chart Creation Modal ─────────────────────────────────────────────
    private bool _createModalOpen = false;

    /// <summary>True while the New Chart modal (with its name field) is open; Game1 skips the -/+ volume keys then.</summary>
    public bool IsTypingName => _createModalOpen;
    private string _newLevelName = "My Custom Chart";
    private float _newLevelBpm = 120f;
    private string? _selectedOggPath = null;
    private string? _createError = null;
    private float _caretBlink = 0f;

    private Rectangle _mdlNameRect;
    private Rectangle _mdlBpmM10Rect;
    private Rectangle _mdlBpmM1Rect;
    private Rectangle _mdlBpmP1Rect;
    private Rectangle _mdlBpmP10Rect;
    private Rectangle _mdlOggBrowseRect;
    private Rectangle _mdlOggClearRect;
    private Rectangle _mdlCreateRect;
    private Rectangle _mdlCancelRect;

    public bool BackPressed { get; private set; }
    public bool LevelSelected { get; private set; }
    public string? SelectedLevelPath { get; private set; }

    private int GetItemHeight() => UiScale.Px(64, UiScale.FromScreen(_screenWidth, _screenHeight), 46);

    private static readonly Color BG_DARK = new(20, 18, 28);
    private static readonly Color BG_CARD = new(30, 28, 44);
    private static readonly Color ACCENT_PINK = new(255, 68, 136);
    private static readonly Color ACCENT_GOLD = new(255, 215, 0);
    private static readonly Color ACCENT_CYAN = new(0, 200, 255);
    private static readonly Color ACCENT_GREEN = new(46, 224, 150);

    public LevelSelector(Texture2D pixel, SpriteFont? font, FontRenderer? fontRenderer, bool startCreateModal = false)
    {
        _pixel = pixel;
        _font = font;
        _fontRenderer = fontRenderer;
        _prevKb = LayoutKeyboard.GetState();
        _prevMs = UI.PointerInput.GetState();
        _createModalOpen = startCreateModal;
        LoadLevelList();
    }

    public void RefreshLevels() => LoadLevelList();

    public void ResetInputCooldown()
    {
        _inputCooldown = 0.2f;
        BackPressed = false;
        LevelSelected = false;
        _prevKb = LayoutKeyboard.GetState();
        _prevMs = UI.PointerInput.GetState();
        _lastMousePos = _prevMs.Position;
    }

    public void SyncInputState()
    {
        _inputCooldown = Math.Max(_inputCooldown, 0.08f);
        _prevKb = LayoutKeyboard.GetState();
        _prevMs = UI.PointerInput.GetState();
        _lastMousePos = _prevMs.Position;
    }

    private void LoadLevelList()
    {
        try
        {
            string levelsDir = FreeplayMenu.GetDefaultLevelsFolder();
            if (!Directory.Exists(levelsDir))
                Directory.CreateDirectory(levelsDir);

            var files = new List<string>();
            string backupsSegment = Path.DirectorySeparatorChar + "Backups" + Path.DirectorySeparatorChar;
            foreach (var f in Directory.GetFiles(levelsDir, "*.*", SearchOption.AllDirectories))
            {
                if (f.Contains(backupsSegment, StringComparison.OrdinalIgnoreCase)) continue;
                if (FreeplayMenu.IsSupportedLevelFile(f)) files.Add(f);
            }

            foreach (var dir in Directory.GetDirectories(levelsDir))
            {
                if (Path.GetFileName(dir).Equals("Backups", StringComparison.OrdinalIgnoreCase)) continue;
                if (File.Exists(Path.Combine(dir, "meta.json")) || File.Exists(Path.Combine(dir, "data.json")))
                    files.Add(dir);
            }

            files.Sort((a, b) => string.Compare(Path.GetFileName(a), Path.GetFileName(b), StringComparison.OrdinalIgnoreCase));
            _levelPaths = files;
            _levelNames = files.Select(f => Path.GetFileNameWithoutExtension(f)).ToList();
            _selectedIndex = Math.Clamp(_selectedIndex, 0, Math.Max(0, _levelPaths.Count - 1));
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[LevelSelector] Failed to load level list: {ex.Message}");
        }
    }

    public void Update(float dt, KeyboardState kb)
    {
        Update(dt, kb, _screenWidth, _screenHeight);
    }

    public void Update(float dt, KeyboardState kb, int screenWidth, int screenHeight)
    {
        // Snapshot → update → compare for UI sounds
        var b = (_selectedIndex, _createModalOpen, _newLevelName, _newLevelBpm, _selectedOggPath, _createError);
        UpdateCore(dt, kb, screenWidth, screenHeight);

        if (!_createModalOpen && _selectedIndex != b._selectedIndex) UI.UiSfx.Play(UI.UiSound.MoveList);
        if (_createModalOpen && !b._createModalOpen) UI.UiSfx.Play(UI.UiSound.Confirm);
        if (!_createModalOpen && b._createModalOpen && !LevelSelected) UI.UiSfx.Play(UI.UiSound.Back);
        if (_createModalOpen && b._createModalOpen)
        {
            if (_newLevelName != b._newLevelName) UI.UiSfx.Play(UI.UiSound.Type);
            if (_newLevelBpm != b._newLevelBpm) UI.UiSfx.Play(_newLevelBpm > b._newLevelBpm ? UI.UiSound.SliderUp : UI.UiSound.SliderDown);
            if (_selectedOggPath != b._selectedOggPath) UI.UiSfx.Play(_selectedOggPath != null ? UI.UiSound.Confirm : UI.UiSound.Delete);
        }
        if (_createError != null && _createError != b._createError) UI.UiSfx.Play(UI.UiSound.Error);
    }

    private void UpdateCore(float dt, KeyboardState kb, int screenWidth, int screenHeight)
    {
        if (screenWidth > 0 && screenHeight > 0)
        {
            _screenWidth = screenWidth;
            _screenHeight = screenHeight;
        }

        BackPressed = false;
        LevelSelected = false;
        _caretBlink += dt * 2.5f;

        var ms = UI.PointerInput.GetState();
        if (!Game1.CanAcceptInput)
        {
            _prevKb = default;
            _prevMs = Game1.NeutralMouseState(ms);
            _lastMousePos = ms.Position;
            return;
        }
        bool mouseMoved = Math.Abs(ms.X - _lastMousePos.X) > 2 || Math.Abs(ms.Y - _lastMousePos.Y) > 2;
        if (mouseMoved) _lastMousePos = ms.Position;
        bool clicked = ms.LeftButton == ButtonState.Pressed && _prevMs.LeftButton == ButtonState.Released;

        if (_inputCooldown > 0f)
        {
            _inputCooldown -= dt;
            _prevKb = kb;
            _prevMs = ms;
            return;
        }

        if (_createModalOpen)
        {
            UpdateCreateModal(kb, ms, clicked);
            _prevKb = kb;
            _prevMs = ms;
            return;
        }

        _hoveredIndex = -1;
        if (_listRect.Contains(ms.Position))
        {
            foreach (var (idx, rect) in _itemRects)
            {
                if (rect.Contains(ms.Position))
                {
                    _hoveredIndex = idx;
                    if (mouseMoved) _selectedIndex = idx;
                    break;
                }
            }
        }

        int itemH = GetItemHeight();
        int wheelDelta = ms.ScrollWheelValue - _prevMs.ScrollWheelValue;
        if (wheelDelta != 0 && _levelPaths.Count > 0)
        {
            float maxScroll = Math.Max(0f, _levelPaths.Count * itemH - _listHeight);
            _targetScrollOffset = Math.Clamp(_targetScrollOffset - (wheelDelta / 120f) * itemH * 1.4f, 0f, maxScroll);
        }

        bool up = JustPressed(kb, Keys.Up);
        bool down = JustPressed(kb, Keys.Down);
        bool enter = JustPressed(kb, Keys.Enter);
        bool esc = JustPressed(kb, Keys.Escape);

        if (up && _levelPaths.Count > 0)
        {
            _selectedIndex = (_selectedIndex - 1 + _levelPaths.Count) % _levelPaths.Count;
            EnsureSelectedVisible();
        }
        else if (down && _levelPaths.Count > 0)
        {
            _selectedIndex = (_selectedIndex + 1) % _levelPaths.Count;
            EnsureSelectedVisible();
        }

        if (clicked && _backBtnRect.Contains(ms.Position))
        {
            BackPressed = true;
        }
        else if (clicked && _newChartBtnRect.Contains(ms.Position))
        {
            OpenCreateModal();
        }
        else if (clicked && _refreshBtnRect.Contains(ms.Position))
        {
            RefreshLevels();
            UI.UiSfx.Play(UI.UiSound.Notice);
        }
        else if (clicked && _hoveredIndex >= 0 && _hoveredIndex < _levelPaths.Count)
        {
            _selectedIndex = _hoveredIndex;
            SelectCurrentLevel();
        }
        else if (enter && _levelPaths.Count > 0)
        {
            SelectCurrentLevel();
        }
        else if (esc)
        {
            BackPressed = true;
        }

        float maxScr = Math.Max(0f, _levelPaths.Count * itemH - _listHeight);
        _targetScrollOffset = Math.Clamp(_targetScrollOffset, 0f, maxScr);
        _scrollOffset = MathHelper.Lerp(_scrollOffset, _targetScrollOffset, Math.Min(1f, dt * 12f));

        _prevKb = kb;
        _prevMs = ms;
    }

    private void OpenCreateModal()
    {
        _createModalOpen = true;
        _createError = null;
        _newLevelName = GenerateDefaultName();
        _newLevelBpm = 120f;
        _selectedOggPath = null;
    }

    private string GenerateDefaultName()
    {
        string levelsDir = FreeplayMenu.GetDefaultLevelsFolder();
        int idx = 1;
        while (File.Exists(Path.Combine(levelsDir, $"New Chart {idx}.fnmm")))
            idx++;
        return $"New Chart {idx}";
    }

    private void UpdateCreateModal(KeyboardState kb, MouseState ms, bool clicked)
    {
        if (JustPressed(kb, Keys.Escape) || (clicked && _mdlCancelRect.Contains(ms.Position)))
        {
            _createModalOpen = false;
            return;
        }

        // Capture level name typing cleanly
        bool shift = kb.IsKeyDown(Keys.LeftShift) || kb.IsKeyDown(Keys.RightShift);
        if (JustPressed(kb, Keys.Back) && _newLevelName.Length > 0)
        {
            _newLevelName = _newLevelName[..^1];
            _createError = null;
        }
        else
        {
            foreach (Keys key in kb.GetPressedKeys())
            {
                if (!JustPressed(kb, key)) continue;
                char? ch = KeyToNameChar(key, shift);
                if (ch.HasValue && _newLevelName.Length < 36)
                {
                    _newLevelName += ch.Value;
                    _createError = null;
                }
                else if (ch.HasValue)
                {
                    UI.UiSfx.Play(UI.UiSound.TypeFull); // name already at max length
                }
            }
        }

        if (JustPressed(kb, Keys.Left)) _newLevelBpm = Math.Max(30f, _newLevelBpm - (shift ? 10f : 1f));
        if (JustPressed(kb, Keys.Right)) _newLevelBpm = Math.Min(400f, _newLevelBpm + (shift ? 10f : 1f));
        if ((JustPressed(kb, Keys.Left) && _newLevelBpm <= 30f) || (JustPressed(kb, Keys.Right) && _newLevelBpm >= 400f))
            UI.UiSfx.Play(UI.UiSound.SliderEnd);

        if (clicked)
        {
            if (_mdlBpmM10Rect.Contains(ms.Position)) _newLevelBpm = Math.Max(30f, _newLevelBpm - 10f);
            else if (_mdlBpmM1Rect.Contains(ms.Position)) _newLevelBpm = Math.Max(30f, _newLevelBpm - 1f);
            else if (_mdlBpmP1Rect.Contains(ms.Position)) _newLevelBpm = Math.Min(400f, _newLevelBpm + 1f);
            else if (_mdlBpmP10Rect.Contains(ms.Position)) _newLevelBpm = Math.Min(400f, _newLevelBpm + 10f);
            else if (_mdlOggBrowseRect.Contains(ms.Position)) BrowseAudioFile();
            else if (_mdlOggClearRect.Contains(ms.Position)) _selectedOggPath = null;
            else if (_mdlCreateRect.Contains(ms.Position)) TryCreateAndOpenChart();
        }
        else if (JustPressed(kb, Keys.Enter))
        {
            TryCreateAndOpenChart();
        }
    }

    private void BrowseAudioFile()
    {
        #if !BLAZORGL && !ANDROID
        var picked = PlatformPaths.PickDesktopFiles(
            "OGG Vorbis Audio (*.ogg)\0*.ogg;*.OGG\0All Files (*.*)\0*.*\0\0",
            "Select Music Track (.OGG)",
            allowMultiSelect: false);
        if (picked.Count > 0 && File.Exists(picked[0]))
        {
            _selectedOggPath = picked[0];
            _createError = null;
        }
        ResetInputCooldown();
        _createModalOpen = true;
        #endif
    }

    private void TryCreateAndOpenChart()
    {
        string cleanName = string.IsNullOrWhiteSpace(_newLevelName) ? "New Chart" : _newLevelName.Trim();
        foreach (char c in Path.GetInvalidFileNameChars())
            cleanName = cleanName.Replace(c, '_');
        cleanName = cleanName.Trim();
        if (string.IsNullOrEmpty(cleanName)) cleanName = "New Chart";

        try
        {
            string levelsDir = FreeplayMenu.GetDefaultLevelsFolder();
            Directory.CreateDirectory(levelsDir);

            string outPath = Path.Combine(levelsDir, cleanName + ".fnmm");
            int suffix = 2;
            while (File.Exists(outPath))
            {
                outPath = Path.Combine(levelsDir, $"{cleanName}_{suffix}.fnmm");
                suffix++;
            }

            byte[]? musicBytes = null;
            if (!string.IsNullOrEmpty(_selectedOggPath) && File.Exists(_selectedOggPath))
                musicBytes = File.ReadAllBytes(_selectedOggPath);

            var level = new FnmmLevel
            {
                LevelName = cleanName,
                Meta = new FnmmMeta
                {
                    Name = cleanName,
                    UUID = Guid.NewGuid().ToString("N"),
                    Version = "1.0"
                },
                Data = new FnmmData
                {
                    Bpm = _newLevelBpm,
                    Version = 1.0f,
                    Npb = 4f,
                    Speed = 1.6f,
                    Strumlines = new List<List<List<FnmmNote>>>
                    {
                        new() { new(), new(), new(), new() }, // Strumline 0: Player
                        new() { new(), new(), new(), new() }, // Strumline 1: Opponent
                    }
                },
                Scene = new FnmmScene
                {
                    Landscape = true,
                    Cameras = new List<SceneCamera> { new() { X = 0f, Y = 0f, Scale = 1f } },
                    Objects = new List<SceneObject>(),
                    Icons = new List<string>()
                },
                Events = new FnmmEvents(),
                MusicData = musicBytes
            };

            FnmmSaver.Save(level, outPath);
            _createModalOpen = false;
            LoadLevelList();

            int createdIdx = _levelPaths.FindIndex(p => string.Equals(Path.GetFullPath(p), Path.GetFullPath(outPath), StringComparison.OrdinalIgnoreCase));
            if (createdIdx >= 0) _selectedIndex = createdIdx;

            SelectedLevelPath = outPath;
            LevelSelected = true;
            Console.WriteLine($"[LevelSelector] Created new chart: {outPath}");
        }
        catch (Exception ex)
        {
            _createError = ex.Message;
            Console.WriteLine($"[LevelSelector] Failed to create chart: {ex.Message}");
        }
    }

    private void SelectCurrentLevel()
    {
        if (_selectedIndex < 0 || _selectedIndex >= _levelPaths.Count) return;
        LevelSelected = true;
        SelectedLevelPath = _levelPaths[_selectedIndex];
        Console.WriteLine($"[LevelSelector] Selected: {_levelNames[_selectedIndex]}");
    }

    private void EnsureSelectedVisible()
    {
        int itemH = GetItemHeight();
        float selTop = _selectedIndex * itemH;
        float selBot = selTop + itemH;
        if (selTop < _targetScrollOffset) _targetScrollOffset = selTop;
        else if (selBot > _targetScrollOffset + _listHeight) _targetScrollOffset = selBot - _listHeight;
    }

    // ── Draw ─────────────────────────────────────────────────────────────────
    public void Draw(SpriteBatch sb, int sw, int sh)
    {
        _screenWidth = sw;
        _screenHeight = sh;
        float s = UiScale.FromScreen(sw, sh);
        int itemH = UiScale.Px(64, s, 46);

        DrawRect(sb, 0, 0, sw, sh, BG_DARK);
        int gridStep = UiScale.Px(120, s, 64);
        for (int y = 0; y < sh; y += gridStep) DrawRect(sb, 0, y, sw, 1, Color.White * 0.02f);
        for (int x = 0; x < sw; x += gridStep) DrawRect(sb, x, 0, 1, sh, Color.White * 0.02f);

        // Top Bar
        int pad = UiScale.Px(24, s);
        int topY = UiScale.Px(22, s);
        int backW = UiScale.Px(112, s);
        int backH = UiScale.Px(36, s);
        _backBtnRect = new Rectangle(sw - backW - pad, topY, backW, backH);
        DrawPillButton(sb, _backBtnRect, UI.PointerInput.BackLabel, new Color(34, 30, 48), Color.White * 0.85f, ACCENT_PINK, UiScale.FontPx(15, s), _prevMs.Position);

        int newBtnW = UiScale.Px(166, s);
        int refreshW = UiScale.Px(110, s);
        int topGap = UiScale.Px(10, s);
        _newChartBtnRect = new Rectangle(pad, topY, newBtnW, backH);
        _refreshBtnRect = new Rectangle(_newChartBtnRect.Right + topGap, topY, refreshW, backH);
        int topBtnFont = UiScale.FontPx(14, s);
        DrawPillButton(sb, _newChartBtnRect, "+ NEW CHART", new Color(24, 82, 58), Color.White, ACCENT_GREEN, topBtnFont, _prevMs.Position);
        DrawPillButton(sb, _refreshBtnRect, "REFRESH", new Color(36, 34, 52), ACCENT_GOLD, ACCENT_GOLD * 0.7f, topBtnFont, _prevMs.Position);

        // Header
        string title = "CHART EDITOR — SELECT LEVEL";
        int titleSize = UiScale.FontPx(32, s);
        var titleSz = MeasureText(title, titleSize);
        DrawText(sb, title, new Vector2(sw / 2f - titleSz.X / 2f, UiScale.Px(24, s)), ACCENT_GOLD, titleSize);

        string subtitle = $"{_levelPaths.Count} level{(_levelPaths.Count == 1 ? "" : "s")} in mods/levels  |  Create new or open any .FNMM";
        int subSize = UiScale.FontPx(15, s);
        var subSz = MeasureText(subtitle, subSize);
        DrawText(sb, subtitle, new Vector2(sw / 2f - subSz.X / 2f, UiScale.Px(74, s)), Color.White * 0.55f, subSize);

        int listTop = UiScale.Px(112, s);
        int listW = Math.Clamp((int)(sw * 0.68f), UiScale.Px(460, s), UiScale.Px(960, s));
        int listX = sw / 2 - listW / 2;
        _listHeight = Math.Max(UiScale.Px(140, s), sh - listTop - UiScale.Px(68, s));
        _listRect = new Rectangle(listX, listTop, listW, _listHeight);

        _itemRects.Clear();
        if (_levelPaths.Count == 0)
        {
            DrawRect(sb, _listRect.X, _listRect.Y, _listRect.Width, _listRect.Height, BG_CARD * 0.6f);
            DrawBorder(sb, _listRect, Color.White * 0.1f);
            var msg1 = "No levels yet in mods/levels";
            var msg2 = "Click '+ NEW CHART' above to create one from scratch, or drop a .FNMM file into Freeplay.";
            int m1Sz = UiScale.FontPx(22, s);
            int m2Sz = UiScale.FontPx(15, s);
            var m1 = MeasureText(msg1, m1Sz);
            var m2 = MeasureText(msg2, m2Sz);
            DrawText(sb, msg1, new Vector2(sw / 2f - m1.X / 2f, _listRect.Y + _listHeight / 2f - UiScale.Px(28, s)), ACCENT_GOLD, m1Sz);
            DrawText(sb, msg2, new Vector2(sw / 2f - m2.X / 2f, _listRect.Y + _listHeight / 2f + UiScale.Px(6, s)), Color.White * 0.65f, m2Sz);
        }
        else
        {
            var gd = sb.GraphicsDevice;
            var prevScissor = gd.ScissorRectangle;
            var prevRaster = gd.RasterizerState;
            _scissorRasterizer ??= new RasterizerState { CullMode = CullMode.None, ScissorTestEnable = true };
            var scissor = Rectangle.Intersect(_listRect, new Rectangle(0, 0, sw, sh));
            if (scissor.Width > 0 && scissor.Height > 0)
            {
                sb.End();
                gd.ScissorRectangle = scissor;
                sb.Begin(SpriteSortMode.Deferred, BlendState.AlphaBlend, SamplerState.LinearClamp, DepthStencilState.None, _scissorRasterizer);

                int idxFont = UiScale.FontPx(14, s);
                int nameFont = UiScale.FontPx(20, s);
                int extFont = UiScale.FontPx(12, s);
                int badgeW = UiScale.Px(34, s);
                int badgeH = UiScale.Px(30, s);
                int cardMarginX = UiScale.Px(4, s);
                int cardPadY = UiScale.Px(3, s);
                int cardTrimW = UiScale.Px(18, s);
                int badgeOffX = UiScale.Px(14, s);
                int selStripeW = UiScale.Px(4, s);

                for (int i = 0; i < _levelPaths.Count; i++)
                {
                    int y = (int)(listTop + i * itemH - _scrollOffset);
                    if (y + itemH < listTop || y > listTop + _listHeight) continue;

                    var r = new Rectangle(listX + cardMarginX, y + cardPadY, listW - cardTrimW, itemH - cardPadY * 2);
                    var hit = Rectangle.Intersect(r, _listRect);
                    if (hit.Width > 0 && hit.Height > 0) _itemRects.Add((i, hit));

                    bool sel = i == _selectedIndex;
                    bool hov = i == _hoveredIndex;
                    DrawRect(sb, r.X, r.Y, r.Width, r.Height, sel ? new Color(46, 36, 62) : (hov ? new Color(38, 34, 56) : BG_CARD * 0.8f));
                    DrawBorder(sb, r, sel ? ACCENT_PINK : (hov ? Color.White * 0.25f : Color.White * 0.08f));
                    if (sel) DrawRect(sb, r.X, r.Y, selStripeW, r.Height, ACCENT_GOLD);

                    string idxStr = $"{i + 1:D2}";
                    var badge = new Rectangle(r.X + badgeOffX, r.Y + (r.Height - badgeH) / 2, badgeW, badgeH);
                    DrawRect(sb, badge.X, badge.Y, badge.Width, badge.Height, ACCENT_PINK * (sel ? 0.35f : 0.16f));
                    var idxSz = MeasureText(idxStr, idxFont);
                    DrawText(sb, idxStr, new Vector2(badge.X + (badge.Width - idxSz.X) / 2f, badge.Y + (badge.Height - idxSz.Y) / 2f),
                        sel ? ACCENT_GOLD : Color.White * 0.6f, idxFont);

                    string ext = Directory.Exists(_levelPaths[i]) ? "FOLDER" : Path.GetExtension(_levelPaths[i]).TrimStart('.').ToUpperInvariant();
                    string name = _levelNames[i];
                    var nameSz = MeasureText(name, nameFont);
                    DrawText(sb, name, new Vector2(badge.Right + badgeOffX, r.Y + (r.Height - nameSz.Y) / 2f), sel ? Color.White : Color.White * 0.82f, nameFont);

                    var extSz = MeasureText(ext, extFont);
                    DrawText(sb, ext, new Vector2(r.Right - extSz.X - UiScale.Px(18, s), r.Y + (r.Height - extSz.Y) / 2f), ACCENT_CYAN * (sel ? 1f : 0.65f), extFont);
                }

                sb.End();
                gd.RasterizerState = prevRaster;
                gd.ScissorRectangle = prevScissor;
                sb.Begin(SpriteSortMode.Deferred, BlendState.AlphaBlend, SamplerState.LinearClamp);
            }
        }

        string hint = "UP/DOWN or WHEEL: Browse  |  ENTER or CLICK: Open Chart  |  ESC: Back";
        int hintSize = UiScale.FontPx(14, s);
        var hintSz = MeasureText(hint, hintSize);
        DrawText(sb, hint, new Vector2(sw / 2f - hintSz.X / 2f, sh - UiScale.Px(36, s)), Color.White * 0.42f, hintSize);

        if (_createModalOpen)
            DrawCreateModal(sb, sw, sh, s);
    }

    private void DrawCreateModal(SpriteBatch sb, int sw, int sh, float s)
    {
        DrawRect(sb, 0, 0, sw, sh, Color.Black * 0.72f);

        int mw = Math.Clamp((int)(sw * 0.46f), UiScale.Px(460, s), UiScale.Px(680, s));
        int mh = UiScale.Px(340, s);
        int mx = sw / 2 - mw / 2;
        int my = sh / 2 - mh / 2;
        var box = new Rectangle(mx, my, mw, mh);

        DrawRect(sb, mx, my, mw, mh, new Color(26, 24, 38));
        DrawBorder(sb, box, ACCENT_GREEN);
        DrawRect(sb, mx, my, mw, UiScale.Px(4, s, 2), ACCENT_GREEN);

        int padX = UiScale.Px(22, s);
        DrawText(sb, "CREATE NEW CHART / LEVEL", new Vector2(mx + padX, my + UiScale.Px(18, s)), ACCENT_GOLD, UiScale.FontPx(22, s));
        DrawText(sb, "Creates a playable & editable .FNMM file in mods/levels", new Vector2(mx + padX, my + UiScale.Px(48, s)), Color.White * 0.55f, UiScale.FontPx(13, s));

        int innerX = mx + padX;
        int innerW = mw - padX * 2;
        int lblFont = UiScale.FontPx(13, s);
        int valFont = UiScale.FontPx(16, s);

        // Name field
        DrawText(sb, "SONG / LEVEL NAME (Type to edit):", new Vector2(innerX, my + UiScale.Px(80, s)), Color.White * 0.8f, lblFont);
        _mdlNameRect = new Rectangle(innerX, my + UiScale.Px(100, s), innerW, UiScale.Px(36, s));
        DrawRect(sb, _mdlNameRect.X, _mdlNameRect.Y, _mdlNameRect.Width, _mdlNameRect.Height, new Color(16, 16, 24));
        DrawBorder(sb, _mdlNameRect, ACCENT_CYAN * 0.7f);
        string caret = ((int)_caretBlink % 2 == 0) ? "_" : "";
        DrawText(sb, _newLevelName + caret, new Vector2(_mdlNameRect.X + UiScale.Px(10, s), _mdlNameRect.Y + UiScale.Px(8, s)), Color.White, valFont);

        // BPM row
        int bpmLblFont = UiScale.FontPx(14, s);
        DrawText(sb, "BPM:", new Vector2(innerX, my + UiScale.Px(152, s)), Color.White * 0.8f, bpmLblFont);
        int bpmBtnY = my + UiScale.Px(146, s);
        int bpmH = UiScale.Px(30, s);
        int bpmGap = UiScale.Px(6, s);
        int bpmWideGap = UiScale.Px(8, s);
        _mdlBpmM10Rect = new Rectangle(innerX + UiScale.Px(54, s), bpmBtnY, UiScale.Px(44, s), bpmH);
        _mdlBpmM1Rect = new Rectangle(_mdlBpmM10Rect.Right + bpmGap, bpmBtnY, UiScale.Px(36, s), bpmH);
        var bpmValRect = new Rectangle(_mdlBpmM1Rect.Right + bpmWideGap, bpmBtnY, UiScale.Px(74, s), bpmH);
        _mdlBpmP1Rect = new Rectangle(bpmValRect.Right + bpmWideGap, bpmBtnY, UiScale.Px(36, s), bpmH);
        _mdlBpmP10Rect = new Rectangle(_mdlBpmP1Rect.Right + bpmGap, bpmBtnY, UiScale.Px(44, s), bpmH);

        DrawPillButton(sb, _mdlBpmM10Rect, "-10", BG_CARD, Color.White, ACCENT_CYAN * 0.5f, lblFont, _prevMs.Position);
        DrawPillButton(sb, _mdlBpmM1Rect, "-1", BG_CARD, Color.White, ACCENT_CYAN * 0.5f, lblFont, _prevMs.Position);
        DrawRect(sb, bpmValRect.X, bpmValRect.Y, bpmValRect.Width, bpmValRect.Height, new Color(16, 16, 24));
        var bpmTxt = $"{_newLevelBpm:F0}";
        var bpmSz = MeasureText(bpmTxt, valFont);
        DrawText(sb, bpmTxt, new Vector2(bpmValRect.X + (bpmValRect.Width - bpmSz.X) / 2f, bpmValRect.Y + (bpmValRect.Height - bpmSz.Y) / 2f), ACCENT_GOLD, valFont);
        DrawPillButton(sb, _mdlBpmP1Rect, "+1", BG_CARD, Color.White, ACCENT_CYAN * 0.5f, lblFont, _prevMs.Position);
        DrawPillButton(sb, _mdlBpmP10Rect, "+10", BG_CARD, Color.White, ACCENT_CYAN * 0.5f, lblFont, _prevMs.Position);

        // Music track row
        DrawText(sb, "AUDIO TRACK (Optional):", new Vector2(innerX, my + UiScale.Px(194, s)), Color.White * 0.8f, lblFont);
        int clearW = UiScale.Px(82, s);
        int oggH = UiScale.Px(34, s);
        int oggGap = UiScale.Px(8, s);
        _mdlOggBrowseRect = new Rectangle(innerX, my + UiScale.Px(214, s), innerW - clearW - oggGap, oggH);
        _mdlOggClearRect = new Rectangle(_mdlOggBrowseRect.Right + oggGap, my + UiScale.Px(214, s), clearW, oggH);
        string audioLabel = string.IsNullOrEmpty(_selectedOggPath)
            ? "Browse .OGG file (or leave empty for blank chart)"
            : $"Track: {Path.GetFileName(_selectedOggPath)}";
        DrawPillButton(sb, _mdlOggBrowseRect, audioLabel, new Color(24, 36, 56), string.IsNullOrEmpty(_selectedOggPath) ? Color.White * 0.8f : ACCENT_GREEN, ACCENT_CYAN, lblFont, _prevMs.Position);
        DrawPillButton(sb, _mdlOggClearRect, "CLEAR", BG_CARD, Color.White * 0.7f, Color.White * 0.2f, UiScale.FontPx(12, s), _prevMs.Position);

        if (!string.IsNullOrEmpty(_createError))
            DrawText(sb, _createError, new Vector2(innerX, my + UiScale.Px(256, s)), new Color(255, 95, 95), lblFont);

        // Footer action buttons
        int btnGap = UiScale.Px(14, s);
        int btnW = (innerW - btnGap) / 2;
        int btnH = UiScale.Px(38, s);
        int btnY = my + mh - UiScale.Px(52, s);
        _mdlCreateRect = new Rectangle(innerX, btnY, btnW, btnH);
        _mdlCancelRect = new Rectangle(innerX + btnW + btnGap, btnY, btnW, btnH);
        DrawPillButton(sb, _mdlCreateRect, "CREATE & OPEN [ENTER]", new Color(28, 96, 66), Color.White, ACCENT_GREEN, bpmLblFont, _prevMs.Position);
        DrawPillButton(sb, _mdlCancelRect, "CANCEL [ESC]", new Color(46, 32, 46), Color.White * 0.85f, ACCENT_PINK, bpmLblFont, _prevMs.Position);
    }

    private static char? KeyToNameChar(Keys k, bool shift)
    {
        if (k >= Keys.A && k <= Keys.Z)
        {
            char c = (char)('a' + (k - Keys.A));
            return shift ? char.ToUpperInvariant(c) : c;
        }
        if (k >= Keys.D0 && k <= Keys.D9 && !shift)
            return (char)('0' + (k - Keys.D0));
        if (k >= Keys.NumPad0 && k <= Keys.NumPad9)
            return (char)('0' + (k - Keys.NumPad0));
        return k switch
        {
            Keys.Space => ' ',
            Keys.OemMinus => shift ? '_' : '-',
            Keys.OemPeriod => '.',
            _ => null
        };
    }

    private void DrawPillButton(SpriteBatch sb, Rectangle r, string text, Color bg, Color fg, Color border, int fontSize, Point mouse)
    {
        bool hov = r.Contains(mouse);
        DrawRect(sb, r.X, r.Y, r.Width, r.Height, hov ? Color.Lerp(bg, Color.White, 0.14f) : bg);
        DrawBorder(sb, r, hov ? Color.White * 0.7f : border);
        var sz = MeasureText(text, fontSize);
        DrawText(sb, text, new Vector2(r.X + (r.Width - sz.X) / 2f, r.Y + (r.Height - sz.Y) / 2f), fg, fontSize);
    }

    private void DrawRect(SpriteBatch sb, int x, int y, int w, int h, Color c) =>
        sb.Draw(_pixel, new Rectangle(x, y, w, h), c);

    private void DrawBorder(SpriteBatch sb, Rectangle r, Color c)
    {
        DrawRect(sb, r.X, r.Y, r.Width, 1, c);
        DrawRect(sb, r.X, r.Bottom - 1, r.Width, 1, c);
        DrawRect(sb, r.X, r.Y, 1, r.Height, c);
        DrawRect(sb, r.Right - 1, r.Y, 1, r.Height, c);
    }

    private Vector2 MeasureText(string text, int size)
    {
        if (_fontRenderer != null) return _fontRenderer.MeasureString(text, size);
        if (_font != null) return _font.MeasureUi(text) * (size / 32f);
        return new Vector2(text.Length * size * 0.5f, size);
    }

    private void DrawText(SpriteBatch sb, string text, Vector2 pos, Color color, int size)
    {
        if (_fontRenderer != null) _fontRenderer.DrawString(sb, text, pos, color, size);
        else if (_font != null) sb.DrawUiString(_font, text, pos, color, 0f, Vector2.Zero, size / 32f, SpriteEffects.None, 0f);
    }

    private bool JustPressed(KeyboardState kb, Keys key) => kb.IsKeyDown(key) && !_prevKb.IsKeyDown(key);
}
