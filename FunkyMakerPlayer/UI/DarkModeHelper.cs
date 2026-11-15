using System;
using System.Runtime.InteropServices;

namespace FunkyMakerPlayer.UI;

// ── Dark Mode P/Invoke ──────────────────────────────────────────────────────
internal static class DarkMode
{
    [DllImport("dwmapi.dll", PreserveSig = true)]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int attrValue, int attrSize);

    [DllImport("SDL2", CallingConvention = CallingConvention.Cdecl)]
    private static extern IntPtr SDL_GetWindowWMInfo(IntPtr window, ref SDL_SysWMinfo info);

    [StructLayout(LayoutKind.Sequential)]
    private struct SDL_version
    {
        public byte major;
        public byte minor;
        public byte patch;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct SDL_SysWMinfo
    {
        public SDL_version version;
        public SDL_SYSWM_TYPE subsystem;
        public IntPtr win_window; // HWND on Windows
    }

    private enum SDL_SYSWM_TYPE
    {
        SDL_SYSWM_UNKNOWN = 0,
        SDL_SYSWM_WINDOWS = 1,
    }

    private const int DWMWA_USE_IMMERSIVE_DARK_MODE_BEFORE_20H1 = 19;
    private const int DWMWA_USE_IMMERSIVE_DARK_MODE = 20;

    /// <summary>
    /// Returns the native Win32 HWND for an SDL window handle on Windows, or IntPtr.Zero otherwise.
    /// </summary>
    public static IntPtr TryGetWindowsHandle(IntPtr sdlWindowHandle)
    {
        if (!OperatingSystem.IsWindows() || sdlWindowHandle == IntPtr.Zero)
            return IntPtr.Zero;

        try
        {
            var wmInfo = new SDL_SysWMinfo();
            wmInfo.version.major = 2;
            wmInfo.version.minor = 0;
            wmInfo.version.patch = 0;

            SDL_GetWindowWMInfo(sdlWindowHandle, ref wmInfo);

            if (wmInfo.subsystem == SDL_SYSWM_TYPE.SDL_SYSWM_WINDOWS)
                return wmInfo.win_window;
        }
        catch
        {
            // Ignore SDL WMInfo failures on non-Windows or headless platforms
        }

        return IntPtr.Zero;
    }

    /// <summary>
    /// Enables Windows 10/11 dark mode for the window title bar.
    /// </summary>
    public static void EnableDarkMode(IntPtr sdlWindowHandle)
    {
        if (!OperatingSystem.IsWindows())
            return;

        try
        {
            // Get native Windows HWND from SDL window
            IntPtr hwnd = TryGetWindowsHandle(sdlWindowHandle);

            if (hwnd != IntPtr.Zero)
            {
                int darkMode = 1;
                // Try Windows 11/10 20H1+ attribute first
                int result = DwmSetWindowAttribute(hwnd, DWMWA_USE_IMMERSIVE_DARK_MODE, ref darkMode, sizeof(int));
                // Fallback to older Windows 10 builds
                if (result != 0)
                    DwmSetWindowAttribute(hwnd, DWMWA_USE_IMMERSIVE_DARK_MODE_BEFORE_20H1, ref darkMode, sizeof(int));

                Console.WriteLine($"[DarkMode] Applied dark mode to window (result={result})");
            }
            else
            {
                Console.WriteLine("[DarkMode] Failed to get Windows HWND from SDL");
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[DarkMode] Error: {ex.Message}");
        }
    }
}
