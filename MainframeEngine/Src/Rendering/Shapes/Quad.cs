using System.Numerics;
using Silk.NET.OpenGL;

namespace MainframeEngine;

public class Quad : ShapeBase
{
    // csharpier-ignore
    private static readonly Vector3[] Vertices =
    [
        new( 0.5f,  0.5f, 0.0f),
        new( 0.5f, -0.5f, 0.0f),
        new(-0.5f, -0.5f, 0.0f),
        new(-0.5f,  0.5f, 0.0f)
    ];

    // csharpier-ignore
    private static readonly uint[] Indices =
    [
        0, 1, 3,
        1, 2, 3
    ];

    private readonly GL? _gl;
    private readonly VertexArrayObject<Vector3, uint>? _vertexArrayObject;
    private readonly Shader? _shader;

    public Quad(IRenderer renderer)
    {
        if (renderer.Backend != RenderingBackend.OpenGL)
        {
            Scale = new Vector3(1, 1, 0);
            return;
        }

        _gl = renderer.GetGL();

        var vertexBuffer = new BufferObject<Vector3>(_gl, Vertices, BufferTargetARB.ArrayBuffer);
        var indexBuffer = new BufferObject<uint>(_gl, Indices, BufferTargetARB.ElementArrayBuffer);
        _vertexArrayObject = new VertexArrayObject<Vector3, uint>(_gl, vertexBuffer, indexBuffer);

        _vertexArrayObject.VertexAttributePointer(0, 3, VertexAttribPointerType.Float, 1, 0);

        _shader = new Shader(_gl,
            "Content/Shaders/Shapes/Shapes.vert",
            "Content/Shaders/Shapes/Shapes.frag");

        Scale = new Vector3(1, 1, 0);
    }

    public unsafe void Draw(ICamera camera)
    {
        if (_gl is null) return;

        _shader!.Use();
        _shader.SetUniform("uModel", ModelMatrix);
        _shader.SetUniform("uView", camera.ViewMatrix);
        _shader.SetUniform("uProjection", camera.ProjectionMatrix);

        _vertexArrayObject!.Bind();
        _gl.DrawElements(PrimitiveType.Triangles, (uint)Indices.Length, DrawElementsType.UnsignedInt, null);
    }
}
