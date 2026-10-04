using MainframeEngine.Audio;

namespace MainframeEngine.Tests.Audio;

/// <summary>The lock-free command/event ring: FIFO order, wrap-around, batches, capacity, and a two-thread stress.</summary>
public sealed class SpscRingTests
{
    [Fact]
    public void CapacityRoundsUpToAPowerOfTwo()
    {
        Assert.Equal(8, new SpscRing<int>(5).Capacity);
        Assert.Equal(2, new SpscRing<int>(1).Capacity);
        Assert.Equal(4096, new SpscRing<int>(4096).Capacity);
    }

    [Fact]
    public void ItemsComeOutInOrderAcrossWrapAround()
    {
        var ring = new SpscRing<int>(4);
        var next = 0;
        var expected = 0;
        for (var round = 0; round < 50; round++)
        {
            while (ring.TryEnqueue(next))
                next++;
            Assert.Equal(4, ring.Count);
            Assert.Equal(0, ring.FreeSpace);
            for (var i = 0; i < 3; i++)
            {
                Assert.True(ring.TryDequeue(out var item));
                Assert.Equal(expected++, item);
            }
        }

        while (ring.TryDequeue(out var item))
            Assert.Equal(expected++, item);
        Assert.Equal(next, expected);
        Assert.False(ring.TryDequeue(out _));
    }

    [Fact]
    public void BatchesPublishWhatFitsInOrder()
    {
        var ring = new SpscRing<int>(8);
        Assert.Equal(5, ring.EnqueueBatch([0, 1, 2, 3, 4]));
        Assert.Equal(3, ring.EnqueueBatch([5, 6, 7, 8, 9])); // only 3 slots left
        for (var i = 0; i < 8; i++)
        {
            Assert.True(ring.TryDequeue(out var item));
            Assert.Equal(i, item);
        }

        Assert.Equal(0, ring.EnqueueBatch([]));
    }

    [Fact]
    public void DequeuedSlotsDropTheirReferences()
    {
        var ring = new SpscRing<AudioCommand>(2);
        var weak = RoundTrip(ring);
        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();
        Assert.False(weak.TryGetTarget(out _));
    }

    // Not inlined, so no copy of the command (and its reference) survives on the test's own stack frame.
    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
    private static WeakReference<object> RoundTrip(SpscRing<AudioCommand> ring)
    {
        var weak = Enqueue(ring);
        Assert.True(Dequeue(ring));
        return weak;

        [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
        static WeakReference<object> Enqueue(SpscRing<AudioCommand> ring)
        {
            var payload = new object();
            ring.TryEnqueue(new AudioCommand { Type = AudioCommandType.PlayVoice, Ref = payload });
            return new WeakReference<object>(payload);
        }

        [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
        static bool Dequeue(SpscRing<AudioCommand> ring) => ring.TryDequeue(out _);
    }

    [Fact]
    public void OneProducerAndOneConsumerThreadKeepStrictOrder()
    {
        const int count = 2_000_000;
        var ring = new SpscRing<long>(256);
        var failure = -1L;
        var consumer = new Thread(() =>
        {
            var expected = 0L;
            while (expected < count)
            {
                if (!ring.TryDequeue(out var item))
                {
                    Thread.SpinWait(4);
                    continue;
                }

                if (item != expected)
                {
                    failure = expected;
                    return;
                }

                expected++;
            }
        });
        consumer.Start();

        Span<long> batch = stackalloc long[16];
        var produced = 0L;
        while (produced < count)
        {
            if ((produced & 1) == 0)
            {
                // Mix single enqueues and batches.
                if (ring.TryEnqueue(produced))
                    produced++;
                else
                    Thread.SpinWait(4);
                continue;
            }

            var n = (int)Math.Min(batch.Length, count - produced);
            for (var i = 0; i < n; i++)
                batch[i] = produced + i;
            produced += ring.EnqueueBatch(batch[..n]);
        }

        Assert.True(consumer.Join(TimeSpan.FromSeconds(30)), "consumer did not finish");
        Assert.Equal(-1L, failure);
    }
}
