using System.Numerics;
using Silk.NET.Input;
using Silk.NET.OpenGL;
using Silk.NET.Windowing;
using SilkTest.Examples.Utils;
using PrimitiveType = Silk.NET.OpenGL.PrimitiveType;

namespace SilkTest.Examples;

internal class ModelLoadingExample : ExampleBase
{
    private static IKeyboard _primaryKeyboard;

    private Texture _Texture;
    private Shader _Shader;
    private Model _Model;

    //Setup the camera's location, directions, and movement speed
    private Vector3 CameraPosition = new(0.0f, 0.0f, 3.0f);
    private Vector3 CameraFront = new(0.0f, 0.0f, -1.0f);
    private readonly Vector3 CameraUp = Vector3.UnitY;
    private Vector3 CameraDirection = Vector3.Zero;
    private float CameraYaw = -90f;
    private float CameraPitch;
    private float CameraZoom = 45f;

    //Used to track change in mouse movement to allow for moving of the Camera
    private static Vector2 LastMousePosition;

    public override void OnLoad(IWindow window)
    {
        base.OnLoad(window);

        _primaryKeyboard = InputContext.Keyboards.FirstOrDefault();
        for (int i = 0; i < InputContext.Mice.Count; i++)
        {
            InputContext.Mice[i].Cursor.CursorMode = CursorMode.Raw;
            InputContext.Mice[i].MouseMove += OnMouseMove;
            InputContext.Mice[i].Scroll += OnMouseWheel;
        }

        _Shader = new Shader(
            Gl,
            "Content/Shaders/ModelLoading/shader.vert",
            "Content/Shaders/ModelLoading/shader.frag"
        );
        _Texture = new Texture(Gl, "Content/Textures/silk.png");
        _Model = new Model(Gl, "Content/Models/cube.obj");
    }

    public override void OnUpdate(double deltaTime)
    {
        base.OnUpdate(deltaTime);

        var moveSpeed = 2.5f * (float)deltaTime;

        if (_primaryKeyboard.IsKeyPressed(Key.W))
        {
            //Move forwards
            CameraPosition += moveSpeed * CameraFront;
        }
        if (_primaryKeyboard.IsKeyPressed(Key.S))
        {
            //Move backwards
            CameraPosition -= moveSpeed * CameraFront;
        }
        if (_primaryKeyboard.IsKeyPressed(Key.A))
        {
            //Move left
            CameraPosition -= Vector3.Normalize(Vector3.Cross(CameraFront, CameraUp)) * moveSpeed;
        }
        if (_primaryKeyboard.IsKeyPressed(Key.D))
        {
            //Move right
            CameraPosition += Vector3.Normalize(Vector3.Cross(CameraFront, CameraUp)) * moveSpeed;
        }
    }

    public override void OnRender(double deltaTime)
    {
        base.OnRender(deltaTime);

        Gl.Enable(EnableCap.DepthTest);
        Gl.Clear(ClearBufferMask.ColorBufferBit | ClearBufferMask.DepthBufferBit);

        _Texture.Bind();
        _Shader.Use();
        _Shader.SetUniform("uTexture0", 0);

        //Use elapsed time to convert to radians to allow our cube to rotate over time
        var difference = (float)(Window.Time * 100);

        var size = Window.FramebufferSize;

        var model =
            Matrix4x4.CreateRotationY(MathHelper.DegreesToRadians(difference))
            * Matrix4x4.CreateRotationX(MathHelper.DegreesToRadians(difference));
        var view = Matrix4x4.CreateLookAt(CameraPosition, CameraPosition + CameraFront, CameraUp);
        //Note that the apsect ratio calculation must be performed as a float, otherwise integer division will be performed (truncating the result).
        var projection = Matrix4x4.CreatePerspectiveFieldOfView(
            MathHelper.DegreesToRadians(CameraZoom),
            (float)size.X / size.Y,
            0.1f,
            100.0f
        );

        foreach (var mesh in _Model.Meshes)
        {
            mesh.Bind();
            _Shader.Use();
            _Texture.Bind();
            _Shader.SetUniform("uTexture0", 0);
            _Shader.SetUniform("uModel", model);
            _Shader.SetUniform("uView", view);
            _Shader.SetUniform("uProjection", projection);

            Gl.DrawArrays(PrimitiveType.Triangles, 0, (uint)mesh.Vertices.Length);
        }
    }

    private void OnMouseMove(IMouse mouse, Vector2 position)
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

    private void OnMouseWheel(IMouse mouse, ScrollWheel scrollWheel)
    {
        //We don't want to be able to zoom in too close or too far away so clamp to these values
        CameraZoom = Math.Clamp(CameraZoom - scrollWheel.Y, 1.0f, 45f);
    }

    public override void OnClose()
    {
        _Model.Dispose();
        _Shader.Dispose();
        _Texture.Dispose();
        base.OnClose();
    }
}
