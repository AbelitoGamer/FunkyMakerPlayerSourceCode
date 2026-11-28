using System;
using System.Collections.Generic;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using Microsoft.Xna.Framework.Input;
using Microsoft.Xna.Framework.Input.Touch;
using FunkyMakerPlayer.UI;

namespace FunkyMakerPlayer;

/// <summary>
/// (fork) Test-build gate screen (see <see cref="TestGate"/>). While the gate hasn't passed, Update/Draw
/// run only this screen: nothing else in the game can be reached.
/// </summary>
public partial class Game1
{
    private readonly TestGate _testGate = new();
    private KeyboardState _testGatePrevKb;
    private GamePadState _testGatePrevPad;
    private bool _testGatePrevTouch = true; // ignore a touch already held at startup
    private float _testGateTime;

    /// <summary>True while the game must stay blocked (call from Update; then skip the rest of the frame).</summary>
    private bool UpdateTestGate(GameTime gameTime)
    {
        if (!TestGate.Enabled || _testGate.Passed) return false;

        _testGateTime += (float)gameTime.ElapsedGameTime.TotalSeconds;
        var kb = Keyboard.GetState();
        var pad = GamePad.GetState(PlayerIndex.One);
        bool touch = false;
        try { foreach (var t in TouchPanel.GetState()) if (t.State is TouchLocationState.Pressed or TouchLocationState.Moved) touch = true; }
        catch { /* no touch support */ }

        bool active = IsActive;
        bool Pressed(Keys k) => active && kb.IsKeyDown(k) && !_testGatePrevKb.IsKeyDown(k);
        bool quit    = Pressed(Keys.Escape) || (IsTouchPlatform && Pressed(Keys.Back)) // Android back button
                    || (pad.Buttons.Back == ButtonState.Pressed && _testGatePrevPad.Buttons.Back != ButtonState.Pressed);
        bool confirm = Pressed(Keys.Enter) || Pressed(Keys.Space) || (touch && !_testGatePrevTouch);

        _testGatePrevKb = kb;
        _testGatePrevPad = pad;
        _testGatePrevTouch = touch;

        if (_testGate.CanRetry && confirm)
        {
            Console.WriteLine($"[TestGate] Retry requested (touch={touch}, enter={kb.IsKeyDown(Keys.Enter)}, space={kb.IsKeyDown(Keys.Space)}, active={active})");
            _testGate.Start();
        }
        else if (quit || (_testGate.State == TestGate.GateState.Closed && confirm))
            QuitFromTestGate();

        return !_testGate.Passed;
    }

    private void QuitFromTestGate()
    {
#if ANDROID
        try
        {
            var activity = (global::Android.App.Activity?)Microsoft.Xna.Framework.Game.Activity;
            if (activity != null) { activity.FinishAffinity(); return; }
        }
        catch { /* fall through */ }
#endif
        Exit();
    }

    /// <summary>Draws the gate screen straight to the window. Returns false once the gate has passed.</summary>
    private bool DrawTestGate()
    {
        if (!TestGate.Enabled || _testGate.Passed) return false;
        if (_spriteBatch == null) return true;

        GraphicsDevice.SetRenderTarget(null);
        GraphicsDevice.Clear(new Color(12, 12, 20));
        int w = GraphicsDevice.PresentationParameters.BackBufferWidth;
        int h = GraphicsDevice.PresentationParameters.BackBufferHeight;
        float s = UiScale.FromScreen(w, h);

        string title;
        Color accent;
        var body = new List<string>();
        string hint;
        switch (_testGate.State)
        {
            case TestGate.GateState.Closed:
                title = "TESTING IS CLOSED";
                accent = new Color(0xFF, 0x44, 0x66);
                body.Add(string.IsNullOrWhiteSpace(_testGate.ServerMessage)
                    ? "The testing period for this version has ended. Thanks for playing!"
                    : _testGate.ServerMessage.Trim());
                hint = IsTouchPlatform ? "Tap or press BACK to close the game" : "[ENTER] / [ESC] Close the game";
                break;
            case TestGate.GateState.NoInternet:
                title = "NO INTERNET CONNECTION";
                accent = new Color(0xFF, 0xAA, 0x33);
                body.Add("This test version needs an internet connection to start.");
                body.Add("Connect to the internet and try again.");
                hint = IsTouchPlatform ? "Tap to retry  -  BACK to quit" : "[ENTER] Retry    [ESC] Quit";
                break;
            case TestGate.GateState.Unreachable:
                title = "CAN'T REACH THE SERVER";
                accent = new Color(0xFF, 0xAA, 0x33);
                body.Add($"The test server didn't answer after {TestGate.MaxAttempts} attempts.");
                body.Add("It may be down or waking up. Please try again in a minute.");
                hint = IsTouchPlatform ? "Tap to retry  -  BACK to quit" : "[ENTER] Retry    [ESC] Quit";
                break;
            default:
                title = "TEST VERSION";
                accent = new Color(0x55, 0xCC, 0xFF);
                int dots = (int)(_testGateTime * 3f) % 4;
                body.Add("Checking test access" + new string('.', dots) + new string(' ', 3 - dots));
                body.Add($"Attempt {Math.Max(1, _testGate.Attempt)}/{TestGate.MaxAttempts}");
                if (_testGate.Attempt > 1)
                    body.Add("The server can take up to a minute to wake up.");
                hint = IsTouchPlatform ? "BACK to quit" : "[ESC] Quit";
                break;
        }

        int panelW = Math.Min(w - UiScale.Px(48, s), UiScale.Px(760, s));
        int pad = UiScale.Px(28, s);
        int titleSize = UiScale.FontPx(34, s);
        int bodySize = UiScale.FontPx(20, s);
        int hintSize = UiScale.FontPx(16, s);

        // Wrap body lines to the panel width
        var lines = new List<string>();
        foreach (var para in body)
            lines.AddRange(WrapGateText(para, bodySize, panelW - pad * 2));

        float titleH = MeasureGate("Ag", titleSize).Y;
        float lineH = MeasureGate("Ag", bodySize).Y * 1.25f;
        float hintH = MeasureGate("Ag", hintSize).Y;
        int panelH = (int)(pad * 2 + titleH + pad * 0.8f + lines.Count * lineH + pad + hintH);
        var panel = new Rectangle((w - panelW) / 2, (h - panelH) / 2, panelW, panelH);

        _spriteBatch.Begin(samplerState: SamplerState.LinearClamp);
        if (_pixel != null)
        {
            _spriteBatch.Draw(_pixel, panel, new Color(22, 24, 38));
            int border = Math.Max(2, UiScale.Px(3, s));
            _spriteBatch.Draw(_pixel, new Rectangle(panel.X, panel.Y, panel.Width, border), accent);
            _spriteBatch.Draw(_pixel, new Rectangle(panel.X, panel.Bottom - border, panel.Width, border), accent * 0.5f);
        }

        float y = panel.Y + pad;
        DrawGateCentered(title, titleSize, y, w, accent);
        y += titleH + pad * 0.8f;
        foreach (var line in lines)
        {
            DrawGateCentered(line, bodySize, y, w, Color.White);
            y += lineH;
        }
        y += pad - (lineH - MeasureGate("Ag", bodySize).Y);
        DrawGateCentered(hint, hintSize, y, w, new Color(160, 165, 190));
        _spriteBatch.End();
        return true;
    }

    private static bool IsTouchPlatform =>
#if ANDROID
        true;
#else
        false;
#endif

    private Vector2 MeasureGate(string text, int size)
    {
        if (_fontRenderer != null) return _fontRenderer.MeasureString(text, size);
        if (_font == null) return new Vector2(text.Length * size * 0.5f, size);
        return _font.MeasureUi(ErrorOverlay.Safe(text)) * (size / (float)Math.Max(1, _font.LineSpacing));
    }

    private void DrawGateCentered(string text, int size, float y, int screenW, Color color)
    {
        var sz = MeasureGate(text, size);
        var pos = new Vector2(MathF.Round((screenW - sz.X) / 2f), MathF.Round(y));
        if (_fontRenderer != null)
            _fontRenderer.DrawString(_spriteBatch, text, pos, color, size);
        else if (_font != null)
            _spriteBatch.DrawUiString(_font, ErrorOverlay.Safe(text), pos, color, 0f, Vector2.Zero,
                size / (float)Math.Max(1, _font.LineSpacing), SpriteEffects.None, 0f);
    }

    private IEnumerable<string> WrapGateText(string text, int size, int maxWidth)
    {
        foreach (var rawLine in text.Replace("\r", "").Split('\n'))
        {
            var words = rawLine.Split(' ');
            string line = "";
            foreach (var word in words)
            {
                string candidate = line.Length == 0 ? word : line + " " + word;
                if (line.Length > 0 && MeasureGate(candidate, size).X > maxWidth)
                {
                    yield return line;
                    line = word;
                }
                else line = candidate;
            }
            yield return line;
        }
    }
}
