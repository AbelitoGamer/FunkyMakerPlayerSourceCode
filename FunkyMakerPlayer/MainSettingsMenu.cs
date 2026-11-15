using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using Microsoft.Xna.Framework.Input;
using System;

namespace FunkyMakerPlayer;

/// <summary>
/// HUD Editor launcher screen - accessible from main menu.
/// Explains how the HUD editor works and lets you open it.
/// </summary>
public class MainSettingsMenu : UI.IDirectScreen
{
    private readonly Texture2D _pixel;
    private readonly SpriteFont _font;
    private FontRenderer? _fontRenderer;
    private KeyboardState _prevKb;
    private MouseState _prevMs;
    private bool _waitForKeyRelease = true;
    private bool _syncPointerOnEnter = true;
    private float _pulseTimer = 0f;
    private Rectangle _enterButtonBounds = Rectangle.Empty;
    private Rectangle _backButtonBounds = Rectangle.Empty;
    private bool _enterHovered = false;
    private bool _backHovered = false;
    
    public bool BackPressed { get; private set; }
    public bool OpenHudEditor { get; private set; }

    public MainSettingsMenu(Texture2D pixel, SpriteFont font, FontRenderer? fontRenderer = null)
    {
        _pixel = pixel;
        _font = font;
        _fontRenderer = fontRenderer;
        _prevKb = Keyboard.GetState();
        _prevMs = Mouse.GetState();
    }

    public void SetFontRenderer(FontRenderer? fontRenderer) => _fontRenderer = fontRenderer;

    public void SyncInputState()
    {
        _syncPointerOnEnter = true;
        _enterHovered = false;
        _backHovered = false;
        _prevKb = Keyboard.GetState();
        _prevMs = Mouse.GetState();
    }

    public void Update(float dt, int screenWidth, int screenHeight)
    {
        Update(dt, Game1.CanAcceptInput ? Keyboard.GetState() : default, screenWidth, screenHeight);
    }

    public void Update(float dt, KeyboardState kb, int screenWidth, int screenHeight)
    {
        var ms = Mouse.GetState();

        _pulseTimer += dt;
        BackPressed = false;
        OpenHudEditor = false;

        if (!Game1.CanAcceptInput)
        {
            _enterHovered = false;
            _backHovered = false;
            _syncPointerOnEnter = true;
            _prevKb = default;
            _prevMs = Game1.NeutralMouseState(ms);
            return;
        }

        if (_syncPointerOnEnter)
        {
            _syncPointerOnEnter = false;
            _prevKb = kb;
            _prevMs = ms;
        }

        // Wait only until entry keys (Enter/Space/Escape) & mouse button are released once
        // so quick intentional presses immediately afterward are never swallowed.
        if (_waitForKeyRelease)
        {
            bool anyEntryHeld = kb.IsKeyDown(Keys.Enter) ||
                                kb.IsKeyDown(Keys.Space) ||
                                kb.IsKeyDown(Keys.Escape) ||
                                ms.LeftButton == ButtonState.Pressed;
            _prevKb = kb;
            _prevMs = ms;
            if (anyEntryHeld)
                return;
            _waitForKeyRelease = false;
        }

        Point mousePos = ms.Position;
        _enterHovered = _enterButtonBounds != Rectangle.Empty && _enterButtonBounds.Contains(mousePos);
        _backHovered  = _backButtonBounds  != Rectangle.Empty && _backButtonBounds.Contains(mousePos);

        bool mouseClicked = ms.LeftButton == ButtonState.Pressed && _prevMs.LeftButton == ButtonState.Released;

        // ESC or Back button click to return
        if ((kb.IsKeyDown(Keys.Escape) && !_prevKb.IsKeyDown(Keys.Escape)) ||
            (mouseClicked && _backHovered))
        {
            BackPressed = true;
        }

        // ENTER, Space, or clicking the open button launches the HUD Editor
        if ((kb.IsKeyDown(Keys.Enter) && !_prevKb.IsKeyDown(Keys.Enter)) ||
            (kb.IsKeyDown(Keys.Space) && !_prevKb.IsKeyDown(Keys.Space)) ||
            (mouseClicked && _enterHovered))
        {
            OpenHudEditor = true;
        }

        _prevKb = kb;
        _prevMs = ms;
    }
    
    /// <summary>
    /// Reset input state when entering this screen so held keys don't leak across screens
    /// </summary>
    public void ResetInputCooldown()
    {
        _waitForKeyRelease = true;
        _syncPointerOnEnter = true;
        BackPressed = false;
        OpenHudEditor = false;
        _enterHovered = false;
        _backHovered = false;
    }

    public void Draw(SpriteBatch sb, int screenWidth, int screenHeight)
    {
        if (screenWidth <= 0 || screenHeight <= 0) return;

        // Modern dark background matching FreeplayMenu / EditorPicker
        sb.Draw(_pixel, new Rectangle(0, 0, screenWidth, screenHeight), new Color(20, 18, 28));
        for (int y = 0; y < screenHeight; y += 120)
            sb.Draw(_pixel, new Rectangle(0, y, screenWidth, 1), Color.White * 0.02f);
        for (int x = 0; x < screenWidth; x += 120)
            sb.Draw(_pixel, new Rectangle(x, 0, 1, screenHeight), Color.White * 0.02f);

        int centerX = screenWidth / 2;
        float scale = Math.Min(screenWidth / 1280f, screenHeight / 720f);
        scale = Math.Max(0.55f, scale);

        // Title
        string title = "HUD EDITOR";
        int titleFontSize = Math.Clamp((int)(48 * scale), 26, 72);
        float titleFallbackScale = 1.5f * scale;
        var titleSize = MeasureText(title, titleFontSize, titleFallbackScale);
        float titleY = Math.Max(16f, screenHeight * 0.055f);
        Vector2 titlePos = new Vector2(centerX - titleSize.X / 2f, titleY);
        DrawText(sb, title, titlePos + new Vector2(2, 2), new Color(255, 68, 136) * 0.25f, titleFontSize, titleFallbackScale);
        DrawText(sb, title, titlePos, new Color(255, 215, 0), titleFontSize, titleFallbackScale);

        // Instructions text
        string[] instructions = new[]
        {
            "The HUD Editor lets you customize the position",
            "and visibility of all HUD elements (health bar,",
            "score, song title, status info, combo, etc.).",
            "",
            "CONTROLS:",
            "  - Left-Click & drag to move elements",
            "  - Right-Click / V / Space: Hide or Show element",
            "  - A: Hide/Show ALL elements at once",
            "  - S: Save layout   |   R: Reset defaults",
            "  - H: Hide/Show instructions",
            "  - ESC: Save & Exit editor",
            "",
            "Changes are saved to: Assets/data/hud_settings.json",
            "NOTE: Icons follow the health bar in FNF Base mode."
        };

        // Measure instruction typography and fit cleanly between title and action buttons
        int mainFontSize = Math.Clamp((int)(20 * scale), 13, 30);
        int subFontSize  = Math.Clamp((int)(18 * scale), 12, 27);
        int bottomFontSize = Math.Clamp((int)(18 * scale), 12, 24);
        int enterFontSize = Math.Clamp((int)(26 * scale), 16, 40);

        float enterFallbackScale = 1.05f * scale;
        float bottomFallbackScale = 0.80f * scale;

        int footerH = Math.Clamp((int)(MeasureText("A", bottomFontSize, bottomFallbackScale).Y + 12), 26, 44);
        int enterBtnH = Math.Clamp((int)(MeasureText("A", enterFontSize, enterFallbackScale).Y + 18), 38, 62);
        int topLimit = (int)(titleY + titleSize.Y + Math.Max(14f, 20f * scale));
        int bottomLimit = screenHeight - footerH - enterBtnH - Math.Max(28, (int)(40 * scale));
        int maxBoxH = Math.Max(160, bottomLimit - topLimit);
        int maxBoxW = Math.Max(320, Math.Min((int)(780 * scale), screenWidth - 48));
        float padX = Math.Max(22f, 32f * scale);
        float padY = Math.Max(16f, 20f * scale);

        // Ensure longest instruction line fits horizontally and total block fits vertically without overlap
        float measuredBlockH;
        while (true)
        {
            float maxLineW = 0f;
            float totalH = 0f;
            float baseMainLineH = Math.Max(MeasureText("Ag", mainFontSize, mainFontSize / 24f).Y + 4f, mainFontSize * 1.25f);
            float baseSubLineH  = Math.Max(MeasureText("Ag", subFontSize,  subFontSize / 24f).Y + 4f,  subFontSize * 1.25f);

            foreach (var line in instructions)
            {
                if (string.IsNullOrEmpty(line))
                {
                    totalH += baseMainLineH * 0.45f;
                    continue;
                }
                int fz = line.StartsWith("  ") ? subFontSize : mainFontSize;
                float lh = line.StartsWith("  ") ? baseSubLineH : baseMainLineH;
                var sz = MeasureText(line, fz, fz / 24f);
                if (sz.X > maxLineW) maxLineW = sz.X;
                totalH += Math.Max(lh, sz.Y + 3f);
            }

            measuredBlockH = totalH;
            bool fitsH = totalH + padY * 2 <= maxBoxH;
            bool fitsW = maxLineW + padX * 2 <= maxBoxW;
            if ((fitsH && fitsW) || mainFontSize <= 11)
                break;

            mainFontSize--;
            subFontSize = Math.Max(10, subFontSize - 1);
        }

        int boxWidth = maxBoxW;
        int boxHeight = Math.Clamp((int)Math.Ceiling(measuredBlockH + padY * 2), 180, maxBoxH);
        int boxX = centerX - boxWidth / 2;
        int boxY = topLimit + Math.Max(0, (maxBoxH - boxHeight) / 2);

        // Draw box background
        sb.Draw(_pixel, new Rectangle(boxX, boxY, boxWidth, boxHeight), new Color(28, 26, 42, 235));
        DrawRectangleBorder(sb, boxX, boxY, boxWidth, boxHeight, new Color(255, 68, 136) * 0.7f, Math.Max(2, (int)(2 * scale)));
        sb.Draw(_pixel, new Rectangle(boxX, boxY, Math.Max(4, (int)(5 * scale)), boxHeight), new Color(255, 215, 0));

        float curY = boxY + (boxHeight - measuredBlockH) / 2f;
        float mainStep = Math.Max(MeasureText("Ag", mainFontSize, mainFontSize / 24f).Y + 4f, mainFontSize * 1.25f);
        float subStep  = Math.Max(MeasureText("Ag", subFontSize,  subFontSize / 24f).Y + 4f,  subFontSize * 1.25f);

        foreach (var line in instructions)
        {
            if (string.IsNullOrEmpty(line))
            {
                curY += mainStep * 0.45f;
                continue;
            }

            bool isIndented = line.StartsWith("  ");
            int lineFontSize = isIndented ? subFontSize : mainFontSize;
            float lineFallbackScale = lineFontSize / 24f;
            float step = Math.Max(isIndented ? subStep : mainStep, MeasureText(line, lineFontSize, lineFallbackScale).Y + 3f);

            Color lineColor = line.StartsWith("CONTROLS:") || line.StartsWith("NOTE:")
                ? new Color(255, 215, 0)
                : (isIndented ? Color.White * 0.86f : Color.White * 0.94f);

            DrawText(sb, line, new Vector2(boxX + padX, curY), lineColor, lineFontSize, lineFallbackScale);
            curY += step;
        }

        // Clickable "Press ENTER to open" action button cleanly spaced below card
        float pulse = (float)Math.Sin(_pulseTimer * Math.PI * 1.8) * 0.12f + 0.88f;
        string enterPrompt = "Press ENTER to open HUD Editor";
        var enterSize = MeasureText(enterPrompt, enterFontSize, enterFallbackScale);
        int enterBtnW = Math.Clamp((int)(enterSize.X + 48 * scale), 260, Math.Max(260, screenWidth - 48));
        int enterBtnX = centerX - enterBtnW / 2;
        int enterBtnY = boxY + boxHeight + Math.Max(14, (int)(18 * scale));
        _enterButtonBounds = new Rectangle(enterBtnX, enterBtnY, enterBtnW, enterBtnH);

        Color btnBg = _enterHovered ? new Color(0, 230, 140) * 0.28f : new Color(0, 190, 115) * 0.14f;
        sb.Draw(_pixel, _enterButtonBounds, btnBg);
        DrawRectangleBorder(sb, enterBtnX, enterBtnY, enterBtnW, enterBtnH,
            (_enterHovered ? Color.White : new Color(0, 230, 140)) * pulse, 2);
        DrawText(sb, enterPrompt,
            new Vector2(enterBtnX + (enterBtnW - enterSize.X) / 2f, enterBtnY + (enterBtnH - enterSize.Y) / 2f),
            (_enterHovered ? Color.White : new Color(0, 240, 150)) * pulse, enterFontSize, enterFallbackScale);

        // Bottom clickable Back hint cleanly pinned to footer
        string bottomInstr = "ENTER / Click: Open HUD Editor   |   ESC: Back";
        var bottomSize = MeasureText(bottomInstr, bottomFontSize, bottomFallbackScale);
        int backBtnW = (int)bottomSize.X + 24;
        int backBtnX = centerX - backBtnW / 2;
        int backBtnY = screenHeight - footerH - 8;
        _backButtonBounds = new Rectangle(backBtnX, backBtnY, backBtnW, footerH);
        if (_backHovered)
            sb.Draw(_pixel, _backButtonBounds, Color.White * 0.08f);
        DrawText(sb, bottomInstr,
            new Vector2(centerX - bottomSize.X / 2f, backBtnY + (footerH - bottomSize.Y) / 2f),
            _backHovered ? Color.White * 0.85f : Color.White * 0.45f, bottomFontSize, bottomFallbackScale);
    }

    private Vector2 MeasureText(string text, int fontSize, float fallbackScale)
    {
        if (_fontRenderer != null)
            return _fontRenderer.MeasureString(text, fontSize);
        return _font.MeasureString(text) * fallbackScale;
    }

    private void DrawText(SpriteBatch sb, string text, Vector2 pos, Color color, int fontSize, float fallbackScale)
    {
        if (_fontRenderer != null)
            _fontRenderer.DrawString(sb, text, pos, color, fontSize);
        else
            sb.DrawString(_font, text, pos, color, 0f, Vector2.Zero, fallbackScale, SpriteEffects.None, 0f);
    }
    
    private void DrawRectangleBorder(SpriteBatch sb, int x, int y, int width, int height, Color color, int thickness)
    {
        // Top
        sb.Draw(_pixel, new Rectangle(x, y, width, thickness), color);
        // Bottom
        sb.Draw(_pixel, new Rectangle(x, y + height - thickness, width, thickness), color);
        // Left
        sb.Draw(_pixel, new Rectangle(x, y, thickness, height), color);
        // Right
        sb.Draw(_pixel, new Rectangle(x + width - thickness, y, thickness, height), color);
    }
}
