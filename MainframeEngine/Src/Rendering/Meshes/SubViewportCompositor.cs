using Silk.NET.Vulkan;

namespace MainframeEngine;

/// <summary>
/// Tonemaps offscreen views (<see cref="SubViewport"/>) into their LDR targets with the same shader as the main
/// view (exposure, ACES fitted, sRGB encoding into an <c>R8G8B8A8_UNORM</c> image), so ImGui and UI draw them
/// exactly like the swapchain shows the main view. Owns the shared pipeline, layouts, a sampler for displaying
/// the results, and the per-view descriptor sets.
/// </summary>
internal sealed unsafe class SubViewportCompositor : IDisposable
{
    private const uint MaxViews = 64;

    private readonly IVulkanContext _ctx;
    private readonly DescriptorSetLayout _setLayout;
    private readonly DescriptorPool _pool;
    private readonly PipelineLayout _layout;
    private readonly RenderPass _prototype;
    private readonly Pipeline _pipeline;
    private readonly Sampler _nearest;
    private bool _disposed;

    public SubViewportCompositor(IVulkanContext ctx)
    {
        _ctx = ctx;
        _nearest = GpuTexture.CreateSampler(ctx, TextureSampling.NearestClamp, 1);
        DisplaySampler = GpuTexture.CreateSampler(ctx, TextureSampling.LinearClamp, 1);
        _setLayout = PipelineBuilder.CreateSetLayout(ctx,
            [new DescriptorSetLayoutBinding { Binding = 0, DescriptorType = DescriptorType.CombinedImageSampler, DescriptorCount = 1, StageFlags = ShaderStageFlags.FragmentBit }],
            "sub-viewport tonemap");

        // Sets of resized views are freed after the frames in flight: room for a few generations per view.
        const uint poolSets = MaxViews * (IVulkanContext.MaxFramesInFlight + 2);
        var size = new DescriptorPoolSize { Type = DescriptorType.CombinedImageSampler, DescriptorCount = poolSets };
        var poolInfo = new DescriptorPoolCreateInfo
        {
            SType = StructureType.DescriptorPoolCreateInfo,
            Flags = DescriptorPoolCreateFlags.FreeDescriptorSetBit,
            MaxSets = poolSets,
            PoolSizeCount = 1,
            PPoolSizes = &size,
        };
        ctx.Vk.CreateDescriptorPool(ctx.Device, in poolInfo, null, out _pool).Check("vkCreateDescriptorPool (sub-viewport tonemap)");

        _layout = PipelineBuilder.CreateLayout(ctx, [_setLayout], 8, ShaderStageFlags.FragmentBit, "sub-viewport tonemap");
        _prototype = RenderTarget.CreateRenderPass(ctx, LdrTargetDesc("prototype"));
        _pipeline = PipelineBuilder.Create(ctx, new PipelineState(), _layout, _prototype,
            "Shaders/Post/Fullscreen.vk.vert.spv", "Shaders/Post/Tonemap.vk.frag.spv", [], [], "sub-viewport tonemap");
    }

    /// <summary>Linear, clamped sampler for showing view images (ImGui).</summary>
    public Sampler DisplaySampler { get; }

    /// <summary>The tonemapped target: sRGB-encoded values in a UNORM image, sampled afterwards.</summary>
    public static RenderTargetDesc LdrTargetDesc(string name) =>
        new($"{name} (LDR)", [RenderTargetAttachment.Sampled(Format.R8G8B8A8Unorm)], null);

    public DescriptorSet AllocateSet(ImageView hdrView)
    {
        var layout = _setLayout;
        var info = new DescriptorSetAllocateInfo
        {
            SType = StructureType.DescriptorSetAllocateInfo,
            DescriptorPool = _pool,
            DescriptorSetCount = 1,
            PSetLayouts = &layout,
        };
        _ctx.Vk.AllocateDescriptorSets(_ctx.Device, in info, out var set).Check("vkAllocateDescriptorSets (sub-viewport tonemap)");
        PipelineBuilder.WriteImage(_ctx, set, 0, new DescriptorImageInfo
        {
            Sampler = _nearest,
            ImageView = hdrView,
            ImageLayout = ImageLayout.ShaderReadOnlyOptimal,
        });
        return set;
    }

    /// <summary>Returns a set once frames in flight no longer use it.</summary>
    public void FreeSet(DescriptorSet set)
    {
        if (set.Handle != 0 && !_disposed)
            _ctx.Deletions.Enqueue(GpuDeletion.Of(_pool, set));
    }

    /// <summary>Records the tonemap of <paramref name="targets"/>' HDR image into its LDR target (no pass active).</summary>
    public void Tonemap(CommandBuffer cb, SubViewportTargets targets)
    {
        var ldr = targets.Ldr!;
        ldr.Begin(cb, default);
        var vk = _ctx.Vk;
        vk.CmdBindPipeline(cb, PipelineBindPoint.Graphics, _pipeline);
        var set = targets.TonemapSet;
        vk.CmdBindDescriptorSets(cb, PipelineBindPoint.Graphics, _layout, 0, 1, &set, 0, null);
        var push = stackalloc uint[2];
        *(float*)push = _ctx.Exposure;
        push[1] = 1; // UNORM target: encode sRGB in the shader
        vk.CmdPushConstants(cb, _layout, ShaderStageFlags.FragmentBit, 0, 8, push);
        PipelineBuilder.SetViewport(vk, cb, ldr.Extent, flipY: false);
        vk.CmdDraw(cb, 3, 1, 0, 0);
        ldr.End(cb); // explicit barrier: ImGui, the UI and materials sample the result later in the frame
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        var deletions = _ctx.Deletions;
        deletions.Enqueue(GpuDeletion.Of(_pipeline));
        deletions.Enqueue(GpuDeletion.Of(_prototype));
        deletions.Enqueue(GpuDeletion.Of(_layout));
        deletions.Enqueue(GpuDeletion.Of(_pool));
        deletions.Enqueue(GpuDeletion.Of(_setLayout));
        deletions.Enqueue(GpuDeletion.Of(_nearest));
        deletions.Enqueue(GpuDeletion.Of(DisplaySampler));
    }
}
