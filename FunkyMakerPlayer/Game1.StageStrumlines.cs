using System;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;

namespace FunkyMakerPlayer;

/// <summary>
/// Stage strumlines (fork): with the "Extra Strumlines" setting on and 3+ strumlines, every
/// strumline but the player's (1, 2, 3…) is part of the stage instead of the HUD:
/// <list type="bullet">
/// <item>It sits right above the character it belongs to (the idle frames' box, so it doesn't bob),
/// in screen space through the matrix that character was drawn with, and scales with the camera zoom
/// (never wider than about the character itself; the head gap grows with the character's height).</item>
/// <item>Its notes fade in a little below the character's feet instead of scrolling in from the screen edge.</item>
/// <item>It is drawn just below that character's sprite and above the object before it
/// (character on object 4 → between objects 3 and 4), so the HUD stays on top.</item>
/// <item>It is shown only while one of its notes is less than <see cref="StageStrumLookAhead"/> s
/// away (or a hold is being played), fading in and out.</item>
/// <item>Over the player's lane column (the lane underlay's area) it drops to
/// <see cref="StageStrumNearPlayerAlpha"/> so the playable notes stay readable.</item>
/// </list>
/// Before, the extras were HUD elements placed with the raw object coordinates (the X offset was
/// also scaled twice, hence the big offsets), hidden whenever no note was being hit (flicker) and
/// free to cover the player's lanes.
/// </summary>
public partial class Game1
{
    private const float StageStrumLookAhead       = 1.5f;  // s: shown while a note is this close
    private const float StageStrumFadeInTime      = 0.25f; // s
    private const float StageStrumFadeOutTime     = 0.5f;  // s
    private const float StageStrumNearPlayerAlpha = 0.5f;  // opacity over the player's lanes
    private const float StageStrumNearFadeTime    = 0.15f; // s to dim / undim
    private const float StageStrumNearMargin      = 12f;   // px: "close to" the player's column
    private const float StageStrumBaseScale       = 0.45f; // size at camera scale 1
    private const float StageStrumMinScale        = 0.2f;
    private const float StageStrumMaxScale        = 0.85f;
    private const float StageStrumMaxCharWidth    = 1.1f;  // lanes at most this × the character's width
    private const float StageStrumTinyScale       = 0.12f; // floor for very small characters
    private const float StageStrumGapLanes        = 0.5f;  // head gap, in receptor sizes…
    private const float StageStrumGapChar         = 0.08f; // …or this × the character's height (larger wins)
    private const float StageStrumBelowFeetLanes  = 2f;    // notes appear this far below the feet (receptor sizes)…
    private const float StageStrumBelowFeetChar   = 0.35f; // …or this × the character's height (larger wins)
    private const float StageStrumMinNoteLanes    = 5f;    // …and at least this far from the receptors

    /// <summary>Character strumline each renderer animates (renderer index ≠ chart in enemy mode).</summary>
    private int[]   _rendererCharStrumLine = Array.Empty<int>();
    private float[] _stageStrumFade  = Array.Empty<float>();  // 0..1, notes nearby
    private float[] _stageStrumNear  = Array.Empty<float>();  // 0..1, 1 = over the player's lanes
    private int[]   _stageStrumOwner = Array.Empty<int>();    // object index it's drawn under (-1 none)
    private bool[]  _stageStrumPlaced = Array.Empty<bool>();  // has had a position over a character
    private bool    _stageStrumlinesActive;

    /// <summary>Stage layout applies: extras setting on (never online) and 3+ strumlines.</summary>
    private bool StageStrumlinesWanted => _level != null && !HideExtras && _noteRenderers.Count >= 3;

    /// <summary>Renderer <paramref name="i"/> is drawn in the stage this frame, not in the HUD.</summary>
    private bool IsStageStrumline(int i) => _stageStrumlinesActive && i >= 1 && i < _noteRenderers.Count;

    /// <summary>Clears the per-strumline state (level load).</summary>
    private void ResetStageStrumlines()
    {
        int n = _noteRenderers.Count;
        _stageStrumFade   = new float[n];
        _stageStrumNear   = new float[n];
        _stageStrumOwner  = new int[n];
        _stageStrumPlaced = new bool[n];
        Array.Fill(_stageStrumOwner, -1);
        _stageStrumlinesActive = false;
    }

    /// <summary>Back to the HUD layout (setting turned off mid-song).</summary>
    private void LeaveStageStrumlines()
    {
        for (int i = 1; i < _noteRenderers.Count; i++)
        {
            _noteRenderers[i].ResetStagePlacement();
            _noteRenderers[i].HudRole = i == 1 ? 1 : -1;
        }
        ResetStageStrumlines();
        RepositionStrumlines();
    }

    /// <summary>First visible object currently singing renderer <paramref name="sl"/>'s chart (-1 none).</summary>
    private int FindStageStrumOwner(int sl)
    {
        if (_level == null) return -1;
        int target = sl < _rendererCharStrumLine.Length ? _rendererCharStrumLine[sl] : sl;
        var objs = _level.Scene.Objects;
        for (int j = 0; j < objs.Count && j < _objAnimators.Length; j++)
        {
            var anim = _objAnimators[j];
            if (anim == null || !anim.Visible || anim.Opacity <= 0.01f) continue;
            if ((int)objs[j].StrumLine == target) return j;  // runtime value (ObjectChartChange)
        }
        return -1;
    }

    /// <summary>
    /// Positions, scales and fades the stage strumlines. Called every frame before the scene is
    /// drawn; uses the character boxes of the previous frame's draw.
    /// </summary>
    private void UpdateStageStrumlines(float dt)
    {
        bool want = StageStrumlinesWanted;
        if (!want)
        {
            if (_stageStrumlinesActive) LeaveStageStrumlines();
            return;
        }
        if (_stageStrumFade.Length != _noteRenderers.Count) ResetStageStrumlines();
        _stageStrumlinesActive = true;

        float sw = _rtWidth, sh = _rtHeight;
        float basePs = Math.Min(sw / 1920f, sh / 1080f);  // world → screen scale at camera scale 1
        // Camera zoom without the beat zoom (the camera matrix has it, so sizes bopped on every beat)
        float camScale = Math.Max(0.01f, _cameraManager?.GetEffectiveScale(_events?.Camera) ?? 1f);
        float fadeIn  = dt / StageStrumFadeInTime;
        float fadeOut = dt / StageStrumFadeOutTime;
        float nearStep = dt / StageStrumNearFadeTime;

        // Player's lane column (what the lane underlay covers), if the player strumline is shown
        var player = _noteRenderers[0];
        bool playerShown = HudSettings.Instance.MasterHudVisible && NoteRenderer.ShowStrumlines &&
                           !(NoteRenderer.HidePlayerStrumline && !_isMultiplayerMatch) && _notesAlpha > 0.01f;
        var (playerLeft, playerRight) = player.LaneSpan();

        for (int i = 1; i < _noteRenderers.Count; i++)
        {
            var r = _noteRenderers[i];
            r.HudRole = -1;  // HUD editor offsets don't apply to stage strumlines
            r.NoArrowEffects = true;  // no beat bounces / pulses

            int owner = FindStageStrumOwner(i);
            if (owner >= 0) _stageStrumOwner[i] = owner;  // none visible: keep the last one while fading out

            if (owner >= 0 && _objAnimators[owner] is { } anim && anim.TryGetAnchorBounds(out var bMin, out var bMax))
            {
                var m = anim.LastDrawMatrix;
                var p1 = Vector2.Transform(bMin, m);
                var p2 = Vector2.Transform(bMax, m);
                float left = Math.Min(p1.X, p2.X), right = Math.Max(p1.X, p2.X);
                float top  = Math.Min(p1.Y, p2.Y), bottom = Math.Max(p1.Y, p2.Y);
                float charW = right - left, charH = bottom - top;

                // Size follows the camera zoom (camera and fixed-world matrices both scale by
                // 1 / camera scale); normalized fixed objects use their own pixel scale
                float zoom = m == Matrix.Identity
                    ? (anim.FixedPixelScale > 0f ? anim.FixedPixelScale / basePs : 1f)
                    : 1f / camScale;
                float scale = Math.Clamp(StageStrumBaseScale * zoom, StageStrumMinScale, StageStrumMaxScale);
                // Small characters: lanes no wider than StageStrumMaxCharWidth × the character
                float fitScale = StageStrumMaxCharWidth * charW / Math.Max(1f, r.LanesWidthPerScale);
                r.NoteScale = Math.Max(StageStrumTinyScale, Math.Min(scale, fitScale));

                float lanesW = r.LanesWidthPx;
                float laneH  = lanesW / NoteRenderer.LANE_COUNT;  // receptor size
                // Gap between the head and the receptors' bottom edge: grows with the strumline
                // and with the character, so short characters don't look squashed under it
                float gap = Math.Max(laneH * StageStrumGapLanes, charH * StageStrumGapChar);
                float ry = top - gap - laneH * 0.5f;
                // Stuck to the character: free to leave the screen with it (no clamping)
                r.PlaceAt((left + right) / 2f, ry);

                // Notes appear a bit below the character's feet instead of from the screen edge
                float belowFeet = Math.Max(laneH * StageStrumBelowFeetLanes, charH * StageStrumBelowFeetChar);
                r.NoteFadeDistance = Math.Max(laneH * StageStrumMinNoteLanes, bottom - ry + belowFeet);
                r.NoteFadeLength   = Math.Max(laneH * 2f, r.NoteFadeDistance * 0.3f);
                _stageStrumPlaced[i] = true;
            }

            // Shown while a note is close (and it has a character to stand on)
            bool active = owner >= 0 && _stageStrumPlaced[i] && r.HasNotesWithin(_songTime, StageStrumLookAhead);
            _stageStrumFade[i] = active
                ? Math.Min(1f, _stageStrumFade[i] + fadeIn)
                : Math.Max(0f, _stageStrumFade[i] - fadeOut);

            // Dimmed over / next to the player's lanes
            bool near = false;
            if (playerShown && _stageStrumPlaced[i])
            {
                var (l, rt) = r.LaneSpan();
                near = l < playerRight + StageStrumNearMargin && rt > playerLeft - StageStrumNearMargin;
            }
            _stageStrumNear[i] = near
                ? Math.Min(1f, _stageStrumNear[i] + nearStep)
                : Math.Max(0f, _stageStrumNear[i] - nearStep);

            float baseAlpha = i == 1 ? GetOpponentStrumlineAlpha() : _notesAlpha;
            r.Alpha = baseAlpha * _stageStrumFade[i] * MathHelper.Lerp(1f, StageStrumNearPlayerAlpha, _stageStrumNear[i]);
        }
    }

    /// <summary>Scene hook: a stage strumline is drawn right below object <paramref name="objIdx"/>.</summary>
    private bool HasStageStrumlineBelow(int objIdx)
    {
        if (!_stageStrumlinesActive) return false;
        for (int i = 1; i < _noteRenderers.Count && i < _stageStrumOwner.Length; i++)
            if (_stageStrumOwner[i] == objIdx && _noteRenderers[i].Alpha > 0.01f) return true;
        return false;
    }

    /// <summary>Scene hook: draws the stage strumlines that belong below object <paramref name="objIdx"/>.</summary>
    private void DrawStageStrumlinesBelow(int objIdx)
    {
        _spriteBatch.Begin(SpriteSortMode.Deferred, BlendState.NonPremultiplied, SamplerState.LinearClamp,
            null, null, null, RtScaleMatrix);
        for (int i = _noteRenderers.Count - 1; i >= 1; i--)
        {
            if (i >= _stageStrumOwner.Length || _stageStrumOwner[i] != objIdx) continue;
            _noteRenderers[i].Draw(_spriteBatch, NoteTime, _font);
        }
        _spriteBatch.End();
    }
}
