using Silk.NET.Vulkan;
using VkBuffer = Silk.NET.Vulkan.Buffer;

namespace MainframeEngine;

/// <summary>
/// Small checked wrappers for resource creation shared by the shadow system and its fallback.
/// Load-time only (they allocate device memory per call; the M3 GPU allocator replaces them).
/// </summary>
internal static unsafe class VkHelpers
{
    public static uint FindMemoryType(IVulkanContext ctx, uint typeBits, MemoryPropertyFlags props)
    {
        ctx.Vk.GetPhysicalDeviceMemoryProperties(ctx.PhysicalDevice, out var memProps);
        for (uint i = 0; i < memProps.MemoryTypeCount; i++)
            if ((typeBits & (1u << (int)i)) != 0 &&
                (memProps.MemoryTypes[(int)i].PropertyFlags & props) == props)
                return i;
        throw new VulkanException($"[Vulkan] No memory type with {props}.");
    }

    public static void CreateBuffer(IVulkanContext ctx, ulong size, BufferUsageFlags usage, MemoryPropertyFlags props,
        out VkBuffer buffer, out DeviceMemory memory)
    {
        var vk = ctx.Vk;
        var info = new BufferCreateInfo
        {
            SType = StructureType.BufferCreateInfo,
            Size = size,
            Usage = usage,
            SharingMode = SharingMode.Exclusive,
        };
        vk.CreateBuffer(ctx.Device, in info, null, out buffer).Check("vkCreateBuffer");
        vk.GetBufferMemoryRequirements(ctx.Device, buffer, out var req);
        var alloc = new MemoryAllocateInfo
        {
            SType = StructureType.MemoryAllocateInfo,
            AllocationSize = req.Size,
            MemoryTypeIndex = FindMemoryType(ctx, req.MemoryTypeBits, props),
        };
        vk.AllocateMemory(ctx.Device, in alloc, null, out memory).Check("vkAllocateMemory (buffer)");
        vk.BindBufferMemory(ctx.Device, buffer, memory, 0).Check("vkBindBufferMemory");
    }

    /// <summary>Host-visible, host-coherent buffer that stays mapped for its lifetime.</summary>
    public static nint CreateMappedBuffer(IVulkanContext ctx, ulong size, BufferUsageFlags usage,
        out VkBuffer buffer, out DeviceMemory memory)
    {
        CreateBuffer(ctx, size, usage, MemoryPropertyFlags.HostVisibleBit | MemoryPropertyFlags.HostCoherentBit,
            out buffer, out memory);
        void* ptr;
        ctx.Vk.MapMemory(ctx.Device, memory, 0, size, 0, &ptr).Check("vkMapMemory");
        return (nint)ptr;
    }

    public static void DestroyMappedBuffer(IVulkanContext ctx, VkBuffer buffer, DeviceMemory memory)
    {
        if (buffer.Handle == 0) return;
        ctx.Vk.UnmapMemory(ctx.Device, memory);
        ctx.Vk.DestroyBuffer(ctx.Device, buffer, null);
        ctx.Vk.FreeMemory(ctx.Device, memory, null);
    }

    public static void CreateImage(IVulkanContext ctx, uint width, uint height, uint layers, Format format,
        ImageUsageFlags usage, ImageCreateFlags flags, out Image image, out DeviceMemory memory)
    {
        var vk = ctx.Vk;
        var info = new ImageCreateInfo
        {
            SType = StructureType.ImageCreateInfo,
            Flags = flags,
            ImageType = ImageType.Type2D,
            Format = format,
            Extent = new Extent3D(width, height, 1),
            MipLevels = 1,
            ArrayLayers = layers,
            Samples = SampleCountFlags.Count1Bit,
            Tiling = ImageTiling.Optimal,
            Usage = usage,
            SharingMode = SharingMode.Exclusive,
            InitialLayout = ImageLayout.Undefined,
        };
        vk.CreateImage(ctx.Device, in info, null, out image).Check("vkCreateImage");
        vk.GetImageMemoryRequirements(ctx.Device, image, out var req);
        var alloc = new MemoryAllocateInfo
        {
            SType = StructureType.MemoryAllocateInfo,
            AllocationSize = req.Size,
            MemoryTypeIndex = FindMemoryType(ctx, req.MemoryTypeBits, MemoryPropertyFlags.DeviceLocalBit),
        };
        vk.AllocateMemory(ctx.Device, in alloc, null, out memory).Check("vkAllocateMemory (image)");
        vk.BindImageMemory(ctx.Device, image, memory, 0).Check("vkBindImageMemory");
    }

    public static ImageView CreateDepthView(IVulkanContext ctx, Image image, Format format, ImageViewType type,
        uint baseLayer, uint layerCount)
    {
        var info = new ImageViewCreateInfo
        {
            SType = StructureType.ImageViewCreateInfo,
            Image = image,
            ViewType = type,
            Format = format,
            SubresourceRange = new ImageSubresourceRange
            {
                AspectMask = ImageAspectFlags.DepthBit,
                LevelCount = 1,
                BaseArrayLayer = baseLayer,
                LayerCount = layerCount,
            },
        };
        ctx.Vk.CreateImageView(ctx.Device, in info, null, out var view).Check("vkCreateImageView (depth)");
        return view;
    }

    public static ShaderModule CreateShaderModule(IVulkanContext ctx, string spvPath)
    {
        var code = File.ReadAllBytes(spvPath);
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

    /// <summary>Records <paramref name="record"/> into a one-time command buffer and waits for it (load time only).</summary>
    public static void SubmitAndWait<TState>(IVulkanContext ctx, TState state, Action<TState, CommandBuffer> record)
    {
        ArgumentNullException.ThrowIfNull(record);
        var vk = ctx.Vk;
        var allocInfo = new CommandBufferAllocateInfo
        {
            SType = StructureType.CommandBufferAllocateInfo,
            CommandPool = ctx.CommandPool,
            Level = CommandBufferLevel.Primary,
            CommandBufferCount = 1,
        };
        vk.AllocateCommandBuffers(ctx.Device, in allocInfo, out var cb).Check("vkAllocateCommandBuffers (one-time)");
        try
        {
            var beginInfo = new CommandBufferBeginInfo
            {
                SType = StructureType.CommandBufferBeginInfo,
                Flags = CommandBufferUsageFlags.OneTimeSubmitBit,
            };
            vk.BeginCommandBuffer(cb, in beginInfo).Check("vkBeginCommandBuffer (one-time)");
            record(state, cb);
            vk.EndCommandBuffer(cb).Check("vkEndCommandBuffer (one-time)");

            var submit = new SubmitInfo
            {
                SType = StructureType.SubmitInfo,
                CommandBufferCount = 1,
                PCommandBuffers = &cb,
            };
            vk.QueueSubmit(ctx.GraphicsQueue, 1, in submit, default).Check("vkQueueSubmit (one-time)");
            vk.QueueWaitIdle(ctx.GraphicsQueue).Check("vkQueueWaitIdle (one-time)");
        }
        finally
        {
            vk.FreeCommandBuffers(ctx.Device, ctx.CommandPool, 1, &cb);
        }
    }

    /// <summary>Depth-aspect layout transition for <paramref name="layers"/> array layers.</summary>
    public static void DepthBarrier(Vk vk, CommandBuffer cb, Image image, uint layers,
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
                AspectMask = ImageAspectFlags.DepthBit,
                LevelCount = 1,
                LayerCount = layers,
            },
        };
        vk.CmdPipelineBarrier(cb, srcStage, dstStage, 0, 0, null, 0, null, 1, &barrier);
    }
}
