using System;

namespace FunkyMakerPlayer.UI;

/// <summary>
/// Resolution-proportional UI scaling helpers (baseline 1280x720 = 1.0x).
/// </summary>
internal static class UiScale
{
    public static float FromScreen(int screenWidth, int screenHeight) =>
        Math.Clamp(Math.Min(screenWidth / 1280f, screenHeight / 720f), 0.70f, 2.50f);

    public static int Px(float baseValue, float scale, int min = 1) =>
        Math.Max(min, (int)MathF.Round(baseValue * scale));

    public static int FontPx(float baseSize, float scale, int min = 10, int max = 140) =>
        Math.Clamp((int)MathF.Round(baseSize * scale), min, max);
}
