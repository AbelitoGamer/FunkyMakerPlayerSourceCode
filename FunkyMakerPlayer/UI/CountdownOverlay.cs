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

        // Countdown sounds, one per beat: [3]=three, [2]=two, [1]=one, [0]=go (index = step)
        private readonly Microsoft.Xna.Framework.Audio.SoundEffect?[] _sounds =
            new Microsoft.Xna.Framework.Audio.SoundEffect?[4];
        private int _lastSoundStep = 4;

        /// <summary>Accepted file names per step (any of .ogg/.wav/.mp3), FNF names included.</summary>
        private static readonly string[][] SoundNames =
        {
            new[] { "go", "introGo" },   // step 0
            new[] { "one", "intro1" },   // step 1
            new[] { "two", "intro2" },   // step 2
            new[] { "three", "intro3" }, // step 3
        };

        public float Timer => _timer;
        public int Step => _step;
        public bool IsDone => _done;

        public CountdownOverlay(GraphicsDevice graphicsDevice, SpriteBatch spriteBatch, SpriteFont? font)
        {
            _graphicsDevice = graphicsDevice;
            _spriteBatch = spriteBatch;
            _font = font;
        }

        // Name of the pack currently loaded into _textures (null = nothing loaded yet)
        private string? _loadedCountdown;

        /// <summary>
        /// Loads the selected pack if it is not the one already loaded. Called when a song
        /// starts, so a pack picked in Settings applies to the next song.
        /// </summary>
        public void EnsureLoaded(string selectedCountdown)
        {
            if (_loadedCountdown != null &&
                string.Equals(_loadedCountdown, selectedCountdown, StringComparison.Ordinal))
                return;
            LoadCountdownAssets(selectedCountdown);
        }

        /// <summary>
        /// Loads countdown sprites from the selected countdown pack.
        /// Per file: mods/countdowns/[selected]/ > mods/countdowns/Default/ > shipped Default copy.
        /// </summary>
        public void LoadCountdownAssets(string selectedCountdown)
        {
            string[] countdownFiles = { "ready.png", "set.png", "go.png" };

            Dispose();
            _loadedCountdown = selectedCountdown;
            LoadSounds(selectedCountdown);

            for (int i = 0; i < 3; i++)
            {
                string? foundPath = null;
                foreach (string candidate in CandidatePaths(selectedCountdown, countdownFiles[i]))
                {
                    if (File.Exists(candidate)) { foundPath = candidate; break; }
                }

                if (foundPath == null)
                {
                    Console.WriteLine($"[Countdown] No {countdownFiles[i]} found for '{selectedCountdown}'");
                    continue;
                }

                try
                {
                    _textures[i] = LoadTextureViaImageSharp(_graphicsDevice, File.ReadAllBytes(foundPath));
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"[Countdown] Failed to load {foundPath}: {ex.Message}");
                }
            }
        }

        /// <summary>Sounds per step: the selected pack first, then the default pack (per sound).</summary>
        private void LoadSounds(string selectedCountdown)
        {
            var dirs = new System.Collections.Generic.List<string>();
            if (!DefaultMods.IsDefaultCountdown(selectedCountdown))
                dirs.Add(Path.Combine(PlatformPaths.GetCountdownsPath(), selectedCountdown));
            dirs.Add(DefaultMods.CountdownDir);
            string? bundled = DefaultMods.Bundled("countdowns", DefaultMods.CountdownName);
            if (bundled != null) dirs.Add(bundled);

            for (int step = 0; step < 4; step++)
            {
                _sounds[step] = null;
                foreach (string dir in dirs)
                {
                    string? path = SoundFiles.Find(dir, SoundNames[step]);
                    if (path == null) continue;
                    _sounds[step] = SoundFiles.Load(path);
                    if (_sounds[step] != null) break;
                }
            }
            Console.WriteLine($"[Countdown] Sounds for '{selectedCountdown}': three={_sounds[3] != null} two={_sounds[2] != null} one={_sounds[1] != null} go={_sounds[0] != null}");
        }

        /// <summary>Where to look for one countdown image, in priority order.</summary>
        public static System.Collections.Generic.IEnumerable<string> CandidatePaths(string selectedCountdown, string fileName)
        {
            if (!DefaultMods.IsDefaultCountdown(selectedCountdown))
                yield return Path.Combine(PlatformPaths.GetCountdownsPath(), selectedCountdown, fileName);

            yield return Path.Combine(DefaultMods.CountdownDir, fileName);

            string? bundled = DefaultMods.Bundled("countdowns", DefaultMods.CountdownName, fileName);
            if (bundled != null)
                yield return bundled;
        }

        /// <summary>
        /// Resets countdown state for a new song.
        /// </summary>
        public void Reset()
        {
            _timer = 0f;
            _step = 3;
            _done = false;
            _lastSoundStep = 4;
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

            // One sound per beat: three, two, one, go
            if (_step != _lastSoundStep && _step >= 0 && _step <= 3)
            {
                _lastSoundStep = _step;
                SoundFiles.Play(_sounds[_step]);
            }

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
