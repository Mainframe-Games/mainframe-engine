using Silk.NET.Vulkan;

namespace MainframeEngine.Tests.Rendering.Memory;

public sealed class StagingRingTests
{
    [Fact]
    public void AllocationsAdvanceAndAreAligned()
    {
        var ring = new StagingRing(1024);

        Assert.True(ring.TryAllocate(10, 16, 1, out var a));
        Assert.True(ring.TryAllocate(10, 16, 1, out var b));

        Assert.Equal(0ul, a);
        Assert.Equal(16ul, b);
        Assert.Equal(26ul, ring.UsedBytes); // 10 + 6 padding + 10
    }

    [Fact]
    public void SpaceIsReclaimedWhenTheFrameCompletes()
    {
        var ring = new StagingRing(1000);
        Assert.True(ring.TryAllocate(600, 1, 1, out _));
        Assert.False(ring.TryAllocate(600, 1, 2, out _)); // frame 1 still in flight

        ring.Release(completedFrame: 1);

        Assert.Equal(0ul, ring.UsedBytes);
        Assert.True(ring.TryAllocate(600, 1, 2, out var offset));
        Assert.Equal(0ul, offset); // empty ring restarts at 0
    }

    [Fact]
    public void AllocationsWrapAroundTheEndWhileOlderFramesAreLive()
    {
        var ring = new StagingRing(1000);
        Assert.True(ring.TryAllocate(400, 1, 1, out _)); // [0, 400)    frame 1
        Assert.True(ring.TryAllocate(400, 1, 2, out _)); // [400, 800)  frame 2
        ring.Release(1);                                  // frame 1 done: [0, 400) free

        Assert.True(ring.TryAllocate(300, 1, 3, out var wrapped)); // tail space (200) too small → wraps to 0
        Assert.Equal(0ul, wrapped);
        Assert.False(ring.TryAllocate(200, 1, 3, out _));          // only [300, 400) left before frame 2's data

        ring.Release(2);
        Assert.True(ring.TryAllocate(500, 1, 4, out var next));    // after frame 3's [0, 300)
        Assert.Equal(300ul, next);
    }

    [Fact]
    public void AFullRingFailsUntilReleased()
    {
        var ring = new StagingRing(256);
        Assert.True(ring.TryAllocate(256, 1, 1, out _));
        Assert.Equal(256ul, ring.UsedBytes);
        Assert.False(ring.TryAllocate(1, 1, 1, out _));
        ring.Release(1);
        Assert.True(ring.TryAllocate(1, 1, 2, out _));
    }

    [Fact]
    public void RequestsLargerThanTheRingFail()
    {
        var ring = new StagingRing(256);
        Assert.False(ring.TryAllocate(257, 1, 1, out _));
    }

    [Fact]
    public void FramesMustNotGoBackwards()
    {
        var ring = new StagingRing(256);
        Assert.True(ring.TryAllocate(8, 1, 5, out _));
        Assert.Throws<ArgumentOutOfRangeException>(() => ring.TryAllocate(8, 1, 4, out _));
    }

    [Fact]
    public void RandomisedUseNeverHandsOutOverlappingLiveRanges()
    {
        var random = new Random(42);
        var ring = new StagingRing(4096);
        var live = new List<(ulong Frame, ulong Start, ulong End)>();
        ulong frame = 1;
        for (var step = 0; step < 4000; step++)
        {
            if (random.NextDouble() < 0.2)
            {
                frame++;
                var completed = frame - 2;
                ring.Release(completed);
                live.RemoveAll(e => e.Frame <= completed);
            }

            var size = (ulong)random.Next(1, 1500);
            if (ring.TryAllocate(size, 16, frame, out var offset))
            {
                Assert.Equal(0ul, offset % 16);
                Assert.True(offset + size <= ring.Capacity);
                foreach (var e in live)
                    Assert.True(offset + size <= e.Start || e.End <= offset, $"[{offset},{offset + size}) overlaps live [{e.Start},{e.End})");
                live.Add((frame, offset, offset + size));
            }
        }
    }
}

public sealed class DeletionQueueTests
{
    private sealed class Recorder : IGpuDestroyer
    {
        public readonly List<ulong> Destroyed = [];
        public void Destroy(in GpuDeletion deletion) => Destroyed.Add(deletion.Handle);
    }

    private static GpuDeletion Item(ulong handle) => new(GpuObjectKind.Buffer, handle);

    [Fact]
    public void ObjectsWaitForTheFrameThatMayUseThem()
    {
        var recorder = new Recorder();
        var queue = new DeletionQueue(recorder);

        queue.BeginFrame(1);
        queue.Enqueue(Item(10)); // released while frame 1 records
        queue.EndFrame();
        queue.BeginFrame(2);
        queue.Enqueue(Item(20));

        queue.Collect(completedFrame: 0);
        Assert.Empty(recorder.Destroyed);

        queue.Collect(completedFrame: 1);
        Assert.Equal([10ul], recorder.Destroyed);
        Assert.Equal(1, queue.PendingCount);

        queue.Collect(completedFrame: 2);
        Assert.Equal([10ul, 20ul], recorder.Destroyed);
    }

    [Fact]
    public void ReleasesBetweenFramesWaitForTheNextFrame()
    {
        // The next frame records pending uploads that may still reference the object.
        var recorder = new Recorder();
        var queue = new DeletionQueue(recorder);
        queue.Enqueue(Item(1)); // load time, before any frame

        queue.Collect(completedFrame: 0);
        Assert.Empty(recorder.Destroyed);

        queue.BeginFrame(1);
        queue.EndFrame();
        queue.Enqueue(Item(2)); // after frame 1 was submitted
        queue.Collect(completedFrame: 1);
        Assert.Equal([1ul], recorder.Destroyed);

        queue.Collect(completedFrame: 2);
        Assert.Equal([1ul, 2ul], recorder.Destroyed);
    }

    [Fact]
    public void CompletedFrameNeverGoesBackwards()
    {
        var recorder = new Recorder();
        var queue = new DeletionQueue(recorder);
        queue.BeginFrame(3);
        queue.Collect(3);
        queue.Collect(1);
        Assert.Equal(3ul, queue.CompletedFrame);
        Assert.Throws<ArgumentOutOfRangeException>(() => queue.BeginFrame(2));
    }

    [Fact]
    public void FlushAllDestroysEverythingInOrder()
    {
        var recorder = new Recorder();
        var queue = new DeletionQueue(recorder);
        queue.BeginFrame(1);
        queue.Enqueue(Item(1));
        queue.BeginFrame(2);
        queue.Enqueue(Item(2));

        queue.FlushAll();

        Assert.Equal([1ul, 2ul], recorder.Destroyed);
        Assert.Equal(0, queue.PendingCount);
    }

    [Fact]
    public void NullDeletionsAreIgnored()
    {
        var recorder = new Recorder();
        var queue = new DeletionQueue(recorder);
        queue.Enqueue(GpuDeletion.Of(default(Pipeline)));
        Assert.Equal(0, queue.PendingCount);
    }

    [Fact]
    public void SteadyStateEnqueueAndCollectDoNotAllocate()
    {
        var recorder = new Recorder();
        var queue = new DeletionQueue(recorder);
        for (ulong f = 1; f <= 10; f++) // grow the backing arrays
        {
            queue.BeginFrame(f);
            queue.Enqueue(Item(f));
            queue.Collect(f - 1);
        }

        recorder.Destroyed.Capacity = 1000;
        ulong frame = 11;
        Assert.Equal(0, AllocationGate.SmallestWindow(() =>
        {
            for (var end = frame + 190; frame < end; frame++)
            {
                queue.BeginFrame(frame);
                queue.Enqueue(Item(frame));
                queue.Collect(frame - 1);
            }
        }));
    }
}
