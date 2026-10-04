using Silk.NET.Vulkan;

namespace MainframeEngine.Tests.Rendering.Memory;

public sealed class GpuAllocatorTests
{
    private const MemoryPropertyFlags DL = MemoryPropertyFlags.DeviceLocalBit;
    private const MemoryPropertyFlags HV = MemoryPropertyFlags.HostVisibleBit;
    private const MemoryPropertyFlags HC = MemoryPropertyFlags.HostCoherentBit;
    private const MemoryPropertyFlags Cached = MemoryPropertyFlags.HostCachedBit;
    private const ulong MiB = 1ul << 20;

    /// <summary>Records device-memory calls; hands out fake handles and fake mapped addresses.</summary>
    private sealed class FakeDevice : IGpuMemoryDevice
    {
        private ulong _next = 1;
        public readonly Dictionary<ulong, (uint Type, ulong Size)> Live = [];
        public readonly HashSet<uint> ExhaustedTypes = [];
        public int AllocateCalls;

        public bool TryAllocate(uint memoryTypeIndex, ulong size, out DeviceMemory memory)
        {
            AllocateCalls++;
            if (ExhaustedTypes.Contains(memoryTypeIndex))
            {
                memory = default;
                return false;
            }

            memory = new DeviceMemory(_next++);
            Live.Add(memory.Handle, (memoryTypeIndex, size));
            return true;
        }

        public void Free(DeviceMemory memory) => Assert.True(Live.Remove(memory.Handle), "freed unknown memory");

        public nint Map(DeviceMemory memory, ulong size) => (nint)(memory.Handle << 32);
    }

    // A discrete-GPU-like table: 0 device-local, 1 host-visible coherent, 2 host-visible cached, 3 ReBAR.
    private static readonly GpuMemoryType[] DiscreteTypes =
    [
        new(DL, 0),
        new(HV | HC, 1),
        new(HV | HC | Cached, 1),
        new(DL | HV | HC, 0),
    ];

    private static readonly ulong[] Heaps = [8192 * MiB, 16384 * MiB];

    private static GpuAllocator Create(FakeDevice device, ulong granularity = 1, GpuMemoryType[]? types = null) =>
        new(device, types ?? DiscreteTypes, Heaps, granularity);

    private static MemoryRequirements Req(ulong size, ulong alignment = 256, uint typeBits = 0xF) =>
        new() { Size = size, Alignment = alignment, MemoryTypeBits = typeBits };

    [Theory]
    [InlineData(GpuMemoryUsage.DeviceLocal, 0)] // pure device-local, not the host-visible ReBAR type
    [InlineData(GpuMemoryUsage.Dynamic, 3)]     // host-visible, device-local preferred
    [InlineData(GpuMemoryUsage.Staging, 1)]     // host-visible, device-local avoided
    [InlineData(GpuMemoryUsage.Readback, 2)]    // host-visible, cached preferred
    public void UsagePicksTheExpectedMemoryType(GpuMemoryUsage usage, uint expectedType)
    {
        using var allocator = Create(new FakeDevice());

        var allocation = allocator.Allocate(Req(1024), usage, GpuResourceKind.Linear);

        Assert.Equal(expectedType, allocation.MemoryTypeIndex);
        Assert.Equal(usage != GpuMemoryUsage.DeviceLocal, allocation.IsMapped);
    }

    [Fact]
    public void ResourceTypeBitsRestrictTheChoice()
    {
        using var allocator = Create(new FakeDevice());

        // Only the ReBAR type is allowed: device-local requests still succeed there.
        var allocation = allocator.Allocate(Req(1024, typeBits: 1u << 3), GpuMemoryUsage.DeviceLocal, GpuResourceKind.Optimal);

        Assert.Equal(3u, allocation.MemoryTypeIndex);
    }

    [Fact]
    public void SmallAllocationsShareOneBlock()
    {
        var device = new FakeDevice();
        using var allocator = Create(device);

        var allocations = Enumerable.Range(0, 100)
            .Select(_ => allocator.Allocate(Req(64 * 1024), GpuMemoryUsage.DeviceLocal, GpuResourceKind.Optimal))
            .ToList();

        Assert.Single(device.Live);
        Assert.Equal(GpuAllocator.DefaultBlockSize, device.Live.Values.Single().Size);
        Assert.All(allocations, a => Assert.Equal(allocations[0].Memory, a.Memory));
        Assert.Equal(100, allocations.Select(a => a.Offset).Distinct().Count());
        var totals = allocator.Totals;
        Assert.Equal(1, totals.BlockCount);
        Assert.Equal(100, totals.AllocationCount);
        Assert.Equal(100ul * 64 * 1024, totals.UsedBytes);
        Assert.Equal(GpuAllocator.DefaultBlockSize, totals.ReservedBytes);
    }

    [Fact]
    public void BlocksAreAddedWhenFull()
    {
        var device = new FakeDevice();
        using var allocator = Create(device);

        for (var i = 0; i < 3; i++)
            allocator.Allocate(Req(30 * MiB), GpuMemoryUsage.DeviceLocal, GpuResourceKind.Optimal);

        Assert.Equal(2, allocator.Totals.BlockCount); // 2 per 64 MiB block
        Assert.Equal(2, device.Live.Count);
    }

    [Fact]
    public void LargeRequestsGetADedicatedAllocationThatIsFreedImmediately()
    {
        var device = new FakeDevice();
        using var allocator = Create(device);

        var large = allocator.Allocate(Req(GpuAllocator.DefaultDedicatedThreshold + 1), GpuMemoryUsage.DeviceLocal, GpuResourceKind.Optimal);

        Assert.True(large.IsDedicated);
        Assert.Equal(0ul, large.Offset);
        Assert.Equal(1, allocator.Totals.DedicatedCount);
        Assert.Equal(0, allocator.Totals.BlockCount);
        Assert.Single(device.Live);

        allocator.Free(large);
        Assert.Empty(device.Live);
        Assert.Equal(0, allocator.Totals.DedicatedCount);
        Assert.Equal(0ul, allocator.Totals.ReservedBytes);
    }

    [Fact]
    public void ExplicitDedicatedRequestsBypassBlocks()
    {
        var device = new FakeDevice();
        using var allocator = Create(device);

        var allocation = allocator.Allocate(Req(4096), GpuMemoryUsage.DeviceLocal, GpuResourceKind.Optimal, dedicated: true);

        Assert.True(allocation.IsDedicated);
        Assert.Equal(4096ul, device.Live.Values.Single().Size);
    }

    [Fact]
    public void MappedAllocationsPointIntoTheirBlocksMapping()
    {
        using var allocator = Create(new FakeDevice());

        var a = allocator.Allocate(Req(1000), GpuMemoryUsage.Dynamic, GpuResourceKind.Linear);
        var b = allocator.Allocate(Req(1000), GpuMemoryUsage.Dynamic, GpuResourceKind.Linear);

        Assert.Equal(a.Memory, b.Memory);
        Assert.Equal((nint)(a.Memory.Handle << 32) + (nint)a.Offset, a.MappedPointer);
        Assert.Equal(b.Offset - a.Offset, (ulong)(b.MappedPointer - a.MappedPointer));
    }

    [Fact]
    public void FreeingEveryAllocationKeepsOneEmptyBlockPerType()
    {
        var device = new FakeDevice();
        using var allocator = Create(device);
        var list = new List<GpuAllocation>();
        for (var i = 0; i < 6; i++)
            list.Add(allocator.Allocate(Req(30 * MiB), GpuMemoryUsage.DeviceLocal, GpuResourceKind.Optimal));
        Assert.Equal(3, device.Live.Count);

        foreach (var a in list)
            allocator.Free(a);

        Assert.Single(device.Live); // one cached empty block
        var stats = allocator.GetMemoryTypeStats(0);
        Assert.Equal(1, stats.BlockCount);
        Assert.Equal(0, stats.AllocationCount);
        Assert.Equal(0ul, stats.UsedBytes);
        Assert.Equal(GpuAllocator.DefaultBlockSize, stats.ReservedBytes);
    }

    [Fact]
    public void AnExhaustedMemoryTypeFallsBackToTheNextBest()
    {
        var device = new FakeDevice();
        device.ExhaustedTypes.Add(3); // ReBAR is full
        using var allocator = Create(device);

        var allocation = allocator.Allocate(Req(1024), GpuMemoryUsage.Dynamic, GpuResourceKind.Linear);

        Assert.NotEqual(3u, allocation.MemoryTypeIndex);
        Assert.True(allocation.IsMapped);
    }

    [Fact]
    public void NoUsableMemoryTypeThrows()
    {
        var device = new FakeDevice();
        using var allocator = Create(device);

        // Host-visible usage restricted to the device-local-only type.
        Assert.Throws<VulkanException>(() => allocator.Allocate(Req(1024, typeBits: 1), GpuMemoryUsage.Staging, GpuResourceKind.Linear));
    }

    [Fact]
    public void BufferImageGranularityIsAppliedInsideBlocks()
    {
        using var allocator = Create(new FakeDevice(), granularity: 4096);

        var buffer = allocator.Allocate(Req(100, alignment: 4), GpuMemoryUsage.DeviceLocal, GpuResourceKind.Linear);
        var image = allocator.Allocate(Req(100, alignment: 4), GpuMemoryUsage.DeviceLocal, GpuResourceKind.Optimal);

        Assert.Equal(buffer.Memory, image.Memory);
        Assert.Equal(4096ul, image.Offset - buffer.Offset);
    }

    [Fact]
    public void StatsTrackEveryTypeAndTotal()
    {
        using var allocator = Create(new FakeDevice());
        allocator.Allocate(Req(1000), GpuMemoryUsage.DeviceLocal, GpuResourceKind.Optimal);
        allocator.Allocate(Req(2000), GpuMemoryUsage.Dynamic, GpuResourceKind.Linear);
        allocator.Allocate(Req(40 * MiB), GpuMemoryUsage.Staging, GpuResourceKind.Linear); // dedicated

        Assert.Equal(1, allocator.GetMemoryTypeStats(0).AllocationCount);
        Assert.Equal(1, allocator.GetMemoryTypeStats(3).AllocationCount);
        Assert.Equal(1, allocator.GetMemoryTypeStats(1).DedicatedCount);
        var totals = allocator.Totals;
        Assert.Equal(3, totals.AllocationCount);
        Assert.Equal(3, totals.DeviceMemoryCount); // two blocks + one dedicated
        Assert.Equal(1000ul + 2000 + 40 * MiB, totals.UsedBytes);
        Assert.Equal(2 * GpuAllocator.DefaultBlockSize + 40 * MiB, totals.ReservedBytes);
    }

    [Fact]
    public void SmallHeapsGetSmallerBlocks()
    {
        var device = new FakeDevice();
        using var allocator = new GpuAllocator(device, [new GpuMemoryType(DL, 0)], [256 * MiB], 1);

        allocator.Allocate(Req(1024, typeBits: 1), GpuMemoryUsage.DeviceLocal, GpuResourceKind.Optimal);

        Assert.Equal(32 * MiB, device.Live.Values.Single().Size); // heap / 8
    }

    [Fact]
    public void DisposeReleasesEveryBlock()
    {
        var device = new FakeDevice();
        var allocator = Create(device);
        allocator.Allocate(Req(1024), GpuMemoryUsage.DeviceLocal, GpuResourceKind.Optimal);
        allocator.Allocate(Req(1024), GpuMemoryUsage.Dynamic, GpuResourceKind.Linear);

        allocator.Dispose();

        Assert.Empty(device.Live);
        Assert.Throws<ObjectDisposedException>(() => allocator.Allocate(Req(1), GpuMemoryUsage.DeviceLocal, GpuResourceKind.Linear));
    }
}
