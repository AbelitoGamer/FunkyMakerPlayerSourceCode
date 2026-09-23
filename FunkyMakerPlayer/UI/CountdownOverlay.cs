using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;
using SixLabors.ImageSharp.Processing;
using System;
using System.IO;

namespace FunkyMakerPlayer.UI
{
    /// <summary>
    /// Handles the "3, 2, 1, GO!" countdown overlay at the beginning of songs.
    /// Supports custom countdown packs from mods/countdowns/[name]/
    /// </summary>
    public class CountdownOverlay
    {
        private readonly GraphicsDevice _graphicsDevice;
        private readonly SpriteBatch _spriteBatch;
        private readonly SpriteFont? _font;

        // Countdown state
        private float _timer = 0f;
        private int _step = 3;  // 3,2,1,0=Go
        private bool _done = false;

        // Countdown sprites: [0]=ready, [1]=set, [2]=go
        private Texture2D?[] _textures = new Texture2D?[3];

        public float Timer => _timer;
        public int Step => _step;
        public bool IsDone => _done;

        public CountdownOverlay(GraphicsDevice graphicsDevice, SpriteBatch spriteBatch, SpriteFont? font)
        {
            _graphicsDevice = graphicsDevice;
            _spriteBatch = spriteBatch;
            _font = font;
        }

        /// <summary>
        /// Loads countdown sprites from the selected countdown pack.
        /// Priority: mods/countdowns/[selectedCountdown]/ > Assets/shared/images/ui/countdown/funkin2/
        /// </summary>
        public void LoadCountdownAssets(string selectedCountdown, string assetsRoot)
        {
            string[] countdownFiles = { "ready.png", "set.png", "go.png" };

            string modsCountdownsDir = Path.Combine(AppContext.BaseDirectory, "mods", "countdowns");
            string defaultCountdownDir = Path.Combine(assetsRoot, "shared", "images", "ui", "countdown", "funkin2");

            for (int i = 0; i < 3; i++)
            {
                string? foundPath = null;

                // If not "default", check selected countdown in mods/countdowns/[name]/
                if (selectedCountdown != "default")
                {
                    string selectedCountdownDir = Path.Combine(modsCountdownsDir, selectedCountdown);
                    string modPath = Path.Combine(selectedCountdownDir, countdownFiles[i]);

                    if (File.Exists(modPath))
                    {
                        foundPath = modPath;
                        Console.WriteLine($"[Countdown] Using '{selectedCountdown}': {modPath}");
                    }
                }

                // Fallback to default Assets if not found
                if (foundPath == null)
                {
                    string defaultPath = Path.Combine(defaultCountdownDir, countdownFiles[i]);

                    if (File.Exists(defaultPath))
                    {
                        foundPath = defaultPath;
                        if (selectedCountdown != "default")
                            Console.WriteLine($"[Countdown] '{selectedCountdown}' missing, using default Assets");
                    }
                }

                if (foundPath != null)
                {
                    _textures[i] = LoadTextureViaImageSharp(_graphicsDevice, File.ReadAllBytes(foundPath));
                }
            }
        }

        /// <summary>
        /// Resets countdown state for a new song.
        /// </summary>
        public void Reset()
        {
            _timer = 0f;
            _step = 3;
            _done = false;
        }

        /// <summary>
        /// Updates the countdown timer.
        /// Returns true when countdown is complete and song should start.
        /// </summary>
        public bool Update(float deltaTime, float bpm)
        {
            if (_done) return false;

            _timer += deltaTime;
            float beatDur = 60f / bpm;
            _step = 3 - (int)(_timer / beatDur);

            // Check if countdown is complete (4 beats)
            if (_timer >= beatDur * 4f && !_done)
            {
                _done = true;
                return true;  // Signal song should start
            }

            return false;
        }

        /// <summary>
        /// Marks countdown as done immediately (for multiplayer sync).
        /// </summary>
        public void MarkAsDone()
        {
            _done = true;
        }

        /// <summary>
        /// Gets the current song time relative to countdown.
        /// Returns negative values during countdown, reaches 0 when music starts.
        /// </summary>
        public float GetSongTime(float bpm)
        {
            float beatDur = 60f / bpm;
            return _timer - (beatDur * 4f);
        }

        /// <summary>
        /// Draws the countdown overlay (3, 2, 1, GO!).
        /// </summary>
        public void Draw(int screenWidth, int screenHeight, float bpm, bool started)
        {
            if (started) return;

            // Map step to sprite: step 2=ready, step 1=set, step 0=go
            int texIdx = _step switch { 2 => 0, 1 => 1, 0 => 2, _ => -1 };
            float beatDur = 60f / bpm;
            float beatPos = _timer % beatDur;

            // Fade in quickly, hold, then fade out at end of beat
            float t = beatPos / beatDur;
            float alpha = t < 0.15f ? t / 0.15f : (t > 0.75f ? 1f - (t - 0.75f) / 0.25f : 1f);
            alpha = MathHelper.Clamp(alpha, 0f, 1f);

            if (texIdx >= 0 && _textures[texIdx] != null)
            {
                var tex = _textures[texIdx]!;
                // Scale to fit ~40% of screen width, centered
                float scale = (screenWidth * 0.4f) / tex.Width;
                int dw = (int)(tex.Width * scale);
                int dh = (int)(tex.Height * scale);
                int dx = screenWidth / 2 - dw / 2;
                int dy = screenHeight / 2 - dh / 2;
                _spriteBatch.Draw(tex, new Microsoft.Xna.Framework.Rectangle(dx, dy, dw, dh), Microsoft.Xna.Framework.Color.White * alpha);
            }
            else if (_font != null && _step == 3)
            {
                // Step 3 has no sprite — draw "3" as text fallback
                string text = "3";
                float scale = 4f;
                var sz = _font.MeasureString(text) * scale;
                _spriteBatch.DrawString(_font, text,
                    new Vector2(screenWidth / 2f - sz.X / 2f, screenHeight / 2f - sz.Y / 2f),
                    Microsoft.Xna.Framework.Color.White * alpha, 0f, Vector2.Zero, scale, SpriteEffects.None, 0f);
            }
        }

        /// <summary>
        /// Disposes countdown textures.
        /// </summary>
        public void Dispose()
        {
            for (int i = 0; i < _textures.Length; i++)
            {
                _textures[i]?.Dispose();
                _textures[i] = null;
            }
        }

        // ── Helper: Load texture via ImageSharp ──────────────────────────────
        private static Texture2D LoadTextureViaImageSharp(GraphicsDevice gd, byte[] imageData)
        {
            using var ms = new MemoryStream(imageData);
            var decoderOptions = new SixLabors.ImageSharp.Formats.DecoderOptions
            {
                SkipMetadata = true,
            };
            using var img = SixLabors.ImageSharp.Image.Load<Rgba32>(decoderOptions, ms);

            int srcW = img.Width;
            int srcH = img.Height;
            if (srcW <= 0 || srcH <= 0) throw new InvalidOperationException("Invalid image dimensions");

            var pixels = new Microsoft.Xna.Framework.Color[srcW * srcH];
            img.ProcessPixelRows(accessor =>
            {
                for (int y = 0; y < srcH; y++)
                {
                    var row = accessor.GetRowSpan(y);
                    int rowBase = y * srcW;
                    for (int x = 0; x < srcW; x++)
                    {
                        var p = row[x];
                        pixels[rowBase + x] = Microsoft.Xna.Framework.Color.FromNonPremultiplied(p.R, p.G, p.B, p.A);
                    }
                }
            });

            var tex = new Texture2D(gd, srcW, srcH);
            tex.SetData(pixels);
            return tex;
        }
    }
}
