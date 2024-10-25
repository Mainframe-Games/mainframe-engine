using Silk.NET.Maths;
using Silk.NET.Windowing;

namespace SilkTest.Examples;

internal interface IExample
{
    void OnLoad(IWindow window);
    void OnUpdate(double deltaTime);
    void OnRender(double deltaTime);
    void OnFramebufferResize(Vector2D<int> newSize);
    void OnClose();
}
