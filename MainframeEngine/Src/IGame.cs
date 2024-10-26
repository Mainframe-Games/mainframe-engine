using System.Numerics;
using Silk.NET.Input;
using Silk.NET.OpenGL;

namespace MainframeEngine;

public interface IGame
{
    void OnLoad(GL gl);
    void OnFramebufferResize(Vector2 newSize);
    void OnUpdate(GameTime gameTime);
    void OnRender(GameTime gameTime);
    void OnClose();
    void OnKeyDown(IKeyboard keyboard, Key key, int arg3);
}
