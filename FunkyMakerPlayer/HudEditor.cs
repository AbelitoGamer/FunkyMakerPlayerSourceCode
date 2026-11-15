using System;
using System.Collections.Generic;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using Microsoft.Xna.Framework.Input;

namespace FunkyMakerPlayer;

/// <summary>
/// Interactive HUD editor that allows dragging and positioning HUD elements visually.
/// </summary>
public class HudEditor
{
    private readonly Texture2D _pixel;
    private readonly SpriteFont _font;
    
    private GraphicsDevice? _graphicsDevice;
    
    private KeyboardState _prevKb;
    private MouseState _prevMouse;
    
    private readonly List<HudElement> _elements = new();
    private int _selectedElement = -1;
    private bool _isDragging = false;
    private Point _dragOffset;
    
    private bool _showGrid = true;
    private const int GRID_SIZE = 20;
    
    // Toggle instructions panel visibility
    private bool _showInstructions = true;
    
    // Save/Reset feedback messages
    private string? _feedbackMessage = null;
    private float _feedbackTimer = 0f;
    private const float FEEDBACK_DURATION = 2f;
    
    public bool BackPressed { get; private set; }
    public bool SavePressed { get; private set; }

    public HudEditor(Texture2D pixel, SpriteFont font, int screenWidth, int screenHeight, GraphicsDevice graphicsDevice)
    {
        _pixel = pixel;
        _font = font;
        _graphicsDevice = graphicsDevice;
        _prevKb = Keyboard.GetState();
        _prevMouse = Mouse.GetState();
        
        // Load actual positions from HudSettings
        LoadFromHudSettings(screenWidth, screenHeight);
    }
    
    private void LoadFromHudSettings(int screenWidth, int screenHeight)
    {
        _elements.Clear();
        
        // Get current HUD settings (don't adjust for downscroll here - we edit the base values)
        var hudSettings = HudSettings.Instance;
        
        // Health Bar
        _elements.Add(new HudElement
        {
            Name = "Health Bar",
            Position = hudSettings.HealthBarPosition.ToPoint(),
            Size = hudSettings.HealthBarSize.ToPoint(),
            Color = Color.Red,
            Type = HudElementType.HealthBar,
            Visible = hudSettings.HealthBarVisible
        });
        
        // Score
        _elements.Add(new HudElement
        {
            Name = "Score",
            Position = hudSettings.ScorePosition.ToPoint(),
            Size = new Point(140, 30),
            Color = Color.White,
            Type = HudElementType.Text,
            Visible = hudSettings.ScoreVisible
        });
        
        // Misses
        _elements.Add(new HudElement
        {
            Name = "Misses",
            Position = hudSettings.MissesPosition.ToPoint(),
            Size = new Point(100, 30),
            Color = Color.OrangeRed,
            Type = HudElementType.Text,
            Visible = hudSettings.MissesVisible
        });
        
        // Accuracy
        _elements.Add(new HudElement
        {
            Name = "Accuracy",
            Position = hudSettings.AccuracyPosition.ToPoint(),
            Size = new Point(120, 30),
            Color = Color.LightBlue,
            Type = HudElementType.Text,
            Visible = hudSettings.AccuracyVisible
        });
        
        // Timer Bar
        _elements.Add(new HudElement
        {
            Name = "Timer Bar",
            Position = hudSettings.TimerBarPosition.ToPoint(),
            Size = hudSettings.TimerBarSize.ToPoint(),
            Color = Color.Cyan,
            Type = HudElementType.HealthBar,
            Visible = hudSettings.TimerBarVisible
        });
        
        // Song Info
        _elements.Add(new HudElement
        {
            Name = "Song Info",
            Position = hudSettings.SongInfoPosition.ToPoint(),
            Size = new Point(200, 30),
            Color = Color.White,
            Type = HudElementType.Text,
            Visible = hudSettings.SongInfoVisible
        });

        // Status Info (bottom-left: Autoplay / Scroll / Speed)
        _elements.Add(new HudElement
        {
            Name = "Status Info",
            Position = hudSettings.StatusInfoPosition.ToPoint(),
            Size = new Point(260, 28),
            Color = Color.LightGreen,
            Type = HudElementType.Text,
            Visible = hudSettings.StatusInfoVisible
        });
        
        // Combo Counter
        _elements.Add(new HudElement
        {
            Name = "Combo",
            Position = hudSettings.ComboPosition.ToPoint(),
            Size = new Point(100, 40),
            Color = Color.Yellow,
            Type = HudElementType.Text,
            Visible = hudSettings.ComboVisible
        });
        
        // Judgement Text
        _elements.Add(new HudElement
        {
            Name = "Judgement",
            Position = hudSettings.JudgementPosition.ToPoint(),
            Size = new Point(200, 50),
            Color = Color.Cyan,
            Type = HudElementType.Text,
            Visible = hudSettings.JudgementVisible
        });
        
        // Opponent Icon (for Fixed at Ends mode)
        _elements.Add(new HudElement
        {
            Name = "Opponent Icon",
            Position = hudSettings.OpponentIconPosition.ToPoint(),
            Size = new Point(100, 100),
            Color = Color.Red,
            Type = HudElementType.Icon,
            Visible = hudSettings.OpponentIconVisible
        });
        
        // Player Icon (for Fixed at Ends mode)
        _elements.Add(new HudElement
        {
            Name = "Player Icon",
            Position = hudSettings.PlayerIconPosition.ToPoint(),
            Size = new Point(100, 100),
            Color = Color.Cyan,
            Type = HudElementType.Icon,
            Visible = hudSettings.PlayerIconVisible
        });

        // Opponent Strumline (left side)
        _elements.Add(new HudElement
        {
            Name = "Opponent Strumline",
            Position = new Point(100, 60),
            Size = new Point(440, 90),
            Color = new Color(194, 75, 153),
            Type = HudElementType.Strumline,
            Visible = hudSettings.StrumlinesVisible && hudSettings.OpponentStrumlineVisible
        });

        // Player Strumline (right side)
        _elements.Add(new HudElement
        {
            Name = "Player Strumline",
            Position = new Point(740, 60),
            Size = new Point(440, 90),
            Color = new Color(0, 255, 255),
            Type = HudElementType.Strumline,
            Visible = hudSettings.StrumlinesVisible && hudSettings.PlayerStrumlineVisible
        });
    }
    
    /// <summary>
    /// Converts mouse position from window coordinates to render target coordinates,
    /// accounting for letterboxing/scaling.
    /// </summary>
    private Point ConvertMousePosition(int mouseX, int mouseY, int rtWidth, int rtHeight)
    {
        if (_graphicsDevice == null)
            return new Point(mouseX, mouseY);
        
        // Get actual window size
        int winW = _graphicsDevice.PresentationParameters.BackBufferWidth;
        int winH = _graphicsDevice.PresentationParameters.BackBufferHeight;
        
        // Calculate letterbox scaling (same as BlitToScreen in Game1.cs)
        float scaleX = (float)winW / rtWidth;
        float scaleY = (float)winH / rtHeight;
        float scale = Math.Min(scaleX, scaleY);
        
        int dstW = (int)(rtWidth * scale);
        int dstH = (int)(rtHeight * scale);
        int dstX = (winW - dstW) / 2;
        int dstY = (winH - dstH) / 2;
        
        // Convert mouse from window space to render target space
        int rtX = (int)((mouseX - dstX) / scale);
        int rtY = (int)((mouseY - dstY) / scale);
        
        // Clamp to render target bounds
        rtX = Math.Clamp(rtX, 0, rtWidth);
        rtY = Math.Clamp(rtY, 0, rtHeight);
        
        return new Point(rtX, rtY);
    }

    public void SyncInputState()
    {
        _isDragging = false;
        _prevKb = Keyboard.GetState();
        _prevMouse = Mouse.GetState();
    }

    public void Update(float dt, int screenWidth, int screenHeight)
    {
        // Update feedback timer
        if (_feedbackTimer > 0f)
        {
            _feedbackTimer -= dt;
            if (_feedbackTimer <= 0f)
            {
                _feedbackMessage = null;
            }
        }

        BackPressed = false;
        SavePressed = false;

        var mouse = Mouse.GetState();
        if (!Game1.CanAcceptInput)
        {
            _isDragging = false;
            _prevKb = default;
            _prevMouse = Game1.NeutralMouseState(mouse);
            return;
        }

        var kb = Keyboard.GetState();
        
        // Convert mouse position from window coordinates to render target coordinates
        Point mousePos = ConvertMousePosition(mouse.X, mouse.Y, screenWidth, screenHeight);
        
        // ESC to go back (auto-save layout so changes take effect immediately)
        if (kb.IsKeyDown(Keys.Escape) && !_prevKb.IsKeyDown(Keys.Escape))
        {
            SaveHudLayout();
            BackPressed = true;
        }
        
        // S to save
        if (kb.IsKeyDown(Keys.S) && !_prevKb.IsKeyDown(Keys.S))
        {
            SavePressed = true;
            SaveHudLayout();
            _feedbackMessage = "Layout saved to hud_settings.json!";
            _feedbackTimer = FEEDBACK_DURATION;
        }
        
        // R to reset to defaults
        if (kb.IsKeyDown(Keys.R) && !_prevKb.IsKeyDown(Keys.R))
        {
            HudSettings.Instance.ResetToDefaults();
            HudSettings.Instance.Save();
            LoadFromHudSettings(screenWidth, screenHeight);
            _feedbackMessage = "Reset to default positions & visibility!";
            _feedbackTimer = FEEDBACK_DURATION;
        }
        
        // G to toggle grid
        if (kb.IsKeyDown(Keys.G) && !_prevKb.IsKeyDown(Keys.G))
        {
            _showGrid = !_showGrid;
        }
        
        // H to toggle instructions panel
        if (kb.IsKeyDown(Keys.H) && !_prevKb.IsKeyDown(Keys.H))
        {
            _showInstructions = !_showInstructions;
        }

        // A to toggle visibility of ALL elements at once (master toggle)
        if (kb.IsKeyDown(Keys.A) && !_prevKb.IsKeyDown(Keys.A))
        {
            bool anyVisible = _elements.Exists(e => e.Visible);
            bool nextState = !anyVisible;
            foreach (var elem in _elements)
                elem.Visible = nextState;
            HudSettings.Instance.MasterHudVisible = nextState;
            SyncToHudSettings();
            _feedbackMessage = $"All HUD Elements: {(nextState ? "VISIBLE" : "HIDDEN")}";
            _feedbackTimer = FEEDBACK_DURATION;
        }

        // V, Space, or Delete to toggle visibility of selected element
        bool toggleVisKey = (kb.IsKeyDown(Keys.V) && !_prevKb.IsKeyDown(Keys.V)) ||
                            (kb.IsKeyDown(Keys.Space) && !_prevKb.IsKeyDown(Keys.Space)) ||
                            (kb.IsKeyDown(Keys.Delete) && !_prevKb.IsKeyDown(Keys.Delete));
        if (toggleVisKey && _selectedElement >= 0 && _selectedElement < _elements.Count)
        {
            var elem = _elements[_selectedElement];
            elem.Visible = !elem.Visible;
            SyncToHudSettings();
            _feedbackMessage = $"{elem.Name}: {(elem.Visible ? "VISIBLE" : "HIDDEN")}";
            _feedbackTimer = FEEDBACK_DURATION;
        }
        
        // Right-click on an element to select and toggle its visibility
        if (mouse.RightButton == ButtonState.Pressed && _prevMouse.RightButton == ButtonState.Released)
        {
            for (int i = _elements.Count - 1; i >= 0; i--)
            {
                var elem = _elements[i];
                var rect = new Rectangle(elem.Position, elem.Size);
                if (rect.Contains(mousePos))
                {
                    _selectedElement = i;
                    elem.Visible = !elem.Visible;
                    SyncToHudSettings();
                    _feedbackMessage = $"{elem.Name}: {(elem.Visible ? "VISIBLE" : "HIDDEN")}";
                    _feedbackTimer = FEEDBACK_DURATION;
                    break;
                }
            }
        }
        
        // Mouse interaction (left-click drag)
        if (mouse.LeftButton == ButtonState.Pressed && _prevMouse.LeftButton == ButtonState.Released)
        {
            // Start dragging
            for (int i = _elements.Count - 1; i >= 0; i--)
            {
                var elem = _elements[i];
                var rect = new Rectangle(elem.Position, elem.Size);
                
                if (rect.Contains(mousePos))
                {
                    _selectedElement = i;
                    _isDragging = true;
                    _dragOffset = new Point(mousePos.X - elem.Position.X, mousePos.Y - elem.Position.Y);
                    break;
                }
            }
        }
        
        if (mouse.LeftButton == ButtonState.Released && _prevMouse.LeftButton == ButtonState.Pressed)
        {
            // Stop dragging
            _isDragging = false;
            SyncToHudSettings();
        }
        
        // Drag selected element
        if (_isDragging && _selectedElement >= 0 && _selectedElement < _elements.Count)
        {
            var elem = _elements[_selectedElement];
            elem.Position = new Point(mousePos.X - _dragOffset.X, mousePos.Y - _dragOffset.Y);
            
            // Snap to grid if enabled
            if (_showGrid)
            {
                elem.Position = new Point(
                    (elem.Position.X / GRID_SIZE) * GRID_SIZE,
                    (elem.Position.Y / GRID_SIZE) * GRID_SIZE
                );
            }
            
            // Keep within bounds
            elem.Position = new Point(
                Math.Clamp(elem.Position.X, 0, screenWidth - elem.Size.X),
                Math.Clamp(elem.Position.Y, 0, screenHeight - elem.Size.Y)
            );
        }
        
        // Keyboard movement for selected element
        if (_selectedElement >= 0 && _selectedElement < _elements.Count && !_isDragging)
        {
            var elem = _elements[_selectedElement];
            int moveSpeed = kb.IsKeyDown(Keys.LeftShift) ? 1 : 5;
            bool moved = false;
            
            if (kb.IsKeyDown(Keys.Up))
            {
                elem.Position = new Point(elem.Position.X, Math.Max(0, elem.Position.Y - moveSpeed));
                moved = true;
            }
            if (kb.IsKeyDown(Keys.Down))
            {
                elem.Position = new Point(elem.Position.X, Math.Min(screenHeight - elem.Size.Y, elem.Position.Y + moveSpeed));
                moved = true;
            }
            if (kb.IsKeyDown(Keys.Left))
            {
                elem.Position = new Point(Math.Max(0, elem.Position.X - moveSpeed), elem.Position.Y);
                moved = true;
            }
            if (kb.IsKeyDown(Keys.Right))
            {
                elem.Position = new Point(Math.Min(screenWidth - elem.Size.X, elem.Position.X + moveSpeed), elem.Position.Y);
                moved = true;
            }
            if (moved)
                SyncToHudSettings();
        }
        
        // Tab to cycle selection
        if (kb.IsKeyDown(Keys.Tab) && !_prevKb.IsKeyDown(Keys.Tab))
        {
            _selectedElement = (_selectedElement + 1) % _elements.Count;
        }
        
        _prevKb = kb;
        _prevMouse = mouse;
    }

    public void Draw(SpriteBatch sb, int screenWidth, int screenHeight, bool hasLiveBackground = false)
    {
        if (hasLiveBackground)
        {
            // Subtle dark overlay over the live paused level so HUD boxes stand out clearly
            sb.Draw(_pixel, new Rectangle(0, 0, screenWidth, screenHeight), Color.Black * 0.45f);
        }
        else
        {
            // Dark background (like gameplay)
            sb.Draw(_pixel, new Rectangle(0, 0, screenWidth, screenHeight), new Color(20, 20, 30));
            DrawGameplayPreview(sb, screenWidth, screenHeight);
        }
        
        // Grid
        if (_showGrid)
        {
            DrawGrid(sb, screenWidth, screenHeight);
        }
        
        // Draw all HUD elements
        for (int i = 0; i < _elements.Count; i++)
        {
            var elem = _elements[i];
            bool isSelected = i == _selectedElement;
            
            DrawHudElement(sb, elem, isSelected);
        }
        
        // Instructions panel (toggle with H)
        if (_showInstructions)
        {
            DrawInstructionsPanel(sb, screenWidth, screenHeight);
        }
        
        // Feedback message (save/reset confirmation)
        if (_feedbackMessage != null && _feedbackTimer > 0f)
        {
            DrawFeedbackMessage(sb, screenWidth, screenHeight);
        }
    }
    
    private void DrawGameplayPreview(SpriteBatch sb, int screenWidth, int screenHeight)
    {
        // Draw a simple representation of the strumline area
        // This gives context for where HUD elements will appear relative to gameplay
        
        // Strumline area (simplified - just draw receptor positions)
        int strumY = (int)(screenHeight * 0.15f); // Top ~15% of screen
        int[] laneXPositions = new int[4];
        int centerX = screenWidth / 2;
        int laneSpacing = 160;
        int startX = centerX - (laneSpacing * 3 / 2);
        
        for (int i = 0; i < 4; i++)
        {
            laneXPositions[i] = startX + (i * laneSpacing);
            
            // Draw receptor placeholder (small square)
            int receptorSize = 100;
            int rx = laneXPositions[i] - receptorSize / 2;
            int ry = strumY - receptorSize / 2;
            
            // Receptor background (semi-transparent)
            sb.Draw(_pixel, new Rectangle(rx, ry, receptorSize, receptorSize), 
                Color.White * 0.1f);
            
            // Receptor border
            DrawRectangleBorder(sb, rx, ry, receptorSize, receptorSize, 
                Color.White * 0.3f, 2);
        }
        
        // Draw a label
        string label = "Strumline Preview";
        var labelSize = _font.MeasureString(label);
        sb.DrawString(_font, label, 
            new Vector2(centerX - labelSize.X / 2, strumY - 80), 
            Color.White * 0.5f);
    }
    
    private void DrawGrid(SpriteBatch sb, int screenWidth, int screenHeight)
    {
        Color gridColor = Color.White * 0.1f;
        
        // Vertical lines
        for (int x = 0; x < screenWidth; x += GRID_SIZE)
        {
            sb.Draw(_pixel, new Rectangle(x, 0, 1, screenHeight), gridColor);
        }
        
        // Horizontal lines
        for (int y = 0; y < screenHeight; y += GRID_SIZE)
        {
            sb.Draw(_pixel, new Rectangle(0, y, screenWidth, 1), gridColor);
        }
    }
    
    private void DrawHudElement(SpriteBatch sb, HudElement elem, bool isSelected)
    {
        var rect = new Rectangle(elem.Position, elem.Size);
        
        // Element background
        Color bgColor;
        if (!elem.Visible)
        {
            bgColor = isSelected ? new Color(120, 60, 60) * 0.45f : Color.Black * 0.35f;
        }
        else
        {
            bgColor = isSelected ? Color.Yellow * 0.5f : elem.Color * 0.3f;
        }
        
        sb.Draw(_pixel, rect, bgColor);
        
        // Border
        Color borderColor;
        if (!elem.Visible)
            borderColor = isSelected ? new Color(255, 120, 100) : Color.Gray * 0.45f;
        else
            borderColor = isSelected ? Color.Yellow : Color.White * 0.7f;

        int borderThickness = isSelected ? 3 : 2;
        DrawRectangleBorder(sb, rect.X, rect.Y, rect.Width, rect.Height, borderColor, borderThickness);
        
        // Label
        float labelScale = 0.8f;
        string displayText = elem.Visible ? elem.Name : $"{elem.Name} [HIDDEN]";
        var labelSize = _font.MeasureString(displayText) * labelScale;
        Vector2 labelPos = new Vector2(
            rect.X + rect.Width / 2 - labelSize.X / 2,
            rect.Y + rect.Height / 2 - labelSize.Y / 2
        );
        
        Color textColor = elem.Visible ? Color.White : new Color(180, 140, 140);
        // Shadow
        sb.DrawString(_font, displayText, labelPos + new Vector2(1, 1), Color.Black, 0f, Vector2.Zero, labelScale, Microsoft.Xna.Framework.Graphics.SpriteEffects.None, 0f);
        // Text
        sb.DrawString(_font, displayText, labelPos, textColor, 0f, Vector2.Zero, labelScale, Microsoft.Xna.Framework.Graphics.SpriteEffects.None, 0f);
        
        // Position & visibility indicator when selected
        if (isSelected)
        {
            string visTag = elem.Visible ? "VISIBLE" : "HIDDEN";
            string posText = $"({elem.Position.X}, {elem.Position.Y}) [{visTag}]";
            var posSize = _font.MeasureString(posText);
            Vector2 posTextPos = new Vector2(rect.X, Math.Max(4, rect.Y - posSize.Y - 5));
            sb.DrawString(_font, posText, posTextPos + new Vector2(1, 1), Color.Black);
            sb.DrawString(_font, posText, posTextPos, elem.Visible ? Color.Yellow : new Color(255, 120, 100));
            
            string sizeNote = "V / Right-Click: Hide/Show";
            Vector2 sizeTextPos = new Vector2(rect.X, Math.Min(700, rect.Y + rect.Height + 5));
            sb.DrawString(_font, sizeNote, sizeTextPos + new Vector2(1, 1), Color.Black, 0f, Vector2.Zero, 0.7f, Microsoft.Xna.Framework.Graphics.SpriteEffects.None, 0f);
            sb.DrawString(_font, sizeNote, sizeTextPos, Color.Cyan, 0f, Vector2.Zero, 0.7f, Microsoft.Xna.Framework.Graphics.SpriteEffects.None, 0f);
        }
    }
    
    private void DrawInstructionsPanel(SpriteBatch sb, int screenWidth, int screenHeight)
    {
        // Instructions
        string[] instructions = new[]
        {
            "Left-Click & Drag: Move elements",
            "Right-Click / V / Space: Hide or Show element",
            "A: Hide or Show ALL elements at once",
            "Arrow Keys (+Shift): Fine-tune position",
            "Tab: Cycle through elements",
            "G: Toggle grid   |   H: Toggle instructions",
            "R: Reset positions & visibility",
            "S: Save layout",
            "ESC: Save & Exit editor"
        };

        int lineStep = Math.Max(20, (int)Math.Ceiling(_font.MeasureString("Ag").Y) + 3);
        int panelW = 500;
        int panelH = 44 + instructions.Length * lineStep + 10;
        int panelX = screenWidth - panelW - 20;
        int panelY = 20;

        // Panel background
        sb.Draw(_pixel, new Rectangle(panelX, panelY, panelW, panelH), Color.Black * 0.85f);
        DrawRectangleBorder(sb, panelX, panelY, panelW, panelH, Color.Cyan, 2);
        
        // Title
        string title = "HUD Editor";
        var titleSize = _font.MeasureString(title);
        sb.DrawString(_font, title, 
            new Vector2(panelX + panelW / 2 - titleSize.X / 2, panelY + 10), 
            Color.Cyan);
        
        int lineY = panelY + 40;
        foreach (var line in instructions)
        {
            Color c = (line.StartsWith("Right-Click") || line.StartsWith("A:")) ? Color.Yellow : Color.White;
            sb.DrawString(_font, line, new Vector2(panelX + 10, lineY), c);
            lineY += lineStep;
        }
    }
    
    private void DrawFeedbackMessage(SpriteBatch sb, int screenWidth, int screenHeight)
    {
        if (_feedbackMessage == null) return;
        
        // Calculate fade-out alpha
        float alpha = Math.Min(_feedbackTimer / 0.5f, 1f); // Fade out in last 0.5 seconds
        
        // Message panel at bottom center
        var messageSize = _font.MeasureString(_feedbackMessage);
        int panelW = (int)messageSize.X + 40;
        int panelH = (int)messageSize.Y + 20;
        int panelX = screenWidth / 2 - panelW / 2;
        int panelY = screenHeight - panelH - 40;
        
        // Panel background with glow
        sb.Draw(_pixel, new Rectangle(panelX - 3, panelY - 3, panelW + 6, panelH + 6), 
            Color.LimeGreen * 0.3f * alpha);
        sb.Draw(_pixel, new Rectangle(panelX, panelY, panelW, panelH), 
            Color.Black * 0.9f * alpha);
        DrawRectangleBorder(sb, panelX, panelY, panelW, panelH, Color.LimeGreen * alpha, 2);
        
        // Message text
        Vector2 textPos = new Vector2(panelX + 20, panelY + 10);
        sb.DrawString(_font, _feedbackMessage, textPos + new Vector2(1, 1), Color.Black * alpha);
        sb.DrawString(_font, _feedbackMessage, textPos, Color.LimeGreen * alpha);
    }
    
    private void DrawRectangleBorder(SpriteBatch sb, int x, int y, int width, int height, Color color, int thickness)
    {
        // Top
        sb.Draw(_pixel, new Rectangle(x, y, width, thickness), color);
        // Bottom
        sb.Draw(_pixel, new Rectangle(x, y + height - thickness, width, thickness), color);
        // Left
        sb.Draw(_pixel, new Rectangle(x, y, thickness, height), color);
        // Right
        sb.Draw(_pixel, new Rectangle(x + width - thickness, y, thickness, height), color);
    }

    private void SyncToHudSettings()
    {
        var hudSettings = HudSettings.Instance;
        hudSettings.MasterHudVisible = _elements.Exists(e => e.Visible);
        
        foreach (var elem in _elements)
        {
            switch (elem.Name)
            {
                case "Health Bar":
                    hudSettings.HealthBarPosition = HudPosition.FromPoint(elem.Position);
                    hudSettings.HealthBarSize = HudPosition.FromPoint(elem.Size);
                    hudSettings.HealthBarVisible = elem.Visible;
                    break;
                    
                case "Score":
                    hudSettings.ScorePosition = HudPosition.FromPoint(elem.Position);
                    hudSettings.ScoreVisible = elem.Visible;
                    break;
                    
                case "Misses":
                    hudSettings.MissesPosition = HudPosition.FromPoint(elem.Position);
                    hudSettings.MissesVisible = elem.Visible;
                    break;
                    
                case "Accuracy":
                    hudSettings.AccuracyPosition = HudPosition.FromPoint(elem.Position);
                    hudSettings.AccuracyVisible = elem.Visible;
                    break;
                    
                case "Timer Bar":
                    hudSettings.TimerBarPosition = HudPosition.FromPoint(elem.Position);
                    hudSettings.TimerBarSize = HudPosition.FromPoint(elem.Size);
                    hudSettings.TimerBarVisible = elem.Visible;
                    break;
                    
                case "Song Info":
                    hudSettings.SongInfoPosition = HudPosition.FromPoint(elem.Position);
                    hudSettings.SongInfoVisible = elem.Visible;
                    break;

                case "Status Info":
                    hudSettings.StatusInfoPosition = HudPosition.FromPoint(elem.Position);
                    hudSettings.StatusInfoVisible = elem.Visible;
                    break;
                    
                case "Combo":
                    hudSettings.ComboPosition = HudPosition.FromPoint(elem.Position);
                    hudSettings.ComboVisible = elem.Visible;
                    break;
                    
                case "Judgement":
                    hudSettings.JudgementPosition = HudPosition.FromPoint(elem.Position);
                    hudSettings.JudgementVisible = elem.Visible;
                    break;
                    
                case "Opponent Icon":
                    hudSettings.OpponentIconPosition = HudPosition.FromPoint(elem.Position);
                    hudSettings.OpponentIconVisible = elem.Visible;
                    break;
                    
                case "Player Icon":
                    hudSettings.PlayerIconPosition = HudPosition.FromPoint(elem.Position);
                    hudSettings.PlayerIconVisible = elem.Visible;
                    break;

                case "Opponent Strumline":
                    hudSettings.OpponentStrumlineVisible = elem.Visible;
                    break;

                case "Player Strumline":
                    hudSettings.PlayerStrumlineVisible = elem.Visible;
                    break;
            }
        }

        hudSettings.StrumlinesVisible = hudSettings.OpponentStrumlineVisible || hudSettings.PlayerStrumlineVisible;
    }
    
    private void SaveHudLayout()
    {
        SyncToHudSettings();
        HudSettings.Instance.Save();
        Settings.Save(Game1.Instance?._graphics.SynchronizeWithVerticalRetrace ?? true);
        Console.WriteLine("[HudEditor] Layout saved to hud_settings.json!");
    }
}

// ── HUD Element ──────────────────────────────────────────────────────────────

public enum HudElementType
{
    HealthBar,
    Icon,
    Text,
    Strumline
}

public class HudElement
{
    public string Name { get; set; } = "";
    public Point Position { get; set; }
    public Point Size { get; set; }
    public Color Color { get; set; }
    public HudElementType Type { get; set; }
    public bool Visible { get; set; } = true;
}
