using Microsoft.Xna.Framework.Input;
using Microsoft.Xna.Framework.Graphics;

namespace FunkyMakerPlayer.Editors;

public interface IEditor
{
    void Update(float dt, KeyboardState kb);
    void Draw(SpriteBatch sb, int screenWidth, int screenHeight);
    bool BackPressed { get; }
    bool IsModified { get; }
}
