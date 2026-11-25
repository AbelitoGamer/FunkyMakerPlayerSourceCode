using System;
using System.Collections.Generic;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using Microsoft.Xna.Framework.Input;

namespace FunkyMakerPlayer.UI;

/// <summary>
/// Arrows/NoteStyle selector menu with preview.
/// </summary>
public class ArrowsMenu : IScreenOverlay
{
    private readonly Texture2D _pixel;
    private readonly SpriteFont _font;
    private readonly FontRenderer? _fontRenderer;
    
    public bool IsActive { get; set; } = false;
    public int Selection { get; set; } = 0;
    public bool SelectionConfirmed { get; private set; } = false;
    private readonly PickerTouch _touch = new(); // taps / clicks (fork)
    
    private List<string> _styles = new();
    private NoteStyle? _previewStyle = null;
    
    public ArrowsMenu(Texture2D pixel, SpriteFont font, FontRenderer? fontRenderer = null)
    {
        _pixel = pixel;
        _font = font;
        _fontRenderer = fontRenderer;
    }
    
    public void Open(GraphicsDevice graphicsDevice)
    {
        _styles = NoteStyle.GetAvailableStyles();
        
        // Select currently active style
        Selection = _styles.IndexOf(Game1.NoteStyleName);
        if (Selection < 0) Selection = 0;
        
        LoadPreview(_styles[Selection], graphicsDevice);
        SelectionConfirmed = false;
        _touch.Sync();
        IsActive = true;
    }
    
    public void Close()
    {
        IsActive = false;
        _previewStyle = null;
    }
    
    private void LoadPreview(string styleName, GraphicsDevice graphicsDevice)
    {
        _previewStyle = NoteStyle.LoadByName(styleName, graphicsDevice);
    }
    
    public void Update(KeyboardState kb, KeyboardState prevKb, GraphicsDevice graphicsDevice)
    {
        if (!IsActive) return;
        

        // Taps / clicks (fork): a row previews it, the previewed row again or APPLY applies, BACK cancels.
        var tap = _touch.Poll(Selection);
        if (tap.Select is int pick && pick >= 0 && pick < _styles.Count && pick != Selection)
        {
            Selection = pick;
            LoadPreview(_styles[Selection], graphicsDevice);
        }
        // Navigation
        if (kb.IsKeyDown(Keys.Up) && !prevKb.IsKeyDown(Keys.Up))
        {
            Selection = (Selection - 1 + _styles.Count) % _styles.Count;
            LoadPreview(_styles[Selection], graphicsDevice);
        }
        
        if (kb.IsKeyDown(Keys.Down) && !prevKb.IsKeyDown(Keys.Down))
        {
            Selection = (Selection + 1) % _styles.Count;
            LoadPreview(_styles[Selection], graphicsDevice);
        }
        
        // Confirm selection
        if (tap.Confirm ||
            (kb.IsKeyDown(Keys.Enter) && !prevKb.IsKeyDown(Keys.Enter)) ||
            (kb.IsKeyDown(Keys.Space) && !prevKb.IsKeyDown(Keys.Space)))
        {
            Game1.NoteStyleName = _styles[Selection];
            SelectionConfirmed = true;
            Close();
        }
        
        // Cancel
        if (tap.Cancel || (kb.IsKeyDown(Keys.Escape) && !prevKb.IsKeyDown(Keys.Escape)))
        {
            Close();
        }
    }
    
    public void Draw(SpriteBatch spriteBatch, int screenWidth, int screenHeight)
    {
        if (!IsActive) return;
        
        int sw = screenWidth;
        int sh = screenHeight;
        
        // Background
        DrawRect(spriteBatch, 0, 0, sw, sh, Color.Black * 0.92f);
        
        // Header
        // (fork) Layout scales with the screen (it was fixed pixels: tiny rows on phones).
        float u = PickerTouch.Scale(sh);
        int hh = PickerTouch.HeaderHeight(sh);
        _touch.BeginRows();
        DrawRect(spriteBatch, 0, 0, sw, hh, new Color(15, 15, 28));
        DrawRect(spriteBatch, 0, hh - 2, sw, 2, new Color(0xFF, 0x44, 0x88) * 0.7f);
        DrawRect(spriteBatch, 0, 0, 4, hh, new Color(0xFF, 0x44, 0x88));
        
        if (_fontRenderer != null)
        {
            int titleSize = (int)(sh * 0.055f);
            titleSize = Math.Clamp(titleSize, 32, 64);
            _fontRenderer.DrawString(spriteBatch, "ARROWS",
                new Vector2(20 * u, 6 * u), new Color(0xFF, 0x44, 0x88), titleSize);
        }
        else
        {
            spriteBatch.DrawString(_font, "ARROWS",
                new Vector2(20 * u, 6 * u), new Color(0xFF, 0x44, 0x88), 0f, Vector2.Zero, 1.3f, SpriteEffects.None, 0f);
        }
        
        _touch.DrawHeaderButtons(spriteBatch, _pixel, _font, _fontRenderer, sw, sh, new Color(0xFF, 0x44, 0x88));

        // ── Left panel: skin list ─────────────────────────────────────────────
        float listX = 16f * u;
        float listW = sw * 0.38f;
        float listY = hh + 10f * u;
        float itemH = 38f * u;
        
        if (_fontRenderer != null)
        {
            int labelSize = (int)(sh * 0.028f);
            labelSize = Math.Clamp(labelSize, 16, 28);
            _fontRenderer.DrawString(spriteBatch, "SKINS",
                new Vector2(listX + 8, listY), new Color(0xFF, 0x44, 0x88) * 0.6f, labelSize);
        }
        else
        {
            spriteBatch.DrawString(_font, "SKINS",
                new Vector2(listX + 8, listY),
                new Color(0xFF, 0x44, 0x88) * 0.6f, 0f, Vector2.Zero, 0.65f, SpriteEffects.None, 0f);
        }
        listY += 22f * u;
        
        for (int i = 0; i < _styles.Count; i++)
        {
            float iy = listY + i * itemH;
            bool sel = i == Selection;
            bool cur = _styles[i] == Game1.NoteStyleName;
            
            _touch.AddRow(i, new Rectangle((int)listX, (int)(iy - 2), (int)listW, (int)(itemH - 2)));
            if (sel)
            {
                DrawRect(spriteBatch, (int)listX, (int)(iy - 2), (int)listW, (int)(itemH - 2),
                    new Color(0xFF, 0x44, 0x88) * 0.18f);
                DrawRect(spriteBatch, (int)listX, (int)(iy - 2), 3, (int)(itemH - 2),
                    new Color(0xFF, 0x44, 0x88));
            }
            
            Color textColor = sel ? Color.White : (cur ? new Color(0xFF, 0xCC, 0x44) : Color.White * 0.6f);
            string label = _styles[i] + (cur ? " (active)" : "");
            
            if (_fontRenderer != null)
            {
                int itemSize = (int)(sh * 0.032f);
                itemSize = Math.Clamp(itemSize, 18, 32);
                _fontRenderer.DrawString(spriteBatch, label,
                    new Vector2(listX + 12 * u, iy + 8 * u), textColor, itemSize);
            }
            else
            {
                spriteBatch.DrawString(_font, label,
                    new Vector2(listX + 12 * u, iy + 8 * u),
                    textColor, 0f, Vector2.Zero, 0.82f, SpriteEffects.None, 0f);
            }
        }
        
        // ── Right panel: info + preview (same layout as the Note Splash menu) ──
        float previewX = sw * 0.42f;
        float previewW = sw - previewX - 16f;
        float previewCx = previewX + previewW / 2f;
        Color accent = new Color(0xFF, 0x44, 0x88);

        DrawText(spriteBatch, "PREVIEW", new Vector2(previewX + 8, hh + 10f * u), accent * 0.6f,
            Math.Clamp((int)(sh * 0.028f), 16, 28), 0.65f);

        var style = _previewStyle;
        float infoY = hh + 46f * u;
        Color infoColor = Color.White * 0.7f;
        int infoSize = Math.Clamp((int)(sh * 0.030f), 16, 28);
        int descSize = (int)(infoSize * 0.85f);
        foreach (var (text, dim) in DescribeStyle(style))
        {
            DrawText(spriteBatch, text, new Vector2(previewX + 8, infoY),
                dim ? infoColor * 0.6f : infoColor, dim ? descSize : infoSize, dim ? 0.65f : 0.7f);
            infoY += dim ? 20f : 24f;
        }

        // Strums on top, then one row per note type
        float laneW = NoteRenderer.LANE_WIDTH * 0.85f;
        float totalW = laneW * 4f;
        float startRX = previewCx - totalW / 2f + 40f;    // room on the left for the row labels
        float firstRowY = Math.Max(infoY + 60f, sh * 0.40f);
        float rowGap = Math.Min(laneW, (sh - 40f - firstRowY) / 3.5f);

        // Shrink everything if the rows are tighter than a note
        float refH = style?.GetNoteFrame(0, 0) is AtlasFrame rf ? Math.Max(1f, rf.FrameH) * style.NoteScale : 100f;
        float fit = Math.Min(0.85f, rowGap * 0.95f / Math.Max(1f, refH));

        string[] rowNames = { "STRUMS", "NORMAL", "NEGATIVE (X)", "POSITIVE (O)" };
        int rowLabelSize = Math.Clamp((int)(sh * 0.024f), 12, 22);
        for (int row = 0; row < 4; row++)
        {
            float rowY = firstRowY + row * rowGap;
            Vector2 labelSz = _fontRenderer != null
                ? _fontRenderer.MeasureString(rowNames[row], rowLabelSize)
                : _font.MeasureString(rowNames[row]) * 0.6f;
            DrawText(spriteBatch, rowNames[row],
                new Vector2(startRX - 14f - labelSz.X, rowY - labelSz.Y / 2f),
                Color.White * 0.45f, rowLabelSize, 0.6f);

            for (int lane = 0; lane < 4; lane++)
            {
                float cx = startRX + lane * laneW + laneW / 2f;
                if (row == 0) DrawPreviewStrum(spriteBatch, style, lane, cx, rowY, fit);
                else DrawPreviewNote(spriteBatch, style, lane, row - 1, cx, rowY, fit);
            }
        }

        // Footer hint
        string hint = PickerTouch.HintText;
        if (_fontRenderer != null)
        {
            int hintSize = (int)(sh * 0.023f);
            hintSize = Math.Clamp(hintSize, 12, 22);
            Vector2 hintMeasure = _fontRenderer.MeasureString(hint, hintSize);
            _fontRenderer.DrawString(spriteBatch, hint,
                new Vector2(sw / 2f - hintMeasure.X / 2f, sh - hintMeasure.Y - 8 * u),
                Color.White * 0.4f, hintSize);
        }
        else
        {
            var hintSz = _font.MeasureString(hint) * 0.55f;
            spriteBatch.DrawString(_font, hint,
                new Vector2(sw / 2f - hintSz.X / 2f, sh - 22),
                Color.White * 0.2f, 0f, Vector2.Zero, 0.55f, SpriteEffects.None, 0f);
        }
    }
    
    // Lane colours used for the X/O overlay and the no-style fallback
    private static readonly Color[] PreviewLaneColors =
    {
        new Color(0xC2, 0x4B, 0x99), new Color(0x00, 0xFF, 0xFF),
        new Color(0x12, 0xFA, 0x05), new Color(0xF9, 0x39, 0x3F),
    };

    /// <summary>Info lines for the selected style: (text, dimmed description?).</summary>
    private static List<(string, bool)> DescribeStyle(NoteStyle? style)
    {
        var lines = new List<(string, bool)>();
        if (style == null)
        {
            lines.Add(("Type: Failed to load", false));
            lines.Add(("Check the skin folder's files", true));
            return lines;
        }

        lines.Add((style.NoteSystem == NoteSystemKind.Fmm ? "Type: Funky Maker" : "Type: Codename", false));

        lines.Add(($"Scale: notes {style.NoteScale:0.##}x, strums {style.ReceptorScale:0.##}x", false));

        bool ownNeg = false, ownPos = false;
        for (int lane = 0; lane < 4; lane++)
        {
            ownNeg |= style.NoteFrames[lane, NoteSkins.Negative] != null;
            ownPos |= style.NoteFrames[lane, NoteSkins.Positive] != null;
        }
        string special = (ownNeg, ownPos) switch
        {
            (true, true) => "own images",
            (true, false) => "own negative, tinted positive",
            (false, true) => "tinted negative, own positive",
            _ => "tinted, with X / O overlay",
        };
        lines.Add(($"Negative / positive: {special}", false));

        bool holds = style.HoldTexture != null;
        for (int lane = 0; lane < 4 && !holds; lane++)
            holds = style.HoldPieceFrames[lane] != null;
        lines.Add(($"Holds: {(holds ? "included" : "none")}", false));
        return lines;
    }

    private void DrawPreviewStrum(SpriteBatch sb, NoteStyle? style, int lane, float cx, float cy, float fit)
    {
        if (style?.ReceptorFrames[lane, 0] is List<AtlasFrame> frames && frames.Count > 0)
        {
            // Same opacity as in game: FMM strums are 85 %, atlas strums shown at 70 % here as before
            float opacity = style.NoteSystem == NoteSystemKind.Fmm ? 0.85f : 0.7f;
            DrawFrameCentered(sb, frames[0], cx, cy, style.ReceptorScale * fit, Color.White * opacity);
            return;
        }
        float w = NoteRenderer.LANE_WIDTH * 0.85f;
        DrawRect(sb, (int)(cx - w * 0.4f), (int)(cy - 20), (int)(w * 0.8f), 40, PreviewLaneColors[lane] * 0.5f);
    }

    /// <summary>Draws a note the way gameplay does: the skin's own frame if it has one,
    /// otherwise the normal note tinted (atlas) or lane-coloured (FMM) with an X / O on top.</summary>
    private void DrawPreviewNote(SpriteBatch sb, NoteStyle? style, int lane, int skinType, float cx, float cy, float fit)
    {
        if (style?.GetNoteFrame(lane, skinType) is not AtlasFrame f) return;

        bool dedicated = skinType == NoteSkins.Default || style.NoteFrames[lane, skinType] != null;
        Color tint = dedicated || style.NoteSystem == NoteSystemKind.Fmm ? Color.White : style.GetSkinTint(skinType);
        float s = style.NoteScale * fit;
        DrawFrameCentered(sb, f, cx, cy, s, tint);

        if (!dedicated)
            DrawSymbol(sb, skinType == NoteSkins.Negative ? "X" : "O", cx, cy, f.FrameW * s, PreviewLaneColors[lane]);
    }

    private static void DrawFrameCentered(SpriteBatch sb, AtlasFrame f, float cx, float cy, float s, Color color)
    {
        float drawX = cx - f.FrameW * s / 2f + (-f.OffsetX) * s;
        float drawY = cy - f.FrameH * s / 2f + (-f.OffsetY) * s;
        sb.Draw(f.Texture, new Vector2(drawX, drawY), f.Source, color, 0f, Vector2.Zero, s, SpriteEffects.None, 0f);
    }

    /// <summary>X / O overlay like NoteRenderer.DrawNoteSymbol: ~40 % of the note width, dark outline.</summary>
    private void DrawSymbol(SpriteBatch sb, string symbol, float cx, float cy, float noteW, Color color)
    {
        var baseSize = _font.MeasureString(symbol);
        float scale = baseSize.X > 0 ? noteW * 0.40f / baseSize.X : 1f;
        var size = baseSize * scale;
        var pos = new Vector2(cx - size.X / 2f, cy - size.Y / 2f);
        float off = scale * 1.5f;
        Color outline = Color.Black * 0.8f;
        foreach (var d in new[] { new Vector2(-off, -off), new Vector2(off, -off), new Vector2(-off, off), new Vector2(off, off) })
            sb.DrawString(_font, symbol, pos + d, outline, 0f, Vector2.Zero, scale, SpriteEffects.None, 0f);
        sb.DrawString(_font, symbol, pos, color, 0f, Vector2.Zero, scale, SpriteEffects.None, 0f);
    }

    /// <summary>Text with the TrueType font when available, else the sprite font at <paramref name="fallbackScale"/>.</summary>
    private void DrawText(SpriteBatch sb, string text, Vector2 pos, Color color, int fontSize, float fallbackScale)
    {
        if (_fontRenderer != null)
            _fontRenderer.DrawString(sb, text, pos, color, fontSize);
        else
            sb.DrawString(_font, text, pos, color, 0f, Vector2.Zero, fallbackScale, SpriteEffects.None, 0f);
    }

    private void DrawRect(SpriteBatch sb, int x, int y, int w, int h, Color c)
    {
        if (w <= 0 || h <= 0) return;
        sb.Draw(_pixel, new Rectangle(x, y, w, h), c);
    }
}
