using FunkyMakerPlayer.Managers;
using FunkyMakerPlayer.UI;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Audio;
using Microsoft.Xna.Framework.Graphics;
using Microsoft.Xna.Framework.Input;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace FunkyMakerPlayer.Editors.Chart
{
    /// <summary>
    /// Redesigned Chart Editor with interactive timeline scrubber, aligned clickable toolbar,
    /// working Select/Move/Hold-resize/Delete tools, Undo/Redo, Playtest hook, and toggleable
    /// docked stage preview that never obscures the chart grid.
    /// </summary>
    public partial class ChartEditor : IEditor
    {
        // ── Core data ────────────────────────────────────────────────────────
        private string _levelPath;
        private FnmmLevel? _level;
        private readonly Texture2D _pixel;
        private readonly SpriteFont? _font;
        private readonly FontRenderer? _fontRenderer;
        private readonly NoteStyle? _noteStyle;

        // ── Editor state ─────────────────────────────────────────────────────
        private const int MAX_STRUMLINES = 32;
        private EditorPhase _phase = EditorPhase.Loading;
        private string? _errorMessage;
        private EditorTool _currentTool = EditorTool.PlaceNote;
        private int _selectedStrumline = 0;  // Focused strumline (0 = BF right, >=1 = FOE/Extra left)
        private int _leftStrumline = 1;      // Strumline displayed on left columns (cycles 1..N-1)
        private int _rightStrumline = 0;     // Strumline displayed on right columns (0 = BF)
        private int _selectedLane = -1;
        private int _hoverCol = -1;          // 0..3 = Left block, 4..7 = Right block
        private int _hoverLane = -1;
        private int _hoverStrumline = -1;
        private float _hoverSnappedBeat = -1f;
        private string? _hoveredEventSummary;
        private bool _showPreview = true;
        private int _snapDivisor = 16;       // 1/4, 1/8, 1/12, 1/16 (1 step square), 1/24, 1/32
        private float _defaultHoldBeats = 0.5f;
        private NotePreset _activePreset = NotePreset.Normal;  // brush preset (skin + action pack)

        // ── Undo/Redo, Selection, Clipboard & Drag state ─────────────────────
        private readonly UndoManager _undoManager = new();
        private FnmmNote? _selectedNote;
        private int _selectedNoteLane = -1;
        private int _selectedNoteStrumline = 0;
        private readonly List<(int lane, float relBeat, float duration, float skin, float onPress, float onMiss)> _clipboardNotes = new();

        private enum DragMode { None, Scrubber, MoveNote, ResizeHold, NewHoldTail }
        private DragMode _dragMode = DragMode.None;
        private FnmmNote? _dragNote;
        private int _dragOrigLane;
        private int _dragOrigStrumline;
        private float _dragOrigStart;
        private float _dragOrigDuration;
        private float _dragGrabOffset;

        // ── Loading & Feedback ───────────────────────────────────────────────
        private float _loadingSpinnerRotation = 0f;
        private System.Threading.Tasks.Task? _loadingTask;
        private string? _toastMessage;
        private float _toastTimer;
        private float _inputCooldown = 0f;

        // ── Receptor & Engine Stage Preview State ────────────────────────────
        private readonly float[,] _receptorPressTime = new float[MAX_STRUMLINES, LANE_COUNT];
        private const float RECEPTOR_ANIM_DURATION = 0.12f;
        private readonly float[] _characterSingTime = new float[MAX_STRUMLINES];
        private readonly int[] _characterSingLane = Enumerable.Repeat(-1, MAX_STRUMLINES).ToArray();
        private const float SING_ANIM_DURATION = 0.22f;

        private FnmmLevel? _previewLevel;
        private readonly Dictionary<string, Dictionary<int, SpriteTextureCacheEntry>> _previewSpriteTextureCache;
        private readonly bool _ownsPreviewTextures;
        private Dictionary<string, List<Texture2D>> _previewTexCache = new(StringComparer.OrdinalIgnoreCase);
        private SpriteAnimator?[] _previewAnimators = Array.Empty<SpriteAnimator?>();
        private EventProcessor? _previewEvents;
        private CameraManager? _previewCameraManager;
        private RenderTarget2D? _previewTarget;
        private int _previewTargetW = 1280;
        private int _previewTargetH = 720;
        private bool _previewInitialized = false;
        private bool _previewNeedsSeek = true;
        private float _previewLastSeekTime = -1f;
        private int _previewCameraMode = -1; // -1 = AUTO, -2 = FIT STAGE, >=0 = scene.Cameras[idx]
        private bool _previewShowOverlays = true;

        // ── Audio Playback, Hitsounds & Metronome ────────────────────────────
        private readonly EditorAudioManager _audioManager = new();
        private bool _isPlaying = false;
        private float _playbackTime = 0f;     // Seconds
        private float _prevPlaybackTime = 0f; // Seconds (for interval edge triggers)
        private bool _hitsoundsEnabled = true;
        private bool _metronomeEnabled = false;
        private SoundEffect? _hitSoundFx;
        private SoundEffect? _metroBeatFx;
        private SoundEffect? _metroSubFx;
        private bool _audioCuesInitialized = false;

        // ── Public integration properties (Game1) ────────────────────────────
        public bool BackPressed { get; private set; }
        public bool IsModified => _undoManager.IsModified || _manualModified;
        public bool SavedInSession { get; private set; }
        private bool _manualModified = false;

        public bool PlaytestRequested { get; private set; }
        public float PlaytestStartSeconds { get; private set; }
        public string LevelPath => _levelPath;
        public string LevelDisplayName => !string.IsNullOrWhiteSpace(_level?.LevelName)
            ? _level.LevelName
            : Path.GetFileNameWithoutExtension(_levelPath);
        public bool IsPlayingAudio => _isPlaying;
        public FnmmLevel? GetEditedLevel() => _level;
        public Dictionary<string, Dictionary<int, SpriteTextureCacheEntry>> PreviewSpriteTextureCache => _previewSpriteTextureCache;
        public bool OwnsPreviewTextures => _ownsPreviewTextures;

        public bool HasCachedPreviewTextures
        {
            get
            {
                if (_level == null) return false;
                if (_level.Sprites.Count == 0) return true;
                foreach (var (name, (_, frames)) in _level.Sprites)
                {
                    if (!_previewSpriteTextureCache.TryGetValue(name, out var sc)) return false;
                    if (sc.Count != frames.Count) return false;
                    foreach (var kvp in sc)
                    {
                        if (kvp.Value.Textures.Count == 0) return false;
                        foreach (var t in kvp.Value.Textures)
                            if (t == null || t.IsDisposed) return false;
                    }
                }
                return true;
            }
        }

        public (byte[]? Pcm, int SampleRate, int Channels) GetCachedAudioPcm()
            => (_audioManager.CachedPcm, _audioManager.SampleRate, _audioManager.Channels);

        public void ConsumePlaytestRequest() => PlaytestRequested = false;

        public void ResetInputCooldown()
        {
            _inputCooldown = 0.2f;
            BackPressed = false;
            PlaytestRequested = false;
            _dragMode = DragMode.None;
            _prevKeyboard = LayoutKeyboard.GetState();
            _prevMouse = UI.PointerInput.GetState();
        }

        public void SyncInputState()
        {
            _inputCooldown = Math.Max(_inputCooldown, 0.08f);
            _dragMode = DragMode.None;
            _prevKeyboard = LayoutKeyboard.GetState();
            _prevMouse = UI.PointerInput.GetState();
        }

        public void PausePlaybackFromFocusLoss()
        {
            if (_isPlaying)
                PausePlayback();
        }

        // ── Grid & Scroll ────────────────────────────────────────────────────
        private float _scrollY = 0f;         // Current playhead position (beats or seconds, per TimesAreSeconds)
        private float _zoom = 1.0f;          // Vertical grid zoom only (1.0x => 1/16th step is a 1:1 square)
        private const int LANE_COUNT = 4;
        private const int GRID_COLS = LANE_COUNT * 2;
        private const int STEPS_PER_BEAT = 4;
        private const float RECEPTOR_Y_OFFSET = 0.20f;

        // ── Layout & Interactive Rectangles ──────────────────────────────────
        private int _screenWidth = 1280;
        private int _screenHeight = 720;
        private float _uiScale = 1f;
        private float _laneWidth = 72f;
        private int _gridHeaderH = 46;
        private int _gridGroupGap = 18;

        private Rectangle _timelineRect;
        private Rectangle _scrubberRect;
        private Rectangle _toolbarRect;
        private Rectangle _gridRect;
        private Rectangle _gridBodyRect;
        private Rectangle _infoPanelRect;
        private Rectangle _previewRect;
        private Rectangle _previewCamBtnRect;
        private Rectangle _previewFxBtnRect;
        private Rectangle _previewCloseBtn;
        private Rectangle _statusBarRect;
        private Rectangle _leftStrumPrevBtn;
        private Rectangle _leftStrumNextBtn;
        private Rectangle _hitsoundBtnRect;
        private Rectangle _metronomeBtnRect;

        private Rectangle _backBtnRect;
        private Rectangle _playBtnRect;
        private Rectangle _playtestBtnRect;
        private Rectangle _undoBtnRect;
        private Rectangle _redoBtnRect;
        private Rectangle _previewToggleRect;
        private Rectangle _saveBtnRect;

        private readonly Rectangle[] _toolBtnRects = new Rectangle[4];
        private Rectangle _strumPlayerBtn;
        private Rectangle _strumEnemyBtn;
        private Rectangle _snapPrevBtn;
        private Rectangle _snapNextBtn;
        private Rectangle _holdMinusBtn;
        private Rectangle _holdPlusBtn;
        private readonly Rectangle[] _skinBtnRects = new Rectangle[4];

        // Inspector clickable controls
        private Rectangle _inspDurMinusBtn;
        private Rectangle _inspDurPlusBtn;
        private Rectangle _inspDeleteBtn;
        private Rectangle _inspSkinBtn;
        private Rectangle _inspSwapStrumBtn;
        private Rectangle _inspBpmMinusBtn;
        private Rectangle _inspBpmPlusBtn;
        private Rectangle _inspZoomMinusBtn;
        private Rectangle _inspZoomPlusBtn;
        private Rectangle _inspSpeedMinusBtn;
        private Rectangle _inspSpeedPlusBtn;

        private readonly List<(Rectangle Rect, Action OnClick, Func<bool>? IsActive)> _clickActions = new();
        private RasterizerState? _scissorRasterizer;

        // ── Theme Colors ─────────────────────────────────────────────────────
        private static readonly Color BG_DARK = new(18, 17, 26);
        private static readonly Color BG_PANEL = new(25, 24, 36);
        private static readonly Color BG_CARD = new(36, 35, 52);
        private static readonly Color BG_CARD_HOVER = new(50, 48, 72);
        private static readonly Color ACCENT_PINK = new(255, 68, 136);
        private static readonly Color ACCENT_GOLD = new(255, 215, 0);
        private static readonly Color ACCENT_CYAN = new(0, 200, 255);
        private static readonly Color ACCENT_GREEN = new(65, 225, 145);
        private static readonly Color GRID_LINE = new(52, 52, 68);
        private static readonly Color BEAT_LINE = new(82, 82, 102);
        private static readonly Color MEASURE_LINE = new(135, 135, 165);

        private static readonly Color[] LANE_COLORS =
        {
            new(194, 75, 153), // Left
            new(0, 225, 255),  // Down
            new(32, 235, 72),  // Up
            new(249, 57, 63),  // Right
        };

        private static readonly string[] LANE_ARROWS = { "L", "D", "U", "R" };
        private static readonly int[] SNAP_VALUES = { 4, 8, 12, 16, 24, 32 };

        private KeyboardState _prevKeyboard;
        private MouseState _prevMouse;

        public ChartEditor(string levelPath, Texture2D pixel, SpriteFont? font,
                          FontRenderer? fontRenderer, NoteStyle? noteStyle = null,
                          Dictionary<string, Dictionary<int, SpriteTextureCacheEntry>>? sharedSpriteTextureCache = null)
        {
            _levelPath = levelPath;
            _pixel = pixel;
            _font = font;
            _fontRenderer = fontRenderer;
            _noteStyle = noteStyle;
            _previewSpriteTextureCache = sharedSpriteTextureCache
                ?? new Dictionary<string, Dictionary<int, SpriteTextureCacheEntry>>(StringComparer.OrdinalIgnoreCase);
            _ownsPreviewTextures = sharedSpriteTextureCache == null;
            _prevKeyboard = LayoutKeyboard.GetState();
            _prevMouse = UI.PointerInput.GetState();

            LoadLevel();
        }

        public void DisposeAudio()
        {
            _audioManager.Dispose();
            DisposePreviewResources();
            DisposeCharacterModalIcons();
            try { _scissorRasterizer?.Dispose(); } catch { }
            _scissorRasterizer = null;
            _hitSoundFx?.Dispose();
            _metroBeatFx?.Dispose();
            _metroSubFx?.Dispose();
            _hitSoundFx = null;
            _metroBeatFx = null;
            _metroSubFx = null;
            _audioCuesInitialized = false;
        }

        private void DisposePreviewResources()
        {
            if (_ownsPreviewTextures)
            {
                var toDispose = new HashSet<Texture2D>();
                foreach (var texList in _previewTexCache.Values)
                {
                    foreach (var t in texList)
                        if (t != null && !t.IsDisposed) toDispose.Add(t);
                }
                foreach (var spriteEntry in _previewSpriteTextureCache.Values)
                {
                    foreach (var cacheEntry in spriteEntry.Values)
                    {
                        foreach (var t in cacheEntry.Textures)
                            if (t != null && !t.IsDisposed) toDispose.Add(t);
                    }
                }
                foreach (var t in toDispose)
                    t.Dispose();
                _previewSpriteTextureCache.Clear();
            }
            _previewTexCache.Clear();
            _previewTarget?.Dispose();
            _previewTarget = null;
            _previewAnimators = Array.Empty<SpriteAnimator?>();
            _previewEvents = null;
            _previewCameraManager = null;
            _previewLevel = null;
            _previewInitialized = false;
            _previewNeedsSeek = true;
            _previewLastSeekTime = -1f;
        }

        private void LoadLevel()
        {
            _loadingTask = System.Threading.Tasks.Task.Run(() =>
            {
                try
                {
                    _level = FnmmLoader.Load(_levelPath);
                    EnsureMinimumStrumlines(_level);
                    _leftStrumline = _level.Data.Strumlines.Count > 1 ? 1 : 0;
                    _rightStrumline = 0;
                    _selectedStrumline = 0;
                    _audioManager.LoadFromLevel(_level);
                    Console.WriteLine($"[ChartEditor] Loaded: {_levelPath} | BPM={_level.Data.EffectiveBpm} TimesAreSeconds={_level.Data.TimesAreSeconds} Strumlines={_level.Data.Strumlines.Count}");
                }
                catch (Exception ex)
                {
                    _errorMessage = $"Failed to load level: {ex.Message}";
                    Console.WriteLine($"[ChartEditor] Load error: {ex.Message}");
                }
            });
        }

        private static void EnsureMinimumStrumlines(FnmmLevel level)
        {
            while (level.Data.Strumlines.Count < 2)
                level.Data.Strumlines.Add(new List<List<FnmmNote>>());

            for (int s = 0; s < level.Data.Strumlines.Count; s++)
            {
                while (level.Data.Strumlines[s].Count < LANE_COUNT)
                    level.Data.Strumlines[s].Add(new List<FnmmNote>());
            }
        }

        // ── Update ───────────────────────────────────────────────────────────
        public void Update(float dt, KeyboardState kb)
        {
            Update(dt, kb, _screenWidth, _screenHeight);
        }

        public void Update(float dt, KeyboardState kb, int screenWidth, int screenHeight)
        {
            if (screenWidth > 0 && screenHeight > 0)
            {
                _screenWidth = screenWidth;
                _screenHeight = screenHeight;
                UpdateLayout(_screenWidth, _screenHeight);
            }

            var ms = UI.PointerInput.GetState();

            if (_toastTimer > 0f)
            {
                _toastTimer -= dt;
                if (_toastTimer <= 0f) _toastMessage = null;
            }

            for (int sl = 0; sl < MAX_STRUMLINES; sl++)
            {
                for (int i = 0; i < LANE_COUNT; i++)
                {
                    if (_receptorPressTime[sl, i] > 0f)
                        _receptorPressTime[sl, i] = Math.Max(0f, _receptorPressTime[sl, i] - dt);
                }

                if (_characterSingTime[sl] > 0f)
                    _characterSingTime[sl] = Math.Max(0f, _characterSingTime[sl] - dt);
                else
                    _characterSingLane[sl] = -1;
            }

            if (!Game1.CanAcceptInput)
            {
                PausePlaybackFromFocusLoss();
                _dragMode = DragMode.None;
                if (_phase == EditorPhase.Loading && _loadingTask != null && _loadingTask.IsCompleted)
                {
                    _phase = _errorMessage != null ? EditorPhase.Error : (_level != null ? EditorPhase.Ready : EditorPhase.Error);
                    _loadingTask = null;
                }
                _prevKeyboard = default;
                _prevMouse = Game1.NeutralMouseState(ms);
                return;
            }

            if (_phase == EditorPhase.Loading)
            {
                _loadingSpinnerRotation += dt * 4f;
                if (JustPressed(kb, Keys.Escape))
                {
                    _audioManager.Stop();
                    BackPressed = true;
                }
                else if (_loadingTask != null && _loadingTask.IsCompleted)
                {
                    _phase = _errorMessage != null ? EditorPhase.Error : (_level != null ? EditorPhase.Ready : EditorPhase.Error);
                    _loadingTask = null;
                }

                _prevKeyboard = kb;
                _prevMouse = ms;
                return;
            }

            if (_phase == EditorPhase.Error)
            {
                if (JustPressed(kb, Keys.Escape) || JustPressed(kb, Keys.Enter) ||
                    (ms.LeftButton == ButtonState.Pressed && _prevMouse.LeftButton == ButtonState.Released && _backBtnRect.Contains(ms.Position)))
                {
                    _audioManager.Stop();
                    BackPressed = true;
                }
                _prevKeyboard = kb;
                _prevMouse = ms;
                return;
            }

            if (_inputCooldown > 0f)
            {
                _inputCooldown -= dt;
                _prevKeyboard = kb;
                _prevMouse = ms;
                return;
            }

            HandleKeyboard(kb, dt);
            HandleMouse(ms);

            if (_isPlaying && _level != null)
            {
                float prevSec = _playbackTime;
                if (_audioManager.HasAudioTrack)
                {
                    _audioManager.Update();
                    if (!_audioManager.IsPlaying)
                    {
                        _isPlaying = false;
                    }
                    else
                    {
                        _playbackTime = _audioManager.CurrentTime / 1000f;
                    }
                }
                else
                {
                    _playbackTime += dt;
                    float maxSec = TimeUnitsToSeconds(GetMaxTimeUnits());
                    if (_playbackTime >= maxSec)
                    {
                        _playbackTime = maxSec;
                        _isPlaying = false;
                    }
                }

                if (_isPlaying)
                {
                    _scrollY = SecondsToTimeUnits(_playbackTime);
                    float bpm = _level.Data.EffectiveBpm;
                    float prevBeat = prevSec * bpm / 60f;
                    float currBeat = _playbackTime * bpm / 60f;

                    if (_metronomeEnabled && _playbackTime >= prevSec)
                    {
                        int prevWhole = (int)MathF.Floor(prevBeat);
                        int currWhole = (int)MathF.Floor(currBeat);
                        if (currWhole > prevWhole && currWhole >= 0)
                            PlayMetronomeTick(downbeat: (currWhole % 4) == 0);
                    }

                    CheckNotesAtReceptor(prevSec, _playbackTime);
                    UpdatePreviewDuringPlay(_playbackTime, currBeat, dt);
                    _prevPlaybackTime = _playbackTime;
                }
            }
            else if (_level != null)
            {
                UpdatePreviewPaused(dt);
            }

            _prevKeyboard = kb;
            _prevMouse = ms;
        }

        // ── Coordinate & Time Unit Helpers ───────────────────────────────────
        private float TimeUnitsToSeconds(float timeUnits)
        {
            if (_level == null) return timeUnits;
            return _level.Data.ToSeconds(timeUnits);
        }

        private float SecondsToTimeUnits(float seconds)
        {
            if (_level == null || _level.Data.TimesAreSeconds)
                return seconds;
            return (seconds * _level.Data.EffectiveBpm) / 60f;
        }

        private float TimeUnitsToBeats(float timeUnits)
        {
            if (_level == null || !_level.Data.TimesAreSeconds)
                return timeUnits;
            return (timeUnits * _level.Data.EffectiveBpm) / 60f;
        }

        private float BeatsToTimeUnits(float beats)
        {
            if (_level == null || !_level.Data.TimesAreSeconds)
                return beats;
            return (beats * 60f) / _level.Data.EffectiveBpm;
        }

        private float GetMaxTimeUnits()
        {
            float durSec = Math.Max(30f, _audioManager.Duration / 1000f);
            float maxUnits = SecondsToTimeUnits(durSec);
            if (_level != null)
            {
                foreach (var sl in _level.Data.Strumlines)
                    foreach (var lane in sl)
                        foreach (var n in lane)
                            maxUnits = Math.Max(maxUnits, n.Start + n.Duration + 4f);
            }
            return maxUnits;
        }

        private float GetSnapStepBeats() => 4f / Math.Max(1, _snapDivisor);

        private float GetSnapStepUnits() => BeatsToTimeUnits(GetSnapStepBeats());

        private float SnapBeatCell(float beat)
        {
            float step = GetSnapStepBeats();
            if (step <= 0.0001f) return Math.Max(0f, beat);
            return Math.Max(0f, MathF.Floor(beat / step) * step);
        }

        private float SnapBeatNearest(float beat)
        {
            float step = GetSnapStepBeats();
            if (step <= 0.0001f) return Math.Max(0f, beat);
            return Math.Max(0f, MathF.Round(beat / step) * step);
        }

        private float GetStepHeight() => _laneWidth * _zoom;
        private float GetBeatHeight() => GetStepHeight() * STEPS_PER_BEAT;
        private float GetScaledTimeHeight()
        {
            if (_level != null && _level.Data.TimesAreSeconds)
                return GetBeatHeight() * (_level.Data.EffectiveBpm / 60f);
            return GetBeatHeight();
        }

        private float GetTotalGridWidth() => _laneWidth * GRID_COLS + _gridGroupGap;

        private float GetGridStartX()
        {
            float totalW = GetTotalGridWidth();
            float leftGutter = UiScale.Px(52, _uiScale, 32);
            float rightPad = UiScale.Px(14, _uiScale, 8);
            return _gridBodyRect.X + leftGutter + Math.Max(0f, (_gridBodyRect.Width - leftGutter - rightPad - totalW) * 0.5f);
        }

        private float GetColumnX(int col) =>
            col < LANE_COUNT
                ? GetGridStartX() + col * _laneWidth
                : GetGridStartX() + LANE_COUNT * _laneWidth + _gridGroupGap + (col - LANE_COUNT) * _laneWidth;

        private void ColToStrumAndLane(int col, out int strumIdx, out int lane)
        {
            if (col < LANE_COUNT)
            {
                strumIdx = _leftStrumline; // Left block = Opponent / selected extra track
                lane = col;
            }
            else
            {
                strumIdx = _rightStrumline; // Right block = Player (BF)
                lane = col - LANE_COUNT;
            }
        }

        private int StrumAndLaneToCol(int strumIdx, int lane)
        {
            if (strumIdx == _leftStrumline) return lane;
            if (strumIdx == _rightStrumline) return LANE_COUNT + lane;
            return -1;
        }

        private float GetReceptorY() => _gridBodyRect.Y + _gridBodyRect.Height * RECEPTOR_Y_OFFSET;

        private float ScreenYToBeat(int y) =>
            TimeUnitsToBeats(_scrollY) + (y - GetReceptorY()) / Math.Max(4f, GetBeatHeight());

        private float ScreenYToTime(int y) =>
            BeatsToTimeUnits(ScreenYToBeat(y));

        private int BeatToScreenY(float beat) =>
            (int)MathF.Round(GetReceptorY() + (beat - TimeUnitsToBeats(_scrollY)) * GetBeatHeight());

        private int TimeToScreenY(float timeUnits) =>
            BeatToScreenY(TimeUnitsToBeats(timeUnits));

        private int ScreenXToColumn(int x)
        {
            float lx = GetGridStartX();
            if (x >= lx && x < lx + _laneWidth * LANE_COUNT)
                return Math.Clamp((int)((x - lx) / _laneWidth), 0, LANE_COUNT - 1);

            float rx = GetColumnX(LANE_COUNT);
            if (x >= rx && x < rx + _laneWidth * LANE_COUNT)
                return LANE_COUNT + Math.Clamp((int)((x - rx) / _laneWidth), 0, LANE_COUNT - 1);

            return -1;
        }

        private int ScreenXToLane(int x)
        {
            int col = ScreenXToColumn(x);
            return col >= 0 ? col % LANE_COUNT : -1;
        }

        // ── Keyboard Input ───────────────────────────────────────────────────
        private void HandleKeyboard(KeyboardState kb, float dt)
        {
            if (_charModalOpen)
            {
                HandleCharacterModalKeyboard(kb);
                return;
            }

            bool ctrl = kb.IsKeyDown(Keys.LeftControl) || kb.IsKeyDown(Keys.RightControl);
            bool shift = kb.IsKeyDown(Keys.LeftShift) || kb.IsKeyDown(Keys.RightShift);

            if (JustPressed(kb, Keys.Escape))
            {
                PausePlayback();
                BackPressed = true;
                return;
            }

            if (ctrl && JustPressed(kb, Keys.S))
            {
                SaveLevel();
                return;
            }

            if (ctrl && JustPressed(kb, Keys.Z))
            {
                Undo();
                return;
            }

            if (ctrl && JustPressed(kb, Keys.Y))
            {
                Redo();
                return;
            }

            if (JustPressed(kb, Keys.Space))
            {
                TogglePlayPause();
                return;
            }

            if (JustPressed(kb, Keys.Enter))
            {
                TriggerPlaytest(fromStart: shift);
                return;
            }

            if (!ctrl && JustPressed(kb, Keys.V))
            {
                _showPreview = !_showPreview;
                UI.UiSfx.Play(UI.UiSound.Toggle);
                ShowToast(_showPreview ? "Stage Preview shown" : "Stage Preview hidden");
            }

            // Tools 1-4
            if (JustPressed(kb, Keys.D1)) SetTool(EditorTool.Select);
            if (JustPressed(kb, Keys.D2)) SetTool(EditorTool.PlaceNote);
            if (JustPressed(kb, Keys.D3)) SetTool(EditorTool.PlaceHold);
            if (JustPressed(kb, Keys.D4)) SetTool(EditorTool.Delete);

            // Clipboard Copy / Paste (Ctrl+C / Ctrl+V)
            if (ctrl && JustPressed(kb, Keys.C)) { CopyCurrentMeasure(); return; }
            if (ctrl && JustPressed(kb, Keys.V)) { PasteClipboardAtPlayhead(); return; }

            // Focus Strumlines Q=BF (Right), W=Left Track, Tab=Cycle Extra Strumlines (3+)
            if (JustPressed(kb, Keys.Q)) SwitchStrumline(_rightStrumline);
            if (JustPressed(kb, Keys.W)) SwitchStrumline(_leftStrumline);
            if (JustPressed(kb, Keys.Tab)) CycleLeftStrumline(shift ? -1 : 1);

            // Stage Preview Cam / Audio Cues / Mirror shortcuts (when Ctrl not held)
            if (!ctrl && JustPressed(kb, Keys.C)) CyclePreviewCamera();
            if (!ctrl && JustPressed(kb, Keys.H))
            {
                _hitsoundsEnabled = !_hitsoundsEnabled; UI.UiSfx.Play(UI.UiSound.Toggle);
                ShowToast(_hitsoundsEnabled ? "Hitsounds ON" : "Hitsounds OFF");
            }
            if (!ctrl && JustPressed(kb, Keys.N))
            {
                _metronomeEnabled = !_metronomeEnabled; UI.UiSfx.Play(UI.UiSound.Toggle);
                ShowToast(_metronomeEnabled ? "Metronome ON" : "Metronome OFF");
            }
            if (!ctrl && JustPressed(kb, Keys.M)) MirrorCurrentMeasure();
            if (!ctrl && JustPressed(kb, Keys.B)) { ToggleCharacterModal(); return; }

            // Snap cycle Z/X
            if (JustPressed(kb, Keys.Z)) CycleSnap(-1);
            if (JustPressed(kb, Keys.X)) CycleSnap(1);

            // Hold duration [ / ]
            if (JustPressed(kb, Keys.OemOpenBrackets)) AdjustHoldDuration(-1);
            if (JustPressed(kb, Keys.OemCloseBrackets)) AdjustHoldDuration(1);

            // Note preset cycle T (Normal → Hurt → Instakill → Heal)
            if (JustPressed(kb, Keys.T))
            {
                if (_selectedNote != null)
                {
                    CycleSelectedNotePreset();
                }
                else
                {
                    _activePreset = NextPreset(_activePreset);
                    UI.UiSfx.Play(UI.UiSound.Toggle);
                    ShowToast($"Brush: {PresetName(_activePreset)}");
                }
            }

            // Delete selected note
            if ((JustPressed(kb, Keys.Delete) || JustPressed(kb, Keys.Back)) && _selectedNote != null)
            {
                DeleteSpecificNote(_selectedNoteStrumline, _selectedNoteLane, _selectedNote);
                _selectedNote = null;
            }

            // Live/Step Note Input on focused strumline (D F J K = Left Down Up Right)
            if (!ctrl)
            {
                if (JustPressed(kb, Keys.D)) StepToggleNoteAtPlayhead(0);
                if (JustPressed(kb, Keys.F)) StepToggleNoteAtPlayhead(1);
                if (JustPressed(kb, Keys.J)) StepToggleNoteAtPlayhead(2);
                if (JustPressed(kb, Keys.K)) StepToggleNoteAtPlayhead(3);
            }

            if (_isPlaying) return;

            // Scroll Up/Down (Beat-normalized speed), PageUp/PageDown (Measure jump), or Home
            float scrollRate = BeatsToTimeUnits((shift ? 12f : 5f) * dt);
            if (kb.IsKeyDown(Keys.Up)) ScrollTo(_scrollY - scrollRate);
            if (kb.IsKeyDown(Keys.Down)) ScrollTo(_scrollY + scrollRate);
            if (JustPressed(kb, Keys.PageUp)) ScrollTo(BeatsToTimeUnits(Math.Max(0f, SnapBeatCell(TimeUnitsToBeats(_scrollY)) - 4f)));
            if (JustPressed(kb, Keys.PageDown)) ScrollTo(BeatsToTimeUnits(SnapBeatCell(TimeUnitsToBeats(_scrollY)) + 4f));
            if (JustPressed(kb, Keys.Home)) ScrollTo(0f);

            // Move selected note Left/Right across all 8 grid columns when in Select mode
            if (_selectedNote != null && JustPressed(kb, Keys.Left))
            {
                int col = StrumAndLaneToCol(_selectedNoteStrumline, _selectedNoteLane);
                if (col > 0)
                {
                    ColToStrumAndLane(col - 1, out int dstStrum, out int dstLane);
                    MoveNote(_selectedNote, _selectedNoteStrumline, _selectedNoteLane, dstStrum, dstLane, _selectedNote.Start);
                }
            }
            if (_selectedNote != null && JustPressed(kb, Keys.Right))
            {
                int col = StrumAndLaneToCol(_selectedNoteStrumline, _selectedNoteLane);
                if (col >= 0 && col < GRID_COLS - 1)
                {
                    ColToStrumAndLane(col + 1, out int dstStrum, out int dstLane);
                    MoveNote(_selectedNote, _selectedNoteStrumline, _selectedNoteLane, dstStrum, dstLane, _selectedNote.Start);
                }
            }

            // Grid Zoom +/-
            if (JustPressed(kb, Keys.OemPlus) || JustPressed(kb, Keys.Add))
                ChangeZoom(0.25f);
            if (JustPressed(kb, Keys.OemMinus) || JustPressed(kb, Keys.Subtract))
                ChangeZoom(-0.25f);

            // Gameplay Note Scroll Speed O/P
            if (JustPressed(kb, Keys.O)) ChangeChartSpeed(-0.1f);
            if (JustPressed(kb, Keys.P)) ChangeChartSpeed(0.1f);
        }

        // ── Audio Cues, Step Input, Multi-Strumline & Clipboard ──────────────
        private void EnsureAudioCuesInitialized()
        {
            if (_audioCuesInitialized) return;
            _audioCuesInitialized = true;
            try
            {
                _hitSoundFx = CreateToneFx(1150f, 0.036f, 55f, 0.55f);
                _metroBeatFx = CreateToneFx(1320f, 0.048f, 42f, 0.60f);
                _metroSubFx = CreateToneFx(880f, 0.038f, 52f, 0.42f);
            }
            catch
            {
                // Headless or audio-disabled environments safely ignore cue synth errors
            }
        }

        private static SoundEffect CreateToneFx(float freqHz, float durationSec, float decayRate, float volume)
        {
            const int sampleRate = 44100;
            int samples = Math.Max(64, (int)(sampleRate * durationSec));
            byte[] pcm = new byte[samples * 2];
            for (int i = 0; i < samples; i++)
            {
                float t = i / (float)sampleRate;
                float env = MathF.Exp(-t * decayRate);
                float wave = MathF.Sin(2f * MathF.PI * freqHz * t) * 0.7f
                           + MathF.Sin(2f * MathF.PI * freqHz * 2.1f * t) * 0.3f;
                short sample = (short)Math.Clamp(wave * env * volume * short.MaxValue, short.MinValue, short.MaxValue);
                pcm[i * 2] = (byte)(sample & 0xFF);
                pcm[i * 2 + 1] = (byte)((sample >> 8) & 0xFF);
            }
            return new SoundEffect(pcm, sampleRate, AudioChannels.Mono);
        }

        private void PlayHitsound()
        {
            if (!_hitsoundsEnabled) return;
            EnsureAudioCuesInitialized();
            try { _hitSoundFx?.Play(0.70f * Game1.MasterVolume, 0f, 0f); } catch { }
        }

        private void PlayMetronomeTick(bool downbeat)
        {
            if (!_metronomeEnabled) return;
            EnsureAudioCuesInitialized();
            try { (downbeat ? _metroBeatFx : _metroSubFx)?.Play((downbeat ? 0.75f : 0.52f) * Game1.MasterVolume, 0f, 0f); } catch { }
        }

        private void StepToggleNoteAtPlayhead(int lane)
        {
            if (_level == null || lane < 0 || lane >= LANE_COUNT) return;
            if (_selectedStrumline < 0 || _selectedStrumline >= _level.Data.Strumlines.Count) return;

            float targetBeat = SnapBeatNearest(TimeUnitsToBeats(_scrollY));
            float targetUnits = BeatsToTimeUnits(targetBeat);
            float tolUnits = Math.Max(0.005f, GetSnapStepUnits() * 0.45f);

            var laneNotes = _level.Data.Strumlines[_selectedStrumline][lane];
            FnmmNote? existing = null;
            foreach (var n in laneNotes)
            {
                if (MathF.Abs(n.Start - targetUnits) <= tolUnits)
                {
                    existing = n;
                    break;
                }
            }

            _receptorPressTime[Math.Min(_selectedStrumline, MAX_STRUMLINES - 1), lane] = RECEPTOR_ANIM_DURATION;
            _selectedLane = lane;

            if (existing != null)
            {
                DeleteSpecificNote(_selectedStrumline, lane, existing);
                if (_selectedNote == existing) _selectedNote = null;
            }
            else
            {
                float dur = _currentTool == EditorTool.PlaceHold
                    ? BeatsToTimeUnits(Math.Max(GetSnapStepBeats(), _defaultHoldBeats))
                    : 0f;
                var created = CreateAndAddNote(_selectedStrumline, lane, targetUnits, dur, BuildPresetProps(_activePreset));
                if (created != null)
                    SelectNote(created, _selectedStrumline, lane);
            }
        }

        private void CycleLeftStrumline(int dir)
        {
            if (_level == null) return;
            int count = _level.Data.Strumlines.Count;
            if (count <= 2)
            {
                int toggle = _selectedStrumline == _rightStrumline ? _leftStrumline : _rightStrumline;
                SwitchStrumline(toggle);
                ShowToast(toggle == _rightStrumline ? "Focus: Player (BF)" : "Focus: Opponent (FOE)");
                return;
            }

            int extraCount = count - 1; // 1 .. count-1
            int offset = (_leftStrumline - 1 + dir + extraCount * 4) % extraCount;
            _leftStrumline = 1 + offset;
            _selectedStrumline = _leftStrumline;
            UI.UiSfx.Play(UI.UiSound.SubTab);
            _selectedNote = null;
            ShowToast($"Left Track: SL{_leftStrumline} ({CountStrumNotes(_leftStrumline)} notes)");
        }

        private void CyclePreviewCamera()
        {
            int camCount = _previewLevel?.Scene.Cameras.Count ?? _level?.Scene.Cameras.Count ?? 0;
            if (_previewCameraMode == -1)
                _previewCameraMode = -2;
            else if (_previewCameraMode == -2)
                _previewCameraMode = camCount > 0 ? 0 : -1;
            else if (_previewCameraMode + 1 < camCount)
                _previewCameraMode++;
            else
                _previewCameraMode = -1;

            _previewNeedsSeek = true;
            UI.UiSfx.Play(UI.UiSound.SubTab);
            ShowToast($"Preview Cam: {GetPreviewCameraLabel()}");
        }

        private string GetPreviewCameraLabel() => _previewCameraMode switch
        {
            -1 => "AUTO",
            -2 => "FIT",
            _ => $"CAM {_previewCameraMode + 1}",
        };

        private void CopyCurrentMeasure()
        {
            if (_level == null || _selectedStrumline >= _level.Data.Strumlines.Count) return;
            float playBeat = Math.Max(0f, TimeUnitsToBeats(_scrollY));
            int barIdx = (int)MathF.Floor(playBeat / 4f);
            float barStart = barIdx * 4f;
            float barEnd = barStart + 4f;

            _clipboardNotes.Clear();
            var strum = _level.Data.Strumlines[_selectedStrumline];
            for (int lane = 0; lane < LANE_COUNT && lane < strum.Count; lane++)
            {
                foreach (var n in strum[lane])
                {
                    float b = TimeUnitsToBeats(n.Start);
                    if (b >= barStart - 0.002f && b < barEnd - 0.002f)
                        _clipboardNotes.Add((lane, Math.Max(0f, b - barStart), n.Duration, n.Props.Skin, n.Props.OnPressEvent, n.Props.OnMissEvent));
                }
            }

            UI.UiSfx.Play((_clipboardNotes.Count > 0 ? UI.UiSound.Confirm : UI.UiSound.Error));
            ShowToast(_clipboardNotes.Count > 0
                ? $"Copied {_clipboardNotes.Count} notes (Bar #{barIdx + 1})"
                : $"No notes in Bar #{barIdx + 1} (SL{_selectedStrumline})");
        }

        private void PasteClipboardAtPlayhead()
        {
            if (_level == null || _clipboardNotes.Count == 0)
            {
                UI.UiSfx.Play(UI.UiSound.Error);
                ShowToast("Clipboard empty (Ctrl+C inside a measure first)");
                return;
            }
            if (_selectedStrumline >= _level.Data.Strumlines.Count) return;

            float playBeat = Math.Max(0f, TimeUnitsToBeats(_scrollY));
            int barIdx = (int)MathF.Floor(playBeat / 4f);
            float targetBarStart = barIdx * 4f;
            var strum = _level.Data.Strumlines[_selectedStrumline];
            var added = new List<(int lane, FnmmNote note)>();

            foreach (var item in _clipboardNotes)
            {
                if (item.lane < 0 || item.lane >= LANE_COUNT) continue;
                float destUnits = BeatsToTimeUnits(targetBarStart + item.relBeat);
                if (strum[item.lane].Any(n => MathF.Abs(n.Start - destUnits) < 0.005f))
                    continue;
                added.Add((item.lane, new FnmmNote
                {
                    Start = destUnits,
                    Duration = item.duration,
                    Props = new NoteProps { Skin = item.skin, OnPressEvent = item.onPress, OnMissEvent = item.onMiss }
                }));
            }

            if (added.Count == 0)
            {
                UI.UiSfx.Play(UI.UiSound.AlreadyInUse);
                ShowToast("All copied notes already present at target bar");
                return;
            }

            UI.UiSfx.Play(UI.UiSound.Confirm);
            _undoManager.Do(new DelegateEditorAction(
                () =>
                {
                    foreach (var (lane, note) in added)
                        if (!strum[lane].Contains(note)) strum[lane].Add(note);
                },
                () =>
                {
                    foreach (var (lane, note) in added)
                        strum[lane].Remove(note);
                }
            ));
            MarkPreviewDirty();
            ShowToast($"Pasted {added.Count} notes to Bar #{barIdx + 1}");
        }

        private void MirrorCurrentMeasure()
        {
            if (_level == null || _selectedStrumline >= _level.Data.Strumlines.Count) return;
            float playBeat = Math.Max(0f, TimeUnitsToBeats(_scrollY));
            int barIdx = (int)MathF.Floor(playBeat / 4f);
            float barStart = barIdx * 4f;
            float barEnd = barStart + 4f;

            var strum = _level.Data.Strumlines[_selectedStrumline];
            var moves = new List<(FnmmNote note, int fromLane, int toLane)>();
            for (int lane = 0; lane < LANE_COUNT && lane < strum.Count; lane++)
            {
                foreach (var n in strum[lane])
                {
                    float b = TimeUnitsToBeats(n.Start);
                    if (b >= barStart - 0.002f && b < barEnd - 0.002f)
                        moves.Add((n, lane, LANE_COUNT - 1 - lane));
                }
            }

            if (moves.Count == 0)
            {
                UI.UiSfx.Play(UI.UiSound.Error);
                ShowToast($"No notes to mirror in Bar #{barIdx + 1}");
                return;
            }

            UI.UiSfx.Play(UI.UiSound.CycleNext);
            _undoManager.Do(new DelegateEditorAction(
                () =>
                {
                    foreach (var (n, from, _) in moves) strum[from].Remove(n);
                    foreach (var (n, _, to) in moves) if (!strum[to].Contains(n)) strum[to].Add(n);
                },
                () =>
                {
                    foreach (var (n, _, to) in moves) strum[to].Remove(n);
                    foreach (var (n, from, _) in moves) if (!strum[from].Contains(n)) strum[from].Add(n);
                }
            ));
            _selectedNote = null;
            MarkPreviewDirty();
            ShowToast($"Mirrored {moves.Count} notes in Bar #{barIdx + 1}");
        }

        // ── Mouse Input ──────────────────────────────────────────────────────
        private void HandleMouse(MouseState ms)
        {
            if (_charModalOpen)
            {
                HandleCharacterModalMouse(ms);
                return;
            }

            var pt = ms.Position;
            bool leftDown = ms.LeftButton == ButtonState.Pressed;
            bool leftJustPressed = leftDown && _prevMouse.LeftButton == ButtonState.Released;
            bool leftJustReleased = !leftDown && _prevMouse.LeftButton == ButtonState.Pressed;
            bool rightJustPressed = ms.RightButton == ButtonState.Pressed && _prevMouse.RightButton == ButtonState.Released;
            var kbNow = LayoutKeyboard.GetState();
            bool shift = kbNow.IsKeyDown(Keys.LeftShift) || kbNow.IsKeyDown(Keys.RightShift);
            bool ctrl = kbNow.IsKeyDown(Keys.LeftControl) || kbNow.IsKeyDown(Keys.RightControl);

            // Mouse wheel: Ctrl+Wheel zooms grid, Shift+Wheel edits hold len, Wheel scrolls chart
            int wheelDelta = ms.ScrollWheelValue - _prevMouse.ScrollWheelValue;
            if (wheelDelta != 0 && _level != null)
            {
                if (ctrl)
                {
                    ChangeZoom(wheelDelta > 0 ? 0.25f : -0.25f);
                }
                else if (shift)
                {
                    AdjustHoldDuration(wheelDelta > 0 ? 1 : -1);
                }
                else
                {
                    float step = GetSnapStepUnits() * 2f;
                    ScrollTo(_scrollY - (wheelDelta / 120f) * step);
                }
            }

            // Track hover cell in 8-column grid
            if (_gridBodyRect.Contains(pt))
            {
                _hoverCol = ScreenXToColumn(pt.X);
                if (_hoverCol >= 0)
                {
                    ColToStrumAndLane(_hoverCol, out _hoverStrumline, out _hoverLane);
                    _hoverSnappedBeat = SnapBeatCell(ScreenYToBeat(pt.Y));
                }
                else
                {
                    _hoverStrumline = -1;
                    _hoverLane = -1;
                    _hoverSnappedBeat = -1f;
                }
            }
            else
            {
                _hoverCol = -1;
                _hoverStrumline = -1;
                _hoverLane = -1;
                _hoverSnappedBeat = -1f;
            }

            // Active drags take priority
            if (_dragMode != DragMode.None)
            {
                UpdateDrag(pt, leftDown, leftJustReleased);
                return;
            }

            // Right-click in grid always quick-deletes under cursor (works on either FOE or BF column)
            if (rightJustPressed && _gridBodyRect.Contains(pt))
            {
                int col = ScreenXToColumn(pt.X);
                if (col >= 0)
                {
                    ColToStrumAndLane(col, out int strumIdx, out int lane);
                    var hit = FindNoteAt(strumIdx, lane, pt.Y);
                    if (hit != null)
                    {
                        DeleteSpecificNote(strumIdx, lane, hit);
                        if (_selectedNote == hit) _selectedNote = null;
                    }
                }
                return;
            }

            if (!leftJustPressed) return;

            // Scrubber click/drag
            if (_scrubberRect.Contains(pt))
            {
                _dragMode = DragMode.Scrubber;
                SeekFromScrubberX(pt.X);
                return;
            }

            // Clickable buttons across Timeline, Toolbar, InfoPanel, Preview close
            foreach (var btn in _clickActions)
            {
                if (btn.Rect != Rectangle.Empty && btn.Rect.Contains(pt))
                {
                    btn.OnClick();
                    return;
                }
            }

            // Grid click
            if (_gridBodyRect.Contains(pt))
            {
                HandleGridMouseDown(pt);
            }
        }

        private void HandleGridMouseDown(Point pt)
        {
            if (_level == null) return;
            int col = ScreenXToColumn(pt.X);
            if (col < 0) return;

            ColToStrumAndLane(col, out int strumIdx, out int lane);
            _selectedStrumline = strumIdx;
            _selectedLane = lane;
            _receptorPressTime[strumIdx, lane] = RECEPTOR_ANIM_DURATION;

            float rawBeat = ScreenYToBeat(pt.Y);
            float snappedBeat = SnapBeatCell(rawBeat);
            float snappedTime = BeatsToTimeUnits(snappedBeat);

            var hitNote = FindNoteAt(strumIdx, lane, pt.Y);

            switch (_currentTool)
            {
                case EditorTool.Select:
                    if (hitNote != null)
                    {
                        SelectNote(hitNote, strumIdx, lane);
                        if (hitNote.Duration > 0f && IsNearHoldTail(hitNote, pt.Y))
                        {
                            BeginDrag(DragMode.ResizeHold, hitNote, strumIdx, lane, ScreenYToTime(pt.Y));
                        }
                        else
                        {
                            BeginDrag(DragMode.MoveNote, hitNote, strumIdx, lane, ScreenYToTime(pt.Y));
                        }
                    }
                    else
                    {
                        _selectedNote = null;
                    }
                    break;

                case EditorTool.PlaceNote:
                    if (hitNote != null && MathF.Abs(TimeUnitsToBeats(hitNote.Start) - snappedBeat) < GetSnapStepBeats() * 0.45f)
                    {
                        SelectNote(hitNote, strumIdx, lane);
                    }
                    else
                    {
                        var created = CreateAndAddNote(strumIdx, lane, snappedTime, 0f, BuildPresetProps(_activePreset));
                        if (created != null) SelectNote(created, strumIdx, lane);
                    }
                    break;

                case EditorTool.PlaceHold:
                    if (hitNote != null && hitNote.Duration > 0f && IsNearHoldTail(hitNote, pt.Y))
                    {
                        SelectNote(hitNote, strumIdx, lane);
                        BeginDrag(DragMode.ResizeHold, hitNote, strumIdx, lane, ScreenYToTime(pt.Y));
                    }
                    else
                    {
                        float initBeats = Math.Max(GetSnapStepBeats(), _defaultHoldBeats);
                        float initDur = BeatsToTimeUnits(initBeats);
                        var createdHold = CreateAndAddNote(strumIdx, lane, snappedTime, initDur, BuildPresetProps(_activePreset));
                        if (createdHold != null)
                        {
                            SelectNote(createdHold, strumIdx, lane);
                            BeginDrag(DragMode.NewHoldTail, createdHold, strumIdx, lane, ScreenYToTime(pt.Y));
                        }
                    }
                    break;

                case EditorTool.Delete:
                    if (hitNote != null)
                    {
                        DeleteSpecificNote(strumIdx, lane, hitNote);
                        if (_selectedNote == hitNote) _selectedNote = null;
                    }
                    break;
            }
        }

        private void BeginDrag(DragMode mode, FnmmNote note, int strumline, int lane, float rawTime)
        {
            _dragMode = mode;
            _dragNote = note;
            _dragOrigStrumline = strumline;
            _dragOrigLane = lane;
            _dragOrigStart = note.Start;
            _dragOrigDuration = note.Duration;
            _dragGrabOffset = rawTime - note.Start;
        }

        private void UpdateDrag(Point pt, bool leftDown, bool leftJustReleased)
        {
            if (_dragMode == DragMode.Scrubber)
            {
                if (leftDown) SeekFromScrubberX(pt.X);
                if (!leftDown || leftJustReleased) _dragMode = DragMode.None;
                return;
            }

            if (_dragNote == null || _level == null)
            {
                _dragMode = DragMode.None;
                return;
            }

            float rawTime = ScreenYToTime(pt.Y);

            if (_dragMode == DragMode.MoveNote)
            {
                int targetCol = ScreenXToColumn(pt.X);
                int targetStrum = _selectedNoteStrumline;
                int targetLane = _selectedNoteLane;
                if (targetCol >= 0)
                    ColToStrumAndLane(targetCol, out targetStrum, out targetLane);

                float newBeat = SnapBeatNearest(TimeUnitsToBeats(rawTime - _dragGrabOffset));
                float newStart = BeatsToTimeUnits(newBeat);

                if (targetStrum != _selectedNoteStrumline || targetLane != _selectedNoteLane)
                {
                    _level.Data.Strumlines[_selectedNoteStrumline][_selectedNoteLane].Remove(_dragNote);
                    _level.Data.Strumlines[targetStrum][targetLane].Add(_dragNote);
                    _selectedNoteStrumline = targetStrum;
                    _selectedStrumline = targetStrum;
                    _selectedNoteLane = targetLane;
                    _selectedLane = targetLane;
                }
                _dragNote.Start = newStart;

                if (leftJustReleased || !leftDown)
                {
                    float finalStart = _dragNote.Start;
                    int finalStrum = _selectedNoteStrumline;
                    int finalLane = _selectedNoteLane;
                    if (finalStrum != _dragOrigStrumline || finalLane != _dragOrigLane || MathF.Abs(finalStart - _dragOrigStart) > 0.0001f)
                    {
                        _level.Data.Strumlines[finalStrum][finalLane].Remove(_dragNote);
                        _level.Data.Strumlines[_dragOrigStrumline][_dragOrigLane].Add(_dragNote);
                        _dragNote.Start = _dragOrigStart;
                        MoveNote(_dragNote, _dragOrigStrumline, _dragOrigLane, finalStrum, finalLane, finalStart);
                    }
                    _dragMode = DragMode.None;
                    _dragNote = null;
                }
            }
            else if (_dragMode == DragMode.ResizeHold || _dragMode == DragMode.NewHoldTail)
            {
                float startBeat = TimeUnitsToBeats(_dragNote.Start);
                float endBeat = SnapBeatCell(ScreenYToBeat(pt.Y));
                float minStep = GetSnapStepBeats();
                float durBeats = Math.Max(_dragMode == DragMode.NewHoldTail ? minStep : 0f, endBeat - startBeat);
                if (durBeats > 0f && durBeats < minStep * 0.5f) durBeats = 0f;
                _dragNote.Duration = BeatsToTimeUnits(durBeats);

                if (leftJustReleased || !leftDown)
                {
                    float finalDur = _dragNote.Duration;
                    if (_dragMode == DragMode.ResizeHold && MathF.Abs(finalDur - _dragOrigDuration) > 0.0001f)
                    {
                        var targetNote = _dragNote;
                        float oldDur = _dragOrigDuration;
                        targetNote.Duration = oldDur;
                        _undoManager.Do(new DelegateEditorAction(
                            () => { targetNote.Duration = finalDur; MarkPreviewDirty(); },
                            () => { targetNote.Duration = oldDur; MarkPreviewDirty(); }
                        ));
                    }
                    else if (_dragMode == DragMode.NewHoldTail && durBeats > 0f)
                    {
                        _defaultHoldBeats = durBeats;
                        MarkPreviewDirty();
                    }
                    _dragMode = DragMode.None;
                    _dragNote = null;
                }
            }
        }

        // ── Chart Editing Actions (with Undo/Redo) ───────────────────────────
        private FnmmNote? CreateAndAddNote(int strumIdx, int lane, float start, float duration, NoteProps props)
        {
            if (_level == null || strumIdx < 0 || strumIdx >= _level.Data.Strumlines.Count) return null;
            var strum = _level.Data.Strumlines[strumIdx];
            if (lane < 0 || lane >= strum.Count) return null;

            if (strum[lane].Any(n => MathF.Abs(n.Start - start) < 0.005f))
            {
                UI.UiSfx.Play(UI.UiSound.Error);
                ShowToast("Note already exists at that beat");
                return null;
            }

            var note = new FnmmNote
            {
                Start = start,
                Duration = duration,
                Props = props
            };

            _undoManager.Do(new DelegateEditorAction(
                () =>
                {
                    if (!strum[lane].Contains(note))
                        strum[lane].Add(note);
                    MarkPreviewDirty();
                },
                () =>
                {
                    strum[lane].Remove(note);
                    MarkPreviewDirty();
                }
            ));
            PlayHitsound();
            return note;
        }

        private void DeleteSpecificNote(int strumIdx, int lane, FnmmNote note)
        {
            if (_level == null || strumIdx < 0 || strumIdx >= _level.Data.Strumlines.Count) return;
            var strum = _level.Data.Strumlines[strumIdx];
            if (lane < 0 || lane >= strum.Count) return;

            UI.UiSfx.Play(UI.UiSound.Delete);
            _undoManager.Do(new DelegateEditorAction(
                () =>
                {
                    strum[lane].Remove(note);
                    MarkPreviewDirty();
                },
                () =>
                {
                    if (!strum[lane].Contains(note))
                        strum[lane].Add(note);
                    MarkPreviewDirty();
                }
            ));
        }

        private void MoveNote(FnmmNote note, int fromStrum, int fromLane, int toStrum, int toLane, float newStart)
        {
            if (_level == null) return;
            float oldStart = note.Start;
            var srcList = _level.Data.Strumlines[fromStrum][fromLane];
            var dstList = _level.Data.Strumlines[toStrum][toLane];
            UI.UiSfx.Play(UI.UiSound.Release);

            _undoManager.Do(new DelegateEditorAction(
                () =>
                {
                    srcList.Remove(note);
                    if (!dstList.Contains(note)) dstList.Add(note);
                    note.Start = newStart;
                    _selectedNoteStrumline = toStrum;
                    _selectedNoteLane = toLane;
                    _selectedLane = toLane;
                    MarkPreviewDirty();
                },
                () =>
                {
                    dstList.Remove(note);
                    if (!srcList.Contains(note)) srcList.Add(note);
                    note.Start = oldStart;
                    _selectedNoteStrumline = fromStrum;
                    _selectedNoteLane = fromLane;
                    _selectedLane = fromLane;
                    MarkPreviewDirty();
                }
            ));
        }

        private void SelectNote(FnmmNote note, int strumIdx, int lane)
        {
            _selectedNote = note;
            _selectedNoteStrumline = strumIdx;
            _selectedNoteLane = lane;
            _selectedLane = lane;
        }

        private FnmmNote? FindNoteAt(int strumIdx, int lane, int mouseY)
        {
            if (_level == null || strumIdx < 0 || strumIdx >= _level.Data.Strumlines.Count) return null;
            var notes = _level.Data.Strumlines[strumIdx][lane];

            float stepH = GetStepHeight();
            float headHalfH = Math.Max(14f * _uiScale, stepH * 0.48f);
            float tailPad = Math.Max(10f * _uiScale, stepH * 0.35f);

            FnmmNote? best = null;
            float bestDist = float.MaxValue;

            foreach (var n in notes)
            {
                float headCenterY = BeatToScreenY(TimeUnitsToBeats(n.Start)) + stepH * 0.5f;
                float headDist = MathF.Abs(mouseY - headCenterY);
                if (headDist <= headHalfH && headDist < bestDist)
                {
                    best = n;
                    bestDist = headDist;
                    continue;
                }

                if (n.Duration > 0f)
                {
                    float tailCenterY = BeatToScreenY(TimeUnitsToBeats(n.Start + n.Duration)) + stepH * 0.5f;
                    if (mouseY >= headCenterY && mouseY <= tailCenterY + tailPad && best == null)
                    {
                        best = n;
                        bestDist = headHalfH;
                    }
                }
            }
            return best;
        }

        private bool IsNearHoldTail(FnmmNote note, int mouseY)
        {
            if (note.Duration <= 0f) return false;
            float stepH = GetStepHeight();
            float tailCenterY = BeatToScreenY(TimeUnitsToBeats(note.Start + note.Duration)) + stepH * 0.5f;
            float handleTol = Math.Max(10f * _uiScale, stepH * 0.36f);
            return MathF.Abs(mouseY - tailCenterY) <= handleTol;
        }

        // ── Quick Controls & Actions ─────────────────────────────────────────
        private void SetTool(EditorTool tool)
        {
            if (_currentTool != tool) UI.UiSfx.Play(UI.UiSound.Focus);
            _currentTool = tool;
        }

        private void SwitchStrumline(int idx)
        {
            if (_level == null || idx < 0 || idx >= _level.Data.Strumlines.Count) return;
            if (idx > 0 && idx != _rightStrumline) _leftStrumline = idx;
            if (_selectedStrumline != idx) UI.UiSfx.Play(UI.UiSound.Tab);
            _selectedStrumline = idx;
            _selectedNote = null;
        }

        private void SwapSelectedNoteStrumline()
        {
            if (_selectedNote == null || _level == null || _level.Data.Strumlines.Count < 2) return;
            int toStrum = _selectedNoteStrumline == _rightStrumline ? _leftStrumline : _rightStrumline;
            if (toStrum == _selectedNoteStrumline || toStrum < 0 || toStrum >= _level.Data.Strumlines.Count) return;
            MoveNote(_selectedNote, _selectedNoteStrumline, _selectedNoteLane, toStrum, _selectedNoteLane, _selectedNote.Start);
        }

        private void CycleSnap(int dir)
        {
            int idx = Array.IndexOf(SNAP_VALUES, _snapDivisor);
            if (idx < 0) idx = 0;
            int newIdx = Math.Clamp(idx + dir, 0, SNAP_VALUES.Length - 1);
            UI.UiSfx.Play((newIdx == idx ? UI.UiSound.SliderEnd : dir < 0 ? UI.UiSound.SliderDown : UI.UiSound.SliderUp));
            idx = newIdx;
            _snapDivisor = SNAP_VALUES[idx];
            ShowToast($"Snap: 1/{_snapDivisor}");
        }

        private void AdjustHoldDuration(int dir)
        {
            UI.UiSfx.Play((dir < 0 ? UI.UiSound.SliderDown : UI.UiSound.SliderUp));
            float stepBeats = GetSnapStepBeats();
            if (_selectedNote != null)
            {
                float oldDur = _selectedNote.Duration;
                float newBeats = Math.Max(0f, TimeUnitsToBeats(oldDur) + dir * stepBeats);
                if (newBeats > 0f && newBeats < stepBeats * 0.5f) newBeats = 0f;
                float newDur = BeatsToTimeUnits(newBeats);
                var target = _selectedNote;
                _undoManager.Do(new DelegateEditorAction(
                    () => { target.Duration = newDur; MarkPreviewDirty(); },
                    () => { target.Duration = oldDur; MarkPreviewDirty(); }
                ));
                if (newBeats > 0f) _defaultHoldBeats = newBeats;
            }
            else
            {
                _defaultHoldBeats = Math.Clamp(_defaultHoldBeats + dir * stepBeats, stepBeats, stepBeats * 32f);
                ShowToast($"Hold Len: {_defaultHoldBeats:F2} beats");
            }
        }

        private void CycleSelectedNotePreset()
        {
            if (_selectedNote == null || _level == null) return;
            var target = _selectedNote;
            NotePreset? current = DetectPreset(target.Props);
            // Custom notes (skin/events not matching a preset) restart the cycle at Normal.
            NotePreset next = current.HasValue ? NextPreset(current.Value) : NotePreset.Normal;

            var old = (target.Props.Skin, target.Props.OnPressEvent, target.Props.OnMissEvent);
            var built = BuildPresetProps(next);
            var neu = (built.Skin, built.OnPressEvent, built.OnMissEvent);

            UI.UiSfx.Play(UI.UiSound.Toggle);
            _undoManager.Do(new DelegateEditorAction(
                () => { (target.Props.Skin, target.Props.OnPressEvent, target.Props.OnMissEvent) = neu; MarkPreviewDirty(); },
                () => { (target.Props.Skin, target.Props.OnPressEvent, target.Props.OnMissEvent) = old; MarkPreviewDirty(); }
            ));
            ShowToast(current.HasValue
                ? $"Note: {PresetName(next)}"
                : $"Note: {PresetName(next)} (custom skin/events replaced, Ctrl+Z to undo)");
        }

        // ── Note presets ─────────────────────────────────────────────────────
        // Skins are purely visual (see NoteSkins). A preset = a skin + the
        // onPressEvent action pack that actually gives the note its effect:
        //   Normal    → skin 0, no events
        //   Hurt      → skin 1 (negative), onPressEvent = pack { PlayerHealth remove 6 }   (20%)
        //   Instakill → skin 1 (negative), onPressEvent = pack { PlayerHealth remove 30 }  (100%)
        //   Heal      → skin 2 (positive), onPressEvent = pack { PlayerHealth add 5 }      (~17%)
        // Values use the FunkyMaker health scale (30 = full bar, NoteSkins.FmmMaxHealth).
        // Matching packs are reused; otherwise one is appended to events.action_packs.
        // Enum order = toolbar button order.
        private enum NotePreset { Normal, Hurt, Instakill, Heal }

        // FunkyMaker health points (0..30 scale, 30 = 100%)
        private const float HURT_PRESET_AMOUNT      = 6f;
        private const float INSTAKILL_PRESET_AMOUNT = NoteSkins.FmmMaxHealth;
        private const float HEAL_PRESET_AMOUNT      = 5f;

        private static NotePreset NextPreset(NotePreset p) => p switch
        {
            NotePreset.Normal    => NotePreset.Hurt,
            NotePreset.Hurt      => NotePreset.Instakill,
            NotePreset.Instakill => NotePreset.Heal,
            _                    => NotePreset.Normal,
        };

        private static string PresetName(NotePreset p) => p switch
        {
            NotePreset.Hurt      => "Hurt (negative skin)",
            NotePreset.Instakill => "Instakill (negative skin)",
            NotePreset.Heal      => "Heal (positive skin)",
            _                    => "Normal",
        };

        private static int PresetSkin(NotePreset p) => p switch
        {
            NotePreset.Hurt      => NoteSkins.Negative,
            NotePreset.Instakill => NoteSkins.Negative,
            NotePreset.Heal      => NoteSkins.Positive,
            _                    => NoteSkins.Default,
        };

        private NoteProps BuildPresetProps(NotePreset p) => p switch
        {
            NotePreset.Hurt => new NoteProps
            {
                Skin = NoteSkins.Negative,
                OnPressEvent = EnsureHealthPack("remove", HURT_PRESET_AMOUNT),
                OnMissEvent = -1f,
            },
            NotePreset.Instakill => new NoteProps
            {
                Skin = NoteSkins.Negative,
                OnPressEvent = EnsureHealthPack("remove", INSTAKILL_PRESET_AMOUNT),
                OnMissEvent = -1f,
            },
            NotePreset.Heal => new NoteProps
            {
                Skin = NoteSkins.Positive,
                OnPressEvent = EnsureHealthPack("add", HEAL_PRESET_AMOUNT),
                OnMissEvent = -1f,
            },
            _ => new NoteProps { Skin = NoteSkins.Default, OnPressEvent = -1f, OnMissEvent = -1f },
        };

        /// <summary>Returns the index of an action pack that only does "PlayerHealth op value", creating it if needed.</summary>
        private int EnsureHealthPack(string operation, float value)
        {
            if (_level == null) return -1;
            var packs = _level.Events.ActionPacks;
            for (int i = 0; i < packs.Count; i++)
                if (IsHealthPack(packs[i], operation, value))
                    return i;

            packs.Add(new ActionPack
            {
                Actions =
                {
                    new EventAction
                    {
                        Type = "PlayerHealth",
                        Props = new EventActionProps
                        {
                            Operation = operation,
                            MinHealth = 0f,
                            ValueRaw = System.Text.Json.JsonSerializer.SerializeToElement((double)value),
                        }
                    }
                }
            });
            _manualModified = true;
            ShowToast($"Added action pack #{packs.Count - 1}: PlayerHealth {operation} {value:0}");
            return packs.Count - 1;
        }

        private static bool IsHealthPack(ActionPack pack, string operation, float value)
        {
            if (pack.Actions.Count != 1) return false;
            var a = pack.Actions[0];
            return a.Type == "PlayerHealth"
                && a.Props.Operation == operation
                && MathF.Abs((a.Props.ValueFloat ?? float.NaN) - value) < 0.001f
                && (a.Props.MinHealth ?? 0f) == 0f;
        }

        private bool PackIs(int packIndex, string operation, float value)
            => _level != null && packIndex >= 0 && packIndex < _level.Events.ActionPacks.Count
               && IsHealthPack(_level.Events.ActionPacks[packIndex], operation, value);

        /// <summary>Which preset a note matches, or null if it uses a custom skin/event combination.</summary>
        private NotePreset? DetectPreset(NoteProps props)
        {
            int skin = props.VisualSkin;
            int press = (int)props.OnPressEvent;
            int miss = (int)props.OnMissEvent;
            if (skin == NoteSkins.Default && press < 0 && miss < 0) return NotePreset.Normal;
            if (miss >= 0) return null;
            if (skin == NoteSkins.Negative && PackIs(press, "remove", HURT_PRESET_AMOUNT)) return NotePreset.Hurt;
            if (skin == NoteSkins.Negative && PackIs(press, "remove", INSTAKILL_PRESET_AMOUNT)) return NotePreset.Instakill;
            if (skin == NoteSkins.Positive && PackIs(press, "add", HEAL_PRESET_AMOUNT)) return NotePreset.Heal;
            return null;
        }

        /// <summary>Inspector text: the preset name, or skin + linked action packs for custom notes.</summary>
        private string DescribeNote(NoteProps props)
        {
            var preset = DetectPreset(props);
            if (preset.HasValue) return PresetName(preset.Value);
            string s = $"{NoteSkins.GetName(props.Skin)} skin";
            if (props.OnPressEvent >= 0) s += $" | press AP{(int)props.OnPressEvent}";
            if (props.OnMissEvent >= 0) s += $" | miss AP{(int)props.OnMissEvent}";
            return s;
        }

        private void ChangeBpm(float delta)
        {
            if (_level == null) return;
            float oldBpm = _level.Data.EffectiveBpm;
            float newBpm = Math.Clamp(MathF.Round((oldBpm + delta) * 10f) / 10f, 30f, 400f);
            if (MathF.Abs(newBpm - oldBpm) < 0.01f) { UI.UiSfx.Play(UI.UiSound.SliderEnd); return; }
            UI.UiSfx.Play((delta < 0 ? UI.UiSound.SliderDown : UI.UiSound.SliderUp));
            _undoManager.Do(new DelegateEditorAction(
                () => { _level.Data.Bpm = newBpm; MarkPreviewDirty(); },
                () => { _level.Data.Bpm = oldBpm; MarkPreviewDirty(); }
            ));
        }

        private void ChangeZoom(float delta)
        {
            float newZoom = Math.Clamp(_zoom + delta, 0.50f, 2.50f);
            UI.UiSfx.Play((newZoom == _zoom ? UI.UiSound.SliderEnd : delta < 0 ? UI.UiSound.SliderDown : UI.UiSound.SliderUp));
            _zoom = newZoom;
            ShowToast($"Grid Zoom: {_zoom:F2}x");
        }

        private void ChangeChartSpeed(float delta)
        {
            if (_level == null) return;
            float oldSpd = _level.Data.Speed;
            float newSpd = Math.Clamp(MathF.Round((oldSpd + delta) * 20f) / 20f, 0.2f, 8.0f);
            if (MathF.Abs(newSpd - oldSpd) < 0.005f) { UI.UiSfx.Play(UI.UiSound.SliderEnd); return; }
            UI.UiSfx.Play((delta < 0 ? UI.UiSound.SliderDown : UI.UiSound.SliderUp));
            _undoManager.Do(new DelegateEditorAction(
                () => _level.Data.Speed = newSpd,
                () => _level.Data.Speed = oldSpd
            ));
            ShowToast($"Note Scroll Speed: {newSpd:F2}x");
        }

        private void Undo()
        {
            if (!_undoManager.CanUndo) { UI.UiSfx.Play(UI.UiSound.Error); return; }
            UI.UiSfx.Play(UI.UiSound.Undo);
            _undoManager.Undo();
            _selectedNote = null;
            MarkPreviewDirty();
            ShowToast("Undo");
        }

        private void Redo()
        {
            if (!_undoManager.CanRedo) { UI.UiSfx.Play(UI.UiSound.Error); return; }
            UI.UiSfx.Play(UI.UiSound.Undo);
            _undoManager.Redo();
            MarkPreviewDirty();
            ShowToast("Redo");
        }

        private float GetTotalDurationSeconds()
        {
            float maxSec = 30f;
            if (_audioManager.HasAudioTrack && _audioManager.Duration > 0f)
                maxSec = Math.Max(maxSec, _audioManager.Duration / 1000f);
            return Math.Max(maxSec, TimeUnitsToSeconds(GetMaxTimeUnits()));
        }

        private void TogglePlayPause()
        {
            _isPlaying = !_isPlaying;
            UI.UiSfx.Play(UI.UiSound.Toggle);
            if (_isPlaying)
            {
                float startSec = TimeUnitsToSeconds(_scrollY);
                _playbackTime = startSec;
                _prevPlaybackTime = startSec;
                if (_audioManager.HasAudioTrack)
                {
                    _audioManager.Seek(startSec * 1000f);
                    _audioManager.Play();
                }
                SyncPreviewToTime(startSec);
            }
            else
            {
                PausePlayback();
            }
        }

        private void PausePlayback()
        {
            _isPlaying = false;
            _audioManager.Pause();
            _previewNeedsSeek = true;
        }

        private void ScrollTo(float targetUnits)
        {
            _scrollY = Math.Clamp(targetUnits, 0f, GetMaxTimeUnits());
            _playbackTime = TimeUnitsToSeconds(_scrollY);
            _prevPlaybackTime = _playbackTime;
            if (!_isPlaying && _audioManager.HasAudioTrack)
                _audioManager.Seek(_playbackTime * 1000f);
            MarkPreviewDirty();
        }

        private void SeekFromScrubberX(int mouseX)
        {
            if (_scrubberRect.Width <= 0) return;
            float norm = Math.Clamp((mouseX - _scrubberRect.X) / (float)_scrubberRect.Width, 0f, 1f);
            float durSec = GetTotalDurationSeconds();
            float targetSec = norm * durSec;
            _playbackTime = targetSec;
            _prevPlaybackTime = targetSec;
            _scrollY = SecondsToTimeUnits(targetSec);
            if (_audioManager.HasAudioTrack)
                _audioManager.Seek(targetSec * 1000f);
            MarkPreviewDirty();
        }

        private void TriggerPlaytest(bool fromStart)
        {
            if (_level == null) return;
            PausePlayback();
            PlaytestStartSeconds = fromStart ? 0f : Math.Max(0f, TimeUnitsToSeconds(_scrollY));
            PlaytestRequested = true;
            UI.UiSfx.Play(UI.UiSound.StartSong);
        }

        private void SaveLevel()
        {
            if (_level == null) return;
            try
            {
                string savePath = _levelPath;
                if (Directory.Exists(savePath))
                {
                    savePath = savePath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + ".fnmm";
                }
                else if (!savePath.EndsWith(".fnmm", StringComparison.OrdinalIgnoreCase) &&
                         !savePath.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
                {
                    savePath = Path.ChangeExtension(savePath, ".fnmm");
                }

                if (File.Exists(savePath))
                    Common.BackupManager.CreateBackupIfNeeded(savePath);

                FnmmSaver.Save(_level, savePath);
                _levelPath = savePath;
                _undoManager.MarkAsSaved();
                _manualModified = false;
                SavedInSession = true;
                UI.UiSfx.Play(UI.UiSound.Save);
                ShowToast($"Saved {Path.GetFileName(savePath)}");
            }
            catch (Exception ex)
            {
                UI.UiSfx.Play(UI.UiSound.Error);
                ShowToast($"Save failed: {ex.Message}");
                Console.WriteLine($"[ChartEditor] Save error: {ex.Message}");
            }
        }

        private void ShowToast(string msg)
        {
            _toastMessage = msg;
            _toastTimer = 2.4f;
        }

        private void CheckNotesAtReceptor(float prevSec, float currSec)
        {
            if (_level == null || currSec < prevSec - 0.001f) return;

            int slCount = Math.Min(_level.Data.Strumlines.Count, MAX_STRUMLINES);
            bool anyHeadCrossed = false;
            bool startedAtZero = MathF.Abs(prevSec) < 0.0005f;

            for (int sl = 0; sl < slCount; sl++)
            {
                var strum = _level.Data.Strumlines[sl];
                bool strumCrossed = false;

                for (int lane = 0; lane < Math.Min(LANE_COUNT, strum.Count); lane++)
                {
                    foreach (var note in strum[lane])
                    {
                        float startSec = TimeUnitsToSeconds(note.Start);
                        float durSec = Math.Max(0f, TimeUnitsToSeconds(note.Duration));
                        float endSec = startSec + durSec;

                        bool crossedHead = (currSec > prevSec) &&
                            ((startSec > prevSec && startSec <= currSec) || (startedAtZero && startSec >= 0f && startSec <= currSec));
                        bool inHold = durSec > 0.001f && currSec >= startSec && currSec <= endSec;

                        if (crossedHead)
                        {
                            _characterSingTime[sl] = SING_ANIM_DURATION;
                            _characterSingLane[sl] = lane;
                            _receptorPressTime[sl, lane] = RECEPTOR_ANIM_DURATION;
                            strumCrossed = true;
                            anyHeadCrossed = true;
                            if (_previewInitialized && _previewLevel != null)
                                FnmmSceneRenderer.TriggerCharacterSing(_previewLevel, _previewAnimators, sl, lane, currSec, endSec);
                        }
                        else if (inHold)
                        {
                            _characterSingTime[sl] = Math.Max(_characterSingTime[sl], 0.10f);
                            _characterSingLane[sl] = lane;
                            _receptorPressTime[sl, lane] = Math.Max(_receptorPressTime[sl, lane], 0.08f);
                            if (_previewInitialized && _previewLevel != null)
                                ExtendPreviewHoldSing(sl, lane, currSec, endSec);
                        }
                    }
                }

                if (strumCrossed)
                    _previewEvents?.NotifyNoteHit(sl);
            }

            if (anyHeadCrossed)
                PlayHitsound();
        }

        private void ExtendPreviewHoldSing(int sl, int lane, float songTime, float holdEnd)
        {
            if (_previewLevel == null) return;
            var objs = _previewLevel.Scene.Objects;
            int targetAnim = lane + 1;
            for (int j = 0; j < objs.Count && j < _previewAnimators.Length; j++)
            {
                if ((int)objs[j].StrumLine != sl) continue;
                var anim = _previewAnimators[j];
                if (anim == null || anim.IsSetByEvent) continue;
                if (anim.CurrentAnimIndex == targetAnim)
                    anim.HoldUntil = Math.Max(anim.HoldUntil, holdEnd);
                else
                    FnmmSceneRenderer.TriggerCharacterSing(_previewLevel, _previewAnimators, sl, lane, songTime, holdEnd);
            }
        }

        private void MarkPreviewDirty() => _previewNeedsSeek = true;

        private void ResetPreviewAnimatorsToIdle()
        {
            if (_previewLevel == null) return;
            var objs = _previewLevel.Scene.Objects;
            for (int i = 0; i < _previewAnimators.Length && i < objs.Count; i++)
            {
                var anim = _previewAnimators[i];
                if (anim == null) continue;
                anim.HoldUntil = -1f;
                anim.SetAnimation(SpriteAnimator.IdleAnim, setByEvent: false);
            }
        }

        private void SyncPreviewToTime(float targetSec)
        {
            if (!_previewInitialized || _previewLevel == null || _previewEvents == null || _level == null)
                return;

            float clamped = Math.Max(0f, targetSec);
            _previewLevel.Data.Bpm = _level.Data.Bpm;
            float bpm = _level.Data.EffectiveBpm;

            ResetPreviewAnimatorsToIdle();
            _previewCameraManager?.Reset();
            _previewEvents.SeekTo(clamped);
            _previewEvents.Update(clamped, 5.0f);

            // Restore note-driven poses near targetSec so scrubbed stops show characters singing!
            int slCount = Math.Min(_level.Data.Strumlines.Count, MAX_STRUMLINES);
            for (int sl = 0; sl < slCount; sl++)
            {
                var strum = _level.Data.Strumlines[sl];
                int bestLane = -1;
                float bestStart = -1f;
                float bestEnd = -1f;

                for (int lane = 0; lane < Math.Min(LANE_COUNT, strum.Count); lane++)
                {
                    foreach (var n in strum[lane])
                    {
                        float ns = TimeUnitsToSeconds(n.Start);
                        float ne = ns + Math.Max(0f, TimeUnitsToSeconds(n.Duration));
                        bool activeHold = ne > ns + 0.01f && clamped >= ns && clamped <= ne;
                        bool recentTap = clamped >= ns && clamped - ns <= 0.22f;
                        if ((activeHold || recentTap) && ns >= bestStart)
                        {
                            bestStart = ns;
                            bestEnd = Math.Max(ne, clamped + SING_ANIM_DURATION);
                            bestLane = lane;
                        }
                    }
                }

                if (bestLane >= 0)
                {
                    FnmmSceneRenderer.TriggerCharacterSing(_previewLevel, _previewAnimators, sl, bestLane, clamped, bestEnd);
                    _characterSingTime[sl] = SING_ANIM_DURATION;
                    _characterSingLane[sl] = bestLane;
                }
            }

            foreach (var anim in _previewAnimators)
            {
                if (anim == null) continue;
                anim.Bpm = bpm;
                anim.Update(0.016f, clamped);
            }

            _previewLastSeekTime = clamped;
            _previewNeedsSeek = false;
        }

        private void UpdatePreviewDuringPlay(float songTime, float currentBeat, float dt)
        {
            if (!_previewInitialized || _previewLevel == null || _previewEvents == null || _level == null)
                return;

            float bpm = _level.Data.EffectiveBpm;
            _previewLevel.Data.Bpm = _level.Data.Bpm;
            _previewEvents.Update(songTime, dt);
            _previewCameraManager?.UpdateBeatZoom((int)MathF.Floor(currentBeat), dt);

            foreach (var anim in _previewAnimators)
            {
                if (anim == null) continue;
                anim.Bpm = bpm;
                anim.Update(dt, songTime);
            }

            _previewLastSeekTime = songTime;
            _previewNeedsSeek = false;
        }

        private void UpdatePreviewPaused(float dt)
        {
            if (!_previewInitialized || _previewLevel == null || _previewEvents == null || _level == null)
                return;

            float curSec = TimeUnitsToSeconds(_scrollY);
            if (_previewNeedsSeek || MathF.Abs(curSec - _previewLastSeekTime) > 0.004f)
            {
                SyncPreviewToTime(curSec);
                return;
            }

            // Keep idle loops breathing gently at paused timestamp (notes holding keeps sing pose via HoldUntil >= _previewLastSeekTime)
            float bpm = _level.Data.EffectiveBpm;
            foreach (var anim in _previewAnimators)
            {
                if (anim == null) continue;
                anim.Bpm = bpm;
                anim.Update(dt, _previewLastSeekTime);
            }
        }

        // ── Layout ───────────────────────────────────────────────────────────
        private void UpdateLayout(int sw, int sh)
        {
            float s = UiScale.FromScreen(sw, sh);
            _uiScale = s;

            int timelineH = UiScale.Px(74, s, 56);
            int statusH = UiScale.Px(28, s, 22);
            _gridHeaderH = UiScale.Px(46, s, 34);
            int toolbarW = Math.Clamp((int)(sw * 0.125f), UiScale.Px(140, s, 110), UiScale.Px(240, s));
            int infoW = Math.Clamp((int)(sw * 0.245f), UiScale.Px(290, s, 240), UiScale.Px(440, s));

            _timelineRect = new Rectangle(0, 0, sw, timelineH);
            _toolbarRect = new Rectangle(0, timelineH, toolbarW, sh - timelineH - statusH);
            _infoPanelRect = new Rectangle(sw - infoW, timelineH, infoW, sh - timelineH - statusH);
            _gridRect = new Rectangle(toolbarW, timelineH, Math.Max(UiScale.Px(220, s), sw - toolbarW - infoW), sh - timelineH - statusH);
            _gridBodyRect = new Rectangle(_gridRect.X, _gridRect.Y + _gridHeaderH, _gridRect.Width, Math.Max(80, _gridRect.Height - _gridHeaderH));
            _statusBarRect = new Rectangle(0, sh - statusH, sw, statusH);

            _gridGroupGap = UiScale.Px(18, s, 10);
            float leftGutter = UiScale.Px(52, s, 32);
            float rightPad = UiScale.Px(14, s, 8);
            float availGridW = Math.Max(200f, _gridBodyRect.Width - leftGutter - rightPad - _gridGroupGap);
            _laneWidth = Math.Clamp(availGridW / GRID_COLS, 40f * s, 72f * s);

            // Top bar layout
            int pad = UiScale.Px(10, s, 6);
            int topY = UiScale.Px(10, s, 6);
            int backH = UiScale.Px(26, s, 22);
            _backBtnRect = new Rectangle(pad, topY, UiScale.Px(76, s, 62), backH);

            // Right-side action pills (row 2 of header)
            int rightX = sw - pad;
            int btnGap = UiScale.Px(6, s, 4);
            int btnH = UiScale.Px(28, s, 22);
            int btnY = UiScale.Px(40, s, 30);
            _saveBtnRect = AllocRightButton(ref rightX, btnY, UiScale.Px(70, s), btnH, btnGap);
            _charsBtnRect = AllocRightButton(ref rightX, btnY, UiScale.Px(82, s), btnH, btnGap);
            _previewToggleRect = AllocRightButton(ref rightX, btnY, UiScale.Px(90, s), btnH, btnGap);
            _redoBtnRect = AllocRightButton(ref rightX, btnY, UiScale.Px(58, s), btnH, btnGap);
            _undoBtnRect = AllocRightButton(ref rightX, btnY, UiScale.Px(60, s), btnH, btnGap);
            _playtestBtnRect = AllocRightButton(ref rightX, btnY, UiScale.Px(98, s), btnH, btnGap);
            _playBtnRect = AllocRightButton(ref rightX, btnY, UiScale.Px(84, s), btnH, btnGap);

            int scrubX = UiScale.Px(12, s, 8);
            int scrubH = UiScale.Px(20, s, 16);
            int scrubW = Math.Max(UiScale.Px(140, s), rightX - scrubX - UiScale.Px(14, s));
            _scrubberRect = new Rectangle(scrubX, btnY + (btnH - scrubH) / 2, scrubW, scrubH);

            // Docked preview inside bottom of _infoPanelRect (zero overlap with chart!)
            if (_showPreview)
            {
                int prevMargin = UiScale.Px(8, s, 6);
                int prevH = Math.Clamp((int)(_infoPanelRect.Height * 0.34f), UiScale.Px(150, s), UiScale.Px(250, s));
                _previewRect = new Rectangle(_infoPanelRect.X + prevMargin, _infoPanelRect.Bottom - prevH - prevMargin, _infoPanelRect.Width - prevMargin * 2, prevH);
                int hdrBtnH = UiScale.Px(20, s, 16);
                int hdrBtnY = _previewRect.Y + UiScale.Px(3, s);
                int hdrGap = UiScale.Px(4, s, 2);
                int cursorX = _previewRect.Right - UiScale.Px(4, s);
                _previewCloseBtn = AllocRightButton(ref cursorX, hdrBtnY, UiScale.Px(22, s, 18), hdrBtnH, hdrGap);
                _previewFxBtnRect = AllocRightButton(ref cursorX, hdrBtnY, UiScale.Px(32, s, 26), hdrBtnH, hdrGap);
                _previewCamBtnRect = AllocRightButton(ref cursorX, hdrBtnY, UiScale.Px(54, s, 44), hdrBtnH, hdrGap);
            }
            else
            {
                _previewRect = Rectangle.Empty;
                _previewCamBtnRect = Rectangle.Empty;
                _previewFxBtnRect = Rectangle.Empty;
                _previewCloseBtn = Rectangle.Empty;
            }
        }

        private static Rectangle AllocRightButton(ref int cursorX, int y, int width, int height, int gap)
        {
            cursorX -= width;
            var r = new Rectangle(cursorX, y, width, height);
            cursorX -= gap;
            return r;
        }

        // ── Draw Entry ───────────────────────────────────────────────────────
        public void Draw(SpriteBatch sb, int screenWidth, int screenHeight)
        {
            _screenWidth = screenWidth;
            _screenHeight = screenHeight;
            UpdateLayout(screenWidth, screenHeight);

            switch (_phase)
            {
                case EditorPhase.Loading:
                    DrawLoading(sb, screenWidth, screenHeight);
                    break;
                case EditorPhase.Error:
                    DrawError(sb, screenWidth, screenHeight);
                    break;
                case EditorPhase.Ready:
                    EnsurePreviewLoaded(sb.GraphicsDevice);
                    if (_showPreview)
                        RenderPreviewToTarget(sb);
                    DrawEditor(sb, screenWidth, screenHeight);
                    break;
            }
        }

        private void DrawLoading(SpriteBatch sb, int sw, int sh)
        {
            sb.Draw(_pixel, new Rectangle(0, 0, sw, sh), BG_DARK);
            float s = _uiScale;
            int cx = sw / 2;
            int cy = sh / 2 - UiScale.Px(20, s);
            float rad = 28f * s;
            int dot = UiScale.Px(8, s, 5);

            for (int i = 0; i < 10; i++)
            {
                float angle = _loadingSpinnerRotation + (i * MathF.PI / 5f);
                float alpha = 1f - (i / 10f) * 0.75f;
                int x = (int)(cx + MathF.Cos(angle) * rad);
                int y = (int)(cy + MathF.Sin(angle) * rad);
                sb.Draw(_pixel, new Rectangle(x - dot / 2, y - dot / 2, dot, dot), ACCENT_PINK * alpha);
            }

            DrawText(sb, "Loading Chart Editor...", new Vector2(sw / 2f, cy + UiScale.Px(54, s)), Color.White, UiScale.FontPx(22, s), centered: true);
            DrawText(sb, Path.GetFileName(_levelPath), new Vector2(sw / 2f, cy + UiScale.Px(84, s)), Color.White * 0.55f, UiScale.FontPx(15, s), centered: true);
        }

        private void DrawError(SpriteBatch sb, int sw, int sh)
        {
            sb.Draw(_pixel, new Rectangle(0, 0, sw, sh), BG_DARK);
            _clickActions.Clear();
            float s = _uiScale;
            DrawButton(sb, _backBtnRect, "< ESC", BG_CARD, Color.White, UiScale.FontPx(13, s), ACCENT_PINK, () => BackPressed = true);
            DrawText(sb, "CHART LOAD ERROR", new Vector2(sw / 2f, sh / 2f - UiScale.Px(40, s)), new Color(255, 95, 95), UiScale.FontPx(28, s), centered: true);
            DrawText(sb, _errorMessage ?? "Unknown error", new Vector2(sw / 2f, sh / 2f + UiScale.Px(4, s)), Color.White * 0.85f, UiScale.FontPx(16, s), centered: true);
            DrawText(sb, "Press ESC or click Back to return", new Vector2(sw / 2f, sh / 2f + UiScale.Px(42, s)), Color.Gray, UiScale.FontPx(15, s), centered: true);
        }

        private void DrawEditor(SpriteBatch sb, int sw, int sh)
        {
            if (_level == null) return;
            _clickActions.Clear();

            sb.Draw(_pixel, new Rectangle(0, 0, sw, sh), BG_DARK);

            DrawGrid(sb);
            DrawTimeline(sb);
            DrawToolbar(sb);
            DrawInfoPanel(sb);
            DrawStatusBar(sb);

            if (_showPreview)
                DrawCharacterPreview(sb);

            if (_charModalOpen)
                DrawCharacterModal(sb, sw, sh);
        }

        // ── Timeline Header ──────────────────────────────────────────────────
        private void DrawTimeline(SpriteBatch sb)
        {
            float s = _uiScale;
            sb.Draw(_pixel, _timelineRect, BG_PANEL);
            sb.Draw(_pixel, new Rectangle(_timelineRect.X, _timelineRect.Bottom - 2, _timelineRect.Width, 2), ACCENT_PINK * 0.7f);

            int btnFont = UiScale.FontPx(13, s);
            int smallBtnFont = UiScale.FontPx(12, s);
            DrawButton(sb, _backBtnRect, "< ESC", BG_CARD, Color.White, btnFont, Color.White * 0.2f, () =>
            {
                PausePlayback();
                BackPressed = true;
            });

            string songTitle = string.IsNullOrWhiteSpace(_level?.LevelName) ? Path.GetFileNameWithoutExtension(_levelPath) : _level!.LevelName;
            string modStar = IsModified ? " *" : "";
            string unitName = (_level?.Data.TimesAreSeconds ?? false) ? "SEC" : "BEATS";
            string metaLine = $"{songTitle}{modStar}   |   BPM {_level?.Data.EffectiveBpm:F1}   |   SPD {_level?.Data.Speed ?? 1f:F2}x   |   {unitName}";
            int titleFont = UiScale.FontPx(16, s);
            float titleH = MeasureText(metaLine, titleFont).Y;
            DrawText(sb, metaLine, new Vector2(_backBtnRect.Right + UiScale.Px(12, s), _backBtnRect.Y + (_backBtnRect.Height - titleH) / 2f), ACCENT_GOLD, titleFont);

            // Interactive Scrubber Bar
            float durSec = GetTotalDurationSeconds();
            float curSec = Math.Clamp(TimeUnitsToSeconds(_scrollY), 0f, durSec);
            float frac = Math.Clamp(curSec / durSec, 0f, 1f);

            sb.Draw(_pixel, _scrubberRect, new Color(14, 14, 22));
            DrawBorder(sb, _scrubberRect, Color.White * 0.16f);

            int fillW = (int)(_scrubberRect.Width * frac);
            if (fillW > 0)
                sb.Draw(_pixel, new Rectangle(_scrubberRect.X, _scrubberRect.Y, fillW, _scrubberRect.Height), ACCENT_PINK * 0.45f);

            // Mini note density ticks & event markers on scrubber
            if (_level != null)
            {
                int tickW = Math.Max(2, UiScale.Px(2, s));
                int tickH = Math.Max(3, UiScale.Px(4, s));
                foreach (var sl in _level.Data.Strumlines)
                {
                    foreach (var lane in sl)
                    {
                        foreach (var n in lane)
                        {
                            float nf = Math.Clamp(TimeUnitsToSeconds(n.Start) / durSec, 0f, 1f);
                            int tx = _scrubberRect.X + (int)(nf * Math.Max(1, _scrubberRect.Width - tickW));
                            sb.Draw(_pixel, new Rectangle(tx, _scrubberRect.Bottom - tickH - 1, tickW, tickH), ACCENT_CYAN * 0.55f);
                        }
                    }
                }

                bool evAlreadySec = _previewLevel != null;
                var evList = _previewLevel?.Events.Events ?? _level.Events.Events;
                foreach (var ev in evList)
                {
                    float evSec = GetEventSeconds(ev, evAlreadySec);
                    float ef = Math.Clamp(evSec / durSec, 0f, 1f);
                    int tx = _scrubberRect.X + (int)(ef * Math.Max(1, _scrubberRect.Width - tickW));
                    sb.Draw(_pixel, new Rectangle(tx, _scrubberRect.Y + 1, tickW, tickH), ACCENT_GOLD * 0.85f);
                }
            }

            int headW = Math.Max(4, UiScale.Px(4, s));
            int headX = _scrubberRect.X + Math.Clamp(fillW - headW / 2, 0, Math.Max(0, _scrubberRect.Width - headW));
            sb.Draw(_pixel, new Rectangle(headX, _scrubberRect.Y - 2, headW, _scrubberRect.Height + 4), Color.White);

            string timeLabel = $"{FormatTime(curSec)} / {FormatTime(durSec)}" + (_audioManager.HasAudioTrack ? "" : " (No Audio)");
            int timeFont = UiScale.FontPx(12, s);
            var timeSz = MeasureText(timeLabel, timeFont);
            int lblPadX = UiScale.Px(6, s);
            int lblPadY = Math.Max(1, UiScale.Px(2, s));
            float lblX = _scrubberRect.X + UiScale.Px(8, s);
            float lblY = _scrubberRect.Y + (_scrubberRect.Height - timeSz.Y) / 2f;
            sb.Draw(_pixel, new Rectangle((int)lblX - lblPadX / 2, (int)lblY - lblPadY / 2, (int)timeSz.X + lblPadX, (int)timeSz.Y + lblPadY), new Color(12, 12, 20) * 0.80f);
            DrawText(sb, timeLabel, new Vector2(lblX, lblY), Color.White * 0.95f, timeFont);

            // Action Buttons
            DrawButton(sb, _playBtnRect, _isPlaying ? "PAUSE" : "PLAY", _isPlaying ? new Color(180, 120, 30) : new Color(36, 90, 62), Color.White, btnFont, _isPlaying ? ACCENT_GOLD : ACCENT_GREEN, TogglePlayPause);
            DrawButton(sb, _playtestBtnRect, "PLAYTEST", new Color(22, 82, 108), Color.White, btnFont, ACCENT_CYAN, () => TriggerPlaytest(false));
            DrawButton(sb, _undoBtnRect, "UNDO", _undoManager.CanUndo ? BG_CARD : new Color(24, 24, 34), _undoManager.CanUndo ? Color.White : Color.Gray, smallBtnFont, Color.White * 0.2f, Undo);
            DrawButton(sb, _redoBtnRect, "REDO", _undoManager.CanRedo ? BG_CARD : new Color(24, 24, 34), _undoManager.CanRedo ? Color.White : Color.Gray, smallBtnFont, Color.White * 0.2f, Redo);
            DrawButton(sb, _previewToggleRect, _showPreview ? "PREV ON" : "PREV OFF", _showPreview ? new Color(46, 54, 88) : BG_CARD, _showPreview ? ACCENT_GOLD : Color.Gray, smallBtnFont, _showPreview ? ACCENT_GOLD * 0.6f : Color.White * 0.15f, () => { _showPreview = !_showPreview; UI.UiSfx.Play(UI.UiSound.Toggle); });
            DrawButton(sb, _charsBtnRect, "CHARS (B)", _charModalOpen ? new Color(78, 46, 112) : new Color(42, 30, 64), new Color(215, 175, 255), smallBtnFont, new Color(178, 102, 255), ToggleCharacterModal);
            DrawButton(sb, _saveBtnRect, IsModified ? "SAVE*" : "SAVE", IsModified ? new Color(150, 50, 88) : BG_CARD, Color.White, btnFont, IsModified ? ACCENT_PINK : Color.White * 0.2f, SaveLevel);
        }

        // ── Left Toolbar (Aligned, Labelled & Clickable!) ────────────────────
        private void DrawToolbar(SpriteBatch sb)
        {
            float s = _uiScale;
            sb.Draw(_pixel, _toolbarRect, BG_PANEL);
            sb.Draw(_pixel, new Rectangle(_toolbarRect.Right - 2, _toolbarRect.Y, 2, _toolbarRect.Height), ACCENT_PINK * 0.45f);

            int padX = UiScale.Px(8, s);
            int w = _toolbarRect.Width - padX * 2;
            int y = _toolbarRect.Y + UiScale.Px(10, s);

            int hdrFont = UiScale.FontPx(12, s);
            int labelFont = UiScale.FontPx(13, s);
            int smallFont = UiScale.FontPx(11, s);
            int hdrStep = UiScale.Px(18, s);

            DrawText(sb, "TOOLS", new Vector2(_toolbarRect.X + padX, y), ACCENT_GOLD, hdrFont);
            y += hdrStep;

            (EditorTool tool, string num, string name, Color accent)[] tools =
            {
                (EditorTool.Select,    "1", "SELECT", ACCENT_CYAN),
                (EditorTool.PlaceNote, "2", "NOTE",   ACCENT_GREEN),
                (EditorTool.PlaceHold, "3", "HOLD",   ACCENT_GOLD),
                (EditorTool.Delete,    "4", "DELETE", ACCENT_PINK),
            };

            int toolH = UiScale.Px(42, s);
            int toolStep = UiScale.Px(47, s);
            int numW = UiScale.Px(22, s);
            int numH = UiScale.Px(24, s);

            for (int i = 0; i < tools.Length; i++)
            {
                var t = tools[i];
                var r = new Rectangle(_toolbarRect.X + padX, y, w, toolH);
                _toolBtnRects[i] = r;
                bool active = _currentTool == t.tool;
                bool hovered = r.Contains(_prevMouse.Position);

                Color fill = active ? t.accent * 0.24f : (hovered ? BG_CARD_HOVER : BG_CARD);
                sb.Draw(_pixel, r, fill);
                DrawBorder(sb, r, active ? t.accent : Color.White * 0.1f);
                if (active)
                    sb.Draw(_pixel, new Rectangle(r.X, r.Y, Math.Max(3, UiScale.Px(4, s)), r.Height), t.accent);

                var numBox = new Rectangle(r.X + UiScale.Px(8, s), r.Y + (r.Height - numH) / 2, numW, numH);
                sb.Draw(_pixel, numBox, active ? t.accent : new Color(24, 24, 34));
                DrawTextCenteredInBox(sb, t.num, numBox, active ? BG_DARK : Color.White, labelFont);

                var labelX = numBox.Right + UiScale.Px(8, s);
                DrawText(sb, t.name, new Vector2(labelX, r.Y + (r.Height - MeasureText(t.name, labelFont).Y) / 2f), active ? Color.White : new Color(205, 205, 225), labelFont);

                var capturedTool = t.tool;
                _clickActions.Add((r, () => SetTool(capturedTool), null));
                y += toolStep;
            }

            // Target Side Focus (Left=FOE / SLn, Right=BF)
            y += UiScale.Px(6, s);
            DrawText(sb, "SIDE FOCUS", new Vector2(_toolbarRect.X + padX, y), ACCENT_GOLD, hdrFont);
            y += hdrStep;
            int halfGap = UiScale.Px(4, s);
            int halfW = (w - halfGap) / 2;
            int ctrlH = UiScale.Px(28, s);
            _strumEnemyBtn = new Rectangle(_toolbarRect.X + padX, y, halfW, ctrlH);
            _strumPlayerBtn = new Rectangle(_toolbarRect.X + padX + halfW + halfGap, y, halfW, ctrlH);
            string leftFocusLabel = (_level != null && _level.Data.Strumlines.Count > 2) ? $"SL{_leftStrumline} (W)" : "FOE (W)";
            bool leftFocused = _selectedStrumline == _leftStrumline;
            bool rightFocused = _selectedStrumline == _rightStrumline;
            DrawButton(sb, _strumEnemyBtn, leftFocusLabel, leftFocused ? new Color(115, 38, 66) : BG_CARD, Color.White, smallFont, leftFocused ? ACCENT_PINK : Color.White * 0.12f, () => SwitchStrumline(_leftStrumline));
            DrawButton(sb, _strumPlayerBtn, "BF (Q)", rightFocused ? new Color(30, 88, 125) : BG_CARD, Color.White, smallFont, rightFocused ? ACCENT_CYAN : Color.White * 0.12f, () => SwitchStrumline(_rightStrumline));
            y += ctrlH + UiScale.Px(10, s);

            // Snap divisor
            DrawText(sb, "GRID SNAP", new Vector2(_toolbarRect.X + padX, y), ACCENT_GOLD, hdrFont);
            y += hdrStep;
            int stepBtnW = UiScale.Px(26, s);
            int stepGap = UiScale.Px(4, s);
            _snapPrevBtn = new Rectangle(_toolbarRect.X + padX, y, stepBtnW, ctrlH);
            var snapLblRect = new Rectangle(_snapPrevBtn.Right + stepGap, y, Math.Max(20, w - (stepBtnW + stepGap) * 2), ctrlH);
            _snapNextBtn = new Rectangle(snapLblRect.Right + stepGap, y, stepBtnW, ctrlH);
            DrawButton(sb, _snapPrevBtn, "<", BG_CARD, Color.White, labelFont, Color.White * 0.14f, () => CycleSnap(-1));
            sb.Draw(_pixel, snapLblRect, new Color(16, 16, 24));
            DrawTextCenteredInBox(sb, $"1/{_snapDivisor}", snapLblRect, Color.White, labelFont);
            DrawButton(sb, _snapNextBtn, ">", BG_CARD, Color.White, labelFont, Color.White * 0.14f, () => CycleSnap(1));
            y += ctrlH + UiScale.Px(10, s);

            // Default hold length
            DrawText(sb, "HOLD BEATS", new Vector2(_toolbarRect.X + padX, y), ACCENT_GOLD, hdrFont);
            y += hdrStep;
            _holdMinusBtn = new Rectangle(_toolbarRect.X + padX, y, stepBtnW, ctrlH);
            var holdLblRect = new Rectangle(_holdMinusBtn.Right + stepGap, y, Math.Max(20, w - (stepBtnW + stepGap) * 2), ctrlH);
            _holdPlusBtn = new Rectangle(holdLblRect.Right + stepGap, y, stepBtnW, ctrlH);
            DrawButton(sb, _holdMinusBtn, "-", BG_CARD, Color.White, labelFont, Color.White * 0.14f, () => AdjustHoldDuration(-1));
            sb.Draw(_pixel, holdLblRect, new Color(16, 16, 24));
            DrawTextCenteredInBox(sb, $"{_defaultHoldBeats:F2}", holdLblRect, ACCENT_GOLD, smallFont);
            DrawButton(sb, _holdPlusBtn, "+", BG_CARD, Color.White, labelFont, Color.White * 0.14f, () => AdjustHoldDuration(1));
            y += ctrlH + UiScale.Px(10, s);

            // Note preset brush (skin is visual; the preset also links the effect action pack).
            // Colors match NoteStyle tints: negative = red X, positive = cyan O.
            int skinH = UiScale.Px(26, s);
            int skinStepY = UiScale.Px(30, s);
            if (y + hdrStep + skinStepY * 2 <= _toolbarRect.Bottom)
            {
                DrawText(sb, "NOTE PRESET", new Vector2(_toolbarRect.X + padX, y), ACCENT_GOLD, hdrFont);
                y += hdrStep;
                string[] presetLabels = { "NORMAL", "HURT X", "KILL X", "HEAL O" };
                Color[] presetCols = { ACCENT_CYAN, new(255, 140, 60), new(255, 50, 50), new(0, 200, 255) };
                for (int i = 0; i < 4; i++)
                {
                    int row = i / 2;
                    int col = i % 2;
                    var sr = new Rectangle(_toolbarRect.X + padX + col * (halfW + halfGap), y + row * skinStepY, halfW, skinH);
                    _skinBtnRects[i] = sr;
                    var preset = (NotePreset)i;
                    bool sel = _activePreset == preset;
                    DrawButton(sb, sr, presetLabels[i], sel ? presetCols[i] * 0.28f : BG_CARD, sel ? Color.White : Color.Gray, smallFont, sel ? presetCols[i] : Color.White * 0.1f, () => { if (_activePreset != preset) UI.UiSfx.Play(UI.UiSound.Toggle); _activePreset = preset; });
                }
                y += skinStepY * 2 + UiScale.Px(8, s);
            }

            // Audio cues (Hitsound + Metronome)
            if (y + hdrStep + ctrlH <= _toolbarRect.Bottom)
            {
                DrawText(sb, "AUDIO CUES", new Vector2(_toolbarRect.X + padX, y), ACCENT_GOLD, hdrFont);
                y += hdrStep;
                _hitsoundBtnRect = new Rectangle(_toolbarRect.X + padX, y, halfW, ctrlH);
                _metronomeBtnRect = new Rectangle(_toolbarRect.X + padX + halfW + halfGap, y, halfW, ctrlH);
                DrawButton(sb, _hitsoundBtnRect, "SND (H)", _hitsoundsEnabled ? new Color(24, 88, 62) : BG_CARD,
                    _hitsoundsEnabled ? Color.White : Color.Gray, smallFont, _hitsoundsEnabled ? ACCENT_GREEN : Color.White * 0.12f,
                    () => { _hitsoundsEnabled = !_hitsoundsEnabled; UI.UiSfx.Play(UI.UiSound.Toggle); ShowToast(_hitsoundsEnabled ? "Hitsounds ON" : "Hitsounds OFF"); });
                DrawButton(sb, _metronomeBtnRect, "MET (N)", _metronomeEnabled ? new Color(92, 72, 22) : BG_CARD,
                    _metronomeEnabled ? Color.White : Color.Gray, smallFont, _metronomeEnabled ? ACCENT_GOLD : Color.White * 0.12f,
                    () => { _metronomeEnabled = !_metronomeEnabled; UI.UiSfx.Play(UI.UiSound.Toggle); ShowToast(_metronomeEnabled ? "Metronome ON" : "Metronome OFF"); });
            }
            else
            {
                _hitsoundBtnRect = Rectangle.Empty;
                _metronomeBtnRect = Rectangle.Empty;
            }
        }

        // ── Grid Rendering (8-Column FOE/Extra | BF, Square 16th Cells) ───────
        private void DrawGrid(SpriteBatch sb)
        {
            float s = _uiScale;
            sb.Draw(_pixel, _gridRect, BG_DARK);
            _hoveredEventSummary = null;

            float gridStartX = GetGridStartX();
            float rightStartX = GetColumnX(LANE_COUNT);
            float blockW = _laneWidth * LANE_COUNT;

            // Fixed Two-Row Header: Section Badges (Left SL / BF) + Lane Arrows (L D U R)
            var headerRect = new Rectangle(_gridRect.X, _gridRect.Y, _gridRect.Width, _gridHeaderH);
            sb.Draw(_pixel, headerRect, new Color(24, 22, 34));
            sb.Draw(_pixel, new Rectangle(_gridRect.X, headerRect.Bottom - 2, _gridRect.Width, 2), ACCENT_PINK * 0.35f);

            int padY = Math.Max(2, UiScale.Px(3, s));
            int secH = Math.Max(14, (int)(_gridHeaderH * 0.42f) - padY);
            int laneY = headerRect.Y + secH + padY * 2;
            int laneH = Math.Max(14, headerRect.Bottom - laneY - padY - 2);
            int secFont = UiScale.FontPx(11, s);
            int laneFont = UiScale.FontPx(14, s);

            var foeHdr = new Rectangle((int)gridStartX, headerRect.Y + padY, (int)blockW, secH);
            var bfHdr = new Rectangle((int)rightStartX, headerRect.Y + padY, (int)blockW, secH);
            bool leftSel = _selectedStrumline == _leftStrumline;
            bool rightSel = _selectedStrumline == _rightStrumline;

            if (_level != null && _level.Data.Strumlines.Count > 2)
            {
                int arrowW = UiScale.Px(22, s, 16);
                _leftStrumPrevBtn = new Rectangle(foeHdr.X, foeHdr.Y, arrowW, foeHdr.Height);
                var midHdr = new Rectangle(foeHdr.X + arrowW + 2, foeHdr.Y, Math.Max(24, foeHdr.Width - (arrowW + 2) * 2), foeHdr.Height);
                _leftStrumNextBtn = new Rectangle(foeHdr.Right - arrowW, foeHdr.Y, arrowW, foeHdr.Height);
                DrawButton(sb, _leftStrumPrevBtn, "<", BG_CARD, Color.White, secFont, ACCENT_PINK * 0.5f, () => CycleLeftStrumline(-1));
                DrawButton(sb, midHdr, $"LEFT TRACK (SL{_leftStrumline})", leftSel ? new Color(92, 32, 58) : new Color(36, 26, 42),
                    leftSel ? Color.White : Color.White * 0.75f, secFont, ACCENT_PINK * (leftSel ? 0.9f : 0.35f), () => SwitchStrumline(_leftStrumline));
                DrawButton(sb, _leftStrumNextBtn, ">", BG_CARD, Color.White, secFont, ACCENT_PINK * 0.5f, () => CycleLeftStrumline(1));
            }
            else
            {
                _leftStrumPrevBtn = _leftStrumNextBtn = Rectangle.Empty;
                DrawButton(sb, foeHdr, "OPPONENT (FOE)", leftSel ? new Color(92, 32, 58) : new Color(36, 26, 42),
                    leftSel ? Color.White : Color.White * 0.7f, secFont, ACCENT_PINK * (leftSel ? 0.9f : 0.35f), () => SwitchStrumline(_leftStrumline));
            }

            DrawButton(sb, bfHdr, "PLAYER (BF)", rightSel ? new Color(24, 72, 102) : new Color(24, 34, 48),
                rightSel ? Color.White : Color.White * 0.7f, secFont, ACCENT_CYAN * (rightSel ? 0.9f : 0.35f), () => SwitchStrumline(_rightStrumline));

            for (int col = 0; col < GRID_COLS; col++)
            {
                ColToStrumAndLane(col, out int sl, out int lane);
                float colX = GetColumnX(col);
                var laneHdr = new Rectangle((int)colX + 1, laneY, Math.Max(4, (int)_laneWidth - 2), laneH);
                bool activeLane = _hoverCol == col || (_selectedStrumline == sl && _selectedLane == lane);
                sb.Draw(_pixel, laneHdr, LANE_COLORS[lane] * (activeLane ? 0.28f : 0.11f));
                DrawTextCenteredInBox(sb, LANE_ARROWS[lane], laneHdr, LANE_COLORS[lane], laneFont);
            }

            if (_level == null) return;

            // Begin scissor clipping for the scrollable chart grid body
            var gd = sb.GraphicsDevice;
            var prevScissor = gd.ScissorRectangle;
            var prevRaster = gd.RasterizerState;
            _scissorRasterizer ??= new RasterizerState { CullMode = CullMode.None, ScissorTestEnable = true };
            var scissor = Rectangle.Intersect(_gridBodyRect, new Rectangle(0, 0, _screenWidth, _screenHeight));
            if (scissor.Width <= 0 || scissor.Height <= 0) return;

            sb.End();
            gd.ScissorRectangle = scissor;
            sb.Begin(SpriteSortMode.Deferred, BlendState.AlphaBlend, SamplerState.LinearClamp, DepthStencilState.None, _scissorRasterizer);

            float stepH = GetStepHeight();
            float beatH = GetBeatHeight();
            float receptorTopY = GetReceptorY();

            // 16th-step square checkerboard rows (0.25 beats per row -> exact square at 1.0x zoom)
            const float stepBeat = 1f / STEPS_PER_BEAT;
            float visibleTopBeat = ScreenYToBeat(_gridBodyRect.Y) - 1f;
            float visibleBotBeat = ScreenYToBeat(_gridBodyRect.Bottom) + 1f;
            int minStep = Math.Max(0, (int)MathF.Floor(visibleTopBeat * STEPS_PER_BEAT));
            int measureFont = UiScale.FontPx(12, s);
            int gutterOffset = UiScale.Px(44, s);

            // Center divider background strip between Left and BF
            sb.Draw(_pixel, new Rectangle((int)(gridStartX + blockW), _gridBodyRect.Y, _gridGroupGap, _gridBodyRect.Height), new Color(13, 13, 21));

            for (int step = minStep; ; step++)
            {
                float bTop = step * stepBeat;
                if (bTop > visibleBotBeat) break;

                int yTop = BeatToScreenY(bTop);
                int yBot = BeatToScreenY(bTop + stepBeat);
                int cellH = Math.Max(1, yBot - yTop);

                for (int col = 0; col < GRID_COLS; col++)
                {
                    bool alt = (step + col) % 2 == 0;
                    Color cellCol = alt ? new Color(24, 24, 34) : new Color(30, 30, 42);
                    sb.Draw(_pixel, new Rectangle((int)GetColumnX(col), yTop, (int)MathF.Ceiling(_laneWidth), cellH), cellCol);
                }

                // Sub-step lines when snap > 1/16 and zoomed in enough
                float subDiv = _snapDivisor / 16f;
                if (subDiv > 1f && stepH / subDiv >= 7f * s)
                {
                    float subBeat = stepBeat / subDiv;
                    for (int k = 1; k < (int)subDiv; k++)
                    {
                        int sy = BeatToScreenY(bTop + k * subBeat);
                        sb.Draw(_pixel, new Rectangle((int)gridStartX, sy, (int)blockW, 1), Color.White * 0.04f);
                        sb.Draw(_pixel, new Rectangle((int)rightStartX, sy, (int)blockW, 1), Color.White * 0.04f);
                    }
                }

                bool isMeasure = (step % 16) == 0;
                bool isBeat = (step % 4) == 0;
                if (isMeasure || isBeat)
                {
                    int lineH = isMeasure ? Math.Max(2, UiScale.Px(2, s)) : 1;
                    Color lineCol = isMeasure ? MEASURE_LINE : BEAT_LINE * 0.65f;
                    sb.Draw(_pixel, new Rectangle((int)gridStartX, yTop, (int)blockW, lineH), lineCol);
                    sb.Draw(_pixel, new Rectangle((int)rightStartX, yTop, (int)blockW, lineH), lineCol);

                    if (isMeasure)
                    {
                        int barNum = step / 16 + 1;
                        DrawText(sb, $"#{barNum}", new Vector2(gridStartX - gutterOffset, yTop - measureFont * 0.45f), Color.White * 0.65f, measureFont);
                    }
                }
                else
                {
                    sb.Draw(_pixel, new Rectangle((int)gridStartX, yTop, (int)blockW, 1), Color.White * 0.05f);
                    sb.Draw(_pixel, new Rectangle((int)rightStartX, yTop, (int)blockW, 1), Color.White * 0.05f);
                }
            }

            // Event Markers overlay (Read-only timeline inspection across chart)
            DrawGridEvents(sb, gridStartX, blockW, rightStartX, visibleTopBeat, visibleBotBeat);

            // Highlight hovered column
            if (_hoverCol >= 0)
            {
                float lx = GetColumnX(_hoverCol);
                sb.Draw(_pixel, new Rectangle((int)lx, _gridBodyRect.Y, (int)_laneWidth, _gridBodyRect.Height), LANE_COLORS[_hoverLane] * 0.08f);
            }

            // Lane dividers for both Left and BF blocks
            int outerW = Math.Max(2, UiScale.Px(3, s));
            for (int lane = 0; lane <= LANE_COUNT; lane++)
            {
                float lx = gridStartX + lane * _laneWidth;
                float rx = rightStartX + lane * _laneWidth;
                int w = (lane == 0 || lane == LANE_COUNT) ? outerW : 1;
                Color lc = (lane == 0 || lane == LANE_COUNT) ? ACCENT_PINK * 0.5f : GRID_LINE;
                Color rc = (lane == 0 || lane == LANE_COUNT) ? ACCENT_CYAN * 0.5f : GRID_LINE;
                sb.Draw(_pixel, new Rectangle((int)lx, _gridBodyRect.Y, w, _gridBodyRect.Height), lc);
                sb.Draw(_pixel, new Rectangle((int)rx, _gridBodyRect.Y, w, _gridBodyRect.Height), rc);
            }

            // Receptor Bar (drawn beneath notes so beat-0 notes stack cleanly on top inside their squares)
            DrawReceptorBar(sb, gridStartX, rightStartX, blockW, receptorTopY, stepH);

            // All notes for active Left track and Right BF track
            DrawStrumlineNotes(sb, _leftStrumline, stepH, beatH);
            DrawStrumlineNotes(sb, _rightStrumline, stepH, beatH);

            // Hover placement ghost cursor (snapped inside the hovered step square)
            if (!_isPlaying && _hoverCol >= 0 && _hoverSnappedBeat >= 0f &&
                (_currentTool == EditorTool.PlaceNote || _currentTool == EditorTool.PlaceHold))
            {
                int gyTop = BeatToScreenY(_hoverSnappedBeat);
                float gCenterY = gyTop + stepH * 0.5f;
                float gCenterX = GetColumnX(_hoverCol) + _laneWidth * 0.5f;

                var cellRect = new Rectangle((int)GetColumnX(_hoverCol) + 1, gyTop + 1, Math.Max(2, (int)_laneWidth - 2), Math.Max(2, (int)stepH - 2));
                DrawBorder(sb, cellRect, LANE_COLORS[_hoverLane] * 0.45f);

                if (_currentTool == EditorTool.PlaceHold)
                {
                    float gBeats = Math.Max(GetSnapStepBeats(), _defaultHoldBeats);
                    int gh = Math.Max(4, (int)MathF.Round(gBeats * beatH));
                    DrawHoldTrail(sb, _hoverLane, PresetSkin(_activePreset), gCenterX, gCenterY, gh, 0.35f, highlighted: false);
                }
                DrawSingleNote(sb, _hoverLane, PresetSkin(_activePreset), gCenterX, gCenterY, 0.42f, highlighted: false);
            }

            // Restore normal batch
            sb.End();
            gd.RasterizerState = prevRaster;
            gd.ScissorRectangle = prevScissor;
            sb.Begin(SpriteSortMode.Deferred, BlendState.AlphaBlend, SamplerState.LinearClamp);
        }

        private float GetEventSeconds(FnmmEvent ev, bool alreadyNormalized)
        {
            if (alreadyNormalized || _level == null) return ev.Time;
            if (!_level.Data.TimesAreSeconds && _level.Data.Bpm > 0f)
                return ev.Time / _level.Data.Bpm * 60f;
            return ev.Time;
        }

        private static string FormatEventActions(FnmmEvent ev)
        {
            if (ev.Actions.Count == 0) return "Event";
            var parts = new List<string>(Math.Min(3, ev.Actions.Count));
            for (int i = 0; i < ev.Actions.Count && i < 2; i++)
                parts.Add(ev.Actions[i].Type);
            if (ev.Actions.Count > 2)
                parts.Add($"+{ev.Actions.Count - 2}");
            return string.Join(", ", parts);
        }

        private void DrawGridEvents(SpriteBatch sb, float gridStartX, float blockW, float rightStartX, float visibleTopBeat, float visibleBotBeat)
        {
            if (_level == null) return;
            bool evAlreadySec = _previewLevel != null;
            var evList = _previewLevel?.Events.Events ?? _level.Events.Events;
            if (evList.Count == 0) return;

            float s = _uiScale;
            float bpm = _level.Data.EffectiveBpm;
            int evFont = UiScale.FontPx(10, s);
            int badgeW = Math.Max(_gridGroupGap - 2, UiScale.Px(18, s));
            int badgeH = UiScale.Px(14, s);
            int midX = (int)(gridStartX + blockW + _gridGroupGap * 0.5f);
            bool hoverInGrid = _gridBodyRect.Contains(_prevMouse.Position);

            foreach (var ev in evList)
            {
                float evSec = GetEventSeconds(ev, evAlreadySec);
                float evBeat = evSec * bpm / 60f;
                if (evBeat < visibleTopBeat || evBeat > visibleBotBeat) continue;

                int y = BeatToScreenY(evBeat);
                sb.Draw(_pixel, new Rectangle((int)gridStartX, y, (int)(rightStartX + blockW - gridStartX), 1), ACCENT_GOLD * 0.45f);

                var badgeRect = new Rectangle(midX - badgeW / 2, y - badgeH / 2, badgeW, badgeH);
                bool hovered = hoverInGrid && Math.Abs(_prevMouse.Position.Y - y) <= UiScale.Px(8, s);
                sb.Draw(_pixel, badgeRect, hovered ? ACCENT_GOLD : new Color(75, 58, 18));
                DrawBorder(sb, badgeRect, ACCENT_GOLD * 0.85f);
                DrawTextCenteredInBox(sb, ev.Actions.Count > 1 ? $"E{ev.Actions.Count}" : "EV", badgeRect, hovered ? BG_DARK : ACCENT_GOLD, evFont);

                if (hovered)
                    _hoveredEventSummary = $"Beat {evBeat:F2} ({evSec:F2}s): {FormatEventActions(ev)}";
            }
        }

        private void DrawStrumlineNotes(SpriteBatch sb, int strumIdx, float stepH, float beatH)
        {
            if (_level == null || strumIdx < 0 || strumIdx >= _level.Data.Strumlines.Count) return;
            var strum = _level.Data.Strumlines[strumIdx];

            for (int lane = 0; lane < LANE_COUNT && lane < strum.Count; lane++)
            {
                int col = StrumAndLaneToCol(strumIdx, lane);
                if (col < 0) continue;
                float centerX = GetColumnX(col) + _laneWidth * 0.5f;

                foreach (var note in strum[lane])
                {
                    float startBeat = TimeUnitsToBeats(note.Start);
                    int yTop = BeatToScreenY(startBeat);
                    float centerY = yTop + stepH * 0.5f;
                    int holdH = note.Duration > 0f ? Math.Max(4, (int)MathF.Round(TimeUnitsToBeats(note.Duration) * beatH)) : 0;

                    if (centerY + holdH < _gridBodyRect.Y - stepH || centerY > _gridBodyRect.Bottom + stepH)
                        continue;

                    bool isSel = ReferenceEquals(note, _selectedNote);
                    if (holdH > 0)
                        DrawHoldTrail(sb, lane, note.Props.Skin, centerX, centerY, holdH, 1f, isSel);

                    DrawSingleNote(sb, lane, note.Props.Skin, centerX, centerY, 1f, isSel,
                        (int)note.Props.OnPressEvent, (int)note.Props.OnMissEvent);
                }
            }
        }

        private void DrawHoldTrail(SpriteBatch sb, int lane, float skin, float centerX, float topY, int holdH, float alpha, bool highlighted)
        {
            if (holdH <= 1 || lane < 0 || lane >= LANE_COUNT) return;

            int skinType = NoteSkins.Normalize(skin);
            bool hasDedicatedFrame = _noteStyle != null && _noteStyle.NoteFrames[lane, skinType] != null;
            Color skinTint = _noteStyle != null ? _noteStyle.GetSkinTint(skinType) : GetSkinDefaultTint(skinType);
            Color baseTint = skinType == 0
                ? (_noteStyle != null && _noteStyle.ColorizeByLane ? LANE_COLORS[lane] : Color.White)
                : (hasDedicatedFrame ? Color.White : skinTint);
            if (highlighted) baseTint = Color.Lerp(baseTint, ACCENT_GOLD, 0.35f);
            Color spriteTint = baseTint * (alpha * 0.85f);

            // Compute consistent NoteStyle -> GridCell scale from lane arrow size
            float stepH = GetStepHeight();
            float cellFitH = Math.Min(_laneWidth, Math.Max(14f * _uiScale, stepH));
            float maxW = _laneWidth * 0.76f;
            float maxH = cellFitH * 0.76f;
            var refFrame = _noteStyle?.GetNoteFrame(lane, skinType);
            float refW = refFrame != null ? Math.Max(1f, refFrame.FrameW > 0 ? refFrame.FrameW : refFrame.Source.Width) : 154f;
            float refH = refFrame != null ? Math.Max(1f, refFrame.FrameH > 0 ? refFrame.FrameH : refFrame.Source.Height) : 154f;
            float editorNoteScale = Math.Min(maxW / refW, maxH / refH);

            // 1. Codename Engine atlas holds (HoldPieceFrames + HoldEndFrames) — always top-to-bottom Upscroll in chart grid
            if (_noteStyle != null && (_noteStyle.IsCodenameEngine || _noteStyle.NoteSystem == NoteSystemKind.Fmm) &&
                _noteStyle.HoldPieceFrames[lane] is { } pieceFrame &&
                _noteStyle.HoldEndFrames[lane] is { } endFrame)
            {
                var pSrc = pieceFrame.Source.Height > 4
                    ? new Rectangle(pieceFrame.Source.X, pieceFrame.Source.Y + 1, pieceFrame.Source.Width, pieceFrame.Source.Height - 2)
                    : pieceFrame.Source;
                var eSrc = endFrame.Source.Height > 4
                    ? new Rectangle(endFrame.Source.X, endFrame.Source.Y + 1, endFrame.Source.Width, endFrame.Source.Height - 1)
                    : endFrame.Source;

                float bodyScale = Math.Min(editorNoteScale, (_laneWidth * 0.52f) / Math.Max(1, pSrc.Width));
                int intWidth = Math.Max(4, (int)MathF.Round(pSrc.Width * bodyScale));
                int intX = (int)MathF.Round(centerX - intWidth * 0.5f);
                int endWidth = Math.Max(4, (int)MathF.Round(eSrc.Width * bodyScale));
                int endX = (int)MathF.Round(centerX - endWidth * 0.5f);

                float fullCapH = eSrc.Height * bodyScale;
                float partH = holdH - fullCapH;
                float consumed = partH >= 0f ? 0f : 1f - (holdH / Math.Max(1f, fullCapH));
                int capClipY = (int)Math.Round(Math.Clamp(consumed, 0f, 1f) * eSrc.Height);
                int capClipH = eSrc.Height - capClipY;
                int startIntY = (int)MathF.Round(topY);
                int joinY = partH > 0f ? (int)MathF.Round(topY + partH) : startIntY;
                int bodyH = joinY - startIntY;

                if (bodyH > 0)
                    sb.Draw(pieceFrame.Texture, new Rectangle(intX, startIntY, intWidth, bodyH), pSrc, spriteTint);
                if (capClipH > 0)
                {
                    int capH = Math.Max(1, (int)Math.Round(capClipH * bodyScale));
                    var clippedEnd = new Rectangle(eSrc.X, eSrc.Y + capClipY, eSrc.Width, capClipH);
                    sb.Draw(endFrame.Texture, new Rectangle(endX, joinY, endWidth, capH), clippedEnd, spriteTint);
                }
                return;
            }

            // 2. FMM 8-column HoldTexture (SustainTrail Upscroll, bottomClip = 0.9f)
            if (_noteStyle?.HoldTexture != null && _noteStyle.HoldTexture.Width >= 8)
            {
                var holdTex = _noteStyle.HoldTexture;
                int colWidth = holdTex.Width / 8;
                int texHeight = holdTex.Height;
                int bodyCol = lane * 2;
                int capCol = lane * 2 + 1;

                float bodyScale = Math.Min(editorNoteScale, (_laneWidth * 0.52f) / Math.Max(1, colWidth));
                int intWidth = Math.Max(4, (int)MathF.Round(colWidth * bodyScale));
                int intX = (int)MathF.Round(centerX - intWidth * 0.5f);

                const float BOTTOM_CLIP = 0.9f;
                int capSrcBottom = Math.Max(1, (int)Math.Round(BOTTOM_CLIP * texHeight));
                float fullCapH = capSrcBottom * bodyScale;
                float partH = holdH - fullCapH;

                var bodySrc = texHeight > 4
                    ? new Rectangle(bodyCol * colWidth, 1, colWidth, texHeight - 2)
                    : new Rectangle(bodyCol * colWidth, 0, colWidth, texHeight);

                float consumed = partH >= 0f ? 0f : 1f - (holdH / Math.Max(1f, fullCapH));
                int capSrcY = (int)Math.Round(Math.Clamp(consumed, 0f, 1f) * capSrcBottom);
                int capSrcH = capSrcBottom - capSrcY;

                int startIntY = (int)MathF.Round(topY);
                int joinY = partH > 0f ? (int)MathF.Round(topY + partH) : startIntY;
                int bodyH = joinY - startIntY;

                if (bodyH > 0)
                    sb.Draw(holdTex, new Rectangle(intX, startIntY, intWidth, bodyH), bodySrc, spriteTint);
                if (capSrcH > 0)
                {
                    int capH = Math.Max(1, (int)Math.Round(capSrcH * bodyScale));
                    var capSrc = new Rectangle(capCol * colWidth, capSrcY, colWidth, capSrcH);
                    sb.Draw(holdTex, new Rectangle(intX, joinY, intWidth, capH), capSrc, spriteTint);
                }
                return;
            }

            // 3. Programmatic fallback sustain bar + tail cap
            Color fbCol = GetNoteColor(lane, skin) * alpha;
            int fbW = Math.Max(UiScale.Px(12, _uiScale), (int)(_laneWidth * 0.26f));
            var fbRect = new Rectangle((int)(centerX - fbW / 2f), (int)MathF.Round(topY), fbW, holdH);
            sb.Draw(_pixel, fbRect, fbCol * 0.55f);
            DrawBorder(sb, fbRect, highlighted ? ACCENT_GOLD : fbCol);

            int capPxH = Math.Max(4, UiScale.Px(6, _uiScale));
            int capPad = UiScale.Px(3, _uiScale);
            sb.Draw(_pixel, new Rectangle(fbRect.X - capPad, fbRect.Bottom - capPxH / 2, fbRect.Width + capPad * 2, capPxH), highlighted ? ACCENT_GOLD : fbCol);
        }

        // Same as the player: the X / O overlay is purely visual and follows the skin.
        private static string? GetNoteSymbol(int skinType, int pressPackIdx, int missPackIdx)
            => NoteSkins.Normalize(skinType) switch
            {
                NoteSkins.Negative => "X",
                NoteSkins.Positive => "O",
                _ => null,
            };

        private void DrawNoteSymbol(SpriteBatch sb, string symbol, float cx, float cy, float visualSize, Color laneColor, float alpha)
        {
            int fontPx = Math.Clamp((int)MathF.Round(visualSize * 0.42f), 9, 28);
            var sz = MeasureText(symbol, fontPx);
            float baseY = cy - sz.Y * 0.5f;
            Color outline = Color.Black * (0.85f * alpha);
            float off = Math.Max(1f, fontPx * 0.08f);
            DrawText(sb, symbol, new Vector2(cx - off, baseY - off), outline, fontPx, centered: true);
            DrawText(sb, symbol, new Vector2(cx + off, baseY - off), outline, fontPx, centered: true);
            DrawText(sb, symbol, new Vector2(cx - off, baseY + off), outline, fontPx, centered: true);
            DrawText(sb, symbol, new Vector2(cx + off, baseY + off), outline, fontPx, centered: true);
            DrawText(sb, symbol, new Vector2(cx, baseY), laneColor * alpha, fontPx, centered: true);
        }

        private static float DrawAtlasFrameCentered(SpriteBatch sb, AtlasFrame f, float centerX, float centerY, float maxW, float maxH, Color tint, float extraScale = 1f)
        {
            float frameW = f.FrameW > 0 ? f.FrameW : f.Source.Width;
            float frameH = f.FrameH > 0 ? f.FrameH : f.Source.Height;
            float scale = Math.Min(maxW / Math.Max(1f, frameW), maxH / Math.Max(1f, frameH)) * extraScale;

            if (f.Rotated)
            {
                Vector2 pivot = new(f.Source.Width * 0.5f, f.Source.Height * 0.5f);
                sb.Draw(f.Texture, new Vector2(centerX, centerY), f.Source, tint, -MathF.PI / 2f, pivot, scale, SpriteEffects.None, 0f);
            }
            else
            {
                float drawX = centerX - frameW * scale * 0.5f + (-f.OffsetX) * scale;
                float drawY = centerY - frameH * scale * 0.5f + (-f.OffsetY) * scale;
                sb.Draw(f.Texture, new Vector2(drawX, drawY), f.Source, tint, 0f, Vector2.Zero, scale, SpriteEffects.None, 0f);
            }
            return frameW * scale;
        }

        private void DrawSingleNote(SpriteBatch sb, int lane, float skin, float centerX, float centerY, float alpha, bool highlighted,
            int onPressEvent = -1, int onMissEvent = -1)
        {
            float s = _uiScale;
            float stepH = GetStepHeight();
            float cellFitH = Math.Min(_laneWidth, Math.Max(14f * s, stepH));
            float maxW = _laneWidth * 0.76f;
            float maxH = cellFitH * 0.76f;
            int skinType = NoteSkins.Normalize(skin);

            if (highlighted)
            {
                int boxW = Math.Max(6, (int)(_laneWidth * 0.90f));
                int boxH = Math.Max(6, (int)(cellFitH * 0.90f));
                var selRect = new Rectangle((int)(centerX - boxW / 2f), (int)(centerY - boxH / 2f), boxW, boxH);
                sb.Draw(_pixel, selRect, ACCENT_GOLD * 0.22f);
                DrawBorder(sb, selRect, ACCENT_GOLD);
            }

            string? symbol = GetNoteSymbol(skinType, onPressEvent, onMissEvent);

            if (_noteStyle != null)
            {
                var frame = _noteStyle.GetNoteFrame(lane, skinType);
                if (frame != null)
                {
                    bool hasDedicated = _noteStyle.NoteFrames[lane, skinType] != null;
                    Color baseTint = skinType == 0
                        ? (_noteStyle.ColorizeByLane ? LANE_COLORS[lane] : Color.White)
                        : (hasDedicated ? Color.White : _noteStyle.GetSkinTint(skinType));
                    float visualW = DrawAtlasFrameCentered(sb, frame, centerX, centerY, maxW, maxH, baseTint * alpha);
                    if (!hasDedicated && symbol != null)
                        DrawNoteSymbol(sb, symbol, centerX, centerY, visualW, LANE_COLORS[lane], alpha);
                    return;
                }
            }

            Color col = GetNoteColor(lane, skin) * alpha;
            int sqW = (int)(maxW * 0.85f);
            int sqH = (int)(maxH * 0.85f);
            var rect = new Rectangle((int)(centerX - sqW / 2f), (int)(centerY - sqH / 2f), sqW, sqH);
            sb.Draw(_pixel, rect, col * 0.85f);
            DrawBorder(sb, rect, Color.White * (0.7f * alpha));
            if (symbol != null)
                DrawNoteSymbol(sb, symbol, centerX, centerY, sqW, LANE_COLORS[lane], alpha);
        }

        private void DrawReceptorBar(SpriteBatch sb, float leftStartX, float rightStartX, float blockW, float receptorTopY, float stepH)
        {
            float s = _uiScale;
            int rowH = Math.Max(8, (int)MathF.Round(stepH));
            int topY = (int)MathF.Round(receptorTopY);

            // Subtle playhead row highlight band + crisp top beat timing line across Left and BF
            sb.Draw(_pixel, new Rectangle((int)leftStartX, topY, (int)blockW, rowH), Color.White * 0.05f);
            sb.Draw(_pixel, new Rectangle((int)rightStartX, topY, (int)blockW, rowH), Color.White * 0.05f);
            int barH = Math.Max(2, UiScale.Px(3, s));
            sb.Draw(_pixel, new Rectangle((int)leftStartX, topY - barH / 2, (int)blockW, barH), ACCENT_PINK * 0.85f);
            sb.Draw(_pixel, new Rectangle((int)rightStartX, topY - barH / 2, (int)blockW, barH), ACCENT_CYAN * 0.85f);

            float centerY = receptorTopY + stepH * 0.5f;
            float cellFitH = Math.Min(_laneWidth, Math.Max(14f * s, stepH));
            float maxW = _laneWidth * 0.72f;
            float maxH = cellFitH * 0.72f;

            for (int col = 0; col < GRID_COLS; col++)
            {
                ColToStrumAndLane(col, out int sl, out int lane);
                float centerX = GetColumnX(col) + _laneWidth * 0.5f;
                int safeSl = Math.Clamp(sl, 0, MAX_STRUMLINES - 1);
                bool pressed = _receptorPressTime[safeSl, lane] > 0f;
                float pressScale = pressed ? 0.90f : 1.0f;

                if (_noteStyle != null)
                {
                    int recState = pressed ? 1 : 0;
                    var recList = _noteStyle.ReceptorFrames[lane, recState];
                    if ((recList == null || recList.Count == 0) && recState != 0)
                        recList = _noteStyle.ReceptorFrames[lane, 0];

                    var frame = (recList != null && recList.Count > 0) ? recList[0] : _noteStyle.GetNoteFrame(lane, 0);
                    Color tint = (recList != null && recList.Count > 0)
                        ? (_noteStyle.ColorizeByLane ? LANE_COLORS[lane] : Color.White) * (pressed ? 0.95f : 0.65f)
                        : LANE_COLORS[lane] * (pressed ? 0.90f : 0.45f);

                    if (frame != null)
                    {
                        DrawAtlasFrameCentered(sb, frame, centerX, centerY, maxW, maxH, tint, pressScale);
                        continue;
                    }
                }

                Color colCol = LANE_COLORS[lane] * (pressed ? 0.95f : 0.65f);
                int sqW = (int)(maxW * 0.80f * pressScale);
                int sqH = (int)(maxH * 0.80f * pressScale);
                var rect = new Rectangle((int)(centerX - sqW / 2f), (int)(centerY - sqH / 2f), sqW, sqH);
                sb.Draw(_pixel, rect, colCol * 0.30f);
                DrawBorder(sb, rect, colCol);
            }
        }

        // ── Right Info & Inspector Panel ─────────────────────────────────────
        private void DrawInfoPanel(SpriteBatch sb)
        {
            float s = _uiScale;
            sb.Draw(_pixel, _infoPanelRect, BG_PANEL);
            sb.Draw(_pixel, new Rectangle(_infoPanelRect.X, _infoPanelRect.Y, 2, _infoPanelRect.Height), ACCENT_PINK * 0.45f);
            if (_level == null) return;

            int padX = UiScale.Px(12, s);
            int x = _infoPanelRect.X + padX;
            int w = _infoPanelRect.Width - padX * 2;
            int y = _infoPanelRect.Y + UiScale.Px(10, s);
            int contentBottom = _showPreview ? _previewRect.Y - UiScale.Px(8, s) : _infoPanelRect.Bottom - UiScale.Px(8, s);

            int hdrFont = UiScale.FontPx(13, s);
            int bodyFont = UiScale.FontPx(13, s);
            int smallFont = UiScale.FontPx(12, s);
            int tipFont = UiScale.FontPx(11, s);
            int rowStep = UiScale.Px(20, s);
            int ctrlBtnW = UiScale.Px(30, s);
            int ctrlBtnH = UiScale.Px(24, s);
            int ctrlGap = UiScale.Px(6, s);

            // Selected Note Inspector card (when a note is selected)
            if (_selectedNote != null)
            {
                DrawText(sb, "SELECTED NOTE", new Vector2(x, y), ACCENT_GOLD, hdrFont);
                y += rowStep;

                float sec = TimeUnitsToSeconds(_selectedNote.Start);
                float beat = TimeUnitsToBeats(_selectedNote.Start);
                DrawText(sb, $"Beat: {beat:F2} ({sec:F2}s)", new Vector2(x, y), Color.White, bodyFont);
                y += rowStep;

                string sideTag = _selectedNoteStrumline == _rightStrumline ? "BF" : (_level.Data.Strumlines.Count > 2 ? $"SL{_selectedNoteStrumline}" : "FOE");
                DrawText(sb, $"{sideTag} {GetLaneName(_selectedNoteLane)} | {DescribeNote(_selectedNote.Props)}", new Vector2(x, y), Color.White * 0.85f, smallFont);
                y += UiScale.Px(22, s);

                // Hold Duration row
                float holdBeats = TimeUnitsToBeats(_selectedNote.Duration);
                DrawText(sb, $"Hold: {holdBeats:F2}b", new Vector2(x, y + UiScale.Px(3, s)), ACCENT_CYAN, bodyFont);
                _inspDurMinusBtn = new Rectangle(_infoPanelRect.Right - padX - ctrlBtnW * 2 - ctrlGap, y, ctrlBtnW, ctrlBtnH);
                _inspDurPlusBtn = new Rectangle(_infoPanelRect.Right - padX - ctrlBtnW, y, ctrlBtnW, ctrlBtnH);
                DrawButton(sb, _inspDurMinusBtn, "-", BG_CARD, Color.White, bodyFont, Color.White * 0.15f, () => AdjustHoldDuration(-1));
                DrawButton(sb, _inspDurPlusBtn, "+", BG_CARD, Color.White, bodyFont, Color.White * 0.15f, () => AdjustHoldDuration(1));
                y += UiScale.Px(30, s);

                // Skin + Swap Left/BF + Delete buttons
                int actionGap = UiScale.Px(5, s);
                int bw = (w - actionGap * 2) / 3;
                int actH = UiScale.Px(26, s);
                _inspSkinBtn = new Rectangle(x, y, bw, actH);
                _inspSwapStrumBtn = new Rectangle(x + bw + actionGap, y, bw, actH);
                _inspDeleteBtn = new Rectangle(x + (bw + actionGap) * 2, y, bw, actH);
                string leftSwapTag = _level.Data.Strumlines.Count > 2 ? $"-> SL{_leftStrumline}" : "-> FOE";
                string swapLbl = _selectedNoteStrumline == _rightStrumline ? leftSwapTag : "-> BF";
                DrawButton(sb, _inspSkinBtn, "PRESET (T)", BG_CARD, Color.White, smallFont, ACCENT_CYAN * 0.5f, CycleSelectedNotePreset);
                DrawButton(sb, _inspSwapStrumBtn, swapLbl, BG_CARD, ACCENT_GOLD, smallFont, ACCENT_GOLD * 0.5f, SwapSelectedNoteStrumline);
                DrawButton(sb, _inspDeleteBtn, "DEL", new Color(110, 32, 48), Color.White, smallFont, ACCENT_PINK, () =>
                {
                    if (_selectedNote != null)
                    {
                        DeleteSpecificNote(_selectedNoteStrumline, _selectedNoteLane, _selectedNote);
                        _selectedNote = null;
                    }
                });
                y += UiScale.Px(34, s);
            }
            else
            {
                _inspDurMinusBtn = _inspDurPlusBtn = _inspDeleteBtn = _inspSkinBtn = _inspSwapStrumBtn = Rectangle.Empty;

                // Chart Stats
                DrawText(sb, "CHART INFO", new Vector2(x, y), ACCENT_GOLD, hdrFont);
                y += rowStep;

                int leftNotes = CountStrumNotes(_leftStrumline);
                int rightNotes = CountStrumNotes(_rightStrumline);
                string leftTitle = _level.Data.Strumlines.Count > 2 ? $"Left (SL{_leftStrumline}):" : "Opponent (FOE):";
                DrawText(sb, $"{leftTitle} {leftNotes}", new Vector2(x, y), ACCENT_PINK, bodyFont);
                y += rowStep;
                DrawText(sb, $"Player (BF):  {rightNotes}", new Vector2(x, y), ACCENT_CYAN, bodyFont);
                y += rowStep;

                if (_level.Data.Strumlines.Count > 2)
                {
                    DrawText(sb, $"Strumlines: {_level.Data.Strumlines.Count} (Tab to cycle)", new Vector2(x, y), ACCENT_GOLD * 0.9f, smallFont);
                    y += rowStep;
                }

                if (!string.IsNullOrEmpty(_hoveredEventSummary))
                {
                    DrawText(sb, _hoveredEventSummary, new Vector2(x, y), ACCENT_GOLD, smallFont);
                    y += rowStep;
                }
                y += UiScale.Px(6, s);
            }

            // BPM / Grid Zoom / Gameplay Note Speed adjusters
            int adjBlockH = UiScale.Px(96, s);
            int adjStep = UiScale.Px(30, s);
            if (y + adjBlockH < contentBottom)
            {
                int minusX = _infoPanelRect.Right - padX - ctrlBtnW * 2 - ctrlGap;
                int plusX = _infoPanelRect.Right - padX - ctrlBtnW;
                int lblOffY = UiScale.Px(3, s);

                DrawText(sb, $"BPM: {_level.Data.EffectiveBpm:F1}", new Vector2(x, y + lblOffY), Color.White, bodyFont);
                _inspBpmMinusBtn = new Rectangle(minusX, y, ctrlBtnW, ctrlBtnH);
                _inspBpmPlusBtn = new Rectangle(plusX, y, ctrlBtnW, ctrlBtnH);
                DrawButton(sb, _inspBpmMinusBtn, "-", BG_CARD, Color.White, bodyFont, Color.White * 0.15f, () => ChangeBpm(-1f));
                DrawButton(sb, _inspBpmPlusBtn, "+", BG_CARD, Color.White, bodyFont, Color.White * 0.15f, () => ChangeBpm(1f));
                y += adjStep;

                DrawText(sb, $"Grid Zoom: {_zoom:F2}x", new Vector2(x, y + lblOffY), Color.White * 0.9f, bodyFont);
                _inspZoomMinusBtn = new Rectangle(minusX, y, ctrlBtnW, ctrlBtnH);
                _inspZoomPlusBtn = new Rectangle(plusX, y, ctrlBtnW, ctrlBtnH);
                DrawButton(sb, _inspZoomMinusBtn, "-", BG_CARD, Color.White, bodyFont, Color.White * 0.15f, () => ChangeZoom(-0.25f));
                DrawButton(sb, _inspZoomPlusBtn, "+", BG_CARD, Color.White, bodyFont, Color.White * 0.15f, () => ChangeZoom(0.25f));
                y += adjStep;

                DrawText(sb, $"Note Spd: {_level.Data.Speed:F2}x", new Vector2(x, y + lblOffY), ACCENT_CYAN, bodyFont);
                _inspSpeedMinusBtn = new Rectangle(minusX, y, ctrlBtnW, ctrlBtnH);
                _inspSpeedPlusBtn = new Rectangle(plusX, y, ctrlBtnW, ctrlBtnH);
                DrawButton(sb, _inspSpeedMinusBtn, "-", BG_CARD, Color.White, bodyFont, Color.White * 0.15f, () => ChangeChartSpeed(-0.1f));
                DrawButton(sb, _inspSpeedPlusBtn, "+", BG_CARD, Color.White, bodyFont, Color.White * 0.15f, () => ChangeChartSpeed(0.1f));
                y += adjStep + UiScale.Px(4, s);
            }
            else
            {
                _inspBpmMinusBtn = _inspBpmPlusBtn = _inspZoomMinusBtn = _inspZoomPlusBtn = _inspSpeedMinusBtn = _inspSpeedPlusBtn = Rectangle.Empty;
            }

            // Concise Shortcuts list in remaining top area
            if (y + UiScale.Px(30, s) < contentBottom)
            {
                DrawText(sb, "SHORTCUTS", new Vector2(x, y), ACCENT_GOLD, smallFont);
                y += UiScale.Px(18, s);
                string[] tips =
                {
                    "L-Click: Place/Drag | R-Click: Del",
                    "D/F/J/K: Step Input | M: Mirror Bar",
                    "Space: Play | Enter: Playtest",
                    "Ctrl+C/V: Copy/Paste Measure",
                    "Tab: Track | C: Cam | H: Snd | N: Met",
                    "Ctrl+Wheel/+/-: Zoom | O/P: Spd",
                    "Ctrl+Z/Y: Undo/Redo | Ctrl+S: Save",
                };
                int tipStep = UiScale.Px(16, s);
                foreach (var tip in tips)
                {
                    if (y + tipStep > contentBottom) break;
                    DrawText(sb, tip, new Vector2(x, y), Color.White * 0.6f, tipFont);
                    y += tipStep;
                }
            }
        }

        // ── Docked Engine Stage Preview (Live RenderTarget2D) ────────────────
        private void DrawCharacterPreview(SpriteBatch sb)
        {
            if (_level == null || _previewRect == Rectangle.Empty) return;
            float s = _uiScale;

            sb.Draw(_pixel, _previewRect, BG_DARK);
            DrawBorder(sb, _previewRect, ACCENT_CYAN * 0.6f);

            int hdrH = UiScale.Px(26, s);
            int pad = UiScale.Px(6, s);
            int footerH = UiScale.Px(28, s);

            // Header bar with Camera mode, FX overlay toggle, and Close button
            sb.Draw(_pixel, new Rectangle(_previewRect.X, _previewRect.Y, _previewRect.Width, hdrH), BG_CARD);
            string hdrLabel = (_previewLevel?.Scene.Cameras.Count ?? 0) > 0 || _previewLevel?.Scene.Objects.Count > 0
                ? "STAGE"
                : "STAGE (NONE)";
            DrawText(sb, hdrLabel, new Vector2(_previewRect.X + UiScale.Px(8, s), _previewRect.Y + UiScale.Px(5, s)), ACCENT_CYAN, UiScale.FontPx(11, s));

            int btnFont = UiScale.FontPx(10, s);
            DrawButton(sb, _previewCamBtnRect, GetPreviewCameraLabel(), new Color(30, 52, 78), ACCENT_GOLD, btnFont, ACCENT_CYAN * 0.55f, CyclePreviewCamera);
            DrawButton(sb, _previewFxBtnRect, "FX", _previewShowOverlays ? new Color(36, 78, 56) : BG_DARK, _previewShowOverlays ? ACCENT_GREEN : Color.Gray, btnFont, _previewShowOverlays ? ACCENT_GREEN * 0.7f : Color.White * 0.15f,
                () => { _previewShowOverlays = !_previewShowOverlays; ShowToast(_previewShowOverlays ? "Stage Overlays ON" : "Stage Overlays OFF"); });
            DrawButton(sb, _previewCloseBtn, "X", new Color(75, 32, 46), Color.White, btnFont, ACCENT_PINK * 0.7f, () => _showPreview = false);

            var stageRect = new Rectangle(_previewRect.X + pad, _previewRect.Y + hdrH + UiScale.Px(4, s),
                _previewRect.Width - pad * 2, Math.Max(40, _previewRect.Height - hdrH - footerH - pad));
            sb.Draw(_pixel, stageRect, new Color(12, 12, 18));

            if (_previewLevel == null || _previewLevel.Scene.Objects.Count == 0 || _previewTarget == null)
            {
                DrawTextCenteredInBox(sb, "Chart-only level (No stage)", stageRect, Color.Gray, UiScale.FontPx(12, s));
            }
            else
            {
                float scale = Math.Min((float)stageRect.Width / _previewTargetW, (float)stageRect.Height / _previewTargetH);
                int dstW = Math.Max(1, (int)MathF.Round(_previewTargetW * scale));
                int dstH = Math.Max(1, (int)MathF.Round(_previewTargetH * scale));
                int dstX = stageRect.X + (stageRect.Width - dstW) / 2;
                int dstY = stageRect.Y + (stageRect.Height - dstH) / 2;
                sb.Draw(_previewTarget, new Rectangle(dstX, dstY, dstW, dstH), Color.White);
            }

            // Character singing arrow strips at bottom of preview
            int footerY = _previewRect.Bottom - UiScale.Px(14, s);
            string leftInd = _level.Data.Strumlines.Count > 2 ? $"SL{_leftStrumline}" : "FOE";
            DrawCharacterIndicator(sb, new Vector2(_previewRect.X + _previewRect.Width * 0.25f, footerY), _leftStrumline, leftInd);
            DrawCharacterIndicator(sb, new Vector2(_previewRect.X + _previewRect.Width * 0.75f, footerY), _rightStrumline, "BF");
        }

        private void EnsurePreviewLoaded(GraphicsDevice gd)
        {
            if (_previewInitialized || _level == null) return;
            _previewInitialized = true;

            try
            {
                _previewLevel = FnmmSceneRenderer.CloneForStagePreview(_level);
                _previewTargetW = _previewLevel.Scene.Landscape ? 1280 : 720;
                _previewTargetH = _previewLevel.Scene.Landscape ? 720 : 1280;

                int maxTexSize = FnmmSceneRenderer.GetGpuMaxTextureSize();
                _previewTexCache = FnmmSceneRenderer.LoadLevelTextures(gd, _previewLevel, maxTexSize, _previewSpriteTextureCache);
                _previewAnimators = FnmmSceneRenderer.CreateAnimators(_previewLevel, _previewTexCache, _previewTargetW, _previewTargetH);
                _previewCameraManager = new CameraManager();
                _previewEvents = new EventProcessor(_previewLevel, _previewAnimators, _previewTexCache);

                if (_previewLevel.Scene.Objects.Count > 0)
                {
                    if (_previewTarget == null || _previewTarget.IsDisposed ||
                        _previewTarget.Width != _previewTargetW || _previewTarget.Height != _previewTargetH)
                    {
                        try { _previewTarget?.Dispose(); } catch { }
                        _previewTarget = new RenderTarget2D(gd, _previewTargetW, _previewTargetH);
                    }
                }
                else if (_previewTarget != null)
                {
                    try { _previewTarget.Dispose(); } catch { }
                    _previewTarget = null;
                }

                SyncPreviewToTime(TimeUnitsToSeconds(_scrollY));
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[ChartEditor] Engine preview load warning: {ex.Message}");
            }
        }

        private void RenderPreviewToTarget(SpriteBatch sb)
        {
            if (!_previewInitialized || _previewLevel == null || _previewEvents == null ||
                _previewCameraManager == null || _previewTarget == null || _previewLevel.Scene.Objects.Count == 0)
                return;

            var gd = sb.GraphicsDevice;
            var prevTargets = gd.GetRenderTargets();
            sb.End();

            try
            {
                gd.SetRenderTarget(_previewTarget);
                gd.Clear(new Color(12, 12, 18));

                float savedX = _previewEvents.Camera.X;
                float savedY = _previewEvents.Camera.Y;
                float savedScale = _previewEvents.Camera.Scale;
                bool tempOverride = false;

                Matrix camMatrix;
                if (_previewCameraMode == -2)
                {
                    var (fx, fy, fscale) = FnmmSceneRenderer.ComputeFitCamera(_previewLevel, _previewAnimators);
                    var fitCam = new CameraState { X = fx, Y = fy, Scale = fscale };
                    _previewEvents.Camera.X = fx;
                    _previewEvents.Camera.Y = fy;
                    _previewEvents.Camera.Scale = fscale;
                    tempOverride = true;
                    camMatrix = _previewCameraManager.BuildCameraMatrix(_previewTargetW, _previewTargetH, fitCam, hasCameraEvents: true);
                }
                else if (_previewCameraMode >= 0 && _previewCameraMode < _previewLevel.Scene.Cameras.Count)
                {
                    var c = _previewLevel.Scene.Cameras[_previewCameraMode];
                    float cScale = c.Scale > 0.01f ? c.Scale : 1f;
                    var manualCam = new CameraState { X = (float)c.X, Y = (float)c.Y, Scale = cScale };
                    _previewEvents.Camera.X = (float)c.X;
                    _previewEvents.Camera.Y = (float)c.Y;
                    _previewEvents.Camera.Scale = cScale;
                    tempOverride = true;
                    camMatrix = _previewCameraManager.BuildCameraMatrix(_previewTargetW, _previewTargetH, manualCam, hasCameraEvents: true);
                }
                else
                {
                    camMatrix = _previewCameraManager.BuildCameraMatrix(_previewTargetW, _previewTargetH, _previewEvents.Camera, _previewEvents.HasCameraEvents);
                }

                FnmmSceneRenderer.DrawSceneOrdered(sb, _previewLevel, _previewAnimators, _previewEvents, _previewCameraManager,
                    _previewTargetW, _previewTargetH, camMatrix, activeEffect: null, includeFixedOverlays: _previewShowOverlays);

                if (tempOverride)
                {
                    _previewEvents.Camera.X = savedX;
                    _previewEvents.Camera.Y = savedY;
                    _previewEvents.Camera.Scale = savedScale;
                }

                if (_previewShowOverlays && _previewEvents.ActiveFlashes.Count > 0)
                {
                    sb.Begin(SpriteSortMode.Deferred, BlendState.AlphaBlend, SamplerState.LinearClamp);
                    GameplayHudOverlay.DrawScreenFlashes(sb, _pixel, _previewEvents, _previewTargetW, _previewTargetH);
                    sb.End();
                }
            }
            finally
            {
                gd.SetRenderTargets(prevTargets);
                gd.Clear(Color.Black);
                sb.Begin(samplerState: SamplerState.LinearClamp);
            }
        }

        private void DrawCharacterIndicator(SpriteBatch sb, Vector2 center, int strumIdx, string label)
        {
            float s = _uiScale;
            int safeSl = Math.Clamp(strumIdx, 0, MAX_STRUMLINES - 1);
            bool isSinging = _characterSingTime[safeSl] > 0f;
            int singLane = _characterSingLane[safeSl];

            int lblFont = UiScale.FontPx(11, s);
            DrawText(sb, label, new Vector2(center.X - UiScale.Px(42, s), center.Y - UiScale.Px(7, s)), isSinging ? Color.White : Color.Gray, lblFont);
            for (int lane = 0; lane < LANE_COUNT; lane++)
            {
                bool active = isSinging && singLane == lane;
                int size = UiScale.Px(active ? 10 : 7, s, 4);
                int ax = (int)(center.X - UiScale.Px(8, s) + lane * UiScale.Px(14, s));
                int ay = (int)(center.Y - size / 2f);
                sb.Draw(_pixel, new Rectangle(ax, ay, size, size), LANE_COLORS[lane] * (active ? 1f : 0.25f));
            }
        }

        // ── Bottom Status Bar ────────────────────────────────────────────────
        private void DrawStatusBar(SpriteBatch sb)
        {
            float s = _uiScale;
            sb.Draw(_pixel, _statusBarRect, BG_PANEL);
            sb.Draw(_pixel, new Rectangle(_statusBarRect.X, _statusBarRect.Y, _statusBarRect.Width, 1), ACCENT_PINK * 0.45f);
            if (_level == null) return;

            int stFont = UiScale.FontPx(12, s);
            string state = _isPlaying ? "PLAYING" : "PAUSED";
            string mod = IsModified ? " | Unsaved *" : "";
            float curBeat = TimeUnitsToBeats(_scrollY);
            string leftText = $"{state} | Beat {curBeat:F2} | {FormatTime(TimeUnitsToSeconds(_scrollY))} | Snap 1/{_snapDivisor} | Tool: {_currentTool}{mod}";
            float textH = MeasureText(leftText, stFont).Y;
            DrawText(sb, leftText, new Vector2(_statusBarRect.X + UiScale.Px(10, s), _statusBarRect.Y + (_statusBarRect.Height - textH) / 2f), _isPlaying ? ACCENT_GREEN : Color.White * 0.85f, stFont);

            if (!string.IsNullOrEmpty(_toastMessage) && _toastTimer > 0f)
            {
                var sz = MeasureText(_toastMessage, stFont);
                DrawText(sb, _toastMessage, new Vector2(_statusBarRect.Right - sz.X - UiScale.Px(12, s), _statusBarRect.Y + (_statusBarRect.Height - sz.Y) / 2f), ACCENT_GOLD, stFont);
            }
        }

        // ── UI Drawing Helpers ───────────────────────────────────────────────
        private void DrawButton(SpriteBatch sb, Rectangle rect, string text, Color bg, Color textColor, int fontSize, Color borderColor, Action onClick)
        {
            bool hovered = rect.Contains(_prevMouse.Position);
            sb.Draw(_pixel, rect, hovered ? Color.Lerp(bg, Color.White, 0.14f) : bg);
            DrawBorder(sb, rect, hovered ? Color.White * 0.7f : borderColor);
            DrawTextCenteredInBox(sb, text, rect, textColor, fontSize);
            _clickActions.Add((rect, onClick, null));
        }

        private void DrawBorder(SpriteBatch sb, Rectangle r, Color c)
        {
            sb.Draw(_pixel, new Rectangle(r.X, r.Y, r.Width, 1), c);
            sb.Draw(_pixel, new Rectangle(r.X, r.Bottom - 1, r.Width, 1), c);
            sb.Draw(_pixel, new Rectangle(r.X, r.Y, 1, r.Height), c);
            sb.Draw(_pixel, new Rectangle(r.Right - 1, r.Y, 1, r.Height), c);
        }

        private Vector2 MeasureText(string text, int fontSize)
        {
            if (_fontRenderer != null)
                return _fontRenderer.MeasureString(text, fontSize);
            if (_font != null)
                return _font.MeasureString(text) * (fontSize / 32f);
            return new Vector2(text.Length * fontSize * 0.55f, fontSize);
        }

        private void DrawText(SpriteBatch sb, string text, Vector2 pos, Color color, int fontSize, bool centered = false)
        {
            if (centered)
            {
                var sz = MeasureText(text, fontSize);
                pos.X -= sz.X / 2f;
            }

            if (_fontRenderer != null)
                _fontRenderer.DrawString(sb, text, pos, color, fontSize);
            else if (_font != null)
                sb.DrawString(_font, text, pos, color, 0f, Vector2.Zero, fontSize / 32f, SpriteEffects.None, 0f);
        }

        private void DrawTextCenteredInBox(SpriteBatch sb, string text, Rectangle box, Color color, int fontSize)
        {
            var sz = MeasureText(text, fontSize);
            var pos = new Vector2(box.X + (box.Width - sz.X) / 2f, box.Y + (box.Height - sz.Y) / 2f);
            DrawText(sb, text, pos, color, fontSize);
        }

        private int CountStrumNotes(int strumIdx)
        {
            if (_level == null || strumIdx >= _level.Data.Strumlines.Count) return 0;
            int total = 0;
            foreach (var lane in _level.Data.Strumlines[strumIdx])
                total += lane.Count;
            return total;
        }

        private static Color GetSkinDefaultTint(int skinType) => NoteSkins.Normalize(skinType) switch
        {
            NoteSkins.Negative => new Color(255, 50, 50),
            NoteSkins.Positive => new Color(0, 200, 255),
            _ => Color.White,
        };

        private static Color GetNoteColor(int lane, float skin) => NoteSkins.Normalize(skin) switch
        {
            NoteSkins.Negative => new Color(255, 50, 50),
            NoteSkins.Positive => new Color(0, 200, 255),
            _ => LANE_COLORS[Math.Clamp(lane, 0, LANE_COLORS.Length - 1)],
        };

        private static string GetLaneName(int lane) => lane switch
        {
            0 => "LEFT",
            1 => "DOWN",
            2 => "UP",
            3 => "RIGHT",
            _ => "?"
        };

        private static string FormatTime(float seconds)
        {
            if (seconds < 0f) seconds = 0f;
            int totalTenths = (int)MathF.Round(seconds * 10f);
            int mins = totalTenths / 600;
            int remTenths = totalTenths % 600;
            int wholeSecs = remTenths / 10;
            int tenth = remTenths % 10;
            return $"{mins:D2}:{wholeSecs:D2}.{tenth}";
        }

        private bool JustPressed(KeyboardState kb, Keys key) => kb.IsKeyDown(key) && !_prevKeyboard.IsKeyDown(key);

        private enum EditorPhase { Loading, Ready, Error }
        private enum EditorTool { Select, PlaceNote, PlaceHold, Delete }

        private sealed class DelegateEditorAction : IEditorAction
        {
            private readonly Action _execute;
            private readonly Action _undo;

            public DelegateEditorAction(Action execute, Action undo)
            {
                _execute = execute;
                _undo = undo;
            }

            public void Execute() => _execute();
            public void Undo() => _undo();
        }
    }
}
