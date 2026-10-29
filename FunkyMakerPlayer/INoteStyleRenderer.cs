using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;

namespace FunkyMakerPlayer
{
    /// <summary>
    /// Interface for different notestyle rendering implementations.
    /// Each engine format (FMM, Codename, Psych, etc.) implements this interface.
    /// </summary>
    public interface INoteStyleRenderer
    {
        /// <summary>
        /// Renders a single note arrow at the specified position.
        /// </summary>
        void DrawArrow(SpriteBatch sb, int lane, float x, float y, float scale, float alpha, Color tint);
        
        /// <summary>
        /// Renders an upcoming hold note (not yet hit).
        /// </summary>
        void DrawUpcomingHold(SpriteBatch sb, int lane, float centerX, float arrowY, 
                             float holdLengthPx, float strumY, float laneWidth, 
                             float alpha, float scrollSpeed, float bpm);
        
        /// <summary>
        /// Renders an active hold note (being held down after hitting the arrow).
        /// </summary>
        void DrawActiveHold(SpriteBatch sb, int lane, float centerX, float strumY, 
                           float holdLengthPx, float laneWidth, float alpha);
        
        /// <summary>
        /// Renders a receptor/strum at the judgment line.
        /// </summary>
        void DrawReceptor(SpriteBatch sb, int lane, float x, float y, int state, 
                         float scale, float alpha);
        
        /// <summary>
        /// Gets the engine name for debugging/logging.
        /// </summary>
        string GetEngineName();
    }
}
