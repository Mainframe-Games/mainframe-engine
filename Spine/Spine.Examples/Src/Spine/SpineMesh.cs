using Silk.NET.OpenGL;

namespace SilkSpine;

internal class SpineMesh : IDisposable
{
    public float[] Vertices { get; private set; }
    public uint[] Indices { get; private set; }
    public List<Texture> Textures { get; }
    private VertexArrayObject<float, uint> VAO { get; }
    private BufferObject<float> VertexBuffer { get; }
    private BufferObject<uint> IndexBuffer { get; }
    private GL GL { get; }

    public SpineMesh(GL gl, List<Texture> textures)
    {
        GL = gl;
        Textures = textures;

        // Setup mesh
        IndexBuffer = new BufferObject<uint>(GL, [], BufferTargetARB.ElementArrayBuffer);
        VertexBuffer = new BufferObject<float>(GL, [], BufferTargetARB.ArrayBuffer);
        VAO = new VertexArrayObject<float, uint>(GL, VertexBuffer, IndexBuffer);

        const uint vertexSize = 9u;
        VAO.VertexAttributePointer(0, 3, VertexAttribPointerType.Float, vertexSize, 0);
        VAO.VertexAttributePointer(1, 2, VertexAttribPointerType.Float, vertexSize, 3);
        VAO.VertexAttributePointer(2, 4, VertexAttribPointerType.Float, vertexSize, 5);
    }

    public void Bind()
    {
        VAO.Bind();
    }

    public void Update(float[] vertices, uint[] indices)
    {
        Vertices = vertices;
        Indices = indices;

        IndexBuffer.Update(indices);
        VertexBuffer.Update(vertices);
    }

    public void Dispose()
    {
        Textures.Clear();
        VAO.Dispose();
        VertexBuffer.Dispose();
        IndexBuffer.Dispose();
    }
}
