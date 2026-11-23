using System;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;

namespace FunkyMakerPlayer.UI;

/// <summary>
/// FNF-style health bar with animated character icons.
/// Supports two modes: FNF Base (icons move with health) and Fixed Ends (icons stay at ends).
/// </summary>
public class HealthBar
{
    private readonly Texture2D _pixel;
    private readonly SpriteBatch _spriteBatch;
    
    // Health and colors
    public float Health { get; set; } = 0.5f;
    public Color OpponentBarColor { get; set; } = new Color(0xFF, 0x00, 0x00);
    public Color PlayerBarColor { get; set; } = new Color(0x66, 0xFF, 0x66);
    
    // Icon animators
    public SpriteAnimator? OpponentIcon { get; set; }
    public SpriteAnimator? PlayerIcon { get; set; }
    public bool FlipOpponentIcon { get; set; }
    public bool FlipPlayerIcon { get; set; }
    
    // Beat bounce animation
    private float _iconBounceScale = 1.0f;
    private int _lastIconBeat = -1;
    
    // Icon mode: 0 = FNF Base (move with health), 1 = Fixed Ends
    public int IconMode { get; set; } = 0;
    
    // Border thicknesses
    private const int OUTER_SHADOW = 6;
    private const int THICK_BORDER = 4;
    private const int INNER_BORDER = 2;
    
    public HealthBar(Texture2D pixel, SpriteBatch spriteBatch)
    {
        _pixel = pixel;
        _spriteBatch = spriteBatch;
    }
    
    /// <summary>
    /// Updates icon animations and beat bounce.
    /// </summary>
    public void Update(float deltaTime, float songTime, float bpm)
    {
        // Update icon animators + switch pose based on health
        bool opponentLosing = Health > 0.8f;
        bool playerLosing = Health < 0.2f;
        
        if (OpponentIcon != null)
        {
            int targetAnim = opponentLosing ? 1 : 0;
            if (OpponentIcon.CurrentAnimIndex != targetAnim)
                OpponentIcon.SetAnimation(targetAnim);
            OpponentIcon.Update(deltaTime, songTime);
        }
        
        if (PlayerIcon != null)
        {
            int targetAnim = playerLosing ? 1 : 0;
            if (PlayerIcon.CurrentAnimIndex != targetAnim)
                PlayerIcon.SetAnimation(targetAnim);
            PlayerIcon.Update(deltaTime, songTime);
        }
        
        // Icon beat bounce (FNF style - bounce on every beat)
        int currentIconBeat = (int)(songTime / 60f * bpm);
        
        if (currentIconBeat != _lastIconBeat)
        {
            _lastIconBeat = currentIconBeat;
            _iconBounceScale = 1.15f;  // Subtle bounce to 115% size
        }
        
        // Smoothly return to normal size
        if (_iconBounceScale > 1.0f)
        {
            _iconBounceScale = Math.Max(1.0f, _iconBounceScale - deltaTime * 2.0f);
        }
    }
    
    /// <summary>
    /// Draws the health bar with gradient fill and animated icons.
    /// </summary>
    public void Draw(int screenHeight, bool downscroll)
    {
        // Get HUD settings (adjust for downscroll if needed)
        var hudSettings = downscroll 
            ? HudSettings.Instance.GetDownscrollAdjusted(screenHeight) 
            : HudSettings.Instance;
        
        // FNF-style health bar dimensions
        int totalBarW = hudSettings.HealthBarSize.X;
        int totalBarH = hudSettings.HealthBarSize.Y;
        int barX = hudSettings.HealthBarPosition.X;
        int barY = hudSettings.HealthBarPosition.Y;
        
        // Calculate inner bar size (subtract all borders from total size)
        int barW = totalBarW - (INNER_BORDER * 2);
        int barH = totalBarH - (INNER_BORDER * 2);
        
        // Adjust positions to account for borders
        int innerBarX = barX + INNER_BORDER;
        int innerBarY = barY + INNER_BORDER;
        
        // Calculate bar widths
        int playerW = (int)(Health * barW);
        int enemyW = barW - playerW;

        if (hudSettings.HealthBarVisible)
        {
            // ── Draw bar background with borders ──────────────────────────────────
            
            // Outer glow/shadow (subtle)
            DrawRect(barX - (OUTER_SHADOW - INNER_BORDER), barY - (OUTER_SHADOW - INNER_BORDER), 
                     totalBarW + (OUTER_SHADOW - INNER_BORDER) * 2, totalBarH + (OUTER_SHADOW - INNER_BORDER) * 2, 
                     Color.Black * 0.5f);
            
            // Black background with thicker border
            DrawRect(barX - (THICK_BORDER - INNER_BORDER), barY - (THICK_BORDER - INNER_BORDER), 
                     totalBarW + (THICK_BORDER - INNER_BORDER) * 2, totalBarH + (THICK_BORDER - INNER_BORDER) * 2, 
                     Color.Black * 0.9f);
            DrawRect(barX, barY, totalBarW, totalBarH, new Color(30, 30, 30));
            
            // ── Draw gradient fill ────────────────────────────────────────────────
            
            // Draw opponent side (left) with smooth gradient
            for (int i = 0; i < enemyW; i++)
            {
                float t = (float)i / barW;
                Color c = Color.Lerp(OpponentBarColor * 0.6f, OpponentBarColor * 1.1f, t * t);
                DrawRect(innerBarX + i, innerBarY, 1, barH, c);
            }
            
            // Draw player side (right) with smooth gradient
            for (int i = 0; i < playerW; i++)
            {
                float t = (float)i / barW;
                Color c = Color.Lerp(PlayerBarColor * 1.1f, PlayerBarColor * 0.6f, t * t);
                DrawRect(innerBarX + enemyW + i, innerBarY, 1, barH, c);
            }
            
            // Add subtle highlights on top and bottom edges
            DrawRect(innerBarX, innerBarY, enemyW, 1, Color.White * 0.2f);
            DrawRect(innerBarX + enemyW, innerBarY, playerW, 1, Color.White * 0.2f);
        }
        
        // ── Draw icons ────────────────────────────────────────────────────────
        
        // Fork: per-icon size from the HUD editor (base 100 px, centred 10 px below the bar top as before)
        int oppIconSize = Math.Max(8, (int)(100 * hudSettings.OpponentIconScale));
        int plrIconSize = Math.Max(8, (int)(100 * hudSettings.PlayerIconScale));

        int opponentIconX, opponentIconY, playerIconX, playerIconY;

        if (IconMode == 0) // FNF Base mode
        {
            // Icons move with the health percentage
            int dividerX = innerBarX + enemyW;

            // Get actual scaled icon widths
            int opponentActualWidth = oppIconSize;
            int playerActualWidth = plrIconSize;

            if (OpponentIcon != null)
            {
                var (tex, src) = OpponentIcon.GetCurrentFrameTexture();
                if (tex != null && src.Width > 0 && src.Height > 0)
                {
                    float scale = (float)oppIconSize / Math.Max(src.Width, src.Height);
                    opponentActualWidth = (int)(src.Width * scale);
                }
            }

            if (PlayerIcon != null)
            {
                var (tex, src) = PlayerIcon.GetCurrentFrameTexture();
                if (tex != null && src.Width > 0 && src.Height > 0)
                {
                    float scale = (float)plrIconSize / Math.Max(src.Width, src.Height);
                    playerActualWidth = (int)(src.Width * scale);
                }
            }

            // Position opponent icon (right edge at divider)
            opponentIconX = dividerX - (oppIconSize + opponentActualWidth) / 2;
            opponentIconY = barY + 10 - oppIconSize / 2;

            // Position player icon (left edge at divider)
            playerIconX = dividerX - (plrIconSize - playerActualWidth) / 2;
            playerIconY = barY + 10 - plrIconSize / 2;
        }
        else // IconMode == 1: Fixed at Ends
        {
            // Icons stay at fixed positions from HudSettings
            opponentIconX = hudSettings.OpponentIconPosition.X;
            opponentIconY = hudSettings.OpponentIconPosition.Y;
            
            playerIconX = hudSettings.PlayerIconPosition.X;
            playerIconY = hudSettings.PlayerIconPosition.Y;
        }
        
        if (hudSettings.OpponentIconVisible)
            DrawIcon(OpponentIcon, opponentIconX, opponentIconY, oppIconSize, flipX: FlipOpponentIcon, _iconBounceScale);
        if (hudSettings.PlayerIconVisible)
            DrawIcon(PlayerIcon, playerIconX, playerIconY, plrIconSize, flipX: FlipPlayerIcon, _iconBounceScale);
    }
    
    /// <summary>
    /// Resets beat bounce animation.
    /// </summary>
    public void ResetBounce()
    {
        _iconBounceScale = 1.0f;
        _lastIconBeat = -1;
    }
    
    // ── Helper Methods ────────────────────────────────────────────────────────
    
    private void DrawRect(int x, int y, int w, int h, Color color)
    {
        if (w <= 0 || h <= 0) return;
        _spriteBatch.Draw(_pixel, new Rectangle(x, y, w, h), color);
    }
    
    private void DrawIcon(SpriteAnimator? anim, int destX, int destY, int size, bool flipX, float bounceScale)
    {
        if (anim == null) return;
        
        var (tex, src) = anim.GetCurrentFrameTexture();
        if (tex == null || src.Width == 0 || src.Height == 0) return;
        
        // Scale to fit within iconSize bounding box (maintaining aspect ratio)
        float scale = (float)size / Math.Max(src.Width, src.Height);
        scale *= bounceScale;  // Apply beat bounce
        
        int drawW = (int)(src.Width * scale);
        int drawH = (int)(src.Height * scale);
        
        // Center within the bounding box
        int drawX = destX + (size - drawW) / 2;
        int drawY = destY + (size - drawH) / 2;
        
        var dest = new Rectangle(drawX, drawY, drawW, drawH);
        var fx = flipX ? SpriteEffects.FlipHorizontally : SpriteEffects.None;
        
        _spriteBatch.Draw(tex, dest, src, Color.White, 0f, Vector2.Zero, fx, 0f);
    }
}
