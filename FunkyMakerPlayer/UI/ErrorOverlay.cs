using System;
using System.Collections.Generic;
using System.Text;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;

namespace FunkyMakerPlayer.UI;

/// <summary>
/// Renders error and empty-state prompt overlays and provides ASCII text sanitization/wrapping helpers.
/// </summary>
public static class ErrorOverlay
{
    /// <summary>
    /// Draws the default drag-and-drop prompt when no level is loaded.
    /// </summary>
    public static void DrawDropHint(SpriteBatch spriteBatch, SpriteFont? font, int sw, int sh)
    {
        if (font == null) return;

        string[] lines = { "Funky Maker Player", "", "Drag a .FNMM, .ZIP or .RAR file here", "", "[ESC] Settings" };
        int lineH = (int)font.MeasureUi("A").Y + 6;
        int totalH = lines.Length * lineH;
        int y = sh / 2 - totalH / 2;

        foreach (var line in lines)
        {
            if (line.Length == 0) { y += lineH; continue; }
            var sz = font.MeasureUi(line);
            spriteBatch.DrawUiString(font, line,
                new Vector2(sw / 2f - sz.X / 2f, y),
                line == "Funky Maker Player" ? Color.Yellow : Color.White * 0.7f);
            y += lineH;
        }
    }

    /// <summary>
    /// Draws the level load error overlay with wrapped ASCII-safe text.
    /// </summary>
    public static void DrawError(SpriteBatch spriteBatch, SpriteFont? font, Texture2D? pixel, int sw, int sh, string msg)
    {
        if (font == null) return;

        bool isDisconnect = msg.StartsWith("Opponent Disconnected", StringComparison.OrdinalIgnoreCase);
        Color accent = isDisconnect ? new Color(0xFF, 0x99, 0x22) : new Color(0xFF, 0x22, 0x22);
        Color titleColor = isDisconnect ? new Color(0xFF, 0xBB, 0x33) : new Color(0xFF, 0x44, 0x44);
        string title = isDisconnect ? "OPPONENT DISCONNECTED" : "LEVEL LOAD ERROR";

        if (pixel != null)
        {
            spriteBatch.Draw(pixel, new Rectangle(0, 0, sw, sh), new Color(10, 5, 5) * 0.95f);
            spriteBatch.Draw(pixel, new Rectangle(0, 0, sw, 4), accent);
        }

        spriteBatch.DrawUiString(font, title,
            new Vector2(20, 14), titleColor, 0f, Vector2.Zero, 1.1f, SpriteEffects.None, 0f);

        int lineH = (int)(font.MeasureUi("A").Y * 0.8f) + 4;
        int y = 56;
        string normalized = msg.Replace("\r\n", "\n").Replace('\r', '\n');
        var rawLines = normalized.Split('\n');
        int startIdx = (isDisconnect && rawLines.Length > 1 && rawLines[0].Trim().Equals("Opponent Disconnected", StringComparison.OrdinalIgnoreCase)) ? 1 : 0;

        for (int i = startIdx; i < rawLines.Length; i++)
        {
            string rawLine = rawLines[i];
            if (string.IsNullOrWhiteSpace(rawLine))
            {
                y += Math.Max(8, lineH / 2);
                continue;
            }

            foreach (var line in WrapText(Safe(rawLine), 90))
            {
                spriteBatch.DrawUiString(font, line, new Vector2(20, y), Color.White * 0.85f, 0f, Vector2.Zero, 0.8f, SpriteEffects.None, 0f);
                y += lineH;
            }
        }

        string hint = "ENTER / ESC / CLICK  Back to menu";
        var hintSz = font.MeasureUi(hint) * 0.65f;
        spriteBatch.DrawUiString(font, hint,
            new Vector2(sw / 2f - hintSz.X / 2f, sh - 28f),
            Color.White * 0.35f, 0f, Vector2.Zero, 0.65f, SpriteEffects.None, 0f);
    }

    /// <summary>
    /// Replaces non-ASCII characters with '?' so SpriteFont rendering cannot throw on unsupported glyphs.
    /// </summary>
    public static string Safe(string? s)
    {
        if (s == null) return "";
        var sb = new StringBuilder(s.Length);
        foreach (char c in s)
            sb.Append((c >= 32 && c <= 126) || c == '\n' ? c : '?');
        return sb.ToString();
    }

    /// <summary>
    /// Word-wraps text to lines of at most <paramref name="maxLen"/> characters.
    /// </summary>
    public static IEnumerable<string> WrapText(string text, int maxLen)
    {
        while (text.Length > maxLen)
        {
            int cut = text.LastIndexOf(' ', maxLen);
            if (cut < 0) cut = maxLen;
            yield return text[..cut];
            text = text[cut..].TrimStart();
        }
        if (text.Length > 0) yield return text;
    }
}
