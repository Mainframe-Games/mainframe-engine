using System.Numerics;
using Silk.NET.Vulkan;
using VkSampler = Silk.NET.Vulkan.Sampler;

namespace MainframeEngine;

/// <summary>
/// "No shadows" stand-in for descriptor set 2, so lit pipelines (Shapes, SpineLit) always declare the
/// same set indices whether or not the game created a <see cref="ShadowSystem"/>. Same layout as the
/// real set; every map is a 1×1 depth image cleared to 1.0, and every light-space matrix maps any
/// position to z = 2 (outside the [0, 1] shadow range), so the shaders' shadow terms are all 1.0.
/// Static content: one set serves every frame slot. Owned by <see cref="VulkanRenderer.ShadowFallback"/>.
/// </summary>
internal sealed unsafe class ShadowFallback : IShadowDescriptors, IDisposable
{
    private readonly IVulkanContext _ctx;
    private readonly Format _format;
    private readonly VkSampler _comparisonSampler;
    private readonly VkSampler _cubeSampler;
    private readonly GpuImage _image2D, _imageCube;
    private readonly GpuBuffer _matrices;
    private readonly DescriptorPool _pool;
    private readonly DescriptorSet _set;
    private bool _disposed;

    public DescriptorSetLayout MainDescSetLayout { get; }

    public DescriptorSet GetMainSet() => _set;

    /// <summary>The game's <see cref="ShadowSystem"/> when there is one, otherwise the renderer's fallback.</summary>
    internal static IShadowDescriptors Resolve(ShadowSystem? shadows, IVulkanContext ctx) =>
        shadows as IShadowDescriptors
        ?? (ctx as VulkanRenderer)?.ShadowFallback
        ?? throw new NotSupportedException($"No ShadowSystem and {ctx.GetType().Name} provides no shadow fallback set.");

    /// <summary>
    /// Light-space matrix used for every fallback slot. Row-vector convention (System.Numerics),
    /// uploaded as-is: <c>(x, y, z, 1) · M = (0, 0, 2, 1)</c>, i.e. depth 2 — outside the shadow range.
    /// </summary>
    internal static Matrix4x4 OutOfRangeLightMatrix { get; } = new(
        0, 0, 0, 0,
        0, 0, 0, 0,
        0, 0, 0, 0,
        0, 0, 2, 1);

    public ShadowFallback(IVulkanContext ctx)
    {
        _ctx = ctx;
        (_format, var linear) = ShadowSystem.ChooseDepthFormat(ctx);
        _comparisonSampler = ShadowSystem.CreateComparisonSampler(ctx, linear);
        _cubeSampler = ShadowSystem.CreateCubeSampler(ctx);
        MainDescSetLayout = ShadowSystem.CreateMainSetLayout(ctx, _comparisonSampler);

        // Depth-attachment usage is required for the DEPTH_STENCIL_READ_ONLY_OPTIMAL layout the set expects.
        const ImageUsageFlags usage = ImageUsageFlags.SampledBit | ImageUsageFlags.TransferDstBit | ImageUsageFlags.DepthStencilAttachmentBit;
        _image2D = GpuImage.Create(ctx, new GpuImageDesc(1, 1, _format, usage));
        _imageCube = GpuImage.Create(ctx, new GpuImageDesc(1, 1, _format, usage)
        {
            ArrayLayers = 6,
            Flags = ImageCreateFlags.CreateCubeCompatibleBit,
            ViewType = ImageViewType.TypeCube,
        });

        // Cleared to far depth by the upload queue at the start of the next frame.
        var aspect = VkHelpers.DepthBarrierAspects(_format);
        ctx.Uploads.ClearDepthToFar(_image2D.Handle, aspect, 1);
        ctx.Uploads.ClearDepthToFar(_imageCube.Handle, aspect, 6);
        ctx.Uploads.FlushIfRecording();

        _matrices = GpuBuffer.Create(ctx, ShadowSystem.ShadowMatricesUboSize, BufferUsageFlags.UniformBufferBit, GpuMemoryUsage.Dynamic);
        var matrix = OutOfRangeLightMatrix;
        for (int i = 0; i < ShadowSystem.MaxShadowDir + ShadowSystem.MaxShadowSpot; i++)
            _matrices.Write(matrix, (ulong)(i * sizeof(Matrix4x4)));

        _pool = ShadowSystem.CreateMainPool(ctx, 1);
        var layout = MainDescSetLayout;
        var allocInfo = new DescriptorSetAllocateInfo
        {
            SType = StructureType.DescriptorSetAllocateInfo,
            DescriptorPool = _pool,
            DescriptorSetCount = 1,
            PSetLayouts = &layout,
        };
        ctx.Vk.AllocateDescriptorSets(ctx.Device, in allocInfo, out _set).Check("vkAllocateDescriptorSets (shadow fallback)");

        Span<ImageView> dir = stackalloc ImageView[ShadowSystem.MaxShadowDir];
        Span<ImageView> spot = stackalloc ImageView[ShadowSystem.MaxShadowSpot];
        Span<ImageView> cube = stackalloc ImageView[ShadowSystem.MaxShadowPoint];
        dir.Fill(_image2D.View);
        spot.Fill(_image2D.View);
        cube.Fill(_imageCube.View);
        ShadowSystem.WriteMainSet(ctx, _set, _matrices.Handle, dir, spot, cube, _cubeSampler);
    }

    /// <summary>Called by the renderer while the device is idle (or through the deletion queue).</summary>
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        var deletions = _ctx.Deletions;
        deletions.Enqueue(GpuDeletion.Of(_pool));
        deletions.Enqueue(GpuDeletion.Of(MainDescSetLayout));
        _matrices.Dispose();
        _image2D.Dispose();
        _imageCube.Dispose();
        deletions.Enqueue(GpuDeletion.Of(_comparisonSampler));
        deletions.Enqueue(GpuDeletion.Of(_cubeSampler));
    }
}
