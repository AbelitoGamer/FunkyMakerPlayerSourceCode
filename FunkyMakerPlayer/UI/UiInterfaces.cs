using Microsoft.Xna.Framework.Graphics;

namespace FunkyMakerPlayer.UI;

/// <summary>
/// Full-window screen rendered directly to the swapchain without the gameplay render target.
/// </summary>
public interface IDirectScreen
{
    void Draw(SpriteBatch spriteBatch, int screenWidth, int screenHeight);
}

/// <summary>
/// Modal sub-menu overlay rendered above Freeplay or paused gameplay.
/// </summary>
public interface IScreenOverlay : IDirectScreen
{
    bool IsActive { get; }
}
