using System.Numerics;
using Silk.NET.Input;
using Silk.NET.Maths;
using Silk.NET.OpenGL;
using Silk.NET.OpenGL.Extensions.ImGui;
using Silk.NET.Windowing;
using Monitor = Silk.NET.Windowing.Monitor;

namespace MainframeEngine;

public sealed class Engine : IDisposable
{
    private readonly IGame _game;
    private ImGuiController _imGuiController = null!;
    
    private int _exitCode;
    private GameTime _gameTime;
    
    private readonly FPSCounter _fps = new();

    public IWindow Window { get; }
    public IInputContext InputContext { get; private set; } = null!;
    public GL Gl { get; private set; } = null!;

    public Engine(in string gameName, in IGame game)
    {
        _game = game;

        var windowOptions = WindowOptions.Default;
        windowOptions.Title = gameName;
        windowOptions.API = windowOptions.API with { Version = new APIVersion(4, 6)};
        
        Window = Silk.NET.Windowing.Window.Create(windowOptions) ?? throw new NullReferenceException();
        Window.Load += OnLoad;
        Window.FramebufferResize += OnFramebufferResize;
        Window.Update += OnUpdate;
        Window.Render += OnRender;
        Window.Closing += OnClose;

        // set window to center of monitor
        var monitor = Monitor.GetMainMonitor(Window);
        var centerScreen = (monitor.VideoMode.Resolution - Window.Size) / 2;
        Window.Position = centerScreen!.Value;
    }

    private void OnLoad()
    {
        Gl = GL.GetApi(Window);

        var openGlVersion = Gl.GetStringS(GLEnum.Version);
        Log.Info($"OpenGL: {openGlVersion}");

        InputContext = Window.CreateInput();
        _imGuiController = new ImGuiController(Gl, Window, InputContext);
        _game.OnLoad(this);
    }

    private void OnFramebufferResize(Vector2D<int> newSize)
    {
        Gl.Viewport(newSize);
        _game.OnResize(new Vector2(newSize.X, newSize.Y));
    }

    /// <summary>
    /// Handles the per-frame update logic for the game, including gameplay updates,
    /// input processing, and updating the state of the graphics or UI frameworks.
    /// </summary>
    /// <param name="delta">The time elapsed since the last update, in seconds.</param>
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

    /// <summary>
    /// Executes the rendering logic for the game's current frame, ensuring that
    /// all visual elements are drawn and any immediate-mode GUI elements are rendered.
    /// </summary>
    /// <param name="delta">The time elapsed since the last frame, in seconds.</param>
    private void OnRender(double delta)
    {
        _game.OnRender(_gameTime);
        _imGuiController.Render();
    }

    /// <summary>
    /// Handles the cleanup of game resources and ensures proper disposal
    /// of components when the game window is closed.
    /// </summary>
    private void OnClose()
    {
        _exitCode = 0;
        _game.OnClose();
        _imGuiController.Dispose();
        InputContext.Dispose();
        Gl.Dispose();
    }

    /// <summary>
    /// Starts the main game loop and returns the exit code when the game terminates.
    /// </summary>
    /// <returns>The exit code of the application.</returns>
    public int Run()
    {
        Window.Run();
        return _exitCode;
    }

    /// <summary>
    /// Closes the game window and sets the exit code for the application.
    /// </summary>
    /// <param name="exitCode">The exit code to set when the game is closed.</param>
    public void Quit(in int exitCode)
    {
        _exitCode = exitCode;
        Window.Close();
    }

    public void Dispose()
    {
        Window.Dispose();
    }
}