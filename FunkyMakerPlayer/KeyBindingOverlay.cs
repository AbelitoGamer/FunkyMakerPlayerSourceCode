using System;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using Microsoft.Xna.Framework.Input;
using System.Collections.Generic;

namespace FunkyMakerPlayer;

/// <summary>
/// Manages the key binding overlay UI for remapping player controls.
/// Displays 4 lanes (LEFT, DOWN, UP, RIGHT) and captures key presses.
/// </summary>
public class KeyBindingOverlay
{
    private bool _isActive = false;
    private int  _currentLane = 0;  // which lane we're waiting to bind (0-3)
    
    private readonly string[] _laneNames  = { "LEFT", "DOWN", "UP", "RIGHT" };
    private readonly Color[]  _laneColors = 
    { 
        new Color(0xC2, 0x4B, 0x99),  // LEFT - purple/pink
        new Color(0, 0xFF, 0xFF),      // DOWN - cyan
        new Color(0x12, 0xFA, 0x05),   // UP - green
        new Color(0xF9, 0x39, 0x3F)    // RIGHT - red
    };

    /// <summary>
    /// Whether the overlay is currently active and capturing input
    /// </summary>
    public bool IsActive
    {
        get => _isActive;
        set
        {
            _isActive = value;
            if (value) _currentLane = 0; // Reset to first lane when activated
        }
    }

    /// <summary>
    /// Current lane being bound (0-3)
    /// </summary>
    public int CurrentLane => _currentLane;

    /// <summary>
    /// Event fired when all keys have been bound successfully
    /// </summary>
    public event System.Action? OnComplete;

    /// <summary>
    /// Event fired when the user cancels the binding process
    /// </summary>
    public event System.Action? OnCancel;

    /// <summary>
    /// Updates the overlay state and handles input
    /// </summary>
    /// <param name="kb">Current keyboard state</param>
    /// <param name="prevKb">Previous keyboard state</param>
    public void Update(KeyboardState kb, KeyboardState prevKb)
    {
        if (!_isActive) return;

        // Capture key presses for the current lane
        var pressedKeys = kb.GetPressedKeys();
        foreach (var key in pressedKeys)
        {
            if (!prevKb.IsKeyDown(key) && 
                key != Keys.Enter && 
                key != Keys.Escape)
            {
                // Bind this key to the current lane
                NoteRenderer.PlayerKeys[_currentLane] = key;
                Settings.Save(true); // Save settings
                
                _currentLane++;
                if (_currentLane >= NoteRenderer.LANE_COUNT)
                {
                    // All lanes bound - complete
                    _isActive = false;
                    OnComplete?.Invoke();
                }
                break;
            }
        }

        // Handle cancel with Escape
        if (kb.IsKeyDown(Keys.Escape) && !prevKb.IsKeyDown(Keys.Escape))
        {
            _isActive = false;
            OnCancel?.Invoke();
        }
    }

    /// <summary>
    /// Draws the key binding overlay UI
    /// </summary>
    /// <param name="spriteBatch">SpriteBatch for drawing</param>
    /// <param name="font">Font to use for text</param>
    /// <param name="whitePixel">1x1 white texture for drawing rectangles</param>
    /// <param name="sw">Screen width</param>
    /// <param name="sh">Screen height</param>
    /// <param name="noteStyle">Optional note style for receptor sprites</param>
    public void Draw(SpriteBatch spriteBatch, SpriteFont font, Texture2D whitePixel, int sw, int sh, NoteStyle? noteStyle = null)
    {
        if (!_isActive) return;

        // Helper to draw rectangles
        void DrawRect(int x, int y, int w, int h, Color color)
        {
            spriteBatch.Draw(whitePixel, new Rectangle(x, y, w, h), color);
        }

        // Dark background overlay
        DrawRect(0, 0, sw, sh, Color.Black * 0.88f);

        // Header bar
        DrawRect(0, 0, sw, 54, new Color(15, 15, 28));
        DrawRect(0, 52, sw, 2, new Color(0xFF, 0x44, 0x88) * 0.7f);
        DrawRect(0, 0, 4, 54, new Color(0xFF, 0x44, 0x88));
        spriteBatch.DrawString(font, "KEY BINDING",
            new Vector2(20, 10), new Color(0xFF, 0x44, 0x88), 0f, Vector2.Zero, 1.3f, SpriteEffects.None, 0f);

        // Instruction text
        string instr = $"Press a key for  {_laneNames[_currentLane]}  ({_currentLane + 1} / 4)";
        var instrSz = font.MeasureString(instr) * 0.9f;
        spriteBatch.DrawString(font, instr,
            new Vector2(sw / 2f - instrSz.X / 2f, 72f),
            Color.White, 0f, Vector2.Zero, 0.9f, SpriteEffects.None, 0f);

        // 4 receptor slots layout
        float slotW   = 114f;
        float slotH   = 152f;
        float totalW  = slotW * 4f + 12f * 3f;
        float startX  = sw / 2f - totalW / 2f;
        float slotY   = sh / 2f - slotH / 2f - 10f;

        for (int lane = 0; lane < 4; lane++)
        {
            float sx  = startX + lane * (slotW + 12f);
            bool  sel = lane == _currentLane;
            bool  done = lane < _currentLane;

            // Slot background
            Color bgCol = sel
                ? _laneColors[lane] * 0.25f
                : (done ? new Color(20, 40, 20) * 0.8f : new Color(20, 20, 35) * 0.8f);
            DrawRect((int)sx, (int)slotY, (int)slotW, (int)slotH, bgCol);

            // Accent border on selected
            if (sel)
            {
                DrawRect((int)sx, (int)slotY, (int)slotW, 3, _laneColors[lane]);
                DrawRect((int)sx, (int)(slotY + slotH - 3), (int)slotW, 3, _laneColors[lane]);
                DrawRect((int)sx, (int)slotY, 3, (int)slotH, _laneColors[lane]);
                DrawRect((int)(sx + slotW - 3), (int)slotY, 3, (int)slotH, _laneColors[lane]);
            }
            else if (done)
            {
                DrawRect((int)sx, (int)slotY, (int)slotW, 2, new Color(0x44, 0xFF, 0x44) * 0.5f);
            }

            // Receptor sprite bounded strictly inside upper slot area so it never overlaps text
            float cx = sx + slotW / 2f;
            float iconBoxW = slotW - 22f;
            float iconBoxH = 74f;
            float ry = slotY + 12f + iconBoxH / 2f;
            if (noteStyle?.ReceptorFrames[lane, sel ? 2 : 0] is List<AtlasFrame> frames && frames.Count > 0)
            {
                var f = frames[0];
                float srcW = Math.Max(1f, f.Source.Width);
                float srcH = Math.Max(1f, f.Source.Height);
                float maxFitScale = Math.Min(iconBoxW / srcW, iconBoxH / srcH);
                float s = Math.Min(noteStyle.ReceptorScale * 0.55f, maxFitScale);
                spriteBatch.Draw(f.Texture, new Vector2(cx, ry), f.Source,
                    sel ? Color.White : Color.White * 0.5f, 0f,
                    new Vector2(srcW / 2f, srcH / 2f), s, SpriteEffects.None, 0f);
            }
            else
            {
                DrawRect((int)(cx - 22), (int)(ry - 22), 44, 44, _laneColors[lane] * (sel ? 0.9f : 0.4f));
            }

            // Lane name
            string lname = _laneNames[lane];
            var lsz = font.MeasureString(lname) * 0.68f;
            spriteBatch.DrawString(font, lname,
                new Vector2(cx - lsz.X / 2f, slotY + slotH - 54f),
                sel ? _laneColors[lane] : Color.White * 0.5f, 0f, Vector2.Zero, 0.68f, SpriteEffects.None, 0f);

            // Current key binding
            string keyStr = done || lane < _currentLane
                ? NoteRenderer.PlayerKeys[lane].ToString()
                : (sel ? "..." : NoteRenderer.PlayerKeys[lane].ToString());
            Color keyCol = done ? new Color(0x44, 0xFF, 0x44) : (sel ? Color.White : Color.White * 0.4f);
            var ksz = font.MeasureString(keyStr) * 0.75f;
            spriteBatch.DrawString(font, keyStr,
                new Vector2(cx - ksz.X / 2f, slotY + slotH - 28f),
                keyCol, 0f, Vector2.Zero, 0.75f, SpriteEffects.None, 0f);
        }

        // Footer hint
        string hint = "ESC  Cancel";
        var hintSz = font.MeasureString(hint) * 0.55f;
        spriteBatch.DrawString(font, hint,
            new Vector2(sw / 2f - hintSz.X / 2f, sh - 22f),
            Color.White * 0.2f, 0f, Vector2.Zero, 0.55f, SpriteEffects.None, 0f);
    }

    /// <summary>
    /// Resets the overlay to its initial state
    /// </summary>
    public void Reset()
    {
        _isActive = false;
        _currentLane = 0;
    }
}
