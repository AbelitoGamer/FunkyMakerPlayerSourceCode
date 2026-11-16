using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;

namespace FunkyMakerPlayer;

/// <summary>
/// Cornered-specific event handler that fuses FNF mod events with FunkyMakerPlayer's event system.
/// This is exclusive to the Cornered level and provides custom visual effects, camera movements,
/// and stage animations based on the original FNF Cornered mod's events from hard.json.
/// 
/// Event times are read from hard.json (in milliseconds) and converted to seconds for FMM compatibility.
/// 
/// Original FNF Events from hard.json include:
/// - Camera movements and zooms
/// - Screen flashes and fades
/// - Character changes and animations
/// - Scroll speed changes
/// - Visual effects (bloom, saturation, cinematic bars)
/// - HScript calls for custom sequences (backgroundFade, phantomIn, phantomOut)
/// 
/// FunkyMakerPlayer Integration:
/// - Uses EventProcessor for camera, flashes, and object control
/// - Adds time-based events that trigger at specific song positions
/// - Extends existing event system without breaking compatibility
/// </summary>
public class CorneredEventHandler
{
    private readonly EventProcessor _eventProcessor;
    private readonly FnmmLevel _level;
    private readonly bool _isFinalHours;
    
    // Time-based event mapping (seconds -> action) - calculated from BPM
    private readonly Dictionary<float, Action> _timeEvents = new();
    private readonly HashSet<float> _firedEvents = new(); // Track fired events
    
    // Custom visual effects state
    private float _particleSpeed = 0.7f;
    private float _particleAlpha = 0.6f;
    private bool _beamVisible = false;
    private bool _snowEnabled = false;
    
    // Yellow arrows (NOTE_undyne mechanic) - chart modification system
    private readonly Random _random = new Random(); // Per-user randomization
    
    // Custom overlay for black screen effect (not used yet, for future implementation)
    private class FlxSprite
    {
        public float Alpha { get; set; }
        public bool Visible { get; set; } = true;
    }
    
    public bool BeamVisible => _beamVisible;
    public bool SnowEnabled => _snowEnabled;
    public float ParticleSpeed => _particleSpeed;
    public float ParticleAlpha => _particleAlpha;
    
    
    public CorneredEventHandler(EventProcessor eventProcessor, FnmmLevel level, string variation)
    {
        _eventProcessor = eventProcessor;
        _level = level;
        _isFinalHours = variation?.ToLowerInvariant() == "final hours";
        
        Console.WriteLine($"[CorneredEvents] Initializing for variation: {variation ?? "default"} (finalHours={_isFinalHours})");
        
        InitializeStepEvents();
    }
    
    /// <summary>
    /// Registers all time-based events from hard.json
    /// Key events extracted from the Cornered chart with exact timings
    /// </summary>
    private void InitializeStepEvents()
    {
        float bpm = _level.Data.Bpm;
        Console.WriteLine($"[CorneredEvents] Initializing events for BPM={bpm}");
        
        // Yellow arrow ranges (NOTE_undyne from hard.json)
        // These notes appear scrambled/random and move to correct positions
        InitializeYellowArrowRanges();
        
        // Key events from hard.json (times in seconds)
        // These are major visual/gameplay moments from the original mod
        
        // 92.5625s - Background fade starts (phantom mode begins)
        AddTimeEvent(92.5625f, () => {
            Console.WriteLine("[CorneredEvents] Background fade - Phantom mode begins");
            BackgroundFade();
        });
        
        // 95.125s - Phantom Papyrus appears
        AddTimeEvent(95.125f, () => {
            Console.WriteLine("[CorneredEvents] Phantom Papyrus appears");
            PhantomIn();
            // Camera flash, character change to phantom_paps
            _eventProcessor.ActiveFlashes.Add(new ScreenFlash {
                Color = Color.Black,
                Duration = 0.5f,
                Elapsed = 0f
            });
        });
        
        // 138.375s - Phantom Out, Sans returns
        AddTimeEvent(138.375f, () => {
            Console.WriteLine("[CorneredEvents] Phantom Out - Sans returns");
            PhantomOut();
            _eventProcessor.Camera.StartShake(15f, 0.05f, 12f);
        });
        
        // 161.75s - First Attack (sans attack-right animation)
        AddTimeEvent(161.75f, () => {
            Console.WriteLine("[CorneredEvents] FIRST ATTACK!");
            _eventProcessor.Camera.StartShake(20f, 0.05f, 10f);
            _eventProcessor.ActiveFlashes.Add(new ScreenFlash {
                Color = Color.Black,
                Duration = 0.3f,
                Elapsed = 0f
            });
        });
        
        // 164.875s - Undyne Hurt appears
        AddTimeEvent(164.875f, () => {
            Console.WriteLine("[CorneredEvents] Undyne appears (hurt)");
            _eventProcessor.ActiveFlashes.Add(new ScreenFlash {
                Color = new Color(255, 255, 255, 128),
                Duration = 1f,
                Elapsed = 0f
            });
        });
        
        // 192.875s - UNDYING TRANSFORMATION (biggest event!)
        AddTimeEvent(192.875f, () => {
            Console.WriteLine("[CorneredEvents] UNDYING TRANSFORMATION!");
            _beamVisible = true;
            _snowEnabled = true;
            _particleSpeed = 1f;
            _particleAlpha = 1f;
            
            // Massive camera shake
            _eventProcessor.Camera.StartShake(25f, 0.05f, 15f);
            
            // Bright flash
            _eventProcessor.ActiveFlashes.Add(new ScreenFlash {
                Color = Color.White,
                Duration = 1.5f,
                Elapsed = 0f
            });
        });
        
        // 269.5s - Second Attack
        AddTimeEvent(269.5f, () => {
            Console.WriteLine("[CorneredEvents] SECOND ATTACK!");
            _eventProcessor.Camera.StartShake(20f, 0.05f, 12f);
            _eventProcessor.ActiveFlashes.Add(new ScreenFlash {
                Color = Color.Black,
                Duration = 0.3f,
                Elapsed = 0f
            });
        });
        
        // 272.875s - Undyne dying (final transformation)
        AddTimeEvent(272.875f, () => {
            Console.WriteLine("[CorneredEvents] Undyne dying - Final phase");
            _eventProcessor.Camera.StartShake(15f, 0.05f, 10f);
            _beamVisible = false;
            _particleSpeed = 0.5f;
        });
        

        
        Console.WriteLine($"[CorneredEvents] Registered {_timeEvents.Count} time-based events");
    }
    
    /// <summary>
    /// Initialize yellow arrow notes from hard.json NOTE_undyne notes
    /// These notes appear scrambled (yellow) and animate to correct positions
    /// All 70 NOTE_undyne (type:1) notes from strumLines[1] in hard.json
    /// </summary>
    public static void LoadHardJsonChart(FnmmLevel level)
    {
        Console.WriteLine($"[CorneredEvents] Loading hard.json chart...");
        
        // Load hard.json and convert to FMM format IN MEMORY
        string json = null;
        try
        {
            var assembly = Assembly.GetExecutingAssembly();
            var resourceName = "events.hard.json";
            
            using (var stream = assembly.GetManifestResourceStream(resourceName))
            {
                if (stream != null)
                {
                    using (var reader = new StreamReader(stream))
                    {
                        json = reader.ReadToEnd();
                        Console.WriteLine($"[CorneredEvents] Loaded hard.json from embedded resource");
                    }
                }
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[CorneredEvents] Failed to load embedded resource: {ex.Message}");
        }
        
        if (json == null)
        {
            string hardJsonPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "events", "hard.json");
            if (!File.Exists(hardJsonPath))
            {
                Console.WriteLine($"[CorneredEvents] WARNING: hard.json not found");
                return;
            }
            json = File.ReadAllText(hardJsonPath);
        }
        
        try
        {
            using var doc = System.Text.Json.JsonDocument.Parse(json);
            var root = doc.RootElement;
            
            // Get BPM
            float fnfBpm = 120f;
            if (root.TryGetProperty("meta", out var meta) && meta.TryGetProperty("bpm", out var bpmProp))
                fnfBpm = bpmProp.GetSingle();
            
            float fmmBpm = 150f; // FMM BPM for this song
            
            // Get strumLines
            if (!root.TryGetProperty("strumLines", out var strumLines) || strumLines.GetArrayLength() < 2)
            {
                Console.WriteLine($"[CorneredEvents] WARNING: Invalid strumLines in hard.json");
                return;
            }
            
            // Convert ALL notes from hard.json to FMM format
            var newStrumlines = new List<List<List<FnmmNote>>>();
            var yellowArrowPositions = new List<(int lane, int noteIndex)>();
            
            for (int strumlineIdx = 0; strumlineIdx < 2; strumlineIdx++)
            {
                var strumline = strumLines[strumlineIdx];
                var notes = strumline.GetProperty("notes");
                
                var lanes = new List<List<FnmmNote>> { new(), new(), new(), new() };
                
                foreach (var note in notes.EnumerateArray())
                {
                    float timeMs = note.GetProperty("time").GetSingle();
                    int laneId = note.GetProperty("id").GetInt32();
                    int noteType = note.TryGetProperty("type", out var typeProp) ? typeProp.GetInt32() : 0;
                    float lengthMs = note.TryGetProperty("length", out var lenProp) ? lenProp.GetSingle() : 0;
                    
                    if (laneId < 0 || laneId >= 4) continue;
                    
                    // Convert time
                    float timeSeconds = timeMs / 1000f;
                    float timeBeats = timeSeconds * fmmBpm / 60f;
                    float durationSeconds = lengthMs / 1000f;
                    float durationBeats = durationSeconds * fmmBpm / 60f;
                    
                    var fmmNote = new FnmmNote
                    {
                        Start = timeBeats,
                        Duration = durationBeats,
                        Props = new NoteProps
                        {
                            Skin = NoteSkins.Default, // skins are visual (0/1/2), not lane ids
                            OnPressEvent = -1,
                            OnMissEvent = -1
                        }
                    };
                    
                    lanes[laneId].Add(fmmNote);
                    
                    // Mark yellow arrows (type 1, player strumline only)
                    if (strumlineIdx == 1 && noteType == 1)
                    {
                        yellowArrowPositions.Add((laneId, lanes[laneId].Count - 1));
                    }
                }
                
                // Sort lanes by time
                foreach (var lane in lanes)
                    lane.Sort((a, b) => a.Start.CompareTo(b.Start));
                
                newStrumlines.Add(lanes);
            }
            
            // REPLACE the chart in memory
            level.Data.Strumlines = newStrumlines;
            level.Data.Bpm = fmmBpm;
            
            Console.WriteLine($"[CorneredEvents] ✓ Replaced chart with hard.json");
            Console.WriteLine($"[CorneredEvents]   Opponent: {newStrumlines[0].Sum(l => l.Count)} notes");
            Console.WriteLine($"[CorneredEvents]   Player: {newStrumlines[1].Sum(l => l.Count)} notes");
            Console.WriteLine($"[CorneredEvents]   Yellow arrows: {yellowArrowPositions.Count}");
            
            // Now randomize the yellow arrows by MOVING them physically between lanes
            var random = new Random(Environment.UserName.GetHashCode());
            var notesToMove = new List<(int fromLane, int noteIndex, int toLane)>();
            
            foreach (var (lane, noteIndex) in yellowArrowPositions)
            {
                // Generate random lane EXCLUDING original
                var availableLanes = new List<int>();
                for (int i = 0; i < 4; i++)
                {
                    if (i != lane) availableLanes.Add(i);
                }
                int targetLane = availableLanes[random.Next(availableLanes.Count)];
                
                // Queue note for moving
                notesToMove.Add((lane, noteIndex, targetLane));
            }
            
            // Execute all moves in reverse order to preserve indices
            notesToMove.Reverse();
            int modifiedCount = 0;
            
            foreach (var (fromLane, noteIndex, toLane) in notesToMove)
            {
                var note = newStrumlines[1][fromLane][noteIndex];
                
                // Remove from original lane
                newStrumlines[1][fromLane].RemoveAt(noteIndex);
                
                // Add to target lane
                newStrumlines[1][toLane].Add(note);
                
                modifiedCount++;
                
                if (modifiedCount <= 5)
                {
                    Console.WriteLine($"[CorneredEvents] ✓ Moved yellow arrow at {note.Start:F2} beats: lane {fromLane} → {toLane}");
                }
            }
            
            // Re-sort all player lanes by Start time to maintain proper ordering
            for (int lane = 0; lane < 4; lane++)
            {
                newStrumlines[1][lane] = newStrumlines[1][lane].OrderBy(n => n.Start).ToList();
            }
            
            Console.WriteLine($"[CorneredEvents] ✓✓✓ Randomized {modifiedCount} yellow arrows");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[CorneredEvents] ERROR: {ex.Message}");
        }
    }
    
    /// <summary>
    /// Load yellow arrow timings from hard.json and match them to data.json notes with WIDE tolerance.
    /// We don't replace the chart - we just find similar notes and randomize them.
    /// </summary>
    private void InitializeYellowArrowRanges()
    {
        Console.WriteLine($"[CorneredEvents] Loading yellow arrow timings from hard.json...");
        
        // Load hard.json
        string json = null;
        try
        {
            var assembly = Assembly.GetExecutingAssembly();
            var resourceName = "FunkyMakerPlayer.events.hard.json";
            
            using (var stream = assembly.GetManifestResourceStream(resourceName))
            {
                if (stream != null)
                {
                    using (var reader = new StreamReader(stream))
                    {
                        json = reader.ReadToEnd();
                        Console.WriteLine($"[CorneredEvents] Loaded hard.json from embedded resource");
                    }
                }
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[CorneredEvents] Failed to load embedded resource: {ex.Message}");
            return;
        }
        
        if (json == null)
        {
            Console.WriteLine($"[CorneredEvents] WARNING: Could not load hard.json");
            return;
        }
        
        try
        {
            using var doc = System.Text.Json.JsonDocument.Parse(json);
            var root = doc.RootElement;
            
            // Get BPM from hard.json
            float hardBpm = 120f;
            if (root.TryGetProperty("meta", out var meta) && meta.TryGetProperty("bpm", out var bpmProp))
                hardBpm = bpmProp.GetSingle();
            
            float fmmBpm = _level.Data.Bpm; // Use level's actual BPM
            
            // Get yellow arrow timings from hard.json (type:1 notes in strumLines[1])
            if (!root.TryGetProperty("strumLines", out var strumLines) || strumLines.GetArrayLength() < 2)
            {
                Console.WriteLine($"[CorneredEvents] WARNING: Invalid strumLines in hard.json");
                return;
            }
            
            var yellowTimings = new List<(float timeBeats, int originalLane)>();
            var playerStrumline = strumLines[1];
            var notes = playerStrumline.GetProperty("notes");
            
            foreach (var note in notes.EnumerateArray())
            {
                int noteType = note.TryGetProperty("type", out var typeProp) ? typeProp.GetInt32() : 0;
                
                if (noteType == 1) // Yellow arrow
                {
                    float timeMs = note.GetProperty("time").GetSingle();
                    int laneId = note.GetProperty("id").GetInt32();
                    
                    // Convert to beats using LEVEL's BPM
                    float timeSeconds = timeMs / 1000f;
                    float timeBeats = (timeSeconds * fmmBpm) / 60f;
                    
                    yellowTimings.Add((timeBeats, laneId % 4));
                }
            }
            
            Console.WriteLine($"[CorneredEvents] Found {yellowTimings.Count} yellow arrows in hard.json");
            
            // Now match these timings to EXISTING notes in data.json with WIDE tolerance
            var playerNotes = _level.Data.Strumlines[1]; // Player strumline
            var matchedNotes = new List<(int lane, FnmmNote note)>();
            
            const float TOLERANCE = 2.0f; // 2 beats tolerance (very generous)
            
            foreach (var (targetTime, originalLane) in yellowTimings)
            {
                FnmmNote? bestMatch = null;
                int bestLane = -1;
                float bestDistance = float.MaxValue;
                
                // Search ALL lanes for closest note in time
                for (int lane = 0; lane < 4; lane++)
                {
                    foreach (var note in playerNotes[lane])
                    {
                        float distance = Math.Abs(note.Start - targetTime);
                        
                        if (distance < TOLERANCE && distance < bestDistance)
                        {
                            bestMatch = note;
                            bestLane = lane;
                            bestDistance = distance;
                        }
                    }
                }
                
                if (bestMatch != null)
                {
                    matchedNotes.Add((bestLane, bestMatch));
                }
            }
            
            Console.WriteLine($"[CorneredEvents] Matched {matchedNotes.Count}/{yellowTimings.Count} yellow arrows to data.json notes (tolerance={TOLERANCE} beats)");
            
            // Randomize matched notes by MOVING them physically between lanes
            int modifiedCount = 0;
            var random = new Random(Environment.UserName.GetHashCode());
            
            foreach (var (originalLane, note) in matchedNotes)
            {
                // Generate random lane EXCLUDING original
                var availableLanes = new List<int>();
                for (int i = 0; i < 4; i++)
                {
                    if (i != originalLane) availableLanes.Add(i);
                }
                int targetLane = availableLanes[random.Next(availableLanes.Count)];
                
                // Remove from original lane
                playerNotes[originalLane].Remove(note);
                
                // Add to target lane
                playerNotes[targetLane].Add(note);
                
                modifiedCount++;
                
                if (modifiedCount <= 5)
                {
                    Console.WriteLine($"[CorneredEvents] ✓ Moved note at {note.Start:F2} beats: lane {originalLane} → {targetLane}");
                }
            }
            
            // Re-sort all player lanes by Start time
            for (int lane = 0; lane < 4; lane++)
            {
                playerNotes[lane] = playerNotes[lane].OrderBy(n => n.Start).ToList();
                _level.Data.Strumlines[1][lane] = playerNotes[lane];
            }
            
            Console.WriteLine($"[CorneredEvents] ✓✓✓ Randomized {modifiedCount} yellow arrows using data.json chart");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[CorneredEvents] ERROR: {ex.Message}");
        }
    }
    
    /// <summary>
    /// Adds a time event that triggers at a specific song time
    /// </summary>
    private void AddTimeEvent(float timeSeconds, Action action)
    {
        if (!_timeEvents.ContainsKey(timeSeconds))
        {
            _timeEvents[timeSeconds] = action;
        }
        else
        {
            Console.WriteLine($"[CorneredEvents] WARNING: Duplicate time event at {timeSeconds}s");
        }
    }
    
    /// <summary>
    /// Tweens particle speed over time (simulates FlxTween.num)
    /// </summary>
    private void TweenParticleSpeed(float from, float to, float duration)
    {
        Console.WriteLine($"[CorneredEvents] Tweening particle speed: {from} → {to} over {duration:F2}s");
        // TODO: Implement tween system
        // For now, immediately set to target value
        _particleSpeed = to;
    }
    
    /// <summary>
    /// Called every frame to update time-based events and yellow arrow transitions
    /// </summary>
    public void Update(float songTime, float currentBeat)
    {
        // Fire time-based events
        const float tolerance = 0.05f;
        foreach (var kvp in _timeEvents.ToList())
        {
            float eventTime = kvp.Key;
            if (songTime >= eventTime - tolerance && !_firedEvents.Contains(eventTime))
            {
                Console.WriteLine($"[CorneredEvents] Firing event at {eventTime}s (songTime={songTime:F2}s)");
                kvp.Value?.Invoke();
                _firedEvents.Add(eventTime);
            }
        }
    }
    
    /// <summary>
    /// Triggers the "background fade" sequence (phantom mode)
    /// </summary>
    public void BackgroundFade()
    {
        Console.WriteLine("[CorneredEvents] Background fade triggered");
        // TODO: Fade out stage sprites: bg, bg_front, walls, bridge, shards, cracks, bones
        // TODO: Fade out particles
        // TODO: Fade out gf and dad
    }
    
    /// <summary>
    /// Triggers the "phantom in" sequence (ghostly appearance)
    /// </summary>
    public void PhantomIn()
    {
        Console.WriteLine("[CorneredEvents] Phantom in triggered");
        // TODO: Set heat shader strength
        // TODO: Hide gf, fade dad to 0
        // TODO: Fade in stage sprites
        // TODO: Change particle colors to grayscale
        // TODO: Show dust icon instead of normal icon
    }
    
    /// <summary>
    /// Triggers the "phantom out" sequence (return to normal)
    /// </summary>
    public void PhantomOut()
    {
        Console.WriteLine("[CorneredEvents] Phantom out triggered");
        // TODO: Restore normal icons
        // TODO: Show characters
        // TODO: Reset heat shader
        // TODO: Restore particle colors
    }
}
