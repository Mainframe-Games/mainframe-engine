using System.Numerics;
using Silk.NET.Vulkan;

namespace MainframeEngine;

/// <summary>
/// FXAA 3.11 (ADR 0154; <c>Post/Fxaa.vk.frag</c>) as an <see cref="PostStage.AfterTonemap"/> effect (ADR 0163), enabled by
/// <see cref="AntiAliasing.Fxaa"/>: it filters the tonemapped <see cref="SceneTextures.Ldr"/> image (sRGB-encoded
/// <c>R8G8B8A8_UNORM</c>) into its output pass — the swapchain when it is the stage's last effect, so the overlay
/// renderers (canvas, gizmos, UI) draw after it, unfiltered. Allocates nothing per frame.
/// </summary>
internal sealed unsafe class FxaaEffect() : PostEffect("fxaa", PostStage.AfterTonemap, PostEffectOrder.Fxaa)
{
    private IVulkanContext _ctx = null!;
    private Sampler _sampler;
    private DescriptorSetLayout _setLayout;
    private DescriptorPool _pool;
    private PipelineLayout _layout;
    private readonly LdrInputSets _inputs = new();
    private readonly PassPipelines _pipelines = new();

    public override bool IsEnabled(in PostEffectSettings settings) => settings.AntiAliasing == AntiAliasing.Fxaa;

    protected override void OnCreate(PostEffectContext context)
    {
        var ctx = context.Vulkan;
        _ctx = ctx;
        var samplerInfo = new SamplerCreateInfo
        {
            SType = StructureType.SamplerCreateInfo,
            MagFilter = Filter.Linear,
            MinFilter = Filter.Linear,
            MipmapMode = SamplerMipmapMode.Nearest,
            AddressModeU = SamplerAddressMode.ClampToEdge,
            AddressModeV = SamplerAddressMode.ClampToEdge,
            AddressModeW = SamplerAddressMode.ClampToEdge,
        };
        ctx.Vk.CreateSampler(ctx.Device, in samplerInfo, null, out _sampler).Check("vkCreateSampler (fxaa)");

        _setLayout = PipelineBuilder.CreateSetLayout(ctx,
            [new DescriptorSetLayoutBinding { Binding = 0, DescriptorType = DescriptorType.CombinedImageSampler, DescriptorCount = 1, StageFlags = ShaderStageFlags.FragmentBit }],
            "fxaa");
        _pool = PipelineBuilder.CreatePool(ctx, LdrInputSets.Capacity,
            [new DescriptorPoolSize { Type = DescriptorType.CombinedImageSampler, DescriptorCount = LdrInputSets.Capacity }], "fxaa");
        _inputs.Allocate(ctx, _pool, _setLayout, "fxaa");
        _layout = PipelineBuilder.CreateLayout(ctx, [_setLayout], (uint)sizeof(FxaaPush), ShaderStageFlags.FragmentBit, "fxaa");
    }

    /// <summary>After a resize (device idle): the LDR images are new, so every input set is rewritten on use.</summary>
    protected override void OnResize(PostEffectContext context) => _inputs.Forget();

    protected override void OnRecord(PostEffectContext context)
    {
        var vk = _ctx.Vk;
        var cb = context.CommandBuffer;
        var extent = context.Scene.Extent;
        var pass = context.OutputRenderPass;
        if (!_pipelines.TryGet(pass, out var pipeline))
            pipeline = _pipelines.Add(pass, PipelineBuilder.Create(_ctx, new PipelineState(), _layout, pass,
                "Shaders/Post/Fullscreen.vk.vert.spv", "Shaders/Post/Fxaa.vk.frag.spv", [], [], "fxaa"));
        var set = _inputs.Get(_ctx, context.Scene.Ldr, _sampler);

        context.BeginOutput();
        vk.CmdBindPipeline(cb, PipelineBindPoint.Graphics, pipeline);
        vk.CmdBindDescriptorSets(cb, PipelineBindPoint.Graphics, _layout, 0, 1, &set, 0, null);
        var push = new FxaaPush
        {
            RcpFrame = new Vector2(1f / extent.Width, 1f / extent.Height),
            DecodeSrgb = context.OutputEncodesSrgb ? 1u : 0u, // the swapchain view encodes: the LDR values already are
        };
        vk.CmdPushConstants(cb, _layout, ShaderStageFlags.FragmentBit, 0, (uint)sizeof(FxaaPush), &push);
        PipelineBuilder.SetViewport(vk, cb, extent, flipY: false);
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

    // Fxaa.vk.frag's push block.
    private struct FxaaPush
    {
        public Vector2 RcpFrame;
        public uint DecodeSrgb;
    }
}

/// <summary>
/// An <see cref="PostStage.AfterTonemap"/> effect's pipelines, one per output render pass it has been handed (the
/// swapchain's, an intermediate LDR target's): two at most. Allocation-free once both exist.
/// </summary>
internal sealed class PassPipelines
{
    private readonly ulong[] _passes = new ulong[2];
    private readonly Pipeline[] _pipelines = new Pipeline[2];

    /// <summary>The pipeline created for <paramref name="pass"/>, if any.</summary>
    public bool TryGet(RenderPass pass, out Pipeline pipeline)
    {
        for (var i = 0; i < _passes.Length; i++)
        {
            if (_passes[i] == pass.Handle)
            {
                pipeline = _pipelines[i];
                return true;
            }
        }

        pipeline = default;
        return false;
    }

    /// <summary>Keeps <paramref name="pipeline"/> as <paramref name="pass"/>'s (destroyed with the others) and returns it.</summary>
    public Pipeline Add(RenderPass pass, Pipeline pipeline)
    {
        for (var i = 0; i < _passes.Length; i++)
        {
            if (_passes[i] != 0)
                continue;
            _passes[i] = pass.Handle;
            return _pipelines[i] = pipeline;
        }

        throw new InvalidOperationException("An effect drew into more than two output render passes.");
    }

    public unsafe void Destroy(IVulkanContext ctx)
    {
        for (var i = 0; i < _passes.Length; i++)
        {
            if (_passes[i] != 0)
                ctx.Vk.DestroyPipeline(ctx.Device, _pipelines[i], null);
            _passes[i] = 0;
            _pipelines[i] = default;
        }
    }
}

/// <summary>
/// Descriptor sets (binding 0: one sampled image) for the LDR images an <see cref="PostStage.AfterTonemap"/> effect reads:
/// one set per image (the stage's ping-pong has two), written on first use and never while a frame in flight may bind
/// it; <see cref="Forget"/> after a resize, when the device is idle.
/// </summary>
internal sealed class LdrInputSets
{
    public const int Capacity = 2;

    private readonly DescriptorSet[] _sets = new DescriptorSet[Capacity];
    private readonly ulong[] _views = new ulong[Capacity];

    public void Allocate(IVulkanContext ctx, DescriptorPool pool, DescriptorSetLayout layout, string what)
    {
        for (var i = 0; i < Capacity; i++)
            _sets[i] = PipelineBuilder.AllocateSet(ctx, pool, layout, what);
    }

    /// <summary>The set binding <paramref name="view"/> with <paramref name="sampler"/>.</summary>
    public DescriptorSet Get(IVulkanContext ctx, ImageView view, Sampler sampler)
    {
        for (var i = 0; i < Capacity; i++)
            if (_views[i] == view.Handle)
                return _sets[i];
        for (var i = 0; i < Capacity; i++)
        {
            if (_views[i] != 0)
                continue;
            _views[i] = view.Handle;
            PipelineBuilder.WriteImage(ctx, _sets[i], 0, new DescriptorImageInfo
            {
                Sampler = sampler,
                ImageView = view,
                ImageLayout = ImageLayout.ShaderReadOnlyOptimal,
            });
            return _sets[i];
        }

        throw new InvalidOperationException("More LDR images than input sets.");
    }

    /// <summary>Forgets every image (device idle: after a resize the views are new).</summary>
    public void Forget() => Array.Clear(_views);
}
