using System.Numerics;
using Mainframe.Silk;
using Silk.NET.OpenGL;
using Shader = Mainframe.Silk.Shader;

namespace SilkSpine;

public class Grid
{
    private readonly GL _gl;
    private readonly Shader _shader;
    private readonly BufferObject<float> _vertexBuffer;
    private readonly uint _vertexArrayId;

    private readonly uint _count;
    
    public bool Is2D { get; set; }

    private Matrix4x4 ModelMatrix => Matrix4x4.CreateRotationX(Mainframe.Math.DegreesToRadiansF(Is2D ? 90 : 0));
    
    public unsafe Grid(GL gl)
    {
        _gl = gl;

        // src: https://github.com/IMCGKN/Grid_OpenGL_Cpp/blob/main/src/main.cpp
        const int gridSize = 200;
        var vertices = new float[(gridSize * 6 + gridSize * 6) * 2];
        var vertexIndex = 0;
        
        var count = 0;
        var j = gridSize;
        while(count < gridSize * 2) {
            vertices[vertexIndex++] = gridSize * 1;
            vertices[vertexIndex++] = 0;
            vertices[vertexIndex++] = -(j * 1);
            vertices[vertexIndex++] = -(gridSize * 1);
            vertices[vertexIndex++] = 0;
            vertices[vertexIndex++] = -(j * 1);
            j--;
            count++;
        }
        j = gridSize;
        count = 0;
        while(count < gridSize * 2) {
            vertices[vertexIndex++] = -(j * 1);
            vertices[vertexIndex++] = 0;
            vertices[vertexIndex++] = - gridSize * 1;
            vertices[vertexIndex++] = -(j * 1);
            vertices[vertexIndex++] = 0;
            vertices[vertexIndex++] = gridSize * 1;
            j--;
            count++;
        }

        _count = (uint)vertices.Length;
        _vertexBuffer = new BufferObject<float>(gl, vertices, BufferTargetARB.ArrayBuffer);
        
        // vertex array
        _vertexArrayId = _gl.GenVertexArray();
        _gl.BindVertexArray(_vertexArrayId);
        _gl.VertexAttribPointer(
            0,
            3,
            VertexAttribPointerType.Float,
            false,
            sizeof(float) * 3,
            null
        );
        _gl.EnableVertexAttribArray(0);
        
        _shader = new Shader(gl,
            "Content/Shaders/Grid.vert", 
            "Content/Shaders/Grid.frag");
    }
    
    public void Draw(Matrix4x4 view, Matrix4x4 projection)
    {
        _gl.Enable(EnableCap.Blend);
        _gl.BlendFunc(BlendingFactor.SrcAlpha, BlendingFactor.OneMinusSrcAlpha);
        {
            _shader.Use();
            _shader.SetUniform("uModel", ModelMatrix);
            _shader.SetUniform("uView", view);
            _shader.SetUniform("uProjection", projection);
            
            _vertexBuffer.Bind();
            _gl.BindVertexArray(_vertexArrayId);

            _gl.DrawArrays(PrimitiveType.Lines, 0, _count);
        }
        
        _gl.Disable(EnableCap.Blend);
    }
}