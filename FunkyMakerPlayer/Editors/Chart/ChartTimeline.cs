using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using System;

namespace FunkyMakerPlayer.Editors.Chart;

/// <summary>
/// Timeline with waveform visualization and playback controls
/// </summary>
public class ChartTimeline
{
    private readonly Texture2D _pixel;
    private readonly SpriteFont _font;
    
    public float CurrentTime { get; set; } = 0f;
    public float Duration { get; set; } = 0f;
    public bool IsPlaying { get; set; } = false;
    public float BPM { get; set; } = 120f;
    
    private Texture2D? _waveformTexture;
    
    public ChartTimeline(Texture2D pixel, SpriteFont font)
    {
        _pixel = pixel;
        _font = font;
    }
    
    public void Update(float dt)
    {
        if (IsPlaying && Duration > 0)
        {
            CurrentTime += dt * 1000f; // Convert to milliseconds
            if (CurrentTime >= Duration)
            {
                CurrentTime = Duration;
                IsPlaying = false;
            }
        }
    }
    
    public void Draw(SpriteBatch sb, Rectangle bounds)
    {
        // Background
        sb.Draw(_pixel, bounds, new Color(20, 20, 30) * 0.9f);
        
        // Draw waveform (simplified - just draw a pattern for now)
        DrawWaveformPlaceholder(sb, bounds);
        
        // Draw playback progress bar
        float progress = Duration > 0 ? CurrentTime / Duration : 0f;
        int progressWidth = (int)(bounds.Width * progress);
        
        sb.Draw(_pixel, 
            new Rectangle(bounds.X, bounds.Y, progressWidth, bounds.Height), 
            new Color(100, 149, 237) * 0.3f);
        
        // Draw playback cursor
        int cursorX = bounds.X + progressWidth;
        sb.Draw(_pixel, 
            new Rectangle(cursorX - 1, bounds.Y, 3, bounds.Height), 
            Color.Red * 0.8f);
        
        // Draw time labels
        string currentTimeStr = FormatTime(CurrentTime);
        string durationStr = FormatTime(Duration);
        string timeLabel = $"{currentTimeStr} / {durationStr}";
        
        var textSize = _font.MeasureString(timeLabel);
        sb.DrawString(_font, timeLabel,
            new Vector2(bounds.X + bounds.Width - textSize.X - 10, bounds.Y + 5),
            Color.White * 0.8f);
        
        // Draw BPM
        string bpmLabel = $"BPM: {BPM:F1}";
        sb.DrawString(_font, bpmLabel,
            new Vector2(bounds.X + 10, bounds.Y + 5),
            Color.Cyan * 0.8f);
    }
    
    private void DrawWaveformPlaceholder(SpriteBatch sb, Rectangle bounds)
    {
        // Simple waveform visualization
        int segments = bounds.Width / 4;
        float centerY = bounds.Y + bounds.Height / 2f;
        
        for (int i = 0; i < segments; i++)
        {
            float x = bounds.X + i * 4;
            
            // Fake waveform using sine waves
            float amplitude = MathF.Sin(i * 0.1f + CurrentTime * 0.001f) * (bounds.Height * 0.3f);
            int height = (int)MathF.Abs(amplitude);
            
            sb.Draw(_pixel,
                new Rectangle((int)x, (int)(centerY - height / 2), 3, height),
                new Color(100, 200, 255) * 0.4f);
        }
    }
    
    private string FormatTime(float milliseconds)
    {
        int totalSeconds = (int)(milliseconds / 1000);
        int minutes = totalSeconds / 60;
        int seconds = totalSeconds % 60;
        return $"{minutes}:{seconds:D2}";
    }
    
    public void Seek(float time)
    {
        CurrentTime = Math.Clamp(time, 0, Duration);
    }
    
    public void TogglePlayback()
    {
        IsPlaying = !IsPlaying;
    }
    
    public void Stop()
    {
        IsPlaying = false;
        CurrentTime = 0f;
    }
}
