using Silk.NET.Vulkan;

namespace MainframeEngine;

/// <summary>Small facts about the formats the engine uses.</summary>
public static class FormatInfo
{
    /// <summary>Bytes per texel of an uncompressed colour or depth format.</summary>
    public static uint BytesPerPixel(Format format) => format switch
    {
        Format.R8Unorm or Format.R8Srgb or Format.S8Uint => 1,
        Format.R8G8Unorm or Format.R16Sfloat or Format.D16Unorm => 2,
        Format.R8G8B8A8Unorm or Format.R8G8B8A8Srgb or Format.B8G8R8A8Unorm or Format.B8G8R8A8Srgb
            or Format.A2B10G10R10UnormPack32 or Format.R32Sfloat or Format.R32Uint or Format.D32Sfloat
            or Format.D24UnormS8Uint or Format.B10G11R11UfloatPack32 or Format.R16G16Sfloat => 4,
        Format.D32SfloatS8Uint => 8,
        Format.R16G16B16A16Sfloat or Format.R32G32Sfloat => 8,
        Format.R32G32B32A32Sfloat => 16,
        _ => throw new NotSupportedException($"No texel size known for {format}."),
    };

    /// <summary>True for formats the hardware decodes sRGB → linear on sampling and encodes on writes.</summary>
    public static bool IsSrgb(Format format) => format is Format.R8G8B8A8Srgb or Format.B8G8R8A8Srgb or Format.R8Srgb
        or Format.R8G8Srgb or Format.R8G8B8Srgb or Format.B8G8R8Srgb or Format.A8B8G8R8SrgbPack32;

    /// <summary>The UNORM format with the same bit layout as an 8-bit sRGB format (and vice versa).</summary>
    public static Format ToggleSrgb(Format format) => format switch
    {
        Format.R8G8B8A8Srgb => Format.R8G8B8A8Unorm,
        Format.R8G8B8A8Unorm => Format.R8G8B8A8Srgb,
        Format.B8G8R8A8Srgb => Format.B8G8R8A8Unorm,
        Format.B8G8R8A8Unorm => Format.B8G8R8A8Srgb,
        _ => format,
    };

    /// <summary>True for depth (and depth/stencil) formats.</summary>
    public static bool IsDepth(Format format) => format is Format.D16Unorm or Format.D32Sfloat or Format.D16UnormS8Uint
        or Format.D24UnormS8Uint or Format.D32SfloatS8Uint or Format.X8D24UnormPack32;

    /// <summary>True for formats with a stencil aspect.</summary>
    public static bool HasStencil(Format format) => format is Format.S8Uint or Format.D16UnormS8Uint
        or Format.D24UnormS8Uint or Format.D32SfloatS8Uint;

    /// <summary>Aspect for views and sampling: depth for depth formats, stencil for <c>S8_UINT</c>, colour otherwise.</summary>
    public static ImageAspectFlags ViewAspect(Format format) =>
        IsDepth(format) ? ImageAspectFlags.DepthBit : format == Format.S8Uint ? ImageAspectFlags.StencilBit : ImageAspectFlags.ColorBit;

    /// <summary>Every aspect of a depth/stencil attachment (both for packed depth/stencil formats).</summary>
    public static ImageAspectFlags AttachmentAspect(Format format)
    {
        var aspect = ImageAspectFlags.None;
        if (IsDepth(format))
            aspect |= ImageAspectFlags.DepthBit;
        if (HasStencil(format))
            aspect |= ImageAspectFlags.StencilBit;
        return aspect == ImageAspectFlags.None ? ImageAspectFlags.ColorBit : aspect;
    }
}
