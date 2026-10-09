using Silk.NET.Vulkan;

namespace MainframeEngine;

/// <summary>
/// The sharpen after TAA (ADR 0166; <c>Post/TaaSharpen.vk.frag</c>): RCAS (AMD FidelityFX FSR 1's robust
/// contrast-adaptive sharpening) on the tonemapped LDR image, an <see cref="PostStage.AfterTonemap"/> effect at
/// <see cref="PostEffectOrder.Sharpen"/>, enabled by <see cref="AntiAliasing.Taa"/> with a
/// <see cref="IVulkanContext.TaaSharpness"/> above 0. It restores the detail the resolve's reconstruction filter softens,
/// after the history (sharpening inside the history would compound frame over frame) and before the canvas, gizmos and UI,
/// which stay unfiltered. ADR 0174: also FSR 1's second half, after an EASU upscale (<see cref="Scaling3DMode.Fsr"/> with
/// TAA off), with <see cref="IVulkanContext.FsrSharpness"/>'s stops (<see cref="SharpnessFor"/>). Allocates nothing per
/// frame.
/// </summary>
internal sealed unsafe class TaaSharpenEffect() : PostEffect("taa sharpen", PostStage.AfterTonemap, PostEffectOrder.Sharpen)
{
    private IVulkanContext _ctx = null!;
    private Sampler _sampler;
    private DescriptorSetLayout _setLayout;
    private DescriptorPool _pool;
    private PipelineLayout _layout;
    private readonly LdrInputSets _inputs = new();
    private readonly PassPipelines _pipelines = new();

    public override bool IsEnabled(in PostEffectSettings settings) =>
        settings.AntiAliasing == AntiAliasing.Taa ? settings.TaaSharpness > 0f : FsrRcas(settings);

    // ADR 0174: FSR 1's RCAS after its EASU upscale.
    private static bool FsrRcas(in PostEffectSettings settings) =>
        settings.Upscaling && settings.Scaling3DMode == Scaling3DMode.Fsr;

    /// <summary>
    /// RCAS's strength (0–1, the scale on its lobe) for <paramref name="settings"/>: <see cref="PostEffectSettings.TaaSharpness"/>
    /// after TAA, else FSR's exp2(−<see cref="PostEffectSettings.FsrSharpness"/>) (RCAS's <c>FsrRcasCon</c>: 0.87 at
    /// Godot's default 0.2).
    /// </summary>
    internal static float SharpnessFor(in PostEffectSettings settings) =>
        settings.AntiAliasing == AntiAliasing.Taa
            ? Math.Clamp(settings.TaaSharpness, 0f, 1f)
            : MathF.Pow(2f, -Math.Clamp(settings.FsrSharpness, 0f, RenderScaling.MaxFsrSharpness));

    protected override void OnCreate(PostEffectContext context)
    {
        var ctx = context.Vulkan;
        _ctx = ctx;
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
        ctx.Vk.CreateSampler(ctx.Device, in samplerInfo, null, out _sampler).Check("vkCreateSampler (taa sharpen)");
        _setLayout = PipelineBuilder.CreateSetLayout(ctx,
            [new DescriptorSetLayoutBinding { Binding = 0, DescriptorType = DescriptorType.CombinedImageSampler, DescriptorCount = 1, StageFlags = ShaderStageFlags.FragmentBit }],
            "taa sharpen");
        _pool = PipelineBuilder.CreatePool(ctx, LdrInputSets.Capacity,
            [new DescriptorPoolSize { Type = DescriptorType.CombinedImageSampler, DescriptorCount = LdrInputSets.Capacity }], "taa sharpen");
        _inputs.Allocate(ctx, _pool, _setLayout, "taa sharpen");
        _layout = PipelineBuilder.CreateLayout(ctx, [_setLayout], (uint)sizeof(SharpenPush), ShaderStageFlags.FragmentBit, "taa sharpen");
    }

    /// <summary>After a resize (device idle): the LDR images are new, so every input set is rewritten on use.</summary>
    protected override void OnResize(PostEffectContext context) => _inputs.Forget();

    protected override void OnRecord(PostEffectContext context)
    {
        var vk = _ctx.Vk;
        var cb = context.CommandBuffer;
        var pass = context.OutputRenderPass;
        if (!_pipelines.TryGet(pass, out var pipeline))
            pipeline = _pipelines.Add(pass, PipelineBuilder.Create(_ctx, new PipelineState(), _layout, pass,
                "Shaders/Post/Fullscreen.vk.vert.spv", "Shaders/Post/TaaSharpen.vk.frag.spv", [], [], "taa sharpen"));
        var set = _inputs.Get(_ctx, context.Scene.Ldr, _sampler);

        context.BeginOutput();
        vk.CmdBindPipeline(cb, PipelineBindPoint.Graphics, pipeline);
        vk.CmdBindDescriptorSets(cb, PipelineBindPoint.Graphics, _layout, 0, 1, &set, 0, null);
        var push = new SharpenPush
        {
            Sharpness = SharpnessFor(context.Settings),
            DecodeSrgb = context.OutputEncodesSrgb ? 1u : 0u,
        };
        vk.CmdPushConstants(cb, _layout, ShaderStageFlags.FragmentBit, 0, (uint)sizeof(SharpenPush), &push);
        PipelineBuilder.SetViewport(vk, cb, context.Scene.Extent, flipY: false);
        vk.CmdDraw(cb, 3, 1, 0, 0);
        context.EndOutput();
    }

    /// <summary>Destroys everything (the caller has waited for the device to be idle).</summary>
    protected override void OnDispose()
    {
        var vk = _ctx.Vk;
        _pipelines.Destroy(_ctx);
        vk.DestroyPipelineLayout(_ctx.Device, _layout, null);
        vk.DestroyDescriptorPool(_ctx.Device, _pool, null);
        vk.DestroyDescriptorSetLayout(_ctx.Device, _setLayout, null);
        vk.DestroySampler(_ctx.Device, _sampler, null);
    }

    // TaaSharpen.vk.frag's push block.
    private struct SharpenPush
    {
        public float Sharpness;
        public uint DecodeSrgb;
    }
}
