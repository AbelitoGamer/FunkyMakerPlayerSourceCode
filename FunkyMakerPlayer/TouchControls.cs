using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using Microsoft.Xna.Framework.Input;
using Microsoft.Xna.Framework.Input.Touch;
using MonoGame.Extended;
using System;
using System.Collections.Generic;
using System.Linq;

namespace FunkyMakerPlayer;

#if ANDROID
/// <summary>
/// Virtual on-screen touch controls for Android 
/// Mode 1: Menu navigation (D-pad + action buttons)
/// Mode 2: Gameplay (4 vertical hitboxes like Funky Maker Mobile)
/// </summary>
public class TouchControls
{
    public enum ControlMode
    {
        Menu,      // D-pad + OK/Back buttons for menus
        Gameplay   // 4 vertical hitboxes for gameplay
    }
    
    private ControlMode _mode = ControlMode.Menu;
    private bool _hidePauseButton = false;  // Hide pause button in multiplayer
    private readonly Hitbox[] _hitboxes = new Hitbox[4];
    private readonly List<VirtualButton> _menuButtons = new();
    private VirtualButton? _pauseButton;  // Separate pause button for gameplay only
    private VirtualButton? _qButton;  // Q button for settings tab navigation (prev tab)
    private VirtualButton? _eButton;  // E button for settings tab navigation (next tab)
    private readonly Dictionary<Keys, bool> _keyStates = new();
    private readonly Dictionary<Keys, bool> _keyStatesPrev = new();  // Previous frame state for detecting justPressed
    private readonly List<Keys> _keyBuffer = new();
    private readonly bool[] _laneDown = new bool[4];
    private readonly bool[] _laneDownPrev = new bool[4];
    private readonly bool[] _laneJustPressed = new bool[4];
    private readonly Dictionary<int, int> _fingerLane = new();      // Active gameplay touch.Id -> locked lane (0..3)
    private readonly HashSet<int> _pauseFingerIds = new();          // Active touches that began on the pause button
    private readonly HashSet<int> _skipFingerIds = new();           // Active touches that began on the Skip Breaks prompt

    /// <summary>
    /// (fork) Skip Breaks prompt in screen (touch) coordinates while it is shown, else null. Touches that
    /// start on it press Space (the skip) and never reach a lane.
    /// </summary>
    public Rectangle? SkipButtonBounds { get; set; }
    private readonly HashSet<int> _seenTouchIds = new();
    private readonly List<int> _staleTouchIds = new();
    private Texture2D? _hitboxTexture;
    private bool _showSettingsButtons = false;  // Flag to show/hide Q and E buttons
    private bool _allControlsVisible = true;  // Flag to show/hide ALL touch controls

    /// <summary>
    /// Optional callback returning true when a hold note is active on the given lane (0..3).
    /// While a hold is active, the finger that pressed it stays locked to that lane until lifted
    /// so slight thumb drift across invisible lane borders never cancels a hold note.
    /// </summary>
    public Func<int, bool>? IsLaneHolding { get; set; }
    
    // Hitbox colors (gameplay mode)
    private static readonly Color[] HITBOX_COLORS = new[]
    {
        new Color(255, 0, 255),   // Magenta - Left
        new Color(0, 255, 255),   // Cyan - Down
        new Color(0, 255, 0),     // Green - Up
        new Color(255, 0, 0)      // Red - Right
    };
    
    private static readonly Keys[] HITBOX_KEYS = new[]
    {
        Keys.D,    // Left lane
        Keys.F,    // Down lane
        Keys.J,    // Up lane
        Keys.K     // Right lane
    };
    
    // Visual constants - More reasonable sizes for production
    private const float HITBOX_IDLE_ALPHA = 0.0f;   // Invisible cuando no se presiona (era 0.5)
    private const float MENU_BUTTON_SIZE = 0.12f;  // 12% of screen height (más razonable)
    private const float MENU_MARGIN = 0.03f;       // Margen del borde de pantalla
    private static readonly Color MENU_COLOR_IDLE = new Color(30, 30, 40, 140);        // Gris más oscuro y menos opaco
    private static readonly Color MENU_COLOR_PRESSED = new Color(60, 100, 200, 180);   // Azul más suave cuando se presiona
    private static readonly Color MENU_COLOR_BORDER = new Color(80, 80, 100, 180);     // Bordes más sutiles
    
    private int _screenWidth;
    private int _screenHeight;
    
    public TouchControls()
    {
        TouchPanel.EnabledGestures = GestureType.None;
        Console.WriteLine("[TouchControls] Constructor called");
    }
    
    /// <summary>
    /// Initialize touch controls based on screen resolution
    /// </summary>
    public void Initialize(GraphicsDevice graphicsDevice, int screenWidth, int screenHeight)
    {
        _screenWidth = screenWidth;
        _screenHeight = screenHeight;
        _keyStates.Clear();
        
        // Create 1x1 white pixel texture
        _hitboxTexture = new Texture2D(graphicsDevice, 1, 1);
        _hitboxTexture.SetData(new[] { Color.White });
        
        InitializeGameplayHitboxes();
        InitializeMenuButtons();
        InitializePauseButton();  // Separate pause button
        
        Console.WriteLine($"[TouchControls] Initialized for {screenWidth}x{screenHeight}, mode: {_mode}");
        Console.WriteLine($"[TouchControls] Gameplay hitboxes:");
        for (int i = 0; i < _hitboxes.Length; i++)
        {
            Console.WriteLine($"[TouchControls]   Hitbox {i} ({_hitboxes[i].Key}): {_hitboxes[i].Bounds}");
        }
        Console.WriteLine($"[TouchControls] Menu buttons count: {_menuButtons.Count}");
    }
    
    /// <summary>
    /// Resync hitbox / button bounds if Android immersive mode, cutout, or rotation changes screen size.
    /// </summary>
    public void SyncBounds(int screenWidth, int screenHeight)
    {
        if (screenWidth <= 0 || screenHeight <= 0) return;
        if (screenWidth == _screenWidth && screenHeight == _screenHeight) return;

        _screenWidth = screenWidth;
        _screenHeight = screenHeight;
        InitializeGameplayHitboxes();
        InitializeMenuButtons();
        InitializePauseButton();
        Console.WriteLine($"[TouchControls] Resynced bounds to {screenWidth}x{screenHeight}");
    }

    /// <summary>
    /// Initialize 4 edge-to-edge vertical hitboxes for gameplay (no 1px rounding gaps).
    /// </summary>
    private void InitializeGameplayHitboxes()
    {
        for (int i = 0; i < 4; i++)
        {
            int left = i * _screenWidth / 4;
            int right = (i + 1) * _screenWidth / 4;
            Keys laneKey = i < NoteRenderer.PlayerKeys.Length ? NoteRenderer.PlayerKeys[i] : HITBOX_KEYS[i];
            _hitboxes[i] = new Hitbox
            {
                Key = laneKey,
                Color = HITBOX_COLORS[i],
                Bounds = new Rectangle(
                    left,
                    0,
                    Math.Max(1, right - left),
                    _screenHeight
                ),
                IsPressed = false
            };
        }
    }

    /// <summary>Maps any screen X coordinate to lane 0..3 with edge clamping (no dead margins).</summary>
    private int LaneFromX(float x)
    {
        int w = Math.Max(1, _screenWidth);
        int lane = (int)(x * 4f / w);
        return Math.Clamp(lane, 0, 3);
    }
    
    /// <summary>
    /// Initialize menu navigation buttons (D-pad + action buttons)
    /// </summary>
    private void InitializeMenuButtons()
    {
        _menuButtons.Clear();
        
        int buttonSize = (int)(_screenHeight * MENU_BUTTON_SIZE);
        int margin = (int)(_screenHeight * MENU_MARGIN);
        int spacing = (int)(_screenHeight * 0.012f);
        // Room for the gesture bar / rounded corners at the bottom (the app runs immersive).
        int bottomSafe = (int)(_screenHeight * 0.03f);

        // ── D-Pad (left side): a real cross, fully on screen ─────────────
        // (fork, 2026-11-23: the old layout put Down below the screen edge and Left/Right
        //  off-centre under Up.)
        int padLeft = margin;
        int padBottom = _screenHeight - margin - bottomSafe;
        int cx = padLeft + buttonSize + spacing;              // left column of the middle button
        int midY = padBottom - 2 * buttonSize - 2 * spacing;  // top of the middle row

        _menuButtons.Add(new VirtualButton
        {
            Key = Keys.Left,
            Bounds = new Rectangle(padLeft, midY, buttonSize, buttonSize),
            Label = "<"
        });
        _menuButtons.Add(new VirtualButton
        {
            Key = Keys.Right,
            Bounds = new Rectangle(cx + buttonSize + spacing, midY, buttonSize, buttonSize),
            Label = ">"
        });
        _menuButtons.Add(new VirtualButton
        {
            Key = Keys.Up,
            Bounds = new Rectangle(cx, midY - buttonSize - spacing, buttonSize, buttonSize),
            Label = "^"
        });
        _menuButtons.Add(new VirtualButton
        {
            Key = Keys.Down,
            Bounds = new Rectangle(cx, midY + buttonSize + spacing, buttonSize, buttonSize),
            Label = "v"
        });

        // ── Action Buttons (right side) ──────────────────────────────────
        int aSize = (int)(buttonSize * 1.1f);
        int bSize = (int)(buttonSize * 0.85f);
        int actionX = _screenWidth - margin - aSize;
        int actionY = padBottom - aSize - buttonSize / 2;

        // A = Enter (confirm)
        _menuButtons.Add(new VirtualButton
        {
            Key = Keys.Enter,
            Bounds = new Rectangle(actionX, actionY, aSize, aSize),
            Label = "A"
        });
        // B = Escape (back), lower-left of A like a gamepad
        _menuButtons.Add(new VirtualButton
        {
            Key = Keys.Escape,
            Bounds = new Rectangle(actionX - bSize - spacing * 2, actionY + aSize - bSize / 2, bSize, bSize),
            Label = "B"
        });

        // ── Settings tab buttons (Q / E) ─────────────────────────────────
        // Above B and A, so they no longer cover the Settings title and tab bar (fork).
        int tabButtonSize = (int)(buttonSize * 0.75f);
        int tabY = actionY - tabButtonSize - spacing * 3;
        _eButton = new VirtualButton
        {
            Key = Keys.E,
            Bounds = new Rectangle(actionX + (aSize - tabButtonSize) / 2, tabY, tabButtonSize, tabButtonSize),
            Label = "E"
        };
        _qButton = new VirtualButton
        {
            Key = Keys.Q,
            Bounds = new Rectangle(_eButton.Bounds.X - tabButtonSize - spacing * 2, tabY, tabButtonSize, tabButtonSize),
            Label = "Q"
        };
    }
    
    /// <summary>
    /// Initialize pause button (gameplay only, top-right corner)
    /// </summary>
    private void InitializePauseButton()
    {
        int buttonSize = (int)(_screenHeight * MENU_BUTTON_SIZE);
        // Hacer el botón más grande (1.0x en lugar de 0.7x) y más fácil de presionar
        int pauseButtonSize = (int)(buttonSize * 1.0f);
        int pauseMargin = (int)(_screenHeight * 0.05f);  // 5% from top
        
        // Mover el botón más hacia la derecha: solo un pequeño margen desde el borde
        // Reducido de hitboxWidth completo a solo la mitad para moverlo más a la derecha
        int xOffset = pauseMargin * 2;  // Solo un pequeño offset desde el borde derecho
        
        _pauseButton = new VirtualButton
        {
            Key = Keys.P,  // P for pause
            Bounds = new Rectangle(_screenWidth - xOffset - pauseButtonSize, pauseMargin, pauseButtonSize, pauseButtonSize),
            Label = "||"  // Pause symbol
        };
        
        Console.WriteLine($"[TouchControls] Pause button: X={_screenWidth - xOffset - pauseButtonSize}, Y={pauseMargin}, Size={pauseButtonSize}x{pauseButtonSize}");
    }
    
    /// <summary>
    /// Switch between menu and gameplay mode
    /// </summary>
    public void SetMode(ControlMode mode)
    {
        if (_mode == mode) return;
        _mode = mode;
        _fingerLane.Clear();
        _pauseFingerIds.Clear();
        _skipFingerIds.Clear();
        Array.Clear(_laneDown, 0, _laneDown.Length);
        Array.Clear(_laneDownPrev, 0, _laneDownPrev.Length);
        Array.Clear(_laneJustPressed, 0, _laneJustPressed.Length);
        for (int i = 0; i < _hitboxes.Length; i++)
            if (_hitboxes[i] != null) _hitboxes[i].IsPressed = false;
        Console.WriteLine($"[TouchControls] Switched to {mode} mode");
    }
    
    /// <summary>
    /// Get current mode
    /// </summary>
    public ControlMode GetMode() => _mode;
    
    /// <summary>
    /// Show or hide Q and E buttons (for settings tab navigation)
    /// </summary>
    public void SetSettingsButtonsVisible(bool visible)
    {
        _showSettingsButtons = visible;
    }
    
    /// <summary>
    /// Hide pause button (for multiplayer mode)
    /// </summary>
    public void SetHidePauseButton(bool hide)
    {
        if (_hidePauseButton == hide) return; // called every frame: log only changes
        _hidePauseButton = hide;
        Console.WriteLine($"[TouchControls] Pause button {(hide ? "hidden" : "visible")}");
    }
    
    /// <summary>
    /// Show or hide ALL touch controls (for screens that don't need them like ModsHelpScreen)
    /// </summary>
    public void SetAllControlsVisible(bool visible)
    {
        _allControlsVisible = visible;
    }
    
    /// <summary>When the on-screen menu buttons (D-pad, A / B, Q / E) are shown (Settings → CONTROLS).</summary>
    public enum MenuButtonsMode { Auto, On, Off }

    /// <summary>Saved setting (<see cref="Game1.MenuButtonsMode"/>), shared by every instance (Game1's and the online menu's).</summary>
    public static MenuButtonsMode ButtonsMode => (MenuButtonsMode)Math.Clamp(Game1.MenuButtonsMode, 0, 2);

    /// <summary>
    /// Set by the owner every frame: the current screen can be used fully by tapping, so in
    /// <see cref="MenuButtonsMode.Auto"/> the D-pad / A / B are hidden and don't cover it
    /// (Android's back gesture still goes back).
    /// </summary>
    public bool ScreenIsTouchReady { get; set; }

    /// <summary>The menu buttons are drawn and react to touches.</summary>
    public bool MenuButtonsShown => ButtonsMode == MenuButtonsMode.On ||
                                    (ButtonsMode == MenuButtonsMode.Auto && !ScreenIsTouchReady);

    /// <summary>The touches read by the last <see cref="Update"/> (read them here, not from TouchPanel again).</summary>
    public TouchCollection LastTouches { get; private set; }

    /// <summary>
    /// Menu mode: touches on points where this returns true are not mapped to the D-pad / A / B
    /// (the screen has its own button there, which wins).
    /// </summary>
    public Func<Point, bool>? IgnorePoint { get; set; }

    /// <summary>Some finger is on the screen (read in the last <see cref="Update"/>).</summary>
    public bool AnyTouchDown { get; private set; }
    private bool _anyTouchDownPrev;

    /// <summary>The last finger was lifted in the last <see cref="Update"/> (a tap anywhere ended).</summary>
    public bool TapReleased => _anyTouchDownPrev && !AnyTouchDown;

    /// <summary>
    /// Update touch input and map to virtual keys
    /// </summary>
    public void Update()
    {
        if (_hitboxTexture == null) return;
        _anyTouchDownPrev = AnyTouchDown;

        // Don't process input if controls are hidden
        if (!_allControlsVisible)
        {
            AnyTouchDown = false;
            LastTouches = TouchPanel.GetState();
            // Reset all states when hidden
            _keyStates.Clear();
            _keyStatesPrev.Clear();
            _fingerLane.Clear();
            _pauseFingerIds.Clear();
            _skipFingerIds.Clear();
            Array.Clear(_laneDown, 0, _laneDown.Length);
            Array.Clear(_laneDownPrev, 0, _laneDownPrev.Length);
            Array.Clear(_laneJustPressed, 0, _laneJustPressed.Length);
            if (_mode == ControlMode.Gameplay)
            {
                foreach (var hitbox in _hitboxes)
                    if (hitbox != null) hitbox.IsPressed = false;
            }
            return;
        }

        // Ensure TouchPanel is properly configured (fixes Android losing touch after scene changes)
        TouchPanel.EnabledGestures = GestureType.None;

        // Save previous frame state BEFORE resetting
        _keyStatesPrev.Clear();
        foreach (var kvp in _keyStates)
            _keyStatesPrev[kvp.Key] = kvp.Value;

        for (int i = 0; i < 4; i++)
        {
            _laneDownPrev[i] = _laneDown[i];
            _laneDown[i] = false;
            _laneJustPressed[i] = false;
        }

        // Reset current frame states
        if (_mode == ControlMode.Gameplay)
        {
            for (int i = 0; i < _hitboxes.Length; i++)
                if (_hitboxes[i] != null) _hitboxes[i].IsPressed = false;
        }

        _keyBuffer.Clear();
        foreach (var key in _keyStates.Keys)
            _keyBuffer.Add(key);
        foreach (var key in _keyBuffer)
            _keyStates[key] = false;

        // Process all active touch points.
        // (No per-touch logging here: this runs every frame on the Android UI thread, and a line per
        // finger per frame to logcat only slows the game down.)
        var touches = TouchPanel.GetState();
        LastTouches = touches;
        bool anyDown = false;
        _seenTouchIds.Clear();

        foreach (var touch in touches)
        {
            // Only process Pressed and Moved states
            // Released state means finger lifted - key will stay false (already reset above)
            if (touch.State == TouchLocationState.Pressed ||
                touch.State == TouchLocationState.Moved)
            {
                anyDown = true;
                _seenTouchIds.Add(touch.Id);
                var touchPos = touch.Position;

                if (_mode == ControlMode.Gameplay)
                {
                    bool isTrackedPause = _pauseFingerIds.Contains(touch.Id);
                    bool isTrackedLane = _fingerLane.TryGetValue(touch.Id, out int lane);

                    // Skip Breaks prompt: claims only touches that started on it
                    if (_skipFingerIds.Contains(touch.Id) || (!isTrackedLane && !isTrackedPause &&
                        touch.State == TouchLocationState.Pressed && SkipButtonBounds is Rectangle skipRect &&
                        skipRect.Contains((int)touchPos.X, (int)touchPos.Y)))
                    {
                        _skipFingerIds.Add(touch.Id);
                        _keyStates[Keys.Space] = true;
                        continue;
                    }

                    // Pause button only claims touches that started on it (never steals an active lane hold)
                    if (isTrackedPause || (!isTrackedLane && !_hidePauseButton && _pauseButton != null &&
                        _pauseButton.Bounds.Contains((int)touchPos.X, (int)touchPos.Y)))
                    {
                        _pauseFingerIds.Add(touch.Id);
                        _keyStates[Keys.P] = true;
                        continue;
                    }

                    if (!isTrackedLane || touch.State == TouchLocationState.Pressed)
                    {
                        lane = LaneFromX(touchPos.X);
                        _fingerLane[touch.Id] = lane;
                        _laneJustPressed[lane] = true;
                    }
                    else
                    {
                        // Keep the finger locked to its starting lane while a hold note is active
                        // on that lane (or while within a generous 35% lane-width hysteresis margin)
                        // so slight thumb roll across invisible borders never breaks a hold note.
                        bool holdActive = IsLaneHolding?.Invoke(lane) ?? false;
                        if (!holdActive)
                        {
                            float laneW = Math.Max(1f, _screenWidth / 4f);
                            float laneLeft = lane * laneW;
                            float laneRight = (lane + 1) * laneW;
                            float hysteresis = laneW * 0.35f;
                            if (touchPos.X < laneLeft - hysteresis || touchPos.X > laneRight + hysteresis)
                            {
                                int newLane = LaneFromX(touchPos.X);
                                if (newLane != lane)
                                {
                                    lane = newLane;
                                    _fingerLane[touch.Id] = lane;
                                    _laneJustPressed[lane] = true;
                                }
                            }
                        }
                    }

                    _laneDown[lane] = true;
                    if (_hitboxes[lane] != null)
                        _hitboxes[lane].IsPressed = true;
                }
                else if (MenuButtonsShown) // Menu mode
                {
                    if (IgnorePoint != null && IgnorePoint(new Point((int)touchPos.X, (int)touchPos.Y)))
                        continue;

                    // Check menu buttons
                    foreach (var button in _menuButtons)
                    {
                        if (button.Bounds.Contains((int)touchPos.X, (int)touchPos.Y))
                        {
                            _keyStates[button.Key] = true;
                        }
                    }

                    // Check Q and E buttons (only if settings buttons are visible)
                    if (_showSettingsButtons)
                    {
                        if (_qButton != null && _qButton.Bounds.Contains((int)touchPos.X, (int)touchPos.Y))
                        {
                            _keyStates[Keys.Q] = true;
                        }

                        if (_eButton != null && _eButton.Bounds.Contains((int)touchPos.X, (int)touchPos.Y))
                        {
                            _keyStates[Keys.E] = true;
                        }
                    }
                }
            }
            else if (touch.State == TouchLocationState.Released)
            {
                _fingerLane.Remove(touch.Id);
                _pauseFingerIds.Remove(touch.Id);
                _skipFingerIds.Remove(touch.Id);
            }
        }

        // Prune any touch IDs that vanished without an explicit Released frame
        if (_fingerLane.Count > 0)
        {
            _staleTouchIds.Clear();
            foreach (var id in _fingerLane.Keys)
                if (!_seenTouchIds.Contains(id)) _staleTouchIds.Add(id);
            foreach (var id in _staleTouchIds)
                _fingerLane.Remove(id);
        }
        if (_pauseFingerIds.Count > 0)
        {
            _staleTouchIds.Clear();
            foreach (var id in _pauseFingerIds)
                if (!_seenTouchIds.Contains(id)) _staleTouchIds.Add(id);
            foreach (var id in _staleTouchIds)
                _pauseFingerIds.Remove(id);
        }
        if (_skipFingerIds.Count > 0)
        {
            _staleTouchIds.Clear();
            foreach (var id in _skipFingerIds)
                if (!_seenTouchIds.Contains(id)) _staleTouchIds.Add(id);
            foreach (var id in _staleTouchIds)
                _skipFingerIds.Remove(id);
        }

        // Mirror active gameplay lanes into _keyStates using the live PlayerKeys mapping
        if (_mode == ControlMode.Gameplay)
        {
            for (int i = 0; i < 4; i++)
            {
                Keys laneKey = i < NoteRenderer.PlayerKeys.Length ? NoteRenderer.PlayerKeys[i] : HITBOX_KEYS[i];
                if (_hitboxes[i] != null) _hitboxes[i].Key = laneKey;
                if (_laneDown[i])
                    _keyStates[laneKey] = true;
            }
        }

        AnyTouchDown = anyDown;
    }

    /// <summary>Check if a gameplay lane (0..3) is currently held down via touch.</summary>
    public bool IsLaneDown(int lane) => lane >= 0 && lane < 4 && _laneDown[lane];

    /// <summary>Check if a gameplay lane (0..3) was held down on the previous frame via touch.</summary>
    public bool WasLaneDown(int lane) => lane >= 0 && lane < 4 && _laneDownPrev[lane];

    /// <summary>Check if a finger newly tapped/entered gameplay lane (0..3) this frame.</summary>
    public bool IsLaneJustPressed(int lane) => lane >= 0 && lane < 4 && _laneJustPressed[lane];

    /// <summary>Check if a virtual key is currently pressed</summary>
    public bool IsKeyDown(Keys key)
    {
        return _keyStates.TryGetValue(key, out bool pressed) && pressed;
    }

    /// <summary>Check if a virtual key was pressed on the previous frame</summary>
    public bool WasKeyDown(Keys key)
    {
        return _keyStatesPrev.TryGetValue(key, out bool pressed) && pressed;
    }
    
    /// <summary>
    /// Check if a virtual key was just pressed THIS frame (tap detection)
    /// </summary>
    public bool IsKeyJustPressed(Keys key)
    {
        bool currentlyPressed = _keyStates.TryGetValue(key, out bool cur) && cur;
        bool previouslyPressed = _keyStatesPrev.TryGetValue(key, out bool prev) && prev;
        return currentlyPressed && !previouslyPressed;
    }

    /// <summary>
    /// Returns true if a screen point falls inside any currently visible menu/settings virtual control.
    /// </summary>
    public bool IsPointOnMenuControl(Point point)
    {
        if (!_allControlsVisible || _mode != ControlMode.Menu || !MenuButtonsShown) return false;

        foreach (var button in _menuButtons)
        {
            if (button.Bounds.Contains(point))
                return true;
        }

        if (_showSettingsButtons)
        {
            if (_qButton != null && _qButton.Bounds.Contains(point)) return true;
            if (_eButton != null && _eButton.Bounds.Contains(point)) return true;
        }

        return false;
    }
    
    /// <summary>
    /// Draw touch controls on screen
    /// </summary>
    public void Draw(SpriteBatch spriteBatch, SpriteFont? font)
    {
        // Don't draw any controls if they're hidden (e.g., ModsHelpScreen is showing)
        if (!_allControlsVisible)
        {
            return;
        }
        
        if (_hitboxTexture == null)
        {
            Console.WriteLine("[TouchControls] ERROR: hitboxTexture is null!");
            return;
        }
        
        if (_mode == ControlMode.Gameplay)
        {
            DrawGameplayHitboxes(spriteBatch);
            DrawPauseButton(spriteBatch, font);  // Always draw pause button in gameplay
        }
        else if (MenuButtonsShown)
        {
            DrawMenuButtons(spriteBatch, font);
            
            // Draw Q and E buttons if they should be visible
            if (_showSettingsButtons)
            {
                DrawSettingsTabButtons(spriteBatch, font);
            }
        }
    }
    
    /// <summary>
    /// Draw gameplay hitboxes (semi-transparent overlays)
    /// </summary>
    private void DrawGameplayHitboxes(SpriteBatch spriteBatch)
    {
        foreach (var hitbox in _hitboxes)
        {
            // If HideTouchHitboxColors is enabled, make hitboxes completely invisible
            // Pressed opacity comes from Settings → Touch Hitbox Opacity (fork; was fixed at 40 %).
            float alpha = Game1.HideTouchHitboxColors ? 0f : (hitbox.IsPressed ? Game1.TouchHitboxOpacity : HITBOX_IDLE_ALPHA);
            Color color = hitbox.Color * alpha;
            
            spriteBatch.Draw(_hitboxTexture, hitbox.Bounds, color);
        }
    }
    
    /// <summary>
    /// Draw pause button with improved circular design (top-right corner)
    /// </summary>
    private void DrawPauseButton(SpriteBatch spriteBatch, SpriteFont? font)
    {
        if (_pauseButton == null || _hidePauseButton) return;  // Don't draw if hidden
        
        var pauseButton = _pauseButton;
        bool isPressed = _keyStates.TryGetValue(Keys.P, out bool pressed) && pressed;
        
        Vector2 center = new Vector2(
            pauseButton.Bounds.X + pauseButton.Bounds.Width / 2f,
            pauseButton.Bounds.Y + pauseButton.Bounds.Height / 2f
        );
        float radius = pauseButton.Bounds.Width / 2f;
        
        // Outer glow when pressed
        if (isPressed)
        {
            spriteBatch.DrawCircle(center, radius + 5, 32, new Color(255, 200, 50, 120), 10f);
        }
        
        // Main circle background
        Color bgColor = isPressed ? new Color(255, 180, 50, 220) : new Color(50, 50, 60, 180);
        spriteBatch.DrawCircle(center, radius, 32, bgColor, radius * 2);
        
        // Border ring
        Color borderColor = isPressed ? new Color(255, 220, 100) : new Color(100, 100, 120, 200);
        spriteBatch.DrawCircle(center, radius, 32, borderColor, 3f);
        
        // Inner highlight - DISABLED to avoid white circle
        // if (!isPressed)
        // {
        //     spriteBatch.DrawCircle(center - new Vector2(radius * 0.3f, radius * 0.3f), radius * 0.25f, 16, new Color(255, 255, 255, 40), radius * 0.5f);
        // }
        
        // Draw pause bars (two vertical rectangles)
        Color barColor = isPressed ? Color.White : new Color(220, 220, 230);
        float barWidth = radius * 0.3f;
        float barHeight = radius * 0.9f;
        float spacing = radius * 0.25f;
        
        // Left bar
        var leftBar = new RectangleF(
            center.X - spacing - barWidth,
            center.Y - barHeight / 2,
            barWidth,
            barHeight
        );
        spriteBatch.FillRectangle(leftBar, barColor);
        
        // Right bar
        var rightBar = new RectangleF(
            center.X + spacing,
            center.Y - barHeight / 2,
            barWidth,
            barHeight
        );
        spriteBatch.FillRectangle(rightBar, barColor);
    }
    
    /// <summary>
    /// Draw menu navigation buttons with improved circular design
    /// </summary>
    private void DrawMenuButtons(SpriteBatch spriteBatch, SpriteFont? font)
    {
        foreach (var button in _menuButtons)
        {
            bool isPressed = _keyStates.TryGetValue(button.Key, out bool pressed) && pressed;
            
            Vector2 center = new Vector2(
                button.Bounds.X + button.Bounds.Width / 2f,
                button.Bounds.Y + button.Bounds.Height / 2f
            );
            float radius = button.Bounds.Width / 2f;
            
            // Determine button style based on key
            if (button.Key == Keys.Enter || button.Key == Keys.Escape)
            {
                // Action buttons (A/B) - circular
                DrawCircularButton(spriteBatch, center, radius, isPressed, button.Label, font);
            }
            else
            {
                // D-pad arrows - rounded rectangles with directional arrows
                DrawDPadButton(spriteBatch, button, isPressed, font);
            }
        }
    }
    
    /// <summary>
    /// Draw circular action button (A/B style)
    /// </summary>
    private void DrawCircularButton(SpriteBatch spriteBatch, Vector2 center, float radius, bool isPressed, string label, SpriteFont? font)
    {
        // Outer glow/shadow
        if (isPressed)
        {
            spriteBatch.DrawCircle(center, radius + 4, 32, new Color(60, 100, 200, 100), 8f);
        }
        
        // Main circle background
        Color bgColor = isPressed ? new Color(70, 120, 220, 220) : new Color(40, 40, 50, 180);
        spriteBatch.DrawCircle(center, radius, 32, bgColor, radius * 2);
        
        // Border ring
        Color borderColor = isPressed ? new Color(100, 150, 255, 255) : new Color(100, 100, 120, 200);
        spriteBatch.DrawCircle(center, radius, 32, borderColor, 3f);
        
        // Inner highlight (top-left) - DISABLED to avoid white circle
        // if (!isPressed)
        // {
        //     spriteBatch.DrawCircle(center - new Vector2(radius * 0.3f, radius * 0.3f), radius * 0.3f, 16, new Color(255, 255, 255, 30), radius * 0.6f);
        // }
        
        // Draw label
        if (font != null && !string.IsNullOrEmpty(label))
        {
            try
            {
                var labelSize = font.MeasureUi(label);
                var labelPos = center - labelSize / 2f;
                
                // Shadow
                spriteBatch.DrawUiString(font, label, labelPos + new Vector2(1.5f, 1.5f), new Color(0, 0, 0, 180));
                // Main text
                spriteBatch.DrawUiString(font, label, labelPos, isPressed ? new Color(255, 255, 255) : new Color(220, 220, 230));
            }
            catch { }
        }
    }
    
    /// <summary>
    /// Draw D-pad directional button with arrow symbol
    /// </summary>
    private void DrawDPadButton(SpriteBatch spriteBatch, VirtualButton button, bool isPressed, SpriteFont? font)
    {
        Rectangle bounds = button.Bounds;
        Vector2 center = new Vector2(bounds.X + bounds.Width / 2f, bounds.Y + bounds.Height / 2f);
        
        // Background with rounded corners effect
        Color bgColor = isPressed ? new Color(70, 120, 220, 200) : new Color(45, 45, 55, 170);
        spriteBatch.FillRectangle(bounds, bgColor);
        
        // Border
        Color borderColor = isPressed ? new Color(100, 150, 255, 255) : new Color(90, 90, 110, 200);
        spriteBatch.DrawRectangle(bounds, borderColor, 2.5f);
        
        // Draw arrow symbol based on direction
        DrawArrowSymbol(spriteBatch, center, button.Key, isPressed, bounds.Width * 0.4f);
    }
    
    /// <summary>
    /// Draw arrow symbol for D-pad directions
    /// </summary>
    private void DrawArrowSymbol(SpriteBatch spriteBatch, Vector2 center, Keys direction, bool isPressed, float size)
    {
        Color arrowColor = isPressed ? Color.White : new Color(200, 200, 210);
        float thickness = isPressed ? 4f : 3f;
        
        // Arrow points (triangle)
        Vector2[] points = new Vector2[3];
        
        switch (direction)
        {
            case Keys.Up:
                points[0] = center + new Vector2(0, -size * 0.6f);           // Top
                points[1] = center + new Vector2(-size * 0.5f, size * 0.3f); // Bottom-left
                points[2] = center + new Vector2(size * 0.5f, size * 0.3f);  // Bottom-right
                break;
                
            case Keys.Down:
                points[0] = center + new Vector2(0, size * 0.6f);            // Bottom
                points[1] = center + new Vector2(-size * 0.5f, -size * 0.3f); // Top-left
                points[2] = center + new Vector2(size * 0.5f, -size * 0.3f);  // Top-right
                break;
                
            case Keys.Left:
                points[0] = center + new Vector2(-size * 0.6f, 0);           // Left
                points[1] = center + new Vector2(size * 0.3f, -size * 0.5f); // Top-right
                points[2] = center + new Vector2(size * 0.3f, size * 0.5f);  // Bottom-right
                break;
                
            case Keys.Right:
                points[0] = center + new Vector2(size * 0.6f, 0);            // Right
                points[1] = center + new Vector2(-size * 0.3f, -size * 0.5f); // Top-left
                points[2] = center + new Vector2(-size * 0.3f, size * 0.5f);  // Bottom-left
                break;
        }
        
        // Draw arrow triangle (filled)
        DrawTriangle(spriteBatch, points[0], points[1], points[2], arrowColor);
        
        // Draw arrow outline for clarity
        spriteBatch.DrawLine(points[0], points[1], arrowColor, thickness);
        spriteBatch.DrawLine(points[1], points[2], arrowColor, thickness);
        spriteBatch.DrawLine(points[2], points[0], arrowColor, thickness);
    }
    
    /// <summary>
    /// Draw a filled triangle using primitive lines
    /// </summary>
    private void DrawTriangle(SpriteBatch spriteBatch, Vector2 p1, Vector2 p2, Vector2 p3, Color color)
    {
        if (_hitboxTexture == null) return;
        
        // Simple scanline fill (approximate)
        // Draw multiple horizontal lines to fill the triangle
        float minY = Math.Min(Math.Min(p1.Y, p2.Y), p3.Y);
        float maxY = Math.Max(Math.Max(p1.Y, p2.Y), p3.Y);
        
        for (float y = minY; y <= maxY; y += 1f)
        {
            // Find intersections with triangle edges at this Y
            List<float> xIntersects = new List<float>();
            
            AddIntersection(xIntersects, p1, p2, y);
            AddIntersection(xIntersects, p2, p3, y);
            AddIntersection(xIntersects, p3, p1, y);
            
            if (xIntersects.Count >= 2)
            {
                xIntersects.Sort();
                float x1 = xIntersects[0];
                float x2 = xIntersects[xIntersects.Count - 1];
                
                spriteBatch.DrawLine(new Vector2(x1, y), new Vector2(x2, y), color, 1f);
            }
        }
    }
    
    /// <summary>
    /// Helper to find line-edge intersection
    /// </summary>
    private void AddIntersection(List<float> intersects, Vector2 p1, Vector2 p2, float y)
    {
        if ((p1.Y <= y && p2.Y >= y) || (p2.Y <= y && p1.Y >= y))
        {
            if (Math.Abs(p2.Y - p1.Y) > 0.001f)
            {
                float t = (y - p1.Y) / (p2.Y - p1.Y);
                float x = p1.X + t * (p2.X - p1.X);
                intersects.Add(x);
            }
        }
    }
    
    /// <summary>
    /// Draw Q and E buttons for settings tab navigation with improved design
    /// </summary>
    private void DrawSettingsTabButtons(SpriteBatch spriteBatch, SpriteFont? font)
    {
        if (_qButton == null || _eButton == null) return;
        
        // Draw Q button (Previous tab - left side)
        {
            bool isPressed = _keyStates.TryGetValue(Keys.Q, out bool pressed) && pressed;
            Vector2 center = new Vector2(
                _qButton.Bounds.X + _qButton.Bounds.Width / 2f,
                _qButton.Bounds.Y + _qButton.Bounds.Height / 2f
            );
            float radius = _qButton.Bounds.Width / 2f;
            
            DrawCircularButton(spriteBatch, center, radius, isPressed, _qButton.Label, font);
        }
        
        // Draw E button (Next tab - right side)
        {
            bool isPressed = _keyStates.TryGetValue(Keys.E, out bool pressed) && pressed;
            Vector2 center = new Vector2(
                _eButton.Bounds.X + _eButton.Bounds.Width / 2f,
                _eButton.Bounds.Y + _eButton.Bounds.Height / 2f
            );
            float radius = _eButton.Bounds.Width / 2f;
            
            DrawCircularButton(spriteBatch, center, radius, isPressed, _eButton.Label, font);
        }
    }
    
    /// <summary>
    /// Draw a rectangle border
    /// </summary>
    private void DrawRectangleBorder(SpriteBatch spriteBatch, Rectangle rect, int thickness, Color color)
    {
        if (_hitboxTexture == null) return;
        
        // Top
        spriteBatch.Draw(_hitboxTexture, new Rectangle(rect.X, rect.Y, rect.Width, thickness), color);
        // Bottom
        spriteBatch.Draw(_hitboxTexture, new Rectangle(rect.X, rect.Bottom - thickness, rect.Width, thickness), color);
        // Left
        spriteBatch.Draw(_hitboxTexture, new Rectangle(rect.X, rect.Y, thickness, rect.Height), color);
        // Right
        spriteBatch.Draw(_hitboxTexture, new Rectangle(rect.Right - thickness, rect.Y, thickness, rect.Height), color);
    }
    
    /// <summary>
    /// Cleanup resources
    /// </summary>
    public void Dispose()
    {
        _hitboxTexture?.Dispose();
        _hitboxTexture = null;
        _menuButtons.Clear();
        _keyStates.Clear();
    }
}

/// <summary>
/// Represents a gameplay hitbox button
/// </summary>
internal class Hitbox
{
    public Keys Key { get; set; }
    public Color Color { get; set; }
    public Rectangle Bounds { get; set; }
    public bool IsPressed { get; set; }
}

/// <summary>
/// Represents a menu navigation button
/// </summary>
internal class VirtualButton
{
    public Keys Key { get; set; }
    public Rectangle Bounds { get; set; }
    public string Label { get; set; } = "";
}
#endif
