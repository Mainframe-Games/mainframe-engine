using System.Numerics;
using System.Runtime.InteropServices;
using Mainframe.Silk;
using Silk.NET.OpenGL;
using Silk.NET.SDL;
using Color = System.Drawing.Color;
using Shader = Mainframe.Silk.Shader;

namespace SilkSpine;

public class Grid
{
    private readonly GL _gl;
    private readonly Shader _shader;
    private readonly BufferObject<Vertex> _vertexBuffer;
    private readonly uint _vertexArrayId;

    private readonly uint _count;
    
    public bool Is2D { get; set; }

    private Matrix4x4 ModelMatrix => Matrix4x4.CreateRotationX(Mainframe.Math.DegreesToRadiansF(Is2D ? 90 : 0));

    private struct Vertex
    {
        public Vector3 Position;
        public Vector4 Color;
    }

    private static Vertex CreateVertex(float x, float y, float z, Vector4 color)
    {
        var v = new Vertex { Position = new Vector3(x, y, z) , Color = color};
        return v;
    }
    
    public unsafe Grid(GL gl, uint gridSize = 200)
    {
        _gl = gl;
        _count = (gridSize + 1) * 4;

        // src: https://github.com/IMCGKN/Grid_OpenGL_Cpp/blob/main/src/main.cpp
        var gridSizeHalf = (int)gridSize / 2;
        var vertices = stackalloc Vertex[(int)_count];
        var vIndex = 0;

        for (int x = gridSizeHalf; x >= -gridSizeHalf; --x)
        {
            var c = x == 0 ? new Vector4(1, 0, 0, 1) : new Vector4(1);
            vertices[vIndex++] = CreateVertex(gridSizeHalf, 0, -x, c);
            vertices[vIndex++] = CreateVertex(-gridSizeHalf, 0, -x, c);
        }
        
        for (int y = gridSizeHalf; y >= -gridSizeHalf; --y)
        {
            var c = y == 0 ? new Vector4(0, 0, 1, 1) : new Vector4(1);
            vertices[vIndex++] = CreateVertex(-y, 0, -gridSizeHalf, c);
            vertices[vIndex++] = CreateVertex(-y, 0, gridSizeHalf, c);
        }

        _vertexBuffer = new BufferObject<Vertex>(gl, vertices, _count, BufferTargetARB.ArrayBuffer);
        
        // vertex array
        _vertexArrayId = _gl.GenVertexArray();
        _gl.BindVertexArray(_vertexArrayId);
        _gl.VertexAttribPointer(
            0,
            3,
            VertexAttribPointerType.Float,
            false,
            (uint)Marshal.SizeOf<Vertex>(),
            (void*)Marshal.OffsetOf<Vertex>(nameof(Vertex.Position))
        );
        _gl.EnableVertexAttribArray(0);
        _gl.VertexAttribPointer(
            1,
            4,
            VertexAttribPointerType.Float,
            false,
            (uint)Marshal.SizeOf<Vertex>(),
            (void*)Marshal.OffsetOf<Vertex>(nameof(Vertex.Color))
        );
        _gl.EnableVertexAttribArray(1);
        
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