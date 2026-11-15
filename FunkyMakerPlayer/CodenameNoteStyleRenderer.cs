using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using System;

namespace FunkyMakerPlayer
{
    /// <summary>
    /// Codename Engine notestyle renderer - C# interpretation of Note.hx
    /// 
    /// Direct translation from CodenameCrew/CodenameEngine Note.hx:
    /// - Lines 172-190: Load 'hold piece' and 'hold end' from atlas
    /// - Lines 237-243: scale.y = (sustainLength * 0.45 * scrollSpeed) / frameHeight + gapFix
    /// - Line 211: alpha = 0.6 for sustains
    /// - Lines 285-287: Position with height * 0.5 offset
    /// - Lines 224-226: gapFix = 1 when antialiasing enabled (prevents gaps)
    /// 
    /// Key insight: Codename STRETCHES one sprite, not tiles multiple
    /// </summary>
    public class CodenameNoteStyleRenderer : INoteStyleRenderer
    {
        private readonly NoteStyle _style;
        private readonly bool _downscroll;
        
        // Cache hold piece and end frames from atlas
        private readonly AtlasFrame?[] _holdPieceFrames = new AtlasFrame?[4];  // body sprites
        private readonly AtlasFrame?[] _holdEndFrames = new AtlasFrame?[4];    // end cap sprites
        
        public CodenameNoteStyleRenderer(NoteStyle style, bool downscroll)
        {
            _style = style;
            _downscroll = downscroll;
            
            // Load hold frames from atlas like Codename Engine does
            // Note.hx lines 172-190: animation.addByPrefix('hold', 'purple hold piece')
            LoadHoldFramesFromAtlas();
        }
        
        private void LoadHoldFramesFromAtlas()
        {
            Console.WriteLine($"[CodenameRenderer] ═══ LoadHoldFramesFromAtlas ═══");
            Console.WriteLine($"  HoldPieceFrames null? {_style.HoldPieceFrames == null}");
            Console.WriteLine($"  HoldEndFrames null? {_style.HoldEndFrames == null}");
            
            if (_style.HoldPieceFrames != null && _style.HoldEndFrames != null)
            {
                // Frames already loaded by NoteStyle
                for (int i = 0; i < 4; i++)
                {
                    _holdPieceFrames[i] = _style.HoldPieceFrames[i];
                    _holdEndFrames[i] = _style.HoldEndFrames[i];
                }
                
                Console.WriteLine($"[CodenameRenderer] ✓ Copied frames from NoteStyle");
                for (int i = 0; i < 4; i++)
                {
                    Console.WriteLine($"  Lane {i}: piece={_holdPieceFrames[i]?.Name ?? "NULL"}, end={_holdEndFrames[i]?.Name ?? "NULL"}");
                }
            }
            else
            {
                Console.WriteLine($"[CodenameRenderer] ✗ Arrays are NULL!");
            }
            Console.WriteLine($"[CodenameRenderer] ═══════════════════════════");
        }
        
        public string GetEngineName() => "Codename Engine";
        
        public void DrawArrow(SpriteBatch sb, int lane, float x, float y, float scale, float alpha, Color tint)
        {
            // Codename uses same arrow system as FMM (atlas frames)
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
            // Codename receptors same as FMM
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
            var pieceFrame = _holdPieceFrames[lane];
            var endFrame = _holdEndFrames[lane];
            
            if (pieceFrame == null || endFrame == null) return;
            
            float sx = laneWidth > 0f ? laneWidth / NoteRenderer.LANE_WIDTH : 1f;
            float noteScale = _style.NoteScale * sx;
            
            DrawCodenameHold(sb, pieceFrame, endFrame, lane, centerX, arrowY, 
                           holdLengthPx, noteScale, alpha * 0.7f, scrollSpeed);
        }
        
        private void DrawCodenameHold(SpriteBatch sb, AtlasFrame pieceFrame, AtlasFrame endFrame,
                                     int lane, float centerX, float holdStartY, float holdLengthPx,
                                     float noteScale, float alphaTint, float scrollSpeed)
        {
            if (holdLengthPx <= 0.5f) return;

            var pieceSrc = pieceFrame.Source;
            var endSrc = endFrame.Source;
            
            // Trim 1px top/bottom of pieceSrc so anti-aliased edge rows (e.g. row 43 with Alpha=127
            // in Cornered/Avulsion/Haunted) are never stretched into a dark horizontal seam above the endcap.
            var cleanPieceSrc = pieceSrc.Height > 4
                ? new Rectangle(pieceSrc.X, pieceSrc.Y + 1, pieceSrc.Width, pieceSrc.Height - 2)
                : pieceSrc;
            var cleanEndSrc = endSrc.Height > 4
                ? new Rectangle(endSrc.X, endSrc.Y + 1, endSrc.Width, endSrc.Height - 1)
                : endSrc;
            
            // Width for piece (body)
            float scaledWidth = cleanPieceSrc.Width * noteScale;
            
            // End cap has its own natural dimensions (preserve 1:1 scaled aspect ratio, never squish)
            float scaledEndWidth = cleanEndSrc.Width * noteScale;
            float fullCapHeight = cleanEndSrc.Height * noteScale;
            
            // Reserve full 1:1 endcap height so the endcap stays 100% intact while body > 0,
            // and smoothly clips from the top at 1:1 scale as the final pixels enter the receptor.
            float partHeight = holdLengthPx - fullCapHeight;
            
            float consumedRatio = partHeight >= 0f
                ? 0f
                : 1f - (holdLengthPx / Math.Max(1f, fullCapHeight));
            consumedRatio = Math.Clamp(consumedRatio, 0f, 1f);
            
            int capClipY = (int)Math.Round(consumedRatio * cleanEndSrc.Height);
            int capClipH = cleanEndSrc.Height - capClipY;
            if (capClipH <= 0) return;
            
            var clippedEndSrc = new Rectangle(cleanEndSrc.X, cleanEndSrc.Y + capClipY, cleanEndSrc.Width, capClipH);
            int capHeight = (int)Math.Round(capClipH * noteScale);
            if (capHeight <= 0) return;
            
            Color tint = Color.White * alphaTint;
            
            int intX = (int)Math.Round(centerX - scaledWidth / 2f);
            int intWidth = (int)Math.Round(scaledWidth);
            
            int endX = (int)Math.Round(centerX - scaledEndWidth / 2f);
            int endWidth = (int)Math.Round(scaledEndWidth);
            
            bool downscroll = NoteRenderer.Downscroll;
            
            if (!downscroll)
            {
                // UPSCROLL: body from startIntY to joinY, end cap from joinY to joinY + capHeight (0 gap, 0 overlap)
                int startIntY = (int)Math.Round(holdStartY);
                int joinY = partHeight > 0f ? (int)Math.Round(holdStartY + partHeight) : startIntY;
                int bodyH = joinY - startIntY;
                
                if (bodyH > 0)
                {
                    sb.Draw(pieceFrame.Texture,
                        new Rectangle(intX, startIntY, intWidth, bodyH),
                        cleanPieceSrc, tint);
                }
                
                sb.Draw(endFrame.Texture,
                    new Rectangle(endX, joinY, endWidth, capHeight),
                    clippedEndSrc, tint);
            }
            else
            {
                // DOWNSCROLL: body from joinY to startIntY, end cap from joinY - capHeight to joinY (0 gap, 0 overlap)
                int startIntY = (int)Math.Round(holdStartY);
                int joinY = partHeight > 0f ? (int)Math.Round(holdStartY - partHeight) : startIntY;
                int bodyH = startIntY - joinY;
                
                if (bodyH > 0)
                {
                    sb.Draw(pieceFrame.Texture,
                        new Rectangle(intX, joinY, intWidth, bodyH),
                        cleanPieceSrc, tint, 0f, Vector2.Zero, SpriteEffects.FlipVertically, 0f);
                }
                
                sb.Draw(endFrame.Texture,
                    new Rectangle(endX, joinY - capHeight, endWidth, capHeight),
                    clippedEndSrc, tint, 0f, Vector2.Zero, SpriteEffects.FlipVertically, 0f);
            }
        }
        
        public void DrawActiveHold(SpriteBatch sb, int lane, float centerX, float strumY, 
                                  float holdLengthPx, float laneWidth, float alpha)
        {
            var pieceFrame = _holdPieceFrames[lane];
            var endFrame = _holdEndFrames[lane];
            
            if (pieceFrame == null || endFrame == null) return;
            
            float sx = laneWidth > 0f ? laneWidth / NoteRenderer.LANE_WIDTH : 1f;
            float noteScale = _style.NoteScale * sx;
            
            DrawCodenameHold(sb, pieceFrame, endFrame, lane, centerX, strumY, 
                           holdLengthPx, noteScale, alpha * 0.7f, 1.0f);
        }
    }
}
