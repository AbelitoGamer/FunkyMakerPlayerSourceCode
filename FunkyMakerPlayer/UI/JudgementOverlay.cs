using System;
using System.Collections.Generic;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;

namespace FunkyMakerPlayer.UI;

/// <summary>
/// Handles judgement popups and falling combo digits (FNF style).
/// Shows "Sick!", "Good", "Bad", "Shit" with fade-out and falling animations.
/// </summary>
public class JudgementOverlay
{
    private readonly SpriteBatch _spriteBatch;
    private readonly SpriteFont _font;
    
    // Judgement popups (stacked vertically)
    private class JudgementPopup
    {
        public string Text = "";
        public Color Color;
        public Vector2 Position;
        public Vector2 Velocity;
        public float Scale;
        public float Alpha;
        public float Timer;
        public float SizeMul = 1f;  // HUD editor scale (fork)

        public const float LIFETIME = 0.8f;  // Popups live for 0.8 seconds
    }
    private readonly List<JudgementPopup> _judgementPopups = new List<JudgementPopup>();
    private const int MAX_JUDGEMENTS = 3;  // Max 3 stacked judgements (FNF style)
    
    // Falling combo digits
    private class ComboDigit
    {
        public char Digit;
        public Vector2 Position;
        public Vector2 Velocity;
        public float Scale;
        public float Alpha;
        public float Timer;
        public float SizeMul = 1f;  // HUD editor scale (fork)

        public const float LIFETIME = 1.0f;  // Digits live for 1 second
    }
    private readonly List<ComboDigit> _comboDigits = new List<ComboDigit>();
    
    public JudgementOverlay(SpriteBatch spriteBatch, SpriteFont font)
    {
        _spriteBatch = spriteBatch;
        _font = font;
    }
    
    /// <summary>
    /// Adds a judgement popup (Sick!, Good, Bad, Shit).
    /// </summary>
    public void AddJudgement(string judgement, Point position, float sizeMul = 1f)
    {
        Color color = judgement switch
        {
            "Sick!" => new Color(0x00, 0xFF, 0xFF),  // Cyan
            "Good" => new Color(0x00, 0xFF, 0x00),   // Green
            "Bad" => new Color(0xFF, 0xAA, 0x00),    // Orange
            _ => new Color(0xFF, 0x44, 0x44)         // Red (Shit/Miss)
        };
        
        // Remove oldest if at max
        if (_judgementPopups.Count >= MAX_JUDGEMENTS)
            _judgementPopups.RemoveAt(0);
        
        // Stack vertically based on current count
        float offsetY = _judgementPopups.Count * 60f * sizeMul;

        _judgementPopups.Add(new JudgementPopup
        {
            Text = judgement,
            Color = color,
            Position = new Vector2(position.X, position.Y + offsetY),
            Velocity = Vector2.Zero,  // No movement, just fade
            Timer = 0f,
            Alpha = 1f,
            Scale = 1.2f * sizeMul,
            SizeMul = sizeMul
        });
    }
    
    /// <summary>
    /// Spawns falling combo digits (FNF style).
    /// </summary>
    public void ShowCombo(int combo, Point position, float sizeMul = 1f)
    {
        if (combo <= 0) return;  // Don't show combo = 0

        string comboStr = combo.ToString();

        // Calculate total width for horizontal spacing
        float totalWidth = 0f;
        foreach (char c in comboStr)
        {
            Vector2 size = _font.MeasureString(c.ToString());
            totalWidth += size.X * 0.9f * sizeMul;  // 90% spacing (tighter)
        }
        
        // Center the combo by offsetting startX
        float startX = position.X - totalWidth / 2f;
        float startY = position.Y;
        
        // Spawn each digit with slight offset
        float xOffset = 0f;
        foreach (char digit in comboStr)
        {
            Vector2 digitSize = _font.MeasureString(digit.ToString());
            
            _comboDigits.Add(new ComboDigit
            {
                Digit = digit,
                Position = new Vector2(startX + xOffset, startY),
                Velocity = new Vector2(0f, 120f * sizeMul),  // Fall downward
                Timer = 0f,
                Alpha = 1f,
                Scale = 1.3f * sizeMul,
                SizeMul = sizeMul
            });

            xOffset += digitSize.X * 0.9f * sizeMul;  // Tight spacing
        }
    }
    
    /// <summary>
    /// Updates animations for judgement popups and combo digits.
    /// </summary>
    public void Update(float deltaTime)
    {
        // Update falling combo digits
        for (int i = _comboDigits.Count - 1; i >= 0; i--)
        {
            var digit = _comboDigits[i];
            digit.Timer += deltaTime;
            
            if (digit.Timer >= ComboDigit.LIFETIME)
            {
                _comboDigits.RemoveAt(i);
                continue;
            }
            
            // Update position (fall down with velocity)
            digit.Position += digit.Velocity * deltaTime;
            
            // Fade out and scale down
            float t = digit.Timer / ComboDigit.LIFETIME;
            digit.Alpha = 1f - t;  // Fade out linearly
            digit.Scale = MathHelper.Lerp(1.3f, 0.8f, t) * digit.SizeMul;  // Shrink a bit
        }
        
        // Update falling judgement popups
        for (int i = _judgementPopups.Count - 1; i >= 0; i--)
        {
            var popup = _judgementPopups[i];
            popup.Timer += deltaTime;
            
            if (popup.Timer >= JudgementPopup.LIFETIME)
            {
                _judgementPopups.RemoveAt(i);
                continue;
            }
            
            // Update position (fall down)
            popup.Position += popup.Velocity * deltaTime;
            
            // Fade out and scale down
            float t = popup.Timer / JudgementPopup.LIFETIME;
            popup.Alpha = 1f - t;  // Fade out linearly
            popup.Scale = MathHelper.Lerp(1.2f, 0.9f, t) * popup.SizeMul;  // Shrink slightly
        }
    }
    
    /// <summary>
    /// Draws all active judgement popups and combo digits.
    /// </summary>
    public void Draw()
    {
        // Draw falling combo digits
        foreach (var digit in _comboDigits)
        {
            _spriteBatch.DrawString(
                _font,
                digit.Digit.ToString(),
                digit.Position,
                Color.Yellow * digit.Alpha,
                0f,
                Vector2.Zero,
                digit.Scale,
                SpriteEffects.None,
                0f
            );
        }
        
        // Draw falling judgement popups
        foreach (var popup in _judgementPopups)
        {
            _spriteBatch.DrawString(
                _font,
                popup.Text,
                popup.Position,
                popup.Color * popup.Alpha,
                0f,
                Vector2.Zero,
                popup.Scale,
                SpriteEffects.None,
                0f
            );
        }
    }
    
    /// <summary>
    /// Clears all active judgements and combo digits.
    /// </summary>
    public void Clear()
    {
        _judgementPopups.Clear();
        _comboDigits.Clear();
    }
}
