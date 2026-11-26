using System;
using System.Collections.Generic;
using System.Text.Json.Serialization;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;

namespace FunkyMakerPlayer.Editors.Character;

/// <summary>
/// Direction the character artwork naturally faces when drawn unflipped (Scale.X &gt; 0).
/// In standard FNF layout, Player (BF, right side) faces Left, and Opponent (left side) faces Right.
/// </summary>
public enum CharacterFacing
{
    Left = 0,
    Right = 1,
}

/// <summary>
/// Source format from which a character project was imported.
/// </summary>
public enum CharacterSourceKind
{
    SparrowXml = 0,
    AnimateSpritemap = 1,
    FnmmSprite = 2,
}

/// <summary>
/// One decoded animation clip imported from Sparrow v2 XML+PNG, Adobe Animate Spritemap, or FNMM strip.
/// All frames inside a clip share the same tight canvas dimensions (CanvasWidth x CanvasHeight)
/// whose top-left sits at (WorldMinX, WorldMinY) in the source atlas/stage space.
/// </summary>
public sealed class ImportedAnimClip : IDisposable
{
    public string Name { get; set; } = "";
    public List<Image<Rgba32>> Frames { get; set; } = new();
    public int CanvasWidth { get; set; }
    public int CanvasHeight { get; set; }
    public float WorldMinX { get; set; }
    public float WorldMinY { get; set; }
    public float SuggestedFps { get; set; } = 24f;
    public bool SuggestedLoop { get; set; } = true;
    /// <summary>Optional Psych/Codename/V-Slice offset [ox, oy] subtracted from draw position.</summary>
    public Vec2 ImportedOffset { get; set; } = new();
    public bool HasImportedOffset { get; set; }

    public void Dispose()
    {
        foreach (var img in Frames)
        {
            try { img.Dispose(); } catch { }
        }
        Frames.Clear();
    }
}

/// <summary>
/// Editable animation slot in the Character Editor.
/// Slots 0..4 map to FunkyMaker's standard character animations:
///   0 = Idle, 1 = Sing Left, 2 = Sing Down, 3 = Sing Up, 4 = Sing Right, 5+ = Extra/Event.
/// </summary>
public sealed class CharacterAnimSlot
{
    public int SlotIndex { get; set; }
    public string Label { get; set; } = "IDLE";
    public string ClipName { get; set; } = "";
    public float Fps { get; set; } = 24f;
    public bool Loop { get; set; } = true;
    /// <summary>
    /// User stage nudge in unscaled pixels relative to the auto-computed foot anchor:
    /// +X moves the sprite RIGHT on stage; +Y moves the sprite DOWN on stage.
    /// </summary>
    public float UserOffsetX { get; set; }
    public float UserOffsetY { get; set; }
}

/// <summary>
/// In-memory editable Character Editor project.
/// </summary>
public sealed class CharacterProject : IDisposable
{
    public string Name { get; set; } = "Custom Character";
    public string Slug { get; set; } = "custom_char";
    public CharacterSourceKind SourceKind { get; set; } = CharacterSourceKind.SparrowXml;
    public float Scale { get; set; } = 1.0f;
    public bool Bilinear { get; set; } = true;
    /// <summary>When true, horizontally flips all baked frames when previewing and saving.</summary>
    public bool BakeFlipX { get; set; }
    /// <summary>Direction the character faces AFTER BakeFlipX is applied.</summary>
    public CharacterFacing NativeFacing { get; set; } = CharacterFacing.Left;

    public List<ImportedAnimClip> AvailableClips { get; set; } = new();
    public List<CharacterAnimSlot> Slots { get; set; } = new();

    public Image<Rgba32>? IconNormal { get; set; }
    public Image<Rgba32>? IconLosing { get; set; }

    public static readonly string[] CoreSlotNames =
    {
        "IDLE",
        "SING LEFT",
        "SING DOWN",
        "SING UP",
        "SING RIGHT",
    };

    public void EnsureCoreSlots()
    {
        for (int i = 0; i < CoreSlotNames.Length; i++)
        {
            if (Slots.Count <= i)
            {
                Slots.Add(new CharacterAnimSlot
                {
                    SlotIndex = i,
                    Label = CoreSlotNames[i],
                    Fps = 24f,
                    Loop = true,
                });
            }
            else
            {
                Slots[i].SlotIndex = i;
                Slots[i].Label = CoreSlotNames[i];
            }
        }
        for (int i = CoreSlotNames.Length; i < Slots.Count; i++)
        {
            Slots[i].SlotIndex = i;
            if (string.IsNullOrWhiteSpace(Slots[i].Label))
                Slots[i].Label = $"EXTRA {i - 4}";
        }
    }

    public ImportedAnimClip? FindClip(string? name)
    {
        if (string.IsNullOrWhiteSpace(name)) return null;
        for (int i = 0; i < AvailableClips.Count; i++)
        {
            if (string.Equals(AvailableClips[i].Name, name, StringComparison.OrdinalIgnoreCase))
                return AvailableClips[i];
        }
        return null;
    }

    /// <summary>
    /// Computes the world foot anchor (in source atlas/stage coordinates) from Slot 0 (Idle)
    /// so every other animation aligns cleanly to Idle's ground contact point.
    /// </summary>
    public (float AnchorX, float AnchorY) GetIdleWorldFootAnchor()
    {
        var idleSlot = Slots.Count > 0 ? Slots[0] : null;
        var idleClip = FindClip(idleSlot?.ClipName) ?? (AvailableClips.Count > 0 ? AvailableClips[0] : null);
        if (idleClip == null)
            return (0f, 0f);

        float anchorX = (idleClip.WorldMinX + idleClip.CanvasWidth * 0.5f) - idleClip.ImportedOffset.X;
        float anchorY = (idleClip.WorldMinY + idleClip.CanvasHeight) - idleClip.ImportedOffset.Y;
        return (anchorX, anchorY);
    }

    /// <summary>
    /// Computes the exact FunkyMaker SpriteAnimData.Offset (in unscaled frame pixels) for a slot.
    /// In FunkyMaker, drawPos = Position - Offset * scale, so pixel (Offset.X, Offset.Y) lands on Position.
    /// </summary>
    public Vec2 ComputeFnmmOffset(CharacterAnimSlot slot, ImportedAnimClip clip)
    {
        var (anchorX, anchorY) = GetIdleWorldFootAnchor();
        float naturalOX = anchorX + clip.ImportedOffset.X - clip.WorldMinX;
        float naturalOY = anchorY + clip.ImportedOffset.Y - clip.WorldMinY;

        if (BakeFlipX)
            naturalOX = clip.CanvasWidth - naturalOX;

        return new Vec2
        {
            X = MathF.Round(naturalOX - slot.UserOffsetX, 2),
            Y = MathF.Round(naturalOY - slot.UserOffsetY, 2),
        };
    }

    public void Dispose()
    {
        foreach (var clip in AvailableClips)
            clip.Dispose();
        AvailableClips.Clear();
        try { IconNormal?.Dispose(); } catch { }
        try { IconLosing?.Dispose(); } catch { }
        IconNormal = null;
        IconLosing = null;
    }
}

/// <summary>
/// Persisted character.json metadata stored alongside sprite.json + 0.png..N.png in mods/characters/&lt;slug&gt;/.
/// </summary>
public sealed class CharacterMetadata
{
    [JsonPropertyName("name")]         public string Name { get; set; } = "Custom Character";
    [JsonPropertyName("slug")]         public string Slug { get; set; } = "custom_char";
    [JsonPropertyName("sourceKind")]   public string SourceKind { get; set; } = "sparrow";
    [JsonPropertyName("scale")]        public float Scale { get; set; } = 1.0f;
    [JsonPropertyName("bilinear")]     public bool Bilinear { get; set; } = true;
    [JsonPropertyName("bakeFlipX")]    public bool BakeFlipX { get; set; }
    [JsonPropertyName("nativeFacing")] public string NativeFacing { get; set; } = "left";
    [JsonPropertyName("hasIcon")]      public bool HasIcon { get; set; }
    [JsonPropertyName("idleWidth")]    public int IdleWidth { get; set; }
    [JsonPropertyName("idleHeight")]   public int IdleHeight { get; set; }
    [JsonPropertyName("slots")]        public List<CharacterSlotMeta> Slots { get; set; } = new();
}

public sealed class CharacterSlotMeta
{
    [JsonPropertyName("index")]       public int Index { get; set; }
    [JsonPropertyName("label")]       public string Label { get; set; } = "";
    [JsonPropertyName("clipName")]    public string ClipName { get; set; } = "";
    [JsonPropertyName("fps")]         public float Fps { get; set; } = 24f;
    [JsonPropertyName("loop")]        public bool Loop { get; set; } = true;
    [JsonPropertyName("userOffsetX")] public float UserOffsetX { get; set; }
    [JsonPropertyName("userOffsetY")] public float UserOffsetY { get; set; }
}

/// <summary>
/// Lightweight entry returned when scanning mods/characters/ for menus and pickers.
/// </summary>
public sealed class CharacterSummary
{
    public string Slug { get; set; } = "";
    public string Name { get; set; } = "";
    public string DirectoryPath { get; set; } = "";
    public bool IsBakedFnmm { get; set; }
    public bool IsRawSourceFolder { get; set; }
    public string RawSourceHint { get; set; } = "";
    public bool HasIcon { get; set; }
    public CharacterFacing NativeFacing { get; set; } = CharacterFacing.Left;
    public float Scale { get; set; } = 1.0f;
    public int IdleHeight { get; set; }
}
