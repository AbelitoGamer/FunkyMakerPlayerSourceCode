using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using Microsoft.Xna.Framework.Input;
using System;
using System.Collections.Generic;
using System.Linq;

namespace FunkyMakerPlayer.Editors.Chart
{
    /// <summary>
    /// Chart Editor - Clean and functional design for modders
    /// Layout: Timeline (top) | Toolbar (left) | Grid (center) | Info Panel (right) | Status Bar (bottom)
    /// </summary>
    public class ChartEditor : IEditor
    {
        // ── Core data ────────────────────────────────────────────────────────
        private readonly string _levelPath;
        private FnmmLevel? _level;
        private readonly Texture2D _pixel;
        private readonly SpriteFont? _font;
        private readonly FontRenderer? _fontRenderer;
        private readonly NoteStyle? _noteStyle;
        
        // ── Editor state ─────────────────────────────────────────────────────
        private EditorPhase _phase = EditorPhase.Loading;
        private string? _errorMessage;
        private EditorTool _currentTool = EditorTool.Select;
        private int _selectedStrumline = 0;  // 0 = player, 1 = opponent
        private int _selectedLane = -1;       // -1 = none, 0-3 = lane
        
        // ── Loading animation ────────────────────────────────────────────────
        private float _loadingSpinnerRotation = 0f;
        private float _loadingFadeAlpha = 0f;
        private bool _waitingForClick = false;
        private System.Threading.Tasks.Task? _loadingTask = null;
        
        // ── Receptor animation ───────────────────────────────────────────────
        private float[] _receptorPressTime = new float[LANE_COUNT];  // Time since press
        private const float RECEPTOR_ANIM_DURATION = 0.1f;  // Press animation duration
        
        // ── Audio playback ───────────────────────────────────────────────────
        private EditorAudioManager _audioManager = new EditorAudioManager();
        private bool _isPlaying = false;
        private float _playbackTime = 0f;  // Current playback time in seconds
        
        // ── Character preview ────────────────────────────────────────────────
        private const int PREVIEW_WIDTH = 300;
        private const int PREVIEW_HEIGHT = 200;
        private const int PREVIEW_MARGIN = 20;
        private Rectangle _previewRect;
        private float[] _characterSingTime = new float[2]; // Time remaining for sing animation [player, opponent]
        private int[] _characterSingLane = new int[2];     // Which lane they're singing [-1 = idle]
        private const float SING_ANIM_DURATION = 0.15f;
        
        // ── IEditor implementation ───────────────────────────────────────────
        public bool BackPressed { get; private set; }
        public bool IsModified { get; private set; }
        
        // ── Grid & Scroll ────────────────────────────────────────────────────
        private float _scrollY = 0f;          // Vertical scroll position (in time units: beats or seconds)
        private float _zoom = 1.0f;           // Zoom level (1.0 = default)
        private float _scrollSpeed = 1.0f;    // Scroll speed multiplier (affects visual spacing)
        private const float BEAT_HEIGHT = 80f; // Pixels per beat at zoom 1.0 (for beat-based charts)
        private const float SECOND_HEIGHT = 120f; // Pixels per second at zoom 1.0 (for time-based charts)
        private const int LANE_COUNT = 4;
        private const float LANE_WIDTH = 120f;
        private const float RECEPTOR_Y_OFFSET = 0.2f; // Receptor bar at 20% from top (like FNF)
        
        // ── Layout dimensions (set in UpdateLayout) ──────────────────────────
        private Rectangle _timelineRect;
        private Rectangle _toolbarRect;
        private Rectangle _gridRect;
        private Rectangle _infoPanelRect;
        private Rectangle _statusBarRect;
        
        // ── Colors ───────────────────────────────────────────────────────────
        private static readonly Color BG_DARK = new Color(20, 20, 25);
        private static readonly Color BG_MEDIUM = new Color(30, 30, 35);
        private static readonly Color BG_LIGHT = new Color(40, 40, 45);
        private static readonly Color ACCENT = new Color(100, 150, 255);
        private static readonly Color GRID_LINE = new Color(60, 60, 65);
        private static readonly Color BEAT_LINE = new Color(80, 80, 85);
        private static readonly Color MEASURE_LINE = new Color(120, 120, 125);
        
        private static readonly Color[] LANE_COLORS = new[]
        {
            new Color(194, 75, 153),  // Purple/Pink - Left
            new Color(0, 255, 255),   // Cyan - Down
            new Color(18, 250, 5),    // Green - Up
            new Color(249, 57, 63),   // Red - Right
        };
        
        // ── Input state ──────────────────────────────────────────────────────
        private KeyboardState _prevKeyboard;
        private MouseState _prevMouse;
        
        // ── Constructor ──────────────────────────────────────────────────────
        public ChartEditor(string levelPath, Texture2D pixel, SpriteFont? font, 
                          FontRenderer? fontRenderer, NoteStyle? noteStyle = null)
        {
            _levelPath = levelPath;
            _pixel = pixel;
            _font = font;
            _fontRenderer = fontRenderer;
            _noteStyle = noteStyle;
            
            // Initialize character arrays
            _characterSingLane[0] = -1;
            _characterSingLane[1] = -1;
            
            LoadLevel();
        }
        
        // ── Destructor / Cleanup ─────────────────────────────────────────────
        ~ChartEditor()
        {
            _audioManager?.Dispose();
        }
        
        // ── Load level from file (asíncrono) ─────────────────────────────────
        private void LoadLevel()
        {
            _loadingTask = System.Threading.Tasks.Task.Run(() =>
            {
                try
                {
                    _level = FnmmLoader.Load(_levelPath);
                    Console.WriteLine($"[ChartEditor] Loaded level: {_levelPath}");
                    Console.WriteLine($"[ChartEditor] BPM: {_level.Data.Bpm}, EffectiveBPM: {_level.Data.EffectiveBpm}");
                    Console.WriteLine($"[ChartEditor] Version: {_level.Data.Version}, TimesAreSeconds: {_level.Data.TimesAreSeconds}");
                    
                    // Load music on main thread after level loads
                    System.Threading.Tasks.Task.Run(() =>
                    {
                        System.Threading.Thread.Sleep(100); // Small delay to ensure we're past loading
                        if (_level != null)
                        {
                            _audioManager.LoadFromLevel(_level);
                        }
                    });
                }
                catch (Exception ex)
                {
                    _errorMessage = $"Failed to load level: {ex.Message}";
                    Console.WriteLine($"[ChartEditor] Error: {ex.Message}");
                }
            });
        }
        
        // ── Update ───────────────────────────────────────────────────────────
        public void Update(float dt, KeyboardState kb)
        {
            var ms = Mouse.GetState();
            
            // Update receptor press animations
            for (int i = 0; i < LANE_COUNT; i++)
            {
                if (_receptorPressTime[i] > 0f)
                    _receptorPressTime[i] -= dt;
            }
            
            // Update character sing animations
            for (int i = 0; i < 2; i++)
            {
                if (_characterSingTime[i] > 0f)
                    _characterSingTime[i] -= dt;
                else
                    _characterSingLane[i] = -1; // Return to idle
            }
            
            // Handle loading state
            if (_phase == EditorPhase.Loading)
            {
                _loadingSpinnerRotation += dt * 3f; // Rotate spinner
                
                // Check if loading finished
                if (_loadingTask != null && _loadingTask.IsCompleted)
                {
                    if (_errorMessage != null)
                    {
                        _phase = EditorPhase.Error;
                    }
                    else if (_level != null)
                    {
                        _phase = EditorPhase.LoadComplete;
                        _waitingForClick = true;
                    }
                    _loadingTask = null;
                }
                
                _prevKeyboard = kb;
                _prevMouse = ms;
                return;
            }
            
            if (_phase == EditorPhase.LoadComplete)
            {
                _loadingSpinnerRotation += dt * 3f; // Keep rotating
                _loadingFadeAlpha += dt * 2f; // Fade in "Click to continue"
                
                // Wait for click to continue
                if (ms.LeftButton == ButtonState.Pressed && _prevMouse.LeftButton == ButtonState.Released)
                {
                    _phase = EditorPhase.Ready;
                    _waitingForClick = false;
                    Console.WriteLine("[ChartEditor] User clicked, entering editor");
                }
                
                _prevKeyboard = kb;
                _prevMouse = ms;
                return;
            }
            
            if (_phase != EditorPhase.Ready) 
            {
                _prevKeyboard = kb;
                _prevMouse = ms;
                return;
            }
            
            HandleKeyboard(kb, dt);
            HandleMouse(ms);
            
            // Update playback if playing
            if (_isPlaying && _audioManager.IsLoaded)
            {
                // Sync with ACTUAL audio playback time (not dt accumulation)
                _playbackTime = _audioManager.CurrentTime / 1000f; // Convert ms to seconds
                
                // Sync scroll position with playback time
                if (_level != null)
                {
                    // If times are in seconds (Version >= 2.0), _playbackTime IS the beat position
                    // If times are in beats (Version < 2.0), convert: (seconds * BPM) / 60
                    if (_level.Data.TimesAreSeconds)
                    {
                        _scrollY = _playbackTime; // Times are already in seconds
                    }
                    else
                    {
                        float bpm = _level.Data.EffectiveBpm;
                        _scrollY = (_playbackTime * bpm) / 60f; // Convert seconds to beats
                    }
                    
                    // Check for notes hitting the receptor bar and trigger character animations
                    CheckNotesAtReceptor();
                }
            }
            
            _prevKeyboard = kb;
            _prevMouse = ms;
        }
        
        // ── Keyboard input ───────────────────────────────────────────────────
        private void HandleKeyboard(KeyboardState kb, float dt)
        {
            // Space to play/pause preview
            if (JustPressed(kb, Keys.Space))
            {
                _isPlaying = !_isPlaying;
                
                if (_isPlaying && _audioManager.IsLoaded)
                {
                    // Seek audio to current scroll position before playing
                    float startTimeMs = _scrollY * 1000f; // Convert to milliseconds
                    _audioManager.Seek(startTimeMs);
                    _audioManager.Play();
                    _playbackTime = _scrollY; // Sync playback time
                    Console.WriteLine($"[ChartEditor] Playback started from {_scrollY:F2}s");
                }
                else if (_audioManager.IsLoaded)
                {
                    _audioManager.Pause();
                    Console.WriteLine($"[ChartEditor] Playback paused at {_playbackTime:F2}s");
                }
            }
            
            // Don't allow other inputs while playing
            if (_isPlaying) return;
            
            // Scroll with arrow keys (INVERTIDO - hacia abajo como FNF)
            float scrollSpeed = 4f * _zoom * dt;
            if (kb.IsKeyDown(Keys.Up)) _scrollY -= scrollSpeed;    // Up = scroll hacia atrás
            if (kb.IsKeyDown(Keys.Down)) _scrollY += scrollSpeed;  // Down = scroll hacia adelante
            
            // Clamp scroll position to valid range
            if (_scrollY < 0f) _scrollY = 0f;
            
            // Zoom with +/- (affects visual spacing)
            if (JustPressed(kb, Keys.OemPlus) || JustPressed(kb, Keys.Add))
            {
                _zoom = Math.Clamp(_zoom + 0.25f, 0.25f, 4f);
                Console.WriteLine($"[ChartEditor] Zoom: {_zoom:F2}x");
            }
            if (JustPressed(kb, Keys.OemMinus) || JustPressed(kb, Keys.Subtract))
            {
                _zoom = Math.Clamp(_zoom - 0.25f, 0.25f, 4f);
                Console.WriteLine($"[ChartEditor] Zoom: {_zoom:F2}x");
            }
            
            // Scroll Speed with O/P (más accesible y cerca de otras teclas)
            if (JustPressed(kb, Keys.O))
            {
                _scrollSpeed = Math.Clamp(_scrollSpeed - 0.25f, 0.25f, 4f);
                Console.WriteLine($"[ChartEditor] Scroll Speed: {_scrollSpeed:F2}x");
            }
            if (JustPressed(kb, Keys.P))
            {
                _scrollSpeed = Math.Clamp(_scrollSpeed + 0.25f, 0.25f, 4f);
                Console.WriteLine($"[ChartEditor] Scroll Speed: {_scrollSpeed:F2}x");
            }
            
            // Tool selection (1-4)
            if (JustPressed(kb, Keys.D1)) _currentTool = EditorTool.Select;
            if (JustPressed(kb, Keys.D2)) _currentTool = EditorTool.PlaceNote;
            if (JustPressed(kb, Keys.D3)) _currentTool = EditorTool.PlaceHold;
            if (JustPressed(kb, Keys.D4)) _currentTool = EditorTool.Delete;
            
            // Strumline selection (Q/W) - with validation and logging
            if (JustPressed(kb, Keys.Q))
            {
                if (_level != null && _level.Data.Strumlines.Count > 0)
                {
                    _selectedStrumline = 0; // Player
                    Console.WriteLine($"[ChartEditor] Selected strumline: Player (0)");
                }
            }
            if (JustPressed(kb, Keys.W))
            {
                if (_level != null && _level.Data.Strumlines.Count > 1)
                {
                    _selectedStrumline = 1; // Opponent
                    Console.WriteLine($"[ChartEditor] Selected strumline: Opponent (1)");
                }
                else
                {
                    Console.WriteLine($"[ChartEditor] Cannot switch to Opponent - only {_level?.Data.Strumlines.Count ?? 0} strumline(s) available");
                }
            }
            
            // Save (Ctrl+S)
            if (kb.IsKeyDown(Keys.LeftControl) && JustPressed(kb, Keys.S))
                SaveLevel();
            
            // Escape to go back
            if (JustPressed(kb, Keys.Escape))
            {
                _audioManager.Stop();
                BackPressed = true;
            }
        }
        
        // ── Mouse input ──────────────────────────────────────────────────────
        private void HandleMouse(MouseState ms)
        {
            // Scroll with mouse wheel
            int wheelDelta = ms.ScrollWheelValue - _prevMouse.ScrollWheelValue;
            if (wheelDelta != 0)
            {
                _scrollY += wheelDelta * 0.003f * _zoom; // Adjust scroll sensitivity with zoom
            }
            
            // Left click in grid area
            if (ms.LeftButton == ButtonState.Pressed && 
                _prevMouse.LeftButton == ButtonState.Released &&
                _gridRect.Contains(ms.Position))
            {
                HandleGridClick(ms.X, ms.Y);
            }
        }
        
        // ── Handle click in grid area ────────────────────────────────────────
        private void HandleGridClick(int mouseX, int mouseY)
        {
            if (_level == null) return;
            
            // Convert mouse position to grid coordinates
            float relX = mouseX - _gridRect.X;
            float relY = mouseY - _gridRect.Y;
            
            // Determine lane (0-3)
            float totalGridWidth = LANE_WIDTH * LANE_COUNT;
            float gridStartX = (_gridRect.Width - totalGridWidth) / 2f;
            int lane = (int)((relX - gridStartX) / LANE_WIDTH);
            
            if (lane < 0 || lane >= LANE_COUNT) return;
            
            // Determine time position (INVERTIDO - desde receptor)
            float scaledTimeHeight = GetScaledTimeHeight();
            float receptorY = _gridRect.Y + _gridRect.Height * RECEPTOR_Y_OFFSET;
            float timePos = _scrollY + (relY - (_gridRect.Height * RECEPTOR_Y_OFFSET)) / scaledTimeHeight;
            
            // Snap to grid
            float snapResolution = _level.Data.TimesAreSeconds ? 0.0625f : 0.25f; // 1/16 second or 1/4 beat
            float snappedTime = MathF.Round(timePos / snapResolution) * snapResolution;
            
            Console.WriteLine($"[ChartEditor] Clicked lane {lane} at time {snappedTime:F2}");
            
            // Animate receptor press
            _receptorPressTime[lane] = RECEPTOR_ANIM_DURATION;
            
            // Place note based on current tool
            if (_currentTool == EditorTool.PlaceNote)
            {
                PlaceNote(lane, snappedTime, 0f);
                IsModified = true;
            }
            else if (_currentTool == EditorTool.PlaceHold)
            {
                PlaceNote(lane, snappedTime, 0.5f); // Default hold duration
                IsModified = true;
            }
            else if (_currentTool == EditorTool.Delete)
            {
                DeleteNoteAt(lane, snappedTime);
                IsModified = true;
            }
            
            _selectedLane = lane;
        }
        
        // ── Place note at position ───────────────────────────────────────────
        private void PlaceNote(int lane, float beat, float duration)
        {
            if (_level == null || _selectedStrumline >= _level.Data.Strumlines.Count) return;
            
            var strumline = _level.Data.Strumlines[_selectedStrumline];
            if (lane >= strumline.Count) return;
            
            // Check if note already exists at this position
            var existing = strumline[lane].FirstOrDefault(n => MathF.Abs(n.Start - beat) < 0.01f);
            if (existing != null)
            {
                Console.WriteLine($"[ChartEditor] Note already exists at beat {beat:F2}");
                return;
            }
            
            var newNote = new FnmmNote
            {
                Start = beat,
                Duration = duration,
                Props = new NoteProps { Skin = 0 }
            };
            
            strumline[lane].Add(newNote);
            Console.WriteLine($"[ChartEditor] Placed note at lane {lane}, beat {beat:F2}, duration {duration:F2}");
        }
        
        // ── Delete note at position ──────────────────────────────────────────
        private void DeleteNoteAt(int lane, float beat)
        {
            if (_level == null || _selectedStrumline >= _level.Data.Strumlines.Count) return;
            
            var strumline = _level.Data.Strumlines[_selectedStrumline];
            if (lane >= strumline.Count) return;
            
            var noteToDelete = strumline[lane].FirstOrDefault(n => MathF.Abs(n.Start - beat) < 0.1f);
            if (noteToDelete != null)
            {
                strumline[lane].Remove(noteToDelete);
                Console.WriteLine($"[ChartEditor] Deleted note at lane {lane}, beat {beat:F2}");
            }
        }
        
        // ── Save level ───────────────────────────────────────────────────────
        private void SaveLevel()
        {
            if (_level == null) return;
            
            try
            {
                FnmmSaver.Save(_level, _levelPath);
                IsModified = false;
                Console.WriteLine($"[ChartEditor] Saved level to {_levelPath}");
            }
            catch (Exception ex)
            {
                _errorMessage = $"Save failed: {ex.Message}";
                Console.WriteLine($"[ChartEditor] Save error: {ex.Message}");
            }
        }
        
        // ── Helper: Check if key just pressed ────────────────────────────────
        private bool JustPressed(KeyboardState kb, Keys key)
        {
            return kb.IsKeyDown(key) && !_prevKeyboard.IsKeyDown(key);
        }
        
        // ── Helper: Get scaled time unit height (beats or seconds) ───────────
        private float GetScaledTimeHeight()
        {
            if (_level == null) return BEAT_HEIGHT * _zoom * _scrollSpeed;
            
            // If times are in seconds (Version 2.0+), use SECOND_HEIGHT
            // If times are in beats (Version < 2.0), use BEAT_HEIGHT
            float baseHeight = _level.Data.TimesAreSeconds ? SECOND_HEIGHT : BEAT_HEIGHT;
            return baseHeight * _zoom * _scrollSpeed;
        }
        
        // ── Check for notes at receptor bar (trigger character animations) ──
        private void CheckNotesAtReceptor()
        {
            if (_level == null) return;
            
            // Current time depends on format
            float currentTime = _level.Data.TimesAreSeconds 
                ? _playbackTime  // Already in seconds
                : (_playbackTime * _level.Data.EffectiveBpm) / 60f; // Convert to beats
            
            const float TOLERANCE = 0.08f; // Window for note detection
            
            // Check each strumline
            for (int strumlineIdx = 0; strumlineIdx < Math.Min(2, _level.Data.Strumlines.Count); strumlineIdx++)
            {
                var strumline = _level.Data.Strumlines[strumlineIdx];
                
                // Check each lane
                for (int lane = 0; lane < Math.Min(LANE_COUNT, strumline.Count); lane++)
                {
                    foreach (var note in strumline[lane])
                    {
                        // Check if note is hitting receptor right now
                        if (MathF.Abs(note.Start - currentTime) < TOLERANCE)
                        {
                            // Trigger sing animation for this character
                            _characterSingTime[strumlineIdx] = SING_ANIM_DURATION;
                            _characterSingLane[strumlineIdx] = lane;
                            break; // Only one note per lane at a time
                        }
                    }
                }
            }
        }
        
        // ── Draw ─────────────────────────────────────────────────────────────
        public void Draw(SpriteBatch sb, int screenWidth, int screenHeight)
        {
            UpdateLayout(screenWidth, screenHeight);
            
            switch (_phase)
            {
                case EditorPhase.Loading:
                    DrawLoading(sb, screenWidth, screenHeight);
                    break;
                case EditorPhase.LoadComplete:
                    DrawLoadComplete(sb, screenWidth, screenHeight);
                    break;
                case EditorPhase.Error:
                    DrawError(sb, screenWidth, screenHeight);
                    break;
                case EditorPhase.Ready:
                    DrawEditor(sb, screenWidth, screenHeight);
                    break;
            }
        }
        
        // ── Update layout rectangles ─────────────────────────────────────────
        private void UpdateLayout(int screenWidth, int screenHeight)
        {
            const int TIMELINE_HEIGHT = 80;
            const int TOOLBAR_WIDTH = 80;
            const int INFO_PANEL_WIDTH = 250;
            const int STATUS_BAR_HEIGHT = 30;
            
            _timelineRect = new Rectangle(0, 0, screenWidth, TIMELINE_HEIGHT);
            _toolbarRect = new Rectangle(0, TIMELINE_HEIGHT, TOOLBAR_WIDTH, screenHeight - TIMELINE_HEIGHT - STATUS_BAR_HEIGHT);
            _infoPanelRect = new Rectangle(screenWidth - INFO_PANEL_WIDTH, TIMELINE_HEIGHT, INFO_PANEL_WIDTH, screenHeight - TIMELINE_HEIGHT - STATUS_BAR_HEIGHT);
            _gridRect = new Rectangle(TOOLBAR_WIDTH, TIMELINE_HEIGHT, screenWidth - TOOLBAR_WIDTH - INFO_PANEL_WIDTH, screenHeight - TIMELINE_HEIGHT - STATUS_BAR_HEIGHT);
            _statusBarRect = new Rectangle(0, screenHeight - STATUS_BAR_HEIGHT, screenWidth, STATUS_BAR_HEIGHT);
            
            // Preview window in bottom-right corner WITHIN grid area (not overlapping info panel)
            _previewRect = new Rectangle(
                _gridRect.Right - PREVIEW_WIDTH - PREVIEW_MARGIN,
                _gridRect.Bottom - PREVIEW_HEIGHT - PREVIEW_MARGIN,
                PREVIEW_WIDTH,
                PREVIEW_HEIGHT
            );
        }
        
        // ── Draw loading screen ──────────────────────────────────────────────
        private void DrawLoading(SpriteBatch sb, int screenWidth, int screenHeight)
        {
            // Dark background
            sb.Draw(_pixel, new Rectangle(0, 0, screenWidth, screenHeight), Color.Black * 0.85f);
            
            // Spinning circle (bottom right)
            int spinnerSize = 60;
            int spinnerX = screenWidth - spinnerSize - 40;
            int spinnerY = screenHeight - spinnerSize - 40;
            
            // Draw spinning circle segments
            for (int i = 0; i < 8; i++)
            {
                float angle = _loadingSpinnerRotation + (i * MathF.PI / 4f);
                float alpha = 1f - (i / 8f) * 0.7f; // Fade trail
                
                float x = spinnerX + spinnerSize / 2 + MathF.Cos(angle) * (spinnerSize / 3);
                float y = spinnerY + spinnerSize / 2 + MathF.Sin(angle) * (spinnerSize / 3);
                
                sb.Draw(_pixel, new Rectangle((int)x - 4, (int)y - 4, 8, 8), 
                    new Color(100, 150, 255) * alpha);
            }
            
            // "Loading..." text
            DrawText(sb, "Loading...", 
                new Vector2(spinnerX + spinnerSize / 2, spinnerY - 20), 
                Color.White, 1f, centered: true);
        }
        
        // ── Draw load complete (waiting for click) ───────────────────────────
        private void DrawLoadComplete(SpriteBatch sb, int screenWidth, int screenHeight)
        {
            // Dark overlay
            sb.Draw(_pixel, new Rectangle(0, 0, screenWidth, screenHeight), Color.Black * 0.85f);
            
            // Spinning circle (bottom right) - still spinning
            int spinnerSize = 60;
            int spinnerX = screenWidth - spinnerSize - 40;
            int spinnerY = screenHeight - spinnerSize - 40;
            
            for (int i = 0; i < 8; i++)
            {
                float angle = _loadingSpinnerRotation + (i * MathF.PI / 4f);
                float alpha = 1f - (i / 8f) * 0.7f;
                
                float x = spinnerX + spinnerSize / 2 + MathF.Cos(angle) * (spinnerSize / 3);
                float y = spinnerY + spinnerSize / 2 + MathF.Sin(angle) * (spinnerSize / 3);
                
                sb.Draw(_pixel, new Rectangle((int)x - 4, (int)y - 4, 8, 8), 
                    new Color(100, 255, 150) * alpha); // Green tint = complete
            }
            
            DrawText(sb, "Complete!", 
                new Vector2(spinnerX + spinnerSize / 2, spinnerY - 20), 
                new Color(100, 255, 150), 1f, centered: true);
            
            // "Click to continue" message (center, pulsing)
            float pulse = 0.7f + 0.3f * MathF.Sin(_loadingFadeAlpha * 3f);
            DrawText(sb, "Click anywhere to continue", 
                new Vector2(screenWidth / 2, screenHeight / 2), 
                Color.White * Math.Min(1f, _loadingFadeAlpha) * pulse, 
                1.5f, centered: true);
        }
        
        // ── Draw error screen ────────────────────────────────────────────────
        private void DrawError(SpriteBatch sb, int screenWidth, int screenHeight)
        {
            sb.Draw(_pixel, new Rectangle(0, 0, screenWidth, screenHeight), BG_DARK);
            DrawText(sb, "ERROR", new Vector2(screenWidth / 2, screenHeight / 2 - 60), Color.Red, 2f, centered: true);
            DrawText(sb, _errorMessage ?? "Unknown error", new Vector2(screenWidth / 2, screenHeight / 2), Color.White, 1f, centered: true);
        }
        
        // ── Draw main editor ─────────────────────────────────────────────────
        private void DrawEditor(SpriteBatch sb, int screenWidth, int screenHeight)
        {
            if (_level == null) return;
            
            // Background
            sb.Draw(_pixel, new Rectangle(0, 0, screenWidth, screenHeight), BG_DARK);
            
            // Draw each section
            DrawTimeline(sb);
            DrawToolbar(sb);
            DrawGrid(sb);
            DrawInfoPanel(sb);
            DrawStatusBar(sb);
            
            // Draw character preview window (on top of everything)
            DrawCharacterPreview(sb);
        }
        
        // ── Draw timeline (audio waveform) ───────────────────────────────────
        private void DrawTimeline(SpriteBatch sb)
        {
            sb.Draw(_pixel, _timelineRect, BG_MEDIUM);
            
            // Border
            sb.Draw(_pixel, new Rectangle(_timelineRect.X, _timelineRect.Bottom - 2, _timelineRect.Width, 2), ACCENT);
            
            DrawText(sb, "Timeline", new Vector2(_timelineRect.X + 10, _timelineRect.Y + 10), Color.White, 1f);
        }
        
        // ── Draw toolbar (tools on left) ─────────────────────────────────────
        private void DrawToolbar(SpriteBatch sb)
        {
            sb.Draw(_pixel, _toolbarRect, BG_MEDIUM);
            
            // Border
            sb.Draw(_pixel, new Rectangle(_toolbarRect.Right - 2, _toolbarRect.Y, 2, _toolbarRect.Height), ACCENT);
            
            // Tool buttons
            string[] tools = { "Select", "Note", "Hold", "Delete" };
            for (int i = 0; i < tools.Length; i++)
            {
                int y = _toolbarRect.Y + 10 + i * 70;
                bool isSelected = (int)_currentTool == i;
                Color bg = isSelected ? ACCENT : BG_LIGHT;
                
                sb.Draw(_pixel, new Rectangle(_toolbarRect.X + 10, y, 60, 60), bg);
                DrawText(sb, (i + 1).ToString(), new Vector2(_toolbarRect.X + 40, y + 30), Color.White, 1.2f, centered: true);
            }
        }
        
        // ── Draw grid (main chart area) ──────────────────────────────────────
        private void DrawGrid(SpriteBatch sb)
        {
            sb.Draw(_pixel, _gridRect, BG_DARK);
            
            if (_level == null) return;
            
            float scaledTimeHeight = GetScaledTimeHeight();
            float totalGridWidth = LANE_WIDTH * LANE_COUNT;
            float gridStartX = _gridRect.X + (_gridRect.Width - totalGridWidth) / 2f;
            
            // Receptor Y (20% from top like FNF)
            float receptorY = _gridRect.Y + _gridRect.Height * RECEPTOR_Y_OFFSET;
            
            // Draw checkerboard background pattern
            DrawGridBackground(sb, gridStartX, scaledTimeHeight, receptorY);
            
            // Draw time lines (beats or seconds depending on format)
            float bpm = _level.Data.EffectiveBpm;
            int beatsPerMeasure = 4;
            bool isTimeBased = _level.Data.TimesAreSeconds;
            
            // Calculate line interval
            float lineInterval = isTimeBased ? 1.0f : 1.0f; // 1 second or 1 beat
            float measureInterval = isTimeBased ? (60f / bpm) * beatsPerMeasure : beatsPerMeasure;
            
            for (int i = (int)_scrollY - 10; i < (int)_scrollY + 40; i++)
            {
                float timeValue = i * lineInterval;
                
                // Calculate Y position FROM receptor (notes scroll down)
                float y = receptorY + (timeValue - _scrollY) * scaledTimeHeight;
                
                if (y < _gridRect.Y || y > _gridRect.Bottom) continue;
                
                bool isMeasure = MathF.Abs(timeValue % measureInterval) < 0.01f;
                bool isHalfMeasure = !isMeasure && MathF.Abs(timeValue % (measureInterval / 2f)) < 0.01f;
                
                Color lineColor = isMeasure ? MEASURE_LINE : (isHalfMeasure ? BEAT_LINE : GRID_LINE);
                int lineThickness = isMeasure ? 3 : (isHalfMeasure ? 2 : 1);
                
                sb.Draw(_pixel, new Rectangle((int)gridStartX, (int)y, (int)totalGridWidth, lineThickness), lineColor);
                
                // Time label on measure lines
                if (isMeasure && timeValue >= 0)
                {
                    string label = isTimeBased ? $"{timeValue:F1}s" : $"{(int)timeValue}";
                    DrawText(sb, label, new Vector2(gridStartX - 40, y - 10), Color.Gray, 0.8f);
                }
            }
            
            // Draw lane dividers
            for (int lane = 0; lane <= LANE_COUNT; lane++)
            {
                float x = gridStartX + lane * LANE_WIDTH;
                Color laneColor = lane < LANE_COUNT ? LANE_COLORS[lane] * 0.4f : GRID_LINE;
                int thickness = (lane == 0 || lane == LANE_COUNT) ? 3 : 2;
                sb.Draw(_pixel, new Rectangle((int)x, _gridRect.Y, thickness, _gridRect.Height), laneColor);
            }
            
            // Highlight selected lane
            if (_selectedLane >= 0 && _selectedLane < LANE_COUNT)
            {
                float x = gridStartX + _selectedLane * LANE_WIDTH;
                sb.Draw(_pixel, new Rectangle((int)x, _gridRect.Y, (int)LANE_WIDTH, _gridRect.Height), LANE_COLORS[_selectedLane] * 0.15f);
            }
            
            // Draw notes (scrolling down from receptor)
            DrawNotes(sb, gridStartX, scaledTimeHeight, receptorY);
            
            // Draw RECEPTOR BAR (encima de todo)
            DrawReceptorBar(sb, gridStartX, receptorY, totalGridWidth);
        }
        
        // ── Draw receptor bar (strumline) ────────────────────────────────────
        private void DrawReceptorBar(SpriteBatch sb, float gridStartX, float receptorY, float totalGridWidth)
        {
            // Receptor bar line (thick white line)
            sb.Draw(_pixel, new Rectangle((int)gridStartX, (int)receptorY - 3, (int)totalGridWidth, 6), Color.White * 0.9f);
            
            // Draw receptors (arrows) on the bar
            for (int lane = 0; lane < LANE_COUNT; lane++)
            {
                float centerX = gridStartX + lane * LANE_WIDTH + LANE_WIDTH / 2f;
                float receptorSize = LANE_WIDTH * 0.7f;
                
                // Check if receptor is being pressed
                bool isPressed = _receptorPressTime[lane] > 0f;
                float pressScale = isPressed ? 0.9f : 1.0f; // Shrink when pressed
                Color receptorColor = LANE_COLORS[lane] * (isPressed ? 1.0f : 0.7f);
                
                // Draw receptor arrow using NoteStyle
                if (_noteStyle != null)
                {
                    var frame = _noteStyle.GetNoteFrame(lane, 0);
                    if (frame != null)
                    {
                        float scale = (receptorSize / frame.Source.Width) * pressScale;
                        sb.Draw(frame.Texture,
                            new Vector2(centerX, receptorY),
                            frame.Source, receptorColor, 0f,
                            new Vector2(frame.Source.Width / 2, frame.Source.Height / 2),
                            scale, SpriteEffects.None, 0f);
                        continue;
                    }
                }
                
                // Fallback: colored square
                int squareSize = (int)(receptorSize * 0.9f * pressScale);
                int squareX = (int)(centerX - squareSize / 2);
                int squareY = (int)(receptorY - squareSize / 2);
                
                sb.Draw(_pixel, new Rectangle(squareX, squareY, squareSize, squareSize), receptorColor * 0.8f);
                sb.Draw(_pixel, new Rectangle(squareX, squareY, squareSize, 2), receptorColor);
                sb.Draw(_pixel, new Rectangle(squareX, squareY + squareSize - 2, squareSize, 2), receptorColor);
                sb.Draw(_pixel, new Rectangle(squareX, squareY, 2, squareSize), receptorColor);
                sb.Draw(_pixel, new Rectangle(squareX + squareSize - 2, squareY, 2, squareSize), receptorColor);
            }
        }
        
        // ── Draw grid background pattern ─────────────────────────────────────
        private void DrawGridBackground(SpriteBatch sb, float gridStartX, float scaledTimeHeight, float receptorY)
        {
            if (_level == null) return;
            
            // Draw subtle checkerboard pattern for better visibility
            float minTime = _scrollY - 10f;
            float maxTime = _scrollY + 40f;
            
            // Quarter divisions
            float quarterInterval = _level.Data.TimesAreSeconds ? 0.25f : 0.25f;
            
            for (float time = MathF.Floor(minTime / quarterInterval) * quarterInterval; time <= maxTime; time += quarterInterval)
            {
                // Y position FROM receptor (scrolling down)
                float y = receptorY + (time - _scrollY) * scaledTimeHeight;
                
                if (y < _gridRect.Y || y > _gridRect.Bottom) continue;
                
                // Alternate pattern every quarter
                int timeIndex = (int)(time / quarterInterval);
                
                for (int lane = 0; lane < LANE_COUNT; lane++)
                {
                    float x = gridStartX + lane * LANE_WIDTH;
                    
                    // Checkerboard pattern
                    bool isDark = (timeIndex + lane) % 2 == 0;
                    Color cellColor = isDark ? BG_DARK : BG_MEDIUM;
                    
                    // Highlight every measure
                    float measureInterval = _level.Data.TimesAreSeconds 
                        ? (60f / _level.Data.EffectiveBpm) * 4f  // 4 beats in seconds
                        : 4f; // 4 beats
                        
                    if (MathF.Abs(time % measureInterval) < 0.01f)
                    {
                        cellColor = isDark ? new Color(25, 25, 30) : new Color(35, 35, 40);
                    }
                    
                    sb.Draw(_pixel, new Rectangle((int)x, (int)y, (int)LANE_WIDTH, (int)(scaledTimeHeight * quarterInterval)), cellColor * 0.3f);
                }
            }
        }
        
        // ── Draw notes on grid (scrolling down from receptor) ────────────────
        private void DrawNotes(SpriteBatch sb, float gridStartX, float scaledTimeHeight, float receptorY)
        {
            if (_level == null || _selectedStrumline >= _level.Data.Strumlines.Count) return;
            
            var strumline = _level.Data.Strumlines[_selectedStrumline];
            
            for (int lane = 0; lane < LANE_COUNT && lane < strumline.Count; lane++)
            {
                foreach (var note in strumline[lane])
                {
                    // Y position FROM receptor (notes scroll down)
                    float timeValue = note.Start;
                    float y = receptorY + (timeValue - _scrollY) * scaledTimeHeight;
                    
                    // Cull off-screen notes
                    if (y < _gridRect.Y - 50 || y > _gridRect.Bottom + 50) continue;
                    
                    // Center X position in lane
                    float centerX = gridStartX + lane * LANE_WIDTH + LANE_WIDTH / 2f;
                    float noteSize = LANE_WIDTH * 0.7f;
                    
                    Color noteColor = LANE_COLORS[lane];
                    
                    // Check for special note properties
                    if (note.Props.IsDamage) noteColor = Color.Purple;
                    if (note.Props.IsInstakill) noteColor = Color.Black;
                    if (note.Props.IsHeal) noteColor = Color.LightGreen;
                    
                    // Draw hold tail first (behind note) - DOWN from receptor
                    if (note.Duration > 0)
                    {
                        float holdHeight = note.Duration * scaledTimeHeight;
                        float holdWidth = LANE_WIDTH * 0.3f;
                        sb.Draw(_pixel, 
                            new Rectangle((int)(centerX - holdWidth / 2), (int)y, (int)holdWidth, (int)holdHeight), 
                            noteColor * 0.6f);
                    }
                    
                    // Draw note arrow using NoteStyle
                    if (_noteStyle != null)
                    {
                        var frame = _noteStyle.GetNoteFrame(lane, (int)note.Props.Skin);
                        if (frame != null)
                        {
                            float scale = noteSize / frame.Source.Width;
                            sb.Draw(frame.Texture,
                                new Vector2(centerX, y),
                                frame.Source, noteColor, 0f,
                                new Vector2(frame.Source.Width / 2, frame.Source.Height / 2),
                                scale, SpriteEffects.None, 0f);
                            continue; // Skip fallback rendering
                        }
                    }
                    
                    // Fallback: colored square with border
                    int squareSize = (int)(noteSize * 0.9f);
                    int squareX = (int)(centerX - squareSize / 2);
                    int squareY = (int)(y - squareSize / 2);
                    
                    sb.Draw(_pixel, new Rectangle(squareX, squareY, squareSize, squareSize), noteColor * 0.8f);
                    // Border
                    sb.Draw(_pixel, new Rectangle(squareX, squareY, squareSize, 2), noteColor);
                    sb.Draw(_pixel, new Rectangle(squareX, squareY + squareSize - 2, squareSize, 2), noteColor);
                    sb.Draw(_pixel, new Rectangle(squareX, squareY, 2, squareSize), noteColor);
                    sb.Draw(_pixel, new Rectangle(squareX + squareSize - 2, squareY, 2, squareSize), noteColor);
                }
            }
        }
        
        // ── Draw info panel (right side) ─────────────────────────────────────
        private void DrawInfoPanel(SpriteBatch sb)
        {
            sb.Draw(_pixel, _infoPanelRect, BG_MEDIUM);
            
            // Border
            sb.Draw(_pixel, new Rectangle(_infoPanelRect.X, _infoPanelRect.Y, 2, _infoPanelRect.Height), ACCENT);
            
            if (_level == null) return;
            
            int y = _infoPanelRect.Y + 10;
            
            // Level info
            DrawText(sb, $"BPM: {_level.Data.EffectiveBpm:F1}", new Vector2(_infoPanelRect.X + 10, y), Color.White, 1f);
            y += 30;
            
            // Strumline info with lane count
            string strumlineName = _selectedStrumline == 0 ? "Player" : "Opponent";
            int laneCount = _selectedStrumline < _level.Data.Strumlines.Count 
                ? _level.Data.Strumlines[_selectedStrumline].Count 
                : 0;
            DrawText(sb, $"Strumline: {strumlineName}", new Vector2(_infoPanelRect.X + 10, y), ACCENT, 1f);
            y += 25;
            DrawText(sb, $"  Lanes: {laneCount}", new Vector2(_infoPanelRect.X + 10, y), Color.Gray, 0.9f);
            y += 30;
            
            DrawText(sb, $"Lane: {(_selectedLane >= 0 ? _selectedLane.ToString() : "None")}", new Vector2(_infoPanelRect.X + 10, y), Color.White, 1f);
            y += 30;
            DrawText(sb, $"Tool: {_currentTool}", new Vector2(_infoPanelRect.X + 10, y), Color.White, 1f);
            y += 30;
            
            // View settings
            DrawText(sb, $"Zoom: {_zoom:F2}x", new Vector2(_infoPanelRect.X + 10, y), new Color(150, 200, 255), 0.9f);
            y += 25;
            DrawText(sb, $"Scroll Speed: {_scrollSpeed:F2}x", new Vector2(_infoPanelRect.X + 10, y), new Color(150, 255, 200), 0.9f);
            y += 35;
            
            // Controls
            DrawText(sb, "Controls:", new Vector2(_infoPanelRect.X + 10, y), ACCENT, 1f);
            y += 25;
            DrawText(sb, "1-4: Tools", new Vector2(_infoPanelRect.X + 10, y), Color.Gray, 0.8f);
            y += 20;
            DrawText(sb, "Q/W: Strumline", new Vector2(_infoPanelRect.X + 10, y), Color.Gray, 0.8f);
            y += 20;
            DrawText(sb, "+/-: Zoom", new Vector2(_infoPanelRect.X + 10, y), Color.Gray, 0.8f);
            y += 20;
            DrawText(sb, "O/P: Scroll Speed", new Vector2(_infoPanelRect.X + 10, y), new Color(150, 255, 200), 0.8f);
            y += 20;
            DrawText(sb, "Arrows: Scroll", new Vector2(_infoPanelRect.X + 10, y), Color.Gray, 0.8f);
            y += 20;
            DrawText(sb, "Wheel: Scroll", new Vector2(_infoPanelRect.X + 10, y), Color.Gray, 0.8f);
            y += 20;
            DrawText(sb, "Space: Play/Pause", new Vector2(_infoPanelRect.X + 10, y), new Color(100, 255, 150), 0.8f);
            y += 20;
            DrawText(sb, "Ctrl+S: Save", new Vector2(_infoPanelRect.X + 10, y), Color.Gray, 0.8f);
        }
        
        // ── Draw status bar (bottom) ─────────────────────────────────────────
        private void DrawStatusBar(SpriteBatch sb)
        {
            sb.Draw(_pixel, _statusBarRect, BG_MEDIUM);
            
            // Border
            sb.Draw(_pixel, new Rectangle(_statusBarRect.X, _statusBarRect.Y, _statusBarRect.Width, 2), ACCENT);
            
            if (_level == null) return;
            
            string playbackStatus = _isPlaying ? "▶ PLAYING" : "⏸ PAUSED";
            string timeFormat = _level.Data.TimesAreSeconds ? "seconds" : "beats";
            string status = $"{playbackStatus} | Pos: {_scrollY:F2} {timeFormat} | Zoom: {_zoom:F2}x | Speed: {_scrollSpeed:F2}x";
            
            if (IsModified)
                status += " | ● Modified";
            
            DrawText(sb, status, new Vector2(_statusBarRect.X + 10, _statusBarRect.Y + 5), Color.White, 0.8f);
            
            // Playback time on right
            if (_isPlaying)
            {
                string time = $"♪ {_playbackTime:F2}s";
                DrawText(sb, time, new Vector2(_statusBarRect.Right - 120, _statusBarRect.Y + 5), new Color(100, 255, 150), 0.8f);
            }
        }
        
        // ── Helper: Draw text ────────────────────────────────────────────────
        private void DrawText(SpriteBatch sb, string text, Vector2 pos, Color color, float scale, bool centered = false)
        {
            if (_fontRenderer != null)
            {
                if (centered)
                {
                    var size = _fontRenderer.MeasureString(text) * scale;
                    pos.X -= size.X / 2;
                }
                _fontRenderer.DrawString(sb, text, pos, color, scale);
            }
            else if (_font != null)
            {
                if (centered)
                {
                    var size = _font.MeasureString(text) * scale;
                    pos.X -= size.X / 2;
                }
                sb.DrawString(_font, text, pos, color, 0f, Vector2.Zero, scale, SpriteEffects.None, 0f);
            }
        }
        
        // ── Draw character preview window ────────────────────────────────────
        private void DrawCharacterPreview(SpriteBatch sb)
        {
            if (_level == null) return;
            
            // Dark background with border
            sb.Draw(_pixel, _previewRect, BG_DARK * 0.95f);
            sb.Draw(_pixel, new Rectangle(_previewRect.X, _previewRect.Y, _previewRect.Width, 3), ACCENT);
            sb.Draw(_pixel, new Rectangle(_previewRect.X, _previewRect.Bottom - 3, _previewRect.Width, 3), ACCENT);
            sb.Draw(_pixel, new Rectangle(_previewRect.X, _previewRect.Y, 3, _previewRect.Height), ACCENT);
            sb.Draw(_pixel, new Rectangle(_previewRect.Right - 3, _previewRect.Y, 3, _previewRect.Height), ACCENT);
            
            // Title
            DrawText(sb, "Level Preview", 
                new Vector2(_previewRect.X + _previewRect.Width / 2, _previewRect.Y + 8), 
                ACCENT, 0.8f, centered: true);
            
            // Preview area (below title)
            Rectangle previewArea = new Rectangle(
                _previewRect.X + 10,
                _previewRect.Y + 25,
                _previewRect.Width - 20,
                _previewRect.Height - 65 // Leave space for character indicators at bottom
            );
            
            // Draw stage/level preview background
            sb.Draw(_pixel, previewArea, new Color(15, 15, 20) * 0.9f);
            
            // Draw a simplified representation of the level
            DrawLevelMiniature(sb, previewArea);
            
            // Draw character singing indicators at bottom
            int charY = _previewRect.Bottom - 35;
            DrawCharacterIndicator(sb, new Vector2(_previewRect.X + 75, charY), 0, "Player");
            DrawCharacterIndicator(sb, new Vector2(_previewRect.Right - 75, charY), 1, "Opponent");
            
            // Stats
            int totalNotes = 0;
            foreach (var strumline in _level.Data.Strumlines)
                foreach (var lane in strumline)
                    totalNotes += lane.Count;
            
            DrawText(sb, $"{totalNotes} notes | BPM: {_level.Data.EffectiveBpm:F0}", 
                new Vector2(_previewRect.X + _previewRect.Width / 2, _previewRect.Bottom - 10), 
                Color.Gray, 0.65f, centered: true);
        }
        
        // ── Draw level miniature (stage, characters, objects) ─────────────────
        private void DrawLevelMiniature(SpriteBatch sb, Rectangle area)
        {
            if (_level == null) return;
            
            // Calculate world bounds from scene objects
            float minX = float.MaxValue, maxX = float.MinValue;
            float minY = float.MaxValue, maxY = float.MinValue;
            
            foreach (var obj in _level.Scene.Objects)
            {
                var pos = obj.GetPosition();
                minX = Math.Min(minX, pos.X);
                maxX = Math.Max(maxX, pos.X);
                minY = Math.Min(minY, pos.Y);
                maxY = Math.Max(maxY, pos.Y);
            }
            
            // If no objects, use default bounds
            if (minX == float.MaxValue)
            {
                minX = 0; maxX = 1920;
                minY = 0; maxY = 1080;
            }
            
            // Add padding
            float worldWidth = maxX - minX;
            float worldHeight = maxY - minY;
            if (worldWidth < 100) worldWidth = 1920;
            if (worldHeight < 100) worldHeight = 1080;
            
            minX -= worldWidth * 0.1f;
            maxX += worldWidth * 0.1f;
            minY -= worldHeight * 0.1f;
            maxY += worldHeight * 0.1f;
            worldWidth = maxX - minX;
            worldHeight = maxY - minY;
            
            // Calculate scale to fit in preview area
            float scaleX = area.Width / worldWidth;
            float scaleY = area.Height / worldHeight;
            float scale = Math.Min(scaleX, scaleY);
            
            // Draw simplified scene objects
            foreach (var obj in _level.Scene.Objects)
            {
                var pos = obj.GetPosition();
                var objScale = obj.GetScale();
                
                // Map world position to preview area
                float screenX = area.X + (pos.X - minX) * scale;
                float screenY = area.Y + (pos.Y - minY) * scale;
                
                // Determine object color based on sprite name
                Color objColor = Color.Gray;
                string spriteName = obj.Spr?.ToLower() ?? "";
                
                if (spriteName.Contains("bf") || spriteName.Contains("player") || spriteName.Contains("boyfriend"))
                {
                    objColor = new Color(100, 150, 255); // Blue for player
                    
                    // Highlight if singing
                    if (_characterSingTime[0] > 0f)
                        objColor = LANE_COLORS[Math.Clamp(_characterSingLane[0], 0, 3)];
                }
                else if (spriteName.Contains("dad") || spriteName.Contains("opponent"))
                {
                    objColor = new Color(255, 100, 100); // Red for opponent
                    
                    // Highlight if singing
                    if (_characterSingTime[1] > 0f)
                        objColor = LANE_COLORS[Math.Clamp(_characterSingLane[1], 0, 3)];
                }
                else if (spriteName.Contains("stage") || spriteName.Contains("bg") || spriteName.Contains("background"))
                {
                    objColor = new Color(60, 60, 80); // Dark blue-gray for stage
                }
                else
                {
                    objColor = new Color(80, 80, 90); // Gray for other objects
                }
                
                // Draw object as rectangle
                int objWidth = Math.Max(3, (int)(100 * objScale.X * scale));
                int objHeight = Math.Max(3, (int)(100 * objScale.Y * scale));
                
                sb.Draw(_pixel, new Rectangle(
                    (int)(screenX - objWidth / 2),
                    (int)(screenY - objHeight / 2),
                    objWidth,
                    objHeight
                ), objColor * 0.8f);
                
                // Draw outline
                sb.Draw(_pixel, new Rectangle((int)(screenX - objWidth / 2), (int)(screenY - objHeight / 2), objWidth, 1), objColor);
                sb.Draw(_pixel, new Rectangle((int)(screenX - objWidth / 2), (int)(screenY + objHeight / 2), objWidth, 1), objColor);
                sb.Draw(_pixel, new Rectangle((int)(screenX - objWidth / 2), (int)(screenY - objHeight / 2), 1, objHeight), objColor);
                sb.Draw(_pixel, new Rectangle((int)(screenX + objWidth / 2), (int)(screenY - objHeight / 2), 1, objHeight), objColor);
            }
            
            // If no scene objects, show placeholder
            if (_level.Scene.Objects.Count == 0)
            {
                DrawText(sb, "No scene objects", 
                    new Vector2(area.X + area.Width / 2, area.Y + area.Height / 2 - 10), 
                    Color.Gray * 0.6f, 0.8f, centered: true);
                DrawText(sb, "Level is chart-only", 
                    new Vector2(area.X + area.Width / 2, area.Y + area.Height / 2 + 10), 
                    Color.Gray * 0.5f, 0.7f, centered: true);
            }
        }
        
        // ── Draw character singing indicator (compact version) ────────────────
        private void DrawCharacterIndicator(SpriteBatch sb, Vector2 center, int strumlineIdx, string label)
        {
            bool isSinging = _characterSingTime[strumlineIdx] > 0f;
            int singLane = _characterSingLane[strumlineIdx];
            
            // Label
            DrawText(sb, label, 
                new Vector2(center.X, center.Y - 15), 
                Color.Gray, 0.6f, centered: true);
            
            // Draw 4 mini arrows horizontally
            int arrowSpacing = 15;
            int startX = (int)(center.X - (LANE_COUNT * arrowSpacing) / 2 + arrowSpacing / 2);
            
            for (int lane = 0; lane < LANE_COUNT; lane++)
            {
                int arrowX = startX + lane * arrowSpacing;
                bool isActive = isSinging && singLane == lane;
                float alpha = isActive ? 1.0f : 0.25f;
                Color arrowColor = LANE_COLORS[lane] * alpha;
                
                // Draw mini arrow using NoteStyle if available
                if (_noteStyle != null)
                {
                    var frame = _noteStyle.GetNoteFrame(lane, 0);
                    if (frame != null)
                    {
                        float scale = 0.25f * (isActive ? 1.2f : 1.0f);
                        sb.Draw(frame.Texture,
                            new Vector2(arrowX, center.Y),
                            frame.Source, arrowColor, 0f,
                            new Vector2(frame.Source.Width / 2, frame.Source.Height / 2),
                            scale, SpriteEffects.None, 0f);
                        continue;
                    }
                }
                
                // Fallback: colored square
                int squareSize = isActive ? 8 : 5;
                sb.Draw(_pixel, new Rectangle(arrowX - squareSize / 2, (int)center.Y - squareSize / 2, squareSize, squareSize), arrowColor);
            }
        }
        
        // ── Helper: Get lane name ────────────────────────────────────────────
        private string GetLaneName(int lane)
        {
            return lane switch
            {
                0 => "LEFT",
                1 => "DOWN",
                2 => "UP",
                3 => "RIGHT",
                _ => "?"
            };
        }
        
        // ── Enums ────────────────────────────────────────────────────────────
        private enum EditorPhase { Loading, LoadComplete, Ready, Error }
        
        private enum EditorTool
        {
            Select,    // Select and move notes
            PlaceNote, // Place single notes
            PlaceHold, // Place hold notes
            Delete     // Delete notes
        }
    }
}
