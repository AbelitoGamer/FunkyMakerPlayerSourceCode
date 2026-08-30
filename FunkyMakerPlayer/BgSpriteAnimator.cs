using System;
using System.Collections.Generic;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;

namespace FunkyMakerPlayer;

/// <summary>
/// TEST BUILD — Background sprite animator for type 2 (background) sprites.
/// Handles chunk-split textures independently from SpriteAnimator.
/// This keeps background rendering logic separate from character animation.
///
/// Background sprites (type 2) are single spritesheets that can be very wide.
/// When the strip exceeds GPU texture limits, it's split into chunks.
/// This class handles both normal and chunk-split backgrounds.
/// </summary>
public class BgSpriteAnimator
{
    public Vector2 Position;
    public Vector2 Scale;
    public bool    Visible  = true;
    public Color   TintColor = Color.White;
    public float   Opacity   = 1.0f;
    public float   Bpm       = 100f;
    public string  SpriteName;

    public Color BlendColor => TintColor * Opacity;

    // ── Chunk split data ──────────────────────────────────────────────────────
    // When the strip is too wide for GPU, it's split into chunks.
    // Each chunk holds framesPerChunk frames of fw×fh pixels.
    private readonly List<Texture2D> _chunks = new();
    private int   _framesPerChunk = 0;  // 0 = not split (single texture)
    private int   _frameWidth     = 0;
    private int   _frameHeight    = 0;
    private int   _totalFrames    = 1;

    // Single texture (no split)
    private Texture2D? _texture;

    // Animation state
    private float _currentFrame = 0f;
    private bool  _playing      = true;

    // Sprite definition
    private readonly FnmmSprite    _def;
    private readonly SpriteAnimData _anim;

    // ── Constructor (single texture) ─────────────────────────────────────────
    public BgSpriteAnimator(string name, FnmmSprite def, Texture2D texture,
                             Vector2 position, Vector2 scale)
    {
        SpriteName = name;
        _def       = def;
        _anim      = def.Data.Count > 0 ? def.Data[0] : new SpriteAnimData();
        _texture   = texture;
        Position   = position;
        Scale      = scale;
        _totalFrames = _anim.FrameCount;
    }

    // ── Constructor (chunk split) ─────────────────────────────────────────────
    public BgSpriteAnimator(string name, FnmmSprite def, List<Texture2D> chunks,
                             int framesPerChunk, int frameWidth, int frameHeight,
                             int totalFrames, Vector2 position, Vector2 scale)
    {
        SpriteName      = name;
        _def            = def;
        _anim           = def.Data.Count > 0 ? def.Data[0] : new SpriteAnimData();
        _chunks.AddRange(chunks);
        _framesPerChunk = framesPerChunk;
        _frameWidth     = frameWidth;
        _frameHeight    = frameHeight;
        _totalFrames    = totalFrames;
        Position        = position;
        Scale           = scale;
    }

    // ── Update ────────────────────────────────────────────────────────────────
    public void Update(float dt)
    {
        if (!_playing) return;

        float fps = AnimFps();
        _currentFrame += fps * dt;

        int fc = Math.Max(1, _totalFrames);
        if (_currentFrame >= fc)
        {
            if (_anim.Loop)
                _currentFrame %= fc;
            else
            {
                _currentFrame = fc - 1;
                _playing = false;
            }
        }
    }

    // ── Draw ──────────────────────────────────────────────────────────────────
    public void Draw(SpriteBatch sb)
    {
        if (!Visible) return;

        int frame = (int)Math.Max(0, _currentFrame);
        var offset = new Vector2(_anim.Offset.X, _anim.Offset.Y);
        float sprScale = _anim.Scale;

        if (_framesPerChunk > 0 && _chunks.Count > 0)
        {
            // Chunk split mode
            int chunk        = frame / _framesPerChunk;
            int frameInChunk = frame % _framesPerChunk;
            if (chunk >= _chunks.Count) return;

            var chunkTex = _chunks[chunk];
            int fw = _frameWidth;
            // Clamp to actual frames in this chunk
            int framesInChunk = fw > 0 ? chunkTex.Width / fw : 1;
            frameInChunk = Math.Clamp(frameInChunk, 0, framesInChunk - 1);

            var src        = new Rectangle(frameInChunk * fw, 0, fw, _frameHeight);
            var finalScale = Scale * sprScale;
            var effects    = BuildEffects(ref finalScale);
            var absScale   = new Vector2(Math.Abs(Scale.X), Math.Abs(Scale.Y));
            var drawPos    = Position - offset * absScale * sprScale;

            sb.Draw(chunkTex, drawPos, src, BlendColor, 0f, Vector2.Zero, finalScale, effects, 0f);
        }
        else if (_texture != null)
        {
            // Single texture mode
            int fc = Math.Max(1, _totalFrames);
            int fw = fc > 0 ? _texture.Width / fc : _texture.Width;
            frame  = Math.Clamp(frame, 0, fc - 1);

            var src        = new Rectangle(frame * fw, 0, fw, _texture.Height);
            var finalScale = Scale * sprScale;
            var effects    = BuildEffects(ref finalScale);
            var absScale   = new Vector2(Math.Abs(Scale.X), Math.Abs(Scale.Y));
            var drawPos    = Position - offset * absScale * sprScale;

            sb.Draw(_texture, drawPos, src, BlendColor, 0f, Vector2.Zero, finalScale, effects, 0f);
        }
    }

    // ── DrawFixed (screen-space) ──────────────────────────────────────────────
    public void DrawFixed(SpriteBatch sb) => Draw(sb);

    // ── Helpers ───────────────────────────────────────────────────────────────
    private float AnimFps()
    {
        if (_anim.SpdFps) return _anim.Spd;
        float spd = Math.Max(0.001f, _anim.Spd);
        return (Bpm / 60f) * 4f * spd;
    }

    private static SpriteEffects BuildEffects(ref Vector2 scale)
    {
        var fx = SpriteEffects.None;
        if (scale.X < 0) { fx |= SpriteEffects.FlipHorizontally; scale.X = -scale.X; }
        if (scale.Y < 0) { fx |= SpriteEffects.FlipVertically;   scale.Y = -scale.Y; }
        return fx;
    }

    public bool CurrentBilinear => _anim.Bilinear;
}
