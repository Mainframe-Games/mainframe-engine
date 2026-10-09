using Silk.NET.Vulkan;

namespace MainframeEngine;

/// <summary>
/// Auto exposure (ADR 0154) in raster passes, never read back to the CPU: the HDR scene's log2 luminance at 64 × 64
/// (<c>AutoExposureLuminance</c>, 16 taps per texel), reduced by 4 × 4 means to 16², 4² and 1 × 1
/// (<c>AutoExposureReduce</c>), then the adapted luminance (<c>AutoExposureAdapt</c>: clamped, approached from last
/// frame's value) into a 1 × 1 <c>R32_SFLOAT</c> image that the tonemap and the glow's first level read; a last pass
/// copies it into the "previous" image for the next frame. Recorded between the scene pass and the tonemap, with no render
/// pass active; allocates nothing per frame. Nothing is blended, so no format needs blending support.
/// </summary>
internal sealed unsafe class AutoExposure : IDisposable
{
    public const int LuminanceSize = 64;
    private const int ReduceLevels = 3; // 16, 4, 1
    private const Format LogFormat = Format.R16Sfloat;
    private const Format AdaptedFormat = Format.R32Sfloat;

    private readonly IVulkanContext _ctx;
    private readonly RenderTarget[] _levels = new RenderTarget[1 + ReduceLevels]; // 64², 16², 4², 1² (log2 luminance)
    private readonly RenderTarget _adapted;  // what the tonemap reads
    private readonly RenderTarget _previous; // last frame's _adapted
    private readonly Sampler _sampler;
    private readonly DescriptorSetLayout _setLayout;
    private readonly DescriptorPool _pool;
    private readonly DescriptorSet[] _levelSets = new DescriptorSet[1 + ReduceLevels]; // source of level k (0: the scene)
    private readonly DescriptorSet _adaptSet; // 0: the 1 × 1 mean, 1: previous
    private readonly DescriptorSet _copySet;  // 0: adapted
    private readonly PipelineLayout _layout;
    private readonly Pipeline _luminancePipeline;
    private readonly Pipeline _reducePipeline;
    private readonly Pipeline _adaptPipeline;
    private readonly Pipeline _copyPipeline;
    private bool _needsClear = true;
    private bool _wasEnabled;
    private bool _disposed;

    public AutoExposure(IVulkanContext ctx, ImageView sceneView)
    {
        _ctx = ctx;
        var size = (uint)LuminanceSize;
        for (var k = 0; k < _levels.Length; k++, size /= 4)
            _levels[k] = new RenderTarget(ctx, new RenderTargetDesc($"auto exposure {size}²", [RenderTargetAttachment.Sampled(LogFormat)], null), new Extent2D(size, size));
        _adapted = new RenderTarget(ctx, new RenderTargetDesc("auto exposure adapted", [RenderTargetAttachment.Sampled(AdaptedFormat)], null), new Extent2D(1, 1));
        _previous = new RenderTarget(ctx, new RenderTargetDesc("auto exposure previous", [RenderTargetAttachment.Sampled(AdaptedFormat)], null), new Extent2D(1, 1));

        // Every pass reads texels (Load): nearest filtering, which every format supports.
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
        ctx.Vk.CreateSampler(ctx.Device, in samplerInfo, null, out _sampler).Check("vkCreateSampler (auto exposure)");

        _setLayout = PipelineBuilder.CreateSetLayout(ctx,
        [
            new DescriptorSetLayoutBinding { Binding = 0, DescriptorType = DescriptorType.CombinedImageSampler, DescriptorCount = 1, StageFlags = ShaderStageFlags.FragmentBit },
            new DescriptorSetLayoutBinding { Binding = 1, DescriptorType = DescriptorType.CombinedImageSampler, DescriptorCount = 1, StageFlags = ShaderStageFlags.FragmentBit },
        ], "auto exposure");
        const uint sets = 1 + ReduceLevels + 2;
        _pool = PipelineBuilder.CreatePool(ctx, sets, [new DescriptorPoolSize { Type = DescriptorType.CombinedImageSampler, DescriptorCount = 2 * sets }], "auto exposure");
        for (var k = 0; k < _levelSets.Length; k++)
            _levelSets[k] = PipelineBuilder.AllocateSet(ctx, _pool, _setLayout, "auto exposure level");
        _adaptSet = PipelineBuilder.AllocateSet(ctx, _pool, _setLayout, "auto exposure adapt");
        _copySet = PipelineBuilder.AllocateSet(ctx, _pool, _setLayout, "auto exposure copy");

        WriteSceneSet(sceneView);
        for (var k = 1; k < _levelSets.Length; k++)
            WriteSet(_levelSets[k], View(_levels[k - 1]), View(_previous));
        WriteSet(_adaptSet, View(_levels[^1]), View(_previous));
        WriteSet(_copySet, View(_adapted), View(_adapted));

        _layout = PipelineBuilder.CreateLayout(ctx, [_setLayout], (uint)sizeof(AdaptPush), ShaderStageFlags.FragmentBit, "auto exposure");
        const string vertex = "Shaders/Post/Fullscreen.vk.vert.spv";
        _luminancePipeline = PipelineBuilder.Create(ctx, new PipelineState(), _layout, _levels[0].RenderPass,
            vertex, "Shaders/Post/AutoExposureLuminance.vk.frag.spv", [], [], "auto exposure luminance");
        _reducePipeline = PipelineBuilder.Create(ctx, new PipelineState(), _layout, _levels[1].RenderPass,
            vertex, "Shaders/Post/AutoExposureReduce.vk.frag.spv", [], [], "auto exposure reduce");
        _adaptPipeline = PipelineBuilder.Create(ctx, new PipelineState(), _layout, _adapted.RenderPass,
            vertex, "Shaders/Post/AutoExposureAdapt.vk.frag.spv", [], [], "auto exposure adapt");
        _copyPipeline = PipelineBuilder.Create(ctx, new PipelineState(), _layout, _previous.RenderPass,
            vertex, "Shaders/Post/AutoExposureReduce.vk.frag.spv", [], [], "auto exposure copy");
    }

    /// <summary>The adapted luminance (1 × 1 <c>R32_SFLOAT</c>, read with <c>Load</c>) for the tonemap and glow sets.</summary>
    public DescriptorImageInfo AdaptedDescriptor => new()
    {
        Sampler = _sampler,
        ImageView = View(_adapted),
        ImageLayout = ImageLayout.ShaderReadOnlyOptimal,
    };

    /// <summary>After a swapchain resize (device idle): the first pass reads the new scene image.</summary>
    public void Resize(ImageView sceneView) => WriteSceneSet(sceneView);

    /// <summary>
    /// Measures and adapts when <paramref name="settings"/> enable auto exposure; the first frame after it is enabled
    /// snaps to the measured value. The first call also clears every image so the tonemap can always bind them.
    /// </summary>
    public void Record(CommandBuffer cb, in PostProcessSettings settings, float deltaTime)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_needsClear)
        {
            foreach (var level in _levels)
            {
                level.Begin(cb, default);
                level.End(cb);
            }

            Span<ClearValue> one = [new ClearValue { Color = new ClearColorValue(1f, 0f, 0f, 1f) }];
            _adapted.Begin(cb, one);
            _adapted.End(cb);
            _previous.Begin(cb, one);
            _previous.End(cb);
            _needsClear = false;
        }

        if (!settings.AutoExposureEnabled)
        {
            _wasEnabled = false;
            return;
        }

        var push = new AdaptPush
        {
            MinLuminance = MathF.Max(settings.AutoExposureMinLuminance, 1e-6f),
            MaxLuminance = MathF.Max(settings.AutoExposureMaxLuminance, MathF.Max(settings.AutoExposureMinLuminance, 1e-6f)),
            Blend = _wasEnabled ? settings.AutoExposureBlend(deltaTime) : 1f,
        };
        _wasEnabled = true;

        Pass(cb, _levels[0], _luminancePipeline, _levelSets[0], ref push);
        for (var k = 1; k < _levels.Length; k++)
            Pass(cb, _levels[k], _reducePipeline, _levelSets[k], ref push);
        Pass(cb, _adapted, _adaptPipeline, _adaptSet, ref push);
        Pass(cb, _previous, _copyPipeline, _copySet, ref push);
    }

    private void Pass(CommandBuffer cb, RenderTarget target, Pipeline pipeline, DescriptorSet set, ref AdaptPush push)
    {
        var vk = _ctx.Vk;
        target.Begin(cb, default);
        vk.CmdBindPipeline(cb, PipelineBindPoint.Graphics, pipeline);
        vk.CmdBindDescriptorSets(cb, PipelineBindPoint.Graphics, _layout, 0, 1, &set, 0, null);
        fixed (AdaptPush* p = &push)
            vk.CmdPushConstants(cb, _layout, ShaderStageFlags.FragmentBit, 0, (uint)sizeof(AdaptPush), p);
        PipelineBuilder.SetViewport(vk, cb, target.Extent, flipY: false);
        vk.CmdDraw(cb, 3, 1, 0, 0);
        target.End(cb);
    }

    private static ImageView View(RenderTarget target) => target.GetColor(0).View;

    private void WriteSceneSet(ImageView sceneView) => WriteSet(_levelSets[0], sceneView, View(_previous));

    private void WriteSet(DescriptorSet set, ImageView binding0, ImageView binding1)
    {
        PipelineBuilder.WriteImage(_ctx, set, 0, new DescriptorImageInfo { Sampler = _sampler, ImageView = binding0, ImageLayout = ImageLayout.ShaderReadOnlyOptimal });
        PipelineBuilder.WriteImage(_ctx, set, 1, new DescriptorImageInfo { Sampler = _sampler, ImageView = binding1, ImageLayout = ImageLayout.ShaderReadOnlyOptimal });
    }

    /// <summary>Destroys everything (the caller has waited for the device to be idle).</summary>
    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        var vk = _ctx.Vk;
        vk.DestroyPipeline(_ctx.Device, _luminancePipeline, null);
        vk.DestroyPipeline(_ctx.Device, _reducePipeline, null);
        vk.DestroyPipeline(_ctx.Device, _adaptPipeline, null);
        vk.DestroyPipeline(_ctx.Device, _copyPipeline, null);
        vk.DestroyPipelineLayout(_ctx.Device, _layout, null);
        vk.DestroyDescriptorPool(_ctx.Device, _pool, null);
        vk.DestroyDescriptorSetLayout(_ctx.Device, _setLayout, null);
        vk.DestroySampler(_ctx.Device, _sampler, null);
        foreach (var level in _levels)
            level.Dispose();
        _adapted.Dispose();
        _previous.Dispose();
    }

    // AutoExposureAdapt.vk.frag's push block (the other passes declare none).
    private struct AdaptPush
    {
        public float MinLuminance;
        public float MaxLuminance;
        public float Blend;
    }
}
