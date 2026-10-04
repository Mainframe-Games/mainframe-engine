using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Silk.NET.Vulkan;
using VkBufferHandle = Silk.NET.Vulkan.Buffer;

namespace MainframeEngine;

/// <summary>
/// A <c>VkBuffer</c> with memory from <see cref="IVulkanContext.Allocator"/>. Host-visible buffers
/// (<see cref="GpuMemoryUsage.Dynamic"/>, staging, readback) are persistently mapped: write them with
/// <see cref="Write{T}(in T, ulong)"/>. Device-local buffers are filled through the
/// <see cref="UploadQueue"/> (<see cref="Upload"/>). <see cref="Dispose"/> hands the buffer and its memory to
/// the <see cref="DeletionQueue"/>, so it is safe while frames in flight still read it.
/// </summary>
public sealed unsafe class GpuBuffer : IDisposable
{
    private readonly IVulkanContext _ctx;
    private bool _disposed;

    private GpuBuffer(IVulkanContext ctx, VkBufferHandle handle, ulong size, BufferUsageFlags usage, GpuAllocation allocation)
    {
        _ctx = ctx;
        Handle = handle;
        Size = size;
        Usage = usage;
        Allocation = allocation;
    }

    public VkBufferHandle Handle { get; }
    public ulong Size { get; }
    public BufferUsageFlags Usage { get; }
    public GpuAllocation Allocation { get; }

    /// <summary>True when the CPU can write the buffer directly (host-visible, persistently mapped).</summary>
    public bool IsMapped => Allocation.IsMapped;

    /// <summary>CPU address of the buffer's first byte (mapped buffers only).</summary>
    public nint MappedPointer => IsMapped ? Allocation.MappedPointer : throw NotMapped();

    /// <summary>The whole mapped buffer (mapped buffers only).</summary>
    public Span<byte> MappedSpan => new((void*)MappedPointer, checked((int)Size));

    /// <summary>
    /// Creates a buffer. <see cref="GpuMemoryUsage.DeviceLocal"/> buffers get <c>TRANSFER_DST</c> added so the
    /// upload queue can fill them.
    /// </summary>
    public static GpuBuffer Create(IVulkanContext ctx, ulong size, BufferUsageFlags usage, GpuMemoryUsage memoryUsage)
    {
        ArgumentNullException.ThrowIfNull(ctx);
        ArgumentOutOfRangeException.ThrowIfZero(size);
        if (memoryUsage == GpuMemoryUsage.DeviceLocal)
            usage |= BufferUsageFlags.TransferDstBit;

        var info = new BufferCreateInfo
        {
            SType = StructureType.BufferCreateInfo,
            Size = size,
            Usage = usage,
            SharingMode = SharingMode.Exclusive,
        };
        var vk = ctx.Vk;
        vk.CreateBuffer(ctx.Device, in info, null, out var buffer).Check("vkCreateBuffer");
        GpuAllocation allocation;
        try
        {
            vk.GetBufferMemoryRequirements(ctx.Device, buffer, out var req);
            allocation = ctx.Allocator.Allocate(req, memoryUsage, GpuResourceKind.Linear);
        }
        catch
        {
            vk.DestroyBuffer(ctx.Device, buffer, null);
            throw;
        }

        vk.BindBufferMemory(ctx.Device, buffer, allocation.Memory, allocation.Offset).Check("vkBindBufferMemory");
        return new GpuBuffer(ctx, buffer, size, usage, allocation);
    }

    /// <summary>A device-local buffer filled with <paramref name="data"/> through the upload queue (static meshes).</summary>
    public static GpuBuffer CreateStatic<T>(IVulkanContext ctx, ReadOnlySpan<T> data, BufferUsageFlags usage) where T : unmanaged
    {
        ArgumentNullException.ThrowIfNull(ctx);
        var bytes = MemoryMarshal.AsBytes(data);
        var buffer = Create(ctx, (ulong)bytes.Length, usage, GpuMemoryUsage.DeviceLocal);
        ctx.Uploads.UploadBuffer(buffer.Handle, 0, bytes);
        ctx.Uploads.FlushIfRecording();
        return buffer;
    }

    /// <summary>Writes <paramref name="value"/> at <paramref name="offset"/> (mapped buffers only).</summary>
    public void Write<T>(in T value, ulong offset = 0) where T : unmanaged
    {
        CheckRange(offset, (ulong)sizeof(T));
        Unsafe.WriteUnaligned((void*)(MappedPointer + (nint)offset), value);
    }

    /// <summary>Writes <paramref name="data"/> at <paramref name="offset"/> (mapped buffers only).</summary>
    public void Write<T>(ReadOnlySpan<T> data, ulong offset = 0) where T : unmanaged
    {
        var bytes = MemoryMarshal.AsBytes(data);
        CheckRange(offset, (ulong)bytes.Length);
        bytes.CopyTo(new Span<byte>((void*)(MappedPointer + (nint)offset), bytes.Length));
    }

    /// <summary>
    /// Fills a range: a direct copy for mapped buffers (the caller must not overwrite data a frame in flight
    /// still reads — use per-frame-slot buffers), otherwise through the upload queue (recorded at the start of the
    /// next frame, after earlier frames' reads of the buffer have finished).
    /// </summary>
    public void Upload(ReadOnlySpan<byte> data, ulong offset = 0)
    {
        CheckRange(offset, (ulong)data.Length);
        if (IsMapped)
        {
            data.CopyTo(new Span<byte>((void*)(Allocation.MappedPointer + (nint)offset), data.Length));
            return;
        }

        _ctx.Uploads.UploadBuffer(Handle, offset, data);
        _ctx.Uploads.FlushIfRecording();
    }

    /// <summary>Descriptor info for a uniform/storage binding of <paramref name="range"/> bytes at <paramref name="offset"/>.</summary>
    public DescriptorBufferInfo Descriptor(ulong offset = 0, ulong range = Vk.WholeSize) =>
        new() { Buffer = Handle, Offset = offset, Range = range };

    private void CheckRange(ulong offset, ulong length)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (offset > Size || length > Size - offset)
            throw new ArgumentOutOfRangeException(nameof(offset), $"Range {offset}+{length} exceeds the {Size}-byte buffer.");
    }

    private static InvalidOperationException NotMapped() =>
        new("The buffer is device-local; use Upload (UploadQueue) instead of mapped writes.");

    /// <summary>Destroys the buffer once no frame in flight can use it.</summary>
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _ctx.Deletions.Enqueue(GpuDeletion.Of(Handle, Allocation));
    }
}
