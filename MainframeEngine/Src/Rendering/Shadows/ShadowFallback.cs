using System.Numerics;
using System.Runtime.CompilerServices;
using Silk.NET.Vulkan;
using VkBuffer = Silk.NET.Vulkan.Buffer;
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
    private readonly Image _image2D, _imageCube;
    private readonly DeviceMemory _memory2D, _memoryCube;
    private readonly ImageView _view2D, _viewCube;
    private readonly VkBuffer _matrices;
    private readonly DeviceMemory _matricesMemory;
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
        VkHelpers.CreateImage(ctx, 1, 1, 1, _format, usage, ImageCreateFlags.None, out _image2D, out _memory2D);
        VkHelpers.CreateImage(ctx, 1, 1, 6, _format, usage, ImageCreateFlags.CreateCubeCompatibleBit, out _imageCube, out _memoryCube);
        _view2D = VkHelpers.CreateDepthView(ctx, _image2D, _format, ImageViewType.Type2D, 0, 1);
        _viewCube = VkHelpers.CreateDepthView(ctx, _imageCube, _format, ImageViewType.TypeCube, 0, 6);
        VkHelpers.SubmitAndWait(ctx, this, static (self, cb) =>
        {
            self.ClearToFarDepth(cb, self._image2D, 1);
            self.ClearToFarDepth(cb, self._imageCube, 6);
        });

        var mapped = VkHelpers.CreateMappedBuffer(ctx, ShadowSystem.ShadowMatricesUboSize, BufferUsageFlags.UniformBufferBit,
            out _matrices, out _matricesMemory);
        var matrix = OutOfRangeLightMatrix;
        for (int i = 0; i < ShadowSystem.MaxShadowDir + ShadowSystem.MaxShadowSpot; i++)
            Unsafe.Copy((byte*)mapped + i * sizeof(Matrix4x4), ref matrix);

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
        dir.Fill(_view2D);
        spot.Fill(_view2D);
        cube.Fill(_viewCube);
        ShadowSystem.WriteMainSet(ctx, _set, _matrices, dir, spot, cube, _cubeSampler);
    }

    private void ClearToFarDepth(CommandBuffer cb, Image image, uint layers)
    {
        var vk = _ctx.Vk;
        VkHelpers.DepthBarrier(vk, cb, image, _format, layers, ImageLayout.Undefined, ImageLayout.TransferDstOptimal,
            AccessFlags.None, AccessFlags.TransferWriteBit, PipelineStageFlags.TopOfPipeBit, PipelineStageFlags.TransferBit);

        var clear = new ClearDepthStencilValue { Depth = 1f };
        var range = new ImageSubresourceRange { AspectMask = ImageAspectFlags.DepthBit, LevelCount = 1, LayerCount = layers };
        vk.CmdClearDepthStencilImage(cb, image, ImageLayout.TransferDstOptimal, &clear, 1, &range);

        VkHelpers.DepthBarrier(vk, cb, image, _format, layers, ImageLayout.TransferDstOptimal, ImageLayout.DepthStencilReadOnlyOptimal,
            AccessFlags.TransferWriteBit, AccessFlags.ShaderReadBit, PipelineStageFlags.TransferBit, PipelineStageFlags.FragmentShaderBit);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        var vk = _ctx.Vk;
        var dev = _ctx.Device;
        vk.DestroyDescriptorPool(dev, _pool, null);
        vk.DestroyDescriptorSetLayout(dev, MainDescSetLayout, null);
        VkHelpers.DestroyMappedBuffer(_ctx, _matrices, _matricesMemory);
        vk.DestroyImageView(dev, _view2D, null);
        vk.DestroyImageView(dev, _viewCube, null);
        vk.DestroyImage(dev, _image2D, null);
        vk.DestroyImage(dev, _imageCube, null);
        vk.FreeMemory(dev, _memory2D, null);
        vk.FreeMemory(dev, _memoryCube, null);
        vk.DestroySampler(dev, _comparisonSampler, null);
        vk.DestroySampler(dev, _cubeSampler, null);
    }
}
