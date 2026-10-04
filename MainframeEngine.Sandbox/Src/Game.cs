using System.Globalization;
using System.Numerics;
using ImGuiNET;
using MainframeEngine.Gizmos;
using Silk.NET.Input;
using Silk.NET.Maths;
using Silk.NET.Windowing;
using Color = System.Drawing.Color;
using MouseButton = Silk.NET.Input.MouseButton;

namespace MainframeEngine.Sandbox;

public sealed class Game(in EngineOptions options) : Engine(options)
{
    public static EngineOptions DefaultOptions => new()
    {
        GameName = "Mainframe Engine Sandbox",
        RenderingBackend = RenderingBackend.Vulkan,
        WindowSize = new Vector2D<int>(1920, 1080),
        IconPath = "Content/Branding/mg_300_circle.png"
    };

    private static readonly int[] FpsPresets = [0, 30, 60, 120, 144, 240];
    private static readonly string[] FpsLabels = ["Unlimited", "30", "60", "120", "144", "240"];

    /// <summary>Set by <c>--qa-capture</c>: frames to screenshot before the engine exits.</summary>
    public QaCapture? QaCapture { get; init; }

    private readonly Camera3D _camera3D = new();

    private SkyEnvironment _sky = null!;
    private readonly LightEnvironment _lights = new();
    private ShadowSystem _shadowSystem = null!;
    private SceneGrid3d _sceneGrid3d = null!;

    private readonly List<Node> _nodes = [];

    private IKeyboard _keyboard = null!;
    private IMouse _mouse = null!;

    private bool CanMoveCamera => _mouse.IsButtonPressed(MouseButton.Right);
    private Vector2 _lastMousePosition;
    private float _cameraSpeed = 10;

    protected override void OnLoad()
    {
        base.OnLoad();

        Renderer.SetClearColor(0.18f, 0.31f, 0.31f); // DarkSlateGray

        _camera3D.Position = new Vector3(0, 5f, 10f);
        _camera3D.LookAt(Vector3.Zero);

        _keyboard = InputContext.Keyboards[0];
        _keyboard.KeyDown += OnKeyDown;

        _mouse = InputContext.Mice[0];
        _mouse.MouseDown += OnMouseDown;
        _mouse.MouseUp += OnMouseUp;
        _mouse.MouseMove += OnMouseMove;

        _sky = new SkyPanoramic(Renderer, "Content/Sky/sky_10_2k.png");
        _sceneGrid3d = new SceneGrid3d(Renderer);

        // Shadow system must be created before any shadow-casting/receiving shapes.
        _shadowSystem = new ShadowSystem((IVulkanContext)Renderer);

        // TODO: see if we can put this into Engine class
        Node.Initialize(Renderer, _shadowSystem);

        var spineNode = new SpineNode(Renderer, new SpineFolder("Content/Models/Spine/SpineBoy"));
        spineNode.SpineScale = 0.001f;
        spineNode.Scale = new Vector3(0.1f, 0.1f, 0.1f);
        // _spineNode.SetAnimation("W/Run");
        spineNode.SetAnimation("walk");
        _nodes.Add(spineNode);

        _nodes.Add(new Quad
        {
            Rotation = new Vector3(90, 0, 0),
            Scale = new Vector3(10, 10, 1),
            Color = Color.White
        });
        _nodes.Add(new Box3d
        {
            Position = new Vector3(3, 1, 0),
            Color = Color.White
        });

        _lights.AddLight(new DirectionalLight
        {
            Position = new Vector3(0, 5, 0),
            Direction = Vector3.Normalize(new Vector3(0, -0.5f, -1)),
            Color = new Vector3(1f, 0.95f, 0.8f),
            Intensity = 0.9f
        });
        // _lights.AddLight(new PointLight
        // { 
        //     Position = new Vector3(3, 2, 2),
        //     Color = new Vector3(0.2f, 0.5f, 1f),
        //     Intensity = 0.1f,
        //     Range = 10
        // });
        // _lights.AddLight(new SpotLight
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
        if (QaCapture?.ShouldCapture(gameTime.FrameCount) == true)
            CaptureFrame();

        UpdateCameraPosition(gameTime.DeltaTime);

        foreach (var node in _nodes)
        {
            node.OnUpdate(gameTime);

            // rotate the box
            if (node is Box3d box)
                box.Rotation += new Vector3(1, 1, 0) * 20 * gameTime.DeltaTime;
        }
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

            var currentFps = MaxFPS;
            var selectedIndex = Array.IndexOf(FpsPresets, currentFps);
            if (selectedIndex < 0) selectedIndex = 0;
            if (ImGui.Combo("Max FPS", ref selectedIndex, FpsLabels, FpsLabels.Length))
                MaxFPS = FpsPresets[selectedIndex];

            ImGui.SeparatorText("Camera");
            // Formatted into stack buffers: interpolated strings would allocate every frame.
            Span<char> text = stackalloc char[64];
            var p = _camera3D.Position;
            if (text.TryWrite(CultureInfo.InvariantCulture, $"Position: <{p.X:0.00}, {p.Y:0.00}, {p.Z:0.00}>", out var written))
                ImGui.TextUnformatted(text[..written]);
            var f = _camera3D.Forward;
            if (text.TryWrite(CultureInfo.InvariantCulture, $"Forward: <{f.X:0.00}, {f.Y:0.00}, {f.Z:0.00}>", out written))
                ImGui.TextUnformatted(text[..written]);
        }
        ImGui.End();

        ImGuiCoordGizmo.DrawCoordinateGizmo(_camera3D);
    }

    protected override void OnShadowPass(in GameTime gameTime)
    {
        // Static lambdas with the node list as state: no per-frame closure allocations.
        _shadowSystem.RenderShadows(
            _lights,
            _nodes,
            draw2D: static (nodes, cb, _, _, _) =>
            {
                foreach (var node in nodes)
                    node.DrawShadow2D(cb);
            },
            drawPoint: static (nodes, cb, _, _, _, lightPos, lightRange) =>
            {
                foreach (var node in nodes)
                    node.DrawShadowPoint(cb, lightPos, lightRange);
            }
        );
    }

    protected override void OnRenderMainPass(in GameTime gameTime)
    {
        Renderer.EnableDepthTest();
        Renderer.Clear();

        // render core stuff
        var frameBufferSize = new Vector2(FramebufferSize.X, Math.Max(1, FramebufferSize.Y));
        _camera3D.AspectRatio = frameBufferSize.X / frameBufferSize.Y;
        _sky.Draw(_camera3D); // must be drawn first — renders behind all geometry
        _sceneGrid3d.Draw(_camera3D);

        // render game stuff
        foreach (var node in _nodes)
            node.Draw(_camera3D, _lights);
    }

    protected override void OnFrameCaptured(FrameCapture capture)
    {
        QaCapture?.Save(capture);
    }

    protected override void OnClose()
    {
        _shadowSystem.Dispose();
        _sky.Dispose();
        _sceneGrid3d.Dispose();
        foreach (var shape in _nodes)
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
