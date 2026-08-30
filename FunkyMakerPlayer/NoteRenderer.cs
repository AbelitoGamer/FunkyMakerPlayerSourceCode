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
    public const float LANE_WIDTH  = 112f;

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

    private const float BASE_SPEED = 450f;

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
    public static bool  Autoplay         = true;
    public static bool  Downscroll       = false;
    /// <summary>
    /// Centers the player strumline in the middle of the screen (hides opponent strumline).
    /// </summary>
    public static bool  Midlescroll      = false;
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

    // ── Per-instance state ───────────────────────────────────────────────────
    private readonly int              _strumlineIndex;
    private bool                      _isPlayer;  // Whether this strumline is controlled by local keyboard
    private float                     _xOffset;   // mutable — can be repositioned dynamically
    private readonly GraphicsDevice   _gd;
    private readonly float            _scrollSpeed;
    private readonly FnmmLevel        _level;     // kept for action_pack analysis
    private SpriteFont?               _font;  // set each Draw call, used for note symbols
    private ArrowEffectManager?       _arrowEffects;  // Random visual effects for arrows
    private float                     _currentSongTime = 0f;  // Current song time for effects
    private readonly List<FnmmNote>[] _notes;
    private readonly List<FnmmNote>[] _notesBackup;  // original notes for seek restore
    private readonly float[]          _glowTimer   = new float[LANE_COUNT];
    private readonly float[]          _holdEndTime = new float[LANE_COUNT];
    private readonly int[]            _recState    = new int[LANE_COUNT];
    private readonly float[]          _recAnimTime = new float[LANE_COUNT];

    // ── Public API ────────────────────────────────────────────────────────────
    
    /// <summary>
    /// Check if a hold note is currently active in the specified lane
    /// </summary>
    public bool IsHoldActive(int lane)
    {
        if (lane < 0 || lane >= LANE_COUNT) return false;
        return _holdEndTime[lane] > 0f;
    }
    private readonly float[]          _confirmHoldTimer = Enumerable.Repeat(-1f, LANE_COUNT).ToArray();  // FNF-style: -1 = inactive, >= 0 = counting (150ms)
    private const float CONFIRM_HOLD_TIME = 0.15f;  // 150ms like FNF official
    private readonly bool[]           _keyHeld     = new bool[LANE_COUNT];  // tracks if key is currently held
    private readonly float[]          _splashTimer = new float[LANE_COUNT];  // note splash effect timer
    private readonly float[]          _comboBreakTimer = new float[LANE_COUNT];  // combo break effect timer
    private readonly Texture2D        _pixel;
    private NoteStyle?                _style;
    private NoteSplashStyle?          _splashStyle;  // null = use programmatic default

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
    /// <summary>Fired with rating string ("Sick!", "Good", "Bad", "Shit") on manual hit.</summary>
    public Action<string>?     OnJudgement;
    /// <summary>Fired when a note with onPressEvent is hit. Args: event ID.</summary>
    public Action<int>?        OnNotePressEvent;
    /// <summary>Fired when a note with onMissEvent is missed. Args: event ID.</summary>
    public Action<int>?        OnNoteMissEvent;
    /// <summary>
    /// Fired when a damage note (skin=1) is hit with NO onPressEvent defined.
    /// The level handles health via action_packs when onPressEvent IS defined.
    /// </summary>
    public Action<float>?      OnDamageNoteHit;
    /// <summary>
    /// Fired when an instakill note (skin=2) is hit with NO onPressEvent defined.
    /// </summary>
    public Action?             OnInstakillNoteHit;
    /// <summary>
    /// Fired when a heal note (skin=3) is hit with NO onPressEvent defined.
    /// </summary>
    public Action?             OnHealNoteHit;

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
        _style          = style;
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
    }
    
    /// <summary>
    /// Sets whether this strumline is controlled by the local player.
    /// Must be called after construction to properly configure input handling.
    /// </summary>
    public void SetIsPlayer(bool isPlayer)
    {
        _isPlayer = isPlayer;
    }

    public void SetStyle(NoteStyle? style) => _style = style;
    public void SetSplashStyle(NoteSplashStyle? splashStyle) => _splashStyle = splashStyle;

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
        
        // Trigger splash effect for Sick! hits
        if (isSick)
            _splashTimer[lane] = 0.3f;
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
            
        for (int i = 0; i < LANE_COUNT; i++)
        {
            _glowTimer[i]   = Math.Max(0f, _glowTimer[i] - dt);
            _recAnimTime[i] += dt;
            _splashTimer[i] = Math.Max(0f, _splashTimer[i] - dt);
            _comboBreakTimer[i] = Math.Max(0f, _comboBreakTimer[i] - dt);
            
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

                // Autoplay: analyze the onPressEvent action_pack to decide if pressing is beneficial.
                // If pressing causes net negative health → dodge.
                // If no event → use skin type as fallback (skin=1 = bad → dodge).
                bool isDodge;
                int pressIdx = (int)note.Props.OnPressEvent;
                if (pressIdx > 0)
                {
                    // Has event — analyze health effect
                    isDodge = _isPlayer && AnalyzePackBenefit(pressIdx) < 0;
                }
                else
                {
                    // No event — use skin type fallback
                    isDodge = _isPlayer && note.Props.IsDamage;  // skin=1 = bad
                }
                if (isDodge)
                {
                    if (songTime - note.Start > HIT_WINDOW)
                        _notes[lane].RemoveAt(i);
                    continue;
                }

                // Normal/heal: press it
                _notes[lane].RemoveAt(i);
                Score += 350; Combo++;
                float holdEnd = note.Duration > 0 ? note.Start + note.Duration : -1f;
                OnNoteHit?.Invoke(lane, holdEnd);
                if (note.Props.OnPressEvent > 0)
                    OnNotePressEvent?.Invoke((int)note.Props.OnPressEvent);
                // Fallback: if no onPressEvent and skin=3 (heal), fire heal callback
                else if (note.Props.IsHeal)
                    OnHealNoteHit?.Invoke();
                if (note.Duration > 0)
                    _holdEndTime[lane] = Math.Max(_holdEndTime[lane], note.Start + note.Duration);
                else
                    _glowTimer[lane] = 0.12f;
                SetRecState(lane, 2);
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
            bool held         = kb.IsKeyDown(PlayerKeys[lane]);
            bool justPressed  = held && !prevKb.IsKeyDown(PlayerKeys[lane]);
            bool justReleased = !held && prevKb.IsKeyDown(PlayerKeys[lane]);
            
            _keyHeld[lane] = held;  // Track key state for receptor rendering

            // ── Active hold tracking ──────────────────────────────────────────
            if (_holdEndTime[lane] > 0f)
            {
                float holdEnd  = _holdEndTime[lane];
                float duration = _holdDuration[lane];
                float elapsed  = songTime - _holdStart[lane];
                float progress = duration > 0f ? Math.Clamp(elapsed / duration, 0f, 1f) : 1f;

                bool holdNaturallyEnded = songTime >= holdEnd;
                bool releasedEarly      = justReleased && !holdNaturallyEnded;

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
                        OnNoteMiss?.Invoke();
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
                
                // Increment rating counters based on judgement
                string judgement = GetJudgement(bestDist);
                switch (judgement)
                {
                    case "Sick!": Sicks++; break;
                    case "Good":  Goods++; break;
                    case "Bad":   Bads++;  break;
                    case "Shit":  Shits++; break;
                }
                
                // Trigger splash effect on Sick! rating (350 points)
                if (scoreGain >= 350)
                    _splashTimer[lane] = 0.3f;  // Splash lasts 300ms
                
                float holdEnd = best.Duration > 0 ? best.Start + best.Duration : -1f;
                OnNoteHit?.Invoke(lane, holdEnd);
                OnJudgement?.Invoke(judgement);

                if (best.Props.OnPressEvent > 0)
                {
                    // Level defines the mechanic
                    OnNotePressEvent?.Invoke((int)best.Props.OnPressEvent);
                }
                else
                {
                    // No event defined — apply default fallback by skin type
                    // skin=1 (BAD/X) → damage/instakill
                    // skin=2 (GOOD/O) → no penalty (press to survive)
                    // skin=3 (heal) → restore health
                    if (best.Props.IsDamage)
                        OnInstakillNoteHit?.Invoke();   // skin=1 = bad = instakill fallback
                    else if (best.Props.IsHeal)
                        OnHealNoteHit?.Invoke();
                    // skin=2 (IsInstakill in old naming) → good note, no default penalty
                }

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
                    OnNoteMiss?.Invoke();
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
                
                // All note types count as misses — the level's onMissEvent handles effects.
                Misses++; Combo = 0;
                _comboBreakTimer[lane] = 0.5f;  // Combo break effect
                OnNoteMiss?.Invoke();
                if (note.Props.OnMissEvent > 0)
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
        _currentSongTime = songTime;  // Store for effects
        // HideWhenIdle: opponent strumlines only show when a note is active
        if (HideWhenIdle && !_anyLaneActive) return;

        float sh     = ScreenH;
        float sx     = ScaleX * NoteScale;
        float strumY = StrumY;
        float spd    = EffSpeed;
        float laneW  = LANE_WIDTH * sx;
        float alpha  = Alpha;

        for (int lane = 0; lane < LANE_COUNT; lane++)
        {
            // Midlescroll split: lanes 0-1 use _xOffset, lanes 2-3 use _xOffsetSplit
            float laneX;
            if (_xOffsetSplit >= 0f && lane >= 2)
                laneX = _xOffsetSplit * sx + (lane - 2) * laneW;
            else
                laneX = _xOffset * sx + lane * laneW;
            float cx = laneX + laneW / 2f;

            // Active hold tail (while pressing hold note)
            if (_holdEndTime[lane] > 0f && songTime < _holdEndTime[lane])
                DrawActiveHold(sb, lane, laneX, cx, strumY, (_holdEndTime[lane] - songTime) * spd, laneW);

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

                bool isDangerous = _isPlayer && (note.Props.IsDamage || note.Props.IsInstakill);

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
                    DrawHold(sb, lane, laneX, cx, noteY, note.Duration * spd, strumY, laneW, alpha, sx);

                DrawNoteHead(sb, lane, cx, noteY, note, sx, alpha, strumY);
            }
        }

        if (!Autoplay && _isPlayer && font != null)
        {
            for (int lane = 0; lane < LANE_COUNT; lane++)
            {
                float lx = _xOffset * sx + lane * laneW + laneW / 2f;
                string label = PlayerKeys[lane].ToString();
                var sz = font.MeasureString(label);
                sb.DrawString(font, label,
                    new Vector2(lx - sz.X / 2f, strumY - sz.Y / 2f),
                    Color.White * 0.5f);
            }
        }
    }

    // ── Active hold (note consumed, tail visible while holding) ───────────────
    private void DrawActiveHold(SpriteBatch sb, int lane, float laneX, float cx,
                                 float strumY, float holdPx, float laneW)
    {
        // Active holds start from the receptor (strumY)
        // In upscroll: hold extends DOWN from receptor
        // In downscroll: hold extends UP from receptor
        float bodyTop, bodyBottom;
        if (!Downscroll)
        {
            bodyTop = strumY;
            bodyBottom = strumY + holdPx;
        }
        else
        {
            bodyBottom = strumY;
            bodyTop = strumY - holdPx;
        }
        DrawHoldBody(sb, lane, laneX, cx, bodyTop, bodyBottom, LaneColors[lane], isActive: true, laneW, Alpha);
    }

    // ── Upcoming hold note ────────────────────────────────────────────────────
    private void DrawHold(SpriteBatch sb, int lane, float laneX, float cx,
                          float noteY, float holdPx, float strumY, float laneW, float alpha, float sx)
    {
        // Note head is ~40px tall (±20px from center) scaled by sx
        // Additional offset needed to account for the note being drawn slightly offset
        // In upscroll: hold extends DOWN from bottom edge
        // In downscroll: hold extends UP from top edge
        
        // Base offset is 20px, but we add a small buffer (1px) to prevent visible gaps
        float noteHeadHalfHeight = 20f * sx + 1f;
        
        float bodyTop, bodyBottom;
        if (!Downscroll)
        {
            bodyTop = noteY + noteHeadHalfHeight;  // Start from bottom of note
            bodyBottom = bodyTop + holdPx;
        }
        else
        {
            bodyBottom = noteY - noteHeadHalfHeight;  // Start from top of note
            bodyTop = bodyBottom - holdPx;
        }
        DrawHoldBody(sb, lane, laneX, cx, bodyTop, bodyBottom, LaneColors[lane], isActive: false, laneW, alpha);
    }

    // ── Shared hold body + cap renderer ──────────────────────────────────────
    private void DrawHoldBody(SpriteBatch sb, int lane, float laneX, float cx,
                               float bodyTop, float bodyBottom, Color laneCol, bool isActive, float laneW, float alpha)
    {
        float bodyH = Math.Max(0f, bodyBottom - bodyTop);
        if (bodyH <= 0f) return;

        // Apply BPM-synchronized pulse effect to hold notes
        float pulseScale = 1f;
        if (_arrowEffects != null && ArrowEffectManager.Enabled)
        {
            float songBeats = _currentSongTime / 60f * _level.Data.EffectiveBpm;
            pulseScale = _arrowEffects.GetHoldNotePulse(songBeats);
        }
        
        // Apply pulse to lane width (subtle breathing effect)
        laneW *= pulseScale;

        Color bodyTint = Color.White * (isActive ? 0.7f : 0.85f) * alpha;
        Color capTint  = Color.White * (isActive ? 0.7f : 0.85f) * alpha;

        // ── PRIORITY 1: HoldNoteCover (FMM v1.1 style: start/hold/end) ────────
        if (_style?.HoldCoverStart[lane] != null || 
            _style?.HoldCoverHold[lane] != null || 
            _style?.HoldCoverEnd[lane] != null)
        {
            float sx = laneW / LANE_WIDTH;
            float s = _style.NoteScale * sx;
            
            AtlasFrame? startFrame = _style.HoldCoverStart[lane];
            AtlasFrame? holdFrame = _style.HoldCoverHold[lane];
            AtlasFrame? endFrame = _style.HoldCoverEnd[lane];
            
            float startH = startFrame != null ? startFrame.FrameH * s : 0f;
            float endH = endFrame != null ? endFrame.FrameH * s : 0f;
            float holdH = holdFrame != null ? holdFrame.FrameH * s : 0f;
            
            // Clamp to body height
            startH = Math.Min(startH, bodyH);
            endH = Math.Min(endH, bodyH - startH);
            float middleH = bodyH - startH - endH;
            
            float currentY = bodyTop;
            
            // Draw start (head)
            if (startFrame != null && startH > 0f)
            {
                // Center the frame in the lane using cx (already calculated as lane center)
                float scaledFrameW = startFrame.FrameW * s;
                float drawX = cx - scaledFrameW / 2f + (-startFrame.OffsetX) * s;
                float drawY = currentY + (-startFrame.OffsetY) * s;
                
                sb.Draw(startFrame.Texture, new Vector2(drawX, drawY), startFrame.Source, 
                    bodyTint, 0f, Vector2.Zero, s, SpriteEffects.None, 0f);
                
                currentY += startH;
            }
            
            // Draw middle (repeating hold)
            if (holdFrame != null && middleH > 0f && holdH > 0f)
            {
                float scaledFrameW = holdFrame.FrameW * s;
                float drawn = 0f;
                
                while (drawn < middleH)
                {
                    float seg = Math.Min(holdH, middleH - drawn);
                    float drawX = cx - scaledFrameW / 2f + (-holdFrame.OffsetX) * s;
                    float drawY = currentY + drawn + (-holdFrame.OffsetY) * s;
                    
                    Rectangle src = holdFrame.Source;
                    if (seg < holdH - 0.5f)
                    {
                        int srcH = Math.Max(1, (int)Math.Ceiling(seg / s));
                        src = new Rectangle(holdFrame.Source.X, holdFrame.Source.Y, 
                            holdFrame.Source.Width, srcH);
                    }
                    
                    sb.Draw(holdFrame.Texture, new Vector2(drawX, drawY), src, 
                        bodyTint, 0f, Vector2.Zero, s, SpriteEffects.None, 0f);
                    
                    drawn += seg;
                }
                
                currentY += middleH;
            }
            
            // Draw end (tail)
            if (endFrame != null && endH > 0f)
            {
                float scaledFrameW = endFrame.FrameW * s;
                float drawX = cx - scaledFrameW / 2f + (-endFrame.OffsetX) * s;
                float drawY = currentY + (-endFrame.OffsetY) * s;
                
                SpriteEffects flip = Downscroll ? SpriteEffects.FlipVertically : SpriteEffects.None;
                sb.Draw(endFrame.Texture, new Vector2(drawX, drawY), endFrame.Source, 
                    capTint, 0f, Vector2.Zero, s, flip, 0f);
            }
            
            return;
        }

        // ── PRIORITY 2: Atlas-based holds (Codename Engine style) ─────────────
        // If HoldPieceFrames are available, use them instead of column-based rendering
        if (_style?.HoldPieceFrames[lane] is AtlasFrame pieceFrame)
        {
            float sx = laneW / LANE_WIDTH;
            float s = _style.NoteScale * sx;
            
            // Calculate dimensions - center in lane using cx
            float scaledPieceW = pieceFrame.FrameW * s;
            float scaledPieceH = pieceFrame.FrameH * s;
            
            // Get end cap frame if available
            AtlasFrame? endFrame = _style.HoldEndFrames[lane];
            float capH = endFrame != null ? endFrame.FrameH * s : 0f;
            capH = Math.Min(capH, bodyH);  // Don't exceed total height
            float bodyOnlyH = bodyH - capH;
            
            // Tile the piece frame for the body
            if (bodyOnlyH > 0f && scaledPieceH > 0f)
            {
                float drawn = 0f;
                while (drawn < bodyOnlyH)
                {
                    float seg = Math.Min(scaledPieceH, bodyOnlyH - drawn);
                    // Center the piece in the lane
                    float drawX = cx - scaledPieceW / 2f + (-pieceFrame.OffsetX) * s;
                    float drawY = bodyTop + drawn + (-pieceFrame.OffsetY) * s;
                    
                    // If partial segment, adjust source rect
                    Rectangle src = pieceFrame.Source;
                    if (seg < scaledPieceH - 0.5f)
                    {
                        int srcH = Math.Max(1, (int)Math.Ceiling(seg / s));
                        src = new Rectangle(pieceFrame.Source.X, pieceFrame.Source.Y, pieceFrame.Source.Width, srcH);
                    }
                    
                    sb.Draw(pieceFrame.Texture, new Vector2(drawX, drawY), src, bodyTint, 0f, Vector2.Zero, s, SpriteEffects.None, 0f);
                    drawn += seg;
                }
            }
            
            // Draw end cap
            if (endFrame != null && capH > 0f)
            {
                float scaledCapW = endFrame.FrameW * s;
                float drawX = cx - scaledCapW / 2f + (-endFrame.OffsetX) * s;
                float drawY = !Downscroll ? (bodyTop + bodyOnlyH + (-endFrame.OffsetY) * s) : (bodyTop - capH + (-endFrame.OffsetY) * s);
                SpriteEffects flip = Downscroll ? SpriteEffects.FlipVertically : SpriteEffects.None;
                sb.Draw(endFrame.Texture, new Vector2(drawX, drawY), endFrame.Source, capTint, 0f, Vector2.Zero, s, flip, 0f);
            }
            return;
        }

        // ── Column-based holds (FMM/traditional style) ────────────────────────
        if (_style?.HoldTexture is Texture2D holdTex)
        {
            int   colW    = holdTex.Width / 8;
            int   texH    = holdTex.Height;
            float sx      = laneW / LANE_WIDTH;
            float s       = _style.NoteScale * sx;
            float bw      = colW * s;
            float bx      = cx - bw / 2f;
            int   bodyCol = lane * 2;
            int   capCol  = lane * 2 + 1;
            // Inset source rects by 1px on each side to prevent LinearClamp bleeding
            // between adjacent columns and tile edges.
            var   bodySrc = new Rectangle(bodyCol * colW + 1, 1, colW - 2, texH - 2);
            var   capSrc  = new Rectangle(capCol  * colW + 1, 1, colW - 2, texH - 2);

            // Cap height at current scale — clamp so it never exceeds the total body height
            float capH    = Math.Min(texH * s, bodyH);
            float bodyOnlyH = bodyH - capH;  // space for tiled body above the cap

            // Tile the body section (above the cap)
            if (bodyOnlyH > 0f)
            {
                float tileH = texH * s;
                float drawn = 0f;
                while (drawn < bodyOnlyH)
                {
                    float seg = Math.Min(tileH, bodyOnlyH - drawn);
                    Rectangle src;
                    if (seg >= tileH - 0.5f)
                        src = bodySrc;
                    else
                        src = new Rectangle(bodyCol * colW, 0, colW, Math.Max(1, (int)Math.Ceiling(seg / s)));
                    sb.Draw(holdTex,
                        new Rectangle((int)bx, (int)(bodyTop + drawn), (int)bw, (int)Math.Ceiling(seg)),
                        src, bodyTint);
                    drawn += seg;
                }
            }

            // Draw cap at the end of the hold, scaled to fit
            if (!Downscroll)
            {
                float capY = bodyTop + bodyOnlyH;
                sb.Draw(holdTex, new Rectangle((int)bx, (int)capY, (int)bw, (int)capH), capSrc, capTint);
            }
            else
            {
                float capY = bodyTop - capH;
                sb.Draw(holdTex, new Rectangle((int)bx, (int)capY, (int)bw, (int)capH),
                    capSrc, capTint, 0f, Vector2.Zero, SpriteEffects.FlipVertically, 0f);
            }
            return;
        }

        // Fallback rectangles
        float fw = laneW * 0.3f;
        float fx = laneX + laneW * 0.35f;
        DrawRect(sb, fx, bodyTop, fw, bodyH, laneCol * 0.6f);
        float capSz  = 8f;
        float capFbY = !Downscroll ? bodyBottom : bodyTop - capSz;
        DrawRect(sb, fx, capFbY, fw, capSz, capTint);
    }

    // ── Receptor ─────────────────────────────────────────────────────────────
    private void DrawReceptor(SpriteBatch sb, int lane, float cx, float cy, float sx, float alpha, float songTime)
    {
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
        
        // Draw combo break effect (red flash)
        if (_comboBreakTimer[lane] > 0f)
        {
            float breakAlpha = _comboBreakTimer[lane] / 0.5f;  // Fade out over 500ms
            float breakSize = 1.2f + (1f - breakAlpha) * 0.3f;  // Expand slightly
            Color breakColor = Color.Red * breakAlpha * 0.6f * alpha;
            float breakRadius = LANE_WIDTH * sx * breakSize * 0.6f;
            DrawCircle(sb, cx, cy, breakRadius, breakColor);
        }
        
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
                    : (state == 0 ? 0.6f : 1.0f);
                
                // Extra glow when glowTimer is active (just hit a note) OR from random effects
                float glowBoost = _glowTimer[lane] > 0f ? 0.3f : effectGlow * 0.5f;
                float finalAlpha = Math.Min(1f, baseAlpha + glowBoost);
                
                // Draw subtle glow behind receptor when active
                if (_glowTimer[lane] > 0f || holdActive || effectGlow > 0f)
                {
                    float glowAlpha = (_glowTimer[lane] > 0f ? 0.4f : effectGlow * 0.6f) * alpha;
                    if (holdActive && _glowTimer[lane] <= 0f) glowAlpha = Math.Max(glowAlpha, 0.2f * alpha);
                    float glowScale = s * 1.15f * effectScale;  // Slightly larger than receptor
                    
                    // Apply same offset logic for glow (negate the offsets)
                    float glowX = baseCenterX - (f.FrameW * glowScale) / 2f + (-f.OffsetX) * glowScale;
                    float glowY = baseCenterY - (f.FrameH * glowScale) / 2f + (-f.OffsetY) * glowScale;
                    
                    // For WhiteGlow effect, use white color instead of lane color
                    Color glowColor = effectGlow > 0.5f ? Color.White * glowAlpha : Color.White * glowAlpha;
                    
                    // Calculate origin for rotation
                    Vector2 glowOrigin = new Vector2(f.FrameW / 2f + f.OffsetX, f.FrameH / 2f + f.OffsetY);
                    
                    sb.Draw(f.Texture, new Vector2(glowX + glowOrigin.X * glowScale, glowY + glowOrigin.Y * glowScale), f.Source,
                        glowColor, effectRotation, glowOrigin, glowScale, SpriteEffects.None, 0f);
                }
                
                // Receptors are NOT colorized by lane (they stay grayscale/original)
                // Only the flying notes get colorized
                Color receptorColor = Color.White * finalAlpha * alpha;
                
                // Calculate origin for rotation (center of the frame)
                Vector2 origin = new Vector2(f.FrameW / 2f + f.OffsetX, f.FrameH / 2f + f.OffsetY);
                float finalScale = s * effectScale;
                
                sb.Draw(f.Texture, new Vector2(drawX + origin.X * s, drawY + origin.Y * s), f.Source,
                    receptorColor, effectRotation, origin, finalScale, SpriteEffects.None, 0f);
                
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

    // ── Note head ────────────────────────────────────────────────────────────
    private void DrawNoteHead(SpriteBatch sb, int lane, float cx, float cy, FnmmNote note, float sx, float alpha, float strumY)
    {
        int skinType = (int)note.Props.Skin;  // 0=normal, 1=bad, 2=good, 3=heal

        // Determine symbol and tint based on event analysis + skin type.
        // Priority: analyze onPressEvent action_pack for health effect.
        // If event exists → use event analysis to determine X or O.
        // If no event → use skin type as fallback.
        int pressPackIdx = (int)note.Props.OnPressEvent;
        int benefit = pressPackIdx > 0 ? AnalyzePackBenefit(pressPackIdx) : 0;

        // Symbol: O = press (beneficial/neutral), X = avoid (harmful)
        // With event: benefit>0 → O, benefit<0 → X, benefit=0 → use skin fallback
        // Without event: skin=1 → X, skin=2 → O, skin=3 → O, skin=0 → none
        string? symbol;
        if (skinType == 0)
        {
            symbol = null;  // normal note, never show symbol
        }
        else if (pressPackIdx > 0)
        {
            // Level defined an event — use health analysis
            symbol = benefit >= 0 ? "O" : "X";  // neutral/positive = O, negative = X
        }
        else
        {
            // No event — use skin type fallback
            symbol = skinType switch { 1 => "X", 2 => "O", 3 => "O", _ => null };
        }

        // Tint: use skin config or defaults
        Color fallbackTint = _style != null
            ? _style.GetSkinTint(skinType)
            : skinType switch
            {
                1 => new Color(255, 50,  50),
                2 => new Color(0,   200, 255),
                3 => new Color(0,   230, 80),
                _ => Color.White,
            };

        if (_style != null)
        {
            var f = _style.GetNoteFrame(lane, skinType);
            if (f != null)
            {
                bool hasDedicatedFrame = _style.NoteFrames[lane, skinType] != null;
                Color baseTint = (hasDedicatedFrame || skinType == 0)
                    ? Color.White
                    : fallbackTint;
                
                // Apply lane colorization if enabled (for grayscale textures)
                Color tint = _style.ColorizeByLane && skinType == 0
                    ? LaneColors[lane] * alpha
                    : baseTint * alpha;

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
                    
                    sb.Draw(f.Texture, new Vector2(glowX, glowY), f.Source,
                        glowColor, 0f, Vector2.Zero, glowScale, SpriteEffects.None, 0f);
                }
                
                // 3. Main note sprite (always static - frame 0 only for flying notes)
                sb.Draw(f.Texture, new Vector2(drawX, drawY), f.Source,
                    tint, 0f, Vector2.Zero, s, SpriteEffects.None, 0f);
                
                // 4. Subtle highlight on top for extra polish (very subtle white overlay)
                // Only on normal notes (skinType == 0), makes them "pop" like in FNF
                if (skinType == 0)
                {
                    float highlightAlpha = (0.12f + proximityFactor * 0.08f) * alpha;  // Brighter near receptor
                    Color highlightColor = Color.White * highlightAlpha;
                    sb.Draw(f.Texture, new Vector2(drawX, drawY), f.Source,
                        highlightColor, 0f, Vector2.Zero, s * 1.03f, SpriteEffects.None, 0f);
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
    /// Analyzes an action_pack to determine if pressing the note is beneficial.
    /// Returns: +1 = net positive (press), -1 = net negative (dodge), 0 = neutral/unknown.
    /// Looks at PlayerHealth actions: "add" = positive, "remove" = negative.
    /// </summary>
    private int AnalyzePackBenefit(int packIndex)
    {
        if (packIndex <= 0) return 0;
        var packs = _level.Events.ActionPacks;
        if (packIndex > packs.Count) return 0;
        var pack = packs[packIndex - 1];
        float netHealth = 0f;
        foreach (var action in pack.Actions)
        {
            if (action.Type != "PlayerHealth") continue;
            float val = action.Props.ValueFloat ?? 0f;
            string op = action.Props.Operation ?? "remove";
            netHealth += op == "add" ? val : -val;
        }
        if (netHealth > 0f) return 1;   // net positive → press
        if (netHealth < 0f) return -1;  // net negative → dodge
        return 0;  // no health effect → neutral, press by default
    }

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
        
        var frames = _splashStyle.SplashFrames[lane]!;
        float duration = _splashStyle.Duration;
        float progress = 1f - (_splashTimer[lane] / duration);  // 0 = just started, 1 = finished
        
        // Calculate frame index based on FPS and progress
        int frameCount = frames.Count;
        int frameIndex = (int)(progress * frameCount);
        if (frameIndex >= frameCount) frameIndex = frameCount - 1;
        if (frameIndex < 0) frameIndex = 0;
        
        var frame = frames[frameIndex];
        float frameAlpha = (1f - progress) * alpha;
        
        // Apply colorize by lane if enabled
        Color tint = _splashStyle.ColorizeByLane ? LaneColors[lane] : Color.White;
        tint *= frameAlpha;
        
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
    private void DrawNoteSplashProgrammatic(SpriteBatch sb, int lane, float cx, float cy, float sx, float alpha)
    {
        float progress = 1f - (_splashTimer[lane] / 0.3f);  // 0 = just started, 1 = finished
        float splashAlpha = (1f - progress) * 0.8f;  // Fade out
        float splashScale = 1f + progress * 0.5f;  // Expand outward
        
        Color splashColor = LaneColors[lane] * splashAlpha * alpha;
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
