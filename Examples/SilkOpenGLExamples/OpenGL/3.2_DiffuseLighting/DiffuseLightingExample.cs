using System.Numerics;
using Silk.NET.Input;
using Silk.NET.Maths;
using Silk.NET.OpenGL;
using Silk.NET.Windowing;
using SilkTest.Examples.Utils;

namespace SilkTest.Examples;

internal class DiffuseLightingExample : ExampleBase
{
    private static IKeyboard primaryKeyboard;

    private BufferObject<float> Vbo;
    private BufferObject<uint> Ebo;
    private VertexArrayObject<float, uint> VaoCube;
    private Shader LightingShader;
    private Shader LampShader;
    private Vector3 LampPosition = new Vector3(1.2f, 1.0f, 2.0f);

    private Camera _Camera;

    //Used to track change in mouse movement to allow for moving of the Camera
    private static Vector2 LastMousePosition;

    // csharpier-ignore
    private static readonly float[] Vertices =
    [
        //X    Y      Z       Normals
        -0.5f, -0.5f, -0.5f,  0.0f,  0.0f, -1.0f,
        0.5f, -0.5f, -0.5f,  0.0f,  0.0f, -1.0f,
        0.5f,  0.5f, -0.5f,  0.0f,  0.0f, -1.0f,
        0.5f,  0.5f, -0.5f,  0.0f,  0.0f, -1.0f,
        -0.5f,  0.5f, -0.5f,  0.0f,  0.0f, -1.0f,
        -0.5f, -0.5f, -0.5f,  0.0f,  0.0f, -1.0f,

        -0.5f, -0.5f,  0.5f,  0.0f,  0.0f,  1.0f,
        0.5f, -0.5f,  0.5f,  0.0f,  0.0f,  1.0f,
        0.5f,  0.5f,  0.5f,  0.0f,  0.0f,  1.0f,
        0.5f,  0.5f,  0.5f,  0.0f,  0.0f,  1.0f,
        -0.5f,  0.5f,  0.5f,  0.0f,  0.0f,  1.0f,
        -0.5f, -0.5f,  0.5f,  0.0f,  0.0f,  1.0f,

        -0.5f,  0.5f,  0.5f, -1.0f,  0.0f,  0.0f,
        -0.5f,  0.5f, -0.5f, -1.0f,  0.0f,  0.0f,
        -0.5f, -0.5f, -0.5f, -1.0f,  0.0f,  0.0f,
        -0.5f, -0.5f, -0.5f, -1.0f,  0.0f,  0.0f,
        -0.5f, -0.5f,  0.5f, -1.0f,  0.0f,  0.0f,
        -0.5f,  0.5f,  0.5f, -1.0f,  0.0f,  0.0f,

        0.5f,  0.5f,  0.5f,  1.0f,  0.0f,  0.0f,
        0.5f,  0.5f, -0.5f,  1.0f,  0.0f,  0.0f,
        0.5f, -0.5f, -0.5f,  1.0f,  0.0f,  0.0f,
        0.5f, -0.5f, -0.5f,  1.0f,  0.0f,  0.0f,
        0.5f, -0.5f,  0.5f,  1.0f,  0.0f,  0.0f,
        0.5f,  0.5f,  0.5f,  1.0f,  0.0f,  0.0f,

        -0.5f, -0.5f, -0.5f,  0.0f, -1.0f,  0.0f,
        0.5f, -0.5f, -0.5f,  0.0f, -1.0f,  0.0f,
        0.5f, -0.5f,  0.5f,  0.0f, -1.0f,  0.0f,
        0.5f, -0.5f,  0.5f,  0.0f, -1.0f,  0.0f,
        -0.5f, -0.5f,  0.5f,  0.0f, -1.0f,  0.0f,
        -0.5f, -0.5f, -0.5f,  0.0f, -1.0f,  0.0f,

        -0.5f,  0.5f, -0.5f,  0.0f,  1.0f,  0.0f,
        0.5f,  0.5f, -0.5f,  0.0f,  1.0f,  0.0f,
        0.5f,  0.5f,  0.5f,  0.0f,  1.0f,  0.0f,
        0.5f,  0.5f,  0.5f,  0.0f,  1.0f,  0.0f,
        -0.5f,  0.5f,  0.5f,  0.0f,  1.0f,  0.0f,
        -0.5f,  0.5f, -0.5f,  0.0f,  1.0f,  0.0f
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
        VaoCube = new VertexArrayObject<float, uint>(Gl, Vbo, Ebo);

        VaoCube.VertexAttributePointer(0, 3, VertexAttribPointerType.Float, 6, 0);
        VaoCube.VertexAttributePointer(1, 3, VertexAttribPointerType.Float, 6, 3);

        //The lighting shader will give our main cube its colour multiplied by the lights intensity
        LightingShader = new Shader(
            Gl,
            "Content/Shaders/DiffuseLighting/shader.vert",
            "Content/Shaders/DiffuseLighting/lighting.frag"
        );
        //The Lamp shader uses a fragment shader that just colours it solid white so that we know it is the light source
        LampShader = new Shader(
            Gl,
            "Content/Shaders/DiffuseLighting/shader.vert",
            "Content/Shaders/DiffuseLighting/shader.frag"
        );

        //Start a camera at position 3 on the Z axis, looking at position -1 on the Z axis
        var size = window.FramebufferSize;
        _Camera = new Camera(
            Vector3.UnitZ * 6,
            Vector3.UnitZ * -1,
            Vector3.UnitY,
            (float)size.X / size.Y
        );
    }

    public override void OnUpdate(double deltaTime)
    {
        base.OnUpdate(deltaTime);

        var moveSpeed = 2.5f * (float)deltaTime;

        if (primaryKeyboard.IsKeyPressed(Key.W))
        {
            //Move forwards
            _Camera.Position += moveSpeed * _Camera.Front;
        }
        if (primaryKeyboard.IsKeyPressed(Key.S))
        {
            //Move backwards
            _Camera.Position -= moveSpeed * _Camera.Front;
        }
        if (primaryKeyboard.IsKeyPressed(Key.A))
        {
            //Move left
            _Camera.Position -=
                Vector3.Normalize(Vector3.Cross(_Camera.Front, _Camera.Up)) * moveSpeed;
        }
        if (primaryKeyboard.IsKeyPressed(Key.D))
        {
            //Move right
            _Camera.Position +=
                Vector3.Normalize(Vector3.Cross(_Camera.Front, _Camera.Up)) * moveSpeed;
        }
    }

    public override void OnRender(double deltaTime)
    {
        base.OnRender(deltaTime);

        Gl.Enable(EnableCap.DepthTest);
        Gl.Clear((uint)(ClearBufferMask.ColorBufferBit | ClearBufferMask.DepthBufferBit));

        VaoCube.Bind();

        //Draw the coral coloured cube that we want to be affected by the light
        RenderLitCube();

        //Draw the 'Lamp' cube that represents where the source of the light will come from
        RenderLampCube();
    }

    private void RenderLitCube()
    {
        //Use the 'lighting shader' that is capable of modifying the cubes colours based on ambient lighting and diffuse lighting
        LightingShader.Use();

        //Set up the uniforms needed for the lighting shaders to be able to draw and light the coral cube
        LightingShader.SetUniform(
            "uModel",
            Matrix4x4.CreateRotationY(MathHelper.DegreesToRadians(25f))
        );
        LightingShader.SetUniform("uView", _Camera.GetViewMatrix());
        LightingShader.SetUniform("uProjection", _Camera.GetProjectionMatrix());
        LightingShader.SetUniform("objectColor", new Vector3(1.0f, 0.5f, 0.31f));
        LightingShader.SetUniform("lightColor", Vector3.One);
        LightingShader.SetUniform("lightPos", LampPosition);

        //We're drawing with just vertices and no indicies, and it takes 36 verticies to have a six-sided textured cube
        Gl.DrawArrays(PrimitiveType.Triangles, 0, 36);
    }

    private void RenderLampCube()
    {
        //Use the 'main' shader that does not do any lighting calculations to just draw the cube to screen in the requested colours.
        LampShader.Use();

        //The Lamp cube is going to be a scaled down version of the normal cubes verticies moved to a different screen location
        var lampMatrix = Matrix4x4.Identity;
        lampMatrix *= Matrix4x4.CreateScale(0.2f);
        lampMatrix *= Matrix4x4.CreateTranslation(LampPosition);

        //Setup the uniforms needed to draw the Lamp in the correct place on screen
        LampShader.SetUniform("uModel", lampMatrix);
        LampShader.SetUniform("uView", _Camera.GetViewMatrix());
        LampShader.SetUniform("uProjection", _Camera.GetProjectionMatrix());

        Gl.DrawArrays(PrimitiveType.Triangles, 0, 36);
    }

    public override void OnFramebufferResize(Vector2D<int> newSize)
    {
        base.OnFramebufferResize(newSize);
        _Camera.AspectRatio = (float)newSize.X / newSize.Y;
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

            _Camera.ModifyDirection(xOffset, yOffset);
        }
    }

    private void OnMouseWheel(IMouse mouse, ScrollWheel scrollWheel)
    {
        _Camera.ModifyZoom(scrollWheel.Y);
    }

    public override void OnClose()
    {
        Vbo.Dispose();
        Ebo.Dispose();
        VaoCube.Dispose();
        LightingShader.Dispose();

        base.OnClose();
    }
}
