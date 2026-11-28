using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Audio;
using Microsoft.Xna.Framework.Graphics;
using Microsoft.Xna.Framework.Input;
using SixLabors.ImageSharp.Processing;
using FunkyMakerPlayer.Managers;  // ← NUEVO: Import de managers
using FunkyMakerPlayer.UI;  // ← NUEVO: Import de UI menus
using FunkyMakerPlayer.Multiplayer;  // ← NUEVO: Import de multiplayer
using MonoGame.Extended;  // ← MonoGame.Extended for OrthographicCamera

namespace FunkyMakerPlayer;

/// <summary>Window and presentation: FPS cap, fullscreen, render target, letterbox and blit (moved out of Game1.cs).</summary>
public partial class Game1
{
    /// <summary>Applies <see cref="FpsCap"/> (the wait itself happens in <see cref="EndDraw"/>).</summary>
    public void ApplyFpsCap()
    {
        IsFixedTimeStep = false;
        _nextFrameTick = 0;
        if (FpsCap > 0 && !_timerResolutionRaised && OperatingSystem.IsWindows())
        {
            try { timeBeginPeriod(1); } catch { /* keep the default resolution */ }
            _timerResolutionRaised = true;
        }
    }

    protected override void EndDraw()
    {
        base.EndDraw();
        WaitForFrameSlot();
    }

    private void WaitForFrameSlot()
    {
        if (FpsCap <= 0) { _nextFrameTick = 0; return; }

        long freq = System.Diagnostics.Stopwatch.Frequency;
        long period = freq / FpsCap;
        long now = _frameClock.ElapsedTicks;

        // First frame, or too far behind (slow frame, window drag): restart the schedule, no wait
        if (_nextFrameTick == 0 || now - _nextFrameTick > period)
        {
            _nextFrameTick = now + period;
            return;
        }

        while (true)
        {
            long remaining = _nextFrameTick - _frameClock.ElapsedTicks;
            if (remaining <= 0) break;
            double ms = remaining * 1000.0 / freq;
            if (ms > 2.0) System.Threading.Thread.Sleep((int)(ms - 1.5));
            else System.Threading.Thread.SpinWait(64);
        }
        _nextFrameTick += period;
    }

    /// <summary>
    /// Fullscreen as a borderless window covering the display. SDL's own fullscreen (even
    /// "desktop" fullscreen) minimizes on focus loss and behaves like exclusive fullscreen;
    /// a plain borderless window alt-tabs and captures like any window.
    /// </summary>
    public void SetFullscreen(bool on)
    {
#if !ANDROID
        _isFullscreen = on;
        _graphics.HardwareModeSwitch = false;
        _graphics.IsFullScreen = false;
        if (on)
        {
            _windowedPosition = Window.Position;
            var dm = GraphicsAdapter.DefaultAdapter.CurrentDisplayMode;
            Window.IsBorderless = true;
            _graphics.PreferredBackBufferWidth = dm.Width;
            // One row taller than the display (that row is off-screen): a GL window that exactly
            // covers the monitor gets promoted to "real" fullscreen by the driver / Windows
            // fullscreen optimizations, and window capture (OBS etc.) then shows a frozen frame.
            _graphics.PreferredBackBufferHeight = dm.Height + 1;
            _graphics.ApplyChanges();
            Window.Position = Point.Zero;
        }
        else
        {
            Window.IsBorderless = false;
            _graphics.PreferredBackBufferWidth = 1280;
            _graphics.PreferredBackBufferHeight = 720;
            _graphics.ApplyChanges();
            if (_windowedPosition.X >= 0 && _windowedPosition.Y >= 0)
                Window.Position = _windowedPosition;
            else
            {
                var dm = GraphicsAdapter.DefaultAdapter.CurrentDisplayMode;
                Window.Position = new Point(Math.Max(0, (dm.Width - 1280) / 2), Math.Max(0, (dm.Height - 720) / 2));
            }
        }
#endif
    }

    private (Rectangle bounds, float scale) ComputeLetterbox(int targetW, int targetH)
    {
        float scale = Math.Min((float)targetW / _rtWidth, (float)targetH / _rtHeight);
        int dstW = (int)(_rtWidth * scale);
        int dstH = (int)(_rtHeight * scale);
        int dstX = (targetW - dstW) / 2;
        int dstY = (targetH - dstH) / 2;
        return (new Rectangle(dstX, dstY, dstW, dstH), scale);
    }

    /// <summary>
    /// Blits the render target to the actual window with letterboxing (black bars).
    /// Call this at the end of every Draw path, after all rendering to _renderTarget is done.
    /// </summary>
    private void BlitToScreen()
    {
        if (_renderTarget == null) return;
        var (dst, _) = ComputeLetterbox(
            GraphicsDevice.PresentationParameters.BackBufferWidth,
            GraphicsDevice.PresentationParameters.BackBufferHeight);
        PresentRenderTarget(dst);
    }

    /// <summary>
    /// (fork) (Re)creates _renderTarget so its physical size matches the letterboxed window area
    /// (logical _rtWidth×_rtHeight × _rtScale). With NativeRenderResolution off, _rtScale = 1 (720p).
    /// Cheap no-op when nothing changed; called every frame so window resizes / fullscreen follow.
    /// </summary>
    private void EnsureRenderTarget()
    {
        float scale = 1f;
        // 32-bit Android (Mono armeabi-v7a) has a tight 32-bit virtual address space; keep the gameplay
        // render target at logical 720p (scale = 1) so full-HD+ screens don't exhaust VRAM / heap.
        if (NativeRenderResolution && !(OperatingSystem.IsAndroid() && !Environment.Is64BitProcess))
        {
            var (dst, s) = ComputeLetterbox(
                GraphicsDevice.PresentationParameters.BackBufferWidth,
                GraphicsDevice.PresentationParameters.BackBufferHeight);
            if (dst.Width > 0 && dst.Height > 0 && s > 0f)
                scale = s;
            if (_maxTexForUpscale <= 0) _maxTexForUpscale = GetGpuMaxTextureSize();
            float maxScale = _maxTexForUpscale / (float)Math.Max(_rtWidth, _rtHeight);
            scale = Math.Clamp(scale, 0.25f, Math.Max(1f, maxScale));
        }

        int pw = Math.Max(1, (int)MathF.Round(_rtWidth  * scale));
        int ph = Math.Max(1, (int)MathF.Round(_rtHeight * scale));
        _rtScale = pw / (float)_rtWidth;
        FontRenderer.RenderTargetScale = _rtScale; // text into the target is rasterized at physical size
        if (_renderTarget != null && !_renderTarget.IsDisposed && _renderTarget.Width == pw && _renderTarget.Height == ph)
            return;

        _renderTarget?.Dispose();
        _renderTarget = new RenderTarget2D(GraphicsDevice, pw, ph,
            false, SurfaceFormat.Color, DepthFormat.None,
            0, RenderTargetUsage.DiscardContents);
    }

    /// <summary>
    /// Draws _renderTarget to the backbuffer at <paramref name="dst"/> using "sharp bilinear"
    /// scaling (fork): first a nearest-neighbour upscale by the integer factor just above the
    /// window scale, then a bilinear pass for the small remaining (down)scale. Pixel art
    /// (bilinear=false objects) stays crisp with even pixels at any window size, instead of
    /// being blurred by a plain bilinear stretch of the 1280x720 frame. Clears the backbuffer.
    /// </summary>
    private void PresentRenderTarget(Rectangle dst)
    {
        if (_renderTarget == null) return;
        // Physical size: at native resolution this is ~1:1 with dst, so k = 1 and no upscale pass runs
        int rtPw = _renderTarget.Width, rtPh = _renderTarget.Height;
        float scale = Math.Max(dst.Width / (float)rtPw, dst.Height / (float)rtPh);
        int k = (OperatingSystem.IsAndroid() && !Environment.Is64BitProcess)
            ? 1
            : (int)MathF.Ceiling(scale - 0.001f);
        if (_maxTexForUpscale <= 0) _maxTexForUpscale = GetGpuMaxTextureSize();
        int maxTex = _maxTexForUpscale;
        while (k > 1 && Math.Max(rtPw, rtPh) * k > maxTex) k--;

        Texture2D source = _renderTarget;
        if (k > 1)
        {
            int uw = rtPw * k, uh = rtPh * k;
            if (_upscaleTarget == null || _upscaleTarget.Width != uw || _upscaleTarget.Height != uh)
            {
                _upscaleTarget?.Dispose();
                _upscaleTarget = new RenderTarget2D(GraphicsDevice, uw, uh,
                    false, SurfaceFormat.Color, DepthFormat.None, 0, RenderTargetUsage.DiscardContents);
            }
            // Done before switching to the backbuffer: switching away from it would discard it
            GraphicsDevice.SetRenderTarget(_upscaleTarget);
            _spriteBatch.Begin(SpriteSortMode.Deferred, BlendState.Opaque, SamplerState.PointClamp);
            _spriteBatch.Draw(_renderTarget, new Rectangle(0, 0, uw, uh), Color.White);
            _spriteBatch.End();
            source = _upscaleTarget;
        }

        GraphicsDevice.SetRenderTarget(null);
        GraphicsDevice.Clear(Color.Black);
        _spriteBatch.Begin(samplerState: SamplerState.LinearClamp);
        _spriteBatch.Draw(source, dst, Color.White);
        _spriteBatch.End();
    }
}
