namespace MainframeEngine.Tests.Rendering.Memory;

public sealed class FreeListBlockTests
{
    [Theory]
    [InlineData(1ul)]
    [InlineData(16ul)]
    [InlineData(256ul)]
    [InlineData(4096ul)]
    public void AllocationsHonourTheRequestedAlignment(ulong alignment)
    {
        var block = new FreeListBlock(1 << 20, 1);

        Assert.True(block.TryAllocate(3, 1, GpuResourceKind.Linear, out _)); // misalign the head
        for (var i = 0; i < 10; i++)
        {
            Assert.True(block.TryAllocate(100, alignment, GpuResourceKind.Linear, out var offset));
            Assert.Equal(0ul, offset % alignment);
        }

        block.Validate();
    }

    [Fact]
    public void AllocationsNeverOverlap()
    {
        var block = new FreeListBlock(4096, 1);
        var ranges = new List<(ulong Start, ulong End)>();
        var sizes = new ulong[] { 100, 7, 512, 33, 1000, 64 };
        foreach (var size in sizes)
        {
            Assert.True(block.TryAllocate(size, 16, GpuResourceKind.Linear, out var offset));
            ranges.Add((offset, offset + size));
        }

        foreach (var a in ranges)
            foreach (var b in ranges)
                if (a != b)
                    Assert.True(a.End <= b.Start || b.End <= a.Start, $"{a} overlaps {b}");
        Assert.Equal(sizes.Aggregate(0ul, (s, x) => s + x), block.UsedBytes);
    }

    [Fact]
    public void LinearAndOptimalNeighboursAreSeparatedByTheGranularity()
    {
        const ulong granularity = 1024;
        var block = new FreeListBlock(1 << 16, granularity);

        Assert.True(block.TryAllocate(100, 4, GpuResourceKind.Linear, out var buffer));
        Assert.True(block.TryAllocate(100, 4, GpuResourceKind.Optimal, out var image));
        Assert.True(block.TryAllocate(100, 4, GpuResourceKind.Optimal, out var image2));
        Assert.True(block.TryAllocate(100, 4, GpuResourceKind.Linear, out var buffer2));

        Assert.Equal(0ul, buffer);
        Assert.Equal(granularity, image);       // pushed to the next page after the buffer
        Assert.Equal(image + 100, image2);       // same kind: packed tightly
        Assert.Equal(100ul, buffer2);            // linear again: reuses the padding hole on the first buffer's page
        block.Validate();
    }

    [Fact]
    public void AnAllocationThatWouldShareAPageWithAConflictingNextNeighbourIsPlacedElsewhere()
    {
        const ulong granularity = 1024;
        var block = new FreeListBlock(8 * granularity, granularity);
        Assert.True(block.TryAllocate(1500, 1, GpuResourceKind.Optimal, out var x)); // [0, 1500)
        Assert.True(block.TryAllocate(500, 1, GpuResourceKind.Optimal, out var y));  // [1500, 2000): page 1
        block.Free(x);                                                               // hole [0, 1500)

        // A 1200-byte buffer in the hole would end in page 1, the image's page: it goes after the image,
        // padded to the next page.
        Assert.True(block.TryAllocate(1200, 1, GpuResourceKind.Linear, out var spilling));
        Assert.Equal(2 * granularity, spilling);

        // A 1000-byte buffer stays inside page 0: the hole is fine.
        Assert.True(block.TryAllocate(1000, 1, GpuResourceKind.Linear, out var fitting));
        Assert.Equal(0ul, fitting);
        Assert.Equal(1500ul, y);
        block.Validate();
    }

    [Fact]
    public void FreeingCoalescesNeighboursBackIntoOneRegion()
    {
        var block = new FreeListBlock(1024, 1);
        Assert.True(block.TryAllocate(256, 1, GpuResourceKind.Linear, out var a));
        Assert.True(block.TryAllocate(256, 1, GpuResourceKind.Linear, out var b));
        Assert.True(block.TryAllocate(256, 1, GpuResourceKind.Linear, out var c));

        block.Free(b);
        Assert.Equal(2, block.FreeRegionCount); // hole + tail
        block.Free(a);
        Assert.Equal(2, block.FreeRegionCount); // a+b merged, tail
        block.Free(c);
        Assert.Equal(1, block.FreeRegionCount); // everything merged
        Assert.Equal(1024ul, block.LargestFreeRegion);
        Assert.True(block.IsEmpty);
        block.Validate();
    }

    [Fact]
    public void FragmentationIsRecoveredOnceTheHolesMerge()
    {
        var block = new FreeListBlock(16 * 64, 1);
        var offsets = new ulong[16];
        for (var i = 0; i < 16; i++)
            Assert.True(block.TryAllocate(64, 1, GpuResourceKind.Linear, out offsets[i]));

        // Free every other allocation: 512 bytes free, but no hole larger than 64.
        for (var i = 0; i < 16; i += 2)
            block.Free(offsets[i]);
        Assert.Equal(512ul, block.Size - block.UsedBytes);
        Assert.False(block.TryAllocate(128, 1, GpuResourceKind.Linear, out _));

        // Free the rest: the holes coalesce and the big request fits.
        for (var i = 1; i < 16; i += 2)
            block.Free(offsets[i]);
        Assert.True(block.TryAllocate(16 * 64, 1, GpuResourceKind.Linear, out var whole));
        Assert.Equal(0ul, whole);
        block.Validate();
    }

    [Fact]
    public void BestFitPrefersTheTightestHole()
    {
        var block = new FreeListBlock(1000, 1);
        Assert.True(block.TryAllocate(300, 1, GpuResourceKind.Linear, out var big));   // [0,300)
        Assert.True(block.TryAllocate(10, 1, GpuResourceKind.Linear, out _));          // [300,310)
        Assert.True(block.TryAllocate(100, 1, GpuResourceKind.Linear, out var small)); // [310,410)
        Assert.True(block.TryAllocate(10, 1, GpuResourceKind.Linear, out _));          // [410,420)
        block.Free(big);   // 300-byte hole
        block.Free(small); // 100-byte hole; tail [420,1000) is 580

        Assert.True(block.TryAllocate(90, 1, GpuResourceKind.Linear, out var placed));
        Assert.Equal(small, placed);
    }

    [Fact]
    public void RequestsLargerThanAnyHoleFail()
    {
        var block = new FreeListBlock(256, 1);
        Assert.False(block.TryAllocate(257, 1, GpuResourceKind.Linear, out _));
        Assert.True(block.TryAllocate(256, 1, GpuResourceKind.Linear, out _));
        Assert.False(block.TryAllocate(1, 1, GpuResourceKind.Linear, out _));
    }

    [Fact]
    public void DoubleFreeAndUnknownOffsetsThrow()
    {
        var block = new FreeListBlock(256, 1);
        Assert.True(block.TryAllocate(64, 1, GpuResourceKind.Linear, out var offset));
        block.Free(offset);
        Assert.Throws<InvalidOperationException>(() => block.Free(offset));
        Assert.Throws<InvalidOperationException>(() => block.Free(17));
    }

    [Fact]
    public void InvalidArgumentsAreRejected()
    {
        Assert.Throws<ArgumentException>(() => new FreeListBlock(256, 3));
        var block = new FreeListBlock(256, 1);
        Assert.Throws<ArgumentException>(() => block.TryAllocate(8, 12, GpuResourceKind.Linear, out _));
        Assert.Throws<ArgumentOutOfRangeException>(() => block.TryAllocate(0, 4, GpuResourceKind.Linear, out _));
    }

    [Fact]
    public void RandomisedAllocateFreeKeepsInvariants()
    {
        var random = new Random(1234);
        var block = new FreeListBlock(1 << 20, 256);
        var live = new List<ulong>();
        for (var step = 0; step < 5000; step++)
        {
            if (live.Count > 0 && random.NextDouble() < 0.45)
            {
                var index = random.Next(live.Count);
                block.Free(live[index]);
                live.RemoveAt(index);
            }
            else
            {
                var size = (ulong)random.Next(1, 8192);
                var alignment = 1ul << random.Next(0, 9);
                var kind = random.Next(2) == 0 ? GpuResourceKind.Linear : GpuResourceKind.Optimal;
                if (block.TryAllocate(size, alignment, kind, out var offset))
                {
                    Assert.Equal(0ul, offset % alignment);
                    live.Add(offset);
                }
            }
        }

        block.Validate();
        foreach (var offset in live)
            block.Free(offset);
        Assert.True(block.IsEmpty);
        Assert.Equal(1, block.FreeRegionCount);
        block.Validate();
    }
}
