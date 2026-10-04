namespace MainframeEngine;

/// <summary>What a sub-allocation holds, for <c>bufferImageGranularity</c> conflicts.</summary>
public enum GpuResourceKind : byte
{
    /// <summary>Buffers and linear-tiling images.</summary>
    Linear,

    /// <summary>Optimal-tiling images.</summary>
    Optimal,
}

/// <summary>
/// Free-list sub-allocator over one <c>VkDeviceMemory</c> block: a sorted list of regions, best-fit
/// placement, alignment, <c>bufferImageGranularity</c> padding between linear and optimal neighbours, and
/// coalescing of adjacent free regions on free. Pure bookkeeping (no Vulkan), so it is unit-tested directly.
/// </summary>
/// <remarks>
/// Every operation is O(regions). Blocks hold a few hundred resources at most, so this beats a tree in
/// practice and never allocates once the region list has grown to its working size. Not thread-safe.
/// </remarks>
internal sealed class FreeListBlock
{
    private struct Region
    {
        public ulong Offset;
        public ulong Size;
        public bool Free;
        public GpuResourceKind Kind; // meaningful for allocated regions only
    }

    private readonly List<Region> _regions = new(16);

    public FreeListBlock(ulong size, ulong bufferImageGranularity)
    {
        ArgumentOutOfRangeException.ThrowIfZero(size);
        if (bufferImageGranularity == 0 || (bufferImageGranularity & (bufferImageGranularity - 1)) != 0)
            throw new ArgumentException($"Granularity {bufferImageGranularity} is not a power of two.", nameof(bufferImageGranularity));

        Size = size;
        Granularity = bufferImageGranularity;
        _regions.Add(new Region { Offset = 0, Size = size, Free = true });
    }

    public ulong Size { get; }
    public ulong Granularity { get; }

    /// <summary>Bytes in live allocations (alignment and granularity padding stays free).</summary>
    public ulong UsedBytes { get; private set; }

    public int AllocationCount { get; private set; }
    public bool IsEmpty => AllocationCount == 0;

    /// <summary>Number of free regions (1 for an empty block; more means fragmentation).</summary>
    public int FreeRegionCount
    {
        get
        {
            var n = 0;
            foreach (var r in _regions)
                if (r.Free) n++;
            return n;
        }
    }

    /// <summary>Size of the largest free region.</summary>
    public ulong LargestFreeRegion
    {
        get
        {
            ulong largest = 0;
            foreach (var r in _regions)
                if (r.Free && r.Size > largest) largest = r.Size;
            return largest;
        }
    }

    /// <summary>
    /// Places <paramref name="size"/> bytes at an offset aligned to <paramref name="alignment"/> (a power of
    /// two), padded to <see cref="Granularity"/> where a neighbour of the other <see cref="GpuResourceKind"/>
    /// would share a granularity page. Best fit: the free region with the least space left over wins.
    /// </summary>
    public bool TryAllocate(ulong size, ulong alignment, GpuResourceKind kind, out ulong offset)
    {
        ArgumentOutOfRangeException.ThrowIfZero(size);
        if (alignment == 0) alignment = 1;
        if ((alignment & (alignment - 1)) != 0)
            throw new ArgumentException($"Alignment {alignment} is not a power of two.", nameof(alignment));

        offset = 0;
        var bestIndex = -1;
        ulong bestStart = 0, bestLeftover = ulong.MaxValue;

        for (var i = 0; i < _regions.Count; i++)
        {
            var r = _regions[i];
            if (!r.Free || r.Size < size)
                continue;
            if (!TryPlace(i, r, size, alignment, kind, out var start))
                continue;

            var leftover = r.Size - size;
            if (leftover < bestLeftover)
            {
                bestIndex = i;
                bestStart = start;
                bestLeftover = leftover;
                if (leftover == 0) break;
            }
        }

        if (bestIndex < 0)
            return false;

        Split(bestIndex, bestStart, size, kind);
        UsedBytes += size;
        AllocationCount++;
        offset = bestStart;
        return true;
    }

    // Start offset for `size` bytes inside free region `index`, honouring alignment and granularity.
    private bool TryPlace(int index, in Region region, ulong size, ulong alignment, GpuResourceKind kind, out ulong start)
    {
        start = AlignUp(region.Offset, alignment);

        // A free region's neighbours are allocated (free neighbours are always coalesced).
        if (Granularity > 1 && index > 0)
        {
            var prev = _regions[index - 1];
            if (prev.Kind != kind && SamePage(prev.Offset + prev.Size - 1, start, Granularity))
                start = AlignUp(start, Granularity);
        }

        var end = start + size; // exclusive
        if (end < start || end > region.Offset + region.Size)
            return false;

        if (Granularity > 1 && index + 1 < _regions.Count)
        {
            var next = _regions[index + 1];
            if (next.Kind != kind && SamePage(end - 1, next.Offset, Granularity))
                return false;
        }

        return true;
    }

    private void Split(int index, ulong start, ulong size, GpuResourceKind kind)
    {
        var r = _regions[index];
        var end = start + size;
        var regionEnd = r.Offset + r.Size;

        _regions[index] = new Region { Offset = start, Size = size, Free = false, Kind = kind };

        if (end < regionEnd)
            _regions.Insert(index + 1, new Region { Offset = end, Size = regionEnd - end, Free = true });

        if (start > r.Offset)
            _regions.Insert(index, new Region { Offset = r.Offset, Size = start - r.Offset, Free = true });
    }

    /// <summary>Frees the allocation that starts at <paramref name="offset"/> and merges it with free neighbours.</summary>
    public void Free(ulong offset)
    {
        var index = FindRegion(offset);
        if (index < 0 || _regions[index].Free)
            throw new InvalidOperationException($"No live allocation at offset {offset} in this block (double free?).");

        var r = _regions[index];
        UsedBytes -= r.Size;
        AllocationCount--;
        r.Free = true;
        _regions[index] = r;

        // Merge with the next, then the previous free region.
        if (index + 1 < _regions.Count && _regions[index + 1].Free)
        {
            r.Size += _regions[index + 1].Size;
            _regions[index] = r;
            _regions.RemoveAt(index + 1);
        }

        if (index > 0 && _regions[index - 1].Free)
        {
            var prev = _regions[index - 1];
            prev.Size += r.Size;
            _regions[index - 1] = prev;
            _regions.RemoveAt(index);
        }
    }

    // Binary search for the region starting exactly at `offset`.
    private int FindRegion(ulong offset)
    {
        int lo = 0, hi = _regions.Count - 1;
        while (lo <= hi)
        {
            var mid = (lo + hi) >>> 1;
            var o = _regions[mid].Offset;
            if (o == offset) return mid;
            if (o < offset) lo = mid + 1;
            else hi = mid - 1;
        }

        return -1;
    }

    internal static ulong AlignUp(ulong value, ulong alignment) => (value + alignment - 1) & ~(alignment - 1);

    private static bool SamePage(ulong a, ulong b, ulong pageSize) => (a & ~(pageSize - 1)) == (b & ~(pageSize - 1));

    /// <summary>Checks the region list invariants (sorted, contiguous, no adjacent free regions). Test hook.</summary>
    internal void Validate()
    {
        ulong expected = 0, used = 0;
        var allocations = 0;
        for (var i = 0; i < _regions.Count; i++)
        {
            var r = _regions[i];
            if (r.Offset != expected)
                throw new InvalidOperationException($"Region {i} starts at {r.Offset}, expected {expected}.");
            if (r.Size == 0)
                throw new InvalidOperationException($"Region {i} is empty.");
            if (r.Free && i > 0 && _regions[i - 1].Free)
                throw new InvalidOperationException($"Regions {i - 1} and {i} are both free (not coalesced).");
            if (!r.Free)
            {
                used += r.Size;
                allocations++;
            }

            expected += r.Size;
        }

        if (expected != Size)
            throw new InvalidOperationException($"Regions cover {expected} of {Size} bytes.");
        if (used != UsedBytes || allocations != AllocationCount)
            throw new InvalidOperationException("Usage counters disagree with the region list.");
    }
}
