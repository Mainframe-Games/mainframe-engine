using System.Drawing;
using System.Numerics;
using ImGuiNET;
using Silk.NET.Core;
using Silk.NET.Input;
using Silk.NET.OpenGL;
using Silk.NET.Windowing;
using StbImageSharp;
using MouseButton = Silk.NET.Input.MouseButton;

namespace MainframeEngine.Test;

public class GameTest : IGame
{
    private IWindow _window = null!;
    private GL _gl = null!;
    
    private readonly CameraPerspective _cameraPerspective = new()
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

    public void OnLoad(IWindow window, GL gl, IInputContext inputContext)
    {
        _window = window;
        _gl = gl;
        gl.ClearColor(Color.DarkSlateGray);

        // Set window icon
        SetWindowIcon();

        // assign input callbacks
        _keyboard = inputContext.Keyboards[0];
        _mouse = inputContext.Mice[0];
        
        _keyboard.KeyDown += OnKeyDown;
        
        _mouse.MouseDown += OnMouseDown;
        _mouse.MouseUp += OnMouseUp;
        _mouse.MouseMove += OnMouseMove;
        
        // create scene objects
        _sceneGrid3d = new SceneGrid3d(gl);
        _box3d = new Box3d(gl);
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

    public void OnFramebufferResize(Vector2 newSize)
    {
    }

    public void OnImGui(GameTime gameTime)
    {
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

    public void OnUpdate(GameTime gameTime)
    {
        UpdateCameraPosition(gameTime.DeltaTime);
    }

    public void OnRender(GameTime gameTime)
    {
        _gl.Enable(EnableCap.DepthTest);
        _gl.Clear(ClearBufferMask.ColorBufferBit | ClearBufferMask.DepthBufferBit);
        
        // draw scene grid
        var frameBufferSize = new Vector2(_window.FramebufferSize.X, _window.FramebufferSize.Y);
        _cameraPerspective.AspectRatio = frameBufferSize.X / frameBufferSize.Y;
        _sceneGrid3d.Draw(_cameraPerspective);
        
        // draw default cube
        _box3d.Draw(_cameraPerspective);
    }

    public void OnClose()
    {
    }
    
    private void UpdateCameraPosition(double deltaTime)
    {
        if (!CanMoveCamera)
            return;

        var camera = _cameraPerspective;

        var baseSpeed = _keyboard.IsKeyPressed(Key.ShiftLeft) ? _cameraSpeed * 2 : _cameraSpeed;
        var moveSpeed = baseSpeed * (float)deltaTime;

        var isPerspectiveCamera = camera is CameraPerspective;

        if (isPerspectiveCamera)
        {
            // forward
            if (_keyboard.IsKeyPressed(Key.W))
                camera.Position += moveSpeed * camera.Forward;
            
            // back
            if (_keyboard.IsKeyPressed(Key.S))
                camera.Position -= moveSpeed * camera.Forward;
            
            // left
            if (_keyboard.IsKeyPressed(Key.A))
                camera.Position -=
                    Vector3.Normalize(Vector3.Cross(camera.Forward, camera.Up)) * moveSpeed;
        
            // right
            if (_keyboard.IsKeyPressed(Key.D))
                camera.Position +=
                    Vector3.Normalize(Vector3.Cross(camera.Forward, camera.Up)) * moveSpeed;
            
            // up
            if (_keyboard.IsKeyPressed(Key.Q))
                camera.Position -= camera.Up * moveSpeed;

            // down
            if (_keyboard.IsKeyPressed(Key.E))
                camera.Position += camera.Up * moveSpeed;
        }
        else
        {
            // up
            if (_keyboard.IsKeyPressed(Key.W))
                camera.Position += camera.Up * moveSpeed;

            // down
            if (_keyboard.IsKeyPressed(Key.S))
                camera.Position -= camera.Up * moveSpeed;
            
            // left
            if (_keyboard.IsKeyPressed(Key.A))
                camera.Position -=
                    Vector3.Normalize(Vector3.Cross(camera.Forward, camera.Up)) * moveSpeed;
        
            // right
            if (_keyboard.IsKeyPressed(Key.D))
                camera.Position +=
                    Vector3.Normalize(Vector3.Cross(camera.Forward, camera.Up)) * moveSpeed;
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
            // reset last position so camera doesn't make massive jump when move mouse again
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
            _cameraPerspective.ModifyDirection(xOffset, yOffset);
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