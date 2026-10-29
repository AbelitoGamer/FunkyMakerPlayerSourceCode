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
            
            if (pieceFrame == null || endFrame == null)
            {
                Console.WriteLine($"[CodenameRenderer] ERROR: NULL frames for lane {lane}! piece={pieceFrame == null}, end={endFrame == null}");
                return;
            }
            
            // Get source rectangles from atlas frames
            var pieceSrc = pieceFrame.Source;
            var endSrc = endFrame.Source;
            
            float noteScale = _style.NoteScale;
            
            // Codename Engine: hold starts from arrow center + half arrow height
            // Note.hx line 227: daNote.y += daNote.height * 0.5
            float arrowHeight = pieceSrc.Height * noteScale;
            float holdStartY = arrowY + (arrowHeight * 0.5f);
            
            // Draw hold using Codename Engine's approach - one stretched sprite
            DrawCodenameHold(sb, pieceFrame, endFrame, lane, centerX, holdStartY, 
                           holdLengthPx, noteScale, alpha * 0.6f, scrollSpeed);
        }
        
        /// <summary>
        /// C# translation of Codename Engine Note.hx updateSustain() method (lines 237-243)
        /// 
        /// Key insight from investigation:
        /// - Codename Engine divides long holds into multiple Note OBJECTS in StrumLine.hx spawnNote()
        /// - Each Note object has its own strumTime (base + offset) so they appear staggered
        /// - Each Note object renders ITSELF as one stretched sprite via scale.y
        /// - We receive complete holds, so we render them as single stretched sprites
        /// 
        /// Original Haxe (Note.hx lines 237-243):
        ///   scale.y = (sustainLength * 0.45 * Math.abs(scrollSpeed)) / frameHeight;
        ///   updateHitbox();
        ///   scale.y += gapFix / frameHeight;
        /// 
        /// gapFix creates overlap to prevent gaps. The body extends BY gapFix pixels.
        /// The endcap should start where the body ACTUALLY ends (after gapFix extension).
        /// </summary>
        private void DrawCodenameHold(SpriteBatch sb, AtlasFrame pieceFrame, AtlasFrame endFrame,
                                     int lane, float centerX, float holdStartY, float holdLengthPx,
                                     float noteScale, float alphaTint, float scrollSpeed)
        {
            var pieceSrc = pieceFrame.Source;
            var endSrc = endFrame.Source;
            
            // Width and height for piece (body)
            float scaledWidth = pieceSrc.Width * noteScale;
            
            // End cap has its own dimensions (often taller than piece)
            float scaledEndWidth = endSrc.Width * noteScale;
            float scaledEndHeight = endSrc.Height * noteScale;
            
            // Note.hx line 224-226: gapFix = 1 when antialiasing enabled
            // This extends the body by gapFix pixels to create overlap with endcap
            float gapFix = 1f;
            
            float frameHeight = pieceSrc.Height;
            
            // Calculate scale.y INCLUDING gapFix (Note.hx line 237-243)
            // scale.y = sustainLength / frameHeight + gapFix / frameHeight
            // The body stretches to: frameHeight * scale.y * noteScale
            // = frameHeight * (holdLengthPx / frameHeight + gapFix / frameHeight) * noteScale
            // = (holdLengthPx + gapFix) * noteScale
            float scaleY = (holdLengthPx / frameHeight) + (gapFix / frameHeight);
            float stretchedBodyHeight = frameHeight * scaleY * noteScale;
            
            Color tint = Color.White * alphaTint;
            
            // Body piece dimensions
            int intX = (int)(centerX - scaledWidth / 2f);
            int intWidth = (int)Math.Ceiling(scaledWidth);
            int bodyHeight = (int)Math.Ceiling(stretchedBodyHeight);
            
            // End cap dimensions (use its own width to preserve aspect ratio)
            int endX = (int)(centerX - scaledEndWidth / 2f);
            int endWidth = (int)Math.Ceiling(scaledEndWidth);
            int capHeight = (int)Math.Ceiling(scaledEndHeight);
            
            if (!_downscroll)
            {
                // UPSCROLL: hold extends downward
                int bodyTop = (int)holdStartY;
                
                // Draw stretched body piece
                // It extends from holdStartY to (holdStartY + stretchedBodyHeight)
                // stretchedBodyHeight = (holdLengthPx + gapFix) * noteScale
                sb.Draw(pieceFrame.Texture,
                    new Rectangle(intX, bodyTop, intWidth, bodyHeight),
                    pieceSrc, tint);
                
                // Draw endcap with slight overlap to prevent gap
                // Position it slightly before body ends to create seamless connection
                int capY = bodyTop + bodyHeight - (int)(gapFix * noteScale);
                sb.Draw(endFrame.Texture,
                    new Rectangle(endX, capY, endWidth, capHeight),
                    endSrc, tint);
            }
            else
            {
                // DOWNSCROLL: hold extends upward (flipped)
                int bodyBottom = (int)holdStartY;
                int bodyTop = bodyBottom - bodyHeight;
                
                // Draw stretched body piece (flipped)
                sb.Draw(pieceFrame.Texture,
                    new Rectangle(intX, bodyTop, intWidth, bodyHeight),
                    pieceSrc, tint, 0f, Vector2.Zero, SpriteEffects.FlipVertically, 0f);
                
                // Draw endcap with overlap (flipped)
                int capY = bodyTop - capHeight + (int)(gapFix * noteScale);
                sb.Draw(endFrame.Texture,
                    new Rectangle(endX, capY, endWidth, capHeight),
                    endSrc, tint, 0f, Vector2.Zero, SpriteEffects.FlipVertically, 0f);
            }
        }
        
        public void DrawActiveHold(SpriteBatch sb, int lane, float centerX, float strumY, 
                                  float holdLengthPx, float laneWidth, float alpha)
        {
            var pieceFrame = _holdPieceFrames[lane];
            var endFrame = _holdEndFrames[lane];
            
            if (pieceFrame == null || endFrame == null) return;
            
            float noteScale = _style.NoteScale;
            var pieceSrc = pieceFrame.Source;
            
            // Active holds: use SAME rendering as upcoming holds for visual consistency
            // Calculate holdStartY the same way as DrawUpcomingHold
            float arrowHeight = pieceSrc.Height * noteScale;
            float holdStartY = strumY + (arrowHeight * 0.5f);
            
            // REMOVED HARDCODED scrollSpeed = 1.0f
            // The scrollSpeed parameter in DrawCodenameHold is not actually used in the function
            // (it was a leftover parameter from earlier code). The holdLengthPx already comes
            // pre-scaled by EffSpeed from NoteRenderer, so we just pass 1.0 as a dummy value.
            // The visual "consumption speed" is controlled by how fast holdLengthPx decreases,
            // which is determined by the songTime update rate, not by this parameter.
            float scrollSpeed = 1.0f;
            
            // Use same DrawCodenameHold to ensure visual consistency
            DrawCodenameHold(sb, pieceFrame, endFrame, lane, centerX, holdStartY, 
                           holdLengthPx, noteScale, alpha, scrollSpeed);
        }
    }
}
