using Silk.NET.Vulkan;
using VkSampler = Silk.NET.Vulkan.Sampler;

namespace MainframeEngine;

/// <summary>
/// "No shadows" stand-in for the shadow set, so lit pipelines (meshes, SpineLit) always declare the same set indices
/// whether or not a <see cref="ShadowSystem"/> exists. Same layout as the real set; every map is a 1×1 placeholder
/// cleared to far depth and the uniforms are all zero, whose shadow codes mean "no shadow" for every light.
/// Static content: one set serves every frame slot. Owned by <see cref="VulkanRenderer.ShadowFallback"/>.
/// </summary>
internal sealed unsafe class ShadowFallback : IShadowDescriptors, IDisposable
{
    private readonly IVulkanContext _ctx;
    private readonly VkSampler _comparisonSampler;
    private readonly ShadowPlaceholderMaps _maps;
    private readonly GpuBuffer _uniforms;
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

    public ShadowFallback(IVulkanContext ctx)
    {
        _ctx = ctx;
        var (format, linear) = ShadowSystem.ChooseDepthFormat(ctx);
        _comparisonSampler = ShadowSystem.CreateComparisonSampler(ctx, linear);
        MainDescSetLayout = ShadowSystem.CreateMainSetLayout(ctx, _comparisonSampler);
        _maps = new ShadowPlaceholderMaps(ctx, format);

        _uniforms = GpuBuffer.Create(ctx, (ulong)ShadowUniforms.Size, BufferUsageFlags.UniformBufferBit, GpuMemoryUsage.Dynamic);
        _uniforms.MappedSpan.Clear(); // every code 0: no light has a shadow

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

        Span<ImageView> cubes = stackalloc ImageView[ShadowSystem.MaxShadowPoint];
        cubes.Fill(_maps.Cube);
        ShadowSystem.WriteMainSet(ctx, _set, _uniforms.Handle, _maps.Array, _maps.Map2D, cubes);
    }

    /// <summary>Called by the renderer while the device is idle (or through the deletion queue).</summary>
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        var deletions = _ctx.Deletions;
        deletions.Enqueue(GpuDeletion.Of(_pool));
        deletions.Enqueue(GpuDeletion.Of(MainDescSetLayout));
        _uniforms.Dispose();
        _maps.Dispose();
        deletions.Enqueue(GpuDeletion.Of(_comparisonSampler));
    }
}
