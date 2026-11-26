using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using FunkyMakerPlayer.UI;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using Microsoft.Xna.Framework.Input;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;
using Color = Microsoft.Xna.Framework.Color;
using Point = Microsoft.Xna.Framework.Point;
using Rectangle = Microsoft.Xna.Framework.Rectangle;

namespace FunkyMakerPlayer.Editors.Character;

/// <summary>
/// Full interactive Character Editor for FunkyMaker Player.
/// Imports Sparrow v2 (.xml + .png + optional Psych/V-Slice/Codename .json/.xml), Adobe Animate Texture
/// Atlases (Animation.json + spritemap*.json/png), .ZIP character packs, and existing .FNMM sprites,
/// provides live stage alignment with Idle Onion Skin ghosting, FPS/loop/scale/flipX/icon configuration,
/// and saves FunkyMaker-native packages to <c>mods/characters/&lt;slug&gt;/</c>.
/// </summary>
public sealed class CharacterEditor : IEditor, IDisposable
{
    private readonly GraphicsDevice _gd;
    private readonly Texture2D _pixel;
    private readonly SpriteFont? _font;
    private readonly FontRenderer? _fontRenderer;

    private int _screenW = 1280;
    private int _screenH = 720;
    private float _uiScale = 1f;

    // ── Library & Active Project ─────────────────────────────────────────────
    private List<CharacterSummary> _library = new();
    private int _libraryScroll = 0;
    private CharacterProject? _project;
    private bool _modified;

    // ── GPU Texture Cache for Current Project ────────────────────────────────
    // Key: clipName -> list of frame Texture2D
    private readonly Dictionary<string, List<Texture2D>> _clipTextures = new(StringComparer.OrdinalIgnoreCase);
    private Texture2D? _iconNormalTex;
    private Texture2D? _iconLosingTex;
    private bool _texturesDirty = true;

    // ── Playback & Stage Viewport State ──────────────────────────────────────
    private int _selectedSlot = 0;
    private int _slotScroll = 0;
    private bool _isPlaying = true;
    private float _animFrame = 0f;
    private bool _onionSkin = true;
    private int _onionRefSlot = 0; // 0 = IDLE
    private float _viewZoom = 0.85f;
    private Vector2 _viewPan = Vector2.Zero;

    private bool _draggingOffset;
    private Point _dragStartMouse;
    private Vector2 _dragStartUserOffset;
    private bool _panningStage;
    private Point _panStartMouse;
    private Vector2 _panStartOffset;

    // ── Modals & Async Import State ──────────────────────────────────────────
    private bool _clipPickerOpen;
    private int _clipPickerScroll;
    private bool _editingName;
    private string _nameBuffer = "";

    private Task<CharacterProject>? _importTask;
    private string _importStatus = "Importing character...";
    private string? _toastText;
    private Color _toastColor = new(65, 225, 145);
    private float _toastTimer;
    private float _spinnerAngle;

    // ── Input State ──────────────────────────────────────────────────────────
    private KeyboardState _prevKb;
    private MouseState _prevMouse;
    private MouseState _prevRawMouse;
    private float _inputCooldown = 0.2f;
    private readonly List<(Rectangle Rect, Action OnClick)> _clickTargets = new();

    // ── Layout Rectangles ────────────────────────────────────────────────────
    private Rectangle _headerRect;
    private Rectangle _leftPanelRect;
    private Rectangle _stageRect;
    private Rectangle _rightPanelRect;
    private Rectangle _libraryListRect;

    // ── Theme Palette (matches ChartEditor) ──────────────────────────────────
    private static readonly Color BG_DARK = new(16, 15, 24);
    private static readonly Color BG_PANEL = new(25, 24, 36);
    private static readonly Color BG_CARD = new(36, 35, 52);
    private static readonly Color BG_CARD_HOVER = new(52, 50, 76);
    private static readonly Color ACCENT_PINK = new(255, 68, 136);
    private static readonly Color ACCENT_GOLD = new(255, 215, 0);
    private static readonly Color ACCENT_CYAN = new(0, 200, 255);
    private static readonly Color ACCENT_GREEN = new(65, 225, 145);
    private static readonly Color ACCENT_PURPLE = new(178, 102, 255);

    public bool BackPressed { get; private set; }
    public bool IsModified => _modified;
    public string? LastSavedSlug { get; private set; }

    public CharacterEditor(
        GraphicsDevice gd,
        Texture2D pixel,
        SpriteFont? font,
        FontRenderer? fontRenderer,
        string? initialSlug = null)
    {
        _gd = gd;
        _pixel = pixel;
        _font = font;
        _fontRenderer = fontRenderer;
        _prevKb = LayoutKeyboard.GetState();
        _prevMouse = PointerInput.GetState();
        _prevRawMouse = PointerInput.GetRawState();

        RefreshLibrary();

        if (!string.IsNullOrWhiteSpace(initialSlug))
        {
            var match = _library.FirstOrDefault(c => string.Equals(c.Slug, initialSlug, StringComparison.OrdinalIgnoreCase));
            if (match != null)
                StartLoadFromPath(match.DirectoryPath, $"Loading {match.Name}...");
        }
        else if (_library.Count > 0)
        {
            StartLoadFromPath(_library[0].DirectoryPath, $"Loading {_library[0].Name}...");
        }
    }

    public void ResetInputCooldown()
    {
        _inputCooldown = 0.20f;
        BackPressed = false;
        _draggingOffset = false;
        _panningStage = false;
        _prevKb = LayoutKeyboard.GetState();
        _prevMouse = PointerInput.GetState();
        _prevRawMouse = PointerInput.GetRawState();
    }

    public void SyncInputState()
    {
        _inputCooldown = Math.Max(_inputCooldown, 0.08f);
        _draggingOffset = false;
        _panningStage = false;
        _prevKb = LayoutKeyboard.GetState();
        _prevMouse = PointerInput.GetState();
        _prevRawMouse = PointerInput.GetRawState();
    }

    public void RefreshLibrary()
    {
        _library = CharacterLibrary.ScanCharacters(bakedOnly: false);
    }

    public void Update(float dt, KeyboardState kb) => Update(dt, kb, _screenW, _screenH);

    public void Update(float dt, KeyboardState kb, int sw, int sh)
    {
        if (sw > 0 && sh > 0)
        {
            _screenW = sw;
            _screenH = sh;
            ComputeLayout(sw, sh);
        }

        if (_toastTimer > 0f)
        {
            _toastTimer -= dt;
            if (_toastTimer <= 0f) _toastText = null;
        }

        var ms = PointerInput.GetState();
        var rawMs = PointerInput.GetRawState();
        if (!Game1.CanAcceptInput)
        {
            _draggingOffset = false;
            _panningStage = false;
            _prevKb = default;
            _prevMouse = Game1.NeutralMouseState(ms);
            _prevRawMouse = Game1.NeutralMouseState(rawMs);
            return;
        }

        // Poll async background import if running
        if (_importTask != null)
        {
            _spinnerAngle += dt * 5f;
            if (_importTask.IsCompleted)
            {
                try
                {
                    var loaded = _importTask.GetAwaiter().GetResult();
                    SetActiveProject(loaded, markModified: loaded.SourceKind != CharacterSourceKind.FnmmSprite);
                    ShowToast($"Loaded '{loaded.Name}' ({loaded.AvailableClips.Count} clip(s))", ACCENT_GREEN);
                }
                catch (Exception ex)
                {
                    var baseEx = ex.GetBaseException();
                    Console.WriteLine($"[CharacterEditor] Import failed: {baseEx}");
                    ShowToast($"Import error: {baseEx.Message}", ACCENT_PINK, 4.5f);
                }
                finally
                {
                    _importTask = null;
                    RefreshLibrary();
                }
            }
            _prevKb = kb;
            _prevMouse = ms;
            _prevRawMouse = rawMs;
            return;
        }

        if (_texturesDirty)
            RebuildProjectTextures();

        // Advance active animation playback
        if (_project != null && _isPlaying && _selectedSlot >= 0 && _selectedSlot < _project.Slots.Count)
        {
            var slot = _project.Slots[_selectedSlot];
            var clip = _project.FindClip(slot.ClipName);
            if (clip != null && clip.Frames.Count > 1)
            {
                _animFrame += dt * Math.Clamp(slot.Fps, 1f, 120f);
                if (_animFrame >= clip.Frames.Count)
                {
                    if (slot.Loop)
                        _animFrame %= clip.Frames.Count;
                    else
                        _animFrame = clip.Frames.Count - 1;
                }
            }
            else
            {
                _animFrame = 0f;
            }
        }

        if (_inputCooldown > 0f)
        {
            _inputCooldown -= dt;
            _prevKb = kb;
            _prevMouse = ms;
            _prevRawMouse = rawMs;
            return;
        }

        if (_editingName)
        {
            HandleNameTyping(kb, ms);
            _prevKb = kb;
            _prevMouse = ms;
            _prevRawMouse = rawMs;
            return;
        }

        HandleKeyboard(kb);
        HandleMouse(ms, rawMs, kb);

        _prevKb = kb;
        _prevMouse = ms;
        _prevRawMouse = rawMs;
    }

    private void ComputeLayout(int sw, int sh)
    {
        _uiScale = UiScale.FromScreen(sw, sh);
        float s = _uiScale;

        int headerH = UiScale.Px(54, s);
        int leftW = Math.Clamp((int)(sw * 0.24f), UiScale.Px(250, s), UiScale.Px(340, s));
        int rightW = Math.Clamp((int)(sw * 0.26f), UiScale.Px(270, s), UiScale.Px(370, s));

        _headerRect = new Rectangle(0, 0, sw, headerH);
        _leftPanelRect = new Rectangle(0, headerH, leftW, sh - headerH);
        _rightPanelRect = new Rectangle(sw - rightW, headerH, rightW, sh - headerH);
        _stageRect = new Rectangle(leftW, headerH, Math.Max(120, sw - leftW - rightW), sh - headerH);
    }

    private void HandleNameTyping(KeyboardState kb, MouseState ms)
    {
        if (JustPressed(kb, Keys.Escape))
        {
            _editingName = false;
            return;
        }
        if (JustPressed(kb, Keys.Enter))
        {
            CommitNameEdit();
            return;
        }
        if (JustPressed(kb, Keys.Back) && _nameBuffer.Length > 0)
        {
            _nameBuffer = _nameBuffer.Substring(0, _nameBuffer.Length - 1);
            return;
        }

        bool shift = kb.IsKeyDown(Keys.LeftShift) || kb.IsKeyDown(Keys.RightShift);
        foreach (var k in kb.GetPressedKeys())
        {
            if (_prevKb.IsKeyDown(k)) continue;
            char? ch = KeyToChar(k, shift);
            if (ch.HasValue && _nameBuffer.Length < 34)
                _nameBuffer += ch.Value;
        }

        if (ms.LeftButton == ButtonState.Pressed && _prevMouse.LeftButton == ButtonState.Released && !_headerRect.Contains(ms.Position))
            CommitNameEdit();
    }

    private void CommitNameEdit()
    {
        _editingName = false;
        if (_project != null && !string.IsNullOrWhiteSpace(_nameBuffer))
        {
            _project.Name = _nameBuffer.Trim();
            _project.Slug = CharacterLibrary.SanitizeSlug(_project.Name);
            _modified = true;
        }
    }

    private void HandleKeyboard(KeyboardState kb)
    {
        bool ctrl = kb.IsKeyDown(Keys.LeftControl) || kb.IsKeyDown(Keys.RightControl);
        bool shift = kb.IsKeyDown(Keys.LeftShift) || kb.IsKeyDown(Keys.RightShift);

        if (JustPressed(kb, Keys.Escape))
        {
            if (_clipPickerOpen)
            {
                _clipPickerOpen = false;
                return;
            }
            BackPressed = true;
            return;
        }

        if (_clipPickerOpen) return;

        if (ctrl && JustPressed(kb, Keys.S))
        {
            SaveCurrentCharacter();
            return;
        }

        if (JustPressed(kb, Keys.Space))
        {
            _isPlaying = !_isPlaying;
            if (_isPlaying) _animFrame = 0f;
            return;
        }

        if (JustPressed(kb, Keys.G))
        {
            _onionSkin = !_onionSkin;
            UiSfx.Play(UiSound.Toggle);
            ShowToast(_onionSkin ? "Onion Skin (Ghost) ON" : "Onion Skin OFF", ACCENT_CYAN);
            return;
        }

        // Quick pose testing with 1..5 or gameplay lane keys D F J K
        if (_project != null)
        {
            if (JustPressed(kb, Keys.D1)) SelectSlot(0, replay: true);
            if (JustPressed(kb, Keys.D2) || (!ctrl && JustPressed(kb, Keys.D))) SelectSlot(1, replay: true);
            if (JustPressed(kb, Keys.D3) || (!ctrl && JustPressed(kb, Keys.F))) SelectSlot(2, replay: true);
            if (JustPressed(kb, Keys.D4) || (!ctrl && JustPressed(kb, Keys.J))) SelectSlot(3, replay: true);
            if (JustPressed(kb, Keys.D5) || (!ctrl && JustPressed(kb, Keys.K))) SelectSlot(4, replay: true);

            // Pixel nudge active slot offset with Arrow keys (Shift = 5px)
            float step = shift ? 5f : 1f;
            if (JustPressed(kb, Keys.Left)) NudgeSlotOffset(-step, 0f);
            if (JustPressed(kb, Keys.Right)) NudgeSlotOffset(step, 0f);
            if (JustPressed(kb, Keys.Up)) NudgeSlotOffset(0f, -step);
            if (JustPressed(kb, Keys.Down)) NudgeSlotOffset(0f, step);
        }
    }

    private bool IsOverClickTarget(Point pos)
    {
        for (int i = 0; i < _clickTargets.Count; i++)
        {
            if (_clickTargets[i].Rect.Contains(pos))
                return true;
        }
        return false;
    }

    private void HandleMouse(MouseState ms, MouseState rawMs, KeyboardState kb)
    {
        bool leftClick = ms.LeftButton == ButtonState.Pressed && _prevMouse.LeftButton == ButtonState.Released;
        bool rawLeftPressed = rawMs.LeftButton == ButtonState.Pressed && _prevRawMouse.LeftButton == ButtonState.Released;
        int wheel = ms.ScrollWheelValue - _prevMouse.ScrollWheelValue;

        if (wheel != 0)
        {
            int step = wheel > 0 ? -1 : 1;
            if (_clipPickerOpen && _project != null)
            {
                _clipPickerScroll = Math.Clamp(_clipPickerScroll + step * 2, 0, Math.Max(0, _project.AvailableClips.Count - 8));
            }
            else if (_libraryListRect.Contains(ms.Position))
            {
                _libraryScroll = Math.Clamp(_libraryScroll + step, 0, Math.Max(0, _library.Count - 4));
            }
            else if (_rightPanelRect.Contains(ms.Position) && _project != null)
            {
                _slotScroll = Math.Clamp(_slotScroll + step, 0, Math.Max(0, _project.Slots.Count - 5));
            }
            else if (_stageRect.Contains(ms.Position) && !PointerInput.IsTouch)
            {
                float factor = wheel > 0 ? 1.12f : (1f / 1.12f);
                _viewZoom = Math.Clamp(_viewZoom * factor, 0.15f, 4.0f);
            }
        }

        // Check registered UI buttons first (tap-on-release on Android, click-on-press on desktop)
        if (leftClick)
        {
            for (int i = _clickTargets.Count - 1; i >= 0; i--)
            {
                if (_clickTargets[i].Rect.Contains(ms.Position))
                {
                    _draggingOffset = false;
                    _panningStage = false;
                    _clickTargets[i].OnClick();
                    return;
                }
            }
        }

        if (_clipPickerOpen)
        {
            if (leftClick) _clipPickerOpen = false;
            return;
        }

        // Stage viewport drag: Left-drag = adjust active animation offset; Right/Middle-drag = pan camera.
        // Uses rawMs so holding and dragging a finger on Android moves the character pose live without
        // interfering with button taps (which start inside _clickTargets).
        if (_stageRect.Contains(rawMs.Position) && _project != null && !IsOverClickTarget(rawMs.Position))
        {
            bool panKey = kb.IsKeyDown(Keys.LeftAlt) || kb.IsKeyDown(Keys.RightAlt);
            if (rawMs.RightButton == ButtonState.Pressed && _prevRawMouse.RightButton == ButtonState.Released ||
                rawMs.MiddleButton == ButtonState.Pressed && _prevRawMouse.MiddleButton == ButtonState.Released ||
                (panKey && rawLeftPressed))
            {
                _panningStage = true;
                _panStartMouse = rawMs.Position;
                _panStartOffset = _viewPan;
            }
            else if (rawLeftPressed)
            {
                var slot = GetActiveSlot();
                if (slot != null)
                {
                    _draggingOffset = true;
                    _dragStartMouse = rawMs.Position;
                    _dragStartUserOffset = new Vector2(slot.UserOffsetX, slot.UserOffsetY);
                }
            }
        }

        if (_panningStage)
        {
            if (rawMs.RightButton == ButtonState.Pressed || rawMs.MiddleButton == ButtonState.Pressed || rawMs.LeftButton == ButtonState.Pressed)
            {
                _viewPan = _panStartOffset + new Vector2(rawMs.Position.X - _panStartMouse.X, rawMs.Position.Y - _panStartMouse.Y);
            }
            else
            {
                _panningStage = false;
            }
        }

        if (_draggingOffset)
        {
            if (rawMs.LeftButton == ButtonState.Pressed)
            {
                var slot = GetActiveSlot();
                if (slot != null)
                {
                    float effScale = Math.Max(0.05f, _viewZoom * Math.Max(0.1f, _project?.Scale ?? 1f));
                    float dx = (rawMs.Position.X - _dragStartMouse.X) / effScale;
                    float dy = (rawMs.Position.Y - _dragStartMouse.Y) / effScale;
                    slot.UserOffsetX = MathF.Round(_dragStartUserOffset.X + dx, 1);
                    slot.UserOffsetY = MathF.Round(_dragStartUserOffset.Y + dy, 1);
                    _modified = true;
                }
            }
            else
            {
                _draggingOffset = false;
            }
        }
    }

    // ── Draw ─────────────────────────────────────────────────────────────────

    public void Draw(SpriteBatch sb, int sw, int sh)
    {
        if (sw != _screenW || sh != _screenH || _headerRect.Width == 0)
            ComputeLayout(sw, sh);

        _clickTargets.Clear();
        sb.Draw(_pixel, new Rectangle(0, 0, sw, sh), BG_DARK);

        DrawStageViewport(sb);
        DrawLeftSidebar(sb);
        DrawRightSidebar(sb);
        DrawHeaderBar(sb);

        if (_clipPickerOpen && _project != null)
            DrawClipPickerModal(sb, sw, sh);

        if (_importTask != null)
            DrawImportBusyOverlay(sb, sw, sh);

        if (_toastText != null)
            DrawToast(sb, sw, sh);
    }

    private void DrawHeaderBar(SpriteBatch sb)
    {
        float s = _uiScale;
        sb.Draw(_pixel, _headerRect, BG_PANEL);
        sb.Draw(_pixel, new Rectangle(0, _headerRect.Bottom - 2, _headerRect.Width, 2), ACCENT_PURPLE * 0.75f);

        int pad = UiScale.Px(10, s);
        int btnH = _headerRect.Height - pad * 2;
        int fontBtn = UiScale.FontPx(12, s);

        var backRect = new Rectangle(pad, pad, UiScale.Px(78, s), btnH);
        DrawBtn(sb, backRect, PointerInput.IsTouch ? "Back" : "< ESC", BG_CARD, Color.White, fontBtn, Color.White * 0.2f, () => BackPressed = true);

        // Right-side import & save buttons
        int rx = _headerRect.Right - pad;
        int saveW = UiScale.Px(132, s);
        rx -= saveW;
        var saveRect = new Rectangle(rx, pad, saveW, btnH);
        bool canSave = _project != null;
        DrawBtn(sb, saveRect, _modified ? "SAVE CHAR *" : "SAVE CHAR",
            canSave ? (_modified ? new Color(36, 128, 82) : new Color(28, 92, 62)) : BG_CARD,
            canSave ? Color.White : Color.Gray, fontBtn, canSave ? ACCENT_GREEN : Color.White * 0.1f,
            SaveCurrentCharacter);

        int impGap = UiScale.Px(6, s);
        int impFnmmW = UiScale.Px(108, s);
        rx -= impFnmmW + impGap;
        DrawBtn(sb, new Rectangle(rx, pad, impFnmmW, btnH), "+ .FNMM",
            BG_CARD, ACCENT_GOLD, fontBtn, ACCENT_GOLD * 0.45f, PromptImportFnmm);

        int impSmW = UiScale.Px(136, s);
        rx -= impSmW + impGap;
        DrawBtn(sb, new Rectangle(rx, pad, impSmW, btnH), "+ SPRITEMAP/ZIP",
            new Color(46, 32, 68), ACCENT_PURPLE, fontBtn, ACCENT_PURPLE * 0.65f, PromptImportSpritemapOrZip);

        int impXmlW = UiScale.Px(132, s);
        rx -= impXmlW + impGap;
        DrawBtn(sb, new Rectangle(rx, pad, impXmlW, btnH), "+ XML / JSON",
            new Color(24, 56, 78), ACCENT_CYAN, fontBtn, ACCENT_CYAN * 0.65f, PromptImportSparrow);

        // Title / editable character name box
        int nameX = backRect.Right + UiScale.Px(12, s);
        int maxNameW = Math.Max(UiScale.Px(150, s), rx - nameX - UiScale.Px(12, s));
        var nameRect = new Rectangle(nameX, pad, Math.Min(UiScale.Px(320, s), maxNameW), btnH);

        if (_project != null)
        {
            bool hovName = nameRect.Contains(_prevMouse.Position);
            sb.Draw(_pixel, nameRect, _editingName ? new Color(48, 42, 74) : (hovName ? new Color(28, 26, 42) : new Color(18, 18, 28)));
            DrawBorder(sb, nameRect, _editingName ? ACCENT_GOLD : (hovName ? ACCENT_PURPLE : Color.White * 0.18f));

            var editIcon = GetIcon("edit");
            int editSz = Math.Clamp((int)(nameRect.Height * 0.56f), 12, 24);
            string shown = _editingName ? (_nameBuffer + "_") : _project.Name;
            DrawText(sb, shown, new Vector2(nameRect.X + UiScale.Px(10, s), nameRect.Y + (nameRect.Height - MeasureText(shown, UiScale.FontPx(14, s)).Y) / 2f),
                _editingName ? ACCENT_GOLD : Color.White, UiScale.FontPx(14, s));
            if (editIcon != null && !_editingName)
            {
                var editRect = new Rectangle(nameRect.Right - editSz - UiScale.Px(8, s), nameRect.Y + (nameRect.Height - editSz) / 2, editSz, editSz);
                sb.Draw(editIcon, editRect, hovName ? ACCENT_GOLD : Color.White * 0.65f);
            }

            _clickTargets.Add((nameRect, BeginRenameCharacter));

            string badge = _project.SourceKind switch
            {
                CharacterSourceKind.AnimateSpritemap => "SPRITEMAP",
                CharacterSourceKind.FnmmSprite => "FNMM",
                _ => "SPARROW XML",
            };
            int bx = nameRect.Right + UiScale.Px(8, s);
            if (bx + UiScale.Px(90, s) < rx)
            {
                var bSz = MeasureText(badge, UiScale.FontPx(11, s));
                var bRect = new Rectangle(bx, nameRect.Y + UiScale.Px(4, s), (int)bSz.X + UiScale.Px(14, s), nameRect.Height - UiScale.Px(8, s));
                sb.Draw(_pixel, bRect, ACCENT_PURPLE * 0.22f);
                DrawBorder(sb, bRect, ACCENT_PURPLE * 0.6f);
                DrawTextCentered(sb, badge, bRect, ACCENT_PURPLE, UiScale.FontPx(11, s));
            }
        }
        else
        {
            DrawText(sb, "CHARACTER EDITOR", new Vector2(nameX, pad + UiScale.Px(6, s)), ACCENT_GOLD, UiScale.FontPx(18, s));
        }
    }

    private void DrawLeftSidebar(SpriteBatch sb)
    {
        float s = _uiScale;
        sb.Draw(_pixel, _leftPanelRect, BG_PANEL);
        sb.Draw(_pixel, new Rectangle(_leftPanelRect.Right - 2, _leftPanelRect.Y, 2, _leftPanelRect.Height), ACCENT_PURPLE * 0.4f);

        int pad = UiScale.Px(10, s);
        int x = _leftPanelRect.X + pad;
        int w = _leftPanelRect.Width - pad * 2;
        int y = _leftPanelRect.Y + pad;

        int hdrFont = UiScale.FontPx(12, s);
        int smallFont = UiScale.FontPx(12, s);

        // 1. Saved Characters Library section
        DrawText(sb, $"MODS/CHARACTERS ({_library.Count})", new Vector2(x, y), ACCENT_GOLD, hdrFont);
        var refreshBtn = new Rectangle(_leftPanelRect.Right - pad - UiScale.Px(54, s), y - UiScale.Px(2, s), UiScale.Px(54, s), UiScale.Px(20, s));
        DrawBtn(sb, refreshBtn, "SCAN", BG_CARD, ACCENT_CYAN, UiScale.FontPx(10, s), ACCENT_CYAN * 0.4f, () =>
        {
            RefreshLibrary();
            ShowToast($"Scanned {_library.Count} character(s)", ACCENT_CYAN);
        });
        y += UiScale.Px(22, s);

        int listH = Math.Clamp((int)(_leftPanelRect.Height * 0.34f), UiScale.Px(120, s), UiScale.Px(240, s));
        _libraryListRect = new Rectangle(x, y, w, listH);
        sb.Draw(_pixel, _libraryListRect, new Color(16, 15, 24));
        DrawBorder(sb, _libraryListRect, Color.White * 0.12f);

        int rowH = UiScale.Px(30, s);
        int visibleRows = Math.Max(1, (listH - UiScale.Px(6, s)) / rowH);
        _libraryScroll = Math.Clamp(_libraryScroll, 0, Math.Max(0, _library.Count - visibleRows));

        if (_library.Count == 0)
        {
            DrawTextCentered(sb, "No saved characters yet.\nImport XML+PNG or Spritemap above!", _libraryListRect, Color.Gray, UiScale.FontPx(11, s));
        }
        else
        {
            for (int i = 0; i < visibleRows && (_libraryScroll + i) < _library.Count; i++)
            {
                var entry = _library[_libraryScroll + i];
                var rr = new Rectangle(x + 3, y + 3 + i * rowH, w - 6, rowH - 2);
                bool active = _project != null && string.Equals(_project.Slug, entry.Slug, StringComparison.OrdinalIgnoreCase);
                string tag = entry.IsRawSourceFolder ? $" [{entry.RawSourceHint}]" : (entry.HasIcon ? " [ICO]" : "");
                string lbl = TruncateText(entry.Name + tag, rr.Width - UiScale.Px(14, s), smallFont);

                DrawBtn(sb, rr, lbl,
                    active ? ACCENT_PURPLE * 0.30f : BG_CARD,
                    active ? Color.White : Color.White * 0.85f,
                    smallFont,
                    active ? ACCENT_PURPLE : Color.White * 0.08f,
                    () => StartLoadFromPath(entry.DirectoryPath, $"Loading {entry.Name}..."),
                    leftAlign: true);
            }
        }
        y += listH + UiScale.Px(12, s);

        if (_project == null) return;

        // 2. Character Settings Card
        DrawText(sb, "CHARACTER SETTINGS", new Vector2(x, y), ACCENT_GOLD, hdrFont);
        y += UiScale.Px(20, s);

        int ctrlH = UiScale.Px(28, s);
        int stepW = UiScale.Px(30, s);
        int gap = UiScale.Px(5, s);

        // Scale row
        DrawText(sb, $"Scale: {_project.Scale:F2}x", new Vector2(x, y + UiScale.Px(5, s)), Color.White, smallFont);
        DrawBtn(sb, new Rectangle(x + w - stepW * 2 - gap, y, stepW, ctrlH), "-", BG_CARD, Color.White, smallFont, Color.White * 0.15f, () =>
        {
            _project.Scale = Math.Clamp(MathF.Round(_project.Scale - 0.05f, 2), 0.10f, 6.0f);
            _modified = true;
        });
        DrawBtn(sb, new Rectangle(x + w - stepW, y, stepW, ctrlH), "+", BG_CARD, Color.White, smallFont, Color.White * 0.15f, () =>
        {
            _project.Scale = Math.Clamp(MathF.Round(_project.Scale + 0.05f, 2), 0.10f, 6.0f);
            _modified = true;
        });
        y += ctrlH + UiScale.Px(7, s);

        // Flip Artwork + Swap Left/Right row
        int halfW = (w - gap) / 2;
        DrawBtn(sb, new Rectangle(x, y, halfW, ctrlH),
            _project.BakeFlipX ? "FLIP X: ON" : "FLIP X: OFF",
            _project.BakeFlipX ? new Color(86, 42, 98) : BG_CARD,
            _project.BakeFlipX ? ACCENT_GOLD : Color.White, UiScale.FontPx(11, s),
            _project.BakeFlipX ? ACCENT_GOLD : Color.White * 0.15f,
            () =>
            {
                _project.BakeFlipX = !_project.BakeFlipX;
                _project.NativeFacing = _project.NativeFacing == CharacterFacing.Left ? CharacterFacing.Right : CharacterFacing.Left;
                _texturesDirty = true;
                _modified = true;
            });

        DrawBtn(sb, new Rectangle(x + halfW + gap, y, halfW, ctrlH),
            "SWAP L<->R", BG_CARD, ACCENT_CYAN, UiScale.FontPx(11, s), ACCENT_CYAN * 0.45f,
            SwapLeftRightSlots);
        y += ctrlH + UiScale.Px(7, s);

        // Native Facing direction (tells Chart Editor & Online whether art looks Left like BF or Right like Opponent)
        string faceLbl = _project.NativeFacing == CharacterFacing.Left
            ? "FACES: <- LEFT (BF)"
            : "FACES: RIGHT (FOE) ->";
        DrawBtn(sb, new Rectangle(x, y, w, ctrlH), faceLbl,
            BG_CARD, ACCENT_GREEN, UiScale.FontPx(11, s), ACCENT_GREEN * 0.45f,
            () =>
            {
                _project.NativeFacing = _project.NativeFacing == CharacterFacing.Left ? CharacterFacing.Right : CharacterFacing.Left;
                _modified = true;
            });
        y += ctrlH + UiScale.Px(7, s);

        // Filtering (Smooth Bilinear vs Pixel Point)
        DrawBtn(sb, new Rectangle(x, y, w, ctrlH),
            _project.Bilinear ? "FILTER: SMOOTH (HD)" : "FILTER: PIXEL ART",
            BG_CARD, Color.White, UiScale.FontPx(11, s), Color.White * 0.16f,
            () =>
            {
                _project.Bilinear = !_project.Bilinear;
                _modified = true;
            });
        y += ctrlH + UiScale.Px(12, s);

        // 3. Health Icon Section
        if (y + UiScale.Px(76, s) <= _leftPanelRect.Bottom)
        {
            DrawText(sb, "HEALTH BAR ICON", new Vector2(x, y), ACCENT_GOLD, hdrFont);
            y += UiScale.Px(20, s);

            int icoBox = UiScale.Px(50, s);
            var normBox = new Rectangle(x, y, icoBox, icoBox);
            var loseBox = new Rectangle(x + icoBox + gap, y, icoBox, icoBox);
            sb.Draw(_pixel, normBox, new Color(16, 15, 24));
            sb.Draw(_pixel, loseBox, new Color(16, 15, 24));
            DrawBorder(sb, normBox, ACCENT_GREEN * 0.5f);
            DrawBorder(sb, loseBox, ACCENT_PINK * 0.5f);

            if (_iconNormalTex != null && !_iconNormalTex.IsDisposed)
                DrawTextureFit(sb, _iconNormalTex, normBox, Color.White);
            else
                DrawTextCentered(sb, "N/A", normBox, Color.Gray, UiScale.FontPx(10, s));

            var loseTex = _iconLosingTex ?? _iconNormalTex;
            if (loseTex != null && !loseTex.IsDisposed)
                DrawTextureFit(sb, loseTex, loseBox, Color.White);
            else
                DrawTextCentered(sb, "N/A", loseBox, Color.Gray, UiScale.FontPx(10, s));

            int btnX = loseBox.Right + gap;
            int btnW = Math.Max(UiScale.Px(60, s), x + w - btnX);
            DrawBtn(sb, new Rectangle(btnX, y, btnW, UiScale.Px(24, s)), "LOAD ICON",
                BG_CARD, ACCENT_CYAN, UiScale.FontPx(11, s), ACCENT_CYAN * 0.5f, PromptLoadIcon);

            DrawBtn(sb, new Rectangle(btnX, y + UiScale.Px(26, s), btnW, UiScale.Px(24, s)), "CLEAR",
                BG_CARD, Color.Gray, UiScale.FontPx(11, s), Color.White * 0.12f, () =>
                {
                    _project.IconNormal?.Dispose();
                    _project.IconLosing?.Dispose();
                    _project.IconNormal = null;
                    _project.IconLosing = null;
                    _texturesDirty = true;
                    _modified = true;
                });
        }
    }

    private void DrawStageViewport(SpriteBatch sb)
    {
        float s = _uiScale;
        sb.Draw(_pixel, _stageRect, new Color(19, 18, 29));

        // Checkerboard / stage grid lines inside _stageRect
        int gridStep = Math.Max(24, (int)(48 * _viewZoom));
        for (int gx = _stageRect.X; gx < _stageRect.Right; gx += gridStep)
            sb.Draw(_pixel, new Rectangle(gx, _stageRect.Y, 1, _stageRect.Height), Color.White * 0.03f);
        for (int gy = _stageRect.Y; gy < _stageRect.Bottom; gy += gridStep)
            sb.Draw(_pixel, new Rectangle(_stageRect.X, gy, _stageRect.Width, 1), Color.White * 0.03f);

        // Foot Anchor (0, 0) world point on screen
        Vector2 anchorScreen = new Vector2(
            _stageRect.X + _stageRect.Width * 0.5f + _viewPan.X,
            _stageRect.Y + _stageRect.Height * 0.78f + _viewPan.Y);

        // Stage floor line + crosshair
        int floorY = (int)anchorScreen.Y;
        if (floorY >= _stageRect.Y && floorY <= _stageRect.Bottom)
            sb.Draw(_pixel, new Rectangle(_stageRect.X, floorY, _stageRect.Width, 2), ACCENT_GOLD * 0.35f);
        int ax = (int)anchorScreen.X;
        if (ax >= _stageRect.X && ax <= _stageRect.Right)
            sb.Draw(_pixel, new Rectangle(ax, _stageRect.Y, 1, _stageRect.Height), ACCENT_CYAN * 0.22f);

        if (_project == null)
        {
            string msg = "Import a Sparrow (XML + PNG) sheet, Adobe Animate Spritemap folder/ZIP,\nor pick a saved character on the left to start editing!";
            DrawTextCentered(sb, msg, _stageRect, Color.White * 0.6f, UiScale.FontPx(16, s));
            return;
        }

        // Draw Onion Skin (Idle frame 0 silhouette) behind active pose
        if (_onionSkin && _onionRefSlot >= 0 && _onionRefSlot < _project.Slots.Count && _onionRefSlot != _selectedSlot)
        {
            DrawSlotFrameOnStage(sb, _project.Slots[_onionRefSlot], 0, anchorScreen, new Color(0, 225, 255) * 0.34f);
        }

        // Draw Active Animation Slot
        var activeSlot = GetActiveSlot();
        if (activeSlot != null)
        {
            int frameIdx = (int)Math.Max(0f, _animFrame);
            DrawSlotFrameOnStage(sb, activeSlot, frameIdx, anchorScreen, Color.White);
        }

        // Draw bright Foot Pivot Crosshair on top
        int ch = UiScale.Px(9, s);
        sb.Draw(_pixel, new Rectangle(ax - ch, floorY - 1, ch * 2 + 1, 3), ACCENT_GOLD);
        sb.Draw(_pixel, new Rectangle(ax - 1, floorY - ch, 3, ch * 2 + 1), ACCENT_GOLD);

        // Top Stage Overlay Toolbar (Onion Skin, Play/Pause, Frame step, Zoom)
        int pad = UiScale.Px(8, s);
        int barY = _stageRect.Y + pad;
        int bx = _stageRect.X + pad;
        int bh = UiScale.Px(28, s);
        int bf = UiScale.FontPx(11, s);

        int ghostW = UiScale.Px(106, s);
        DrawBtn(sb, new Rectangle(bx, barY, ghostW, bh),
            _onionSkin ? "GHOST: ON (G)" : "GHOST: OFF (G)",
            _onionSkin ? new Color(18, 68, 86) : BG_CARD,
            _onionSkin ? ACCENT_CYAN : Color.Gray, bf,
            _onionSkin ? ACCENT_CYAN : Color.White * 0.15f,
            () => _onionSkin = !_onionSkin);
        bx += ghostW + UiScale.Px(6, s);

        int playW = UiScale.Px(82, s);
        DrawBtn(sb, new Rectangle(bx, barY, playW, bh),
            _isPlaying ? "PAUSE" : "PLAY",
            _isPlaying ? new Color(110, 82, 24) : new Color(26, 86, 56),
            Color.White, bf, _isPlaying ? ACCENT_GOLD : ACCENT_GREEN,
            () => { _isPlaying = !_isPlaying; if (_isPlaying) _animFrame = 0f; });
        bx += playW + UiScale.Px(6, s);

        var curClip = activeSlot != null ? _project.FindClip(activeSlot.ClipName) : null;
        int totalFrames = curClip?.Frames.Count ?? 1;
        int shownFrame = Math.Clamp((int)_animFrame + 1, 1, Math.Max(1, totalFrames));
        DrawBtn(sb, new Rectangle(bx, barY, UiScale.Px(26, s), bh), "<", BG_CARD, Color.White, bf, Color.White * 0.15f, () =>
        {
            _isPlaying = false;
            _animFrame = ((int)_animFrame - 1 + totalFrames) % Math.Max(1, totalFrames);
        });
        bx += UiScale.Px(28, s);
        var fBox = new Rectangle(bx, barY, UiScale.Px(64, s), bh);
        sb.Draw(_pixel, fBox, BG_PANEL * 0.9f);
        DrawTextCentered(sb, $"{shownFrame}/{totalFrames}", fBox, Color.White, bf);
        bx += UiScale.Px(66, s);
        DrawBtn(sb, new Rectangle(bx, barY, UiScale.Px(26, s), bh), ">", BG_CARD, Color.White, bf, Color.White * 0.15f, () =>
        {
            _isPlaying = false;
            _animFrame = ((int)_animFrame + 1) % Math.Max(1, totalFrames);
        });

        // Right side of stage top bar: Zoom & Reset View
        int zrx = _stageRect.Right - pad;
        int fitW = UiScale.Px(54, s);
        zrx -= fitW;
        DrawBtn(sb, new Rectangle(zrx, barY, fitW, bh), "FIT", BG_CARD, Color.White, bf, Color.White * 0.15f, () =>
        {
            _viewPan = Vector2.Zero;
            _viewZoom = 0.85f;
        });
        zrx -= UiScale.Px(30, s);
        DrawBtn(sb, new Rectangle(zrx, barY, UiScale.Px(26, s), bh), "+", BG_CARD, Color.White, bf, Color.White * 0.15f,
            () => _viewZoom = Math.Clamp(_viewZoom * 1.15f, 0.15f, 4f));
        zrx -= UiScale.Px(30, s);
        DrawBtn(sb, new Rectangle(zrx, barY, UiScale.Px(26, s), bh), "-", BG_CARD, Color.White, bf, Color.White * 0.15f,
            () => _viewZoom = Math.Clamp(_viewZoom / 1.15f, 0.15f, 4f));

        // Bottom Touch/Click Nudge Bar + Hint
        int botY = _stageRect.Bottom - UiScale.Px(36, s);
        int nbW = UiScale.Px(34, s);
        int nbH = UiScale.Px(28, s);
        int nbx = _stageRect.X + pad;
        DrawBtn(sb, new Rectangle(nbx, botY, nbW, nbH), "<", BG_CARD, Color.White, bf, Color.White * 0.18f, () => NudgeSlotOffset(-2f, 0f));
        nbx += nbW + 4;
        DrawBtn(sb, new Rectangle(nbx, botY, nbW, nbH), "^", BG_CARD, Color.White, bf, Color.White * 0.18f, () => NudgeSlotOffset(0f, -2f));
        nbx += nbW + 4;
        DrawBtn(sb, new Rectangle(nbx, botY, nbW, nbH), "v", BG_CARD, Color.White, bf, Color.White * 0.18f, () => NudgeSlotOffset(0f, 2f));
        nbx += nbW + 4;
        DrawBtn(sb, new Rectangle(nbx, botY, nbW, nbH), ">", BG_CARD, Color.White, bf, Color.White * 0.18f, () => NudgeSlotOffset(2f, 0f));
        nbx += nbW + 6;
        DrawBtn(sb, new Rectangle(nbx, botY, UiScale.Px(68, s), nbH), "ZERO OFF", BG_CARD, ACCENT_GOLD, bf, ACCENT_GOLD * 0.4f, () =>
        {
            var sl = GetActiveSlot();
            if (sl != null) { sl.UserOffsetX = 0f; sl.UserOffsetY = 0f; _modified = true; }
        });

        string hint = PointerInput.IsTouch
            ? "Drag: Align Pose  |  Tap arrows: Nudge  |  Back gesture: Exit"
            : "Drag: Align Pose  |  Arrows: 1px (Shift=5px)  |  D/F/J/K: Test Sing  |  R-Drag: Pan";
        int hf = UiScale.FontPx(11, s);
        var hSz = MeasureText(hint, hf);
        if (nbx + UiScale.Px(80, s) + hSz.X < _stageRect.Right)
            DrawText(sb, hint, new Vector2(_stageRect.Right - hSz.X - pad, botY + (nbH - hSz.Y) / 2f), Color.White * 0.5f, hf);
    }

    private void DrawSlotFrameOnStage(SpriteBatch sb, CharacterAnimSlot slot, int frameIndex, Vector2 anchorScreen, Color tint)
    {
        if (_project == null) return;
        var clip = _project.FindClip(slot.ClipName);
        if (clip == null || !_clipTextures.TryGetValue(clip.Name, out var texList) || texList.Count == 0)
            return;

        int clamped = Math.Clamp(frameIndex, 0, texList.Count - 1);
        var tex = texList[clamped];
        if (tex == null || tex.IsDisposed) return;

        Vec2 fnmmOff = _project.ComputeFnmmOffset(slot, clip);
        float drawScale = _project.Scale * _viewZoom;
        Vector2 drawPos = anchorScreen - new Vector2(fnmmOff.X, fnmmOff.Y) * drawScale;

        sb.Draw(tex, drawPos, null, tint, 0f, Vector2.Zero, drawScale, SpriteEffects.None, 0f);
    }

    private void DrawRightSidebar(SpriteBatch sb)
    {
        float s = _uiScale;
        sb.Draw(_pixel, _rightPanelRect, BG_PANEL);
        sb.Draw(_pixel, new Rectangle(_rightPanelRect.X, _rightPanelRect.Y, 2, _rightPanelRect.Height), ACCENT_PURPLE * 0.4f);

        if (_project == null) return;

        int pad = UiScale.Px(10, s);
        int x = _rightPanelRect.X + pad;
        int w = _rightPanelRect.Width - pad * 2;
        int y = _rightPanelRect.Y + pad;

        int hdrFont = UiScale.FontPx(12, s);
        int smallFont = UiScale.FontPx(12, s);

        DrawText(sb, "ANIMATION SLOTS", new Vector2(x, y), ACCENT_GOLD, hdrFont);
        int addW = UiScale.Px(68, s);
        DrawBtn(sb, new Rectangle(x + w - addW, y - UiScale.Px(2, s), addW, UiScale.Px(20, s)), "+ EXTRA",
            BG_CARD, ACCENT_GREEN, UiScale.FontPx(10, s), ACCENT_GREEN * 0.45f, AddExtraSlot);
        y += UiScale.Px(22, s);

        Color[] slotColors =
        {
            ACCENT_GOLD,             // 0 Idle
            new(194, 75, 153),       // 1 Left
            new(0, 225, 255),        // 2 Down
            new(32, 235, 72),        // 3 Up
            new(249, 57, 63),        // 4 Right
        };

        int slotH = UiScale.Px(38, s);
        int slotGap = UiScale.Px(4, s);
        int maxListH = Math.Clamp((int)(_rightPanelRect.Height * 0.50f), UiScale.Px(200, s), UiScale.Px(330, s));
        int visSlots = Math.Max(3, maxListH / (slotH + slotGap));
        _slotScroll = Math.Clamp(_slotScroll, 0, Math.Max(0, _project.Slots.Count - visSlots));

        for (int vi = 0; vi < visSlots && (_slotScroll + vi) < _project.Slots.Count; vi++)
        {
            int idx = _slotScroll + vi;
            var slot = _project.Slots[idx];
            var sr = new Rectangle(x, y, w, slotH);
            bool sel = idx == _selectedSlot;
            Color accent = idx < slotColors.Length ? slotColors[idx] : ACCENT_PURPLE;

            sb.Draw(_pixel, sr, sel ? accent * 0.22f : BG_CARD);
            DrawBorder(sb, sr, sel ? accent : Color.White * 0.1f);
            sb.Draw(_pixel, new Rectangle(sr.X, sr.Y, UiScale.Px(4, s), sr.Height), accent);

            string topLine = $"{idx}: {slot.Label}";
            string clipInfo = string.IsNullOrEmpty(slot.ClipName) ? "(unmapped)" : slot.ClipName;
            clipInfo = TruncateText(clipInfo, w - UiScale.Px(20, s), UiScale.FontPx(10, s));

            DrawText(sb, topLine, new Vector2(sr.X + UiScale.Px(10, s), sr.Y + UiScale.Px(3, s)), Color.White, UiScale.FontPx(12, s));
            DrawText(sb, clipInfo, new Vector2(sr.X + UiScale.Px(10, s), sr.Y + UiScale.Px(19, s)), Color.White * 0.65f, UiScale.FontPx(10, s));

            int captured = idx;
            _clickTargets.Add((sr, () => SelectSlot(captured, replay: true)));
            y += slotH + slotGap;
        }

        y += UiScale.Px(8, s);

        // Selected Slot Inspector
        var active = GetActiveSlot();
        if (active == null) return;

        DrawText(sb, $"SLOT {active.SlotIndex}: {active.Label}", new Vector2(x, y), ACCENT_CYAN, hdrFont);
        if (active.SlotIndex >= 5)
        {
            var delRect = new Rectangle(x + w - UiScale.Px(54, s), y - 2, UiScale.Px(54, s), UiScale.Px(20, s));
            DrawBtn(sb, delRect, "DEL", new Color(110, 32, 48), Color.White, UiScale.FontPx(10, s), ACCENT_PINK, RemoveSelectedExtraSlot);
        }
        y += UiScale.Px(22, s);

        int ctrlH = UiScale.Px(28, s);
        int stepW = UiScale.Px(28, s);
        int gap = UiScale.Px(4, s);

        // Clip selector row (<  ClipName  > + Browse popup)
        DrawText(sb, "Source Clip / Prefix:", new Vector2(x, y), Color.White * 0.75f, UiScale.FontPx(11, s));
        y += UiScale.Px(16, s);

        DrawBtn(sb, new Rectangle(x, y, stepW, ctrlH), "<", BG_CARD, Color.White, smallFont, Color.White * 0.15f, () => CycleSlotClip(-1));
        var pickBtn = new Rectangle(x + stepW + gap, y, Math.Max(40, w - (stepW + gap) * 2), ctrlH);
        string shownClip = TruncateText(string.IsNullOrEmpty(active.ClipName) ? "(pick clip)" : active.ClipName, pickBtn.Width - UiScale.Px(12, s), UiScale.FontPx(11, s));
        DrawBtn(sb, pickBtn, shownClip, new Color(20, 20, 32), ACCENT_GOLD, UiScale.FontPx(11, s), ACCENT_GOLD * 0.45f, () => _clipPickerOpen = true);
        DrawBtn(sb, new Rectangle(pickBtn.Right + gap, y, stepW, ctrlH), ">", BG_CARD, Color.White, smallFont, Color.White * 0.15f, () => CycleSlotClip(1));
        y += ctrlH + UiScale.Px(8, s);

        // FPS & Loop row
        DrawText(sb, $"Speed: {active.Fps:F0} FPS", new Vector2(x, y + UiScale.Px(5, s)), Color.White, smallFont);
        DrawBtn(sb, new Rectangle(x + w - stepW * 2 - gap, y, stepW, ctrlH), "-", BG_CARD, Color.White, smallFont, Color.White * 0.15f, () =>
        {
            active.Fps = Math.Clamp(active.Fps - 2f, 2f, 96f);
            _modified = true;
        });
        DrawBtn(sb, new Rectangle(x + w - stepW, y, stepW, ctrlH), "+", BG_CARD, Color.White, smallFont, Color.White * 0.15f, () =>
        {
            active.Fps = Math.Clamp(active.Fps + 2f, 2f, 96f);
            _modified = true;
        });
        y += ctrlH + UiScale.Px(7, s);

        DrawBtn(sb, new Rectangle(x, y, w, ctrlH),
            active.Loop ? "LOOP / HOLD RETURN: ON" : "LOOP: OFF (FREEZE END)",
            active.Loop ? new Color(24, 72, 54) : BG_CARD,
            active.Loop ? ACCENT_GREEN : Color.Gray, UiScale.FontPx(11, s),
            active.Loop ? ACCENT_GREEN * 0.6f : Color.White * 0.15f,
            () => { active.Loop = !active.Loop; _modified = true; });
        y += ctrlH + UiScale.Px(8, s);

        // Offset Readout
        var clipObj = _project.FindClip(active.ClipName);
        Vec2 fnmmOff = clipObj != null ? _project.ComputeFnmmOffset(active, clipObj) : new Vec2();
        DrawText(sb, $"Nudge: ({active.UserOffsetX:+0.#;-0.#;0}, {active.UserOffsetY:+0.#;-0.#;0}) px", new Vector2(x, y), ACCENT_CYAN, UiScale.FontPx(11, s));
        y += UiScale.Px(16, s);
        DrawText(sb, $"FNMM Offset: ({fnmmOff.X:F0}, {fnmmOff.Y:F0})  Size: {clipObj?.CanvasWidth ?? 0}x{clipObj?.CanvasHeight ?? 0}",
            new Vector2(x, y), Color.White * 0.55f, UiScale.FontPx(10, s));
    }

    private void DrawClipPickerModal(SpriteBatch sb, int sw, int sh)
    {
        if (_project == null) return;
        float s = _uiScale;
        sb.Draw(_pixel, new Rectangle(0, 0, sw, sh), Color.Black * 0.65f);

        int mw = Math.Clamp((int)(sw * 0.46f), UiScale.Px(360, s), UiScale.Px(620, s));
        int mh = Math.Clamp((int)(sh * 0.68f), UiScale.Px(300, s), UiScale.Px(540, s));
        var mr = new Rectangle((sw - mw) / 2, (sh - mh) / 2, mw, mh);
        sb.Draw(_pixel, mr, BG_PANEL);
        DrawBorder(sb, mr, ACCENT_GOLD);

        int pad = UiScale.Px(14, s);
        DrawText(sb, $"SELECT CLIP FOR {GetActiveSlot()?.Label} ({_project.AvailableClips.Count} available)",
            new Vector2(mr.X + pad, mr.Y + pad), ACCENT_GOLD, UiScale.FontPx(14, s));

        var closeBtn = new Rectangle(mr.Right - pad - UiScale.Px(64, s), mr.Y + pad - 2, UiScale.Px(64, s), UiScale.Px(26, s));
        DrawBtn(sb, closeBtn, "CLOSE", BG_CARD, Color.White, UiScale.FontPx(11, s), Color.White * 0.2f, () => _clipPickerOpen = false);

        int listY = mr.Y + UiScale.Px(46, s);
        int rowH = UiScale.Px(32, s);
        int maxRows = Math.Max(3, (mr.Bottom - pad - listY) / (rowH + 3));
        _clipPickerScroll = Math.Clamp(_clipPickerScroll, 0, Math.Max(0, _project.AvailableClips.Count - maxRows));

        for (int i = 0; i < maxRows && (_clipPickerScroll + i) < _project.AvailableClips.Count; i++)
        {
            var clip = _project.AvailableClips[_clipPickerScroll + i];
            var rr = new Rectangle(mr.X + pad, listY + i * (rowH + 3), mw - pad * 2, rowH);
            bool cur = string.Equals(GetActiveSlot()?.ClipName, clip.Name, StringComparison.OrdinalIgnoreCase);
            string lbl = $"{clip.Name}   ({clip.Frames.Count}f, {clip.CanvasWidth}x{clip.CanvasHeight})";
            DrawBtn(sb, rr, lbl, cur ? ACCENT_CYAN * 0.28f : BG_CARD, Color.White, UiScale.FontPx(12, s),
                cur ? ACCENT_CYAN : Color.White * 0.1f, () =>
                {
                    var sl = GetActiveSlot();
                    if (sl != null)
                    {
                        sl.ClipName = clip.Name;
                        sl.Fps = clip.SuggestedFps > 0 ? clip.SuggestedFps : sl.Fps;
                        _animFrame = 0f;
                        _modified = true;
                    }
                    _clipPickerOpen = false;
                }, leftAlign: true);
        }
    }

    private void DrawImportBusyOverlay(SpriteBatch sb, int sw, int sh)
    {
        float s = _uiScale;
        sb.Draw(_pixel, new Rectangle(0, 0, sw, sh), Color.Black * 0.72f);
        int bw = UiScale.Px(380, s);
        int bh = UiScale.Px(110, s);
        var box = new Rectangle((sw - bw) / 2, (sh - bh) / 2, bw, bh);
        sb.Draw(_pixel, box, BG_PANEL);
        DrawBorder(sb, box, ACCENT_PURPLE);

        string dots = new string('.', ((int)(_spinnerAngle * 2f) % 4));
        DrawTextCentered(sb, _importStatus + dots, box, ACCENT_GOLD, UiScale.FontPx(15, s));
    }

    private void DrawToast(SpriteBatch sb, int sw, int sh)
    {
        if (string.IsNullOrEmpty(_toastText)) return;
        float s = _uiScale;
        int tf = UiScale.FontPx(13, s);
        var sz = MeasureText(_toastText!, tf);
        int tw = (int)sz.X + UiScale.Px(28, s);
        int th = (int)sz.Y + UiScale.Px(14, s);
        var tr = new Rectangle((sw - tw) / 2, _headerRect.Bottom + UiScale.Px(12, s), tw, th);
        sb.Draw(_pixel, tr, new Color(16, 16, 26) * 0.94f);
        DrawBorder(sb, tr, _toastColor);
        DrawTextCentered(sb, _toastText!, tr, _toastColor, tf);
    }

    // ── Actions & Helpers ────────────────────────────────────────────────────

    private CharacterAnimSlot? GetActiveSlot()
    {
        if (_project == null || _project.Slots.Count == 0) return null;
        _selectedSlot = Math.Clamp(_selectedSlot, 0, _project.Slots.Count - 1);
        return _project.Slots[_selectedSlot];
    }

    private void SelectSlot(int idx, bool replay)
    {
        if (_project == null || idx < 0 || idx >= _project.Slots.Count) return;
        _selectedSlot = idx;
        if (replay)
        {
            _animFrame = 0f;
            _isPlaying = true;
        }
    }

    private void NudgeSlotOffset(float dx, float dy)
    {
        var sl = GetActiveSlot();
        if (sl == null) return;
        sl.UserOffsetX = MathF.Round(sl.UserOffsetX + dx, 1);
        sl.UserOffsetY = MathF.Round(sl.UserOffsetY + dy, 1);
        _modified = true;
    }

    private void SwapLeftRightSlots()
    {
        if (_project == null || _project.Slots.Count < 5) return;
        var s1 = _project.Slots[1];
        var s4 = _project.Slots[4];
        (s1.ClipName, s4.ClipName) = (s4.ClipName, s1.ClipName);
        (s1.Fps, s4.Fps) = (s4.Fps, s1.Fps);
        (s1.UserOffsetX, s4.UserOffsetX) = (s4.UserOffsetX, s1.UserOffsetX);
        (s1.UserOffsetY, s4.UserOffsetY) = (s4.UserOffsetY, s1.UserOffsetY);
        _modified = true;
        ShowToast("Swapped Sing Left <-> Sing Right!", ACCENT_CYAN);
    }

    private void CycleSlotClip(int dir)
    {
        var sl = GetActiveSlot();
        if (_project == null || sl == null || _project.AvailableClips.Count == 0) return;
        int cur = _project.AvailableClips.FindIndex(c => string.Equals(c.Name, sl.ClipName, StringComparison.OrdinalIgnoreCase));
        int next = ((cur < 0 ? 0 : cur) + dir + _project.AvailableClips.Count) % _project.AvailableClips.Count;
        sl.ClipName = _project.AvailableClips[next].Name;
        _animFrame = 0f;
        _modified = true;
    }

    private void AddExtraSlot()
    {
        if (_project == null || _project.AvailableClips.Count == 0) return;
        int idx = _project.Slots.Count;
        _project.Slots.Add(new CharacterAnimSlot
        {
            SlotIndex = idx,
            Label = $"EXTRA {idx - 4}",
            ClipName = _project.AvailableClips[0].Name,
            Fps = 24f,
            Loop = false,
        });
        _selectedSlot = idx;
        _modified = true;
    }

    private void RemoveSelectedExtraSlot()
    {
        if (_project == null || _selectedSlot < 5 || _selectedSlot >= _project.Slots.Count) return;
        _project.Slots.RemoveAt(_selectedSlot);
        _project.EnsureCoreSlots();
        _selectedSlot = Math.Clamp(_selectedSlot, 0, _project.Slots.Count - 1);
        _modified = true;
    }

    private void BeginRenameCharacter()
    {
        if (_project == null) return;
#if ANDROID
        var activity = Microsoft.Xna.Framework.Game.Activity;
        if (activity != null)
        {
            activity.RunOnUiThread(() =>
            {
                var input = new global::Android.Widget.EditText(activity) { Text = _project.Name };
                input.SetSingleLine(true);
                new global::Android.App.AlertDialog.Builder(activity)
                    .SetTitle("Character Name")
                    .SetView(input)
                    .SetPositiveButton("OK", (_, _) =>
                    {
                        string val = input.Text?.Trim() ?? "";
                        if (!string.IsNullOrEmpty(val) && _project != null)
                        {
                            _project.Name = val;
                            _project.Slug = CharacterLibrary.SanitizeSlug(val);
                            _modified = true;
                        }
                    })
                    .SetNegativeButton("Cancel", (_, _) => { })
                    .Show();
            });
            return;
        }
#endif
        _nameBuffer = _project.Name;
        _editingName = true;
    }

    private void PromptImportSparrow()
    {
#if !ANDROID && !BLAZORGL
        var files = PlatformPaths.PickDesktopFiles(
            "Sparrow / Psych / Codename Character (*.xml;*.json;*.png;*.zip)\0*.xml;*.json;*.png;*.zip\0All Files (*.*)\0*.*\0\0",
            "Select Sparrow XML, PNG, Character JSON, or ZIP");
        if (files.Count > 0)
            StartLoadFromPath(files[0], "Importing Sparrow character");
#elif ANDROID
        PickAndroidFileAndImport("Importing character archive/file");
#endif
    }

    private void PromptImportSpritemapOrZip()
    {
#if !ANDROID && !BLAZORGL
        var files = PlatformPaths.PickDesktopFiles(
            "Animate Spritemap or Character ZIP (Animation.json;spritemap*.json;*.zip)\0Animation.json;spritemap*.json;*.zip;*.json\0All Files (*.*)\0*.*\0\0",
            "Select Animation.json (inside Spritemap folder) or Character .ZIP");
        if (files.Count > 0)
            StartLoadFromPath(files[0], "Rasterizing Adobe Animate Spritemap");
#elif ANDROID
        PickAndroidFileAndImport("Importing Spritemap .ZIP");
#endif
    }

    private void PromptImportFnmm()
    {
#if !ANDROID && !BLAZORGL
        var files = PlatformPaths.PickDesktopFiles(
            "FunkyMaker Level (*.fnmm;*.zip)\0*.fnmm;*.zip\0All Files (*.*)\0*.*\0\0",
            "Select .FNMM Level to Extract Character");
        if (files.Count > 0)
            StartLoadFromPath(files[0], "Extracting character from .FNMM");
#elif ANDROID
        PickAndroidFileAndImport("Extracting character from .FNMM");
#endif
    }

    private void PromptLoadIcon()
    {
        if (_project == null) return;
#if !ANDROID && !BLAZORGL
        var files = PlatformPaths.PickDesktopFiles(
            "Health Icon PNG (*.png)\0*.png\0\0",
            "Select Health Icon PNG (150x150 or 300x150 2-frame strip)");
        if (files.Count > 0)
        {
            try
            {
                CharacterLibrary.LoadIconFromPng(_project, files[0]);
                _texturesDirty = true;
                _modified = true;
                ShowToast("Health icon loaded!", ACCENT_GREEN);
            }
            catch (Exception ex)
            {
                ShowToast($"Icon error: {ex.Message}", ACCENT_PINK);
            }
        }
#elif ANDROID
        var activity = Microsoft.Xna.Framework.Game.Activity as Platforms.Android.MainActivity;
        activity?.PickFile((uri, fileName, stream) =>
        {
            if (stream == null || _project == null) return;
            try
            {
                string tmp = Path.Combine(Path.GetTempPath(), "fmp_ico_" + (fileName ?? "icon.png"));
                using (var fs = File.Create(tmp)) stream.CopyTo(fs);
                CharacterLibrary.LoadIconFromPng(_project, tmp);
                try { File.Delete(tmp); } catch { }
                _texturesDirty = true;
                _modified = true;
                ShowToast("Health icon loaded!", ACCENT_GREEN);
            }
            catch (Exception ex)
            {
                ShowToast($"Icon error: {ex.Message}", ACCENT_PINK);
            }
        });
#endif
    }

#if ANDROID
    private void PickAndroidFileAndImport(string statusMsg)
    {
        var activity = Microsoft.Xna.Framework.Game.Activity as Platforms.Android.MainActivity;
        if (activity == null) return;
        activity.PickFile((uri, fileName, stream) =>
        {
            if (stream == null) return;
            string safeName = string.IsNullOrWhiteSpace(fileName) ? "character.zip" : fileName!;
            string tmpPath = Path.Combine(Path.GetTempPath(), "fmp_char_imp_" + Guid.NewGuid().ToString("N") + "_" + safeName);
            using (var fs = File.Create(tmpPath))
                stream.CopyTo(fs);

            _importStatus = statusMsg;
            _importTask = Task.Run(() =>
            {
                try { return CharacterLibrary.ImportAny(tmpPath); }
                finally { try { File.Delete(tmpPath); } catch { } }
            });
        });
    }
#endif

    private void StartLoadFromPath(string path, string statusMsg)
    {
        _importStatus = statusMsg;
        _importTask = Task.Run(() => CharacterLibrary.ImportAny(path));
    }

    private void SaveCurrentCharacter()
    {
        if (_project == null) return;
        try
        {
            CharacterLibrary.BakeAndSave(_project);
            LastSavedSlug = _project.Slug;
            _modified = false;
            RefreshLibrary();
            ShowToast($"Saved to mods/characters/{_project.Slug}!", ACCENT_GREEN);
        }
        catch (Exception ex)
        {
            ShowToast($"Save failed: {ex.Message}", ACCENT_PINK, 4f);
        }
    }

    private void SetActiveProject(CharacterProject proj, bool markModified)
    {
        DisposeProjectTextures();
        _project?.Dispose();
        _project = proj;
        _project.EnsureCoreSlots();
        _selectedSlot = 0;
        _slotScroll = 0;
        _animFrame = 0f;
        _isPlaying = true;
        _viewPan = Vector2.Zero;
        _modified = markModified;
        _texturesDirty = true;

        // Auto-fit initial zoom to Idle height
        var idleClip = _project.FindClip(_project.Slots[0].ClipName);
        if (idleClip != null && idleClip.CanvasHeight > 0 && _stageRect.Height > 120)
        {
            float targetH = _stageRect.Height * 0.60f;
            _viewZoom = Math.Clamp(targetH / (idleClip.CanvasHeight * Math.Max(0.2f, _project.Scale)), 0.20f, 2.2f);
        }
    }

    private void RebuildProjectTextures()
    {
        _texturesDirty = false;
        DisposeProjectTextures();
        if (_project == null) return;

        foreach (var clip in _project.AvailableClips)
        {
            var list = new List<Texture2D>(clip.Frames.Count);
            foreach (var img in clip.Frames)
            {
                list.Add(CreateTextureFromImage(img, _project.BakeFlipX));
            }
            _clipTextures[clip.Name] = list;
        }

        if (_project.IconNormal != null)
            _iconNormalTex = CreateTextureFromImage(_project.IconNormal, _project.BakeFlipX);
        if (_project.IconLosing != null)
            _iconLosingTex = CreateTextureFromImage(_project.IconLosing, _project.BakeFlipX);
    }

    private Texture2D CreateTextureFromImage(Image<Rgba32> img, bool flipH)
    {
        int w = img.Width;
        int h = img.Height;
        var tex = new Texture2D(_gd, w, h, false, SurfaceFormat.Color);
        var pixels = new Color[w * h];

        img.ProcessPixelRows(acc =>
        {
            for (int y = 0; y < h; y++)
            {
                var row = acc.GetRowSpan(y);
                int dstRowStart = y * w;
                for (int x = 0; x < w; x++)
                {
                    var p = row[flipH ? (w - 1 - x) : x];
                    // Premultiply for MonoGame SpriteBatch BlendState.AlphaBlend
                    if (p.A == 0)
                        pixels[dstRowStart + x] = Color.Transparent;
                    else if (p.A == 255)
                        pixels[dstRowStart + x] = new Color(p.R, p.G, p.B, (byte)255);
                    else
                    {
                        float a = p.A / 255f;
                        pixels[dstRowStart + x] = new Color((byte)(p.R * a), (byte)(p.G * a), (byte)(p.B * a), p.A);
                    }
                }
            }
        });

        tex.SetData(pixels);
        return tex;
    }

    private void DisposeProjectTextures()
    {
        foreach (var list in _clipTextures.Values)
            foreach (var t in list)
                try { if (!t.IsDisposed) t.Dispose(); } catch { }
        _clipTextures.Clear();

        try { if (_iconNormalTex != null && !_iconNormalTex.IsDisposed) _iconNormalTex.Dispose(); } catch { }
        try { if (_iconLosingTex != null && !_iconLosingTex.IsDisposed) _iconLosingTex.Dispose(); } catch { }
        _iconNormalTex = null;
        _iconLosingTex = null;
    }

    public void Dispose()
    {
        foreach (var t in _icons.Values)
            try { if (t != null && !t.IsDisposed) t.Dispose(); } catch { }
        _icons.Clear();
        DisposeProjectTextures();
        _project?.Dispose();
        _project = null;
    }

    private void ShowToast(string msg, Color color, float seconds = 2.6f)
    {
        _toastText = msg;
        _toastColor = color;
        _toastTimer = seconds;
    }

    // ── Primitive UI Drawing ─────────────────────────────────────────────────

    // ── Icon buttons (PNG icons in Assets/images/ui/character_editor/) ───────
    private readonly Dictionary<string, Texture2D?> _icons = new(StringComparer.OrdinalIgnoreCase);

    private Texture2D? GetIcon(string name)
    {
        if (_icons.TryGetValue(name, out var cached)) return cached;
        var tex = LoadUiIcon(_gd, name);
        _icons[name] = tex;
        return tex;
    }

    public static Texture2D? LoadUiIcon(GraphicsDevice gd, string name)
    {
        try
        {
            using var s = TitleContainer.OpenStream($"Assets/images/ui/character_editor/{name}.png");
            var tex = Texture2D.FromStream(gd, s);
            var px = new Color[tex.Width * tex.Height];
            tex.GetData(px);
            for (int i = 0; i < px.Length; i++)
            {
                var c = px[i];
                px[i] = new Color(c.R * c.A / 255, c.G * c.A / 255, c.B * c.A / 255, c.A);
            }
            tex.SetData(px);
            return tex;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>Maps a button label to an icon name and the (shortened) label kept next to it ("" = icon only).</summary>
    private static (string? Icon, string Label) ResolveIcon(string text)
    {
        switch (text)
        {
            case "< ESC": return ("back", "ESC");
            case "Back": return ("back", "Back");
            case "<": return ("prev", "");
            case ">": return ("next", "");
            case "+": return ("add", "");
            case "-": return ("remove", "");
            case "^": return ("arrow_up", "");
            case "v": return ("arrow_down", "");
            case "SCAN": return ("refresh", "SCAN");
            case "FIT": return ("fit", "FIT");
            case "PAUSE": return ("pause", "PAUSE");
            case "PLAY": return ("play", "PLAY");
            case "ZERO OFF": return ("reset_origin", "ZERO");
            case "LOAD ICON": return ("load_icon", "ICON");
            case "CLEAR": return ("delete", "CLEAR");
            case "DEL": return ("delete", "DEL");
            case "CLOSE": return ("close", "CLOSE");
            case "+ EXTRA": return ("extra_track", "EXTRA");
            case "SWAP L<->R": return ("swap_horiz", "SWAP L/R");
            case "+ .FNMM": return ("import_fnmm", ".FNMM");
            case "+ SPRITEMAP/ZIP": return ("import_spritemap", "SPRITEMAP/ZIP");
            case "+ XML / JSON": return ("import_xml", "XML / JSON");
            case "SAVE CHAR": return ("save", "SAVE");
            case "SAVE CHAR *": return ("save", "SAVE *");
        }
        if (text.StartsWith("FLIP X")) return ("flip", text.Replace("FLIP X: ", "FLIP "));
        if (text.StartsWith("GHOST: ON")) return ("ghost_on", "GHOST (G)");
        if (text.StartsWith("GHOST: OFF")) return ("ghost_off", "GHOST (G)");
        if (text.StartsWith("FACES: <-")) return ("face_left", "FACES LEFT (BF)");
        if (text.StartsWith("FACES:")) return ("face_right", "FACES RIGHT (FOE)");
        if (text.StartsWith("FILTER: SMOOTH")) return ("filter_smooth", "SMOOTH (HD)");
        if (text.StartsWith("FILTER: PIXEL")) return ("filter_pixel", "PIXEL ART");
        if (text.StartsWith("LOOP / HOLD")) return ("loop_on", "LOOP ON");
        if (text.StartsWith("LOOP: OFF")) return ("loop_off", "LOOP OFF");
        return (null, text);
    }

    private void DrawBtn(SpriteBatch sb, Rectangle r, string text, Color bg, Color fg, int fontSize, Color border, Action onClick, bool leftAlign = false)
    {
        bool hov = r.Contains(_prevMouse.Position);
        sb.Draw(_pixel, r, hov ? Color.Lerp(bg, Color.White, 0.12f) : bg);
        DrawBorder(sb, r, hov ? Color.Lerp(border, Color.White, 0.35f) : border);

        Texture2D? icon = null;
        string label = text;
        if (!leftAlign)
        {
            var (iconName, shortLabel) = ResolveIcon(text);
            if (iconName != null)
            {
                icon = GetIcon(iconName);
                if (icon != null) label = shortLabel;
            }
        }

        if (icon != null)
        {
            int isz = Math.Clamp((int)(r.Height * 0.62f), 12, 40);
            int gap = label.Length > 0 ? UiScale.Px(5, _uiScale, 3) : 0;
            float textW = label.Length > 0 ? MeasureText(label, fontSize).X : 0f;
            float total = isz + gap + textW;
            if (total > r.Width - 4 && label.Length > 0) { label = ""; gap = 0; textW = 0; total = isz; }
            float sx = r.X + (r.Width - total) / 2f;
            var iconRect = new Rectangle((int)sx, r.Y + (r.Height - isz) / 2, isz, isz);
            sb.Draw(icon, iconRect, fg);
            if (label.Length > 0)
            {
                var sz = MeasureText(label, fontSize);
                DrawText(sb, label, new Vector2(sx + isz + gap, r.Y + (r.Height - sz.Y) / 2f), fg, fontSize);
            }
        }
        else if (leftAlign)
        {
            var sz = MeasureText(text, fontSize);
            DrawText(sb, text, new Vector2(r.X + UiScale.Px(8, _uiScale), r.Y + (r.Height - sz.Y) / 2f), fg, fontSize);
        }
        else
        {
            DrawTextCentered(sb, text, r, fg, fontSize);
        }
        _clickTargets.Add((r, onClick));
    }

    private void DrawTextureFit(SpriteBatch sb, Texture2D tex, Rectangle box, Color tint)
    {
        int pad = 4;
        float maxW = Math.Max(4, box.Width - pad * 2);
        float maxH = Math.Max(4, box.Height - pad * 2);
        float sc = MathF.Min(maxW / tex.Width, maxH / tex.Height);
        int dw = Math.Max(1, (int)(tex.Width * sc));
        int dh = Math.Max(1, (int)(tex.Height * sc));
        sb.Draw(tex, new Rectangle(box.X + (box.Width - dw) / 2, box.Y + (box.Height - dh) / 2, dw, dh), tint);
    }

    private void DrawBorder(SpriteBatch sb, Rectangle r, Color c)
    {
        sb.Draw(_pixel, new Rectangle(r.X, r.Y, r.Width, 1), c);
        sb.Draw(_pixel, new Rectangle(r.X, r.Bottom - 1, r.Width, 1), c);
        sb.Draw(_pixel, new Rectangle(r.X, r.Y, 1, r.Height), c);
        sb.Draw(_pixel, new Rectangle(r.Right - 1, r.Y, 1, r.Height), c);
    }

    private Vector2 MeasureText(string text, int size)
    {
        if (_fontRenderer != null) return _fontRenderer.MeasureString(text, size);
        if (_font != null) return _font.MeasureString(text) * (size / 32f);
        return new Vector2(text.Length * size * 0.5f, size);
    }

    private void DrawText(SpriteBatch sb, string text, Vector2 pos, Color color, int size)
    {
        if (_fontRenderer != null) _fontRenderer.DrawString(sb, text, pos, color, size);
        else if (_font != null) sb.DrawString(_font, text, pos, color, 0f, Vector2.Zero, size / 32f, SpriteEffects.None, 0f);
    }

    private void DrawTextCentered(SpriteBatch sb, string text, Rectangle box, Color color, int size)
    {
        string[] lines = text.Split('\n');
        float lineH = MeasureText("Ay", size).Y;
        float totalH = lines.Length * lineH;
        float startY = box.Y + (box.Height - totalH) * 0.5f;
        for (int i = 0; i < lines.Length; i++)
        {
            var sz = MeasureText(lines[i], size);
            DrawText(sb, lines[i], new Vector2(box.X + (box.Width - sz.X) * 0.5f, startY + i * lineH), color, size);
        }
    }

    private string TruncateText(string text, int maxW, int size)
    {
        if (MeasureText(text, size).X <= maxW) return text;
        for (int len = text.Length - 1; len > 3; len--)
        {
            string sub = text.Substring(0, len) + "..";
            if (MeasureText(sub, size).X <= maxW) return sub;
        }
        return text;
    }

    private bool JustPressed(KeyboardState kb, Keys k) => kb.IsKeyDown(k) && !_prevKb.IsKeyDown(k);

    private static char? KeyToChar(Keys k, bool shift)
    {
        if (k >= Keys.A && k <= Keys.Z)
        {
            char c = (char)('a' + (k - Keys.A));
            return shift ? char.ToUpperInvariant(c) : c;
        }
        if (k >= Keys.D0 && k <= Keys.D9) return (char)('0' + (k - Keys.D0));
        if (k >= Keys.NumPad0 && k <= Keys.NumPad9) return (char)('0' + (k - Keys.NumPad0));
        if (k == Keys.Space) return ' ';
        if (k == Keys.OemMinus || k == Keys.Subtract) return shift ? '_' : '-';
        return null;
    }
}
