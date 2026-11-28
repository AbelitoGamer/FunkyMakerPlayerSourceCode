using System;
using System.Collections.Generic;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using Microsoft.Xna.Framework.Input;

namespace FunkyMakerPlayer;

/// <summary>
/// Skip Breaks (fork, Settings → GAMEPLAY, off by default): during a stretch of more than
/// <see cref="BreakMinGap"/> s with no arrows in the chart, Space (Android: tapping the prompt) jumps
/// to <see cref="BreakLeadIn"/> s before the next arrow; after the last arrow it jumps to the end of
/// the song. A prompt above the health bar says "Press Space to Skip the Intro / Intermission /
/// Outro".
/// <para>Unlike the Timeskip it is not penalised: the attempt still counts (personal best included)
/// and the replay stays valid, because every skip is saved in the replay
/// (<see cref="Replay.ReplayTrack.Skips"/>) and applied again at the same song time when it is
/// watched. Solo only (never online, where it would desync the song from the other players).</para>
/// </summary>
public partial class Game1
{
    /// <summary>Setting: Skip Breaks (saved as <c>SkipBreaks</c>).</summary>
    public static bool SkipBreaks = false;

    private const float BreakMinGap = 3f;      // s without arrows before a skip is offered
    private const float BreakLeadIn = 1f;      // s left before the next arrow after a skip
    private const float BreakMinSaving = 0.5f; // don't offer a skip that saves less than this
    // Offered only once the previous arrow is out of its hit window (a miss there is already counted,
    // a skip can't be used to drop it)
    private const float BreakAfterLastNote = NoteRenderer.HIT_WINDOW + 0.1f;

    private enum BreakKind { None, Intro, Intermission, Outro }

    // Every note of every strumline sorted by start, and the latest end time up to each one
    private float[] _breakStarts = Array.Empty<float>();
    private float[] _breakBusyUntil = Array.Empty<float>();

    private BreakKind _breakKind;
    private BreakKind _breakShownKind;   // kept while the prompt fades out
    private float _breakTarget;
    private float _breakPromptAlpha;
    private Rectangle _breakPromptRect;  // logical (HUD) coordinates of the last drawn prompt
    private int _replaySkipCursor;

    /// <summary>Builds the note table used to find breaks (call after the chart is loaded).</summary>
    private void PrepareBreakSkips()
    {
        _breakKind = _breakShownKind = BreakKind.None;
        _breakPromptAlpha = 0f;
        _replaySkipCursor = 0;
        var notes = new List<(float Start, float End)>();
        if (_level != null)
        {
            foreach (var sl in _level.Data.Strumlines)
                foreach (var lane in sl)
                    foreach (var note in lane)
                    {
                        float s = _level.Data.ToSeconds(note.Start);
                        notes.Add((s, s + Math.Max(0f, _level.Data.ToSeconds(note.Duration))));
                    }
        }
        notes.Sort((a, b) => a.Start.CompareTo(b.Start));
        _breakStarts = new float[notes.Count];
        _breakBusyUntil = new float[notes.Count];
        float busy = 0f;
        for (int i = 0; i < notes.Count; i++)
        {
            _breakStarts[i] = notes[i].Start;
            busy = Math.Max(busy, notes[i].End);
            _breakBusyUntil[i] = busy;
        }
    }

    /// <summary>The break <paramref name="t"/> is in, and where a skip would land.</summary>
    private (BreakKind Kind, float Target) FindBreak(float t)
    {
        int n = _breakStarts.Length;
        if (n == 0) return (BreakKind.None, 0f);

        // First note that starts after t
        int lo = 0, hi = n;
        while (lo < hi)
        {
            int mid = (lo + hi) / 2;
            if (_breakStarts[mid] <= t) lo = mid + 1; else hi = mid;
        }
        int next = lo;
        float prevEnd = next > 0 ? _breakBusyUntil[next - 1] : 0f;
        if (next > 0 && t < prevEnd + BreakAfterLastNote) return (BreakKind.None, 0f); // a note is still going

        float gapEnd = next < n ? _breakStarts[next] : _songEndTime;
        if (gapEnd >= float.MaxValue || gapEnd - prevEnd <= BreakMinGap) return (BreakKind.None, 0f);

        float target = next < n ? gapEnd - BreakLeadIn : gapEnd;
        if (target - t < BreakMinSaving) return (BreakKind.None, 0f);
        var kind = next == 0 ? BreakKind.Intro : next < n ? BreakKind.Intermission : BreakKind.Outro;
        return (kind, target);
    }

    private bool CanSkipBreaksNow =>
        SkipBreaks && _level != null && _started && !_paused && !_inPauseMenu && _songTime >= 0f &&
        !_isMultiplayerMatch && !_onlineSpectating && _songTransition == null &&
        !(_gameOverOverlay?.IsActive ?? false) && !(_settingsManager?.IsActive ?? false);

    /// <summary>Every gameplay frame: finds the current break, Space skips it; replays re-apply recorded skips.</summary>
    private void UpdateBreakSkip(KeyboardState kb, float dt)
    {
        if (IsWatchingReplay)
        {
            _breakKind = BreakKind.None;
            var skips = _activeReplay?.Local?.Skips;
            if (skips != null && _replaySkipCursor < skips.Count && _started && !_paused &&
                _songTime >= skips[_replaySkipCursor].From)
            {
                var skip = skips[_replaySkipCursor++];
                if (skip.To > _songTime) SeekSong(skip.To, breakSkip: true);
            }
        }
        else
        {
            (_breakKind, _breakTarget) = CanSkipBreaksNow ? FindBreak(_songTime) : (BreakKind.None, 0f);
            if (_breakKind != BreakKind.None && kb.IsKeyDown(Keys.Space) && !_prevKb.IsKeyDown(Keys.Space))
            {
                float from = _songTime;
                SeekSong(_breakTarget, breakSkip: true);
                _replayRecorder?.Track.Skips.Add(new Replay.ReplaySkip(from, _songTime));
                UI.UiSfx.Play(UI.UiSound.Confirm);
                _breakKind = BreakKind.None;
                _breakPromptAlpha = 0f; // gone at once, no fade over the next section
            }
        }

        if (_breakKind != BreakKind.None) _breakShownKind = _breakKind;
        float target = _breakKind != BreakKind.None ? 1f : 0f;
        _breakPromptAlpha = MathHelper.Clamp(_breakPromptAlpha + (target > _breakPromptAlpha ? dt * 4f : -dt * 5f), 0f, 1f);
        UpdateBreakSkipTouchButton();
    }

    /// <summary>Android: the prompt is a button (its touches send Space and never reach a lane).</summary>
    private void UpdateBreakSkipTouchButton()
    {
#if ANDROID
        if (_touchControls == null) return;
        if (_breakKind == BreakKind.None || _breakPromptRect.Width <= 0)
        {
            _touchControls.SkipButtonBounds = null;
            return;
        }
        int bw = GraphicsDevice.PresentationParameters.BackBufferWidth;
        int bh = GraphicsDevice.PresentationParameters.BackBufferHeight;
        int tw = Microsoft.Xna.Framework.Input.Touch.TouchPanel.DisplayWidth;
        int th = Microsoft.Xna.Framework.Input.Touch.TouchPanel.DisplayHeight;
        if (tw <= 0 || th <= 0) { tw = bw; th = bh; }
        var (dst, _) = ComputeLetterbox(tw, th);
        float k = dst.Width / (float)Math.Max(1, _rtWidth);
        var r = _breakPromptRect;
        int pad = (int)(12 * k);
        _touchControls.SkipButtonBounds = new Rectangle(
            dst.X + (int)(r.X * k) - pad, dst.Y + (int)(r.Y * k) - pad,
            (int)(r.Width * k) + pad * 2, (int)(r.Height * k) + pad * 2);
#endif
    }

    /// <summary>The "Press Space to Skip the ..." prompt, centred above the health bar's default place.</summary>
    private void DrawBreakSkipPrompt(int sw, int sh)
    {
        if (_breakPromptAlpha <= 0.01f || _breakShownKind == BreakKind.None || _font == null || _pixel == null)
        {
            _breakPromptRect = Rectangle.Empty;
            return;
        }
#if ANDROID
        const string verb = "Tap here";
#else
        const string verb = "Press Space";
#endif
        string what = _breakShownKind switch
        {
            BreakKind.Intro => "Intro",
            BreakKind.Intermission => "Intermission",
            _ => "Outro",
        };
        string text = $"{verb} to Skip the {what}";

        float u = sh / 720f;
        float scale = Math.Clamp(u, 0.6f, 2f) * 0.6f;
        Vector2 size = _font.MeasureUi(text) * scale;
        int padX = (int)(16 * u), padY = (int)(6 * u);
        // Default health bar: y 650 of 720 (upscroll) or 50 (downscroll); the prompt sits on the
        // side of it that faces the middle of the screen.
        float barY = (NoteRenderer.Downscroll ? 50f : 650f) * u;
        float y = NoteRenderer.Downscroll ? barY + 44 * u : barY - 18 * u - size.Y - padY * 2;
        var rect = new Rectangle((int)(sw / 2f - size.X / 2f) - padX, (int)y, (int)size.X + padX * 2, (int)size.Y + padY * 2);
        _breakPromptRect = rect;

        float a = _breakPromptAlpha * (0.85f + 0.15f * MathF.Sin((float)_songTime * 4f));
        _spriteBatch.Draw(_pixel, rect, Color.Black * (0.6f * a));
        _spriteBatch.Draw(_pixel, new Rectangle(rect.X, rect.Bottom - Math.Max(2, (int)(2 * u)), rect.Width, Math.Max(2, (int)(2 * u))),
            new Color(120, 220, 255) * a);
        _spriteBatch.DrawUiString(_font, text, new Vector2(rect.X + padX, rect.Y + padY), Color.White * a,
            0f, Vector2.Zero, scale, SpriteEffects.None, 0f);
    }
}
