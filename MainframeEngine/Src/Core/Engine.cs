using Silk.NET.Core;
using Silk.NET.Input;
using Silk.NET.Input.Sdl;
using Silk.NET.Maths;
using Silk.NET.Windowing;
using Silk.NET.Windowing.Sdl;
using StbImageSharp;

namespace MainframeEngine;

public struct EngineOptions()
{
    public required string GameName;
    public RenderingBackend RenderingBackend = RenderingBackend.Vulkan;
    public Vector2D<int> WindowSize = new(800, 600);
    public string? IconPath;

    /// <summary>Starts with vertical sync on; toggle at runtime with <see cref="IRenderer.VSync"/>.</summary>
    public bool VSync = true;

    /// <summary>
    /// Enables the Khronos validation layers when they are installed. Messages are counted by
    /// <see cref="IVulkanContext.Validation"/>. Defaults to on in Debug builds of the engine and off in
    /// Release; set it explicitly to override (render tests force it on).
    /// </summary>
    public bool EnableValidation = DefaultEnableValidation;

    /// <summary>Validation default for this engine build: true in Debug, false in Release.</summary>
    public const bool DefaultEnableValidation =
#if DEBUG
        true;
#else
        false;
#endif

    /// <summary>
    /// Allows <see cref="Engine.CaptureFrame"/>. The swapchain gains transfer-source usage, which some
    /// drivers (MoltenVK) render slightly slower to, so it is opt-in.
    /// </summary>
    public bool EnableFrameCapture;

    /// <summary>Creates the window visible. Tests may hide it where the platform still presents.</summary>
    public bool WindowVisible = true;

    /// <summary>Closes the engine after this many rendered frames; 0 runs until the window closes.</summary>
    public int MaxFrames;

    /// <summary>
    /// When greater than zero, every update receives this delta time instead of wall-clock time, so
    /// simulation and animation are deterministic (golden-image tests, QA captures).
    /// </summary>
    public float FixedDeltaTime;

    /// <summary>Fixed rate of <see cref="Node.OnPhysicsProcess"/> and the physics servers (M2).</summary>
    public int PhysicsTicksPerSecond = 60;

    /// <summary>
    /// The game's Steam app id; when non-zero the engine registers a <see cref="SteamServer"/> (Steam stays
    /// optional: it is inert when Steam cannot start). 0 (default) leaves Steam alone.
    /// </summary>
    public uint SteamAppId;
}

public abstract class Engine : IDisposable
{
    private VulkanImGuiController? _vkImGuiController;

    private ExitCode _exitCode;
    private bool _waitingForRestore; // IsEventDriven was switched on while minimised
    private bool _quitRequested;      // Quit() called; the window closes after this iteration's render
    private GameTime _gameTime;
    private readonly FPSCounter _fps = new();
    private int _renderedFrames;
    private InputRouter? _inputRouter; // M2: routes window input into the scene tree

    public IWindow Window { get; }
    public IInputContext InputContext { get; private set; } = null!;
    public IRenderer Renderer { get; private set; } = null!;

    // --- M2 scene tree ------------------------------------------------------------------------------
    // The engine owns a SceneTree and runs it every frame (process after the legacy OnUpdate hook, the
    // RenderServer draws its root world after the legacy render hooks open each pass). Games may keep
    // using only the legacy hooks: an empty tree costs nothing.

    /// <summary>The engine-owned scene tree; add nodes under <see cref="Root"/> or call <see cref="SceneTree.ChangeScene(Node)"/>.</summary>
    public SceneTree Tree { get; }

    /// <summary>The tree's root viewport (<c>/root</c>).</summary>
    public SceneViewport Root => Tree.Root;

    /// <summary>Engine servers (the <see cref="RenderServer"/> exists after <see cref="OnLoad"/>).</summary>
    public ServerRegistry Servers => Tree.Servers;

    private EngineOptions EngineOptions { get; }

    /// <summary>Number of frames rendered and presented so far (skipped frames are not counted).</summary>
    public int RenderedFrameCount => _renderedFrames;

    public string GameName => EngineOptions.GameName;
    public RenderingBackend RenderingBackend => EngineOptions.RenderingBackend;
    public Vector2D<int> WindowSize => EngineOptions.WindowSize;
    public string? IconPath => EngineOptions.IconPath;

    /// <summary>
    /// Framebuffer size in pixels — what the swapchain, viewports and aspect ratios use. On HiDPI
    /// displays this is larger than <see cref="IWindow.Size"/> (points); prefer it over
    /// <see cref="IWindow.FramebufferSize"/>, which SDL reports in points for Vulkan windows.
    /// </summary>
    public Vector2D<int> FramebufferSize => WindowPixels.FramebufferSize(Window);

    /// <summary>
    /// Caps the frame rate. Set to 0 for unlimited.
    /// Has no effect when VSync is enabled (the display refresh rate governs timing).
    /// </summary>
    public int MaxFPS
    {
        get => (int)Window.FramesPerSecond;
        set
        {
            Window.FramesPerSecond = value > 0 ? value : 0;
            Window.UpdatesPerSecond = value > 0 ? value : 0;
        }
    }

    protected Engine(in EngineOptions engineOptions)
    {
        EngineOptions = engineOptions;
        Tree = new SceneTree { PhysicsTicksPerSecond = engineOptions.PhysicsTicksPerSecond }; // M2

        // Linux: Silk.NET cannot find package natives (libSDL2) in runtimes/linux-x64/native on its own.
        SilkNativeResolver.Install();
        UseSdl();

        // macOS: SDL can't find the Vulkan loader on its own (dyld no longer searches
        // /usr/local/lib for leaf-name dlopen) — hand it one before the window is created.
        VulkanLoaderBootstrap.Initialize();

        var windowOptions = WindowOptions.DefaultVulkan with
        {
            Title = $"{engineOptions.GameName} ({engineOptions.RenderingBackend})",
            API = WindowOptions.DefaultVulkan.API with { Version = new APIVersion(1, 2) },
            Size = engineOptions.WindowSize,
            IsVisible = engineOptions.WindowVisible,
        };

        try
        {
            Window = Silk.NET.Windowing.Window.Create(windowOptions) ?? throw new InvalidOperationException("Failed to create the window.");
        }
        catch (PlatformNotSupportedException e)
        {
            // Silk reports only "not applicable" — surface SDL's own reason (missing video driver, display, …).
            throw new PlatformNotSupportedException($"{e.Message} SDL: {DescribeSdlFailure()}", e);
        }
        Window.Load += CenterWindow;
        Window.Load += OnLoad;
        Window.FramebufferResize += OnFramebufferResize;
        Window.Update += OnUpdate;
        Window.Render += OnRender;
        Window.Closing += OnClose;
    }

    /// <summary>
    /// Windowing and input run on SDL2 (Silk.NET's SDL backend). Registered explicitly rather than
    /// discovered by reflection, so trimmed/AOT builds keep the backend; GLFW is not referenced.
    /// </summary>
    private static void UseSdl()
    {
        SdlWindowing.RegisterPlatform();
        SdlInput.RegisterPlatform();
        Silk.NET.Windowing.Window.PrioritizeSdl();
    }

    private static string DescribeSdlFailure()
    {
        try
        {
            var sdl = Silk.NET.SDL.SdlProvider.SDL.Value;
            return sdl.GetErrorS() is { Length: > 0 } error ? error : "SDL loaded but reported no error";
        }
        catch (Exception e)
        {
            return $"{e.GetType().Name}: {e.Message}";
        }
    }

    // Monitors can only be queried once the window exists, and there may be none at all (a
    // sleeping display on macOS, some headless X servers).
    private void CenterWindow()
    {
        if (Window.Monitor is { } monitor)
            Window.Center(monitor);
    }

    protected virtual void OnLoad()
    {
        InputContext = Window.CreateInput();
        Log.Info($"[Window] SDL window {Window.Size.X}x{Window.Size.Y} pt, framebuffer {FramebufferSize.X}x{FramebufferSize.Y} px");

        Renderer = new VulkanRenderer(Window, new VulkanRendererOptions
        {
            EnableValidation = EngineOptions.EnableValidation,
            VSync = EngineOptions.VSync,
            EnableFrameCapture = EngineOptions.EnableFrameCapture,
        });

        if (Renderer is IVulkanContext vkCtx)
            _vkImGuiController = new VulkanImGuiController(vkCtx, InputContext, Window);

        // M2: the render server replaces the static Node.Initialize; tree input comes from the window.
        Servers.Register(new RenderServer(Renderer));
        if (EngineOptions.SteamAppId != 0)
            Servers.Register(new SteamServer(EngineOptions.SteamAppId));
        _inputRouter = new InputRouter(InputContext, Tree);

        SetWindowIcon(EngineOptions.IconPath);
    }

    private void SetWindowIcon(in string? path = null)
    {
        if (path is null)
            return;

        var img = ImageResult.FromMemory(
            File.ReadAllBytes(ContentPaths.Resolve(path)),
            ColorComponents.RedGreenBlueAlpha
        ) ?? throw new InvalidDataException($"Failed to decode window icon '{path}'.");
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
        _gameTime.DeltaTime = EngineOptions.FixedDeltaTime > 0f ? EngineOptions.FixedDeltaTime : (float)delta;
        _gameTime.FrameCount = _fps.TotalFrameCount;
        _gameTime.FramesPerSecond = _fps.Fps;
        _gameTime.FramesTimeMs = _fps.Ms;

        _vkImGuiController?.Update(_gameTime.DeltaTime);

        OnImGui(_gameTime);
        OnUpdate(_gameTime);
        Tree.Tick(_gameTime); // M2: physics steps, process, deferred calls/frees, transform sync
    }

    private void OnRender(double delta)
    {
        RenderFrame();

        // Close only between frames: SDL raises Closing synchronously inside Close(), and OnClose
        // disposes the renderer — never while a frame is being recorded or an update is running.
        if (_quitRequested || (EngineOptions.MaxFrames > 0 && _renderedFrames >= EngineOptions.MaxFrames))
            Window.Close();
    }

    private void RenderFrame()
    {
        // Minimised (or no drawable area): render nothing and block on window events instead of
        // spinning the loop; the frame's ImGui NewFrame is closed so frames stay paired.
        if (IsMinimised())
        {
            _vkImGuiController?.DiscardFrame();
            if (!Window.IsEventDriven)
            {
                Window.IsEventDriven = true;
                _waitingForRestore = true;
            }
            return;
        }

        if (_waitingForRestore)
        {
            Window.IsEventDriven = false;
            _waitingForRestore = false;
        }

        Renderer.BeginFrame();

        // For Vulkan, BeginFrame can return early without starting a frame (swapchain recreation
        // during resize / fullscreen toggle). Skip draw calls in that case.
        var frameStarted = Renderer is not IVulkanContext vk || vk.FrameStarted;
        if (frameStarted)
        {
            // Shadow pass runs before the main render pass (command buffer is open, no render pass active).
            OnShadowPass(_gameTime);
            Servers.Render?.RenderShadows(Root); // M2: the scene tree's shadow casters

            // Begin the main render pass, then let the game draw geometry.
            (Renderer as IVulkanContext)?.BeginRenderPass();

            Servers.Render?.RenderMain(Root); // M2: sky, then the scene tree's visuals
            OnRenderMainPass(_gameTime);
            _vkImGuiController?.Render(); // tonemaps the scene target, then ImGui in the overlay pass
        }
        else
        {
            _vkImGuiController?.DiscardFrame();
        }

        Renderer.EndFrame();

        if (!frameStarted)
            return;

        _renderedFrames++;

        if (Renderer.TryTakeCapture(out var capture))
            OnFrameCaptured(capture);
    }

    /// <summary>
    /// Copies the frame being built in this iteration (call from <see cref="OnImGui"/>,
    /// <see cref="OnUpdate(in GameTime)"/> or a render hook) back to the CPU once it has been rendered;
    /// <see cref="OnFrameCaptured"/> receives the pixels. Requires
    /// <see cref="EngineOptions.EnableFrameCapture"/>.
    /// </summary>
    public void CaptureFrame()
    {
        if (!EngineOptions.EnableFrameCapture)
            throw new InvalidOperationException($"Frame capture is disabled; set {nameof(EngineOptions)}.{nameof(EngineOptions.EnableFrameCapture)}.");

        Renderer.RequestCapture();
    }

    /// <summary>Receives the result of <see cref="CaptureFrame"/> after the frame has been presented.</summary>
    protected virtual void OnFrameCaptured(FrameCapture capture)
    {
    }

    // Legacy per-frame hooks, optional since the scene tree (M2) can drive everything.

    /// <summary>Build ImGui windows (inside the ImGui frame, before update).</summary>
    protected virtual void OnImGui(in GameTime gameTime)
    {
    }

    /// <summary>Game logic, before the scene tree's tick.</summary>
    protected virtual void OnUpdate(in GameTime gameTime)
    {
    }

    /// <summary>Depth pre-pass recording (no render pass active), before the scene tree's shadow casters.</summary>
    protected virtual void OnShadowPass(in GameTime gameTime)
    {
    }

    /// <summary>Main-pass drawing, after the scene tree's sky and visuals.</summary>
    protected virtual void OnRenderMainPass(in GameTime gameTime)
    {
    }

    private bool IsMinimised()
    {
        if (Window.WindowState == WindowState.Minimized)
            return true;
        var size = FramebufferSize;
        return size.X <= 0 || size.Y <= 0;
    }

    /// <summary>
    /// Frees the scene tree and servers, then disposes ImGui, input and the renderer. Call <c>base.OnClose()</c>
    /// last. Keeps the exit code set by <see cref="Quit"/>.
    /// </summary>
    protected virtual void OnClose()
    {
        // M2: free the scene (nodes release their GPU objects), then the servers and cached resources,
        // while the renderer is still alive.
        _inputRouter?.Dispose();
        _inputRouter = null;
        Tree.Shutdown();
        Servers.Dispose();
        ResourceLoader.ClearCache();

        _vkImGuiController?.Dispose();
        _vkImGuiController = null;
        InputContext?.Dispose();
        Renderer?.Dispose();
    }

    public ExitCode Run()
    {
        Window.Run();
        return _exitCode;
    }

    /// <summary>
    /// Requests shutdown with <paramref name="exitCode"/> (returned by <see cref="Run"/>). The window
    /// closes at the end of the current iteration's render, so it is safe to call from
    /// <see cref="OnUpdate(in GameTime)"/>, <see cref="OnImGui"/> or input handlers.
    /// </summary>
    public void Quit(in ExitCode exitCode)
    {
        _exitCode = exitCode;
        _quitRequested = true;
    }

    public void Dispose()
    {
        Window.Dispose();
    }
}
