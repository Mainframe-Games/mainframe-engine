using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Silk.NET.Core;
using Silk.NET.Core.Native;
using Silk.NET.Maths;
using Silk.NET.Vulkan;
using Silk.NET.Vulkan.Extensions.EXT;
using Silk.NET.Vulkan.Extensions.KHR;
using Silk.NET.Windowing;
using VkBuffer = Silk.NET.Vulkan.Buffer;

namespace MainframeEngine;

/// <summary>
/// Vulkan rendering backend.
/// Implements IVulkanContext so shapes can record draw commands into the active command buffer.
/// </summary>
internal sealed unsafe partial class VulkanRenderer : IRenderer, IVulkanContext
{
    private readonly IWindow _window;

    private Vk? _vk;
    private Instance _instance;
    private PhysicalDevice _physicalDevice;
    private Device _device;
    private Queue _graphicsQueue;
    private Queue _presentQueue;
    private KhrSurface? _khrSurface;
    private SurfaceKHR _surface;

    // swapchain
    private KhrSwapchain? _khrSwapChain;
    private SwapchainKHR _swapChain;
    private Image[]? _swapChainImages;
    private Format _swapChainImageFormat;
    private Extent2D _swapChainExtent;

    // Passes, views, framebuffers and the HDR scene target: VulkanRenderer.Presentation.cs

    // GPU memory, uploads and deferred destruction (created right after the device)
    private GpuAllocator? _allocator;
    private DeletionQueue? _deletions;
    private UploadQueue? _uploads;
    private PipelineCache? _pipelineCache;
    private ShaderModuleCache? _shaderModules;
    private FrameContext? _frameContext;
    private float _exposure = IVulkanContext.DefaultExposure;
    private float _maxSamplerAnisotropy = 1f; // 1 = anisotropic filtering unavailable
    private ulong _frameNumber;                                      // frames that started recording (1-based)
    private readonly ulong[] _slotFrameNumber = new ulong[MaxFramesInFlight]; // last frame recorded in each slot

    // commands: one primary command buffer per frame slot (allocated once, reset each frame)
    private CommandPool _commandPool;
    private readonly CommandBuffer[] _commandBuffers = new CommandBuffer[MaxFramesInFlight];

    // sync: acquire semaphores and fences per frame slot; render-finished semaphores per swapchain
    // image (a present may still be waiting on one until that image is acquired again), recreated
    // whenever the image count changes.
    private const int MaxFramesInFlight = IVulkanContext.MaxFramesInFlight;
    private readonly Silk.NET.Vulkan.Semaphore[] _imageAvailableSemaphores = new Silk.NET.Vulkan.Semaphore[MaxFramesInFlight];
    private readonly Fence[] _inFlightFences = new Fence[MaxFramesInFlight];
    private Silk.NET.Vulkan.Semaphore[] _renderFinishedSemaphores = [];
    private int _currentFrame;
    private uint _currentImageIndex;
    private bool _frameStarted;
    private long _frameWaitTicks; // Stopwatch ticks blocked on fences, acquire and present since BeginFrame
    private bool _framebufferResized;
    private bool _vsync;

    // clear color
    private float _clearR, _clearG, _clearB, _clearA = 1f;

    // validation
    private bool _enableValidationLayers;
    private readonly string[] _validationLayers = ["VK_LAYER_KHRONOS_validation"];
    private ExtDebugUtils? _debugUtils;
    private DebugUtilsMessengerEXT _debugMessenger;
    private readonly VulkanValidationLog _validation = new();
    private GCHandle _validationHandle; // user data for the static debug callback

    // frame capture (opt-in: swapchain needs TRANSFER_SRC usage)
    private readonly bool _enableFrameCapture;
    private bool _captureSupported;
    private bool _captureRequested;
    private bool _captureRecorded;
    private FrameCapture? _completedCapture;
    private GpuBuffer? _captureBuffer;

    private readonly string[] _deviceExtensions = [KhrSwapchain.ExtensionName];

    // "No shadows" descriptor set (set 2) for lit pipelines when the game has no ShadowSystem.
    private ShadowFallback? _shadowFallback;
    private bool _disposed;

    /// <summary>
    /// The set-2 stand-in used by lit pipelines when there is no <see cref="ShadowSystem"/>: same layout,
    /// 1×1 maps cleared to far depth and matrices that put every fragment outside the shadow frustum.
    /// Created on first use, destroyed with the device.
    /// </summary>
    internal ShadowFallback ShadowFallback => _shadowFallback ??= new ShadowFallback(this);

    #region IRenderer

    public RenderingBackend Backend => RenderingBackend.Vulkan;

    public bool VSync
    {
        get => _vsync;
        set
        {
            if (_vsync == value) return;
            _vsync = value;
            _framebufferResized = true;
        }
    }

    #endregion

    #region IVulkanContext

    public Vk Vk => _vk!;
    public Device Device => _device;
    public PhysicalDevice PhysicalDevice => _physicalDevice;
    public RenderPass RenderPass => SceneTarget.RenderPass;
    public CommandPool CommandPool => _commandPool;
    public Queue GraphicsQueue => _graphicsQueue;
    public bool FrameStarted => _frameStarted;
    public double LastFrameWaitMilliseconds => Stopwatch.GetElapsedTime(0, _frameWaitTicks).TotalMilliseconds;
    public int FrameSlot => _currentFrame;
    public CommandBuffer CurrentCommandBuffer => _frameStarted ? _commandBuffers[_currentFrame] : default;
    public Extent2D SwapchainExtent => _swapChainExtent;
    public uint SwapchainImageCount => (uint)(_swapChainImages?.Length ?? 0);
    public uint CurrentImageIndex => _currentImageIndex;
    public Silk.NET.Vulkan.Framebuffer CurrentFramebuffer => SceneTarget.Framebuffer;
    public VulkanValidationLog Validation => _validation;
    public GpuAllocator Allocator => _allocator ?? throw new InvalidOperationException("The device is not initialised.");
    public UploadQueue Uploads => _uploads ?? throw new InvalidOperationException("The device is not initialised.");
    public DeletionQueue Deletions => _deletions ?? throw new InvalidOperationException("The device is not initialised.");
    public PipelineCache Pipelines => _pipelineCache ?? throw new InvalidOperationException("The device is not initialised.");
    public ShaderModuleCache Shaders => _shaderModules ?? throw new InvalidOperationException("The device is not initialised.");
    public FrameContext Frame => _frameContext ??= new FrameContext(this);
    public ulong FrameNumber => _frameNumber;
    public float MaxSamplerAnisotropy => _maxSamplerAnisotropy;

    public PostProcessSettings PostProcess { get; set; } = PostProcessSettings.Default;

    public LightShaftsSun LightShaftsSun { get; set; }

    public AntiAliasing AntiAliasing
    {
        get => _antiAliasing;
        set => _antiAliasing = Enum.IsDefined(value) ? value : throw new ArgumentOutOfRangeException(nameof(value), value, "Unknown anti-aliasing mode.");
    }

    public float TaaSharpness
    {
        get => _taaSharpness;
        set => _taaSharpness = float.IsFinite(value) ? Math.Clamp(value, 0f, 1f) : throw new ArgumentOutOfRangeException(nameof(value), value, "TAA sharpness must be finite.");
    }

    public Scaling3DMode Scaling3DMode
    {
        get => _scaling3DMode;
        set => _scaling3DMode = Enum.IsDefined(value) ? value : throw new ArgumentOutOfRangeException(nameof(value), value, "Unknown scaling mode.");
    }

    public float Scaling3DScale
    {
        get => _scaling3DScale;
        set
        {
            if (!float.IsFinite(value))
                throw new ArgumentOutOfRangeException(nameof(value), value, "The 3D scale must be finite.");
            var scale = RenderScaling.ClampScale(value);
            if (scale == _scaling3DScale)
                return;
            _scaling3DScale = scale;
            _renderSizeDirty = true; // applied at the start of the next frame (device idle)
        }
    }

    public float FsrSharpness
    {
        get => _fsrSharpness;
        set => _fsrSharpness = float.IsFinite(value)
            ? Math.Clamp(value, 0f, RenderScaling.MaxFsrSharpness)
            : throw new ArgumentOutOfRangeException(nameof(value), value, "FSR sharpness must be finite.");
    }

    public Extent2D RenderExtent => _renderExtent;

    public float FrameDeltaTime { get; set; }

    public float Exposure
    {
        get => _exposure;
        set => _exposure = float.IsFinite(value) && value > 0f ? value : throw new ArgumentOutOfRangeException(nameof(value), "Exposure must be positive.");
    }

    #endregion

    public VulkanRenderer(IWindow window, in VulkanRendererOptions options)
    {
        _window = window;
        _enableValidationLayers = options.EnableValidation;
        _vsync = options.VSync;
        _enableFrameCapture = options.EnableFrameCapture;
        AntiAliasing = options.AntiAliasing;
        TaaSharpness = options.TaaSharpness;
        _glow = new GlowEffect(_autoExposure);
        RegisterPostEffects();
        InitVulkan();
    }

    #region IRenderer implementation

    public void OnResize(Vector2D<int> newSize) => _framebufferResized = true;

    public void BeginFrame()
    {
        _frameWaitTicks = 0;

        // A pending resize/VSync change, or a minimised window (0×0 drawable): no frame until the
        // swapchain can be rebuilt with a real extent.
        if (_framebufferResized && !RecreateSwapchain())
            return;

        // ADR 0174: a new render scale resizes the render-resolution targets (the device idle, like a resize).
        if (_renderSizeDirty)
            ApplyRenderScale();

        // The slot's previous submission must be done before its command buffer and per-frame
        // resources (UBOs, vertex buffers keyed by FrameSlot) are reused.
        var waitStart = Stopwatch.GetTimestamp();
        _vk!.WaitForFences(_device, 1, in _inFlightFences[_currentFrame], true, ulong.MaxValue)
            .Check("vkWaitForFences (frame slot)");
        _frameWaitTicks += Stopwatch.GetTimestamp() - waitStart;

        // Frames finish in submission order: everything up to this slot's last frame is done.
        var completed = _slotFrameNumber[_currentFrame];
        _deletions!.Collect(completed);
        _uploads!.Release(completed);

        uint imageIndex;
        var acquireStart = Stopwatch.GetTimestamp();
        var result = _khrSwapChain!.AcquireNextImage(_device, _swapChain, ulong.MaxValue,
            _imageAvailableSemaphores[_currentFrame], default, &imageIndex);
        _frameWaitTicks += Stopwatch.GetTimestamp() - acquireStart;

        if (result == Result.ErrorOutOfDateKhr)
        {
            // The semaphore was not signalled; the fence stays signalled (it is only reset before
            // a submit), so the next BeginFrame does not deadlock.
            _framebufferResized = true;
            RecreateSwapchain();
            return;
        }

        if (result != Result.Success && result != Result.SuboptimalKhr)
            throw new VulkanException($"[Vulkan] Failed to acquire a swapchain image: {result}");

        _currentImageIndex = imageIndex;

        var cb = _commandBuffers[_currentFrame];
        _vk.ResetCommandBuffer(cb, 0).Check("vkResetCommandBuffer");

        var beginInfo = new CommandBufferBeginInfo
        {
            SType = StructureType.CommandBufferBeginInfo,
            Flags = CommandBufferUsageFlags.OneTimeSubmitBit,
        };
        _vk.BeginCommandBuffer(cb, in beginInfo).Check("vkBeginCommandBuffer");
        _frameStarted = true;

        _frameNumber++;
        _slotFrameNumber[_currentFrame] = _frameNumber;
        _deletions.BeginFrame(_frameNumber);

        // Pending staging copies and layout initialisations run first, before any pass reads them.
        _uploads.Record(cb);
    }

    public void EndFrame()
    {
        if (!_frameStarted) return;

        var imageIndex = _currentImageIndex;
        var cb = _commandBuffers[_currentFrame];

        // Finish the pass sequence (scene → tonemap → overlay) and close the command buffer
        EndPasses(cb);
        _frameStarted = false;
        if (_captureRequested)
            RecordCapture(cb, imageIndex);
        _vk!.EndCommandBuffer(cb).Check("vkEndCommandBuffer");

        var waitSemaphore = _imageAvailableSemaphores[_currentFrame];
        var waitStage = PipelineStageFlags.ColorAttachmentOutputBit;
        var signalSemaphore = _renderFinishedSemaphores[imageIndex];
        var commandBuffer = cb;

        var submitInfo = new SubmitInfo
        {
            SType = StructureType.SubmitInfo,
            WaitSemaphoreCount = 1,
            PWaitSemaphores = &waitSemaphore,
            PWaitDstStageMask = &waitStage,
            CommandBufferCount = 1,
            PCommandBuffers = &commandBuffer,
            SignalSemaphoreCount = 1,
            PSignalSemaphores = &signalSemaphore,
        };

        var frameFence = _inFlightFences[_currentFrame];
        _vk.ResetFences(_device, 1, in frameFence).Check("vkResetFences");
        _vk.QueueSubmit(_graphicsQueue, 1, in submitInfo, frameFence).Check("vkQueueSubmit (frame)");
        _deletions!.EndFrame();

        var swapChainHandle = _swapChain;
        var presentInfo = new PresentInfoKHR
        {
            SType = StructureType.PresentInfoKhr,
            WaitSemaphoreCount = 1,
            PWaitSemaphores = &signalSemaphore,
            SwapchainCount = 1,
            PSwapchains = &swapChainHandle,
            PImageIndices = &imageIndex,
        };

        var presentStart = Stopwatch.GetTimestamp();
        var presentResult = _khrSwapChain!.QueuePresent(_presentQueue, in presentInfo);

        // Read back before any recreation below (it waits for this frame's fence).
        if (_captureRecorded)
            ReadBackCapture(frameFence);
        _frameWaitTicks += Stopwatch.GetTimestamp() - presentStart;

        if (presentResult == Result.ErrorOutOfDateKhr || presentResult == Result.SuboptimalKhr || _framebufferResized)
        {
            _framebufferResized = true;
            RecreateSwapchain(); // on failure (minimised) the flag stays set and BeginFrame retries
        }
        else if (presentResult != Result.Success)
        {
            throw new VulkanException($"[Vulkan] Failed to present a swapchain image: {presentResult}");
        }

        _currentFrame = (_currentFrame + 1) % MaxFramesInFlight;
    }

    public void SetClearColor(float r, float g, float b, float a = 1f)
    {
        _clearR = r;
        _clearG = g;
        _clearB = b;
        _clearA = a;
    }

    // Clear is handled by the render pass LoadOp.Clear — no explicit call needed.
    public void Clear() { }

    // Depth state is pipeline state in Vulkan — handled per-pipeline, not as a global toggle.
    public void EnableDepthTest() { }
    public void DisableDepthTest() { }

    public void RequestCapture()
    {
        if (!_captureSupported)
        {
            Log.Warning("[Vulkan] Frame capture requested but not available (needs EnableFrameCapture and a " +
                        "swapchain with TRANSFER_SRC usage in B8G8R8A8/R8G8B8A8).");
            return;
        }

        _captureRequested = true;
    }

    public bool TryTakeCapture([NotNullWhen(true)] out FrameCapture? capture)
    {
        capture = _completedCapture;
        _completedCapture = null;
        return capture is not null;
    }

    #endregion

    #region Frame capture

    // Records swapchain image → host buffer after the main pass. The pass leaves the image in
    // PRESENT_SRC; move it to TRANSFER_SRC for the copy and back again before present.
    private void RecordCapture(CommandBuffer cb, uint imageIndex)
    {
        _captureRequested = false;
        EnsureCaptureBuffer();

        var image = _swapChainImages![imageIndex];
        var range = new ImageSubresourceRange(ImageAspectFlags.ColorBit, 0, 1, 0, 1);

        // An explicit dependency on the colour writes, not only a chain through the render pass's outgoing
        // dependency: MoltenVK does not order the copy after the last tiles of the pass otherwise, and a slow frame
        // was captured with its bottom tiles still black.
        var toTransfer = new ImageMemoryBarrier
        {
            SType               = StructureType.ImageMemoryBarrier,
            SrcAccessMask       = AccessFlags.ColorAttachmentWriteBit,
            DstAccessMask       = AccessFlags.TransferReadBit,
            OldLayout           = ImageLayout.PresentSrcKhr,
            NewLayout           = ImageLayout.TransferSrcOptimal,
            SrcQueueFamilyIndex = Vk.QueueFamilyIgnored,
            DstQueueFamilyIndex = Vk.QueueFamilyIgnored,
            Image               = image,
            SubresourceRange    = range,
        };
        _vk!.CmdPipelineBarrier(cb, PipelineStageFlags.ColorAttachmentOutputBit | PipelineStageFlags.TransferBit,
            PipelineStageFlags.TransferBit, 0, 0, null, 0, null, 1, &toTransfer);

        var region = new BufferImageCopy
        {
            BufferOffset      = 0,
            BufferRowLength   = 0, // tightly packed
            BufferImageHeight = 0,
            ImageSubresource  = new ImageSubresourceLayers(ImageAspectFlags.ColorBit, 0, 0, 1),
            ImageOffset       = default,
            ImageExtent       = new Extent3D(_swapChainExtent.Width, _swapChainExtent.Height, 1),
        };
        _vk.CmdCopyImageToBuffer(cb, image, ImageLayout.TransferSrcOptimal, _captureBuffer!.Handle, 1, &region);

        var toPresent = toTransfer with
        {
            SrcAccessMask = AccessFlags.TransferReadBit,
            DstAccessMask = AccessFlags.None,
            OldLayout     = ImageLayout.TransferSrcOptimal,
            NewLayout     = ImageLayout.PresentSrcKhr,
        };
        var toHost = new BufferMemoryBarrier
        {
            SType               = StructureType.BufferMemoryBarrier,
            SrcAccessMask       = AccessFlags.TransferWriteBit,
            DstAccessMask       = AccessFlags.HostReadBit,
            SrcQueueFamilyIndex = Vk.QueueFamilyIgnored,
            DstQueueFamilyIndex = Vk.QueueFamilyIgnored,
            Buffer              = _captureBuffer.Handle,
            Offset              = 0,
            Size                = Vk.WholeSize,
        };
        _vk.CmdPipelineBarrier(cb, PipelineStageFlags.TransferBit,
            PipelineStageFlags.BottomOfPipeBit | PipelineStageFlags.HostBit,
            0, 0, null, 1, &toHost, 1, &toPresent);

        _captureRecorded = true;
    }

    // Waits for the frame that contains the copy, then converts the readback to RGBA8.
    private void ReadBackCapture(Fence frameFence)
    {
        _captureRecorded = false;
        _vk!.WaitForFences(_device, 1, in frameFence, true, ulong.MaxValue);

        var width = (int)_swapChainExtent.Width;
        var height = (int)_swapChainExtent.Height;
        var src = new ReadOnlySpan<byte>((void*)_captureBuffer!.MappedPointer, width * height * 4);
        var pixels = new byte[src.Length];
        var swapRedBlue = _swapChainImageFormat is Format.B8G8R8A8Unorm or Format.B8G8R8A8Srgb;
        for (var i = 0; i < src.Length; i += 4)
        {
            pixels[i] = swapRedBlue ? src[i + 2] : src[i];
            pixels[i + 1] = src[i + 1];
            pixels[i + 2] = swapRedBlue ? src[i] : src[i + 2];
            pixels[i + 3] = 255; // composited opaque; shader alpha is meaningless here
        }

        _completedCapture = new FrameCapture(width, height, pixels);
    }

    private void EnsureCaptureBuffer()
    {
        var size = (ulong)_swapChainExtent.Width * _swapChainExtent.Height * 4;
        if (_captureBuffer is not null && _captureBuffer.Size == size)
            return;

        // Replaced only between frames that do not capture; the old one is retired by the deletion queue.
        _captureBuffer?.Dispose();
        _captureBuffer = GpuBuffer.Create(this, size, BufferUsageFlags.TransferDstBit, GpuMemoryUsage.Readback);
    }

    #endregion

    #region Vulkan Init

    private void InitVulkan()
    {
        CreateInstance();
        SetupDebugMessenger();
        CreateSurface();
        PickPhysicalDevice();
        CreateLogicalDevice();
        CreateGpuMemory();
        CreateSwapchain();
        CreateCommandPool();
        CreateCommandBuffers();
        CreateSyncObjects();
        CreatePresentation(); // after the command pool: the scene target and tonemap pipeline use the upload queue
    }

    private void CreateInstance()
    {
        // On macOS Vk must bind the same library the bootstrap gave SDL — a mismatched pair
        // produces instances SDL_Vulkan_CreateSurface rejects.
        _vk = VulkanLoaderBootstrap.TryCreateVk() ?? Vk.GetApi();

        if (_enableValidationLayers && !CheckValidationLayerSupport())
        {
            Log.Warning("[Vulkan] Validation layers requested but not available — disabling.");
            _enableValidationLayers = false;
        }

        _validation.IsEnabled = _enableValidationLayers;
        if (_enableValidationLayers)
            _validationHandle = GCHandle.Alloc(_validation);

        var appInfo = new ApplicationInfo
        {
            SType = StructureType.ApplicationInfo,
            PApplicationName = (byte*)Marshal.StringToHGlobalAnsi("Mainframe Engine"),
            ApplicationVersion = EngineInfo.VulkanVersion,
            PEngineName = (byte*)Marshal.StringToHGlobalAnsi("Mainframe Engine"),
            EngineVersion = EngineInfo.VulkanVersion, // drivers may key per-engine workarounds on it
            ApiVersion = Vk.Version12
        };

        var createInfo = new InstanceCreateInfo
        {
            SType = StructureType.InstanceCreateInfo,
            PApplicationInfo = &appInfo
        };

        var extensions = GetRequiredExtensions();

        // Portability drivers (MoltenVK on macOS) are hidden by loaders >= 1.3.216 unless
        // portability enumeration is explicitly requested. macOS-only: desktop loaders
        // advertise this extension everywhere, and enabling it on Windows/Linux would
        // un-hide non-conformant translation-layer drivers in device enumeration.
        if (OperatingSystem.IsMacOS() && IsInstanceExtensionAvailable("VK_KHR_portability_enumeration"))
        {
            extensions = extensions.Append("VK_KHR_portability_enumeration").ToArray();
            createInfo.Flags |= InstanceCreateFlags.EnumeratePortabilityBitKhr;
        }

        createInfo.EnabledExtensionCount = (uint)extensions.Length;
        createInfo.PpEnabledExtensionNames = (byte**)SilkMarshal.StringArrayToPtr(extensions);

        if (_enableValidationLayers)
        {
            createInfo.EnabledLayerCount = (uint)_validationLayers.Length;
            createInfo.PpEnabledLayerNames = (byte**)SilkMarshal.StringArrayToPtr(_validationLayers);

            var debugCreateInfo = CreateDebugMessengerInfo();
            createInfo.PNext = &debugCreateInfo;
        }
        else
        {
            createInfo.EnabledLayerCount = 0;
            createInfo.PNext = null;
        }

        if (_vk.CreateInstance(in createInfo, null, out _instance) != Result.Success)
            throw new VulkanException("[Vulkan] Failed to create instance!");

        Log.Debug($"[Vulkan] Instance created.");

        Marshal.FreeHGlobal((IntPtr)appInfo.PApplicationName);
        Marshal.FreeHGlobal((IntPtr)appInfo.PEngineName);
        SilkMarshal.Free((nint)createInfo.PpEnabledExtensionNames);
        if (_enableValidationLayers)
            SilkMarshal.Free((nint)createInfo.PpEnabledLayerNames);
    }

    // The allocator, deletion queue and upload queue live as long as the device.
    private void CreateGpuMemory()
    {
        _allocator = GpuAllocator.Create(_vk!, _physicalDevice, _device);
        _deletions = new DeletionQueue(new VulkanDestroyer(_vk!, _device, _allocator));
        _uploads = new UploadQueue(this, _allocator, _deletions);
        _pipelineCache = new PipelineCache(_vk!, _physicalDevice, _device);
        _shaderModules = new ShaderModuleCache(_vk!, _device);
    }

    private string[] GetRequiredExtensions()
    {
        // SDL_Vulkan_GetInstanceExtensions through Silk's surface abstraction.
        var windowExtensions = _window.VkSurface!.GetRequiredExtensions(out var count);
        var extensions = SilkMarshal.PtrToStringArray((nint)windowExtensions, (int)count);
        return _enableValidationLayers
            ? extensions.Append(ExtDebugUtils.ExtensionName).ToArray()
            : extensions;
    }

    private bool IsInstanceExtensionAvailable(string name)
    {
        var count = 0u;
        _vk!.EnumerateInstanceExtensionProperties((byte*)null, ref count, null);
        var props = new ExtensionProperties[count];
        fixed (ExtensionProperties* ptr = props)
            _vk!.EnumerateInstanceExtensionProperties((byte*)null, ref count, ptr);
        return props.Any(p => Marshal.PtrToStringAnsi((IntPtr)p.ExtensionName) == name);
    }

    private bool IsDeviceExtensionAvailable(PhysicalDevice device, string name)
    {
        var count = 0u;
        _vk!.EnumerateDeviceExtensionProperties(device, (byte*)null, ref count, null);
        var props = new ExtensionProperties[count];
        fixed (ExtensionProperties* ptr = props)
            _vk!.EnumerateDeviceExtensionProperties(device, (byte*)null, ref count, ptr);
        return props.Any(p => Marshal.PtrToStringAnsi((IntPtr)p.ExtensionName) == name);
    }

    // Warnings and errors only: verbose/info traffic is per-call noise that would cost a string
    // allocation per message every frame. The callback is a static unmanaged function pointer (no
    // delegate to keep alive); the collector arrives through pUserData.
    private DebugUtilsMessengerCreateInfoEXT CreateDebugMessengerInfo() => new()
    {
        SType = StructureType.DebugUtilsMessengerCreateInfoExt,
        MessageSeverity =
            DebugUtilsMessageSeverityFlagsEXT.WarningBitExt |
            DebugUtilsMessageSeverityFlagsEXT.ErrorBitExt,
        MessageType =
            DebugUtilsMessageTypeFlagsEXT.GeneralBitExt |
            DebugUtilsMessageTypeFlagsEXT.PerformanceBitExt |
            DebugUtilsMessageTypeFlagsEXT.ValidationBitExt,
        PfnUserCallback = new PfnDebugUtilsMessengerCallbackEXT(&DebugCallback),
        PUserData = (void*)GCHandle.ToIntPtr(_validationHandle),
    };

    private void SetupDebugMessenger()
    {
        if (!_enableValidationLayers) return;
        if (!_vk!.TryGetInstanceExtension(_instance, out _debugUtils)) return;

        var createInfo = CreateDebugMessengerInfo();

        if (_debugUtils!.CreateDebugUtilsMessenger(_instance, in createInfo, null, out _debugMessenger) != Result.Success)
            throw new VulkanException("[Vulkan] Failed to set up debug messenger!");
    }

    private bool CheckValidationLayerSupport()
    {
        var count = 0u;
        _vk!.EnumerateInstanceLayerProperties(ref count, null);
        var layers = new LayerProperties[count];
        fixed (LayerProperties* ptr = layers)
            _vk!.EnumerateInstanceLayerProperties(ref count, ptr);

        var names = layers.Select(l => Marshal.PtrToStringAnsi((IntPtr)l.LayerName)).ToHashSet();
        return _validationLayers.All(names.Contains);
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static Bool32 DebugCallback(
        DebugUtilsMessageSeverityFlagsEXT severity,
        DebugUtilsMessageTypeFlagsEXT types,
        DebugUtilsMessengerCallbackDataEXT* data,
        void* userData)
    {
        if (userData is not null && GCHandle.FromIntPtr((nint)userData).Target is VulkanValidationLog log)
        {
            var message = Marshal.PtrToStringUTF8((nint)data->PMessage) ?? string.Empty;
            log.Record((severity & DebugUtilsMessageSeverityFlagsEXT.ErrorBitExt) != 0, message);
        }

        return Vk.False; // never abort the call that triggered the message
    }

    private void CreateSurface()
    {
        if (!_vk!.TryGetInstanceExtension<KhrSurface>(_instance, out _khrSurface))
            throw new NotSupportedException("[Vulkan] KHR_surface extension not found.");

        _surface = _window.VkSurface!.Create<AllocationCallbacks>(_instance.ToHandle(), null).ToSurface();
        Log.Debug("[Vulkan] Surface created.");
    }

    private void PickPhysicalDevice()
    {
        foreach (var d in _vk!.GetPhysicalDevices(_instance))
        {
            if (IsDeviceSuitable(d))
            {
                _physicalDevice = d;
                Log.Debug("[Vulkan] Physical device selected.");
                return;
            }
        }
        throw new VulkanException("[Vulkan] Failed to find a suitable GPU!");
    }

    private bool IsDeviceSuitable(PhysicalDevice device)
        => FindQueueFamilies(device).IsComplete();

    private QueueFamilyIndices FindQueueFamilies(PhysicalDevice device)
    {
        var indices = new QueueFamilyIndices();
        var count = 0u;
        _vk!.GetPhysicalDeviceQueueFamilyProperties(device, ref count, null);
        var families = new QueueFamilyProperties[count];
        fixed (QueueFamilyProperties* ptr = families)
            _vk!.GetPhysicalDeviceQueueFamilyProperties(device, ref count, ptr);

        for (var i = 0u; i < families.Length; i++)
        {
            if (families[i].QueueFlags.HasFlag(QueueFlags.GraphicsBit))
                indices.GraphicsFamily = i;

            _khrSurface!.GetPhysicalDeviceSurfaceSupport(device, i, _surface, out var present);
            if (present) indices.PresentFamily = i;

            if (indices.IsComplete()) break;
        }
        return indices;
    }

    private void CreateLogicalDevice()
    {
        var indices = FindQueueFamilies(_physicalDevice);
        var uniqueFamilies = new[] { indices.GraphicsFamily!.Value, indices.PresentFamily!.Value }.Distinct().ToArray();

        using var mem = GlobalMemory.Allocate(uniqueFamilies.Length * sizeof(DeviceQueueCreateInfo));
        var queueCreateInfos = (DeviceQueueCreateInfo*)Unsafe.AsPointer(ref mem.GetPinnableReference());

        float priority = 1f;
        for (int i = 0; i < uniqueFamilies.Length; i++)
        {
            queueCreateInfos[i] = new DeviceQueueCreateInfo
            {
                SType = StructureType.DeviceQueueCreateInfo,
                QueueFamilyIndex = uniqueFamilies[i],
                QueueCount = 1,
                PQueuePriorities = &priority
            };
        }

        var extensionList = new List<string>(_deviceExtensions);

        // Spec requires enabling VK_KHR_portability_subset when the device advertises it (MoltenVK does).
        if (IsDeviceExtensionAvailable(_physicalDevice, "VK_KHR_portability_subset"))
            extensionList.Add("VK_KHR_portability_subset");
        AddPresentationExtensions(extensionList); // sRGB swapchain with a UNORM overlay view, when available
        var deviceExtensions = extensionList.ToArray();

        // The lit shaders index their shadow sampler arrays with a loop counter, which needs
        // shaderSampledImageArrayDynamicIndexing (supported by MoltenVK, lavapipe and desktop GPUs).
        _vk!.GetPhysicalDeviceFeatures(_physicalDevice, out var supported);
        if (!supported.ShaderSampledImageArrayDynamicIndexing)
            Log.Warning("[Vulkan] shaderSampledImageArrayDynamicIndexing is not supported; shadow sampling is undefined on this device.");
        var features = new PhysicalDeviceFeatures
        {
            ShaderSampledImageArrayDynamicIndexing = supported.ShaderSampledImageArrayDynamicIndexing,
            SamplerAnisotropy = supported.SamplerAnisotropy, // material textures (M3); optional
        };
        if (supported.SamplerAnisotropy)
        {
            _vk.GetPhysicalDeviceProperties(_physicalDevice, out var deviceProps);
            _maxSamplerAnisotropy = deviceProps.Limits.MaxSamplerAnisotropy;
        }
        var createInfo = new DeviceCreateInfo
        {
            SType = StructureType.DeviceCreateInfo,
            QueueCreateInfoCount = (uint)uniqueFamilies.Length,
            PQueueCreateInfos = queueCreateInfos,
            PEnabledFeatures = &features,
            EnabledExtensionCount = (uint)deviceExtensions.Length,
            PpEnabledExtensionNames = (byte**)SilkMarshal.StringArrayToPtr(deviceExtensions)
        };

        if (_enableValidationLayers)
        {
            createInfo.EnabledLayerCount = (uint)_validationLayers.Length;
            createInfo.PpEnabledLayerNames = (byte**)SilkMarshal.StringArrayToPtr(_validationLayers);
        }

        if (_vk!.CreateDevice(_physicalDevice, in createInfo, null, out _device) != Result.Success)
            throw new VulkanException("[Vulkan] Failed to create logical device!");

        Log.Debug("[Vulkan] Logical device created.");
        _vk!.GetDeviceQueue(_device, indices.GraphicsFamily!.Value, 0, out _graphicsQueue);
        _vk!.GetDeviceQueue(_device, indices.PresentFamily!.Value, 0, out _presentQueue);

        if (_enableValidationLayers)
            SilkMarshal.Free((nint)createInfo.PpEnabledLayerNames);
        SilkMarshal.Free((nint)createInfo.PpEnabledExtensionNames);
    }

    private void CreateSwapchain(SwapchainKHR oldSwapchain = default)
    {
        var support = QuerySwapChainSupport(_physicalDevice);
        var (format, encoding, overlayFormat) = ChooseSurfaceFormat(support.Formats, _mutableFormatAvailable, _requestedEncoding);
        _encoding = encoding;
        _overlayFormat = overlayFormat;
        var presentMode = ChoosePresentMode(support.PresentModes);
        var extent = ChooseExtent(support.Capabilities);

        var imageCount = support.Capabilities.MinImageCount + 1;
        if (support.Capabilities.MaxImageCount > 0 && imageCount > support.Capabilities.MaxImageCount)
            imageCount = support.Capabilities.MaxImageCount;

        var createInfo = new SwapchainCreateInfoKHR
        {
            SType = StructureType.SwapchainCreateInfoKhr,
            Surface = _surface,
            MinImageCount = imageCount,
            ImageFormat = format.Format,
            ImageColorSpace = format.ColorSpace,
            ImageExtent = extent,
            ImageArrayLayers = 1,
            ImageUsage = ImageUsageFlags.ColorAttachmentBit,
        };

        // Frame capture copies out of the swapchain image, which needs TRANSFER_SRC usage.
        _captureSupported = _enableFrameCapture &&
                            (support.Capabilities.SupportedUsageFlags & ImageUsageFlags.TransferSrcBit) != 0 &&
                            format.Format is Format.B8G8R8A8Unorm or Format.B8G8R8A8Srgb
                                          or Format.R8G8B8A8Unorm or Format.R8G8B8A8Srgb;
        if (_captureSupported)
            createInfo.ImageUsage |= ImageUsageFlags.TransferSrcBit;
        else if (_enableFrameCapture)
            Log.Warning($"[Vulkan] Frame capture unavailable for swapchain format {format.Format}.");

        var indices = FindQueueFamilies(_physicalDevice);
        var queueFamilies = stackalloc[] { indices.GraphicsFamily!.Value, indices.PresentFamily!.Value };

        if (indices.GraphicsFamily != indices.PresentFamily)
        {
            createInfo = createInfo with
            {
                ImageSharingMode = SharingMode.Concurrent,
                QueueFamilyIndexCount = 2,
                PQueueFamilyIndices = queueFamilies,
            };
        }
        else
        {
            createInfo.ImageSharingMode = SharingMode.Exclusive;
        }

        createInfo = createInfo with
        {
            PreTransform = support.Capabilities.CurrentTransform,
            CompositeAlpha = CompositeAlphaFlagsKHR.OpaqueBitKhr,
            PresentMode = presentMode,
            Clipped = true,
            // Lets the driver hand over resources and keep presenting the old images meanwhile.
            OldSwapchain = oldSwapchain,
        };

        // sRGB swapchain whose images can also be viewed as UNORM (the overlay pass), when chosen.
        var formatList = default(ImageFormatListCreateInfo);
        var viewFormats = stackalloc Format[2];
        ApplyMutableFormat(ref createInfo, &formatList, viewFormats);

        if (_khrSwapChain is null && !_vk!.TryGetDeviceExtension(_instance, _device, out _khrSwapChain))
            throw new NotSupportedException("[Vulkan] VK_KHR_swapchain extension not found.");

        _khrSwapChain!.CreateSwapchain(_device, in createInfo, null, out _swapChain).Check("vkCreateSwapchainKHR");

        _khrSwapChain.GetSwapchainImages(_device, _swapChain, ref imageCount, null).Check("vkGetSwapchainImagesKHR");
        _swapChainImages = new Image[imageCount];
        fixed (Image* ptr = _swapChainImages)
            _khrSwapChain.GetSwapchainImages(_device, _swapChain, ref imageCount, ptr).Check("vkGetSwapchainImagesKHR");

        _swapChainImageFormat = format.Format;
        _swapChainExtent = extent;
        Log.Debug("[Vulkan] Swapchain created.");
    }

    private SwapChainSupportDetails QuerySwapChainSupport(PhysicalDevice device)
    {
        var details = new SwapChainSupportDetails();
        _khrSurface!.GetPhysicalDeviceSurfaceCapabilities(device, _surface, out details.Capabilities);

        var count = 0u;
        _khrSurface.GetPhysicalDeviceSurfaceFormats(device, _surface, ref count, null);
        details.Formats = count == 0 ? [] : new SurfaceFormatKHR[count];
        if (count > 0)
            fixed (SurfaceFormatKHR* ptr = details.Formats)
                _khrSurface.GetPhysicalDeviceSurfaceFormats(device, _surface, ref count, ptr);

        _khrSurface.GetPhysicalDeviceSurfacePresentModes(device, _surface, ref count, null);
        details.PresentModes = count == 0 ? [] : new PresentModeKHR[count];
        if (count > 0)
            fixed (PresentModeKHR* ptr = details.PresentModes)
                _khrSurface.GetPhysicalDeviceSurfacePresentModes(device, _surface, ref count, ptr);

        return details;
    }

    private PresentModeKHR ChoosePresentMode(IReadOnlyList<PresentModeKHR> modes)
    {
        if (_vsync)
            return PresentModeKHR.FifoKhr;

        foreach (var m in modes)
            if (m == PresentModeKHR.MailboxKhr) return m;
        foreach (var m in modes)
            if (m == PresentModeKHR.ImmediateKhr) return m;
        return PresentModeKHR.FifoKhr;
    }

    private Extent2D ChooseExtent(SurfaceCapabilitiesKHR capabilities)
    {
        if (capabilities.CurrentExtent.Width != uint.MaxValue)
            return capabilities.CurrentExtent;

        var size = WindowPixels.FramebufferSize(_window);
        return new Extent2D
        {
            Width = Math.Clamp((uint)size.X, capabilities.MinImageExtent.Width, capabilities.MaxImageExtent.Width),
            Height = Math.Clamp((uint)size.Y, capabilities.MinImageExtent.Height, capabilities.MaxImageExtent.Height),
        };
    }

    private void CreateCommandPool()
    {
        var poolInfo = new CommandPoolCreateInfo
        {
            SType = StructureType.CommandPoolCreateInfo,
            QueueFamilyIndex = FindQueueFamilies(_physicalDevice).GraphicsFamily!.Value,
            Flags = CommandPoolCreateFlags.ResetCommandBufferBit,
        };

        if (_vk!.CreateCommandPool(_device, in poolInfo, null, out _commandPool) != Result.Success)
            throw new VulkanException("[Vulkan] Failed to create command pool!");

        Log.Debug("[Vulkan] Command pool created.");
    }

    // One per frame slot, allocated once: they never depend on the swapchain.
    private void CreateCommandBuffers()
    {
        var allocInfo = new CommandBufferAllocateInfo
        {
            SType = StructureType.CommandBufferAllocateInfo,
            CommandPool = _commandPool,
            Level = CommandBufferLevel.Primary,
            CommandBufferCount = MaxFramesInFlight,
        };

        fixed (CommandBuffer* ptr = _commandBuffers)
            _vk!.AllocateCommandBuffers(_device, in allocInfo, ptr).Check("vkAllocateCommandBuffers (frame slots)");

        Log.Debug($"[Vulkan] {MaxFramesInFlight} frame-slot command buffers allocated.");
    }

    private void CreateSyncObjects()
    {
        var semInfo = new SemaphoreCreateInfo { SType = StructureType.SemaphoreCreateInfo };
        var fenceInfo = new FenceCreateInfo { SType = StructureType.FenceCreateInfo, Flags = FenceCreateFlags.SignaledBit };

        for (int i = 0; i < MaxFramesInFlight; i++)
        {
            _vk!.CreateSemaphore(_device, in semInfo, null, out _imageAvailableSemaphores[i]).Check("vkCreateSemaphore (image available)");
            _vk.CreateFence(_device, in fenceInfo, null, out _inFlightFences[i]).Check("vkCreateFence (frame slot)");
        }

        CreateRenderFinishedSemaphores();
        Log.Debug("[Vulkan] Sync objects created.");
    }

    private void CreateRenderFinishedSemaphores()
    {
        var semInfo = new SemaphoreCreateInfo { SType = StructureType.SemaphoreCreateInfo };
        _renderFinishedSemaphores = new Silk.NET.Vulkan.Semaphore[_swapChainImages!.Length];
        for (int i = 0; i < _renderFinishedSemaphores.Length; i++)
            _vk!.CreateSemaphore(_device, in semInfo, null, out _renderFinishedSemaphores[i]).Check("vkCreateSemaphore (render finished)");
    }

    private void DestroyRenderFinishedSemaphores()
    {
        foreach (var semaphore in _renderFinishedSemaphores)
            _vk!.DestroySemaphore(_device, semaphore, null);
        _renderFinishedSemaphores = [];
    }

    private Format FindDepthFormat()
    {
        var candidates = new[] { Format.D32Sfloat, Format.D32SfloatS8Uint, Format.D24UnormS8Uint };
        foreach (var format in candidates)
        {
            _vk!.GetPhysicalDeviceFormatProperties(_physicalDevice, format, out var props);
            if ((props.OptimalTilingFeatures & FormatFeatureFlags.DepthStencilAttachmentBit) != 0)
                return format;
        }
        throw new VulkanException("[Vulkan] Failed to find supported depth format!");
    }

    #endregion

    #region Swapchain Recreation

    // Everything sized or formatted by the swapchain except the swapchain handle itself (the scene target is
    // resized, not destroyed, so pipelines built against its render pass stay valid).
    private void DestroySwapchainDependents()
    {
        DestroySwapchainViews();
    }

    private void CleanupSwapchain()
    {
        DestroySwapchainDependents();
        if (_swapChain.Handle != 0)
            _khrSwapChain!.DestroySwapchain(_device, _swapChain, null);
        _swapChain = default;
    }

    /// <summary>
    /// Rebuilds the swapchain (resize, VSync/present-mode change, out-of-date). Returns false and keeps
    /// <see cref="_framebufferResized"/> set while the window has no area (minimised), so the caller
    /// skips the frame instead of spinning; the engine blocks on window events meanwhile.
    /// </summary>
    private bool RecreateSwapchain()
    {
        var size = WindowPixels.FramebufferSize(_window);
        if (size.X <= 0 || size.Y <= 0)
            return false;

        _khrSurface!.GetPhysicalDeviceSurfaceCapabilities(_physicalDevice, _surface, out var caps)
            .Check("vkGetPhysicalDeviceSurfaceCapabilitiesKHR");
        if (caps.CurrentExtent.Width == 0 || caps.CurrentExtent.Height == 0)
            return false;

        _vk!.DeviceWaitIdle(_device).Check("vkDeviceWaitIdle (swapchain recreation)");

        var oldSwapchain = _swapChain;
        var oldFormat = _swapChainImageFormat;
        var oldImageCount = _swapChainImages?.Length ?? 0;

        DestroySwapchainDependents();
        CreateSwapchain(oldSwapchain);
        _khrSwapChain!.DestroySwapchain(_device, oldSwapchain, null);

        // Every pipeline in the engine is built against the main render pass, whose colour format
        // is the swapchain's. ChooseSurfaceFormat is deterministic for a surface, so this does not
        // happen on supported platforms; if it does, fail loudly rather than record with
        // incompatible pipelines (rebuilding them would need a recreation callback per owner).
        if (_swapChainImageFormat != oldFormat)
        {
            throw new VulkanException(
                $"[Vulkan] Swapchain format changed on recreation ({oldFormat} → {_swapChainImageFormat}); " +
                "pipelines built against the main render pass would be incompatible.");
        }

        RecreatePresentation();

        if (_swapChainImages!.Length != oldImageCount)
        {
            Log.Info($"[Vulkan] Swapchain image count {oldImageCount} -> {_swapChainImages.Length}.");
            DestroyRenderFinishedSemaphores();
            CreateRenderFinishedSemaphores();
        }

        // The device was idle: release what the old swapchain resources (and earlier frames) held.
        _deletions!.Collect(_frameNumber);
        _uploads!.Release(_frameNumber);

        _framebufferResized = false;
        return true;
    }

    #endregion

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        var vk = _vk;
        if (vk is null) return;

        if (_device.Handle != 0)
        {
            vk.DeviceWaitIdle(_device);
            _shadowFallback?.Dispose();
            _shadowFallback = null;
            _captureBuffer?.Dispose();
            _captureBuffer = null;
            _frameContext?.Dispose();
            _frameContext = null;
            DestroyPresentation();
            CleanupSwapchain();

            // Everything games and subsystems released is idle now; then the memory itself.
            _deletions?.FlushAll();
            _uploads?.Dispose();
            _allocator?.Dispose();
            _shaderModules?.Dispose();
            _pipelineCache?.Dispose(); // saves the cache file

            for (int i = 0; i < MaxFramesInFlight; i++)
            {
                vk.DestroySemaphore(_device, _imageAvailableSemaphores[i], null);
                vk.DestroyFence(_device, _inFlightFences[i], null);
            }
            DestroyRenderFinishedSemaphores();

            vk.DestroyCommandPool(_device, _commandPool, null); // frees the frame-slot command buffers
            vk.DestroyDevice(_device, null);
            _device = default;
        }

        // The messenger may be missing even with validation on (extension unavailable).
        if (_debugUtils is not null && _debugMessenger.Handle != 0)
            _debugUtils.DestroyDebugUtilsMessenger(_instance, _debugMessenger, null);
        _debugUtils?.Dispose();

        if (_surface.Handle != 0)
            _khrSurface?.DestroySurface(_instance, _surface, null);
        _khrSurface?.Dispose();
        _khrSwapChain?.Dispose();
        if (_instance.Handle != 0)
            vk.DestroyInstance(_instance, null);
        vk.Dispose();

        // Freed last: instance destruction can still report through the callback.
        if (_validationHandle.IsAllocated)
            _validationHandle.Free();
    }

    private struct QueueFamilyIndices
    {
        public uint? GraphicsFamily;
        public uint? PresentFamily;
        public bool IsComplete() => GraphicsFamily.HasValue && PresentFamily.HasValue;
    }

    private struct SwapChainSupportDetails
    {
        public SurfaceCapabilitiesKHR Capabilities;
        public SurfaceFormatKHR[] Formats;
        public PresentModeKHR[] PresentModes;
    }
}
