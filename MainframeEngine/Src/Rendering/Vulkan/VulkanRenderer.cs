using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Silk.NET.Core;
using Silk.NET.Core.Native;
using Silk.NET.Maths;
using Silk.NET.Vulkan;
using Silk.NET.Vulkan.Extensions.EXT;
using Silk.NET.Vulkan.Extensions.KHR;
using Silk.NET.Windowing;

namespace MainframeEngine;

/// <summary>
/// Vulkan rendering backend.
/// Implements IVulkanContext so shapes can record draw commands into the active command buffer.
/// </summary>
internal unsafe class VulkanRenderer : IRenderer, IVulkanContext
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

    // clear color
    private float _clearR, _clearG, _clearB, _clearA = 1f;

    // validation
    private bool _enableValidationLayers;
    private readonly string[] _validationLayers = ["VK_LAYER_KHRONOS_validation"];
    private ExtDebugUtils? _debugUtils;
    private DebugUtilsMessengerEXT _debugMessenger;

    private readonly string[] _deviceExtensions = [KhrSwapchain.ExtensionName];

    #region IRenderer

    public RenderingBackend Backend => RenderingBackend.Vulkan;

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

    #endregion

    public VulkanRenderer(IWindow window, bool enableValidationLayers)
    {
        _window = window;
        _enableValidationLayers = enableValidationLayers;
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

        _vk!.WaitForFences(_device, 1, _inFlightFences![_currentFrame], true, ulong.MaxValue);

        uint imageIndex;
        var result = _khrSwapChain!.AcquireNextImage(_device, _swapChain, ulong.MaxValue,
            _imageAvailableSemaphores![_currentFrame], default, &imageIndex);

        if (result == Result.ErrorOutOfDateKhr)
        {
            RecreateSwapchain();
            return;
        }
        else if (result != Result.Success && result != Result.SuboptimalKhr)
        {
            throw new Exception("[Vulkan] Failed to acquire swap chain image!");
        }

        _currentImageIndex = imageIndex;
        _frameStarted = true;

        if (_imagesInFlight![imageIndex].Handle != 0)
            _vk!.WaitForFences(_device, 1, _imagesInFlight[imageIndex], true, ulong.MaxValue);
        _imagesInFlight[imageIndex] = _inFlightFences[_currentFrame];

        // Begin command buffer
        var cb = _commandBuffers![imageIndex];
        _vk!.ResetCommandBuffer(cb, 0);

        var beginInfo = new CommandBufferBeginInfo { SType = StructureType.CommandBufferBeginInfo };
        if (_vk!.BeginCommandBuffer(cb, beginInfo) != Result.Success)
            throw new Exception("[Vulkan] Failed to begin recording command buffer!");

        // Begin render pass — shapes record their draw commands while this is open
        var clearColor = new ClearValue
        {
            Color = new() { Float32_0 = _clearR, Float32_1 = _clearG, Float32_2 = _clearB, Float32_3 = _clearA }
        };

        var renderPassInfo = new RenderPassBeginInfo
        {
            SType = StructureType.RenderPassBeginInfo,
            RenderPass = _renderPass,
            Framebuffer = _swapChainFramebuffers![imageIndex],
            RenderArea = { Offset = default, Extent = _swapChainExtent },
            ClearValueCount = 1,
            PClearValues = &clearColor,
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
        if (_vk!.EndCommandBuffer(cb) != Result.Success)
            throw new Exception("[Vulkan] Failed to record command buffer!");

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

        _vk!.ResetFences(_device, 1, _inFlightFences![_currentFrame]);

        if (_vk!.QueueSubmit(_graphicsQueue, 1, submitInfo, _inFlightFences[_currentFrame]) != Result.Success)
            throw new Exception("[Vulkan] Failed to submit draw command buffer!");

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

        var presentResult = _khrSwapChain!.QueuePresent(_presentQueue, presentInfo);

        if (presentResult == Result.ErrorOutOfDateKhr || presentResult == Result.SuboptimalKhr || _framebufferResized)
        {
            _framebufferResized = false;
            RecreateSwapchain();
        }
        else if (presentResult != Result.Success)
        {
            throw new Exception("[Vulkan] Failed to present swap chain image!");
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
        CreateFramebuffers();
        CreateCommandPool();
        CreateCommandBuffers();
        CreateSyncObjects();
    }

    private void CreateInstance()
    {
        _vk = Vk.GetApi();

        if (_enableValidationLayers && !CheckValidationLayerSupport())
        {
            Log.Warning("[Vulkan] Validation layers requested but not available — disabling.");
            _enableValidationLayers = false;
        }

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
        createInfo.EnabledExtensionCount = (uint)extensions.Length;
        createInfo.PpEnabledExtensionNames = (byte**)SilkMarshal.StringArrayToPtr(extensions);

        if (_enableValidationLayers)
        {
            createInfo.EnabledLayerCount = (uint)_validationLayers.Length;
            createInfo.PpEnabledLayerNames = (byte**)SilkMarshal.StringArrayToPtr(_validationLayers);

            DebugUtilsMessengerCreateInfoEXT debugCreateInfo = new();
            PopulateDebugMessengerCreateInfo(ref debugCreateInfo);
            createInfo.PNext = &debugCreateInfo;
        }
        else
        {
            createInfo.EnabledLayerCount = 0;
            createInfo.PNext = null;
        }

        if (_vk.CreateInstance(createInfo, null, out _instance) != Result.Success)
            throw new Exception("[Vulkan] Failed to create instance!");

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

    private static void PopulateDebugMessengerCreateInfo(ref DebugUtilsMessengerCreateInfoEXT createInfo)
    {
        createInfo.SType = StructureType.DebugUtilsMessengerCreateInfoExt;
        createInfo.MessageSeverity =
            DebugUtilsMessageSeverityFlagsEXT.VerboseBitExt |
            DebugUtilsMessageSeverityFlagsEXT.WarningBitExt |
            DebugUtilsMessageSeverityFlagsEXT.ErrorBitExt;
        createInfo.MessageType =
            DebugUtilsMessageTypeFlagsEXT.GeneralBitExt |
            DebugUtilsMessageTypeFlagsEXT.PerformanceBitExt |
            DebugUtilsMessageTypeFlagsEXT.ValidationBitExt;
        createInfo.PfnUserCallback = (DebugUtilsMessengerCallbackFunctionEXT)DebugCallback;
    }

    private void SetupDebugMessenger()
    {
        if (!_enableValidationLayers) return;
        if (!_vk!.TryGetInstanceExtension(_instance, out _debugUtils)) return;

        var createInfo = new DebugUtilsMessengerCreateInfoEXT();
        PopulateDebugMessengerCreateInfo(ref createInfo);

        if (_debugUtils!.CreateDebugUtilsMessenger(_instance, createInfo, null, out _debugMessenger) != Result.Success)
            throw new Exception("[Vulkan] Failed to set up debug messenger!");
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

    private static uint DebugCallback(
        DebugUtilsMessageSeverityFlagsEXT severity,
        DebugUtilsMessageTypeFlagsEXT types,
        DebugUtilsMessengerCallbackDataEXT* data,
        void* userData)
    {
        Log.Info($"[Vulkan] {Marshal.PtrToStringAnsi((nint)data->PMessage)}");
        return Vk.False;
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
        throw new Exception("[Vulkan] Failed to find a suitable GPU!");
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

        var features = new PhysicalDeviceFeatures();
        var createInfo = new DeviceCreateInfo
        {
            SType = StructureType.DeviceCreateInfo,
            QueueCreateInfoCount = (uint)uniqueFamilies.Length,
            PQueueCreateInfos = queueCreateInfos,
            PEnabledFeatures = &features,
            EnabledExtensionCount = (uint)_deviceExtensions.Length,
            PpEnabledExtensionNames = (byte**)SilkMarshal.StringArrayToPtr(_deviceExtensions)
        };

        if (_enableValidationLayers)
        {
            createInfo.EnabledLayerCount = (uint)_validationLayers.Length;
            createInfo.PpEnabledLayerNames = (byte**)SilkMarshal.StringArrayToPtr(_validationLayers);
        }

        if (_vk!.CreateDevice(_physicalDevice, createInfo, null, out _device) != Result.Success)
            throw new Exception("[Vulkan] Failed to create logical device!");

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

        if (_khrSwapChain!.CreateSwapchain(_device, createInfo, null, out _swapChain) != Result.Success)
            throw new Exception("[Vulkan] Failed to create swap chain!");

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

    private static PresentModeKHR ChoosePresentMode(IReadOnlyList<PresentModeKHR> modes)
    {
        foreach (var m in modes)
            if (m == PresentModeKHR.MailboxKhr) return m;
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

            if (_vk!.CreateImageView(_device, createInfo, null, out _swapChainImageViews[i]) != Result.Success)
                throw new Exception("[Vulkan] Failed to create image view!");
        }
    }

    private void CreateRenderPass()
    {
        var colorAttachment = new AttachmentDescription
        {
            Format = _swapChainImageFormat,
            Samples = SampleCountFlags.Count1Bit,
            LoadOp = AttachmentLoadOp.Clear,
            StoreOp = AttachmentStoreOp.Store,
            StencilLoadOp = AttachmentLoadOp.DontCare,
            StencilStoreOp = AttachmentStoreOp.DontCare,
            InitialLayout = ImageLayout.Undefined,
            FinalLayout = ImageLayout.PresentSrcKhr,
        };

        var colorRef = new AttachmentReference { Attachment = 0, Layout = ImageLayout.ColorAttachmentOptimal };
        var subpass = new SubpassDescription
        {
            PipelineBindPoint = PipelineBindPoint.Graphics,
            ColorAttachmentCount = 1,
            PColorAttachments = &colorRef,
        };

        var dependency = new SubpassDependency
        {
            SrcSubpass = Vk.SubpassExternal,
            DstSubpass = 0,
            SrcStageMask = PipelineStageFlags.ColorAttachmentOutputBit,
            SrcAccessMask = 0,
            DstStageMask = PipelineStageFlags.ColorAttachmentOutputBit,
            DstAccessMask = AccessFlags.ColorAttachmentWriteBit,
        };

        var renderPassInfo = new RenderPassCreateInfo
        {
            SType = StructureType.RenderPassCreateInfo,
            AttachmentCount = 1,
            PAttachments = &colorAttachment,
            SubpassCount = 1,
            PSubpasses = &subpass,
            DependencyCount = 1,
            PDependencies = &dependency,
        };

        if (_vk!.CreateRenderPass(_device, renderPassInfo, null, out _renderPass) != Result.Success)
            throw new Exception("[Vulkan] Failed to create render pass!");

        Log.Info("[Vulkan] Render pass created.");
    }

    private void CreateFramebuffers()
    {
        _swapChainFramebuffers = new Silk.NET.Vulkan.Framebuffer[_swapChainImageViews!.Length];
        for (int i = 0; i < _swapChainImageViews.Length; i++)
        {
            var attachment = _swapChainImageViews[i];
            var fbInfo = new FramebufferCreateInfo
            {
                SType = StructureType.FramebufferCreateInfo,
                RenderPass = _renderPass,
                AttachmentCount = 1,
                PAttachments = &attachment,
                Width = _swapChainExtent.Width,
                Height = _swapChainExtent.Height,
                Layers = 1,
            };

            if (_vk!.CreateFramebuffer(_device, fbInfo, null, out _swapChainFramebuffers[i]) != Result.Success)
                throw new Exception("[Vulkan] Failed to create framebuffer!");
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

        if (_vk!.CreateCommandPool(_device, poolInfo, null, out _commandPool) != Result.Success)
            throw new Exception("[Vulkan] Failed to create command pool!");

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
            if (_vk!.AllocateCommandBuffers(_device, allocInfo, ptr) != Result.Success)
                throw new Exception("[Vulkan] Failed to allocate command buffers!");

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
            if (_vk!.CreateSemaphore(_device, semInfo, null, out _imageAvailableSemaphores[i]) != Result.Success ||
                _vk!.CreateFence(_device, fenceInfo, null, out _inFlightFences[i]) != Result.Success)
                throw new Exception("[Vulkan] Failed to create sync objects!");
        }

        for (int i = 0; i < _renderFinishedSemaphores.Length; i++)
            if (_vk!.CreateSemaphore(_device, semInfo, null, out _renderFinishedSemaphores[i]) != Result.Success)
                throw new Exception("[Vulkan] Failed to create render finished semaphore!");

        Log.Info("[Vulkan] Sync objects created.");
    }

    #endregion

    #region Swapchain Recreation

    private void CleanupSwapchain()
    {
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
        CreateFramebuffers();
        CreateCommandBuffers();
        _imagesInFlight = new Fence[_swapChainImages!.Length];
    }

    #endregion

    public void Dispose()
    {
        _vk!.DeviceWaitIdle(_device);
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
