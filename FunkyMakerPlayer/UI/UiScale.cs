using System;

namespace FunkyMakerPlayer.UI;

/// <summary>
/// Resolution-proportional UI scaling helpers (baseline 1280x720 = 1.0x).
/// <para>
/// <see cref="Touch"/> (fork, 2026-11-27): menus scale with the screen height, so on a phone they
/// take the same share of the screen as on a monitor but are physically tiny (a 46 px card on a
/// 2.8-inch-tall screen is about 4 mm). On touch devices every menu size is multiplied by this
/// factor, worked out from the screen's physical size (<see cref="InitTouchFromScreen"/>): about 1.5
/// on a phone, 1 on a tablet or a desktop. <see cref="FromScreen"/> includes it; screens with their
/// own formula multiply by it themselves.
/// </para>
/// </summary>
internal static class UiScale
{
    /// <summary>Extra menu scale on touch screens (1 = none; desktop is always 1).</summary>
    public static float Touch { get; private set; } = 1f;

    /// <summary>Desktop developer harness: pretending to be a touch device (Game1.DevShot.cs).</summary>
    public static bool SimulatedTouch { get; private set; }

    // A short side of this many inches gets no boost; smaller screens grow up to MaxTouch.
    private const float TouchReferenceInches = 4.2f;
    private const float MaxTouch = 1.6f;

    /// <summary>Android: sets <see cref="Touch"/> from the screen's short side in inches.</summary>
    public static void InitTouchFromScreen(float shortSideInches)
    {
        if (shortSideInches <= 0.5f || float.IsNaN(shortSideInches)) return;
        Touch = Math.Clamp(TouchReferenceInches / shortSideInches, 1f, MaxTouch);
        Console.WriteLine($"[UiScale] Screen short side {shortSideInches:0.00} in -> touch scale {Touch:0.00}");
    }

    public static void SimulateTouch(float boost)
    {
        SimulatedTouch = true;
        Touch = Math.Clamp(boost, 1f, 2f);
    }

    /// <summary>
    /// Menu scale for a screen: proportional to 1280x720, times <see cref="Touch"/>, but never more
    /// than what keeps an 853x480 layout on screen (so a boosted layout still fits).
    /// </summary>
    public static float FromScreen(int screenWidth, int screenHeight)
    {
        float baseScale = Math.Min(screenWidth / 1280f, screenHeight / 720f);
        float fit = Math.Min(screenWidth / 853f, screenHeight / 480f);
        return Math.Clamp(Math.Min(baseScale * Touch, fit), 0.70f, 2.50f);
    }

    /// <summary>Plain proportional scale (no touch boost), for layouts that must fill the screen exactly.</summary>
    public static float Proportional(int screenWidth, int screenHeight) =>
        Math.Clamp(Math.Min(screenWidth / 1280f, screenHeight / 720f), 0.70f, 2.50f);

    public static int Px(float baseValue, float scale, int min = 1) =>
        Math.Max(min, (int)MathF.Round(baseValue * scale));

    // Touch screens: small print (base 11-14) is unreadable on a phone, so it starts at this base
    private const float TouchMinFontBase = 15f;

    public static int FontPx(float baseSize, float scale, int min = 10, int max = 140) =>
        Math.Clamp((int)MathF.Round((Touch > 1f ? Math.Max(baseSize, TouchMinFontBase) : baseSize) * scale), min, max);
}
