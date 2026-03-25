using System.Drawing;
using System.Numerics;
using ImGuiNET;
using Silk.NET.Core;
using Silk.NET.Input;
using Silk.NET.Windowing;
using StbImageSharp;
using MouseButton = Silk.NET.Input.MouseButton;

namespace MainframeEngine.Sandbox;

public class GameTest : IGame
{
    private IWindow _window = null!;
    private IRenderer _renderer = null!;

    private readonly Camera3D _camera3D = new()
    {
        Position = new Vector3(1, 1, 5f),
    };

    private SceneGrid3d _sceneGrid3d = null!;
    private Quad _quad = null!;
    private Box3d _box3d = null!;

    private readonly LightEnvironment _lights = new();
    private readonly DirectionalLight _dirLight  = new() { Direction = Vector3.Normalize(new Vector3(-1, -2, -1)), Color = new Vector3(1f, 0.95f, 0.8f), Intensity = 0.9f };
    private readonly PointLight       _pointLight = new() { Position = new Vector3(3, 2, 2), Color = new Vector3(0.2f, 0.5f, 1f), Intensity = 5f, Range = 8f };
    private readonly SpotLight        _spotLight  = new() { Position = new Vector3(-2, 4, 2), Direction = Vector3.Normalize(new Vector3(0.5f, -1, -0.5f)), Color = new Vector3(1f, 0.8f, 0.2f), Intensity = 8f, Range = 15f, InnerConeAngle = 12f, OuterConeAngle = 25f };

    private IKeyboard _keyboard = null!;
    private IMouse _mouse = null!;

    private bool CanMoveCamera => _mouse.Cursor.CursorMode is CursorMode.Raw;
    private Vector2 _lastMousePosition;
    private float _cameraSpeed = 10;

    public void OnLoad(in Engine engine)
    {
        _window = engine.Window;
        _renderer = engine.Renderer;
        _renderer.SetClearColor(0.18f, 0.31f, 0.31f); // DarkSlateGray

        SetWindowIcon();

        _keyboard = engine.InputContext.Keyboards[0];
        _keyboard.KeyDown += OnKeyDown;

        _mouse = engine.InputContext.Mice[0];
        _mouse.MouseDown += OnMouseDown;
        _mouse.MouseUp += OnMouseUp;
        _mouse.MouseMove += OnMouseMove;

        _sceneGrid3d = new SceneGrid3d(_renderer);
        _quad = new Quad(_renderer) { Color = new Vector3(0.8f, 0.3f, 0.2f) };
        _box3d = new Box3d(_renderer) { Color = new Vector3(0.8f, 0.3f, 0.2f) };

        _lights.DirectionalLights.Add(_dirLight);
        _lights.PointLights.Add(_pointLight);
        _lights.SpotLights.Add(_spotLight);
    }

    private void SetWindowIcon()
    {
        var img = ImageResult.FromMemory(
            File.ReadAllBytes("Content/Branding/mg_300_circle.png"),
            ColorComponents.RedGreenBlueAlpha
        ) ?? throw new NullReferenceException();
        var ico = new RawImage(img.Width, img.Height, img.Data);
        _window.SetWindowIcon(ref ico);
    }

    public void OnResize(in Vector2 newSize)
    {
    }

    private static bool TryProjectToScreen(Vector3 worldPos, Matrix4x4 view, Matrix4x4 proj, Vector2 screenSize, out Vector2 screenPos)
    {
        var clip = Vector4.Transform(new Vector4(worldPos, 1f), view * proj);
        if (clip.W <= 0f)
        {
            screenPos = default;
            return false;
        }
        var ndc = new Vector3(clip.X / clip.W, clip.Y / clip.W, clip.Z / clip.W);
        screenPos = new Vector2(
            (ndc.X * 0.5f + 0.5f) * screenSize.X,
            (1.0f - (ndc.Y * 0.5f + 0.5f)) * screenSize.Y
        );
        return true;
    }

    private void DrawLightGizmos()
    {
        var drawList = ImGui.GetBackgroundDrawList();
        var screenSize = new Vector2(_window.FramebufferSize.X, _window.FramebufferSize.Y);
        var view = _camera3D.ViewMatrix;
        var proj = _camera3D.ProjectionMatrix;

        foreach (var pt in _lights.PointLights)
        {
            if (!TryProjectToScreen(pt.Position, view, proj, screenSize, out var sp)) continue;
            var col = new Vector4(pt.Color, 1f);
            var packed = ImGui.ColorConvertFloat4ToU32(col);
            drawList.AddCircleFilled(sp, 8f, packed);
            drawList.AddCircle(sp, 10f, 0xFFFFFFFF, 0, 1.5f);
        }

        foreach (var sl in _lights.SpotLights)
        {
            if (!TryProjectToScreen(sl.Position, view, proj, screenSize, out var sp)) continue;
            var col = new Vector4(sl.Color, 1f);
            var packed = ImGui.ColorConvertFloat4ToU32(col);
            drawList.AddCircleFilled(sp, 8f, packed);
            drawList.AddCircle(sp, 10f, 0xFFFFFFFF, 0, 1.5f);

            var dirEnd = sl.Position + sl.Direction * 1.5f;
            if (TryProjectToScreen(dirEnd, view, proj, screenSize, out var ep))
                drawList.AddLine(sp, ep, 0xFFFFFFFF, 1.5f);
        }

        foreach (var dl in _lights.DirectionalLights)
        {
            var origin = new Vector2(screenSize.X - 60f, 60f);
            var dir2d = new Vector2(dl.Direction.X, dl.Direction.Y);
            if (dir2d.LengthSquared() > 0.0001f)
                dir2d = Vector2.Normalize(dir2d);
            var col = new Vector4(dl.Color, 1f);
            var packed = ImGui.ColorConvertFloat4ToU32(col);
            drawList.AddLine(origin, origin + dir2d * 40f, packed, 2f);
            drawList.AddCircleFilled(origin, 5f, packed);
        }
    }

    public void OnImGui(in GameTime gameTime)
    {
        DrawLightGizmos();

        ImGui.SetNextWindowPos(Vector2.Zero, ImGuiCond.Always, new Vector2(0, 0));
        if (ImGui.Begin("Game Window", ImGuiWindowFlags.AlwaysAutoResize))
        {
            ImGui.Value("FrameCount", gameTime.FrameCount);
            ImGui.Value("DeltaTime", (float)gameTime.DeltaTime);
            ImGui.Value("FPS", gameTime.FramesPerSecond);
            ImGui.Value("Ms", gameTime.FramesTimeMs);

            var vsync = _window.VSync;
            if (ImGui.Checkbox("VSync", ref vsync))
            {
                if (vsync != _window.VSync)
                    _window.VSync = vsync;
            }

            var isFullScreen = _window.WindowState is WindowState.Fullscreen;
            if (ImGui.Checkbox("FullScreen", ref isFullScreen))
                _window.WindowState = isFullScreen ? WindowState.Fullscreen : WindowState.Normal;
        }
        ImGui.End();
    }

    public void OnUpdate(in GameTime gameTime)
    {
        UpdateCameraPosition(gameTime.DeltaTime);
    }

    public void OnRender(in GameTime gameTime)
    {
        _renderer.EnableDepthTest();
        _renderer.Clear();

        var frameBufferSize = new Vector2(_window.FramebufferSize.X, _window.FramebufferSize.Y);
        _camera3D.AspectRatio = frameBufferSize.X / frameBufferSize.Y;
        _sceneGrid3d.Draw(_camera3D);
        _quad.Draw(_camera3D);
        _box3d.Draw(_camera3D, _lights);
    }

    public void OnClose()
    {
        _sceneGrid3d.Dispose();
        _quad.Dispose();
        _box3d.Dispose();
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
            _window.Close();
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
