using System.Numerics;
using Mainframe.Silk;
using Silk.NET.OpenGL;

namespace SilkSpine;

public class Grid
{
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
    private readonly VertexArrayObject<float, uint> _vertexArrayObject;
    private readonly uint _length;
    private readonly uint _shaderId;

    public Grid(GL gl, uint slices = 10)
    {
        _gl = gl;
        
        var vertices = new float[(slices + 1)* (slices + 1) * 3];
        var indices = new uint[slices * slices * 8];

        var index = 0;
        for (int j = 0; j <= slices; ++j)
        {
            for (int i = 0; i <= slices; ++i)
            {
                var x = (float)i / slices;
                var y = 0;
                var z = (float)j / slices;
                vertices[index++] = x;
                vertices[index++] = y;
                vertices[index++] = z;
            }
        }

        index = 0;
        for (int j = 0; j < slices; j++)
        {
            for (int i = 0; i < slices; i++)
            {
                var row1 = j * (slices + 1);
                var row2 = (j + 1) * (slices + 1);
                
                indices[index++] = (uint)(row1 + i);
                indices[index++] = (uint)(row1 + i + 1);
                indices[index++] = (uint)(row1 + i + 1);
                indices[index++] = (uint)(row2 + i + 1);
                
                indices[index++] = (uint)(row2 + i + 1);
                indices[index++] = (uint)(row2 + i);
                indices[index++] = (uint)(row2 + i);
                indices[index++] = (uint)(row1 + i);
            }
        }
        
        var vertexBuffer = new BufferObject<float>(gl, vertices, BufferTargetARB.ArrayBuffer);
        var indexBuffer = new BufferObject<uint>(gl, indices, BufferTargetARB.ElementArrayBuffer);
        _vertexArrayObject = new VertexArrayObject<float, uint>(gl, vertexBuffer, indexBuffer);
        
        _vertexArrayObject.VertexAttributePointer(
            0, 
            3,
            VertexAttribPointerType.Float, 
            3,
            0);

        _length = (uint)indices.Length;
        
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
        // _gl.DrawElements(GLEnum.Lines, _length, DrawElementsType.UnsignedInt, 0);
        // _gl.DrawElements(PrimitiveType.Lines, _length, DrawElementsType.UnsignedInt, null);
        _gl.DrawElements(PrimitiveType.Triangles, _length, DrawElementsType.UnsignedInt, null);
    }
}