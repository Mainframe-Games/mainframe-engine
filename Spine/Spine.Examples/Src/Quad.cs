using System.Numerics;
using Mainframe.Silk;
using Silk.NET.OpenGL;
using Shader = Mainframe.Silk.Shader;

namespace SilkSpine;

public class Quad
{
    // csharpier-ignore
    private static readonly Vector3[] Vertices =
    [
        new( 0.5f,  0.5f, 0.0f),
        new( 0.5f, -0.5f, 0.0f),
        new(-0.5f, -0.5f, 0.0f),
        new(-0.5f,  0.5f, 0.0f)
    ];

    //Index data, uploaded to the EBO.
    // csharpier-ignore
    private static readonly uint[] Indices =
    [
        0, 1, 3,
        1, 2, 3
    ];
    
    //Vertex shaders are run on each vertex.
    private const string VertexShaderSource = """
                                              
                                                          #version 330 core //Using version GLSL version 3.3
                                                          layout (location = 0) in vec4 vPos;
                                                          
                                                          void main()
                                                          {
                                                              gl_Position = vec4(vPos.x, vPos.y, vPos.z, 1.0);
                                                          }
                                                          
                                              """;

    //Fragment shaders are run on each fragment/pixel of the geometry.
    private const string FragmentShaderSource = """
                                                
                                                            #version 330 core
                                                            out vec4 FragColor;
                                                    
                                                            void main()
                                                            {
                                                                FragColor = vec4(1.0f, 0.5f, 0.2f, 1.0f);
                                                            }
                                                            
                                                """;
    
    private readonly GL _gl;
    private readonly VertexArrayObject<Vector3, uint> _vertexArrayObject;
    private readonly Shader _shader;

    private Matrix4x4 ModelMatrix =>
        // scale
        Matrix4x4.CreateScale(new Vector3(200f, 100f, 0))
        // rotation
        * Matrix4x4.CreateRotationX(Mainframe.Math.DegreesToRadiansF(0))
        * Matrix4x4.CreateRotationY(Mainframe.Math.DegreesToRadiansF(0))
        * Matrix4x4.CreateRotationZ(Mainframe.Math.DegreesToRadiansF(0))
        // translation
        * Matrix4x4.CreateTranslation(Vector3.Zero);

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
            "Content/Shaders/Generic.vert", 
            "Content/Shaders/White.frag");
    }
    
    public unsafe void Draw(Matrix4x4 view, Matrix4x4 projection)
    {
        _shader.Use();
        _shader.SetUniform("uModel", ModelMatrix);
        _shader.SetUniform("uView", view);
        _shader.SetUniform("uProjection", projection);
        
        _vertexArrayObject.Bind();

        _gl.DrawElements(
            PrimitiveType.Triangles,
            (uint)Indices.Length,
            DrawElementsType.UnsignedInt,
            null
        );
    }
}