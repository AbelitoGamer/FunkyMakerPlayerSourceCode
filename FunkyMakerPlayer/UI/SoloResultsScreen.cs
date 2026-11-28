using System;
using System.Collections.Generic;
using System.Globalization;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using Microsoft.Xna.Framework.Input;

namespace FunkyMakerPlayer.UI;

/// <summary>
/// Results screen after a solo song (fork), laid out like osu!lazer's: a row of capsules, one per
/// attempt on this song and side, worse runs to the left and better ones to the right.
/// <list type="bullet">
/// <item>The run that just ended opens alone in the middle, expanded: its place among the attempts,
/// the song / mapper, the accuracy wheel (rank bands around it, the rank letter in the centre), the
/// score, stars and difficulty, accuracy / max combo / combo rating, the judgement counts and the
/// timing distribution of its hits. Numbers show +/- against your previous best.</item>
/// <item>When its animation ends, the other attempts slide out from behind it as compact capsules.
/// Mouse wheel / drag / swipe scrolls the row; clicking a capsule (or LEFT / RIGHT) expands it, and
/// its numbers then compare against the run that just ended.</item>
/// </list>
/// ENTER / ESC goes back to Freeplay (the first ENTER skips the animation), R retries, W watches the
/// replay of the expanded attempt when it has one.
/// <para>Browse mode (<see cref="Run.Browse"/>, opened from Freeplay's personal best): the same screen as
/// an overlay over Freeplay for the saved attempts, the personal best in the middle. No backdrop and no
/// retry; DELETE (twice) removes the expanded attempt (the owner does it, see <see cref="Choice.Delete"/>).</para>
/// </summary>
public sealed class SoloResultsScreen
{
    public enum Choice { None, Continue, Retry, WatchReplay, Delete }

    /// <summary>What the screen shows (filled by Game1 when the song ends).</summary>
    public sealed class Run
    {
        public string SongName = "";
        public string Mapper = "";
        public string Difficulty = "";
        /// <summary>Star rating at the run's rate, or negative when unknown.</summary>
        public double Stars = -1;
        /// <summary>The run that just ended (the saved attempt, or a copy when it wasn't saved).</summary>
        public FreeplayAttempt Current = new();
        /// <summary>Every other attempt on this level and side.</summary>
        public List<FreeplayAttempt> Others = new();
        /// <summary>Timing error of every hit of this run, in ms (negative = early).</summary>
        public List<float> HitErrorsMs = new();
        public bool NewBest;
        /// <summary>Why the run doesn't count ("" = it was saved): autoplay, timeskip, rate, replay.</summary>
        public string NotSavedReason = "";
        /// <summary>Notes in the chart, for attempts saved without it (0 = unknown).</summary>
        public int ChartNotes;
        /// <summary>Viewing saved attempts from Freeplay: <see cref="Current"/> is the personal best.</summary>
        public bool Browse;
    }

    private readonly Texture2D _pixel;
    private readonly Run _run;
    /// <summary>All attempts, lowest score first (the row, left to right).</summary>
    private readonly List<FreeplayAttempt> _cards = new();
    private readonly int _currentIdx;
    /// <summary>Best attempt before this run (what this run's numbers compare against), or null.</summary>
    private readonly FreeplayAttempt? _previousBest;
    private readonly int[] _histogram;
    private readonly float _histRangeMs;

    private float _time;
    private float _alpha;
    private bool _closing;
    private Choice _pending = Choice.None;
    private readonly Dictionary<Choice, Rectangle> _buttons = new();
    private readonly List<(int Index, Rectangle Rect)> _cardRects = new();
    private Choice _hovered = Choice.None;
    private float _deleteArmed; // seconds left to confirm a delete (browse mode)

    // Row: the expanded capsule, the scroll position (row x at the screen centre) and pointer drag.
    private int _focus;
    private float _focusAge = 10f;
    private float _scroll, _scrollVis;
    private bool _prevDown, _pressActive, _dragging;
    private Point _downPos;
    private float _downScroll;
    private int _lastSw = 1280, _lastSh = 720;
    private bool _laidOut;

    // Timings of the opening animation (seconds)
    private const float FadeIn = 0.3f;
    private const float WheelFrom = 0.3f, WheelTo = 1.5f;
    private const float RankAt = 1.5f;
    private const float SidesAt = 1.9f;
    private const int HistBins = 81;

    private static readonly (float From, float To, string Rank)[] Bands =
    {
        (0f, 70f, "D"), (70f, 80f, "C"), (80f, 90f, "B"), (90f, 95f, "A"), (95f, 99.5f, "S"), (99.5f, 100f, "S+"),
    };

    private static readonly Color Panel = new(30, 26, 48);
    private static readonly Color PanelDark = new(22, 19, 36);
    private static readonly Color Muted = new(150, 152, 178);
    private static readonly Color Better = new(110, 235, 130);
    private static readonly Color Worse = new(255, 95, 105);
    private static readonly Color SickCol = new(120, 220, 255);
    private static readonly Color GoodCol = new(110, 230, 120);
    private static readonly Color BadCol = new(255, 200, 90);
    private static readonly Color ShitCol = new(230, 130, 70);
    private static readonly Color MissCol = new(255, 80, 90);

    /// <param name="focus">Attempt to expand first (default: <see cref="Run.Current"/>).</param>
    /// <param name="skipIntro">Show everything at once (reopened after a delete).</param>
    public SoloResultsScreen(Texture2D pixel, Run run, FreeplayAttempt? focus = null, bool skipIntro = false)
    {
        _pixel = pixel;
        _run = run;

        _cards.AddRange(run.Others);
        _cards.Add(run.Current);
        // Lowest score on the left; equal scores: the older run first.
        _cards.Sort((a, b) => a.Score != b.Score ? a.Score.CompareTo(b.Score) : a.DateUtc.CompareTo(b.DateUtc));
        _currentIdx = _cards.IndexOf(run.Current);
        _focus = _currentIdx;

        foreach (var a in run.Others)
            if (a.Ranked && (_previousBest == null || a.Score > _previousBest.Score)) _previousBest = a;
        if (_previousBest == null)
            foreach (var a in run.Others)
                if (!a.Failed && (_previousBest == null || a.Score > _previousBest.Score)) _previousBest = a;

        // Timing histogram over the outermost hit window (in real ms at the run's rate).
        float rate = run.Current.Rate > 0f ? run.Current.Rate : 1f;
        _histRangeMs = MathF.Ceiling(NoteRenderer.HIT_WINDOW * 1000f / rate);
        _histogram = new int[HistBins];
        foreach (float e in run.HitErrorsMs)
        {
            float t = (Math.Clamp(e, -_histRangeMs, _histRangeMs) + _histRangeMs) / (2f * _histRangeMs);
            _histogram[Math.Clamp((int)(t * HistBins), 0, HistBins - 1)]++;
        }

        if (focus != null && _cards.IndexOf(focus) is int fi and >= 0) _focus = fi;
        if (skipIntro) _time = SidesAt + 1f;
        else UiSfx.Play(run.Browse ? UiSound.Confirm : UiSound.Save);
    }

    private bool Browse => _run.Browse;

    /// <summary>The expanded attempt.</summary>
    public FreeplayAttempt FocusedAttempt => Focused;

    /// <summary>Attempt next to the expanded one (to expand after deleting it), or null when it's the only one.</summary>
    public FreeplayAttempt? FocusNeighbor
        => _focus + 1 < _cards.Count ? _cards[_focus + 1] : _focus > 0 ? _cards[_focus - 1] : null;

    /// <summary>Fades out and then returns <see cref="Choice.Continue"/> (the owner's close key).</summary>
    public void Close()
    {
        if (_closing) return;
        UiSfx.Play(UiSound.Back);
        _pending = Choice.Continue;
        _closing = true;
    }

    private FreeplayAttempt Focused => _cards[_focus];
    private bool FocusIsCurrent => _focus == _currentIdx;
    private bool IntroDone => _time >= SidesAt;

    /// <summary>Replay of the expanded attempt (when its file still exists), or null.</summary>
    public string? ReplayPath => Focused.HasReplay ? Focused.ReplayPath : null;

    // ══════════════════════════════════════════════════════════════════════
    // Update
    // ══════════════════════════════════════════════════════════════════════

    /// <summary>
    /// Per frame. <paramref name="pointer"/> is in render-target pixels, <paramref name="down"/> = the
    /// mouse button / a finger is down, <paramref name="wheel"/> = scroll wheel notches (+ = up / left).
    /// </summary>
    public Choice Update(float dt, KeyboardState kb, KeyboardState prevKb, Point pointer, bool down, float wheel)
    {
        _time += dt;
        _focusAge += dt;
        if (_closing)
        {
            _alpha = Math.Max(0f, _alpha - dt / 0.25f);
            return _alpha <= 0f ? _pending : Choice.None;
        }
        _alpha = Math.Min(1f, _time / FadeIn);
        _deleteArmed = Math.Max(0f, _deleteArmed - dt);
        if (!_dragging) _scrollVis += (_scroll - _scrollVis) * Math.Min(1f, dt * 12f);

        bool Pressed(Keys k) => kb.IsKeyDown(k) && !prevKb.IsKeyDown(k);
        _hovered = Choice.None;
        foreach (var (c, r) in _buttons) if (r.Contains(pointer)) _hovered = c;

        bool pressStart = down && !_prevDown, release = !down && _prevDown;
        _prevDown = down;
        if (_time < 0.4f) // a key / finger still held from the last note doesn't act on the screen
        {
            _pressActive = false;
            return Choice.None;
        }

        Choice pick = Choice.None;
        if (Pressed(Keys.Enter) || Pressed(Keys.Space))
        {
            if (!IntroDone) { SkipIntro(); return Choice.None; }
            pick = Choice.Continue;
        }
        else if (Pressed(Keys.Escape)) pick = Choice.Continue;
        else if (IntroDone && Pressed(Keys.Left)) SetFocus(_focus - 1);
        else if (IntroDone && Pressed(Keys.Right)) SetFocus(_focus + 1);

        // Wheel: scroll the row by one compact capsule per notch.
        if (wheel != 0f && IntroDone)
            _scroll = ClampScroll(_scroll - wheel * (SideW + Gap));

        // Pointer: drag / swipe scrolls; a press released without dragging is a click.
        if (pressStart)
        {
            _pressActive = true;
            _dragging = false;
            _downPos = pointer;
            _downScroll = _scroll;
        }
        else if (down && _pressActive && IntroDone)
        {
            int dx = pointer.X - _downPos.X;
            if (!_dragging && Math.Abs(dx) > 12f * U) _dragging = true;
            if (_dragging) _scrollVis = _scroll = ClampScroll(_downScroll - dx);
        }
        if (release && _pressActive)
        {
            _pressActive = false;
            if (!_dragging)
            {
                if (_hovered != Choice.None) pick = _hovered;
                else if (!IntroDone) SkipIntro();
                else
                    foreach (var (i, r) in _cardRects)
                        if (r.Contains(pointer)) { SetFocus(i); break; }
            }
            _dragging = false;
        }

        if (pick == Choice.WatchReplay && ReplayPath == null) pick = Choice.None;
        if (pick == Choice.Delete)
        {
            // First press arms it, the second (within 3 s, same attempt) deletes.
            if (_deleteArmed <= 0f) { _deleteArmed = 3f; UiSfx.Play(UiSound.Error); return Choice.None; }
            _deleteArmed = 0f;
            UiSfx.Play(UiSound.Confirm);
            return Choice.Delete; // instant: the owner removes it and reopens the screen
        }
        if (pick == Choice.None) return Choice.None;
        UiSfx.Play(pick == Choice.Continue ? UiSound.Back : UiSound.Confirm);
        if (pick == Choice.Retry) return Choice.Retry; // instant: the song reloads behind it
        _pending = pick;
        _closing = true;
        return Choice.None;
    }

    private void SkipIntro() => _time = Math.Max(_time, SidesAt);

    /// <summary>Expands capsule <paramref name="index"/>; it keeps its place on screen and then glides to the centre.</summary>
    private void SetFocus(int index)
    {
        index = Math.Clamp(index, 0, _cards.Count - 1);
        if (index == _focus) return;
        _deleteArmed = 0f;
        float screenOffset = CenterOf(index) - _scrollVis; // where it is now, relative to the screen centre
        _focus = index;
        _focusAge = 0f;
        _scroll = CenterOf(index);
        _scrollVis = _scroll - screenOffset;
        UiSfx.Play(UiSound.Move);
    }

    // ══════════════════════════════════════════════════════════════════════
    // Layout
    // ══════════════════════════════════════════════════════════════════════

    private float U => _lastSh / 720f;
    private float MainW => Math.Min(380f * U, _lastSw - 40f * U);
    // Browsing from Freeplay there is no timing graph (only the run that just ended has one), so the
    // capsule leaves its space out (78 px shorter).
    private float MainH => (Browse ? 522f : 600f) * U;
    private float SideW => 170f * U;
    private float SideH => 304f * U;
    private float Gap => 14f * U;
    private float BarH => 60f * U;

    private float WidthOf(int i) => i == _focus ? MainW : SideW;

    /// <summary>Centre of capsule <paramref name="i"/> along the row (row coordinates).</summary>
    private float CenterOf(int i)
    {
        float x = 0f;
        for (int k = 0; k < i; k++) x += WidthOf(k) + Gap;
        return x + WidthOf(i) / 2f;
    }

    private float ClampScroll(float v) => Math.Clamp(v, CenterOf(0), CenterOf(_cards.Count - 1));

    // ══════════════════════════════════════════════════════════════════════
    // Draw
    // ══════════════════════════════════════════════════════════════════════

    public void Draw(SpriteBatch sb, SpriteFont font, int sw, int sh)
    {
        _k = LineUnit / Math.Max(1, font.LineSpacing);
        bool relayout = !_laidOut || _lastSw != sw || _lastSh != sh;
        _lastSw = sw;
        _lastSh = sh;
        if (relayout) _scrollVis = _scroll = CenterOf(_focus); // the layout depends on the screen size
        _laidOut = true;
        float a = _alpha;
        float u = U;

        // ── Backdrop ─────────────────────────────────────────────────────
        Color accent = RankColor(RankOf(_run.Current));
        if (Browse)
        {
            Rect(sb, 0, 0, sw, sh, new Color(8, 6, 16) * (0.8f * a)); // over Freeplay: just dim it
        }
        else
        {
            Rect(sb, 0, 0, sw, sh, new Color(12, 10, 22) * a);
            for (int i = 0; i < 9; i++)
            {
                int x = (int)(i * 160 * u - (_time * 14f % (160 * u)));
                Rect(sb, x, 0, (int)(56 * u), sh, accent * (0.022f * a));
            }
            Rect(sb, 0, 0, sw, (int)(3 * u), accent * (0.7f * a));
        }

        // ── Capsules ─────────────────────────────────────────────────────
        float rowTop = 14f * u + Math.Max(0f, (sh - BarH - 28f * u - MainH) / 2f);
        float rowMid = rowTop + MainH / 2f;
        _cardRects.Clear();

        // Compact ones first (farthest first), so the expanded capsule and its neighbours sit on top.
        var order = new List<int>();
        for (int i = 0; i < _cards.Count; i++) if (i != _focus) order.Add(i);
        order.Sort((x, y) => Math.Abs(y - _focus).CompareTo(Math.Abs(x - _focus)));
        float focusX = sw / 2f + (CenterOf(_focus) - _scrollVis);
        foreach (int i in order)
        {
            // Intro: they slide out from behind the expanded capsule, nearest first.
            float t = Math.Clamp((_time - SidesAt - 0.05f * Math.Abs(i - _focus)) / 0.45f, 0f, 1f);
            if (t <= 0f) continue;
            float e = 1f - MathF.Pow(1f - t, 3f);
            float cx = MathHelper.Lerp(focusX, sw / 2f + (CenterOf(i) - _scrollVis), e);
            var r = new Rectangle((int)(cx - SideW / 2f), (int)(rowMid - SideH / 2f), (int)SideW, (int)SideH);
            if (r.Right < 0 || r.Left > sw) continue;
            DrawCompact(sb, font, i, r, a * e);
            _cardRects.Add((i, r));
        }

        float intro = Math.Clamp(_time / 0.35f, 0f, 1f);
        float pop = 0.92f + 0.08f * (1f - MathF.Pow(1f - intro, 3f));
        var mr = new Rectangle((int)(focusX - MainW * pop / 2f), (int)(rowMid - MainH * pop / 2f), (int)(MainW * pop), (int)(MainH * pop));
        DrawExpanded(sb, font, mr, a * intro);
        _cardRects.Add((_focus, mr));

        // ── Bottom bar ───────────────────────────────────────────────────
        DrawBar(sb, font, sw, sh, a);
    }

    private void DrawBar(SpriteBatch sb, SpriteFont font, int sw, int sh, float a)
    {
        float u = U;
        int barY = (int)(sh - BarH);
        Rect(sb, 0, barY, sw, (int)BarH, PanelDark * (0.97f * a));
        Rect(sb, 0, barY, sw, 1, Color.White * (0.08f * a));
        _buttons.Clear();
        bool touch = PointerInput.IsTouch;
        float bh = 40f * u, by = barY + (BarH - bh) / 2f;

        var back = new Rectangle((int)(16 * u), (int)by, (int)(150 * u), (int)bh);
        DrawButton(sb, font, Choice.Continue, back, touch ? "BACK" : "ESC  BACK", new Color(255, 102, 170), a);

        var list = new List<(Choice C, string Label, Color Col)>();
        if (!Browse) list.Add((Choice.Retry, "RETRY", new Color(255, 215, 80)));
        if (ReplayPath != null) list.Add((Choice.WatchReplay, "WATCH REPLAY", SickCol));
        if (Browse)
            list.Add((Choice.Delete, _deleteArmed > 0f ? "CONFIRM DELETE?" : "DELETE ATTEMPT", Worse));
        float bw = 210f * u;
        float bx = sw / 2f - (list.Count * bw + (list.Count - 1) * 12f * u) / 2f;
        foreach (var (c, label, col) in list)
        {
            DrawButton(sb, font, c, new Rectangle((int)bx, (int)by, (int)bw, (int)bh), label, col, a);
            bx += bw + 12f * u;
        }

        if (!touch && _cards.Count > 1)
        {
            string hint = $"LEFT / RIGHT  attempts ({_cards.Count})";
            float w = Measure(font, hint) * 0.36f * u;
            Text(sb, font, hint, sw - 18 * u - w, barY + (BarH - LineUnit * 0.36f * u) / 2f, 0.36f * u, Muted * (a * (IntroDone ? 1f : 0f)));
        }
    }

    private void DrawButton(SpriteBatch sb, SpriteFont font, Choice c, Rectangle r, string label, Color col, float a)
    {
        float u = U;
        _buttons[c] = r;
        bool hov = _hovered == c;
        Rect(sb, r.X, r.Y, r.Width, r.Height, (hov ? new Color(50, 44, 78) : Panel) * a);
        Rect(sb, r.X, r.Bottom - (int)(3 * u), r.Width, (int)(3 * u), col * (hov ? a : 0.65f * a));
        Centered(sb, font, label, r.X + r.Width / 2f, r.Y + (r.Height - LineUnit * 0.46f * u) / 2f, 0.46f * u, (hov ? Color.White : col) * a);
    }

    // ── Expanded capsule ────────────────────────────────────────────────────

    private void DrawExpanded(SpriteBatch sb, SpriteFont font, Rectangle r, float a)
    {
        if (a <= 0f) return;
        float u = U;
        var at = Focused;
        bool current = FocusIsCurrent;
        // Animation clock: the run that just ended plays the full intro; another capsule a short one.
        // (_focusAge starts above _time and only resets when another capsule is expanded.)
        bool opening = _focusAge >= _time;
        float t = opening ? _time : 0.3f + _focusAge * 2.2f;
        float fade = opening ? 1f : Math.Clamp(_focusAge / 0.18f, 0f, 1f);
        a *= fade;
        if (a <= 0f) return;

        string rank = RankOf(at);
        Color rankCol = RankColor(rank);
        Rect(sb, r.X, r.Y, r.Width, r.Height, Panel * (0.98f * a));
        Rect(sb, r.X, r.Y, r.Width, (int)(4 * u), rankCol * a);
        Outline(sb, r, rankCol * (0.35f * a));

        float cx = r.X + r.Width / 2f;
        float y = r.Y + 12 * u;
        float innerW = r.Width - 32 * u;

        // Place among the attempts
        int place = _cards.Count - _focus;
        string placeText = $"#{place}";
        float pw = Measure(font, placeText) * 0.66f * u;
        string ofText = _cards.Count > 1 ? $" / {_cards.Count}" : "";
        float ow = Measure(font, ofText) * 0.4f * u;
        float px = cx - (pw + ow) / 2f;
        Text(sb, font, placeText, px, y, 0.66f * u, Color.White * a);
        if (ofText.Length > 0) Text(sb, font, ofText, px + pw, y + 9 * u, 0.4f * u, Muted * a);
        if (current && _run.NewBest)
        {
            float pulse = 0.75f + 0.25f * MathF.Sin(_time * 5f);
            Pill(sb, font, "NEW BEST", r.Right - 12 * u, y + 2 * u, Better, a * pulse, rightAlign: true);
        }
        if (at.Failed) Pill(sb, font, $"FAILED {at.Progress * 100f:0}%", r.X + 12 * u, y + 2 * u, Worse, a);
        else if (Math.Abs(at.Rate - 1f) > 0.001f) Pill(sb, font, $"{at.Rate:0.00}x", r.X + 12 * u, y + 2 * u, BadCol, a);
        y += 32 * u;

        // Song, mapper
        CenteredFit(sb, font, _run.SongName, cx, y, innerW, 0.56f * u, Color.White * a);
        y += 24 * u;
        if (_run.Mapper.Length > 0)
        {
            CenteredFit(sb, font, $"mapped by {_run.Mapper}", cx, y, innerW, 0.38f * u, Muted * a);
            y += 18 * u;
        }

        // Accuracy wheel
        float radius = 80f * u;
        float wheelCy = y + 22 * u + radius;
        float fill = Ease(Math.Clamp((t - WheelFrom) / (WheelTo - WheelFrom), 0f, 1f));
        DrawWheel(sb, font, new Vector2(cx, wheelCy), radius, at.Accuracy, fill, a);
        float rt = Math.Clamp((t - RankAt) / 0.3f, 0f, 1f);
        if (rt > 0f)
        {
            float scale = (rt < 1f ? MathHelper.Lerp(1.9f, 1f, Ease(rt)) : 1f) * 2.1f * u * _k;
            Vector2 sz = font.MeasureUi(rank) * scale;
            var pos = new Vector2(cx - sz.X / 2f, wheelCy - sz.Y / 2f);
            sb.DrawUiString(font, rank, pos + new Vector2(3 * u, 4 * u), Color.Black * (0.5f * a * rt), 0f, Vector2.Zero, scale, SpriteEffects.None, 0f);
            sb.DrawUiString(font, rank, pos, rankCol * (a * rt), 0f, Vector2.Zero, scale, SpriteEffects.None, 0f);
        }
        else
        {
            // Accuracy counting up inside the wheel until the letter lands
            Centered(sb, font, $"{at.Accuracy * fill:0.00}%", cx, wheelCy - LineUnit * 0.25f * u, 0.5f * u, Color.White * (a * 0.8f));
        }
        y = wheelCy + radius + 10 * u;

        // Score (counts up), with its difference
        float scoreT = Ease(Math.Clamp((t - 0.2f) / 1.2f, 0f, 1f));
        string score = ((int)Math.Round(at.Score * scoreT)).ToString("N0", CultureInfo.InvariantCulture);
        float ss = 1.05f * u;
        float sw2 = Measure(font, score) * ss;
        Text(sb, font, score, cx - sw2 / 2f, y, ss, Color.White * a);
        // Ran just now: vs the previous best. Browsing: the best vs the next best, the rest vs the best.
        var reference = current ? _previousBest : _cards[_currentIdx];
        if (reference != null && scoreT >= 1f)
            Delta(sb, font, at.Score - reference.Score, true, v => v.ToString("N0", CultureInfo.InvariantCulture),
                cx + sw2 / 2f + 8 * u, y + LineUnit * ss * 0.5f - LineUnit * 0.19f * u, 0.38f * u, a);
        y += LineUnit * ss + 2 * u;

        // Stars + difficulty
        if (_run.Stars >= 0)
        {
            string st = _run.Stars.ToString("0.00", CultureInfo.InvariantCulture) + " *";
            float stw = Measure(font, st) * 0.4f * u + 16 * u, sth = 20 * u;
            Rect(sb, (int)(cx - stw / 2f), (int)y, (int)stw, (int)sth, FreeplayMenu.StarColor(_run.Stars) * a);
            Centered(sb, font, st, cx, y + (sth - LineUnit * 0.4f * u) / 2f, 0.4f * u, FreeplayMenu.StarTextColor(_run.Stars) * a, shadow: false);
            y += sth + 4 * u;
        }
        if (_run.Difficulty.Length > 0)
        {
            CenteredFit(sb, font, _run.Difficulty, cx, y, innerW, 0.38f * u, Muted * a);
            y += 18 * u;
        }
        y = Math.Max(y, r.Y + 346 * u);

        // Stats: accuracy, max combo, combo rating
        float st1 = Math.Clamp((t - 0.5f) / 0.4f, 0f, 1f) * a;
        float x0 = r.X + 14 * u, rowW = r.Width - 28 * u;
        float cw = rowW / 3f;
        int notes = at.ChartNotes > 0 ? at.ChartNotes : _run.ChartNotes;
        string combo = notes > 0 ? $"{at.MaxCombo:N0}/{notes:N0}" : $"{at.MaxCombo:N0}x";
        StatCell(sb, font, x0, y, cw, "ACCURACY", $"{at.Accuracy:0.00}%", Color.White, st1,
            reference == null ? null : at.Accuracy - reference.Accuracy, true, v => $"{v:0.00}%");
        StatCell(sb, font, x0 + cw, y, cw, "MAX COMBO", combo,
            notes > 0 && at.MaxCombo >= notes ? Better : Color.White, st1,
            reference == null ? null : at.MaxCombo - reference.MaxCombo, true, v => $"{v:N0}");
        var (label, labelCol) = ComboRating(at);
        StatCell(sb, font, x0 + cw * 2, y, cw, "RATING", label, labelCol, st1, null, true, null);
        y += 54 * u;

        // Judgements
        float st2 = Math.Clamp((t - 0.65f) / 0.4f, 0f, 1f) * a;
        float jw = rowW / 5f;
        var judgements = new (string L, int V, int? R, bool Higher, Color C)[]
        {
            ("SICK", at.Sicks, reference?.Sicks, true, SickCol),
            ("GOOD", at.Goods, reference?.Goods, false, GoodCol),
            ("BAD", at.Bads, reference?.Bads, false, BadCol),
            ("SHIT", at.Shits, reference?.Shits, false, ShitCol),
            ("MISS", at.Misses, reference?.Misses, false, MissCol),
        };
        for (int i = 0; i < judgements.Length; i++)
        {
            var j = judgements[i];
            StatCell(sb, font, x0 + jw * i, y, jw, j.L, j.V.ToString("N0", CultureInfo.InvariantCulture), Color.White, st2,
                j.R is int rv ? j.V - rv : null, j.Higher, v => $"{v:N0}", j.C);
        }
        y += 56 * u;

        // Timing distribution (browsing: only the average error / unstable rate line, no graph)
        float st3 = Math.Clamp((t - 0.8f) / 0.4f, 0f, 1f) * a;
        if (Browse)
        {
            if (st3 > 0f) DrawErrorLine(sb, font, at, x0, y, rowW, st3);
            y += 20 * u;
        }
        else
        {
            DrawTiming(sb, font, at, current, x0, y, rowW, t, st3);
            y += 98 * u;
        }

        // Footer: when it was played / why it wasn't saved, and what the +/- compare against
        string when;
        Color whenCol = Muted;
        if (current && _run.NotSavedReason.Length > 0) { when = _run.NotSavedReason; whenCol = BadCol; }
        else when = "Played " + at.DateUtc.ToLocalTime().ToString("dd MMM yyyy  HH:mm", CultureInfo.InvariantCulture);
        if (reference != null)
            when += Browse ? (current ? "   |   +/- vs next best" : "   |   +/- vs your best")
                           : (current ? "   |   +/- vs your best" : "   |   +/- vs this run");
        CenteredFit(sb, font, when, cx, Math.Min(y, r.Bottom - 22 * u), innerW, 0.34f * u, whenCol * a);
    }

    /// <summary>Ring of rank bands with the accuracy filled in (0 = top, clockwise), and the band letters around it.</summary>
    private void DrawWheel(SpriteBatch sb, SpriteFont font, Vector2 c, float radius, float accuracy, float fill, float a)
    {
        float u = U;
        float thin = 4f * u, thick = 16f * u;
        float gap = 0.0035f;
        float innerR = radius - 14f * u;
        Arc(sb, c, innerR, thick, 0f, 1f, Color.Lerp(Panel, Color.Black, 0.35f) * a);
        float shown = accuracy / 100f * fill;
        foreach (var (from, to, rank) in Bands)
        {
            Color col = RankColor(rank);
            float f0 = from / 100f, f1 = to / 100f;
            Arc(sb, c, radius, thin, f0 + gap, f1 - gap, col * (0.85f * a));
            if (shown > f0) Arc(sb, c, innerR, thick, f0, Math.Min(f1, shown), col * a);

            // Letter badge outside the ring (S+ sits a bit further out, its band is tiny)
            float mid = (f0 + f1) / 2f;
            float ang = mid * MathHelper.TwoPi - MathHelper.PiOver2;
            float lr = radius + (rank == "S+" ? 22f : 13f) * u;
            var p = c + new Vector2(MathF.Cos(ang), MathF.Sin(ang)) * lr;
            float sc = 0.3f * u;
            Vector2 sz = font.MeasureUi(rank) * (sc * _k);
            Rect(sb, (int)(p.X - sz.X / 2f - 4 * u), (int)(p.Y - sz.Y / 2f), (int)(sz.X + 8 * u), (int)sz.Y, col * (0.9f * a));
            sb.DrawUiString(font, rank, p - sz / 2f, new Color(20, 18, 30) * a, 0f, Vector2.Zero, sc * _k, SpriteEffects.None, 0f);
        }
    }

    private void DrawTiming(SpriteBatch sb, SpriteFont font, FreeplayAttempt at, bool current, float x, float y, float w, float t, float a)
    {
        if (a <= 0f) return;
        float u = U;
        Text(sb, font, "TIMING DISTRIBUTION", x, y, 0.34f * u, Muted * a);
        float gy = y + 18 * u, gh = 46 * u;
        Rect(sb, (int)x, (int)gy, (int)w, (int)gh, PanelDark * a);

        if (current && _run.HitErrorsMs.Count > 0)
        {
            int max = 1;
            foreach (int n in _histogram) max = Math.Max(max, n);
            float grow = Ease(Math.Clamp((t - 0.9f) / 0.6f, 0f, 1f));
            float bw = w / HistBins;
            float rate = _run.Current.Rate > 0f ? _run.Current.Rate : 1f;
            for (int i = 0; i < HistBins; i++)
            {
                float centreMs = -_histRangeMs + (i + 0.5f) * (2f * _histRangeMs / HistBins);
                float song = Math.Abs(centreMs) * rate / 1000f; // judged in song time
                Color col = song < NoteRenderer.W_SICK ? SickCol : song < NoteRenderer.W_GOOD ? GoodCol : song < NoteRenderer.W_BAD ? BadCol : ShitCol;
                float h = _histogram[i] == 0 ? 2f * u : Math.Max(2f * u, (gh - 6 * u) * _histogram[i] / max * grow);
                Rect(sb, (int)(x + i * bw + bw * 0.15f), (int)(gy + gh - h), Math.Max(1, (int)(bw * 0.7f)), (int)h,
                    (_histogram[i] == 0 ? Color.White * 0.12f : col) * a);
            }
            Rect(sb, (int)(x + w / 2f), (int)gy, Math.Max(1, (int)(1 * u)), (int)gh, Color.White * (0.6f * a));
        }
        else
        {
            string msg = at.HitErrorMs == null ? "No timing data for this attempt"
                : "Graph only for the run that just ended";
            Centered(sb, font, msg, x + w / 2f, gy + (gh - LineUnit * 0.32f * u) / 2f, 0.32f * u, Muted * (0.8f * a));
        }

        // Axis
        float ay = gy + gh + 2 * u;
        string range = _histRangeMs.ToString("0", CultureInfo.InvariantCulture);
        Text(sb, font, $"-{range} early", x, ay, 0.3f * u, Muted * (0.8f * a));
        Centered(sb, font, "0", x + w / 2f, ay, 0.3f * u, Muted * (0.8f * a));
        string late = $"late +{range}";
        Text(sb, font, late, x + w - Measure(font, late) * 0.3f * u, ay, 0.3f * u, Muted * (0.8f * a));

        // Average error and unstable rate
        DrawErrorLine(sb, font, at, x, ay + 14 * u, w, a);
    }

    /// <summary>"Avg. error" on the left and "Unstable rate" on the right of one line.</summary>
    private void DrawErrorLine(SpriteBatch sb, SpriteFont font, FreeplayAttempt at, float x, float ly, float w, float a)
    {
        float u = U;
        string avg = at.HitErrorMs is float m
            ? Math.Abs(m) < 0.05f ? "Avg. error  0 ms" : $"Avg. error  {Math.Abs(m):0.00} ms {(m < 0 ? "early" : "late")}"
            : "Avg. error  -";
        string ur = at.UnstableRate is float v ? $"Unstable rate  {v:0.00}" : "Unstable rate  -";
        Text(sb, font, avg, x, ly, 0.34f * u, Color.White * (0.85f * a));
        Text(sb, font, ur, x + w - Measure(font, ur) * 0.34f * u, ly, 0.34f * u, Color.White * (0.85f * a));
    }

    private void StatCell(SpriteBatch sb, SpriteFont font, float x, float y, float w, string label, string value, Color valueCol,
        float a, float? delta, bool higherBetter, Func<float, string>? format, Color? labelCol = null)
    {
        if (a <= 0f) return;
        float u = U;
        float cx = x + w / 2f;
        Rect(sb, (int)(x + 2 * u), (int)y, (int)(w - 4 * u), (int)(48 * u), PanelDark * a);
        Rect(sb, (int)(x + 2 * u), (int)y, (int)(w - 4 * u), (int)(2 * u), (labelCol ?? Muted) * (0.7f * a));
        Centered(sb, font, label, cx, y + 5 * u, 0.3f * u, (labelCol ?? Muted) * a);
        CenteredFit(sb, font, value, cx, y + 17 * u, w - 10 * u, 0.46f * u, valueCol * a);
        if (delta is float d && format != null)
        {
            string txt = DeltaText(d, format, out Color col, higherBetter);
            if (txt.Length > 0) Centered(sb, font, txt, cx, y + 35 * u, 0.28f * u, col * a, shadow: false);
        }
    }

    // ── Compact capsule ─────────────────────────────────────────────────────

    private void DrawCompact(SpriteBatch sb, SpriteFont font, int index, Rectangle r, float a)
    {
        if (a <= 0f) return;
        float u = U;
        var at = _cards[index];
        bool current = index == _currentIdx;
        string rank = RankOf(at);
        Color rankCol = RankColor(rank);
        Rect(sb, r.X, r.Y, r.Width, r.Height, Panel * (0.94f * a));
        Rect(sb, r.X, r.Y, r.Width, (int)(3 * u), rankCol * (0.8f * a));
        Outline(sb, r, (current ? SickCol * 0.8f : Color.White * 0.06f) * a);

        float cx = r.X + r.Width / 2f;
        float y = r.Y + 10 * u;
        Centered(sb, font, $"#{_cards.Count - index}", cx, y, 0.5f * u, Color.White * a);
        y += 24 * u;
        Centered(sb, font, at.DateUtc.ToLocalTime().ToString("dd MMM yy  HH:mm", CultureInfo.InvariantCulture), cx, y, 0.3f * u, Muted * a);
        y += 16 * u;
        string tag = current ? (Browse ? "BEST" : "THIS RUN") : at.Failed ? $"FAILED {at.Progress * 100f:0}%" : Math.Abs(at.Rate - 1f) > 0.001f ? $"{at.Rate:0.00}x" : "";
        if (tag.Length > 0)
            Centered(sb, font, tag, cx, y, 0.3f * u, (current ? (Browse ? Better : SickCol) : at.Failed ? Worse : BadCol) * a);
        y += 20 * u;

        float lx = r.X + 12 * u, rx = r.Right - 12 * u;
        void Row(string label, string value, Color lc)
        {
            Text(sb, font, label, lx, y, 0.32f * u, lc * a);
            Text(sb, font, value, rx - Measure(font, value) * 0.32f * u, y, 0.32f * u, Color.White * a);
            y += 17 * u;
        }
        Row("Sick", at.Sicks.ToString("N0", CultureInfo.InvariantCulture), SickCol);
        Row("Good", at.Goods.ToString("N0", CultureInfo.InvariantCulture), GoodCol);
        Row("Bad", at.Bads.ToString("N0", CultureInfo.InvariantCulture), BadCol);
        Row("Shit", at.Shits.ToString("N0", CultureInfo.InvariantCulture), ShitCol);
        Row("Miss", at.Misses.ToString("N0", CultureInfo.InvariantCulture), MissCol);
        y += 4 * u;
        Rect(sb, (int)lx, (int)y, (int)(rx - lx), 1, Color.White * (0.1f * a));
        y += 8 * u;
        Row("Max Combo", $"{at.MaxCombo:N0}x", Muted);
        Row("Accuracy", $"{at.Accuracy:0.00}%", Muted);

        // Score, rank and how it compares with the expanded capsule
        float by = r.Bottom - 78 * u;
        Centered(sb, font, at.Score.ToString("N0", CultureInfo.InvariantCulture), cx, by, 0.6f * u, Color.White * a);
        by += 28 * u;
        var (label, labelCol) = ComboRating(at);
        string badge = label.Length > 0 && label != "CLEAR" && label != "FAILED" ? $"{rank}  {label}" : rank;
        float bw = Measure(font, badge) * 0.36f * u + 18 * u, bh = 18 * u;
        Rect(sb, (int)(cx - bw / 2f), (int)by, (int)bw, (int)bh, rankCol * (0.9f * a));
        Centered(sb, font, badge, cx, by + (bh - LineUnit * 0.36f * u) / 2f, 0.36f * u, new Color(20, 18, 30) * a, shadow: false);
        by += bh + 5 * u;
        string d = DeltaText(at.Score - Focused.Score, v => v.ToString("N0", CultureInfo.InvariantCulture), out Color dc, true);
        if (d.Length > 0) Centered(sb, font, d, cx, by, 0.3f * u, dc * a, shadow: false);
    }

    // ── Helpers ─────────────────────────────────────────────────────────────

    private static string RankOf(FreeplayAttempt at)
        => at.Sicks + at.Goods + at.Bads + at.Shits + at.Misses == 0 ? "-" : FreeplayData.RankFor(at.Accuracy);

    /// <summary>SFC / GFC / FC, CLEAR (finished with misses) or FAILED, with its colour.</summary>
    private static (string Label, Color Color) ComboRating(FreeplayAttempt at)
    {
        if (at.Failed) return ("FAILED", new Color(255, 95, 105));
        return at.ComboLabel switch
        {
            "SFC" => ("SFC", new Color(120, 220, 255)),
            "GFC" => ("GFC", new Color(110, 230, 120)),
            "FC" => ("FC", new Color(255, 215, 80)),
            _ => ("CLEAR", new Color(185, 190, 212)),
        };
    }

    /// <summary>"+12" / "-3" coloured green when it's an improvement, red when worse; "" when equal.</summary>
    private static string DeltaText(float d, Func<float, string> format, out Color col, bool higherBetter)
    {
        col = Muted;
        if (Math.Abs(d) < 0.005f) return "";
        bool better = higherBetter ? d > 0 : d < 0;
        col = better ? Better : Worse;
        return (d > 0 ? "+" : "-") + format(Math.Abs(d));
    }

    private void Delta(SpriteBatch sb, SpriteFont font, float d, bool higherBetter, Func<float, string> format, float x, float y, float scale, float a)
    {
        string txt = DeltaText(d, format, out Color col, higherBetter);
        if (txt.Length > 0) Text(sb, font, txt, x, y, scale, col * a, shadow: false);
    }

    private void Pill(SpriteBatch sb, SpriteFont font, string text, float x, float y, Color col, float a, bool rightAlign = false)
    {
        float u = U;
        float w = Measure(font, text) * 0.3f * u + 12 * u, h = 16 * u;
        if (rightAlign) x -= w;
        Rect(sb, (int)x, (int)y, (int)w, (int)h, col * (0.9f * a));
        Text(sb, font, text, x + 6 * u, y + (h - LineUnit * 0.3f * u) / 2f, 0.3f * u, new Color(20, 18, 30) * a, shadow: false);
    }

    private static float Ease(float t) => 1f - MathF.Pow(1f - t, 3f);

    public static Color RankColor(string rank) => rank switch
    {
        "S+" => new Color(255, 230, 120),
        "S" => new Color(255, 200, 70),
        "A" => new Color(110, 230, 120),
        "B" => new Color(100, 180, 255),
        "C" => new Color(200, 120, 255),
        "-" => new Color(150, 150, 170),
        _ => new Color(255, 90, 90),
    };

    /// <summary>Arc of a ring from <paramref name="f0"/> to <paramref name="f1"/> (fractions of a turn, 0 = top, clockwise).</summary>
    private void Arc(SpriteBatch sb, Vector2 c, float radius, float thick, float f0, float f1, Color col)
    {
        if (f1 <= f0) return;
        int steps = Math.Max(1, (int)MathF.Ceiling((f1 - f0) * 200f));
        float outer = radius + thick / 2f;
        for (int i = 0; i < steps; i++)
        {
            float a0 = MathHelper.TwoPi * (f0 + (f1 - f0) * i / steps) - MathHelper.PiOver2;
            float a1 = MathHelper.TwoPi * (f0 + (f1 - f0) * (i + 1) / steps) - MathHelper.PiOver2;
            float am = (a0 + a1) / 2f;
            var p = c + new Vector2(MathF.Cos(am), MathF.Sin(am)) * radius;
            float len = outer * (a1 - a0) + 0.75f;
            sb.Draw(_pixel, p, null, col, am + MathHelper.PiOver2, new Vector2(0.5f, 0.5f), new Vector2(len, thick), SpriteEffects.None, 0f);
        }
    }

    private void Outline(SpriteBatch sb, Rectangle r, Color c)
    {
        Rect(sb, r.X, r.Y, r.Width, 1, c);
        Rect(sb, r.X, r.Bottom - 1, r.Width, 1, c);
        Rect(sb, r.X, r.Y, 1, r.Height, c);
        Rect(sb, r.Right - 1, r.Y, 1, r.Height, c);
    }

    /// <summary>Text sizes here are in units of a 40 px line: the font's own size doesn't matter.</summary>
    private const float LineUnit = 40f;
    private float _k = 1f;

    /// <summary>Width of <paramref name="text"/> at scale 1 (in <see cref="LineUnit"/> terms).</summary>
    private float Measure(SpriteFont font, string text) => font.MeasureUi(ErrorOverlay.Safe(text)).X * _k;

    private void Rect(SpriteBatch sb, int x, int y, int w, int h, Color c)
    {
        if (w <= 0 || h <= 0) return;
        sb.Draw(_pixel, new Rectangle(x, y, w, h), c);
    }

    private void Text(SpriteBatch sb, SpriteFont font, string text, float x, float y, float scale, Color color, bool shadow = true)
    {
        text = ErrorOverlay.Safe(text);
        scale *= _k;
        if (shadow)
            sb.DrawUiString(font, text, new Vector2(x + 1.5f, y + 1.5f), Color.Black * (color.A / 255f * 0.55f), 0f, Vector2.Zero, scale, SpriteEffects.None, 0f);
        sb.DrawUiString(font, text, new Vector2(x, y), color, 0f, Vector2.Zero, scale, SpriteEffects.None, 0f);
    }

    private void Centered(SpriteBatch sb, SpriteFont font, string text, float cx, float y, float scale, Color color, bool shadow = true)
    {
        text = ErrorOverlay.Safe(text);
        float w = Measure(font, text) * scale;
        Text(sb, font, text, cx - w / 2f, y, scale, color, shadow);
    }

    private void CenteredFit(SpriteBatch sb, SpriteFont font, string text, float cx, float y, float maxW, float scale, Color color)
    {
        text = ErrorOverlay.Safe(text);
        float w = Measure(font, text) * scale;
        if (w > maxW && w > 0) scale *= maxW / w;
        Centered(sb, font, text, cx, y, scale, color);
    }
}
