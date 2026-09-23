using System;
using System.Collections.Generic;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using MonoGame.Extended;

namespace FunkyMakerPlayer;

/// <summary>
/// Procedural notesplash templates using MonoGame.Extended drawing primitives.
/// Multiple visual styles: Circles, Splash Drops, Star Burst, Explosion, Ring Pulse.
/// </summary>
public class ParticleNoteSplash
{
    public enum SplashStyle
    {
        CircleExpand,    // Expanding circles
        SplashDrops,     // Water drops splashing outward
        StarBurst,       // Star/rays bursting out
        Explosion,       // Polygonal explosion
        RingPulse,       // Pulsing rings
        Randomizer       // Random style on each hit
    }
    
    private struct SplashEffect
    {
        public Vector2 Position;
        public Color Color;
        public float Timer;
        public float Lifetime;
        public SplashStyle Style;
        public int Lane;
    }
    
    private List<SplashEffect> _activeSplashes = new();
    private Random _random = new Random();
    
    // El usuario selecciona UN estilo y se aplica a TODOS los lanes
    public SplashStyle CurrentStyle { get; set; } = SplashStyle.CircleExpand;
    
    // Colors for each lane (FNF style)
    private static readonly Color[] LaneColors = new[]
    {
        new Color(194, 75, 153),   // Lane 0: Purple
        new Color(0, 255, 255),    // Lane 1: Cyan
        new Color(18, 250, 5),     // Lane 2: Green
        new Color(249, 57, 63)     // Lane 3: Red
    };
    
    public ParticleNoteSplash(GraphicsDevice graphicsDevice)
    {
        // Pure procedural - no textures needed
    }
    
    public void Trigger(int lane, Vector2 position)
    {
        if (lane < 0 || lane >= 4) return;
        
        // Determinar el estilo a usar
        SplashStyle styleToUse = CurrentStyle;
        
        // Si es Randomizer, elegir uno aleatorio (excluyendo Randomizer mismo)
        if (CurrentStyle == SplashStyle.Randomizer)
        {
            // Estilos disponibles: 0-4 (CircleExpand, SplashDrops, StarBurst, Explosion, RingPulse)
            styleToUse = (SplashStyle)_random.Next(0, 5);
        }
        
        // TODOS los lanes usan el mismo estilo (CurrentStyle o el random)
        _activeSplashes.Add(new SplashEffect
        {
            Position = position,
            Color = LaneColors[lane],
            Timer = 0f,
            Lifetime = 0.5f,  // Aumentado para que sea más visible
            Style = styleToUse,  // El usuario eligió este estilo (o random)
            Lane = lane
        });
    }
    
    public void Update(GameTime gameTime)
    {
        float dt = (float)gameTime.ElapsedGameTime.TotalSeconds;
        
        for (int i = _activeSplashes.Count - 1; i >= 0; i--)
        {
            var splash = _activeSplashes[i];
            splash.Timer += dt;
            
            if (splash.Timer >= splash.Lifetime)
            {
                _activeSplashes.RemoveAt(i);
            }
            else
            {
                _activeSplashes[i] = splash;
            }
        }
    }
    
    public void Draw(SpriteBatch spriteBatch)
    {
        foreach (var splash in _activeSplashes)
        {
            switch (splash.Style)
            {
                case SplashStyle.CircleExpand:
                    DrawCircleExpand(spriteBatch, splash);
                    break;
                case SplashStyle.SplashDrops:
                    DrawSplashDrops(spriteBatch, splash);
                    break;
                case SplashStyle.StarBurst:
                    DrawStarBurst(spriteBatch, splash);
                    break;
                case SplashStyle.Explosion:
                    DrawExplosion(spriteBatch, splash);
                    break;
                case SplashStyle.RingPulse:
                    DrawRingPulse(spriteBatch, splash);
                    break;
            }
        }
    }
    
    // ═══════════════════════════════════════════════════════════════════════
    // PLANTILLA 1: Círculos Expandiéndose
    // ═══════════════════════════════════════════════════════════════════════
    private void DrawCircleExpand(SpriteBatch sb, SplashEffect splash)
    {
        float t = splash.Timer / splash.Lifetime;
        float alpha = 1f - t;
        
        // 3 círculos expandiéndose a diferente velocidad
        for (int i = 0; i < 3; i++)
        {
            float delay = i * 0.05f;
            float localT = MathHelper.Clamp((splash.Timer - delay) / (splash.Lifetime - delay), 0f, 1f);
            
            if (localT > 0f)
            {
                float radius = MathHelper.Lerp(10f, 60f, localT);
                float thickness = MathHelper.Lerp(8f, 2f, localT);
                float localAlpha = (1f - localT) * alpha;
                
                sb.DrawCircle(splash.Position, radius, 24, splash.Color * localAlpha, thickness);
            }
        }
        
        // Centro brillante
        if (t < 0.5f)
        {
            float centerAlpha = (1f - t * 2f) * alpha;
            sb.FillRectangle(splash.Position.X - 6f, splash.Position.Y - 6f, 12f, 12f, splash.Color * centerAlpha);
        }
    }
    
    // ═══════════════════════════════════════════════════════════════════════
    // PLANTILLA 2: Gotas Salpicando (Water Splash)
    // ═══════════════════════════════════════════════════════════════════════
    private void DrawSplashDrops(SpriteBatch sb, SplashEffect splash)
    {
        float t = splash.Timer / splash.Lifetime;
        float alpha = 1f - t;
        
        // 8 gotas volando hacia afuera
        for (int i = 0; i < 8; i++)
        {
            float angle = (i / 8f) * MathHelper.TwoPi + splash.Timer * 2f;
            float dist = MathHelper.Lerp(0f, 50f, t);
            
            Vector2 dropPos = splash.Position + new Vector2(
                MathF.Cos(angle) * dist,
                MathF.Sin(angle) * dist
            );
            
            // Gota como punto
            float dropSize = MathHelper.Lerp(8f, 4f, t);
            sb.FillRectangle(dropPos.X - dropSize/2f, dropPos.Y - dropSize/2f, dropSize, dropSize, splash.Color * alpha * 0.8f);
            
            // Estela/trail detrás de la gota
            Vector2 trailStart = Vector2.Lerp(splash.Position, dropPos, 0.6f);
            sb.DrawLine(trailStart, dropPos, splash.Color * alpha * 0.5f, dropSize * 0.5f);
        }
        
        // Anillo de impacto central
        float ringRadius = MathHelper.Lerp(5f, 35f, t);
        sb.DrawCircle(splash.Position, ringRadius, 20, splash.Color * alpha * 0.6f, 3f);
    }
    
    // ═══════════════════════════════════════════════════════════════════════
    // PLANTILLA 3: Explosión de Rayos/Estrella
    // ═══════════════════════════════════════════════════════════════════════
    private void DrawStarBurst(SpriteBatch sb, SplashEffect splash)
    {
        float t = splash.Timer / splash.Lifetime;
        float alpha = 1f - t;
        float rotation = splash.Timer * 4f;
        
        // 8 rayos principales
        for (int i = 0; i < 8; i++)
        {
            float angle = rotation + (i * MathHelper.TwoPi / 8f);
            float rayLength = MathHelper.Lerp(10f, 70f, t);
            float innerDist = rayLength * 0.2f;
            
            Vector2 innerPos = splash.Position + new Vector2(
                MathF.Cos(angle) * innerDist,
                MathF.Sin(angle) * innerDist
            );
            
            Vector2 outerPos = splash.Position + new Vector2(
                MathF.Cos(angle) * rayLength,
                MathF.Sin(angle) * rayLength
            );
            
            // Rayo grueso que adelgaza
            float thickness = MathHelper.Lerp(12f, 3f, t);
            sb.DrawLine(innerPos, outerPos, splash.Color * alpha, thickness);
            
            // Punta brillante
            float tipSize = thickness;
            sb.FillRectangle(outerPos.X - tipSize/2f, outerPos.Y - tipSize/2f, tipSize, tipSize, splash.Color * alpha * 1.2f);
        }
        
        // Centro brillante
        float centerSize = MathHelper.Lerp(15f, 8f, t);
        sb.FillRectangle(splash.Position.X - centerSize/2f, splash.Position.Y - centerSize/2f, centerSize, centerSize, Color.White * alpha);
        sb.DrawCircle(splash.Position, centerSize + 3f, 16, splash.Color * alpha, 2f);
    }
    
    // ═══════════════════════════════════════════════════════════════════════
    // PLANTILLA 4: Explosión Poligonal
    // ═══════════════════════════════════════════════════════════════════════
    private void DrawExplosion(SpriteBatch sb, SplashEffect splash)
    {
        float t = splash.Timer / splash.Lifetime;
        float alpha = 1f - t;
        
        // Fragmentos poligonales volando
        int fragmentCount = 12;
        for (int i = 0; i < fragmentCount; i++)
        {
            float angle = (i / (float)fragmentCount) * MathHelper.TwoPi;
            float dist = MathHelper.Lerp(5f, 60f, t) * (0.8f + (float)_random.NextDouble() * 0.4f);
            float rotation = angle + splash.Timer * 5f;
            
            Vector2 fragPos = splash.Position + new Vector2(
                MathF.Cos(angle) * dist,
                MathF.Sin(angle) * dist
            );
            
            // Dibuja fragmento como rombo/polígono
            DrawPolygonFragment(sb, fragPos, rotation, 
                MathHelper.Lerp(10f, 6f, t), splash.Color * alpha);
        }
        
        // Onda de choque
        float shockRadius = MathHelper.Lerp(10f, 80f, t);
        sb.DrawCircle(splash.Position, shockRadius, 32, splash.Color * alpha * 0.4f, 2f);
        
        // Centro
        if (t < 0.4f)
        {
            float localT = t / 0.4f;
            float centerSize = 18f * (1f - localT);
            sb.FillRectangle(splash.Position.X - centerSize/2f, splash.Position.Y - centerSize/2f, centerSize, centerSize, Color.White * (1f - localT));
        }
    }
    
    private void DrawPolygonFragment(SpriteBatch sb, Vector2 pos, float rotation, float size, Color color)
    {
        // Rombo simple (4 puntos)
        Vector2[] points = new Vector2[4];
        for (int i = 0; i < 4; i++)
        {
            float angle = rotation + (i * MathHelper.PiOver2);
            points[i] = pos + new Vector2(
                MathF.Cos(angle) * size,
                MathF.Sin(angle) * size
            );
        }
        
        // Dibuja las 4 líneas del rombo
        for (int i = 0; i < 4; i++)
        {
            sb.DrawLine(points[i], points[(i + 1) % 4], color, 2f);
        }
    }
    
    // ═══════════════════════════════════════════════════════════════════════
    // PLANTILLA 5: Anillos Pulsantes
    // ═══════════════════════════════════════════════════════════════════════
    private void DrawRingPulse(SpriteBatch sb, SplashEffect splash)
    {
        float t = splash.Timer / splash.Lifetime;
        float alpha = 1f - t;
        
        // 4 anillos que pulsan hacia afuera
        for (int i = 0; i < 4; i++)
        {
            float delay = i * 0.08f;
            float localT = MathHelper.Clamp((splash.Timer - delay) / (splash.Lifetime - delay), 0f, 1f);
            
            if (localT > 0f)
            {
                // Pulso: crece rápido, luego se estabiliza
                float pulseT = localT < 0.3f ? localT / 0.3f : 1f;
                float radius = MathHelper.Lerp(15f, 65f, pulseT);
                
                // Grosor pulsa también
                float thickness = MathHelper.Lerp(6f, 2f, localT) * (1f + MathF.Sin(localT * 20f) * 0.3f);
                float localAlpha = (1f - localT) * alpha;
                
                // Anillo con brillo interior/exterior
                sb.DrawCircle(splash.Position, radius, 28, splash.Color * localAlpha, thickness);
                
                // Brillo interior más suave
                if (localT < 0.6f)
                {
                    sb.DrawCircle(splash.Position, radius - thickness - 2f, 24, 
                        splash.Color * localAlpha * 0.5f, 1f);
                }
            }
        }
        
        // Partículas orbitando
        int particleCount = 8;
        for (int i = 0; i < particleCount; i++)
        {
            float angle = (i / (float)particleCount) * MathHelper.TwoPi + splash.Timer * 3f;
            float orbitRadius = 40f;
            Vector2 particlePos = splash.Position + new Vector2(
                MathF.Cos(angle) * orbitRadius,
                MathF.Sin(angle) * orbitRadius
            );
            
            float particleSize = 4f * (1f - t);
            sb.FillRectangle(particlePos.X - particleSize/2f, particlePos.Y - particleSize/2f, particleSize, particleSize, splash.Color * alpha);
        }
    }
}
