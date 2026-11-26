using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using Microsoft.Xna.Framework.Input;
using System;

namespace FunkyMakerPlayer;

/// <summary>
/// Pause menu UI with crisp TrueType font rendering.
/// Drawn directly to window after letterboxing for best quality.
/// </summary>
public class PauseMenu
{
    private readonly Texture2D _pixel;
    private readonly SpriteFont _font;
    private readonly FontRenderer? _fontRenderer;
    private bool _isMultiplayer = false;
    
    public int Selection { get; set; } = 0;

    // Item hit boxes in window coordinates, recorded by Draw (fork: tap / click an item).
    private readonly Rectangle[] _itemRects = new Rectangle[5];
    private Point _lastPointer = new(-1, -1);

    /// <summary>When true, displays live-match pause options (Resume, Settings, Exit Match) without freezing gameplay.</summary>
    public bool IsMultiplayer
    {
        get => _isMultiplayer;
        set
        {
            if (_isMultiplayer != value)
            {
                _isMultiplayer = value;
                Selection = Math.Clamp(Selection, 0, Math.Max(0, ItemCount - 1));
            }
        }
    }

    public int ItemCount => _isMultiplayer
        ? 3
        :
#if ANDROID
        4;
#else
        5;
#endif
    
    /// <summary>
    /// Actions that can be returned by HandleInput
    /// </summary>
    public enum PauseAction
    {
        None,
        Resume,
        RestartSong,
        OpenHudEditor,
        OpenSettings,
        ExitToMenu
    }
    
    public PauseMenu(Texture2D pixel, SpriteFont font, FontRenderer? fontRenderer = null)
    {
        _pixel = pixel;
        _font = font;
        _fontRenderer = fontRenderer;
    }
    
    /// <summary>
    /// Handle input for pause menu navigation and selection.
    /// Returns the action that should be executed.
    /// </summary>
    public PauseAction HandleInput(KeyboardState kb, KeyboardState prevKb) =>
        HandleInput(kb, prevKb, default, default);

    /// <summary>Keyboard plus pointer: hovering an item selects it, a click / tap runs it.</summary>
    public PauseAction HandleInput(KeyboardState kb, KeyboardState prevKb, MouseState ms, MouseState prevMs)
    {
        // Pointer (fork): the menu was keyboard / D-pad only, also on phones.
        if (ms.Position != _lastPointer)
        {
            _lastPointer = ms.Position;
            for (int i = 0; i < ItemCount; i++)
                if (_itemRects[i].Contains(ms.Position)) { Selection = i; break; }
        }
        if (ms.LeftButton == ButtonState.Pressed && prevMs.LeftButton == ButtonState.Released)
        {
            for (int i = 0; i < ItemCount; i++)
            {
                if (!_itemRects[i].Contains(ms.Position)) continue;
                Selection = i;
                return ActionFor(i);
            }
        }

        // Navigation
        if (kb.IsKeyDown(Keys.Up) && !prevKb.IsKeyDown(Keys.Up))
            Selection = (Selection - 1 + ItemCount) % ItemCount;
        
        if (kb.IsKeyDown(Keys.Down) && !prevKb.IsKeyDown(Keys.Down))
            Selection = (Selection + 1) % ItemCount;
        
        // Selection with Enter or Space
        if ((kb.IsKeyDown(Keys.Enter) && !prevKb.IsKeyDown(Keys.Enter)) ||
            (kb.IsKeyDown(Keys.Space) && !prevKb.IsKeyDown(Keys.Space)))
            return ActionFor(Selection);

        return PauseAction.None;
    }
    
    private PauseAction ActionFor(int item)
    {
        if (_isMultiplayer)
        {
            return item switch
            {
                0 => PauseAction.Resume,
                1 => PauseAction.OpenSettings,
                2 => PauseAction.ExitToMenu,
                _ => PauseAction.None
            };
        }
#if ANDROID
        return item switch
        {
            0 => PauseAction.Resume,
            1 => PauseAction.RestartSong,
            2 => PauseAction.OpenSettings,
            3 => PauseAction.ExitToMenu,
            _ => PauseAction.None
        };
#else
        return item switch
        {
            0 => PauseAction.Resume,
            1 => PauseAction.RestartSong,
            2 => PauseAction.OpenHudEditor,
            3 => PauseAction.OpenSettings,
            4 => PauseAction.ExitToMenu,
            _ => PauseAction.None
        };
#endif
    }

    /// <summary>
    /// Draw pause menu adjusted to letterbox window coordinates.
    /// </summary>
    public void Draw(SpriteBatch spriteBatch, int offsetX, int offsetY, int displayW, int displayH, 
        float scale, int rtWidth, int rtHeight, GameTime gameTime, 
        int score = 0, float accuracy = 100f, int combo = 0, int misses = 0,
        int sicks = 0, int goods = 0, int bads = 0, int shits = 0)
    {
        // Transform render target coordinates to window coordinates
        float TransformX(float rtX) => offsetX + rtX * scale;
        float TransformY(float rtY) => offsetY + rtY * scale;
        float TransformSize(float rtSize) => rtSize * scale;
        
        // Use render target dimensions for layout calculations
        int sw = rtWidth;
        int sh = rtHeight;
        
        // Dark overlay
        DrawRect(spriteBatch, offsetX, offsetY, displayW, displayH, Color.Black * 0.55f);
        
        // Subtle left-side backdrop for the FMM-style left-aligned pause menu
        float leftBackdropW = TransformSize(460f);
        DrawRect(spriteBatch, offsetX, offsetY, (int)leftBackdropW, displayH, new Color(14, 12, 22) * 0.65f);
        DrawRect(spriteBatch, offsetX + (int)leftBackdropW, offsetY, Math.Max(2, (int)TransformSize(2f)), displayH, new Color(255, 68, 136) * 0.35f);
        
        // Colors
        Color accentColor = new Color(255, 68, 136);
        
        // ── Left-aligned Menu Area (FMM original style) ────────────────────────
        float menuX = TransformX(75f);
        float menuY = TransformY(220f);
        
        // "PAUSED" title - RESPONSIVE
        string title = "PAUSED";
        int titleFontSize = (int)(displayH * 0.10f);
        titleFontSize = Math.Clamp(titleFontSize, 52, 128);
        
        float titleX = menuX - TransformSize(10f);
        float titleY = TransformY(85f);
        
        // Title with shadow
        if (_fontRenderer != null)
        {
            _fontRenderer.DrawString(spriteBatch, title,
                new Vector2(titleX + 4, titleY + 4),
                Color.Black * 0.6f, titleFontSize);
            _fontRenderer.DrawString(spriteBatch, title,
                new Vector2(titleX, titleY),
                Color.White, titleFontSize);
        }
        else
        {
            spriteBatch.DrawString(_font, title,
                new Vector2(titleX + 4, titleY + 4),
                Color.Black * 0.6f, 0f, Vector2.Zero, 2.1f, SpriteEffects.None, 0f);
            spriteBatch.DrawString(_font, title,
                new Vector2(titleX, titleY),
                Color.White, 0f, Vector2.Zero, 2.1f, SpriteEffects.None, 0f);
        }
        
        // Accent underline beneath PAUSED
        Color headerAccent = _isMultiplayer ? new Color(255, 190, 60) : accentColor;
        DrawRect(spriteBatch, (int)titleX, (int)(titleY + TransformSize(82f)), (int)TransformSize(240f), Math.Max(3, (int)TransformSize(4f)), headerAccent);

        if (_isMultiplayer)
        {
            string liveBadge = "LIVE MATCH - GAMEPLAY CONTINUES";
            int liveFontSize = Math.Clamp((int)(displayH * 0.022f), 12, 24);
            float liveY = titleY + TransformSize(94f);
            if (_fontRenderer != null)
            {
                _fontRenderer.DrawString(spriteBatch, liveBadge, new Vector2(titleX, liveY), headerAccent, liveFontSize);
            }
            else
            {
                spriteBatch.DrawString(_font, liveBadge, new Vector2(titleX, liveY), headerAccent, 0f, Vector2.Zero, 0.72f, SpriteEffects.None, 0f);
            }
        }

        // Menu items (clean, no unnecessary descriptions)
        string[] items;
        if (_isMultiplayer)
        {
            items = new[]
            {
                "Resume",
                "Settings",
                "Exit Match",
            };
        }
        else
        {
#if ANDROID
            items = new[]
            {
                "Resume",
                "Restart Song",
                "Settings",
                "Exit to Menu",
            };
#else
            items = new[]
            {
                "Resume",
                "Restart Song",
                "HUD Editor",
                "Settings",
                "Exit to Menu",
            };
#endif
        }

        float itemH = TransformSize(64f);
        float itemW = TransformSize(340f);
        
        for (int i = 0; i < items.Length; i++)
        {
            string label = items[i];
            bool isSelected = (i == Selection);
            
            float itemY = menuY + i * itemH;
            if (i < _itemRects.Length)
                _itemRects[i] = new Rectangle((int)(menuX - TransformSize(30)), (int)(itemY - TransformSize(8)),
                    (int)(itemW + TransformSize(50)), (int)itemH);
            
            // Selection background with animation
            if (isSelected)
            {
                float pulse = 0.5f + 0.5f * MathF.Sin((float)gameTime.TotalGameTime.TotalSeconds * 4f);
                Color selBg = accentColor * (0.25f + pulse * 0.1f);
                DrawRect(spriteBatch, (int)(menuX - TransformSize(12)), (int)(itemY - TransformSize(6)), 
                    (int)(itemW + TransformSize(20)), (int)(itemH - TransformSize(10)), selBg);
                
                DrawRect(spriteBatch, (int)(menuX - TransformSize(12)), (int)(itemY - TransformSize(6)), 
                    Math.Max(3, (int)TransformSize(4f)), (int)(itemH - TransformSize(10)), accentColor);
            }
            
            // Item label - RESPONSIVE
            Color labelColor = isSelected ? Color.White : Color.White * 0.6f;
            int labelFontSize = (int)(displayH * 0.048f);
            labelFontSize = Math.Clamp(labelFontSize, 26, 66);
            if (isSelected) labelFontSize += 4;
            
            float textOffsetX = isSelected ? TransformSize(16f) : TransformSize(6f);
            
            if (_fontRenderer != null)
            {
                _fontRenderer.DrawString(spriteBatch, label,
                    new Vector2(menuX + textOffsetX, itemY),
                    labelColor, labelFontSize);
            }
            else
            {
                float labelScale = isSelected ? 1.25f : 1.05f;
                spriteBatch.DrawString(_font, label,
                    new Vector2(menuX + textOffsetX, itemY),
                    labelColor, 0f, Vector2.Zero, labelScale, SpriteEffects.None, 0f);
            }
            
            // Arrow indicator
            if (isSelected)
            {
                string arrow = ">";
                int arrowSize = (int)(displayH * 0.052f);
                arrowSize = Math.Clamp(arrowSize, 28, 68);
                
                if (_fontRenderer != null)
                {
                    _fontRenderer.DrawString(spriteBatch, arrow,
                        new Vector2(menuX - TransformSize(28), itemY + TransformSize(2)),
                        accentColor, arrowSize);
                }
                else
                {
                    spriteBatch.DrawString(_font, arrow,
                        new Vector2(menuX - TransformSize(28), itemY + TransformSize(2)),
                        accentColor, 0f, Vector2.Zero, 1.35f, SpriteEffects.None, 0f);
                }
            }
        }

        // Bottom instructions (aligned cleanly on the left)
        float bottomY = TransformY(sh - 42f);
        string instructions = UI.PointerInput.IsTouch
            ? "Tap an option   |   Back: resume"
            : "UP/DOWN: Navigate   |   ENTER / Click: Select   |   ESC: Resume";
        int instrFontSize = (int)(displayH * 0.025f);
        instrFontSize = Math.Clamp(instrFontSize, 14, 30);
        
        if (_fontRenderer != null)
        {
            _fontRenderer.DrawString(spriteBatch, instructions,
                new Vector2(titleX, bottomY),
                Color.White * 0.45f, instrFontSize);
        }
        else
        {
            spriteBatch.DrawString(_font, instructions,
                new Vector2(titleX, bottomY),
                Color.White * 0.45f, 0f, Vector2.Zero, 0.65f, SpriteEffects.None, 0f);
        }
    }
    
    private void DrawRect(SpriteBatch sb, int x, int y, int w, int h, Color c)
    {
        if (w <= 0 || h <= 0) return;
        sb.Draw(_pixel, new Rectangle(x, y, w, h), c);
    }
}
