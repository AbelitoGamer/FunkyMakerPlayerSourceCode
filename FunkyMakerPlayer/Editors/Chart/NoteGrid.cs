using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using Microsoft.Xna.Framework.Input;
using System;
using System.Collections.Generic;
using System.Linq;
using FunkyMakerPlayer.Editors.Common;

namespace FunkyMakerPlayer.Editors.Chart;

/// <summary>
/// Note grid with individual squares for each note (Codename Engine style)
/// </summary>
public class NoteGrid
{
    private readonly Texture2D _pixel;
    private readonly SpriteFont _font;
    private readonly NoteStyle? _noteStyle;
    
    private List<List<List<FnmmNote>>>? _strumlines;
    private int _currentStrumline = 0;
    
    private List<FnmmNote> _selectedNotes = new();
    private FnmmNote? _hoverNote = null;
    
    public float CurrentTime { get; set; } = 0f; // Seconds
    public float BPM { get; set; } = 120f;
    public float Speed { get; set; } = 1.0f;
    public float GridSnap { get; set; } = 1.0f / 16.0f; // 1/16 beats
    
    // Scroll state
    private float _scrollOffset = 0f; // Manual scroll offset
    private const float SCROLL_SPEED = 200f; // Pixels per second when scrolling
    private int _lastWheelValue = 0; // Track mouse wheel
    
    // Visual constants
    private const int CELL_SIZE = 40; // Each square cell size
    private const float BASE_SPEED = 600f; // Scroll speed
    
    // Keyboard state for arrow keys scrolling
    private KeyboardState _prevKb = Keyboard.GetState();
    
    private int GetLaneCount(int strumlineIndex)
    {
        if (_strumlines == null || strumlineIndex >= _strumlines.Count)
            return 4;
        return _strumlines[strumlineIndex].Count;
    }
    
    public NoteGrid(Texture2D pixel, SpriteFont font, NoteStyle? noteStyle = null)
    {
        _pixel = pixel;
        _font = font;
        _noteStyle = noteStyle;
    }
    
    public void LoadFromFnmm(FnmmLevel level)
    {
        if (level?.Data?.Strumlines == null)
        {
            Console.WriteLine("[NoteGrid] ⚠️ No strumlines in level");
            _strumlines = null;
            return;
        }
        
        // Direct reference to the level's strumlines
        _strumlines = level.Data.Strumlines;
        _currentStrumline = 0;
        
        int totalNotes = 0;
        foreach (var strumline in _strumlines)
        {
            foreach (var lane in strumline)
            {
                totalNotes += lane.Count;
            }
        }
        
        Console.WriteLine($"[NoteGrid] Loaded {_strumlines.Count} strumlines with {totalNotes} total notes");
    }
    
    public List<FnmmNote> GetAllNotes()
    {
        var allNotes = new List<FnmmNote>();
        if (_strumlines == null) return allNotes;
        
        foreach (var strumline in _strumlines)
        {
            foreach (var lane in strumline)
            {
                allNotes.AddRange(lane);
            }
        }
        return allNotes;
    }
    
    public void PlaceNote(int lane, float timeBeats)
    {
        if (_strumlines == null || _currentStrumline >= _strumlines.Count) return;
        
        var strumline = _strumlines[_currentStrumline];
        if (lane < 0 || lane >= strumline.Count) return;
        
        var newNote = new FnmmNote
        {
            Start = timeBeats,
            Duration = 0,
            Props = new NoteProps { Skin = 0 }
        };
        
        strumline[lane].Add(newNote);
        Console.WriteLine($"[NoteGrid] Placed note at lane {lane}, time {timeBeats:F3}");
    }
    
    public void DeleteNote(FnmmNote note)
    {
        if (_strumlines == null) return;
        
        foreach (var strumline in _strumlines)
        {
            foreach (var lane in strumline)
            {
                if (lane.Remove(note))
                {
                    Console.WriteLine("[NoteGrid] Deleted note");
                    return;
                }
            }
        }
    }
    
    public void DeleteSelected()
    {
        foreach (var note in _selectedNotes.ToList())
        {
            DeleteNote(note);
        }
        _selectedNotes.Clear();
    }
    
    public void SelectNote(FnmmNote note, bool addToSelection)
    {
        if (!addToSelection)
            _selectedNotes.Clear();
        
        if (!_selectedNotes.Contains(note))
            _selectedNotes.Add(note);
    }
    
    public void SelectAll()
    {
        _selectedNotes.Clear();
        _selectedNotes.AddRange(GetAllNotes());
    }
    
    public void ClearSelection() => _selectedNotes.Clear();
    
    public List<FnmmNote> GetSelectedNotes() => _selectedNotes;
    
    public int GetStrumlineCount() => _strumlines?.Count ?? 0;
    
    public int GetCurrentStrumline() => _currentStrumline;
    
    public void NextStrumline()
    {
        if (_strumlines == null) return;
        _currentStrumline = (_currentStrumline + 1) % _strumlines.Count;
    }
    
    public void PreviousStrumline()
    {
        if (_strumlines == null) return;
        _currentStrumline--;
        if (_currentStrumline < 0) _currentStrumline = _strumlines.Count - 1;
    }
    
    public FnmmNote? GetNoteAt(Point screenPos, Rectangle gridBounds)
    {
        if (_strumlines == null) return null;
        
        // Check all strumlines
        int currentX = GetChartStartX(gridBounds);
        
        for (int s = 0; s < _strumlines.Count; s++)
        {
            var strumline = _strumlines[s];
            int laneCount = strumline.Count;
            int strumlineWidth = CELL_SIZE * laneCount;
            
            if (screenPos.X >= currentX && screenPos.X < currentX + strumlineWidth)
            {
                // Inside this strumline
                for (int lane = 0; lane < laneCount; lane++)
                {
                    foreach (var note in strumline[lane])
                    {
                        var rect = GetNoteCellRect(note, lane, currentX, gridBounds);
                        if (rect.Contains(screenPos))
                            return note;
                    }
                }
            }
            
            currentX += strumlineWidth;
        }
        
        return null;
    }
    
    public (int lane, float timeBeats) ScreenPosToGridData(Point screenPos, Rectangle gridBounds)
    {
        if (_strumlines == null || _strumlines.Count == 0)
            return (0, 0f);
        
        int currentX = GetChartStartX(gridBounds);
        float strumY = GetStrumY(gridBounds);
        
        // Find which strumline
        for (int s = 0; s < _strumlines.Count; s++)
        {
            int laneCount = GetLaneCount(s);
            int strumlineWidth = CELL_SIZE * laneCount;
            
            if (screenPos.X >= currentX && screenPos.X < currentX + strumlineWidth)
            {
                _currentStrumline = s;
                
                int lane = (screenPos.X - currentX) / CELL_SIZE;
                lane = Math.Clamp(lane, 0, laneCount - 1);
                
                float scrollSpeed = GetScrollSpeed();
                // Account for scroll offset when calculating time
                float timeDiff = (screenPos.Y - strumY - _scrollOffset) / scrollSpeed;
                float timeValue = CurrentTime + timeDiff;
                timeValue = SnapTime(timeValue);
                
                return (lane, timeValue);
            }
            
            currentX += strumlineWidth;
        }
        
        return (0, 0f);
    }
    
    private int GetChartStartX(Rectangle gridBounds)
    {
        if (_strumlines == null) return gridBounds.X;
        
        int totalLanes = 0;
        foreach (var strumline in _strumlines)
        {
            totalLanes += strumline.Count;
        }
        
        int totalWidth = totalLanes * CELL_SIZE;
        return gridBounds.X + (gridBounds.Width - totalWidth) / 2;
    }
    
    private float GetStrumY(Rectangle gridBounds)
    {
        return gridBounds.Y + gridBounds.Height * 0.6f;
    }
    
    private float GetScrollSpeed()
    {
        return BASE_SPEED * (BPM / 120f) * Speed;
    }
    
    private float SnapTime(float timeBeats)
    {
        if (GridSnap <= 0) return timeBeats;
        return MathF.Round(timeBeats / GridSnap) * GridSnap;
    }
    
    private Rectangle GetNoteCellRect(FnmmNote note, int lane, int strumlineX, Rectangle gridBounds)
    {
        float strumY = GetStrumY(gridBounds);
        float scrollSpeed = GetScrollSpeed();
        float timeDiff = note.Start - CurrentTime;
        float noteY = strumY + timeDiff * scrollSpeed + _scrollOffset;
        
        int x = strumlineX + lane * CELL_SIZE;
        int y = (int)noteY;
        
        return new Rectangle(x, y, CELL_SIZE, CELL_SIZE);
    }
    
    public void Update(float dt, MouseState ms, Rectangle gridBounds)
    {
        if (!Game1.CanAcceptInput)
        {
            _prevKb = default;
            _lastWheelValue = ms.ScrollWheelValue;
            return;
        }

        _hoverNote = GetNoteAt(ms.Position, gridBounds);
        
        // Keyboard scrolling
        KeyboardState kb = Keyboard.GetState();
        
        if (kb.IsKeyDown(Keys.Up))
            _scrollOffset += SCROLL_SPEED * dt;
        if (kb.IsKeyDown(Keys.Down))
            _scrollOffset -= SCROLL_SPEED * dt;
        
        _prevKb = kb;
        
        // Mouse wheel scroll
        if (gridBounds.Contains(ms.Position))
        {
            int wheelDelta = ms.ScrollWheelValue;
            
            if (wheelDelta != _lastWheelValue)
            {
                float delta = (wheelDelta - _lastWheelValue) / 120f;
                _scrollOffset += delta * CELL_SIZE * 2;
                _lastWheelValue = wheelDelta;
            }
        }
    }
    
    public void Draw(SpriteBatch sb, Rectangle gridBounds)
    {
        if (_strumlines == null || _strumlines.Count == 0)
        {
            string msg = "No strumlines found";
            var size = _font.MeasureString(msg);
            sb.DrawString(_font, msg,
                new Vector2(gridBounds.X + gridBounds.Width / 2 - size.X / 2, gridBounds.Y + gridBounds.Height / 2),
                Color.Gray);
            return;
        }
        
        int startX = GetChartStartX(gridBounds);
        float strumY = GetStrumY(gridBounds);
        
        // Draw each strumline side by side
        int currentX = startX;
        for (int s = 0; s < _strumlines.Count; s++)
        {
            var strumline = _strumlines[s];
            int laneCount = strumline.Count;
            int strumlineWidth = CELL_SIZE * laneCount;
            
            var strumlineBounds = new Rectangle(currentX, gridBounds.Y, strumlineWidth, gridBounds.Height);
            
            // Draw background grid
            DrawGridBackground(sb, strumlineBounds, laneCount, strumY);
            
            // Draw notes as individual squares
            for (int lane = 0; lane < laneCount; lane++)
            {
                foreach (var note in strumline[lane])
                {
                    DrawNoteSquare(sb, note, lane, currentX, gridBounds, s);
                }
            }
            
            // Draw strumline label
            string label = s == 0 ? "Player" : s == 1 ? "Opponent" : $"Chart {s + 1}";
            var labelSize = _font.MeasureString(label) * 0.35f;
            sb.DrawString(_font, label,
                new Vector2(currentX + strumlineWidth / 2 - labelSize.X / 2, gridBounds.Y + 2),
                new Color(150, 150, 170), 0f, Vector2.Zero, 0.35f, SpriteEffects.None, 0f);
            
            currentX += strumlineWidth;
        }
        
        // Draw strum line (on top)
        int totalWidth = 0;
        foreach (var s in _strumlines)
        {
            totalWidth += s.Count * CELL_SIZE;
        }
        
        sb.Draw(_pixel, new Rectangle(startX, (int)strumY - 2, totalWidth, 4), Color.White * 0.8f);
    }
    
    private void DrawGridBackground(SpriteBatch sb, Rectangle bounds, int laneCount, float strumY)
    {
        // Draw grid filling the ENTIRE vertical space (not relative to strumY)
        // Start from top of bounds and fill downward
        
        // Calculate how many cells fit vertically
        int cellsVisible = (bounds.Height / CELL_SIZE) + 2;
        
        // Calculate starting beat based on scroll offset
        float startBeat = -((_scrollOffset + bounds.Height / 2) / CELL_SIZE);
        int firstBeat = (int)Math.Floor(startBeat);
        
        // Draw cells filling the entire height
        for (int i = 0; i < cellsVisible + 20; i++) // Extra cells for smooth scrolling
        {
            int beatIndex = firstBeat + i;
            if (beatIndex < 0) continue;
            
            // Calculate Y position from top
            float y = bounds.Y + (beatIndex - startBeat) * CELL_SIZE;
            
            if (y + CELL_SIZE < bounds.Top || y > bounds.Bottom) continue;
            
            for (int lane = 0; lane < laneCount; lane++)
            {
                int x = bounds.X + lane * CELL_SIZE;
                
                // Checkerboard pattern
                bool isDark = (beatIndex + lane) % 2 == 0;
                Color cellColor = isDark ? new Color(18, 18, 24) : new Color(24, 24, 32);
                
                // Highlight every 4 beats (measure lines)
                if (beatIndex % 4 == 0)
                {
                    cellColor = isDark ? new Color(28, 28, 38) : new Color(32, 32, 42);
                }
                
                // Draw the cell square
                sb.Draw(_pixel, new Rectangle(x, (int)y, CELL_SIZE, CELL_SIZE), cellColor);
                
                // Grid lines (borders of each square)
                sb.Draw(_pixel, new Rectangle(x, (int)y, CELL_SIZE, 1), new Color(40, 40, 50) * 0.6f);
                sb.Draw(_pixel, new Rectangle(x, (int)y, 1, CELL_SIZE), new Color(40, 40, 50) * 0.6f);
            }
            
            // Draw measure lines (every 4 beats) - horizontal thicker lines
            if (beatIndex % 4 == 0)
            {
                sb.Draw(_pixel, new Rectangle(bounds.X, (int)y - 1, bounds.Width, 2), new Color(80, 80, 100) * 0.8f);
                
                // Draw beat number on measure lines
                string beatNum = $"{beatIndex}";
                var size = _font.MeasureString(beatNum) * 0.35f;
                sb.DrawString(_font, beatNum,
                    new Vector2(bounds.X - size.X - 3, y - size.Y / 2),
                    new Color(120, 120, 140), 0f, Vector2.Zero, 0.35f, SpriteEffects.None, 0f);
            }
        }
        
        // Vertical lane separators (thicker)
        for (int lane = 1; lane < laneCount; lane++)
        {
            int x = bounds.X + lane * CELL_SIZE;
            sb.Draw(_pixel, new Rectangle(x - 1, bounds.Y, 2, bounds.Height), new Color(60, 60, 80) * 0.5f);
        }
    }
    
    private void DrawNoteSquare(SpriteBatch sb, FnmmNote note, int lane, int strumlineX, Rectangle gridBounds, int strumlineIndex)
    {
        var rect = GetNoteCellRect(note, lane, strumlineX, gridBounds);
        
        if (rect.Bottom < gridBounds.Top || rect.Top > gridBounds.Bottom)
            return;
        
        Color noteColor = GetNoteColor(lane);
        
        if (note.Props.IsDamage) noteColor = Color.Purple;
        if (note.Props.IsInstakill) noteColor = Color.Black;
        if (note.Props.IsHeal) noteColor = Color.LightGreen;
        
        if (_selectedNotes.Contains(note))
            noteColor = Color.White;
        
        if (_hoverNote != null && _hoverNote != note)
            noteColor *= 0.6f;
        
        // Draw arrow from notestyle or fallback
        if (_noteStyle != null)
        {
            var frame = _noteStyle.GetNoteFrame(lane, (int)note.Props.Skin);
            if (frame != null)
            {
                float scale = (CELL_SIZE - 8f) / frame.Source.Width;
                sb.Draw(frame.Texture,
                    new Vector2(rect.X + rect.Width / 2, rect.Y + rect.Height / 2),
                    frame.Source, noteColor, 0f,
                    new Vector2(frame.Source.Width / 2, frame.Source.Height / 2),
                    scale, SpriteEffects.None, 0f);
                return;
            }
        }
        
        // Fallback: colored square
        sb.Draw(_pixel, new Rectangle(rect.X + 4, rect.Y + 4, rect.Width - 8, rect.Height - 8), noteColor * 0.8f);
        sb.Draw(_pixel, new Rectangle(rect.X + 4, rect.Y + 4, rect.Width - 8, 2), noteColor);
        sb.Draw(_pixel, new Rectangle(rect.X + 4, rect.Bottom - 6, rect.Width - 8, 2), noteColor);
        sb.Draw(_pixel, new Rectangle(rect.X + 4, rect.Y + 4, 2, rect.Height - 8), noteColor);
        sb.Draw(_pixel, new Rectangle(rect.Right - 6, rect.Y + 4, 2, rect.Height - 8), noteColor);
    }
    
    private Color GetNoteColor(int lane)
    {
        return lane switch
        {
            0 => new Color(194, 75, 153),  // Pink (Left)
            1 => new Color(0, 255, 255),   // Cyan (Down)
            2 => new Color(18, 250, 5),    // Green (Up)
            3 => new Color(249, 57, 63),   // Red (Right)
            _ => Color.White
        };
    }
}
