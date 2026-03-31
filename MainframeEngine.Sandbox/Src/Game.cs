using System.Drawing;
using System.Numerics;
using ImGuiNET;
using Silk.NET.Input;
using Silk.NET.Windowing;
using MouseButton = Silk.NET.Input.MouseButton;

namespace MainframeEngine.Sandbox;

public sealed class Game(in EngineOptions engineInfo) : Engine(engineInfo)
{
    private readonly Camera3D _camera3D = new()
    {
        Position = new Vector3(1, 1, 5f),
    };

    private SkyEnvironment _sky = null!;
    private readonly LightEnvironment _lights = new();
    private ShadowSystem _shadowSystem = null!;
    private SceneGrid3d _sceneGrid3d = null!;
    
    private readonly List<ShapeBase> _shapes = [];

    private IKeyboard _keyboard = null!;
    private IMouse _mouse = null!;

    private bool CanMoveCamera => _mouse.Cursor.CursorMode is CursorMode.Raw;
    private Vector2 _lastMousePosition;
    private float _cameraSpeed = 10;

    protected override void OnLoad()
    {
        base.OnLoad();
        
        Renderer.SetClearColor(0.18f, 0.31f, 0.31f); // DarkSlateGray

        _keyboard = InputContext.Keyboards[0];
        _keyboard.KeyDown += OnKeyDown;

        _mouse = InputContext.Mice[0];
        _mouse.MouseDown += OnMouseDown;
        _mouse.MouseUp += OnMouseUp;
        _mouse.MouseMove += OnMouseMove;

        _sky = new SkyPanoramic(Renderer, "Content/Sky/sky_16_2k.png");
        _sceneGrid3d = new SceneGrid3d(Renderer);

        // Shadow system must be created before any shadow-casting/receiving shapes.
        _shadowSystem = new ShadowSystem((IVulkanContext)Renderer);
        
        // TODO: see if we can put this into Engine class
        Node.Initialize(Renderer, _shadowSystem);

        _shapes.Add(new Quad
        {
            Rotation = new Vector3(90, 0, 0),
            Scale = new Vector3(10, 10, 1),
            Color = Color.White
        });
        _shapes.Add(new Box3d
        {
            Position = new Vector3(0, 1, 0),
            Color = Color.White
        });

        _lights.DirectionalLights.Add(new DirectionalLight
        {
            Position = new Vector3(0, 5, 0),
            Direction = Vector3.Normalize(new Vector3(-1, -2, -1)),
            Color = new Vector3(1f, 0.95f, 0.8f),
            Intensity = 0.9f
        });
        // _lights.PointLights.Add(new PointLight
        // { 
        //     Position = new Vector3(3, 2, 2),
        //     Color = new Vector3(0.2f, 0.5f, 1f),
        //     Intensity = 0.1f,
        //     Range = 10
        // });
        // _lights.SpotLights.Add(new SpotLight
        // {
        //     Position = new Vector3(-2, 4, 2),
        //     Direction = Vector3.Normalize(new Vector3(0.5f, -1, -0.5f)),
        //     Color = new Vector3(1,1,1),
        //     Intensity = 1,
        //     Range = 15f,
        //     InnerConeAngle = 12f,
        //     OuterConeAngle = 25f
        // });
    }
    
    protected override void OnUpdate(in GameTime gameTime)
    {
        UpdateCameraPosition(gameTime.DeltaTime);
        _shapes[1].Rotation += new Vector3(1, 1, 0) * 20 * gameTime.DeltaTime; // rotate the box
    }

    protected override void OnImGui(in GameTime gameTime)
    {
        _lights.DrawLightGizmos(_camera3D);

        ImGui.SetNextWindowPos(Vector2.Zero, ImGuiCond.Always, new Vector2(0, 0));
        if (ImGui.Begin("Game Window", ImGuiWindowFlags.AlwaysAutoResize))
        {
            ImGui.Value("FrameCount", gameTime.FrameCount);
            ImGui.Value("DeltaTime", gameTime.DeltaTime);
            ImGui.Value("FPS", gameTime.FramesPerSecond);
            ImGui.Value("Ms", gameTime.FramesTimeMs);

            var vsync = Renderer.VSync;
            if (ImGui.Checkbox("VSync", ref vsync))
                Renderer.VSync = vsync;

            var isFullScreen = Window.WindowState is WindowState.Fullscreen;
            if (ImGui.Checkbox("FullScreen", ref isFullScreen))
                Window.WindowState = isFullScreen ? WindowState.Fullscreen : WindowState.Normal;

            int[] fpsPresets = [0, 30, 60, 120, 144, 240];
            string[] fpsLabels = ["Unlimited", "30", "60", "120", "144", "240"];
            var currentFps = MaxFPS;
            var selectedIndex = Array.IndexOf(fpsPresets, currentFps);
            if (selectedIndex < 0) selectedIndex = 0;
            if (ImGui.Combo("Max FPS", ref selectedIndex, fpsLabels, fpsLabels.Length))
                MaxFPS = fpsPresets[selectedIndex];
        }
        ImGui.End();
    }

    protected override void OnShadowPass(in GameTime gameTime)
    {
        _shadowSystem.RenderShadows(
            _lights,
            draw2D: (cb, p32, p12, lay) => { // TODO: remove allocations here
                foreach (var shape in _shapes)
                    shape.DrawShadow2D(cb);
            },
            drawPoint: (cb, p32, p12, lay, lightPos, lightRange) => {
                foreach (var shape in _shapes)
                    shape.DrawShadowPoint(cb, lightPos, lightRange);
            }
        );
    }

    protected override void OnRenderMainPass(in GameTime gameTime)
    {
        Renderer.EnableDepthTest();
        Renderer.Clear();

        var frameBufferSize = new Vector2(Window.FramebufferSize.X, Window.FramebufferSize.Y);
        _camera3D.AspectRatio = frameBufferSize.X / frameBufferSize.Y;
        _sky.Draw(_camera3D); // must be drawn first — renders behind all geometry
        _sceneGrid3d.Draw(_camera3D);
        foreach (var shape in _shapes)
            shape.Draw(_camera3D, _lights);
    }

    protected override void OnClose()
    {
        _shadowSystem.Dispose();
        _sky.Dispose();
        _sceneGrid3d.Dispose();
        foreach (var shape in _shapes)
            shape.Dispose();
        
        base.OnClose();
    }

    private void UpdateCameraPosition(double deltaTime)
    {
        if (!CanMoveCamera)
            return;

        var camera = _camera3D;
        var baseSpeed = _keyboard.IsKeyPressed(Key.ShiftLeft) ? _cameraSpeed * 2 : _cameraSpeed;
        var moveSpeed = baseSpeed * (float)deltaTime;

        var isPerspectiveCamera = camera is Camera3D;

        if (isPerspectiveCamera)
        {
            if (_keyboard.IsKeyPressed(Key.W))
                camera.Position += moveSpeed * camera.Forward;
            if (_keyboard.IsKeyPressed(Key.S))
                camera.Position -= moveSpeed * camera.Forward;
            if (_keyboard.IsKeyPressed(Key.A))
                camera.Position -= Vector3.Normalize(Vector3.Cross(camera.Forward, camera.Up)) * moveSpeed;
            if (_keyboard.IsKeyPressed(Key.D))
                camera.Position += Vector3.Normalize(Vector3.Cross(camera.Forward, camera.Up)) * moveSpeed;
            if (_keyboard.IsKeyPressed(Key.Q))
                camera.Position -= camera.Up * moveSpeed;
            if (_keyboard.IsKeyPressed(Key.E))
                camera.Position += camera.Up * moveSpeed;
        }
        else
        {
            if (_keyboard.IsKeyPressed(Key.W))
                camera.Position += camera.Up * moveSpeed;
            if (_keyboard.IsKeyPressed(Key.S))
                camera.Position -= camera.Up * moveSpeed;
            if (_keyboard.IsKeyPressed(Key.A))
                camera.Position -= Vector3.Normalize(Vector3.Cross(camera.Forward, camera.Up)) * moveSpeed;
            if (_keyboard.IsKeyPressed(Key.D))
                camera.Position += Vector3.Normalize(Vector3.Cross(camera.Forward, camera.Up)) * moveSpeed;
        }
    }

    public void OnKeyDown(IKeyboard keyboard, Key key, int arg3)
    {
        if (key is Key.AltLeft)
        {
            _mouse.Cursor.CursorMode = _mouse.Cursor.CursorMode is CursorMode.Raw
                ? CursorMode.Normal
                : CursorMode.Raw;
        }

        if (key == Key.Escape)
            Window.Close();
    }

    private void OnMouseMove(IMouse mouse, Vector2 position)
    {
        if (!CanMoveCamera)
        {
            _lastMousePosition = default;
            return;
        }

        if (_lastMousePosition == default)
        {
            _lastMousePosition = position;
        }
        else
        {
            const float lookSensitivity = 0.1f;
            var xOffset = (position.X - _lastMousePosition.X) * lookSensitivity;
            var yOffset = (position.Y - _lastMousePosition.Y) * lookSensitivity;
            _lastMousePosition = position;
            _camera3D.ModifyDirection(xOffset, yOffset);
        }
    }

    private void OnMouseDown(IMouse mouse, MouseButton button)
    {
        if (button is MouseButton.Right)
            _mouse.Cursor.CursorMode = CursorMode.Raw;
    }

    private void OnMouseUp(IMouse mouse, MouseButton button)
    {
        if (button is MouseButton.Right)
            _mouse.Cursor.CursorMode = CursorMode.Normal;
    }
}
