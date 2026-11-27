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
/// provides live stage alignment with Onion Skin ghosting, Smooth/Pixel filtering preview, multi-backdrop
/// stage (Dark/Checker/Light), FPS/loop/scale/flipX/icon configuration, and saves FunkyMaker-native
/// packages to <c>mods/characters/&lt;slug&gt;/</c>.
/// </summary>
public sealed class CharacterEditor : IEditor, IDisposable
{
    private readonly GraphicsDevice _gd;
    private readonly Texture2D _pixel;
    private readonly SpriteFont? _font;
    private readonly FontRenderer? _fontRenderer;
    private readonly RasterizerState _scissorRasterizer = new() { ScissorTestEnable = true, CullMode = CullMode.None };

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
    private int _onionRefSlot = 0; // 0 = IDLE (can be switched to any slot)
    private int _stageBgMode = 0;  // 0 = Dark, 1 = Checker, 2 = Light
    private float _viewZoom = 0.85f;
    private Vector2 _viewPan = Vector2.Zero;

    private bool _draggingOffset;
    private Point _dragStartMouse;
    private Vector2 _dragStartUserOffset;
    private bool _panningStage;
    private Point _panStartMouse;
    private Vector2 _panStartOffset;

    // ── Modals & Async Import State ──────────────────────────────────────────
    private bool _importModalOpen;
    private bool _clipPickerOpen;
    private int _clipPickerScroll;
    private int _clipPickerHighlight;
    private bool _confirmDiscardOpen;
    private string _confirmDiscardTitle = "UNSAVED CHANGES";
    private string _confirmDiscardBody = "";
    private Action? _pendingDiscardAction;

    private bool _editingName;
    private string _nameBuffer = "";

    private Task<CharacterProject>? _importTask;
    private string _importStatus = "Importing character...";
    private string? _toastText;
    private Color _toastColor = new(65, 225, 145);
    private float _toastTimer;
    private float _spinnerAngle;

    // ── Hover Tooltip ────────────────────────────────────────────────────────
    private string? _hoverTooltip;
    private Rectangle _hoverTooltipRect;

    // ── Input State ──────────────────────────────────────────────────────────
    private KeyboardState _prevKb;
    private MouseState _prevMouse;
    private MouseState _prevRawMouse;
    private float _inputCooldown = 0.2f;
    private readonly List<(Rectangle Rect, Action OnClick, bool Repeatable)> _clickTargets = new();
    private Rectangle _heldRepeatRect;
    private Action? _heldRepeatAction;
    private float _heldRepeatTimer;
    private bool _suppressNextReleaseClick;
    private const float RepeatInitialDelay = 0.30f;
    private const float RepeatInterval = 0.055f;

    // ── Layout Rectangles ────────────────────────────────────────────────────
    private Rectangle _headerRect;
    private Rectangle _leftPanelRect;
    private Rectangle _stageRect;
    private Rectangle _stageCanvasRect; // _stageRect minus top & bottom toolbars
    private Rectangle _rightPanelRect;
    private Rectangle _libraryListRect;
    private Rectangle _slotsListRect;

    // ── Theme Palette (matches ChartEditor & EditorPicker) ───────────────────
    private static readonly Color BG_DARK = new(16, 15, 24);
    private static readonly Color BG_PANEL = new(24, 23, 35);
    private static readonly Color BG_SECTION = new(19, 18, 29);
    private static readonly Color BG_CARD = new(34, 33, 50);
    private static readonly Color BG_BAR = new(15, 14, 23);
    private static readonly Color ACCENT_PINK = new(255, 68, 136);
    private static readonly Color ACCENT_GOLD = new(255, 215, 0);
    private static readonly Color ACCENT_CYAN = new(0, 200, 255);
    private static readonly Color ACCENT_GREEN = new(65, 225, 145);
    private static readonly Color ACCENT_PURPLE = new(178, 102, 255);

    private static readonly Color[] SlotAccentColors =
    {
        new(255, 215, 0),   // 0 Idle
        new(194, 75, 153),  // 1 Left
        new(0, 225, 255),   // 2 Down
        new(32, 235, 72),   // 3 Up
        new(249, 57, 63),   // 4 Right
    };

    private static readonly string[] QuickPoseShortLabels = { "IDLE", "L", "D", "U", "R" };

    public bool BackPressed { get; private set; }
    public bool IsModified => _modified;
    public string? LastSavedSlug { get; private set; }

    private bool AnyModalOpen => _importModalOpen || _clipPickerOpen || _confirmDiscardOpen;

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
        _heldRepeatAction = null;
        _suppressNextReleaseClick = false;
        _prevKb = LayoutKeyboard.GetState();
        _prevMouse = PointerInput.GetState();
        _prevRawMouse = PointerInput.GetRawState();
    }

    public void SyncInputState()
    {
        _inputCooldown = Math.Max(_inputCooldown, 0.08f);
        _draggingOffset = false;
        _panningStage = false;
        _heldRepeatAction = null;
        _suppressNextReleaseClick = false;
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
            _heldRepeatAction = null;
            _suppressNextReleaseClick = false;
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
        HandleMouse(ms, rawMs, kb, dt);

        _prevKb = kb;
        _prevMouse = ms;
        _prevRawMouse = rawMs;
    }

    private void ComputeLayout(int sw, int sh)
    {
        _uiScale = UiScale.FromScreen(sw, sh);
        float s = _uiScale;

        int headerH = UiScale.Px(54, s);
        int leftW = Math.Clamp((int)(sw * 0.235f), UiScale.Px(255, s), UiScale.Px(335, s));
        int rightW = Math.Clamp((int)(sw * 0.255f), UiScale.Px(275, s), UiScale.Px(365, s));

        _headerRect = new Rectangle(0, 0, sw, headerH);
        _leftPanelRect = new Rectangle(0, headerH, leftW, Math.Max(120, sh - headerH));
        _rightPanelRect = new Rectangle(sw - rightW, headerH, rightW, Math.Max(120, sh - headerH));
        _stageRect = new Rectangle(leftW, headerH, Math.Max(120, sw - leftW - rightW), Math.Max(120, sh - headerH));

        int topBarH = UiScale.Px(40, s);
        int botBarH = UiScale.Px(42, s);
        _stageCanvasRect = new Rectangle(
            _stageRect.X,
            _stageRect.Y + topBarH,
            _stageRect.Width,
            Math.Max(60, _stageRect.Height - topBarH - botBarH));
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
            string trimmed = _nameBuffer.Trim();
            if (!string.Equals(_project.Name, trimmed, StringComparison.Ordinal))
            {
                _project.Name = trimmed;
                _project.Slug = CharacterLibrary.SanitizeSlug(_project.Name);
                _modified = true;
            }
        }
    }

    private void HandleKeyboard(KeyboardState kb)
    {
        bool ctrl = kb.IsKeyDown(Keys.LeftControl) || kb.IsKeyDown(Keys.RightControl);
        bool shift = kb.IsKeyDown(Keys.LeftShift) || kb.IsKeyDown(Keys.RightShift);

        // Modal-specific keyboard handling
        if (_confirmDiscardOpen)
        {
            if (JustPressed(kb, Keys.Escape))
            {
                CloseConfirmDiscard();
                UiSfx.Play(UiSound.Back);
            }
            else if (JustPressed(kb, Keys.Enter))
            {
                ExecuteSaveAndPendingAction();
            }
            return;
        }

        if (_importModalOpen)
        {
            if (JustPressed(kb, Keys.Escape))
            {
                _importModalOpen = false;
                UiSfx.Play(UiSound.Back);
            }
            else if (JustPressed(kb, Keys.D1)) { _importModalOpen = false; PromptImportSparrow(); }
            else if (JustPressed(kb, Keys.D2)) { _importModalOpen = false; PromptImportSpritemapOrZip(); }
            else if (JustPressed(kb, Keys.D3)) { _importModalOpen = false; PromptImportFnmm(); }
            return;
        }

        if (_clipPickerOpen)
        {
            if (JustPressed(kb, Keys.Escape))
            {
                _clipPickerOpen = false;
                UiSfx.Play(UiSound.Back);
                return;
            }
            if (_project != null && _project.AvailableClips.Count > 0)
            {
                int count = _project.AvailableClips.Count;
                if (JustPressed(kb, Keys.Up))
                {
                    _clipPickerHighlight = (_clipPickerHighlight - 1 + count) % count;
                    EnsureClipPickerHighlightVisible();
                    UiSfx.Play(UiSound.Move);
                }
                else if (JustPressed(kb, Keys.Down))
                {
                    _clipPickerHighlight = (_clipPickerHighlight + 1) % count;
                    EnsureClipPickerHighlightVisible();
                    UiSfx.Play(UiSound.Move);
                }
                else if (JustPressed(kb, Keys.Enter))
                {
                    ApplyPickedClip(_clipPickerHighlight);
                }
            }
            return;
        }

        if (JustPressed(kb, Keys.Escape))
        {
            RequestLeaveEditor();
            return;
        }

        if (ctrl && JustPressed(kb, Keys.S))
        {
            SaveCurrentCharacter();
            return;
        }

        if (!ctrl && JustPressed(kb, Keys.I))
        {
            _importModalOpen = true;
            UiSfx.Play(UiSound.SubTab);
            return;
        }

        if (!ctrl && JustPressed(kb, Keys.B))
        {
            CycleStageBackground();
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

    private void HandleMouse(MouseState ms, MouseState rawMs, KeyboardState kb, float dt)
    {
        bool leftClick = ms.LeftButton == ButtonState.Pressed && _prevMouse.LeftButton == ButtonState.Released;
        bool rawLeftDown = rawMs.LeftButton == ButtonState.Pressed;
        bool rawLeftPressed = rawLeftDown && _prevRawMouse.LeftButton == ButtonState.Released;
        int wheel = ms.ScrollWheelValue - _prevMouse.ScrollWheelValue;

        if (wheel != 0)
        {
            int step = wheel > 0 ? -1 : 1;
            if (_clipPickerOpen && _project != null)
            {
                _clipPickerScroll = Math.Clamp(_clipPickerScroll + step * 2, 0, Math.Max(0, _project.AvailableClips.Count - 4));
            }
            else if (!AnyModalOpen)
            {
                if (_libraryListRect.Contains(ms.Position))
                {
                    _libraryScroll = Math.Clamp(_libraryScroll + step, 0, Math.Max(0, _library.Count - 3));
                }
                else if ((_slotsListRect.Contains(ms.Position) || _rightPanelRect.Contains(ms.Position)) && _project != null)
                {
                    _slotScroll = Math.Clamp(_slotScroll + step, 0, Math.Max(0, _project.Slots.Count - 3));
                }
                else if (_stageRect.Contains(ms.Position) && !PointerInput.IsTouch)
                {
                    float factor = wheel > 0 ? 1.12f : (1f / 1.12f);
                    _viewZoom = Math.Clamp(_viewZoom * factor, 0.15f, 4.0f);
                }
            }
        }

        // Hold-to-repeat for +/-/arrow stepper buttons (works smoothly on both desktop mouse and Android touch)
        if (rawLeftPressed)
        {
            for (int i = _clickTargets.Count - 1; i >= 0; i--)
            {
                if (_clickTargets[i].Repeatable && _clickTargets[i].Rect.Contains(rawMs.Position))
                {
                    _draggingOffset = false;
                    _panningStage = false;
                    _heldRepeatRect = _clickTargets[i].Rect;
                    _heldRepeatAction = _clickTargets[i].OnClick;
                    _heldRepeatTimer = RepeatInitialDelay;
                    _suppressNextReleaseClick = PointerInput.IsTouch;
                    _heldRepeatAction();
                    return;
                }
            }
        }

        if (_heldRepeatAction != null)
        {
            if (rawLeftDown)
            {
                var tol = _heldRepeatRect;
                tol.Inflate(8, 8);
                if (tol.Contains(rawMs.Position))
                {
                    _heldRepeatTimer -= dt;
                    while (_heldRepeatTimer <= 0f)
                    {
                        _heldRepeatAction();
                        _heldRepeatTimer += RepeatInterval;
                    }
                }
                return;
            }
            _heldRepeatAction = null;
        }

        if (leftClick && _suppressNextReleaseClick)
        {
            _suppressNextReleaseClick = false;
            return;
        }

        // Check registered UI buttons (highest z-order wins)
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

        if (AnyModalOpen)
            return;

        // Stage viewport drag: Left-drag = adjust active animation offset; Right/Middle-drag = pan camera.
        // Uses rawMs so holding and dragging a finger on Android moves the character pose live without
        // interfering with button taps (which start inside _clickTargets).
        if (_stageCanvasRect.Contains(rawMs.Position) && _project != null && !IsOverClickTarget(rawMs.Position))
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
        _hoverTooltip = null;
        sb.Draw(_pixel, new Rectangle(0, 0, sw, sh), BG_DARK);

        DrawStageViewport(sb);
        DrawLeftSidebar(sb);
        DrawRightSidebar(sb);
        DrawHeaderBar(sb);

        if (_clipPickerOpen && _project != null)
            DrawClipPickerModal(sb, sw, sh);

        if (_importModalOpen)
            DrawImportModal(sb, sw, sh);

        if (_confirmDiscardOpen)
            DrawConfirmDiscardModal(sb, sw, sh);

        if (_importTask != null)
            DrawImportBusyOverlay(sb, sw, sh);

        if (_toastText != null)
            DrawToast(sb, sw, sh);

        if (!PointerInput.IsTouch && !string.IsNullOrEmpty(_hoverTooltip) && _importTask == null)
            DrawTooltip(sb, sw, sh, _hoverTooltip!, _hoverTooltipRect);
    }

    private void DrawHeaderBar(SpriteBatch sb)
    {
        float s = _uiScale;
        sb.Draw(_pixel, _headerRect, BG_PANEL);
        sb.Draw(_pixel, new Rectangle(0, _headerRect.Bottom - 2, _headerRect.Width, 2), ACCENT_PURPLE * 0.75f);

        int pad = UiScale.Px(10, s);
        int btnH = _headerRect.Height - pad * 2;
        int fontBtn = UiScale.FontPx(12, s);

        var backRect = new Rectangle(pad, pad, UiScale.Px(88, s), btnH);
        DrawBtn(sb, backRect, PointerInput.IsTouch ? "Back" : "< ESC",
            BG_CARD, Color.White, fontBtn, Color.White * 0.2f,
            RequestLeaveEditor,
            tooltip: "Return to previous menu (Esc)");

        // Right-side clean action buttons: IMPORT + SAVE
        int rx = _headerRect.Right - pad;
        int saveW = UiScale.Px(112, s);
        rx -= saveW;
        var saveRect = new Rectangle(rx, pad, saveW, btnH);
        bool canSave = _project != null;
        DrawBtn(sb, saveRect, _modified ? "SAVE CHAR *" : "SAVE CHAR",
            canSave ? (_modified ? new Color(36, 132, 84) : new Color(28, 92, 62)) : BG_CARD,
            canSave ? Color.White : Color.Gray, fontBtn,
            canSave ? (_modified ? ACCENT_GREEN : ACCENT_GREEN * 0.65f) : Color.White * 0.1f,
            () => SaveCurrentCharacter(),
            tooltip: "Save character to mods/characters (Ctrl+S)");

        int gap = UiScale.Px(8, s);
        int importW = UiScale.Px(114, s);
        rx -= importW + gap;
        DrawBtn(sb, new Rectangle(rx, pad, importW, btnH), "IMPORT",
            new Color(38, 36, 68), ACCENT_CYAN, fontBtn, ACCENT_CYAN * 0.65f,
            () => { _importModalOpen = true; UiSfx.Play(UiSound.SubTab); },
            tooltip: "Import Sparrow XML, Animate Spritemap/ZIP, or .FNMM (I)");

        // Title / editable character name box (clamped cleanly so it never overlaps right buttons)
        int nameX = backRect.Right + UiScale.Px(12, s);
        int availW = Math.Max(0, rx - nameX - UiScale.Px(12, s));

        if (_project != null && availW >= UiScale.Px(110, s))
        {
            string badge = _project.SourceKind switch
            {
                CharacterSourceKind.AnimateSpritemap => "SPRITEMAP",
                CharacterSourceKind.FnmmSprite => "FNMM",
                _ => "SPARROW",
            };
            Color badgeCol = _project.SourceKind switch
            {
                CharacterSourceKind.AnimateSpritemap => ACCENT_PURPLE,
                CharacterSourceKind.FnmmSprite => ACCENT_GOLD,
                _ => ACCENT_CYAN,
            };
            var bSz = MeasureText(badge, UiScale.FontPx(11, s));
            int badgeW = (int)bSz.X + UiScale.Px(16, s);
            bool showBadge = availW >= UiScale.Px(220, s) + badgeW;

            int maxNameW = showBadge ? availW - badgeW - UiScale.Px(8, s) : availW;
            var nameRect = new Rectangle(nameX, pad, Math.Min(UiScale.Px(320, s), maxNameW), btnH);

            bool hovName = !AnyModalOpen && nameRect.Contains(_prevMouse.Position);
            sb.Draw(_pixel, nameRect, _editingName ? new Color(48, 42, 74) : (hovName ? new Color(28, 26, 42) : BG_SECTION));
            DrawBorder(sb, nameRect, _editingName ? ACCENT_GOLD : (hovName ? ACCENT_PURPLE : Color.White * 0.18f));

            var editIcon = GetIcon("edit");
            int editSz = Math.Clamp((int)(nameRect.Height * 0.54f), 12, 22);
            int rightReserve = (editIcon != null && !_editingName) ? (editSz + UiScale.Px(14, s)) : UiScale.Px(10, s);
            int nameFont = UiScale.FontPx(14, s);
            string rawShown = _editingName ? (_nameBuffer + "_") : _project.Name;
            string shown = TruncateText(rawShown, Math.Max(40, nameRect.Width - UiScale.Px(12, s) - rightReserve), nameFont);
            DrawText(sb, shown, new Vector2(nameRect.X + UiScale.Px(10, s), nameRect.Y + (nameRect.Height - MeasureText(shown, nameFont).Y) / 2f),
                _editingName ? ACCENT_GOLD : Color.White, nameFont);

            if (editIcon != null && !_editingName)
            {
                var editRect = new Rectangle(nameRect.Right - editSz - UiScale.Px(8, s), nameRect.Y + (nameRect.Height - editSz) / 2, editSz, editSz);
                sb.Draw(editIcon, editRect, hovName ? ACCENT_GOLD : Color.White * 0.6f);
            }

            if (hovName && !_editingName)
                SetTooltip("Click to rename character", nameRect);
            _clickTargets.Add((nameRect, BeginRenameCharacter, false));

            if (showBadge)
            {
                int bx = nameRect.Right + UiScale.Px(8, s);
                var bRect = new Rectangle(bx, nameRect.Y + UiScale.Px(4, s), badgeW, nameRect.Height - UiScale.Px(8, s));
                sb.Draw(_pixel, bRect, badgeCol * 0.18f);
                DrawBorder(sb, bRect, badgeCol * 0.6f);
                DrawTextCentered(sb, badge, bRect, badgeCol, UiScale.FontPx(11, s));
            }
        }
        else if (availW >= UiScale.Px(120, s))
        {
            int titleFont = UiScale.FontPx(17, s);
            var tSz = MeasureText("CHARACTER EDITOR", titleFont);
            DrawText(sb, "CHARACTER EDITOR", new Vector2(nameX, _headerRect.Y + (_headerRect.Height - tSz.Y) / 2f), ACCENT_GOLD, titleFont);
            if (availW >= tSz.X + UiScale.Px(200, s))
            {
                string sub = "Sparrow XML  •  Spritemap  •  .FNMM";
                int subFont = UiScale.FontPx(11, s);
                var sSz = MeasureText(sub, subFont);
                DrawText(sb, sub, new Vector2(nameX + tSz.X + UiScale.Px(14, s), _headerRect.Y + (_headerRect.Height - sSz.Y) / 2f), Color.White * 0.45f, subFont);
            }
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

        int hdrFont = UiScale.FontPx(11, s);
        int smallFont = UiScale.FontPx(12, s);
        int tinyFont = UiScale.FontPx(10, s);

        // ── 1. Saved Characters Library ──────────────────────────────────────
        int scanW = UiScale.Px(72, s);
        int hdrH = UiScale.Px(22, s);
        DrawText(sb, $"CHARACTERS ({_library.Count})", new Vector2(x, y + UiScale.Px(3, s)), ACCENT_GOLD, hdrFont);
        var refreshBtn = new Rectangle(x + w - scanW, y, scanW, hdrH);
        DrawBtn(sb, refreshBtn, "SCAN", BG_CARD, ACCENT_CYAN, tinyFont, ACCENT_CYAN * 0.45f, () =>
        {
            RefreshLibrary();
            ShowToast($"Scanned {_library.Count} character(s)", ACCENT_CYAN);
        }, tooltip: "Rescan mods/characters folder");
        y += hdrH + UiScale.Px(6, s);

        int listH = Math.Clamp((int)(_leftPanelRect.Height * 0.30f), UiScale.Px(112, s), UiScale.Px(220, s));
        _libraryListRect = new Rectangle(x, y, w, listH);
        sb.Draw(_pixel, _libraryListRect, BG_SECTION);
        DrawBorder(sb, _libraryListRect, Color.White * 0.12f);

        int rowH = UiScale.Px(30, s);
        int visibleRows = Math.Max(1, (listH - UiScale.Px(6, s)) / rowH);
        _libraryScroll = Math.Clamp(_libraryScroll, 0, Math.Max(0, _library.Count - visibleRows));
        bool libNeedsScroll = _library.Count > visibleRows;
        int scrollBarW = libNeedsScroll ? UiScale.Px(8, s) : 0;

        if (_library.Count == 0)
        {
            DrawTextCentered(sb, "No saved characters yet.\nClick IMPORT above to add one!", _libraryListRect, Color.White * 0.45f, UiScale.FontPx(11, s));
        }
        else
        {
            int itemW = w - 6 - scrollBarW;
            for (int i = 0; i < visibleRows && (_libraryScroll + i) < _library.Count; i++)
            {
                var entry = _library[_libraryScroll + i];
                var rr = new Rectangle(x + 3, y + 3 + i * rowH, itemW, rowH - 2);
                bool active = _project != null && string.Equals(_project.Slug, entry.Slug, StringComparison.OrdinalIgnoreCase);
                bool hov = !AnyModalOpen && rr.Contains(_prevMouse.Position);

                sb.Draw(_pixel, rr, active ? ACCENT_PURPLE * 0.28f : (hov ? Color.Lerp(BG_CARD, Color.White, 0.08f) : BG_CARD));
                DrawBorder(sb, rr, active ? ACCENT_PURPLE : (hov ? Color.White * 0.25f : Color.White * 0.07f));
                if (active)
                    sb.Draw(_pixel, new Rectangle(rr.X, rr.Y, UiScale.Px(3, s, 2), rr.Height), ACCENT_PURPLE);

                var rowIcon = GetIcon(entry.IsRawSourceFolder ? "folder_open" : "character");
                int icSz = Math.Clamp((int)(rr.Height * 0.56f), 12, 20);
                int textLeft = rr.X + UiScale.Px(7, s);
                if (rowIcon != null)
                {
                    sb.Draw(rowIcon, new Rectangle(textLeft, rr.Y + (rr.Height - icSz) / 2, icSz, icSz), active ? Color.White : Color.White * 0.65f);
                    textLeft += icSz + UiScale.Px(5, s);
                }

                string badgeTxt = entry.IsRawSourceFolder ? entry.RawSourceHint : (entry.HasIcon ? "ICO" : "");
                int badgeReserve = 0;
                if (!string.IsNullOrEmpty(badgeTxt))
                {
                    var bSz = MeasureText(badgeTxt, tinyFont);
                    int bw = (int)bSz.X + UiScale.Px(8, s);
                    int bh = rr.Height - UiScale.Px(8, s);
                    var br = new Rectangle(rr.Right - bw - UiScale.Px(4, s), rr.Y + (rr.Height - bh) / 2, bw, bh);
                    Color bc = entry.IsRawSourceFolder ? ACCENT_GOLD : ACCENT_GREEN;
                    sb.Draw(_pixel, br, bc * 0.16f);
                    DrawBorder(sb, br, bc * 0.45f);
                    DrawTextCentered(sb, badgeTxt, br, bc, tinyFont);
                    badgeReserve = bw + UiScale.Px(8, s);
                }

                string lbl = TruncateText(entry.Name, Math.Max(30, rr.Right - textLeft - badgeReserve - UiScale.Px(4, s)), smallFont);
                var lSz = MeasureText(lbl, smallFont);
                DrawText(sb, lbl, new Vector2(textLeft, rr.Y + (rr.Height - lSz.Y) / 2f), active ? Color.White : Color.White * 0.85f, smallFont);

                var captured = entry;
                _clickTargets.Add((rr, () =>
                {
                    if (_project != null && string.Equals(_project.Slug, captured.Slug, StringComparison.OrdinalIgnoreCase))
                        return;
                    RequestDiscardAware($"Switch to '{captured.Name}'? Unsaved changes to '{_project?.Name}' will be lost.",
                        () => StartLoadFromPath(captured.DirectoryPath, $"Loading {captured.Name}..."));
                }, false));
            }

            if (libNeedsScroll)
                DrawScrollbar(sb, new Rectangle(_libraryListRect.Right - scrollBarW - 1, _libraryListRect.Y + 3, scrollBarW - 2, _libraryListRect.Height - 6),
                    _libraryScroll, visibleRows, _library.Count, ACCENT_PURPLE);
        }
        y += listH + UiScale.Px(10, s);

        if (_project == null)
        {
            var hintCard = new Rectangle(x, y, w, Math.Min(UiScale.Px(110, s), Math.Max(0, _leftPanelRect.Bottom - pad - y)));
            if (hintCard.Height >= UiScale.Px(64, s))
            {
                sb.Draw(_pixel, hintCard, BG_SECTION);
                DrawBorder(sb, hintCard, Color.White * 0.08f);
                DrawTextCentered(sb, "Supported formats:\nSparrow v2 (XML + PNG)\nAdobe Animate Spritemap\n.ZIP packs & .FNMM levels", hintCard, Color.White * 0.48f, UiScale.FontPx(11, s));
            }
            return;
        }

        // ── 2. Character Settings Card ───────────────────────────────────────
        int secPad = UiScale.Px(8, s);
        int ctrlH = UiScale.Px(27, s);
        int rowGap = UiScale.Px(6, s);
        int settingsCardH = secPad * 2 + UiScale.Px(18, s) + ctrlH * 4 + rowGap * 3;
        var settingsCard = new Rectangle(x, y, w, settingsCardH);
        sb.Draw(_pixel, settingsCard, BG_SECTION);
        DrawBorder(sb, settingsCard, Color.White * 0.10f);

        int ix = x + secPad;
        int iw = w - secPad * 2;
        int iy = y + secPad;

        DrawText(sb, "CHARACTER SETTINGS", new Vector2(ix, iy), ACCENT_GOLD, hdrFont);
        iy += UiScale.Px(18, s);

        int stepW = UiScale.Px(28, s);
        int resetW = UiScale.Px(34, s);
        int gap = UiScale.Px(4, s);
        bool shiftHeld = _prevKb.IsKeyDown(Keys.LeftShift) || _prevKb.IsKeyDown(Keys.RightShift);

        // Scale row: label + 1x reset + - + + (hold to repeat!)
        DrawText(sb, $"Scale: {_project.Scale:F2}x", new Vector2(ix, iy + (ctrlH - MeasureText("Ay", smallFont).Y) / 2f), Color.White, smallFont);
        int rxCtrl = ix + iw;
        rxCtrl -= stepW;
        DrawBtn(sb, new Rectangle(rxCtrl, iy, stepW, ctrlH), "+", BG_CARD, Color.White, smallFont, Color.White * 0.15f, () =>
        {
            float delta = shiftHeld ? 0.25f : 0.05f;
            _project.Scale = Math.Clamp(MathF.Round(_project.Scale + delta, 2), 0.10f, 6.0f);
            _modified = true;
        }, tooltip: "Increase scale (+0.05x, hold to repeat, Shift: +0.25x)", repeatable: true);

        rxCtrl -= stepW + gap;
        DrawBtn(sb, new Rectangle(rxCtrl, iy, stepW, ctrlH), "-", BG_CARD, Color.White, smallFont, Color.White * 0.15f, () =>
        {
            float delta = shiftHeld ? 0.25f : 0.05f;
            _project.Scale = Math.Clamp(MathF.Round(_project.Scale - delta, 2), 0.10f, 6.0f);
            _modified = true;
        }, tooltip: "Decrease scale (-0.05x, hold to repeat, Shift: -0.25x)", repeatable: true);

        rxCtrl -= resetW + gap;
        bool isOneX = MathF.Abs(_project.Scale - 1.0f) < 0.005f;
        DrawBtn(sb, new Rectangle(rxCtrl, iy, resetW, ctrlH), "1x",
            isOneX ? BG_CARD * 0.6f : BG_CARD, isOneX ? Color.White * 0.4f : ACCENT_GOLD, tinyFont,
            isOneX ? Color.White * 0.08f : ACCENT_GOLD * 0.4f, () =>
            {
                _project.Scale = 1.0f;
                _modified = true;
            }, tooltip: "Reset scale to 1.00x");
        iy += ctrlH + rowGap;

        // Flip Artwork + Swap Left/Right row
        int halfW = (iw - gap) / 2;
        DrawBtn(sb, new Rectangle(ix, iy, halfW, ctrlH),
            _project.BakeFlipX ? "FLIP X: ON" : "FLIP X: OFF",
            _project.BakeFlipX ? new Color(86, 42, 98) : BG_CARD,
            _project.BakeFlipX ? ACCENT_GOLD : Color.White, UiScale.FontPx(11, s),
            _project.BakeFlipX ? ACCENT_GOLD : Color.White * 0.15f,
            () =>
            {
                _project.BakeFlipX = !_project.BakeFlipX;
                _project.IconFlipX = !_project.IconFlipX;
                _project.NativeFacing = _project.NativeFacing == CharacterFacing.Left ? CharacterFacing.Right : CharacterFacing.Left;
                _texturesDirty = true;
                _modified = true;
            }, tooltip: "Horizontally flip character artwork (and icon)");

        DrawBtn(sb, new Rectangle(ix + halfW + gap, iy, iw - halfW - gap, ctrlH),
            "SWAP L<->R", BG_CARD, ACCENT_CYAN, UiScale.FontPx(11, s), ACCENT_CYAN * 0.45f,
            SwapLeftRightSlots, tooltip: "Swap Sing Left and Sing Right slots");
        iy += ctrlH + rowGap;

        // Native Facing direction
        string faceLbl = _project.NativeFacing == CharacterFacing.Left
            ? "FACES: <- LEFT (BF)"
            : "FACES: RIGHT (FOE) ->";
        DrawBtn(sb, new Rectangle(ix, iy, iw, ctrlH), faceLbl,
            BG_CARD, ACCENT_GREEN, UiScale.FontPx(11, s), ACCENT_GREEN * 0.45f,
            () =>
            {
                _project.NativeFacing = _project.NativeFacing == CharacterFacing.Left ? CharacterFacing.Right : CharacterFacing.Left;
                _modified = true;
            }, tooltip: "Native facing direction (used to auto-flip in Chart Editor & Online)");
        iy += ctrlH + rowGap;

        // Filtering (Smooth Bilinear vs Pixel Point — live preview!)
        DrawBtn(sb, new Rectangle(ix, iy, iw, ctrlH),
            _project.Bilinear ? "FILTER: SMOOTH (HD)" : "FILTER: PIXEL ART",
            _project.Bilinear ? BG_CARD : new Color(36, 44, 66),
            _project.Bilinear ? Color.White : ACCENT_CYAN, UiScale.FontPx(11, s),
            _project.Bilinear ? Color.White * 0.16f : ACCENT_CYAN * 0.55f,
            () =>
            {
                _project.Bilinear = !_project.Bilinear;
                _modified = true;
            }, tooltip: "Texture filtering mode (applies live on stage and in-game)");

        y += settingsCardH + UiScale.Px(10, s);

        // ── 3. Health Bar Icon Card ──────────────────────────────────────────
        int icoBox = UiScale.Px(48, s);
        int icoCardH = secPad * 2 + UiScale.Px(18, s) + icoBox + UiScale.Px(14, s);
        if (y + icoCardH <= _leftPanelRect.Bottom - pad)
        {
            var icoCard = new Rectangle(x, y, w, icoCardH);
            sb.Draw(_pixel, icoCard, BG_SECTION);
            DrawBorder(sb, icoCard, Color.White * 0.10f);

            int cy = y + secPad;
            string icoDirHint = _project.NativeFacing == CharacterFacing.Left ? "HEALTH ICON (<- LEFT)" : "HEALTH ICON (RIGHT ->)";
            DrawText(sb, icoDirHint, new Vector2(ix, cy), ACCENT_GOLD, hdrFont);
            cy += UiScale.Px(18, s);

            var normBox = new Rectangle(ix, cy, icoBox, icoBox);
            var loseBox = new Rectangle(ix + icoBox + gap, cy, icoBox, icoBox);
            sb.Draw(_pixel, normBox, BG_DARK);
            sb.Draw(_pixel, loseBox, BG_DARK);
            DrawBorder(sb, normBox, ACCENT_GREEN * 0.55f);
            DrawBorder(sb, loseBox, ACCENT_PINK * 0.55f);

            if (_iconNormalTex != null && !_iconNormalTex.IsDisposed)
                DrawTextureFit(sb, _iconNormalTex, normBox, Color.White);
            else
                DrawTextCentered(sb, "NONE", normBox, Color.White * 0.35f, tinyFont);

            var loseTex = _iconLosingTex ?? _iconNormalTex;
            if (loseTex != null && !loseTex.IsDisposed)
                DrawTextureFit(sb, loseTex, loseBox, Color.White);
            else
                DrawTextCentered(sb, "NONE", loseBox, Color.White * 0.35f, tinyFont);

            // Subtle captions under icon boxes
            int capFont = UiScale.FontPx(9, s, min: 9);
            DrawTextCentered(sb, "NORMAL", new Rectangle(normBox.X, normBox.Bottom + 2, normBox.Width, UiScale.Px(12, s)), ACCENT_GREEN * 0.8f, capFont);
            DrawTextCentered(sb, "LOSING", new Rectangle(loseBox.X, loseBox.Bottom + 2, loseBox.Width, UiScale.Px(12, s)), ACCENT_PINK * 0.8f, capFont);

            int btnX = loseBox.Right + UiScale.Px(8, s);
            int btnW = Math.Max(UiScale.Px(74, s), ix + iw - btnX);
            int icoBtnH = (icoBox - gap) / 2;
            int clrW = UiScale.Px(28, s);
            int loadW = Math.Max(UiScale.Px(44, s), btnW - clrW - gap);

            DrawBtn(sb, new Rectangle(btnX, cy, loadW, icoBtnH), "LOAD ICON",
                BG_CARD, ACCENT_CYAN, UiScale.FontPx(10, s), ACCENT_CYAN * 0.5f, PromptLoadIcon,
                tooltip: "Load 150x150 or 300x150 2-frame health icon PNG");

            DrawBtn(sb, new Rectangle(btnX + loadW + gap, cy, btnW - loadW - gap, icoBtnH), "CLR_ICO",
                BG_CARD, Color.White * 0.7f, UiScale.FontPx(10, s), Color.White * 0.14f, () =>
                {
                    _project.IconNormal?.Dispose();
                    _project.IconLosing?.Dispose();
                    _project.IconNormal = null;
                    _project.IconLosing = null;
                    _project.IconFlipX = false;
                    _texturesDirty = true;
                    _modified = true;
                }, tooltip: "Remove custom health bar icon");

            bool hasIcon = _project.IconNormal != null;
            DrawBtn(sb, new Rectangle(btnX, cy + icoBtnH + gap, btnW, icoBox - icoBtnH - gap), "FLIP ICON",
                hasIcon && _project.IconFlipX ? new Color(86, 42, 98) : BG_CARD,
                hasIcon ? (_project.IconFlipX ? ACCENT_GOLD : Color.White) : Color.Gray,
                UiScale.FontPx(10, s),
                hasIcon ? (_project.IconFlipX ? ACCENT_GOLD : ACCENT_PURPLE * 0.55f) : Color.White * 0.08f, () =>
                {
                    if (_project.IconNormal == null) return;
                    _project.IconFlipX = !_project.IconFlipX;
                    _texturesDirty = true;
                    _modified = true;
                }, tooltip: "Flip health icon horizontally (auto-mirrors for Player vs Opponent in-game)");

            y += icoCardH + UiScale.Px(10, s);
        }

        // ── 4. Export Info Footer (if vertical room remains) ─────────────────
        int infoH = UiScale.Px(46, s);
        if (y + infoH <= _leftPanelRect.Bottom - pad)
        {
            var infoCard = new Rectangle(x, _leftPanelRect.Bottom - pad - infoH, w, infoH);
            sb.Draw(_pixel, infoCard, BG_SECTION * 0.85f);
            DrawBorder(sb, infoCard, Color.White * 0.08f);

            string pathLine = TruncateText($"mods/characters/{_project.Slug}", infoCard.Width - secPad * 2, tinyFont);
            string statsLine = $"{_project.AvailableClips.Count} clip(s)  •  {_project.Slots.Count} slot(s)  •  {(_modified ? "Unsaved" : "Saved")}";
            DrawText(sb, pathLine, new Vector2(infoCard.X + secPad, infoCard.Y + UiScale.Px(6, s)), Color.White * 0.65f, tinyFont);
            DrawText(sb, statsLine, new Vector2(infoCard.X + secPad, infoCard.Y + UiScale.Px(23, s)), _modified ? ACCENT_GOLD * 0.9f : ACCENT_GREEN * 0.8f, tinyFont);
        }
    }

    private void DrawStageViewport(SpriteBatch sb)
    {
        float s = _uiScale;
        int topBarH = _stageCanvasRect.Y - _stageRect.Y;
        int botBarH = _stageRect.Bottom - _stageCanvasRect.Bottom;

        // ── 1. Stage Canvas Background (Dark / Checker / Light) ──────────────
        if (_stageBgMode == 0)
        {
            sb.Draw(_pixel, _stageCanvasRect, new Color(18, 17, 28));
            int gridStep = Math.Max(24, (int)(48 * _viewZoom));
            for (int gx = _stageCanvasRect.X; gx < _stageCanvasRect.Right; gx += gridStep)
                sb.Draw(_pixel, new Rectangle(gx, _stageCanvasRect.Y, 1, _stageCanvasRect.Height), Color.White * 0.035f);
            for (int gy = _stageCanvasRect.Y; gy < _stageCanvasRect.Bottom; gy += gridStep)
                sb.Draw(_pixel, new Rectangle(_stageCanvasRect.X, gy, _stageCanvasRect.Width, 1), Color.White * 0.035f);
        }
        else
        {
            Color cA = _stageBgMode == 1 ? new Color(34, 33, 48) : new Color(186, 190, 202);
            Color cB = _stageBgMode == 1 ? new Color(46, 45, 64) : new Color(210, 214, 224);
            sb.Draw(_pixel, _stageCanvasRect, cA);
            int tile = UiScale.Px(24, s, 14);
            for (int gy = _stageCanvasRect.Y, row = 0; gy < _stageCanvasRect.Bottom; gy += tile, row++)
            {
                int th = Math.Min(tile, _stageCanvasRect.Bottom - gy);
                for (int gx = _stageCanvasRect.X + ((row & 1) * tile); gx < _stageCanvasRect.Right; gx += tile * 2)
                {
                    int tw = Math.Min(tile, _stageCanvasRect.Right - gx);
                    sb.Draw(_pixel, new Rectangle(gx, gy, tw, th), cB);
                }
            }
        }

        // Foot Anchor (0, 0) world point on screen
        Vector2 anchorScreen = new Vector2(
            _stageCanvasRect.X + _stageCanvasRect.Width * 0.5f + _viewPan.X,
            _stageCanvasRect.Y + _stageCanvasRect.Height * 0.78f + _viewPan.Y);

        int floorY = (int)anchorScreen.Y;
        int ax = (int)anchorScreen.X;
        Color floorCol = _stageBgMode == 2 ? new Color(180, 110, 0) * 0.65f : ACCENT_GOLD * 0.42f;
        Color axisCol = _stageBgMode == 2 ? new Color(0, 110, 160) * 0.45f : ACCENT_CYAN * 0.26f;

        if (floorY >= _stageCanvasRect.Y && floorY < _stageCanvasRect.Bottom)
            sb.Draw(_pixel, new Rectangle(_stageCanvasRect.X, floorY, _stageCanvasRect.Width, 2), floorCol);
        if (ax >= _stageCanvasRect.X && ax < _stageCanvasRect.Right)
            sb.Draw(_pixel, new Rectangle(ax, _stageCanvasRect.Y, 1, _stageCanvasRect.Height), axisCol);

        // ── 2. Empty Stage Welcome Card (when no project loaded) ─────────────
        if (_project == null)
        {
            int cw = Math.Min(UiScale.Px(420, s), _stageCanvasRect.Width - UiScale.Px(32, s));
            int ch = UiScale.Px(190, s);
            var card = new Rectangle(
                _stageCanvasRect.X + (_stageCanvasRect.Width - cw) / 2,
                _stageCanvasRect.Y + (_stageCanvasRect.Height - ch) / 2,
                cw, ch);
            sb.Draw(_pixel, card, BG_PANEL * 0.94f);
            DrawBorder(sb, card, ACCENT_PURPLE * 0.65f);

            var ico = GetIcon("character");
            int icoSz = UiScale.Px(34, s);
            int cy = card.Y + UiScale.Px(18, s);
            if (ico != null)
            {
                sb.Draw(ico, new Rectangle(card.X + (card.Width - icoSz) / 2, cy, icoSz, icoSz), ACCENT_PURPLE);
                cy += icoSz + UiScale.Px(8, s);
            }
            DrawTextCentered(sb, "NO CHARACTER LOADED", new Rectangle(card.X, cy, card.Width, UiScale.Px(22, s)), ACCENT_GOLD, UiScale.FontPx(15, s));
            cy += UiScale.Px(24, s);
            DrawTextCentered(sb, "Import Sparrow XML+PNG, Adobe Animate Spritemap,\nor pick a saved character on the left.",
                new Rectangle(card.X + 12, cy, card.Width - 24, UiScale.Px(38, s)), Color.White * 0.7f, UiScale.FontPx(12, s));
            cy += UiScale.Px(46, s);

            int ctaW = UiScale.Px(180, s);
            int ctaH = UiScale.Px(34, s);
            DrawBtn(sb, new Rectangle(card.X + (card.Width - ctaW) / 2, cy, ctaW, ctaH),
                "IMPORT", new Color(36, 48, 86), ACCENT_CYAN, UiScale.FontPx(13, s), ACCENT_CYAN,
                () => { _importModalOpen = true; UiSfx.Play(UiSound.SubTab); });

            // Still draw clean empty docked bars so frame looks cohesive
            DrawStageBarsFrame(sb, topBarH, botBarH);
            return;
        }

        // ── 3. Clipped Sprite Draw (honors Smooth vs Pixel Art filtering!) ───
        var clipRect = Rectangle.Intersect(_stageCanvasRect, _gd.Viewport.Bounds);
        var activeSlot = GetActiveSlot();
        if (clipRect.Width > 0 && clipRect.Height > 0)
        {
            var prevScissor = _gd.ScissorRectangle;
            sb.End();
            _gd.ScissorRectangle = clipRect;
            sb.Begin(
                SpriteSortMode.Deferred,
                BlendState.AlphaBlend,
                _project.Bilinear ? SamplerState.LinearClamp : SamplerState.PointClamp,
                null,
                _scissorRasterizer);

            // Onion Skin ghost behind active pose
            if (_onionSkin && _onionRefSlot >= 0 && _onionRefSlot < _project.Slots.Count && _onionRefSlot != _selectedSlot)
            {
                Color ghostTint = _stageBgMode == 2 ? new Color(0, 120, 210) * 0.36f : new Color(0, 225, 255) * 0.34f;
                DrawSlotFrameOnStage(sb, _project.Slots[_onionRefSlot], 0, anchorScreen, ghostTint);
            }

            if (activeSlot != null)
            {
                int frameIdx = (int)Math.Max(0f, _animFrame);
                DrawSlotFrameOnStage(sb, activeSlot, frameIdx, anchorScreen, Color.White);
            }

            sb.End();
            _gd.ScissorRectangle = prevScissor;
            sb.Begin(samplerState: SamplerState.LinearClamp);
        }

        // Bright Foot Pivot Crosshair on top (only inside canvas)
        if (_stageCanvasRect.Contains(ax, floorY))
        {
            int cross = UiScale.Px(8, s);
            sb.Draw(_pixel, new Rectangle(ax - cross, floorY - 1, cross * 2 + 1, 3), ACCENT_GOLD);
            sb.Draw(_pixel, new Rectangle(ax - 1, floorY - cross, 3, cross * 2 + 1), ACCENT_GOLD);
        }

        // Floating Stage Status Pill (top-left of canvas)
        if (activeSlot != null)
        {
            string statusTxt = $"{activeSlot.Label}  •  Nudge ({activeSlot.UserOffsetX:+0.#;-0.#;0}, {activeSlot.UserOffsetY:+0.#;-0.#;0}) px";
            int sf = UiScale.FontPx(11, s);
            var sSz = MeasureText(statusTxt, sf);
            var pill = new Rectangle(_stageCanvasRect.X + UiScale.Px(10, s), _stageCanvasRect.Y + UiScale.Px(8, s),
                (int)sSz.X + UiScale.Px(16, s), UiScale.Px(22, s));
            sb.Draw(_pixel, pill, BG_BAR * 0.84f);
            DrawBorder(sb, pill, _draggingOffset ? ACCENT_GOLD : Color.White * 0.16f);
            DrawTextCentered(sb, statusTxt, pill, _draggingOffset ? ACCENT_GOLD : Color.White * 0.88f, sf);
        }

        // Visible shortcut hint right above bottom bar
        string hint = PointerInput.IsTouch
            ? "Drag stage: Align pose  •  Tap L/D/U/R below: Test poses"
            : "Drag: Align Pose  •  Arrows: Nudge (Shift 5px)  •  D/F/J/K: Sing  •  R-Drag: Pan";
        int hf = UiScale.FontPx(10, s);
        var hSz = MeasureText(hint, hf);
        if (hSz.X + UiScale.Px(16, s) <= _stageCanvasRect.Width)
        {
            var hintPill = new Rectangle(
                _stageCanvasRect.X + (_stageCanvasRect.Width - (int)hSz.X - UiScale.Px(16, s)) / 2,
                _stageCanvasRect.Bottom - UiScale.Px(22, s),
                (int)hSz.X + UiScale.Px(16, s),
                UiScale.Px(18, s));
            sb.Draw(_pixel, hintPill, BG_BAR * 0.72f);
            DrawTextCentered(sb, hint, hintPill, Color.White * 0.65f, hf);
        }

        // ── 4. Docked Top & Bottom Stage Toolbars ────────────────────────────
        DrawStageBarsFrame(sb, topBarH, botBarH);

        int pad = UiScale.Px(8, s);
        int bh = UiScale.Px(28, s);
        int barY = _stageRect.Y + (topBarH - bh) / 2;
        int bf = UiScale.FontPx(11, s);
        int bx = _stageRect.X + pad;

        // Top-left: Ghost, Play/Pause, Frame Stepper
        int ghostW = UiScale.Px(90, s);
        DrawBtn(sb, new Rectangle(bx, barY, ghostW, bh),
            _onionSkin ? "GHOST: ON" : "GHOST: OFF",
            _onionSkin ? new Color(18, 68, 86) : BG_CARD,
            _onionSkin ? ACCENT_CYAN : Color.White * 0.6f, bf,
            _onionSkin ? ACCENT_CYAN : Color.White * 0.15f,
            () => { _onionSkin = !_onionSkin; UiSfx.Play(UiSound.Toggle); },
            tooltip: "Toggle Onion Skin ghost reference (G)");
        bx += ghostW + UiScale.Px(6, s);

        int playW = UiScale.Px(80, s);
        DrawBtn(sb, new Rectangle(bx, barY, playW, bh),
            _isPlaying ? "PAUSE" : "PLAY",
            _isPlaying ? new Color(102, 74, 22) : new Color(26, 86, 56),
            Color.White, bf, _isPlaying ? ACCENT_GOLD : ACCENT_GREEN,
            () => { _isPlaying = !_isPlaying; if (_isPlaying) _animFrame = 0f; },
            tooltip: "Play / Pause animation (Space)");
        bx += playW + UiScale.Px(6, s);

        var curClip = activeSlot != null ? _project.FindClip(activeSlot.ClipName) : null;
        int totalFrames = curClip?.Frames.Count ?? 1;
        int shownFrame = Math.Clamp((int)_animFrame + 1, 1, Math.Max(1, totalFrames));
        int stepBtn = UiScale.Px(26, s);
        DrawBtn(sb, new Rectangle(bx, barY, stepBtn, bh), "<", BG_CARD, Color.White, bf, Color.White * 0.15f, () =>
        {
            _isPlaying = false;
            _animFrame = ((int)_animFrame - 1 + totalFrames) % Math.Max(1, totalFrames);
        }, tooltip: "Previous frame (hold to step)", repeatable: true);
        bx += stepBtn + 2;

        var fBox = new Rectangle(bx, barY, UiScale.Px(54, s), bh);
        sb.Draw(_pixel, fBox, BG_SECTION);
        DrawBorder(sb, fBox, Color.White * 0.10f);
        DrawTextCentered(sb, $"{shownFrame}/{totalFrames}", fBox, Color.White, bf);
        bx += fBox.Width + 2;

        DrawBtn(sb, new Rectangle(bx, barY, stepBtn, bh), ">", BG_CARD, Color.White, bf, Color.White * 0.15f, () =>
        {
            _isPlaying = false;
            _animFrame = ((int)_animFrame + 1) % Math.Max(1, totalFrames);
        }, tooltip: "Next frame (hold to step)", repeatable: true);

        // Top-right: BG toggle, Zoom out / % / Zoom in / FIT
        int zrx = _stageRect.Right - pad;
        int fitW = UiScale.Px(58, s);
        zrx -= fitW;
        DrawBtn(sb, new Rectangle(zrx, barY, fitW, bh), "FIT", BG_CARD, Color.White, bf, Color.White * 0.18f,
            FitCameraToCharacter, tooltip: "Reset pan and fit character to viewport");

        zrx -= stepBtn + 4;
        DrawBtn(sb, new Rectangle(zrx, barY, stepBtn, bh), "ZOOM+", BG_CARD, Color.White, bf, Color.White * 0.15f,
            () => _viewZoom = Math.Clamp(_viewZoom * 1.12f, 0.15f, 4f), tooltip: "Zoom in (hold to repeat)", repeatable: true);

        int zoomReadW = UiScale.Px(44, s);
        zrx -= zoomReadW + 2;
        var zBox = new Rectangle(zrx, barY, zoomReadW, bh);
        sb.Draw(_pixel, zBox, BG_SECTION);
        DrawBorder(sb, zBox, Color.White * 0.10f);
        DrawTextCentered(sb, $"{(int)MathF.Round(_viewZoom * 100f)}%", zBox, Color.White * 0.85f, UiScale.FontPx(10, s));

        zrx -= stepBtn + 2;
        DrawBtn(sb, new Rectangle(zrx, barY, stepBtn, bh), "ZOOM-", BG_CARD, Color.White, bf, Color.White * 0.15f,
            () => _viewZoom = Math.Clamp(_viewZoom / 1.12f, 0.15f, 4f), tooltip: "Zoom out (hold to repeat)", repeatable: true);

        int bgW = UiScale.Px(62, s);
        zrx -= bgW + UiScale.Px(8, s);
        if (zrx > bx + stepBtn + 8)
        {
            string bgLbl = _stageBgMode switch { 1 => "CHECK", 2 => "LIGHT", _ => "DARK" };
            DrawBtn(sb, new Rectangle(zrx, barY, bgW, bh), bgLbl,
                _stageBgMode == 2 ? new Color(74, 78, 92) : BG_CARD,
                _stageBgMode == 2 ? ACCENT_GOLD : Color.White * 0.85f, UiScale.FontPx(10, s),
                Color.White * 0.18f, CycleStageBackground,
                tooltip: "Cycle stage backdrop: Dark / Checker / Light (B)");
        }

        // Bottom Bar: Left = Directional Nudge Arrows + ZERO; Right = Quick Pose Test Pills
        int nbH = UiScale.Px(28, s);
        int botY = _stageCanvasRect.Bottom + (botBarH - nbH) / 2;
        int nbW = UiScale.Px(30, s);
        int nbx = _stageRect.X + pad;

        DrawBtn(sb, new Rectangle(nbx, botY, nbW, nbH), "NUDGE_L", BG_CARD, Color.White, bf, Color.White * 0.18f,
            () => NudgeSlotOffset(-2f, 0f), tooltip: "Nudge left 2px (hold to repeat)", repeatable: true);
        nbx += nbW + 3;
        DrawBtn(sb, new Rectangle(nbx, botY, nbW, nbH), "NUDGE_U", BG_CARD, Color.White, bf, Color.White * 0.18f,
            () => NudgeSlotOffset(0f, -2f), tooltip: "Nudge up 2px (hold to repeat)", repeatable: true);
        nbx += nbW + 3;
        DrawBtn(sb, new Rectangle(nbx, botY, nbW, nbH), "NUDGE_D", BG_CARD, Color.White, bf, Color.White * 0.18f,
            () => NudgeSlotOffset(0f, 2f), tooltip: "Nudge down 2px (hold to repeat)", repeatable: true);
        nbx += nbW + 3;
        DrawBtn(sb, new Rectangle(nbx, botY, nbW, nbH), "NUDGE_R", BG_CARD, Color.White, bf, Color.White * 0.18f,
            () => NudgeSlotOffset(2f, 0f), tooltip: "Nudge right 2px (hold to repeat)", repeatable: true);
        nbx += nbW + 6;

        int zeroW = UiScale.Px(74, s);
        DrawBtn(sb, new Rectangle(nbx, botY, zeroW, nbH), "ZERO OFF", BG_CARD, ACCENT_GOLD, bf, ACCENT_GOLD * 0.45f, () =>
        {
            var sl = GetActiveSlot();
            if (sl != null) { sl.UserOffsetX = 0f; sl.UserOffsetY = 0f; _modified = true; }
        }, tooltip: "Reset active slot nudge to (0, 0)");
        nbx += zeroW + UiScale.Px(10, s);

        // Quick Pose Test Pills on bottom-right (IDLE, L, D, U, R)
        int poseGap = UiScale.Px(4, s);
        int idleBtnW = UiScale.Px(48, s);
        int dirBtnW = UiScale.Px(32, s);
        int totalPosesW = idleBtnW + dirBtnW * 4 + poseGap * 4;
        if (_stageRect.Right - pad - totalPosesW >= nbx)
        {
            int px = _stageRect.Right - pad - totalPosesW;
            for (int pi = 0; pi < 5 && pi < _project.Slots.Count; pi++)
            {
                int pw = pi == 0 ? idleBtnW : dirBtnW;
                bool sel = _selectedSlot == pi;
                Color acc = SlotAccentColors[pi];
                int captured = pi;
                DrawBtn(sb, new Rectangle(px, botY, pw, nbH), QuickPoseShortLabels[pi],
                    sel ? acc * 0.30f : BG_CARD,
                    sel ? Color.White : acc,
                    UiScale.FontPx(10, s),
                    sel ? acc : acc * 0.35f,
                    () => SelectSlot(captured, replay: true),
                    tooltip: $"Test {_project.Slots[pi].Label} pose (key {pi + 1})");
                px += pw + poseGap;
            }
        }
    }

    private void DrawStageBarsFrame(SpriteBatch sb, int topBarH, int botBarH)
    {
        var topBar = new Rectangle(_stageRect.X, _stageRect.Y, _stageRect.Width, topBarH);
        var botBar = new Rectangle(_stageRect.X, _stageCanvasRect.Bottom, _stageRect.Width, botBarH);
        sb.Draw(_pixel, topBar, BG_BAR * 0.94f);
        sb.Draw(_pixel, new Rectangle(topBar.X, topBar.Bottom - 1, topBar.Width, 1), Color.White * 0.10f);
        sb.Draw(_pixel, botBar, BG_BAR * 0.94f);
        sb.Draw(_pixel, new Rectangle(botBar.X, botBar.Y, botBar.Width, 1), Color.White * 0.10f);
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

        int pad = UiScale.Px(10, s);
        int x = _rightPanelRect.X + pad;
        int w = _rightPanelRect.Width - pad * 2;
        int y = _rightPanelRect.Y + pad;

        if (_project == null)
        {
            var emptyBox = new Rectangle(x, y, w, UiScale.Px(150, s));
            sb.Draw(_pixel, emptyBox, BG_SECTION);
            DrawBorder(sb, emptyBox, Color.White * 0.08f);
            DrawTextCentered(sb, "ANIMATION SLOTS\n\nLoad or import a character\nto map Idle / Sing clips,\nFPS, loops & offsets.",
                emptyBox, Color.White * 0.45f, UiScale.FontPx(11, s));
            return;
        }

        int hdrFont = UiScale.FontPx(11, s);
        int smallFont = UiScale.FontPx(12, s);
        int tinyFont = UiScale.FontPx(10, s);

        // ── 1. Animation Slots List ──────────────────────────────────────────
        int hdrH = UiScale.Px(22, s);
        DrawText(sb, $"ANIMATION SLOTS ({_project.Slots.Count})", new Vector2(x, y + UiScale.Px(3, s)), ACCENT_GOLD, hdrFont);
        int addW = UiScale.Px(82, s);
        DrawBtn(sb, new Rectangle(x + w - addW, y, addW, hdrH), "+ EXTRA",
            BG_CARD, ACCENT_GREEN, tinyFont, ACCENT_GREEN * 0.45f, AddExtraSlot,
            tooltip: "Add an extra event animation slot (slot 5+)");
        y += hdrH + UiScale.Px(6, s);

        int inspectorReserved = UiScale.Px(244, s);
        int availForSlots = Math.Max(UiScale.Px(140, s), _rightPanelRect.Bottom - pad - y - inspectorReserved - UiScale.Px(10, s));
        int slotH = UiScale.Px(38, s);
        int slotGap = UiScale.Px(4, s);
        int visSlots = Math.Max(3, (availForSlots - 6) / (slotH + slotGap));
        int listBoxH = Math.Min(availForSlots, Math.Max(visSlots, Math.Min(_project.Slots.Count, visSlots)) * (slotH + slotGap) + 6);

        _slotsListRect = new Rectangle(x, y, w, listBoxH);
        sb.Draw(_pixel, _slotsListRect, BG_SECTION);
        DrawBorder(sb, _slotsListRect, Color.White * 0.10f);

        _slotScroll = Math.Clamp(_slotScroll, 0, Math.Max(0, _project.Slots.Count - visSlots));
        bool slotsNeedScroll = _project.Slots.Count > visSlots;
        int scrollBarW = slotsNeedScroll ? UiScale.Px(8, s) : 0;
        int rowW = w - 6 - scrollBarW;

        for (int vi = 0; vi < visSlots && (_slotScroll + vi) < _project.Slots.Count; vi++)
        {
            int idx = _slotScroll + vi;
            var slot = _project.Slots[idx];
            var sr = new Rectangle(x + 3, y + 3 + vi * (slotH + slotGap), rowW, slotH);
            bool sel = idx == _selectedSlot;
            bool hov = !AnyModalOpen && sr.Contains(_prevMouse.Position);
            Color accent = idx < SlotAccentColors.Length ? SlotAccentColors[idx] : ACCENT_PURPLE;

            sb.Draw(_pixel, sr, sel ? accent * 0.24f : (hov ? Color.Lerp(BG_CARD, Color.White, 0.08f) : BG_CARD));
            DrawBorder(sb, sr, sel ? accent : (hov ? Color.White * 0.22f : Color.White * 0.08f));
            sb.Draw(_pixel, new Rectangle(sr.X, sr.Y, UiScale.Px(4, s, 2), sr.Height), accent);

            var clipObj = _project.FindClip(slot.ClipName);
            string metaRight = clipObj != null ? $"{clipObj.Frames.Count}f · {slot.Fps:F0}fps" : "empty";
            var metaSz = MeasureText(metaRight, tinyFont);

            string topLine = $"{idx}: {slot.Label}" + (idx == _onionRefSlot && _onionSkin ? " [GHOST]" : "");
            topLine = TruncateText(topLine, Math.Max(40, sr.Width - (int)metaSz.X - UiScale.Px(20, s)), UiScale.FontPx(11, s));
            DrawText(sb, topLine, new Vector2(sr.X + UiScale.Px(10, s), sr.Y + UiScale.Px(3, s)), Color.White, UiScale.FontPx(11, s));
            DrawText(sb, metaRight, new Vector2(sr.Right - metaSz.X - UiScale.Px(6, s), sr.Y + UiScale.Px(4, s)),
                clipObj != null ? Color.White * 0.52f : ACCENT_PINK * 0.75f, tinyFont);

            bool hasNudge = MathF.Abs(slot.UserOffsetX) > 0.05f || MathF.Abs(slot.UserOffsetY) > 0.05f;
            string offTag = hasNudge ? $"({slot.UserOffsetX:+0;-0;0},{slot.UserOffsetY:+0;-0;0})" : "";
            int offReserve = hasNudge ? (int)MeasureText(offTag, tinyFont).X + UiScale.Px(10, s) : 0;

            string clipInfo = string.IsNullOrEmpty(slot.ClipName) ? "(unmapped)" : slot.ClipName;
            clipInfo = TruncateText(clipInfo, Math.Max(40, sr.Width - UiScale.Px(18, s) - offReserve), tinyFont);
            DrawText(sb, clipInfo, new Vector2(sr.X + UiScale.Px(10, s), sr.Y + UiScale.Px(20, s)), Color.White * 0.65f, tinyFont);

            if (hasNudge)
            {
                var oSz = MeasureText(offTag, tinyFont);
                DrawText(sb, offTag, new Vector2(sr.Right - oSz.X - UiScale.Px(6, s), sr.Y + UiScale.Px(20, s)), ACCENT_CYAN * 0.85f, tinyFont);
            }

            int captured = idx;
            _clickTargets.Add((sr, () => SelectSlot(captured, replay: true), false));
        }

        if (slotsNeedScroll)
            DrawScrollbar(sb, new Rectangle(_slotsListRect.Right - scrollBarW - 1, _slotsListRect.Y + 3, scrollBarW - 2, _slotsListRect.Height - 6),
                _slotScroll, visSlots, _project.Slots.Count, ACCENT_GOLD);

        y += listBoxH + UiScale.Px(10, s);

        // ── 2. Selected Slot Inspector Card ──────────────────────────────────
        var active = GetActiveSlot();
        if (active == null) return;

        int secPad = UiScale.Px(8, s);
        int inspH = Math.Max(UiScale.Px(215, s), _rightPanelRect.Bottom - pad - y);
        var inspCard = new Rectangle(x, y, w, inspH);
        sb.Draw(_pixel, inspCard, BG_SECTION);
        Color activeAccent = active.SlotIndex < SlotAccentColors.Length ? SlotAccentColors[active.SlotIndex] : ACCENT_PURPLE;
        DrawBorder(sb, inspCard, activeAccent * 0.55f);

        int ix = x + secPad;
        int iw = w - secPad * 2;
        int iy = y + secPad;

        DrawText(sb, $"SLOT {active.SlotIndex}: {active.Label}", new Vector2(ix, iy + UiScale.Px(2, s)), activeAccent, hdrFont);
        if (active.SlotIndex >= 5)
        {
            var delRect = new Rectangle(ix + iw - UiScale.Px(64, s), iy - 1, UiScale.Px(64, s), UiScale.Px(22, s));
            DrawBtn(sb, delRect, "DEL", new Color(110, 32, 48), Color.White, tinyFont, ACCENT_PINK, RemoveSelectedExtraSlot,
                tooltip: "Delete this extra slot");
        }
        iy += UiScale.Px(24, s);

        int ctrlH = UiScale.Px(28, s);
        int stepW = UiScale.Px(28, s);
        int gap = UiScale.Px(4, s);
        bool shiftHeld = _prevKb.IsKeyDown(Keys.LeftShift) || _prevKb.IsKeyDown(Keys.RightShift);

        // Clip selector row (<  ClipName  > + Browse popup)
        DrawText(sb, "Source Clip / Prefix:", new Vector2(ix, iy), Color.White * 0.7f, tinyFont);
        iy += UiScale.Px(15, s);

        DrawBtn(sb, new Rectangle(ix, iy, stepW, ctrlH), "<", BG_CARD, Color.White, smallFont, Color.White * 0.15f,
            () => CycleSlotClip(-1), tooltip: "Previous clip");
        var pickBtn = new Rectangle(ix + stepW + gap, iy, Math.Max(40, iw - (stepW + gap) * 2), ctrlH);
        string shownClip = TruncateText(string.IsNullOrEmpty(active.ClipName) ? "(pick clip)" : active.ClipName, pickBtn.Width - UiScale.Px(12, s), UiScale.FontPx(11, s));
        DrawBtn(sb, pickBtn, shownClip, BG_DARK, ACCENT_GOLD, UiScale.FontPx(11, s), ACCENT_GOLD * 0.5f, OpenClipPicker,
            tooltip: "Open clip browser");
        DrawBtn(sb, new Rectangle(pickBtn.Right + gap, iy, stepW, ctrlH), ">", BG_CARD, Color.White, smallFont, Color.White * 0.15f,
            () => CycleSlotClip(1), tooltip: "Next clip");
        iy += ctrlH + UiScale.Px(7, s);

        // FPS row (step 1, hold to repeat, Shift = 4)
        DrawText(sb, $"Speed: {active.Fps:F0} FPS", new Vector2(ix, iy + (ctrlH - MeasureText("Ay", smallFont).Y) / 2f), Color.White, smallFont);
        DrawBtn(sb, new Rectangle(ix + iw - stepW * 2 - gap, iy, stepW, ctrlH), "-", BG_CARD, Color.White, smallFont, Color.White * 0.15f, () =>
        {
            float step = shiftHeld ? 4f : 1f;
            active.Fps = Math.Clamp(active.Fps - step, 1f, 96f);
            _modified = true;
        }, tooltip: "Decrease FPS (-1, hold to repeat, Shift: -4)", repeatable: true);
        DrawBtn(sb, new Rectangle(ix + iw - stepW, iy, stepW, ctrlH), "+", BG_CARD, Color.White, smallFont, Color.White * 0.15f, () =>
        {
            float step = shiftHeld ? 4f : 1f;
            active.Fps = Math.Clamp(active.Fps + step, 1f, 96f);
            _modified = true;
        }, tooltip: "Increase FPS (+1, hold to repeat, Shift: +4)", repeatable: true);
        iy += ctrlH + UiScale.Px(6, s);

        // Loop toggle
        DrawBtn(sb, new Rectangle(ix, iy, iw, ctrlH),
            active.Loop ? "LOOP / HOLD RETURN: ON" : "LOOP: OFF (FREEZE END)",
            active.Loop ? new Color(24, 72, 54) : BG_CARD,
            active.Loop ? ACCENT_GREEN : Color.White * 0.7f, UiScale.FontPx(11, s),
            active.Loop ? ACCENT_GREEN * 0.6f : Color.White * 0.15f,
            () => { active.Loop = !active.Loop; _modified = true; },
            tooltip: "Loop animation vs freeze on last frame");
        iy += ctrlH + UiScale.Px(6, s);

        // Onion Skin Reference Selector
        bool isGhostRef = _onionRefSlot == active.SlotIndex;
        int refBtnH = UiScale.Px(24, s);
        DrawBtn(sb, new Rectangle(ix, iy, iw, refBtnH),
            isGhostRef ? "GHOST REF: ACTIVE" : "SET AS GHOST REF",
            isGhostRef ? new Color(18, 58, 74) : BG_CARD,
            isGhostRef ? ACCENT_CYAN : Color.White * 0.75f, tinyFont,
            isGhostRef ? ACCENT_CYAN * 0.65f : Color.White * 0.14f,
            () =>
            {
                _onionRefSlot = active.SlotIndex;
                _onionSkin = true;
                ShowToast($"Ghost reference set to {active.Label}", ACCENT_CYAN);
            }, tooltip: "Use this slot's frame 0 as the Onion Skin alignment ghost");
        iy += refBtnH + UiScale.Px(8, s);

        // Compact Offset & Canvas Readout Box
        int boxH = Math.Max(UiScale.Px(38, s), inspCard.Bottom - secPad - iy);
        if (boxH >= UiScale.Px(34, s))
        {
            var metaBox = new Rectangle(ix, iy, iw, Math.Min(UiScale.Px(46, s), boxH));
            sb.Draw(_pixel, metaBox, BG_DARK);
            DrawBorder(sb, metaBox, Color.White * 0.08f);

            var activeClipObj = _project.FindClip(active.ClipName);
            Vec2 fnmmOff = activeClipObj != null ? _project.ComputeFnmmOffset(active, activeClipObj) : new Vec2();
            string line1 = $"Nudge: ({active.UserOffsetX:+0.#;-0.#;0}, {active.UserOffsetY:+0.#;-0.#;0}) px";
            string line2 = TruncateText($"FNMM: ({fnmmOff.X:F0}, {fnmmOff.Y:F0})  •  {activeClipObj?.CanvasWidth ?? 0}x{activeClipObj?.CanvasHeight ?? 0}",
                metaBox.Width - UiScale.Px(12, s), tinyFont);

            DrawText(sb, line1, new Vector2(metaBox.X + UiScale.Px(8, s), metaBox.Y + UiScale.Px(5, s)), ACCENT_CYAN, UiScale.FontPx(11, s));
            DrawText(sb, line2, new Vector2(metaBox.X + UiScale.Px(8, s), metaBox.Y + UiScale.Px(22, s)), Color.White * 0.55f, tinyFont);
        }
    }

    // ── Modals ───────────────────────────────────────────────────────────────

    private void DrawImportModal(SpriteBatch sb, int sw, int sh)
    {
        float s = _uiScale;
        var full = new Rectangle(0, 0, sw, sh);
        sb.Draw(_pixel, full, Color.Black * 0.68f);

        int mw = Math.Clamp((int)(sw * 0.44f), UiScale.Px(400, s), UiScale.Px(560, s));
        int mh = UiScale.Px(316, s);
        var mr = new Rectangle((sw - mw) / 2, (sh - mh) / 2, mw, mh);

        _clickTargets.Add((full, () => _importModalOpen = false, false));
        _clickTargets.Add((mr, () => { }, false));

        sb.Draw(_pixel, mr, BG_PANEL);
        DrawBorder(sb, mr, ACCENT_CYAN * 0.85f);

        int pad = UiScale.Px(16, s);
        int hdrH = UiScale.Px(46, s);
        sb.Draw(_pixel, new Rectangle(mr.X + 1, mr.Y + 1, mr.Width - 2, hdrH), BG_SECTION);
        sb.Draw(_pixel, new Rectangle(mr.X + 1, mr.Y + hdrH, mr.Width - 2, 1), Color.White * 0.10f);

        DrawText(sb, "IMPORT CHARACTER", new Vector2(mr.X + pad, mr.Y + UiScale.Px(14, s)), ACCENT_GOLD, UiScale.FontPx(15, s));

        int closeW = UiScale.Px(84, s);
        var closeBtn = new Rectangle(mr.Right - pad - closeW, mr.Y + (hdrH - UiScale.Px(28, s)) / 2, closeW, UiScale.Px(28, s));
        DrawBtn(sb, closeBtn, "CLOSE", BG_CARD, Color.White, UiScale.FontPx(11, s), Color.White * 0.2f, () => _importModalOpen = false);

        int y = mr.Y + hdrH + UiScale.Px(14, s);
        int cw = mr.Width - pad * 2;
        int cardH = UiScale.Px(68, s);
        int cardGap = UiScale.Px(10, s);

        DrawImportOptionCard(sb, new Rectangle(mr.X + pad, y, cw, cardH),
            "1", "import_xml", "SPARROW v2 (XML + PNG / JSON)",
            "Standard FNF, Psych Engine, V-Slice & Codename spritesheets",
            ACCENT_CYAN, () =>
            {
                _importModalOpen = false;
                RequestDiscardAware("Import a new Sparrow character? Unsaved changes will be lost.", PromptImportSparrow);
            });
        y += cardH + cardGap;

        DrawImportOptionCard(sb, new Rectangle(mr.X + pad, y, cw, cardH),
            "2", "import_spritemap", "ADOBE ANIMATE SPRITEMAP / .ZIP",
            "Animation.json + spritemap*.json/png or packaged .ZIP character",
            ACCENT_PURPLE, () =>
            {
                _importModalOpen = false;
                RequestDiscardAware("Import a Spritemap / ZIP character? Unsaved changes will be lost.", PromptImportSpritemapOrZip);
            });
        y += cardH + cardGap;

        DrawImportOptionCard(sb, new Rectangle(mr.X + pad, y, cw, cardH),
            "3", "import_fnmm", "EXTRACT FROM .FNMM LEVEL",
            "Extract an existing FunkyMaker character directly from a chart file",
            ACCENT_GOLD, () =>
            {
                _importModalOpen = false;
                RequestDiscardAware("Extract character from .FNMM? Unsaved changes will be lost.", PromptImportFnmm);
            });
    }

    private void DrawImportOptionCard(SpriteBatch sb, Rectangle r, string keyHint, string iconName, string title, string subtitle, Color accent, Action onClick)
    {
        float s = _uiScale;
        bool hov = r.Contains(_prevMouse.Position);
        sb.Draw(_pixel, r, hov ? Color.Lerp(BG_CARD, accent, 0.14f) : BG_CARD);
        DrawBorder(sb, r, hov ? accent : accent * 0.45f);
        sb.Draw(_pixel, new Rectangle(r.X, r.Y, UiScale.Px(4, s, 2), r.Height), accent);

        int boxSz = UiScale.Px(42, s);
        var icoBox = new Rectangle(r.X + UiScale.Px(14, s), r.Y + (r.Height - boxSz) / 2, boxSz, boxSz);
        sb.Draw(_pixel, icoBox, accent * 0.16f);
        DrawBorder(sb, icoBox, accent * 0.55f);

        var tex = GetIcon(iconName);
        if (tex != null)
        {
            int isz = (int)(boxSz * 0.62f);
            sb.Draw(tex, new Rectangle(icoBox.X + (boxSz - isz) / 2, icoBox.Y + (boxSz - isz) / 2, isz, isz), accent);
        }

        int tx = icoBox.Right + UiScale.Px(12, s);
        int maxTextW = Math.Max(60, r.Right - tx - UiScale.Px(34, s));
        int tFont = UiScale.FontPx(13, s);
        int subFont = UiScale.FontPx(11, s);
        DrawText(sb, TruncateText(title, maxTextW, tFont), new Vector2(tx, r.Y + UiScale.Px(14, s)), Color.White, tFont);
        DrawText(sb, TruncateText(subtitle, maxTextW, subFont), new Vector2(tx, r.Y + UiScale.Px(36, s)), Color.White * 0.62f, subFont);

        if (!PointerInput.IsTouch)
        {
            var kBox = new Rectangle(r.Right - UiScale.Px(30, s), r.Y + (r.Height - UiScale.Px(22, s)) / 2, UiScale.Px(20, s), UiScale.Px(22, s));
            sb.Draw(_pixel, kBox, BG_DARK);
            DrawBorder(sb, kBox, accent * 0.45f);
            DrawTextCentered(sb, keyHint, kBox, accent, UiScale.FontPx(10, s));
        }

        _clickTargets.Add((r, onClick, false));
    }

    private void DrawClipPickerModal(SpriteBatch sb, int sw, int sh)
    {
        if (_project == null) return;
        float s = _uiScale;
        var full = new Rectangle(0, 0, sw, sh);
        sb.Draw(_pixel, full, Color.Black * 0.68f);

        int mw = Math.Clamp((int)(sw * 0.48f), UiScale.Px(380, s), UiScale.Px(640, s));
        int mh = Math.Clamp((int)(sh * 0.70f), UiScale.Px(320, s), UiScale.Px(560, s));
        var mr = new Rectangle((sw - mw) / 2, (sh - mh) / 2, mw, mh);

        // Register backdrop close + modal body click swallow so clicking inside doesn't close!
        _clickTargets.Add((full, () => _clipPickerOpen = false, false));
        _clickTargets.Add((mr, () => { }, false));

        sb.Draw(_pixel, mr, BG_PANEL);
        DrawBorder(sb, mr, ACCENT_GOLD);

        int pad = UiScale.Px(14, s);
        int hdrH = UiScale.Px(46, s);
        sb.Draw(_pixel, new Rectangle(mr.X + 1, mr.Y + 1, mr.Width - 2, hdrH), BG_SECTION);
        sb.Draw(_pixel, new Rectangle(mr.X + 1, mr.Y + hdrH, mr.Width - 2, 1), Color.White * 0.10f);

        int closeW = UiScale.Px(84, s);
        var closeBtn = new Rectangle(mr.Right - pad - closeW, mr.Y + (hdrH - UiScale.Px(28, s)) / 2, closeW, UiScale.Px(28, s));
        DrawBtn(sb, closeBtn, "CLOSE", BG_CARD, Color.White, UiScale.FontPx(11, s), Color.White * 0.2f, () => _clipPickerOpen = false);

        string title = TruncateText($"SELECT CLIP FOR {GetActiveSlot()?.Label} ({_project.AvailableClips.Count})",
            closeBtn.X - (mr.X + pad) - UiScale.Px(10, s), UiScale.FontPx(14, s));
        DrawText(sb, title, new Vector2(mr.X + pad, mr.Y + UiScale.Px(14, s)), ACCENT_GOLD, UiScale.FontPx(14, s));

        int listY = mr.Y + hdrH + UiScale.Px(10, s);
        int listH = mr.Bottom - pad - listY;
        int rowH = UiScale.Px(34, s);
        int rowGap = 3;
        int maxRows = Math.Max(3, listH / (rowH + rowGap));
        _clipPickerScroll = Math.Clamp(_clipPickerScroll, 0, Math.Max(0, _project.AvailableClips.Count - maxRows));

        bool needsScroll = _project.AvailableClips.Count > maxRows;
        int scrollW = needsScroll ? UiScale.Px(10, s) : 0;
        int itemW = mw - pad * 2 - scrollW;
        int rowFont = UiScale.FontPx(12, s);
        int metaFont = UiScale.FontPx(10, s);

        for (int i = 0; i < maxRows && (_clipPickerScroll + i) < _project.AvailableClips.Count; i++)
        {
            int clipIdx = _clipPickerScroll + i;
            var clip = _project.AvailableClips[clipIdx];
            var rr = new Rectangle(mr.X + pad, listY + i * (rowH + rowGap), itemW, rowH);
            bool cur = string.Equals(GetActiveSlot()?.ClipName, clip.Name, StringComparison.OrdinalIgnoreCase);
            bool keySel = clipIdx == _clipPickerHighlight;
            bool hov = rr.Contains(_prevMouse.Position);

            sb.Draw(_pixel, rr, cur ? ACCENT_CYAN * 0.26f : (hov || keySel ? Color.Lerp(BG_CARD, Color.White, 0.10f) : BG_CARD));
            DrawBorder(sb, rr, cur ? ACCENT_CYAN : (keySel ? ACCENT_GOLD * 0.75f : (hov ? Color.White * 0.28f : Color.White * 0.08f)));
            if (cur)
                sb.Draw(_pixel, new Rectangle(rr.X, rr.Y, UiScale.Px(3, s, 2), rr.Height), ACCENT_CYAN);

            int leftX = rr.X + UiScale.Px(10, s);
            if (cur)
            {
                var chk = GetIcon("check_on");
                if (chk != null)
                {
                    int isz = UiScale.Px(16, s);
                    sb.Draw(chk, new Rectangle(leftX, rr.Y + (rr.Height - isz) / 2, isz, isz), ACCENT_CYAN);
                    leftX += isz + UiScale.Px(6, s);
                }
            }

            string meta = $"{clip.Frames.Count}f  •  {clip.CanvasWidth}x{clip.CanvasHeight}";
            var mSz = MeasureText(meta, metaFont);
            DrawText(sb, meta, new Vector2(rr.Right - mSz.X - UiScale.Px(10, s), rr.Y + (rr.Height - mSz.Y) / 2f),
                cur ? ACCENT_CYAN : Color.White * 0.55f, metaFont);

            string nameLbl = TruncateText(clip.Name, Math.Max(40, rr.Right - leftX - (int)mSz.X - UiScale.Px(20, s)), rowFont);
            var nSz = MeasureText(nameLbl, rowFont);
            DrawText(sb, nameLbl, new Vector2(leftX, rr.Y + (rr.Height - nSz.Y) / 2f), Color.White, rowFont);

            int capturedIdx = clipIdx;
            _clickTargets.Add((rr, () => ApplyPickedClip(capturedIdx), false));
        }

        if (needsScroll)
            DrawScrollbar(sb, new Rectangle(mr.Right - pad - scrollW + 2, listY, scrollW - 3, listH),
                _clipPickerScroll, maxRows, _project.AvailableClips.Count, ACCENT_GOLD);
    }

    private void DrawConfirmDiscardModal(SpriteBatch sb, int sw, int sh)
    {
        float s = _uiScale;
        var full = new Rectangle(0, 0, sw, sh);
        sb.Draw(_pixel, full, Color.Black * 0.72f);

        int mw = Math.Clamp(UiScale.Px(430, s), UiScale.Px(340, s), sw - 32);
        int mh = UiScale.Px(168, s);
        var mr = new Rectangle((sw - mw) / 2, (sh - mh) / 2, mw, mh);

        _clickTargets.Add((full, CloseConfirmDiscard, false));
        _clickTargets.Add((mr, () => { }, false));

        sb.Draw(_pixel, mr, BG_PANEL);
        DrawBorder(sb, mr, ACCENT_GOLD);

        int pad = UiScale.Px(16, s);
        DrawText(sb, _confirmDiscardTitle, new Vector2(mr.X + pad, mr.Y + pad), ACCENT_GOLD, UiScale.FontPx(15, s));

        var msgRect = new Rectangle(mr.X + pad, mr.Y + UiScale.Px(44, s), mw - pad * 2, UiScale.Px(54, s));
        DrawTextCentered(sb, _confirmDiscardBody, msgRect, Color.White * 0.85f, UiScale.FontPx(12, s));

        int btnH = UiScale.Px(34, s);
        int btnY = mr.Bottom - pad - btnH;
        int gap = UiScale.Px(8, s);
        int btnW = (mw - pad * 2 - gap * 2) / 3;
        int bf = UiScale.FontPx(11, s);

        DrawBtn(sb, new Rectangle(mr.X + pad, btnY, btnW, btnH), "SAVE & GO",
            new Color(32, 118, 76), Color.White, bf, ACCENT_GREEN, ExecuteSaveAndPendingAction);

        DrawBtn(sb, new Rectangle(mr.X + pad + btnW + gap, btnY, btnW, btnH), "DISCARD",
            new Color(110, 34, 50), Color.White, bf, ACCENT_PINK, () =>
            {
                var act = _pendingDiscardAction;
                CloseConfirmDiscard();
                _modified = false;
                act?.Invoke();
            });

        DrawBtn(sb, new Rectangle(mr.X + pad + (btnW + gap) * 2, btnY, mw - pad * 2 - (btnW + gap) * 2, btnH), "CANCEL",
            BG_CARD, Color.White, bf, Color.White * 0.22f, CloseConfirmDiscard);
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

    private void DrawTooltip(SpriteBatch sb, int sw, int sh, string text, Rectangle anchor)
    {
        float s = _uiScale;
        int tf = UiScale.FontPx(11, s);
        var sz = MeasureText(text, tf);
        int tw = (int)sz.X + UiScale.Px(16, s);
        int th = (int)sz.Y + UiScale.Px(10, s);

        int tx = Math.Clamp(anchor.X + (anchor.Width - tw) / 2, 8, Math.Max(8, sw - tw - 8));
        int ty = anchor.Bottom + UiScale.Px(6, s);
        if (ty + th > sh - 8)
            ty = Math.Max(8, anchor.Y - th - UiScale.Px(6, s));

        var r = new Rectangle(tx, ty, tw, th);
        sb.Draw(_pixel, r, new Color(12, 12, 20) * 0.96f);
        DrawBorder(sb, r, ACCENT_PURPLE * 0.75f);
        DrawTextCentered(sb, text, r, Color.White * 0.92f, tf);
    }

    // ── Actions & Helpers ────────────────────────────────────────────────────

    private void RequestLeaveEditor()
    {
        RequestDiscardAware($"Exit Character Editor?\nUnsaved changes to '{_project?.Name}' will be lost.",
            () => BackPressed = true);
    }

    private void RequestDiscardAware(string body, Action action)
    {
        if (!_modified || _project == null)
        {
            action();
            return;
        }
        _confirmDiscardTitle = "UNSAVED CHANGES";
        _confirmDiscardBody = body;
        _pendingDiscardAction = action;
        _confirmDiscardOpen = true;
    }

    private void CloseConfirmDiscard()
    {
        _confirmDiscardOpen = false;
        _pendingDiscardAction = null;
    }

    private void ExecuteSaveAndPendingAction()
    {
        var act = _pendingDiscardAction;
        CloseConfirmDiscard();
        if (SaveCurrentCharacter())
            act?.Invoke();
    }

    private void CycleStageBackground()
    {
        _stageBgMode = (_stageBgMode + 1) % 3;
        UiSfx.Play(UiSound.Toggle);
        string modeName = _stageBgMode switch { 1 => "Checkerboard", 2 => "Light", _ => "Dark Studio" };
        ShowToast($"Stage Backdrop: {modeName}", ACCENT_CYAN, 1.8f);
    }

    private void FitCameraToCharacter()
    {
        _viewPan = Vector2.Zero;
        if (_project != null)
        {
            var slot = GetActiveSlot() ?? (_project.Slots.Count > 0 ? _project.Slots[0] : null);
            var clip = _project.FindClip(slot?.ClipName);
            if (clip != null && clip.CanvasHeight > 0 && _stageCanvasRect.Height > 80)
            {
                float targetH = _stageCanvasRect.Height * 0.68f;
                _viewZoom = Math.Clamp(targetH / (clip.CanvasHeight * Math.Max(0.2f, _project.Scale)), 0.20f, 2.5f);
                return;
            }
        }
        _viewZoom = 0.85f;
    }

    private void OpenClipPicker()
    {
        if (_project == null) return;
        _clipPickerOpen = true;
        var active = GetActiveSlot();
        int idx = active != null
            ? _project.AvailableClips.FindIndex(c => string.Equals(c.Name, active.ClipName, StringComparison.OrdinalIgnoreCase))
            : 0;
        _clipPickerHighlight = Math.Max(0, idx);
        EnsureClipPickerHighlightVisible();
    }

    private void EnsureClipPickerHighlightVisible()
    {
        if (_project == null) return;
        int approxRows = 8;
        if (_clipPickerHighlight < _clipPickerScroll)
            _clipPickerScroll = _clipPickerHighlight;
        else if (_clipPickerHighlight >= _clipPickerScroll + approxRows)
            _clipPickerScroll = Math.Max(0, _clipPickerHighlight - approxRows + 1);
    }

    private void ApplyPickedClip(int clipIdx)
    {
        if (_project == null || clipIdx < 0 || clipIdx >= _project.AvailableClips.Count) return;
        var clip = _project.AvailableClips[clipIdx];
        var sl = GetActiveSlot();
        if (sl != null)
        {
            sl.ClipName = clip.Name;
            sl.Fps = clip.SuggestedFps > 0 ? clip.SuggestedFps : sl.Fps;
            _animFrame = 0f;
            _modified = true;
        }
        _clipPickerOpen = false;
        UiSfx.Play(UiSound.Confirm);
    }

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
        _slotScroll = Math.Max(0, _project.Slots.Count - 4);
        _modified = true;
    }

    private void RemoveSelectedExtraSlot()
    {
        if (_project == null || _selectedSlot < 5 || _selectedSlot >= _project.Slots.Count) return;
        _project.Slots.RemoveAt(_selectedSlot);
        _project.EnsureCoreSlots();
        _selectedSlot = Math.Clamp(_selectedSlot, 0, _project.Slots.Count - 1);
        _onionRefSlot = Math.Clamp(_onionRefSlot, 0, _project.Slots.Count - 1);
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
            "Animate Spritemap or Character ZIP (Animation.json;animations.json;spritemap*.json;metadata.json;*.zip)\0Animation.json;animations.json;metadata.json;spritemap*.json;*.zip;*.json\0All Files (*.*)\0*.*\0\0",
            "Select Animation.json / animations.json / spritemap*.json or Character .ZIP");
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
                _project.IconFlipX = false;
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
                _project.IconFlipX = false;
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

    private bool SaveCurrentCharacter()
    {
        if (_project == null) return false;
        try
        {
            CharacterLibrary.BakeAndSave(_project);
            LastSavedSlug = _project.Slug;
            _modified = false;
            RefreshLibrary();
            ShowToast($"Saved to mods/characters/{_project.Slug}!", ACCENT_GREEN);
            return true;
        }
        catch (Exception ex)
        {
            ShowToast($"Save failed: {ex.Message}", ACCENT_PINK, 4f);
            return false;
        }
    }

    private void SetActiveProject(CharacterProject proj, bool markModified)
    {
        DisposeProjectTextures();
        _project?.Dispose();
        _project = proj;
        _project.EnsureCoreSlots();
        _selectedSlot = 0;
        _onionRefSlot = 0;
        _slotScroll = 0;
        _animFrame = 0f;
        _isPlaying = true;
        _viewPan = Vector2.Zero;
        _modified = markModified;
        _texturesDirty = true;
        FitCameraToCharacter();
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
            _iconNormalTex = CreateTextureFromImage(_project.IconNormal, _project.IconFlipX);
        if (_project.IconLosing != null)
            _iconLosingTex = CreateTextureFromImage(_project.IconLosing, _project.IconFlipX);
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
        try { _scissorRasterizer.Dispose(); } catch { }
    }

    private void ShowToast(string msg, Color color, float seconds = 2.6f)
    {
        _toastText = msg;
        _toastColor = color;
        _toastTimer = seconds;
    }

    private void SetTooltip(string? tooltip, Rectangle anchor)
    {
        if (string.IsNullOrEmpty(tooltip)) return;
        _hoverTooltip = tooltip;
        _hoverTooltipRect = anchor;
    }

    // ── Primitive UI Drawing & Icon Registry ─────────────────────────────────

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

    /// <summary>Maps a button token/label to its Material Design PNG icon and clean display text.</summary>
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
            case "NUDGE_L": return ("arrow_left", "");
            case "NUDGE_R": return ("arrow_right", "");
            case "NUDGE_U": return ("arrow_up", "");
            case "NUDGE_D": return ("arrow_down", "");
            case "ZOOM+": return ("zoom_in", "");
            case "ZOOM-": return ("zoom_out", "");
            case "SCAN": return ("refresh", "SCAN");
            case "FIT": return ("fit", "FIT");
            case "PAUSE": return ("pause", "PAUSE");
            case "PLAY": return ("play", "PLAY");
            case "ZERO OFF": return ("reset_origin", "ZERO");
            case "LOAD ICON": return ("load_icon", "LOAD");
            case "CLR_ICO": return ("delete", "");
            case "FLIP ICON": return ("flip", "FLIP ICON");
            case "CLEAR": return ("delete", "CLEAR");
            case "DEL": return ("delete", "DEL");
            case "CLOSE": return ("close", "CLOSE");
            case "CANCEL": return ("close", "CANCEL");
            case "DISCARD": return ("delete", "DISCARD");
            case "SAVE & GO": return ("save", "SAVE");
            case "IMPORT": return ("folder_open", "IMPORT");
            case "+ EXTRA": return ("extra_track", "EXTRA");
            case "SWAP L<->R": return ("swap_horiz", "SWAP L/R");
            case "SAVE CHAR": return ("save", "SAVE");
            case "SAVE CHAR *": return ("save", "SAVE *");
            case "GHOST: ON": return ("ghost_on", "GHOST");
            case "GHOST: OFF": return ("ghost_off", "GHOST");
            case "GHOST REF: ACTIVE": return ("ghost_on", "GHOST REF: ON");
            case "SET AS GHOST REF": return ("ghost_off", "USE AS GHOST REF");
        }
        if (text.StartsWith("FLIP X")) return ("flip", text.Replace("FLIP X: ", "FLIP "));
        if (text.StartsWith("FACES: <-")) return ("face_left", "FACES LEFT (BF)");
        if (text.StartsWith("FACES:")) return ("face_right", "FACES RIGHT (FOE)");
        if (text.StartsWith("FILTER: SMOOTH")) return ("filter_smooth", "SMOOTH (HD)");
        if (text.StartsWith("FILTER: PIXEL")) return ("filter_pixel", "PIXEL ART");
        if (text.StartsWith("LOOP / HOLD")) return ("loop_on", "LOOP ON");
        if (text.StartsWith("LOOP: OFF")) return ("loop_off", "LOOP OFF");
        return (null, text);
    }

    private void DrawBtn(SpriteBatch sb, Rectangle r, string text, Color bg, Color fg, int fontSize, Color border, Action onClick, bool leftAlign = false, string? tooltip = null, bool repeatable = false)
    {
        bool hov = r.Contains(_prevMouse.Position);
        sb.Draw(_pixel, r, hov ? Color.Lerp(bg, Color.White, 0.12f) : bg);
        DrawBorder(sb, r, hov ? Color.Lerp(border, Color.White, 0.35f) : border);

        if (hov && !string.IsNullOrEmpty(tooltip))
            SetTooltip(tooltip, r);

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
            int isz = Math.Clamp((int)(r.Height * 0.60f), 12, 34);
            int gap = label.Length > 0 ? UiScale.Px(5, _uiScale, 3) : 0;
            float textW = label.Length > 0 ? MeasureText(label, fontSize).X : 0f;
            float total = isz + gap + textW;
            if (total > r.Width - 6 && label.Length > 0)
            {
                // Shrink icon slightly before dropping label so text stays readable
                isz = Math.Max(11, (int)(r.Height * 0.48f));
                total = isz + gap + textW;
                if (total > r.Width - 6)
                {
                    label = "";
                    gap = 0;
                    total = isz;
                }
            }
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
        _clickTargets.Add((r, onClick, repeatable));
    }

    private void DrawScrollbar(SpriteBatch sb, Rectangle track, int scroll, int visible, int total, Color accent)
    {
        if (total <= visible || track.Height < 12 || track.Width < 2) return;
        sb.Draw(_pixel, track, Color.White * 0.06f);
        float ratio = Math.Clamp(visible / (float)total, 0.12f, 1f);
        int thumbH = Math.Max(14, (int)(track.Height * ratio));
        float maxScroll = Math.Max(1, total - visible);
        int thumbY = track.Y + (int)((track.Height - thumbH) * Math.Clamp(scroll / maxScroll, 0f, 1f));
        sb.Draw(_pixel, new Rectangle(track.X, thumbY, track.Width, thumbH), accent * 0.65f);
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
        if (maxW <= 12 || string.IsNullOrEmpty(text)) return text;
        if (MeasureText(text, size).X <= maxW) return text;
        for (int len = text.Length - 1; len > 2; len--)
        {
            string sub = text.Substring(0, len) + "..";
            if (MeasureText(sub, size).X <= maxW) return sub;
        }
        return text.Length > 2 ? text.Substring(0, 2) + ".." : text;
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
