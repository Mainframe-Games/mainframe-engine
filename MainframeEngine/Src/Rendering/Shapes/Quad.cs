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
    
    private readonly GL _gl;
    private readonly VertexArrayObject<Vector3, uint> _vertexArrayObject;
    private readonly Shader _shader;

    public Quad(GL gl)
    {
        _gl = gl;
        
        var vertexBuffer = new BufferObject<Vector3>(gl, Vertices, BufferTargetARB.ArrayBuffer);
        var indexBuffer = new BufferObject<uint>(gl, Indices, BufferTargetARB.ElementArrayBuffer);
        _vertexArrayObject = new VertexArrayObject<Vector3, uint>(gl, vertexBuffer, indexBuffer);
        
        _vertexArrayObject.VertexAttributePointer(
            0,
            3,
            VertexAttribPointerType.Float, 
            1,
            0);
        
        _shader = new Shader(gl,
            "Content/Shaders/Shapes/Shapes.vert", 
            "Content/Shaders/Shapes/Shapes.frag");

        Scale = new Vector3(1, 1, 0);
    }
    
    public unsafe void Draw(ICamera camera)
    {
        _shader.Use();
        _shader.SetUniform("uModel", ModelMatrix);
        _shader.SetUniform("uView", camera.ViewMatrix);
        _shader.SetUniform("uProjection", camera.ProjectionMatrix);
        
        _vertexArrayObject.Bind();

        _gl.DrawElements(
            PrimitiveType.Triangles,
            (uint)Indices.Length,
            DrawElementsType.UnsignedInt,
            null
        );
    }
}