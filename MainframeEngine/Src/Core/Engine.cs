using System.Numerics;
using Silk.NET.Input;
using Silk.NET.Maths;
using Silk.NET.OpenGL.Extensions.ImGui;
using Silk.NET.Windowing;
using Monitor = Silk.NET.Windowing.Monitor;

namespace MainframeEngine;

public sealed class Engine : IDisposable
{
    private readonly IGame _game;
    private readonly RenderingBackend _renderingBackend;
    private ImGuiController? _imGuiController;
    private VulkanImGuiController? _vkImGuiController;

    private int _exitCode;
    private GameTime _gameTime;
    private readonly FPSCounter _fps = new();

    public IWindow Window { get; }
    public IInputContext InputContext { get; private set; } = null!;
    public IRenderer Renderer { get; private set; } = null!;

    public struct Info()
    {
        public required string GameName;
        public RenderingBackend RenderingBackend = RenderingBackend.Vulkan;
        public Vector2 WindowSize = new(800, 600);
    }

    public Engine(in Info info, IGame game)
    {
        _game = game;
        _renderingBackend = info.RenderingBackend;

        var windowSize = new Vector2D<int>((int)info.WindowSize.X, (int)info.WindowSize.Y);
        var windowOptions = _renderingBackend == RenderingBackend.Vulkan
            ? WindowOptions.DefaultVulkan with
            {
                Title = $"{info.GameName} ({_renderingBackend})",
                API = WindowOptions.DefaultVulkan.API with { Version = new APIVersion(1, 2) },
                Size = windowSize
                
            }
            : WindowOptions.Default with
            {
                Title = $"{info.GameName} ({_renderingBackend})",
                API = WindowOptions.Default.API with { Version = new APIVersion(4, 6) },
                Size = windowSize
            };

        Window = Silk.NET.Windowing.Window.Create(windowOptions) ?? throw new NullReferenceException();
        Window.Load += OnLoad;
        Window.FramebufferResize += OnFramebufferResize;
        Window.Update += OnUpdate;
        Window.Render += OnRender;
        Window.Closing += OnClose;

        var monitor = Monitor.GetMainMonitor(Window);
        var centerScreen = (monitor.VideoMode.Resolution - Window.Size) / 2;
        Window.Position = centerScreen!.Value;
    }

    private void OnLoad()
    {
        InputContext = Window.CreateInput();

        Renderer = _renderingBackend switch
        {
            RenderingBackend.OpenGL => CreateOpenGLRenderer(),
            RenderingBackend.Vulkan => new VulkanRenderer(Window, enableValidationLayers: true),
            _ => throw new ArgumentOutOfRangeException(nameof(_renderingBackend))
        };

        if (Renderer is IVulkanContext vkCtx)
            _vkImGuiController = new VulkanImGuiController(vkCtx, InputContext, Window);

        _game.OnLoad(this);
    }

    private IRenderer CreateOpenGLRenderer()
    {
        var gl = Silk.NET.OpenGL.GL.GetApi(Window);
        var version = gl.GetStringS(Silk.NET.OpenGL.GLEnum.Version);
        Log.Info($"[OpenGL] {version}");

        _imGuiController = new ImGuiController(gl, Window, InputContext);
        return new OpenGLRenderer(gl);
    }

    private void OnFramebufferResize(Vector2D<int> newSize)
    {
        Renderer.OnResize(newSize);
        _game.OnResize(new Vector2(newSize.X, newSize.Y));
    }

    private void OnUpdate(double delta)
    {
        _fps.Update();
        _gameTime.DeltaTime = delta;
        _gameTime.FrameCount = _fps.TotalFrameCount;
        _gameTime.FramesPerSecond = _fps.Fps;
        _gameTime.FramesTimeMs = _fps.Ms;

        _imGuiController?.Update((float)delta);
        _vkImGuiController?.Update((float)delta);

        _game.OnImGui(_gameTime);

        _game.OnUpdate(_gameTime);
    }

    private void OnRender(double delta)
    {
        Renderer.BeginFrame();

        // For Vulkan, BeginFrame can return early without starting a frame (swapchain recreation
        // during resize / fullscreen toggle). Skip draw calls in that case.
        if (Renderer is not IVulkanContext vk || vk.FrameStarted)
        {
            _game.OnRender(_gameTime);
            _vkImGuiController?.Render(); // inside the render pass, before EndFrame
        }

        Renderer.EndFrame();
        _imGuiController?.Render();
    }

    private void OnClose()
    {
        _exitCode = 0;
        _game.OnClose();
        _vkImGuiController?.Dispose();
        _imGuiController?.Dispose();
        InputContext.Dispose();
        Renderer.Dispose();
    }

    public int Run()
    {
        Window.Run();
        return _exitCode;
    }

    public void Quit(int exitCode)
    {
        _exitCode = exitCode;
        Window.Close();
    }

    public void Dispose()
    {
        Window.Dispose();
    }
}
