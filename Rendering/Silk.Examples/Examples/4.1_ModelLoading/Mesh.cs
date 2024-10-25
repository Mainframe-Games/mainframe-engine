using Silk.NET.OpenGL;

namespace SilkTest.Examples;

internal class Mesh : IDisposable
{
    public Mesh(GL gl, float[] vertices, uint[] indices, List<Texture> textures)
    {
        GL = gl;
        Vertices = vertices;
        Indices = indices;
        Textures = textures;

        // Setup mesh
        EBO = new BufferObject<uint>(GL, Indices, BufferTargetARB.ElementArrayBuffer);
        VBO = new BufferObject<float>(GL, Vertices, BufferTargetARB.ArrayBuffer);
        VAO = new VertexArrayObject<float, uint>(GL, VBO, EBO);
        VAO.VertexAttributePointer(0, 3, VertexAttribPointerType.Float, 5, 0);
        VAO.VertexAttributePointer(1, 2, VertexAttribPointerType.Float, 5, 3);
    }

    public float[] Vertices { get; }
    private uint[] Indices { get; }
    private List<Texture> Textures { get; }
    private VertexArrayObject<float, uint> VAO { get; set; }
    private BufferObject<float> VBO { get; set; }
    private BufferObject<uint> EBO { get; set; }
    private GL GL { get; }

    public void Bind()
    {
        VAO.Bind();
    }

    public void Dispose()
    {
        Textures.Clear();
        VAO.Dispose();
        VBO.Dispose();
        EBO.Dispose();
    }
}
