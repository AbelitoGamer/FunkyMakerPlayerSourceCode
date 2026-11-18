using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;

namespace FunkyMakerPlayer;

/// <summary>Which note system a note style uses (skin.json <c>"noteSystem"</c>).</summary>
public enum NoteSystemKind
{
    /// <summary>Default: PNG + XML atlases with one frame per lane and state.</summary>
    Atlas,
    /// <summary>FMM-faithful: one plain image per note type, rotated and tinted per lane.</summary>
    Fmm,
}

/// <summary>
/// Images and settings of an FMM-faithful note style (<see cref="NoteSystemKind.Fmm"/>).
/// Mirrors the original FunkyMaker Mobile HUD (recreated in notestyles/FMMFaithful/hud.hx):
/// every note is the same up-arrow image, rotated per lane and coloured by tint.
/// </summary>
public sealed class FmmNoteAssets
{
    /// <summary>note.png: the up arrow used for every lane, and for the strums.</summary>
    public Texture2D Note { get; set; } = null!;
    /// <summary>goodnote.png: positive (O) notes. Null → note.png with an O overlay.</summary>
    public Texture2D? GoodNote { get; set; }
    /// <summary>badnote.png: negative (X) notes. Null → note.png with an X overlay.</summary>
    public Texture2D? BadNote { get; set; }

    /// <summary>holdtrail.png, cropped to its visible rows: stretched along the hold.</summary>
    public Texture2D? HoldTrail { get; set; }
    public Rectangle HoldTrailSource { get; set; }
    /// <summary>holdend.png, cropped to its visible rows: the cap at the tail.</summary>
    public Texture2D? HoldEnd { get; set; }
    public Rectangle HoldEndSource { get; set; }

    // Colours from hud.hx (noteColors / pressedColors / strum colour 0x828282)
    public Color[] NoteColors { get; set; } =
    {
        new Color(0xCA, 0x4E, 0x9D), new Color(0x00, 0xFF, 0xFE),
        new Color(0x10, 0xFA, 0x05), new Color(0xFC, 0x38, 0x44),
    };
    public Color[] PressedColors { get; set; } =
    {
        new Color(0xC5, 0x76, 0xA8), new Color(0x4A, 0xEB, 0xEB),
        new Color(0x55, 0xE8, 0x4F), new Color(0xE4, 0x6E, 0x75),
    };
    public Color StrumColor { get; set; } = new Color(0x82, 0x82, 0x82);

    /// <summary>Holds are drawn 0.8 wide while notes use 0.7 (hud.hx: note.scale.x = 0.8).</summary>
    public float HoldWidthScale { get; set; } = 0.8f / 0.7f;

    /// <summary>Drop shadow on notes, holds and strums (hud.hx dropShadow: alpha 0.6, 10 px down at 720p).</summary>
    public bool DropShadow { get; set; } = true;
    public float ShadowAlpha { get; set; } = 0.6f;
    public float ShadowDistance { get; set; } = 10f;

    /// <summary>Image for a note skin (0 default, 1 negative, 2 positive); null when it has none.</summary>
    public Texture2D? ImageForSkin(int skinType) => NoteSkins.Normalize(skinType) switch
    {
        NoteSkins.Negative => BadNote,
        NoteSkins.Positive => GoodNote,
        _ => Note,
    };
}

/// <summary>Loads FMM-faithful note styles.</summary>
public static class FmmNoteSystem
{
    public const string NoteFile = "note.png";
    public const string GoodNoteFile = "goodnote.png";
    public const string BadNoteFile = "badnote.png";
    public const string HoldTrailFile = "holdtrail.png";
    public const string HoldEndFile = "holdend.png";

    /// <summary>On-screen size (px at 720p) a note gets when skin.json sets no noteScale,
    /// matching the default atlas skin.</summary>
    private const float AutoFitSize = 102f;

    /// <summary>Quarter turns clockwise that turn the up arrow into each lane (L, D, U, R).</summary>
    public static readonly int[] LaneQuarterTurns = { 3, 2, 0, 1 };

    /// <summary>True when the folder's skin.json says <c>"noteSystem": "fmm"</c>.</summary>
    public static bool IsFmmFolder(string folderPath)
    {
        string skinJson = Path.Combine(folderPath, "skin.json");
        if (!File.Exists(skinJson)) return false;
        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(skinJson));
            return doc.RootElement.ValueKind == JsonValueKind.Object &&
                   doc.RootElement.TryGetProperty("noteSystem", out var ns) &&
                   ns.ValueKind == JsonValueKind.String &&
                   string.Equals(ns.GetString(), "fmm", StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Loads an FMM-faithful style. note.png is required; everything else is optional.
    /// Also fills the per-lane atlas frames (pre-rotated, pre-tinted copies) so menus and
    /// editors that only know the atlas system still show the skin correctly.
    /// </summary>
    public static NoteStyle? Load(string folderPath, GraphicsDevice gd)
    {
        string notePath = Path.Combine(folderPath, NoteFile);
        if (!File.Exists(notePath))
        {
            Console.Error.WriteLine($"[FmmNoteSystem] {folderPath}: noteSystem is \"fmm\" but {NoteFile} is missing");
            return null;
        }

        try
        {
            var assets = new FmmNoteAssets { Note = LoadTexture(notePath, gd)! };
            assets.GoodNote = LoadTexture(Path.Combine(folderPath, GoodNoteFile), gd);
            assets.BadNote = LoadTexture(Path.Combine(folderPath, BadNoteFile), gd);

            assets.HoldTrail = LoadTexture(Path.Combine(folderPath, HoldTrailFile), gd);
            if (assets.HoldTrail != null) assets.HoldTrailSource = VisibleRows(assets.HoldTrail);
            assets.HoldEnd = LoadTexture(Path.Combine(folderPath, HoldEndFile), gd);
            if (assets.HoldEnd != null) assets.HoldEndSource = VisibleRows(assets.HoldEnd);

            // Opacity defaults from hud.hx: strums 0.85, holds 0.6 × 1.4 (skin.json can override)
            var style = new NoteStyle
            {
                NoteSystem = NoteSystemKind.Fmm, Fmm = assets,
                StrumAlpha = 0.85f, NoteAlpha = 1f, HoldAlpha = 0.84f,
            };
            float autoScale = AutoFitSize / Math.Max(1, assets.Note.Width);
            style.NoteScale = autoScale;
            style.ReceptorScale = autoScale;

            ApplySkinJson(Path.Combine(folderPath, "skin.json"), style, assets);
            BakeCompatibilityFrames(style, assets, gd);
            style.LoadMissSounds(folderPath);

            Console.WriteLine($"[FmmNoteSystem] Loaded {folderPath} (good={assets.GoodNote != null}, bad={assets.BadNote != null}, " +
                              $"holds={assets.HoldTrail != null && assets.HoldEnd != null}, scale={style.NoteScale:0.###})");
            return style;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[FmmNoteSystem] Failed to load {folderPath}: {ex.Message}");
            return null;
        }
    }

    // ── skin.json ────────────────────────────────────────────────────────────

    private static void ApplySkinJson(string path, NoteStyle style, FmmNoteAssets a)
    {
        if (!File.Exists(path)) return;
        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(path));
            var root = doc.RootElement;
            style.ReadOpacity(root);

            if (root.TryGetProperty("noteScale", out var ns) && ns.ValueKind == JsonValueKind.Number)
            {
                style.NoteScale = ns.GetSingle();
                style.ReceptorScale = style.NoteScale; // strums follow notes unless set separately
            }
            if (root.TryGetProperty("receptorScale", out var rs) && rs.ValueKind == JsonValueKind.Number)
                style.ReceptorScale = rs.GetSingle();

            if (root.TryGetProperty("noteColors", out var nc)) ReadColorList(nc, a.NoteColors);
            if (root.TryGetProperty("pressedColors", out var pc)) ReadColorList(pc, a.PressedColors);
            if (root.TryGetProperty("strumColor", out var sc) && TryReadColor(sc, out var strum)) a.StrumColor = strum;
            if (root.TryGetProperty("dropShadow", out var ds) &&
                (ds.ValueKind == JsonValueKind.True || ds.ValueKind == JsonValueKind.False))
                a.DropShadow = ds.GetBoolean();

            // Shared with the atlas system: tints for X/O notes that have no dedicated image
            if (root.TryGetProperty("tintNegative", out var tn) && TryReadColor(tn, out var neg)) style.TintNegative = neg;
            if (root.TryGetProperty("tintPositive", out var tp) && TryReadColor(tp, out var pos)) style.TintPositive = pos;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[FmmNoteSystem] skin.json parse error in {path}: {ex.Message}");
        }
    }

    private static void ReadColorList(JsonElement arr, Color[] target)
    {
        if (arr.ValueKind != JsonValueKind.Array) return;
        int i = 0;
        foreach (var el in arr.EnumerateArray())
        {
            if (i >= target.Length) break;
            if (TryReadColor(el, out var c)) target[i] = c;
            i++;
        }
    }

    /// <summary>Accepts "#RRGGBB", "0xRRGGBB", "RRGGBB" or [r, g, b] (0–255).</summary>
    internal static bool TryReadColor(JsonElement el, out Color color)
    {
        color = Color.White;
        if (el.ValueKind == JsonValueKind.String)
        {
            string s = (el.GetString() ?? "").Trim();
            if (s.StartsWith("#")) s = s[1..];
            else if (s.StartsWith("0x", StringComparison.OrdinalIgnoreCase)) s = s[2..];
            if (s.Length != 6 || !int.TryParse(s, System.Globalization.NumberStyles.HexNumber, null, out int rgb))
                return false;
            color = new Color((rgb >> 16) & 0xFF, (rgb >> 8) & 0xFF, rgb & 0xFF);
            return true;
        }
        if (el.ValueKind == JsonValueKind.Array)
        {
            int[] c = { 255, 255, 255 };
            int i = 0;
            foreach (var v in el.EnumerateArray())
            {
                if (i >= 3) break;
                if (v.ValueKind == JsonValueKind.Number) c[i] = Math.Clamp((int)v.GetDouble(), 0, 255);
                i++;
            }
            color = new Color(c[0], c[1], c[2]);
            return true;
        }
        return false;
    }

    // ── Images ───────────────────────────────────────────────────────────────

    private static Texture2D? LoadTexture(string path, GraphicsDevice gd)
    {
        if (!File.Exists(path)) return null;
        using var fs = File.OpenRead(path);
        return Texture2D.FromStream(gd, fs);
    }

    /// <summary>Full width, rows trimmed to the visible pixels (alpha &gt; 8). The hold end
    /// image is usually a mostly-empty cell with the cap at the top; without trimming the hold
    /// would end well before its note does.</summary>
    private static Rectangle VisibleRows(Texture2D tex)
    {
        var data = new Color[tex.Width * tex.Height];
        tex.GetData(data);
        int top = -1, bottom = -1;
        for (int y = 0; y < tex.Height && top < 0; y++)
            for (int x = 0; x < tex.Width; x++)
                if (data[y * tex.Width + x].A > 8) { top = y; break; }
        for (int y = tex.Height - 1; y >= 0 && bottom < 0; y--)
            for (int x = 0; x < tex.Width; x++)
                if (data[y * tex.Width + x].A > 8) { bottom = y; break; }
        if (top < 0) return new Rectangle(0, 0, tex.Width, tex.Height);
        return new Rectangle(0, top, tex.Width, bottom - top + 1);
    }

    /// <summary>
    /// Fills NoteFrames / ReceptorFrames / HoldPieceFrames / HoldEndFrames with rotated, tinted
    /// copies. Gameplay does not use them (FmmNoteSystemRenderer rotates and tints on the GPU);
    /// they exist for the Arrows menu, key-binding screen and chart editor.
    /// </summary>
    private static void BakeCompatibilityFrames(NoteStyle style, FmmNoteAssets a, GraphicsDevice gd)
    {
        var full = new Rectangle(0, 0, a.Note.Width, a.Note.Height);
        for (int lane = 0; lane < 4; lane++)
        {
            int turns = LaneQuarterTurns[lane];
            style.NoteFrames[lane, NoteSkins.Default] = Bake(gd, a.Note, full, turns, a.NoteColors[lane], $"fmm note {lane}");
            if (a.BadNote != null)
                style.NoteFrames[lane, NoteSkins.Negative] = Bake(gd, a.BadNote,
                    new Rectangle(0, 0, a.BadNote.Width, a.BadNote.Height), turns, a.NoteColors[lane], $"fmm bad {lane}");
            if (a.GoodNote != null)
                style.NoteFrames[lane, NoteSkins.Positive] = Bake(gd, a.GoodNote,
                    new Rectangle(0, 0, a.GoodNote.Width, a.GoodNote.Height), turns, a.NoteColors[lane], $"fmm good {lane}");

            var idle = Bake(gd, a.Note, full, turns, a.StrumColor, $"fmm strum {lane}");
            var lit = Bake(gd, a.Note, full, turns, a.PressedColors[lane], $"fmm strum lit {lane}");
            style.ReceptorFrames[lane, 0] = new List<AtlasFrame> { idle };
            style.ReceptorFrames[lane, 1] = new List<AtlasFrame> { lit };
            style.ReceptorFrames[lane, 2] = new List<AtlasFrame> { lit };
            style.ReceptorFrames[lane, 3] = new List<AtlasFrame> { lit };

            if (a.HoldTrail != null && a.HoldEnd != null)
            {
                style.HoldPieceFrames[lane] = Bake(gd, a.HoldTrail, a.HoldTrailSource, 0, a.NoteColors[lane], $"fmm hold {lane}");
                style.HoldEndFrames[lane] = Bake(gd, a.HoldEnd, a.HoldEndSource, 0, a.NoteColors[lane], $"fmm hold end {lane}");
            }
        }
    }

    /// <summary>Copies <paramref name="src"/> rotated clockwise by quarter turns and multiplied by a tint.</summary>
    private static AtlasFrame Bake(GraphicsDevice gd, Texture2D tex, Rectangle src, int quarterTurns, Color tint, string name)
    {
        var input = new Color[src.Width * src.Height];
        tex.GetData(0, src, input, 0, input.Length);

        bool swap = quarterTurns % 2 == 1;
        int w = swap ? src.Height : src.Width;
        int h = swap ? src.Width : src.Height;
        var output = new Color[w * h];

        for (int y = 0; y < src.Height; y++)
        {
            for (int x = 0; x < src.Width; x++)
            {
                var c = input[y * src.Width + x];
                c = new Color(c.R * tint.R / 255, c.G * tint.G / 255, c.B * tint.B / 255, c.A);
                int nx, ny;
                switch (quarterTurns & 3)
                {
                    case 1: nx = src.Height - 1 - y; ny = x; break;                       // 90° clockwise
                    case 2: nx = src.Width - 1 - x; ny = src.Height - 1 - y; break;       // 180°
                    case 3: nx = y; ny = src.Width - 1 - x; break;                        // 90° counter-clockwise
                    default: nx = x; ny = y; break;
                }
                output[ny * w + nx] = c;
            }
        }

        var baked = new Texture2D(gd, w, h);
        baked.SetData(output);
        return new AtlasFrame
        {
            Texture = baked,
            Source = new Rectangle(0, 0, w, h),
            FrameW = w,
            FrameH = h,
            Name = name,
        };
    }
}

/// <summary>
/// Gameplay renderer for FMM-faithful styles. Owns the strum animation from hud.hx:
/// a key press shrinks the strum to 0.45 and lights it with the pressed colour, a hit pops
/// it to 1.0 (lit); every frame it lerps back to its rest size (0.7) and turns grey again
/// once it is close to it. Scales here are ratios of the rest size, so a style's
/// receptorScale stays the "0.7".
/// </summary>
public sealed class FmmNoteSystemRenderer : INoteStyleRenderer
{
    private const float RestScale = 0.7f;
    private const float PressRatio = 0.45f / RestScale;
    private const float HitRatio = 1.0f / RestScale;
    // Grey again inside [0.55, 0.65]/0.7 (hud.hx) on the way up from a press, and within the
    // mirrored band above rest on the way down from a hit (the script's literal rule never
    // reaches grey after a hit, which the original game does).
    private const float GreyBandLow = 0.55f / RestScale;
    private const float GreyBandHigh = RestScale / 0.65f;
    /// <summary>CoolUtil.fpsLerp(…, 0.2): 20 % of the way per 60 fps frame.</summary>
    private const float LerpPerFrame = 0.2f;

    private static readonly float[] LaneRotation = { -MathF.PI / 2f, MathF.PI, 0f, MathF.PI / 2f };

    private readonly NoteStyle _style;
    private readonly FmmNoteAssets _a;
    private readonly float[] _strumRatio = { 1f, 1f, 1f, 1f };
    private readonly bool[] _strumLit = new bool[4];
    private readonly int[] _holdStep = { int.MinValue, int.MinValue, int.MinValue, int.MinValue };

    /// <summary>Screen-space shadow offset in px; NoteRenderer sets it from the render size.</summary>
    public float ShadowOffsetY { get; set; } = 10f;

    public FmmNoteSystemRenderer(NoteStyle style)
    {
        _style = style;
        _a = style.Fmm ?? throw new ArgumentException("Style has no FMM assets", nameof(style));
    }

    public string GetEngineName() => "FMM Faithful";

    public float NoteImageWidth => _a.Note.Width;
    public Color PressedColor(int lane) => _a.PressedColors[lane & 3];

    // ── Strum animation ──────────────────────────────────────────────────────

    /// <summary>Key just pressed on this lane (hud.hx onInputUpdate).</summary>
    public void OnPress(int lane)
    {
        _strumRatio[lane] = PressRatio;
        _strumLit[lane] = true;
    }

    /// <summary>Note (or hold piece) hit on this lane (hud.hx onPostNoteHit).</summary>
    public void OnHit(int lane)
    {
        _strumRatio[lane] = HitRatio;
        _strumLit[lane] = true;
    }

    /// <summary>
    /// Called every frame while a hold is active. In the original each hold piece is its own
    /// hit, one per step, so the strum re-pops on every step of the hold.
    /// </summary>
    public void HoldTick(int lane, float songTime, float stepSeconds)
    {
        if (stepSeconds <= 0f) return;
        int step = (int)MathF.Floor(songTime / stepSeconds);
        if (step == _holdStep[lane]) return;
        _holdStep[lane] = step;
        OnHit(lane);
    }

    public void Update(float dt)
    {
        float t = Math.Clamp(LerpPerFrame * 60f * dt, 0f, 1f);
        for (int lane = 0; lane < 4; lane++)
        {
            _strumRatio[lane] += (1f - _strumRatio[lane]) * t;
            if (_strumRatio[lane] >= GreyBandLow && _strumRatio[lane] <= GreyBandHigh)
                _strumLit[lane] = false;
        }
    }

    // ── Drawing ──────────────────────────────────────────────────────────────

    /// <summary>Draws a strum centred on (cx, cy). <paramref name="scale"/> = receptorScale × lane scale.</summary>
    public void DrawStrum(SpriteBatch sb, int lane, float cx, float cy, float scale, float alpha, float extraRotation = 0f)
    {
        var tex = _a.Note;
        float s = scale * _strumRatio[lane];
        float a = _style.StrumAlpha * alpha;
        float rot = LaneRotation[lane] + extraRotation;
        var origin = new Vector2(tex.Width / 2f, tex.Height / 2f);
        var pos = new Vector2(cx, cy);

        DrawShadow(sb, tex, null, pos, rot, origin, new Vector2(s), a, SpriteEffects.None);
        Color c = _strumLit[lane] ? _a.PressedColors[lane] : _a.StrumColor;
        sb.Draw(tex, pos, null, c * a, rot, origin, s, SpriteEffects.None, 0f);
    }

    /// <summary>
    /// Draws a note centred on (cx, cy). Returns false when the skin type (X/O) has no image
    /// of its own and note.png was used instead, so the caller can overlay the symbol.
    /// </summary>
    public bool DrawNote(SpriteBatch sb, int lane, int skinType, float cx, float cy, float scale, float alpha)
    {
        var tex = _a.ImageForSkin(skinType);
        bool dedicated = tex != null;
        tex ??= _a.Note;

        float rot = LaneRotation[lane];
        var origin = new Vector2(tex.Width / 2f, tex.Height / 2f);
        var pos = new Vector2(cx, cy);
        alpha *= _style.NoteAlpha;

        DrawShadow(sb, tex, null, pos, rot, origin, new Vector2(scale), alpha, SpriteEffects.None);
        sb.Draw(tex, pos, null, _a.NoteColors[lane] * alpha, rot, origin, scale, SpriteEffects.None, 0f);
        return dedicated;
    }

    public void DrawUpcomingHold(SpriteBatch sb, int lane, float centerX, float arrowY,
                                 float holdLengthPx, float strumY, float laneWidth,
                                 float alpha, float scrollSpeed, float bpm)
        => DrawHold(sb, lane, centerX, arrowY, holdLengthPx, laneWidth, alpha);

    public void DrawActiveHold(SpriteBatch sb, int lane, float centerX, float strumY,
                               float holdLengthPx, float laneWidth, float alpha)
        => DrawHold(sb, lane, centerX, strumY, holdLengthPx, laneWidth, alpha);

    /// <summary>
    /// Body stretched from <paramref name="startY"/>, cap at the tail at 1:1 scale. When the
    /// remaining hold is shorter than the cap, the cap is clipped from its top so it shrinks
    /// into the strum instead of squashing (same rule as the Codename renderer).
    /// </summary>
    private void DrawHold(SpriteBatch sb, int lane, float centerX, float startY, float lengthPx, float laneWidth, float alpha)
    {
        if (_a.HoldTrail == null || _a.HoldEnd == null || lengthPx <= 0.5f) return;

        float sx = laneWidth > 0f ? laneWidth / NoteRenderer.LANE_WIDTH : 1f;
        float sy = _style.NoteScale * sx;
        float sw = sy * _a.HoldWidthScale;

        var piece = _a.HoldTrailSource;
        if (piece.Height > 4) piece = new Rectangle(piece.X, piece.Y + 1, piece.Width, piece.Height - 2);
        var end = _a.HoldEndSource;

        float fullCap = end.Height * sy;
        float bodyLen = lengthPx - fullCap;
        float consumed = bodyLen >= 0f ? 0f : Math.Clamp(1f - lengthPx / Math.Max(1f, fullCap), 0f, 1f);
        int clipY = (int)MathF.Round(consumed * end.Height);
        int clipH = end.Height - clipY;
        if (clipH <= 0) return;
        int capH = (int)MathF.Round(clipH * sy);
        if (capH <= 0) return;

        bool down = NoteRenderer.Downscroll;
        // The cap's top rows (the ones nearest the strum) disappear first. In downscroll the
        // cap is drawn flipped, so those same source rows are still the ones nearest the strum.
        var capSrc = new Rectangle(end.X, end.Y + clipY, end.Width, clipH);

        int bodyW = (int)MathF.Round(piece.Width * sw);
        int capW = (int)MathF.Round(end.Width * sw);
        int bodyX = (int)MathF.Round(centerX - bodyW / 2f);
        int capX = (int)MathF.Round(centerX - capW / 2f);
        int start = (int)MathF.Round(startY);
        int join = bodyLen > 0f ? (int)MathF.Round(down ? startY - bodyLen : startY + bodyLen) : start;

        Rectangle bodyDst = down
            ? new Rectangle(bodyX, join, bodyW, start - join)
            : new Rectangle(bodyX, start, bodyW, join - start);
        Rectangle capDst = down
            ? new Rectangle(capX, join - capH, capW, capH)
            : new Rectangle(capX, join, capW, capH);
        var fx = down ? SpriteEffects.FlipVertically : SpriteEffects.None;

        float a = (_style.HoldAlpha ?? 0.84f) * alpha;
        Color tint = _a.NoteColors[lane] * a;

        if (_a.DropShadow)
        {
            Color shadow = Color.Black * (_a.ShadowAlpha * a);
            var off = new Point(0, (int)MathF.Round(ShadowOffsetY));
            if (bodyDst.Height > 0)
                sb.Draw(_a.HoldTrail, new Rectangle(bodyDst.Location + off, bodyDst.Size), piece, shadow, 0f, Vector2.Zero, fx, 0f);
            sb.Draw(_a.HoldEnd, new Rectangle(capDst.Location + off, capDst.Size), capSrc, shadow, 0f, Vector2.Zero, fx, 0f);
        }

        if (bodyDst.Height > 0)
            sb.Draw(_a.HoldTrail, bodyDst, piece, tint, 0f, Vector2.Zero, fx, 0f);
        sb.Draw(_a.HoldEnd, capDst, capSrc, tint, 0f, Vector2.Zero, fx, 0f);
    }

    private void DrawShadow(SpriteBatch sb, Texture2D tex, Rectangle? src, Vector2 pos, float rot,
                            Vector2 origin, Vector2 scale, float alpha, SpriteEffects fx)
    {
        if (!_a.DropShadow) return;
        sb.Draw(tex, pos + new Vector2(0f, ShadowOffsetY), src, Color.Black * (_a.ShadowAlpha * alpha),
                rot, origin, scale, fx, 0f);
    }

    // INoteStyleRenderer members NoteRenderer does not call for this system; kept functional.
    public void DrawArrow(SpriteBatch sb, int lane, float x, float y, float scale, float alpha, Color tint)
        => DrawNote(sb, lane, NoteSkins.Default, x + _a.Note.Width * scale / 2f, y + _a.Note.Height * scale / 2f, scale, alpha);

    public void DrawReceptor(SpriteBatch sb, int lane, float x, float y, int state, float scale, float alpha)
        => DrawStrum(sb, lane, x + _a.Note.Width * scale / 2f, y + _a.Note.Height * scale / 2f, scale, alpha);
}
