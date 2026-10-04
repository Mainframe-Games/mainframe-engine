using Silk.NET.Vulkan;

namespace MainframeEngine;

/// <summary>How a resource's memory is used; picks the memory type (see <see cref="GpuAllocator"/>).</summary>
public enum GpuMemoryUsage : byte
{
    /// <summary>GPU-only (textures, render targets, static vertex data). Requires DEVICE_LOCAL when the device has it.</summary>
    DeviceLocal,

    /// <summary>
    /// CPU writes every frame, GPU reads (uniform rings, dynamic vertex data). HOST_VISIBLE | HOST_COHERENT,
    /// persistently mapped; DEVICE_LOCAL preferred (unified memory, resizable BAR).
    /// </summary>
    Dynamic,

    /// <summary>CPU writes once, GPU copies (staging). HOST_VISIBLE | HOST_COHERENT, persistently mapped.</summary>
    Staging,

    /// <summary>GPU writes, CPU reads (captures). HOST_VISIBLE | HOST_COHERENT, HOST_CACHED preferred, mapped.</summary>
    Readback,
}

/// <summary>
/// A range of device memory handed out by <see cref="GpuAllocator"/>: a sub-allocation of a 64 MiB block or a
/// dedicated <c>VkDeviceMemory</c>. Bind with <see cref="Memory"/> + <see cref="Offset"/>; free with
/// <see cref="GpuAllocator.Free"/> (or let a <see cref="GpuBuffer"/>/<see cref="GpuImage"/> own it).
/// </summary>
public readonly struct GpuAllocation : IEquatable<GpuAllocation>
{
    internal GpuAllocation(DeviceMemory memory, ulong offset, ulong size, uint memoryTypeIndex, nint mapped,
        GpuAllocator.Block? block)
    {
        Memory = memory;
        Offset = offset;
        Size = size;
        MemoryTypeIndex = memoryTypeIndex;
        MappedPointer = mapped;
        Block = block;
    }

    public DeviceMemory Memory { get; }
    public ulong Offset { get; }
    public ulong Size { get; }
    public uint MemoryTypeIndex { get; }

    /// <summary>CPU address of <see cref="Offset"/> for host-visible memory (persistently mapped), otherwise 0.</summary>
    public nint MappedPointer { get; }

    /// <summary>The owning block, or null for a dedicated allocation.</summary>
    internal GpuAllocator.Block? Block { get; }

    public bool IsNull => Memory.Handle == 0;
    public bool IsMapped => MappedPointer != 0;
    public bool IsDedicated => !IsNull && Block is null;

    /// <summary>The mapped range as a span (host-visible allocations only).</summary>
    public unsafe Span<byte> MappedSpan => IsMapped
        ? new Span<byte>((void*)MappedPointer, checked((int)Size))
        : throw new InvalidOperationException("The allocation is not host-visible.");

    public bool Equals(GpuAllocation other) => Memory.Handle == other.Memory.Handle && Offset == other.Offset;
    public override bool Equals(object? obj) => obj is GpuAllocation other && Equals(other);
    public override int GetHashCode() => HashCode.Combine(Memory.Handle, Offset);
    public static bool operator ==(GpuAllocation left, GpuAllocation right) => left.Equals(right);
    public static bool operator !=(GpuAllocation left, GpuAllocation right) => !left.Equals(right);
}

/// <summary>Usage of one memory type (see <see cref="GpuAllocator.GetMemoryTypeStats"/>).</summary>
public readonly record struct GpuMemoryTypeStats(
    int MemoryTypeIndex,
    uint HeapIndex,
    MemoryPropertyFlags Flags,
    int BlockCount,
    int DedicatedCount,
    int AllocationCount,
    ulong ReservedBytes,
    ulong UsedBytes);

/// <summary>Allocator totals (see <see cref="GpuAllocator.Totals"/>).</summary>
public readonly record struct GpuAllocatorStats(
    int DeviceMemoryCount,
    int BlockCount,
    int DedicatedCount,
    int AllocationCount,
    ulong ReservedBytes,
    ulong UsedBytes);

/// <summary>Device-memory primitives behind <see cref="GpuAllocator"/>; Vulkan in the engine, a fake in tests.</summary>
internal interface IGpuMemoryDevice
{
    /// <summary>Allocates device memory; false when the heap is exhausted (other failures throw).</summary>
    bool TryAllocate(uint memoryTypeIndex, ulong size, out DeviceMemory memory);

    void Free(DeviceMemory memory);

    /// <summary>Maps the whole allocation; stays mapped until <see cref="Free"/>.</summary>
    nint Map(DeviceMemory memory, ulong size);
}

/// <summary>Flags and heap of one memory type.</summary>
internal readonly record struct GpuMemoryType(MemoryPropertyFlags Flags, uint HeapIndex);
