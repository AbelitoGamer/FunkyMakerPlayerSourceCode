using System;
using System.Collections.Generic;
using System.IO;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using Microsoft.Xna.Framework.Input;

namespace FunkyMakerPlayer;

/// <summary>
/// Main menu with a slide-in level panel on the right side.
/// Layout:
///   Left area  — title, nav buttons (Freeplay, HUD Editor, Credits)
///   Right panel — slides in when Freeplay is selected, shows level list
/// </summary>
public partial class FreeplayMenu
{
    // ── Public ───────────────────────────────────────────────────────────────
    public bool   LevelChosen  { get; set; }
    public string LevelPath    { get; private set; } = "";
    /// <summary>-1=none, 1=HUD Editor, 2=Credits, 3=Multiplayer — consumed once read</summary>
    public int    MenuAction   { get; internal set; } = -1;
    
    /// <summary>Action to call when file picker should be opened (Blazor only)</summary>
    public Action? OnFilePickerRequested { get; set; }

    // ── Private ──────────────────────────────────────────────────────────────
    private readonly List<string> _paths = new();
    private readonly List<string> _names = new();
    private int   _selected   = 0;
    private float _scrollY    = 0f;
    private float _targetScrollY = 0f;

    private readonly Texture2D   _pixel;
    private readonly SpriteFont? _font;
    private readonly FontRenderer? _fontRenderer;  // Nueva fuente TrueType
    private readonly string      _levelsFolder;
    private readonly Texture2D?  _folderIcon;  // Folder icon texture
    private GraphicsDevice? _graphicsDevice;  // For scissor rectangle clipping

    private KeyboardState _prevKb;
    private MouseState    _prevMs;

    // Panel slide animation
    private float _panelX     = 1f;   // 0 = fully open, 1 = fully closed (normalized)
    private bool  _panelOpen  = false;

    // Drop zone pulse animation
    private float _dropZonePulse = 0f;
    private bool  _dropZonePulseDirection = true;
    
    // Folder icon hover state
    private bool  _folderIconHover = false;
    
    // Screen dimensions for mouse coordinate transformation
    private int _lastScreenWidth = 1280;   // Render target width
    private int _lastScreenHeight = 720;   // Render target height
    private int _lastWindowWidth = 1280;   // Actual window width
    private int _lastWindowHeight = 720;   // Actual window height

    // Main menu selection: 0=Freeplay, 1=HUD Editor, 2=Multiplayer, 3=Credits
    private int  _menuSel     = 0;
    private bool _inPanel     = false;  // true when navigating the level list

    // Transition animations
    private enum TransitionState { None, HudEditorFall, CreditsIn }
    private TransitionState _transition = TransitionState.None;
    private float _transTimer  = 0f;
    private const float TRANS_DURATION = 0.45f;

    // Credits fade
    private float _creditsAlpha = 0f;
    private bool  _creditsOpen  = false;
    
    // Clickable links in credits
    private struct ClickableLink
    {
        public Rectangle Bounds;
        public string Url;
        public bool IsHovered;
    }
    private List<ClickableLink> _creditLinks = new();

    private const int ITEM_H    = 48;
    private const int PANEL_W   = 650;  // Aumentado de 480 a 650 para más espacio

    // Input cooldown — ignores inputs for a short time after opening to prevent accidental selections
    private float _inputCooldown = 0f;
    private const float INPUT_COOLDOWN_DURATION = 0.25f;

    public FreeplayMenu(Texture2D pixel, SpriteFont? font, string levelsFolder, Texture2D? folderIcon = null, FontRenderer? fontRenderer = null)
    {
        _pixel        = pixel;
        _font         = font;
        _fontRenderer = fontRenderer;
        _levelsFolder = levelsFolder;
        _folderIcon   = folderIcon;
        Refresh();
    }

    /// <summary>Call this when the freeplay menu becomes visible to prevent input bleed-through.</summary>
    public void ResetInputCooldown() => _inputCooldown = INPUT_COOLDOWN_DURATION;

    public void Refresh()
    {
        _paths.Clear();
        _names.Clear();

        // Ensure folder exists before scanning
        if (!Directory.Exists(_levelsFolder))
        {
            Console.WriteLine($"[FreeplayMenu] Levels folder doesn't exist yet: {_levelsFolder}");
            Console.WriteLine($"[FreeplayMenu] Creating it now...");
            try
            {
                Directory.CreateDirectory(_levelsFolder);
                Console.WriteLine($"[FreeplayMenu] ✅ Created: {_levelsFolder}");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[FreeplayMenu] ❌ Failed to create folder: {ex.Message}");
                return;
            }
        }

        var files = new List<string>(Directory.GetFiles(_levelsFolder, "*.FNMM", SearchOption.TopDirectoryOnly));
        foreach (var f in Directory.GetFiles(_levelsFolder, "*.fnmm", SearchOption.TopDirectoryOnly))
            if (!files.Contains(f)) files.Add(f);
        // Also list .rar level archives
        foreach (var f in Directory.GetFiles(_levelsFolder, "*.rar", SearchOption.TopDirectoryOnly))
            if (!files.Contains(f)) files.Add(f);
        
        // Also list folders that contain a meta.json file (unpacked levels)
        foreach (var dir in Directory.GetDirectories(_levelsFolder))
        {
            string metaPath = Path.Combine(dir, "meta.json");
            if (File.Exists(metaPath))
                files.Add(dir);
        }

        files.Sort(StringComparer.OrdinalIgnoreCase);

        foreach (var f in files)
        {
            _paths.Add(f);
            _names.Add(Path.GetFileNameWithoutExtension(f));
        }

        _selected = Math.Clamp(_selected, 0, Math.Max(0, _paths.Count - 1));
    }

    public void Update(float dt, KeyboardState kb)
    {
        Update(dt, kb, 1280, 720, 1280, 720); // Default resolution
    }
    
    public void Update(float dt, KeyboardState kb, int screenWidth, int screenHeight, int windowWidth = 0, int windowHeight = 0)
    {
        // Use window dimensions if provided, otherwise use screen dimensions
        if (windowWidth == 0) windowWidth = screenWidth;
        if (windowHeight == 0) windowHeight = screenHeight;
        
        // Animate panel
        float target = _panelOpen ? 0f : 1f;
        _panelX = MathHelper.Lerp(_panelX, target, Math.Min(1f, dt * 14f));

        // Animate drop zone pulse (for when list is empty)
        if (_dropZonePulseDirection)
        {
            _dropZonePulse += dt * 2f;
            if (_dropZonePulse >= 1f)
            {
                _dropZonePulse = 1f;
                _dropZonePulseDirection = false;
            }
        }
        else
        {
            _dropZonePulse -= dt * 2f;
            if (_dropZonePulse <= 0f)
            {
                _dropZonePulse = 0f;
                _dropZonePulseDirection = true;
            }
        }

        // Handle mouse input for folder icon (both Blazor and Windows)
        // Store screen dimensions for mouse coordinate transformation
        _lastScreenWidth = screenWidth;
        _lastScreenHeight = screenHeight;
        _lastWindowWidth = windowWidth;
        _lastWindowHeight = windowHeight;
        
        var mouse = Mouse.GetState();
        bool mouseClicked = mouse.LeftButton == ButtonState.Pressed && 
                           _prevMs.LeftButton == ButtonState.Released;
        
        // Check if hovering over folder icon
        if (_panelOpen && _paths.Count == 0 && _folderIcon != null)
        {
            var iconBounds = GetFolderIconBounds(screenWidth, screenHeight);
            _folderIconHover = iconBounds.Contains(mouse.Position);
            
            // Handle click on folder icon
            if (mouseClicked && _folderIconHover)
            {
                #if BLAZORGL
                OnFilePickerRequested?.Invoke();
                #else
                OpenFolderBrowser();
                #endif
            }
        }
        else
        {
            _folderIconHover = false;
        }

        // Input cooldown — ignore all inputs briefly after opening
        if (_inputCooldown > 0f)
        {
            _inputCooldown -= dt;
            _prevKb = kb;
            return;
        }

        // Handle transition animations
        if (_transition != TransitionState.None)
        {
            _transTimer += dt;
            if (_transition == TransitionState.CreditsIn)
                _creditsAlpha = Math.Min(1f, _transTimer / TRANS_DURATION);

            if (_transTimer >= TRANS_DURATION)
            {
                if (_transition == TransitionState.CreditsIn)
                {
                    _creditsOpen = true;
                    _creditsAlpha = 1f;
                }
                _transition = TransitionState.None;
                _transTimer = 0f;
            }
            _prevKb = kb;
            return;
        }

        bool up    = kb.IsKeyDown(Keys.Up)    && !_prevKb.IsKeyDown(Keys.Up);
        bool down  = kb.IsKeyDown(Keys.Down)  && !_prevKb.IsKeyDown(Keys.Down);
        bool enter = kb.IsKeyDown(Keys.Enter) && !_prevKb.IsKeyDown(Keys.Enter);
        bool esc   = kb.IsKeyDown(Keys.Escape) && !_prevKb.IsKeyDown(Keys.Escape);
        bool r     = kb.IsKeyDown(Keys.R)     && !_prevKb.IsKeyDown(Keys.R);

        // Close credits with ESC or Enter
        if (_creditsOpen)
        {
            if (esc || enter)
            {
                _creditsOpen  = false;
                _creditsAlpha = 0f;
            }
            
            // Check for clicks on credit links
            if (mouse.LeftButton == ButtonState.Pressed && _prevMs.LeftButton == ButtonState.Released)
            {
                // NOTE: Click handling moved to Draw() where bounds are calculated
            }
            
            _prevKb = kb;
            return;
        }

        if (_inPanel)
        {
            if (up)   _selected = (_selected - 1 + Math.Max(1, _paths.Count)) % Math.Max(1, _paths.Count);
            if (down) _selected = (_selected + 1) % Math.Max(1, _paths.Count);
            if (r)    Refresh();

            if (enter && _paths.Count > 0)
            {
                LevelPath   = _paths[_selected];
                LevelChosen = true;
            }

            if (esc)
            {
                _inPanel    = false;
                _panelOpen  = false;
            }

            _targetScrollY = _selected * ITEM_H;
            _scrollY = MathHelper.Lerp(_scrollY, _targetScrollY, Math.Min(1f, dt * 12f));
        }
        else
        {
            if (up)   
            {
                // Skip disabled items
                do {
                    _menuSel = (_menuSel - 1 + 4) % 4;
                } while (!IsMenuItemEnabledInternal(_menuSel));
            }
            if (down) 
            {
                // Skip disabled items  
                do {
                    _menuSel = (_menuSel + 1) % 4;
                } while (!IsMenuItemEnabledInternal(_menuSel));
            }

            if (enter && IsMenuItemEnabledInternal(_menuSel))
            {
                if (_menuSel == 0)  // Freeplay
                {
                    _panelOpen = true;
                    _inPanel   = true;
                }
                else if (_menuSel == 1)  // HUD Editor
                {
                    MenuAction = 1;  // Signal to open HUD editor screen
                }
                else if (_menuSel == 2)  // Multiplayer
                {
                    MenuAction = 3;  // Signal to open multiplayer menu
                }
                else if (_menuSel == 3)  // Credits — fade in
                {
                    _transition   = TransitionState.CreditsIn;
                    _transTimer   = 0f;
                    _creditsAlpha = 0f;
                }
            }
        }

        _prevKb = kb;
        _prevMs = mouse;
    }

    /// <summary>Returns the current main menu selection action, or -1 if none.</summary>
    public int ConsumeMenuAction()
    {
        if (!_inPanel)
        {
            var kb = Keyboard.GetState();
            bool enter = kb.IsKeyDown(Keys.Enter) && !_prevKb.IsKeyDown(Keys.Enter);
            if (enter && _menuSel == 1) return 1;  // HUD Editor
            if (enter && _menuSel == 2) return 2;  // Credits
        }
        return -1;
    }

    public void Draw(SpriteBatch sb, int sw, int sh, int windowWidth = 0, int windowHeight = 0)
    {
        // Capture GraphicsDevice for scissor rectangle
        _graphicsDevice = sb.GraphicsDevice;
        
        // Use window dimensions if provided, otherwise use render target dimensions
        if (windowWidth == 0) windowWidth = sw;
        if (windowHeight == 0) windowHeight = sh;
        
        // Store for mouse transformation
        _lastScreenWidth = sw;
        _lastScreenHeight = sh;
        _lastWindowWidth = windowWidth;
        _lastWindowHeight = windowHeight;
        // ── Modern solid background ──────────────────────────────────────────
        // Smooth dark gradient
        DrawRect(sb, 0, 0, sw, sh, new Color(20, 18, 28));

        // Subtle pattern overlay
        for (int y = 0; y < sh; y += 120)
        {
            DrawRect(sb, 0, y, sw, 1, Color.White * 0.02f);
        }
        for (int x = 0; x < sw; x += 120)
        {
            DrawRect(sb, x, 0, 1, sh, Color.White * 0.02f);
        }

        // ── Left area — title + menu ──────────────────────────────────────────
        int leftW = sw - (int)((1f - _panelX) * PANEL_W);

        if (_font != null || _fontRenderer != null)
        {
            // Title with glow effect - RESPONSIVE SIZE
            int titleSize = (int)(sh * 0.065f);  // Reducido de 0.10f a 0.065f
            titleSize = Math.Clamp(titleSize, 40, 80);  // Ajustado rango
            string title = "FUNKY MAKER";
            var titleSz = MeasureText(title, titleSize);
            float titleX = leftW / 2f - titleSz.X / 2f;
            float titleY = sh * 0.22f;
            
            // Glow effect (single layer for cleaner look)
            Color glowColor = new Color(255, 68, 136) * 0.1f;
            DrawText(sb, title, new Vector2(titleX + 2, titleY + 2), glowColor, titleSize);
            
            // Main title
            DrawText(sb, title, new Vector2(titleX, titleY), new Color(255, 215, 0), titleSize);
            
            // "PLAYER" subtitle with different color
            string player = "PLAYER";
            int playerSize = (int)(sh * 0.050f);  // Reducido de 0.078f a 0.050f
            playerSize = Math.Clamp(playerSize, 32, 64);  // Ajustado rango
            var playerSz = MeasureText(player, playerSize);
            float playerY = titleY + titleSz.Y - 5;
            DrawText(sb, player, new Vector2(leftW / 2f - playerSz.X / 2f, playerY),
                new Color(255, 68, 136), playerSize);

            // Decorative line under title
            int lineY = (int)(playerY + playerSz.Y + 15);
            int lineW = 300;
            DrawRect(sb, leftW / 2 - lineW / 2, lineY, lineW, 3, new Color(255, 68, 136) * 0.6f);
            // Accent dots
            DrawRect(sb, leftW / 2 - lineW / 2 - 15, lineY, 8, 3, new Color(255, 215, 0));
            DrawRect(sb, leftW / 2 + lineW / 2 + 7, lineY, 8, 3, new Color(255, 215, 0));

            // Subtitle - RESPONSIVE
            string sub = "Fan-made PC player for Funky Maker Mobile";
            int subSize = (int)(sh * 0.025f);  // Reducido de 0.039f a 0.025f
            subSize = Math.Clamp(subSize, 16, 32);  // Ajustado rango
            var subSz = MeasureText(sub, subSize);
            DrawText(sb, sub, new Vector2(leftW / 2f - subSz.X / 2f, lineY + 25),
                Color.White * 0.4f, subSize);

            // Menu items — Modern card-style buttons - RESPONSIVE
            string[] items = { "FREEPLAY", "HUD EDITOR", "MULTIPLAYER", "CREDITS" };
            float menuY = sh * 0.56f;
            int menuSize = (int)(sh * 0.038f);  // Reducido de 0.058f a 0.038f
            menuSize = Math.Clamp(menuSize, 24, 48);  // Ajustado rango
            int cardWidth = (int)(leftW * 0.45f);  // 45% del ancho izquierdo
            cardWidth = Math.Clamp(cardWidth, 280, 400);
            int cardHeight = (int)(sh * 0.065f);  // Reducido de 0.078f a 0.065f
            cardHeight = Math.Clamp(cardHeight, 42, 68);  // Ajustado rango
            
            for (int i = 0; i < items.Length; i++)
            {
                bool sel = !_inPanel && i == _menuSel;
                bool enabled = IsMenuItemEnabledInternal(i);
                Color textCol = enabled 
                    ? (sel ? Color.White : new Color(200, 200, 220))
                    : new Color(100, 60, 80);
                
                int itemSize = sel ? menuSize + 2 : menuSize;  // Un poco más grande cuando seleccionado
                var sz = MeasureText(items[i], itemSize);
                
                int cardX = leftW / 2 - cardWidth / 2;
                int cardY = (int)(menuY - cardHeight / 2 + 8);
                
                // Card background
                if (enabled)
                {
                    if (sel)
                    {
                        // Selected: gradient card with glow
                        Color cardTop = new Color(255, 68, 136) * 0.25f;
                        Color cardBottom = new Color(255, 68, 136) * 0.15f;
                        
                        // Glow border
                        DrawRect(sb, cardX - 3, cardY - 3, cardWidth + 6, cardHeight + 6, 
                            new Color(255, 68, 136) * 0.4f);
                        
                        // Gradient fill
                        for (int g = 0; g < 10; g++)
                        {
                            float gt = g / 10f;
                            DrawRect(sb, cardX, cardY + g * (cardHeight / 10), cardWidth, cardHeight / 10 + 1,
                                Color.Lerp(cardTop, cardBottom, gt));
                        }
                        
                        // Accent line on left
                        DrawRect(sb, cardX, cardY, 4, cardHeight, new Color(255, 215, 0));
                    }
                    else
                    {
                        // Not selected: subtle dark card
                        DrawRect(sb, cardX, cardY, cardWidth, cardHeight, new Color(35, 35, 55) * 0.4f);
                        DrawRect(sb, cardX, cardY, cardWidth, 1, Color.White * 0.1f);
                        DrawRect(sb, cardX, cardY + cardHeight - 1, cardWidth, 1, Color.White * 0.05f);
                    }
                }
                else
                {
                    // Disabled: dark with strikethrough
                    DrawRect(sb, cardX, cardY, cardWidth, cardHeight, new Color(25, 15, 20) * 0.5f);
                    DrawRect(sb, cardX + 30, cardY + cardHeight / 2, cardWidth - 60, 2, new Color(255, 0, 0) * 0.3f);
                }
                
                // Text centered in card (both horizontally and vertically)
                float textX = leftW / 2f - sz.X / 2f;
                float textY = menuY - sz.Y / 2f;  // Centrado vertical
                
                DrawText(sb, items[i], new Vector2(textX, textY), textCol, itemSize);
                
                menuY += cardHeight + 12;
            }

            // Bottom hint - clean design without bar - RESPONSIVE
            string hint = _inPanel 
                ? "< ESC Back  |  R Refresh" 
                : "UP/DOWN Navigate  |  ENTER Select  |  ESC Settings";
            int hintSize = (int)(sh * 0.023f);  // Reducido de 0.036f a 0.023f
            hintSize = Math.Clamp(hintSize, 14, 28);  // Ajustado rango
            var hintSz = MeasureText(hint, hintSize);
            
            DrawText(sb, hint, new Vector2(leftW / 2f - hintSz.X / 2f, sh - 35),
                Color.White * 0.35f, hintSize);
        }

        // ── Right panel — level list ──────────────────────────────────────────
        if (_panelX < 0.99f)
        {
            int panelLeft = (int)(sw - PANEL_W * (1f - _panelX));

            // Modern panel background - solid
            DrawRect(sb, panelLeft, 0, sw - panelLeft, sh, new Color(22, 22, 38));
            
            // Glowing accent border
            DrawRect(sb, panelLeft, 0, 3, sh, new Color(255, 68, 136) * 0.8f);
            DrawRect(sb, panelLeft + 3, 0, 1, sh, new Color(255, 215, 0) * 0.4f);

            if (_font != null || _fontRenderer != null)
            {
                // Modern panel header
                DrawRect(sb, panelLeft, 0, sw - panelLeft, 80, new Color(30, 20, 40) * 0.9f);
                DrawRect(sb, panelLeft, 78, sw - panelLeft, 2, new Color(255, 68, 136) * 0.5f);
                
                // "LEVELS" title with icon - RESPONSIVE
                string levelsTitle = "LEVELS";
                int levelsTitleSize = (int)(sh * 0.045f);  // Reducido de 0.067f a 0.045f
                levelsTitleSize = Math.Clamp(levelsTitleSize, 28, 56);  // Ajustado rango
                DrawText(sb, levelsTitle, new Vector2(panelLeft + 20, 16),
                    new Color(255, 215, 0), levelsTitleSize);
                
                // Count badge - RESPONSIVE
                string countStr = $"{_paths.Count} SONGS";
                int countSize = (int)(sh * 0.026f);  // Reducido de 0.039f a 0.026f
                countSize = Math.Clamp(countSize, 16, 34);  // Ajustado rango
                var countSz = MeasureText(countStr, countSize);
                int headerBadgeX = panelLeft + 20;
                int headerBadgeY = 50;
                int headerBadgeW = (int)countSz.X + 16;
                int headerBadgeH = (int)(sh * 0.030f);  // Reducido de 0.036f a 0.030f
                headerBadgeH = Math.Clamp(headerBadgeH, 18, 28);  // Ajustado rango
                
                DrawRect(sb, headerBadgeX, headerBadgeY, headerBadgeW, headerBadgeH, new Color(255, 68, 136) * 0.3f);
                DrawRect(sb, headerBadgeX, headerBadgeY, 2, headerBadgeH, new Color(255, 68, 136));
                DrawText(sb, countStr, new Vector2(headerBadgeX + 8, headerBadgeY + 2),
                    Color.White * 0.8f, countSize);
                
                // "R to refresh" hint - RESPONSIVE
                string refreshHint = "[R] REFRESH";
                int refreshSize = (int)(sh * 0.022f);  // Reducido de 0.033f a 0.022f
                refreshSize = Math.Clamp(refreshSize, 14, 28);  // Ajustado rango
                var refreshSz = MeasureText(refreshHint, refreshSize);
                DrawText(sb, refreshHint, new Vector2(panelLeft + (sw - panelLeft) - refreshSz.X - 20, 54),
                    new Color(255, 215, 0) * 0.5f, refreshSize);

                int listTop = 85;
                int listH   = sh - listTop - 55;

                if (_paths.Count == 0)
                {
                    // ── EMPTY STATE: Drop Zone Visual ─────────────────────────────
                    
                    // Calculate pulse effect (0.5 to 1.0)
                    float pulseAlpha = 0.5f + (_dropZonePulse * 0.5f);
                    
                    // Drop zone box (dashed border effect)
                    int dropBoxW = 380;
                    int dropBoxH = 280;
                    int dropBoxX = panelLeft + (sw - panelLeft - dropBoxW) / 2;
                    int dropBoxY = sh / 2 - dropBoxH / 2;
                    
                    // Background box with subtle gradient
                    DrawRect(sb, dropBoxX, dropBoxY, dropBoxW, dropBoxH, 
                        new Color(30, 30, 50) * 0.6f);
                    
                    // Pulsing border (4 sides)
                    Color borderColor = new Color(0, 180, 255) * pulseAlpha;  // Cyan
                    int borderThickness = 3;
                    
                    // Top border (dashed)
                    for (int i = 0; i < dropBoxW; i += 20)
                    {
                        int dashLen = Math.Min(12, dropBoxW - i);
                        DrawRect(sb, dropBoxX + i, dropBoxY, dashLen, borderThickness, borderColor);
                    }
                    
                    // Bottom border (dashed)
                    for (int i = 0; i < dropBoxW; i += 20)
                    {
                        int dashLen = Math.Min(12, dropBoxW - i);
                        DrawRect(sb, dropBoxX + i, dropBoxY + dropBoxH - borderThickness, 
                            dashLen, borderThickness, borderColor);
                    }
                    
                    // Left border (dashed)
                    for (int i = 0; i < dropBoxH; i += 20)
                    {
                        int dashLen = Math.Min(12, dropBoxH - i);
                        DrawRect(sb, dropBoxX, dropBoxY + i, borderThickness, dashLen, borderColor);
                    }
                    
                    // Right border (dashed)
                    for (int i = 0; i < dropBoxH; i += 20)
                    {
                        int dashLen = Math.Min(12, dropBoxH - i);
                        DrawRect(sb, dropBoxX + dropBoxW - borderThickness, dropBoxY + i, 
                            borderThickness, dashLen, borderColor);
                    }
                    
                    // Icon (folder PNG or fallback to text)
                    float iconY = dropBoxY + 50;
                    float mainTextYOffset;
                    
                    if (_folderIcon != null)
                    {
                        // Draw PNG icon
                        int iconSize = 128;
                        int iconX = dropBoxX + (dropBoxW - iconSize) / 2;
                        
                        // Apply hover effect
                        Color iconColor = _folderIconHover 
                            ? new Color(0, 200, 255) * pulseAlpha  // Brighter cyan on hover
                            : Color.White * pulseAlpha;
                        
                        // Draw icon with hover scale
                        float hoverScale = _folderIconHover ? 1.1f : 1.0f;
                        int scaledSize = (int)(iconSize * hoverScale);
                        int scaledX = iconX - (scaledSize - iconSize) / 2;
                        int scaledY = (int)(iconY - (scaledSize - iconSize) / 2);
                        
                        sb.Draw(_folderIcon, 
                            new Rectangle(scaledX, scaledY, scaledSize, scaledSize), 
                            iconColor);
                        
                        mainTextYOffset = iconY + iconSize + 10;
                        
                        // Hover hint - RESPONSIVE
                        if (_folderIconHover && (_font != null || _fontRenderer != null))
                        {
                            string hoverHint = "Click to browse files";
                            int hintSize = (int)(sh * 0.028f);  // Reducido de 0.042f a 0.028f
                            hintSize = Math.Clamp(hintSize, 16, 36);  // Ajustado rango
                            var hintMeasure = MeasureText(hoverHint, hintSize);
                            DrawText(sb, hoverHint,
                                new Vector2(dropBoxX + (dropBoxW - hintMeasure.X) / 2f, iconY - 30),
                                new Color(0, 200, 255) * pulseAlpha * 0.8f, hintSize);
                        }
                    }
                    else
                    {
                        // Fallback: text-based icon - RESPONSIVE
                        string iconText = "[ ]";
                        int iconSize = (int)(sh * 0.130f);  // Reducido de 0.194f a 0.130f
                        iconSize = Math.Clamp(iconSize, 60, 140);  // Ajustado rango
                        var iconMeasure = MeasureText(iconText, iconSize);
                        float iconX = dropBoxX + (dropBoxW - iconMeasure.X) / 2f;
                        
                        DrawText(sb, iconText, new Vector2(iconX, iconY), 
                            Color.White * pulseAlpha, iconSize);
                        
                        mainTextYOffset = iconY + iconMeasure.Y + 10;
                    }
                    
                    // Main text - RESPONSIVE
                    string mainText = GetEmptyStateMainTextInternal();
                    int mainSize = (int)(sh * 0.048f);  // Reducido de 0.072f a 0.048f
                    mainSize = Math.Clamp(mainSize, 26, 60);  // Ajustado rango
                    var mainMeasure = MeasureText(mainText, mainSize);
                    float mainX = dropBoxX + (dropBoxW - mainMeasure.X) / 2f;
                    
                    DrawText(sb, mainText, new Vector2(mainX, mainTextYOffset), 
                        new Color(0, 180, 255) * pulseAlpha, mainSize);
                    
                    // Secondary text - RESPONSIVE
                    string secondaryText = GetEmptyStateSecondaryTextInternal();
                    int secondSize = (int)(sh * 0.039f);  // Reducido de 0.058f a 0.039f
                    secondSize = Math.Clamp(secondSize, 20, 48);  // Ajustado rango
                    var secondMeasure = MeasureText(secondaryText, secondSize);
                    float secondX = dropBoxX + (dropBoxW - secondMeasure.X) / 2f;
                    float secondY = mainTextYOffset + mainMeasure.Y + 5;
                    
                    DrawText(sb, secondaryText, new Vector2(secondX, secondY), 
                        Color.White * 0.7f, secondSize);
                    
                    // Help text (bottom) - RESPONSIVE
                    string helpText = GetEmptyStateHelpTextInternal();
                    int helpSize = (int)(sh * 0.026f);  // Reducido de 0.039f a 0.026f
                    helpSize = Math.Clamp(helpSize, 16, 34);  // Ajustado rango
                    var helpMeasure = MeasureText(helpText, helpSize);
                    float helpX = dropBoxX + (dropBoxW - helpMeasure.X) / 2f;
                    float helpY = dropBoxY + dropBoxH - 40;
                    
                    DrawText(sb, helpText, new Vector2(helpX, helpY), 
                        Color.White * 0.4f, helpSize);
                }
                else
                {
                    // ── Enable scissor rectangle for level list clipping ──
                    Rectangle oldScissor = _graphicsDevice?.ScissorRectangle ?? default;
                    RasterizerState? oldRasterizer = _graphicsDevice?.RasterizerState;
                    
                    if (_graphicsDevice != null)
                    {
                        // End current batch
                        sb.End();
                        
                        // Set scissor rectangle to clip list area
                        Rectangle scissorRect = new Rectangle(panelLeft, listTop, sw - panelLeft, listH);
                        _graphicsDevice.ScissorRectangle = scissorRect;
                        
                        // Create rasterizer state with scissor test enabled
                        RasterizerState scissorRasterizer = new RasterizerState
                        {
                            CullMode = CullMode.None,
                            ScissorTestEnable = true
                        };
                        
                        // Begin new batch with scissor test (match Game1's LinearClamp sampler)
                        sb.Begin(
                            SpriteSortMode.Deferred,
                            BlendState.AlphaBlend,
                            SamplerState.LinearClamp,
                            DepthStencilState.None,
                            scissorRasterizer
                        );
                    }
                    
                    // Level list items with modern card design
                    for (int i = 0; i < _paths.Count; i++)
                    {
                        float itemY = listTop + i * ITEM_H - _scrollY;
                        if (itemY + ITEM_H < listTop || itemY > listTop + listH) continue;

                        bool sel = _inPanel && i == _selected;
                        
                        // Card background with subtle gradient
                        if (sel)
                        {
                            // Selected: vibrant gradient
                            Color cardTop = new Color(255, 68, 136) * 0.35f;
                            Color cardBottom = new Color(255, 68, 136) * 0.2f;
                            DrawRect(sb, panelLeft + 8, (int)itemY + 2, sw - panelLeft - 16, ITEM_H - 5, cardTop);
                            
                            // Glow effect
                            DrawRect(sb, panelLeft + 6, (int)itemY, sw - panelLeft - 12, 1, new Color(255, 215, 0) * 0.7f);
                            DrawRect(sb, panelLeft + 6, (int)itemY + ITEM_H - 4, sw - panelLeft - 12, 1, new Color(255, 68, 136) * 0.5f);
                            
                            // Left accent bar
                            DrawRect(sb, panelLeft + 8, (int)itemY + 2, 4, ITEM_H - 5, new Color(255, 215, 0));
                        }
                        else
                        {
                            // Not selected: subtle card
                            Color cardBg = (i % 2 == 0) ? new Color(28, 28, 45) * 0.5f : new Color(22, 22, 38) * 0.5f;
                            DrawRect(sb, panelLeft + 10, (int)itemY + 3, sw - panelLeft - 20, ITEM_H - 7, cardBg);
                            DrawRect(sb, panelLeft + 10, (int)itemY + 3, sw - panelLeft - 20, 1, Color.White * 0.05f);
                        }

                        // Track number badge - RESPONSIVE
                        string idx = $"{i + 1:D2}";
                        int badgeX = panelLeft + (sel ? 18 : 16);
                        int badgeY = (int)(itemY + ITEM_H / 2f - 12);
                        int badgeSize = 24;
                        
                        // Badge circle/square
                        DrawRect(sb, badgeX, badgeY, badgeSize, badgeSize, new Color(255, 68, 136) * (sel ? 0.4f : 0.2f));
                        DrawRect(sb, badgeX, badgeY, 2, badgeSize, new Color(255, 215, 0) * (sel ? 0.8f : 0.3f));
                        
                        int badgeNumSize = (int)(sh * 0.026f);  // Reducido de 0.039f a 0.026f
                        badgeNumSize = Math.Clamp(badgeNumSize, 16, 34);  // Ajustado rango
                        DrawText(sb, idx, new Vector2(badgeX + 6, badgeY + 4),
                            sel ? new Color(255, 215, 0) : Color.White * 0.5f, badgeNumSize);

                        // Song name - RESPONSIVE
                        float nameX = panelLeft + (sel ? 52 : 50);
                        float nameY = itemY + ITEM_H / 2f - 10;
                        Color nameColor = sel ? Color.White : new Color(200, 200, 220) * 0.8f;
                        int nameSize = (int)(sh * 0.032f);  // Reducido de 0.047f a 0.032f
                        nameSize = Math.Clamp(nameSize, 20, 42);  // Ajustado rango
                        if (sel) nameSize += 2;  // Un poco más grande si está seleccionado
                        
                        DrawText(sb, Safe(_names[i]), new Vector2(nameX, nameY), nameColor, nameSize);
                        
                        // Play icon on hover/selected - RESPONSIVE
                        if (sel)
                        {
                            string playIcon = ">";
                            int playIconSize = (int)(sh * 0.036f);  // Reducido de 0.053f a 0.036f
                            playIconSize = Math.Clamp(playIconSize, 22, 46);  // Ajustado rango
                            DrawText(sb, playIcon, new Vector2(sw - 35, nameY - 2),
                                new Color(255, 215, 0) * 0.8f, playIconSize);
                        }
                    }

                    // ── Disable scissor rectangle and restore state ──
                    if (_graphicsDevice != null)
                    {
                        // End scissor batch
                        sb.End();
                        
                        // Restore original rasterizer state
                        _graphicsDevice.RasterizerState = oldRasterizer;
                        _graphicsDevice.ScissorRectangle = oldScissor;
                        
                        // Begin normal batch again (match Game1's LinearClamp sampler)
                        sb.Begin(
                            SpriteSortMode.Deferred,
                            BlendState.AlphaBlend,
                            SamplerState.LinearClamp,
                            DepthStencilState.None,
                            RasterizerState.CullNone
                        );
                    }

                    // Modern scrollbar
                    float totalH = _paths.Count * ITEM_H;
                    if (totalH > listH)
                    {
                        float barH = Math.Max(32f, listH * listH / totalH);
                        float barY = listTop + (_scrollY / (totalH - listH)) * (listH - barH);
                        
                        // Scrollbar track
                        DrawRect(sb, sw - 12, listTop, 8, listH, new Color(20, 20, 30) * 0.5f);
                        
                        // Scrollbar thumb with gradient
                        DrawRect(sb, sw - 11, (int)barY, 6, (int)barH, new Color(255, 68, 136) * 0.6f);
                        DrawRect(sb, sw - 11, (int)barY, 2, (int)barH, new Color(255, 215, 0) * 0.4f);
                    }
                }

                // Modern footer at bottom of panel - RESPONSIVE
                DrawRect(sb, panelLeft, sh - 50, sw - panelLeft, 50, new Color(20, 15, 25) * 0.9f);
                DrawRect(sb, panelLeft, sh - 50, sw - panelLeft, 2, new Color(255, 68, 136) * 0.4f);
                string drop = GetPanelBottomHintTextInternal();
                int dropSize = (int)(sh * 0.023f);  // Reducido de 0.036f a 0.023f
                dropSize = Math.Clamp(dropSize, 14, 28);  // Ajustado rango
                var dsz = MeasureText(drop, dropSize);
                DrawText(sb, drop,
                    new Vector2(panelLeft + (sw - panelLeft) / 2f - dsz.X / 2f, sh - 32),
                    new Color(255, 215, 0) * 0.5f, dropSize);
            }
        }

        // ── Credits overlay (fade in) ─────────────────────────────────────────
        float credAlpha = _creditsAlpha;
        if (_transition == TransitionState.CreditsIn)
            credAlpha = Math.Min(1f, _transTimer / TRANS_DURATION);

        if (credAlpha > 0.01f && (_font != null || _fontRenderer != null))
        {
            DrawRect(sb, 0, 0, sw, sh, new Color(10, 10, 18) * credAlpha);
            
            // Clear previous frame's links
            _creditLinks.Clear();
            
            // Credit lines with URLs
            var creditData = new (string text, string? url)[] {
                ("Funky Maker Player", null),
                ("", null),
                ("Developed by  @thisisankush", null),
                ("", null),
                ("Funky Maker is a game by  KingamesCreator", null),
                ("  Android - Google Play", "https://play.google.com/store/apps/details?id=com.kingamescreator.fnmm"),
                ("  iOS - App Store", "https://apps.apple.com/app/funky-maker-mobile/id6761866418"),
                ("", null),
                ("Built with MonoGame  NVorbis  ImageSharp  SharpCompress", null),
                ("", null),
                ("UI Icons by Icons8 - icons8.com", "https://icons8.com"),
                ("", null),
                ("[Enter] or [ESC] to close", null),
            };
            
            int creditSize = (int)(sh * 0.032f);  // Reducido de 0.050f a 0.032f - RESPONSIVE
            creditSize = Math.Clamp(creditSize, 20, 40);  // Ajustado rango
            int ch = (int)(MeasureText("A", creditSize).Y) + 6;
            int totalCH = creditData.Length * ch;
            int cy = sh / 2 - totalCH / 2;
            
            var mouse = Mouse.GetState();
            // Transform mouse coordinates from window to render target space
            Point transformedMouse = TransformMouseToRenderTarget(mouse.Position, _lastWindowWidth, _lastWindowHeight, _lastScreenWidth, _lastScreenHeight);
            
            foreach (var (text, url) in creditData)
            {
                if (text.Length == 0) { cy += ch; continue; }
                
                var sz = MeasureText(text, creditSize);
                float textX = sw / 2f - sz.X / 2f;
                
                Color col;
                bool isTitle = text == "Funky Maker Player";
                bool isIndented = text.StartsWith("  ");
                bool isLink = url != null;
                
                // Create bounds for clickable links (in render target space)
                Rectangle bounds = new Rectangle((int)textX, cy, (int)sz.X, (int)sz.Y);
                bool isHovered = isLink && bounds.Contains(transformedMouse);
                
                if (isLink)
                {
                    _creditLinks.Add(new ClickableLink 
                    { 
                        Bounds = bounds, 
                        Url = url ?? string.Empty,
                        IsHovered = isHovered
                    });
                }
                
                // Color logic
                if (isTitle)
                    col = new Color(255, 215, 0) * credAlpha;  // Gold
                else if (isLink && isHovered)
                    col = new Color(255, 215, 0) * credAlpha;  // Gold on hover
                else if (isLink)
                    col = new Color(0, 180, 255) * credAlpha;  // Cyan for links
                else if (isIndented)
                    col = Color.White * 0.5f * credAlpha;
                else
                    col = Color.White * 0.85f * credAlpha;
                
                // Underline for links
                if (isLink)
                {
                    int underlineY = cy + (int)sz.Y + 2;
                    DrawRect(sb, (int)textX, underlineY, (int)sz.X, 1, col * 0.6f);
                }
                
                DrawText(sb, text, new Vector2(textX, cy), col, creditSize);
                cy += ch;
            }
            
            // Handle clicks on credit links (must be in Draw() after bounds are calculated)
            var currentMouse = Mouse.GetState();
            if (currentMouse.LeftButton == ButtonState.Pressed && _prevMs.LeftButton == ButtonState.Released)
            {
                Point clickPos = TransformMouseToRenderTarget(currentMouse.Position, _lastWindowWidth, _lastWindowHeight, _lastScreenWidth, _lastScreenHeight);
                
                foreach (var link in _creditLinks)
                {
                    if (link.Bounds.Contains(clickPos))
                    {
                        // Open URL
                        try
                        {
                            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
                            {
                                FileName = link.Url,
                                UseShellExecute = true
                            });
                            Console.WriteLine($"[Credits] Opening URL: {link.Url}");
                        }
                        catch (Exception ex)
                        {
                            Console.WriteLine($"[Credits] Failed to open URL: {ex.Message}");
                        }
                        break;
                    }
                }
            }
        }
    }

    // ── Helpers ──────────────────────────────────────────────────────────────
    private void DrawRect(SpriteBatch sb, int x, int y, int w, int h, Color c)
    {
        if (w <= 0 || h <= 0) return;
        sb.Draw(_pixel, new Rectangle(x, y, w, h), c);
    }
    
    /// <summary>
    /// Draw text using FontStashSharp if available, fallback to SpriteFont.
    /// </summary>
    private void DrawText(SpriteBatch sb, string text, Vector2 position, Color color, int fontSize)
    {
        if (_fontRenderer != null)
        {
            _fontRenderer.DrawString(sb, text, position, color, fontSize);
        }
        else if (_font != null)
        {
            // Fallback to bitmap font with scale
            float scale = fontSize / 32f;  // Assuming base font is ~32px
            sb.DrawString(_font, text, position, color, 0f, Vector2.Zero, scale, SpriteEffects.None, 0f);
        }
    }
    
    /// <summary>
    /// Measure text using FontStashSharp if available, fallback to SpriteFont.
    /// </summary>
    private Vector2 MeasureText(string text, int fontSize)
    {
        if (_fontRenderer != null)
        {
            return _fontRenderer.MeasureString(text, fontSize);
        }
        else if (_font != null)
        {
            float scale = fontSize / 32f;
            return _font.MeasureString(text) * scale;
        }
        return Vector2.Zero;
    }

    private string Safe(string s)
    {
        if (string.IsNullOrEmpty(s) || _font == null) return s ?? "";
        var sb = new System.Text.StringBuilder(s.Length);
        foreach (char c in s)
            sb.Append(c >= 32 && c < 127 ? c : '?');
        return sb.ToString();
    }

    public static string GetDefaultLevelsFolder() =>
        Path.Combine(AppContext.BaseDirectory, "mods", "levels");
    
    // ── Platform-specific menu item state ────────────────────────────────────
    /// <summary>
    /// Check if a menu item is enabled. Defaults to enabled on all platforms.
    /// Blazor override disables HUD Editor and Multiplayer.
    /// </summary>
    private bool IsMenuItemEnabledInternal(int index)
    {
        #if BLAZORGL
        return IsMenuItemEnabled(index);
        #else
        return true;  // All items enabled on desktop
        #endif
    }
    
    /// <summary>
    /// Get color for menu item. Defaults to standard colors on all platforms.
    /// Blazor override shows disabled items in dark red.
    /// </summary>
    private Color GetMenuItemColorInternal(int index, bool isSelected)
    {
        #if BLAZORGL
        return GetMenuItemColor(index, isSelected);
        #else
        // Desktop: standard colors
        return isSelected ? new Color(0xFF, 0x44, 0x88) : Color.White * 0.55f;
        #endif
    }
    
    /// <summary>
    /// Get main text for empty state (no levels found).
    /// </summary>
    private string GetEmptyStateMainTextInternal()
    {
        #if BLAZORGL
        return GetEmptyStateMainText();
        #else
        return "DRAG & DROP";
        #endif
    }
    
    /// <summary>
    /// Get secondary text for empty state.
    /// </summary>
    private string GetEmptyStateSecondaryTextInternal()
    {
        #if BLAZORGL
        return GetEmptyStateSecondaryText();
        #else
        return ".FNMM FILES HERE";
        #endif
    }
    
    /// <summary>
    /// Get help text for empty state.
    /// </summary>
    private string GetEmptyStateHelpTextInternal()
    {
        #if BLAZORGL
        return GetEmptyStateHelpText();
        #else
        return "Or copy files to 'levels/' folder";
        #endif
    }
    
    /// <summary>
    /// Get bottom panel hint text.
    /// </summary>
    private string GetPanelBottomHintTextInternal()
    {
        #if BLAZORGL
        return GetPanelBottomHintText();
        #else
        return "Drop a .FNMM here to add it";
        #endif
    }
    
    /// <summary>
    /// Get the bounds of the folder icon for click detection.
    /// </summary>
    private Rectangle GetFolderIconBounds(int screenWidth, int screenHeight)
    {
        if (_folderIcon == null) return Rectangle.Empty;
        
        int panelLeft = (int)(screenWidth - PANEL_W * (1f - _panelX));
        
        // Icon size and position (centered in drop zone)
        int iconSize = 128;  // Scale icon to 128x128
        int dropBoxW = 380;
        int dropBoxH = 280;
        int dropBoxX = panelLeft + (screenWidth - panelLeft - dropBoxW) / 2;
        int dropBoxY = screenHeight / 2 - dropBoxH / 2;
        
        int iconX = dropBoxX + (dropBoxW - iconSize) / 2;
        int iconY = dropBoxY + 50;
        
        return new Rectangle(iconX, iconY, iconSize, iconSize);
    }
    
    /// <summary>
    /// Transform mouse coordinates from window space to render target space.
    /// The game renders to a fixed 1280x720 (or 1920x1080) render target, then scales to window.
    /// </summary>
    private Point TransformMouseToRenderTarget(Point mousePos, int windowWidth, int windowHeight, int rtWidth, int rtHeight)
    {
        // Calculate how the render target is displayed in the window
        float windowAspect = windowWidth / (float)windowHeight;
        float rtAspect = rtWidth / (float)rtHeight;
        
        int dstW, dstH, offsetX, offsetY;
        
        if (windowAspect > rtAspect)
        {
            // Window is wider - letterbox on sides
            dstH = windowHeight;
            dstW = (int)(rtAspect * dstH);
            offsetX = (windowWidth - dstW) / 2;
            offsetY = 0;
        }
        else
        {
            // Window is taller - letterbox on top/bottom
            dstW = windowWidth;
            dstH = (int)(dstW / rtAspect);
            offsetX = 0;
            offsetY = (windowHeight - dstH) / 2;
        }
        
        // Transform mouse from window space to render target space
        int rtX = (int)((mousePos.X - offsetX) * rtWidth / (float)dstW);
        int rtY = (int)((mousePos.Y - offsetY) * rtHeight / (float)dstH);
        
        return new Point(rtX, rtY);
    }
    
    /// <summary>
    /// Opens Windows folder browser to select .FNMM files.
    /// </summary>
    private void OpenFolderBrowser()
    {
        #if !BLAZORGL && WINDOWS
        try
        {
            var dialog = new System.Windows.Forms.OpenFileDialog
            {
                Filter = "Funky Maker Levels (*.FNMM;*.fnmm;*.rar)|*.FNMM;*.fnmm;*.rar|All files (*.*)|*.*",
                Multiselect = true,
                Title = "Select Funky Maker level files"
            };
            
            if (dialog.ShowDialog() == System.Windows.Forms.DialogResult.OK)
            {
                foreach (var file in dialog.FileNames)
                {
                    string destPath = Path.Combine(_levelsFolder, Path.GetFileName(file));
                    
                    // Copy file to levels folder if not already there
                    if (!File.Exists(destPath))
                    {
                        Directory.CreateDirectory(_levelsFolder);
                        File.Copy(file, destPath);
                    }
                }
                
                Refresh();  // Reload level list
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[FreeplayMenu] Error opening file dialog: {ex.Message}");
        }
        #endif
    }
}
