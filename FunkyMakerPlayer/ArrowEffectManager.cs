using System;
using Microsoft.Xna.Framework;

namespace FunkyMakerPlayer;

/// <summary>
/// Manages random visual effects for receptor arrows synchronized with music BPM.
/// Effects are purely cosmetic and don't affect gameplay.
/// </summary>
public class ArrowEffectManager
{
    public enum EffectType
    {
        None,
        WhiteGlow,      // Brillo blanco
        BounceVertical, // Rebote vertical
        Spin360,        // Giro 360° completo
        BounceHorizontal, // Rebote horizontal
        Dizzy,          // Efecto mareado/ondulante
    }

    // ── Global effect state (applies to all 4 arrows) ──────────────────────
    private struct GlobalEffect
    {
        public EffectType Type;
        public float Timer;           // Time since effect started
        public float Duration;        // How long the effect lasts
        public float BeatPhase;       // 0-1, where in the current beat we are
    }

    private GlobalEffect _currentEffect;
    
    // ── Settings ───────────────────────────────────────────────────────────
    public static bool   Enabled         = false;  // Master toggle (disabled by default)
    public static float  EffectIntensity = 1.0f;   // 0-1, multiplier for effect strength
    public static float  EffectChance    = 0.25f;  // Chance per beat to trigger effect (25%)
    public static int    SelectedEffect  = 0;      // 0=Random, 1=WhiteGlow, 2=BounceVertical, etc.
    
    private float _bpm;
    private float _beatDuration;
    private float _lastBeatTime = -999f;
    private Random _rng = new Random();

    public ArrowEffectManager(float bpm)
    {
        _bpm = bpm;
        _beatDuration = 60f / Math.Max(1f, bpm);
        
        // Initialize with no effect
        _currentEffect = new GlobalEffect
        {
            Type = EffectType.None,
            Timer = 0f,
            Duration = 0f,
            BeatPhase = 0f
        };
    }

    public void SetBPM(float bpm)
    {
        _bpm = bpm;
        _beatDuration = 60f / Math.Max(1f, bpm);
    }

    public void Update(float dt, float songBeats)
    {
        if (!Enabled) return;

        float currentBeat = MathF.Floor(songBeats);
        
        // Check if we crossed a beat boundary (sync with music beats)
        if (currentBeat > _lastBeatTime + 0.5f)
        {
            _lastBeatTime = currentBeat;
            TriggerRandomEffect();
        }

        // Update current global effect
        if (_currentEffect.Type != EffectType.None)
        {
            _currentEffect.Timer += dt;
            _currentEffect.BeatPhase = (songBeats % 1.0f);  // Fraction of current beat
            
            // Effect finished?
            if (_currentEffect.Timer >= _currentEffect.Duration)
            {
                _currentEffect.Type = EffectType.None;
                _currentEffect.Timer = 0f;
            }
        }
    }

    private void TriggerRandomEffect()
    {
        // Don't override existing effect
        if (_currentEffect.Type != EffectType.None) return;
        
        // Randomly decide if we trigger an effect this beat
        if (_rng.NextDouble() > EffectChance) return;

        // Pick effect based on SelectedEffect setting
        EffectType randomEffect;
        
        if (SelectedEffect == 0)
        {
            // Random mode: pick any effect
            var effectTypes = Enum.GetValues<EffectType>();
            randomEffect = (EffectType)_rng.Next(1, effectTypes.Length);
        }
        else
        {
            // Specific effect selected (1-5 maps to WhiteGlow-Dizzy)
            randomEffect = (EffectType)SelectedEffect;
        }
        
        // Duration varies by effect type
        float duration = randomEffect switch
        {
            EffectType.WhiteGlow => _beatDuration * 0.5f,      // Half beat flash
            EffectType.BounceVertical => _beatDuration * 2f,   // 2 beats
            EffectType.Spin360 => _beatDuration * 1f,          // 1 beat
            EffectType.BounceHorizontal => _beatDuration * 2f, // 2 beats
            EffectType.Dizzy => _beatDuration * 4f,            // 4 beats
            _ => _beatDuration
        };
        
        _currentEffect = new GlobalEffect
        {
            Type = randomEffect,
            Timer = 0f,
            Duration = duration,
            BeatPhase = 0f
        };
    }

    /// <summary>
    /// Gets the name of the currently selected effect for display in settings.
    /// </summary>
    public static string GetSelectedEffectName()
    {
        return SelectedEffect switch
        {
            0 => "Random",
            1 => "White Glow",
            2 => "Bounce Vertical",
            3 => "Spin 360°",
            4 => "Bounce Horizontal",
            5 => "Dizzy",
            _ => "Random"
        };
    }

    /// <summary>
    /// Gets the visual transformation to apply to a receptor arrow.
    /// Returns (offsetX, offsetY, rotation, scale, glowAmount)
    /// Same effect applies to all 4 arrows simultaneously.
    /// </summary>
    public (float offsetX, float offsetY, float rotation, float scale, float glow) 
        GetArrowTransform(int lane, float baseX, float baseY)
    {
        if (!Enabled || lane < 0 || lane >= 4) 
            return (0f, 0f, 0f, 1f, 0f);

        if (_currentEffect.Type == EffectType.None)
            return (0f, 0f, 0f, 1f, 0f);

        float progress = _currentEffect.Timer / _currentEffect.Duration;  // 0-1
        float intensity = EffectIntensity;
        float beatPhase = _currentEffect.BeatPhase;
        
        // Add slight lane offset for visual variety (each arrow slightly different phase)
        float laneOffset = lane * MathF.PI * 0.5f;  // 0°, 90°, 180°, 270°

        return _currentEffect.Type switch
        {
            EffectType.WhiteGlow => ApplyWhiteGlow(progress, intensity),
            EffectType.BounceVertical => ApplyBounceVertical(progress, beatPhase, intensity, laneOffset),
            EffectType.Spin360 => ApplySpin360(progress, intensity),
            EffectType.BounceHorizontal => ApplyBounceHorizontal(progress, beatPhase, intensity, laneOffset),
            EffectType.Dizzy => ApplyDizzy(progress, beatPhase, intensity, laneOffset),
            _ => (0f, 0f, 0f, 1f, 0f)
        };
    }

    private (float, float, float, float, float) ApplyWhiteGlow(float progress, float intensity)
    {
        // Flash bright then fade out smoothly with ease-out
        float easeOut = 1f - MathF.Pow(progress, 2f);  // Quadratic ease-out
        float glow = easeOut * intensity;
        float scale = 1f + glow * 0.1f;  // Slight scale up
        return (0f, 0f, 0f, scale, glow);
    }

    private (float, float, float, float, float) ApplyBounceVertical(float progress, float beatPhase, float intensity, float offset)
    {
        // Bounce up and down with the beat, with smooth ease at end
        float easeMultiplier = progress < 0.9f ? 1f : (1f - (progress - 0.9f) / 0.1f);  // Fade last 10%
        float bounceY = MathF.Sin(beatPhase * MathF.PI * 2f + offset) * 15f * intensity * easeMultiplier;
        return (0f, bounceY, 0f, 1f, 0f);
    }

    private (float, float, float, float, float) ApplySpin360(float progress, float intensity)
    {
        // Smooth 360° rotation with ease-in-out
        float eased = progress < 0.5f 
            ? 2f * progress * progress  // Ease-in first half
            : 1f - MathF.Pow(-2f * progress + 2f, 2f) / 2f;  // Ease-out second half
        float rotation = eased * MathF.PI * 2f * intensity;
        return (0f, 0f, rotation, 1f, 0f);
    }

    private (float, float, float, float, float) ApplyBounceHorizontal(float progress, float beatPhase, float intensity, float offset)
    {
        // Bounce left and right with the beat, with smooth ease at end
        float easeMultiplier = progress < 0.9f ? 1f : (1f - (progress - 0.9f) / 0.1f);  // Fade last 10%
        float bounceX = MathF.Sin(beatPhase * MathF.PI * 2f + offset) * 10f * intensity * easeMultiplier;
        return (bounceX, 0f, 0f, 1f, 0f);
    }

    private (float, float, float, float, float) ApplyDizzy(float progress, float beatPhase, float intensity, float offset)
    {
        // Circular wobble motion (lissajous-style) with smooth fade out
        float easeMultiplier = progress < 0.85f ? 1f : (1f - (progress - 0.85f) / 0.15f);  // Fade last 15%
        float wobbleX = MathF.Sin(beatPhase * MathF.PI * 4f + offset) * 8f * intensity * easeMultiplier;
        float wobbleY = MathF.Cos(beatPhase * MathF.PI * 3f + offset) * 8f * intensity * easeMultiplier;
        float wobbleRot = MathF.Sin(beatPhase * MathF.PI * 2f) * 0.1f * intensity * easeMultiplier;
        return (wobbleX, wobbleY, wobbleRot, 1f, 0f);
    }

    /// <summary>
    /// Gets a pulsing scale multiplier for hold notes synchronized with BPM.
    /// Returns a value that oscillates between 0.98 and 1.02.
    /// </summary>
    public float GetHoldNotePulse(float songBeats)
    {
        if (!Enabled) return 1f;
        
        float beatPhase = songBeats % 1.0f;
        float pulse = MathF.Sin(beatPhase * MathF.PI * 2f);
        return 1f + pulse * 0.02f * EffectIntensity;  // ±2% size variation
    }
}
