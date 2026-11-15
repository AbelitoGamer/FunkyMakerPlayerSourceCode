using Microsoft.Xna.Framework.Input;
using Microsoft.Xna.Framework.Graphics;

namespace FunkyMakerPlayer.Editors;

public interface IEditor : FunkyMakerPlayer.UI.IDirectScreen
{
    void Update(float dt, KeyboardState kb);
    bool BackPressed { get; }
    bool IsModified { get; }
}
