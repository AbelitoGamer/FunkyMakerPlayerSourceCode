using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.Xna.Framework;

namespace FunkyMakerPlayer;

public class ScreenFlash
{
    public Color Color    { get; set; }
    public float Duration { get; set; }
    public float Elapsed  { get; set; }
    public float Alpha    => MathHelper.Clamp(1f - Elapsed / Duration, 0f, 1f);
    public bool  Done     => Elapsed >= Duration;
}

/// <summary>
/// Camera state.
///
/// GameMaker camera movement uses frame-by-frame lerp (exponential approach):
///   each frame: pos += (target - pos) * speed
/// This gives the characteristic "fast start, slow finish" feel.
/// For timed transitions (transitionTime > 0) we use easing curves.
/// For "default" easing we use GM-style exponential lerp.
/// </summary>
public class CameraState
{
    public float X     { get; set; }
    public float Y     { get; set; }
    public float Scale { get; set; } = 1f;
    
    // Store the base scale from scene.json camera
    // This is used as reference when CameraSet loads a new camera
    public float BaseScale { get; set; } = 1f;
    
    // ── World bounds for camera clamping ─────────────────────────────────────
    private const float WORLD_WIDTH = 2160f;
    private const float WORLD_HEIGHT = 1920f;
    private const float VIEWPORT_WIDTH = 1920f;
    private const float VIEWPORT_HEIGHT = 1080f;

    // ── Position transition state (X/Y only) ─────────────────────────────────
    private float _sx, _sy; // start position
    public float StartX => _sx;  // Expose start position for scrollfactor calculations
    public float StartY => _sy;
    public float TargetX  { get; set; }
    public float TargetY  { get; set; }
    public float PosTransTime { get; set; }
    public float PosTransElapsed { get; set; }
    public string PosEasingType { get; set; } = "default";
    
    // Beat-time support for position transitions
    public bool UseBeatTimePos { get; set; }
    public float StartBeatPos { get; set; }
    public float DurationBeatsPos { get; set; }
    
    // ── Scale transition state (zoom only) ───────────────────────────────────
    private float _ss; // start scale
    public float TargetScale { get; set; } = 1f;
    public float ScaleTransTime { get; set; }
    public float ScaleTransElapsed { get; set; }
    public string ScaleEasingType { get; set; } = "default";
    
    // Track if the current scale transition is from a CameraZoom event
    // Used to determine if zoom should be preserved across CameraSet changes
    public bool IsZoomEvent { get; set; }
    
    // Beat-time support for scale transitions
    public bool UseBeatTimeScale { get; set; }
    public float StartBeatScale { get; set; }
    public float DurationBeatsScale { get; set; }
    
    // ── Legacy properties for backward compatibility ─────────────────────────
    public float TransTime { get => Math.Max(PosTransTime, ScaleTransTime); set => PosTransTime = value; }
    public float TransElapsed { get => Math.Max(PosTransElapsed, ScaleTransElapsed); set => PosTransElapsed = value; }
    public string EasingType { get => PosEasingType; set => PosEasingType = value; }


    // ── Camera shake ─────────────────────────────────────────────────────────
    private float _shakeStrength;
    private float _shakeDelay;
    private float _shakeCount;
    private float _shakeTimer;
    private static readonly Random _shakeRng = new();

    /// <summary>Offset applied by shake — add to X/Y when building camera matrix.</summary>
    public float ShakeOffsetX { get; private set; }
    public float ShakeOffsetY { get; private set; }

    public void StartShake(float strength, float delay, float shakes)
    {
        _shakeStrength = strength;
        _shakeDelay    = delay > 0f ? delay : 0.05f;
        _shakeCount    = shakes > 0f ? shakes : 3f;
        _shakeTimer    = 0f;
        ShakeOffsetX   = 0f;
        ShakeOffsetY   = 0f;
    }

    public void StartTransition(float tx, float ty, float ts,
                                float transTime, string easing,
                                bool useBeatTime = false, float startBeat = 0f, float durationBeats = 0f)
    {
        _sx = X; _sy = Y; _ss = Scale;
        TargetX = tx; TargetY = ty; TargetScale = ts;
        
        // Set BOTH position and scale transitions
        PosTransTime = transTime; PosTransElapsed = 0f; PosEasingType = easing;
        ScaleTransTime = transTime; ScaleTransElapsed = 0f; ScaleEasingType = easing;
        IsZoomEvent = false; // CameraSet transitions are NOT zoom events
        
        // Beat-time support
        UseBeatTimePos = useBeatTime;
        UseBeatTimeScale = useBeatTime;
        StartBeatPos = startBeat;
        StartBeatScale = startBeat;
        DurationBeatsPos = durationBeats;
        DurationBeatsScale = durationBeats;
        
        if (transTime <= 0f) { X = tx; Y = ty; Scale = ts; }
    }
    
    /// <summary>
    /// Start ONLY a scale transition, preserving any active position transition
    /// </summary>
    public void StartScaleTransition(float targetScale, float transTime, string easing,
                                     bool useBeatTime = false, float startBeat = 0f, float durationBeats = 0f,
                                     bool isZoomEvent = false)
    {
        _ss = Scale;
        TargetScale = targetScale;
        ScaleTransTime = transTime;
        ScaleTransElapsed = 0f;
        ScaleEasingType = easing;
        IsZoomEvent = isZoomEvent; // Track if this is a CameraZoom event
        
        // Beat-time support
        UseBeatTimeScale = useBeatTime;
        StartBeatScale = startBeat;
        DurationBeatsScale = durationBeats;
        
        if (transTime <= 0f) { Scale = targetScale; }
    }

    /// <summary>
    /// Clamps camera position to prevent black borders outside world bounds.
    /// </summary>
    private void ClampToWorldBounds()
    {
        // Calculate visible area in world space based on scale
        float visibleWidth = VIEWPORT_WIDTH * Scale;
        float visibleHeight = VIEWPORT_HEIGHT * Scale;
        
        // Calculate safe bounds (camera can't get too close to edges)
        float minX = visibleWidth / 2f;
        float maxX = WORLD_WIDTH - visibleWidth / 2f;
        float minY = visibleHeight / 2f;
        float maxY = WORLD_HEIGHT - visibleHeight / 2f;
        
        // Clamp camera position AND targets to prevent transition "tilteo"
        X = Math.Clamp(X, minX, maxX);
        Y = Math.Clamp(Y, minY, maxY);
        TargetX = Math.Clamp(TargetX, minX, maxX);
        TargetY = Math.Clamp(TargetY, minY, maxY);
    }
    
    /// <summary>
    /// Public method to clamp camera position (called after manual position changes).
    /// </summary>
    public void ClampPosition()
    {
        ClampToWorldBounds();
    }

    public void Update(float dt, float currentBeat = 0f)
    {
        // ── Camera shake ─────────────────────────────────────────────────────
        if (_shakeCount > 0f)
        {
            _shakeTimer += dt;
            if (_shakeTimer >= _shakeDelay)
            {
                _shakeTimer -= _shakeDelay;
                _shakeCount -= 1f;
                if (_shakeCount > 0f)
                {
                    float angle = (float)(_shakeRng.NextDouble() * Math.PI * 2);
                    float decay = _shakeCount / Math.Max(1f, _shakeCount + 1f);
                    ShakeOffsetX = MathF.Cos(angle) * _shakeStrength * decay;
                    ShakeOffsetY = MathF.Sin(angle) * _shakeStrength * decay;
                }
                else
                {
                    ShakeOffsetX = 0f;
                    ShakeOffsetY = 0f;
                }
            }
        }

        // ── Position transition (X/Y) ────────────────────────────────────────
        if (PosTransTime > 0f)
        {
            // Calculate elapsed based on timing mode
            float elapsed, duration;
            if (UseBeatTimePos)
            {
                elapsed = currentBeat - StartBeatPos;
                duration = DurationBeatsPos;
            }
            else
            {
                PosTransElapsed = Math.Min(PosTransElapsed + dt, PosTransTime);
                elapsed = PosTransElapsed;
                duration = PosTransTime;
            }
            
            // Only snap when time expires - don't use nearTarget check
            // because easing curves can slow down near the end causing premature termination
            bool timeExpired = elapsed >= duration;
            
            if (timeExpired)
            {
                X = TargetX; 
                Y = TargetY; 
                PosTransTime = 0f; // Mark position transition as complete
                UseBeatTimePos = false;
            }
            else
            {
                if (PosEasingType == "default")
                {
                    // Use exponential lerp for smooth "default" easing
                    float lerpRate = duration > 0f ? 8.0f / duration : 60f;
                    float alpha = 1f - MathF.Exp(-lerpRate * dt);
                    X += (TargetX - X) * alpha;
                    Y += (TargetY - Y) * alpha;
                }
                else
                {
                    // Use specified easing function
                    float e = Ease(elapsed / duration, PosEasingType);
                    X = MathHelper.Lerp(_sx, TargetX, e);
                    Y = MathHelper.Lerp(_sy, TargetY, e);
                }
            }
        }
        
        // ── Scale transition (zoom) ──────────────────────────────────────────
        if (ScaleTransTime > 0f)
        {
            // Calculate elapsed based on timing mode
            float elapsed, duration;
            if (UseBeatTimeScale)
            {
                elapsed = currentBeat - StartBeatScale;
                duration = DurationBeatsScale;
            }
            else
            {
                ScaleTransElapsed = Math.Min(ScaleTransElapsed + dt, ScaleTransTime);
                elapsed = ScaleTransElapsed;
                duration = ScaleTransTime;
            }
            
            // Only snap when time expires - don't use nearTarget check
            // because easing curves can slow down near the end causing premature termination
            bool timeExpired = elapsed >= duration;
            
            if (timeExpired)
            {
                Scale = TargetScale;
                ScaleTransTime = 0f; // Mark scale transition as complete
                UseBeatTimeScale = false;
                IsZoomEvent = false; // Reset zoom event flag when transition completes
            }
            else
            {
                if (ScaleEasingType == "default")
                {
                    float lerpRate = duration > 0f ? 8.0f / duration : 60f;
                    float alpha = 1f - MathF.Exp(-lerpRate * dt);
                    Scale += (TargetScale - Scale) * alpha;
                }
                else
                {
                    float e = Ease(elapsed / duration, ScaleEasingType);
                    Scale = MathHelper.Lerp(_ss, TargetScale, e);
                }
            }
        }
        
        // ── Clamp camera to world bounds ─────────────────────────────────────
        ClampToWorldBounds();
    }

    private static float Ease(float t, string type) => type switch
    {
        "circ"  => EaseOutCirc(t),  // Standard ease-out: starts fast, decelerates smoothly
        "quad"  => 1f - (1f - t) * (1f - t),  // ease-out: starts fast, decelerates smoothly
        "cubic" => 1f - MathF.Pow(1f - t, 3f),  // ease-out: starts fast, decelerates smoothly
        "sine"  => MathF.Sin(t * MathF.PI / 2f),  // ease-out
        "expo"  => t >= 1f ? 1f : 1f - MathF.Pow(2f, -10f * t),  // ease-out
        "bounce"=> EaseBounce(t),  // ease-out
        _       => t,  // linear
    };

    private static float EaseBounce(float t)
    {
        if (t < 1f / 2.75f) return 7.5625f * t * t;
        if (t < 2f / 2.75f) { t -= 1.5f / 2.75f;   return 7.5625f * t * t + 0.75f; }
        if (t < 2.5f / 2.75f) { t -= 2.25f / 2.75f; return 7.5625f * t * t + 0.9375f; }
        t -= 2.625f / 2.75f; return 7.5625f * t * t + 0.984375f;
    }
    
    private static float EaseInOutCirc(float t)
    {
        // Ease-in-out circular: smooth acceleration and deceleration
        // First half: ease-in (slow start), second half: ease-out (slow end)
        if (t < 0.5f)
        {
            // Ease-in: 0.5 * (1 - sqrt(1 - (2t)^2))
            return 0.5f * (1f - MathF.Sqrt(1f - 4f * t * t));
        }
        else
        {
            // Ease-out: 0.5 * (sqrt(1 - (2t-2)^2) + 1)
            float x = 2f * t - 2f;
            return 0.5f * (MathF.Sqrt(1f - x * x) + 1f);
        }
    }
    
    private static float EaseOutCirc(float t)
    {
        // Standard ease-out circular (like easings.net and GameMaker)
        // Starts fast, decelerates smoothly at the end
        // Formula: sqrt(1 - (t - 1)^2)
        return MathF.Sqrt(1f - MathF.Pow(t - 1f, 2f));
    }
}

/// <summary>
/// Fires FNMM events at the correct beat time.
///
/// Objects are indexed 0-74 (GameMaker array).
/// Cameras are indexed 0-49 (GameMaker array), but CameraSet target is 1-based.
///
/// If no CameraSet events exist, the camera auto-follows the last character
/// that sang (classic FNF behavior).
/// </summary>
public class EventProcessor
{
    private readonly FnmmLevel                          _level;
    private readonly SpriteAnimator?[]                  _objAnimators;
    private readonly Dictionary<string, List<Microsoft.Xna.Framework.Graphics.Texture2D>> _texCache;
    private int  _nextEventIndex  = 0;
    private bool _hasCameraEvents = false;

    // Unknown event type logger
    private readonly HashSet<string> _unknownTypes = new();
    private static readonly HashSet<string> KnownTypes = new()
    {
        "ObjectAnimationSet", "ObjectVisibilitySet", "ObjectSpriteSet",
        "ObjectBlendColor", "ObjectOpacitySet", "ObjectTransformSet", "ObjectPositionSet",
        "CameraSet", "CameraZoom", "ScreenFlash", "BarIconChange",
        "RunActionPack", "PlayerHealth", "ObjectChartChange", "CameraShake", "GUI"
    };

    /// <summary>
    /// Set of event type strings that were encountered but not handled.
    /// Populated in real-time as events fire. Useful for debug overlay.
    /// </summary>
    public IReadOnlyCollection<string> UnknownEventTypes => _unknownTypes;

    // Per-object opacity (0..1), separate from Visible flag
    private readonly float[] _objOpacity;
    
    // Per-object opacity tween
    private class OpacityTween
    {
        public float From { get; set; }
        public float To { get; set; }
        public float Elapsed { get; set; }
        public float Duration { get; set; }
        public bool UseBeatTime { get; set; }
        public float StartBeat { get; set; }
        public float DurationBeats { get; set; }
        
        public bool IsDone(float elapsed) => UseBeatTime ? elapsed >= DurationBeats : elapsed >= Duration;
        public bool IsActive => Duration > 0f;
    }
    private readonly OpacityTween[] _opacityTween;

    // Object transform tweens (for v3 format multi-position objects)
    private class ObjectTransformTween
    {
        public int      ObjIndex           { get; set; }
        public int      PosIdx             { get; set; }  // Transform array index to update
        public float    StartTime          { get; set; }
        public float    Duration           { get; set; }
        public Vector2  StartPos           { get; set; }
        public Vector2  EndPos             { get; set; }
        public Vector2  StartScale         { get; set; }
        public Vector2  EndScale           { get; set; }
        public Func<float, float> Ease     { get; set; } = (t) => t;
        public int      NewTransformIndex  { get; set; }
        public bool     UseBeatTime        { get; set; }  // If true, use beat-based timing instead of seconds
        public float    StartBeat          { get; set; }  // Starting beat (if UseBeatTime=true)
        public float    DurationBeats      { get; set; }  // Duration in beats (if UseBeatTime=true)
    }
    private readonly List<ObjectTransformTween> _tweens = new();
    
    // Track which objects have active tweens (so Game1 doesn't overwrite their positions)
    private readonly HashSet<int> _objectsWithActiveTweens = new();
    
    // Track which objects ever had position events (these are controlled by EventProcessor)
    private readonly HashSet<int> _objectsWithPositionEvents = new();
    
    // Track current song time for tween creation during FireEvent
    private float _currentSongTime = 0f;
    private float _currentBeat = 0f;  // Current beat position for beat-based tweens

    // Auto-camera: smoothly follows a target world position
    private float _autoCamTargetX, _autoCamTargetY, _autoCamTargetScale;
    private const float AUTO_CAM_SPEED = 2.0f;
    
    // Auto-camera cooldown: prevents camera from switching too quickly between characters
    private float _autoCamLastSwitchTime = 0f;
    private const float AUTO_CAM_SWITCH_COOLDOWN = 0.5f;  // Minimum 0.5 seconds between switches
    private int _autoCamLastStrumline = -1;  // Track which strumline camera is currently following

    public float AutoCamTargetX => _autoCamTargetX;
    public float AutoCamTargetY => _autoCamTargetY;

    public CameraState       Camera        { get; } = new();
    public List<ScreenFlash> ActiveFlashes { get; } = new();
    public bool              HasCameraEvents => _hasCameraEvents;

    // ══════════════════════════════════════════════════════════════════════════
    // HELPER: Get camera scale with proper format handling
    // ══════════════════════════════════════════════════════════════════════════
    /// <summary>
    /// Gets the effective camera scale, handling both nested and legacy formats.
    /// Nested format: scale is used directly
    /// Legacy format: scale is also used directly (inversion disabled)
    /// </summary>
    private static float GetCameraScale(SceneCamera cam)
    {
        float scale = (float)cam.Scale;
        
        // For nested format, use scale directly
        if (cam.UsesNestedPositionFormat)
        {
            return Math.Min(scale, 1.0f);  // Clamp to max 1.0
        }
        
        // For legacy format, use scale directly (inversion disabled)
        // Legacy cameras with scale < 0.6 were being inverted incorrectly
        return Math.Min(scale, 1.0f);  // Clamp to max 1.0
    }

    /// <summary>
    /// Check if an object has an active tween (so Game1 doesn't overwrite its position)
    /// </summary>
    public bool HasActiveTween(int objIndex) => _objectsWithActiveTweens.Contains(objIndex);
    
    /// <summary>
    /// Check if an object ever had position events (these objects are controlled by EventProcessor)
    /// </summary>
    public bool HasPositionEvents(int objIndex) => _objectsWithPositionEvents.Contains(objIndex);

    /// <summary>
    /// Whether the engine HUD (health bar, score, timer) should be visible.
    /// Controlled by GUI events (action_pack type "GUI" with visible=true/false).
    /// </summary>
    public bool HudVisible { get; private set; } = true;

    /// <summary>Fired when BarIconChange event occurs. Args: (target 0=opp/1=player, spriteName)</summary>
    public Action<int, string>? OnBarIconChange;
    /// <summary>Fired when PlayerHealth event occurs. Args: (delta -1..1, minHealth 0..1)</summary>
    public Action<float, float>? OnPlayerHealth;
    /// <summary>Fired when PlayerHealth op=set occurs. Args: (targetHealth 0..1, minHealth 0..1)</summary>
    public Action<float, float>? OnPlayerHealthSet;

    // Called by NoteRenderer when a note is hit — used for auto-camera
    // strumlineIndex: 0=player, 1=opponent
    public void NotifyNoteHit(int strumlineIndex)
    {
        if (_hasCameraEvents)
        {
            // Level has manual camera events - don't auto-move
            // Console.WriteLine($"[AutoCamera] Blocked: Manual camera mode active (_hasCameraEvents=true)");
            return;
        }

        var objs = _level.Scene.Objects;

        // Only move auto-camera for strumlines 0 (player) and 1 (opponent).
        if (strumlineIndex > 1) return;
        
        // COOLDOWN: Prevent camera from switching too quickly between characters
        // Only switch if enough time has passed OR if this is the first note hit
        float currentTime = _currentSongTime;
        if (_autoCamLastStrumline == strumlineIndex)
        {
            // Same strumline - always allow (keeps camera on current singer)
            _autoCamLastSwitchTime = currentTime;
        }
        else if (_autoCamLastStrumline >= 0 && (currentTime - _autoCamLastSwitchTime) < AUTO_CAM_SWITCH_COOLDOWN)
        {
            // Different strumline but cooldown not expired - ignore this note
            return;
        }
        else
        {
            // Different strumline and cooldown expired - allow switch
            _autoCamLastStrumline = strumlineIndex;
            _autoCamLastSwitchTime = currentTime;
        }

        // When multiple characters share the same strumline (e.g. one at the top
        // and one at the bottom of the scene), follow the one closest to where the
        // camera already is — this matches the character that is currently singing.
        SceneObject? best     = null;
        float            bestDist = float.MaxValue;
        foreach (var o in objs)
        {
            if ((int)o.StrumLine != strumlineIndex) continue;
            if (!o.Visible) continue;
            float dx   = o.X - Camera.X;
            float dy   = o.Y - Camera.Y;
            float dist = dx * dx + dy * dy;
            if (dist < bestDist) { bestDist = dist; best = o; }
        }
        // Fallback: any object on this strumline even if not visible
        if (best == null)
        {
            foreach (var o in objs)
            {
                if ((int)o.StrumLine != strumlineIndex) continue;
                float dx   = o.X - Camera.X;
                float dy   = o.Y - Camera.Y;
                float dist = dx * dx + dy * dy;
                if (dist < bestDist) { bestDist = dist; best = o; }
            }
        }
        if (best == null) return;

        // Find the camera that belongs to this strumline by checking which camera the character is assigned to
        var cams = _level.Scene.Cameras;
        
        // FNF/GameMaker convention:
        // - Camera 0 = Opponent (StrumLine 1)
        // - Camera 1 = Player (StrumLine 0)
        // So we look for objects in this strumline and check their assigned camera
        
        // First, check if the object we found has a valid camera assigned
        int camIdx = best.Camera;
        
        if (camIdx >= 0 && camIdx < cams.Count)
        {
            // Object has valid camera assignment - use it
            _autoCamTargetX = (float)cams[camIdx].X;
            _autoCamTargetY = (float)cams[camIdx].Y;
            float baseScale = GetCameraScale(cams[camIdx]);
            _autoCamTargetScale = baseScale;
            
            Console.WriteLine($"[AutoCamera] Strumline {strumlineIndex} → Camera {camIdx} (from object.Camera={best.Camera}) pos=({_autoCamTargetX:F1}, {_autoCamTargetY:F1}) scale={_autoCamTargetScale:F3} [cooldown active]");
        }
        else
        {
            // Object has no camera assigned (Camera=-1), use FNF convention as fallback:
            // StrumLine 0 (player) → Camera 1
            // StrumLine 1 (opponent) → Camera 0
            camIdx = strumlineIndex == 0 ? 1 : 0;
            // If the convention camera doesn't exist (e.g. single-camera levels), use the
            // first real camera instead of inventing one at the character's position.
            if (camIdx >= cams.Count) camIdx = 0;

            if (camIdx >= 0 && camIdx < cams.Count)
            {
                _autoCamTargetX = (float)cams[camIdx].X;
                _autoCamTargetY = (float)cams[camIdx].Y;
                float baseScale = GetCameraScale(cams[camIdx]);
                _autoCamTargetScale = baseScale;
                
                Console.WriteLine($"[AutoCamera] Strumline {strumlineIndex} → Camera {camIdx} (FNF convention fallback) pos=({_autoCamTargetX:F1}, {_autoCamTargetY:F1}) scale={_autoCamTargetScale:F3} [cooldown active]");
            }
            else
            {
                // Final fallback: use character position if no camera defined
                _autoCamTargetX = best.X;
                _autoCamTargetY = best.Y;
                float baseScale = cams.Count > 0 ? GetCameraScale(cams[0]) : 0.75f;
                _autoCamTargetScale = baseScale;
                
                Console.WriteLine($"[AutoCamera] Strumline {strumlineIndex} → FALLBACK to character pos=({_autoCamTargetX:F1}, {_autoCamTargetY:F1}) scale={_autoCamTargetScale:F3} [cooldown active]");
            }
        }
    }

    public EventProcessor(FnmmLevel level,
                          SpriteAnimator?[] objAnimators,
                          Dictionary<string, List<Microsoft.Xna.Framework.Graphics.Texture2D>> texCache)
    {
        _level        = level;
        _objAnimators = objAnimators;
        _texCache     = texCache;

        int maxObjs = Math.Max(75, objAnimators.Length);
        _objOpacity    = new float[maxObjs];
        _opacityTween  = new OpacityTween[maxObjs];
        
        // Initialize opacity from scene.json (respects initial opacity values)
        var objs = level.Scene.Objects;
        for (int i = 0; i < maxObjs; i++)
        {
            // Use initial opacity from scene.json, default to 1.0 if not specified
            float initialOpacity = i < objs.Count ? (objs[i].Opacity ?? 1f) : 1f;
            _objOpacity[i] = initialOpacity;
            _opacityTween[i] = new OpacityTween();
            
            // Also apply to animator if it exists
            if (i < objAnimators.Length && objAnimators[i] != null)
            {
                objAnimators[i]!.Opacity = initialOpacity;
            }
        }

        // Convert event times to seconds if needed, using the same rule as note times.
        // version >= 2.0 or bpm=0 → already in seconds. version < 2.0 with bpm > 0 → beats.
        if (!level.Data.TimesAreSeconds && level.Data.Bpm > 0f)
        {
            float bpm = level.Data.Bpm;
            foreach (var ev in level.Events.Events)
                ev.Time = ev.Time / bpm * 60f;
        }

        _level.Events.Events.Sort((a, b) => a.Time.CompareTo(b.Time));

        // Log ActionPacks count
        Console.WriteLine($"[EventProcessor] Loaded {level.Events.ActionPacks.Count} ActionPacks");

        var cams = level.Scene.Cameras;
        Console.WriteLine($"[EventProcessor] Level has {cams.Count} camera(s) defined in scene.json");
        
        // Check if this level has any CameraSet events (for logging purposes only)
        int cameraSetCount = level.Events.Events.Count(ev => ev.Actions.Any(a => a.Type == "CameraSet"));
        Console.WriteLine($"[EventProcessor] Found {cameraSetCount} CameraSet events in level");

        // CAMERA AUTO LOGIC:
        // - Start with auto-camera ENABLED by default (if 2+ cameras exist)
        // - Only disable if there's just 1 camera (static shot design)
        // - CameraSet events can disable auto-camera when they execute (not at init)
        _hasCameraEvents = false;  // Start with auto-camera enabled

        // ──────────────────────────────────────────────────────────────────────
        // CAMERA SCALE COMPATIBILITY FIX
        // Legacy format: {x, y, scale} where scale ~0.4-0.5 needs inversion (scale → 1/scale)
        // New format: {position: {x, y}, scale} where scale is used directly
        // 
        // Detection: Check if camera uses UsesNestedPositionFormat flag (set by FnmmLoader)
        // Only apply scale inversion for legacy format cameras (flat {x, y, scale})
        // ──────────────────────────────────────────────────────────────────────
        // DISABLED: Legacy camera compatibility transform was inverting scale incorrectly
        // Levels with scale < 0.6 (close camera) were being inverted to scale > 1.0 (far camera)
        /*
        bool hasLegacyCamera = cams.Any(c => !c.UsesNestedPositionFormat && c.Scale < 0.6f);
        if (hasLegacyCamera)
        {
            Console.WriteLine($"[EventProcessor] Detected legacy camera format (flat {{x, y, scale}} with scale < 0.6)");
            Console.WriteLine($"[EventProcessor] Applying compatibility transform: scale → 1/scale for legacy cameras");
            foreach (var cam in cams.Where(c => !c.UsesNestedPositionFormat && c.Scale < 0.6f))
            {
                float oldScale = cam.Scale;
                float newScale = 1.0f / Math.Max(0.1f, oldScale);
                newScale = Math.Min(newScale, 1.0f);
                cam.Scale = newScale;
                Console.WriteLine($"[EventProcessor] Camera scale adjusted: {oldScale:F3} → {newScale:F3} (clamped to max 1.0)");
            }
        }
        */
        
        // Log camera format info
        foreach (var cam in cams)
        {
            string format = cam.UsesNestedPositionFormat ? "nested" : "legacy";
            Console.WriteLine($"[EventProcessor] Camera at ({cam.X}, {cam.Y}) scale={cam.Scale:F3} format={format}");
        }

        // Always start with cams[1] (player camera) as default if available, otherwise cams[0].
        // cams[0] = opponent, cams[1] = player (FNF convention)
        int defaultCam = cams.Count >= 2 ? 1 : 0;
        if (cams.Count > 0)
        {
            // Get scale with proper format handling
            float clampedScale = GetCameraScale(cams[defaultCam]);
            
            Camera.X     = Camera.TargetX     = (float)cams[defaultCam].X;
            Camera.Y     = Camera.TargetY     = (float)cams[defaultCam].Y;
            Camera.Scale = Camera.TargetScale = clampedScale;
            Camera.BaseScale = clampedScale;
            Camera.ClampPosition();  // Clamp initial position to world bounds
            _autoCamTargetX     = (float)cams[defaultCam].X;
            _autoCamTargetY     = (float)cams[defaultCam].Y;
            _autoCamTargetScale = clampedScale;
        }

        // Only disable auto-camera if there's just 1 camera (static shot design)
        // CameraSet events will control auto-camera activation/deactivation during gameplay
        if (cams.Count < 2)
        {
            _hasCameraEvents = true;  // Disable auto-camera for single-camera levels
            Console.WriteLine($"[EventProcessor] Single camera detected: Auto-camera disabled");
        }
        else
        {
            // Auto-camera enabled by default for multi-camera levels
            _hasCameraEvents = false;
            Console.WriteLine($"[EventProcessor] Auto-camera enabled by default ({cams.Count} cameras available)");
        }
    }

    /// <summary>
    /// Applies only the instantaneous (transitionTime=0) actions from events at time=0.
    /// Called during SetupLevel so objects start in the correct visible/opacity state
    /// without starting long-running transitions that would be offset by load time.
    /// </summary>
    public void ApplyInitialState()
    {
        foreach (var ev in _level.Events.Events)
        {
            if (ev.Time > 0f) break;
            // Apply ALL actions at time=0 — both instant and transitional.
            // Transitional ones (like long opacity fades) need to start at time=0.
            FireEvent(ev);
        }
        // Advance _nextEventIndex past all time=0 events so they don't fire again
        // when _songTime reaches 0 during gameplay.
        _nextEventIndex = 0;
        while (_nextEventIndex < _level.Events.Events.Count &&
               _level.Events.Events[_nextEventIndex].Time <= 0f)
            _nextEventIndex++;
    }

    public void Update(float songTime, float dt)
    {
        _currentSongTime = songTime;  // Store for use in FireEvent
        _currentBeat = _level.Data != null ? songTime * _level.Data.EffectiveBpm / 60f : 0f;  // Calculate current beat
        var events = _level.Events.Events;
        while (_nextEventIndex < events.Count &&
               events[_nextEventIndex].Time <= songTime)
        {
            FireEvent(events[_nextEventIndex]);
            _nextEventIndex++;
        }

        if (_hasCameraEvents)
        {
            Camera.Update(dt, _currentBeat);
        }
        else
        {
            // Auto-camera: FNF-style smooth lerp toward the singer's position.
            // Reduced lerpRate from 12.0 to 4.5 for smoother camera transitions
            // This makes the camera reach the target smoothly without brusque jumps
            // (reaches ~90% in ~0.5 seconds, ~98% in ~0.85 seconds)
            const float lerpRate = 4.5f;
            float speed = 1f - MathF.Exp(-lerpRate * dt);
            Camera.X     = MathHelper.Lerp(Camera.X,     _autoCamTargetX,     speed);
            Camera.Y     = MathHelper.Lerp(Camera.Y,     _autoCamTargetY,     speed);
            
            // CRITICAL: Only lerp scale towards auto-camera target if:
            // 1. No active scale transition (from CameraZoom)
            // 2. Scale differs from target
            // This preserves CameraZoom transitions during auto-camera mode
            bool hasActiveScaleTransition = Camera.ScaleTransTime > 0f && Camera.ScaleTransElapsed < Camera.ScaleTransTime;
            if (!hasActiveScaleTransition && Math.Abs(Camera.Scale - _autoCamTargetScale) > 0.001f)
            {
                Camera.Scale = MathHelper.Lerp(Camera.Scale, _autoCamTargetScale, speed);
            }
            Camera.Update(dt, _currentBeat);  // handles shake offset and scale transitions
        }

        // Update opacity tweens
        for (int i = 0; i < _opacityTween.Length; i++)
        {
            var tween = _opacityTween[i];
            if (!tween.IsActive) continue;
            
            // Calculate elapsed based on timing mode
            float elapsed, duration;
            if (tween.UseBeatTime)
            {
                elapsed = _currentBeat - tween.StartBeat;
                duration = tween.DurationBeats;
            }
            else
            {
                tween.Elapsed += dt;
                elapsed = tween.Elapsed;
                duration = tween.Duration;
            }
            
            float t = Math.Min(elapsed / duration, 1f);
            _objOpacity[i] = MathHelper.Lerp(tween.From, tween.To, t);
            if (_objAnimators[i] != null)
                _objAnimators[i]!.Opacity = _objOpacity[i];
            
            if (tween.IsDone(elapsed))
            {
                // Tween complete
                _objOpacity[i] = tween.To;
                tween.Duration = 0f; // Mark as inactive
                if (_objAnimators[i] != null)
                    _objAnimators[i]!.Opacity = tween.To;
            }
        }

        // Update transform tweens (v3 format multi-position objects)
        // Also update the set of objects with active tweens
        _objectsWithActiveTweens.Clear();
        
        for (int i = _tweens.Count - 1; i >= 0; i--)
        {
            var tween = _tweens[i];
            
            // Calculate elapsed time based on timing mode
            float elapsed, duration;
            if (tween.UseBeatTime)
            {
                // Beat-based timing: calculate elapsed beats
                elapsed = _currentBeat - tween.StartBeat;
                duration = tween.DurationBeats;
            }
            else
            {
                // Time-based timing: calculate elapsed seconds
                elapsed = songTime - tween.StartTime;
                duration = tween.Duration;
            }
            
            if (elapsed >= duration)
            {
                // Tween complete - snap to final position and update transform index
                var objs = _level.Scene.Objects;
                if (tween.ObjIndex < objs.Count && tween.NewTransformIndex >= 0)
                {
                    // Update transform index FIRST so GetPosition() returns correct value
                    objs[tween.ObjIndex].CurrentTransformIndex = tween.NewTransformIndex;
                }
                
                // Then update animator to final position
                if (_objAnimators[tween.ObjIndex] != null)
                {
                    _objAnimators[tween.ObjIndex]!.Position = tween.EndPos;
                    _objAnimators[tween.ObjIndex]!.Scale = tween.EndScale;
                }
                
                // Remove tween (this will cause Game1 to start reading from GetPosition() again)
                _tweens.RemoveAt(i);
            }
            else
            {
                // Interpolate
                float t = tween.Ease(elapsed / duration);
                if (_objAnimators[tween.ObjIndex] != null)
                {
                    var pos = Vector2.Lerp(tween.StartPos, tween.EndPos, t);
                    var scale = Vector2.Lerp(tween.StartScale, tween.EndScale, t);
                    _objAnimators[tween.ObjIndex]!.Position = pos;
                    _objAnimators[tween.ObjIndex]!.Scale = scale;
                    
                    // Mark this object as having an active tween
                    _objectsWithActiveTweens.Add(tween.ObjIndex);
                }
            }
        }

        for (int i = ActiveFlashes.Count - 1; i >= 0; i--)
        {
            ActiveFlashes[i].Elapsed += dt;
            if (ActiveFlashes[i].Done) ActiveFlashes.RemoveAt(i);
        }
    }

    /// <summary>
    /// Jumps the event processor to the given song time.
    /// Replays all events up to targetTime instantly so object states
    /// (visibility, opacity, sprite, camera) are correct at the new position.
    /// </summary>
    public void SeekTo(float targetTime)
    {
        _currentSongTime = targetTime;  // Store for use in FireEvent during seek
        
        // Update current beat for beat-time system
        if (_level.Data != null && _level.Data.EffectiveBpm > 0f)
        {
            _currentBeat = targetTime * _level.Data.EffectiveBpm / 60f;
        }
        
        // Reset camera to initial state (same as in constructor)
        // CRITICAL FIX: Use default camera (cams[1] for player if available, else cams[0])
        // This matches the initialization logic in the constructor
        var cams = _level.Scene.Cameras;
        if (cams.Count > 0)
        {
            int defaultCam = cams.Count >= 2 ? 1 : 0;
            var cam = cams[defaultCam];
            float clampedScale = GetCameraScale(cam);
            
            Camera.X     = Camera.TargetX     = (float)cam.X;
            Camera.Y     = Camera.TargetY     = (float)cam.Y;
            Camera.Scale = Camera.TargetScale = clampedScale;
            Camera.BaseScale = clampedScale;
            _autoCamTargetX     = (float)cam.X;
            _autoCamTargetY     = (float)cam.Y;
            _autoCamTargetScale = clampedScale;
            
            Console.WriteLine($"[SeekTo] Reset camera to cam[{defaultCam}]: pos=({cam.X:F1},{cam.Y:F1}) scale={clampedScale:F3} BaseScale={clampedScale:F3}");
        }
        
        // Reset all objects to initial state before replaying events
        // so we don't get double-applied visibility/opacity/sprite changes
        foreach (var obj in _level.Scene.Objects)
            obj.ResetToInitialState();

        // Reset all animator states to match initial object state
        var objs = _level.Scene.Objects;
        for (int i = 0; i < objs.Count && i < _objAnimators.Length; i++)
        {
            var anim = _objAnimators[i];
            if (anim == null) continue;
            anim.Visible  = objs[i].Visible;
            // Use initial opacity from scene.json, default to 1.0 if not specified
            float initialOpacity = objs[i].Opacity ?? 1f;
            anim.Opacity  = initialOpacity;
            anim.TintColor = Microsoft.Xna.Framework.Color.White;
        }

        // Reset opacity tweens to initial scene values
        for (int i = 0; i < _opacityTween.Length; i++)
        {
            float initialOpacity = i < objs.Count ? (objs[i].Opacity ?? 1f) : 1f;
            _opacityTween[i].From = initialOpacity;
            _opacityTween[i].To = initialOpacity;
            _opacityTween[i].Elapsed = 0f;
            _opacityTween[i].Duration = 0f;
            _opacityTween[i].UseBeatTime = false;
        }
        for (int i = 0; i < _objOpacity.Length; i++)
        {
            _objOpacity[i] = i < objs.Count ? (objs[i].Opacity ?? 1f) : 1f;
        }

        // Clear transform tweens
        _tweens.Clear();

        // Reset HUD visibility to default
        HudVisible = true;

        // Clear active flashes
        ActiveFlashes.Clear();

        _nextEventIndex = 0;
        var events = _level.Events.Events;
        
        // Execute all events up to targetTime to ensure correct state
        while (_nextEventIndex < events.Count &&
               events[_nextEventIndex].Time <= targetTime)
        {
            var ev = events[_nextEventIndex];
            
            // Skip pure visual effects during seek (flashes, shakes)
            // but process ALL state-changing events
            bool hasVisualOnly = ev.Actions.All(a =>
                a.Type == "ScreenFlash" || a.Type == "CameraShake");
            
            if (!hasVisualOnly)
            {
                // Execute the event normally (will apply all state changes)
                FireEvent(ev);
            }
            
            _nextEventIndex++;
        }
    }

    private void FireEvent(FnmmEvent ev)
    {
        var objs = _level.Scene.Objects;
        var cams = _level.Scene.Cameras;
        
        // Store event's time for delay compensation in Fixed object tweens
        float eventTime = ev.Time;
        
        foreach (var action in ev.Actions)
        {
            int objIdx = action.Props.Object.HasValue ? (int)action.Props.Object.Value : -1;

            // Track unknown event types for the log
            if (!KnownTypes.Contains(action.Type) && !string.IsNullOrEmpty(action.Type))
            {
                if (_unknownTypes.Add(action.Type))  // Add returns true only on first insertion
                    Console.WriteLine($"[Event] UNKNOWN type: \"{action.Type}\" — props: {System.Text.Json.JsonSerializer.Serialize(action.Props)}");
            }

            switch (action.Type)
            {
                // ── Object index 0-74 ────────────────────────────────────────

                case "ObjectChartChange":
                    // Reassigns an object to a different strumline at runtime.
                    // Game1's OnNoteHit/OnNoteMiss callbacks read StrumLine dynamically,
                    // so this takes effect immediately on the next note hit.
                    if (ValidObj(objIdx) && objIdx < objs.Count)
                    {
                        float oldStrumLine = objs[objIdx].StrumLine;
                        float newStrumLine = action.Props.StrumLine ?? -1f;
                        
                        // CRITICAL FIX: When strumLine changes, force return to idle immediately
                        // But ONLY for objects that are being replaced, not the one changing
                        if (oldStrumLine != newStrumLine)
                        {
                            // Reset all objects on the OLD strumLine (they're losing this strumLine)
                            // EXCEPT the object that's changing
                            if (oldStrumLine >= 0)
                            {
                                for (int i = 0; i < objs.Count; i++)
                                {
                                    if (i != objIdx && objs[i].StrumLine == oldStrumLine && _objAnimators[i] != null)
                                    {
                                        var anim = _objAnimators[i]!;
                                        
                                        // ONLY reset if NOT set by an event
                                        if (!anim.IsSetByEvent)
                                        {
                                            anim.HoldUntil = -1f;
                                            anim.SetSingTimer(0f);
                                            // Force idle immediately - they lost their strumLine
                                            anim.SetAnimation(SpriteAnimator.IdleAnim);
                                        }
                                    }
                                }
                            }
                            
                            // Reset the object that's changing strumLine
                            if (_objAnimators[objIdx] != null)
                            {
                                var anim = _objAnimators[objIdx]!;
                                
                                // Like FMM, the object whose type changes always restarts on idle,
                                // even over an event animation (which is released). Legacy levels
                                // put changetype BEFORE changeanim in the same event for this reason
                                // (e.g. PARADIGM 83.744s: changesprite wormalt keeps anim 6, then
                                // changetype back to player must drop it to idle).
                                anim.HoldUntil = -1f;
                                anim.SetSingTimer(0f);
                                anim.SetAnimation(SpriteAnimator.IdleAnim, setByEvent: false);
                            }
                            
                            // Reset all objects on the NEW strumLine (they're getting replaced)
                            // EXCEPT the object that's changing
                            if (newStrumLine >= 0)
                            {
                                for (int i = 0; i < objs.Count; i++)
                                {
                                    if (i != objIdx && objs[i].StrumLine == newStrumLine && _objAnimators[i] != null)
                                    {
                                        var anim = _objAnimators[i]!;
                                        
                                        // ONLY reset if NOT set by an event
                                        if (!anim.IsSetByEvent)
                                        {
                                            anim.HoldUntil = -1f;
                                            anim.SetSingTimer(0f);
                                            anim.SetAnimation(SpriteAnimator.IdleAnim);
                                        }
                                    }
                                }
                            }
                        }
                        
                        // Now change the strumLine
                        objs[objIdx].StrumLine = newStrumLine;
                        
                        // CRITICAL: Update IsCharacter flag based on new strumLine
                        // Objects moving from strumLine -1 to >= 0 become characters
                        // Objects moving from >= 0 to -1 become background objects
                        if (_objAnimators[objIdx] != null)
                        {
                            _objAnimators[objIdx]!.IsCharacter = newStrumLine >= 0f;
                        }
                    }
                    break;

                case "ObjectAnimationSet":
                    if (ValidObj(objIdx))
                    {
                        int anim = (int)(action.Props.Anim ?? 0);
                        
                        // When setting to idle (anim 0), don't mark as event-controlled
                        // This allows notes to override idle animations normally
                        bool setByEvent = (anim != 0);
                        
                        Console.WriteLine($"[ObjectAnimationSet] t={_currentSongTime:F2} Obj {objIdx} ({objs[objIdx].Spr}) → anim={anim} (setByEvent={setByEvent})");
                        
                        // Pass setByEvent based on whether it's an idle animation. Notes of this
                        // moment leave it alone, later notes interrupt it (BlocksNoteAt).
                        _objAnimators[objIdx]?.SetAnimation(anim, setByEvent: setByEvent,
                            eventTime: _actionPackNoteTime ?? eventTime);
                    }
                    break;

                case "ObjectVisibilitySet":
                    if (ValidObj(objIdx))
                    {
                        bool vis = action.Props.Value ?? true;
                        if (objIdx < objs.Count) objs[objIdx]!.Visible = vis;
                        if (_objAnimators[objIdx] != null)
                        {
                            _objAnimators[objIdx]!.Visible = vis;
                            Console.WriteLine($"[ObjectVisibilitySet] t={_currentSongTime:F2} Obj {objIdx} ({objs[objIdx]!.Spr}) → visible={vis}");
                        }
                    }
                    break;

                case "ObjectOpacitySet":
                    if (ValidObj(objIdx))
                    {
                        float targetOpacity = action.Props.ValueFloat ?? 1f;
                        float durationOriginal = action.Props.TransitionTime ?? 0f;
                        bool isSeconds = (action.Props.TransitionTimeUnit ?? "seconds") == "seconds";
                        float duration = durationOriginal;
                        
                        // Convert beats to seconds if needed (for logging only)
                        if (!isSeconds && _level.Data != null)
                            duration = BeatsToSeconds(durationOriginal, _level.Data.EffectiveBpm);

                        string objName = objIdx < objs.Count ? objs[objIdx]!.Spr : $"obj{objIdx}";
                        
                        if (duration <= 0f)
                        {
                            _objOpacity[objIdx] = targetOpacity;
                            _opacityTween[objIdx].From = targetOpacity;
                            _opacityTween[objIdx].To = targetOpacity;
                            _opacityTween[objIdx].Duration = 0f;
                            if (_objAnimators[objIdx] != null)
                                _objAnimators[objIdx]!.Opacity = targetOpacity;
                            Console.WriteLine($"[ObjectOpacitySet] t={eventTime:F2} Obj {objIdx} ({objName}) ␦ opacity={targetOpacity:F2} (instant)");
                        }
                        else
                        {
                            var tween = _opacityTween[objIdx];
                            tween.From = _objOpacity[objIdx];
                            tween.To = targetOpacity;
                            tween.Elapsed = 0f;
                            tween.Duration = duration;
                            tween.UseBeatTime = !isSeconds;
                            tween.StartBeat = _currentBeat;
                            tween.DurationBeats = durationOriginal;
                            
                            string timeMode = !isSeconds ? "beats" : "seconds";
                            Console.WriteLine($"[ObjectOpacitySet] t={eventTime:F2} Obj {objIdx} ({objName}) ␦ opacity {_objOpacity[objIdx]:F2}→{targetOpacity:F2} dur={duration:F2}s ({durationOriginal:F2}{(isSeconds ? "s" : "b")}) [mode={timeMode}]");
                        }
                    }
                    break;

                case "ObjectSpriteSet":
                    if (ValidObj(objIdx) && action.Props.Sprite != null)
                    {
                        string newSpr = action.Props.Sprite;
                        // Update scene object's current sprite name
                        if (objIdx < objs.Count) objs[objIdx]!.Spr = newSpr;

                        // Swap animator to use the new sprite's textures
                        if (_level.Sprites.TryGetValue(newSpr, out var sprData) &&
                            _texCache.TryGetValue(newSpr, out var textures))
                        {
                            // Preserve position/scale/color/opacity/fixed state from the old animator
                            // Use GetPosition()/GetScale() to respect transform arrays when old animator is absent
                            var oldAnim  = _objAnimators[objIdx];
                            var objPos   = objIdx < objs.Count ? objs[objIdx]!.GetPosition() : new Vec2 { X = 0, Y = 0 };
                            var objScale = objIdx < objs.Count ? objs[objIdx]!.GetScale() : new Vec2 { X = 1, Y = 1 };
                            bool vis     = objIdx < objs.Count ? objs[objIdx]!.Visible : true;
                            Color tint   = oldAnim?.TintColor ?? Color.White;
                            float opacity = oldAnim?.Opacity ?? 1.0f;
                            bool isChar  = objIdx < objs.Count && objs[objIdx]!.StrumLine >= 0f;
                            bool isFixed = oldAnim?.IsFixed ?? (objIdx < objs.Count && objs[objIdx]!.Fixed);
                            int renderW  = oldAnim?.RenderWidth ?? 1280;
                            int renderH  = oldAnim?.RenderHeight ?? 720;

                            Vector2 pos;
                            if (oldAnim != null)
                            {
                                pos = oldAnim.Position;
                            }
                            else if (isFixed && objIdx < objs.Count && objs[objIdx]!.Transform is { Count: > 0 } &&
                                     objPos.X >= 0f && objPos.X <= 1f && objPos.Y >= 0f && objPos.Y <= 1f)
                            {
                                pos = new Vector2(objPos.X * renderW, objPos.Y * renderH);
                            }
                            else
                            {
                                pos = new Vector2(objPos.X, objPos.Y);
                            }

                            var scale = oldAnim?.Scale ?? new Vector2(objScale.X, objScale.Y);

                            // Preserve event control state and animation info: levels do "alts" by
                            // swapping sprites mid-song (e.g. HEART mark ↔ markalt), so the current
                            // pose/hold must carry over to the new sprite.
                            bool wasSetByEvent = oldAnim?.IsSetByEvent ?? false;
                            float eventLockTime = oldAnim?.EventTime ?? -1f;
                            int currentAnim = oldAnim?.CurrentAnimIndex ?? 0;
                            float holdUntil = oldAnim?.HoldUntil ?? -1f;

                            var newAnimator = new SpriteAnimator(
                                newSpr, sprData.Def, textures, pos, scale)
                            {
                                Bpm             = _level.Data!.EffectiveBpm,
                                Visible         = vis,
                                TintColor       = tint,
                                Opacity         = opacity,
                                IsCharacter     = isChar,
                                IsBG            = sprData.Def.Type == 2.0f,
                                LevelVersion    = _level.Data.Version,
                                IsFixed         = isFixed,
                                FixedPixelScale = oldAnim?.FixedPixelScale ?? 0f,
                                RenderWidth     = renderW,
                                RenderHeight    = renderH,
                            };
                            _objAnimators[objIdx] = newAnimator;

                            // Restore animation state on the new sprite
                            if (newAnimator.AnimCount > 0)
                            {
                                int safeAnim = Math.Clamp(currentAnim, 0, newAnimator.AnimCount - 1);
                                newAnimator.SetAnimation(safeAnim, setByEvent: wasSetByEvent, eventTime: eventLockTime);
                                newAnimator.HoldUntil = holdUntil;

                                Console.WriteLine($"[ObjectSpriteSet] t={_currentSongTime:F2} Obj {objIdx} ({objs[objIdx]!.Spr}) → preserved anim={safeAnim} IsSetByEvent={wasSetByEvent} IsFixed={isFixed}");
                            }
                        }
                    }
                    break;

                case "ObjectTransformSet":
                    // Changes the transform index of an object to switch between
                    // multiple positions/scales defined in scene.json
                    if (ValidObj(objIdx) && objIdx < objs.Count)
                    {
                        // Mark this object as having position events (EventProcessor controls it)
                        _objectsWithPositionEvents.Add(objIdx);
                        
                        var obj = objs[objIdx];
                        
                        // Check if object has multiple transforms
                        if (obj.Transform == null || obj.Transform.Count == 0)
                        {
                            // Object doesn't have multiple transforms, ignore event
                            break;
                        }

                        int posIdx = (int)(action.Props.Position ?? 0f);
                        if (posIdx < 0 || posIdx >= obj.Transform!.Count)
                        {
                            // Invalid position index
                            break;
                        }

                        float durationOriginal = action.Props.Duration ?? 0f;
                        bool isSeconds = (action.Props.TimeUnit ?? "seconds") == "seconds";
                        float duration = durationOriginal;
                        if (!isSeconds && _level.Data != null)
                            duration = BeatsToSeconds(durationOriginal, _level.Data.EffectiveBpm);

                        if (duration <= 0f)
                        {
                            // Instant change
                            obj.CurrentTransformIndex = posIdx;
                            if (_objAnimators[objIdx] != null)
                            {
                                var animator = _objAnimators[objIdx]!;
                                var newPos = obj.GetPosition();
                                var newScale = obj.GetScale();
                                animator.Position = new Vector2(newPos.X, newPos.Y);
                                animator.Scale = new Vector2(newScale.X, newScale.Y);
                            }
                            // Cancel any active tween for this object
                            CancelObjectTween(objIdx);
                        }
                        else
                        {
                            // Interpolated change
                            if (_objAnimators[objIdx] != null)
                            {
                                var targetTransform = obj.Transform[posIdx];
                                var targetPos = new Vector2(targetTransform.Position.X, targetTransform.Position.Y);
                                var targetScale = new Vector2(targetTransform.Scale.X, targetTransform.Scale.Y);
                                
                                // Check if there's already an active tween for this object
                                bool hasActiveTween = false;
                                float remainingTime = 0f;
                                
                                foreach (var existingTween in _tweens)
                                {
                                    if (existingTween.ObjIndex == objIdx)
                                    {
                                        remainingTime = (existingTween.StartTime + existingTween.Duration) - _currentSongTime;
                                        if (remainingTime > 0f)
                                        {
                                            hasActiveTween = true;
                                            break;
                                        }
                                    }
                                }
                                
                                // Tolerance: if remaining time is < 0.1 seconds, consider it done
                                const float TOLERANCE = 0.1f;
                                
                                if (hasActiveTween && remainingTime > TOLERANCE)
                                {
                                    // Active tween with significant time remaining
                                    // Cancel it and start new one from current position
                                    CancelObjectTween(objIdx);
                                }
                                
                                var startPos = _objAnimators[objIdx]!.Position;
                                var startScale = _objAnimators[objIdx]!.Scale;

                                string easeName = action.Props.Interpolation ?? "linear";
                                
                                var tween = new ObjectTransformTween
                                {
                                    ObjIndex     = objIdx,
                                    PosIdx       = posIdx,
                                    StartTime    = _currentSongTime,
                                    Duration     = duration,
                                    StartPos     = startPos,
                                    EndPos       = targetPos,
                                    StartScale   = startScale,
                                    EndScale     = targetScale,
                                    Ease         = GetEaseFunction(easeName),
                                    NewTransformIndex = posIdx,
                                    // Use beat-time for non-Fixed objects when duration is in beats
                                    UseBeatTime   = !obj.Fixed && !isSeconds,
                                    StartBeat     = _currentBeat,
                                    DurationBeats = durationOriginal,
                                };
                                _tweens.Add(tween);
                                
                                string objName = obj.Spr ?? objIdx.ToString();
                                string timeMode = !obj.Fixed && !isSeconds ? "beats" : "seconds";
                                Console.WriteLine($"[ObjectTransformSet] t={_currentSongTime:F2} Obj {objIdx} ({objName}) → transform={posIdx} dur={duration:F2}s ({durationOriginal:F2}{(isSeconds ? "s" : "b")}) [mode={timeMode}]");
                            }
                        }
                    }
                    break;

                case "ObjectPositionSet":
                    // Simple position change event (doesn't affect scale)
                    // Used in legacy levels or for direct position control
                    if (ValidObj(objIdx) && _objAnimators[objIdx] != null)
                    {
                        // Mark this object as having position events (EventProcessor controls it)
                        _objectsWithPositionEvents.Add(objIdx);
                        
                        // Get target position from action properties
                        // The position index references a transform if using v3 format
                        // Some levels use "transform" property name instead of "position"
                        int posIdx = (int)(action.Props.Position ?? action.Props.Transform ?? 0f);
                        
                        float durationOriginal = action.Props.TransitionTime ?? 0f;
                        bool isSeconds = (action.Props.TransitionTimeUnit ?? "seconds") == "seconds";
                        float duration = durationOriginal;
                        if (!isSeconds && _level.Data != null) duration = BeatsToSeconds(durationOriginal, _level.Data.EffectiveBpm);

                        var obj = objs[objIdx];
                        Vector2 targetPos;

                        // Check if object has transform array
                        if (obj.Transform != null && obj.Transform.Count > 0 && 
                            posIdx >= 0 && posIdx < obj.Transform.Count)
                        {
                            // Use transform array position
                            var transform = obj.Transform[posIdx];
                            targetPos = new Vector2(transform.Position.X, transform.Position.Y);
                            
                            // CRITICAL: If object is fixed, check if coordinates need conversion
                            // Use same logic as Game1.cs: check initial obj.X/Y to determine coordinate type
                            if (obj.Fixed)
                            {
                                // Use INITIAL coordinates (obj.X/Y) to determine if normalized, not Transform coords
                                bool isNormalized = obj.X >= 0f && obj.X <= 1f && obj.Y >= 0f && obj.Y <= 1f;
                                
                                if (isNormalized)
                                {
                                    // Normalized coordinates (0-1): convert to screen pixels
                                    int screenW = 1280; // Default width
                                    int screenH = 720;  // Default height
                                    targetPos = new Vector2(targetPos.X * screenW, targetPos.Y * screenH);
                                }
                                // else: pixel coordinates, use as-is
                            }
                        }
                        else
                        {
                            // Legacy or no transform array: use current position (no-op)
                            targetPos = _objAnimators[objIdx]!.Position;
                        }

                        string objName = obj.Spr ?? objIdx.ToString();
                        bool wasVisible = _objAnimators[objIdx]!.Visible;
                        if (duration <= 0f)
                        {
                            // Instant change - update position AND scale
                            _objAnimators[objIdx]!.Position = targetPos;
                            
                            // Update scale if object has Transform array
                            if (obj.Transform != null && obj.Transform.Count > 0 && 
                                posIdx >= 0 && posIdx < obj.Transform.Count)
                            {
                                var transform = obj.Transform[posIdx];
                                var transformScale = new Vector2(transform.Scale.X, transform.Scale.Y);
                                
                                // Use scale directly from Transform - it's already in the correct coordinate system
                                _objAnimators[objIdx]!.Scale = transformScale;
                            }
                            
                            Console.WriteLine($"[ObjectPositionSet] t={_currentSongTime:F2} Obj {objIdx} ({objName}) → pos=({targetPos.X:F3},{targetPos.Y:F3}) posIdx={posIdx} visible={wasVisible} (instant)");
                            // Cancel any active tween for this object
                            CancelObjectTween(objIdx);
                            
                            // CRITICAL FIX: Update CurrentTransformIndex so GetPosition() returns correct transform
                            // Game1.cs will read GetPosition() each frame and convert coordinates appropriately
                            if (objIdx < objs.Count)
                            {
                                if (obj.Transform != null && obj.Transform.Count > 0 && 
                                    posIdx >= 0 && posIdx < obj.Transform.Count)
                                {
                                    // Update the current transform index
                                    objs[objIdx].CurrentTransformIndex = posIdx;
                                    Console.WriteLine($"[ObjectPositionSet] DEBUG: Updated CurrentTransformIndex to {posIdx}, Fixed={obj.Fixed}");
                                }
                                else
                                {
                                    // Update X/Y directly for objects without Transform array
                                    objs[objIdx].X = targetPos.X;
                                    objs[objIdx].Y = targetPos.Y;
                                    Console.WriteLine($"[ObjectPositionSet] DEBUG: Updated obj.X/Y to ({targetPos.X:F3},{targetPos.Y:F3}), Fixed={obj.Fixed}");
                                }
                            }
                        }
                        else
                        {
                            // Interpolated change
                            Console.WriteLine($"[ObjectPositionSet] t={_currentSongTime:F2} Obj {objIdx} ({objName}) → pos=({targetPos.X:F3},{targetPos.Y:F3}) posIdx={posIdx} visible={wasVisible} dur={duration:F2}s");
                            Console.WriteLine($"[ObjectPositionSet] DEBUG: Starting tween from ({_objAnimators[objIdx]!.Position.X:F3},{_objAnimators[objIdx]!.Position.Y:F3}) to ({targetPos.X:F3},{targetPos.Y:F3})");
                            // Check if there's already an active tween for this object
                            bool hasActiveTween = false;
                            float remainingTime = 0f;
                            
                            for (int i = 0; i < _tweens.Count; i++)
                            {
                                var existingTween = _tweens[i];
                                if (existingTween.ObjIndex == objIdx)
                                {
                                    remainingTime = (existingTween.StartTime + existingTween.Duration) - _currentSongTime;
                                    if (remainingTime > 0f)
                                    {
                                        hasActiveTween = true;
                                        break;
                                    }
                                }
                            }
                            
                            // For Fixed objects: always cancel and create a single fused tween
                            // For non-Fixed objects: just cancel
                            if (hasActiveTween)
                            {
                                if (obj.Fixed && remainingTime > 0.01f)
                                {
                                    Console.WriteLine($"[TweenFuse] Obj {objIdx} ({objName}): Canceling old tween ({remainingTime:F3}s left), will fuse into new movement");
                                }
                                CancelObjectTween(objIdx);
                            }
                            
                            var startPos = _objAnimators[objIdx]!.Position;
                            var startScale = _objAnimators[objIdx]!.Scale;
                            var endScale = startScale; // Default: keep scale unchanged
                            
                            // If object has Transform array, get the target scale
                            if (obj.Transform != null && obj.Transform.Count > 0 && 
                                posIdx >= 0 && posIdx < obj.Transform.Count)
                            {
                                var transform = obj.Transform[posIdx];
                                var transformScale = new Vector2(transform.Scale.X, transform.Scale.Y);
                                
                                // Use scale directly from Transform - it's already in the correct coordinate system
                                endScale = transformScale;
                            }
                            
                            string easeName = action.Props.EasingType ?? "linear";
                            
                            // Start tween at event time for proper synchronization
                            // No artificial compensation - let events fire at correct times
                            float tweenStartTime = eventTime;
                            float tweenDuration = duration;
                            
                            var tween = new ObjectTransformTween
                            {
                                ObjIndex     = objIdx,
                                PosIdx       = posIdx,
                                StartTime    = tweenStartTime,
                                Duration     = tweenDuration,
                                StartPos     = startPos,
                                EndPos       = targetPos,
                                StartScale   = startScale,
                                EndScale     = endScale,
                                Ease         = GetEaseFunction(easeName),
                                NewTransformIndex = posIdx,
                                UseBeatTime   = !obj.Fixed && !isSeconds,
                                StartBeat     = _currentBeat,
                                DurationBeats = durationOriginal,
                            };
                            _tweens.Add(tween);
                            
                            if (remainingTime > 0.01f)
                            {
                                string timeMode = !obj.Fixed && !isSeconds ? "beats" : "seconds";
                                Console.WriteLine($"[SmoothFuse] Obj {objIdx} ({objName}): Fused animation using duration {duration:F2}s ({durationOriginal:F2}{(isSeconds ? "s" : "b")}) [mode={timeMode}]");
                            }
                        }
                    }
                    break;

                case "ObjectBlendColor":
                    if (ValidObj(objIdx) && _objAnimators[objIdx] != null)
                    {
                        byte r = Clamp255(action.Props.ColorR ?? 255f);
                        byte g = Clamp255(action.Props.ColorG ?? 255f);
                        byte b = Clamp255(action.Props.ColorB ?? 255f);
                        _objAnimators[objIdx]!.TintColor = new Color(r, g, b);
                    }
                    break;

                // ── Camera index 0-based (matches GameMaker/Funky Maker array) ──

                case "CameraSet":
                {
                    // mode="auto" → switch to auto-camera
                    if (action.Props.Mode == "auto")
                    {
                        _hasCameraEvents = false;
                        
                        Console.WriteLine($"[CameraSet] Switching to AUTO mode (_hasCameraEvents=false)");
                        
                        // If we're mid-transition, use the TARGET values as the starting point
                        // for auto-camera to avoid visual jumps
                        if (Camera!.TransTime > 0f && Camera!.TransElapsed < Camera!.TransTime)
                        {
                            _autoCamTargetX     = Camera!.TargetX;
                            _autoCamTargetY     = Camera!.TargetY;
                            _autoCamTargetScale = Camera!.TargetScale;
                            Console.WriteLine($"[CameraSet] Auto-camera starting from transition target: ({_autoCamTargetX:F1}, {_autoCamTargetY:F1})");
                        }
                        else
                        {
                            // Use current camera position as starting point
                            _autoCamTargetX     = Camera!.X;
                            _autoCamTargetY     = Camera!.Y;
                            _autoCamTargetScale = Camera!.Scale;
                            Console.WriteLine($"[CameraSet] Auto-camera starting from current pos: ({_autoCamTargetX:F1}, {_autoCamTargetY:F1})");
                        }
                        
                        // Cancel any active camera transition so it doesn't fight the auto-camera lerp
                        Camera.TransTime = 0f;
                        Camera.TransElapsed = 0f;
                        break;
                    }

                    // target is 0-based index into the cameras array
                    int camIdx = (int)(action.Props.Target ?? 0f);
                    if (camIdx < 0 || camIdx >= cams.Count)
                    {
                        // Out of range → fallback to auto
                        Console.WriteLine($"[CameraSet] WARNING: Camera target {camIdx} out of range (0-{cams.Count - 1}), falling back to AUTO mode");
                        _hasCameraEvents = false;
                        
                        // Same logic as mode="auto" above
                        if (Camera!.TransTime > 0f && Camera!.TransElapsed < Camera!.TransTime)
                        {
                            _autoCamTargetX     = Camera!.TargetX;
                            _autoCamTargetY     = Camera!.TargetY;
                            _autoCamTargetScale = Camera!.TargetScale;
                        }
                        else
                        {
                            _autoCamTargetX     = Camera!.X;
                            _autoCamTargetY     = Camera!.Y;
                            _autoCamTargetScale = Camera!.Scale;
                        }
                        
                        Camera.TransTime    = 0f;
                        Camera.TransElapsed = 0f;
                        break;
                    }

                    var cam = cams[camIdx];
                    float durationOriginal = action.Props.TransitionTime ?? 0f;
                    bool isSeconds = (action.Props.TransitionTimeUnit ?? "seconds") == "seconds";
                    float transTime = durationOriginal;
                    
                    // Convert beats to seconds if needed (for logging only)
                    if (!isSeconds && _level.Data != null)
                        transTime = BeatsToSeconds(durationOriginal, _level.Data.EffectiveBpm);

                    _hasCameraEvents = true;
                    
                    Console.WriteLine($"[CameraSet] MANUAL mode activated (_hasCameraEvents=true)");
                    
                    // Get scale with proper format handling
                    float clampedScale = GetCameraScale(cam);
                    
                    string timeMode = !isSeconds ? "beats" : "seconds";
                    Console.WriteLine($"[CameraSet] target={camIdx} pos=({cam.X:F1},{cam.Y:F1}) scale={clampedScale:F3} transTime={transTime:F2}s ({durationOriginal:F2}{(isSeconds ? "s" : "b")}) [mode={timeMode}] format={(cam.UsesNestedPositionFormat ? "nested" : "legacy")}");
                    
                    // ZOOM PRESERVATION: Only preserve zoom if there's an active CameraZoom EVENT
                    // Check IsZoomEvent flag to distinguish CameraZoom from regular CameraSet transitions
                    // This prevents false "zoom preservation" when switching between cameras with different base scales
                    float currentZoomRatio = 1.0f;
                    bool hasActiveZoomEvent = Camera != null && 
                                              Camera.IsZoomEvent && 
                                              Camera.ScaleTransTime > 0f && 
                                              Camera.ScaleTransElapsed < Camera.ScaleTransTime;
                    
                    if (hasActiveZoomEvent && Camera != null && Camera.BaseScale > 0.01f)
                    {
                        // Calculate current zoom as ratio: currentScale / baseScale
                        // Only preserve if there's an active CameraZoom event
                        currentZoomRatio = Camera.Scale / Camera.BaseScale;
                        
                        // Only preserve zoom if it's significantly different from 1.0
                        if (Math.Abs(currentZoomRatio - 1.0f) > 0.05f)
                        {
                            Console.WriteLine($"[CameraSet] Preserving zoom ratio {currentZoomRatio:F2}x (current={Camera.Scale:F3}, base={Camera.BaseScale:F3}) [active CameraZoom event]");
                        }
                        else
                        {
                            currentZoomRatio = 1.0f; // Reset to no zoom if very close to 1.0
                        }
                    }
                    
                    // Update base scale for new camera
                    if (Camera != null)
                        Camera.BaseScale = clampedScale;
                    
                    // Apply preserved zoom ratio to new camera's base scale
                    float targetScale = clampedScale * currentZoomRatio;
                    Console.WriteLine($"[CameraSet] New base scale={clampedScale:F3}, zoom ratio={currentZoomRatio:F2}x → target scale={targetScale:F3}");
                    
                    // SMART TRANSITION: If position isn't changing, only transition scale
                    // This prevents CameraSet from canceling active position transitions
                    // when legacy levels use CameraSet for zoom-only changes
                    float targetX = (float)cam.X;
                    float targetY = (float)cam.Y;
                    
                    bool positionChanging = Math.Abs(targetX - Camera!.TargetX) > 1f || 
                                           Math.Abs(targetY - Camera!.TargetY) > 1f;
                    bool scaleChanging = Math.Abs(targetScale - Camera!.TargetScale) > 0.01f;
                    
                    if (!positionChanging && scaleChanging && Camera!.PosTransTime > 0f)
                    {
                        // Position isn't changing AND there's an active position transition
                        // Only transition the scale to preserve the position animation
                        Camera.StartScaleTransition(
                            targetScale,
                            transTime,
                            action.Props.EasingType ?? "default",
                            !isSeconds,
                            _currentBeat,
                            durationOriginal);
                    }
                    else
                    {
                        // Normal full transition (position + scale)
                        Camera.StartTransition(
                            targetX, targetY, targetScale,
                            transTime,
                            action.Props.EasingType ?? "default",
                            !isSeconds,
                            _currentBeat,
                            durationOriginal);
                        Camera.ClampPosition();  // Clamp after setting transition targets
                    }
                    break;
                }

                case "CameraZoom":
                {
                    // CameraZoom applies zoom as a PERCENTAGE relative to BASE camera scale
                    // BaseScale is the original scale from scene.json (set by CameraSet)
                    // This ensures zoom always works from the camera's original scale,
                    // not from a previously zoomed scale
                    //
                    // Funky Maker semantics:
                    //   value=1.0 → 100% = keep base scale unchanged
                    //   value=0.0 → apply max zoom IN from base scale
                    //   value=0.5 → 50% zoom between max IN and base
                    //
                    // MonoGame: lower scale = zoom IN (closer), higher scale = zoom OUT (farther)
                    //
                    // Formula: targetScale = baseScale * zoomValue
                    //   value=1.0 → scale = baseScale (no zoom)
                    //   value=0.5 → scale = baseScale * 0.5 (zoomed in)
                    //   value=0.0 → scale = baseScale * 0.1 (max zoom in, clamped)
                    
                    float zoomValue = action.Props.ValueFloat ?? 1.0f;
                    float durationOriginal = action.Props.TransitionTime ?? 0f;
                    bool isSeconds = (action.Props.TransitionTimeUnit ?? "seconds") == "seconds";
                    float duration = durationOriginal;
                    
                    // Convert beats to seconds if needed (for logging only)
                    if (!isSeconds && _level.Data != null)
                        duration = BeatsToSeconds(durationOriginal, _level.Data.EffectiveBpm);
                    
                    // CRITICAL FIX: CameraZoom does NOT disable auto-camera
                    // It works in BOTH manual and auto-camera modes
                    // Don't set _hasCameraEvents = true here
                    
                    // Use BaseScale as the 100% reference (from scene.json camera)
                    // This ensures we always zoom from the camera's original scale,
                    // even if a previous CameraZoom modified the current scale
                    float baseScale = Camera?.BaseScale ?? 1.0f;
                    
                    // Use zoom value directly as percentage
                    // Clamp minimum to prevent extreme zoom
                    float zoomPercentage = Math.Max(0.1f, zoomValue);
                    
                    // Apply percentage to base scale
                    float targetScale = baseScale * zoomPercentage;
                    
                    string cameraMode = _hasCameraEvents ? "manual" : "auto";
                    string timeMode = !isSeconds ? "beats" : "seconds";
                    Console.WriteLine($"[CameraZoom] value={zoomValue:F2} baseScale={baseScale:F3} targetScale={targetScale:F3} dur={duration:F2}s ({durationOriginal:F2}{(isSeconds ? "s" : "b")}) [mode={timeMode}] [camera={cameraMode}]");
                    
                    // Update auto-camera target if in auto-camera mode
                    // This ensures auto-camera doesn't fight against the zoom
                    if (!_hasCameraEvents)
                    {
                        _autoCamTargetScale = targetScale;
                        Console.WriteLine($"[CameraZoom] Updated auto-camera target scale to {targetScale:F3}");
                    }
                    
                    // Use StartScaleTransition to preserve any active position transitions
                    Camera!.StartScaleTransition(
                        targetScale,
                        duration,
                        action.Props.EasingType ?? "default",
                        !isSeconds,
                        _currentBeat,
                        durationOriginal,
                        isZoomEvent: true); // Mark this as a CameraZoom event
                    break;
                }

                // ── Screen flash ─────────────────────────────────────────────

                case "BarIconChange":
                {
                    int iconTarget = (int)(action.Props.Target ?? 0f);
                    string? newSprite = action.Props.Sprite;
                    if (newSprite != null)
                        OnBarIconChange?.Invoke(iconTarget, newSprite);
                    break;
                }

                case "PlayerHealth":
                {
                    // operation: "add", "remove", or "set"
                    // value / minHealth use the FunkyMaker scale 0..30 (30 = full bar)
                    // → convert to the engine's 0..1
                    string op  = action.Props.Operation ?? "remove";
                    float  val = (action.Props.ValueFloat ?? 0f) / NoteSkins.FmmMaxHealth;
                    float  minH = action.Props.MinHealth.HasValue
                        ? action.Props.MinHealth.Value / NoteSkins.FmmMaxHealth
                        : 0f;
                    float delta = op switch
                    {
                        "add"    =>  val,
                        "remove" => -val,
                        "set"    =>  val,   // absolute set — Game1 handles via special value
                        _        => -val,
                    };
                    // Pack minHealth into the callback: positive delta = add/set, negative = remove
                    // For "set", we pass a special flag via a large negative sentinel
                    // Game1 checks: if delta >= -10f treat as normal delta, else it's a "set" op
                    // Simpler: fire two values — use a tuple-like encoding
                    // Actually cleanest: add a new event callback for "set"
                    if (op == "set")
                        OnPlayerHealthSet?.Invoke(val, minH);
                    else
                        OnPlayerHealth?.Invoke(delta, minH);
                    break;
                }

                case "RunActionPack":
                {
                    // Execute an action_pack by index (0-based) from the timeline
                    int packIdx = (int)(action.Props.ActionPack ?? -1f);
                    Console.WriteLine($"[RunActionPack] Executing ActionPack {packIdx} at time {_currentSongTime:F2}s");
                    FireActionPack(packIdx, _actionPackNoteTime); // nested in a note's pack: keep its time
                    break;
                }

                case "ScreenFlash":
                {
                    float dur = action.Props.Duration ?? 0.2f;
                    if (action.Props.TimeUnit == "beats" && dur > 0f)
                        dur = dur / _level.Data!.EffectiveBpm * 60f;

                    byte r = Clamp255(action.Props.ColorR ?? 255f);
                    byte g = Clamp255(action.Props.ColorG ?? 255f);
                    byte b = Clamp255(action.Props.ColorB ?? 255f);
                    float a = action.Props.Alpha ?? 0.5f;

                    ActiveFlashes.Add(new ScreenFlash
                    {
                        Color    = new Color(r, g, b) * a,
                        Duration = Math.Max(0.01f, dur),
                        Elapsed  = 0f,
                    });
                    break;
                }

                case "CameraShake":
                {
                    float strength = action.Props.Strength ?? 10f;
                    float delay    = action.Props.Delay    ?? 0.05f;
                    float shakes   = action.Props.Shakes   ?? 3f;
                    // Convert delay from beats if needed
                    if (action.Props.TimeUnit == "beats" && delay > 0f)
                        delay = delay / _level.Data!.EffectiveBpm * 60f;
                    Camera?.StartShake(strength, delay, shakes);
                    break;
                }

                case "GUI":
                    // GUI events control HUD visibility (health bar, score, etc.)
                    // Supports both {"visible": bool} and {"value": bool} prop formats.
                    HudVisible = action.Props.VisibleBool
                              ?? action.Props.Value
                              ?? true;
                    break;
            }
        }
    }

    /// <summary>
    /// Execute an action_pack by index (1-based, matching note properties).
    /// Called from NoteRenderer callbacks when onPressEvent/onMissEvent fires.
    /// </summary>
    public void FireActionPack(int packIndex, float? noteTime = null)
    {
        _actionPackNoteTime = noteTime;
        try { FireActionPackCore(packIndex); }
        finally { _actionPackNoteTime = null; }
    }

    /// <summary>
    /// Chart time of the note whose action pack is firing (null for timeline events / RunActionPack):
    /// animations set by the pack are protected from that note's moment only (SpriteAnimator.BlocksNoteAt).
    /// </summary>
    private float? _actionPackNoteTime;

    private void FireActionPackCore(int packIndex)
    {
        var packs = _level.Events.ActionPacks;
        Console.WriteLine($"[FireActionPack] Requested pack {packIndex} (0-based), total packs available: {packs.Count}");
        
        // packIndex is 0-based (standard array indexing)
        if (packIndex < 0 || packIndex >= packs.Count)
        {
            Console.WriteLine($"[FireActionPack] ERROR: Invalid pack index {packIndex} (valid range: 0-{packs.Count - 1})");
            Console.WriteLine($"[FireActionPack] NOTE: If action_packs are defined in events.json, ensure they're being loaded into FnmmLevel.Events.ActionPacks");
            return;
        }
        
        Console.WriteLine($"[FireActionPack] Firing pack {packIndex} with {packs[packIndex].Actions.Count} actions");
        
        foreach (var action in packs[packIndex].Actions)
        {
            // Use current song time so Fixed object delay compensation works correctly
            var fakeEvent = new FnmmEvent { Time = _currentSongTime };
            fakeEvent.Actions.Add(action);
            FireEvent(fakeEvent);
        }
    }

    // ── Helpers ──────────────────────────────────────────────────────────────

    // Object indices are 0-74
    private bool ValidObj(int idx) => idx >= 0 && idx < _objAnimators.Length;

    private static byte Clamp255(float v) => (byte)Math.Clamp(v, 0f, 255f);

    /// <summary>Converts beats to seconds using the level's effective BPM.</summary>
    private static float BeatsToSeconds(float beats, float bpm)
    {
        return beats / bpm * 60f;
    }

    /// <summary>Cancels any active transform tween for the specified object.</summary>
    private void CancelObjectTween(int objIdx)
    {
        for (int i = _tweens.Count - 1; i >= 0; i--)
        {
            if (_tweens[i].ObjIndex == objIdx)
            {
                _tweens.RemoveAt(i);
            }
        }
    }
    
    /// <summary>Gets the easing function by name.</summary>
    private static Func<float, float> GetEaseFunction(string name) => name.ToLower() switch
    {
        "linear"   => (t) => t,
        "quad"     => (t) => 1f - (1f - t) * (1f - t),  // ease-out: starts fast, decelerates smoothly
        "cubic"    => (t) => 1f - MathF.Pow(1f - t, 3f),  // ease-out: starts fast, decelerates smoothly
        "sine"     => (t) => MathF.Sin(t * MathF.PI / 2f),
        "expo"     => (t) => t >= 1f ? 1f : 1f - MathF.Pow(2f, -10f * t),
        "circ"     => EaseOutCirc,  // Standard ease-out: starts fast, decelerates smoothly
        "bounce"   => EaseBounce,
        "elastic"  => EaseElastic,
        _          => (t) => t,  // default to linear
    };
    
    private static float EaseBounce(float t)
    {
        if (t < 1f / 2.75f) return 7.5625f * t * t;
        if (t < 2f / 2.75f) { t -= 1.5f / 2.75f;   return 7.5625f * t * t + 0.75f; }
        if (t < 2.5f / 2.75f) { t -= 2.25f / 2.75f; return 7.5625f * t * t + 0.9375f; }
        t -= 2.625f / 2.75f; return 7.5625f * t * t + 0.984375f;
    }
    
    private static float EaseElastic(float t)
    {
        if (t <= 0f) return 0f;
        if (t >= 1f) return 1f;
        return MathF.Pow(2f, -10f * t) * MathF.Sin((t - 0.075f) * (2f * MathF.PI) / 0.3f) + 1f;
    }
    
    private static float EaseInOutCirc(float t)
    {
        // Ease-in-out circular: smooth acceleration and deceleration
        // First half: ease-in (slow start), second half: ease-out (slow end)
        if (t < 0.5f)
        {
            // Ease-in: 0.5 * (1 - sqrt(1 - (2t)^2))
            return 0.5f * (1f - MathF.Sqrt(1f - 4f * t * t));
        }
        else
        {
            // Ease-out: 0.5 * (sqrt(1 - (2t-2)^2) + 1)
            float x = 2f * t - 2f;
            return 0.5f * (MathF.Sqrt(1f - x * x) + 1f);
        }
    }
    
    private static float EaseOutCirc(float t)
    {
        // Standard ease-out circular (like easings.net and GameMaker)
        // Starts fast, decelerates smoothly at the end
        // Formula: sqrt(1 - (t - 1)^2)
        return MathF.Sqrt(1f - MathF.Pow(t - 1f, 2f));
    }

    /// <summary>
    /// Write unknown event types to a log file next to the exe.
    /// Call this when the song ends.
    /// </summary>
    public void FlushUnknownLog(string levelName)
    {
        if (_unknownTypes.Count == 0) return;
        try
        {
            string logPath = System.IO.Path.Combine(
                AppContext.BaseDirectory, "unknown_events.log");
            var lines = new System.Collections.Generic.List<string>
            {
                $"=== {levelName} — {DateTime.Now:yyyy-MM-dd HH:mm:ss} ===",
            };
            foreach (var t in _unknownTypes.OrderBy(x => x))
                lines.Add($"  UNKNOWN EVENT TYPE: {t}");
            lines.Add("");
            System.IO.File.AppendAllLines(logPath, lines);
            Console.WriteLine($"[FunkyMakerPlayer] Unknown events logged to {logPath}");
        }
        catch { }
    }
}
