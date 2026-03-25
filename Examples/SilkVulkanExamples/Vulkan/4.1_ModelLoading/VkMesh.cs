using Silk.NET.Vulkan;
using Buffer = Silk.NET.Vulkan.Buffer;

namespace SilkVulkanExamples.Vulkan;

/// <summary>
/// A Vulkan mesh loaded from a model file. Holds vertex buffer + index buffer.
/// </summary>
public unsafe class VkMesh : IDisposable
{
    private readonly ExampleBase _owner;
    public Buffer VertexBuffer { get; }
    public DeviceMemory VertexMemory { get; }
    public Buffer IndexBuffer { get; }
    public DeviceMemory IndexMemory { get; }
    public uint IndexCount { get; }
    public uint VertexCount { get; }

    public VkMesh(ExampleBase owner, float[] vertices, uint[] indices)
    {
        _owner = owner;
        VertexCount = (uint)vertices.Length / 8; // pos(3) + normal(3) + uv(2)
        IndexCount = (uint)indices.Length;

        // Vertex buffer
        ulong vSize = (ulong)(sizeof(float) * vertices.Length);
        owner.CreateBuffer(vSize, BufferUsageFlags.TransferSrcBit,
            MemoryPropertyFlags.HostVisibleBit | MemoryPropertyFlags.HostCoherentBit,
            out var vs, out var vsm);
        void* data;
        owner.Vk.MapMemory(owner.Device, vsm, 0, vSize, 0, &data);
        fixed (float* v = vertices) System.Buffer.MemoryCopy(v, data, vSize, vSize);
        owner.Vk.UnmapMemory(owner.Device, vsm);

        Buffer vb; DeviceMemory vm;
        owner.CreateBuffer(vSize, BufferUsageFlags.TransferDstBit | BufferUsageFlags.VertexBufferBit,
            MemoryPropertyFlags.DeviceLocalBit, out vb, out vm);
        owner.CopyBuffer(vs, vb, vSize);
        owner.Vk.DestroyBuffer(owner.Device, vs, null);
        owner.Vk.FreeMemory(owner.Device, vsm, null);
        VertexBuffer = vb;
        VertexMemory = vm;

        // Index buffer
        ulong iSize = (ulong)(sizeof(uint) * indices.Length);
        owner.CreateBuffer(iSize, BufferUsageFlags.TransferSrcBit,
            MemoryPropertyFlags.HostVisibleBit | MemoryPropertyFlags.HostCoherentBit,
            out var is_, out var ism);
        owner.Vk.MapMemory(owner.Device, ism, 0, iSize, 0, &data);
        fixed (uint* idx = indices) System.Buffer.MemoryCopy(idx, data, iSize, iSize);
        owner.Vk.UnmapMemory(owner.Device, ism);

        Buffer ib; DeviceMemory im;
        owner.CreateBuffer(iSize, BufferUsageFlags.TransferDstBit | BufferUsageFlags.IndexBufferBit,
            MemoryPropertyFlags.DeviceLocalBit, out ib, out im);
        owner.CopyBuffer(is_, ib, iSize);
        owner.Vk.DestroyBuffer(owner.Device, is_, null);
        owner.Vk.FreeMemory(owner.Device, ism, null);
        IndexBuffer = ib;
        IndexMemory = im;
    }

    public void Dispose()
    {
        _owner.Vk.DestroyBuffer(_owner.Device, IndexBuffer, null);
        _owner.Vk.FreeMemory(_owner.Device, IndexMemory, null);
        _owner.Vk.DestroyBuffer(_owner.Device, VertexBuffer, null);
        _owner.Vk.FreeMemory(_owner.Device, VertexMemory, null);
    }
}
