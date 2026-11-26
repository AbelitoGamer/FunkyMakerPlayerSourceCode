using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using FunkyMakerPlayer.Osu;

namespace FunkyMakerPlayer;

/// <summary>
/// osu! support in gameplay (fork; the format itself lives in <c>Osu/</c>). An osu! level is an
/// ordinary <see cref="FnmmLevel"/> with <see cref="FnmmLevel.IsOsu"/> and
/// <see cref="FnmmLevel.OsuExtras"/>; this file adds what it brings on top, each with a setting in
/// <see cref="OsuSettings"/>:
/// <list type="bullet">
/// <item>Background image, video (ffmpeg) and storyboard behind the notes, darkened by Background Dim;
/// the storyboard's Overlay layer above the notes.</item>
/// <item>Scroll speed changes: the level's <see cref="ScrollTimeline"/> on every note renderer.</item>
/// <item>Hitsounds on key press (and on autoplay hits).</item>
/// <item>Beatmap skin: the set folder's own mania note images instead of the chosen note style.</item>
/// </list>
/// Also: importing .osz / .osk files and finding .osu levels for replays.
/// </summary>
public partial class Game1
{
    private Texture2D? _osuBackground;
    private OsuVideo? _osuVideo;
    private OsuStoryboard? _osuStoryboard;
    private FnmmLevel? _osuExtrasLevel;          // level the background / storyboard were loaded for
    private NoteStyle? _beatmapSkinStyle;
    private bool _osuVideoUnavailable;
    private string? _beatmapSkinFolder;

    private OsuLevelExtras? OsuExtras => _level?.IsOsu == true ? _level.OsuExtras : null;

    /// <summary>Note style for the level being set up: the beatmap's own skin when allowed, else the chosen one.</summary>
    private NoteStyle? LevelNoteStyle()
    {
        var extras = OsuExtras;
        if (extras == null || !extras.HasBeatmapSkin || !OsuSettings.PreferBeatmapSkins) return _noteStyle;
        if (_beatmapSkinStyle == null || _beatmapSkinFolder != extras.Folder ||
            _beatmapSkinStyle.OsuBuiltForDownscroll != NoteRenderer.Downscroll)
        {
            _beatmapSkinStyle = OsuManiaSkin.Load(extras.Folder, GraphicsDevice, beatmapSkin: true);
            _beatmapSkinFolder = extras.Folder;
            if (_beatmapSkinStyle != null) Console.WriteLine("[Osu] Using the beatmap's own note skin");
        }
        return _beatmapSkinStyle ?? _noteStyle;
    }

    /// <summary>Called at the end of SetupLevel (also on restart): background, storyboard, video, SV, hitsounds.</summary>
    private void SetupOsuExtras()
    {
        _appliedRendererStyle = LevelNoteStyle(); // the renderers were just built with it
        var extras = OsuExtras;
        if (extras == null)
        {
            DisposeOsuExtras();
            return;
        }

        // Background / storyboard images: once per level (a restart keeps them).
        if (!ReferenceEquals(_osuExtrasLevel, _level))
        {
            DisposeOsuExtras();
            _osuExtrasLevel = _level;
            if (_level!.BackgroundImage != null)
            {
                try { _osuBackground = LoadTextureViaImageSharp(GraphicsDevice, _level.BackgroundImage); }
                catch (Exception ex) { Console.WriteLine($"[Osu] Background texture failed: {ex.Message}"); }
            }
            if (OsuSettings.Storyboard && extras.Storyboard != null)
            {
                if (_loadingScreen != null) { _loadingScreen.CurrentStep = "Loading storyboard..."; RenderLoadingScreen(); }
                try
                {
                    extras.Storyboard.LoadResources(GraphicsDevice, extras.Folder);
                    _osuStoryboard = extras.Storyboard;
                }
                catch (Exception ex) { Console.WriteLine($"[Osu] Storyboard images failed: {ex.Message}"); }
            }
            if (OsuSettings.Video)
                _osuVideo = OsuVideo.TryCreate(extras.VideoPath, extras.VideoStartSec, OsuSettings.VideoFullQuality);
        }

        // Scroll speed changes on every strumline.
        foreach (var r in _noteRenderers)
            r.Scroll = OsuSettings.ScrollSpeedChanges ? extras.Scroll : null;

        // Hitsounds: on key press (manual play and replays), on hits under autoplay.
        if (extras.Hitsounds is { } hs && _noteRenderers.Count > 0)
        {
            var player = _noteRenderers[0];
            var onKey = player.OnKeyPress;
            player.OnKeyPress = lane => { onKey?.Invoke(lane); hs.PlayForLane(lane, _songTime); };
            var onHit = player.OnNoteHit;
            player.OnNoteHit = (lane, holdEnd) =>
            {
                onHit?.Invoke(lane, holdEnd);
                if (NoteRenderer.Botplay && player.ReplayInput == null) hs.PlayForLane(lane, _songTime);
            };
        }
    }

    private void DisposeOsuExtras()
    {
        _osuBackground?.Dispose();
        _osuBackground = null;
        _osuVideo?.Dispose();
        _osuVideo = null;
        _osuStoryboard?.Dispose();
        _osuStoryboard = null;
        _osuExtrasLevel = null;
        _osuVideoUnavailable = false;
    }

    /// <summary>Per frame in gameplay: video frames and storyboard samples.</summary>
    private void UpdateOsuExtras()
    {
        var extras = OsuExtras;
        if (extras == null) return;

        // Settings changed mid-song (pause → Settings) apply at once.
        if (OsuSettings.Storyboard && _osuStoryboard == null && extras.Storyboard != null && ReferenceEquals(_osuExtrasLevel, _level))
        {
            try { extras.Storyboard.LoadResources(GraphicsDevice, extras.Folder); _osuStoryboard = extras.Storyboard; }
            catch (Exception ex) { Console.WriteLine($"[Osu] Storyboard images failed: {ex.Message}"); extras.Storyboard = null; }
        }
        else if (!OsuSettings.Storyboard && _osuStoryboard != null)
        {
            _osuStoryboard.Dispose();
            _osuStoryboard = null;
        }
        // Video Quality changed mid-song: reopen at the new quality (it resumes at the song time).
        if (_osuVideo != null && _osuVideo.FullQuality != OsuSettings.VideoFullQuality)
        {
            _osuVideo.Dispose();
            _osuVideo = null;
            _osuVideoUnavailable = false;
        }
        if (OsuSettings.Video && _osuVideo == null && extras.VideoPath != null && !_osuVideoUnavailable)
        {
            _osuVideo = OsuVideo.TryCreate(extras.VideoPath, extras.VideoStartSec, OsuSettings.VideoFullQuality);
            _osuVideoUnavailable = _osuVideo == null; // no decoder: don't retry every frame
        }
        else if (!OsuSettings.Video && _osuVideo != null)
        {
            _osuVideo.Dispose();
            _osuVideo = null;
        }
        var scroll = OsuSettings.ScrollSpeedChanges ? extras.Scroll : null;
        foreach (var r in _noteRenderers)
            if (r.Scroll != scroll) r.Scroll = scroll;

        _osuVideo?.Update(_songTime, GraphicsDevice, clockRunning: !_paused);
        if (_started && !_paused)
            _osuStoryboard?.UpdateSamples(_songTime, OsuPassing, Math.Clamp(MasterVolume * OsuSettings.HitsoundVolume, 0f, 1f));
    }

    /// <summary>Pass / Fail storyboard layers follow the health bar.</summary>
    private bool OsuPassing => (_healthBar?.Health ?? 1f) >= 0.5f;

    /// <summary>
    /// Behind the scene: background (unless the storyboard shows it itself), video, storyboard layers
    /// Background / Pass / Fail / Foreground, then the Background Dim.
    /// </summary>
    private void DrawOsuBackground(int sw, int sh)
    {
        var extras = OsuExtras;
        if (extras == null) return;
        bool storyboardBackground = _osuStoryboard != null && _osuStoryboard.ReplacesBackground(extras.Beatmap.BackgroundFile);

        _spriteBatch.Begin(SpriteSortMode.Deferred, BlendState.AlphaBlend, SamplerState.LinearClamp,
            null, null, null, RtScaleMatrix);
        if (_osuBackground != null && !storyboardBackground)
            DrawCovered(_osuBackground, sw, sh, Color.White);
        if (_osuVideo?.Texture is { } video && _osuVideo.Alpha > 0f)
            DrawCovered(video, sw, sh, Color.White * _osuVideo.Alpha, fit: true);
        _spriteBatch.End();

        _osuStoryboard?.Draw(_spriteBatch, RtScaleMatrix, _songTime, overlay: false, OsuPassing, 1f);

        if (OsuSettings.BackgroundDim > 0f && _pixel != null)
        {
            _spriteBatch.Begin(SpriteSortMode.Deferred, BlendState.AlphaBlend, SamplerState.LinearClamp,
                null, null, null, RtScaleMatrix);
            _spriteBatch.Draw(_pixel, new Rectangle(0, 0, sw, sh), Color.Black * OsuSettings.BackgroundDim);
            _spriteBatch.End();
        }
    }

    /// <summary>The storyboard's Overlay layer, drawn over the notes (dimmed too).</summary>
    private void DrawOsuOverlay()
    {
        if (_osuStoryboard == null || OsuExtras == null) return;
        _osuStoryboard.Draw(_spriteBatch, RtScaleMatrix, _songTime, overlay: true, OsuPassing, 1f - OsuSettings.BackgroundDim);
    }

    /// <summary>Cover (fill, cropping) or fit (letterbox) a texture into the frame.</summary>
    private void DrawCovered(Texture2D tex, int sw, int sh, Color color, bool fit = false)
    {
        float sx = sw / (float)tex.Width, sy = sh / (float)tex.Height;
        float scale = fit ? Math.Min(sx, sy) : Math.Max(sx, sy);
        int w = (int)(tex.Width * scale), h = (int)(tex.Height * scale);
        _spriteBatch.Draw(tex, new Rectangle((sw - w) / 2, (sh - h) / 2, w, h), color);
    }

    /// <summary>
    /// Imports dropped / picked osu! files. Returns true when <paramref name="path"/> was an osu! file
    /// (handled here, with a toast in Freeplay).
    /// </summary>
    private bool TryImportOsuFile(string path)
    {
        if (!OsuLibrary.IsBeatmapArchive(path) && !OsuLibrary.IsSkinArchive(path)) return false;
        var result = OsuLibrary.Import(path);
        if (result.Ok && !result.IsSkin && result.Path != null)
        {
            _freeplay?.OnOsuSetImported(result.Path, result.Message);
            if (!_inFreeplay && _level == null && _loadState == LoadState.Idle) _inFreeplay = true;
        }
        else
        {
            _freeplay?.ShowToast(result.Message);
        }
        return true;
    }

    /// <summary>A .osu difficulty in mods/osu with the replay's level hash, or null.</summary>
    private static string? FindOsuLevelForReplay(string levelHash)
    {
        try
        {
            foreach (var file in Directory.EnumerateFiles(OsuLibrary.RootFolder, "*.osu", SearchOption.AllDirectories))
                if (Multiplayer.LevelHasher.CalculateHash(file) == levelHash)
                    return file;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[Replay] osu! level search failed: {ex.Message}");
        }
        return null;
    }
}
