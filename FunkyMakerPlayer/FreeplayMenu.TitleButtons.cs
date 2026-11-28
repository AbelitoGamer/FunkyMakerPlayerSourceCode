using System;
using System.Collections.Generic;
using FunkyMakerPlayer.UI;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using Microsoft.Xna.Framework.Input;

namespace FunkyMakerPlayer;

/// <summary>
/// Title screen secondary buttons (fork, 2026-11-27): a row of small buttons under the main options,
/// for things that are not a place to go.
/// <list type="bullet">
/// <item>Desktop: <b>MODS FOLDER</b> (shows where the mods folder is; opens a window to move it
/// between AppData and next to the game, <see cref="ModsStorage"/>). It used to be a Settings row.</item>
/// <item>Android: OPEN MODS / IMPORT FILE / FOLDER ACCESS, which used to be three extra main options
/// (<see cref="MenuAction"/> 5 / 4 / 6, handled by Game1.Android.cs).</item>
/// </list>
/// Mouse / touch only, like the other buttons (no keys, see the UI rule in Context.md).
/// </summary>
public partial class FreeplayMenu
{
    private enum TitleButton { ModsFolder, OpenMods, ImportFile, FolderAccess }

    private readonly List<(Rectangle Bounds, TitleButton Id)> _titleButtons = new();
    private int _titleButtonHover = -1;

    private static IEnumerable<TitleButton> TitleButtonIds()
    {
#if ANDROID
        yield return TitleButton.OpenMods;
        yield return TitleButton.ImportFile;
        yield return TitleButton.FolderAccess;
#else
        yield return TitleButton.ModsFolder;
#endif
    }

    private string TitleButtonLabel(TitleButton id) => id switch
    {
        TitleButton.OpenMods => "OPEN MODS",
        TitleButton.ImportFile => "IMPORT FILE",
        TitleButton.FolderAccess => "FOLDER ACCESS",
        _ => ModsStorage.IsMoving
            ? Safe(ModsStorage.Status ?? "MOVING MODS...")
            : "MODS FOLDER: " + (ModsStorage.Current == ModsStorage.Location.AppData ? "APPDATA" : "NEXT TO GAME"),
    };

    /// <summary>Height the row needs below the main options (0 = no row).</summary>
    private static int TitleButtonsRowHeight(float s) =>
        UI.PointerInput.IsTouch ? UiScale.Px(52, s, 44) : UiScale.Px(36, s, 30);

    /// <summary>Hover / click on the row. Returns true when it used the click.</summary>
    private bool UpdateTitleButtons(Point rtMouse, bool mouseClicked)
    {
        _titleButtonHover = -1;
        for (int i = 0; i < _titleButtons.Count; i++)
        {
            if (!_titleButtons[i].Bounds.Contains(rtMouse)) continue;
            _titleButtonHover = i;
            if (!mouseClicked) return false;
            RunTitleButton(_titleButtons[i].Id);
            return true;
        }
        return false;
    }

    private void RunTitleButton(TitleButton id)
    {
        switch (id)
        {
            case TitleButton.OpenMods: MenuAction = 5; break;     // open the mods folder
            case TitleButton.ImportFile: MenuAction = 4; break;   // file picker
            case TitleButton.FolderAccess: MenuAction = 6; break; // SAF folder picker
            case TitleButton.ModsFolder:
#if !ANDROID
                if (ModsStorage.IsMoving) { UI.UiSfx.Play(UI.UiSound.Error); return; }
                _modsMoveTarget = ModsStorage.Current == ModsStorage.Location.AppData
                    ? ModsStorage.Location.NextToGame
                    : ModsStorage.Location.AppData;
                _modsMoveSel = 1; // CANCEL selected first
#endif
                break;
        }
        UI.UiSfx.Play(UI.UiSound.Confirm);
    }

    /// <summary>Draws the row centred in the left area with its top at <paramref name="y"/>.</summary>
    private void DrawTitleButtons(SpriteBatch sb, int leftW, int y, float s)
    {
        _titleButtons.Clear();
        int h = TitleButtonsRowHeight(s);
        if (h <= 0) return;

        var ids = new List<TitleButton>(TitleButtonIds());
        int gap = UiScale.Px(10, s, 6);
        int padX = UiScale.Px(16, s, 10);
        int fontSize = UI.PointerInput.IsTouch ? UiScale.FontPx(19, s) : UiScale.FontPx(15, s);
        float maxW = leftW - UiScale.Px(32, s);

        // Shrink the text until the whole row fits
        int[] widths = new int[ids.Count];
        int total;
        while (true)
        {
            total = gap * (ids.Count - 1);
            for (int i = 0; i < ids.Count; i++)
            {
                widths[i] = (int)MeasureText(TitleButtonLabel(ids[i]), fontSize).X + padX * 2;
                total += widths[i];
            }
            if (total <= maxW || fontSize <= 10) break;
            fontSize--;
        }

        int x = leftW / 2 - total / 2;
        Color accent = new Color(255, 215, 0);
        for (int i = 0; i < ids.Count; i++)
        {
            var r = new Rectangle(x, y, widths[i], h);
            _titleButtons.Add((r, ids[i]));
            bool hov = i == _titleButtonHover;
            DrawRect(sb, r.X, r.Y, r.Width, r.Height, hov ? new Color(55, 42, 75) * 0.8f : new Color(30, 28, 48) * 0.6f);
            DrawRect(sb, r.X, r.Y, r.Width, 1, hov ? accent * 0.7f : Color.White * 0.12f);
            DrawRect(sb, r.X, r.Bottom - 1, r.Width, 1, hov ? new Color(255, 68, 136) * 0.7f : Color.White * 0.06f);
            string label = TitleButtonLabel(ids[i]);
            var sz = MeasureText(label, fontSize);
            DrawText(sb, label, new Vector2(r.X + (r.Width - sz.X) / 2f, r.Y + (r.Height - sz.Y) / 2f),
                hov ? Color.White : new Color(200, 200, 220) * 0.85f, fontSize);
            x += widths[i] + gap;
        }
    }

#if !ANDROID
    // ── Move mods folder window (desktop) ────────────────────────────────────
    private ModsStorage.Location? _modsMoveTarget;
    private int _modsMoveSel = 1;            // 0 = MOVE, 1 = CANCEL
    private Rectangle _modsMoveYesBounds, _modsMoveNoBounds;

    private const string ModsAppDataInfo =
        "AppData (recommended): one mods folder shared by every copy of the game on this computer, " +
        "kept safe when you update or reinstall.";
    private const string ModsNextToGameInfo =
        "Next to the game: the mods folder sits beside the game's .exe, so levels and mods are easier to find, " +
        "edit and manage by hand. Downside: each copy of the game has its own separate levels, which are no " +
        "longer shared like in AppData. If you don't manage level files often, move it back to AppData.";

    private static string ModsLocationName(ModsStorage.Location l) =>
        l == ModsStorage.Location.AppData ? "AppData" : "Next to game";

    /// <summary>True while the window is open (it then takes all the menu's input).</summary>
    private bool UpdateModsMoveWindow(KeyboardState kb, Point rtMouse, bool mouseClicked, bool mouseMoved, bool enter, bool esc)
    {
        if (_modsMoveTarget is not { } target) return false;
        bool yesHover = _modsMoveYesBounds.Contains(rtMouse);
        bool noHover = _modsMoveNoBounds.Contains(rtMouse);
        if (mouseMoved && yesHover) _modsMoveSel = 0;
        if (mouseMoved && noHover) _modsMoveSel = 1;
        bool Pressed(Keys k) => kb.IsKeyDown(k) && !_prevKb.IsKeyDown(k);
        if (Pressed(Keys.Left) || Pressed(Keys.Right)) _modsMoveSel = 1 - _modsMoveSel;

        bool move = (mouseClicked && yesHover) || (enter && _modsMoveSel == 0);
        bool cancel = esc || (mouseClicked && noHover) || (enter && _modsMoveSel == 1) ||
                      (mouseClicked && !yesHover && !noHover && !_modsMoveBox.Contains(rtMouse));
        if (move)
        {
            _modsMoveTarget = null;
            if (!ModsStorage.IsMoving) _ = ModsStorage.MoveToAsync(target);
            UI.UiSfx.Play(UI.UiSound.Confirm);
        }
        else if (cancel)
        {
            _modsMoveTarget = null;
            UI.UiSfx.Play(UI.UiSound.Back);
        }
        return true;
    }

    private Rectangle _modsMoveBox;

    private void DrawModsMoveWindow(SpriteBatch sb, int sw, int sh)
    {
        if (_modsMoveTarget is not { } target)
        {
            _modsMoveYesBounds = _modsMoveNoBounds = _modsMoveBox = Rectangle.Empty;
            return;
        }
        float s = UiScale.FromScreen(sw, sh);
        var from = ModsStorage.Current;
        Color accent = new Color(255, 68, 136);
        DrawRect(sb, 0, 0, sw, sh, new Color(8, 8, 16) * 0.78f);

        int boxW = Math.Min(UiScale.Px(780, s), sw - UiScale.Px(40, s));
        int pad = UiScale.Px(24, s);
        int titleSize = UiScale.FontPx(28, s);
        int bodySize = UiScale.FontPx(17, s);
        float maxW = boxW - pad * 2;

        var lines = new List<(string? Text, Color Color)>();
        void Para(string text, Color c)
        {
            string line = "";
            foreach (var word in Safe(text).Split(' '))
            {
                string test = line.Length == 0 ? word : line + " " + word;
                if (MeasureText(test, bodySize).X > maxW && line.Length > 0)
                {
                    lines.Add((line, c));
                    line = word;
                }
                else line = test;
            }
            if (line.Length > 0) lines.Add((line, c));
        }
        Para($"From ({ModsLocationName(from)}): {ModsStorage.PathFor(from)}", Color.White * 0.7f);
        Para($"To ({ModsLocationName(target)}): {ModsStorage.PathFor(target)}", Color.White * 0.95f);
        lines.Add((null, Color.White));
        Para(target == ModsStorage.Location.AppData ? ModsAppDataInfo : ModsNextToGameInfo, new Color(255, 225, 150));
        lines.Add((null, Color.White));
        Para("Everything is moved: levels, note styles, splashes, countdowns and fonts. Identical files are merged; " +
             "different files with the same name are kept as \"name (2)\".", Color.White * 0.75f);
        if (ModsStorage.Status is { } last)
            Para($"Last move: {last}", Color.White * 0.55f);

        float lineH = MeasureText("Ag", bodySize).Y + UiScale.Px(4, s);
        float titleH = MeasureText("Ag", titleSize).Y;
        int btnH = UiScale.Px(46, s, 38);
        float bodyH = 0;
        foreach (var l in lines) bodyH += l.Text == null ? lineH * 0.5f : lineH;
        int boxH = (int)(pad + titleH + pad * 0.6f + bodyH + pad + btnH + pad);
        int boxX = (sw - boxW) / 2;
        int boxY = Math.Max(UiScale.Px(10, s), (sh - boxH) / 2);
        _modsMoveBox = new Rectangle(boxX, boxY, boxW, boxH);

        int glow = UiScale.Px(3, s, 2);
        DrawRect(sb, boxX - glow, boxY - glow, boxW + glow * 2, boxH + glow * 2, accent * 0.35f);
        DrawRect(sb, boxX, boxY, boxW, boxH, new Color(22, 22, 38) * 0.98f);
        DrawRect(sb, boxX, boxY, boxW, UiScale.Px(4, s, 2), accent);

        float y = boxY + pad;
        DrawText(sb, "Move the mods folder?", new Vector2(boxX + pad, y), new Color(255, 215, 0), titleSize);
        y += titleH + pad * 0.6f;
        foreach (var l in lines)
        {
            if (l.Text == null) { y += lineH * 0.5f; continue; }
            DrawText(sb, l.Text, new Vector2(boxX + pad, y), l.Color, bodySize);
            y += lineH;
        }

        int btnGap = UiScale.Px(20, s);
        int btnW = (boxW - pad * 2 - btnGap) / 2;
        int btnY = boxY + boxH - pad - btnH;
        _modsMoveYesBounds = new Rectangle(boxX + pad, btnY, btnW, btnH);
        _modsMoveNoBounds = new Rectangle(boxX + pad + btnW + btnGap, btnY, btnW, btnH);
        int btnFont = UiScale.FontPx(18, s);
        DrawModalChoiceButton(sb, _modsMoveYesBounds, _modsMoveSel == 0, "MOVE", new Color(80, 240, 150), btnFont);
        DrawModalChoiceButton(sb, _modsMoveNoBounds, _modsMoveSel == 1, "CANCEL", accent, btnFont);
    }
#endif
}

public partial class FreeplayMenu
{
    /// <summary>Developer harness (Game1.DevShot.cs): opens a screen of this menu.</summary>
    internal void DevOpen(string what)
    {
        switch (what)
        {
            case "freeplay": ExecuteMainMenuSelection(0); break;
            case "editors": ExecuteMainMenuSelection(3); break;
            case "credits": ExecuteMainMenuSelection(5); break;
            case "replays": ExecuteMainMenuSelection(2); break;
            case "online": ExecuteMainMenuSelection(1); break;
#if !ANDROID
            case "modsfolder": RunTitleButton(TitleButton.ModsFolder); break;
#endif
        }
    }
}
