using System.Drawing;
using System.Numerics;
using Silk.NET.Input;
using Silk.NET.OpenGL;
using SpineTest;

namespace MainframeEngine.Test;

public class GameTest : IGame
{
    private GL _gl;
    
    public void OnLoad(GL gl)
    {
        _gl = gl;
        gl.ClearColor(Color.DarkSlateGray);
    }

    public void OnFramebufferResize(Vector2 newSize)
    {
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