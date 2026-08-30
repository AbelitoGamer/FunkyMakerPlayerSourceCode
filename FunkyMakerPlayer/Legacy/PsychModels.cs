using System.Collections.Generic;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace FunkyMakerPlayer;

// ── Chart (data/SongName/songname.json) ────────────────────────────────────

public class PsychChart
{
    // Some charts wrap everything in a "song" key (handled by ParseChart, not here)
    [JsonPropertyName("player1")]   public string?     Player1   { get; set; }
    [JsonPropertyName("player2")]   public string?     Player2   { get; set; }
    [JsonPropertyName("gfVersion")] public string?     GfVersion { get; set; }
    [JsonPropertyName("stage")]     public string?     Stage     { get; set; }
    [JsonPropertyName("bpm")]       public float       Bpm       { get; set; }
    [JsonPropertyName("speed")]     public float       Speed     { get; set; } = 1f;
    [JsonPropertyName("notes")]     public List<PsychSection>? Notes { get; set; }
    [JsonPropertyName("format")]    public string?     Format    { get; set; }
}

public class PsychSong
{
    [JsonPropertyName("player1")]   public string?     Player1   { get; set; }
    [JsonPropertyName("player2")]   public string?     Player2   { get; set; }
    [JsonPropertyName("gfVersion")] public string?     GfVersion { get; set; }
    [JsonPropertyName("stage")]     public string?     Stage     { get; set; }
    [JsonPropertyName("bpm")]       public float       Bpm       { get; set; }
    [JsonPropertyName("speed")]     public float       Speed     { get; set; } = 1f;
    [JsonPropertyName("notes")]     public List<PsychSection>? Notes { get; set; }
}

public class PsychSection
{
    [JsonPropertyName("sectionNotes")]  public List<JsonElement>? SectionNotes  { get; set; }
    [JsonPropertyName("sectionBeats")]  public float              SectionBeats  { get; set; } = 4f;
    [JsonPropertyName("mustHitSection")]public bool               MustHitSection{ get; set; }
    [JsonPropertyName("bpm")]           public float              Bpm           { get; set; }
    [JsonPropertyName("changeBPM")]     public bool               ChangeBpm     { get; set; }
    [JsonPropertyName("altAnim")]       public bool               AltAnim       { get; set; }
}

// ── Note (parsed from SectionNotes) ──────────────────────────────────────

public class PsychNote
{
    public float Time { get; set; }          // Time in milliseconds
    public int Direction { get; set; }       // 0=Left, 1=Down, 2=Up, 3=Right
    public float Length { get; set; }        // Sustain length in milliseconds
    public bool MustHitNote { get; set; }    // True if player must hit it
    public string Type { get; set; } = "";   // Note type (normal, alt, etc)
}

// ── Events (data/SongName/events.json) ────────────────────────────────────
// Format: { "events": [ [time_ms, [ [name, val1, val2], ... ]], ... ] }

public class PsychEvents
{
    [JsonPropertyName("events")] public List<JsonElement>? Events { get; set; }
    [JsonPropertyName("format")] public string?            Format { get; set; }
}

// ── Character (characters/name.json) ──────────────────────────────────────

public class PsychCharacter
{
    [JsonPropertyName("animations")]     public List<PsychAnim>  Animations    { get; set; } = new();
    [JsonPropertyName("image")]          public string           Image         { get; set; } = "";
    [JsonPropertyName("scale")]          public float            Scale         { get; set; } = 1f;
    [JsonPropertyName("position")]       public List<float>?     Position      { get; set; }
    [JsonPropertyName("camera_position")]public List<float>?     CameraPosition{ get; set; }
    [JsonPropertyName("flip_x")]         public bool             FlipX         { get; set; }
    [JsonPropertyName("no_antialiasing")]public bool             NoAntialiasing{ get; set; }
    [JsonPropertyName("healthicon")]     public string?          HealthIcon    { get; set; }
    [JsonPropertyName("healthbar_colors")]public List<int>?      HealthbarColors{ get; set; }
    [JsonPropertyName("sing_duration")]  public float            SingDuration  { get; set; } = 4f;

    public float PosX => Position?.Count > 0 ? Position[0] : 0f;
    public float PosY => Position?.Count > 1 ? Position[1] : 0f;
}

public class PsychAnim
{
    [JsonPropertyName("anim")]    public string       Anim    { get; set; } = "";
    [JsonPropertyName("name")]    public string       Name    { get; set; } = "";
    [JsonPropertyName("fps")]     public int          Fps     { get; set; } = 24;
    [JsonPropertyName("loop")]    public bool         Loop    { get; set; }
    [JsonPropertyName("indices")] public List<int>?   Indices { get; set; }
    [JsonPropertyName("offsets")] public List<float>? Offsets { get; set; }

    public float OffsetX => Offsets?.Count > 0 ? Offsets[0] : 0f;
    public float OffsetY => Offsets?.Count > 1 ? Offsets[1] : 0f;
}

// ── Stage (stages/name.json) ───────────────────────────────────────────────

public class PsychStage
{
    [JsonPropertyName("directory")]   public string?    Directory   { get; set; }
    [JsonPropertyName("defaultZoom")] public float      DefaultZoom { get; set; } = 1f;
    [JsonPropertyName("isPixelStage")]public bool       IsPixelStage{ get; set; }
    [JsonPropertyName("boyfriend")]   public List<float>?Boyfriend  { get; set; }
    [JsonPropertyName("girlfriend")]  public List<float>?Girlfriend { get; set; }
    [JsonPropertyName("opponent")]    public List<float>?Opponent   { get; set; }

    public float BfX  => Boyfriend?.Count > 0 ? Boyfriend[0]  : 770f;
    public float BfY  => Boyfriend?.Count > 1 ? Boyfriend[1]  : 100f;
    public float OppX => Opponent?.Count  > 0 ? Opponent[0]   : 100f;
    public float OppY => Opponent?.Count  > 1 ? Opponent[1]   : 100f;
    public float GfX  => Girlfriend?.Count> 0 ? Girlfriend[0] : 400f;
    public float GfY  => Girlfriend?.Count> 1 ? Girlfriend[1] : 130f;
}

// ── Sparrow XML atlas ──────────────────────────────────────────────────────
// <TextureAtlas imagePath="...">
//   <SubTexture name="animName0000" x y width height frameX frameY frameWidth frameHeight/>
// </TextureAtlas>

public class SparrowFrame
{
    public string Name        { get; set; } = "";
    public int    X           { get; set; }
    public int    Y           { get; set; }
    public int    Width       { get; set; }
    public int    Height      { get; set; }
    public int    FrameX      { get; set; }   // trim offset X (optional)
    public int    FrameY      { get; set; }   // trim offset Y (optional)
    public int    FrameWidth  { get; set; }   // original width before trim (optional)
    public int    FrameHeight { get; set; }   // original height before trim (optional)
}

