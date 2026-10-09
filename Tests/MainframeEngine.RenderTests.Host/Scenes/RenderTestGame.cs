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
    private readonly List<double> _frameTimes = [];
    private readonly List<double> _cpuFrameTimes = [];
    private long _lastFrameTimestamp;
    private double _shadowCpuMs, _shadowGpuMs;

    protected RenderTestGame(HostOptions host) : base(CreateOptions(host))
    {
        _host = host;
    }

    private static EngineOptions CreateOptions(HostOptions host) => new()
    {
        GameName = $"Render test: {host.Scene}",
        WindowSize = new Vector2D<int>(host.Width, host.Height),
        // Captures are Width×Height × Scale pixels on any display (1× monitor, 2× Retina), so goldens do not depend on it.
        ContentScale = host.Scale,
        WindowVisible = !host.Hidden,
        EnableValidation = !host.NoValidation,
        EnableFrameCapture = true,
        VSync = false,
        FixedDeltaTime = 1f / 60f,
        MaxFrames = host.MaxFrames,
        // The silent null device: CI machines have no audio device, and render tests should not make noise.
        Audio = new AudioOptions { Device = AudioDeviceMode.Null, BusLayoutPath = null },
        DevOverlayVisible = false, // scenes opt in (showcase, shadow-lights --count 2)
        Locale = "en", // never the machine's language: captures must not depend on it
    };

    protected IVulkanContext Vulkan => (IVulkanContext)Renderer;

    /// <summary>The host options (scene parameters such as <see cref="HostOptions.Count"/>).</summary>
    protected HostOptions Host => _host;

    /// <summary>Aspect of the image actually being rendered (the swapchain extent, in pixels).</summary>
    protected float AspectRatio
    {
        get
        {
            var extent = Vulkan.SwapchainExtent;
            return (float)extent.Width / Math.Max(1u, extent.Height);
        }
    }

    protected override void OnLoad()
    {
        base.OnLoad();
        Renderer.SetClearColor(0.1f, 0.1f, 0.1f);
        if (Servers.Render is { } render)
        {
            render.ForceDepthPrepass |= _host.Prepass;
            render.ForceProjectionJitter = _host.Jitter;
            if (_host.VelocityView)
                render.DebugView = RenderDebugView.Velocity;
        }

        if (_host.AntiAliasing is { } antiAliasing)
            Vulkan.AntiAliasing = antiAliasing;
        if (_host.TaaSharpness is { } sharpness)
            Vulkan.TaaSharpness = sharpness;

        if (_host.UpdateRate > 0)
        {
            // Renders run unthrottled, updates at UpdateRate: the UI renderer sees renders without a new update.
            Window.FramesPerSecond = 0;
            Window.UpdatesPerSecond = _host.UpdateRate;
        }

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

        // Frame time: wall clock between consecutive updates (one per rendered frame, VSync off).
        if (_host.PerfMeasuredFrames > 0)
        {
            var now = System.Diagnostics.Stopwatch.GetTimestamp();
            if (frame > (uint)_host.PerfWarmupFrames + 1 && _frameTimes.Count < _host.PerfMeasuredFrames)
            {
                _frameTimes.Add(System.Diagnostics.Stopwatch.GetElapsedTime(_lastFrameTimestamp, now).TotalMilliseconds);
                _cpuFrameTimes.Add(LastFrameCpuMilliseconds); // the previous frame's update + render, GPU waits excluded
                if (Servers.Render?.ExistingShadows is { } shadows)
                {
                    _shadowCpuMs += shadows.LastCpuMilliseconds;
                    _shadowGpuMs += shadows.LastGpuMilliseconds;
                }
            }
            _lastFrameTimestamp = now;
        }

        if (Array.BinarySearch(_host.CaptureFrames, frame) >= 0)
        {
            _pendingCapture = frame;
            CaptureFrame();
        }

        if (_host.UiHiddenUntilFrame > 0 && Ui?.Renderer is { } ui)
            ui.Visible = frame > _host.UiHiddenUntilFrame;

        // Swapchain-recreation and shutdown hooks (frame numbers from the command line).
        if (frame == _host.ResizeAtFrame)
            ResizeWindow(new Vector2D<int>(_host.ResizeTo.Width, _host.ResizeTo.Height));
        if (frame == _host.ToggleVSyncAtFrame)
            Renderer.VSync = !Renderer.VSync;
        if (frame == _host.QuitWithErrorAtFrame)
            Quit(ExitCode.Error);
        RunInputScript(frame);
        RunMinimizeScript(frame);

        UpdateScene(gameTime);

        if (_host.NoShadows && frame <= 2)
        {
            var lights = Root.World3D.Lights;
            foreach (var light in lights.DirectionalLights) light.CastsShadows = false;
            foreach (var light in lights.SpotLights) light.CastsShadows = false;
            foreach (var light in lights.PointLights) light.CastsShadows = false;
        }
    }

    /// <summary>Frames covered by the scripted right-drag (<c>--input</c>): button down, motion, button up.</summary>
    public const uint InputFrames = 22;

    private void RunInputScript(uint frame)
    {
        var start = _host.InputAtFrame;
        if (start == 0 || frame < start || frame >= start + InputFrames)
            return;
        var step = (int)(frame - start);
        var x = Window.Size.X / 2 + step * 8;
        var y = Window.Size.Y / 2;
        if (step == 0)
            QaSdl.PushMouse(Window, Silk.NET.SDL.EventType.Mousebuttondown, x, y, 0, 0);
        else if (step == (int)InputFrames - 1)
            QaSdl.PushMouse(Window, Silk.NET.SDL.EventType.Mousebuttonup, x, y, 0, 0);
        else if (step != (int)InputFrames - 2) // one quiet frame before the release
            QaSdl.PushMouse(Window, Silk.NET.SDL.EventType.Mousemotion, x, y, 8, 0);
    }

    // --minimize: minimise, count the updates and frames while minimised, restore after MinimizedSeconds.
    private const double MinimizedSeconds = 1.5;
    private long _minimizedAt;
    private bool _minimizeDone;
    private int _renderedAtSettle; // frames rendered once the minimise has taken effect
    private System.Threading.Timer? _wakeTimer;

    private void RunMinimizeScript(uint frame)
    {
        if (_host.MinimizeAtFrame == 0)
            return;

        if (frame == _host.MinimizeAtFrame && _minimizedAt == 0 && !_minimizeDone)
        {
            Window.WindowState = Silk.NET.Windowing.WindowState.Minimized;
            _minimizedAt = System.Diagnostics.Stopwatch.GetTimestamp();
            _renderedAtSettle = -1;
            QaSdl.WakeEnabled = true;
            var windowId = QaSdl.WindowId(Window);
            _wakeTimer = new System.Threading.Timer(_ => QaSdl.WakeEventLoop(windowId), null, 100, 100);
            return;
        }

        if (_minimizedAt == 0)
            return;
        var elapsed = System.Diagnostics.Stopwatch.GetElapsedTime(_minimizedAt).TotalSeconds;
        if (_renderedAtSettle < 0 && elapsed >= 0.5)
        {
            // The minimise (a frame or two in flight) has taken effect by now — unless the window system has no minimise
            // (a bare Xvfb without a window manager), where there is nothing to check and the run only proves it stays clean.
            _renderedAtSettle = Window.WindowState == Silk.NET.Windowing.WindowState.Minimized ? RenderedFrameCount : int.MaxValue;
        }
        if (elapsed >= MinimizedSeconds)
        {
            // While minimised the engine renders nothing (it blocks on events); growth since then means it spun.
            var rendered = _renderedAtSettle == int.MaxValue ? 0 : RenderedFrameCount - _renderedAtSettle;
            if (rendered > 0)
                Fail($"{rendered} frames were rendered while the window was minimised; expected none.");
            StopWakeTimer();
            _minimizedAt = 0;
            _minimizeDone = true;
            Window.WindowState = Silk.NET.Windowing.WindowState.Normal;
        }
    }

    // Stops the wake timer and waits for an in-flight callback, so no SDL call races SDL shutdown.
    private void StopWakeTimer()
    {
        QaSdl.WakeEnabled = false;
        if (_wakeTimer is null)
            return;
        using (var done = new ManualResetEvent(false))
        {
            if (_wakeTimer.Dispose(done))
                done.WaitOne();
        }

        _wakeTimer = null;
    }

    protected override void OnFrameCaptured(FrameCapture capture)
    {
        var path = Path.Combine(_host.OutputDirectory, $"{_host.Scene}_frame{_pendingCapture:D4}.png");
        capture.SavePng(path);
        _captures.Add(new HostResult.CaptureInfo(_pendingCapture, path, capture.Width, capture.Height));

        // With --update-rate the render count per update is unbounded: stop once every capture is taken.
        if (_host.UpdateRate > 0 && _captures.Count == _host.CaptureFrames.Length)
            Quit(ExitCode.Ok);
    }

    /// <summary>Set once the engine has shut down; includes validation messages from teardown.</summary>
    public HostResult? Result { get; private set; }

    private readonly List<string> _checkFailures = [];

    /// <summary>Records a failed scene self-check; reported in <see cref="HostResult.SceneCheckFailures"/>.</summary>
    protected void Fail(string message) => _checkFailures.Add(message);

    protected override void OnClose()
    {
        var (deviceName, driver, tag, deviceType) = DescribeDevice();
        var validation = Vulkan.Validation;
        var gpu = Vulkan.Allocator.Totals;
        var pipelineCacheBytes = Vulkan.Pipelines.LoadedBytes;
        var pipelineCacheInBackground = Vulkan.Pipelines.LoadsInBackground;
        var shaderModules = Vulkan.Shaders.Count;
        Vulkan.Vk.GetPhysicalDeviceProperties(Vulkan.PhysicalDevice, out var props);
        var meshStats = Servers.Render?.MeshStats ?? default;
        var meshPipelines = Servers.Render?.PipelineStates?.Count ?? 0;
        var sortedTimes = _frameTimes.Order().ToArray();
        var sortedCpuTimes = _cpuFrameTimes.Order().ToArray();

        StopWakeTimer();
        DisposeScene();
        base.OnClose(); // destroys the device: leaks and in-use destruction are reported here

        Result = new HostResult
        {
            Scene = _host.Scene,
            DeviceName = deviceName,
            DeviceType = deviceType,
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
            ContentScale = _host.Scale,
            SceneCheckFailures = _checkFailures,
            PipelineCacheLoadedBytes = pipelineCacheBytes,
            PipelineCacheLoadsInBackground = pipelineCacheInBackground,
            GpuDeviceMemoryCount = gpu.DeviceMemoryCount,
            GpuAllocationCount = gpu.AllocationCount,
            GpuReservedBytes = (long)gpu.ReservedBytes,
            ShaderModuleCount = shaderModules,
            MaxMemoryAllocationCount = props.Limits.MaxMemoryAllocationCount,
            AverageFrameMs = sortedTimes.Length > 0 ? sortedTimes.Average() : 0,
            P95FrameMs = P95(sortedTimes),
            AverageCpuFrameMs = sortedCpuTimes.Length > 0 ? sortedCpuTimes.Average() : 0,
            P95CpuFrameMs = P95(sortedCpuTimes),
            PerfMeasuredFrames = sortedTimes.Length,
            Configuration = BuildConfiguration,
            MeshInstances = meshStats.Instances,
            MeshDrawCalls = meshStats.DrawCalls,
            MeshShadowDrawCalls = meshStats.ShadowDrawCalls,
            ShadowCpuMs = sortedTimes.Length > 0 ? _shadowCpuMs / sortedTimes.Length : 0,
            ShadowGpuMs = sortedTimes.Length > 0 ? _shadowGpuMs / sortedTimes.Length : 0,
            MeshPipelines = meshPipelines,
        };
    }

    protected abstract void DisposeScene();

    private static double P95(double[] sorted) =>
        sorted.Length > 0 ? sorted[(int)Math.Min(sorted.Length - 1, Math.Ceiling(sorted.Length * 0.95) - 1)] : 0;

    private const string BuildConfiguration =
#if DEBUG
        "Debug";
#else
        "Release";
#endif

    private (string Name, string Driver, string Tag, string DeviceType) DescribeDevice() => DescribeDevice(Vulkan);

    /// <summary>The device's name, driver, golden folder tag (<see cref="HostResult.PlatformTag"/>) and type.</summary>
    internal static unsafe (string Name, string Driver, string Tag, string DeviceType) DescribeDevice(IVulkanContext vulkan)
    {
        var driverProps = new PhysicalDeviceDriverProperties { SType = StructureType.PhysicalDeviceDriverProperties };
        var props = new PhysicalDeviceProperties2 { SType = StructureType.PhysicalDeviceProperties2, PNext = &driverProps };
        vulkan.Vk.GetPhysicalDeviceProperties2(vulkan.PhysicalDevice, &props);

        var name = SilkMarshal.PtrToString((nint)props.Properties.DeviceName) ?? "unknown";
        // Silk's enum has DriverIDXxx aliases; normalise so ToString is stable.
        var driver = driverProps.DriverID.ToString().Replace("DriverID", "", StringComparison.Ordinal);
        var tag = driverProps.DriverID switch
        {
            DriverId.Moltenvk => "moltenvk",
            DriverId.MesaLlvmpipe => "lavapipe",
            _ => driver.ToLowerInvariant(),
        };
        return (name, driver, tag, props.Properties.DeviceType.ToString());
    }
}
