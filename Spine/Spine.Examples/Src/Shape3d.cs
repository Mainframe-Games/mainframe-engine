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
        //X    Y      Z
        -0.5f, -0.5f, -0.5f,
        0.5f, -0.5f, -0.5f,
        0.5f,  0.5f, -0.5f,
        0.5f,  0.5f, -0.5f,
        -0.5f,  0.5f, -0.5f,
        -0.5f, -0.5f, -0.5f,

        -0.5f, -0.5f,  0.5f,
        0.5f, -0.5f,  0.5f,
        0.5f,  0.5f,  0.5f,
        0.5f,  0.5f,  0.5f,
        -0.5f,  0.5f,  0.5f,
        -0.5f, -0.5f,  0.5f,

        -0.5f,  0.5f,  0.5f,
        -0.5f,  0.5f, -0.5f,
        -0.5f, -0.5f, -0.5f,
        -0.5f, -0.5f, -0.5f,
        -0.5f, -0.5f,  0.5f,
        -0.5f,  0.5f,  0.5f,

        0.5f,  0.5f,  0.5f,
        0.5f,  0.5f, -0.5f,
        0.5f, -0.5f, -0.5f,
        0.5f, -0.5f, -0.5f,
        0.5f, -0.5f,  0.5f,
        0.5f,  0.5f,  0.5f,

        -0.5f, -0.5f, -0.5f,
        0.5f, -0.5f, -0.5f,
        0.5f, -0.5f,  0.5f,
        0.5f, -0.5f,  0.5f,
        -0.5f, -0.5f,  0.5f,
        -0.5f, -0.5f, -0.5f,

        -0.5f,  0.5f, -0.5f,
        0.5f,  0.5f, -0.5f,
        0.5f,  0.5f,  0.5f,
        0.5f,  0.5f,  0.5f,
        -0.5f,  0.5f,  0.5f,
        -0.5f,  0.5f, -0.5f
    ];

    // csharpier-ignore
    private static readonly uint[] Indices =
    [
        0, 1, 3,
        1, 2, 3
    ];

    private BufferObject<uint> Ebo;
    private BufferObject<float> Vbo;
    private readonly VertexArrayObject<float, uint> VaoCube;
    private readonly GL _gl;

    private readonly Shader Shader;

    public Box3d(GL gl)
    {
        _gl = gl;
        
        Ebo = new BufferObject<uint>(gl, Indices, BufferTargetARB.ElementArrayBuffer);
        Vbo = new BufferObject<float>(gl, Vertices, BufferTargetARB.ArrayBuffer);
        VaoCube = new VertexArrayObject<float, uint>(gl, Vbo, Ebo);
        
        VaoCube.VertexAttributePointer(0, 3, VertexAttribPointerType.Float, 3, 0);
        
        Shader = new Shader(
            gl,
            "Content/Shaders/AmbientLighting/shader.vert",
            "Content/Shaders/AmbientLighting/shader.frag"
        );
    }

    public void Render(Matrix4x4 viewMatrix, Matrix4x4 projectionMatrix)
    {
        VaoCube.Bind();
        Shader.Use();

        var model =
            Matrix4x4.CreateScale(Vector3.One * 100)
             * Matrix4x4.CreateFromQuaternion(
                Quaternion.CreateFromYawPitchRoll(MathHelper.DegreesToRadiansF(45), 0, 0))
             * Matrix4x4.CreateTranslation(-100, 0, 0);
        
        //Slightly rotate the cube to give it an angled face to look at
        Shader.SetUniform("uModel", model);
        Shader.SetUniform("uView", viewMatrix);
        Shader.SetUniform("uProjection", projectionMatrix);
        // Shader.SetUniform("objectColor", new Vector3(1.0f, 0.5f, 0.31f));
        // Shader.SetUniform("lightColor", Vector3.One);
        
        //We're drawing with just vertices and no indicies, and it takes 36 verticies to have a six-sided textured cube
        // _gl.DrawArrays(PrimitiveType.Triangles, 0, 36);
        _gl.DrawArrays(PrimitiveType.Triangles, 0, (uint)Vertices.Length);
    }
}