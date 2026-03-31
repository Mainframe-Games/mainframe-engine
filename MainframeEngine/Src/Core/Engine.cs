using Silk.NET.Core;
using Silk.NET.Input;
using Silk.NET.Maths;
using Silk.NET.Windowing;
using StbImageSharp;
using Monitor = Silk.NET.Windowing.Monitor;

namespace MainframeEngine;

public struct EngineOptions()
{
    public required string GameName;
    public RenderingBackend RenderingBackend = RenderingBackend.Vulkan;
    public Vector2D<int> WindowSize = new(800, 600);
    public string? IconPath;
}

public abstract class Engine : IDisposable
{
    private VulkanImGuiController? _vkImGuiController;

    private int _exitCode;
    private GameTime _gameTime;
    private readonly FPSCounter _fps = new();

    public IWindow Window { get; }
    public IInputContext InputContext { get; private set; } = null!;
    public IRenderer Renderer { get; private set; } = null!;
    
    private EngineOptions EngineOptions { get; }
    
    public string GameName => EngineOptions.GameName;
    public RenderingBackend RenderingBackend => EngineOptions.RenderingBackend;
    public Vector2D<int> WindowSize => EngineOptions.WindowSize;
    public string? IconPath => EngineOptions.IconPath;

    protected Engine(in EngineOptions engineOptions)
    {
        this.EngineOptions = engineOptions;
        
        var windowOptions = WindowOptions.DefaultVulkan with
        {
            Title = $"{engineOptions.GameName} ({engineOptions.RenderingBackend})",
            API = WindowOptions.DefaultVulkan.API with { Version = new APIVersion(1, 2) },
            Size = engineOptions.WindowSize
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

    protected virtual void OnLoad()
    {
        InputContext = Window.CreateInput();

        Renderer = new VulkanRenderer(Window, enableValidationLayers: true);

        if (Renderer is IVulkanContext vkCtx)
            _vkImGuiController = new VulkanImGuiController(vkCtx, InputContext, Window);
        
        SetWindowIcon(EngineOptions.IconPath);
    }
    
    private void SetWindowIcon(in string? path = null)
    {
        if (path is null)
            return;
        
        var img = ImageResult.FromMemory(
            File.ReadAllBytes(path),
            ColorComponents.RedGreenBlueAlpha
        ) ?? throw new NullReferenceException();
        var ico = new RawImage(img.Width, img.Height, img.Data);
        Window.SetWindowIcon(ref ico);
    }
    
    protected virtual void OnFramebufferResize(Vector2D<int> newSize)
    {
        Renderer.OnResize(newSize);
    }
    
    private void OnUpdate(double delta)
    {
        _fps.Update();
        _gameTime.DeltaTime = (float)delta;
        _gameTime.FrameCount = _fps.TotalFrameCount;
        _gameTime.FramesPerSecond = _fps.Fps;
        _gameTime.FramesTimeMs = _fps.Ms;

        _vkImGuiController?.Update((float)delta);

        OnImGui(_gameTime);
        OnUpdate(_gameTime);
    }

    private void OnRender(double delta)
    {
        Renderer.BeginFrame();

        // For Vulkan, BeginFrame can return early without starting a frame (swapchain recreation
        // during resize / fullscreen toggle). Skip draw calls in that case.
        if (Renderer is not IVulkanContext vk || vk.FrameStarted)
        {
            // Shadow pass runs before the main render pass (command buffer is open, no render pass active).
            OnShadowPass(_gameTime);

            // Begin the main render pass, then let the game draw geometry.
            (Renderer as IVulkanContext)?.BeginRenderPass();

            OnRenderMainPass(_gameTime);
            _vkImGuiController?.Render(); // inside the render pass, before EndFrame
        }

        Renderer.EndFrame();
    }
    
    protected abstract void OnImGui(in GameTime gameTime);
    protected abstract void OnUpdate(in GameTime gameTime);
    protected abstract void OnShadowPass(in GameTime gameTime);
    protected abstract void OnRenderMainPass(in GameTime gameTime);

    protected virtual void OnClose()
    {
        _exitCode = 0;
        _vkImGuiController?.Dispose();
        InputContext.Dispose();
        Renderer.Dispose();
    }

    public int Run()
    {
        Window.Run();
        return _exitCode;
    }

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
