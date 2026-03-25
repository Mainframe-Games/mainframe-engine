using Silk.NET.Maths;
using Silk.NET.OpenGL;

namespace MainframeEngine;

internal sealed class OpenGLRenderer : IRenderer
{
    private readonly GL _gl;

    public RenderingBackend Backend => RenderingBackend.OpenGL;

    internal OpenGLRenderer(GL gl)
    {
        _gl = gl;
    }

    public void OnResize(Vector2D<int> newSize) => _gl.Viewport(newSize);

    public void BeginFrame() { }
    public void EndFrame() { }

    public void SetClearColor(float r, float g, float b, float a = 1f) => _gl.ClearColor(r, g, b, a);

    public void Clear() => _gl.Clear(ClearBufferMask.ColorBufferBit | ClearBufferMask.DepthBufferBit);

    public void EnableDepthTest() => _gl.Enable(EnableCap.DepthTest);
    public void DisableDepthTest() => _gl.Disable(EnableCap.DepthTest);

    public GL GetGL() => _gl;

    public void Dispose() => _gl.Dispose();
}
