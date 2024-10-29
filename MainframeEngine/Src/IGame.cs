using System.Numerics;
using Silk.NET.Input;
using Silk.NET.OpenGL;
using Silk.NET.Windowing;

namespace MainframeEngine;

public interface IGame
{
    void OnLoad(IWindow window, GL gl, IInputContext inputContext);
    void OnFramebufferResize(Vector2 newSize);
    void OnImGui(GameTime gameTime);
    void OnUpdate(GameTime gameTime);
    void OnRender(GameTime gameTime);
    void OnClose();
}
