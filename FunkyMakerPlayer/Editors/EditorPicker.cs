using FunkyMakerPlayer.UI;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using Microsoft.Xna.Framework.Input;
using System;

namespace FunkyMakerPlayer.Editors;

/// <summary>
/// Editor Hub — choose between creating a new chart, editing an existing chart, or editing HUD layout.
/// Supports both intuitive Up/Down keyboard navigation and full mouse hover/click.
/// </summary>
public class EditorPicker : FunkyMakerPlayer.UI.IDirectScreen
{
    private readonly Texture2D _pixel;
    private readonly SpriteFont? _font;
    private readonly FontRenderer? _fontRenderer;

    private int _selected = 0;
    private int _hoveredCard = -1;
    private bool _backHovered = false;
    private Rectangle _backBtnBounds = Rectangle.Empty;
    private readonly Rectangle[] _cardBounds;

    private KeyboardState _prevKb;
    private MouseState _prevMouse;
    private Point _lastMousePos = new(-9999, -9999);
    private float _inputCooldown = 0f;
    private const float INPUT_COOLDOWN_DURATION = 0.22f;

    public bool BackPressed { get; private set; }
    public bool EditorChosen { get; private set; }
    public EditorType ChosenEditor { get; private set; }

    private struct EditorCard
    {
        public string Name;
        public string Description;
        public string Badge;
        public Color AccentColor;
        public EditorType Type;
    }

    private readonly EditorCard[] _cards =
    {
        new()
        {
            Name = "NEW CHART / LEVEL",
            Description = "Create a brand-new .FNMM chart with custom BPM & optional .OGG audio",
            Badge = "NEW",
            AccentColor = new Color(46, 224, 150),
            Type = EditorType.NewChart
        },
        new()
        {
            Name = "CHART EDITOR",
            Description = "Place & resize notes/holds, edit BPM, sync audio, preview & playtest",
            Badge = "OPEN",
            AccentColor = new Color(255, 68, 136),
            Type = EditorType.Chart
        },
        new()
        {
            Name = "HUD EDITOR",
            Description = "Customize receptor positions, combo popups, health bar & note style",
            Badge = "HUD",
            AccentColor = new Color(0, 200, 255),
            Type = EditorType.Hud
        },
        new()
        {
            Name = "CHARACTER EDITOR",
            Description = "Import Sparrow XML+PNG or Animate Spritemaps, align poses & icons for Charts/Online",
            Badge = "CHAR",
            AccentColor = new Color(178, 102, 255),
            Type = EditorType.Character
        },
    };

    private static readonly string[] UPCOMING_TOOLS =
    {
        "Stage Editor", "Events Editor", "Sprite Editor"
    };

    public EditorPicker(Texture2D pixel, SpriteFont? font, FontRenderer? fontRenderer)
    {
        _pixel = pixel;
        _font = font;
        _fontRenderer = fontRenderer;
        _cardBounds = new Rectangle[_cards.Length];
        _prevKb = LayoutKeyboard.GetState();
        _prevMouse = UI.PointerInput.GetState();
    }

    public void ResetInputCooldown()
    {
        _inputCooldown = INPUT_COOLDOWN_DURATION;
        BackPressed = false;
        EditorChosen = false;
        _hoveredCard = -1;
        _prevKb = LayoutKeyboard.GetState();
        _prevMouse = UI.PointerInput.GetState();
        _lastMousePos = _prevMouse.Position;
    }

    public void SyncInputState()
    {
        _inputCooldown = Math.Max(_inputCooldown, 0.08f);
        _hoveredCard = -1;
        _backHovered = false;
        _prevKb = LayoutKeyboard.GetState();
        _prevMouse = UI.PointerInput.GetState();
        _lastMousePos = _prevMouse.Position;
    }

    public void Update(float dt, KeyboardState kb, int screenWidth, int screenHeight)
    {
        var ms = UI.PointerInput.GetState();
        if (!Game1.CanAcceptInput)
        {
            _hoveredCard = -1;
            _backHovered = false;
            _prevKb = default;
            _prevMouse = Game1.NeutralMouseState(ms);
            _lastMousePos = ms.Position;
            return;
        }
        bool mouseMoved = Math.Abs(ms.X - _lastMousePos.X) > 2 || Math.Abs(ms.Y - _lastMousePos.Y) > 2;
        if (mouseMoved) _lastMousePos = ms.Position;

        bool clicked = ms.LeftButton == ButtonState.Pressed && _prevMouse.LeftButton == ButtonState.Released;

        _hoveredCard = -1;
        for (int i = 0; i < _cardBounds.Length; i++)
        {
            if (_cardBounds[i] != Rectangle.Empty && _cardBounds[i].Contains(ms.Position))
            {
                _hoveredCard = i;
                if (mouseMoved && _selected != i) { _selected = i; UI.UiSfx.Play(UI.UiSound.Move); }
                break;
            }
        }
        _backHovered = _backBtnBounds != Rectangle.Empty && _backBtnBounds.Contains(ms.Position);

        if (_inputCooldown > 0f)
        {
            _inputCooldown -= dt;
            _prevKb = kb;
            _prevMouse = ms;
            return;
        }

        bool up = (kb.IsKeyDown(Keys.Up) && !_prevKb.IsKeyDown(Keys.Up)) ||
                  (kb.IsKeyDown(Keys.Left) && !_prevKb.IsKeyDown(Keys.Left));
        bool down = (kb.IsKeyDown(Keys.Down) && !_prevKb.IsKeyDown(Keys.Down)) ||
                    (kb.IsKeyDown(Keys.Right) && !_prevKb.IsKeyDown(Keys.Right));
        bool enter = (kb.IsKeyDown(Keys.Enter) && !_prevKb.IsKeyDown(Keys.Enter)) ||
                     (kb.IsKeyDown(Keys.Space) && !_prevKb.IsKeyDown(Keys.Space));
        bool esc = kb.IsKeyDown(Keys.Escape) && !_prevKb.IsKeyDown(Keys.Escape);
        bool seven = Controls.Pressed(HotkeyAction.OpenChartEditor, kb, _prevKb);

        if (up) _selected = (_selected - 1 + _cards.Length) % _cards.Length;
        else if (down) _selected = (_selected + 1) % _cards.Length;
        if (up || down) UI.UiSfx.Play(UI.UiSound.Move);

        if (clicked && _backHovered)
        {
            BackPressed = true;
        }
        else if (clicked && _hoveredCard >= 0)
        {
            _selected = _hoveredCard;
            SelectCurrentCard();
        }
        else if (enter)
        {
            SelectCurrentCard();
        }
        else if (esc || seven)
        {
            BackPressed = true;
        }

        _prevKb = kb;
        _prevMouse = ms;
    }

    private void SelectCurrentCard()
    {
        EditorChosen = true;
        ChosenEditor = _cards[_selected].Type;
        UI.UiSfx.Play(UI.UiSound.EditorSelect);
        Console.WriteLine($"[EditorPicker] Chosen: {_cards[_selected].Name} ({ChosenEditor})");
    }

    public void Draw(SpriteBatch sb, int sw, int sh)
    {
        float s = UiScale.FromScreen(sw, sh);
        DrawRect(sb, 0, 0, sw, sh, new Color(20, 18, 28));

        int gridStep = UiScale.Px(120, s, 64);
        for (int y = 0; y < sh; y += gridStep)
            DrawRect(sb, 0, y, sw, 1, Color.White * 0.02f);
        for (int x = 0; x < sw; x += gridStep)
            DrawRect(sb, x, 0, 1, sh, Color.White * 0.02f);

        // Top-right ESC Back button
        int backW = UiScale.Px(112, s);
        int backH = UiScale.Px(36, s);
        int margin = UiScale.Px(24, s);
        int topPad = UiScale.Px(22, s);
        _backBtnBounds = new Rectangle(sw - backW - margin, topPad, backW, backH);
        DrawRect(sb, _backBtnBounds.X, _backBtnBounds.Y, backW, backH, _backHovered ? new Color(56, 48, 76) : new Color(34, 30, 48));
        DrawBorder(sb, _backBtnBounds, _backHovered ? new Color(255, 68, 136) : Color.White * 0.16f);
        int backTextSize = UiScale.FontPx(15, s);
        var backSz = MeasureText(UI.PointerInput.BackLabel, backTextSize);
        DrawText(sb, UI.PointerInput.BackLabel, new Vector2(_backBtnBounds.X + (backW - backSz.X) / 2f, _backBtnBounds.Y + (backH - backSz.Y) / 2f),
            Color.White * (_backHovered ? 1f : 0.85f), backTextSize);

        // Header
        string title = "EDITOR HUB";
        int titleSize = UiScale.FontPx(46, s);
        var titleSz = MeasureText(title, titleSize);
        float titleX = sw / 2f - titleSz.X / 2f;
        float titleY = sh * 0.09f;
        int shadowOff = UiScale.Px(2, s, 1);
        DrawText(sb, title, new Vector2(titleX + shadowOff, titleY + shadowOff), new Color(255, 68, 136) * 0.18f, titleSize);
        DrawText(sb, title, new Vector2(titleX, titleY), new Color(255, 215, 0), titleSize);

        int lineY = (int)(titleY + titleSz.Y + UiScale.Px(6, s));
        int lineW = Math.Clamp((int)(sw * 0.18f), UiScale.Px(140, s), UiScale.Px(280, s));
        DrawRect(sb, sw / 2 - lineW / 2, lineY, lineW, UiScale.Px(3, s, 2), new Color(255, 68, 136) * 0.7f);

        string subtitle = "Build new charts from scratch or polish existing .FNMM levels";
        int subSize = UiScale.FontPx(17, s);
        var subSz = MeasureText(subtitle, subSize);
        float subY = lineY + UiScale.Px(12, s);
        DrawText(sb, subtitle, new Vector2(sw / 2f - subSz.X / 2f, subY), Color.White * 0.55f, subSize);

        // Main active editor cards (centered responsive list)
        int cardW = Math.Clamp((int)(sw * 0.52f), UiScale.Px(460, s), UiScale.Px(760, s));
        int cardH = Math.Clamp((int)(sh * 0.115f), UiScale.Px(70, s), UiScale.Px(100, s));
        int cardGap = UiScale.Px(12, s);
        int totalH = _cards.Length * cardH + (_cards.Length - 1) * cardGap;

        float topBound = subY + subSz.Y + UiScale.Px(24, s);
        float bottomBound = sh - UiScale.Px(110, s);
        // Small / boosted (touch) screens: shrink the cards so they never run under the footer
        float availH = bottomBound - topBound;
        if (totalH > availH && _cards.Length > 0)
        {
            cardGap = Math.Max(4, (int)(cardGap * 0.6f));
            cardH = Math.Max(UiScale.Px(44, s), (int)((availH - (_cards.Length - 1) * cardGap) / _cards.Length));
            totalH = _cards.Length * cardH + (_cards.Length - 1) * cardGap;
        }
        int startY = (int)Math.Max(topBound, topBound + (bottomBound - topBound - totalH) / 2f);
        int cardX = sw / 2 - cardW / 2;

        int glowPad = UiScale.Px(3, s);
        int selStripe = UiScale.Px(5, s);
        int idleStripe = UiScale.Px(3, s);
        int badgePad = UiScale.Px(18, s);
        int textGap = UiScale.Px(4, s);

        for (int i = 0; i < _cards.Length; i++)
        {
            var card = _cards[i];
            int cardY = startY + i * (cardH + cardGap);
            var r = new Rectangle(cardX, cardY, cardW, cardH);
            _cardBounds[i] = r;

            bool selected = i == _selected;
            bool hovered = i == _hoveredCard;

            if (selected)
                DrawRect(sb, r.X - glowPad, r.Y - glowPad, r.Width + glowPad * 2, r.Height + glowPad * 2, card.AccentColor * 0.30f);

            DrawRect(sb, r.X, r.Y, r.Width, r.Height, selected ? new Color(34, 30, 50) : (hovered ? new Color(30, 28, 46) : new Color(24, 24, 38)));
            DrawBorder(sb, r, selected ? card.AccentColor : (hovered ? card.AccentColor * 0.55f : Color.White * 0.10f));
            DrawRect(sb, r.X, r.Y, selected ? selStripe : idleStripe, r.Height, card.AccentColor * (selected ? 1f : 0.45f));

            // Left badge box
            int badgeSize = Math.Clamp(cardH - UiScale.Px(24, s), UiScale.Px(44, s), UiScale.Px(72, s));
            var badgeRect = new Rectangle(r.X + badgePad, r.Y + (cardH - badgeSize) / 2, badgeSize, badgeSize);
            DrawRect(sb, badgeRect.X, badgeRect.Y, badgeRect.Width, badgeRect.Height, card.AccentColor * (selected ? 0.25f : 0.14f));
            DrawBorder(sb, badgeRect, card.AccentColor * (selected ? 0.8f : 0.4f));

            int badgeFont = Math.Clamp((int)(badgeSize * 0.26f), UiScale.FontPx(11, s), UiScale.FontPx(16, s));
            var bSz = MeasureText(card.Badge, badgeFont);
            DrawText(sb, card.Badge, new Vector2(badgeRect.X + (badgeRect.Width - bSz.X) / 2f, badgeRect.Y + (badgeRect.Height - bSz.Y) / 2f),
                card.AccentColor, badgeFont);

            // Text block
            int textX = badgeRect.Right + badgePad;
            int nameSize = Math.Clamp((int)(cardH * 0.28f), UiScale.FontPx(18, s), UiScale.FontPx(28, s));
            int descSize = Math.Clamp((int)(cardH * 0.17f), UiScale.FontPx(12, s), UiScale.FontPx(17, s));
            int textMaxW = r.Right - textX - UiScale.Px(40, s);
            while (nameSize > 10 && MeasureText(card.Name, nameSize).X > textMaxW) nameSize--;
            while (descSize > 9 && MeasureText(card.Description, descSize).X > textMaxW) descSize--;
            var nameSz = MeasureText(card.Name, nameSize);
            var descSz = MeasureText(card.Description, descSize);
            float blockH = nameSz.Y + textGap + descSz.Y;
            float textTop = r.Y + (cardH - blockH) / 2f;

            DrawText(sb, card.Name, new Vector2(textX, textTop), selected ? Color.White : Color.White * 0.88f, nameSize);
            DrawText(sb, card.Description, new Vector2(textX, textTop + nameSz.Y + textGap), Color.White * (selected ? 0.68f : 0.45f), descSize);

            if (selected)
            {
                int arrowSz = UiScale.FontPx(22, s);
                var arrM = MeasureText(">", arrowSz);
                DrawText(sb, ">", new Vector2(r.Right - arrM.X - UiScale.Px(20, s), r.Y + (cardH - arrM.Y) / 2f), card.AccentColor, arrowSz);
            }
        }

        // Upcoming / roadmap tools strip (non-interactive informational pills)
        string upcomingLbl = "UPCOMING TOOLS:";
        int upFont = UiScale.FontPx(12, s);
        int pillH = UiScale.Px(24, s);
        int pillPad = UiScale.Px(16, s);
        int pillGap = UiScale.Px(8, s);
        var upM = MeasureText(upcomingLbl, upFont);
        int pillY = sh - UiScale.Px(78, s);
        int totalRowW = (int)upM.X + UiScale.Px(12, s);
        foreach (var t in UPCOMING_TOOLS)
            totalRowW += (int)MeasureText(t, upFont).X + pillPad + pillGap;

        int rx = Math.Max(UiScale.Px(20, s), (sw - totalRowW) / 2);
        DrawText(sb, upcomingLbl, new Vector2(rx, pillY + (pillH - upM.Y) / 2f), Color.White * 0.35f, upFont);
        rx += (int)upM.X + UiScale.Px(10, s);

        foreach (var t in UPCOMING_TOOLS)
        {
            var tm = MeasureText(t, upFont);
            int pw = (int)tm.X + pillPad;
            DrawRect(sb, rx, pillY, pw, pillH, new Color(28, 28, 40) * 0.7f);
            DrawBorder(sb, new Rectangle(rx, pillY, pw, pillH), Color.White * 0.08f);
            DrawText(sb, t, new Vector2(rx + pillPad / 2f, pillY + (pillH - tm.Y) / 2f), Color.White * 0.38f, upFont);
            rx += pw + pillGap;
        }

        string hint = UI.PointerInput.IsTouch
            ? "Tap a tool to open it  |  Back gesture: return"
            : $"UP/DOWN or MOUSE: Navigate  |  ENTER or CLICK: Open  |  ESC / {Controls.Hint(HotkeyAction.OpenChartEditor)}: Back";
        int hintSize = UiScale.FontPx(15, s);
        var hintSz = MeasureText(hint, hintSize);
        DrawText(sb, hint, new Vector2(sw / 2f - hintSz.X / 2f, sh - UiScale.Px(34, s)), Color.White * 0.42f, hintSize);
    }

    private void DrawRect(SpriteBatch sb, int x, int y, int w, int h, Color color) =>
        sb.Draw(_pixel, new Rectangle(x, y, w, h), color);

    private void DrawBorder(SpriteBatch sb, Rectangle r, Color c)
    {
        DrawRect(sb, r.X, r.Y, r.Width, 1, c);
        DrawRect(sb, r.X, r.Bottom - 1, r.Width, 1, c);
        DrawRect(sb, r.X, r.Y, 1, r.Height, c);
        DrawRect(sb, r.Right - 1, r.Y, 1, r.Height, c);
    }

    private Vector2 MeasureText(string text, int size)
    {
        if (_fontRenderer != null) return _fontRenderer.MeasureString(text, size);
        if (_font != null) return _font.MeasureUi(text) * (size / 32f);
        return new Vector2(text.Length * size * 0.5f, size);
    }

    private void DrawText(SpriteBatch sb, string text, Vector2 pos, Color color, int size)
    {
        if (_fontRenderer != null) _fontRenderer.DrawString(sb, text, pos, color, size);
        else if (_font != null) sb.DrawUiString(_font, text, pos, color, 0f, Vector2.Zero, size / 32f, SpriteEffects.None, 0f);
    }
}
