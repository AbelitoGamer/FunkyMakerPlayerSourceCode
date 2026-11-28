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

/// <summary>In-game settings overlay results (moved out of Game1.cs).</summary>
public partial class Game1
{
    private void ExitSettingsMenu()
    {
#if ANDROID
        _touchControls?.SetSettingsButtonsVisible(false);
#endif
        if (_level != null)
        {
            _inPauseMenu = true;
            if (_pauseMenu != null)
            {
                _pauseMenu.IsMultiplayer = _isMultiplayerMatch;
                _pauseMenu.Selection = 0;
            }
            _pausedByTimeskip = false;
        }
        else if (_inMultiplayer)
        {
            _multiplayerMenu?.ResetInputCooldown();
        }
        else if (_inFreeplay)
        {
            _freeplay?.ResetInputCooldown();
        }
        else if (_pausedByTimeskip && _paused && _started && _level != null)
        {
            ResumeSong();
            _pausedByTimeskip = false;
        }
    }

    private void ApplySettingsSelectResult(SettingsActionResult result)
    {
        if (result.NeedsStrumlineReposition && _level != null)
            RepositionStrumlines();

        if (result.NeedsCameraReset)
            _cameraManager?.ResetScaleOverride();

        if (result.OpenKeyBind)
        {
            _keyBindOverlay.IsActive = true;
            if (!_keyBindCallbacksWired)
            {
                _keyBindCallbacksWired = true;
                _keyBindOverlay.OnComplete += () => {
                    if (_settingsManager != null) _settingsManager.IsActive = true;
                    _keyCaptureHoldoff = true; // the key that closed it must not type into the search
                };
                _keyBindOverlay.OnCancel += () => {
                    if (_settingsManager != null) _settingsManager.IsActive = true;
                    _keyCaptureHoldoff = true; // the key that closed it must not type into the search
                };
            }
        }

        if (result.OpenArrowsMenu)
            _arrowsMenu?.Open(GraphicsDevice);

        if (result.OpenSplashMenu)
            _splashMenu?.Open(GraphicsDevice);

        if (result.OpenCountdownMenu)
            _countdownMenu?.Open(GraphicsDevice);

        if (result.OpenFontMenu)
            _fontMenu?.Open(GraphicsDevice);

        if (result.OpenHudEditor && BlockEditorOnMobile("HUD Editor"))
            result.OpenHudEditor = false; // phones: stay in Settings, the notice window explains why

        if (result.OpenHudEditor)
        {
#if ANDROID
            _touchControls?.SetSettingsButtonsVisible(false);
#endif
            _hudEditorReturnTarget = _inFreeplay
                ? HudEditorReturnTarget.FreeplaySettings
                : (_inMultiplayer
                    ? HudEditorReturnTarget.MultiplayerSettings
                    : HudEditorReturnTarget.GameplaySettings);
            _inFreeplay = false;
            _inMultiplayer = false;
            _inMainSettings = true;
            _mainSettingsMenu?.ResetInputCooldown();
        }

        if (result.ExitMenu)
        {
            if (_settingsManager != null) _settingsManager.IsActive = false;
            bool openedSubmenu = result.OpenKeyBind || result.OpenArrowsMenu ||
                                 result.OpenSplashMenu || result.OpenCountdownMenu ||
                                 result.OpenFontMenu || result.OpenHudEditor;
            if (openedSubmenu)
            {
#if ANDROID
                _touchControls?.SetSettingsButtonsVisible(false);
#endif
            }
            else
            {
                ExitSettingsMenu();
            }
        }

        if (result.ToggleVSync)
        {
            _graphics.SynchronizeWithVerticalRetrace = !_graphics.SynchronizeWithVerticalRetrace;
            // ApplyChanges re-centres / re-borders the SDL window, which broke our borderless
            // fullscreen: lay it out again (keeping the windowed position it returns to).
            var windowedPos = _windowedPosition;
            _graphics.ApplyChanges();
            if (_isFullscreen)
            {
                SetFullscreen(true);
                _windowedPosition = windowedPos;
            }
        }

        if (result.ToggleFullscreen)
            SetFullscreen(!_isFullscreen);

        if (result.FpsCapChanged)
            ApplyFpsCap();

        if (result.NeedsStrumlineAlphaUpdate)
        {
            if (_noteRenderers.Count > 0)
                _noteRenderers[0].Alpha = (NoteRenderer.HidePlayerStrumline && !_isMultiplayerMatch) ? 0f : _notesAlpha;
            if (_noteRenderers.Count > 1)
                _noteRenderers[1].Alpha = GetOpponentStrumlineAlpha();
            for (int si = 2; si < _noteRenderers.Count; si++)
                _noteRenderers[si].Alpha = HideExtras ? 0f : _notesAlpha;
        }

        if (result.NeedsOpponentAlphaUpdate && _noteRenderers.Count > 1)
        {
            _noteRenderers[1].Alpha = GetOpponentStrumlineAlpha();
        }

        if (result.NeedsSave)
            Settings.Save(_graphics.SynchronizeWithVerticalRetrace);
    }

    private float GetOpponentStrumlineAlpha()
    {
        if (_isMultiplayerMatch)
        {
            var mp = Multiplayer.MultiplayerSettings.Instance;
            return mp.ShowOpponentNotes ? _notesAlpha * (mp.GhostTransparency / 100f) : 0f;
        }
        return HideOpponentStrumline ? 0f : _notesAlpha;
    }

    private void ApplySettingsAdjustmentResult(SettingsActionResult result)
    {
        if (result.AdjustVolume != 0f)
        {
            MasterVolume = MathF.Round(Math.Clamp(MasterVolume + result.AdjustVolume * 0.05f, 0f, 1f), 2);
            if (_musicInst != null) _musicInst.Volume = SongPlaybackVolume;
            _volumeToastTimer = VOLUME_TOAST_DURATION;
            Settings.Save(_graphics.SynchronizeWithVerticalRetrace);
        }

        if (result.SeekSong != 0f && _started && !_isMultiplayerMatch)
        {
            SeekSong(_songTime + result.SeekSong);
            if (!_paused)
            {
                PauseSong();
                _pausedByTimeskip = true;
            }
        }

        if (result.SongVolumeChanged && _musicInst != null)
            _musicInst.Volume = SongPlaybackVolume;

        if (result.NeedsSave)
            Settings.Save(_graphics.SynchronizeWithVerticalRetrace);
    }
}
