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
    private readonly IWindow _window;

    private IInputContext _inputContext = null!;
    private GL _gl = null!;
    private ImGuiController _imGuiController = null!;

    private int _exitCode;
    private GameTime _gameTime;
    
    private readonly FPSCounter _fps = new();

    public Game(string gameName, IGame game)
    {
        _game = game;

        var windowOptions = WindowOptions.Default;
        windowOptions.Title = gameName;
        
        var window = Window.Create(windowOptions) ?? throw new NullReferenceException();
        window.Load += OnLoad;
        window.FramebufferResize += OnFramebufferResize;
        window.Update += OnUpdate;
        window.Render += OnRender;
        window.Closing += OnClose;

        _window = window;

        // set window to center of monitor
        var monitor = Monitor.GetMainMonitor(window);
        var centerScreen = (monitor.VideoMode.Resolution - window.Size) / 2;
        window.Position = centerScreen!.Value;
    }

    private void OnLoad()
    {
        _gl = GL.GetApi(_window);
        Log.Info($"OpenGL: {_gl.GetStringS(GLEnum.Version)}");

        _inputContext = _window.CreateInput();
        _imGuiController = new ImGuiController(_gl, _window, _inputContext);
        _game.OnLoad(_window, _gl, _inputContext);
    }

    private void OnFramebufferResize(Vector2D<int> newSize)
    {
        _gl.Viewport(newSize);
        _game.OnFramebufferResize(new Vector2(newSize.X, newSize.Y));
    }

    private void OnUpdate(double delta)
    {
        _fps.Update();
        
        _gameTime.DeltaTime = delta;
        _gameTime.FrameCount = _fps.TotalFrameCount;
        _gameTime.FramesPerSecond = _fps.Fps;
        _gameTime.FramesTimeMs = _fps.Ms;
        
        _imGuiController.Update((float)delta);
        _game.OnImGui(_gameTime);
        _game.OnUpdate(_gameTime);
    }

    private void OnRender(double delta)
    {
        _game.OnRender(_gameTime);
        _imGuiController.Render();
    }

    private void OnClose()
    {
        _game.OnClose();
        _imGuiController.Dispose();
        _inputContext.Dispose();
        _gl.Dispose();
    }

    public int Run()
    {
        _window.Run();
        return _exitCode;
    }

    public void Dispose()
    {
        _window.Dispose();
    }
}