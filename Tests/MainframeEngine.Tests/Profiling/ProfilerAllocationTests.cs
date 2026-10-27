namespace MainframeEngine.Tests.Profiling;

/// <summary>Recording is allocation-free in steady state: scopes on two threads, counters, spikes, the frame end.</summary>
[Collection(nameof(SerialProfiler))]
public sealed class ProfilerAllocationTests : IDisposable
{
    private static readonly ProfilerMarker[] Markers =
        [.. Enumerable.Range(0, 10).Select(i => new ProfilerMarker("Alloc.Marker" + i, ProfilerCategory.Game, ProfilerMarkerKind.Cpu, "", 0))];

    private static readonly ProfilerCounter[] Counters = [.. Enumerable.Range(0, 32).Select(i => new ProfilerCounter("Alloc.Counter" + i))];

    public ProfilerAllocationTests() => Profiler.ResetForTests();

    public void Dispose() => Profiler.ResetForTests();

    [Fact]
    public void RecordingFramesOfScopesOnTwoThreadsWithCountersAndSpikesAllocatesNothing()
    {
        Profiler.SpikeThresholdMs = 0.0001f; // every frame is a spike: the spike slots are copied into all the time
        var token = Profiler.Acquire();
        using var start = new SemaphoreSlim(0);
        using var done = new SemaphoreSlim(0);
        var stop = false;
        var worker = new Thread(() =>
        {
            Profiler.SetThreadName("Worker");
            while (true)
            {
                start.Wait();
                if (Volatile.Read(ref stop))
                    return;
                for (var i = 0; i < 250; i++)
                {
                    using var _ = Markers[i % Markers.Length].Auto();
                    Counters[0].Add(1);
                }

                done.Release();
            }
        }) { IsBackground = true };
        worker.Start();

        void Frames(int count)
        {
            for (var f = 0; f < count; f++)
            {
                Profiler.BeginFrame();
                start.Release();
                for (var i = 0; i < 125; i++)
                {
                    using var outer = Markers[i % Markers.Length].Auto();
                    Markers[(i + 1) % Markers.Length].Begin();
                    Markers[(i + 1) % Markers.Length].End();
                }

                for (var c = 1; c < Counters.Length; c++)
                    Counters[c].Set(f + c);
                done.Wait();
                Profiler.EndFrame(1f, 0f);
            }
        }

        try
        {
            Frames(300); // warm-up: thread buffers, JIT, the history
            var allocated = AllocationGate.SmallestWindow(() => Frames(300));
            Assert.Equal(0, allocated);
            Assert.Equal(600, Profiler.Snapshot.FrameCount);
            Assert.True(Profiler.Snapshot.TryGetNewest(out var frame));
            Assert.Equal(1 + 250 + 250, frame.EventCount); // Frame + 250 main + 250 worker
            Assert.Equal(0, Profiler.DroppedEvents);
        }
        finally
        {
            Volatile.Write(ref stop, true);
            start.Release();
            worker.Join();
            Profiler.Release(token);
        }
    }

    [Fact]
    public void ScopesWhileNotRecordingAllocateNothing()
    {
        var allocated = AllocationGate.SmallestWindow(() =>
        {
            for (var i = 0; i < 10_000; i++)
            {
                using var _ = Markers[i % Markers.Length].Auto();
                Counters[1].Add(1);
            }
        });
        Assert.Equal(0, allocated);
    }
}
