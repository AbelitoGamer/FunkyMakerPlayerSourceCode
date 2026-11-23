using System;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading.Tasks;

namespace FunkyMakerPlayer;

/// <summary>
/// "Share a file" helpers used by the multiplayer level browser.
/// - Windows: opens Explorer with the file selected.
/// - Linux: asks the file manager to highlight it (FileManager1 over D-Bus), else opens its folder.
/// - macOS: reveals it in Finder.
/// - Desktop: also copies the full path to the clipboard (via SDL2, which MonoGame DesktopGL ships).
/// - Android: opens the system share sheet (Discord, Drive, WhatsApp…) with the file attached.
/// </summary>
public static class PlatformShare
{
    /// <summary>What <see cref="ShareFile"/> managed to do.</summary>
    public readonly record struct Result(bool Opened, bool PathCopied);

    public static Result ShareFile(string path)
    {
        if (!File.Exists(path)) return new Result(false, false);
#if ANDROID
        return new Result(Platforms.Android.LevelShareProvider.ShareFile(path), false);
#else
        bool copied = TryCopyToClipboard(path);
        bool opened = RevealInFileManager(path);
        return new Result(opened, copied);
#endif
    }

#if !ANDROID
    /// <summary>Shows a file or folder in the system file manager (fork: Freeplay "Show in folder").</summary>
    public static bool Reveal(string path)
        => (File.Exists(path) || Directory.Exists(path)) && RevealInFileManager(path);

    private static bool RevealInFileManager(string path)
    {
        try
        {
            if (OperatingSystem.IsWindows())
            {
                // explorer.exe wants the classic form: /select,"C:\path with spaces\file"
                var psi = new ProcessStartInfo("explorer.exe", $"/select,\"{path}\"") { UseShellExecute = false };
                Process.Start(psi)?.Dispose();
                return true;
            }
            if (OperatingSystem.IsMacOS())
            {
                var psi = new ProcessStartInfo("open") { UseShellExecute = false };
                psi.ArgumentList.Add("-R");
                psi.ArgumentList.Add(path);
                Process.Start(psi)?.Dispose();
                return true;
            }

            // Linux: try to highlight the file; fall back to opening its folder. Runs off the
            // game thread because dbus-send can take a moment (or hang if no file manager answers).
            string uri = new Uri(path).AbsoluteUri;
            string dir = Path.GetDirectoryName(path) ?? path;
            Task.Run(() =>
            {
                if (RunAndWait("dbus-send", 3000, "--session", "--print-reply", "--dest=org.freedesktop.FileManager1",
                        "--type=method_call", "/org/freedesktop/FileManager1",
                        "org.freedesktop.FileManager1.ShowItems", $"array:string:{uri}", "string:"))
                    return;
                RunAndWait("xdg-open", 0, dir);
            });
            return true;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[PlatformShare] Could not open the file manager: {ex.Message}");
            return false;
        }
    }

    /// <summary>Starts a tool; when <paramref name="timeoutMs"/> &gt; 0 waits for it and returns whether it exited with 0.</summary>
    private static bool RunAndWait(string tool, int timeoutMs, params string[] args)
    {
        try
        {
            var psi = new ProcessStartInfo(tool)
            {
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
            };
            foreach (var a in args) psi.ArgumentList.Add(a);
            using var proc = Process.Start(psi);
            if (proc == null) return false;
            if (timeoutMs <= 0) return true;
            if (!proc.WaitForExit(timeoutMs)) { try { proc.Kill(); } catch { } return false; }
            return proc.ExitCode == 0;
        }
        catch
        {
            return false; // tool not installed
        }
    }

    // ── Clipboard through SDL2 (already loaded by MonoGame DesktopGL) ─────────
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int SdlSetClipboardText(IntPtr utf8Text);

    private static SdlSetClipboardText? _sdlSetClipboard;
    private static bool _sdlLookedUp;

    /// <summary>Copies text to the system clipboard. Must be called from the game (main) thread.</summary>
    public static bool TryCopyToClipboard(string text)
    {
        try
        {
            if (!_sdlLookedUp)
            {
                _sdlLookedUp = true;
                var asm = typeof(Microsoft.Xna.Framework.Game).Assembly;
                foreach (var name in new[] { "SDL2", "libSDL2-2.0.so.0", "libSDL2-2.0.0.dylib", "libSDL2.so" })
                {
                    if (NativeLibrary.TryLoad(name, asm, null, out var handle) &&
                        NativeLibrary.TryGetExport(handle, "SDL_SetClipboardText", out var fn))
                    {
                        _sdlSetClipboard = Marshal.GetDelegateForFunctionPointer<SdlSetClipboardText>(fn);
                        break;
                    }
                }
                if (_sdlSetClipboard == null)
                    Console.WriteLine("[PlatformShare] SDL2 clipboard not available");
            }
            if (_sdlSetClipboard == null) return false;

            IntPtr ptr = Marshal.StringToCoTaskMemUTF8(text);
            try { return _sdlSetClipboard(ptr) == 0; }
            finally { Marshal.FreeCoTaskMem(ptr); }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[PlatformShare] Clipboard copy failed: {ex.Message}");
            return false;
        }
    }
#endif
}
