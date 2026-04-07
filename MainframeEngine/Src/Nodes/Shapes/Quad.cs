using System.Numerics;
using System.Runtime.CompilerServices;
using Silk.NET.Vulkan;
using VkBuffer = Silk.NET.Vulkan.Buffer;

namespace MainframeEngine;

public class Quad : LitShapeVk, IDisposable
{
    // csharpier-ignore
    private static readonly float[] Vertices =
    [
        //  X      Y     Z     U     V     NX    NY     NZ
         0.5f,  0.5f, 0.0f, 1.0f, 1.0f, 0.0f, 0.0f, -1.0f,
         0.5f, -0.5f, 0.0f, 1.0f, 0.0f, 0.0f, 0.0f, -1.0f,
        -0.5f, -0.5f, 0.0f, 0.0f, 0.0f, 0.0f, 0.0f, -1.0f,
        -0.5f,  0.5f, 0.0f, 0.0f, 1.0f, 0.0f, 0.0f, -1.0f,
    ];

    // csharpier-ignore
    private static readonly uint[] Indices = [0, 1, 3, 1, 2, 3];

    private static readonly VertexInputAttributeDescription[] Attribs =
    [
        new() { Location = 0, Binding = 0, Format = Format.R32G32B32Sfloat, Offset = 0  }, // pos
        new() { Location = 1, Binding = 0, Format = Format.R32G32Sfloat,    Offset = 12 }, // uv
        new() { Location = 2, Binding = 0, Format = Format.R32G32B32Sfloat, Offset = 20 }, // normal
    ];

    private VkBuffer     _vertexBuffer;
    private DeviceMemory _vertexBufferMemory;
    private VkBuffer     _indexBuffer;
    private DeviceMemory _indexBufferMemory;

    public Quad()
    {
        if (Renderer is IVulkanContext vkCtx)
        {
            CreateVertexBuffer(vkCtx);
            CreateIndexBuffer(vkCtx);
            InitLitVulkan(vkCtx,
                new VertexInputBindingDescription
                {
                    Binding   = 0,
                    Stride    = 8 * sizeof(float),
                    InputRate = VertexInputRate.Vertex,
                },
                Attribs,
                "Content/Shaders/Shapes/Shapes.vk.vert.spv",
                "Content/Shaders/Shapes/Shapes.vk.frag.spv",
                CullModeFlags.None);
        }
    }
    
    public override unsafe void Dispose()
    {
        base.Dispose();
        
        if (VkCtx is null) 
            return;
        
        var vk     = VkCtx.Vk;
        var device = VkCtx.Device;
        vk.DestroyBuffer(device, _indexBuffer, null);
        vk.FreeMemory(device, _indexBufferMemory, null);
        vk.DestroyBuffer(device, _vertexBuffer, null);
        vk.FreeMemory(device, _vertexBufferMemory, null);
    }

    protected override unsafe void DrawGeometry(CommandBuffer cb)
    {
        var vb  = _vertexBuffer;
        var off = 0ul;
        VkCtx!.Vk.CmdBindVertexBuffers(cb, 0, 1, &vb, &off);
        VkCtx.Vk.CmdBindIndexBuffer(cb, _indexBuffer, 0, IndexType.Uint32);
        VkCtx.Vk.CmdDrawIndexed(cb, (uint)Indices.Length, 1, 0, 0, 0);
    }

    public override unsafe void DrawShadow2D(CommandBuffer cb)
    {
        if (VkCtx is null || Shadows is null) return;
        var vk     = VkCtx.Vk;
        var pipe   = Shadows.GetShadow2DPipeline(8 * sizeof(float));
        var layout = Shadows.Shadow2DLayout;
        vk.CmdBindPipeline(cb, PipelineBindPoint.Graphics, pipe);
        var vb = _vertexBuffer; var off = 0ul;
        vk.CmdBindVertexBuffers(cb, 0, 1, &vb, &off);
        vk.CmdBindIndexBuffer(cb, _indexBuffer, 0, IndexType.Uint32);
        var model = ModelMatrix;
        vk.CmdPushConstants(cb, layout, ShaderStageFlags.VertexBit, 0, 64, &model);
        vk.CmdDrawIndexed(cb, (uint)Indices.Length, 1, 0, 0, 0);
    }

    public override unsafe void DrawShadowPoint(CommandBuffer cb, Vector3 lightPos, float lightRange)
    {
        if (VkCtx is null || Shadows is null) return;
        var vk     = VkCtx.Vk;
        var pipe   = Shadows.GetShadowPointPipeline(8 * sizeof(float));
        var layout = Shadows.ShadowPointLayout;
        vk.CmdBindPipeline(cb, PipelineBindPoint.Graphics, pipe);
        var vb = _vertexBuffer; var off = 0ul;
        vk.CmdBindVertexBuffers(cb, 0, 1, &vb, &off);
        vk.CmdBindIndexBuffer(cb, _indexBuffer, 0, IndexType.Uint32);
        var pc = stackalloc float[20];
        var model = ModelMatrix;
        Unsafe.Copy(pc, ref model);
        pc[16] = lightPos.X; pc[17] = lightPos.Y; pc[18] = lightPos.Z; pc[19] = lightRange;
        vk.CmdPushConstants(cb, layout, ShaderStageFlags.VertexBit | ShaderStageFlags.FragmentBit, 0, 80, pc);
        vk.CmdDrawIndexed(cb, (uint)Indices.Length, 1, 0, 0, 0);
    }

    private unsafe void CreateVertexBuffer(IVulkanContext ctx)
    {
        var size = (ulong)(Vertices.Length * sizeof(float));

        CreateBuffer(ctx, size,
            BufferUsageFlags.TransferSrcBit,
            MemoryPropertyFlags.HostVisibleBit | MemoryPropertyFlags.HostCoherentBit,
            out var stagingBuffer, out var stagingMemory);

        void* mapped;
        ctx.Vk.MapMemory(ctx.Device, stagingMemory, 0, size, 0, &mapped);
        fixed (float* src = Vertices)
            Unsafe.CopyBlock(mapped, src, (uint)size);
        ctx.Vk.UnmapMemory(ctx.Device, stagingMemory);

        CreateBuffer(ctx, size,
            BufferUsageFlags.TransferDstBit | BufferUsageFlags.VertexBufferBit,
            MemoryPropertyFlags.DeviceLocalBit,
            out _vertexBuffer, out _vertexBufferMemory);

        CopyBuffer(ctx, stagingBuffer, _vertexBuffer, size);
        ctx.Vk.DestroyBuffer(ctx.Device, stagingBuffer, null);
        ctx.Vk.FreeMemory(ctx.Device, stagingMemory, null);
    }

    private unsafe void CreateIndexBuffer(IVulkanContext ctx)
    {
        var size = (ulong)(Indices.Length * sizeof(uint));

        CreateBuffer(ctx, size,
            BufferUsageFlags.TransferSrcBit,
            MemoryPropertyFlags.HostVisibleBit | MemoryPropertyFlags.HostCoherentBit,
            out var stagingBuffer, out var stagingMemory);

        void* mapped;
        ctx.Vk.MapMemory(ctx.Device, stagingMemory, 0, size, 0, &mapped);
        fixed (uint* src = Indices)
            Unsafe.CopyBlock(mapped, src, (uint)size);
        ctx.Vk.UnmapMemory(ctx.Device, stagingMemory);

        CreateBuffer(ctx, size,
            BufferUsageFlags.TransferDstBit | BufferUsageFlags.IndexBufferBit,
            MemoryPropertyFlags.DeviceLocalBit,
            out _indexBuffer, out _indexBufferMemory);

        CopyBuffer(ctx, stagingBuffer, _indexBuffer, size);
        ctx.Vk.DestroyBuffer(ctx.Device, stagingBuffer, null);
        ctx.Vk.FreeMemory(ctx.Device, stagingMemory, null);
    }
}
