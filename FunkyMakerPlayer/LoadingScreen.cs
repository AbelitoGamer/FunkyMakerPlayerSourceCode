using System;
using System.IO;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;

namespace FunkyMakerPlayer;

/// <summary>
/// Loading screen shown while a level loads.
///
/// Phases:
///   Loading  — cats pop in randomly all over the screen while the file loads.
///              Title + "Loading..." + PROGRESS PERCENTAGE shown on top.
///   Ready    — all cats visible, "Press ENTER to play" pulses.
///   Clearing — player pressed Enter: cats fade out randomly.
///   Done     — fully cleared, Game1 starts the countdown.
/// </summary>
public class LoadingScreen
{
    public enum Phase { Loading, Ready, Clearing, Done }
    public Phase CurrentPhase { get; private set; } = Phase.Loading;

    // ── Loading progress tracking ────────────────────────────────────────────
    public float LoadingProgress { get; set; } = 0f;  // 0.0 to 1.0
    public string CurrentStep { get; set; } = "Initializing...";

    private readonly string      _levelName;
    private readonly Texture2D   _pixel;
    private readonly SpriteFont? _font;
    private readonly Texture2D?  _catTex;
    private readonly bool        _isMultiplayer;  // Don't show "Press ENTER" in multiplayer

    // ── Cat grid ─────────────────────────────────────────────────────────────
    private struct CatCell
    {
        public float AppearDelay;   // seconds until this cat pops in
        public float ClearDelay;    // seconds after Enter until this cat fades
        public float Alpha;
        public float Scale;         // pop-in bounce scale
        public float Rotation;      // slight random tilt per cat
        public bool  Appeared;
    }

    private CatCell[,] _grid  = new CatCell[0, 0];
    private int   _cols, _rows;
    private int   _catSize    = 160;   // big cats — fills screen nicely
    private float _timer      = 0f;
    private float _clearTimer = 0f;

    private const float FILL_WINDOW  = 1.2f;  // spread appear delays over this many seconds
    private const float CLEAR_WINDOW = 0.5f;  // spread clear  delays over this many seconds
    private const float POP_SPEED    = 12f;   // scale-in speed
    private const float FADE_SPEED   = 8f;    // fade-out speed

    // ── UI animation ─────────────────────────────────────────────────────────
    private float _uiAlpha   = 0f;   // fades in once loading starts
    private float _rotation  = 0f;   // spinning cat in center (while loading)
    private float _dotTimer  = 0f;
    private int   _dotCount  = 0;
    private float _pulse     = 0f;   // "Press ENTER" pulse

    // ── Constructor ──────────────────────────────────────────────────────────
    public LoadingScreen(string levelName, Texture2D pixel, SpriteFont? font,
                         string? assetsRoot, GraphicsDevice gd, bool isMultiplayer = false)
    {
        _levelName = levelName;
        _pixel     = pixel;
        _font      = font;
        _isMultiplayer = isMultiplayer;

        if (assetsRoot != null)
        {
            string catPath = Path.Combine(assetsRoot, "LoadingScreen", "AnkushCat.png");
            if (File.Exists(catPath))
            {
                using var fs = File.OpenRead(catPath);
                _catTex = Texture2D.FromStream(gd, fs);
            }
        }
    }

    // ── Init grid (called once we know the render-target size) ───────────────
    private void EnsureGrid(int sw, int sh)
    {
        int cols = sw / _catSize + 2;
        int rows = sh / _catSize + 2;
        if (_cols == cols && _rows == rows) return;

        _cols = cols;
        _rows = rows;
        _grid = new CatCell[_cols, _rows];

        var rng = new Random();
        for (int x = 0; x < _cols; x++)
        for (int y = 0; y < _rows; y++)
        {
            _grid[x, y] = new CatCell
            {
                AppearDelay = (float)rng.NextDouble() * FILL_WINDOW,
                ClearDelay  = (float)rng.NextDouble() * CLEAR_WINDOW,
                Alpha       = 0f,
                Scale       = 0f,
                Rotation    = (float)(rng.NextDouble() - 0.5) * 0.3f,  // ±~17°
                Appeared    = false,
            };
        }
    }

    // ── Update ───────────────────────────────────────────────────────────────
    public void Update(float dt, bool loadComplete, bool enterPressed, int sw, int sh, bool autoStart = false)
    {
        EnsureGrid(sw, sh);

        _timer    += dt;
        _uiAlpha   = Math.Min(1f, _uiAlpha + dt * 3f);
        _rotation += dt * 2.5f;
        _pulse    += dt * 4f;

        _dotTimer += dt;
        if (_dotTimer >= 0.4f) { _dotTimer = 0f; _dotCount = (_dotCount + 1) % 4; }

        switch (CurrentPhase)
        {
            case Phase.Loading:
            {
                // Cats pop in randomly while loading
                for (int x = 0; x < _cols; x++)
                for (int y = 0; y < _rows; y++)
                {
                    ref var cell = ref _grid[x, y];
                    if (_timer >= cell.AppearDelay)
                    {
                        cell.Appeared = true;
                        cell.Scale = Math.Min(1f, cell.Scale + dt * POP_SPEED);
                        cell.Alpha = Math.Min(1f, cell.Alpha + dt * POP_SPEED);
                    }
                }
                if (loadComplete)
                    CurrentPhase = Phase.Ready;
                break;
            }

            case Phase.Ready:
            {
                // Make sure all cats are fully visible
                for (int x = 0; x < _cols; x++)
                for (int y = 0; y < _rows; y++)
                {
                    ref var cell = ref _grid[x, y];
                    cell.Appeared = true;
                    cell.Scale = Math.Min(1f, cell.Scale + dt * POP_SPEED);
                    cell.Alpha = Math.Min(1f, cell.Alpha + dt * POP_SPEED);
                }
                // In multiplayer, auto-start without waiting for ENTER
                if (enterPressed || autoStart)
                {
                    CurrentPhase = Phase.Clearing;
                    _clearTimer  = 0f;
                }
                break;
            }

            case Phase.Clearing:
            {
                _clearTimer += dt;
                bool allOut = true;
                for (int x = 0; x < _cols; x++)
                for (int y = 0; y < _rows; y++)
                {
                    ref var cell = ref _grid[x, y];
                    if (_clearTimer >= cell.ClearDelay)
                        cell.Alpha = Math.Max(0f, cell.Alpha - dt * FADE_SPEED);
                    if (cell.Alpha > 0f) allOut = false;
                }
                // Also fade UI text
                _uiAlpha = Math.Max(0f, _uiAlpha - dt * FADE_SPEED);
                if (allOut)
                    CurrentPhase = Phase.Done;
                break;
            }
        }
    }

    // ── Draw ─────────────────────────────────────────────────────────────────
    public void Draw(SpriteBatch sb, int sw, int sh)
    {
        if (CurrentPhase == Phase.Done) return;

        // Black background
        sb.Draw(_pixel, new Rectangle(0, 0, sw, sh), Color.Black);

        // Cat grid
        if (_catTex != null)
        {
            float halfTex = _catTex.Width / 2f;
            float scale   = _catSize / (float)_catTex.Width;

            for (int x = 0; x < _cols; x++)
            for (int y = 0; y < _rows; y++)
            {
                ref var cell = ref _grid[x, y];
                if (!cell.Appeared || cell.Alpha <= 0f) continue;

                // Center of this cell
                float cx = x * _catSize + _catSize / 2f;
                float cy = y * _catSize + _catSize / 2f;
                float s  = scale * cell.Scale;

                sb.Draw(_catTex,
                    new Vector2(cx, cy),
                    null,
                    Color.White * cell.Alpha,
                    cell.Rotation,
                    new Vector2(halfTex, halfTex),
                    s,
                    SpriteEffects.None,
                    0f);
            }
        }
        else
        {
            // Fallback: colored squares
            for (int x = 0; x < _cols; x++)
            for (int y = 0; y < _rows; y++)
            {
                ref var cell = ref _grid[x, y];
                if (!cell.Appeared || cell.Alpha <= 0f) continue;
                int size = (int)(_catSize * cell.Scale);
                int off  = (_catSize - size) / 2;
                sb.Draw(_pixel,
                    new Rectangle(x * _catSize + off, y * _catSize + off, size, size),
                    Color.Cyan * cell.Alpha);
            }
        }

        if (_font == null || _uiAlpha <= 0f) return;

        int scx = sw / 2;
        int scy = sh / 2;

        // Level title (shadow + main)
        string title = Safe(_levelName);
        var titleSz  = _font.MeasureString(title);
        var titlePos = new Vector2(scx - titleSz.X / 2f, scy - 60f);
        sb.DrawString(_font, title, titlePos + new Vector2(2, 2), Color.Black   * _uiAlpha);
        sb.DrawString(_font, title, titlePos,                      Color.Yellow  * _uiAlpha);

        // Status line with progress
        string status;
        if (CurrentPhase == Phase.Loading)
        {
            int percentage = (int)(LoadingProgress * 100f);
            status = $"{CurrentStep} ({percentage}%)";
        }
        else
        {
            status = _isMultiplayer ? "Waiting for opponent..." : "Press ENTER to play";
        }

        Color statusColor = CurrentPhase == Phase.Ready
            ? Color.Lerp(Color.White, Color.Yellow, 0.5f + 0.5f * MathF.Sin(_pulse))
            : Color.White * 0.75f;

        var statusSz  = _font.MeasureString(status);
        var statusPos = new Vector2(scx - statusSz.X / 2f, scy + 30f);
        sb.DrawString(_font, status, statusPos + new Vector2(2, 2), Color.Black   * _uiAlpha);
        sb.DrawString(_font, status, statusPos,                      statusColor   * _uiAlpha);

        // Progress bar (only during loading)
        if (CurrentPhase == Phase.Loading && LoadingProgress > 0f)
        {
            int barWidth = 400;
            int barHeight = 24;
            int barX = scx - barWidth / 2;
            int barY = scy + 80;

            // Background (dark)
            sb.Draw(_pixel, new Rectangle(barX, barY, barWidth, barHeight), 
                Color.Black * 0.7f * _uiAlpha);

            // Border
            sb.Draw(_pixel, new Rectangle(barX, barY, barWidth, 2), 
                Color.White * 0.5f * _uiAlpha);  // Top
            sb.Draw(_pixel, new Rectangle(barX, barY + barHeight - 2, barWidth, 2), 
                Color.White * 0.5f * _uiAlpha);  // Bottom
            sb.Draw(_pixel, new Rectangle(barX, barY, 2, barHeight), 
                Color.White * 0.5f * _uiAlpha);  // Left
            sb.Draw(_pixel, new Rectangle(barX + barWidth - 2, barY, 2, barHeight), 
                Color.White * 0.5f * _uiAlpha);  // Right

            // Fill (progress)
            int fillWidth = (int)((barWidth - 8) * LoadingProgress);
            if (fillWidth > 0)
            {
                // Gradient effect: cyan to yellow
                Color fillColor = Color.Lerp(new Color(0, 180, 255), Color.Yellow, LoadingProgress);
                sb.Draw(_pixel, new Rectangle(barX + 4, barY + 4, fillWidth, barHeight - 8), 
                    fillColor * _uiAlpha);
            }
        }
    }

    private static string Safe(string? s)
    {
        if (s == null) return "";
        var sb = new System.Text.StringBuilder(s.Length);
        foreach (char c in s)
            sb.Append(c >= 32 && c <= 126 ? c : '?');
        return sb.ToString();
    }
}
