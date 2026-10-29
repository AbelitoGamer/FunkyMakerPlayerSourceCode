using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using System;

namespace FunkyMakerPlayer
{
    /// <summary>
    /// FunkyMaker's native notestyle renderer.
    /// Handles the original FMM format with 8-column hold textures.
    /// </summary>
    public class FMMNoteStyleRenderer : INoteStyleRenderer
    {
        private readonly NoteStyle _style;
        private readonly bool _downscroll;
        
        public FMMNoteStyleRenderer(NoteStyle style, bool downscroll)
        {
            _style = style;
            _downscroll = downscroll;
        }
        
        public string GetEngineName() => "FunkyMaker";
        
        public void DrawArrow(SpriteBatch sb, int lane, float x, float y, float scale, float alpha, Color tint)
        {
            // FMM uses atlas frames for arrows
            var frame = _style.GetNoteFrame(lane, 0);
            if (frame == null) return;
            
            var src = frame.Source;
            var dst = new Rectangle((int)x, (int)y, 
                                   (int)(frame.Source.Width * scale), 
                                   (int)(frame.Source.Height * scale));
            
            sb.Draw(frame.Texture, dst, src, tint * alpha);
        }
        
        public void DrawReceptor(SpriteBatch sb, int lane, float x, float y, int state, float scale, float alpha)
        {
            // FMM receptor rendering (uses existing animation system)
            // This would integrate with the existing receptor animation code
            // For now, simplified version
            var frames = _style.ReceptorFrames[lane, state];
            if (frames == null || frames.Count == 0) return;
            
            var frame = frames[0]; // First frame for simplicity
            var src = frame.Source;
            var dst = new Rectangle((int)x, (int)y,
                                   (int)(frame.Source.Width * scale),
                                   (int)(frame.Source.Height * scale));
            
            sb.Draw(frame.Texture, dst, src, Color.White * alpha);
        }
        
        public void DrawUpcomingHold(SpriteBatch sb, int lane, float centerX, float arrowY, 
                                    float holdLengthPx, float strumY, float laneWidth, 
                                    float alpha, float scrollSpeed, float bpm)
        {
            if (_style.HoldTexture == null) return;
            
            var holdTex = _style.HoldTexture;
            int colWidth = holdTex.Width / 8;
            int texHeight = holdTex.Height;
            int bodyCol = lane * 2;
            int capCol = lane * 2 + 1;
            
            var bodySrc = new Rectangle(bodyCol * colWidth, 0, colWidth, texHeight);
            var capSrc = new Rectangle(capCol * colWidth, 0, colWidth, texHeight);
            
            float noteScale = _style.NoteScale;
            float scaledWidth = colWidth * noteScale;
            float fixedCapHeight = texHeight * noteScale;
            
            float totalLength = holdLengthPx;
            float actualCapHeight = Math.Min(fixedCapHeight, totalLength);
            float bodyStretchHeight = totalLength - actualCapHeight;
            
            // Integer positioning to prevent sub-pixel gaps
            int intX = (int)(centerX - scaledWidth / 2f);
            int intWidth = (int)Math.Ceiling(scaledWidth);
            
            Color bodyTint = Color.White * 0.85f * alpha;
            Color capTint = Color.White * 0.85f * alpha;
            
            if (!_downscroll)
            {
                // Upscroll: body first, then cap
                int currentY = (int)arrowY;
                
                if (bodyStretchHeight > 1f)
                {
                    // Add 2px overlap to eliminate gaps
                    int bodyHeight = (int)Math.Ceiling(bodyStretchHeight) + 2;
                    sb.Draw(holdTex, 
                        new Rectangle(intX, currentY, intWidth, bodyHeight),
                        bodySrc, bodyTint);
                    currentY += (int)bodyStretchHeight;
                }
                
                if (actualCapHeight > 0f)
                {
                    int capHeight = (int)Math.Ceiling(actualCapHeight);
                    sb.Draw(holdTex,
                        new Rectangle(intX, currentY, intWidth, capHeight),
                        capSrc, capTint);
                }
            }
            else
            {
                // Downscroll: cap first (flipped), then body (flipped)
                int currentY = (int)arrowY;
                
                if (actualCapHeight > 0f)
                {
                    int capHeight = (int)Math.Ceiling(actualCapHeight) + 2;
                    int capTop = currentY - capHeight;
                    sb.Draw(holdTex,
                        new Rectangle(intX, capTop, intWidth, capHeight),
                        capSrc, capTint, 0f, Vector2.Zero, SpriteEffects.FlipVertically, 0f);
                    currentY = capTop;
                }
                
                if (bodyStretchHeight > 1f)
                {
                    int bodyHeight = (int)Math.Ceiling(bodyStretchHeight);
                    int bodyTop = currentY - bodyHeight;
                    sb.Draw(holdTex,
                        new Rectangle(intX, bodyTop, intWidth, bodyHeight),
                        bodySrc, bodyTint, 0f, Vector2.Zero, SpriteEffects.FlipVertically, 0f);
                }
            }
        }
        
        public void DrawActiveHold(SpriteBatch sb, int lane, float centerX, float strumY, 
                                  float holdLengthPx, float laneWidth, float alpha)
        {
            if (_style.HoldTexture == null) return;
            
            var holdTex = _style.HoldTexture;
            int colWidth = holdTex.Width / 8;
            int texHeight = holdTex.Height;
            int bodyCol = lane * 2;
            int capCol = lane * 2 + 1;
            
            var bodySrc = new Rectangle(bodyCol * colWidth, 0, colWidth, texHeight);
            var capSrc = new Rectangle(capCol * colWidth, 0, colWidth, texHeight);
            
            float noteScale = _style.NoteScale;
            float scaledWidth = colWidth * noteScale;
            float fixedCapHeight = texHeight * noteScale;
            
            float totalLength = holdLengthPx;
            float actualCapHeight = Math.Min(fixedCapHeight, totalLength);
            float bodyStretchHeight = totalLength - actualCapHeight;
            
            // Integer positioning to prevent sub-pixel gaps
            int intX = (int)(centerX - scaledWidth / 2f);
            int intWidth = (int)Math.Ceiling(scaledWidth);
            
            Color bodyTint = Color.White * 0.7f * alpha;
            Color capTint = Color.White * 0.7f * alpha;
            
            if (!_downscroll)
            {
                // Upscroll: body first, then cap extending downward from strum
                int currentY = (int)strumY;
                
                if (bodyStretchHeight > 1f)
                {
                    int bodyHeight = (int)Math.Ceiling(bodyStretchHeight) + 2;
                    sb.Draw(holdTex,
                        new Rectangle(intX, currentY, intWidth, bodyHeight),
                        bodySrc, bodyTint);
                    currentY += (int)bodyStretchHeight;
                }
                
                if (actualCapHeight > 0f)
                {
                    int capHeight = (int)Math.Ceiling(actualCapHeight);
                    sb.Draw(holdTex,
                        new Rectangle(intX, currentY, intWidth, capHeight),
                        capSrc, capTint);
                }
            }
            else
            {
                // Downscroll: cap first (flipped), then body (flipped) extending upward from strum
                int currentY = (int)strumY;
                
                if (actualCapHeight > 0f)
                {
                    int capHeight = (int)Math.Ceiling(actualCapHeight) + 2;
                    int capTop = currentY - capHeight;
                    sb.Draw(holdTex,
                        new Rectangle(intX, capTop, intWidth, capHeight),
                        capSrc, capTint, 0f, Vector2.Zero, SpriteEffects.FlipVertically, 0f);
                    currentY = capTop;
                }
                
                if (bodyStretchHeight > 1f)
                {
                    int bodyHeight = (int)Math.Ceiling(bodyStretchHeight);
                    int bodyTop = currentY - bodyHeight;
                    sb.Draw(holdTex,
                        new Rectangle(intX, bodyTop, intWidth, bodyHeight),
                        bodySrc, bodyTint, 0f, Vector2.Zero, SpriteEffects.FlipVertically, 0f);
                }
            }
        }
    }
}
