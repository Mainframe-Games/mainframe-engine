using System.Drawing;
using System.Numerics;
using ImGuiNET;
using Silk.NET.Input;
using Silk.NET.OpenGL;
using Silk.NET.Windowing;

namespace MainframeEngine.Test;

public class GameTest : IGame
{
    private IWindow _window;
    private GL _gl;
    
    public void OnLoad(IWindow window, GL gl)
    {
        _window = window;
        _gl = gl;
        gl.ClearColor(Color.DarkSlateGray);
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
            ImGui.Value("FramesPerSecond", gameTime.FramesPerSecond);
            ImGui.Value("FramesTimeMs", gameTime.FramesTimeMs);
            
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
    }

    public void OnRender(GameTime gameTime)
    {
        _gl.Enable(EnableCap.DepthTest);
        _gl.Clear(ClearBufferMask.ColorBufferBit | ClearBufferMask.DepthBufferBit);
    }

    public void OnClose()
    {
    }

    public void OnKeyDown(IKeyboard keyboard, Key key, int arg3)
    {
    }
}