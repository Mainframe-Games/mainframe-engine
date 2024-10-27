using System.Numerics;
using Mainframe.Silk;
using Silk.NET.OpenGL;
using Shader = Mainframe.Silk.Shader;

namespace SilkSpine;

public class Box3d
{
    // csharpier-ignore
    private static readonly float[] Vertices =
    [
        //X    Y      Z     U   V
        -0.5f, -0.5f, -0.5f,  0.0f, 0.0f,
        0.5f, -0.5f, -0.5f,  1.0f, 0.0f,
        0.5f,  0.5f, -0.5f,  1.0f, 1.0f,
        0.5f,  0.5f, -0.5f,  1.0f, 1.0f,
        -0.5f,  0.5f, -0.5f,  0.0f, 1.0f,
        -0.5f, -0.5f, -0.5f,  0.0f, 0.0f,

        -0.5f, -0.5f,  0.5f,  0.0f, 0.0f,
        0.5f, -0.5f,  0.5f,  1.0f, 0.0f,
        0.5f,  0.5f,  0.5f,  1.0f, 1.0f,
        0.5f,  0.5f,  0.5f,  1.0f, 1.0f,
        -0.5f,  0.5f,  0.5f,  0.0f, 1.0f,
        -0.5f, -0.5f,  0.5f,  0.0f, 0.0f,

        -0.5f,  0.5f,  0.5f,  1.0f, 0.0f,
        -0.5f,  0.5f, -0.5f,  1.0f, 1.0f,
        -0.5f, -0.5f, -0.5f,  0.0f, 1.0f,
        -0.5f, -0.5f, -0.5f,  0.0f, 1.0f,
        -0.5f, -0.5f,  0.5f,  0.0f, 0.0f,
        -0.5f,  0.5f,  0.5f,  1.0f, 0.0f,

        0.5f,  0.5f,  0.5f,  1.0f, 0.0f,
        0.5f,  0.5f, -0.5f,  1.0f, 1.0f,
        0.5f, -0.5f, -0.5f,  0.0f, 1.0f,
        0.5f, -0.5f, -0.5f,  0.0f, 1.0f,
        0.5f, -0.5f,  0.5f,  0.0f, 0.0f,
        0.5f,  0.5f,  0.5f,  1.0f, 0.0f,

        -0.5f, -0.5f, -0.5f,  0.0f, 1.0f,
        0.5f, -0.5f, -0.5f,  1.0f, 1.0f,
        0.5f, -0.5f,  0.5f,  1.0f, 0.0f,
        0.5f, -0.5f,  0.5f,  1.0f, 0.0f,
        -0.5f, -0.5f,  0.5f,  0.0f, 0.0f,
        -0.5f, -0.5f, -0.5f,  0.0f, 1.0f,

        -0.5f,  0.5f, -0.5f,  0.0f, 1.0f,
        0.5f,  0.5f, -0.5f,  1.0f, 1.0f,
        0.5f,  0.5f,  0.5f,  1.0f, 0.0f,
        0.5f,  0.5f,  0.5f,  1.0f, 0.0f,
        -0.5f,  0.5f,  0.5f,  0.0f, 0.0f,
        -0.5f,  0.5f, -0.5f,  0.0f, 1.0f
    ];

    // csharpier-ignore
    private static readonly uint[] Indices =
    [
        0, 1, 3,
        1, 2, 3
    ];

    private readonly GL _gl;
    private readonly VertexArrayObject<float, uint> _vertexArray;
    private readonly Shader _shader;
    
    private Matrix4x4 ModelMatrix =>
        // scale
        Matrix4x4.CreateScale(new Vector3(500f, 1f, 500))
        // rotation
        * Matrix4x4.CreateRotationX(Mainframe.Math.DegreesToRadiansF(0))
        * Matrix4x4.CreateRotationY(Mainframe.Math.DegreesToRadiansF(0))
        * Matrix4x4.CreateRotationZ(Mainframe.Math.DegreesToRadiansF(0))
        // translation
        * Matrix4x4.CreateTranslation(Vector3.Zero);

    public Box3d(GL gl)
    {
        _gl = gl;
        
        var vertexBuffer = new BufferObject<float>(gl, Vertices, BufferTargetARB.ArrayBuffer);
        var indexBuffer = new BufferObject<uint>(gl, Indices, BufferTargetARB.ElementArrayBuffer);
        _vertexArray = new VertexArrayObject<float, uint>(gl, vertexBuffer, indexBuffer);

        _vertexArray.VertexAttributePointer(0, 3, VertexAttribPointerType.Float, 5, 0);
        _vertexArray.VertexAttributePointer(1, 2, VertexAttribPointerType.Float, 5, 3);

        _shader = new Shader(gl,
            "Content/Shaders/Generic.vert", 
            "Content/Shaders/White.frag");
    }

    public void Draw(Matrix4x4 view, Matrix4x4 projection)
    {
        _shader.Use();
        
        _shader.Use();
        _shader.SetUniform("uModel", ModelMatrix);
        _shader.SetUniform("uView", view);
        _shader.SetUniform("uProjection", projection);
        
        _vertexArray.Bind();

        _gl.DrawArrays(PrimitiveType.Triangles, 0, 36);
    }
}