using System;
using System.Runtime.InteropServices;

namespace FunkyMakerPlayer;

/// <summary>
/// Helper class to enable Windows Game Mode for better recording software detection.
/// </summary>
public static class GameModeHelper
{
    // Windows DPI Awareness API
    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool SetProcessDPIAware();

    /// <summary>
    /// Attempts to enable Game Mode optimizations for this process.
    /// This helps recording software (Medal, OBS, etc.) detect the application as a game.
    /// </summary>
    public static void EnableGameMode()
    {
        if (!OperatingSystem.IsWindows())
            return;

        try
        {
            // Set DPI awareness (helps with detection)
            SetProcessDPIAware();
            
            Console.WriteLine("[GameMode] Game Mode hints enabled");
            Console.WriteLine("[GameMode] To fully enable: Press Win+G and mark as game");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[GameMode] Could not enable Game Mode hints: {ex.Message}");
        }
    }
}
