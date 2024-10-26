using System.Numerics;
using Silk.NET.Input;
using Silk.NET.Maths;
using Silk.NET.OpenGL;
using Silk.NET.OpenGL.Extensions.ImGui;
using Silk.NET.Windowing;
using Monitor = Silk.NET.Windowing.Monitor;

namespace MainframeEngine;

public sealed class Game : IDisposable
{
    private readonly IGame _game;
    private int _exitCode;

    private IWindow Window { get; }
    private IInputContext InputContext { get; set; } = null!;
    private GL Gl { get; set; } = null!;
    private ImGuiController ImGuiController { get; set; } = null!;

    public Game(string gameName, IGame game)
    {
        _game = game;

        var windowOptions = WindowOptions.Default;
        windowOptions.Title = gameName;
        
        var window =
            Silk.NET.Windowing.Window.Create(windowOptions) ?? throw new NullReferenceException();
        window.Load += OnLoad;
        window.FramebufferResize += OnFramebufferResize;
        window.Update += OnUpdate;
        window.Render += OnRender;
        window.Closing += OnClose;

        Window = window;

        // set window to center of monitor
        var monitor = Monitor.GetMainMonitor(window);
        var centerScreen = (monitor.VideoMode.Resolution - window.Size) / 2;
        window.Position = centerScreen!.Value;
    }

    private void OnLoad()
    {
        Gl = GL.GetApi(Window);
        Console.WriteLine($"OpenGL: {Gl.GetStringS(GLEnum.Version)}");

        InputContext = Window.CreateInput();
        for (int i = 0; i < InputContext.Keyboards.Count; i++)
            InputContext.Keyboards[i].KeyDown += OnKeyDown;
        
        ImGuiController = new ImGuiController(Gl, Window, InputContext);
        _game.OnLoad(Gl);
    }

    private void OnFramebufferResize(Vector2D<int> newSize) =>
        _game.OnFramebufferResize(new Vector2(newSize.X, newSize.Y));

    private void OnUpdate(double delta) => _game.OnUpdate(new GameTime { DeltaTime = delta });

    private void OnRender(double delta) => _game.OnRender(new GameTime { DeltaTime = delta });

    private void OnClose()
    {
        _game.OnClose();
        ImGuiController.Dispose();
        InputContext.Dispose();
        Gl.Dispose();
    }

    private void OnKeyDown(IKeyboard keyboard, Key key, int arg3) =>
        _game.OnKeyDown(keyboard, key, arg3);

    public int Run()
    {
        Window.Run();
        return _exitCode;
    }

    public void Dispose()
    {
        Window.Dispose();
    }
}
