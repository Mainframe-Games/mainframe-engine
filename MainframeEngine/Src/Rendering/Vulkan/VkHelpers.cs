using Silk.NET.Vulkan;

namespace MainframeEngine;

/// <summary>
/// Small shared Vulkan helpers (depth barriers). Buffers and images come from <see cref="GpuBuffer"/> /
/// <see cref="GpuImage"/> and <see cref="GpuAllocator"/>.
/// </summary>
internal static unsafe class VkHelpers
{
    public static ShaderModule CreateShaderModule(IVulkanContext ctx, string spvPath)
    {
        var code = File.ReadAllBytes(ContentPaths.Resolve(spvPath));
        fixed (byte* ptr = code)
        {
            var info = new ShaderModuleCreateInfo
            {
                SType = StructureType.ShaderModuleCreateInfo,
                CodeSize = (nuint)code.Length,
                PCode = (uint*)ptr,
            };
            ctx.Vk.CreateShaderModule(ctx.Device, in info, null, out var module).Check("vkCreateShaderModule");
            return module;
        }
    }

    /// <summary>
    /// Aspects a layout transition must name for a depth <paramref name="format"/>: combined
    /// depth/stencil formats need both (without separateDepthStencilLayouts), depth-only formats just
    /// depth. Image views for sampling still use the depth aspect alone.
    /// </summary>
    public static ImageAspectFlags DepthBarrierAspects(Format format) => format switch
    {
        Format.D32SfloatS8Uint or Format.D24UnormS8Uint or Format.D16UnormS8Uint
            => ImageAspectFlags.DepthBit | ImageAspectFlags.StencilBit,
        _ => ImageAspectFlags.DepthBit,
    };

    /// <summary>Depth(/stencil) layout transition for <paramref name="layers"/> array layers of a <paramref name="format"/> image.</summary>
    public static void DepthBarrier(Vk vk, CommandBuffer cb, Image image, Format format, uint layers,
        ImageLayout oldLayout, ImageLayout newLayout,
        AccessFlags srcAccess, AccessFlags dstAccess, PipelineStageFlags srcStage, PipelineStageFlags dstStage)
    {
        var barrier = new ImageMemoryBarrier
        {
            SType = StructureType.ImageMemoryBarrier,
            SrcAccessMask = srcAccess,
            DstAccessMask = dstAccess,
            OldLayout = oldLayout,
            NewLayout = newLayout,
            SrcQueueFamilyIndex = Vk.QueueFamilyIgnored,
            DstQueueFamilyIndex = Vk.QueueFamilyIgnored,
            Image = image,
            SubresourceRange = new ImageSubresourceRange
            {
                AspectMask = DepthBarrierAspects(format),
                LevelCount = 1,
                LayerCount = layers,
            },
        };
        vk.CmdPipelineBarrier(cb, srcStage, dstStage, 0, 0, null, 0, null, 1, &barrier);
    }
}
