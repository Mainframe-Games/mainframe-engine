using System.Numerics;
using Mainframe.Silk;
using Silk.NET.OpenGL;
using Shader = Mainframe.Silk.Shader;

namespace SilkSpine;

public class Grid
{
    private readonly GL _gl;
    private readonly VertexArrayObject<float, uint> _vertexArrayObject;
    private readonly Shader _shader;
    private readonly uint _verticesLength;
    private readonly uint _indicesLength;

    public Grid(GL gl, uint slices = 10)
    {
        _gl = gl;
        
        var vertices = new float[(slices + 1) * (slices + 1) * 3];

        var index = 0;
        for (int j = 0; j <= slices; ++j)
        {
            for (int i = 0; i <= slices; ++i)
            {
                var x = (float)i / slices;
                var z = (float)j / slices;
                vertices[index++] = x;
                vertices[index++] = 0;
                vertices[index++] = z;
            }
        }

        var indices = new uint[slices * slices * 8];
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

        _verticesLength = (uint)vertices.Length;
        _indicesLength = (uint)indices.Length;
        
        _shader = new Shader(gl,
            "Content/Shaders/Generic.vert", 
            "Content/Shaders/White.frag");
    }
    
    private Matrix4x4 ModelMatrix =>
        // scale
        Matrix4x4.CreateScale(new Vector3(100))
        // rotation
        * Matrix4x4.CreateRotationX(Mainframe.Math.DegreesToRadiansF(0))
        * Matrix4x4.CreateRotationY(Mainframe.Math.DegreesToRadiansF(0))
        * Matrix4x4.CreateRotationZ(Mainframe.Math.DegreesToRadiansF(0))
        // translation
        * Matrix4x4.CreateTranslation(Vector3.Zero);

    public unsafe void Draw(Matrix4x4 view, Matrix4x4 projection)
    {
        _shader.Use();
        _shader.SetUniform("uModel", ModelMatrix);
        _shader.SetUniform("uView", view);
        _shader.SetUniform("uProjection", projection);
        
        _vertexArrayObject.Bind();
        // _gl.DrawElements(GLEnum.Lines, _length, DrawElementsType.UnsignedInt, null);
        // _gl.DrawArrays(PrimitiveType.Lines, 0, _verticesLength);
        _gl.DrawElements(PrimitiveType.Lines, _indicesLength, DrawElementsType.UnsignedInt, null);
        // _gl.DrawElements(PrimitiveType.Triangles, _length, DrawElementsType.UnsignedInt, null);
    }
}