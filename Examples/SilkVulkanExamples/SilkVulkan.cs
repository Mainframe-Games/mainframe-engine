using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Silk.NET.Core;
using Silk.NET.Core.Native;
using Silk.NET.Maths;
using Silk.NET.Vulkan;
using Silk.NET.Vulkan.Extensions.EXT;
using Silk.NET.Vulkan.Extensions.KHR;
using Silk.NET.Windowing;

namespace SilkVulkanExamples;

/*
    Tutorials Completed:
    - BaseCode
    - ValidationLayers
    - PhysicalDevice
    - LogicalDevice
    - WindowSurface
    - Swapchain
    - ImageViews
    - RenderPass
    - GraphicsPipeline
    - Framebuffers
    - CommandPool
    - CommandBuffers
    - RenderingAndPresentation
 */


/// <summary>
/// A Vulkan application using Silk.NET that renders a simple RGB triangle.
/// <remarks>
/// Tutorial Src: https://github.com/dfkeenan/SilkVulkanTutorial
/// </remarks>
/// </summary>
public unsafe class SilkVulkan : IDisposable
{
    private IWindow? window;
    private Vk? vk;
    private Instance instance;
    private PhysicalDevice physicalDevice;
    private Device device;
    private Queue graphicsQueue;
    private Queue presentQueue;
    private KhrSurface? khrSurface;
    private SurfaceKHR surface;

    // swapchain
    private KhrSwapchain? khrSwapChain;
    private SwapchainKHR swapChain;
    private Image[]? swapChainImages;
    private Format swapChainImageFormat;
    private Extent2D swapChainExtent;

    // image views
    private ImageView[]? swapChainImageViews;

    // render pass
    private RenderPass renderPass;

    // pipeline
    private PipelineLayout pipelineLayout;
    private Pipeline graphicsPipeline;

    // framebuffers
    private Framebuffer[]? swapChainFramebuffers;

    // commands
    private CommandPool commandPool;
    private CommandBuffer[]? commandBuffers;

    // sync objects
    private const int MaxFramesInFlight = 2;
    private Silk.NET.Vulkan.Semaphore[]? imageAvailableSemaphores;
    private Silk.NET.Vulkan.Semaphore[]? renderFinishedSemaphores;
    private Fence[]? inFlightFences;
    private Fence[]? imagesInFlight;
    private int currentFrame;
    private bool framebufferResized;

    // validation layers
    private bool EnableValidationLayers { get; set; }
    private readonly string[] validationLayers = ["VK_LAYER_KHRONOS_validation"];
    private ExtDebugUtils? debugUtils;
    private DebugUtilsMessengerEXT debugMessenger;

    private readonly string[] deviceExtensions = [KhrSwapchain.ExtensionName];

    public SilkVulkan(string title, int width, int height, bool enableValidationLayers = true)
    {
        EnableValidationLayers = enableValidationLayers;
        InitWindow(title, width, height);
        InitVulkan(title);
    }

    public void Dispose()
    {
        Log.Info("[Vulkan] Disposing...");

        vk!.DeviceWaitIdle(device);

        CleanupSwapchain();

        vk!.DestroyPipeline(device, graphicsPipeline, null);
        vk!.DestroyPipelineLayout(device, pipelineLayout, null);
        vk!.DestroyRenderPass(device, renderPass, null);

        for (int i = 0; i < MaxFramesInFlight; i++)
        {
            vk!.DestroySemaphore(device, imageAvailableSemaphores![i], null);
            vk!.DestroyFence(device, inFlightFences![i], null);
        }

        for (int i = 0; i < renderFinishedSemaphores!.Length; i++)
            vk!.DestroySemaphore(device, renderFinishedSemaphores[i], null);

        vk!.DestroyCommandPool(device, commandPool, null);
        vk!.DestroyDevice(device, null);

        if (EnableValidationLayers)
            debugUtils!.DestroyDebugUtilsMessenger(instance, debugMessenger, null);

        khrSurface?.DestroySurface(instance, surface, null);
        vk?.DestroyInstance(instance, null);
        vk?.Dispose();

        window?.Dispose();
    }

    private void CleanupSwapchain()
    {
        if (swapChainFramebuffers is not null)
            foreach (var framebuffer in swapChainFramebuffers)
                vk!.DestroyFramebuffer(device, framebuffer, null);

        if (commandBuffers is not null)
            fixed (CommandBuffer* commandBuffersPtr = commandBuffers)
                vk!.FreeCommandBuffers(device, commandPool, (uint)commandBuffers.Length, commandBuffersPtr);

        if (swapChainImageViews is not null)
            foreach (var imageView in swapChainImageViews)
                vk!.DestroyImageView(device, imageView, null);

        khrSwapChain!.DestroySwapchain(device, swapChain, null);
    }

    public void Run()
    {
        window?.Run();
    }

    #region Setup

    private void InitWindow(string title, int width, int height)
    {
        var options = WindowOptions.DefaultVulkan with
        {
            Size = new Vector2D<int>(width, height),
            Title = title,
        };

        window = Window.Create(options);
        window.Render += DrawFrame;
        window.FramebufferResize += _ => framebufferResized = true;
        window.Initialize();
        window.Center();

        if (window.VkSurface is null)
            throw new Exception("Windowing platform doesn't support Vulkan.");
    }

    private void InitVulkan(string appName)
    {
        CreateInstance(appName, "Mainframe Engine");
        SetupDebugMessenger();
        CreateSurface();
        PickPhysicalDevice();
        CreateLogicalDevice();
        CreateSwapchain();
        CreateImageViews();
        CreateRenderPass();
        CreateGraphicsPipeline();
        CreateFramebuffers();
        CreateCommandPool();
        CreateCommandBuffers();
        CreateSyncObjects();
    }

    private void CreateInstance(string appName, string engineName)
    {
        vk = Vk.GetApi();

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
            createInfo.EnabledLayerCount = (uint)validationLayers.Length;
            createInfo.PpEnabledLayerNames = (byte**)SilkMarshal.StringArrayToPtr(validationLayers);

            DebugUtilsMessengerCreateInfoEXT debugCreateInfo = new();
            PopulateDebugMessengerCreateInfo(ref debugCreateInfo);
            createInfo.PNext = &debugCreateInfo;
        }
        else
        {
            createInfo.EnabledLayerCount = 0;
            createInfo.PNext = null;
        }

        if (vk.CreateInstance(in createInfo, null, out instance) != Result.Success)
            throw new Exception("failed to create instance!");

        Log.Info($"[Vulkan] Instance: {instance}");

        Marshal.FreeHGlobal((IntPtr)appInfo.PApplicationName);
        Marshal.FreeHGlobal((IntPtr)appInfo.PEngineName);
        SilkMarshal.Free((nint)createInfo.PpEnabledExtensionNames);

        if (EnableValidationLayers)
            SilkMarshal.Free((nint)createInfo.PpEnabledLayerNames);
    }

    private string[] GetRequiredExtensions()
    {
        var glfwExtensions = window!.VkSurface!.GetRequiredExtensions(out var glfwExtensionCount);
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
        if (!EnableValidationLayers)
            return;

        //TryGetInstanceExtension equivilant to method CreateDebugUtilsMessengerEXT from original tutorial.
        if (!vk!.TryGetInstanceExtension(instance, out debugUtils))
            return;

        var createInfo = new DebugUtilsMessengerCreateInfoEXT();
        PopulateDebugMessengerCreateInfo(ref createInfo);

        if (debugUtils!.CreateDebugUtilsMessenger(instance, in createInfo, null, out debugMessenger) is not Result.Success)
            throw new Exception("failed to set up debug messenger!");
    }

    private bool CheckValidationLayerSupport()
    {
        var layerCount = 0u;
        vk!.EnumerateInstanceLayerProperties(ref layerCount, null);
        var availableLayers = new LayerProperties[layerCount];
        fixed (LayerProperties* availableLayersPtr = availableLayers)
            vk!.EnumerateInstanceLayerProperties(ref layerCount, availableLayersPtr);

        var availableLayerNames = availableLayers.Select(layer => Marshal.PtrToStringAnsi((IntPtr)layer.LayerName)).ToHashSet();

        return validationLayers.All(availableLayerNames.Contains);
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

    #region Window Surface

    private void CreateSurface()
    {
        if (!vk!.TryGetInstanceExtension<KhrSurface>(instance, out khrSurface))
            throw new NotSupportedException("KHR_surface extension not found.");

        surface = window!.VkSurface!.Create<AllocationCallbacks>(instance.ToHandle(), null).ToSurface();
        Log.Info($"[Vulkan] Surface: {surface}");
    }

    #endregion

    #region Physical Device

    private void PickPhysicalDevice()
    {
        var devices = vk!.GetPhysicalDevices(instance);

        foreach (var d in devices)
        {
            if (IsDeviceSuitable(d))
            {
                physicalDevice = d;
                Log.Info($"[Vulkan] Physical Device: {physicalDevice}");
                break;
            }
        }

        if (physicalDevice.Handle == 0)
            throw new Exception("failed to find a suitable GPU!");
    }

    private bool IsDeviceSuitable(PhysicalDevice device)
    {
        var indices = FindQueueFamilies(device);
        return indices.IsComplete();
    }

    private QueueFamilyIndices FindQueueFamilies(PhysicalDevice device)
    {
        var indices = new QueueFamilyIndices();

        var queueFamilityCount = 0u;
        vk!.GetPhysicalDeviceQueueFamilyProperties(device, ref queueFamilityCount, null);

        var queueFamilies = new QueueFamilyProperties[queueFamilityCount];
        fixed (QueueFamilyProperties* queueFamiliesPtr = queueFamilies)
            vk!.GetPhysicalDeviceQueueFamilyProperties(device, ref queueFamilityCount, queueFamiliesPtr);

        var i = 0u;
        foreach (var queueFamily in queueFamilies)
        {
            if (queueFamily.QueueFlags.HasFlag(QueueFlags.GraphicsBit))
                indices.GraphicsFamily = i;

            khrSurface!.GetPhysicalDeviceSurfaceSupport(device, i, surface, out var presentSupport);

            if (presentSupport)
                indices.PresentFamily = i;

            if (indices.IsComplete())
                break;

            i++;
        }

        return indices;
    }

    #endregion

    #region Logical Device

    private void CreateLogicalDevice()
    {
        var indices = FindQueueFamilies(physicalDevice);

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

        var createInfo = new DeviceCreateInfo
        {
            SType = StructureType.DeviceCreateInfo,
            QueueCreateInfoCount = (uint)uniqueQueueFamilies.Length,
            PQueueCreateInfos = queueCreateInfos,

            PEnabledFeatures = &deviceFeatures,

            EnabledExtensionCount = (uint)deviceExtensions.Length,
            PpEnabledExtensionNames = (byte**)SilkMarshal.StringArrayToPtr(deviceExtensions)
        };

        if (EnableValidationLayers)
        {
            createInfo.EnabledLayerCount = (uint)validationLayers.Length;
            createInfo.PpEnabledLayerNames = (byte**)SilkMarshal.StringArrayToPtr(validationLayers);
        }
        else
        {
            createInfo.EnabledLayerCount = 0;
        }

        if (vk!.CreateDevice(physicalDevice, in createInfo, null, out device) != Result.Success)
            throw new Exception("failed to create logical device!");

        Log.Info($"[Vulkan] Logical Device: {device}");

        vk!.GetDeviceQueue(device, indices.GraphicsFamily!.Value, 0, out graphicsQueue);
        vk!.GetDeviceQueue(device, indices.PresentFamily!.Value, 0, out presentQueue);

        if (EnableValidationLayers)
            SilkMarshal.Free((nint)createInfo.PpEnabledLayerNames);

        SilkMarshal.Free((nint)createInfo.PpEnabledExtensionNames);
    }

    #endregion

    #region Swapchain

    private void CreateSwapchain()
    {
        var swapChainSupport = QuerySwapChainSupport(physicalDevice);
        var surfaceFormat = ChooseSwapSurfaceFormat(swapChainSupport.Formats);
        var presentMode = ChoosePresentMode(swapChainSupport.PresentModes);
        var extent = ChooseSwapExtent(swapChainSupport.Capabilities);

        var imageCount = swapChainSupport.Capabilities.MinImageCount + 1;
        if (swapChainSupport.Capabilities.MaxImageCount > 0 && imageCount > swapChainSupport.Capabilities.MaxImageCount)
            imageCount = swapChainSupport.Capabilities.MaxImageCount;

        var creatInfo = new SwapchainCreateInfoKHR
        {
            SType = StructureType.SwapchainCreateInfoKhr,
            Surface = surface,

            MinImageCount = imageCount,
            ImageFormat = surfaceFormat.Format,
            ImageColorSpace = surfaceFormat.ColorSpace,
            ImageExtent = extent,
            ImageArrayLayers = 1,
            ImageUsage = ImageUsageFlags.ColorAttachmentBit,
        };

        var indices = FindQueueFamilies(physicalDevice);
        var queueFamilyIndices = stackalloc[] { indices.GraphicsFamily!.Value, indices.PresentFamily!.Value };

        if (indices.GraphicsFamily != indices.PresentFamily)
        {
            creatInfo = creatInfo with
            {
                ImageSharingMode = SharingMode.Concurrent,
                QueueFamilyIndexCount = 2,
                PQueueFamilyIndices = queueFamilyIndices,
            };
        }
        else
        {
            creatInfo.ImageSharingMode = SharingMode.Exclusive;
        }

        creatInfo = creatInfo with
        {
            PreTransform = swapChainSupport.Capabilities.CurrentTransform,
            CompositeAlpha = CompositeAlphaFlagsKHR.OpaqueBitKhr,
            PresentMode = presentMode,
            Clipped = true,

            OldSwapchain = default
        };

        if (!vk!.TryGetDeviceExtension(instance, device, out khrSwapChain))
            throw new NotSupportedException("VK_KHR_swapchain extension not found.");

        if (khrSwapChain!.CreateSwapchain(device, in creatInfo, null, out swapChain) != Result.Success)
            throw new Exception("failed to create swap chain!");

        khrSwapChain.GetSwapchainImages(device, swapChain, ref imageCount, null);
        swapChainImages = new Image[imageCount];
        fixed (Image* swapChainImagesPtr = swapChainImages)
            khrSwapChain.GetSwapchainImages(device, swapChain, ref imageCount, swapChainImagesPtr);

        swapChainImageFormat = surfaceFormat.Format;
        swapChainExtent = extent;

        Log.Info($"[Vulkan] Swapchain: {swapChain}");
    }

    private SwapChainSupportDetails QuerySwapChainSupport(PhysicalDevice physicalDevice)
    {
        var details = new SwapChainSupportDetails();

        khrSurface!.GetPhysicalDeviceSurfaceCapabilities(physicalDevice, surface, out details.Capabilities);

        var formatCount = 0u;
        khrSurface.GetPhysicalDeviceSurfaceFormats(physicalDevice, surface, ref formatCount, null);

        if (formatCount != 0)
        {
            details.Formats = new SurfaceFormatKHR[formatCount];
            fixed (SurfaceFormatKHR* formatsPtr = details.Formats)
                khrSurface.GetPhysicalDeviceSurfaceFormats(physicalDevice, surface, ref formatCount, formatsPtr);
        }
        else
        {
            details.Formats = [];
        }

        var presentModeCount = 0u;
        khrSurface.GetPhysicalDeviceSurfacePresentModes(physicalDevice, surface, ref presentModeCount, null);

        if (presentModeCount != 0)
        {
            details.PresentModes = new PresentModeKHR[presentModeCount];
            fixed (PresentModeKHR* formatsPtr = details.PresentModes)
                khrSurface.GetPhysicalDeviceSurfacePresentModes(physicalDevice, surface, ref presentModeCount, formatsPtr);
        }
        else
        {
            details.PresentModes = [];
        }

        return details;
    }

    private static SurfaceFormatKHR ChooseSwapSurfaceFormat(SurfaceFormatKHR[] availableFormats)
    {
        foreach (var availableFormat in availableFormats)
            if (availableFormat is { Format: Format.B8G8R8A8Srgb, ColorSpace: ColorSpaceKHR.SpaceSrgbNonlinearKhr })
                return availableFormat;

        return availableFormats[0];
    }

    private static PresentModeKHR ChoosePresentMode(IReadOnlyList<PresentModeKHR> availablePresentModes)
    {
        foreach (var availablePresentMode in availablePresentModes)
            if (availablePresentMode == PresentModeKHR.MailboxKhr)
                return availablePresentMode;

        return PresentModeKHR.FifoKhr;
    }

    private Extent2D ChooseSwapExtent(SurfaceCapabilitiesKHR capabilities)
    {
        if (capabilities.CurrentExtent.Width != uint.MaxValue)
            return capabilities.CurrentExtent;

        var framebufferSize = window!.FramebufferSize;

        var actualExtent = new Extent2D
        {
            Width = (uint)framebufferSize.X,
            Height = (uint)framebufferSize.Y
        };

        actualExtent.Width = Math.Clamp(actualExtent.Width, capabilities.MinImageExtent.Width, capabilities.MaxImageExtent.Width);
        actualExtent.Height = Math.Clamp(actualExtent.Height, capabilities.MinImageExtent.Height, capabilities.MaxImageExtent.Height);

        return actualExtent;
    }

    #endregion

    #region Image Views

    private void CreateImageViews()
    {
        swapChainImageViews = new ImageView[swapChainImages!.Length];
        for (int i = 0; i < swapChainImages.Length; i++)
        {
            var createInfo = new ImageViewCreateInfo
            {
                SType = StructureType.ImageViewCreateInfo,
                Image = swapChainImages[i],
                ViewType = ImageViewType.Type2D,
                Format = swapChainImageFormat,
                Components =
                {
                    R = ComponentSwizzle.Identity,
                    G = ComponentSwizzle.Identity,
                    B = ComponentSwizzle.Identity,
                    A = ComponentSwizzle.Identity,
                },
                SubresourceRange =
                {
                    AspectMask = ImageAspectFlags.ColorBit,
                    BaseMipLevel = 0,
                    LevelCount = 1,
                    BaseArrayLayer = 0,
                    LayerCount = 1,
                }
            };

            if (vk!.CreateImageView(device, in createInfo, null, out swapChainImageViews[i]) != Result.Success)
                throw new Exception("failed to create image views!");
        }

        Log.Info($"[Vulkan] Image Views: {swapChainImageViews.Length}");
    }

    #endregion

    #region Render Pass

    private void CreateRenderPass()
    {
        var colorAttachment = new AttachmentDescription
        {
            Format = swapChainImageFormat,
            Samples = SampleCountFlags.Count1Bit,
            LoadOp = AttachmentLoadOp.Clear,
            StoreOp = AttachmentStoreOp.Store,
            StencilLoadOp = AttachmentLoadOp.DontCare,
            StencilStoreOp = AttachmentStoreOp.DontCare,
            InitialLayout = ImageLayout.Undefined,
            FinalLayout = ImageLayout.PresentSrcKhr,
        };

        var colorAttachmentRef = new AttachmentReference
        {
            Attachment = 0,
            Layout = ImageLayout.ColorAttachmentOptimal,
        };

        var subpass = new SubpassDescription
        {
            PipelineBindPoint = PipelineBindPoint.Graphics,
            ColorAttachmentCount = 1,
            PColorAttachments = &colorAttachmentRef,
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

        if (vk!.CreateRenderPass(device, in renderPassInfo, null, out renderPass) != Result.Success)
            throw new Exception("failed to create render pass!");

        Log.Info($"[Vulkan] Render Pass: {renderPass}");
    }

    #endregion

    #region Graphics Pipeline

    private void CreateGraphicsPipeline()
    {
        var vertShaderCode = File.ReadAllBytes("Content/Shaders/shader.vert.spv");
        var fragShaderCode = File.ReadAllBytes("Content/Shaders/shader.frag.spv");

        var vertShaderModule = CreateShaderModule(vertShaderCode);
        var fragShaderModule = CreateShaderModule(fragShaderCode);

        var vertShaderStageInfo = new PipelineShaderStageCreateInfo
        {
            SType = StructureType.PipelineShaderStageCreateInfo,
            Stage = ShaderStageFlags.VertexBit,
            Module = vertShaderModule,
            PName = (byte*)SilkMarshal.StringToPtr("main"),
        };

        var fragShaderStageInfo = new PipelineShaderStageCreateInfo
        {
            SType = StructureType.PipelineShaderStageCreateInfo,
            Stage = ShaderStageFlags.FragmentBit,
            Module = fragShaderModule,
            PName = (byte*)SilkMarshal.StringToPtr("main"),
        };

        var shaderStages = stackalloc[] { vertShaderStageInfo, fragShaderStageInfo };

        var vertexInputInfo = new PipelineVertexInputStateCreateInfo
        {
            SType = StructureType.PipelineVertexInputStateCreateInfo,
            VertexBindingDescriptionCount = 0,
            VertexAttributeDescriptionCount = 0,
        };

        var inputAssembly = new PipelineInputAssemblyStateCreateInfo
        {
            SType = StructureType.PipelineInputAssemblyStateCreateInfo,
            Topology = PrimitiveTopology.TriangleList,
            PrimitiveRestartEnable = false,
        };

        var viewport = new Viewport
        {
            X = 0,
            Y = 0,
            Width = swapChainExtent.Width,
            Height = swapChainExtent.Height,
            MinDepth = 0,
            MaxDepth = 1,
        };

        var scissor = new Rect2D { Offset = default, Extent = swapChainExtent };

        var viewportState = new PipelineViewportStateCreateInfo
        {
            SType = StructureType.PipelineViewportStateCreateInfo,
            ViewportCount = 1,
            PViewports = &viewport,
            ScissorCount = 1,
            PScissors = &scissor,
        };

        var rasterizer = new PipelineRasterizationStateCreateInfo
        {
            SType = StructureType.PipelineRasterizationStateCreateInfo,
            DepthClampEnable = false,
            RasterizerDiscardEnable = false,
            PolygonMode = PolygonMode.Fill,
            LineWidth = 1,
            CullMode = CullModeFlags.BackBit,
            FrontFace = FrontFace.Clockwise,
            DepthBiasEnable = false,
        };

        var multisampling = new PipelineMultisampleStateCreateInfo
        {
            SType = StructureType.PipelineMultisampleStateCreateInfo,
            SampleShadingEnable = false,
            RasterizationSamples = SampleCountFlags.Count1Bit,
        };

        var colorBlendAttachment = new PipelineColorBlendAttachmentState
        {
            ColorWriteMask = ColorComponentFlags.RBit | ColorComponentFlags.GBit | ColorComponentFlags.BBit | ColorComponentFlags.ABit,
            BlendEnable = false,
        };

        var colorBlending = new PipelineColorBlendStateCreateInfo
        {
            SType = StructureType.PipelineColorBlendStateCreateInfo,
            LogicOpEnable = false,
            LogicOp = LogicOp.Copy,
            AttachmentCount = 1,
            PAttachments = &colorBlendAttachment,
        };

        var pipelineLayoutInfo = new PipelineLayoutCreateInfo
        {
            SType = StructureType.PipelineLayoutCreateInfo,
            SetLayoutCount = 0,
            PushConstantRangeCount = 0,
        };

        if (vk!.CreatePipelineLayout(device, in pipelineLayoutInfo, null, out pipelineLayout) != Result.Success)
            throw new Exception("failed to create pipeline layout!");

        var pipelineInfo = new GraphicsPipelineCreateInfo
        {
            SType = StructureType.GraphicsPipelineCreateInfo,
            StageCount = 2,
            PStages = shaderStages,
            PVertexInputState = &vertexInputInfo,
            PInputAssemblyState = &inputAssembly,
            PViewportState = &viewportState,
            PRasterizationState = &rasterizer,
            PMultisampleState = &multisampling,
            PColorBlendState = &colorBlending,
            Layout = pipelineLayout,
            RenderPass = renderPass,
            Subpass = 0,
            BasePipelineHandle = default,
        };

        if (vk!.CreateGraphicsPipelines(device, default, 1, in pipelineInfo, null, out graphicsPipeline) != Result.Success)
            throw new Exception("failed to create graphics pipeline!");

        Log.Info($"[Vulkan] Graphics Pipeline: {graphicsPipeline}");

        vk!.DestroyShaderModule(device, vertShaderModule, null);
        vk!.DestroyShaderModule(device, fragShaderModule, null);

        SilkMarshal.Free((nint)vertShaderStageInfo.PName);
        SilkMarshal.Free((nint)fragShaderStageInfo.PName);
    }

    private ShaderModule CreateShaderModule(byte[] code)
    {
        var createInfo = new ShaderModuleCreateInfo
        {
            SType = StructureType.ShaderModuleCreateInfo,
            CodeSize = (nuint)code.Length,
        };

        fixed (byte* codePtr = code)
        {
            createInfo.PCode = (uint*)codePtr;
            if (vk!.CreateShaderModule(device, in createInfo, null, out var shaderModule) != Result.Success)
                throw new Exception("failed to create shader module!");
            return shaderModule;
        }
    }

    #endregion

    #region Framebuffers

    private void CreateFramebuffers()
    {
        swapChainFramebuffers = new Framebuffer[swapChainImageViews!.Length];
        for (int i = 0; i < swapChainImageViews.Length; i++)
        {
            var attachment = swapChainImageViews[i];
            var framebufferInfo = new FramebufferCreateInfo
            {
                SType = StructureType.FramebufferCreateInfo,
                RenderPass = renderPass,
                AttachmentCount = 1,
                PAttachments = &attachment,
                Width = swapChainExtent.Width,
                Height = swapChainExtent.Height,
                Layers = 1,
            };

            if (vk!.CreateFramebuffer(device, in framebufferInfo, null, out swapChainFramebuffers[i]) != Result.Success)
                throw new Exception("failed to create framebuffer!");
        }

        Log.Info($"[Vulkan] Framebuffers: {swapChainFramebuffers.Length}");
    }

    #endregion

    #region Command Pool & Buffers

    private void CreateCommandPool()
    {
        var queueFamilyIndices = FindQueueFamilies(physicalDevice);
        var poolInfo = new CommandPoolCreateInfo
        {
            SType = StructureType.CommandPoolCreateInfo,
            QueueFamilyIndex = queueFamilyIndices.GraphicsFamily!.Value,
        };

        if (vk!.CreateCommandPool(device, in poolInfo, null, out commandPool) != Result.Success)
            throw new Exception("failed to create command pool!");

        Log.Info($"[Vulkan] Command Pool: {commandPool}");
    }

    private void CreateCommandBuffers()
    {
        commandBuffers = new CommandBuffer[swapChainFramebuffers!.Length];
        var allocInfo = new CommandBufferAllocateInfo
        {
            SType = StructureType.CommandBufferAllocateInfo,
            CommandPool = commandPool,
            Level = CommandBufferLevel.Primary,
            CommandBufferCount = (uint)commandBuffers.Length,
        };

        fixed (CommandBuffer* commandBuffersPtr = commandBuffers)
            if (vk!.AllocateCommandBuffers(device, in allocInfo, commandBuffersPtr) != Result.Success)
                throw new Exception("failed to allocate command buffers!");

        for (int i = 0; i < commandBuffers.Length; i++)
        {
            var beginInfo = new CommandBufferBeginInfo { SType = StructureType.CommandBufferBeginInfo };
            if (vk!.BeginCommandBuffer(commandBuffers[i], in beginInfo) != Result.Success)
                throw new Exception("failed to begin recording command buffer!");

            var clearColor = new ClearValue
            {
                Color = new() { Float32_0 = 0.05f, Float32_1 = 0.05f, Float32_2 = 0.05f, Float32_3 = 1 }
            };

            var renderPassInfo = new RenderPassBeginInfo
            {
                SType = StructureType.RenderPassBeginInfo,
                RenderPass = renderPass,
                Framebuffer = swapChainFramebuffers[i],
                RenderArea = { Offset = default, Extent = swapChainExtent },
                ClearValueCount = 1,
                PClearValues = &clearColor,
            };

            vk!.CmdBeginRenderPass(commandBuffers[i], &renderPassInfo, SubpassContents.Inline);
            vk!.CmdBindPipeline(commandBuffers[i], PipelineBindPoint.Graphics, graphicsPipeline);
            vk!.CmdDraw(commandBuffers[i], 3, 1, 0, 0);
            vk!.CmdEndRenderPass(commandBuffers[i]);

            if (vk!.EndCommandBuffer(commandBuffers[i]) != Result.Success)
                throw new Exception("failed to record command buffer!");
        }

        Log.Info($"[Vulkan] Command Buffers: {commandBuffers.Length}");
    }

    #endregion

    #region Sync Objects

    private void CreateSyncObjects()
    {
        imageAvailableSemaphores = new Silk.NET.Vulkan.Semaphore[MaxFramesInFlight];
        // One per swapchain image — safe to reuse only once that image is re-acquired
        renderFinishedSemaphores = new Silk.NET.Vulkan.Semaphore[swapChainImages!.Length];
        inFlightFences = new Fence[MaxFramesInFlight];
        imagesInFlight = new Fence[swapChainImages!.Length];

        var semaphoreInfo = new SemaphoreCreateInfo { SType = StructureType.SemaphoreCreateInfo };
        var fenceInfo = new FenceCreateInfo
        {
            SType = StructureType.FenceCreateInfo,
            Flags = FenceCreateFlags.SignaledBit,
        };

        for (int i = 0; i < MaxFramesInFlight; i++)
        {
            if (vk!.CreateSemaphore(device, in semaphoreInfo, null, out imageAvailableSemaphores[i]) != Result.Success ||
                vk!.CreateFence(device, in fenceInfo, null, out inFlightFences[i]) != Result.Success)
                throw new Exception("failed to create synchronization objects!");
        }

        for (int i = 0; i < renderFinishedSemaphores.Length; i++)
        {
            if (vk!.CreateSemaphore(device, in semaphoreInfo, null, out renderFinishedSemaphores[i]) != Result.Success)
                throw new Exception("failed to create render finished semaphore!");
        }

        Log.Info("[Vulkan] Sync Objects created");
    }

    #endregion

    #endregion

    #region Draw Frame

    private void DrawFrame(double delta)
    {
        vk!.WaitForFences(device, 1, in inFlightFences![currentFrame], true, ulong.MaxValue);

        uint imageIndex;
        var result = khrSwapChain!.AcquireNextImage(device, swapChain, ulong.MaxValue,
            imageAvailableSemaphores![currentFrame], default, &imageIndex);

        if (result == Result.ErrorOutOfDateKhr)
        {
            RecreateSwapchain();
            return;
        }
        else if (result != Result.Success && result != Result.SuboptimalKhr)
        {
            throw new Exception("failed to acquire swap chain image!");
        }

        if (imagesInFlight![imageIndex].Handle != 0)
            vk!.WaitForFences(device, 1, in imagesInFlight[imageIndex], true, ulong.MaxValue);

        imagesInFlight[imageIndex] = inFlightFences[currentFrame];

        var waitSemaphore = imageAvailableSemaphores[currentFrame];
        var waitStage = PipelineStageFlags.ColorAttachmentOutputBit;
        // Indexed by imageIndex — safe to reuse because re-acquiring this image guarantees
        // the previous presentation using this semaphore has completed.
        var signalSemaphore = renderFinishedSemaphores![imageIndex];
        var commandBuffer = commandBuffers![imageIndex];

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

        vk!.ResetFences(device, 1, in inFlightFences[currentFrame]);

        if (vk!.QueueSubmit(graphicsQueue, 1, in submitInfo, inFlightFences[currentFrame]) != Result.Success)
            throw new Exception("failed to submit draw command buffer!");

        var swapChainHandle = swapChain;
        var presentInfo = new PresentInfoKHR
        {
            SType = StructureType.PresentInfoKhr,
            WaitSemaphoreCount = 1,
            PWaitSemaphores = &signalSemaphore,
            SwapchainCount = 1,
            PSwapchains = &swapChainHandle,
            PImageIndices = &imageIndex,
        };

        result = khrSwapChain.QueuePresent(presentQueue, in presentInfo);

        if (result == Result.ErrorOutOfDateKhr || result == Result.SuboptimalKhr || framebufferResized)
        {
            framebufferResized = false;
            RecreateSwapchain();
        }
        else if (result != Result.Success)
        {
            throw new Exception("failed to present swap chain image!");
        }

        currentFrame = (currentFrame + 1) % MaxFramesInFlight;
    }

    #endregion

    #region Swapchain Recreation

    private void RecreateSwapchain()
    {
        // Wait while window is minimized
        var size = window!.FramebufferSize;
        while (size.X == 0 || size.Y == 0)
        {
            size = window.FramebufferSize;
            window.DoEvents();
        }

        vk!.DeviceWaitIdle(device);
        CleanupSwapchain();
        CreateSwapchain();
        CreateImageViews();
        CreateFramebuffers();
        CreateCommandBuffers();
        imagesInFlight = new Fence[swapChainImages!.Length];
    }

    #endregion

    private struct QueueFamilyIndices
    {
        public uint? GraphicsFamily { get; set; }
        public uint? PresentFamily { get; set; }

        public bool IsComplete() => GraphicsFamily.HasValue;
    }

    private struct SwapChainSupportDetails
    {
        public SurfaceCapabilitiesKHR Capabilities;
        public SurfaceFormatKHR[] Formats;
        public PresentModeKHR[] PresentModes;
    }
}
