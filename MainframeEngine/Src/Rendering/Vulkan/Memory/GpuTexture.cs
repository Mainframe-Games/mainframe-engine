using Silk.NET.Vulkan;
using StbImageSharp;

namespace MainframeEngine;

/// <summary>How a texture's 8-bit values are interpreted (see docs/design/color-pipeline.md).</summary>
public enum TextureColorSpace : byte
{
    /// <summary>Colour authored in sRGB (albedo, sky, sprites, UI art): <c>R8G8B8A8_SRGB</c>, decoded to linear on sampling.</summary>
    Srgb,

    /// <summary>Data (normals, masks, coverage, lookup tables): <c>R8G8B8A8_UNORM</c>, sampled as stored.</summary>
    Linear,
}

/// <summary>Sampler parameters for a <see cref="GpuTexture"/>.</summary>
public readonly record struct TextureSampling(Filter Filter, SamplerAddressMode AddressMode)
{
    /// <summary>Anisotropic filtering level (1 = off); clamped to <see cref="IVulkanContext.MaxSamplerAnisotropy"/>.</summary>
    public float MaxAnisotropy { get; init; } = 1f;

    public static TextureSampling LinearClamp => new(Filter.Linear, SamplerAddressMode.ClampToEdge);
    public static TextureSampling LinearRepeat => new(Filter.Linear, SamplerAddressMode.Repeat);
    public static TextureSampling NearestClamp => new(Filter.Nearest, SamplerAddressMode.ClampToEdge);
}

/// <summary>
/// A sampled RGBA8 texture (2D or cube): a <see cref="GpuImage"/>, its sampler, and the upload of its pixels
/// through the <see cref="UploadQueue"/> (optionally with a generated mip chain). Ready to bind as a combined
/// image sampler in <c>SHADER_READ_ONLY_OPTIMAL</c>.
/// </summary>
public sealed unsafe class GpuTexture : IDisposable
{
    private readonly IVulkanContext _ctx;
    private bool _disposed;

    private GpuTexture(IVulkanContext ctx, GpuImage image, Sampler sampler, TextureColorSpace colorSpace)
    {
        _ctx = ctx;
        Image = image;
        Sampler = sampler;
        ColorSpace = colorSpace;
    }

    public GpuImage Image { get; }
    public Sampler Sampler { get; }
    public TextureColorSpace ColorSpace { get; }
    public ImageView View => Image.View;
    public uint Width => Image.Width;
    public uint Height => Image.Height;

    /// <summary>Combined image sampler descriptor info.</summary>
    public DescriptorImageInfo Descriptor => new()
    {
        Sampler = Sampler,
        ImageView = Image.View,
        ImageLayout = ImageLayout.ShaderReadOnlyOptimal,
    };

    /// <summary>The 8-bit RGBA format for a colour space.</summary>
    public static Format FormatFor(TextureColorSpace colorSpace) =>
        colorSpace == TextureColorSpace.Srgb ? Format.R8G8B8A8Srgb : Format.R8G8B8A8Unorm;

    /// <summary>A 2D texture from tightly packed RGBA8 pixels.</summary>
    public static GpuTexture Create2D(IVulkanContext ctx, uint width, uint height, ReadOnlySpan<byte> rgba,
        TextureColorSpace colorSpace, TextureSampling sampling, bool generateMips = false) =>
        Create(ctx, width, height, 1, rgba, colorSpace, sampling, generateMips, ImageViewType.Type2D, ImageCreateFlags.None);

    /// <summary>A cube texture from six tightly packed RGBA8 faces in Vulkan order (+X, -X, +Y, -Y, +Z, -Z).</summary>
    public static GpuTexture CreateCube(IVulkanContext ctx, uint faceSize, ReadOnlySpan<byte> sixFacesRgba,
        TextureColorSpace colorSpace, TextureSampling sampling) =>
        Create(ctx, faceSize, faceSize, 6, sixFacesRgba, colorSpace, sampling, false, ImageViewType.TypeCube,
            ImageCreateFlags.CreateCubeCompatibleBit);

    /// <summary>Decodes an image file (PNG, JPEG, TGA, BMP; path via <see cref="ContentPaths.Resolve(string)"/>) into a 2D texture.</summary>
    public static GpuTexture Load(IVulkanContext ctx, string path, TextureColorSpace colorSpace, TextureSampling sampling,
        bool generateMips = false)
    {
        var image = ImageResult.FromMemory(File.ReadAllBytes(ContentPaths.Resolve(path)), ColorComponents.RedGreenBlueAlpha)
                    ?? throw new InvalidDataException($"Could not decode '{path}'.");
        return Create2D(ctx, (uint)image.Width, (uint)image.Height, image.Data, colorSpace, sampling, generateMips);
    }

    private static GpuTexture Create(IVulkanContext ctx, uint width, uint height, uint layers, ReadOnlySpan<byte> rgba,
        TextureColorSpace colorSpace, TextureSampling sampling, bool generateMips, ImageViewType viewType, ImageCreateFlags flags)
    {
        ArgumentNullException.ThrowIfNull(ctx);
        var format = FormatFor(colorSpace);
        var mips = generateMips && SupportsLinearBlit(ctx, format) ? GpuImageDesc.FullMipChain(width, height) : 1u;
        var usage = ImageUsageFlags.SampledBit | ImageUsageFlags.TransferDstBit;
        if (mips > 1)
            usage |= ImageUsageFlags.TransferSrcBit;

        var image = GpuImage.Create(ctx, new GpuImageDesc(width, height, format, usage)
        {
            ArrayLayers = layers,
            MipLevels = mips,
            Flags = flags,
            ViewType = viewType,
        });

        Sampler sampler;
        try
        {
            ctx.Uploads.UploadImage(image, rgba);
            ctx.Uploads.FlushIfRecording();
            sampler = CreateSampler(ctx, sampling, mips);
        }
        catch
        {
            image.Dispose();
            throw;
        }

        return new GpuTexture(ctx, image, sampler, colorSpace);
    }

    private static bool SupportsLinearBlit(IVulkanContext ctx, Format format)
    {
        ctx.Vk.GetPhysicalDeviceFormatProperties(ctx.PhysicalDevice, format, out var props);
        const FormatFeatureFlags needed = FormatFeatureFlags.BlitSrcBit | FormatFeatureFlags.BlitDstBit |
                                          FormatFeatureFlags.SampledImageFilterLinearBit;
        return (props.OptimalTilingFeatures & needed) == needed;
    }

    internal static Sampler CreateSampler(IVulkanContext ctx, TextureSampling sampling, uint mipLevels)
    {
        // Anisotropy needs the samplerAnisotropy feature (enabled by the renderer when the device has it).
        var anisotropy = Math.Min(sampling.MaxAnisotropy, ctx.MaxSamplerAnisotropy);
        var info = new SamplerCreateInfo
        {
            SType = StructureType.SamplerCreateInfo,
            MagFilter = sampling.Filter,
            MinFilter = sampling.Filter,
            MipmapMode = sampling.Filter == Filter.Linear ? SamplerMipmapMode.Linear : SamplerMipmapMode.Nearest,
            AddressModeU = sampling.AddressMode,
            AddressModeV = sampling.AddressMode,
            AddressModeW = sampling.AddressMode,
            MinLod = 0,
            MaxLod = mipLevels,
            AnisotropyEnable = anisotropy > 1f && sampling.Filter == Filter.Linear,
            MaxAnisotropy = Math.Max(1f, anisotropy),
        };
        ctx.Vk.CreateSampler(ctx.Device, in info, null, out var sampler).Check("vkCreateSampler (texture)");
        return sampler;
    }

    /// <summary>Destroys the texture once no frame in flight can use it.</summary>
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _ctx.Deletions.Enqueue(GpuDeletion.Of(Sampler));
        Image.Dispose();
    }
}
