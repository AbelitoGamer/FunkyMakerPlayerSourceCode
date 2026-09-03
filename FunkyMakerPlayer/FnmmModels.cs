using System;
using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace FunkyMakerPlayer;

// ── meta.json ──────────────────────────────────────────────────────────────
public class FnmmMeta
{
    [JsonPropertyName("name")]    public string Name    { get; set; } = "";
    [JsonPropertyName("UUID")]    public string UUID    { get; set; } = "";
    [JsonPropertyName("version")] public string Version { get; set; } = "";
}

// ── data.json ──────────────────────────────────────────────────────────────
public class NoteProps
{
    [JsonPropertyName("skin")]         public float Skin         { get; set; }
    [JsonPropertyName("onPressEvent")] public float OnPressEvent { get; set; }
    [JsonPropertyName("onMissEvent")]  public float OnMissEvent  { get; set; }
    
    // Skin values:
    //   0 = normal note
    //   1 = damage note  (hitting it drains health)
    //   2 = instakill    (hitting it = instant game over)
    //   3 = heal note    (hitting it restores health)
    [System.Text.Json.Serialization.JsonIgnore]
    public bool IsNormal   => Skin == 0f;
    [System.Text.Json.Serialization.JsonIgnore]
    public bool IsDamage   => Skin == 1f;
    [System.Text.Json.Serialization.JsonIgnore]
    public bool IsInstakill => Skin == 2f;
    [System.Text.Json.Serialization.JsonIgnore]
    public bool IsHeal     => Skin == 3f;
}

public class FnmmNote
{
    /// <summary>Time in beats when the note starts.</summary>
    [JsonPropertyName("start")]    public float Start    { get; set; }
    /// <summary>Duration in beats (0 = tap note, >0 = hold note).</summary>
    [JsonPropertyName("duration")] public float Duration { get; set; }
    [JsonPropertyName("props")]    public NoteProps Props { get; set; } = new();
}

public class FnmmData
{
    [JsonPropertyName("bpm")]        public float Bpm     { get; set; } = 0f;
    [JsonPropertyName("version")]    public float Version { get; set; }
    [JsonPropertyName("npb")]        public float Npb     { get; set; } = 4f;
    [JsonPropertyName("speed")]      public float Speed   { get; set; } = 1f;
    // "strumLines" is normalized to "strumlines" by the loader before parsing
    [JsonPropertyName("strumlines")] public List<List<List<FnmmNote>>> Strumlines { get; set; } = new();

    [System.Text.Json.Serialization.JsonIgnore]
    public bool TimesAreSeconds => FnmmTimeConverter.AreTimesInSeconds(Version, Bpm);

    public float ToSeconds(float noteTime) =>
        FnmmTimeConverter.ToSeconds(noteTime, Version, Bpm);

    [System.Text.Json.Serialization.JsonIgnore]
    public float EffectiveBpm => FnmmTimeConverter.GetEffectiveBpm(Bpm);
}

// ── scene.json ─────────────────────────────────────────────────────────────
public class Vec2
{
    [JsonPropertyName("x")] public float X { get; set; }
    [JsonPropertyName("y")] public float Y { get; set; }
}

public class RgbColor
{
    [JsonPropertyName("r")] public float R { get; set; } = 255f;
    [JsonPropertyName("g")] public float G { get; set; } = 255f;
    [JsonPropertyName("b")] public float B { get; set; } = 255f;
}

public class Transform
{
    [JsonPropertyName("position")] public Vec2 Position { get; set; } = new();
    [JsonPropertyName("scale")]    public Vec2 Scale    { get; set; } = new();
}

public class SceneObject
{
    // ── V2 Format (legacy) ──
    [JsonPropertyName("spr")]       public string Spr       { get; set; } = "";
    [JsonPropertyName("x")]         public float  X         { get; set; }
    [JsonPropertyName("y")]         public float  Y         { get; set; }
    [JsonPropertyName("scale")]     public Vec2?  Scale     { get; set; }
    
    // ── V3 Format (new) ──
    [JsonPropertyName("transform")] public List<Transform>? Transform { get; set; }
    [JsonPropertyName("color")]     public RgbColor?        Color     { get; set; }
    [JsonPropertyName("opacity")]   public float?           Opacity   { get; set; }
    
    // ── Common fields ──
    [JsonPropertyName("visible")]   public bool   Visible   { get; set; }
    [JsonPropertyName("fixed")]     public bool   Fixed     { get; set; }
    [JsonPropertyName("strumLine")] public float  StrumLine { get; set; }
    /// <summary>Initial animation index (if specified in scene.json).</summary>
    [JsonPropertyName("anim")]      public float? Anim      { get; set; }
    /// <summary>
    /// Camera index this object is "attached to" (-1 = none).
    /// The object's visual scale is multiplied by cameras[camera].scale,
    /// simulating that the object lives inside that camera's space.
    /// </summary>
    [JsonPropertyName("camera")]    public int    Camera    { get; set; } = -1;

    /// <summary>Current transform index being displayed (for multi-position objects).</summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public int CurrentTransformIndex { get; set; } = 0;

    /// <summary>Snapshot of the original JSON values — used to reset state on restart.</summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public SceneObject? InitialState { get; set; }

    /// <summary>Gets the active position based on current transform index.</summary>
    public Vec2 GetPosition()
    {
        // Handle both formats transparently
        if (Transform != null && Transform.Count > 0)
        {
            int idx = System.Math.Clamp(CurrentTransformIndex, 0, Transform.Count - 1);
            return Transform[idx].Position;
        }
        return new Vec2 { X = X, Y = Y };
    }

    /// <summary>Gets the active scale based on current transform index.</summary>
    public Vec2 GetScale()
    {
        // Handle both formats transparently
        if (Transform != null && Transform.Count > 0)
        {
            int idx = System.Math.Clamp(CurrentTransformIndex, 0, Transform.Count - 1);
            return Transform[idx].Scale;
        }
        return Scale ?? new Vec2 { X = 1f, Y = 1f };
    }

    /// <summary>Save current values as the initial state (call once after loading).</summary>
    public void SaveInitialState()
    {
        InitialState = new SceneObject
        {
            Spr       = Spr,
            X         = X,
            Y         = Y,
            Scale     = Scale != null ? new Vec2 { X = Scale.X, Y = Scale.Y } : null,
            Transform = Transform != null ? new List<Transform>(Transform) : null,
            Color     = Color != null ? new RgbColor { R = Color.R, G = Color.G, B = Color.B } : null,
            Opacity   = Opacity,
            Visible   = Visible,
            Fixed     = Fixed,
            StrumLine = StrumLine,
            Anim      = Anim,
            Camera    = Camera,
        };
    }

    /// <summary>Restore mutable fields to their initial state.</summary>
    public void ResetToInitialState()
    {
        if (InitialState == null) return;
        Spr       = InitialState.Spr;
        Visible   = InitialState.Visible;
        StrumLine = InitialState.StrumLine;
        CurrentTransformIndex = 0;  // Reset to first position
    }
}

public class SceneCamera
{
    [JsonPropertyName("x")]     public float X     { get; set; }
    [JsonPropertyName("y")]     public float Y     { get; set; }
    [JsonPropertyName("scale")] public float Scale { get; set; } = 1.0f;
    
    // For nested format: {"position": {"x": 514, "y": 1070}, "scale": 0.95}
    [JsonPropertyName("position")]
    public Position? NestedPosition { get; set; }
    
    /// <summary>
    /// True if this camera uses nested position format {position: {x, y}, scale}.
    /// Set manually during scene loading if needed.
    /// </summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public bool UsesNestedPositionFormat { get; set; }
    
    // Helper class for nested position format
    public class Position
    {
        [JsonPropertyName("x")] public float X { get; set; }
        [JsonPropertyName("y")] public float Y { get; set; }
    }
}

public class FnmmScene
{
    [JsonPropertyName("objects")]   public List<SceneObject>  Objects   { get; set; } = new();
    [JsonPropertyName("cameras")]   public List<SceneCamera>  Cameras   { get; set; } = new();
    [JsonPropertyName("icons")]     public List<string>       Icons     { get; set; } = new();
    [JsonPropertyName("landscape")] public bool               Landscape { get; set; }
}

// ── events.json ────────────────────────────────────────────────────────────
public class EventActionProps
{
    // ObjectAnimationSet / ObjectSpriteSet / ObjectVisibilitySet / ObjectBlendColor
    [JsonPropertyName("object")]  public float?  Object  { get; set; }
    [JsonPropertyName("anim")]    public float?  Anim    { get; set; }
    // "value" can be bool (true/false) or number (1.0/0.0) depending on level version
    [JsonPropertyName("value")]   public System.Text.Json.JsonElement? ValueRaw { get; set; }
    [JsonPropertyName("sprite")]  public string? Sprite  { get; set; }

    /// <summary>Interprets value as bool regardless of whether JSON sent bool or number.</summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public bool? Value
    {
        get
        {
            if (ValueRaw == null) return null;
            var v = ValueRaw.Value;
            return v.ValueKind switch
            {
                System.Text.Json.JsonValueKind.True   => true,
                System.Text.Json.JsonValueKind.False  => false,
                System.Text.Json.JsonValueKind.Number => v.GetDouble() != 0.0,
                System.Text.Json.JsonValueKind.String => v.GetString()?.ToLower() is "true" or "yes" or "1",
                _ => null,
            };
        }
    }

    /// <summary>Interprets value as float (for ObjectOpacitySet etc.).</summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public float? ValueFloat
    {
        get
        {
            if (ValueRaw == null) return null;
            var v = ValueRaw.Value;
            return v.ValueKind switch
            {
                System.Text.Json.JsonValueKind.Number => (float)v.GetDouble(),
                System.Text.Json.JsonValueKind.True   => 1f,
                System.Text.Json.JsonValueKind.False  => 0f,
                _ => null,
            };
        }
    }

    // ObjectBlendColor
    [JsonPropertyName("color_r")] public float? ColorR { get; set; }
    [JsonPropertyName("color_g")] public float? ColorG { get; set; }
    [JsonPropertyName("color_b")] public float? ColorB { get; set; }

    // ObjectTransformSet / ObjectPositionSet (v3 format - multiple positions)
    [JsonPropertyName("position")]      public float?  Position      { get; set; }
    [JsonPropertyName("transform")]     public float?  Transform     { get; set; } // Alternative name for position
    [JsonPropertyName("interpolation")] public string? Interpolation { get; set; }
    // Note: Duration and TimeUnit are already defined below for ScreenFlash/ObjectOpacitySet

    // CameraSet
    [JsonPropertyName("mode")]               public string? Mode               { get; set; }
    [JsonPropertyName("target")]             public float?  Target             { get; set; }
    [JsonPropertyName("transitionTime")]     public float?  TransitionTime     { get; set; }
    [JsonPropertyName("transitionTimeUnit")] public string? TransitionTimeUnit { get; set; }
    [JsonPropertyName("easingType")]         public string? EasingType         { get; set; }

    // ObjectChartChange
    [JsonPropertyName("strumLine")]          public float?  StrumLine          { get; set; }

    // RunActionPack
    [JsonPropertyName("actionPack")]         public float?  ActionPack         { get; set; }

    // PlayerHealth
    [JsonPropertyName("operation")]          public string? Operation          { get; set; }
    [JsonPropertyName("minHealth")]          public float?  MinHealth          { get; set; }
    // Note: PlayerHealth uses "value" too — already covered by ValueRaw/ValueFloat above

    // ScreenFlash / ObjectOpacitySet shared
    [JsonPropertyName("alpha")]    public float?  Alpha    { get; set; }
    [JsonPropertyName("duration")] public float?  Duration { get; set; }
    [JsonPropertyName("timeUnit")] public string? TimeUnit { get; set; }

    // CameraShake
    [JsonPropertyName("strength")] public float?  Strength { get; set; }
    [JsonPropertyName("shakes")]   public float?  Shakes   { get; set; }
    [JsonPropertyName("delay")]    public float?  Delay    { get; set; }

    // GUI (show/hide HUD elements)
    [JsonPropertyName("element")]  public string? Element  { get; set; }
    // GUI visible flag — some levels use {"visible": true/false} directly
    [JsonPropertyName("visible")]  public System.Text.Json.JsonElement? VisibleRaw { get; set; }

    /// <summary>Interprets the GUI "visible" field as bool.</summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public bool? VisibleBool
    {
        get
        {
            if (VisibleRaw == null) return null;
            var v = VisibleRaw.Value;
            return v.ValueKind switch
            {
                System.Text.Json.JsonValueKind.True   => true,
                System.Text.Json.JsonValueKind.False  => false,
                System.Text.Json.JsonValueKind.Number => v.GetDouble() != 0.0,
                System.Text.Json.JsonValueKind.String => v.GetString()?.ToLower() is "true" or "yes" or "1",
                _ => null,
            };
        }
    }
}

public class EventAction
{
    [JsonPropertyName("type")]  public string          Type  { get; set; } = "";
    [JsonPropertyName("props")] public EventActionProps Props { get; set; } = new();
}

public class FnmmEvent
{
    /// <summary>Time in beats.</summary>
    [JsonPropertyName("time")]    public float             Time    { get; set; }
    [JsonPropertyName("actions")] public List<EventAction> Actions { get; set; } = new();
}

public class FnmmEvents
{
    [JsonPropertyName("events")]       public List<FnmmEvent>        Events      { get; set; } = new();
    /// <summary>
    /// action_packs[i] = a list of EventActions that can be referenced by index.
    /// Used by onPressEvent/onMissEvent on notes.
    /// </summary>
    [JsonPropertyName("action_packs")] public List<ActionPack>       ActionPacks { get; set; } = new();
}

public class ActionPack
{
    [JsonPropertyName("actions")] public List<EventAction> Actions { get; set; } = new();
}

// ── sprite.json ────────────────────────────────────────────────────────────
public class SpriteAnimData
{
    [JsonPropertyName("bilinear")] public bool   Bilinear { get; set; }
    [JsonPropertyName("loop")]     public bool   Loop     { get; set; }
    /// <summary>Number of frames (can be int or string in JSON).</summary>
    [JsonPropertyName("num")]      public object Num      { get; set; } = 1;
    [JsonPropertyName("spd")]      public float  Spd      { get; set; }
    [JsonPropertyName("spd_fps")]  public bool   SpdFps   { get; set; }
    [JsonPropertyName("offset")]   public Vec2   Offset   { get; set; } = new();
    [JsonPropertyName("scale")]    public float  Scale    { get; set; }

    public int FrameCount => Num is System.Text.Json.JsonElement je
        ? (je.ValueKind == System.Text.Json.JsonValueKind.String
            ? int.Parse(je.GetString()!)
            : (int)je.GetDouble())
        : (int)System.Convert.ToDouble(Num);

    /// <summary>
    /// When >= 0, this anim's frames were split from a too-wide strip.
    /// SplitOffset is the index in the texture list where this anim's frames start.
    /// </summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public int SplitOffset { get; set; } = -1;

    /// <summary>
    /// For type=0 sprites with multiple PNGs: the index in _textures where this
    /// animation's texture lives. Set during SetupLevel. -1 = use _currentAnim directly.
    /// </summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public int TextureIndex { get; set; } = -1;

    /// <summary>
    /// When > 0, each split texture contains this many frames (chunk mode).
    /// Frame f is in chunk (f / ChunkFrames), at x-offset (f % ChunkFrames) * frameWidth.
    /// When 0, each split texture is exactly one frame (legacy per-frame mode).
    /// </summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public int ChunkFrames { get; set; } = 0;

    /// <summary>Width of a single frame in pixels (set during chunk split).</summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public int FrameWidth { get; set; } = 0;

    /// <summary>
    /// Compensation factor when the texture was downscaled to fit GPU limits.
    /// 1.0 = no downscale. 2.0 = texture was halved, draw at 2x scale to compensate.
    /// </summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public float DownscaleCompensation { get; set; } = 1.0f;

    /// <summary>
    /// When true, this sprite uses packed atlas instead of horizontal strip.
    /// Frame coordinates are stored in AtlasFrameRects.
    /// </summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public bool UsesPackedAtlas { get; set; } = false;

    /// <summary>
    /// Frame rectangles for packed atlas (X, Y, Width, Height for each frame).
    /// Only used when UsesPackedAtlas = true.
    /// </summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public List<AtlasPacker.FrameRect>? AtlasFrameRects { get; set; } = null;

    [System.Text.Json.Serialization.JsonIgnore]
    public bool IsSplit => SplitOffset >= 0;
}

public class FnmmSprite
{
    /// <summary>
    /// 1 = multi-anim spritesheet (one PNG per anim index, each is a horizontal strip of N frames).
    /// 2 = single-anim spritesheet (one PNG, horizontal strip of N frames).
    /// 3 = multi-frame (separate PNGs: 0.png, 1.png, … one per animation index).
    /// </summary>
    [JsonPropertyName("type")] public float              Type { get; set; }
    [JsonPropertyName("data")] public List<SpriteAnimData> Data { get; set; } = new();
}

// ── Loaded level (all together) ────────────────────────────────────────────
public class FnmmLevel
{
    public FnmmMeta   Meta   { get; set; } = new();
    public FnmmData   Data   { get; set; } = new();
    public FnmmScene  Scene  { get; set; } = new();
    public FnmmEvents Events { get; set; } = new();
    /// <summary>spriteName → (FnmmSprite definition, list of PNG byte arrays in order)</summary>
    public Dictionary<string, (FnmmSprite Def, List<byte[]> Frames)> Sprites { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    /// <summary>Root-level loose PNGs (key = filename without extension)</summary>
    public Dictionary<string, byte[]> LooseImages { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public byte[]? MusicData { get; set; }
    public string  LevelName { get; set; } = "";
}
