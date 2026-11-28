using System;
using FunkyMakerPlayer.UI;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using Microsoft.Xna.Framework.Input;

namespace FunkyMakerPlayer
{
    /// <summary>
    /// Android "FOLDER ACCESS" guide (title screen secondary button). Rewritten 2026-11-28 (fork):
    /// it was seven swipe pages of tiny centred text with no way back except the system gesture.
    /// Now one screen sized with <see cref="UiScale.FromScreen"/>: a Back button, three short steps
    /// and a big CHOOSE FOLDER button that opens the Android folder picker
    /// (<see cref="LaunchFolderPicker"/>, handled by Game1). Works by tap (UI/PointerInput) and keys
    /// (Escape / back gesture closes, Enter picks the folder).
    /// </summary>
    public class ModsHelpScreen
    {
        private readonly UiDraw _ui;
        private Rectangle _backBounds, _pickBounds;
        private bool _backHover, _pickHover;
        private MouseState _prevMouse;
        private KeyboardState _prevKb;
        private float _openTime;

        public bool IsVisible { get; set; } = false;
        public bool LaunchFolderPicker { get; private set; } = false;

        private static readonly (string Title, string Body)[] Steps =
        {
            ("Put your mods in a folder",
             "Copy your mods to the phone, inside a folder that has a \"mods\" folder in it.\nExample: Documents/MyMods/mods"),
            ("Choose that folder",
             "Tap CHOOSE FOLDER and pick the folder that has \"mods\" inside (MyMods in the example)."),
            ("Allow access",
             "Tap \"Use this folder\" and then \"Allow\". The game copies everything into its own mods folder."),
        };

        public ModsHelpScreen(FontRenderer fontRenderer, Texture2D pixel)
        {
            _ui = new UiDraw(pixel, null, fontRenderer);
        }

        public void Show()
        {
            IsVisible = true;
            LaunchFolderPicker = false;
            _openTime = 0f;
            _prevMouse = PointerInput.GetState();
            _prevKb = LayoutKeyboard.GetState();
        }

        public void Hide() => IsVisible = false;

        public void Update(GameTime gameTime)
        {
            LaunchFolderPicker = false;
            if (!IsVisible) return;
            _openTime += (float)gameTime.ElapsedGameTime.TotalSeconds;

            var ms = PointerInput.GetState();
            var kb = LayoutKeyboard.GetState();
            if (!Game1.CanAcceptInput)
            {
                _prevMouse = Game1.NeutralMouseState(ms);
                _prevKb = kb;
                return;
            }

            _backHover = _backBounds.Contains(ms.Position);
            _pickHover = _pickBounds.Contains(ms.Position);
            bool clicked = _openTime > 0.15f &&
                           ms.LeftButton == ButtonState.Pressed && _prevMouse.LeftButton == ButtonState.Released;
            bool Pressed(Keys k) => kb.IsKeyDown(k) && !_prevKb.IsKeyDown(k);
            _prevMouse = ms;
            _prevKb = kb;

            if (Pressed(Keys.Escape) || Pressed(Keys.Back) || (clicked && _backHover))
            {
                UiSfx.Play(UiSound.Back);
                IsVisible = false;
            }
            else if (Pressed(Keys.Enter) || (clicked && _pickHover))
            {
                UiSfx.Play(UiSound.Confirm);
                LaunchFolderPicker = true;
                IsVisible = false;
            }
        }

        public void Draw(SpriteBatch sb, int sw, int sh)
        {
            if (!IsVisible) return;
            float s = UiScale.FromScreen(sw, sh);
            Color accent = new Color(255, 68, 136);
            Color gold = new Color(255, 215, 0);
            Color green = new Color(70, 220, 140);

            _ui.Rect(sb, 0, 0, sw, sh, new Color(20, 18, 28));

            int margin = UiScale.Px(28, s);
            int top = UiScale.Px(20, s);

            // Back button (top left)
            int backH = UiScale.Px(44, s, 40);
            int backFont = UiScale.FontPx(17, s);
            string backLabel = PointerInput.BackLabel;
            int backW = (int)_ui.Measure(backLabel, backFont).X + UiScale.Px(36, s);
            _backBounds = new Rectangle(margin, top, backW, backH);
            _ui.Rect(sb, _backBounds, _backHover ? new Color(56, 48, 76) : new Color(34, 30, 48));
            Border(sb, _backBounds, _backHover ? accent : Color.White * 0.18f);
            _ui.CenterText(sb, backLabel, _backBounds, Color.White * (_backHover ? 1f : 0.88f), backFont);

            // Title, centred on the Back button's row
            int titleFont = UiScale.FontPx(34, s);
            const string title = "FOLDER ACCESS";
            var tsz = _ui.Measure(title, titleFont);
            float titleY = top + (backH - tsz.Y) / 2f;
            _ui.Text(sb, title, new Vector2((sw - tsz.X) / 2f, titleY), gold, titleFont);

            int subFont = UiScale.FontPx(17, s);
            const string sub = "Import a whole folder of mods at once";
            var subSz = _ui.Measure(sub, subFont);
            float subY = top + backH + UiScale.Px(10, s);
            _ui.Text(sb, sub, new Vector2((sw - subSz.X) / 2f, subY), Color.White * 0.6f, subFont);

            // Bottom: CHOOSE FOLDER button and the IMPORT FILE tip
            int tipFont = UiScale.FontPx(15, s);
            const string tip = "Only one level file? Use IMPORT FILE on the title screen instead.";
            var tipSz = _ui.Measure(tip, tipFont);
            float tipY = sh - UiScale.Px(18, s) - tipSz.Y;
            int pickH = UiScale.Px(62, s, 48);
            int pickW = Math.Min(UiScale.Px(420, s), sw - margin * 2);
            _pickBounds = new Rectangle((sw - pickW) / 2, (int)(tipY - UiScale.Px(14, s) - pickH), pickW, pickH);
            int glow = UiScale.Px(3, s, 2);
            _ui.Rect(sb, _pickBounds.X - glow, _pickBounds.Y - glow, _pickBounds.Width + glow * 2, _pickBounds.Height + glow * 2,
                green * (_pickHover ? 0.5f : 0.28f));
            _ui.Rect(sb, _pickBounds, _pickHover ? new Color(40, 150, 95) : new Color(30, 120, 78));
            _ui.CenterText(sb, "CHOOSE FOLDER", _pickBounds, Color.White, UiScale.FontPx(24, s));
            _ui.Text(sb, tip, new Vector2((sw - tipSz.X) / 2f, tipY), Color.White * 0.5f, tipFont);

            // Steps: side by side on a wide (landscape) screen, stacked otherwise
            float stepsTop = subY + subSz.Y + UiScale.Px(20, s);
            float stepsBottom = _pickBounds.Y - UiScale.Px(20, s);
            int gap = UiScale.Px(16, s);
            bool row = sw > sh;
            int n = Steps.Length;
            int areaW = Math.Min(sw - margin * 2, UiScale.Px(1200, s));
            int areaX = (sw - areaW) / 2;
            int cardW = row ? (areaW - gap * (n - 1)) / n : areaW;
            // Cards no taller than their text needs: the block is centred instead of stretched
            int cardH = row
                ? (int)Math.Min(stepsBottom - stepsTop, UiScale.Px(200, s))
                : (int)Math.Min((stepsBottom - stepsTop - gap * (n - 1)) / n, UiScale.Px(170, s));
            float blockH = row ? cardH : n * cardH + gap * (n - 1);
            stepsTop += Math.Max(0f, (stepsBottom - stepsTop - blockH) / 2f);
            int pad = UiScale.Px(18, s);
            int numFont = UiScale.FontPx(26, s);
            int headFont = UiScale.FontPx(20, s);
            int bodyFont = UiScale.FontPx(17, s);

            for (int i = 0; i < n; i++)
            {
                var r = row
                    ? new Rectangle(areaX + i * (cardW + gap), (int)stepsTop, cardW, cardH)
                    : new Rectangle(areaX, (int)stepsTop + i * (cardH + gap), cardW, cardH);
                _ui.Rect(sb, r, new Color(28, 26, 42));
                Border(sb, r, Color.White * 0.10f);
                _ui.Rect(sb, r.X, r.Y, r.Width, UiScale.Px(3, s, 2), accent * 0.8f);

                // Number badge + heading
                string num = (i + 1).ToString();
                int badge = UiScale.Px(40, s, 30);
                var badgeR = new Rectangle(r.X + pad, r.Y + pad, badge, badge);
                _ui.Rect(sb, badgeR, accent * 0.22f);
                Border(sb, badgeR, accent * 0.8f);
                _ui.CenterText(sb, num, badgeR, Color.White, numFont);

                float textX = badgeR.Right + UiScale.Px(12, s);
                float headMax = r.Right - pad - textX;
                int hf = headFont;
                while (hf > 11 && _ui.Measure(Steps[i].Title, hf).X > headMax) hf--;
                var hsz = _ui.Measure(Steps[i].Title, hf);
                _ui.Text(sb, Steps[i].Title, new Vector2(textX, badgeR.Y + (badge - hsz.Y) / 2f), Color.White, hf);

                // Body, wrapped; shrinks until it fits the card
                float bodyTop = badgeR.Bottom + UiScale.Px(12, s);
                float maxW = r.Width - pad * 2;
                float maxH = r.Bottom - pad - bodyTop;
                int bf = bodyFont;
                var lines = Wrap(Steps[i].Body, bf, maxW);
                float lineH = _ui.Measure("Ag", bf).Y + UiScale.Px(4, s);
                while (bf > 11 && lines.Count * lineH > maxH)
                {
                    bf--;
                    lines = Wrap(Steps[i].Body, bf, maxW);
                    lineH = _ui.Measure("Ag", bf).Y + UiScale.Px(4, s);
                }
                float y = bodyTop;
                foreach (var l in lines)
                {
                    _ui.Text(sb, l, new Vector2(r.X + pad, y), Color.White * 0.78f, bf);
                    y += lineH;
                }
            }
        }

        private System.Collections.Generic.List<string> Wrap(string text, int size, float maxW)
        {
            var result = new System.Collections.Generic.List<string>();
            foreach (var para in text.Split('\n'))
            {
                string line = "";
                foreach (var word in para.Split(' '))
                {
                    string test = line.Length == 0 ? word : line + " " + word;
                    if (_ui.Measure(test, size).X > maxW && line.Length > 0)
                    {
                        result.Add(line);
                        line = word;
                    }
                    else line = test;
                }
                result.Add(line);
            }
            return result;
        }

        private void Border(SpriteBatch sb, Rectangle r, Color c)
        {
            _ui.Rect(sb, r.X, r.Y, r.Width, 1, c);
            _ui.Rect(sb, r.X, r.Bottom - 1, r.Width, 1, c);
            _ui.Rect(sb, r.X, r.Y, 1, r.Height, c);
            _ui.Rect(sb, r.Right - 1, r.Y, 1, r.Height, c);
        }
    }
}
