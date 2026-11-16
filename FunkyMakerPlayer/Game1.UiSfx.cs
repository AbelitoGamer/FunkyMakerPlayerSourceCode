using Microsoft.Xna.Framework.Input;
using FunkyMakerPlayer.UI;

namespace FunkyMakerPlayer;

/// <summary>
/// UI sound effects for screens and overlays owned by Game1.
/// A snapshot of the UI state is taken before each Update and compared afterwards, so sounds
/// follow what actually changed (keyboard, mouse and touch alike) without touching every input path.
/// Individual menus (Freeplay, Editor Hub, editors…) queue their own in-menu sounds via UiSfx.
/// </summary>
public partial class Game1
{
    private readonly record struct UiSfxSnapshot(
        int ScreenDepth,
        bool Playtesting,
        bool SettingsActive, int SettingsTab, int SettingsSelection, bool SettingsFocused,
        bool PauseActive, int PauseSelection,
        bool GameOverActive, int GameOverSelection,
        bool KeyBindActive, int KeyBindLane,
        bool ArrowsActive, int ArrowsSelection, string NoteStyle,
        bool SplashActive, int SplashSelection, string SplashStyle,
        bool CountdownActive, int CountdownSelection, string Countdown,
        bool FontActive, int FontSelection,
        bool LoadingActive, string LoadingStep, LoadingScreen.Phase LoadingPhase,
        bool HasError,
        KeyboardState Keyboard);

    /// <summary>
    /// Rough navigation depth used to tell "going forward" (Confirm) from "going back" (Back)
    /// when the active full screen changes.
    /// </summary>
    private int GetUiScreenDepth()
    {
        if (_inEditor || _inHudEditor) return 3;
        if (_inLevelSelector || _inMainSettings) return 2;
        if (_inEditorPicker || _inMultiplayer) return 1;
        if (_inFreeplay) return 0;
        return 1; // gameplay / loading / results
    }

    private UiSfxSnapshot CaptureUiSfxSnapshot() => new(
        GetUiScreenDepth(),
        _playtestingFromEditor,
        _settingsManager?.IsActive ?? false, _settingsManager?.CurrentTab ?? 0,
        _settingsManager?.Selection ?? 0, _settingsManager?.IsFocused ?? false,
        _inPauseMenu, _pauseMenu?.Selection ?? 0,
        _gameOverOverlay?.IsActive ?? false, _gameOverOverlay?.Selection ?? 0,
        _keyBindOverlay.IsActive, _keyBindOverlay.CurrentLane,
        _arrowsMenu?.IsActive ?? false, _arrowsMenu?.Selection ?? 0, NoteStyleName,
        _splashMenu?.IsActive ?? false, _splashMenu?.Selection ?? 0, NoteSplashStyleName,
        _countdownMenu?.IsActive ?? false, _countdownMenu?.Selection ?? 0, SelectedCountdown,
        _fontMenu?.IsActive ?? false, _fontMenu?.Selection ?? 0,
        _loadingScreen != null, _loadingScreen?.CurrentStep ?? "", _loadingScreen?.CurrentPhase ?? LoadingScreen.Phase.Loading,
        _errorMsg != null,
        // _prevKb = previous frame's keys before UpdateInternal, this frame's keys after it
        // (merged with Android touch buttons), so comparing the two snapshots gives "just pressed".
        _prevKb);

    private void QueueUiSfxForScreenChanges(UiSfxSnapshot b)
    {
        var a = CaptureUiSfxSnapshot();
        bool confirmKey = Pressed(a.Keyboard, b.Keyboard, Keys.Enter) || Pressed(a.Keyboard, b.Keyboard, Keys.Space);

        // ── Full screens ─────────────────────────────────────────────────────
        if (b.Playtesting && !a.Playtesting)
            UiSfx.Play(UiSound.Back);                 // back from playtest to the editor
        else if (a.ScreenDepth > b.ScreenDepth)
            UiSfx.Play(UiSound.Confirm);
        else if (a.ScreenDepth < b.ScreenDepth)
            UiSfx.Play(UiSound.Back);

        if (!b.HasError && a.HasError)
            UiSfx.Play(UiSound.Error);

        // Settings re-appears when you come back from a picker / key binder / HUD launcher:
        // that must not sound like "opening" it again.
        bool returnedFromSubScreen = a.ScreenDepth < b.ScreenDepth ||
            (b.ArrowsActive && !a.ArrowsActive) || (b.SplashActive && !a.SplashActive) ||
            (b.CountdownActive && !a.CountdownActive) || (b.FontActive && !a.FontActive) ||
            (b.KeyBindActive && !a.KeyBindActive);

        // ── Settings overlay ─────────────────────────────────────────────────
        if (a.SettingsActive != b.SettingsActive)
        {
            if (!a.SettingsActive) UiSfx.Play(UiSound.Back);
            else if (!returnedFromSubScreen) UiSfx.Play(UiSound.Confirm);
        }
        else if (a.SettingsActive)
        {
            if (a.SettingsTab != b.SettingsTab) UiSfx.Play(UiSound.Tab);
            else if (a.SettingsSelection != b.SettingsSelection) UiSfx.Play(UiSound.MoveRow);
            if (a.SettingsFocused && !b.SettingsFocused) UiSfx.Play(UiSound.Focus);
            // Toggles / sliders / actions are queued by SettingsManager itself.
        }

        // ── Pause menu ───────────────────────────────────────────────────────
        if (a.PauseActive != b.PauseActive)
            UiSfx.Play(a.PauseActive || confirmKey ? UiSound.Confirm : UiSound.Back);
        else if (a.PauseActive && a.PauseSelection != b.PauseSelection)
            UiSfx.Play(UiSound.Move);

        // ── Game over ────────────────────────────────────────────────────────
        if (a.GameOverActive && b.GameOverActive && a.GameOverSelection != b.GameOverSelection)
            UiSfx.Play(UiSound.Move);
        else if (b.GameOverActive && !a.GameOverActive)
            UiSfx.Play(b.GameOverSelection == 0 ? UiSound.Confirm : UiSound.Back);

        // ── Key binding overlay ──────────────────────────────────────────────
        if (a.KeyBindActive != b.KeyBindActive)
            UiSfx.Play(a.KeyBindActive ? UiSound.Confirm : (b.KeyBindLane >= 3 && !Pressed(a.Keyboard, b.Keyboard, Keys.Escape) ? UiSound.Save : UiSound.Back));
        else if (a.KeyBindActive && a.KeyBindLane != b.KeyBindLane)
            UiSfx.Play(UiSound.Type);

        // ── Style pickers (note style / splash / countdown / font) ──────────
        StylePicker(b.ArrowsActive, a.ArrowsActive, b.ArrowsSelection, a.ArrowsSelection, b.NoteStyle != a.NoteStyle, confirmKey);
        StylePicker(b.SplashActive, a.SplashActive, b.SplashSelection, a.SplashSelection, b.SplashStyle != a.SplashStyle, confirmKey);
        StylePicker(b.CountdownActive, a.CountdownActive, b.CountdownSelection, a.CountdownSelection, b.Countdown != a.Countdown, confirmKey);
        StylePicker(b.FontActive, a.FontActive, b.FontSelection, a.FontSelection, confirmKey, confirmKey);

        // ── Loading screen ───────────────────────────────────────────────────
        if (a.LoadingActive && b.LoadingActive)
        {
            if (a.LoadingStep != b.LoadingStep) UiSfx.Play(UiSound.LoadTick);
            if (b.LoadingPhase == LoadingScreen.Phase.Loading && a.LoadingPhase == LoadingScreen.Phase.Ready)
                UiSfx.Play(UiSound.Notice);
            if (b.LoadingPhase == LoadingScreen.Phase.Ready && a.LoadingPhase == LoadingScreen.Phase.Clearing)
                UiSfx.Play(UiSound.Confirm);
        }
    }

    private static void StylePicker(bool wasActive, bool isActive, int wasSel, int isSel, bool changed, bool confirmKey)
    {
        if (!wasActive && isActive)
            UiSfx.Play(UiSound.Confirm);
        else if (wasActive && !isActive)
            UiSfx.Play(changed ? UiSound.StyleSelect : confirmKey ? UiSound.AlreadyInUse : UiSound.Back);
        else if (isActive && wasSel != isSel)
            UiSfx.Play(UiSound.Move);
    }

    private static bool Pressed(KeyboardState now, KeyboardState before, Keys key)
        => now.IsKeyDown(key) && !before.IsKeyDown(key);
}
