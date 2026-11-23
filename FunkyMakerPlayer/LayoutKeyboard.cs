using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using Microsoft.Xna.Framework.Input;

namespace FunkyMakerPlayer;

/// <summary>
/// Keyboard state that also sees layout-specific keys (fork). MonoGame DesktopGL maps SDL key
/// codes (layout characters) to <see cref="Keys"/> and drops non-ASCII ones, so keys like Spanish
/// Ñ, Ç, ´ or º never appear in <see cref="Keyboard.GetState()"/>. This reads SDL's physical key
/// state and gives each such key a <see cref="Keys"/> value that no other key on the current
/// layout produces, plus a display name (the layout character). Use instead of Keyboard.GetState().
/// </summary>
public static class LayoutKeyboard
{
#if ANDROID
    public static KeyboardState GetState() => Keyboard.GetState();
    public static string? DisplayName(Keys key) => null;
#else
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate IntPtr GetKeyboardStateFn(out int numKeys);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int GetKeyFromScancodeFn(int scancode);

    private static bool _initDone;
    private static GetKeyboardStateFn? _getKeyboardState;
    private static GetKeyFromScancodeFn? _getKeyFromScancode;

    // scancode → injected key, and injected key → display name
    private static (int Scancode, Keys Key)[] _extra = Array.Empty<(int, Keys)>();
    private static readonly Dictionary<Keys, string> _names = new();

    // US-position Keys for SDL scancodes of punctuation keys (preferred when free)
    private static readonly Dictionary<int, Keys> Positional = new()
    {
        [45] = Keys.OemMinus, [46] = Keys.OemPlus, [47] = Keys.OemOpenBrackets, [48] = Keys.OemCloseBrackets,
        [49] = Keys.OemPipe, [50] = Keys.OemPipe, [51] = Keys.OemSemicolon, [52] = Keys.OemQuotes,
        [53] = Keys.OemTilde, [54] = Keys.OemComma, [55] = Keys.OemPeriod, [56] = Keys.OemQuestion,
        [100] = Keys.OemBackslash,
    };

    // Unused Keys values handed out when the positional one is already taken by the layout
    private static readonly Keys[] Spare =
    {
        Keys.Oem8, Keys.OemAuto, Keys.OemCopy, Keys.OemEnlW, Keys.OemClear, Keys.Pa1, Keys.Attn,
        Keys.Crsel, Keys.Exsel, Keys.EraseEof, Keys.Zoom, Keys.ProcessKey, Keys.ImeConvert, Keys.ImeNoConvert,
    };

    /// <summary>Re-reads the layout (call after a keyboard layout change; done once automatically).</summary>
    public static void RefreshLayout()
    {
        _initDone = true;
        _extra = Array.Empty<(int, Keys)>();
        _names.Clear();
        if (!LoadSdl()) return;

        Func<int, Keys>? toXna = null;
        try
        {
            var m = typeof(Keys).Assembly.GetType("Microsoft.Xna.Framework.Input.KeyboardUtil")
                ?.GetMethod("ToXna", BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic, null, new[] { typeof(int) }, null);
            if (m != null) toXna = code => (Keys)m.Invoke(null, new object[] { code })!;
        }
        catch { /* fall back to the non-ASCII rule below */ }

        try
        {
            // Which Keys does the current layout already produce, and which keys produce nothing?
            var used = new HashSet<Keys>();
            var unmapped = new List<(int Scancode, int Keycode)>();
            for (int sc = 4; sc < 232; sc++)
            {
                int code = _getKeyFromScancode!(sc);
                if (code == 0) continue;
                Keys k = toXna != null ? toXna(code) : (code < 128 ? Keys.A /* assume mapped */ : Keys.None);
                if (k != Keys.None) used.Add(k);
                else if (code >= 128 && code < (1 << 30)) unmapped.Add((sc, code));  // printable non-ASCII
            }

            var extra = new List<(int, Keys)>();
            int spare = 0;
            foreach (var (sc, code) in unmapped)
            {
                Keys key;
                if (Positional.TryGetValue(sc, out var pos) && !used.Contains(pos)) key = pos;
                else
                {
                    while (spare < Spare.Length && used.Contains(Spare[spare])) spare++;
                    if (spare >= Spare.Length) break;
                    key = Spare[spare++];
                }
                used.Add(key);
                extra.Add((sc, key));
                _names[key] = char.ConvertFromUtf32(code).ToUpperInvariant();
            }
            _extra = extra.ToArray();
            if (_extra.Length > 0)
                Console.WriteLine($"[LayoutKeyboard] Layout keys added: {string.Join(", ", _extra.Select(e => $"{_names[e.Item2]}={e.Item2}"))}");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[LayoutKeyboard] Layout scan failed: {ex.Message}");
            _extra = Array.Empty<(int, Keys)>();
        }
    }

    public static KeyboardState GetState()
    {
        var state = Keyboard.GetState();
        if (!_initDone) RefreshLayout();
        if (_extra.Length == 0 || _getKeyboardState == null) return state;

        List<Keys>? added = null;
        try
        {
            IntPtr arr = _getKeyboardState(out int count);
            if (arr == IntPtr.Zero) return state;
            foreach (var (sc, key) in _extra)
                if (sc < count && Marshal.ReadByte(arr, sc) != 0 && !state.IsKeyDown(key))
                    (added ??= new List<Keys>()).Add(key);
        }
        catch { return state; }

        if (added == null) return state;
        return new KeyboardState(state.GetPressedKeys().Concat(added).ToArray(), state.CapsLock, state.NumLock);
    }

    /// <summary>Layout character for a key added by this class (e.g. "Ñ"), else null.</summary>
    public static string? DisplayName(Keys key)
    {
        if (!_initDone) RefreshLayout();
        return _names.TryGetValue(key, out var n) ? n : null;
    }

    private static bool LoadSdl()
    {
        if (_getKeyboardState != null && _getKeyFromScancode != null) return true;
        foreach (var name in new[] { "SDL2", "SDL2.dll", "libSDL2-2.0.so.0", "libSDL2-2.0.0.dylib", "libSDL2.dylib" })
        {
            if (!NativeLibrary.TryLoad(name, typeof(Keys).Assembly, null, out var lib) &&
                !NativeLibrary.TryLoad(name, out lib))
                continue;
            if (NativeLibrary.TryGetExport(lib, "SDL_GetKeyboardState", out var a) &&
                NativeLibrary.TryGetExport(lib, "SDL_GetKeyFromScancode", out var b))
            {
                _getKeyboardState = Marshal.GetDelegateForFunctionPointer<GetKeyboardStateFn>(a);
                _getKeyFromScancode = Marshal.GetDelegateForFunctionPointer<GetKeyFromScancodeFn>(b);
                return true;
            }
        }
        Console.WriteLine("[LayoutKeyboard] SDL2 not found; layout-specific keys unavailable.");
        return false;
    }
#endif
}
