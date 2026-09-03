using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using Microsoft.Xna.Framework.Input;
using Microsoft.Xna.Framework.Input.Touch;
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
    private readonly Hitbox[] _hitboxes = new Hitbox[4];
    private readonly List<VirtualButton> _menuButtons = new();
    private VirtualButton? _pauseButton;  // Separate pause button for gameplay only
    private VirtualButton? _qButton;  // Q button for settings tab navigation (prev tab)
    private VirtualButton? _eButton;  // E button for settings tab navigation (next tab)
    private readonly Dictionary<Keys, bool> _keyStates = new();
    private readonly Dictionary<Keys, bool> _keyStatesPrev = new();  // Previous frame state for detecting justPressed
    private Texture2D? _hitboxTexture;
    private bool _showSettingsButtons = false;  // Flag to show/hide Q and E buttons
    
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
    private const float HITBOX_PRESSED_ALPHA = 0.4f; // Visible pero sutil cuando se presiona (era 0.8)
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
    /// Initialize 4 vertical hitboxes for gameplay
    /// </summary>
    private void InitializeGameplayHitboxes()
    {
        float hitboxWidth = _screenWidth / 4f;
        
        for (int i = 0; i < 4; i++)
        {
            _hitboxes[i] = new Hitbox
            {
                Key = HITBOX_KEYS[i],
                Color = HITBOX_COLORS[i],
                Bounds = new Rectangle(
                    (int)(i * hitboxWidth),
                    0,
                    (int)hitboxWidth,
                    _screenHeight
                ),
                IsPressed = false
            };
        }
    }
    
    /// <summary>
    /// Initialize menu navigation buttons (D-pad + action buttons)
    /// </summary>
    private void InitializeMenuButtons()
    {
        _menuButtons.Clear();
        
        int buttonSize = (int)(_screenHeight * MENU_BUTTON_SIZE);
        int margin = (int)(_screenHeight * MENU_MARGIN);
        int spacing = (int)(_screenHeight * 0.02f);
        
        // Extra margin for Android navigation bar (bottom 10% of screen)
        int bottomSafeZone = (int)(_screenHeight * 0.10f);
        
        // ── D-Pad (Left side) ────────────────────────────────────────────
        int dpadCenterX = margin + buttonSize;
        int dpadCenterY = _screenHeight - margin - buttonSize - bottomSafeZone;
        
        _menuButtons.Add(new VirtualButton
        {
            Key = Keys.Left,
            Bounds = new Rectangle(margin, dpadCenterY, buttonSize, buttonSize),
            Label = "<"  // Left arrow
        });
        
        _menuButtons.Add(new VirtualButton
        {
            Key = Keys.Right,
            Bounds = new Rectangle(dpadCenterX + spacing, dpadCenterY, buttonSize, buttonSize),
            Label = ">"  // Right arrow
        });
        
        _menuButtons.Add(new VirtualButton
        {
            Key = Keys.Up,
            Bounds = new Rectangle(dpadCenterX, dpadCenterY - buttonSize - spacing, buttonSize, buttonSize),
            Label = "^"  // Up arrow
        });
        
        _menuButtons.Add(new VirtualButton
        {
            Key = Keys.Down,
            Bounds = new Rectangle(dpadCenterX, dpadCenterY + buttonSize + spacing, buttonSize, buttonSize),
            Label = "v"  // Down arrow
        });
        
        // ── Action Buttons (Right side) ──────────────────────────────
        int actionX = _screenWidth - margin - buttonSize;
        int actionY = _screenHeight - margin - buttonSize - bottomSafeZone;
        
        // Enter/Space (OK button) - botón A para confirmar (maneja ambas teclas)
        _menuButtons.Add(new VirtualButton
        {
            Key = Keys.Enter,
            Bounds = new Rectangle(actionX, actionY, buttonSize, buttonSize),
            Label = "A"
        });
        
        // Escape (back button) - botón B para volver
        _menuButtons.Add(new VirtualButton
        {
            Key = Keys.Escape,
            Bounds = new Rectangle(actionX - buttonSize - spacing, actionY, (int)(buttonSize * 0.8f), (int)(buttonSize * 0.8f)),
            Label = "B"
        });
        
        // ── Settings Tab Navigation (Q and E) ────────────────────────
        // These buttons appear at the top of the screen, left and right sides
        // Only visible when _showSettingsButtons is true
        int tabButtonSize = (int)(buttonSize * 0.9f);
        int topMargin = (int)(_screenHeight * 0.05f);
        
        // Q button - Previous tab (left side, top)
        _qButton = new VirtualButton
        {
            Key = Keys.Q,
            Bounds = new Rectangle(margin, topMargin, tabButtonSize, tabButtonSize),
            Label = "Q"
        };
        
        // E button - Next tab (right side, top)
        _eButton = new VirtualButton
        {
            Key = Keys.E,
            Bounds = new Rectangle(_screenWidth - margin - tabButtonSize, topMargin, tabButtonSize, tabButtonSize),
            Label = "E"
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
        _mode = mode;
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
    /// Update touch input and map to virtual keys
    /// </summary>
    public void Update()
    {
        if (_hitboxTexture == null) return;
        
        // Ensure TouchPanel is properly configured (fixes Android losing touch after scene changes)
        TouchPanel.EnabledGestures = GestureType.None;
        
        // Save previous frame state BEFORE resetting
        _keyStatesPrev.Clear();
        foreach (var kvp in _keyStates)
            _keyStatesPrev[kvp.Key] = kvp.Value;
        
        // Reset current frame states
        if (_mode == ControlMode.Gameplay)
        {
            foreach (var hitbox in _hitboxes)
                hitbox.IsPressed = false;
        }
        
        var keys = new List<Keys>(_keyStates.Keys);
        foreach (var key in keys)
            _keyStates[key] = false;
        
        // Process all active touch points
        var touches = TouchPanel.GetState();
        
        // Debug: Log touch count
        if (touches.Count > 0)
        {
            Console.WriteLine($"[TouchControls] {touches.Count} touch(es) detected in {_mode} mode");
        }
        
        foreach (var touch in touches)
        {
            // Only process Pressed and Moved states
            // Released state means finger lifted - key will stay false (already reset above)
            if (touch.State == TouchLocationState.Pressed || 
                touch.State == TouchLocationState.Moved)
            {
                var touchPos = touch.Position;
                
                Console.WriteLine($"[TouchControls] Touch at ({touchPos.X:F0}, {touchPos.Y:F0}) state={touch.State}");
                
                if (_mode == ControlMode.Gameplay)
                {
                    // Check pause button first (only in gameplay mode)
                    if (_pauseButton != null && 
                        _pauseButton.Bounds.Contains((int)touchPos.X, (int)touchPos.Y))
                    {
                        _keyStates[Keys.P] = true;
                        Console.WriteLine("[TouchControls] Pause button pressed!");
                    }
                    
                    // Check hitboxes
                    for (int i = 0; i < _hitboxes.Length; i++)
                    {
                        if (_hitboxes[i].Bounds.Contains((int)touchPos.X, (int)touchPos.Y))
                        {
                            _hitboxes[i].IsPressed = true;
                            _keyStates[_hitboxes[i].Key] = true;
                            Console.WriteLine($"[TouchControls] Hitbox {i} pressed! Key={_hitboxes[i].Key} - Setting to TRUE");
                        }
                    }
                }
                else // Menu mode
                {
                    // Check menu buttons
                    foreach (var button in _menuButtons)
                    {
                        if (button.Bounds.Contains((int)touchPos.X, (int)touchPos.Y))
                        {
                            _keyStates[button.Key] = true;
                            Console.WriteLine($"[TouchControls] Menu button pressed! Key={button.Key}");
                        }
                    }
                    
                    // Check Q and E buttons (only if settings buttons are visible)
                    if (_showSettingsButtons)
                    {
                        if (_qButton != null && _qButton.Bounds.Contains((int)touchPos.X, (int)touchPos.Y))
                        {
                            _keyStates[Keys.Q] = true;
                            Console.WriteLine("[TouchControls] Q button pressed!");
                        }
                        
                        if (_eButton != null && _eButton.Bounds.Contains((int)touchPos.X, (int)touchPos.Y))
                        {
                            _keyStates[Keys.E] = true;
                            Console.WriteLine("[TouchControls] E button pressed!");
                        }
                    }
                }
            }
        }
    }
    
    /// <summary>
    /// Check if a virtual key is currently pressed
    /// </summary>
    public bool IsKeyDown(Keys key)
    {
        return _keyStates.TryGetValue(key, out bool pressed) && pressed;
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
    /// Draw touch controls on screen
    /// </summary>
    public void Draw(SpriteBatch spriteBatch, SpriteFont? font)
    {
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
        else
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
            float alpha = Game1.HideTouchHitboxColors ? 0f : (hitbox.IsPressed ? HITBOX_PRESSED_ALPHA : HITBOX_IDLE_ALPHA);
            Color color = hitbox.Color * alpha;
            
            spriteBatch.Draw(_hitboxTexture, hitbox.Bounds, color);
        }
    }
    
    /// <summary>
    /// Draw pause button (always visible in gameplay, top-right corner)
    /// </summary>
    private void DrawPauseButton(SpriteBatch spriteBatch, SpriteFont? font)
    {
        if (_pauseButton == null) return;
        
        var pauseButton = _pauseButton;
        bool isPressed = _keyStates.TryGetValue(Keys.P, out bool pressed) && pressed;
        Color bgColor = isPressed ? new Color(60, 100, 200, 200) : new Color(30, 30, 40, 160);
        Color barColor = isPressed ? Color.White : new Color(200, 200, 200);
        
        // Draw background circle/square
        spriteBatch.Draw(_hitboxTexture, pauseButton.Bounds, bgColor);
        
        // Draw border
        DrawRectangleBorder(spriteBatch, pauseButton.Bounds, 2, new Color(80, 80, 100, 200));
        
        // Draw pause symbol (two vertical bars)
        int barWidth = pauseButton.Bounds.Width / 5;
        int barHeight = pauseButton.Bounds.Height / 2;
        int centerX = pauseButton.Bounds.X + pauseButton.Bounds.Width / 2;
        int centerY = pauseButton.Bounds.Y + pauseButton.Bounds.Height / 2;
        int spacing = barWidth / 2;
        
        // Left bar
        spriteBatch.Draw(_hitboxTexture, 
            new Rectangle(centerX - barWidth - spacing/2, centerY - barHeight/2, barWidth, barHeight), 
            barColor);
        
        // Right bar
        spriteBatch.Draw(_hitboxTexture, 
            new Rectangle(centerX + spacing/2, centerY - barHeight/2, barWidth, barHeight), 
            barColor);
    }
    
    /// <summary>
    /// Draw menu navigation buttons
    /// </summary>
    private void DrawMenuButtons(SpriteBatch spriteBatch, SpriteFont? font)
    {
        foreach (var button in _menuButtons)
        {
            bool isPressed = _keyStates.TryGetValue(button.Key, out bool pressed) && pressed;
            Color fillColor = isPressed ? MENU_COLOR_PRESSED : MENU_COLOR_IDLE;
            
            // Draw button background
            spriteBatch.Draw(_hitboxTexture, button.Bounds, fillColor);
            
            // Draw button border
            DrawRectangleBorder(spriteBatch, button.Bounds, 2, MENU_COLOR_BORDER);
            
            // Draw label (with safety for missing characters)
            if (font != null && !string.IsNullOrEmpty(button.Label))
            {
                try
                {
                    var labelSize = font.MeasureString(button.Label);
                    var labelPos = new Vector2(
                        button.Bounds.X + (button.Bounds.Width - labelSize.X) / 2,
                        button.Bounds.Y + (button.Bounds.Height - labelSize.Y) / 2
                    );
                    
                    // Shadow for readability
                    spriteBatch.DrawString(font, button.Label, labelPos + new Vector2(2, 2), Color.Black);
                    // Main text
                    spriteBatch.DrawString(font, button.Label, labelPos, Color.White);
                }
                catch
                {
                    // Silently skip if font doesn't support character
                }
            }
        }
    }
    
    /// <summary>
    /// Draw Q and E buttons for settings tab navigation
    /// </summary>
    private void DrawSettingsTabButtons(SpriteBatch spriteBatch, SpriteFont? font)
    {
        if (_qButton == null || _eButton == null) return;
        
        // Draw Q button (Previous tab)
        {
            bool isPressed = _keyStates.TryGetValue(Keys.Q, out bool pressed) && pressed;
            Color fillColor = isPressed ? MENU_COLOR_PRESSED : MENU_COLOR_IDLE;
            
            spriteBatch.Draw(_hitboxTexture, _qButton.Bounds, fillColor);
            DrawRectangleBorder(spriteBatch, _qButton.Bounds, 2, MENU_COLOR_BORDER);
            
            if (font != null)
            {
                try
                {
                    var labelSize = font.MeasureString(_qButton.Label);
                    var labelPos = new Vector2(
                        _qButton.Bounds.X + (_qButton.Bounds.Width - labelSize.X) / 2,
                        _qButton.Bounds.Y + (_qButton.Bounds.Height - labelSize.Y) / 2
                    );
                    spriteBatch.DrawString(font, _qButton.Label, labelPos + new Vector2(2, 2), Color.Black);
                    spriteBatch.DrawString(font, _qButton.Label, labelPos, Color.White);
                }
                catch { }
            }
        }
        
        // Draw E button (Next tab)
        {
            bool isPressed = _keyStates.TryGetValue(Keys.E, out bool pressed) && pressed;
            Color fillColor = isPressed ? MENU_COLOR_PRESSED : MENU_COLOR_IDLE;
            
            spriteBatch.Draw(_hitboxTexture, _eButton.Bounds, fillColor);
            DrawRectangleBorder(spriteBatch, _eButton.Bounds, 2, MENU_COLOR_BORDER);
            
            if (font != null)
            {
                try
                {
                    var labelSize = font.MeasureString(_eButton.Label);
                    var labelPos = new Vector2(
                        _eButton.Bounds.X + (_eButton.Bounds.Width - labelSize.X) / 2,
                        _eButton.Bounds.Y + (_eButton.Bounds.Height - labelSize.Y) / 2
                    );
                    spriteBatch.DrawString(font, _eButton.Label, labelPos + new Vector2(2, 2), Color.Black);
                    spriteBatch.DrawString(font, _eButton.Label, labelPos, Color.White);
                }
                catch { }
            }
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
