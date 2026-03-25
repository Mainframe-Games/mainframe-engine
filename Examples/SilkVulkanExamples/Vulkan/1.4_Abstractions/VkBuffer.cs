using Silk.NET.Vulkan;
using Buffer = Silk.NET.Vulkan.Buffer;

namespace SilkVulkanExamples.Vulkan;

/// <summary>
/// Wraps a Vulkan vertex/index buffer with staging upload.
/// </summary>
public unsafe class VkBuffer : IDisposable
{
    private readonly ExampleBase _owner;
    private Buffer _buffer;
    private DeviceMemory _memory;

    public Buffer Handle => _buffer;

    public VkBuffer(ExampleBase owner, void* data, ulong size, BufferUsageFlags usage)
    {
        _owner = owner;

        owner.CreateBuffer(size,
            BufferUsageFlags.TransferSrcBit,
            MemoryPropertyFlags.HostVisibleBit | MemoryPropertyFlags.HostCoherentBit,
            out var staging, out var stagingMem);

        void* mapped;
        owner.Vk.MapMemory(owner.Device, stagingMem, 0, size, 0, &mapped);
        System.Buffer.MemoryCopy(data, mapped, size, size);
        owner.Vk.UnmapMemory(owner.Device, stagingMem);

        owner.CreateBuffer(size,
            BufferUsageFlags.TransferDstBit | usage,
            MemoryPropertyFlags.DeviceLocalBit,
            out _buffer, out _memory);

        owner.CopyBuffer(staging, _buffer, size);
        owner.Vk.DestroyBuffer(owner.Device, staging, null);
        owner.Vk.FreeMemory(owner.Device, stagingMem, null);
    }

    public void Dispose()
    {
        _owner.Vk.DestroyBuffer(_owner.Device, _buffer, null);
        _owner.Vk.FreeMemory(_owner.Device, _memory, null);
    }
}
