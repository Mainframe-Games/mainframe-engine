using System.Diagnostics;
using Silk.NET.Vulkan;

namespace MainframeEngine;

/// <summary>
/// In-house device-memory allocator (ADR 0005: no VMA dependency). Sub-allocates buffers and images from
/// 64 MiB <c>VkDeviceMemory</c> blocks per memory type with a best-fit free list (<see cref="FreeListBlock"/>:
/// alignment, <c>bufferImageGranularity</c>, coalescing), and gives requests over 32 MiB their own dedicated
/// allocation. Host-visible blocks are mapped once, persistently; every allocation from them carries its CPU
/// address.
/// </summary>
/// <remarks>
/// <para><b>Threading:</b> not thread-safe. Allocate and free on the render thread (the thread that created
/// the renderer); Debug builds assert it. Loading on worker threads must hand GPU resource creation back to the
/// render thread.</para>
/// <para><b>Lifetime:</b> an allocation still referenced by in-flight frames must not be freed directly; GPU
/// wrappers (<see cref="GpuBuffer"/>, <see cref="GpuImage"/>) free through the <see cref="DeletionQueue"/>.
/// Disposing the allocator frees every block, so it is disposed after the device is idle.</para>
/// <para><b>Stats:</b> <see cref="Totals"/> and <see cref="GetMemoryTypeStats"/> are O(1) counters, cheap
/// enough for a per-frame debug readout.</para>
/// </remarks>
public sealed class GpuAllocator : IDisposable
{
    /// <summary>Size of a sub-allocation block (smaller on heaps under 512 MiB).</summary>
    public const ulong DefaultBlockSize = 64ul << 20;

    /// <summary>Requests larger than this get a dedicated <c>VkDeviceMemory</c>.</summary>
    public const ulong DefaultDedicatedThreshold = 32ul << 20;

    internal sealed class Block(DeviceMemory memory, uint memoryTypeIndex, FreeListBlock freeList, nint mapped)
    {
        public readonly DeviceMemory Memory = memory;
        public readonly uint MemoryTypeIndex = memoryTypeIndex;
        public readonly FreeListBlock FreeList = freeList;
        public readonly nint Mapped = mapped;
    }

    private struct TypeState
    {
        public List<Block> Blocks;
        public int DedicatedCount;
        public int AllocationCount;
        public ulong ReservedBytes;
        public ulong UsedBytes;
    }

    private readonly IGpuMemoryDevice _device;
    private readonly GpuMemoryType[] _types;
    private readonly ulong[] _heapSizes;
    private readonly TypeState[] _state;
    private readonly ulong _granularity;
    private readonly int _ownerThread = Environment.CurrentManagedThreadId;
    private bool _disposed;

    internal GpuAllocator(IGpuMemoryDevice device, GpuMemoryType[] memoryTypes, ulong[] heapSizes,
        ulong bufferImageGranularity, ulong blockSize = DefaultBlockSize, ulong dedicatedThreshold = DefaultDedicatedThreshold)
    {
        ArgumentNullException.ThrowIfNull(device);
        ArgumentNullException.ThrowIfNull(memoryTypes);
        ArgumentNullException.ThrowIfNull(heapSizes);
        ArgumentOutOfRangeException.ThrowIfZero(blockSize);
        _device = device;
        _types = memoryTypes;
        _heapSizes = heapSizes;
        _granularity = Math.Max(1ul, bufferImageGranularity);
        BlockSize = blockSize;
        DedicatedThreshold = Math.Min(dedicatedThreshold, blockSize);
        _state = new TypeState[memoryTypes.Length];
        for (var i = 0; i < _state.Length; i++)
            _state[i].Blocks = [];
    }

    /// <summary>Creates the allocator for a Vulkan device (called by the renderer).</summary>
    internal static unsafe GpuAllocator Create(Vk vk, PhysicalDevice physicalDevice, Device device)
    {
        vk.GetPhysicalDeviceMemoryProperties(physicalDevice, out var props);
        vk.GetPhysicalDeviceProperties(physicalDevice, out var deviceProps);

        var types = new GpuMemoryType[props.MemoryTypeCount];
        for (var i = 0; i < types.Length; i++)
            types[i] = new GpuMemoryType(props.MemoryTypes[i].PropertyFlags, props.MemoryTypes[i].HeapIndex);
        var heaps = new ulong[props.MemoryHeapCount];
        for (var i = 0; i < heaps.Length; i++)
            heaps[i] = props.MemoryHeaps[i].Size;

        return new GpuAllocator(new VulkanMemoryDevice(vk, device), types, heaps,
            deviceProps.Limits.BufferImageGranularity);
    }

    public ulong BlockSize { get; }
    public ulong DedicatedThreshold { get; }
    public ulong BufferImageGranularity => _granularity;
    public int MemoryTypeCount => _types.Length;

    /// <summary>
    /// Allocates memory for <paramref name="requirements"/> (from <c>vkGet{Buffer,Image}MemoryRequirements</c>).
    /// Throws <see cref="VulkanException"/> when no allowed memory type can satisfy it.
    /// </summary>
    public GpuAllocation Allocate(in MemoryRequirements requirements, GpuMemoryUsage usage, GpuResourceKind kind,
        bool dedicated = false)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        CheckThread();
        ArgumentOutOfRangeException.ThrowIfZero(requirements.Size);

        var (required, preferred, avoided) = Flags(usage);
        var tried = 0u; // memory types that already failed
        while (true)
        {
            var type = FindMemoryType(requirements.MemoryTypeBits & ~tried, required, preferred, avoided);
            if (type < 0)
            {
                throw new VulkanException(
                    $"[GpuAllocator] No memory type for {requirements.Size} bytes ({usage}, type bits 0x{requirements.MemoryTypeBits:X}).");
            }

            var mapped = (_types[type].Flags & MemoryPropertyFlags.HostVisibleBit) != 0;
            if (TryAllocateFromType((uint)type, requirements, kind, mapped, dedicated, out var allocation))
                return allocation;

            tried |= 1u << type;
        }
    }

    private bool TryAllocateFromType(uint type, in MemoryRequirements req, GpuResourceKind kind, bool mapped,
        bool dedicated, out GpuAllocation allocation)
    {
        ref var state = ref _state[type];

        if (dedicated || req.Size > DedicatedThreshold)
            return TryAllocateDedicated(type, req.Size, mapped, out allocation);

        foreach (var block in state.Blocks)
        {
            if (block.FreeList.TryAllocate(req.Size, req.Alignment, kind, out var offset))
            {
                allocation = Track(block, offset, req.Size);
                return true;
            }
        }

        // New block: full size, or smaller (down to what this request needs) when the heap is short.
        var heapSize = _heapSizes[_types[type].HeapIndex];
        var size = Math.Min(BlockSize, Math.Max(heapSize / 8, 1ul << 20));
        while (true)
        {
            if (size >= req.Size && _device.TryAllocate(type, size, out var memory))
            {
                var mappedBase = mapped ? _device.Map(memory, size) : 0;
                var block = new Block(memory, type, new FreeListBlock(size, _granularity), mappedBase);
                state.Blocks.Add(block);
                state.ReservedBytes += size;
                if (!block.FreeList.TryAllocate(req.Size, req.Alignment, kind, out var offset))
                    throw new InvalidOperationException("A fresh block could not hold the allocation it was created for.");
                allocation = Track(block, offset, req.Size);
                return true;
            }

            var smaller = size / 2;
            if (smaller < req.Size || smaller < (1ul << 20))
                break;
            size = smaller;
        }

        // Last resort before giving up on this type: a dedicated allocation of exactly the request.
        return TryAllocateDedicated(type, req.Size, mapped, out allocation);
    }

    private bool TryAllocateDedicated(uint type, ulong size, bool mapped, out GpuAllocation allocation)
    {
        if (!_device.TryAllocate(type, size, out var memory))
        {
            allocation = default;
            return false;
        }

        ref var state = ref _state[type];
        state.DedicatedCount++;
        state.AllocationCount++;
        state.ReservedBytes += size;
        state.UsedBytes += size;
        allocation = new GpuAllocation(memory, 0, size, type, mapped ? _device.Map(memory, size) : 0, block: null);
        return true;
    }

    private GpuAllocation Track(Block block, ulong offset, ulong size)
    {
        ref var state = ref _state[block.MemoryTypeIndex];
        state.AllocationCount++;
        state.UsedBytes += size;
        return new GpuAllocation(block.Memory, offset, size, block.MemoryTypeIndex,
            block.Mapped == 0 ? 0 : block.Mapped + (nint)offset, block);
    }

    /// <summary>
    /// Returns an allocation's memory. The GPU must be done with it: free through the
    /// <see cref="DeletionQueue"/> unless the device is idle. Freeing a null allocation does nothing.
    /// </summary>
    public void Free(in GpuAllocation allocation)
    {
        if (allocation.IsNull || _disposed)
            return;
        CheckThread();

        ref var state = ref _state[allocation.MemoryTypeIndex];
        state.AllocationCount--;
        state.UsedBytes -= allocation.Size;

        if (allocation.Block is not { } block)
        {
            state.DedicatedCount--;
            state.ReservedBytes -= allocation.Size;
            _device.Free(allocation.Memory);
            return;
        }

        block.FreeList.Free(allocation.Offset);

        // Keep one empty block per type to avoid allocate/free churn; release the others.
        if (block.FreeList.IsEmpty && CountEmptyBlocks(state.Blocks) > 1)
        {
            state.Blocks.Remove(block);
            state.ReservedBytes -= block.FreeList.Size;
            _device.Free(block.Memory);
        }
    }

    private static int CountEmptyBlocks(List<Block> blocks)
    {
        var n = 0;
        foreach (var b in blocks)
            if (b.FreeList.IsEmpty) n++;
        return n;
    }

    /// <summary>Totals over every memory type.</summary>
    public GpuAllocatorStats Totals
    {
        get
        {
            int blocks = 0, dedicated = 0, allocations = 0;
            ulong reserved = 0, used = 0;
            foreach (var s in _state)
            {
                blocks += s.Blocks.Count;
                dedicated += s.DedicatedCount;
                allocations += s.AllocationCount;
                reserved += s.ReservedBytes;
                used += s.UsedBytes;
            }

            return new GpuAllocatorStats(blocks + dedicated, blocks, dedicated, allocations, reserved, used);
        }
    }

    /// <summary>Usage of one memory type (0 .. <see cref="MemoryTypeCount"/> - 1).</summary>
    public GpuMemoryTypeStats GetMemoryTypeStats(int memoryTypeIndex)
    {
        var s = _state[memoryTypeIndex];
        var t = _types[memoryTypeIndex];
        return new GpuMemoryTypeStats(memoryTypeIndex, t.HeapIndex, t.Flags, s.Blocks.Count, s.DedicatedCount,
            s.AllocationCount, s.ReservedBytes, s.UsedBytes);
    }

    /// <summary>Required, preferred and avoided property flags per usage.</summary>
    internal static (MemoryPropertyFlags Required, MemoryPropertyFlags Preferred, MemoryPropertyFlags Avoided) Flags(GpuMemoryUsage usage)
    {
        const MemoryPropertyFlags hostCoherent = MemoryPropertyFlags.HostVisibleBit | MemoryPropertyFlags.HostCoherentBit;
        return usage switch
        {
            GpuMemoryUsage.DeviceLocal => (0, MemoryPropertyFlags.DeviceLocalBit, MemoryPropertyFlags.HostVisibleBit),
            GpuMemoryUsage.Dynamic => (hostCoherent, MemoryPropertyFlags.DeviceLocalBit, 0),
            GpuMemoryUsage.Staging => (hostCoherent, 0, MemoryPropertyFlags.DeviceLocalBit),
            GpuMemoryUsage.Readback => (hostCoherent, MemoryPropertyFlags.HostCachedBit, 0),
            _ => throw new ArgumentOutOfRangeException(nameof(usage)),
        };
    }

    /// <summary>
    /// Best memory type among <paramref name="typeBits"/> that has every required flag: most preferred flags,
    /// then fewest avoided flags, then lowest index (drivers list faster types first). -1 if none.
    /// </summary>
    internal int FindMemoryType(uint typeBits, MemoryPropertyFlags required, MemoryPropertyFlags preferred,
        MemoryPropertyFlags avoided)
    {
        var best = -1;
        var bestScore = int.MinValue;
        for (var i = 0; i < _types.Length; i++)
        {
            if ((typeBits & (1u << i)) == 0)
                continue;
            var flags = _types[i].Flags;
            if ((flags & required) != required)
                continue;

            var score = BitCount(flags & preferred) * 16 - BitCount(flags & avoided);
            if (score > bestScore)
            {
                best = i;
                bestScore = score;
            }
        }

        return best;
    }

    private static int BitCount(MemoryPropertyFlags flags) => System.Numerics.BitOperations.PopCount((uint)flags);

    [Conditional("DEBUG")]
    private void CheckThread()
    {
        if (Environment.CurrentManagedThreadId != _ownerThread)
            throw new InvalidOperationException("GpuAllocator is not thread-safe: use it from the render thread only.");
    }

    /// <summary>Frees every block and dedicated allocation. The device must be idle.</summary>
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        foreach (ref var s in _state.AsSpan())
        {
            foreach (var block in s.Blocks)
                _device.Free(block.Memory);
            s.Blocks.Clear();
        }
    }

    /// <summary>Dedicated allocations still alive at dispose are leaks; the validation layer reports them.</summary>
    internal int LiveAllocationCount => Totals.AllocationCount;
}

/// <summary><see cref="IGpuMemoryDevice"/> over <c>vkAllocateMemory</c>/<c>vkMapMemory</c>.</summary>
internal sealed unsafe class VulkanMemoryDevice(Vk vk, Device device) : IGpuMemoryDevice
{
    public bool TryAllocate(uint memoryTypeIndex, ulong size, out DeviceMemory memory)
    {
        var info = new MemoryAllocateInfo
        {
            SType = StructureType.MemoryAllocateInfo,
            AllocationSize = size,
            MemoryTypeIndex = memoryTypeIndex,
        };
        var result = vk.AllocateMemory(device, in info, null, out memory);
        if (result is Result.ErrorOutOfDeviceMemory or Result.ErrorOutOfHostMemory or Result.ErrorTooManyObjects)
            return false;
        result.Check("vkAllocateMemory (GpuAllocator)");
        return true;
    }

    public void Free(DeviceMemory memory) => vk.FreeMemory(device, memory, null);

    public nint Map(DeviceMemory memory, ulong size)
    {
        void* ptr;
        vk.MapMemory(device, memory, 0, Vk.WholeSize, 0, &ptr).Check("vkMapMemory (GpuAllocator)");
        return (nint)ptr;
    }
}
