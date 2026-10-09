using Silk.NET.Vulkan;

namespace MainframeEngine;

/// <summary>
/// The spatial upscale (ADR 0174; <c>Post/Upscale.vk.frag</c>): a <see cref="PostStage.BeforeTonemap"/> effect at
/// <see cref="PostEffectOrder.Upscale"/>, enabled while the view renders below its output size
/// (<see cref="PostEffectSettings.Upscaling"/>). It brings the render-resolution HDR colour to the output resolution, so
/// auto exposure, glow, light shafts and the tonemap run at the window's size:
/// <list type="bullet">
/// <item><see cref="Scaling3DMode.Fsr"/> without TAA: FSR 1's EASU (edge-adaptive, deringed Lanczos), then RCAS on the
/// tonemapped image (<see cref="TaaSharpenEffect"/> with <see cref="IVulkanContext.FsrSharpness"/>);</item>
/// <item><see cref="Scaling3DMode.Bilinear"/>, or a TAA frame the resolve skipped (no motion vectors, no camera): a
/// bilinear tap.</item>
/// </list>
/// With TAA on the resolve upscales (TAAU) and writes the output colour itself; this effect then records nothing. One
/// fullscreen pass into a pooled output-sized target, then <see cref="PostEffectContext.CopyToOutputColor"/>. Allocates
/// nothing per frame.
/// </summary>
internal sealed unsafe class SpatialUpscaleEffect() : PostEffect("upscale", PostStage.BeforeTonemap, PostEffectOrder.Upscale)
{
    /// <summary>The pooled output-resolution target the upscale draws into (linear HDR).</summary>
    internal static readonly PostTargetDesc TargetDesc = new("upscale", SceneTextures.ColorFormat);

    // Upscale.vk.frag's modes.
    internal const uint ModeBilinear = 0, ModeEasu = 1;

    private IVulkanContext _ctx = null!;
    private Sampler _sampler;
    private DescriptorSetLayout _setLayout;
    private DescriptorPool _pool;
    private DescriptorSet _set;
    private PipelineLayout _layout;
    private Pipeline _pipeline;

    /// <summary>Frames this effect upscaled (tests).</summary>
    internal long UpscaledFrames { get; private set; }

    /// <summary>Frames it upscaled with EASU (tests).</summary>
    internal long EasuFrames { get; private set; }

    public override bool IsEnabled(in PostEffectSettings settings) => settings.Upscaling;

    /// <summary>The shader mode for <paramref name="settings"/>: EASU for <see cref="Scaling3DMode.Fsr"/> without TAA.</summary>
    internal static uint ModeFor(in PostEffectSettings settings) =>
        settings.Scaling3DMode == Scaling3DMode.Fsr && settings.AntiAliasing != AntiAliasing.Taa ? ModeEasu : ModeBilinear;

    protected override void OnCreate(PostEffectContext context)
    {
        var ctx = context.Vulkan;
        _ctx = ctx;
        _sampler = GpuTexture.CreateSampler(ctx, TextureSampling.LinearClamp, 1);
        _setLayout = PipelineBuilder.CreateSetLayout(ctx,
            [new DescriptorSetLayoutBinding { Binding = 0, DescriptorType = DescriptorType.CombinedImageSampler, DescriptorCount = 1, StageFlags = ShaderStageFlags.FragmentBit }],
            "upscale");
        _pool = PipelineBuilder.CreatePool(ctx, 1,
            [new DescriptorPoolSize { Type = DescriptorType.CombinedImageSampler, DescriptorCount = 1 }], "upscale");
        _set = PipelineBuilder.AllocateSet(ctx, _pool, _setLayout, "upscale");
        WriteSet(context.Scene);
        _layout = PipelineBuilder.CreateLayout(ctx, [_setLayout], (uint)sizeof(UpscalePush), ShaderStageFlags.FragmentBit, "upscale");
        // The pooled target keeps its render pass across resizes: one pipeline.
        _pipeline = PipelineBuilder.Create(ctx, new PipelineState(), _layout, context.OutputTargets.Get(TargetDesc).RenderPass,
            "Shaders/Post/Fullscreen.vk.vert.spv", "Shaders/Post/Upscale.vk.frag.spv", [], [], "upscale");
    }

    /// <summary>After a resize or a scale change (device idle): the render colour is a new image.</summary>
    protected override void OnResize(PostEffectContext context) => WriteSet(context.Scene);

    protected override void OnRecord(PostEffectContext context)
    {
        // TAAU's resolve already wrote the output colour this frame.
        if (context.OutputColorWritten)
            return;

        var scene = context.Scene;
        var target = context.OutputTargets.Get(TargetDesc);
        var cb = context.CommandBuffer;
        var vk = _ctx.Vk;
        var mode = ModeFor(context.Settings);
        target.Begin(cb, default);
        vk.CmdBindPipeline(cb, PipelineBindPoint.Graphics, _pipeline);
        var set = _set;
        vk.CmdBindDescriptorSets(cb, PipelineBindPoint.Graphics, _layout, 0, 1, &set, 0, null);
        var push = new UpscalePush
        {
            InputWidth = Math.Max(1u, scene.RenderExtent.Width),
            InputHeight = Math.Max(1u, scene.RenderExtent.Height),
            OutputWidth = Math.Max(1u, target.Extent.Width),
            OutputHeight = Math.Max(1u, target.Extent.Height),
            Mode = mode,
        };
        vk.CmdPushConstants(cb, _layout, ShaderStageFlags.FragmentBit, 0, (uint)sizeof(UpscalePush), &push);
        PipelineBuilder.SetViewport(vk, cb, target.Extent, flipY: false);
        vk.CmdDraw(cb, 3, 1, 0, 0);
        target.End(cb);
        context.CopyToOutputColor(target.GetColor(0).View);
        UpscaledFrames++;
        if (mode == ModeEasu)
            EasuFrames++;
    }

    private void WriteSet(SceneTextures scene) =>
        PipelineBuilder.WriteImage(_ctx, _set, 0, new DescriptorImageInfo
        {
            Sampler = _sampler,
            ImageView = scene.RenderColor,
            ImageLayout = ImageLayout.ShaderReadOnlyOptimal,
        });

    /// <summary>Destroys everything (the caller has waited for the device to be idle); the pool owns the target.</summary>
    protected override void OnDispose()
    {
        var vk = _ctx.Vk;
        vk.DestroyPipeline(_ctx.Device, _pipeline, null);
        vk.DestroyPipelineLayout(_ctx.Device, _layout, null);
        vk.DestroyDescriptorPool(_ctx.Device, _pool, null);
        vk.DestroyDescriptorSetLayout(_ctx.Device, _setLayout, null);
        vk.DestroySampler(_ctx.Device, _sampler, null);
    }

    // Upscale.vk.frag's push block.
    private struct UpscalePush
    {
        public float InputWidth;
        public float InputHeight;
        public float OutputWidth;
        public float OutputHeight;
        public uint Mode;
    }
}
