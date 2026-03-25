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
    private Box3d _box3d = null!;

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
        _box3d = new Box3d(_renderer);
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

    public void OnImGui(in GameTime gameTime)
    {
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
        _box3d.Draw(_camera3D);
    }

    public void OnClose()
    {
        _sceneGrid3d.Dispose();
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
