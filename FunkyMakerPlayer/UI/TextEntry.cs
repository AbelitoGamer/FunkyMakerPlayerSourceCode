using System;
using System.Collections.Concurrent;
using System.Text;
using System.Threading.Tasks;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Input;

namespace FunkyMakerPlayer.UI
{
    /// <summary>
    /// Free text input for menus (fork): chat, room names, passwords, search.
    /// Desktop: characters from <see cref="GameWindow.TextInput"/> (any keyboard layout, accents, symbols)
    /// are queued and read with <see cref="Drain"/>. Android: <see cref="PromptAsync"/> opens the system
    /// text dialog (MonoGame <see cref="KeyboardInput"/>).
    /// </summary>
    public static class TextEntry
    {
        private static readonly ConcurrentQueue<char> Chars = new();
        private static bool _attached;

        /// <summary>True while some menu reads typed text (others should ignore letter hotkeys).</summary>
        public static bool Capturing { get; set; }

        public static void Attach(GameWindow window)
        {
            if (_attached) return;
            _attached = true;
#if !ANDROID
            window.TextInput += (_, e) =>
            {
                if (Capturing && !char.IsControl(e.Character))
                    Chars.Enqueue(e.Character);
            };
#endif
        }

        /// <summary>Characters typed since the last call (only while <see cref="Capturing"/>).</summary>
        public static string Drain()
        {
            if (Chars.IsEmpty) return "";
            var sb = new StringBuilder();
            while (Chars.TryDequeue(out char c)) sb.Append(c);
            return sb.ToString();
        }

        public static void Clear()
        {
            while (Chars.TryDequeue(out _)) { }
        }

        /// <summary>Android: native text dialog. Null when cancelled.</summary>
        public static async Task<string?> PromptAsync(string title, string description, string current, bool password)
        {
            try
            {
                return await KeyboardInput.Show(title, description, current, password);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[TextEntry] Keyboard dialog failed: {ex.Message}");
                return null;
            }
        }
    }
}
