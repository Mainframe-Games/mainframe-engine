using System.Numerics;
using Silk.NET.Vulkan;

namespace MainframeEngine;

/// <summary>
/// FXAA 3.11 (ADR 0154; <c>Post/Fxaa.vk.frag</c>): with <see cref="AntiAliasing.Fxaa"/> the tonemap draws into this
/// pass's <c>R8G8B8A8_UNORM</c> intermediate (sRGB-encoded values, the swapchain's size) instead of the swapchain, and
/// <see cref="Draw"/> filters it into the swapchain pass, before the overlay renderers (canvas, gizmos, UI) draw, so they
/// are never blurred. Created on first use; allocates nothing per frame.
/// </summary>
internal sealed unsafe class FxaaPass : IDisposable
{
    public const Format LdrFormat = Format.R8G8B8A8Unorm;

    private readonly IVulkanContext _ctx;
    private readonly RenderTarget _ldr;
    private readonly Sampler _sampler;
    private readonly DescriptorSetLayout _setLayout;
    private readonly DescriptorPool _pool;
    private readonly DescriptorSet _set;
    private readonly PipelineLayout _layout;
    private readonly Pipeline _pipeline;
    private bool _disposed;

    public FxaaPass(IVulkanContext ctx, Extent2D extent, RenderPass presentPass)
    {
        _ctx = ctx;
        _ldr = new RenderTarget(ctx, new RenderTargetDesc("fxaa input", [RenderTargetAttachment.Sampled(LdrFormat)], null), extent);
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
        _pool = PipelineBuilder.CreatePool(ctx, 1, [new DescriptorPoolSize { Type = DescriptorType.CombinedImageSampler, DescriptorCount = 1 }], "fxaa");
        _set = PipelineBuilder.AllocateSet(ctx, _pool, _setLayout, "fxaa");
        WriteSet();
        _layout = PipelineBuilder.CreateLayout(ctx, [_setLayout], (uint)sizeof(FxaaPush), ShaderStageFlags.FragmentBit, "fxaa");
        _pipeline = PipelineBuilder.Create(ctx, new PipelineState(), _layout, presentPass,
            "Shaders/Post/Fullscreen.vk.vert.spv", "Shaders/Post/Fxaa.vk.frag.spv", [], [], "fxaa");
    }

    /// <summary>The intermediate's render pass: build the tonemap pipelines that draw into it against this.</summary>
    public RenderPass LdrRenderPass => _ldr.RenderPass;

    /// <summary>Begins the pass into the intermediate (no render pass may be active).</summary>
    public void BeginLdr(CommandBuffer cb) => _ldr.Begin(cb, default);

    /// <summary>Ends it, with the barrier that makes it readable by <see cref="Draw"/>.</summary>
    public void EndLdr(CommandBuffer cb) => _ldr.End(cb);

    /// <summary>
    /// Draws the filtered intermediate inside the swapchain pass; <paramref name="decodeSrgb"/> when the swapchain view
    /// encodes sRGB itself (the intermediate already holds encoded values).
    /// </summary>
    public void Draw(CommandBuffer cb, bool decodeSrgb)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        var vk = _ctx.Vk;
        var extent = _ldr.Extent;
        vk.CmdBindPipeline(cb, PipelineBindPoint.Graphics, _pipeline);
        var set = _set;
        vk.CmdBindDescriptorSets(cb, PipelineBindPoint.Graphics, _layout, 0, 1, &set, 0, null);
        var push = new FxaaPush
        {
            RcpFrame = new Vector2(1f / extent.Width, 1f / extent.Height),
            DecodeSrgb = decodeSrgb ? 1u : 0u,
        };
        vk.CmdPushConstants(cb, _layout, ShaderStageFlags.FragmentBit, 0, (uint)sizeof(FxaaPush), &push);
        PipelineBuilder.SetViewport(vk, cb, extent, flipY: false);
        vk.CmdDraw(cb, 3, 1, 0, 0);
    }

    /// <summary>After a swapchain resize (device idle).</summary>
    public void Resize(Extent2D extent)
    {
        if (_ldr.Resize(extent))
            WriteSet();
    }

    private void WriteSet() =>
        PipelineBuilder.WriteImage(_ctx, _set, 0, new DescriptorImageInfo
        {
            Sampler = _sampler,
            ImageView = _ldr.GetColor(0).View,
            ImageLayout = ImageLayout.ShaderReadOnlyOptimal,
        });

    /// <summary>Destroys everything (the caller has waited for the device to be idle).</summary>
    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        var vk = _ctx.Vk;
        vk.DestroyPipeline(_ctx.Device, _pipeline, null);
        vk.DestroyPipelineLayout(_ctx.Device, _layout, null);
        vk.DestroyDescriptorPool(_ctx.Device, _pool, null);
        vk.DestroyDescriptorSetLayout(_ctx.Device, _setLayout, null);
        vk.DestroySampler(_ctx.Device, _sampler, null);
        _ldr.Dispose();
    }

    // Fxaa.vk.frag's push block.
    private struct FxaaPush
    {
        public Vector2 RcpFrame;
        public uint DecodeSrgb;
    }
}
