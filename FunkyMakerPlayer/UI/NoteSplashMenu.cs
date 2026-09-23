using System;
using System.Collections.Generic;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using Microsoft.Xna.Framework.Input;

namespace FunkyMakerPlayer.UI;

/// <summary>
/// NoteSplash selector menu with animated preview.
/// </summary>
public class NoteSplashMenu
{
    private readonly Texture2D _pixel;
    private readonly SpriteFont _font;
    private readonly FontRenderer? _fontRenderer;
    private readonly GraphicsDevice _graphicsDevice;
    
    public bool IsActive { get; set; } = false;
    public int Selection { get; set; } = 0;
    
    private List<string> _styles = new();
    private NoteSplashStyle? _previewStyle = null;
    private ParticleNoteSplash? _previewParticles = null;  // Preview for Extended particles
    private float _animTimer = 0f;
    private int _animLane = 0;
    
    public NoteSplashMenu(Texture2D pixel, SpriteFont font, GraphicsDevice graphicsDevice, FontRenderer? fontRenderer = null)
    {
        _pixel = pixel;
        _font = font;
        _graphicsDevice = graphicsDevice;
        _fontRenderer = fontRenderer;
    }
    
    public void Open(GraphicsDevice graphicsDevice)
    {
        _styles = NoteSplashStyle.GetAvailableStyles();
        
        // Select currently active style
        Selection = _styles.IndexOf(Game1.NoteSplashStyleName);
        if (Selection < 0) Selection = 0;
        
        LoadPreview(_styles[Selection], graphicsDevice);
        _animTimer = 0f;
        IsActive = true;
    }
    
    public void Close()
    {
        IsActive = false;
        _previewStyle = null;
    }
    
    private void LoadPreview(string styleName, GraphicsDevice graphicsDevice)
    {
        _previewStyle = NoteSplashStyle.LoadByName(styleName, graphicsDevice);
        
        // If it's a particle style, create preview particle system
        if (styleName.StartsWith("Particles:"))
        {
            _previewParticles = new ParticleNoteSplash(graphicsDevice);
            
            // Parse which style was selected
            if (styleName.Contains("CircleExpand"))
                _previewParticles.CurrentStyle = ParticleNoteSplash.SplashStyle.CircleExpand;
            else if (styleName.Contains("SplashDrops"))
                _previewParticles.CurrentStyle = ParticleNoteSplash.SplashStyle.SplashDrops;
            else if (styleName.Contains("StarBurst"))
                _previewParticles.CurrentStyle = ParticleNoteSplash.SplashStyle.StarBurst;
            else if (styleName.Contains("Explosion"))
                _previewParticles.CurrentStyle = ParticleNoteSplash.SplashStyle.Explosion;
            else if (styleName.Contains("RingPulse"))
                _previewParticles.CurrentStyle = ParticleNoteSplash.SplashStyle.RingPulse;
            else if (styleName.Contains("Randomizer"))
                _previewParticles.CurrentStyle = ParticleNoteSplash.SplashStyle.Randomizer;
        }
        else
        {
            _previewParticles = null;
        }
    }
    
    public void Update(KeyboardState kb, KeyboardState prevKb, float deltaTime, GraphicsDevice graphicsDevice, List<NoteRenderer> noteRenderers)
    {
        if (!IsActive) return;
        
        // Update animation timer (loops continuously)
        _animTimer += deltaTime;
        float loopDuration = _previewStyle?.Duration ?? 0.5f;  // Use 0.5s for particles
        if (_animTimer >= loopDuration + 0.5f)  // Add 0.5s pause between loops
        {
            _animTimer = 0f;
            _animLane = (_animLane + 1) % 4;  // Cycle through lanes
            
            // Trigger particle preview splash
            if (_previewParticles != null)
            {
                // Calculate preview position (center of preview area)
                int sw = _graphicsDevice.Viewport.Width;
                int sh = _graphicsDevice.Viewport.Height;
                float previewX = sw * 0.42f;
                float previewCx = previewX + (sw - previewX - 16f) / 2f;
                float previewCy = sh / 2f;
                
                _previewParticles.Trigger(_animLane, new Vector2(previewCx, previewCy));
            }
        }
        
        // Update particle system
        if (_previewParticles != null)
        {
            _previewParticles.Update(new GameTime(TimeSpan.Zero, TimeSpan.FromSeconds(deltaTime)));
        }
        
        // Navigation
        if (kb.IsKeyDown(Keys.Up) && !prevKb.IsKeyDown(Keys.Up))
        {
            Selection = (Selection - 1 + _styles.Count) % _styles.Count;
            LoadPreview(_styles[Selection], graphicsDevice);
            _animTimer = 0f;
        }
        
        if (kb.IsKeyDown(Keys.Down) && !prevKb.IsKeyDown(Keys.Down))
        {
            Selection = (Selection + 1) % _styles.Count;
            LoadPreview(_styles[Selection], graphicsDevice);
            _animTimer = 0f;
        }
        
        // Confirm selection
        if ((kb.IsKeyDown(Keys.Enter) && !prevKb.IsKeyDown(Keys.Enter)) ||
            (kb.IsKeyDown(Keys.Space) && !prevKb.IsKeyDown(Keys.Space)))
        {
            Game1.NoteSplashStyleName = _styles[Selection];
            
            // Apply to all note renderers
            var splashStyle = NoteSplashStyle.LoadByName(Game1.NoteSplashStyleName, graphicsDevice);
            
            // If it's a particle style, recreate the particle splash system
            ParticleNoteSplash? particleSplash = null;
            if (Game1.NoteSplashStyleName.StartsWith("Particles:"))
            {
                particleSplash = new ParticleNoteSplash(graphicsDevice);
                
                // Parse which style was selected
                if (Game1.NoteSplashStyleName.Contains("CircleExpand"))
                    particleSplash.CurrentStyle = ParticleNoteSplash.SplashStyle.CircleExpand;
                else if (Game1.NoteSplashStyleName.Contains("SplashDrops"))
                    particleSplash.CurrentStyle = ParticleNoteSplash.SplashStyle.SplashDrops;
                else if (Game1.NoteSplashStyleName.Contains("StarBurst"))
                    particleSplash.CurrentStyle = ParticleNoteSplash.SplashStyle.StarBurst;
                else if (Game1.NoteSplashStyleName.Contains("Explosion"))
                    particleSplash.CurrentStyle = ParticleNoteSplash.SplashStyle.Explosion;
                else if (Game1.NoteSplashStyleName.Contains("RingPulse"))
                    particleSplash.CurrentStyle = ParticleNoteSplash.SplashStyle.RingPulse;
                else if (Game1.NoteSplashStyleName.Contains("Randomizer"))
                    particleSplash.CurrentStyle = ParticleNoteSplash.SplashStyle.Randomizer;
                
                Console.WriteLine($"[NoteSplashMenu] Applied particle style: {particleSplash.CurrentStyle}");
            }
            
            foreach (var r in noteRenderers)
            {
                r.SetSplashStyle(splashStyle);
                r.SetParticleSplash(particleSplash);
            }
            
            Settings.Save(true);  // Save settings
            Close();
        }
        
        // Cancel
        if (kb.IsKeyDown(Keys.Escape) && !prevKb.IsKeyDown(Keys.Escape))
        {
            Close();
        }
    }
    
    public void Draw(SpriteBatch spriteBatch, int screenWidth, int screenHeight)
    {
        if (!IsActive) return;
        
        int sw = screenWidth;
        int sh = screenHeight;
        
        Color accentColor = new Color(0x00, 0xFF, 0xFF);  // Cyan accent
        
        // Background
        DrawRect(spriteBatch, 0, 0, sw, sh, Color.Black * 0.92f);
        
        // Header
        DrawRect(spriteBatch, 0, 0, sw, 54, new Color(15, 15, 28));
        DrawRect(spriteBatch, 0, 52, sw, 2, accentColor * 0.7f);
        DrawRect(spriteBatch, 0, 0, 4, 54, accentColor);
        
        if (_fontRenderer != null)
        {
            int titleSize = (int)(sh * 0.055f);
            titleSize = Math.Clamp(titleSize, 32, 64);
            _fontRenderer.DrawString(spriteBatch, "NOTE SPLASH",
                new Vector2(20, 10), accentColor, titleSize);
        }
        else
        {
            spriteBatch.DrawString(_font, "NOTE SPLASH",
                new Vector2(20, 10), accentColor, 0f, Vector2.Zero, 1.3f, SpriteEffects.None, 0f);
        }
        
        // ── Left panel: splash style list ─────────────────────────────────────
        float listX = 16f;
        float listW = sw * 0.38f;
        float listY = 64f;
        float itemH = 38f;
        
        if (_fontRenderer != null)
        {
            int labelSize = (int)(sh * 0.028f);
            labelSize = Math.Clamp(labelSize, 16, 28);
            _fontRenderer.DrawString(spriteBatch, "STYLES",
                new Vector2(listX + 8, listY), accentColor * 0.6f, labelSize);
        }
        else
        {
            spriteBatch.DrawString(_font, "STYLES",
                new Vector2(listX + 8, listY),
                accentColor * 0.6f, 0f, Vector2.Zero, 0.65f, SpriteEffects.None, 0f);
        }
        listY += 22f;
        
        for (int i = 0; i < _styles.Count; i++)
        {
            float iy = listY + i * itemH;
            bool sel = i == Selection;
            bool cur = _styles[i] == Game1.NoteSplashStyleName;
            
            if (sel)
            {
                DrawRect(spriteBatch, (int)listX, (int)(iy - 2), (int)listW, (int)(itemH - 2),
                    accentColor * 0.18f);
                DrawRect(spriteBatch, (int)listX, (int)(iy - 2), 3, (int)(itemH - 2),
                    accentColor);
            }
            
            Color textColor = sel ? Color.White : (cur ? accentColor : Color.White * 0.6f);
            string label = _styles[i] + (cur ? " (active)" : "");
            
            if (_fontRenderer != null)
            {
                int itemSize = (int)(sh * 0.032f);
                itemSize = Math.Clamp(itemSize, 18, 32);
                _fontRenderer.DrawString(spriteBatch, label,
                    new Vector2(listX + 12, iy + 8), textColor, itemSize);
            }
            else
            {
                spriteBatch.DrawString(_font, label,
                    new Vector2(listX + 12, iy + 8),
                    textColor, 0f, Vector2.Zero, 0.82f, SpriteEffects.None, 0f);
            }
        }
        
        // ── Right panel: splash preview ────────────────────────────────────────
        float previewX = sw * 0.42f;
        float previewCx = previewX + (sw - previewX - 16f) / 2f;
        float previewCy = sh / 2f;
        
        if (_fontRenderer != null)
        {
            int previewLabelSize = (int)(sh * 0.028f);
            previewLabelSize = Math.Clamp(previewLabelSize, 16, 28);
            _fontRenderer.DrawString(spriteBatch, "PREVIEW",
                new Vector2(previewX + 8, 64f), accentColor * 0.6f, previewLabelSize);
        }
        else
        {
            spriteBatch.DrawString(_font, "PREVIEW",
                new Vector2(previewX + 8, 64f),
                accentColor * 0.6f, 0f, Vector2.Zero, 0.65f, SpriteEffects.None, 0f);
        }
        
        // Show info about current style
        var style = _previewStyle;
        string selectedName = _styles[Selection];
        float infoY = 100f;
        Color infoColor = Color.White * 0.7f;
        int infoSize = (int)(sh * 0.030f);
        infoSize = Math.Clamp(infoSize, 16, 28);
        
        if (selectedName.StartsWith("Particles:"))
        {
            // Show info for particle-based splash
            if (_fontRenderer != null)
            {
                _fontRenderer.DrawString(spriteBatch, "Type: Programmatic (Extended)",
                    new Vector2(previewX + 8, infoY), infoColor, infoSize);
                infoY += 24f;
                int descSize = (int)(infoSize * 0.85f);
                _fontRenderer.DrawString(spriteBatch, "Procedural visual effects",
                    new Vector2(previewX + 8, infoY), infoColor * 0.6f, descSize);
                infoY += 20f;
                _fontRenderer.DrawString(spriteBatch, "No textures required",
                    new Vector2(previewX + 8, infoY), infoColor * 0.6f, descSize);
            }
            else
            {
                spriteBatch.DrawString(_font, "Type: Programmatic (Extended)",
                    new Vector2(previewX + 8, infoY), infoColor, 0f, Vector2.Zero, 0.7f, SpriteEffects.None, 0f);
                infoY += 24f;
                spriteBatch.DrawString(_font, "Procedural visual effects",
                    new Vector2(previewX + 8, infoY), infoColor * 0.6f, 0f, Vector2.Zero, 0.65f, SpriteEffects.None, 0f);
                infoY += 20f;
                spriteBatch.DrawString(_font, "No textures required",
                    new Vector2(previewX + 8, infoY), infoColor * 0.6f, 0f, Vector2.Zero, 0.65f, SpriteEffects.None, 0f);
            }
        }
        else if (style != null && style.HasTextures)
        {
            if (_fontRenderer != null)
            {
                _fontRenderer.DrawString(spriteBatch, "Type: Texture-based",
                    new Vector2(previewX + 8, infoY), infoColor, infoSize);
                infoY += 24f;
                _fontRenderer.DrawString(spriteBatch, $"FPS: {style.AnimationFps}",
                    new Vector2(previewX + 8, infoY), infoColor, infoSize);
                infoY += 24f;
                _fontRenderer.DrawString(spriteBatch, $"Duration: {style.Duration:F2}s",
                    new Vector2(previewX + 8, infoY), infoColor, infoSize);
                infoY += 24f;
                _fontRenderer.DrawString(spriteBatch, $"Scale: {style.Scale:F2}x",
                    new Vector2(previewX + 8, infoY), infoColor, infoSize);
            }
            else
            {
                spriteBatch.DrawString(_font, "Type: Texture-based",
                    new Vector2(previewX + 8, infoY), infoColor, 0f, Vector2.Zero, 0.7f, SpriteEffects.None, 0f);
                infoY += 24f;
                spriteBatch.DrawString(_font, $"FPS: {style.AnimationFps}",
                    new Vector2(previewX + 8, infoY), infoColor, 0f, Vector2.Zero, 0.7f, SpriteEffects.None, 0f);
                infoY += 24f;
                spriteBatch.DrawString(_font, $"Duration: {style.Duration:F2}s",
                    new Vector2(previewX + 8, infoY), infoColor, 0f, Vector2.Zero, 0.7f, SpriteEffects.None, 0f);
                infoY += 24f;
                spriteBatch.DrawString(_font, $"Scale: {style.Scale:F2}x",
                    new Vector2(previewX + 8, infoY), infoColor, 0f, Vector2.Zero, 0.7f, SpriteEffects.None, 0f);
            }
        }
        else
        {
            if (_fontRenderer != null)
            {
                _fontRenderer.DrawString(spriteBatch, "Type: Programmatic (Default)",
                    new Vector2(previewX + 8, infoY), infoColor, infoSize);
                infoY += 24f;
                int descSize = (int)(infoSize * 0.85f);
                _fontRenderer.DrawString(spriteBatch, "Simple particle burst effect",
                    new Vector2(previewX + 8, infoY), infoColor * 0.6f, descSize);
                infoY += 20f;
                _fontRenderer.DrawString(spriteBatch, "No copyright issues",
                    new Vector2(previewX + 8, infoY), infoColor * 0.6f, descSize);
            }
            else
            {
                spriteBatch.DrawString(_font, "Type: Programmatic (Default)",
                    new Vector2(previewX + 8, infoY), infoColor, 0f, Vector2.Zero, 0.7f, SpriteEffects.None, 0f);
                infoY += 24f;
                spriteBatch.DrawString(_font, "Simple particle burst effect",
                    new Vector2(previewX + 8, infoY), infoColor * 0.6f, 0f, Vector2.Zero, 0.65f, SpriteEffects.None, 0f);
                infoY += 20f;
                spriteBatch.DrawString(_font, "No copyright issues",
                    new Vector2(previewX + 8, infoY), infoColor * 0.6f, 0f, Vector2.Zero, 0.65f, SpriteEffects.None, 0f);
            }
        }
        
        // ── Draw animated splash preview in center ────────────────────────────
        float receptorSize = NoteRenderer.LANE_WIDTH * 0.8f;
        
        // Lane colors for splash
        Color[] laneColors = {
            new Color(0xC2, 0x4B, 0x99),  // Purple (Left)
            new Color(0x00, 0xFF, 0xFF),  // Cyan (Down)
            new Color(0x12, 0xFA, 0x05),  // Green (Up)
            new Color(0xF9, 0x39, 0x3F)   // Red (Right)
        };
        
        // Draw lane indicator
        string[] laneNames = { "LEFT", "DOWN", "UP", "RIGHT" };
        if (_fontRenderer != null)
        {
            int laneSize = (int)(sh * 0.030f);
            laneSize = Math.Clamp(laneSize, 16, 28);
            Vector2 laneMeasure = _fontRenderer.MeasureString(laneNames[_animLane], laneSize);
            _fontRenderer.DrawString(spriteBatch, laneNames[_animLane],
                new Vector2(previewCx - laneMeasure.X / 2f, previewCy + 60),
                laneColors[_animLane] * 0.7f, laneSize);
        }
        else
        {
            spriteBatch.DrawString(_font, laneNames[_animLane],
                new Vector2(previewCx - 25, previewCy + 60),
                laneColors[_animLane] * 0.7f, 0f, Vector2.Zero, 0.7f, SpriteEffects.None, 0f);
        }
        
        // Draw splash animation
        float loopDuration = style?.Duration ?? 0.5f;
        
        // If using particle preview, draw it with Extended
        if (_previewParticles != null)
        {
            _previewParticles.Draw(spriteBatch);
        }
        else if (_animTimer < loopDuration)  // Only draw during animation duration
        {
            if (style != null && style.HasTextures && style.SplashFrames[_animLane] != null)
            {
                // Texture-based splash
                var frames = style.SplashFrames[_animLane]!;
                float progress = _animTimer / loopDuration;
                int frameIndex = (int)(progress * frames.Count);
                if (frameIndex >= frames.Count) frameIndex = frames.Count - 1;
                
                var frame = frames[frameIndex];
                float splashScale = 1.5f * style.Scale;  // Larger for preview
                float alpha = 1f - progress * 0.5f;  // Fade slightly
                
                Color tint = style.ColorizeByLane ? laneColors[_animLane] : Color.White;
                tint *= alpha;
                
                // Center the splash frame
                float drawX = previewCx - frame.FrameW * splashScale / 2f + (-frame.OffsetX) * splashScale;
                float drawY = previewCy - frame.FrameH * splashScale / 2f + (-frame.OffsetY) * splashScale;
                
                spriteBatch.Draw(frame.Texture, new Vector2(drawX, drawY), frame.Source, tint,
                    0f, Vector2.Zero, splashScale, SpriteEffects.None, 0f);
            }
            else
            {
                // Programmatic splash (particle burst)
                float progress = _animTimer / loopDuration;
                float splashAlpha = (1f - progress) * 0.8f;
                float splashScale = 1f + progress * 0.5f;
                
                Color splashColor = laneColors[_animLane] * splashAlpha;
                float splashSize = receptorSize * splashScale;
                float particleLen = splashSize * 0.4f;
                float particleThick = 8f;
                float offset = splashSize * 0.3f * progress;
                
                // Draw 4 particles in + pattern
                DrawRect(spriteBatch, (int)(previewCx - particleLen/2), (int)(previewCy - particleThick/2 - offset),
                         (int)particleLen, (int)particleThick, splashColor);  // Top
                DrawRect(spriteBatch, (int)(previewCx - particleLen/2), (int)(previewCy - particleThick/2 + offset),
                         (int)particleLen, (int)particleThick, splashColor);  // Bottom
                DrawRect(spriteBatch, (int)(previewCx - particleThick/2 - offset), (int)(previewCy - particleLen/2),
                         (int)particleThick, (int)particleLen, splashColor);  // Left
                DrawRect(spriteBatch, (int)(previewCx - particleThick/2 + offset), (int)(previewCy - particleLen/2),
                         (int)particleThick, (int)particleLen, splashColor);  // Right
            }
        }
        
        // Footer hint
        string hint = "UP/DOWN  Select    ENTER  Apply    ESC  Back";
        if (_fontRenderer != null)
        {
            int hintSize = (int)(sh * 0.023f);
            hintSize = Math.Clamp(hintSize, 12, 22);
            Vector2 hintMeasure = _fontRenderer.MeasureString(hint, hintSize);
            _fontRenderer.DrawString(spriteBatch, hint,
                new Vector2(sw / 2f - hintMeasure.X / 2f, sh - 22),
                Color.White * 0.2f, hintSize);
        }
        else
        {
            var hintSz = _font.MeasureString(hint) * 0.55f;
            spriteBatch.DrawString(_font, hint,
                new Vector2(sw / 2f - hintSz.X / 2f, sh - 22),
                Color.White * 0.2f, 0f, Vector2.Zero, 0.55f, SpriteEffects.None, 0f);
        }
    }
    
    private void DrawRect(SpriteBatch sb, int x, int y, int w, int h, Color c)
    {
        if (w <= 0 || h <= 0) return;
        sb.Draw(_pixel, new Rectangle(x, y, w, h), c);
    }
}
