using System.Numerics;
using Silk.NET.Vulkan;

namespace MainframeEngine;

/// <summary>
/// Bokeh depth of field (ADR 0168; Godot's <c>CameraAttributesPractical</c> DoF): a <see cref="PostStage.BeforeTonemap"/>
/// effect after TAA (<see cref="PostEffectOrder.DepthOfField"/>), enabled by <see cref="PostProcessSettings.DofEnabled"/>.
/// Three raster passes from the scene's own colour and depth (no prepass needed):
/// <list type="number">
/// <item><c>DofPrefilter</c>, half resolution: 2 × 2 colour mean + the signed circle of confusion of the nearest depth.</item>
/// <item><c>DofGather</c>, half resolution: a scatter-as-gather over a golden-angle disc of 16 or 32 taps
/// (<see cref="PostProcessSettings.DofQuality"/>), near and far in one pass (background samples cannot cover a sharper
/// foreground).</item>
/// <item><c>DofComposite</c>, full resolution: the sharp scene blended towards the bokeh by each pixel's own circle, then
/// written back with <see cref="PostEffectContext.CopyToSceneColor"/>.</item>
/// </list>
/// Targets come from the pool (<c>dof prefilter</c>, <c>dof bokeh</c> at half size, <c>dof composite</c>), all
/// <c>R16G16B16A16_SFLOAT</c>. Allocates nothing per frame.
/// </summary>
internal sealed unsafe class DepthOfFieldEffect() : PostEffect("depth of field", PostStage.BeforeTonemap, PostEffectOrder.DepthOfField)
{
    private const Format TargetFormat = Format.R16G16B16A16Sfloat;
    private static readonly PostTargetDesc PrefilterDesc = new("dof prefilter", TargetFormat, PostTargetScale.Half);
    private static readonly PostTargetDesc BokehDesc = new("dof bokeh", TargetFormat, PostTargetScale.Half);
    private static readonly PostTargetDesc CompositeDesc = new("dof composite", TargetFormat);

    private IVulkanContext _ctx = null!;
    private RenderTarget _prefilter = null!;
    private RenderTarget _bokeh = null!;
    private RenderTarget _composite = null!;
    private Sampler _pointSampler;
    private Sampler _linearSampler;
    private DescriptorSetLayout _setLayout;
    private DescriptorPool _pool;
    private DescriptorSet _prefilterSet; // 0: scene colour (linear), 1: depth
    private DescriptorSet _gatherSet;    // 0: prefiltered (linear)
    private DescriptorSet _compositeSet; // 0: scene colour (point), 1: depth, 2: bokeh (linear)
    private PipelineLayout _layout;
    private Pipeline _prefilterPipeline;
    private Pipeline _gatherPipeline;
    private Pipeline _compositePipeline;

    /// <summary>Gather taps per quality.</summary>
    public static int Taps(DepthOfFieldQuality quality) => quality == DepthOfFieldQuality.High ? 32 : 16;

    public override bool IsEnabled(in PostEffectSettings settings) => settings.World.DofEnabled;

    protected override void OnCreate(PostEffectContext context)
    {
        var ctx = context.Vulkan;
        _ctx = ctx;
        var point = new SamplerCreateInfo
        {
            SType = StructureType.SamplerCreateInfo,
            MagFilter = Filter.Nearest,
            MinFilter = Filter.Nearest,
            MipmapMode = SamplerMipmapMode.Nearest,
            AddressModeU = SamplerAddressMode.ClampToEdge,
            AddressModeV = SamplerAddressMode.ClampToEdge,
            AddressModeW = SamplerAddressMode.ClampToEdge,
        };
        ctx.Vk.CreateSampler(ctx.Device, in point, null, out _pointSampler).Check("vkCreateSampler (dof point)");
        var linear = point with { MagFilter = Filter.Linear, MinFilter = Filter.Linear };
        ctx.Vk.CreateSampler(ctx.Device, in linear, null, out _linearSampler).Check("vkCreateSampler (dof linear)");

        _setLayout = PipelineBuilder.CreateSetLayout(ctx,
        [
            new DescriptorSetLayoutBinding { Binding = 0, DescriptorType = DescriptorType.CombinedImageSampler, DescriptorCount = 1, StageFlags = ShaderStageFlags.FragmentBit },
            new DescriptorSetLayoutBinding { Binding = 1, DescriptorType = DescriptorType.CombinedImageSampler, DescriptorCount = 1, StageFlags = ShaderStageFlags.FragmentBit },
            new DescriptorSetLayoutBinding { Binding = 2, DescriptorType = DescriptorType.CombinedImageSampler, DescriptorCount = 1, StageFlags = ShaderStageFlags.FragmentBit },
        ], "dof");
        _pool = PipelineBuilder.CreatePool(ctx, 3, [new DescriptorPoolSize { Type = DescriptorType.CombinedImageSampler, DescriptorCount = 9 }], "dof");
        _prefilterSet = PipelineBuilder.AllocateSet(ctx, _pool, _setLayout, "dof prefilter");
        _gatherSet = PipelineBuilder.AllocateSet(ctx, _pool, _setLayout, "dof gather");
        _compositeSet = PipelineBuilder.AllocateSet(ctx, _pool, _setLayout, "dof composite");
        GetTargets(context);
        WriteSets(context.Scene);

        _layout = PipelineBuilder.CreateLayout(ctx, [_setLayout], (uint)sizeof(DofPush), ShaderStageFlags.FragmentBit, "dof");
        _prefilterPipeline = Create("DofPrefilter", _prefilter.RenderPass);
        _gatherPipeline = Create("DofGather", _bokeh.RenderPass);
        _compositePipeline = Create("DofComposite", _composite.RenderPass);
    }

    private Pipeline Create(string shader, RenderPass pass) =>
        PipelineBuilder.Create(_ctx, new PipelineState(), _layout, pass,
            "Shaders/Post/Fullscreen.vk.vert.spv", $"Shaders/Post/{shader}.vk.frag.spv", [], [], shader);

    private void GetTargets(PostEffectContext context)
    {
        _prefilter = context.Targets.Get(PrefilterDesc);
        _bokeh = context.Targets.Get(BokehDesc);
        _composite = context.Targets.Get(CompositeDesc);
    }

    /// <summary>After a resize (device idle): the pool's targets and the scene's views are new; rewrite every set.</summary>
    protected override void OnResize(PostEffectContext context)
    {
        GetTargets(context);
        WriteSets(context.Scene);
    }

    protected override void OnRecord(PostEffectContext context)
    {
        if (!context.HasCamera)
            return;
        var settings = context.Settings.World;
        var projection = context.Camera.Projection;
        var push = new DofPush
        {
            Projection = new Vector4(projection.M33, projection.M34, projection.M43, projection.M44),
            FarDistance = settings.DofBlurFarDistance,
            FarTransition = settings.DofBlurFarTransition,
            NearDistance = settings.DofBlurNearDistance,
            NearTransition = settings.DofBlurNearTransition,
            MaxRadius = settings.DofMaxRadius(context.Scene.Extent.Height),
            Flags = (settings.DofBlurFarEnabled ? 1u : 0u) | (settings.DofBlurNearEnabled ? 2u : 0u),
            Taps = (uint)Taps(settings.DofQuality),
        };
        var cb = context.CommandBuffer;
        Pass(cb, _prefilter, _prefilterPipeline, _prefilterSet, in push);
        Pass(cb, _bokeh, _gatherPipeline, _gatherSet, in push);
        Pass(cb, _composite, _compositePipeline, _compositeSet, in push);
        context.CopyToSceneColor(_composite.GetColor(0).View);
    }

    private void Pass(CommandBuffer cb, RenderTarget target, Pipeline pipeline, DescriptorSet set, in DofPush push)
    {
        var vk = _ctx.Vk;
        var extent = target.Extent;
        target.Begin(cb, default);
        vk.CmdBindPipeline(cb, PipelineBindPoint.Graphics, pipeline);
        vk.CmdBindDescriptorSets(cb, PipelineBindPoint.Graphics, _layout, 0, 1, &set, 0, null);
        fixed (DofPush* p = &push)
            vk.CmdPushConstants(cb, _layout, ShaderStageFlags.FragmentBit, 0, (uint)sizeof(DofPush), p);
        PipelineBuilder.SetViewport(vk, cb, extent, flipY: false);
        vk.CmdDraw(cb, 3, 1, 0, 0);
        target.End(cb);
    }

    private void WriteSets(SceneTextures scene)
    {
        var depth = Image(_pointSampler, scene.Depth, ImageLayout.DepthStencilReadOnlyOptimal);
        var prefiltered = Image(_linearSampler, _prefilter.GetColor(0).View, ImageLayout.ShaderReadOnlyOptimal);
        var bokeh = Image(_linearSampler, _bokeh.GetColor(0).View, ImageLayout.ShaderReadOnlyOptimal);
        // Every binding of every set is written (the passes read only some), so each set is complete.
        PipelineBuilder.WriteImage(_ctx, _prefilterSet, 0, Image(_linearSampler, scene.Color, ImageLayout.ShaderReadOnlyOptimal));
        PipelineBuilder.WriteImage(_ctx, _prefilterSet, 1, depth);
        PipelineBuilder.WriteImage(_ctx, _prefilterSet, 2, bokeh);
        PipelineBuilder.WriteImage(_ctx, _gatherSet, 0, prefiltered);
        PipelineBuilder.WriteImage(_ctx, _gatherSet, 1, depth);
        PipelineBuilder.WriteImage(_ctx, _gatherSet, 2, prefiltered);
        PipelineBuilder.WriteImage(_ctx, _compositeSet, 0, Image(_pointSampler, scene.Color, ImageLayout.ShaderReadOnlyOptimal));
        PipelineBuilder.WriteImage(_ctx, _compositeSet, 1, depth);
        PipelineBuilder.WriteImage(_ctx, _compositeSet, 2, bokeh);
    }

    private static DescriptorImageInfo Image(Sampler sampler, ImageView view, ImageLayout layout) =>
        new() { Sampler = sampler, ImageView = view, ImageLayout = layout };

    /// <summary>Destroys everything it created (the pool owns the targets; the device is idle).</summary>
    protected override void OnDispose()
    {
        var vk = _ctx.Vk;
        vk.DestroyPipeline(_ctx.Device, _prefilterPipeline, null);
        vk.DestroyPipeline(_ctx.Device, _gatherPipeline, null);
        vk.DestroyPipeline(_ctx.Device, _compositePipeline, null);
        vk.DestroyPipelineLayout(_ctx.Device, _layout, null);
        vk.DestroyDescriptorPool(_ctx.Device, _pool, null);
        vk.DestroyDescriptorSetLayout(_ctx.Device, _setLayout, null);
        vk.DestroySampler(_ctx.Device, _pointSampler, null);
        vk.DestroySampler(_ctx.Device, _linearSampler, null);
    }

    // include/dof.slang's push block (std430).
    private struct DofPush
    {
        public Vector4 Projection;
        public float FarDistance;
        public float FarTransition;
        public float NearDistance;
        public float NearTransition;
        public float MaxRadius;
        public uint Flags;
        public uint Taps;
        public float Pad;
    }
}
