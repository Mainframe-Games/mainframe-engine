using Silk.NET.Vulkan;

namespace MainframeEngine;

/// <summary>Creation parameters for a <see cref="GpuImage"/>.</summary>
public readonly record struct GpuImageDesc(uint Width, uint Height, Format Format, ImageUsageFlags Usage)
{
    public uint ArrayLayers { get; init; } = 1;
    public uint MipLevels { get; init; } = 1;

    /// <summary>Depth in texels: more than 1 only for a 3D image (<see cref="ViewType"/> <c>3D</c>, one layer).</summary>
    public uint Depth { get; init; } = 1;

    public ImageCreateFlags Flags { get; init; }

    /// <summary>View type of <see cref="GpuImage.View"/> (2D, 2D array, cube, 3D: a <c>VK_IMAGE_TYPE_3D</c> image).</summary>
    public ImageViewType ViewType { get; init; } = ImageViewType.Type2D;

    /// <summary>
    /// Format of the default view when it differs from the image's (sRGB/UNORM pairs). Needs
    /// <c>MUTABLE_FORMAT</c>, which is then added to <see cref="Flags"/>.
    /// </summary>
    public Format? ViewFormat { get; init; }

    /// <summary>
    /// Aspect of the default view; null uses <see cref="FormatInfo.ViewAspect"/> (depth for depth formats). A
    /// depth/stencil attachment of a packed format needs both aspects (<see cref="FormatInfo.AttachmentAspect"/>).
    /// </summary>
    public ImageAspectFlags? ViewAspect { get; init; }

    /// <summary>Full mip chain length for a <paramref name="width"/>×<paramref name="height"/> image.</summary>
    public static uint FullMipChain(uint width, uint height) =>
        (uint)Math.Floor(Math.Log2(Math.Max(1u, Math.Max(width, height)))) + 1;
}

/// <summary>
/// A <c>VkImage</c> with memory from <see cref="IVulkanContext.Allocator"/> and a default view covering every
/// layer and mip. Extra views (<see cref="CreateView"/>) are owned by the image. <see cref="Dispose"/> hands
/// the image, its views and memory to the <see cref="DeletionQueue"/>.
/// </summary>
public sealed unsafe class GpuImage : IDisposable
{
    private readonly IVulkanContext _ctx;
    private readonly List<ImageView> _extraViews = [];
    private bool _disposed;

    private GpuImage(IVulkanContext ctx, Image handle, in GpuImageDesc desc, GpuAllocation allocation, ImageView view)
    {
        _ctx = ctx;
        Handle = handle;
        Description = desc;
        Allocation = allocation;
        View = view;
    }

    public Image Handle { get; }
    public ImageView View { get; }
    public GpuAllocation Allocation { get; }
    public GpuImageDesc Description { get; }
    public Format Format => Description.Format;
    public uint Width => Description.Width;
    public uint Height => Description.Height;
    public uint ArrayLayers => Description.ArrayLayers;
    public uint MipLevels => Description.MipLevels;

    /// <summary>Depth in texels (1 unless a 3D image).</summary>
    public uint Depth => Description.Depth;
    public Extent2D Extent => new(Width, Height);

    /// <summary>Aspect of the default view (depth for depth formats, colour otherwise).</summary>
    public ImageAspectFlags Aspect => FormatInfo.ViewAspect(Format);

    /// <summary>Creates an image (optimal tiling, exclusive, initial layout UNDEFINED) and its default view.</summary>
    public static GpuImage Create(IVulkanContext ctx, in GpuImageDesc desc, bool dedicated = false)
    {
        ArgumentNullException.ThrowIfNull(ctx);
        ArgumentOutOfRangeException.ThrowIfZero(desc.Width);
        ArgumentOutOfRangeException.ThrowIfZero(desc.Height);
        ArgumentOutOfRangeException.ThrowIfZero(desc.ArrayLayers);
        ArgumentOutOfRangeException.ThrowIfZero(desc.MipLevels);
        ArgumentOutOfRangeException.ThrowIfZero(desc.Depth);
        var volume = desc.ViewType == ImageViewType.Type3D;
        if (volume && desc.ArrayLayers != 1)
            throw new ArgumentException("A 3D image has one layer.", nameof(desc));
        if (!volume && desc.Depth != 1)
            throw new ArgumentException("Only a 3D image has a depth.", nameof(desc));

        var flags = desc.Flags;
        if (desc.ViewFormat is { } viewFormat && viewFormat != desc.Format)
            flags |= ImageCreateFlags.CreateMutableFormatBit;

        var info = new ImageCreateInfo
        {
            SType = StructureType.ImageCreateInfo,
            Flags = flags,
            ImageType = volume ? ImageType.Type3D : ImageType.Type2D,
            Format = desc.Format,
            Extent = new Extent3D(desc.Width, desc.Height, desc.Depth),
            MipLevels = desc.MipLevels,
            ArrayLayers = desc.ArrayLayers,
            Samples = SampleCountFlags.Count1Bit,
            Tiling = ImageTiling.Optimal,
            Usage = desc.Usage,
            SharingMode = SharingMode.Exclusive,
            InitialLayout = ImageLayout.Undefined,
        };

        var vk = ctx.Vk;
        vk.CreateImage(ctx.Device, in info, null, out var image).Check("vkCreateImage");
        GpuAllocation allocation = default;
        try
        {
            vk.GetImageMemoryRequirements(ctx.Device, image, out var req);
            allocation = ctx.Allocator.Allocate(req, GpuMemoryUsage.DeviceLocal, GpuResourceKind.Optimal, dedicated);
            vk.BindImageMemory(ctx.Device, image, allocation.Memory, allocation.Offset).Check("vkBindImageMemory");
            var view = CreateViewHandle(ctx, image, desc.ViewFormat ?? desc.Format, desc.ViewAspect ?? FormatInfo.ViewAspect(desc.Format),
                desc.ViewType, 0, desc.ArrayLayers, 0, desc.MipLevels);
            return new GpuImage(ctx, image, desc with { Flags = flags }, allocation, view);
        }
        catch
        {
            vk.DestroyImage(ctx.Device, image, null);
            ctx.Allocator.Free(allocation);
            throw;
        }
    }

    /// <summary>
    /// An additional view (one cube face, one array layer, another format). Owned by this image and destroyed
    /// with it.
    /// </summary>
    public ImageView CreateView(ImageViewType type, uint baseLayer, uint layerCount, Format? format = null)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        var view = CreateViewHandle(_ctx, Handle, format ?? Format, Aspect, type, baseLayer, layerCount, 0, MipLevels);
        _extraViews.Add(view);
        return view;
    }

    /// <summary>An additional view of some mips of some layers (a framebuffer attachment of one face and mip).</summary>
    public ImageView CreateView(ImageViewType type, uint baseLayer, uint layerCount, uint baseMip, uint mipCount)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        var view = CreateViewHandle(_ctx, Handle, Format, Aspect, type, baseLayer, layerCount, baseMip, mipCount);
        _extraViews.Add(view);
        return view;
    }

    private static ImageView CreateViewHandle(IVulkanContext ctx, Image image, Format format, ImageAspectFlags aspect,
        ImageViewType type, uint baseLayer, uint layerCount, uint baseMip, uint mipCount)
    {
        var info = new ImageViewCreateInfo
        {
            SType = StructureType.ImageViewCreateInfo,
            Image = image,
            ViewType = type,
            Format = format,
            SubresourceRange = new ImageSubresourceRange(aspect, baseMip, mipCount, baseLayer, layerCount),
        };
        ctx.Vk.CreateImageView(ctx.Device, in info, null, out var view).Check("vkCreateImageView");
        return view;
    }

    /// <summary>Destroys the image, its views and memory once no frame in flight can use them.</summary>
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        var deletions = _ctx.Deletions;
        foreach (var view in _extraViews)
            deletions.Enqueue(GpuDeletion.Of(view));
        _extraViews.Clear();
        deletions.Enqueue(GpuDeletion.Of(View));
        deletions.Enqueue(GpuDeletion.Of(Handle, Allocation));
    }
}
