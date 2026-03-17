using Bliss.CSharp;
using Bliss.CSharp.Colors;
using Bliss.CSharp.Graphics.Rendering.Renderers;
using Bliss.CSharp.Images;
using Bliss.CSharp.Interact;
using Bliss.CSharp.Interact.Contexts;
using Bliss.CSharp.Logging;
using Bliss.CSharp.Textures;
using Bliss.CSharp.Transformations;
using Bliss.CSharp.Windowing;
using MiniAudioEx.Core.StandardAPI;
using Veldrid;

namespace BlissExamples;

public class Game : IDisposable
{
    private double _fixedFrameRate;
    private readonly double _fixedUpdateTimeStep;
    private double _fixedUpdateTimer;

    public GameSettings Settings { get; }
    public IWindow MainWindow { get; }
    public GraphicsDevice GraphicsDevice { get; }
    public CommandList CommandList { get; private set; }
    public FullScreenRenderer FullScreenRenderer { get; private set; }
    public RenderTexture2D FullScreenTexture { get; private set; }
    public Texture2D FullScreenResolvedTexture { get; private set; }

    public Game(GameSettings settings)
    {
        Logger.Info("Hello World! Bliss start...");
        Logger.Info($"\t> CPU: {SystemInfo.Cpu}");
        Logger.Info($"\t> MEMORY: {SystemInfo.MemorySize} GB");
        Logger.Info($"\t> THREADS: {SystemInfo.Threads}");
        Logger.Info($"\t> OS: {SystemInfo.Os}");

        Logger.Info("Initialize window and graphics device...");

        Settings = settings;
        _fixedUpdateTimeStep = settings.FixedTimeStep;

        var options = new GraphicsDeviceOptions
        {
            Debug = false,
            HasMainSwapchain = true,
            SwapchainDepthFormat = PixelFormat.D32FloatS8UInt,
            SyncToVerticalBlank = settings.VSync,
            ResourceBindingModel = ResourceBindingModel.Improved,
            PreferDepthRangeZeroToOne = true,
            PreferStandardClipSpaceYDirection = true,
            SwapchainSrgbFormat = false
        };

        MainWindow = Window.CreateWindow(WindowType.Sdl3, Settings.Size.Width, Settings.Size.Height,
            Settings.Title, Settings.WindowFlags, options, Settings.Backend,
            out GraphicsDevice graphicsDevice);
        MainWindow.SetMinimumSize(Settings.MinSize.Width, Settings.MinSize.Height);
        MainWindow.Resized += () => OnResize(new Rectangle(MainWindow.GetX(), MainWindow.GetY(),
            MainWindow.GetWidth(), MainWindow.GetHeight()));
        GraphicsDevice = graphicsDevice;

        Logger.Info("Initialize time...");
        Time.Init();

        Logger.Info($"Set target FPS to: {Settings.TargetFps}");
        SetTargetFps(Settings.TargetFps);

        Logger.Info("Initialize command list...");
        CommandList = graphicsDevice.ResourceFactory.CreateCommandList();

        Logger.Info("Initialize global resources...");
        GlobalResource.Init(graphicsDevice);

        Logger.Info("Initialize input...");
        if (MainWindow is Sdl3Window)
            Input.Init(new Sdl3InputContext(MainWindow));
        else
            throw new Exception("This type of window is not supported by the InputContext!");
        
        Logger.Info("Initialize audio device...");
        AudioContext.Initialize(44100, 2);
        
        // Init
        FullScreenRenderer = new FullScreenRenderer(GraphicsDevice);
        FullScreenTexture = new RenderTexture2D(GraphicsDevice, (uint) MainWindow.GetWidth(), (uint) MainWindow.GetHeight(), false, Settings.SampleCount);
        FullScreenResolvedTexture = new Texture2D(GraphicsDevice, new Image(MainWindow.GetWidth(), MainWindow.GetHeight()), false);
    }

    protected virtual void OnResize(Rectangle rectangle)
    {
        // Resize main swapchain.
        GraphicsDevice.MainSwapchain.Resize((uint)rectangle.Width, (uint)rectangle.Height);
    }

    public void Dispose()
    {
    }

    public void SetTargetFps(int fps)
    {
        _fixedFrameRate = 1.0F / fps;
    }

    public int GetTargetFps()
    {
        return (int)(1.0F / _fixedFrameRate);
    }

    public int Run()
    {
        Logger.Info("Start main loops...");
        while (MainWindow.Exists)
        {
            if (GetTargetFps() != 0 && Time.Timer.Elapsed.TotalSeconds <= _fixedFrameRate)
                continue;

            Time.Update();

            MainWindow.PumpEvents();
            Input.Begin();

            AudioContext.Update();
            Update();
            AfterUpdate();

            _fixedUpdateTimer += Time.Delta;
            while (_fixedUpdateTimer >= _fixedUpdateTimeStep)
            {
                FixedUpdate();
                _fixedUpdateTimer -= _fixedUpdateTimeStep;
            }

            Draw(GraphicsDevice, CommandList);
            Input.End();
        }

        return 0;
    }

    private void Draw(in GraphicsDevice graphicsDevice, in CommandList commandList)
    {
        commandList.Begin();
        commandList.SetFramebuffer(FullScreenTexture.Framebuffer);
        commandList.ClearColorTarget(0, Color.DarkGray.ToRgbaFloat());
        commandList.ClearDepthStencil(1.0F);
        {
            // Enables relative mouse mod.
            Input.EnableRelativeMouseMode();
        }
        
        commandList.End();
        
        // Draw ScreenPass.
        commandList.Begin();
        {
            // Resolve texture.
            if (FullScreenTexture.SampleCount != TextureSampleCount.Count1)
                commandList.ResolveTexture(FullScreenTexture.ColorTexture, FullScreenResolvedTexture.DeviceTexture);
            else
                commandList.CopyTexture(FullScreenTexture.ColorTexture, FullScreenResolvedTexture.DeviceTexture);
            
            commandList.SetFramebuffer(graphicsDevice.SwapchainFramebuffer);
            commandList.ClearColorTarget(0, Color.DarkGray.ToRgbaFloat());
            
            FullScreenRenderer.Draw(commandList, FullScreenResolvedTexture, GraphicsDevice.SwapchainFramebuffer.OutputDescription);
        }
        commandList.End();
        
        graphicsDevice.SubmitCommands(commandList);
        graphicsDevice.SwapBuffers();
    }

    private void FixedUpdate()
    {
    }

    private void Update()
    {
    }

    private void AfterUpdate()
    {
    }
}