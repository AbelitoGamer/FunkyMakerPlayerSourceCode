using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace FunkyMakerPlayer;

/// <summary>
/// Converts old-format .FNMM files to the current format in-memory.
///
/// Old format differences:
///   data.json  → uses "notes" (flat 8-lane array) instead of "strumlines"
///   evt.json   → old event format instead of events.json
///   scene.json → objects use "type" (0=bg,1=player,2=enemy) instead of "strumLine"
///   sprite type 0 → same as type 2 (single spritesheet)
/// </summary>
public static class FnmmConverter
{
    private static readonly JsonSerializerOptions _json = new()
    {
        PropertyNameCaseInsensitive = true,
        AllowTrailingCommas         = true,
        ReadCommentHandling         = JsonCommentHandling.Skip,
        WriteIndented               = false,
    };

    /// <summary>
    /// Detects if a ZIP archive uses the old format and converts it to new format bytes.
    /// Returns null if already new format.
    /// </summary>
    public static byte[]? ConvertIfNeeded(string fnmmPath)
    {
        using var zip = ZipFile.OpenRead(fnmmPath);

        // Old format has evt.json instead of events.json
        bool isOld = zip.GetEntry("evt.json") != null;

        // Also check nested layout (subfolder/evt.json)
        if (!isOld)
            isOld = zip.Entries.Any(e =>
                e.FullName.Replace('\\', '/').EndsWith("/evt.json", StringComparison.OrdinalIgnoreCase));

        // Also check if data.json has "notes" instead of "strumlines"
        if (!isOld)
        {
            var dataEntry = zip.Entries.FirstOrDefault(e =>
            {
                string norm = e.FullName.Replace('\\', '/');
                return norm.Equals("data.json", StringComparison.OrdinalIgnoreCase) ||
                       norm.EndsWith("/data.json", StringComparison.OrdinalIgnoreCase);
            });
            if (dataEntry != null)
            {
                using var sr = new StreamReader(dataEntry.Open());
                string dataText = sr.ReadToEnd();
                if (dataText.Contains("\"notes\"") && !dataText.Contains("\"strumlines\""))
                    isOld = true;
            }
        }

        if (!isOld) return null;

        Console.WriteLine("[FnmmConverter] Old format detected, converting...");
        return Convert(zip);
    }

    /// <summary>
    /// Detects if an extracted level directory (e.g. from .rar or .zip) uses the old format
    /// and converts its JSON files in-place.
    /// </summary>
    public static void ConvertDirectoryIfNeeded(string levelDir)
    {
        string dataPath = Path.Combine(levelDir, "data.json");
        if (!File.Exists(dataPath)) return;

        string evtPath = Path.Combine(levelDir, "evt.json");
        string dataJson = File.ReadAllText(dataPath);
        bool isOld = File.Exists(evtPath) ||
                     (dataJson.Contains("\"notes\"") && !dataJson.Contains("\"strumlines\""));

        if (!isOld) return;

        Console.WriteLine("[FnmmConverter] Old format detected in extracted directory, converting...");

        File.WriteAllText(dataPath, ConvertData(dataJson));

        string? evtJson = File.Exists(evtPath) ? File.ReadAllText(evtPath) : null;
        if (File.Exists(evtPath))
            File.Delete(evtPath);
        File.WriteAllText(Path.Combine(levelDir, "events.json"), ConvertEvents(evtJson, dataJson));

        string scenePath = Path.Combine(levelDir, "scene.json");
        if (File.Exists(scenePath))
            File.WriteAllText(scenePath, ConvertScene(File.ReadAllText(scenePath)));

        string spritesDir = Path.Combine(levelDir, "sprites");
        if (Directory.Exists(spritesDir))
        {
            foreach (var sprJsonPath in Directory.EnumerateFiles(spritesDir, "sprite.json", SearchOption.AllDirectories))
                File.WriteAllText(sprJsonPath, FixSpriteJson(File.ReadAllText(sprJsonPath)));
        }
    }

    private static byte[] Convert(ZipArchive zip)
    {
        // Read all entries into memory first (zip is read-only)
        var entries = new Dictionary<string, byte[]>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in zip.Entries)
        {
            if (string.IsNullOrEmpty(entry.Name)) continue; // skip directory entries
            using var ms = new MemoryStream();
            entry.Open().CopyTo(ms);
            entries[entry.FullName.Replace('\\', '/')] = ms.ToArray();
        }

        // Detect prefix (nested layout: "UUID/data.json" → prefix = "UUID/")
        string prefix = "";
        if (!entries.ContainsKey("data.json"))
        {
            var nested = entries.Keys.FirstOrDefault(k =>
                k.EndsWith("/data.json", StringComparison.OrdinalIgnoreCase));
            if (nested != null)
                prefix = nested[..^"data.json".Length];
        }

        string Key(string name) => prefix + name;

        // ── Convert data.json ────────────────────────────────────────────────
        string dataJson = System.Text.Encoding.UTF8.GetString(entries[Key("data.json")]);
        string newDataJson = ConvertData(dataJson);
        entries[Key("data.json")] = System.Text.Encoding.UTF8.GetBytes(newDataJson);

        // ── Convert evt.json → events.json ───────────────────────────────────
        string? evtJson = null;
        string evtKey = Key("evt.json");
        if (entries.TryGetValue(evtKey, out var evtBytes))
        {
            evtJson = System.Text.Encoding.UTF8.GetString(evtBytes);
            entries.Remove(evtKey);
        }
        string newEventsJson = ConvertEvents(evtJson, dataJson);
        entries[Key("events.json")] = System.Text.Encoding.UTF8.GetBytes(newEventsJson);

        // ── Convert scene.json ───────────────────────────────────────────────
        string sceneKey = Key("scene.json");
        if (entries.TryGetValue(sceneKey, out var sceneBytes))
        {
            string sceneJson = System.Text.Encoding.UTF8.GetString(sceneBytes);
            string newSceneJson = ConvertScene(sceneJson);
            entries[sceneKey] = System.Text.Encoding.UTF8.GetBytes(newSceneJson);
        }

        // ── Fix sprite.json files (type 0 is valid, bilinear default, etc.) ──
        foreach (var key in entries.Keys.ToList())
        {
            if (key.EndsWith("/sprite.json", StringComparison.OrdinalIgnoreCase))
            {
                string sprJson = System.Text.Encoding.UTF8.GetString(entries[key]);
                string fixedSpr = FixSpriteJson(sprJson);
                entries[key] = System.Text.Encoding.UTF8.GetBytes(fixedSpr);
            }
        }

        // ── Write new ZIP ────────────────────────────────────────────────────
        using var outMs = new MemoryStream();
        using (var outZip = new ZipArchive(outMs, ZipArchiveMode.Create, leaveOpen: true))
        {
            foreach (var (name, data) in entries)
            {
                var entry = outZip.CreateEntry(name, CompressionLevel.Optimal);
                using var es = entry.Open();
                es.Write(data, 0, data.Length);
            }
        }
        return outMs.ToArray();
    }

    // ── data.json: notes[] → strumlines[][][] ────────────────────────────────
    private static string ConvertData(string oldJson)
    {
        var doc = JsonNode.Parse(oldJson)!.AsObject();

        if (!doc.ContainsKey("notes")) return oldJson; // already new format

        var notes = doc["notes"]!.AsArray();
        float bpm  = doc["bpm"]?.GetValue<float>() ?? 120f;

        // Old format: notes[0..3] = ENEMY (SL1), notes[4..7] = PLAYER (SL0)
        // New: strumlines[0] = player, strumlines[1] = enemy
        int laneCount = notes.Count;

        var strumlines = new JsonArray();

        // SL0 = player = notes[4..7]
        var playerSl = new JsonArray();
        for (int lane = 0; lane < 4; lane++)
        {
            int srcIdx = 4 + lane;
            playerSl.Add(BuildLaneArray(notes, srcIdx));
        }
        strumlines.Add(playerSl);

        // SL1 = enemy = notes[0..3]
        var enemySl = new JsonArray();
        for (int lane = 0; lane < 4; lane++)
        {
            int srcIdx = lane;
            enemySl.Add(BuildLaneArray(notes, srcIdx));
        }
        strumlines.Add(enemySl);

        doc.Remove("notes");
        doc["strumlines"] = strumlines;

        // Mark as version 2.0 (times already in seconds in old format)
        doc["version"] = 2.0;

        // Ensure speed and npb exist with sensible defaults
        if (!doc.ContainsKey("speed")) doc["speed"] = 1.0;
        if (!doc.ContainsKey("npb"))   doc["npb"]   = 4.0;

        return doc.ToJsonString(_json);
    }

    private static JsonArray BuildLaneArray(JsonArray notes, int srcIdx)
    {
        var laneArray = new JsonArray();
        if (srcIdx >= notes.Count) return laneArray;
        foreach (var note in notes[srcIdx]!.AsArray())
        {
            var noteObj = note!.AsObject();
            if (!noteObj.ContainsKey("props") || noteObj["props"] is not JsonObject props)
            {
                props = new JsonObject();
                noteObj["props"] = props;
            }
            if (!props.ContainsKey("skin"))         props["skin"]         = 0.0;
            if (!props.ContainsKey("onPressEvent")) props["onPressEvent"] = -1.0;
            if (!props.ContainsKey("onMissEvent"))  props["onMissEvent"]  = -1.0;
            laneArray.Add(noteObj.DeepClone());
        }
        return laneArray;
    }

    // ── evt.json → events.json ───────────────────────────────────────────────
    private static string ConvertEvents(string? evtJson, string dataJson)
    {
        var eventsList = new JsonArray();
        var actionPacks = new JsonArray();

        if (evtJson == null)
            return JsonSerializer.Serialize(new { events = eventsList, action_packs = actionPacks }, _json);

        var oldEvents = JsonNode.Parse(evtJson)!.AsArray();

        // Group events by time (same time → same event with multiple actions)
        var grouped = new SortedDictionary<double, List<JsonObject>>();
        foreach (var ev in oldEvents)
        {
            var obj = ev!.AsObject();
            double time = obj["time"]?.GetValue<double>() ?? 0.0;
            string type = obj["type"]?.GetValue<string>() ?? "";
            var args = obj["args"]?.AsArray() ?? new JsonArray();

            if (!grouped.ContainsKey(time))
                grouped[time] = new List<JsonObject>();

            // Convert old event type to new action
            var action = ConvertOldEvent(type, args);
            if (action != null)
                grouped[time].Add(action);
        }

        foreach (var (time, actions) in grouped)
        {
            if (actions.Count == 0) continue;
            var actionsArray = new JsonArray();
            foreach (var a in actions) actionsArray.Add(a.DeepClone());
            eventsList.Add(new JsonObject
            {
                ["time"]    = time,
                ["actions"] = actionsArray,
            });
        }

        return JsonSerializer.Serialize(
            new { events = eventsList, action_packs = actionPacks }, _json);
    }

    private static JsonObject? ConvertOldEvent(string type, JsonArray args)
    {
        // Args should always be JSON strings, but accept bare numbers/bools too (ToString of a
        // non-string JsonValue gives its raw JSON text). Numbers are parsed culture-invariant so
        // "0.5" never becomes 5 / 0 on systems that use ',' as the decimal separator.
        string Arg(int i)
        {
            if (i >= args.Count || args[i] is not JsonValue v) return "";
            return v.TryGetValue<string>(out var str) ? str : v.ToJsonString();
        }
        float ArgF(int i) =>
            float.TryParse(Arg(i), System.Globalization.NumberStyles.Float,
                           System.Globalization.CultureInfo.InvariantCulture, out float f) ? f : 0f;

        // Old-format camera cuts always take 0.83 s.
        const double OldCameraTransition = 0.83;

        switch (type.ToLower())
        {
            case "camera":
            {
                string target = Arg(0);
                if (target == "auto")
                {
                    return new JsonObject
                    {
                        ["type"] = "CameraSet",
                        ["props"] = new JsonObject
                        {
                            ["mode"]               = "auto",
                            ["target"]             = 0.0,
                            ["transitionTime"]     = OldCameraTransition,
                            ["transitionTimeUnit"] = "seconds",
                            ["easingType"]         = "default",
                        }
                    };
                }
                // Numeric camera index: 0-based in both formats, copied as-is
                if (float.TryParse(target, System.Globalization.NumberStyles.Float,
                                   System.Globalization.CultureInfo.InvariantCulture, out float camIdx))
                {
                    return new JsonObject
                    {
                        ["type"] = "CameraSet",
                        ["props"] = new JsonObject
                        {
                            ["mode"]               = "focus",
                            ["target"]             = (double)camIdx,  // already 0-based
                            ["transitionTime"]     = OldCameraTransition,
                            ["transitionTimeUnit"] = "seconds",
                            ["easingType"]         = "default",
                        }
                    };
                }
                return null;
            }

            case "changeanim":
                return new JsonObject
                {
                    ["type"] = "ObjectAnimationSet",
                    ["props"] = new JsonObject
                    {
                        ["object"] = (double)ArgF(0),
                        ["anim"]   = (double)ArgF(1),
                    }
                };

            case "changesprite":
                return new JsonObject
                {
                    ["type"] = "ObjectSpriteSet",
                    ["props"] = new JsonObject
                    {
                        ["object"] = (double)ArgF(0),
                        ["sprite"] = Arg(1),
                    }
                };

            case "changetype":
                // Old "type" field on objects — maps to strumLine change
                // type 0=bg → strumLine -1, type 1=player → strumLine 0, type 2=enemy → strumLine 1
                float objIdx = ArgF(0);
                float oldType = ArgF(1);
                float newStrumLine = oldType switch
                {
                    1f => 0f,   // player
                    2f => 1f,   // enemy
                    _  => -1f,  // background
                };
                return new JsonObject
                {
                    ["type"] = "ObjectChartChange",
                    ["props"] = new JsonObject
                    {
                        ["object"]   = (double)objIdx,
                        ["strumLine"] = (double)newStrumLine,
                    }
                };

            case "visible":
                return new JsonObject
                {
                    ["type"] = "ObjectVisibilitySet",
                    ["props"] = new JsonObject
                    {
                        ["object"] = (double)ArgF(0),
                        ["value"]  = Arg(1).ToLower() is "yes" or "true",
                    }
                };

            case "flash":
                return new JsonObject
                {
                    ["type"] = "ScreenFlash",
                    ["props"] = new JsonObject
                    {
                        ["color_r"] = 255.0,
                        ["color_g"] = 255.0,
                        ["color_b"] = 255.0,
                        ["alpha"]   = 0.5,
                        ["duration"] = 0.3,
                        ["timeUnit"] = "seconds",
                    }
                };

            case "shake":
                // Old shake has no args; fixed equivalent in the current format.
                return new JsonObject
                {
                    ["type"] = "CameraShake",
                    ["props"] = new JsonObject
                    {
                        ["delay"]    = 0.10,
                        ["shakes"]   = 2.0,
                        ["strength"] = 12.0,
                        ["timeUnit"] = "seconds",
                    }
                };

            // Old format didn't have these, but we support them for completeness
            case "opacity":
                return new JsonObject
                {
                    ["type"] = "ObjectOpacitySet",
                    ["props"] = new JsonObject
                    {
                        ["object"]         = (double)ArgF(0),
                        ["valueFloat"]     = (double)ArgF(1),
                        ["transitionTime"] = 0.0,
                    }
                };

            case "color":
            case "tint":
                return new JsonObject
                {
                    ["type"] = "ObjectBlendColor",
                    ["props"] = new JsonObject
                    {
                        ["object"]  = (double)ArgF(0),
                        ["color_r"] = (double)ArgF(1),
                        ["color_g"] = (double)ArgF(2),
                        ["color_b"] = (double)ArgF(3),
                    }
                };

            default:
                // Unknown event type — log and skip
                Console.WriteLine($"[FnmmConverter] Unknown old event type: {type}");
                return null;
        }
    }

    // ── scene.json: type → strumLine ─────────────────────────────────────────
    private static string ConvertScene(string oldJson)
    {
        var doc = JsonNode.Parse(oldJson)!.AsObject();
        var objects = doc["objects"]?.AsArray();
        if (objects == null) return oldJson;

        foreach (var obj in objects)
        {
            var o = obj!.AsObject();
            // Old: type 0=bg, 1=player, 2=enemy
            // New: strumLine -1=bg, 0=player, 1=enemy
            if (o.ContainsKey("type") && !o.ContainsKey("strumLine"))
            {
                float oldType = o["type"]?.GetValue<float>() ?? 0f;
                float strumLine = oldType switch
                {
                    1f => 0f,   // player
                    2f => 1f,   // enemy
                    _  => -1f,  // background
                };
                o["strumLine"] = strumLine;
                o.Remove("type");
            }
            // Ensure "fixed" field exists
            if (!o.ContainsKey("fixed")) o["fixed"] = false;
        }

        return doc.ToJsonString(_json);
    }

    // ── sprite.json: ensure required fields have defaults ────────────────────
    private static string FixSpriteJson(string sprJson)
    {
        // type 0 is valid (multi-anim, N PNGs each a horizontal strip — same as type 1).
        // Just ensure each animation entry has all required fields with sensible defaults.
        try
        {
            var doc = JsonNode.Parse(sprJson)!.AsObject();
            var data = doc["data"]?.AsArray();
            if (data != null)
            {
                foreach (var item in data)
                {
                    var anim = item!.AsObject();
                    if (!anim.ContainsKey("bilinear")) anim["bilinear"] = true;
                    if (!anim.ContainsKey("loop"))     anim["loop"]     = true;
                    if (!anim.ContainsKey("spd"))      anim["spd"]      = 2.0;
                    if (!anim.ContainsKey("spd_fps"))  anim["spd_fps"]  = false;
                    if (!anim.ContainsKey("scale"))    anim["scale"]    = 1.0;
                    if (!anim.ContainsKey("offset"))   anim["offset"]   = new JsonObject { ["x"] = 0.0, ["y"] = 0.0 };
                }
            }
            return doc.ToJsonString(_json);
        }
        catch
        {
            return sprJson; // if parsing fails, return as-is
        }
    }
}
