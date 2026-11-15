using System;
using System.Collections.Generic;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;

namespace FunkyMakerPlayer;

/// <summary>
/// Handles a single scene sprite with its animation data.
///
/// Sprite types:
///   1 – Multi-anim spritesheet: N PNGs (0.png…), each a horizontal strip of M frames.
///   2 – Single-anim spritesheet: 1 PNG, horizontal strip of N frames.
///   3 – Multi-frame: N PNGs, one per animation index, each a single frame.
///
/// Position semantics (Funky Maker):
///   Position is the world-space anchor point of the sprite.
///   The sprite.json offset defines how many pixels to shift the draw position
///   relative to that anchor, scaled by the final scale.
///   Draw position = Position - offset * |finalScale|
///
/// Animation speed:
///   spd_fps = true  → spd is literal FPS (frames per second).
///   spd_fps = false → spd is step-based: fps = spd * (bpm/60) * 4
///                     1 step = 1/4 beat. spd=2 at 120bpm → 16fps.
/// </summary>
public class SpriteAnimator
{
    // ── Public state ─────────────────────────────────────────────────────────
    public Vector2 Position;
    public Vector2 Scale;
    public bool    Visible     = true;
    public Color   TintColor   = Color.White;  // Color tint (from ObjectBlendColor)
    public float   Opacity     = 1.0f;         // Alpha/opacity (from ObjectOpacitySet)
    public string  SpriteName;
    public int     AnimCount        => _def.Data.Count;
    public int     CurrentAnimIndex => _currentAnim;
    public bool    IsSetByEvent => _setByEvent;  // Check if current animation was set by an event
    public float   CurrentSprScale  => _def.Data.Count > 0 ? _def.Data[_currentAnim].Scale : 1f;
    public Vector2 CurrentFrameSize
    {
        get
        {
            if (_def.Data.Count == 0 || _textures.Count == 0) return new Vector2(64, 64);
            var anim = _def.Data[_currentAnim];
            float dc = anim.DownscaleCompensation > 0f ? anim.DownscaleCompensation : 1f;
            if (anim.UsesPackedAtlas && anim.AtlasFrameRects != null && anim.AtlasFrameRects.Count > 0)
            {
                var r = anim.AtlasFrameRects[0];
                return new Vector2(r.Width * dc, r.Height * dc);
            }
            if (anim.IsSplit && anim.SplitOffset >= 0 && anim.SplitOffset < _textures.Count)
            {
                int w = anim.FrameWidth > 0 ? anim.FrameWidth : _textures[anim.SplitOffset].Width;
                return new Vector2(w * dc, _textures[anim.SplitOffset].Height * dc);
            }
            int sprType = (int)_def.Type;
            int texIdx  = sprType == 2 ? 0 : (anim.TextureIndex >= 0 ? anim.TextureIndex : _currentAnim);
            if (texIdx >= _textures.Count) return new Vector2(64, 64);
            var tex = _textures[texIdx];
            int fc  = GetFrameCount(_currentAnim);
            return new Vector2((fc > 0 ? tex.Width / fc : tex.Width) * dc, tex.Height * dc);
        }
    }
    public float   Bpm         = 120f;
    
    /// <summary>
    /// When false, SetAnimation never activates the sing-hold timer.
    /// Set to false for background/prop objects (strumLine = -1).
    /// </summary>
    public bool IsCharacter = false;
    
    /// <summary>
    /// When true, the sprite is a background object that needs offset subtraction.
    /// Set based on sprite definition type (def.Type == 2 or background detection).
    /// </summary>
    public bool IsBG = false;
    
    /// <summary>
    /// Level format version. Always use V3 behavior.
    /// V3: All sprites apply offset subtraction (correct for scrollFactor coordinates).
    /// </summary>
    public float LevelVersion = 3.0f;
    
    /// <summary>
    /// When true, the sprite is a fixed (screen-space) overlay.
    /// The offset is applied without scaling — it's a direct screen-pixel anchor.
    /// </summary>
    public bool IsFixed = false;

    /// <summary>
    /// Scale multiplier applied to fixed objects to match the camera's pixel scale.
    /// Set this to pixelScale = sw / (CAM_W * cam.scale) each frame for fixed objects.
    /// When 0, fixed objects use their raw Scale without camera adaptation.
    /// </summary>
    public float FixedPixelScale = 0f;
    
    // Debug flag to log offset only once per sprite
    private bool _offsetLogged = false;

    /// <summary>Render target dimensions — used for fixed object stretch-to-fit when scale > 1.</summary>
    public int RenderWidth  = 1280;
    public int RenderHeight = 720;
    
    /// <summary>Final blend color combining tint and opacity.</summary>
    public Color BlendColor => TintColor * Opacity;

    /// <summary>Current animation data (offset, scale, etc.)</summary>
    public SpriteAnimData CurrentAnimData =>
        _def.Data.Count > 0 ? _def.Data[_currentAnim] : new SpriteAnimData();

    /// <summary>
    /// Returns the current frame's texture and source rectangle.
    /// Useful for custom drawing (e.g. health bar icons).
    /// </summary>
    public (Texture2D? tex, Rectangle src) GetCurrentFrameTexture()
    {
        if (_def.Data.Count == 0 || _textures.Count == 0) return (null, Rectangle.Empty);
        int sprType = (int)_def.Type;
        int frame   = (int)Math.Max(0, _currentFrame);
        var anim    = _def.Data[_currentAnim];

        // Packed 2D atlas (single or multi-atlas)
        if (anim.UsesPackedAtlas && anim.AtlasFrameRects != null && anim.AtlasFrameRects.Count > 0)
        {
            int clamped = Math.Clamp(frame, 0, anim.AtlasFrameRects.Count - 1);
            var rect    = anim.AtlasFrameRects[clamped];
            int texIdx  = anim.SplitOffset + rect.AtlasIndex;
            if (texIdx < 0 || texIdx >= _textures.Count) return (null, Rectangle.Empty);
            return (_textures[texIdx], AtlasPacker.GetFrameSourceRect(rect));
        }

        // Split strip: chunk mode or per-frame mode
        if (anim.IsSplit)
        {
            if (anim.ChunkFrames > 0)
            {
                int chunk   = frame / anim.ChunkFrames;
                int inChunk = frame % anim.ChunkFrames;
                int texIdx  = anim.SplitOffset + chunk;
                if (texIdx < 0 || texIdx >= _textures.Count) return (null, Rectangle.Empty);
                var t = _textures[texIdx];
                int fw = anim.FrameWidth > 0 ? anim.FrameWidth : t.Width;
                int framesInChunk = fw > 0 ? Math.Max(1, t.Width / fw) : 1;
                inChunk = Math.Clamp(inChunk, 0, framesInChunk - 1);
                return (t, new Rectangle(inChunk * fw, 0, fw, t.Height));
            }
            else
            {
                int texIdx = anim.SplitOffset + Math.Clamp(frame, 0, Math.Max(0, anim.FrameCount - 1));
                if (texIdx < 0 || texIdx >= _textures.Count) return (null, Rectangle.Empty);
                var t = _textures[texIdx];
                return (t, new Rectangle(0, 0, t.Width, t.Height));
            }
        }

        switch (sprType)
        {
            case 0:
            case 1:
            {
                // Use TextureIndex if set — fixes type=0 with mixed split/non-split anims
                int texIdx = anim.TextureIndex >= 0 ? anim.TextureIndex : Math.Min(_currentAnim, _textures.Count - 1);
                var tex    = _textures[texIdx];
                int fc     = GetFrameCount(_currentAnim);
                int fw     = fc > 0 ? tex.Width / fc : tex.Width;
                frame      = Math.Clamp(frame, 0, fc - 1);
                return (tex, new Rectangle(frame * fw, 0, fw, tex.Height));
            }
            case 2:
            {
                var tex = _textures[0];
                int fc  = GetFrameCount(_currentAnim);
                int fw  = fc > 0 ? tex.Width / fc : tex.Width;
                frame   = Math.Clamp(frame, 0, fc - 1);
                return (tex, new Rectangle(frame * fw, 0, fw, tex.Height));
            }
            case 3:
            {
                int texIdx = Math.Min(_currentAnim, _textures.Count - 1);
                var tex    = _textures[texIdx];
                // Type 3: each anim is its own PNG. If num > 1, the PNG is a horizontal strip.
                int fc = GetFrameCount(_currentAnim);
                if (fc <= 1)
                    return (tex, new Rectangle(0, 0, tex.Width, tex.Height));
                int fw = tex.Width / fc;
                frame  = Math.Clamp(frame, 0, fc - 1);
                return (tex, new Rectangle(frame * fw, 0, fw, tex.Height));
            }
            default:
                return (null, Rectangle.Empty);
        }
    }

    // When > 0 the animator is locked in a sing pose (hold note active)
    // Set this from outside to keep the character singing during a hold.
    public float HoldUntil = -1f;  // song time in seconds

    // ── Private ──────────────────────────────────────────────────────────────
    private readonly FnmmSprite      _def;
    private readonly List<Texture2D> _textures = new();

    private int   _currentAnim  = 0;
    private float _currentFrame = 0f;
    private bool  _playing      = true;
    
    // Hold note animation state
    private bool  _wasHolding   = false;  // Track if we were holding in the previous frame
    private bool  _completingHold = false; // True when completing animation after hold release
    private bool  _setByEvent = false;  // Track if current animation was set by an event (not gameplay)
    private const int HOLD_LOOP_FRAMES = 3;  // Loop first 3 frames during hold

    // Seconds to hold a sing pose before returning to idle.
    // = 1 beat duration at the current BPM, minimum 0.5s.
    // When BPM is unknown (0), use 0.75s as a safe default.
    private float SingHold => Bpm > 0f
        ? Math.Max(0.5f, 60f / Math.Max(1f, Bpm))
        : 0.75f;
    private float _singTimer = 0f;

    // ── Constructor ──────────────────────────────────────────────────────────
    public SpriteAnimator(string name, FnmmSprite def,
                          List<Texture2D> loadedTextures,
                          Vector2 position, Vector2 scale)
    {
        SpriteName = name;
        _def       = def;
        Position   = position;
        Scale      = scale;
        _textures.AddRange(loadedTextures);
    }

    // ── Public API ───────────────────────────────────────────────────────────

    private int GetIdleAnimIndex() => (_def.Data.Count > 1 && _def.Data[0].FrameCount == 1) ? 1 : 0;

    public void SetAnimation(int animIndex, bool setByEvent = false)
    {
        if (animIndex < 0 || animIndex >= _def.Data.Count) return;

        // Always start the target animation at frame 0 (including sequential video/cutscene clips).
        // Hold notes are handled separately via HoldUntil — when a hold is active,
        // Update() will keep the pose locked regardless of frame advancement.
        _currentAnim  = animIndex;
        _currentFrame = 0f;
        _playing      = true;
        _completingHold = false;  // Reset hold completion flag
        _setByEvent = setByEvent;  // Mark if this was set by an event

        if (IsCharacter)
        {
            int idleAnim = GetIdleAnimIndex();
            var anim = _def.Data[animIndex];
            // Characters: set sing timer for non-idle animations:
            // - Loop=true: hold pose for minimum SingHold/animDuration before returning to idle.
            // - Loop=false + setByEvent: hold event protection while the animation plays, without auto-returning to idle.
            // - Loop=false + !setByEvent: stops on its last frame per FMM behavior instead of returning to idle.
            if (animIndex != 0 && animIndex != idleAnim && (anim.Loop || setByEvent))
            {
                float fps = AnimFps(anim);
                float animDuration = fps > 0f ? GetFrameCount(animIndex) / fps : 0f;
                float needed = anim.Loop ? Math.Max(SingHold, animDuration) : animDuration;
                _singTimer = needed;
            }
            else
            {
                _singTimer = 0f;
            }
        }
        else
        {
            // For non-character objects (props, backgrounds, etc.):
            // If animation has Loop=false, disable sing timer so it stays on last frame
            // This is used in charts like Execution D-side where Lord X transforms
            var anim = _def.Data[animIndex];
            if (!anim.Loop && animIndex != 0)
            {
                _singTimer = 0f; // Disable auto-return to idle for non-looping transformations
            }
            else
            {
                _singTimer = 0f; // Objects don't auto-return to idle normally
            }
        }
    }

    /// <summary>
    /// Manually sets the sing timer to a specific value.
    /// Used when forcibly returning a character to idle (e.g., on strumline change).
    /// </summary>
    public void SetSingTimer(float value)
    {
        _singTimer = value;
    }

    /// <summary>
    /// Call every frame. songTime is the current song position in seconds
    /// (used to check HoldUntil for hold notes).
    /// </summary>
    public void Update(float dt, float songTime = -1f)
    {
        // If a hold note is active, keep the sing pose and don't count down
        bool holdActive = HoldUntil > 0f && songTime >= 0f && songTime < HoldUntil;

        // Clean up expired hold
        if (HoldUntil > 0f && songTime >= HoldUntil)
        {
            HoldUntil = -1f;
            // When hold ends, continue from current frame to complete animation
            // _wasHolding will be set to false below, allowing full animation to play
        }

        // Detect hold release: transition from holding to not holding
        bool holdJustReleased = _wasHolding && !holdActive;
        _wasHolding = holdActive;

        if (!holdActive)
        {
            // Countdown to return to idle
            if (_singTimer > 0f)
            {
                _singTimer -= dt;
            }
            
            // When sing timer expires, check if we can return to idle
            // This happens when animation completes OR is already finished (!_playing)
            // If animation was set by an event, we now clear that flag to allow
            // the character to be controlled by notes again
            int idleAnim = GetIdleAnimIndex();
            if (_singTimer <= 0f && _currentAnim != 0 && _currentAnim != idleAnim && IsCharacter && _currentAnim < _def.Data.Count)
            {
                var currentAnim = _def.Data[_currentAnim];

                // Clear event control once timer/animation expires so future notes aren't blocked
                if (_setByEvent && (currentAnim.Loop || !_playing))
                {
                    _setByEvent = false;
                    Console.WriteLine($"[SpriteAnimator] {SpriteName} cleared IsSetByEvent (timer expired)");
                }

                // Only auto-return to idle when anim.Loop == true.
                // In FMM, anim.Loop == false stops on the last frame instead of returning to idle.
                if (currentAnim.Loop)
                {
                    int frameCount = GetFrameCount(_currentAnim);
                    bool animationCompleted = _currentFrame >= (frameCount - 1);

                    if (!_playing || animationCompleted || frameCount <= 1)
                    {
                        SetAnimation(idleAnim);
                        return;  // Early return after switching to idle
                    }
                }
            }
        }
        else
        {
            // Reset the timer so we don't snap to idle mid-hold (only for Loop=true animations)
            _singTimer = (_currentAnim < _def.Data.Count && _def.Data[_currentAnim].Loop) ? SingHold : 0f;
        }

        // Allow update even if !_playing for non-looping animations that need to complete
        if (_def.Data.Count == 0) return;

        var anim = _def.Data[_currentAnim];
        int activeIdleAnim = GetIdleAnimIndex();
        // Only ignore anim.Loop when anim.Loop == true on normal character poses (so poses play once before returning to idle);
        // when anim.Loop == false, honor it so the animation stops on its last frame as in FMM.
        bool ignorePoseLoop = IsCharacter && !_setByEvent && _currentAnim != 0 && _currentAnim != activeIdleAnim && anim.Loop;

        // If animation finished and not looping (or ignoring Loop=true on a normal character pose), stay on last frame
        if (!_playing && (!anim.Loop || ignorePoseLoop))
            return;

        float fps  = AnimFps(anim);
        _currentFrame += fps * dt;

        int fc = GetFrameCount(_currentAnim);
        
        // HOLD NOTE ANIMATION LOGIC:
        // During hold: loop first HOLD_LOOP_FRAMES frames
        // After release: continue from current frame to end (no loop)
        if (holdActive && fc > HOLD_LOOP_FRAMES)
        {
            // Loop the first few frames (0, 1, 2, 0, 1, 2, ...)
            if (_currentFrame >= HOLD_LOOP_FRAMES)
                _currentFrame = _currentFrame % HOLD_LOOP_FRAMES;
            // Ensure we're not in completion mode during hold
            _completingHold = false;
        }
        else if (_wasHolding && !holdActive && fc > HOLD_LOOP_FRAMES)
        {
            // Hold was just released or we're in the process of completing
            // If we're still in the loop range, jump to continuation frames
            if (_currentFrame < HOLD_LOOP_FRAMES && !_completingHold)
                _currentFrame = HOLD_LOOP_FRAMES;
            // Mark that we're completing a hold animation (no loop)
            // This flag stays true until animation completes
            _completingHold = true;
        }
        else if (!holdActive && _currentFrame < HOLD_LOOP_FRAMES && _completingHold)
        {
            // Safety: if we're completing but somehow back in loop range, skip ahead
            _currentFrame = HOLD_LOOP_FRAMES;
        }
        
        // Handle animation completion
        if (_currentFrame >= fc)
        {
            // If we're completing a hold animation, DON'T loop - just finish once
            if (_completingHold)
            {
                _currentFrame = fc - 1;
                _playing = false;
                _completingHold = false;  // Reset flag
                
                // CRITICAL: After hold completion animation finishes, return to idle
                if (_currentAnim != 0 && _currentAnim != activeIdleAnim && IsCharacter)
                {
                    SetAnimation(activeIdleAnim);
                }
            }
            else if (anim.Loop && !ignorePoseLoop)
            {
                // Normal loop behavior (idle, props/backgrounds, or looping event animations)
                _currentFrame %= Math.Max(1, fc);
            }
            else
            {
                // Animation finished: either Loop=false (stops on last frame without returning to idle)
                // or normal character pose ignoring Loop=true (waits on last frame until _singTimer -> idle)
                _currentFrame = fc - 1;
                _playing      = false;

                if (!IsCharacter || !anim.Loop)
                {
                    _singTimer = 0f;
                }
            }
        }
    }

    public void Draw(SpriteBatch sb)
    {
        if (!Visible || _def.Data.Count == 0) return;

        int   sprType  = (int)_def.Type;
        var   anim     = _def.Data[_currentAnim];
        float sprScale = anim.Scale;
        var   offset   = new Vector2(anim.Offset.X, anim.Offset.Y);
        int   frame    = (int)Math.Max(0, _currentFrame);

        // ── Packed Atlas Mode ────────────────────────────────────────────────
        // Draw from 2D atlas coordinates (supports single and multi-atlas strips)
        if (anim.UsesPackedAtlas && anim.AtlasFrameRects != null)
        {
            if (anim.AtlasFrameRects.Count > 0)
            {
                int clamped = Math.Clamp(frame, 0, anim.AtlasFrameRects.Count - 1);
                var rect    = anim.AtlasFrameRects[clamped];
                int texIdx  = anim.SplitOffset + rect.AtlasIndex;
                if (texIdx >= 0 && texIdx < _textures.Count)
                {
                    DrawAtlasFrame(sb, texIdx, clamped, rect, offset, sprScale);
                }
            }
            return;
        }

        // ── Legacy Chunk/Strip Mode ──────────────────────────────────────────
        // If this anim's strip was split into chunk textures,
        // find the right chunk and x-offset within it
        if (anim.IsSplit)
        {
            if (anim.ChunkFrames > 0)
            {
                // Chunk mode: multiple frames per texture
                int chunk        = frame / anim.ChunkFrames;
                int frameInChunk = frame % anim.ChunkFrames;
                int texIdx       = anim.SplitOffset + chunk;
                if (texIdx < _textures.Count)
                {
                    // Use actual texture width to handle last chunk (may have fewer frames)
                    var chunkTex = _textures[texIdx];
                    int fw = anim.FrameWidth;
                    // Clamp frameInChunk to actual frames in this chunk
                    int framesInChunk = fw > 0 ? chunkTex.Width / fw : 1;
                    frameInChunk = Math.Clamp(frameInChunk, 0, framesInChunk - 1);
                    DrawChunkFrame(sb, texIdx, frameInChunk, offset, sprScale, fw);
                }
            }
            else
            {
                // Legacy per-frame mode
                int texIdx = anim.SplitOffset + Math.Clamp(frame, 0, anim.FrameCount - 1);
                if (texIdx < _textures.Count)
                    DrawSingle(sb, texIdx, offset, sprScale);
            }
            return;
        }

        switch (sprType)
        {
            case 0:
            case 1: DrawStrip(sb, _currentAnim, frame, offset, sprScale); break;
            case 2: DrawStrip(sb, 0,            frame, offset, sprScale); break;
            case 3:
            {
                int fc = GetFrameCount(_currentAnim);
                if (fc <= 1)
                    DrawSingle(sb, _currentAnim, offset, sprScale);
                else
                    DrawStrip(sb, _currentAnim, frame, offset, sprScale);
                break;
            }
        }
    }

    /// <summary>
    /// Draws a fixed object. Fixed objects use normalized position (0..1) converted
    /// to world coordinates before calling, and are drawn with the same camera matrix
    /// as world objects — so this is identical to Draw().
    /// </summary>
    public void DrawFixed(SpriteBatch sb, int screenW, int screenH) => Draw(sb);

    /// <summary>Whether the current animation frame wants bilinear filtering.</summary>
    public bool CurrentBilinear =>
        _def.Data.Count > 0 && _def.Data[_currentAnim].Bilinear;

    // ── Helpers ──────────────────────────────────────────────────────────────

    private float AnimFps(SpriteAnimData anim)
    {
        if (anim.SpdFps) return Math.Clamp(anim.Spd, 0.1f, 240f);  // Clamp literal FPS to reasonable range

        // spd_fps=false: beat-based timing
        // Formula: fps = frameCount / (spd * secondsPerBeat)
        // where secondsPerBeat = 60 / BPM
        // This means:
        // - spd=1 (1 beat): animation completes in 1 beat
        // - spd=2 (2 beats): animation completes in 2 beats (slower)
        // - spd=0.5 (half beat): animation completes in 0.5 beat (faster)
        
        float bpm = Math.Max(1f, Bpm);
        float spd = Math.Clamp(anim.Spd, 0.001f, 100f);  // Clamp to prevent division issues
        int frameCount = GetFrameCount(_currentAnim);
        
        float secondsPerBeat = 60f / bpm;
        float animDurationSeconds = spd * secondsPerBeat;
        float calculatedFps = frameCount / animDurationSeconds;
        
        return Math.Clamp(calculatedFps, 0.1f, 240f);  // Cap at reasonable FPS range
    }

    private void DrawStrip(SpriteBatch sb, int texIndex, int frame,
                           Vector2 offset, float sprScale)
    {
        // Use TextureIndex if set — fixes type=0 sprites with mixed split/non-split anims
        var animData = _def.Data.Count > _currentAnim ? _def.Data[_currentAnim] : null;
        if (animData != null && animData.TextureIndex >= 0)
            texIndex = animData.TextureIndex;

        if (texIndex >= _textures.Count) return;
        var tex = _textures[texIndex];
        if (tex == null || tex.IsDisposed) return;  // Skip if texture is null or disposed

        int fc = GetFrameCount(_currentAnim);
        int fw = fc > 0 ? tex.Width / fc : tex.Width;
        int fh = tex.Height;
        frame  = Math.Clamp(frame, 0, fc - 1);

        var src        = new Rectangle(frame * fw, 0, fw, fh);
        float dc       = _def.Data.Count > _currentAnim ? _def.Data[_currentAnim].DownscaleCompensation : 1.0f;
        var finalScale = Scale * sprScale * dc;
        var effects    = BuildEffects(ref finalScale);

        if (IsFixed)
        {
            float absScaleX = Math.Abs(Scale.X);
            float absScaleY = Math.Abs(Scale.Y);
            
            // When FixedPixelScale is 0, use the same scale as non-fixed objects (Scale * sprScale * dc)
            // Otherwise use FixedPixelScale for camera-independent scaling
            float ps = FixedPixelScale > 0f ? FixedPixelScale : 1f;
            bool useNormalScale = FixedPixelScale == 0f;
            
            float ox = Scale.X < 0 ? (fw - offset.X) : offset.X;
            float oy = Scale.Y < 0 ? (fh - offset.Y) : offset.Y;
            var origin = new Vector2(ox, oy);
            
            Vector2 fixedScale;
            if (useNormalScale)
            {
                // Same as non-fixed objects: just Scale * sprScale * dc
                fixedScale = new Vector2(absScaleX * sprScale * dc, absScaleY * sprScale * dc);
            }
            else
            {
                // Use FixedPixelScale for camera-independent rendering
                fixedScale = new Vector2(absScaleX * sprScale * dc * ps, absScaleY * sprScale * dc * ps);
            }
            
            sb.Draw(tex, Position, src, BlendColor, 0f, origin, fixedScale, effects, 0f);
        }
        else
        {
            var absObjScale = new Vector2(Math.Abs(Scale.X), Math.Abs(Scale.Y));
            Vector2 drawPos;
            
            // Always apply offset subtraction (V3 behavior)
            // This is correct when positions use scrollFactor coordinate system
            bool applyOffset = true;
            
            if (applyOffset)
            {
                float effOX = Scale.X < 0 ? (fw - offset.X) : offset.X;
                float effOY = Scale.Y < 0 ? (fh - offset.Y) : offset.Y;
                drawPos = Position - new Vector2(effOX, effOY) * absObjScale * sprScale;
            }
            else
            {
                drawPos = Position;
            }
            
            // Debug log for offset application (only log first frame)
            if (_currentFrame < 1 && !_offsetLogged)
            {
                string mode = applyOffset ? "OFFSET APPLIED (V3)" : "NO OFFSET";
                Console.WriteLine($"[SpriteOffset] Normal Draw: offset=({offset.X},{offset.Y}), Position=({Position.X},{Position.Y}), drawPos=({drawPos.X},{drawPos.Y}) [{mode}]");
                _offsetLogged = true;
            }
            
            sb.Draw(tex, drawPos, src, BlendColor, 0f, Vector2.Zero, finalScale, effects, 0f);
        }
    }

    private void DrawSingle(SpriteBatch sb, int texIndex,
                            Vector2 offset, float sprScale)
    {
        // Use TextureIndex if set — fixes type=0 sprites with mixed split/non-split anims
        var animData = _def.Data.Count > _currentAnim ? _def.Data[_currentAnim] : null;
        if (animData != null && animData.TextureIndex >= 0)
            texIndex = animData.TextureIndex;

        if (texIndex >= _textures.Count) return;
        var tex = _textures[texIndex];
        if (tex == null || tex.IsDisposed) return;  // Skip if texture is null or disposed
        var finalScale = Scale * sprScale;
        var effects    = BuildEffects(ref finalScale);

        if (IsFixed)
        {
            float absScaleX = Math.Abs(Scale.X);
            float absScaleY = Math.Abs(Scale.Y);
            
            // When FixedPixelScale is 0, use the same scale as non-fixed objects (Scale * sprScale)
            // Otherwise use FixedPixelScale for camera-independent scaling
            float ps = FixedPixelScale > 0f ? FixedPixelScale : 1f;
            bool useNormalScale = FixedPixelScale == 0f;
            
            float ox = Scale.X < 0 ? (tex.Width  - offset.X) : offset.X;
            float oy = Scale.Y < 0 ? (tex.Height - offset.Y) : offset.Y;
            var origin = new Vector2(ox, oy);
            
            Vector2 fixedScale;
            if (useNormalScale)
            {
                // Same as non-fixed objects: just Scale * sprScale
                fixedScale = new Vector2(absScaleX * sprScale, absScaleY * sprScale);
            }
            else
            {
                // Use FixedPixelScale for camera-independent rendering
                fixedScale = new Vector2(absScaleX * sprScale * ps, absScaleY * sprScale * ps);
            }
            
            sb.Draw(tex, Position, null, BlendColor, 0f, origin, fixedScale, effects, 0f);
        }
        else
        {
            var absObjScale = new Vector2(Math.Abs(Scale.X), Math.Abs(Scale.Y));
            Vector2 drawPos;
            
            // Always apply offset subtraction (V3 behavior)
            // This is correct when positions use scrollFactor coordinate system
            bool applyOffset = true;
            
            if (applyOffset)
            {
                float effOX = Scale.X < 0 ? (tex.Width - offset.X) : offset.X;
                float effOY = Scale.Y < 0 ? (tex.Height - offset.Y) : offset.Y;
                drawPos = Position - new Vector2(effOX, effOY) * absObjScale * sprScale;
            }
            else
            {
                drawPos = Position;
            }
            
            // Debug log for offset application (only log first frame)
            if (_currentFrame < 1 && !_offsetLogged)
            {
                string mode = applyOffset ? $"OFFSET APPLIED (v{LevelVersion})" : "NO OFFSET";
                Console.WriteLine($"[SpriteOffset] Single Draw: offset=({offset.X},{offset.Y}), Position=({Position.X},{Position.Y}), drawPos=({drawPos.X},{drawPos.Y}) [{mode}]");
                _offsetLogged = true;
            }
            
            sb.Draw(tex, drawPos, null, BlendColor, 0f, Vector2.Zero, finalScale, effects, 0f);
        }
    }

    /// <summary>
    /// Draws a specific frame from a chunk texture.
    /// frameInChunk = which frame within the chunk (0-based).
    /// fw = width of a single frame in pixels.
    /// </summary>
    private void DrawChunkFrame(SpriteBatch sb, int texIndex, int frameInChunk,
                                Vector2 offset, float sprScale, int fw)
    {
        if (texIndex >= _textures.Count) return;
        var tex = _textures[texIndex];
        if (tex == null || tex.IsDisposed) return;  // Skip if texture is null or disposed
        int fh  = tex.Height;
        var src = new Rectangle(frameInChunk * fw, 0, fw, fh);

        var finalScale  = Scale * sprScale;
        var effects     = BuildEffects(ref finalScale);
        
        var absObjScale = new Vector2(Math.Abs(Scale.X), Math.Abs(Scale.Y));
        Vector2 drawPos;
        
        // V3: All sprites apply offset with subtraction
        // V4: Only IsCharacter and IsBG apply offset
        bool applyOffset = (LevelVersion < 4.0f) || IsCharacter || IsBG;
        
        if (applyOffset)
        {
            float effOX2 = Scale.X < 0 ? (fw - offset.X) : offset.X;
            float effOY2 = Scale.Y < 0 ? (fh - offset.Y) : offset.Y;
            drawPos = Position - new Vector2(effOX2, effOY2) * absObjScale * sprScale;
        }
        else
        {
            drawPos = Position;
        }
        
        // Debug log for offset application (only log first few frames)
        if (_currentFrame < 1 && !_offsetLogged)
        {
            string mode = applyOffset ? $"OFFSET APPLIED (v{LevelVersion})" : "NO OFFSET";
            Console.WriteLine($"[SpriteOffset] Strip Draw: offset=({offset.X},{offset.Y}), Position=({Position.X},{Position.Y}), drawPos=({drawPos.X},{drawPos.Y}) [{mode}]");
            _offsetLogged = true;
        }

        sb.Draw(tex, drawPos, src, BlendColor, 0f, Vector2.Zero, finalScale, effects, 0f);
    }

    /// <summary>
    /// Draws a frame from a packed 2D atlas.
    /// Used when sprite was auto-converted from giant horizontal strip.
    /// </summary>
    private void DrawAtlasFrame(SpriteBatch sb, int texIndex, int frame,
                                AtlasPacker.FrameRect frameRect, Vector2 offset, float sprScale)
    {
        if (texIndex >= _textures.Count) return;
        var tex = _textures[texIndex];
        if (tex == null || tex.IsDisposed) return;

        var src = AtlasPacker.GetFrameSourceRect(frameRect);
        int fw = frameRect.Width;
        int fh = frameRect.Height;

        float dc = _def.Data.Count > _currentAnim && _def.Data[_currentAnim].DownscaleCompensation > 0f
            ? _def.Data[_currentAnim].DownscaleCompensation
            : 1.0f;
        var finalScale = Scale * sprScale * dc;
        var effects = BuildEffects(ref finalScale);

        float scaledOX = offset.X / dc;
        float scaledOY = offset.Y / dc;

        if (IsFixed)
        {
            // Fixed atlas frame: use FixedPixelScale like other draw methods
            float absScaleX = Math.Abs(Scale.X);
            float absScaleY = Math.Abs(Scale.Y);
            
            // When FixedPixelScale is 0, use the same scale as non-fixed objects (Scale * sprScale * dc)
            // Otherwise use FixedPixelScale for camera-independent scaling
            float ps = FixedPixelScale > 0f ? FixedPixelScale : 1f;
            bool useNormalScale = FixedPixelScale == 0f;
            
            float ox = Scale.X < 0 ? (fw - scaledOX) : scaledOX;
            float oy = Scale.Y < 0 ? (fh - scaledOY) : scaledOY;
            var origin = new Vector2(ox, oy);
            
            Vector2 fixedScale = useNormalScale
                ? new Vector2(absScaleX * sprScale * dc, absScaleY * sprScale * dc)
                : new Vector2(absScaleX * sprScale * dc * ps, absScaleY * sprScale * dc * ps);
            
            sb.Draw(tex, Position, src, BlendColor, 0f, origin, fixedScale, effects, 0f);
        }
        else
        {
            // Non-fixed: standard positioning with offset applied in compensated space
            var absObjScale = new Vector2(Math.Abs(Scale.X), Math.Abs(Scale.Y));
            float effOX = Scale.X < 0 ? (fw - scaledOX) : scaledOX;
            float effOY = Scale.Y < 0 ? (fh - scaledOY) : scaledOY;
            var drawPos = Position - new Vector2(effOX, effOY) * absObjScale * sprScale * dc;
            
            // Debug log for offset application (only log first few frames)
            if (_currentFrame < 1 && !_offsetLogged)
            {
                Console.WriteLine($"[SpriteOffset] Atlas Draw: offset=({offset.X},{offset.Y}), effOffset=({effOX},{effOY}), dc={dc:0.###}, Position=({Position.X},{Position.Y}), drawPos=({drawPos.X},{drawPos.Y})");
                _offsetLogged = true;
            }
            
            sb.Draw(tex, drawPos, src, BlendColor, 0f, Vector2.Zero, finalScale, effects, 0f);
        }
    }

    private static SpriteEffects BuildEffects(ref Vector2 scale)
    {
        var fx = SpriteEffects.None;
        if (scale.X < 0) { fx |= SpriteEffects.FlipHorizontally; scale.X = -scale.X; }
        if (scale.Y < 0) { fx |= SpriteEffects.FlipVertically;   scale.Y = -scale.Y; }
        return fx;
    }

    private int GetFrameCount(int animIndex)
    {
        if (animIndex >= _def.Data.Count) return 1;
        return Math.Max(1, _def.Data[animIndex].FrameCount);
    }
}
