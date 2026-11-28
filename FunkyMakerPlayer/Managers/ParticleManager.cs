using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using System;
using System.Collections.Generic;

namespace FunkyMakerPlayer.Managers;

/// <summary>
/// Simple particle system for visual effects (combo digits, judgements, explosions)
/// Replaces manual falling digit/popup systems with a flexible particle engine
/// </summary>
public class ParticleManager
{
    private readonly List<Particle> _particles = new List<Particle>();
    private readonly Random _random = new Random();
    
    /// <summary>
    /// Single particle data
    /// </summary>
    public class Particle
    {
        public string Text = "";
        public Vector2 Position;
        public Vector2 Velocity;
        public Vector2 Acceleration;
        public float Scale = 1f;
        public float ScaleVelocity = 0f;
        public float Alpha = 1f;
        public float AlphaDecay = 1f;
        public Color Color = Color.White;
        public float Lifetime = 1f;
        public float Age = 0f;
        public bool IsActive => Age < Lifetime;
    }
    
    /// <summary>
    /// Update all particles
    /// </summary>
    public void Update(GameTime gameTime)
    {
        float dt = (float)gameTime.ElapsedGameTime.TotalSeconds;
        
        // Update particles
        for (int i = _particles.Count - 1; i >= 0; i--)
        {
            var p = _particles[i];
            
            p.Age += dt;
            if (!p.IsActive)
            {
                _particles.RemoveAt(i);
                continue;
            }
            
            // Physics
            p.Velocity += p.Acceleration * dt;
            p.Position += p.Velocity * dt;
            
            // Scale
            p.Scale += p.ScaleVelocity * dt;
            p.Scale = MathF.Max(0.1f, p.Scale);
            
            // Fade out
            p.Alpha -= p.AlphaDecay * dt;
            p.Alpha = MathHelper.Clamp(p.Alpha, 0f, 1f);
        }
    }
    
    /// <summary>
    /// Draw all particles
    /// </summary>
    public void Draw(SpriteBatch spriteBatch, SpriteFont font)
    {
        foreach (var p in _particles)
        {
            if (p.IsActive && !string.IsNullOrEmpty(p.Text))
            {
                spriteBatch.DrawUiString(
                    font,
                    p.Text,
                    p.Position,
                    p.Color * p.Alpha,
                    0f,
                    Vector2.Zero,
                    p.Scale,
                    SpriteEffects.None,
                    0f
                );
            }
        }
    }
    
    /// <summary>
    /// Spawn a falling combo digit particle (FNF style)
    /// </summary>
    public void SpawnComboDigit(char digit, Vector2 position, SpriteFont font)
    {
        Vector2 digitSize = font.MeasureUi(digit.ToString());
        
        _particles.Add(new Particle
        {
            Text = digit.ToString(),
            Position = position,
            Velocity = new Vector2(
                MathHelper.Lerp(-20f, 20f, (float)_random.NextDouble()),
                100f  // Fall down
            ),
            Acceleration = new Vector2(0f, 50f),  // Gravity
            Scale = 1.3f,
            ScaleVelocity = -0.5f,  // Shrink slightly
            Alpha = 1f,
            AlphaDecay = 1.25f,  // Fade over ~0.8s
            Color = Color.Yellow,
            Lifetime = 0.8f
        });
    }
    
    /// <summary>
    /// Spawn a judgement popup particle (Sick, Good, Bad, Shit)
    /// </summary>
    public void SpawnJudgement(string text, Vector2 position, Color color, float yOffset = 0f)
    {
        _particles.Add(new Particle
        {
            Text = text,
            Position = position + new Vector2(0f, yOffset),
            Velocity = new Vector2(0f, 80f),  // Fall down
            Acceleration = new Vector2(0f, 30f),  // Gravity
            Scale = 1.2f,
            ScaleVelocity = -0.3f,
            Alpha = 1f,
            AlphaDecay = 1.25f,  // Fade over ~0.8s
            Color = color,
            Lifetime = 0.8f
        });
    }
    
    /// <summary>
    /// Spawn a burst of particles (explosions, note hits, etc)
    /// </summary>
    public void SpawnBurst(
        Vector2 position,
        int count,
        string text,
        Color color,
        float speedMin = 50f,
        float speedMax = 150f)
    {
        for (int i = 0; i < count; i++)
        {
            float angle = MathHelper.TwoPi * i / count;
            float speed = MathHelper.Lerp(speedMin, speedMax, (float)_random.NextDouble());
            
            _particles.Add(new Particle
            {
                Text = text,
                Position = position,
                Velocity = new Vector2(
                    MathF.Cos(angle) * speed,
                    MathF.Sin(angle) * speed
                ),
                Acceleration = new Vector2(0f, 100f),  // Gravity
                Scale = 1f,
                ScaleVelocity = -0.5f,
                Alpha = 1f,
                AlphaDecay = 2f,  // Quick fade
                Color = color,
                Lifetime = 0.5f
            });
        }
    }
    
    /// <summary>
    /// Clear all particles
    /// </summary>
    public void Clear()
    {
        _particles.Clear();
    }
    
    /// <summary>
    /// Get count of active particles
    /// </summary>
    public int ActiveParticleCount => _particles.Count;
}
