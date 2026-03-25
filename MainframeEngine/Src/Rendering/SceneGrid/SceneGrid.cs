using System.Numerics;
using System.Runtime.InteropServices;
using Silk.NET.OpenGL;

namespace MainframeEngine;

/// <summary>
/// Base class for making scene grid
/// </summary>
public abstract class SceneGrid
{
    protected readonly Vector4 DefaultColor = new(1f, 1f, 1f, 0.1f);
    protected readonly Vector4 Red = new(1f, 0f, 0f, 1f);
    protected readonly Vector4 Yellow = new(1f, 1f, 0f, 1f);
    protected readonly Vector4 Blue = new(0f, 0f, 1f, 1f);

    protected readonly uint _vertexCount;
    private readonly GL? _gl;
    private uint _vertexArrayId;
    private readonly Shader? _shader;

    protected struct Vertex(float x, float y, float z, Vector4 color)
    {
        public Vector3 Position = new(x, y, z);
        public Vector4 Color = color;
    }

    protected SceneGrid(IRenderer renderer, uint vertexCount)
    {
        _vertexCount = vertexCount;

        if (renderer.Backend != RenderingBackend.OpenGL)
            return;

        _gl = renderer.GetGL();
        _shader = new Shader(_gl,
            "Content/Shaders/SceneGrid/SceneGrid.vert",
            "Content/Shaders/SceneGrid/SceneGrid.frag");
    }

    public void Draw(ICamera camera)
    {
        if (_gl is null) return;

        _gl.Enable(EnableCap.Blend);
        _gl.BlendFunc(BlendingFactor.SrcAlpha, BlendingFactor.OneMinusSrcAlpha);
        {
            _shader!.Use();
            _shader.SetUniform("uView", camera.ViewMatrix);
            _shader.SetUniform("uProjection", camera.ProjectionMatrix);

            _gl.BindVertexArray(_vertexArrayId);
            _gl.DrawArrays(PrimitiveType.Lines, 0, _vertexCount);
        }
        _gl.Disable(EnableCap.Blend);
    }

    protected unsafe void BuildVertexArray(Vertex* vertices)
    {
        if (_gl is null) return;

        var vertexBufferId = _gl.GenBuffer();
        _gl.BindBuffer(BufferTargetARB.ArrayBuffer, vertexBufferId);
        _gl.BufferData(
            BufferTargetARB.ArrayBuffer,
            (nuint)(_vertexCount * sizeof(Vertex)),
            vertices,
            BufferUsageARB.StaticDraw
        );

        _vertexArrayId = _gl.GenVertexArray();
        _gl.BindVertexArray(_vertexArrayId);
        _gl.VertexAttribPointer(
            0, 3, VertexAttribPointerType.Float, false,
            (uint)Marshal.SizeOf<Vertex>(),
            (void*)Marshal.OffsetOf<Vertex>(nameof(Vertex.Position))
        );
        _gl.EnableVertexAttribArray(0);
        _gl.VertexAttribPointer(
            1, 4, VertexAttribPointerType.Float, false,
            (uint)Marshal.SizeOf<Vertex>(),
            (void*)Marshal.OffsetOf<Vertex>(nameof(Vertex.Color))
        );
        _gl.EnableVertexAttribArray(1);
    }
}
