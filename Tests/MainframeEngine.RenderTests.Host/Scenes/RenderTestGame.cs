using Silk.NET.Core.Native;
using Silk.NET.Maths;
using Silk.NET.Vulkan;

namespace MainframeEngine.RenderTests.Host.Scenes;

/// <summary>
/// Base for render-test scenes: deterministic options (fixed 60 Hz step, validation on, capture
/// enabled, VSync off), frame captures written as PNGs, and an optional allocation measurement over a
/// window of steady-state frames.
/// </summary>
public abstract class RenderTestGame : Engine
{
    private readonly HostOptions _host;
    private readonly List<HostResult.CaptureInfo> _captures = [];
    private uint _pendingCapture;
    private long _allocationStart;
    private long? _allocatedBytes;

    protected RenderTestGame(HostOptions host) : base(CreateOptions(host))
    {
        _host = host;
    }

    private static EngineOptions CreateOptions(HostOptions host) => new()
    {
        GameName = $"Render test: {host.Scene}",
        WindowSize = new Vector2D<int>(host.Width, host.Height),
        WindowVisible = !host.Hidden,
        EnableValidation = true,
        EnableFrameCapture = true,
        VSync = false,
        FixedDeltaTime = 1f / 60f,
        MaxFrames = host.MaxFrames,
    };

    protected IVulkanContext Vulkan => (IVulkanContext)Renderer;

    protected float AspectRatio
    {
        get
        {
            var size = FramebufferSize;
            return (float)size.X / Math.Max(1, size.Y);
        }
    }

    protected override void OnLoad()
    {
        base.OnLoad();
        Renderer.SetClearColor(0.1f, 0.1f, 0.1f);
        LoadScene();
    }

    protected abstract void LoadScene();
    protected abstract void UpdateScene(in GameTime gameTime);

    protected sealed override void OnUpdate(in GameTime gameTime)
    {
        var frame = gameTime.FrameCount;

        // Measure around whole frames: [warmup + 1, warmup + 1 + count) on this (the main) thread.
        if (_host.AllocationMeasuredFrames > 0)
        {
            if (frame == (uint)_host.AllocationWarmupFrames + 1)
                _allocationStart = GC.GetAllocatedBytesForCurrentThread();
            else if (frame == (uint)(_host.AllocationWarmupFrames + _host.AllocationMeasuredFrames + 1))
                _allocatedBytes = GC.GetAllocatedBytesForCurrentThread() - _allocationStart;
        }

        if (Array.BinarySearch(_host.CaptureFrames, frame) >= 0)
        {
            _pendingCapture = frame;
            CaptureFrame();
        }

        // Swapchain-recreation and shutdown hooks (frame numbers from the command line).
        if (frame == _host.ResizeAtFrame)
            Window.Size = new Vector2D<int>(_host.ResizeTo.Width, _host.ResizeTo.Height);
        if (frame == _host.ToggleVSyncAtFrame)
            Renderer.VSync = !Renderer.VSync;
        if (frame == _host.QuitWithErrorAtFrame)
            Quit(ExitCode.Error);

        UpdateScene(gameTime);
    }

    protected override void OnFrameCaptured(FrameCapture capture)
    {
        var path = Path.Combine(_host.OutputDirectory, $"{_host.Scene}_frame{_pendingCapture:D4}.png");
        capture.SavePng(path);
        _captures.Add(new HostResult.CaptureInfo(_pendingCapture, path, capture.Width, capture.Height));
    }

    /// <summary>Set once the engine has shut down; includes validation messages from teardown.</summary>
    public HostResult? Result { get; private set; }

    private readonly List<string> _checkFailures = [];

    /// <summary>Records a failed scene self-check; reported in <see cref="HostResult.SceneCheckFailures"/>.</summary>
    protected void Fail(string message) => _checkFailures.Add(message);

    protected override void OnClose()
    {
        var (deviceName, driver, tag) = DescribeDevice();
        var validation = Vulkan.Validation;

        DisposeScene();
        base.OnClose(); // destroys the device: leaks and in-use destruction are reported here

        Result = new HostResult
        {
            Scene = _host.Scene,
            DeviceName = deviceName,
            Driver = driver,
            PlatformTag = tag,
            ValidationEnabled = validation.IsEnabled,
            ValidationWarnings = validation.WarningCount,
            ValidationErrors = validation.ErrorCount,
            ValidationMessages = validation.Messages,
            RenderedFrames = RenderedFrameCount,
            AllocatedBytes = _allocatedBytes,
            MeasuredFrames = _allocatedBytes is null ? 0 : _host.AllocationMeasuredFrames,
            Captures = _captures,
            SceneCheckFailures = _checkFailures,
        };
    }

    protected abstract void DisposeScene();

    private unsafe (string Name, string Driver, string Tag) DescribeDevice()
    {
        var driverProps = new PhysicalDeviceDriverProperties { SType = StructureType.PhysicalDeviceDriverProperties };
        var props = new PhysicalDeviceProperties2 { SType = StructureType.PhysicalDeviceProperties2, PNext = &driverProps };
        Vulkan.Vk.GetPhysicalDeviceProperties2(Vulkan.PhysicalDevice, &props);

        var name = SilkMarshal.PtrToString((nint)props.Properties.DeviceName) ?? "unknown";
        // Silk's enum has DriverIDXxx aliases; normalise so ToString is stable.
        var driver = driverProps.DriverID.ToString().Replace("DriverID", "", StringComparison.Ordinal);
        var tag = driverProps.DriverID switch
        {
            DriverId.Moltenvk => "moltenvk",
            DriverId.MesaLlvmpipe => "lavapipe",
            _ => driver.ToLowerInvariant(),
        };
        return (name, driver, tag);
    }
}
