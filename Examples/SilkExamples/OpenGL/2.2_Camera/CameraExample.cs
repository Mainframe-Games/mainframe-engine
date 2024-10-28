using System.Numerics;
using Silk.NET.Input;
using Silk.NET.OpenGL;
using Silk.NET.Windowing;
using SilkTest.Examples.Utils;

namespace SilkTest.Examples;

internal class CameraExample : ExampleBase
{
    private static IKeyboard primaryKeyboard;

    private static BufferObject<float> Vbo;
    private static BufferObject<uint> Ebo;
    private static VertexArrayObject<float, uint> Vao;
    private static Texture Texture;
    private static Shader Shader;

    //Setup the camera's location, directions, and movement speed
    private static Vector3 CameraPosition = new Vector3(0.0f, 0.0f, 3.0f);
    private static Vector3 CameraFront = new Vector3(0.0f, 0.0f, -1.0f);
    private static Vector3 CameraUp = Vector3.UnitY;
    private static Vector3 CameraDirection = Vector3.Zero;
    private static float CameraYaw = -90f;
    private static float CameraPitch = 0f;
    private static float CameraZoom = 45f;

    //Used to track change in mouse movement to allow for moving of the Camera
    private static Vector2 LastMousePosition;

    // csharpier-ignore
    private static readonly float[] Vertices =
    [
        // X    Y      Z      U     V
        -0.5f, -0.5f, -0.5f,  0.0f, 1.0f,
         0.5f, -0.5f, -0.5f,  1.0f, 1.0f,
         0.5f,  0.5f, -0.5f,  1.0f, 0.0f,
         0.5f,  0.5f, -0.5f,  1.0f, 0.0f,
        -0.5f,  0.5f, -0.5f,  0.0f, 0.0f,
        -0.5f, -0.5f, -0.5f,  0.0f, 1.0f,

        -0.5f, -0.5f,  0.5f,  0.0f, 1.0f,
         0.5f, -0.5f,  0.5f,  1.0f, 1.0f,
         0.5f,  0.5f,  0.5f,  1.0f, 0.0f,
         0.5f,  0.5f,  0.5f,  1.0f, 0.0f,
        -0.5f,  0.5f,  0.5f,  0.0f, 0.0f,
        -0.5f, -0.5f,  0.5f,  0.0f, 1.0f,

        -0.5f,  0.5f,  0.5f,  1.0f, 1.0f,
        -0.5f,  0.5f, -0.5f,  1.0f, 0.0f,
        -0.5f, -0.5f, -0.5f,  0.0f, 0.0f,
        -0.5f, -0.5f, -0.5f,  0.0f, 0.0f,
        -0.5f, -0.5f,  0.5f,  0.0f, 1.0f,
        -0.5f,  0.5f,  0.5f,  1.0f, 1.0f,

         0.5f,  0.5f,  0.5f,  1.0f, 1.0f,
         0.5f,  0.5f, -0.5f,  1.0f, 0.0f,
         0.5f, -0.5f, -0.5f,  0.0f, 0.0f,
         0.5f, -0.5f, -0.5f,  0.0f, 0.0f,
         0.5f, -0.5f,  0.5f,  0.0f, 1.0f,
         0.5f,  0.5f,  0.5f,  1.0f, 1.0f,

        -0.5f, -0.5f, -0.5f,  0.0f, 0.0f,
         0.5f, -0.5f, -0.5f,  1.0f, 0.0f,
         0.5f, -0.5f,  0.5f,  1.0f, 1.0f,
         0.5f, -0.5f,  0.5f,  1.0f, 1.0f,
        -0.5f, -0.5f,  0.5f,  0.0f, 1.0f,
        -0.5f, -0.5f, -0.5f,  0.0f, 0.0f,

        -0.5f,  0.5f, -0.5f,  0.0f, 0.0f,
         0.5f,  0.5f, -0.5f,  1.0f, 0.0f,
         0.5f,  0.5f,  0.5f,  1.0f, 1.0f,
         0.5f,  0.5f,  0.5f,  1.0f, 1.0f,
        -0.5f,  0.5f,  0.5f,  0.0f, 1.0f,
        -0.5f,  0.5f, -0.5f,  0.0f, 0.0f
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

        primaryKeyboard = InputContext.Keyboards.FirstOrDefault();

        for (int i = 0; i < InputContext.Mice.Count; i++)
        {
            InputContext.Mice[i].Cursor.CursorMode = CursorMode.Raw;
            InputContext.Mice[i].MouseMove += OnMouseMove;
            InputContext.Mice[i].Scroll += OnMouseWheel;
        }

        Ebo = new BufferObject<uint>(Gl, Indices, BufferTargetARB.ElementArrayBuffer);
        Vbo = new BufferObject<float>(Gl, Vertices, BufferTargetARB.ArrayBuffer);
        Vao = new VertexArrayObject<float, uint>(Gl, Vbo, Ebo);

        Vao.VertexAttributePointer(0, 3, VertexAttribPointerType.Float, 5, 0);
        Vao.VertexAttributePointer(1, 2, VertexAttribPointerType.Float, 5, 3);

        Shader = new Shader(
            Gl,
            "Content/Shaders/Camera/shader.vert",
            "Content/Shaders/Camera/shader.frag"
        );
        Texture = new Texture(Gl, "Content/Textures/silk.png");
    }

    public override void OnUpdate(double deltaTime)
    {
        base.OnUpdate(deltaTime);

        var moveSpeed = 2.5f * (float)deltaTime;

        if (primaryKeyboard.IsKeyPressed(Key.W))
        {
            //Move forwards
            CameraPosition += moveSpeed * CameraFront;
        }
        if (primaryKeyboard.IsKeyPressed(Key.S))
        {
            //Move backwards
            CameraPosition -= moveSpeed * CameraFront;
        }
        if (primaryKeyboard.IsKeyPressed(Key.A))
        {
            //Move left
            CameraPosition -= Vector3.Normalize(Vector3.Cross(CameraFront, CameraUp)) * moveSpeed;
        }
        if (primaryKeyboard.IsKeyPressed(Key.D))
        {
            //Move right
            CameraPosition += Vector3.Normalize(Vector3.Cross(CameraFront, CameraUp)) * moveSpeed;
        }
    }

    public override void OnRender(double deltaTime)
    {
        base.OnRender(deltaTime);

        Gl.Enable(EnableCap.DepthTest);
        Gl.Clear((uint)(ClearBufferMask.ColorBufferBit | ClearBufferMask.DepthBufferBit));

        Vao.Bind();
        Texture.Bind();
        Shader.Use();
        Shader.SetUniform("uTexture", 0);

        //Use elapsed time to convert to radians to allow our cube to rotate over time
        var difference = (float)(Window.Time * 100);

        var size = Window.FramebufferSize;

        var model =
            Matrix4x4.CreateRotationY(MathHelper.DegreesToRadians(difference))
            * Matrix4x4.CreateRotationX(MathHelper.DegreesToRadians(difference));
        var view = Matrix4x4.CreateLookAt(CameraPosition, CameraPosition + CameraFront, CameraUp);
        var projection = Matrix4x4.CreatePerspectiveFieldOfView(
            MathHelper.DegreesToRadians(CameraZoom),
            (float)size.X / size.Y,
            0.1f,
            100.0f
        );

        Shader.SetUniform("uModel", model);
        Shader.SetUniform("uView", view);
        Shader.SetUniform("uProjection", projection);

        //We're drawing with just vertices and no indices, and it takes 36 vertices to have a six-sided textured cube
        Gl.DrawArrays(PrimitiveType.Triangles, 0, 36);
    }

    private static void OnMouseMove(IMouse mouse, Vector2 position)
    {
        var lookSensitivity = 0.1f;
        if (LastMousePosition == default)
        {
            LastMousePosition = position;
        }
        else
        {
            var xOffset = (position.X - LastMousePosition.X) * lookSensitivity;
            var yOffset = (position.Y - LastMousePosition.Y) * lookSensitivity;
            LastMousePosition = position;

            CameraYaw += xOffset;
            CameraPitch -= yOffset;

            //We don't want to be able to look behind us by going over our head or under our feet so make sure it stays within these bounds
            CameraPitch = Math.Clamp(CameraPitch, -89.0f, 89.0f);

            CameraDirection.X =
                MathF.Cos(MathHelper.DegreesToRadians(CameraYaw))
                * MathF.Cos(MathHelper.DegreesToRadians(CameraPitch));
            CameraDirection.Y = MathF.Sin(MathHelper.DegreesToRadians(CameraPitch));
            CameraDirection.Z =
                MathF.Sin(MathHelper.DegreesToRadians(CameraYaw))
                * MathF.Cos(MathHelper.DegreesToRadians(CameraPitch));
            CameraFront = Vector3.Normalize(CameraDirection);
        }
    }

    private static void OnMouseWheel(IMouse mouse, ScrollWheel scrollWheel)
    {
        //We don't want to be able to zoom in too close or too far away so clamp to these values
        CameraZoom = Math.Clamp(CameraZoom - scrollWheel.Y, 1.0f, 45f);
    }

    public override void OnClose()
    {
        Vbo.Dispose();
        Ebo.Dispose();
        Vao.Dispose();
        Shader.Dispose();
        Texture.Dispose();

        base.OnClose();
    }
}
