using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Silk.NET.Core;
using Silk.NET.Core.Native;
using Silk.NET.Input;
using Silk.NET.Maths;
using Silk.NET.Vulkan;
using Silk.NET.Vulkan.Extensions.EXT;
using Silk.NET.Vulkan.Extensions.KHR;
using Silk.NET.Windowing;
using StbImageSharp;
using Buffer = Silk.NET.Vulkan.Buffer;
using Semaphore = Silk.NET.Vulkan.Semaphore;

namespace SilkVulkanExamples.Vulkan;

public unsafe abstract class ExampleBase : IExample
{
    public string Name => GetType().Name.Replace("Example", "");

    private protected IWindow Window = null!;
    private protected IInputContext InputContext = null!;
    internal Vk Vk = null!;
    private protected Instance Instance;
    private protected PhysicalDevice PhysicalDevice;
    internal Device Device;
    private protected Queue GraphicsQueue;
    private protected Queue PresentQueue;
    private protected KhrSurface KhrSurface = null!;
    private protected SurfaceKHR Surface;
    private protected KhrSwapchain KhrSwapChain = null!;
    private protected SwapchainKHR SwapChain;
    private protected Image[] SwapChainImages = [];
    private protected Format SwapChainImageFormat;
    private protected Extent2D SwapChainExtent;
    private protected ImageView[] SwapChainImageViews = [];
    private protected RenderPass RenderPass;
    private protected Framebuffer[] SwapChainFramebuffers = [];
    private protected CommandPool CommandPool;
    private protected CommandBuffer[] CommandBuffers = [];

    // Depth buffer
    private protected Image DepthImage;
    private protected DeviceMemory DepthImageMemory;
    private protected ImageView DepthImageView;

    // Sync objects
    protected const int MaxFramesInFlight = 2;
    private protected Semaphore[] ImageAvailableSemaphores = [];
    private protected Semaphore[] RenderFinishedSemaphores = [];
    private protected Fence[] InFlightFences = [];
    private protected Fence[] ImagesInFlight = [];
    private protected int CurrentFrame;
    private protected bool FramebufferResized;

    // Validation layers
    private protected bool EnableValidationLayers = true;
    private readonly string[] _validationLayers = ["VK_LAYER_KHRONOS_validation"];
    private ExtDebugUtils? _debugUtils;
    private DebugUtilsMessengerEXT _debugMessenger;

    private readonly string[] _deviceExtensions = [KhrSwapchain.ExtensionName];
    private bool _disposed;

    // Called by Program.cs
    public void Initialize(IWindow window)
    {
        Window = window;
        Window.Render += DrawFrame;
        Window.Update += delta => OnUpdate(delta);
        Window.FramebufferResize += _ => FramebufferResized = true;
        Window.Initialize();

        if (Window.VkSurface is null)
            throw new InvalidOperationException("Windowing platform doesn't support Vulkan.");

        SetupInput();
        InitVulkan();
        OnLoad();
    }

    public void Run() => Window.Run();

    // Called by Program.cs after the window loop exits.
    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;

        Vk.DeviceWaitIdle(Device);
        OnClose();
        CleanupVulkan();
    }

    private void SetupInput()
    {
        InputContext = Window.CreateInput();
        for (int i = 0; i < InputContext.Keyboards.Count; i++)
        {
            InputContext.Keyboards[i].KeyDown += (kb, key, arg) =>
            {
                if (key == Key.Escape)
                    Window.Close();
                OnKeyDown(kb, key, arg);
            };
        }
        for (int i = 0; i < InputContext.Mice.Count; i++)
        {
            InputContext.Mice[i].MouseMove += OnMouseMove;
            InputContext.Mice[i].Scroll += OnMouseWheel;
        }
    }

    protected virtual void OnLoad() { }
    protected virtual void OnUpdate(double delta) { }
    protected virtual void OnRender(CommandBuffer cmd, uint imageIndex, double delta) { }
    protected virtual void OnClose() { }
    protected virtual void OnResize() { }
    protected virtual void OnKeyDown(IKeyboard kb, Key key, int arg) { }
    protected virtual void OnMouseMove(IMouse mouse, Vector2 pos) { }
    protected virtual void OnMouseWheel(IMouse mouse, ScrollWheel scroll) { }

    #region Vulkan Initialization

    protected virtual void InitVulkan()
    {
        CreateInstance(Name, "Mainframe Engine");
        SetupDebugMessenger();
        CreateSurface();
        PickPhysicalDevice();
        CreateLogicalDevice();
        CreateSwapchain();
        CreateImageViews();
        CreateCommandPool();
        CreateDepthResources();
        CreateRenderPass();
        CreateFramebuffers();
        CreateCommandBuffers();
        CreateSyncObjects();
    }

    private void CreateInstance(string appName, string engineName)
    {
        Vk = Vk.GetApi();

        if (EnableValidationLayers && !CheckValidationLayerSupport())
        {
            Log.Error("Validation layers requested, but not available!");
            EnableValidationLayers = false;
        }

        var appInfo = new ApplicationInfo
        {
            SType = StructureType.ApplicationInfo,
            PApplicationName = (byte*)Marshal.StringToHGlobalAnsi(appName),
            ApplicationVersion = new Version32(1, 0, 0),
            PEngineName = (byte*)Marshal.StringToHGlobalAnsi(engineName),
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
        createInfo.EnabledLayerCount = 0;

        if (EnableValidationLayers)
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

        if (Vk.CreateInstance(in createInfo, null, out Instance) != Result.Success)
            throw new InvalidOperationException("failed to create instance!");

        Marshal.FreeHGlobal((IntPtr)appInfo.PApplicationName);
        Marshal.FreeHGlobal((IntPtr)appInfo.PEngineName);
        SilkMarshal.Free((nint)createInfo.PpEnabledExtensionNames);

        if (EnableValidationLayers)
            SilkMarshal.Free((nint)createInfo.PpEnabledLayerNames);
    }

    private string[] GetRequiredExtensions()
    {
        var glfwExtensions = Window.VkSurface!.GetRequiredExtensions(out var glfwExtensionCount);
        var extensions = SilkMarshal.PtrToStringArray((nint)glfwExtensions, (int)glfwExtensionCount);

        if (EnableValidationLayers)
            return extensions.Append(ExtDebugUtils.ExtensionName).ToArray();

        return extensions;
    }

    private static void PopulateDebugMessengerCreateInfo(ref DebugUtilsMessengerCreateInfoEXT createInfo)
    {
        createInfo.SType = StructureType.DebugUtilsMessengerCreateInfoExt;
        createInfo.MessageSeverity = DebugUtilsMessageSeverityFlagsEXT.VerboseBitExt |
                                     DebugUtilsMessageSeverityFlagsEXT.WarningBitExt |
                                     DebugUtilsMessageSeverityFlagsEXT.ErrorBitExt;
        createInfo.MessageType = DebugUtilsMessageTypeFlagsEXT.GeneralBitExt |
                                 DebugUtilsMessageTypeFlagsEXT.PerformanceBitExt |
                                 DebugUtilsMessageTypeFlagsEXT.ValidationBitExt;
        createInfo.PfnUserCallback = (DebugUtilsMessengerCallbackFunctionEXT)DebugCallback;
    }

    private void SetupDebugMessenger()
    {
        if (!EnableValidationLayers) return;

        if (!Vk.TryGetInstanceExtension(Instance, out _debugUtils))
            return;

        var createInfo = new DebugUtilsMessengerCreateInfoEXT();
        PopulateDebugMessengerCreateInfo(ref createInfo);

        if (_debugUtils!.CreateDebugUtilsMessenger(Instance, in createInfo, null, out _debugMessenger) != Result.Success)
            throw new InvalidOperationException("failed to set up debug messenger!");
    }

    private bool CheckValidationLayerSupport()
    {
        var layerCount = 0u;
        Vk.EnumerateInstanceLayerProperties(ref layerCount, null);
        var availableLayers = new LayerProperties[layerCount];
        fixed (LayerProperties* availableLayersPtr = availableLayers)
            Vk.EnumerateInstanceLayerProperties(ref layerCount, availableLayersPtr);

        var availableLayerNames = availableLayers
            .Select(layer => Marshal.PtrToStringAnsi((IntPtr)layer.LayerName))
            .ToHashSet();

        return _validationLayers.All(availableLayerNames.Contains);
    }

    private static uint DebugCallback(
        DebugUtilsMessageSeverityFlagsEXT messageSeverity,
        DebugUtilsMessageTypeFlagsEXT messageTypes,
        DebugUtilsMessengerCallbackDataEXT* pCallbackData,
        void* pUserData)
    {
        Log.Print($"[Vulkan] validation layer: {Marshal.PtrToStringAnsi((nint)pCallbackData->PMessage)}");
        return Vk.False;
    }

    private void CreateSurface()
    {
        if (!Vk.TryGetInstanceExtension<KhrSurface>(Instance, out KhrSurface))
            throw new NotSupportedException("KHR_surface extension not found.");

        Surface = Window.VkSurface!.Create<AllocationCallbacks>(Instance.ToHandle(), null).ToSurface();
    }

    private void PickPhysicalDevice()
    {
        var devices = Vk.GetPhysicalDevices(Instance);

        foreach (var d in devices)
        {
            if (IsDeviceSuitable(d))
            {
                PhysicalDevice = d;
                break;
            }
        }

        if (PhysicalDevice.Handle == 0)
            throw new InvalidOperationException("failed to find a suitable GPU!");
    }

    private bool IsDeviceSuitable(PhysicalDevice device)
    {
        var indices = FindQueueFamilies(device);
        bool extensionsSupported = CheckDeviceExtensionSupport(device);
        bool swapChainAdequate = false;
        if (extensionsSupported)
        {
            var swapChainSupport = QuerySwapChainSupport(device);
            swapChainAdequate = swapChainSupport.Formats.Length > 0 && swapChainSupport.PresentModes.Length > 0;
        }
        return indices.IsComplete() && extensionsSupported && swapChainAdequate;
    }

    private bool CheckDeviceExtensionSupport(PhysicalDevice device)
    {
        var extensionCount = 0u;
        Vk.EnumerateDeviceExtensionProperties(device, (byte*)null, ref extensionCount, null);
        var availableExtensions = new ExtensionProperties[extensionCount];
        fixed (ExtensionProperties* ext = availableExtensions)
            Vk.EnumerateDeviceExtensionProperties(device, (byte*)null, ref extensionCount, ext);

        var availableExtensionNames = availableExtensions
            .Select(e => Marshal.PtrToStringAnsi((IntPtr)e.ExtensionName))
            .ToHashSet();

        return _deviceExtensions.All(availableExtensionNames.Contains);
    }

    protected QueueFamilyIndices FindQueueFamilies(PhysicalDevice device)
    {
        var indices = new QueueFamilyIndices();

        var queueFamilyCount = 0u;
        Vk.GetPhysicalDeviceQueueFamilyProperties(device, ref queueFamilyCount, null);

        var queueFamilies = new QueueFamilyProperties[queueFamilyCount];
        fixed (QueueFamilyProperties* queueFamiliesPtr = queueFamilies)
            Vk.GetPhysicalDeviceQueueFamilyProperties(device, ref queueFamilyCount, queueFamiliesPtr);

        var i = 0u;
        foreach (var queueFamily in queueFamilies)
        {
            if (queueFamily.QueueFlags.HasFlag(QueueFlags.GraphicsBit))
                indices.GraphicsFamily = i;

            KhrSurface.GetPhysicalDeviceSurfaceSupport(device, i, Surface, out var presentSupport);

            if (presentSupport)
                indices.PresentFamily = i;

            if (indices.IsComplete())
                break;

            i++;
        }

        return indices;
    }

    private void CreateLogicalDevice()
    {
        var indices = FindQueueFamilies(PhysicalDevice);

        var uniqueQueueFamilies = new[] { indices.GraphicsFamily!.Value, indices.PresentFamily!.Value };
        uniqueQueueFamilies = uniqueQueueFamilies.Distinct().ToArray();

        using var mem = GlobalMemory.Allocate(uniqueQueueFamilies.Length * sizeof(DeviceQueueCreateInfo));
        var queueCreateInfos = (DeviceQueueCreateInfo*)Unsafe.AsPointer(ref mem.GetPinnableReference());

        float queuePriority = 1.0f;
        for (int i = 0; i < uniqueQueueFamilies.Length; i++)
        {
            queueCreateInfos[i] = new DeviceQueueCreateInfo
            {
                SType = StructureType.DeviceQueueCreateInfo,
                QueueFamilyIndex = uniqueQueueFamilies[i],
                QueueCount = 1,
                PQueuePriorities = &queuePriority
            };
        }

        var deviceFeatures = new PhysicalDeviceFeatures();
        deviceFeatures.SamplerAnisotropy = true;

        var createInfo = new DeviceCreateInfo
        {
            SType = StructureType.DeviceCreateInfo,
            QueueCreateInfoCount = (uint)uniqueQueueFamilies.Length,
            PQueueCreateInfos = queueCreateInfos,
            PEnabledFeatures = &deviceFeatures,
            EnabledExtensionCount = (uint)_deviceExtensions.Length,
            PpEnabledExtensionNames = (byte**)SilkMarshal.StringArrayToPtr(_deviceExtensions)
        };

        if (EnableValidationLayers)
        {
            createInfo.EnabledLayerCount = (uint)_validationLayers.Length;
            createInfo.PpEnabledLayerNames = (byte**)SilkMarshal.StringArrayToPtr(_validationLayers);
        }
        else
        {
            createInfo.EnabledLayerCount = 0;
        }

        if (Vk.CreateDevice(PhysicalDevice, in createInfo, null, out Device) != Result.Success)
            throw new InvalidOperationException("failed to create logical device!");

        Vk.GetDeviceQueue(Device, indices.GraphicsFamily!.Value, 0, out GraphicsQueue);
        Vk.GetDeviceQueue(Device, indices.PresentFamily!.Value, 0, out PresentQueue);

        if (EnableValidationLayers)
            SilkMarshal.Free((nint)createInfo.PpEnabledLayerNames);

        SilkMarshal.Free((nint)createInfo.PpEnabledExtensionNames);
    }

    private void CreateSwapchain()
    {
        var swapChainSupport = QuerySwapChainSupport(PhysicalDevice);
        var surfaceFormat = ChooseSwapSurfaceFormat(swapChainSupport.Formats);
        var presentMode = ChoosePresentMode(swapChainSupport.PresentModes);
        var extent = ChooseSwapExtent(swapChainSupport.Capabilities);

        var imageCount = swapChainSupport.Capabilities.MinImageCount + 1;
        if (swapChainSupport.Capabilities.MaxImageCount > 0 && imageCount > swapChainSupport.Capabilities.MaxImageCount)
            imageCount = swapChainSupport.Capabilities.MaxImageCount;

        var createInfo = new SwapchainCreateInfoKHR
        {
            SType = StructureType.SwapchainCreateInfoKhr,
            Surface = Surface,
            MinImageCount = imageCount,
            ImageFormat = surfaceFormat.Format,
            ImageColorSpace = surfaceFormat.ColorSpace,
            ImageExtent = extent,
            ImageArrayLayers = 1,
            ImageUsage = ImageUsageFlags.ColorAttachmentBit,
        };

        var indices = FindQueueFamilies(PhysicalDevice);
        var queueFamilyIndices = stackalloc[] { indices.GraphicsFamily!.Value, indices.PresentFamily!.Value };

        if (indices.GraphicsFamily != indices.PresentFamily)
        {
            createInfo = createInfo with
            {
                ImageSharingMode = SharingMode.Concurrent,
                QueueFamilyIndexCount = 2,
                PQueueFamilyIndices = queueFamilyIndices,
            };
        }
        else
        {
            createInfo.ImageSharingMode = SharingMode.Exclusive;
        }

        createInfo = createInfo with
        {
            PreTransform = swapChainSupport.Capabilities.CurrentTransform,
            CompositeAlpha = CompositeAlphaFlagsKHR.OpaqueBitKhr,
            PresentMode = presentMode,
            Clipped = true,
            OldSwapchain = default
        };

        if (!Vk.TryGetDeviceExtension(Instance, Device, out KhrSwapChain))
            throw new NotSupportedException("VK_KHR_swapchain extension not found.");

        if (KhrSwapChain.CreateSwapchain(Device, in createInfo, null, out SwapChain) != Result.Success)
            throw new InvalidOperationException("failed to create swap chain!");

        KhrSwapChain.GetSwapchainImages(Device, SwapChain, ref imageCount, null);
        SwapChainImages = new Image[imageCount];
        fixed (Image* swapChainImagesPtr = SwapChainImages)
            KhrSwapChain.GetSwapchainImages(Device, SwapChain, ref imageCount, swapChainImagesPtr);

        SwapChainImageFormat = surfaceFormat.Format;
        SwapChainExtent = extent;
    }

    private SwapChainSupportDetails QuerySwapChainSupport(PhysicalDevice physicalDevice)
    {
        var details = new SwapChainSupportDetails();
        KhrSurface.GetPhysicalDeviceSurfaceCapabilities(physicalDevice, Surface, out details.Capabilities);

        var formatCount = 0u;
        KhrSurface.GetPhysicalDeviceSurfaceFormats(physicalDevice, Surface, ref formatCount, null);

        if (formatCount != 0)
        {
            details.Formats = new SurfaceFormatKHR[formatCount];
            fixed (SurfaceFormatKHR* formatsPtr = details.Formats)
                KhrSurface.GetPhysicalDeviceSurfaceFormats(physicalDevice, Surface, ref formatCount, formatsPtr);
        }
        else
        {
            details.Formats = [];
        }

        var presentModeCount = 0u;
        KhrSurface.GetPhysicalDeviceSurfacePresentModes(physicalDevice, Surface, ref presentModeCount, null);

        if (presentModeCount != 0)
        {
            details.PresentModes = new PresentModeKHR[presentModeCount];
            fixed (PresentModeKHR* modesPtr = details.PresentModes)
                KhrSurface.GetPhysicalDeviceSurfacePresentModes(physicalDevice, Surface, ref presentModeCount, modesPtr);
        }
        else
        {
            details.PresentModes = [];
        }

        return details;
    }

    private static SurfaceFormatKHR ChooseSwapSurfaceFormat(SurfaceFormatKHR[] availableFormats)
    {
        foreach (var format in availableFormats)
            if (format is { Format: Format.B8G8R8A8Srgb, ColorSpace: ColorSpaceKHR.SpaceSrgbNonlinearKhr })
                return format;
        return availableFormats[0];
    }

    private static PresentModeKHR ChoosePresentMode(IReadOnlyList<PresentModeKHR> availablePresentModes)
    {
        foreach (var mode in availablePresentModes)
            if (mode == PresentModeKHR.MailboxKhr)
                return mode;
        return PresentModeKHR.FifoKhr;
    }

    private Extent2D ChooseSwapExtent(SurfaceCapabilitiesKHR capabilities)
    {
        if (capabilities.CurrentExtent.Width != uint.MaxValue)
            return capabilities.CurrentExtent;

        var framebufferSize = Window.FramebufferSize;
        var actualExtent = new Extent2D
        {
            Width = (uint)framebufferSize.X,
            Height = (uint)framebufferSize.Y
        };

        actualExtent.Width = Math.Clamp(actualExtent.Width, capabilities.MinImageExtent.Width, capabilities.MaxImageExtent.Width);
        actualExtent.Height = Math.Clamp(actualExtent.Height, capabilities.MinImageExtent.Height, capabilities.MaxImageExtent.Height);

        return actualExtent;
    }

    private void CreateImageViews()
    {
        SwapChainImageViews = new ImageView[SwapChainImages.Length];
        for (int i = 0; i < SwapChainImages.Length; i++)
            SwapChainImageViews[i] = CreateImageView(SwapChainImages[i], SwapChainImageFormat, ImageAspectFlags.ColorBit);
    }

    private void CreateDepthResources()
    {
        var depthFormat = FindDepthFormat();

        CreateImage(
            SwapChainExtent.Width, SwapChainExtent.Height, 1,
            depthFormat,
            ImageTiling.Optimal,
            ImageUsageFlags.DepthStencilAttachmentBit,
            MemoryPropertyFlags.DeviceLocalBit,
            out DepthImage, out DepthImageMemory);

        DepthImageView = CreateImageView(DepthImage, depthFormat, ImageAspectFlags.DepthBit);
        TransitionImageLayout(DepthImage, depthFormat, ImageLayout.Undefined, ImageLayout.DepthStencilAttachmentOptimal);
    }

    private Format FindDepthFormat()
    {
        return FindSupportedFormat(
            [Format.D32Sfloat, Format.D32SfloatS8Uint, Format.D24UnormS8Uint],
            ImageTiling.Optimal,
            FormatFeatureFlags.DepthStencilAttachmentBit);
    }

    private Format FindSupportedFormat(Format[] candidates, ImageTiling tiling, FormatFeatureFlags features)
    {
        foreach (var format in candidates)
        {
            Vk.GetPhysicalDeviceFormatProperties(PhysicalDevice, format, out var props);

            if (tiling == ImageTiling.Linear && (props.LinearTilingFeatures & features) == features)
                return format;
            if (tiling == ImageTiling.Optimal && (props.OptimalTilingFeatures & features) == features)
                return format;
        }

        throw new InvalidOperationException("failed to find supported format!");
    }

    protected static bool HasStencilComponent(Format format)
    {
        return format == Format.D32SfloatS8Uint || format == Format.D24UnormS8Uint;
    }

    private void CreateRenderPass()
    {
        var colorAttachment = new AttachmentDescription
        {
            Format = SwapChainImageFormat,
            Samples = SampleCountFlags.Count1Bit,
            LoadOp = AttachmentLoadOp.Clear,
            StoreOp = AttachmentStoreOp.Store,
            StencilLoadOp = AttachmentLoadOp.DontCare,
            StencilStoreOp = AttachmentStoreOp.DontCare,
            InitialLayout = ImageLayout.Undefined,
            FinalLayout = ImageLayout.PresentSrcKhr,
        };

        var depthAttachment = new AttachmentDescription
        {
            Format = FindDepthFormat(),
            Samples = SampleCountFlags.Count1Bit,
            LoadOp = AttachmentLoadOp.Clear,
            StoreOp = AttachmentStoreOp.DontCare,
            StencilLoadOp = AttachmentLoadOp.DontCare,
            StencilStoreOp = AttachmentStoreOp.DontCare,
            InitialLayout = ImageLayout.Undefined,
            FinalLayout = ImageLayout.DepthStencilAttachmentOptimal,
        };

        var colorAttachmentRef = new AttachmentReference
        {
            Attachment = 0,
            Layout = ImageLayout.ColorAttachmentOptimal,
        };

        var depthAttachmentRef = new AttachmentReference
        {
            Attachment = 1,
            Layout = ImageLayout.DepthStencilAttachmentOptimal,
        };

        var subpass = new SubpassDescription
        {
            PipelineBindPoint = PipelineBindPoint.Graphics,
            ColorAttachmentCount = 1,
            PColorAttachments = &colorAttachmentRef,
            PDepthStencilAttachment = &depthAttachmentRef,
        };

        var dependency = new SubpassDependency
        {
            SrcSubpass = Vk.SubpassExternal,
            DstSubpass = 0,
            SrcStageMask = PipelineStageFlags.ColorAttachmentOutputBit | PipelineStageFlags.EarlyFragmentTestsBit,
            SrcAccessMask = 0,
            DstStageMask = PipelineStageFlags.ColorAttachmentOutputBit | PipelineStageFlags.EarlyFragmentTestsBit,
            DstAccessMask = AccessFlags.ColorAttachmentWriteBit | AccessFlags.DepthStencilAttachmentWriteBit,
        };

        var attachments = stackalloc AttachmentDescription[] { colorAttachment, depthAttachment };

        var renderPassInfo = new RenderPassCreateInfo
        {
            SType = StructureType.RenderPassCreateInfo,
            AttachmentCount = 2,
            PAttachments = attachments,
            SubpassCount = 1,
            PSubpasses = &subpass,
            DependencyCount = 1,
            PDependencies = &dependency,
        };

        if (Vk.CreateRenderPass(Device, in renderPassInfo, null, out RenderPass) != Result.Success)
            throw new InvalidOperationException("failed to create render pass!");
    }

    private void CreateFramebuffers()
    {
        SwapChainFramebuffers = new Framebuffer[SwapChainImageViews.Length];
        var attachments = stackalloc ImageView[2];
        for (int i = 0; i < SwapChainImageViews.Length; i++)
        {
            attachments[0] = SwapChainImageViews[i];
            attachments[1] = DepthImageView;

            var framebufferInfo = new FramebufferCreateInfo
            {
                SType = StructureType.FramebufferCreateInfo,
                RenderPass = RenderPass,
                AttachmentCount = 2,
                PAttachments = attachments,
                Width = SwapChainExtent.Width,
                Height = SwapChainExtent.Height,
                Layers = 1,
            };

            if (Vk.CreateFramebuffer(Device, in framebufferInfo, null, out SwapChainFramebuffers[i]) != Result.Success)
                throw new InvalidOperationException("failed to create framebuffer!");
        }
    }

    private void CreateCommandPool()
    {
        var queueFamilyIndices = FindQueueFamilies(PhysicalDevice);
        var poolInfo = new CommandPoolCreateInfo
        {
            SType = StructureType.CommandPoolCreateInfo,
            QueueFamilyIndex = queueFamilyIndices.GraphicsFamily!.Value,
            Flags = CommandPoolCreateFlags.ResetCommandBufferBit,
        };

        if (Vk.CreateCommandPool(Device, in poolInfo, null, out CommandPool) != Result.Success)
            throw new InvalidOperationException("failed to create command pool!");
    }

    private void CreateCommandBuffers()
    {
        CommandBuffers = new CommandBuffer[SwapChainFramebuffers.Length];
        var allocInfo = new CommandBufferAllocateInfo
        {
            SType = StructureType.CommandBufferAllocateInfo,
            CommandPool = CommandPool,
            Level = CommandBufferLevel.Primary,
            CommandBufferCount = (uint)CommandBuffers.Length,
        };

        fixed (CommandBuffer* commandBuffersPtr = CommandBuffers)
            if (Vk.AllocateCommandBuffers(Device, in allocInfo, commandBuffersPtr) != Result.Success)
                throw new InvalidOperationException("failed to allocate command buffers!");
    }

    private void CreateSyncObjects()
    {
        ImageAvailableSemaphores = new Semaphore[MaxFramesInFlight];
        RenderFinishedSemaphores = new Semaphore[SwapChainImages.Length];
        InFlightFences = new Fence[MaxFramesInFlight];
        ImagesInFlight = new Fence[SwapChainImages.Length];

        var semaphoreInfo = new SemaphoreCreateInfo { SType = StructureType.SemaphoreCreateInfo };
        var fenceInfo = new FenceCreateInfo
        {
            SType = StructureType.FenceCreateInfo,
            Flags = FenceCreateFlags.SignaledBit,
        };

        for (int i = 0; i < MaxFramesInFlight; i++)
        {
            if (Vk.CreateSemaphore(Device, in semaphoreInfo, null, out ImageAvailableSemaphores[i]) != Result.Success ||
                Vk.CreateFence(Device, in fenceInfo, null, out InFlightFences[i]) != Result.Success)
                throw new InvalidOperationException("failed to create synchronization objects!");
        }

        for (int i = 0; i < RenderFinishedSemaphores.Length; i++)
        {
            if (Vk.CreateSemaphore(Device, in semaphoreInfo, null, out RenderFinishedSemaphores[i]) != Result.Success)
                throw new InvalidOperationException("failed to create render finished semaphore!");
        }
    }

    #endregion

    #region Draw Frame

    private void DrawFrame(double delta)
    {
        Vk.WaitForFences(Device, 1, in InFlightFences[CurrentFrame], true, ulong.MaxValue);

        uint imageIndex;
        var result = KhrSwapChain.AcquireNextImage(Device, SwapChain, ulong.MaxValue,
            ImageAvailableSemaphores[CurrentFrame], default, &imageIndex);

        if (result == Result.ErrorOutOfDateKhr)
        {
            RecreateSwapchain();
            return;
        }
        else if (result != Result.Success && result != Result.SuboptimalKhr)
        {
            throw new InvalidOperationException("failed to acquire swap chain image!");
        }

        if (ImagesInFlight[imageIndex].Handle != 0)
            Vk.WaitForFences(Device, 1, in ImagesInFlight[imageIndex], true, ulong.MaxValue);

        ImagesInFlight[imageIndex] = InFlightFences[CurrentFrame];

        // Re-record command buffer for this frame
        RecordCommandBuffer(CommandBuffers[imageIndex], imageIndex, delta);

        var waitSemaphore = ImageAvailableSemaphores[CurrentFrame];
        var waitStage = PipelineStageFlags.ColorAttachmentOutputBit;
        var signalSemaphore = RenderFinishedSemaphores[imageIndex];
        var commandBuffer = CommandBuffers[imageIndex];

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

        Vk.ResetFences(Device, 1, in InFlightFences[CurrentFrame]);

        if (Vk.QueueSubmit(GraphicsQueue, 1, in submitInfo, InFlightFences[CurrentFrame]) != Result.Success)
            throw new InvalidOperationException("failed to submit draw command buffer!");

        var swapChainHandle = SwapChain;
        var presentInfo = new PresentInfoKHR
        {
            SType = StructureType.PresentInfoKhr,
            WaitSemaphoreCount = 1,
            PWaitSemaphores = &signalSemaphore,
            SwapchainCount = 1,
            PSwapchains = &swapChainHandle,
            PImageIndices = &imageIndex,
        };

        result = KhrSwapChain.QueuePresent(PresentQueue, in presentInfo);

        if (result == Result.ErrorOutOfDateKhr || result == Result.SuboptimalKhr || FramebufferResized)
        {
            FramebufferResized = false;
            RecreateSwapchain();
        }
        else if (result != Result.Success)
        {
            throw new InvalidOperationException("failed to present swap chain image!");
        }

        CurrentFrame = (CurrentFrame + 1) % MaxFramesInFlight;
    }

    private void RecordCommandBuffer(CommandBuffer commandBuffer, uint imageIndex, double delta)
    {
        Vk.ResetCommandBuffer(commandBuffer, 0);

        var beginInfo = new CommandBufferBeginInfo { SType = StructureType.CommandBufferBeginInfo };
        if (Vk.BeginCommandBuffer(commandBuffer, in beginInfo) != Result.Success)
            throw new InvalidOperationException("failed to begin recording command buffer!");

        // Two clear values: color + depth
        var clearValues = stackalloc ClearValue[]
        {
            new ClearValue { Color = new() { Float32_0 = 0.18f, Float32_1 = 0.31f, Float32_2 = 0.31f, Float32_3 = 1.0f } },
            new ClearValue { DepthStencil = new() { Depth = 1.0f, Stencil = 0 } }
        };

        var renderPassInfo = new RenderPassBeginInfo
        {
            SType = StructureType.RenderPassBeginInfo,
            RenderPass = RenderPass,
            Framebuffer = SwapChainFramebuffers[imageIndex],
            RenderArea = { Offset = default, Extent = SwapChainExtent },
            ClearValueCount = 2,
            PClearValues = clearValues,
        };

        Vk.CmdBeginRenderPass(commandBuffer, &renderPassInfo, SubpassContents.Inline);

        // Set dynamic viewport and scissor
        var viewport = new Viewport
        {
            X = 0,
            Y = 0,
            Width = SwapChainExtent.Width,
            Height = SwapChainExtent.Height,
            MinDepth = 0,
            MaxDepth = 1,
        };
        Vk.CmdSetViewport(commandBuffer, 0, 1, &viewport);

        var scissor = new Rect2D { Offset = default, Extent = SwapChainExtent };
        Vk.CmdSetScissor(commandBuffer, 0, 1, &scissor);

        OnRender(commandBuffer, imageIndex, delta);

        Vk.CmdEndRenderPass(commandBuffer);

        if (Vk.EndCommandBuffer(commandBuffer) != Result.Success)
            throw new InvalidOperationException("failed to record command buffer!");
    }

    #endregion

    #region Swapchain Recreation

    protected virtual void RecreateSwapchain()
    {
        var size = Window.FramebufferSize;
        while (size.X == 0 || size.Y == 0)
        {
            size = Window.FramebufferSize;
            Window.DoEvents();
        }

        Vk.DeviceWaitIdle(Device);
        CleanupSwapchain();

        CreateSwapchain();
        CreateImageViews();
        CreateDepthResources();
        CreateFramebuffers();
        CreateCommandBuffers();
        ImagesInFlight = new Fence[SwapChainImages.Length];

        OnResize();
    }

    protected virtual void CleanupSwapchain()
    {
        // Destroy depth resources
        Vk.DestroyImageView(Device, DepthImageView, null);
        Vk.DestroyImage(Device, DepthImage, null);
        Vk.FreeMemory(Device, DepthImageMemory, null);

        foreach (var framebuffer in SwapChainFramebuffers)
            Vk.DestroyFramebuffer(Device, framebuffer, null);

        fixed (CommandBuffer* commandBuffersPtr = CommandBuffers)
            Vk.FreeCommandBuffers(Device, CommandPool, (uint)CommandBuffers.Length, commandBuffersPtr);

        foreach (var imageView in SwapChainImageViews)
            Vk.DestroyImageView(Device, imageView, null);

        KhrSwapChain.DestroySwapchain(Device, SwapChain, null);
    }

    #endregion

    #region Helper Methods

    protected uint FindMemoryType(uint typeFilter, MemoryPropertyFlags properties)
    {
        Vk.GetPhysicalDeviceMemoryProperties(PhysicalDevice, out var memProperties);

        for (int i = 0; i < memProperties.MemoryTypeCount; i++)
        {
            if ((typeFilter & (1u << i)) != 0 &&
                (memProperties.MemoryTypes[i].PropertyFlags & properties) == properties)
            {
                return (uint)i;
            }
        }

        throw new InvalidOperationException("failed to find suitable memory type!");
    }

    public void CreateBuffer(ulong size, BufferUsageFlags usage, MemoryPropertyFlags props,
        out Buffer buffer, out DeviceMemory memory)
    {
        var bufferInfo = new BufferCreateInfo
        {
            SType = StructureType.BufferCreateInfo,
            Size = size,
            Usage = usage,
            SharingMode = SharingMode.Exclusive,
        };

        fixed (Buffer* bufferPtr = &buffer)
            if (Vk.CreateBuffer(Device, in bufferInfo, null, bufferPtr) != Result.Success)
                throw new InvalidOperationException("failed to create buffer!");

        Vk.GetBufferMemoryRequirements(Device, buffer, out var memRequirements);

        var allocInfo = new MemoryAllocateInfo
        {
            SType = StructureType.MemoryAllocateInfo,
            AllocationSize = memRequirements.Size,
            MemoryTypeIndex = FindMemoryType(memRequirements.MemoryTypeBits, props),
        };

        fixed (DeviceMemory* memoryPtr = &memory)
            if (Vk.AllocateMemory(Device, in allocInfo, null, memoryPtr) != Result.Success)
                throw new InvalidOperationException("failed to allocate buffer memory!");

        Vk.BindBufferMemory(Device, buffer, memory, 0);
    }

    public void CopyBuffer(Buffer srcBuffer, Buffer dstBuffer, ulong size)
    {
        var commandBuffer = BeginSingleTimeCommands();

        var copyRegion = new BufferCopy { Size = size };
        Vk.CmdCopyBuffer(commandBuffer, srcBuffer, dstBuffer, 1, &copyRegion);

        EndSingleTimeCommands(commandBuffer);
    }

    protected CommandBuffer BeginSingleTimeCommands()
    {
        var allocInfo = new CommandBufferAllocateInfo
        {
            SType = StructureType.CommandBufferAllocateInfo,
            Level = CommandBufferLevel.Primary,
            CommandPool = CommandPool,
            CommandBufferCount = 1,
        };

        Vk.AllocateCommandBuffers(Device, in allocInfo, out var commandBuffer);

        var beginInfo = new CommandBufferBeginInfo
        {
            SType = StructureType.CommandBufferBeginInfo,
            Flags = CommandBufferUsageFlags.OneTimeSubmitBit,
        };

        Vk.BeginCommandBuffer(commandBuffer, in beginInfo);

        return commandBuffer;
    }

    protected void EndSingleTimeCommands(CommandBuffer commandBuffer)
    {
        Vk.EndCommandBuffer(commandBuffer);

        var submitInfo = new SubmitInfo
        {
            SType = StructureType.SubmitInfo,
            CommandBufferCount = 1,
            PCommandBuffers = &commandBuffer,
        };

        Vk.QueueSubmit(GraphicsQueue, 1, in submitInfo, default);
        Vk.QueueWaitIdle(GraphicsQueue);

        Vk.FreeCommandBuffers(Device, CommandPool, 1, &commandBuffer);
    }

    protected void CreateImage(uint width, uint height, uint mipLevels, Format format,
        ImageTiling tiling, ImageUsageFlags usage, MemoryPropertyFlags properties,
        out Image image, out DeviceMemory imageMemory)
    {
        var imageInfo = new ImageCreateInfo
        {
            SType = StructureType.ImageCreateInfo,
            ImageType = ImageType.Type2D,
            Extent = { Width = width, Height = height, Depth = 1 },
            MipLevels = mipLevels,
            ArrayLayers = 1,
            Format = format,
            Tiling = tiling,
            InitialLayout = ImageLayout.Undefined,
            Usage = usage,
            Samples = SampleCountFlags.Count1Bit,
            SharingMode = SharingMode.Exclusive,
        };

        fixed (Image* imagePtr = &image)
            if (Vk.CreateImage(Device, in imageInfo, null, imagePtr) != Result.Success)
                throw new InvalidOperationException("failed to create image!");

        Vk.GetImageMemoryRequirements(Device, image, out var memRequirements);

        var allocInfo = new MemoryAllocateInfo
        {
            SType = StructureType.MemoryAllocateInfo,
            AllocationSize = memRequirements.Size,
            MemoryTypeIndex = FindMemoryType(memRequirements.MemoryTypeBits, properties),
        };

        fixed (DeviceMemory* imageMemoryPtr = &imageMemory)
            if (Vk.AllocateMemory(Device, in allocInfo, null, imageMemoryPtr) != Result.Success)
                throw new InvalidOperationException("failed to allocate image memory!");

        Vk.BindImageMemory(Device, image, imageMemory, 0);
    }

    protected void TransitionImageLayout(Image image, Format format, ImageLayout oldLayout, ImageLayout newLayout, uint mipLevels = 1)
    {
        var commandBuffer = BeginSingleTimeCommands();

        var barrier = new ImageMemoryBarrier
        {
            SType = StructureType.ImageMemoryBarrier,
            OldLayout = oldLayout,
            NewLayout = newLayout,
            SrcQueueFamilyIndex = Vk.QueueFamilyIgnored,
            DstQueueFamilyIndex = Vk.QueueFamilyIgnored,
            Image = image,
            SubresourceRange =
            {
                AspectMask = newLayout == ImageLayout.DepthStencilAttachmentOptimal
                    ? (HasStencilComponent(format)
                        ? ImageAspectFlags.DepthBit | ImageAspectFlags.StencilBit
                        : ImageAspectFlags.DepthBit)
                    : ImageAspectFlags.ColorBit,
                BaseMipLevel = 0,
                LevelCount = mipLevels,
                BaseArrayLayer = 0,
                LayerCount = 1,
            }
        };

        PipelineStageFlags sourceStage;
        PipelineStageFlags destinationStage;

        if (oldLayout == ImageLayout.Undefined && newLayout == ImageLayout.TransferDstOptimal)
        {
            barrier.SrcAccessMask = 0;
            barrier.DstAccessMask = AccessFlags.TransferWriteBit;
            sourceStage = PipelineStageFlags.TopOfPipeBit;
            destinationStage = PipelineStageFlags.TransferBit;
        }
        else if (oldLayout == ImageLayout.TransferDstOptimal && newLayout == ImageLayout.ShaderReadOnlyOptimal)
        {
            barrier.SrcAccessMask = AccessFlags.TransferWriteBit;
            barrier.DstAccessMask = AccessFlags.ShaderReadBit;
            sourceStage = PipelineStageFlags.TransferBit;
            destinationStage = PipelineStageFlags.FragmentShaderBit;
        }
        else if (oldLayout == ImageLayout.Undefined && newLayout == ImageLayout.DepthStencilAttachmentOptimal)
        {
            barrier.SrcAccessMask = 0;
            barrier.DstAccessMask = AccessFlags.DepthStencilAttachmentReadBit | AccessFlags.DepthStencilAttachmentWriteBit;
            sourceStage = PipelineStageFlags.TopOfPipeBit;
            destinationStage = PipelineStageFlags.EarlyFragmentTestsBit;
        }
        else
        {
            throw new InvalidOperationException("unsupported layout transition!");
        }

        Vk.CmdPipelineBarrier(commandBuffer,
            sourceStage, destinationStage,
            0, 0, null, 0, null, 1, &barrier);

        EndSingleTimeCommands(commandBuffer);
    }

    protected void CopyBufferToImage(Buffer buffer, Image image, uint width, uint height)
    {
        var commandBuffer = BeginSingleTimeCommands();

        var region = new BufferImageCopy
        {
            BufferOffset = 0,
            BufferRowLength = 0,
            BufferImageHeight = 0,
            ImageSubresource =
            {
                AspectMask = ImageAspectFlags.ColorBit,
                MipLevel = 0,
                BaseArrayLayer = 0,
                LayerCount = 1,
            },
            ImageOffset = new Offset3D { X = 0, Y = 0, Z = 0 },
            ImageExtent = new Extent3D { Width = width, Height = height, Depth = 1 },
        };

        Vk.CmdCopyBufferToImage(commandBuffer, buffer, image, ImageLayout.TransferDstOptimal, 1, &region);

        EndSingleTimeCommands(commandBuffer);
    }

    protected ImageView CreateImageView(Image image, Format format, ImageAspectFlags aspectFlags, uint mipLevels = 1)
    {
        var viewInfo = new ImageViewCreateInfo
        {
            SType = StructureType.ImageViewCreateInfo,
            Image = image,
            ViewType = ImageViewType.Type2D,
            Format = format,
            SubresourceRange =
            {
                AspectMask = aspectFlags,
                BaseMipLevel = 0,
                LevelCount = mipLevels,
                BaseArrayLayer = 0,
                LayerCount = 1,
            }
        };

        if (Vk.CreateImageView(Device, in viewInfo, null, out var imageView) != Result.Success)
            throw new InvalidOperationException("failed to create image view!");

        return imageView;
    }

    public (Image image, DeviceMemory memory, ImageView view, Sampler sampler) CreateTextureFromFile(string path)
    {
        using var stream = File.OpenRead(path);
        var imageResult = ImageResult.FromStream(stream, ColorComponents.RedGreenBlueAlpha);
        var width = (uint)imageResult.Width;
        var height = (uint)imageResult.Height;
        var pixels = imageResult.Data;

        ulong imageSize = (ulong)(width * height * 4);

        CreateBuffer(imageSize,
            BufferUsageFlags.TransferSrcBit,
            MemoryPropertyFlags.HostVisibleBit | MemoryPropertyFlags.HostCoherentBit,
            out var stagingBuffer, out var stagingMemory);

        void* data;
        Vk.MapMemory(Device, stagingMemory, 0, imageSize, 0, &data);
        fixed (byte* pixelsPtr = pixels)
            System.Buffer.MemoryCopy(pixelsPtr, data, imageSize, imageSize);
        Vk.UnmapMemory(Device, stagingMemory);

        CreateImage(width, height, 1, Format.R8G8B8A8Srgb,
            ImageTiling.Optimal,
            ImageUsageFlags.TransferDstBit | ImageUsageFlags.SampledBit,
            MemoryPropertyFlags.DeviceLocalBit,
            out var image, out var imageMemory);

        TransitionImageLayout(image, Format.R8G8B8A8Srgb, ImageLayout.Undefined, ImageLayout.TransferDstOptimal);
        CopyBufferToImage(stagingBuffer, image, width, height);
        TransitionImageLayout(image, Format.R8G8B8A8Srgb, ImageLayout.TransferDstOptimal, ImageLayout.ShaderReadOnlyOptimal);

        Vk.DestroyBuffer(Device, stagingBuffer, null);
        Vk.FreeMemory(Device, stagingMemory, null);

        var view = CreateImageView(image, Format.R8G8B8A8Srgb, ImageAspectFlags.ColorBit);

        Vk.GetPhysicalDeviceProperties(PhysicalDevice, out var physicalDeviceProperties);

        var samplerInfo = new SamplerCreateInfo
        {
            SType = StructureType.SamplerCreateInfo,
            MagFilter = Filter.Linear,
            MinFilter = Filter.Linear,
            AddressModeU = SamplerAddressMode.Repeat,
            AddressModeV = SamplerAddressMode.Repeat,
            AddressModeW = SamplerAddressMode.Repeat,
            AnisotropyEnable = true,
            MaxAnisotropy = physicalDeviceProperties.Limits.MaxSamplerAnisotropy,
            BorderColor = BorderColor.IntOpaqueBlack,
            UnnormalizedCoordinates = false,
            CompareEnable = false,
            CompareOp = CompareOp.Always,
            MipmapMode = SamplerMipmapMode.Linear,
        };

        if (Vk.CreateSampler(Device, in samplerInfo, null, out var sampler) != Result.Success)
            throw new InvalidOperationException("failed to create texture sampler!");

        return (image, imageMemory, view, sampler);
    }

    protected ShaderModule CreateShaderModule(string spvPath)
    {
        return CreateShaderModule(File.ReadAllBytes(spvPath));
    }

    protected ShaderModule CreateShaderModule(byte[] code)
    {
        var createInfo = new ShaderModuleCreateInfo
        {
            SType = StructureType.ShaderModuleCreateInfo,
            CodeSize = (nuint)code.Length,
        };

        fixed (byte* codePtr = code)
        {
            createInfo.PCode = (uint*)codePtr;
            if (Vk.CreateShaderModule(Device, in createInfo, null, out var shaderModule) != Result.Success)
                throw new InvalidOperationException("failed to create shader module!");
            return shaderModule;
        }
    }

    protected static PipelineShaderStageCreateInfo MakeShaderStage(ShaderStageFlags stage, ShaderModule module, string entryPoint = "main")
    {
        return new PipelineShaderStageCreateInfo
        {
            SType = StructureType.PipelineShaderStageCreateInfo,
            Stage = stage,
            Module = module,
            PName = (byte*)SilkMarshal.StringToPtr(entryPoint),
        };
    }

    #endregion

    #region Cleanup

    protected virtual void CleanupVulkan()
    {
        CleanupSwapchain();

        Vk.DestroyRenderPass(Device, RenderPass, null);

        for (int i = 0; i < MaxFramesInFlight; i++)
        {
            Vk.DestroySemaphore(Device, ImageAvailableSemaphores[i], null);
            Vk.DestroyFence(Device, InFlightFences[i], null);
        }

        for (int i = 0; i < RenderFinishedSemaphores.Length; i++)
            Vk.DestroySemaphore(Device, RenderFinishedSemaphores[i], null);

        Vk.DestroyCommandPool(Device, CommandPool, null);
        Vk.DestroyDevice(Device, null);

        if (EnableValidationLayers)
            _debugUtils?.DestroyDebugUtilsMessenger(Instance, _debugMessenger, null);

        KhrSurface?.DestroySurface(Instance, Surface, null);
        Vk.DestroyInstance(Instance, null);
        Vk.Dispose();

        InputContext?.Dispose();
    }

    #endregion

    #region Nested Types

    protected struct QueueFamilyIndices
    {
        public uint? GraphicsFamily { get; set; }
        public uint? PresentFamily { get; set; }
        public bool IsComplete() => GraphicsFamily.HasValue && PresentFamily.HasValue;
    }

    protected struct SwapChainSupportDetails
    {
        public SurfaceCapabilitiesKHR Capabilities;
        public SurfaceFormatKHR[] Formats;
        public PresentModeKHR[] PresentModes;
    }

    #endregion
}
