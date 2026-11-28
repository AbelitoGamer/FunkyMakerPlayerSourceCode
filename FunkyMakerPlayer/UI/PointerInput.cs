using System;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Input;
using Microsoft.Xna.Framework.Input.Touch;

namespace FunkyMakerPlayer.UI;

/// <summary>
/// The pointer menus read instead of <see cref="Mouse.GetState()"/>. On desktop it is the mouse.
/// On Android (where Mouse.GetState never sees touches) it is the touch screen turned into a mouse,
/// with phone semantics:
/// <list type="bullet">
/// <item>a finger on the screen moves the pointer (hover) but does not press the button;</item>
/// <item>lifting it without dragging is a click: the left button reads Pressed for one frame;</item>
/// <item>dragging scrolls (mouse wheel, and <see cref="DragDeltaY"/> for 1:1 lists) and never clicks;</item>
/// <item>holding still for <see cref="LongPressSeconds"/> is a right click (options menus), not a left one.</item>
/// </list>
/// Game1 calls <see cref="Update"/> once per frame, right after <c>TouchControls.Update</c>, with the
/// touches that call read (MonoGame ages touches on every <c>TouchPanel.GetState()</c>, so it is read
/// once). Touches that start on a visible on-screen D-pad / A / B button are left to that button.
/// </summary>
public static class PointerInput
{
    public const float LongPressSeconds = 0.5f;

    /// <summary>True on platforms where the pointer is a finger (Android).</summary>
    public static bool IsTouch => OperatingSystem.IsAndroid() || UiScale.SimulatedTouch;

    /// <summary>Label of the screens' back buttons: no keyboard key on phones.</summary>
    public static string BackLabel => IsTouch ? "<  Back" : "ESC  Back";

#if ANDROID
    private static MouseState _state;
    private static int _touchId = -1;
    private static Vector2 _start, _last;
    private static bool _dragged, _longPressed;
    private static float _held;
    private static int _wheel;
    private static float _wheelAccum;
    private static Point _pos = new(-10000, -10000);

    /// <summary>Vertical finger movement this frame while dragging (window pixels, + = finger moved down).</summary>
    public static float DragDeltaY { get; private set; }

    /// <summary>A finger is dragging (past the tap threshold) right now.</summary>
    public static bool IsDragging => _touchId >= 0 && _dragged;

    /// <summary>A finger is on the screen and tracked by the pointer.</summary>
    public static bool IsDown => _touchId >= 0;

    public static void Update(TouchCollection touches, Func<Point, bool>? ownedByButton, int screenHeight, float dt)
    {
        DragDeltaY = 0f;
        bool left = false, right = false;

        TouchLocation? cur = null;
        foreach (var t in touches)
        {
            if (t.State is not (TouchLocationState.Pressed or TouchLocationState.Moved)) continue;
            if (_touchId >= 0)
            {
                if (t.Id == _touchId) { cur = t; break; }
                continue;
            }
            var pt = new Point((int)t.Position.X, (int)t.Position.Y);
            if (ownedByButton != null && ownedByButton(pt)) continue;
            cur = t;
            break;
        }

        // Fingers wobble a few millimetres while tapping: scale with the screen (high-DPI phones).
        float dragThreshold = Math.Max(24f, screenHeight * 0.035f);
        float wheelStep = Math.Max(20f, screenHeight * 0.04f); // finger px per wheel notch

        if (cur is TouchLocation c)
        {
            var p = c.Position;
            if (c.Id != _touchId)
            {
                _touchId = c.Id;
                _start = _last = p;
                _dragged = _longPressed = false;
                _held = 0f;
                _wheelAccum = 0f;
            }
            if (!_dragged && Vector2.Distance(p, _start) > dragThreshold) _dragged = true;
            if (_dragged)
            {
                DragDeltaY = p.Y - _last.Y;
                // Finger down = content follows = like scrolling the wheel up.
                _wheelAccum += DragDeltaY;
                while (_wheelAccum > wheelStep) { _wheel += 120; _wheelAccum -= wheelStep; }
                while (_wheelAccum < -wheelStep) { _wheel -= 120; _wheelAccum += wheelStep; }
            }
            else if (!_longPressed)
            {
                _held += dt;
                if (_held >= LongPressSeconds)
                {
                    _longPressed = true;
                    right = true; // Pressed for this frame only
                }
            }
            _last = p;
            _pos = new Point((int)p.X, (int)p.Y);
        }
        else if (_touchId >= 0)
        {
            // The finger was lifted: a tap clicks where it was; a drag or long press doesn't.
            if (!_dragged && !_longPressed)
            {
                left = true; // Pressed for this frame only
            }
            else if (_dragged)
            {
                _pos = new Point(-10000, -10000); // nothing stays hovered after a scroll
            }
            _touchId = -1;
        }

        _state = new MouseState(_pos.X, _pos.Y, _wheel,
            left ? ButtonState.Pressed : ButtonState.Released,
            ButtonState.Released,
            right ? ButtonState.Pressed : ButtonState.Released,
            ButtonState.Released, ButtonState.Released);
    }

    public static MouseState GetState() => _state;

    /// <summary>
    /// For drag-based screens (HUD editor): the left button is held while the finger is down,
    /// like a real mouse, with no tap / long-press translation.
    /// </summary>
    public static MouseState GetRawState() => new(_pos.X, _pos.Y, _wheel,
        _touchId >= 0 ? ButtonState.Pressed : ButtonState.Released,
        ButtonState.Released, ButtonState.Released, ButtonState.Released, ButtonState.Released);
#else
    public static MouseState GetRawState() => Mouse.GetState();
    public static float DragDeltaY => 0f;
    public static bool IsDragging => false;
    public static bool IsDown => false;
    public static MouseState GetState() => Mouse.GetState();
#endif
}
