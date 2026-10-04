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
internal sealed unsafe class VulkanRenderer : IRenderer, IVulkanContext
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

    // image views + render pass + framebuffers
    private ImageView[]? _swapChainImageViews;
    private RenderPass _renderPass;
    private Silk.NET.Vulkan.Framebuffer[]? _swapChainFramebuffers;

    // depth buffer
    private Format _depthFormat;
    private Image _depthImage;
    private DeviceMemory _depthImageMemory;
    private ImageView _depthImageView;

    // commands
    private CommandPool _commandPool;
    private CommandBuffer[]? _commandBuffers;

    // sync
    private const int MaxFramesInFlight = 2;
    private Silk.NET.Vulkan.Semaphore[]? _imageAvailableSemaphores;
    private Silk.NET.Vulkan.Semaphore[]? _renderFinishedSemaphores;
    private Fence[]? _inFlightFences;
    private Fence[]? _imagesInFlight;
    private int _currentFrame;
    private uint _currentImageIndex;
    private bool _frameStarted;
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
    private VkBuffer _captureBuffer;
    private DeviceMemory _captureMemory;
    private ulong _captureBufferSize;
    private nint _captureMapped;

    private readonly string[] _deviceExtensions = [KhrSwapchain.ExtensionName];

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
    public RenderPass RenderPass => _renderPass;
    public CommandPool CommandPool => _commandPool;
    public Queue GraphicsQueue => _graphicsQueue;
    public bool FrameStarted => _frameStarted;
    public CommandBuffer CurrentCommandBuffer => _frameStarted ? _commandBuffers![_currentImageIndex] : default;
    public Extent2D SwapchainExtent => _swapChainExtent;
    public uint SwapchainImageCount => (uint)(_swapChainImages?.Length ?? 0);
    public uint CurrentImageIndex => _currentImageIndex;
    public Silk.NET.Vulkan.Framebuffer CurrentFramebuffer => _swapChainFramebuffers![_currentImageIndex];
    public VulkanValidationLog Validation => _validation;

    #endregion

    public VulkanRenderer(IWindow window, in VulkanRendererOptions options)
    {
        _window = window;
        _enableValidationLayers = options.EnableValidation;
        _vsync = options.VSync;
        _enableFrameCapture = options.EnableFrameCapture;
        InitVulkan();
    }

    #region IRenderer implementation

    public void OnResize(Vector2D<int> newSize) => _framebufferResized = true;

    public void BeginFrame()
    {
        if (_framebufferResized)
        {
            _framebufferResized = false;
            RecreateSwapchain();
        }

        _vk!.WaitForFences(_device, 1, in _inFlightFences![_currentFrame], true, ulong.MaxValue);

        uint imageIndex;
        var result = _khrSwapChain!.AcquireNextImage(_device, _swapChain, ulong.MaxValue,
            _imageAvailableSemaphores![_currentFrame], default, &imageIndex);

        if (result == Result.ErrorOutOfDateKhr)
        {
            RecreateSwapchain();
            return;
        }

        if (result != Result.Success && result != Result.SuboptimalKhr)
            throw new VulkanException("[Vulkan] Failed to acquire swap chain image!");

        _currentImageIndex = imageIndex;
        _frameStarted = true;

        if (_imagesInFlight![imageIndex].Handle != 0)
            _vk!.WaitForFences(_device, 1, in _imagesInFlight[imageIndex], true, ulong.MaxValue);
        _imagesInFlight[imageIndex] = _inFlightFences[_currentFrame];

        // Begin command buffer
        var cb = _commandBuffers![imageIndex];
        _vk!.ResetCommandBuffer(cb, 0);

        var beginInfo = new CommandBufferBeginInfo { SType = StructureType.CommandBufferBeginInfo };
        if (_vk!.BeginCommandBuffer(cb, in beginInfo) != Result.Success)
            throw new VulkanException("[Vulkan] Failed to begin recording command buffer!");

    }

    public void BeginRenderPass()
    {
        if (!_frameStarted) return;
        var cb       = _commandBuffers![_currentImageIndex];
        var clearValues = stackalloc ClearValue[2];
        clearValues[0] = new ClearValue { Color = new() { Float32_0 = _clearR, Float32_1 = _clearG, Float32_2 = _clearB, Float32_3 = _clearA } };
        clearValues[1] = new ClearValue { DepthStencil = new() { Depth = 1.0f, Stencil = 0 } };

        var renderPassInfo = new RenderPassBeginInfo
        {
            SType           = StructureType.RenderPassBeginInfo,
            RenderPass      = _renderPass,
            Framebuffer     = _swapChainFramebuffers![_currentImageIndex],
            RenderArea      = { Offset = default, Extent = _swapChainExtent },
            ClearValueCount = 2,
            PClearValues    = clearValues,
        };

        _vk!.CmdBeginRenderPass(cb, &renderPassInfo, SubpassContents.Inline);
    }

    public void EndFrame()
    {
        if (!_frameStarted) return;
        _frameStarted = false;

        var imageIndex = _currentImageIndex;
        var cb = _commandBuffers![imageIndex];

        // Close render pass and command buffer
        _vk!.CmdEndRenderPass(cb);
        if (_captureRequested)
            RecordCapture(cb, imageIndex);
        if (_vk!.EndCommandBuffer(cb) != Result.Success)
            throw new VulkanException("[Vulkan] Failed to record command buffer!");

        var waitSemaphore = _imageAvailableSemaphores![_currentFrame];
        var waitStage = PipelineStageFlags.ColorAttachmentOutputBit;
        var signalSemaphore = _renderFinishedSemaphores![imageIndex];
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

        var frameFence = _inFlightFences![_currentFrame];
        _vk!.ResetFences(_device, 1, in frameFence);

        if (_vk!.QueueSubmit(_graphicsQueue, 1, in submitInfo, frameFence) != Result.Success)
            throw new VulkanException("[Vulkan] Failed to submit draw command buffer!");

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

        var presentResult = _khrSwapChain!.QueuePresent(_presentQueue, in presentInfo);

        if (presentResult == Result.ErrorOutOfDateKhr || presentResult == Result.SuboptimalKhr || _framebufferResized)
        {
            _framebufferResized = false;
            RecreateSwapchain();
        }
        else if (presentResult != Result.Success)
        {
            throw new VulkanException("[Vulkan] Failed to present swap chain image!");
        }

        if (_captureRecorded)
            ReadBackCapture(frameFence);

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
        _vk!.CmdPipelineBarrier(cb, PipelineStageFlags.ColorAttachmentOutputBit, PipelineStageFlags.TransferBit,
            0, 0, null, 0, null, 1, &toTransfer);

        var region = new BufferImageCopy
        {
            BufferOffset      = 0,
            BufferRowLength   = 0, // tightly packed
            BufferImageHeight = 0,
            ImageSubresource  = new ImageSubresourceLayers(ImageAspectFlags.ColorBit, 0, 0, 1),
            ImageOffset       = default,
            ImageExtent       = new Extent3D(_swapChainExtent.Width, _swapChainExtent.Height, 1),
        };
        _vk.CmdCopyImageToBuffer(cb, image, ImageLayout.TransferSrcOptimal, _captureBuffer, 1, &region);

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
            Buffer              = _captureBuffer,
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
        var src = new ReadOnlySpan<byte>((void*)_captureMapped, width * height * 4);
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
        if (_captureBuffer.Handle != 0 && _captureBufferSize == size)
            return;

        DestroyCaptureBuffer();

        var bufferInfo = new BufferCreateInfo
        {
            SType       = StructureType.BufferCreateInfo,
            Size        = size,
            Usage       = BufferUsageFlags.TransferDstBit,
            SharingMode = SharingMode.Exclusive,
        };
        if (_vk!.CreateBuffer(_device, in bufferInfo, null, out _captureBuffer) != Result.Success)
            throw new VulkanException("[Vulkan] Failed to create the frame capture buffer!");

        _vk.GetBufferMemoryRequirements(_device, _captureBuffer, out var memReq);
        const MemoryPropertyFlags required = MemoryPropertyFlags.HostVisibleBit | MemoryPropertyFlags.HostCoherentBit;
        var memoryType = TryFindMemoryType(memReq.MemoryTypeBits, required | MemoryPropertyFlags.HostCachedBit)
                         ?? FindMemoryType(memReq.MemoryTypeBits, required);
        var allocInfo = new MemoryAllocateInfo
        {
            SType           = StructureType.MemoryAllocateInfo,
            AllocationSize  = memReq.Size,
            MemoryTypeIndex = memoryType,
        };
        if (_vk.AllocateMemory(_device, in allocInfo, null, out _captureMemory) != Result.Success)
            throw new VulkanException("[Vulkan] Failed to allocate frame capture memory!");

        _vk.BindBufferMemory(_device, _captureBuffer, _captureMemory, 0);
        void* mapped;
        _vk.MapMemory(_device, _captureMemory, 0, size, 0, &mapped);
        _captureMapped = (nint)mapped;
        _captureBufferSize = size;
    }

    private void DestroyCaptureBuffer()
    {
        if (_captureBuffer.Handle == 0)
            return;

        _vk!.UnmapMemory(_device, _captureMemory);
        _vk.DestroyBuffer(_device, _captureBuffer, null);
        _vk.FreeMemory(_device, _captureMemory, null);
        _captureBuffer = default;
        _captureMemory = default;
        _captureMapped = 0;
        _captureBufferSize = 0;
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
        CreateSwapchain();
        CreateImageViews();
        CreateRenderPass();
        CreateDepthResources();
        CreateFramebuffers();
        CreateCommandPool();
        CreateCommandBuffers();
        CreateSyncObjects();
    }

    private void CreateInstance()
    {
        // On macOS Vk must bind the same library the bootstrap gave GLFW — a mismatched pair
        // produces instances glfwCreateWindowSurface rejects.
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
            ApplicationVersion = new Version32(1, 0, 0),
            PEngineName = (byte*)Marshal.StringToHGlobalAnsi("Mainframe Engine"),
            EngineVersion = new Version32(1, 0, 0),
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

        Log.Info($"[Vulkan] Instance created.");

        Marshal.FreeHGlobal((IntPtr)appInfo.PApplicationName);
        Marshal.FreeHGlobal((IntPtr)appInfo.PEngineName);
        SilkMarshal.Free((nint)createInfo.PpEnabledExtensionNames);
        if (_enableValidationLayers)
            SilkMarshal.Free((nint)createInfo.PpEnabledLayerNames);
    }

    private string[] GetRequiredExtensions()
    {
        var glfwExtensions = _window.VkSurface!.GetRequiredExtensions(out var count);
        var extensions = SilkMarshal.PtrToStringArray((nint)glfwExtensions, (int)count);
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
        Log.Info("[Vulkan] Surface created.");
    }

    private void PickPhysicalDevice()
    {
        foreach (var d in _vk!.GetPhysicalDevices(_instance))
        {
            if (IsDeviceSuitable(d))
            {
                _physicalDevice = d;
                Log.Info("[Vulkan] Physical device selected.");
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

        var deviceExtensions = _deviceExtensions;

        // Spec requires enabling VK_KHR_portability_subset when the device advertises it (MoltenVK does).
        if (IsDeviceExtensionAvailable(_physicalDevice, "VK_KHR_portability_subset"))
            deviceExtensions = deviceExtensions.Append("VK_KHR_portability_subset").ToArray();

        var features = new PhysicalDeviceFeatures();
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

        Log.Info("[Vulkan] Logical device created.");
        _vk!.GetDeviceQueue(_device, indices.GraphicsFamily!.Value, 0, out _graphicsQueue);
        _vk!.GetDeviceQueue(_device, indices.PresentFamily!.Value, 0, out _presentQueue);

        if (_enableValidationLayers)
            SilkMarshal.Free((nint)createInfo.PpEnabledLayerNames);
        SilkMarshal.Free((nint)createInfo.PpEnabledExtensionNames);
    }

    private void CreateSwapchain()
    {
        var support = QuerySwapChainSupport(_physicalDevice);
        var format = ChooseSurfaceFormat(support.Formats);
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
            OldSwapchain = default
        };

        if (!_vk!.TryGetDeviceExtension(_instance, _device, out _khrSwapChain))
            throw new NotSupportedException("[Vulkan] VK_KHR_swapchain extension not found.");

        if (_khrSwapChain!.CreateSwapchain(_device, in createInfo, null, out _swapChain) != Result.Success)
            throw new VulkanException("[Vulkan] Failed to create swap chain!");

        _khrSwapChain.GetSwapchainImages(_device, _swapChain, ref imageCount, null);
        _swapChainImages = new Image[imageCount];
        fixed (Image* ptr = _swapChainImages)
            _khrSwapChain.GetSwapchainImages(_device, _swapChain, ref imageCount, ptr);

        _swapChainImageFormat = format.Format;
        _swapChainExtent = extent;
        Log.Info("[Vulkan] Swapchain created.");
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

    private static SurfaceFormatKHR ChooseSurfaceFormat(SurfaceFormatKHR[] formats)
    {
        foreach (var f in formats)
            if (f is { Format: Format.B8G8R8A8Unorm, ColorSpace: ColorSpaceKHR.SpaceSrgbNonlinearKhr })
                return f;
        return formats[0];
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

        var size = _window.FramebufferSize;
        return new Extent2D
        {
            Width = Math.Clamp((uint)size.X, capabilities.MinImageExtent.Width, capabilities.MaxImageExtent.Width),
            Height = Math.Clamp((uint)size.Y, capabilities.MinImageExtent.Height, capabilities.MaxImageExtent.Height),
        };
    }

    private void CreateImageViews()
    {
        _swapChainImageViews = new ImageView[_swapChainImages!.Length];
        for (int i = 0; i < _swapChainImages.Length; i++)
        {
            var createInfo = new ImageViewCreateInfo
            {
                SType = StructureType.ImageViewCreateInfo,
                Image = _swapChainImages[i],
                ViewType = ImageViewType.Type2D,
                Format = _swapChainImageFormat,
                Components = { R = ComponentSwizzle.Identity, G = ComponentSwizzle.Identity, B = ComponentSwizzle.Identity, A = ComponentSwizzle.Identity },
                SubresourceRange = { AspectMask = ImageAspectFlags.ColorBit, BaseMipLevel = 0, LevelCount = 1, BaseArrayLayer = 0, LayerCount = 1 }
            };

            if (_vk!.CreateImageView(_device, in createInfo, null, out _swapChainImageViews[i]) != Result.Success)
                throw new VulkanException("[Vulkan] Failed to create image view!");
        }
    }

    private void CreateRenderPass()
    {
        _depthFormat = FindDepthFormat();

        var colorAttachment = new AttachmentDescription
        {
            Format         = _swapChainImageFormat,
            Samples        = SampleCountFlags.Count1Bit,
            LoadOp         = AttachmentLoadOp.Clear,
            StoreOp        = AttachmentStoreOp.Store,
            StencilLoadOp  = AttachmentLoadOp.DontCare,
            StencilStoreOp = AttachmentStoreOp.DontCare,
            InitialLayout  = ImageLayout.Undefined,
            FinalLayout    = ImageLayout.PresentSrcKhr,
        };

        var depthAttachment = new AttachmentDescription
        {
            Format         = _depthFormat,
            Samples        = SampleCountFlags.Count1Bit,
            LoadOp         = AttachmentLoadOp.Clear,
            StoreOp        = AttachmentStoreOp.DontCare,
            StencilLoadOp  = AttachmentLoadOp.DontCare,
            StencilStoreOp = AttachmentStoreOp.DontCare,
            InitialLayout  = ImageLayout.Undefined,
            FinalLayout    = ImageLayout.DepthStencilAttachmentOptimal,
        };

        var colorRef = new AttachmentReference { Attachment = 0, Layout = ImageLayout.ColorAttachmentOptimal };
        var depthRef = new AttachmentReference { Attachment = 1, Layout = ImageLayout.DepthStencilAttachmentOptimal };
        var subpass = new SubpassDescription
        {
            PipelineBindPoint       = PipelineBindPoint.Graphics,
            ColorAttachmentCount    = 1,
            PColorAttachments       = &colorRef,
            PDepthStencilAttachment = &depthRef,
        };

        var dependency = new SubpassDependency
        {
            SrcSubpass    = Vk.SubpassExternal,
            DstSubpass    = 0,
            SrcStageMask  = PipelineStageFlags.ColorAttachmentOutputBit | PipelineStageFlags.EarlyFragmentTestsBit,
            SrcAccessMask = 0,
            DstStageMask  = PipelineStageFlags.ColorAttachmentOutputBit | PipelineStageFlags.EarlyFragmentTestsBit,
            DstAccessMask = AccessFlags.ColorAttachmentWriteBit | AccessFlags.DepthStencilAttachmentWriteBit,
        };

        var attachments = stackalloc AttachmentDescription[] { colorAttachment, depthAttachment };
        var renderPassInfo = new RenderPassCreateInfo
        {
            SType           = StructureType.RenderPassCreateInfo,
            AttachmentCount = 2,
            PAttachments    = attachments,
            SubpassCount    = 1,
            PSubpasses      = &subpass,
            DependencyCount = 1,
            PDependencies   = &dependency,
        };

        if (_vk!.CreateRenderPass(_device, in renderPassInfo, null, out _renderPass) != Result.Success)
            throw new VulkanException("[Vulkan] Failed to create render pass!");

        Log.Info("[Vulkan] Render pass created.");
    }

    private void CreateFramebuffers()
    {
        _swapChainFramebuffers = new Silk.NET.Vulkan.Framebuffer[_swapChainImageViews!.Length];
        var fbAttachments = stackalloc ImageView[2];
        for (int i = 0; i < _swapChainImageViews.Length; i++)
        {
            fbAttachments[0] = _swapChainImageViews[i];
            fbAttachments[1] = _depthImageView;
            var fbInfo = new FramebufferCreateInfo
            {
                SType           = StructureType.FramebufferCreateInfo,
                RenderPass      = _renderPass,
                AttachmentCount = 2,
                PAttachments    = fbAttachments,
                Width           = _swapChainExtent.Width,
                Height          = _swapChainExtent.Height,
                Layers          = 1,
            };

            if (_vk!.CreateFramebuffer(_device, in fbInfo, null, out _swapChainFramebuffers[i]) != Result.Success)
                throw new VulkanException("[Vulkan] Failed to create framebuffer!");
        }
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

        Log.Info("[Vulkan] Command pool created.");
    }

    private void CreateCommandBuffers()
    {
        _commandBuffers = new CommandBuffer[_swapChainFramebuffers!.Length];
        var allocInfo = new CommandBufferAllocateInfo
        {
            SType = StructureType.CommandBufferAllocateInfo,
            CommandPool = _commandPool,
            Level = CommandBufferLevel.Primary,
            CommandBufferCount = (uint)_commandBuffers.Length,
        };

        fixed (CommandBuffer* ptr = _commandBuffers)
            if (_vk!.AllocateCommandBuffers(_device, in allocInfo, ptr) != Result.Success)
                throw new VulkanException("[Vulkan] Failed to allocate command buffers!");

        Log.Info($"[Vulkan] {_commandBuffers.Length} command buffers allocated.");
    }

    private void CreateSyncObjects()
    {
        _imageAvailableSemaphores = new Silk.NET.Vulkan.Semaphore[MaxFramesInFlight];
        _renderFinishedSemaphores = new Silk.NET.Vulkan.Semaphore[_swapChainImages!.Length];
        _inFlightFences = new Fence[MaxFramesInFlight];
        _imagesInFlight = new Fence[_swapChainImages!.Length];

        var semInfo = new SemaphoreCreateInfo { SType = StructureType.SemaphoreCreateInfo };
        var fenceInfo = new FenceCreateInfo { SType = StructureType.FenceCreateInfo, Flags = FenceCreateFlags.SignaledBit };

        for (int i = 0; i < MaxFramesInFlight; i++)
        {
            if (_vk!.CreateSemaphore(_device, in semInfo, null, out _imageAvailableSemaphores[i]) != Result.Success ||
                _vk!.CreateFence(_device, in fenceInfo, null, out _inFlightFences[i]) != Result.Success)
                throw new VulkanException("[Vulkan] Failed to create sync objects!");
        }

        for (int i = 0; i < _renderFinishedSemaphores.Length; i++)
            if (_vk!.CreateSemaphore(_device, in semInfo, null, out _renderFinishedSemaphores[i]) != Result.Success)
                throw new VulkanException("[Vulkan] Failed to create render finished semaphore!");

        Log.Info("[Vulkan] Sync objects created.");
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

    private uint FindMemoryType(uint typeFilter, MemoryPropertyFlags properties)
        => TryFindMemoryType(typeFilter, properties)
           ?? throw new VulkanException("[Vulkan] Failed to find suitable memory type!");

    private uint? TryFindMemoryType(uint typeFilter, MemoryPropertyFlags properties)
    {
        _vk!.GetPhysicalDeviceMemoryProperties(_physicalDevice, out var memProps);
        for (uint i = 0; i < memProps.MemoryTypeCount; i++)
        {
            if ((typeFilter & (1u << (int)i)) != 0 &&
                (memProps.MemoryTypes[(int)i].PropertyFlags & properties) == properties)
                return i;
        }
        return null;
    }

    private void CreateDepthResources()
    {
        var imageInfo = new ImageCreateInfo
        {
            SType         = StructureType.ImageCreateInfo,
            ImageType     = ImageType.Type2D,
            Format        = _depthFormat,
            Extent        = new Extent3D(_swapChainExtent.Width, _swapChainExtent.Height, 1),
            MipLevels     = 1,
            ArrayLayers   = 1,
            Samples       = SampleCountFlags.Count1Bit,
            Tiling        = ImageTiling.Optimal,
            Usage         = ImageUsageFlags.DepthStencilAttachmentBit,
            SharingMode   = SharingMode.Exclusive,
            InitialLayout = ImageLayout.Undefined,
        };

        if (_vk!.CreateImage(_device, in imageInfo, null, out _depthImage) != Result.Success)
            throw new VulkanException("[Vulkan] Failed to create depth image!");

        _vk!.GetImageMemoryRequirements(_device, _depthImage, out var memReq);

        var allocInfo = new MemoryAllocateInfo
        {
            SType           = StructureType.MemoryAllocateInfo,
            AllocationSize  = memReq.Size,
            MemoryTypeIndex = FindMemoryType(memReq.MemoryTypeBits, MemoryPropertyFlags.DeviceLocalBit),
        };

        if (_vk!.AllocateMemory(_device, in allocInfo, null, out _depthImageMemory) != Result.Success)
            throw new VulkanException("[Vulkan] Failed to allocate depth image memory!");

        _vk!.BindImageMemory(_device, _depthImage, _depthImageMemory, 0);

        var viewInfo = new ImageViewCreateInfo
        {
            SType            = StructureType.ImageViewCreateInfo,
            Image            = _depthImage,
            ViewType         = ImageViewType.Type2D,
            Format           = _depthFormat,
            SubresourceRange =
            {
                AspectMask     = ImageAspectFlags.DepthBit,
                BaseMipLevel   = 0,
                LevelCount     = 1,
                BaseArrayLayer = 0,
                LayerCount     = 1,
            },
        };

        if (_vk!.CreateImageView(_device, in viewInfo, null, out _depthImageView) != Result.Success)
            throw new VulkanException("[Vulkan] Failed to create depth image view!");

        Log.Info("[Vulkan] Depth resources created.");
    }

    private void DestroyDepthResources()
    {
        _vk!.DestroyImageView(_device, _depthImageView, null);
        _vk!.FreeMemory(_device, _depthImageMemory, null);
        _vk!.DestroyImage(_device, _depthImage, null);
    }

    #endregion

    #region Swapchain Recreation

    private void CleanupSwapchain()
    {
        DestroyDepthResources();

        if (_swapChainFramebuffers is not null)
            foreach (var fb in _swapChainFramebuffers)
                _vk!.DestroyFramebuffer(_device, fb, null);

        if (_commandBuffers is not null)
            fixed (CommandBuffer* ptr = _commandBuffers)
                _vk!.FreeCommandBuffers(_device, _commandPool, (uint)_commandBuffers.Length, ptr);

        if (_swapChainImageViews is not null)
            foreach (var iv in _swapChainImageViews)
                _vk!.DestroyImageView(_device, iv, null);

        _khrSwapChain!.DestroySwapchain(_device, _swapChain, null);
    }

    private void RecreateSwapchain()
    {
        var size = _window.FramebufferSize;
        while (size.X == 0 || size.Y == 0)
        {
            size = _window.FramebufferSize;
            _window.DoEvents();
        }

        _vk!.DeviceWaitIdle(_device);
        CleanupSwapchain();
        CreateSwapchain();
        CreateImageViews();
        CreateDepthResources();
        CreateFramebuffers();
        CreateCommandBuffers();
        _imagesInFlight = new Fence[_swapChainImages!.Length];
    }

    #endregion

    public void Dispose()
    {
        _vk!.DeviceWaitIdle(_device);
        DestroyCaptureBuffer();
        CleanupSwapchain();

        for (int i = 0; i < MaxFramesInFlight; i++)
        {
            _vk!.DestroySemaphore(_device, _imageAvailableSemaphores![i], null);
            _vk!.DestroyFence(_device, _inFlightFences![i], null);
        }

        for (int i = 0; i < _renderFinishedSemaphores!.Length; i++)
            _vk!.DestroySemaphore(_device, _renderFinishedSemaphores[i], null);

        _vk!.DestroyCommandPool(_device, _commandPool, null);
        _vk!.DestroyRenderPass(_device, _renderPass, null);
        _vk!.DestroyDevice(_device, null);

        if (_enableValidationLayers)
            _debugUtils!.DestroyDebugUtilsMessenger(_instance, _debugMessenger, null);

        _khrSurface?.DestroySurface(_instance, _surface, null);
        _vk?.DestroyInstance(_instance, null);
        _vk?.Dispose();

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
