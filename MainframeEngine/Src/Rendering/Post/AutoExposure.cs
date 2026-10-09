using Silk.NET.Vulkan;

namespace MainframeEngine;

/// <summary>
/// Auto exposure (ADR 0154) in raster passes, never read back to the CPU: the HDR scene's log2 luminance at 64 × 64
/// (<c>AutoExposureLuminance</c>, 16 taps per texel), reduced by 4 × 4 means to 16², 4² and 1 × 1
/// (<c>AutoExposureReduce</c>), then the adapted luminance (<c>AutoExposureAdapt</c>: clamped, approached from last
/// frame's value) into a 1 × 1 <c>R32_SFLOAT</c> image that the tonemap and the glow's first level read; a last pass
/// copies it into the "previous" image for the next frame. A <see cref="PostStage.BeforeTonemap"/> effect (ADR 0163),
/// enabled whenever the post tonemap pass runs (it binds the adapted luminance even with auto exposure off); allocates
/// nothing per frame. Nothing is blended, so no format needs blending support.
/// <para>
/// <see cref="AutoExposureMode.Histogram"/> (ADR 0177) replaces the reduce and adapt passes after the 64 × 64 log
/// luminance: per-row histograms (64 bins × 64 rows <c>R32_SFLOAT</c>, <c>AutoExposureHistogram</c>: each texel the
/// metering weight of its row's texels in its bin), their column sums (64 × 1, <c>AutoExposureHistogramSum</c>), then
/// <c>AutoExposureHistogramAdapt</c>: the band average between the low and high percentiles and highlight protection
/// (<see cref="AutoExposureHistogram"/> is the C# reference), clamped and adapted into the same 1 × 1 image.
/// </para>
/// </summary>
internal sealed unsafe class AutoExposure() : PostEffect("auto exposure", PostStage.BeforeTonemap, PostEffectOrder.AutoExposure)
{
    public const int LuminanceSize = 64;
    private const int ReduceLevels = 3; // 16, 4, 1
    private const Format LogFormat = Format.R16Sfloat;
    private const Format AdaptedFormat = Format.R32Sfloat;
    private const Format HistogramFormat = Format.R32Sfloat;

    private IVulkanContext _ctx = null!;
    private readonly RenderTarget[] _levels = new RenderTarget[1 + ReduceLevels]; // 64², 16², 4², 1² (log2 luminance)
    private RenderTarget _adapted = null!;  // what the tonemap reads
    private RenderTarget _previous = null!; // last frame's _adapted
    private RenderTarget _histogramRows = null!; // ADR 0177: 64 bins × 64 rows
    private RenderTarget _histogram = null!;     // 64 bins × 1
    private Sampler _sampler;
    private DescriptorSetLayout _setLayout;
    private DescriptorPool _pool;
    private readonly DescriptorSet[] _levelSets = new DescriptorSet[1 + ReduceLevels]; // source of level k (0: the scene)
    private DescriptorSet _adaptSet; // 0: the 1 × 1 mean, 1: previous
    private DescriptorSet _copySet;  // 0: adapted
    private DescriptorSet _rowsSet;           // 0: the 64² log luminance
    private DescriptorSet _sumSet;            // 0: the per-row histograms
    private DescriptorSet _histogramAdaptSet; // 0: the histogram, 1: previous
    private PipelineLayout _layout;
    private PipelineLayout _histogramLayout;
    private Pipeline _luminancePipeline;
    private Pipeline _reducePipeline;
    private Pipeline _adaptPipeline;
    private Pipeline _copyPipeline;
    private Pipeline _histogramPipeline;
    private Pipeline _histogramSumPipeline;
    private Pipeline _histogramAdaptPipeline;
    private bool _needsClear = true;
    private bool _wasEnabled;
    private QueryPool _timestamps;
    private readonly bool[] _timestampsPending = new bool[IVulkanContext.MaxFramesInFlight];
    private double _timestampPeriodNs;

    /// <summary>GPU time of the measure-and-adapt passes in a recent frame, in milliseconds (0 while off or untimed).</summary>
    public double LastGpuMilliseconds { get; private set; }

    /// <summary>Runs whenever the post tonemap pass does: the tonemap and glow bind its adapted luminance either way.</summary>
    public override bool IsEnabled(in PostEffectSettings settings) => settings.PostTonemap;

    protected override void OnCreate(PostEffectContext context)
    {
        var ctx = context.Vulkan;
        var sceneView = context.Scene.Color;
        _ctx = ctx;
        var size = (uint)LuminanceSize;
        for (var k = 0; k < _levels.Length; k++, size /= 4)
            _levels[k] = new RenderTarget(ctx, new RenderTargetDesc($"auto exposure {size}²", [RenderTargetAttachment.Sampled(LogFormat)], null), new Extent2D(size, size));
        _adapted = new RenderTarget(ctx, new RenderTargetDesc("auto exposure adapted", [RenderTargetAttachment.Sampled(AdaptedFormat)], null), new Extent2D(1, 1));
        _previous = new RenderTarget(ctx, new RenderTargetDesc("auto exposure previous", [RenderTargetAttachment.Sampled(AdaptedFormat)], null), new Extent2D(1, 1));
        const uint bins = AutoExposureHistogram.BinCount;
        _histogramRows = new RenderTarget(ctx, new RenderTargetDesc("auto exposure histogram rows", [RenderTargetAttachment.Sampled(HistogramFormat)], null),
            new Extent2D(bins, LuminanceSize));
        _histogram = new RenderTarget(ctx, new RenderTargetDesc("auto exposure histogram", [RenderTargetAttachment.Sampled(HistogramFormat)], null),
            new Extent2D(bins, 1));

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
        const uint sets = 1 + ReduceLevels + 2 + 3;
        _pool = PipelineBuilder.CreatePool(ctx, sets, [new DescriptorPoolSize { Type = DescriptorType.CombinedImageSampler, DescriptorCount = 2 * sets }], "auto exposure");
        for (var k = 0; k < _levelSets.Length; k++)
            _levelSets[k] = PipelineBuilder.AllocateSet(ctx, _pool, _setLayout, "auto exposure level");
        _adaptSet = PipelineBuilder.AllocateSet(ctx, _pool, _setLayout, "auto exposure adapt");
        _copySet = PipelineBuilder.AllocateSet(ctx, _pool, _setLayout, "auto exposure copy");
        _rowsSet = PipelineBuilder.AllocateSet(ctx, _pool, _setLayout, "auto exposure histogram rows");
        _sumSet = PipelineBuilder.AllocateSet(ctx, _pool, _setLayout, "auto exposure histogram sum");
        _histogramAdaptSet = PipelineBuilder.AllocateSet(ctx, _pool, _setLayout, "auto exposure histogram adapt");

        WriteSceneSet(sceneView);
        for (var k = 1; k < _levelSets.Length; k++)
            WriteSet(_levelSets[k], View(_levels[k - 1]), View(_previous));
        WriteSet(_adaptSet, View(_levels[^1]), View(_previous));
        WriteSet(_copySet, View(_adapted), View(_adapted));
        WriteSet(_rowsSet, View(_levels[0]), View(_previous));
        WriteSet(_sumSet, View(_histogramRows), View(_previous));
        WriteSet(_histogramAdaptSet, View(_histogram), View(_previous));

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

        _histogramLayout = PipelineBuilder.CreateLayout(ctx, [_setLayout], (uint)sizeof(HistogramPush), ShaderStageFlags.FragmentBit, "auto exposure histogram");
        _histogramPipeline = PipelineBuilder.Create(ctx, new PipelineState(), _histogramLayout, _histogramRows.RenderPass,
            vertex, "Shaders/Post/AutoExposureHistogram.vk.frag.spv", [], [], "auto exposure histogram rows");
        _histogramSumPipeline = PipelineBuilder.Create(ctx, new PipelineState(), _histogramLayout, _histogram.RenderPass,
            vertex, "Shaders/Post/AutoExposureHistogramSum.vk.frag.spv", [], [], "auto exposure histogram sum");
        _histogramAdaptPipeline = PipelineBuilder.Create(ctx, new PipelineState(), _histogramLayout, _adapted.RenderPass,
            vertex, "Shaders/Post/AutoExposureHistogramAdapt.vk.frag.spv", [], [], "auto exposure histogram adapt");
        CreateTimestamps();
    }

    /// <summary>The adapted luminance (1 × 1 <c>R32_SFLOAT</c>, read with <c>Load</c>) for the tonemap and glow sets.</summary>
    public DescriptorImageInfo AdaptedDescriptor => new()
    {
        Sampler = _sampler,
        ImageView = View(_adapted),
        ImageLayout = ImageLayout.ShaderReadOnlyOptimal,
    };

    /// <summary>After a swapchain resize (device idle): the first pass reads the new scene image.</summary>
    protected override void OnResize(PostEffectContext context) => WriteSceneSet(context.Scene.Color);

    protected override void OnRecord(PostEffectContext context) =>
        Record(context.CommandBuffer, context.Settings.World, context.DeltaTime, context.Exposure);

    /// <summary>
    /// Measures and adapts when <paramref name="settings"/> enable auto exposure; the first frame after it is enabled
    /// snaps to the measured value. The first call also clears every image so the tonemap can always bind them.
    /// <paramref name="exposure"/> is the frame's manual exposure, which highlight protection accounts for.
    /// </summary>
    private void Record(CommandBuffer cb, in PostProcessSettings settings, float deltaTime, float exposure)
    {
        if (_needsClear)
        {
            foreach (var level in _levels)
            {
                level.Begin(cb, default);
                level.End(cb);
            }

            _histogramRows.Begin(cb, default);
            _histogramRows.End(cb);
            _histogram.Begin(cb, default);
            _histogram.End(cb);

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
            LastGpuMilliseconds = 0;
            return;
        }

        var slot = _ctx.FrameSlot;
        BeginGpuTiming(cb, slot);
        var (minLuminance, maxLuminance) = AutoExposureHistogram.LuminanceRange(settings);
        var blend = _wasEnabled ? settings.AutoExposureBlend(deltaTime) : 1f;
        _wasEnabled = true;

        var push = new AdaptPush { MinLuminance = minLuminance, MaxLuminance = maxLuminance, Blend = blend };
        Pass(cb, _levels[0], _luminancePipeline, _layout, _levelSets[0], &push, sizeof(AdaptPush));
        if (settings.AutoExposureMode == AutoExposureMode.Histogram)
        {
            var histogram = new HistogramPush
            {
                LogMin = settings.AutoExposureHistogramLogMin,
                LogMax = settings.AutoExposureHistogramLogMax,
                Metering = (uint)settings.AutoExposureMetering,
                LowPercent = settings.AutoExposureLowPercent,
                HighPercent = settings.AutoExposureHighPercent,
                HighlightPercent = settings.AutoExposureHighlightPercent,
                HighlightLimit = AutoExposureHistogram.HighlightLimit(settings, exposure),
                MinLuminance = minLuminance,
                MaxLuminance = maxLuminance,
                Blend = blend,
            };
            Pass(cb, _histogramRows, _histogramPipeline, _histogramLayout, _rowsSet, &histogram, sizeof(HistogramPush));
            Pass(cb, _histogram, _histogramSumPipeline, _histogramLayout, _sumSet, &histogram, sizeof(HistogramPush));
            Pass(cb, _adapted, _histogramAdaptPipeline, _histogramLayout, _histogramAdaptSet, &histogram, sizeof(HistogramPush));
        }
        else
        {
            for (var k = 1; k < _levels.Length; k++)
                Pass(cb, _levels[k], _reducePipeline, _layout, _levelSets[k], &push, sizeof(AdaptPush));
            Pass(cb, _adapted, _adaptPipeline, _layout, _adaptSet, &push, sizeof(AdaptPush));
        }

        Pass(cb, _previous, _copyPipeline, _layout, _copySet, &push, sizeof(AdaptPush));
        EndGpuTiming(cb, slot);
    }

    // Two timestamps per frame slot around the passes (SsaoEffect's pattern), when the graphics queue has them.
    private void CreateTimestamps()
    {
        _ctx.Vk.GetPhysicalDeviceProperties(_ctx.PhysicalDevice, out var props);
        if (!props.Limits.TimestampComputeAndGraphics || props.Limits.TimestampPeriod <= 0f)
            return;
        var info = new QueryPoolCreateInfo
        {
            SType = StructureType.QueryPoolCreateInfo,
            QueryType = QueryType.Timestamp,
            QueryCount = 2 * IVulkanContext.MaxFramesInFlight,
        };
        _ctx.Vk.CreateQueryPool(_ctx.Device, in info, null, out _timestamps).Check("vkCreateQueryPool (auto exposure timing)");
        _timestampPeriodNs = props.Limits.TimestampPeriod;
    }

    // Reads the slot's previous timestamps (its fence was waited) and starts this frame's.
    private void BeginGpuTiming(CommandBuffer cb, int slot)
    {
        if (_timestamps.Handle == 0)
            return;
        if (_timestampsPending[slot])
        {
            var data = stackalloc ulong[2];
            if (_ctx.Vk.GetQueryPoolResults(_ctx.Device, _timestamps, (uint)slot * 2, 2, 16, data, 8, QueryResultFlags.Result64Bit) == Result.Success)
                LastGpuMilliseconds = (data[1] - data[0]) * _timestampPeriodNs / 1e6;
            _timestampsPending[slot] = false;
        }

        _ctx.Vk.CmdResetQueryPool(cb, _timestamps, (uint)slot * 2, 2);
        _ctx.Vk.CmdWriteTimestamp(cb, PipelineStageFlags.TopOfPipeBit, _timestamps, (uint)slot * 2);
    }

    private void EndGpuTiming(CommandBuffer cb, int slot)
    {
        if (_timestamps.Handle == 0)
            return;
        _ctx.Vk.CmdWriteTimestamp(cb, PipelineStageFlags.BottomOfPipeBit, _timestamps, (uint)slot * 2 + 1);
        _timestampsPending[slot] = true;
    }

    private void Pass(CommandBuffer cb, RenderTarget target, Pipeline pipeline, PipelineLayout layout, DescriptorSet set, void* push, int pushSize)
    {
        var vk = _ctx.Vk;
        target.Begin(cb, default);
        vk.CmdBindPipeline(cb, PipelineBindPoint.Graphics, pipeline);
        vk.CmdBindDescriptorSets(cb, PipelineBindPoint.Graphics, layout, 0, 1, &set, 0, null);
        vk.CmdPushConstants(cb, layout, ShaderStageFlags.FragmentBit, 0, (uint)pushSize, push);
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
    protected override void OnDispose()
    {
        var vk = _ctx.Vk;
        vk.DestroyPipeline(_ctx.Device, _luminancePipeline, null);
        vk.DestroyPipeline(_ctx.Device, _reducePipeline, null);
        vk.DestroyPipeline(_ctx.Device, _adaptPipeline, null);
        vk.DestroyPipeline(_ctx.Device, _copyPipeline, null);
        vk.DestroyPipeline(_ctx.Device, _histogramPipeline, null);
        vk.DestroyPipeline(_ctx.Device, _histogramSumPipeline, null);
        vk.DestroyPipeline(_ctx.Device, _histogramAdaptPipeline, null);
        vk.DestroyPipelineLayout(_ctx.Device, _layout, null);
        vk.DestroyPipelineLayout(_ctx.Device, _histogramLayout, null);
        vk.DestroyDescriptorPool(_ctx.Device, _pool, null);
        vk.DestroyDescriptorSetLayout(_ctx.Device, _setLayout, null);
        vk.DestroySampler(_ctx.Device, _sampler, null);
        if (_timestamps.Handle != 0)
            vk.DestroyQueryPool(_ctx.Device, _timestamps, null);
        foreach (var level in _levels)
            level.Dispose();
        _adapted.Dispose();
        _previous.Dispose();
        _histogramRows.Dispose();
        _histogram.Dispose();
    }

    // AutoExposureAdapt.vk.frag's push block (the luminance, reduce and copy passes declare none).
    private struct AdaptPush
    {
        public float MinLuminance;
        public float MaxLuminance;
        public float Blend;
    }

    // include/auto_exposure.slang's HistogramParams: the push block of the three histogram passes (ADR 0177).
    private struct HistogramPush
    {
        public float LogMin;
        public float LogMax;
        public uint Metering;
        public float LowPercent;
        public float HighPercent;
        public float HighlightPercent;
        public float HighlightLimit;
        public float MinLuminance;
        public float MaxLuminance;
        public float Blend;
    }
}
