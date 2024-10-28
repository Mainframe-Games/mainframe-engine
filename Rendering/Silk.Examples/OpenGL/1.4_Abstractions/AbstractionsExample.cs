using Silk.NET.OpenGL;
using Silk.NET.Windowing;

namespace SilkTest.Examples;

internal unsafe class AbstractionsExample : ExampleBase
{
    //Our new abstracted objects, here we specify what the types are.
    private BufferObject<float> Vbo;
    private BufferObject<uint> Ebo;
    private VertexArrayObject<float, uint> Vao;

    public Texture Texture;
    private Shader Shader;

    // csharpier-ignore
    private static readonly float[] Vertices =
    [
        //X    Y      Z     S    T
        0.5f,  0.5f, 0.0f, 1.0f, 0.0f,
        0.5f, -0.5f, 0.0f, 1.0f, 1.0f,
        -0.5f, -0.5f, 0.0f, 0.0f, 1.0f,
        -0.5f,  0.5f, 0.5f, 0.0f, 0.0f
    ];

    // csharpier-ignore
    private static readonly uint[] Indices =
    [
        0, 1, 3,
        1, 2, 3
    ];

    public override void OnLoad(IWindow window)
    {
        base.OnLoad(window);

        //Instantiating our new abstractions
        Ebo = new BufferObject<uint>(Gl, Indices, BufferTargetARB.ElementArrayBuffer);
        Vbo = new BufferObject<float>(Gl, Vertices, BufferTargetARB.ArrayBuffer);
        Vao = new VertexArrayObject<float, uint>(Gl, Vbo, Ebo);

        //Telling the VAO object how to lay out the attribute pointers
        Vao.VertexAttributePointer(0, 3, VertexAttribPointerType.Float, 5, 0);
        Vao.VertexAttributePointer(1, 2, VertexAttribPointerType.Float, 5, 3);

        Shader = new Shader(
            Gl,
            "Content/Shaders/Abstractions/shader.vert",
            "Content/Shaders/Abstractions/shader.frag"
        );
        Texture = new Texture(Gl, "Content/Textures/silk.png");
    }

    public override void OnRender(double deltaTime)
    {
        base.OnRender(deltaTime);

        //Binding and using our VAO and shader.
        Vao.Bind();
        Shader.Use();

        Texture.Bind(TextureUnit.Texture0);

        //Setting a uniform.
        Shader.SetUniform("uTexture", 0);

        Gl.DrawElements(
            PrimitiveType.Triangles,
            (uint)Indices.Length,
            DrawElementsType.UnsignedInt,
            null
        );
    }

    public override void OnClose()
    {
        //Remember to dispose all the instances.
        Vbo.Dispose();
        Ebo.Dispose();
        Vao.Dispose();
        Shader.Dispose();
        Texture.Dispose();

        base.OnClose();
    }
}
