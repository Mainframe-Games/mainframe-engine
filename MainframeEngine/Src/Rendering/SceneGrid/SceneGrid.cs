using System.Numerics;
using System.Runtime.InteropServices;
using Silk.NET.OpenGL;

namespace MainframeEngine;

/// <summary>
/// Base class for making scene grid
/// </summary>
/// <param name="gl"></param>
/// <param name="vertexCount">Number of vertices in the array</param>
public abstract class SceneGrid(GL gl, uint vertexCount)
{
    protected readonly Vector4 DefaultColor = new(1f, 1f, 1f, 0.1f);
    protected readonly Vector4 Red = new(1f, 0f, 0f, 1f);
    protected readonly Vector4 Yellow = new(1f, 1f, 0f, 1f);
    protected readonly Vector4 Blue = new(0f, 0f, 1f, 1f);

    protected readonly uint _vertexCount = vertexCount;
    private uint _vertexArrayId;
    private readonly Shader _shader = new(gl,
        "Content/Shaders/SceneGrid/SceneGrid.vert", 
        "Content/Shaders/SceneGrid/SceneGrid.frag");
    
    protected struct Vertex(float x, float y, float z, Vector4 color)
    {
        public Vector3 Position = new(x, y, z);
        public Vector4 Color = color;
    }

    public void Draw(ICamera camera)
    {
        gl.Enable(EnableCap.Blend);
        gl.BlendFunc(BlendingFactor.SrcAlpha, BlendingFactor.OneMinusSrcAlpha);
        {
            _shader.Use();
            _shader.SetUniform("uView", camera.ViewMatrix);
            _shader.SetUniform("uProjection", camera.ProjectionMatrix);
            
            gl.BindVertexArray(_vertexArrayId);
            gl.DrawArrays(PrimitiveType.Lines, 0, _vertexCount);
        }
        
        gl.Disable(EnableCap.Blend);
    }

    protected unsafe void BuildVertexArray(Vertex* vertices)
    {
        // vertex buffer
        var vertexBufferId = gl.GenBuffer();
        gl.BindBuffer(BufferTargetARB.ArrayBuffer, vertexBufferId);
        gl.BufferData(
            BufferTargetARB.ArrayBuffer,
            (nuint)(_vertexCount * sizeof(Vertex)),
            vertices,
            BufferUsageARB.StaticDraw
        );
        
        // vertex array
        _vertexArrayId = gl.GenVertexArray();
        gl.BindVertexArray(_vertexArrayId);
        gl.VertexAttribPointer(
            0,
            3,
            VertexAttribPointerType.Float,
            false,
            (uint)Marshal.SizeOf<Vertex>(),
            (void*)Marshal.OffsetOf<Vertex>(nameof(Vertex.Position))
        );
        gl.EnableVertexAttribArray(0);
        gl.VertexAttribPointer(
            1,
            4,
            VertexAttribPointerType.Float,
            false,
            (uint)Marshal.SizeOf<Vertex>(),
            (void*)Marshal.OffsetOf<Vertex>(nameof(Vertex.Color))
        );
        gl.EnableVertexAttribArray(1);
    }
}