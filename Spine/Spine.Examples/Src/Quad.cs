using System.Numerics;
using Mainframe.Silk;
using Silk.NET.OpenGL;

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
    private readonly uint _shaderId;

    // private Shader _shader;

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
        
        //Creating a vertex shader.
        uint vertexShader = gl.CreateShader(ShaderType.VertexShader);
        gl.ShaderSource(vertexShader, VertexShaderSource);
        gl.CompileShader(vertexShader);

        //Creating a fragment shader.
        uint fragmentShader = gl.CreateShader(ShaderType.FragmentShader);
        gl.ShaderSource(fragmentShader, FragmentShaderSource);
        gl.CompileShader(fragmentShader);
        
        //Combining the shaders under one shader program.
        _shaderId = gl.CreateProgram();
        gl.AttachShader(_shaderId, vertexShader);
        gl.AttachShader(_shaderId, fragmentShader);
        gl.LinkProgram(_shaderId);

        //Checking the linking for errors.
        gl.GetProgram(_shaderId, GLEnum.LinkStatus, out var status);
        if (status == 0)
        {
            Console.WriteLine($"Error linking shader {gl.GetProgramInfoLog(_shaderId)}");
        }

        //Delete the no longer useful individual shaders;
        gl.DetachShader(_shaderId, vertexShader);
        gl.DetachShader(_shaderId, fragmentShader);
        gl.DeleteShader(vertexShader);
        gl.DeleteShader(fragmentShader);
    }

    public unsafe void Draw()
    {
        _vertexArrayObject.Bind();
        _gl.UseProgram(_shaderId);

        _gl.DrawElements(
            PrimitiveType.Triangles,
            (uint)Indices.Length,
            DrawElementsType.UnsignedInt,
            null
        );
    }
}