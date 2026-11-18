using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using System;

namespace FunkyMakerPlayer
{
    /// <summary>
    /// FunkyMaker's native notestyle renderer.
    /// Handles the original FMM/FNF format with 8-column hold textures (SustainTrail.hx logic).
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
            var frames = _style.ReceptorFrames[lane, state];
            if (frames == null || frames.Count == 0) return;
            
            var frame = frames[0];
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
            DrawFMMHold(sb, lane, centerX, arrowY, holdLengthPx, laneWidth, alpha * (_style.HoldAlpha ?? 0.85f));
        }
        
        public void DrawActiveHold(SpriteBatch sb, int lane, float centerX, float strumY, 
                                  float holdLengthPx, float laneWidth, float alpha)
        {
            DrawFMMHold(sb, lane, centerX, strumY, holdLengthPx, laneWidth, alpha * (_style.HoldAlpha ?? 0.75f));
        }

        /// <summary>
        /// Direct port of official FunkinCrew/Funkin SustainTrail.hx (lines 94, 100, 308-398).
        /// Keeps end caps at their exact 1:1 scaled aspect ratio without squishing, clips off the
        /// bottom 10% overflow line (bottomClip = 0.9), and joins body + end cap with zero overlap/gap.
        /// </summary>
        private void DrawFMMHold(SpriteBatch sb, int lane, float centerX, float startY,
                                 float holdLengthPx, float laneWidth, float alphaTint)
        {
            if (_style.HoldTexture == null || holdLengthPx <= 0.5f) return;
            
            var holdTex = _style.HoldTexture;
            int colWidth = holdTex.Width / 8;
            int texHeight = holdTex.Height;
            int bodyCol = lane * 2;
            int capCol = lane * 2 + 1;
            
            float sx = laneWidth > 0f ? laneWidth / NoteRenderer.LANE_WIDTH : 1f;
            float noteScale = _style.NoteScale * sx;
            float scaledWidth = colWidth * noteScale;
            
            // Official FNF SustainTrail.hx bottomClip = 0.9f clips off the bottom 10% overflow artifact
            const float BOTTOM_CLIP = 0.9f;
            int capSrcBottom = (int)Math.Round(BOTTOM_CLIP * texHeight);
            float fullCapHeight = capSrcBottom * noteScale;
            float partHeight = holdLengthPx - fullCapHeight;
            
            // Trim 1px top/bottom on body column to avoid stretching any edge-filtering row
            var bodySrc = texHeight > 4
                ? new Rectangle(bodyCol * colWidth, 1, colWidth, texHeight - 2)
                : new Rectangle(bodyCol * colWidth, 0, colWidth, texHeight);
            
            // While partHeight >= 0, the end cap is drawn 100% intact.
            // When partHeight < 0 (body consumed), clip the top of the end cap at 1:1 scale at the receptor.
            float consumedRatio = partHeight >= 0f
                ? 0f
                : 1f - (holdLengthPx / Math.Max(1f, fullCapHeight));
            consumedRatio = Math.Clamp(consumedRatio, 0f, 1f);
            
            int capSrcY = (int)Math.Round(consumedRatio * capSrcBottom);
            int capSrcH = capSrcBottom - capSrcY;
            if (capSrcH <= 0) return;
            
            var capSrc = new Rectangle(capCol * colWidth, capSrcY, colWidth, capSrcH);
            
            // Screen height of the end cap is ALWAYS 1:1 with capSrcH * noteScale (never squished or stretched!)
            int capHeight = (int)Math.Round(capSrcH * noteScale);
            if (capHeight <= 0) return;
            
            int intX = (int)Math.Round(centerX - scaledWidth / 2f);
            int intWidth = (int)Math.Round(scaledWidth);
            
            Color tint = Color.White * alphaTint;
            bool downscroll = NoteRenderer.Downscroll;
            
            if (!downscroll)
            {
                // Upscroll: body from startIntY to joinY, end cap from joinY to joinY + capHeight (0 gap, 0 overlap)
                int startIntY = (int)Math.Round(startY);
                int joinY = partHeight > 0f ? (int)Math.Round(startY + partHeight) : startIntY;
                int bodyH = joinY - startIntY;
                
                if (bodyH > 0)
                {
                    sb.Draw(holdTex,
                        new Rectangle(intX, startIntY, intWidth, bodyH),
                        bodySrc, tint);
                }
                
                sb.Draw(holdTex,
                    new Rectangle(intX, joinY, intWidth, capHeight),
                    capSrc, tint);
            }
            else
            {
                // Downscroll: body from joinY to startIntY, end cap from joinY - capHeight to joinY (0 gap, 0 overlap)
                int startIntY = (int)Math.Round(startY);
                int joinY = partHeight > 0f ? (int)Math.Round(startY - partHeight) : startIntY;
                int bodyH = startIntY - joinY;
                
                if (bodyH > 0)
                {
                    sb.Draw(holdTex,
                        new Rectangle(intX, joinY, intWidth, bodyH),
                        bodySrc, tint, 0f, Vector2.Zero, SpriteEffects.FlipVertically, 0f);
                }
                
                sb.Draw(holdTex,
                    new Rectangle(intX, joinY - capHeight, intWidth, capHeight),
                    capSrc, tint, 0f, Vector2.Zero, SpriteEffects.FlipVertically, 0f);
            }
        }
    }
}
