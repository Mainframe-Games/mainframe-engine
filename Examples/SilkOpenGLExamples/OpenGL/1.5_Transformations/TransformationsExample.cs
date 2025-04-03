using System.Numerics;
using Silk.NET.OpenGL;
using Silk.NET.Windowing;

namespace SilkTest.Examples;

internal unsafe class TransformationsExample : ExampleBase
{
    private static BufferObject<float> Vbo;
    private static BufferObject<uint> Ebo;
    private static VertexArrayObject<float, uint> Vao;
    private static Texture Texture;
    private static Shader Shader;

    //Creating transforms for the transformations
    private static readonly Transform[] Transforms = new Transform[4];

    // csharpier-ignore
    private static readonly float[] Vertices =
    {
        //X    Y      Z     U   V
        0.5f,  0.5f, 0.0f, 1f, 0f,
        0.5f, -0.5f, 0.0f, 1f, 1f,
        -0.5f, -0.5f, 0.0f, 0f, 1f,
        -0.5f,  0.5f, 0.5f, 0f, 0f
    };

    // csharpier-ignore
    private static readonly uint[] Indices =
    [
        0, 1, 3,
        1, 2, 3
    ];

    public override void OnLoad(IWindow window)
    {
        base.OnLoad(window);

        Ebo = new BufferObject<uint>(Gl, Indices, BufferTargetARB.ElementArrayBuffer);
        Vbo = new BufferObject<float>(Gl, Vertices, BufferTargetARB.ArrayBuffer);
        Vao = new VertexArrayObject<float, uint>(Gl, Vbo, Ebo);

        Vao.VertexAttributePointer(0, 3, VertexAttribPointerType.Float, 5, 0);
        Vao.VertexAttributePointer(1, 2, VertexAttribPointerType.Float, 5, 3);

        Shader = new Shader(
            Gl,
            "Content/Shaders/Transformations/shader.vert",
            "Content/Shaders/Transformations/shader.frag"
        );

        Texture = new Texture(Gl, "Content/Textures/silk.png");

        //Unlike in the transformation, because of our abstraction, order doesn't matter here.
        //Translation.
        Transforms[0] = new Transform { Position = new Vector3(0.5f, 0.5f, 0f) };
        //Rotation.
        Transforms[1] = new Transform
        {
            Rotation = Quaternion.CreateFromAxisAngle(Vector3.UnitZ, 1f)
        };
        //Scaling.
        Transforms[2] = new Transform { Scale = 0.5f };
        //Mixed transformation.
        Transforms[3] = new Transform
        {
            Position = new Vector3(-0.5f, 0.5f, 0f),
            Rotation = Quaternion.CreateFromAxisAngle(Vector3.UnitZ, 1f),
            Scale = 0.5f
        };
    }

    public override void OnRender(double deltaTime)
    {
        base.OnRender(deltaTime);

        Vao.Bind();
        Texture.Bind();
        Shader.Use();
        Shader.SetUniform("uTexture", 0);

        for (int i = 0; i < Transforms.Length; i++)
        {
            //Using the transformations.
            Shader.SetUniform("uModel", Transforms[i].ViewMatrix);

            Gl.DrawElements(
                PrimitiveType.Triangles,
                (uint)Indices.Length,
                DrawElementsType.UnsignedInt,
                null
            );
        }
    }

    #region Private Classes

    private class Transform
    {
        //A transform abstraction.
        //For a transform we need to have a position a scale and a rotation,
        //depending on what application you are creating, the type for these may vary.

        //Here we have chosen a vec3 for position, float for scale and quaternion for rotation,
        //as that is the most normal to go with.
        //Another example could have been vec3, vec3, vec4, so the rotation is an axis angle instead of a quaternion

        public Vector3 Position { get; set; } = new(0, 0, 0);

        public float Scale { get; set; } = 1f;

        public Quaternion Rotation { get; set; } = Quaternion.Identity;

        //Note: The order here does matter.
        public Matrix4x4 ViewMatrix =>
            Matrix4x4.Identity
            * Matrix4x4.CreateFromQuaternion(Rotation)
            * Matrix4x4.CreateScale(Scale)
            * Matrix4x4.CreateTranslation(Position);
    }

    #endregion
}
