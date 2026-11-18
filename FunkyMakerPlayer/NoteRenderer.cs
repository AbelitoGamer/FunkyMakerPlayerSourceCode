using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using Microsoft.Xna.Framework.Input;

namespace FunkyMakerPlayer;

/// <summary>
/// FNF scroll modes:
///   UPSCROLL   (Downscroll=false): receptor at TOP    (15%), notes come from BELOW, travel UP
///   DOWNSCROLL (Downscroll=true):  receptor at BOTTOM (85%), notes come from ABOVE, travel DOWN
///
/// FNF layout: Strumline 0 = PLAYER (RIGHT), Strumline 1 = OPPONENT (LEFT)
/// </summary>
public class NoteRenderer
{
    public const int   LANE_COUNT  = 4;
    public const float LANE_WIDTH  = 110f;  // Balanced spacing - not too close, not too far

    // ── Hit windows (seconds) — matching FNF base game ───────────────────────
    // Sick:  ±45ms   → 350 pts
    // Good:  ±90ms   → 200 pts
    // Bad:   ±135ms  → 100 pts
    // Shit:  ±166ms  →  50 pts  (also the miss threshold)
    public const float HIT_WINDOW  = 0.166f;   // outermost window — beyond this = miss
    private const float W_SICK  = 0.045f;
    private const float W_GOOD  = 0.090f;
    private const float W_BAD   = 0.135f;
    // W_SHIT = HIT_WINDOW (0.166f)

    private const float BASE_SPEED = 450f;  // Standard FNF scroll speed (1.0x)

    private const float STRUM_Y_UP   = 0.15f;  // upscroll:   receptor near TOP
    private const float STRUM_Y_DOWN = 0.85f;  // downscroll: receptor near BOTTOM

    private static readonly Color[] LaneColors =
    {
        new Color(0xC2, 0x4B, 0x99),
        new Color(0x00, 0xFF, 0xFF),
        new Color(0x12, 0xFA, 0x05),
        new Color(0xF9, 0x39, 0x3F),
    };

    // ── Global options ───────────────────────────────────────────────────────
    public static bool  Autoplay         = false;  // Changed to false - players must press notes manually
    public static bool  Downscroll       = false;
    /// <summary>
    /// Centers the player strumline in the middle of the screen (hides opponent strumline).
    /// </summary>
    public static bool  Midlescroll      = false;
    /// <summary>
    /// Master visibility toggle for all strumlines (receptors, notes, holds, splashes).
    /// </summary>
    public static bool  ShowStrumlines   = true;
    /// <summary>
    /// Hides the player's strumline (strumline 0) visually while keeping gameplay input active.
    /// </summary>
    public static bool  HidePlayerStrumline = false;
    /// <summary>
    /// Visual scroll speed multiplier. 1.0 = normal, 2.0 = 2x faster visually.
    /// Only affects how fast notes move on screen — does NOT affect timing or audio sync.
    /// </summary>
    public static float ScrollMultiplier = 1.0f;
    /// <summary>Configurable key bindings: left, down, up, right.</summary>
    public static Keys[] PlayerKeys = { Keys.D, Keys.F, Keys.J, Keys.K };
    
    /// <summary>
    /// Ghost Tapping: When false, pressing keys without notes nearby breaks combo and counts as miss.
    /// When true (default), ghost taps have no penalty.
    /// </summary>
    public static bool GhostTapping = true;

    /// <summary>Draw each lane's key name on the player's strums (Settings → Gameplay). Off by default.</summary>
    public static bool ShowKeyLabels = false;

    /// <summary>Opacity (0–0.75) of the black lane underlay behind the player strumline. 0 = off.</summary>
    public static float LaneUnderlay = 0f;
    public const float LaneUnderlayMax = 0.75f;
    
    /// <summary>
    /// Input buffer in seconds. Allows hitting notes slightly early (before hit window).
    /// Default: 0.010 (10ms) - makes the game feel more responsive.
    /// </summary>
    public static float InputBuffer = 0.010f;
    
    /// <summary>
    /// Safe frames at song start. Misses don't count during this time.
    /// Default: 5 frames (~0.083s at 60fps) - prevents accidental early misses.
    /// </summary>
    public static float SafeFramesTime = 0.083f;
    
    /// <summary>
    /// Camera Follow Player: When enabled, camera follows the singing character.
    /// Switches between player/opponent based on upcoming notes (FNF-style).
    /// Uses actual object positions from ObjectPositionSet/ObjectTransformSet events.
    /// Does NOT interfere with CameraSet events.
    /// </summary>
    public static bool CameraFollowPlayer = false;

    // ── Per-instance state ───────────────────────────────────────────────────
    private readonly int              _strumlineIndex;
    private bool                      _isPlayer;  // Whether this strumline is controlled by local keyboard
    private float                     _xOffset;   // mutable — can be repositioned dynamically
    private readonly GraphicsDevice   _gd;
    private readonly float            _scrollSpeed;
    private readonly FnmmLevel        _level;     // kept for action_pack analysis
    private NoteStyle?                _style;
    private SpriteFont?               _font;  // set each Draw call, used for note symbols
    private ArrowEffectManager?       _arrowEffects;  // Random visual effects for arrows
    private float                     _currentSongTime = 0f;  // Current song time for effects
    private readonly List<FnmmNote>[] _notes;
    private readonly List<FnmmNote>[] _notesBackup;  // original notes for seek restore
    private readonly float[]          _glowTimer   = new float[LANE_COUNT];
    private readonly float[]          _holdEndTime = new float[LANE_COUNT];
    
    // ── FNF-style receptor intro animation (countdown pop-in) ────────────────
    private readonly float[]          _receptorIntroTime = new float[LANE_COUNT];  // Time since receptor appeared
    private readonly bool[]           _receptorIntroActive = new bool[LANE_COUNT]; // Whether intro animation is active
    private const float RECEPTOR_INTRO_DURATION = 0.25f;  // 250ms pop-in animation (like FNF)
    private readonly int[]            _recState    = new int[LANE_COUNT];
    private readonly float[]          _recAnimTime = new float[LANE_COUNT];
    
#if ANDROID
    // Touch controls for Android (optional)
    private TouchControls?            _touchControls;
#endif

    // ── Public API ────────────────────────────────────────────────────────────
    
    /// <summary>
    /// Helper to check if a key is down (combines keyboard + touch controls)
    /// </summary>
    private bool IsKeyDown(KeyboardState kb, Keys key)
    {
#if ANDROID
        return kb.IsKeyDown(key) || (_touchControls?.IsKeyDown(key) ?? false);
#else
        return kb.IsKeyDown(key);
#endif
    }
    
    /// <summary>
    /// Helper to check if a key was just pressed THIS frame
    /// On Android, uses tap detection from TouchControls instead of frame-to-frame comparison
    /// </summary>
    private bool IsKeyJustPressed(KeyboardState kb, KeyboardState prevKb, Keys key)
    {
#if ANDROID
        // On Android, use TouchControls tap detection which is more reliable for touch
        bool touchJustPressed = _touchControls?.IsKeyJustPressed(key) ?? false;
        if (touchJustPressed) return true;
        
        // Also check keyboard for testing
        return kb.IsKeyDown(key) && !prevKb.IsKeyDown(key);
#else
        return kb.IsKeyDown(key) && !prevKb.IsKeyDown(key);
#endif
    }
    
#if ANDROID
    /// <summary>
    /// Set the touch controls for Android input detection
    /// </summary>
    public void SetTouchControls(TouchControls? touchControls)
    {
        _touchControls = touchControls;
    }
#endif
    
    /// <summary>
    /// Starts the FNF-style receptor intro animation (pop-in effect).
    /// Call this when the countdown starts (3, 2, 1, GO!) to show receptors appearing.
    /// Each receptor pops in with a stagger delay based on its lane.
    /// </summary>
    /// <param name="startDelay">Initial delay in seconds before first receptor appears (default: 0.5s)</param>
    /// <param name="staggerDelay">Delay in seconds between each receptor (default: 0.05s)</param>
    public void StartReceptorIntro(float startDelay = 0.5f, float staggerDelay = 0.05f)
    {
        for (int i = 0; i < LANE_COUNT; i++)
        {
            _receptorIntroActive[i] = true;
            _receptorIntroTime[i] = -(startDelay + i * staggerDelay);  // Negative time = waiting to start
        }
    }
    
    /// <summary>
    /// Check if a hold note is currently active in the specified lane
    /// </summary>
    public bool IsHoldActive(int lane)
    {
        if (lane < 0 || lane >= LANE_COUNT) return false;
        return _holdEndTime[lane] > 0f;
    }
    private readonly float[]          _confirmHoldTimer = Enumerable.Repeat(-1f, LANE_COUNT).ToArray();  // FNF-style: -1 = inactive, >= 0 = counting (150ms)
    
    // ── Combo bounce animation (FNF-style) ────────────────────────────────────
    public float ComboScale { get; private set; } = 1.0f;  // Current combo scale for bounce effect
    private const float COMBO_BOUNCE_SCALE = 1.3f;         // Target scale when combo increases (30% larger)
    private const float COMBO_BOUNCE_SPEED = 10f;          // How fast the scale returns to 1.0
    private const float CONFIRM_HOLD_TIME = 0.15f;  // 150ms like FNF official
    private readonly bool[]           _keyHeld     = new bool[LANE_COUNT];  // tracks if key is currently held
    private readonly float[]          _splashTimer = new float[LANE_COUNT];  // note splash effect timer
    private readonly float[]          _comboBreakTimer = new float[LANE_COUNT];  // combo break effect timer
    private readonly Texture2D        _pixel;
    private NoteSplashStyle?          _splashStyle;  // null = use programmatic default
    
    // Strategy Pattern: Delegate rendering to engine-specific implementations
    private INoteStyleRenderer?       _renderer;  // Current active renderer
    private FmmNoteSystemRenderer?    _fmm;       // Same object as _renderer for FMM-system styles

    // ── Hold note completion tracking (player only) ───────────────────────────
    // Stores the initial hit judgement distance for each active hold lane.
    // -1 = no active hold being tracked.
    private readonly float[] _holdHitDist  = new float[LANE_COUNT];  // initial hit dist
    private readonly float[] _holdStart    = new float[LANE_COUNT];  // song time when hold started
    private readonly float[] _holdDuration = new float[LANE_COUNT];  // total hold duration

    // Bonus score for completing a hold (awarded on release at the end)
    private const int HOLD_BONUS_SICK  = 250;
    private const int HOLD_BONUS_GOOD  = 150;
    private const int HOLD_BONUS_BAD   =  75;
    private const int HOLD_BONUS_SHIT  =  25;
    // Fraction of hold that must be completed to not count as a miss (0.25 = 25%)
    private const float HOLD_MIN_COMPLETION = 0.25f;
    // Within this many seconds of the end, releasing counts as "completed"
    private const float HOLD_END_LENIENCY = 0.10f;

    /// <summary>
    /// When true, receptors and notes are hidden when no note is active.
    /// Used for opponent strumlines — they only flash when singing.
    /// </summary>
    public bool HideWhenIdle = false;

    /// <summary>Alpha multiplier for drawing (0=invisible, 1=fully visible).</summary>
    public float Alpha = 1.0f;

    /// <summary>Scale multiplier for notes and receptors (1=normal, 0.6=smaller).</summary>
    public float NoteScale = 1.0f;
    
    /// <summary>
    /// When true, this strumline will NOT auto-hit notes even if Autoplay is enabled.
    /// Used for multiplayer opponent strumline — hits are triggered manually via network packets.
    /// </summary>
    public bool DisableAutoplay = false;

    /// <summary>Override the X position of this strumline (screen pixels, unscaled).</summary>
    public void SetXOffset(float x) { _xOffset = x; _xOffsetSplit = -1f; }

    // ── Midlescroll split (opponent only) ─────────────────────────────────────
    // When set >= 0, lanes 0-1 use _xOffset and lanes 2-3 use _xOffsetSplit.
    // This places left/down on the left side and up/right on the right side of center.
    private float _xOffsetSplit = -1f;

    /// <summary>
    /// Sets a split X layout: lanes 0-1 at <paramref name="xLeft"/>, lanes 2-3 at <paramref name="xRight"/>.
    /// Used for the opponent strumline in midlescroll mode.
    /// </summary>
    public void SetSplitXOffset(float xLeft, float xRight) { _xOffset = xLeft; _xOffsetSplit = xRight; }

    // Tracks whether any lane is currently active (note hit or hold)
    private bool _anyLaneActive = false;
    
    // Song start time for safe frames calculation
    private float _songStartTime = -1f;

    // Fixed render target dimensions — set once, never change with window resize
    private int _rtWidth  = 1280;
    private int _rtHeight = 720;

    /// <summary>
    /// Must be called after SetupLevel with the render target dimensions.
    /// This decouples NoteRenderer from the GPU viewport so resizing the window
    /// never stretches notes.
    /// </summary>
    public void SetRenderTargetSize(int w, int h) { _rtWidth = w; _rtHeight = h; }

    /// <summary>
    /// Discards all notes before targetTime without firing miss/hit callbacks.
    /// Used for timeskip so the chart stays in sync with the new song position.
    /// </summary>
    public void SeekTo(float targetTime)
    {
        for (int lane = 0; lane < LANE_COUNT; lane++)
        {
            // Restore all notes from backup, then remove those already past targetTime
            _notes[lane].Clear();
            foreach (var n in _notesBackup[lane])
                _notes[lane].Add(n);
            // Remove notes whose window has fully passed at targetTime
            _notes[lane].RemoveAll(n => n.Start + n.Duration < targetTime - HIT_WINDOW);
        }
        // Clear any active hold state
        for (int i = 0; i < LANE_COUNT; i++)
            _holdEndTime[i] = -1f;
        
        // Reset song start time for safe frames recalculation
        _songStartTime = targetTime > 0f ? targetTime : -1f;
    }

    private float ScreenH => _rtHeight;
    private float ScreenW => _rtWidth;
    private float StrumY  => _customStrumY >= 0 ? _customStrumY : ScreenH * (Downscroll ? STRUM_Y_DOWN : STRUM_Y_UP);
    // Scale factor relative to base 1280×720
    private float ScaleX  => ScreenW / 1280f;
    private float ScaleY  => ScreenH / 720f;
    // Effective scroll speed including visual multiplier and screen scale
    private float EffSpeed => _scrollSpeed * ScrollMultiplier * ScaleY;

    private float _customStrumY = -1f;  // -1 = use default

    /// <summary>Override the Y position of the receptor (screen pixels).</summary>
    public void SetStrumY(float y) => _customStrumY = y;

    /// <summary>Which HUD-editor strumline offset applies: 0 = player, 1 = opponent, -1 = none.</summary>
    public int HudRole { get; set; } = -1;

    /// <summary>HUD editor offset for this strumline, in render-target pixels.</summary>
    private Vector2 HudOffsetPx
    {
        get
        {
            var hud = HudSettings.Instance;
            var o = HudRole == 0 ? hud.PlayerStrumlineOffset : HudRole == 1 ? hud.OpponentStrumlineOffset : null;
            return o == null ? Vector2.Zero : new Vector2(o.X * ScaleX, o.Y * ScaleY);
        }
    }

    public int Score  { get; private set; }
    public int Misses { get; private set; }
    public int Combo  { get; private set; }
    
    // ── Rating counters (for accurate accuracy calculation) ───────────────
    public int Sicks { get; private set; }
    public int Goods { get; private set; }
    public int Bads  { get; private set; }
    public int Shits { get; private set; }

    public Action<int, float>? OnNoteHit;
    public Action?             OnNoteMiss;
    /// <summary>Lane of the miss being reported, valid while OnNoteMiss runs.</summary>
    public int                 LastMissLane { get; private set; } = -1;
    /// <summary>Fired when player presses arrow key (for multiplayer animation sync). Args: lane index.</summary>
    public Action<int>?        OnKeyPress;
    /// <summary>Fired with rating string ("Sick!", "Good", "Bad", "Shit") on manual hit.</summary>
    public Action<string>?     OnJudgement;
    /// <summary>Fired when combo changes. Args: new combo value.</summary>
    public Action<int>?        OnComboChanged;
    /// <summary>Fired when a note with onPressEvent is hit. Args: event ID.</summary>
    public Action<int>?        OnNotePressEvent;
    /// <summary>Fired when a note with onMissEvent is missed. Args: event ID.</summary>
    public Action<int>?        OnNoteMissEvent;
    // Note: there are no skin-based effect callbacks. Skins are visual only;
    // damage/instakill/heal come from the note's action packs (see NoteSkins).

    public NoteRenderer(FnmmLevel level, int chartIndex,
                        float xOffset, GraphicsDevice gd, NoteStyle? style = null)
    {
        // chartIndex = which chart from the level to load (0=enemy, 1=player)
        // _isPlayer = whether THIS strumline is controlled by local keyboard
        // In single-player: strumline 0 = player (chart 1), strumline 1 = opponent (chart 0)
        // In multiplayer: strumline 0 = player (MY chart), strumline 1 = opponent (THEIR chart)
        _strumlineIndex = chartIndex;  // Store for note loading
        _isPlayer       = false;  // Will be set correctly by SetIsPlayer() after construction
        _xOffset        = xOffset;
        _gd             = gd;
        _level          = level;

        float bpm   = level.Data.EffectiveBpm;
        float speed = level.Data.Speed > 0 ? level.Data.Speed : 1f;
        _scrollSpeed = BASE_SPEED * (bpm / 120f) * speed;

        // Initialize arrow effects manager with BPM
        _arrowEffects = new ArrowEffectManager(bpm);

        _pixel = new Texture2D(gd, 1, 1);
        _pixel.SetData(new[] { Color.White });

        for (int i = 0; i < LANE_COUNT; i++)
        {
            _holdEndTime[i]  = -1f;
            _holdHitDist[i]  = -1f;
            _holdStart[i]    = -1f;
            _holdDuration[i] = -1f;
        }

        var sl = level.Data.Strumlines;
        _notes       = new List<FnmmNote>[LANE_COUNT];
        _notesBackup = new List<FnmmNote>[LANE_COUNT];
        for (int i = 0; i < LANE_COUNT; i++)
        {
            _notes[i]       = new List<FnmmNote>();
            _notesBackup[i] = new List<FnmmNote>();
            if (chartIndex < sl.Count && i < sl[chartIndex].Count)
            {
                foreach (var note in sl[chartIndex][i])
                {
                    // Convert beat times to seconds if needed
                    var converted = new FnmmNote
                    {
                        Start    = level.Data.ToSeconds(note.Start),
                        Duration = level.Data.ToSeconds(note.Duration),
                        Props    = note.Props,
                    };
                    _notes[i].Add(converted);
                    _notesBackup[i].Add(converted);  // keep original copy
                }
            }
        }
        
        // Set style and create appropriate renderer
        SetStyle(style);
    }
    
    /// <summary>
    /// Sets whether this strumline is controlled by the local player.
    /// Must be called after construction to properly configure input handling.
    /// </summary>
    public void SetIsPlayer(bool isPlayer)
    {
        _isPlayer = isPlayer;
    }

    public void SetStyle(NoteStyle? style)
    {
        _style = style;
        
        Console.WriteLine($"[NoteRenderer] ═══════════════════════════════════════════");
        Console.WriteLine($"[NoteRenderer] SetStyle called");
        
        _fmm = null;
        if (style == null)
        {
            _renderer = null;
            Console.WriteLine($"[NoteRenderer] Style is null, no renderer");
            Console.WriteLine($"[NoteRenderer] ═══════════════════════════════════════════");
            return;
        }
        
        Console.WriteLine($"[NoteRenderer] IsCodenameEngine flag: {style.IsCodenameEngine}");
        Console.WriteLine($"[NoteRenderer] HoldTexture exists: {style.HoldTexture != null}");
        
        // Choose renderer based on note system / engine type
        if (style.NoteSystem == NoteSystemKind.Fmm && style.Fmm != null)
        {
            _fmm = new FmmNoteSystemRenderer(style);
            _renderer = _fmm;
            Console.WriteLine($"[NoteRenderer] Using FMM-faithful note system");
        }
        else if (style.IsCodenameEngine)
        {
            _renderer = new CodenameNoteStyleRenderer(style, Downscroll);
            Console.WriteLine($"[NoteRenderer] ✓✓✓ USING CODENAME ENGINE RENDERER ✓✓✓");
        }
        else
        {
            _renderer = new FMMNoteStyleRenderer(style, Downscroll);
            Console.WriteLine($"[NoteRenderer] Using FunkyMaker native renderer");
        }
        
        Console.WriteLine($"[NoteRenderer] Active renderer: {_renderer?.GetEngineName()}");
        Console.WriteLine($"[NoteRenderer] ═══════════════════════════════════════════");
    }
    public void SetSplashStyle(NoteSplashStyle? splashStyle) => _splashStyle = splashStyle;
    
    private ParticleNoteSplash? _particleSplash;
    private int _pendingSplashLane = -1;  // -1 = no pending splash
    private int _drawHoldDebugCounter = 0;  // Debug: count DrawHold calls
    
    public void SetParticleSplash(ParticleNoteSplash? particleSplash) => _particleSplash = particleSplash;

    /// <summary>
    /// Manually triggers a hit animation on a specific lane.
    /// Used for multiplayer to animate opponent's hits received over network.
    /// </summary>
    /// <param name="lane">Lane index (0-3)</param>
    /// <param name="isSick">True to trigger splash effect (Sick! rating)</param>
    public void TriggerManualHit(int lane, bool isSick)
    {
        if (lane < 0 || lane >= LANE_COUNT)
            return;
            
        // Trigger glow effect on receptor
        _glowTimer[lane] = 0.15f;
        SetRecState(lane, 2);  // Confirm state (pressed)
        _fmm?.OnHit(lane);
        
        // Trigger splash effect for Sick! hits (only default if no particles)
        if (isSick && _particleSplash == null)
            _splashTimer[lane] = SplashLength(lane);
        
        // Note: Particle splash for multiplayer opponent hits would need separate implementation
        // since we can't trigger _pendingSplashLane from here (no Draw() call for opponent)
    }

    // ── Update ───────────────────────────────────────────────────────────────
    public void Update(float songTime, float dt, KeyboardState kb, KeyboardState prevKb)
    {
        // Initialize song start time on first update with positive songTime
        if (_songStartTime < 0f && songTime > 0f)
            _songStartTime = songTime;
        
        // Update arrow effects (synchronized with BPM)
        if (_arrowEffects != null)
        {
            float songBeats = songTime / 60f * _level.Data.EffectiveBpm;
            _arrowEffects.Update(dt, songBeats);
        }
        
        // Update combo bounce animation (lerp scale back to 1.0)
        if (ComboScale > 1.0f)
        {
            ComboScale = MathHelper.Lerp(ComboScale, 1.0f, COMBO_BOUNCE_SPEED * dt);
            if (ComboScale <= 1.01f)
                ComboScale = 1.0f;
        }
            
        for (int i = 0; i < LANE_COUNT; i++)
        {
            _glowTimer[i]   = Math.Max(0f, _glowTimer[i] - dt);
            _recAnimTime[i] += dt;
            _splashTimer[i] = Math.Max(0f, _splashTimer[i] - dt);
            _comboBreakTimer[i] = Math.Max(0f, _comboBreakTimer[i] - dt);
            
            // Update receptor intro animation
            if (_receptorIntroActive[i])
            {
                _receptorIntroTime[i] += dt;
                if (_receptorIntroTime[i] >= RECEPTOR_INTRO_DURATION)
                {
                    _receptorIntroActive[i] = false;  // Animation complete
                }
            }
            
            // Auto-return to idle after ghosttap press animation expires
            if (_glowTimer[i] <= 0f && _recState[i] == 1)  // State 1 = press
            {
                SetRecState(i, 0);  // Return to idle
            }
            
            // Update confirm hold timer (FNF-style)
            if (_confirmHoldTimer[i] >= 0)
            {
                _confirmHoldTimer[i] += dt;
                if (_confirmHoldTimer[i] >= CONFIRM_HOLD_TIME)
                {
                    _confirmHoldTimer[i] = -1;  // Reset
                    // Transition back to static (unless player is still holding key)
                    if (_isPlayer && _keyHeld[i])
                        SetRecState(i, 1);  // Go to press if key still held
                    else
                        SetRecState(i, 0);  // Go to static
                }
            }
        }

        if (Autoplay && !DisableAutoplay)  UpdateAutoplay(songTime);
        else if (_isPlayer)                 UpdatePlayer(songTime, kb, prevKb);
        else if (!DisableAutoplay)          UpdateAutoplay(songTime);
        // If DisableAutoplay is true for opponent, clean up old notes that weren't hit
        else                                UpdateOpponentMultiplayer(songTime);

        if (_fmm != null)
        {
            // Each step of an active hold re-hits the strum, like FMM's per-piece sustain hits
            float stepSeconds = 15f / Math.Max(1f, _level.Data.EffectiveBpm);
            for (int lane = 0; lane < LANE_COUNT; lane++)
                if (_holdEndTime[lane] > 0f && songTime < _holdEndTime[lane])
                    _fmm.HoldTick(lane, songTime, stepSeconds);
            _fmm.Update(dt);
        }
    }

    private void UpdateAutoplay(float songTime)
    {
        _anyLaneActive = false;
        for (int lane = 0; lane < LANE_COUNT; lane++)
        {
            for (int i = _notes[lane].Count - 1; i >= 0; i--)
            {
                var note = _notes[lane][i];
                if (songTime < note.Start) continue;

                // Autoplay: skins are visual only. Dodge a note only when its action packs
                // make letting it pass better than pressing it (NoteSkins.ShouldAvoid);
                // notes without events are always pressed.
                bool isDodge = _isPlayer && NoteSkins.ShouldAvoid(_level.Events, note.Props);
                if (isDodge)
                {
                    if (songTime - note.Start > HIT_WINDOW)
                        _notes[lane].RemoveAt(i);
                    continue;
                }

                // Normal/heal: press it
                _notes[lane].RemoveAt(i);
                Score += 350; 
                Combo++;
                ComboScale = COMBO_BOUNCE_SCALE;  // Trigger bounce animation
                OnComboChanged?.Invoke(Combo);  // Fire combo changed event
                float holdEnd = note.Duration > 0 ? note.Start + note.Duration : -1f;
                OnNoteHit?.Invoke(lane, holdEnd);
                // Botplay hits are perfect, so the player's strumline shows a splash (as in FNF)
                if (_isPlayer)
                    TriggerSplash(lane);
                if (note.Props.OnPressEvent >= 0)
                    OnNotePressEvent?.Invoke((int)note.Props.OnPressEvent);
                if (note.Duration > 0)
                    _holdEndTime[lane] = Math.Max(_holdEndTime[lane], note.Start + note.Duration);
                else
                    _glowTimer[lane] = 0.12f;
                SetRecState(lane, 2);
                _fmm?.OnHit(lane);
                _anyLaneActive = true;
            }

            if (_holdEndTime[lane] > 0f)
            {
                if (songTime < _holdEndTime[lane])
                { _glowTimer[lane] = 0.12f; SetRecState(lane, 2); _anyLaneActive = true; }
                else
                { _holdEndTime[lane] = -1f; SetRecState(lane, 0); }
            }
            else if (_glowTimer[lane] > 0f)
                _anyLaneActive = true;
            else if (_glowTimer[lane] <= 0f)
                SetRecState(lane, 0);
        }
    }

    private void UpdatePlayer(float songTime, KeyboardState kb, KeyboardState prevKb)
    {
        for (int lane = 0; lane < LANE_COUNT; lane++)
        {
            // Check both keyboard and touch controls (Android)
            bool held = IsKeyDown(kb, PlayerKeys[lane]);
            bool heldPrev = IsKeyDown(prevKb, PlayerKeys[lane]);
            bool justPressed = IsKeyJustPressed(kb, prevKb, PlayerKeys[lane]);  // Use new tap detection
            bool justReleased = !held && heldPrev;
            
            _keyHeld[lane] = held;  // Track key state for receptor rendering

            // ── Active hold tracking ──────────────────────────────────────────
            if (_holdEndTime[lane] > 0f)
            {
                float holdEnd  = _holdEndTime[lane];
                float duration = _holdDuration[lane];
                float elapsed  = songTime - _holdStart[lane];
                float progress = duration > 0f ? Math.Clamp(elapsed / duration, 0f, 1f) : 1f;

                bool holdNaturallyEnded = songTime >= holdEnd || (!held && songTime >= holdEnd - HOLD_END_LENIENCY);
                bool releasedEarly      = !held && !holdNaturallyEnded;

                if (held || holdNaturallyEnded)
                {
                    // Still holding or hold just finished naturally
                    _glowTimer[lane] = 0.12f;
                    SetRecState(lane, 2);

                    if (holdNaturallyEnded)
                    {
                        // Completed! Award bonus based on initial hit quality
                        int bonus = HoldBonus(_holdHitDist[lane]);
                        Score += bonus;
                        // Fire Sick judgement for completing the hold
                        Sicks++;  // Count completed hold as Sick!
                        OnJudgement?.Invoke("Sick!");
                        _holdEndTime[lane]  = -1f;
                        _holdHitDist[lane]  = -1f;
                        _holdStart[lane]    = -1f;
                        _holdDuration[lane] = -1f;
                        _glowTimer[lane]    = 0.20f;
                        SetRecState(lane, 2);
                    }
                }
                else if (releasedEarly)
                {
                    // Released before the end
                    if (progress < HOLD_MIN_COMPLETION)
                    {
                        // Barely held — counts as a miss
                        Misses++; Combo = 0;
                        Shits++;  // Count failed hold as Shit
                        _comboBreakTimer[lane] = 0.5f;  // Combo break effect
                        _style?.PlayMissSound();
                        LastMissLane = lane; OnNoteMiss?.Invoke();
                        OnJudgement?.Invoke("Shit");
                    }
                    else
                    {
                        // Partial hold — downgrade judgement one level, no bonus
                        string degraded = DegradeJudgement(GetJudgement(_holdHitDist[lane]));
                        
                        // Increment counter for degraded judgement
                        switch (degraded)
                        {
                            case "Sick!": Sicks++; break;
                            case "Good":  Goods++; break;
                            case "Bad":   Bads++;  break;
                            case "Shit":  Shits++; break;
                        }
                        
                        OnJudgement?.Invoke(degraded);
                        // Keep score already awarded on hit, just no bonus
                    }
                    _holdEndTime[lane]  = -1f;
                    _holdHitDist[lane]  = -1f;
                    _holdStart[lane]    = -1f;
                    _holdDuration[lane] = -1f;
                    SetRecState(lane, 0);
                }
                // Don't process justPressed while a hold is active on this lane
                continue;
            }

            if (justReleased) SetRecState(lane, 0);

            // Key held with no active hold → show pressed state
            // This keeps the receptor in "press" state while holding the key down
            if (held && _recState[lane] != 2)  // Don't override confirm state during its animation
                SetRecState(lane, 1);

            if (!justPressed) continue;
            
            // Fire key press callback for multiplayer animation sync
            // This happens BEFORE hit detection so opponent sees animation even on misses
            OnKeyPress?.Invoke(lane);
            _fmm?.OnPress(lane);

            // ── Find best note in hit window ──────────────────────────────────
            FnmmNote? best     = null;
            float     bestDist = float.MaxValue;
            
            // Input buffer: allow hitting notes slightly early
            foreach (var note in _notes[lane])
            {
                float dist = Math.Abs(songTime - note.Start);
                float timeDiff = note.Start - songTime;
                
                // Check if note is within hit window OR within input buffer (early hit)
                bool inWindow = dist <= HIT_WINDOW || (timeDiff > 0 && timeDiff <= InputBuffer);
                
                if (inWindow && dist < bestDist)
                { best = note; bestDist = dist; }
            }

            if (best != null)
            {
                _notes[lane].Remove(best);

                // Press the note and fire onPressEvent — level handles effects via action_packs.
                // Fallback: if no onPressEvent, apply default mechanic based on skin type.
                int scoreGain = RateScore(bestDist);
                Score += scoreGain;
                Combo++;
                ComboScale = COMBO_BOUNCE_SCALE;  // Trigger bounce animation
                OnComboChanged?.Invoke(Combo);  // Fire combo changed event
                
                // Increment rating counters based on judgement
                string judgement = GetJudgement(bestDist);
                switch (judgement)
                {
                    case "Sick!": Sicks++; break;
                    case "Good":  Goods++; break;
                    case "Bad":   Bads++;  break;
                    case "Shit":  Shits++; break;
                }
                
                // Splash only on a Sick! (350 points)
                if (scoreGain >= 350)
                    TriggerSplash(lane);
                
                float holdEnd = best.Duration > 0 ? best.Start + best.Duration : -1f;
                OnNoteHit?.Invoke(lane, holdEnd);
                OnJudgement?.Invoke(judgement);

                // The level's action pack defines any effect. Skins are visual only:
                // there is no hardcoded damage/instakill/heal fallback.
                if (best.Props.OnPressEvent >= 0)
                    OnNotePressEvent?.Invoke((int)best.Props.OnPressEvent);

                if (best.Duration > 0)
                {
                    _holdEndTime[lane]  = holdEnd;
                    _holdHitDist[lane]  = bestDist;
                    _holdStart[lane]    = songTime;
                    _holdDuration[lane] = best.Duration;
                }
                else
                    _glowTimer[lane] = RecAnimDuration(lane, 2, 0.15f);

                SetRecState(lane, 2);
                _fmm?.OnHit(lane);
            }
            else
            {
                // Ghost tap — no note nearby
                // If GhostTapping is disabled, this breaks combo and counts as miss
                if (!GhostTapping)
                {
                    Misses++;
                    Combo = 0;
                    _comboBreakTimer[lane] = 0.5f;  // Combo break effect lasts 500ms
                    _style?.PlayMissSound();
                    LastMissLane = lane; OnNoteMiss?.Invoke();
                    OnJudgement?.Invoke("Miss");
                }
                
                // Visual flash only (brief press animation)
                // Duration matches the press animation length from the style XML
                _glowTimer[lane] = RecAnimDuration(lane, 1, 0.08f);
                SetRecState(lane, 1);
            }
        }

        // ── Miss detection — notes that passed the hit window ─────────────────
        // Safe frames: don't count misses during the first few frames of the song
        bool inSafeFrames = _songStartTime >= 0f && (songTime - _songStartTime) < SafeFramesTime;
        
        for (int lane = 0; lane < LANE_COUNT; lane++)
        {
            for (int i = _notes[lane].Count - 1; i >= 0; i--)
            {
                var note = _notes[lane][i];
                if (songTime - note.Start <= HIT_WINDOW) continue;

                _notes[lane].RemoveAt(i);
                
                // Safe frames: skip miss penalty during song start
                if (inSafeFrames) continue;
                
                // Avoid-notes (pressing hurts or missing heals) and notes that heal when
                // pressed are NOT misses: no combo break, no miss penalty.
                // Their onMissEvent still fires.
                if (!NoteSkins.IsMissFree(_level.Events, note.Props))
                {
                    Misses++; Combo = 0;
                    _comboBreakTimer[lane] = 0.5f;  // Combo break effect
                    _style?.PlayMissSound();
                    LastMissLane = lane; OnNoteMiss?.Invoke();
                }
                if (note.Props.OnMissEvent >= 0)
                    OnNoteMissEvent?.Invoke((int)note.Props.OnMissEvent);
            }
        }
    }

    /// <summary>
    /// Update logic for opponent strumline in multiplayer mode.
    /// Cleans up notes that have passed without being hit (no autoplay, hits come from network).
    /// </summary>
    private void UpdateOpponentMultiplayer(float songTime)
    {
        // Simply remove notes that are far past the hit window
        // No scoring, no callbacks — hits are handled by TriggerManualHit from network packets
        for (int lane = 0; lane < LANE_COUNT; lane++)
        {
            for (int i = _notes[lane].Count - 1; i >= 0; i--)
            {
                var note = _notes[lane][i];
                // Remove notes that are more than 2 seconds past (generous window for network lag)
                if (songTime - note.Start > HIT_WINDOW * 10f)
                {
                    _notes[lane].RemoveAt(i);
                }
            }
        }
    }

    // ── Hold helpers ──────────────────────────────────────────────────────────

    /// <summary>Bonus score for completing a hold, based on initial hit quality.</summary>
    private static int HoldBonus(float hitDist)
    {
        if (hitDist < W_SICK) return HOLD_BONUS_SICK;
        if (hitDist < W_GOOD) return HOLD_BONUS_GOOD;
        if (hitDist < W_BAD)  return HOLD_BONUS_BAD;
        return HOLD_BONUS_SHIT;
    }

    /// <summary>Downgrades a judgement string by one level.</summary>
    private static string DegradeJudgement(string j) => j switch
    {
        "Sick!" => "Good",
        "Good"  => "Bad",
        "Bad"   => "Shit",
        _       => "Shit",
    };

    private void SetRecState(int lane, int state)
    {
        if (_recState[lane] != state) { _recState[lane] = state; _recAnimTime[lane] = 0f; }
    }

    /// <summary>
    /// Returns the total duration in seconds of a receptor animation state for a given lane.
    /// Falls back to the provided default if no style frames are available.
    /// </summary>
    private float RecAnimDuration(int lane, int state, float fallback)
    {
        if (_style == null) return fallback;
        var frames = _style.ReceptorFrames[lane, state]
                  ?? _style.ReceptorFrames[lane, Math.Min(state, 2)]
                  ?? _style.ReceptorFrames[lane, 0];
        if (frames == null || frames.Count == 0) return fallback;
        float fps = _style.StateFps[Math.Min(state, _style.StateFps.Length - 1)];
        return frames.Count / Math.Max(1f, fps);
    }

    // ── Draw ─────────────────────────────────────────────────────────────────
    public void Draw(SpriteBatch sb, float songTime, SpriteFont? font)
    {
        _font = font;
        _currentSongTime = songTime; // Store for yellow arrow detection & effects

        // Master HUD / Strumline visibility and per-strumline visibility checks
        if (!HudSettings.Instance.MasterHudVisible ||
            !ShowStrumlines ||
            (_isPlayer && HidePlayerStrumline) ||
            Alpha <= 0.01f)
        {
            _pendingSplashLane = -1;
            return;
        }

        // HideWhenIdle: opponent strumlines only show when a note is active
        if (HideWhenIdle && !_anyLaneActive)
        {
            _pendingSplashLane = -1;
            return;
        }

        float sh     = ScreenH;
        float sx     = ScaleX * NoteScale;
        Vector2 hudOffset = HudOffsetPx;
        float strumY = StrumY + hudOffset.Y;
        float spd    = EffSpeed;
        float laneW  = LANE_WIDTH * sx;
        float alpha  = Alpha;
        if (_fmm != null) _fmm.ShadowOffsetY = 10f * ScaleY;  // hud.hx dropShadow: 10 px at 720p


        for (int lane = 0; lane < LANE_COUNT; lane++)
        {
            // Calculate lane position
            // _xOffset is in unscaled pixels, so we need to scale it
            // Then add lane spacing (which is already scaled via laneW)
            float baseX = _xOffset * sx;

            // Midlescroll split: lanes 0-1 use _xOffset, lanes 2-3 use _xOffsetSplit
            if (_xOffsetSplit >= 0f && lane >= 2)
            {
                baseX = _xOffsetSplit * sx;
                baseX += (lane - 2) * laneW;  // Lanes 2-3 count from 0
            }
            else
            {
                baseX += lane * laneW;  // Normal spacing
            }
            baseX += hudOffset.X;
            
            float cx = baseX + laneW / 2f;  // Center X of the lane

            // Active hold tail (while pressing hold note)
            // Consumed at exact 1:1 chart scroll speed (spd) from the receptor (strumY)
            if (_holdEndTime[lane] > 0f && songTime < _holdEndTime[lane])
            {
                float remainingTime = _holdEndTime[lane] - songTime;
                float holdPx = remainingTime * spd;
                DrawActiveHold(sb, lane, baseX, cx, strumY, holdPx, laneW);
            }

            DrawReceptor(sb, lane, cx, strumY, sx, alpha, songTime);

            foreach (var note in _notes[lane])
            {
                float timeDiff = note.Start - songTime;

                float noteY = !Downscroll
                    ? strumY + timeDiff * spd
                    : strumY - timeDiff * spd;

                // ── FNF-style aggressive culling for performance ──────────────
                // Only render notes that are reasonably close to the viewport
                // FNF uses a larger margin (2x screen height) for smooth appearance
                if (noteY < -sh * 0.5f || noteY > sh * 1.5f) continue;

                // Avoid-notes and marked (X/O) notes stay drawn past the receptor so the
                // player can see them scroll by.
                bool isDangerous = _isPlayer &&
                    (note.Props.VisualSkin != NoteSkins.Default || NoteSkins.ShouldAvoid(_level.Events, note.Props));

                if (!isDangerous)
                {
                    // Hide notes that have passed the receptor beyond the hit window
                    // (they're already a miss). Notes within the window stay visible
                    // so the player can still hit them.
                    float pastReceptor = songTime - note.Start;
                    if (!Downscroll && noteY < strumY && pastReceptor > HIT_WINDOW) continue;
                    if (Downscroll  && noteY > strumY && pastReceptor > HIT_WINDOW) continue;
                }

                if (note.Duration > 0)
                {
                    DrawHold(sb, lane, baseX, cx, noteY, note.Duration * spd, strumY, laneW, alpha, sx);
                }

                DrawNoteHead(sb, lane, cx, noteY, note, sx, alpha, strumY);
            }
        }

        // ── Trigger particle splash if pending (after we have strumY position) ──
        if (_pendingSplashLane >= 0 && _particleSplash != null)
        {
            int lane = _pendingSplashLane;
            
            // Calculate splash position (center of receptor) - same logic as main loop
            float baseX = _xOffset * sx;
            if (_xOffsetSplit >= 0f && lane >= 2)
            {
                baseX = _xOffsetSplit * sx;
                baseX += (lane - 2) * laneW;
            }
            else
            {
                baseX += lane * laneW;
            }
            baseX += hudOffset.X;
            float cx = baseX + laneW / 2f;
            
            _particleSplash.Trigger(lane, new Vector2(cx, strumY));
            _pendingSplashLane = -1;  // Reset
        }

        if (ShowKeyLabels && !Autoplay && _isPlayer && font != null && Alpha > 0.01f)
        {
            for (int lane = 0; lane < LANE_COUNT; lane++)
            {
                // Calculate position - same logic as main loop
                float baseX = _xOffset * sx + lane * laneW + hudOffset.X;
                float lx = baseX + laneW / 2f;
                string label = PlayerKeys[lane].ToString();
                var sz = font.MeasureString(label);
                sb.DrawString(font, label,
                    new Vector2(lx - sz.X / 2f, strumY - sz.Y / 2f),
                    Color.White * 0.5f * alpha);  // Use alpha parameter (respects HUD visibility fade)
            }
        }
    }

    // ── Active hold (note consumed, tail visible while holding) ───────────────
    private void DrawActiveHold(SpriteBatch sb, int lane, float laneX, float cx,
                                 float strumY, float holdPx, float laneW)
    {
        // Delegate to the appropriate renderer based on notestyle type
        if (_renderer != null)
        {
            _renderer.DrawActiveHold(sb, lane, cx, strumY, holdPx, laneW, Alpha);
            return;
        }

        float bodyTop = !Downscroll ? strumY : strumY - holdPx;
        float bodyBottom = !Downscroll ? strumY + holdPx : strumY;
        DrawHoldBody(sb, lane, laneX, cx, bodyTop, bodyBottom, LaneColors[lane], true, laneW, Alpha);
    }

    // ── Upcoming hold note ────────────────────────────────────────────────────
    private void DrawHold(SpriteBatch sb, int lane, float laneX, float cx,
                          float noteY, float holdPx, float strumY, float laneW, float alpha, float sx)
    {
        // Delegate to the appropriate renderer based on notestyle type
        if (_renderer != null)
        {
            _renderer.DrawUpcomingHold(sb, lane, cx, noteY, holdPx, strumY, laneW, alpha, sx, _level.Data.Bpm);
            return;
        }
        
        float bodyTop = !Downscroll ? noteY : noteY - holdPx;
        float bodyBottom = !Downscroll ? noteY + holdPx : noteY;
        DrawHoldBody(sb, lane, laneX, cx, bodyTop, bodyBottom, LaneColors[lane], false, laneW, alpha);
    }

    // ── Shared hold body + cap renderer ──────────────────────────────────────
    private void DrawHoldBody(SpriteBatch sb, int lane, float laneX, float cx,
                               float bodyTop, float bodyBottom, Color laneCol, bool isActive, float laneW, float alpha)
    {
        float bodyH = Math.Max(0f, bodyBottom - bodyTop);
        if (bodyH <= 0f) return;

        // Apply BPM-synchronized pulse effect
        float pulseScale = 1f;
        if (_arrowEffects != null && ArrowEffectManager.Enabled)
        {
            float songBeats = _currentSongTime / 60f * _level.Data.EffectiveBpm;
            pulseScale = _arrowEffects.GetHoldNotePulse(songBeats);
        }
        laneW *= pulseScale;

        Color bodyTint = Color.White * (isActive ? 0.7f : 0.85f) * alpha;
        Color capTint  = Color.White * (isActive ? 0.7f : 0.85f) * alpha;
        float sx = laneW / LANE_WIDTH;
        float s = (_style?.NoteScale ?? 0.7f) * sx;

        // ── Column-based holds (FMM/converted Codename format) ────────────────
        if (_style?.HoldTexture is Texture2D holdTex)
        {
            int colW = holdTex.Width / 8;
            int texH = holdTex.Height;
            float bw = colW * s;
            float bx = cx - bw / 2f;
            int bodyCol = lane * 2;
            int capCol = lane * 2 + 1;
            
            const float BOTTOM_CLIP = 0.9f;
            int capSrcBottom = (int)Math.Round(BOTTOM_CLIP * texH);
            float fullCapHeight = capSrcBottom * s;
            float partHeight = bodyH - fullCapHeight;
            
            var bodySrc = texH > 4
                ? new Rectangle(bodyCol * colW, 1, colW, texH - 2)
                : new Rectangle(bodyCol * colW, 0, colW, texH);
            
            float consumedRatio = partHeight >= 0f
                ? 0f
                : 1f - (bodyH / Math.Max(1f, fullCapHeight));
            consumedRatio = Math.Clamp(consumedRatio, 0f, 1f);
            
            int capSrcY = (int)Math.Round(consumedRatio * capSrcBottom);
            int capSrcH = capSrcBottom - capSrcY;
            if (capSrcH <= 0) return;
            
            var capSrc = new Rectangle(capCol * colW, capSrcY, colW, capSrcH);
            int capH = (int)Math.Round(capSrcH * s);
            if (capH <= 0) return;
            
            int intBx = (int)Math.Round(bx);
            int intBw = (int)Math.Round(bw);
            
            if (!Downscroll)
            {
                int startIntY = (int)Math.Round(bodyTop);
                int joinY = partHeight > 0f ? (int)Math.Round(bodyTop + partHeight) : startIntY;
                int stretchH = joinY - startIntY;
                
                if (stretchH > 0)
                {
                    sb.Draw(holdTex, new Rectangle(intBx, startIntY, intBw, stretchH),
                        bodySrc, bodyTint);
                }
                
                sb.Draw(holdTex, new Rectangle(intBx, joinY, intBw, capH),
                    capSrc, capTint);
            }
            else
            {
                int startIntY = (int)Math.Round(bodyBottom);
                int joinY = partHeight > 0f ? (int)Math.Round(bodyBottom - partHeight) : startIntY;
                int stretchH = startIntY - joinY;
                
                if (stretchH > 0)
                {
                    sb.Draw(holdTex, new Rectangle(intBx, joinY, intBw, stretchH),
                        bodySrc, bodyTint, 0f, Vector2.Zero, SpriteEffects.FlipVertically, 0f);
                }
                
                sb.Draw(holdTex, new Rectangle(intBx, joinY - capH, intBw, capH),
                    capSrc, capTint, 0f, Vector2.Zero, SpriteEffects.FlipVertically, 0f);
            }
            return;
        }

        // ── FALLBACK: Simple rectangles ───────────────────────────────────────
        float fw = laneW * 0.3f;
        float fx = laneX + laneW * 0.35f;
        DrawRect(sb, fx, bodyTop, fw, bodyH, laneCol * 0.6f);
        
        float capSz = 8f;
        float capY = Downscroll ? bodyTop : bodyBottom - capSz;
        DrawRect(sb, fx, capY, fw, capSz, capTint);
    }

    // ── Receptor ─────────────────────────────────────────────────────────────
    private void DrawReceptor(SpriteBatch sb, int lane, float cx, float cy, float sx, float alpha, float songTime)
    {
        if (_fmm != null && _style != null)
        {
            DrawFmmReceptor(sb, lane, cx, cy, sx, alpha);
            return;
        }

        // ── FNF-style receptor intro animation (pop-in effect during countdown) ──
        float introScale = 1f;
        float introAlpha = 1f;
        
        if (_receptorIntroActive[lane])
        {
            float t = _receptorIntroTime[lane];
            
            if (t < 0f)
            {
                // Waiting to start - receptor is invisible
                return;  // Don't draw receptor yet
            }
            else if (t < RECEPTOR_INTRO_DURATION)
            {
                // Animation in progress - elastic pop-in effect (FNF style)
                float progress = t / RECEPTOR_INTRO_DURATION;
                
                // Elastic ease-out: overshoot then settle
                // FNF receptors "bounce" in with an elastic feel
                float elasticProgress;
                if (progress < 0.6f)
                {
                    // Quick scale up with overshoot
                    float p = progress / 0.6f;
                    elasticProgress = MathF.Sin(p * MathF.PI * 0.5f);  // Fast ease out
                    introScale = elasticProgress * 1.2f;  // Scale to 120% 
                }
                else
                {
                    // Settle back to normal size
                    float p = (progress - 0.6f) / 0.4f;
                    introScale = 1.2f - (p * 0.2f);  // From 120% back to 100%
                }
                
                // Fade in quickly at the start
                introAlpha = Math.Min(1f, progress * 3f);  // Fade in first 33% of animation
            }
        }
        
        // Apply intro animation to alpha
        alpha *= introAlpha;
        
        // Don't draw if completely transparent
        if (alpha <= 0.01f) return;
        
        // Get random arrow effects
        float effectOffsetX = 0f, effectOffsetY = 0f, effectRotation = 0f;
        float effectScale = 1f, effectGlow = 0f;
        
        if (_arrowEffects != null && ArrowEffectManager.Enabled)
        {
            var transform = _arrowEffects.GetArrowTransform(lane, cx, cy);
            effectOffsetX = transform.offsetX;
            effectOffsetY = transform.offsetY;
            effectRotation = transform.rotation;
            effectScale = transform.scale;
            effectGlow = transform.glow;
        }
        
        // Apply effect transforms to position
        cx += effectOffsetX;
        cy += effectOffsetY;
        
        if (_style != null)
        {
            bool holdActive = _holdEndTime[lane] > 0f;
            int state = _recState[lane];
            if (holdActive && state == 2) state = 3;

            // FNF-style receptor state transitions:
            // - press (1): plays ONCE, holds last frame (NO LOOP even when key held)
            // - confirm (2): plays once, then starts confirmHoldTimer
            // - After CONFIRM_HOLD_TIME (150ms): 
            //     * Player: goes to press (1) if key held, else static (0)
            //     * Opponent: goes to static (0)
            
            // For press (1) state: animation plays once and holds last frame
            // Transition handled in Update() when key is released
            if (state == 1)
            {
                // Press animation: play once, hold last frame, no loop
                // Release is handled in UpdatePlayer/UpdateOpponent
            }
            
            // For confirm (2) without active hold: start timer when animation finishes
            if (!holdActive && state == 2 && _confirmHoldTimer[lane] < 0)
            {
                float dur = RecAnimDuration(lane, state, 0.15f);
                if (_recAnimTime[lane] >= dur)
                {
                    // Animation finished → start hold timer
                    // Player: reset timer to allow immediate transition
                    // Opponent: start counting to enforce 150ms delay
                    _confirmHoldTimer[lane] = _isPlayer ? -1f : 0f;
                    
                    if (_isPlayer)
                    {
                        // Player can transition immediately based on key state
                        SetRecState(lane, _keyHeld[lane] ? 1 : 0);
                        state = _recState[lane];
                    }
                    // Opponent waits for timer (handled in Update())
                }
            }

            var frames = _style.ReceptorFrames[lane, state]
                      ?? _style.ReceptorFrames[lane, Math.Min(state, 2)]
                      ?? _style.ReceptorFrames[lane, 0];

            if (frames != null && frames.Count > 0)
            {
                float fps = _style.StateFps[Math.Min(state, _style.StateFps.Length - 1)];
                // Calculate frame index based on state:
                // - Static (0) and ConfirmHold (3): LOOP continuously
                // - Press (1) and Confirm (2): PLAY ONCE, hold last frame
                int totalFrames = frames.Count;
                int fi;
                if (state == 0 || state == 3)
                    fi = (int)(_recAnimTime[lane] * fps) % totalFrames;  // loop
                else
                    fi = Math.Min((int)(_recAnimTime[lane] * fps), totalFrames - 1);  // play once, clamp to last frame
                
                var f  = frames[fi];
                float s = _style.ReceptorScale * sx;
                
                // ── FNF-STYLE: Use offsets but keep receptor anchored ────────
                // FNF approach: offsets are applied, but the receptor stays centered
                // The offset adjusts the sprite position RELATIVE to the center point
                // This keeps visual consistency while respecting the sprite's natural pivot
                
                // Calculate the base position (centered on cx, cy)
                float baseCenterX = cx;
                float baseCenterY = cy;
                
                // Apply frame dimensions and offsets
                // IMPORTANT: Offsets in XML need to be SUBTRACTED (they're already negative in the file)
                // The (-f.OffsetX) compensates for the sprite's natural pivot point
                float drawX = baseCenterX - (f.FrameW * s) / 2f + (-f.OffsetX) * s;
                float drawY = baseCenterY - (f.FrameH * s) / 2f + (-f.OffsetY) * s;
                
                // Enhanced glow for active states with subtle outer glow
                float baseAlpha = holdActive
                    ? 0.75f + 0.25f * MathF.Sin(_recAnimTime[lane] * MathF.PI * 8f)
                    : (state == 0 ? 1.0f : 1.0f);  // Always full brightness for receptors
                
                // Extra glow when glowTimer is active (just hit a note) OR from random effects
                float glowBoost = _glowTimer[lane] > 0f ? 0.3f : effectGlow * 0.5f;
                float finalAlpha = Math.Min(1f, baseAlpha + glowBoost);
                
                // Draw subtle glow behind receptor when active
                if (_glowTimer[lane] > 0f || holdActive || effectGlow > 0f)
                {
                    float glowAlpha = (_glowTimer[lane] > 0f ? 0.4f : effectGlow * 0.6f) * alpha;
                    if (holdActive && _glowTimer[lane] <= 0f) glowAlpha = Math.Max(glowAlpha, 0.2f * alpha);
                    float glowScale = s * 1.15f;  // Slightly larger than receptor (no effectScale)
                    
                    // Apply same offset logic for glow (negate the offsets)
                    float glowX = baseCenterX - (f.FrameW * glowScale) / 2f + (-f.OffsetX) * glowScale;
                    float glowY = baseCenterY - (f.FrameH * glowScale) / 2f + (-f.OffsetY) * glowScale;
                    
                    // For WhiteGlow effect, use white color instead of lane color
                    Color glowColor = effectGlow > 0.5f ? Color.White * glowAlpha : Color.White * glowAlpha;
                    
                    // Calculate origin for rotation
                    Vector2 glowOrigin = new Vector2(f.FrameW / 2f + f.OffsetX, f.FrameH / 2f + f.OffsetY);
                    
                    // Handle rotated frames
                    if (f.Rotated)
                    {
                        // For rotated frames, use center pivot
                        Vector2 centerPivot = new Vector2(f.FrameW / 2f, f.FrameH / 2f);
                        float totalRotation = effectRotation - MathF.PI / 2f;
                        sb.Draw(f.Texture, new Vector2(baseCenterX, baseCenterY), f.Source,
                            glowColor, totalRotation, centerPivot, glowScale, SpriteEffects.None, 0f);
                    }
                    else
                    {
                        sb.Draw(f.Texture, new Vector2(glowX + glowOrigin.X * glowScale, glowY + glowOrigin.Y * glowScale), f.Source,
                            glowColor, effectRotation, glowOrigin, glowScale, SpriteEffects.None, 0f);
                    }
                }
                
                // Receptors are NOT colorized by lane (they stay grayscale/original)
                // Only the flying notes get colorized
                Color receptorColor = Color.White * finalAlpha * alpha * _style.StrumAlpha;
                
                // Calculate origin for rotation (center of the frame)
                Vector2 origin = new Vector2(f.FrameW / 2f + f.OffsetX, f.FrameH / 2f + f.OffsetY);
                // Don't scale receptors when pressing - keep original size
                float finalScale = s * introScale;  // Only apply intro animation scale
                
                // Handle rotated frames (Starling atlas format)
                if (f.Rotated)
                {
                    // For rotated sprites in atlas, apply -90° rotation when drawing
                    // Use center pivot and position at center
                    Vector2 centerPivot = new Vector2(f.FrameW / 2f, f.FrameH / 2f);
                    float totalRotation = effectRotation - MathF.PI / 2f;  // Base rotation + frame rotation (-90°)
                    sb.Draw(f.Texture, new Vector2(baseCenterX, baseCenterY), f.Source,
                        receptorColor, totalRotation, centerPivot, finalScale, SpriteEffects.None, 0f);
                }
                else
                {
                    sb.Draw(f.Texture, new Vector2(drawX + origin.X * s, drawY + origin.Y * s), f.Source,
                        receptorColor, effectRotation, origin, finalScale, SpriteEffects.None, 0f);
                }
                
                // Draw note splash effect on Sick! hits
                if (_splashTimer[lane] > 0f)
                {
                    DrawNoteSplash(sb, lane, cx, cy, sx, alpha);
                }
                
                return;
            }
        }

        // Fallback rendering
        Color bc = LaneColors[lane];
        bool  on = _glowTimer[lane] > 0f || _holdEndTime[lane] > 0f;
        Color rc = on ? Color.Lerp(bc * 0.5f, Color.White, 0.7f) : bc * 0.4f;
        float hw = (LANE_WIDTH - 10f) * sx;
        DrawRect(sb, cx - hw / 2f, cy - 20f * sx, hw, 40f * sx, rc * alpha);
        DrawRect(sb, cx - hw / 2f, cy - 20f * sx, hw, 40f * sx, Color.White * 0.25f * alpha, outline: true);
        
        // Draw splash effect even in fallback mode
        if (_splashTimer[lane] > 0f)
        {
            DrawNoteSplash(sb, lane, cx, cy, sx, alpha);
        }
    }

    /// <summary>
    /// FMM-faithful strum: no intro pop-in or glow (hud.hx cancels the strum animations);
    /// the press/hit animation lives in FmmNoteSystemRenderer.
    /// </summary>
    private void DrawFmmReceptor(SpriteBatch sb, int lane, float cx, float cy, float sx, float alpha)
    {
        float rotation = 0f;
        if (_arrowEffects != null && ArrowEffectManager.Enabled)
        {
            var transform = _arrowEffects.GetArrowTransform(lane, cx, cy);
            cx += transform.offsetX;
            cy += transform.offsetY;
            rotation = transform.rotation;
        }

        _fmm!.DrawStrum(sb, lane, cx, cy, _style!.ReceptorScale * sx, alpha, rotation);

        if (_splashTimer[lane] > 0f)
            DrawNoteSplash(sb, lane, cx, cy, sx, alpha);
    }

    /// <summary>Lane colour for splashes: FMM styles tint them with the pressed colours (hud.hx onSplashShown).</summary>
    private Color SplashLaneColor(int lane) => _fmm?.PressedColor(lane) ?? LaneColors[lane];

    // ── Note head ────────────────────────────────────────────────────────────
    private void DrawNoteHead(SpriteBatch sb, int lane, float cx, float cy, FnmmNote note, float sx, float alpha, float strumY)
    {
        int skinType = note.Props.VisualSkin;  // 0=default, 1=negative (X), 2=positive (O)

        // Symbol overlay (only when the style has no dedicated frame for the skin):
        // purely visual — X for the negative skin, O for the positive skin.
        string? symbol = skinType switch
        {
            NoteSkins.Negative => "X",
            NoteSkins.Positive => "O",
            _ => null,
        };

        // FMM-faithful: one image rotated and tinted per lane, no FNF pop/glow extras
        if (_fmm != null && _style != null)
        {
            float noteScale = _style.NoteScale * sx;
            bool dedicated = _fmm.DrawNote(sb, lane, skinType, cx, cy, noteScale, alpha);
            if (!dedicated && symbol != null && _font != null)
                DrawNoteSymbol(sb, symbol, cx, cy, _fmm.NoteImageWidth * noteScale, LaneColors[lane], alpha);
            return;
        }

        // Tint: use skin config or defaults
        Color fallbackTint = _style != null
            ? _style.GetSkinTint(skinType)
            : skinType switch
            {
                NoteSkins.Negative => new Color(255, 50,  50),
                NoteSkins.Positive => new Color(0,   200, 255),
                _ => Color.White,
            };

        if (_style != null)
        {
            var f = _style.GetNoteFrame(lane, skinType);
            if (f != null)
            {
                alpha *= _style.NoteAlpha;
                bool hasDedicatedFrame = _style.NoteFrames[lane, skinType] != null;
                Color baseTint = (hasDedicatedFrame || skinType == 0)
                    ? Color.White
                    : fallbackTint;
                
                // Apply lane colorization if enabled (for grayscale textures)
                Color tint = _style.ColorizeByLane && skinType == 0
                    ? LaneColors[lane] * alpha
                    : baseTint * alpha;
                
                // CHECK: Cornered-exclusive yellow arrows (NOTE_undyne - scrambled notes)
                float s     = _style.NoteScale * sx;
                
                // ── FNF-STYLE NOTE ENHANCEMENTS ───────────────────────────────
                // Calculate distance to receptor for proximity effects
                float distToReceptor = Math.Abs(cy - strumY);
                float proximityRange = 150f * ScaleY;  // Pixels from receptor where glow activates
                float proximityFactor = Math.Max(0f, 1f - (distToReceptor / proximityRange));
                
                // ── "POP" EFFECT: Note scales up slightly as it approaches receptor ──
                // FNF has this subtle effect that makes timing more visual
                // Scale: 1.0 (far) → 1.05 (at receptor) → back to 1.0 after passing
                float popScale = 1.0f;
                if (proximityFactor > 0f)
                {
                    // Ease-out curve for smooth scaling
                    float eased = 1f - MathF.Pow(1f - proximityFactor, 2f);
                    popScale = 1.0f + eased * 0.05f;  // Max 5% larger at receptor
                }
                
                s *= popScale;
                
                float drawX = cx - f.FrameW * s / 2f + (-f.OffsetX) * s;
                float drawY = cy - f.FrameH * s / 2f + (-f.OffsetY) * s;
                
                // 1. Subtle shadow/outline for depth (drawn behind the note)
                float shadowOffset = 3f * sx;
                Color shadowColor = Color.Black * 0.35f * alpha;
                sb.Draw(f.Texture, new Vector2(drawX + shadowOffset, drawY + shadowOffset), f.Source,
                    shadowColor, 0f, Vector2.Zero, s, SpriteEffects.None, 0f);
                
                // 2. Proximity glow (gets brighter as note approaches receptor) - behind main sprite
                if (skinType == 0 && proximityFactor > 0f)
                {
                    float glowAlpha = proximityFactor * 0.25f * alpha;  // Max 25% opacity at receptor
                    float glowScale = s * (1.0f + proximityFactor * 0.15f);  // Slightly larger
                    Color glowColor = LaneColors[lane] * glowAlpha;
                    
                    float glowX = cx - f.FrameW * glowScale / 2f + (-f.OffsetX) * glowScale;
                    float glowY = cy - f.FrameH * glowScale / 2f + (-f.OffsetY) * glowScale;
                    
                    // All frames are unrotated during atlas loading
                    sb.Draw(f.Texture, new Vector2(glowX, glowY), f.Source,
                        glowColor, 0f, Vector2.Zero, glowScale, SpriteEffects.None, 0f);
                }
                
                // 3. Main note sprite (all frames are unrotated during atlas loading)
                sb.Draw(f.Texture, new Vector2(drawX, drawY), f.Source,
                    tint, 0f, Vector2.Zero, s, SpriteEffects.None, 0f);
                
                // 4. Subtle highlight on top for extra polish (very subtle white overlay)
                // Only on normal notes (skinType == 0), makes them "pop" like in FNF
                if (skinType == 0)
                {
                    float highlightAlpha = (0.12f + proximityFactor * 0.08f) * alpha;  // Brighter near receptor
                    Color highlightColor = Color.White * highlightAlpha;
                    float highlightScale = s * 1.03f;
                    
                    if (f.Rotated)
                    {
                        Vector2 centerPivot = new Vector2(f.FrameW / 2f, f.FrameH / 2f);
                        sb.Draw(f.Texture, new Vector2(cx, cy), f.Source,
                            highlightColor, -MathF.PI / 2f, centerPivot, highlightScale, SpriteEffects.None, 0f);
                    }
                    else
                    {
                        sb.Draw(f.Texture, new Vector2(drawX, drawY), f.Source,
                            highlightColor, 0f, Vector2.Zero, highlightScale, SpriteEffects.None, 0f);
                    }
                }

                // Draw symbol overlay if no dedicated frame and font available
                // Large symbol in lane color, centered on the note
                if (!hasDedicatedFrame && symbol != null && _font != null)
                {
                    DrawNoteSymbol(sb, symbol, cx, cy, f.FrameW * s, LaneColors[lane], alpha);
                }
                return;
            }
        }

        // Fallback: no style loaded — draw colored rectangle
        Color c  = skinType == 0 ? LaneColors[lane] : fallbackTint;
        
        float hw = (LANE_WIDTH - 10f) * sx;
        
        // Calculate proximity for fallback rendering too
        float distToReceptorFb = Math.Abs(cy - strumY);
        float proximityRangeFb = 150f * ScaleY;
        float proximityFactorFb = Math.Max(0f, 1f - (distToReceptorFb / proximityRangeFb));
        
        // Pop effect for fallback
        float popScaleFb = 1.0f;
        if (proximityFactorFb > 0f)
        {
            float eased = 1f - MathF.Pow(1f - proximityFactorFb, 2f);
            popScaleFb = 1.0f + eased * 0.05f;
        }
        
        float scaledHw = hw * popScaleFb;
        
        // Draw shadow
        DrawRect(sb, cx - scaledHw / 2f + 2f * sx, cy - 20f * sx + 2f * sx, scaledHw, 40f * sx * popScaleFb, Color.Black * 0.35f * alpha);
        
        // Draw proximity glow for fallback
        if (skinType == 0 && proximityFactorFb > 0f)
        {
            float glowSize = scaledHw * (1.0f + proximityFactorFb * 0.15f);
            DrawRect(sb, cx - glowSize / 2f, cy - 20f * sx, glowSize, 40f * sx * popScaleFb, 
                LaneColors[lane] * 0.25f * proximityFactorFb * alpha);
        }
        
        // Draw main rect
        DrawRect(sb, cx - scaledHw / 2f, cy - 20f * sx, scaledHw, 40f * sx * popScaleFb, c * alpha);
        DrawRect(sb, cx - scaledHw / 2f, cy - 20f * sx, scaledHw, 40f * sx * popScaleFb, Color.White * 0.4f * alpha, outline: true);

        // Draw symbol overlay for non-normal notes
        if (_font != null && symbol != null)
        {
            DrawNoteSymbol(sb, symbol, cx, cy, scaledHw, LaneColors[lane], alpha);
        }
    }

    // ── Action pack analysis ─────────────────────────────────────────────────
    /// <summary>
    /// Analyzes an action_pack to calculate net health change.
    /// Returns the actual health value: positive = healing, negative = damage, 0 = neutral.
    /// This allows comparing which option does less damage when both are harmful.
    /// </summary>
    // ── Note symbol overlay (X / O) ──────────────────────────────────────────    /// <summary>
    /// Draws a large X or O symbol centered on the note, in the lane color.
    /// Uses a dark outline pass + bright fill pass for readability on any background.
    /// </summary>
    private void DrawNoteSymbol(SpriteBatch sb, string symbol, float cx, float cy,
                                float noteVisualW, Color laneColor, float alpha)
    {
        if (_font == null) return;

        // Scale so the symbol fills ~40% of the note's visual width
        var baseSize = _font.MeasureString(symbol);
        float targetW = noteVisualW * 0.40f;
        float symScale = baseSize.X > 0 ? targetW / baseSize.X : 1f;
        var symSize = baseSize * symScale;

        float x = cx - symSize.X / 2f;
        float y = cy - symSize.Y / 2f;

        // Dark outline — draw at 4 offsets
        float off = symScale * 1.5f;
        Color outline = Color.Black * 0.8f * alpha;
        sb.DrawString(_font, symbol, new Vector2(x - off, y - off), outline, 0f, Vector2.Zero, symScale, SpriteEffects.None, 0f);
        sb.DrawString(_font, symbol, new Vector2(x + off, y - off), outline, 0f, Vector2.Zero, symScale, SpriteEffects.None, 0f);
        sb.DrawString(_font, symbol, new Vector2(x - off, y + off), outline, 0f, Vector2.Zero, symScale, SpriteEffects.None, 0f);
        sb.DrawString(_font, symbol, new Vector2(x + off, y + off), outline, 0f, Vector2.Zero, symScale, SpriteEffects.None, 0f);

        // Bright fill in lane color
        sb.DrawString(_font, symbol, new Vector2(x, y), laneColor * alpha, 0f, Vector2.Zero, symScale, SpriteEffects.None, 0f);
    }

    // ── Helpers ──────────────────────────────────────────────────────────────
    private static int RateScore(float dist)
    {
        if (dist < W_SICK) return 350;
        if (dist < W_GOOD) return 200;
        if (dist < W_BAD)  return 100;
        return 50;
    }

    private static string GetJudgement(float dist)
    {
        if (dist < W_SICK) return "Sick!";
        if (dist < W_GOOD) return "Good";
        if (dist < W_BAD)  return "Bad";
        return "Shit";
    }

    private void DrawRect(SpriteBatch sb, float x, float y, float w, float h,
                          Color color, bool outline = false)
    {
        if (w <= 0 || h <= 0) return;
        if (outline)
        {
            float t = 2f;
            sb.Draw(_pixel, new Rectangle((int)x,       (int)y,       (int)w, (int)t), color);
            sb.Draw(_pixel, new Rectangle((int)x,       (int)(y+h-t), (int)w, (int)t), color);
            sb.Draw(_pixel, new Rectangle((int)x,       (int)y,       (int)t, (int)h), color);
            sb.Draw(_pixel, new Rectangle((int)(x+w-t), (int)y,       (int)t, (int)h), color);
        }
        else
            sb.Draw(_pixel, new Rectangle((int)x, (int)y, (int)w, (int)h), color);
    }
    
    /// <summary>
    /// Draws a note splash effect (particle burst) when hitting Sick! rating.
    /// Uses texture-based splash if _splashStyle has textures for THIS lane, otherwise uses programmatic default.
    /// </summary>
    private void DrawNoteSplash(SpriteBatch sb, int lane, float cx, float cy, float sx, float alpha)
    {
        // Check if we have texture-based splash FOR THIS SPECIFIC LANE
        // Must check both: 1) style exists, 2) this lane has frames loaded
        if (_splashStyle != null && _splashStyle.SplashFrames[lane] != null && _splashStyle.SplashFrames[lane]!.Count > 0)
        {
            DrawNoteSplashTexture(sb, lane, cx, cy, sx, alpha);
        }
        else
        {
            // Fallback to programmatic splash (default) - either no style or this lane has no frames
            DrawNoteSplashProgrammatic(sb, lane, cx, cy, sx, alpha);
        }
    }

    /// <summary>
    /// Draws texture-based notesplash animation from XML style.
    /// </summary>
    private void DrawNoteSplashTexture(SpriteBatch sb, int lane, float cx, float cy, float sx, float alpha)
    {
        if (_splashStyle == null || _splashStyle.SplashFrames[lane] == null) return;
        
        // Plays at the style's fps; _splashTimer counts down from SplashLength(lane)
        float elapsed = SplashLength(lane) - _splashTimer[lane];
        var frame = _splashStyle.FrameAt(lane, elapsed);
        if (frame == null) return;

        Color tint = _splashStyle.TintFor(lane, SplashLaneColor(lane)) * (_splashStyle.AlphaAt(lane, elapsed) * alpha);
        
        // FNF-style positioning: SAME AS RECEPTOR DRAWING (line 858-859)
        // Center the full frame, then apply NEGATED offset (like receptors do)
        float scale = sx * _splashStyle.Scale;
        
        // Match receptor positioning exactly
        float drawX = cx - frame.FrameW * scale / 2f + (-frame.OffsetX) * scale;
        float drawY = cy - frame.FrameH * scale / 2f + (-frame.OffsetY) * scale;
        
        // Apply user-defined offsets from splash.json
        drawX += _splashStyle.OffsetX[lane] * sx;
        drawY += _splashStyle.OffsetY[lane] * sx;
        
        sb.Draw(frame.Texture, new Vector2(drawX, drawY), frame.Source, tint,
                0f, Vector2.Zero, scale, SpriteEffects.None, 0f);
    }

    /// <summary>
    /// Draws programmatic notesplash (default fallback, no copyright issues).
    /// Simple particle burst effect with color-coded glow.
    /// </summary>
    /// <summary>
    /// Full-height black column covering this strumline's four lanes (plus a small margin),
    /// so the chart stays readable over busy backgrounds. Follows the strumline's position,
    /// HUD offset and visibility. Game1 calls it for the player strumline right after the
    /// scene, before the HUD, so it sits below every UI element and the notes.
    /// </summary>
    /// <param name="alpha">The strumline's opacity this frame (0 hides the underlay).</param>
    public void DrawLaneUnderlay(SpriteBatch sb, float alpha)
    {
        if (HudRole != 0 || LaneUnderlay <= 0f) return;
        if (!HudSettings.Instance.MasterHudVisible || !ShowStrumlines ||
            (_isPlayer && HidePlayerStrumline) || alpha <= 0.01f)
            return;

        float sx = ScaleX * NoteScale;
        float laneW = LANE_WIDTH * sx;
        float hudOffsetX = HudOffsetPx.X;

        float left = float.MaxValue, right = float.MinValue;
        for (int lane = 0; lane < LANE_COUNT; lane++)
        {
            float baseX = _xOffsetSplit >= 0f && lane >= 2
                ? _xOffsetSplit * sx + (lane - 2) * laneW
                : _xOffset * sx + lane * laneW;
            baseX += hudOffsetX;
            left = Math.Min(left, baseX);
            right = Math.Max(right, baseX + laneW);
        }
        float margin = laneW * 0.08f;
        DrawRect(sb, left - margin, 0f, right - left + margin * 2f, ScreenH,
                 Color.Black * (Math.Clamp(LaneUnderlay, 0f, LaneUnderlayMax) * alpha));
    }

    /// <summary>Starts a splash on a lane (only while this strumline is visible).</summary>
    private void TriggerSplash(int lane)
    {
        bool visible = HudSettings.Instance.MasterHudVisible && ShowStrumlines &&
                       !(_isPlayer && HidePlayerStrumline) && Alpha > 0.01f;
        if (!visible) return;

        // Particle splashes are positioned in Draw() (strumY is only known there)
        if (_particleSplash != null)
            _pendingSplashLane = lane;
        else
            _splashTimer[lane] = SplashLength(lane);
    }

    /// <summary>Splash length for a lane: the texture animation's length, or 0.3 s for the code-drawn one.</summary>
    private float SplashLength(int lane)
    {
        float len = _splashStyle?.Length(lane) ?? 0f;
        return len > 0f ? len : 0.3f;
    }

    private void DrawNoteSplashProgrammatic(SpriteBatch sb, int lane, float cx, float cy, float sx, float alpha)
    {
        float progress = Math.Clamp(1f - (_splashTimer[lane] / 0.3f), 0f, 1f);  // 0 = just started, 1 = finished
        float splashAlpha = (1f - progress) * 0.8f;  // Fade out
        float splashScale = 1f + progress * 0.5f;  // Expand outward
        
        Color splashColor = SplashLaneColor(lane) * splashAlpha * alpha;
        float splashSize = LANE_WIDTH * sx * splashScale;
        
        // Draw 4 particles in + pattern
        float particleLen = splashSize * 0.4f;
        float particleThick = 8f * sx;
        float offset = splashSize * 0.3f * progress;  // Move outward
        
        // Horizontal particles
        DrawRect(sb, cx - particleLen/2f, cy - particleThick/2f + offset, particleLen, particleThick, splashColor);
        DrawRect(sb, cx - particleLen/2f, cy - particleThick/2f - offset, particleLen, particleThick, splashColor);
        
        // Vertical particles
        DrawRect(sb, cx - particleThick/2f + offset, cy - particleLen/2f, particleThick, particleLen, splashColor);
        DrawRect(sb, cx - particleThick/2f - offset, cy - particleLen/2f, particleThick, particleLen, splashColor);
        
        // Center glow
        float glowRadius = splashSize * 0.3f;
        DrawCircle(sb, cx, cy, glowRadius, splashColor * 0.5f);
    }
    
    /// <summary>
    /// Draws a filled circle using a simple approximation (octagon).
    /// </summary>
    private void DrawCircle(SpriteBatch sb, float cx, float cy, float radius, Color color)
    {
        if (radius <= 0) return;
        
        // Draw as octagon (8 triangles from center)
        int segments = 8;
        float angleStep = MathF.PI * 2f / segments;
        
        for (int i = 0; i < segments; i++)
        {
            float angle1 = angleStep * i;
            float angle2 = angleStep * (i + 1);
            
            float x1 = cx + MathF.Cos(angle1) * radius;
            float y1 = cy + MathF.Sin(angle1) * radius;
            float x2 = cx + MathF.Cos(angle2) * radius;
            float y2 = cy + MathF.Sin(angle2) * radius;
            
            // Draw triangle: center -> point1 -> point2
            DrawTriangle(sb, cx, cy, x1, y1, x2, y2, color);
        }
    }
    
    /// <summary>
    /// Draws a filled triangle (approximated with rectangles).
    /// </summary>
    private void DrawTriangle(SpriteBatch sb, float x1, float y1, float x2, float y2, float x3, float y3, Color color)
    {
        // Simple approximation: draw lines between points
        DrawLine(sb, x1, y1, x2, y2, color, 2f);
        DrawLine(sb, x2, y2, x3, y3, color, 2f);
        DrawLine(sb, x3, y3, x1, y1, color, 2f);
    }
    
    /// <summary>
    /// Draws a line between two points.
    /// </summary>
    private void DrawLine(SpriteBatch sb, float x1, float y1, float x2, float y2, Color color, float thickness)
    {
        float dx = x2 - x1;
        float dy = y2 - y1;
        float length = MathF.Sqrt(dx * dx + dy * dy);
        if (length < 0.1f) return;
        
        float angle = MathF.Atan2(dy, dx);
        sb.Draw(_pixel, new Vector2(x1, y1), null, color, angle, Vector2.Zero, 
            new Vector2(length, thickness), SpriteEffects.None, 0f);
    }
}
