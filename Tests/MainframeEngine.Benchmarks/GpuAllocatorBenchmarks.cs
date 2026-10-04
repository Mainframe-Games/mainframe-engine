using BenchmarkDotNet.Attributes;
using Silk.NET.Vulkan;

namespace MainframeEngine.Benchmarks;

/// <summary>
/// GPU allocator bookkeeping (no driver calls: a fake device hands out memory handles). Allocation happens at
/// load time and on resize, so these guard against pathological free-list costs rather than per-frame budgets.
/// </summary>
[MemoryDiagnoser]
public class GpuAllocatorBenchmarks : IDisposable
{
    private sealed class FakeDevice : IGpuMemoryDevice
    {
        private ulong _next = 1;
        public bool TryAllocate(uint memoryTypeIndex, ulong size, out DeviceMemory memory)
        {
            memory = new DeviceMemory(_next++);
            return true;
        }

        public void Free(DeviceMemory memory) { }
        public nint Map(DeviceMemory memory, ulong size) => 0x1000;
    }

    private static readonly MemoryRequirements Small = new() { Size = 64 * 1024, Alignment = 256, MemoryTypeBits = 1 };
    private static readonly MemoryRequirements Image = new() { Size = 4 << 20, Alignment = 4096, MemoryTypeBits = 1 };

    private GpuAllocator _allocator = null!;
    private readonly GpuAllocation[] _live = new GpuAllocation[256];
    private int _cursor;

    [GlobalSetup]
    public void Setup()
    {
        _allocator = new GpuAllocator(new FakeDevice(), [new GpuMemoryType(MemoryPropertyFlags.DeviceLocalBit, 0)],
            [8ul << 30], bufferImageGranularity: 1024);

        // A realistic working set: 256 live allocations of mixed kinds spread over a few blocks.
        for (var i = 0; i < _live.Length; i++)
            _live[i] = _allocator.Allocate(i % 4 == 0 ? Image : Small, GpuMemoryUsage.DeviceLocal,
                i % 2 == 0 ? GpuResourceKind.Optimal : GpuResourceKind.Linear);
    }

    [GlobalCleanup]
    public void Dispose() => _allocator.Dispose();

    /// <summary>Allocate and free one buffer next to 256 live allocations.</summary>
    [Benchmark(Baseline = true)]
    public ulong AllocateFreeBuffer()
    {
        var a = _allocator.Allocate(Small, GpuMemoryUsage.DeviceLocal, GpuResourceKind.Linear);
        _allocator.Free(a);
        return a.Offset;
    }

    /// <summary>Replace one live allocation (free + allocate of the other kind): churn with granularity checks.</summary>
    [Benchmark]
    public ulong ChurnMixedKinds()
    {
        var i = _cursor = (_cursor + 37) & (_live.Length - 1);
        _allocator.Free(_live[i]);
        var kind = _live[i].Offset % 2 == 0 ? GpuResourceKind.Linear : GpuResourceKind.Optimal;
        _live[i] = _allocator.Allocate(i % 4 == 0 ? Image : Small, GpuMemoryUsage.DeviceLocal, kind);
        return _live[i].Offset;
    }
}
