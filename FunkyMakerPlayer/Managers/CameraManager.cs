using System;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using MonoGame.Extended;

namespace FunkyMakerPlayer.Managers;

/// <summary>
/// Manages camera positioning, zoom, and beat-synced effects.
/// Handles both automatic camera following and manual zoom override.
/// </summary>
public class CameraManager
{
    // ── Camera State ──────────────────────────────────────────────────────────
    private float _camScaleOverride = 0f;  // Manual zoom override (0 = use auto)
    private float _camZoom = 1.0f;         // Current zoom level (beat-synced)
    private int   _lastBeat = -1;          // Last beat that triggered zoom
    private Matrix _lastCameraMatrix = Matrix.Identity;
    
    // ── MonoGame.Extended Camera (Beta) ───────────────────────────────────────
    private OrthographicCamera? _extendedCamera;  // Extended camera for scrollFactor support
    public bool UseExtendedCamera { get; set; } = false;  // Toggle for beta feature

    // ── Constants ─────────────────────────────────────────────────────────────
    private const float ZOOM_BEAT_AMOUNT = 0.03f;   // How much to zoom on beat (3%)
    private const float ZOOM_LERP_SPEED = 4.5f;     // How fast zoom returns to normal
    private const float CAM_W = 1920f;              // Camera viewport width
    private const float CAM_H = 1080f;              // Camera viewport height

    // ── Properties ────────────────────────────────────────────────────────────
    public float ScaleOverride
    {
        get => _camScaleOverride;
        set => _camScaleOverride = Math.Clamp(value, 0f, 5f);
    }

    public float CurrentZoom => _camZoom;
    public Matrix LastCameraMatrix => _lastCameraMatrix;
    public bool HasManualOverride => _camScaleOverride > 0f;

    // ── Initialization ────────────────────────────────────────────────────────

    public CameraManager()
    {
        Console.WriteLine("[CameraManager] Initialized");
    }
    
    /// <summary>
    /// Initializes MonoGame.Extended OrthographicCamera for parallax support.
    /// Call this after graphics device is available.
    /// </summary>
    public void InitializeExtendedCamera(GraphicsDevice graphicsDevice)
    {
        _extendedCamera = new OrthographicCamera(graphicsDevice);
        Console.WriteLine("[CameraManager] Extended camera initialized");
    }

    /// <summary>
    /// Resets camera state (called when loading new level or restarting).
    /// </summary>
    public void Reset()
    {
        _camZoom = 1.0f;
        _lastBeat = -1;
        _camScaleOverride = 0f;
        _lastCameraMatrix = Matrix.Identity;
        
        Console.WriteLine("[CameraManager] Reset");
    }

    // ── Beat-Synced Zoom ──────────────────────────────────────────────────────

    /// <summary>
    /// Updates camera zoom with beat detection and lerp.
    /// Call this every frame during gameplay.
    /// </summary>
    public void UpdateBeatZoom(int currentBeat, float deltaTime)
    {
        // Trigger beat zoom every 4 beats (every measure)
        if (currentBeat != _lastBeat && currentBeat % 4 == 0)
        {
            _lastBeat = currentBeat;
            _camZoom += ZOOM_BEAT_AMOUNT;  // Add zoom instantly
        }
        
        // Smoothly lerp zoom back to 1.0
        _camZoom = MathHelper.Lerp(_camZoom, 1.0f, ZOOM_LERP_SPEED * deltaTime);
    }
    
    /// <summary>
    /// Updates MonoGame.Extended camera position and zoom.
    /// Call this every frame when UseExtendedCamera is enabled.
    /// </summary>
    public void UpdateExtendedCamera(CameraState camera, int screenWidth, int screenHeight, bool hasCameraEvents)
    {
        if (_extendedCamera == null) return;
        
        // Manual override ALWAYS takes priority
        float scale = (_camScaleOverride > 0f) ? _camScaleOverride : camera.Scale;
        
        // Apply beat zoom only when no camera events and no manual override
        float finalScale = (hasCameraEvents && _camScaleOverride == 0f) ? scale : (scale / _camZoom);
        
        // Set camera position (OrthographicCamera uses center position)
        _extendedCamera.Position = new Vector2(camera.X + camera.ShakeOffsetX, camera.Y + camera.ShakeOffsetY);
        
        // Set zoom (Extended camera uses Zoom property, not Scale)
        _extendedCamera.Zoom = finalScale;
        
        // Update viewport size if changed
        if (_extendedCamera.BoundingRectangle.Width != screenWidth || 
            _extendedCamera.BoundingRectangle.Height != screenHeight)
        {
            // OrthographicCamera adjusts to viewport automatically
        }
    }

    // ── Manual Zoom Control ───────────────────────────────────────────────────

    /// <summary>
    /// Increases manual zoom override by 0.1.
    /// </summary>
    public void IncreaseZoom(float currentAutoScale)
    {
        if (_camScaleOverride == 0f)
            _camScaleOverride = currentAutoScale;
        
        _camScaleOverride = MathF.Round(Math.Min(5.0f, _camScaleOverride + 0.1f), 2);
        Console.WriteLine($"[CameraManager] Manual zoom: {_camScaleOverride:F2}");
    }

    /// <summary>
    /// Decreases manual zoom override by 0.1.
    /// </summary>
    public void DecreaseZoom(float currentAutoScale)
    {
        if (_camScaleOverride == 0f)
            _camScaleOverride = currentAutoScale;
        
        _camScaleOverride = MathF.Round(Math.Max(0.1f, _camScaleOverride - 0.1f), 2);
        Console.WriteLine($"[CameraManager] Manual zoom: {_camScaleOverride:F2}");
    }

    /// <summary>
    /// Resets manual zoom override to use automatic camera scale.
    /// </summary>
    public void ResetScaleOverride()
    {
        _camScaleOverride = 0f;
        Console.WriteLine("[CameraManager] Manual zoom reset to auto");
    }

    // ── Camera Matrix Building ────────────────────────────────────────────────

    /// <summary>
    /// Builds the camera transformation matrix for world-space objects.
    /// Applies camera position, scale, shake offsets, and beat-synced zoom.
    /// </summary>
    public Matrix BuildCameraMatrix(int screenWidth, int screenHeight, CameraState camera, bool hasCameraEvents)
    {
        // If using Extended camera, return its view matrix
        if (UseExtendedCamera && _extendedCamera != null)
        {
            _lastCameraMatrix = _extendedCamera.GetViewMatrix();
            return _lastCameraMatrix;
        }
        
        // Original matrix building logic
        // Manual override ALWAYS takes priority if set (even with camera events)
        float scale = (_camScaleOverride > 0f) ? _camScaleOverride : camera.Scale;

        // Apply beat zoom ONLY when there are no camera events AND no manual override
        // Beat zoom (FNF style) - divide to zoom out on beat
        // _camZoom starts at 1.0, increases to ~1.03 on beat, then lerps back to 1.0
        // scale / 1.03 = slightly zoomed out, scale / 1.0 = normal
        float finalScale = (hasCameraEvents && _camScaleOverride == 0f) ? scale : (scale / _camZoom);

        float pixelScale = Math.Min(
            (float)screenWidth / (CAM_W * finalScale),
            (float)screenHeight / (CAM_H * finalScale));

        // Build matrix: translate by camera position (with shake), scale, then center on screen
        var matrix = Matrix.CreateTranslation(-(camera.X + camera.ShakeOffsetX), -(camera.Y + camera.ShakeOffsetY), 0f)
                   * Matrix.CreateScale(pixelScale, pixelScale, 1f)
                   * Matrix.CreateTranslation(screenWidth / 2f, screenHeight / 2f, 0f);

        _lastCameraMatrix = matrix;
        return matrix;
    }

    /// <summary>
    /// Builds a fixed zoom matrix for objects that should zoom with beat
    /// but not follow camera movement.
    /// </summary>
    public Matrix BuildFixedZoomMatrix(int screenWidth, int screenHeight, bool isLandscape)
    {
        float worldW = isLandscape ? 2160f : 1920f;
        float worldH = isLandscape ? 1920f : 2160f;
        float scale = _camScaleOverride > 0f ? _camScaleOverride : 1f;

        float pixelScale = Math.Min(
            (float)screenWidth / (worldW * scale),
            (float)screenHeight / (worldH * scale));

        // Center the world in screen space (fixed position, no camera offset)
        float offsetX = (screenWidth - worldW * pixelScale) * 0.5f;
        float offsetY = (screenHeight - worldH * pixelScale) * 0.5f;

        return Matrix.CreateScale(pixelScale)
             * Matrix.CreateTranslation(offsetX, offsetY, 0);
    }

    /// <summary>
    /// Builds a fixed matrix for objects that don't move with camera.
    /// Uses same pixel scale as camera matrix for consistency.
    /// </summary>
    public Matrix BuildFixedMatrix(int screenWidth, int screenHeight, bool isLandscape, float camScale)
    {
        float worldW = isLandscape ? 2160f : 1920f;
        float worldH = isLandscape ? 1920f : 2160f;
        
        float effectiveScale = _camScaleOverride > 0f ? _camScaleOverride : camScale;

        float pixelScale = Math.Min(
            (float)screenWidth / (CAM_W * effectiveScale),
            (float)screenHeight / (CAM_H * effectiveScale));

        // Pivot around world center (worldW/2, worldH/2) so fixed objects stay centered
        return Matrix.CreateTranslation(-worldW / 2f, -worldH / 2f, 0f)
             * Matrix.CreateScale(pixelScale, pixelScale, 1f)
             * Matrix.CreateTranslation(screenWidth / 2f, screenHeight / 2f, 0f);
    }

    /// <summary>
    /// Gets the current effective camera scale (manual or auto).
    /// </summary>
    public float GetEffectiveScale(CameraState? camera)
    {
        if (_camScaleOverride > 0f)
            return _camScaleOverride;
        
        return camera?.Scale ?? 1f;
    }

    /// <summary>
    /// Gets pixel scale for debug display calculations.
    /// </summary>
    public float GetPixelScale(int screenWidth, int screenHeight, CameraState? camera, bool isLandscape)
    {
        float worldW = isLandscape ? 2160f : 1920f;
        float worldH = isLandscape ? 1920f : 2160f;
        float scale = GetEffectiveScale(camera);

        return Math.Min(
            (float)screenWidth / (worldW * scale),
            (float)screenHeight / (worldH * scale));
    }

    /// <summary>
    /// Checks if camera zoom can be manually overridden.
    /// (Only allowed when no camera events are active)
    /// </summary>
    public bool CanOverrideZoom(bool hasCameraEvents)
    {
        return !hasCameraEvents;
    }
}
