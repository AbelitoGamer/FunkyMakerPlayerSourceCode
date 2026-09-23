using Microsoft.Xna.Framework;
using MonoGame.Extended.Tweening;
using System;

namespace FunkyMakerPlayer.Managers;

/// <summary>
/// Manages all tweening animations using MonoGame.Extended.Tweening
/// Replaces manual lerp systems with professional easing functions
/// </summary>
public class TweenManager
{
    private readonly Tweener _tweener = new Tweener();
    
    /// <summary>
    /// Update all active tweens
    /// </summary>
    public void Update(GameTime gameTime)
    {
        _tweener.Update((float)gameTime.ElapsedGameTime.TotalSeconds);
    }
    
    /// <summary>
    /// Tween a float property with easing
    /// </summary>
    public Tween TweenFloat(
        object target,
        System.Linq.Expressions.Expression<Func<object, float>> property,
        float toValue,
        float duration,
        float delay = 0f,
        Func<float, float>? easing = null)
    {
        return _tweener.TweenTo(
            target: target,
            expression: property,
            toValue: toValue,
            duration: duration,
            delay: delay)
            .Easing(easing ?? EasingFunctions.QuadraticOut);
    }
    
    /// <summary>
    /// Tween a Vector2 property with easing
    /// </summary>
    public Tween TweenVector2(
        object target,
        System.Linq.Expressions.Expression<Func<object, Vector2>> property,
        Vector2 toValue,
        float duration,
        float delay = 0f,
        Func<float, float>? easing = null)
    {
        return _tweener.TweenTo(
            target: target,
            expression: property,
            toValue: toValue,
            duration: duration,
            delay: delay)
            .Easing(easing ?? EasingFunctions.QuadraticOut);
    }
    
    /// <summary>
    /// Tween a Color property with easing
    /// </summary>
    public Tween TweenColor(
        object target,
        System.Linq.Expressions.Expression<Func<object, Color>> property,
        Color toValue,
        float duration,
        float delay = 0f,
        Func<float, float>? easing = null)
    {
        return _tweener.TweenTo(
            target: target,
            expression: property,
            toValue: toValue,
            duration: duration,
            delay: delay)
            .Easing(easing ?? EasingFunctions.QuadraticOut);
    }
    
    /// <summary>
    /// Cancel all tweens for a specific target
    /// </summary>
    public void CancelTweens(object target)
    {
        _tweener.CancelAndCompleteAll();
    }
    
    /// <summary>
    /// Clear all active tweens
    /// </summary>
    public void Clear()
    {
        _tweener.CancelAndCompleteAll();
    }
}
