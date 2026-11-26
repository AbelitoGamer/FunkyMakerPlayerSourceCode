using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using Microsoft.Xna.Framework.Input;

namespace FunkyMakerPlayer.UI;

/// <summary>
/// Every attempt at a level, for one side (fork, opened from the Freeplay details' personal best).
/// Newest first, the personal best marked; each row shows the stats, the result (or how far a game
/// over got), the song rate and whether its replay still exists. Enter / click / WATCH watches the
/// replay; "..." (or the options key, or right click) has WATCH REPLAY and DELETE ATTEMPT.
/// The owner reads <see cref="WatchRequested"/> (a replay path) and deletes through the callback.
/// </summary>
public sealed class AttemptsWindow
{
    private readonly UiDraw _ui;
    private readonly OptionsMenu _menu;
    private KeyboardState _prevKb;
    private string _levelPath = "", _levelName = "";
    private bool _enemy;
    private List<FreeplayAttempt> _attempts = new();
    private readonly Dictionary<FreeplayAttempt, bool> _replayExists = new();
    private FreeplayAttempt? _best;
    private int _sel, _scroll, _visible = 6;
    private float _anim;
    private Point _mouse;
    private Rectangle _panel, _closeBtn;
    private readonly List<(int Index, Rectangle Row, Rectangle Watch, Rectangle More)> _rows = new();

    public bool IsOpen { get; private set; }
    /// <summary>Replay file the player asked to watch (the owner clears it).</summary>
    public string? WatchRequested { get; set; }

    public AttemptsWindow(UiDraw ui)
    {
        _ui = ui;
        _menu = new OptionsMenu(ui);
    }

    public void Open(string levelPath, string levelName, bool enemy, KeyboardState kb)
    {
        _levelPath = levelPath;
        _levelName = levelName;
        _enemy = enemy;
        _prevKb = kb;
        _sel = 0;
        _scroll = 0;
        Reload();
        _anim = 0f;
        IsOpen = true;
        UiSfx.Play(UiSound.Confirm);
    }

    private void Reload()
    {
        var rec = FreeplayData.Instance.Get(_levelPath);
        _attempts = rec?.Attempts.Where(a => a.Enemy == _enemy).OrderByDescending(a => a.DateUtc).ToList() ?? new();
        _best = rec?.Best(_enemy);
        _replayExists.Clear();
        foreach (var a in _attempts) _replayExists[a] = a.HasReplay; // checked once, not every frame
        _sel = Math.Clamp(_sel, 0, Math.Max(0, _attempts.Count - 1));
    }

    public void Close()
    {
        if (!IsOpen) return;
        IsOpen = false;
        _menu.Close();
        UiSfx.Play(UiSound.Back);
    }

    private void Watch(int index)
    {
        if (index < 0 || index >= _attempts.Count) return;
        if (!_replayExists[_attempts[index]]) { UiSfx.Play(UiSound.Error); return; }
        WatchRequested = _attempts[index].ReplayPath;
        IsOpen = false;
    }

    private void OpenMenu(int index, Point anchor, KeyboardState kb, string? confirmFirst = null)
    {
        if (index < 0 || index >= _attempts.Count) return;
        _sel = index;
        bool replay = _replayExists[_attempts[index]];
        _menu.Open($"Attempt of {_attempts[index].DateUtc.ToLocalTime():yyyy-MM-dd HH:mm}", new[]
        {
            new OptionsMenu.Item("watch", replay ? "WATCH REPLAY" : "NO REPLAY", Enabled: replay),
            new OptionsMenu.Item("delete", "DELETE ATTEMPT", Danger: true, Confirm: "Delete this attempt? (its replay stays)"),
        }, anchor, kb, confirmFirst);
    }

    public bool Update(float dt, KeyboardState kb, Point mouse, bool clicked, bool rightClicked, bool mouseMoved)
    {
        _anim = MathHelper.Lerp(_anim, IsOpen ? 1f : 0f, Math.Min(1f, dt * 16f));
        _mouse = mouse;
        if (!IsOpen) { _prevKb = kb; return false; }

        if (_menu.Update(kb, mouse, clicked, rightClicked, mouseMoved, out string? chosen))
        {
            if (chosen == "watch") Watch(_sel);
            else if (chosen == "delete" && _sel < _attempts.Count)
            {
                FreeplayData.Instance.DeleteAttempt(_levelPath, _attempts[_sel]);
                Reload();
            }
            _prevKb = kb;
            return true;
        }

        bool Pressed(Keys k) => kb.IsKeyDown(k) && !_prevKb.IsKeyDown(k);
        int hover = _rows.FindIndex(r => r.Row.Contains(mouse));
        int hoverIndex = hover >= 0 ? _rows[hover].Index : -1;
        if (hoverIndex >= 0 && mouseMoved && hoverIndex != _sel) { _sel = hoverIndex; UiSfx.Play(UiSound.MoveList); }

        if (Pressed(Keys.Escape) || Pressed(Keys.Back) || Controls.Pressed(HotkeyAction.FreeplayAttempts, kb, _prevKb))
            Close();
        else if (_attempts.Count > 0 && (Pressed(Keys.Up) || Pressed(Keys.Down)))
        {
            _sel = Math.Clamp(_sel + (Pressed(Keys.Up) ? -1 : 1), 0, _attempts.Count - 1);
            UiSfx.Play(UiSound.MoveList);
        }
        else if (Pressed(Keys.Enter) || Pressed(Keys.Space))
            Watch(_sel);
        else if (Controls.Pressed(HotkeyAction.FreeplayOptions, kb, _prevKb) && hover < 0 && _sel < _attempts.Count)
        {
            var r = _rows.FirstOrDefault(x => x.Index == _sel);
            OpenMenu(_sel, r.More != Rectangle.Empty ? new Point(r.More.Right, r.More.Bottom) : mouse, kb);
        }
        else if (Controls.Pressed(HotkeyAction.DeleteItem, kb, _prevKb) && _sel < _attempts.Count)
        {
            var r = _rows.FirstOrDefault(x => x.Index == _sel);
            OpenMenu(_sel, r.More != Rectangle.Empty ? new Point(r.More.Right, r.More.Bottom) : mouse, kb, confirmFirst: "delete");
        }
        else if (rightClicked && hover >= 0)
            OpenMenu(hoverIndex, mouse, kb);
        else if (clicked)
        {
            if (_closeBtn.Contains(mouse) || !_panel.Contains(mouse)) Close();
            else if (hover >= 0)
            {
                var r = _rows[hover];
                if (r.More.Contains(mouse)) OpenMenu(r.Index, new Point(r.More.Right, r.More.Bottom), kb);
                else Watch(r.Index);
            }
        }

        if (_sel < _scroll) _scroll = _sel;
        if (_sel >= _scroll + _visible) _scroll = _sel - _visible + 1;
        _scroll = Math.Clamp(_scroll, 0, Math.Max(0, _attempts.Count - _visible));

        _prevKb = kb;
        return true;
    }

    /// <summary>Scrolls the list (mouse wheel, from the owner).</summary>
    public void Scroll(int rows)
    {
        if (!IsOpen || _attempts.Count == 0) return;
        _sel = Math.Clamp(_sel + rows, 0, _attempts.Count - 1);
    }

    public void Draw(SpriteBatch sb, int sw, int sh)
    {
        _rows.Clear();
        float a = _anim;
        if (!IsOpen) return; // (fade-in only: Update stops running once closed)
        float s = UiScale.FromScreen(sw, sh);

        int w = Math.Min(sw - UiScale.Px(40, s), UiScale.Px(1040, s));
        int h = Math.Min(sh - UiScale.Px(40, s), UiScale.Px(600, s));
        _panel = new Rectangle((sw - w) / 2, (sh - h) / 2 - (int)((1f - a) * UiScale.Px(14, s)), w, h);
        _ui.ModalFrame(sb, sw, sh, _panel, a, new Color(255, 215, 0));

        int pad = UiScale.Px(22, s);
        int titleF = UiScale.FontPx(24, s);
        string title = _enemy ? "ATTEMPTS  (ENEMY)" : "ATTEMPTS";
        _ui.Text(sb, title, new Vector2(_panel.X + pad, _panel.Y + UiScale.Px(16, s)), new Color(255, 215, 0) * a, titleF);
        int nf = UiScale.FontPx(15, s);
        float tw = _ui.Measure(title, titleF).X;
        string name = _ui.Truncate(_levelName, nf, w - pad * 3 - tw - UiScale.Px(140, s));
        _ui.Text(sb, name, new Vector2(_panel.X + pad + tw + UiScale.Px(16, s), _panel.Y + UiScale.Px(24, s)), Color.White * (0.7f * a), nf);

        int btnF = UiScale.FontPx(14, s);
        int cw = (int)_ui.Measure("CLOSE", btnF).X + UiScale.Px(36, s), bh = UiScale.Px(34, s, 28);
        _closeBtn = new Rectangle(_panel.Right - pad - cw, _panel.Y + UiScale.Px(16, s), cw, bh);
        _ui.Rect(sb, _closeBtn, (_closeBtn.Contains(_mouse) ? new Color(255, 68, 136) * 0.4f : new Color(36, 34, 56)) * a);
        _ui.CenterText(sb, "CLOSE", _closeBtn, Color.White * a, btnF);

        int listTop = _panel.Y + UiScale.Px(64, s);
        int footerH = UiScale.Px(36, s);
        int rowH = UiScale.Px(58, s, 46), gap = UiScale.Px(6, s, 4);
        int listBottom = _panel.Bottom - footerH;
        _visible = Math.Max(1, (listBottom - listTop + gap) / (rowH + gap));

        if (_attempts.Count == 0)
        {
            _ui.CenterText(sb, "No attempts yet. Finish (or fail) a song and it shows up here.",
                new Rectangle(_panel.X, listTop, w, UiScale.Px(80, s)), Color.White * (0.45f * a), UiScale.FontPx(16, s));
        }

        int bigF = UiScale.FontPx(20, s), smallF = UiScale.FontPx(12, s), tagF = UiScale.FontPx(12, s);
        for (int i = _scroll; i < Math.Min(_attempts.Count, _scroll + _visible); i++)
        {
            var at = _attempts[i];
            var row = new Rectangle(_panel.X + pad, listTop + (i - _scroll) * (rowH + gap), w - pad * 2, rowH);
            bool sel = i == _sel;
            bool isBest = ReferenceEquals(at, _best);
            _ui.Rect(sb, row, (sel ? new Color(44, 38, 66) : new Color(30, 28, 48)) * a);
            _ui.Rect(sb, row.X, row.Y, UiScale.Px(4, s, 3), row.Height,
                (isBest ? new Color(255, 215, 0) : at.Failed ? new Color(255, 90, 110) : new Color(255, 68, 136) * 0.5f) * a);

            int x = row.X + UiScale.Px(14, s);
            // Rank / result column
            string rank = at.Failed ? "F" : at.Rank;
            Color rc = at.Failed ? new Color(255, 90, 110) : RankColor(rank);
            var rankBox = new Rectangle(x, row.Y + (rowH - UiScale.Px(36, s)) / 2, UiScale.Px(44, s), UiScale.Px(36, s));
            _ui.Rect(sb, rankBox, new Color(16, 16, 28) * a);
            _ui.CenterText(sb, rank, rankBox, rc * a, bigF);
            x = rankBox.Right + UiScale.Px(14, s);

            // Score + line of stats
            string score = at.Score.ToString("N0", CultureInfo.InvariantCulture);
            _ui.Text(sb, score, new Vector2(x, row.Y + UiScale.Px(6, s)), Color.White * a, bigF);
            float scoreW = _ui.Measure(score, bigF).X;
            int tagX = x + (int)scoreW + UiScale.Px(10, s);
            void Tag(string text, Color c)
            {
                var tsz = _ui.Measure(text, tagF);
                var tr = new Rectangle(tagX, row.Y + UiScale.Px(9, s), (int)tsz.X + UiScale.Px(12, s), (int)tsz.Y + UiScale.Px(4, s));
                _ui.Rect(sb, tr, c * (0.22f * a));
                _ui.CenterText(sb, text, tr, c * a, tagF);
                tagX = tr.Right + UiScale.Px(6, s);
            }
            if (isBest) Tag("BEST", new Color(255, 215, 0));
            if (at.Failed) Tag($"FAILED AT {at.Progress * 100f:0}%", new Color(255, 110, 120));
            else if (at.ComboLabel.Length > 0) Tag(at.ComboLabel, new Color(255, 225, 120));
            if (Math.Abs(at.Rate - 1f) > 0.001f) Tag(at.Rate.ToString("0.00", CultureInfo.InvariantCulture) + "x", new Color(190, 140, 255));

            string stats = $"{at.Accuracy.ToString("0.00", CultureInfo.InvariantCulture)}%   |   {at.Misses} miss   |   {at.MaxCombo}x combo   |   " +
                           $"S {at.Sicks}  G {at.Goods}  B {at.Bads}  Sh {at.Shits}";
            // Right: date, replay, "..."
            int moreW = UiScale.Px(30, s, 24), btnH = UiScale.Px(26, s, 22);
            var more = new Rectangle(row.Right - UiScale.Px(10, s) - moreW, row.Y + (rowH - btnH) / 2, moreW, btnH);
            bool replay = _replayExists.TryGetValue(at, out bool ex) && ex;
            string watchLabel = replay ? "WATCH" : "NO REPLAY";
            int ww = (int)_ui.Measure("NO REPLAY", tagF).X + UiScale.Px(18, s);
            var watch = new Rectangle(more.X - UiScale.Px(8, s) - ww, more.Y, ww, btnH);
            if (replay)
            {
                bool wh = watch.Contains(_mouse);
                _ui.Rect(sb, watch, (wh ? new Color(46, 224, 150) * 0.45f : new Color(46, 224, 150) * 0.18f) * a);
                _ui.CenterText(sb, watchLabel, watch, (wh ? Color.White : new Color(120, 240, 180)) * a, tagF);
            }
            else _ui.CenterText(sb, watchLabel, watch, Color.White * (0.3f * a), tagF);
            _ui.MoreButton(sb, more, more.Contains(_mouse), a, s);

            string when = at.DateUtc.ToLocalTime().ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture);
            var wsz = _ui.Measure(when, smallF);
            _ui.Text(sb, when, new Vector2(watch.X - UiScale.Px(14, s) - wsz.X, row.Y + (rowH - wsz.Y) / 2f), Color.White * (0.45f * a), smallF);

            float statsW = watch.X - UiScale.Px(14, s) - wsz.X - UiScale.Px(14, s) - x;
            _ui.Text(sb, _ui.Truncate(stats, smallF, statsW), new Vector2(x, row.Bottom - UiScale.Px(8, s) - _ui.Measure("A", smallF).Y),
                Color.White * (0.65f * a), smallF);

            _rows.Add((i, row, replay ? watch : Rectangle.Empty, more));
        }

        string hint = UI.PointerInput.IsTouch
            ? $"{_attempts.Count} attempt{(_attempts.Count == 1 ? "" : "s")}   |   WATCH: replay   |   \"...\": Options   |   Back: close"
            : $"{_attempts.Count} attempt{(_attempts.Count == 1 ? "" : "s")}   |   ENTER Watch replay   |   [{Controls.Hint(HotkeyAction.FreeplayOptions)}] Options   |   ESC Close";
        int hf = _ui.FitFontSize(hint, UiScale.FontPx(13, s), 9, w - pad * 2);
        var hsz = _ui.Measure(hint, hf);
        _ui.Text(sb, hint, new Vector2(_panel.X + pad, _panel.Bottom - footerH + (footerH - hsz.Y) / 2f), Color.White * (0.4f * a), hf);

        _menu.Draw(sb, sw, sh);
    }

    private static Color RankColor(string rank) => rank switch
    {
        "S+" => new Color(255, 225, 90),
        "S" => new Color(255, 200, 60),
        "A" => new Color(110, 235, 130),
        "B" => new Color(90, 200, 255),
        "C" => new Color(200, 150, 255),
        _ => new Color(255, 110, 120),
    };
}
