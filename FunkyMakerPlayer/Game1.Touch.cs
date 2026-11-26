using System;
using System.Threading;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Input;

namespace FunkyMakerPlayer;

/// <summary>
/// Android touch UI (fork, 2026-11-23): the system back button / gesture, the shared touch pointer
/// (<see cref="UI.PointerInput"/>) and when the on-screen menu buttons are hidden.
/// </summary>
public partial class Game1
{
    private static int _pendingAndroidBack;
#if ANDROID
    private GamePadState _prevBackPad;
#endif

    /// <summary>Called by MainActivity (UI thread, same as the game loop) on back button / gesture.</summary>
    public static void RequestAndroidBack() => Interlocked.Increment(ref _pendingAndroidBack);

    /// <summary>
    /// Start of the frame: a pending back press becomes Escape held for this frame, or, on the plain
    /// main menu, sends the app to the background like any Android app (it used to quit the game
    /// from every screen, or open Settings from the main menu).
    /// </summary>
    private void UpdateAndroidBack()
    {
#if ANDROID
        LayoutKeyboard.BackHeld = false;
        bool back = Interlocked.Exchange(ref _pendingAndroidBack, 0) > 0;
        // Some MonoGame / Android versions report the back key as the gamepad Back button instead.
        var pad = GamePad.GetState(PlayerIndex.One);
        if (pad.Buttons.Back == ButtonState.Pressed && _prevBackPad.Buttons.Back != ButtonState.Pressed)
            back = true;
        _prevBackPad = pad;
        if (!back) return;

        if (IsAtAndroidRootScreen())
        {
            try { Microsoft.Xna.Framework.Game.Activity?.MoveTaskToBack(true); }
            catch (Exception ex) { Console.WriteLine($"[Android] MoveTaskToBack failed: {ex.Message}"); }
            return;
        }
        LayoutKeyboard.BackHeld = true;
#endif
    }

#if ANDROID
    private bool IsAtAndroidRootScreen() =>
        _inFreeplay && _freeplay != null && _freeplay.IsAtRoot && _level == null &&
        _loadingScreen == null && _errorMsg == null &&
        !(_settingsManager?.IsActive ?? false) && !_keyBindOverlay.IsActive &&
        !(_modsHelpScreen?.IsVisible ?? false) &&
        !_inMultiplayer && !_inMainSettings && !_inHudEditor && !_inEditorPicker &&
        !_inLevelSelector && !_inEditor && !_inReplays;

    /// <summary>
    /// Updates the shared touch pointer after <c>TouchControls.Update</c>. In "Auto" the D-pad /
    /// A / B are hidden on screens that work fully by tapping, so they don't cover lists and
    /// buttons (the chart / HUD editors and text-name entry keep them).
    /// </summary>
    private void UpdateTouchPointer(GameTime gameTime)
    {
        if (_touchControls == null) return;
        bool isNonTouchEditor = _inEditor && _activeEditor is not Editors.Character.CharacterEditor;
        bool touchReady = !isNonTouchEditor && !_inHudEditor && !_inLevelSelector && !_keyBindOverlay.IsActive;
        _touchControls.ScreenIsTouchReady = touchReady;
        UI.PointerInput.Update(_touchControls.LastTouches,
            p => _touchControls.IsPointOnMenuControl(p),
            GraphicsDevice.PresentationParameters.BackBufferHeight,
            (float)gameTime.ElapsedGameTime.TotalSeconds);
    }
#endif
}
