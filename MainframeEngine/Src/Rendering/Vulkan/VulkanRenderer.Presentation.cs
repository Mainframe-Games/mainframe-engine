using Silk.NET.Vulkan;

namespace MainframeEngine;

/// <summary>
/// Colour pipeline (docs/design/color-pipeline.md): the scene renders into an HDR offscreen target
/// (<see cref="SceneTarget"/>, <c>R16G16B16A16_SFLOAT</c> + depth); the tonemap pass (exposure, ACES fitted)
/// writes it to the swapchain; then the overlay pass draws ImGui, which is authored in sRGB, so it is composited
/// exactly as before the colour pipeline existed.
/// </summary>
/// <remarks>
/// <para>Swapchain choice (<see cref="SwapchainEncoding"/>, <see cref="ChooseSurfaceFormat"/>): by default a UNORM
/// swapchain whose tonemap shader encodes sRGB, with ImGui in the same pass writing its sRGB values unchanged. A
/// surface without 8-bit UNORM formats gets an sRGB swapchain: with <c>VK_KHR_swapchain_mutable_format</c> the
/// tonemap writes an sRGB view (hardware encode) and the overlay a UNORM view of the same image; otherwise ImGui
/// linearises its colours (blending then happens in linear space). <c>MAINFRAME_SWAPCHAIN_ENCODING</c> forces a
/// path for QA.</para>
/// <para>Pass order per frame: <see cref="BeginRenderPass"/> (scene) → <see cref="BeginOverlayPass"/> (end scene,
/// tonemap, begin overlay) → <see cref="EndFrame"/>, which runs whatever is missing so a frame always ends
/// presentable.</para>
/// </remarks>
internal sealed unsafe partial class VulkanRenderer
{
    /// <summary>Format of the HDR scene colour target.</summary>
    public const Format SceneColorFormat = Format.R16G16B16A16Sfloat;

    private enum PassState : byte { None, Scene, Overlay }

    /// <summary>How the swapchain gets sRGB-encoded pixels; see the type remarks.</summary>
    internal enum SwapchainEncoding : byte
    {
        /// <summary>sRGB swapchain; tonemap via an sRGB view, overlay via a UNORM view (mutable format).</summary>
        SrgbWithUnormOverlay,

        /// <summary>UNORM swapchain; the tonemap shader encodes sRGB; overlay in the same pass.</summary>
        UnormShaderEncode,

        /// <summary>sRGB swapchain without a UNORM view; overlay colours are linearised in the shader.</summary>
        SrgbOnly,
    }

    /// <summary>Forces the swapchain encoding (QA, tests): <c>srgb-mutable</c>, <c>unorm</c> or <c>srgb</c>.</summary>
    internal const string EncodingVariable = "MAINFRAME_SWAPCHAIN_ENCODING";

    private const string MutableFormatExtension = "VK_KHR_swapchain_mutable_format";
    private SwapchainEncoding? _requestedEncoding;

    internal static SwapchainEncoding? ParseEncoding(string? value) => value?.ToLowerInvariant() switch
    {
        null or "" => null,
        "srgb-mutable" => SwapchainEncoding.SrgbWithUnormOverlay,
        "unorm" => SwapchainEncoding.UnormShaderEncode,
        "srgb" => SwapchainEncoding.SrgbOnly,
        _ => throw new ArgumentException($"{EncodingVariable} must be srgb-mutable, unorm or srgb (got '{value}')."),
    };
    private const string ImageFormatListExtension = "VK_KHR_image_format_list";

    private bool _mutableFormatAvailable; // device extension enabled
    private SwapchainEncoding _encoding;
    private Format _overlayFormat;
    private ImageView[]? _swapChainImageViews;   // swapchain format (tonemap target)
    private ImageView[]? _overlayImageViews;     // UNORM views (SrgbWithUnormOverlay only)
    private Framebuffer[]? _presentFramebuffers;
    private Framebuffer[]? _overlayFramebuffers; // SrgbWithUnormOverlay only
    private RenderPass _presentPass;
    private RenderPass _overlayPass;             // == _presentPass unless SrgbWithUnormOverlay
    private Format _depthFormat;
    private RenderTarget? _sceneTarget;
    private PassState _passState;

    // Tonemap: fullscreen triangle reading the HDR target (one texel per pixel).
    private DescriptorSetLayout _tonemapSetLayout;
    private DescriptorPool _tonemapPool;
    private DescriptorSet _tonemapSet;
    private Sampler _tonemapSampler;
    private PipelineLayout _tonemapLayout;
    private Pipeline _tonemapPipeline;

    public RenderTarget SceneTarget => _sceneTarget ?? throw new InvalidOperationException("The renderer is not initialised.");
    public RenderPass OverlayRenderPass => _overlayPass;
    public bool OverlayEncodesSrgb => _encoding == SwapchainEncoding.SrgbOnly;
    internal SwapchainEncoding Encoding => _encoding;
    internal Format SwapchainFormat => _swapChainImageFormat;

    // ── Swapchain format ──────────────────────────────────────────────────────

    /// <summary>
    /// Picks the surface format and where sRGB encoding happens (ADR "colour pipeline"): a UNORM swapchain with the
    /// tonemap shader encoding first (exact ImGui, tonemap and UI in one pass, works everywhere); an sRGB swapchain
    /// only when no 8-bit UNORM format is offered, or when <c>MAINFRAME_SWAPCHAIN_ENCODING</c> asks for one.
    /// Deterministic for a surface, so recreation keeps the choice.
    /// </summary>
    /// <remarks>
    /// The sRGB + mutable-format path is opt-in because MoltenVK serves the UNORM view of a swapchain image from a
    /// stale drawable on some frames (the overlay then misses the presented image); both encodings produce
    /// byte-identical scene pixels.
    /// </remarks>
    internal static (SurfaceFormatKHR Format, SwapchainEncoding Encoding, Format OverlayFormat) ChooseSurfaceFormat(
        ReadOnlySpan<SurfaceFormatKHR> formats, bool mutableFormatAvailable, SwapchainEncoding? requested = null)
    {
        if (formats.IsEmpty)
            throw new VulkanException("[Vulkan] The surface reports no formats.");

        ReadOnlySpan<Format> srgb = [Format.B8G8R8A8Srgb, Format.R8G8B8A8Srgb];
        ReadOnlySpan<Format> unorm = [Format.B8G8R8A8Unorm, Format.R8G8B8A8Unorm];

        if (requested == SwapchainEncoding.SrgbWithUnormOverlay && mutableFormatAvailable && Find(formats, srgb) is { } mutable)
            return (mutable, SwapchainEncoding.SrgbWithUnormOverlay, FormatInfo.ToggleSrgb(mutable.Format));
        if (requested == SwapchainEncoding.SrgbOnly && Find(formats, srgb) is { } srgbOnly)
            return (srgbOnly, SwapchainEncoding.SrgbOnly, srgbOnly.Format);
        if (Find(formats, unorm) is { } raw)
            return (raw, SwapchainEncoding.UnormShaderEncode, raw.Format);
        if (mutableFormatAvailable && Find(formats, srgb) is { } fallbackMutable)
            return (fallbackMutable, SwapchainEncoding.SrgbWithUnormOverlay, FormatInfo.ToggleSrgb(fallbackMutable.Format));

        foreach (var f in formats)
            if (FormatInfo.IsSrgb(f.Format))
                return (f, SwapchainEncoding.SrgbOnly, f.Format);
        return (formats[0], SwapchainEncoding.UnormShaderEncode, formats[0].Format);
    }

    private static SurfaceFormatKHR? Find(ReadOnlySpan<SurfaceFormatKHR> formats, ReadOnlySpan<Format> wanted)
    {
        foreach (var want in wanted)
            foreach (var f in formats)
                if (f.Format == want && f.ColorSpace == ColorSpaceKHR.SpaceSrgbNonlinearKhr)
                    return f;
        return null;
    }

    // ── Creation / recreation ─────────────────────────────────────────────────

    private void CreatePresentation()
    {
        _depthFormat = FindDepthFormat();
        CreateSwapchainViews();
        CreatePresentPasses();
        CreatePresentFramebuffers();
        _sceneTarget = new RenderTarget(this,
            new RenderTargetDesc("scene", [RenderTargetAttachment.Sampled(SceneColorFormat)], _depthFormat), _swapChainExtent);
        CreateTonemap();
        Log.Info($"[Vulkan] Colour pipeline: HDR {SceneColorFormat} → tonemap → {_swapChainImageFormat} ({_encoding}).");
    }

    /// <summary>After swapchain recreation (device idle): new views and framebuffers, resized scene target.</summary>
    private void RecreatePresentation()
    {
        CreateSwapchainViews();
        CreatePresentFramebuffers();
        if (_sceneTarget!.Resize(_swapChainExtent))
            WriteTonemapSet();
    }

    private void CreateSwapchainViews()
    {
        var count = _swapChainImages!.Length;
        _swapChainImageViews = new ImageView[count];
        _overlayImageViews = _encoding == SwapchainEncoding.SrgbWithUnormOverlay ? new ImageView[count] : null;
        for (var i = 0; i < count; i++)
        {
            _swapChainImageViews[i] = CreateSwapchainView(_swapChainImages[i], _swapChainImageFormat);
            if (_overlayImageViews is not null)
                _overlayImageViews[i] = CreateSwapchainView(_swapChainImages[i], _overlayFormat);
        }
    }

    private ImageView CreateSwapchainView(Image image, Format format)
    {
        var info = new ImageViewCreateInfo
        {
            SType = StructureType.ImageViewCreateInfo,
            Image = image,
            ViewType = ImageViewType.Type2D,
            Format = format,
            SubresourceRange = new ImageSubresourceRange(ImageAspectFlags.ColorBit, 0, 1, 0, 1),
        };
        _vk!.CreateImageView(_device, in info, null, out var view).Check("vkCreateImageView (swapchain)");
        return view;
    }

    private void CreatePresentPasses()
    {
        var separateOverlay = _encoding == SwapchainEncoding.SrgbWithUnormOverlay;
        _presentPass = CreateSwapchainPass(_swapChainImageFormat, AttachmentLoadOp.DontCare, ImageLayout.Undefined,
            separateOverlay ? ImageLayout.ColorAttachmentOptimal : ImageLayout.PresentSrcKhr, "present");
        _overlayPass = separateOverlay
            ? CreateSwapchainPass(_overlayFormat, AttachmentLoadOp.Load, ImageLayout.ColorAttachmentOptimal, ImageLayout.PresentSrcKhr, "overlay")
            : _presentPass;
    }

    // One colour attachment on the swapchain image. Incoming: the acquire semaphore is waited at
    // COLOR_ATTACHMENT_OUTPUT, and an overlay pass loads what the tonemap pass wrote. Outgoing: the writes and the
    // final layout transition happen-before the transfer stage, where a frame-capture copy starts.
    private RenderPass CreateSwapchainPass(Format format, AttachmentLoadOp load, ImageLayout initial, ImageLayout final, string what)
    {
        var attachment = new AttachmentDescription
        {
            Format = format,
            Samples = SampleCountFlags.Count1Bit,
            LoadOp = load,
            StoreOp = AttachmentStoreOp.Store,
            StencilLoadOp = AttachmentLoadOp.DontCare,
            StencilStoreOp = AttachmentStoreOp.DontCare,
            InitialLayout = initial,
            FinalLayout = final,
        };
        var colorRef = new AttachmentReference(0, ImageLayout.ColorAttachmentOptimal);
        var subpass = new SubpassDescription
        {
            PipelineBindPoint = PipelineBindPoint.Graphics,
            ColorAttachmentCount = 1,
            PColorAttachments = &colorRef,
        };
        var dependencies = stackalloc SubpassDependency[2];
        dependencies[0] = new SubpassDependency
        {
            SrcSubpass = Vk.SubpassExternal,
            DstSubpass = 0,
            SrcStageMask = PipelineStageFlags.ColorAttachmentOutputBit,
            SrcAccessMask = AccessFlags.ColorAttachmentWriteBit,
            DstStageMask = PipelineStageFlags.ColorAttachmentOutputBit,
            DstAccessMask = AccessFlags.ColorAttachmentReadBit | AccessFlags.ColorAttachmentWriteBit,
        };
        dependencies[1] = new SubpassDependency
        {
            SrcSubpass = 0,
            DstSubpass = Vk.SubpassExternal,
            SrcStageMask = PipelineStageFlags.ColorAttachmentOutputBit,
            SrcAccessMask = AccessFlags.ColorAttachmentWriteBit,
            DstStageMask = PipelineStageFlags.TransferBit,
            DstAccessMask = AccessFlags.TransferReadBit,
        };
        var info = new RenderPassCreateInfo
        {
            SType = StructureType.RenderPassCreateInfo,
            AttachmentCount = 1,
            PAttachments = &attachment,
            SubpassCount = 1,
            PSubpasses = &subpass,
            DependencyCount = 2,
            PDependencies = dependencies,
        };
        _vk!.CreateRenderPass(_device, in info, null, out var pass).Check($"vkCreateRenderPass ({what})");
        return pass;
    }

    private void CreatePresentFramebuffers()
    {
        _presentFramebuffers = CreateFramebuffers(_presentPass, _swapChainImageViews!);
        _overlayFramebuffers = _overlayImageViews is null ? null : CreateFramebuffers(_overlayPass, _overlayImageViews);
    }

    private Framebuffer[] CreateFramebuffers(RenderPass pass, ImageView[] views)
    {
        var framebuffers = new Framebuffer[views.Length];
        for (var i = 0; i < views.Length; i++)
        {
            var view = views[i];
            var info = new FramebufferCreateInfo
            {
                SType = StructureType.FramebufferCreateInfo,
                RenderPass = pass,
                AttachmentCount = 1,
                PAttachments = &view,
                Width = _swapChainExtent.Width,
                Height = _swapChainExtent.Height,
                Layers = 1,
            };
            _vk!.CreateFramebuffer(_device, in info, null, out framebuffers[i]).Check("vkCreateFramebuffer (swapchain)");
        }

        return framebuffers;
    }

    private void CreateTonemap()
    {
        var samplerInfo = new SamplerCreateInfo
        {
            SType = StructureType.SamplerCreateInfo,
            MagFilter = Filter.Nearest,
            MinFilter = Filter.Nearest,
            MipmapMode = SamplerMipmapMode.Nearest,
            AddressModeU = SamplerAddressMode.ClampToEdge,
            AddressModeV = SamplerAddressMode.ClampToEdge,
            AddressModeW = SamplerAddressMode.ClampToEdge,
        };
        _vk!.CreateSampler(_device, in samplerInfo, null, out _tonemapSampler).Check("vkCreateSampler (tonemap)");

        _tonemapSetLayout = PipelineBuilder.CreateSetLayout(this,
            [new DescriptorSetLayoutBinding { Binding = 0, DescriptorType = DescriptorType.CombinedImageSampler, DescriptorCount = 1, StageFlags = ShaderStageFlags.FragmentBit }],
            "tonemap");
        _tonemapPool = PipelineBuilder.CreatePool(this, 1,
            [new DescriptorPoolSize { Type = DescriptorType.CombinedImageSampler, DescriptorCount = 1 }], "tonemap");
        _tonemapSet = PipelineBuilder.AllocateSet(this, _tonemapPool, _tonemapSetLayout, "tonemap");
        WriteTonemapSet();

        _tonemapLayout = PipelineBuilder.CreateLayout(this, [_tonemapSetLayout], (uint)sizeof(TonemapPush), ShaderStageFlags.FragmentBit, "tonemap");
        _tonemapPipeline = PipelineBuilder.Create(this, new PipelineState(), _tonemapLayout, _presentPass,
            "Shaders/Post/Fullscreen.vk.vert.spv", "Shaders/Post/Tonemap.vk.frag.spv", [], [], "tonemap");
    }

    private void WriteTonemapSet() =>
        PipelineBuilder.WriteImage(this, _tonemapSet, 0, new DescriptorImageInfo
        {
            Sampler = _tonemapSampler,
            ImageView = _sceneTarget!.GetColor(0).View,
            ImageLayout = ImageLayout.ShaderReadOnlyOptimal,
        });

    private struct TonemapPush
    {
        public float Exposure;
        public uint EncodeSrgb; // 1 when the swapchain view does not encode (UNORM)
    }

    // ── Per-frame passes ──────────────────────────────────────────────────────

    /// <summary>Begins the HDR scene pass (cleared to the clear colour, converted to linear).</summary>
    public void BeginRenderPass()
    {
        if (!_frameStarted || _passState != PassState.None) return;

        var clears = stackalloc ClearValue[2];
        clears[0] = new ClearValue
        {
            Color = new ClearColorValue(ColorSpace.SrgbToLinear(_clearR), ColorSpace.SrgbToLinear(_clearG),
                ColorSpace.SrgbToLinear(_clearB), _clearA),
        };
        clears[1] = new ClearValue { DepthStencil = new ClearDepthStencilValue(1f, 0) };
        _sceneTarget!.Begin(_commandBuffers[_currentFrame], new ReadOnlySpan<ClearValue>(clears, 2));
        _passState = PassState.Scene;
    }

    /// <summary>
    /// Ends the scene pass, tonemaps it into the swapchain image and begins the overlay pass (UI drawn after
    /// tonemapping, in the swapchain's encoding). Idempotent within a frame.
    /// </summary>
    public void BeginOverlayPass()
    {
        if (!_frameStarted || _passState == PassState.Overlay) return;
        if (_passState == PassState.None)
            BeginRenderPass(); // nothing was drawn: still clear, so the frame shows the clear colour

        var vk = _vk!;
        var cb = _commandBuffers[_currentFrame];
        vk.CmdEndRenderPass(cb);

        // Offscreen overlay work (UI layers, clip masks, filters) between the scene pass and the tonemap.
        foreach (var overlay in _overlayRenderers)
            overlay.RecordOffscreen(cb);

        BeginSwapchainPass(cb, _presentPass, _presentFramebuffers![_currentImageIndex]);
        vk.CmdBindPipeline(cb, PipelineBindPoint.Graphics, _tonemapPipeline);
        var set = _tonemapSet;
        vk.CmdBindDescriptorSets(cb, PipelineBindPoint.Graphics, _tonemapLayout, 0, 1, &set, 0, null);
        var push = new TonemapPush
        {
            Exposure = _exposure,
            EncodeSrgb = FormatInfo.IsSrgb(_swapChainImageFormat) ? 0u : 1u,
        };
        vk.CmdPushConstants(cb, _tonemapLayout, ShaderStageFlags.FragmentBit, 0, (uint)sizeof(TonemapPush), &push);
        PipelineBuilder.SetViewport(vk, cb, _swapChainExtent, flipY: false);
        vk.CmdDraw(cb, 3, 1, 0, 0);

        if (_overlayPass.Handle != _presentPass.Handle)
        {
            vk.CmdEndRenderPass(cb);
            BeginSwapchainPass(cb, _overlayPass, _overlayFramebuffers![_currentImageIndex]);
        }

        _passState = PassState.Overlay;
        foreach (var overlay in _overlayRenderers)
            overlay.RecordOverlay(cb);
    }

    // ── Overlay renderers (game UI) ───────────────────────────────────────────

    private readonly List<IOverlayRenderer> _overlayRenderers = [];
    private Format _stencilFormat;

    public void AddOverlayRenderer(IOverlayRenderer renderer)
    {
        ArgumentNullException.ThrowIfNull(renderer);
        if (!_overlayRenderers.Contains(renderer))
            _overlayRenderers.Add(renderer);
    }

    public bool RemoveOverlayRenderer(IOverlayRenderer renderer) => _overlayRenderers.Remove(renderer);

    /// <summary>A stencil-capable attachment format: S8 when supported (smallest), else a packed depth/stencil format.</summary>
    public Format StencilFormat
    {
        get
        {
            if (_stencilFormat != Format.Undefined)
                return _stencilFormat;
            foreach (var format in (ReadOnlySpan<Format>)[Format.S8Uint, Format.D24UnormS8Uint, Format.D32SfloatS8Uint])
            {
                _vk!.GetPhysicalDeviceFormatProperties(_physicalDevice, format, out var props);
                if ((props.OptimalTilingFeatures & FormatFeatureFlags.DepthStencilAttachmentBit) != 0)
                    return _stencilFormat = format;
            }

            throw new VulkanException("[Vulkan] No stencil attachment format is supported.");
        }
    }

    private void BeginSwapchainPass(CommandBuffer cb, RenderPass pass, Framebuffer framebuffer)
    {
        var info = new RenderPassBeginInfo
        {
            SType = StructureType.RenderPassBeginInfo,
            RenderPass = pass,
            Framebuffer = framebuffer,
            RenderArea = new Rect2D { Extent = _swapChainExtent },
        };
        _vk!.CmdBeginRenderPass(cb, &info, SubpassContents.Inline);
    }

    /// <summary>EndFrame: finish the pass sequence (tonemap if nobody did) and leave the image presentable.</summary>
    private void EndPasses(CommandBuffer cb)
    {
        BeginOverlayPass();
        _vk!.CmdEndRenderPass(cb);
        _passState = PassState.None;
    }

    // ── Teardown ──────────────────────────────────────────────────────────────

    /// <summary>Swapchain views and framebuffers (device idle).</summary>
    private void DestroySwapchainViews()
    {
        DestroyAll(ref _presentFramebuffers);
        DestroyAll(ref _overlayFramebuffers);
        DestroyAll(ref _swapChainImageViews);
        DestroyAll(ref _overlayImageViews);
    }

    private void DestroyAll(ref Framebuffer[]? framebuffers)
    {
        if (framebuffers is null) return;
        foreach (var fb in framebuffers)
            _vk!.DestroyFramebuffer(_device, fb, null);
        framebuffers = null;
    }

    private void DestroyAll(ref ImageView[]? views)
    {
        if (views is null) return;
        foreach (var view in views)
            _vk!.DestroyImageView(_device, view, null);
        views = null;
    }

    /// <summary>Everything the colour pipeline owns (device idle; deletions are flushed afterwards).</summary>
    private void DestroyPresentation()
    {
        DestroySwapchainViews();
        _sceneTarget?.Dispose();
        _sceneTarget = null;

        var vk = _vk!;
        vk.DestroyPipeline(_device, _tonemapPipeline, null);
        vk.DestroyPipelineLayout(_device, _tonemapLayout, null);
        vk.DestroyDescriptorPool(_device, _tonemapPool, null);
        vk.DestroyDescriptorSetLayout(_device, _tonemapSetLayout, null);
        vk.DestroySampler(_device, _tonemapSampler, null);
        if (_overlayPass.Handle != _presentPass.Handle)
            vk.DestroyRenderPass(_device, _overlayPass, null);
        vk.DestroyRenderPass(_device, _presentPass, null);
        _overlayPass = _presentPass = default;
    }

    /// <summary>Device extensions for the colour pipeline: the mutable-format swapchain when the device has it.</summary>
    private void AddPresentationExtensions(List<string> extensions)
    {
        _requestedEncoding = ParseEncoding(Environment.GetEnvironmentVariable(EncodingVariable));
        // Only enabled when the sRGB + UNORM-view swapchain is asked for (see ChooseSurfaceFormat).
        _mutableFormatAvailable = _requestedEncoding == SwapchainEncoding.SrgbWithUnormOverlay &&
                                  IsDeviceExtensionAvailable(_physicalDevice, MutableFormatExtension) &&
                                  IsDeviceExtensionAvailable(_physicalDevice, ImageFormatListExtension);
        if (!_mutableFormatAvailable)
            return;
        extensions.Add(MutableFormatExtension);
        extensions.Add(ImageFormatListExtension);
    }

    /// <summary>Adds MUTABLE_FORMAT and the sRGB/UNORM view-format list when the encoding needs a UNORM view.</summary>
    private void ApplyMutableFormat(ref SwapchainCreateInfoKHR createInfo, ImageFormatListCreateInfo* formatList, Format* viewFormats)
    {
        if (_encoding != SwapchainEncoding.SrgbWithUnormOverlay)
            return;
        viewFormats[0] = createInfo.ImageFormat;
        viewFormats[1] = _overlayFormat;
        *formatList = new ImageFormatListCreateInfo
        {
            SType = StructureType.ImageFormatListCreateInfo,
            ViewFormatCount = 2,
            PViewFormats = viewFormats,
        };
        createInfo.Flags |= SwapchainCreateFlagsKHR.MutableFormatBitKhr;
        createInfo.PNext = formatList;
    }
}
