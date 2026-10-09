using System.Numerics;
using Silk.NET.Vulkan;

namespace MainframeEngine;

/// <summary>
/// Screen-space ambient occlusion (ADR 0165): ground-truth ambient occlusion (GTAO, Jimenez et al. 2016) as an
/// <see cref="PostStage.AfterPrepass"/> effect, enabled by <see cref="PostProcessSettings.SsaoEnabled"/>. Three fullscreen
/// raster passes after the depth prepass, before the lit scene pass:
/// <list type="number">
/// <item><c>Post/Gtao</c> at half resolution: per texel the view position and a normal rebuilt from the prepass depth,
/// <see cref="Slices"/> slice directions (rotated per texel by a 4 × 4 pattern, and per frame only while TAA jitters the
/// projection) with <see cref="StepsPerSide"/> samples each way out to <see cref="PostProcessSettings.SsaoRadius"/> metres
/// → visibility and linear depth (<c>R16G16_SFLOAT</c>).</item>
/// <item><c>Post/GtaoDenoise</c> twice at half resolution: 4 × 4 depth-aware bilateral blurs (windows −1..2, then −2..1)
/// that cancel the pattern, ping-ponging through the GTAO target.</item>
/// <item><c>Post/GtaoUpsample</c> at full resolution: a joint bilateral upsample against the full depth, then
/// intensity and power → <c>R8G8B8A8_UNORM</c> (r AO; g, b, a reserved for the contact shadow and a bent normal).</item>
/// </list>
/// The full-resolution image is bound at set 0 binding 5 for the main pass (<see cref="FrameContext.SetAmbientOcclusion(in DescriptorImageInfo, long, float, float)"/>,
/// in <c>OnBeginFrame</c>), with the light-affect and AO-channel settings in <see cref="FrameData.AmbientOcclusion"/>.
/// Targets come from the stack's pool. Allocates nothing per frame.
/// </summary>
internal sealed unsafe class SsaoEffect() : PostEffect("ssao", PostStage.AfterPrepass, PostEffectOrder.Ssao)
{
    /// <summary>Slice directions per half-resolution texel (× the 16 rotations the denoise averages: 32 directions).</summary>
    public const int Slices = 2;

    /// <summary>Depth samples on each side of the texel along each slice.</summary>
    public const int StepsPerSide = 4;

    /// <summary>The most the radius may cover on screen, as a fraction of the image height.</summary>
    public const float MaxRadiusFraction = 0.2f;

    /// <summary>The GTAO pass's output: visibility (r) and linear depth (g) at half resolution.</summary>
    public static readonly PostTargetDesc GtaoTarget = new("ssao gtao", Format.R16G16Sfloat, PostTargetScale.Half);

    /// <summary>The denoised half-resolution visibility and depth.</summary>
    public static readonly PostTargetDesc DenoiseTarget = new("ssao denoise", Format.R16G16Sfloat, PostTargetScale.Half);

    /// <summary>The full-resolution AO the lit shaders read (r; g, b, a reserved and 1).</summary>
    public static readonly PostTargetDesc OutputTarget = new("ssao", Format.R8G8B8A8Unorm);

    private IVulkanContext _ctx = null!;
    private RenderTarget _gtao = null!;
    private RenderTarget _denoise = null!;
    private RenderTarget _output = null!;
    private DescriptorSetLayout _setLayout;
    private DescriptorPool _pool;
    private DescriptorSet _gtaoSet;     // 0: scene depth
    private DescriptorSet _denoiseSet;  // 0: GTAO
    private DescriptorSet _denoise2Set; // 0: the first denoise (written back into the GTAO target)
    private DescriptorSet _upsampleSet; // 0: denoised (the GTAO target), 1: scene depth
    private PipelineLayout _layout;
    private Pipeline _gtaoPipeline;
    private Pipeline _denoisePipeline;
    private Pipeline _upsamplePipeline;
    private long _outputId;
    private QueryPool _timestamps;
    private readonly bool[] _timestampsPending = new bool[IVulkanContext.MaxFramesInFlight];
    private double _timestampPeriodNs;
    private ulong _timestampMask;

    public override PostEffectNeeds Needs => PostEffectNeeds.DepthPrepass;

    public override bool IsEnabled(in PostEffectSettings settings) => settings.World.SsaoEnabled;

    /// <summary>The frame whose AO <see cref="OutputView"/> holds (0: never drawn).</summary>
    public ulong DrawnFrame { get; private set; }

    /// <summary>GPU time of the four passes in the last frame whose timestamps came back (0 without timestamp support).</summary>
    public double LastGpuMilliseconds { get; private set; }

    /// <summary>The full-resolution AO (shader-read layout once a frame has recorded it).</summary>
    public ImageView OutputView => _output.GetColor(0).View;

    protected override void OnCreate(PostEffectContext context)
    {
        var ctx = context.Vulkan;
        _ctx = ctx;
        _gtao = context.Targets.Get(GtaoTarget);
        _denoise = context.Targets.Get(DenoiseTarget);
        _output = context.Targets.Get(OutputTarget);

        _setLayout = PipelineBuilder.CreateSetLayout(ctx,
        [
            new DescriptorSetLayoutBinding { Binding = 0, DescriptorType = DescriptorType.CombinedImageSampler, DescriptorCount = 1, StageFlags = ShaderStageFlags.FragmentBit },
            new DescriptorSetLayoutBinding { Binding = 1, DescriptorType = DescriptorType.CombinedImageSampler, DescriptorCount = 1, StageFlags = ShaderStageFlags.FragmentBit },
        ], "ssao");
        _pool = PipelineBuilder.CreatePool(ctx, 4, [new DescriptorPoolSize { Type = DescriptorType.CombinedImageSampler, DescriptorCount = 8 }], "ssao");
        _gtaoSet = PipelineBuilder.AllocateSet(ctx, _pool, _setLayout, "ssao gtao");
        _denoiseSet = PipelineBuilder.AllocateSet(ctx, _pool, _setLayout, "ssao denoise");
        _denoise2Set = PipelineBuilder.AllocateSet(ctx, _pool, _setLayout, "ssao denoise 2");
        _upsampleSet = PipelineBuilder.AllocateSet(ctx, _pool, _setLayout, "ssao upsample");
        WriteSets(context.Scene);

        _layout = PipelineBuilder.CreateLayout(ctx, [_setLayout], (uint)sizeof(GtaoPush), ShaderStageFlags.FragmentBit, "ssao");
        const string vertex = "Shaders/Post/Fullscreen.vk.vert.spv";
        _gtaoPipeline = PipelineBuilder.Create(ctx, new PipelineState(), _layout, _gtao.RenderPass, vertex, "Shaders/Post/Gtao.vk.frag.spv", [], [], "ssao gtao");
        _denoisePipeline = PipelineBuilder.Create(ctx, new PipelineState(), _layout, _denoise.RenderPass, vertex, "Shaders/Post/GtaoDenoise.vk.frag.spv", [], [], "ssao denoise");
        _upsamplePipeline = PipelineBuilder.Create(ctx, new PipelineState(), _layout, _output.RenderPass, vertex, "Shaders/Post/GtaoUpsample.vk.frag.spv", [], [], "ssao upsample");
        _outputId++;
        CreateTimestamps();
    }

    /// <summary>On frames with a prepass: binds the AO image for the main pass (the AfterPrepass stage writes it first).</summary>
    protected override void OnBeginFrame(PostEffectContext context)
    {
        if (!context.Scene.HasPrepass)
            return; // no AfterPrepass stage this frame: the lit shaders keep the white image
        var settings = context.Settings.World;
        var image = new DescriptorImageInfo
        {
            Sampler = context.Scene.LinearSampler,
            ImageView = OutputView,
            ImageLayout = ImageLayout.ShaderReadOnlyOptimal,
        };
        context.Vulkan.Frame.SetAmbientOcclusion(image, _outputId, settings.SsaoLightAffect, settings.SsaoAoChannelAffect);
    }

    /// <summary>After a resize (device idle): the pool resized the targets, so the sets and the binding's id change.</summary>
    protected override void OnResize(PostEffectContext context)
    {
        WriteSets(context.Scene);
        _outputId++;
    }

    protected override void OnRecord(PostEffectContext context)
    {
        if (!context.HasCamera || !context.Scene.HasPrepass)
            return;
        var push = Parameters(context.Settings.World, context.Camera.JitteredProjection, context.Scene.Extent, context.Vulkan.Frame.JitterIndex);
        var cb = context.CommandBuffer;
        var slot = _ctx.FrameSlot;
        BeginGpuTiming(cb, slot);
        Pass(cb, _gtao, _gtaoPipeline, _gtaoSet, ref push);
        push.Noise.W = -1f;
        Pass(cb, _denoise, _denoisePipeline, _denoiseSet, ref push);
        push.Noise.W = -2f;
        Pass(cb, _gtao, _denoisePipeline, _denoise2Set, ref push); // both half targets share a format: one pipeline
        Pass(cb, _output, _upsamplePipeline, _upsampleSet, ref push);
        EndGpuTiming(cb, slot);
        DrawnFrame = context.FrameNumber;
    }

    /// <summary>
    /// The passes' push block for <paramref name="settings"/>, the <paramref name="projection"/> the prepass rasterised
    /// with (row vectors, Vulkan depth), a scene of <paramref name="scene"/> pixels and the TAA jitter index
    /// <paramref name="temporalIndex"/> (0 without TAA: the rotation pattern stays still).
    /// </summary>
    internal static GtaoPush Parameters(in PostProcessSettings settings, in Matrix4x4 projection, Extent2D scene, int temporalIndex)
    {
        var sharpness = Math.Clamp(settings.SsaoSharpness, 0f, 1f);
        return new GtaoPush
        {
            Projection = new Vector4(projection.M11, projection.M22, projection.M31, projection.M32),
            Projection2 = new Vector4(projection.M41, projection.M42, projection.M33, projection.M43),
            Projection3 = new Vector4(projection.M34, projection.M44, Math.Max(1u, scene.Width), Math.Max(1u, scene.Height)),
            Shape = new Vector4(
                MathF.Max(settings.SsaoRadius, 0.01f),
                MathF.Max(MaxRadiusFraction * scene.Height, 4f),
                Math.Clamp(settings.SsaoHorizon, 0f, 1f) * MathF.PI * 0.5f,
                Math.Clamp(settings.SsaoDetail, 0f, 5f)),
            Filter = new Vector4(
                MathF.Max(settings.SsaoIntensity, 0f),
                MathF.Max(settings.SsaoPower, 0.01f),
                DenoiseTolerance(sharpness),
                UpsampleTolerance(sharpness)),
            Noise = new Vector4(Math.Max(temporalIndex, 0), Slices, StepsPerSide, -1f),
        };
    }

    /// <summary>The denoise's depth tolerance (relative to the depth) for a sharpness: 0.02 at Godot's default 0.98.</summary>
    internal static float DenoiseTolerance(float sharpness) => 0.01f + (1f - Math.Clamp(sharpness, 0f, 1f)) * 0.5f;

    /// <summary>The upsample's depth tolerance (relative): 0.06 at 0.98, wide enough for grazing planes one texel apart.</summary>
    internal static float UpsampleTolerance(float sharpness) => 0.03f + (1f - Math.Clamp(sharpness, 0f, 1f)) * 1.5f;

    private void Pass(CommandBuffer cb, RenderTarget target, Pipeline pipeline, DescriptorSet source, ref GtaoPush push)
    {
        var vk = _ctx.Vk;
        target.Begin(cb, default);
        vk.CmdBindPipeline(cb, PipelineBindPoint.Graphics, pipeline);
        vk.CmdBindDescriptorSets(cb, PipelineBindPoint.Graphics, _layout, 0, 1, &source, 0, null);
        fixed (GtaoPush* p = &push)
            vk.CmdPushConstants(cb, _layout, ShaderStageFlags.FragmentBit, 0, (uint)sizeof(GtaoPush), p);
        PipelineBuilder.SetViewport(vk, cb, target.Extent, flipY: false);
        vk.CmdDraw(cb, 3, 1, 0, 0);
        target.End(cb);
    }

    private void WriteSets(SceneTextures scene)
    {
        var depth = Image(scene.PointSampler, scene.Depth, ImageLayout.DepthStencilReadOnlyOptimal);
        PipelineBuilder.WriteImage(_ctx, _gtaoSet, 0, depth);
        PipelineBuilder.WriteImage(_ctx, _gtaoSet, 1, depth); // unread: every set is complete
        var gtao = Image(scene.PointSampler, _gtao.GetColor(0).View, ImageLayout.ShaderReadOnlyOptimal);
        PipelineBuilder.WriteImage(_ctx, _denoiseSet, 0, gtao);
        PipelineBuilder.WriteImage(_ctx, _denoiseSet, 1, depth);
        var denoised = Image(scene.PointSampler, _denoise.GetColor(0).View, ImageLayout.ShaderReadOnlyOptimal);
        PipelineBuilder.WriteImage(_ctx, _denoise2Set, 0, denoised);
        PipelineBuilder.WriteImage(_ctx, _denoise2Set, 1, depth);
        PipelineBuilder.WriteImage(_ctx, _upsampleSet, 0, gtao);
        PipelineBuilder.WriteImage(_ctx, _upsampleSet, 1, depth);
    }

    // Two timestamps per frame slot around the passes (the ShadowSystem pattern), when the graphics queue has them.
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
        _ctx.Vk.CreateQueryPool(_ctx.Device, in info, null, out _timestamps).Check("vkCreateQueryPool (ssao timing)");
        _timestampPeriodNs = props.Limits.TimestampPeriod;
        _timestampMask = ulong.MaxValue; // a narrower counter wrapping between the two writes reads wrong once (rare)
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
                LastGpuMilliseconds = ((data[1] - data[0]) & _timestampMask) * _timestampPeriodNs / 1e6;
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

    private static DescriptorImageInfo Image(Sampler sampler, ImageView view, ImageLayout layout) =>
        new() { Sampler = sampler, ImageView = view, ImageLayout = layout };

    /// <summary>Destroys the pipelines and sets (the device is idle); the pool owns the targets.</summary>
    protected override void OnDispose()
    {
        var vk = _ctx.Vk;
        vk.DestroyPipeline(_ctx.Device, _gtaoPipeline, null);
        vk.DestroyPipeline(_ctx.Device, _denoisePipeline, null);
        vk.DestroyPipeline(_ctx.Device, _upsamplePipeline, null);
        vk.DestroyPipelineLayout(_ctx.Device, _layout, null);
        vk.DestroyDescriptorPool(_ctx.Device, _pool, null);
        vk.DestroyDescriptorSetLayout(_ctx.Device, _setLayout, null);
        if (_timestamps.Handle != 0)
            vk.DestroyQueryPool(_ctx.Device, _timestamps, null);
    }

    /// <summary><c>include/gtao.slang</c>'s push block (std430, 96 bytes).</summary>
    internal struct GtaoPush
    {
        /// <summary>M11, M22, M31, M32.</summary>
        public Vector4 Projection;

        /// <summary>M41, M42, M33, M43.</summary>
        public Vector4 Projection2;

        /// <summary>M34, M44, scene width, scene height.</summary>
        public Vector4 Projection3;

        /// <summary>Radius (m), maximum radius (full pixels), horizon bias (radians), detail.</summary>
        public Vector4 Shape;

        /// <summary>Intensity, power, denoise tolerance, upsample tolerance.</summary>
        public Vector4 Filter;

        /// <summary>Temporal index, slices, steps per side, the denoise window's first offset.</summary>
        public Vector4 Noise;
    }
}
