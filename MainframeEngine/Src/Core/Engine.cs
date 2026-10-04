using System.Diagnostics;
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
    /// <summary>Initial window size in points (OS points, or layout points when <see cref="ContentScale"/> is fixed).</summary>
    public Vector2D<int> WindowSize = new(800, 600);
    public string? IconPath;

    /// <summary>
    /// Fixed content scale: pixels per point of <see cref="WindowSize"/>. When greater than zero the framebuffer (swapchain,
    /// frame captures) is exactly <see cref="WindowSize"/> × <see cref="ContentScale"/> pixels on any display: the OS window
    /// is sized for the backing scale of the display it lands on (a 2× Retina window gets half the points of a 1× monitor's
    /// one), and the UI's dp ratio and ImGui use this scale instead of the display's. Start-up fails when the window cannot
    /// reach that size (a fractional display scale that does not divide it). 0 (default) follows the display: the window
    /// is <see cref="WindowSize"/> OS points and the framebuffer whatever that is in pixels. Render tests and QA captures
    /// set it so their images do not depend on the monitor.
    /// </summary>
    public float ContentScale;

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

    /// <summary>3D physics project settings (gravity, solver, threading) for the <see cref="PhysicsServer3D"/>.</summary>
    public PhysicsSettings3D Physics3D = new();

    /// <summary>2D physics project settings (gravity, <see cref="PhysicsSettings2D.PixelsPerMeter"/>) for the <see cref="PhysicsServer2D"/>.</summary>
    public PhysicsSettings2D Physics2D = new();

    /// <summary>Draw every collision shape each frame (Godot's "Visible Collision Shapes"); toggle later on the physics servers.</summary>
    public bool DebugCollisionShapes;

    /// <summary>
    /// The game's Steam app id; when non-zero the engine registers a <see cref="SteamServer"/> (Steam stays
    /// optional: it is inert when Steam cannot start). 0 (default) leaves Steam alone.
    /// </summary>
    public uint SteamAppId;

    /// <summary>
    /// Audio (M7): the engine registers an <see cref="AudioServer"/> when <see cref="AudioOptions.Enabled"/>. Without a
    /// usable device it runs on the silent null device; startup never fails because of audio.
    /// </summary>
    public AudioOptions Audio = new();

    /// <summary>
    /// Registers the game UI server (<see cref="UiServer"/>, RmlUi; M8). Default on. Start-up fails with a clear
    /// error when the native RmlUi library is missing or incompatible.
    /// </summary>
    public bool EnableUi = true;

    /// <summary>UI server options (fonts, hot reload, source content folders); null uses the defaults.</summary>
    public UiServerOptions? Ui;

    /// <summary>Shows the ImGui developer overlay at start-up; F12 toggles it at runtime (<see cref="Engine.DevOverlayVisible"/>).</summary>
    public bool DevOverlayVisible = true;

    /// <summary>
    /// Starting locale (<c>es</c>, <c>pt_BR</c>), typically from the player's settings. Null picks the OS UI language
    /// when catalogs exist for it, otherwise <see cref="Localization.LocalizationOptions.SourceLocale"/> (M9).
    /// </summary>
    public string? Locale;

    /// <summary>Catalog location, domain, source locale and fallbacks for <see cref="Localization.Tr"/>; null uses the defaults.</summary>
    public Localization.LocalizationOptions? Localization;
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

    /// <summary>The game UI server (null when <see cref="EngineOptions.EnableUi"/> is off or before <see cref="OnLoad"/>).</summary>
    public UiServer? Ui => Servers.Get<UiServer>();

    /// <summary>
    /// Whether the ImGui developer overlay (<see cref="OnImGui"/>, renderer/debug windows) is drawn. F12 toggles it.
    /// The game UI is unaffected.
    /// </summary>
    public bool DevOverlayVisible { get; set; }

    /// <summary>The key that toggles <see cref="DevOverlayVisible"/>.</summary>
    public Key DevOverlayKey { get; set; } = Key.F12;

    // --- M2 scene tree ------------------------------------------------------------------------------
    // The engine owns a SceneTree and runs it every frame (process after the legacy OnUpdate hook, the
    // RenderServer draws its root world after the legacy render hooks open each pass). Games may keep
    // using only the legacy hooks: an empty tree costs nothing.

    /// <summary>The engine-owned scene tree; add nodes under <see cref="Root"/> or call <see cref="SceneTree.ChangeScene(Node)"/>.</summary>
    public SceneTree Tree { get; }

    /// <summary>The tree's root viewport (<c>/root</c>).</summary>
    public SceneViewport Root => Tree.Root;

    /// <summary>
    /// Multiplayer for <see cref="Tree"/> (M5): host with <see cref="Networking.MultiplayerApi.Host"/>, join with
    /// <see cref="Networking.MultiplayerApi.Connect"/>. Registered in <see cref="OnLoad"/>; idle until started.
    /// </summary>
    public Networking.MultiplayerApi Multiplayer { get; private set; } = null!;

    /// <summary>Engine servers (the <see cref="RenderServer"/> exists after <see cref="OnLoad"/>).</summary>
    public ServerRegistry Servers => Tree.Servers;

    private EngineOptions EngineOptions { get; }

    /// <summary>Number of frames rendered and presented so far (skipped frames are not counted).</summary>
    public int RenderedFrameCount => _renderedFrames;

    /// <summary>
    /// CPU time of the last rendered frame, in milliseconds: its update (ImGui, <see cref="OnUpdate(in GameTime)"/>,
    /// the scene tree tick) and render (draw-list build, shadow/offscreen/main/overlay command recording, submit),
    /// without the time the renderer was blocked on the GPU or the swapchain
    /// (<see cref="IVulkanContext.LastFrameWaitMilliseconds"/>). Unlike the wall-clock frame time it does not depend on
    /// how fast the GPU is (on a CPU rasterizer such as lavapipe, the GPU wait dominates).
    /// </summary>
    public double LastFrameCpuMilliseconds { get; private set; }

    private long _updateTicks; // Stopwatch ticks of the updates since the last rendered frame

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
    /// Pixels per layout point: <see cref="EngineOptions.ContentScale"/> when fixed, otherwise the display's backing scale
    /// (framebuffer pixels per OS window point, 2 on Retina).
    /// </summary>
    public float ContentScale => WindowPixels.ContentScale(Window, FramebufferSize, EngineOptions.ContentScale);

    /// <summary>
    /// Resizes the window to <paramref name="size"/> points. With a fixed <see cref="EngineOptions.ContentScale"/> these are
    /// layout points and the framebuffer becomes exactly <paramref name="size"/> × that scale pixels on any display (or this
    /// throws); otherwise they are OS window points. The swapchain is rebuilt on the next frame.
    /// </summary>
    public void ResizeWindow(Vector2D<int> size)
    {
        if (size.X <= 0 || size.Y <= 0)
            throw new ArgumentOutOfRangeException(nameof(size), size, "The window size must be positive.");
        if (EngineOptions.ContentScale > 0f)
            EnsureFramebufferSize(WindowPixels.PixelsFor(size, EngineOptions.ContentScale));
        else
            Window.Size = size;
    }

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
        if (!float.IsFinite(engineOptions.ContentScale) || engineOptions.ContentScale < 0f)
            throw new ArgumentOutOfRangeException(nameof(engineOptions), engineOptions.ContentScale,
                $"{nameof(EngineOptions)}.{nameof(EngineOptions.ContentScale)} must be 0 (the display's) or a positive scale.");
        EngineOptions = engineOptions;
        DevOverlayVisible = engineOptions.DevOverlayVisible;
        // M9: catalogs load before any game code runs, so constructors and OnLoad can translate.
        Localization.Tr.Configure(engineOptions.Localization ?? new Localization.LocalizationOptions(), engineOptions.Locale);
        Tree = new SceneTree { PhysicsTicksPerSecond = engineOptions.PhysicsTicksPerSecond }; // M2
        MainframeEngine.Input.Current = Tree.Input; // M10: Input.IsActionPressed(...) reads the engine's tree

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
        Window.Load += PlaceWindow;
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

    // Runs when the window exists, before OnLoad creates the swapchain. Monitors can only be queried once the window
    // exists, and there may be none at all (a sleeping display on macOS, some headless X servers).
    private void PlaceWindow()
    {
        if (EngineOptions.ContentScale <= 0f)
        {
            CenterWindow();
            return;
        }

        var pixels = WindowPixels.PixelsFor(EngineOptions.WindowSize, EngineOptions.ContentScale);
        WindowPixels.ResizeToPixels(Window, pixels); // size for this display first, so centring uses the final size
        CenterWindow();
        EnsureFramebufferSize(pixels); // centring may have moved it to a display of another scale
    }

    private void CenterWindow()
    {
        if (Window.Monitor is { } monitor)
            Window.Center(monitor);
    }

    private void EnsureFramebufferSize(Vector2D<int> pixels)
    {
        var reached = WindowPixels.ResizeToPixels(Window, pixels);
        if (reached != pixels)
            throw new InvalidOperationException(
                $"{nameof(EngineOptions)}.{nameof(EngineOptions.ContentScale)} {EngineOptions.ContentScale} asks for a {pixels.X}x{pixels.Y} px " +
                $"framebuffer, but the window ({Window.Size.X}x{Window.Size.Y} pt) is {reached.X}x{reached.Y} px on this display.");
    }

    protected virtual void OnLoad()
    {
        InputContext = Window.CreateInput();
        Log.Info($"[Window] SDL window {Window.Size.X}x{Window.Size.Y} pt, framebuffer {FramebufferSize.X}x{FramebufferSize.Y} px" +
                 (EngineOptions.ContentScale > 0f ? $" (fixed content scale {EngineOptions.ContentScale})" : ""));

        Renderer = new VulkanRenderer(Window, new VulkanRendererOptions
        {
            EnableValidation = EngineOptions.EnableValidation,
            VSync = EngineOptions.VSync,
            EnableFrameCapture = EngineOptions.EnableFrameCapture,
        });

        if (Renderer is IVulkanContext vkCtx)
            _vkImGuiController = new VulkanImGuiController(vkCtx, InputContext, Window, EngineOptions.ContentScale);

        // Servers are disposed in reverse registration order (after the tree is freed): UI, physics, audio, multiplayer,
        // Steam, then the render server last, so nothing outlives what it depends on (the UI's GPU objects go while the
        // renderer is alive; multiplayer's Steam transport on SteamServer; every server's nodes are gone before any
        // server goes). Frame servers run in this order too.
        // M2: the render server replaces the static Node.Initialize; tree input comes from the window.
        Servers.Register(new RenderServer(Renderer));
        if (EngineOptions.SteamAppId != 0)
            Servers.Register(new SteamServer(EngineOptions.SteamAppId));
        // M5: replication for the engine's tree; idle (no network) until StartServer/StartClient.
        Multiplayer = Networking.MultiplayerApi.Attach(Tree);
        if (EngineOptions.Audio.Enabled)
            RegisterAudioServer(EngineOptions.Audio);
        // M6: physics servers, stepped by the tree's fixed tick; they create a space per world on demand.
        Servers.Register(new PhysicsServer3D(EngineOptions.Physics3D) { DebugDrawEnabled = EngineOptions.DebugCollisionShapes });
        Servers.Register(new PhysicsServer2D(EngineOptions.Physics2D) { DebugDrawEnabled = EngineOptions.DebugCollisionShapes });
        if (EngineOptions.EnableUi)
        {
            // M8: RmlUi game UI — sees input before the tree's nodes, renders after the tonemap below ImGui.
            var uiOptions = EngineOptions.Ui ?? new UiServerOptions();
            if (EngineOptions.ContentScale > 0f)
                uiOptions = uiOptions with { ContentScale = EngineOptions.ContentScale };
            var ui = new UiServer(Renderer, Window, InputContext, uiOptions);
            ui.CanRender = () => !IsMinimised();
            Servers.Register(ui);
        }

        _inputRouter = new InputRouter(InputContext, Tree);
        foreach (var keyboard in InputContext.Keyboards)
            keyboard.KeyDown += OnEngineKeyDown;

        SetWindowIcon(EngineOptions.IconPath);
    }

    // M7: AudioServer.Create already falls back to the null device; this guard only keeps an unexpected failure
    // (e.g. a broken bus layout resource type) from taking the game down — audio is never worth a failed startup.
    private void RegisterAudioServer(in AudioOptions options)
    {
        try
        {
            Servers.Register(AudioServer.Create(options, Tree));
        }
        catch (Exception e) when (e is not OutOfMemoryException)
        {
            Log.Error($"[Audio] Audio is unavailable: {e}");
        }
    }

    private void OnEngineKeyDown(IKeyboard keyboard, Key key, int scancode)
    {
        if (key == DevOverlayKey && key != Key.Unknown)
            DevOverlayVisible = !DevOverlayVisible;
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
        var start = Stopwatch.GetTimestamp();
        _fps.Update();
        _gameTime.DeltaTime = EngineOptions.FixedDeltaTime > 0f ? EngineOptions.FixedDeltaTime : (float)delta;
        _gameTime.FrameCount = _fps.TotalFrameCount;
        _gameTime.FramesPerSecond = _fps.Fps;
        _gameTime.FramesTimeMs = _fps.Ms;

        _vkImGuiController?.Update(_gameTime.DeltaTime);

        if (DevOverlayVisible)
            OnImGui(_gameTime);
        OnUpdate(_gameTime);
        Tree.Tick(_gameTime); // M2: physics steps, process, deferred calls/frees, transform sync
        _updateTicks += Stopwatch.GetTimestamp() - start;
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

        var renderStart = Stopwatch.GetTimestamp();

        // M3: cull and sort the scene's meshes, create/update their GPU resources (uploads join this frame).
        Servers.Render?.PrepareFrame(Root);

        Renderer.BeginFrame();

        // For Vulkan, BeginFrame can return early without starting a frame (swapchain recreation
        // during resize / fullscreen toggle). Skip draw calls in that case.
        var frameStarted = Renderer is not IVulkanContext vk || vk.FrameStarted;
        if (frameStarted)
        {
            // Shadow pass runs before the main render pass (command buffer is open, no render pass active).
            OnShadowPass(_gameTime);
            Servers.Render?.RenderShadows(Root); // M2: the scene tree's shadow casters
            Servers.Render?.RenderOffscreen(Root); // M3: sub-viewports and object-ID picking passes

            // Begin the main render pass, then let the game draw geometry.
            (Renderer as IVulkanContext)?.BeginRenderPass();

            Servers.Render?.RenderMain(Root); // M2: sky, then the scene tree's visuals
            OnRenderMainPass(_gameTime);
            // Tonemaps the scene target, draws the game UI, then ImGui in the overlay pass. With the overlay hidden
            // the frame is discarded and EndFrame runs the tonemap + UI.
            if (DevOverlayVisible)
                _vkImGuiController?.Render();
            else
                _vkImGuiController?.DiscardFrame();
        }
        else
        {
            _vkImGuiController?.DiscardFrame();
        }

        Renderer.EndFrame();

        if (!frameStarted)
            return;

        var cpuTicks = _updateTicks + Stopwatch.GetTimestamp() - renderStart;
        _updateTicks = 0;
        var waitMs = Renderer is IVulkanContext context ? context.LastFrameWaitMilliseconds : 0;
        LastFrameCpuMilliseconds = Math.Max(0, Stopwatch.GetElapsedTime(0, cpuTicks).TotalMilliseconds - waitMs);

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
        if (InputContext is not null)
            foreach (var keyboard in InputContext.Keyboards)
                keyboard.KeyDown -= OnEngineKeyDown;
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

    /// <summary>The code <see cref="Run"/> will return (set by <see cref="Quit"/>; <see cref="ExitCode.Ok"/> otherwise).</summary>
    public ExitCode CurrentExitCode => _exitCode;

    public void Dispose()
    {
        if (ReferenceEquals(MainframeEngine.Input.Current, Tree.Input))
            MainframeEngine.Input.Current = null;
        Window.Dispose();
    }
}
