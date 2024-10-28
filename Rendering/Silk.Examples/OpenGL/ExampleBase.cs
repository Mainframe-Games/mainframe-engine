using System.Drawing;
using Silk.NET.Input;
using Silk.NET.Maths;
using Silk.NET.OpenGL;
using Silk.NET.Windowing;

namespace SilkTest.Examples;

internal abstract class ExampleBase : IExample
{
    protected IWindow Window { get; private set; } = null!;
    protected IInputContext InputContext { get; private set; } = null!;
    protected GL Gl { get; private set; } = null!;

    public virtual void OnLoad(IWindow window)
    {
        Window = window;

        InputContext = window.CreateInput();
        for (int i = 0; i < InputContext.Keyboards.Count; i++)
            InputContext.Keyboards[i].KeyDown += OnKeyDown;

        //Getting the opengl api for drawing to the screen.
        Gl = GL.GetApi(window);
        Gl.ClearColor(Color.DarkSlateGray);
    }

    private void OnKeyDown(IKeyboard keyboard, Key key, int arg3)
    {
        if (key == Key.Escape)
            Window.Close();
    }

    public virtual void OnUpdate(double deltaTime)
    {
        var isAlt = InputContext.Keyboards[0].IsKeyPressed(Key.AltLeft);
        InputContext.Mice[0].Cursor.CursorMode = isAlt ? CursorMode.Normal : CursorMode.Raw;
    }

    public virtual void OnRender(double deltaTime)
    {
        Gl.Clear((uint)ClearBufferMask.ColorBufferBit);
    }

    public virtual void OnFramebufferResize(Vector2D<int> newSize)
    {
        Gl.Viewport(newSize);
    }

    public virtual void OnClose()
    {
        InputContext.Dispose();
        Gl.Dispose();
    }
}
