using System.Drawing;
using System.Numerics;
using ImGuiNET;
using Mainframe.Silk;
using Silk.NET.Input;
using Silk.NET.OpenGL;
using Silk.NET.Windowing;

namespace MainframeEngine.Test;

public class GameTest : IGame
{
    private IWindow _window = null!;
    private GL _gl = null!;
    
    private readonly CameraPerspective _cameraPerspective = new ()
    {
        Position = new Vector3(1, 1, 5f),
    };
    
    private SceneGrid3d _sceneGrid3d = null!;

    private double _sampleTime;
    private GameTime _gameTimeSample;

    public void OnLoad(IWindow window, GL gl)
    {
        _window = window;
        _gl = gl;
        gl.ClearColor(Color.DarkSlateGray);

        _sceneGrid3d = new SceneGrid3d(gl);
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
            ImGui.Value("FramesPerSecond", _gameTimeSample.FramesPerSecond);
            ImGui.Value("FramesTimeMs", _gameTimeSample.FramesTimeMs);
            
            var vsync = _window.VSync;
            if (ImGui.Checkbox("VSync", ref vsync))
                _window.VSync = vsync;
            
            var isFullScreen = _window.WindowState is WindowState.Fullscreen;
            if (ImGui.Checkbox("FullScreen", ref isFullScreen))
                _window.WindowState = isFullScreen ? WindowState.Fullscreen : WindowState.Normal;
        }
        ImGui.End();
    }

    public void OnUpdate(GameTime gameTime)
    {
        _sampleTime += gameTime.DeltaTime;
        if (_sampleTime > 0.5f)
        {
            _gameTimeSample = gameTime;
            _sampleTime = 0;
        }
    }

    public void OnRender(GameTime gameTime)
    {
        _gl.Enable(EnableCap.DepthTest);
        _gl.Clear(ClearBufferMask.ColorBufferBit | ClearBufferMask.DepthBufferBit);
        
        var frameBufferSize = new Vector2(_window.FramebufferSize.X, _window.FramebufferSize.Y);
        _cameraPerspective.AspectRatio = frameBufferSize.X / frameBufferSize.Y;
        _sceneGrid3d.Draw(_cameraPerspective.ViewMatrix, _cameraPerspective.ProjectionMatrix);
    }

    public void OnClose()
    {
    }

    public void OnKeyDown(IKeyboard keyboard, Key key, int arg3)
    {
    }
}