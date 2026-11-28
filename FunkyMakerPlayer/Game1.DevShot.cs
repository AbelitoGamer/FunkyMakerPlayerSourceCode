#if !ANDROID
using System;
using System.IO;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;

namespace FunkyMakerPlayer;

/// <summary>
/// Developer harness (fork, desktop only; does nothing unless its environment variables are set).
/// Used to check layouts — mainly the phone-sized UI — without a phone:
/// <list type="bullet">
/// <item><c>FMP_WINDOW=1200x540</c>: window size at startup (e.g. a 2400×1080 phone at half size).</item>
/// <item><c>FMP_TOUCH_SIM=1.5</c>: pretend to be a touch device with that <see cref="UI.UiScale.Touch"/>
/// factor (menus get bigger exactly as on a phone; <see cref="UI.PointerInput.IsTouch"/> is true).</item>
/// <item><c>FMP_OPEN=settings[:tab] | freeplay | editors | credits | replays | online | modsfolder | level | pause</c>:
/// screen to open after start (<c>level</c> / <c>pause</c> load <c>FMP_LEVEL</c>; <c>pause</c> pauses 6 s in).</item>| freeplay | editors | credits</c>: screen to open after start.</item>
/// <item><c>FMP_SHOT=path.png</c> (+ <c>FMP_SHOT_DELAY</c> seconds, default 4): save the window to a
/// PNG and quit.</item>
/// </list>
/// </summary>
public partial class Game1
{
    private static readonly string? DevShotPath = Environment.GetEnvironmentVariable("FMP_SHOT");
    private static readonly string? DevOpen = Environment.GetEnvironmentVariable("FMP_OPEN");
    private float _devTime;
    private bool _devOpened, _devShotDone;

    /// <summary>Startup part (constructor): window size and touch simulation.</summary>
    private void ApplyDevHarnessStartup()
    {
        if (Environment.GetEnvironmentVariable("FMP_TOUCH_SIM") is { } sim &&
            float.TryParse(sim, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out float boost))
            UI.UiScale.SimulateTouch(boost);

        if (Environment.GetEnvironmentVariable("FMP_WINDOW") is { } win)
        {
            var parts = win.ToLowerInvariant().Split('x');
            if (parts.Length == 2 && int.TryParse(parts[0], out int w) && int.TryParse(parts[1], out int h) && w > 0 && h > 0)
            {
                _graphics.PreferredBackBufferWidth = w;
                _graphics.PreferredBackBufferHeight = h;
                _devForcedWindow = new Point(w, h);
            }
        }
    }

    private Point? _devForcedWindow;
    private float _devPauseAt = -1f;

    /// <summary>Every update: opens the requested screen once.</summary>
    private void UpdateDevHarness(float dt)
    {
        if (DevOpen == null && DevShotPath == null) return;
        _devTime += dt;
        if (_devForcedWindow is { } fw && !_isFullscreen &&
            (_graphics.PreferredBackBufferWidth != fw.X || _graphics.PreferredBackBufferHeight != fw.Y))
        {
            _graphics.PreferredBackBufferWidth = fw.X;
            _graphics.PreferredBackBufferHeight = fw.Y;
            _graphics.ApplyChanges();
        }
        if (_devPauseAt > 0f && _devTime >= _devPauseAt && _level != null && _started)
        {
            _devPauseAt = -1f;
            _inPauseMenu = true;
            PauseSong();
        }
        if (_devOpened || DevOpen == null || _devTime < 1.5f || _freeplay == null) return;
        _devOpened = true;
        var parts = DevOpen.Split(':');
        switch (parts[0].ToLowerInvariant())
        {
            case "level":
            case "pause":
                if (Environment.GetEnvironmentVariable("FMP_LEVEL") is { } lvl && File.Exists(lvl))
                {
                    _inFreeplay = false;
                    StartLoadingLevel(lvl);
                    _devPauseAt = parts[0] == "pause" ? _devTime + 6f : -1f;
                }
                break;
            case "settings":
                if (_settingsManager != null)
                {
                    _settingsManager.IsActive = true;
                    if (parts.Length > 1 && int.TryParse(parts[1], out int tab)) _settingsManager.CurrentTab = tab;
                }
                break;
            default:
                _freeplay.DevOpen(parts[0].ToLowerInvariant());
                break;
        }
    }

    /// <summary>After each Draw: saves the screenshot and quits once the delay has passed.</summary>
    private void DevShotAfterDraw()
    {
        if (DevShotPath == null || _devShotDone) return;
        float delay = 4f;
        if (Environment.GetEnvironmentVariable("FMP_SHOT_DELAY") is { } d &&
            float.TryParse(d, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out float dd))
            delay = dd;
        if (_devTime < delay) return;
        _devShotDone = true;
        try
        {
            int w = GraphicsDevice.PresentationParameters.BackBufferWidth;
            int h = GraphicsDevice.PresentationParameters.BackBufferHeight;
            var data = new Color[w * h];
            GraphicsDevice.GetBackBufferData(data);
            for (int i = 0; i < data.Length; i++) data[i].A = 255;
            using var tex = new Texture2D(GraphicsDevice, w, h);
            tex.SetData(data);
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(DevShotPath))!);
            using var fs = File.Create(DevShotPath);
            tex.SaveAsPng(fs, w, h);
            Console.WriteLine($"[DevShot] Saved {DevShotPath} ({w}x{h})");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[DevShot] Failed: {ex.Message}");
        }
        Exit();
    }
}
#endif
